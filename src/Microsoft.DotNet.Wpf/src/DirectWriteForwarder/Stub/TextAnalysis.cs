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
            // Managed itemization: no DWrite. Group consecutive characters that share the same
            // script class (via WPF's classification) into runs; each run becomes an ItemProps
            // span. Complex-script/number substitution nuances are simplified.
            var spans = new List<MS.Internal.Span>();
            if (length == 0) return spans;

            int runStart = 0;
            int prevKey = -1;
            bool prevCombining = false, prevCaret = false, prevIndic = false, prevLatin = false, prevExtended = false;

            for (int i = 0; i < (int)length; i++)
            {
                // Combine surrogate pairs into a scalar for classification.
                int scalar = text[i];
                int advance = 1;
                if (i + 1 < (int)length && (scalar & 0xFC00) == 0xD800 && (text[i + 1] & 0xFC00) == 0xDC00)
                {
                    scalar = (((scalar & 0x3FF) << 10) | (text[i + 1] & 0x3FF)) + 0x10000;
                    advance = 2;
                }

                classificationUtility.GetCharAttribute(scalar,
                    out bool isCombining, out bool needsCaretInfo, out bool isIndic,
                    out bool isDigit, out bool isLatin, out bool isStrong);

                bool extended = scalar > 0xFFFF;
                int key = (isLatin ? 1 : 0) | (isIndic ? 2 : 0) | (isDigit ? 4 : 0) | (isStrong ? 8 : 0);

                if (i == 0)
                {
                    prevKey = key; prevCombining = isCombining; prevCaret = needsCaretInfo;
                    prevIndic = isIndic; prevLatin = isLatin; prevExtended = extended;
                }
                else if (key != prevKey)
                {
                    spans.Add(new MS.Internal.Span(
                        MakeProps(prevKey, numberCulture, prevCombining, prevCaret, prevExtended, prevIndic, prevLatin),
                        i - runStart));
                    runStart = i;
                    prevKey = key; prevCombining = isCombining; prevCaret = needsCaretInfo;
                    prevIndic = isIndic; prevLatin = isLatin; prevExtended = extended;
                }
                else
                {
                    prevCombining |= isCombining; prevCaret |= needsCaretInfo; prevExtended |= extended;
                }

                if (advance == 2) i++;
            }

            spans.Add(new MS.Internal.Span(
                MakeProps(prevKey, numberCulture, prevCombining, prevCaret, prevExtended, prevIndic, prevLatin),
                (int)length - runStart));
            return spans;
        }

        private static ItemProps MakeProps(int key, CultureInfo digitCulture,
            bool combining, bool caret, bool extended, bool indic, bool latin)
        {
            var p = ItemProps.Create(null, null, digitCulture, combining, caret, extended, indic, latin);
            p.ScriptKey = key;
            return p;
        }

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
            throw NotSupported;
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
            throw NotSupported;
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
            throw NotSupported;
        }
    }
}
