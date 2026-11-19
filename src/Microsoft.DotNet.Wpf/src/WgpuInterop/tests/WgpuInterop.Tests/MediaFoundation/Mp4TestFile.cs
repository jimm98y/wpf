// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// MP4 files for the source reader's tests, written here: an H.264 track of I_PCM pictures (known to the
// byte, MFDecoderTests' stream) and an AAC track of silent frames, as a plain file (moov before or after
// mdat) or a fragmented one (moov, then moof + mdat per fragment).
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace WgpuInterop.Tests.MediaFoundation
{
    internal sealed class Mp4TestFile
    {
        public int Width = 64, Height = 48, Frames = 6;
        public uint VideoTimescale = 90000, FrameTicks = 3600;          // 25 fps
        public bool Audio = true;
        public int AudioFrames = 12;
        public bool MoovFirst = true, Fragmented;
        public int CropRight, CropBottom;
        /// <summary>The tkhd matrix's rotation (0, 90, 180, 270).</summary>
        public int Rotation;
        /// <summary>The audio trak before the video's in moov; the tracks' ids.</summary>
        public bool AudioTrakFirst;
        public int VideoTrackId = 1, AudioTrackId = 2;

        /// <summary>The NV12 pictures the video track holds.</summary>
        public readonly List<byte[]> Pictures = new();

        /// <summary>A silent AAC-LC frame, stereo.</summary>
        public static readonly byte[] SilentAac = { 0x21, 0x00, 0x49, 0x90, 0x02, 0x19, 0x00, 0x23, 0x80 };
        public const int SampleRate = 44100;

        private byte[] _sps = Array.Empty<byte>(), _pps = Array.Empty<byte>();

        // ---- the samples ---------------------------------------------------------------------------------

        /// <summary>The video samples: each a length-prefixed IDR picture.</summary>
        public List<byte[]> VideoSamples()
        {
            Pictures.Clear();
            _sps = Strip(MFDecoderTests.Sps(Width / 16, Height / 16, CropRight, CropBottom));
            _pps = Strip(MFDecoderTests.Pps());
            var r = new List<byte[]>();
            for (int k = 0; k < Frames; k++)
            {
                var pic = MFDecoderTests.Picture(Width, Height, k);
                Pictures.Add(pic);
                r.Add(LengthPrefixed(Strip(MFDecoderTests.Idr(pic, Width, Height, k & 1))));
            }
            return r;
        }

        public byte[] SpsNal => _sps;
        public byte[] PpsNal => _pps;

        private static byte[] Strip(byte[] annexB) => annexB.AsSpan(4).ToArray();            // the 00 00 00 01

        private static byte[] LengthPrefixed(byte[] nal)
        {
            var r = new byte[4 + nal.Length];
            r[0] = (byte)(nal.Length >> 24); r[1] = (byte)(nal.Length >> 16); r[2] = (byte)(nal.Length >> 8); r[3] = (byte)nal.Length;
            nal.CopyTo(r, 4);
            return r;
        }

        // ---- boxes ------------------------------------------------------------------------------------

        private sealed class B
        {
            private readonly MemoryStream _m = new();
            public B U8(int v) { _m.WriteByte((byte)v); return this; }
            public B U16(int v) => U8(v >> 8).U8(v);
            public B U24(int v) => U8(v >> 16).U16(v);
            public B U32(long v) => U16((int)(v >> 16) & 0xFFFF).U16((int)v & 0xFFFF);
            public B U64(long v) => U32(v >> 32).U32(v & 0xFFFFFFFF);
            public B Bytes(byte[] b) { _m.Write(b); return this; }
            public B Str(string s) => Bytes(Encoding.ASCII.GetBytes(s));
            public B Zero(int n) => Bytes(new byte[n]);
            public byte[] Done() => _m.ToArray();
            public int Length => (int)_m.Length;
        }

        private static byte[] Box(string type, params byte[][] content)
        {
            int n = 8 + content.Sum(c => c.Length);
            var b = new B().U32(n).Str(type);
            foreach (var c in content) b.Bytes(c);
            return b.Done();
        }

        private static byte[] Full(string type, int version, int flags, params byte[][] content)
            => Box(type, new[] { new B().U8(version).U24(flags).Done() }.Concat(content).ToArray());

        private static readonly byte[] Unity = new B().U32(0x10000).U32(0).U32(0).U32(0).U32(0x10000).U32(0).U32(0).U32(0).U32(0x40000000).Done();

        private byte[] Matrix()
        {
            // a, b, u, c, d, v, x, y, w (16.16, w 2.30): rotation by the angle, translated to stay in view
            int a, b, c, d;
            (a, b, c, d) = Rotation switch { 90 => (0, 1, -1, 0), 180 => (-1, 0, 0, -1), 270 => (0, -1, 1, 0), _ => (1, 0, 0, 1) };
            int tx = Rotation switch { 90 => Height, 180 => Width, _ => 0 };
            int ty = Rotation switch { 180 => Height, 270 => Width, _ => 0 };
            return new B().U32(a * 0x10000).U32(b * 0x10000).U32(0).U32(c * 0x10000).U32(d * 0x10000).U32(0).U32(tx * 0x10000).U32(ty * 0x10000).U32(0x40000000).Done();
        }

        private long VideoDuration => (long)Frames * FrameTicks;
        private long AudioDuration => (long)AudioFrames * 1024;

        private byte[] Mvhd() => Full("mvhd", 0, 0, new B().U32(0).U32(0).U32(1000).U32(Math.Max(VideoDuration * 1000 / VideoTimescale, Audio ? AudioDuration * 1000 / SampleRate : 0))
            .U32(0x10000).U16(0x100).Zero(10).Bytes(Unity).Zero(24).U32(3).Done());

        private byte[] Tkhd(int id, long duration, bool video) => Full("tkhd", 0, 3, new B().U32(0).U32(0).U32(id).U32(0).U32(duration).Zero(8).U16(0).U16(video ? 0 : 1)
            .U16(video ? 0 : 0x100).U16(0).Bytes(video ? Matrix() : Unity).U32(video ? Width << 16 : 0).U32(video ? Height << 16 : 0).Done());

        private static byte[] Mdhd(uint timescale, long duration) => Full("mdhd", 0, 0, new B().U32(0).U32(0).U32(timescale).U32(duration).U16(0x55C4 /* und */).U16(0).Done());

        private static byte[] Hdlr(string type, string name) => Full("hdlr", 0, 0, new B().U32(0).Str(type).Zero(12).Str(name).U8(0).Done());

        private static byte[] Dinf() => Box("dinf", Full("dref", 0, 0, new B().U32(1).Done(), Full("url ", 0, 1)));

        private byte[] Avc1()
        {
            var avcc = new B().U8(1).U8(_sps[1]).U8(_sps[2]).U8(_sps[3]).U8(0xFF).U8(0xE1).U16(_sps.Length).Bytes(_sps).U8(1).U16(_pps.Length).Bytes(_pps).Done();
            var entry = new B().Zero(6).U16(1).Zero(16).U16(Width).U16(Height).U32(0x480000).U32(0x480000).U32(0).U16(1).Zero(32).U16(0x18).U16(0xFFFF).Done();
            return Box("avc1", entry, Box("avcC", avcc));
        }

        private static byte[] Mp4a()
        {
            byte[] asc = { 0x12, 0x10 };                          // AAC-LC, 44100, stereo
            byte[] Desc(int tag, byte[] body) => new B().U8(tag).U8(body.Length).Bytes(body).Done();
            var dcd = Desc(4, new B().U8(0x40).U8(0x15).U24(0).U32(128000).U32(128000).Bytes(Desc(5, asc)).Done());
            var es = Desc(3, new B().U16(1).U8(0).Bytes(dcd).Bytes(Desc(6, new byte[] { 2 })).Done());
            var entry = new B().Zero(6).U16(1).Zero(8).U16(2).U16(16).U16(0).U16(0).U32(SampleRate << 16).Done();
            return Box("mp4a", entry, Full("esds", 0, 0, es));
        }

        private static byte[] Stbl(byte[] stsd, uint[] sizes, uint delta, long[] offsets, bool syncAll, bool video)
        {
            var parts = new List<byte[]>
            {
                Full("stsd", 0, 0, new B().U32(1).Done(), stsd),
                Full("stts", 0, 0, new B().U32(1).U32(sizes.Length).U32(delta).Done()),
            };
            if (video && !syncAll) parts.Add(Full("stss", 0, 0, new B().U32(1).U32(1).Done()));
            parts.Add(Full("stsc", 0, 0, new B().U32(1).U32(1).U32(1).U32(1).Done()));   // a sample per chunk
            var stsz = new B().U32(0).U32(sizes.Length);
            foreach (var s in sizes) stsz.U32(s);
            parts.Add(Full("stsz", 0, 0, stsz.Done()));
            var stco = new B().U32(offsets.Length);
            foreach (var o in offsets) stco.U32(o);
            parts.Add(Full("stco", 0, 0, stco.Done()));
            return Box("stbl", parts.ToArray());
        }

        private byte[] Moov(uint[] vSizes, long[] vOffsets, uint[] aSizes, long[] aOffsets, bool fragmented)
        {
            var tracks = new List<byte[]> { Mvhd() };
            var traks = new List<byte[]>();
            var vstbl = Stbl(Avc1(), fragmented ? Array.Empty<uint>() : vSizes, FrameTicks, fragmented ? Array.Empty<long>() : vOffsets, true, true);
            traks.Add(Box("trak", Tkhd(VideoTrackId, fragmented ? 0 : VideoDuration * 1000 / VideoTimescale, true),
                Box("mdia", Mdhd(VideoTimescale, fragmented ? 0 : VideoDuration), Hdlr("vide", "VideoHandler"),
                    Box("minf", Full("vmhd", 0, 1, new B().Zero(8).Done()), Dinf(), vstbl))));
            if (Audio)
            {
                var astbl = Stbl(Mp4a(), fragmented ? Array.Empty<uint>() : aSizes, 1024, fragmented ? Array.Empty<long>() : aOffsets, true, false);
                traks.Add(Box("trak", Tkhd(AudioTrackId, fragmented ? 0 : AudioDuration * 1000 / SampleRate, false),
                    Box("mdia", Mdhd(SampleRate, fragmented ? 0 : AudioDuration), Hdlr("soun", "SoundHandler"),
                        Box("minf", Full("smhd", 0, 0, new B().Zero(4).Done()), Dinf(), astbl))));
            }
            if (AudioTrakFirst) traks.Reverse();
            tracks.AddRange(traks);
            if (fragmented)
            {
                var trex = new List<byte[]> { Full("trex", 0, 0, new B().U32(VideoTrackId).U32(1).U32(FrameTicks).U32(0).U32(0).Done()) };
                if (Audio) trex.Add(Full("trex", 0, 0, new B().U32(AudioTrackId).U32(1).U32(1024).U32(0).U32(0).Done()));
                tracks.Add(Box("mvex", trex.ToArray()));
            }
            return Box("moov", tracks.ToArray());
        }

        private static byte[] Ftyp(bool fragmented) => Box("ftyp", new B().Str(fragmented ? "iso5" : "isom").U32(0x200).Str("isom").Str("iso2").Str("avc1").Str("mp41").Done());

        /// <summary>The file.</summary>
        public byte[] Build()
        {
            var video = VideoSamples();
            var audio = Audio ? Enumerable.Range(0, AudioFrames).Select(_ => SilentAac).ToList() : new List<byte[]>();
            return Fragmented ? BuildFragmented(video, audio) : BuildPlain(video, audio);
        }

        private byte[] BuildPlain(List<byte[]> video, List<byte[]> audio)
        {
            // interleaved: by time, one sample per chunk
            var order = video.Select((s, i) => (Time: (double)i * FrameTicks / VideoTimescale, Video: true, Data: s))
                .Concat(audio.Select((s, i) => (Time: (double)i * 1024 / SampleRate, Video: false, Data: s)))
                .OrderBy(x => x.Time).ThenBy(x => x.Video ? 0 : 1).ToList();
            var ftyp = Ftyp(false);
            byte[] Moov0(long baseOffset)
            {
                long at = baseOffset + 8;
                var vo = new List<long>(); var ao = new List<long>();
                foreach (var x in order) { (x.Video ? vo : ao).Add(at); at += x.Data.Length; }
                return Moov(video.Select(v => (uint)v.Length).ToArray(), vo.ToArray(), audio.Select(a => (uint)a.Length).ToArray(), ao.ToArray(), false);
            }
            var mdat = Box("mdat", order.Select(x => x.Data).ToArray());
            if (MoovFirst)
            {
                int moovLen = Moov0(0).Length;
                var moov = Moov0(ftyp.Length + moovLen);
                return ftyp.Concat(moov).Concat(mdat).ToArray();
            }
            return ftyp.Concat(mdat).Concat(Moov0(ftyp.Length)).ToArray();
        }

        private byte[] BuildFragmented(List<byte[]> video, List<byte[]> audio)
        {
            var file = new List<byte>(Ftyp(true));
            file.AddRange(Moov(Array.Empty<uint>(), Array.Empty<long>(), Array.Empty<uint>(), Array.Empty<long>(), true));
            // a fragment per two frames, with the audio of their time
            int seq = 1;
            for (int v = 0; v < video.Count; v += 2)
            {
                var vs = video.Skip(v).Take(2).ToList();
                double t0 = (double)v * FrameTicks / VideoTimescale, t1 = (double)(v + vs.Count) * FrameTicks / VideoTimescale;
                int a0 = (int)Math.Ceiling(t0 * SampleRate / 1024 - 1e-9), a1 = v + 2 >= video.Count ? audio.Count : Math.Min(audio.Count, (int)Math.Ceiling(t1 * SampleRate / 1024 - 1e-9));
                var aus = audio.Skip(a0).Take(Math.Max(0, a1 - a0)).ToList();
                byte[] Traf(int id, long decodeTime, List<byte[]> samples, long dataOffset, bool isVideo)
                {
                    var trun = new B().U32(samples.Count).U32(dataOffset);
                    foreach (var s in samples) { trun.U32(s.Length); if (isVideo) trun.U32(0x02000000); }        // sizes; video: each a sync sample
                    return Box("traf", Full("tfhd", 0, 0x20000, new B().U32(id).Done()),                   // default-base-is-moof
                        Full("tfdt", 1, 0, new B().U64(decodeTime).Done()),
                        Full("trun", 0, 0x1 | 0x200 | (isVideo ? 0x400 : 0), trun.Done()));
                }
                byte[] Moof(long vOff, long aOff)
                {
                    var parts = new List<byte[]> { Full("mfhd", 0, 0, new B().U32(seq).Done()), Traf(VideoTrackId, (long)v * FrameTicks, vs, vOff, true) };
                    if (aus.Count > 0) parts.Add(Traf(AudioTrackId, (long)a0 * 1024, aus, aOff, false));
                    return Box("moof", parts.ToArray());
                }
                int moofLen = Moof(0, 0).Length;
                long vOffset = moofLen + 8, aOffset = vOffset + vs.Sum(s => s.Length);
                file.AddRange(Moof(vOffset, aOffset));
                file.AddRange(Box("mdat", vs.Concat(aus).ToArray()));
                seq++;
            }
            return file.ToArray();
        }

        public string WriteTemp(string name)
        {
            string path = Path.Combine(Path.GetTempPath(), $"wpfmf-{name}-{Environment.ProcessId}.mp4");
            File.WriteAllBytes(path, Build());
            return path;
        }
    }
}
