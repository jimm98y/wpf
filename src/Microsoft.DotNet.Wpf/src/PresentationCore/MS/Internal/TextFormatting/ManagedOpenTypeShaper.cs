// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// OpenType shaping off Windows: GSUB substitution and GPOS positioning driven from WPF's own
// managed layout engine (MS.Internal.Shaping.OpenTypeLayout).
//
// On Windows WPF shapes through DWrite, which picks the script, enables the features that script
// needs, and applies both tables. Off Windows the managed DirectWriteForwarder stub produces only
// NOMINAL cmap glyphs -- one glyph per codepoint, hmtx advances, no offsets -- so everything DWrite
// did has to happen here instead. This file is that layer, and it plugs into the two LineServices
// callbacks that own the glyph buffers: GetGlyphs (substitution) and GetGlyphPositions (placement).
//
// What it adds over a nominal run:
//
//   * the SCRIPT. The engine looks features up under a script tag, so text has to be classified
//     ('arab', 'hebr', 'deva', ...) before any of a font's Arabic or Indic lookups are reachable
//     at all. Asking for 'latn' -- which is what this used to do -- finds nothing in an Arabic
//     font, which is why Arabic came out as disconnected isolated letters.
//   * the POSITIONAL FORMS. Arabic and Syriac letters change shape with their neighbours, and the
//     font expresses that as four features (init/medi/fina/isol) applied to INDIVIDUAL characters.
//     Deciding which one each character gets is the joining algorithm below; it is the difference
//     between cursive Arabic and a row of unconnected letters.
//   * GPOS. Marks (Arabic harakat, Hebrew niqqud, Indic and Thai vowel signs) carry no advance and
//     are positioned by attachment to the base glyph. Without GPOS every mark in a run piles up at
//     the origin of its own cell. GPOS is also where most modern fonts keep their kerning.
//
// What it deliberately does NOT do: Indic reordering. Devanagari and its relatives need syllables
// segmented and pre-base matras and reph MOVED before the features are applied (the USE/Indic
// shaper); applying the feature set alone gets conjuncts and nukta forms but leaves a pre-base
// matra sitting after its consonant. That is its own project -- see the note on Indic below.
//

using MS.Internal.FontCache;
using MS.Internal.Shaping;
using MS.Internal.Text.TextInterface;
using System;
using System.Globalization;
using System.Windows.Media;

namespace MS.Internal.TextFormatting
{
    internal static class ManagedOpenTypeShaper
    {
        // WPF_SHAPE_LOG=1 traces the plan and what each pass did. The first question when text looks
        // wrong is always "which script did it pick, and did the font have that script at all".
        private static readonly bool s_log =
            Environment.GetEnvironmentVariable("WPF_SHAPE_LOG") == "1";

        /// <summary>Packs a four-character OpenType tag. The shared <see cref="OpenTypeTags"/> enum
        /// carries only the handful of tags WPF's old shaper used and none of the script tags, and
        /// this file needs about forty; spelling them as text keeps them checkable against the
        /// OpenType registry by eye.</summary>
        private static uint Tag(string tag)
            => ((uint)tag[0] << 24) | ((uint)tag[1] << 16) | ((uint)tag[2] << 8) | tag[3];

        // OpenType's default SCRIPT tag is "DFLT" in capitals; "dflt" in lower case is the default
        // LANGSYS tag. They are not interchangeable, and asking for script "dflt" matches nothing --
        // FindScript compares the tag exactly. The previous fallback here asked for the lower-case
        // one, so it never found anything and was a no-op on every font.
        private static readonly uint DfltScript = Tag("DFLT");
        private static readonly uint DfltLangSys = Tag("dflt");

        #region Public entry points

        /// <summary>
        ///  Applies the font's GSUB features to a nominally-mapped run, in place.
        /// </summary>
        /// <remarks>
        ///  <para>
        ///   Follows the LineServices GetGlyphs buffer contract. <paramref name="glyphs"/> and
        ///   <paramref name="clusterMap"/> hold the nominal run on entry; on return they hold the
        ///   substituted one and the method returns its glyph count.
        ///  </para>
        ///  <para>
        ///   Substitution can GROW a run -- a one-to-many lookup (GSUB type 2, and the ccmp
        ///   decompositions Arabic and Indic fonts lean on) turns one glyph into several. When the
        ///   result does not fit in <paramref name="glyphCapacity"/> the buffers are left EXACTLY as
        ///   they were and the required count is returned, which is the caller's signal to reallocate
        ///   and ask again. Writing a partial result was the old behaviour and it was silently fatal:
        ///   the truncated glyph array no longer matched the cluster map written beside it, and the
        ///   inconsistent run was dropped on the floor further down. Every Arabic string rendered as
        ///   nothing at all in any font whose ccmp grew the run -- including Noto Nastaliq Urdu, which
        ///   is exactly what the fallback chain picks for Arabic on a machine with no Windows fonts.
        ///  </para>
        /// </remarks>
        /// <returns>
        ///  The glyph count the substituted run needs. When it is &lt;= <paramref name="glyphCapacity"/>
        ///  the buffers hold that run; when it is greater the buffers are untouched.
        /// </returns>
        internal static unsafe int Substitute(
            GlyphTypeface glyphTypeface,
            char*         text,
            int           charCount,
            ushort*       glyphs,          // in/out: glyph indices
            int           glyphCount,
            int           glyphCapacity,   // how many glyphs the buffers can hold
            ushort*       clusterMap,      // in/out: char -> glyph index
            int*          canGlyphAlone,   // in/out (may be null): per-char independence flag
            uint*         glyphProps = null // out (may be null): each glyph's source character, see GlyphSourceShift
            )
        {
            // No direction parameter: GSUB lookups are defined over glyphs in LOGICAL order for
            // every script, and the one direction-sensitive lookup type (8, reverse chaining) is
            // recognised and walked backwards by the engine itself. Direction matters to GPOS, which
            // is why Position takes it and this does not.
            if (glyphTypeface == null || charCount <= 0 || glyphCount <= 0)
            {
                return glyphCount;
            }

            try
            {
                if (TryGetCachedSubstitution(glyphTypeface, text, charCount, glyphs, glyphCapacity,
                                             clusterMap, canGlyphAlone, glyphProps, out int cached))
                {
                    return cached;
                }

                ushort[] sources = new ushort[Math.Max(glyphCapacity, glyphCount)];
                int produced = SubstituteCore(glyphTypeface, text, charCount, glyphs, glyphCount,
                                              glyphCapacity, clusterMap, canGlyphAlone, sources);
                WriteGlyphSources(glyphProps, sources, produced, glyphCapacity);
                CacheSubstitution(glyphTypeface, text, charCount, glyphs, produced, glyphCapacity,
                                  clusterMap, canGlyphAlone, sources);
                return produced;
            }
            catch (Exception e)
            {
                // A font whose tables this engine cannot digest must cost its own shaping and
                // nothing else. Letting it out aborts the whole GetGlyphs callback, and LS answers
                // that by dropping the run -- text vanishes rather than merely being unshaped.
                if (s_log) Log($"GSUB face='{FamilyOf(glyphTypeface)}' THREW {e}");
                return glyphCount;
            }
        }

        // A glyph's source character -- which character GSUB made it from -- rides from the
        // substitution pass to the positioning pass in the HIGH half of its glyph properties, which
        // nothing else in this backend reads (the forwarder stub writes the low half, zero). The
        // cluster map alone cannot say it: a mark decomposed out of a character that then joined a
        // ligature (Microsoft Uighur's lam + alef-with-hamza: lam-alef and a hamza) belongs to a
        // cluster it does not start, and mark-to-ligature needs its character to find its component.
        // DirectWrite keeps the same thing in its shaping glyph properties between GetGlyphs and
        // GetGlyphPlacements.
        private const int GlyphSourceShift = 16;

        private static unsafe void WriteGlyphSources(uint* glyphProps, ushort[] sources, int glyphCount, int glyphCapacity)
        {
            if (glyphProps == null || sources == null || glyphCount > glyphCapacity) return;
            for (int g = 0; g < glyphCount && g < sources.Length; g++)
            {
                glyphProps[g] = (glyphProps[g] & 0xFFFF) | ((uint)sources[g] << GlyphSourceShift);
            }
        }

        // ---- shaped-run cache ---------------------------------------------------------------
        //
        // Substitution is a PURE function of (face, characters): the same string through the same font
        // always yields the same glyphs, cluster map and independence flags. It is also expensive —
        // every call walks the font's GSUB tables lookup by lookup, reading them two bytes at a time
        // (FontTable.GetUShort), because this port shapes in managed code where Windows would call
        // DirectWrite.
        //
        // That cost is paid over and over for identical text. WPF re-formats a line whenever its
        // visual is re-rendered, and anything that animates puts the whole scene through render and
        // commit EVERY FRAME — so an app with one spinner re-shapes all its visible labels 60 times a
        // second. In WpfHexEditorIDE this was the single largest remaining cost once the GC problem
        // was fixed, with shaping frames dominating an at-rest profile.
        //
        // Keyed on the typeface instance and the run's characters. Bounded and cleared wholesale when
        // full: text runs are highly repetitive, so a plain cap keeps the common case hot without the
        // bookkeeping of an eviction policy.
        private const int ShapeCacheLimit = 4096;

        private sealed class ShapedRun
        {
            public ushort[] Glyphs;
            public ushort[] ClusterMap;
            public int[] CanGlyphAlone;   // null when the caller did not ask for it
            public ushort[] Sources;      // each glyph's source character + 1, 0 when unknown
            public int GlyphCount;
        }

        // Runs longer than this are not cached: long runs repeat far less often, cost more to copy in
        // and out, and are what a text-heavy view (an editor's lines, a log panel) produces endlessly.
        private const int ShapeCacheMaxRunLength = 128;

        // ThreadStatic rather than locked: text formatting is bound to the thread that owns the
        // Dispatcher, and a per-thread cache needs no synchronisation on this hot path.
        //
        // Nested by typeface so the inner dictionary is keyed by STRING ALONE, which lets a lookup use
        // the span alternate lookup and touch no allocation at all. The first version keyed a single
        // dictionary on (typeface, string) and built that string on EVERY call -- including hits -- so
        // consulting the cache allocated a string per shaped run, at shaping rates. Caching to avoid
        // work while allocating to ask the cache is self-defeating: it trades processor time for
        // garbage, and in a wasm runtime whose collector is not cheap that is a poor trade.
        [ThreadStatic] private static Dictionary<GlyphTypeface, Dictionary<string, ShapedRun>> s_shapeCache;

        private static unsafe bool TryGetCachedSubstitution(
            GlyphTypeface glyphTypeface, char* text, int charCount,
            ushort* glyphs, int glyphCapacity, ushort* clusterMap, int* canGlyphAlone, uint* glyphProps,
            out int glyphCountOut)
        {
            glyphCountOut = 0;
            if (charCount > ShapeCacheMaxRunLength) return false;

            Dictionary<GlyphTypeface, Dictionary<string, ShapedRun>> cache = s_shapeCache;
            if (cache == null || !cache.TryGetValue(glyphTypeface, out Dictionary<string, ShapedRun> forFace))
            {
                return false;
            }

            // Span lookup: finds the entry without materialising the key.
            if (!forFace.GetAlternateLookup<ReadOnlySpan<char>>()
                        .TryGetValue(new ReadOnlySpan<char>(text, charCount), out ShapedRun run))
            {
                return false;
            }

            // The caller wants independence flags this entry was not asked to record: recompute.
            if (canGlyphAlone != null && run.CanGlyphAlone == null) return false;

            // Does not fit the caller's buffers: report the size so it can grow and come back, exactly
            // as the uncached path does, and leave the buffers untouched.
            if (run.GlyphCount > glyphCapacity)
            {
                glyphCountOut = run.GlyphCount;
                return true;
            }

            for (int g = 0; g < run.GlyphCount; g++) glyphs[g] = run.Glyphs[g];
            for (int c = 0; c < charCount; c++) clusterMap[c] = run.ClusterMap[c];
            if (canGlyphAlone != null)
            {
                for (int c = 0; c < charCount; c++) canGlyphAlone[c] = run.CanGlyphAlone[c];
            }
            WriteGlyphSources(glyphProps, run.Sources, run.GlyphCount, glyphCapacity);

            glyphCountOut = run.GlyphCount;
            return true;
        }

        private static unsafe void CacheSubstitution(
            GlyphTypeface glyphTypeface, char* text, int charCount,
            ushort* glyphs, int glyphCount, int glyphCapacity, ushort* clusterMap, int* canGlyphAlone,
            ushort[] sources)
        {
            // Only in-place results are cacheable: when the run did not fit, the buffers still hold
            // the caller's input and there is nothing to remember.
            if (glyphCount <= 0 || glyphCount > glyphCapacity) return;
            if (charCount > ShapeCacheMaxRunLength) return;

            Dictionary<GlyphTypeface, Dictionary<string, ShapedRun>> cache = s_shapeCache ??= new();
            if (!cache.TryGetValue(glyphTypeface, out Dictionary<string, ShapedRun> forFace))
            {
                forFace = new Dictionary<string, ShapedRun>(StringComparer.Ordinal);
                cache[glyphTypeface] = forFace;
            }

            if (forFace.Count >= ShapeCacheLimit) forFace.Clear();

            var run = new ShapedRun
            {
                Glyphs = new ushort[glyphCount],
                ClusterMap = new ushort[charCount],
                Sources = sources[..glyphCount],
                GlyphCount = glyphCount,
            };
            for (int g = 0; g < glyphCount; g++) run.Glyphs[g] = glyphs[g];
            for (int c = 0; c < charCount; c++) run.ClusterMap[c] = clusterMap[c];
            if (canGlyphAlone != null)
            {
                run.CanGlyphAlone = new int[charCount];
                for (int c = 0; c < charCount; c++) run.CanGlyphAlone[c] = canGlyphAlone[c];
            }

            forFace[new string(text, 0, charCount)] = run;
        }

        /// <summary>
        ///  The mark order DirectWrite's Hebrew engine shapes in (TextShaping.dll,
        ///  ShapingLibraryInternal::NeedsReorder / VerifyCharacterOrder): each run of marks after a
        ///  base is stably sorted by <see cref="HebrewReorderClass"/>, so a shin dot or a dagesh typed
        ///  after the vowel still reaches the base first and the face's ccmp ligature (shin + shin dot)
        ///  applies. Marks of one class keep their order -- a meteg stays on whichever side of the
        ///  qamats it was typed. The reordered cluster becomes one: every character in it maps to the
        ///  base glyph, as DirectWrite's cluster map has it.
        /// </summary>
        private static unsafe void ReorderHebrewMarks(char* text, int charCount, ushort* glyphs, int glyphCount, ushort* clusterMap)
        {
            if (charCount != glyphCount)
            {
                return;   // nominal runs are one glyph per character; Hebrew has no surrogates
            }

            int c = 0;
            while (c < charCount)
            {
                if (HebrewReorderClass(text[c]) is 0 or 10 or 11)
                {
                    c++;
                    continue;
                }
                int start = c;
                bool sorted = true;
                int previous = 0;
                while (c < charCount && HebrewReorderClass(text[c]) is int k && k is not (0 or 10 or 11))
                {
                    if (k < previous) sorted = false;
                    previous = k;
                    c++;
                }
                if (sorted || start == 0)
                {
                    continue;
                }

                // Stable insertion sort of glyphs [start, c) by their characters' classes. The text
                // itself is the caller's and stays as it is.
                Span<int> classes = stackalloc int[c - start];
                for (int i = start; i < c; i++)
                {
                    classes[i - start] = HebrewReorderClass(text[i]);
                }
                for (int i = 1; i < classes.Length; i++)
                {
                    int k = classes[i];
                    ushort gl = glyphs[start + i];
                    int j = i - 1;
                    while (j >= 0 && classes[j] > k)
                    {
                        classes[j + 1] = classes[j];
                        glyphs[start + j + 1] = glyphs[start + j];
                        j--;
                    }
                    classes[j + 1] = k;
                    glyphs[start + j + 1] = gl;
                }
                for (int i = start; i < c; i++)
                {
                    clusterMap[i] = clusterMap[start - 1];
                }
            }
        }

        /// <summary>TextShaping.dll's GetReorderClass: the high nibble of byHebrewCharAttribute for
        /// U+0590..05FF, and a few marks outside the block. 0 is a base, 10 (CGJ) and 11 (ZWJ,
        /// ZWNJ) end a mark run.</summary>
        private static int HebrewReorderClass(char ch)
        {
            if (ch >= 0x0590 && ch < 0x0600)
            {
                return ch - 0x0590 < s_hebrewReorderClass.Length ? s_hebrewReorderClass[ch - 0x0590] : 0;
            }
            return ch switch
            {
                '\u0307' or '\u0308' => 9,
                '\u034F' => 10,
                '\u0323' => 4,
                '\u200C' or '\u200D' => 11,
                '\uFB1E' => 2,
                _ => 0,
            };
        }

        private static readonly byte[] s_hebrewReorderClass =
        {
            // 0590
            0, 4, 6, 6, 6, 6, 4, 6, 6, 6, 5, 4, 6, 6, 6, 6,
            // 05A0
            6, 6, 4, 4, 4, 4, 4, 4, 6, 6, 4, 6, 6, 5, 7, 8,
            // 05B0: sheva..qamats 4, holam 3, dagesh 2, meteg 4, rafe 2, shin/sin dot 1
            4, 4, 4, 4, 4, 4, 4, 4, 4, 3, 3, 4, 2, 4, 0, 2,
            // 05C0
            0, 1, 1, 0, 6, 4, 0, 4,
        };

        private static unsafe int SubstituteCore(
            GlyphTypeface glyphTypeface,
            char*         text,
            int           charCount,
            ushort*       glyphs,
            int           glyphCount,
            int           glyphCapacity,
            ushort*       clusterMap,
            int*          canGlyphAlone,
            ushort[]      sources          // out: each glyph's source character + 1 (0: unknown)
            )
        {
            ReorderHebrewMarks(text, charCount, glyphs, glyphCount, clusterMap);

            FontFaceLayoutInfo layout = glyphTypeface.FontFaceLayoutInfo;
            if (layout == null || layout.Gsub() == null)
            {
                return glyphCount;   // no GSUB table -> nothing to substitute
            }

            ShapingPlan plan = ShapingPlan.For(text, charCount);
            Feature[] features = plan.BuildSubstitutionFeatures(text, charCount);
            if (features.Length == 0)
            {
                return glyphCount;
            }

            IOpenTypeFont font = new GsubGposTables(layout);
            ShaperBuffers buffers = Seed(charCount, glyphs, glyphCount, clusterMap);

            OpenTypeLayoutResult result = OpenTypeLayoutResult.ScriptNotFound;
            uint used = 0;

            foreach (uint script in plan.ScriptCandidates(DfltScript))
            {
                // Re-seed each attempt: a pass that failed part way may already have touched them.
                buffers = Seed(charCount, glyphs, glyphCount, clusterMap);
                used = script;

                if (plan.IsIndic)
                {
                    DecomposeSplitMatras(glyphTypeface, text, charCount, buffers.CharMap, buffers.GlyphInfoList);
                }

                result = OpenTypeLayout.SubstituteGlyphs(
                    font, buffers.LayoutWorkspace, script, DfltLangSys,
                    features, features.Length, 0,
                    charCount, buffers.CharMap, buffers.GlyphInfoList);

                if (plan.IsIndic && result == OpenTypeLayoutResult.Success)
                {
                    // A shaping engine's order: the basic forms first, then the syllables are
                    // reordered, then the presentation forms see the reordered glyphs.
                    ReorderIndicSyllables(text, charCount, buffers.CharMap, buffers.GlyphInfoList);
                    Feature[] presentation = plan.IndicPresentationFeatures(text, charCount);
                    result = OpenTypeLayout.SubstituteGlyphs(
                        font, buffers.LayoutWorkspace, script, DfltLangSys,
                        presentation, presentation.Length, 0,
                        charCount, buffers.CharMap, buffers.GlyphInfoList);
                }

                if (!IsScriptMiss(result))
                {
                    break;
                }
            }

            if (s_log)
            {
                Log($"GSUB face='{FamilyOf(glyphTypeface)}' script='{Unpack(used)}' " +
                    $"features={features.Length} {glyphCount}->{buffers.GlyphInfoList.Length} result={result}");
            }

            if (result != OpenTypeLayoutResult.Success)
            {
                return glyphCount;
            }

            int newGlyphCount = buffers.GlyphInfoList.Length;
            if (newGlyphCount > glyphCapacity)
            {
                // Does not fit. Leave every buffer as we found it and let the caller grow them.
                return newGlyphCount;
            }

            GlyphInfoList glyphInfo = buffers.GlyphInfoList;
            UshortList charmap = buffers.CharMap;

            for (int g = 0; g < newGlyphCount; g++)
            {
                glyphs[g] = glyphInfo.Glyphs[g];
                sources[g] = (ushort)(glyphInfo.FirstChars[g] + 1);
            }
            for (int c = 0; c < charCount; c++)
            {
                clusterMap[c] = charmap[c];
            }

            if (canGlyphAlone != null)
            {
                UpdateCanGlyphAlone(charCount, clusterMap, canGlyphAlone);
            }

            return newGlyphCount;
        }

        /// <summary>
        ///  Applies the font's GPOS features: mark attachment, cursive attachment, and the kerning
        ///  that modern fonts keep in GPOS rather than in a 'kern' table.
        /// </summary>
        /// <remarks>
        ///  <para>
        ///   Runs entirely in FONT DESIGN UNITS and converts once at the end. GPOS mark attachment
        ///   subtracts the advances accumulated between a base glyph and its mark from an anchor
        ///   difference, so the advances fed in have to be in the same space as the anchors; feeding
        ///   it the ideal-unit advances the caller holds would misplace every mark by the scale
        ///   factor. Design units also mean one rounding step instead of two.
        ///  </para>
        ///  <para>
        ///   Advances are adjusted by their DELTA rather than overwritten, so a run whose GPOS does
        ///   nothing keeps the exact advances the caller computed and this pass cannot perturb
        ///   existing metrics.
        ///  </para>
        /// </remarks>
        internal static unsafe void Position(
            GlyphTypeface glyphTypeface,
            char*         text,
            int           charCount,
            ushort*       glyphs,
            int           glyphCount,
            ushort*       clusterMap,
            bool          isRightToLeft,
            double        designToIdeal,   // design units -> the caller's ideal units
            int*          advances,        // in/out: ideal-unit advances
            GlyphOffset*  offsets,         // in/out: ideal-unit offsets
            uint*         glyphProps = null, // in (may be null): the source characters Substitute recorded
            double        pixelsPerEm = 0,  // Display mode: the device ppem (0 in Ideal mode)
            double        pixelsPerDip = 1
            )
        {
            if (glyphTypeface == null || charCount <= 0 || glyphCount <= 0)
            {
                return;
            }

            try
            {
                PositionCore(glyphTypeface, text, charCount, glyphs, glyphCount, clusterMap,
                             isRightToLeft, designToIdeal, advances, offsets, glyphProps, pixelsPerEm, pixelsPerDip);
            }
            catch (Exception e)
            {
                // As in Substitute: degrade to the nominal placement the caller already has rather
                // than aborting GetGlyphPositions, which would lose the run's metrics entirely.
                if (s_log) Log($"GPOS face='{FamilyOf(glyphTypeface)}' THREW {e}");
            }
        }

        private static unsafe void PositionCore(
            GlyphTypeface glyphTypeface,
            char*         text,
            int           charCount,
            ushort*       glyphs,
            int           glyphCount,
            ushort*       clusterMap,
            bool          isRightToLeft,
            double        designToIdeal,
            int*          advances,
            GlyphOffset*  offsets,
            uint*         glyphProps,
            double        pixelsPerEm,
            double        pixelsPerDip
            )
        {
            FontFaceLayoutInfo layout = glyphTypeface.FontFaceLayoutInfo;
            if (layout == null || layout.Gpos() == null)
            {
                return;   // no GPOS table -> nominal advances stand
            }

            ShapingPlan plan = ShapingPlan.For(text, charCount);
            Feature[] features = plan.BuildPositioningFeatures(charCount);
            if (features.Length == 0)
            {
                return;
            }

            ushort designEm = (ushort)glyphTypeface.DesignEmHeight;
            if (designEm == 0)
            {
                return;
            }

            // Design-unit advances to start from, straight out of the font's own metrics. GPOS
            // adjusts these; the difference is what gets scaled back into the caller's units.
            int[] designAdvances = new int[glyphCount];
            for (int g = 0; g < glyphCount; g++)
            {
                designAdvances[g] = DesignAdvance(glyphTypeface, glyphs[g], designEm);
            }

            // Display mode positions in DEVICE PIXELS, as DirectWrite's GetGdiCompatibleGlyphPlacements
            // does: the same OTLS engine with pixel metrics, so every anchor and value is rounded to
            // pixels on its own (Positioning.DesignToPixels) and device tables apply. A kasra under
            // Arial's initial beh at 32 ppem is round(-155) - round(95) = -3 px, not round(-3.9).
            // The advances it starts from are the caller's, already GDI-compatible whole pixels.
            ushort ppem = pixelsPerEm > 0 ? (ushort)Math.Round(pixelsPerEm, MidpointRounding.AwayFromZero) : (ushort)0;
            bool pixels = ppem > 0 && pixelsPerDip > 0;
            double unitToIdeal = pixels ? TextFormatterImp.ToIdeal / pixelsPerDip : designToIdeal;
            if (pixels)
            {
                for (int g = 0; g < glyphCount; g++)
                {
                    designAdvances[g] = (int)Math.Round(advances[g] * pixelsPerDip / TextFormatterImp.ToIdeal);
                }
            }

            int[] workAdvances = (int[])designAdvances.Clone();
            LayoutOffset[] workOffsets = new LayoutOffset[glyphCount];

            IOpenTypeFont font = new GsubGposTables(layout);
            ShaperBuffers buffers = Seed(charCount, glyphs, glyphCount, clusterMap, glyphProps);

            // DesignEmHeight = 0 asks the engine for design units (Positioning.DesignToPixels is the
            // identity then). PixelsEm = 0 with it: a GPOS device table indexed by 0 ppem is out of
            // its own size range and contributes nothing, which is what we want -- device deltas are
            // hinting adjustments in PIXELS and would otherwise be added to a design-unit total.
            var metrics = pixels
                ? new LayoutMetrics(isRightToLeft ? TextFlowDirection.RTL : TextFlowDirection.LTR, designEm, ppem, ppem)
                : new LayoutMetrics(isRightToLeft ? TextFlowDirection.RTL : TextFlowDirection.LTR, 0, 0, 0);

            OpenTypeLayoutResult result = OpenTypeLayoutResult.ScriptNotFound;
            uint used = 0;

            int[] rtlShift = isRightToLeft ? new int[glyphCount] : null;
            fixed (int* pWorkAdvances = workAdvances)
            fixed (LayoutOffset* pWorkOffsets = workOffsets)
            {
                try
                {
                    foreach (uint script in plan.ScriptCandidates(DfltScript))
                    {
                        buffers = Seed(charCount, glyphs, glyphCount, clusterMap, glyphProps);
                        Array.Copy(designAdvances, workAdvances, glyphCount);
                        Array.Clear(workOffsets, 0, glyphCount);
                        if (rtlShift != null) Array.Clear(rtlShift, 0, glyphCount);
                        used = script;

                        Positioning.RtlAdvanceShift = rtlShift;
                        result = OpenTypeLayout.PositionGlyphs(
                            font, buffers.LayoutWorkspace, script, DfltLangSys, metrics,
                            features, features.Length, 0,
                            charCount, buffers.CharMap, buffers.GlyphInfoList,
                            pWorkAdvances, pWorkOffsets);

                        if (!IsScriptMiss(result))
                        {
                            break;
                        }
                    }
                }
                finally
                {
                    Positioning.RtlAdvanceShift = null;
                }
            }

            if (s_log)
            {
                Log($"GPOS face='{FamilyOf(glyphTypeface)}' script='{Unpack(used)}' " +
                    $"glyphs={glyphCount} result={result}");
            }

            if (result != OpenTypeLayoutResult.Success)
            {
                return;
            }

            // A positioning lookup must not change the glyph count; if it somehow did, the buffers
            // no longer describe the caller's run and applying them would corrupt it.
            if (buffers.GlyphInfoList.Length != glyphCount)
            {
                return;
            }

            for (int g = 0; g < glyphCount; g++)
            {
                // Where the caller's advance is the nominal one, round the ADJUSTED advance once,
                // as WPF rounds the float advance DWrite's GetGlyphPlacements returns with the
                // kerning already in it. Rounding the nominal advance and the adjustment separately
                // is off by an ideal unit on some glyphs, which a long line adds up.
                int nominal = DWriteIdealAdvance(designAdvances[g], designToIdeal, designEm);
                advances[g] = !pixels && advances[g] == nominal
                    ? DWriteIdealAdvance(workAdvances[g], designToIdeal, designEm)
                    : advances[g] + Round((workAdvances[g] - designAdvances[g]) * unitToIdeal);
                // The engine's dx is physical (+ right); DirectWrite's offset runs in the reading
                // direction, so a right-to-left run's is the negation (a qamats DirectWrite puts at
                // -4.16 the engine puts at +4.16). The forwarder TRUNCATES an offset into ideal units
                // -- du = (int)(advanceOffset * scalingFactor), TextAnalyzer.cpp -- of DWrite's float
                // offset, the design value times the float em scale; rounding it put Arial's sheva
                // at -809 where stock has -808.
                // Right to left the reading-direction offset is XAdvance - XPlacement (rtlShift holds
                // the XAdvance the value records applied; dx stayed the physical placement).
                int dx = isRightToLeft ? rtlShift[g] - workOffsets[g].dx : workOffsets[g].dx;
                offsets[g].du += pixels ? (int)(dx * unitToIdeal) : TruncateOffset(dx, designToIdeal, designEm);
                offsets[g].dv += pixels ? (int)(workOffsets[g].dy * unitToIdeal) : TruncateOffset(workOffsets[g].dy, designToIdeal, designEm);
            }
        }

        // ---- Indic reordering ---------------------------------------------------------------

        /// <summary>The vowel signs written BEFORE the consonant they follow (the left matras).</summary>
        private static bool IsPreBaseMatra(char c) => c is '\u093F' or '\u094E'
            or '\u09BF' or '\u09C7' or '\u09C8'
            or '\u0A3F' or '\u0ABF'
            or '\u0B47'
            or '\u0BC6' or '\u0BC7' or '\u0BC8'
            or '\u0D46' or '\u0D47' or '\u0D48';

        private static bool IsIndicVirama(char c) => c >= '\u0900' && c <= '\u0D7F' && (c & 0x7F) == 0x4D;

        /// <summary>
        ///  The vowel signs DirectWrite shapes as their canonical decompositions -- the two-part
        ///  (and Kannada's three-part) vowels: Bengali o/au, Oriya ai/o/au, Tamil o/oo/au, Telugu
        ///  ai, Kannada ii/ee/ai/o/oo, Malayalam o/oo/au. Kannada "prii" comes back as its i-sign
        ///  and a U+0CD5 length mark.
        /// </summary>
        private static string SplitMatra(char c) => c switch
        {
            '\u09CB' => "\u09C7\u09BE",
            '\u09CC' => "\u09C7\u09D7",
            '\u0B48' => "\u0B47\u0B56",
            '\u0B4B' => "\u0B47\u0B3E",
            '\u0B4C' => "\u0B47\u0B57",
            '\u0BCA' => "\u0BC6\u0BBE",
            '\u0BCB' => "\u0BC7\u0BBE",
            '\u0BCC' => "\u0BC6\u0BD7",
            '\u0C48' => "\u0C46\u0C56",
            '\u0CC0' => "\u0CBF\u0CD5",
            '\u0CC7' => "\u0CC6\u0CD5",
            '\u0CC8' => "\u0CC6\u0CD6",
            '\u0CCA' => "\u0CC6\u0CC2",
            '\u0CCB' => "\u0CC6\u0CC2\u0CD5",
            '\u0D4A' => "\u0D46\u0D3E",
            '\u0D4B' => "\u0D47\u0D3E",
            '\u0D4C' => "\u0D46\u0D57",
            _ => null,
        };

        /// <summary>
        ///  DirectWrite's Indic engine shapes a two-part vowel as its two parts: the character keeps
        ///  one place in the text and gets two glyphs, so the first can be reordered in front of its
        ///  consonant like any pre-base matra (Tamil "mozhi": e-sign, ma, aa-sign).
        /// </summary>
        private static unsafe void DecomposeSplitMatras(GlyphTypeface glyphTypeface, char* text, int charCount,
                                                        UshortList charmap, GlyphInfoList glyphInfo)
        {
            for (int c = 0; c < charCount; c++)
            {
                if (SplitMatra(text[c]) is not string parts) continue;
                var partGlyphs = new ushort[parts.Length];
                bool mapped = true;
                for (int k = 0; k < parts.Length && mapped; k++)
                {
                    mapped = glyphTypeface.CharacterToGlyphMap.TryGetValue(parts[k], out partGlyphs[k]) && partGlyphs[k] != 0;
                }
                if (!mapped) continue;

                int g = charmap[c];
                if (g >= glyphInfo.Length || (c > 0 && charmap[c - 1] == g) || (c + 1 < charCount && charmap[c + 1] == g)) continue;
                int extra = parts.Length - 1;
                glyphInfo.Insert(g, extra);
                for (int k = 0; k < parts.Length; k++)
                {
                    glyphInfo.Glyphs[g + k] = partGlyphs[k];
                    glyphInfo.GlyphFlags[g + k] = (ushort)GlyphFlags.Unresolved;
                    glyphInfo.FirstChars[g + k] = (ushort)c;
                    glyphInfo.LigatureCounts[g + k] = 1;
                }
                for (int k = c + 1; k < charCount; k++) charmap[k] = (ushort)(charmap[k] + extra);
            }
        }

        private static bool IsIndicMark(char c)
            => CharUnicodeInfo.GetUnicodeCategory(c) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark;

        /// <summary>One orthographic syllable: a base, the consonants a virama binds to it (and a
        /// joiner after the virama), and the signs that hang off the result.</summary>
        private static unsafe int IndicSyllableEnd(char* text, int at, int charCount)
        {
            int i = at + 1;
            while (i < charCount)
            {
                char c = text[i];
                if (IsIndicMark(c)) { i++; continue; }
                if (c is '\u200D' or '\u200C' && IsIndicVirama(text[i - 1])) { i++; continue; }
                // A virama binds the next consonant into the syllable -- except Tamil's pulli, which
                // ends one: "ll" + "lai" is two syllables, and the ai-sign goes before the second lla.
                if ((IsIndicVirama(text[i - 1]) && !IsTamil(text[i - 1]))
                    || (text[i - 1] is '\u200D' && i >= 2 && IsIndicVirama(text[i - 2])))
                {
                    i++;
                    continue;
                }
                break;
            }
            return i;
        }

        private static bool IsTamil(char c) => c >= (char)0x0B80 && c <= (char)0x0BFF;

        /// <summary>Candrabindu, anusvara and visarga: offsets 1 to 3 of every Indic block.</summary>
        private static bool IsSyllableModifier(char c) => c >= (char)0x0900 && c <= (char)0x0D7F && (c & 0x7F) is >= 1 and <= 3;

        /// <summary>The ra whose post-virama form is written before the base: Malayalam's 'pref'.
        /// (Oriya, Telugu and Kannada ligate virama + ra too, into a below- or post-base form that
        /// stays where it is.)</summary>
        private static bool IsPrefRa(char c) => c is '\u0D30';

        // Bengali puts its reph before a post-base matra ("rbaa": ba, reph, aa); Devanagari after it
        // ("rvo": va, o, reph, which the face then ligates).
        private static bool RephBeforePostBase(char ra) => ra is '\u09B0';

        /// <summary>The right-side (post-base) vowel signs of the scripts whose reph precedes them.</summary>
        private static bool IsPostBaseMatra(char c) => c is '\u093B' or '\u093E' or '\u0940' or '\u0949' or '\u094A' or '\u094B' or '\u094C' or '\u094F'
            or '\u09BE' or '\u09C0' or '\u09D7' or '\u0ABE' or '\u0AC0' or '\u0AC9' or '\u0ACB' or '\u0ACC';

        /// <summary>Ra, whose virama form at the start of a syllable is the reph.</summary>
        private static bool IsIndicRa(char c) => c is '\u0930' or '\u09B0' or '\u0AB0' or '\u0B30'
            or '\u0C30' or '\u0CB0';

        /// <summary>
        ///  Reorders each syllable after the basic forms, as DirectWrite's Indic engine does: a reph
        ///  (the rphf form of a syllable-initial ra + virama) goes to the END of the syllable --
        ///  "rtha" is tha, reph; "rthu" tha, u, reph -- and a pre-base matra to the front, in front of
        ///  the conjunct or half forms the basic forms made. The syllable becomes one cluster, as
        ///  DirectWrite's cluster map has it (its glyphs are no longer in character order).
        /// </summary>
        private static unsafe void ReorderIndicSyllables(char* text, int charCount, UshortList charmap, GlyphInfoList glyphInfo)
        {
            int glyphCount = glyphInfo.Length;
            for (int c = 0; c < charCount; )
            {
                int end = IndicSyllableEnd(text, c, charCount);
                int gs = charmap[c];
                int ge = end < charCount ? charmap[end] : glyphCount;
                int reph = ge > gs + 1 && end - c > 2 && IsIndicRa(text[c]) && IsIndicVirama(text[c + 1])
                           && charmap[c + 1] == gs && charmap[c + 2] != gs
                    ? gs : -1;
                // A pre-base consonant form (Malayalam's virama + ra, 'pref') goes in front of the
                // consonant it follows -- the base: "stra" is sa, virama, ra-form, ta.
                for (int i = c + 1; i + 1 < end && ge > gs + 1; i++)
                {
                    if (!IsIndicVirama(text[i]) || !IsPrefRa(text[i + 1])) continue;
                    int g = charmap[i];
                    int baseGlyph = charmap[i - 1];
                    if (g <= gs || g >= ge || charmap[i + 1] != g || baseGlyph >= g) continue;   // no pre-base form was made
                    MoveGlyph(glyphInfo, g, baseGlyph);
                    if (reph >= baseGlyph) reph++;
                    break;
                }
                for (int i = c + 1; i < end && ge > gs + 1; i++)
                {
                    if (!IsPreBaseMatra(text[i]) && !(SplitMatra(text[i]) is string parts && IsPreBaseMatra(parts[0]))) continue;
                    int g = charmap[i];
                    if (g <= gs || g >= ge) continue;   // it went into a ligature: nothing to move
                    MoveGlyph(glyphInfo, g, gs);
                    if (reph >= 0) reph++;
                    break;
                }
                if (reph >= 0)
                {
                    // ... before the syllable modifiers (candrabindu, anusvara, visarga), which the
                    // face then joins it with: "rtham" is tha, reph+anusvara.
                    int to = ge - 1;
                    while (to > reph && IsSyllableModifier(text[glyphInfo.FirstChars[to]])) to--;
                    if (RephBeforePostBase(text[c]))
                    {
                        for (int g = reph + 1; g <= to; g++)
                        {
                            if (IsPostBaseMatra(text[glyphInfo.FirstChars[g]])) { to = g - 1; break; }
                        }
                    }
                    MoveGlyphForward(glyphInfo, reph, to);
                }
                if (ge > gs)
                {
                    for (int k = c; k < end; k++) charmap[k] = (ushort)gs;
                }
                c = end;
            }
        }

        private static void MoveGlyphForward(GlyphInfoList glyphInfo, int from, int to)
        {
            ushort glyph = glyphInfo.Glyphs[from], flags = glyphInfo.GlyphFlags[from];
            ushort first = glyphInfo.FirstChars[from], count = glyphInfo.LigatureCounts[from];
            for (int g = from; g < to; g++)
            {
                glyphInfo.Glyphs[g] = glyphInfo.Glyphs[g + 1];
                glyphInfo.GlyphFlags[g] = glyphInfo.GlyphFlags[g + 1];
                glyphInfo.FirstChars[g] = glyphInfo.FirstChars[g + 1];
                glyphInfo.LigatureCounts[g] = glyphInfo.LigatureCounts[g + 1];
            }
            glyphInfo.Glyphs[to] = glyph;
            glyphInfo.GlyphFlags[to] = flags;
            glyphInfo.FirstChars[to] = first;
            glyphInfo.LigatureCounts[to] = count;
        }

        private static void MoveGlyph(GlyphInfoList glyphInfo, int from, int to)
        {
            ushort glyph = glyphInfo.Glyphs[from], flags = glyphInfo.GlyphFlags[from];
            ushort first = glyphInfo.FirstChars[from], count = glyphInfo.LigatureCounts[from];
            for (int g = from; g > to; g--)
            {
                glyphInfo.Glyphs[g] = glyphInfo.Glyphs[g - 1];
                glyphInfo.GlyphFlags[g] = glyphInfo.GlyphFlags[g - 1];
                glyphInfo.FirstChars[g] = glyphInfo.FirstChars[g - 1];
                glyphInfo.LigatureCounts[g] = glyphInfo.LigatureCounts[g - 1];
            }
            glyphInfo.Glyphs[to] = glyph;
            glyphInfo.GlyphFlags[to] = flags;
            glyphInfo.FirstChars[to] = first;
            glyphInfo.LigatureCounts[to] = count;
        }

        /// <summary>
        ///  Puts every combining mark in the cluster of the character before it, once the run is
        ///  shaped: the mark keeps its own glyph, but a caret steps over a base and its marks as one
        ///  cell, as DWrite's shaping engines cluster them (GenericEngineGetGlyphs keeps an
        ///  IS_COMBINING character with its base; the Hebrew and Arabic engines do the same). It
        ///  happens after GSUB and GPOS because both read the cluster map: the per-character
        ///  features (the Arabic joining forms) to find their glyphs, mark-to-ligature to find which
        ///  component a mark sits on.
        /// </summary>
        internal static unsafe void MergeMarkClusters(char* text, int charCount, ushort* clusterMap, int glyphCount)
        {
            for (int i = 1; i < charCount; i++)
            {
                if (clusterMap[i] == clusterMap[i - 1]) continue;
                int cp = text[i];
                if (char.IsLowSurrogate(text[i])) continue;
                if (char.IsHighSurrogate(text[i]) && i + 1 < charCount && char.IsLowSurrogate(text[i + 1]))
                {
                    cp = char.ConvertToUtf32(text[i], text[i + 1]);
                }
                if (CharUnicodeInfo.GetUnicodeCategory(cp) is not (UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark))
                {
                    continue;
                }

                // Only a mark whose glyphs follow its base's can join it; anything else would make the
                // map non-monotonic.
                ushort from = clusterMap[i];
                ushort to = clusterMap[i - 1];
                if (from < to) continue;
                for (int j = i; j < charCount && clusterMap[j] == from; j++)
                {
                    clusterMap[j] = to;
                }
            }
        }

        /// <summary>
        ///  Shapes a nominal run held in managed arrays, resizing them as substitution requires.
        /// </summary>
        /// <remarks>
        ///  The array-shaped face of <see cref="Substitute"/> and <see cref="Position"/>, for
        ///  FormattedTextSymbols -- the line COLLAPSING SYMBOL, which is glyphed in one call
        ///  (TextAnalyzer.GetGlyphsAndTheirPlacements) rather than through the two LineServices
        ///  callbacks, and so needs its own shaping. Only a custom
        ///  <c>TextCollapsingProperties.Symbol</c> reaches it; the default ellipsis is one Latin
        ///  character. Narrow, but an unshaped fragment at the end of every trimmed line is exactly
        ///  the kind of thing nobody would trace back to here.
        ///
        ///  Growth is handled here rather than pushed at the caller: this owns the arrays, so it can
        ///  allocate what the shaper asks for and go again.
        /// </remarks>
        internal static unsafe void ShapeArrays(
            GlyphTypeface     glyphTypeface,
            char[]            text,
            bool              isRightToLeft,
            double            designToIdeal,
            ref ushort[]      clusterMap,
            ref ushort[]      glyphIndices,
            ref int[]         glyphAdvances,
            ref GlyphOffset[] glyphOffsets
            )
        {
            if (glyphTypeface == null || text == null || text.Length == 0 ||
                glyphIndices == null || glyphIndices.Length == 0 ||
                clusterMap == null || glyphAdvances == null || glyphOffsets == null)
            {
                return;
            }

            int charCount = text.Length;
            int glyphCount = glyphIndices.Length;

            // Substitution may need more room than the nominal run took. Ask at the run's own size;
            // if that is not enough the shaper reports the count it needs and the second pass has it.
            for (int attempt = 0; attempt < 2; attempt++)
            {
                int required;
                fixed (char* pText = text)
                fixed (ushort* pGlyphs = glyphIndices)
                fixed (ushort* pCluster = clusterMap)
                {
                    required = Substitute(glyphTypeface, pText, charCount,
                                          pGlyphs, glyphCount, glyphIndices.Length,
                                          pCluster, null);
                }

                if (required <= glyphIndices.Length)
                {
                    glyphCount = required;
                    break;
                }

                Array.Resize(ref glyphIndices, required);
            }

            if (glyphIndices.Length != glyphCount)
            {
                Array.Resize(ref glyphIndices, glyphCount);
            }

            // Substitution can change the glyph count, and then the caller's nominal advances no
            // longer line up with the glyphs. Rebuild them from the font's own metrics, which is
            // what produced them in the first place.
            ushort designEm = glyphTypeface.DesignEmHeight;
            if (glyphAdvances.Length != glyphCount)
            {
                glyphAdvances = new int[glyphCount];
                for (int g = 0; g < glyphCount; g++)
                {
                    glyphAdvances[g] = designEm == 0
                        ? 0
                        : DWriteIdealAdvance(DesignAdvance(glyphTypeface, glyphIndices[g], designEm), designToIdeal, designEm);
                }
            }
            if (glyphOffsets.Length != glyphCount)
            {
                glyphOffsets = new GlyphOffset[glyphCount];
            }

            fixed (char* pText = text)
            fixed (ushort* pGlyphs = glyphIndices)
            fixed (ushort* pCluster = clusterMap)
            fixed (int* pAdvances = glyphAdvances)
            fixed (GlyphOffset* pOffsets = glyphOffsets)
            {
                Position(glyphTypeface, pText, charCount, pGlyphs, glyphCount, pCluster,
                         isRightToLeft, designToIdeal, pAdvances, pOffsets);
            }
        }

        #endregion

        #region Engine plumbing

        /// <summary>
        ///  Seeds the engine's buffers from a run of nominal glyphs: the char-to-glyph map, and the
        ///  per-glyph info the lookups read.
        /// </summary>
        /// <remarks>
        ///  FirstChars and LigatureCounts are derived by inverting the cluster map, so a cluster that
        ///  is already merged (a surrogate pair mapping to one glyph) carries its true character span.
        ///  GlyphFlags start at <see cref="GlyphFlags.Unresolved"/>, NOT zero: the engine only
        ///  classifies a glyph against GDEF when its type is Unresolved, and zero is Unassigned --
        ///  already-classified as nothing. Seeding zero left every glyph looking like a base glyph, so
        ///  IgnoreMarks on a kerning lookup did not ignore marks and mark attachment could not find
        ///  its marks.
        /// </remarks>
        private static unsafe ShaperBuffers Seed(int charCount, ushort* glyphs, int glyphCount, ushort* clusterMap,
                                                 uint* glyphProps = null)
        {
            var buffers = new ShaperBuffers((ushort)charCount, (ushort)glyphCount);
            GlyphInfoList glyphInfo = buffers.GlyphInfoList;
            UshortList charmap = buffers.CharMap;

            for (int c = 0; c < charCount; c++)
            {
                charmap[c] = clusterMap[c];
            }

            for (int g = 0; g < glyphCount; g++)
            {
                glyphInfo.Glyphs[g] = glyphs[g];
                glyphInfo.GlyphFlags[g] = (ushort)GlyphFlags.Unresolved;
                glyphInfo.FirstChars[g] = ushort.MaxValue;
                glyphInfo.LigatureCounts[g] = 0;
            }

            for (int c = 0; c < charCount; c++)
            {
                int g = clusterMap[c];
                if (g < 0 || g >= glyphCount)
                {
                    continue;
                }
                if (c < glyphInfo.FirstChars[g])
                {
                    glyphInfo.FirstChars[g] = (ushort)c;
                }
                glyphInfo.LigatureCounts[g] = (ushort)(glyphInfo.LigatureCounts[g] + 1);
            }

            for (int g = 0; g < glyphCount; g++)
            {
                // A glyph no character maps to (a decomposition product inside a cluster) takes the
                // character Substitute recorded it came from.
                int source = glyphProps != null ? (int)(glyphProps[g] >> GlyphSourceShift) - 1 : -1;
                if (glyphInfo.FirstChars[g] == ushort.MaxValue && source >= 0 && source < charCount)
                {
                    glyphInfo.FirstChars[g] = (ushort)source;
                }
                if (glyphInfo.FirstChars[g] == ushort.MaxValue)
                {
                    glyphInfo.FirstChars[g] = (ushort)g;
                }
                if (glyphInfo.LigatureCounts[g] == 0)
                {
                    glyphInfo.LigatureCounts[g] = 1;
                }
            }

            return buffers;
        }

        /// <summary>
        ///  Whether the pass failed because the font does not declare the script we asked for, as
        ///  opposed to running and finding nothing to do. Only the former is worth another attempt
        ///  under a different tag.
        /// </summary>
        private static bool IsScriptMiss(OpenTypeLayoutResult result)
            => result == OpenTypeLayoutResult.ScriptNotFound
            || result == OpenTypeLayoutResult.LangSysNotFound;

        /// <summary>
        ///  A character that ended up sharing its glyph with another character is part of a ligature
        ///  and can no longer be glyphed on its own.
        /// </summary>
        private static unsafe void UpdateCanGlyphAlone(int charCount, ushort* clusterMap, int* canGlyphAlone)
        {
            for (int c = 0; c < charCount; c++)
            {
                int g = clusterMap[c];
                for (int c2 = 0; c2 < charCount; c2++)
                {
                    if (c2 != c && clusterMap[c2] == g)
                    {
                        canGlyphAlone[c] = 0;
                        break;
                    }
                }
            }
        }

        /// <summary>The glyph's own advance in font design units.</summary>
        private static int DesignAdvance(GlyphTypeface glyphTypeface, ushort glyph, ushort designEm)
        {
            // AdvanceWidths is em-relative (the design advance over unitsPerEm), which is how WPF
            // exposes hmtx everywhere else.
            return glyphTypeface.AdvanceWidths.TryGetValue(glyph, out double em)
                ? Round(em * designEm)
                : 0;
        }

        private static int Round(double value) => (int)Math.Round(value, MidpointRounding.AwayFromZero);

        /// <summary>DirectWrite's em scale: the float em size over the units per em, in float.</summary>
        private static float DWriteScale(double designToIdeal, ushort designEm, out double emSize)
        {
            emSize = designToIdeal * designEm / TextFormatterImp.ToIdeal;
            return (float)emSize / designEm;
        }

        /// <summary>
        ///  A design-unit advance as stock WPF gets it: DirectWrite's FLOAT advance (design units
        ///  times the float em scale), rounded into ideal units the forwarder's way,
        ///  round(advance * emSize * 300 / (float)emSize) (TextAnalyzer.cpp). Rounding the exact
        ///  product instead is one unit off wherever it sits on a half: Calibri's 1080-unit V at
        ///  10.667 px (8 pt) is 1687.5000005 exactly but 1687.49997 through the float.
        /// </summary>
        internal static int DWriteIdealAdvance(int design, double designToIdeal, ushort designEm)
        {
            float scale = DWriteScale(designToIdeal, designEm, out double emSize);
            float advance = design * scale;
            return (int)Math.Round(advance * emSize * TextFormatterImp.ToIdeal / (float)emSize);
        }

        /// <summary>A design-unit offset as the forwarder turns DWrite's into ideal units: the
        /// float DIP offset (design units times the float em scale), truncated after scaling by the
        /// ideal factor.</summary>
        private static int TruncateOffset(int design, double designToIdeal, ushort designEm)
        {
            float scale = DWriteScale(designToIdeal, designEm, out _);
            float dip = design * scale;
            return (int)(dip * TextFormatterImp.ToIdeal);
        }

        private static string FamilyOf(GlyphTypeface glyphTypeface)
        {
            try
            {
                foreach (string name in glyphTypeface.FamilyNames.Values) { return name; }
            }
            catch { }
            return "?";
        }

        private static string Unpack(uint tag) => new string(new[]
            { (char)(tag >> 24), (char)((tag >> 16) & 0xFF), (char)((tag >> 8) & 0xFF), (char)(tag & 0xFF) });

        private static void Log(string message) => Console.WriteLine(message);

        #endregion

        #region The shaping plan: which script, and which features

        /// <summary>
        ///  What the run's characters imply: an OpenType script tag, and the features that script
        ///  wants applied. Everything here is derived from the text, because off Windows there is no
        ///  DWrite script analysis to read it from -- <c>ItemProps.ScriptAnalysis</c> is null.
        /// </summary>
        private readonly struct ShapingPlan
        {
            /// <summary>The script tag to try first.</summary>
            public readonly uint ScriptTag;

            /// <summary>
            ///  A second tag to try before giving up and falling back to DFLT, or the same as
            ///  <see cref="ScriptTag"/> when there is only one spelling. This exists for the Indic
            ///  scripts, which have two generations of tag: a font either registers the OpenType v2
            ///  tag ('dev2') or the original one ('deva'), and which one it is says which shaping
            ///  model the font was built for. Trying v2 first matches what every other shaper does.
            /// </summary>
            public readonly uint AltScriptTag;

            private readonly ScriptClass _class;

            private ShapingPlan(uint scriptTag, uint altScriptTag, ScriptClass scriptClass)
            {
                ScriptTag = scriptTag;
                AltScriptTag = altScriptTag;
                _class = scriptClass;
            }

            public static unsafe ShapingPlan For(char* text, int charCount)
            {
                // The first STRONG character decides. Runs are itemized before they reach here, so a
                // run is one script plus whatever punctuation and spacing rode along with it, and
                // those are exactly the characters that must not get a vote.
                for (int i = 0; i < charCount; i++)
                {
                    int scalar = text[i];
                    if (char.IsHighSurrogate(text[i]) && i + 1 < charCount && char.IsLowSurrogate(text[i + 1]))
                    {
                        scalar = char.ConvertToUtf32(text[i], text[i + 1]);
                        i++;
                    }

                    ScriptClass cls = Classify(scalar, out uint tag, out uint altTag);
                    if (cls != ScriptClass.Common)
                    {
                        return new ShapingPlan(tag, altTag, cls);
                    }
                }

                return new ShapingPlan(Tag("latn"), Tag("latn"), ScriptClass.Latin);
            }

            /// <summary>
            ///  The script tags to try, in order, ending with DFLT. Duplicates are collapsed so a
            ///  script with one spelling costs one attempt.
            /// </summary>
            public uint[] ScriptCandidates(uint dflt)
            {
                if (ScriptTag == dflt)
                {
                    return new[] { dflt };
                }
                if (AltScriptTag == ScriptTag || AltScriptTag == dflt)
                {
                    return new[] { ScriptTag, dflt };
                }
                return new[] { ScriptTag, AltScriptTag, dflt };
            }

            /// <summary>GSUB features, in application order.</summary>
            public unsafe Feature[] BuildSubstitutionFeatures(char* text, int charCount)
            {
                var features = new System.Collections.Generic.List<Feature>(16);
                ushort all = (ushort)charCount;

                switch (_class)
                {
                    case ScriptClass.Arabic:
                    case ScriptClass.Syriac:
                        // ccmp first (decompositions the positional forms then act on), then the
                        // per-character joining forms, then the ligatures that join across them.
                        Add(features, "ccmp", all);
                        Add(features, "locl", all);
                        AppendJoiningForms(features, text, charCount);
                        Add(features, "rlig", all);
                        Add(features, "calt", all);
                        Add(features, "liga", all);
                        Add(features, "mset", all);
                        break;

                    case ScriptClass.Indic:
                        // The basic forms only: conjuncts, half forms, rakar/rephs, nukta
                        // composition. The syllables are reordered after these and the
                        // presentation forms applied to the result (IndicPresentationFeatures).
                        Add(features, "ccmp", all);
                        Add(features, "locl", all);
                        Add(features, "nukt", all);
                        Add(features, "akhn", all);
                        Add(features, "rphf", all);
                        Add(features, "rkrf", all);
                        Add(features, "pref", all);
                        Add(features, "blwf", all);
                        Add(features, "half", all);
                        Add(features, "pstf", all);
                        Add(features, "vatu", all);
                        Add(features, "cjct", all);
                        break;

                    default:
                        // Latin, Cyrillic, Greek, Hebrew, Thai, CJK and everything else that needs no
                        // per-character machinery: composition plus the ligature families. This is
                        // the set DWrite enables by default for horizontal text.
                        Add(features, "ccmp", all);
                        Add(features, "locl", all);
                        Add(features, "rlig", all);
                        Add(features, "liga", all);
                        Add(features, "clig", all);
                        Add(features, "calt", all);
                        break;
                }

                return features.ToArray();
            }

            public bool IsIndic => _class == ScriptClass.Indic;

            /// <summary>The Indic presentation forms, applied once the syllables are reordered;
            /// 'init' on the syllable that starts a word (Bengali's word-initial e-matra).</summary>
            public unsafe Feature[] IndicPresentationFeatures(char* text, int charCount)
            {
                var features = new System.Collections.Generic.List<Feature>(8);
                ushort all = (ushort)charCount;
                for (int c = 0; c < charCount; )
                {
                    int end = IndicSyllableEnd(text, c, charCount);
                    bool wordStart = c == 0 || !(char.IsLetter(text[c - 1]) || IsIndicMark(text[c - 1]));
                    if (wordStart && char.IsLetter(text[c]))
                    {
                        features.Add(new Feature((ushort)c, (ushort)(end - c), Tag("init"), 1));
                    }
                    c = end;
                }
                Add(features, "pres", all);
                Add(features, "abvs", all);
                Add(features, "blws", all);
                Add(features, "psts", all);
                Add(features, "haln", all);
                Add(features, "calt", all);
                Add(features, "clig", all);
                return features.ToArray();
            }

            /// <summary>GPOS features.</summary>
            public Feature[] BuildPositioningFeatures(int charCount)
            {
                var features = new System.Collections.Generic.List<Feature>(8);
                ushort all = (ushort)charCount;

                Add(features, "kern", all);
                Add(features, "mark", all);
                Add(features, "mkmk", all);

                if (_class == ScriptClass.Arabic || _class == ScriptClass.Syriac)
                {
                    // Cursive attachment: what puts a Nastaliq or Kufi word on its sweeping baseline
                    // rather than on a flat one.
                    Add(features, "curs", all);
                }

                if (_class == ScriptClass.Indic)
                {
                    Add(features, "abvm", all);
                    Add(features, "blwm", all);
                    Add(features, "dist", all);
                }

                return features.ToArray();
            }

            private static void Add(System.Collections.Generic.List<Feature> features, string tag, ushort length)
                => features.Add(new Feature(0, length, Tag(tag), 1));

            /// <summary>
            ///  Adds one positional-form feature per character, per the Unicode cursive joining
            ///  algorithm (UAX #9 / the Arabic Shaping property).
            /// </summary>
            /// <remarks>
            ///  A letter takes its form from whether the letters on each side join to it, looking
            ///  THROUGH transparent characters (the harakat and other combining marks, which do not
            ///  break a join). The feature applies to a single character, which is what the Feature
            ///  start/length pair is for -- these cannot be run-wide like the others, because
            ///  neighbouring letters in the same run get different ones.
            /// </remarks>
            private static unsafe void AppendJoiningForms(
                System.Collections.Generic.List<Feature> features, char* text, int charCount)
            {
                // Classify once; the neighbour scan reads it repeatedly.
                var types = new JoiningType[charCount];
                for (int i = 0; i < charCount; i++)
                {
                    int scalar = text[i];
                    if (char.IsHighSurrogate(text[i]) && i + 1 < charCount && char.IsLowSurrogate(text[i + 1]))
                    {
                        scalar = char.ConvertToUtf32(text[i], text[i + 1]);
                        types[i] = JoiningOf(scalar);
                        if (i + 1 < charCount) { types[i + 1] = JoiningType.Transparent; }
                        i++;
                        continue;
                    }
                    types[i] = JoiningOf(scalar);
                }

                for (int i = 0; i < charCount; i++)
                {
                    if (types[i] == JoiningType.Transparent)
                    {
                        continue;
                    }

                    // The previous non-transparent letter joins forward if it can attach on its left
                    // side: dual-joining, left-joining, or join-causing (tatweel, ZWJ).
                    bool prevJoins = false;
                    for (int p = i - 1; p >= 0; p--)
                    {
                        if (types[p] == JoiningType.Transparent) continue;
                        prevJoins = types[p] == JoiningType.Dual
                                 || types[p] == JoiningType.Left
                                 || types[p] == JoiningType.Causing;
                        break;
                    }

                    // ... and the next one joins backward if it can attach on its right side.
                    bool nextJoins = false;
                    for (int n = i + 1; n < charCount; n++)
                    {
                        if (types[n] == JoiningType.Transparent) continue;
                        nextJoins = types[n] == JoiningType.Dual
                                 || types[n] == JoiningType.Right
                                 || types[n] == JoiningType.Causing;
                        break;
                    }

                    // A letter can only take a form its own joining type allows: a right-joining
                    // letter (alef, dal, waw, ...) never has an initial or medial form, so it takes
                    // the final form when something precedes it and the isolated form otherwise.
                    string form;
                    switch (types[i])
                    {
                        case JoiningType.Dual:
                            form = prevJoins ? (nextJoins ? "medi" : "fina")
                                             : (nextJoins ? "init" : "isol");
                            break;
                        case JoiningType.Right:
                            form = prevJoins ? "fina" : "isol";
                            break;
                        case JoiningType.Left:
                            form = nextJoins ? "init" : "isol";
                            break;
                        default:
                            form = "isol";
                            break;
                    }

                    features.Add(new Feature((ushort)i, 1, Tag(form), 1));
                }
            }
        }

        #endregion

        #region Unicode classification

        /// <summary>The Unicode general category of a codepoint, astral planes included.</summary>
        private static UnicodeCategory CategoryOf(int cp)
            => cp <= 0xFFFF
                ? CharUnicodeInfo.GetUnicodeCategory((char)cp)
                : CharUnicodeInfo.GetUnicodeCategory(char.ConvertFromUtf32(cp), 0);

        private enum ScriptClass
        {
            Common,      // punctuation, digits, spaces: no vote in picking the run's script
            Latin,
            Arabic,
            Syriac,
            Hebrew,
            Thaana,
            Indic,       // the Brahmic scripts that need the Indic feature set
            Thai,
            Other,       // has a script tag, needs no special feature handling
        }

        /// <summary>
        ///  The run's script class and OpenType script tag(s) from a codepoint.
        /// </summary>
        /// <remarks>
        ///  The tag matters as much as the class: the engine looks every feature up under a script
        ///  table, so a wrong tag means the font's lookups for that script are simply never reached.
        ///  Getting a DEFAULT here is not harmless either -- asking for 'latn' in a Devanagari font
        ///  can still succeed (many carry a latn table for their Latin glyphs) and then applies the
        ///  Indic feature set against the wrong lookups.
        /// </remarks>
        private static ScriptClass Classify(int cp, out uint tag, out uint altTag)
        {
            // Marks and format characters inherit the surrounding script rather than choosing one.
            switch (CategoryOf(cp))
            {
                case UnicodeCategory.NonSpacingMark:
                case UnicodeCategory.EnclosingMark:
                case UnicodeCategory.Format:
                case UnicodeCategory.Control:
                case UnicodeCategory.SpaceSeparator:
                case UnicodeCategory.LineSeparator:
                case UnicodeCategory.ParagraphSeparator:
                case UnicodeCategory.DecimalDigitNumber:
                case UnicodeCategory.OpenPunctuation:
                case UnicodeCategory.ClosePunctuation:
                case UnicodeCategory.OtherPunctuation:
                case UnicodeCategory.MathSymbol:
                    return Plain(ScriptClass.Common, "latn", out tag, out altTag);
            }

            // The cursive and complex scripts, which need their own feature handling.
            if (cp >= 0x0590 && cp <= 0x05FF) return Plain(ScriptClass.Hebrew, "hebr", out tag, out altTag);
            if (cp >= 0x0600 && cp <= 0x06FF) return Plain(ScriptClass.Arabic, "arab", out tag, out altTag);
            if (cp >= 0x0700 && cp <= 0x074F) return Plain(ScriptClass.Syriac, "syrc", out tag, out altTag);
            if (cp >= 0x0750 && cp <= 0x077F) return Plain(ScriptClass.Arabic, "arab", out tag, out altTag);
            if (cp >= 0x0780 && cp <= 0x07BF) return Plain(ScriptClass.Thaana, "thaa", out tag, out altTag);
            if (cp >= 0x08A0 && cp <= 0x08FF) return Plain(ScriptClass.Arabic, "arab", out tag, out altTag);
            if (cp >= 0xFB50 && cp <= 0xFDFF) return Plain(ScriptClass.Arabic, "arab", out tag, out altTag);
            if (cp >= 0xFE70 && cp <= 0xFEFF) return Plain(ScriptClass.Arabic, "arab", out tag, out altTag);

            // The Brahmic scripts on the Indic model. Each has an OpenType v2 tag and an original
            // one; a font declares whichever generation it was built for.
            if (cp >= 0x0900 && cp <= 0x097F) return Indic("dev2", "deva", out tag, out altTag);
            if (cp >= 0x0980 && cp <= 0x09FF) return Indic("bng2", "beng", out tag, out altTag);
            if (cp >= 0x0A00 && cp <= 0x0A7F) return Indic("gur2", "guru", out tag, out altTag);
            if (cp >= 0x0A80 && cp <= 0x0AFF) return Indic("gjr2", "gujr", out tag, out altTag);
            if (cp >= 0x0B00 && cp <= 0x0B7F) return Indic("ory2", "orya", out tag, out altTag);
            if (cp >= 0x0B80 && cp <= 0x0BFF) return Indic("tml2", "taml", out tag, out altTag);
            if (cp >= 0x0C00 && cp <= 0x0C7F) return Indic("tel2", "telu", out tag, out altTag);
            if (cp >= 0x0C80 && cp <= 0x0CFF) return Indic("knd2", "knda", out tag, out altTag);
            if (cp >= 0x0D00 && cp <= 0x0D7F) return Indic("mlm2", "mlym", out tag, out altTag);
            if (cp >= 0x0D80 && cp <= 0x0DFF) return Indic("sinh", "sinh", out tag, out altTag);

            if (cp >= 0x0E00 && cp <= 0x0E7F) return Plain(ScriptClass.Thai, "thai", out tag, out altTag);

            // Scripts that need no special feature handling but do need their own tag, so that
            // whatever locl/ccmp/liga lookups they carry are reachable.
            if (cp < 0x0370) return Plain(ScriptClass.Latin, "latn", out tag, out altTag);
            if (cp <= 0x03FF) return Plain(ScriptClass.Other, "grek", out tag, out altTag);
            if (cp <= 0x052F) return Plain(ScriptClass.Other, "cyrl", out tag, out altTag);
            if (cp <= 0x058F) return Plain(ScriptClass.Other, "armn", out tag, out altTag);
            if (cp >= 0x0E80 && cp <= 0x0EFF) return Plain(ScriptClass.Other, "lao ", out tag, out altTag);
            if (cp >= 0x0F00 && cp <= 0x0FFF) return Plain(ScriptClass.Other, "tibt", out tag, out altTag);
            if (cp >= 0x1000 && cp <= 0x109F) return Plain(ScriptClass.Other, "mym2", out tag, out altTag);
            if (cp >= 0x10A0 && cp <= 0x10FF) return Plain(ScriptClass.Other, "geor", out tag, out altTag);
            if (cp >= 0x1100 && cp <= 0x11FF) return Plain(ScriptClass.Other, "hang", out tag, out altTag);
            if (cp >= 0x1200 && cp <= 0x139F) return Plain(ScriptClass.Other, "ethi", out tag, out altTag);
            if (cp >= 0x1780 && cp <= 0x17FF) return Plain(ScriptClass.Other, "khmr", out tag, out altTag);
            if (cp >= 0x3040 && cp <= 0x30FF) return Plain(ScriptClass.Other, "kana", out tag, out altTag);
            if (cp >= 0x3400 && cp <= 0x9FFF) return Plain(ScriptClass.Other, "hani", out tag, out altTag);
            if (cp >= 0xAC00 && cp <= 0xD7AF) return Plain(ScriptClass.Other, "hang", out tag, out altTag);
            if (cp >= 0xF900 && cp <= 0xFAFF) return Plain(ScriptClass.Other, "hani", out tag, out altTag);

            return Plain(ScriptClass.Other, "latn", out tag, out altTag);
        }

        private static ScriptClass Plain(ScriptClass cls, string script, out uint tag, out uint altTag)
        {
            tag = altTag = Tag(script);
            return cls;
        }

        private static ScriptClass Indic(string v2, string v1, out uint tag, out uint altTag)
        {
            tag = Tag(v2);
            altTag = Tag(v1);
            return ScriptClass.Indic;
        }

        /// <summary>
        ///  The Unicode Joining_Type property, for the cursive scripts that have one.
        /// </summary>
        private enum JoiningType
        {
            NonJoining,    // U: joins on neither side
            Dual,          // D: joins on both
            Right,         // R: joins only to the letter before it
            Left,          // L: joins only to the letter after it
            Causing,       // C: joins on both and is not itself shaped (tatweel, ZWJ)
            Transparent,   // T: invisible to the algorithm (combining marks)
        }

        private static JoiningType JoiningOf(int cp)
        {
            // Transparent is a property of the CATEGORY, not a list: every non-spacing and enclosing
            // mark is transparent to joining, in Arabic and everywhere else. Deriving it this way
            // covers the harakat, the Quranic annotation marks and the Syriac vowel points at once.
            UnicodeCategory category = CategoryOf(cp);
            if (category == UnicodeCategory.NonSpacingMark || category == UnicodeCategory.EnclosingMark)
            {
                return JoiningType.Transparent;
            }

            switch (cp)
            {
                case 0x0640:    // ARABIC TATWEEL: the kashida, which exists to be joined to
                case 0x200D:    // ZERO WIDTH JOINER
                    return JoiningType.Causing;

                case 0x200C:    // ZERO WIDTH NON-JOINER
                case 0x0621:    // ARABIC LETTER HAMZA
                case 0x0674:    // ARABIC LETTER HIGH HAMZA
                case 0x08AD:    // ARABIC LETTER LOW ALEF
                    return JoiningType.NonJoining;
            }

            // Arabic right-joining letters: those with no initial or medial form. Everything else in
            // the Arabic letter ranges is dual-joining.
            if (IsArabicRightJoining(cp))
            {
                return JoiningType.Right;
            }

            // Arabic, Arabic Supplement and Arabic Extended-A letters.
            if ((cp >= 0x0622 && cp <= 0x064A) ||
                (cp >= 0x066E && cp <= 0x066F) ||
                (cp >= 0x0671 && cp <= 0x06D3) ||
                (cp >= 0x06FA && cp <= 0x06FF) ||
                (cp >= 0x0750 && cp <= 0x077F) ||
                (cp >= 0x08A0 && cp <= 0x08BF))
            {
                return JoiningType.Dual;
            }

            // Syriac letters (the Alaph's extra final forms are not modelled; see the file header).
            if (cp >= 0x0712 && cp <= 0x072F) return JoiningType.Dual;
            if (cp >= 0x074D && cp <= 0x074F) return JoiningType.Dual;

            return JoiningType.NonJoining;
        }

        /// <summary>
        ///  The Arabic and Syriac letters that join only to their right (Joining_Type=R): alef, dal,
        ///  thal, reh, zain, waw and the families built on them.
        /// </summary>
        private static bool IsArabicRightJoining(int cp)
        {
            switch (cp)
            {
                case 0x0622:    // ALEF WITH MADDA ABOVE
                case 0x0623:    // ALEF WITH HAMZA ABOVE
                case 0x0624:    // WAW WITH HAMZA ABOVE
                case 0x0625:    // ALEF WITH HAMZA BELOW
                case 0x0627:    // ALEF
                case 0x0629:    // TEH MARBUTA
                case 0x062F:    // DAL
                case 0x0630:    // THAL
                case 0x0631:    // REH
                case 0x0632:    // ZAIN
                case 0x0648:    // WAW
                case 0x0671:    // ALEF WASLA
                case 0x0672:
                case 0x0673:
                case 0x0675:
                case 0x0676:
                case 0x0677:
                case 0x06C0:    // (HEH GOAL U+06C1 and U+06C2 are DUAL-joining: Urdu's medial heh)
                case 0x06C3:
                case 0x06C4:
                case 0x06C5:
                case 0x06C6:
                case 0x06C7:
                case 0x06C8:
                case 0x06C9:
                case 0x06CA:
                case 0x06CB:
                case 0x06CD:
                case 0x06CF:
                case 0x06D2:    // YEH BARREE
                case 0x06D3:    // YEH BARREE WITH HAMZA ABOVE
                case 0x06D5:    // AE
                case 0x06EE:    // DAL WITH INVERTED V
                case 0x06EF:    // REH WITH INVERTED V
                case 0x0759: case 0x075A: case 0x075B:      // Arabic Supplement dal/reh forms
                case 0x076B: case 0x076C: case 0x0771:
                case 0x0773: case 0x0774: case 0x0778: case 0x0779:
                case 0x08AA: case 0x08AB: case 0x08AC:      // Arabic Extended-A reh/waw forms
                case 0x08AE: case 0x08B1: case 0x08B2: case 0x08B9:
                case 0x0710:    // SYRIAC ALAPH
                case 0x0715:    // SYRIAC DALATH
                case 0x0716:
                case 0x0717:
                case 0x0718:
                case 0x0719:
                case 0x071E:
                case 0x0728:
                case 0x072A:
                case 0x072C:
                case 0x072F:
                    return true;
            }

            // The Urdu/Sindhi/Pashto dal and reh families (U+0688..U+0699) are all right-joining.
            return cp >= 0x0688 && cp <= 0x0699;
        }

        #endregion
    }
}
