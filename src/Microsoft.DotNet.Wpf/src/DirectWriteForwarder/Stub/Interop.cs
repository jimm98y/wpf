// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Compile-only stub. See DirectWriteForwarderStub.csproj for details.
// Mirrors Factory.h's "InternalFactory" helper and DWriteTypeConverter.h, plus the
// MS::Internal::NativeWPFDLLLoader / MS::Internal::TrueTypeSubsetter helpers declared directly in
// DirectWriteForwarder's main.cpp / TrueTypeSubsetter/truetype.h (both outside the
// MS.Internal.Text.TextInterface namespace).
//
// DWriteTypeConverter.h declares many more Convert() overloads than PresentationCore actually
// calls; only the overloads with real call sites are included below (adding the rest would just
// be dead surface, and some would collide on parameter type since C# cannot overload on return
// type alone).

using System;

namespace MS.Internal.Text.TextInterface
{
    public static class InternalFactory
    {
        public static unsafe int CreateFontFile(
            Native.IDWriteFactory* factory,
            FontFileLoader fontFileLoader,
            Uri filePathUri,
            Native.IDWriteFontFile** dwriteFontFile
            )
        {
            throw new PlatformNotSupportedException("DirectWrite text/font shaping is not available on this platform.");
        }
    }

    public static class DWriteTypeConverter
    {
        // DWRITE_FACTORY_TYPE Convert(FactoryType) - cast to int at the call site.
        public static int Convert(FactoryType factoryType)
        {
            throw new PlatformNotSupportedException("DirectWrite text/font shaping is not available on this platform.");
        }

        // unsigned char Convert(FontSimulations) - DWRITE_FONT_SIMULATIONS flags byte.
        public static byte Convert(FontSimulations fontSimulations)
        {
            throw new PlatformNotSupportedException("DirectWrite text/font shaping is not available on this platform.");
        }

        // DWRITE_MEASURING_MODE Convert(TextFormattingMode) - cast to UInt16 at the call site.
        public static int Convert(System.Windows.Media.TextFormattingMode measuringMode)
        {
            throw new PlatformNotSupportedException("DirectWrite text/font shaping is not available on this platform.");
        }
    }
}

namespace MS.Internal
{
    // Mirrors the no-op MS::Internal::NativeWPFDLLLoader declared in
    // DirectWriteForwarder/main.cpp. On Windows this exists purely to keep the module
    // constructor from being linked away in Release builds; it does no work of its own.
    public static class NativeWPFDLLLoader
    {
        public static void LoadDwrite()
        {
        }
    }

    // Mirrors the public MS::Internal::TrueTypeSubsetter declared in
    // DirectWriteForwarder/CPP/TrueTypeSubsetter/truetype.h. Only ComputeSubset is consumed by
    // PresentationCore (MS/internal/FontFace/FontDriver.cs), for building a subsetted TrueType
    // font for embedding.
    public static class TrueTypeSubsetter
    {
        public static unsafe byte[] ComputeSubset(void* fontData, int fileSize, Uri sourceUri, int directoryOffset, ushort[] glyphArray)
        {
            throw new PlatformNotSupportedException("DirectWrite text/font shaping is not available on this platform.");
        }
    }
}
