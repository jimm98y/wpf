// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The iOS/UIKit windowing backend — the iOS sibling of CocoaWindow and BrowserWindow.
//
// iOS has ONE fullscreen UIWindow, so a WPF "window" is a UIView inside it, and the UIView* is what
// flows through WPF as the HWND (exactly as CocoaWindow exposes its NSView*). The windows are the
// browser head's: the first top-level window is the app's own and fills the screen; every other one
// (a dialog, a tool window) floats over it at the size and place it asks for, is brought to the front
// when activated, and is moved and resized by this layer (BeginMoveDrag, BeginResizeDrag), as
// DefWindowProc's move and size loops do on Windows. The WebGPU compositor
// turns that same pointer into a Metal surface (IosInterop.CreateSurface), so a WPF Window maps to:
// UIWindow -> root UIView -> our WpfMetalView (its backing layer IS a CAMetalLayer).
//
// Why a synthesised ObjC class instead of just adding a CAMetalLayer sublayer, as macOS does:
// CALayer.autoresizingMask is `API_UNAVAILABLE(ios)` (macOS/Mac Catalyst only). On macOS MacInterop
// can add the metal layer as an autoresizing SUBLAYER of a stock NSView and Core Animation keeps it
// sized. On iOS a sublayer would keep its initial frame while the view resized (rotation, split
// view, keyboard) and Core Animation would stretch a stale drawable over the new bounds. Overriding
// +layerClass makes the view's BACKING layer the CAMetalLayer, and UIKit keeps a backing layer's
// geometry in sync with its view for free. That needs a UIView subclass, and WindowsBase cannot
// reference UIKit, so the class is built at run time through the Objective-C runtime — the same
// technique CocoaWindow uses for its WpfAppearanceObserver.
//
// Popups: iOS has no child windows, so WPF Popups become borderless sibling subviews presenting
// through their own swap chains (NativePlatform.SupportsLayeredWindows is false off Windows), which
// is how the browser head behaves too.
//

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace MS.Internal.Interop
{
    /// <summary>
    /// A single WPF window on iOS: a Metal-backed UIView inside the app's one UIWindow. Its HWND is a
    /// small value of its own (<see cref="Handle"/>), IntPtr.Zero until <see cref="Create"/>.
    /// </summary>
    public sealed unsafe class UIKitWindow : IPlatformWindow
    {
        private static readonly Dictionary<IntPtr, UIKitWindow> s_byView = new Dictionary<IntPtr, UIKitWindow>();
        private static readonly List<UIKitWindow> s_zOrder = new List<UIKitWindow>();   // last = topmost

        private IntPtr _view;      // UIView* (WpfMetalView)
        private IntPtr _handle;    // the HWND (see Handle)

        // 0x0F00_0000 up: clear of WinForms' driver handles, the Win32 shim's, BrowserWindow's 0x0B00_0000+,
        // the web overlays' 0x0C00_0000+, WaylandWindow's 0x0D00_0000+, CocoaWindow's 0x0E00_0000+ and
        // HwndWrapper's hidden windows at 0x7F00_0000+.
        private static int s_nextHandle = 0x0F00_0000;
        private static readonly Dictionary<IntPtr, UIKitWindow> s_byHandle = new Dictionary<IntPtr, UIKitWindow>();

        /// <summary>The UIView* of a window <paramref name="handle"/>; anything that is not one of these
        /// windows' handles (a view already, a WinForms host's own) is returned as it is.</summary>
        internal static IntPtr ViewOf(IntPtr handle)
        {
            if (s_byHandle.TryGetValue(handle, out UIKitWindow w)) return w._view;
            // one of these handles whose window has gone has no view: passed on as it is, the number
            // was messaged as an object (the compositor asks about a closed window's target once more)
            if ((long)handle >= 0x0F00_0000 && (long)handle <= s_nextHandle) return IntPtr.Zero;
            return handle;
        }

        /// <summary>The handle of the window whose view is <paramref name="view"/>, or <paramref name="view"/>
        /// itself when it is no such view (it may be a handle already).</summary>
        internal static IntPtr HandleOf(IntPtr view)
            => s_byView.TryGetValue(view, out UIKitWindow w) && w._handle != IntPtr.Zero ? w._handle : view;

        private static readonly bool s_trace = Environment.GetEnvironmentVariable("WPF_TRACE_UIKIT") == "1";
        internal static bool Trace => s_trace;

        // The app's own window: the first top-level one, which fills the screen. The others float.
        private static IntPtr s_mainView;

        private bool IsMain => _view != IntPtr.Zero && _view == s_mainView;

        private bool _ownSurface;   // a borderless window with a surface of its own (Create)

        /// <summary>A child window that takes the keyboard when pressed, as SetFocus on one does on Windows.</summary>
        public bool TakesKeyboardOnPress { get; set; }

        /// <summary>Whether the app has its own window yet (the first framed one, whichever stack made it).</summary>
        public static bool HasMainWindow => s_mainView != IntPtr.Zero && s_byView.ContainsKey(s_mainView);

        /// <summary>A top-level window over the app's own, which fills the screen: a dialog, a tool window.</summary>
        public bool IsSecondary => !IsBorderless && !IsMain;

        /// <summary>The window draws its own frame -- Windows' title bar, edges and the shadow in the
        /// see-through margin round them (the WinForms host's WindowChrome) -- so it gets none here.</summary>
        public void SetOwnFrame()
        {
            if (_view == IntPtr.Zero) return;
            SendVoidBool(_view, Sel("setOpaque:"), false);
            IntPtr layer = Send(_view, Sel("layer"));
            if (layer == IntPtr.Zero) return;
            SendVoidBool(layer, Sel("setOpaque:"), false);
            SendVoidFloat(layer, Sel("setShadowOpacity:"), 0f);
        }

        /// <summary>
        /// The window's HWND: a small value of its own, not the view's address, as on macOS (CocoaWindow.Handle).
        /// A Windows window handle always fits in 32 bits and code relies on it -- an app's
        /// WindowUtil.SetOwner takes owner.ToInt32(), which overflowed on a 64-bit UIView* and its login
        /// dialog never opened. What reaches UIKit or Metal with a handle resolves it through
        /// <see cref="ViewOf"/>; what UIKit reports by view is turned into this through <see cref="HandleOf"/>.
        /// </summary>
        public IntPtr Handle => _handle;

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
        private static UIKitWindow s_active;

        private bool CanBeActive => _view != IntPtr.Zero && (!IsBorderless || _ownSurface);

        /// <summary>Makes <paramref name="window"/> the active window: the one that was is told first that
        /// it no longer is, as WM_ACTIVATE (WA_INACTIVE) reaches it before the new one's WA_ACTIVE.</summary>
        private static void SetActive(UIKitWindow window)
        {
            if (window != null && !window.CanBeActive) return;
            if (ReferenceEquals(s_active, window)) return;
            UIKitWindow old = s_active;
            s_active = window;
            if (s_trace) Console.WriteLine($"[uikit] active 0x{old?._view ?? IntPtr.Zero:x} -> 0x{window?._view ?? IntPtr.Zero:x}");
            old?.ActiveChanged?.Invoke(false);
            window?.ActiveChanged?.Invoke(true);
        }

        /// <summary>The active window goes away (hidden, destroyed): the activation passes to the app's
        /// own window, as Windows activates the next one, or to none.</summary>
        private void PassActivation()
        {
            if (!ReferenceEquals(s_active, this)) return;
            UIKitWindow next = s_mainView != _view && s_byView.TryGetValue(s_mainView, out UIKitWindow main) ? main : null;
            if (next != null && SendBool(next._view, Sel("isHidden"))) next = null;
            SetActive(next);
        }

        /// <summary>
        /// iOS changes scale only when a window moves between displays (external display / Stage
        /// Manager); declared to satisfy IPlatformWindow and raised by <see cref="NotifyScaleChanged"/>.
        /// </summary>
        public event Action<double> ScaleChanged;

        /// <summary>
        /// The app head's root UIView (the UIWindow's rootViewController.view) that WPF windows are
        /// added into. The iOS head sets this before running the dispatcher.
        /// </summary>
        public static IntPtr RootView { get; set; }

        /// <summary>The window of a handle -- or of its view, which UIKit's callbacks name.</summary>
        public static UIKitWindow FromHandle(IntPtr handle)
            => s_byHandle.TryGetValue(handle, out UIKitWindow w) || s_byView.TryGetValue(handle, out w) ? w : null;

        /// <param name="x">Origin in top-left device PIXELS (borderless popups; the main window fills the screen).</param>
        public void Create(string title, int x, int y, int width, int height, bool borderless)
            => Create(title, x, y, width, height, borderless, ownSurface: false);

        /// <summary>As <see cref="Create(string, int, int, int, int, bool)"/>; <paramref name="ownSurface"/>: a
        /// borderless window that presents through a surface of its own rather than its owner's, as a
        /// borderless form does (a splash screen) -- it floats, is stacked and activated as a window.</summary>
        public void Create(string title, int x, int y, int width, int height, bool borderless, bool ownSurface)
        {
            _ownSurface = borderless && ownSurface;
            // UIKit views exist on the main thread only; touching one elsewhere is undefined (and
            // the views and z-order registries below are not locked). Refuse catchably, as the
            // Cocoa head does, instead of corrupting the one UI thread there is.
            if (!IsMainThread)
            {
                throw new InvalidOperationException(
                    "iOS has one UI thread: a WPF window can only be created on the main thread. " +
                    "Marshal the call to the main thread's Dispatcher.");
            }

            IsBorderless = borderless;

            double scale = ScreenScale();
            var frame = new CGRect
            {
                x = x / scale,
                y = y / scale,
                width = width / scale,
                height = height / scale,
            };

            // The app's own window fills the screen, which is all the desktop there is: WPF asks for its
            // own default/desired size (640x400 etc.), which would leave the app rendering into a
            // rectangle in a corner. Other top-level windows keep the frame they ask for, as popups do.
            bool main = !borderless && (s_mainView == IntPtr.Zero || !s_byView.ContainsKey(s_mainView));
            if (main && RootView != IntPtr.Zero)
            {
                CGRect rootBounds = SendRect(RootView, Sel("bounds"));

                // The head may create the WPF window before UIKit has laid the root view out, in
                // which case bounds is still EMPTY - snapping to it produced a zero-sized view and
                // a completely blank screen (the app renders happily into a 0x0 surface). Fall back
                // to the screen so the window is always visible; a later layout pass resizes it.
                if (rootBounds.width < 1 || rootBounds.height < 1)
                {
                    IntPtr screen = Send(objc_getClass("UIScreen"), Sel("mainScreen"));
                    if (screen != IntPtr.Zero) rootBounds = SendRect(screen, Sel("bounds"));
                }

                if (rootBounds.width >= 1 && rootBounds.height >= 1)
                    frame = rootBounds;
            }

            // A popup is drawn INTO its owner's surface (NativePlatform.PopupsShareOwnerSurface), so it
            // needs no Metal layer of its own -- only somewhere for UIKit to deliver its touches.
            _view = borderless && !_ownSurface ? CreateTouchOnlyView(frame) : CreateMetalBackedView(frame, fillsSuperview: main);
            if (_view == IntPtr.Zero)
                return;
            if (main) s_mainView = _view;
            else if (!borderless) AddShadow(_view);

            IntPtr root = RootView;
            if (root != IntPtr.Zero)
                SendVoidPtr(root, Sel("addSubview:"), _view);

            s_byView[_view] = this;
            _handle = (IntPtr)(s_nextHandle += 4);
            s_byHandle[_handle] = this;
            AppContext.SetData("WpfWebGpu.UIKitWindow.ViewOf", (Func<IntPtr, IntPtr>)ViewOf);
            s_zOrder.Add(this);
            if (s_trace)
                Console.WriteLine($"[uikit] window 0x{_view:x} '{title}' {(main ? "main" : borderless ? (_ownSurface ? "borderless+surface" : "popup") : "floating")} " +
                                  $"at ({frame.x:F0},{frame.y:F0}) {frame.width:F0}x{frame.height:F0}pt");

            // After the view is registered: a drag can arrive as soon as the interactions exist, and
            // the drop target resolves the window from the view it names.
            UIKitDragDrop.AttachInteractions(_view);
        }

        /// <summary>Show/hide the view (WPF's ShowWindow SW_HIDE/SW_SHOW).</summary>
        /// <remarks>
        /// setHidden: rather than removeFromSuperview: the view keeps its place in the hierarchy and
        /// its Metal layer, so the same window can be shown again without being rebuilt — which is
        /// what a caller that hides and re-shows a cached window (a docking adorner between drags)
        /// needs. A hidden UIView also stops receiving touches, so a hidden window cannot swallow
        /// input, matching the platform behaviour on the other heads.
        /// </remarks>
        public void SetVisible(bool visible)
        {
            if (_view == IntPtr.Zero) return;
            if (!visible) _minimized = false;
            SendVoidBool(_view, Sel("setHidden:"), !visible);
            if (!visible) PassActivation();
        }

        public void SetVisible(bool visible, bool activate)
        {
            SetVisible(visible);
            if (visible && activate) Activate();
        }

        /// <summary>To the front, as SetForegroundWindow brings a window there: above the windows, under
        /// the popups that are open (a menu stays over the window it belongs to).</summary>
        public void Activate()
        {
            if (_view == IntPtr.Zero || RootView == IntPtr.Zero || (IsBorderless && !_ownSurface)) return;
            s_keyWindow = _view;
            TakeKeys();
            SetActive(this);
            SendVoidPtr(RootView, Sel("bringSubviewToFront:"), _view);
            s_zOrder.Remove(this);
            s_zOrder.Add(this);
            // its child windows come with it, just above it (SetParentWindow)...
            RaiseChildren(this);
            // ...and the popups stay above it. Not a child window (an ElementHost's WPF content), which is
            // a borderless view too: raised over every window, it lay over a dialog shown above its form
            // and took the dialog's touches.
            foreach (UIKitWindow popup in s_zOrder.ToArray())
            {
                if (!popup.IsBorderless || popup._ownSurface || popup._view == IntPtr.Zero || popup._parentWindow != null) continue;
                SendVoidPtr(RootView, Sel("bringSubviewToFront:"), popup._view);
                s_zOrder.Remove(popup);
                s_zOrder.Add(popup);
            }
        }

        private UIKitWindow _parentWindow;   // a child window's parent (SetParentWindow)

        /// <summary>The window is a child of <paramref name="parent"/> (a UIKitWindow's handle; any other is
        /// ignored): its view lies just above the parent's and goes where the parent goes in the z-order,
        /// under the windows above the parent, as Windows stacks a child window with its parent.</summary>
        public void SetParentWindow(IntPtr parent)
        {
            UIKitWindow p = FromHandle(parent);
            if (_view == IntPtr.Zero || p == null || p == this || p._view == IntPtr.Zero || RootView == IntPtr.Zero) return;
            _parentWindow = p;
            PlaceAbove(this, p);
            RaiseChildren(this);
        }

        private static void PlaceAbove(UIKitWindow child, UIKitWindow parent)
        {
            SendVoidPtrPtr(RootView, Sel("insertSubview:aboveSubview:"), child._view, parent._view);
            s_zOrder.Remove(child);
            int at = s_zOrder.IndexOf(parent);
            s_zOrder.Insert(at < 0 ? s_zOrder.Count : at + 1, child);
        }

        /// <summary>The child windows of <paramref name="parent"/> (and theirs), just above it.</summary>
        private static void RaiseChildren(UIKitWindow parent)
        {
            foreach (UIKitWindow child in s_zOrder.ToArray())
            {
                if (child._parentWindow != parent || child._view == IntPtr.Zero) continue;
                PlaceAbove(child, parent);
                RaiseChildren(child);
            }
        }

        // ---- moving and sizing a window -------------------------------------------
        //
        // DefWindowProc's move and size loops (WM_SYSCOMMAND SC_MOVE / SC_SIZE): begun while the press
        // that asked for them is being handled, the window follows the finger until it lifts, and the
        // loop takes the touches meanwhile -- the app sees none of them. The browser head's are the same.

        // ---- minimized, maximized, restored: ShowWindow's states -----------------------------------
        //
        // As AndroidWindow has them: the app's own window fills the screen and stays a normal window
        // there; a floating one is maximized to the rectangle WM_GETMINMAXINFO gives it (the screen,
        // unless the window says otherwise) and restored to the frame it had, and WPF hears of it from
        // the WM_SIZE that follows (GetWindowState gives its wParam), which raises Window.StateChanged.
        // Minimized, with no taskbar to go to, it is out of sight until it is restored.

        private bool _maximized, _minimized;
        private CGRect _restoreFrame, _maximizedFrame;

        /// <summary>The window's maximized rectangle, asked of the window as Windows asks it (WM_GETMINMAXINFO,
        /// HwndWrapper's): given the monitor's rectangle, then the head's maximized position and size, it
        /// answers the position and size (device pixels), or null to keep them.</summary>
        public Func<int[], int[]> QueryMaximizedRect { get; set; }

        public void SetWindowState(int state)
        {
            if (_view == IntPtr.Zero || IsMain) return;
            if (s_trace) Console.WriteLine($"[uikit] 0x{_view:x} state -> SW {state}");
            switch (state)
            {
                case 2: case 6: case 7: case 11:   // SW_SHOWMINIMIZED, SW_MINIMIZE, SW_SHOWMINNOACTIVE, SW_FORCEMINIMIZE
                    if (_minimized) return;
                    _minimized = true;
                    SendVoidBool(_view, Sel("setHidden:"), true);
                    RaiseResized();
                    break;

                case 3:   // SW_SHOWMAXIMIZED / SW_MAXIMIZE
                    Unminimize();
                    if (!IsMaximizedNow)
                    {
                        _restoreFrame = SendRect(_view, Sel("frame"));
                        GetPrimaryScreenPixels(out _, out _, out int width, out int height, out _, out _, out _, out _);
                        int x = 0, y = 0;
                        if (QueryMaximizedRect?.Invoke(new[] { 0, 0, width, height, 0, 0, width, height }) is int[] r && r.Length >= 4 && r[2] > 0 && r[3] > 0)
                        {
                            x = r[0]; y = r[1]; width = r[2]; height = r[3];
                        }
                        double scale = ScreenScale();
                        _maximizedFrame = new CGRect { x = x / scale, y = y / scale, width = width / scale, height = height / scale };
                        SendVoidRect(_view, Sel("setFrame:"), _maximizedFrame);
                        _maximized = true;
                    }
                    RaiseResized();
                    break;

                case 1: case 9:   // SW_SHOWNORMAL, SW_RESTORE
                    bool wasMinimized = Unminimize();
                    if (IsMaximizedNow)
                    {
                        _maximized = false;
                        SendVoidRect(_view, Sel("setFrame:"), _restoreFrame);
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
            SendVoidBool(_view, Sel("setHidden:"), false);
            return true;
        }

        /// <summary>Maximized while it keeps its maximized frame: moved or sized out of it (a drag on its
        /// caption), it is a normal window again, as on Windows.</summary>
        private bool IsMaximizedNow
        {
            get
            {
                if (!_maximized || IsMain || _view == IntPtr.Zero) return false;
                CGRect f = SendRect(_view, Sel("frame"));
                if (Math.Abs(f.x - _maximizedFrame.x) < 0.5 && Math.Abs(f.y - _maximizedFrame.y) < 0.5
                    && Math.Abs(f.width - _maximizedFrame.width) < 0.5 && Math.Abs(f.height - _maximizedFrame.height) < 0.5)
                    return true;
                _maximized = false;
                return false;
            }
        }

        public int GetWindowState() => _minimized ? 2 : IsMaximizedNow ? 3 : 1;

        private sealed class Drag
        {
            public UIKitWindow Window;
            public int HitTest;                 // 0 for a move; HTLEFT..HTBOTTOMRIGHT for a size
            public double X0, Y0;               // the press, in the root view's points
            public CGRect Frame0;               // the window's frame then
            public double MinW, MinH, MaxW, MaxH;
        }

        private static Drag s_drag;
        private static double s_pressX, s_pressY;   // the last press, in the root view's points
        private static int s_cursorX, s_cursorY;
        private static bool s_cursorKnown;

        /// <summary>GetCursorPos: where the finger (or the pointer) last was, in screen device pixels.</summary>
        internal static bool TryGetCursorPixels(out int x, out int y)
        {
            x = s_cursorX;
            y = s_cursorY;
            return s_cursorKnown;
        }
        private static bool s_pressed;

        public void BeginMoveDrag()
        {
            if (_view == IntPtr.Zero || IsMain || !s_pressed) return;
            s_drag = new Drag { Window = this, X0 = s_pressX, Y0 = s_pressY, Frame0 = SendRect(_view, Sel("frame")) };
        }

        /// <summary>A floating window has no frame of its own to be resized by: this layer does it.</summary>
        public bool ResizesFromEdges => !IsBorderless && !IsMain;

        public bool BeginResizeDrag(int hitTest, int minWidth, int minHeight, int maxWidth, int maxHeight)
        {
            if (!ResizesFromEdges || !s_pressed || hitTest < 10 || hitTest > 17) return false;
            double scale = ScreenScale();
            s_drag = new Drag
            {
                Window = this, HitTest = hitTest, X0 = s_pressX, Y0 = s_pressY, Frame0 = SendRect(_view, Sel("frame")),
                MinW = Math.Max(minWidth / scale, 1), MinH = Math.Max(minHeight / scale, 1),
                MaxW = maxWidth > 0 ? maxWidth / scale : double.MaxValue, MaxH = maxHeight > 0 ? maxHeight / scale : double.MaxValue,
            };
            return true;
        }

        private static void DragTo(double x, double y)
        {
            Drag d = s_drag;
            if (d.Window._view == IntPtr.Zero) { s_drag = null; return; }
            double dx = x - d.X0, dy = y - d.Y0;
            CGRect f = d.Frame0;
            if (d.HitTest == 0)
            {
                f.x += dx;
                // the top edge stays on the screen, so that the caption can be taken hold of again
                f.y = Math.Max(0, f.y + dy);
                SendVoidRect(d.Window._view, Sel("setFrame:"), f);
                return;
            }
            int ht = d.HitTest;
            bool left = ht is 10 or 13 or 16, right = ht is 11 or 14 or 17;
            bool top = ht is 12 or 13 or 14, bottom = ht is 15 or 16 or 17;
            double w = f.width, h = f.height;
            if (right) w += dx; else if (left) w -= dx;
            if (bottom) h += dy; else if (top) h -= dy;
            w = Math.Min(Math.Max(w, d.MinW), d.MaxW);
            h = Math.Min(Math.Max(h, d.MinH), d.MaxH);
            f.x = left ? d.Frame0.x + d.Frame0.width - w : d.Frame0.x;
            f.y = top ? d.Frame0.y + d.Frame0.height - h : d.Frame0.y;
            if (f.y < 0) { if (top) h += f.y; f.y = 0; }
            f.width = w;
            f.height = h;
            SendVoidRect(d.Window._view, Sel("setFrame:"), f);
            d.Window.RaiseResized();
        }

        public void Destroy()
        {
            if (_view == IntPtr.Zero)
                return;

            PassActivation();
            if (ReferenceEquals(s_active, this)) s_active = null;
            foreach (UIKitWindow child in s_zOrder) if (child._parentWindow == this) child._parentWindow = null;
            _parentWindow = null;
            Send(_view, Sel("removeFromSuperview"));
            s_byView.Remove(_view);
            s_byHandle.Remove(_handle);
            s_zOrder.Remove(this);
            if (s_keyWindow == _view) s_keyWindow = s_mainView != _view ? s_mainView : IntPtr.Zero;
            if (s_drag?.Window == this) s_drag = null;
            Send(_view, Sel("release"));
            _view = IntPtr.Zero;
            _handle = IntPtr.Zero;
        }

        /// <summary>Resize the content area, in POINTS.</summary>
        public void SetContentSize(int width, int height)
        {
            if (_view == IntPtr.Zero) return;
            // The app's own window: keep OUR frame, but still report the size. RaiseResized drives
            // the synthetic WM_SIZE -> OnResize -> UpdateWindowSettings that tells the compositor
            // its target size; swallowing it leaves the target at 0x0 and NOTHING is composited
            // (window correctly sized and interactive, but a blank screen).
            if (IsFullScreenTopLevel) { RaiseResized(); return; }
            CGRect f = SendRect(_view, Sel("frame"));
            f.width = width;
            f.height = height;
            SendVoidRect(_view, Sel("setFrame:"), f);
            RaiseResized();
        }

        /// <summary>Resize from DEVICE PIXELS, as WPF's SetWindowPos supplies. Keeps fractional
        /// points so an odd pixel size round-trips exactly at @2x/@3x (as CocoaWindow does).</summary>
        public void SetContentSizePixels(int cx, int cy)
        {
            if (s_trace) Console.WriteLine($"[uikit] 0x{_view:x} size -> {cx}x{cy}px");
            if (_view == IntPtr.Zero) return;
            if (IsFullScreenTopLevel) { RaiseResized(); return; }   // see SetContentSize
            double scale = ScreenScale();
            CGRect f = SendRect(_view, Sel("frame"));
            f.width = cx / scale;
            f.height = cy / scale;
            SendVoidRect(_view, Sel("setFrame:"), f);
            RaiseResized();
        }

        public void SetFrameOrigin(int xPixels, int yPixels)
        {
            if (s_trace) Console.WriteLine($"[uikit] 0x{_view:x} origin -> ({xPixels},{yPixels})px{(IsFullScreenTopLevel ? " (ignored: the app's own window)" : "")}");
            if (_view == IntPtr.Zero || IsFullScreenTopLevel) return;
            double scale = ScreenScale();
            CGRect f = SendRect(_view, Sel("frame"));
            f.x = xPixels / scale;
            f.y = yPixels / scale;
            SendVoidRect(_view, Sel("setFrame:"), f);
        }

        public void GetContentSize(out int width, out int height)
        {
            CGRect f = _view != IntPtr.Zero ? SendRect(_view, Sel("bounds")) : default;
            width = (int)Math.Round(f.width);
            height = (int)Math.Round(f.height);
        }

        public void GetPixelSize(out int width, out int height)
        {
            CGRect f = _view != IntPtr.Zero ? SendRect(_view, Sel("bounds")) : default;
            double scale = ScreenScale();
            width = (int)Math.Round(f.width * scale);
            height = (int)Math.Round(f.height * scale);
        }

        /// <summary>iOS has no window caption, so the outer window size equals the content size
        /// (same as the browser head).</summary>
        public void GetWindowPixelSize(out int width, out int height) => GetPixelSize(out width, out height);

        public void GetClientScreenOriginPixels(out int sx, out int sy)
        {
            sx = sy = 0;
            if (_view == IntPtr.Zero) return;

            // Origin of our bounds in the window's coordinate space (nil = window), then to pixels.
            CGRect bounds = SendRect(_view, Sel("bounds"));
            CGRect inWindow = SendRectRectPtr(_view, Sel("convertRect:toView:"), bounds, IntPtr.Zero);
            double scale = ScreenScale();
            sx = (int)Math.Round(inWindow.x * scale);
            sy = (int)Math.Round(inWindow.y * scale);
        }

        /// <summary>
        /// The app's own window is pinned to the whole screen, so WPF's SetWindowPos calls for it are
        /// ignored - GetPixelSize keeps reporting the real bounds and layout follows those. The other
        /// windows and the popups keep their requested geometry.
        /// </summary>
        private bool IsFullScreenTopLevel => IsMain && RootView != IntPtr.Zero;

        public double GetBackingScale() => ScreenScale();

        /// <summary>
        /// The display's refresh ceiling. maximumFramesPerSecond is 120 on a ProMotion iPad and 60
        /// elsewhere; it is the ceiling rather than the current adaptive rate, which is what WPF
        /// wants for pacing.
        /// </summary>
        public double GetRefreshRateHz()
        {
            IntPtr screen = Send(objc_getClass("UIScreen"), Sel("mainScreen"));
            if (screen == IntPtr.Zero) return 0;
            nint fps = SendNInt(screen, Sel("maximumFramesPerSecond"));
            return fps > 0 ? fps : 0;
        }

        /// <summary>Topmost registered window containing the point (top-left device pixels).</summary>
        public static IntPtr HitTest(int x, int y)
        {
            for (int i = s_zOrder.Count - 1; i >= 0; i--)
            {
                UIKitWindow w = s_zOrder[i];
                if (w._view == IntPtr.Zero) continue;
                // a hidden window is no window under the point, as WindowFromPoint passes it over
                if (SendBool(w._view, Sel("isHidden"))) continue;

                w.GetClientScreenOriginPixels(out int ox, out int oy);
                w.GetPixelSize(out int pw, out int ph);
                if (x >= ox && x < ox + pw && y >= oy && y < oy + ph)
                    return w._handle;
            }
            return IntPtr.Zero;
        }

        /// <summary>The whole screen in device pixels; iOS has no work-area distinction.</summary>
        public static bool GetPrimaryScreenPixels(
            out int monLeft, out int monTop, out int monRight, out int monBottom,
            out int workLeft, out int workTop, out int workRight, out int workBottom)
        {
            monLeft = monTop = workLeft = workTop = 0;
            monRight = workRight = 0;
            monBottom = workBottom = 0;

            IntPtr screen = Send(objc_getClass("UIScreen"), Sel("mainScreen"));
            if (screen == IntPtr.Zero) return false;

            CGRect b = SendRect(screen, Sel("bounds"));
            double scale = ScreenScale();
            monRight = workRight = (int)Math.Round(b.width * scale);
            monBottom = workBottom = (int)Math.Round(b.height * scale);
            return true;
        }

        /// <summary>Called by the head when the view's size changed (rotation, split view).</summary>
        public void NotifyResized()  => RaiseResized();

        /// <summary>Called by the head when the window moved to a display with another scale.</summary>
        public void NotifyScaleChanged() => ScaleChanged?.Invoke(ScreenScale());

        private void RaiseResized()
        {
            if (!IsMain && !IsBorderless && _view != IntPtr.Zero) UpdateShadowPath(_view);
            GetPixelSize(out int w, out int h);
            Resized?.Invoke(w, h);
        }

        // ---- The Metal-backed UIView class, synthesised at run time ---------------

        private static IntPtr s_metalViewClass;

        private static IntPtr CreateMetalBackedView(CGRect frame, bool fillsSuperview)
        {
            IntPtr cls = EnsureMetalViewClass();
            if (cls == IntPtr.Zero) return IntPtr.Zero;

            IntPtr view = SendRectArg(Send(cls, Sel("alloc")), Sel("initWithFrame:"), frame);
            if (view == IntPtr.Zero) return IntPtr.Zero;

            SendVoidBool(view, Sel("setOpaque:"), true);

            // Follow the superview's size. Without this the view keeps the frame it was created
            // with, so rotating the device (or entering split view) left WPF rendering into the old
            // portrait rectangle while the screen was landscape. UIViewAutoresizing.FlexibleWidth
            // (1<<1) | FlexibleHeight (1<<4); CALayer.autoresizingMask is macOS-only, but the VIEW
            // mask exists on iOS and UIKit keeps a backing layer in step with its view.
            // Only the app's own window: a floating one keeps the frame WPF gives it.
            if (fillsSuperview) SendVoidUIntPtr(view, Sel("setAutoresizingMask:"), (UIntPtr)(2 | 16));
            AddScrollRecognizer(view);
            return view;
        }

        /// <summary>The shadow round a floating window, as Windows draws one round a top-level window.</summary>
        private static void AddShadow(IntPtr view)
        {
            IntPtr layer = Send(view, Sel("layer"));
            if (layer == IntPtr.Zero) return;
            SendVoidBool(layer, Sel("setMasksToBounds:"), false);
            IntPtr black = Send(Send(objc_getClass("UIColor"), Sel("blackColor")), Sel("CGColor"));
            SendVoidPtr(layer, Sel("setShadowColor:"), black);
            SendVoidFloat(layer, Sel("setShadowOpacity:"), 0.45f);
            SendVoidDouble(layer, Sel("setShadowRadius:"), 20);
            SendVoidSize(layer, Sel("setShadowOffset:"), new CGSize { width = 0, height = 8 });
            UpdateShadowPath(view);
        }

        /// <summary>The shadow's outline, which saves Core Animation working it out of the layer's
        /// pixels every frame; it follows the window's size.</summary>
        private static void UpdateShadowPath(IntPtr view)
        {
            IntPtr layer = Send(view, Sel("layer"));
            if (layer == IntPtr.Zero) return;
            CGRect b = SendRect(view, Sel("bounds"));
            IntPtr path = CGPathCreateWithRect(b, IntPtr.Zero);
            SendVoidPtr(layer, Sel("setShadowPath:"), path);
            CGPathRelease(path);
        }

        // ---- The touch-only UIView class, for popups ------------------------------
        //
        // A WPF Popup on iOS is a borderless sibling view inside the same UIWindow, and its PIXELS
        // now come from the owner's surface: the compositor draws the popup's scene into the window
        // it belongs to, translated to the popup's position. That is what gives a Popup its rounded
        // corners and drop shadow -- presenting it through its own opaque swap chain (iOS has no
        // layered-window path; SupportsLayeredWindows is false off Windows) painted the transparent
        // area around that chrome as solid black.
        //
        // The view still has to exist, because WPF needs the popup to be a real window for INPUT:
        // mouse capture, hit testing and every message route key off its handle, and a touch landing
        // on the owner's view would be delivered with the OWNER's handle and ignored by the popup.
        // So this is the same synthesised class as WpfMetalView minus the one thing it does not
        // need: +layerClass. Without that override the backing layer is a plain CALayer, the view
        // draws nothing, and (being non-opaque with no background colour) it is invisible.

        private static IntPtr s_touchViewClass;

        private static IntPtr CreateTouchOnlyView(CGRect frame)
        {
            IntPtr cls = EnsureTouchViewClass();
            if (cls == IntPtr.Zero) return IntPtr.Zero;

            IntPtr view = SendRectArg(Send(cls, Sel("alloc")), Sel("initWithFrame:"), frame);
            if (view == IntPtr.Zero) return IntPtr.Zero;

            // Transparent, and NO autoresizing mask: unlike the fullscreen window view, a popup keeps
            // the frame WPF gives it (SetContentSizePixels / SetFrameOrigin) and must not track the
            // superview's size.
            SendVoidBool(view, Sel("setOpaque:"), false);
            AddScrollRecognizer(view);
            return view;
        }

        private static IntPtr EnsureTouchViewClass()
        {
            if (s_touchViewClass != IntPtr.Zero) return s_touchViewClass;

            // Re-registering an existing class pair aborts the process, so look it up first.
            IntPtr existing = objc_getClass("WpfTouchView");
            if (existing != IntPtr.Zero) return s_touchViewClass = existing;

            IntPtr uiView = objc_getClass("UIView");
            if (uiView == IntPtr.Zero) return IntPtr.Zero;    // not a UIKit process

            IntPtr cls = objc_allocateClassPair(uiView, "WpfTouchView", UIntPtr.Zero);
            if (cls == IntPtr.Zero) return IntPtr.Zero;

            AddTouchMethods(cls);
            // UITextInput: what makes the view able to become first responder (so the keyboard
            // appears at all) and able to host an inline composition. Must precede registerClassPair.
            UIKitTextInput.AddTextInputMethods(cls);
            // UIAccessibility: makes the view a container VoiceOver can walk. Also before
            // registerClassPair, for the same reason.
            UIKitAccessibility.AddViewAccessibility(cls);
            class_addMethod(cls, Sel("wpfScroll:"),
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, void>)&ScrollImp, "v@:@");

            objc_registerClassPair(cls);
            return s_touchViewClass = cls;
        }

        /// <summary>
        /// Lets a TRACKPAD (or mouse wheel) scroll the content. Indirect pointer scrolling is not
        /// delivered as touches at all: iOS routes it only to a UIPanGestureRecognizer that opts in
        /// through allowedScrollTypesMask, so a plain UIView never hears about it and the gesture
        /// appears to do nothing. maximumNumberOfTouches = 0 keeps this recognizer off real fingers,
        /// which touchesBegan/Moved already handle as drag-to-scroll.
        /// </summary>
        private static void AddScrollRecognizer(IntPtr view)
        {
            IntPtr cls = objc_getClass("UIPanGestureRecognizer");
            if (cls == IntPtr.Zero) return;

            IntPtr pan = SendPtrPtr(Send(cls, Sel("alloc")), Sel("initWithTarget:action:"), view, Sel("wpfScroll:"));
            if (pan == IntPtr.Zero) return;

            SendVoidUIntPtr(pan, Sel("setMaximumNumberOfTouches:"), UIntPtr.Zero);
            SendVoidUIntPtr(pan, Sel("setAllowedScrollTypesMask:"), (UIntPtr)3);   // discrete | continuous
            SendVoidPtr(view, Sel("addGestureRecognizer:"), pan);
        }

        // Cumulative translation already turned into wheel, so each callback only sends the delta.
        private static double s_scrollLastTranslationY;

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void ScrollImp(IntPtr self, IntPtr sel, IntPtr recognizer)
        {
            // Never let a managed exception unwind into UIKit.
            try
            {
                const long UIGestureRecognizerStateBegan = 1;
                long state = (long)Send(recognizer, Sel("state"));
                if (state == UIGestureRecognizerStateBegan)
                {
                    s_scrollLastTranslationY = 0;
                    s_panAccum = 0;
                }

                CGPoint t = SendPointPtr(recognizer, Sel("translationInView:"), self);
                double deltaLogical = t.y - s_scrollLastTranslationY;   // translation is in POINTS
                s_scrollLastTranslationY = t.y;
                if (deltaLogical == 0) return;

                // Put the mouse under the pointer first: a wheel report carries no position of its
                // own, so WPF would otherwise scroll whatever was last touched rather than what the
                // pointer is over.
                Action<TouchMessage> handler = MouseInput;
                if (handler == null) return;

                CGPoint p = SendPointPtr(recognizer, Sel("locationInView:"), self);
                double scale = ScreenScale();
                double x = p.x * scale, y = p.y * scale;
                // queued, as a touch is (PostInput)
                PostInput(() =>
                {
                    if (!s_byView.ContainsKey(self)) return;   // gone since: its view is released
                    MouseInput?.Invoke(new TouchMessage(HandleOf(self), 0, (int)Math.Round(x), (int)Math.Round(y), Environment.TickCount));
                    EmitWheel(self, deltaLogical, x, y);
                });
            }
            catch (Exception e)
            {
                Console.WriteLine($"WPF iOS scroll dispatch failed: {e}");
            }
        }

        private static IntPtr EnsureMetalViewClass()
        {
            if (s_metalViewClass != IntPtr.Zero) return s_metalViewClass;

            // Re-registering an existing class pair aborts the process, so look it up first.
            IntPtr existing = objc_getClass("WpfMetalView");
            if (existing != IntPtr.Zero) return s_metalViewClass = existing;

            IntPtr uiView = objc_getClass("UIView");
            if (uiView == IntPtr.Zero) return IntPtr.Zero;    // not a UIKit process

            IntPtr cls = objc_allocateClassPair(uiView, "WpfMetalView", UIntPtr.Zero);
            if (cls == IntPtr.Zero) return IntPtr.Zero;

            // +layerClass is a CLASS method, so it is added to the METACLASS. The IMP must be a
            // static [UnmanagedCallersOnly] function pointer: iOS is aot-only and cannot build a
            // native->managed wrapper for a delegate at run time.
            class_addMethod(object_getClass(cls), Sel("layerClass"),
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr>)&LayerClassImp,
                "#@:");   // returns Class; (id self, SEL _cmd)

            AddTouchMethods(cls);
            // UITextInput: what makes the view able to become first responder (so the keyboard
            // appears at all) and able to host an inline composition. Must precede registerClassPair.
            UIKitTextInput.AddTextInputMethods(cls);
            // UIAccessibility: makes the view a container VoiceOver can walk. Also before
            // registerClassPair, for the same reason.
            UIKitAccessibility.AddViewAccessibility(cls);

            // The action the scroll recognizer targets back at the view (see AddScrollRecognizer).
            class_addMethod(cls, Sel("wpfScroll:"),
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, void>)&ScrollImp, "v@:@");

            // UIKit calls layoutSubviews whenever the view's bounds change - rotation, split view,
            // the keyboard - which is the one place that reliably knows the new size. Raising
            // Resized from here drives the synthetic WM_SIZE -> OnResize -> UpdateWindowSettings
            // that relayouts WPF and reconfigures the swap chain to the new device pixel size.
            class_addMethod(cls, Sel("layoutSubviews"),
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void>)&LayoutSubviewsImp, "v@:");

            objc_registerClassPair(cls);
            return s_metalViewClass = cls;
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static IntPtr LayerClassImp(IntPtr self, IntPtr sel) => objc_getClass("CAMetalLayer");

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void LayoutSubviewsImp(IntPtr self, IntPtr sel)
        {
            // No objc_msgSendSuper: the autoresizing mask is applied by UIKit BEFORE layoutSubviews
            // runs and this view has no constraints or subviews of its own, so there is nothing the
            // base implementation needs to do. Never let a managed exception unwind into UIKit.
            try
            {
                if (s_byView.TryGetValue(self, out UIKitWindow w))
                    w.RaiseResized();
            }
            catch (Exception e)
            {
                Console.WriteLine($"WPF iOS layoutSubviews failed: {e}");
            }
        }

        // ---- Touch input ----------------------------------------------------------
        //
        // A single touch drives WPF's mouse: iOS has no hover, so a tap is "move to the point, then
        // press" - the move MUST precede the press or WPF button-downs land wherever the previous
        // touch ended (the mouse position is only updated by a move). Multi-touch is out of scope
        // here; the first touch wins, as it does for a mouse.

        /// <param name="Kind">0 = move, 1 = press, 2 = release, 3 = wheel (drag-to-scroll; see
        /// <see cref="DispatchTouch"/>), 5/6 = the right button's press/release (press and hold). <paramref name="Wheel"/> is set only for kind 3.</param>
        public readonly record struct TouchMessage(IntPtr View, int Kind, int X, int Y, int TimestampMs, int Wheel = 0);

        /// <summary>Raised on the UI thread as UIKit delivers touches; consumed by HwndMouseInputProvider.</summary>
        public static event Action<TouchMessage> MouseInput;

        /// <summary>
        /// Deliver a mouse event as if the platform had reported it. See CocoaWindow.InjectMouse
        /// for why the visual-tree inspector goes in here rather than raising routed events:
        /// GetPosition and CaptureMouse read the MouseDevice, not the event.
        /// </summary>
        /// <param name="kind">0 move, 1 down, 2 up, 3 wheel (drag-to-scroll).</param>
        internal static void InjectMouse(IntPtr view, int kind, int x, int y, int wheel)
        {
            MouseInput?.Invoke(new TouchMessage(HandleOf(view), kind, x, y, Environment.TickCount, wheel));
        }

        /// <summary>
        /// Test seam: raises a touch as if UIKit had delivered it, in device pixels relative to the
        /// view. Neither simctl nor the simulator offers any touch-injection API, so this is the only
        /// way to exercise the input path (mapping, hit test, click synthesis) automatically. It does
        /// NOT cover UIKit's own delivery into touchesBegan: - a physical tap is still the only proof
        /// of that segment.
        /// </summary>
        public static void InjectTouch(IntPtr view, int kind, int xPixels, int yPixels)
        {
            // To the window on top at that point, as UIKit hit-tests a real touch -- a dialog over the
            // window the point was given in -- and that window keeps the touch until it lifts.
            UIKitWindow given = FromHandle(view);
            if (given != null)
            {
                given.GetClientScreenOriginPixels(out int gx, out int gy);
                int sx = gx + xPixels, sy = gy + yPixels;
                UIKitWindow target = kind == 1 || s_injectTarget == null ? FromHandle(HitTest(sx, sy)) ?? given : s_injectTarget;
                if (kind == 1) s_injectTarget = target;
                else if (kind == 2) s_injectTarget = null;
                target.GetClientScreenOriginPixels(out int tx, out int ty);
                view = target._view;
                xPixels = sx - tx;
                yPixels = sy - ty;
            }
            Emit(ViewOf(view), kind, xPixels, yPixels, ScreenScale());
        }

        private static UIKitWindow s_injectTarget;   // the window an injected press went to, until its release

        private static void AddTouchMethods(IntPtr cls)
        {
            class_addMethod(cls, Sel("touchesBegan:withEvent:"),
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, void>)&TouchesBeganImp, "v@:@@");
            class_addMethod(cls, Sel("touchesMoved:withEvent:"),
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, void>)&TouchesMovedImp, "v@:@@");
            class_addMethod(cls, Sel("touchesEnded:withEvent:"),
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, void>)&TouchesEndedImp, "v@:@@");
            // Cancelled is NOT ended. The mouse emulation treats both as a release, but a contact
            // must be abandoned rather than completed: no tap, and no manipulation finished.
            class_addMethod(cls, Sel("touchesCancelled:withEvent:"),
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, void>)&TouchesCancelledImp, "v@:@@");
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void TouchesBeganImp(IntPtr self, IntPtr sel, IntPtr touches, IntPtr evt)
        {
            // Position first, then the press - see the note above.
            DispatchTouch(self, touches, 0);
            DispatchTouch(self, touches, 1);
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void TouchesMovedImp(IntPtr self, IntPtr sel, IntPtr touches, IntPtr evt)
            => DispatchTouch(self, touches, 0);

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void TouchesEndedImp(IntPtr self, IntPtr sel, IntPtr touches, IntPtr evt)
            => DispatchTouch(self, touches, 2);

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void TouchesCancelledImp(IntPtr self, IntPtr sel, IntPtr touches, IntPtr evt)
            => DispatchTouch(self, touches, 3);

        // ---- Drag-to-scroll ----
        //
        // A touch drives the MOUSE, and WPF scrolls a ScrollViewer from the wheel or the scrollbar --
        // never from a mouse drag over content. So without this a finger drag moves the cursor and
        // nothing scrolls, which on a touch screen reads as "scrolling is broken". (WPF's own touch
        // panning is not an option here: ScrollViewer.PanningMode defaults to None, so it would need
        // both a real TouchDevice and every ScrollViewer opting in.)
        //
        // Instead the drag itself becomes wheel rotation, the input WPF already scrolls from. The
        // press is still delivered up front so taps keep working; a drag that starts on a control
        // simply never produces a click, because that control captured the mouse and the release
        // lands elsewhere -- which is also what a mouse drag does.
        private static double s_panLastY;         // last reported touch Y, device px
        private static double s_panStartY;        // where this touch went down, device px
        private static double s_panAccum;         // drag not yet turned into a notch, logical px
        private static bool s_panning;            // past the threshold: emitting wheel

        // Below this the touch is a tap, not a pan, so a slightly unsteady finger cannot scroll.
        private const double PanThresholdPixels = 10;

        // ScrollViewer ignores the wheel MAGNITUDE -- any wheel event scrolls
        // SystemParameters.WheelScrollLines (3) lines of 16px. So distance is converted into NOTCHES
        // (a wheel event each time the finger covers another 48 logical px) rather than into a bigger
        // delta, which is what makes the content track the finger instead of flying off at 2.4x.
        private const double LogicalPixelsPerNotch = 3 * 16;
        private const int WheelNotch = 120;

        // One notch per touch event is all that lands: WPF computes each scroll from the last APPLIED
        // VerticalOffset, so several wheels inside one event collapse into one. Carry at most a notch
        // of unspent drag so a fast flick catches up over the next events instead of drifting forever.
        private const double MaxCarryPixels = 2 * LogicalPixelsPerNotch;

        // ---- Contacts ---------------------------------------------------------------------------
        //
        // A UITouch has no numeric identifier: UIKit identifies a contact by the IDENTITY of the
        // UITouch object, which it keeps alive and reuses for the whole life of that finger. The
        // seam wants a small integer, so the object pointer is mapped to one for as long as the
        // contact lasts and the entry is dropped when it lifts -- which is also what makes a pointer
        // UIKit later recycles harmless.
        private static readonly Dictionary<IntPtr, int> s_contactIds = new();
        private static int s_nextContactId;

        /// <summary>The contacts of a touch event, read while UIKit's UITouch objects are valid (inside its
        /// callback), as the delivery to the touch sink that is run later (see <see cref="PostInput"/>).</summary>
        private static Action ReportContacts(IntPtr view, IntPtr touches, int kind, double scale)
        {
            if (PlatformTouch.Sink is null) return null;

            IntPtr all = Send(touches, Sel("allObjects"));
            if (all == IntPtr.Zero) return null;
            var contacts = new List<(int Id, int X, int Y, PenState Pen)>();

            nint count = SendNInt(all, Sel("count"));
            for (nint i = 0; i < count; i++)
            {
                IntPtr touch = SendPtrNInt(all, Sel("objectAtIndex:"), i);
                if (touch == IntPtr.Zero) continue;

                if (!TryContactId(touch, kind, out int id)) continue;

                CGPoint p = SendPointPtr(touch, Sel("locationInView:"), view);
                int x = (int)Math.Round(p.x * scale);
                int y = (int)Math.Round(p.y * scale);

                // UITouchType: 0 direct (a finger), 1 indirect, 2 pencil, 3 indirect pointer. Only a
                // pencil measures force; a finger's is a constant that means nothing, so it is
                // reported as unknown rather than handed on as though it were a reading.
                bool isPencil = SendNInt(touch, Sel("type")) == 2;
                PenState pen = PenState.None;
                if (isPencil)
                {
                    double max = SendDouble(touch, Sel("maximumPossibleForce"));
                    double pressure = max > 0 ? SendDouble(touch, Sel("force")) / max : -1;

                    // UIKit gives the tip's ALTITUDE from the screen (0 = flat, pi/2 = upright) and
                    // its AZIMUTH around the screen; the seam takes the two per-axis tilts the
                    // browser and Windows both use, so the spherical pair is resolved into them.
                    double altitude = SendDouble(touch, Sel("altitudeAngle"));
                    double azimuth = SendDoublePtr(touch, Sel("azimuthAngleInView:"), view);
                    double fromVertical = (Math.PI / 2) - altitude;
                    double degrees = fromVertical * 180.0 / Math.PI;

                    pen = new PenState(pressure,
                                       degrees * Math.Cos(azimuth),
                                       degrees * Math.Sin(azimuth));
                }

                contacts.Add((id, x, y, pen));
            }
            if (contacts.Count == 0) return null;
            return () =>
            {
                IPlatformTouchSink sink = PlatformTouch.Sink;
                if (sink is null) return;
                uint timestamp = (uint)Environment.TickCount;
                IntPtr window = HandleOf(view);
                foreach (var (id, x, y, pen) in contacts)
                {
                    switch (kind)
                    {
                        case 1: sink.TouchDown(window, id, x, y, pen, timestamp); break;
                        case 0: sink.TouchMove(window, id, x, y, pen, timestamp); break;
                        case 2: sink.TouchUp(window, id, x, y, timestamp); break;
                        case 3: sink.TouchCancel(window, id); break;
                    }
                }
            };
        }

        /// <summary>The contact id for a UITouch, allocating on a down and releasing on an up.</summary>
        private static bool TryContactId(IntPtr touch, int kind, out int id)
        {
            if (kind == 1)
            {
                id = s_contactIds[touch] = ++s_nextContactId;
                return true;
            }

            if (!s_contactIds.TryGetValue(touch, out id)) return false;   // never saw it go down
            if (kind == 2 || kind == 3) s_contactIds.Remove(touch);
            return true;
        }

        // ---- Input is queued, as Windows queues it ----
        //
        // UIKit calls touchesBegan/Ended from inside its event dispatch, and what a touch sets off -- a
        // click whose handler shows a modal dialog -- ran right there, its modal loop nested in UIKit's
        // callout. One such level was survived; a dialog opened from a dialog (Security Login's Change
        // Password) was two callouts deep, and UIKit delivered it no touch at all: it could not be
        // pressed, moved or closed, while its caret went on blinking. On Windows the input sits in the
        // message queue and the app's own loop dispatches it, never inside the system's delivery. So
        // here: the callout reads what the touch says and returns, and the touch is delivered when one of
        // our loops gets control back (WaitForEvents, RunLoop) or, failing that, from a run-loop source
        // of its own.

        private static readonly System.Collections.Concurrent.ConcurrentQueue<Action> s_input = new();
        private static IntPtr s_inputSource, s_mainRunLoop;

        private static void PostInput(Action deliver)
        {
            s_input.Enqueue(deliver);
            if (IsMainThread) EnsureInputSource();   // made on the main thread; signalled from any
            if (s_inputSource != IntPtr.Zero)
            {
                CFRunLoopSourceSignal(s_inputSource);
                CFRunLoopWakeUp(s_mainRunLoop);
            }
        }

        /// <summary>Delivers the input UIKit queued (main thread). Each delivery checks that its window is
        /// still there: one queued behind a tap that closed it would message a released view. Not re-entrant: input delivered from a
        /// modal loop that a delivery started is delivered by that loop, in order.</summary>
        internal static void DrainInput()
        {
            if (!IsMainThread) return;
            while (s_input.TryDequeue(out Action next))
            {
                try { next(); }
                catch (Exception e) { Console.WriteLine($"WPF iOS input delivery failed: {e}"); }
            }
        }

        private static void EnsureInputSource()
        {
            if (s_inputSource != IntPtr.Zero || !IsMainThread) return;
            s_mainRunLoop = CFRunLoopGetCurrent();
            var context = new CFRunLoopSourceContext
            {
                perform = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, void>)&InputSourcePerform,
            };
            s_inputSource = CFRunLoopSourceCreate(IntPtr.Zero, 0, ref context);
            if (s_inputSource == IntPtr.Zero) return;
            // the default mode by name; the common-modes set is matched by the exported constant only
            // (DispatcherRunLoop.EnsureRunLoopSource), which the default mode's loops are enough for here
            if (s_defaultMode == IntPtr.Zero)
                s_defaultMode = CFStringCreateWithCString(IntPtr.Zero, "kCFRunLoopDefaultMode", 0x08000100 /* UTF-8 */);
            CFRunLoopAddSource(s_mainRunLoop, s_inputSource, s_defaultMode);
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void InputSourcePerform(IntPtr info)
        {
            try { DrainInput(); }
            catch { }
        }

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

        // Press and hold: the right button (TouchHold). Its right press and release go to MouseInput as
        // they are; the left button's kinds through Emit, as every touch did.
        private static readonly TouchHold s_hold = new TouchHold(EmitHeld, PostInput, v => s_byView.ContainsKey(v));

        private static void Gesture(IntPtr view, int kind, double x, double y, double scale) => s_hold.Touch(view, kind, x, y, scale);

        private static void EmitHeld(IntPtr view, int kind, double x, double y)
        {
            if (kind is RightPress or RightRelease)
            {
                if (s_trace && kind == RightPress) Console.WriteLine($"[uikit] press and hold at ({x:F0},{y:F0}): right click");
                MouseInput?.Invoke(new TouchMessage(HandleOf(view), kind, (int)Math.Round(x), (int)Math.Round(y), Environment.TickCount));
                return;
            }
            Emit(view, kind, x, y, ScreenScale());
        }

        /// <summary>TouchMessage kinds of the right button (a press held still, TouchHold).</summary>
        public const int RightPress = TouchHold.RightPress, RightRelease = TouchHold.RightRelease;

        private static void DispatchTouch(IntPtr view, IntPtr touches, int kind)
        {
            // Never let a managed exception unwind into UIKit.
            try
            {
                Action<TouchMessage> handler = MouseInput;
                if (handler == null || touches == IntPtr.Zero) return;

                IntPtr touch = Send(touches, Sel("anyObject"));
                if (touch == IntPtr.Zero) return;

                CGPoint p = SendPointPtr(touch, Sel("locationInView:"), view);
                double scale = ScreenScale();

                // Every touch, not just the one the mouse follows. The mouse emulation stays: a
                // TouchDevice raises the Touch events and drives Manipulation but does not promote
                // itself to the mouse, so contacts alone would gain pinch and lose Button.Click.
                Action contacts = ReportContacts(view, touches, kind, scale);

                // The mouse has no notion of a cancelled press, so a cancel still releases it.
                int mouseKind = kind == 3 ? 2 : kind;
                PostInput(() =>
                {
                    // the window may have gone since (a tap on its close box): its view is released
                    if (!s_byView.ContainsKey(view)) return;
                    Gesture(view, mouseKind, p.x * scale, p.y * scale, scale);
                    contacts?.Invoke();
                });
            }
            catch (Exception e)
            {
                Console.WriteLine($"WPF iOS touch dispatch failed: {e}");
            }
        }

        /// <summary>
        /// Raises one touch as the mouse message(s) it stands for: the move/press/release itself, plus
        /// the synthesized wheel that makes a drag scroll. Both UIKit delivery and
        /// <see cref="InjectTouch"/> come through here, so the test seam exercises the real path.
        /// </summary>
        private static void Emit(IntPtr view, int kind, double x, double y, double scale)
        {
            Action<TouchMessage> handler = MouseInput;
            if (handler == null) return;

            // Where on the screen (the root view's points), which a move or size loop follows: the
            // view itself moves under the finger.
            CGRect at = SendRect(view, Sel("frame"));
            double rx = at.x + x / scale, ry = at.y + y / scale;
            s_cursorX = (int)Math.Round(rx * scale); s_cursorY = (int)Math.Round(ry * scale); s_cursorKnown = true;
            if (s_drag != null)
            {
                if (kind == 0) DragTo(rx, ry);
                else if (kind == 2) { DragTo(rx, ry); s_drag = null; s_pressed = false; }
                return;
            }
            if (kind == 1)
            {
                s_pressX = rx; s_pressY = ry; s_pressed = true;
                // a press gives a window the keys, and a child window that takes them (an ElementHost's
                // WPF tree) -- never a popup (a menu, a tooltip), as on Windows
                if (s_byView.TryGetValue(view, out UIKitWindow pressed) && (!pressed.IsBorderless || pressed._ownSurface || pressed.TakesKeyboardOnPress))
                    s_keyWindow = view;
                TakeKeys();
                if (pressed != null) SetActive(pressed);   // a popup or a child leaves it (SetActive)
            }
            else if (kind == 2) s_pressed = false;

            int timestamp = Environment.TickCount;
            handler(new TouchMessage(HandleOf(view), kind, (int)Math.Round(x), (int)Math.Round(y), timestamp));

            switch (kind)
            {
                case 1:   // press: this touch has not panned yet
                    s_panStartY = s_panLastY = y;
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
                        if (Math.Abs(y - s_panStartY) < PanThresholdPixels) return;
                        s_panning = true;     // threshold crossed; scroll from here on
                        s_panLastY = y;
                    }

                    // Finger down moves the content down, which is a scroll UP (positive wheel).
                    double deltaLogical = (y - s_panLastY) / scale;
                    s_panLastY = y;
                    EmitWheel(view, deltaLogical, x, y);
                    return;
            }
        }

        /// <summary>
        /// Turns scrolled DISTANCE into wheel notches and raises them. Shared by the finger drag and
        /// the trackpad, which differ only in where the distance comes from.
        /// </summary>
        private static void EmitWheel(IntPtr view, double deltaLogical, double x = 0, double y = 0)
        {
            Action<TouchMessage> handler = MouseInput;
            if (handler == null) return;

            s_panAccum += deltaLogical;
            if (Math.Abs(s_panAccum) < LogicalPixelsPerNotch) return;   // not a notch yet

            int sign = Math.Sign(s_panAccum);
            s_panAccum -= sign * LogicalPixelsPerNotch;
            if (Math.Abs(s_panAccum) > MaxCarryPixels) s_panAccum = sign * MaxCarryPixels;

            handler(new TouchMessage(HandleOf(view), 3, (int)Math.Round(x), (int)Math.Round(y),
                                     Environment.TickCount, sign * WheelNotch));
        }

        // ---- The hardware keyboard -------------------------------------------------
        //
        // UIKit hands a hardware keyboard's keys (pressesBegan:/pressesEnded:) to the first responder, and
        // a view becomes one only by asking -- the WPF view does when a text box takes the focus
        // (UIKitTextInput), and UIKit then shows the soft keyboard, which a press anywhere else must not.
        // So a hidden key view of the screen holds first responder the rest of the time, and turns each
        // press into a key message for the window that has the keyboard: the one last pressed or
        // activated, as Windows gives the keys to the active window and SetFocus to a child window that
        // was clicked. Its keys are Windows' virtual keys.

        /// <summary>A key of the hardware keyboard, down or up, for <see cref="View"/>'s window.</summary>
        public readonly record struct UIKitKeyMessage(IntPtr View, bool IsDown, int VirtualKey, string Characters,
                                                     bool Shift, bool Ctrl, bool Alt, bool Meta, int TimestampMs);

        /// <summary>Raised on the main thread for each hardware key; consumed by the keyboard input providers.</summary>
        public static event Action<UIKitKeyMessage> KeyInput;

        private static IntPtr s_keyView;
        private static IntPtr s_keyWindow;   // the window the keys go to

        /// <summary>The window that has the keyboard (pressed or activated last), by handle.</summary>
        public static IntPtr KeyboardWindow => HandleOf(s_keyWindow);

        /// <summary>The keyboard back to the key view, once no text box is editing (UIKitTextInput).</summary>
        internal static void TakeKeys()
        {
            if (!IsMainThread || RootView == IntPtr.Zero) return;
            if (s_keyView == IntPtr.Zero)
            {
                IntPtr cls = EnsureKeyViewClass();
                if (cls == IntPtr.Zero) return;
                s_keyView = SendRectArg(Send(cls, Sel("alloc")), Sel("initWithFrame:"), new CGRect { width = 1, height = 1 });
                if (s_keyView == IntPtr.Zero) return;
                SendVoidBool(s_keyView, Sel("setHidden:"), true);
                SendVoidPtr(RootView, Sel("addSubview:"), s_keyView);
            }
            if (!UIKitTextInput.IsEnabled) Send(s_keyView, Sel("becomeFirstResponder"));
        }

        private static IntPtr EnsureKeyViewClass()
        {
            IntPtr existing = objc_getClass("WpfKeyView");
            if (existing != IntPtr.Zero) return existing;
            IntPtr uiView = objc_getClass("UIView");
            if (uiView == IntPtr.Zero) return IntPtr.Zero;
            IntPtr cls = objc_allocateClassPair(uiView, "WpfKeyView", UIntPtr.Zero);
            if (cls == IntPtr.Zero) return IntPtr.Zero;
            class_addMethod(cls, Sel("canBecomeFirstResponder"),
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, byte>)&CanBecomeFirstResponderImp, "c@:");
            class_addMethod(cls, Sel("pressesBegan:withEvent:"),
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, void>)&PressesBeganImp, "v@:@@");
            class_addMethod(cls, Sel("pressesEnded:withEvent:"),
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, void>)&PressesEndedImp, "v@:@@");
            class_addMethod(cls, Sel("pressesCancelled:withEvent:"),
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, void>)&PressesEndedImp, "v@:@@");
            objc_registerClassPair(cls);
            return cls;
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static byte CanBecomeFirstResponderImp(IntPtr self, IntPtr sel) => 1;

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void PressesBeganImp(IntPtr self, IntPtr sel, IntPtr presses, IntPtr evt) => DispatchPresses(presses, true);

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void PressesEndedImp(IntPtr self, IntPtr sel, IntPtr presses, IntPtr evt) => DispatchPresses(presses, false);

        // UIKeyModifierFlags
        private const long UIKeyModifierShift = 1 << 17, UIKeyModifierControl = 1 << 18, UIKeyModifierAlternate = 1 << 19, UIKeyModifierCommand = 1 << 20;

        private static void DispatchPresses(IntPtr presses, bool down)
        {
            // Never let a managed exception unwind into UIKit.
            try
            {
                Action<UIKitKeyMessage> handler = KeyInput;
                if (handler == null || presses == IntPtr.Zero) return;
                IntPtr all = Send(presses, Sel("allObjects"));
                nint count = all == IntPtr.Zero ? 0 : SendNInt(all, Sel("count"));
                for (nint i = 0; i < count; i++)
                {
                    IntPtr key = Send(SendPtrNInt(all, Sel("objectAtIndex:"), i), Sel("key"));   // UIKey, nil for a remote's press
                    if (key == IntPtr.Zero) continue;
                    int vk = VirtualKeyOfHidUsage((int)SendNInt(key, Sel("keyCode")));
                    long mods = SendNInt(key, Sel("modifierFlags"));
                    string chars = NSStringToString(Send(key, Sel("characters")));
                    if (s_trace) Console.WriteLine($"[uikit] key {(down ? "down" : "up")} vk=0x{vk:x} -> 0x{s_keyWindow:x}");
                    // queued, as a touch is (PostInput): a key can open a dialog too
                    var message = new UIKitKeyMessage(HandleOf(s_keyWindow), down, vk, chars,
                        (mods & UIKeyModifierShift) != 0, (mods & UIKeyModifierControl) != 0,
                        (mods & UIKeyModifierAlternate) != 0, (mods & UIKeyModifierCommand) != 0, Environment.TickCount);
                    PostInput(() => KeyInput?.Invoke(message));
                }
            }
            catch (Exception e)
            {
                Console.WriteLine($"WPF iOS key dispatch failed: {e}");
            }
        }

        /// <summary>
        /// Test seam: a hardware key as if UIKit had pressed it, for the window that has the keys -- the
        /// route a real press takes from the key view. <paramref name="key"/> is a key's name as a browser's
        /// KeyboardEvent.key gives it ("a", "Enter", "ArrowDown"), which the visual-tree inspector speaks.
        /// </summary>
        public static void InjectKey(bool down, string key, string characters, bool shift, bool ctrl, bool alt, bool meta)
        {
            int vk = key switch
            {
                { Length: 1 } when char.IsAsciiLetter(key[0]) => char.ToUpperInvariant(key[0]),
                { Length: 1 } when char.IsAsciiDigit(key[0]) => key[0],
                " " => 0x20,
                "Enter" => 0x0D, "Tab" => 0x09, "Backspace" => 0x08, "Escape" => 0x1B, "Delete" => 0x2E, "Insert" => 0x2D,
                "Home" => 0x24, "End" => 0x23, "PageUp" => 0x21, "PageDown" => 0x22,
                "ArrowLeft" => 0x25, "ArrowUp" => 0x26, "ArrowRight" => 0x27, "ArrowDown" => 0x28,
                "Shift" => 0x10, "Control" or "Meta" => 0x11, "Alt" => 0x12,
                { Length: >= 2 } when key[0] == 'F' && int.TryParse(key.AsSpan(1), out int f) && f >= 1 && f <= 24 => 0x70 + f - 1,
                _ => 0,
            };
            if (s_trace) Console.WriteLine($"[uikit] key {(down ? "down" : "up")} vk=0x{vk:x} '{characters}' -> 0x{s_keyWindow:x} (injected)");
            KeyInput?.Invoke(new UIKitKeyMessage(HandleOf(s_keyWindow), down, vk, characters, shift, ctrl, alt, meta, Environment.TickCount));
        }

        private static string NSStringToString(IntPtr nsString)
        {
            if (nsString == IntPtr.Zero) return null;
            IntPtr utf8 = Send(nsString, Sel("UTF8String"));
            return utf8 == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(utf8);
        }

        /// <summary>A key's USB HID usage (UIKeyboardHIDUsage) as the Windows virtual key it is.</summary>
        internal static int VirtualKeyOfHidUsage(int usage)
        {
            if (usage >= 0x04 && usage <= 0x1D) return 'A' + (usage - 0x04);          // a..z
            if (usage >= 0x1E && usage <= 0x26) return '1' + (usage - 0x1E);          // 1..9
            if (usage >= 0x3A && usage <= 0x45) return 0x70 + (usage - 0x3A);         // F1..F12
            if (usage >= 0x68 && usage <= 0x73) return 0x7C + (usage - 0x68);         // F13..F24
            if (usage >= 0x59 && usage <= 0x61) return 0x61 + (usage - 0x59);         // keypad 1..9
            return usage switch
            {
                0x27 => '0',
                0x28 => 0x0D, 0x29 => 0x1B, 0x2A => 0x08, 0x2B => 0x09, 0x2C => 0x20,  // Return, Escape, Backspace, Tab, Space
                0x2D => 0xBD, 0x2E => 0xBB, 0x2F => 0xDB, 0x30 => 0xDD, 0x31 => 0xDC,  // - = [ ] \
                0x33 => 0xBA, 0x34 => 0xDE, 0x35 => 0xC0, 0x36 => 0xBC, 0x37 => 0xBE, 0x38 => 0xBF,   // ; ' ` , . /
                0x39 => 0x14,                                                          // Caps Lock
                0x46 => 0x2C, 0x47 => 0x91, 0x48 => 0x13,                              // Print Screen, Scroll Lock, Pause
                0x49 => 0x2D, 0x4A => 0x24, 0x4B => 0x21, 0x4C => 0x2E, 0x4D => 0x23, 0x4E => 0x22,   // Insert, Home, PgUp, Delete, End, PgDn
                0x4F => 0x27, 0x50 => 0x25, 0x51 => 0x28, 0x52 => 0x26,                // arrows: right, left, down, up
                0x53 => 0x90, 0x54 => 0x6F, 0x55 => 0x6A, 0x56 => 0x6D, 0x57 => 0x6B, 0x58 => 0x0D,   // Num Lock, keypad / * - + Enter
                0x62 => 0x60, 0x63 => 0x6E, 0x65 => 0x5D,                              // keypad 0 and ., Menu
                0xE0 or 0xE4 => 0x11, 0xE1 or 0xE5 => 0x10, 0xE2 or 0xE6 => 0x12,      // Control, Shift, Alt (Option)
                0xE3 or 0xE7 => 0x11,                                                  // Command is Control, as on the other Apple head
                _ => 0,
            };
        }

        // ---- The dispatcher's wait ---------------------------------------------------
        //
        // The main thread is UIKit's run loop's, as it is AppKit's on macOS: UIApplicationMain runs it,
        // and the head calls the app's Main from inside it once the scene has connected. The
        // dispatcher's idle wait (Dispatcher.WaitForWork) runs that run loop: touches, keys and layout
        // are delivered from it, Core Animation commits what was presented when it is about to sleep,
        // and posted work ends it through the dispatcher's run-loop source (DispatcherRunLoop), which
        // handling is the "source handled" that returns. A nested frame -- a modal dialog, an Invoke's
        // wait -- is one more turn of the same loop.

        [ThreadStatic] private static bool s_waiting;
        private static IntPtr s_defaultMode;

        /// <summary>Whether the main thread's dispatcher is in its wait (<see cref="WaitForEvents"/>):
        /// posted work then ends the wait rather than running inside it.</summary>
        internal static bool IsWaitingForWork => s_waiting;

        /// <summary>Runs the main run loop until a source has been handled -- an event, posted dispatcher
        /// work -- or <paramref name="timeoutMilliseconds"/> passes (negative: no limit). Main thread.</summary>
        internal static void WaitForEvents(int timeoutMilliseconds)
        {
            if (!IsMainThread) return;
            if (s_defaultMode == IntPtr.Zero)
                s_defaultMode = CFStringCreateWithCString(IntPtr.Zero, "kCFRunLoopDefaultMode", 0x08000100 /* UTF-8 */);
            double seconds = timeoutMilliseconds < 0 ? 1.0e9 : timeoutMilliseconds / 1000.0;
            s_waiting = true;
            int result = 0;
            try { result = CFRunLoopRunInMode(s_defaultMode, seconds, true); }
            finally { s_waiting = false; }
            DrainInput();
            if (s_trace) Console.WriteLine($"[uikit] wait {timeoutMilliseconds} ms ended: {result}");
        }

        /// <summary>Runs the main run loop as another loop's idle wait -- WinForms' message loop -- until a
        /// source has been handled or <paramref name="timeoutMilliseconds"/> passes: UIKit's input and
        /// Core Animation's commits, and the WPF dispatcher's posted work, which its run-loop source runs
        /// (it is not the dispatcher's own wait). Main thread.</summary>
        public static void RunLoop(int timeoutMilliseconds)
        {
            if (!IsMainThread) { System.Threading.Thread.Sleep(Math.Max(0, timeoutMilliseconds)); return; }
            if (s_defaultMode == IntPtr.Zero)
                s_defaultMode = CFStringCreateWithCString(IntPtr.Zero, "kCFRunLoopDefaultMode", 0x08000100 /* UTF-8 */);
            bool wasWaiting = s_waiting;
            s_waiting = false;
            try { CFRunLoopRunInMode(s_defaultMode, Math.Max(0, timeoutMilliseconds) / 1000.0, true); }
            finally { s_waiting = wasWaiting; }
            DrainInput();
        }

        private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
        [DllImport(CoreFoundation)] private static extern int CFRunLoopRunInMode(IntPtr mode, double seconds, [MarshalAs(UnmanagedType.I1)] bool returnAfterSourceHandled);
        [DllImport(CoreFoundation)] private static extern IntPtr CFStringCreateWithCString(IntPtr allocator, string cStr, uint encoding);
        [DllImport(CoreFoundation)] private static extern IntPtr CFRunLoopGetCurrent();
        [DllImport(CoreFoundation)] private static extern IntPtr CFRunLoopSourceCreate(IntPtr allocator, nint order, ref CFRunLoopSourceContext context);
        [DllImport(CoreFoundation)] private static extern void CFRunLoopAddSource(IntPtr runLoop, IntPtr source, IntPtr mode);
        [DllImport(CoreFoundation)] private static extern void CFRunLoopSourceSignal(IntPtr source);
        [DllImport(CoreFoundation)] private static extern void CFRunLoopWakeUp(IntPtr runLoop);

        /// <summary>nativeScale (not scale): the true pixel ratio on phones that render-and-downsample.</summary>
        private static double ScreenScale()
        {
            IntPtr screen = Send(objc_getClass("UIScreen"), Sel("mainScreen"));
            if (screen == IntPtr.Zero) return 1.0;
            double s = SendDouble(screen, Sel("nativeScale"));
            return s > 0 ? s : 1.0;
        }

        // ---- Objective-C runtime --------------------------------------------------

        private const string ObjC = "/usr/lib/libobjc.dylib";

        [StructLayout(LayoutKind.Sequential)]
        private struct CGRect { public double x, y, width, height; }

        [StructLayout(LayoutKind.Sequential)]
        private struct CGPoint { public double x, y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct CGSize { public double width, height; }

        private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
        [DllImport(CoreGraphics)] private static extern IntPtr CGPathCreateWithRect(CGRect rect, IntPtr transform);
        [DllImport(CoreGraphics)] private static extern void CGPathRelease(IntPtr path);

        private static IntPtr Sel(string name) => sel_registerName(name);


        /// <summary>Non-zero on the process's main thread -- the only one UIKit accepts views on.</summary>
        [DllImport("/usr/lib/libSystem.dylib")] private static extern int pthread_main_np();

        /// <summary>
        /// Whether the calling thread is the main thread: iOS has exactly one UI thread, the run loop's
        /// that the Dispatcher waits in. A second WPF UI thread cannot be supported here;
        /// <see cref="Create"/> refuses one with this.
        /// </summary>
        internal static bool IsMainThread => pthread_main_np() != 0;

        [DllImport(ObjC)] private static extern IntPtr objc_getClass(string name);
        [DllImport(ObjC)] private static extern IntPtr sel_registerName(string name);
        [DllImport(ObjC)] private static extern IntPtr objc_allocateClassPair(IntPtr superclass, string name, UIntPtr extraBytes);
        [DllImport(ObjC)] private static extern void objc_registerClassPair(IntPtr cls);
        [DllImport(ObjC)] private static extern IntPtr object_getClass(IntPtr obj);
        [DllImport(ObjC)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool class_addMethod(IntPtr cls, IntPtr sel, IntPtr imp, string types);

        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr receiver, IntPtr selector);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendVoidPtr(IntPtr receiver, IntPtr selector, IntPtr arg);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendVoidPtrPtr(IntPtr receiver, IntPtr selector, IntPtr arg1, IntPtr arg2);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendPtrPtr(IntPtr receiver, IntPtr selector, IntPtr a, IntPtr b);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendVoidBool(IntPtr receiver, IntPtr selector, [MarshalAs(UnmanagedType.I1)] bool arg);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] [return: MarshalAs(UnmanagedType.I1)] private static extern bool SendBool(IntPtr receiver, IntPtr selector);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendVoidFloat(IntPtr receiver, IntPtr selector, float arg);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendVoidDouble(IntPtr receiver, IntPtr selector, double arg);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendVoidSize(IntPtr receiver, IntPtr selector, CGSize arg);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendVoidUIntPtr(IntPtr receiver, IntPtr selector, UIntPtr arg);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern double SendDouble(IntPtr receiver, IntPtr selector);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern nint SendNInt(IntPtr receiver, IntPtr selector);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern double SendDoublePtr(IntPtr receiver, IntPtr selector, IntPtr arg);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendPtrNInt(IntPtr receiver, IntPtr selector, nint arg);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern CGPoint SendPointPtr(IntPtr receiver, IntPtr selector, IntPtr arg);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern CGRect SendRect(IntPtr receiver, IntPtr selector);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendVoidRect(IntPtr receiver, IntPtr selector, CGRect arg);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendRectArg(IntPtr receiver, IntPtr selector, CGRect arg);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern CGRect SendRectRectPtr(IntPtr receiver, IntPtr selector, CGRect r, IntPtr view);
    }
}
