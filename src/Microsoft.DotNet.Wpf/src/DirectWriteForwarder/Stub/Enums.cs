// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Compile-only stub for the managed surface normally provided by DirectWriteForwarder.vcxproj
// (a C++/CLI project wrapping Win32 DirectWrite, which cannot build on non-Windows platforms).
// See DirectWriteForwarderStub.csproj for details. This file contains the simple enums that
// mirror the ones declared in the original C++/CLI headers under
// DirectWriteForwarder/CPP/DWriteWrapper/*.h (FactoryType.h, FontStretch.h, FontStyle.h,
// FontWeight.h, FontSimulation.h, FontFaceType.h, FontFileType.h, InformationalStringID.h,
// OpenTypeTableTag.h, DWriteFontFeatureTag.h).

using System;

namespace MS.Internal.Text.TextInterface
{
    public enum FactoryType
    {
        Shared,
        Isolated
    }

    public enum FontStretch
    {
        Undefined = 0,
        UltraCondensed = 1,
        ExtraCondensed = 2,
        Condensed = 3,
        SemiCondensed = 4,
        Normal = 5,
        Medium = 5,
        SemiExpanded = 6,
        Expanded = 7,
        ExtraExpanded = 8,
        UltraExpanded = 9
    }

    public enum FontStyle
    {
        Normal = 0,
        Oblique = 1,
        Italic = 2
    }

    public enum FontWeight
    {
        Thin = 100,
        ExtraLight = 200,
        UltraLight = 200,
        Light = 300,
        Normal = 400,
        Regular = 400,
        Medium = 500,
        DemiBold = 600,
        SemiBOLD = 600,
        Bold = 700,
        ExtraBold = 800,
        UltraBold = 800,
        Black = 900,
        Heavy = 900,
        ExtraBlack = 950,
        UltraBlack = 950
    }

    [Flags]
    public enum FontSimulations
    {
        None = 0x0000,
        Bold = 0x0001,
        Oblique = 0x0002
    }

    public enum FontFaceType
    {
        CFF,
        TrueType,
        TrueTypeCollection,
        Type1,
        Vector,
        Bitmap,
        Unknown
    }

    public enum FontFileType
    {
        Unknown,
        CFF,
        TrueType,
        TrueTypeCollection,
        Type1PFM,
        Type1PFB,
        Vector,
        Bitmap
    }

    public enum InformationalStringID
    {
        None,
        CopyrightNotice,
        VersionStrings,
        Trademark,
        Manufacturer,
        Designer,
        DesignerURL,
        Description,
        FontVendorURL,
        LicenseDescription,
        LicenseInfoURL,
        WIN32FamilyNames,
        Win32SubFamilyNames,
        PreferredFamilyNames,
        PreferredSubFamilyNames,
        SampleText
    }

    // Values match DWRITE_MAKE_OPENTYPE_TAG('a','b','c','d') = a | (b<<8) | (c<<16) | (d<<24).
    public enum OpenTypeTableTag : uint
    {
        CharToIndexMap = 0x70616d63,       // 'cmap'
        ControlValue = 0x20747663,         // 'cvt '
        BitmapData = 0x54444245,           // 'EBDT'
        BitmapLocation = 0x434c4245,       // 'EBLC'
        BitmapScale = 0x43534245,          // 'EBSC'
        Editor0 = 0x30746465,              // 'edt0'
        Editor1 = 0x31746465,              // 'edt1'
        Encryption = 0x70797263,           // 'cryp'
        FontHeader = 0x64616568,           // 'head'
        FontProgram = 0x6d677066,          // 'fpgm'
        GridfitAndScanProc = 0x70736167,   // 'gasp'
        GlyphDirectory = 0x72696467,       // 'gdir'
        GlyphData = 0x66796c67,            // 'glyf'
        HoriDeviceMetrics = 0x786d6468,    // 'hdmx'
        HoriHeader = 0x61656868,           // 'hhea'
        HorizontalMetrics = 0x78746d68,    // 'hmtx'
        IndexToLoc = 0x61636f6c,           // 'loca'
        Kerning = 0x6e72656b,              // 'kern'
        LinearThreshold = 0x4853544c,      // 'LTSH'
        MaxProfile = 0x7078616d,           // 'maxp'
        NamingTable = 0x656d616e,          // 'name'
        OS_2 = 0x322f534f,                 // 'OS/2'
        Postscript = 0x74736f70,           // 'post'
        PreProgram = 0x70657270,           // 'prep'
        VertDeviceMetrics = 0x584d4456,    // 'VDMX'
        VertHeader = 0x61656876,           // 'vhea'
        VerticalMetrics = 0x78746d76,      // 'vmtx'
        PCLT = 0x544c4350,                 // 'PCLT'
        TTO_GSUB = 0x42555347,             // 'GSUB'
        TTO_GPOS = 0x534f5047,             // 'GPOS'
        TTO_GDEF = 0x46454447,             // 'GDEF'
        TTO_BASE = 0x45534142,             // 'BASE'
        TTO_JSTF = 0x46545347,             // 'JSTF'
    }

    public enum DWriteFontFeatureTag
    {
        AlternativeFractions = 0x63726661,
        PetiteCapitalsFromCapitals = 0x63703263,
        SmallCapitalsFromCapitals = 0x63733263,
        ContextualAlternates = 0x746c6163,
        CaseSensitiveForms = 0x65736163,
        GlyphCompositionDecomposition = 0x706d6363,
        ContextualLigatures = 0x67696c63,
        CapitalSpacing = 0x70737063,
        ContextualSwash = 0x68777363,
        CursivePositioning = 0x73727563,
        Default = 0x746c6664,
        DiscretionaryLigatures = 0x67696c64,
        ExpertForms = 0x74707865,
        Fractions = 0x63617266,
        FullWidth = 0x64697766,
        HalfForms = 0x666c6168,
        HalantForms = 0x6e6c6168,
        AlternateHalfWidth = 0x746c6168,
        HistoricalForms = 0x74736968,
        HorizontalKanaAlternates = 0x616e6b68,
        HistoricalLigatures = 0x67696c68,
        HalfWidth = 0x64697768,
        HojoKanjiForms = 0x6f6a6f68,
        JIS04Forms = 0x3430706a,
        JIS78Forms = 0x3837706a,
        JIS83Forms = 0x3338706a,
        JIS90Forms = 0x3039706a,
        Kerning = 0x6e72656b,
        StandardLigatures = 0x6167696c,
        LiningFigures = 0x6d756e6c,
        LocalizedForms = 0x6c636f6c,
        MarkPositioning = 0x6b72616d,
        MathematicalGreek = 0x6b72676d,
        MarkToMarkPositioning = 0x6b6d6b6d,
        AlternateAnnotationForms = 0x746c616e,
        NLCKanjiForms = 0x6b636c6e,
        OldStyleFigures = 0x6d756e6f,
        Ordinals = 0x6e64726f,
        ProportionalAlternateWidth = 0x746c6170,
        PetiteCapitals = 0x70616370,
        ProportionalFigures = 0x6d756e70,
        ProportionalWidths = 0x64697770,
        QuarterWidths = 0x64697771,
        RequiredLigatures = 0x67696c72,
        RubyNotationForms = 0x79627572,
        StylisticAlternates = 0x746c6173,
        ScientificInferiors = 0x666e6973,
        SmallCapitals = 0x70636d73,
        SimplifiedForms = 0x6c706d73,
        StylisticSet1 = 0x31307373,
        StylisticSet2 = 0x32307373,
        StylisticSet3 = 0x33307373,
        StylisticSet4 = 0x34307373,
        StylisticSet5 = 0x35307373,
        StylisticSet6 = 0x36307373,
        StylisticSet7 = 0x37307373,
        StylisticSet8 = 0x38307373,
        StylisticSet9 = 0x39307373,
        StylisticSet10 = 0x30317373,
        StylisticSet11 = 0x31317373,
        StylisticSet12 = 0x32317373,
        StylisticSet13 = 0x33317373,
        StylisticSet14 = 0x34317373,
        StylisticSet15 = 0x35317373,
        StylisticSet16 = 0x36317373,
        StylisticSet17 = 0x37317373,
        StylisticSet18 = 0x38317373,
        StylisticSet19 = 0x39317373,
        StylisticSet20 = 0x30327373,
        Subscript = 0x73627573,
        Superscript = 0x73707573,
        Swash = 0x68737773,
        Titling = 0x6c746974,
        TraditionalNameForms = 0x6d616e74,
        TabularFigures = 0x6d756e74,
        TraditionalForms = 0x64617274,
        ThirdWidths = 0x64697774,
        Unicase = 0x63696e75,
        SlashedZero = 0x6f72657a,
    }
}
