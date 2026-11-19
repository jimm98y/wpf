// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The files the source reader reads (Documentation/media-foundation-shim.md, "The source reader"): one
// container each, as Windows' source of it hands its samples out (probed: MFSourceReaderProbe's report).
//
//   - MPEG-4 (Mp4Demuxer.cs): its samples one by one;
//   - WAV: PCM in tenths of a second, each a clean point;
//   - MP3: whole frames up to 8 KB a sample, the first a clean point; the duration the file's size over the
//     first frame's bitrate;
//   - ADTS AAC and FLAC: the file's bytes as they come (64 KB a sample), the decoder finding the frames.
//

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.Wpf.Interop.WebGpu.MediaFoundation.Codecs;

namespace Microsoft.Wpf.Interop.WebGpu.MediaFoundation.Source
{
    internal abstract class MediaContainer : IDisposable
    {
        protected readonly IByteSource Src;
        public readonly List<Mp4Track> Tracks = new();
        private readonly object _lock = new();

        protected MediaContainer(IByteSource src) { Src = src; }

        public long Length => Src.Length;
        public void Dispose() => Src.Dispose();

        /// <summary>The container of the bytes, or null where none reads them.</summary>
        public static MediaContainer? Open(IByteSource src)
        {
            Span<byte> h = stackalloc byte[12];
            int n = src.Read(0, h);
            if (n >= 12 && h.Slice(0, 4).SequenceEqual("RIFF"u8) && h.Slice(8, 4).SequenceEqual("WAVE"u8)) return new WavContainer(src);
            if (n >= 4 && h.Slice(0, 4).SequenceEqual("fLaC"u8)) return new FlacContainer(src);
            if (MatroskaContainer.Sniff(h)) return new MatroskaContainer(src);
            if (Mp4Demuxer.Sniff(src)) return Mp4Demuxer.Open(src);
            if (n >= 3 && h.Slice(0, 3).SequenceEqual("ID3"u8) || n >= 2 && h[0] == 0xFF && (h[1] & 0xE0) == 0xE0)
            {
                // ADTS's sync is 12 bits of ones and layer 00; an MPEG audio frame's 11 bits and a layer
                if (n >= 2 && h[0] == 0xFF && (h[1] & 0xF6) == 0xF0) return new AdtsContainer(src);
                return Mp3Container.Create(src);
            }
            return null;
        }

        public byte[] ReadAt(long offset, int n)
        {
            var b = new byte[n];
            int got;
            lock (_lock) got = Src.Read(offset, b);
            if (got < n) Array.Resize(ref b, got);
            return b;
        }

        /// <summary>A track's native type.</summary>
        public abstract MFMediaType? Native(Mp4Track t);

        /// <summary>A sample's bytes, as the source hands them out.</summary>
        public virtual byte[] Read(Mp4Track t, in Mp4Sample s, out uint[]? nalLengths) { nalLengths = null; return ReadAt(s.Offset, s.Size); }

        /// <summary>The presentation's attributes (MF_PD_*); null where the source has none of the key.</summary>
        public abstract MFValue? Presentation(Guid key);

        /// <summary>MF_SOURCE_READER_MEDIASOURCE_CHARACTERISTICS: CAN_SEEK | CAN_PAUSE | DOES_NOT_USE_NETWORK.</summary>
        public virtual uint Characteristics => 0x86;

        /// <summary>What a sample carries: a clean point, a discontinuity (the first, and the first after a seek).</summary>
        public virtual (bool CleanPoint, bool Discontinuity) SampleFlags(in Mp4Sample s, bool first) => (s.Sync, first);

        protected static MFMediaType Audio(Guid subtype)
        {
            var t = new MFMediaType();
            t.Attributes.Set(MF.MF_MT_MAJOR_TYPE, SR.MFMediaType_Audio);
            t.Attributes.Set(MF.MF_MT_SUBTYPE, subtype);
            return t;
        }

        protected Mp4Track AudioTrack(string format, int rate, int channels, int bits)
        {
            var t = new Mp4Track { Id = 1, Handler = "soun", Format = format, Timescale = (uint)rate, SampleRate = rate, Channels = channels, SampleBits = bits };
            Tracks.Add(t);
            return t;
        }

        protected static long Hns(long v, long rate) => (long)((Int128)v * 10_000_000 / rate);
    }

    // ---- WAV --------------------------------------------------------------------------------------------

    internal sealed class WavContainer : MediaContainer
    {
        private readonly Mp4Track _t;
        private readonly int _block, _rate, _channels, _bits;
        private readonly ushort _tag;
        private readonly long _data, _dataLength;
        private readonly uint _mask;
        private readonly Guid _sub;

        public WavContainer(IByteSource src) : base(src)
        {
            long at = 12;
            byte[]? fmt = null;
            while (at + 8 <= src.Length)
            {
                var h = ReadAt(at, 8);
                string id = Encoding.ASCII.GetString(h, 0, 4);
                long size = BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(4));
                if (id == "fmt ") fmt = ReadAt(at + 8, (int)Math.Min(size, 64));
                else if (id == "data") { _data = at + 8; _dataLength = Math.Min(size, src.Length - _data); break; }
                at += 8 + size + (size & 1);
            }
            if (fmt == null || fmt.Length < 16 || _data == 0) throw new System.IO.InvalidDataException("no fmt or data");
            _tag = BinaryPrimitives.ReadUInt16LittleEndian(fmt);
            _channels = BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(2));
            _rate = (int)BinaryPrimitives.ReadUInt32LittleEndian(fmt.AsSpan(4));
            _block = BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(12));
            _bits = BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(14));
            if (_tag == 0xFFFE && fmt.Length >= 40)
            {
                // WAVE_FORMAT_EXTENSIBLE: the mask and the subformat's tag
                _mask = BinaryPrimitives.ReadUInt32LittleEndian(fmt.AsSpan(20));
                _tag = BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(24));
            }
            _sub = MediaFormats.Wave(_tag);
            _t = AudioTrack(_tag == 3 ? "fl32" : "sowt", _rate, _channels, _bits);
            // tenths of a second
            int perSample = Math.Max(1, _rate / 10) * _block;
            for (long o = 0; o < _dataLength; o += perSample)
            {
                int size = (int)Math.Min(perSample, _dataLength - o);
                _t.Samples.Add(new Mp4Sample { Offset = _data + o, Size = size, Dts = o / _block, Duration = (uint)(size / _block), Sync = true, Fragment = -1 });
            }
        }

        public override MFMediaType? Native(Mp4Track t)
        {
            var mt = Audio(_sub);
            var a = mt.Attributes;
            a.Set(MF.MF_MT_ALL_SAMPLES_INDEPENDENT, 1u);
            a.Set(SR.MF_MT_AUDIO_AVG_BYTES_PER_SECOND, (uint)(_rate * _block));
            a.Set(SR.MF_MT_AUDIO_BITS_PER_SAMPLE, (uint)_bits);
            a.Set(SR.MF_MT_AUDIO_BLOCK_ALIGNMENT, (uint)_block);
            a.Set(SR.MF_MT_AUDIO_NUM_CHANNELS, (uint)_channels);
            if (_mask != 0) a.Set(new Guid("55fb5765-644a-4caf-8479-938983bb1588"), _mask);
            a.Set(SR.MF_MT_AUDIO_PREFER_WAVEFORMATEX, 1u);
            a.Set(SR.MF_MT_AUDIO_SAMPLES_PER_SECOND, (uint)_rate);
            return mt;
        }

        public override MFValue? Presentation(Guid key)
        {
            if (key == SR.MF_PD_DURATION) return MFValue.Of((ulong)Hns(_dataLength / Math.Max(1, _block), _rate));
            if (key == SR.MF_PD_TOTAL_FILE_SIZE) return MFValue.Of((ulong)Length);
            if (key == SR.MF_PD_MIME_TYPE) return MFValue.Of("audio/wav");
            if (key == SR.MF_PD_AUDIO_ENCODING_BITRATE) return MFValue.Of((uint)(_rate * _block * 8));
            return null;
        }

        public override (bool CleanPoint, bool Discontinuity) SampleFlags(in Mp4Sample s, bool first) => (true, false);
    }

    // ---- MP3 --------------------------------------------------------------------------------------------

    internal sealed class Mp3Container : MediaContainer
    {
        private int _rate, _channels, _bitrate, _firstSize;
        private long _audioEnd;

        private Mp3Container(IByteSource src) : base(src) { }

        private static readonly int[] s_bitrate1 = { 0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320 };
        private static readonly int[] s_bitrate2 = { 0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160 };

        /// <summary>An MPEG audio layer III frame's header: its length, rate, channels, bitrate and samples.</summary>
        internal static bool Header(ReadOnlySpan<byte> h, out int length, out int rate, out int channels, out int bitrate, out int samples)
        {
            length = rate = channels = bitrate = samples = 0;
            if (h.Length < 4 || h[0] != 0xFF || (h[1] & 0xE0) != 0xE0) return false;
            int version = (h[1] >> 3) & 3, layer = (h[1] >> 1) & 3, bi = h[2] >> 4, ri = (h[2] >> 2) & 3, pad = (h[2] >> 1) & 1;
            if (version == 1 || layer != 1 || bi is 0 or 15 || ri == 3) return false;           // layer III only
            int[] rates = version == 3 ? new[] { 44100, 48000, 32000 } : version == 2 ? new[] { 22050, 24000, 16000 } : new[] { 11025, 12000, 8000 };
            rate = rates[ri];
            bitrate = (version == 3 ? s_bitrate1 : s_bitrate2)[bi] * 1000;
            samples = version == 3 ? 1152 : 576;
            length = (version == 3 ? 144 : 72) * bitrate / rate + pad;
            channels = (h[3] >> 6) == 3 ? 1 : 2;
            return true;
        }

        public static Mp3Container? Create(IByteSource src)
        {
            var c = new Mp3Container(src);
            long at = 0;
            var head = c.ReadAt(0, 10);
            if (head.Length == 10 && head.AsSpan(0, 3).SequenceEqual("ID3"u8))
                at = 10 + (head[6] << 21 | head[7] << 14 | head[8] << 7 | head[9]) + ((head[5] & 0x10) != 0 ? 10 : 0);
            long end = src.Length;
            var tag = end >= 128 ? c.ReadAt(end - 128, 3) : Array.Empty<byte>();
            if (tag.AsSpan().SequenceEqual("TAG"u8)) end -= 128;
            c._audioEnd = end;
            // the frames: whole frames to 8 KB a sample, each sample's frames counted
            Mp4Track? t = null;
            long sampleAt = at, frames = 0, dts = 0;
            int sampleSize = 0, frameSamples = 1152;
            while (at + 4 <= end)
            {
                var h = c.ReadAt(at, 4);
                if (!Header(h, out int len, out int rate, out int ch, out int br, out int ns) || at + len > end) break;
                if (t == null)
                {
                    t = c.AudioTrack(".mp3", rate, ch, 16); c._rate = rate; c._channels = ch; c._bitrate = br; c._firstSize = len;
                    // times in 100 ns units: each frame's duration rounded down, summed (as Windows' MP3 source counts them)
                    t.Timescale = 10_000_000;
                    frameSamples = (int)((long)ns * 10_000_000 / rate);
                }
                if (sampleSize + len > 8192 && sampleSize > 0)
                {
                    t.Samples.Add(new Mp4Sample { Offset = sampleAt, Size = sampleSize, Dts = dts, Duration = (uint)(frames * frameSamples), Sync = t.Samples.Count == 0, Fragment = -1 });
                    dts += frames * frameSamples; sampleAt = at; sampleSize = 0; frames = 0;
                }
                sampleSize += len; frames++; at += len;
            }
            if (t == null) { c.Dispose(); return null; }
            if (sampleSize > 0) t.Samples.Add(new Mp4Sample { Offset = sampleAt, Size = sampleSize, Dts = dts, Duration = (uint)(frames * frameSamples), Sync = t.Samples.Count == 0, Fragment = -1 });
            return c;
        }

        public override MFMediaType? Native(Mp4Track t)
        {
            var mt = Audio(SR.MFAudioFormat_MP3);
            var a = mt.Attributes;
            a.Set(SR.MF_MT_AUDIO_AVG_BYTES_PER_SECOND, (uint)(_bitrate / 8));
            a.Set(SR.MF_MT_AUDIO_BLOCK_ALIGNMENT, 1u);
            a.Set(SR.MF_MT_AUDIO_NUM_CHANNELS, (uint)_channels);
            a.Set(SR.MF_MT_AUDIO_PREFER_WAVEFORMATEX, 1u);
            a.Set(SR.MF_MT_AUDIO_SAMPLES_PER_SECOND, (uint)_rate);
            // MPEGLAYER3WAVEFORMAT's tail: wID 1 (MPEGLAYER3_ID_MPEG), fdwFlags 0, nBlockSize the first frame's, nFramesPerBlock 0, nCodecDelay 0
            var ud = new byte[12];
            ud[0] = 1;
            BinaryPrimitives.WriteUInt16LittleEndian(ud.AsSpan(6), (ushort)_firstSize);
            a.Set(MF.MF_MT_USER_DATA, MFValue.Of(ud));
            return mt;
        }

        public override MFValue? Presentation(Guid key)
        {
            // CBR: the file's size over the first frame's bitrate, as Windows estimates it
            if (key == SR.MF_PD_DURATION) return MFValue.Of((ulong)((Int128)Length * 8 * 10_000_000 / Math.Max(1, _bitrate)));
            if (key == SR.MF_PD_TOTAL_FILE_SIZE) return MFValue.Of((ulong)Length);
            if (key == SR.MF_PD_MIME_TYPE) return MFValue.Of("audio/mpeg");
            if (key == SR.MF_PD_AUDIO_ENCODING_BITRATE) return MFValue.Of((uint)_bitrate);
            return null;
        }

        public override (bool CleanPoint, bool Discontinuity) SampleFlags(in Mp4Sample s, bool first) => (s.Sync, false);
    }

    // ---- ADTS and FLAC: the bytes as they come -----------------------------------------------------------

    /// <summary>A file the decoder finds the frames of: its bytes in samples of 64 KB.</summary>
    internal abstract class RawContainer : MediaContainer
    {
        protected RawContainer(IByteSource src) : base(src) { }

        protected void Chunks(Mp4Track t, long from, Func<long, long> framesBefore, bool durations)
        {
            for (long o = from; o < Length; o += 65536)
            {
                int size = (int)Math.Min(65536, Length - o);
                long f0 = framesBefore(o), f1 = framesBefore(o + size);
                t.Samples.Add(new Mp4Sample { Offset = o, Size = size, Dts = f0, Duration = durations ? (uint)(f1 - f0) : uint.MaxValue, Sync = false, Fragment = -1 });
            }
        }
    }

    internal sealed class AdtsContainer : RawContainer
    {
        private readonly int _rate, _channels, _profile, _frames;
        private readonly long _bytes;
        private readonly byte[] _asc;
        private static readonly int[] s_rates = { 96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050, 16000, 12000, 11025, 8000, 7350 };

        public AdtsContainer(IByteSource src) : base(src)
        {
            var h = ReadAt(0, 7);
            _profile = h[2] >> 6;                                     // object type - 1
            int ri = (h[2] >> 2) & 0xF;
            _channels = ((h[2] & 1) << 2) | (h[3] >> 6);
            _rate = ri < s_rates.Length ? s_rates[ri] : 44100;
            _asc = new[] { (byte)((_profile + 1) << 3 | ri >> 1), (byte)((ri & 1) << 7 | _channels << 3) };
            // the frames' starts, for the samples' times
            var starts = new List<long>();
            long at = 0;
            while (at + 7 <= src.Length)
            {
                var f = ReadAt(at, 7);
                if (f[0] != 0xFF || (f[1] & 0xF6) != 0xF0) break;
                int len = ((f[3] & 3) << 11) | (f[4] << 3) | (f[5] >> 5);
                if (len < 7) break;
                starts.Add(at);
                at += len;
            }
            _frames = starts.Count;
            _bytes = at;
            var t = AudioTrack("adts", _rate, _channels, 16);
            // times in 100 ns units: each frame's duration rounded down, summed (as Windows' ADTS source counts them)
            t.Timescale = 10_000_000;
            long perFrame = 1024L * 10_000_000 / _rate;
            Chunks(t, 0, o => { int i = starts.BinarySearch(o); return (i >= 0 ? i : ~i) * perFrame; }, true);
        }

        private uint Bitrate => _frames == 0 ? 0 : (uint)((Int128)_bytes * 8 * _rate / (_frames * 1024L));

        public override MFMediaType? Native(Mp4Track t)
        {
            var mt = Audio(SR.MFAudioFormat_AAC);
            var a = mt.Attributes;
            a.Set(SR.MF_MT_AAC_AUDIO_PROFILE_LEVEL_INDICATION, 0xFEu);       // no profile and level given
            a.Set(SR.MF_MT_AAC_PAYLOAD_TYPE, 1u);                            // ADTS
            a.Set(SR.MF_MT_AUDIO_AVG_BYTES_PER_SECOND, Bitrate / 8);
            a.Set(SR.MF_MT_AUDIO_BLOCK_ALIGNMENT, 1u);
            a.Set(SR.MF_MT_AUDIO_NUM_CHANNELS, (uint)_channels);
            a.Set(SR.MF_MT_AUDIO_SAMPLES_PER_SECOND, (uint)_rate);
            // HEAACWAVEINFO's tail: payload type 1, profile 0xFE, then the AudioSpecificConfig
            var ud = new byte[12 + _asc.Length];
            ud[0] = 1; ud[2] = 0xFE;
            _asc.CopyTo(ud, 12);
            a.Set(MF.MF_MT_USER_DATA, MFValue.Of(ud));
            return mt;
        }

        public override MFValue? Presentation(Guid key)
        {
            if (key == SR.MF_PD_DURATION) return MFValue.Of((ulong)Hns(_frames * 1024L, _rate));
            if (key == SR.MF_PD_TOTAL_FILE_SIZE) return MFValue.Of((ulong)Length);
            if (key == SR.MF_PD_MIME_TYPE) return MFValue.Of("audio/vnd.dlna.adts");
            if (key == SR.MF_PD_AUDIO_ENCODING_BITRATE) return MFValue.Of(Bitrate);
            return null;
        }

        public override (bool CleanPoint, bool Discontinuity) SampleFlags(in Mp4Sample s, bool first) => (false, false);
    }

    internal sealed class FlacContainer : RawContainer
    {
        private readonly int _rate, _channels, _bits, _maxBlock;
        private readonly byte[] _metadata;
        public static readonly Guid MF_MT_AUDIO_FLAC_MAX_BLOCK_SIZE = new("8b81adae-4b5a-4d40-8022-f38d09ca3c5c");
        /// <summary>The stream's metadata blocks, as Windows' FLAC source gives them (an attribute of no published name).</summary>
        public static readonly Guid FlacMetadataAttribute = new("3eadb782-73df-4c6c-bf3a-e31977954435");

        public FlacContainer(IByteSource src) : base(src)
        {
            long at = 4;
            var meta = new List<byte>();
            while (at + 4 <= src.Length)
            {
                var h = ReadAt(at, 4);
                int len = h[1] << 16 | h[2] << 8 | h[3];
                meta.AddRange(ReadAt(at, 4 + len));
                at += 4 + len;
                if ((h[0] & 0x80) != 0) break;                         // the last block
            }
            _metadata = meta.ToArray();
            // STREAMINFO: min/max block size, min/max frame size, then rate 20 bits, channels - 1, bits - 1
            var si = _metadata.AsSpan(4);
            _maxBlock = si[2] << 8 | si[3];
            _rate = si[10] << 12 | si[11] << 4 | si[12] >> 4;
            _channels = ((si[12] >> 1) & 7) + 1;
            _bits = ((si[12] & 1) << 4 | si[13] >> 4) + 1;
            var t = AudioTrack("fLaC", _rate, _channels, _bits);
            t.Config = _metadata;
            Chunks(t, 0, _ => 0, false);
        }

        public override MFMediaType? Native(Mp4Track t)
        {
            var mt = Audio(SR.MFAudioFormat_FLAC);
            var a = mt.Attributes;
            a.Set(FlacMetadataAttribute, MFValue.Of(_metadata));
            a.Set(MF_MT_AUDIO_FLAC_MAX_BLOCK_SIZE, (uint)_maxBlock);
            a.Set(SR.MF_MT_AUDIO_AVG_BYTES_PER_SECOND, 0u);
            a.Set(SR.MF_MT_AUDIO_BITS_PER_SAMPLE, (uint)_bits);
            a.Set(SR.MF_MT_AUDIO_BLOCK_ALIGNMENT, (uint)(_channels * ((_bits + 7) / 8)));
            a.Set(SR.MF_MT_AUDIO_NUM_CHANNELS, (uint)_channels);
            a.Set(SR.MF_MT_AUDIO_SAMPLES_PER_SECOND, (uint)_rate);
            return mt;
        }

        // the duration unknown (0), no size: as Windows' FLAC source has it
        public override MFValue? Presentation(Guid key)
        {
            if (key == SR.MF_PD_DURATION) return MFValue.Of(0UL);
            if (key == SR.MF_PD_MIME_TYPE) return MFValue.Of("audio/flac");
            return null;
        }

        public override uint Characteristics => 0x6;

        public override (bool CleanPoint, bool Discontinuity) SampleFlags(in Mp4Sample s, bool first) => (false, first);
    }
}
