// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// What a stream of the source reader reads its compressed samples from (Documentation/media-foundation-shim.md,
// "The source reader", "Media sources"): a track of a file the shim reads itself (ContainerFeed), or a
// stream of a media source of the application's (ForeignSource.cs). Each sample as the media source of
// the file hands it out on Windows (MFMediaSource.cs gives the same samples to its own callers).
//

using System;
using System.Collections.Generic;
using System.Linq;

namespace Microsoft.Wpf.Interop.WebGpu.MediaFoundation.Source
{
    internal abstract class SampleFeed : IDisposable
    {
        public abstract bool Audio { get; }
        public abstract bool Video { get; }
        /// <summary>The time of the next sample; long.MaxValue at the end.</summary>
        public abstract long NextTime { get; }
        /// <summary>No sample left.</summary>
        public abstract bool AtEnd { get; }
        /// <summary>The next sample (the caller's reference), or null at the end.</summary>
        public abstract MFSample? Take(bool first);
        /// <summary>To the sync sample at or before the time.</summary>
        public abstract void Seek(long hns);
        /// <summary>Back to the sync sample at or before the one next (a decoder starting).</summary>
        public virtual void BackToSync() { }
        /// <summary>A stream tick the source sent before its next sample (MEStreamTick: a gap, its time), taken.</summary>
        public virtual bool TakeTick(out long time) { time = 0; return false; }
        /// <summary>The type the source changed to before the sample last taken (MEStreamFormatChanged), taken; null of none.</summary>
        public virtual MFMediaType? TakeFormat() => null;
        public virtual void Dispose() { }
    }

    /// <summary>A position in a track of a file: the sample next.</summary>
    internal sealed class TrackCursor
    {
        private readonly MediaContainer _c;
        public readonly Mp4Track Track;
        public int Next;

        public TrackCursor(MediaContainer c, Mp4Track t) { _c = c; Track = t; }

        private int _prelude = -1;             // a sample handed out first after a seek (Matroska's FLAC header)

        public bool AtEnd => _prelude < 0 && Next >= Track.Samples.Count;
        public int Remaining => Math.Max(0, Track.Samples.Count - Next) + (_prelude >= 0 ? 1 : 0);
        public long NextTime => _prelude >= 0 ? Track.Pts(Track.Samples[_prelude]) : Next < Track.Samples.Count ? Track.Pts(Track.Samples[Next]) : long.MaxValue;

        /// <summary>The next sample's index (the caller has checked AtEnd).</summary>
        public int Take()
        {
            if (_prelude >= 0) { int p = _prelude; _prelude = -1; return p; }
            return Next++;
        }

        /// <summary>To the sample the container's source goes on from at the position.</summary>
        public void Seek(long hns)
        {
            Next = _c.SeekIndex(Track, hns);
            _prelude = _c.SeekPrelude(Track) is int p && p < Next ? p : -1;
        }

        public void BackToSync()
        {
            bool anySync = Track.Samples.Exists(x => x.Sync);
            while (anySync && Next > 0 && !Track.Samples[Math.Min(Next, Track.Samples.Count - 1)].Sync) Next--;
        }
    }

    /// <summary>The samples of a file's track, as Windows' source of the file hands them out.</summary>
    internal static unsafe class ContainerSamples
    {
        public static readonly Guid MFSampleExtension_DecodeTimestamp = new("73a954d4-09e2-4861-befc-94bd97c08e6e");

        /// <summary>
        /// The sample: its bytes (Annex B where the source converts), times, NAL lengths, clean point and discontinuity.
        /// With <paramref name="perNal"/>, in buffers as the MPEG-4 source makes them of Annex B: the parameter sets in
        /// one, then each NAL unit's start code and the rest of it in one each.
        /// </summary>
        public static MFSample Make(MediaContainer c, Mp4Track t, in Mp4Sample m, bool first, bool perNal = false)
        {
            var bytes = c.Read(t, m, out var lengths);
            if (t.Format == "twos") Swap16(bytes);
            var s = new MFSample();
            if (perNal && lengths is { Length: > 0 }) foreach (var (off, len) in NalBuffers(bytes, lengths, t.Hevc)) AddBuffer(s, bytes.AsSpan(off, len));
            else AddBuffer(s, bytes);
            s.Time = t.Pts(m);
            if (m.CtsOffset != 0) s.Attributes.Set(MFSampleExtension_DecodeTimestamp, MFValue.Of((ulong)t.Hns(m.Dts)));
            if (m.Duration != uint.MaxValue) s.Duration = t.Hns(m.Duration);
            if (lengths != null)
            {
                var blob = new byte[lengths.Length * 4];
                for (int i = 0; i < lengths.Length; i++) BitConverter.GetBytes(lengths[i]).CopyTo(blob, i * 4);
                s.Attributes.Set(SR.MFSampleExtension_NALULengthInfo, MFValue.Of(blob));
            }
            var (clean, disc) = c.SampleFlags(m, first);
            if (clean) s.Attributes.Set(MF.MFSampleExtension_CleanPoint, 1u);
            if (disc) s.Attributes.Set(MF.MFSampleExtension_Discontinuity, 1u);
            return s;
        }

        private static void AddBuffer(MFSample s, ReadOnlySpan<byte> bytes)
        {
            var buf = new MFMemoryBuffer((uint)Math.Max(1, bytes.Length));
            byte* d = buf.LockBytes(true);
            bytes.CopyTo(new Span<byte>(d, bytes.Length));
            buf.UnlockBytes();
            buf.CurrentLength = (uint)bytes.Length;
            s.Buffers.Add(buf.Pointer);           // the sample's reference
        }

        /// <summary>The leading parameter sets as one span; each NAL unit after them as its start code and its rest.</summary>
        private static IEnumerable<(int, int)> NalBuffers(byte[] b, uint[] lengths, bool hevc)
        {
            var spans = new List<(int, int)>();
            int at = 0, i = 0;
            int ps = 0;
            for (; i < lengths.Length; i++)
            {
                int sc = StartCode(b, at + ps);
                if (sc == 0 || at + ps + sc >= b.Length) break;
                int type = hevc ? (b[at + ps + sc] >> 1) & 0x3F : b[at + ps + sc] & 0x1F;
                bool isPs = hevc ? type is 32 or 33 or 34 : type is 7 or 8;
                if (!isPs) break;
                ps += (int)lengths[i];
            }
            if (ps > 0) spans.Add((at, ps));
            at += ps;
            for (; i < lengths.Length; i++)
            {
                int len = (int)lengths[i];
                if (at + len > b.Length) len = b.Length - at;
                int sc = StartCode(b, at);
                if (sc > 0 && sc < len) { spans.Add((at, sc)); spans.Add((at + sc, len - sc)); }
                else if (len > 0) spans.Add((at, len));
                at += len;
            }
            if (at < b.Length) spans.Add((at, b.Length - at));
            return spans;
        }

        private static int StartCode(byte[] b, int at)
        {
            if (at + 4 <= b.Length && b[at] == 0 && b[at + 1] == 0 && b[at + 2] == 0 && b[at + 3] == 1) return 4;
            if (at + 3 <= b.Length && b[at] == 0 && b[at + 1] == 0 && b[at + 2] == 1) return 3;
            return 0;
        }

        private static void Swap16(byte[] b) { for (int i = 0; i + 1 < b.Length; i += 2) (b[i], b[i + 1]) = (b[i + 1], b[i]); }
    }

    /// <summary>A track of a file the shim reads itself.</summary>
    internal sealed class ContainerFeed : SampleFeed
    {
        private readonly MediaContainer _c;
        public readonly TrackCursor Cursor;

        public ContainerFeed(MediaContainer c, Mp4Track t) { _c = c; Cursor = new TrackCursor(c, t); }

        public Mp4Track Track => Cursor.Track;
        public override bool Audio => Track.Audio;
        public override bool Video => Track.Video;
        public override long NextTime => Cursor.NextTime;
        public override bool AtEnd => Cursor.AtEnd;
        public override MFSample? Take(bool first) => Cursor.AtEnd ? null : ContainerSamples.Make(_c, Track, Track.Samples[Cursor.Take()], first);
        public override void Seek(long hns) => Cursor.Seek(hns);
        public override void BackToSync() => Cursor.BackToSync();
    }
}

namespace Microsoft.Wpf.Interop.WebGpu.MediaFoundation.Source
{
    /// <summary>What a source reader reads: its streams (in order, each selected or not), and the source's presentation.</summary>
    internal abstract class ReaderSource : IDisposable
    {
        public abstract IEnumerable<(SampleFeed Feed, MFMediaType Native, bool Selected)> Streams();
        public abstract uint Characteristics { get; }
        public abstract MFValue? Presentation(Guid key);
        /// <summary>The media source the reader is of (IMFMediaSource), or none.</summary>
        public virtual IntPtr MediaSource => IntPtr.Zero;
        /// <summary>A stream selected or not, by the reader's index of it.</summary>
        public virtual void Select(int index, bool on) { }
        /// <summary>The whole source to a new position, before each stream's feed is.</summary>
        public virtual int Seek(long hns) => MF.S_OK;
        public abstract void Dispose();
    }

    /// <summary>A file the shim reads itself: its streams in its source's order; the first of each kind selected.</summary>
    internal sealed class ContainerSource : ReaderSource
    {
        private readonly MediaContainer _c;
        private readonly MFMediaSource? _owner;
        private readonly bool _disconnect;

        /// <summary>The container, the reader's own; or of a media source of the shim's, shut down with the reader unless told not to.</summary>
        public ContainerSource(MediaContainer c, MFMediaSource? owner = null, bool disconnect = false)
        {
            _c = c; _owner = owner; _disconnect = disconnect;
            owner?.AddRef();
        }

        public override IEnumerable<(SampleFeed, MFMediaType, bool)> Streams()
        {
            var kinds = new HashSet<string>();
            foreach (var t in _c.SourceOrder)
            {
                var native = _c.Native(t);
                if (native == null) continue;
                yield return (new ContainerFeed(_c, t), native, kinds.Add(t.Handler));
            }
        }

        public override uint Characteristics => _c.Characteristics;
        public override MFValue? Presentation(Guid key) => _c.Presentation(key);
        public override IntPtr MediaSource => _owner?.Pointer ?? IntPtr.Zero;

        public override void Dispose()
        {
            if (_owner == null) { _c.Dispose(); return; }
            if (!_disconnect) _owner.Shutdown();
            _owner.Release();
        }
    }
}
