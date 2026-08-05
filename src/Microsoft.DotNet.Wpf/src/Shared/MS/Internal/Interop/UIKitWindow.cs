// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The iOS/UIKit windowing backend — the iOS sibling of CocoaWindow and BrowserWindow.
//
// iOS has ONE fullscreen UIWindow, so a WPF "window" is a UIView inside it, and the UIView* is what
// flows through WPF as the HWND (exactly as CocoaWindow exposes its NSView*). The WebGPU compositor
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
    /// A single WPF window on iOS: a Metal-backed UIView inside the app's one UIWindow. The UIView*
    /// stands in for the HWND. IntPtr.Zero until <see cref="Create"/>.
    /// </summary>
    public sealed unsafe class UIKitWindow : IPlatformWindow
    {
        private static readonly Dictionary<IntPtr, UIKitWindow> s_byView = new Dictionary<IntPtr, UIKitWindow>();
        private static readonly List<UIKitWindow> s_zOrder = new List<UIKitWindow>();   // last = topmost

        private IntPtr _view;      // UIView* (WpfMetalView) — the window handle

        /// <summary>The UIView* that stands in for the HWND.</summary>
        public IntPtr Handle => _view;

        public bool IsBorderless { get; private set; }

        public static IntPtr MouseCaptureHandle { get; set; }

        /// <summary>Raised when the view's content size changed (device pixels).</summary>
        public event Action<int, int> Resized;

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

        public static UIKitWindow FromHandle(IntPtr handle)
            => s_byView.TryGetValue(handle, out UIKitWindow w) ? w : null;

        /// <param name="x">Origin in top-left device PIXELS (borderless popups; the main window fills the screen).</param>
        public void Create(string title, int x, int y, int width, int height, bool borderless)
        {
            IsBorderless = borderless;

            double scale = ScreenScale();
            var frame = new CGRect
            {
                x = x / scale,
                y = y / scale,
                width = width / scale,
                height = height / scale,
            };

            // iOS has no window manager: a top-level window is always the full screen. WPF asks for
            // its own default/desired size (640x400 etc.), which on a phone would leave the app
            // rendering into a small rectangle in a corner. Only real top-level windows are snapped
            // - borderless views are Popups/menus, which DO need their requested geometry.
            if (!borderless && RootView != IntPtr.Zero)
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
            _view = borderless ? CreateTouchOnlyView(frame) : CreateMetalBackedView(frame);
            if (_view == IntPtr.Zero)
                return;

            IntPtr root = RootView;
            if (root != IntPtr.Zero)
                SendVoidPtr(root, Sel("addSubview:"), _view);

            s_byView[_view] = this;
            s_zOrder.Add(this);
        }

        public void Destroy()
        {
            if (_view == IntPtr.Zero)
                return;

            Send(_view, Sel("removeFromSuperview"));
            s_byView.Remove(_view);
            s_zOrder.Remove(this);
            Send(_view, Sel("release"));
            _view = IntPtr.Zero;
        }

        /// <summary>Resize the content area, in POINTS.</summary>
        public void SetContentSize(int width, int height)
        {
            if (_view == IntPtr.Zero) return;
            // Fullscreen top-level: keep OUR frame, but still report the size. RaiseResized drives
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
        /// A top-level (non-Popup) window on iOS is pinned to the whole screen, so WPF's
        /// SetWindowPos calls for it are ignored - GetPixelSize keeps reporting the real bounds and
        /// layout follows those. Popups keep their requested geometry.
        /// </summary>
        private bool IsFullScreenTopLevel => !IsBorderless && RootView != IntPtr.Zero;

        public double GetBackingScale() => ScreenScale();

        /// <summary>Topmost registered window containing the point (top-left device pixels).</summary>
        public static IntPtr HitTest(int x, int y)
        {
            for (int i = s_zOrder.Count - 1; i >= 0; i--)
            {
                UIKitWindow w = s_zOrder[i];
                if (w._view == IntPtr.Zero) continue;

                w.GetClientScreenOriginPixels(out int ox, out int oy);
                w.GetPixelSize(out int pw, out int ph);
                if (x >= ox && x < ox + pw && y >= oy && y < oy + ph)
                    return w._view;
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
            GetPixelSize(out int w, out int h);
            Resized?.Invoke(w, h);
        }

        // ---- The Metal-backed UIView class, synthesised at run time ---------------

        private static IntPtr s_metalViewClass;

        private static IntPtr CreateMetalBackedView(CGRect frame)
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
            SendVoidUIntPtr(view, Sel("setAutoresizingMask:"), (UIntPtr)(2 | 16));
            AddScrollRecognizer(view);
            return view;
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
                handler(new TouchMessage(self, 0, (int)Math.Round(x), (int)Math.Round(y), Environment.TickCount));

                EmitWheel(self, deltaLogical, x, y);
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
        /// <see cref="DispatchTouch"/>). <paramref name="Wheel"/> is set only for kind 3.</param>
        public readonly record struct TouchMessage(IntPtr View, int Kind, int X, int Y, int TimestampMs, int Wheel = 0);

        /// <summary>Raised on the UI thread as UIKit delivers touches; consumed by HwndMouseInputProvider.</summary>
        public static event Action<TouchMessage> MouseInput;

        /// <summary>
        /// Test seam: raises a touch as if UIKit had delivered it, in device pixels relative to the
        /// view. Neither simctl nor the simulator offers any touch-injection API, so this is the only
        /// way to exercise the input path (mapping, hit test, click synthesis) automatically. It does
        /// NOT cover UIKit's own delivery into touchesBegan: - a physical tap is still the only proof
        /// of that segment.
        /// </summary>
        public static void InjectTouch(IntPtr view, int kind, int xPixels, int yPixels)
            => Emit(view, kind, xPixels, yPixels, ScreenScale());

        private static void AddTouchMethods(IntPtr cls)
        {
            class_addMethod(cls, Sel("touchesBegan:withEvent:"),
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, void>)&TouchesBeganImp, "v@:@@");
            class_addMethod(cls, Sel("touchesMoved:withEvent:"),
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, void>)&TouchesMovedImp, "v@:@@");
            class_addMethod(cls, Sel("touchesEnded:withEvent:"),
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, void>)&TouchesEndedImp, "v@:@@");
            class_addMethod(cls, Sel("touchesCancelled:withEvent:"),
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, void>)&TouchesEndedImp, "v@:@@");
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
                Emit(view, kind, p.x * scale, p.y * scale, scale);
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

            int timestamp = Environment.TickCount;
            handler(new TouchMessage(view, kind, (int)Math.Round(x), (int)Math.Round(y), timestamp));

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

            handler(new TouchMessage(view, 3, (int)Math.Round(x), (int)Math.Round(y),
                                     Environment.TickCount, sign * WheelNotch));
        }

        // ---- The CADisplayLink dispatcher pump ------------------------------------
        //
        // UIKit owns the main run loop and UIApplicationMain never returns, so - unlike macOS,
        // where the dispatcher drives the loop itself via CocoaWindow.PumpEvents - WPF here
        // cannot own the loop; it has to be a guest in it. CADisplayLink is the iOS analogue of
        // the browser's requestAnimationFrame: a display-aligned callback issued by the run loop.
        // The tick is delivered on the main thread, which is the dispatcher thread, so the pump
        // body runs synchronously inside it. (The browser pump is an async/await loop only
        // because it must yield to the JS event loop; awaiting here would risk resuming the
        // continuation on a thread-pool thread, and ProcessQueue has hard UI-thread affinity.)

        private static IntPtr s_displayLink;
        private static IntPtr s_linkTarget;   // retained: performSelector wake-ups are sent to it
        private static Action s_tick;

        /// <summary>
        /// Starts (once) a display-linked callback that invokes <paramref name="tick"/> on the
        /// main thread every frame. Returns false when not running under UIKit.
        /// </summary>
        public static bool StartDisplayLink(Action tick)
        {
            if (s_displayLink != IntPtr.Zero) { s_tick = tick; return true; }

            IntPtr cls = objc_getClass("CADisplayLink");
            IntPtr target = CreateDisplayLinkTarget();
            if (cls == IntPtr.Zero || target == IntPtr.Zero) return false;
            s_linkTarget = target;

            IntPtr link = SendPtrPtr(cls, Sel("displayLinkWithTarget:selector:"), target, Sel("wpfTick:"));
            if (link == IntPtr.Zero) return false;

            IntPtr runLoop = Send(objc_getClass("NSRunLoop"), Sel("currentRunLoop"));
            if (runLoop == IntPtr.Zero) return false;


            // The REAL NSRunLoopCommonModes global, read out of Foundation. An NSString built with
            // the same characters is NOT interchangeable here: common mode is a pseudo-mode that
            // CFRunLoop resolves by pointer identity against the constant, so an equal-by-value
            // string is accepted silently and the source is then attached to a mode that never
            // runs - the link is installed, retains nothing, and simply never fires.
            // Common (not default) mode keeps ticking during the tracking loops UIKit enters
            // while a finger is down.
            IntPtr mode = GetForegroundConstant("NSRunLoopCommonModes");
            if (mode == IntPtr.Zero) return false;

            // displayLinkWithTarget: hands back an AUTORELEASED link. Holding it in a static IntPtr
            // is invisible to ARC/the autorelease pool, so retain it explicitly - otherwise the pool
            // drains at the end of this run-loop turn and the link dies before its first fire.
            Send(link, Sel("retain"));
            SendVoidPtrPtr(link, Sel("addToRunLoop:forMode:"), runLoop, mode);

            s_tick = tick;
            s_displayLink = link;
            return true;
        }

        /// <summary>
        /// Pause/resume the display link. A paused link costs nothing, which is how the dispatcher
        /// goes fully idle between changes (the macOS/Win32 loops block in WaitForWork instead);
        /// <see cref="RequestWake"/> and <see cref="ScheduleWake"/> bring it back.
        /// </summary>
        public static void SetDisplayLinkPaused(bool paused)
        {
            if (s_displayLink != IntPtr.Zero)
                SendVoidBool(s_displayLink, Sel("setPaused:"), paused);
        }

        /// <summary>
        /// Resume the pump because work was queued. Callable from ANY thread (Dispatcher.BeginInvoke
        /// off the UI thread is the whole point), so it hops to the main thread rather than touching
        /// the display link directly - CoreAnimation objects are not thread-safe.
        /// </summary>
        public static void RequestWake()
        {
            if (s_linkTarget == IntPtr.Zero) return;
            SendVoidSelPtrBool(s_linkTarget, Sel("performSelectorOnMainThread:withObject:waitUntilDone:"),
                               Sel("wpfWake:"), IntPtr.Zero, false);
        }

        /// <summary>
        /// Resume the pump in <paramref name="seconds"/>, for a pending DispatcherTimer: the link can
        /// still be paused meanwhile, so an app waiting on a timer idles instead of spinning at the
        /// display rate. Main thread only; replaces any previously scheduled wake.
        /// </summary>
        public static void ScheduleWake(double seconds)
        {
            if (s_linkTarget == IntPtr.Zero) return;
            SendVoidPtr(objc_getClass("NSObject"), Sel("cancelPreviousPerformRequestsWithTarget:"), s_linkTarget);
            SendVoidSelPtrDouble(s_linkTarget, Sel("performSelector:withObject:afterDelay:"),
                                 Sel("wpfWake:"), IntPtr.Zero, Math.Max(0.0, seconds));
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void WakeImp(IntPtr self, IntPtr sel, IntPtr arg)
        {
            try { SetDisplayLinkPaused(false); }
            catch (Exception e) { Console.WriteLine($"WPF iOS pump wake failed: {e}"); }
        }

        public static void StopDisplayLink()
        {
            if (s_displayLink == IntPtr.Zero) return;
            Send(s_displayLink, Sel("invalidate"));
            s_displayLink = IntPtr.Zero;
            s_tick = null;
        }

        /// <summary>Reads an NSString* constant (e.g. NSRunLoopCommonModes) out of Foundation.</summary>
        private static IntPtr GetForegroundConstant(string symbol)
        {
            IntPtr foundation = dlopen("/System/Library/Frameworks/Foundation.framework/Foundation", 2 /*RTLD_NOW*/);
            if (foundation == IntPtr.Zero) return IntPtr.Zero;

            IntPtr slot = dlsym(foundation, symbol);   // address OF the variable
            return slot == IntPtr.Zero ? IntPtr.Zero : *(IntPtr*)slot;
        }

        private static IntPtr CreateDisplayLinkTarget()
        {
            IntPtr cls = objc_getClass("WpfDisplayLinkTarget");
            if (cls == IntPtr.Zero)
            {
                IntPtr nsObject = objc_getClass("NSObject");
                if (nsObject == IntPtr.Zero) return IntPtr.Zero;

                cls = objc_allocateClassPair(nsObject, "WpfDisplayLinkTarget", UIntPtr.Zero);
                if (cls == IntPtr.Zero) return IntPtr.Zero;

                // An INSTANCE method this time, so it goes on the class itself (contrast
                // +layerClass above, which goes on the metaclass).
                class_addMethod(cls, Sel("wpfTick:"),
                    (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, void>)&TickImp,
                    "v@:@");   // void; (id self, SEL _cmd, CADisplayLink* sender)
                class_addMethod(cls, Sel("wpfWake:"),
                    (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, void>)&WakeImp,
                    "v@:@");   // void; (id self, SEL _cmd, id arg) - unpauses the link

                objc_registerClassPair(cls);
            }

            return Send(Send(cls, Sel("alloc")), Sel("init"));
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void TickImp(IntPtr self, IntPtr sel, IntPtr sender)
        {
            // A managed exception unwinding into Objective-C terminates the process without a
            // usable stack, so the pump's failures are reported here rather than propagated.
            try
            {
                s_tick?.Invoke();
            }
            catch (Exception e)
            {
                Console.WriteLine($"WPF iOS dispatcher pump failed: {e}");
            }
        }

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

        private static IntPtr Sel(string name) => sel_registerName(name);

        [DllImport("/usr/lib/libSystem.dylib")] private static extern IntPtr dlopen(string path, int mode);
        [DllImport("/usr/lib/libSystem.dylib")] private static extern IntPtr dlsym(IntPtr handle, string symbol);

        [DllImport(ObjC)] private static extern IntPtr objc_getClass(string name);
        [DllImport(ObjC)] private static extern IntPtr sel_registerName(string name);
        [DllImport(ObjC)] private static extern IntPtr objc_allocateClassPair(IntPtr superclass, string name, UIntPtr extraBytes);
        [DllImport(ObjC)] private static extern void objc_registerClassPair(IntPtr cls);
        [DllImport(ObjC)] private static extern IntPtr object_getClass(IntPtr obj);
        [DllImport(ObjC)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool class_addMethod(IntPtr cls, IntPtr sel, IntPtr imp, string types);

        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr receiver, IntPtr selector);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendVoidPtr(IntPtr receiver, IntPtr selector, IntPtr arg);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendPtrPtr(IntPtr receiver, IntPtr selector, IntPtr a, IntPtr b);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendVoidPtrPtr(IntPtr receiver, IntPtr selector, IntPtr a, IntPtr b);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendVoidBool(IntPtr receiver, IntPtr selector, [MarshalAs(UnmanagedType.I1)] bool arg);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendVoidUIntPtr(IntPtr receiver, IntPtr selector, UIntPtr arg);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendVoidSelPtrBool(IntPtr receiver, IntPtr selector, IntPtr sel2, IntPtr arg, [MarshalAs(UnmanagedType.I1)] bool wait);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendVoidSelPtrDouble(IntPtr receiver, IntPtr selector, IntPtr sel2, IntPtr arg, double delay);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern double SendDouble(IntPtr receiver, IntPtr selector);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern CGPoint SendPointPtr(IntPtr receiver, IntPtr selector, IntPtr arg);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern CGRect SendRect(IntPtr receiver, IntPtr selector);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendVoidRect(IntPtr receiver, IntPtr selector, CGRect arg);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendRectArg(IntPtr receiver, IntPtr selector, CGRect arg);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern CGRect SendRectRectPtr(IntPtr receiver, IntPtr selector, CGRect r, IntPtr view);
    }
}
