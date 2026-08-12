// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The Linux/Wayland implementation of IPlatformWindow -- the sibling of CocoaWindow, UIKitWindow,
// AndroidWindow and BrowserWindow, and deliberately shaped like CocoaWindow so the change at every
// dispatch site is symmetric with the macOS one already there.
//
// The window handle is the wl_surface*. It is stable for the window's lifetime (unlike Android's
// Surface, which is destroyed and recreated under a live window, hence that backend's synthetic
// handles), and it is exactly what WGPUSurfaceSourceWaylandSurface wants, so the handle WPF passes
// around IS the thing the renderer builds on.
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

        private IntPtr _surface;            // wl_surface* -- THE HANDLE
        private IntPtr _frame;              // libdecor_frame* (toplevels)
        private IntPtr _xdgSurface;         // popups only
        private IntPtr _xdgPopup;           // popups only
        private IntPtr _positioner;         // transient, during (re)positioning
        private IntPtr _viewport;
        private IntPtr _fractionalScale;
        private GCHandle _self;

        private bool _borderless;
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

        public IntPtr Handle => _surface;
        public bool IsBorderless => _borderless;

        /// <summary>
        /// False once the compositor tells us the surface is not being shown. libdecor forwards
        /// xdg_toplevel's SUSPENDED state, which exists for exactly this: it means "nobody can see
        /// this, stop drawing". Presenting anyway would block the UI thread on a Fifo swap chain
        /// (see NativePlatform.IsWindowVisible).
        /// </summary>
        internal bool IsVisible => !_destroyed && (_windowState & LibdecorWindowState.Suspended) == 0;

        // ---- Creation -----------------------------------------------------------------------

        public static void EnsureApplication()
        {
            WaylandDisplay.SurfaceScaleQuery ??= static s => FromHandle(s)?.GetBackingScale() ?? 1.0;
            // A borderless window (popup, menu, tooltip) is not opaque: its rounded chrome and drop
            // shadow have to composite over whatever is behind them.
            WaylandDisplay.WindowOpaqueQuery ??= static s => !(FromHandle(s)?.IsBorderless ?? false);
            WaylandDisplay.WindowOriginQuery ??= GetOriginWithinOwner;
            WaylandDisplay.SurfaceScreenOriginQuery ??= static (IntPtr s, out int x, out int y) =>
            {
                x = y = 0;
                FromHandle(s)?.GetClientScreenOriginPixels(out x, out y);
            };
            WaylandDisplay.WindowVisibleQuery ??= static h => FromHandle(h)?.IsVisible ?? true;
            WaylandDisplay.EnsureInitialized();
        }

        /// <summary>
        /// A popup's top-left corner in device pixels relative to its owner's CONTENT origin. Used
        /// when the popup is drawn INTO the owner's surface rather than presenting its own -- which
        /// on Linux is the normal case, because the GL backend advertises no premultiplied
        /// composite-alpha mode (see NativePlatform.PopupsShareOwnerSurface).
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
            EnsureStaticListeners();

            _borderless = borderless;
            _ownerHandle = owner;
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
                s_byHandle[_surface] = this;
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
            WlDecor.libdecor_frame_map(_frame);
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
            WaylandWindow? owner = FromHandle(_ownerHandle);
            if (owner is not null && !ReferenceEquals(owner, this)) return owner;

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
        /// For a libdecor toplevel that window geometry is the CONTENT area. The decorations are
        /// separate subsurfaces that sit outside it, so the geometry origin coincides with the top
        /// left of the client area and the conversion is simply "relative to the client origin".
        ///
        /// It is tempting to run the point through libdecor_frame_translate_coordinate first, since
        /// that converts content coordinates into libdecor's decorated-FRAME space. That is a
        /// different space from the window geometry, and using it puts every popup exactly one
        /// titlebar too low -- which is how this was found.
        /// </summary>
        private static void VirtualToOwnerGeometry(WaylandWindow owner, int virtualX, int virtualY,
                                                   out int geometryX, out int geometryY)
        {
            double scale = owner.GetBackingScale();
            owner.GetClientScreenOriginPixels(out int clientX, out int clientY);
            geometryX = (int)Math.Round((virtualX - clientX) / scale);
            geometryY = (int)Math.Round((virtualY - clientY) / scale);
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
            virtualX = clientX + (int)Math.Round(geometryX * scale);
            virtualY = clientY + (int)Math.Round(geometryY * scale);
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

                w._contentW = Math.Max(1, cw);
                w._contentH = Math.Max(1, ch);
                if (WlDecor.libdecor_configuration_get_window_state(configuration, out LibdecorWindowState state))
                    w._windowState = state;

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
        private static void OnSurfaceLeave(IntPtr userData, IntPtr surface, IntPtr output) { }

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
            if (_borderless) return 0;
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
            if (_borderless) { w = 0; h = 0; return; }
            w = NonClientWidthDips;
            h = CaptionDips();
        }

        public void SetContentSize(int width, int height)
        {
            _contentW = Math.Max(1, width);
            _contentH = Math.Max(1, height);
            CommitSize();
        }

        public void SetContentSizePixels(int cx, int cy)
        {
            double scale = GetBackingScale();
            NonClientInsetsDips(out double ncW, out double ncH);
            _contentW = Math.Max(1, (int)Math.Round(cx / scale - ncW));
            _contentH = Math.Max(1, (int)Math.Round(cy / scale - ncH));
            CommitSize();
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
        public void SetVisible(bool visible)
        {
            if (_destroyed || _surface == IntPtr.Zero) return;

            if (!visible)
            {
                Wl.Request(_surface, WL_SURFACE_ATTACH,
                    WlArgument.Ptr(IntPtr.Zero), WlArgument.Int(0), WlArgument.Int(0));
                Wl.Request(_surface, WL_SURFACE_COMMIT);
            }
            else
            {
                Wl.Request(_surface, WL_SURFACE_DAMAGE,
                    WlArgument.Int(0), WlArgument.Int(0), WlArgument.Int(int.MaxValue), WlArgument.Int(int.MaxValue));
                Wl.Request(_surface, WL_SURFACE_COMMIT);
            }

            WaylandDisplay.Flush();
        }

        /// <summary>Not wired yet: the Wayland equivalent is xdg_toplevel.move, which needs the seat
        /// and the serial of the button press that started the drag. Left as a no-op rather than
        /// guessed at, so a caption drag simply does nothing here instead of moving the wrong window.</summary>
        public void BeginMoveDrag() { }

        public void Destroy()
        {
            if (_destroyed) return;
            _destroyed = true;

            lock (s_lock)
            {
                s_byHandle.Remove(_surface);
                s_zOrder.Remove(this);
            }

            try
            {
                // Role objects first, then the surface: destroying a wl_surface that still has a
                // role attached is a protocol error.
                if (_xdgPopup != IntPtr.Zero) { Wl.Destroy(_xdgPopup, XDG_POPUP_DESTROY); _xdgPopup = IntPtr.Zero; }
                if (_xdgSurface != IntPtr.Zero) { Wl.Destroy(_xdgSurface, XDG_SURFACE_DESTROY); _xdgSurface = IntPtr.Zero; }
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
            lock (s_lock) return s_byHandle.TryGetValue(handle, out WaylandWindow? w) ? w : null;
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
            lock (s_lock)
            {
                for (int i = s_zOrder.Count - 1; i >= 0; i--)
                {
                    WaylandWindow w = s_zOrder[i];
                    if (w._destroyed) continue;
                    w.GetPixelSize(out int pw, out int ph);
                    w.GetClientScreenOriginPixels(out int ox, out int oy);
                    if (x >= ox && x < ox + pw && y >= oy && y < oy + ph) return w._surface;
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

        public static void SetTitle(IntPtr handle, string title)
        {
            WaylandWindow? w = FromHandle(handle);
            if (w is null) return;
            w._title = title ?? string.Empty;
            if (w._frame != IntPtr.Zero) WlDecor.libdecor_frame_set_title(w._frame, w._title);
        }

        public static void SetCursor(string name) => WaylandCursor.SetCursor(name);

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

        /// <summary>Minimize/maximize/restore, via libdecor (which owns the toplevel).</summary>
        public static void SetWindowState(IntPtr handle, int state)
        {
            WaylandWindow? w = FromHandle(handle);
            if (w is null || w._frame == IntPtr.Zero) return;
            switch (state)
            {
                case 1: WlDecor.libdecor_frame_set_minimized(w._frame); break;   // SW_MINIMIZE
                case 2: WlDecor.libdecor_frame_set_maximized(w._frame); break;   // SW_MAXIMIZE
                default: WlDecor.libdecor_frame_unset_maximized(w._frame); break;
            }
        }

        public static bool IsMaximized(IntPtr handle)
            => (FromHandle(handle)?._windowState & LibdecorWindowState.Maximized) != 0;

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

            if (_closeRequested)
            {
                _closeRequested = false;
                Closed?.Invoke(_surface);
            }
        }

        /// <summary>Milliseconds until the pump must wake regardless of compositor traffic (key
        /// repeat), or -1 when nothing is pending.</summary>
        public static int NextDeadlineMs() => WaylandInput.NextDeadlineMs();
    }
}
