// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The sample grabber sink (MFCreateSampleGrabberSinkActivate; Documentation/media-foundation-shim.md, "The media
// session"), as Windows' behaves (MFMediaSessionProbe, MediaSession.windows.txt): a media sink of one stream (id 0)
// whose handler takes a type of the given one's attributes, completed by the loader; its presentation clock given to
// the callback (OnSetPresentationClock), the clock's starts, stops and pauses passed on to it; each sample handed to
// OnProcessSample at its presentation time (at once with MF_SAMPLEGRABBERSINK_IGNORE_CLOCK), of its time, duration
// and bytes, the next one asked for as one is taken (three asked for ahead); a marker told once the samples before
// it are; OnShutdown at Shutdown.
//

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Wpf.Interop.WebGpu.Direct3D;
using Microsoft.Wpf.Interop.WebGpu.MediaFoundation.Sink;

namespace Microsoft.Wpf.Interop.WebGpu.MediaFoundation.Session
{
    internal static class SinkIds
    {
        public static readonly Guid IID_IMFMediaSink = new("6ef2a660-47c0-4666-b13d-cbb717f2fa2c"), IID_IMFStreamSink = new("0a97b3cf-8e7c-4a3d-8f8c-0c843dc247fb");
        public static readonly Guid MF_SAMPLEGRABBERSINK_IGNORE_CLOCK = new("0efda2c0-2b69-4e2e-ab8d-46dcbff7d25d");
        public static readonly Guid MF_SAMPLEGRABBERSINK_SAMPLE_TIME_OFFSET = new("62e3d776-8100-4e03-a6e8-bd3857ac9c47");
        public const uint MEStreamSinkStarted = 801, MEStreamSinkStopped = 802, MEStreamSinkPaused = 803, MEStreamSinkRequestSample = 805, MEStreamSinkMarker = 806;
        public const uint MEDIASINK_FIXED_STREAMS = 1;
        public const int MF_E_STREAMSINK_REMOVED = unchecked((int)0xC00D4A38), MF_E_NO_CLOCK = unchecked((int)0xC00D9C40);
    }

    /// <summary>What a stream sink of the shim's renders to: the clock's time, the sample's delivery, its media sink.</summary>
    internal interface IStreamOwner
    {
        /// <summary>The presentation time now, or none.</summary>
        long? Now { get; }
        /// <summary>Samples delivered as they come, not at their times.</summary>
        bool IgnoreClock { get; }
        /// <summary>How far ahead of its time a sample is delivered (an audio device's latency), in 100 ns.</summary>
        long Lead { get; }
        void Deliver(IntPtr sample, Guid major, long time, long duration);
        ComObject Sink { get; }
    }

    /// <summary>IMFMediaSink of the sample grabber, and IMFClockStateSink of its clock.</summary>
    internal sealed unsafe class MFSampleGrabberSink : ComObject, IStreamOwner
    {
        private static readonly Vtable s_vt = Build(), s_clockVt = BuildClock();
        private readonly IntPtr _callback;                  // IMFSampleGrabberSinkCallback, AddRef'd
        internal readonly MFGrabberStream Stream;
        public bool IgnoreClock { get; }
        internal readonly long Offset;
        public long Lead => Offset;
        public ComObject Sink => this;
        private readonly ClockSink _clockSink;
        internal MFPresentationClock? Clock;
        private IntPtr _foreignClock;                       // an application's IMFPresentationClock, AddRef'd
        internal bool IsShutdown;
        internal readonly object Lock = new();

        private static Vtable Build()
        {
            var vt = new Vtable("IMFMediaSink", 12, SinkIds.IID_IMFMediaSink);
            vt.Set(3, (delegate* unmanaged<IntPtr, uint*, int>)&GetCharacteristics);
            vt.Set(4, (delegate* unmanaged<IntPtr, uint, IntPtr, IntPtr*, int>)&AddStreamSink);
            vt.Set(5, (delegate* unmanaged<IntPtr, uint, int>)&RemoveStreamSink);
            vt.Set(6, (delegate* unmanaged<IntPtr, uint*, int>)&GetStreamSinkCount);
            vt.Set(7, (delegate* unmanaged<IntPtr, uint, IntPtr*, int>)&GetStreamSinkByIndex);
            vt.Set(8, (delegate* unmanaged<IntPtr, uint, IntPtr*, int>)&GetStreamSinkById);
            vt.Set(9, (delegate* unmanaged<IntPtr, IntPtr, int>)&SetPresentationClock);
            vt.Set(10, (delegate* unmanaged<IntPtr, IntPtr*, int>)&GetPresentationClock);
            vt.Set(11, (delegate* unmanaged<IntPtr, int>)&ShutdownSlot);
            return vt;
        }

        private static Vtable BuildClock()
        {
            var vt = new Vtable("IMFClockStateSink", 8, MFPresentationClock.IID_IMFClockStateSink);
            vt.Set(3, (delegate* unmanaged<IntPtr, long, long, int>)&OnClockStart);
            vt.Set(4, (delegate* unmanaged<IntPtr, long, int>)&OnClockStop);
            vt.Set(5, (delegate* unmanaged<IntPtr, long, int>)&OnClockPause);
            vt.Set(6, (delegate* unmanaged<IntPtr, long, int>)&OnClockRestart);
            vt.Set(7, (delegate* unmanaged<IntPtr, long, float, int>)&OnClockSetRate);
            return vt;
        }

        /// <summary>The clock's state sink: the sink's own object (a callback of the sink's clock).</summary>
        private sealed class ClockSink : ComObject
        {
            public readonly MFSampleGrabberSink Owner;
            public ClockSink(MFSampleGrabberSink owner) : base(s_clockVt) { Owner = owner; }
        }

        public MFSampleGrabberSink(MFMediaType type, IntPtr callback, bool ignoreClock, long offset) : base(s_vt)
        {
            Com.AddRef(callback);
            _callback = callback;
            IgnoreClock = ignoreClock;
            Offset = offset;
            _clockSink = new ClockSink(this);
            Stream = new MFGrabberStream(this, new GrabberTypeHandler(type));
        }

        protected override void OnFinalRelease()
        {
            Shutdown();
            Stream.Release();
            _clockSink.Release();
            Com.Release(_callback);
        }

        public void Shutdown()
        {
            IntPtr foreign;
            lock (Lock)
            {
                if (IsShutdown) return;
                IsShutdown = true;
                foreign = _foreignClock; _foreignClock = IntPtr.Zero;
            }
            Stream.Shut();
            Detach(foreign);
            ((delegate* unmanaged<IntPtr, int>)Com.Fn(_callback, 10))(_callback);          // OnShutdown
        }

        private void Detach(IntPtr clock)
        {
            if (Clock != null) { Clock.RemoveSink(_clockSink.Pointer); Clock.Release(); Clock = null; }
            if (clock != IntPtr.Zero) { ((delegate* unmanaged<IntPtr, IntPtr, int>)Com.Fn(clock, 12))(clock, _clockSink.Pointer); Com.Release(clock); }
        }

        /// <summary>The presentation time now (the clock's), or none.</summary>
        public long? Now
        {
            get
            {
                if (Clock != null) return Clock.Time;
                IntPtr c = _foreignClock;
                if (c == IntPtr.Zero) return null;
                long t;
                return ((delegate* unmanaged<IntPtr, long*, int>)Com.Fn(c, 10))(c, &t) >= 0 ? t : null;
            }
        }

        /// <summary>The callback's IMFClockStateSink slot (3 start, 4 stop, 5 pause, 6 restart).</summary>
        private void TellCallback(int slot, long system, long offset = 0)
        {
            if (slot == 3) ((delegate* unmanaged<IntPtr, long, long, int>)Com.Fn(_callback, 3))(_callback, system, offset);
            else ((delegate* unmanaged<IntPtr, long, int>)Com.Fn(_callback, slot))(_callback, system);
        }

        public void Deliver(IntPtr sample, Guid major, long time, long duration)
        {
            byte[] bytes = MFSample.ForeignSampleBytes(sample);
            fixed (byte* b = bytes)
                ((delegate* unmanaged<IntPtr, Guid*, uint, long, long, byte*, uint, int>)Com.Fn(_callback, 9))(_callback, &major, 0, time, duration, b, (uint)bytes.Length);
        }

        private static MFSampleGrabberSink? S(IntPtr self) => From<MFSampleGrabberSink>(self);
        private static MFSampleGrabberSink? CS(IntPtr self) => From<ClockSink>(self)?.Owner;

        [UnmanagedCallersOnly]
        private static int GetCharacteristics(IntPtr self, uint* c)
        {
            if (S(self) is not { } s || c == null) return MF.E_POINTER;
            if (s.IsShutdown) return MF.MF_E_SHUTDOWN;
            *c = SinkIds.MEDIASINK_FIXED_STREAMS;
            return MF.S_OK;
        }

        [UnmanagedCallersOnly] private static int AddStreamSink(IntPtr self, uint id, IntPtr type, IntPtr* stream) { if (stream != null) *stream = IntPtr.Zero; return unchecked((int)0xC00D4A3A); }   // MF_E_STREAMSINKS_FIXED
        [UnmanagedCallersOnly] private static int RemoveStreamSink(IntPtr self, uint id) => unchecked((int)0xC00D4A3A);

        [UnmanagedCallersOnly]
        private static int GetStreamSinkCount(IntPtr self, uint* n)
        {
            if (S(self) is not { } s || n == null) return MF.E_POINTER;
            if (s.IsShutdown) return MF.MF_E_SHUTDOWN;
            *n = 1;
            return MF.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int GetStreamSinkByIndex(IntPtr self, uint i, IntPtr* stream)
        {
            if (S(self) is not { } s || stream == null) return MF.E_POINTER;
            *stream = IntPtr.Zero;
            if (s.IsShutdown) return MF.MF_E_SHUTDOWN;
            if (i != 0) return MF.E_INVALIDARG;
            *stream = s.Stream.AddRefPointer();
            return MF.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int GetStreamSinkById(IntPtr self, uint id, IntPtr* stream)
        {
            if (S(self) is not { } s || stream == null) return MF.E_POINTER;
            *stream = IntPtr.Zero;
            if (s.IsShutdown) return MF.MF_E_SHUTDOWN;
            if (id != 0) return unchecked((int)0xC00D4A3B);           // MF_E_INVALIDSTREAMNUMBER of a sink
            *stream = s.Stream.AddRefPointer();
            return MF.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int SetPresentationClock(IntPtr self, IntPtr clock)
        {
            if (S(self) is not { } s) return MF.E_POINTER;
            if (s.IsShutdown) return MF.MF_E_SHUTDOWN;
            IntPtr old;
            lock (s.Lock) { old = s._foreignClock; s._foreignClock = IntPtr.Zero; }
            s.Detach(old);
            if (clock != IntPtr.Zero)
            {
                if (From<MFPresentationClock>(clock) is { } ours) { ours.AddRef(); s.Clock = ours; ours.AddSink(s._clockSink.Pointer); }
                else
                {
                    Com.AddRef(clock);
                    s._foreignClock = clock;
                    ((delegate* unmanaged<IntPtr, IntPtr, int>)Com.Fn(clock, 11))(clock, s._clockSink.Pointer);       // AddClockStateSink
                }
            }
            ((delegate* unmanaged<IntPtr, IntPtr, int>)Com.Fn(s._callback, 8))(s._callback, clock);              // OnSetPresentationClock
            return MF.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int GetPresentationClock(IntPtr self, IntPtr* clock)
        {
            if (S(self) is not { } s || clock == null) return MF.E_POINTER;
            *clock = IntPtr.Zero;
            if (s.IsShutdown) return MF.MF_E_SHUTDOWN;
            IntPtr c = s.Clock?.Pointer ?? s._foreignClock;
            if (c == IntPtr.Zero) return SinkIds.MF_E_NO_CLOCK;
            Com.AddRef(c);
            *clock = c;
            return MF.S_OK;
        }

        [UnmanagedCallersOnly] private static int ShutdownSlot(IntPtr self) { if (S(self) is not { } s) return MF.E_POINTER; if (s.IsShutdown) return MF.MF_E_SHUTDOWN; s.Shutdown(); return MF.S_OK; }

        // ---- IMFClockStateSink: the callback told, the stream started, stopped, paused ----

        [UnmanagedCallersOnly]
        private static int OnClockStart(IntPtr self, long system, long offset)
        {
            if (CS(self) is not { } s) return MF.E_POINTER;
            s.TellCallback(3, system, offset);
            s.Stream.ClockStarted(offset);
            return MF.S_OK;
        }

        [UnmanagedCallersOnly] private static int OnClockStop(IntPtr self, long system) { if (CS(self) is not { } s) return MF.E_POINTER; s.TellCallback(4, system); s.Stream.ClockStopped(); return MF.S_OK; }
        [UnmanagedCallersOnly] private static int OnClockPause(IntPtr self, long system) { if (CS(self) is not { } s) return MF.E_POINTER; s.TellCallback(5, system); s.Stream.ClockPaused(); return MF.S_OK; }
        [UnmanagedCallersOnly] private static int OnClockRestart(IntPtr self, long system) { if (CS(self) is not { } s) return MF.E_POINTER; s.TellCallback(6, system); s.Stream.ClockRestarted(); return MF.S_OK; }

        [UnmanagedCallersOnly]
        private static int OnClockSetRate(IntPtr self, long system, float rate)
        {
            if (CS(self) is not { } s) return MF.E_POINTER;
            ((delegate* unmanaged<IntPtr, long, float, int>)Com.Fn(s._callback, 7))(s._callback, system, rate);
            return MF.S_OK;
        }
    }

    /// <summary>IMFStreamSink of the shim's sinks (the sample grabber's, the audio renderer's): its samples to its owner at their times, on a thread of its own.</summary>
    internal sealed unsafe class MFGrabberStream : ComObject
    {
        private static readonly Vtable s_vt = Build();
        private const int Ahead = 3;
        private readonly IStreamOwner _sink;
        private readonly MFEventQueue _queue = new();
        internal readonly ComObject Handler;
        private readonly Guid _major;
        private readonly Queue<(IntPtr Sample, uint Marker, MFValue? Context)> _pending = new();     // samples (AddRef'd) and markers, in order
        private readonly object _lock = new();
        private enum State { Stopped, Running, Paused }
        private State _state;
        private int _asked;                                 // requests sent not answered
        private Thread? _thread;
        private bool _shut;

        private static Vtable Build()
        {
            var vt = new Vtable("IMFStreamSink", 13, SinkIds.IID_IMFStreamSink, MS_IID_IMFMediaEventGenerator);
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

        private static readonly Guid MS_IID_IMFMediaEventGenerator = new("2cd0bd52-bcd5-4b89-b62c-eadc0c031e7d");

        public MFGrabberStream(IStreamOwner sink, ComObject handler) : base(s_vt)
        {
            _sink = sink;
            Handler = handler;
            Guid g; IntPtr h = handler.Pointer;
            _major = ((delegate* unmanaged<IntPtr, Guid*, int>)Com.Fn(h, 8))(h, &g) >= 0 ? g : Guid.Empty;
        }

        protected override void OnFinalRelease() { Shut(); Handler.Release(); }

        internal void Shut()
        {
            Thread? t;
            lock (_lock)
            {
                if (_shut) return;
                _shut = true;
                DropPending();
                t = _thread; _thread = null;
                Monitor.PulseAll(_lock);
            }
            t?.Join();
            _queue.Shutdown();
        }

        private void DropPending() { while (_pending.Count > 0) { var p = _pending.Dequeue(); Com.Release(p.Sample); } }

        private void Tell(uint type, MFValue? value = null, int status = MF.S_OK) => _queue.QueueEvent(type, Guid.Empty, status, value);

        /// <summary>The requests made up to the number asked for ahead.</summary>
        private void AskMore()
        {
            while (_state == State.Running && _asked + _pending.Count < Ahead) { _asked++; Tell(SinkIds.MEStreamSinkRequestSample); }
        }

        internal void ClockStarted(long offset)
        {
            lock (_lock)
            {
                if (_shut) return;
                if (offset != MFPresentationClock.PRESENTATION_CURRENT_POSITION && _state != State.Paused) { DropPending(); _asked = 0; }
                _state = State.Running;
                Tell(SinkIds.MEStreamSinkStarted);
                AskMore();
                EnsureThread();
                Monitor.PulseAll(_lock);
            }
        }

        internal void ClockRestarted() { lock (_lock) { if (_shut) return; _state = State.Running; Tell(SinkIds.MEStreamSinkStarted); AskMore(); EnsureThread(); Monitor.PulseAll(_lock); } }
        internal void ClockPaused() { lock (_lock) { if (_shut) return; _state = State.Paused; Tell(SinkIds.MEStreamSinkPaused); Monitor.PulseAll(_lock); } }
        internal void ClockStopped() { lock (_lock) { if (_shut) return; _state = State.Stopped; DropPending(); _asked = 0; Tell(SinkIds.MEStreamSinkStopped); Monitor.PulseAll(_lock); } }

        private void EnsureThread()
        {
            if (_thread != null) return;
            _thread = new Thread(Run) { IsBackground = true, Name = "Sample grabber" };
            _thread.Start();
        }

        /// <summary>Each sample at its time (of the clock's), each marker once the samples before it are.</summary>
        private void Run()
        {
            var major = _major;
            while (true)
            {
                (IntPtr Sample, uint Marker, MFValue? Context) next;
                lock (_lock)
                {
                    while (!_shut && (_state != State.Running || _pending.Count == 0)) Monitor.Wait(_lock, 50);
                    if (_shut) return;
                    next = _pending.Peek();
                }
                if (next.Sample == IntPtr.Zero)
                {
                    lock (_lock) { if (_pending.Count > 0 && _pending.Peek().Sample == IntPtr.Zero) _pending.Dequeue(); }
                    Tell(SinkIds.MEStreamSinkMarker, next.Context);
                    continue;
                }
                long time, duration;
                if (((delegate* unmanaged<IntPtr, long*, int>)Com.Fn(next.Sample, 35))(next.Sample, &time) < 0) time = 0;
                if (((delegate* unmanaged<IntPtr, long*, int>)Com.Fn(next.Sample, 37))(next.Sample, &duration) < 0) duration = 0;
                // its time: waited for while the clock runs (stopped or flushed meanwhile: dropped)
                if (!_sink.IgnoreClock)
                {
                    bool gone = false;
                    while (true)
                    {
                        long? now = _sink.Now;
                        lock (_lock)
                        {
                            if (_shut) return;
                            if (_pending.Count == 0 || _pending.Peek().Sample != next.Sample) { gone = true; break; }
                            if (_state == State.Running && now is long n && n >= time - _sink.Lead) break;
                            Monitor.Wait(_lock, now is long m && _state == State.Running ? (int)Math.Clamp((time - _sink.Lead - m) / 10_000, 1, 20) : 20);
                        }
                    }
                    if (gone) continue;
                }
                lock (_lock)
                {
                    if (_pending.Count == 0 || _pending.Peek().Sample != next.Sample) continue;
                    _pending.Dequeue();
                }
                try { _sink.Deliver(next.Sample, major, time, duration); }
                catch (Exception e) { D3DLog.Error("a stream sink's delivery", e); }
                finally { Com.Release(next.Sample); }
                lock (_lock) AskMore();
            }
        }

        private static MFGrabberStream? M(IntPtr self) => From<MFGrabberStream>(self);

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
        private static int GetMediaSink(IntPtr self, IntPtr* sink)
        {
            if (M(self) is not { } m || sink == null) return MF.E_POINTER;
            *sink = IntPtr.Zero;
            if (m._shut) return SinkIds.MF_E_STREAMSINK_REMOVED;
            *sink = m._sink.Sink.AddRefPointer();
            return MF.S_OK;
        }

        [UnmanagedCallersOnly] private static int GetIdentifier(IntPtr self, uint* id) { if (M(self) is not { } m || id == null) return MF.E_POINTER; if (m._shut) return SinkIds.MF_E_STREAMSINK_REMOVED; *id = 0; return MF.S_OK; }

        [UnmanagedCallersOnly]
        private static int GetMediaTypeHandler(IntPtr self, IntPtr* h)
        {
            if (M(self) is not { } m || h == null) return MF.E_POINTER;
            *h = IntPtr.Zero;
            if (m._shut) return SinkIds.MF_E_STREAMSINK_REMOVED;
            *h = m.Handler.AddRefPointer();
            return MF.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int ProcessSample(IntPtr self, IntPtr sample)
        {
            if (M(self) is not { } m || sample == IntPtr.Zero) return MF.E_POINTER;
            lock (m._lock)
            {
                if (m._shut) return SinkIds.MF_E_STREAMSINK_REMOVED;
                if (m._state == State.Stopped) return unchecked((int)0xC00D4A3E);          // MF_E_INVALIDREQUEST of a stopped sink
                Com.AddRef(sample);
                m._pending.Enqueue((sample, 0, null));
                if (m._asked > 0) m._asked--;
                Monitor.PulseAll(m._lock);
            }
            return MF.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int PlaceMarker(IntPtr self, uint type, byte* value, byte* context)
        {
            if (M(self) is not { } m) return MF.E_POINTER;
            lock (m._lock)
            {
                if (m._shut) return SinkIds.MF_E_STREAMSINK_REMOVED;
                var ctx = context == null ? null : MFMediaEvent.ValueOf(context);
                if (m._pending.Count == 0 && m._state != State.Paused) { m.Tell(SinkIds.MEStreamSinkMarker, ctx); return MF.S_OK; }
                m._pending.Enqueue((IntPtr.Zero, type, ctx));
                Monitor.PulseAll(m._lock);
            }
            return MF.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int Flush(IntPtr self)
        {
            if (M(self) is not { } m) return MF.E_POINTER;
            lock (m._lock)
            {
                if (m._shut) return SinkIds.MF_E_STREAMSINK_REMOVED;
                // the samples dropped, the markers told (as cancelled)
                foreach (var p in m._pending) if (p.Sample == IntPtr.Zero) m.Tell(SinkIds.MEStreamSinkMarker, p.Context, unchecked((int)0x80004004));
                m.DropPending();
                Monitor.PulseAll(m._lock);
            }
            return MF.S_OK;
        }
    }

    /// <summary>The grabber stream's handler: types of the given one's attributes; the current type, the one set.</summary>
    internal sealed unsafe class GrabberTypeHandler : ComObject
    {
        private static readonly Vtable s_vt = Build();
        private readonly MFMediaType _type;
        public MFMediaType? Current;
        private readonly object _lock = new();

        private static Vtable Build()
        {
            var vt = new Vtable("IMFMediaTypeHandler", 9, new Guid("e93dcf6c-4b07-4e1e-8123-aa16ed6eadf5"));
            vt.Set(3, (delegate* unmanaged<IntPtr, IntPtr, IntPtr*, int>)&IsMediaTypeSupported);
            vt.Set(4, (delegate* unmanaged<IntPtr, uint*, int>)&GetMediaTypeCount);
            vt.Set(5, (delegate* unmanaged<IntPtr, uint, IntPtr*, int>)&GetMediaTypeByIndex);
            vt.Set(6, (delegate* unmanaged<IntPtr, IntPtr, int>)&SetCurrentMediaType);
            vt.Set(7, (delegate* unmanaged<IntPtr, IntPtr*, int>)&GetCurrentMediaType);
            vt.Set(8, (delegate* unmanaged<IntPtr, Guid*, int>)&GetMajorType);
            return vt;
        }

        public GrabberTypeHandler(MFMediaType type) : base(s_vt) { _type = type; type.AddRef(); }

        public Guid Major => _type.Attributes.Guid(MF.MF_MT_MAJOR_TYPE);

        protected override void OnFinalRelease() { _type.Release(); Current?.Release(); }

        /// <summary>Of every attribute the given type has, the same value.</summary>
        private bool Takes(IntPtr type)
        {
            var t = Source.Mp4Types.Read(type);
            try
            {
                foreach (var (k, v) in _type.Attributes.All())
                    if (t.Attributes.Get(k) is not { } w || !w.SameAs(v)) return false;
                return true;
            }
            finally { t.Release(); }
        }

        private static GrabberTypeHandler? H(IntPtr self) => From<GrabberTypeHandler>(self);

        [UnmanagedCallersOnly]
        private static int IsMediaTypeSupported(IntPtr self, IntPtr type, IntPtr* closest)
        {
            if (H(self) is not { } h || type == IntPtr.Zero) return MF.E_POINTER;
            if (closest != null) *closest = IntPtr.Zero;
            return h.Takes(type) ? MF.S_OK : MF.MF_E_INVALIDMEDIATYPE;
        }

        [UnmanagedCallersOnly] private static int GetMediaTypeCount(IntPtr self, uint* n) { if (H(self) is null || n == null) return MF.E_POINTER; *n = 1; return MF.S_OK; }

        [UnmanagedCallersOnly]
        private static int GetMediaTypeByIndex(IntPtr self, uint i, IntPtr* type)
        {
            if (H(self) is not { } h || type == null) return MF.E_POINTER;
            *type = IntPtr.Zero;
            if (i != 0) return MF.MF_E_NO_MORE_TYPES;
            *type = h._type.Clone().Pointer;
            return MF.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int SetCurrentMediaType(IntPtr self, IntPtr type)
        {
            if (H(self) is not { } h || type == IntPtr.Zero) return MF.E_POINTER;
            if (!h.Takes(type)) return MF.MF_E_INVALIDMEDIATYPE;
            var t = Source.Mp4Types.Read(type);
            lock (h._lock) { h.Current?.Release(); h.Current = t; }
            return MF.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int GetCurrentMediaType(IntPtr self, IntPtr* type)
        {
            if (H(self) is not { } h || type == null) return MF.E_POINTER;
            *type = IntPtr.Zero;
            lock (h._lock)
            {
                if (h.Current == null) return MFE.MF_E_NOT_INITIALIZED;
                *type = h.Current.AddRefPointer();
            }
            return MF.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int GetMajorType(IntPtr self, Guid* major)
        {
            if (H(self) is not { } h || major == null) return MF.E_POINTER;
            *major = h.Major;
            return MF.S_OK;
        }
    }
}
