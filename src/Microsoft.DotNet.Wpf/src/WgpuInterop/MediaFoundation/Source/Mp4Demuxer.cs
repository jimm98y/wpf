// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The MPEG-4 file source's demuxer (Documentation/media-foundation-shim.md, "The source reader"): ISO base
// media files (.mp4, .m4v, .m4a, .mov) -- plain, moov before or after mdat, and fragmented (moof + mdat).
// It reads the boxes' headers and the sample tables only; samples are read from the file as they are asked
// for. What it hands out is what Windows' MPEG-4 source hands out (its behaviour probed, MFSourceReaderProbe):
// H.264 and H.265 in Annex B, the parameter sets before each sync sample; the rest as stored.
//

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Wpf.Interop.WebGpu.MediaFoundation.Codecs;

namespace Microsoft.Wpf.Interop.WebGpu.MediaFoundation.Source
{
    /// <summary>Bytes at an offset: a file, or an application's IMFByteStream.</summary>
    internal interface IByteSource : IDisposable
    {
        long Length { get; }
        /// <summary>Reads at the offset; fewer bytes only at the end.</summary>
        int Read(long offset, Span<byte> into);
    }

    internal sealed class FileByteSource : IByteSource
    {
        private readonly Microsoft.Win32.SafeHandles.SafeFileHandle _file;
        public FileByteSource(string path) { _file = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); Length = RandomAccess.GetLength(_file); }
        public long Length { get; }
        public int Read(long offset, Span<byte> into)
        {
            int total = 0;
            while (total < into.Length)
            {
                int n = RandomAccess.Read(_file, into.Slice(total), offset + total);
                if (n <= 0) break;
                total += n;
            }
            return total;
        }
        public void Dispose() => _file.Dispose();
    }

    internal struct Mp4Sample
    {
        public long Offset;
        public int Size;
        public long Dts;
        public int CtsOffset;
        public uint Duration;
        public bool Sync;
        /// <summary>The fragment it is of: -1 in moov's tables.</summary>
        public int Fragment;
    }

    internal sealed class Mp4Track
    {
        public uint Id;
        public string Handler = "";
        public uint Timescale = 1;
        public ulong MediaDuration;
        /// <summary>The first edit's start in the media (elst media_time), in its timescale.</summary>
        public long EditStart;
        public string Format = "";
        /// <summary>The whole stsd box.</summary>
        public byte[] SampleDescription = Array.Empty<byte>();
        public int Width, Height;
        public int Rotation;
        public uint ParW = 1, ParH = 1;
        /// <summary>The codec configuration box's payload (avcC, hvcC, vpcC, av1C, dOps, dfLa, alac), or esds' decoder specific info.</summary>
        public byte[] Config = Array.Empty<byte>();
        public byte ObjectType;
        public uint EsdsBitrate;
        public int Channels, SampleRate, SampleBits;
        public int NalLength = 4;
        /// <summary>H.264's and H.265's parameter sets, without start codes, in order.</summary>
        public readonly List<byte[]> ParameterSets = new();
        public readonly List<Mp4Sample> Samples = new();
        /// <summary>trex's defaults.</summary>
        public uint DefDescription = 1, DefDuration, DefSize, DefFlags;
        public bool Video => Handler == "vide";
        public bool Audio => Handler == "soun";
        public bool AnnexB => Format is "avc1" or "avc3" or "hvc1" or "hev1";
        public bool Hevc => Format is "hvc1" or "hev1";

        /// <summary>A time of the track's timescale in 100 ns units.</summary>
        public long Hns(long t) => (long)((Int128)t * 10_000_000 / Timescale);

        public long Pts(in Mp4Sample s) => Hns(s.Dts + s.CtsOffset - EditStart);
    }

    internal sealed class Mp4Demuxer : IDisposable
    {
        private readonly IByteSource _src;
        public readonly List<Mp4Track> Tracks = new();
        public uint MovieTimescale = 1000;
        public ulong MovieDuration, FragmentDuration;
        public bool Fragmented;
        private readonly object _lock = new();

        private Mp4Demuxer(IByteSource src) { _src = src; }

        public long Length => _src.Length;

        public void Dispose() => _src.Dispose();

        /// <summary>Whether the bytes begin as an ISO base media file does.</summary>
        public static bool Sniff(IByteSource src)
        {
            Span<byte> h = stackalloc byte[8];
            if (src.Read(0, h) < 8) return false;
            string type = Encoding.ASCII.GetString(h.Slice(4, 4));
            return type is "ftyp" or "moov" or "mdat" or "free" or "skip" or "wide" or "pnot" or "styp" or "sidx" or "uuid";
        }

        public static Mp4Demuxer Open(IByteSource src)
        {
            var d = new Mp4Demuxer(src);
            d.ReadTop();
            return d;
        }

        // ---- boxes ------------------------------------------------------------------------------------

        private byte[] ReadAt(long offset, int n)
        {
            var b = new byte[n];
            int got = _src.Read(offset, b);
            if (got < n) Array.Resize(ref b, got);
            return b;
        }

        /// <summary>The boxes of a span: type, payload start, payload length.</summary>
        private static IEnumerable<(string Type, int Start, int Length)> Children(byte[] data, int start, int end)
        {
            int at = start;
            while (at + 8 <= end)
            {
                long size = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(at));
                string type = Encoding.ASCII.GetString(data, at + 4, 4);
                int header = 8;
                if (size == 1) { if (at + 16 > end) yield break; size = (long)BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(at + 8)); header = 16; }
                else if (size == 0) size = end - at;
                if (size < header || at + size > end) yield break;
                yield return (type, at + header, (int)size - header);
                at += (int)size;
            }
        }

        private void ReadTop()
        {
            long at = 0, len = _src.Length;
            int fragment = 0;
            Span<byte> h = stackalloc byte[16];
            while (at + 8 <= len)
            {
                if (_src.Read(at, h) < 8) break;
                long size = BinaryPrimitives.ReadUInt32BigEndian(h);
                string type = Encoding.ASCII.GetString(h.Slice(4, 4));
                int header = 8;
                if (size == 1) { size = (long)BinaryPrimitives.ReadUInt64BigEndian(h.Slice(8)); header = 16; }
                else if (size == 0) size = len - at;
                if (size < header) break;
                if (type == "moov" && size < int.MaxValue) Moov(ReadAt(at + header, (int)size - header));
                else if (type == "moof" && size < int.MaxValue) { Moof(ReadAt(at + header, (int)size - header), at, fragment++); Fragmented = true; }
                at += size;
            }
            if (Tracks.Count == 0) throw new InvalidDataException("no moov");
        }

        private void Moov(byte[] m)
        {
            foreach (var (type, s, n) in Children(m, 0, m.Length))
            {
                if (type == "mvhd")
                {
                    int v = m[s];
                    MovieTimescale = BinaryPrimitives.ReadUInt32BigEndian(m.AsSpan(s + (v == 1 ? 20 : 12)));
                    MovieDuration = v == 1 ? BinaryPrimitives.ReadUInt64BigEndian(m.AsSpan(s + 24)) : BinaryPrimitives.ReadUInt32BigEndian(m.AsSpan(s + 16));
                    if (MovieDuration == (v == 1 ? ulong.MaxValue : uint.MaxValue)) MovieDuration = 0;
                    if (MovieTimescale == 0) MovieTimescale = 1000;
                }
                else if (type == "trak") Trak(m, s, s + n);
                else if (type == "mvex")
                    foreach (var (t2, s2, n2) in Children(m, s, s + n))
                    {
                        if (t2 == "mehd") FragmentDuration = m[s2] == 1 ? BinaryPrimitives.ReadUInt64BigEndian(m.AsSpan(s2 + 4)) : BinaryPrimitives.ReadUInt32BigEndian(m.AsSpan(s2 + 4));
                        else if (t2 == "trex" && Tracks.FirstOrDefault(t => t.Id == BinaryPrimitives.ReadUInt32BigEndian(m.AsSpan(s2 + 4))) is { } tr)
                        {
                            tr.DefDescription = BinaryPrimitives.ReadUInt32BigEndian(m.AsSpan(s2 + 8));
                            tr.DefDuration = BinaryPrimitives.ReadUInt32BigEndian(m.AsSpan(s2 + 12));
                            tr.DefSize = BinaryPrimitives.ReadUInt32BigEndian(m.AsSpan(s2 + 16));
                            tr.DefFlags = BinaryPrimitives.ReadUInt32BigEndian(m.AsSpan(s2 + 20));
                        }
                    }
            }
        }

        private static (string, int, int)? Find(byte[] d, int s, int e, params string[] path)
        {
            (string, int, int)? cur = null;
            int cs = s, ce = e;
            foreach (var p in path)
            {
                cur = null;
                foreach (var c in Children(d, cs, ce)) if (c.Type == p) { cur = c; break; }
                if (cur is not { } f) return null;
                cs = f.Item2; ce = f.Item2 + f.Item3;
            }
            return cur;
        }

        private void Trak(byte[] m, int s, int e)
        {
            var t = new Mp4Track();
            if (Find(m, s, e, "tkhd") is { } tk)
            {
                int v = m[tk.Item2];
                t.Id = BinaryPrimitives.ReadUInt32BigEndian(m.AsSpan(tk.Item2 + (v == 1 ? 20 : 12)));
                int mx = tk.Item2 + (v == 1 ? 52 : 40);
                if (mx + 36 <= m.Length)
                {
                    int a = BinaryPrimitives.ReadInt32BigEndian(m.AsSpan(mx)), b = BinaryPrimitives.ReadInt32BigEndian(m.AsSpan(mx + 4));
                    // the angle the matrix turns by, clockwise, as Windows reports it (MF_MT_VIDEO_ROTATION)
                    t.Rotation = (a, b) switch { (0, > 0) => 90, ( < 0, 0) => 180, (0, < 0) => 270, _ => 0 };
                }
            }
            if (Find(m, s, e, "edts", "elst") is { } el)
            {
                int v = m[el.Item2];
                uint count = BinaryPrimitives.ReadUInt32BigEndian(m.AsSpan(el.Item2 + 4));
                int at = el.Item2 + 8;
                for (uint i = 0; i < count && at + (v == 1 ? 20 : 12) <= el.Item2 + el.Item3; i++)
                {
                    long mediaTime = v == 1 ? BinaryPrimitives.ReadInt64BigEndian(m.AsSpan(at + 8)) : BinaryPrimitives.ReadInt32BigEndian(m.AsSpan(at + 4));
                    at += v == 1 ? 20 : 12;
                    if (mediaTime >= 0) { t.EditStart = mediaTime; break; }      // an empty edit (-1) delays the track; the first real one gives the start
                }
            }
            if (Find(m, s, e, "mdia", "mdhd") is { } md)
            {
                int v = m[md.Item2];
                t.Timescale = BinaryPrimitives.ReadUInt32BigEndian(m.AsSpan(md.Item2 + (v == 1 ? 20 : 12)));
                t.MediaDuration = v == 1 ? BinaryPrimitives.ReadUInt64BigEndian(m.AsSpan(md.Item2 + 24)) : BinaryPrimitives.ReadUInt32BigEndian(m.AsSpan(md.Item2 + 16));
                if (t.MediaDuration == (v == 1 ? ulong.MaxValue : uint.MaxValue)) t.MediaDuration = 0;
                if (t.Timescale == 0) t.Timescale = 1;
            }
            if (Find(m, s, e, "mdia", "hdlr") is { } hd) t.Handler = Encoding.ASCII.GetString(m, hd.Item2 + 8, 4);
            if (Find(m, s, e, "mdia", "minf", "stbl") is not { } stbl) return;
            int ss = stbl.Item2, se = stbl.Item2 + stbl.Item3;
            if (Find(m, ss, se, "stsd") is { } sd)
            {
                t.SampleDescription = m.AsSpan(sd.Item2 - 8, sd.Item3 + 8).ToArray();
                SampleEntry(t, m, sd.Item2 + 8, sd.Item2 + sd.Item3);
            }
            SampleTable(t, m, ss, se);
            Tracks.Add(t);
        }

        private static void SampleEntry(Mp4Track t, byte[] m, int s, int e)
        {
            foreach (var (type, es, en) in Children(m, s, e))
            {
                t.Format = type;
                int end = es + en;
                if (t.Video)
                {
                    t.Width = BinaryPrimitives.ReadUInt16BigEndian(m.AsSpan(es + 24));
                    t.Height = BinaryPrimitives.ReadUInt16BigEndian(m.AsSpan(es + 26));
                    foreach (var (ct, cs, cn) in Children(m, es + 78, end))
                    {
                        var payload = m.AsSpan(cs, cn).ToArray();
                        switch (ct)
                        {
                            case "avcC": t.Config = payload; AvcC(t, payload); break;
                            case "hvcC": t.Config = payload; HvcC(t, payload); break;
                            case "vpcC": case "av1C": t.Config = payload; break;
                            case "esds": Esds(t, payload); break;
                            case "pasp": t.ParW = BinaryPrimitives.ReadUInt32BigEndian(payload); t.ParH = BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(4)); break;
                        }
                    }
                }
                else if (t.Audio)
                {
                    int version = BinaryPrimitives.ReadUInt16BigEndian(m.AsSpan(es + 8));
                    t.Channels = BinaryPrimitives.ReadUInt16BigEndian(m.AsSpan(es + 16));
                    t.SampleBits = BinaryPrimitives.ReadUInt16BigEndian(m.AsSpan(es + 18));
                    t.SampleRate = (int)(BinaryPrimitives.ReadUInt32BigEndian(m.AsSpan(es + 24)) >> 16);
                    int boxes = es + 28 + (version == 1 ? 16 : version == 2 ? 36 : 0);
                    if (version == 2 && es + 64 <= end)
                    {
                        t.SampleRate = (int)BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64BigEndian(m.AsSpan(es + 32)));
                        t.Channels = (int)BinaryPrimitives.ReadUInt32BigEndian(m.AsSpan(es + 40));
                        t.SampleBits = (int)BinaryPrimitives.ReadUInt32BigEndian(m.AsSpan(es + 48));
                    }
                    foreach (var (ct, cs, cn) in Children(m, Math.Min(boxes, end), end))
                    {
                        var payload = m.AsSpan(cs, cn).ToArray();
                        switch (ct)
                        {
                            case "esds": Esds(t, payload); break;
                            case "dOps": case "dfLa": case "alac": case "dac3": case "dec3": t.Config = payload; break;
                            case "wave": foreach (var (wt, ws, wn) in Children(payload, 0, payload.Length)) if (wt == "esds") Esds(t, payload.AsSpan(ws, wn).ToArray()); break;
                        }
                    }
                }
                return;                                                    // the first entry
            }
        }

        private static void AvcC(Mp4Track t, byte[] c)
        {
            if (c.Length < 7) return;
            t.NalLength = (c[4] & 3) + 1;
            int at = 5;
            int n = c[at++] & 0x1F;
            for (int i = 0; i < n && at + 2 <= c.Length; i++) { int l = BinaryPrimitives.ReadUInt16BigEndian(c.AsSpan(at)); at += 2; if (at + l > c.Length) return; t.ParameterSets.Add(c.AsSpan(at, l).ToArray()); at += l; }
            if (at >= c.Length) return;
            n = c[at++];
            for (int i = 0; i < n && at + 2 <= c.Length; i++) { int l = BinaryPrimitives.ReadUInt16BigEndian(c.AsSpan(at)); at += 2; if (at + l > c.Length) return; t.ParameterSets.Add(c.AsSpan(at, l).ToArray()); at += l; }
        }

        private static void HvcC(Mp4Track t, byte[] c)
        {
            if (c.Length < 23) return;
            t.NalLength = (c[21] & 3) + 1;
            int arrays = c[22], at = 23;
            for (int a = 0; a < arrays && at + 3 <= c.Length; a++)
            {
                int n = BinaryPrimitives.ReadUInt16BigEndian(c.AsSpan(at + 1));
                at += 3;
                for (int i = 0; i < n && at + 2 <= c.Length; i++) { int l = BinaryPrimitives.ReadUInt16BigEndian(c.AsSpan(at)); at += 2; if (at + l > c.Length) return; t.ParameterSets.Add(c.AsSpan(at, l).ToArray()); at += l; }
            }
        }

        private static void Esds(Mp4Track t, byte[] e)
        {
            // full box: version and flags, then ES_Descriptor (3) > DecoderConfigDescriptor (4) > DecoderSpecificInfo (5)
            int at = 4;
            static int Len(byte[] d, ref int i) { int l = 0; for (int k = 0; k < 4 && i < d.Length; k++) { byte b = d[i++]; l = l << 7 | (b & 0x7F); if ((b & 0x80) == 0) break; } return l; }
            if (at >= e.Length || e[at++] != 3) return;
            Len(e, ref at);
            if (at + 3 > e.Length) return;
            int flags = e[at + 2];
            at += 3;
            if ((flags & 0x80) != 0) at += 2;
            if ((flags & 0x40) != 0 && at < e.Length) at += 1 + e[at];
            if ((flags & 0x20) != 0) at += 2;
            if (at >= e.Length || e[at++] != 4) return;
            int dcdLen = Len(e, ref at);
            int dcdEnd = Math.Min(e.Length, at + dcdLen);
            if (at + 13 > e.Length) return;
            t.ObjectType = e[at];
            t.EsdsBitrate = BinaryPrimitives.ReadUInt32BigEndian(e.AsSpan(at + 9));
            at += 13;
            if (at < dcdEnd && e[at++] == 5)
            {
                int l = Len(e, ref at);
                t.Config = e.AsSpan(at, Math.Min(l, e.Length - at)).ToArray();
            }
        }

        private static void SampleTable(Mp4Track t, byte[] m, int s, int e)
        {
            uint[] sizes = Array.Empty<uint>(); uint fixedSize = 0; int count = 0;
            var chunks = new List<long>();
            var stsc = new List<(uint First, uint PerChunk)>();
            var stts = new List<(uint Count, uint Delta)>();
            var ctts = new List<(uint Count, int Offset)>();
            HashSet<uint>? sync = null;
            foreach (var (type, bs, bn) in Children(m, s, e))
            {
                var b = m.AsSpan(bs, bn);
                switch (type)
                {
                    case "stsz":
                        fixedSize = BinaryPrimitives.ReadUInt32BigEndian(b.Slice(4));
                        count = (int)BinaryPrimitives.ReadUInt32BigEndian(b.Slice(8));
                        if (fixedSize == 0) { sizes = new uint[count]; for (int i = 0; i < count && 12 + i * 4 + 4 <= b.Length; i++) sizes[i] = BinaryPrimitives.ReadUInt32BigEndian(b.Slice(12 + i * 4)); }
                        break;
                    case "stz2":
                    {
                        int field = b[7];
                        count = (int)BinaryPrimitives.ReadUInt32BigEndian(b.Slice(8));
                        sizes = new uint[count];
                        for (int i = 0; i < count; i++)
                            sizes[i] = field switch { 4 => (uint)((b[12 + i / 2] >> (i % 2 == 0 ? 4 : 0)) & 0xF), 8 => b[12 + i], _ => BinaryPrimitives.ReadUInt16BigEndian(b.Slice(12 + i * 2)) };
                        break;
                    }
                    case "stco": { uint n = BinaryPrimitives.ReadUInt32BigEndian(b.Slice(4)); for (int i = 0; i < n && 8 + i * 4 + 4 <= b.Length; i++) chunks.Add(BinaryPrimitives.ReadUInt32BigEndian(b.Slice(8 + i * 4))); break; }
                    case "co64": { uint n = BinaryPrimitives.ReadUInt32BigEndian(b.Slice(4)); for (int i = 0; i < n && 8 + i * 8 + 8 <= b.Length; i++) chunks.Add((long)BinaryPrimitives.ReadUInt64BigEndian(b.Slice(8 + i * 8))); break; }
                    case "stsc": { uint n = BinaryPrimitives.ReadUInt32BigEndian(b.Slice(4)); for (int i = 0; i < n && 8 + i * 12 + 12 <= b.Length; i++) stsc.Add((BinaryPrimitives.ReadUInt32BigEndian(b.Slice(8 + i * 12)), BinaryPrimitives.ReadUInt32BigEndian(b.Slice(12 + i * 12)))); break; }
                    case "stts": { uint n = BinaryPrimitives.ReadUInt32BigEndian(b.Slice(4)); for (int i = 0; i < n && 8 + i * 8 + 8 <= b.Length; i++) stts.Add((BinaryPrimitives.ReadUInt32BigEndian(b.Slice(8 + i * 8)), BinaryPrimitives.ReadUInt32BigEndian(b.Slice(12 + i * 8)))); break; }
                    case "ctts": { uint n = BinaryPrimitives.ReadUInt32BigEndian(b.Slice(4)); for (int i = 0; i < n && 8 + i * 8 + 8 <= b.Length; i++) ctts.Add((BinaryPrimitives.ReadUInt32BigEndian(b.Slice(8 + i * 8)), BinaryPrimitives.ReadInt32BigEndian(b.Slice(12 + i * 8)))); break; }
                    case "stss": { sync = new(); uint n = BinaryPrimitives.ReadUInt32BigEndian(b.Slice(4)); for (int i = 0; i < n && 8 + i * 4 + 4 <= b.Length; i++) sync.Add(BinaryPrimitives.ReadUInt32BigEndian(b.Slice(8 + i * 4))); break; }
                }
            }
            if (count == 0 || chunks.Count == 0) return;
            // offsets: the chunks, their samples in order
            var offsets = new long[count];
            int sample = 0;
            for (int c = 0; c < chunks.Count && sample < count; c++)
            {
                uint per = 0;
                for (int k = stsc.Count - 1; k >= 0; k--) if (stsc[k].First <= c + 1) { per = stsc[k].PerChunk; break; }
                long at = chunks[c];
                for (uint i = 0; i < per && sample < count; i++, sample++)
                {
                    offsets[sample] = at;
                    at += fixedSize != 0 ? fixedSize : sizes[sample];
                }
            }
            long dts = 0;
            int si = 0, ci = 0; uint sleft = stts.Count > 0 ? stts[0].Count : 0, cleft = ctts.Count > 0 ? ctts[0].Count : 0;
            for (int i = 0; i < sample; i++)
            {
                while (sleft == 0 && si + 1 < stts.Count) sleft = stts[++si].Count;
                uint delta = stts.Count > 0 ? stts[si].Delta : 0;
                if (sleft > 0) sleft--;
                int cts = 0;
                if (ctts.Count > 0)
                {
                    while (cleft == 0 && ci + 1 < ctts.Count) cleft = ctts[++ci].Count;
                    cts = ctts[ci].Offset;
                    if (cleft > 0) cleft--;
                }
                t.Samples.Add(new Mp4Sample { Offset = offsets[i], Size = (int)(fixedSize != 0 ? fixedSize : sizes[i]), Dts = dts, CtsOffset = cts, Duration = delta, Sync = sync == null || sync.Contains((uint)i + 1), Fragment = -1 });
                dts += delta;
            }
        }

        private void Moof(byte[] m, long moofStart, int fragment)
        {
            long nextBase = moofStart;
            foreach (var (type, s, n) in Children(m, 0, m.Length))
            {
                if (type != "traf") continue;
                Mp4Track? t = null;
                long baseOffset = nextBase;
                uint defDuration = 0, defSize = 0, defFlags = 0;
                long? decodeTime = null, runEnd = null;
                foreach (var (ct, cs, cn) in Children(m, s, s + n))
                {
                    if (ct == "tfhd")
                    {
                        uint flags = BinaryPrimitives.ReadUInt32BigEndian(m.AsSpan(cs)) & 0xFFFFFF;
                        uint id = BinaryPrimitives.ReadUInt32BigEndian(m.AsSpan(cs + 4));
                        t = Tracks.FirstOrDefault(x => x.Id == id);
                        if (t == null) break;
                        defDuration = t.DefDuration; defSize = t.DefSize; defFlags = t.DefFlags;
                        int at = cs + 8;
                        if ((flags & 0x1) != 0) { baseOffset = (long)BinaryPrimitives.ReadUInt64BigEndian(m.AsSpan(at)); at += 8; }
                        else if ((flags & 0x20000) != 0) baseOffset = moofStart;
                        if ((flags & 0x2) != 0) at += 4;
                        if ((flags & 0x8) != 0) { defDuration = BinaryPrimitives.ReadUInt32BigEndian(m.AsSpan(at)); at += 4; }
                        if ((flags & 0x10) != 0) { defSize = BinaryPrimitives.ReadUInt32BigEndian(m.AsSpan(at)); at += 4; }
                        if ((flags & 0x20) != 0) { defFlags = BinaryPrimitives.ReadUInt32BigEndian(m.AsSpan(at)); }
                    }
                    else if (ct == "tfdt" && t != null)
                    {
                        long tfdt = m[cs] == 1 ? (long)BinaryPrimitives.ReadUInt64BigEndian(m.AsSpan(cs + 4)) : BinaryPrimitives.ReadUInt32BigEndian(m.AsSpan(cs + 4));
                        // Windows takes the time to 100 ns units and back, each rounded down: a tick early where it does not divide
                        decodeTime = (long)((Int128)t.Hns(tfdt) * t.Timescale / 10_000_000);
                    }
                    else if (ct == "trun" && t != null)
                    {
                        uint flags = BinaryPrimitives.ReadUInt32BigEndian(m.AsSpan(cs)) & 0xFFFFFF;
                        int version = m[cs];
                        uint count = BinaryPrimitives.ReadUInt32BigEndian(m.AsSpan(cs + 4));
                        int at = cs + 8;
                        // without a data offset, a run follows the one before it (the first, the base)
                        long dataAt = runEnd ?? baseOffset;
                        if ((flags & 0x1) != 0) { dataAt = baseOffset + BinaryPrimitives.ReadInt32BigEndian(m.AsSpan(at)); at += 4; }
                        uint? firstFlags = null;
                        if ((flags & 0x4) != 0) { firstFlags = BinaryPrimitives.ReadUInt32BigEndian(m.AsSpan(at)); at += 4; }
                        long dts = decodeTime ?? (t.Samples.Count > 0 ? t.Samples[^1].Dts + t.Samples[^1].Duration : 0);
                        decodeTime = null;
                        for (uint i = 0; i < count; i++)
                        {
                            uint dur = defDuration, size = defSize, sflags = i == 0 && firstFlags is { } ff ? ff : defFlags; int cts = 0;
                            if ((flags & 0x100) != 0) { dur = BinaryPrimitives.ReadUInt32BigEndian(m.AsSpan(at)); at += 4; }
                            if ((flags & 0x200) != 0) { size = BinaryPrimitives.ReadUInt32BigEndian(m.AsSpan(at)); at += 4; }
                            if ((flags & 0x400) != 0) { sflags = BinaryPrimitives.ReadUInt32BigEndian(m.AsSpan(at)); at += 4; }
                            if ((flags & 0x800) != 0) { cts = version == 0 ? (int)BinaryPrimitives.ReadUInt32BigEndian(m.AsSpan(at)) : BinaryPrimitives.ReadInt32BigEndian(m.AsSpan(at)); at += 4; }
                            // sample_is_non_sync_sample; audio is all sync whatever the flags say
                            bool isSync = t.Audio || (sflags & 0x10000) == 0;
                            t.Samples.Add(new Mp4Sample { Offset = dataAt, Size = (int)size, Dts = dts, CtsOffset = cts, Duration = dur, Sync = isSync, Fragment = fragment });
                            dataAt += size;
                            dts += dur;
                        }
                        nextBase = dataAt;
                        runEnd = dataAt;
                    }
                }
            }
        }

        // ---- samples ----------------------------------------------------------------------------------

        /// <summary>
        /// A sample as Windows' source hands it out: H.264 / H.265 in Annex B, the parameter sets (3-byte start
        /// codes) before a sync sample and each NAL unit after a 4-byte one; with the NAL units' lengths.
        /// </summary>
        public byte[] Read(Mp4Track t, in Mp4Sample s, out uint[]? nalLengths)
        {
            nalLengths = null;
            byte[] raw;
            lock (_lock) raw = ReadAt(s.Offset, s.Size);
            if (!t.AnnexB) return raw;
            var lengths = new List<uint>();
            var o = new List<byte>(raw.Length + 64);
            if (s.Sync)
                foreach (var p in t.ParameterSets) { o.AddRange(new byte[] { 0, 0, 1 }); o.AddRange(p); lengths.Add((uint)p.Length + 3); }
            int at = 0, L = t.NalLength;
            while (at + L <= raw.Length)
            {
                int n = 0;
                for (int i = 0; i < L; i++) n = n << 8 | raw[at + i];
                at += L;
                if (n <= 0 || at + n > raw.Length) break;
                o.AddRange(new byte[] { 0, 0, 0, 1 });
                o.AddRange(new ArraySegment<byte>(raw, at, n));
                lengths.Add((uint)n + 4);
                at += n;
            }
            nalLengths = lengths.ToArray();
            return o.ToArray();
        }

        /// <summary>The parameter sets as MF_MT_MPEG_SEQUENCE_HEADER has them: each after a 3-byte start code.</summary>
        public static byte[] SequenceHeader(Mp4Track t)
        {
            var o = new List<byte>();
            foreach (var p in t.ParameterSets) { o.AddRange(new byte[] { 0, 0, 1 }); o.AddRange(p); }
            return o.ToArray();
        }

        /// <summary>The picture's size, cropped, from the stream's sequence parameter set.</summary>
        public static (int W, int H)? PictureSize(Mp4Track t)
        {
            try
            {
                foreach (var p in t.ParameterSets)
                {
                    if (!t.Hevc && p.Length > 4 && (p[0] & 0x1F) == 7) return H264Size(p);
                    if (t.Hevc && p.Length > 4 && ((p[0] >> 1) & 0x3F) == 33) return HevcSize(p);
                }
            }
            catch (Exception e) when (e is EndOfStreamException or InvalidDataException) { }
            return null;
        }

        /// <summary>H.264's profile_idc and level_idc (MF_MT_MPEG2_PROFILE, _LEVEL).</summary>
        public static (uint Profile, uint Level)? ProfileLevel(Mp4Track t)
        {
            foreach (var p in t.ParameterSets)
            {
                if (!t.Hevc && p.Length > 3 && (p[0] & 0x1F) == 7) return (p[1], p[3]);
                if (t.Hevc && p.Length > 14 && ((p[0] >> 1) & 0x3F) == 33)
                {
                    var r = new BitReader(p.AsSpan(2), rbsp: true);
                    r.U(8);                                            // vps id, sub-layers, nesting
                    r.U(2); r.U(1);
                    uint profile = r.U(5);
                    r.Skip(32 + 48);
                    return (profile, r.U(8));
                }
            }
            return null;
        }

        private static (int, int) H264Size(byte[] nal)
        {
            var r = new BitReader(nal.AsSpan(1), rbsp: true);
            uint profile = r.U(8); r.U(8); r.U(8);
            r.Ue();
            uint chroma = 1;
            if (profile is 100 or 110 or 122 or 244 or 44 or 83 or 86 or 118 or 128 or 138 or 139 or 134 or 135)
            {
                chroma = r.Ue();
                if (chroma == 3) r.U(1);
                r.Ue(); r.Ue(); r.U(1);
                if (r.Flag())
                    for (int i = 0; i < (chroma != 3 ? 8 : 12); i++)
                        if (r.Flag())
                        {
                            int size = i < 6 ? 16 : 64, last = 8, next = 8;
                            for (int j = 0; j < size; j++) { if (next != 0) next = (last + r.Se() + 256) % 256; last = next == 0 ? last : next; }
                        }
            }
            r.Ue();
            uint poc = r.Ue();
            if (poc == 0) r.Ue();
            else if (poc == 1) { r.U(1); r.Se(); r.Se(); uint n = r.Ue(); for (uint i = 0; i < n; i++) r.Se(); }
            r.Ue(); r.U(1);
            int w = (int)(r.Ue() + 1) * 16;
            int hUnits = (int)r.Ue() + 1;
            bool frameOnly = r.Flag();
            int h = hUnits * 16 * (frameOnly ? 1 : 2);
            if (!frameOnly) r.U(1);
            r.U(1);
            if (r.Flag())
            {
                int cl = (int)r.Ue(), cr = (int)r.Ue(), ct = (int)r.Ue(), cb = (int)r.Ue();
                int ux = chroma is 1 or 2 ? 2 : 1, uy = (chroma == 1 ? 2 : 1) * (frameOnly ? 1 : 2);
                if (chroma == 0) { ux = 1; uy = frameOnly ? 1 : 2; }
                w -= (cl + cr) * ux; h -= (ct + cb) * uy;
            }
            return (w, h);
        }

        /// <summary>Whether H.264's SPS says frames only (frame_mbs_only_flag: MF_PROGRESSIVE_CODING_CONTENT).</summary>
        public static bool H264FramesOnly(Mp4Track t)
        {
            foreach (var p in t.ParameterSets)
                if (!t.Hevc && p.Length > 4 && (p[0] & 0x1F) == 7)
                    try
                    {
                        var r = new BitReader(p.AsSpan(1), rbsp: true);
                        uint profile = r.U(8); r.U(16); r.Ue();
                        if (profile is 100 or 110 or 122 or 244 or 44 or 83 or 86 or 118 or 128 or 138 or 139 or 134 or 135) return true;   // scaling lists in the way: frames assumed
                        r.Ue();
                        uint poc = r.Ue();
                        if (poc == 0) r.Ue();
                        else if (poc == 1) { r.U(1); r.Se(); r.Se(); uint n = r.Ue(); for (uint i = 0; i < n; i++) r.Se(); }
                        r.Ue(); r.U(1); r.Ue(); r.Ue();
                        return r.Flag();
                    }
                    catch (Exception e) when (e is EndOfStreamException or InvalidDataException) { }
            return true;
        }

        private static (int, int) HevcSize(byte[] nal)
        {
            var r = new BitReader(nal.AsSpan(2), rbsp: true);
            r.U(4);
            int sub = (int)r.U(3);
            r.U(1);
            r.Skip(88); r.U(8);
            Span<bool> pp = stackalloc bool[8], lp = stackalloc bool[8];
            for (int i = 0; i < sub; i++) { pp[i] = r.Flag(); lp[i] = r.Flag(); }
            if (sub > 0) for (int i = sub; i < 8; i++) r.U(2);
            for (int i = 0; i < sub; i++) { if (pp[i]) r.Skip(88); if (lp[i]) r.U(8); }
            r.Ue();
            uint chroma = r.Ue();
            if (chroma == 3) r.U(1);
            int w = (int)r.Ue(), h = (int)r.Ue();
            if (r.Flag())
            {
                int cl = (int)r.Ue(), cr = (int)r.Ue(), ct = (int)r.Ue(), cb = (int)r.Ue();
                int ux = chroma is 1 or 2 ? 2 : 1, uy = chroma == 1 ? 2 : 1;
                w -= (cl + cr) * ux; h -= (ct + cb) * uy;
            }
            return (w, h);
        }
    }
}
