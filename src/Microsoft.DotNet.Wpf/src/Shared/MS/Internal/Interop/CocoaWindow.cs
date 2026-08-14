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
    public sealed class CocoaWindow : IPlatformWindow
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

        /// <summary>Set a window's title-bar text (see PlatformWindow.SetTitle).</summary>
        public static void SetTitle(IntPtr handle, string title)
        {
            CocoaWindow w = FromHandle(handle);
            if (w == null || w._window == IntPtr.Zero) return;
            SendVoidPtr(w._window, Sel("setTitle:"), MakeNSString(title ?? string.Empty));
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
            Create(title, x, y, width, height, borderless, owner: IntPtr.Zero);
        }

        /// <summary>
        /// As above, but <paramref name="owner"/> is the handle of the window this one belongs to (for a
        /// popup, its owner window). A popup uses the owner's backing scale so its DPI, placement math and
        /// render surface all match the display the owner is on -- while a popup is being positioned it is
        /// not yet associated with that display, so its own NSScreen can report the wrong (primary) scale.
        /// </summary>
        public void Create(string title, int x, int y, int width, int height, bool borderless, IntPtr owner)
        {
            Create(title, x, y, width, height, borderless, owner, chromeless: false);
        }

        /// <summary>
        /// As above, plus <paramref name="chromeless"/>: a REAL window (it takes key focus, resizes and
        /// moves like any other) that simply has no title bar, because the app draws its own — WPF's
        /// WindowStyle=None. A floating tool window is the everyday case.
        /// </summary>
        /// <remarks>
        /// Deliberately not the same as <paramref name="borderless"/>, even though both mean "no title
        /// bar". A truly borderless NSWindow answers NO to canBecomeKeyWindow: right for menus and drag
        /// adorners, which must never take focus, and fatal for a window the user is meant to type into
        /// — it would look frozen. So a chromeless window stays TITLED and hides its title bar instead:
        /// full-size content view, transparent title bar, no title text, no traffic lights. That is the
        /// standard AppKit recipe for custom chrome, and it keeps every behaviour a titled window has.
        /// </remarks>
        public void Create(string title, int x, int y, int width, int height, bool borderless, IntPtr owner, bool chromeless)
        {
            Create(title, x, y, width, height, borderless, owner, chromeless, visible: true);
        }

        /// <summary>
        /// As above, plus <paramref name="visible"/>: whether the window should actually be put on
        /// screen now (Win32's WS_VISIBLE at CreateWindowEx time).
        /// </summary>
        /// <remarks>
        /// Showing unconditionally desynchronises WPF from the platform, and the divergence is
        /// unrecoverable: WPF creates a window it intends to keep HIDDEN (every docking adorner is
        /// created this way), we put it on screen anyway, and WPF -- still believing it hidden --
        /// makes the app's later Hide() a no-op, so nothing ever orders it out. The window then sits
        /// invisible-to-WPF but on top of the app, swallowing every click underneath it.
        /// </remarks>
        public void Create(string title, int x, int y, int width, int height, bool borderless, IntPtr owner, bool chromeless, bool visible)
        {
            Create(title, x, y, width, height, borderless, owner, chromeless, visible, activatable: !borderless);
        }

        /// <summary>
        /// As above, plus <paramref name="activatable"/>: whether this window may take key focus.
        /// </summary>
        /// <remarks>
        /// Only meaningful together with <paramref name="borderless"/>, and it is what separates a
        /// popup from a real window that happens to be transparent -- see EnsureWindowClass. WPF marks
        /// the windows that must never take focus with WS_EX_NOACTIVATE (Popup.CreateWindow), which is
        /// what the caller tests; everything else is a window the user can be expected to type into.
        /// </remarks>
        public void Create(string title, int x, int y, int width, int height, bool borderless, IntPtr owner, bool chromeless, bool visible, bool activatable)
        {
            // AppKit refuses to build a window anywhere but the main thread, and it refuses by
            // raising an Objective-C exception -- which unwinds through managed frames into
            // std::terminate and takes the process with it, with no managed stack to say why. Check
            // first so the caller gets an exception it can actually catch (and a test host gets a
            // failing test rather than a dead run).
            if (pthread_main_np() == 0)
            {
                throw new InvalidOperationException(
                    "A Cocoa window can only be created on the process's main thread; AppKit aborts the " +
                    "process otherwise. Marshal the call to the main thread (Dispatcher) and retry.");
            }

            EnsureApplication();

            _ownerHandle = owner;
            _borderless = borderless;
            if (width <= 0) width = borderless ? 1 : 800;
            if (height <= 0) height = borderless ? 1 : 600;

            _activatable = activatable;
            IntPtr nsWindowClass = EnsureWindowClass();
            IntPtr alloc = Send(nsWindowClass, Sel("alloc"));

            var frame = new NSRect { x = x, y = y, width = width, height = height };
            const ulong NSWindowStyleMaskBorderless = 0;
            ulong styleMask = borderless
                ? NSWindowStyleMaskBorderless
                : (NSWindowStyleMaskTitled | NSWindowStyleMaskClosable |
                   NSWindowStyleMaskMiniaturizable | NSWindowStyleMaskResizable);
            // Chromeless keeps every titled-window behaviour and only takes the title bar away, so the
            // content view extends over where it would have been.
            if (chromeless) styleMask |= NSWindowStyleMaskFullSizeContentView;

            _window = SendInitWindow(alloc, Sel("initWithContentRect:styleMask:backing:defer:"),
                                     frame, styleMask, NSBackingStoreBuffered, false);

            // Must be registered before anything can order the window in: -canBecomeKeyWindow is asked
            // by AppKit during that call, not later.
            if (activatable) s_activatable.Add(_window);

            // Our own NSView subclass rather than the stock content view, because VoiceOver walks the
            // VIEW hierarchy: an accessibility client asks the view what its children are, and only a
            // class we own can answer with the WPF automation tree. Everything else about it is a
            // plain content view -- same frame, same layer-backing, same handle semantics -- and the
            // window still owns it, so nothing downstream (the CAMetalLayer, mouse routing, the input
            // context) can tell the difference.
            IntPtr viewClass = EnsureContentViewClass();
            if (viewClass != IntPtr.Zero)
            {
                NSRect contentRect = SendRect(_window, Sel("contentLayoutRect"));
                IntPtr view = SendPtrRect(Send(viewClass, Sel("alloc")), Sel("initWithFrame:"), contentRect);
                if (view != IntPtr.Zero)
                {
                    SendVoidPtr(_window, Sel("setContentView:"), view);

                    // A view receives dragging messages only after it has registered the types it
                    // takes -- implementing the methods is not enough.
                    CocoaDragDrop.RegisterDraggedTypes(view);
                }
            }

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

            if (chromeless)
            {
                // Take the title bar away without giving up being a titled window: transparent bar, no
                // title text, and none of the three traffic-light buttons -- WPF drew its own caption
                // and close/minimise/maximise buttons in the content, and the native ones would sit on
                // top of them. Combined with FullSizeContentView above, the content view covers the
                // whole frame, so the window looks exactly as it does on Windows under WindowStyle=None.
                const nint NSWindowTitleHidden = 1;
                const nuint NSWindowCloseButton = 0, NSWindowMiniaturizeButton = 1, NSWindowZoomButton = 2;
                SendVoidBool(_window, Sel("setTitlebarAppearsTransparent:"), true);
                SendVoidNInt(_window, Sel("setTitleVisibility:"), NSWindowTitleHidden);
                // NOTE: the window stays MOVABLE. AppKit is kept out of the app's title-bar area by the
                // content view answering NO to -mouseDownCanMoveWindow (see AddMouseDownCanMoveWindow),
                // not by setMovable:NO -- which would also disable -performWindowDragWithEvent: and so
                // break the app's own caption drag, leaving the window impossible to move.
                foreach (nuint b in new[] { NSWindowCloseButton, NSWindowMiniaturizeButton, NSWindowZoomButton })
                {
                    IntPtr button = SendPtrNUInt(_window, Sel("standardWindowButton:"), b);
                    if (button != IntPtr.Zero) SendVoidBool(button, Sel("setHidden:"), true);
                }
            }

            if (borderless)
            {
                // Popups (menus/ComboBox/ToolTip) are per-pixel-alpha on Windows. Make the NSWindow
                // non-opaque with a clear background so the WebGPU surface's alpha is honoured -- the
                // popup's drop shadow + rounded corners then composite over the content behind the
                // window instead of a filled (white) rectangle. The CAMetalLayer is matched to the
                // window's opacity in MacInterop.CreateSurface, and the surface is configured with a
                // premultiplied alpha mode + a transparent clear for layered targets.
                SendVoidBool(_window, Sel("setOpaque:"), false);
                SendVoidPtr(_window, Sel("setBackgroundColor:"),
                            Send(objc_getClass("NSColor"), Sel("clearColor")));
                // No AppKit window shadow: it cannot follow a CAMetalLayer sublayer's alpha, so on a
                // transparent popup it renders as a hard rectangular border around the whole window.
                // The popup's shadow comes from WPF's own DropShadowEffect, rendered by the compositor.
                SendVoidBool(_window, Sel("setHasShadow:"), false);

                // Float above normal windows and appear without stealing key focus, and place it at the
                // requested screen position instead of centering. NSFloatingWindowLevel = 3.
                SendVoidNInt(_window, Sel("setLevel:"), 3);
                SetFrameOrigin(x, y);
                if (visible) SendVoidPtr(_window, Sel("orderFront:"), IntPtr.Zero);
            }
            else
            {
                // The requested width/height is the OUTER window size (Win32/WPF Window.Width/Height
                // semantics). Set the OUTER FRAME directly so the physical window (chrome included) is
                // exactly Width x Height REGARDLESS of the title-bar height -- sizing the content view
                // instead depends on the caption, which macOS reports inconsistently before the window is
                // fully realized (window came out ~10px too tall). Width is shrunk by the faked side frame
                // (macOS has none) so the client width matches Windows and a Width-relative tab fills.
                SetOuterFramePoints(width - Win32NonClientWidthPts, height - Win32ResizeFramePts);

                if (visible) SendVoidPtr(_window, Sel("makeKeyAndOrderFront:"), IntPtr.Zero);
                Send(_window, Sel("center"));
            }

            lock (s_lock)
            {
                s_byView[_contentView] = this;
            }

            // A freshly-ordered-front window's CAMetalLayer stays occluded until the Cocoa run loop has
            // run enough for the window-server compositing handshake to complete. While occluded, wgpu
            // hands back no Metal drawable, so the window paints nothing and the first frame can stall
            // for many seconds until some unrelated event happens to run the loop. Pump the run loop
            // here (bounded) so the handshake completes synchronously and the first present succeeds.
            //
            // Popups (borderless: menus/ComboBox/ToolTip) need this too. A classic-themed menu opened on
            // mouse-down has no entrance animation, so it produces no further commits after it opens --
            // nothing re-drives Present, and the popup shows nothing until an unrelated event (a mouse
            // move) happens to run the loop. Pump it the same way, but don't activateIgnoringOtherApps:
            // for a popup -- it floats above without taking key focus, and stealing focus here would
            // dismiss the very menu we're opening.
            if (visible) PumpUntilVisible(activate: !borderless);
        }

        // Run the Cocoa run loop until the window server reports this window on-screen (its occlusion
        // state includes Visible), bounded so we never block window creation indefinitely.
        private void PumpUntilVisible(bool activate = true)
        {
            const ulong NSWindowOcclusionStateVisible = 1UL << 1;
            if (activate)
            {
                IntPtr app = Send(objc_getClass("NSApplication"), Sel("sharedApplication"));
                SendVoidBool(app, Sel("activateIgnoringOtherApps:"), true);
            }
            // NEVER pump from inside a pump. This loop exists to let a window created at startup finish
            // mapping before the caller carries on, and that is safe only at the top level. Reached
            // from within PumpEvents it re-enters event dispatch on a half-built window, and when the
            // handler being re-entered creates a window of its own the recursion has no bottom:
            //
            //   MouseMove -> app shows an overlay -> Window.Show -> Create -> PumpUntilVisible
            //             -> PumpEvents -> next MouseMove -> app shows an overlay -> ...
            //
            // which is exactly what a docking drag does (an overlay window per drag target, shown on
            // mouse-move); it span at 100% CPU until the stack ran out. Win32 has no equivalent
            // behaviour to preserve here: CreateWindowEx does not dispatch input, and a window created
            // inside a message handler simply appears once that handler returns and the message loop
            // continues -- which is precisely what skipping the pump gives.
            if (s_pumpDepth > 0)
            {
                // Skipping the pump leaves the window ordered-in but never PAINTED: its first frame is
                // a WPF render, and this early return is taken exactly when a window is created from
                // inside input handling -- which is every menu, dropdown and tooltip. The window sat
                // there empty until the next unrelated event happened to drive a render, so a menu
                // "did not open" until the pointer moved over it.
                //
                // Flushing the Dispatcher to Render priority paints it now. This is NOT the recursion
                // that made the pump dangerous: no AppKit event is dequeued and no input is dispatched,
                // so nothing can re-enter the application the way PumpEvents did.
                try
                {
                    // Background, NOT Render: invoking AT a priority runs everything at that priority
                    // and ABOVE, so Background drains the whole queue including Render (where WPF's
                    // layout and MediaContext frame live) while Render alone leaves pending layout
                    // behind. A window shown before its content has been laid out presents an EMPTY
                    // frame, which is why a menu appeared only once some later event forced a real
                    // render -- moving the mouse over it. Same idiom, and same reason, as ElementHost's
                    // per-frame pump.
                    System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                        () => { }, System.Windows.Threading.DispatcherPriority.Background);
                }
                catch { /* no dispatcher on this thread, or it is shutting down */ }

                // A WPF render is not enough on its own: a freshly ordered-in window's CAMetalLayer
                // stays OCCLUDED until the window-server handshake completes, and while it is occluded
                // wgpu hands back no drawable, so the window is simply blank. That handshake needs the
                // RUN LOOP to turn over, which is what the (skipped) pump below would have done.
                //
                // Spin the run loop directly instead: CFRunLoopRunInMode dispatches timers and sources
                // WITHOUT dequeuing NSEvents, so no input is delivered and none of the re-entrancy this
                // early return exists to prevent can happen. Bounded, and stops as soon as the window
                // reports itself unoccluded.
                const ulong NSWindowOcclusionStateVisibleNested = 1UL << 1;
                IntPtr mode = s_defaultRunLoopMode ??= MakeNSString("kCFRunLoopDefaultMode");
                for (int i = 0; i < 20; i++)
                {
                    if (((ulong)(long)Send(_window, Sel("occlusionState")) & NSWindowOcclusionStateVisibleNested) != 0)
                        break;
                    CFRunLoopRunInMode(mode, 0.005, false);
                }
                return;
            }

            for (int i = 0; i < 120; i++)   // ~ up to 120 * 8ms; breaks as soon as visible (usually a few iterations)
            {
                PumpEvents(8);
                if (((ulong)(long)Send(_window, Sel("occlusionState")) & NSWindowOcclusionStateVisible) != 0)
                    break;
            }
        }

        private bool _borderless;
        private bool _activatable;
        private IntPtr _ownerHandle;

        /// <summary>True for popup/menu windows (borderless, floating); they get moved to their
        /// requested screen position by SetWindowPos, unlike AppKit-placed top-level windows.</summary>
        public bool IsBorderless => _borderless;

        // ---- Mica backdrop (macOS translucency) -------------------------------------

        private IntPtr _visualEffectView;   // NSVisualEffectView* installed behind the content, or Zero
        private bool _micaEnabled;

        /// <summary>True once <see cref="EnableMicaBackdrop"/> has made this window translucent.</summary>
        public bool MicaEnabled => _micaEnabled;

        /// <summary>
        /// Turn this top-level window into a translucent "Mica" backdrop: make the NSWindow non-opaque
        /// with a clear background and install an NSVisualEffectView (behind-window blur) as a sibling
        /// behind the layer-backed content view. Windows 11 Mica composites the desktop wallpaper behind
        /// a transparent window; AppKit's NSVisualEffectView is the macOS equivalent (it blurs whatever
        /// is behind the window). Wherever WPF paints the (Fluent-Transparent) window background the blur
        /// shows through. The CAMetalLayer's opacity follows the window's (see MacInterop.CreateSurface)
        /// and the WebGPU compositor configures the surface with an alpha mode + transparent clear so the
        /// alpha is honoured. Called from WindowBackdropManager when the Fluent theme requests a backdrop.
        /// </summary>
        public void EnableMicaBackdrop()
        {
            if (_window == IntPtr.Zero || _contentView == IntPtr.Zero || _micaEnabled) return;

            IntPtr vevClass = objc_getClass("NSVisualEffectView");
            if (vevClass == IntPtr.Zero) return;   // AppKit unavailable -- leave the window opaque

            _micaEnabled = true;

            // Non-opaque window with a clear background so the surface's alpha reaches the compositor and
            // the material behind shows through the transparent parts of the scene.
            SendVoidBool(_window, Sel("setOpaque:"), false);
            SendVoidPtr(_window, Sel("setBackgroundColor:"),
                        Send(objc_getClass("NSColor"), Sel("clearColor")));

            IntPtr vev = Send(Send(vevClass, Sel("alloc")), Sel("init"));
            if (vev == IntPtr.Zero) return;

            // Fill the content view and track its size as the window resizes.
            NSRect bounds = SendRect(_contentView, Sel("bounds"));
            SendVoidRect(vev, Sel("setFrame:"), bounds);
            SendVoidNUInt(vev, Sel("setAutoresizingMask:"), NSViewWidthSizable | NSViewHeightSizable);

            // Leave the effect view's own appearance UNSET so it inherits the window's (which
            // SetWindowAppearance keeps in sync with the WPF theme). Forcing it dark in both themes
            // renders the light theme as a dark gray frost, where Windows light Mica is near-white.
            SendVoidNInt(vev, Sel("setMaterial:"), MicaMaterial());
            SendVoidNInt(vev, Sel("setBlendingMode:"), 0);
            SendVoidNInt(vev, Sel("setState:"), 1);

            // Insert BELOW the metal content. NSWindowBelow = -1; the CAMetalLayer is added later (at
            // surface creation) as a top-most sublayer, so the effect view renders behind the WPF scene.
            SendVoidPtrNIntPtr(_contentView, Sel("addSubview:positioned:relativeTo:"), vev, -1, IntPtr.Zero);
            _visualEffectView = vev;
        }

        // NSVisualEffectMaterial for the Mica backdrop (the effect view is always rendered in dark
        // appearance so this is the desktop-showing material in both themes). Default
        // UnderWindowBackground (21); override with WPF_MAC_MICA_MATERIAL.
        private static nint MicaMaterial()
        {
            string env = Environment.GetEnvironmentVariable("WPF_MAC_MICA_MATERIAL");
            if (!string.IsNullOrEmpty(env) && int.TryParse(env, out int m)) return m;
            return 21;   // NSVisualEffectMaterialUnderWindowBackground
        }

        // A named NSAppearance (Dark/Light Aqua), or Zero if unavailable.
        private static IntPtr MakeAppearance(bool dark)
        {
            IntPtr cls = objc_getClass("NSAppearance");
            if (cls == IntPtr.Zero) return IntPtr.Zero;
            IntPtr name = MakeNSString(dark ? "NSAppearanceNameDarkAqua" : "NSAppearanceNameAqua");
            return SendPtrRet(cls, Sel("appearanceNamed:"), name);
        }

        /// <summary>
        /// Force this window's AppKit appearance to Dark or Light (NSAppearanceNameDarkAqua / Aqua), so
        /// the Mica <c>NSVisualEffectView</c> renders the matching material regardless of the OS
        /// appearance. Called when the WPF theme is applied so app-Light uses a light material and
        /// app-Dark a dark one (else app-Dark on a light system keeps a light frost and the light text is
        /// illegible). Setting it on the window cascades to its views, including the effect view.
        /// </summary>
        public void SetWindowAppearance(bool dark)
        {
            if (_window == IntPtr.Zero) return;
            IntPtr appearance = MakeAppearance(dark);
            if (appearance != IntPtr.Zero)
                SendVoidPtr(_window, Sel("setAppearance:"), appearance);

            // The Mica effect view carries no appearance of its own, so it follows the window set above:
            // light theme gets the light material (near-white, like Windows Mica), dark the dark one.
        }

        /// <summary>Undo <see cref="EnableMicaBackdrop"/>: remove the effect view and make the window
        /// opaque again (WindowBackdropType.None off-Windows).</summary>
        public void DisableMicaBackdrop()
        {
            if (!_micaEnabled) return;
            _micaEnabled = false;
            if (_visualEffectView != IntPtr.Zero)
            {
                Send(_visualEffectView, Sel("removeFromSuperview"));
                _visualEffectView = IntPtr.Zero;
            }
            if (_window != IntPtr.Zero)
                SendVoidBool(_window, Sel("setOpaque:"), true);
        }

        /// <summary>Resize the window's content area to the given size in points.</summary>
        public void SetContentSize(int width, int height) => SetContentSizePoints(width, height);

        /// <summary>
        /// Set the window's OUTER frame (chrome included) to the given size in points, preserving the current
        /// top-left corner. Makes the physical window exactly Window.Width x Height regardless of the
        /// title-bar height -- unlike setContentSize:, whose resulting outer size depends on the caption
        /// (which macOS reports inconsistently before the window is fully realized, leaving the window a few
        /// px too tall). Non-borderless windows only.
        /// </summary>
        private void SetOuterFramePoints(double width, double height)
        {
            if (_window == IntPtr.Zero || width <= 0 || height <= 0) return;
            NSRect cur = SendRect(_window, Sel("frame"));   // screen points, bottom-left origin
            double top = cur.y + cur.height;                // keep the top edge fixed as the size changes
            SendVoidRectBool(_window, Sel("setFrame:display:"),
                new NSRect { x = cur.x, y = top - height, width = width, height = height }, true);
        }

        /// <summary>
        /// Resize the window's content area to a size given in DEVICE PIXELS. Converts to points by
        /// dividing by the backing scale but keeps the FRACTIONAL result -- it does not round to whole
        /// points. This matters on Retina (2x): an integer point size always maps to an even pixel size
        /// (points * 2), so rounding the incoming pixels to whole points first makes an ODD pixel width
        /// unrepresentable and it comes back 1px short from GetPixelSize (round(points * scale)). WPF then
        /// lays out and clips the window's content 1px narrow, which drops a popup's rightmost column --
        /// e.g. a menu's right 1px border. Passing fractional points (110.5pt -> 221px at 2x, pixel-grid
        /// aligned) round-trips exactly.
        /// </summary>
        public void SetContentSizePixels(int cx, int cy)
        {
            double scale = GetBackingScale();
            if (scale <= 0) scale = 1.0;
            SetContentSizePoints(cx / scale, cy / scale);
            TraceWindow($"size cx={cx} cy={cy}");
        }

        private void SetContentSizePoints(double width, double height)
        {
            if (_window == IntPtr.Zero || width <= 0 || height <= 0) return;

            if (!_borderless)
            {
                // The incoming size is the WPF Window size = the OUTER window size (Win32/WPF semantics).
                // Set the OUTER FRAME directly so the physical window (chrome included) is exactly Width x
                // Height regardless of the title-bar height. Width shrinks by the faked side frame so the
                // client width matches Windows and a Width-relative tab fills. AppKit places/moves top-level
                // windows, so just size.
                SetOuterFramePoints(width - Win32NonClientWidthPts, height - Win32ResizeFramePts);
                return;
            }

            // Popups: WPF drives programmatic resizes (popup auto-sizing) through SetWindowPos with
            // SWP_NOMOVE, whose Win32 contract keeps the window's TOP-LEFT corner fixed while the size
            // changes. Cocoa's -setContentSize: instead keeps the BOTTOM-LEFT origin fixed and grows the
            // window upward, so an auto-sizing popup's top edge would jump up as its content lays out (then
            // Reposition would yank it back down -- the menu "jumps up and down until it settles"). Preserve
            // the top-left corner to match the Win32 semantics WPF relies on.
            NSRect before = SendRect(_window, Sel("frame"));           // screen points, bottom-left origin
            double topPt = before.y + before.height;                   // top edge, measured from screen bottom
            SendVoidSize(_window, Sel("setContentSize:"), new NSSize { width = width, height = height });
            NSRect after = SendRect(_window, Sel("frame"));
            SendVoidPoint(_window, Sel("setFrameOrigin:"), new NSPoint { x = before.x, y = topPt - after.height });
        }

        /// <summary>
        /// Position the window's top-left at the given SCREEN point expressed in device pixels with a
        /// top-left origin (Win32 convention). Converts to Cocoa's points / bottom-left screen origin.
        /// Used for popups (SetWindowPos move).
        /// </summary>
        public void SetFrameOrigin(int xPixels, int yPixels)
        {
            if (_window == IntPtr.Zero) return;

            // CW_USEDEFAULT is "no position", not a coordinate. Passing it on builds a frame at
            // int.MinValue, which AppKit rejects with an Objective-C exception that terminates the
            // process (see the same guard in HwndWrapper). WPF sends it whenever it moves a window it
            // was never given a position for; leaving the window where it is, is the whole meaning of
            // the sentinel.
            const int CW_USEDEFAULT = unchecked((int)0x80000000);
            if (xPixels == CW_USEDEFAULT || yPixels == CW_USEDEFAULT) return;

            TraceWindow($"move x={xPixels} y={yPixels}");
            double scale = GetBackingScale();
            double xPt = xPixels / scale;
            double yTopPt = yPixels / scale;

            // Flip about the PRIMARY screen height, matching GetClientScreenOriginPixels (which produced
            // the top-left screen coordinates handed back here) so the round-trip is consistent across
            // monitors. Set the window's TOP-LEFT corner directly with setFrameTopLeftPoint: rather than
            // computing a bottom-left origin from the window's current frame height -- during a popup's
            // create-then-resize the height is transiently 1px, which made the Y land far off on reopen.
            double screenH = PrimaryScreenHeightPoints();
            SendVoidPoint(_window, Sel("setFrameTopLeftPoint:"), new NSPoint { x = xPt, y = screenH - yTopPt });
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

            if (s_traceWindows && !_tracedOrigin)
            {
                _tracedOrigin = true;
                Console.Error.WriteLine(
                    $"[origin] view=0x{_contentView:x} borderless={_borderless} screenH={screenH} " +
                    $"frame=({frame.x},{frame.y} {frame.width}x{frame.height}) " +
                    $"content=({content.x},{content.y} {content.width}x{content.height}) -> client=({sx},{sy})");
            }
        }

        private bool _tracedOrigin;

        /// <summary>
        /// Converts a rectangle in client device pixels (top-left origin, the units WPF hands out)
        /// to screen points with a bottom-left origin -- Cocoa's own coordinate space, and what
        /// -firstRectForCharacterRange: has to answer in so an input method's candidate window lands
        /// under the text being typed. The inverse of the mouse path's screen-to-client math.
        /// Returns false when <paramref name="view"/> does not belong to a live window.
        /// </summary>
        internal static bool TryConvertClientPixelsToScreenPoints(
            IntPtr view, int x, int y, int width, int height,
            out double sx, out double sy, out double swidth, out double sheight)
        {
            sx = sy = swidth = sheight = 0;

            CocoaWindow w = FromHandle(view);
            if (w == null || w._window == IntPtr.Zero) return false;

            double scale = w.GetBackingScale();
            if (scale <= 0) return false;

            w.GetClientScreenOriginPixels(out int ox, out int oy);

            swidth = width / scale;
            sheight = height / scale;
            sx = (ox + x) / scale;

            // y arrives as the TOP edge measured downwards; Cocoa wants the BOTTOM edge measured up
            // from the primary screen's bottom, so flip the far edge rather than the near one.
            sy = PrimaryScreenHeightPoints() - ((oy + y) / scale) - sheight;
            return true;
        }

        /// <summary>
        /// Converts a rectangle already in SCREEN device pixels (top-left origin, the space
        /// AutomationPeer.GetBoundingRectangle reports in) to Cocoa's screen points with a
        /// bottom-left origin. What NSAccessibility's -accessibilityFrame is defined in.
        ///
        /// Distinct from TryConvertClientPixelsToScreenPoints above, which starts from a point
        /// relative to a window's client area; this one starts from the screen space that
        /// ClientToScreen already produced, so only the scale and the vertical flip remain.
        /// </summary>
        internal static void ConvertScreenPixelsToCocoaPoints(
            double x, double y, double width, double height,
            out double sx, out double sy, out double swidth, out double sheight)
        {
            double scale = PrimaryScreenScale();
            if (scale <= 0) scale = 1;

            swidth = width / scale;
            sheight = height / scale;
            sx = x / scale;

            // y arrives as the TOP edge measured downwards; Cocoa wants the BOTTOM edge measured up
            // from the primary screen's bottom, so the far edge is what gets flipped.
            sy = PrimaryScreenHeightPoints() - (y / scale) - sheight;
        }

        /// <summary>
        /// The inverse of ConvertScreenPixelsToCocoaPoints for a point: Cocoa screen points
        /// (bottom-left origin) to the top-left device pixels WPF hit-tests in.
        /// </summary>
        internal static void ConvertCocoaPointsToScreenPixels(double x, double y, out double px, out double py)
        {
            double scale = PrimaryScreenScale();
            if (scale <= 0) scale = 1;

            px = x * scale;
            py = (PrimaryScreenHeightPoints() - y) * scale;
        }

        // ---- activatable borderless windows -------------------------------------------------
        //
        // A borderless NSWindow answers NO to -canBecomeKeyWindow, and AppKit gives no property to
        // change that -- it must be OVERRIDDEN. That default is right for a menu, a tooltip or a drag
        // adorner, and WRONG for a real top-level window that merely paints its own chrome.
        //
        // WPF makes such a window borderless without meaning "never focus me": AllowsTransparency
        // sets WS_EX_LAYERED (HwndSource.Initialize), which is the same bit a popup carries, so a
        // WindowStyle=None + AllowsTransparency window -- a command palette, a splash screen -- came
        // up unfocusable. It could never become key, so it never activated, and therefore could never
        // DEACTIVATE either: anything closing itself on Window.Deactivated (the standard way to
        // dismiss a palette) stayed on screen forever, ignoring clicks elsewhere and outliving even
        // the minimising of the app.
        //
        // So the class is created once and the behaviour is per-window: only handles registered in
        // s_activatable answer YES, leaving popups and adorners exactly as they were.

        private static IntPtr s_windowClass;
        private static readonly HashSet<IntPtr> s_activatable = new HashSet<IntPtr>();

        private delegate bool BoolWindowImpl(IntPtr self, IntPtr sel);
        private static BoolWindowImpl s_canBecomeKeyImpl;
        private static BoolWindowImpl s_canBecomeMainImpl;

        private static IntPtr EnsureWindowClass()
        {
            if (s_windowClass != IntPtr.Zero) return s_windowClass;

            // Re-registering an existing class pair aborts the process, so look it up first.
            IntPtr existing = objc_getClass("WpfWindow");
            if (existing != IntPtr.Zero) return s_windowClass = existing;

            IntPtr nsWindow = objc_getClass("NSWindow");
            if (nsWindow == IntPtr.Zero) return IntPtr.Zero;

            IntPtr cls = objc_allocateClassPair(nsWindow, "WpfWindow", UIntPtr.Zero);
            if (cls == IntPtr.Zero) return s_windowClass = nsWindow;   // fall back to stock NSWindow

            // Kept alive in static fields: the runtime stores the raw function pointer.
            s_canBecomeKeyImpl  = static (self, sel) => s_activatable.Contains(self);
            s_canBecomeMainImpl = static (self, sel) => s_activatable.Contains(self);
            class_addMethod(cls, Sel("canBecomeKeyWindow"),
                            Marshal.GetFunctionPointerForDelegate(s_canBecomeKeyImpl), "c@:");
            class_addMethod(cls, Sel("canBecomeMainWindow"),
                            Marshal.GetFunctionPointerForDelegate(s_canBecomeMainImpl), "c@:");

            objc_registerClassPair(cls);
            return s_windowClass = cls;
        }

        // The content-view class, synthesised once. Its only job beyond being an NSView is to answer
        // accessibility questions out of the WPF automation tree; CocoaAccessibility adds those.
        private static IntPtr s_contentViewClass;

        private static IntPtr EnsureContentViewClass()
        {
            if (s_contentViewClass != IntPtr.Zero) return s_contentViewClass;

            // Re-registering an existing class pair aborts the process, so look it up first.
            IntPtr existing = objc_getClass("WpfContentView");
            if (existing != IntPtr.Zero) return s_contentViewClass = existing;

            IntPtr nsView = objc_getClass("NSView");
            if (nsView == IntPtr.Zero) return IntPtr.Zero;

            IntPtr cls = objc_allocateClassPair(nsView, "WpfContentView", UIntPtr.Zero);
            if (cls == IntPtr.Zero) return IntPtr.Zero;

            CocoaAccessibility.AddViewAccessibility(cls);
            AddGestureMethods(cls);
            AddMouseDownCanMoveWindow(cls);

            // NSDraggingDestination is implemented BY THE VIEW on macOS, not by a delegate, so the
            // drop methods join the accessibility ones on this class.
            CocoaDragDrop.AddViewDragging(cls);

            objc_registerClassPair(cls);
            return s_contentViewClass = cls;
        }

        // ---- Trackpad gestures ---------------------------------------------------------------
        //
        // A Mac has no touchscreen, so this head reports GESTURES rather than contacts: AppKit has
        // already recognised the pinch or the twist, and PlatformGesture turns that into an
        // IManipulator pair. See PlatformGesture.cs for why fabricating touch contacts from trackpad
        // coordinates would be a lie -- they are positions on the TRACKPAD, not on the window.
        //
        // AppKit reports each event as a DELTA (magnification is "how much more since last time"),
        // and the seam takes a cumulative factor, so the running total is kept here. A gesture with
        // no beginGesture:/endGesture: pair around it -- which is what an older trackpad driver
        // sends -- still works: the first magnify starts one and it ends when the totals settle.

        private static double s_gestureMagnification = 1;
        private static double s_gestureRotation;
        private static bool s_gestureActive;

        private delegate bool BoolViewImpl(IntPtr self, IntPtr sel);
        private static BoolViewImpl s_cannotMoveWindowImpl;

        /// <summary>
        /// Answer NO to -mouseDownCanMoveWindow, so AppKit never drags the window because the user
        /// pressed on our content.
        /// </summary>
        /// <remarks>
        /// This is the narrow lever for a window whose chrome the app draws. AppKit asks the view
        /// under the pointer whether a press there may move the window; a plain NSView can say yes,
        /// and then a click on the app's own menu bar dragged the window instead of opening the menu.
        ///
        /// Deliberately NOT setMovable:NO, which was the first attempt: that stops those unwanted
        /// drags but also makes -performWindowDragWithEvent: do nothing (AppKit refuses to drag a
        /// window that is not movable), so the app's OWN title bar stopped working too and no window
        /// could be moved at all. Saying "this view is not a drag handle" leaves the window movable,
        /// which is what BeginMoveDrag needs.
        /// </remarks>
        private static void AddMouseDownCanMoveWindow(IntPtr cls)
        {
            s_cannotMoveWindowImpl = static (self, sel) => false;   // kept alive: the runtime stores the raw pointer
            class_addMethod(cls, Sel("mouseDownCanMoveWindow"),
                            Marshal.GetFunctionPointerForDelegate(s_cannotMoveWindowImpl), "c@:");
        }

        private static void AddGestureMethods(IntPtr cls)
        {
            // Keep the delegates alive for the process: class_addMethod stores the raw pointer.
            s_magnifyImpl = OnMagnify;
            s_rotateImpl = OnRotate;
            s_endGestureImpl = OnEndGesture;

            class_addMethod(cls, Sel("magnifyWithEvent:"),
                            Marshal.GetFunctionPointerForDelegate(s_magnifyImpl), "v@:@");
            class_addMethod(cls, Sel("rotateWithEvent:"),
                            Marshal.GetFunctionPointerForDelegate(s_rotateImpl), "v@:@");
            class_addMethod(cls, Sel("endGestureWithEvent:"),
                            Marshal.GetFunctionPointerForDelegate(s_endGestureImpl), "v@:@");
        }

        private delegate void GestureMethod(IntPtr self, IntPtr sel, IntPtr nsEvent);
        private static GestureMethod s_magnifyImpl, s_rotateImpl, s_endGestureImpl;

        private static void OnMagnify(IntPtr self, IntPtr sel, IntPtr nsEvent)
        {
            try
            {
                // NSEvent.magnification is the delta as a fraction: 0.1 means "10% larger than a
                // moment ago", so it compounds rather than adding.
                s_gestureMagnification *= 1 + SendDouble(nsEvent, Sel("magnification"));
                DispatchGesture(self, nsEvent);
            }
            catch { }
        }

        private static void OnRotate(IntPtr self, IntPtr sel, IntPtr nsEvent)
        {
            try
            {
                // NSEvent.rotation is in degrees, counter-clockwise; WPF turns clockwise.
                s_gestureRotation -= SendFloat(nsEvent, Sel("rotation"));
                DispatchGesture(self, nsEvent);
            }
            catch { }
        }

        private static void OnEndGesture(IntPtr self, IntPtr sel, IntPtr nsEvent)
        {
            try
            {
                if (!s_gestureActive) return;
                PlatformGesture.Sink?.GestureEnd(self, false);
                s_gestureActive = false;
                s_gestureMagnification = 1;
                s_gestureRotation = 0;
            }
            catch { }
        }

        private static void DispatchGesture(IntPtr view, IntPtr nsEvent)
        {
            IPlatformGestureSink sink = PlatformGesture.Sink;
            if (sink is null) return;

            IntPtr handle = view;
            if (handle == IntPtr.Zero) return;

            GestureScreenPoint(nsEvent, out int screenX, out int screenY);

            if (!s_gestureActive)
            {
                s_gestureActive = true;
                sink.GestureBegin(handle, screenX, screenY);
            }

            sink.GestureUpdate(handle, screenX, screenY, s_gestureMagnification, s_gestureRotation);
        }

        /// <summary>The event's location in the same screen device pixels the seams use.</summary>
        private static void GestureScreenPoint(IntPtr nsEvent, out int screenX, out int screenY)
        {
            IntPtr window = Send(nsEvent, Sel("window"));
            NSPoint inWindow = SendPoint(nsEvent, Sel("locationInWindow"));

            // Window-local (bottom-left origin) to global, then flipped to the top-left origin WPF
            // and these seams use, then scaled to device pixels.
            NSRect frame = window != IntPtr.Zero ? SendRect(window, Sel("frame")) : default;
            double globalX = frame.x + inWindow.x;
            double globalY = frame.y + inWindow.y;

            double scale = window != IntPtr.Zero ? SendDouble(window, Sel("backingScaleFactor")) : PrimaryScreenScale();
            double flippedY = PrimaryScreenHeightPoints() - globalY;

            screenX = (int)Math.Round(globalX * scale);
            screenY = (int)Math.Round(flippedY * scale);
        }

        /// <summary>The primary screen's backing scale, for conversions with no window in hand.</summary>
        private static double PrimaryScreenScale()
        {
            IntPtr screens = Send(objc_getClass("NSScreen"), Sel("screens"));
            IntPtr primary = (screens != IntPtr.Zero && SendNUInt(screens, Sel("count")) > 0)
                ? SendPtrNUInt(screens, Sel("objectAtIndex:"), 0)
                : Send(objc_getClass("NSScreen"), Sel("mainScreen"));
            return primary == IntPtr.Zero ? 1 : SendDouble(primary, Sel("backingScaleFactor"));
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
        /// <summary>
        /// The refresh rate of the display this window is on, in Hz.
        /// </summary>
        /// <remarks>
        /// -[NSScreen maximumFramesPerSecond] is the honest answer on a ProMotion panel, where the
        /// mode's own refresh rate reads as the current adaptive rate rather than the ceiling. It is
        /// macOS 12+; older systems and any screen that answers 0 fall back to the display mode's
        /// rate, which CoreGraphics reports as 0 for some internal panels -- hence the final 0,
        /// meaning "cannot say", which leaves WPF's existing fallback in charge.
        /// </remarks>
        /// <summary>
        /// Minimize/maximize/restore. AppKit spells maximize "zoom", which fills the screen's
        /// VISIBLE frame (menu bar and Dock excluded) rather than going fullscreen -- the same thing
        /// a maximized window means on Windows.
        /// </summary>
        /// <remarks>
        /// zoom: toggles, so it is only sent when the window is not already zoomed -- otherwise a
        /// second SW_SHOWMAXIMIZED (WPF sends one on every Show of an already-maximized window)
        /// would restore it instead.
        /// </remarks>
        public void SetWindowState(int state)
        {
            if (_window == IntPtr.Zero) return;

            switch (state)
            {
                case 2:   // SW_SHOWMINIMIZED
                case 6:   // SW_MINIMIZE
                case 7:   // SW_SHOWMINNOACTIVE
                    SendVoidPtr(_window, Sel("miniaturize:"), IntPtr.Zero);
                    break;

                case 3:   // SW_SHOWMAXIMIZED / SW_MAXIMIZE
                    if (SendBool(_window, Sel("isMiniaturized"))) SendVoidPtr(_window, Sel("deminiaturize:"), IntPtr.Zero);
                    if (!SendBool(_window, Sel("isZoomed"))) SendVoidPtr(_window, Sel("zoom:"), IntPtr.Zero);
                    break;

                case 1:   // SW_NORMAL
                case 9:   // SW_RESTORE
                    if (SendBool(_window, Sel("isMiniaturized"))) SendVoidPtr(_window, Sel("deminiaturize:"), IntPtr.Zero);
                    if (SendBool(_window, Sel("isZoomed"))) SendVoidPtr(_window, Sel("zoom:"), IntPtr.Zero);
                    break;
            }
        }

        public double GetRefreshRateHz()
        {
            IntPtr screen = _window != IntPtr.Zero ? Send(_window, Sel("screen")) : IntPtr.Zero;
            if (screen == IntPtr.Zero) screen = Send(objc_getClass("NSScreen"), Sel("mainScreen"));
            if (screen == IntPtr.Zero) return 0;

            if (SendBoolSel(screen, Sel("respondsToSelector:"), Sel("maximumFramesPerSecond")))
            {
                nint fps = SendNInt(screen, Sel("maximumFramesPerSecond"));
                if (fps > 0) return fps;
            }

            // Fall back to the CoreGraphics display mode for the screen's own display id.
            IntPtr description = Send(screen, Sel("deviceDescription"));
            if (description == IntPtr.Zero) return 0;
            IntPtr number = SendPtrRet(description, Sel("objectForKey:"), MakeNSString("NSScreenNumber"));
            if (number == IntPtr.Zero) return 0;

            IntPtr mode = CGDisplayCopyDisplayMode((uint)SendNInt(number, Sel("unsignedIntValue")));
            if (mode == IntPtr.Zero) return 0;
            double hz = CGDisplayModeGetRefreshRate(mode);
            CGDisplayModeRelease(mode);
            return hz;
        }

        [DllImport(ObjC, EntryPoint = "objc_msgSend")] [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool SendBoolSel(IntPtr receiver, IntPtr selector, IntPtr arg);

        [DllImport(CoreGraphicsFramework)] private static extern IntPtr CGDisplayCopyDisplayMode(uint display);
        [DllImport(CoreGraphicsFramework)] private static extern double CGDisplayModeGetRefreshRate(IntPtr mode);
        [DllImport(CoreGraphicsFramework)] private static extern void CGDisplayModeRelease(IntPtr mode);

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

            // A borderless popup (menu/dropdown/tooltip) is not yet on its owner's display while it is
            // being positioned, so its own NSScreen can report the primary display's scale instead of the
            // owner's -- which puts the popup on the wrong monitor. Defer to the owner window's scale so
            // the popup's DPI, its placement math and its render surface all match the owner's display.
            if (_borderless && _ownerHandle != IntPtr.Zero)
            {
                CocoaWindow owner = FromHandle(_ownerHandle);
                if (owner != null && !ReferenceEquals(owner, this))
                {
                    return owner.GetBackingScale();
                }
            }

            // Same problem, no owner to borrow from: a top-level WPF Window (an overlay/adorner, which
            // sets no Owner) is created at CW_USEDEFAULT -- far off-screen -- and backingScaleFactor
            // then answers for the PRIMARY display, not the one the app is on. On a machine whose
            // built-in display is Retina and whose external one is not, the window reports 2 while it is
            // off-screen and 1 once moved, and it flips back to 2 on the next resize-then-move.
            //
            // That disagreement is not cosmetic: WPF converts DIPs to device pixels with the DPI it was
            // told at creation and SetContentSizePixels divides by whatever this returns NOW, so every
            // such window came out at half or double its intended size, changing on each move. A drag
            // adorner sized for the app window covered the whole screen instead.
            //
            // So fall back to the app's main window, which is on the display the adorner belongs to,
            // for as long as this window is not yet on a screen of its own.
            if (_window != IntPtr.Zero && Send(_window, Sel("screen")) == IntPtr.Zero)
            {
                CocoaWindow main = MainWindowForScale();
                if (main != null && !ReferenceEquals(main, this))
                {
                    return main.GetBackingScale();
                }
            }

            double scale = 0;
            if (_window != IntPtr.Zero)
            {
                // Prefer the NSWindow's own backingScaleFactor: it is the canonical, reliable value for a
                // shown window and reflects the display the window is primarily on. window.screen's
                // backingScaleFactor can be transiently wrong/stale (it reported 1 on a Retina display in
                // most reads), which is what made this flaky. Fall back to the screen only if needed.
                scale = SendDouble(_window, Sel("backingScaleFactor"));
                if (scale <= 0)
                {
                    IntPtr screen = Send(_window, Sel("screen"));
                    if (screen != IntPtr.Zero) scale = SendDouble(screen, Sel("backingScaleFactor"));
                }
            }
            if (scale <= 0)
            {
                IntPtr mainScreen = Send(objc_getClass("NSScreen"), Sel("mainScreen"));
                if (mainScreen != IntPtr.Zero) scale = SendDouble(mainScreen, Sel("backingScaleFactor"));
            }
            return scale > 0 ? scale : 1.0;
        }

        /// <summary>
        /// Hand key status to an ordinary window when the window that had it is going away.
        /// </summary>
        /// <remarks>
        /// Closing or hiding the key window can leave the application with NO key window at all —
        /// AppKit does not always promote another one, and it never does for a window we order out
        /// ourselves. A keyless app is not obviously broken to look at (it still renders, and still
        /// receives mouse-moved events, though with a nil window on them) but it cannot be used:
        /// clicks are consumed as failed activation attempts and no menu will open. Re-docking a tool
        /// window does exactly this — the floating window holding key is closed — which presented as
        /// the whole app freezing while continuing to draw.
        /// </remarks>
        private static void PromoteKeyWindow(CocoaWindow leaving)
        {
            IntPtr app = Send(objc_getClass("NSApplication"), Sel("sharedApplication"));
            if (app != IntPtr.Zero && Send(app, Sel("keyWindow")) != IntPtr.Zero) return;   // still fine

            CocoaWindow next = null;
            lock (s_lock)
            {
                foreach (CocoaWindow cw in s_byView.Values)
                {
                    if (ReferenceEquals(cw, leaving) || cw._borderless || cw._window == IntPtr.Zero) continue;
                    if (!SendBool(cw._window, Sel("isVisible"))) continue;
                    // Prefer the app's main window; otherwise the first ordinary window still on screen.
                    if (next == null || (app != IntPtr.Zero && cw._window == Send(app, Sel("mainWindow"))))
                        next = cw;
                }
            }

            if (next == null) return;
            next.TraceWindow("PROMOTE to key");
            SendVoidPtr(next._window, Sel("makeKeyAndOrderFront:"), IntPtr.Zero);
        }

        /// <summary>The window whose display a not-yet-placed window should take its scale from: the
        /// app's main (else key) window, falling back to any registered ordinary window.</summary>
        private static CocoaWindow MainWindowForScale()
        {
            IntPtr app = Send(objc_getClass("NSApplication"), Sel("sharedApplication"));
            if (app != IntPtr.Zero)
            {
                foreach (string sel in new[] { "mainWindow", "keyWindow" })
                {
                    IntPtr w = Send(app, Sel(sel));
                    if (w == IntPtr.Zero) continue;
                    CocoaWindow cw = FromHandle(Send(w, Sel("contentView")));
                    if (cw != null && !cw._borderless) return cw;
                }
            }

            // Nothing is key yet (this can run while the first window is still coming up), so take any
            // ordinary window that is already on a screen.
            lock (s_lock)
            {
                foreach (CocoaWindow cw in s_byView.Values)
                {
                    if (!cw._borderless && cw._window != IntPtr.Zero &&
                        Send(cw._window, Sel("screen")) != IntPtr.Zero)
                    {
                        return cw;
                    }
                }
            }
            return null;
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

        // Non-client insets in POINTS (DIPs) for titled (non-borderless) windows, so a WPF Window's
        // Width/Height (the OUTER window size, Win32 semantics) yields the client area. Used CONSISTENTLY by
        // both the SIZING path (SetContentSize/creation) and the REPORTING path (GetWindowRect): they MUST
        // match, or WPF reconciles the discrepancy by resizing the window (reporting a bigger non-client than
        // we physically remove makes WPF grow the window to compensate). So:
        //  - HEIGHT = the REAL macOS title-bar height (frame - content). The macOS caption is the same height
        //    as Windows' here, so this makes the PHYSICAL window exactly Window.Height (chrome included) AND
        //    ActualHeight = Window.Height, matching Windows.
        //  - WIDTH = a fixed 10 (both side frames). macOS titled windows have NO side frame, so without this
        //    the client would be full-width and a Width-relative content offset (e.g. a Width-10 tab) would
        //    leave a ~5px gap each side; 10 makes the client width match Windows so the tab fills.
        private const double Win32NonClientWidthPts = 10.0;    // horizontal frame (both sides); macOS has none natively
        // Win32's INVISIBLE resize frame -- the grab border OUTSIDE the visible window on Windows, INCLUDED in
        // GetWindowRect/Window.Height. macOS has no such border, so to match the VISIBLE Windows window we shrink
        // the physical frame by this (both dimensions) and add it back in GetWindowRect so ActualHeight still
        // equals Window.Height (WPF then doesn't reconcile a mismatch by resizing the window).
        private const double Win32ResizeFramePts = 10.0;

        /// <summary>Non-client insets in points reported via GetWindowRect (0,0 for a borderless popup), so
        /// ActualWidth/Height equal Window.Width/Height. Width = side frame; height = real macOS caption plus
        /// the invisible resize frame the physical window was shrunk by.</summary>
        private void NonClientInsetsPoints(out double w, out double h)
        {
            if (_borderless || _window == IntPtr.Zero) { w = 0; h = 0; return; }
            w = Win32NonClientWidthPts;
            NSRect frame = SendRect(_window, Sel("frame"));
            NSRect content = SendRectRect(_window, Sel("contentRectForFrameRect:"), frame);
            double cap = frame.height - content.height;   // real macOS title-bar height
            h = (cap > 0 ? cap : 0.0) + Win32ResizeFramePts;
        }

        /// <summary>
        /// Current OUTER window (frame) size in pixels: the content view plus the emulated Win32 non-client
        /// frame. This is what GetWindowRect reports (whereas GetClientRect/GetPixelSize report the content
        /// view = client), so WPF sees a non-zero non-client area and Window.Width/Height behave as the
        /// outer window size like Win32. Equals the content size for a borderless popup (no frame).
        /// </summary>
        public void GetWindowPixelSize(out int width, out int height)
        {
            GetPixelSize(out width, out height);
            if (_borderless || _window == IntPtr.Zero) return;
            double scale = GetBackingScale();
            NonClientInsetsPoints(out double wIn, out double hIn);
            width += (int)Math.Round(wIn * scale);
            height += (int)Math.Round(hIn * scale);
        }

        /// <summary>Close and release the window.</summary>
        /// <summary>Order the NSWindow off or back onto the screen (WPF's ShowWindow SW_HIDE/SW_SHOW).</summary>
        /// <remarks>
        /// orderOut: rather than close: — the window stays alive and keeps its content view, its
        /// surface and its handle, so the very same window can be shown again. That is what an app
        /// reusing a cached overlay expects (a docking drag hides its adorner windows between drags
        /// and shows them again on the next one), and it is why hiding must NOT go through Destroy.
        ///
        /// Re-showing a borderless window uses orderFront: rather than makeKeyAndOrderFront:, matching
        /// how it was first shown: an adorner or tooltip must not steal key focus from the window
        /// being dragged.
        /// </remarks>
        // WF_TRACE_WINDOWS=1 traces every window's creation, geometry and show/hide. The first thing to
        // reach for when a window is the wrong size, in the wrong place, or on screen when it should
        // not be -- an invisible window left over the app swallows every click, which presents as the
        // app having frozen rather than as anything to do with windows.
        private static readonly bool s_traceWindows = Environment.GetEnvironmentVariable("WF_TRACE_WINDOWS") == "1";

        private void TraceWindow(string what)
        {
            if (!s_traceWindows || _window == IntPtr.Zero) return;
            NSRect f = SendRect(_window, Sel("frame"));
            Console.Error.WriteLine(
                $"[win] {what} view=0x{_contentView:x} borderless={_borderless} " +
                $"frame=({f.x},{f.y} {f.width}x{f.height}) scale={GetBackingScale()} " +
                $"visible={SendBool(_window, Sel("isVisible"))}");
        }

        /// <summary>Make this the key window (Win32 SetForegroundWindow / Window.Activate).</summary>
        public void Activate()
        {
            if (_window == IntPtr.Zero) return;
            if (_borderless && !_activatable) return;   // a popup must never take focus
            SendVoidPtr(_window, Sel("makeKeyAndOrderFront:"), IntPtr.Zero);
        }

        public void SetVisible(bool visible) => SetVisible(visible, activate: true);

        /// <summary>
        /// As above, plus <paramref name="activate"/>: whether showing should also take key focus.
        /// </summary>
        /// <remarks>
        /// This is Win32's SW_SHOW vs SW_SHOWNA (and SetWindowPos's SWP_NOACTIVATE), which WPF uses to
        /// say which windows may steal focus: a Popup is always shown non-activating, a Window with
        /// ShowActivated=true is not. Collapsing the two lost that intent -- the only distinction left
        /// was the window's own chrome, so a transparent top-level window could never be focused.
        /// A window that may not become key at all ignores the request.
        /// </remarks>
        public void SetVisible(bool visible, bool activate)
        {
            if (_window == IntPtr.Zero) return;
            activate &= !_borderless || _activatable;
            TraceWindow(visible ? "show" : "hide");

            if (!visible)
            {
                // Set BEFORE ordering out: CheckClosed reads isVisible on the pump thread and treats
                // "was on screen, now isn't, and we didn't destroy it" as the user having clicked the
                // close button. Without this flag every Hide() would raise a WM_CLOSE and WPF would
                // tear the window down for real -- the opposite of hiding it, and fatal for a main
                // window, whose Hide() would quit the app.
                bool wasKey = SendBool(_window, Sel("isKeyWindow"));
                _hiddenByUs = true;
                SendVoidPtr(_window, Sel("orderOut:"), IntPtr.Zero);
                // orderOut: never promotes a successor, so hiding the key window strands the app
                // without one. See PromoteKeyWindow.
                if (wasKey) PromoteKeyWindow(this);
                return;
            }

            _hiddenByUs = false;
            SendVoidPtr(_window, Sel(activate ? "makeKeyAndOrderFront:" : "orderFront:"), IntPtr.Zero);

            if (s_traceWindows)
            {
                // The state AFTER ordering in. A window can present frames at the right position and
                // still be invisible: ordered out, behind another window, at the wrong level, or fully
                // transparent. These are the four, measured rather than assumed.
                Console.Error.WriteLine(
                    $"[shown] view=0x{_contentView:x} isVisible={SendBool(_window, Sel("isVisible"))} " +
                    $"occlusion=0x{(ulong)(long)Send(_window, Sel("occlusionState")):x} " +
                    $"level={(long)Send(_window, Sel("level"))} " +
                    $"alpha={SendDouble(_window, Sel("alphaValue"))} " +
                    $"windowNumber={(long)Send(_window, Sel("windowNumber"))} " +
                    $"onActiveSpace={SendBool(_window, Sel("isOnActiveSpace"))}");
            }

            // Complete the window-server handshake here, exactly as creation does when it shows the
            // window itself. A window ordered front has an OCCLUDED CAMetalLayer until the run loop
            // has turned over: wgpu hands back no drawable, so nothing is painted and the window comes
            // up EMPTY until some unrelated event happens to run the loop. Creation used to be the only
            // place a window was shown, so this was only ever done there; now that a window WPF created
            // hidden is shown from here instead, this is where the handshake has to happen for it.
            PumpUntilVisible(activate: activate);
        }

        /// <summary>Start an AppKit window drag from the event being handled. This is the API made
        /// for exactly this case -- a window whose title bar is drawn by the app -- and it runs
        /// AppKit's own move loop, so the window tracks the mouse and snapping/spaces behave natively.
        /// Chromeless windows set movable:NO (AppKit's title-bar drag would otherwise swallow clicks
        /// meant for the app's own menu bar), which makes this the ONLY way they can be moved.</summary>
        public void BeginMoveDrag()
        {
            if (_window == IntPtr.Zero) return;
            IntPtr app = Send(objc_getClass("NSApplication"), Sel("sharedApplication"));
            IntPtr evt = app != IntPtr.Zero ? Send(app, Sel("currentEvent")) : IntPtr.Zero;
            if (evt == IntPtr.Zero) return;

            SendVoidPtr(_window, Sel("performWindowDragWithEvent:"), evt);

            // That call is a MODAL loop inside AppKit: it runs until the button is released, our pump
            // does not run, and AppKit swallows the mouse-up. Anything holding WPF mouse capture when
            // the drag began therefore never sees its release, and a capture that is never released
            // makes the whole app stop responding to clicks -- the exact failure this stack has already
            // produced twice by other routes. Hand WPF the release the OS ate.
            Action<CocoaMouseMessage> handler = MouseInput;
            if (handler == null || _contentView == IntPtr.Zero) return;

            NSPoint global = SendPoint(objc_getClass("NSEvent"), Sel("mouseLocation"));
            double scale = GetBackingScale();
            int gx = (int)Math.Round(global.x * scale);
            int gy = (int)Math.Round((PrimaryScreenHeightPoints() - global.y) * scale);
            GetClientScreenOriginPixels(out int ox, out int oy);
            handler(new CocoaMouseMessage(_contentView, (int)NSLeftMouseUp, 0,
                                          gx - ox, gy - oy, 0, Environment.TickCount));
        }

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

            bool wasKey = _window != IntPtr.Zero && SendBool(_window, Sel("isKeyWindow"));

            if (_window != IntPtr.Zero)
            {
                s_activatable.Remove(_window);
                Send(_window, Sel("close"));
                _window = IntPtr.Zero;
            }
            _contentView = IntPtr.Zero;

            // Closing the key window can leave the app with none — which makes it unusable while it
            // carries on rendering. Docking a floating tool window closes exactly that window.
            if (wasKey) PromoteKeyWindow(this);
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
        private bool _hiddenByUs;

        /// <summary>
        /// Backstop for the keyless-app state: if this app is frontmost yet has no key window, give
        /// key status back to an ordinary window.
        /// </summary>
        /// <remarks>
        /// PromoteKeyWindow covers the two paths we control (closing and hiding), but a window can lose
        /// key for reasons we never see, and the resulting state is bad enough — the app renders but
        /// cannot be clicked, with no visible cause — to be worth detecting rather than trusting we
        /// found every path. Gated on the app being ACTIVE so this can never pull focus from another
        /// application; a backgrounded app is supposed to have no key window.
        /// </remarks>
        private void RecoverKeyWindow()
        {
            IntPtr app = Send(objc_getClass("NSApplication"), Sel("sharedApplication"));
            if (app == IntPtr.Zero) return;
            if (!SendBool(app, Sel("isActive"))) return;
            if (Send(app, Sel("keyWindow")) != IntPtr.Zero) return;
            PromoteKeyWindow(null);
        }

        /// <summary>Raised when the window becomes, or stops being, the key window. True = activated.
        /// The host turns this into the WM_ACTIVATE/WM_SETFOCUS pair WPF expects.</summary>
        public event Action<bool> ActiveChanged;

        private bool _wasKey;

        /// <summary>
        /// Detect key-window changes and report them, so WPF learns the window was activated.
        /// </summary>
        /// <remarks>
        /// Polled, like the close and resize checks beside it, because this backend deliberately owns
        /// no NSWindow delegate. Without it WPF was never told about activation at all: Window.IsActive
        /// stayed false for the app's whole life, and every visual keyed off focus stayed switched off
        /// — a docking tab's selection border (which lights on IsKeyboardFocusWithin) simply never
        /// appeared, and neither did anything else drawn only when the window is active.
        /// </remarks>
        private void CheckActivated()
        {
            if (_window == IntPtr.Zero || _destroyedByUs) return;

            bool key = SendBool(_window, Sel("isKeyWindow"));
            if (key == _wasKey) return;
            _wasKey = key;
            TraceWindow(key ? "ACTIVATE" : "DEACTIVATE");
            ActiveChanged?.Invoke(key);
        }

        private void CheckClosed()
        {
            // _hiddenByUs: an orderOut: from SetVisible looks exactly like a user close from here
            // (isVisible goes false), and reporting that would destroy a window WPF only hid.
            if (_window == IntPtr.Zero || _destroyedByUs || _hiddenByUs) return;
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

        // ---- system appearance (dark/light) -----------------------------------------

        /// <summary>
        /// True when macOS is in Dark mode. Reads the global <c>AppleInterfaceStyle</c> user default,
        /// which is the string "Dark" in dark mode and absent (nil) in light mode -- the standard,
        /// AppKit-free way to query the system appearance. Lets WPF's <c>ThemeMode.System</c> select the
        /// matching Fluent Light/Dark dictionary off-Windows (mirrors the Windows registry read).
        /// </summary>
        public static bool IsSystemDarkTheme()
        {
            try
            {
                // Once NSApplication exists, NSApp.effectiveAppearance is the reliable, LIVE source (it
                // updates when the user toggles Dark/Light at runtime); its name is e.g.
                // "NSAppearanceNameDarkAqua" in dark mode. Before the app is up, fall back to the global
                // AppleInterfaceStyle default (works without AppKit, for the very first theme resolution).
                if (s_appInitialized)
                {
                    IntPtr app = Send(objc_getClass("NSApplication"), Sel("sharedApplication"));
                    if (app != IntPtr.Zero)
                    {
                        IntPtr appearance = Send(app, Sel("effectiveAppearance"));
                        if (appearance != IntPtr.Zero)
                        {
                            IntPtr name = Send(appearance, Sel("name"));
                            IntPtr u = name != IntPtr.Zero ? Send(name, Sel("UTF8String")) : IntPtr.Zero;
                            string appName = u != IntPtr.Zero ? Marshal.PtrToStringUTF8(u) : null;
                            if (!string.IsNullOrEmpty(appName))
                                return appName.Contains("Dark", StringComparison.OrdinalIgnoreCase);
                        }
                    }
                }

                // NSUserDefaults lives in Foundation; make sure it is loaded before asking for the class.
                const int RTLD_NOW = 2;
                dlopen("/System/Library/Frameworks/Foundation.framework/Foundation", RTLD_NOW);

                IntPtr cls = objc_getClass("NSUserDefaults");
                if (cls == IntPtr.Zero) return false;
                IntPtr defaults = Send(cls, Sel("standardUserDefaults"));
                if (defaults == IntPtr.Zero) return false;

                IntPtr key = MakeNSString("AppleInterfaceStyle");
                IntPtr val = SendPtrRet(defaults, Sel("stringForKey:"), key);
                if (val == IntPtr.Zero) return false;   // key absent => Light mode

                IntPtr utf8 = Send(val, Sel("UTF8String"));
                string s = utf8 != IntPtr.Zero ? Marshal.PtrToStringUTF8(utf8) : null;
                return string.Equals(s, "Dark", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Raised (on the UI/pump thread) when the macOS system appearance toggles Dark/Light.
        /// WPF's ThemeManager subscribes and re-applies the Fluent dictionary for ThemeMode.System, the
        /// off-Windows equivalent of the WM_SETTINGCHANGE theme-change path.</summary>
        public static event Action SystemAppearanceChanged;

        private static IntPtr s_appearanceObserver;   // retained NSObject observer, or Zero

        // The Objective-C -appearanceChanged: IMP: (id self, SEL _cmd, id notification) -> void. Kept in
        // a static field so the delegate (and thus the native thunk) is never collected.
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void AppearanceChangedDelegate(IntPtr self, IntPtr cmd, IntPtr notification);
        private static readonly AppearanceChangedDelegate s_appearanceChangedDel = OnAppearanceChangedNative;

        private static void OnAppearanceChangedNative(IntPtr self, IntPtr cmd, IntPtr notification)
        {
            try { SystemAppearanceChanged?.Invoke(); } catch { /* handler failure shouldn't cross the ABI */ }
        }

        // Register (once) for the macOS Dark/Light toggle instead of polling: AppKit posts the distributed
        // notification "AppleInterfaceThemeChangedNotification" when the user switches appearance. We create
        // a tiny NSObject subclass at runtime whose -appearanceChanged: selector is backed by the managed
        // thunk above, and add it as an observer on NSDistributedNotificationCenter. The notification is
        // delivered on the main run loop (pumped by PumpEvents), so the handler runs on the UI thread.
        private static void EnsureAppearanceObserver()
        {
            if (s_appearanceObserver != IntPtr.Zero) return;

            IntPtr nsobject = objc_getClass("NSObject");
            if (nsobject == IntPtr.Zero) return;

            IntPtr cls = objc_getClass("WpfAppearanceObserver");
            if (cls == IntPtr.Zero)
            {
                cls = objc_allocateClassPair(nsobject, "WpfAppearanceObserver", UIntPtr.Zero);
                if (cls == IntPtr.Zero) return;
                IntPtr imp = Marshal.GetFunctionPointerForDelegate(s_appearanceChangedDel);
                class_addMethod(cls, Sel("appearanceChanged:"), imp, "v@:@");  // void, (id, SEL, id)
                objc_registerClassPair(cls);
            }

            IntPtr obs = Send(Send(cls, Sel("alloc")), Sel("init"));
            if (obs == IntPtr.Zero) return;

            IntPtr center = Send(objc_getClass("NSDistributedNotificationCenter"), Sel("defaultCenter"));
            if (center == IntPtr.Zero) return;
            IntPtr name = MakeNSString("AppleInterfaceThemeChangedNotification");
            SendVoidPtr4(center, Sel("addObserver:selector:name:object:"),
                         obs, Sel("appearanceChanged:"), name, IntPtr.Zero);
            s_appearanceObserver = obs;   // keep the observer alive
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

            // Observe runtime Dark/Light toggles so ThemeMode.System re-themes live.
            EnsureAppearanceObserver();
        }

        /// <summary>
        /// Run the Cocoa event loop for up to <paramref name="maxMilliseconds"/>, then drain any
        /// remaining queued events. Blocking on nextEventMatchingMask actually *runs* the run loop
        /// (CoreAnimation commits, the window-server handshake that makes the layer visible, input
        /// delivery) - draining alone does not, which leaves the CAMetalLayer occluded. Called from
        /// the Dispatcher's run loop each frame in place of a bare managed wait.
        /// </summary>
        // > 0 while PumpEvents is on the stack. Window creation consults this and declines to pump
        // again (see PumpUntilVisible), which is what keeps event dispatch from nesting into itself.
        private static int s_pumpDepth;

        internal static void PumpEvents(int maxMilliseconds)
        {
            if (!s_appInitialized) return;
            s_pumpDepth++;
            try { PumpEventsCore(maxMilliseconds); }
            finally { s_pumpDepth--; }
        }

        private static void PumpEventsCore(int maxMilliseconds)
        {

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

        /// <summary>
        /// The reconciliation half of <see cref="PumpEvents"/>, without touching the event queue.
        /// While AppKit owns the thread in a tracking loop (a titlebar drag or a live resize) the
        /// dispatcher is driven by its CFRunLoop source instead of by PumpEvents, so the size poll
        /// above never runs -- and a live resize would stretch the old drawable until the mouse came
        /// up. The run-loop source calls this so the resize is seen while the gesture is happening.
        /// </summary>
        internal static void ReconcileWindows() => DetectResizes();

        /// <summary>Raised (on the UI/pump thread) when the content size changes, in points.</summary>
        public event Action<int, int> Resized;

        /// <summary>Raised (on the UI/pump thread) when the backing scale factor changes (window moved
        /// to a different-DPI display). Carries the new scale.</summary>
        public event Action<double> ScaleChanged;

        private int _lastReportedW = -1, _lastReportedH = -1;
        private double _lastReportedScale = -1;

        private void CheckResize()
        {
            if (_contentView == IntPtr.Zero) return;

            // Detect a backing-scale change first: when the window is dragged onto a different-DPI
            // display its point size is unchanged, so the Resized path below won't fire. Raise
            // ScaleChanged so the host updates its DPI scale, re-lays-out, and reconfigures the
            // render surface to the new device-pixel size.
            double scale = GetBackingScale();
            if (_lastReportedScale < 0)
            {
                _lastReportedScale = scale;
            }
            else if (Math.Abs(scale - _lastReportedScale) > 0.01)
            {
                _lastReportedScale = scale;
                ScaleChanged?.Invoke(scale);
            }

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
                try { w.RecoverKeyWindow(); } catch { /* never let recovery break the pump */ }
                try { w.CheckActivated(); } catch { /* a torn-down window shouldn't break the pump */ }
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
            IntPtr view = window != IntPtr.Zero ? Send(window, Sel("contentView")) : IntPtr.Zero;
            CocoaWindow w = view != IntPtr.Zero ? FromHandle(view) : null;

            // An event we cannot attribute to a live window is DROPPED, and dropping a mouse-UP is not
            // survivable: whatever was mid-drag never learns the button was released, so its capture is
            // never given up and the app stops responding from then on. This happens for real -- a drag
            // that creates or destroys windows (undocking makes a floating window, docking closes it)
            // leaves AppKit holding events whose window is gone or not ours.
            if (window == IntPtr.Zero || w == null)
            {
                bool isUp = type == NSLeftMouseUp || type == NSRightMouseUp || type == NSOtherMouseUp;
                if (s_traceInput)
                {
                    IntPtr nsApp = Send(objc_getClass("NSApplication"), Sel("sharedApplication"));
                    bool appActive = nsApp != IntPtr.Zero && SendBool(nsApp, Sel("isActive"));
                    IntPtr keyWin = nsApp != IntPtr.Zero ? Send(nsApp, Sel("keyWindow")) : IntPtr.Zero;
                    Console.Error.WriteLine(
                        $"[input] DROPPED type={type} evtWin=0x{window:x} view=0x{view:x} " +
                        $"reason={(window == IntPtr.Zero ? "no-window" : "unregistered-window")} " +
                        $"capture=0x{MouseCaptureHandle:x} appActive={appActive} keyWin=0x{keyWin:x}");
                }

                // Deliver a dropped release to whoever holds capture: that is the element the button was
                // pressed against, and the only party that can end the drag.
                if (isUp && MouseCaptureHandle != IntPtr.Zero)
                {
                    CocoaWindow capWin = FromHandle(MouseCaptureHandle);
                    if (capWin != null)
                    {
                        NSPoint gp = SendPoint(objc_getClass("NSEvent"), Sel("mouseLocation"));
                        double cs = capWin.GetBackingScale();
                        int gx = (int)Math.Round(gp.x * cs);
                        int gy = (int)Math.Round((PrimaryScreenHeightPoints() - gp.y) * cs);
                        capWin.GetClientScreenOriginPixels(out int ox2, out int oy2);
                        if (s_traceInput)
                            Console.Error.WriteLine($"[input] RESCUED up -> capture 0x{MouseCaptureHandle:x}");
                        handler(new CocoaMouseMessage(MouseCaptureHandle, (int)type, 0,
                                                      gx - ox2, gy - oy2, 0, Environment.TickCount));
                    }
                }
                return;
            }

            // A plain mouse-moved event is delivered to the KEY window, but the cursor may actually be
            // over a floating popup (combo dropdown/menu). Resolve the window under the global cursor
            // and report the move relative to IT, so hover/highlight and the mouse-over element track
            // the popup rather than the window beneath it. (Button/drag events already go to the right
            // window, since clicks are delivered to the clicked window.)
            // ...unless a window holds capture, in which case the capture block below owns the routing
            // and this hover redirection must not run: it would send moves to whatever is under the
            // pointer while a drag is in progress.
            if (type == NSMouseMoved && MouseCaptureHandle == IntPtr.Zero)
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

            // While a window holds the mouse capture, EVERY mouse event belongs to it, whatever window
            // the pointer is actually over. That is what Win32 SetCapture guarantees and what WPF's
            // capture model is built on, and AppKit does not do it: it delivers each event to the window
            // under the cursor. So the moment a drag put a NEW window under the pointer -- undocking a
            // tool window creates a floating one right there -- the events went to that window instead,
            // the captured element in the ORIGINAL window never saw the mouse-up, and its drag never
            // ended. Capture then stayed held for the life of the app: WPF routed all later input to
            // that element, and a menu (which needs to take capture itself, and gives up if capture is
            // already held) could no longer open anywhere.
            IntPtr captured = MouseCaptureHandle;
            if (captured != IntPtr.Zero && captured != view)
            {
                CocoaWindow cap = FromHandle(captured);
                if (cap != null)
                {
                    view = captured;
                    w = cap;
                }
            }

            // Convert the event location to WPF top-left client device pixels using the SAME screen-space
            // math as the mouse-moved branch above (which is known-correct on Retina: hover/hit-testing
            // tracks properly). Deriving the client point via global-screen coordinates minus the window's
            // client screen origin avoids the earlier local-flip that mixed a window content-height (pixels)
            // with a location (points) and mapped clicks to the top of the window on scale-2 displays.
            NSPoint loc = SendPoint(evt, Sel("locationInWindow"));                       // window points, bottom-left
            NSPoint bloc = SendPointPoint(window, Sel("convertPointToScreen:"), loc);    // screen points, bottom-left
            double scale = w.GetBackingScale();
            int bx = (int)Math.Round(bloc.x * scale);
            int by = (int)Math.Round((PrimaryScreenHeightPoints() - bloc.y) * scale);
            w.GetClientScreenOriginPixels(out int cox, out int coy);
            int x = bx - cox;
            int y = by - coy;

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

            // WF_TRACE_INPUT=1: one line per button event (moves are far too noisy), showing which
            // window AppKit delivered it to, who holds capture, and where it was finally routed. This
            // is what tells apart "the event never arrived", "it went to the wrong window" and "capture
            // is stuck", which look identical from the outside -- the app simply stops responding.
            if (s_traceInput && type != NSMouseMoved && type != NSLeftMouseDragged &&
                type != NSRightMouseDragged && type != NSOtherMouseDragged)
            {
                Console.Error.WriteLine(
                    $"[input] type={type} evtWin=0x{Send(window, Sel("contentView")):x} " +
                    $"capture=0x{MouseCaptureHandle:x} -> target=0x{view:x} at ({x},{y})");
            }

            handler(new CocoaMouseMessage(view, (int)type, button, x, y, wheel, Environment.TickCount));
        }

        private static readonly bool s_traceInput = Environment.GetEnvironmentVariable("WF_TRACE_INPUT") == "1";

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

            // While a text field has focus, the keystroke is the input method's before it is ours:
            // it is what a Japanese IME turns into a composition, and what -handleEvent: answers YES
            // to. Anything it consumed has already come back through NSTextInputClient as marked or
            // committed text (see CocoaTextInput), so passing the same keystroke on as a character
            // would type every word twice.
            if (isDown && CocoaTextInput.IsEnabled && CocoaTextInput.HandleKeyEvent(evt))
            {
                // Mid-composition the input method owns the whole keyboard, arrow keys and Return
                // included -- those move through candidates rather than through the document, so WPF
                // must not see them at all. With no composition in flight only the TEXT belongs to
                // the input method; the key itself still has to reach WPF for shortcuts and editing
                // keys, so it is reported with no characters attached.
                if (CocoaTextInput.IsComposing) return;

                handler(new CocoaKeyMessage(view, isDown, keyCode, null, repeat, flags, Environment.TickCount));
                return;
            }

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
        private const string CoreGraphicsFramework = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";

        [DllImport(Carbon)] private static extern IntPtr TISCopyCurrentKeyboardInputSource();
        [DllImport(Carbon)] private static extern IntPtr TISGetInputSourceProperty(IntPtr inputSource, IntPtr propertyKey);
        [DllImport(Carbon)] private static extern byte LMGetKbdType();
        [DllImport(Carbon)]
        private static extern int UCKeyTranslate(IntPtr keyLayoutPtr, ushort virtualKeyCode, ushort keyAction,
            uint modifierKeyState, uint keyboardType, uint keyTranslateOptions, ref uint deadKeyState,
            UIntPtr maxStringLength, out UIntPtr actualStringLength, [Out] ushort[] unicodeString);
        [DllImport(CoreFoundation)] private static extern IntPtr CFDataGetBytePtr(IntPtr data);
        [DllImport(CoreFoundation)] private static extern void CFRelease(IntPtr cf);
        // NSString and CFStringRef are toll-free bridged, so the mode created by MakeNSString is a
        // valid CFStringRef here.
        [DllImport(CoreFoundation)]
        private static extern int CFRunLoopRunInMode(IntPtr mode, double seconds,
                                                     [MarshalAs(UnmanagedType.I1)] bool returnAfterSourceHandled);
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

        // Runtime class creation, for the distributed-notification observer's callback method.
        [DllImport(ObjC)] private static extern IntPtr objc_allocateClassPair(IntPtr superclass, string name, UIntPtr extraBytes);
        [DllImport(ObjC)] private static extern void objc_registerClassPair(IntPtr cls);
        [DllImport(ObjC)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool class_addMethod(IntPtr cls, IntPtr sel, IntPtr imp, string types);

        [DllImport(ObjC, EntryPoint = "objc_msgSend")]
        private static extern void SendVoidPtr4(IntPtr receiver, IntPtr selector, IntPtr a, IntPtr b, IntPtr c, IntPtr d);
        [DllImport("/usr/lib/libSystem.dylib")] private static extern IntPtr dlopen(string path, int mode);

        /// <summary>Non-zero on the process's main thread -- the only one AppKit accepts UI on.</summary>
        [DllImport("/usr/lib/libSystem.dylib")] private static extern int pthread_main_np();

        // objc_msgSend is variadic in C; declare one typed alias per call shape we use.
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr receiver, IntPtr selector);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendVoidPtr(IntPtr receiver, IntPtr selector, IntPtr arg);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendVoidNInt(IntPtr receiver, IntPtr selector, nint arg);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendVoidBool(IntPtr receiver, IntPtr selector, [MarshalAs(UnmanagedType.I1)] bool arg);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendPtrRet(IntPtr receiver, IntPtr selector, IntPtr arg);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendPtrDouble(IntPtr receiver, IntPtr selector, double arg);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern double SendDouble(IntPtr receiver, IntPtr selector);
        // NSEvent.rotation is a float, not a CGFloat: a float return arrives in a different register
        // than a double, so reading it as one gives nonsense.
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern float SendFloat(IntPtr receiver, IntPtr selector);
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

        [DllImport(ObjC, EntryPoint = "objc_msgSend")]
        private static extern void SendVoidRect(IntPtr receiver, IntPtr selector, NSRect arg);

        [DllImport(ObjC, EntryPoint = "objc_msgSend")]
        private static extern IntPtr SendPtrRect(IntPtr receiver, IntPtr selector, NSRect arg);

        [DllImport(ObjC, EntryPoint = "objc_msgSend")]
        private static extern void SendVoidRectBool(IntPtr receiver, IntPtr selector, NSRect arg, [MarshalAs(UnmanagedType.I1)] bool b);

        [DllImport(ObjC, EntryPoint = "objc_msgSend")]
        private static extern void SendVoidNUInt(IntPtr receiver, IntPtr selector, nuint arg);

        [DllImport(ObjC, EntryPoint = "objc_msgSend")]
        private static extern void SendVoidPtrNIntPtr(IntPtr receiver, IntPtr selector, IntPtr arg1, nint arg2, IntPtr arg3);

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

        private const ulong NSWindowStyleMaskFullSizeContentView = 1 << 15;
        private const ulong NSWindowStyleMaskTitled = 1 << 0;
        private const ulong NSWindowStyleMaskClosable = 1 << 1;
        private const ulong NSWindowStyleMaskMiniaturizable = 1 << 2;
        private const ulong NSWindowStyleMaskResizable = 1 << 3;
        private const ulong NSBackingStoreBuffered = 2;

        // NSAutoresizingMaskOptions: the effect view fills its superview on resize.
        private const nuint NSViewWidthSizable = 2;
        private const nuint NSViewHeightSizable = 16;
    }
}
