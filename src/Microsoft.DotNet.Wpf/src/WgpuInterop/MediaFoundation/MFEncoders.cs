// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Media Foundation off Windows: the encoders, as IMFTransform -- Windows' "H264 Encoder MFT" and "Microsoft AAC
// Audio Encoder MFT" as MFTEnumEx lists them (Documentation/media-foundation-shim.md, "The encoders"), over the
// system's encoders (Codecs/PlatformEncoders.cs). Their behaviour was probed (MFEncoderProbe):
//
//   - synchronous, one stream each way; the application's output samples written (no samples of their own);
//   - H.264: the output type first (its size, frame rate, interlace mode and bitrate required), then an input of
//     IYUV, YV12, NV12 or YUY2 of the same size and rate; Main profile unless MF_MT_MPEG2_PROFILE says otherwise;
//     the output current with MF_MT_MPEG_SEQUENCE_HEADER; Annex B access units, each led by an access unit
//     delimiter, the parameter sets before each key frame; the frames held as Windows' encoder holds them (five, of
//     Baseline four), and, of profiles with B pictures, the times a frame later than the decode times;
//   - AAC: 16-bit PCM of 1, 2, 6 or 8 channels at Windows' rates in, an output of Windows' table of 279 types (raw or
//     ADTS, by bytes a second and profile level), the input and output in either order but matching; the types the
//     application sets filled in with the rest of Windows' attributes, and kept (GetCurrentType a copy of them);
//     packets of 1024 frames, each a clean point, timed from the first input.
//

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Wpf.Interop.WebGpu.Direct3D;
using Microsoft.Wpf.Interop.WebGpu.MediaFoundation.Codecs;

namespace Microsoft.Wpf.Interop.WebGpu.MediaFoundation
{
    /// <summary>The IMFTransform of an encoder: one stream each way, the types and samples left to the encoder.</summary>
    internal abstract unsafe class MFEncoder : MFTransform
    {
        public static readonly Guid MFT_CATEGORY_VIDEO_ENCODER = new("f79eac7d-e545-4387-bdee-d647d7bde42a"), MFT_CATEGORY_AUDIO_ENCODER = new("91c64bd0-f91e-4d8c-9276-db248279d975");
        public static readonly Guid MFSampleExtension_DecodeTimestamp = new("73a954d4-09e2-4861-befc-94bd97c08e6e");
        protected static readonly Guid MF_MT_AM_FORMAT_TYPE = new("73d1072d-1870-4174-a063-29ff4ff6c11e"), FORMAT_WaveFormatEx = new("05589f81-c356-11ce-bf01-00aa0055595a");

        private static readonly Vtable s_transform = BuildTransform();
        protected readonly object Lock = new();
        protected readonly MFAttributeBag Bag = new();
        protected IntPtr In, Out;                   // the application's types as set (AddRef'd): kept, filled in
        protected bool Shut;

        protected MFEncoder(Vtable codecApi) : base(s_transform, codecApi) { }

        protected override void OnFinalRelease()
        {
            Shutdown();
            Bag.Release();
            Com.Release(In); Com.Release(Out);
            In = Out = IntPtr.Zero;
        }

        private static MFEncoder? E(IntPtr self) => From<MFEncoder>(self);

        // ---- what each encoder does ---------------------------------------------------------------------

        protected abstract int InputAvailable(uint i, out MFMediaType? type);
        protected abstract int OutputAvailable(uint i, out MFMediaType? type);
        /// <summary>Whether the type will do as the input (S_OK) or why not; with the attributes to fill it in with.</summary>
        protected abstract int CheckInput(MFMediaType t, out List<(Guid, MFValue)> fill);
        protected abstract int CheckOutput(MFMediaType t, bool test, out List<(Guid, MFValue)> fill);
        protected abstract void TypesChanged();
        protected abstract uint OutputSize();
        protected abstract int Input(IntPtr sample);
        /// <summary>The next output into the application's sample: S_OK, or MF_E_TRANSFORM_NEED_MORE_INPUT.</summary>
        protected abstract int Output(IntPtr sample);
        protected abstract void Flush();
        protected abstract void Drain();
        /// <summary>The current output type as handed out: a copy of the one kept, with what the encoder adds.</summary>
        protected virtual MFMediaType CurrentOutput(MFMediaType kept) => kept;
        /// <summary>Whether GetAttributes gives a store (Windows' AAC encoder has none: E_NOTIMPL).</summary>
        protected virtual bool HasAttributes => true;

        // ---- helpers ------------------------------------------------------------------------------------

        /// <summary>A copy of an application's type (of the shim's or not).</summary>
        protected static MFMediaType Read(IntPtr type) => Source.Mp4Types.Read(type);

        /// <summary>An attribute set on an application's type, through its own IMFAttributes::SetItem.</summary>
        protected static void SetOn(IntPtr type, Guid key, MFValue v)
        {
            var pv = stackalloc byte[MF.PropVariantSize];
            MFAttributeStore.Write(v, pv);
            ((delegate* unmanaged<IntPtr, Guid*, byte*, int>)Com.Fn(type, 18))(type, &key, pv);
            Source.Ole.PropVariantClear(pv);
        }

        /// <summary>The bytes of an input sample, ours or the application's.</summary>
        protected static byte[] Bytes(IntPtr sample) => From<MFSample>(sample)?.Bytes() ?? MFSample.ForeignSampleBytes(sample);

        protected static long? Time(IntPtr sample) { long t; return ((delegate* unmanaged<IntPtr, long*, int>)Com.Fn(sample, 35))(sample, &t) >= 0 ? t : null; }
        protected static long? Duration(IntPtr sample) { long t; return ((delegate* unmanaged<IntPtr, long*, int>)Com.Fn(sample, 37))(sample, &t) >= 0 ? t : null; }

        /// <summary>The bytes into the application's sample, its first buffer; with the time, duration and attributes.</summary>
        protected static int Write(IntPtr sample, ReadOnlySpan<byte> bytes, long time, long duration, IEnumerable<(Guid, MFValue)> attributes)
        {
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
                    if (max < bytes.Length) return MF.MF_E_BUFFERTOOSMALL;
                    bytes.CopyTo(new Span<byte>(p, bytes.Length));
                }
                finally { ((delegate* unmanaged<IntPtr, int>)Com.Fn(buf, 4))(buf); }
                ((delegate* unmanaged<IntPtr, uint, int>)Com.Fn(buf, 6))(buf, (uint)bytes.Length);
            }
            finally { Com.Release(buf); }
            ((delegate* unmanaged<IntPtr, long, int>)Com.Fn(sample, 36))(sample, time);
            ((delegate* unmanaged<IntPtr, long, int>)Com.Fn(sample, 38))(sample, duration);
            foreach (var (k, v) in attributes) SetOn(sample, k, v);
            return MF.S_OK;
        }

        // ---- IMFTransform -------------------------------------------------------------------------------

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
        [UnmanagedCallersOnly] private static int GetStreamIDs(IntPtr self, uint ni, uint* i, uint no, uint* o) => MF.E_NOTIMPL;

        [UnmanagedCallersOnly]
        private static int GetInputStreamInfo(IntPtr self, uint id, byte* info)
        {
            if (info == null) return MF.E_POINTER;
            if (id != 0) return MF.MF_E_INVALIDSTREAMNUMBER;
            new Span<byte>(info, 24).Clear();            // no latency, no flags, no size, no lookahead, no alignment
            return MF.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int GetOutputStreamInfo(IntPtr self, uint id, uint* info)
        {
            if (E(self) is not { } e || info == null) return MF.E_POINTER;
            if (id != 0) return MF.MF_E_INVALIDSTREAMNUMBER;
            lock (e.Lock) { info[0] = 0; info[1] = e.OutputSize(); info[2] = 0; }
            return MF.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int GetAttributes(IntPtr self, IntPtr* a)
        {
            if (E(self) is not { } e || a == null) return MF.E_POINTER;
            *a = IntPtr.Zero;
            if (!e.HasAttributes) return MF.E_NOTIMPL;
            *a = e.Bag.AddRefPointer();
            return MF.S_OK;
        }

        [UnmanagedCallersOnly] private static int NoStreamAttributes(IntPtr self, uint id, IntPtr* a) { if (a != null) *a = IntPtr.Zero; return MF.E_NOTIMPL; }
        [UnmanagedCallersOnly] private static int NoStreams(IntPtr self, uint id) => MF.E_NOTIMPL;
        [UnmanagedCallersOnly] private static int NoAddStreams(IntPtr self, uint n, uint* ids) => MF.E_NOTIMPL;

        [UnmanagedCallersOnly]
        private static int GetInputAvailableType(IntPtr self, uint id, uint i, IntPtr* type)
        {
            if (E(self) is not { } e || type == null) return MF.E_POINTER;
            *type = IntPtr.Zero;
            if (id != 0) return MF.MF_E_INVALIDSTREAMNUMBER;
            lock (e.Lock)
            {
                int hr = e.InputAvailable(i, out var t);
                if (t != null) *type = t.Pointer;
                return hr;
            }
        }

        [UnmanagedCallersOnly]
        private static int GetOutputAvailableType(IntPtr self, uint id, uint i, IntPtr* type)
        {
            if (E(self) is not { } e || type == null) return MF.E_POINTER;
            *type = IntPtr.Zero;
            if (id != 0) return MF.MF_E_INVALIDSTREAMNUMBER;
            lock (e.Lock)
            {
                int hr = e.OutputAvailable(i, out var t);
                if (t != null) *type = t.Pointer;
                return hr;
            }
        }

        [UnmanagedCallersOnly]
        private static int SetInputType(IntPtr self, uint id, IntPtr type, uint flags)
        {
            if (E(self) is not { } e) return MF.E_POINTER;
            if (id != 0) return MF.MF_E_INVALIDSTREAMNUMBER;
            bool test = (flags & 1) != 0;
            try
            {
                lock (e.Lock)
                {
                    if (e.Shut) return MF.MF_E_SHUTDOWN;
                    if (type == IntPtr.Zero) { if (!test) { Com.Release(e.In); e.In = IntPtr.Zero; e.TypesChanged(); } return MF.S_OK; }
                    var t = Read(type);
                    try
                    {
                        int hr = e.CheckInput(t, out var fill);
                        if (hr < 0 || test) return hr;
                        foreach (var (k, v) in fill) SetOn(type, k, v);
                        Com.AddRef(type);
                        Com.Release(e.In);
                        e.In = type;
                        e.TypesChanged();
                        return MF.S_OK;
                    }
                    finally { t.Release(); }
                }
            }
            catch (Exception ex) { D3DLog.Error("IMFTransform.SetInputType", ex); return MF.E_FAIL; }
        }

        [UnmanagedCallersOnly]
        private static int SetOutputType(IntPtr self, uint id, IntPtr type, uint flags)
        {
            if (E(self) is not { } e) return MF.E_POINTER;
            if (id != 0) return MF.MF_E_INVALIDSTREAMNUMBER;
            bool test = (flags & 1) != 0;
            try
            {
                lock (e.Lock)
                {
                    if (e.Shut) return MF.MF_E_SHUTDOWN;
                    if (type == IntPtr.Zero) { if (!test) { Com.Release(e.Out); e.Out = IntPtr.Zero; e.TypesChanged(); } return MF.S_OK; }
                    var t = Read(type);
                    try
                    {
                        int hr = e.CheckOutput(t, test, out var fill);
                        if (hr < 0 || test) return hr;
                        foreach (var (k, v) in fill) SetOn(type, k, v);
                        Com.AddRef(type);
                        Com.Release(e.Out);
                        e.Out = type;
                        e.TypesChanged();
                        return MF.S_OK;
                    }
                    finally { t.Release(); }
                }
            }
            catch (Exception ex) { D3DLog.Error("IMFTransform.SetOutputType", ex); return MF.E_FAIL; }
        }

        [UnmanagedCallersOnly]
        private static int GetInputCurrentType(IntPtr self, uint id, IntPtr* type)
        {
            if (E(self) is not { } e || type == null) return MF.E_POINTER;
            *type = IntPtr.Zero;
            if (id != 0) return MF.MF_E_INVALIDSTREAMNUMBER;
            lock (e.Lock)
            {
                if (e.In == IntPtr.Zero) return MF.MF_E_TRANSFORM_TYPE_NOT_SET;
                *type = Read(e.In).Pointer;
            }
            return MF.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int GetOutputCurrentType(IntPtr self, uint id, IntPtr* type)
        {
            if (E(self) is not { } e || type == null) return MF.E_POINTER;
            *type = IntPtr.Zero;
            if (id != 0) return MF.MF_E_INVALIDSTREAMNUMBER;
            try
            {
                lock (e.Lock)
                {
                    if (e.Out == IntPtr.Zero) return MF.MF_E_TRANSFORM_TYPE_NOT_SET;
                    *type = e.CurrentOutput(Read(e.Out)).Pointer;
                }
                return MF.S_OK;
            }
            catch (Exception ex) { D3DLog.Error("IMFTransform.GetOutputCurrentType", ex); return MF.E_FAIL; }
        }

        [UnmanagedCallersOnly]
        private static int GetInputStatus(IntPtr self, uint id, uint* f)
        {
            if (E(self) is not { } e || f == null) return MF.E_POINTER;
            if (id != 0) return MF.MF_E_INVALIDSTREAMNUMBER;
            *f = 1;                                          // MFT_INPUT_STATUS_ACCEPT_DATA
            return MF.S_OK;
        }

        [UnmanagedCallersOnly] private static int GetOutputStatus(IntPtr self, uint* f) => MF.E_NOTIMPL;
        [UnmanagedCallersOnly] private static int SetOutputBounds(IntPtr self, long lo, long hi) => MF.E_NOTIMPL;
        [UnmanagedCallersOnly] private static int ProcessEvent(IntPtr self, uint id, IntPtr ev) => MF.E_NOTIMPL;

        [UnmanagedCallersOnly]
        private static int ProcessMessage(IntPtr self, uint message, nuint param)
        {
            if (E(self) is not { } e) return MF.E_POINTER;
            try
            {
                lock (e.Lock)
                {
                    switch (message)
                    {
                        case 0: e.Flush(); return MF.S_OK;               // MFT_MESSAGE_COMMAND_FLUSH
                        case 1: e.Drain(); return MF.S_OK;               // MFT_MESSAGE_COMMAND_DRAIN
                        default: return MF.S_OK;                         // begin/end streaming, start/end of stream, markers
                    }
                }
            }
            catch (Exception ex) { D3DLog.Error("IMFTransform.ProcessMessage", ex); return MF.E_FAIL; }
        }

        [UnmanagedCallersOnly]
        private static int ProcessInput(IntPtr self, uint id, IntPtr sample, uint flags)
        {
            if (E(self) is not { } e || sample == IntPtr.Zero) return MF.E_POINTER;
            if (id != 0) return MF.MF_E_INVALIDSTREAMNUMBER;
            try
            {
                lock (e.Lock)
                {
                    if (e.Shut) return MF.MF_E_SHUTDOWN;
                    if (e.In == IntPtr.Zero || e.Out == IntPtr.Zero) return MF.MF_E_TRANSFORM_TYPE_NOT_SET;
                    return e.Input(sample);
                }
            }
            catch (Exception ex) { D3DLog.Error("IMFTransform.ProcessInput", ex); return MF.E_FAIL; }
        }

        /// <summary>MFT_OUTPUT_DATA_BUFFER: dwStreamID, pSample, dwStatus, pEvents.</summary>
        [UnmanagedCallersOnly]
        private static int ProcessOutput(IntPtr self, uint flags, uint count, byte* buffers, uint* status)
        {
            if (E(self) is not { } e || buffers == null || status == null) return MF.E_POINTER;
            *status = 0;
            if (count != 1) return MF.E_INVALIDARG;
            try
            {
                lock (e.Lock)
                {
                    *(uint*)(buffers + 2 * IntPtr.Size) = 0;
                    if (e.In == IntPtr.Zero || e.Out == IntPtr.Zero) return MF.MF_E_TRANSFORM_NEED_MORE_INPUT;
                    IntPtr sample = *(IntPtr*)(buffers + IntPtr.Size);
                    if (sample == IntPtr.Zero) return MF.E_INVALIDARG;            // the application provides the samples
                    return e.Output(sample);
                }
            }
            catch (Exception ex) { D3DLog.Error("IMFTransform.ProcessOutput", ex); return MF.E_FAIL; }
        }
    }

    /// <summary>Windows' "H264 Encoder MFT", over the system's H.264 encoder.</summary>
    internal sealed unsafe class MFH264Encoder : MFEncoder
    {
        public const string Name = "H264 Encoder MFT";
        public static readonly Guid Clsid = new("6ca50344-051a-4ded-9779-a43305165e35");
        public static readonly Guid[] Inputs = { MF.MFVideoFormat_IYUV, MF.MFVideoFormat_YV12, MF.MFVideoFormat_NV12, MF.MFVideoFormat_YUY2 };
        private static readonly Guid MFT_ENCODER_SUPPORTS_CONFIG_EVENT = new("86a355ae-3a77-4ec4-9f31-01149a4e92de");
        private static readonly Guid MF_MT_MPEG2_PROFILE = new("ad76a80b-2d5c-4e0b-b375-64e520137036"), MF_MT_AVG_BITRATE = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
        private static readonly Guid MF_MT_VIDEO_NOMINAL_RANGE = new("c21b8ee5-b956-4071-8daf-325edf5cab11");
        private static readonly Guid MFSampleExtension_VideoEncodePictureType = new("973704e6-cd14-483c-8f20-c9fc0928bad5");

        private static readonly Vtable s_codecApi = BuildCodecApi();

        private PlatformVideoEncoder? _encoder;
        private byte[]? _sequenceHeader;
        private readonly Queue<(byte[] Unit, long Time, bool Key)> _out = new();
        private readonly Queue<(long Time, long Duration)> _times = new();      // the inputs' times, in order: the decode times
        private int _held;                     // frames in the encoder or queued, not yet handed out
        private bool _draining;
        private byte[] _buffer = new byte[65536];

        // ICodecAPI's settings
        private uint _rateControl, _meanBitrate, _quality = 65, _qualityVsSpeed = 33, _gop, _bPictures = 1, _maxQp = 51, _minQp, _bufferSize, _maxBitrate;
        private bool _lowLatency, _cabac, _forceKey;
        private ulong _qp = 26;

        public MFH264Encoder() : base(s_codecApi)
        {
            Bag.Attributes.Set(MFT_ENCODER_SUPPORTS_CONFIG_EVENT, 1u);
        }

        public override void Shutdown()
        {
            lock (Lock)
            {
                Shut = true;
                _encoder?.Dispose(); _encoder = null;
                _out.Clear(); _times.Clear(); _held = 0;
            }
        }

        private MFMediaType? OutType => Out == IntPtr.Zero ? null : Read(Out);
        private uint Profile { get { using var _ = new Releaser(OutType, out var t); return t?.Attributes.U32(MF_MT_MPEG2_PROFILE, 77) is uint p && p != 0 ? p : 77; } }
        /// <summary>The frames Windows' encoder holds before its first output: five, of Baseline (no B pictures) four.</summary>
        private int Latency => _lowLatency ? 0 : Profile == 66 || _bPictures == 0 ? 4 : 5;
        private bool BPictures => !_lowLatency && Profile != 66 && _bPictures > 0;

        private readonly struct Releaser : IDisposable
        {
            private readonly MFMediaType? _t;
            public Releaser(MFMediaType? t, out MFMediaType? same) { _t = t; same = t; }
            public void Dispose() => _t?.Release();
        }

        protected override int OutputAvailable(uint i, out MFMediaType? type)
        {
            type = null;
            if (i > 0) return MF.MF_E_NO_MORE_TYPES;
            type = MFMediaType.Video(MF.MFVideoFormat_H264);
            return MF.S_OK;
        }

        protected override int InputAvailable(uint i, out MFMediaType? type)
        {
            type = null;
            using var _ = new Releaser(OutType, out var o);
            if (o == null) return MF.MF_E_TRANSFORM_TYPE_NOT_SET;
            if (i >= Inputs.Length) return MF.MF_E_NO_MORE_TYPES;
            var t = new MFMediaType();
            var a = t.Attributes;
            a.Set(MF.MF_MT_FRAME_RATE, o.Attributes.U64(MF.MF_MT_FRAME_RATE));
            a.Set(MF.MF_MT_FRAME_SIZE, o.Attributes.U64(MF.MF_MT_FRAME_SIZE));
            a.Set(MF.MF_MT_INTERLACE_MODE, 2u);
            a.Set(MF.MF_MT_MAJOR_TYPE, MF.MFMediaType_Video);
            a.Set(MF.MF_MT_PIXEL_ASPECT_RATIO, MF.Pack(1, 1));
            a.Set(MF.MF_MT_SUBTYPE, Inputs[i]);
            a.Set(MF_MT_VIDEO_NOMINAL_RANGE, 2u);
            type = t;
            return MF.S_OK;
        }

        protected override int CheckOutput(MFMediaType t, bool test, out List<(Guid, MFValue)> fill)
        {
            fill = new();
            if (t.Attributes.Guid(MF.MF_MT_MAJOR_TYPE) != MF.MFMediaType_Video || t.Subtype != MF.MFVideoFormat_H264) return MF.MF_E_INVALIDMEDIATYPE;
            if (t.Attributes.Get(MF.MF_MT_FRAME_SIZE) == null) return MF.MF_E_ATTRIBUTENOTFOUND;
            var (w, h) = t.FrameSize;
            if (w == 0 || h == 0 || w % 2 != 0 || h % 2 != 0) return MF.MF_E_INVALIDMEDIATYPE;
            uint p = t.Attributes.U32(MF_MT_MPEG2_PROFILE);
            if (p is not (0 or 66 or 77 or 100)) return MF.MF_E_INVALIDMEDIATYPE;
            if (test) return MF.S_OK;
            // what the set needs that its test does not
            if (t.Attributes.Get(MF.MF_MT_FRAME_RATE) == null || t.Attributes.Get(MF.MF_MT_INTERLACE_MODE) == null) return MF.MF_E_ATTRIBUTENOTFOUND;
            if (t.Attributes.Get(MF_MT_AVG_BITRATE) == null) return MF.E_FAIL;
            if (!PlatformVideoEncoder.Supports(VideoCodec.H264)) return MF.MF_E_INVALIDMEDIATYPE;
            return MF.S_OK;
        }

        protected override int CheckInput(MFMediaType t, out List<(Guid, MFValue)> fill)
        {
            fill = new();
            using var _ = new Releaser(OutType, out var o);
            if (o == null) return MF.MF_E_TRANSFORM_TYPE_NOT_SET;
            if (t.Attributes.Guid(MF.MF_MT_MAJOR_TYPE) != MF.MFMediaType_Video || Array.IndexOf(Inputs, t.Subtype) < 0) return MF.MF_E_INVALIDMEDIATYPE;
            if (t.FrameSize != o.FrameSize) return MF.MF_E_INVALIDMEDIATYPE;
            if (t.Attributes.Get(MF.MF_MT_FRAME_RATE) is { } r && r.U != o.Attributes.U64(MF.MF_MT_FRAME_RATE)) return MF.MF_E_INVALIDMEDIATYPE;
            return MF.S_OK;
        }

        protected override void TypesChanged()
        {
            _encoder?.Dispose(); _encoder = null;
            _sequenceHeader = null;
            _out.Clear(); _times.Clear(); _held = 0; _draining = false;
        }

        /// <summary>The output buffer: 1.875 bytes a pixel, at least 32 KB.</summary>
        protected override uint OutputSize()
        {
            using var _ = new Releaser(OutType, out var o);
            if (o == null) return 0;
            var (w, h) = o.FrameSize;
            return Math.Max(32768u, w * h * 15 / 8);
        }

        private VideoEncoderOptions Options(MFMediaType o)
        {
            var (w, h) = o.FrameSize;
            var (n, d) = MF.Unpack(o.Attributes.U64(MF.MF_MT_FRAME_RATE));
            uint bitrate = _meanBitrate != 0 ? _meanBitrate : o.Attributes.U32(MF_MT_AVG_BITRATE, 500_000);
            return new VideoEncoderOptions
            {
                Width = w, Height = h, FpsNom = n == 0 ? 30 : n, FpsDenom = d == 0 ? 1 : d,
                Bitrate = bitrate, H264Profile = Profile,
                RateControl = _rateControl switch { 0 => RateControlMode.ConstantBitrate, 1 or 2 => RateControlMode.VariableBitrate, 3 => RateControlMode.Quality, _ => RateControlMode.Default },
                Quality = _quality, KeyFrameInterval = _gop,
            };
        }

        /// <summary>
        /// MF_MT_MPEG_SEQUENCE_HEADER: the parameter sets, each after a four-byte start code, as the encoder makes them --
        /// of a black picture encoded for them, where no frame has been yet.
        /// </summary>
        protected override MFMediaType CurrentOutput(MFMediaType kept)
        {
            if (_sequenceHeader == null)
            {
                var (w, h) = kept.FrameSize;
                using var enc = PlatformVideoEncoder.Create(VideoCodec.H264, Options(kept));
                if (enc != null)
                {
                    var black = new byte[w * h * 3 / 2];
                    black.AsSpan(0, (int)(w * h)).Fill(16);
                    black.AsSpan((int)(w * h)).Fill(128);
                    enc.Push(black, 0);
                    enc.BeginDrain();
                    var buf = new byte[65536];
                    while (enc.TryTake(ref buf, out uint len, out _)) { _sequenceHeader ??= ParameterSets(buf.AsSpan(0, (int)len)); }
                    enc.EndDrain();
                }
            }
            if (_sequenceHeader != null) kept.Attributes.Set(MF.MF_MT_MPEG_SEQUENCE_HEADER, MFValue.Of(_sequenceHeader));
            return kept;
        }

        /// <summary>The SPS and PPS of an access unit, each after 00 00 00 01.</summary>
        private static byte[]? ParameterSets(ReadOnlySpan<byte> au)
        {
            var r = new List<byte>();
            foreach (var (off, len) in Nals(au))
            {
                int type = au[off] & 0x1F;
                if (type is 7 or 8) { r.AddRange(new byte[] { 0, 0, 0, 1 }); r.AddRange(au.Slice(off, len).ToArray()); }
            }
            return r.Count > 0 ? r.ToArray() : null;
        }

        /// <summary>The NAL units of Annex B: each one's payload's offset and length (past its start code).</summary>
        private static List<(int, int)> Nals(ReadOnlySpan<byte> b)
        {
            var starts = new List<int>();
            for (int i = 0; i + 3 <= b.Length; i++)
                if (b[i] == 0 && b[i + 1] == 0 && b[i + 2] == 1) { starts.Add(i + 3); i += 2; }
            var r = new List<(int, int)>();
            for (int k = 0; k < starts.Count; k++)
            {
                int end = k + 1 < starts.Count ? starts[k + 1] - 3 : b.Length;
                if (k + 1 < starts.Count && end > 0 && b[end - 1] == 0) end--;              // a four-byte start code's leading zero
                r.Add((starts[k], end - starts[k]));
            }
            return r;
        }

        protected override int Input(IntPtr sample)
        {
            using var _ = new Releaser(OutType, out var o);
            using var __ = new Releaser(Read(In), out var i);
            if (_encoder == null)
            {
                _encoder = PlatformVideoEncoder.Create(VideoCodec.H264, Options(o!));
                if (_encoder == null) return MF.E_FAIL;
            }
            var (w, h) = o!.FrameSize;
            var nv12 = ToNv12(Bytes(sample), i!.Subtype, (int)w, (int)h);
            long time = Time(sample) ?? 0, dur = Duration(sample) ?? FrameDuration(o);
            _times.Enqueue((time, dur));
            _encoder.Push(nv12, time);
            _held++;
            Collect();
            return MF.S_OK;
        }

        private static long FrameDuration(MFMediaType o)
        {
            var (n, d) = MF.Unpack(o.Attributes.U64(MF.MF_MT_FRAME_RATE));
            return n > 0 ? 10_000_000L * d / n : 0;
        }

        private void Collect()
        {
            if (_encoder == null) return;
            while (_encoder.TryTake(ref _buffer, out uint len, out long t))
            {
                var unit = _buffer.AsSpan(0, (int)len).ToArray();
                bool key = Nals(unit).Any(n => (unit[n.Item1] & 0x1F) == 5);
                _out.Enqueue((unit, t, key));
            }
        }

        protected override int Output(IntPtr sample)
        {
            Collect();
            // where Windows' encoder would have a frame out by now, the system's (GStreamer's runs on threads of its own) is waited for
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (_out.Count == 0 && _encoder != null && !_draining && _held > Latency && sw.ElapsedMilliseconds < 500) { System.Threading.Thread.Sleep(2); Collect(); }
            // Windows' encoder holds its frames (its look-ahead) until enough have come, or it is drained
            if (_out.Count == 0) { if (_draining) { _draining = false; _held = 0; _times.Clear(); } return MF.MF_E_TRANSFORM_NEED_MORE_INPUT; }
            if (!_draining && _held <= Latency) return MF.MF_E_TRANSFORM_NEED_MORE_INPUT;
            var (unit, _, key) = _out.Peek();
            var (dts, dur) = _times.Count > 0 ? _times.Peek() : (0L, 0L);
            using var _o = new Releaser(OutType, out var o);
            // an access unit delimiter first: primary_pic_type 0 of I pictures, 1 of P
            var au = new byte[6 + unit.Length];
            au[3] = 1; au[4] = 9; au[5] = key ? (byte)0x10 : (byte)0x30;
            unit.CopyTo(au, 6);
            long pts = BPictures ? dts + FrameDuration(o!) : dts;
            var attrs = new List<(Guid, MFValue)>
            {
                (new Guid("2b5d5457-5547-4f07-b8c8-b4a3a9a1daac"), MFValue.Of(1u)),
                (MFSampleExtension_DecodeTimestamp, MFValue.Of((ulong)dts)),
                (new Guid("9154733f-e1bd-41bf-81d3-fcd918f71332"), MFValue.Of(65535u)),
                (MFSampleExtension_VideoEncodePictureType, MFValue.Of(key ? 0u : 1u)),
            };
            attrs.Add((MF.MFSampleExtension_CleanPoint, MFValue.Of(key ? 1u : 0u)));
            int hr = Write(sample, au, pts, dur, attrs);
            if (hr < 0) return hr;
            _out.Dequeue();
            if (_times.Count > 0) _times.Dequeue();
            _held--;
            return MF.S_OK;
        }

        protected override void Flush()
        {
            _encoder?.Dispose(); _encoder = null;
            _out.Clear(); _times.Clear(); _held = 0; _draining = false;
        }

        protected override void Drain()
        {
            if (_encoder != null) { _encoder.BeginDrain(); Collect(); _encoder.EndDrain(); _encoder.Dispose(); _encoder = null; }
            _draining = true;
        }

        /// <summary>IYUV/I420 (Y, U, V), YV12 (Y, V, U) and YUY2 as NV12.</summary>
        internal static byte[] ToNv12(byte[] src, Guid sub, int w, int h)
        {
            if (sub == MF.MFVideoFormat_NV12) return src;
            int cw = w / 2, ch = h / 2;
            var dst = new byte[w * h + 2 * cw * ch];
            if (sub == MF.MFVideoFormat_YUY2)
            {
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        int at = (y * w + x) * 2;
                        if (at >= src.Length) break;
                        dst[y * w + x] = src[at];
                        if (y % 2 == 0 && x % 2 == 0 && at + 3 < src.Length) { int c = w * h + (y / 2) * 2 * cw + x; dst[c] = src[at + 1]; dst[c + 1] = src[at + 3]; }
                    }
                return dst;
            }
            Array.Copy(src, dst, Math.Min(w * h, src.Length));
            bool vFirst = sub == MF.MFVideoFormat_YV12;
            int u = w * h + (vFirst ? cw * ch : 0), v = w * h + (vFirst ? 0 : cw * ch);
            for (int k = 0; k < cw * ch; k++)
            {
                if (u + k < src.Length) dst[w * h + 2 * k] = src[u + k];
                if (v + k < src.Length) dst[w * h + 2 * k + 1] = src[v + k];
            }
            return dst;
        }

        // ---- ICodecAPI: the properties Windows' encoder supports, of their defaults ---------------------

        private static readonly Guid AVEncCommonRateControlMode = new("1c0608e9-370c-4710-8a58-cb6181c42423"), AVEncCommonMeanBitRate = new("f7222374-2144-4815-b550-a37f8e12ee52");
        private static readonly Guid AVEncCommonQuality = new("fcbf57a3-7ea5-4b0c-9644-69b40c39c391"), AVEncCommonQualityVsSpeed = new("98332df8-03cd-476b-89fa-3f9e442dec9f");
        private static readonly Guid AVEncMPVGOPSize = new("95f31b26-95a4-41aa-9303-246a7fc6eef1"), AVEncMPVDefaultBPictureCount = new("8d390aac-dc5c-4200-b57f-814d04babab2");
        private static readonly Guid AVEncH264CABACEnable = new("ee6cad62-d305-4248-a50e-e1b255f7caf8"), AVLowLatencyMode = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");
        private static readonly Guid AVEncVideoForceKeyFrame = new("398c1b98-8353-475a-9ef2-8f265d260345"), AVEncCommonBufferSize = new("0db96574-b6a4-4c8b-8106-3773de0310cd");
        private static readonly Guid AVEncCommonMaxBitRate = new("9651eae4-39b9-4ebf-85ef-d7f444ec7465"), AVEncVideoMinQP = new("0ee22c6a-a37c-4568-b5f1-9d4c2b3ab886");
        private static readonly Guid AVEncVideoMaxQP = new("3daf6f66-a6a7-45e0-a8e5-f2743f46a3a2"), AVEncNumWorkerThreads = new("b0c8bf60-16f7-4951-a30b-1db1609293d6");
        private static readonly Guid AVEncVideoTemporalLayerCount = new("19caebff-b74d-4cfd-8c27-c2f9d97d5f52"), AVEncAdaptiveMode = new("4419b185-da1f-4f53-bc76-097d0c1efb1e");
        private static readonly Guid AVEncVideoLTRBufferControl = new("a4a0e93d-4cbc-444c-89f4-826d310e92a7"), AVEncMPVProfile = new("dabb534a-1d99-4284-975a-d90e2239baa1");
        private static readonly Guid AVEncVideoEncodeQP = new("2cb5696b-23fb-4ce1-a0f9-ef5b90fd55ca"), AVEncSliceControlMode = new("e9e782ef-5f18-44c9-a90b-e9c3c2c17b0b");

        private static Vtable BuildCodecApi()
        {
            var vt = new Vtable("ICodecAPI", 18, MF.IID_ICodecAPI);
            CodecApiSlots.Fill(vt);
            vt.Set(3, (delegate* unmanaged<IntPtr, Guid*, int>)&IsSupported);
            vt.Set(4, (delegate* unmanaged<IntPtr, Guid*, int>)&IsSupported);       // IsModifiable
            vt.Set(7, (delegate* unmanaged<IntPtr, Guid*, byte*, int>)&GetValue);   // GetDefaultValue
            vt.Set(8, (delegate* unmanaged<IntPtr, Guid*, byte*, int>)&GetValue);
            vt.Set(9, (delegate* unmanaged<IntPtr, Guid*, byte*, int>)&SetValue);
            return vt;
        }

        private static readonly Guid[] s_supported = { AVEncCommonRateControlMode, AVEncCommonMeanBitRate, AVEncCommonQuality, AVEncCommonQualityVsSpeed, AVEncMPVGOPSize,
            AVEncMPVDefaultBPictureCount, AVEncH264CABACEnable, AVLowLatencyMode, AVEncVideoForceKeyFrame, AVEncCommonBufferSize, AVEncCommonMaxBitRate, AVEncVideoMinQP,
            AVEncVideoMaxQP, AVEncNumWorkerThreads, AVEncVideoTemporalLayerCount, AVEncAdaptiveMode, AVEncVideoLTRBufferControl, AVEncVideoEncodeQP, AVEncSliceControlMode };

        [UnmanagedCallersOnly]
        private static int IsSupported(IntPtr self, Guid* api)
        {
            if (api == null) return MF.E_POINTER;
            if (*api == AVEncMPVProfile) return 1;                     // S_FALSE, as Windows'
            return Array.IndexOf(s_supported, *api) >= 0 ? MF.S_OK : MF.E_NOTIMPL;
        }

        /// <summary>VARIANT: vt at 0, the value at 8 (VT_UI4 19, VT_BOOL 11 of -1 or 0).</summary>
        [UnmanagedCallersOnly]
        private static int GetValue(IntPtr self, Guid* api, byte* v)
        {
            if (From<MFH264Encoder>(self) is not { } e || api == null || v == null) return MF.E_POINTER;
            new Span<byte>(v, 16).Clear();
            var g = *api;
            if (g == AVEncSliceControlMode) return unchecked((int)0x80040314);      // as Windows': supported, of no value to get
            if (g == AVEncVideoEncodeQP) { *(ushort*)v = 21; *(ulong*)(v + 8) = e._qp; return MF.S_OK; }    // VT_UI8
            if (g == AVEncH264CABACEnable || g == AVLowLatencyMode)
            {
                *(ushort*)v = 11;
                *(short*)(v + 8) = (g == AVLowLatencyMode ? e._lowLatency : e._cabac) ? (short)-1 : (short)0;
                return MF.S_OK;
            }
            uint? value = g == AVEncCommonRateControlMode ? e._rateControl : g == AVEncCommonMeanBitRate ? (e._meanBitrate != 0 ? e._meanBitrate : e.OutBitrate())
                : g == AVEncCommonQuality ? e._quality : g == AVEncCommonQualityVsSpeed ? e._qualityVsSpeed : g == AVEncMPVGOPSize ? e._gop
                : g == AVEncMPVDefaultBPictureCount ? e._bPictures : g == AVEncVideoForceKeyFrame ? (e._forceKey ? 1u : 0u) : g == AVEncCommonBufferSize ? e._bufferSize
                : g == AVEncCommonMaxBitRate ? e._maxBitrate : g == AVEncVideoMinQP ? e._minQp : g == AVEncVideoMaxQP ? e._maxQp : g == AVEncNumWorkerThreads ? 0u
                : g == AVEncVideoTemporalLayerCount ? 1u : g == AVEncAdaptiveMode ? 0u : g == AVEncVideoLTRBufferControl ? 0u : null;
            if (value is not uint u) return MF.E_NOTIMPL;
            *(ushort*)v = 19;
            *(uint*)(v + 8) = u;
            return MF.S_OK;
        }

        private uint OutBitrate() { lock (Lock) { using var _ = new Releaser(OutType, out var o); return o?.Attributes.U32(MF_MT_AVG_BITRATE, 500_000) ?? 500_000; } }

        [UnmanagedCallersOnly]
        private static int SetValue(IntPtr self, Guid* api, byte* v)
        {
            if (From<MFH264Encoder>(self) is not { } e || api == null || v == null) return MF.E_POINTER;
            var g = *api;
            ushort vt = *(ushort*)v;
            uint u = vt == 11 ? (*(short*)(v + 8) != 0 ? 1u : 0u) : *(uint*)(v + 8);
            lock (e.Lock)
            {
                if (g == AVEncCommonRateControlMode) e._rateControl = u;
                else if (g == AVEncCommonMeanBitRate)
                {
                    // as Windows' encoder (MFSinkTransformProbe's): of the configuration before the types are set (over the type's
                    // bit rate), a change of the running encoder once frames have come; between the two, not taken
                    if (e.Out == IntPtr.Zero) e._meanBitrate = u;
                    else if (e._encoder != null) { e._meanBitrate = u; e._encoder.SetBitrate(u); }
                }
                else if (g == AVEncCommonQuality) e._quality = u;
                else if (g == AVEncCommonQualityVsSpeed) e._qualityVsSpeed = u;
                else if (g == AVEncMPVGOPSize) e._gop = u;
                else if (g == AVEncMPVDefaultBPictureCount) e._bPictures = u;
                else if (g == AVEncH264CABACEnable) e._cabac = u != 0;
                else if (g == AVLowLatencyMode) e._lowLatency = u != 0;
                else if (g == AVEncVideoForceKeyFrame) e._forceKey = u != 0;
                else if (g == AVEncCommonBufferSize) e._bufferSize = u;
                else if (g == AVEncCommonMaxBitRate) e._maxBitrate = u;
                else if (g == AVEncVideoMinQP) e._minQp = u;
                else if (g == AVEncVideoMaxQP) e._maxQp = u;
                else if (g == AVEncVideoEncodeQP) e._qp = vt == 21 ? *(ulong*)(v + 8) : u;
                else if (Array.IndexOf(s_supported, g) < 0) return MF.E_NOTIMPL;
            }
            return MF.S_OK;
        }
    }

    /// <summary>Windows' "Microsoft AAC Audio Encoder MFT", over the system's AAC encoder.</summary>
    internal sealed unsafe class MFAacEncoder : MFEncoder
    {
        public const string Name = "Microsoft AAC Audio Encoder MFT";
        public static readonly Guid Clsid = new("93af0c51-2275-45d2-a35b-f2ba21caed00");
        private static readonly Guid MF_MT_AVG_BITRATE = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e"), MF_MT_AUDIO_PREFER_WAVEFORMATEX = new("a901aaba-e037-458a-bdf6-545be2074042");
        private static readonly Guid MF_MT_AAC_PAYLOAD_TYPE = new("bfbabe79-7434-4d1c-94f0-72a3b9e17188"), MF_MT_AAC_AUDIO_PROFILE_LEVEL_INDICATION = new("7632f0e6-9538-4d61-acda-ea29c8c14456");

        private static readonly Vtable s_codecApi = BuildCodecApi();

        /// <summary>The rates and channel counts of the inputs, in Windows' order.</summary>
        private static readonly uint[] s_rates = { 44100, 48000, 11025, 16000, 22050, 24000, 32000, 96000 };
        private static readonly uint[] s_channels = { 1, 2, 6, 8 };

        /// <summary>Windows' output types: channels, rate, bytes a second, profile level, payload (0 raw, 1 ADTS), in its order.</summary>
        internal static readonly (uint Ch, uint Rate, uint Bytes, uint Pl, uint Payload)[] Table = BuildTable();

        private PlatformAudioEncoder? _encoder;
        private readonly Queue<byte[]> _packets = new();
        private long? _t0;
        private long _packetsOut;
        private bool _first = true;
        private byte[] _buffer = new byte[8192];
        private long _framesIn;
        private int _entry = -1;               // the output's entry of the table, as set
        private bool _draining;

        public MFAacEncoder() : base(s_codecApi) { }

        protected override bool HasAttributes => false;

        /// <summary>The table, run by run: each run a profile level and payload over (channels, rate, bytes a second).</summary>
        private static (uint, uint, uint, uint, uint)[] BuildTable()
        {
            var t = new List<(uint, uint, uint, uint, uint)>();
            void Run(uint pl, uint payload, params (uint Ch, uint Rate, uint[] Bytes)[] cells)
            {
                foreach (var (ch, rate, bytes) in cells) foreach (var b in bytes) t.Add((ch, rate, b, pl, payload));
            }
            uint[] lc = { 12000, 16000, 20000, 24000 }, mc = { 72000, 96000, 120000, 144000 };
            // per payload, 44100 then 48000, each mono, stereo, 5.1
            for (uint payload = 0; payload <= 1; payload++)
                foreach (var rate in new uint[] { 44100, 48000 })
                {
                    Run(0x29, payload, (1, rate, lc));
                    Run(0x29, payload, (2, rate, lc));
                    Run(0x2A, payload, (6, rate, mc));
                }
            for (uint payload = 0; payload <= 1; payload++)
            {
                Run(0x28, payload, (1, 11025, new uint[] { 1000, 1500, 2000 }), (1, 16000, new uint[] { 1000, 1500, 2000, 3000, 4000 }), (1, 22050, new uint[] { 3000, 4000 }), (1, 24000, new uint[] { 3000 }));
                Run(0x29, payload, (1, 24000, new uint[] { 4000 }), (1, 32000, new uint[] { 6000, 8000, 12000, 16000 }), (1, 44100, new uint[] { 6000, 8000 }), (1, 48000, new uint[] { 6000, 8000 }));
            }
            Run(0x2C, 0, (1, 24000, new uint[] { 1500 }), (1, 32000, new uint[] { 2000, 3000, 4000, 6000 }), (1, 44100, new uint[] { 6000 }), (1, 48000, new uint[] { 6000 }));
            for (uint payload = 0; payload <= 1; payload++)
            {
                Run(0x28, payload, (2, 11025, new uint[] { 2000 }), (2, 16000, new uint[] { 2000, 3000, 4000 }), (2, 22050, new uint[] { 4000 }), (2, 24000, new uint[] { 4000 }));
                Run(0x29, payload, (2, 32000, new uint[] { 6000, 8000, 12000, 16000, 24000, 32000, 40000 }), (2, 44100, new uint[] { 32000, 40000 }), (2, 48000, new uint[] { 32000, 40000 }));
                Run(0x2B, payload, (2, 96000, new uint[] { 40000 }));
            }
            Run(0x2C, 0, (2, 32000, new uint[] { 2000, 3000, 4000, 6000, 8000, 12000 }), (2, 44100, new uint[] { 2000, 3000, 4000, 6000, 8000, 12000 }), (2, 48000, new uint[] { 2000, 3000, 4000, 6000, 8000, 12000 }));
            Run(0x30, 0, (2, 22050, new uint[] { 1500 }), (2, 24000, new uint[] { 1500 }), (2, 32000, new uint[] { 1500, 2000 }), (2, 44100, new uint[] { 3000, 4000, 6000 }), (2, 48000, new uint[] { 3000, 4000, 6000 }));
            uint[] six = { 16000, 24000, 32000, 40000, 60000, 64000, 80000, 90000 }, sixHe = { 8000, 12000, 16000, 24000, 32000, 40000 };
            uint[] eight = { 32000, 40000, 60000, 64000, 80000, 90000 };
            for (uint payload = 0; payload <= 1; payload++) Run(0x2A, payload, (6, 32000, six), (6, 44100, six), (6, 48000, six));
            Run(0x2E, 0, (6, 32000, sixHe), (6, 44100, sixHe), (6, 48000, sixHe));
            for (uint payload = 0; payload <= 1; payload++) Run(0x50, payload, (8, 32000, eight), (8, 44100, eight), (8, 48000, eight));
            Run(0x52, 0, (8, 32000, sixHe), (8, 44100, sixHe), (8, 48000, sixHe));
            return t.ToArray();
        }

        public override void Shutdown()
        {
            lock (Lock) { Shut = true; _encoder?.Dispose(); _encoder = null; _packets.Clear(); }
        }

        private static MFMediaType Pcm(Guid sub, uint ch, uint rate)
        {
            var t = new MFMediaType();
            var a = t.Attributes;
            a.Set(MF_MT_AM_FORMAT_TYPE, FORMAT_WaveFormatEx);
            a.Set(MF.MF_MT_ALL_SAMPLES_INDEPENDENT, 1u);
            a.Set(Source.SR.MF_MT_AUDIO_AVG_BYTES_PER_SECOND, rate * ch * 2);
            a.Set(Source.SR.MF_MT_AUDIO_BITS_PER_SAMPLE, 16u);
            a.Set(Source.SR.MF_MT_AUDIO_BLOCK_ALIGNMENT, ch * 2);
            a.Set(Source.SR.MF_MT_AUDIO_NUM_CHANNELS, ch);
            a.Set(MF_MT_AUDIO_PREFER_WAVEFORMATEX, 1u);
            a.Set(Source.SR.MF_MT_AUDIO_SAMPLES_PER_SECOND, rate);
            a.Set(MF_MT_AVG_BITRATE, rate * ch * 16);
            a.Set(MF.MF_MT_COMPRESSED, 0u);
            a.Set(MF.MF_MT_FIXED_SIZE_SAMPLES, 1u);
            a.Set(MF.MF_MT_MAJOR_TYPE, Source.SR.MFMediaType_Audio);
            a.Set(MF.MF_MT_SUBTYPE, sub);
            return t;
        }

        private static MFMediaType Aac((uint Ch, uint Rate, uint Bytes, uint Pl, uint Payload) e)
        {
            var t = new MFMediaType();
            var a = t.Attributes;
            a.Set(MF_MT_AM_FORMAT_TYPE, FORMAT_WaveFormatEx);
            a.Set(MF_MT_AAC_AUDIO_PROFILE_LEVEL_INDICATION, e.Pl);
            a.Set(MF_MT_AAC_PAYLOAD_TYPE, e.Payload);
            a.Set(Source.SR.MF_MT_AUDIO_AVG_BYTES_PER_SECOND, e.Bytes);
            a.Set(Source.SR.MF_MT_AUDIO_BITS_PER_SAMPLE, 16u);
            a.Set(Source.SR.MF_MT_AUDIO_BLOCK_ALIGNMENT, 1u);
            a.Set(Source.SR.MF_MT_AUDIO_NUM_CHANNELS, e.Ch);
            a.Set(MF_MT_AUDIO_PREFER_WAVEFORMATEX, 1u);
            a.Set(Source.SR.MF_MT_AUDIO_SAMPLES_PER_SECOND, e.Rate);
            a.Set(MF_MT_AVG_BITRATE, e.Bytes * 8);
            a.Set(MF.MF_MT_COMPRESSED, 1u);
            a.Set(MF.MF_MT_FIXED_SIZE_SAMPLES, 0u);
            a.Set(MF.MF_MT_MAJOR_TYPE, Source.SR.MFMediaType_Audio);
            a.Set(MF.MF_MT_SUBTYPE, Source.SR.MFAudioFormat_AAC);
            return t;
        }

        protected override int InputAvailable(uint i, out MFMediaType? type)
        {
            type = null;
            if (i >= s_rates.Length * s_channels.Length * 2) return MF.MF_E_NO_MORE_TYPES;
            uint rate = s_rates[i / 8], ch = s_channels[i / 2 % 4];
            type = Pcm(i % 2 == 0 ? Source.SR.MFAudioFormat_PCM : Source.SR.MFAudioFormat_Float, ch, rate);
            return MF.S_OK;
        }

        protected override int OutputAvailable(uint i, out MFMediaType? type)
        {
            type = null;
            if (i >= Table.Length) return MF.MF_E_NO_MORE_TYPES;
            type = Aac(Table[i]);
            return MF.S_OK;
        }

        private (uint Ch, uint Rate)? Side(IntPtr kept)
        {
            if (kept == IntPtr.Zero) return null;
            var t = Read(kept);
            try { return (t.Attributes.U32(Source.SR.MF_MT_AUDIO_NUM_CHANNELS), t.Attributes.U32(Source.SR.MF_MT_AUDIO_SAMPLES_PER_SECOND)); }
            finally { t.Release(); }
        }

        protected override int CheckInput(MFMediaType t, out List<(Guid, MFValue)> fill)
        {
            fill = new();
            var a = t.Attributes;
            if (a.Guid(MF.MF_MT_MAJOR_TYPE) != Source.SR.MFMediaType_Audio || t.Subtype != Source.SR.MFAudioFormat_PCM) return MF.MF_E_INVALIDMEDIATYPE;
            uint ch = a.U32(Source.SR.MF_MT_AUDIO_NUM_CHANNELS), rate = a.U32(Source.SR.MF_MT_AUDIO_SAMPLES_PER_SECOND);
            if (a.U32(Source.SR.MF_MT_AUDIO_BITS_PER_SAMPLE, 16) != 16 || Array.IndexOf(s_channels, ch) < 0 || Array.IndexOf(s_rates, rate) < 0) return MF.MF_E_INVALIDMEDIATYPE;
            if (Side(Out) is { } o && (o.Ch != ch || o.Rate != rate)) return MF.MF_E_INVALIDMEDIATYPE;
            if (!PlatformAudioEncoder.Supports(AudioCodec.AAC)) return MF.MF_E_INVALIDMEDIATYPE;
            var p = Pcm(Source.SR.MFAudioFormat_PCM, ch, rate);
            fill = p.Attributes.All().Select(kv => (kv.Item1, MFAttributesObject.Copy(kv.Item2))).ToList();
            p.Release();
            return MF.S_OK;
        }

        /// <summary>The entry of the table the type is: its channels, rate and bytes a second, and its profile level and payload where it gives them.</summary>
        private static int Entry(MFMediaType t)
        {
            var a = t.Attributes;
            uint ch = a.U32(Source.SR.MF_MT_AUDIO_NUM_CHANNELS), rate = a.U32(Source.SR.MF_MT_AUDIO_SAMPLES_PER_SECOND), bytes = a.U32(Source.SR.MF_MT_AUDIO_AVG_BYTES_PER_SECOND);
            uint? pl = a.Get(MF_MT_AAC_AUDIO_PROFILE_LEVEL_INDICATION) is { Type: MF.VT_UI4 } p ? (uint)p.U : null;
            uint payload = a.U32(MF_MT_AAC_PAYLOAD_TYPE, 0);
            for (int i = 0; i < Table.Length; i++)
            {
                var e = Table[i];
                if (e.Ch == ch && e.Rate == rate && e.Bytes == bytes && e.Payload == payload && (pl == null || pl == e.Pl)) return i;
            }
            return -1;
        }

        protected override int CheckOutput(MFMediaType t, bool test, out List<(Guid, MFValue)> fill)
        {
            fill = new();
            var a = t.Attributes;
            if (a.Guid(MF.MF_MT_MAJOR_TYPE) != Source.SR.MFMediaType_Audio || t.Subtype != Source.SR.MFAudioFormat_AAC) return MF.MF_E_INVALIDMEDIATYPE;
            if (a.U32(Source.SR.MF_MT_AUDIO_BITS_PER_SAMPLE, 16) != 16) return MF.MF_E_INVALIDMEDIATYPE;
            int i = Entry(t);
            if (i < 0) return MF.MF_E_INVALIDMEDIATYPE;
            var e = Table[i];
            if (Side(In) is { } inp && (inp.Ch != e.Ch || inp.Rate != e.Rate)) return MF.MF_E_INVALIDMEDIATYPE;
            if (!PlatformAudioEncoder.Supports(AudioCodec.AAC)) return MF.MF_E_INVALIDMEDIATYPE;
            if (!test) _entry = i;
            var full = Aac(e);
            full.Attributes.Set(MF.MF_MT_USER_DATA, MFValue.Of(UserData(e)));
            fill = full.Attributes.All().Select(kv => (kv.Item1, MFAttributesObject.Copy(kv.Item2))).ToList();
            full.Release();
            return MF.S_OK;
        }

        private static uint ObjectType(uint pl) => pl switch { 0x2C or 0x2E => 5, 0x30 or 0x52 => 29, _ => 2 };

        /// <summary>HEAACWAVEINFO's tail: payload type, profile level, struct type, reserved; then the AudioSpecificConfig.</summary>
        private static byte[] UserData((uint Ch, uint Rate, uint Bytes, uint Pl, uint Payload) e)
        {
            var asc = Asc(e);
            var b = new byte[12 + asc.Length];
            b[0] = (byte)e.Payload; b[2] = (byte)e.Pl;
            asc.CopyTo(b, 12);
            return b;
        }

        private static readonly uint[] s_ascRates = { 96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050, 16000, 12000, 11025, 8000, 7350 };

        /// <summary>The AudioSpecificConfig of AAC-LC (HE's of its core and the SBR's rate), of the rate and channels.</summary>
        private static byte[] Asc((uint Ch, uint Rate, uint Bytes, uint Pl, uint Payload) e)
        {
            uint ot = ObjectType(e.Pl);
            uint cfg = e.Ch == 8 ? 7 : e.Ch;
            if (ot == 29) cfg = 1;
            int ri = Array.IndexOf(s_ascRates, e.Rate);
            if (ot == 2) { int v = (2 << 11) | (ri << 7) | ((int)cfg << 3); return new[] { (byte)(v >> 8), (byte)v }; }
            // explicit hierarchical signalling: the SBR object type (5 bits), the core's rate index (half the rate), the channel
            // configuration, the output's rate index, the core's object type (AAC-LC), GASpecificConfig's three bits of 0
            int core = Array.IndexOf(s_ascRates, e.Rate / 2);
            uint bits = (ot << 27) | ((uint)core << 23) | (cfg << 19) | ((uint)ri << 15) | (2u << 10);
            return new[] { (byte)(bits >> 24), (byte)(bits >> 16), (byte)(bits >> 8), (byte)bits };
        }

        protected override void TypesChanged()
        {
            _encoder?.Dispose(); _encoder = null;
            _packets.Clear(); _t0 = null; _packetsOut = 0; _first = true; _framesIn = 0;
        }

        protected override uint OutputSize() => Side(Out) is { } o ? 768 * o.Ch : 0;

        private (uint Ch, uint Rate, uint Bytes, uint Pl, uint Payload) Current() => Table[Math.Max(0, _entry)];

        protected override int Input(IntPtr sample)
        {
            var e = Current();
            if (_encoder == null)
            {
                _encoder = PlatformAudioEncoder.Create(AudioCodec.AAC, new AudioEncoderOptions { Channels = e.Ch, SampleRate = e.Rate, BitsPerSample = 16, AacBitrate = e.Bytes * 8, AacObjectType = ObjectType(e.Pl) });
                if (_encoder == null) return MF.E_FAIL;
            }
            _t0 ??= Time(sample) ?? 0;
            var pcm = Bytes(sample);
            _framesIn += pcm.Length / (2 * e.Ch);
            _encoder.Push(pcm, 0);
            Collect();
            return MF.S_OK;
        }

        private void Collect()
        {
            if (_encoder == null) return;
            while (_encoder.TryTake(ref _buffer, out uint len)) _packets.Enqueue(_buffer.AsSpan(0, (int)len).ToArray());
        }

        protected override int Output(IntPtr sample)
        {
            Collect();
            {
                // where Windows' encoder would have a packet by now, the system's (GStreamer's runs on threads of its own) is waited for
                var c = Current();
                uint f = ObjectType(c.Pl) == 2 ? 1024u : 2048u;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (_packets.Count == 0 && _encoder != null && _framesIn >= (_packetsOut + 1) * f + 2048 && sw.ElapsedMilliseconds < (OperatingSystem.IsBrowser() ? 3000 : 200)) { System.Threading.Thread.Sleep(2); Collect(); }
            }
            if (_packets.Count == 0) { _draining = false; return MF.MF_E_TRANSFORM_NEED_MORE_INPUT; }
            var e = Current();
            var packet = _packets.Peek();
            uint frames = ObjectType(e.Pl) == 2 ? 1024u : 2048u;
            // Windows' encoder looks 2048 frames ahead: a packet comes once that much has come after it, or at the drain
            if (!_draining && _framesIn < (_packetsOut + 1) * frames + 2048) return MF.MF_E_TRANSFORM_NEED_MORE_INPUT;
            // at the drain: the packets of the input and the encoder's priming (2112 frames of the core's); what a system's
            // encoder pads after them (WebCodecs' does) Windows' has not
            if (_draining && _packetsOut >= (_framesIn + 2112 * (frames / 1024) + frames - 1) / frames) { _packets.Clear(); _draining = false; return MF.MF_E_TRANSFORM_NEED_MORE_INPUT; }
            long time = _t0!.Value + _packetsOut * frames * 10_000_000L / e.Rate, dur = frames * 10_000_000L / e.Rate;
            byte[] bytes = e.Payload == 1 ? Adts(packet, e) : packet;
            var attrs = new List<(Guid, MFValue)> { (MF.MFSampleExtension_CleanPoint, MFValue.Of(1u)) };
            if (_first) attrs.Add((MF.MFSampleExtension_Discontinuity, MFValue.Of(1u)));
            int hr = Write(sample, bytes, time, dur, attrs);
            if (hr < 0) return hr;
            _packets.Dequeue();
            _packetsOut++;
            _first = false;
            return MF.S_OK;
        }

        /// <summary>The raw packet with an ADTS header (no CRC): MPEG-4, its object type, rate index and channels.</summary>
        private static byte[] Adts(byte[] packet, (uint Ch, uint Rate, uint Bytes, uint Pl, uint Payload) e)
        {
            int len = packet.Length + 7, ri = Array.IndexOf(s_ascRates, ObjectType(e.Pl) == 2 ? e.Rate : e.Rate / 2);
            uint cfg = e.Ch == 8 ? 7 : e.Ch;
            var h = new byte[len];
            h[0] = 0xFF; h[1] = 0xF1;
            h[2] = (byte)((1 << 6) | (ri << 2) | (int)(cfg >> 2));
            h[3] = (byte)((cfg & 3) << 6 | (len >> 11));
            h[4] = (byte)(len >> 3);
            h[5] = (byte)((len & 7) << 5 | 0x1F);
            h[6] = 0xFC;
            packet.CopyTo(h, 7);
            return h;
        }

        protected override void Flush()
        {
            _encoder?.Dispose(); _encoder = null;
            _packets.Clear(); _t0 = null; _packetsOut = 0; _first = true; _framesIn = 0;
        }

        protected override void Drain()
        {
            _draining = true;
            if (_encoder == null) return;
            _encoder.BeginDrain();
            Collect();
            _encoder.EndDrain();
            _encoder.Dispose();
            _encoder = null;
        }

        // ---- ICodecAPI: answered, of no property (as Windows' AAC encoder) --------------------------------

        private static Vtable BuildCodecApi()
        {
            var vt = new Vtable("ICodecAPI", 18, MF.IID_ICodecAPI);
            CodecApiSlots.Fill(vt);
            return vt;
        }
    }
}
