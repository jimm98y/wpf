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

        /// <summary>A child window's parent (WS_CHILD): its view lies just above the parent's and goes where
        /// the parent goes in the z-order, under the windows above the parent, as Windows stacks a child
        /// window with its parent.</summary>
        void SetViewParent(IntPtr child, IntPtr parent) { }

        /// <summary>The view's surface keeps what is drawn with alpha see-through (a translucent format):
        /// a window drawing its own frame, whose resize margin outside the visible edge is.</summary>
        void SetViewTranslucent(IntPtr handle) { }

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

        /// <summary>The screen is a desktop (a headset's: AndroidXrHost), not a phone's or a tablet's: the app's own
        /// window is a window on it as any other is -- moved by its caption, sized from its edges, maximized and
        /// restored -- rather than the screen itself.</summary>
        bool DesktopWindows => false;

        /// <summary>The host draws Windows' frame -- the caption with the title and the close button, the edge and
        /// its shadow -- round a window that has one (AndroidWindow.HasFrame, GetFrameRect), as Windows' DWM draws
        /// it round the client, and draws it again when told (FrameChanged). The headset's does.</summary>
        bool DrawsFrames => false;

        /// <summary>A window's frame is to be drawn again: its title, its activation or its close button changed.</summary>
        void FrameChanged(IntPtr handle) { }

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
        // read when asked: this class is loaded by the activity before the head applies the app's environment
        private static bool s_trace => Environment.GetEnvironmentVariable("WPF_TRACE_ANDROID") == "1";

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

        /// <summary>The app's own window, pinned to the activity it fills -- not on a desktop (IAndroidHost.DesktopWindows),
        /// where it is a window as the others are.</summary>
        private bool Pinned => IsMain && Host?.DesktopWindows != true;

        /// <summary>The window is the screen (the app's own, pinned): its size is the activity's.</summary>
        public bool FillsScreen => Pinned;

        // ---- the frame: Windows' non-client area, where the host draws it ---------------------------------
        //
        // A WPF window with a caption (WS_CAPTION: a dialog, a tool window, a MessageBox's) has Windows' frame round
        // its client, which on Windows the DWM draws and DefWindowProc answers for. Where the host draws frames
        // (IAndroidHost.DrawsFrames) this layer answers for it: the rectangle WPF moves and sizes is the outer one
        // (Window.Left, Width: GetWindowRect), the view is the client inside it (GetClientRect, WM_SIZE), and a press
        // on the frame moves the window (the caption), sizes it (the edges) or closes it (the close button), as
        // DefWindowProc's loops do. Without it a MessageBox came up as a bare client, neither movable nor closable.
        // The view's fields (_xPixels.. _heightPixels) stay the client's; the frame is the insets round it.

        private int _frameLeft, _frameTop, _frameRight, _frameBottom;   // device pixels; all 0 without a frame
        private bool _frameSizable, _frameCloseBox, _closeHover, _closeHeld;
        private string _title = "";
        private long _captionClickTime;
        private int _captionClickX, _captionClickY;
        private static AndroidWindow s_framePress;   // the window whose frame the button went down on, until it comes up

        private const int HTNOWHERE = 0, HTCLIENT = 1, HTCAPTION = 2, HTLEFT = 10, HTRIGHT = 11, HTTOP = 12, HTTOPLEFT = 13,
            HTTOPRIGHT = 14, HTBOTTOM = 15, HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17, HTBORDER = 18, HTCLOSE = 20;

        /// <summary>The window's style (WS_*), set before <see cref="Create(string, int, int, int, int, bool)"/>: a
        /// top-level window with a caption gets a frame, where the host draws them.</summary>
        public int FrameStyle { get; set; }

        /// <summary>The window has Windows' frame round its client, which the host draws.</summary>
        public bool HasFrame => _frameTop > 0;

        /// <summary>The frame has sizing edges (WS_THICKFRAME).</summary>
        public bool FrameSizable => _frameSizable;

        /// <summary>The caption has a close button (WS_SYSMENU).</summary>
        public bool FrameHasCloseBox => _frameCloseBox;

        /// <summary>The window's title (SetWindowText), which its caption shows.</summary>
        public string Title => _title;

        /// <summary>The window is the active one, whose caption is drawn so.</summary>
        public bool IsActiveWindow => ReferenceEquals(s_active, this);

        /// <summary>The close button's look: 0, the pointer over it (FrameCloseHot), or held down on it (FrameClosePressed).</summary>
        public int FrameState => _closeHover ? (_closeHeld ? FrameClosePressed : FrameCloseHot) : 0;
        public const int FrameCloseHot = 1, FrameClosePressed = 2;

        /// <summary>The frame round the client, device pixels: the sides and the foot (the resize margin outside the
        /// visible edge, and the edge), and the top (the caption).</summary>
        public void GetFrameInsets(out int left, out int top, out int right, out int bottom)
        {
            left = _frameLeft; top = _frameTop; right = _frameRight; bottom = _frameBottom;
        }

        /// <summary>The window's outer rectangle, top-left device pixels: the client and its frame.</summary>
        public void GetFrameRect(out int x, out int y, out int width, out int height)
        {
            x = _xPixels - _frameLeft;
            y = _yPixels - _frameTop;
            width = _widthPixels + _frameLeft + _frameRight;
            height = _heightPixels + _frameTop + _frameBottom;
        }

        /// <summary>The close button was clicked: WM_CLOSE for the window.</summary>
        public event Action<IntPtr> Closed;

        /// <summary>SetWindowText: the title its caption shows.</summary>
        public static void SetTitle(IntPtr handle, string title)
        {
            if (FromHandle(handle) is not { } w || w._title == (title ?? "")) return;
            w._title = title ?? "";
            w.OnFrameChanged();
        }

        private void OnFrameChanged()
        {
            if (HasFrame && _handle != IntPtr.Zero) Host?.FrameChanged(_handle);
        }

        // Create's rectangle, the outer one, made the client's by the frame: AdjustWindowRectExForDpi's on
        // Windows 11 -- a caption, with or without a sizing frame, 8 pixels on three sides and 31 on top at 96 dpi
        // (XplatUIWebGpu.Windows11Frame: a dialog frame of 3 that does not scale, a border and the padded border
        // of 4 that do, and the caption of 22 and a pixel).
        private bool _translucent;
        private int _desktopX, _desktopY, _desktopWidth, _desktopHeight;   // the app's own window on a desktop, while pinned
        private bool _desktopKnown;

        // ---- moving to another host: a headset's panel app going immersive, and back ----------------------
        //
        // One process, two activities (a 2D panel's and an immersive one's), each with a host of its own: the
        // windows go across as they are -- their handles, their stacking, their state -- each given a view in
        // the new host (whose surface reaches the renderer as an activity's restart's does, NotifySurfaceChanged).
        // The app's own window fills a panel; on a headset's desktop it is a window as the others are, where it
        // was there last (or at the panel's size in the corner); a window with a caption gains or loses its frame
        // there, keeping its outer rectangle, as a window does when a theme changes its frame.

        /// <summary>Every window into <paramref name="host"/>'s views, out of the host it was in (on the app's UI thread).</summary>
        public static void Rehost(IAndroidHost host)
        {
            OnUi(() =>
            {
                IAndroidHost old = Host;
                if (ReferenceEquals(old, host) || host == null) return;
                List<AndroidWindow> windows;
                lock (s_zOrder) windows = new List<AndroidWindow>(s_zOrder);
                if (s_trace) Console.WriteLine($"[android] rehost {windows.Count} windows: {old?.GetType().Name} -> {host.GetType().Name}");
                foreach (AndroidWindow w in windows)
                {
                    if (w._handle == IntPtr.Zero) continue;
                    old?.DestroyView(w._handle);
                    lock (w) w.ReleaseNativeWindow();
                }
                Host = host;
                s_drag = null;
                s_framePress = null;
                foreach (AndroidWindow w in windows)
                    if (w._handle != IntPtr.Zero) w.MoveTo(host);
                // the children once their parents have views (a host ignores a parent it has none for: an ElementHost's
                // tree made before its form's window, left a window of its own, lay over a dialog and took its touches),
                // then the stacking as it was, bottom up
                foreach (AndroidWindow w in windows)
                {
                    if (w._handle == IntPtr.Zero) continue;
                    IntPtr parent = w._parentWindow;
                    // a child of another toolkit's window (an ElementHost's tree in a form) names none of ours: the
                    // window it lies in, as the pointer finds it (IsEmbeddedIn)
                    // -- or, laid out for the host it came from and not yet for this one (past its window's edge by
                    // then), the largest window that holds its corner
                    if (parent == IntPtr.Zero && w.IsBorderless && !w._ownSurface)
                    {
                        AndroidWindow holder = null;
                        foreach (AndroidWindow p in windows)
                        {
                            if (p == w || p._handle == IntPtr.Zero || p.IsBorderless) continue;
                            if (w.IsEmbeddedIn(p)) { holder = p; break; }
                            bool corner = w._xPixels >= p._xPixels && w._yPixels >= p._yPixels
                                && w._xPixels < p._xPixels + p._widthPixels && w._yPixels < p._yPixels + p._heightPixels;
                            if (corner && (holder == null || (long)p._widthPixels * p._heightPixels > (long)holder._widthPixels * holder._heightPixels)) holder = p;
                        }
                        if (holder != null) parent = holder._handle;
                    }
                    if (parent == IntPtr.Zero) continue;
                    if (s_trace) Console.WriteLine($"[android] 0x{w._handle:x} stacked with 0x{parent:x}");
                    host.SetViewParent(w._handle, parent);
                }
                foreach (AndroidWindow w in windows)
                    if (w._handle != IntPtr.Zero && w._parentWindow == IntPtr.Zero && w.CanBeActive && w._visible) host.BringToFront(w._handle);
            });
        }

        private void MoveTo(IAndroidHost host)
        {
            bool desktop = host.DesktopWindows;
            StereoPair = false;
            // the outer rectangle, which stays (the frame comes and goes round it)
            int ox = _xPixels - _frameLeft, oy = _yPixels - _frameTop;
            int ow = _widthPixels + _frameLeft + _frameRight, oh = _heightPixels + _frameTop + _frameBottom;
            bool fills = IsMain && !desktop;
            if (IsMain)
            {
                if (fills)
                {
                    if (!_wasFilling) { _desktopX = ox; _desktopY = oy; _desktopWidth = ow; _desktopHeight = oh; _desktopKnown = true; }
                    host.GetScreenPixels(out ow, out oh);
                    ox = oy = 0;
                }
                else if (_wasFilling && _desktopKnown)
                {
                    ox = _desktopX; oy = _desktopY; ow = _desktopWidth; oh = _desktopHeight;
                }
                _wasFilling = fills;
                _maximized = false;
            }
            _frameLeft = _frameTop = _frameRight = _frameBottom = 0;
            _xPixels = ox; _yPixels = oy; _widthPixels = Math.Max(1, ow); _heightPixels = Math.Max(1, oh);
            if (!IsBorderless && !fills) ApplyFrame();
            if (s_trace) Console.WriteLine($"[android] 0x{_handle:x} into {host.GetType().Name} at ({_xPixels},{_yPixels}) {_widthPixels}x{_heightPixels}px{(HasFrame ? " framed" : "")}{(fills ? " filling" : "")}");
            host.CreateView(_handle, _xPixels, _yPixels, _widthPixels, _heightPixels, IsBorderless, fills, _ownSurface);
            if (_translucent) host.SetViewTranslucent(_handle);
            if (!_visible || _minimized) host.SetViewVisible(_handle, false);
            RaiseResized();
            OnFrameChanged();
        }

        private bool _wasFilling;

        private void ApplyFrame()
        {
            const int WS_CAPTION = 0x00C00000, WS_THICKFRAME = 0x00040000, WS_SYSMENU = 0x00080000, WS_CHILD = 0x40000000;
            if ((FrameStyle & WS_CAPTION) != WS_CAPTION || (FrameStyle & WS_CHILD) != 0 || Host?.DrawsFrames != true) return;
            double scale = GetBackingScale();
            int M(int v) => (int)Math.Floor(v * scale + 0.5);
            int edge = 3 + M(1) + M(4);
            _frameLeft = _frameRight = _frameBottom = edge;
            _frameTop = edge + M(22) + 1;
            _frameSizable = (FrameStyle & WS_THICKFRAME) != 0;
            _frameCloseBox = (FrameStyle & WS_SYSMENU) != 0;
            _xPixels += _frameLeft;
            _yPixels += _frameTop;
            _widthPixels = Math.Max(1, _widthPixels - _frameLeft - _frameRight);
            _heightPixels = Math.Max(1, _heightPixels - _frameTop - _frameBottom);
        }

        /// <summary>WM_NCHITTEST for a point relative to the client's origin (device pixels), as Windows 11 answers it.</summary>
        private int FrameHit(int x, int y)
        {
            if (x >= 0 && y >= 0 && x < _widthPixels && y < _heightPixels) return HTCLIENT;
            int ox = x + _frameLeft, oy = y + _frameTop;
            int ow = _widthPixels + _frameLeft + _frameRight, oh = _heightPixels + _frameTop + _frameBottom;
            if (ox < 0 || oy < 0 || ox >= ow || oy >= oh) return HTNOWHERE;
            double scale = GetBackingScale();
            int margin = Math.Max(0, _frameLeft - 1);                       // the resize margin outside the visible edge
            int closeWidth = (int)Math.Round(46 * scale);
            if (_frameCloseBox && oy >= 1 && oy < _frameTop && ox < ow - margin - 1 && ox >= ow - margin - 1 - closeWidth) return HTCLOSE;
            if (_frameSizable)
            {
                bool left = ox < _frameLeft, right = ox >= ow - _frameRight;
                bool top = oy < (int)Math.Round(4 * scale), bottom = oy >= oh - _frameBottom;
                if (top && left) return HTTOPLEFT;
                if (top && right) return HTTOPRIGHT;
                if (bottom && left) return HTBOTTOMLEFT;
                if (bottom && right) return HTBOTTOMRIGHT;
                if (left) return HTLEFT;
                if (right) return HTRIGHT;
                if (top) return HTTOP;
                if (bottom) return HTBOTTOM;
            }
            if (oy < _frameTop && ox >= margin && ox < ow - margin) return HTCAPTION;
            return HTBORDER;
        }

        private void SetCloseLook(bool hover, bool held)
        {
            if (_closeHover == hover && _closeHeld == held) return;
            _closeHover = hover;
            _closeHeld = held;
            OnFrameChanged();
        }

        /// <summary>A touch on the window (relative to its client) that is its frame's, as DefWindowProc takes a press on
        /// the non-client area: true when it was. A move over the frame goes on to the client as well -- the pointer
        /// leaving its elements -- unless the button went down on the frame.</summary>
        private bool OnFrame(int kind, int x, int y)
        {
            int hit = FrameHit(x, y);
            bool pressedHere = ReferenceEquals(s_framePress, this);
            switch (kind)
            {
                case 0:
                    SetCloseLook(hit == HTCLOSE && _frameCloseBox && (pressedHere ? _closeHeld : !s_pressed), _closeHeld);
                    return pressedHere;
                case 1:
                    if (hit == HTCLIENT) return false;
                    if (hit == HTNOWHERE) return true;
                    s_framePress = this;
                    Activate();
                    if (hit == HTCLOSE) { SetCloseLook(true, true); return true; }
                    if (hit == HTCAPTION)
                    {
                        // a second click on the caption maximizes the window, or restores it (SM_CXDOUBLECLK's square)
                        long now = Environment.TickCount64;
                        int slop = (int)Math.Round(2 * GetBackingScale());
                        bool second = now - _captionClickTime <= 500 && Math.Abs(x - _captionClickX) <= slop && Math.Abs(y - _captionClickY) <= slop;
                        _captionClickTime = second ? 0 : now;
                        _captionClickX = x;
                        _captionClickY = y;
                        if (second && _frameSizable) SetWindowState(IsMaximizedNow ? 9 : 3);
                        else BeginMoveDrag();
                        return true;
                    }
                    if (hit >= HTLEFT && hit <= HTBOTTOMRIGHT && _frameSizable && !IsMaximizedNow)
                        BeginResizeDrag(hit, 0, 0, 0, 0);
                    return true;
                case 2:
                    if (!pressedHere) return false;
                    s_framePress = null;
                    bool close = _closeHeld && hit == HTCLOSE;
                    SetCloseLook(hit == HTCLOSE && _frameCloseBox, false);
                    if (close) Closed?.Invoke(_handle);
                    return true;
                default:
                    return hit != HTCLIENT;
            }
        }

        /// <summary>A top-level window over the app's own: a dialog, a tool window.</summary>
        public bool IsSecondary => !IsBorderless && !IsMain;

        /// <summary>Whether the app has its own window yet (the first framed one, whichever stack made it).</summary>
        public static bool HasMainWindow { get { lock (s_byHandle) return s_mainHandle != IntPtr.Zero && s_byHandle.ContainsKey(s_mainHandle); } }

        public void SetVisible(bool visible, bool activate)
        {
            SetVisible(visible);
            if (visible && activate) Activate();
        }

        /// <summary>The window is a child of <paramref name="parent"/> (an AndroidWindow's handle; any other is
        /// ignored): it is stacked with it (IAndroidHost.SetViewParent).</summary>
        private IntPtr _parentWindow;   // SetParentWindow's: a child is where a pointer reaches it (PointerTarget)

        public void SetParentWindow(IntPtr parent)
        {
            if (_handle == IntPtr.Zero || parent == IntPtr.Zero || FromHandle(parent) == null)
            {
                // moved out of the parent it had (an ElementHost out of its parking window into a form whose
                // window is not one yet): no longer that one's
                if (s_trace) Console.WriteLine($"[android] 0x{_handle:x} child of 0x{parent:x}, not a window (yet)");
                _parentWindow = IntPtr.Zero;
                return;
            }
            _parentWindow = parent;
            if (s_trace) Console.WriteLine($"[android] 0x{_handle:x} child of 0x{parent:x}");
            Host?.SetViewParent(_handle, parent);
        }

        /// <summary>The window draws its own frame -- Windows' title bar, edges and the shadow in the
        /// see-through margin round them (the WinForms host's WindowChrome) -- so its surface is translucent
        /// there (UIKitWindow.SetOwnFrame).</summary>
        public void SetOwnFrame()
        {
            _translucent = true;
            if (_handle != IntPtr.Zero) Host?.SetViewTranslucent(_handle);
        }

        /// <summary>To the front, as SetForegroundWindow brings a window there.</summary>
        public void Activate()
        {
            if (_handle == IntPtr.Zero || (IsBorderless && !_ownSurface)) return;
            lock (s_zOrder) { s_zOrder.Remove(this); s_zOrder.Add(this); }
            s_keyWindow = _handle;
            Host?.BringToFront(_handle);
            SetActive(this);
        }

        public bool IsBorderless { get; private set; }

        public static IntPtr MouseCaptureHandle { get; set; }

        /// <summary>Raised when the view's content size changed (device pixels).</summary>
        public event Action<int, int> Resized;

        /// <summary>The window became the active one (true) or stopped being it (false): WM_ACTIVATE, which
        /// Window.IsActive and GetActiveWindow follow -- a dialog shown without an owner takes the active
        /// window for one, and centred on nothing when there was none.</summary>
        public event Action<bool> ActiveChanged;

        // The active window: the top-level one (framed, or borderless with a surface of its own) that was
        // last activated or pressed, as Windows' foreground window is. A popup or a child window taking
        // the keys leaves it as it is.
        private static AndroidWindow s_active;

        private bool CanBeActive => _handle != IntPtr.Zero && (!IsBorderless || _ownSurface);

        /// <summary>Makes <paramref name="window"/> the active window: the one that was is told first that
        /// it no longer is, as WM_ACTIVATE (WA_INACTIVE) reaches it before the new one's WA_ACTIVE.</summary>
        private static void SetActive(AndroidWindow window)
        {
            if (window != null && !window.CanBeActive) return;
            if (ReferenceEquals(s_active, window)) return;
            AndroidWindow old = s_active;
            s_active = window;
            if (s_trace) Console.WriteLine($"[android] active 0x{old?._handle ?? IntPtr.Zero:x} -> 0x{window?._handle ?? IntPtr.Zero:x}");
            old?.ActiveChanged?.Invoke(false);
            window?.ActiveChanged?.Invoke(true);
            old?.OnFrameChanged();
            window?.OnFrameChanged();
        }

        /// <summary>The active window goes away (hidden, destroyed): the activation passes to the app's
        /// own window, as Windows activates the next one, or to none.</summary>
        private void PassActivation()
        {
            if (!ReferenceEquals(s_active, this)) return;
            AndroidWindow next = s_mainHandle != _handle ? FromHandle(s_mainHandle) : null;
            if (next != null && !next._visible) next = null;
            SetActive(next);
        }

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
            bool desktop = Host?.DesktopWindows == true;
            if (main && !desktop)
            {
                Host?.GetScreenPixels(out _widthPixels, out _heightPixels);
                _xPixels = _yPixels = 0;
            }
            else if (main)
            {
                // on a desktop: where it asks to be, as much of it as the screen holds
                int sw = _widthPixels, sh = _heightPixels;
                Host?.GetScreenPixels(out sw, out sh);
                _widthPixels = Math.Min(_widthPixels, sw);
                _heightPixels = Math.Min(_heightPixels, sh);
                _xPixels = Math.Clamp(_xPixels, 0, Math.Max(0, sw - _widthPixels));
                _yPixels = Math.Clamp(_yPixels, 0, Math.Max(0, sh - _heightPixels));
            }

            _title = title ?? "";
            _wasFilling = main && !desktop;
            if (!borderless && !(main && !desktop)) ApplyFrame();

            if (s_trace) Console.WriteLine($"[android] 0x{_handle:x} created '{title}' {(main ? "main" : borderless ? (_ownSurface ? "borderless, own surface" : "borderless") : "floating")} at ({_xPixels},{_yPixels}) {_widthPixels}x{_heightPixels}px{(HasFrame ? $", frame {_frameLeft},{_frameTop},{_frameRight},{_frameBottom}" : "")}");
            lock (s_byHandle) s_byHandle[_handle] = this;
            lock (s_zOrder) s_zOrder.Add(this);
            if (main) s_mainHandle = _handle;

            if (Host is null || !Host.CreateView(_handle, _xPixels, _yPixels, _widthPixels, _heightPixels, borderless, main && !desktop, _ownSurface))
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
            if (!visible) _minimized = false;
            _visible = visible;
            Host?.SetViewVisible(_handle, visible);
            if (!visible) PassActivation();
        }

        private bool _visible = true;

        // ---- minimized, maximized, restored: ShowWindow's states -----------------------------------
        //
        // The app's own window fills the activity and has no other state to be in: it stays a normal
        // window there, which is what it looks like -- maximized, a window drawing its own frame pads its
        // content by the border Windows would hang off the screen (Workbench's ribbon window by 5px). A floating one is
        // maximized to the screen and restored to the frame it had, as on Windows -- WPF learns it from
        // the WM_SIZE the change raises (GetWindowState gives its wParam), which is what raises
        // Window.StateChanged; a docking library's floating host sizes its pane to fill on that event.
        // Minimized, with no taskbar to go to, it is out of sight until it is restored.

        private bool _maximized, _minimized;
        private int _restoreX, _restoreY, _restoreWidth, _restoreHeight;
        private int _maxX, _maxY, _maxWidth, _maxHeight;

        /// <summary>The window's maximized rectangle, asked of the window as Windows asks it (WM_GETMINMAXINFO,
        /// HwndWrapper's): given the monitor's rectangle, then the head's maximized position and size, it
        /// answers the position and size (device pixels), or null to keep them.</summary>
        public Func<int[], int[]> QueryMaximizedRect { get; set; }

        public void SetWindowState(int state)
        {
            if (_handle == IntPtr.Zero) return;
            if (s_trace) Console.WriteLine($"[android] 0x{_handle:x} state -> SW {state}");
            switch (state)
            {
                case 2: case 6: case 7: case 11:   // SW_SHOWMINIMIZED, SW_MINIMIZE, SW_SHOWMINNOACTIVE, SW_FORCEMINIMIZE
                    if (Pinned || _minimized) return;
                    _minimized = true;
                    Host?.SetViewVisible(_handle, false);
                    RaiseResized();
                    break;

                case 3:   // SW_SHOWMAXIMIZED / SW_MAXIMIZE
                    Unminimize();
                    if (Pinned) return;
                    if (!IsMaximizedNow)
                    {
                        _restoreX = _xPixels; _restoreY = _yPixels; _restoreWidth = _widthPixels; _restoreHeight = _heightPixels;
                        // the screen, which is all the work area there is -- unless the window says otherwise
                        int width = _widthPixels, height = _heightPixels;
                        Host?.GetScreenPixels(out width, out height);
                        int screenWidth = width, screenHeight = height;
                        int x = 0, y = 0;
                        // a framed window's outer rectangle hangs its border off the screen, as Windows maximizes
                        // it: the caption on the screen's top edge, the client to the screen's sides and foot
                        if (HasFrame) { x = y = -_frameLeft; width += _frameLeft + _frameRight; height += _frameLeft + _frameBottom; }
                        if (QueryMaximizedRect?.Invoke(new[] { 0, 0, screenWidth, screenHeight, x, y, width, height }) is int[] r && r.Length >= 4 && r[2] > 0 && r[3] > 0)
                        {
                            x = r[0]; y = r[1]; width = r[2]; height = r[3];
                        }
                        if (HasFrame)
                        {
                            x += _frameLeft; y += _frameTop;
                            width = Math.Max(1, width - _frameLeft - _frameRight);
                            height = Math.Max(1, height - _frameTop - _frameBottom);
                        }
                        _maxX = _xPixels = x;
                        _maxY = _yPixels = y;
                        _maxWidth = _widthPixels = width;
                        _maxHeight = _heightPixels = height;
                        _maximized = true;
                        Host?.SetViewFrame(_handle, _xPixels, _yPixels, _widthPixels, _heightPixels);
                    }
                    RaiseResized();
                    break;

                case 1: case 9:   // SW_SHOWNORMAL, SW_RESTORE
                    bool wasMinimized = Unminimize();
                    if (IsMaximizedNow && !Pinned)
                    {
                        _maximized = false;
                        _xPixels = _restoreX; _yPixels = _restoreY; _widthPixels = _restoreWidth; _heightPixels = _restoreHeight;
                        Host?.SetViewFrame(_handle, _xPixels, _yPixels, _widthPixels, _heightPixels);
                        RaiseResized();
                    }
                    else
                    {
                        _maximized = false;
                        if (wasMinimized) RaiseResized();
                    }
                    break;
            }
        }

        private bool Unminimize()
        {
            if (!_minimized) return false;
            _minimized = false;
            Host?.SetViewVisible(_handle, true);
            return true;
        }

        /// <summary>Maximized while it keeps its maximized frame: moved or sized out of it (a drag on its
        /// caption), it is a normal window again, as on Windows.</summary>
        private bool IsMaximizedNow
        {
            get
            {
                if (!_maximized || Pinned) return false;
                if (_xPixels == _maxX && _yPixels == _maxY && _widthPixels == _maxWidth && _heightPixels == _maxHeight) return true;
                _maximized = false;
                return false;
            }
        }

        public int GetWindowState() => _minimized ? 2 : IsMaximizedNow ? 3 : 1;

        // ---- the move and size loops: DefWindowProc's, for a floating window ----------------------
        //
        // The app's own window fills the activity; a dialog or a tool window floats over it with no
        // frame of the platform's, so this layer moves it (DragMove, a caption) and sizes it from its
        // edges (WindowChrome), as UIKitWindow does. A loop runs from the press that began it until
        // its release, following the finger in screen pixels; the touches meanwhile are its own.

        private sealed class Drag
        {
            public AndroidWindow Window;
            public int HitTest;                 // 0 for a move; HTLEFT..HTBOTTOMRIGHT for a size
            public int X0, Y0;                  // the press, screen pixels
            public int Left0, Top0, Width0, Height0;
            public int MinW, MinH, MaxW, MaxH;
        }

        private static Drag s_drag;
        private static int s_pressX, s_pressY;  // the last press, screen pixels
        private static int s_cursorX, s_cursorY;
        private static bool s_cursorKnown;

        /// <summary>GetCursorPos: where the finger (or the mouse) last was, in screen device pixels.</summary>
        public static bool TryGetCursorPixels(out int x, out int y)
        {
            x = s_cursorX;
            y = s_cursorY;
            return s_cursorKnown;
        }
        private static bool s_pressed;

        public void BeginMoveDrag()
        {
            if (s_trace) Console.WriteLine($"[android] 0x{_handle:x} move loop{(_handle == IntPtr.Zero || Pinned || !s_pressed ? " refused" : "")}");
            if (_handle == IntPtr.Zero || Pinned || !s_pressed) return;
            s_drag = new Drag { Window = this, X0 = s_pressX, Y0 = s_pressY, Left0 = _xPixels, Top0 = _yPixels, Width0 = _widthPixels, Height0 = _heightPixels };
        }

        /// <summary>A floating window has no frame of its own to be resized by: this layer does it.</summary>
        public bool ResizesFromEdges => !IsBorderless && !Pinned;

        public bool BeginResizeDrag(int hitTest, int minWidth, int minHeight, int maxWidth, int maxHeight)
        {
            if (s_trace) Console.WriteLine($"[android] 0x{_handle:x} size loop ht={hitTest}{(!ResizesFromEdges || !s_pressed || hitTest < 10 || hitTest > 17 ? " refused" : "")}");
            if (!ResizesFromEdges || !s_pressed || hitTest < 10 || hitTest > 17) return false;
            s_drag = new Drag
            {
                Window = this, HitTest = hitTest, X0 = s_pressX, Y0 = s_pressY,
                Left0 = _xPixels, Top0 = _yPixels, Width0 = _widthPixels, Height0 = _heightPixels,
                MinW = Math.Max(minWidth - _frameLeft - _frameRight, 1), MinH = Math.Max(minHeight - _frameTop - _frameBottom, 1),
                MaxW = maxWidth > 0 ? maxWidth - _frameLeft - _frameRight : int.MaxValue, MaxH = maxHeight > 0 ? maxHeight - _frameTop - _frameBottom : int.MaxValue,
            };
            return true;
        }

        private static void DragTo(int x, int y)
        {
            Drag d = s_drag;
            AndroidWindow w = d.Window;
            if (w._handle == IntPtr.Zero) { s_drag = null; return; }
            int dx = x - d.X0, dy = y - d.Y0;
            if (d.HitTest == 0)
            {
                // a maximized window taken by its caption is restored under the pointer once it moves (SM_CXDRAG), as
                // on Windows: the same fraction of its width under the pointer, the caption where it is held
                if (w.IsMaximizedNow && (Math.Abs(dx) > 4 || Math.Abs(dy) > 4))
                {
                    double across = w._widthPixels > 0 ? (double)(d.X0 - w._xPixels) / w._widthPixels : 0.5;
                    int down = d.Y0 - w._yPixels;
                    w.SetWindowState(9);
                    d.Left0 = d.X0 - (int)Math.Round(across * w._widthPixels);
                    d.Top0 = d.Y0 - Math.Min(down, Math.Max(0, w._heightPixels - 1));
                }
                // the top edge stays on the screen, so that the caption can be taken hold of again
                w.SetClientOrigin(d.Left0 + dx, Math.Max(w._frameTop, d.Top0 + dy));
                return;
            }
            int ht = d.HitTest;
            bool left = ht is 10 or 13 or 16, right = ht is 11 or 14 or 17;
            bool top = ht is 12 or 13 or 14, bottom = ht is 15 or 16 or 17;
            int width = d.Width0, height = d.Height0;
            if (right) width += dx; else if (left) width -= dx;
            if (bottom) height += dy; else if (top) height -= dy;
            width = Math.Min(Math.Max(width, d.MinW), d.MaxW);
            height = Math.Min(Math.Max(height, d.MinH), d.MaxH);
            int fx = left ? d.Left0 + d.Width0 - width : d.Left0;
            int fy = top ? d.Top0 + d.Height0 - height : d.Top0;
            if (fy < w._frameTop) { if (top) height += fy - w._frameTop; fy = w._frameTop; }
            w._xPixels = fx;
            w._yPixels = fy;
            w.SetClientSizePixels(width, height);
        }

        public void Destroy()
        {
            if (_handle == IntPtr.Zero)
                return;

            PassActivation();
            if (ReferenceEquals(s_active, this)) s_active = null;
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

        /// <summary>Resize from DEVICE PIXELS, as WPF's SetWindowPos supplies: the outer size (the client's with its frame).</summary>
        public void SetContentSizePixels(int cx, int cy)
        {
            if (HasFrame)
            {
                cx = Math.Max(1, cx - _frameLeft - _frameRight);
                cy = Math.Max(1, cy - _frameTop - _frameBottom);
            }
            SetClientSizePixels(cx, cy);
        }

        private void SetClientSizePixels(int cx, int cy)
        {
            if (_handle == IntPtr.Zero) return;

            // Fullscreen top-level: keep OUR size, but still report it. RaiseResized drives the
            // synthetic WM_SIZE -> OnResize -> UpdateWindowSettings that tells the compositor its
            // target size; swallowing it leaves the target at 0x0 and NOTHING is composited (the
            // window is correctly sized and interactive, and the screen stays blank). Exactly the
            // same trap as UIKitWindow.SetContentSize.
            if (IsFullScreenTopLevel) { RaiseResized(); return; }

            if (s_trace) Console.WriteLine($"[android] 0x{_handle:x} size -> {cx}x{cy}px");
            _widthPixels = cx;
            _heightPixels = cy;
            Host?.SetViewFrame(_handle, _xPixels, _yPixels, _widthPixels, _heightPixels);
            RaiseResized();
        }

        /// <summary>Move to the outer rectangle's top-left (SetWindowPos), device pixels.</summary>
        public void SetFrameOrigin(int xPixels, int yPixels)
        {
            const int CW_USEDEFAULT = unchecked((int)0x80000000);
            if (HasFrame && xPixels != CW_USEDEFAULT && yPixels != CW_USEDEFAULT) { xPixels += _frameLeft; yPixels += _frameTop; }
            SetClientOrigin(xPixels, yPixels);
        }

        private void SetClientOrigin(int xPixels, int yPixels)
        {
            if (s_trace) Console.WriteLine($"[android] 0x{_handle:x} origin -> ({xPixels},{yPixels})px{(IsFullScreenTopLevel ? " (ignored: the app's own window)" : "")}");
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

        /// <summary>The outer window size: the client's, with the frame round it where there is one.</summary>
        public void GetWindowPixelSize(out int width, out int height)
        {
            width = _widthPixels + _frameLeft + _frameRight;
            height = _heightPixels + _frameTop + _frameBottom;
        }

        /// <summary>The outer window's top-left (GetWindowRect), device pixels.</summary>
        public void GetWindowScreenOriginPixels(out int x, out int y)
        {
            x = _xPixels - _frameLeft;
            y = _yPixels - _frameTop;
        }

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
        private bool IsFullScreenTopLevel => Pinned && Host != null;

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

                if (x >= w._xPixels - w._frameLeft && x < w._xPixels + w._widthPixels + w._frameRight &&
                    y >= w._yPixels - w._frameTop && y < w._yPixels + w._heightPixels + w._frameBottom)
                    return w._handle;
            }
            return IntPtr.Zero;
        }

        /// <summary>
        /// The window a pointer at the point (top-left device pixels) is over: the topmost shown one, a child
        /// (an ElementHost's WPF tree) over its parent, as a finger reaches a child through its own view;
        /// with where it is, for the pointer's coordinates in it.
        /// </summary>
        public static IntPtr PointerTarget(int x, int y, out int left, out int top)
        {
            AndroidWindow found = null;
            lock (s_zOrder)
            {
                // the topmost window that is not a child, then down through its children under the point
                for (int i = s_zOrder.Count - 1; i >= 0 && found == null; i--)
                    if (s_zOrder[i]._parentWindow == IntPtr.Zero && s_zOrder[i].Contains(x, y)) found = s_zOrder[i];
                for (bool deeper = found != null; deeper;)
                {
                    deeper = false;
                    for (int i = s_zOrder.Count - 1; i >= 0; i--)
                        if (s_zOrder[i] != found && s_zOrder[i].Contains(x, y) && s_zOrder[i].IsEmbeddedIn(found)) { found = s_zOrder[i]; deeper = true; break; }
                }
            }
            left = found?._xPixels ?? 0;
            top = found?._yPixels ?? 0;
            return found?._handle ?? IntPtr.Zero;
        }

        // a child: SetParentWindow's, or (made before its parent, so the parent was not there to name) a
        // borderless window drawn into the parent's surface, within it
        private bool IsEmbeddedIn(AndroidWindow parent) => _parentWindow == parent._handle
            || (_parentWindow == IntPtr.Zero && IsBorderless && !_ownSurface && !parent.IsBorderless
                && _xPixels >= parent._xPixels && _yPixels >= parent._yPixels
                && _xPixels + _widthPixels <= parent._xPixels + parent._widthPixels && _yPixels + _heightPixels <= parent._yPixels + parent._heightPixels);

        private bool Contains(int x, int y) => _handle != IntPtr.Zero && _visible && _widthPixels > 1 && _heightPixels > 1
            && x >= _xPixels - _frameLeft && x < _xPixels + _widthPixels + _frameRight && y >= _yPixels - _frameTop && y < _yPixels + _heightPixels + _frameBottom;

        /// <summary>Where a window is (top-left device pixels), false when there is no such window.</summary>
        public static bool GetOrigin(IntPtr handle, out int left, out int top)
        {
            AndroidWindow w = FromHandle(handle);
            left = w?._xPixels ?? 0;
            top = w?._yPixels ?? 0;
            return w != null;
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

        /// <summary>The window is shown to the two eyes apart -- a headset's window holding 3D (AndroidXrHost):
        /// the compositor draws it twice into a surface twice its width, the left eye's picture beside the
        /// right eye's.</summary>
        public bool StereoPair { get; set; }

        public static bool IsStereoWindow(IntPtr handle)
            => s_byHandle.TryGetValue(handle, out AndroidWindow w) && w.StereoPair;

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
                seam?.GetProperty("StereoWindowQuery", PublicStatic)?.SetValue(null, (Func<IntPtr, bool>)IsStereoWindow);

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
            => NotifyKey(IntPtr.Zero, down, keyCode, unicodeChar, metaState);

        /// <summary>A key for <paramref name="window"/> (zero: the window that has the keys) -- a headset
        /// controller's modifier for the window under its ray (AndroidXrHost's Alt for an orbit).</summary>
        public static bool NotifyKey(IntPtr window, bool down, int keyCode, int unicodeChar, int metaState)
        {
            int vk = VirtualKeyOfKeyCode(keyCode);
            if (vk == 0) return false;
            string chars = unicodeChar > 0 ? char.ConvertFromUtf32(unicodeChar) : null;
            var message = new KeyMessage(IntPtr.Zero, down, vk, chars,
                (metaState & MetaShiftOn) != 0, (metaState & MetaCtrlOn) != 0, (metaState & MetaAltOn) != 0, (metaState & MetaMetaOn) != 0,
                Environment.TickCount);
            OnUi(() => KeyInput?.Invoke(message with { Window = window != IntPtr.Zero ? window : s_keyWindow != IntPtr.Zero ? s_keyWindow : s_mainHandle }));
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
        /// <param name="Pointer">A pointer's (a headset controller's ray), not a finger's: it stays where it is
        /// after a release, as a mouse's does, instead of lifting off.</param>
        public readonly record struct TouchMessage(IntPtr Window, int Kind, int X, int Y, int TimestampMs, int Wheel = 0, bool Pointer = false);

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

        /// <summary>A pointer that is a mouse, not a finger (an XR controller's ray, AndroidXrHost): kind 0 move,
        /// 1 press, 2 release, RightPress / RightRelease the right button, at device pixels of the window --
        /// straight to the window, with no press and hold (a button held still is a button held, not a right
        /// click).</summary>
        // a pointer is a mouse, not a finger: what it drags it drags (a selection, a new control's
        // rectangle), never scrolling the page as a finger's pan does. Where on the SCREEN (device pixels):
        // made the window's own here, on the UI thread, where the window is now -- made on the pointer's
        // thread, a window being dragged had moved on by the time it got there, and each move overshot by
        // the last (the window flew off).
        //
        // A ray is never still, so it moves every frame it is shown: a move waiting for the UI thread is
        // updated rather than another queued (each is a WM_MOUSEMOVE and the window drawn again; queued, a
        // click waited seconds behind them). A press or a release goes in order, after the move before it.
        public static void NotifyPointer(IntPtr handle, int kind, int screenX, int screenY)
        {
            if (kind == 0)
            {
                int generation;
                lock (s_pointerLock)
                {
                    bool queued = s_pointerMove != null;
                    s_pointerMove = (handle, screenX, screenY);
                    if (queued) return;
                    generation = s_pointerGeneration;
                }
                OnUi(() =>
                {
                    (IntPtr Handle, int X, int Y)? move;
                    lock (s_pointerLock)
                    {
                        if (generation != s_pointerGeneration) return;   // a press or a release took it
                        move = s_pointerMove;
                        s_pointerMove = null;
                    }
                    if (move is { } m) Pointer(m.Handle, 0, m.X, m.Y);
                });
                return;
            }
            (IntPtr Handle, int X, int Y)? before;
            lock (s_pointerLock)
            {
                before = s_pointerMove;
                s_pointerMove = null;
                s_pointerGeneration++;
            }
            OnUi(() =>
            {
                if (before is { } m) Pointer(m.Handle, 0, m.X, m.Y);
                Pointer(handle, kind, screenX, screenY);
            });
        }

        private static readonly object s_pointerLock = new object();
        private static (IntPtr Handle, int X, int Y)? s_pointerMove;   // the move waiting for the UI thread
        private static int s_pointerGeneration;                         // a press or a release since it was queued

        private static void Pointer(IntPtr handle, int kind, int screenX, int screenY)
        {
            GetOrigin(handle, out int left, out int top);
            int x = screenX - left, y = screenY - top;
            if (kind is TouchHold.RightPress or TouchHold.RightRelease) EmitHeld(handle, kind, x, y);
            else Touch(handle, kind, x, y, pans: false);
        }

        public static void NotifyTouch(IntPtr handle, int kind, int xPixels, int yPixels)
            => OnUi(() => s_hold.Touch(handle, kind, xPixels, yPixels, s_byHandle.TryGetValue(handle, out AndroidWindow w) ? w.GetBackingScale() : 1.0));

        // Press and hold: the right button (TouchHold). Its right press and release go to MouseInput as they
        // are; the left button's kinds through Touch, as every touch did.
        private static readonly TouchHold s_hold = new TouchHold(EmitHeld, OnUi, h => s_byHandle.ContainsKey(h));

        private static void EmitHeld(IntPtr handle, int kind, double x, double y)
        {
            int px = (int)Math.Round(x), py = (int)Math.Round(y);
            if (kind is TouchHold.RightPress or TouchHold.RightRelease)
            {
                if (s_trace && kind == TouchHold.RightPress) Console.WriteLine($"[android] press and hold 0x{handle:x} ({px},{py}): right click");
                try { MouseInput?.Invoke(new TouchMessage(handle, kind, px, py, Environment.TickCount)); }
                catch (Exception e) { Console.WriteLine($"WPF Android touch dispatch failed: {e}"); }
                return;
            }
            Touch(handle, kind, px, py);
        }

        /// <summary>TouchMessage kinds of the right button (a press held still, TouchHold).</summary>
        public const int RightPress = TouchHold.RightPress, RightRelease = TouchHold.RightRelease;

        private static void Touch(IntPtr handle, int kind, int xPixels, int yPixels, bool pans = true)
        {
            // Where on the screen, which a move or size loop follows: the view itself moves under the finger.
            AndroidWindow at = FromHandle(handle);
            int sx = xPixels + (at?._xPixels ?? 0), sy = yPixels + (at?._yPixels ?? 0);
            if (s_trace && kind != 0) Console.WriteLine($"[android] touch {(kind == 1 ? "down" : "up")} 0x{handle:x} ({xPixels},{yPixels}) screen ({sx},{sy}){(s_drag != null ? " (loop)" : "")}");
            if (s_drag != null)
            {
                if (kind == 0) DragTo(sx, sy);
                else if (kind == 2) { DragTo(sx, sy); s_drag = null; s_pressed = false; s_framePress = null; }
                return;
            }
            s_cursorX = sx; s_cursorY = sy; s_cursorKnown = true;
            if (kind == 1) { s_pressX = sx; s_pressY = sy; s_pressed = true; }
            else if (kind == 2) s_pressed = false;
            // a press gives a window the keys, and a child window that takes them (an ElementHost's WPF
            // tree) -- never a popup (a menu, a tooltip), as on Windows
            if (kind == 1 && FromHandle(handle) is { } pressed)
            {
                if (!pressed.IsBorderless || pressed._ownSurface || pressed.TakesKeyboardOnPress) s_keyWindow = handle;
                SetActive(pressed);   // a popup or a child leaves it (SetActive)
            }
            // the frame's: a press on the caption, an edge or the close button (DefWindowProc's)
            if (at != null && at.HasFrame && at.OnFrame(kind, xPixels, yPixels)) return;
            // Never let a managed exception unwind into the Java frame that delivered this.
            try
            {
                Action<TouchMessage> handler = MouseInput;
                if (handler == null) return;

                double scale = s_byHandle.TryGetValue(handle, out AndroidWindow w) ? w.GetBackingScale() : 1.0;
                handler(new TouchMessage(handle, kind, xPixels, yPixels, Environment.TickCount, Pointer: !pans));
                if (!pans) return;

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
