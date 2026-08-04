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
                Woken?.Invoke();
            }
        }

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
            else
            {
                _wake.WaitOne(timeoutMilliseconds);
            }
            return !_disposed;
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
