// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The Android windowing backend — the Android sibling of CocoaWindow, BrowserWindow and UIKitWindow.
//
// Android is shaped like iOS from WPF's point of view: one fullscreen activity, so a WPF "window" is
// a view inside it, and popups are borderless sibling views drawn into their owner's surface.
//
// The app runs on a UI thread of its own, not on the main Looper's: WPF's dispatcher blocks there,
// as on the desktop heads, so a modal dialog, a MessageBox or an Invoke's wait is a nested frame like
// anywhere else -- Android's Looper cannot be re-entered, and a guest pump on it could run none. The
// host does what it does to views on the main thread (AndroidHost.OnMain), and what Android reports
// on the main thread -- touches, text, a surface coming or going -- is carried to the app's UI
// thread here (OnUi), as Windows posts input to the thread that owns the window.
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
        /// <param name="fillsScreen">The app's own window, which fills the activity and follows its size;
        /// any other keeps the frame given (a dialog floating over it, a popup).</param>
        /// <param name="ownSurface">A borderless window that presents through a surface of its own (a
        /// borderless form) rather than its owner's (a popup).</param>
        bool CreateView(IntPtr handle, int x, int y, int width, int height, bool borderless, bool fillsScreen, bool ownSurface);

        /// <summary>A window to the front, above the others (SetForegroundWindow).</summary>
        void BringToFront(IntPtr handle) { }

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

        // The app's own window: the first top-level one, which fills the activity. The others float.
        private static IntPtr s_mainHandle;
        private bool _ownSurface;
        private bool IsMain => _handle != IntPtr.Zero && _handle == s_mainHandle;

        /// <summary>A top-level window over the app's own: a dialog, a tool window.</summary>
        public bool IsSecondary => !IsBorderless && !IsMain;

        /// <summary>Whether the app has its own window yet (the first framed one, whichever stack made it).</summary>
        public static bool HasMainWindow { get { lock (s_byHandle) return s_mainHandle != IntPtr.Zero && s_byHandle.ContainsKey(s_mainHandle); } }

        public void SetVisible(bool visible, bool activate)
        {
            SetVisible(visible);
            if (visible && activate) Activate();
        }

        /// <summary>To the front, as SetForegroundWindow brings a window there.</summary>
        public void Activate()
        {
            if (_handle == IntPtr.Zero || (IsBorderless && !_ownSurface)) return;
            lock (s_zOrder) { s_zOrder.Remove(this); s_zOrder.Add(this); }
            s_keyWindow = _handle;
            Host?.BringToFront(_handle);
        }

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
            => Create(title, x, y, width, height, borderless, ownSurface: false);

        /// <summary>As <see cref="Create(string, int, int, int, int, bool)"/>; <paramref name="ownSurface"/>: a
        /// borderless window that presents through a surface of its own (a borderless form, a splash
        /// screen), stacked and activated as a window.</summary>
        public void Create(string title, int x, int y, int width, int height, bool borderless, bool ownSurface)
        {
            // The first thread to make a window is the app's UI thread, whose dispatcher what Android
            // reports goes to (OnUi); a window of another thread (a splash screen's) is made as well, the
            // host doing its view work on the main Looper whoever asks.
            NoteUiThread();

            IsBorderless = borderless;
            _ownSurface = borderless && ownSurface;
            InstallCompositorResolver();

            // Start well above zero and step by a page so these never collide with each other, with
            // HwndWrapper's own synthetic handles, or with a real pointer. They are only ever compared
            // for equality and looked up in s_byHandle — never dereferenced.
            _handle = new IntPtr(System.Threading.Interlocked.Add(ref s_nextHandle, 0x1000) + 0x5A00_0000);

            _xPixels = x;
            _yPixels = y;
            _widthPixels = width;
            _heightPixels = height;

            // The app's own window fills the activity, which is all the desktop there is: WPF asks for
            // its own default/desired size (1024x768 etc.), which would leave the app rendering into a
            // rectangle in a corner. The others -- a dialog, a tool window, a popup -- keep the frame
            // they ask for and float over it, as the browser and iOS heads have them.
            bool main = !borderless && (s_mainHandle == IntPtr.Zero || !s_byHandle.ContainsKey(s_mainHandle));
            if (main)
            {
                Host?.GetScreenPixels(out _widthPixels, out _heightPixels);
                _xPixels = _yPixels = 0;
            }

            lock (s_byHandle) s_byHandle[_handle] = this;
            lock (s_zOrder) s_zOrder.Add(this);
            if (main) s_mainHandle = _handle;

            if (Host is null || !Host.CreateView(_handle, _xPixels, _yPixels, _widthPixels, _heightPixels, borderless, main, _ownSurface))
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
            lock (this) ReleaseNativeWindow();
            lock (s_byHandle) s_byHandle.Remove(_handle);
            lock (s_zOrder) s_zOrder.Remove(this);
            if (s_keyWindow == _handle) s_keyWindow = IntPtr.Zero;
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
        private bool IsFullScreenTopLevel => IsMain && Host != null;

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
            AndroidWindow w;
            lock (s_byHandle) if (!s_byHandle.TryGetValue(handle, out w)) return;

            // the native window at once (the compositor reads it on the app's UI thread), the resize on that thread
            lock (w)
            {
                if (w._nativeWindow != nativeWindow)
                {
                    w.ReleaseNativeWindow();
                    if (nativeWindow != IntPtr.Zero)
                        ANativeWindow_acquire(nativeWindow);
                    w._nativeWindow = nativeWindow;
                }
            }

            OnUi(() =>
            {
                w._widthPixels = widthPixels;
                w._heightPixels = heightPixels;
                w.RaiseResized();
            });
        }

        /// <summary>A view's Surface is going away (the activity is stopping). The window keeps its
        /// handle and its size; it simply has nothing to present into until a new Surface arrives.</summary>
        public static void NotifySurfaceDestroyed(IntPtr handle)
        {
            AndroidWindow w;
            lock (s_byHandle) if (!s_byHandle.TryGetValue(handle, out w)) return;
            lock (w) w.ReleaseNativeWindow();
        }

        /// <summary>Called by the head when the display density changed.</summary>
        /// <summary>
        /// Text-input callbacks from the host's InputConnection. They land on the Android UI thread,
        /// which is the dispatcher thread, so they reach the editor directly.
        /// </summary>
        public static void NotifyComposingText(string text, int cursorPosition)
            => OnUi(() => AndroidTextInput.NotifyComposingText(text, cursorPosition));

        public static void NotifyCommitText(string text, int cursorPosition)
            => OnUi(() => AndroidTextInput.NotifyCommitText(text, cursorPosition));

        public static void NotifyFinishComposing()
            => OnUi(AndroidTextInput.NotifyFinishComposing);

        public static void NotifyDeleteSurrounding(int before, int after)
            => OnUi(() => AndroidTextInput.NotifyDeleteSurrounding(before, after));

        public static void NotifyScaleChanged()
            => OnUi(() =>
            {
                foreach (AndroidWindow w in s_zOrder.ToArray())
                    w.ScaleChanged?.Invoke(w.GetBackingScale());
            });

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
        {
            AndroidWindow w;
            lock (s_byHandle) if (!s_byHandle.TryGetValue(handle, out w)) return IntPtr.Zero;
            lock (w) return w._nativeWindow;
        }

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
        /// Whether this window is a popup (a borderless sibling view), which on this head means its
        /// scene belongs in its OWNER's surface rather than one of its own.
        /// </summary>
        /// <remarks>
        /// The compositor used to work that out from MilTarget.IsLayered, which is a Windows
        /// per-pixel-alpha flag and is false for every popup here -- so popups took the own-surface
        /// path that NativePlatform.PopupsShareOwnerSurface exists to avoid, and simply did not
        /// appear. Measured on the emulator: the popup opened, the process stayed up and the rest of
        /// the app kept rendering, and none of the popup's pixels ever reached the screen.
        /// </remarks>
        public static bool IsPopupWindow(IntPtr handle)
            => s_byHandle.TryGetValue(handle, out AndroidWindow w) && w.IsBorderless;

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
                seam?.GetProperty("PopupWindowQuery", PublicStatic)?.SetValue(null, (Func<IntPtr, bool>)IsPopupWindow);

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

        // ---- The app's UI thread ----------------------------------------------------

        private static System.Threading.Thread s_uiThread;
        private static System.Windows.Threading.Dispatcher s_uiDispatcher;

        /// <summary>Runs what Android reported on the app's UI thread: now if this is it, else posted to
        /// its dispatcher at input priority, as Windows posts a window's input to its thread's queue.</summary>
        private static void OnUi(Action work)
        {
            System.Windows.Threading.Dispatcher d = s_uiDispatcher;
            if (d == null || d.CheckAccess()) { work(); return; }
            d.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, work);
        }

        // What the main thread asks the app's UI thread and waits for (accessibility queries, a drop's
        // answer) goes two ways at once, whichever comes first: to the dispatcher, and here, where the
        // UI thread looks while it is itself waiting on the main thread (WaitServingMain) -- so neither
        // ever waits on the other while that one waits back.
        private static readonly System.Collections.Concurrent.ConcurrentQueue<Action> s_uiCalls = new();
        private static readonly System.Threading.AutoResetEvent s_uiCallPosted = new(false);

        /// <summary>For the main thread: <paramref name="work"/> run on the app's UI thread, its answer awaited.</summary>
        public static T InvokeOnUi<T>(Func<T> work)
        {
            System.Windows.Threading.Dispatcher d = s_uiDispatcher;
            if (d == null || d.CheckAccess()) return work();
            T result = default;
            Exception error = null;
            int taken = 0;
            using var done = new System.Threading.ManualResetEventSlim();
            Action run = () =>
            {
                if (System.Threading.Interlocked.Exchange(ref taken, 1) != 0) return;
                try { result = work(); }
                catch (Exception e) { error = e; }
                finally { done.Set(); }
            };
            s_uiCalls.Enqueue(run);
            s_uiCallPosted.Set();
            d.BeginInvoke(System.Windows.Threading.DispatcherPriority.Send, run);
            done.Wait();
            if (error != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(error);
            return result;
        }

        /// <summary>For the app's UI thread waiting on the main thread: waits for <paramref name="done"/>,
        /// running what the main thread asks of it meanwhile (<see cref="InvokeOnUi"/>).</summary>
        public static void WaitServingMain(System.Threading.WaitHandle done)
        {
            var handles = new System.Threading.WaitHandle[] { done, s_uiCallPosted };
            while (true)
            {
                while (s_uiCalls.TryDequeue(out Action call)) call();
                if (System.Threading.WaitHandle.WaitAny(handles) == 0) return;
            }
        }

        /// <summary>
        /// The idle wait of another framework's message loop on this thread -- WinForms' -- which also
        /// runs the WPF dispatcher's work as it comes (Dispatcher.ServeForeignLoop): what Android
        /// reports goes there (OnUi), and on this head no OS loop runs it otherwise.
        /// </summary>
        public static void RunLoop(int timeoutMilliseconds, System.Threading.WaitHandle posted)
            => System.Windows.Threading.Dispatcher.CurrentDispatcher.ServeForeignLoop(timeoutMilliseconds, posted);

        /// <summary>The first thread that makes a window is the app's UI thread: its dispatcher is where
        /// what Android reports goes (OnUi). Once it has ended, the next thread to make one is.</summary>
        private static void NoteUiThread()
        {
            System.Threading.Thread me = System.Threading.Thread.CurrentThread;
            System.Threading.Thread prior = s_uiThread;
            if (prior == me || (prior != null && prior.IsAlive)) return;
            s_uiThread = me;
            s_uiDispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        }

        // ---- The hardware keyboard -------------------------------------------------
        //
        // A keyboard's keys (a tablet's, a phone's over Bluetooth, the emulator's) come to the activity,
        // which hands them here before any view sees them (AndroidHost.DispatchKey): for the window that
        // has the keyboard -- the one last pressed or activated, as Windows gives the keys to the active
        // window and SetFocus to a child window that was clicked -- as Windows' virtual keys.

        /// <summary>A key of the hardware keyboard, down or up, for <see cref="Window"/>.</summary>
        public readonly record struct KeyMessage(IntPtr Window, bool IsDown, int VirtualKey, string Characters,
                                                 bool Shift, bool Ctrl, bool Alt, bool Meta, int TimestampMs);

        /// <summary>Raised on the app's UI thread for each hardware key; consumed by the keyboard input providers.</summary>
        public static event Action<KeyMessage> KeyInput;

        private static IntPtr s_keyWindow;   // the window the keys go to

        /// <summary>A child window that takes the keyboard when pressed, as SetFocus on one does on Windows.</summary>
        public bool TakesKeyboardOnPress { get; set; }

        // KeyEvent's meta state
        private const int MetaShiftOn = 0x1, MetaAltOn = 0x2, MetaCtrlOn = 0x1000, MetaMetaOn = 0x10000;

        /// <summary>
        /// A hardware key from the activity (main thread): <paramref name="keyCode"/> is Android's
        /// (KeyEvent.getKeyCode), <paramref name="unicodeChar"/> what it types with the modifiers held
        /// (getUnicodeChar), 0 for none. False for a key Windows has no virtual key for, which the system
        /// keeps (Back, Home, the volume).
        /// </summary>
        public static bool NotifyKey(bool down, int keyCode, int unicodeChar, int metaState)
        {
            int vk = VirtualKeyOfKeyCode(keyCode);
            if (vk == 0) return false;
            string chars = unicodeChar > 0 ? char.ConvertFromUtf32(unicodeChar) : null;
            var message = new KeyMessage(IntPtr.Zero, down, vk, chars,
                (metaState & MetaShiftOn) != 0, (metaState & MetaCtrlOn) != 0, (metaState & MetaAltOn) != 0, (metaState & MetaMetaOn) != 0,
                Environment.TickCount);
            OnUi(() => KeyInput?.Invoke(message with { Window = s_keyWindow != IntPtr.Zero ? s_keyWindow : s_mainHandle }));
            return true;
        }

        /// <summary>An Android key code (android.view.KeyEvent.KEYCODE_*) as the Windows virtual key it is.</summary>
        internal static int VirtualKeyOfKeyCode(int code)
        {
            if (code >= 29 && code <= 54) return 'A' + (code - 29);           // A..Z
            if (code >= 7 && code <= 16) return '0' + (code - 7);             // 0..9
            if (code >= 131 && code <= 142) return 0x70 + (code - 131);       // F1..F12
            if (code >= 144 && code <= 153) return 0x60 + (code - 144);       // keypad 0..9
            return code switch
            {
                66 or 160 => 0x0D, 67 => 0x08, 112 => 0x2E, 61 => 0x09, 62 => 0x20, 111 => 0x1B,   // Enter, Backspace, Delete, Tab, Space, Escape
                19 => 0x26, 20 => 0x28, 21 => 0x25, 22 => 0x27,                  // arrows: up, down, left, right
                122 => 0x24, 123 => 0x23, 92 => 0x21, 93 => 0x22, 124 => 0x2D,   // Home, End, PgUp, PgDn, Insert
                59 or 60 => 0x10, 113 or 114 => 0x11, 57 or 58 => 0x12,          // Shift, Control, Alt
                117 => 0x5B, 118 => 0x5C, 115 => 0x14, 116 => 0x91, 143 => 0x90, 120 => 0x2C, 121 => 0x13,   // Win, Caps, Scroll, Num Lock, Print Screen, Pause
                69 => 0xBD, 70 => 0xBB, 71 => 0xDB, 72 => 0xDD, 73 => 0xDC,      // - = [ ] \
                74 => 0xBA, 75 => 0xDE, 76 => 0xBF, 55 => 0xBC, 56 => 0xBE, 68 => 0xC0,   // ; ' / , . `
                154 => 0x6F, 155 => 0x6A, 156 => 0x6D, 157 => 0x6B, 158 => 0x6E,  // keypad / * - + .
                82 => 0x5D,                                                       // Menu
                _ => 0,
            };
        }

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

        /// <summary>
        /// Deliver a mouse event as if the platform had reported it. See CocoaWindow.InjectMouse
        /// for why the visual-tree inspector goes in here rather than raising routed events:
        /// GetPosition and CaptureMouse read the MouseDevice, not the event.
        /// </summary>
        /// <param name="kind">0 move, 1 down, 2 up, 3 wheel (drag-to-scroll).</param>
        internal static void InjectMouse(IntPtr window, int kind, int x, int y, int wheel)
        {
            MouseInput?.Invoke(new TouchMessage(window, kind, x, y, Environment.TickCount, wheel));
        }

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
            => OnUi(() => TouchContact(handle, kind, contactId, xPixels, yPixels, pressure, tiltX, tiltY, isEraser));

        private static void TouchContact(IntPtr handle, int kind, int contactId,
                                         int xPixels, int yPixels, double pressure,
                                         double tiltX, double tiltY, bool isEraser)
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
            => OnUi(() => Touch(handle, kind, xPixels, yPixels));

        private static void Touch(IntPtr handle, int kind, int xPixels, int yPixels)
        {
            // a press gives a window the keys, and a child window that takes them (an ElementHost's WPF
            // tree) -- never a popup (a menu, a tooltip), as on Windows
            if (kind == 1 && FromHandle(handle) is { } pressed && (!pressed.IsBorderless || pressed._ownSurface || pressed.TakesKeyboardOnPress))
                s_keyWindow = handle;
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
            => OnUi(() => Scroll(handle, deltaLogical, xPixels, yPixels));

        private static void Scroll(IntPtr handle, double deltaLogical, int xPixels, int yPixels)
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
