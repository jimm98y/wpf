// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The Linux/Wayland implementation of IPlatformWindow -- the sibling of CocoaWindow, UIKitWindow,
// AndroidWindow and BrowserWindow, and deliberately shaped like CocoaWindow so the change at every
// dispatch site is symmetric with the macOS one already there.
//
// The window handle is a small value of its own (Handle), and the wl_surface* stands behind it
// (Surface): a Windows window handle always fits in 32 bits, and code relies on that -- app's
// WindowUtil.SetOwner takes owner.ToInt32(), which the 64-bit wl_surface* overflowed, so Workbench's
// login dialog never opened (CocoaWindow's handles are small for the same reason). Input reaches WPF
// with the handle (HandleOf); the renderer resolves the surface it builds on (SurfaceOf).
//
// TOPLEVELS ARE OWNED BY LIBDECOR. It creates the xdg_surface/xdg_toplevel pair, draws the GNOME
// titlebar, and runs interactive move/resize -- see WlDecor. POPUPS are ours: libdecor has no
// concept of them, so they go through our own xdg_wm_base, with libdecor supplying the parent
// xdg_surface and the content->window-geometry coordinate conversion.
//
// ---------------------------------------------------------------------------------------------
// THE COORDINATE PROBLEM, AND WHAT THIS FILE DOES ABOUT IT
//
// WPF assumes an absolute screen coordinate space: Window.Left/Top, SetWindowPos, ClientToScreen,
// WindowFromPoint and WindowStartupLocation all speak it. Wayland has no such thing, by design --
// a client cannot learn where its window is, and cannot ask to be put anywhere. There is no
// set_position request and there never will be.
//
// So this file keeps a VIRTUAL global space, anchored at the primary output's top-left and measured
// in device pixels, in which every window has a remembered origin:
//
//   * Screen bounds are REAL: wl_output reports position, mode and scale, so GetPrimaryScreenPixels
//     is honest and Window.CenterScreen lands sensibly.
//   * Toplevel positions are REMEMBERED FICTION. SetFrameOrigin records the value WPF chose and
//     does not move anything; GNOME places the window. WPF's own bookkeeping stays perfectly
//     self-consistent, which is what matters -- it never observes a contradiction.
//   * Popup positions are REAL, because the compositor tells us. A popup is placed through an
//     xdg_positioner anchored in the owner, and xdg_popup.configure reports where the compositor
//     ACTUALLY put it after any constraint sliding or flipping. Writing that back into the popup's
//     virtual origin is the keystone: it is what keeps a menu that the compositor nudged on-screen
//     hit-testing correctly, because GetClientScreenOriginPixels then reports the truth.
//   * Input closes the loop: pointer coordinates are surface-local, so virtual = local*scale +
//     window origin is self-consistent by construction.
//
// What stays unfaithful, stated plainly rather than papered over: Window.Left/Top cannot move a
// toplevel, so WindowStartupLocation.Manual/CenterScreen/CenterOwner cannot place one; a window
// never learns which output it is on; and there is no work area distinct from the screen area
// (GNOME's top bar is a layer surface clients cannot see).
// ---------------------------------------------------------------------------------------------
//

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using static MS.Internal.Interop.Wayland.WlProtocols;

namespace MS.Internal.Interop.Wayland
{
    [SupportedOSPlatform("linux")]
    public sealed unsafe class WaylandWindow : IPlatformWindow
    {
        // ---- Win32 non-client emulation ---------------------------------------------------
        //
        // Same discipline as CocoaWindow.cs (see its Win32NonClientWidthPts comment): the SIZING
        // path and the REPORTING path must use the SAME insets, or WPF sees a window whose reported
        // outer size disagrees with what it asked for and resizes it to compensate -- in a loop.
        //
        // Width: Win32 titled windows have a side frame, GNOME's does not; 10 DIPs keeps a
        // Width-relative content offset landing where the same XAML puts it on Windows.
        // Height: queried from libdecor per-frame where possible (the GTK headerbar's height depends
        // on the theme and font, and there is no API that states it), with this as the fallback.
        private const double NonClientWidthDips = 10.0;
        private const double FallbackCaptionDips = 37.0;

        private IntPtr _surface;            // wl_surface*
        private IntPtr _handle;             // the HWND: see the file header
        private IntPtr _frame;              // libdecor_frame* (toplevels)
        private IntPtr _xdgSurface;         // popups only
        private IntPtr _xdgPopup;           // popups only
        private IntPtr _positioner;         // transient, during (re)positioning
        private IntPtr _viewport;
        private IntPtr _fractionalScale;
        private GCHandle _self;

        private bool _borderless;
        private bool _chromeless;            // a toplevel that draws its own frame: libdecor's is hidden
        private IntPtr _ownerHandle;
        private string _title = string.Empty;

        private int _contentW = 1, _contentH = 1;       // logical (DIP) size
        private int _pendingW, _pendingH;
        private int _scale120;                           // fractional scale in 120ths, 0 = unknown
        private int _outputScale = 1;                    // integer wl_output scale fallback
        private int _virtualX, _virtualY;                // device px, virtual global space
        private bool _configured;
        private bool _closeRequested;
        private bool _destroyed;
        private LibdecorWindowState _windowState;

        private int _lastReportedW, _lastReportedH;
        private double _lastReportedScale;

        public event Action<double>? ScaleChanged;
        /// <summary>Content-size change, in logical pixels -- routed to a synthetic WM_SIZE.</summary>
        public event Action<int, int>? Resized;
        /// <summary>Title-bar close (or compositor close request) -- routed to a synthetic WM_CLOSE.</summary>
        public event Action<IntPtr>? Closed;

        private static readonly Dictionary<IntPtr, WaylandWindow> s_byHandle = new();
        private static readonly List<WaylandWindow> s_zOrder = new();   // last == topmost
        private static readonly object s_lock = new object();

        private static LibdecorFrameInterface* s_frameInterface;
        private static IntPtr* s_xdgSurfaceListener;
        private static IntPtr* s_xdgPopupListener;
        private static IntPtr* s_fractionalScaleListener;
        private static IntPtr* s_surfaceListener;

        public IntPtr Handle => _handle;

        /// <summary>The window's wl_surface*: what the compositor and the renderer are given.</summary>
        public IntPtr Surface => _surface;

        // 0x0D00_0000 up, clear of the other heads' and the shims' handle ranges (CocoaWindow's comment)
        private static int s_nextHandle = 0x0D00_0000;
        private static readonly Dictionary<IntPtr, WaylandWindow> s_bySurface = new();

        /// <summary>The handle of the window whose wl_surface is <paramref name="surfaceOrHandle"/>;
        /// anything else (a handle already, or a surface that is no window of ours) as it is.</summary>
        public static IntPtr HandleOf(IntPtr surfaceOrHandle)
        {
            if (surfaceOrHandle == IntPtr.Zero) return IntPtr.Zero;
            lock (s_lock) return s_bySurface.TryGetValue(surfaceOrHandle, out WaylandWindow? w) ? w._handle : surfaceOrHandle;
        }

        /// <summary>The wl_surface* of window <paramref name="handle"/>; anything else as it is.</summary>
        public static IntPtr SurfaceOf(IntPtr handle)
        {
            if (handle == IntPtr.Zero) return IntPtr.Zero;
            lock (s_lock) return s_byHandle.TryGetValue(handle, out WaylandWindow? w) && w._surface != IntPtr.Zero ? w._surface : handle;
        }
        public bool IsBorderless => _borderless;

        /// <summary>
        /// False once the compositor tells us the surface is not being shown. libdecor forwards
        /// xdg_toplevel's SUSPENDED state, which exists for exactly this: it means "nobody can see
        /// this, stop drawing". Presenting anyway would block the UI thread on a Fifo swap chain
        /// (see NativePlatform.IsWindowVisible).
        ///
        /// Nor is a window that was hidden (SetVisible(false): its buffer taken away). WPF hides a
        /// popup as it closes and destroys it later, and the frame it composed in between went to a
        /// surface the compositor no longer shows -- whose frame callback never comes, so the swap
        /// waited on it for good and the app hung.
        /// A popup, too, only while what it is placed against is: the compositor shows nothing of a
        /// popup whose parent it does not show.
        internal bool IsVisible => ShowableButSuspended && (_windowState & LibdecorWindowState.Suspended) == 0;

        /// <summary><see cref="IsVisible"/> but for the compositor's suspended state.</summary>
        private bool ShowableButSuspended => !_destroyed && !_hidden && IsMapped
            && (_xdgPopup == IntPtr.Zero || _popupParent is not { } parent || (parent._xdgPopup != IntPtr.Zero ? parent._onScreen && parent.IsVisible : parent.IsVisible));

        private bool _shown;    // a frame has been let through since the window was made or last hidden

        /// <summary>
        /// Whether a frame may be presented now (asked once a frame by the compositor of WPF's windows).
        /// A Fifo swap waits for the frame callback of the frame before, which comes only from a surface
        /// the compositor shows -- and whether it shows one is said by wl_surface.enter, not by anything
        /// this side knows: a popup of a window nothing presents, a window hidden as it closes. So a
        /// surface not on an output yet gets ONE frame (the one that maps it) and then none until it is
        /// on screen; without this, a frame for such a window hung the app inside eglSwapBuffers.
        /// </summary>
        internal bool CanPresent()
        {
            // The frame that first shows the window waits on nothing (there is no frame before it whose
            // callback a swap could wait for), so the compositor's "suspended" does not hold it back: GNOME
            // marks a toplevel it has nothing of yet suspended, a while after it was made -- a dialog whose
            // first content came late (a busy UI thread, a frame that had nothing in it to show) then
            // never showed at all.
            if (!_shown && ShowableButSuspended && !_framePending)
            {
                _shown = true;
                _framePending = true;
                return true;
            }
            if (!IsVisible) return false;
            if (_outputsEntered > 0) return true;
            if (_framePending) return false;
            _framePending = true;
            return true;
        }

        /// <summary><see cref="CanPresent"/>, for a presenter of this window outside WPF's compositor (the
        /// WinForms host's): asked once for each frame it is about to present.</summary>
        public bool ReadyToPresent() => CanPresent();

        private int _outputsEntered;   // outputs the surface is on (wl_surface.enter less leave)
        private bool _framePending;    // a frame went to the surface while it was on none

        /// <summary>Whether the surface has a role the compositor shows (a toplevel, or a popup with a
        /// parent) and has been configured: only then may anything be presented into it.</summary>
        public bool IsMapped => !_destroyed && _configured && (_frame != IntPtr.Zero || _xdgPopup != IntPtr.Zero);

        // ---- Creation -----------------------------------------------------------------------

        public static void EnsureApplication()
        {
            AppContext.SetData("WpfWebGpu.Wayland.SurfaceOf", (Func<IntPtr, IntPtr>)SurfaceOf);
            WaylandDisplay.SurfaceScaleQuery ??= static s => FromHandle(s)?.GetBackingScale() ?? 1.0;
            // A borderless window (popup, menu, tooltip) is not opaque: its rounded chrome and drop
            // shadow have to composite over whatever is behind them.
            WaylandDisplay.WindowOpaqueQuery ??= static s => !(FromHandle(s)?.IsBorderless ?? false);

            // The same bit, asked as its own question. The compositor needs to know a popup is a
            // popup, and it used to infer that from MilTarget.IsLayered -- a Windows per-pixel-alpha
            // flag that is false for every popup here, so popups were never composited into their
            // owner and took the own-surface path that PopupsShareOwnerSurface exists to prevent.
            // Not inferred from opacity: a main window turned non-opaque for a translucent backdrop
            // is not a popup, and treating it as one would draw it into an owner it does not have.
            WaylandDisplay.PopupWindowQuery ??= static s => FromHandle(s)?.IsBorderless ?? false;
            WaylandDisplay.WindowOriginQuery ??= GetOriginWithinOwner;
            WaylandDisplay.SurfaceScreenOriginQuery ??= static (IntPtr s, out int x, out int y) =>
            {
                x = y = 0;
                FromHandle(s)?.GetClientScreenOriginPixels(out x, out y);
            };
            WaylandDisplay.WindowVisibleQuery ??= static h => FromHandle(h)?.CanPresent() ?? true;
            WaylandDisplay.EnsureInitialized();
        }

        /// <summary>
        /// A popup's top-left corner in device pixels relative to its owner's CONTENT origin. Used
        /// when the popup is drawn INTO the owner's surface rather than presenting its own
        /// (WPF_LINUX_COMPOSITE_POPUPS=1, see NativePlatform.PopupsShareOwnerSurface).
        ///
        /// Content origin, not the owner's virtual origin. The virtual origin is the OUTER top left
        /// (Win32 semantics), so subtracting it leaves the caption height in the offset and the
        /// popup is drawn one titlebar too low -- and because compositing is the path that actually
        /// puts pixels on screen here, that is what the user sees, however correct the xdg_positioner
        /// happens to be. Both origins live in the same virtual space, so going through
        /// GetClientScreenOriginPixels keeps this exact even after the compositor has slid the popup
        /// to keep it on screen.
        /// </summary>
        private static void GetOriginWithinOwner(IntPtr handle, out int x, out int y)
        {
            x = 0;
            y = 0;
            WaylandWindow? w = FromHandle(handle);
            if (w is null) return;
            WaylandWindow? owner = w.ResolveOwner();
            if (owner is null) return;
            owner.GetClientScreenOriginPixels(out int clientX, out int clientY);
            x = w._virtualX - clientX;
            y = w._virtualY - clientY;
        }

        public void Create(string title, int x, int y, int width, int height, bool borderless)
            => Create(title, x, y, width, height, borderless, IntPtr.Zero);

        public void Create(string title, int x, int y, int width, int height, bool borderless, IntPtr owner)
        {
            EnsureApplication();
            // One UI thread per connection (WaylandDisplay.ClaimUiThread): a second one gets an
            // exception it can catch, before any protocol object exists.
            WaylandDisplay.ClaimUiThread();
            EnsureStaticListeners();

            _borderless = borderless;
            _ownerHandle = owner;
            // a window asked for at the middle of the screen (CenterScreen) is put there by the compositor
            if (!borderless && owner == IntPtr.Zero && !CenterWhenMapped && IsCenteredOnScreen(x, y, width, height))
                CenterWhenMapped = true;
            _title = title ?? string.Empty;
            _virtualX = x;
            _virtualY = y;

            double scale = GetBackingScale();
            _contentW = Math.Max(1, (int)Math.Round(width / scale) - (borderless ? 0 : (int)NonClientWidthDips));
            _contentH = Math.Max(1, (int)Math.Round(height / scale) - (borderless ? 0 : (int)CaptionDips()));

            _surface = Wl.Construct(WaylandDisplay.Compositor, WL_COMPOSITOR_CREATE_SURFACE,
                "wl_surface", Wl.wl_proxy_get_version(WaylandDisplay.Compositor), WlArgument.NewId());
            if (_surface == IntPtr.Zero)
                throw new InvalidOperationException("wl_compositor.create_surface failed.");

            _self = GCHandle.Alloc(this);
            lock (s_lock)
            {
                _handle = (IntPtr)System.Threading.Interlocked.Add(ref s_nextHandle, 4);
                s_byHandle[_handle] = this;
                s_bySurface[_surface] = this;
                s_zOrder.Add(this);
            }

            Wl.wl_proxy_add_listener(_surface, s_surfaceListener, GCHandle.ToIntPtr(_self));
            AttachScaleObjects();

            if (borderless)
                CreatePopup();
            else
                CreateToplevel();

            // The mandatory empty first commit: it has no buffer, and its only job is to provoke the
            // compositor's first configure. Until that configure arrives and is acked, the surface
            // has no size and no committed role, and anything attached to it is discarded.
            Wl.Request(_surface, WL_SURFACE_COMMIT);
            WaylandDisplay.Flush();

            PumpUntilVisible();

            // after the initial commit, which made the compositor's window, and before the first
            // buffer, which places it
            if (CenterWhenMapped && !borderless && _ownerHandle == IntPtr.Zero) MarkDialogUntilMapped();

            // Only a real toplevel is worth exporting: a popup is a menu or a tooltip belonging to a
            // window that has already registered, and AT-SPI wants one application root, not one per
            // surface. The bridge itself decides whether an assistive technology is present.
            if (!borderless) AtSpiBridge.TryAttach(_surface);
        }

        private void CreateToplevel()
        {
            if (WaylandDisplay.Decor == IntPtr.Zero)
                throw new InvalidOperationException("libdecor is unavailable, so a decorated Wayland toplevel cannot be created.");

            _frame = WlDecor.libdecor_decorate(WaylandDisplay.Decor, _surface, s_frameInterface, GCHandle.ToIntPtr(_self));
            if (_frame == IntPtr.Zero)
                throw new InvalidOperationException("libdecor_decorate failed.");

            WlDecor.libdecor_frame_set_app_id(_frame, AppId);
            WlDecor.libdecor_frame_set_title(_frame, _title);
            WlDecor.libdecor_frame_set_min_content_size(_frame, 1, 1);
            // an owned window (a dialog) belongs to its owner before it is shown: the compositor places it
            // when it maps, over the owner (see SetOwnerWindow)
            if (FromHandle(_ownerHandle) is { } owner && !ReferenceEquals(owner, this) && owner._frame != IntPtr.Zero && !owner._destroyed)
                WlDecor.libdecor_frame_set_parent(_frame, owner._frame);
            WlDecor.libdecor_frame_map(_frame);
        }

        /// <summary>
        /// Set before Create: the window should open at the centre of the screen (a CenterScreen
        /// form, a splash). A client cannot place a toplevel on Wayland, but GNOME centres a dialog
        /// that has no parent on its monitor, so the toplevel is a modal dialog (xdg-dialog-v1) until
        /// it is on screen -- placed by then -- and an ordinary window after.
        /// </summary>
        public bool CenterWhenMapped { get; set; }

        /// <summary>The outer rectangle (device pixels) is centred on the primary screen, or on its work
        /// area, give or take a pixel of rounding.</summary>
        private static bool IsCenteredOnScreen(int x, int y, int width, int height)
        {
            if (!GetPrimaryScreenPixels(out int l, out int t, out int r, out int b, out int wl, out int wt, out int wr, out int wb))
                return false;
            int cx = x + width / 2, cy = y + height / 2;
            return (Math.Abs(cx - (l + r) / 2) <= 1 && Math.Abs(cy - (t + b) / 2) <= 1)
                || (Math.Abs(cx - (wl + wr) / 2) <= 1 && Math.Abs(cy - (wt + wb) / 2) <= 1);
        }

        private IntPtr _dialog;       // the xdg_dialog_v1 that has the compositor centre the window
        private IntPtr _gtkSurface;   // or, on a GNOME without it, gtk_surface1's modal hint
        private bool _onScreen;       // the surface has entered an output: shown, and placed
        private bool _gtkModal;

        private void MarkDialogUntilMapped()
        {
            try
            {
                if (WaylandDisplay.WmDialog != IntPtr.Zero && _frame != IntPtr.Zero)
                {
                    IntPtr toplevel = WlDecor.libdecor_frame_get_xdg_toplevel(_frame);
                    if (toplevel != IntPtr.Zero)
                    {
                        _dialog = Wl.Construct(WaylandDisplay.WmDialog, XDG_WM_DIALOG_V1_GET_XDG_DIALOG, "xdg_dialog_v1", 1,
                            WlArgument.NewId(), WlArgument.Ptr(toplevel));
                        if (_dialog != IntPtr.Zero) { Wl.Request(_dialog, XDG_DIALOG_V1_SET_MODAL); return; }
                    }
                }
                if (WaylandDisplay.GtkShell != IntPtr.Zero)
                {
                    _gtkSurface = Wl.Construct(WaylandDisplay.GtkShell, GTK_SHELL1_GET_GTK_SURFACE, "gtk_surface1", 1,
                        WlArgument.NewId(), WlArgument.Ptr(_surface));
                    if (_gtkSurface != IntPtr.Zero) { Wl.Request(_gtkSurface, GTK_SURFACE1_SET_MODAL); _gtkModal = true; }
                }
                WaylandDisplay.LogSink?.Invoke($"centre on map: xdg_dialog={_dialog != IntPtr.Zero} gtk_surface={_gtkSurface != IntPtr.Zero}");
            }
            catch { _dialog = IntPtr.Zero; }
        }

        /// <summary>The window is on screen, so placed: an ordinary window from now on.</summary>
        private void DropDialog()
        {
            try
            {
                if (_dialog != IntPtr.Zero)
                {
                    IntPtr dialog = _dialog;
                    _dialog = IntPtr.Zero;
                    Wl.Destroy(dialog, XDG_DIALOG_V1_DESTROY);
                }
                if (_gtkModal && _gtkSurface != IntPtr.Zero)
                {
                    _gtkModal = false;
                    Wl.Request(_gtkSurface, GTK_SURFACE1_UNSET_MODAL);
                }
            }
            catch { }
        }

        private void CreatePopup()
        {
            IntPtr wmBase = WaylandDisplay.XdgWmBase;
            if (wmBase == IntPtr.Zero)
            {
                // No xdg_wm_base: the popup cannot be a real surface. Leave it unmapped rather than
                // failing window creation -- WPF will still route input to it through the handle.
                WaylandDisplay.LogSink?.Invoke("no xdg_wm_base; popup will not be mapped.");
                return;
            }

            _xdgSurface = Wl.Construct(wmBase, XDG_WM_BASE_GET_XDG_SURFACE, "xdg_surface",
                Wl.wl_proxy_get_version(wmBase), WlArgument.NewId(), WlArgument.Ptr(_surface));
            Wl.wl_proxy_add_listener(_xdgSurface, s_xdgSurfaceListener, GCHandle.ToIntPtr(_self));

            IntPtr parentXdg = ParentXdgSurface();
            if (parentXdg == IntPtr.Zero)
            {
                WaylandDisplay.LogSink?.Invoke("popup has no parent xdg_surface; not mapping it.");
                return;
            }

            IntPtr positioner = BuildPositioner();
            _popupParent = ResolveOwner();
            _popupSeq = ++s_popupSeq;
            _xdgPopup = Wl.Construct(_xdgSurface, XDG_SURFACE_GET_POPUP, "xdg_popup",
                Wl.wl_proxy_get_version(_xdgSurface),
                WlArgument.NewId(), WlArgument.Ptr(parentXdg), WlArgument.Ptr(positioner));
            Wl.wl_proxy_add_listener(_xdgPopup, s_xdgPopupListener, GCHandle.ToIntPtr(_self));
            Wl.Destroy(positioner, XDG_POSITIONER_DESTROY);
        }

        /// <summary>
        /// The window this popup is positioned against. Every place that reasons about a popup's
        /// placement MUST agree on this: parenting to one window while anchoring against another's
        /// coordinates puts the popup somewhere neither of them expects.
        /// </summary>
        private WaylandWindow? ResolveOwner()
        {
            // the owner, if it can still parent a popup: a popup that lost its role (one below it went, see
            // the popup stack) or a destroyed window cannot, and a popup made against it was never mapped --
            // presenting into that surface then aborted the process inside EGL
            // Once placed, a popup keeps the window it was placed against: every conversion between its
            // position and the owner's must use the same one.
            if (_popupParent is { } placed && !placed._destroyed && (placed._frame != IntPtr.Zero || placed._xdgPopup != IntPtr.Zero))
                return placed;
            // A popup owner only if the compositor shows it: a WPF tree hosted in a WinForms form has a
            // window of its own that nothing ever presents (the form composites it) -- made while a
            // splash screen was the only window, it is a popup of that -- and a popup placed against it
            // was never shown, so its second frame waited for good on the first (the app hung).
            WaylandWindow? owner = FromHandle(_ownerHandle);
            if (owner is not null && !ReferenceEquals(owner, this) && !owner._destroyed
                && (owner._frame != IntPtr.Zero || (owner._xdgPopup != IntPtr.Zero && owner._onScreen && !owner._hidden))) return owner;

            // No explicit owner (WPF does not always supply one for menus): fall back to the
            // topmost mapped toplevel, which is the window the menu visually belongs to.
            lock (s_lock)
            {
                for (int i = s_zOrder.Count - 1; i >= 0; i--)
                {
                    WaylandWindow w = s_zOrder[i];
                    if (!ReferenceEquals(w, this) && w._frame != IntPtr.Zero) return w;
                }
            }
            return null;
        }

        /// <summary>
        /// The owner's decoration inset in DIPs: the offset from its window-geometry origin to its
        /// content origin. Measured from libdecor rather than assumed, because the GTK headerbar's
        /// height depends on the theme and font.
        /// </summary>
        private static void OwnerDecorationInset(WaylandWindow owner, out int insetX, out int insetY)
        {
            insetX = 0;
            insetY = 0;
            if (owner._frame == IntPtr.Zero) return;
            try { WlDecor.libdecor_frame_translate_coordinate(owner._frame, 0, 0, out insetX, out insetY); }
            catch { }
        }

        /// <summary>
        /// A point in the VIRTUAL space to the space xdg_positioner.set_anchor_rect is specified
        /// against: the parent xdg_surface's WINDOW GEOMETRY.
        ///
        /// For a libdecor toplevel that window geometry is the decorated FRAME, less its shadow: on
        /// GNOME the headerbar lies inside it, so the content origin is one headerbar below the
        /// geometry origin. libdecor_frame_translate_coordinate gives that offset (none for a frame
        /// whose decorations are hidden). Measured: without it a combo drop-down presenting its own
        /// surface opened one headerbar (37 px) above the combo. The way back (OwnerGeometryToVirtual)
        /// takes the same offset off, so a popup drawn into its owner lands where it did.
        /// </summary>
        private static void VirtualToOwnerGeometry(WaylandWindow owner, int virtualX, int virtualY,
                                                   out int geometryX, out int geometryY)
        {
            double scale = owner.GetBackingScale();
            owner.GetClientScreenOriginPixels(out int clientX, out int clientY);
            OwnerDecorationInset(owner, out int insetX, out int insetY);
            geometryX = (int)Math.Round((virtualX - clientX) / scale) + insetX;
            geometryY = (int)Math.Round((virtualY - clientY) / scale) + insetY;
        }

        /// <summary>
        /// The exact inverse of <see cref="VirtualToOwnerGeometry"/>, for turning what the compositor
        /// reports in xdg_popup.configure back into the virtual space.
        ///
        /// These two MUST remain inverses. WPF asks for a position, we convert it one way, and the
        /// compositor answers in the same space; if the return trip is not the exact mirror, the
        /// popup's recorded origin drifts from where WPF believes it is and hit-testing follows the
        /// wrong rectangle -- so a menu can look right and still not respond where it is drawn.
        /// </summary>
        private static void OwnerGeometryToVirtual(WaylandWindow owner, int geometryX, int geometryY,
                                                   out int virtualX, out int virtualY)
        {
            double scale = owner.GetBackingScale();
            owner.GetClientScreenOriginPixels(out int clientX, out int clientY);
            OwnerDecorationInset(owner, out int insetX, out int insetY);
            virtualX = clientX + (int)Math.Round((geometryX - insetX) * scale);
            virtualY = clientY + (int)Math.Round((geometryY - insetY) * scale);
        }

        /// <summary>Diagnostics: how the owner's decoration geometry actually measures.</summary>
        private static string DescribeOwnerFrame(WaylandWindow owner)
        {
            OwnerDecorationInset(owner, out int ix, out int iy);
            owner.GetClientScreenOriginPixels(out int cx, out int cy);
            return $" | owner inset=({ix},{iy}) caption={owner.CaptionDips():0.#} client=({cx},{cy}) content={owner._contentW}x{owner._contentH}";
        }

        /// <summary>The xdg_surface a popup is parented to: its owner's, whether that owner is a
        /// libdecor toplevel or another popup (a submenu).</summary>
        private IntPtr ParentXdgSurface()
        {
            WaylandWindow? owner = ResolveOwner();
            if (owner is null) return IntPtr.Zero;
            if (owner._xdgSurface != IntPtr.Zero) return owner._xdgSurface;
            if (owner._frame != IntPtr.Zero) return WlDecor.libdecor_frame_get_xdg_surface(owner._frame);
            return IntPtr.Zero;
        }

        /// <summary>
        /// An xdg_positioner placing this popup where WPF asked, expressed the only way Wayland
        /// allows: relative to the owner.
        ///
        /// Three coordinate spaces meet here and the conversion order is the whole difficulty:
        ///
        ///   VIRTUAL   what WPF speaks. A window's virtual origin is its OUTER top-left (Win32
        ///             semantics), which by construction is also its window-geometry origin --
        ///             GetWindowPixelSize and GetClientScreenOriginPixels are built around that.
        ///   CONTENT   the owner's client area, one titlebar below its outer top-left.
        ///   GEOMETRY  what xdg_positioner.set_anchor_rect is specified against.
        ///
        /// So the popup's absolute position is first taken relative to the owner's CLIENT origin,
        /// and only then run through libdecor to land in window-geometry space. Subtracting the
        /// owner's virtual origin instead would already yield a geometry-relative offset, and
        /// translating that a second time adds the titlebar twice -- which is precisely how a combo
        /// dropdown ends up one headerbar too low.
        /// </summary>
        private IntPtr BuildPositioner()
        {
            IntPtr wmBase = WaylandDisplay.XdgWmBase;
            IntPtr positioner = Wl.Construct(wmBase, XDG_WM_BASE_CREATE_POSITIONER, "xdg_positioner",
                Wl.wl_proxy_get_version(wmBase), WlArgument.NewId());

            WaylandWindow? owner = ResolveOwner();
            int relX = 0, relY = 0;
            if (owner is not null)
            {
                VirtualToOwnerGeometry(owner, _virtualX, _virtualY, out relX, out relY);
            }

            WaylandDisplay.LogSink?.Invoke(
                $"popup anchor: virtual=({_virtualX},{_virtualY}) owner={(owner is null ? "none" : $"({owner._virtualX},{owner._virtualY})")} " +
                $"scale={GetBackingScale():0.##} -> geometry=({relX},{relY}) size={_contentW}x{_contentH}"
                + (owner is null ? "" : DescribeOwnerFrame(owner)));

            Wl.Request(positioner, XDG_POSITIONER_SET_ANCHOR_RECT,
                WlArgument.Int(relX), WlArgument.Int(relY), WlArgument.Int(1), WlArgument.Int(1));
            Wl.Request(positioner, XDG_POSITIONER_SET_SIZE,
                WlArgument.Int(Math.Max(1, _contentW)), WlArgument.Int(Math.Max(1, _contentH)));
            Wl.Request(positioner, XDG_POSITIONER_SET_ANCHOR, WlArgument.UInt(XDG_POSITIONER_ANCHOR_TOP_LEFT));
            Wl.Request(positioner, XDG_POSITIONER_SET_GRAVITY, WlArgument.UInt(XDG_POSITIONER_GRAVITY_BOTTOM_RIGHT));
            // Let the compositor keep the popup on-screen. Whatever it decides comes back through
            // xdg_popup.configure and is written into the virtual origin, so sliding is not a
            // correctness problem -- it is only one if we never learn about it.
            Wl.Request(positioner, XDG_POSITIONER_SET_CONSTRAINT_ADJUSTMENT,
                WlArgument.UInt(XDG_POSITIONER_CONSTRAINT_ADJUSTMENT_SLIDE_X |
                                XDG_POSITIONER_CONSTRAINT_ADJUSTMENT_SLIDE_Y |
                                XDG_POSITIONER_CONSTRAINT_ADJUSTMENT_FLIP_Y |
                                XDG_POSITIONER_CONSTRAINT_ADJUSTMENT_RESIZE_Y));
            return positioner;
        }

        private void AttachScaleObjects()
        {
            if (WaylandDisplay.FractionalScaleManager != IntPtr.Zero)
            {
                _fractionalScale = Wl.Construct(WaylandDisplay.FractionalScaleManager,
                    WP_FRACTIONAL_SCALE_MANAGER_GET_FRACTIONAL_SCALE, "wp_fractional_scale_v1", 1,
                    WlArgument.NewId(), WlArgument.Ptr(_surface));
                Wl.wl_proxy_add_listener(_fractionalScale, s_fractionalScaleListener, GCHandle.ToIntPtr(_self));
            }
            if (WaylandDisplay.Viewporter != IntPtr.Zero)
            {
                _viewport = Wl.Construct(WaylandDisplay.Viewporter, WP_VIEWPORTER_GET_VIEWPORT,
                    "wp_viewport", 1, WlArgument.NewId(), WlArgument.Ptr(_surface));
            }
        }

        private static string AppId
        {
            get
            {
                string? name = AppDomain.CurrentDomain.FriendlyName;
                if (string.IsNullOrEmpty(name)) return "dotnet-wpf";
                // GNOME matches the app id against a .desktop file name, so keep it plain.
                return name.Replace(' ', '-');
            }
        }

        /// <summary>
        /// Pump until the compositor has configured the surface. Not an optimisation: a Wayland
        /// surface has no size and no committed role until its first configure is acked, so if
        /// Create returned before that, HwndTarget would hand the handle to the renderer, the first
        /// present would be silently discarded, and the window would never map.
        /// </summary>
        private void PumpUntilVisible()
        {
            for (int i = 0; i < 200 && !_configured && !_destroyed; i++)
                PumpEvents(8);

            if (!_configured)
                WaylandDisplay.LogSink?.Invoke("no configure within ~1.6s; the window may not map.");
        }

        // ---- Listener plumbing ---------------------------------------------------------------

        private static void EnsureStaticListeners()
        {
            if (s_frameInterface != null) return;

            s_frameInterface = (LibdecorFrameInterface*)NativeMemory.AllocZeroed((nuint)sizeof(LibdecorFrameInterface));
            s_frameInterface->configure = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, void>)&OnDecorConfigure;
            s_frameInterface->close = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void>)&OnDecorClose;
            s_frameInterface->commit = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void>)&OnDecorCommit;
            s_frameInterface->dismiss_popup = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, void>)&OnDecorDismissPopup;

            s_xdgSurfaceListener = Wl.Vtable(
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, uint, void>)&OnXdgSurfaceConfigure);
            s_xdgPopupListener = Wl.Vtable(
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, int, int, int, int, void>)&OnPopupConfigure,
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void>)&OnPopupDone,
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, uint, void>)&OnPopupRepositioned);
            s_fractionalScaleListener = Wl.Vtable(
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, uint, void>)&OnPreferredScale);
            s_surfaceListener = Wl.Vtable(
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, void>)&OnSurfaceEnter,
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, void>)&OnSurfaceLeave,
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, int, void>)&OnPreferredBufferScale,
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, uint, void>)&OnPreferredBufferTransform);
        }

        private static WaylandWindow? FromUserData(IntPtr userData)
        {
            if (userData == IntPtr.Zero) return null;
            try
            {
                object? target = GCHandle.FromIntPtr(userData).Target;
                return target as WaylandWindow;
            }
            catch { return null; }
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void OnDecorConfigure(IntPtr frame, IntPtr configuration, IntPtr userData)
        {
            try
            {
                WaylandWindow? w = FromUserData(userData);
                if (w is null) return;

                // No content size in the configuration means "you choose" (the initial configure and
                // any floating-state one), so keep what we have.
                if (!WlDecor.libdecor_configuration_get_content_size(configuration, frame, out int cw, out int ch)
                    || cw <= 0 || ch <= 0)
                {
                    cw = w._contentW;
                    ch = w._contentH;
                }

                if (cw != w._contentW || ch != w._contentH)
                    WaylandDisplay.LogSink?.Invoke($"window 0x{w._surface:x}: compositor configure {cw}x{ch} (was {w._contentW}x{w._contentH})");
                w._contentW = Math.Max(1, cw);
                w._contentH = Math.Max(1, ch);
                if (WlDecor.libdecor_configuration_get_window_state(configuration, out LibdecorWindowState state))
                {
                    if (state != w._windowState)
                        WaylandDisplay.LogSink?.Invoke($"window 0x{w._surface:x}: compositor state {w._windowState} -> {state}");
                    w._windowState = state;
                }

                IntPtr decorState = WlDecor.libdecor_state_new(w._contentW, w._contentH);
                WlDecor.libdecor_frame_commit(frame, decorState, configuration);
                WlDecor.libdecor_state_free(decorState);

                w.ApplyBufferScale();
                w._configured = true;
            }
            catch { }
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void OnDecorClose(IntPtr frame, IntPtr userData)
        {
            try
            {
                WaylandWindow? w = FromUserData(userData);
                if (w is not null) w._closeRequested = true;
            }
            catch { }
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void OnDecorCommit(IntPtr frame, IntPtr userData)
        {
            try
            {
                WaylandWindow? w = FromUserData(userData);
                if (w is not null && w._surface != IntPtr.Zero) Wl.Request(w._surface, WL_SURFACE_COMMIT);
            }
            catch { }
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void OnDecorDismissPopup(IntPtr frame, IntPtr seatName, IntPtr userData) { }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void OnXdgSurfaceConfigure(IntPtr userData, IntPtr xdgSurface, uint serial)
        {
            try
            {
                Wl.Request(xdgSurface, XDG_SURFACE_ACK_CONFIGURE, WlArgument.UInt(serial));
                WaylandWindow? w = FromUserData(userData);
                if (w is null) return;
                w.ApplyBufferScale();
                w._configured = true;
            }
            catch { }
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void OnPopupConfigure(IntPtr userData, IntPtr popup, int x, int y, int width, int height)
        {
            try
            {
                WaylandWindow? w = FromUserData(userData);
                if (w is null) return;

                // THE WRITE-BACK. x/y are where the compositor actually placed the popup relative to
                // its parent, after any constraint sliding or flipping. Folding it into the virtual
                // origin is what keeps hit-testing and ClientToScreen correct for a menu that was
                // nudged to stay on-screen.
                WaylandWindow? owner = w.ResolveOwner();
                int ownerClientX = 0, ownerClientY = 0;
                if (owner is not null)
                {
                    OwnerGeometryToVirtual(owner, x, y, out int vx, out int vy);
                    w._virtualX = vx;
                    w._virtualY = vy;
                    owner.GetClientScreenOriginPixels(out ownerClientX, out ownerClientY);
                }
                if (width > 0 && height > 0)
                {
                    w._contentW = width;
                    w._contentH = height;
                }

                WaylandDisplay.LogSink?.Invoke(
                    $"popup configured: compositor placed it at ({x},{y}) rel. to owner geometry, " +
                    $"size={width}x{height} -> virtual=({w._virtualX},{w._virtualY})"
                    + $" composite-origin=({w._virtualX - ownerClientX},{w._virtualY - ownerClientY})");
            }
            catch { }
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void OnPopupDone(IntPtr userData, IntPtr popup)
        {
            try
            {
                // The compositor dismissed the popup (a click outside, or the parent losing focus).
                WaylandWindow? w = FromUserData(userData);
                if (w is not null) w._closeRequested = true;
            }
            catch { }
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void OnPopupRepositioned(IntPtr userData, IntPtr popup, uint token) { }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void OnPreferredScale(IntPtr userData, IntPtr fractionalScale, uint scale120)
        {
            try
            {
                WaylandWindow? w = FromUserData(userData);
                if (w is null || scale120 == 0) return;
                w._scale120 = (int)scale120;
                w.ApplyBufferScale();
            }
            catch { }
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void OnSurfaceEnter(IntPtr userData, IntPtr surface, IntPtr output)
        {
            try
            {
                WaylandWindow? w = FromUserData(userData);
                if (w is null) return;
                w._onScreen = true;
                w._outputsEntered++;
                w._framePending = false;
                w.DropDialog();   // on screen, so placed: an ordinary window from now on
                lock (WaylandDisplay.Outputs)
                {
                    foreach (WaylandOutput o in WaylandDisplay.Outputs)
                    {
                        if (o.Proxy != output) continue;
                        w._outputScale = Math.Max(1, o.Scale);
                        break;
                    }
                }
                w.ApplyBufferScale();
            }
            catch { }
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void OnSurfaceLeave(IntPtr userData, IntPtr surface, IntPtr output)
        {
            try
            {
                WaylandWindow? w = FromUserData(userData);
                if (w is not null && w._outputsEntered > 0) w._outputsEntered--;
            }
            catch { }
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void OnPreferredBufferScale(IntPtr userData, IntPtr surface, int factor)
        {
            try
            {
                WaylandWindow? w = FromUserData(userData);
                if (w is null || factor <= 0) return;
                w._outputScale = factor;
                w.ApplyBufferScale();
            }
            catch { }
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void OnPreferredBufferTransform(IntPtr userData, IntPtr surface, uint transform) { }

        /// <summary>
        /// Tell the compositor how the buffer we are about to attach relates to the surface's
        /// logical size. This MUST be committed before the renderer attaches its first buffer: a
        /// buffer whose dimensions do not match the declared scale is wl_surface.error.invalid_size,
        /// which is a FATAL protocol error -- it kills the whole connection, not just the window.
        /// </summary>
        private void ApplyBufferScale()
        {
            if (_surface == IntPtr.Zero) return;

            if (_scale120 > 0 && _viewport != IntPtr.Zero)
            {
                // Fractional path: the buffer is device-sized, the surface is logical-sized, and the
                // viewport maps one onto the other. set_buffer_scale stays at 1 -- the two mechanisms
                // are mutually exclusive.
                Wl.Request(_surface, WL_SURFACE_SET_BUFFER_SCALE, WlArgument.Int(1));
                Wl.Request(_viewport, WP_VIEWPORT_SET_DESTINATION,
                    WlArgument.Int(Math.Max(1, _contentW)), WlArgument.Int(Math.Max(1, _contentH)));
            }
            else
            {
                Wl.Request(_surface, WL_SURFACE_SET_BUFFER_SCALE, WlArgument.Int(Math.Max(1, _outputScale)));
            }
        }

        // ---- IPlatformWindow ------------------------------------------------------------------

        public double GetRefreshRateHz() => WaylandDisplay.RefreshRateForSurface(_surface);

        public double GetBackingScale()
        {
            string? force = Environment.GetEnvironmentVariable("WPF_LINUX_FORCE_SCALE");
            if (!string.IsNullOrEmpty(force) &&
                double.TryParse(force, System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out double forced) && forced > 0)
            {
                return forced;
            }

            // A popup defers to its owner, exactly as CocoaWindow does: while it is being positioned
            // it has not entered any output yet, so its own scale would be a guess, and a popup whose
            // DPI disagrees with its owner's renders at the wrong size and lands in the wrong place.
            if (_borderless && _ownerHandle != IntPtr.Zero)
            {
                WaylandWindow? owner = FromHandle(_ownerHandle);
                if (owner is not null && !ReferenceEquals(owner, this)) return owner.GetBackingScale();
            }

            if (_scale120 > 0) return _scale120 / 120.0;
            return Math.Max(1, _outputScale);
        }

        /// <summary>The caption height in DIPs. libdecor draws the headerbar, and its height depends
        /// on the GTK theme and font, so it is measured rather than assumed: translating the content
        /// origin into window-geometry space yields exactly the decoration inset.</summary>
        private double CaptionDips()
        {
            if (_borderless || _chromeless) return 0;
            if (_frame != IntPtr.Zero)
            {
                try
                {
                    WlDecor.libdecor_frame_translate_coordinate(_frame, 0, 0, out _, out int frameY);
                    if (frameY > 0) return frameY;
                }
                catch { }
            }
            return FallbackCaptionDips;
        }

        private void NonClientInsetsDips(out double w, out double h)
        {
            if (_borderless || _chromeless) { w = 0; h = 0; return; }
            w = NonClientWidthDips;
            h = CaptionDips();
        }

        public void SetContentSize(int width, int height)
        {
            LogSize("SetContentSize", width, height);
            _contentW = Math.Max(1, width);
            _contentH = Math.Max(1, height);
            CommitSize();
        }

        public void SetContentSizePixels(int cx, int cy)
        {
            LogSize("SetContentSizePixels", cx, cy);
            double scale = GetBackingScale();
            NonClientInsetsDips(out double ncW, out double ncH);
            _contentW = Math.Max(1, (int)Math.Round(cx / scale - ncW));
            _contentH = Math.Max(1, (int)Math.Round(cy / scale - ncH));
            CommitSize();
        }

        private void LogSize(string what, int w, int h)
        {
            if (WaylandDisplay.LogSink is not { } log) return;
            string[] frames = Environment.StackTrace.Split('\n');
            log($"window 0x{_surface:x}: {what}({w},{h}) content was {_contentW}x{_contentH} chromeless={_chromeless} <- "
                + string.Join(" <- ", System.Linq.Enumerable.Take(System.Linq.Enumerable.Skip(frames, 3), 6)).Replace("\r", "").Replace("   at ", ""));
        }

        private void CommitSize()
        {
            if (_destroyed) return;
            if (_frame != IntPtr.Zero)
            {
                IntPtr state = WlDecor.libdecor_state_new(_contentW, _contentH);
                WlDecor.libdecor_frame_commit(_frame, state, IntPtr.Zero);
                WlDecor.libdecor_state_free(state);
            }
            else if (_xdgPopup != IntPtr.Zero)
            {
                Reposition();
            }
            ApplyBufferScale();
        }

        /// <summary>
        /// Move a popup. Toplevels only record the value -- see the coordinate discussion in the file
        /// header; xdg-shell has no set_position and a client cannot place its own toplevel.
        /// </summary>
        public void SetFrameOrigin(int xPixels, int yPixels)
        {
            _virtualX = xPixels;
            _virtualY = yPixels;
            // moved to the middle of the screen before it is first shown (WPF places a CenterScreen
            // window after making it): the compositor centres it, as it would have at Create
            if (_frame != IntPtr.Zero && !_onScreen && _ownerHandle == IntPtr.Zero && _dialog == IntPtr.Zero && !_gtkModal)
            {
                GetWindowPixelSize(out int w, out int h);
                if (IsCenteredOnScreen(xPixels, yPixels, w, h)) MarkDialogUntilMapped();
            }
            if (_xdgPopup != IntPtr.Zero) Reposition();
        }

        private void Reposition()
        {
            // xdg_popup.reposition arrived in xdg-shell v3; GNOME is well past that, but a compositor
            // below it can only be served by tearing the popup down and rebuilding it, which WPF
            // achieves anyway by destroying and recreating the window.
            if (_xdgPopup == IntPtr.Zero || Wl.wl_proxy_get_version(_xdgPopup) < 3) return;
            try
            {
                IntPtr positioner = BuildPositioner();
                Wl.Request(_xdgPopup, XDG_POPUP_REPOSITION, WlArgument.Ptr(positioner), WlArgument.UInt(++s_repositionToken));
                Wl.Destroy(positioner, XDG_POSITIONER_DESTROY);
            }
            catch { }
        }
        private static uint s_repositionToken;

        public void GetContentSize(out int width, out int height)
        {
            width = _contentW;
            height = _contentH;
        }

        public void GetPixelSize(out int width, out int height)
        {
            double scale = GetBackingScale();
            width = Math.Max(1, (int)Math.Round(_contentW * scale));
            height = Math.Max(1, (int)Math.Round(_contentH * scale));
        }

        public void GetWindowPixelSize(out int width, out int height)
        {
            double scale = GetBackingScale();
            NonClientInsetsDips(out double ncW, out double ncH);
            width = Math.Max(1, (int)Math.Round((_contentW + ncW) * scale));
            height = Math.Max(1, (int)Math.Round((_contentH + ncH) * scale));
        }

        public void GetClientScreenOriginPixels(out int sx, out int sy)
        {
            double scale = GetBackingScale();
            NonClientInsetsDips(out double ncW, out double ncH);
            // The client area sits inside the reported outer window: half the side frame across,
            // the caption down -- the same split the sizing path applies.
            sx = _virtualX + (int)Math.Round(ncW / 2.0 * scale);
            sy = _virtualY + (int)Math.Round(ncH * scale);
        }

        /// <summary>Show/hide the surface (WPF's ShowWindow SW_HIDE/SW_SHOW).</summary>
        /// <remarks>
        /// Wayland has no "hide": a surface is mapped exactly while it has a buffer attached, so
        /// attaching a NULL buffer and committing unmaps it, and the compositor stops showing it and
        /// stops sending it input. The role objects (xdg_surface / libdecor frame) and the handle all
        /// survive, so the window can be shown again — which is the whole point, versus Destroy.
        ///
        /// Showing again only commits: the surface re-maps when the renderer attaches its next buffer,
        /// and the damage below is what asks for that frame. Attaching a buffer here is not possible
        /// (the renderer owns them) and not needed.
        /// </remarks>
        private bool _hidden;   // SetVisible(false): not on screen, and not what is under the pointer

        public void SetVisible(bool visible)
        {
            if (_destroyed || _surface == IntPtr.Zero) return;
            _hidden = !visible;

            if (!visible)
            {
                // off every output now, and shown again only by the next frame (see CanPresent)
                _outputsEntered = 0;
                _framePending = false;
                _shown = false;
                Wl.Request(_surface, WL_SURFACE_ATTACH,
                    WlArgument.Ptr(IntPtr.Zero), WlArgument.Int(0), WlArgument.Int(0));
                Wl.Request(_surface, WL_SURFACE_COMMIT);
            }
            else
            {
                _framePending = false;
                Wl.Request(_surface, WL_SURFACE_DAMAGE,
                    WlArgument.Int(0), WlArgument.Int(0), WlArgument.Int(int.MaxValue), WlArgument.Int(int.MaxValue));
                Wl.Request(_surface, WL_SURFACE_COMMIT);
            }

            WaylandDisplay.Flush();
        }

        /// <summary>
        /// A window that draws its own frame (WindowStyle=None with WindowChrome, a form that takes the
        /// non-client area over): libdecor's headerbar and borders are hidden, so there is one caption,
        /// the window's, as on Windows -- and the window is moved and sized by this layer through the
        /// compositor (<see cref="BeginMoveDrag"/>, <see cref="BeginResizeDrag"/>) from what its own
        /// hit-testing says is there. The content then IS the window: no non-client inset.
        /// </summary>
        /// <summary>
        /// The window belongs to <paramref name="ownerHandle"/> (a form's Owner, a modal dialog's owner): the
        /// compositor keeps it above that window and places it over it, as Windows centres a dialog on its
        /// owner. Without it GNOME put every dialog in the top left corner of the screen.
        /// </summary>
        public void SetOwnerWindow(IntPtr ownerHandle)
        {
            if (_frame == IntPtr.Zero || _destroyed) return;
            WaylandWindow? owner = FromHandle(ownerHandle);
            IntPtr parent = owner is not null && !ReferenceEquals(owner, this) && owner._frame != IntPtr.Zero && !owner._destroyed ? owner._frame : IntPtr.Zero;
            WlDecor.libdecor_frame_set_parent(_frame, parent);
            WaylandDisplay.Flush();
        }

        public void SetChromeless(bool chromeless)
        {
            WaylandDisplay.LogSink?.Invoke($"window 0x{_surface:x}: chromeless {chromeless} (was {_chromeless}, frame {(_frame != IntPtr.Zero)})");
            if (_frame == IntPtr.Zero || chromeless == _chromeless) return;
            // The client area becomes the whole window (WindowChrome's WM_NCCALCSIZE), as CocoaWindow's
            // SetChromeless: the outer size WPF gave the window stays and is now the client size, with no
            // non-client frame emulated around it. Kept as the content size, the frame's caption and side
            // frame were lost from it: a WindowChrome wizard of 800x540 came up 790x503 with its bottom row
            // (Back, Next) cut off. A maximized window is the compositor's to size.
            GetWindowPixelSize(out int outerW, out int outerH);
            _chromeless = chromeless;
            WlDecor.libdecor_frame_set_visibility(_frame, !chromeless);
            if ((_windowState & (LibdecorWindowState.Maximized | LibdecorWindowState.Fullscreen)) == 0) SetContentSizePixels(outerW, outerH);
            else CommitSize();
            WaylandDisplay.Flush();
        }

        /// <summary>DefWindowProc's move loop for SC_MOVE (DragMove, a caption the window draws): the
        /// compositor's interactive move, from the press that holds the pointer.</summary>
        public void BeginMoveDrag()
        {
            if (_frame == IntPtr.Zero || _destroyed) return;
            uint serial = WaylandInput.LastPointerButtonSerial;
            if (serial == 0 || WaylandInput.Seat == IntPtr.Zero) return;
            WlDecor.libdecor_frame_move(_frame, WaylandInput.Seat, serial);
            WaylandDisplay.Flush();
        }

        /// <summary>
        /// WM_NCHITTEST for a point in screen device pixels: the window procedure's answer, HTCLIENT when it
        /// has none. Set by HwndWrapper, which owns the procedure.
        /// </summary>
        public Func<int, int, int>? NonClientHitTest { get; set; }

        private uint _lastCaptionPressTime;

        /// <summary>
        /// A left press on a window that draws its own frame, where the WINDOW says what is there, as
        /// Windows asks it (WM_NCHITTEST). On its caption the press is the system's -- WM_NCLBUTTONDOWN,
        /// which DefWindowProc turns into a move, and a double-click into maximize or restore -- and on its
        /// sizing border a resize; WPF never sees those. A window whose own chrome hooks the press first
        /// (WindowChrome's) answers HTCLIENT here, and keeps it. <paramref name="x"/>, <paramref name="y"/>:
        /// surface-local device pixels.
        /// </summary>
        internal bool NonClientPress(int x, int y, uint time)
        {
            if (!_chromeless || _frame == IntPtr.Zero || NonClientHitTest == null) return false;
            int hit;
            try { hit = NonClientHitTest(_virtualX + x, _virtualY + y); }
            catch { return false; }
            WaylandDisplay.LogSink?.Invoke($"window 0x{_handle:x}: press at ({x},{y}) of {_contentW}x{_contentH} -> WM_NCHITTEST {hit}");
            const int HTCAPTION = 2;
            if (hit == HTCAPTION)
            {
                bool doubleClick = _lastCaptionPressTime != 0 && time - _lastCaptionPressTime <= 500;
                _lastCaptionPressTime = doubleClick ? 0 : time;
                if (doubleClick)
                {
                    if ((_windowState & LibdecorWindowState.Maximized) != 0) WlDecor.libdecor_frame_unset_maximized(_frame);
                    else WlDecor.libdecor_frame_set_maximized(_frame);
                    WaylandDisplay.Flush();
                }
                else BeginMoveDrag();
                return true;
            }
            _lastCaptionPressTime = 0;
            return hit >= 10 && hit <= 17 && BeginResizeDrag(hit, 0, 0, 0, 0);
        }

        /// <summary>A window drawing its own frame is sized from its edges here; libdecor's borders size the others.</summary>
        public bool ResizesFromEdges => _frame != IntPtr.Zero && _chromeless;

        /// <summary>DefWindowProc's size loop for a press on the border (HTLEFT..HTBOTTOMRIGHT): the
        /// compositor's interactive resize from that edge. The minimum and maximum sizes are libdecor's
        /// content limits, which the compositor holds the resize to.</summary>
        public bool BeginResizeDrag(int hitTest, int minWidth, int minHeight, int maxWidth, int maxHeight)
        {
            if (!ResizesFromEdges || _destroyed) return false;
            int edge = hitTest switch
            {
                12 => 1,   // HTTOP
                15 => 2,   // HTBOTTOM
                10 => 3,   // HTLEFT
                13 => 4,   // HTTOPLEFT
                16 => 5,   // HTBOTTOMLEFT
                11 => 6,   // HTRIGHT
                14 => 7,   // HTTOPRIGHT
                17 => 8,   // HTBOTTOMRIGHT
                _ => 0,
            };
            uint serial = WaylandInput.LastPointerButtonSerial;
            if (edge == 0 || serial == 0 || WaylandInput.Seat == IntPtr.Zero) return false;
            double scale = GetBackingScale();
            if (minWidth > 0 || minHeight > 0)
                WlDecor.libdecor_frame_set_min_content_size(_frame, Math.Max(1, (int)Math.Round(minWidth / scale)), Math.Max(1, (int)Math.Round(minHeight / scale)));
            if (maxWidth > 0 || maxHeight > 0)
                WlDecor.libdecor_frame_set_max_content_size(_frame, maxWidth > 0 ? (int)Math.Round(maxWidth / scale) : 0, maxHeight > 0 ? (int)Math.Round(maxHeight / scale) : 0);
            WlDecor.libdecor_frame_resize(_frame, WaylandInput.Seat, serial, edge);
            WaylandDisplay.Flush();
            return true;
        }

        // ---- the popup stack ------------------------------------------------------------------
        //
        // A popup cannot outlive the popup it is placed against: destroying one with a popup of its own
        // still open is a protocol error that kills the connection (xdg_wm_base.not_the_topmost_popup),
        // and every window with it. WPF destroys windows in its own order (ElementHost's parking window
        // goes while the hosted window made on it stays), so the popups placed against one that goes --
        // and theirs -- lose their role first, newest first: their surfaces stay, unmapped, as a window
        // whose owner closed is hidden on Windows. A later SIBLING is left alone: none of these popups
        // takes a grab, and only a grab chain is ordered -- dropping siblings took a context menu down
        // with the tooltip that happened to be closing as it opened.

        private WaylandWindow? _popupParent;
        private long _popupSeq;
        private static long s_popupSeq;

        /// <summary>This popup is placed against <paramref name="other"/>, directly or through popups of popups.</summary>
        private bool IsPlacedAgainst(WaylandWindow other)
        {
            WaylandWindow w = this;
            for (int i = 0; i < 64 && w._popupParent is { } p; i++)
            {
                if (ReferenceEquals(p, other)) return true;
                w = p;
            }
            return false;
        }

        private WaylandWindow RootToplevel()
        {
            WaylandWindow w = this;
            for (int i = 0; i < 64 && w._popupParent is { } p; i++) w = p;
            return w;
        }

        /// <summary>Takes the popup role off every popup placed against <paramref name="root"/> (directly
        /// or through others) made after <paramref name="after"/> (0: all of them), newest first, so that
        /// <paramref name="root"/> can go.</summary>
        private static void DropPopupsAbove(WaylandWindow root, long after)
        {
            List<WaylandWindow> above = new();
            lock (s_lock)
            {
                foreach (WaylandWindow w in s_zOrder)
                    if (!w._destroyed && w._xdgPopup != IntPtr.Zero && w._popupSeq > after && w.IsPlacedAgainst(root))
                        above.Add(w);
            }
            above.Sort((a, b) => b._popupSeq.CompareTo(a._popupSeq));
            foreach (WaylandWindow w in above) w.DropPopupRole();
        }

        private void DropPopupRole()
        {
            WaylandDisplay.LogSink?.Invoke($"popup 0x{_surface:x}: role dropped (a popup below it is going)");
            try
            {
                if (_xdgPopup != IntPtr.Zero) { Wl.Destroy(_xdgPopup, XDG_POPUP_DESTROY); _xdgPopup = IntPtr.Zero; }
                if (_xdgSurface != IntPtr.Zero) { Wl.Destroy(_xdgSurface, XDG_SURFACE_DESTROY); _xdgSurface = IntPtr.Zero; }
            }
            catch { }
            _configured = false;
        }

        public void Destroy()
        {
            if (_destroyed) return;
            // what lies above it goes first (see the popup stack)
            if (_xdgPopup != IntPtr.Zero) DropPopupsAbove(this, _popupSeq);
            else if (_frame != IntPtr.Zero) DropPopupsAbove(this, 0);
            _destroyed = true;

            lock (s_lock)
            {
                s_byHandle.Remove(_handle);
                s_bySurface.Remove(_surface);
                s_zOrder.Remove(this);
            }

            try
            {
                // Role objects first, then the surface: destroying a wl_surface that still has a
                // role attached is a protocol error.
                if (_xdgPopup != IntPtr.Zero) { Wl.Destroy(_xdgPopup, XDG_POPUP_DESTROY); _xdgPopup = IntPtr.Zero; }
                if (_xdgSurface != IntPtr.Zero) { Wl.Destroy(_xdgSurface, XDG_SURFACE_DESTROY); _xdgSurface = IntPtr.Zero; }
                DropDialog();
                if (_gtkSurface != IntPtr.Zero) { Wl.wl_proxy_destroy(_gtkSurface); _gtkSurface = IntPtr.Zero; }   // v1 has no destructor
                if (_frame != IntPtr.Zero) { WlDecor.libdecor_frame_unref(_frame); _frame = IntPtr.Zero; }
                if (_fractionalScale != IntPtr.Zero) { Wl.Destroy(_fractionalScale, WP_FRACTIONAL_SCALE_DESTROY); _fractionalScale = IntPtr.Zero; }
                if (_viewport != IntPtr.Zero) { Wl.Destroy(_viewport, WP_VIEWPORT_DESTROY); _viewport = IntPtr.Zero; }
                if (_surface != IntPtr.Zero) { Wl.Destroy(_surface, WL_SURFACE_DESTROY); _surface = IntPtr.Zero; }
                WaylandDisplay.Flush();
            }
            catch { }

            if (_self.IsAllocated) _self.Free();
        }

        // ---- Statics the dispatch sites use ----------------------------------------------------

        public static WaylandWindow? FromHandle(IntPtr handle)
        {
            if (handle == IntPtr.Zero) return null;
            lock (s_lock)
                return s_byHandle.TryGetValue(handle, out WaylandWindow? w) ? w
                     : s_bySurface.TryGetValue(handle, out w) ? w : null;   // the wl_surface* names it too
        }

        internal static double ScaleForSurface(IntPtr surface) => FromHandle(surface)?.GetBackingScale() ?? 1.0;

        public static IntPtr MouseCaptureHandle { get; set; }

        /// <summary>
        /// Topmost window whose virtual bounds contain the point. Wayland offers no stacking query at
        /// all -- there is no equivalent of NSWindow's orderedIndex -- so creation order is tracked
        /// explicitly and searched back to front.
        /// </summary>
        public static IntPtr HitTest(int x, int y)
        {
            IntPtr top = HitTest(x, y, null);
            // the window the toplevel's own owner puts at that point inside it: a WinForms form's hosted
            // WPF tree (ElementHost), whose window lies UNDER the form's in creation order but is its child,
            // as WindowFromPoint finds the deepest child on Windows -- or WPF's mouse hit-testing found the
            // form's window, no source of its own, and the hosted tree's controls never saw the mouse
            if (top != IntPtr.Zero && ChildWindowAt is { } child && child(top, x, y) is var inner && inner != IntPtr.Zero)
                return inner;
            return top;
        }

        /// <summary>Installed by a host that puts windows of its own inside a toplevel (WinForms'): the window
        /// at a point of the virtual screen inside that toplevel, or zero.</summary>
        public static Func<IntPtr, int, int, IntPtr>? ChildWindowAt { get; set; }

        /// <summary>The topmost window at a point of the virtual screen that <paramref name="accept"/> takes
        /// (by handle): a host looking for one kind of window among those stacked there -- the WPF trees it
        /// composites into its own surface, which lie under it in creation order.</summary>
        public static IntPtr HitTest(int x, int y, Func<IntPtr, bool>? accept)
        {
            lock (s_lock)
            {
                for (int i = s_zOrder.Count - 1; i >= 0; i--)
                {
                    WaylandWindow w = s_zOrder[i];
                    if (w._destroyed || w._hidden) continue;
                    w.GetPixelSize(out int pw, out int ph);
                    w.GetClientScreenOriginPixels(out int ox, out int oy);
                    if (x >= ox && x < ox + pw && y >= oy && y < oy + ph && (accept is null || accept(w._handle))) return w._handle;
                }
            }
            return IntPtr.Zero;
        }

        public static bool GetPrimaryScreenPixels(
            out int monLeft, out int monTop, out int monRight, out int monBottom,
            out int workLeft, out int workTop, out int workRight, out int workBottom)
        {
            bool ok = WaylandDisplay.GetPrimaryScreenPixels(out monLeft, out monTop, out monRight, out monBottom);
            // Wayland exposes no work area: GNOME's top bar is a layer surface clients cannot see.
            workLeft = monLeft; workTop = monTop; workRight = monRight; workBottom = monBottom;
            return ok;
        }

        /// <summary>Whether the calling thread is the one that reads the compositor connection (or nothing
        /// has claimed it yet): the thread a window's requests that wait for the compositor must come from.</summary>
        public static bool IsUiThread => WaylandDisplay.Display == IntPtr.Zero || WaylandDisplay.IsPumpThread;

        /// <summary>Makes the calling thread the one that reads the compositor connection, before any window
        /// exists: a stack that starts its UI thread there (the WinForms driver on the app's main thread)
        /// claims it before a thread of the app's (a splash screen's) makes the first window. False when
        /// there is no compositor, or another live thread already has it.</summary>
        public static bool ClaimUiThread()
        {
            try
            {
                EnsureApplication();
                WaylandDisplay.ClaimUiThread();
                return true;
            }
            catch (Exception) { return false; }
        }

        /// <summary>The primary output's scale (device pixels per logical pixel), before there is a window
        /// to ask: what a stack laying out at the screen's DPI takes for it (WPF_LINUX_FORCE_SCALE wins).</summary>
        public static double PrimaryScale()
        {
            string? force = Environment.GetEnvironmentVariable("WPF_LINUX_FORCE_SCALE");
            if (!string.IsNullOrEmpty(force) &&
                double.TryParse(force, System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out double forced) && forced > 0)
                return forced;
            EnsureApplication();
            lock (WaylandDisplay.Outputs)
                return WaylandDisplay.Outputs.Count > 0 ? Math.Max(1, WaylandDisplay.Outputs[0].Scale) : 1;
        }

        public static void SetTitle(IntPtr handle, string title)
        {
            WaylandWindow? w = FromHandle(handle);
            if (w is null) return;
            w._title = title ?? string.Empty;
            if (w._frame != IntPtr.Zero) WlDecor.libdecor_frame_set_title(w._frame, w._title);
        }

        public static void SetCursor(string name) => WaylandCursor.SetCursor(name);

        /// <summary>A cursor of the app's own (a .cur or .ani): its picture, BGRA straight alpha, and hot spot;
        /// <paramref name="key"/> names it, so that it is made into a buffer once.</summary>
        public static void SetCustomCursor(int key, byte[] bgra, int width, int height, int hotspotX, int hotspotY) =>
            WaylandCursor.SetCustomCursor(key, bgra, width, height, hotspotX, hotspotY);

        /// <summary>
        /// The pointer's position in the virtual screen space, or false when it is not over any of
        /// our windows. Wayland reports pointer coordinates surface-locally and never globally, so
        /// this composes the last motion with the owning window's virtual client origin -- the same
        /// space ClientToScreen reports, which is what makes the answer consistent.
        /// </summary>
        public static bool TryGetPointerPosition(out int x, out int y)
        {
            x = 0;
            y = 0;
            WaylandWindow? w = FromHandle(WaylandInput.FocusSurface);
            if (w is null) return false;

            double scale = w.GetBackingScale();
            w.GetClientScreenOriginPixels(out int clientX, out int clientY);
            x = clientX + (int)Math.Round(WaylandInput.PointerSurfaceX * scale);
            y = clientY + (int)Math.Round(WaylandInput.PointerSurfaceY * scale);
            return true;
        }

        /// <summary>
        /// Minimize/maximize/restore, via libdecor (which owns the toplevel). The argument is the
        /// SW_* value WPF asked for.
        /// </summary>
        /// <remarks>
        /// The values matched here used to be 1 and 2, which are SW_NORMAL and SW_SHOWMINIMIZED --
        /// so "show normally" minimized the window and "minimize" maximized it, and the maximize WPF
        /// actually sends (SW_SHOWMAXIMIZED, 3) fell through to unset_maximized. It is now the
        /// IPlatformWindow member every head implements, against the real constants.
        /// </remarks>
        public void SetWindowState(int state)
        {
            WaylandDisplay.LogSink?.Invoke($"window 0x{_handle:x}: state SW {state}");
            if (_frame == IntPtr.Zero) return;
            switch (state)
            {
                case 2:   // SW_SHOWMINIMIZED
                case 6:   // SW_MINIMIZE
                case 7:   // SW_SHOWMINNOACTIVE
                    WlDecor.libdecor_frame_set_minimized(_frame);
                    break;
                case 3:   // SW_SHOWMAXIMIZED / SW_MAXIMIZE
                    WlDecor.libdecor_frame_set_maximized(_frame);
                    break;
                case 1:   // SW_NORMAL
                case 9:   // SW_RESTORE
                    WlDecor.libdecor_frame_unset_maximized(_frame);
                    break;
            }
            // now: a request left in the connection's buffer waited for whatever was sent next, and a
            // window whose content did not change (a minimize) sent nothing
            WaylandDisplay.Flush();
        }

        public static bool IsMaximized(IntPtr handle)
            => (FromHandle(handle)?._windowState & LibdecorWindowState.Maximized) != 0;

        /// <summary>
        /// The compositor's own answer, in SW_* terms -- see IPlatformWindow.GetWindowState for why
        /// the framework needs it.
        /// </summary>
        /// <remarks>
        /// Maximized or normal, never minimized: xdg-shell has no minimized STATE. A client can ask
        /// to be minimized (xdg_toplevel.set_minimized) and the compositor never reports back that it
        /// happened, because from the protocol's point of view a minimized window is simply one the
        /// compositor has stopped sending frame callbacks to.
        /// </remarks>
        public int GetWindowState()
        {
            const int SwNormal = 1, SwShowMaximized = 3;
            return (_windowState & LibdecorWindowState.Maximized) != 0 ? SwShowMaximized : SwNormal;
        }

        // ---- The pump --------------------------------------------------------------------------

        /// <summary>
        /// Read and dispatch compositor events for up to <paramref name="maxMilliseconds"/>, then
        /// reconcile. The mac backend's PumpEvents equivalent, and used for the same jobs: the
        /// bounded pump during window creation, and nested pumps inside modal waits.
        /// </summary>
        public static void PumpEvents(int maxMilliseconds)
        {
            if (!WaylandDisplay.IsActive) return;
            WaylandDisplay.ReadEvents(maxMilliseconds);
            AfterDispatch();
        }

        /// <summary>
        /// Turn the state the listeners recorded into the events WPF expects. Split out from
        /// PumpEvents so the BLOCKING half can live in the dispatcher run loop (which owns the poll
        /// set) while this reconciliation half still runs on every pass -- the same division
        /// CocoaWindow.DetectResizes has.
        /// </summary>
        public static void AfterDispatch()
        {
            if (!WaylandDisplay.IsActive) return;

            WaylandInput.PumpKeyRepeat();

            // A Clipboard.SetText issued before the user had touched the window could not be
            // published (the compositor validates set_selection against a real input serial), so it
            // was held back; publish it as soon as one exists.
            WaylandClipboard.FlushDeferred();

            WaylandWindow[] windows;
            lock (s_lock) windows = s_zOrder.ToArray();

            foreach (WaylandWindow w in windows)
            {
                if (w._destroyed) continue;
                try { w.Reconcile(); } catch { }
            }
        }

        private void Reconcile()
        {
            double scale = GetBackingScale();
            if (_lastReportedScale != 0 && Math.Abs(scale - _lastReportedScale) > 0.001)
            {
                _lastReportedScale = scale;
                ScaleChanged?.Invoke(scale);
            }
            else if (_lastReportedScale == 0)
            {
                _lastReportedScale = scale;
            }

            if (_contentW != _lastReportedW || _contentH != _lastReportedH)
            {
                _lastReportedW = _contentW;
                _lastReportedH = _contentH;
                Resized?.Invoke(_contentW, _contentH);
            }

            // The compositor's activation (xdg_toplevel's ACTIVATED state, through libdecor): the window
            // with the keyboard, as Windows' active window -- WM_ACTIVATE, Form.Activated, the active
            // caption. Popups have no such state; they never take activation, as on Windows.
            if (_frame != IntPtr.Zero)
            {
                bool active = (_windowState & LibdecorWindowState.Active) != 0;
                if (active != _lastReportedActive)
                {
                    _lastReportedActive = active;
                    ActiveChanged?.Invoke(active);
                }
            }

            if (_closeRequested)
            {
                _closeRequested = false;
                Closed?.Invoke(_handle);
            }
        }

        private bool _lastReportedActive;

        /// <summary>The window became the active one (true), or stopped being it.</summary>
        public event Action<bool>? ActiveChanged;

        /// <summary>Whether the compositor has this window active.</summary>
        public bool IsActive => _frame != IntPtr.Zero && (_windowState & LibdecorWindowState.Active) != 0;

        /// <summary>Milliseconds until the pump must wake regardless of compositor traffic (key
        /// repeat), or -1 when nothing is pending.</summary>
        public static int NextDeadlineMs() => WaylandInput.NextDeadlineMs();

        // ---- input for a stack with no WPF window of its own (the WinForms host) ----------------
        //
        // The same events WPF's input providers take (WaylandInput.MouseInput/KeyInput), in a public
        // shape: a surface-local pointer in device pixels, and keys as Windows' virtual keys, by the
        // mapping WPF uses (WaylandInput.VirtualKeyOfKeysym) so both stacks agree on every key.

        /// <param name="Kind">0 move, 1 button down, 2 button up, 3 wheel, 4 the pointer left the surface.</param>
        /// <param name="Button">1 left, 2 right, 3 middle, 4 back (X1), 5 forward (X2); 0 for the others.</param>
        /// <param name="Wheel">Vertical wheel in WHEEL_DELTA units (120 a notch), positive away from the user.</param>
        public readonly record struct MouseMessage(IntPtr Window, int Kind, int Button, int X, int Y, int Wheel, int WheelHorizontal, int TimestampMs);

        public readonly record struct KeyMessage(IntPtr Window, bool IsDown, int VirtualKey, string? Characters, bool IsRepeat,
                                                 bool Shift, bool Ctrl, bool Alt, int TimestampMs, uint Keysym);

        private static Action<MouseMessage>? s_mouseInput;
        private static Action<KeyMessage>? s_keyInput;
        private static bool s_forwarding;

        public static event Action<MouseMessage>? MouseInput
        {
            add { EnsureForwarding(); s_mouseInput += value; }
            remove { s_mouseInput -= value; }
        }

        public static event Action<KeyMessage>? KeyInput
        {
            add { EnsureForwarding(); s_keyInput += value; }
            remove { s_keyInput -= value; }
        }

        private static void EnsureForwarding()
        {
            if (s_forwarding) return;
            s_forwarding = true;
            WaylandInput.MouseInput += m =>
            {
                int kind = m.Kind switch
                {
                    WaylandMouseKind.Move => 0,
                    WaylandMouseKind.ButtonDown => 1,
                    WaylandMouseKind.ButtonUp => 2,
                    WaylandMouseKind.Wheel => 3,
                    _ => 4,
                };
                int button = m.Button switch
                {
                    WaylandInput.BTN_LEFT => 1,
                    WaylandInput.BTN_RIGHT => 2,
                    WaylandInput.BTN_MIDDLE => 3,
                    WaylandInput.BTN_SIDE => 4,
                    WaylandInput.BTN_EXTRA => 5,
                    _ => 0,
                };
                s_mouseInput?.Invoke(new MouseMessage(m.Surface, kind, button, m.X, m.Y, m.Wheel, m.WheelHorizontal, m.TimestampMs));
            };
            WaylandInput.KeyInput += k => s_keyInput?.Invoke(new KeyMessage(k.Surface, k.IsDown, WaylandInput.VirtualKeyOfKeysym(k.Keysym),
                k.Characters, k.IsRepeat, (k.Modifiers & WaylandModifiers.Shift) != 0, (k.Modifiers & WaylandModifiers.Control) != 0,
                (k.Modifiers & WaylandModifiers.Alt) != 0, k.TimestampMs, k.Keysym));
        }

        /// <summary>A pointer event for a WPF window that the compositor cannot deliver to itself -- one
        /// whose picture another stack composites into its own window (an ElementHost's tree in a form):
        /// the host hands it what landed over it, as the compositor would. Kinds and buttons as
        /// <see cref="MouseMessage"/>; <paramref name="x"/>, <paramref name="y"/> local device pixels.</summary>
        public static void DeliverMouse(IntPtr window, int kind, int button, int x, int y, int wheel)
        {
            var k = kind switch { 0 => WaylandMouseKind.Move, 1 => WaylandMouseKind.ButtonDown, 2 => WaylandMouseKind.ButtonUp, 3 => WaylandMouseKind.Wheel, _ => WaylandMouseKind.Leave };
            int b = button switch { 1 => WaylandInput.BTN_LEFT, 2 => WaylandInput.BTN_RIGHT, 3 => WaylandInput.BTN_MIDDLE, 4 => WaylandInput.BTN_SIDE, 5 => WaylandInput.BTN_EXTRA, _ => 0 };
            WaylandInput.InjectMouse(window, k, b, x, y, wheel);
        }

        /// <summary>A key for such a window (see <see cref="DeliverMouse"/>), as the compositor reported it.</summary>
        public static void DeliverKey(IntPtr window, KeyMessage key)
            => WaylandInput.InjectKey(window, key.IsDown, key.Keysym, key.Characters);

        /// <summary>
        /// For a message loop on the pump thread that is not WPF's (WinForms'): reads and dispatches what
        /// the compositor sent within <paramref name="timeoutMilliseconds"/>, reconciles (sizes, scale,
        /// activation, close requests, key repeat), then runs the thread's WPF work -- an ElementHost's
        /// tree -- which nothing else runs there.
        /// </summary>
        public static void RunLoop(int timeoutMilliseconds)
        {
            if (WaylandDisplay.IsActive && WaylandDisplay.IsPumpThread)
            {
                PumpEvents(timeoutMilliseconds);   // reads, dispatches, reconciles
            }
            else if (timeoutMilliseconds > 0)
            {
                System.Threading.Thread.Sleep(timeoutMilliseconds);
            }
            System.Windows.Threading.Dispatcher.FromThread(System.Threading.Thread.CurrentThread)?.ServeForeignLoop(0, null);
        }
    }
}
