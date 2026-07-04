// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Minimal Cocoa (AppKit) window backend used by HwndWrapper on macOS, where there is no
// Win32 HWND. It creates an NSWindow with a layer-backed NSView and exposes the NSView* as the
// window "handle" that the rest of WPF threads around in place of an HWND. The WebGPU compositor
// turns that NSView* into a wgpu Metal surface (see MacInterop.CreateSurface in the WebGPU
// engine), so a WPF Window maps to: NSWindow -> contentView (NSView, layer-backed) -> CAMetalLayer.
//
// This is the only file in WindowsBase that talks to the Objective-C runtime / AppKit. P/Invoke
// targets (libobjc, AppKit) are resolved lazily at call time so the file still compiles and ships
// on every platform; the frameworks only load when actually running on macOS.
//

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace MS.Internal.Interop
{
    /// <summary>
    /// A single on-screen Cocoa window (NSWindow + layer-backed NSView). The NSView pointer is the
    /// cross-platform stand-in for an HWND: it is what <see cref="ContentView"/> returns and what
    /// the composition target / WebGPU surface are created against.
    /// </summary>
    // Public because the shared MS.Win32 native-method wrappers (GetWindowRect/SetWindowPos/...)
    // reference it, and those files are compiled into several WPF assemblies; the type itself is
    // defined once (in WindowsBase) so all callers share a single window map.
    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    public sealed class CocoaWindow
    {
        private IntPtr _window;      // NSWindow*
        private IntPtr _contentView; // NSView* (layer-backed) - used as the window handle

        /// <summary>The NSView* that stands in for the HWND. IntPtr.Zero until <see cref="Create"/>.</summary>
        public IntPtr ContentView => _contentView;

        /// <summary>The NSWindow* backing this window.</summary>
        public IntPtr Window => _window;

        /// <summary>All live windows, keyed by their content-view handle, so the handle can be
        /// resolved back to its window (e.g. to query size) from code that only has the "hwnd".</summary>
        private static readonly Dictionary<IntPtr, CocoaWindow> s_byView = new();
        private static readonly object s_lock = new();

        public static CocoaWindow FromHandle(IntPtr view)
        {
            lock (s_lock)
            {
                return s_byView.TryGetValue(view, out CocoaWindow w) ? w : null;
            }
        }

        /// <summary>
        /// The content-view handle of a window whose client area (in device pixels, origin (0,0))
        /// contains the point, or Zero. Stands in for Win32 WindowFromPoint off-Windows; because
        /// client and screen coordinates are treated as identical there, this resolves the window a
        /// client point belongs to (adequate for the single top-level window case).
        /// </summary>
        public static IntPtr HitTest(int x, int y)
        {
            lock (s_lock)
            {
                IntPtr best = IntPtr.Zero;
                long bestOrder = long.MaxValue;
                foreach (CocoaWindow w in s_byView.Values)
                {
                    // Skip windows that aren't on screen (e.g. a popup that was just ordered out) so a
                    // stale entry can't win the hit test.
                    if (w._window == IntPtr.Zero || !SendBool(w._window, Sel("isVisible"))) continue;

                    w.GetClientScreenOriginPixels(out int ox, out int oy);
                    w.GetPixelSize(out int pw, out int ph);
                    if (pw <= 0 || ph <= 0 || x < ox || y < oy || x >= ox + pw || y >= oy + ph)
                    {
                        continue;
                    }

                    // Among overlapping windows return the frontmost (NSWindow orderedIndex: 0 = front),
                    // so a click over a popup floating above the main window hits the POPUP, not the
                    // window beneath it -- essential for selecting popup items / dismissing on outside click.
                    long order = w._window != IntPtr.Zero ? (long)SendNInt(w._window, Sel("orderedIndex")) : long.MaxValue;
                    if (order < bestOrder)
                    {
                        bestOrder = order;
                        best = w._contentView;
                    }
                }
                return best;
            }
        }

        /// <summary>
        /// Create and show an NSWindow with a layer-backed content view. Must be called on the main
        /// (UI) thread. <paramref name="width"/>/<paramref name="height"/> are in points.
        /// </summary>
        public void Create(string title, int x, int y, int width, int height)
        {
            Create(title, x, y, width, height, borderless: false);
        }

        /// <summary>
        /// Create and show an NSWindow. When <paramref name="borderless"/> is true the window has no
        /// title bar and floats above normal windows (used for popups: combo dropdowns, menus,
        /// tooltips); x/y are then treated as a screen position (device pixels, top-left origin).
        /// </summary>
        public void Create(string title, int x, int y, int width, int height, bool borderless)
        {
            EnsureApplication();

            _borderless = borderless;
            if (width <= 0) width = borderless ? 1 : 800;
            if (height <= 0) height = borderless ? 1 : 600;

            IntPtr nsWindowClass = objc_getClass("NSWindow");
            IntPtr alloc = Send(nsWindowClass, Sel("alloc"));

            var frame = new NSRect { x = x, y = y, width = width, height = height };
            const ulong NSWindowStyleMaskBorderless = 0;
            ulong styleMask = borderless
                ? NSWindowStyleMaskBorderless
                : (NSWindowStyleMaskTitled | NSWindowStyleMaskClosable |
                   NSWindowStyleMaskMiniaturizable | NSWindowStyleMaskResizable);

            _window = SendInitWindow(alloc, Sel("initWithContentRect:styleMask:backing:defer:"),
                                     frame, styleMask, NSBackingStoreBuffered, false);

            // Make the content view layer-backed so wgpu can install a CAMetalLayer on it.
            _contentView = Send(_window, Sel("contentView"));
            SendVoidBool(_contentView, Sel("setWantsLayer:"), true);

            if (!string.IsNullOrEmpty(title))
            {
                IntPtr nsTitle = MakeNSString(title);
                SendVoidPtr(_window, Sel("setTitle:"), nsTitle);
            }

            // Deliver mouse-moved events (hover). Button/drag/scroll events arrive regardless, but
            // NSWindow suppresses bare mouseMoved unless this is set. Needed for WPF MouseMove/hit-test.
            SendVoidBool(_window, Sel("setAcceptsMouseMovedEvents:"), true);

            // Keep the NSWindow object alive after the user clicks the red close button (default is to
            // release it, which would dangle). We detect the close by polling isVisible and route it to
            // WPF as a WM_CLOSE; the window is torn down for real from CocoaWindow.Destroy.
            SendVoidBool(_window, Sel("setReleasedWhenClosed:"), false);

            if (borderless)
            {
                // Float above normal windows and appear without stealing key focus, and place it at the
                // requested screen position instead of centering. NSFloatingWindowLevel = 3.
                SendVoidNInt(_window, Sel("setLevel:"), 3);
                SetFrameOrigin(x, y);
                SendVoidPtr(_window, Sel("orderFront:"), IntPtr.Zero);
            }
            else
            {
                SendVoidPtr(_window, Sel("makeKeyAndOrderFront:"), IntPtr.Zero);
                Send(_window, Sel("center"));
            }

            lock (s_lock)
            {
                s_byView[_contentView] = this;
            }
        }

        private bool _borderless;

        /// <summary>True for popup/menu windows (borderless, floating); they get moved to their
        /// requested screen position by SetWindowPos, unlike AppKit-placed top-level windows.</summary>
        public bool IsBorderless => _borderless;

        /// <summary>Resize the window's content area to the given size in points.</summary>
        public void SetContentSize(int width, int height)
        {
            if (_window == IntPtr.Zero || width <= 0 || height <= 0) return;
            SendVoidSize(_window, Sel("setContentSize:"), new NSSize { width = width, height = height });
        }

        /// <summary>
        /// Position the window's top-left at the given SCREEN point expressed in device pixels with a
        /// top-left origin (Win32 convention). Converts to Cocoa's points / bottom-left screen origin.
        /// Used for popups (SetWindowPos move).
        /// </summary>
        public void SetFrameOrigin(int xPixels, int yPixels)
        {
            if (_window == IntPtr.Zero) return;
            double scale = GetBackingScale();
            double xPt = xPixels / scale;
            double yTopPt = yPixels / scale;

            double screenH = 0;
            IntPtr screen = Send(_window, Sel("screen"));
            if (screen == IntPtr.Zero) screen = Send(objc_getClass("NSScreen"), Sel("mainScreen"));
            if (screen != IntPtr.Zero) screenH = SendRect(screen, Sel("frame")).height;

            NSRect frame = SendRect(_window, Sel("frame"));
            // Cocoa origin is the window's bottom-left, measured from the screen bottom.
            double yBottomPt = screenH - yTopPt - frame.height;
            SendVoidPoint(_window, Sel("setFrameOrigin:"), new NSPoint { x = xPt, y = yBottomPt });
        }

        /// <summary>Current content-view size in points (device-independent units).</summary>
        public void GetContentSize(out int width, out int height)
        {
            width = 0; height = 0;
            if (_contentView == IntPtr.Zero) return;

            NSRect bounds = SendRect(_contentView, Sel("bounds"));
            width = (int)Math.Round(bounds.width);
            height = (int)Math.Round(bounds.height);
        }

        /// <summary>
        /// The top-left corner of this window's client (content) area in SCREEN coordinates, expressed
        /// in device pixels with a top-left origin (Win32 screen convention). This is the offset
        /// ClientToScreen adds / ScreenToClient subtracts, so popups land at the right screen location.
        /// </summary>
        public void GetClientScreenOriginPixels(out int sx, out int sy)
        {
            sx = 0; sy = 0;
            if (_window == IntPtr.Zero) return;

            NSRect frame = SendRect(_window, Sel("frame"));                       // screen points, bottom-left
            NSRect content = SendRectRect(_window, Sel("contentRectForFrameRect:"), frame); // screen points, bottom-left
            double scale = GetBackingScale();

            double screenH = PrimaryScreenHeightPoints();
            // Content top edge measured from the top of the (primary) screen.
            double topLeftYpt = screenH - content.y - content.height;
            sx = (int)Math.Round(content.x * scale);
            sy = (int)Math.Round(topLeftYpt * scale);
        }

        private static double PrimaryScreenHeightPoints()
        {
            // Win32 screen coordinates are anchored to the primary monitor's top-left; Cocoa's global
            // coordinate system is anchored to the primary screen's bottom-left. Flip using that screen.
            IntPtr screens = Send(objc_getClass("NSScreen"), Sel("screens"));
            IntPtr primary = (screens != IntPtr.Zero && SendNUInt(screens, Sel("count")) > 0)
                ? SendPtrNUInt(screens, Sel("objectAtIndex:"), 0)
                : Send(objc_getClass("NSScreen"), Sel("mainScreen"));
            return primary != IntPtr.Zero ? SendRect(primary, Sel("frame")).height : 0;
        }

        /// <summary>
        /// The primary screen's full and work-area bounds in Win32 device-pixel semantics (top-left
        /// origin, pixels = points * backingScale). Backs the off-Windows GetMonitorInfo guard so
        /// Window.CenterScreen / work-area clamping have a real monitor rect. The work area is
        /// NSScreen.visibleFrame (Cocoa bottom-left) flipped into the frame's top-left space (the menu
        /// bar shrinks it from the top, the Dock from a side/bottom). Returns false if AppKit is
        /// unavailable (caller falls back to a default).
        /// </summary>
        public static bool GetPrimaryScreenPixels(
            out int monLeft, out int monTop, out int monRight, out int monBottom,
            out int workLeft, out int workTop, out int workRight, out int workBottom)
        {
            monLeft = monTop = monRight = monBottom = 0;
            workLeft = workTop = workRight = workBottom = 0;
            EnsureApplication();
            IntPtr screens = Send(objc_getClass("NSScreen"), Sel("screens"));
            IntPtr primary = (screens != IntPtr.Zero && SendNUInt(screens, Sel("count")) > 0)
                ? SendPtrNUInt(screens, Sel("objectAtIndex:"), 0)
                : Send(objc_getClass("NSScreen"), Sel("mainScreen"));
            if (primary == IntPtr.Zero) return false;

            double scale = SendDouble(primary, Sel("backingScaleFactor"));
            if (scale <= 0) scale = 1.0;
            NSRect frame = SendRect(primary, Sel("frame"));            // full, bottom-left points
            NSRect vis = SendRect(primary, Sel("visibleFrame"));       // work area, bottom-left points

            monLeft = 0;
            monTop = 0;
            monRight = (int)Math.Round(frame.width * scale);
            monBottom = (int)Math.Round(frame.height * scale);

            // Flip visibleFrame (bottom-left) into the frame's top-left space, then scale to pixels.
            double workTopPt = frame.height - (vis.y + vis.height);   // menu-bar gap at the top
            workLeft = (int)Math.Round(vis.x * scale);
            workTop = (int)Math.Round(workTopPt * scale);
            workRight = (int)Math.Round((vis.x + vis.width) * scale);
            workBottom = (int)Math.Round((workTopPt + vis.height) * scale);
            return true;
        }

        /// <summary>
        /// The window's backing scale factor (2.0 on a Retina display, 1.0 otherwise). This is the
        /// single source of truth for the device-pixel/DIP ratio on macOS: the HwndTarget DPI scale,
        /// the client rects (GetPixelSize), and the CAMetalLayer contentsScale must all agree on it,
        /// or layout and the render surface disagree (doubled layout / wrong-scale rendering).
        /// Sourced from the window's screen (stable as soon as the window is ordered in), falling
        /// back to the main screen so it is reliable even at HwndTarget-construction time.
        /// WPF_MAC_FORCE_SCALE overrides it (used to exercise the Retina path on a 1x display).
        /// </summary>
        public double GetBackingScale()
        {
            string force = Environment.GetEnvironmentVariable("WPF_MAC_FORCE_SCALE");
            if (!string.IsNullOrEmpty(force) &&
                double.TryParse(force, System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out double forced) &&
                forced > 0)
            {
                return forced;
            }

            double scale = 0;
            if (_window != IntPtr.Zero)
            {
                IntPtr screen = Send(_window, Sel("screen"));
                if (screen != IntPtr.Zero) scale = SendDouble(screen, Sel("backingScaleFactor"));
                if (scale <= 0) scale = SendDouble(_window, Sel("backingScaleFactor"));
            }
            if (scale <= 0)
            {
                IntPtr mainScreen = Send(objc_getClass("NSScreen"), Sel("mainScreen"));
                if (mainScreen != IntPtr.Zero) scale = SendDouble(mainScreen, Sel("backingScaleFactor"));
            }
            return scale > 0 ? scale : 1.0;
        }

        /// <summary>Current content-view size in pixels (points * backing scale).</summary>
        public void GetPixelSize(out int width, out int height)
        {
            width = 0; height = 0;
            if (_contentView == IntPtr.Zero) return;

            NSRect bounds = SendRect(_contentView, Sel("bounds"));
            double scale = GetBackingScale();

            width = (int)Math.Round(bounds.width * scale);
            height = (int)Math.Round(bounds.height * scale);
        }

        /// <summary>Close and release the window.</summary>
        public void Destroy()
        {
            _destroyedByUs = true;

            if (_contentView != IntPtr.Zero)
            {
                lock (s_lock)
                {
                    s_byView.Remove(_contentView);
                }
            }

            if (_window != IntPtr.Zero)
            {
                Send(_window, Sel("close"));
                _window = IntPtr.Zero;
            }
            _contentView = IntPtr.Zero;
        }

        /// <summary>Raised (on the pump thread) when the user closes the window via its title-bar close
        /// button; the host dispatches a WM_CLOSE so WPF runs its Closing/Closed/shutdown sequence.</summary>
        public event Action<IntPtr> Closed;

        /// <summary>
        /// The NSView handle of the window that currently holds WPF mouse capture (or Zero). This
        /// stands in for Win32 GetCapture() off-Windows so WPF's capture-reestablish heuristics
        /// (e.g. ComboBox/Popup dismiss) see a non-zero "OS capture" while a control is captured.
        /// </summary>
        public static IntPtr MouseCaptureHandle;

        private bool _wasVisible;
        private bool _destroyedByUs;

        private void CheckClosed()
        {
            if (_window == IntPtr.Zero || _destroyedByUs) return;
            bool visible = SendBool(_window, Sel("isVisible"));
            if (visible)
            {
                _wasVisible = true;
                return;
            }
            // Was on-screen and now isn't, and we didn't tear it down -> the user closed it.
            if (_wasVisible)
            {
                _wasVisible = false;
                Closed?.Invoke(_contentView);
            }
        }

        // ---- NSApplication ----------------------------------------------------------

        private static bool s_appInitialized;

        /// <summary>
        /// Bring up NSApplication once so windows can be shown and events dispatched. Sets a
        /// regular activation policy (so the app gets a Dock icon / can become active) and calls
        /// finishLaunching. The actual event loop is pumped incrementally by <see cref="PumpEvents"/>.
        /// </summary>
        internal static void EnsureApplication()
        {
            if (s_appInitialized) return;
            s_appInitialized = true;

            // NSApplication/NSWindow live in AppKit; make sure it (and Foundation) are loaded into
            // the process before we ask the Objective-C runtime for those classes.
            const int RTLD_NOW = 2;
            dlopen("/System/Library/Frameworks/Foundation.framework/Foundation", RTLD_NOW);
            dlopen("/System/Library/Frameworks/AppKit.framework/AppKit", RTLD_NOW);

            IntPtr app = Send(objc_getClass("NSApplication"), Sel("sharedApplication"));
            // NSApplicationActivationPolicyRegular = 0
            SendVoidNInt(app, Sel("setActivationPolicy:"), 0);
            Send(app, Sel("finishLaunching"));
            SendVoidBool(app, Sel("activateIgnoringOtherApps:"), true);
        }

        /// <summary>
        /// Run the Cocoa event loop for up to <paramref name="maxMilliseconds"/>, then drain any
        /// remaining queued events. Blocking on nextEventMatchingMask actually *runs* the run loop
        /// (CoreAnimation commits, the window-server handshake that makes the layer visible, input
        /// delivery) - draining alone does not, which leaves the CAMetalLayer occluded. Called from
        /// the Dispatcher's run loop each frame in place of a bare managed wait.
        /// </summary>
        internal static void PumpEvents(int maxMilliseconds)
        {
            if (!s_appInitialized) return;

            IntPtr app = Send(objc_getClass("NSApplication"), Sel("sharedApplication"));
            IntPtr mode = s_defaultRunLoopMode ??= MakeNSString("kCFRunLoopDefaultMode");

            // Block up to maxMilliseconds for the first event (runs the run loop meanwhile)...
            IntPtr until = SendPtrDouble(objc_getClass("NSDate"), Sel("dateWithTimeIntervalSinceNow:"),
                                         maxMilliseconds / 1000.0);
            IntPtr evt = SendNextEvent(app, Sel("nextEventMatchingMask:untilDate:inMode:dequeue:"),
                                       ulong.MaxValue, until, mode, true);

            // ...then dispatch it and drain the rest without blocking. NSEventMaskAny = ulong.MaxValue.
            IntPtr distantPast = Send(objc_getClass("NSDate"), Sel("distantPast"));
            while (evt != IntPtr.Zero)
            {
                // Translate mouse/scroll events into the managed input event before letting AppKit
                // dispatch the event normally (so window dragging, etc. still work).
                try { ReportMouseEvent(evt); } catch { /* a stray event shouldn't break the pump */ }
                try { ReportKeyEvent(evt); } catch { /* a stray event shouldn't break the pump */ }
                SendVoidPtr(app, Sel("sendEvent:"), evt);
                evt = SendNextEvent(app, Sel("nextEventMatchingMask:untilDate:inMode:dequeue:"),
                                    ulong.MaxValue, distantPast, mode, true);
            }

            // Live windows have no resize delegate wired into the managed side; poll their content
            // size after draining and raise Resized so the hosting HwndTarget re-lays-out and the
            // WebGPU surface is reconfigured (rather than the CAMetalLayer stretching the old frame).
            DetectResizes();
        }

        /// <summary>Raised (on the UI/pump thread) when the content size changes, in points.</summary>
        public event Action<int, int> Resized;

        private int _lastReportedW = -1, _lastReportedH = -1;

        private void CheckResize()
        {
            if (_contentView == IntPtr.Zero) return;
            GetContentSize(out int w, out int h);
            if (w <= 0 || h <= 0) return;
            if (w != _lastReportedW || h != _lastReportedH)
            {
                _lastReportedW = w;
                _lastReportedH = h;
                Resized?.Invoke(w, h);
            }
        }

        private static void DetectResizes()
        {
            CocoaWindow[] windows;
            lock (s_lock)
            {
                if (s_byView.Count == 0) return;
                windows = new CocoaWindow[s_byView.Count];
                s_byView.Values.CopyTo(windows, 0);
            }
            foreach (CocoaWindow w in windows)
            {
                try { w.CheckResize(); } catch { /* a torn-down window shouldn't break the pump */ }
                try { w.CheckClosed(); } catch { /* a torn-down window shouldn't break the pump */ }
            }
        }

        private static IntPtr? s_defaultRunLoopMode;

        // ---- mouse input ------------------------------------------------------------

        /// <summary>
        /// A translated Cocoa mouse/scroll event. <see cref="X"/>/<see cref="Y"/> are client
        /// coordinates in DEVICE PIXELS with a top-left origin (WPF's convention); <see cref="NSType"/>
        /// is the raw NSEventType; <see cref="Wheel"/> is in WPF wheel units (120 per notch).
        /// </summary>
        public readonly struct CocoaMouseMessage
        {
            public IntPtr View { get; }
            public int NSType { get; }
            public int ButtonNumber { get; }
            public int X { get; }
            public int Y { get; }
            public int Wheel { get; }
            public int TimestampMs { get; }

            public CocoaMouseMessage(IntPtr view, int nsType, int buttonNumber, int x, int y, int wheel, int timestampMs)
            {
                View = view; NSType = nsType; ButtonNumber = buttonNumber;
                X = x; Y = y; Wheel = wheel; TimestampMs = timestampMs;
            }
        }

        /// <summary>Raised (on the UI/pump thread) for each Cocoa mouse/scroll event; the WPF mouse
        /// input provider subscribes and forwards it to the InputManager.</summary>
        public static event Action<CocoaMouseMessage> MouseInput;

        // NSEventType values we translate.
        private const ulong NSLeftMouseDown = 1, NSLeftMouseUp = 2, NSRightMouseDown = 3, NSRightMouseUp = 4,
                            NSMouseMoved = 5, NSLeftMouseDragged = 6, NSRightMouseDragged = 7,
                            NSScrollWheel = 22, NSOtherMouseDown = 25, NSOtherMouseUp = 26, NSOtherMouseDragged = 27;

        private static bool IsMouseType(ulong t) =>
            t == NSLeftMouseDown || t == NSLeftMouseUp || t == NSRightMouseDown || t == NSRightMouseUp ||
            t == NSMouseMoved || t == NSLeftMouseDragged || t == NSRightMouseDragged ||
            t == NSScrollWheel || t == NSOtherMouseDown || t == NSOtherMouseUp || t == NSOtherMouseDragged;

        private static void ReportMouseEvent(IntPtr evt)
        {
            Action<CocoaMouseMessage> handler = MouseInput;
            if (handler == null || evt == IntPtr.Zero) return;

            ulong type = SendNUInt(evt, Sel("type"));
            if (!IsMouseType(type)) return;

            IntPtr window = Send(evt, Sel("window"));
            if (window == IntPtr.Zero) return;
            IntPtr view = Send(window, Sel("contentView"));
            CocoaWindow w = FromHandle(view);
            if (w == null) return;

            // A plain mouse-moved event is delivered to the KEY window, but the cursor may actually be
            // over a floating popup (combo dropdown/menu). Resolve the window under the global cursor
            // and report the move relative to IT, so hover/highlight and the mouse-over element track
            // the popup rather than the window beneath it. (Button/drag events already go to the right
            // window, since clicks are delivered to the clicked window.)
            if (type == NSMouseMoved)
            {
                // Use THIS event's location (converted to screen) rather than the current global cursor,
                // so batched/coalesced moves are deterministic and match where the event happened.
                NSPoint locWin = SendPoint(evt, Sel("locationInWindow"));                 // relative to event window
                NSPoint gloc = SendPointPoint(window, Sel("convertPointToScreen:"), locWin); // screen points, bottom-left
                double gs = w.GetBackingScale();
                int gx = (int)Math.Round(gloc.x * gs);
                int gy = (int)Math.Round((PrimaryScreenHeightPoints() - gloc.y) * gs);
                IntPtr underView = HitTest(gx, gy);
                CocoaWindow uw = underView != IntPtr.Zero ? FromHandle(underView) : null;
                if (uw != null)
                {
                    uw.GetClientScreenOriginPixels(out int ox, out int oy);
                    handler(new CocoaMouseMessage(underView, (int)type, 0, gx - ox, gy - oy, 0, Environment.TickCount));
                    return;
                }
            }

            // locationInWindow is in window points, bottom-left origin. Flip Y against the content
            // height and scale to device pixels so it matches WPF's top-left device-pixel client rect.
            NSPoint loc = SendPoint(evt, Sel("locationInWindow"));
            w.GetContentSize(out int _, out int contentH);
            double scale = w.GetBackingScale();
            int x = (int)Math.Round(loc.x * scale);
            int y = (int)Math.Round((contentH - loc.y) * scale);

            int wheel = 0;
            if (type == NSScrollWheel)
            {
                double dy = SendDouble(evt, Sel("scrollingDeltaY"));
                bool precise = SendBool(evt, Sel("hasPreciseScrollingDeltas"));
                // WPF uses 120 units per notch. Trackpad "precise" deltas are pixel-ish and large;
                // line-based wheel deltas are small. Scale each into roughly one notch per gesture step.
                wheel = (int)Math.Round(dy * (precise ? 4.0 : 40.0));
            }

            int button = 0;
            if (type == NSOtherMouseDown || type == NSOtherMouseUp || type == NSOtherMouseDragged)
            {
                button = (int)SendNInt(evt, Sel("buttonNumber"));
            }

            handler(new CocoaMouseMessage(view, (int)type, button, x, y, wheel, Environment.TickCount));
        }

        // ---- keyboard input ---------------------------------------------------------

        /// <summary>
        /// A translated Cocoa key event. <see cref="KeyCode"/> is the macOS hardware key code (kVK_*),
        /// mapped to a Win32 virtual key by the WPF keyboard provider. <see cref="Characters"/> is the
        /// typed text for a key-down (null for key-up / modifier changes). <see cref="ModifierFlags"/>
        /// is the raw NSEvent modifier mask.
        /// </summary>
        public readonly struct CocoaKeyMessage
        {
            public IntPtr View { get; }
            public bool IsDown { get; }
            public int KeyCode { get; }
            public string Characters { get; }
            public bool IsRepeat { get; }
            public ulong ModifierFlags { get; }
            public int TimestampMs { get; }

            public CocoaKeyMessage(IntPtr view, bool isDown, int keyCode, string characters, bool isRepeat, ulong modifierFlags, int timestampMs)
            {
                View = view; IsDown = isDown; KeyCode = keyCode; Characters = characters;
                IsRepeat = isRepeat; ModifierFlags = modifierFlags; TimestampMs = timestampMs;
            }
        }

        /// <summary>Raised (on the UI/pump thread) for each Cocoa key-down/up and modifier change; the
        /// WPF keyboard input provider subscribes and forwards it to the InputManager.</summary>
        public static event Action<CocoaKeyMessage> KeyInput;

        private const ulong NSKeyDown = 10, NSKeyUp = 11, NSFlagsChanged = 12;

        // NSEventModifierFlag masks and the modifier key codes they correspond to.
        private const ulong NSFlagCapsLock = 0x10000, NSFlagShift = 0x20000, NSFlagControl = 0x40000,
                            NSFlagOption = 0x80000, NSFlagCommand = 0x100000;

        private static ulong ModifierMaskForKeyCode(int keyCode) => keyCode switch
        {
            0x38 or 0x3C => NSFlagShift,     // Shift, RightShift
            0x3B or 0x3E => NSFlagControl,   // Control, RightControl
            0x3A or 0x3D => NSFlagOption,    // Option, RightOption
            0x37 or 0x36 => NSFlagCommand,   // Command, RightCommand
            0x39         => NSFlagCapsLock,  // CapsLock
            _            => 0,
        };

        private static void ReportKeyEvent(IntPtr evt)
        {
            Action<CocoaKeyMessage> handler = KeyInput;
            if (handler == null || evt == IntPtr.Zero) return;

            ulong type = SendNUInt(evt, Sel("type"));
            if (type != NSKeyDown && type != NSKeyUp && type != NSFlagsChanged) return;

            IntPtr window = Send(evt, Sel("window"));
            if (window == IntPtr.Zero) return;
            IntPtr view = Send(window, Sel("contentView"));
            CocoaWindow w = FromHandle(view);
            if (w == null) return;

            int keyCode = (int)(SendNUInt(evt, Sel("keyCode")) & 0xFFFF);
            ulong flags = SendNUInt(evt, Sel("modifierFlags"));

            if (type == NSFlagsChanged)
            {
                // A modifier key toggled; direction is whether its flag is now set.
                ulong mask = ModifierMaskForKeyCode(keyCode);
                bool down = mask != 0 && (flags & mask) != 0;
                handler(new CocoaKeyMessage(view, down, keyCode, null, false, flags, Environment.TickCount));
                return;
            }

            bool isDown = type == NSKeyDown;
            bool repeat = isDown && SendBool(evt, Sel("isARepeat"));
            string chars = null;
            if (isDown)
            {
                // Translate through the keyboard layout with a persistent dead-key state so accented
                // characters compose (e.g. Option+e then e -> "é"). Returns "" while a dead key is
                // pending (no text yet) and null only if the Carbon layout query is unavailable, in
                // which case fall back to the event's already-composed characters.
                chars = w.TranslateKeyToText(keyCode, flags);
                if (chars == null)
                {
                    IntPtr ns = Send(evt, Sel("characters"));
                    if (ns != IntPtr.Zero)
                    {
                        IntPtr utf8 = Send(ns, Sel("UTF8String"));
                        if (utf8 != IntPtr.Zero) chars = Marshal.PtrToStringUTF8(utf8);
                    }
                }
            }

            handler(new CocoaKeyMessage(view, isDown, keyCode, chars, repeat, flags, Environment.TickCount));
        }

        // Persistent dead-key state for this window's keyboard layout translation (UCKeyTranslate).
        private uint _deadKeyState;

        /// <summary>
        /// Translate a key press through the current keyboard layout, composing dead keys via the
        /// window's persistent dead-key state. Returns the composed text, "" while a dead key is
        /// pending (no output yet), or null if the layout can't be queried (caller falls back to the
        /// event's characters). This is what makes accented input (Option+e, e -> "é") work; full IME
        /// (CJK candidate windows / inline marked text) needs NSTextInputClient and is not done here.
        /// </summary>
        private string TranslateKeyToText(int keyCode, ulong nsModifierFlags)
        {
            try
            {
                IntPtr source = TISCopyCurrentKeyboardInputSource();
                if (source == IntPtr.Zero) return null;
                try
                {
                    IntPtr key = s_tisUnicodeLayoutKey ??= LoadTisUnicodeLayoutKey();
                    if (key == IntPtr.Zero) return null;

                    IntPtr layoutData = TISGetInputSourceProperty(source, key); // not owned
                    if (layoutData == IntPtr.Zero) return null;
                    IntPtr layout = CFDataGetBytePtr(layoutData);
                    if (layout == IntPtr.Zero) return null;

                    // NSEvent modifier flags -> UCKeyTranslate modifierKeyState (Toolbox format >>8).
                    uint mods = 0;
                    if ((nsModifierFlags & NSFlagCommand) != 0)  mods |= 0x01;
                    if ((nsModifierFlags & NSFlagShift) != 0)    mods |= 0x02;
                    if ((nsModifierFlags & NSFlagCapsLock) != 0) mods |= 0x04;
                    if ((nsModifierFlags & NSFlagOption) != 0)   mods |= 0x08;
                    if ((nsModifierFlags & NSFlagControl) != 0)  mods |= 0x10;

                    const int maxLen = 8;
                    ushort[] buf = new ushort[maxLen];
                    int status = UCKeyTranslate(layout, (ushort)keyCode, /*kUCKeyActionDown*/ 0, mods,
                                                LMGetKbdType(), /*options*/ 0, ref _deadKeyState,
                                                (UIntPtr)maxLen, out UIntPtr actual, buf);
                    if (status != 0) return null;

                    int n = (int)actual;
                    if (n <= 0) return string.Empty; // dead key pending or produced no character
                    var sb = new System.Text.StringBuilder(n);
                    for (int i = 0; i < n; i++) sb.Append((char)buf[i]);
                    return sb.ToString();
                }
                finally
                {
                    CFRelease(source);
                }
            }
            catch
            {
                return null;
            }
        }

        private static IntPtr? s_tisUnicodeLayoutKey;

        // The kTISPropertyUnicodeKeyLayoutData symbol is an exported CFStringRef global; resolve its
        // address via dlsym and read the CFStringRef out of it.
        private static IntPtr LoadTisUnicodeLayoutKey()
        {
            const int RTLD_NOW = 2;
            IntPtr h = dlopen("/System/Library/Frameworks/Carbon.framework/Carbon", RTLD_NOW);
            if (h == IntPtr.Zero) return IntPtr.Zero;
            IntPtr sym = dlsym(h, "kTISPropertyUnicodeKeyLayoutData");
            return sym != IntPtr.Zero ? Marshal.ReadIntPtr(sym) : IntPtr.Zero;
        }

        private const string Carbon = "/System/Library/Frameworks/Carbon.framework/Carbon";
        private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

        [DllImport(Carbon)] private static extern IntPtr TISCopyCurrentKeyboardInputSource();
        [DllImport(Carbon)] private static extern IntPtr TISGetInputSourceProperty(IntPtr inputSource, IntPtr propertyKey);
        [DllImport(Carbon)] private static extern byte LMGetKbdType();
        [DllImport(Carbon)]
        private static extern int UCKeyTranslate(IntPtr keyLayoutPtr, ushort virtualKeyCode, ushort keyAction,
            uint modifierKeyState, uint keyboardType, uint keyTranslateOptions, ref uint deadKeyState,
            UIntPtr maxStringLength, out UIntPtr actualStringLength, [Out] ushort[] unicodeString);
        [DllImport(CoreFoundation)] private static extern IntPtr CFDataGetBytePtr(IntPtr data);
        [DllImport(CoreFoundation)] private static extern void CFRelease(IntPtr cf);
        [DllImport("/usr/lib/libSystem.dylib")] private static extern IntPtr dlsym(IntPtr handle, string symbol);

        // ---- cursor -----------------------------------------------------------------

        /// <summary>
        /// Set the current mouse cursor to an NSCursor obtained from the given NSCursor class-method
        /// selector (e.g. "IBeamCursor", "pointingHandCursor"). No-op if the selector is null/unknown.
        /// Called on the UI thread from the WPF mouse input provider's SetCursor.
        /// </summary>
        public static void SetCursor(string nsCursorSelector)
        {
            if (string.IsNullOrEmpty(nsCursorSelector)) return;
            IntPtr cls = objc_getClass("NSCursor");
            if (cls == IntPtr.Zero) return;
            IntPtr cursor = Send(cls, Sel(nsCursorSelector));
            if (cursor != IntPtr.Zero) Send(cursor, Sel("set"));
        }

        // ---- helpers ----------------------------------------------------------------

        private static IntPtr MakeNSString(string s)
        {
            IntPtr cls = objc_getClass("NSString");
            byte[] utf8 = System.Text.Encoding.UTF8.GetBytes(s + "\0");
            IntPtr buf = Marshal.AllocHGlobal(utf8.Length);
            try
            {
                Marshal.Copy(utf8, 0, buf, utf8.Length);
                return SendPtrRet(cls, Sel("stringWithUTF8String:"), buf);
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }

        // ---- Objective-C runtime ----------------------------------------------------

        private const string ObjC = "/usr/lib/libobjc.A.dylib";

        private static IntPtr Sel(string name) => sel_registerName(name);

        [DllImport(ObjC)] private static extern IntPtr objc_getClass(string name);
        [DllImport(ObjC)] private static extern IntPtr sel_registerName(string name);
        [DllImport("/usr/lib/libSystem.dylib")] private static extern IntPtr dlopen(string path, int mode);

        // objc_msgSend is variadic in C; declare one typed alias per call shape we use.
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr receiver, IntPtr selector);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendVoidPtr(IntPtr receiver, IntPtr selector, IntPtr arg);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendVoidNInt(IntPtr receiver, IntPtr selector, nint arg);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendVoidBool(IntPtr receiver, IntPtr selector, [MarshalAs(UnmanagedType.I1)] bool arg);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendPtrRet(IntPtr receiver, IntPtr selector, IntPtr arg);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendPtrDouble(IntPtr receiver, IntPtr selector, double arg);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern double SendDouble(IntPtr receiver, IntPtr selector);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern NSRect SendRect(IntPtr receiver, IntPtr selector);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern NSRect SendRectRect(IntPtr receiver, IntPtr selector, NSRect arg);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendPtrNUInt(IntPtr receiver, IntPtr selector, nuint arg);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern NSPoint SendPoint(IntPtr receiver, IntPtr selector);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern ulong SendNUInt(IntPtr receiver, IntPtr selector);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern nint SendNInt(IntPtr receiver, IntPtr selector);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] [return: MarshalAs(UnmanagedType.I1)] private static extern bool SendBool(IntPtr receiver, IntPtr selector);

        [DllImport(ObjC, EntryPoint = "objc_msgSend")]
        private static extern IntPtr SendInitWindow(IntPtr receiver, IntPtr selector, NSRect rect, ulong styleMask, ulong backing, [MarshalAs(UnmanagedType.I1)] bool defer);

        [DllImport(ObjC, EntryPoint = "objc_msgSend")]
        private static extern IntPtr SendNextEvent(IntPtr receiver, IntPtr selector, ulong mask, IntPtr untilDate, IntPtr mode, [MarshalAs(UnmanagedType.I1)] bool dequeue);

        [DllImport(ObjC, EntryPoint = "objc_msgSend")]
        private static extern void SendVoidSize(IntPtr receiver, IntPtr selector, NSSize size);

        [DllImport(ObjC, EntryPoint = "objc_msgSend")]
        private static extern void SendVoidPoint(IntPtr receiver, IntPtr selector, NSPoint point);

        [DllImport(ObjC, EntryPoint = "objc_msgSend")]
        private static extern NSPoint SendPointPoint(IntPtr receiver, IntPtr selector, NSPoint point);

        [StructLayout(LayoutKind.Sequential)]
        private struct NSRect
        {
            public double x;
            public double y;
            public double width;
            public double height;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NSSize
        {
            public double width;
            public double height;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NSPoint
        {
            public double x;
            public double y;
        }

        private const ulong NSWindowStyleMaskTitled = 1 << 0;
        private const ulong NSWindowStyleMaskClosable = 1 << 1;
        private const ulong NSWindowStyleMaskMiniaturizable = 1 << 2;
        private const ulong NSWindowStyleMaskResizable = 1 << 3;
        private const ulong NSBackingStoreBuffered = 2;
    }
}
