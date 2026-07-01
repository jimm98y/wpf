// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

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
        internal void Signal()
        {
            if (!_disposed)
            {
                _wake.Set();
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

            _wake.WaitOne(timeoutMilliseconds);
            return !_disposed;
        }

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
