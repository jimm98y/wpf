// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The media session (MFCreateMediaSession; Documentation/media-foundation-shim.md, "The media session"), as Windows'
// behaves (MFMediaSessionProbe, MediaSession.windows.txt). Its calls are queued and done in order on the session's
// thread, which also moves the samples: each stream sink's MEStreamSinkRequestSample answered by the next sample of
// its branch -- the source stream's (a RequestSample and its MEMediaSample), through the branch's transforms --, the
// source's end drained through them and marked (MFSTREAMSINK_MARKER_ENDOFSEGMENT); the presentation clock the sinks
// are given, started, paused and stopped as the session is. Its events, of Windows' order:
//
//   - SetTopology: MESessionTopologySet (the full topology), MESessionNotifyPresentationTime, MESessionCapabilitiesChanged
//     (start, seek, the rates, no network: 0x73), MESessionTopologyStatus READY;
//   - the first Start: TOPOSTATUS_STARTED_SOURCE; each Start: the capabilities with pause (0x77), MESessionStarted (its
//     MF_EVENT_PRESENTATION_TIME_OFFSET); Pause: the capabilities without it, MESessionPaused; Stop: MESessionStopped
//     (MF_SESSION_APPROX_EVENT_OCCURRENCE_TIME);
//   - the end of every branch: MEEndOfPresentation, TOPOSTATUS_ENDED, the capabilities, MESessionEnded, the clock stopped;
//   - Close: the capabilities of neither start nor seek (0x70), MESessionClosed; Shutdown: the sinks shut down, every
//     call after it MF_E_SHUTDOWN.
//

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Wpf.Interop.WebGpu.Direct3D;
using Microsoft.Wpf.Interop.WebGpu.MediaFoundation.Sink;
using Microsoft.Wpf.Interop.WebGpu.MediaFoundation.Source;

namespace Microsoft.Wpf.Interop.WebGpu.MediaFoundation.Session
{
    internal sealed unsafe class MFMediaSession : ComObject
    {
        public static readonly Guid IID_IMFMediaSession = new("90377834-21d0-4dee-8214-ba2e3e6c1127"), IID_IMFGetService = new("fa993888-4383-415a-a930-dd472a8cf6f7");
        private static readonly Guid IID_IMFMediaEventGenerator = new("2cd0bd52-bcd5-4b89-b62c-eadc0c031e7d"), IID_IMFMediaSource = new("279a808d-aec7-40c8-9c6b-a6b492c78a66");
        private static readonly Guid IID_IMFMediaStream = new("d182108f-4ec6-443f-aa42-a71106ec825f");
        private static readonly Guid MF_EVENT_TOPOLOGY_STATUS = new("30c5018d-9a53-454b-ad9e-6d5f8fa7c43b"), MF_EVENT_SESSIONCAPS = new("7e5ebcd0-11b8-4abe-afad-10f6599a7f42");
        private static readonly Guid MF_EVENT_SESSIONCAPS_DELTA = new("7e5ebcd1-11b8-4abe-afad-10f6599a7f42"), MF_EVENT_START_PRESENTATION_TIME = new("5ad914d0-9b45-4a8d-a2c0-81d1e50bfb07");
        private static readonly Guid MF_EVENT_PRESENTATION_TIME_OFFSET = new("5ad914d1-9b45-4a8d-a2c0-81d1e50bfb07"), MF_EVENT_START_PRESENTATION_TIME_AT_OUTPUT = new("5ad914d2-9b45-4a8d-a2c0-81d1e50bfb07");
        private static readonly Guid MF_SESSION_APPROX_EVENT_OCCURRENCE_TIME = new("190e852f-6238-42d1-b5af-69ea338ef850");
        public const uint MESessionTopologySet = 101, MESessionTopologiesCleared = 102, MESessionStarted = 103, MESessionPaused = 104, MESessionStopped = 105;
        public const uint MESessionClosed = 106, MESessionEnded = 107, MESessionCapabilitiesChanged = 110, MESessionTopologyStatus = 111, MESessionNotifyPresentationTime = 112;
        public const uint MEEndOfPresentation = 211, MEExtendedType = 2;
        public const uint TOPOSTATUS_READY = 100, TOPOSTATUS_STARTED_SOURCE = 200, TOPOSTATUS_ENDED = 400;
        private const uint CAP_START = 1, CAP_SEEK = 2, CAP_PAUSE = 4, CAP_RATE_FORWARD = 0x10, CAP_RATE_REVERSE = 0x20, CAP_NO_NETWORK = 0x40;
        public const int MF_E_INVALIDREQUEST = unchecked((int)0xC00D36B2), MF_E_SESSION_SHUTDOWN = unchecked((int)0xC00D3E85);
        private const uint MFT_MESSAGE_COMMAND_FLUSH = 0, MFT_MESSAGE_COMMAND_DRAIN = 1, MFT_MESSAGE_NOTIFY_BEGIN_STREAMING = 0x10000000, MFT_MESSAGE_NOTIFY_END_STREAMING = 0x10000001;
        private const uint MFT_MESSAGE_NOTIFY_END_OF_STREAM = 0x10000002, MFT_MESSAGE_NOTIFY_START_OF_STREAM = 0x10000003;

        private static readonly Vtable s_vt = Build(), s_serviceVt = BuildService();
        private readonly MFEventQueue _queue = new();
        internal readonly MFPresentationClock Clock = new() { NoTimeSource = true };
        private readonly BlockingCollection<Action> _commands = new();
        private readonly Thread _thread;
        private readonly object _lock = new();
        private volatile bool _shutdown, _closed;
        private uint _caps;

        // the topology being played and its pipeline (the session's thread's)
        private MFTopology? _full;
        private readonly List<Branch> _branches = new();
        private readonly List<IntPtr> _sources = new(), _sinks = new(), _transforms = new();      // AddRef'd
        private readonly Dictionary<IntPtr, IntPtr> _pds = new();                                 // a source's presentation descriptor (AddRef'd)
        private enum State { Empty, Ready, Started, Paused, Stopped, Ended }
        private State _state;
        private bool _sourceStarted, _streaming, _endSent;

        private sealed class Branch
        {
            public IntPtr Source, Stream, StreamSink;          // the source's (not AddRef'd: _sources'), its stream, the stream sink (AddRef'd)
            public uint StreamId;
            public readonly List<IntPtr> Mfts = new();         // in order (AddRef'd: _transforms')
            public readonly Queue<IntPtr> Samples = new();     // the source's samples not taken (AddRef'd)
            public int Requests;                                // the sink's requests not answered
            public bool Asked, SourceEnded, MarkerPlaced, Done, WaitStarted;
            public bool[] Drained = Array.Empty<bool>();
        }

        private static Vtable Build()
        {
            var vt = new Vtable("IMFMediaSession", 17, IID_IMFMediaSession, IID_IMFMediaEventGenerator);
            vt.Set(3, (delegate* unmanaged<IntPtr, uint, IntPtr*, int>)&GetEvent);
            vt.Set(4, (delegate* unmanaged<IntPtr, IntPtr, IntPtr, int>)&BeginGetEvent);
            vt.Set(5, (delegate* unmanaged<IntPtr, IntPtr, IntPtr*, int>)&EndGetEvent);
            vt.Set(6, (delegate* unmanaged<IntPtr, uint, Guid*, int, byte*, int>)&QueueEvent);
            vt.Set(7, (delegate* unmanaged<IntPtr, uint, IntPtr, int>)&SetTopology);
            vt.Set(8, (delegate* unmanaged<IntPtr, int>)&ClearTopologies);
            vt.Set(9, (delegate* unmanaged<IntPtr, Guid*, byte*, int>)&Start);
            vt.Set(10, (delegate* unmanaged<IntPtr, int>)&Pause);
            vt.Set(11, (delegate* unmanaged<IntPtr, int>)&Stop);
            vt.Set(12, (delegate* unmanaged<IntPtr, int>)&Close);
            vt.Set(13, (delegate* unmanaged<IntPtr, int>)&ShutdownSlot);
            vt.Set(14, (delegate* unmanaged<IntPtr, IntPtr*, int>)&GetClock);
            vt.Set(15, (delegate* unmanaged<IntPtr, uint*, int>)&GetSessionCapabilities);
            vt.Set(16, (delegate* unmanaged<IntPtr, uint, ulong, IntPtr*, int>)&GetFullTopology);
            return vt;
        }

        private static Vtable BuildService()
        {
            var vt = new Vtable("IMFGetService", 4, IID_IMFGetService);
            vt.Set(3, (delegate* unmanaged<IntPtr, Guid*, Guid*, IntPtr*, int>)&GetServiceSlot);
            return vt;
        }

        public MFMediaSession() : base(s_vt, s_serviceVt)
        {
            _thread = new Thread(Run) { IsBackground = true, Name = "Media session" };
            _thread.Start();
        }

        protected override void OnFinalRelease()
        {
            Shutdown();
            Clock.Release();
        }

        private void Queue(uint type, int status = MF.S_OK, MFValue? value = null, params (Guid, MFValue)[] attrs)
        {
            var e = new MFMediaEvent(type, Guid.Empty, status, value);
            foreach (var (k, v) in attrs) e.Attributes.Set(k, v);
            _queue.QueueEvent(e.Pointer);
            e.Release();
        }

        private void Caps(uint caps)
        {
            uint delta = caps ^ _caps;
            _caps = caps;
            Queue(MESessionCapabilitiesChanged, MF.S_OK, null, (MF_EVENT_SESSIONCAPS, MFValue.Of(caps)), (MF_EVENT_SESSIONCAPS_DELTA, MFValue.Of(delta)));
        }

        private const uint CapsReady = CAP_START | CAP_SEEK | CAP_RATE_FORWARD | CAP_RATE_REVERSE | CAP_NO_NETWORK;

        // ---- the session's thread ---------------------------------------------------------------------------

        private void Run()
        {
            bool busy = false;
            while (!_shutdown)
            {
                if (_commands.TryTake(out var cmd, busy ? 0 : 2))
                {
                    try { cmd(); }
                    catch (Exception e) { D3DLog.Error("the media session", e); }
                }
                try { busy = Pump(); }
                catch (Exception e) { D3DLog.Error("the media session's samples", e); busy = false; }
            }
        }

        private int Command(Action a)
        {
            if (_shutdown) return MF_E_SESSION_SHUTDOWN;
            try { _commands.Add(a); }
            catch (InvalidOperationException) { return MF_E_SESSION_SHUTDOWN; }
            return MF.S_OK;
        }

        // ---- SetTopology --------------------------------------------------------------------------------------

        private void DoSetTopology(MFTopology partial)
        {
            try
            {
                Teardown();
                int hr = TopoLoader.Resolve(partial, out var full);
                if (hr < 0) { Queue(MESessionTopologySet, hr, new MFValue { Type = MF.VT_UNKNOWN, Unknown = partial.AddRefPointer() }); return; }
                _full = full!;
                hr = Build(_full);
                if (hr < 0) { Teardown(); Queue(MESessionTopologySet, hr, new MFValue { Type = MF.VT_UNKNOWN, Unknown = partial.AddRefPointer() }); return; }
                Clock.NoTimeSource = false;
                foreach (var s in _sinks) ((delegate* unmanaged<IntPtr, IntPtr, int>)Com.Fn(s, 9))(s, Clock.Pointer);     // IMFMediaSink::SetPresentationClock
                _state = State.Ready;
                _sourceStarted = false;
                Queue(MESessionTopologySet, MF.S_OK, new MFValue { Type = MF.VT_UNKNOWN, Unknown = _full.AddRefPointer() });
                Queue(MESessionNotifyPresentationTime, MF.S_OK, null, (MF_EVENT_START_PRESENTATION_TIME, MFValue.Of(0UL)), (MF_EVENT_PRESENTATION_TIME_OFFSET, MFValue.Of(0UL)), (MF_EVENT_START_PRESENTATION_TIME_AT_OUTPUT, MFValue.Of(0UL)));
                _caps = 0;
                Caps(CapsReady);
                Queue(MESessionTopologyStatus, MF.S_OK, new MFValue { Type = MF.VT_UNKNOWN, Unknown = _full.AddRefPointer() }, (MF_EVENT_TOPOLOGY_STATUS, MFValue.Of(TOPOSTATUS_READY)));
            }
            finally { partial.Release(); }
        }

        /// <summary>The branches of the full topology: each source node to its output, through its transforms.</summary>
        private int Build(MFTopology full)
        {
            foreach (var src in full.Nodes.Where(n => n.NodeType == TopoIds.SOURCESTREAM))
            {
                var b = new Branch();
                IntPtr source = src.Attributes.Get(TopoIds.MF_TOPONODE_SOURCE)!.Unknown, pd = src.Attributes.Get(TopoIds.MF_TOPONODE_PRESENTATION_DESCRIPTOR)!.Unknown;
                IntPtr sd = src.Attributes.Get(TopoIds.MF_TOPONODE_STREAM_DESCRIPTOR)!.Unknown;
                if (Com.QueryInterface(source, IID_IMFMediaSource, out var ms) < 0) return TopoLoader.MF_E_TOPO_MISSING_SOURCE;
                if (!_sources.Contains(ms)) { _sources.Add(ms); Com.AddRef(pd); _pds[ms] = pd; }
                else Com.Release(ms);
                b.Source = ms;
                uint id; ((delegate* unmanaged<IntPtr, uint*, int>)Com.Fn(sd, 33))(sd, &id);
                b.StreamId = id;
                for (var n = src.Down(0); n != null; n = n.Down(0))
                {
                    if (n.NodeType == TopoIds.TRANSFORM)
                    {
                        if (Com.QueryInterface(n.Object, new Guid("bf94c121-5b05-4e6f-8000-ba598961414d"), out var t) < 0) return MF.E_INVALIDARG;
                        _transforms.Add(t); b.Mfts.Add(t);
                        if (Mft.OutputType(t) is { } o) { _outSubtype[t] = o.Subtype; o.Release(); }
                    }
                    else if (n.NodeType == TopoIds.OUTPUT)
                    {
                        if (Com.QueryInterface(n.Object, SinkIds.IID_IMFStreamSink, out var ss) < 0) return MF.E_INVALIDARG;
                        b.StreamSink = ss;
                        IntPtr sink;
                        if (((delegate* unmanaged<IntPtr, IntPtr*, int>)Com.Fn(ss, 7))(ss, &sink) >= 0)
                        {
                            if (!_sinks.Contains(sink)) _sinks.Add(sink); else Com.Release(sink);
                        }
                        break;
                    }
                }
                if (b.StreamSink == IntPtr.Zero) return TopoLoader.MF_E_TOPO_UNSUPPORTED;
                b.Drained = new bool[b.Mfts.Count];
                _branches.Add(b);
            }
            return MF.S_OK;
        }

        /// <summary>The pipeline let go (the sinks and transforms not shut down: Shutdown does that).</summary>
        private void Teardown()
        {
            foreach (var b in _branches) { DropSamples(b); Com.Release(b.Stream); Com.Release(b.StreamSink); }
            _branches.Clear();
            foreach (var t in _transforms) Com.Release(t);
            _transforms.Clear();
            _outSubtype.Clear();
            _empty.Clear();
            foreach (var s in _sinks) Com.Release(s);
            _sinks.Clear();
            foreach (var (_, pd) in _pds) Com.Release(pd);
            _pds.Clear();
            foreach (var s in _sources) Com.Release(s);
            _sources.Clear();
            _full?.Release(); _full = null;
            _state = State.Empty;
            _streaming = false;
        }

        private static void DropSamples(Branch b) { while (b.Samples.Count > 0) Com.Release(b.Samples.Dequeue()); }

        // ---- Start, Pause, Stop, Close -----------------------------------------------------------------------

        private void DoStart(long? at)
        {
            if (_state == State.Empty) { Queue(MESessionStarted, MF_E_INVALIDREQUEST); return; }
            bool fromPaused = _state == State.Paused;
            bool seek = at != null && (_state == State.Started || _state == State.Paused);
            if (_state is State.Stopped or State.Ended || seek) Reset(flushSinks: true);
            if (!_streaming)
            {
                foreach (var t in _transforms) { Mft.ProcessMessage(t, MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, 0); Mft.ProcessMessage(t, MFT_MESSAGE_NOTIFY_START_OF_STREAM, 0); }
                _streaming = true;
            }
            // the sources, of the position (none: where they are, from a pause; else the beginning)
            long? pos = at ?? (fromPaused ? null : (_state is State.Stopped or State.Ended or State.Ready ? 0 : null));
            foreach (var s in _sources)
            {
                int hr = StartSource(s, pos);
                if (hr < 0) { Queue(MESessionStarted, hr); return; }
            }
            if (!_sourceStarted)
            {
                _sourceStarted = true;
                Queue(MESessionTopologyStatus, MF.S_OK, new MFValue { Type = MF.VT_UNKNOWN, Unknown = _full!.AddRefPointer() }, (MF_EVENT_TOPOLOGY_STATUS, MFValue.Of(TOPOSTATUS_STARTED_SOURCE)));
                foreach (var s in _sinks) RendererStarted(s);
            }
            _endSent = false;
            Clock.Start(fromPaused && at == null ? MFPresentationClock.PRESENTATION_CURRENT_POSITION : pos ?? 0);
            _state = State.Started;
            Caps(CapsReady | CAP_PAUSE);
            Queue(MESessionStarted, MF.S_OK, null, (MF_EVENT_PRESENTATION_TIME_OFFSET, MFValue.Of(0UL)));
        }

        /// <summary>A renderer's event as the source starts (the audio renderer's endpoint): what the sink tells, passed on.</summary>
        private void RendererStarted(IntPtr sink)
        {
            if (ComObject.From<ComObject>(sink) is IRendererEvents r)
                foreach (var (ext, value) in r.StartEvents()) { var e = new MFMediaEvent(MEExtendedType, ext, MF.S_OK, value); _queue.QueueEvent(e.Pointer); e.Release(); }
        }

        /// <summary>The source started at the position: its events to MESourceStarted (MESourceSeeked), its streams attached.</summary>
        private int StartSource(IntPtr source, long? at)
        {
            var pv = stackalloc byte[MF.PropVariantSize];
            new Span<byte>(pv, MF.PropVariantSize).Clear();
            if (at is long a) { *(ushort*)pv = 20; *(long*)(pv + 8) = a; }
            Guid fmt = Guid.Empty;
            IntPtr pd = _pds[source];
            // only the streams of a branch selected
            uint n; ((delegate* unmanaged<IntPtr, uint*, int>)Com.Fn(pd, 33))(pd, &n);
            for (uint i = 0; i < n; i++)
            {
                int sel; IntPtr sd;
                if (((delegate* unmanaged<IntPtr, uint, int*, IntPtr*, int>)Com.Fn(pd, 34))(pd, i, &sel, &sd) < 0) continue;
                uint sid; ((delegate* unmanaged<IntPtr, uint*, int>)Com.Fn(sd, 33))(sd, &sid);
                Com.Release(sd);
                uint id = sid;
                bool want = _branches.Exists(b => b.Source == source && b.StreamId == id);
                if (want != (sel != 0)) ((delegate* unmanaged<IntPtr, uint, int>)Com.Fn(pd, want ? 35 : 36))(pd, i);
            }
            foreach (var b in _branches.Where(b => b.Source == source)) { b.WaitStarted = true; b.Asked = false; }
            int hr = ((delegate* unmanaged<IntPtr, IntPtr, Guid*, byte*, int>)Com.Fn(source, 9))(source, pd, &fmt, pv);
            if (hr < 0) return hr;
            while (true)
            {
                var e = ForeignSource.Event(source, out hr);
                if (e == null) return hr < 0 ? hr : MF.E_FAIL;
                var (type, status, unk) = e.Value;
                try
                {
                    if (status < 0) return status;
                    if (type is MS.MENewStream or MS.MEUpdatedStream && unk != IntPtr.Zero && Com.QueryInterface(unk, IID_IMFMediaStream, out var stream) >= 0)
                    {
                        IntPtr sd; uint sid = uint.MaxValue;
                        if (((delegate* unmanaged<IntPtr, IntPtr*, int>)Com.Fn(stream, 8))(stream, &sd) >= 0) { ((delegate* unmanaged<IntPtr, uint*, int>)Com.Fn(sd, 33))(sd, &sid); Com.Release(sd); }
                        uint id = sid;
                        foreach (var b in _branches.Where(b => b.Source == source && b.StreamId == id))
                        {
                            if (b.Stream != stream) { Com.AddRef(stream); Com.Release(b.Stream); b.Stream = stream; }
                        }
                        Com.Release(stream);
                    }
                    else if (type is MS.MESourceStarted or MS.MESourceSeeked) return MF.S_OK;
                    else if (type == MS.MEError) return MF.E_FAIL;
                }
                finally { Com.Release(unk); }
            }
        }

        /// <summary>Every branch back to before its first sample: the samples dropped, the transforms and (where asked) the sinks flushed.</summary>
        private void Reset(bool flushSinks)
        {
            foreach (var b in _branches)
            {
                DropSamples(b);
                if (flushSinks) ((delegate* unmanaged<IntPtr, int>)Com.Fn(b.StreamSink, 12))(b.StreamSink);       // IMFStreamSink::Flush
                b.Asked = false; b.SourceEnded = false; b.MarkerPlaced = false; b.Done = false;
                Array.Clear(b.Drained);
            }
        }

        private void DoPause()
        {
            if (_state != State.Started) { Queue(MESessionPaused, MF_E_INVALIDREQUEST); return; }
            foreach (var s in _sources) ((delegate* unmanaged<IntPtr, int>)Com.Fn(s, 11))(s);
            Clock.Pause();
            _state = State.Paused;
            Caps(CapsReady);
            Queue(MESessionPaused);
        }

        private void DoStop()
        {
            if (_state is State.Empty or State.Ready) { Queue(MESessionStopped, MF_E_INVALIDREQUEST); return; }
            long at = Clock.Time;
            if (_state is State.Started or State.Paused)
            {
                foreach (var s in _sources) ((delegate* unmanaged<IntPtr, int>)Com.Fn(s, 10))(s);
                Clock.Stop();
            }
            Reset(flushSinks: true);
            _state = State.Stopped;
            Caps(CapsReady);
            Queue(MESessionStopped, MF.S_OK, null, (MF_SESSION_APPROX_EVENT_OCCURRENCE_TIME, MFValue.Of((ulong)at)));
        }

        private void DoClose()
        {
            if (_state is State.Started or State.Paused)
            {
                foreach (var s in _sources) ((delegate* unmanaged<IntPtr, int>)Com.Fn(s, 10))(s);
                Clock.Stop();
                Reset(flushSinks: true);
            }
            if (_streaming) foreach (var t in _transforms) Mft.ProcessMessage(t, MFT_MESSAGE_NOTIFY_END_STREAMING, 0);
            _streaming = false;
            _closed = true;
            _state = _state == State.Empty ? State.Empty : State.Stopped;
            Caps(CapsReady & ~(CAP_START | CAP_SEEK));
            Queue(MESessionClosed);
        }

        // ---- the samples --------------------------------------------------------------------------------------

        /// <summary>The events of the sources, streams and stream sinks taken, the requests answered; whether anything moved.</summary>
        private bool Pump()
        {
            if (_branches.Count == 0) return false;
            bool moved = false;
            foreach (var s in _sources) while (Take(s, out var type, out _, out var unk, out _)) { moved = true; Com.Release(unk); if (type == MS.MEError) D3DLog.Once("session-source-error", "Media Foundation: the media source's MEError"); }
            foreach (var b in _branches)
            {
                if (b.Stream != IntPtr.Zero)
                    while (Take(b.Stream, out var type, out var status, out var unk, out _))
                    {
                        moved = true;
                        switch (type)
                        {
                            case MS.MEStreamStarted or MS.MEStreamSeeked: b.WaitStarted = false; break;
                            case MS.MEMediaSample:
                                if (unk != IntPtr.Zero && !b.WaitStarted && _state is State.Started or State.Paused) { Com.AddRef(unk); b.Samples.Enqueue(unk); }
                                b.Asked = false;
                                break;
                            case MS.MEEndOfStream: if (!b.WaitStarted) b.SourceEnded = true; b.Asked = false; break;
                        }
                        Com.Release(unk);
                    }
                while (Take(b.StreamSink, out var stype, out _, out var sunk, out _))
                {
                    moved = true;
                    if (stype == SinkIds.MEStreamSinkRequestSample) b.Requests++;
                    else if (stype == SinkIds.MEStreamSinkMarker && b.MarkerPlaced) b.Done = true;
                    else if (stype == SinkIds.MEStreamSinkStopped) b.Requests = 0;
                    Com.Release(sunk);
                }
                if (_state == State.Started && Feed(b)) moved = true;
            }
            // every branch ended and rendered: the end
            if (_state == State.Started && !_endSent && _branches.All(b => b.Done))
            {
                _endSent = true;
                long at = Clock.Time;
                Queue(MEEndOfPresentation);
                Queue(MESessionTopologyStatus, MF.S_OK, new MFValue { Type = MF.VT_UNKNOWN, Unknown = _full!.AddRefPointer() }, (MF_EVENT_TOPOLOGY_STATUS, MFValue.Of(TOPOSTATUS_ENDED)));
                Caps(CapsReady);
                Queue(MESessionEnded, MF.S_OK, null, (MF_SESSION_APPROX_EVENT_OCCURRENCE_TIME, MFValue.Of((ulong)at)));
                foreach (var s in _sources) ((delegate* unmanaged<IntPtr, int>)Com.Fn(s, 10))(s);
                Clock.Stop();
                _state = State.Ended;
                moved = true;
            }
            return moved;
        }

        private static bool Take(IntPtr gen, out uint type, out int status, out IntPtr unk, out long i8)
        {
            IntPtr e;
            type = 0; status = 0; unk = IntPtr.Zero; i8 = 0;
            if (((delegate* unmanaged<IntPtr, uint, IntPtr*, int>)Com.Fn(gen, 3))(gen, 1, &e) < 0) return false;          // MF_EVENT_FLAG_NO_WAIT
            uint t; int s;
            ((delegate* unmanaged<IntPtr, uint*, int>)Com.Fn(e, 33))(e, &t);
            ((delegate* unmanaged<IntPtr, int*, int>)Com.Fn(e, 35))(e, &s);
            var pv = stackalloc byte[MF.PropVariantSize];
            new Span<byte>(pv, MF.PropVariantSize).Clear();
            ((delegate* unmanaged<IntPtr, byte*, int>)Com.Fn(e, 36))(e, pv);
            if (*(ushort*)pv == MF.VT_UNKNOWN) unk = *(IntPtr*)(pv + 8);
            else { if (*(ushort*)pv == 20) i8 = *(long*)(pv + 8); Ole.PropVariantClear(pv); }
            Com.Release(e);
            type = t; status = s;
            return true;
        }

        /// <summary>The sink's requests answered while samples come; the source asked where they wait for one; the end drained and marked.</summary>
        private bool Feed(Branch b)
        {
            bool moved = false;
            while (b.Requests > 0)
            {
                IntPtr s = Pull(b, b.Mfts.Count);
                if (s == IntPtr.Zero) break;
                ((delegate* unmanaged<IntPtr, IntPtr, int>)Com.Fn(b.StreamSink, 10))(b.StreamSink, s);          // ProcessSample
                Com.Release(s);
                b.Requests--;
                moved = true;
            }
            // everything out: the end of the segment marked
            if (b.SourceEnded && b.Samples.Count == 0 && !b.MarkerPlaced && b.Drained.All(d => d) && AllOut(b))
            {
                b.MarkerPlaced = true;
                var pv = stackalloc byte[MF.PropVariantSize];
                new Span<byte>(pv, MF.PropVariantSize).Clear();
                ((delegate* unmanaged<IntPtr, uint, byte*, byte*, int>)Com.Fn(b.StreamSink, 11))(b.StreamSink, 2, null, pv);   // MFSTREAMSINK_MARKER_ENDOFSEGMENT
                moved = true;
            }
            return moved;
        }

        private readonly HashSet<IntPtr> _empty = new();
        private readonly Dictionary<IntPtr, Guid> _outSubtype = new();     // each transform's output subtype, as resolved (kept across a stream change)          // transforms that said NEED_MORE_INPUT since their last input

        private bool AllOut(Branch b) => b.Mfts.All(t => _empty.Contains(t));

        /// <summary>The next sample out of the branch at the level (0: the source's), or none for now (the source asked).</summary>
        private IntPtr Pull(Branch b, int level)
        {
            if (level == 0)
            {
                if (b.Samples.Count > 0) return b.Samples.Dequeue();
                if (!b.Asked && !b.SourceEnded && !b.WaitStarted && b.Stream != IntPtr.Zero)
                {
                    int hr = ((delegate* unmanaged<IntPtr, IntPtr, int>)Com.Fn(b.Stream, 9))(b.Stream, IntPtr.Zero);      // RequestSample
                    if (hr == MS.MF_E_END_OF_STREAM) b.SourceEnded = true;
                    else if (hr >= 0) b.Asked = true;
                }
                return IntPtr.Zero;
            }
            IntPtr t = b.Mfts[level - 1];
            for (int guard = 0; guard < 64; guard++)
            {
                int hr = Mft.Output(t, out var sample, out bool changed, null!);
                if (hr >= 0 && sample != null) { _empty.Remove(t); return sample.Pointer; }
                if (changed) { Renegotiate(b, level - 1); continue; }
                if (hr != MF.MF_E_TRANSFORM_NEED_MORE_INPUT && hr < 0) { D3DLog.Once("session-mft:" + hr, $"Media Foundation: a transform's ProcessOutput: 0x{hr:x8}"); return IntPtr.Zero; }
                _empty.Add(t);
                if (b.Drained[level - 1]) return IntPtr.Zero;
                IntPtr input = Pull(b, level - 1);
                if (input == IntPtr.Zero)
                {
                    // the stream above ended: drained
                    bool upEnded = level - 1 == 0 ? b.SourceEnded && b.Samples.Count == 0 : b.Drained[level - 2] && _empty.Contains(b.Mfts[level - 2]);
                    if (upEnded)
                    {
                        Mft.ProcessMessage(t, MFT_MESSAGE_NOTIFY_END_OF_STREAM, 0);
                        Mft.ProcessMessage(t, MFT_MESSAGE_COMMAND_DRAIN, 0);
                        b.Drained[level - 1] = true;
                        _empty.Remove(t);
                        continue;
                    }
                    return IntPtr.Zero;
                }
                hr = Mft.ProcessInput(t, input);
                Com.Release(input);
                _empty.Remove(t);
                if (hr < 0 && hr != MF.MF_E_NOTACCEPTING) D3DLog.Once("session-input:" + hr, $"Media Foundation: a transform's ProcessInput: 0x{hr:x8}");
            }
            return IntPtr.Zero;
        }

        /// <summary>A transform's new output type (MF_E_TRANSFORM_STREAM_CHANGE): of its subtype; what is after it told.</summary>
        private void Renegotiate(Branch b, int i)
        {
            IntPtr t = b.Mfts[i];
            Guid sub = _outSubtype.TryGetValue(t, out var g) ? g : Guid.Empty;
            if (Mft.SetOutputOf(t, sub) < 0)
            {
                // of the subtype no more: its first
                IntPtr first;
                if (((delegate* unmanaged<IntPtr, uint, uint, IntPtr*, int>)Com.Fn(t, 14))(t, 0, 0, &first) >= 0) { Mft.SetOutputType(t, first); Com.Release(first); }
            }
            var now = Mft.OutputType(t);
            if (now == null) return;
            try
            {
                if (i + 1 < b.Mfts.Count)
                {
                    IntPtr next = b.Mfts[i + 1];
                    var nextOut = Mft.OutputType(next);
                    Mft.SetInputType(next, now.Pointer);
                    if (nextOut != null)
                    {
                        // its output of the same subtype at the new size
                        var o = nextOut.Clone();
                        foreach (var k in new[] { MF.MF_MT_FRAME_SIZE, MF.MF_MT_DEFAULT_STRIDE, MF.MF_MT_SAMPLE_SIZE })
                            if (now.Attributes.Get(k) is { } v && k == MF.MF_MT_FRAME_SIZE) o.Attributes.Set(k, v);
                        o.Attributes.Delete(MF.MF_MT_DEFAULT_STRIDE); o.Attributes.Delete(MF.MF_MT_SAMPLE_SIZE);
                        Mft.SetOutputType(next, o.Pointer);
                        o.Release(); nextOut.Release();
                    }
                }
                else
                {
                    IntPtr h;
                    if (((delegate* unmanaged<IntPtr, IntPtr*, int>)Com.Fn(b.StreamSink, 9))(b.StreamSink, &h) >= 0)
                    {
                        ((delegate* unmanaged<IntPtr, IntPtr, int>)Com.Fn(h, 6))(h, now.Pointer);
                        Com.Release(h);
                    }
                }
            }
            finally { now.Release(); }
        }

        // ---- Shutdown -----------------------------------------------------------------------------------------

        public void Shutdown()
        {
            lock (_lock)
            {
                if (_shutdown) return;
                _shutdown = true;
            }
            _commands.CompleteAdding();
            if (Thread.CurrentThread != _thread) _thread.Join();
            foreach (var s in _sinks) ((delegate* unmanaged<IntPtr, int>)Com.Fn(s, 11))(s);              // IMFMediaSink::Shutdown
            foreach (var t in _transforms) if (Com.QueryInterface(t, new Guid("ad5d6c94-a0ff-4a9a-9a92-32ab2a6cf7e6"), out var sh) >= 0) { ((delegate* unmanaged<IntPtr, int>)Com.Fn(sh, 4))(sh); Com.Release(sh); }
            Teardown();
            _queue.Shutdown();
        }

        // ---- the vtable ---------------------------------------------------------------------------------------

        private static MFMediaSession? S(IntPtr self) => From<MFMediaSession>(self);

        [UnmanagedCallersOnly]
        private static int GetEvent(IntPtr self, uint flags, IntPtr* e)
        {
            if (S(self) is not { } s || e == null) return MF.E_POINTER;
            if (s._shutdown) { *e = IntPtr.Zero; return MF_E_SESSION_SHUTDOWN; }
            int hr = s._queue.GetEvent((flags & 1) != 0, out var ev);
            *e = ev;
            return hr;
        }

        [UnmanagedCallersOnly] private static int BeginGetEvent(IntPtr self, IntPtr callback, IntPtr state) => S(self) is { } s ? (s._shutdown ? MF_E_SESSION_SHUTDOWN : s._queue.BeginGetEvent(callback, state)) : MF.E_POINTER;

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

        [UnmanagedCallersOnly]
        private static int SetTopology(IntPtr self, uint flags, IntPtr topology)
        {
            if (S(self) is not { } s) return MF.E_POINTER;
            if (s._shutdown) return MF_E_SESSION_SHUTDOWN;
            if (From<MFTopology>(topology) is not { } t) return MF.E_INVALIDARG;
            if (s._closed) return MF_E_INVALIDREQUEST;
            t.AddRef();
            return s.Command(() => s.DoSetTopology(t));
        }

        [UnmanagedCallersOnly]
        private static int ClearTopologies(IntPtr self)
        {
            if (S(self) is not { } s) return MF.E_POINTER;
            return s.Command(() => s.Queue(MESessionTopologiesCleared));
        }

        [UnmanagedCallersOnly]
        private static int Start(IntPtr self, Guid* format, byte* position)
        {
            if (S(self) is not { } s) return MF.E_POINTER;
            if (s._shutdown) return MF_E_SESSION_SHUTDOWN;
            if (format != null && *format != Guid.Empty) return MF.E_INVALIDARG;
            ushort vt = position == null ? (ushort)0 : *(ushort*)position;
            if (vt != 0 && vt != 20) return MF.MF_E_INVALIDTYPE;
            long? at = vt == 20 ? *(long*)(position + 8) : null;
            return s.Command(() => s.DoStart(at));
        }

        [UnmanagedCallersOnly] private static int Pause(IntPtr self) => S(self) is { } s ? s.Command(s.DoPause) : MF.E_POINTER;
        [UnmanagedCallersOnly] private static int Stop(IntPtr self) => S(self) is { } s ? s.Command(s.DoStop) : MF.E_POINTER;
        [UnmanagedCallersOnly] private static int Close(IntPtr self) => S(self) is { } s ? s.Command(s.DoClose) : MF.E_POINTER;

        [UnmanagedCallersOnly]
        private static int ShutdownSlot(IntPtr self)
        {
            if (S(self) is not { } s) return MF.E_POINTER;
            try { s.Shutdown(); }
            catch (Exception e) { D3DLog.Error("IMFMediaSession.Shutdown", e); }
            return MF.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int GetClock(IntPtr self, IntPtr* clock)
        {
            if (S(self) is not { } s || clock == null) return MF.E_POINTER;
            *clock = IntPtr.Zero;
            if (s._shutdown) return MF_E_SESSION_SHUTDOWN;
            *clock = s.Clock.AddRefPointer();
            return MF.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int GetSessionCapabilities(IntPtr self, uint* caps)
        {
            if (S(self) is not { } s || caps == null) return MF.E_POINTER;
            if (s._shutdown) return MF_E_SESSION_SHUTDOWN;
            *caps = s._caps;
            return MF.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int GetFullTopology(IntPtr self, uint flags, ulong id, IntPtr* topology)
        {
            if (S(self) is not { } s || topology == null) return MF.E_POINTER;
            *topology = IntPtr.Zero;
            if (s._shutdown) return MF_E_SESSION_SHUTDOWN;
            var t = s._full;
            if (t == null || (flags & 1) == 0 && t.Id != id) return MF_E_INVALIDREQUEST;
            *topology = t.AddRefPointer();
            return MF.S_OK;
        }

        /// <summary>A service of the sinks' (the first that has it).</summary>
        [UnmanagedCallersOnly]
        private static int GetServiceSlot(IntPtr self, Guid* service, Guid* iid, IntPtr* ppv)
        {
            if (S(self) is not { } s || service == null || iid == null || ppv == null) return MF.E_POINTER;
            *ppv = IntPtr.Zero;
            if (s._shutdown) return MF_E_SESSION_SHUTDOWN;
            foreach (var sink in s._sinks.ToArray())
            {
                if (Com.QueryInterface(sink, IID_IMFGetService, out var gs) < 0) continue;
                int hr = ((delegate* unmanaged<IntPtr, Guid*, Guid*, IntPtr*, int>)Com.Fn(gs, 3))(gs, service, iid, ppv);
                Com.Release(gs);
                if (hr >= 0) return hr;
            }
            return MF.E_NOINTERFACE;
        }
    }

    /// <summary>A renderer of the shim's that tells the session of events of its own as the source starts.</summary>
    internal interface IRendererEvents
    {
        IEnumerable<(Guid Extended, MFValue? Value)> StartEvents();
    }
}
