// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The media sources of files, and the source resolver that makes them (Documentation/media-foundation-shim.md,
// "Media sources"): IMFMediaSource and IMFMediaStream over the shim's containers, as Windows' sources of the
// same files behave (probed: MFMediaSourceProbe, the report in MediaSource.windows.txt):
//
//   - the presentation descriptor: the audio streams first, then the video ones, all selected, each of its one
//     native type (the source reader's); a copy of the source's each time it is asked for;
//   - Start: MENewStream (MEUpdatedStream of a stream there already) for each selected stream, then
//     MESourceStarted (MESourceSeeked when started or paused already, at a position), then MEStreamStarted
//     (MEStreamSeeked) on each stream; from a pause, the requests held then answered;
//   - RequestSample: MEMediaSample with the sample (and its token as MFSampleExtension_Token), MEEndOfStream for
//     the request after the last sample, MEEndOfPresentation once every selected stream has ended;
//   - Pause and Stop: MESourcePaused/MESourceStopped and MEStreamPaused/MEStreamStopped; Shutdown: MF_E_SHUTDOWN
//     from every call after it, of the source and of its streams;
//   - what differs by the kind of file, as each of Windows' sources differs (SourceProfile).
//

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Wpf.Interop.WebGpu.Direct3D;

namespace Microsoft.Wpf.Interop.WebGpu.MediaFoundation.Source
{
    internal static class MS
    {
        public static readonly Guid IID_IMFMediaSource = new("279a808d-aec7-40c8-9c6b-a6b492c78a66"), IID_IMFMediaEventGenerator = new("2cd0bd52-bcd5-4b89-b62c-eadc0c031e7d");
        public static readonly Guid IID_IMFMediaStream = new("d182108f-4ec6-443f-aa42-a71106ec825f"), IID_IMFSourceResolver = new("fbe5a32d-a497-4b61-bb85-97b1a848a6e3");
        public static readonly Guid MFSampleExtension_Token = new("8294da66-f328-4805-b551-00deb4c57a61");
        public static readonly Guid MF_SOURCE_READER_DISCONNECT_MEDIASOURCE_ON_SHUTDOWN = new("56b67165-219e-456d-a22e-2d3004c7fe56");
        public const int MF_E_END_OF_STREAM = unchecked((int)0xC00D3E84), MF_E_MEDIA_SOURCE_WRONGSTATE = unchecked((int)0xC00D3E9B);
        // MediaEventType
        public const uint MEError = 1, MESourceStarted = 201, MEStreamStarted = 202, MESourceSeeked = 203, MEStreamSeeked = 204, MENewStream = 205, MEUpdatedStream = 206;
        public const uint MESourceStopped = 207, MEStreamStopped = 208, MESourcePaused = 209, MEStreamPaused = 210, MEEndOfPresentation = 211, MEEndOfStream = 212;
        public const uint MEMediaSample = 213, MEStreamTick = 214, MEStreamFormatChanged = 216;
        // MF_RESOLUTION_FLAGS, MF_OBJECT_TYPE
        public const uint MF_RESOLUTION_MEDIASOURCE = 1, MF_RESOLUTION_BYTESTREAM = 2;
        public const uint MF_OBJECT_MEDIASOURCE = 0, MF_OBJECT_BYTESTREAM = 1, MF_OBJECT_INVALID = 2;

        public static readonly Guid[] PresentationKeys =
        {
            SR.MF_PD_DURATION, SR.MF_PD_TOTAL_FILE_SIZE, SR.MF_PD_MIME_TYPE, SR.MF_PD_AUDIO_ENCODING_BITRATE,
            new("6c990d36-bb8e-477a-8598-0d5d96fcd88a"), SR.MF_PD_LAST_MODIFIED_TIME,        // MF_PD_VIDEO_ENCODING_BITRATE
            Mp4Demuxer.MF_PD_FRAGMENTED, new("33026ee0-e387-4582-ae0a-34a2ad3baa18"),
        };
    }

    /// <summary>What Windows' sources of the kinds of file do differently (probed).</summary>
    internal sealed class SourceProfile
    {
        /// <summary>MESourceStarted and MESourceSeeked with the position; MEStreamStarted with it; MEStreamSeeked with it.</summary>
        public bool StartedTime = true, StreamStartedTime = true, SeekedTime = true;
        /// <summary>Start at a position other than the beginning (the FLAC source: MF_E_INVALIDREQUEST).</summary>
        public bool Seeks = true;
        /// <summary>MEEndOfStream with the last sample, and requests beyond it refused (MP3, FLAC); else for the request after it.</summary>
        public bool EndsWithLast;
        /// <summary>MFSampleExtension_Token on every sample, empty of a request without one (all but Matroska's).</summary>
        public bool TokenAlways = true;
        /// <summary>H.264 and HEVC in buffers of their NAL units (MPEG-4's).</summary>
        public bool PerNal;
        public int StoppedRequest = MS.MF_E_MEDIA_SOURCE_WRONGSTATE, DeselectedRequest = MF.MF_E_SHUTDOWN;
        /// <summary>Started again with nothing left: MEEndOfStream at once (ADTS, MP3, FLAC).</summary>
        public bool EndsOnRestart;
        /// <summary>Shutdown once shut down (FLAC's and Matroska's: MF_E_SHUTDOWN).</summary>
        public int ShutdownAgain = MF.S_OK;

        public static SourceProfile For(MediaContainer c) => c switch
        {
            Mp4Demuxer => new SourceProfile { PerNal = true },
            Mp3Container => new SourceProfile { StartedTime = false, SeekedTime = false, EndsWithLast = true, EndsOnRestart = true, StoppedRequest = MS.MF_E_END_OF_STREAM },
            FlacContainer => new SourceProfile { StartedTime = false, StreamStartedTime = false, Seeks = false, EndsWithLast = true, EndsOnRestart = true, ShutdownAgain = MF.MF_E_SHUTDOWN },
            AdtsContainer => new SourceProfile { EndsOnRestart = true },
            MatroskaContainer => new SourceProfile { TokenAlways = false, StoppedRequest = MS.MF_E_END_OF_STREAM, DeselectedRequest = MS.MF_E_END_OF_STREAM, ShutdownAgain = MF.MF_E_SHUTDOWN },
            _ => new SourceProfile(),
        };
    }

    /// <summary>IMFMediaSource of a file the shim reads.</summary>
    internal sealed unsafe class MFMediaSource : ComObject
    {
        private static readonly Vtable s_vt = Build();
        public readonly MediaContainer Container;
        internal readonly SourceProfile Profile;
        private readonly MFEventQueue _queue = new();
        private readonly MFPresentationDescriptor _pd = new();
        internal readonly List<MFMediaStream> Streams = new();       // one per stream descriptor, in its order
        internal readonly object Lock = new();
        private enum State { Stopped, Started, Paused, Shutdown }
        private State _state;
        private bool _endSent;

        private static Vtable Build()
        {
            var vt = new Vtable("IMFMediaSource", 13, MS.IID_IMFMediaSource, MS.IID_IMFMediaEventGenerator);
            vt.Set(3, (delegate* unmanaged<IntPtr, uint, IntPtr*, int>)&GetEvent);
            vt.Set(4, (delegate* unmanaged<IntPtr, IntPtr, IntPtr, int>)&BeginGetEvent);
            vt.Set(5, (delegate* unmanaged<IntPtr, IntPtr, IntPtr*, int>)&EndGetEvent);
            vt.Set(6, (delegate* unmanaged<IntPtr, uint, Guid*, int, byte*, int>)&QueueEvent);
            vt.Set(7, (delegate* unmanaged<IntPtr, uint*, int>)&GetCharacteristics);
            vt.Set(8, (delegate* unmanaged<IntPtr, IntPtr*, int>)&CreatePresentationDescriptor);
            vt.Set(9, (delegate* unmanaged<IntPtr, IntPtr, Guid*, byte*, int>)&Start);
            vt.Set(10, (delegate* unmanaged<IntPtr, int>)&Stop);
            vt.Set(11, (delegate* unmanaged<IntPtr, int>)&Pause);
            vt.Set(12, (delegate* unmanaged<IntPtr, int>)&ShutdownSlot);
            return vt;
        }

        public MFMediaSource(MediaContainer c) : base(s_vt)
        {
            Container = c;
            Profile = SourceProfile.For(c);
            foreach (var k in MS.PresentationKeys) if (c.Presentation(k) is { } v) _pd.Attributes.Set(k, v);
            foreach (var t in c.SourceOrder)
            {
                var native = c.Native(t);
                if (native == null) continue;
                var sd = new MFStreamDescriptor(c.StreamId(t, Streams.Count), new[] { native }.Concat(c.MoreTypes(t, native)).ToList());
                c.Describe(t, sd.Attributes);
                if (c.TypeCurrent) { native.AddRef(); sd.Handler.Current = native; }
                _pd.Add(sd.Pointer, true);
                Streams.Add(new MFMediaStream(this, t, sd, sd.Id));
                sd.Release();
            }
        }

        /// <summary>A source of the file, or the HRESULT Windows' resolver answers with.</summary>
        public static int Create(IByteSource src, out MFMediaSource? source)
        {
            source = null;
            MediaContainer? d;
            try { d = MediaContainer.Open(src); }
            catch (Exception e) { src.Dispose(); D3DLog.Once("mf-file:" + e.GetType().Name, "Media Foundation: the file is not read: " + e.Message); return SR.MF_E_UNSUPPORTED_FORMAT; }
            if (d == null) { src.Dispose(); return SR.MF_E_UNSUPPORTED_BYTESTREAM_TYPE; }
            source = new MFMediaSource(d);
            return MF.S_OK;
        }

        protected override void OnFinalRelease()
        {
            Shutdown();
            _pd.Release();
            foreach (var st in Streams) st.Release();
        }

        public bool IsShutdown { get { lock (Lock) return _state == State.Shutdown; } }

        public void Shutdown()
        {
            lock (Lock)
            {
                if (_state == State.Shutdown) return;
                _state = State.Shutdown;
                _queue.Shutdown();
                foreach (var s in Streams) s.Shut();
            }
            Container.Dispose();
        }

        private static MFMediaSource? S(IntPtr self) => From<MFMediaSource>(self);

        internal void Queue(uint type, MFValue? value = null, int status = MF.S_OK) => _queue.QueueEvent(type, Guid.Empty, status, value);

        /// <summary>MEEndOfPresentation, once every selected stream has ended.</summary>
        internal void CheckEnd()
        {
            if (_endSent) return;
            var active = Streams.Where(s => s.Active).ToList();
            if (active.Count == 0 || active.Any(s => !s.EndSent)) return;
            _endSent = true;
            Queue(MS.MEEndOfPresentation);
        }

        internal int RequestState => _state switch { State.Shutdown => MF.MF_E_SHUTDOWN, State.Stopped => Profile.StoppedRequest, State.Paused => 1, _ => 0 };

        // ---- IMFMediaEventGenerator ------------------------------------------------------------------------

        [UnmanagedCallersOnly]
        private static int GetEvent(IntPtr self, uint flags, IntPtr* e)
        {
            if (S(self) is not { } s || e == null) return MF.E_POINTER;
            int hr = s._queue.GetEvent((flags & 1) != 0, out var ev);
            *e = ev;
            return hr;
        }

        [UnmanagedCallersOnly] private static int BeginGetEvent(IntPtr self, IntPtr callback, IntPtr state) => S(self)?._queue.BeginGetEvent(callback, state) ?? MF.E_POINTER;

        [UnmanagedCallersOnly]
        private static int EndGetEvent(IntPtr self, IntPtr result, IntPtr* e)
        {
            if (S(self) is not { } s || e == null) return MF.E_POINTER;
            int hr = s._queue.EndGetEvent(result, out var ev);
            *e = ev;
            return hr;
        }

        [UnmanagedCallersOnly]
        private static int QueueEvent(IntPtr self, uint type, Guid* ext, int status, byte* value)
            => S(self)?._queue.QueueEvent(type, ext == null ? Guid.Empty : *ext, status, MFMediaEvent.ValueOf(value)) ?? MF.E_POINTER;

        // ---- IMFMediaSource --------------------------------------------------------------------------------

        [UnmanagedCallersOnly]
        private static int GetCharacteristics(IntPtr self, uint* c)
        {
            if (S(self) is not { } s || c == null) return MF.E_POINTER;
            if (s.IsShutdown) return MF.MF_E_SHUTDOWN;
            *c = s.Container.Characteristics;
            return MF.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int CreatePresentationDescriptor(IntPtr self, IntPtr* pd)
        {
            if (S(self) is not { } s || pd == null) return MF.E_POINTER;
            *pd = IntPtr.Zero;
            if (s.IsShutdown) return MF.MF_E_SHUTDOWN;
            *pd = s._pd.CloneOf().Pointer;
            return MF.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int Start(IntPtr self, IntPtr pd, Guid* format, byte* position)
        {
            if (S(self) is not { } s) return MF.E_POINTER;
            try { return s.StartAt(pd, format, position); }
            catch (Exception e) { D3DLog.Error("IMFMediaSource.Start", e); return MF.E_FAIL; }
        }

        private int StartAt(IntPtr pd, Guid* format, byte* position)
        {
            lock (Lock)
            {
                if (_state == State.Shutdown) return MF.MF_E_SHUTDOWN;
                if (pd == IntPtr.Zero) return MF.E_INVALIDARG;
                if (format != null && *format != Guid.Empty) return MF.E_INVALIDARG;
                ushort vt = position == null ? (ushort)0 : *(ushort*)position;
                if (vt != 0 && vt != 20) return MF.MF_E_INVALIDTYPE;
                long? at = vt == 20 ? *(long*)(position + 8) : null;
                if (at is long a0 && a0 != 0 && !Profile.Seeks) return SR.MF_E_INVALIDREQUEST;
                // the streams the descriptor selects, by their identifiers
                var selected = new HashSet<uint>();
                uint n;
                ((delegate* unmanaged<IntPtr, uint*, int>)Com.Fn(pd, 33))(pd, &n);
                for (uint i = 0; i < n; i++)
                {
                    int sel; IntPtr sd;
                    if (((delegate* unmanaged<IntPtr, uint, int*, IntPtr*, int>)Com.Fn(pd, 34))(pd, i, &sel, &sd) < 0) continue;
                    uint id;
                    ((delegate* unmanaged<IntPtr, uint*, int>)Com.Fn(sd, 33))(sd, &id);
                    Com.Release(sd);
                    if (sel != 0) selected.Add(id);
                }
                bool resume = at == null && _state == State.Paused;
                bool seek = at != null && _state != State.Stopped;
                long pos = at ?? 0;
                bool position0 = !resume && (at != null || _state == State.Stopped);
                foreach (var s in Streams)
                {
                    bool on = selected.Contains(s.Id);
                    if (!on) { s.Active = false; continue; }
                    bool was = s.Created;
                    s.Active = true;
                    s.Created = true;
                    Com.AddRef(s.Pointer);
                    Queue(was ? MS.MEUpdatedStream : MS.MENewStream, new MFValue { Type = MF.VT_UNKNOWN, Unknown = s.Pointer });
                }
                MFValue? Time(bool give) => give && !resume ? new MFValue { Type = 20, U = (ulong)pos } : null;
                _state = State.Started;
                _endSent = false;
                Queue(seek ? MS.MESourceSeeked : MS.MESourceStarted, Time(seek ? Profile.SeekedTime : Profile.StartedTime));
                foreach (var s in Streams.Where(s => s.Active))
                {
                    if (position0) s.Position(pos);
                    s.Queue(seek ? MS.MEStreamSeeked : MS.MEStreamStarted, Time(seek ? Profile.SeekedTime : Profile.StreamStartedTime));
                }
                // the requests held, and the end of a stream with nothing left
                foreach (var s in Streams.Where(s => s.Active)) s.Resume();
                CheckEnd();
            }
            return MF.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int Stop(IntPtr self)
        {
            if (S(self) is not { } s) return MF.E_POINTER;
            lock (s.Lock)
            {
                if (s._state == State.Shutdown) return MF.MF_E_SHUTDOWN;
                s._state = State.Stopped;
                s.Queue(MS.MESourceStopped);
                foreach (var st in s.Streams.Where(x => x.Active)) st.Stopped();
            }
            return MF.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int Pause(IntPtr self)
        {
            if (S(self) is not { } s) return MF.E_POINTER;
            lock (s.Lock)
            {
                if (s._state == State.Shutdown) return MF.MF_E_SHUTDOWN;
                if (s._state != State.Started) return MS.MF_E_MEDIA_SOURCE_WRONGSTATE;
                s._state = State.Paused;
                s.Queue(MS.MESourcePaused);
                foreach (var st in s.Streams.Where(x => x.Active)) st.Queue(MS.MEStreamPaused);
            }
            return MF.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int ShutdownSlot(IntPtr self)
        {
            if (S(self) is not { } s) return MF.E_POINTER;
            if (s.IsShutdown) return s.Profile.ShutdownAgain;
            s.Shutdown();
            return MF.S_OK;
        }
    }

    /// <summary>IMFMediaStream: a stream of a file's source.</summary>
    internal sealed unsafe class MFMediaStream : ComObject
    {
        private static readonly Vtable s_vt = Build();
        private readonly MFMediaSource _source;
        private readonly MFEventQueue _queue = new();
        private readonly MFStreamDescriptor _sd;
        private readonly TrackCursor _cursor;
        private readonly Queue<IntPtr> _held = new();        // tokens of the requests held while paused (AddRef'd; zero of none)
        private bool _first = true;
        private bool _shut;
        public readonly uint Id;
        public bool Created, Active, EndSent;

        private static Vtable Build()
        {
            var vt = new Vtable("IMFMediaStream", 10, MS.IID_IMFMediaStream, MS.IID_IMFMediaEventGenerator);
            vt.Set(3, (delegate* unmanaged<IntPtr, uint, IntPtr*, int>)&GetEvent);
            vt.Set(4, (delegate* unmanaged<IntPtr, IntPtr, IntPtr, int>)&BeginGetEvent);
            vt.Set(5, (delegate* unmanaged<IntPtr, IntPtr, IntPtr*, int>)&EndGetEvent);
            vt.Set(6, (delegate* unmanaged<IntPtr, uint, Guid*, int, byte*, int>)&QueueEvent);
            vt.Set(7, (delegate* unmanaged<IntPtr, IntPtr*, int>)&GetMediaSource);
            vt.Set(8, (delegate* unmanaged<IntPtr, IntPtr*, int>)&GetStreamDescriptor);
            vt.Set(9, (delegate* unmanaged<IntPtr, IntPtr, int>)&RequestSample);
            return vt;
        }

        public MFMediaStream(MFMediaSource source, Mp4Track t, MFStreamDescriptor sd, uint id) : base(s_vt)
        {
            _source = source; Id = id; _cursor = new TrackCursor(source.Container, t);
            sd.AddRef();
            _sd = sd;
        }

        protected override void OnFinalRelease()
        {
            Shut();
            _sd.Release();
        }

        internal void Shut()
        {
            if (_shut) return;
            _shut = true;
            _queue.Shutdown();
            while (_held.Count > 0) Com.Release(_held.Dequeue());
        }

        internal void Queue(uint type, MFValue? value = null) => _queue.QueueEvent(type, Guid.Empty, MF.S_OK, value);

        internal void Position(long hns)
        {
            _cursor.Seek(hns);
            _first = true;
            EndSent = false;
            while (_held.Count > 0) Com.Release(_held.Dequeue());
        }

        internal void Stopped()
        {
            Queue(MS.MEStreamStopped);
            while (_held.Count > 0) Com.Release(_held.Dequeue());
        }

        /// <summary>Started again: the requests held answered; a stream with nothing left ended at once.</summary>
        internal void Resume()
        {
            _endTold = false;
            while (_held.Count > 0)
            {
                IntPtr token = _held.Dequeue();
                Deliver(token);
                Com.Release(token);
            }
            if (_cursor.AtEnd && !_endTold && _source.Profile.EndsOnRestart) End();     // (again, where it was told before)
        }

        private bool _endTold;

        private void End()
        {
            EndSent = true;
            _endTold = true;
            Queue(MS.MEEndOfStream);
            _source.CheckEnd();
        }

        /// <summary>The next sample, as MEMediaSample (the end, for the request after the last).</summary>
        private void Deliver(IntPtr token)
        {
            if (_cursor.AtEnd) { if (!EndSent) End(); return; }
            var t = _cursor.Track;
            var s = ContainerSamples.Make(_source.Container, t, t.Samples[_cursor.Take()], _first, _source.Profile.PerNal);
            _first = false;
            if (token != IntPtr.Zero || _source.Profile.TokenAlways)
            {
                Com.AddRef(token);
                s.Attributes.Set(MS.MFSampleExtension_Token, new MFValue { Type = MF.VT_UNKNOWN, Unknown = token });
            }
            Queue(MS.MEMediaSample, new MFValue { Type = MF.VT_UNKNOWN, Unknown = s.Pointer });       // the event's reference
            if (_source.Profile.EndsWithLast && _cursor.AtEnd) End();
        }

        private static MFMediaStream? M(IntPtr self) => From<MFMediaStream>(self);

        [UnmanagedCallersOnly]
        private static int GetEvent(IntPtr self, uint flags, IntPtr* e)
        {
            if (M(self) is not { } m || e == null) return MF.E_POINTER;
            int hr = m._queue.GetEvent((flags & 1) != 0, out var ev);
            *e = ev;
            return hr;
        }

        [UnmanagedCallersOnly] private static int BeginGetEvent(IntPtr self, IntPtr callback, IntPtr state) => M(self)?._queue.BeginGetEvent(callback, state) ?? MF.E_POINTER;

        [UnmanagedCallersOnly]
        private static int EndGetEvent(IntPtr self, IntPtr result, IntPtr* e)
        {
            if (M(self) is not { } m || e == null) return MF.E_POINTER;
            int hr = m._queue.EndGetEvent(result, out var ev);
            *e = ev;
            return hr;
        }

        [UnmanagedCallersOnly]
        private static int QueueEvent(IntPtr self, uint type, Guid* ext, int status, byte* value)
            => M(self)?._queue.QueueEvent(type, ext == null ? Guid.Empty : *ext, status, MFMediaEvent.ValueOf(value)) ?? MF.E_POINTER;

        [UnmanagedCallersOnly]
        private static int GetMediaSource(IntPtr self, IntPtr* source)
        {
            if (M(self) is not { } m || source == null) return MF.E_POINTER;
            *source = IntPtr.Zero;
            if (m._source.IsShutdown) return MF.MF_E_SHUTDOWN;
            *source = m._source.AddRefPointer();
            return MF.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int GetStreamDescriptor(IntPtr self, IntPtr* sd)
        {
            if (M(self) is not { } m || sd == null) return MF.E_POINTER;
            *sd = IntPtr.Zero;
            if (m._source.IsShutdown) return MF.MF_E_SHUTDOWN;
            *sd = m._sd.AddRefPointer();
            return MF.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int RequestSample(IntPtr self, IntPtr token)
        {
            if (M(self) is not { } m) return MF.E_POINTER;
            try
            {
                lock (m._source.Lock)
                {
                    int state = m._source.RequestState;
                    if (state == MF.MF_E_SHUTDOWN) return state;
                    if (!m.Active) return m._source.Profile.DeselectedRequest;
                    if (state < 0) return state;
                    if (m.EndSent || m._source.Profile.EndsWithLast && m._held.Count >= m._cursor.Remaining) return MS.MF_E_END_OF_STREAM;
                    if (state == 1) { Com.AddRef(token); m._held.Enqueue(token); return MF.S_OK; }          // paused: answered when started
                    m.Deliver(token);
                }
                return MF.S_OK;
            }
            catch (Exception e) { D3DLog.Error("IMFMediaStream.RequestSample", e); return MF.E_FAIL; }
        }
    }

    /// <summary>IMFSourceResolver: media sources (and byte streams) of URLs and of byte streams.</summary>
    internal sealed unsafe class MFSourceResolver : ComObject
    {
        private static readonly Vtable s_vt = Build();

        private static Vtable Build()
        {
            var vt = new Vtable("IMFSourceResolver", 10, MS.IID_IMFSourceResolver);
            vt.Set(3, (delegate* unmanaged<IntPtr, char*, uint, IntPtr, uint*, IntPtr*, int>)&CreateObjectFromURL);
            vt.Set(4, (delegate* unmanaged<IntPtr, IntPtr, char*, uint, IntPtr, uint*, IntPtr*, int>)&CreateObjectFromByteStream);
            vt.Set(5, (delegate* unmanaged<IntPtr, char*, uint, IntPtr, IntPtr*, IntPtr, IntPtr, int>)&BeginCreateObjectFromURL);
            vt.Set(6, (delegate* unmanaged<IntPtr, IntPtr, uint*, IntPtr*, int>)&EndCreateObject);
            vt.Set(7, (delegate* unmanaged<IntPtr, IntPtr, char*, uint, IntPtr, IntPtr*, IntPtr, IntPtr, int>)&BeginCreateObjectFromByteStream);
            vt.Set(8, (delegate* unmanaged<IntPtr, IntPtr, uint*, IntPtr*, int>)&EndCreateObject);
            vt.Set(9, (delegate* unmanaged<IntPtr, IntPtr, int>)&CancelObjectCreation);
            return vt;
        }

        public MFSourceResolver() : base(s_vt) { }

        /// <summary>The object of the URL: its byte stream, where that is what is asked for alone; else a media source of it.</summary>
        public static int FromUrl(string url, uint flags, out uint type, out IntPtr obj)
        {
            type = MS.MF_OBJECT_INVALID; obj = IntPtr.Zero;
            if ((flags & MS.MF_RESOLUTION_BYTESTREAM) != 0 && (flags & MS.MF_RESOLUTION_MEDIASOURCE) == 0)
            {
                IntPtr bs;
                int bhr;
                fixed (char* p = url) bhr = MFExports.MFCreateFile(1, 0, 0, p, &bs);
                if (bhr < 0) return bhr;
                type = MS.MF_OBJECT_BYTESTREAM; obj = bs;
                return MF.S_OK;
            }
            int hr = MFExports.OpenUrl(url, out var src);
            if (hr < 0) return hr;
            hr = MFMediaSource.Create(src!, out var source);
            if (hr < 0) return hr;
            type = MS.MF_OBJECT_MEDIASOURCE; obj = source!.Pointer;
            return MF.S_OK;
        }

        public static int FromByteStream(IntPtr byteStream, out uint type, out IntPtr obj)
        {
            type = MS.MF_OBJECT_INVALID; obj = IntPtr.Zero;
            if (byteStream == IntPtr.Zero) return MF.E_POINTER;
            IByteSource src;
            try { src = From<MFFileByteStream>(byteStream) is { Path: { } fp } ? new FileByteSource(fp) : new ForeignByteSource(byteStream); }
            catch (Exception e) { return MFFileByteStream.Hr(e); }
            int hr = MFMediaSource.Create(src, out var source);
            if (hr < 0) return hr;
            type = MS.MF_OBJECT_MEDIASOURCE; obj = source!.Pointer;
            return MF.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int CreateObjectFromURL(IntPtr self, char* url, uint flags, IntPtr props, uint* type, IntPtr* obj)
        {
            if (url == null || type == null || obj == null) return MF.E_POINTER;
            *obj = IntPtr.Zero;
            try
            {
                int hr = FromUrl(new string(url), flags, out var t, out var o);
                if (hr >= 0) { *type = t; *obj = o; }
                return hr;
            }
            catch (Exception e) { D3DLog.Error("IMFSourceResolver.CreateObjectFromURL", e); return MF.E_FAIL; }
        }

        [UnmanagedCallersOnly]
        private static int CreateObjectFromByteStream(IntPtr self, IntPtr byteStream, char* url, uint flags, IntPtr props, uint* type, IntPtr* obj)
        {
            if (type == null || obj == null) return MF.E_POINTER;
            *obj = IntPtr.Zero;
            try
            {
                int hr = FromByteStream(byteStream, out var t, out var o);
                if (hr >= 0) { *type = t; *obj = o; }
                return hr;
            }
            catch (Exception e) { D3DLog.Error("IMFSourceResolver.CreateObjectFromByteStream", e); return MF.E_FAIL; }
        }

        /// <summary>The asynchronous forms: the object made at once, the callback invoked on the thread pool with it (or the failure).</summary>
        private static int Later(int hr, IntPtr obj, IntPtr* cancel, IntPtr callback, IntPtr state)
        {
            if (cancel != null) *cancel = IntPtr.Zero;
            if (callback == IntPtr.Zero) { Com.Release(obj); return MF.E_POINTER; }
            var r = new MFAsyncResult(obj, callback, state) { Status = hr };
            Com.Release(obj);
            r.InvokeLater();
            return MF.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int BeginCreateObjectFromURL(IntPtr self, char* url, uint flags, IntPtr props, IntPtr* cancel, IntPtr callback, IntPtr state)
        {
            if (url == null) return MF.E_POINTER;
            int hr;
            IntPtr o;
            try { hr = FromUrl(new string(url), flags, out _, out o); }
            catch (Exception e) { D3DLog.Error("IMFSourceResolver.BeginCreateObjectFromURL", e); hr = MF.E_FAIL; o = IntPtr.Zero; }
            return Later(hr, o, cancel, callback, state);
        }

        [UnmanagedCallersOnly]
        private static int BeginCreateObjectFromByteStream(IntPtr self, IntPtr byteStream, char* url, uint flags, IntPtr props, IntPtr* cancel, IntPtr callback, IntPtr state)
        {
            int hr;
            IntPtr o;
            try { hr = FromByteStream(byteStream, out _, out o); }
            catch (Exception e) { D3DLog.Error("IMFSourceResolver.BeginCreateObjectFromByteStream", e); hr = MF.E_FAIL; o = IntPtr.Zero; }
            return Later(hr, o, cancel, callback, state);
        }

        [UnmanagedCallersOnly]
        private static int EndCreateObject(IntPtr self, IntPtr result, uint* type, IntPtr* obj)
        {
            if (result == IntPtr.Zero || type == null || obj == null) return MF.E_POINTER;
            *obj = IntPtr.Zero; *type = MS.MF_OBJECT_INVALID;
            int hr = ((delegate* unmanaged<IntPtr, int>)Com.Fn(result, 4))(result);                   // IMFAsyncResult::GetStatus
            if (hr < 0) return hr;
            IntPtr o;
            hr = ((delegate* unmanaged<IntPtr, IntPtr*, int>)Com.Fn(result, 6))(result, &o);           // GetObject
            if (hr < 0) return hr;
            *type = From<MFMediaSource>(o) != null ? MS.MF_OBJECT_MEDIASOURCE : MS.MF_OBJECT_BYTESTREAM;
            *obj = o;
            return MF.S_OK;
        }

        [UnmanagedCallersOnly] private static int CancelObjectCreation(IntPtr self, IntPtr cookie) => MF.S_OK;
    }
}
