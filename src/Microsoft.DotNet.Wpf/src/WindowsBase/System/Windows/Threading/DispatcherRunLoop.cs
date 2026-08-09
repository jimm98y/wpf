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
    /// That Win32 plumbing has been replaced by this single managed implementation, so the
    /// dispatcher behaves the same on Windows, macOS and Linux.
    ///
    /// The queue itself is unchanged: a caller signals that work is available (or that the next
    /// <see cref="DispatcherTimer"/> due time has changed) via <see cref="Signal"/>, and the
    /// dispatcher thread blocks in <see cref="Wait"/> until signaled or until its timer deadline
    /// elapses. If a platform needs to co-operate with a native run loop that integration belongs
    /// here and nowhere else -- and two platforms do, for one reason: an OS modal loop (a window
    /// drag or resize, a tracking menu) takes the thread and never comes back to <see cref="Wait"/>,
    /// so a managed wake it cannot see means the queue stops being serviced mid-gesture. Windows is
    /// signalled through a message-only window and a Win32 timer, macOS through a CFRunLoopSource
    /// and CFRunLoopTimer in the common run-loop modes; both call <see cref="Pump"/>. Linux needs
    /// none of it, because a Wayland interactive move is performed by the compositor and never
    /// blocks the client thread.
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

            // Windows: the message-only window has to be created on the thread that will pump it,
            // and this constructor runs on the dispatcher's own thread. See EnsureMessageWindow.
            if (OperatingSystem.IsWindows())
            {
                EnsureMessageWindow();
            }
            else if (OperatingSystem.IsMacOS())
            {
                // Same purpose, different mechanism: CFRunLoopGetCurrent binds to the calling
                // thread, which is the dispatcher's. See EnsureRunLoopSource.
                EnsureRunLoopSource();
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
                // Windows: ALSO signal through a posted window message. See EnsureMessageWindow --
                // an OS modal loop (dragging or resizing a window, tracking a menu) never returns to
                // the loop in Wait, so the managed event alone leaves the dispatcher queue unserviced
                // for as long as the user holds the mouse down. A posted message is delivered by
                // whichever loop is pumping, including that one.
                if (_msgWindow != IntPtr.Zero)
                {
                    PostMessageW(_msgWindow, s_wakeMessage, IntPtr.Zero, IntPtr.Zero);
                }
                // macOS: the same idea through CoreFoundation. Signalling a run-loop source that is
                // registered in the COMMON modes is what reaches AppKit's event-tracking loop; both
                // of these calls are documented thread-safe, which matters because Signal comes from
                // arbitrary threads.
                if (_cfSource != IntPtr.Zero)
                {
                    CFRunLoopSourceSignal(_cfSource);
                    CFRunLoopWakeUp(_cfRunLoop);
                }
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

            // Drain what is currently queued so no input is left stranded until the next wake.
            //
            // Bounded, because dispatching is no longer side-effect free: the wake message runs
            // Pump, Pump services one dispatcher operation, and an operation that leaves more work
            // behind posts another wake message -- which this very loop would then pick up. Left
            // unbounded, a steadily refilled queue (an animation posting a render op every frame,
            // say) would keep us in here indefinitely and the caller would never get to re-test
            // frame.Continue, so exiting a nested frame or shutting down could hang. Returning after
            // a bounded batch costs nothing: the caller loops straight back into Wait, and
            // MWMO_INPUTAVAILABLE reports the still-queued messages immediately.
            int budget = 64;
            while (!_disposed && budget-- > 0 && PeekMessageW(out MSG msg, IntPtr.Zero, 0, 0, PM_REMOVE))
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

        // ---- Windows: co-operating with the OS's own modal message loops ---------------------
        //
        // Everything above assumes the dispatcher thread comes back to Wait. While the user drags
        // or resizes a window, or a menu or scrollbar is tracking, that assumption is false: user32
        // runs its OWN message loop inside DefWindowProc and does not return until the mouse is
        // released. The thread is parked deep inside DispatchMessageW, so Dispatcher's
        // "wait, promote timers, ProcessQueue" loop is not running and NOTHING driven by the
        // dispatcher happens -- no rendering, no animations, no DispatcherTimer ticks -- for the
        // whole gesture.
        //
        // A managed AutoResetEvent is invisible to that loop. Two things are not: a message POSTED
        // to a window of this thread, and a WM_TIMER. So the dispatcher keeps a message-only window
        // whose WndProc services the queue, Signal posts to it, and the dispatcher's timer due-time
        // is mirrored onto a real Win32 timer. This is the Win32 plumbing the class comment says was
        // replaced -- it is kept to the minimum that the OS modal loops require, and only on Windows.

        /// <summary>
        /// Service the dispatcher queue. Set by <see cref="Dispatcher"/>; invoked from the
        /// message-only window's WndProc, i.e. from whatever loop is currently pumping.
        /// </summary>
        internal Action Pump;

        private IntPtr _msgWindow;
        private static uint s_wakeMessage;
        private static readonly object s_classLock = new object();
        private static bool s_classRegistered;
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<IntPtr, DispatcherRunLoop> s_byWindow = new();

        private void EnsureMessageWindow()
        {
            lock (s_classLock)
            {
                if (s_wakeMessage == 0)
                {
                    s_wakeMessage = RegisterWindowMessageW("WpfDispatcherRunLoopWake");
                }

                if (!s_classRegistered)
                {
                    // One class for the process; the WndProc finds the right instance by HWND, so a
                    // per-thread class (whose name would have to be recycled with thread ids) is not
                    // needed. A class that somehow already exists is fine -- CreateWindowEx below is
                    // what actually has to succeed.
                    WNDCLASS wc = default;
                    s_staticWndProc = StaticWndProc;
                    wc.lpfnWndProc = Marshal.GetFunctionPointerForDelegate(s_staticWndProc);
                    wc.lpszClassName = ClassName;
                    RegisterClassW(ref wc);
                    s_classRegistered = true;
                }
            }

            // HWND_MESSAGE: a message-only window -- never shown, never enumerated, and it still
            // receives posted messages and WM_TIMER.
            _msgWindow = CreateWindowExW(0, ClassName, null, 0, 0, 0, 0, 0, HWND_MESSAGE, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (_msgWindow != IntPtr.Zero)
            {
                s_byWindow[_msgWindow] = this;
            }
        }

        private static WndProcDelegate s_staticWndProc;   // rooted for the lifetime of the class

        private static IntPtr StaticWndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            if ((msg == s_wakeMessage || msg == WM_TIMER) && s_byWindow.TryGetValue(hwnd, out DispatcherRunLoop loop) && !loop._disposed)
            {
                loop.Pump?.Invoke();
                return IntPtr.Zero;
            }
            return DefWindowProcW(hwnd, msg, wParam, lParam);
        }

        /// <summary>
        /// Mirror the dispatcher's next timer due time onto a Win32 timer, so DispatcherTimers keep
        /// ticking inside an OS modal loop. <paramref name="delayMilliseconds"/> is clamped to the
        /// OS minimum; a due time already in the past simply fires on the next tick.
        /// </summary>
        internal void SetOsTimer(int delayMilliseconds)
        {
            if (_disposed)
            {
                return;
            }

            if (_msgWindow != IntPtr.Zero)
            {
                SetTimer(_msgWindow, TimerId, (uint)Math.Clamp(delayMilliseconds, USER_TIMER_MINIMUM, 0x7FFFFFFF), IntPtr.Zero);
            }
            else if (_cfTimer != IntPtr.Zero)
            {
                // Re-arming one long-lived timer rather than creating one per deadline: a
                // CFRunLoopTimer stays registered (and stays in the common modes) across fires.
                CFRunLoopTimerSetNextFireDate(_cfTimer, CFAbsoluteTimeGetCurrent() + Math.Max(0, delayMilliseconds) / 1000.0);
            }
        }

        internal void KillOsTimer()
        {
            if (_msgWindow != IntPtr.Zero)
            {
                KillTimer(_msgWindow, TimerId);
            }
            else if (_cfTimer != IntPtr.Zero)
            {
                // No CFRunLoopTimer "disable": push the next fire out of reach instead, which keeps
                // the timer valid and registered so SetOsTimer can simply re-arm it.
                CFRunLoopTimerSetNextFireDate(_cfTimer, CFAbsoluteTimeGetCurrent() + FarFutureSeconds);
            }
        }

        // ---- macOS: co-operating with AppKit's event-tracking run loop -----------------------
        //
        // The macOS pump does not run the run loop itself: Dispatcher.WaitForWork blocks in the
        // managed event and then calls CocoaWindow.PumpEvents, which drains NSApp with
        // nextEventMatchingMask/sendEvent:. The catch is that AppKit handles a titlebar drag and a
        // live resize INSIDE -[NSWindow sendEvent:], where it runs the run loop itself in
        // NSEventTrackingRunLoopMode until the mouse comes up. For that whole gesture the pump never
        // returns, so -- exactly as with the Win32 modal loop -- ProcessQueue never runs and
        // rendering, animations and DispatcherTimers stop.
        //
        // The wake has to be something that tracking loop services, which a managed AutoResetEvent is
        // not. A CFRunLoopSource plus a CFRunLoopTimer registered in the COMMON modes are: common
        // modes is precisely the set that includes the tracking mode. (iOS already relies on the same
        // property with its CADisplayLink.) Outside a tracking loop these are inert -- nothing runs
        // the run loop, and the managed event keeps driving the pump as before -- so this only ever
        // adds behaviour during the gestures that were broken.

        private IntPtr _cfRunLoop;
        private IntPtr _cfSource;
        private IntPtr _cfTimer;
        private GCHandle _cfSelf;
        private static CFRunLoopPerformCallBack s_performCallback;
        private static CFRunLoopTimerCallBack s_timerCallback;

        private const double FarFutureSeconds = 1.0e9;

        private void EnsureRunLoopSource()
        {
            _cfRunLoop = CFRunLoopGetCurrent();
            if (_cfRunLoop == IntPtr.Zero)
            {
                return;
            }

            // kCFRunLoopCommonModes is a CFStringRef constant whose CONTENTS are that same name, and
            // CFRunLoop matches modes by string equality -- so a string built here is interchangeable
            // with the exported symbol, and no dlsym dance is needed. CocoaWindow does the same for
            // kCFRunLoopDefaultMode.
            IntPtr commonModes = CFStringCreateWithCString(IntPtr.Zero, "kCFRunLoopCommonModes", kCFStringEncodingUTF8);
            if (commonModes == IntPtr.Zero)
            {
                return;
            }

            _cfSelf = GCHandle.Alloc(this, GCHandleType.Weak);
            IntPtr info = GCHandle.ToIntPtr(_cfSelf);

            s_performCallback ??= PerformCallback;
            CFRunLoopSourceContext sourceContext = default;
            sourceContext.info = info;
            sourceContext.perform = Marshal.GetFunctionPointerForDelegate(s_performCallback);
            _cfSource = CFRunLoopSourceCreate(IntPtr.Zero, 0, ref sourceContext);
            if (_cfSource != IntPtr.Zero)
            {
                CFRunLoopAddSource(_cfRunLoop, _cfSource, commonModes);
            }

            s_timerCallback ??= TimerCallback;
            CFRunLoopTimerContext timerContext = default;
            timerContext.info = info;
            // Created already parked in the far future and re-armed by SetOsTimer. A repeating
            // interval (rather than one-shot) keeps the timer valid after it fires.
            _cfTimer = CFRunLoopTimerCreate(IntPtr.Zero,
                                            CFAbsoluteTimeGetCurrent() + FarFutureSeconds,
                                            FarFutureSeconds,
                                            0, 0,
                                            Marshal.GetFunctionPointerForDelegate(s_timerCallback),
                                            ref timerContext);
            if (_cfTimer != IntPtr.Zero)
            {
                CFRunLoopAddTimer(_cfRunLoop, _cfTimer, commonModes);
            }

            CFRelease(commonModes);
        }

        private static void PerformCallback(IntPtr info) => PumpFromRunLoop(info);
        private static void TimerCallback(IntPtr timer, IntPtr info) => PumpFromRunLoop(info);

        private static void PumpFromRunLoop(IntPtr info)
        {
            if (info == IntPtr.Zero)
            {
                return;
            }

            DispatcherRunLoop loop = GCHandle.FromIntPtr(info).Target as DispatcherRunLoop;
            if (loop is null || loop._disposed)
            {
                return;
            }

            // A live resize is only noticed by CocoaWindow's post-drain size poll, which the tracking
            // loop is currently starving; run it here so the window re-lays-out during the gesture
            // instead of Core Animation stretching the old drawable until mouse-up.
            try { MS.Internal.Interop.CocoaWindow.ReconcileWindows(); } catch { /* never break the pump */ }

            loop.Pump?.Invoke();
        }

        private const uint kCFStringEncodingUTF8 = 0x08000100;

        [StructLayout(LayoutKind.Sequential)]
        private struct CFRunLoopSourceContext
        {
            public nint version;
            public IntPtr info;
            public IntPtr retain;
            public IntPtr release;
            public IntPtr copyDescription;
            public IntPtr equal;
            public IntPtr hash;
            public IntPtr schedule;
            public IntPtr cancel;
            public IntPtr perform;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct CFRunLoopTimerContext
        {
            public nint version;
            public IntPtr info;
            public IntPtr retain;
            public IntPtr release;
            public IntPtr copyDescription;
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void CFRunLoopPerformCallBack(IntPtr info);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void CFRunLoopTimerCallBack(IntPtr timer, IntPtr info);

        private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

        [DllImport(CoreFoundation)]
        private static extern IntPtr CFRunLoopGetCurrent();
        [DllImport(CoreFoundation)]
        private static extern IntPtr CFRunLoopSourceCreate(IntPtr allocator, nint order, ref CFRunLoopSourceContext context);
        [DllImport(CoreFoundation)]
        private static extern void CFRunLoopAddSource(IntPtr runLoop, IntPtr source, IntPtr mode);
        [DllImport(CoreFoundation)]
        private static extern void CFRunLoopSourceSignal(IntPtr source);
        [DllImport(CoreFoundation)]
        private static extern void CFRunLoopSourceInvalidate(IntPtr source);
        [DllImport(CoreFoundation)]
        private static extern void CFRunLoopWakeUp(IntPtr runLoop);
        [DllImport(CoreFoundation)]
        private static extern IntPtr CFRunLoopTimerCreate(IntPtr allocator, double fireDate, double interval, uint flags, nint order, IntPtr callout, ref CFRunLoopTimerContext context);
        [DllImport(CoreFoundation)]
        private static extern void CFRunLoopAddTimer(IntPtr runLoop, IntPtr timer, IntPtr mode);
        [DllImport(CoreFoundation)]
        private static extern void CFRunLoopTimerSetNextFireDate(IntPtr timer, double fireDate);
        [DllImport(CoreFoundation)]
        private static extern void CFRunLoopTimerInvalidate(IntPtr timer);
        [DllImport(CoreFoundation)]
        private static extern double CFAbsoluteTimeGetCurrent();
        [DllImport(CoreFoundation)]
        private static extern IntPtr CFStringCreateWithCString(IntPtr allocator, [MarshalAs(UnmanagedType.LPUTF8Str)] string cStr, uint encoding);
        [DllImport(CoreFoundation)]
        private static extern void CFRelease(IntPtr cf);

        private const string ClassName = "WpfDispatcherRunLoop";
        private const uint WM_TIMER = 0x0113;
        private const int USER_TIMER_MINIMUM = 10;
        private static readonly IntPtr HWND_MESSAGE = new IntPtr(-3);
        private static readonly nuint TimerId = 1;

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WNDCLASS
        {
            public uint style;
            public IntPtr lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpszMenuName;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern ushort RegisterClassW(ref WNDCLASS lpWndClass);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateWindowExW(uint exStyle, string className, string windowName, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr DefWindowProcW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool PostMessageW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern uint RegisterWindowMessageW(string lpString);
        [DllImport("user32.dll")]
        private static extern nuint SetTimer(IntPtr hwnd, nuint id, uint elapseMs, IntPtr func);
        [DllImport("user32.dll")]
        private static extern bool KillTimer(IntPtr hwnd, nuint id);
        [DllImport("user32.dll")]
        private static extern bool DestroyWindow(IntPtr hwnd);

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

            // The message-only window belongs to this thread, and Shutdown runs on it. Drop it from
            // the lookup first so a message still in flight cannot find a half-torn-down loop.
            IntPtr window = _msgWindow;
            if (window != IntPtr.Zero)
            {
                _msgWindow = IntPtr.Zero;
                s_byWindow.TryRemove(window, out _);
                KillTimer(window, TimerId);
                DestroyWindow(window);
            }

            // Same on macOS: invalidate first (which removes them from every mode they were added
            // to), then release, then drop the handle the callbacks resolve through.
            IntPtr source = _cfSource, timer = _cfTimer;
            _cfSource = IntPtr.Zero;
            _cfTimer = IntPtr.Zero;
            if (timer != IntPtr.Zero)
            {
                CFRunLoopTimerInvalidate(timer);
                CFRelease(timer);
            }
            if (source != IntPtr.Zero)
            {
                CFRunLoopSourceInvalidate(source);
                CFRelease(source);
            }
            if (_cfSelf.IsAllocated)
            {
                _cfSelf.Free();
            }
        }
    }
}
