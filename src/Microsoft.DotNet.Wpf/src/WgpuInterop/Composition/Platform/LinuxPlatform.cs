// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The one PUBLIC seam on the Linux backend, mirroring AndroidPlatform for the same reason: the two
// halves of the Linux window mapping are built independently and neither references the other.
//
//   WindowsBase  owns the WPF window handles (MS.Internal.Interop.Wayland.WaylandWindow) and owns
//                the process's single wl_display connection.
//   this engine  needs that connection to build a wgpu surface, and cannot reference WPF at all --
//                DUCE loads this whole assembly reflectively precisely to keep that direction clear.
//
// The display in particular is not merely convenient, it is REQUIRED to be the same object:
// vkCreateWaylandSurfaceKHR and vkGetPhysicalDeviceWaylandPresentationSupportKHR both take the
// wl_display their surface belongs to, the GLES backend's eglGetPlatformDisplay likewise, and
// libwayland exposes no wl_proxy_get_display to derive it from a wl_surface. A second connection
// would silently produce surfaces the compositor associates with a different client.
//
// So WaylandWindow installs itself here (reflectively, as DUCE loads the engine). An app head
// embedding the engine WITHOUT WPF -- the Wayland spike -- sets these itself.
//

using System;

namespace Microsoft.Wpf.Interop.WebGpu
{
    /// <summary>Public registration point for the Linux/Wayland window mapping (see file header).</summary>
    public static class LinuxPlatform
    {
        /// <summary>Reports a window's top-left corner in device pixels, relative to its owner's
        /// content area. See <see cref="WindowOriginQuery"/>.</summary>
        public delegate void WindowOriginCallback(IntPtr handle, out int x, out int y);

        /// <summary>
        /// The process's single wl_display*, or Zero when this is not a Wayland session (in which
        /// case <see cref="Composition.Platform.LinuxInterop"/> falls back to the X11 path).
        /// </summary>
        public static IntPtr WaylandDisplay
        {
            get => Composition.Platform.LinuxInterop.WaylandDisplay;
            set => Composition.Platform.LinuxInterop.WaylandDisplay = value;
        }

        /// <summary>
        /// Reports whether a WPF window handle names an OPAQUE window. Popups are not: they need a
        /// premultiplied-alpha surface so their shadow and rounded corners composite over what is
        /// behind them. Null means "assume opaque".
        /// </summary>
        public static Func<IntPtr, bool>? WindowOpaqueQuery
        {
            get => Composition.Platform.LinuxInterop.OpaqueQuery;
            set => Composition.Platform.LinuxInterop.OpaqueQuery = value;
        }

        /// <summary>
        /// Reports a window's top-left corner in device pixels within its owner. Used when a popup is
        /// drawn into its owner's surface rather than its own (see NativePlatform.PopupsShareOwnerSurface,
        /// which Linux enters when the GL backend advertises no premultiplied composite-alpha mode).
        /// </summary>
        public static WindowOriginCallback? WindowOriginQuery
        {
            get => Composition.Platform.LinuxInterop.OriginQuery;
            set => Composition.Platform.LinuxInterop.OriginQuery = value;
        }

        /// <summary>
        /// Reports whether a window is on screen. A Wayland compositor stops sending frame callbacks
        /// to a surface nobody can see, and with a Fifo swap chain that makes the next
        /// wgpuSurfaceGetCurrentTexture block until it comes back -- on the UI thread. See
        /// NativePlatform.IsWindowVisible.
        /// </summary>
        /// <summary>
        /// Reports whether a WPF window handle names a POPUP, whose scene is drawn into its owner's
        /// surface rather than one of its own (see NativePlatform.PopupsShareOwnerSurface). Null
        /// means "no popups here", which leaves every window presenting itself.
        /// </summary>
        public static Func<IntPtr, bool>? PopupWindowQuery
        {
            get => Composition.Platform.LinuxInterop.PopupQuery;
            set => Composition.Platform.LinuxInterop.PopupQuery = value;
        }

        public static Func<IntPtr, bool>? WindowVisibleQuery
        {
            get => Composition.Platform.LinuxInterop.VisibleQuery;
            set => Composition.Platform.LinuxInterop.VisibleQuery = value;
        }

        /// <summary>
        /// Flushes the Wayland connection's outgoing buffer. Called after a present so a just-shown
        /// frame reaches the compositor immediately even when the app then goes idle -- the exact
        /// analogue of MacInterop.FlushTransaction. Without it a committed frame can sit in the
        /// buffer until some unrelated event wakes the run loop.
        /// </summary>
        public static Action? FlushDisplay
        {
            get => Composition.Platform.LinuxInterop.FlushDisplayHook;
            set => Composition.Platform.LinuxInterop.FlushDisplayHook = value;
        }
    }
}
