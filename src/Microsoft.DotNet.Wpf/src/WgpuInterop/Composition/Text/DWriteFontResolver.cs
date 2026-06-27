// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Resolves the native IDWriteFont pointer carried by a WPF glyph run to a managed
// font that can produce glyph outlines. WPF shapes text with DirectWrite and sends
// only an AddRef'd IDWriteFont* (plus already-shaped glyph indices); to render the
// run we walk IDWriteFont -> IDWriteFontFace -> IDWriteFontFile -> local file path,
// load the .ttf with TrueTypeFont, and cache it per font pointer.
//
// Windows-only (it is used by the HWND sink, which is itself Windows-specific). On
// any failure (collection fonts, CFF/OTF outlines, non-local fonts) it returns null
// and the glyph run is simply skipped.
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Microsoft.Wpf.Interop.WebGpu.Composition.Text
{
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    internal sealed class DWriteFontResolver
    {
        private readonly Dictionary<ulong, IGlyphOutlineFont?> _cache = new();

        /// <summary>Resolve an IDWriteFont* to a glyph-outline font (cached; null on failure).</summary>
        public IGlyphOutlineFont? Resolve(ulong pIDWriteFont)
        {
            if (pIDWriteFont == 0) return null;
            if (_cache.TryGetValue(pIDWriteFont, out IGlyphOutlineFont? cached)) return cached;

            IGlyphOutlineFont? font = null;
            try { font = Load(pIDWriteFont); if (font != null) Log($"resolved font 0x{pIDWriteFont:x}"); }
            catch (Exception ex) { Log($"font resolve failed 0x{pIDWriteFont:x}: {ex.GetType().Name}: {ex.Message}"); font = null; }
            _cache[pIDWriteFont] = font;
            return font;
        }

        private static readonly string? s_log = Environment.GetEnvironmentVariable("WPF_WEBGPU_SINK_LOG");
        private static void Log(string m)
        {
            if (s_log != null) try { File.AppendAllText(s_log, m + Environment.NewLine); } catch { }
        }

        private static IGlyphOutlineFont? Load(ulong pIDWriteFont)
        {
            object o = Marshal.GetObjectForIUnknown((IntPtr)pIDWriteFont);
            var font = (IDWriteFont)o;
            font.CreateFontFace(out IDWriteFontFace face);

            uint numberOfFiles = 0;
            face.GetFiles(ref numberOfFiles, IntPtr.Zero);   // first call: query the file count
            if (numberOfFiles == 0) return null;
            var files = new IntPtr[numberOfFiles];
            GCHandle gch = GCHandle.Alloc(files, GCHandleType.Pinned);
            try { face.GetFiles(ref numberOfFiles, gch.AddrOfPinnedObject()); }
            finally { gch.Free(); }

            try
            {
                if (files[0] == IntPtr.Zero) return null;
                var file = (IDWriteFontFile)Marshal.GetObjectForIUnknown(files[0]);
                file.GetReferenceKey(out IntPtr key, out uint keySize);
                file.GetLoader(out IDWriteFontFileLoader loader);

                if (loader is not IDWriteLocalFontFileLoader local) return null;   // non-local font
                local.GetFilePathLengthFromKey(key, keySize, out uint len);
                var sb = new StringBuilder((int)len + 1);
                local.GetFilePathFromKey(key, keySize, sb, len + 1);
                string path = sb.ToString();

                if (!File.Exists(path)) return null;
                byte[] bytes = File.ReadAllBytes(path);
                // TrueTypeFont parses a single sfnt (.ttf); skip TrueType Collections.
                if (bytes.Length >= 4 && bytes[0] == (byte)'t' && bytes[1] == (byte)'t' &&
                    bytes[2] == (byte)'c' && bytes[3] == (byte)'f')
                    return null;
                return new TrueTypeFont(bytes);
            }
            finally
            {
                foreach (IntPtr p in files) if (p != IntPtr.Zero) Marshal.Release(p);
            }
        }

        // ---- minimal DirectWrite COM interop (only the methods we call) -------------

        [ComImport, Guid("acd16696-8c14-4f5d-877e-fe3fc1d32737"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDWriteFont
        {
            void Reserved1(); void Reserved2(); void Reserved3(); void Reserved4(); void Reserved5();
            void Reserved6(); void Reserved7(); void Reserved8(); void Reserved9(); void Reserved10();
            void CreateFontFace(out IDWriteFontFace fontFace);
        }

        [ComImport, Guid("5f49804d-7024-4d43-bfa9-d25984f53849"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDWriteFontFace
        {
            void Reserved1_GetType();
            void GetFiles(ref uint numberOfFiles, IntPtr fontFiles);
            [PreserveSig] uint GetIndex();
        }

        [ComImport, Guid("739d886a-cef5-47dc-8769-1a8b41bebbb0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDWriteFontFile
        {
            void GetReferenceKey(out IntPtr referenceKey, out uint referenceKeySize);
            void GetLoader(out IDWriteFontFileLoader fontFileLoader);
        }

        [ComImport, Guid("727cad4e-d6af-4c9e-8a08-d695b11caa49"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDWriteFontFileLoader
        {
            void Reserved1_CreateStreamFromKey();
        }

        [ComImport, Guid("b2d9f3ec-c9fe-4a11-a2ec-d86208f7c0a2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDWriteLocalFontFileLoader
        {
            void Reserved1_CreateStreamFromKey();
            void GetFilePathLengthFromKey(IntPtr fontFileReferenceKey, uint fontFileReferenceKeySize, out uint filePathLength);
            void GetFilePathFromKey(IntPtr fontFileReferenceKey, uint fontFileReferenceKeySize,
                [MarshalAs(UnmanagedType.LPWStr)] StringBuilder filePath, uint filePathSize);
        }
    }
}
