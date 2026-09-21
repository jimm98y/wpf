// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Compile-only stub. See DirectWriteForwarderStub.csproj for details.
//
// MS.Internal.Text.TextInterface.Native mirrors the namespace that Common.h opens for the
// native DWrite interop declarations (#include "DWrite.h" inside
// "namespace MS { namespace Internal { namespace Text { namespace TextInterface { namespace Native").
// PresentationCore's Factory.cs and FontCollectionLoader.cs use these purely as pointer-cast
// targets (e.g. "(Native.IDWriteFactory*)_factory.Value") when bridging to/from the real
// MS.Internal.Interop.DWrite native structs it defines itself. Since these types are never
// dereferenced through this stub assembly, empty unsafe structs are sufficient to satisfy the
// C# compiler.

namespace MS.Internal.Text.TextInterface.Native
{
    public unsafe struct IDWriteFactory
    {
    }

    public unsafe struct IDWriteFont
    {
    }

    public unsafe struct IDWriteFontFile
    {
    }

    public unsafe struct IDWriteFontFace
    {
    }

    public unsafe struct IDWriteFontCollection
    {
    }

    public unsafe struct IDWriteTextAnalyzer
    {
    }

    public enum DWRITE_FONT_FILE_TYPE
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

    public enum DWRITE_FONT_FACE_TYPE
    {
        CFF,
        TrueType,
        TrueTypeCollection,
        Type1,
        Vector,
        Bitmap,
        Unknown
    }
}
