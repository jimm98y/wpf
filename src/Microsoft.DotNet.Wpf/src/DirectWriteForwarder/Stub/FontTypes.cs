// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Compile-only stub. See DirectWriteForwarderStub.csproj for details.
// Mirrors Font.h, FontFace.h, FontFamily.h, FontCollection.h, FontList.h, FontFile.h.
// All members throw PlatformNotSupportedException - this assembly is never meant to execute
// any real DirectWrite logic, only to satisfy PresentationCore's compile-time contract.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;

namespace MS.Internal.Text.TextInterface
{
    public sealed class Font
    {
        private static readonly PlatformNotSupportedException NotSupported =
            new PlatformNotSupportedException("DirectWrite text/font shaping is not available on this platform.");

        public unsafe Font(Native.IDWriteFont* font) { }

        public IntPtr DWriteFontAddRef
        {
            get { throw NotSupported; }
        }

        public FontFamily Family
        {
            get { throw NotSupported; }
        }

        public FontWeight Weight
        {
            get { throw NotSupported; }
        }

        public FontStretch Stretch
        {
            get { throw NotSupported; }
        }

        public FontStyle Style
        {
            get { throw NotSupported; }
        }

        public bool IsSymbolFont
        {
            get { throw NotSupported; }
        }

        public LocalizedStrings FaceNames
        {
            get { throw NotSupported; }
        }

        public FontSimulations SimulationFlags
        {
            get { throw NotSupported; }
        }

        public FontMetrics Metrics
        {
            get { throw NotSupported; }
        }

        public double Version
        {
            get { throw NotSupported; }
        }

        public FontMetrics DisplayMetrics(float emSize, float pixelsPerDip)
        {
            throw NotSupported;
        }

        public static void ResetFontFaceCache()
        {
            throw NotSupported;
        }

        public FontFace GetFontFace()
        {
            throw NotSupported;
        }

        public bool GetInformationalStrings(InformationalStringID informationalStringID, out LocalizedStrings informationalStrings)
        {
            throw NotSupported;
        }

        public bool HasCharacter(uint unicodeValue)
        {
            throw NotSupported;
        }
    }

    // Implements IDisposable to mirror the C++/CLI destructor ("~FontFace()") declared in
    // FontFace.h, which PresentationCore relies on via "using (FontFace fontFace = ...)".
    public sealed unsafe class FontFace : IDisposable
    {
        private static readonly PlatformNotSupportedException NotSupported =
            new PlatformNotSupportedException("DirectWrite text/font shaping is not available on this platform.");

        public FontFace(Native.IDWriteFontFace* fontFace) { }

        public void Dispose()
        {
        }

        public Native.IDWriteFontFace* DWriteFontFaceNoAddRef
        {
            get { throw NotSupported; }
        }

        public IntPtr DWriteFontFaceAddRef
        {
            get { throw NotSupported; }
        }

        public FontFaceType Type
        {
            get { throw NotSupported; }
        }

        public uint Index
        {
            get { throw NotSupported; }
        }

        public FontSimulations SimulationFlags
        {
            get { throw NotSupported; }
        }

        public bool IsSymbolFont
        {
            get { throw NotSupported; }
        }

        public FontMetrics Metrics
        {
            get { throw NotSupported; }
        }

        public ushort GlyphCount
        {
            get { throw NotSupported; }
        }

        public FontFile GetFileZero()
        {
            throw NotSupported;
        }

        public void AddRef()
        {
            throw NotSupported;
        }

        public void Release()
        {
            throw NotSupported;
        }

        public void GetDesignGlyphMetrics(
            ushort* glyphIndices,
            uint glyphCount,
            GlyphMetrics* glyphMetrics
            )
        {
            throw NotSupported;
        }

        public void GetDisplayGlyphMetrics(
            ushort* glyphIndices,
            uint glyphCount,
            GlyphMetrics* glyphMetrics,
            float emSize,
            bool useDisplayNatural,
            bool isSideways,
            float pixelsPerDip
            )
        {
            throw NotSupported;
        }

        public void GetArrayOfGlyphIndices(
            uint* codePoints,
            uint glyphCount,
            ushort* glyphIndices
            )
        {
            throw NotSupported;
        }

        public bool TryGetFontTable(OpenTypeTableTag openTypeTableTag, out byte[] tableData)
        {
            throw NotSupported;
        }

        public bool ReadFontEmbeddingRights(out ushort fsType)
        {
            throw NotSupported;
        }
    }

    public class FontList : IEnumerable<Font>
    {
        private static readonly PlatformNotSupportedException NotSupported =
            new PlatformNotSupportedException("DirectWrite text/font shaping is not available on this platform.");

        public unsafe FontList(Native.IDWriteFactory* fontList) { }

        public Font this[uint index]
        {
            get { throw NotSupported; }
        }

        public uint Count
        {
            get { throw NotSupported; }
        }

        public FontCollection FontsCollection
        {
            get { throw NotSupported; }
        }

        public virtual IEnumerator<Font> GetEnumerator()
        {
            throw NotSupported;
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }
    }

    public sealed class FontFamily : FontList
    {
        public unsafe FontFamily(Native.IDWriteFactory* fontFamily) : base(fontFamily) { }

        public LocalizedStrings FamilyNames
        {
            get { throw new PlatformNotSupportedException("DirectWrite text/font shaping is not available on this platform."); }
        }

        public bool IsPhysical
        {
            get { throw new PlatformNotSupportedException("DirectWrite text/font shaping is not available on this platform."); }
        }

        public bool IsComposite
        {
            get { throw new PlatformNotSupportedException("DirectWrite text/font shaping is not available on this platform."); }
        }

        public string OrdinalName
        {
            get { throw new PlatformNotSupportedException("DirectWrite text/font shaping is not available on this platform."); }
        }

        public new FontMetrics Metrics
        {
            get { throw new PlatformNotSupportedException("DirectWrite text/font shaping is not available on this platform."); }
        }

        public new FontMetrics DisplayMetrics(float emSize, float pixelsPerDip)
        {
            throw new PlatformNotSupportedException("DirectWrite text/font shaping is not available on this platform.");
        }

        public Font GetFirstMatchingFont(FontWeight weight, FontStretch stretch, FontStyle style)
        {
            throw new PlatformNotSupportedException("DirectWrite text/font shaping is not available on this platform.");
        }

        public FontList GetMatchingFonts(FontWeight weight, FontStretch stretch, FontStyle style)
        {
            throw new PlatformNotSupportedException("DirectWrite text/font shaping is not available on this platform.");
        }
    }

    public sealed unsafe class FontCollection
    {
        private static readonly PlatformNotSupportedException NotSupported =
            new PlatformNotSupportedException("DirectWrite text/font shaping is not available on this platform.");

        public FontCollection(Native.IDWriteFontCollection* fontCollection) { }

        public uint FamilyCount
        {
            get { throw NotSupported; }
        }

        public FontFamily this[uint familyIndex]
        {
            get { throw NotSupported; }
        }

        public FontFamily this[string familyName]
        {
            get { throw NotSupported; }
        }

        public bool FindFamilyName(string familyName, out uint index)
        {
            throw NotSupported;
        }

        public Font GetFontFromFontFace(FontFace fontFace)
        {
            throw NotSupported;
        }
    }

    public sealed unsafe class FontFile : IDisposable
    {
        private static readonly PlatformNotSupportedException NotSupported =
            new PlatformNotSupportedException("DirectWrite text/font shaping is not available on this platform.");

        public FontFile(Native.IDWriteFontFile* fontFile) { }

        public Native.IDWriteFontFile* DWriteFontFileNoAddRef
        {
            get { throw NotSupported; }
        }

        public bool Analyze(
            out Native.DWRITE_FONT_FILE_TYPE dwriteFontFileType,
            out Native.DWRITE_FONT_FACE_TYPE dwriteFontFaceType,
            out uint numberOfFaces,
            int* hr
            )
        {
            throw NotSupported;
        }

        public string GetUriPath()
        {
            throw NotSupported;
        }

        public void Dispose()
        {
        }
    }
}
