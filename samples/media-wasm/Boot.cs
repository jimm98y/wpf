// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// WebAssembly entry point: pre-acquires the WebGPU adapter/device (Promise-based,
// must complete before the WPF compositor starts), then chains into the shared
// gallery Program.Main. On the browser Application.Run returns immediately (the
// dispatcher runs as a self-scheduling async pump), so Main returning does NOT end
// the app — the wasm runtime stays alive and keeps servicing the pump.
//

using System;
using System.Threading.Tasks;
using Microsoft.Wpf.Interop.WebGpu;

// AOT-profile collection trigger. In a -p:CollectAotProfile=true build (<WasmProfilers>aot),
// main.js points the mono AOT profiler's write-at-method at AotProfiling.Stop; INVOKING it from
// JS (after the gallery has rendered/scrolled) flushes the collected generic instantiations to
// INTERNAL.aotProfileData, which the Playwright driver saves as gallery.aotprofile. No-op in
// normal builds (the profiler isn't linked in, so nothing hooks this call).
internal static partial class AotProfiling
{
    // Unique name so the mono AOT profiler's write-at-method desc can't collide with a common
    // method like DispatcherTimer.Stop (the profiler matches loosely by method name).
    [System.Runtime.InteropServices.JavaScript.JSExport]
    internal static void WpfAotProfileFlush() { }
}

internal static class WasmBoot
{
    private static async Task<int> Main(string[] args)
    {
        try
        {
            Console.WriteLine("GalleryWasm: initializing WebGPU...");
            await WgpuBrowser.InitializeAsync();
            Console.WriteLine("GalleryWasm: WebGPU ready, starting WPF...");
            int rc = Program.Main(args);
            Console.WriteLine($"GalleryWasm: WPF started (pump running), Main rc={rc}");
            return rc;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"GalleryWasm BOOT FAILED: {ex}");
            return 1;
        }
    }
}
