// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// A stream's transforms in the sink writer, as Windows' writer chains them (MFSinkTransformProbe,
// SinkTransform.windows.txt): the encoder MFT (H.264's, HEVC's, AAC's) driven through its vtable, after a converter
// where the encoder takes not the input as it is.
//
//   - video: the encoder takes IYUV, YV12, NV12 and YUY2 (HEVC's: IYUV, NV12, I422, I444) of the target's size. Another subtype or size goes
//     through a video processor first, to the first of those the encoder takes that is not of the input's
//     own layout (I420 to YV12; RGB32, or NV12 of another size, to IYUV);
//   - audio: the encoder takes 16-bit PCM of the target's rate. Float, other bits or another rate go
//     through an audio effect first (Windows' resampler), whose output comes in samples of 1024 frames.
//
// The converters are the shim's own (MFConverterStandIn: an IMFTransform of their types for
// GetTransformForStream); the encoder is the MFT the application can reach through GetServiceForStream.
//

using System;
using System.Collections.Generic;
using Microsoft.Wpf.Interop.WebGpu.Direct3D;
using Microsoft.Wpf.Interop.WebGpu.MediaFoundation.Codecs;
using Microsoft.Wpf.Interop.WebGpu.MediaFoundation.Source;

namespace Microsoft.Wpf.Interop.WebGpu.MediaFoundation.Sink
{
    internal sealed unsafe class SinkChain : IDisposable
    {
        private const uint MFT_MESSAGE_COMMAND_DRAIN = 1, MFT_MESSAGE_NOTIFY_BEGIN_STREAMING = 0x10000000, MFT_MESSAGE_NOTIFY_START_OF_STREAM = 0x10000003;
        private const uint MFT_MESSAGE_NOTIFY_END_OF_STREAM = 0x10000002;
        private static readonly Guid MF_MT_VIDEO_NOMINAL_RANGE = new("c21b8ee5-b956-4071-8daf-325edf5cab11");
        private static readonly Guid MF_MT_AUDIO_CHANNEL_MASK = new("55fb5765-644a-4caf-8479-938983bb1588");

        /// <summary>The transforms in order, each AddRef'd: the converter (where there is one), then the encoder.</summary>
        public readonly List<(Guid Category, IntPtr Transform)> Transforms = new();
        public IntPtr Encoder { get; private set; }
        private readonly bool _video;

        // video: the input's subtype and size, the converter's subtype and the target's size
        private readonly Guid _inSub, _toSub;
        private readonly int _inW, _inH, _w, _h;
        private readonly bool _convertVideo;

        // audio: the converter (its output in samples of 1024 frames, timed from the first input's time)
        private readonly AudioConverter? _audio;
        private readonly int _rate, _channels;
        private readonly List<byte> _pending = new();
        private long? _t0;
        private long _framesOut;

        private readonly Queue<(byte[] Data, long Time, long Duration, bool Clean)> _out = new();

        /// <summary>The samples given to the encoder, and the last one's time (the writer's statistics: encoded and processed).</summary>
        public long Encoded, LastEncoded;

        private SinkChain(bool video, IntPtr encoder, Guid inSub, Guid toSub, int inW, int inH, int w, int h, AudioConverter? audio, int rate, int channels)
        {
            _video = video; Encoder = encoder; _inSub = inSub; _toSub = toSub; _inW = inW; _inH = inH; _w = w; _h = h;
            _convertVideo = video && (inSub != toSub || inW != w || inH != h);
            _audio = audio; _rate = rate; _channels = channels;
        }

        /// <summary>The planes of a subtype: I420 and IYUV are one layout.</summary>
        private static Guid Layout(Guid sub) => sub == MF.MFVideoFormat_I420 ? MF.MFVideoFormat_IYUV : sub;

        /// <summary>
        /// The chain from the input to the target (H.264 or AAC), its encoder's types set: S_OK, or the encoder's answer
        /// where it takes neither.
        /// </summary>
        public static int Create(MFMediaType target, MFMediaType input, IntPtr parameters, out SinkChain? chain)
        {
            chain = null;
            bool video = target.Attributes.Guid(MF.MF_MT_MAJOR_TYPE) == MF.MFMediaType_Video;
            bool hevc = video && target.Subtype == MF.MFVideoFormat_HEVC;
            var inputs = hevc ? MFHevcEncoder.Inputs : MFH264Encoder.Inputs;
            var category = video ? MFEncoder.MFT_CATEGORY_VIDEO_ENCODER : MFEncoder.MFT_CATEGORY_AUDIO_ENCODER;
            // an encoder the application registered in the process before the shim's own (as Windows' writer takes it)
            IntPtr local = LocalTransforms.First(category, video ? MF.MFMediaType_Video : SR.MFMediaType_Audio, Guid.Empty, target.Subtype);
            IntPtr encoder = IntPtr.Zero;
            MFMediaType? convIn = null, convOut = null, encIn;
            SinkChain c;
            if (video)
            {
                var (w, h) = target.FrameSize;
                var (iw, ih) = input.FrameSize;
                Guid to = input.Subtype;
                if (Array.IndexOf(inputs, input.Subtype) < 0 || (iw, ih) != (w, h))
                {
                    to = Array.Find(inputs, s => Layout(s) != Layout(input.Subtype));
                    convIn = input.Clone();
                    convOut = input.Clone();
                    convOut.Attributes.Set(MF.MF_MT_SUBTYPE, to);
                    convOut.Attributes.Set(MF.MF_MT_FRAME_SIZE, MF.Pack(w, h));
                    convOut.Attributes.Set(MF_MT_VIDEO_NOMINAL_RANGE, 2u);
                    encIn = convOut.Clone();
                }
                else encIn = input.Clone();
                c = new SinkChain(true, IntPtr.Zero, input.Subtype, to, (int)iw, (int)ih, (int)w, (int)h, null, 0, 0);
            }
            else
            {
                var a = input.Attributes;
                bool fl = input.Subtype == MediaFormats.Float;
                uint bits = a.U32(SR.MF_MT_AUDIO_BITS_PER_SAMPLE, fl ? 32u : 16u), ch = a.U32(SR.MF_MT_AUDIO_NUM_CHANNELS, 2), rate = a.U32(SR.MF_MT_AUDIO_SAMPLES_PER_SECOND, 44100);
                uint trate = target.Attributes.U32(SR.MF_MT_AUDIO_SAMPLES_PER_SECOND, 44100);
                AudioConverter? conv = null;
                if (fl || bits != 16 || rate != trate)
                {
                    convIn = Wave(fl, bits, ch, rate);
                    convOut = Wave(false, 16, ch, trate);
                    encIn = convOut.Clone();
                    conv = new AudioConverter(fl, (int)bits, (int)ch, (int)rate, false, 16, (int)ch, (int)trate);
                }
                else encIn = input.Clone();
                c = new SinkChain(false, IntPtr.Zero, Guid.Empty, Guid.Empty, 0, 0, 0, 0, conv, (int)trate, (int)ch);
            }
            // H.264: Baseline where the target names no profile, the least level of its size, rate and bit rate where it names none
            var outType = target.Clone();
            if (video && !hevc)
            {
                if (outType.Attributes.U32(MF_MT_MPEG2_PROFILE) == 0) outType.Attributes.Set(MF_MT_MPEG2_PROFILE, 66u);
                if (outType.Attributes.U32(MF_MT_MPEG2_LEVEL) == 0)
                {
                    var (w, h) = target.FrameSize;
                    var (rn, rd) = MF.Unpack(target.Attributes.U64(MF.MF_MT_FRAME_RATE, MF.Pack(30, 1)));
                    outType.Attributes.Set(MF_MT_MPEG2_LEVEL, Level(w, h, rn, rd, target.Attributes.U32(SR.MF_MT_AVG_BITRATE), outType.Attributes.U32(MF_MT_MPEG2_PROFILE)));
                }
            }
            try
            {
                // SetInputMediaType's encoding parameters: the encoder's ICodecAPI values, set before its types (Windows' encoder
                // takes a mean bit rate then, and not between its types and its first frame)
                // the output first, as Windows' writer sets them (H.264's encoder takes no input before it)
                int Set(IntPtr e)
                {
                    if (parameters != IntPtr.Zero) Configure(e, parameters);
                    // the input first; an encoder that takes none before its output (H.264's) given the output, then the input
                    if (Mft.SetInputType(e, encIn.Pointer) >= 0) return Mft.SetOutputType(e, outType.Pointer);
                    int h = Mft.SetOutputType(e, outType.Pointer);
                    return h >= 0 ? Mft.SetInputType(e, encIn.Pointer) : h;
                }
                int hr = -1;
                if (local != IntPtr.Zero) { hr = Set(local); if (hr >= 0) encoder = local; else Com.Release(local); }
                if (encoder == IntPtr.Zero)
                {
                    MFEncoder own = hevc ? new MFHevcEncoder() : video ? new MFH264Encoder() : new MFAacEncoder();
                    hr = Set(own.Pointer);
                    if (hr < 0) { own.Release(); convIn?.Release(); convOut?.Release(); return hr; }
                    encoder = own.Pointer;
                }
                c.Encoder = encoder;
                if (convIn != null)
                    c.Transforms.Add((video ? ReaderEffects.MFT_CATEGORY_VIDEO_PROCESSOR : ReaderEffects.MFT_CATEGORY_AUDIO_EFFECT, new MFConverterStandIn(convIn, convOut!).Pointer));
                c.Transforms.Add((category, encoder));          // the encoder's own reference
                convIn?.Release(); convOut?.Release();
                chain = c;
                return MF.S_OK;
            }
            finally { encIn.Release(); outType.Release(); }
        }

        private static readonly Guid MF_MT_MPEG2_PROFILE = new("ad76a80b-2d5c-4e0b-b375-64e520137036"), MF_MT_MPEG2_LEVEL = new("96f66574-11c5-4015-8666-bff516436da7");

        /// <summary>H.264's levels (Table A-1): the level (times ten), its macroblocks a second, a frame's, its bit rate (kb/s; of High, 1.25 times). Not 1b.</summary>
        private static readonly (uint Level, long Mbps, long Fs, long Br)[] s_levels =
        {
            (10, 1485, 99, 64), (11, 3000, 396, 192), (12, 6000, 396, 384), (13, 11880, 396, 768), (20, 11880, 396, 2000), (21, 19800, 792, 4000),
            (22, 20250, 1620, 4000), (30, 40500, 1620, 10000), (31, 108000, 3600, 14000), (32, 216000, 5120, 20000), (40, 245760, 8192, 20000),
            (41, 245760, 8192, 50000), (42, 522240, 8704, 50000), (50, 589824, 22080, 135000), (51, 983040, 36864, 240000), (52, 2073600, 36864, 240000),
        };

        /// <summary>The least level of the picture's macroblocks, their rate and the bit rate (Windows' writer's, where the target names none).</summary>
        internal static uint Level(uint w, uint h, uint rn, uint rd, uint bitrate, uint profile)
        {
            long fs = (long)((w + 15) / 16) * ((h + 15) / 16);
            double mbps = rd > 0 ? (double)fs * rn / rd : fs * 30.0;
            double factor = profile == 100 ? 1250 : 1000;
            foreach (var l in s_levels)
                if (fs <= l.Fs && mbps <= l.Mbps && bitrate <= l.Br * factor) return l.Level;
            return 52;
        }

        /// <summary>Each of the attributes of 32 or 64 bits as the ICodecAPI value of its key (what the encoder has no property of, passed over).</summary>
        private static void Configure(IntPtr encoder, IntPtr attributes)
        {
            var iid = MF.IID_ICodecAPI; IntPtr api;
            if (((delegate* unmanaged<IntPtr, Guid*, IntPtr*, int>)Com.Fn(encoder, 0))(encoder, &iid, &api) < 0) return;
            try
            {
                uint n;
                if (((delegate* unmanaged<IntPtr, uint*, int>)Com.Fn(attributes, 30))(attributes, &n) < 0) return;
                var pv = stackalloc byte[24];
                for (uint i = 0; i < n; i++)
                {
                    new Span<byte>(pv, 24).Clear();
                    Guid k;
                    if (((delegate* unmanaged<IntPtr, uint, Guid*, byte*, int>)Com.Fn(attributes, 31))(attributes, i, &k, pv) < 0) continue;
                    // a PROPVARIANT of VT_UI4 or VT_UI8 is the VARIANT of it
                    if (*(ushort*)pv is MF.VT_UI4 or MF.VT_UI8) ((delegate* unmanaged<IntPtr, Guid*, byte*, int>)Com.Fn(api, 9))(api, &k, pv);
                    Ole.PropVariantClear(pv);
                }
            }
            finally { Com.Release(api); }
        }

        /// <summary>The type of a WAVEFORMATEX as MFInitMediaTypeFromWaveFormatEx makes it: of float, WAVEFORMATEXTENSIBLE's (its speakers).</summary>
        private static MFMediaType Wave(bool fl, uint bits, uint ch, uint rate)
        {
            var t = new MFMediaType();
            var a = t.Attributes;
            uint block = ch * bits / 8;
            a.Set(MF.MF_MT_MAJOR_TYPE, SR.MFMediaType_Audio);
            a.Set(MF.MF_MT_SUBTYPE, fl ? MediaFormats.Float : MediaFormats.PCM);
            a.Set(SR.MF_MT_AUDIO_NUM_CHANNELS, ch);
            a.Set(SR.MF_MT_AUDIO_SAMPLES_PER_SECOND, rate);
            a.Set(SR.MF_MT_AUDIO_BITS_PER_SAMPLE, bits);
            a.Set(SR.MF_MT_AUDIO_BLOCK_ALIGNMENT, block);
            a.Set(SR.MF_MT_AUDIO_AVG_BYTES_PER_SECOND, block * rate);
            a.Set(MF.MF_MT_ALL_SAMPLES_INDEPENDENT, 1u);
            if (fl) a.Set(MF_MT_AUDIO_CHANNEL_MASK, AudioConverter.Mask((int)ch));
            else { a.Set(SR.MF_MT_AUDIO_PREFER_WAVEFORMATEX, 1u); a.Set(MF.MF_MT_FIXED_SIZE_SAMPLES, 1u); }
            return t;
        }

        public void Begin()
        {
            Mft.ProcessMessage(Encoder, MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, 0);
            Mft.ProcessMessage(Encoder, MFT_MESSAGE_NOTIFY_START_OF_STREAM, 0);
        }

        /// <summary>A sample of the application's into the chain: converted, encoded; what the encoder gives queued.</summary>
        public void Push(byte[] data, long time, long duration)
        {
            if (_video)
            {
                if (_convertVideo)
                {
                    var nv12 = MFSinkWriter.ToNv12(data, _inSub, _inW, _inH);
                    if (_inW != _w || _inH != _h) nv12 = Scale(nv12, _inW, _inH, _w, _h);
                    data = FromNv12(nv12, _toSub, _w, _h);
                }
                Encode(data, time, duration);
                return;
            }
            if (_audio == null) { Encode(data, time, duration); return; }
            _t0 ??= time;
            _pending.AddRange(_audio.Convert(data));
            Chunks(false);
        }

        /// <summary>The converter's output in samples of 1024 frames (and, at the end, what is left).</summary>
        private void Chunks(bool end)
        {
            int frame = 2 * _channels, chunk = 1024 * frame;
            while (_pending.Count >= chunk || end && _pending.Count >= frame)
            {
                int n = Math.Min(chunk, _pending.Count / frame * frame);
                var b = _pending.GetRange(0, n).ToArray();
                _pending.RemoveRange(0, n);
                long t = _t0!.Value + _framesOut * 10_000_000L / _rate;
                _framesOut += n / frame;
                Encode(b, t, _t0.Value + _framesOut * 10_000_000L / _rate - t);
            }
        }

        private void Encode(byte[] data, long time, long duration)
        {
            var s = new MFSample();
            var buf = new MFMemoryBuffer((uint)data.Length);
            data.CopyTo(new Span<byte>(buf.LockBytes(true), data.Length));
            buf.CurrentLength = (uint)data.Length;
            s.SetBuffer(buf.Pointer);
            buf.Release();
            s.Time = time; s.Duration = duration;
            try
            {
                int hr = Mft.ProcessInput(Encoder, s.Pointer);
                if (hr == MF.MF_E_NOTACCEPTING) { Collect(); hr = Mft.ProcessInput(Encoder, s.Pointer); }
                if (hr < 0) throw new InvalidOperationException($"the encoder's ProcessInput: 0x{hr:x8}");
                Encoded++; LastEncoded = time;
                Collect();
            }
            finally { s.Release(); }
        }

        /// <summary>The encoder's samples, until it wants more.</summary>
        private void Collect()
        {
            while (true)
            {
                int hr = Mft.Output(Encoder, out var sample, out bool changed, null!);
                if (changed) continue;
                if (hr == MF.MF_E_TRANSFORM_NEED_MORE_INPUT) return;
                if (hr < 0) throw new InvalidOperationException($"the encoder's ProcessOutput: 0x{hr:x8}");
                if (sample == null) return;
                _out.Enqueue((sample.Bytes(), sample.Time ?? 0, sample.Duration ?? 0, sample.Attributes.U32(MF.MFSampleExtension_CleanPoint) != 0));
                sample.Release();
            }
        }

        public bool TryTake(out (byte[] Data, long Time, long Duration, bool Clean) s) => _out.TryDequeue(out s);

        /// <summary>The end: the converter's rest encoded, the encoder drained.</summary>
        public void Drain()
        {
            if (_audio != null) { _pending.AddRange(_audio.Drain()); Chunks(true); }
            // as Windows' writer ends it: drained, then flushed, the end of the stream and of streaming told
            Mft.ProcessMessage(Encoder, MFT_MESSAGE_COMMAND_DRAIN, 0);
            Collect();
            Mft.ProcessMessage(Encoder, 0, 0);                                   // MFT_MESSAGE_COMMAND_FLUSH
            Mft.ProcessMessage(Encoder, MFT_MESSAGE_NOTIFY_END_OF_STREAM, 0);
            Mft.ProcessMessage(Encoder, 0x10000001, 0);                          // MFT_MESSAGE_NOTIFY_END_STREAMING
        }

        /// <summary>NV12 to another size, each plane bilinearly.</summary>
        internal static byte[] Scale(byte[] src, int sw, int sh, int dw, int dh)
        {
            var d = new byte[dw * dh * 3 / 2];
            void Plane(int so, int spw, int sph, int sstride, int dof, int dpw, int dph, int dstride, int comps)
            {
                for (int y = 0; y < dph; y++)
                {
                    double fy = Math.Max(0, (y + 0.5) * sph / dph - 0.5);
                    int y0 = Math.Min((int)fy, sph - 1), y1 = Math.Min(y0 + 1, sph - 1);
                    double wy = fy - y0;
                    for (int x = 0; x < dpw; x++)
                    {
                        double fx = Math.Max(0, (x + 0.5) * spw / dpw - 0.5);
                        int x0 = Math.Min((int)fx, spw - 1), x1 = Math.Min(x0 + 1, spw - 1);
                        double wx = fx - x0;
                        for (int c = 0; c < comps; c++)
                        {
                            double v = (1 - wy) * ((1 - wx) * src[so + y0 * sstride + x0 * comps + c] + wx * src[so + y0 * sstride + x1 * comps + c])
                                     + wy * ((1 - wx) * src[so + y1 * sstride + x0 * comps + c] + wx * src[so + y1 * sstride + x1 * comps + c]);
                            d[dof + y * dstride + x * comps + c] = (byte)Math.Clamp((int)(v + 0.5), 0, 255);
                        }
                    }
                }
            }
            Plane(0, sw, sh, sw, 0, dw, dh, dw, 1);
            Plane(sw * sh, sw / 2, sh / 2, sw, dw * dh, dw / 2, dh / 2, dw, 2);
            return d;
        }

        /// <summary>NV12 as IYUV (Y, U, V) or YV12 (Y, V, U); NV12 as it is.</summary>
        internal static byte[] FromNv12(byte[] src, Guid sub, int w, int h)
        {
            if (sub == MF.MFVideoFormat_NV12) return src;
            var o = new byte[w * h * 3 / 2];
            Array.Copy(src, o, w * h);
            int cw = w / 2, ch = h / 2;
            bool yv12 = sub == MF.MFVideoFormat_YV12;
            int u = w * h + (yv12 ? cw * ch : 0), v = w * h + (yv12 ? 0 : cw * ch);
            for (int y = 0; y < ch; y++)
                for (int x = 0; x < cw; x++)
                {
                    o[u + y * cw + x] = src[w * h + y * w + 2 * x];
                    o[v + y * cw + x] = src[w * h + y * w + 2 * x + 1];
                }
            return o;
        }

        public void Dispose()
        {
            foreach (var (_, t) in Transforms) Com.Release(t);
            Transforms.Clear();
        }
    }
}
