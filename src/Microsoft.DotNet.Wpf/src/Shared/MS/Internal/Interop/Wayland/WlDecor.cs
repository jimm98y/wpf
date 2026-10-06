// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// libdecor: client-side window decorations for Wayland toplevels.
//
// Wayland has no server-side decorations on GNOME -- mutter does not implement
// xdg-decoration's server-side mode, so a plain xdg_toplevel is an undecorated rectangle with no
// titlebar, no close button, and no way to move or resize it. Every request the user would make
// through window chrome (drag the titlebar, grab an edge, double-click to maximize, the window
// menu) has to be drawn and driven by the application. libdecor is the library the Wayland
// ecosystem uses for exactly that, and Ubuntu ships it WITH the GTK plugin, so our windows get the
// same Adwaita headerbar as every other GNOME app, following the system theme, for free.
//
// It also owns the toplevel's xdg_surface/xdg_toplevel pair and the whole configure/ack_configure
// handshake -- which is why WlProtocols never has to construct a toplevel. What libdecor does NOT
// cover is popups, so those go through our own xdg_wm_base; libdecor_frame_get_xdg_surface hands
// us the parent an xdg_popup needs, and libdecor_frame_translate_coordinate converts a
// content-relative point into the window-geometry space xdg_positioner anchors against.
//
// Callbacks are static [UnmanagedCallersOnly] methods gathered into native vtables, the same
// AOT-safe pattern used for the Wayland listeners themselves (see Wl.Vtable).
//

using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace MS.Internal.Interop.Wayland
{
    /// <summary>enum libdecor_window_state.</summary>
    [Flags]
    internal enum LibdecorWindowState
    {
        None = 0,
        Active = 1 << 0,
        Maximized = 1 << 1,
        Fullscreen = 1 << 2,
        TiledLeft = 1 << 3,
        TiledRight = 1 << 4,
        TiledTop = 1 << 5,
        TiledBottom = 1 << 6,
        Suspended = 1 << 7,
    }

    /// <summary>enum libdecor_capabilities.</summary>
    [Flags]
    internal enum LibdecorCapabilities
    {
        Move = 1 << 0,
        Resize = 1 << 1,
        Minimize = 1 << 2,
        Fullscreen = 1 << 3,
        Close = 1 << 4,
    }

    /// <summary>
    /// struct libdecor_interface: one error callback followed by ten reserved slots. The reserved
    /// slots must exist -- libdecor stores the struct BY POINTER and would read past the end of a
    /// short one -- but stay null, which libdecor treats as "not implemented".
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct LibdecorInterface
    {
        public IntPtr error;
        public IntPtr reserved0, reserved1, reserved2, reserved3, reserved4;
        public IntPtr reserved5, reserved6, reserved7, reserved8, reserved9;
    }

    /// <summary>
    /// struct libdecor_frame_interface: configure, close, commit, dismiss_popup, then ten reserved
    /// slots (same by-pointer caveat as <see cref="LibdecorInterface"/>).
    ///
    /// `configure` is not optional and not merely informative: a frame stays unmapped until the
    /// application answers one with libdecor_frame_commit, so a null here is a window that never
    /// appears.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct LibdecorFrameInterface
    {
        public IntPtr configure;
        public IntPtr close;
        public IntPtr commit;
        public IntPtr dismiss_popup;
        public IntPtr reserved0, reserved1, reserved2, reserved3, reserved4;
        public IntPtr reserved5, reserved6, reserved7, reserved8, reserved9;
    }

    [SupportedOSPlatform("linux")]
    internal static unsafe class WlDecor
    {
        private const string Lib = "libdecor-0.so.0";

        [DllImport(Lib)] internal static extern IntPtr libdecor_new(IntPtr display, LibdecorInterface* iface);
        [DllImport(Lib)] internal static extern void libdecor_unref(IntPtr context);
        [DllImport(Lib)] internal static extern int libdecor_get_fd(IntPtr context);

        /// <summary>
        /// Dispatch libdecor's own queue. ALWAYS call with timeout 0 from the WPF run loop: a
        /// positive timeout makes libdecor poll the display fd itself, which both blocks the
        /// dispatcher and races our wl_display_prepare_read/read_events pairing.
        /// </summary>
        [DllImport(Lib)] internal static extern int libdecor_dispatch(IntPtr context, int timeoutMs);

        [DllImport(Lib)] internal static extern IntPtr libdecor_decorate(IntPtr context, IntPtr wlSurface, LibdecorFrameInterface* iface, IntPtr userData);
        [DllImport(Lib)] internal static extern void libdecor_frame_unref(IntPtr frame);
        [DllImport(Lib)] internal static extern void libdecor_frame_map(IntPtr frame);
        [DllImport(Lib)] internal static extern void libdecor_frame_set_visibility(IntPtr frame, [MarshalAs(UnmanagedType.I1)] bool visible);
        [DllImport(Lib)] internal static extern void libdecor_frame_set_title(IntPtr frame, [MarshalAs(UnmanagedType.LPUTF8Str)] string title);
        [DllImport(Lib)] internal static extern void libdecor_frame_set_app_id(IntPtr frame, [MarshalAs(UnmanagedType.LPUTF8Str)] string appId);
        [DllImport(Lib)] internal static extern void libdecor_frame_set_capabilities(IntPtr frame, LibdecorCapabilities capabilities);
        [DllImport(Lib)] internal static extern void libdecor_frame_set_min_content_size(IntPtr frame, int w, int h);
        [DllImport(Lib)] internal static extern void libdecor_frame_set_max_content_size(IntPtr frame, int w, int h);
        [DllImport(Lib)] internal static extern void libdecor_frame_commit(IntPtr frame, IntPtr state, IntPtr configuration);
        [DllImport(Lib)] internal static extern void libdecor_frame_set_minimized(IntPtr frame);
        [DllImport(Lib)] internal static extern void libdecor_frame_set_maximized(IntPtr frame);
        [DllImport(Lib)] internal static extern void libdecor_frame_unset_maximized(IntPtr frame);
        [DllImport(Lib)] [return: MarshalAs(UnmanagedType.I1)] internal static extern bool libdecor_frame_is_floating(IntPtr frame);
        [DllImport(Lib)] internal static extern void libdecor_frame_close(IntPtr frame);

        /// <summary>The toplevel's xdg_surface -- the parent an xdg_popup is created against.</summary>
        [DllImport(Lib)] internal static extern IntPtr libdecor_frame_get_xdg_surface(IntPtr frame);
        [DllImport(Lib)] internal static extern IntPtr libdecor_frame_get_xdg_toplevel(IntPtr frame);

        /// <summary>
        /// Converts a point in the CONTENT surface's coordinates into the frame's window-geometry
        /// coordinates. xdg_positioner.set_anchor_rect is specified against window geometry, which
        /// for a decorated window differs from the content surface by the titlebar/shadow insets --
        /// so without this every menu opens displaced by the height of the titlebar.
        /// </summary>
        [DllImport(Lib)] internal static extern void libdecor_frame_translate_coordinate(IntPtr frame, int surfaceX, int surfaceY, out int frameX, out int frameY);

        [DllImport(Lib)] internal static extern IntPtr libdecor_state_new(int width, int height);
        [DllImport(Lib)] internal static extern void libdecor_state_free(IntPtr state);

        [DllImport(Lib)] [return: MarshalAs(UnmanagedType.I1)]
        internal static extern bool libdecor_configuration_get_content_size(IntPtr configuration, IntPtr frame, out int width, out int height);

        [DllImport(Lib)] [return: MarshalAs(UnmanagedType.I1)]
        internal static extern bool libdecor_configuration_get_window_state(IntPtr configuration, out LibdecorWindowState state);

        /// <summary>True when libdecor is present. Checked before use so a machine without it gets a
        /// clear diagnostic instead of a DllNotFoundException from deep inside window creation.</summary>
        internal static bool IsAvailable
        {
            get
            {
                if (s_available is bool known) return known;
                bool ok = NativeLibrary.TryLoad(Lib, out IntPtr h);
                if (ok) NativeLibrary.Free(h);
                s_available = ok;
                return ok;
            }
        }
        private static bool? s_available;
    }
}
