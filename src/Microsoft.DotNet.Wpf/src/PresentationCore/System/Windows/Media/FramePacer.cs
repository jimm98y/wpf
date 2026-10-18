// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Threading;

namespace System.Windows.Media
{
    /// <summary>
    /// The wait between two frames, kept to the sub-millisecond: a thread of its own sleeps until the
    /// deadline and then runs the callback on the dispatcher (at Send priority, so nothing queued in
    /// between delays it). See MediaContext.StartPromoteToInput for why the frame does not wait on a
    /// DispatcherTimer. One pending deadline at a time: a new one replaces it, Cancel drops it.
    /// <para>WPF_PRECISE_PACING=0 falls back to the DispatcherTimer.</para>
    /// </summary>
    internal sealed class FramePacer
    {
        internal static bool Enabled = Environment.GetEnvironmentVariable("WPF_PRECISE_PACING") != "0";   // tests compare both

        private readonly Dispatcher _dispatcher;
        private readonly Action _callback;
        private readonly object _lock = new object();
        private readonly AutoResetEvent _wake = new AutoResetEvent(false);
        private long _dueTimestamp;      // Stopwatch timestamp; 0 = nothing pending
        private int _generation;
        private Thread _thread;

        internal FramePacer(Dispatcher dispatcher, Action callback)
        {
            _dispatcher = dispatcher;
            _callback = callback;
        }

        internal void Schedule(TimeSpan delay)
        {
            lock (_lock)
            {
                _dueTimestamp = Stopwatch.GetTimestamp() + (long)(delay.TotalSeconds * Stopwatch.Frequency);
                _generation++;
                if (_thread == null)
                {
                    // Above the UI thread, as stock's render thread is: it only ever wakes to say "now",
                    // and a wake-up that waits behind other work is a late frame.
                    _thread = new Thread(Run) { IsBackground = true, Name = "WPF frame pacer", Priority = ThreadPriority.Highest };
                    _thread.Start();
                }
            }
            _wake.Set();
        }

        internal void Cancel()
        {
            lock (_lock)
            {
                if (_dueTimestamp == 0) return;
                _dueTimestamp = 0;
                _generation++;
            }
            _wake.Set();
        }

        private void Run()
        {
            IntPtr timer = CreateTimer();
            while (true)
            {
                long due; int generation;
                lock (_lock) { due = _dueTimestamp; generation = _generation; }
                if (due == 0)
                {
                    if (_dispatcher.HasShutdownStarted) return;
                    _wake.WaitOne(1000);
                    continue;
                }
                long remaining = due - Stopwatch.GetTimestamp();
                if (remaining > 0)
                {
                    WaitFor(timer, remaining);
                    continue;   // re-read: rescheduled or cancelled meanwhile, or woken early
                }
                lock (_lock)
                {
                    if (generation != _generation) continue;
                    _dueTimestamp = 0;
                }
                if (_dispatcher.HasShutdownStarted) return;
                _dispatcher.BeginInvoke(DispatcherPriority.Send, (Action)(() =>
                {
                    lock (_lock) if (generation != _generation) return;   // superseded while queued
                    _callback();
                }));
            }
        }

        /// <summary>Sleep for up to <paramref name="stopwatchTicks"/>, returning early on Schedule/Cancel.</summary>
        private void WaitFor(IntPtr timer, long stopwatchTicks)
        {
            if (timer != IntPtr.Zero)
            {
                long due100ns = -Math.Max(1, stopwatchTicks * 10_000_000 / Stopwatch.Frequency);
                if (SetWaitableTimer(timer, in due100ns, 0, IntPtr.Zero, IntPtr.Zero, false))
                {
                    WaitHandle.WaitAny(new WaitHandle[] { _wake, _timerHandle! });
                    return;
                }
            }
            int ms = (int)Math.Max(1, stopwatchTicks * 1000 / Stopwatch.Frequency);
            _wake.WaitOne(ms);
        }

        private WaitHandle _timerHandle;

        /// <summary>A high-resolution waitable timer (Windows 10 1803 and later), whose deadline does not
        /// round to the system tick; zero elsewhere, where a plain timed wait is already precise.</summary>
        private IntPtr CreateTimer()
        {
            if (!OperatingSystem.IsWindows()) return IntPtr.Zero;
            try
            {
                IntPtr h = CreateWaitableTimerExW(IntPtr.Zero, null, 0x2 /* CREATE_WAITABLE_TIMER_HIGH_RESOLUTION */, 0x1F0003 /* TIMER_ALL_ACCESS */);
                if (h == IntPtr.Zero) return IntPtr.Zero;
                _timerHandle = new TimerWaitHandle(h);
                return h;
            }
            catch (EntryPointNotFoundException) { return IntPtr.Zero; }
            catch (DllNotFoundException) { return IntPtr.Zero; }
        }

        private sealed class TimerWaitHandle : WaitHandle
        {
            internal TimerWaitHandle(IntPtr h) { SafeWaitHandle = new Microsoft.Win32.SafeHandles.SafeWaitHandle(h, ownsHandle: true); }
        }

        [DllImport("kernel32", CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateWaitableTimerExW(IntPtr attributes, string name, uint flags, uint access);

        [DllImport("kernel32")]
        private static extern bool SetWaitableTimer(IntPtr timer, in long dueTime, int period, IntPtr completion, IntPtr arg, bool resume);
    }
}
