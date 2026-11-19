// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The MPEG-4 file sink's muxer (Documentation/media-foundation-shim.md, "The sink writer"): an MP4 file laid out
// as Windows' MPEG-4 sink writes one (probed: MFSinkWriterProbe's report).
//
//   ftyp mp42 (mp41, isom), a uuid box of Media Foundation's version, mdat (64-bit size), then moov:
//   the movie's timescale the audio's rate where there is audio; video at its frame rate times 1000;
//   chunks of 0.32 s each track; durations of the last sample's time and duration in 100 ns units, rounded
//   down, then in the track's units, rounded down again; audio with a ctts of zeros; esds' descriptors of
//   four-byte lengths; udta with an empty iTunes meta and an Xtra box.
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Microsoft.Wpf.Interop.WebGpu.MediaFoundation.Sink
{
    internal sealed class MuxTrack
    {
        public bool Video;
        public string Format = "avc1";              // avc1, hvc1, mp4a
        public uint Timescale;
        public int Width, Height;
        public uint ParW = 1, ParH = 1;
        public int Channels, SampleRate, SampleBits = 16;
        public uint Bitrate, MaxBitrate, BufferSize;
        /// <summary>The decoder's configuration: avcC's / hvcC's payload, or AAC's AudioSpecificConfig.</summary>
        public byte[] Config = Array.Empty<byte>();
        internal readonly List<(long Offset, int Size, long Time, long Duration, bool Sync)> Samples = new();
        internal readonly List<(long Offset, int Count)> Chunks = new();
        internal readonly MemoryStream Pending = new();
        internal readonly List<(int Size, long Time, long Duration, bool Sync)> PendingSamples = new();
    }

    internal sealed class Mp4Muxer : IDisposable
    {
        private readonly Stream _out;
        private readonly List<MuxTrack> _tracks = new();
        private long _mdatStart = -1;
        private const long ChunkHns = 3_200_000;        // 0.32 s
        private static readonly DateTime s_epoch = new(1904, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        private readonly uint _created = (uint)(DateTime.UtcNow - s_epoch).TotalSeconds;

        public Mp4Muxer(Stream output)
        {
            _out = output;
        }

        /// <summary>
        /// ftyp, Media Foundation's version (uuid) and mdat's header -- in one write, when the first chunk is (Windows' sink writes
        /// nothing before it has samples).
        /// </summary>
        private void Header()
        {
            if (_mdatStart >= 0) return;
            var uuid = new byte[] { 0x5c, 0xa7, 0x08, 0xfb, 0x32, 0x8e, 0x42, 0x05, 0xa8, 0x61, 0x65, 0x0e, 0xca, 0x0a, 0x95, 0x96 };
            var ftyp = Box("ftyp", Enc("mp42"), U32(0), Enc("mp41"), Enc("isom"));
            var version = Box("uuid", uuid, U32(11), Enc("6.2.09200.0"));
            long start = _out.Position;
            _mdatStart = start + ftyp.Length + version.Length;
            _out.Write(Concat(ftyp, version, U32(1), Enc("mdat"), U64(16)));          // a 64-bit size, set at the end
        }



        public MuxTrack Add(MuxTrack t) { _tracks.Add(t); return t; }
        public bool Has(MuxTrack t) => _tracks.Contains(t);

        public void Dispose() => _out.Dispose();

        /// <summary>A sample of the track (H.264 and H.265 as length-prefixed NAL units already), held in its chunk.</summary>
        public void Write(MuxTrack t, ReadOnlySpan<byte> data, long time, long duration, bool sync)
        {
            t.Pending.Write(data);
            t.PendingSamples.Add((data.Length, time, duration, sync));
            long first = t.PendingSamples[0].Time;
            if (time + duration - first >= ChunkHns) Flush(t);
        }

        private readonly List<(MuxTrack Track, int Index, long Start, byte[] Data, List<(int Size, long Time, long Duration, bool Sync)> Samples)> _ready = new();

        /// <summary>The track's chunk closed: written once no track can have a chunk of an earlier start (on ties, the first track's first).</summary>
        private void Flush(MuxTrack t, bool ending = false)
        {
            if (t.PendingSamples.Count > 0)
            {
                _ready.Add((t, _tracks.IndexOf(t), t.PendingSamples[0].Time, t.Pending.ToArray(), t.PendingSamples.ToList()));
                t.Pending.SetLength(0);
                t.PendingSamples.Clear();
            }
            while (_ready.Count > 0)
            {
                var head = _ready.OrderBy(c => c.Start).ThenBy(c => c.Index).First();
                // another track still to close a chunk that starts before (or with, being earlier) this one: wait for it
                bool wait = !ending && _tracks.Any(o => o != head.Track && !_ready.Any(c => c.Track == o) &&
                    (o.PendingSamples.Count > 0 ? (o.PendingSamples[0].Time < head.Start || o.PendingSamples[0].Time == head.Start && _tracks.IndexOf(o) < head.Index) : !_ended.Contains(o)));
                if (wait) return;
                _ready.Remove(head);
                Header();
                long at = _out.Position;
                _out.Write(head.Data);
                head.Track.Chunks.Add((at, head.Samples.Count));
                long off = at;
                foreach (var (size, time, duration, sync) in head.Samples) { head.Track.Samples.Add((off, size, time, duration, sync)); off += size; }
            }
        }

        private readonly HashSet<MuxTrack> _ended = new();

        /// <summary>The last chunks, mdat's size, and moov.</summary>
        public void Finish()
        {
            foreach (var t in _tracks) _ended.Add(t);
            foreach (var t in _tracks) Flush(t, ending: false);
            Flush(_tracks[0], ending: true);
            Header();
            long end = _out.Position;
            // mdat's header again, of its size: from its start, as Windows' sink goes back to it
            _out.Position = _mdatStart;
            _out.Write(Concat(U32(1), Enc("mdat"), U64(end - _mdatStart)));
            _out.Position = end;
            Write(Moov());
            _out.Flush();
        }

        // ---- moov -------------------------------------------------------------------------------------

        private uint MovieTimescale => _tracks.FirstOrDefault(x => !x.Video) is { } a ? (uint)a.SampleRate : _tracks.Count > 0 ? _tracks[0].Timescale : 1000;

        /// <summary>A track's end in a timescale: its last sample's time and duration in 100 ns units, rounded down there.</summary>
        private static ulong End(MuxTrack t, uint scale)
        {
            if (t.Samples.Count == 0) return 0;
            var last = t.Samples[^1];
            return (ulong)((Int128)(last.Time + last.Duration) * scale / 10_000_000);
        }

        private byte[] Moov()
        {
            uint ms = MovieTimescale;
            ulong movieDuration = _tracks.Select(t => End(t, ms)).DefaultIfEmpty(0UL).Max();
            var parts = new List<byte[]>
            {
                Full("mvhd", 0, 0, U32(_created), U32(_created), U32(ms), U32((uint)movieDuration), U32(0x10000), U16(0x100), new byte[10], Matrix(), new byte[24], U32((uint)_tracks.Count + 1)),
            };
            for (int i = 0; i < _tracks.Count; i++) parts.Add(Trak(_tracks[i], (uint)i + 1, ms));
            // udta: an empty iTunes metadata, and Xtra
            var meta = Full("meta", 0, 0, Full("hdlr", 0, 0, U32(0), Enc("mdir"), new byte[12], new byte[1]), Box("ilst"));
            parts.Add(Box("udta", meta, Box("Xtra")));
            return Box("moov", parts.ToArray());
        }

        private byte[] Trak(MuxTrack t, uint id, uint movieScale)
        {
            ulong tkDuration = End(t, movieScale), mdDuration = End(t, t.Timescale);
            var tkhd = Full("tkhd", 0, 1, U32(_created), U32(_created), U32(id), U32(0), U32((uint)tkDuration), new byte[8], U16(0), U16(0), U16(t.Video ? 0 : 0x100), U16(0), Matrix(),
                U32(t.Video ? (uint)t.Width << 16 : 0), U32(t.Video ? (uint)t.Height << 16 : 0));
            var mdhd = Full("mdhd", 0, 0, U32(_created), U32(_created), U32(t.Timescale), U32((uint)mdDuration), U16(0x55C4), U16(0));
            var hdlr = Full("hdlr", 0, 0, U32(0), Enc(t.Video ? "vide" : "soun"), new byte[12], Encoding.ASCII.GetBytes(t.Video ? "VideoHandler\0" : "SoundHandler\0"));
            var mhd = t.Video ? Full("vmhd", 0, 1, new byte[8]) : Full("smhd", 0, 0, new byte[4]);
            var dinf = Box("dinf", Full("dref", 0, 0, U32(1), Full("url ", 0, 1)));
            return Box("trak", tkhd, Box("mdia", mdhd, hdlr, Box("minf", mhd, dinf, Stbl(t))));
        }

        private byte[] Stbl(MuxTrack t)
        {
            var parts = new List<byte[]> { Full("stsd", 0, 0, U32(1), Entry(t)) };
            // stts: the durations in the track's units, runs of equal ones
            var deltas = new List<(uint Count, uint Delta)>();
            long prevTicks = 0;
            for (int i = 0; i < t.Samples.Count; i++)
            {
                long ticks = (long)((Int128)(t.Samples[i].Time + t.Samples[i].Duration) * t.Timescale / 10_000_000);
                long start = i == 0 ? (long)((Int128)t.Samples[0].Time * t.Timescale / 10_000_000) : prevTicks;
                uint d = (uint)Math.Max(0, ticks - start);
                // each sample its own duration where given whole (the video's frames, the audio's packets)
                uint whole = (uint)((Int128)t.Samples[i].Duration * t.Timescale / 10_000_000 + (t.Video ? 0 : 0));
                if (!t.Video) d = (uint)Math.Round((double)t.Samples[i].Duration * t.Timescale / 10_000_000);
                else d = whole;
                prevTicks = start + d;
                if (deltas.Count > 0 && deltas[^1].Delta == d) deltas[^1] = (deltas[^1].Count + 1, d);
                else deltas.Add((1, d));
            }
            parts.Add(Full("stts", 0, 0, U32((uint)deltas.Count), Concat(deltas.Select(x => Concat(U32(x.Count), U32(x.Delta))))));
            if (!t.Video) parts.Add(Full("ctts", 0, 0, U32(1), U32((uint)t.Samples.Count), U32(0)));
            // stsc: a run per change of samples per chunk
            var runs = new List<(uint First, uint Per)>();
            for (int c = 0; c < t.Chunks.Count; c++)
                if (runs.Count == 0 || runs[^1].Per != t.Chunks[c].Count) runs.Add(((uint)c + 1, (uint)t.Chunks[c].Count));
            parts.Add(Full("stsc", 0, 0, U32((uint)runs.Count), Concat(runs.Select(r => Concat(U32(r.First), U32(r.Per), U32(1))))));
            parts.Add(Full("stsz", 0, 0, U32(0), U32((uint)t.Samples.Count), Concat(t.Samples.Select(s => U32((uint)s.Size)))));
            bool big = t.Chunks.Count > 0 && t.Chunks[^1].Offset > uint.MaxValue;
            parts.Add(big ? Full("co64", 0, 0, U32((uint)t.Chunks.Count), Concat(t.Chunks.Select(c => U64(c.Offset))))
                          : Full("stco", 0, 0, U32((uint)t.Chunks.Count), Concat(t.Chunks.Select(c => U32((uint)c.Offset)))));
            if (t.Video && t.Samples.Any(s => !s.Sync))
            {
                var sync = t.Samples.Select((s, i) => (s, i)).Where(x => x.s.Sync).Select(x => U32((uint)x.i + 1)).ToList();
                parts.Add(Full("stss", 0, 0, U32((uint)sync.Count), Concat(sync)));
            }
            else if (t.Video) parts.Add(Full("stss", 0, 0, U32((uint)t.Samples.Count), Concat(Enumerable.Range(1, t.Samples.Count).Select(i => U32((uint)i)))));
            return Box("stbl", parts.ToArray());
        }

        private static byte[] Entry(MuxTrack t)
        {
            if (t.Video)
            {
                // the compressor's name, as Windows' sink names it: a length byte, then the name, in 32 bytes
                string name = t.Format == "hvc1" ? "HEVC Coding" : "AVC Coding";
                var compressor = new byte[32];
                compressor[0] = (byte)name.Length;
                Encoding.ASCII.GetBytes(name).CopyTo(compressor, 1);
                var fields = Concat(new byte[6], U16(1), new byte[16], U16((ushort)t.Width), U16((ushort)t.Height), U32(0x480000), U32(0x480000), U32(0), U16(1), compressor, U16(0x18), U16(0xFFFF));
                return Box(t.Format, fields, Box(t.Format == "hvc1" ? "hvcC" : "avcC", t.Config));
            }
            var audio = Concat(new byte[6], U16(1), new byte[8], U16((ushort)t.Channels), U16((ushort)t.SampleBits), U16(0), U16(0), U32((uint)t.SampleRate << 16));
            return Box("mp4a", audio, Full("esds", 0, 0, Esds(t)));
        }

        /// <summary>ES_Descriptor > DecoderConfigDescriptor > DecoderSpecificInfo of four-byte lengths (0x80 0x80 0x80 n), then SLConfigDescriptor.</summary>
        private static byte[] Esds(MuxTrack t)
        {
            static byte[] D(byte tag, byte[] body) => Concat(new byte[] { tag, 0x80, 0x80, 0x80, (byte)body.Length }, body);
            var dsi = D(5, t.Config);
            var dcd = D(4, Concat(new byte[] { 0x40, 0x15 }, new[] { (byte)(t.BufferSize >> 16), (byte)(t.BufferSize >> 8), (byte)t.BufferSize }, U32(t.MaxBitrate), U32(t.Bitrate), dsi));
            // the SLConfigDescriptor of a one-byte length, as Windows' sink writes it
            return D(3, Concat(U16(0), new byte[] { 0 }, dcd, new byte[] { 6, 1, 2 }));
        }

        // ---- bytes --------------------------------------------------------------------------------------

        private void Write(params byte[][] parts) { foreach (var p in parts) _out.Write(p); }

        private static byte[] Matrix() => Concat(U32(0x10000), U32(0), U32(0), U32(0), U32(0x10000), U32(0), U32(0), U32(0), U32(0x40000000));
        private static byte[] Enc(string s) => Encoding.ASCII.GetBytes(s);
        private static byte[] U16(ushort v) => new[] { (byte)(v >> 8), (byte)v };
        private static byte[] U16(int v) => U16((ushort)v);
        private static byte[] U32(uint v) => new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };
        private static byte[] U32(int v) => U32((uint)v);
        private static byte[] U64(long v) => Concat(U32((uint)(v >> 32)), U32((uint)v));
        private static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();
        private static byte[] Concat(IEnumerable<byte[]> parts) => parts.SelectMany(p => p).ToArray();
        private static byte[] Box(string type, params byte[][] content) { var c = Concat(content); return Concat(U32((uint)(8 + c.Length)), Enc(type), c); }
        private static byte[] Full(string type, byte version, uint flags, params byte[][] content) => Box(type, Concat(new[] { new byte[] { version, (byte)(flags >> 16), (byte)(flags >> 8), (byte)flags } }.Concat(content).ToArray()));
    }
}
