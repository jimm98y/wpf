// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Windows' MPEG-4 media sink (MFCreateMPEG4MediaSink; the sink the sink writer writes to), as Windows' answers
// (MFMediaSinkProbe, MediaSink.windows.txt):
//
//   - IMFFinalizableMediaSink (IMFMediaSink), IMFClockStateSink, IMFMediaEventGenerator, IMFGetService and IMFAttributes;
//     rateless (MEDIASINK_RATELESS), of no clock until one is set (MF_E_NO_CLOCK);
//   - a stream sink of the video type given (its identifier 1), of the audio type (2); more added by any identifier,
//     removed by theirs (MF_E_INVALIDSTREAMNUMBER of one there is not), by index MF_E_INVALIDINDEX past the last;
//   - each stream sink an IMFStreamSink, its own IMFMediaTypeHandler (of the one type, current), IMFMediaEventGenerator;
//   - its samples into the MP4 file (Mp4Muxer.cs) as they come: H.264's and HEVC's from Annex B to length-prefixed NAL
//     units, their parameter sets to avcC / hvcC; AAC's configuration of the type's MF_MT_USER_DATA (of the rate and
//     channels, AAC-LC, where it has none); a stream of no samples an empty track;
//   - finalized (BeginFinalize, EndFinalize): the file finished and its byte stream closed; the sink answering on;
//     after Shutdown, MF_E_SHUTDOWN.
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Wpf.Interop.WebGpu.Direct3D;
using Microsoft.Wpf.Interop.WebGpu.MediaFoundation.Source;

namespace Microsoft.Wpf.Interop.WebGpu.MediaFoundation.Sink
{
    internal sealed unsafe class MFMpeg4Sink : MFAttributesObject
    {
        public static readonly Guid IID_IMFMediaSink = new("6ef2a660-47c0-4666-b13d-cbb717f2fa2c"), IID_IMFFinalizableMediaSink = new("eaecb74a-9a50-42ce-9541-6a7f57aa4ad7");
        public static readonly Guid IID_IMFGetService = new("fa993888-4383-415a-a930-dd472a8cf6f7");
        public const int MF_E_NO_CLOCK = unchecked((int)0xC00D36D7), MF_E_INVALIDINDEX = unchecked((int)0xC00D36BF), MF_E_STREAMSINK_EXISTS = unchecked((int)0xC00D4A3B);
        private const uint MEDIASINK_RATELESS = 0x4, MEStreamSinkStarted = 801, MEStreamSinkStopped = 802, MEStreamSinkPaused = 803, MEStreamSinkMarker = 806;

        private static readonly Guid MFAacPayloadType = new("bfbabe79-7434-4d1c-94f0-72a3b9e17188");
        private static readonly Vtable s_sink = BuildSink(), s_clockSink = BuildClockSink(), s_events = BuildEvents(), s_service = BuildService(), s_attributes = BuildAttributes();

        private readonly object _lock = new();
        private readonly MFEventQueue _queue = new();
        private readonly List<MFStreamSinkObject> _streams = new();
        private Stream? _out;
        private Mp4Muxer? _mux;
        private IntPtr _clock;                         // IMFPresentationClock, AddRef'd
        private bool _shutdown, _finalized;

        private MFMpeg4Sink(Stream output) : base(s_sink, s_clockSink, s_events, s_service, s_attributes) { _out = output; }

        /// <summary>The sink of the file, its streams of the types given (video, then audio; either may be null).</summary>
        public static MFMpeg4Sink Create(Stream output, MFMediaType? video, MFMediaType? audio)
        {
            var s = new MFMpeg4Sink(output);
            if (video != null) s._streams.Add(new MFStreamSinkObject(s, 1, video.Clone()));
            if (audio != null)
            {
                var a = audio.Clone();
                // (Windows' sink gives AAC of no payload type its raw packets': 0)
                if (a.Subtype == SR.MFAudioFormat_AAC && a.Attributes.Get(MFAacPayloadType) == null) a.Attributes.Set(MFAacPayloadType, 0u);
                s._streams.Add(new MFStreamSinkObject(s, 2, a));
            }
            return s;
        }

        protected override void OnFinalRelease()
        {
            DoShutdown();
            base.OnFinalRelease();
        }

        private void DoShutdown()
        {
            List<MFStreamSinkObject> streams;
            lock (_lock)
            {
                if (_shutdown) return;
                _shutdown = true;
                streams = _streams.ToList();
                _streams.Clear();
                if (_clock != IntPtr.Zero) { RemoveFromClock(); Com.Release(_clock); _clock = IntPtr.Zero; }
                if (!_finalized) { _out?.Dispose(); _out = null; }
            }
            foreach (var s in streams) { s.Detach(); s.Release(); }
            _queue.Shutdown();
        }

        private void RemoveFromClock()
        {
            IntPtr me = PointerFor(s_clockSink);
            ((delegate* unmanaged<IntPtr, IntPtr, int>)Com.Fn(_clock, 12))(_clock, me);
        }

        /// <summary>The track of a type the sink writes (H.264, HEVC, AAC), else null.</summary>
        public static MuxTrack? TrackOf(MFMediaType t)
        {
            var major = t.Attributes.Guid(MF.MF_MT_MAJOR_TYPE);
            var sub = t.Subtype;
            if (major == MF.MFMediaType_Video && (sub == MF.MFVideoFormat_H264 || sub == MF.MFVideoFormat_HEVC))
            {
                var (fw, fh) = t.FrameSize;
                var (rn, rd) = MF.Unpack(t.Attributes.U64(MF.MF_MT_FRAME_RATE, MF.Pack(30, 1)));
                if (fw == 0 || fh == 0 || rn == 0 || rd == 0) return null;
                var (pw, ph) = MF.Unpack(t.Attributes.U64(MF.MF_MT_PIXEL_ASPECT_RATIO, MF.Pack(1, 1)));
                return new MuxTrack
                {
                    Video = true, Format = sub == MF.MFVideoFormat_HEVC ? "hvc1" : "avc1", Width = (int)fw, Height = (int)fh,
                    // the frame rate times 1000 (25 fps: 25000), where that is whole; else the rate's numerator
                    Timescale = rn * 1000UL % rd == 0 ? (uint)(rn * 1000UL / rd) : rn, ParW = pw, ParH = ph,
                };
            }
            if (major == SR.MFMediaType_Audio && sub == SR.MFAudioFormat_AAC)
            {
                var track = new MuxTrack
                {
                    Format = "mp4a", Channels = (int)t.Attributes.U32(SR.MF_MT_AUDIO_NUM_CHANNELS, 2), SampleRate = (int)t.Attributes.U32(SR.MF_MT_AUDIO_SAMPLES_PER_SECOND, 44100),
                    SampleBits = 16, BufferSize = 6144,                // Windows' encoder's (an AAC frame's largest)
                };
                track.Timescale = (uint)track.SampleRate;
                track.Bitrate = track.MaxBitrate = t.Attributes.U32(SR.MF_MT_AUDIO_AVG_BYTES_PER_SECOND, 16000) * 8;
                if (t.Attributes.Get(MF.MF_MT_USER_DATA)?.B is { Length: > 12 } ud) track.Config = ud.AsSpan(12).ToArray();
                else
                {
                    // AAC-LC's AudioSpecificConfig of the rate and channels
                    int[] rates = { 96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050, 16000, 12000, 11025, 8000, 7350 };
                    int fi = Array.IndexOf(rates, track.SampleRate); if (fi < 0) fi = 4;
                    int asc = 2 << 11 | fi << 7 | (track.Channels & 15) << 3;
                    track.Config = new[] { (byte)(asc >> 8), (byte)asc };
                }
                return track;
            }
            return null;
        }

        /// <summary>A sample of a stream into the file: the muxer begun at the first, of every stream's track.</summary>
        internal int Write(MFStreamSinkObject s, byte[] data, long time, long duration, bool sync)
        {
            lock (_lock)
            {
                if (_shutdown) return MF.MF_E_SHUTDOWN;
                if (_finalized || _out == null) return MFE.MF_E_INVALIDREQUEST;
                if (_mux == null)
                {
                    _mux = new Mp4Muxer(_out);
                    foreach (var o in _streams) if (o.Track != null) _mux.Add(o.Track);
                }
                if (s.Track == null || !_mux.Has(s.Track)) return MF.S_OK;          // (a stream added after the first sample: not written)
                if (!s.Track.Video) { _mux.Write(s.Track, data, time, duration, true); return MF.S_OK; }
                bool hevc = s.Track.Format == "hvc1";
                var o2 = new MemoryStream(data.Length);
                var sets = new List<byte[]>();
                foreach (var nal in AnnexB.Nals(data))
                {
                    int type = hevc ? (nal[0] >> 1) & 0x3F : nal[0] & 0x1F;
                    bool parameterSet = hevc ? type is 32 or 33 or 34 : type is 7 or 8;
                    if (parameterSet) { sets.Add(nal); continue; }
                    o2.Write(new[] { (byte)(nal.Length >> 24), (byte)(nal.Length >> 16), (byte)(nal.Length >> 8), (byte)nal.Length });
                    o2.Write(nal);
                }
                if (s.Track.Config.Length == 0 && sets.Count > 0) s.Track.Config = hevc ? AnnexB.HvcC(sets) : AnnexB.AvcC(sets);
                _mux.Write(s.Track, o2.ToArray(), time, duration, sync || AnnexB.IsKey(data, hevc));
                return MF.S_OK;
            }
        }

        /// <summary>The file finished: its last chunks and moov (of a file of no samples, none), the output closed.</summary>
        public int Finish()
        {
            lock (_lock)
            {
                if (_shutdown) return MF.MF_E_SHUTDOWN;
                if (_finalized) return MF.S_OK;
                _finalized = true;
                try { _mux?.Finish(); }
                finally { _out?.Dispose(); _out = null; }
            }
            return MF.S_OK;
        }

        /// <summary>Whether any sample has come.</summary>
        public bool Written { get { lock (_lock) return _mux != null; } }

        private static MFMpeg4Sink? S(IntPtr self) => From<MFMpeg4Sink>(self);

        // ---- IMFMediaSink, IMFFinalizableMediaSink ---------------------------------------------------------

        private static Vtable BuildSink()
        {
            var vt = new Vtable("IMFFinalizableMediaSink", 14, IID_IMFFinalizableMediaSink, IID_IMFMediaSink);
            vt.Set(3, (delegate* unmanaged<IntPtr, uint*, int>)&GetCharacteristics);
            vt.Set(4, (delegate* unmanaged<IntPtr, uint, IntPtr, IntPtr*, int>)&AddStreamSink);
            vt.Set(5, (delegate* unmanaged<IntPtr, uint, int>)&RemoveStreamSink);
            vt.Set(6, (delegate* unmanaged<IntPtr, uint*, int>)&GetStreamSinkCount);
            vt.Set(7, (delegate* unmanaged<IntPtr, uint, IntPtr*, int>)&GetStreamSinkByIndex);
            vt.Set(8, (delegate* unmanaged<IntPtr, uint, IntPtr*, int>)&GetStreamSinkById);
            vt.Set(9, (delegate* unmanaged<IntPtr, IntPtr, int>)&SetPresentationClock);
            vt.Set(10, (delegate* unmanaged<IntPtr, IntPtr*, int>)&GetPresentationClock);
            vt.Set(11, (delegate* unmanaged<IntPtr, int>)&ShutdownSlot);
            vt.Set(12, (delegate* unmanaged<IntPtr, IntPtr, IntPtr, int>)&BeginFinalize);
            vt.Set(13, (delegate* unmanaged<IntPtr, IntPtr, int>)&EndFinalize);
            return vt;
        }

        [UnmanagedCallersOnly]
        private static int GetCharacteristics(IntPtr self, uint* c)
        {
            if (S(self) is not { } s || c == null) return MF.E_POINTER;
            lock (s._lock) { if (s._shutdown) return MF.MF_E_SHUTDOWN; *c = MEDIASINK_RATELESS; }
            return MF.S_OK;
        }

        /// <summary>A stream sink of the identifier and type (the sink writer's streams added to a sink of the application's).</summary>
        public int Add(uint id, MFMediaType type, out MFStreamSinkObject? stream)
        {
            stream = null;
            lock (_lock)
            {
                if (_shutdown) return MF.MF_E_SHUTDOWN;
                if (_streams.Any(x => x.Id == id)) return MF_E_STREAMSINK_EXISTS;
                stream = new MFStreamSinkObject(this, id, type.Clone());
                _streams.Add(stream);
                return MF.S_OK;
            }
        }

        [UnmanagedCallersOnly]
        private static int AddStreamSink(IntPtr self, uint id, IntPtr type, IntPtr* stream)
        {
            if (S(self) is not { } s || type == IntPtr.Zero) return MF.E_POINTER;
            if (stream != null) *stream = IntPtr.Zero;
            var t = Mp4Types.Read(type);
            try
            {
                int hr = s.Add(id, t, out var made);
                if (hr >= 0 && stream != null) *stream = made!.AddRefPointer();
                return hr;
            }
            finally { t.Release(); }
        }

        [UnmanagedCallersOnly]
        private static int RemoveStreamSink(IntPtr self, uint id)
        {
            if (S(self) is not { } s) return MF.E_POINTER;
            MFStreamSinkObject? gone;
            lock (s._lock)
            {
                if (s._shutdown) return MF.MF_E_SHUTDOWN;
                gone = s._streams.FirstOrDefault(x => x.Id == id);
                if (gone == null) return MF.MF_E_INVALIDSTREAMNUMBER;
                s._streams.Remove(gone);
            }
            gone.Detach(); gone.Release();
            return MF.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int GetStreamSinkCount(IntPtr self, uint* n)
        {
            if (S(self) is not { } s || n == null) return MF.E_POINTER;
            lock (s._lock) { if (s._shutdown) return MF.MF_E_SHUTDOWN; *n = (uint)s._streams.Count; }
            return MF.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int GetStreamSinkByIndex(IntPtr self, uint i, IntPtr* stream)
        {
            if (S(self) is not { } s || stream == null) return MF.E_POINTER;
            *stream = IntPtr.Zero;
            lock (s._lock)
            {
                if (s._shutdown) return MF.MF_E_SHUTDOWN;
                if (i >= s._streams.Count) return MF_E_INVALIDINDEX;
                *stream = s._streams[(int)i].AddRefPointer();
            }
            return MF.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int GetStreamSinkById(IntPtr self, uint id, IntPtr* stream)
        {
            if (S(self) is not { } s || stream == null) return MF.E_POINTER;
            *stream = IntPtr.Zero;
            lock (s._lock)
            {
                if (s._shutdown) return MF.MF_E_SHUTDOWN;
                var x = s._streams.FirstOrDefault(o => o.Id == id);
                if (x == null) return MF.MF_E_INVALIDSTREAMNUMBER;
                *stream = x.AddRefPointer();
            }
            return MF.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int SetPresentationClock(IntPtr self, IntPtr clock)
        {
            if (S(self) is not { } s) return MF.E_POINTER;
            lock (s._lock)
            {
                if (s._shutdown) return MF.MF_E_SHUTDOWN;
                if (s._clock != IntPtr.Zero) { s.RemoveFromClock(); Com.Release(s._clock); s._clock = IntPtr.Zero; }
                if (clock == IntPtr.Zero) return MF.S_OK;
                int hr = ((delegate* unmanaged<IntPtr, IntPtr, int>)Com.Fn(clock, 11))(clock, s.PointerFor(s_clockSink));      // AddClockStateSink
                if (hr < 0) return hr;
                Com.AddRef(clock);
                s._clock = clock;
            }
            return MF.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int GetPresentationClock(IntPtr self, IntPtr* clock)
        {
            if (S(self) is not { } s || clock == null) return MF.E_POINTER;
            *clock = IntPtr.Zero;
            lock (s._lock)
            {
                if (s._shutdown) return MF.MF_E_SHUTDOWN;
                if (s._clock == IntPtr.Zero) return MF_E_NO_CLOCK;
                Com.AddRef(s._clock);
                *clock = s._clock;
            }
            return MF.S_OK;
        }

        [UnmanagedCallersOnly] private static int ShutdownSlot(IntPtr self) { if (S(self) is not { } s) return MF.E_POINTER; lock (s._lock) if (s._shutdown) return MF.MF_E_SHUTDOWN; s.DoShutdown(); return MF.S_OK; }

        [UnmanagedCallersOnly]
        private static int BeginFinalize(IntPtr self, IntPtr callback, IntPtr state)
        {
            if (S(self) is not { } s) return MF.E_POINTER;
            int hr;
            try { hr = s.Finish(); }
            catch (Exception e) { D3DLog.Error("IMFFinalizableMediaSink.BeginFinalize", e); hr = MF.E_FAIL; }
            if (hr == MF.MF_E_SHUTDOWN) return hr;
            if (callback != IntPtr.Zero)
            {
                // (the file is finished already: its callback told at once, on the caller's thread)
                var result = new MFAsyncResult(IntPtr.Zero, callback, state) { Status = hr };
                result.InvokeNow();
            }
            return MF.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int EndFinalize(IntPtr self, IntPtr result)
        {
            if (result == IntPtr.Zero) return MF.E_POINTER;
            return ((delegate* unmanaged<IntPtr, int>)Com.Fn(result, 4))(result);                 // IMFAsyncResult::GetStatus
        }

        // ---- IMFClockStateSink: the stream sinks told -------------------------------------------------------

        private static Vtable BuildClockSink()
        {
            var vt = new Vtable("IMFClockStateSink", 8, MFPresentationClock.IID_IMFClockStateSink);
            vt.Set(3, (delegate* unmanaged<IntPtr, long, long, int>)&OnClockStart);
            vt.Set(4, (delegate* unmanaged<IntPtr, long, int>)&OnClockStop);
            vt.Set(5, (delegate* unmanaged<IntPtr, long, int>)&OnClockPause);
            vt.Set(6, (delegate* unmanaged<IntPtr, long, int>)&OnClockRestart);
            vt.Set(7, (delegate* unmanaged<IntPtr, long, float, int>)&OnClockSetRate);
            return vt;
        }

        private int Tell(uint type)
        {
            List<MFStreamSinkObject> streams;
            lock (_lock) { if (_shutdown) return MF.MF_E_SHUTDOWN; streams = _streams.ToList(); }
            foreach (var x in streams) x.Queue.QueueEvent(type, Guid.Empty, MF.S_OK, null);
            return MF.S_OK;
        }

        [UnmanagedCallersOnly] private static int OnClockStart(IntPtr self, long system, long offset) => S(self)?.Tell(MEStreamSinkStarted) ?? MF.E_POINTER;
        [UnmanagedCallersOnly] private static int OnClockStop(IntPtr self, long system) => S(self)?.Tell(MEStreamSinkStopped) ?? MF.E_POINTER;
        [UnmanagedCallersOnly] private static int OnClockPause(IntPtr self, long system) => S(self)?.Tell(MEStreamSinkPaused) ?? MF.E_POINTER;
        [UnmanagedCallersOnly] private static int OnClockRestart(IntPtr self, long system) => S(self)?.Tell(MEStreamSinkStarted) ?? MF.E_POINTER;
        [UnmanagedCallersOnly] private static int OnClockSetRate(IntPtr self, long system, float rate) => MF.S_OK;

        // ---- IMFMediaEventGenerator, IMFGetService, IMFAttributes ------------------------------------------

        internal static Vtable BuildEvents(Func<IntPtr, MFEventQueue?>? of = null)
        {
            var vt = new Vtable("IMFMediaEventGenerator", 7, MS.IID_IMFMediaEventGenerator);
            vt.Set(3, (delegate* unmanaged<IntPtr, uint, IntPtr*, int>)&GetEvent);
            vt.Set(4, (delegate* unmanaged<IntPtr, IntPtr, IntPtr, int>)&BeginGetEvent);
            vt.Set(5, (delegate* unmanaged<IntPtr, IntPtr, IntPtr*, int>)&EndGetEvent);
            vt.Set(6, (delegate* unmanaged<IntPtr, uint, Guid*, int, byte*, int>)&QueueEvent);
            return vt;
        }

        /// <summary>The event queue of the sink or of a stream sink.</summary>
        internal static MFEventQueue? QueueOf(IntPtr self) => From(self) switch { MFMpeg4Sink s => s._queue, MFStreamSinkObject x => x.Queue, _ => null };

        [UnmanagedCallersOnly]
        private static int GetEvent(IntPtr self, uint flags, IntPtr* e)
        {
            if (QueueOf(self) is not { } q || e == null) return MF.E_POINTER;
            int hr = q.GetEvent((flags & 1) != 0, out var ev);
            *e = ev;
            return hr;
        }

        [UnmanagedCallersOnly] private static int BeginGetEvent(IntPtr self, IntPtr callback, IntPtr state) => QueueOf(self)?.BeginGetEvent(callback, state) ?? MF.E_POINTER;

        [UnmanagedCallersOnly]
        private static int EndGetEvent(IntPtr self, IntPtr result, IntPtr* e)
        {
            if (QueueOf(self) is not { } q || e == null) return MF.E_POINTER;
            int hr = q.EndGetEvent(result, out var ev);
            *e = ev;
            return hr;
        }

        [UnmanagedCallersOnly]
        private static int QueueEvent(IntPtr self, uint type, Guid* ext, int status, byte* value)
            => QueueOf(self)?.QueueEvent(type, ext == null ? Guid.Empty : *ext, status, MFMediaEvent.ValueOf(value)) ?? MF.E_POINTER;

        private static Vtable BuildService()
        {
            var vt = new Vtable("IMFGetService", 4, IID_IMFGetService);
            vt.Set(3, (delegate* unmanaged<IntPtr, Guid*, Guid*, IntPtr*, int>)&GetService);
            return vt;
        }

        [UnmanagedCallersOnly] private static int GetService(IntPtr self, Guid* service, Guid* riid, IntPtr* ppv) { if (ppv != null) *ppv = IntPtr.Zero; return MFSinkWriter.MF_E_UNSUPPORTED_SERVICE; }

        private static Vtable BuildAttributes() { var vt = new Vtable("IMFAttributes", 33, MF.IID_IMFAttributes); FillAttributes(vt); return vt; }
    }

    /// <summary>A stream sink of the MPEG-4 sink: IMFStreamSink, its own IMFMediaTypeHandler (of its one type), IMFMediaEventGenerator.</summary>
    internal sealed unsafe class MFStreamSinkObject : ComObject
    {
        public static readonly Guid IID_IMFStreamSink = new("0a97b3cf-8e7c-4a3d-8f8c-0c843dc247fb");
        private static readonly Vtable s_stream = BuildStream(), s_handler = BuildHandler(), s_events = MFMpeg4Sink.BuildEvents();

        private MFMpeg4Sink? _sink;
        public readonly uint Id;
        public readonly MFEventQueue Queue = new();
        private readonly MFMediaTypeHandler _types;
        public MuxTrack? Track;

        public MFStreamSinkObject(MFMpeg4Sink sink, uint id, MFMediaType type) : base(s_stream, s_handler, s_events)
        {
            _sink = sink; Id = id;
            _types = new MFMediaTypeHandler(new[] { type }) { Current = type.Clone() };
            Track = MFMpeg4Sink.TrackOf(type);
        }

        /// <summary>
        /// Its type replaced by one of the same major type and subtype (the sink writer's encoder's output, as Windows' writer
        /// sets it): its one type and the current; the track of it, where no sample has come.
        /// </summary>
        public int SetType(MFMediaType t)
        {
            var cur = _types.Current;
            if (cur == null || cur.Attributes.Guid(MF.MF_MT_MAJOR_TYPE) != t.Attributes.Guid(MF.MF_MT_MAJOR_TYPE) || cur.Subtype != t.Subtype) return MF.MF_E_INVALIDMEDIATYPE;
            foreach (var old in _types.Types) old.Release();
            _types.Types.Clear();
            _types.Types.Add(t.Clone());
            _types.Current = t.Clone();
            cur.Release();
            if (_sink is { Written: false }) Track = MFMpeg4Sink.TrackOf(t);
            return MF.S_OK;
        }

        /// <summary>Of the sink no longer (removed, or the sink shut down): MF_E_STREAMSINK_REMOVED after.</summary>
        public void Detach() { _sink = null; Queue.Shutdown(); }

        protected override void OnFinalRelease() { Detach(); _types.Release(); }

        private static MFStreamSinkObject? X(IntPtr self) => From<MFStreamSinkObject>(self);
        private const int MF_E_STREAMSINK_REMOVED = unchecked((int)0xC00D4A38);

        private static Vtable BuildStream()
        {
            var vt = new Vtable("IMFStreamSink", 13, IID_IMFStreamSink, MS.IID_IMFMediaEventGenerator);
            vt.Set(3, (delegate* unmanaged<IntPtr, uint, IntPtr*, int>)&GetEvent);
            vt.Set(4, (delegate* unmanaged<IntPtr, IntPtr, IntPtr, int>)&BeginGetEvent);
            vt.Set(5, (delegate* unmanaged<IntPtr, IntPtr, IntPtr*, int>)&EndGetEvent);
            vt.Set(6, (delegate* unmanaged<IntPtr, uint, Guid*, int, byte*, int>)&QueueEvent);
            vt.Set(7, (delegate* unmanaged<IntPtr, IntPtr*, int>)&GetMediaSink);
            vt.Set(8, (delegate* unmanaged<IntPtr, uint*, int>)&GetIdentifier);
            vt.Set(9, (delegate* unmanaged<IntPtr, IntPtr*, int>)&GetMediaTypeHandler);
            vt.Set(10, (delegate* unmanaged<IntPtr, IntPtr, int>)&ProcessSample);
            vt.Set(11, (delegate* unmanaged<IntPtr, uint, byte*, byte*, int>)&PlaceMarker);
            vt.Set(12, (delegate* unmanaged<IntPtr, int>)&Flush);
            return vt;
        }

        [UnmanagedCallersOnly]
        private static int GetEvent(IntPtr self, uint flags, IntPtr* e)
        {
            if (X(self) is not { } x || e == null) return MF.E_POINTER;
            int hr = x.Queue.GetEvent((flags & 1) != 0, out var ev);
            *e = ev;
            return hr;
        }

        [UnmanagedCallersOnly] private static int BeginGetEvent(IntPtr self, IntPtr callback, IntPtr state) => X(self)?.Queue.BeginGetEvent(callback, state) ?? MF.E_POINTER;

        [UnmanagedCallersOnly]
        private static int EndGetEvent(IntPtr self, IntPtr result, IntPtr* e)
        {
            if (X(self) is not { } x || e == null) return MF.E_POINTER;
            int hr = x.Queue.EndGetEvent(result, out var ev);
            *e = ev;
            return hr;
        }

        [UnmanagedCallersOnly]
        private static int QueueEvent(IntPtr self, uint type, Guid* ext, int status, byte* value)
            => X(self)?.Queue.QueueEvent(type, ext == null ? Guid.Empty : *ext, status, MFMediaEvent.ValueOf(value)) ?? MF.E_POINTER;

        [UnmanagedCallersOnly]
        private static int GetMediaSink(IntPtr self, IntPtr* sink)
        {
            if (X(self) is not { } x || sink == null) return MF.E_POINTER;
            *sink = IntPtr.Zero;
            if (x._sink is not { } s) return MF_E_STREAMSINK_REMOVED;
            *sink = s.AddRefPointer();
            return MF.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int GetIdentifier(IntPtr self, uint* id)
        {
            if (X(self) is not { } x || id == null) return MF.E_POINTER;
            if (x._sink == null) return MF_E_STREAMSINK_REMOVED;
            *id = x.Id;
            return MF.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int GetMediaTypeHandler(IntPtr self, IntPtr* handler)
        {
            if (X(self) is not { } x || handler == null) return MF.E_POINTER;
            *handler = IntPtr.Zero;
            if (x._sink == null) return MF_E_STREAMSINK_REMOVED;
            IntPtr p = x.PointerFor(s_handler);
            Com.AddRef(p);
            *handler = p;
            return MF.S_OK;
        }

        /// <summary>The sample into the file: its bytes, time, duration and clean point.</summary>
        [UnmanagedCallersOnly]
        private static int ProcessSample(IntPtr self, IntPtr sample)
        {
            if (X(self) is not { } x || sample == IntPtr.Zero) return MF.E_POINTER;
            if (x._sink is not { } s) return MF_E_STREAMSINK_REMOVED;
            try
            {
                byte[] bytes; long time = 0, duration = 0; uint clean = 0;
                if (From<MFSample>(sample) is { } ours) { bytes = ours.Bytes(); time = ours.Time ?? 0; duration = ours.Duration ?? 0; clean = ours.Attributes.U32(MF.MFSampleExtension_CleanPoint); }
                else
                {
                    bytes = MFSample.ForeignSampleBytes(sample);
                    ((delegate* unmanaged<IntPtr, long*, int>)Com.Fn(sample, 35))(sample, &time);
                    ((delegate* unmanaged<IntPtr, long*, int>)Com.Fn(sample, 37))(sample, &duration);
                    var key = MF.MFSampleExtension_CleanPoint;
                    ((delegate* unmanaged<IntPtr, Guid*, uint*, int>)Com.Fn(sample, 7))(sample, &key, &clean);
                }
                return s.Write(x, bytes, time, duration, clean != 0);
            }
            catch (Exception e) { D3DLog.Error("IMFStreamSink.ProcessSample", e); return MF.E_FAIL; }
        }

        /// <summary>A marker: MEStreamSinkMarker of its context, at once (the sink holds no samples back).</summary>
        [UnmanagedCallersOnly]
        private static int PlaceMarker(IntPtr self, uint type, byte* value, byte* context)
        {
            if (X(self) is not { } x) return MF.E_POINTER;
            if (x._sink == null) return MF_E_STREAMSINK_REMOVED;
            return x.Queue.QueueEvent(806, Guid.Empty, MF.S_OK, MFMediaEvent.ValueOf(context));
        }

        [UnmanagedCallersOnly] private static int Flush(IntPtr self) => X(self) is { } x ? (x._sink == null ? MF_E_STREAMSINK_REMOVED : MF.S_OK) : MF.E_POINTER;

        // ---- IMFMediaTypeHandler: the handler's own, through its vtable ---------------------------------------

        private static Vtable BuildHandler()
        {
            var vt = new Vtable("IMFMediaTypeHandler", 9, MFE.IID_IMFMediaTypeHandler);
            vt.Set(3, (delegate* unmanaged<IntPtr, IntPtr, IntPtr*, int>)&IsMediaTypeSupported);
            vt.Set(4, (delegate* unmanaged<IntPtr, uint*, int>)&GetMediaTypeCount);
            vt.Set(5, (delegate* unmanaged<IntPtr, uint, IntPtr*, int>)&GetMediaTypeByIndex);
            vt.Set(6, (delegate* unmanaged<IntPtr, IntPtr, int>)&SetCurrentMediaType);
            vt.Set(7, (delegate* unmanaged<IntPtr, IntPtr*, int>)&GetCurrentMediaType);
            vt.Set(8, (delegate* unmanaged<IntPtr, Guid*, int>)&GetMajorType);
            return vt;
        }

        private static IntPtr H(IntPtr self) => X(self)?._types.Pointer ?? IntPtr.Zero;

        [UnmanagedCallersOnly] private static int IsMediaTypeSupported(IntPtr self, IntPtr t, IntPtr* closest) { var h = H(self); return h == IntPtr.Zero ? MF.E_POINTER : ((delegate* unmanaged<IntPtr, IntPtr, IntPtr*, int>)Com.Fn(h, 3))(h, t, closest); }
        [UnmanagedCallersOnly] private static int GetMediaTypeCount(IntPtr self, uint* n) { var h = H(self); return h == IntPtr.Zero ? MF.E_POINTER : ((delegate* unmanaged<IntPtr, uint*, int>)Com.Fn(h, 4))(h, n); }
        [UnmanagedCallersOnly] private static int GetMediaTypeByIndex(IntPtr self, uint i, IntPtr* t) { var h = H(self); return h == IntPtr.Zero ? MF.E_POINTER : ((delegate* unmanaged<IntPtr, uint, IntPtr*, int>)Com.Fn(h, 5))(h, i, t); }
        [UnmanagedCallersOnly]
        private static int SetCurrentMediaType(IntPtr self, IntPtr t)
        {
            if (X(self) is not { } x || t == IntPtr.Zero) return MF.E_POINTER;
            var m = Mp4Types.Read(t);
            try { return x.SetType(m); }
            finally { m.Release(); }
        }
        [UnmanagedCallersOnly] private static int GetCurrentMediaType(IntPtr self, IntPtr* t) { var h = H(self); return h == IntPtr.Zero ? MF.E_POINTER : ((delegate* unmanaged<IntPtr, IntPtr*, int>)Com.Fn(h, 7))(h, t); }
        [UnmanagedCallersOnly] private static int GetMajorType(IntPtr self, Guid* g) { var h = H(self); return h == IntPtr.Zero ? MF.E_POINTER : ((delegate* unmanaged<IntPtr, Guid*, int>)Com.Fn(h, 8))(h, g); }
    }

    /// <summary>H.264's and HEVC's Annex B: its NAL units, key frames, and the configuration of their parameter sets.</summary>
    internal static class AnnexB
    {
        public static IEnumerable<byte[]> Nals(byte[] b)
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

        public static bool IsKey(byte[] au, bool hevc)
        {
            foreach (var nal in Nals(au))
            {
                if (nal.Length == 0) continue;
                int type = hevc ? (nal[0] >> 1) & 0x3F : nal[0] & 0x1F;
                if (hevc ? type is >= 16 and <= 23 : type == 5) return true;
            }
            return false;
        }

        public static byte[] AvcC(List<byte[]> sets)
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
        public static byte[] HvcC(List<byte[]> sets)
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
    }
}
