// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Compile-only stub. See DirectWriteForwarderStub.csproj for details.
// Mirrors IFontSource.h, FontFileLoader.h, FontFileStream.h, FontFileEnumerator.h.

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using MS.Internal.Text.TextInterface.Interfaces;

namespace MS.Internal.Text.TextInterface
{
    public interface IFontSource
    {
        void TestFileOpenable();
        UnmanagedMemoryStream GetUnmanagedStream();
        DateTime GetLastWriteTimeUtc();

        Uri Uri { get; }

        bool IsComposite { get; }
    }

    public interface IFontSourceFactory
    {
        IFontSource Create(string uriString);
    }

    [ClassInterface(ClassInterfaceType.None)]
    [ComVisible(true)]
    public unsafe class FontFileLoader : IDWriteFontFileLoaderMirror
    {
        private readonly IFontSourceFactory _fontSourceFactory;

        public FontFileLoader()
        {
            System.Diagnostics.Debug.Fail("Assertion failed");
        }

        public FontFileLoader(IFontSourceFactory fontSourceFactory)
        {
            _fontSourceFactory = fontSourceFactory;
        }

        [ComVisible(true)]
        public int CreateStreamFromKey(void* fontFileReferenceKey, uint fontFileReferenceKeySize, IntPtr* fontFileStream)
        {
            throw new PlatformNotSupportedException("DirectWrite text/font shaping is not available on this platform.");
        }
    }

    [ClassInterface(ClassInterfaceType.None)]
    [ComVisible(true)]
    public sealed unsafe class FontFileStream : Interfaces.IDWriteFontFileStreamMirror, IDisposable
    {
        public FontFileStream()
        {
            System.Diagnostics.Debug.Fail("Assertion failed");
        }

        public FontFileStream(IFontSource fontSource)
        {
        }

        public void Dispose()
        {
        }

        [ComVisible(true)]
        public int ReadFileFragment(void** fragmentStart, ulong fileOffset, ulong fragmentSize, void** fragmentContext)
        {
            throw new PlatformNotSupportedException("DirectWrite text/font shaping is not available on this platform.");
        }

        [ComVisible(true)]
        public void ReleaseFileFragment(void* fragmentContext)
        {
            throw new PlatformNotSupportedException("DirectWrite text/font shaping is not available on this platform.");
        }

        [ComVisible(true)]
        public int GetFileSize(ulong* fileSize)
        {
            throw new PlatformNotSupportedException("DirectWrite text/font shaping is not available on this platform.");
        }

        [ComVisible(true)]
        public int GetLastWriteTime(ulong* lastWriteTime)
        {
            throw new PlatformNotSupportedException("DirectWrite text/font shaping is not available on this platform.");
        }
    }

    [ClassInterface(ClassInterfaceType.None)]
    [ComVisible(true)]
    public sealed unsafe class FontFileEnumerator : IDWriteFontFileEnumeratorMirror
    {
        public FontFileEnumerator()
        {
            System.Diagnostics.Debug.Fail("Assertion failed");
        }

        public FontFileEnumerator(
            IEnumerable<IFontSource> fontSourceCollection,
            FontFileLoader fontFileLoader,
            Native.IDWriteFactory* factory
            )
        {
        }

        [ComVisible(true)]
        public int MoveNext(out bool hasCurrentFile)
        {
            throw new PlatformNotSupportedException("DirectWrite text/font shaping is not available on this platform.");
        }

        [ComVisible(true)]
        public int GetCurrentFontFile(Native.IDWriteFontFile** fontFile)
        {
            throw new PlatformNotSupportedException("DirectWrite text/font shaping is not available on this platform.");
        }
    }
}
