// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The sink writer (Documentation/media-foundation-shim.md, "The sink writer"): IMFSinkWriterEx writing an MP4
// file as Windows' MPEG-4 sink lays it out (Mp4Muxer.cs). A stream's target type is H.264, HEVC or AAC (or the
// input as it is). Each goes through its encoder MFT (H.264's, HEVC's, AAC's), after a converter where the encoder
// takes not the input as it is (SinkChain.cs), as Windows' writer chains them; GetTransformForStream lists the chain
// and GetServiceForStream hands out the encoder. H.264's and HEVC's parameter sets go to avcC / hvcC, out of the
// samples, as Windows' sink takes them out.
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Wpf.Interop.WebGpu.Direct3D;
using Microsoft.Wpf.Interop.WebGpu.MediaFoundation.Codecs;
using Microsoft.Wpf.Interop.WebGpu.MediaFoundation.Source;

namespace Microsoft.Wpf.Interop.WebGpu.MediaFoundation.Sink
{
    internal sealed unsafe class MFSinkWriter : ComObject
    {
        public static readonly Guid IID_IMFSinkWriter = new("3137f1cd-fe5e-4805-a5d8-fb477448cb3d"), IID_IMFSinkWriterEx = new("588d72ab-5bc1-496a-8714-b70617141b25");
        public const int MF_E_SINK_NO_SAMPLES_PROCESSED = unchecked((int)0xC00D4A44), MF_E_UNSUPPORTED_SERVICE = unchecked((int)0xC00D36BA);
        private const uint MF_SINK_WRITER_ALL_STREAMS = 0xFFFFFFFE, MF_SINK_WRITER_MEDIASINK = 0xFFFFFFFF;
        private static readonly Vtable s_vt = Build();

        private sealed class Stream
        {
            public MFMediaType Target = null!;
            public MFMediaType? Input;
            public bool Video;
            public int Channels;
            public IntPtr StreamSink;                     // IMFStreamSink, AddRef'd: from BeginWriting (of a sink of the application's, from the start)
            public SinkChain? Chain;                      // the encoder MFT, a converter before it
            public long Received, LastReceived;
            public bool Passthrough;
            public long FrameDuration;
            public long AudioTime = -1, AudioFrames;
            public bool ConfigDone;
        }

        private System.IO.Stream? _file;                  // of a writer of a URL or byte stream: the file its MPEG-4 sink writes, made at BeginWriting
        private IntPtr _sink;                             // IMFMediaSink, AddRef'd
        private readonly bool _sinkGiven;                 // the application's (MFCreateSinkWriterFromMediaSink)
        private MFPresentationClock? _clock;
        private readonly List<Stream> _streams = new();
        private bool _begun, _finished;
        private int _failed;
        private readonly object _lock = new();

        private static Vtable Build()
        {
            var vt = new Vtable("IMFSinkWriterEx", 15, IID_IMFSinkWriterEx, IID_IMFSinkWriter);
            vt.Set(3, (delegate* unmanaged<IntPtr, IntPtr, uint*, int>)&AddStream);
            vt.Set(4, (delegate* unmanaged<IntPtr, uint, IntPtr, IntPtr, int>)&SetInputMediaType);
            vt.Set(5, (delegate* unmanaged<IntPtr, int>)&BeginWriting);
            vt.Set(6, (delegate* unmanaged<IntPtr, uint, IntPtr, int>)&WriteSample);
            vt.Set(7, (delegate* unmanaged<IntPtr, uint, long, int>)&SendStreamTick);
            vt.Set(8, (delegate* unmanaged<IntPtr, uint, IntPtr, int>)&PlaceMarker);
            vt.Set(9, (delegate* unmanaged<IntPtr, uint, int>)&NotifyEndOfSegment);
            vt.Set(10, (delegate* unmanaged<IntPtr, uint, int>)&Flush);
            vt.Set(11, (delegate* unmanaged<IntPtr, int>)&FinalizeWriting);
            vt.Set(12, (delegate* unmanaged<IntPtr, uint, Guid*, Guid*, IntPtr*, int>)&GetServiceForStream);
            vt.Set(13, (delegate* unmanaged<IntPtr, uint, void*, int>)&GetStatistics);
            vt.Set(14, (delegate* unmanaged<IntPtr, uint, uint, Guid*, IntPtr*, int>)&GetTransformForStream);
            return vt;
        }

        public MFSinkWriter(System.IO.Stream file) : base(s_vt) { _file = file; }

        /// <summary>A writer of a media sink of the application's: its streams the sink's stream sinks, of their current types.</summary>
        public MFSinkWriter(IntPtr sink) : base(s_vt)
        {
            Com.AddRef(sink); _sink = sink; _sinkGiven = true;
            uint n;
            if (((delegate* unmanaged<IntPtr, uint*, int>)Com.Fn(sink, 6))(sink, &n) < 0) n = 0;
            for (uint i = 0; i < n; i++)
            {
                IntPtr ss;
                if (((delegate* unmanaged<IntPtr, uint, IntPtr*, int>)Com.Fn(sink, 7))(sink, i, &ss) < 0) continue;
                var t = StreamType(ss);
                if (t == null) { Com.Release(ss); continue; }
                _streams.Add(new Stream { Target = t, StreamSink = ss, Video = t.Attributes.Guid(MF.MF_MT_MAJOR_TYPE) == MF.MFMediaType_Video, Channels = (int)t.Attributes.U32(SR.MF_MT_AUDIO_NUM_CHANNELS, 2) });
            }
        }

        /// <summary>A stream sink's type: its handler's current one, or its first.</summary>
        private static MFMediaType? StreamType(IntPtr streamSink)
        {
            IntPtr h;
            if (((delegate* unmanaged<IntPtr, IntPtr*, int>)Com.Fn(streamSink, 9))(streamSink, &h) < 0) return null;
            try
            {
                IntPtr t;
                if (((delegate* unmanaged<IntPtr, IntPtr*, int>)Com.Fn(h, 7))(h, &t) < 0 && ((delegate* unmanaged<IntPtr, uint, IntPtr*, int>)Com.Fn(h, 5))(h, 0, &t) < 0) return null;
                var m = Mp4Types.Read(t);
                Com.Release(t);
                return m;
            }
            finally { Com.Release(h); }
        }

        protected override void OnFinalRelease()
        {
            foreach (var s in _streams) { s.Chain?.Dispose(); s.Target.Release(); s.Input?.Release(); Com.Release(s.StreamSink); }
            if (_sink != IntPtr.Zero) { ((delegate* unmanaged<IntPtr, int>)Com.Fn(_sink, 11))(_sink); Com.Release(_sink); _sink = IntPtr.Zero; }
            _clock?.Release();
            if (_file != null && !_begun) _file.Dispose();
        }

        private static MFSinkWriter? W(IntPtr self) => From<MFSinkWriter>(self);

        // ---- streams ------------------------------------------------------------------------------------

        [UnmanagedCallersOnly]
        private static int AddStream(IntPtr self, IntPtr type, uint* index)
        {
            if (W(self) is not { } w || type == IntPtr.Zero || index == null) return MF.E_POINTER;
            lock (w._lock)
            {
                if (w._begun) return SR.MF_E_INVALIDREQUEST;
                var t = Mp4Types.Read(type);
                var major = t.Attributes.Guid(MF.MF_MT_MAJOR_TYPE);
                var sub = t.Subtype;
                bool video = major == MF.MFMediaType_Video, audio = major == SR.MFMediaType_Audio;
                if (!(video && (sub == MF.MFVideoFormat_H264 || sub == MF.MFVideoFormat_HEVC) || audio && sub == SR.MFAudioFormat_AAC))
                {
                    t.Release();
                    D3DLog.Once("mf-sink-type:" + sub, $"Media Foundation: the sink writer writes H.264, HEVC and AAC to MP4, not {sub}");
                    return MF.MF_E_INVALIDMEDIATYPE;
                }
                if (MFMpeg4Sink.TrackOf(t) == null) { t.Release(); return MF.MF_E_INVALIDMEDIATYPE; }
                var stream = new Stream { Target = t, Video = video, Channels = (int)t.Attributes.U32(SR.MF_MT_AUDIO_NUM_CHANNELS, 2) };
                // of a sink of the application's: a stream sink of it, of the next identifier
                if (w._sinkGiven)
                {
                    IntPtr ss;
                    int ahr = ((delegate* unmanaged<IntPtr, uint, IntPtr, IntPtr*, int>)Com.Fn(w._sink, 4))(w._sink, (uint)w._streams.Count + 1, t.Pointer, &ss);
                    if (ahr < 0) { t.Release(); return ahr; }
                    stream.StreamSink = ss;
                }
                w._streams.Add(stream);
                *index = (uint)(w._streams.Count - 1);
            }
            return MF.S_OK;
        }

        private static readonly Guid[] s_videoInputs = { MF.MFVideoFormat_NV12, MF.MFVideoFormat_I420, MF.MFVideoFormat_IYUV, MF.MFVideoFormat_YV12, MF.MFVideoFormat_YUY2, SR.MFVideoFormat_RGB32, SR.MFVideoFormat_ARGB32 };

        [UnmanagedCallersOnly]
        private static int SetInputMediaType(IntPtr self, uint i, IntPtr type, IntPtr encodingParameters)
        {
            if (W(self) is not { } w || type == IntPtr.Zero) return MF.E_POINTER;
            lock (w._lock)
            {
                if (i >= w._streams.Count) return MF.MF_E_INVALIDSTREAMNUMBER;
                var s = w._streams[(int)i];
                var t = Mp4Types.Read(type);
                if (t.Attributes.Guid(MF.MF_MT_MAJOR_TYPE) != s.Target.Attributes.Guid(MF.MF_MT_MAJOR_TYPE)) { t.Release(); return MF.MF_E_INVALIDMEDIATYPE; }
                // the target itself: written as it comes
                if (t.Subtype == s.Target.Subtype) { s.Chain?.Dispose(); s.Chain = null; s.Input?.Release(); s.Input = t; s.Passthrough = true; return MF.S_OK; }
                bool hevc = s.Target.Subtype == MF.MFVideoFormat_HEVC;
                if (s.Video)
                {
                    if (Array.IndexOf(s_videoInputs, t.Subtype) < 0 || hevc && t.FrameSize != s.Target.FrameSize) { t.Release(); return MF.MF_E_INVALIDMEDIATYPE; }
                }
                else
                {
                    bool pcm = t.Subtype == MediaFormats.PCM || t.Subtype == MediaFormats.Float;
                    if (!pcm || t.Attributes.U32(SR.MF_MT_AUDIO_NUM_CHANNELS) != s.Channels) { t.Release(); return MF.MF_E_INVALIDMEDIATYPE; }
                }
                int hr = SinkChain.Create(s.Target, t, encodingParameters, out var chain);
                if (hr < 0) { t.Release(); return hr; }
                // a stream sink of the application's sink: of the encoder's output type, as Windows' writer sets it
                if (w._sinkGiven && Mft.OutputType(chain!.Encoder) is { } eo)
                {
                    IntPtr h;
                    if (((delegate* unmanaged<IntPtr, IntPtr*, int>)Com.Fn(s.StreamSink, 9))(s.StreamSink, &h) >= 0)
                    {
                        ((delegate* unmanaged<IntPtr, IntPtr, int>)Com.Fn(h, 6))(h, eo.Pointer);
                        Com.Release(h);
                    }
                    eo.Release();
                }
                s.Chain?.Dispose(); s.Chain = chain;
                s.Input?.Release(); s.Input = t; s.Passthrough = false;
            }
            return MF.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int BeginWriting(IntPtr self)
        {
            if (W(self) is not { } w) return MF.E_POINTER;
            lock (w._lock)
            {
                if (w._begun) return SR.MF_E_INVALIDREQUEST;
                if (w._streams.Count == 0) return SR.MF_E_INVALIDREQUEST;              // as Windows', of no streams
                try
                {
                    // a byte stream of the application's: one Windows' sink can write and seek, or E_INVALIDARG (and the writer done)
                    if (w._file is ByteStreamWriter bs && !bs.Writable) { w._finished = true; return MF.E_INVALIDARG; }
                    foreach (var s in w._streams)
                    {
                        if (s.Input == null) { s.Input = s.Target.Clone(); s.Passthrough = true; }
                        if (s.Video) { var (rn0, rd0) = MF.Unpack(s.Target.Attributes.U64(MF.MF_MT_FRAME_RATE, MF.Pack(30, 1))); s.FrameDuration = rn0 == 0 ? 0 : 10_000_000L * rd0 / rn0; }
                        s.Chain?.Begin();
                    }
                    // the MPEG-4 sink of the file: a stream sink each, of the encoder's output type (or the target's), identifiers from 1
                    if (!w._sinkGiven)
                    {
                        var sink = MFMpeg4Sink.Create(w._file!, null, null);
                        for (int i = 0; i < w._streams.Count; i++)
                        {
                            var s = w._streams[i];
                            var type = s.Chain != null ? Mft.OutputType(s.Chain.Encoder) ?? s.Target.Clone() : s.Target.Clone();
                            sink.Add((uint)i + 1, type, out var ss);
                            type.Release();
                            s.StreamSink = ss!.AddRefPointer();
                        }
                        w._sink = sink.Pointer;
                    }
                    // its clock, started
                    w._clock = new MFPresentationClock();
                    ((delegate* unmanaged<IntPtr, IntPtr, int>)Com.Fn(w._sink, 9))(w._sink, w._clock.Pointer);
                    w._clock.Start(0);
                    w._begun = true;
                }
                catch (Exception e) { D3DLog.Error("IMFSinkWriter.BeginWriting", e); return MF.E_FAIL; }
            }
            return MF.S_OK;
        }

        // ---- samples ------------------------------------------------------------------------------------

        [UnmanagedCallersOnly]
        private static int WriteSample(IntPtr self, uint i, IntPtr sample)
        {
            if (W(self) is not { } w || sample == IntPtr.Zero) return MF.E_POINTER;
            try
            {
                lock (w._lock)
                {
                    if (!w._begun || w._finished) return SR.MF_E_INVALIDREQUEST;
                    if (i >= w._streams.Count) return MF.MF_E_INVALIDSTREAMNUMBER;
                    var s = w._streams[(int)i];
                    var (bytes, time, duration, clean) = Read(sample);
                    s.Received++; s.LastReceived = time;
                    if (s.Chain != null) { s.Chain.Push(bytes, time, duration > 0 ? duration : s.FrameDuration); return w.TakeChain(s); }
                    return Process(s.StreamSink, sample);
                }
                return MF.S_OK;
            }
            catch (Exception e) { D3DLog.Error("IMFSinkWriter.WriteSample", e); return MF.E_FAIL; }
        }

        private static (byte[] Bytes, long Time, long Duration, bool Clean) Read(IntPtr sample)
        {
            if (From<MFSample>(sample) is { } s) return (s.Bytes(), s.Time ?? 0, s.Duration ?? 0, s.Attributes.U32(MF.MFSampleExtension_CleanPoint) != 0);
            var b = MFSample.ForeignSampleBytes(sample);
            long t = 0, d = 0;
            ((delegate* unmanaged<IntPtr, long*, int>)Com.Fn(sample, 35))(sample, &t);
            ((delegate* unmanaged<IntPtr, long*, int>)Com.Fn(sample, 37))(sample, &d);
            uint clean = 0; var key = MF.MFSampleExtension_CleanPoint;
            ((delegate* unmanaged<IntPtr, Guid*, uint*, int>)Com.Fn(sample, 7))(sample, &key, &clean);
            return (b, t, d, clean != 0);
        }

        /// <summary>IMFStreamSink::ProcessSample.</summary>
        private static int Process(IntPtr streamSink, IntPtr sample) => ((delegate* unmanaged<IntPtr, IntPtr, int>)Com.Fn(streamSink, 10))(streamSink, sample);

        /// <summary>The encoder MFT's samples to the stream sink: of their times, durations and clean points.</summary>
        private int TakeChain(Stream s)
        {
            while (s.Chain!.TryTake(out var o))
            {
                var sample = new MFSample();
                var buf = new MFMemoryBuffer((uint)Math.Max(1, o.Data.Length));
                o.Data.CopyTo(new Span<byte>(buf.LockBytes(true), o.Data.Length));
                buf.CurrentLength = (uint)o.Data.Length;
                sample.SetBuffer(buf.Pointer);
                buf.Release();
                sample.Time = o.Time;
                sample.Duration = o.Duration > 0 ? o.Duration : s.FrameDuration;
                sample.Attributes.Set(MF.MFSampleExtension_CleanPoint, MFValue.Of(o.Clean || !s.Video ? 1u : 0u));
                int hr = Process(s.StreamSink, sample.Pointer);
                sample.Release();
                if (hr < 0) return hr;
            }
            return MF.S_OK;
        }

        // ---- conversions ----------------------------------------------------------------------------------

        /// <summary>A frame of the input's subtype as NV12.</summary>
        internal static byte[] ToNv12(byte[] src, Guid sub, int w, int h)
        {
            if (sub == MF.MFVideoFormat_NV12) return src;
            var o = new byte[w * h * 3 / 2];
            int cw = w / 2, ch = h / 2;
            if (sub == MF.MFVideoFormat_I420 || sub == MF.MFVideoFormat_IYUV || sub == MF.MFVideoFormat_YV12)
            {
                Array.Copy(src, o, w * h);
                int u = w * h + (sub == MF.MFVideoFormat_YV12 ? cw * ch : 0), v = w * h + (sub == MF.MFVideoFormat_YV12 ? 0 : cw * ch);
                for (int y = 0; y < ch; y++) for (int x = 0; x < cw; x++) { o[w * h + y * w + 2 * x] = src[u + y * cw + x]; o[w * h + y * w + 2 * x + 1] = src[v + y * cw + x]; }
                return o;
            }
            if (sub == MF.MFVideoFormat_YUY2)
            {
                for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) o[y * w + x] = src[(y * w + x) * 2];
                for (int y = 0; y < ch; y++) for (int x = 0; x < cw; x++) { int a = (2 * y * w + 2 * x) * 2; o[w * h + y * w + 2 * x] = src[a + 1]; o[w * h + y * w + 2 * x + 1] = src[a + 3]; }
                return o;
            }
            // RGB32 / ARGB32, bottom-up as MF's default stride is positive... top-down here: BT.601 limited
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int p = (y * w + x) * 4; int b = src[p], g = src[p + 1], r = src[p + 2];
                    o[y * w + x] = (byte)Math.Clamp(((66 * r + 129 * g + 25 * b + 128) >> 8) + 16, 0, 255);
                    if ((x & 1) == 0 && (y & 1) == 0)
                    {
                        o[w * h + (y / 2) * w + x] = (byte)Math.Clamp(((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128, 0, 255);
                        o[w * h + (y / 2) * w + x + 1] = (byte)Math.Clamp(((112 * r - 94 * g - 18 * b + 128) >> 8) + 128, 0, 255);
                    }
                }
            return o;
        }

        // ---- the rest -------------------------------------------------------------------------------------

        [UnmanagedCallersOnly] private static int SendStreamTick(IntPtr self, uint i, long time) => W(self) is { } w && i < w._streams.Count ? MF.S_OK : MF.MF_E_INVALIDSTREAMNUMBER;
        [UnmanagedCallersOnly] private static int PlaceMarker(IntPtr self, uint i, IntPtr context) => MF.S_OK;
        [UnmanagedCallersOnly] private static int NotifyEndOfSegment(IntPtr self, uint i) => MF.S_OK;
        [UnmanagedCallersOnly] private static int Flush(IntPtr self, uint i) => MF.S_OK;

        [UnmanagedCallersOnly]
        private static int FinalizeWriting(IntPtr self)
        {
            if (W(self) is not { } w) return MF.E_POINTER;
            try
            {
                lock (w._lock)
                {
                    if (w._failed < 0) return w._failed;
                    if (!w._begun || w._finished) return SR.MF_E_INVALIDREQUEST;
                    // nothing written: Windows' sink has no file to finish, and the writer is done with that error
                    if (w._streams.All(s => s.Received == 0))
                    {
                        w._failed = MF_E_SINK_NO_SAMPLES_PROCESSED; w._finished = true;
                        if (!w._sinkGiven) ((delegate* unmanaged<IntPtr, int>)Com.Fn(w._sink, 11))(w._sink);
                        return w._failed;
                    }
                    foreach (var s in w._streams)
                        if (s.Chain != null) { s.Chain.Drain(); int th = w.TakeChain(s); if (th < 0) return th; }
                    int hr = FinalizeSink(w._sink);
                    w._clock?.Stop();
                    w._finished = true;
                    if (hr < 0) return hr;
                }
                return MF.S_OK;
            }
            catch (Exception e) { D3DLog.Error("IMFSinkWriter.Finalize", e); return MF.E_FAIL; }
        }

        /// <summary>
        /// The sink finalized: IMFFinalizableMediaSink's BeginFinalize, waited for, and EndFinalize (a sink not finalizable,
        /// told nothing more).
        /// </summary>
        private static int FinalizeSink(IntPtr sink)
        {
            var iid = MFMpeg4Sink.IID_IMFFinalizableMediaSink; IntPtr f;
            if (((delegate* unmanaged<IntPtr, Guid*, IntPtr*, int>)Com.Fn(sink, 0))(sink, &iid, &f) < 0) return MF.S_OK;
            try
            {
                var done = new MFWaitCallback();
                try
                {
                    int hr = ((delegate* unmanaged<IntPtr, IntPtr, IntPtr, int>)Com.Fn(f, 12))(f, done.Pointer, IntPtr.Zero);
                    if (hr < 0) return hr;
                    if (!done.Done.Wait(TimeSpan.FromSeconds(60))) return MF.E_FAIL;
                    return ((delegate* unmanaged<IntPtr, IntPtr, int>)Com.Fn(f, 13))(f, done.Result);
                }
                finally { done.Release(); }
            }
            finally { Com.Release(f); }
        }

        /// <summary>The stream's encoder (of GUID_NULL, the service of the transform itself), as its interface asked for; of a stream written as it comes, none.</summary>
        [UnmanagedCallersOnly]
        private static int GetServiceForStream(IntPtr self, uint i, Guid* service, Guid* riid, IntPtr* obj)
        {
            if (W(self) is not { } w || riid == null || obj == null) return MF.E_POINTER;
            *obj = IntPtr.Zero;
            lock (w._lock)
            {
                // the media sink: of a writer of a URL or byte stream once writing has begun (Windows' makes it then), of the application's always
                if (i == MF_SINK_WRITER_MEDIASINK)
                {
                    if (w._sink == IntPtr.Zero) return MF_E_UNSUPPORTED_SERVICE;
                    IntPtr q;
                    int qhr = ((delegate* unmanaged<IntPtr, Guid*, IntPtr*, int>)Com.Fn(w._sink, 0))(w._sink, riid, &q);
                    if (qhr >= 0) *obj = q;
                    return qhr;
                }
                if (i >= w._streams.Count) return MF.MF_E_INVALIDSTREAMNUMBER;
                var c = w._streams[(int)i].Chain;
                if (c == null || service != null && *service != Guid.Empty) return MF_E_UNSUPPORTED_SERVICE;
                IntPtr p;
                int hr = ((delegate* unmanaged<IntPtr, Guid*, IntPtr*, int>)Com.Fn(c.Encoder, 0))(c.Encoder, riid, &p);
                if (hr >= 0) *obj = p;
                return hr;
            }
        }

        /// <summary>
        /// MF_SINK_WRITER_STATISTICS (cb, five times, four counts, the bytes queued and processed, four more): of a stream or of
        /// all of them; the samples received, the samples the encoder was given (encoded and processed), their last times.
        /// </summary>
        [UnmanagedCallersOnly]
        private static int GetStatistics(IntPtr self, uint i, void* stats)
        {
            if (W(self) is not { } w || stats == null) return MF.E_POINTER;
            byte* st = (byte*)stats;
            if (*(uint*)st < 112) return MF.E_INVALIDARG;
            lock (w._lock)
            {
                if (w._failed < 0) return w._failed;
                if (!w._begun) return SR.MF_E_INVALIDREQUEST;
                IEnumerable<Stream> of;
                if (i == MF_SINK_WRITER_ALL_STREAMS) of = w._streams;
                else if (i < w._streams.Count) of = new[] { w._streams[(int)i] };
                else return MF.MF_E_INVALIDSTREAMNUMBER;
                new Span<byte>(st + 4, 108).Clear();
                foreach (var s in of)
                {
                    long encoded = s.Chain?.Encoded ?? s.Received, lastEncoded = s.Chain?.LastEncoded ?? s.LastReceived;
                    *(long*)(st + 8) = Math.Max(*(long*)(st + 8), s.LastReceived);
                    *(long*)(st + 16) = Math.Max(*(long*)(st + 16), lastEncoded);
                    *(long*)(st + 24) = Math.Max(*(long*)(st + 24), lastEncoded);
                    *(long*)(st + 48) += s.Received;
                    *(long*)(st + 56) += encoded;
                    *(long*)(st + 64) += encoded;
                }
            }
            return MF.S_OK;
        }

        /// <summary>The stream's transforms in order (the converter, the encoder), each with its category; of a stream written as it comes, none.</summary>
        [UnmanagedCallersOnly]
        private static int GetTransformForStream(IntPtr self, uint i, uint n, Guid* category, IntPtr* t)
        {
            if (W(self) is not { } w || t == null) return MF.E_POINTER;
            *t = IntPtr.Zero;
            lock (w._lock)
            {
                if (i >= w._streams.Count) return MF.MF_E_INVALIDSTREAMNUMBER;
                var c = w._streams[(int)i].Chain;
                if (c == null || n >= c.Transforms.Count) return MF.MF_E_INVALIDINDEX;
                var (cat, x) = c.Transforms[(int)n];
                if (category != null) *category = cat;
                Com.AddRef(x);
                *t = x;
                return MF.S_OK;
            }
        }
    }

    /// <summary>An IMFAsyncCallback that keeps its result and sets an event (the sink writer waits on it for its sink's finalizing).</summary>
    /// <remarks>Of the namespace, not nested: the browser's entry points of [UnmanagedCallersOnly] methods are found by namespace and name.</remarks>
    internal sealed unsafe class MFWaitCallback : ComObject
    {
        private static readonly Vtable s_cb = BuildCallback();
        public readonly System.Threading.ManualResetEventSlim Done = new();
        public IntPtr Result;
        public MFWaitCallback() : base(s_cb) { }
        protected override void OnFinalRelease() { Com.Release(Result); Done.Dispose(); }

        private static Vtable BuildCallback()
        {
            var vt = new Vtable("IMFAsyncCallback", 5, new Guid("a27003cf-2354-4f2a-8d6a-ab7cff15437e"));
            vt.Set(3, (delegate* unmanaged<IntPtr, uint*, uint*, int>)&GetParameters);
            vt.Set(4, (delegate* unmanaged<IntPtr, IntPtr, int>)&Invoke);
            return vt;
        }

        [UnmanagedCallersOnly] private static int GetParameters(IntPtr self, uint* flags, uint* queue) => MF.E_NOTIMPL;

        [UnmanagedCallersOnly]
        private static int Invoke(IntPtr self, IntPtr result)
        {
            if (From<MFWaitCallback>(self) is not { } c) return MF.E_POINTER;
            Com.AddRef(result); c.Result = result;
            c.Done.Set();
            return MF.S_OK;
        }
    }
}
