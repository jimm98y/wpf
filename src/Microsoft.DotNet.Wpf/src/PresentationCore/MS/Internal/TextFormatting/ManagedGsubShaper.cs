// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Applies OpenType GSUB substitution (ligatures and contextual alternates) to a
// nominally-mapped glyph run.
//
// On Windows WPF shapes text through DWrite, which applies the font's GSUB features.
// Off Windows the managed DirectWriteForwarder stub produces only *nominal* cmap
// glyphs (one glyph per codepoint, no OpenType features), so ligature-based fonts
// (e.g. Cascadia Code's "-->", "<!--") render as separate glyphs.
//
// This helper closes that gap by driving WPF's own managed OpenType layout engine
// (MS.Internal.Shaping.OpenTypeLayout -- the very engine WPF has always shipped for
// GSUB/GPOS) over the nominal run, in the one place that decides the glyph indices
// for shaped text: the LineServices GetGlyphs callback.
//

using MS.Internal.FontCache;
using MS.Internal.Shaping;
using System.Windows.Media;

namespace MS.Internal.TextFormatting
{
    internal static class ManagedGsubShaper
    {
        // Default GSUB features applied to horizontal text of any script, matching the
        // set DWrite/WPF enable by default. 'liga'/'clig'/'calt'/'rlig' cover standard,
        // contextual and required ligatures; 'ccmp' handles glyph composition/decomposition.
        private static readonly uint[] s_features = new uint[]
        {
            (uint)OpenTypeTags.ccmp,
            (uint)OpenTypeTags.rlig,
            (uint)OpenTypeTags.liga,
            (uint)OpenTypeTags.clig,
            (uint)OpenTypeTags.calt,
        };

        /// <summary>
        ///   Substitutes ligatures / contextual forms into a nominally-mapped run, in place.
        /// </summary>
        /// <remarks>
        ///   The buffers follow the LineServices GetGlyphs contract: <paramref name="glyphs"/>
        ///   holds nominal glyph indices, <paramref name="clusterMap"/> maps each character to
        ///   the index of its glyph. On return they reflect any substitution and the method
        ///   returns the (possibly smaller) glyph count. The run is left untouched when the
        ///   font exposes no GSUB for the script or nothing is substituted.
        /// </remarks>
        internal static unsafe int Substitute(
            GlyphTypeface glyphTypeface,
            int           charCount,
            ushort*       glyphs,        // in/out: glyph indices
            int           glyphCount,
            ushort*       clusterMap,    // in/out: char -> glyph index
            int*          canGlyphAlone  // in/out (may be null): per-char independence flag
            )
        {
            if (glyphTypeface == null || charCount <= 0 || glyphCount <= 0)
            {
                return glyphCount;
            }

            FontFaceLayoutInfo layout = glyphTypeface.FontFaceLayoutInfo;
            if (layout == null || layout.Gsub() == null)
            {
                return glyphCount;   // no GSUB table -> nothing to substitute
            }

            IOpenTypeFont font = new GsubGposTables(layout);

            ShaperBuffers buffers = new ShaperBuffers((ushort)charCount, (ushort)glyphCount);
            GlyphInfoList glyphInfo = buffers.GlyphInfoList;
            UshortList charmap = buffers.CharMap;

            // Seed the char->glyph map and per-glyph info from the nominal run. FirstChars and
            // LigatureCounts are derived by inverting the cluster map so any pre-merged cluster
            // (e.g. a surrogate pair mapping to one glyph) carries the correct character span.
            for (int c = 0; c < charCount; c++)
            {
                charmap[c] = clusterMap[c];
            }

            for (int g = 0; g < glyphCount; g++)
            {
                glyphInfo.Glyphs[g] = glyphs[g];
                glyphInfo.GlyphFlags[g] = 0;          // classified from GDEF by the engine
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
                if (glyphInfo.FirstChars[g] == ushort.MaxValue)
                {
                    glyphInfo.FirstChars[g] = (ushort)g;
                }
                if (glyphInfo.LigatureCounts[g] == 0)
                {
                    glyphInfo.LigatureCounts[g] = 1;
                }
            }

            Feature[] featureSet = new Feature[s_features.Length];
            for (int i = 0; i < s_features.Length; i++)
            {
                featureSet[i] = new Feature(0, (ushort)charCount, s_features[i], 1);
            }

            // Programming-ligature fonts register their features under 'latn' and/or the
            // default script; try Latin first, then fall back to the default script.
            OpenTypeLayoutResult result = OpenTypeLayout.SubstituteGlyphs(
                font, buffers.LayoutWorkspace,
                (uint)OpenTypeTags.latn, (uint)OpenTypeTags.dflt,
                featureSet, featureSet.Length, 0,
                charCount, charmap, glyphInfo);

            if (result == OpenTypeLayoutResult.ScriptNotFound)
            {
                result = OpenTypeLayout.SubstituteGlyphs(
                    font, buffers.LayoutWorkspace,
                    (uint)OpenTypeTags.dflt, (uint)OpenTypeTags.dflt,
                    featureSet, featureSet.Length, 0,
                    charCount, charmap, glyphInfo);
            }

            if (result != OpenTypeLayoutResult.Success)
            {
                return glyphCount;
            }

            int newGlyphCount = glyphInfo.Length;

            // Write the (possibly rewritten) glyph indices and cluster map back out. A single
            // substitution (e.g. a calt swap) can change glyph ids without changing the count.
            int writeCount = newGlyphCount < glyphCount ? newGlyphCount : glyphCount;
            for (int g = 0; g < writeCount; g++)
            {
                glyphs[g] = glyphInfo.Glyphs[g];
            }
            for (int c = 0; c < charCount; c++)
            {
                clusterMap[c] = charmap[c];
            }

            if (newGlyphCount < glyphCount && canGlyphAlone != null)
            {
                // A character that now shares its glyph with other characters (part of a
                // ligature) can no longer be glyphed on its own.
                for (int c = 0; c < charCount; c++)
                {
                    int g = clusterMap[c];
                    int shared = 0;
                    for (int c2 = 0; c2 < charCount; c2++)
                    {
                        if (clusterMap[c2] == g)
                        {
                            shared++;
                        }
                    }
                    if (shared > 1)
                    {
                        canGlyphAlone[c] = 0;
                    }
                }
            }

            return newGlyphCount < glyphCount ? newGlyphCount : glyphCount;
        }
    }
}
