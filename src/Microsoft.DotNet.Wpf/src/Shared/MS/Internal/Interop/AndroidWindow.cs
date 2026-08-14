// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The Android windowing backend — the Android sibling of CocoaWindow, BrowserWindow and UIKitWindow.
//
// Android is shaped like iOS from WPF's point of view: one fullscreen activity, so a WPF "window" is
// a view inside it, popups are borderless sibling views presenting through their own swap chains
// (NativePlatform.SupportsLayeredWindows is false off Windows), and the OS owns the main run loop so
// the dispatcher must be a guest in it (Choreographer here, CADisplayLink there).
//
// Two things are NOT like iOS, and they are why this file looks the way it does:
//
//  1. WindowsBase cannot create Android views. On iOS, UIKitWindow synthesises a UIView subclass at
//     run time through the Objective-C runtime; there is no equivalent on Android, because a Java
//     View subclass is BYTECODE and cannot be conjured through JNI. What creates views instead is
//     the app head, which is a .NET-for-Android assembly and gets the Java bindings for free. So the
//     Java-side half of this backend is an interface, <see cref="IAndroidHost"/>, that the head
//     implements and installs in <see cref="Host"/>; everything WPF-shaped (handles, z-order, hit
//     testing, coordinate conversion, touch-to-mouse) stays here, where it is testable and shared.
//     Nothing below touches JNI: the only native calls are two NDK refcount functions out of
//     libandroid.so, which need no JNIEnv.
//
//  2. The native window comes and goes. An android.view.Surface — and the ANativeWindow the GPU
//     renders into — exists only between surfaceCreated and surfaceDestroyed, and Android tears it
//     down every time the activity stops (home button, screen off, task switch), handing back a
//     DIFFERENT one on resume. A WPF window handle, by contrast, must be stable for the window's
//     whole life: it is what HwndSource, HwndTarget, mouse capture and every WM_ message key off.
//     So the handle here is SYNTHETIC (as on the browser head), and the live ANativeWindow hangs off
//     it, changing underneath. The compositor asks for it through AndroidPlatform.NativeWindowResolver
//     — installed by InstallCompositorResolver below — and rebuilds its wgpu surface when it changes.
//

using System;
using System.Collections.Generic;

namespace MS.Internal.Interop
{
    /// <summary>
    /// The Java-side half of the Android windowing backend, implemented by the app head (a
    /// net10.0-android assembly) and installed in <see cref="AndroidWindow.Host"/> before WPF starts.
    /// All sizes and positions are in DEVICE PIXELS, which is what Android's own view geometry uses.
    /// Every member is called on the UI (dispatcher) thread except <see cref="RequestWake"/>.
    /// </summary>
    public interface IAndroidHost
    {
        /// <summary>
        /// Create the native view backing a WPF window and add it to the activity's content view.
        /// The view must render through a Surface and report it back with
        /// <see cref="AndroidWindow.NotifySurfaceChanged"/> / <see cref="AndroidWindow.NotifySurfaceDestroyed"/>,
        /// and forward its touches with <see cref="AndroidWindow.NotifyTouch"/>. Returns false if the
        /// view could not be created (no activity yet), which makes the WPF window a no-op.
        /// </summary>
        bool CreateView(IntPtr handle, int x, int y, int width, int height, bool borderless);

        /// <summary>Move/resize a view created by <see cref="CreateView"/> (top-left device pixels).</summary>
        void SetViewFrame(IntPtr handle, int x, int y, int width, int height);

        /// <summary>Show or hide a view created by <see cref="CreateView"/>, keeping it in the
        /// hierarchy so it can be shown again (WPF's Window.Hide(), not Close()).</summary>
        void SetViewVisible(IntPtr handle, bool visible);

        /// <summary>Remove a view created by <see cref="CreateView"/> from the activity.</summary>
        void DestroyView(IntPtr handle);

        /// <summary>
        /// Raise the soft keyboard over a view and put it in text-editing mode. The view has to
        /// answer onCheckIsTextEditor/onCreateInputConnection for this to do anything, which is why
        /// it belongs to the host: WindowsBase cannot subclass an Android View.
        /// </summary>
        void ShowSoftKeyboard(IntPtr handle, bool multiline, bool password);

        /// <summary>Dismiss the soft keyboard.</summary>
        void HideSoftKeyboard(IntPtr handle);

        /// <summary>
        /// Report the caret rectangle (device pixels, relative to the view) so the input method can
        /// keep its candidate strip clear of the text being typed.
        /// </summary>
        void SetImeCursorRect(IntPtr handle, int x, int y, int width, int height);

        /// <summary>Display density: device pixels per density-independent pixel, i.e. WPF's DPI scale
        /// (DisplayMetrics.Density — 1.0 at mdpi, 3.5 on a modern phone).</summary>
        double Density { get; }

        /// <summary>
        /// The display's refresh rate in Hz (Display.getRefreshRate), or 0 when it cannot be read.
        /// Phones have shipped 90Hz and 120Hz panels for years; WPF paces itself against this, and
        /// without it every one of them ran at 60. See IPlatformWindow.GetRefreshRateHz.
        /// </summary>
        double RefreshRateHz => 0;

        /// <summary>The activity's content area in device pixels — the whole screen minus system bars.</summary>
        void GetScreenPixels(out int width, out int height);

        /// <summary>
        /// Install a per-frame callback (Choreographer.postFrameCallback), delivered on the UI thread.
        /// Returns false when not running under an activity, which unwinds the dispatcher frame.
        /// </summary>
        bool StartFrameCallback(Action tick);

        /// <summary>Stop the per-frame callback for good (dispatcher shutdown).</summary>
        void StopFrameCallback();

        /// <summary>Pause/resume the per-frame callback. A paused pump costs nothing, which is how an
        /// idle WPF app stops draining the battery; see <see cref="AndroidWindow.SetFrameCallbackPaused"/>.</summary>
        void SetFrameCallbackPaused(bool paused);

        /// <summary>Resume the pump because work was queued. Callable from ANY thread (that is the whole
        /// point of Dispatcher.BeginInvoke), so implementations must hop to the UI thread themselves.</summary>
        void RequestWake();

        /// <summary>Resume the pump in <paramref name="seconds"/>, for a pending DispatcherTimer, replacing
        /// any previously scheduled wake. UI thread only.</summary>
        void ScheduleWake(double seconds);
    }

    /// <summary>
    /// A single WPF window on Android: a view inside the app's one activity. The handle is synthetic
    /// and stable; the ANativeWindow behind it is not (see the file header).
    /// </summary>
    public sealed class AndroidWindow : IPlatformWindow
    {
        private static readonly Dictionary<IntPtr, AndroidWindow> s_byHandle = new Dictionary<IntPtr, AndroidWindow>();
        private static readonly List<AndroidWindow> s_zOrder = new List<AndroidWindow>();   // last = topmost
        private static long s_nextHandle;

        private IntPtr _handle;
        private IntPtr _nativeWindow;                 // ANativeWindow*, acquired; Zero when there is no live Surface
        private int _xPixels, _yPixels;               // top-left of the view, device pixels
        private int _widthPixels, _heightPixels;      // last size the head reported, device pixels

        /// <summary>The synthetic handle that stands in for the HWND.</summary>
        public IntPtr Handle => _handle;

        public bool IsBorderless { get; private set; }

        public static IntPtr MouseCaptureHandle { get; set; }

        /// <summary>Raised when the view's content size changed (device pixels).</summary>
        public event Action<int, int> Resized;

        /// <summary>
        /// Android changes density only when the app moves to a display with another one (desktop
        /// mode, external screen); declared to satisfy IPlatformWindow and raised by
        /// <see cref="NotifyScaleChanged"/>.
        /// </summary>
        public event Action<double> ScaleChanged;

        /// <summary>The Java-side half of this backend, installed by the app head before WPF starts.</summary>
        public static IAndroidHost Host { get; set; }

        public static AndroidWindow FromHandle(IntPtr handle)
            => s_byHandle.TryGetValue(handle, out AndroidWindow w) ? w : null;

        /// <param name="x">Origin in top-left device PIXELS (borderless popups; the main window fills the activity).</param>
        public void Create(string title, int x, int y, int width, int height, bool borderless)
        {
            IsBorderless = borderless;
            InstallCompositorResolver();

            // Start well above zero and step by a page so these never collide with each other, with
            // HwndWrapper's own synthetic handles, or with a real pointer. They are only ever compared
            // for equality and looked up in s_byHandle — never dereferenced.
            _handle = new IntPtr(System.Threading.Interlocked.Add(ref s_nextHandle, 0x1000) + 0x5A00_0000);

            _xPixels = x;
            _yPixels = y;
            _widthPixels = width;
            _heightPixels = height;

            // Android has no window manager: a top-level window is always the activity's content
            // area. WPF asks for its own default/desired size (1024x768 etc.), which would leave the
            // app rendering into a rectangle in a corner. Only real top-level windows are snapped —
            // borderless views are Popups/menus, which DO need their requested geometry.
            if (!borderless)
            {
                Host?.GetScreenPixels(out _widthPixels, out _heightPixels);
                _xPixels = _yPixels = 0;
            }

            s_byHandle[_handle] = this;
            s_zOrder.Add(this);

            if (Host is null || !Host.CreateView(_handle, _xPixels, _yPixels, _widthPixels, _heightPixels, borderless))
            {
                // No activity to host the view. Keep the handle registered so WPF's teardown path
                // still finds this window, but it will never present anything.
                return;
            }

            RaiseResized();
        }

        /// <summary>Show/hide the view (WPF's ShowWindow SW_HIDE/SW_SHOW).</summary>
        /// <remarks>
        /// The view stays in the activity's hierarchy, keeping its Surface and this window's handle
        /// valid, so a caller that hides and re-shows a cached window (a docking adorner between
        /// drags) gets the same window back rather than a destroyed one.
        /// </remarks>
        public void SetVisible(bool visible)
        {
            if (_handle == IntPtr.Zero) return;
            Host?.SetViewVisible(_handle, visible);
        }

        /// <summary>No-op: Android windows are not user-movable (one fullscreen activity).</summary>
        public void BeginMoveDrag() { }

        public void Destroy()
        {
            if (_handle == IntPtr.Zero)
                return;

            Host?.DestroyView(_handle);
            ReleaseNativeWindow();
            s_byHandle.Remove(_handle);
            s_zOrder.Remove(this);
            _handle = IntPtr.Zero;
        }

        /// <summary>Resize the content area, in POINTS (DIPs at scale 1).</summary>
        public void SetContentSize(int width, int height)
        {
            double scale = GetBackingScale();
            SetContentSizePixels((int)Math.Round(width * scale), (int)Math.Round(height * scale));
        }

        /// <summary>Resize from DEVICE PIXELS, as WPF's SetWindowPos supplies.</summary>
        public void SetContentSizePixels(int cx, int cy)
        {
            if (_handle == IntPtr.Zero) return;

            // Fullscreen top-level: keep OUR size, but still report it. RaiseResized drives the
            // synthetic WM_SIZE -> OnResize -> UpdateWindowSettings that tells the compositor its
            // target size; swallowing it leaves the target at 0x0 and NOTHING is composited (the
            // window is correctly sized and interactive, and the screen stays blank). Exactly the
            // same trap as UIKitWindow.SetContentSize.
            if (IsFullScreenTopLevel) { RaiseResized(); return; }

            _widthPixels = cx;
            _heightPixels = cy;
            Host?.SetViewFrame(_handle, _xPixels, _yPixels, _widthPixels, _heightPixels);
            RaiseResized();
        }

        public void SetFrameOrigin(int xPixels, int yPixels)
        {
            if (_handle == IntPtr.Zero || IsFullScreenTopLevel) return;
            _xPixels = xPixels;
            _yPixels = yPixels;
            Host?.SetViewFrame(_handle, _xPixels, _yPixels, _widthPixels, _heightPixels);
        }

        public void GetContentSize(out int width, out int height)
        {
            double scale = GetBackingScale();
            width = (int)Math.Round(_widthPixels / scale);
            height = (int)Math.Round(_heightPixels / scale);
        }

        public void GetPixelSize(out int width, out int height)
        {
            width = _widthPixels;
            height = _heightPixels;
        }

        /// <summary>Android has no window caption, so the outer window size equals the content size
        /// (same as the browser and iOS heads).</summary>
        public void GetWindowPixelSize(out int width, out int height) => GetPixelSize(out width, out height);

        public void GetClientScreenOriginPixels(out int sx, out int sy)
        {
            sx = _xPixels;
            sy = _yPixels;
        }

        /// <summary>
        /// A top-level (non-Popup) window on Android is pinned to the activity's content area, so
        /// WPF's SetWindowPos calls for it are ignored — GetPixelSize keeps reporting the real size
        /// and layout follows that. Popups keep their requested geometry.
        /// </summary>
        private bool IsFullScreenTopLevel => !IsBorderless && Host != null;

        public double GetRefreshRateHz() => Host?.RefreshRateHz ?? 0;

        public double GetBackingScale()
        {
            double density = Host?.Density ?? 1.0;
            return density > 0 ? density : 1.0;
        }

        /// <summary>Topmost registered window containing the point (top-left device pixels).</summary>
        public static IntPtr HitTest(int x, int y)
        {
            for (int i = s_zOrder.Count - 1; i >= 0; i--)
            {
                AndroidWindow w = s_zOrder[i];
                if (w._handle == IntPtr.Zero) continue;

                if (x >= w._xPixels && x < w._xPixels + w._widthPixels &&
                    y >= w._yPixels && y < w._yPixels + w._heightPixels)
                    return w._handle;
            }
            return IntPtr.Zero;
        }

        /// <summary>The activity's content area in device pixels; Android has no work-area distinction
        /// (the system bars are already excluded).</summary>
        public static bool GetPrimaryScreenPixels(
            out int monLeft, out int monTop, out int monRight, out int monBottom,
            out int workLeft, out int workTop, out int workRight, out int workBottom)
        {
            monLeft = monTop = workLeft = workTop = 0;
            monRight = workRight = 0;
            monBottom = workBottom = 0;

            if (Host is null) return false;

            Host.GetScreenPixels(out int w, out int h);
            monRight = workRight = w;
            monBottom = workBottom = h;
            return true;
        }

        // ---- Surface lifetime ------------------------------------------------------
        //
        // The head reports its view's Surface through these. Both the ARRIVAL of a native window and
        // its REPLACEMENT matter to the compositor (see the file header), which notices by comparing
        // the pointer it built its wgpu surface on; nothing extra has to be signalled here.

        /// <summary>
        /// A view's Surface was created or resized. <paramref name="nativeWindow"/> is the
        /// ANativeWindow* obtained from it (ANativeWindow_fromSurface), which this window takes a
        /// reference on for as long as it uses it.
        /// </summary>
        public static void NotifySurfaceChanged(IntPtr handle, IntPtr nativeWindow, int widthPixels, int heightPixels)
        {
            if (!s_byHandle.TryGetValue(handle, out AndroidWindow w)) return;

            if (w._nativeWindow != nativeWindow)
            {
                w.ReleaseNativeWindow();
                if (nativeWindow != IntPtr.Zero)
                    ANativeWindow_acquire(nativeWindow);
                w._nativeWindow = nativeWindow;
            }

            w._widthPixels = widthPixels;
            w._heightPixels = heightPixels;
            w.RaiseResized();
        }

        /// <summary>A view's Surface is going away (the activity is stopping). The window keeps its
        /// handle and its size; it simply has nothing to present into until a new Surface arrives.</summary>
        public static void NotifySurfaceDestroyed(IntPtr handle)
        {
            if (!s_byHandle.TryGetValue(handle, out AndroidWindow w)) return;
            w.ReleaseNativeWindow();
        }

        /// <summary>Called by the head when the display density changed.</summary>
        /// <summary>
        /// Text-input callbacks from the host's InputConnection. They land on the Android UI thread,
        /// which is the dispatcher thread, so they reach the editor directly.
        /// </summary>
        public static void NotifyComposingText(string text, int cursorPosition)
            => AndroidTextInput.NotifyComposingText(text, cursorPosition);

        public static void NotifyCommitText(string text, int cursorPosition)
            => AndroidTextInput.NotifyCommitText(text, cursorPosition);

        public static void NotifyFinishComposing()
            => AndroidTextInput.NotifyFinishComposing();

        public static void NotifyDeleteSurrounding(int before, int after)
            => AndroidTextInput.NotifyDeleteSurrounding(before, after);

        public static void NotifyScaleChanged()
        {
            foreach (AndroidWindow w in s_zOrder)
                w.ScaleChanged?.Invoke(w.GetBackingScale());
        }

        private void ReleaseNativeWindow()
        {
            if (_nativeWindow == IntPtr.Zero) return;
            ANativeWindow_release(_nativeWindow);
            _nativeWindow = IntPtr.Zero;
        }

        private void RaiseResized() => Resized?.Invoke(_widthPixels, _heightPixels);

        // Refcounting the ANativeWindow is what makes the handle indirection safe: the one the head
        // hands over is retained for as long as this window points at it, so a frame already in
        // flight cannot end up rendering into freed memory when the activity stops. These are NDK
        // entry points (libandroid.so) and take no JNIEnv.
        [System.Runtime.InteropServices.DllImport("android")]
        private static extern void ANativeWindow_acquire(IntPtr window);

        [System.Runtime.InteropServices.DllImport("android")]
        private static extern void ANativeWindow_release(IntPtr window);

        // ---- The compositor's view of all this -------------------------------------

        private static bool s_resolverInstalled;

        /// <summary>The live ANativeWindow* behind a WPF window handle, or Zero when there is none.</summary>
        public static IntPtr GetNativeWindow(IntPtr handle)
            => s_byHandle.TryGetValue(handle, out AndroidWindow w) ? w._nativeWindow : IntPtr.Zero;

        /// <summary>
        /// The window's top-left corner in device pixels within the activity. The compositor needs it
        /// when it draws a popup into its owner's surface (NativePlatform.PopupsShareOwnerSurface).
        /// </summary>
        public static void GetWindowOrigin(IntPtr handle, out int x, out int y)
        {
            x = y = 0;
            if (s_byHandle.TryGetValue(handle, out AndroidWindow w))
                w.GetClientScreenOriginPixels(out x, out y);
        }

        /// <summary>
        /// Whether the window is opaque. Top-level windows are; POPUPS are not -- a Popup's chrome is
        /// a rounded rectangle with a drop shadow drawn onto a transparent background, so its surface
        /// must carry alpha or the transparent parts composite as black. The head backs a borderless
        /// window with a translucent view to match (see AndroidHost.CreateView).
        /// </summary>
        public static bool IsWindowOpaque(IntPtr handle)
            => !s_byHandle.TryGetValue(handle, out AndroidWindow w) || !w.IsBorderless;

        /// <summary>
        /// Hand <see cref="GetNativeWindow"/> to the WebGPU engine. Loaded reflectively for the same
        /// reason DUCE loads the whole engine reflectively (Common/Graphics/exports.cs): WPF must keep
        /// no compile-time dependency on the graphics backend, in either direction. Failing silently
        /// is correct — an app running on native milcore has no engine to tell.
        /// </summary>
        private static void InstallCompositorResolver()
        {
            if (s_resolverInstalled) return;
            s_resolverInstalled = true;

            try
            {
                Type seam = Type.GetType("Microsoft.Wpf.Interop.WebGpu.AndroidPlatform, Microsoft.Wpf.Interop.WebGpu", throwOnError: false);
                const System.Reflection.BindingFlags PublicStatic =
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static;
                seam?.GetProperty("NativeWindowResolver", PublicStatic)?.SetValue(null, (Func<IntPtr, IntPtr>)GetNativeWindow);
                seam?.GetProperty("WindowOpaqueQuery", PublicStatic)?.SetValue(null, (Func<IntPtr, bool>)IsWindowOpaque);

                // WindowOriginCallback is declared by the engine (it has out parameters, so no Func
                // shape fits); bind to it by the property's own delegate type.
                System.Reflection.PropertyInfo? origin = seam?.GetProperty("WindowOriginQuery", PublicStatic);
                origin?.SetValue(null, Delegate.CreateDelegate(
                    origin.PropertyType,
                    typeof(AndroidWindow).GetMethod(nameof(GetWindowOrigin), PublicStatic)!));
            }
            catch (Exception e)
            {
                Console.WriteLine($"WPF Android: could not install the compositor window resolver: {e}");
            }
        }

        // ---- The Choreographer dispatcher pump -------------------------------------
        //
        // Android owns the main run loop exactly as UIKit does: Activity.onCreate is a callback on
        // the main Looper, so Dispatcher.Run cannot block there — it installs a per-frame callback and
        // returns (see Dispatcher.RunAndroidPump). Choreographer is the Android analogue of
        // CADisplayLink and of the browser's requestAnimationFrame: a display-aligned callback
        // delivered on the UI thread, which is also the dispatcher thread, so the tick body runs
        // synchronously inside it. These are thin forwards to the head, which owns the Java objects;
        // they live here so Dispatcher talks to ONE windowing backend per platform.

        /// <summary>Start the per-frame pump. False when there is no host/activity to drive it.</summary>
        public static bool StartFrameCallback(Action tick) => Host?.StartFrameCallback(tick) ?? false;

        /// <summary>Stop the pump for good (dispatcher shutdown).</summary>
        public static void StopFrameCallback() => Host?.StopFrameCallback();

        /// <summary>Park/un-park the pump so an idle app costs nothing.</summary>
        public static void SetFrameCallbackPaused(bool paused) => Host?.SetFrameCallbackPaused(paused);

        /// <summary>Un-park the pump because work was queued. Callable from any thread.</summary>
        public static void RequestWake() => Host?.RequestWake();

        /// <summary>Un-park the pump in <paramref name="seconds"/>, for a pending DispatcherTimer.</summary>
        public static void ScheduleWake(double seconds) => Host?.ScheduleWake(Math.Max(0.0, seconds));

        // ---- Touch input -----------------------------------------------------------
        //
        // A single touch drives WPF's mouse, exactly as on iOS (see UIKitWindow's notes, which apply
        // verbatim): there is no hover, so a tap is "move to the point, THEN press" — the move must
        // precede the press or WPF button-downs land wherever the previous touch ended. Multi-touch
        // is out of scope; the first pointer wins, as it does for a mouse.
        //
        // A drag becomes wheel rotation for the same reason it does on iOS: WPF scrolls a ScrollViewer
        // from the wheel or the scrollbar, never from a mouse drag over content, so without this a
        // finger drag would move the cursor and nothing would scroll.

        /// <param name="Kind">0 = move, 1 = press, 2 = release, 3 = wheel (synthesized from a drag).
        /// <paramref name="Wheel"/> is set only for kind 3.</param>
        public readonly record struct TouchMessage(IntPtr Window, int Kind, int X, int Y, int TimestampMs, int Wheel = 0);

        /// <summary>Raised on the UI thread as Android delivers touches; consumed by HwndMouseInputProvider.</summary>
        public static event Action<TouchMessage> MouseInput;

        // Same tuning as UIKitWindow, and for the same reasons documented there: below the threshold a
        // touch is a tap rather than a pan; ScrollViewer ignores the wheel MAGNITUDE (any wheel event
        // scrolls SystemParameters.WheelScrollLines * 16px), so distance becomes NOTCHES rather than a
        // bigger delta; and at most a notch of unspent drag is carried so a flick catches up instead of
        // drifting forever.
        private const double PanThresholdPixels = 10;
        private const double LogicalPixelsPerNotch = 3 * 16;
        private const double MaxCarryPixels = 2 * LogicalPixelsPerNotch;
        private const int WheelNotch = 120;

        private static double s_panLastY;      // last reported touch Y, device px
        private static double s_panStartY;     // where this touch went down, device px
        private static double s_panAccum;      // drag not yet turned into a notch, logical px
        private static bool s_panning;         // past the threshold: emitting wheel

        /// <summary>
        /// Raise one touch as the mouse message(s) it stands for. Called by the head from its view's
        /// onTouchEvent, in device pixels relative to that view. Kinds match
        /// <see cref="TouchMessage"/>: 0 = move (ACTION_MOVE), 1 = press (ACTION_DOWN),
        /// 2 = release (ACTION_UP / ACTION_CANCEL).
        /// </summary>
        /// <summary>
        ///  Reports one CONTACT -- one finger or pen tip of a possibly multi-touch gesture -- to
        ///  WPF's touch stack.
        /// </summary>
        /// <remarks>
        ///  <para>
        ///   Separate from <see cref="NotifyTouch"/>, and additional to it rather than replacing it.
        ///   A TouchDevice raises the Touch events and drives Manipulation but does NOT promote
        ///   itself to the mouse, so a head that reported contacts alone would gain pinch and lose
        ///   Button.Click. The host therefore keeps driving the mouse from the primary pointer and
        ///   reports every pointer here as well.
        ///  </para>
        ///  <para>
        ///   Coordinates arrive view-relative, as Android reports them, and the seam wants SCREEN
        ///   device pixels -- so the client origin is added here, which is exactly what
        ///   PlatformTouchDevice subtracts on the way back out.
        ///  </para>
        /// </remarks>
        /// <param name="kind">0 = move, 1 = down, 2 = up, 3 = cancel.</param>
        /// <param name="pressure">0..1 from the digitizer, or negative where it reports none.</param>
        /// <param name="isEraser">The inverted end of a stylus, which Android reports as its own
        /// tool type rather than as a state of the pen.</param>
        public static void NotifyTouchContact(IntPtr handle, int kind, int contactId,
                                              int xPixels, int yPixels, double pressure,
                                              double tiltX, double tiltY, bool isEraser = false)
        {
            // Never let a managed exception unwind into the Java frame that delivered this.
            try
            {
                IPlatformTouchSink sink = PlatformTouch.Sink;
                if (sink is null) return;

                int screenX = xPixels, screenY = yPixels;
                if (s_byHandle.TryGetValue(handle, out AndroidWindow window))
                {
                    window.GetClientScreenOriginPixels(out int originX, out int originY);
                    screenX += originX;
                    screenY += originY;
                }

                uint timestamp = (uint)Environment.TickCount;
                var pen = new PenState(pressure, tiltX, tiltY, isEraser);
                switch (kind)
                {
                    case 1: sink.TouchDown(handle, contactId, screenX, screenY, pen, timestamp); break;
                    case 0: sink.TouchMove(handle, contactId, screenX, screenY, pen, timestamp); break;
                    case 2: sink.TouchUp(handle, contactId, screenX, screenY, timestamp); break;
                    case 3: sink.TouchCancel(handle, contactId); break;
                }
            }
            catch
            {
                // Same contract as NotifyTouch: a managed failure must not reach the Java frame.
            }
        }

        public static void NotifyTouch(IntPtr handle, int kind, int xPixels, int yPixels)
        {
            // Never let a managed exception unwind into the Java frame that delivered this.
            try
            {
                Action<TouchMessage> handler = MouseInput;
                if (handler == null) return;

                double scale = s_byHandle.TryGetValue(handle, out AndroidWindow w) ? w.GetBackingScale() : 1.0;
                handler(new TouchMessage(handle, kind, xPixels, yPixels, Environment.TickCount));

                switch (kind)
                {
                    case 1:   // press: this touch has not panned yet
                        s_panStartY = s_panLastY = yPixels;
                        s_panAccum = 0;
                        s_panning = false;
                        return;

                    case 2:   // release
                        s_panning = false;
                        s_panAccum = 0;
                        return;

                    case 0:
                        if (!s_panning)
                        {
                            if (Math.Abs(yPixels - s_panStartY) < PanThresholdPixels) return;
                            s_panning = true;      // threshold crossed; scroll from here on
                            s_panLastY = yPixels;
                        }

                        // Finger down moves the content down, which is a scroll UP (positive wheel).
                        double deltaLogical = (yPixels - s_panLastY) / scale;
                        s_panLastY = yPixels;
                        EmitWheel(handle, deltaLogical, xPixels, yPixels);
                        return;
                }
            }
            catch (Exception e)
            {
                Console.WriteLine($"WPF Android touch dispatch failed: {e}");
            }
        }

        /// <summary>
        /// Raise a scroll that Android already measured for us — a mouse wheel or trackpad
        /// (ACTION_SCROLL), which is delivered as a distance rather than as touches.
        /// <paramref name="deltaLogical"/> is in density-independent pixels, positive = scroll up.
        /// </summary>
        public static void NotifyScroll(IntPtr handle, double deltaLogical, int xPixels, int yPixels)
        {
            try
            {
                Action<TouchMessage> handler = MouseInput;
                if (handler == null) return;

                // Put the mouse under the pointer first: a wheel report carries no position of its
                // own, so WPF would otherwise scroll whatever was last touched rather than what the
                // pointer is over.
                handler(new TouchMessage(handle, 0, xPixels, yPixels, Environment.TickCount));
                EmitWheel(handle, deltaLogical, xPixels, yPixels);
            }
            catch (Exception e)
            {
                Console.WriteLine($"WPF Android scroll dispatch failed: {e}");
            }
        }

        /// <summary>Turns scrolled DISTANCE into wheel notches and raises them. Shared by the finger
        /// drag and the trackpad/mouse, which differ only in where the distance comes from.</summary>
        private static void EmitWheel(IntPtr handle, double deltaLogical, int x, int y)
        {
            Action<TouchMessage> handler = MouseInput;
            if (handler == null) return;

            s_panAccum += deltaLogical;
            if (Math.Abs(s_panAccum) < LogicalPixelsPerNotch) return;   // not a notch yet

            int sign = Math.Sign(s_panAccum);
            s_panAccum -= sign * LogicalPixelsPerNotch;
            if (Math.Abs(s_panAccum) > MaxCarryPixels) s_panAccum = sign * MaxCarryPixels;

            handler(new TouchMessage(handle, 3, x, y, Environment.TickCount, sign * WheelNotch));
        }
    }
}
