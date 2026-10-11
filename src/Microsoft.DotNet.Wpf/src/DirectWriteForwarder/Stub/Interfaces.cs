// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Compile-only stub. See DirectWriteForwarderStub.csproj for details.
// Mirrors DWriteInterfaces.h (MS::Internal::Text::TextInterface::Interfaces).
//
// Note: IDWriteFontCollectionLoaderMirror is NOT declared here - PresentationCore already
// declares it directly (in the plain MS.Internal.Text.TextInterface namespace, not .Interfaces)
// in MS/public/Text/TextInterface/DWriteInterfaces.cs, and FontCollectionLoader.cs in
// PresentationCore implements it directly. Re-declaring it here would collide.

using System;
using System.Runtime.InteropServices;

namespace MS.Internal.Text.TextInterface.Interfaces
{
    [ComImport]
    [Guid("6d4865fe-0ab8-4d91-8f62-5dd6be34a3e0")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public unsafe interface IDWriteFontFileStreamMirror
    {
        [PreserveSig]
        int ReadFileFragment(
            void** fragmentStart,
            ulong fileOffset,
            ulong fragmentSize,
            void** fragmentContext
            );

        [PreserveSig]
        void ReleaseFileFragment(
            void* fragmentContext
            );

        [PreserveSig]
        int GetFileSize(
            ulong* fileSize
            );

        [PreserveSig]
        int GetLastWriteTime(
            ulong* lastWriteTime
            );
    }

    [ComImport]
    [Guid("727cad4e-d6af-4c9e-8a08-d695b11caa49")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public unsafe interface IDWriteFontFileLoaderMirror
    {
        [PreserveSig]
        int CreateStreamFromKey(
            void* fontFileReferenceKey,
            [MarshalAs(UnmanagedType.U4)] uint fontFileReferenceKeySize,
            IntPtr* fontFileStream
            );
    }

    [ComImport]
    [Guid("72755049-5ff7-435d-8348-4be97cfa6c7c")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public unsafe interface IDWriteFontFileEnumeratorMirror
    {
        [PreserveSig]
        int MoveNext(
            [MarshalAs(UnmanagedType.Bool)] out bool hasCurrentFile
            );

        [PreserveSig]
        int GetCurrentFontFile(
            Native.IDWriteFontFile** fontFile
            );
    }
}
