// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Linux backend for NativePlatform. Two surface sources, chosen by what the session actually is:
//
//   Wayland (the supported head): the handle is a wl_surface*, paired with the process's single
//   wl_display* which the windowing layer installs through LinuxPlatform.WaylandDisplay. Both
//   halves MUST come from that one connection -- see LinuxPlatform's header.
//
//   X11: the handle is an X11 Window (XID) on the process's default display. Kept because the
//   engine can be embedded without the Wayland windowing head (and for a genuinely-X11 session),
//   but it is NOT a fallback for "Wayland failed": WaylandWindow is the only thing that creates
//   windows on this platform, so a surface built on an X11 window nothing else knows about would
//   be strictly worse than a clear error. Hence the explicit session check in CreateSurface.
//

using System;
using System.Runtime.InteropServices;

namespace Microsoft.Wpf.Interop.WebGpu.Composition.Platform
{
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    internal static unsafe class LinuxInterop
    {
        /// <summary>The process's wl_display*, installed by the windowing layer (see LinuxPlatform).</summary>
        internal static IntPtr WaylandDisplay;

        internal static Func<IntPtr, bool>? OpaqueQuery;
        internal static LinuxPlatform.WindowOriginCallback? OriginQuery;
        internal static Action? FlushDisplayHook;

        /// <summary>Reports whether a window is currently visible; see NativePlatform.IsWindowVisible.</summary>
        internal static Func<IntPtr, bool>? VisibleQuery;

        /// <summary>
        /// Whether a window handle names a popup, i.e. one whose scene belongs in its owner's surface
        /// (see NativePlatform.PopupsShareOwnerSurface). Installed by the windowing backend; null
        /// leaves every window presenting through a surface of its own.
        /// </summary>
        internal static Func<IntPtr, bool>? PopupQuery;

        public static IntPtr CreateSurface(IntPtr instance, IntPtr windowHandle)
        {
            if (windowHandle == IntPtr.Zero) return IntPtr.Zero;

            if (WaylandDisplay != IntPtr.Zero)
                return CreateWaylandSurface(instance, WaylandDisplay, windowHandle);

            // No Wayland connection installed. Only take the X11 path when this really is an X11
            // session -- under Wayland, DISPLAY is also set (XWayland), and silently building an
            // XWayland surface would hand back a window the input/windowing layer cannot drive.
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")))
                return IntPtr.Zero;

            return CreateXlibSurface(instance, windowHandle);
        }

        private static IntPtr CreateWaylandSurface(IntPtr instance, IntPtr display, IntPtr wlSurface)
        {
            var source = new Wgpu.WGPUSurfaceSourceWaylandSurface
            {
                chain = new Wgpu.WGPUChainedStruct { next = null, sType = Wgpu.WGPUSType_SurfaceSourceWaylandSurface },
                display = (void*)display,
                surface = (void*)wlSurface,
            };
            var desc = new Wgpu.WGPUSurfaceDescriptor { nextInChain = (Wgpu.WGPUChainedStruct*)&source };
            return Wgpu.wgpuInstanceCreateSurface(instance, &desc);
        }

        // ---- X11 -------------------------------------------------------------------------------

        private static IntPtr s_x11Display;
        private static bool s_x11Tried;
        private static readonly object s_x11Lock = new object();

        /// <summary>
        /// The process's X11 Display*, opened once. Opening one per surface (as this file used to)
        /// leaks a socket and an ~1 MB Display struct per window, and XCloseDisplay while wgpu still
        /// holds surfaces built on it would invalidate them -- so it is opened once and kept.
        /// </summary>
        private static IntPtr GetX11Display()
        {
            lock (s_x11Lock)
            {
                if (s_x11Tried) return s_x11Display;
                s_x11Tried = true;
                try
                {
                    // Before ANY other Xlib call: WPF renders off the UI thread and wgpu's Vulkan/GL
                    // backends call into Xlib from their own threads. Xlib is not thread-safe unless
                    // this is the first thing the process does with it.
                    XInitThreads();
                    s_x11Display = XOpenDisplay(null);
                }
                catch (DllNotFoundException)
                {
                    s_x11Display = IntPtr.Zero;   // no libX11 (a Wayland-only image)
                }
                return s_x11Display;
            }
        }

        private static IntPtr CreateXlibSurface(IntPtr instance, IntPtr x11Window)
        {
            IntPtr display = GetX11Display();
            if (display == IntPtr.Zero) return IntPtr.Zero;

            var source = new Wgpu.WGPUSurfaceSourceXlibWindow
            {
                chain = new Wgpu.WGPUChainedStruct { next = null, sType = Wgpu.WGPUSType_SurfaceSourceXlibWindow },
                display = (void*)display,
                window = (ulong)x11Window,
            };
            var desc = new Wgpu.WGPUSurfaceDescriptor { nextInChain = (Wgpu.WGPUChainedStruct*)&source };
            return Wgpu.wgpuInstanceCreateSurface(instance, &desc);
        }

        [DllImport("libX11.so.6")] private static extern IntPtr XOpenDisplay(string? display);
        [DllImport("libX11.so.6")] private static extern int XInitThreads();
    }
}
