// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// A presentation clock (IMFPresentationClock, IMFClock) of the system's time: what the sink writer gives its media sink
// (Windows' writer's sink has a clock once writing has begun: MFMediaSinkProbe). Its time is the start offset and the
// time it has run since, in 100 ns units; its state sinks (IMFClockStateSink) told of each start, stop and pause.
//

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Wpf.Interop.WebGpu.Direct3D;

namespace Microsoft.Wpf.Interop.WebGpu.MediaFoundation.Sink
{
    internal sealed unsafe class MFPresentationClock : ComObject
    {
        public static readonly Guid IID_IMFPresentationClock = new("868ce85c-8ea9-4f55-ab82-b009a910a805"), IID_IMFClock = new("2eb1e945-18b8-4139-9b1a-d5d584818530");
        public static readonly Guid IID_IMFClockStateSink = new("f6696e82-74f7-4f3d-a178-8a5e09c3659f");
        private const uint MFCLOCK_STATE_INVALID = 0, MFCLOCK_STATE_RUNNING = 1, MFCLOCK_STATE_STOPPED = 2, MFCLOCK_STATE_PAUSED = 3;
        public const long PRESENTATION_CURRENT_POSITION = 0x7fffffffffffffff;
        private static readonly Vtable s_vt = Build();

        private readonly object _lock = new();
        private readonly List<IntPtr> _sinks = new();          // IMFClockStateSink, AddRef'd
        private uint _state = MFCLOCK_STATE_INVALID;
        private long _offset;                                   // the clock's time at the last start or pause
        private long _since;                                    // the system time then

        private static Vtable Build()
        {
            var vt = new Vtable("IMFPresentationClock", 16, IID_IMFPresentationClock, IID_IMFClock);
            vt.Set(3, (delegate* unmanaged<IntPtr, uint*, int>)&GetClockCharacteristics);
            vt.Set(4, (delegate* unmanaged<IntPtr, uint, long*, long*, int>)&GetCorrelatedTime);
            vt.Set(5, (delegate* unmanaged<IntPtr, uint*, int>)&GetContinuityKey);
            vt.Set(6, (delegate* unmanaged<IntPtr, uint, uint*, int>)&GetState);
            vt.Set(7, (delegate* unmanaged<IntPtr, byte*, int>)&GetProperties);
            vt.Set(8, (delegate* unmanaged<IntPtr, IntPtr, int>)&SetTimeSource);
            vt.Set(9, (delegate* unmanaged<IntPtr, IntPtr*, int>)&GetTimeSource);
            vt.Set(10, (delegate* unmanaged<IntPtr, long*, int>)&GetTime);
            vt.Set(11, (delegate* unmanaged<IntPtr, IntPtr, int>)&AddClockStateSink);
            vt.Set(12, (delegate* unmanaged<IntPtr, IntPtr, int>)&RemoveClockStateSink);
            vt.Set(13, (delegate* unmanaged<IntPtr, long, int>)&StartSlot);
            vt.Set(14, (delegate* unmanaged<IntPtr, int>)&StopSlot);
            vt.Set(15, (delegate* unmanaged<IntPtr, int>)&PauseSlot);
            return vt;
        }

        public MFPresentationClock() : base(s_vt) { }

        /// <summary>No time source yet (the media session's clock before a topology is set): GetTime is MF_E_CLOCK_NO_TIME_SOURCE.</summary>
        public bool NoTimeSource;

        public uint State { get { lock (_lock) return _state; } }

        public void AddSink(IntPtr sink) { lock (_lock) { if (_sinks.Contains(sink)) return; Com.AddRef(sink); _sinks.Add(sink); } }
        public void RemoveSink(IntPtr sink) { lock (_lock) { if (_sinks.Remove(sink)) Com.Release(sink); } }

        protected override void OnFinalRelease() { lock (_lock) { foreach (var s in _sinks) Com.Release(s); _sinks.Clear(); } }

        /// <summary>The system's time in 100 ns units.</summary>
        public static long SystemTime => (long)(Stopwatch.GetTimestamp() * (10_000_000.0 / Stopwatch.Frequency));

        public long Time
        {
            get { lock (_lock) return _state == MFCLOCK_STATE_RUNNING ? _offset + SystemTime - _since : _offset; }
        }

        private void Tell(int slot, long system, long offset = 0)
        {
            IntPtr[] sinks;
            lock (_lock) { sinks = _sinks.ToArray(); foreach (var s in sinks) Com.AddRef(s); }
            foreach (var s in sinks)
            {
                try
                {
                    if (slot == 3) ((delegate* unmanaged<IntPtr, long, long, int>)Com.Fn(s, 3))(s, system, offset);
                    else ((delegate* unmanaged<IntPtr, long, int>)Com.Fn(s, slot))(s, system);
                }
                finally { Com.Release(s); }
            }
        }

        public int Start(long offset)
        {
            long now = SystemTime;
            bool restart;
            lock (_lock)
            {
                restart = _state == MFCLOCK_STATE_PAUSED && offset == PRESENTATION_CURRENT_POSITION;
                if (offset != PRESENTATION_CURRENT_POSITION) _offset = offset;
                else if (_state == MFCLOCK_STATE_RUNNING) _offset += now - _since;
                _since = now;
                _state = MFCLOCK_STATE_RUNNING;
            }
            if (restart) Tell(6, now); else Tell(3, now, offset);
            return MF.S_OK;
        }

        public int Stop()
        {
            long now = SystemTime;
            lock (_lock)
            {
                if (_state == MFCLOCK_STATE_STOPPED) return unchecked((int)0xC00D9C44);       // MF_E_CLOCK_STATE_ALREADY_SET
                _state = MFCLOCK_STATE_STOPPED; _offset = 0;
            }
            Tell(4, now);
            return MF.S_OK;
        }

        public int Pause()
        {
            long now = SystemTime;
            lock (_lock)
            {
                if (_state == MFCLOCK_STATE_PAUSED) return unchecked((int)0xC00D9C44);
                if (_state != MFCLOCK_STATE_RUNNING) return MFE.MF_E_INVALIDREQUEST;
                _offset += now - _since; _since = now;
                _state = MFCLOCK_STATE_PAUSED;
            }
            Tell(5, now);
            return MF.S_OK;
        }

        private static MFPresentationClock? C(IntPtr self) => From<MFPresentationClock>(self);

        // MFCLOCK_CHARACTERISTICS_FLAG_FREQUENCY_10MHZ, _IS_SYSTEM_CLOCK
        [UnmanagedCallersOnly] private static int GetClockCharacteristics(IntPtr self, uint* c) { if (c == null) return MF.E_POINTER; *c = 2 | 4; return MF.S_OK; }

        [UnmanagedCallersOnly]
        private static int GetCorrelatedTime(IntPtr self, uint reserved, long* clock, long* system)
        {
            if (C(self) is not { } c || clock == null || system == null) return MF.E_POINTER;
            *system = SystemTime; *clock = c.Time;
            return MF.S_OK;
        }

        [UnmanagedCallersOnly] private static int GetContinuityKey(IntPtr self, uint* k) { if (k == null) return MF.E_POINTER; *k = 0; return MF.S_OK; }

        [UnmanagedCallersOnly]
        private static int GetState(IntPtr self, uint reserved, uint* state)
        {
            if (C(self) is not { } c || state == null) return MF.E_POINTER;
            lock (c._lock) *state = c._state;
            return MF.S_OK;
        }

        /// <summary>MFCLOCK_PROPERTIES: the correlation, the clock's id (GUID_NULL), flags, its frequency (10 MHz), tolerance, jitter.</summary>
        [UnmanagedCallersOnly]
        private static int GetProperties(IntPtr self, byte* p)
        {
            if (p == null) return MF.E_POINTER;
            new Span<byte>(p, 48).Clear();
            *(ulong*)(p + 8 + 16 + 8) = 10_000_000;              // qwClockFrequency (after the correlation, the GUID, the flags and padding)
            return MF.S_OK;
        }

        // (its time source the system's: of none to set or hand out)
        [UnmanagedCallersOnly] private static int SetTimeSource(IntPtr self, IntPtr source) => MF.S_OK;
        [UnmanagedCallersOnly] private static int GetTimeSource(IntPtr self, IntPtr* source) { if (source == null) return MF.E_POINTER; *source = IntPtr.Zero; return unchecked((int)0xC00D9C41); }   // MF_E_CLOCK_NO_TIME_SOURCE

        [UnmanagedCallersOnly]
        private static int GetTime(IntPtr self, long* t)
        {
            if (C(self) is not { } c || t == null) return MF.E_POINTER;
            *t = 0;
            if (c.NoTimeSource) return unchecked((int)0xC00D9C41);            // MF_E_CLOCK_NO_TIME_SOURCE
            *t = c.Time;
            return MF.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int AddClockStateSink(IntPtr self, IntPtr sink)
        {
            if (C(self) is not { } c || sink == IntPtr.Zero) return MF.E_POINTER;
            lock (c._lock) { if (c._sinks.Contains(sink)) return MF.E_INVALIDARG; Com.AddRef(sink); c._sinks.Add(sink); }
            return MF.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int RemoveClockStateSink(IntPtr self, IntPtr sink)
        {
            if (C(self) is not { } c || sink == IntPtr.Zero) return MF.E_POINTER;
            lock (c._lock) { if (c._sinks.Remove(sink)) Com.Release(sink); }
            return MF.S_OK;
        }

        [UnmanagedCallersOnly] private static int StartSlot(IntPtr self, long offset) => C(self)?.Start(offset) ?? MF.E_POINTER;
        [UnmanagedCallersOnly] private static int StopSlot(IntPtr self) => C(self)?.Stop() ?? MF.E_POINTER;
        [UnmanagedCallersOnly] private static int PauseSlot(IntPtr self) => C(self)?.Pause() ?? MF.E_POINTER;
    }
}
