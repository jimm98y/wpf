// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The source reader (Documentation/media-foundation-shim.md, "The source reader"): IMFSourceReaderEx over
// the MPEG-4 demuxer, in Windows' manner (its behaviour probed: MFSourceReaderProbe, the report in
// SourceReader.windows.txt).
//
//   - streams: the audio tracks first, then the video ones; the first of each kind selected;
//   - native types as Windows' MPEG-4 source makes them; samples as it hands them out (Mp4Demuxer.Read);
//   - a current type the native one, or one the reader decodes to: the shim's decoder MFTs (MFTransform.cs)
//     for NV12, YV12, IYUV, I420 and YUY2, and with MF_SOURCE_READER_ENABLE_(ADVANCED_)VIDEO_PROCESSING the
//     RGB types besides (RGB32, ARGB32, RGB24: the decoder's NV12 converted, BT.601 or BT.709, limited
//     range, chroma nearest -- as Windows' video processor does it);
//   - a decoder's change of size reported as Windows reports it: MF_SOURCE_READERF_CURRENTMEDIATYPECHANGED
//     on the sample, the new type with the picture in its apertures;
//   - MF_SOURCE_READER_D3D_MANAGER: the decoder's own NV12 textures of the manager's device;
//   - synchronous, or asynchronous with MF_SOURCE_READER_ASYNC_CALLBACK (a thread of the reader's own);
//   - seeking to the sync sample at or before the position (in a fragmented file, the fragment's first).
//

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Wpf.Interop.WebGpu.Direct3D;
using Microsoft.Wpf.Interop.WebGpu.MediaFoundation.Codecs;

namespace Microsoft.Wpf.Interop.WebGpu.MediaFoundation.Source
{
    /// <summary>The source reader's GUIDs and results.</summary>
    internal static class SR
    {
        public const int MF_E_INVALIDREQUEST = unchecked((int)0xC00D36B2), MF_E_TOPO_CODEC_NOT_FOUND = unchecked((int)0xC00D5212);
        public const int MF_E_UNSUPPORTED_BYTESTREAM_TYPE = unchecked((int)0xC00D36C4), MF_E_UNSUPPORTED_SCHEME = unchecked((int)0xC00D36C3);
        public const int MF_E_UNSUPPORTED_FORMAT = unchecked((int)0xC00D36C2);
        public const uint FIRST_VIDEO = 0xFFFFFFFC, FIRST_AUDIO = 0xFFFFFFFD, ANY = 0xFFFFFFFE, MEDIASOURCE = 0xFFFFFFFF;
        // MF_SOURCE_READERF
        public const uint ERROR = 1, ENDOFSTREAM = 2, NEWSTREAM = 4, NATIVEMEDIATYPECHANGED = 0x10, CURRENTMEDIATYPECHANGED = 0x20, STREAMTICK = 0x100;

        public static readonly Guid IID_IMFSourceReader = new("70ae66f2-c809-4e4f-8915-bdcb406b7993"), IID_IMFSourceReaderEx = new("7b981cf0-560e-4116-9875-b099895f23d7");
        public static readonly Guid MF_SOURCE_READER_ASYNC_CALLBACK = new("1e3dbeac-bb43-4c35-b507-cd644464c965"), MF_SOURCE_READER_D3D_MANAGER = new("ec822da2-e1e9-4b29-a0d8-563c719f5269");
        public static readonly Guid MF_SOURCE_READER_DISABLE_DXVA = new("aa456cfd-3943-4a1e-a77d-1838c0ea2e35"), MF_SOURCE_READER_ENABLE_VIDEO_PROCESSING = new("fb394f3d-ccf1-42ee-bbb3-f9b845d5681d");
        public static readonly Guid MF_SOURCE_READER_ENABLE_ADVANCED_VIDEO_PROCESSING = new("0f81da2c-b537-4672-a8b2-a681b17307a3"), MF_SOURCE_READER_MEDIASOURCE_CHARACTERISTICS = new("6d23f5c8-c5d7-4a9b-9971-5d11f8bca880");
        public static readonly Guid MF_PD_DURATION = new("6c990d33-bb8e-477a-8598-0d5d96fcd88a"), MF_PD_TOTAL_FILE_SIZE = new("6c990d34-bb8e-477a-8598-0d5d96fcd88a"), MF_PD_MIME_TYPE = new("6c990d37-bb8e-477a-8598-0d5d96fcd88a");
        public static readonly Guid MF_PD_AUDIO_ENCODING_BITRATE = new("6c990d35-bb8e-477a-8598-0d5d96fcd88a"), MF_PD_LAST_MODIFIED_TIME = new("6c990d38-bb8e-477a-8598-0d5d96fcd88a");
        public static readonly Guid MF_SD_LANGUAGE = new("00af2180-bdc2-423c-abca-f503593bc121");
        public static readonly Guid MF_BYTESTREAM_ORIGIN_NAME = new("fc358288-3cb6-460c-a424-b6681260375a"), MF_BYTESTREAM_LAST_MODIFIED_TIME = new("fc35828b-3cb6-460c-a424-b6681260375a");
        public static readonly Guid MFT_CATEGORY_VIDEO_DECODER = new("d6c02d4b-6833-45b4-971a-05a4b04bab91"), MFT_CATEGORY_VIDEO_PROCESSOR = new("302ea3fc-aa5f-47f9-9f7a-c2188bb16302");

        public static readonly Guid MFMediaType_Audio = new("73647561-0000-0010-8000-00aa00389b71");
        public static readonly Guid MFAudioFormat_AAC = new("00001610-0000-0010-8000-00aa00389b71"), MFAudioFormat_MP3 = new("00000055-0000-0010-8000-00aa00389b71");
        public static readonly Guid MFAudioFormat_PCM = new("00000001-0000-0010-8000-00aa00389b71"), MFAudioFormat_Float = new("00000003-0000-0010-8000-00aa00389b71");
        public static readonly Guid MFAudioFormat_FLAC = new("0000f1ac-0000-0010-8000-00aa00389b71"), MFAudioFormat_ALAC = MediaFormats.ALAC;
        public static readonly Guid MFAudioFormat_Opus = new("0000704f-0000-0010-8000-00aa00389b71");
        public static readonly Guid MFAudioFormat_Dolby_AC3 = new("e06d802c-db46-11cf-b4d1-00805f6cbbea"), MFAudioFormat_Dolby_DDPlus = new("a7fb87af-2d02-42fb-a4d4-05cd93843bdd");
        public static readonly Guid MFVideoFormat_RGB32 = new("00000016-0000-0010-8000-00aa00389b71"), MFVideoFormat_ARGB32 = new("00000015-0000-0010-8000-00aa00389b71");
        public static readonly Guid MFVideoFormat_RGB24 = new("00000014-0000-0010-8000-00aa00389b71"), MFVideoFormat_MJPG = MediaFormats.FourCC("MJPG");

        public static readonly Guid MF_MT_AM_FORMAT_TYPE = new("73d1072d-1870-4174-a063-29ff4ff6c11e"), FORMAT_MPEG2Video = new("e06d80e3-db46-11cf-b4d1-00805f6cbbea"), FORMAT_WaveFormatEx = new("05589f81-c356-11ce-bf01-00aa0055595a");
        public static readonly Guid MF_PROGRESSIVE_CODING_CONTENT = new("8f020eea-1508-471f-9da6-507d7cfa40db"), MF_NALU_LENGTH_SET = new("a7911d53-12a4-4965-ae70-6eadd6ff0551");
        public static readonly Guid MF_MT_AVG_BITRATE = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e"), MF_MT_AVG_BIT_ERROR_RATE = new("799cabd6-3508-4db4-a3c7-569cd533deb1");
        public static readonly Guid MF_MT_MPEG2_PROFILE = new("ad76a80b-2d5c-4e0b-b375-64e520137036"), MF_MT_MPEG2_LEVEL = new("96f66574-11c5-4015-8666-bff516436da7");
        public static readonly Guid MF_MT_MPEG4_SAMPLE_DESCRIPTION = new("261e9d83-9529-4b8f-a111-8b9c950a81a9"), MF_MT_MPEG4_CURRENT_SAMPLE_ENTRY = new("9aa7e155-b64a-4c1d-a500-455d600b6560");
        public static readonly Guid MF_MT_VIDEO_ROTATION = new("c380465d-2271-428c-9b83-ecea3b4a85c1"), MF_MT_PAN_SCAN_APERTURE = new("79614dde-9187-48fb-b8c7-4d52689de649");
        public static readonly Guid MF_MT_TRANSFER_FUNCTION = new("5fb0fce9-be5c-4935-a811-ec838f8eed93"), MF_MT_VIDEO_CHROMA_SITING = new("65df2370-c773-4c33-aa64-843e068efb0c");
        public static readonly Guid MF_MT_VIDEO_PRIMARIES = new("dbfbe4d7-0740-4ee0-8192-850ab0e21935");
        public static readonly Guid MF_MT_YUV_MATRIX = new("3e23d450-2c75-4d25-a00e-b91670d12327");
        public static readonly Guid MF_MT_AUDIO_NUM_CHANNELS = new("37e48bf5-645e-4c5b-89de-ada9e29b696a"), MF_MT_AUDIO_SAMPLES_PER_SECOND = new("5faeeae7-0290-4c31-9e8a-c534f68d9dba");
        public static readonly Guid MF_MT_AUDIO_AVG_BYTES_PER_SECOND = new("1aab75c8-cfef-451c-ab95-ac034b8e1731"), MF_MT_AUDIO_BLOCK_ALIGNMENT = new("322de230-9eeb-43bd-ab7a-ff412251541d");
        public static readonly Guid MF_MT_AUDIO_BITS_PER_SAMPLE = new("f2deb57f-40fa-4764-aa33-ed4f2d1ff669"), MF_MT_AUDIO_PREFER_WAVEFORMATEX = new("a901aaba-e037-458a-bdf6-545be2074042");
        public static readonly Guid MF_MT_AAC_PAYLOAD_TYPE = new("bfbabe79-7434-4d1c-94f0-72a3b9e17188"), MF_MT_AAC_AUDIO_PROFILE_LEVEL_INDICATION = new("7632f0e6-9538-4d61-acda-ea29c8c14456");
        public static readonly Guid MFSampleExtension_NALULengthInfo = new("19124e7c-ad4b-465f-bb18-20186287b6af");
    }

    internal sealed unsafe class MFSourceReader : ComObject
    {
        private static readonly Vtable s_vt = Build();
        private readonly ReaderSource _source;
        private readonly List<ReaderStream> _streams = new();
        private readonly object _lock = new();
        private readonly IntPtr _callback;             // IMFSourceReaderCallback, AddRef'd
        private readonly IntPtr _manager;              // IMFDXGIDeviceManager, AddRef'd
        private readonly bool _videoProcessing;
        private bool _shutdown;
        private Thread? _worker;
        private readonly Queue<Action> _work = new();
        private int _pending;

        private static Vtable Build()
        {
            var vt = new Vtable("IMFSourceReaderEx", 17, SR.IID_IMFSourceReaderEx, SR.IID_IMFSourceReader);
            vt.Set(3, (delegate* unmanaged<IntPtr, uint, int*, int>)&GetStreamSelection);
            vt.Set(4, (delegate* unmanaged<IntPtr, uint, int, int>)&SetStreamSelection);
            vt.Set(5, (delegate* unmanaged<IntPtr, uint, uint, IntPtr*, int>)&GetNativeMediaType);
            vt.Set(6, (delegate* unmanaged<IntPtr, uint, IntPtr*, int>)&GetCurrentMediaType);
            vt.Set(7, (delegate* unmanaged<IntPtr, uint, uint*, IntPtr, int>)&SetCurrentMediaType);
            vt.Set(8, (delegate* unmanaged<IntPtr, Guid*, byte*, int>)&SetCurrentPosition);
            vt.Set(9, (delegate* unmanaged<IntPtr, uint, uint, uint*, uint*, long*, IntPtr*, int>)&ReadSample);
            vt.Set(10, (delegate* unmanaged<IntPtr, uint, int>)&Flush);
            vt.Set(11, (delegate* unmanaged<IntPtr, uint, Guid*, Guid*, IntPtr*, int>)&GetServiceForStream);
            vt.Set(12, (delegate* unmanaged<IntPtr, uint, Guid*, byte*, int>)&GetPresentationAttribute);
            vt.Set(13, (delegate* unmanaged<IntPtr, uint, IntPtr, uint*, int>)&SetNativeMediaType);
            vt.Set(14, (delegate* unmanaged<IntPtr, uint, IntPtr, int>)&AddTransformForStream);
            vt.Set(15, (delegate* unmanaged<IntPtr, uint, int>)&RemoveAllTransformsForStream);
            vt.Set(16, (delegate* unmanaged<IntPtr, uint, uint, Guid*, IntPtr*, int>)&GetTransformForStream);
            return vt;
        }

        private MFSourceReader(ReaderSource source, IntPtr attributes) : base(s_vt)
        {
            _source = source;
            if (attributes != IntPtr.Zero)
            {
                _callback = Unknown(attributes, SR.MF_SOURCE_READER_ASYNC_CALLBACK);
                if (U32(attributes, SR.MF_SOURCE_READER_DISABLE_DXVA) == 0) _manager = Unknown(attributes, SR.MF_SOURCE_READER_D3D_MANAGER);
                _videoProcessing = U32(attributes, SR.MF_SOURCE_READER_ENABLE_VIDEO_PROCESSING) != 0 || U32(attributes, SR.MF_SOURCE_READER_ENABLE_ADVANCED_VIDEO_PROCESSING) != 0;
            }
            int index = 0;
            foreach (var (feed, native, selected) in source.Streams())
                _streams.Add(new ReaderStream(this, index++, feed, native) { Selected = selected });
        }

        /// <summary>A reader of the source (its own from here on).</summary>
        public static MFSourceReader Over(ReaderSource source, IntPtr attributes) => new(source, attributes);

        public static int Create(IByteSource src, IntPtr attributes, out MFSourceReader? reader)
        {
            reader = null;
            MediaContainer? d;
            try { d = MediaContainer.Open(src); }
            catch (Exception e) { src.Dispose(); D3DLog.Once("mf-file:" + e.GetType().Name, "Media Foundation: the file is not read: " + e.Message); return SR.MF_E_UNSUPPORTED_FORMAT; }
            if (d == null) { src.Dispose(); return SR.MF_E_UNSUPPORTED_BYTESTREAM_TYPE; }
            reader = new MFSourceReader(new ContainerSource(d), attributes);
            if (reader._streams.Count == 0) D3DLog.Once("mf-mp4-nostreams", "Media Foundation: the MPEG-4 file has no audio or video the source reader reads");
            return MF.S_OK;
        }

        protected override void OnFinalRelease()
        {
            lock (_lock)
            {
                _shutdown = true;
                Monitor.PulseAll(_lock);
            }
            _worker?.Join(2000);
            foreach (var s in _streams) s.Dispose();
            _source.Dispose();
            Com.Release(_callback);
            Com.Release(_manager);
        }

        private static IntPtr Unknown(IntPtr attributes, Guid key)
        {
            var iid = new Guid("00000000-0000-0000-C000-000000000046");
            IntPtr u;
            return ((delegate* unmanaged<IntPtr, Guid*, Guid*, IntPtr*, int>)Com.Fn(attributes, 17))(attributes, &key, &iid, &u) >= 0 ? u : IntPtr.Zero;
        }

        private static uint U32(IntPtr attributes, Guid key) { uint v; return ((delegate* unmanaged<IntPtr, Guid*, uint*, int>)Com.Fn(attributes, 7))(attributes, &key, &v) >= 0 ? v : 0; }

        private static MFSourceReader? R(IntPtr self) => From<MFSourceReader>(self);

        public bool VideoProcessing => _videoProcessing;
        public IntPtr Manager => _manager;

        /// <summary>The stream an index names: a stream's, the first video or audio stream's.</summary>
        private ReaderStream? StreamOf(uint i) => i switch
        {
            SR.FIRST_VIDEO => _streams.FirstOrDefault(s => s.Video),
            SR.FIRST_AUDIO => _streams.FirstOrDefault(s => s.Audio),
            _ => i < _streams.Count ? _streams[(int)i] : null,
        };

        // ---- selection and types ----------------------------------------------------------------------

        [UnmanagedCallersOnly]
        private static int GetStreamSelection(IntPtr self, uint i, int* selected)
        {
            if (R(self) is not { } r || selected == null) return MF.E_POINTER;
            lock (r._lock)
            {
                if (r.StreamOf(i) is not { } s) return MF.MF_E_INVALIDSTREAMNUMBER;
                *selected = s.Selected ? 1 : 0;
            }
            return MF.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int SetStreamSelection(IntPtr self, uint i, int selected)
        {
            if (R(self) is not { } r) return MF.E_POINTER;
            lock (r._lock)
            {
                if (i is SR.ANY or SR.MEDIASOURCE) { foreach (var s in r._streams) { s.Selected = selected != 0; r._source.Select(s.Index, s.Selected); } return MF.S_OK; }
                if (r.StreamOf(i) is not { } st) return MF.MF_E_INVALIDSTREAMNUMBER;
                st.Selected = selected != 0;
                r._source.Select(st.Index, st.Selected);
            }
            return MF.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int GetNativeMediaType(IntPtr self, uint i, uint n, IntPtr* type)
        {
            if (R(self) is not { } r || type == null) return MF.E_POINTER;
            *type = IntPtr.Zero;
            lock (r._lock)
            {
                if (r.StreamOf(i) is not { } s) return MF.MF_E_INVALIDSTREAMNUMBER;
                if (n != 0 && n != 0xFFFFFFFF) return MF.MF_E_NO_MORE_TYPES;           // MF_SOURCE_READER_CURRENT_TYPE_INDEX: the one in use, the only one
                *type = s.Native.Clone().Pointer;
            }
            return MF.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int GetCurrentMediaType(IntPtr self, uint i, IntPtr* type)
        {
            if (R(self) is not { } r || type == null) return MF.E_POINTER;
            *type = IntPtr.Zero;
            lock (r._lock)
            {
                if (r.StreamOf(i) is not { } s) return MF.MF_E_INVALIDSTREAMNUMBER;
                *type = s.Current.Clone().Pointer;
            }
            return MF.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int SetCurrentMediaType(IntPtr self, uint i, uint* reserved, IntPtr type)
        {
            if (R(self) is not { } r) return MF.E_POINTER;
            if (type == IntPtr.Zero) return MF.E_POINTER;
            try
            {
                lock (r._lock)
                {
                    if (r.StreamOf(i) is not { } s) return MF.MF_E_INVALIDSTREAMNUMBER;
                    return s.SetCurrent(Mp4Types.Read(type));
                }
            }
            catch (Exception e) { D3DLog.Error("IMFSourceReader.SetCurrentMediaType", e); return MF.E_FAIL; }
        }

        [UnmanagedCallersOnly]
        private static int SetNativeMediaType(IntPtr self, uint i, IntPtr type, uint* flags)
        {
            if (R(self) is not { } r || type == IntPtr.Zero) return MF.E_POINTER;
            if (flags != null) *flags = 0;
            lock (r._lock)
            {
                if (r.StreamOf(i) is not { } s) return MF.MF_E_INVALIDSTREAMNUMBER;
                // a file's streams have their one type
                var t = Mp4Types.Read(type);
                return t.Subtype == s.Native.Subtype && t.Attributes.Guid(MF.MF_MT_MAJOR_TYPE) == s.Native.Attributes.Guid(MF.MF_MT_MAJOR_TYPE) ? MF.S_OK : MF.MF_E_INVALIDMEDIATYPE;
            }
        }

        [UnmanagedCallersOnly]
        private static int AddTransformForStream(IntPtr self, uint i, IntPtr transform)
        {
            D3DLog.Once("mf-reader-addtransform", "IMFSourceReaderEx.AddTransformForStream: the shim's source reader takes no transforms of the application's");
            return MF.E_NOTIMPL;
        }

        [UnmanagedCallersOnly]
        private static int RemoveAllTransformsForStream(IntPtr self, uint i)
        {
            if (R(self) is not { } r) return MF.E_POINTER;
            lock (r._lock)
            {
                if (r.StreamOf(i) is not { } s) return MF.MF_E_INVALIDSTREAMNUMBER;
                return MF.S_OK;                     // the decoder is no transform the application added
            }
        }

        [UnmanagedCallersOnly]
        private static int GetTransformForStream(IntPtr self, uint i, uint n, Guid* category, IntPtr* transform)
        {
            if (R(self) is not { } r || transform == null) return MF.E_POINTER;
            *transform = IntPtr.Zero;
            lock (r._lock)
            {
                if (r.StreamOf(i) is not { } s) return MF.MF_E_INVALIDSTREAMNUMBER;
                if (n != 0 || s.Decoder == null) return MF.MF_E_INVALIDINDEX;
                if (category != null) *category = s.Audio ? MFAudioDecoder.MFT_CATEGORY_AUDIO_DECODER : SR.MFT_CATEGORY_VIDEO_DECODER;
                *transform = s.Decoder.AddRefPointer();
            }
            return MF.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int GetServiceForStream(IntPtr self, uint i, Guid* service, Guid* riid, IntPtr* obj)
        {
            if (R(self) is not { } r || riid == null || obj == null) return MF.E_POINTER;
            *obj = IntPtr.Zero;
            lock (r._lock)
            {
                if (i == SR.MEDIASOURCE)
                {
                    // the media source itself (GUID_NULL), where the reader is of one
                    if (service != null && *service != Guid.Empty || r._source.MediaSource == IntPtr.Zero) return MF.E_NOINTERFACE;
                    int h = Com.QueryInterface(r._source.MediaSource, *riid, out var src);
                    *obj = src;
                    return h;
                }
                if (r.StreamOf(i) is not { } s) return MF.MF_E_INVALIDSTREAMNUMBER;
                // GUID_NULL: the object itself, the stream's decoder
                if (service != null && *service != Guid.Empty || s.Decoder == null) return MF.E_NOINTERFACE;
                int hr = Com.QueryInterface(s.Decoder.Pointer, *riid, out var p);
                *obj = p;
                return hr;
            }
        }

        [UnmanagedCallersOnly]
        private static int GetPresentationAttribute(IntPtr self, uint i, Guid* key, byte* value)
        {
            if (R(self) is not { } r || key == null || value == null) return MF.E_POINTER;
            MFValue? v = null;
            lock (r._lock)
            {
                if (i == SR.MEDIASOURCE)
                {
                    if (*key == SR.MF_SOURCE_READER_MEDIASOURCE_CHARACTERISTICS) v = MFValue.Of(r._source.Characteristics);
                    else v = r._source.Presentation(*key);
                }
                else
                {
                    if (r.StreamOf(i) is not { } s) return MF.MF_E_INVALIDSTREAMNUMBER;
                }
            }
            if (v == null) return MF.MF_E_ATTRIBUTENOTFOUND;
            MFAttributeStore.Write(v, value);
            return MF.S_OK;
        }

        // ---- position --------------------------------------------------------------------------------

        [UnmanagedCallersOnly]
        private static int SetCurrentPosition(IntPtr self, Guid* format, byte* position)
        {
            if (R(self) is not { } r || position == null) return MF.E_POINTER;
            if (format != null && *format != Guid.Empty) return MF.E_INVALIDARG;
            if (*(ushort*)position != 20) return MF.MF_E_INVALIDTYPE;                    // VT_I8
            long t = *(long*)(position + 8);
            lock (r._lock)
            {
                if (r._pending > 0) return SR.MF_E_INVALIDREQUEST;                       // asynchronous reads not yet answered
                int sh = r._source.Seek(t);
                if (sh < 0) return sh;
                foreach (var s in r._streams) s.Seek(t);
            }
            return MF.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int Flush(IntPtr self, uint i)
        {
            if (R(self) is not { } r) return MF.E_POINTER;
            List<ReaderStream> which;
            lock (r._lock)
            {
                if (i is SR.ANY or SR.MEDIASOURCE) which = r._streams.ToList();
                else if (r.StreamOf(i) is { } s) which = new() { s };
                else return MF.MF_E_INVALIDSTREAMNUMBER;
                foreach (var s in which) s.Flush();
                if (r._callback == IntPtr.Zero) return MF.S_OK;
                // asynchronous: the reads waiting are dropped, then IMFSourceReaderCallback::OnFlush
                r._work.Clear();
                r._pending = 0;
                r.Post(() => ((delegate* unmanaged<IntPtr, uint, int>)Com.Fn(r._callback, 4))(r._callback, i));
            }
            return MF.S_OK;
        }

        // ---- reading ----------------------------------------------------------------------------------

        [UnmanagedCallersOnly]
        private static int ReadSample(IntPtr self, uint i, uint control, uint* actual, uint* flags, long* time, IntPtr* sample)
        {
            if (R(self) is not { } r) return MF.E_POINTER;
            if (r._callback != IntPtr.Zero)
            {
                // asynchronous: no out parameters; the answer goes to OnReadSample
                if (actual != null || flags != null || time != null || sample != null) return MF.E_INVALIDARG;
                lock (r._lock)
                {
                    int check = r.Check(i);
                    if (check < 0) return check;
                    r._pending++;
                    r.Post(() =>
                    {
                        int hr; uint a, f; long ts; IntPtr s;
                        lock (r._lock)
                        {
                            if (r._pending == 0) return;                 // flushed
                            r._pending--;
                            hr = r.Read(i, control, out a, out f, out ts, out s);
                        }
                        try { ((delegate* unmanaged<IntPtr, int, uint, uint, long, IntPtr, int>)Com.Fn(r._callback, 3))(r._callback, hr, a, f, ts, s); }
                        finally { Com.Release(s); }
                    });
                }
                return MF.S_OK;
            }
            if (actual == null || flags == null || time == null || sample == null) return MF.E_POINTER;
            lock (r._lock)
            {
                int check = r.Check(i);
                if (check < 0) { *sample = IntPtr.Zero; return check; }
                int hr = r.Read(i, control, out uint a, out uint f, out long ts, out IntPtr s);
                *actual = a; *flags = f; *time = ts; *sample = s;
                return hr;
            }
        }

        private int Check(uint i)
        {
            if (_shutdown) return MF.MF_E_SHUTDOWN;
            if (i == SR.ANY) return _streams.Any(s => s.Selected) ? MF.S_OK : SR.MF_E_INVALIDREQUEST;
            if (StreamOf(i) is not { } st) return MF.MF_E_INVALIDSTREAMNUMBER;
            return st.Selected ? MF.S_OK : SR.MF_E_INVALIDREQUEST;
        }

        /// <summary>A sample of the stream (of the selected one next in time, for ANY).</summary>
        private int Read(uint i, uint control, out uint actual, out uint flags, out long time, out IntPtr sample)
        {
            actual = 0; flags = 0; time = 0; sample = IntPtr.Zero;
            ReaderStream? s;
            if (i == SR.ANY)
            {
                var live = _streams.Where(x => x.Selected && !x.Ended).ToList();
                // the next in time; at the end, the end of the last stream again
                s = live.Count > 0 ? live.OrderBy(x => x.NextTime).ThenBy(x => x.Index).First() : _streams.LastOrDefault(x => x.Selected);
                if (s == null) return SR.MF_E_INVALIDREQUEST;
            }
            else s = StreamOf(i)!;
            actual = (uint)s.Index;
            try
            {
                var got = s.Next((control & 1) != 0, out flags);          // MF_SOURCE_READER_CONTROLF_DRAIN
                if (got == null) return MF.S_OK;
                time = got.Time ?? 0;
                sample = got.Pointer;
                return MF.S_OK;
            }
            catch (Exception e)
            {
                D3DLog.Error("IMFSourceReader.ReadSample", e);
                flags = SR.ERROR;
                return MF.E_FAIL;
            }
        }

        /// <summary>The reader's thread: asynchronous reads and flushes, in order.</summary>
        private void Post(Action a)
        {
            _work.Enqueue(a);
            Monitor.PulseAll(_lock);
            if (_worker != null) return;
            _worker = new Thread(() =>
            {
                while (true)
                {
                    Action next;
                    lock (_lock)
                    {
                        while (_work.Count == 0 && !_shutdown) Monitor.Wait(_lock);
                        if (_shutdown) return;
                        next = _work.Dequeue();
                    }
                    try { next(); }
                    catch (Exception e) { D3DLog.Error("IMFSourceReaderCallback", e); }
                }
            }) { IsBackground = true, Name = "Source reader" };
            _worker.Start();
        }
    }
}
