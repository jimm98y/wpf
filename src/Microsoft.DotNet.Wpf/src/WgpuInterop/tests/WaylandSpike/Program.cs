// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Gate G3 of the Linux head: a real Wayland window presenting a wgpu clear colour, with NO WPF in
// the picture. Sibling of AndroidSpike/IosSpike, and the same idea -- prove the platform layer on
// its own so that when WPF is layered on top, a failure there is unambiguously WPF's.
//
// What passing this actually establishes:
//   * the hand-authored wl_interface tables marshal correctly (a wrong signature, opcode or types[]
//     entry is a protocol error that kills the connection, and nothing checks them at compile time);
//   * the [UnmanagedCallersOnly] listener vtables are wired in the right order;
//   * libdecor decorates, configures and maps a toplevel, and the configure/ack handshake completes;
//   * WGPUSurfaceSourceWaylandSurface builds a presentable surface from our wl_surface + wl_display;
//   * the surface's real capabilities (formats, alpha modes, present modes) -- which decide whether
//     popups can be transparent on this backend.
//
//   dotnet run --project tests/WaylandSpike -p:WgpuRid=linux-arm64
//   WAYLAND_DEBUG=1 dotnet run ...     decodes every request and event: the debugging lever
//   --frames N                          exit after N frames instead of on window close
//

using System;
using System.Runtime.InteropServices;
using MS.Internal.Interop.Wayland;
using Microsoft.Wpf.Interop.WebGpu;
using Microsoft.Wpf.Interop.WebGpu.Composition;
using static Microsoft.Wpf.Interop.WebGpu.Wgpu;
using static MS.Internal.Interop.Wayland.WlProtocols;

internal static unsafe class Program
{
    private static IntPtr s_surface;      // wl_surface
    private static IntPtr s_frame;        // libdecor_frame
    private static int s_contentW = 800, s_contentH = 600;
    private static bool s_configured;
    private static bool s_closed;
    private static LibdecorFrameInterface* s_frameIface;

    private static int Main(string[] args)
    {
        int maxFrames = 0;
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == "--frames") int.TryParse(args[i + 1], out maxFrames);

        Console.WriteLine("== WaylandSpike: bare Wayland window + wgpu clear colour, no WPF ==");

        // 1. Connect and bind globals.
        WaylandDisplay.LogSink ??= m => Console.WriteLine("  [wayland] " + m);
        WaylandDisplay.EnsureInitialized();
        Console.WriteLine($"connected: display=0x{WaylandDisplay.Display.ToInt64():x}");

        if (WaylandDisplay.Decor == IntPtr.Zero)
        {
            Console.Error.WriteLine("FAILED: libdecor unavailable, cannot create a decorated toplevel.");
            return 1;
        }

        // 2. Create a surface and let libdecor own the toplevel.
        s_surface = Wl.Construct(WaylandDisplay.Compositor, WL_COMPOSITOR_CREATE_SURFACE, "wl_surface",
            Wl.wl_proxy_get_version(WaylandDisplay.Compositor), WlArgument.NewId());
        if (s_surface == IntPtr.Zero) { Console.Error.WriteLine("FAILED: create_surface"); return 1; }

        s_frameIface = (LibdecorFrameInterface*)NativeMemory.AllocZeroed((nuint)sizeof(LibdecorFrameInterface));
        s_frameIface->configure = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, void>)&OnConfigure;
        s_frameIface->close = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void>)&OnClose;
        s_frameIface->commit = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void>)&OnCommit;
        s_frameIface->dismiss_popup = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, void>)&OnDismissPopup;

        s_frame = WlDecor.libdecor_decorate(WaylandDisplay.Decor, s_surface, s_frameIface, IntPtr.Zero);
        if (s_frame == IntPtr.Zero) { Console.Error.WriteLine("FAILED: libdecor_decorate"); return 1; }
        WlDecor.libdecor_frame_set_app_id(s_frame, "wpf-wayland-spike");
        WlDecor.libdecor_frame_set_title(s_frame, "WPF Wayland spike");
        WlDecor.libdecor_frame_map(s_frame);

        // The mandatory empty first commit, then pump until the compositor configures us. Nothing
        // may be attached to the surface before that.
        Wl.Request(s_surface, WL_SURFACE_COMMIT);
        WaylandDisplay.Flush();
        for (int i = 0; i < 200 && !s_configured; i++) WaylandDisplay.ReadEvents(8);
        if (!s_configured) { Console.Error.WriteLine("FAILED: no configure from the compositor"); return 1; }
        Console.WriteLine($"configured: content {s_contentW}x{s_contentH}");

        // 3. wgpu on that surface, through the same seam WPF uses.
        LinuxPlatform.WaylandDisplay = WaylandDisplay.Display;
        WgpuContext.LogSink ??= m => Console.WriteLine("  " + m);
        using var ctx = WgpuContext.Create();
        Console.WriteLine($"adapter: {ctx.AdapterDescription}");

        IntPtr wgpuSurface = Microsoft.Wpf.Interop.WebGpu.Composition.Platform.NativePlatform.CreateWindowSurface(ctx.Instance, s_surface);
        if (wgpuSurface == IntPtr.Zero) { Console.Error.WriteLine("FAILED: could not create a wgpu surface"); return 1; }
        Console.WriteLine($"wgpu surface: 0x{wgpuSurface.ToInt64():x}");

        // 4. Report the real capabilities. The alpha modes decide whether a WPF popup can be
        //    transparent on this backend, or whether popups must be composited into their owner.
        var caps = new WGPUSurfaceCapabilities();
        if (wgpuSurfaceGetCapabilities(wgpuSurface, ctx.Adapter, &caps) == WGPUStatus.Success)
        {
            Console.Write("formats:");
            for (nuint i = 0; i < caps.formatCount; i++) Console.Write($" {caps.formats[i]}");
            Console.Write("\nalphaModes:");
            for (nuint i = 0; i < caps.alphaModeCount; i++) Console.Write($" {caps.alphaModes[i]}");
            Console.Write("\npresentModes:");
            for (nuint i = 0; i < caps.presentModeCount; i++) Console.Write($" {caps.presentModes[i]}");
            Console.WriteLine();
        }
        else
        {
            Console.WriteLine("wgpuSurfaceGetCapabilities failed (not fatal).");
        }

        // 5. Configure and present a colour ramp until the window is closed.
        WGPUTextureFormat format = caps.formatCount > 0 ? caps.formats[0] : WGPUTextureFormat.BGRA8Unorm;
        int configuredW = 0, configuredH = 0, frames = 0;

        while (!s_closed && (maxFrames == 0 || frames < maxFrames))
        {
            WaylandDisplay.ReadEvents(16);

            if (configuredW != s_contentW || configuredH != s_contentH)
            {
                configuredW = s_contentW;
                configuredH = s_contentH;
                var config = new WGPUSurfaceConfiguration
                {
                    device = ctx.Device,
                    format = format,
                    usage = WGPUTextureUsage.RenderAttachment,
                    width = (uint)Math.Max(1, configuredW),
                    height = (uint)Math.Max(1, configuredH),
                    presentMode = WGPUPresentMode.Fifo,
                    alphaMode = WGPUCompositeAlphaMode.Auto,
                };
                wgpuSurfaceConfigure(wgpuSurface, &config);
                Console.WriteLine($"surface configured {configuredW}x{configuredH} format={format}");
            }

            var surfaceTexture = new WGPUSurfaceTexture();
            wgpuSurfaceGetCurrentTexture(wgpuSurface, &surfaceTexture);
            if (surfaceTexture.texture == IntPtr.Zero) continue;

            IntPtr view = wgpuTextureCreateView(surfaceTexture.texture, IntPtr.Zero);
            IntPtr encoder = wgpuDeviceCreateCommandEncoder(ctx.Device, IntPtr.Zero);

            double t = frames / 120.0;
            var attachment = new WGPURenderPassColorAttachment
            {
                view = view,
                loadOp = WGPULoadOp.Clear,
                storeOp = WGPUStoreOp.Store,
                depthSlice = ~0u,
                clearValue = new WGPUColor
                {
                    r = 0.5 + 0.5 * Math.Sin(t),
                    g = 0.25,
                    b = 0.5 + 0.5 * Math.Cos(t),
                    a = 1.0,
                },
            };
            var passDesc = new WGPURenderPassDescriptor { colorAttachmentCount = 1, colorAttachments = &attachment };
            IntPtr pass = wgpuCommandEncoderBeginRenderPass(encoder, &passDesc);
            wgpuRenderPassEncoderEnd(pass);
            IntPtr commands = wgpuCommandEncoderFinish(encoder, IntPtr.Zero);
            wgpuQueueSubmit(ctx.Queue, 1, &commands);
            wgpuSurfacePresent(wgpuSurface);
            WaylandDisplay.Flush();

            wgpuCommandBufferRelease(commands);
            wgpuRenderPassEncoderRelease(pass);
            wgpuCommandEncoderRelease(encoder);
            wgpuTextureViewRelease(view);
            wgpuTextureRelease(surfaceTexture.texture);

            if (++frames % 60 == 0) Console.WriteLine($"  {frames} frames");
        }

        Console.WriteLine($"WAYLAND SPIKE PASSED: {frames} frames presented.");
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    private static void OnConfigure(IntPtr frame, IntPtr configuration, IntPtr userData)
    {
        try
        {
            if (!WlDecor.libdecor_configuration_get_content_size(configuration, frame, out int w, out int h) || w <= 0 || h <= 0)
            {
                w = s_contentW;
                h = s_contentH;
            }
            s_contentW = w;
            s_contentH = h;

            IntPtr state = WlDecor.libdecor_state_new(w, h);
            WlDecor.libdecor_frame_commit(frame, state, configuration);
            WlDecor.libdecor_state_free(state);
            s_configured = true;
        }
        catch (Exception e) { Console.Error.WriteLine("configure: " + e.Message); }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    private static void OnClose(IntPtr frame, IntPtr userData) => s_closed = true;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    private static void OnCommit(IntPtr frame, IntPtr userData)
    {
        try { Wl.Request(s_surface, WL_SURFACE_COMMIT); } catch { }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    private static void OnDismissPopup(IntPtr frame, IntPtr seatName, IntPtr userData) { }
}
