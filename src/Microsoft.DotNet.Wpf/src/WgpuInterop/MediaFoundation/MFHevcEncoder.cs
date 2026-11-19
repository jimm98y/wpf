// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Windows' HEVC encoder MFT ("HEVCVideoExtensionEncoder", of the HEVC Video Extensions; there is no CLSID_MSH265EncoderMFT
// on Windows of today), over the system's HEVC encoder (Codecs/PlatformEncoders.cs), as Windows' answers
// (MFHevcEncoderProbe, HevcEncoder.windows.txt):
//
//   - IMFTransform, ICodecAPI and IMFRealTimeClientEx; one stream each way; its attributes MFT_ENCODER_SUPPORTS_CONFIG_EVENT;
//   - its output type alone of its major type and subtype; inputs (IYUV, NV12, I422, I444) once the output is set, each of
//     the output's size and rate; Main (or no profile) taken, Main10 refused at the set (not its test), an odd size refused;
//   - a picture in, its access unit out at once (no reordering, no access unit delimiter: the first of VPS, SPS, PPS and
//     its IDR), of the input's time as its time and decode time, a clean point of an IRAP picture;
//   - its current output type the type set (no sequence header: the sink writer takes the parameter sets of the first
//     access unit).
//
// Not here: MFSampleExtension_VideoEncodeQP (Windows' encoder gives each picture's QP; the system's encoders do not
// tell it). 4:2:2 and 4:4:4 inputs are encoded as 4:2:0, their chroma averaged.
//

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Wpf.Interop.WebGpu.Direct3D;
using Microsoft.Wpf.Interop.WebGpu.MediaFoundation.Codecs;

namespace Microsoft.Wpf.Interop.WebGpu.MediaFoundation
{
    internal sealed unsafe class MFHevcEncoder : MFEncoder
    {
        public const string Name = "HEVCVideoExtensionEncoder";
        public static readonly Guid MFVideoFormat_I422 = new("32323449-0000-0010-8000-00aa00389b71"), MFVideoFormat_I444 = new("34343449-0000-0010-8000-00aa00389b71");
        public static readonly Guid[] Inputs = { MF.MFVideoFormat_IYUV, MF.MFVideoFormat_NV12, MFVideoFormat_I422, MFVideoFormat_I444 };
        /// <summary>The input types its activate lists (MFT_INPUT_TYPES_Attributes), in Windows' order.</summary>
        public static readonly Guid[] InputsListed = { MF.MFVideoFormat_NV12, MF.MFVideoFormat_IYUV, MF.MFVideoFormat_YV12, MF.MFVideoFormat_YUY2 };
        private static readonly Guid MFT_ENCODER_SUPPORTS_CONFIG_EVENT = new("86a355ae-3a77-4ec4-9f31-01149a4e92de");
        private static readonly Guid MF_MT_MPEG2_PROFILE = new("ad76a80b-2d5c-4e0b-b375-64e520137036"), MF_MT_AVG_BITRATE = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
        private static readonly Guid MF_MT_VIDEO_NOMINAL_RANGE = new("c21b8ee5-b956-4071-8daf-325edf5cab11");
        private static readonly Guid MFSampleExtension_VideoEncodePictureType = new("973704e6-cd14-483c-8f20-c9fc0928bad5");

        private static readonly Vtable s_codecApi = BuildCodecApi(), s_realtime = MFVideoProcessor.BuildRealTime();

        private PlatformVideoEncoder? _encoder;
        private readonly Queue<(byte[] Unit, bool Key)> _out = new();
        private readonly Queue<(long Time, long Duration)> _times = new();
        private byte[] _buffer = new byte[65536];

        // ICodecAPI's settings
        private uint _rateControl, _meanBitrate, _quality = 65, _qualityVsSpeed, _gop, _maxQp = 51, _minQp, _bufferSize, _maxBitrate;
        private bool _lowLatency, _forceKey;
        private ulong _qp = 26;

        public MFHevcEncoder() : base(new[] { s_codecApi, s_realtime })
        {
            Bag.Attributes.Set(MFT_ENCODER_SUPPORTS_CONFIG_EVENT, 1u);
        }

        public override void Shutdown()
        {
            lock (Lock)
            {
                Shut = true;
                _encoder?.Dispose(); _encoder = null;
                _out.Clear(); _times.Clear();
            }
        }

        private MFMediaType? OutType => Out == IntPtr.Zero ? null : Read(Out);

        protected override int OutputAvailable(uint i, out MFMediaType? type)
        {
            type = i == 0 ? MFMediaType.Video(MF.MFVideoFormat_HEVC) : null;
            return type == null ? MF.MF_E_NO_MORE_TYPES : MF.S_OK;
        }

        protected override int InputAvailable(uint i, out MFMediaType? type)
        {
            type = null;
            var o = OutType;
            if (o == null) return MF.MF_E_TRANSFORM_TYPE_NOT_SET;
            try
            {
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
            finally { o.Release(); }
        }

        protected override int CheckOutput(MFMediaType t, bool test, out List<(Guid, MFValue)> fill)
        {
            fill = new();
            if (t.Attributes.Guid(MF.MF_MT_MAJOR_TYPE) != MF.MFMediaType_Video || t.Subtype != MF.MFVideoFormat_HEVC) return MF.MF_E_INVALIDMEDIATYPE;
            if (t.Attributes.Get(MF.MF_MT_FRAME_SIZE) == null) return MF.MF_E_ATTRIBUTENOTFOUND;
            var (w, h) = t.FrameSize;
            if (w == 0 || h == 0 || w % 2 != 0 || h % 2 != 0) return MF.MF_E_INVALIDMEDIATYPE;
            if (test) return MF.S_OK;
            // (Main10 passes the test, and is refused at the set)
            uint p = t.Attributes.U32(MF_MT_MPEG2_PROFILE);
            if (p is not (0 or 1)) return unchecked((int)0xC00D36BD);
            if (t.Attributes.Get(MF.MF_MT_FRAME_RATE) == null || t.Attributes.Get(MF.MF_MT_INTERLACE_MODE) == null) return MF.MF_E_ATTRIBUTENOTFOUND;
            if (!PlatformVideoEncoder.Supports(VideoCodec.H265)) return MF.MF_E_INVALIDMEDIATYPE;
            return MF.S_OK;
        }

        protected override int CheckInput(MFMediaType t, out List<(Guid, MFValue)> fill)
        {
            fill = new();
            var o = OutType;
            if (o == null) return MF.MF_E_TRANSFORM_TYPE_NOT_SET;
            try
            {
                if (t.Attributes.Guid(MF.MF_MT_MAJOR_TYPE) != MF.MFMediaType_Video || Array.IndexOf(Inputs, t.Subtype) < 0) return MF.MF_E_INVALIDMEDIATYPE;
                if (t.FrameSize != o.FrameSize) return MF.MF_E_INVALIDMEDIATYPE;
                if (t.Attributes.Get(MF.MF_MT_FRAME_RATE) is { } r && r.U != o.Attributes.U64(MF.MF_MT_FRAME_RATE)) return MF.MF_E_INVALIDMEDIATYPE;
                return MF.S_OK;
            }
            finally { o.Release(); }
        }

        protected override void TypesChanged()
        {
            _encoder?.Dispose(); _encoder = null;
            _out.Clear(); _times.Clear();
        }

        /// <summary>The output buffer: 1.875 bytes a pixel (Windows' 144000 of 320 x 240).</summary>
        protected override uint OutputSize()
        {
            var o = OutType;
            if (o == null) return 0;
            try { var (w, h) = o.FrameSize; return Math.Max(32768u, w * h * 15 / 8); }
            finally { o.Release(); }
        }

        private VideoEncoderOptions Options(MFMediaType o)
        {
            var (w, h) = o.FrameSize;
            var (n, d) = MF.Unpack(o.Attributes.U64(MF.MF_MT_FRAME_RATE));
            return new VideoEncoderOptions
            {
                Width = w, Height = h, FpsNom = n == 0 ? 30 : n, FpsDenom = d == 0 ? 1 : d,
                Bitrate = _meanBitrate != 0 ? _meanBitrate : o.Attributes.U32(MF_MT_AVG_BITRATE, 500_000),
                RateControl = _rateControl switch { 0 => RateControlMode.ConstantBitrate, 1 or 2 => RateControlMode.VariableBitrate, 3 => RateControlMode.Quality, _ => RateControlMode.Default },
                Quality = _quality, KeyFrameInterval = _gop,
            };
        }

        protected override int Input(IntPtr sample)
        {
            var o = OutType; var i = Read(In);
            try
            {
                if (_encoder == null)
                {
                    _encoder = PlatformVideoEncoder.Create(VideoCodec.H265, Options(o!));
                    if (_encoder == null) return MF.E_FAIL;
                }
                var (w, h) = o!.FrameSize;
                var nv12 = ToNv12(Bytes(sample), i.Subtype, (int)w, (int)h);
                long time = Time(sample) ?? 0, dur = Duration(sample) ?? FrameDuration(o);
                _times.Enqueue((time, dur));
                _encoder.Push(nv12, time);
                Collect();
                return MF.S_OK;
            }
            finally { o?.Release(); i.Release(); }
        }

        /// <summary>4:2:2 (I422) and 4:4:4 (I444) planar to NV12, each 4:2:0 sample the mean of those it covers; the others as the H.264 encoder's.</summary>
        internal static byte[] ToNv12(byte[] src, Guid sub, int w, int h)
        {
            if (sub != MFVideoFormat_I422 && sub != MFVideoFormat_I444) return MFH264Encoder.ToNv12(src, sub, w, h);
            int sx = sub == MFVideoFormat_I444 ? 1 : 2, pw = w / sx, cw = w / 2, ch = h / 2;
            var dst = new byte[w * h + 2 * cw * ch];
            Array.Copy(src, dst, Math.Min(w * h, src.Length));
            byte At(int i) => i < src.Length ? src[i] : (byte)128;
            int u = w * h, v = w * h + pw * h;
            for (int y = 0; y < ch; y++)
                for (int x = 0; x < cw; x++)
                {
                    int su = 0, sv = 0, n = 0;
                    for (int dy = 0; dy < 2; dy++)
                        for (int dx = 0; dx < (sx == 1 ? 2 : 1); dx++)
                        {
                            int at = (2 * y + dy) * pw + (sx == 1 ? 2 * x + dx : x);
                            su += At(u + at); sv += At(v + at); n++;
                        }
                    dst[w * h + y * w + 2 * x] = (byte)((su + n / 2) / n);
                    dst[w * h + y * w + 2 * x + 1] = (byte)((sv + n / 2) / n);
                }
            return dst;
        }

        private static long FrameDuration(MFMediaType o)
        {
            var (n, d) = MF.Unpack(o.Attributes.U64(MF.MF_MT_FRAME_RATE));
            return n > 0 ? 10_000_000L * d / n : 0;
        }

        private void Collect()
        {
            if (_encoder == null) return;
            while (_encoder.TryTake(ref _buffer, out uint len, out _))
            {
                var unit = _buffer.AsSpan(0, (int)len).ToArray();
                _out.Enqueue((unit, IsIrap(unit)));
            }
        }

        /// <summary>Whether an access unit holds an IRAP picture (NAL types 16 to 23).</summary>
        private static bool IsIrap(byte[] au)
        {
            for (int i = 0; i + 3 < au.Length; i++)
                if (au[i] == 0 && au[i + 1] == 0 && au[i + 2] == 1) { int type = (au[i + 3] >> 1) & 0x3F; if (type is >= 16 and <= 23) return true; i += 2; }
            return false;
        }

        protected override int Output(IntPtr sample)
        {
            Collect();
            // Windows' encoder has each picture out at once: the system's waited for (made to finish what it holds)
            var sw = System.Diagnostics.Stopwatch.StartNew();
            bool completed = false;
            while (_out.Count == 0 && _encoder != null && _times.Count > 0 && sw.ElapsedMilliseconds < 500)
            {
                if (!completed && sw.ElapsedMilliseconds >= 100) { _encoder.CompleteUntil(_times.Peek().Time); completed = true; }
                System.Threading.Thread.Sleep(2); Collect();
            }
            if (_out.Count == 0) return MF.MF_E_TRANSFORM_NEED_MORE_INPUT;
            var (unit, key) = _out.Peek();
            var (time, dur) = _times.Count > 0 ? _times.Peek() : (0L, 0L);
            var attrs = new List<(Guid, MFValue)>
            {
                (MFSampleExtension_DecodeTimestamp, MFValue.Of((ulong)time)),
                (new Guid("9154733f-e1bd-41bf-81d3-fcd918f71332"), MFValue.Of(0u)),
                (MFSampleExtension_VideoEncodePictureType, MFValue.Of(key ? 0u : 1u)),
                (MF.MFSampleExtension_CleanPoint, MFValue.Of(key ? 1u : 0u)),
            };
            int hr = Write(sample, unit, time, dur, attrs);
            if (hr < 0) return hr;
            _out.Dequeue();
            if (_times.Count > 0) _times.Dequeue();
            return MF.S_OK;
        }

        protected override void Flush()
        {
            _encoder?.Dispose(); _encoder = null;
            _out.Clear(); _times.Clear();
        }

        protected override void Drain()
        {
            if (_encoder != null) { _encoder.BeginDrain(); Collect(); _encoder.EndDrain(); _encoder.Dispose(); _encoder = null; }
        }

        // ---- ICodecAPI: the properties Windows' encoder supports (S_OK), knows of (S_FALSE), of their defaults ----

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

        private static readonly Guid[] s_supported = { AVEncCommonRateControlMode, AVEncCommonMeanBitRate, AVEncCommonQuality, AVEncCommonQualityVsSpeed, AVEncMPVGOPSize,
            AVLowLatencyMode, AVEncVideoForceKeyFrame, AVEncCommonBufferSize, AVEncCommonMaxBitRate, AVEncVideoEncodeQP, AVEncVideoMinQP, AVEncVideoMaxQP, AVEncNumWorkerThreads };
        private static readonly Guid[] s_known = { AVEncMPVDefaultBPictureCount, AVEncH264CABACEnable, AVEncVideoTemporalLayerCount, AVEncMPVProfile, AVEncAdaptiveMode,
            AVEncSliceControlMode, AVEncVideoLTRBufferControl };

        private static Vtable BuildCodecApi()
        {
            var vt = new Vtable("ICodecAPI", 18, MF.IID_ICodecAPI);
            CodecApiSlots.Fill(vt);
            vt.Set(3, (delegate* unmanaged<IntPtr, Guid*, int>)&IsSupported);
            vt.Set(4, (delegate* unmanaged<IntPtr, Guid*, int>)&IsSupported);
            vt.Set(7, (delegate* unmanaged<IntPtr, Guid*, byte*, int>)&GetValue);
            vt.Set(8, (delegate* unmanaged<IntPtr, Guid*, byte*, int>)&GetValue);
            vt.Set(9, (delegate* unmanaged<IntPtr, Guid*, byte*, int>)&SetValue);
            return vt;
        }

        [UnmanagedCallersOnly]
        private static int IsSupported(IntPtr self, Guid* api)
        {
            if (api == null) return MF.E_POINTER;
            return Array.IndexOf(s_supported, *api) >= 0 ? MF.S_OK : Array.IndexOf(s_known, *api) >= 0 ? 1 : MF.E_NOTIMPL;
        }

        /// <summary>VARIANT: vt at 0, the value at 8 (VT_UI4 19, VT_UI8 21, VT_BOOL 11 of -1 or 0).</summary>
        [UnmanagedCallersOnly]
        private static int GetValue(IntPtr self, Guid* api, byte* v)
        {
            if (From<MFHevcEncoder>(self) is not { } e || api == null || v == null) return MF.E_POINTER;
            new Span<byte>(v, 16).Clear();
            var g = *api;
            if (g == AVEncSliceControlMode) return unchecked((int)0x80040314);
            if (g == AVEncVideoEncodeQP) { *(ushort*)v = 21; *(ulong*)(v + 8) = e._qp; return MF.S_OK; }
            if (g == AVLowLatencyMode) { *(ushort*)v = 11; *(short*)(v + 8) = e._lowLatency ? (short)-1 : (short)0; return MF.S_OK; }
            uint? value = g == AVEncCommonRateControlMode ? e._rateControl : g == AVEncCommonMeanBitRate ? (e._meanBitrate != 0 ? e._meanBitrate : e.OutBitrate())
                : g == AVEncCommonQuality ? e._quality : g == AVEncCommonQualityVsSpeed ? e._qualityVsSpeed : g == AVEncMPVGOPSize ? e._gop
                : g == AVEncMPVDefaultBPictureCount ? 0u : g == AVEncVideoForceKeyFrame ? (e._forceKey ? 1u : 0u) : g == AVEncCommonBufferSize ? e._bufferSize
                : g == AVEncCommonMaxBitRate ? e._maxBitrate : g == AVEncVideoMinQP ? e._minQp : g == AVEncVideoMaxQP ? e._maxQp : g == AVEncNumWorkerThreads ? 0u
                : g == AVEncVideoTemporalLayerCount ? 1u : g == AVEncVideoLTRBufferControl ? 0u : null;
            if (value is not uint u) return MF.E_NOTIMPL;
            *(ushort*)v = 19;
            *(uint*)(v + 8) = u;
            return MF.S_OK;
        }

        private uint OutBitrate() { lock (Lock) { var o = OutType; try { return o?.Attributes.U32(MF_MT_AVG_BITRATE, 500_000) ?? 500_000; } finally { o?.Release(); } } }

        [UnmanagedCallersOnly]
        private static int SetValue(IntPtr self, Guid* api, byte* v)
        {
            if (From<MFHevcEncoder>(self) is not { } e || api == null || v == null) return MF.E_POINTER;
            var g = *api;
            ushort vt = *(ushort*)v;
            uint u = vt == 11 ? (*(short*)(v + 8) != 0 ? 1u : 0u) : *(uint*)(v + 8);
            lock (e.Lock)
            {
                if (g == AVEncCommonRateControlMode) e._rateControl = u;
                else if (g == AVEncCommonMeanBitRate)
                {
                    if (e.Out == IntPtr.Zero) e._meanBitrate = u;
                    else if (e._encoder != null) { e._meanBitrate = u; e._encoder.SetBitrate(u); }
                }
                else if (g == AVEncCommonQuality) e._quality = u;
                else if (g == AVEncCommonQualityVsSpeed) e._qualityVsSpeed = u;
                else if (g == AVEncMPVGOPSize) e._gop = u;
                else if (g == AVLowLatencyMode) e._lowLatency = u != 0;
                else if (g == AVEncVideoForceKeyFrame) e._forceKey = u != 0;
                else if (g == AVEncCommonBufferSize) e._bufferSize = u;
                else if (g == AVEncCommonMaxBitRate) e._maxBitrate = u;
                else if (g == AVEncVideoMinQP) e._minQp = u;
                else if (g == AVEncVideoMaxQP) e._maxQp = u;
                else if (g == AVEncVideoEncodeQP) e._qp = vt == 21 ? *(ulong*)(v + 8) : u;
                else return MF.E_NOTIMPL;
            }
            return MF.S_OK;
        }
    }
}
