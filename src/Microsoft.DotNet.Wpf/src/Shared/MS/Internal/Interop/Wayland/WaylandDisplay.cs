// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The process's single Wayland connection: the wl_display, the globals bound off its registry, the
// libdecor context, and the read/dispatch pump everything else drives.
//
// Deliberately free of any WPF dependency so it can be exercised on its own -- the WaylandSpike
// test brings a window up through this file and nothing else, which is what makes a protocol-table
// or handshake bug debuggable without WPF in the picture.
//
// ONE connection, always. vkCreateWaylandSurfaceKHR (and the GLES backend's eglGetPlatformDisplay)
// take the wl_display their surface belongs to, and libwayland exposes no wl_proxy_get_display to
// recover it from a wl_surface, so the renderer is handed this display through
// LinuxPlatform.WaylandDisplay. A second wl_display_connect would yield surfaces the compositor
// associates with a different client and silently never present.
//

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using static MS.Internal.Interop.Wayland.WlProtocols;

namespace MS.Internal.Interop.Wayland
{
    /// <summary>A wl_output and the geometry the compositor reported for it.</summary>
    internal sealed class WaylandOutput
    {
        public IntPtr Proxy;
        public uint Name;           // registry name, for global_remove
        public int X, Y;            // position in the compositor's global space, logical px
        public int Width, Height;   // current mode, device px
        public int Scale = 1;       // integer buffer scale
    }

    [SupportedOSPlatform("linux")]
    internal static unsafe class WaylandDisplay
    {
        public static IntPtr Display { get; private set; }
        public static IntPtr Registry { get; private set; }
        public static IntPtr Compositor { get; private set; }
        public static IntPtr Subcompositor { get; private set; }
        public static IntPtr Shm { get; private set; }
        public static IntPtr Seat { get; private set; }
        public static uint SeatVersion { get; private set; }
        public static IntPtr XdgWmBase { get; private set; }
        public static IntPtr Viewporter { get; private set; }
        public static IntPtr FractionalScaleManager { get; private set; }
        public static IntPtr CursorShapeManager { get; private set; }
        public static IntPtr DataDeviceManager { get; private set; }
        public static IntPtr Decor { get; private set; }

        public static bool IsActive => Display != IntPtr.Zero;

        internal static readonly List<WaylandOutput> Outputs = new();

        /// <summary>
        /// Maps a wl_surface to its backing scale. Installed by WaylandWindow; left at 1.0 for a
        /// host that drives this layer without WPF (the WaylandSpike test), which is also what keeps
        /// the input layer from having to know the window type.
        /// </summary>
        public static Func<IntPtr, double>? SurfaceScaleQuery;

        internal static double ScaleForSurface(IntPtr surface) => SurfaceScaleQuery?.Invoke(surface) ?? 1.0;

        /// <summary>Reports whether a window is opaque; forwarded to the engine (see InstallCompositorSeam).</summary>
        public static Func<IntPtr, bool>? WindowOpaqueQuery;

        /// <summary>A window's origin within its owner, in device pixels. Needed once popups are
        /// composited into the owner's surface, which on Linux they are (NativePlatform).</summary>
        public delegate void WindowOriginCallback(IntPtr handle, out int x, out int y);
        public static WindowOriginCallback? WindowOriginQuery;

        /// <summary>Diagnostics sink (WPF_WAYLAND_LOG=1), mirroring the engine's LogSink pattern.</summary>
        public static Action<string>? LogSink =
            Environment.GetEnvironmentVariable("WPF_WAYLAND_LOG") == "1"
                ? static m => Console.Error.WriteLine("[wayland] " + m)
                : null;

        private static readonly object s_lock = new object();
        private static bool s_initialized;

        // Native memory kept for the process lifetime: listener vtables and the two libdecor
        // interface structs, all of which the C side stores BY POINTER.
        private static IntPtr* s_registryListener;
        private static IntPtr* s_wmBaseListener;
        private static IntPtr* s_outputListener;
        private static LibdecorInterface* s_decorInterface;

        /// <summary>
        /// Connect and bind the globals. Idempotent, and safe to call from window creation --
        /// mirrors CocoaWindow.EnsureApplication, which likewise brings the platform up lazily on
        /// first use rather than requiring an app head to bootstrap it.
        /// </summary>
        public static void EnsureInitialized()
        {
            lock (s_lock)
            {
                if (s_initialized) return;
                s_initialized = true;   // set first: a failed attempt must not be retried per-window

                Display = Wl.wl_display_connect(null);
                if (Display == IntPtr.Zero)
                {
                    throw new InvalidOperationException(
                        "Could not connect to a Wayland compositor. WAYLAND_DISPLAY=" +
                        (Environment.GetEnvironmentVariable("WAYLAND_DISPLAY") ?? "(unset)") +
                        ", XDG_RUNTIME_DIR=" + (Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") ?? "(unset)"));
                }

                s_registryListener = Wl.Vtable(
                    (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, uint, IntPtr, uint, void>)&OnGlobal,
                    (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, uint, void>)&OnGlobalRemove);
                s_wmBaseListener = Wl.Vtable(
                    (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, uint, void>)&OnWmBasePing);
                s_outputListener = Wl.Vtable(
                    (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, int, int, int, int, int, IntPtr, IntPtr, int, void>)&OnOutputGeometry,
                    (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, uint, int, int, int, void>)&OnOutputMode,
                    (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void>)&OnOutputDone,
                    (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, int, void>)&OnOutputScale,
                    (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, void>)&OnOutputName,
                    (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, void>)&OnOutputDescription);

                // wl_display.get_registry is opcode 1 (0 is sync).
                Registry = Wl.Construct(Display, 1, "wl_registry", 1, WlArgument.NewId());
                Wl.wl_proxy_add_listener(Registry, s_registryListener, IntPtr.Zero);

                // TWO roundtrips, not one. The first delivers the registry's `global` burst; the
                // second delivers the events those newly bound globals then emit (wl_seat
                // capabilities, wl_output geometry/mode/scale/done). Stopping after one leaves the
                // seat with no pointer/keyboard and every output sized 0x0.
                Wl.wl_display_roundtrip(Display);
                Wl.wl_display_roundtrip(Display);

                if (Compositor == IntPtr.Zero)
                    throw new InvalidOperationException("The Wayland compositor did not advertise wl_compositor.");

                InitializeDecor();

                LogSink?.Invoke($"connected: compositor={Compositor != IntPtr.Zero} xdg_wm_base={XdgWmBase != IntPtr.Zero} " +
                                $"seat=v{SeatVersion} viewporter={Viewporter != IntPtr.Zero} " +
                                $"fractional={FractionalScaleManager != IntPtr.Zero} cursorShape={CursorShapeManager != IntPtr.Zero} " +
                                $"outputs={Outputs.Count} libdecor={Decor != IntPtr.Zero}");

                // Hand the renderer this connection. Reflective so neither assembly references the
                // other -- exactly how AndroidWindow installs itself (InstallCompositorResolver).
                InstallCompositorSeam();
            }
        }

        private static void InitializeDecor()
        {
            if (!WlDecor.IsAvailable)
            {
                LogSink?.Invoke("libdecor-0.so.0 not found: toplevels would have no decorations.");
                return;
            }
            s_decorInterface = (LibdecorInterface*)NativeMemory.AllocZeroed((nuint)sizeof(LibdecorInterface));
            s_decorInterface->error = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, int, IntPtr, void>)&OnDecorError;
            Decor = WlDecor.libdecor_new(Display, s_decorInterface);
            if (Decor == IntPtr.Zero)
                LogSink?.Invoke("libdecor_new failed: toplevels will have no decorations.");
        }

        /// <summary>
        /// Publishes the wl_display (and the popup-compositing queries) to the WebGPU engine through
        /// its public LinuxPlatform seam, by reflection so WindowsBase keeps no compile-time
        /// reference to the engine -- DUCE loads that assembly reflectively for the same reason.
        /// </summary>
        private static void InstallCompositorSeam()
        {
            try
            {
                Type? t = Type.GetType("Microsoft.Wpf.Interop.WebGpu.LinuxPlatform, Microsoft.Wpf.Interop.WebGpu");
                if (t is null)
                {
                    // Not merely a missing nicety: without the display, the GL backend's
                    // eglGetPlatformDisplay finds no windowing system, falls back to a SURFACELESS
                    // EGL platform, and every later wgpuSurfaceConfigure fails with "Surface does
                    // not support the adapter's queue family" -- from inside Rust, as an abort.
                    LogSink?.Invoke("could not find Microsoft.Wpf.Interop.WebGpu.LinuxPlatform; " +
                                    "the renderer will not receive the Wayland display.");
                    return;
                }
                t.GetProperty("WaylandDisplay")?.SetValue(null, Display);
                t.GetProperty("FlushDisplay")?.SetValue(null, new Action(Flush));

                if (WindowOpaqueQuery is not null)
                    t.GetProperty("WindowOpaqueQuery")?.SetValue(null, WindowOpaqueQuery);

                // The origin callback has an `out` parameter, so its delegate type cannot be named
                // here (it is declared in the engine). Bind our static to whatever the property's
                // type turns out to be.
                System.Reflection.PropertyInfo? originProperty = t.GetProperty("WindowOriginQuery");
                if (originProperty is not null && WindowOriginQuery is not null)
                {
                    originProperty.SetValue(null, Delegate.CreateDelegate(
                        originProperty.PropertyType, WindowOriginQuery.Target, WindowOriginQuery.Method));
                }

                object? readBack = t.GetProperty("WaylandDisplay")?.GetValue(null);
                LogSink?.Invoke($"compositor seam installed; engine sees display=0x{((IntPtr)(readBack ?? IntPtr.Zero)).ToInt64():x}");
            }
            catch (Exception e)
            {
                LogSink?.Invoke("could not install the compositor seam: " + e.Message);
            }
        }

        // ---- Registry -----------------------------------------------------------------------

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void OnGlobal(IntPtr data, IntPtr registry, uint name, IntPtr ifaceUtf8, uint version)
        {
            // An exception crossing back into libwayland is undefined behaviour, so every handler
            // in this file swallows. Losing one global degrades a feature; unwinding through C
            // corrupts the connection.
            try
            {
                string iface = Wl.FromUtf8(ifaceUtf8);
                switch (iface)
                {
                    case "wl_compositor":
                        Compositor = Bind(registry, name, iface, Math.Min(version, 6u));
                        break;
                    case "wl_subcompositor":
                        Subcompositor = Bind(registry, name, iface, Math.Min(version, 1u));
                        break;
                    case "wl_shm":
                        Shm = Bind(registry, name, iface, Math.Min(version, 1u));
                        break;
                    case "wl_seat":
                        // v8 carries wl_pointer.axis_value120, the only scroll event that reports
                        // real 1/120 detents; below that we have to synthesise them (see WaylandInput).
                        SeatVersion = Math.Min(version, 8u);
                        Seat = Bind(registry, name, iface, SeatVersion);
                        WaylandInput.AttachSeat(Seat, SeatVersion);
                        break;
                    case "xdg_wm_base":
                        XdgWmBase = Bind(registry, name, iface, Math.Min(version, 6u));
                        Wl.wl_proxy_add_listener(XdgWmBase, s_wmBaseListener, IntPtr.Zero);
                        break;
                    case "wp_viewporter":
                        Viewporter = Bind(registry, name, iface, 1);
                        break;
                    case "wp_fractional_scale_manager_v1":
                        FractionalScaleManager = Bind(registry, name, iface, 1);
                        break;
                    case "wp_cursor_shape_manager_v1":
                        CursorShapeManager = Bind(registry, name, iface, 1);
                        break;
                    case "wl_data_device_manager":
                        DataDeviceManager = Bind(registry, name, iface, Math.Min(version, 3u));
                        break;
                    case "wl_output":
                        {
                            IntPtr proxy = Bind(registry, name, iface, Math.Min(version, 4u));
                            var output = new WaylandOutput { Proxy = proxy, Name = name };
                            lock (Outputs) Outputs.Add(output);
                            Wl.wl_proxy_add_listener(proxy, s_outputListener, IntPtr.Zero);
                            break;
                        }
                }
            }
            catch { }
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void OnGlobalRemove(IntPtr data, IntPtr registry, uint name)
        {
            try
            {
                lock (Outputs)
                {
                    for (int i = 0; i < Outputs.Count; i++)
                    {
                        if (Outputs[i].Name != name) continue;
                        Outputs.RemoveAt(i);
                        break;
                    }
                }
            }
            catch { }
        }

        /// <summary>
        /// wl_registry.bind. The one irregular request in the core protocol: its new_id has no
        /// static interface, so the wire form expands to (name, interface_name, version, new_id) --
        /// four argument slots for a signature that reads "usun". The interface name is taken from
        /// the wl_interface's own `name` field, which is already a NUL-terminated native string.
        /// </summary>
        private static IntPtr Bind(IntPtr registry, uint name, string interfaceName, uint version)
        {
            IntPtr iface = Wl.Interface(interfaceName);
            if (iface == IntPtr.Zero)
            {
                LogSink?.Invoke($"no wl_interface table for '{interfaceName}'; not binding it.");
                return IntPtr.Zero;
            }

            WlArgument* args = stackalloc WlArgument[4];
            args[0] = WlArgument.UInt(name);
            args[1] = WlArgument.Ptr(((WlInterface*)iface)->name);
            args[2] = WlArgument.UInt(version);
            args[3] = WlArgument.NewId();
            return Wl.wl_proxy_marshal_array_flags(registry, WL_REGISTRY_BIND, iface, version, 0, args);
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void OnWmBasePing(IntPtr data, IntPtr wmBase, uint serial)
        {
            // Not optional: a compositor that does not get its pong back marks the app unresponsive
            // and eventually offers to kill it.
            try { Wl.Request(wmBase, XDG_WM_BASE_PONG, WlArgument.UInt(serial)); }
            catch { }
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void OnDecorError(IntPtr context, int error, IntPtr messageUtf8)
        {
            try { LogSink?.Invoke($"libdecor error {error}: {Wl.FromUtf8(messageUtf8)}"); }
            catch { }
        }

        // ---- Outputs ------------------------------------------------------------------------

        private static WaylandOutput? FindOutput(IntPtr proxy)
        {
            lock (Outputs)
            {
                foreach (WaylandOutput o in Outputs)
                    if (o.Proxy == proxy) return o;
            }
            return null;
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void OnOutputGeometry(IntPtr data, IntPtr output, int x, int y, int physW, int physH,
                                             int subpixel, IntPtr make, IntPtr model, int transform)
        {
            try
            {
                WaylandOutput? o = FindOutput(output);
                if (o is null) return;
                o.X = x;
                o.Y = y;
            }
            catch { }
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void OnOutputMode(IntPtr data, IntPtr output, uint flags, int width, int height, int refresh)
        {
            try
            {
                const uint WL_OUTPUT_MODE_CURRENT = 1;
                if ((flags & WL_OUTPUT_MODE_CURRENT) == 0) return;
                WaylandOutput? o = FindOutput(output);
                if (o is null) return;
                o.Width = width;
                o.Height = height;
            }
            catch { }
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void OnOutputScale(IntPtr data, IntPtr output, int factor)
        {
            try
            {
                WaylandOutput? o = FindOutput(output);
                if (o is not null && factor > 0) o.Scale = factor;
            }
            catch { }
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void OnOutputDone(IntPtr data, IntPtr output) { }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void OnOutputName(IntPtr data, IntPtr output, IntPtr name) { }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void OnOutputDescription(IntPtr data, IntPtr output, IntPtr description) { }

        /// <summary>
        /// Primary screen bounds in device pixels. Wayland exposes no "primary" concept and no work
        /// area (GNOME's top bar is a layer surface clients cannot see), so this reports the first
        /// output and treats its full extent as the work area.
        /// </summary>
        public static bool GetPrimaryScreenPixels(out int left, out int top, out int right, out int bottom)
        {
            lock (Outputs)
            {
                foreach (WaylandOutput o in Outputs)
                {
                    if (o.Width <= 0 || o.Height <= 0) continue;
                    left = o.X * o.Scale;
                    top = o.Y * o.Scale;
                    right = left + o.Width;
                    bottom = top + o.Height;
                    return true;
                }
            }
            left = top = 0;
            right = 1920;
            bottom = 1080;
            return false;
        }

        // ---- The pump -----------------------------------------------------------------------

        [StructLayout(LayoutKind.Sequential)]
        private struct PollFd
        {
            public int fd;
            public short events;
            public short revents;
        }

        private const short POLLIN = 0x001;

        [DllImport("libc", SetLastError = true)]
        private static extern int poll(PollFd* fds, nuint nfds, int timeout);

        public static void Flush()
        {
            if (Display != IntPtr.Zero) Wl.wl_display_flush(Display);
        }

        /// <summary>
        /// Block for up to <paramref name="timeoutMs"/> waiting for compositor events, then dispatch
        /// whatever arrived. Returns true if anything was read.
        ///
        /// This is the prepare_read/read_events dance, and it is mandatory rather than stylistic.
        /// wl_display_dispatch() does its own poll with NO timeout and NO other file descriptors, so
        /// calling it from a loop that must also service the WPF dispatcher queue would block
        /// forever the moment the compositor went idle -- starving DispatcherTimer and any
        /// cross-thread BeginInvoke -- and would race any other reader of the fd.
        ///
        /// The flush placement matters just as much: it has to happen AFTER prepare_read and BEFORE
        /// poll. Flush earlier and a request issued in between sits in the outgoing buffer; skip it
        /// and the compositor never sees the commit we are waiting for a reply to, so poll blocks
        /// until the timeout every single frame.
        /// </summary>
        public static bool ReadEvents(int timeoutMs, int extraFd = -1)
        {
            if (Display == IntPtr.Zero) return false;

            // prepare_read fails while the queue still holds undispatched events; drain and retry,
            // otherwise read_events would block behind events already in memory.
            while (Wl.wl_display_prepare_read(Display) != 0)
            {
                if (Wl.wl_display_dispatch_pending(Display) < 0) return false;
            }

            Wl.wl_display_flush(Display);

            int n;
            PollFd* fds = stackalloc PollFd[2];
            fds[0].fd = Wl.wl_display_get_fd(Display);
            fds[0].events = POLLIN;
            nuint count = 1;
            if (extraFd >= 0)
            {
                fds[1].fd = extraFd;
                fds[1].events = POLLIN;
                count = 2;
            }

            do { n = poll(fds, count, timeoutMs); }
            while (n < 0 && Marshal.GetLastPInvokeError() == 4 /* EINTR */);

            bool read = false;
            if (n > 0 && (fds[0].revents & POLLIN) != 0)
            {
                Wl.wl_display_read_events(Display);
                read = true;
            }
            else
            {
                // Nothing to read (timeout, error, or only the extra fd fired). The prepared read
                // MUST be cancelled -- libwayland keeps a per-connection reader count and leaving it
                // raised deadlocks the next prepare_read.
                Wl.wl_display_cancel_read(Display);
            }

            Wl.wl_display_dispatch_pending(Display);
            if (Decor != IntPtr.Zero) WlDecor.libdecor_dispatch(Decor, 0);
            return read;
        }

        /// <summary>Dispatch without blocking; used by nested pumps (modal dialogs, clipboard reads).</summary>
        public static void DispatchPending()
        {
            if (Display == IntPtr.Zero) return;
            Wl.wl_display_dispatch_pending(Display);
            if (Decor != IntPtr.Zero) WlDecor.libdecor_dispatch(Decor, 0);
            Wl.wl_display_flush(Display);
        }

        /// <summary>The connection's fd, for a caller that owns the poll set (see DispatcherRunLoop).</summary>
        public static int Fd => Display == IntPtr.Zero ? -1 : Wl.wl_display_get_fd(Display);
    }
}
