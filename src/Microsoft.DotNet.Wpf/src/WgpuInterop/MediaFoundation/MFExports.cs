// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Media Foundation off Windows: mfplat's exports, as the P/Invoke rewriter reaches them
// (Documentation/pinvoke-rewriter.md). Off Windows only: Windows has its own Media Foundation, which the
// shim's Direct3D 11 device serves.
//

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Wpf.Interop.WebGpu.Direct3D;
using Microsoft.Wpf.Interop.WebGpu.MediaFoundation.Codecs;

namespace Microsoft.Wpf.Interop.WebGpu.MediaFoundation
{
    /// <summary>MFT_REGISTER_TYPE_INFO.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MFT_REGISTER_TYPE_INFO { public Guid guidMajorType; public Guid guidSubtype; }

    public static unsafe class MFExports
    {
        [Win32Export("MFStartup")] public static int MFStartup(uint version, uint flags) => MF.S_OK;
        [Win32Export("MFShutdown")] public static int MFShutdown() => MF.S_OK;
        [Win32Export("MFLockPlatform")] public static int MFLockPlatform() => MF.S_OK;
        [Win32Export("MFUnlockPlatform")] public static int MFUnlockPlatform() => MF.S_OK;

        [Win32Export("MFCreateAttributes")]
        public static int MFCreateAttributes(IntPtr* attributes, uint initialSize)
        {
            if (attributes == null) return MF.E_POINTER;
            *attributes = new MFAttributeBag().Pointer;
            return MF.S_OK;
        }

        [Win32Export("MFCreateMediaType")]
        public static int MFCreateMediaType(IntPtr* type)
        {
            if (type == null) return MF.E_POINTER;
            *type = new MFMediaType().Pointer;
            return MF.S_OK;
        }

        [Win32Export("MFCreateSample")]
        public static int MFCreateSample(IntPtr* sample)
        {
            if (sample == null) return MF.E_POINTER;
            *sample = new MFSample().Pointer;
            return MF.S_OK;
        }

        [Win32Export("MFCreateMemoryBuffer")]
        public static int MFCreateMemoryBuffer(uint max, IntPtr* buffer)
        {
            if (buffer == null) return MF.E_POINTER;
            *buffer = new MFMemoryBuffer(max).Pointer;
            return MF.S_OK;
        }

        /// <summary>The alignment is a mask (MF_16_BYTE_ALIGNMENT is 15).</summary>
        [Win32Export("MFCreateAlignedMemoryBuffer")]
        public static int MFCreateAlignedMemoryBuffer(uint max, uint alignment, IntPtr* buffer)
        {
            if (buffer == null) return MF.E_POINTER;
            *buffer = new MFMemoryBuffer(max, alignment).Pointer;
            return MF.S_OK;
        }

        [Win32Export("MFCreateDXGISurfaceBuffer")]
        public static int MFCreateDXGISurfaceBuffer(Guid* riid, IntPtr surface, uint subresource, int bottomUp, IntPtr* buffer)
        {
            if (buffer == null || surface == IntPtr.Zero) return MF.E_POINTER;
            *buffer = IntPtr.Zero;
            if (ComObject.From<D3DTexture2D>(surface) == null) return MF.E_INVALIDARG;
            try { *buffer = new MFDxgiBuffer(surface, subresource).Pointer; }
            catch (Exception e) { D3DLog.Error("MFCreateDXGISurfaceBuffer", e); return MF.E_FAIL; }
            return MF.S_OK;
        }

        [Win32Export("MFCreateDXGIDeviceManager")]
        public static int MFCreateDXGIDeviceManager(uint* token, IntPtr* manager)
        {
            if (token == null || manager == null) return MF.E_POINTER;
            var m = new MFDeviceManager();
            *token = m.Token;
            *manager = m.Pointer;
            return MF.S_OK;
        }

        /// <summary>
        /// The decoders of the category whose types match, as IMFActivate objects in an array of CoTaskMem:
        /// the shim's (synchronous) decoders of what this system decodes. MFT_ENUM_FLAG: SYNCMFT 1, and 0
        /// meaning the default set (SYNCMFT | LOCALMFT | SORTANDFILTER).
        /// </summary>
        [Win32Export("MFTEnumEx")]
        public static int MFTEnumEx(Guid category, uint flags, MFT_REGISTER_TYPE_INFO* input, MFT_REGISTER_TYPE_INFO* output, IntPtr** activates, uint* count)
        {
            if (activates == null || count == null) return MF.E_POINTER;
            *activates = null;
            *count = 0;
            var found = new List<MFActivate>();
            bool sync = flags == 0 || (flags & 1) != 0;
            if (sync && category == MF.MFT_CATEGORY_VIDEO_DECODER)
                foreach (var info in MFVideoDecoder.Registered)
                {
                    if (input != null && (input->guidMajorType != MF.MFMediaType_Video && input->guidMajorType != Guid.Empty || input->guidSubtype != Guid.Empty && Array.IndexOf(info.Inputs, input->guidSubtype) < 0)) continue;
                    if (output != null && output->guidSubtype != Guid.Empty && output->guidSubtype != MF.MFVideoFormat_NV12 && output->guidSubtype != MF.MFVideoFormat_YV12
                        && output->guidSubtype != MF.MFVideoFormat_IYUV && output->guidSubtype != MF.MFVideoFormat_I420 && output->guidSubtype != MF.MFVideoFormat_YUY2) continue;
                    if (!PlatformVideoDecoder.Supports(info.Codec)) continue;
                    var a = new MFActivate(() => new MFVideoDecoder(info));
                    a.Attributes.Set(MF.MFT_FRIENDLY_NAME_Attribute, MFValue.Of(info.Name));
                    a.Attributes.Set(MF.MFT_TRANSFORM_CLSID_Attribute, info.Clsid);
                    a.Attributes.Set(MF.MFT_INPUT_TYPES_Attributes, MFValue.Of(TypeInfos(info.Inputs)));
                    a.Attributes.Set(MF.MFT_OUTPUT_TYPES_Attributes, MFValue.Of(TypeInfos(new[] { MF.MFVideoFormat_NV12, MF.MFVideoFormat_YV12, MF.MFVideoFormat_IYUV, MF.MFVideoFormat_I420, MF.MFVideoFormat_YUY2 })));
                    found.Add(a);
                }
            if (sync && category == MFAudioDecoder.MFT_CATEGORY_AUDIO_DECODER)
                foreach (var info in MFAudioDecoder.Registered)
                {
                    if (!info.Listed) continue;
                    if (input != null && (input->guidMajorType != MFAudioDecoder.MFMediaType_Audio && input->guidMajorType != Guid.Empty || input->guidSubtype != Guid.Empty && Array.IndexOf(info.Inputs, input->guidSubtype) < 0)) continue;
                    if (output != null && output->guidSubtype != Guid.Empty && output->guidSubtype != MediaFormats.PCM && output->guidSubtype != MediaFormats.Float) continue;
                    if (!PlatformAudioDecoder.Supports(info.Codec)) continue;
                    var a = new MFActivate(() => new MFAudioDecoder(info));
                    a.Attributes.Set(MF.MFT_FRIENDLY_NAME_Attribute, MFValue.Of(info.Name));
                    a.Attributes.Set(MF.MFT_TRANSFORM_CLSID_Attribute, info.Clsid);
                    a.Attributes.Set(MF.MFT_INPUT_TYPES_Attributes, MFValue.Of(TypeInfos(MFAudioDecoder.MFMediaType_Audio, info.Inputs)));
                    // what Windows' decoders say they give: AAC's float and PCM, the others' PCM
                    a.Attributes.Set(MF.MFT_OUTPUT_TYPES_Attributes, MFValue.Of(TypeInfos(MFAudioDecoder.MFMediaType_Audio, info.Codec is AudioCodec.AAC or AudioCodec.Opus ? new[] { MediaFormats.Float, MediaFormats.PCM } : new[] { MediaFormats.PCM })));
                    found.Add(a);
                }
            if (found.Count == 0) return MF.S_OK;
            var array = (IntPtr*)Marshal.AllocCoTaskMem(found.Count * IntPtr.Size);
            for (int i = 0; i < found.Count; i++) array[i] = found[i].Pointer;
            *activates = array;
            *count = (uint)found.Count;
            return MF.S_OK;
        }

        private static byte[] TypeInfos(Guid[] subtypes) => TypeInfos(MF.MFMediaType_Video, subtypes);

        private static byte[] TypeInfos(Guid major, Guid[] subtypes)
        {
            var b = new byte[subtypes.Length * 32];
            for (int i = 0; i < subtypes.Length; i++)
            {
                major.TryWriteBytes(b.AsSpan(i * 32));
                subtypes[i].TryWriteBytes(b.AsSpan(i * 32 + 16));
            }
            return b;
        }

        // ---- the source reader (Source/) -----------------------------------------------------------------

        /// <summary>A file's path from a URL: a path as it is, or a file: URL.</summary>
        private static string? PathOf(string url, out int hr)
        {
            hr = MF.S_OK;
            if (url.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            {
                if (Uri.TryCreate(url, UriKind.Absolute, out var u) && u.IsFile) return u.LocalPath;
                hr = MF.E_INVALIDARG;
                return null;
            }
            int colon = url.IndexOf("://", StringComparison.Ordinal);
            if (colon > 1) { hr = Source.SR.MF_E_UNSUPPORTED_SCHEME; D3DLog.Once("mf-scheme:" + url[..colon], $"Media Foundation: the source reader reads files only, not {url[..colon]}: URLs"); return null; }
            return url;
        }

        [Win32Export("MFCreateSourceReaderFromURL")]
        public static int MFCreateSourceReaderFromURL(char* url, IntPtr attributes, IntPtr* reader)
        {
            if (reader == null || url == null) return MF.E_POINTER;
            *reader = IntPtr.Zero;
            string? path = PathOf(new string(url), out int hr);
            if (path == null) return hr;
            Source.IByteSource src;
            try { src = new Source.FileByteSource(path); }
            catch (Exception e) { return Source.MFFileByteStream.Hr(e); }
            hr = Source.MFSourceReader.Create(src, attributes, out var r);
            if (hr >= 0) *reader = r!.Pointer;
            return hr;
        }

        [Win32Export("MFCreateSourceReaderFromByteStream")]
        public static int MFCreateSourceReaderFromByteStream(IntPtr byteStream, IntPtr attributes, IntPtr* reader)
        {
            if (reader == null || byteStream == IntPtr.Zero) return MF.E_POINTER;
            *reader = IntPtr.Zero;
            Source.IByteSource src;
            try
            {
                src = ComObject.From<Source.MFFileByteStream>(byteStream) is { } f ? new Source.FileByteSource(f.Path) : new Source.ForeignByteSource(byteStream);
            }
            catch (Exception e) { return Source.MFFileByteStream.Hr(e); }
            int hr = Source.MFSourceReader.Create(src, attributes, out var r);
            if (hr >= 0) *reader = r!.Pointer;
            return hr;
        }

        /// <summary>MF_FILE_ACCESSMODE: read 1, write 2, both 3. MF_FILE_OPENMODE: fail if none 0, fail if one 1, reset 2, append 3, delete 4.</summary>
        [Win32Export("MFCreateFile")]
        public static int MFCreateFile(uint access, uint open, uint flags, char* url, IntPtr* stream)
        {
            if (stream == null || url == null) return MF.E_POINTER;
            *stream = IntPtr.Zero;
            string? path = PathOf(new string(url), out int hr);
            if (path == null) return hr;
            var fa = access switch { 1 => System.IO.FileAccess.Read, 2 => System.IO.FileAccess.Write, _ => System.IO.FileAccess.ReadWrite };
            var fm = open switch { 0 => System.IO.FileMode.Open, 1 => System.IO.FileMode.CreateNew, 2 => System.IO.FileMode.OpenOrCreate, 3 => System.IO.FileMode.OpenOrCreate, 4 => System.IO.FileMode.Create, _ => System.IO.FileMode.Open };
            try
            {
                var fs = new System.IO.FileStream(path, fm, fa, System.IO.FileShare.Read | ((flags & 1) != 0 ? System.IO.FileShare.Write : 0));
                if (open == 2 && fa != System.IO.FileAccess.Read) fs.SetLength(0);
                if (open == 3) fs.Seek(0, System.IO.SeekOrigin.End);
                *stream = new Source.MFFileByteStream(fs, System.IO.Path.GetFullPath(path)).Pointer;
                return MF.S_OK;
            }
            catch (Exception e) { return Source.MFFileByteStream.Hr(e); }
        }

        [Win32Export("MFFrameRateToAverageTimePerFrame")]
        public static int MFFrameRateToAverageTimePerFrame(uint numerator, uint denominator, ulong* average)
        {
            if (average == null) return MF.E_POINTER;
            *average = numerator == 0 ? 0 : (ulong)(10_000_000.0 * denominator / numerator + 0.5);
            return MF.S_OK;
        }

        [Win32Export("MFAverageTimePerFrameToFrameRate")]
        public static int MFAverageTimePerFrameToFrameRate(ulong average, uint* numerator, uint* denominator)
        {
            if (numerator == null || denominator == null) return MF.E_POINTER;
            if (average == 0) { *numerator = 0; *denominator = 0; return MF.S_OK; }
            // the standard rates exactly (29.97 is 30000/1001), else per 100 ns
            foreach (var (n, d) in new (uint, uint)[] { (60000, 1001), (30000, 1001), (24000, 1001), (60, 1), (50, 1), (30, 1), (25, 1), (24, 1) })
                if (Math.Abs(10_000_000.0 * d / n - average) < 1) { *numerator = n; *denominator = d; return MF.S_OK; }
            *numerator = 10_000_000;
            *denominator = (uint)average;
            return MF.S_OK;
        }

        [Win32Export("MFCalculateImageSize")]
        public static int MFCalculateImageSize(Guid* subtype, uint width, uint height, uint* size)
        {
            if (subtype == null || size == null) return MF.E_POINTER;
            Guid s = *subtype;
            if (s == MF.MFVideoFormat_NV12 || s == MF.MFVideoFormat_YV12 || s == MF.MFVideoFormat_IYUV || s == MF.MFVideoFormat_I420) *size = MFVideoDecoder.ImageSize(s, width, height);
            else if (s == MF.MFVideoFormat_YUY2) *size = width * height * 2;
            else if (BytesPerPixel(s) is uint bpp and > 0) *size = ((width * bpp + 3) & ~3u) * height;
            else { *size = 0; return MF.E_INVALIDARG; }
            return MF.S_OK;
        }

        /// <summary>The RGB subtypes' bytes per pixel (RGB32 22, ARGB32 21, RGB24 20, RGB565 23, RGB555 24).</summary>
        private static uint BytesPerPixel(Guid s)
        {
            var b = s.ToByteArray();
            if (!s.ToString().EndsWith("-0000-0010-8000-00aa00389b71", StringComparison.OrdinalIgnoreCase)) return 0;
            uint fourcc = BitConverter.ToUInt32(b, 0);
            return fourcc switch { 21 or 22 => 4, 20 => 3, 23 or 24 => 2, _ => 0 };
        }

        [Win32Export("MFGetStrideForBitmapInfoHeader")]
        public static int MFGetStrideForBitmapInfoHeader(uint format, uint width, int* stride)
        {
            if (stride == null) return MF.E_POINTER;
            // a FOURCC (NV12, YV12, IYUV, I420: the luma's; YUY2 two bytes a pixel) or a D3DFMT (RGB32 22, ARGB 21, RGB24 20)
            *stride = format switch
            {
                0x3231564E or 0x32315659 or 0x56555949 or 0x30323449 => (int)width,
                0x32595559 or 0x59565955 => (int)width * 2,
                21 or 22 => (int)width * 4,
                20 => (int)((width * 3 + 3) & ~3u),
                23 or 24 => (int)width * 2,
                _ => 0,
            };
            return *stride == 0 ? MF.E_INVALIDARG : MF.S_OK;
        }

        [Win32Export("MFCopyImage")]
        public static int MFCopyImage(byte* dst, int dstStride, byte* src, int srcStride, uint widthInBytes, uint lines)
        {
            if (dst == null || src == null) return MF.E_POINTER;
            for (uint r = 0; r < lines; r++) Buffer.MemoryCopy(src + r * srcStride, dst + r * dstStride, widthInBytes, widthInBytes);
            return MF.S_OK;
        }

        /// <summary>What PropVariantClear frees of a PROPVARIANT an attribute was read into.</summary>
        internal static void PropVariantClear(byte* pv)
        {
            ushort vt = *(ushort*)pv;
            switch (vt)
            {
                case MF.VT_CLSID: case MF.VT_LPWSTR: Marshal.FreeCoTaskMem(*(IntPtr*)(pv + 8)); break;
                case MF.VT_BLOB: Marshal.FreeCoTaskMem(*(IntPtr*)(pv + MF.BlobPointerOffset)); break;
                case MF.VT_UNKNOWN: Com.Release(*(IntPtr*)(pv + 8)); break;
            }
            new Span<byte>(pv, MF.PropVariantSize).Clear();
        }
    }
}
