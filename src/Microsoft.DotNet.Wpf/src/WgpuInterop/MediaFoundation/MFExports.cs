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
                    a.Attributes.Set(MF.MFT_OUTPUT_TYPES_Attributes, MFValue.Of(TypeInfos(MFAudioDecoder.MFMediaType_Audio, info.Codec == AudioCodec.AAC ? new[] { MediaFormats.Float, MediaFormats.PCM } : info.Codec == AudioCodec.Opus ? new[] { MediaFormats.Float } : new[] { MediaFormats.PCM })));
                    found.Add(a);
                }
            // the encoders, of what the system encodes
            if (sync && category == MFEncoder.MFT_CATEGORY_VIDEO_ENCODER
                && (input == null || (input->guidMajorType == MF.MFMediaType_Video || input->guidMajorType == Guid.Empty) && (input->guidSubtype == Guid.Empty || Array.IndexOf(MFH264Encoder.Inputs, input->guidSubtype) >= 0))
                && (output == null || output->guidSubtype == Guid.Empty || output->guidSubtype == MF.MFVideoFormat_H264)
                && PlatformVideoEncoder.Supports(VideoCodec.H264))
            {
                var a = new MFActivate(() => new MFH264Encoder());
                a.Attributes.Set(MF.MFT_FRIENDLY_NAME_Attribute, MFValue.Of(MFH264Encoder.Name));
                a.Attributes.Set(MF.MFT_TRANSFORM_CLSID_Attribute, MFH264Encoder.Clsid);
                a.Attributes.Set(MF.MFT_INPUT_TYPES_Attributes, MFValue.Of(TypeInfos(MFH264Encoder.Inputs)));
                a.Attributes.Set(MF.MFT_OUTPUT_TYPES_Attributes, MFValue.Of(TypeInfos(new[] { MF.MFVideoFormat_H264 })));
                found.Add(a);
            }
            if (sync && category == MFEncoder.MFT_CATEGORY_AUDIO_ENCODER
                && (input == null || (input->guidMajorType == MFAudioDecoder.MFMediaType_Audio || input->guidMajorType == Guid.Empty) && (input->guidSubtype == Guid.Empty || input->guidSubtype == MediaFormats.PCM))
                && (output == null || output->guidSubtype == Guid.Empty || output->guidSubtype == Source.SR.MFAudioFormat_AAC)
                && PlatformAudioEncoder.Supports(AudioCodec.AAC))
            {
                var a = new MFActivate(() => new MFAacEncoder());
                a.Attributes.Set(MF.MFT_FRIENDLY_NAME_Attribute, MFValue.Of(MFAacEncoder.Name));
                a.Attributes.Set(MF.MFT_TRANSFORM_CLSID_Attribute, MFAacEncoder.Clsid);
                a.Attributes.Set(MF.MFT_INPUT_TYPES_Attributes, MFValue.Of(TypeInfos(MFAudioDecoder.MFMediaType_Audio, new[] { MediaFormats.PCM })));
                a.Attributes.Set(MF.MFT_OUTPUT_TYPES_Attributes, MFValue.Of(TypeInfos(MFAudioDecoder.MFMediaType_Audio, new[] { Source.SR.MFAudioFormat_AAC })));
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
            if (colon > 1) { hr = Source.SR.MF_E_UNSUPPORTED_SCHEME; D3DLog.Once("mf-scheme:" + url[..colon], $"Media Foundation: the source reader reads files and http(s): URLs, not {url[..colon]}: ones"); return null; }
            return url;
        }

        /// <summary>The bytes of a URL: an http(s): one as Windows' network byte stream reads it, else a file's.</summary>
        internal static int OpenUrl(string text, out Source.IByteSource? src)
        {
            src = null;
            int hr;
            if (Uri.TryCreate(text, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps))
            {
                hr = Source.HttpByteSource.Open(u, out var web);
                if (hr < 0) return hr;
                src = web!;
                return MF.S_OK;
            }
            string? path = PathOf(text, out hr);
            if (path == null) return hr;
            try { src = new Source.FileByteSource(path); }
            catch (Exception e) { return Source.MFFileByteStream.Hr(e); }
            return MF.S_OK;
        }

        [Win32Export("MFCreateSourceReaderFromURL")]
        public static int MFCreateSourceReaderFromURL(char* url, IntPtr attributes, IntPtr* reader)
        {
            if (reader == null || url == null) return MF.E_POINTER;
            *reader = IntPtr.Zero;
            int hr = OpenUrl(new string(url), out var src);
            if (hr < 0) return hr;
            hr = Source.MFSourceReader.Create(src!, attributes, out var r);
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
                src = ComObject.From<Source.MFFileByteStream>(byteStream) is { Path: { } fp } ? new Source.FileByteSource(fp) : new Source.ForeignByteSource(byteStream);
            }
            catch (Exception e) { return Source.MFFileByteStream.Hr(e); }
            int hr = Source.MFSourceReader.Create(src, attributes, out var r);
            if (hr >= 0) *reader = r!.Pointer;
            return hr;
        }

        /// <summary>MF_FILE_ACCESSMODE: read 1, write 2, both 3. MF_FILE_OPENMODE: fail if none 0, fail if one 1, reset 2, append 3, delete 4.</summary>
        // ---- what media sources are described and talk by (MFSourceObjects.cs) --------------------------------

        [Win32Export("MFCreateEventQueue")]
        public static int MFCreateEventQueue(IntPtr* queue)
        {
            if (queue == null) return MF.E_POINTER;
            *queue = new MFEventQueue().Pointer;
            return MF.S_OK;
        }

        [Win32Export("MFCreateMediaEvent")]
        public static int MFCreateMediaEvent(uint type, Guid* extended, int status, byte* value, IntPtr* e)
        {
            if (e == null) return MF.E_POINTER;
            *e = new MFMediaEvent(type, extended == null ? Guid.Empty : *extended, status, MFMediaEvent.ValueOf(value)).Pointer;
            return MF.S_OK;
        }

        [Win32Export("MFCreateStreamDescriptor")]
        public static int MFCreateStreamDescriptor(uint id, uint count, IntPtr* types, IntPtr* descriptor)
        {
            if (descriptor == null || types == null && count > 0) return MF.E_POINTER;
            *descriptor = IntPtr.Zero;
            if (count == 0) return MF.E_INVALIDARG;
            var list = new List<MFMediaType>();
            for (uint i = 0; i < count; i++)
            {
                if (types[i] == IntPtr.Zero) { foreach (var t in list) t.Release(); return MF.E_POINTER; }
                // the application's types themselves where they are the shim's; copies of others'
                if (ComObject.From<MFMediaType>(types[i]) is { } ours) { ours.AddRef(); list.Add(ours); }
                else list.Add(Source.Mp4Types.Read(types[i]));
            }
            *descriptor = new MFStreamDescriptor(id, list).Pointer;
            return MF.S_OK;
        }

        [Win32Export("MFCreatePresentationDescriptor")]
        public static int MFCreatePresentationDescriptor(uint count, IntPtr* descriptors, IntPtr* pd)
        {
            if (pd == null || descriptors == null && count > 0) return MF.E_POINTER;
            *pd = IntPtr.Zero;
            var p = new MFPresentationDescriptor();
            for (uint i = 0; i < count; i++)
            {
                if (descriptors[i] == IntPtr.Zero) { p.Release(); return MF.E_POINTER; }
                p.Add(descriptors[i]);
            }
            *pd = p.Pointer;
            return MF.S_OK;
        }

        /// <summary>The capture devices: none yet (camera and microphone capture are to come), as of a system without any.</summary>
        [Win32Export("MFEnumDeviceSources")]
        public static int MFEnumDeviceSources(IntPtr attributes, IntPtr** activates, uint* count)
        {
            if (activates == null || count == null) return MF.E_POINTER;
            *activates = null; *count = 0;
            D3DLog.Once("mf-capture", "Media Foundation: MFEnumDeviceSources lists no devices yet (capture is to come)");
            return MF.S_OK;
        }

        [Win32Export("MFCreateSourceResolver")]
        public static int MFCreateSourceResolver(IntPtr* resolver)
        {
            if (resolver == null) return MF.E_POINTER;
            *resolver = new Source.MFSourceResolver().Pointer;
            return MF.S_OK;
        }

        /// <summary>
        /// A source reader of a media source: of the shim's own, over its file (shut down with the reader unless
        /// MF_SOURCE_READER_DISCONNECT_MEDIASOURCE_ON_SHUTDOWN); of the application's, through its streams' events.
        /// </summary>
        [Win32Export("MFCreateSourceReaderFromMediaSource")]
        public static int MFCreateSourceReaderFromMediaSource(IntPtr source, IntPtr attributes, IntPtr* reader)
        {
            if (reader == null || source == IntPtr.Zero) return MF.E_POINTER;
            *reader = IntPtr.Zero;
            bool disconnect = false;
            if (attributes != IntPtr.Zero)
            {
                var k = Source.MS.MF_SOURCE_READER_DISCONNECT_MEDIASOURCE_ON_SHUTDOWN;
                uint v;
                disconnect = ((delegate* unmanaged<IntPtr, Guid*, uint*, int>)Com.Fn(attributes, 7))(attributes, &k, &v) >= 0 && v != 0;
            }
            try
            {
                Source.ReaderSource rs;
                if (ComObject.From<Source.MFMediaSource>(source) is { } ours && !ForceForeignSource)
                {
                    if (ours.IsShutdown) return MF.MF_E_SHUTDOWN;
                    rs = new Source.ContainerSource(ours.Container, ours, disconnect);
                }
                else
                {
                    int hr = Source.ForeignSource.Create(source, disconnect, out var fs);
                    if (hr < 0) return hr;
                    rs = fs!;
                }
                *reader = Source.MFSourceReader.Over(rs, attributes).Pointer;
                return MF.S_OK;
            }
            catch (Exception e) { D3DLog.Error("MFCreateSourceReaderFromMediaSource", e); return MF.E_FAIL; }
        }

        /// <summary>Tests: the shim's own sources read as the application's are (of the thread).</summary>
        [ThreadStatic] internal static bool ForceForeignSource;

        private static readonly Guid MF_TRANSCODE_CONTAINERTYPE = new("150ff23f-4abc-478b-ac4f-e1916fba1cca"), MFTranscodeContainerType_MPEG4 = new("dc6cd05d-b9d0-40ef-bd35-fa622c1ab28a");

        /// <summary>
        /// A sink writer of MP4 (Sink/): of the URL's path, or of a byte stream -- the shim's own file, or the application's (written
        /// through its IMFByteStream). As Windows': of a byte stream, without a URL the container is MF_TRANSCODE_CONTAINERTYPE's, and
        /// without either the answer is E_INVALIDARG; MPEG-4 by the container type, else by the URL's extension.
        /// </summary>
        [Win32Export("MFCreateSinkWriterFromURL")]
        public static int MFCreateSinkWriterFromURL(char* url, IntPtr byteStream, IntPtr attributes, IntPtr* writer)
        {
            if (writer == null) return MF.E_POINTER;
            *writer = IntPtr.Zero;
            try
            {
                Guid container = Guid.Empty;
                if (attributes != IntPtr.Zero)
                {
                    var k = MF_TRANSCODE_CONTAINERTYPE; Guid g;
                    if (((delegate* unmanaged<IntPtr, Guid*, Guid*, int>)Com.Fn(attributes, 10))(attributes, &k, &g) >= 0) container = g;
                }
                string? text = url != null ? new string(url) : null;
                if (container == Guid.Empty)
                {
                    if (text == null) return MF.E_INVALIDARG;
                    string ext = System.IO.Path.GetExtension(text).ToLowerInvariant();
                    if (ext is not (".mp4" or ".m4v" or ".m4a" or ".mov" or ".3gp")) { D3DLog.Once("mf-sink-ext:" + ext, $"Media Foundation: the sink writer writes MP4 files, not {ext}"); return SR_MF_E_UNSUPPORTED_FORMAT; }
                }
                else if (container != MFTranscodeContainerType_MPEG4) { D3DLog.Once("mf-sink-container:" + container, $"Media Foundation: the sink writer writes MPEG-4, not the container {container}"); return SR_MF_E_UNSUPPORTED_FORMAT; }
                System.IO.Stream target;
                if (byteStream != IntPtr.Zero)
                {
                    if (ComObject.From<Source.MFFileByteStream>(byteStream) is { Path: { } fsp }) target = new System.IO.FileStream(fsp, System.IO.FileMode.Create, System.IO.FileAccess.ReadWrite, System.IO.FileShare.Read);
                    else target = new Sink.ByteStreamWriter(byteStream);
                }
                else
                {
                    string? path = PathOf(text!, out int hr);
                    if (path == null) return hr;
                    target = new System.IO.FileStream(path, System.IO.FileMode.Create, System.IO.FileAccess.ReadWrite, System.IO.FileShare.Read);
                }
                *writer = new Sink.MFSinkWriter(target).Pointer;
                return MF.S_OK;
            }
            catch (Exception e) { return Source.MFFileByteStream.Hr(e); }
        }

        private const int SR_MF_E_UNSUPPORTED_FORMAT = unchecked((int)0xC00D36C2);

        /// <summary>An IMFByteStream over a COM IStream of the application's.</summary>
        [Win32Export("MFCreateMFByteStreamOnStream")]
        public static int MFCreateMFByteStreamOnStream(IntPtr stream, IntPtr* byteStream)
        {
            if (stream == IntPtr.Zero || byteStream == null) return MF.E_POINTER;
            *byteStream = new Source.MFFileByteStream(new Source.ComStream(stream), null).Pointer;
            return MF.S_OK;
        }

        /// <summary>A byte stream of a temporary file of its own, deleted when it is closed.</summary>
        [Win32Export("MFCreateTempFile")]
        public static int MFCreateTempFile(uint access, uint open, uint flags, IntPtr* byteStream)
        {
            if (byteStream == null) return MF.E_POINTER;
            *byteStream = IntPtr.Zero;
            try
            {
                string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "MFT" + Guid.NewGuid().ToString("N")[..8] + ".tmp");
                var fs = new System.IO.FileStream(path, System.IO.FileMode.CreateNew, System.IO.FileAccess.ReadWrite, System.IO.FileShare.Read, 65536, System.IO.FileOptions.DeleteOnClose);
                *byteStream = new Source.MFFileByteStream(fs, null).Pointer;
                return MF.S_OK;
            }
            catch (Exception e) { return Source.MFFileByteStream.Hr(e); }
        }

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
