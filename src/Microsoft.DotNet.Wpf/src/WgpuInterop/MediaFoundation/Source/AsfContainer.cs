// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// ASF (WMV, WMA), as Windows' ASF source hands it out (MFAsfProbe, Asf.windows.txt) -- the container: its streams in the
// order of their numbers, each sample a whole media object (its payloads put together), of its presentation time less the
// preroll, its duration of the Sample Duration payload extension (else none), each payload extension's data an attribute of
// its system's GUID, its object number (9ed713c8-...), a video key frame a clean point, a discontinuity at the first and
// where the object numbers skip. The native types of the stream properties: WMV of its BITMAPINFOHEADER (its codec data
// MF_MT_USER_DATA), WMA of its WAVEFORMATEX (its tail MF_MT_USER_DATA); the duration the play duration less the preroll.
// There is no WMV or WMA decoder off Windows: the compressed samples only.
//

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Wpf.Interop.WebGpu.Direct3D;

namespace Microsoft.Wpf.Interop.WebGpu.MediaFoundation.Source
{
    internal sealed class AsfContainer : MediaContainer
    {
        private static readonly Guid Header = new("75b22630-668e-11cf-a6d9-00aa0062ce6c"), Data = new("75b22636-668e-11cf-a6d9-00aa0062ce6c");
        private static readonly Guid FileProperties = new("8cabdca1-a947-11cf-8ee4-00c00c205365"), StreamProperties = new("b7dc0791-a9b7-11cf-8ee6-00c00c205365");
        private static readonly Guid HeaderExtension = new("5fbf03b5-a92e-11cf-8ee3-00c00c205365"), ExtendedStreamProperties = new("14e6a5cb-c672-4332-8399-a96952065b5a");
        private static readonly Guid AudioMedia = new("f8699e40-5b4d-11cf-a8fd-00805f5c442b"), VideoMedia = new("bc19efc0-5b4d-11cf-a8fd-00805f5c442b");
        private static readonly Guid SampleDurationExtension = new("c6bd9450-867f-4907-83a3-c77921b733ad");
        internal static readonly Guid MFASFSampleExtension_SampleNumber = new("9ed713c8-9b87-4b26-8297-a93b0c5a8acc");
        private static readonly Guid MF_PD_ASF_FILEPROPERTIES_FILE_ID = new("3de649b4-d76d-4e66-9ec9-78120fb4c7e3");
        private static readonly Guid MF_PD_LAST_MODIFIED_TIME = new("6c990d38-bb8e-477a-8598-0d5d96fcd88a");
        public const int MF_E_OUT_OF_RANGE = unchecked((int)0xC00D36E5);

        private sealed class Stream
        {
            public int Number;
            public bool Video;
            public byte[] TypeData = Array.Empty<byte>();
            public uint Bitrate;
            public long FrameTime;
            public readonly List<(Guid Id, int Size)> Extensions = new();
            public Mp4Track Track = null!;
        }

        /// <summary>A media object: its payloads' bytes (offset, length into the file), its extensions' data, its number.</summary>
        private sealed class MediaObject
        {
            public readonly List<(long Offset, int Length)> Parts = new();
            public int Size, Have;
            public uint Number;
            public readonly List<(Guid Id, byte[] Data)> Extensions = new();
            public bool Gap;
            public int Sample;                        // its sample's index in its track
        }

        private static readonly Guid MFSampleExtension_Token = new("8294da66-f328-4805-b551-00deb4c57a61");
        private static readonly MFMediaType s_token = new();
        private readonly List<Stream> _streams = new();
        private readonly List<MediaObject> _objects = new();
        private Guid _fileId;
        private long _playDuration, _preroll;

        public static bool Sniff(ReadOnlySpan<byte> h) => h.Length >= 16 && new Guid(h.Slice(0, 16)) == Header;

        public AsfContainer(IByteSource src) : base(src)
        {
            long headerSize = (long)BinaryPrimitives.ReadUInt64LittleEndian(ReadAt(16, 8));
            var head = ReadAt(0, (int)Math.Min(headerSize, 16 << 20));
            uint count = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(24));
            int packetSize = 0;
            int p = 30;
            for (uint i = 0; i < count && p + 24 <= head.Length; i++)
            {
                var id = new Guid(head.AsSpan(p, 16));
                int size = (int)BinaryPrimitives.ReadUInt64LittleEndian(head.AsSpan(p + 16));
                if (size < 24 || p + size > head.Length) break;
                var o = head.AsSpan(p + 24, size - 24);
                if (id == FileProperties)
                {
                    _fileId = new Guid(o.Slice(0, 16));
                    _playDuration = (long)BinaryPrimitives.ReadUInt64LittleEndian(o.Slice(40));
                    _preroll = (long)BinaryPrimitives.ReadUInt64LittleEndian(o.Slice(56));
                    packetSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(o.Slice(68));
                }
                else if (id == StreamProperties) AddStream(o);
                else if (id == HeaderExtension)
                {
                    int q = 22, end = 22 + (int)BinaryPrimitives.ReadUInt32LittleEndian(o.Slice(18));
                    while (q + 24 <= Math.Min(end, o.Length))
                    {
                        var eid = new Guid(o.Slice(q, 16));
                        int es = (int)BinaryPrimitives.ReadUInt64LittleEndian(o.Slice(q + 16));
                        if (es < 24 || q + es > o.Length) break;
                        if (eid == ExtendedStreamProperties) ExtendStream(o.Slice(q + 24, es - 24));
                        q += es;
                    }
                }
                p += size;
            }
            foreach (var s in _streams.OrderBy(x => x.Number))
            {
                s.Track = new Mp4Track { Id = (uint)s.Number, Handler = s.Video ? "vide" : "soun", Timescale = 10_000_000 };
                Tracks.Add(s.Track);
            }
            ReadData(headerSize, packetSize);
        }

        private void AddStream(ReadOnlySpan<byte> o)
        {
            var type = new Guid(o.Slice(0, 16));
            int typeLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(o.Slice(40));
            int number = BinaryPrimitives.ReadUInt16LittleEndian(o.Slice(48)) & 0x7F;
            if (type != AudioMedia && type != VideoMedia) return;
            _streams.Add(new Stream { Number = number, Video = type == VideoMedia, TypeData = o.Slice(54, Math.Min(typeLen, o.Length - 54)).ToArray() });
        }

        private void ExtendStream(ReadOnlySpan<byte> o)
        {
            int number = BinaryPrimitives.ReadUInt16LittleEndian(o.Slice(48));
            var s = _streams.FirstOrDefault(x => x.Number == number);
            if (s == null) return;
            s.Bitrate = BinaryPrimitives.ReadUInt32LittleEndian(o.Slice(16));
            s.FrameTime = (long)BinaryPrimitives.ReadUInt64LittleEndian(o.Slice(52));
            int names = BinaryPrimitives.ReadUInt16LittleEndian(o.Slice(60)), exts = BinaryPrimitives.ReadUInt16LittleEndian(o.Slice(62));
            int q = 64;
            for (int i = 0; i < names; i++) q += 4 + BinaryPrimitives.ReadUInt16LittleEndian(o.Slice(q + 2));
            for (int i = 0; i < exts && q + 22 <= o.Length; i++)
            {
                s.Extensions.Add((new Guid(o.Slice(q, 16)), BinaryPrimitives.ReadUInt16LittleEndian(o.Slice(q + 16))));
                q += 22 + (int)BinaryPrimitives.ReadUInt32LittleEndian(o.Slice(q + 18));
            }
        }

        private static int Field(ReadOnlySpan<byte> b, ref int at, int type)
        {
            int v = type switch { 1 => b[at], 2 => BinaryPrimitives.ReadUInt16LittleEndian(b.Slice(at)), 3 => (int)BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(at)), _ => 0 };
            at += type == 3 ? 4 : type;
            return v;
        }

        /// <summary>The data object's packets: each payload into its media object, each whole object a sample of its stream.</summary>
        private void ReadData(long at, int packetSize)
        {
            if (packetSize <= 0) return;
            long dataSize = (long)BinaryPrimitives.ReadUInt64LittleEndian(ReadAt(at + 16, 8));
            long first = at + 50, end = Math.Min(Length, at + dataSize);
            var open = new Dictionary<int, MediaObject>();
            var last = new Dictionary<int, uint>();
            for (long pk = first; pk + packetSize <= end; pk += packetSize)
            {
                var b = ReadAt(pk, packetSize);
                int i = 0;
                if ((b[0] & 0x80) != 0) i += 1 + (b[0] & 0x0F);             // error correction data
                byte lengthFlags = b[i++], props = b[i++];
                int packetLength = Field(b, ref i, (lengthFlags >> 5) & 3);
                Field(b, ref i, (lengthFlags >> 1) & 3);                     // sequence
                int padding = Field(b, ref i, (lengthFlags >> 3) & 3);
                i += 6;                                                      // send time, duration
                if (packetLength == 0) packetLength = packetSize;
                int payloadEnd = Math.Min(packetSize, packetLength) - padding;
                int payloads = 1, payloadLenType = 0;
                bool multiple = (lengthFlags & 1) != 0;
                if (multiple) { byte pf = b[i++]; payloads = pf & 0x3F; payloadLenType = pf >> 6; }
                for (int k = 0; k < payloads && i < payloadEnd; k++)
                {
                    byte sn = b[i++];
                    int number = sn & 0x7F; bool key = (sn & 0x80) != 0;
                    uint objectNumber = (uint)Field(b, ref i, (props >> 4) & 3);
                    int offset = Field(b, ref i, (props >> 2) & 3);
                    int repLen = Field(b, ref i, props & 3);
                    int objectSize = 0; long pts = 0;
                    var exts = new List<(Guid, byte[])>();
                    var st = _streams.FirstOrDefault(x => x.Number == number);
                    if (repLen >= 8)
                    {
                        objectSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(i));
                        pts = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(i + 4));
                        int e = i + 8;
                        if (st != null)
                            foreach (var (id, size) in st.Extensions)
                            {
                                int n = size == 0xFFFF ? BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(e)) : size;
                                if (size == 0xFFFF) e += 2;
                                if (e + n > i + repLen) break;
                                exts.Add((id, b.AsSpan(e, n).ToArray()));
                                e += n;
                            }
                    }
                    i += repLen;
                    int len = multiple ? Field(b, ref i, payloadLenType) : payloadEnd - i;
                    if (len < 0 || i + len > b.Length) break;
                    if (st != null && repLen != 1)
                    {
                        if (offset == 0)
                        {
                            var mo = new MediaObject { Size = objectSize, Number = objectNumber, Sample = st.Track.Samples.Count };
                            mo.Extensions.AddRange(exts);
                            mo.Gap = last.TryGetValue(number, out var prev) && objectNumber != ((prev + 1) & 0xFF) && objectNumber != prev + 1;
                            last[number] = objectNumber;
                            open[number] = mo;
                            var s = new Mp4Sample
                            {
                                Offset = _objects.Count, Size = objectSize, Dts = (pts - _preroll) * 10_000,
                                Duration = exts.FirstOrDefault(x => x.Item1 == SampleDurationExtension).Item2 is { Length: >= 2 } d ? (uint)(BinaryPrimitives.ReadUInt16LittleEndian(d) * 10_000) : uint.MaxValue,
                                Sync = st.Video && key, Fragment = -1,
                            };
                            _objects.Add(mo);
                            st.Track.Samples.Add(s);
                        }
                        if (open.TryGetValue(number, out var cur) && cur.Number == objectNumber)
                        {
                            cur.Parts.Add((pk + i, len)); cur.Have += len;
                            // (each fragment's replicated data its own: Windows' takes the last's extensions)
                            if (offset > 0 && repLen >= 8)
                            {
                                cur.Extensions.Clear(); cur.Extensions.AddRange(exts);
                                var smp = st.Track.Samples[cur.Sample];
                                smp.Duration = exts.FirstOrDefault(x => x.Item1 == SampleDurationExtension).Item2 is { Length: >= 2 } d2 ? (uint)(BinaryPrimitives.ReadUInt16LittleEndian(d2) * 10_000) : uint.MaxValue;
                                st.Track.Samples[cur.Sample] = smp;
                            }
                        }
                    }
                    i += len;
                }
            }
        }

        public override byte[] Read(Mp4Track t, in Mp4Sample s, out uint[]? nalLengths)
        {
            nalLengths = null;
            var o = _objects[(int)s.Offset];
            var r = new byte[o.Parts.Sum(x => x.Length)];
            int at = 0;
            foreach (var (off, len) in o.Parts) { ReadAt(off, len).CopyTo(r, at); at += len; }
            return r;
        }

        /// <summary>The payload extensions' data and the object's number, as attributes of the sample.</summary>
        public override void Extra(Mp4Track t, in Mp4Sample s, MFSample sample)
        {
            var o = _objects[(int)s.Offset];
            foreach (var (id, data) in o.Extensions) sample.Attributes.Set(id, MFValue.Of(data));
            sample.Attributes.Set(MFASFSampleExtension_SampleNumber, MFValue.Of(o.Number));
            // (Windows' ASF source gives each sample a token, MFSampleExtension_Token, as its RequestSample's)
            Com.AddRef(s_token.Pointer);                                 // (the attribute's reference)
            sample.Attributes.Set(MFSampleExtension_Token, new MFValue { Type = MF.VT_UNKNOWN, Unknown = s_token.Pointer });
        }

        public override (bool CleanPoint, bool Discontinuity) SampleFlags(in Mp4Sample s, bool first) => (s.Sync, first || _objects[(int)s.Offset].Gap);

        public override IEnumerable<Mp4Track> SourceOrder => Tracks.Where(t => t.Samples.Count > 0);

        public override int SeekCheck(long hns) => hns > _playDuration - _preroll * 10_000 ? MF_E_OUT_OF_RANGE : MF.S_OK;

        public override MFMediaType? Native(Mp4Track m)
        {
            var s = _streams.First(x => x.Track == m);
            var d = s.TypeData;
            if (s.Video)
            {
                if (d.Length < 11 + 40) return null;
                var bih = d.AsSpan(11);
                int w = BinaryPrimitives.ReadInt32LittleEndian(bih.Slice(4)), h = Math.Abs(BinaryPrimitives.ReadInt32LittleEndian(bih.Slice(8)));
                uint fourcc = BinaryPrimitives.ReadUInt32LittleEndian(bih.Slice(16));
                int biSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(bih);
                var t = new MFMediaType();
                var a = t.Attributes;
                a.Set(MF.MF_MT_MAJOR_TYPE, MF.MFMediaType_Video);
                a.Set(MF.MF_MT_SUBTYPE, new Guid(fourcc, 0, 0x10, 0x80, 0, 0, 0xaa, 0, 0x38, 0x9b, 0x71));
                a.Set(MF.MF_MT_FRAME_SIZE, MF.Pack((uint)w, (uint)h));
                if (s.FrameTime > 0) { uint n, dd; unsafe { MFExports.MFAverageTimePerFrameToFrameRate((ulong)s.FrameTime, &n, &dd); } a.Set(MF.MF_MT_FRAME_RATE, MF.Pack(n, dd)); }
                if (s.Bitrate > 0) a.Set(SR.MF_MT_AVG_BITRATE, s.Bitrate);
                a.Set(MF.MF_MT_INTERLACE_MODE, 2u);
                a.Set(MF.MF_MT_PIXEL_ASPECT_RATIO, MF.Pack(1, 1));
                if (biSize > 40 && bih.Length >= biSize) a.Set(MF.MF_MT_USER_DATA, MFValue.Of(bih.Slice(40, biSize - 40).ToArray()));
                a.Set(new Guid("c380465d-2271-428c-9b83-ecea3b4a85c1"), 0u);                // MF_MT_VIDEO_ROTATION
                return t;
            }
            if (d.Length < 18) return null;
            ushort tag = BinaryPrimitives.ReadUInt16LittleEndian(d), ch = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(2));
            uint rate = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(4)), bytes = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(8));
            ushort align = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(12)), bits = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(14)), cb = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(16));
            var at2 = Audio(new Guid(tag, 0, 0x10, 0x80, 0, 0, 0xaa, 0, 0x38, 0x9b, 0x71));
            var aa = at2.Attributes;
            aa.Set(SR.MF_MT_AUDIO_AVG_BYTES_PER_SECOND, bytes);
            aa.Set(SR.MF_MT_AUDIO_BITS_PER_SAMPLE, (uint)bits);
            aa.Set(SR.MF_MT_AUDIO_BLOCK_ALIGNMENT, (uint)align);
            aa.Set(SR.MF_MT_AUDIO_NUM_CHANNELS, (uint)ch);
            aa.Set(SR.MF_MT_AUDIO_PREFER_WAVEFORMATEX, 1u);
            aa.Set(SR.MF_MT_AUDIO_SAMPLES_PER_SECOND, rate);
            if (cb > 0 && d.Length >= 18 + cb) aa.Set(MF.MF_MT_USER_DATA, MFValue.Of(d.AsSpan(18, cb).ToArray()));
            return at2;
        }

        public override MFValue? Presentation(Guid key)
        {
            if (key == SR.MF_PD_DURATION) return MFValue.Of((ulong)Math.Max(0, _playDuration - _preroll * 10_000));
            if (key == MF_PD_ASF_FILEPROPERTIES_FILE_ID) return MFValue.Of(_fileId);
            if (key == MF_PD_LAST_MODIFIED_TIME) return LastWrite();
            return null;
        }
    }
}
