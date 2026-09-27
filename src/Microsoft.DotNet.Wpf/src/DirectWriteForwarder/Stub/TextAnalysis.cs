// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Compile-only stub. See DirectWriteForwarderStub.csproj for details.
// Mirrors IClassification.h, ItemProps.h, TextAnalyzer.h.

using System;
using System.Collections.Generic;
using System.Globalization;

namespace MS.Internal.Text.TextInterface
{
    public interface IClassification
    {
        void GetCharAttribute(
            int unicodeScalar,
            out bool isCombining,
            out bool needsCaretInfo,
            out bool isIndic,
            out bool isDigit,
            out bool isLatin,
            out bool isStrong
            );
    }

    // Off-Windows: a character's script as WPF classifies it, so the managed itemizer can resolve
    // script runs the way DWrite's AnalyzeScript does. 0 for a character with no script of its own
    // (spaces, punctuation, digits, combining marks), which takes the script of the text around it.
    public interface IScriptClassification
    {
        int GetScript(int unicodeScalar);
    }

    // Managed-backed off-Windows. Produced by the managed TextAnalyzer.Itemize; carries the
    // per-run script/number-substitution properties WPF's font mapping and shaping read. There is
    // no native DWrite script analysis, so ScriptAnalysis/NumberSubstitution are null and the
    // shaping path falls back to nominal (cmap) glyphs -- correct for non-complex scripts.
    public sealed class ItemProps
    {
        internal int ScriptKey;          // opaque key: runs with the same key may shape together
        private CultureInfo _digitCulture;
        private bool _hasCombiningMark, _needsCaretInfo, _hasExtendedCharacter, _isIndic, _isLatin;

        public ItemProps()
        {
        }

        public unsafe void* NumberSubstitutionNoAddRef => null;

        public unsafe void* ScriptAnalysis => null;

        public CultureInfo DigitCulture => _digitCulture;

        public bool HasExtendedCharacter => _hasExtendedCharacter;

        public bool NeedsCaretInfo => _needsCaretInfo;

        public bool IsIndic => _isIndic;

        public bool IsLatin => _isLatin;

        public bool HasCombiningMark => _hasCombiningMark;

        public bool CanShapeTogether(ItemProps other)
            => other != null && other.ScriptKey == ScriptKey
               && Equals(other._digitCulture, _digitCulture);

        public static unsafe ItemProps Create(
            void* scriptAnalysis,
            void* numberSubstitution,
            CultureInfo digitCulture,
            bool hasCombiningMark,
            bool needsCaretInfo,
            bool hasExtendedCharacter,
            bool isIndic,
            bool isLatin
            )
        {
            return new ItemProps
            {
                _digitCulture = digitCulture,
                _hasCombiningMark = hasCombiningMark,
                _needsCaretInfo = needsCaretInfo,
                _hasExtendedCharacter = hasExtendedCharacter,
                _isIndic = isIndic,
                _isLatin = isLatin,
            };
        }
    }

    // Delegate shapes mirroring the private delegates declared inside TextAnalyzer.h. These are
    // used as the target types for the PresentationNative interop entry points defined in
    // MS.Internal.TextFormatting.LineServices.UnsafeNativeMethods.
    public unsafe delegate int CreateTextAnalysisSource(
        char* text,
        uint length,
        char* culture,
        void* factory,
        bool isRightToLeft,
        char* numberCulture,
        bool ignoreUserOverride,
        uint numberSubstitutionMethod,
        void** ppTextAnalysisSource);

    public unsafe delegate void* CreateTextAnalysisSink();

    public unsafe delegate void* GetScriptAnalysisList(void* textAnalysisSink);

    public unsafe delegate void* GetNumberSubstitutionList(void* textAnalysisSink);

    public sealed unsafe class TextAnalyzer
    {
        private static readonly PlatformNotSupportedException NotSupported =
            new PlatformNotSupportedException("DirectWrite text/font shaping is not available on this platform.");

        // Used by System.Windows.Media.textformatting.TextFormatterContext to replace soft
        // hyphens when needed.
        public const char CharHyphen = '\x002d';

        private readonly bool _managed;

        public TextAnalyzer(Native.IDWriteTextAnalyzer* textAnalyzer)
        {
        }

        private TextAnalyzer(bool managed) { _managed = managed; }

        // Off-Windows factory: a managed analyzer backed by the OpenType font stack.
        internal static TextAnalyzer CreateManaged() => new TextAnalyzer(true);

        public static IList<MS.Internal.Span> Itemize(
            char* text,
            uint length,
            CultureInfo culture,
            Native.IDWriteFactory* pDWriteFactory,
            bool isRightToLeftParagraph,
            CultureInfo numberCulture,
            bool ignoreUserOverride,
            uint numberSubstitutionMethod,
            IClassification classificationUtility,
            CreateTextAnalysisSink pfnCreateTextAnalysisSink,
            GetScriptAnalysisList pfnGetScriptAnalysisList,
            GetNumberSubstitutionList pfnGetNumberSubstitutionList,
            CreateTextAnalysisSource pfnCreateTextAnalysisSource
            )
        {
            // Managed itemization, following what the DWrite path does: DWrite's AnalyzeScript cuts
            // the text into script runs, and TextItemizer.Itemize cuts those again only where digit
            // handling changes. A character with no script of its own -- a space, punctuation, a
            // digit, a combining mark -- belongs to the script run around it, so "brown fox 0123"
            // is ONE item. The items become the text store's runs, and runs from different items
            // are never shaped together; cutting at every space (as this did) left one run per
            // word, and the kerning pairs across a word boundary (" A", " T") were never applied.
            var spans = new List<MS.Internal.Span>();
            if (length == 0) return spans;

            int n = (int)length;
            var scriptOf = classificationUtility as IScriptClassification;

            var combining = new bool[n];
            var caret = new bool[n];
            var indic = new bool[n];
            var latin = new bool[n];
            var strong = new bool[n];
            var extended = new bool[n];
            var digit = new bool[n];
            var script = new int[n];      // resolved below; Unresolved until then

            for (int i = 0; i < n; i++)
            {
                // Combine surrogate pairs into a scalar for classification; both halves take its
                // attributes.
                int scalar = text[i];
                int units = 1;
                if (i + 1 < n && (scalar & 0xFC00) == 0xD800 && (text[i + 1] & 0xFC00) == 0xDC00)
                {
                    scalar = (((scalar & 0x3FF) << 10) | (text[i + 1] & 0x3FF)) + 0x10000;
                    units = 2;
                }

                classificationUtility.GetCharAttribute(scalar,
                    out bool isCombining, out bool needsCaretInfo, out bool isIndic,
                    out bool isDigit, out bool isLatin, out bool isStrong);

                // A C0/C1 control has no visual and DWrite gives it a run of its own
                // (DWRITE_SCRIPT_SHAPES_NO_VISUAL), whatever surrounds it.
                int s = scalar < 0x20 || (scalar >= 0x7F && scalar <= 0x9F) ? NoVisualScript
                      : scriptOf != null ? scriptOf.GetScript(scalar)
                      : isStrong ? (isLatin ? 1 : isIndic ? 2 : 3) : 0;

                for (int u = i; u < i + units; u++)
                {
                    combining[u] = isCombining;
                    caret[u] = needsCaretInfo;
                    indic[u] = isIndic;
                    latin[u] = isLatin;
                    strong[u] = isStrong;
                    extended[u] = scalar > 0xFFFF;
                    // Digits are an item boundary only when they are substituted.
                    digit[u] = numberCulture != null && isDigit;
                    script[u] = s == 0 ? Unresolved : s;
                }
                i += units - 1;
            }

            // A character without a script takes the one before it; at the start of the text, the
            // first one after it. Text with no script at all is one Common run.
            int firstScript = Array.FindIndex(script, s => s != Unresolved && s != NoVisualScript);
            int carry = firstScript >= 0 ? script[firstScript] : CommonScript;
            for (int i = 0; i < n; i++)
            {
                if (script[i] == Unresolved)
                {
                    script[i] = carry;
                }
                else if (script[i] != NoVisualScript)
                {
                    carry = script[i];
                }
            }

            // Except whitespace at the very START of the text: DWrite leaves it an item of its own
            // (" abc" and " (abc" are two items, "( abc" and "1 abc" one). The text is a bidi
            // level run, so this is what splits the space after a Hebrew word from a Latin one.
            for (int i = 0; i < n && char.IsWhiteSpace(text[i]) && script[i] != NoVisualScript; i++)
                script[i] = LeadingWhitespace;

            int start = 0;
            for (int i = 1; i <= n; i++)
            {
                if (i < n && script[i] == script[start] && digit[i] == digit[start])
                {
                    continue;
                }

                // The item's attributes, aggregated as TextItemizer.Itemize does: a combining mark
                // or an extended character anywhere; caret info unless some strong character does
                // without it; Indic if any strong character is; Latin if every strong one is.
                bool hasCombining = false, needsCaret = true, hasExtended = false;
                int strongCount = 0, latinCount = 0, indicCount = 0;
                for (int c = start; c < i; c++)
                {
                    hasCombining |= combining[c];
                    hasExtended |= extended[c];
                    if (strong[c])
                    {
                        if (!caret[c]) needsCaret = false;
                        strongCount++;
                        if (latin[c]) latinCount++;
                        else if (indic[c]) indicCount++;
                    }
                }

                var props = ItemProps.Create(null, null, digit[start] ? numberCulture : null,
                    hasCombining, needsCaret, hasExtended, indicCount > 0,
                    strongCount > 0 && latinCount == strongCount);
                props.ScriptKey = script[start];
                spans.Add(new MS.Internal.Span(props, i - start));
                start = i;
            }
            return spans;
        }

        private const int Unresolved = -1;
        private const int NoVisualScript = -2;
        private const int CommonScript = 0;
        private const int LeadingWhitespace = -3;

        public static void AnalyzeExtendedCharactersAndDigits(
            char* text,
            uint length,
            TextItemizer textItemizer,
            byte* pCharAttribute,
            CultureInfo numberCulture,
            IClassification classificationUtility
            )
        {
            throw NotSupported;
        }

        // ---- Nominal shaping ---------------------------------------------------------
        //
        // The "simple shaper": nominal cmap glyph per codepoint (surrogate pairs form one
        // glyph), 1:1 cluster map, hmtx design advances, no OpenType features (no
        // ligatures/kerning/complex-script reordering). This is what DWrite effectively
        // produces for plain Latin runs, and it is what the managed LineServices shim
        // needs off-Windows when a font's GSUB pushes WPF off the fast path.

        public void GetGlyphsAndTheirPlacements(
            char* textString,
            uint textLength,
            Font font,
            ushort blankGlyphIndex,
            bool isSideways,
            bool isRightToLeft,
            CultureInfo cultureInfo,
            DWriteFontFeature[][] features,
            uint[] featureRangeLengths,
            double fontEmSize,
            double scalingFactor,
            float pixelsPerDip,
            System.Windows.Media.TextFormattingMode textFormattingMode,
            ItemProps itemProps,
            out ushort[] clusterMap,
            out ushort[] glyphIndices,
            out int[] glyphAdvances,
            out GlyphOffset[] glyphOffsets
            )
        {
            Managed.OpenTypeFontData d = font.Face.GetData();

            var cmap = new ushort[textLength];
            var gids = new ushort[textLength];
            uint glyphCount = MapNominal(textString, textLength, d, blankGlyphIndex, cmap, gids, isRightToLeft);

            clusterMap = cmap;
            glyphIndices = new ushort[glyphCount];
            Array.Copy(gids, glyphIndices, glyphCount);
            glyphAdvances = new int[glyphCount];
            glyphOffsets = new GlyphOffset[glyphCount];
            double toIdeal = fontEmSize / d.UnitsPerEm * scalingFactor;
            for (uint i = 0; i < glyphCount; i++)
                glyphAdvances[i] = (int)Math.Round(SimulatedMetrics.BoldAdvance(d, font.Face.Simulations, glyphIndices[i]) * toIdeal);
        }

        public void GetGlyphs(
            char* textString,
            uint textLength,
            Font font,
            ushort blankGlyphIndex,
            bool isSideways,
            bool isRightToLeft,
            CultureInfo cultureInfo,
            DWriteFontFeature[][] features,
            uint[] featureRangeLengths,
            uint maxGlyphCount,
            System.Windows.Media.TextFormattingMode textFormattingMode,
            ItemProps itemProps,
            ushort* clusterMap,
            ushort* textProps,
            ushort* glyphIndices,
            uint* glyphProps,
            int* pfCanGlyphAlone,
            out uint actualGlyphCount
            )
        {
            Managed.OpenTypeFontData d = font.Face.GetData();

            var cmap = new ushort[textLength];
            var gids = new ushort[textLength];
            actualGlyphCount = MapNominal(textString, textLength, d, blankGlyphIndex, cmap, gids, isRightToLeft);
            if (actualGlyphCount > maxGlyphCount)
                return;   // caller re-invokes with a larger buffer based on actualGlyphCount

            for (uint i = 0; i < textLength; i++)
            {
                clusterMap[i] = cmap[i];
                if (textProps != null) textProps[i] = 0;
                if (pfCanGlyphAlone != null) pfCanGlyphAlone[i] = 1;
            }
            for (uint g = 0; g < actualGlyphCount; g++)
            {
                glyphIndices[g] = gids[g];
                if (glyphProps != null) glyphProps[g] = 0;
            }
        }

        public void GetGlyphPlacements(
            char* textString,
            ushort* clusterMap,
            ushort* textProps,
            uint textLength,
            ushort* glyphIndices,
            uint* glyphProps,
            uint glyphCount,
            Font font,
            double fontEmSize,
            double scalingFactor,
            bool isSideways,
            bool isRightToLeft,
            CultureInfo cultureInfo,
            DWriteFontFeature[][] features,
            uint[] featureRangeLengths,
            System.Windows.Media.TextFormattingMode textFormattingMode,
            ItemProps itemProps,
            float pixelsPerDip,
            int* glyphAdvances,
            out GlyphOffset[] glyphOffsets
            )
        {
            Managed.OpenTypeFontData d = font.Face.GetData();
            double toIdeal = fontEmSize / d.UnitsPerEm * scalingFactor;
            // GDI_CLASSIC (TextFormattingMode.Display): DirectWrite's GetGdiCompatibleGlyphPlacements
            // hands back GDI's own whole-pixel advance, in DIPs.
            double pixels = fontEmSize * pixelsPerDip;
            bool gdi = textFormattingMode == System.Windows.Media.TextFormattingMode.Display && !isSideways && pixels > 0;
            for (uint g = 0; g < glyphCount; g++)
            {
                int px = gdi ? GdiCompatibleAdvances.PixelAdvance(font.Face, (int)font.Face.Simulations, pixels, glyphIndices[g]) : -1;
                glyphAdvances[g] = px >= 0
                    ? (int)Math.Round(px / (double)pixelsPerDip * scalingFactor)
                    : (int)Math.Round(SimulatedMetrics.BoldAdvance(d, font.Face.Simulations, glyphIndices[g]) * toIdeal);
            }
            glyphOffsets = new GlyphOffset[glyphCount];
        }

        /// <summary>The Unicode Bidi_Mirroring_Glyph of a character, or null: the paired brackets
        /// and relations (BidiMirroring.txt), which is every mirror text actually meets.</summary>
        private static uint? Mirror(uint cp)
        {
            switch (cp)
            {
                case 0x28: return 0x29; case 0x29: return 0x28;
                case 0x3C: return 0x3E; case 0x3E: return 0x3C;
                case 0x5B: return 0x5D; case 0x5D: return 0x5B;
                case 0x7B: return 0x7D; case 0x7D: return 0x7B;
                case 0xAB: return 0xBB; case 0xBB: return 0xAB;
                case 0x2039: return 0x203A; case 0x203A: return 0x2039;
                case 0x2045: return 0x2046; case 0x2046: return 0x2045;
                case 0x207D: return 0x207E; case 0x207E: return 0x207D;
                case 0x208D: return 0x208E; case 0x208E: return 0x208D;
                case 0x2208: return 0x220B; case 0x220B: return 0x2208;
                case 0x2209: return 0x220C; case 0x220C: return 0x2209;
                case 0x220A: return 0x220D; case 0x220D: return 0x220A;
                case 0x2264: return 0x2265; case 0x2265: return 0x2264;
                case 0x2266: return 0x2267; case 0x2267: return 0x2266;
                case 0x226A: return 0x226B; case 0x226B: return 0x226A;
                case 0x226E: return 0x226F; case 0x226F: return 0x226E;
                case 0x2270: return 0x2271; case 0x2271: return 0x2270;
                case 0x2282: return 0x2283; case 0x2283: return 0x2282;
                case 0x2286: return 0x2287; case 0x2287: return 0x2286;
                case 0x2329: return 0x232A; case 0x232A: return 0x2329;
                case 0x3008: return 0x3009; case 0x3009: return 0x3008;
                case 0xFF08: return 0xFF09; case 0xFF09: return 0xFF08;
                case 0xFF1C: return 0xFF1E; case 0xFF1E: return 0xFF1C;
                case 0xFF3B: return 0xFF3D; case 0xFF3D: return 0xFF3B;
                case 0xFF5B: return 0xFF5D; case 0xFF5D: return 0xFF5B;
                case 0xFF5F: return 0xFF60; case 0xFF60: return 0xFF5F;
                case 0xFF62: return 0xFF63; case 0xFF63: return 0xFF62;
            }
            // Runs of open/close pairs: the ceiling/floor brackets, the mathematical and
            // ornamental brackets, and the CJK corner and lenticular brackets.
            if ((cp >= 0x2308 && cp <= 0x230B) || (cp >= 0x2768 && cp <= 0x2775) || (cp >= 0x27E6 && cp <= 0x27EF)
                || (cp >= 0x2983 && cp <= 0x2998) || (cp >= 0x300A && cp <= 0x3011) || (cp >= 0x3014 && cp <= 0x301B))
                return (cp & 1) == 0 ? cp + 1 : cp - 1;
            return null;
        }

        // Maps UTF-16 text to nominal glyphs: one glyph per codepoint (a surrogate pair's
        // two code units share one cluster), cluster map entry = first glyph of the char.
        private static uint MapNominal(char* text, uint textLength,
            Managed.OpenTypeFontData d, ushort blankGlyphIndex, ushort[] clusterMap, ushort[] gids,
            bool isRightToLeft = false)
        {
            uint g = 0;
            for (uint i = 0; i < textLength; i++)
            {
                uint cp = text[i];
                bool pair = char.IsHighSurrogate(text[i]) && i + 1 < textLength && char.IsLowSurrogate(text[i + 1]);
                if (pair)
                    cp = (uint)char.ConvertToUtf32(text[i], text[i + 1]);

                // Right to left, a bidi-mirrored character is drawn as its mirror, as DWrite's
                // GetGlyphs does: the '(' that opens a parenthesis in Hebrew is the ')' glyph.
                if (isRightToLeft && Mirror(cp) is uint m && d.GlyphIndex(m) != 0) cp = m;
                ushort gid = (ushort)d.GlyphIndex(cp);
                if (gid == 0 && (cp == 0x20 || cp == 0xA0 || cp == 0x09))
                    gid = blankGlyphIndex;

                clusterMap[i] = (ushort)g;
                if (pair)
                {
                    clusterMap[i + 1] = (ushort)g;
                    i++;
                }
                gids[g++] = gid;
            }
            return g;
        }
    }
}
