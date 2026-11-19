// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Matroska and WebM (Documentation/media-foundation-shim.md, "The source reader"): as Windows' Matroska
// source hands them out (probed: MFSourceReaderProbe's report).
//
//   - the blocks in the order they are stored, each at its timecode (a presentation time), its duration the
//     track's DefaultDuration, in 100 ns units rounded down;
//   - H.264 and HEVC in Annex B like the MPEG-4 source's, the parameter sets of CodecPrivate before each key
//     frame; FLAC's CodecPrivate (fLaC and its metadata) a sample of its own before the first frame;
//   - native types of Windows' own: the codec ID (an attribute of no published name), compressed, no
//     bitrate, the frame rate of twice the DefaultDuration (as Windows reads it).
//

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.Wpf.Interop.WebGpu.Direct3D;
using Microsoft.Wpf.Interop.WebGpu.MediaFoundation.Codecs;

namespace Microsoft.Wpf.Interop.WebGpu.MediaFoundation.Source
{
    internal sealed class MatroskaContainer : MediaContainer
    {
        /// <summary>The track's Matroska codec ID, as Windows' source gives it.</summary>
        public static readonly Guid MF_MT_MATROSKA_CODEC_ID = new("9668dde5-c869-4d6a-b02e-463b7c231b53");
        public static readonly Guid MF_MT_AUDIO_FLOAT_SAMPLES_PER_SECOND = new("fb3b724a-cfb5-4319-aefe-6e42b2406132");

        private sealed class Track
        {
            public ulong Number, Type, DefaultDuration, CodecDelay;
            public string CodecId = "";
            public byte[] CodecPrivate = Array.Empty<byte>();
            public long PrivateOffset;
            public int Width, Height, DisplayWidth, DisplayHeight, Channels = 1, BitDepth;
            public double Rate = 8000;
            public Mp4Track? T;
        }

        private ulong _timecodeScale = 1_000_000;
        private double _duration;
        private readonly List<Track> _tracks = new();
        private readonly Dictionary<Mp4Track, Track> _of = new();

        public static bool Sniff(ReadOnlySpan<byte> head) => head.Length >= 4 && head[0] == 0x1A && head[1] == 0x45 && head[2] == 0xDF && head[3] == 0xA3;

        public MatroskaContainer(IByteSource src) : base(src)
        {
            long at = 0, end = src.Length;
            // EBML header, then the Segment
            while (at < end)
            {
                if (!Element(at, end, out ulong id, out long data, out long size)) break;
                if (id == 0x18538067) { Segment(data, Math.Min(end, data + size)); break; }
                at = data + size;
            }
            foreach (var t in _tracks.Where(t => t.T != null)) Tracks.Add(t.T!);
        }

        // ---- EBML -----------------------------------------------------------------------------------------

        private bool Vint(long at, bool keepMarker, out ulong value, out int length)
        {
            value = 0; length = 0;
            var b = ReadAt(at, 1);
            if (b.Length == 0 || b[0] == 0) return false;
            int l = 1;
            while ((b[0] & (0x80 >> (l - 1))) == 0) l++;
            var all = ReadAt(at, l);
            if (all.Length < l) return false;
            value = keepMarker ? all[0] : (ulong)(all[0] & ((0x80 >> (l - 1)) - 1));
            for (int i = 1; i < l; i++) value = value << 8 | all[i];
            length = l;
            return true;
        }

        /// <summary>An element: its id, where its data starts and its size (an unknown size: to the end).</summary>
        private bool Element(long at, long end, out ulong id, out long data, out long size)
        {
            data = size = 0;
            if (!Vint(at, true, out id, out int il)) return false;
            if (!Vint(at + il, false, out ulong s, out int sl)) return false;
            data = at + il + sl;
            size = s == (1UL << (7 * sl)) - 1 ? end - data : (long)s;
            return true;
        }

        private ulong UInt(long at, long size) { ulong v = 0; foreach (var b in ReadAt(at, (int)size)) v = v << 8 | b; return v; }
        private double Float(long at, long size) { var b = ReadAt(at, (int)size); return size == 4 ? BinaryPrimitives.ReadSingleBigEndian(b) : BinaryPrimitives.ReadDoubleBigEndian(b); }

        private void Segment(long at, long end)
        {
            while (at < end)
            {
                if (!Element(at, end, out ulong id, out long data, out long size)) break;
                long e = Math.Min(end, data + size);
                switch (id)
                {
                    case 0x1549A966: Info(data, e); break;
                    case 0x1654AE6B: TracksOf(data, e); break;
                    case 0x1F43B675: Cluster(data, e); break;
                }
                at = e;
            }
        }

        private void Info(long at, long end)
        {
            while (at < end)
            {
                if (!Element(at, end, out ulong id, out long data, out long size)) break;
                if (id == 0x2AD7B1) _timecodeScale = UInt(data, size);
                else if (id == 0x4489) _duration = Float(data, size);
                at = data + size;
            }
        }

        private void TracksOf(long at, long end)
        {
            while (at < end)
            {
                if (!Element(at, end, out ulong id, out long data, out long size)) break;
                if (id == 0xAE) TrackEntry(data, data + size);
                at = data + size;
            }
        }

        private void TrackEntry(long at, long end)
        {
            var t = new Track();
            while (at < end)
            {
                if (!Element(at, end, out ulong id, out long data, out long size)) break;
                switch (id)
                {
                    case 0xD7: t.Number = UInt(data, size); break;
                    case 0x83: t.Type = UInt(data, size); break;
                    case 0x86: t.CodecId = Encoding.ASCII.GetString(ReadAt(data, (int)size)).TrimEnd('\0'); break;
                    case 0x63A2: t.CodecPrivate = ReadAt(data, (int)size); t.PrivateOffset = data; break;
                    case 0x23E383: t.DefaultDuration = UInt(data, size); break;
                    case 0x56AA: t.CodecDelay = UInt(data, size); break;
                    case 0xE0:
                        for (long v = data; v < data + size;)
                        {
                            if (!Element(v, data + size, out ulong vid, out long vd, out long vs)) break;
                            if (vid == 0xB0) t.Width = (int)UInt(vd, vs);
                            else if (vid == 0xBA) t.Height = (int)UInt(vd, vs);
                            else if (vid == 0x54B0) t.DisplayWidth = (int)UInt(vd, vs);
                            else if (vid == 0x54BA) t.DisplayHeight = (int)UInt(vd, vs);
                            v = vd + vs;
                        }
                        break;
                    case 0xE1:
                        for (long v = data; v < data + size;)
                        {
                            if (!Element(v, data + size, out ulong aid, out long ad, out long asz)) break;
                            if (aid == 0xB5) t.Rate = Float(ad, asz);
                            else if (aid == 0x9F) t.Channels = (int)UInt(ad, asz);
                            else if (aid == 0x6264) t.BitDepth = (int)UInt(ad, asz);
                            v = ad + asz;
                        }
                        break;
                }
                at = data + size;
            }
            string? format = t.CodecId switch
            {
                "V_VP9" => "vp09", "V_AV1" => "av01", "V_MPEG4/ISO/AVC" => "avc1", "V_MPEGH/ISO/HEVC" => "hvc1",
                "A_OPUS" => "Opus", "A_FLAC" => "fLaC", "A_MPEG/L3" => ".mp3", "A_PCM/INT/LIT" => "sowt", "A_PCM/FLOAT/IEEE" => "fl32",
                _ => t.CodecId.StartsWith("A_AAC") ? "mp4a" : null,
            };
            if (format != null && t.Type is 1 or 2)
            {
                var m = new Mp4Track
                {
                    Id = (uint)t.Number, Handler = t.Type == 1 ? "vide" : "soun", Format = format, Timescale = 10_000_000,
                    Width = t.Width, Height = t.Height, Channels = t.Channels, SampleRate = (int)t.Rate, SampleBits = t.BitDepth,
                    Config = t.CodecPrivate, ObjectType = 0x40,
                };
                if (format == "avc1") AvcC(m, t.CodecPrivate);
                else if (format == "hvc1") HvcC(m, t.CodecPrivate);
                // FLAC's metadata: a sample of its own, the decoder's to read
                if (format == "fLaC" && t.CodecPrivate.Length > 0)
                    m.Samples.Add(new Mp4Sample { Offset = t.PrivateOffset, Size = t.CodecPrivate.Length, Dts = 0, Duration = 0, Sync = true, Fragment = -1 });
                t.T = m;
                _of[m] = t;
            }
            else D3DLog.Once("mf-mkv:" + t.CodecId, $"Media Foundation: the Matroska file's track '{t.CodecId}' is not read");
            _tracks.Add(t);
        }

        private static void AvcC(Mp4Track t, byte[] c)
        {
            if (c.Length < 7) return;
            t.NalLength = (c[4] & 3) + 1;
            int at = 5, n = c[at++] & 0x1F;
            for (int k = 0; k < 2; k++)
            {
                for (int i = 0; i < n && at + 2 <= c.Length; i++) { int l = c[at] << 8 | c[at + 1]; at += 2; if (at + l > c.Length) return; t.ParameterSets.Add(c.AsSpan(at, l).ToArray()); at += l; }
                if (at >= c.Length) return;
                n = c[at++];
            }
        }

        private static void HvcC(Mp4Track t, byte[] c)
        {
            if (c.Length < 23) return;
            t.NalLength = (c[21] & 3) + 1;
            int arrays = c[22], at = 23;
            for (int a = 0; a < arrays && at + 3 <= c.Length; a++)
            {
                int n = c[at + 1] << 8 | c[at + 2];
                at += 3;
                for (int i = 0; i < n && at + 2 <= c.Length; i++) { int l = c[at] << 8 | c[at + 1]; at += 2; if (at + l > c.Length) return; t.ParameterSets.Add(c.AsSpan(at, l).ToArray()); at += l; }
            }
        }

        private void Cluster(long at, long end)
        {
            long timecode = 0;
            while (at < end)
            {
                if (!Element(at, end, out ulong id, out long data, out long size)) break;
                long e = Math.Min(end, data + size);
                if (id == 0xE7) timecode = (long)UInt(data, size);
                else if (id == 0xA3) Block(data, e - data, timecode, simple: true, keyInGroup: false);
                else if (id == 0xA0)
                {
                    // a BlockGroup: its Block, a key frame where it references no other
                    long blockAt = 0, blockSize = 0; bool references = false;
                    for (long g = data; g < e;)
                    {
                        if (!Element(g, e, out ulong gid, out long gd, out long gs)) break;
                        if (gid == 0xA1) { blockAt = gd; blockSize = gs; }
                        else if (gid == 0xFB) references = true;
                        g = gd + gs;
                    }
                    if (blockSize > 0) Block(blockAt, blockSize, timecode, simple: false, keyInGroup: !references);
                }
                at = e;
            }
        }

        private void Block(long at, long size, long clusterTime, bool simple, bool keyInGroup)
        {
            if (!Vint(at, false, out ulong number, out int nl)) return;
            var h = ReadAt(at + nl, 3);
            if (h.Length < 3) return;
            short rel = (short)(h[0] << 8 | h[1]);
            byte flags = h[2];
            var t = _tracks.FirstOrDefault(x => x.Number == number);
            if (t?.T == null) return;
            long hns = (long)((Int128)(clusterTime + rel) * (long)_timecodeScale / 100);
            bool key = simple ? (flags & 0x80) != 0 : keyInGroup;
            uint dur = (uint)(t.DefaultDuration / 100);
            long payload = at + nl + 3, payloadSize = size - nl - 3;
            int lacing = (flags >> 1) & 3;
            // FLAC's first frame after its header: a discontinuity of its own (Fragment -2 marks it)
            int mark = t.T.Format == "fLaC" && t.T.Samples.Count == 1 ? -2 : -1;
            if (lacing == 0) { t.T.Samples.Add(new Mp4Sample { Offset = payload, Size = (int)payloadSize, Dts = hns, Duration = dur, Sync = key, Fragment = mark }); return; }
            // laced: the frames' sizes, then the frames
            var lace = ReadAt(payload, (int)Math.Min(payloadSize, 4096));
            int count = lace[0] + 1, p = 1;
            var sizes = new long[count];
            if (lacing == 2) { for (int i = 0; i < count; i++) sizes[i] = (payloadSize - 1) / count; }
            else if (lacing == 1)
            {
                long sum = 0;
                for (int i = 0; i < count - 1; i++) { long s = 0; while (lace[p] == 255) { s += 255; p++; } s += lace[p++]; sizes[i] = s; sum += s; }
                sizes[count - 1] = payloadSize - p - sum;
            }
            else
            {
                Vint(payload + p, false, out ulong first, out int fl); p += fl;
                sizes[0] = (long)first; long sum = sizes[0];
                for (int i = 1; i < count - 1; i++)
                {
                    Vint(payload + p, false, out ulong raw, out int rl); p += rl;
                    long bias = (1L << (7 * rl - 1)) - 1;
                    sizes[i] = sizes[i - 1] + ((long)raw - bias); sum += sizes[i];
                }
                sizes[count - 1] = payloadSize - p - sum;
            }
            long off = payload + p;
            for (int i = 0; i < count; i++)
            {
                t.T.Samples.Add(new Mp4Sample { Offset = off, Size = (int)sizes[i], Dts = hns + i * dur, Duration = dur, Sync = key, Fragment = -1 });
                off += sizes[i];
            }
        }

        // ---- what the reader asks ---------------------------------------------------------------------------

        public override byte[] Read(Mp4Track t, in Mp4Sample s, out uint[]? nalLengths) => Mp4Demuxer.ToAnnexB(t, ReadAt(s.Offset, s.Size), s.Sync, out nalLengths);

        public override MFMediaType? Native(Mp4Track m)
        {
            var t = _of[m];
            var mt = new MFMediaType();
            var a = mt.Attributes;
            a.Set(MF_MT_MATROSKA_CODEC_ID, MFValue.Of(t.CodecId));
            if (m.Video)
            {
                Guid sub = m.Format switch { "vp09" => MediaFormats.VP90, "av01" => MediaFormats.AV1, "avc1" => MF.MFVideoFormat_H264, _ => MF.MFVideoFormat_HEVC };
                var (w, h) = Mp4Demuxer.PictureSize(m) ?? (t.Width, t.Height);
                a.Set(MF.MF_MT_ALL_SAMPLES_INDEPENDENT, 0u);
                a.Set(SR.MF_MT_AVG_BITRATE, 0u);
                a.Set(MF.MF_MT_COMPRESSED, 1u);
                // the frame rate of twice the default duration: Windows takes it for a field's
                if (t.DefaultDuration > 0) a.Set(MF.MF_MT_FRAME_RATE, MF.Pack((uint)Math.Round(1e10 / (2.0 * t.DefaultDuration)), 10));
                a.Set(MF.MF_MT_FRAME_SIZE, MF.Pack((uint)w, (uint)h));
                a.Set(MF.MF_MT_MAJOR_TYPE, MF.MFMediaType_Video);
                if (m.AnnexB && Mp4Demuxer.ProfileLevel(m) is { } pl) { a.Set(SR.MF_MT_MPEG2_LEVEL, pl.Level); a.Set(SR.MF_MT_MPEG2_PROFILE, pl.Profile); }
                if (m.AnnexB && m.ParameterSets.Count > 0) a.Set(MF.MF_MT_MPEG_SEQUENCE_HEADER, MFValue.Of(Mp4Demuxer.SequenceHeader(m)));
                if (m.Format == "av01" && t.CodecPrivate.Length > 4) a.Set(MF.MF_MT_MPEG_SEQUENCE_HEADER, MFValue.Of(t.CodecPrivate.AsSpan(4).ToArray()));
                uint pw = (uint)(t.DisplayWidth > 0 ? t.DisplayWidth * t.Height : 1), ph = (uint)(t.DisplayHeight > 0 ? t.DisplayHeight * t.Width : 1);
                a.Set(MF.MF_MT_PIXEL_ASPECT_RATIO, pw == ph ? MF.Pack(1, 1) : MF.Pack(pw, ph));
                a.Set(MF.MF_MT_SUBTYPE, sub);
                a.Set(MF.MF_MT_VIDEO_NOMINAL_RANGE, 2u);
                if (m.AnnexB) a.Set(SR.MF_NALU_LENGTH_SET, 1u);
                return mt;
            }
            Guid asub = m.Format switch
            {
                "Opus" => SR.MFAudioFormat_Opus, "fLaC" => SR.MFAudioFormat_FLAC, ".mp3" => SR.MFAudioFormat_MP3,
                "sowt" => SR.MFAudioFormat_PCM, "fl32" => SR.MFAudioFormat_Float, _ => SR.MFAudioFormat_AAC,
            };
            int bits = t.BitDepth > 0 ? t.BitDepth : 16;
            a.Set(SR.MF_MT_AUDIO_AVG_BYTES_PER_SECOND, AvgBytes(m, t));
            a.Set(SR.MF_MT_AUDIO_BITS_PER_SAMPLE, (uint)bits);
            a.Set(MF_MT_AUDIO_FLOAT_SAMPLES_PER_SECOND, new MFValue { Type = MF.VT_R8, D = t.Rate });
            a.Set(SR.MF_MT_AUDIO_NUM_CHANNELS, (uint)t.Channels);
            a.Set(SR.MF_MT_AUDIO_SAMPLES_PER_SECOND, (uint)t.Rate);
            a.Set(MF.MF_MT_COMPRESSED, asub == SR.MFAudioFormat_PCM || asub == SR.MFAudioFormat_Float ? 0u : 1u);
            a.Set(MF.MF_MT_MAJOR_TYPE, SR.MFMediaType_Audio);
            a.Set(MF.MF_MT_SUBTYPE, asub);
            if (asub == SR.MFAudioFormat_AAC)
            {
                var ud = new byte[12 + t.CodecPrivate.Length];
                t.CodecPrivate.CopyTo(ud, 12);
                a.Set(MF.MF_MT_USER_DATA, MFValue.Of(ud));
            }
            else if (asub == SR.MFAudioFormat_Opus && t.CodecPrivate.Length > 0) a.Set(MF.MF_MT_USER_DATA, MFValue.Of(t.CodecPrivate));
            return mt;
        }

        /// <summary>Opus: 16000 (Windows' figure, whatever the stream's); the others: the track's bytes over its time.</summary>
        private uint AvgBytes(Mp4Track m, Track t)
        {
            if (m.Format == "Opus") return 16000;
            if (m.Format is "sowt" or "fl32") return (uint)(t.Rate * t.Channels * Math.Max(1, t.BitDepth / 8));
            long bytes = m.Samples.Sum(s => (long)s.Size), end = m.Samples.Count > 0 ? m.Samples.Max(s => s.Dts + s.Duration) : 0;
            return end > 0 ? (uint)((Int128)bytes * 10_000_000 / end) : 0;
        }

        public override MFValue? Presentation(Guid key)
        {
            if (key == SR.MF_PD_DURATION) return MFValue.Of((ulong)(_duration * _timecodeScale / 100));
            if (key == SR.MF_PD_TOTAL_FILE_SIZE) return MFValue.Of((ulong)Length);
            if (key == SR.MF_PD_MIME_TYPE) return MFValue.Of("video/x-matroska");
            return null;
        }

        public override uint Characteristics => 0x6;

        /// <summary>Matroska's blocks: a key frame a clean point; the discontinuity on the first, and on FLAC's first frame after its header.</summary>
        public override (bool CleanPoint, bool Discontinuity) SampleFlags(in Mp4Sample s, bool first) => (s.Sync, first || s.Fragment == -2);
    }
}
