// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The sink writer (Documentation/media-foundation-shim.md, "The sink writer"): IMFSinkWriterEx writing an MP4
// file as Windows' MPEG-4 sink lays it out (Mp4Muxer.cs). A stream's target type is H.264, HEVC or AAC (or the
// input as it is); its input type what the system's encoder takes, converted where it is another (video: I420,
// IYUV, YV12, YUY2, RGB32, ARGB32 to NV12; audio: float and other bits to 16-bit PCM). The encoders are the
// system's (Codecs/PlatformEncoders.cs). H.264's and HEVC's parameter sets go to avcC / hvcC, out of the samples,
// as Windows' sink takes them out.
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
        public const int MF_E_SINK_NO_STREAMS = unchecked((int)0xC00D4A44);
        private static readonly Vtable s_vt = Build();

        private sealed class Stream
        {
            public MFMediaType Target = null!;
            public MFMediaType? Input;
            public MuxTrack Track = null!;
            public PlatformVideoEncoder? Video;
            public PlatformAudioEncoder? Audio;
            public bool Passthrough;
            public long FrameDuration;
            public byte[] Scratch = Array.Empty<byte>();
            public readonly Queue<(long Time, long Duration)> Inputs = new();
            public long AudioTime = -1, AudioFrames;
            public bool ConfigDone;
        }

        private readonly FileStream _file;
        private Mp4Muxer? _mux;
        private readonly List<Stream> _streams = new();
        private bool _begun, _finished;
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

        public MFSinkWriter(FileStream file) : base(s_vt) { _file = file; }

        protected override void OnFinalRelease()
        {
            foreach (var s in _streams) { s.Video?.Dispose(); s.Audio?.Dispose(); s.Target.Release(); s.Input?.Release(); }
            if (!_finished) _file.Dispose();
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
                var track = new MuxTrack { Video = video };
                if (video)
                {
                    var (fw, fh) = t.FrameSize;
                    var (rn, rd) = MF.Unpack(t.Attributes.U64(MF.MF_MT_FRAME_RATE, MF.Pack(30, 1)));
                    if (fw == 0 || fh == 0 || rn == 0 || rd == 0) { t.Release(); return MF.MF_E_INVALIDMEDIATYPE; }
                    track.Format = sub == MF.MFVideoFormat_HEVC ? "hvc1" : "avc1";
                    track.Width = (int)fw; track.Height = (int)fh;
                    // the frame rate times 1000 (25 fps: 25000), where that is whole; else the rate's numerator
                    track.Timescale = rn * 1000UL % rd == 0 ? (uint)(rn * 1000UL / rd) : rn;
                    var (pw, ph) = MF.Unpack(t.Attributes.U64(MF.MF_MT_PIXEL_ASPECT_RATIO, MF.Pack(1, 1)));
                    track.ParW = pw; track.ParH = ph;
                }
                else
                {
                    track.Format = "mp4a";
                    track.Channels = (int)t.Attributes.U32(SR.MF_MT_AUDIO_NUM_CHANNELS, 2);
                    track.SampleRate = (int)t.Attributes.U32(SR.MF_MT_AUDIO_SAMPLES_PER_SECOND, 44100);
                    track.Timescale = (uint)track.SampleRate;
                    track.SampleBits = 16;
                    uint bytes = t.Attributes.U32(SR.MF_MT_AUDIO_AVG_BYTES_PER_SECOND, 16000);
                    track.Bitrate = track.MaxBitrate = bytes * 8;
                    track.BufferSize = 6144;                                   // Windows' encoder's (an AAC frame's largest)
                }
                w._streams.Add(new Stream { Target = t, Track = track });
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
                if (t.Subtype == s.Target.Subtype) { s.Input?.Release(); s.Input = t; s.Passthrough = true; return MF.S_OK; }
                if (s.Track.Video)
                {
                    if (Array.IndexOf(s_videoInputs, t.Subtype) < 0 || t.FrameSize != s.Target.FrameSize) { t.Release(); return MF.MF_E_INVALIDMEDIATYPE; }
                }
                else
                {
                    bool pcm = t.Subtype == MediaFormats.PCM || t.Subtype == MediaFormats.Float;
                    if (!pcm || t.Attributes.U32(SR.MF_MT_AUDIO_SAMPLES_PER_SECOND) != s.Track.SampleRate || t.Attributes.U32(SR.MF_MT_AUDIO_NUM_CHANNELS) != s.Track.Channels) { t.Release(); return MF.MF_E_INVALIDMEDIATYPE; }
                }
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
                if (w._streams.Count == 0) return MF_E_SINK_NO_STREAMS;
                try
                {
                    foreach (var s in w._streams)
                    {
                        if (s.Input == null) { s.Input = s.Target.Clone(); s.Passthrough = true; }
                        if (s.Passthrough) continue;
                        if (s.Track.Video)
                        {
                            var (rn, rd) = MF.Unpack(s.Target.Attributes.U64(MF.MF_MT_FRAME_RATE, MF.Pack(30, 1)));
                            s.FrameDuration = 10_000_000L * rd / rn;
                            var codec = s.Target.Subtype == MF.MFVideoFormat_HEVC ? VideoCodec.H265 : VideoCodec.H264;
                            s.Video = PlatformVideoEncoder.Create(codec, new VideoEncoderOptions
                            {
                                Width = (uint)s.Track.Width, Height = (uint)s.Track.Height, FpsNom = rn, FpsDenom = rd,
                                Bitrate = s.Target.Attributes.U32(SR.MF_MT_AVG_BITRATE, 2_000_000),
                                // Windows' H.264 encoder's profile where the type names none: Baseline
                                H264Profile = s.Target.Attributes.U32(SR.MF_MT_MPEG2_PROFILE, 66),
                            });
                            if (s.Video == null) return SR.MF_E_TOPO_CODEC_NOT_FOUND;
                        }
                        else
                        {
                            s.Audio = PlatformAudioEncoder.Create(AudioCodec.AAC, new AudioEncoderOptions
                            {
                                Channels = (uint)s.Track.Channels, SampleRate = (uint)s.Track.SampleRate, BitsPerSample = 16, Bitrate = s.Track.Bitrate,
                            });
                            if (s.Audio == null) return SR.MF_E_TOPO_CODEC_NOT_FOUND;
                        }
                    }
                    w._mux = new Mp4Muxer(w._file);
                    foreach (var s in w._streams) w._mux.Add(s.Track);
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
                    if (s.Passthrough) { w.Mux(s, bytes, time, duration > 0 ? duration : s.FrameDuration, clean || !s.Track.Video); return MF.S_OK; }
                    if (s.Video != null)
                    {
                        var nv12 = ToNv12(bytes, s.Input!.Subtype, s.Track.Width, s.Track.Height);
                        s.Inputs.Enqueue((time, duration > 0 ? duration : s.FrameDuration));
                        s.Video.Push(nv12, time);
                        w.TakeVideo(s);
                    }
                    else
                    {
                        if (s.AudioTime < 0) s.AudioTime = time;
                        s.Audio!.Push(ToPcm16(bytes, s.Input!), time);
                        w.TakeAudio(s);
                    }
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

        private void TakeVideo(Stream s)
        {
            while (s.Video!.TryTake(ref s.Scratch, out uint n, out long time))
            {
                var au = s.Scratch.AsSpan(0, (int)n).ToArray();
                var (t, d) = s.Inputs.Count > 0 ? s.Inputs.Dequeue() : (time, s.FrameDuration);
                Mux(s, au, t, d, IsKey(au, s.Track.Format == "hvc1"));
            }
        }

        private void TakeAudio(Stream s)
        {
            while (s.Audio!.TryTake(ref s.Scratch, out uint n))
            {
                if (!s.ConfigDone && s.Audio.Config is { Length: > 0 } asc) { s.Track.Config = asc; s.ConfigDone = true; }
                // AAC's packets: 1024 frames each, counted from the first input's time
                long t = s.AudioTime + s.AudioFrames * 10_000_000 / s.Track.SampleRate;
                long t1 = s.AudioTime + (s.AudioFrames + 1024) * 10_000_000 / s.Track.SampleRate;
                s.AudioFrames += 1024;
                _mux!.Write(s.Track, s.Scratch.AsSpan(0, (int)n), t, t1 - t, true);
            }
        }

        /// <summary>A sample into its track: H.264 and HEVC from Annex B to length-prefixed NAL units, the parameter sets to the configuration.</summary>
        private void Mux(Stream s, byte[] data, long time, long duration, bool sync)
        {
            if (!s.Track.Video) { if (s.Track.Config.Length == 0 && s.Target.Attributes.Get(MF.MF_MT_USER_DATA)?.B is { Length: > 12 } ud) s.Track.Config = ud.AsSpan(12).ToArray(); _mux!.Write(s.Track, data, time, duration, true); return; }
            bool hevc = s.Track.Format == "hvc1";
            var o = new MemoryStream(data.Length);
            var sets = new List<byte[]>();
            foreach (var nal in Nals(data))
            {
                int type = hevc ? (nal[0] >> 1) & 0x3F : nal[0] & 0x1F;
                bool parameterSet = hevc ? type is 32 or 33 or 34 : type is 7 or 8;
                if (parameterSet) { sets.Add(nal); continue; }
                o.Write(new[] { (byte)(nal.Length >> 24), (byte)(nal.Length >> 16), (byte)(nal.Length >> 8), (byte)nal.Length });
                o.Write(nal);
            }
            if (s.Track.Config.Length == 0 && sets.Count > 0) s.Track.Config = hevc ? HvcC(sets) : AvcC(sets);
            _mux!.Write(s.Track, o.ToArray(), time, duration, sync);
        }

        private static IEnumerable<byte[]> Nals(byte[] b)
        {
            int i = 0, start = -1;
            while (i + 3 <= b.Length)
            {
                if (b[i] == 0 && b[i + 1] == 0 && (b[i + 2] == 1 || i + 3 < b.Length && b[i + 2] == 0 && b[i + 3] == 1))
                {
                    int sc = b[i + 2] == 1 ? 3 : 4;
                    if (start >= 0) yield return Trim(b, start, i);
                    i += sc; start = i;
                    continue;
                }
                i++;
            }
            if (start >= 0 && start < b.Length) yield return Trim(b, start, b.Length);
        }

        private static byte[] Trim(byte[] b, int from, int to) { while (to > from && b[to - 1] == 0) to--; return b.AsSpan(from, to - from).ToArray(); }

        private static bool IsKey(byte[] au, bool hevc)
        {
            foreach (var nal in Nals(au))
            {
                int type = hevc ? (nal[0] >> 1) & 0x3F : nal[0] & 0x1F;
                if (hevc ? type is >= 16 and <= 23 : type == 5) return true;
            }
            return false;
        }

        private static byte[] AvcC(List<byte[]> sets)
        {
            var sps = sets.Where(n => (n[0] & 0x1F) == 7).ToList();
            var pps = sets.Where(n => (n[0] & 0x1F) == 8).ToList();
            if (sps.Count == 0) return Array.Empty<byte>();
            var o = new List<byte> { 1, sps[0][1], sps[0][2], sps[0][3], 0xFF, (byte)(0xE0 | sps.Count) };
            foreach (var p in sps) { o.Add((byte)(p.Length >> 8)); o.Add((byte)p.Length); o.AddRange(p); }
            o.Add((byte)pps.Count);
            foreach (var p in pps) { o.Add((byte)(p.Length >> 8)); o.Add((byte)p.Length); o.AddRange(p); }
            return o.ToArray();
        }

        /// <summary>hvcC of the parameter sets: the SPS's profile, tier and level; 4:2:0, 8 bits; lengths of 4 bytes; VPS, SPS, PPS arrays.</summary>
        private static byte[] HvcC(List<byte[]> sets)
        {
            var sps = sets.FirstOrDefault(n => ((n[0] >> 1) & 0x3F) == 33);
            var o = new List<byte> { 1 };
            // profile_tier_level of the SPS: after its 2-byte header and 1 byte (vps id, sub-layers, nesting), 12 bytes
            var ptl = new byte[12];
            if (sps != null && sps.Length >= 15)
            {
                var rbsp = Unescape(sps.AsSpan(2).ToArray());
                if (rbsp.Length >= 13) Array.Copy(rbsp, 1, ptl, 0, 12);
            }
            o.AddRange(ptl);                                            // profile space/tier/idc, compat flags, constraints, level
            o.AddRange(new byte[] { 0xF0, 0x00, 0xFC, 0xFD, 0xF8, 0xF8, 0x00, 0x00, 0x0F });
            var groups = new[] { 32, 33, 34 }.Select(type => (type, sets.Where(n => ((n[0] >> 1) & 0x3F) == type).ToList())).Where(g => g.Item2.Count > 0).ToList();
            o.Add((byte)groups.Count);
            foreach (var (type, nals) in groups)
            {
                o.Add((byte)(0x80 | type));
                o.Add((byte)(nals.Count >> 8)); o.Add((byte)nals.Count);
                foreach (var n in nals) { o.Add((byte)(n.Length >> 8)); o.Add((byte)n.Length); o.AddRange(n); }
            }
            return o.ToArray();
        }

        private static byte[] Unescape(byte[] b)
        {
            var o = new List<byte>(b.Length);
            int zeros = 0;
            foreach (var x in b)
            {
                if (zeros >= 2 && x == 3) { zeros = 0; continue; }
                o.Add(x);
                zeros = x == 0 ? zeros + 1 : 0;
            }
            return o.ToArray();
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

        private static byte[] ToPcm16(byte[] src, MFMediaType input)
        {
            bool fl = input.Subtype == MediaFormats.Float;
            uint bits = input.Attributes.U32(SR.MF_MT_AUDIO_BITS_PER_SAMPLE, fl ? 32u : 16u);
            if (!fl && bits == 16) return src;
            uint ch = input.Attributes.U32(SR.MF_MT_AUDIO_NUM_CHANNELS, 2), rate = input.Attributes.U32(SR.MF_MT_AUDIO_SAMPLES_PER_SECOND, 44100);
            return new AudioConverter(fl, (int)bits, (int)ch, (int)rate, false, 16, (int)ch, (int)rate).Convert(src);
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
                    if (!w._begun || w._finished) return SR.MF_E_INVALIDREQUEST;
                    foreach (var s in w._streams)
                    {
                        if (s.Video != null) { s.Video.BeginDrain(); w.TakeVideo(s); s.Video.EndDrain(); }
                        if (s.Audio != null) { s.Audio.BeginDrain(); w.TakeAudio(s); s.Audio.EndDrain(); }
                    }
                    w._mux!.Finish();
                    w._finished = true;
                    w._file.Dispose();
                }
                return MF.S_OK;
            }
            catch (Exception e) { D3DLog.Error("IMFSinkWriter.Finalize", e); return MF.E_FAIL; }
        }

        [UnmanagedCallersOnly] private static int GetServiceForStream(IntPtr self, uint i, Guid* service, Guid* riid, IntPtr* obj) { if (obj != null) *obj = IntPtr.Zero; return MF.E_NOINTERFACE; }
        [UnmanagedCallersOnly] private static int GetStatistics(IntPtr self, uint i, void* stats) => MF.E_NOTIMPL;
        [UnmanagedCallersOnly] private static int GetTransformForStream(IntPtr self, uint i, uint n, Guid* category, IntPtr* t) { if (t != null) *t = IntPtr.Zero; return MF.MF_E_INVALIDINDEX; }
    }
}
