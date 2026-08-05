// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.InteropServices;
using System.Threading;

namespace System.Windows.Threading
{
    /// <summary>
    /// The cross-platform wake/wait primitive that drives <see cref="Dispatcher"/>'s message loop.
    /// </summary>
    /// <remarks>
    /// WPF's Dispatcher owns a fully managed priority queue; historically the only OS coupling was
    /// how the pump was <em>woken</em> (a Win32 message-only HWND plus RegisterWindowMessage /
    /// PostMessage), how it <em>blocked</em> (GetMessage) and how it <em>timed</em> (SetTimer).
    /// That Win32 plumbing has been replaced by this single managed implementation, used identically
    /// on every platform, so the dispatcher behaves the same on Windows, macOS and Linux.
    ///
    /// The queue itself is unchanged: a caller signals that work is available (or that the next
    /// <see cref="DispatcherTimer"/> due time has changed) via <see cref="Signal"/>, and the
    /// dispatcher thread blocks in <see cref="Wait"/> until signaled or until its timer deadline
    /// elapses. If a platform ever needs to co-operate with a native run loop (e.g. CFRunLoop on
    /// macOS or the GLib main loop on Linux) that integration belongs here and nowhere else.
    /// </remarks>
    internal sealed class DispatcherRunLoop
    {
        // Auto-reset so that N signals arriving while the loop is busy collapse into a single
        // wake-up - exactly the coalescing the old registered-window-message scheme provided.
        private readonly AutoResetEvent _wake = new AutoResetEvent(false);
        private volatile bool _disposed;

        internal DispatcherRunLoop()
        {
            // Create the wake fd UP FRONT on Linux rather than on first use. Signal() runs on
            // arbitrary threads and can fire before this thread ever reaches Wait, and a signal that
            // finds no fd is a signal the poll set never learns about.
            if (OperatingSystem.IsLinux() && !OperatingSystem.IsAndroid())
            {
                EnsureWakeFd();
            }
        }

        /// <summary>
        /// Wake the dispatcher thread so a pending <see cref="Wait"/> returns promptly. Safe to
        /// call from any thread; redundant signals coalesce into one.
        /// </summary>
        /// <summary>
        /// Optional platform hook invoked on every <see cref="Signal"/>. A pump that does not block
        /// in <see cref="Wait"/> - iOS, where UIKit owns the run loop and the dispatcher is driven by
        /// a CADisplayLink - parks itself when idle and needs this to learn that work has arrived.
        /// Called from whichever thread signalled, so the handler must be thread-safe.
        /// </summary>
        internal Action Woken;

        internal void Signal()
        {
            if (!_disposed)
            {
                _wake.Set();
                // The managed AutoResetEvent is not a pollable file descriptor on Linux, and the
                // Wayland pump has to block in poll() on the compositor's fd. So the signal is
                // MIRRORED into an eventfd that can share that poll set. write(2) is thread-safe and
                // async-signal-safe, which matters because Signal comes from arbitrary threads.
                if (_wakeFd >= 0)
                {
                    WriteWakeFd();
                }
                Woken?.Invoke();
            }
        }

        private int _wakeFd = -1;

        private unsafe void WriteWakeFd()
        {
            ulong one = 1;
            // A full counter (EAGAIN) means a wake is already pending, which is exactly the
            // coalescing the AutoResetEvent provides; nothing to do.
            write(_wakeFd, &one, 8);
        }

        /// <summary>
        /// Create the eventfd that pairs <see cref="Signal"/> with a poll set. Done lazily so a
        /// process that never opens a window (or is not on Linux) pays nothing.
        /// </summary>
        private void EnsureWakeFd()
        {
            if (_wakeFd >= 0) return;
            try
            {
                _wakeFd = eventfd(0, EFD_NONBLOCK | EFD_CLOEXEC);
            }
            catch (EntryPointNotFoundException)
            {
                _wakeFd = -1;
            }
        }

        private const int EFD_CLOEXEC = 0x80000;   // O_CLOEXEC
        private const int EFD_NONBLOCK = 0x800;    // O_NONBLOCK

        [DllImport("libc", SetLastError = true)]
        private static extern int eventfd(uint initval, int flags);

        [DllImport("libc", SetLastError = true)]
        private static extern unsafe nint write(int fd, void* buf, nuint count);

        [DllImport("libc", SetLastError = true)]
        private static extern unsafe nint read(int fd, void* buf, nuint count);

        /// <summary>
        /// Block the dispatcher thread until <see cref="Signal"/> is called or <paramref name="timeoutMilliseconds"/>
        /// elapses (<see cref="Timeout.Infinite"/> waits indefinitely). Returns false once the loop
        /// has been shut down, which the caller treats as "exit the frame".
        /// </summary>
        internal bool Wait(int timeoutMilliseconds)
        {
            if (_disposed)
            {
                return false;
            }

            if (OperatingSystem.IsWindows())
            {
                // On Windows the UI thread owns a Win32 HWND whose mouse/keyboard/paint input arrives
                // as thread messages. A bare managed event wait (below) never observes those, so the
                // window renders but can't be interacted with (spinning cursor). Wait for EITHER the
                // dispatcher wake event OR a queued message, then drain the message queue so each HWND's
                // WndProc runs and routes input into WPF (which in turn Signal()s us with real work).
                WaitWindows(timeoutMilliseconds);
            }
            else if (MS.Internal.Interop.Wayland.WaylandDisplay.IsActive)
            {
                // Linux/Wayland: the same shape as the Windows arm, for the same reason. Compositor
                // input arrives on the connection's file descriptor, which a bare managed event wait
                // never observes -- the window would render once and then be completely inert. Block
                // on {wayland fd, wake eventfd}, then dispatch, so input routes into WPF (which in
                // turn Signal()s us with real work).
                WaitLinux(timeoutMilliseconds);
            }
            else
            {
                _wake.WaitOne(timeoutMilliseconds);
            }
            return !_disposed;
        }

        private unsafe void WaitLinux(int timeoutMilliseconds)
        {
            EnsureWakeFd();

            // A held key repeats on a client-side timer (Wayland sends repeat_info and expects the
            // client to synthesise them), so the wait must not outlast the next repeat or the key
            // stops repeating whenever nothing else is waking the loop.
            int deadline = MS.Internal.Interop.Wayland.WaylandWindow.NextDeadlineMs();
            int timeout = timeoutMilliseconds;
            if (deadline >= 0 && (timeout < 0 || deadline < timeout))
            {
                timeout = deadline;
            }

            // Never block on a signal that has ALREADY arrived. The managed event is the only record
            // of a Signal that happened before this loop first created its eventfd -- and with the
            // usual infinite timeout, missing one does not merely delay the frame, it hangs the app
            // forever with the window mapped but blank.
            if (_wake.WaitOne(0))
            {
                timeout = 0;
            }

            // ReadEvents owns the prepare_read/flush/poll/read_events sequence -- getting that order
            // wrong deadlocks the moment the app idles (see WaylandDisplay.ReadEvents).
            MS.Internal.Interop.Wayland.WaylandDisplay.ReadEvents(timeout, _wakeFd);

            if (_wakeFd >= 0)
            {
                // Drain the eventfd counter. It is non-blocking, so an empty one just returns EAGAIN.
                ulong scratch;
                read(_wakeFd, &scratch, 8);
            }

            // Consume any managed signal too, so the AutoResetEvent and the eventfd stay in step for
            // code paths that still wait on the event itself.
            _wake.WaitOne(0);
        }

        private void WaitWindows(int timeoutMilliseconds)
        {
            IntPtr wake = _wake.SafeWaitHandle.DangerousGetHandle();
            Span<IntPtr> handles = stackalloc IntPtr[1] { wake };
            uint timeout = timeoutMilliseconds < 0 ? INFINITE : (uint)timeoutMilliseconds;

            unsafe
            {
                fixed (IntPtr* pHandles = handles)
                {
                    // Returns 0 when the wake event is signalled (AutoResetEvent consumed), 1 when a
                    // message is available, or WAIT_TIMEOUT. MWMO_INPUTAVAILABLE also reports messages
                    // already sitting in the queue on entry.
                    MsgWaitForMultipleObjectsEx(1, pHandles, timeout, QS_ALLINPUT, MWMO_INPUTAVAILABLE);
                }
            }

            // Drain everything currently queued so no input is left stranded until the next wake.
            while (!_disposed && PeekMessageW(out MSG msg, IntPtr.Zero, 0, 0, PM_REMOVE))
            {
                if (msg.message == WM_QUIT)
                {
                    // Preserve the quit for the outer application loop, then stop pumping.
                    PostQuitMessage((int)msg.wParam);
                    break;
                }
                TranslateMessage(ref msg);
                DispatchMessageW(ref msg);
            }
        }

        private const uint INFINITE = 0xFFFFFFFF;
        private const uint QS_ALLINPUT = 0x04FF;
        private const uint MWMO_INPUTAVAILABLE = 0x0004;
        private const uint PM_REMOVE = 0x0001;
        private const uint WM_QUIT = 0x0012;

        [StructLayout(LayoutKind.Sequential)]
        private struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public int pt_x;
            public int pt_y;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern unsafe uint MsgWaitForMultipleObjectsEx(uint nCount, IntPtr* pHandles, uint dwMilliseconds, uint dwWakeMask, uint dwFlags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool PeekMessageW(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);
        [DllImport("user32.dll")]
        private static extern bool TranslateMessage(ref MSG lpMsg);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr DispatchMessageW(ref MSG lpMsg);
        [DllImport("user32.dll")]
        private static extern void PostQuitMessage(int nExitCode);

        /// <summary>
        /// Permanently stop the loop and release any thread blocked in <see cref="Wait"/> so the
        /// outermost frame can unwind. The underlying handle is intentionally not disposed here:
        /// <see cref="Signal"/> may be called concurrently from other threads during shutdown, and
        /// leaving the handle to finalization avoids an ObjectDisposedException race (a dispatcher
        /// is a long-lived, per-thread object, so at most one handle lingers per UI thread).
        /// </summary>
        internal void Shutdown()
        {
            _disposed = true;
            _wake.Set();
        }
    }
}
