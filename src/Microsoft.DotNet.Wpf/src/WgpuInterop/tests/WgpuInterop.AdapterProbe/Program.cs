// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Reports which adapter wgpu selects for each backend the host can enable, so the
// Linux head can be pinned to the one that is actually hardware-accelerated rather
// than guessing. Prints the same "backend=/type=/device=" string WgpuContext derives
// (WgpuContext.cs:198) for: all backends (wgpu's own choice), Vulkan only, and GL only.
//
// Why this exists: a Vulkan adapter is not automatically the fast one. Under a VM whose
// host exposes VirGL (OpenGL passthrough) but not Venus (Vulkan passthrough), Vulkan
// enumerates only lavapipe -- a CPU rasterizer -- while GL reaches the real GPU. The
// adapterType field is the tell: Cpu means software, anything else means hardware.
//
// The GL-on-Wayland case additionally needs the wl_display handed to the instance via
// WGPUInstanceExtras.displayHandle, so this also proves wl_display_connect works.
//
//   dotnet run --project tests/WgpuInterop.AdapterProbe -p:WgpuRid=linux-arm64
//

using System;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Wpf.Interop.WebGpu;
using static Microsoft.Wpf.Interop.WebGpu.Wgpu;

internal static unsafe class Program
{
    private static IntPtr s_adapter;
    private static bool s_done;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    private static void OnAdapter(WGPURequestAdapterStatus status, IntPtr adapter, WGPUStringView message, IntPtr u1, IntPtr u2)
    {
        if (status == WGPURequestAdapterStatus.Success) s_adapter = adapter;
        s_done = true;
    }

    [DllImport("libwayland-client.so.0")]
    private static extern IntPtr wl_display_connect(string? name);

    private static int Main()
    {
        IntPtr wlDisplay = IntPtr.Zero;
        try { wlDisplay = wl_display_connect(null); }
        catch (DllNotFoundException) { }
        Console.WriteLine($"wl_display_connect -> 0x{wlDisplay.ToInt64():x} " +
                          (wlDisplay != IntPtr.Zero ? "(Wayland session OK)" : "(no Wayland)"));
        Console.WriteLine();

        Probe("all backends", WGPUInstanceBackend_All, IntPtr.Zero);
        Probe("Vulkan only", WGPUInstanceBackend_Vulkan, IntPtr.Zero);
        Probe("GL only", WGPUInstanceBackend_GL, IntPtr.Zero);
        if (wlDisplay != IntPtr.Zero)
            Probe("GL only + wl_display", WGPUInstanceBackend_GL, wlDisplay);

        // What the engine itself selects, which is the answer that actually matters.
        Console.WriteLine();
        Microsoft.Wpf.Interop.WebGpu.LinuxPlatform.WaylandDisplay = wlDisplay;
        using (var ctx = Microsoft.Wpf.Interop.WebGpu.Composition.WgpuContext.Create())
        {
            Console.WriteLine($"WgpuContext.Create()   : {ctx.AdapterDescription}");
            Console.WriteLine($"                         IsOpenGL={ctx.IsOpenGL}");
        }
        return 0;
    }

    private static void Probe(string label, ulong backends, IntPtr waylandDisplay)
    {
        IntPtr instance;
        if (backends == WGPUInstanceBackend_All && waylandDisplay == IntPtr.Zero)
        {
            instance = wgpuCreateInstance(null);
        }
        else
        {
            var extras = new WGPUInstanceExtras
            {
                chain = new WGPUChainedStruct { next = null, sType = WGPUSType_InstanceExtras },
                backends = backends,
            };
            if (waylandDisplay != IntPtr.Zero)
            {
                // WGPUNativeDisplayHandleType_Wayland = 3; the union's first pointer field is
                // the wl_display (webgpu.h: WGPUWaylandDisplayHandle { void* display; }).
                extras.displayHandle.type = 3;
                extras.displayHandle.data.display = (void*)waylandDisplay;
            }
            var desc = new WGPUInstanceDescriptor { nextInChain = (WGPUChainedStruct*)&extras };
            instance = wgpuCreateInstance(&desc);
        }

        if (instance == IntPtr.Zero) { Console.WriteLine($"{label,-22}: wgpuCreateInstance FAILED"); return; }

        s_adapter = IntPtr.Zero;
        s_done = false;
        var cb = new WGPURequestAdapterCallbackInfo
        {
            mode = WGPUCallbackMode.AllowProcessEvents,
            callback = (IntPtr)(delegate* unmanaged[Cdecl]<WGPURequestAdapterStatus, IntPtr, WGPUStringView, IntPtr, IntPtr, void>)&OnAdapter,
        };
        var options = new WGPURequestAdapterOptions { powerPreference = WGPUPowerPreference.HighPerformance };
        wgpuInstanceRequestAdapter(instance, &options, cb);
        for (int i = 0; i < 1000 && !s_done; i++) { wgpuInstanceProcessEvents(instance); Thread.Sleep(1); }

        if (s_adapter == IntPtr.Zero) { Console.WriteLine($"{label,-22}: no adapter"); return; }

        var info = new WGPUAdapterInfo();
        if (wgpuAdapterGetInfo(s_adapter, &info) == WGPUStatus.Success)
        {
            static string S(WGPUStringView v) => v.data == null ? "" : System.Text.Encoding.UTF8.GetString(v.data, (int)v.length);
            bool cpu = info.adapterType == WGPUAdapterType.CPU;
            Console.WriteLine($"{label,-22}: backend={info.backendType} type={info.adapterType} " +
                              $"device='{S(info.device)}' {(cpu ? "  <-- SOFTWARE" : "  <-- hardware")}");
        }
        else
        {
            Console.WriteLine($"{label,-22}: adapter acquired, wgpuAdapterGetInfo failed");
        }
    }
}
