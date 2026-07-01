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

    public sealed class ItemProps
    {
        private static readonly PlatformNotSupportedException NotSupported =
            new PlatformNotSupportedException("DirectWrite text/font shaping is not available on this platform.");

        public ItemProps()
        {
        }

        public unsafe void* NumberSubstitutionNoAddRef
        {
            get { throw NotSupported; }
        }

        public unsafe void* ScriptAnalysis
        {
            get { throw NotSupported; }
        }

        public CultureInfo DigitCulture
        {
            get { throw NotSupported; }
        }

        public bool HasExtendedCharacter
        {
            get { throw NotSupported; }
        }

        public bool NeedsCaretInfo
        {
            get { throw NotSupported; }
        }

        public bool IsIndic
        {
            get { throw NotSupported; }
        }

        public bool IsLatin
        {
            get { throw NotSupported; }
        }

        public bool HasCombiningMark
        {
            get { throw NotSupported; }
        }

        public bool CanShapeTogether(ItemProps other)
        {
            throw NotSupported;
        }

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
            throw NotSupported;
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

        public TextAnalyzer(Native.IDWriteTextAnalyzer* textAnalyzer)
        {
        }

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
            throw NotSupported;
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
