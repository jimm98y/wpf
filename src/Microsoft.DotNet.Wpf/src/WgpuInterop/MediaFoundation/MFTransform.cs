// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Media Foundation off Windows: the video decoders, as IMFTransform -- what Windows' own decoder MFTs
// are to an application, over the system's decoder (PlatformVideoDecoder: VideoToolbox on Apple's).
//
// As Windows' H.264 and HEVC decoders behave:
//   - synchronous, one stream in and one out (id 0); the input type first, then the output's;
//   - the output types NV12, YV12, IYUV, I420 and YUY2, of the coded size (macroblocks of 16 for H.264,
//     8 for HEVC), the picture's own size in the apertures where it is smaller;
//   - the first frame (and any change of size) is MF_E_TRANSFORM_STREAM_CHANGE: the output type is
//     to be set again, of the size the stream has;
//   - ProcessInput is MF_E_NOTACCEPTING while a frame is ready; ProcessOutput is
//     MF_E_TRANSFORM_NEED_MORE_INPUT while none is;
//   - without a device manager the application's sample is written; given one (MFT_MESSAGE_SET_D3D_MANAGER)
//     the decoder hands out samples of its own, each an NV12 texture of the manager's device
//     (IMFDXGIBuffer), from a pool the samples return to as the application lets them go;
//   - MF_LOW_LATENCY (and ICodecAPI's CODECAPI_AVLowLatencyMode) hands frames out as they are decoded.
//

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.Wpf.Interop.WebGpu.Direct3D;
using Microsoft.Wpf.Interop.WebGpu.MediaFoundation.Codecs;

namespace Microsoft.Wpf.Interop.WebGpu.MediaFoundation
{
    /// <summary>A decoder the shim registers: its name, its CLSID, its codec and the subtypes it takes.</summary>
    internal sealed record DecoderInfo(string Name, Guid Clsid, VideoCodec Codec, uint Alignment, Guid[] Inputs);

    internal abstract unsafe class MFTransform : ComObject
    {
        protected MFTransform(params Vtable[] vts) : base(vts) { }
        public abstract void Shutdown();
    }

    internal sealed unsafe class MFVideoDecoder : MFTransform
    {
        public static readonly DecoderInfo[] Registered =
        {
            new("Microsoft H264 Video Decoder MFT", new Guid("62CE7E72-4C71-4d20-B15D-452831A87D9D"), VideoCodec.H264, 16, new[] { MF.MFVideoFormat_H264, MF.MFVideoFormat_H264_ES }),
            new("Microsoft H265 Video Decoder MFT", new Guid("420A51A3-D605-430C-B4FC-45274FA6C562"), VideoCodec.H265, 8, new[] { MF.MFVideoFormat_HEVC, MediaFormats.FourCC("HEVS") }),
            new("Microsoft VP9 Video Decoder MFT", new Guid("ac03e3d8-f4a3-4b34-a2d6-1e1dbe0c0eb1"), VideoCodec.VP9, 8, new[] { MediaFormats.VP90 }),
            new("Microsoft AV1 Video Decoder MFT", new Guid("1b2ed8ba-cc6b-4ffe-a2c5-0ba1bd3bdbd5"), VideoCodec.AV1, 8, new[] { MediaFormats.AV1 }),
            new("Microsoft MPEG Video Decoder MFT", new Guid("2D709E52-123F-49b5-9CBC-9AF5CDE28FB9"), VideoCodec.H262, 16, new[] { MediaFormats.MPEG2, MediaFormats.FourCC("MPG1") }),
            new("Mpeg4s Decoder MFT", new Guid("5686a0d9-fe39-409f-9dff-3fdbc849f9f5"), VideoCodec.Mpeg4, 16, new[] { MediaFormats.MP4V, MediaFormats.FourCC("M4S2") }),
            new("H263 Video Decoder MFT", new Guid("2a47207d-c3b8-4a57-a1fd-b3c9f3cf6fda"), VideoCodec.H263, 16, new[] { MediaFormats.H263 }),
        };

        /// <summary>The output subtypes, in the order Windows' decoders give them.</summary>
        private static readonly Guid[] s_outputs = { MF.MFVideoFormat_NV12, MF.MFVideoFormat_YV12, MF.MFVideoFormat_IYUV, MF.MFVideoFormat_I420, MF.MFVideoFormat_YUY2 };

        private static readonly Vtable s_transform = BuildTransform(), s_codecApi = BuildCodecApi();

        private readonly DecoderInfo _info;
        private readonly object _lock = new();
        private readonly MFAttributeBag _attributes = new();
        private MFMediaType? _in, _out;
        private PlatformVideoDecoder? _decoder;
        private DecodedFrame? _ready;
        private bool _draining, _shutdown;
        /// <summary>The coded size the output has (the first frame's, aligned), and the picture's.</summary>
        private (uint W, uint H) _coded, _picture;
        private bool _sizeKnown;
        private IntPtr _manager;                 // IMFDXGIDeviceManager, AddRef'd
        private readonly List<MFSample> _free = new();
        private readonly List<MFSample> _all = new();
        private bool _lowLatency;

        public MFVideoDecoder(DecoderInfo info) : base(s_transform, s_codecApi)
        {
            _info = info;
            var a = _attributes.Attributes;
            a.Set(MF.MF_SA_D3D11_AWARE, 1u);
            a.Set(MF.MF_SA_D3D_AWARE, 1u);
            a.Set(MF.MFT_SUPPORT_DYNAMIC_FORMAT_CHANGE, 1u);
            a.Set(MF.MF_LOW_LATENCY, 0u);
        }

        private bool LowLatency => _lowLatency || _attributes.Attributes.U32(MF.MF_LOW_LATENCY) != 0;

        public override void Shutdown()
        {
            lock (_lock)
            {
                _shutdown = true;
                _ready?.Dispose(); _ready = null;
                _decoder?.Dispose(); _decoder = null;
                foreach (var s in _all) { s.ReturnTo = null; if (_free.Contains(s)) { s.AddRef(); s.Release(); } }
                _all.Clear(); _free.Clear();
                Com.Release(_manager); _manager = IntPtr.Zero;
            }
        }

        protected override void OnFinalRelease()
        {
            Shutdown();
            _attributes.Release();
            _in?.Release(); _out?.Release();
        }

        private static DecoderInfo? InfoOf(IntPtr self) => From<MFVideoDecoder>(self)?._info;
        private static MFVideoDecoder? D(IntPtr self) => From<MFVideoDecoder>(self);

        // ---- IMFTransform ------------------------------------------------------------------------------

        private static Vtable BuildTransform()
        {
            var vt = new Vtable("IMFTransform", 26, MF.IID_IMFTransform);
            vt.Set(3, (delegate* unmanaged<IntPtr, uint*, uint*, uint*, uint*, int>)&GetStreamLimits);
            vt.Set(4, (delegate* unmanaged<IntPtr, uint*, uint*, int>)&GetStreamCount);
            vt.Set(5, (delegate* unmanaged<IntPtr, uint, uint*, uint, uint*, int>)&GetStreamIDs);
            vt.Set(6, (delegate* unmanaged<IntPtr, uint, byte*, int>)&GetInputStreamInfo);
            vt.Set(7, (delegate* unmanaged<IntPtr, uint, uint*, int>)&GetOutputStreamInfo);
            vt.Set(8, (delegate* unmanaged<IntPtr, IntPtr*, int>)&GetAttributes);
            vt.Set(9, (delegate* unmanaged<IntPtr, uint, IntPtr*, int>)&NoStreamAttributes);
            vt.Set(10, (delegate* unmanaged<IntPtr, uint, IntPtr*, int>)&NoStreamAttributes);
            vt.Set(11, (delegate* unmanaged<IntPtr, uint, int>)&NoStreams);
            vt.Set(12, (delegate* unmanaged<IntPtr, uint, uint*, int>)&NoAddStreams);
            vt.Set(13, (delegate* unmanaged<IntPtr, uint, uint, IntPtr*, int>)&GetInputAvailableType);
            vt.Set(14, (delegate* unmanaged<IntPtr, uint, uint, IntPtr*, int>)&GetOutputAvailableType);
            vt.Set(15, (delegate* unmanaged<IntPtr, uint, IntPtr, uint, int>)&SetInputType);
            vt.Set(16, (delegate* unmanaged<IntPtr, uint, IntPtr, uint, int>)&SetOutputType);
            vt.Set(17, (delegate* unmanaged<IntPtr, uint, IntPtr*, int>)&GetInputCurrentType);
            vt.Set(18, (delegate* unmanaged<IntPtr, uint, IntPtr*, int>)&GetOutputCurrentType);
            vt.Set(19, (delegate* unmanaged<IntPtr, uint, uint*, int>)&GetInputStatus);
            vt.Set(20, (delegate* unmanaged<IntPtr, uint*, int>)&GetOutputStatus);
            vt.Set(21, (delegate* unmanaged<IntPtr, long, long, int>)&SetOutputBounds);
            vt.Set(22, (delegate* unmanaged<IntPtr, uint, IntPtr, int>)&ProcessEvent);
            vt.Set(23, (delegate* unmanaged<IntPtr, uint, nuint, int>)&ProcessMessage);
            vt.Set(24, (delegate* unmanaged<IntPtr, uint, IntPtr, uint, int>)&ProcessInput);
            vt.Set(25, (delegate* unmanaged<IntPtr, uint, uint, byte*, uint*, int>)&ProcessOutput);
            return vt;
        }

        [UnmanagedCallersOnly]
        private static int GetStreamLimits(IntPtr self, uint* inMin, uint* inMax, uint* outMin, uint* outMax)
        {
            if (inMin == null || inMax == null || outMin == null || outMax == null) return MF.E_POINTER;
            *inMin = *inMax = *outMin = *outMax = 1;
            return MF.S_OK;
        }

        [UnmanagedCallersOnly] private static int GetStreamCount(IntPtr self, uint* i, uint* o) { if (i == null || o == null) return MF.E_POINTER; *i = *o = 1; return MF.S_OK; }
        [UnmanagedCallersOnly] private static int GetStreamIDs(IntPtr self, uint ni, uint* i, uint no, uint* o) => MF.E_NOTIMPL;      // fixed ids: 0

        [UnmanagedCallersOnly]
        private static int GetInputStreamInfo(IntPtr self, uint id, byte* info)
        {
            if (info == null) return MF.E_POINTER;
            if (id != 0) return MF.MF_E_INVALIDSTREAMNUMBER;
            new Span<byte>(info, 24).Clear();
            // MFT_INPUT_STREAM_INFO: latency 0; whole samples, one per buffer
            *(uint*)(info + 8) = 0x1 | 0x2;
            return MF.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int GetOutputStreamInfo(IntPtr self, uint id, uint* info)
        {
            if (D(self) is not { } d || info == null) return MF.E_POINTER;
            if (id != 0) return MF.MF_E_INVALIDSTREAMNUMBER;
            lock (d._lock)
            {
                // whole samples, one per buffer, of a fixed size; the decoder's own samples where it has a device
                info[0] = 0x1 | 0x2 | 0x4 | (d._manager != IntPtr.Zero ? 0x100u : 0u);
                info[1] = d._out != null ? ImageSize(d._out.Subtype, d.OutSize.W, d.OutSize.H) : 0;
                info[2] = 0;
            }
            return MF.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int GetAttributes(IntPtr self, IntPtr* a)
        {
            if (D(self) is not { } d || a == null) return MF.E_POINTER;
            *a = d._attributes.AddRefPointer();
            return MF.S_OK;
        }

        [UnmanagedCallersOnly] private static int NoStreamAttributes(IntPtr self, uint id, IntPtr* a) { if (a != null) *a = IntPtr.Zero; return MF.E_NOTIMPL; }
        [UnmanagedCallersOnly] private static int NoStreams(IntPtr self, uint id) => MF.E_NOTIMPL;
        [UnmanagedCallersOnly] private static int NoAddStreams(IntPtr self, uint n, uint* ids) => MF.E_NOTIMPL;

        [UnmanagedCallersOnly]
        private static int GetInputAvailableType(IntPtr self, uint id, uint i, IntPtr* type)
        {
            if (D(self) is not { } d || type == null) return MF.E_POINTER;
            *type = IntPtr.Zero;
            if (id != 0) return MF.MF_E_INVALIDSTREAMNUMBER;
            if (i >= d._info.Inputs.Length) return MF.MF_E_NO_MORE_TYPES;
            *type = MFMediaType.Video(d._info.Inputs[i]).Pointer;
            return MF.S_OK;
        }

        /// <summary>The output's size: the coded size of the frames once known, else the input type's, aligned.</summary>
        private (uint W, uint H) OutSize
        {
            get
            {
                if (_sizeKnown) return _coded;
                var (w, h) = _in?.FrameSize ?? (0, 0);
                return (Align(w), Align(h));
            }
        }

        private uint Align(uint v) => (v + _info.Alignment - 1) / _info.Alignment * _info.Alignment;

        private MFMediaType OutputType(Guid subtype)
        {
            var (w, h) = OutSize;
            var t = MFMediaType.Video(subtype, w, h);
            var a = t.Attributes;
            a.Set(MF.MF_MT_FIXED_SIZE_SAMPLES, 1u);
            a.Set(MF.MF_MT_ALL_SAMPLES_INDEPENDENT, 1u);
            a.Set(MF.MF_MT_INTERLACE_MODE, 2u);                                     // progressive
            a.Set(MF.MF_MT_PIXEL_ASPECT_RATIO, MF.Pack(1, 1));
            if (w > 0) a.Set(MF.MF_MT_DEFAULT_STRIDE, (uint)Stride(subtype, w));
            if (w > 0 && h > 0) a.Set(MF.MF_MT_SAMPLE_SIZE, ImageSize(subtype, w, h));
            if (_in?.Attributes.Get(MF.MF_MT_FRAME_RATE) is { } rate) a.Set(MF.MF_MT_FRAME_RATE, MFAttributesObject.Copy(rate));
            if (_in?.Attributes.Get(MF.MF_MT_PIXEL_ASPECT_RATIO) is { } par) a.Set(MF.MF_MT_PIXEL_ASPECT_RATIO, MFAttributesObject.Copy(par));
            // the picture, where it is smaller than the coded size: MFVideoArea { offset x, offset y (MFOffset), cx, cy }
            var (pw, ph) = _sizeKnown ? _picture : (_in?.FrameSize ?? (0, 0));
            if (pw > 0 && ph > 0 && (pw != w || ph != h))
            {
                var area = new byte[16];
                BitConverter.GetBytes(pw).CopyTo(area, 8);
                BitConverter.GetBytes(ph).CopyTo(area, 12);
                a.Set(MF.MF_MT_MINIMUM_DISPLAY_APERTURE, MFValue.Of(area));
                a.Set(MF.MF_MT_GEOMETRIC_APERTURE, MFValue.Of((byte[])area.Clone()));
            }
            return t;
        }

        internal static int Stride(Guid subtype, uint w) => subtype == MF.MFVideoFormat_YUY2 ? (int)w * 2 : (int)w;

        internal static uint ImageSize(Guid subtype, uint w, uint h) => subtype == MF.MFVideoFormat_YUY2 ? w * h * 2 : w * h + 2 * ((w + 1) / 2) * ((h + 1) / 2);

        [UnmanagedCallersOnly]
        private static int GetOutputAvailableType(IntPtr self, uint id, uint i, IntPtr* type)
        {
            if (D(self) is not { } d || type == null) return MF.E_POINTER;
            *type = IntPtr.Zero;
            if (id != 0) return MF.MF_E_INVALIDSTREAMNUMBER;
            lock (d._lock)
            {
                if (d._in == null) return MF.MF_E_TRANSFORM_TYPE_NOT_SET;
                if (i >= s_outputs.Length) return MF.MF_E_NO_MORE_TYPES;
                *type = d.OutputType(s_outputs[i]).Pointer;
            }
            return MF.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int SetInputType(IntPtr self, uint id, IntPtr type, uint flags)
        {
            if (D(self) is not { } d) return MF.E_POINTER;
            if (id != 0) return MF.MF_E_INVALIDSTREAMNUMBER;
            bool test = (flags & 1) != 0;                    // MFT_SET_TYPE_TEST_ONLY
            lock (d._lock)
            {
                if (d._shutdown) return MF.MF_E_SHUTDOWN;
                if (type == IntPtr.Zero)
                {
                    if (test) return MF.S_OK;
                    d._in?.Release(); d._in = null;
                    d._out?.Release(); d._out = null;
                    d.ResetDecoder();
                    return MF.S_OK;
                }
                var t = From<MFMediaType>(type);
                if (t == null) return MF.E_INVALIDARG;
                if (t.Attributes.Guid(MF.MF_MT_MAJOR_TYPE) != MF.MFMediaType_Video || Array.IndexOf(d._info.Inputs, t.Subtype) < 0) return MF.MF_E_INVALIDMEDIATYPE;
                if (!PlatformVideoDecoder.Supports(d._info.Codec)) return MF.MF_E_INVALIDMEDIATYPE;
                if (test) return MF.S_OK;
                d._in?.Release();
                d._in = t.Clone();
                d._out?.Release(); d._out = null;
                d._sizeKnown = false;
                d.ResetDecoder();
                // what the type carries of the stream before its first sample (H.264's avcC-less sequence header: Annex B)
                if (t.Attributes.Get(MF.MF_MT_MPEG_SEQUENCE_HEADER) is { Type: MF.VT_BLOB, B: { Length: > 0 } header }) d.Decoder?.Push(header, 0);
            }
            return MF.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int SetOutputType(IntPtr self, uint id, IntPtr type, uint flags)
        {
            if (D(self) is not { } d) return MF.E_POINTER;
            if (id != 0) return MF.MF_E_INVALIDSTREAMNUMBER;
            bool test = (flags & 1) != 0;
            lock (d._lock)
            {
                if (d._in == null) return MF.MF_E_TRANSFORM_TYPE_NOT_SET;
                if (type == IntPtr.Zero) { if (!test) { d._out?.Release(); d._out = null; } return MF.S_OK; }
                var t = From<MFMediaType>(type);
                if (t == null) return MF.E_INVALIDARG;
                if (t.Attributes.Guid(MF.MF_MT_MAJOR_TYPE) != MF.MFMediaType_Video || Array.IndexOf(s_outputs, t.Subtype) < 0) return MF.MF_E_INVALIDMEDIATYPE;
                // given a device, the decoder's own samples are NV12 textures
                if (d._manager != IntPtr.Zero && t.Subtype != MF.MFVideoFormat_NV12) return MF.MF_E_INVALIDMEDIATYPE;
                if (test) return MF.S_OK;
                d._out?.Release();
                d._out = t.Clone();
                var (w, h) = d._out.FrameSize;
                if (w > 0 && h > 0 && !d._sizeKnown) d._coded = (w, h);
            }
            return MF.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int GetInputCurrentType(IntPtr self, uint id, IntPtr* type)
        {
            if (D(self) is not { } d || type == null) return MF.E_POINTER;
            *type = IntPtr.Zero;
            if (id != 0) return MF.MF_E_INVALIDSTREAMNUMBER;
            lock (d._lock) { if (d._in == null) return MF.MF_E_TRANSFORM_TYPE_NOT_SET; *type = d._in.Clone().Pointer; }
            return MF.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int GetOutputCurrentType(IntPtr self, uint id, IntPtr* type)
        {
            if (D(self) is not { } d || type == null) return MF.E_POINTER;
            *type = IntPtr.Zero;
            if (id != 0) return MF.MF_E_INVALIDSTREAMNUMBER;
            lock (d._lock) { if (d._out == null) return MF.MF_E_TRANSFORM_TYPE_NOT_SET; *type = d._out.Clone().Pointer; }
            return MF.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int GetInputStatus(IntPtr self, uint id, uint* f)
        {
            if (D(self) is not { } d || f == null) return MF.E_POINTER;
            if (id != 0) return MF.MF_E_INVALIDSTREAMNUMBER;
            lock (d._lock) *f = d.Ready() ? 0u : 1u;                 // MFT_INPUT_STATUS_ACCEPT_DATA
            return MF.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int GetOutputStatus(IntPtr self, uint* f)
        {
            if (D(self) is not { } d || f == null) return MF.E_POINTER;
            lock (d._lock) *f = d.Ready() ? 1u : 0u;                 // MFT_OUTPUT_STATUS_SAMPLE_READY
            return MF.S_OK;
        }

        [UnmanagedCallersOnly] private static int SetOutputBounds(IntPtr self, long lo, long hi) => MF.E_NOTIMPL;
        [UnmanagedCallersOnly] private static int ProcessEvent(IntPtr self, uint id, IntPtr e) => MF.E_NOTIMPL;

        [UnmanagedCallersOnly]
        private static int ProcessMessage(IntPtr self, uint message, nuint param)
        {
            if (D(self) is not { } d) return MF.E_POINTER;
            try
            {
                lock (d._lock)
                {
                    switch (message)
                    {
                        case 0:                                      // MFT_MESSAGE_COMMAND_FLUSH
                            d._ready?.Dispose(); d._ready = null;
                            d._decoder?.Flush();
                            d._draining = false;
                            return MF.S_OK;
                        case 1:                                      // MFT_MESSAGE_COMMAND_DRAIN
                            d._decoder?.BeginDrain();
                            d._draining = true;
                            return MF.S_OK;
                        case 2:                                      // MFT_MESSAGE_SET_D3D_MANAGER
                        {
                            Com.Release(d._manager);
                            d._manager = IntPtr.Zero;
                            d.DropPool();
                            if (param != 0)
                            {
                                int hr = Com.QueryInterface((IntPtr)param, MF.IID_IMFDXGIDeviceManager, out var m);
                                if (hr < 0) return hr;
                                if (From<MFDeviceManager>(m) is not { Device: not 0 })
                                {
                                    Com.Release(m);
                                    return MF.MF_E_UNSUPPORTED_D3D_TYPE;
                                }
                                d._manager = m;
                            }
                            return MF.S_OK;
                        }
                        default:
                            return MF.S_OK;                          // begin/end streaming, start/end of stream, markers, drop samples
                    }
                }
            }
            catch (Exception e) { D3DLog.Error("IMFTransform.ProcessMessage", e); return MF.E_FAIL; }
        }

        private PlatformVideoDecoder? Decoder => _decoder ??= _in != null ? PlatformVideoDecoder.Create(_info.Codec, LowLatency) : null;

        private void ResetDecoder()
        {
            _ready?.Dispose(); _ready = null;
            _decoder?.Dispose(); _decoder = null;
            _draining = false;
        }

        /// <summary>Whether a frame is ready (taken from the decoder into _ready).</summary>
        private bool Ready()
        {
            if (_ready != null) return true;
            if (_decoder != null && _decoder.TryTake(out var f)) _ready = f;
            return _ready != null;
        }

        [UnmanagedCallersOnly]
        private static int ProcessInput(IntPtr self, uint id, IntPtr sample, uint flags)
        {
            if (D(self) is not { } d || sample == IntPtr.Zero) return MF.E_POINTER;
            if (id != 0) return MF.MF_E_INVALIDSTREAMNUMBER;
            try
            {
                lock (d._lock)
                {
                    if (d._shutdown) return MF.MF_E_SHUTDOWN;
                    if (d._in == null) return MF.MF_E_TRANSFORM_TYPE_NOT_SET;
                    if (d.Ready()) return MF.MF_E_NOTACCEPTING;
                    var dec = d.Decoder;
                    if (dec == null) return MF.E_FAIL;
                    if (d._draining) { dec.EndDrain(); d._draining = false; }
                    byte[] bytes;
                    long time = 0;
                    if (From<MFSample>(sample) is { } s)
                    {
                        bytes = s.Bytes();
                        time = s.Time ?? 0;
                    }
                    else
                    {
                        bytes = ForeignSampleBytes(sample);
                        long t;
                        if (((delegate* unmanaged<IntPtr, long*, int>)Com.Fn(sample, 35))(sample, &t) >= 0) time = t;
                    }
                    dec.Push(bytes, time);
                }
                return MF.S_OK;
            }
            catch (Exception e) { D3DLog.Error("IMFTransform.ProcessInput", e); return MF.E_FAIL; }
        }

        /// <summary>The bytes of a sample not of ours, through its own IMFSample methods.</summary>
        private static byte[] ForeignSampleBytes(IntPtr sample)
        {
            uint n;
            ((delegate* unmanaged<IntPtr, uint*, int>)Com.Fn(sample, 39))(sample, &n);
            var all = new List<byte>();
            for (uint i = 0; i < n; i++)
            {
                IntPtr b;
                if (((delegate* unmanaged<IntPtr, uint, IntPtr*, int>)Com.Fn(sample, 40))(sample, i, &b) < 0) continue;
                all.AddRange(From<MFBuffer>(b) is { } ours ? OwnBytes(ours) : MFSample.ForeignBytes(b));
                Com.Release(b);
            }
            return all.ToArray();
        }

        private static byte[] OwnBytes(MFBuffer b)
        {
            byte* p = b.LockBytes(false);
            try { return new ReadOnlySpan<byte>(p, (int)b.CurrentLength).ToArray(); }
            finally { b.UnlockBytes(); }
        }

        /// <summary>MFT_OUTPUT_DATA_BUFFER: dwStreamID, pSample, dwStatus, pEvents (pointer-aligned).</summary>
        private static int SampleOffset => IntPtr.Size;
        private static int StatusOffset => 2 * IntPtr.Size;

        [UnmanagedCallersOnly]
        private static int ProcessOutput(IntPtr self, uint flags, uint count, byte* buffers, uint* status)
        {
            if (D(self) is not { } d || buffers == null || status == null) return MF.E_POINTER;
            if (count != 1) return MF.E_INVALIDARG;
            *status = 0;
            *(uint*)(buffers + StatusOffset) = 0;
            try
            {
                lock (d._lock)
                {
                    if (d._shutdown) return MF.MF_E_SHUTDOWN;
                    if (d._in == null) return MF.MF_E_TRANSFORM_TYPE_NOT_SET;
                    if (!d.Ready())
                    {
                        if (d._draining) { d._decoder?.EndDrain(); d._draining = false; }
                        return MF.MF_E_TRANSFORM_NEED_MORE_INPUT;
                    }
                    var f = d._ready!;
                    // the first frame, or a new size: the output type is to be set again, of it
                    var coded = (d.Align((uint)f.Width), d.Align((uint)f.Height));
                    if (!d._sizeKnown || coded != d._coded || d._picture != ((uint)f.Width, (uint)f.Height))
                    {
                        d._coded = coded;
                        d._picture = ((uint)f.Width, (uint)f.Height);
                        d._sizeKnown = true;
                        d._out?.Release(); d._out = null;
                        *(uint*)(buffers + StatusOffset) = 0x100;            // MFT_OUTPUT_DATA_BUFFER_FORMAT_CHANGE
                        return MF.MF_E_TRANSFORM_STREAM_CHANGE;
                    }
                    if (d._out == null) return MF.MF_E_TRANSFORM_TYPE_NOT_SET;
                    long duration = d.FrameDuration();
                    IntPtr* slot = (IntPtr*)(buffers + SampleOffset);
                    if (d._manager != IntPtr.Zero)
                    {
                        var s = d.PooledSample();
                        if (s == null) return MF.E_FAIL;
                        d.Upload(f, s);
                        s.Time = f.Time; s.Duration = duration;
                        s.Attributes.Set(MF.MFSampleExtension_CleanPoint, 1u);
                        *slot = s.AddRefPointer();
                    }
                    else
                    {
                        if (*slot == IntPtr.Zero) return MF.E_INVALIDARG;
                        int hr = d.WriteInto(*slot, f, duration);
                        if (hr < 0) return hr;
                    }
                    f.Dispose();
                    d._ready = null;
                }
                return MF.S_OK;
            }
            catch (Exception e) { D3DLog.Error("IMFTransform.ProcessOutput", e); return MF.E_FAIL; }
        }

        private long FrameDuration()
        {
            var (n, den) = MF.Unpack(_in?.Attributes.U64(MF.MF_MT_FRAME_RATE) ?? 0);
            return n > 0 && den > 0 ? 10_000_000L * den / n : 0;
        }

        /// <summary>The frame into the application's sample, in the output's subtype, padded to the coded size.</summary>
        private int WriteInto(IntPtr sample, DecodedFrame f, long duration)
        {
            var (w, h) = _coded;
            uint size = ImageSize(_out!.Subtype, w, h);
            IntPtr buf;
            int hr = ((delegate* unmanaged<IntPtr, uint, IntPtr*, int>)Com.Fn(sample, 40))(sample, 0, &buf);
            if (hr < 0) return hr;
            try
            {
                byte* p; uint max, cur;
                hr = ((delegate* unmanaged<IntPtr, byte**, uint*, uint*, int>)Com.Fn(buf, 3))(buf, &p, &max, &cur);
                if (hr < 0) return hr;
                try
                {
                    if (max < size) return MF.MF_E_BUFFERTOOSMALL;
                    Convert(f, p, _out.Subtype, w, h);
                }
                finally { ((delegate* unmanaged<IntPtr, int>)Com.Fn(buf, 4))(buf); }
                ((delegate* unmanaged<IntPtr, uint, int>)Com.Fn(buf, 6))(buf, size);
            }
            finally { Com.Release(buf); }
            ((delegate* unmanaged<IntPtr, long, int>)Com.Fn(sample, 36))(sample, f.Time);
            if (duration > 0) ((delegate* unmanaged<IntPtr, long, int>)Com.Fn(sample, 38))(sample, duration);
            var key = MF.MFSampleExtension_CleanPoint;
            ((delegate* unmanaged<IntPtr, Guid*, uint, int>)Com.Fn(sample, 21))(sample, &key, 1);
            return MF.S_OK;
        }

        /// <summary>
        /// The frame as <paramref name="subtype"/> of <paramref name="w"/> by <paramref name="h"/>: NV12 (luma, then interleaved
        /// chroma), IYUV/I420 (Y, U, V), YV12 (Y, V, U), YUY2 (Y0 U Y1 V). Past the picture: black.
        /// </summary>
        internal static void Convert(DecodedFrame f, byte* dst, Guid subtype, uint w, uint h)
        {
            int cw = (int)(w + 1) / 2, ch = (int)(h + 1) / 2;
            var nv12 = (byte*)NativeMemory.Alloc((nuint)(w * h + (uint)(2 * cw * ch)));
            try
            {
                new Span<byte>(nv12, (int)(w * h)).Fill(16);
                new Span<byte>(nv12 + w * h, 2 * cw * ch).Fill(128);
                f.CopyTo(nv12, (int)w, nv12 + w * h, 2 * cw, (int)w, (int)h);
                byte* y = nv12, uv = nv12 + w * h;
                if (subtype == MF.MFVideoFormat_NV12) { Buffer.MemoryCopy(nv12, dst, w * h + 2 * cw * ch, w * h + 2 * cw * ch); return; }
                if (subtype == MF.MFVideoFormat_YUY2)
                {
                    for (int r = 0; r < h; r++)
                        for (int x = 0; x < w; x += 2)
                        {
                            byte* o = dst + r * w * 2 + x * 2;
                            byte* c = uv + (r / 2) * 2 * cw + x;
                            o[0] = y[r * w + x]; o[1] = c[0]; o[2] = y[r * w + Math.Min(x + 1, (int)w - 1)]; o[3] = c[1];
                        }
                    return;
                }
                Buffer.MemoryCopy(y, dst, w * h, w * h);
                bool vFirst = subtype == MF.MFVideoFormat_YV12;
                byte* u = dst + w * h + (vFirst ? cw * ch : 0), v = dst + w * h + (vFirst ? 0 : cw * ch);
                for (int i = 0; i < cw * ch; i++) { u[i] = uv[2 * i]; v[i] = uv[2 * i + 1]; }
            }
            finally { NativeMemory.Free(nv12); }
        }

        // ---- the decoder's own samples (with a device) --------------------------------------------------

        private MFSample? PooledSample()
        {
            lock (_free)
            {
                if (_free.Count > 0)
                {
                    var s = _free[^1];
                    _free.RemoveAt(_free.Count - 1);
                    return s;
                }
            }
            var m = From<MFDeviceManager>(_manager);
            if (m == null || m.Device == IntPtr.Zero) return null;
            var (w, h) = _coded;
            var desc = new D3D11_TEXTURE2D_DESC
            {
                Width = w, Height = h, MipLevels = 1, ArraySize = 1, Format = Formats.NV12, SampleDesc = new DXGI_SAMPLE_DESC { Count = 1 },
                Usage = D3D11_USAGE.DEFAULT, BindFlags = (D3D11_BIND)(0x200 | 0x8),           // decoder, shader resource
            };
            uint bind = _attributes.Attributes.U32(MF.MF_SA_D3D11_BINDFLAGS);
            if (bind != 0) desc.BindFlags = (D3D11_BIND)(bind | 0x8);
            IntPtr tex;
            int hr = ((delegate* unmanaged<IntPtr, D3D11_TEXTURE2D_DESC*, D3D11_SUBRESOURCE_DATA*, IntPtr*, int>)Com.Fn(m.Device, Vtbl.ID3D11Device.CreateTexture2D))(m.Device, &desc, null, &tex);
            if (hr < 0) { D3DLog.Once("mftex", $"Media Foundation: the decoder's texture of {w}x{h} was not made: 0x{hr:x8}"); return null; }
            var buffer = new MFDxgiBuffer(tex, 0);
            Com.Release(tex);
            var sample = new MFSample();
            sample.SetBuffer(buffer.Pointer);
            buffer.Release();
            sample.ReturnTo = Return;
            lock (_free) _all.Add(sample);
            // the pool's reference is let go: the sample lives on at no references, returned to the pool
            sample.Release();
            return sample;
        }

        private void Return(MFSample s)
        {
            lock (_free)
            {
                // a sample of another size (the stream changed) is not used again
                if (_shutdown || !_all.Contains(s)) { s.ReturnTo = null; return; }
                _free.Add(s);
            }
        }

        private void DropPool()
        {
            lock (_free)
            {
                foreach (var s in _free) { s.ReturnTo = null; s.AddRef(); s.Release(); }
                foreach (var s in _all) s.ReturnTo = null;
                _free.Clear();
                _all.Clear();
            }
        }

        private void Upload(DecodedFrame f, MFSample s)
        {
            var (w, h) = _coded;
            if (From<MFDxgiBuffer>(s.Buffers[0]) is not { } b) return;
            uint size = ImageSize(MF.MFVideoFormat_NV12, w, h);
            var tmp = (byte*)NativeMemory.Alloc(size);
            try
            {
                Convert(f, tmp, MF.MFVideoFormat_NV12, w, h);
                D3DTextureIo.Write(b.Texture, 0, tmp, (int)w, (int)(h + h / 2));
            }
            finally { NativeMemory.Free(tmp); }
        }

        // ---- ICodecAPI ----------------------------------------------------------------------------------

        private static Vtable BuildCodecApi()
        {
            var vt = new Vtable("ICodecAPI", 18, MF.IID_ICodecAPI);
            vt.Set(3, (delegate* unmanaged<IntPtr, Guid*, int>)&IsSupported);
            vt.Set(4, (delegate* unmanaged<IntPtr, Guid*, int>)&IsSupported);       // IsModifiable
            vt.Set(8, (delegate* unmanaged<IntPtr, Guid*, byte*, int>)&GetValue);
            vt.Set(7, (delegate* unmanaged<IntPtr, Guid*, byte*, int>)&GetValue);   // GetDefaultValue
            vt.Set(9, (delegate* unmanaged<IntPtr, Guid*, byte*, int>)&SetValue);
            return vt;
        }

        [UnmanagedCallersOnly] private static int IsSupported(IntPtr self, Guid* api) => api != null && *api == MF.MF_LOW_LATENCY ? MF.S_OK : MF.E_NOTIMPL;

        /// <summary>VARIANT: vt at 0, the value at 8 (VT_UI4 19, VT_BOOL 11).</summary>
        [UnmanagedCallersOnly]
        private static int GetValue(IntPtr self, Guid* api, byte* v)
        {
            if (D(self) is not { } d || api == null || v == null) return MF.E_POINTER;
            if (*api != MF.MF_LOW_LATENCY) return MF.E_NOTIMPL;
            new Span<byte>(v, 16).Clear();
            *(ushort*)v = 19;
            *(uint*)(v + 8) = d.LowLatency ? 1u : 0u;
            return MF.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int SetValue(IntPtr self, Guid* api, byte* v)
        {
            if (D(self) is not { } d || api == null || v == null) return MF.E_POINTER;
            if (*api != MF.MF_LOW_LATENCY) return MF.E_NOTIMPL;
            ushort vt = *(ushort*)v;
            d._lowLatency = vt == 11 ? *(short*)(v + 8) != 0 : *(uint*)(v + 8) != 0;
            return MF.S_OK;
        }
    }
}
