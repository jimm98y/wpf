// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// WebAssembly entry point injected by WpfWebGpu.Sdk: pre-acquires the WebGPU
// adapter/device (Promise-based, must complete before the WPF compositor starts),
// then chains into the application's own entry point — for a XAML app the
// markup-compiled App.Main, otherwise any static Main the assembly defines.
//
// On the browser Application.Run returns immediately (the dispatcher runs as a
// self-scheduling async pump), so Main returning does NOT end the app — the wasm
// runtime stays alive and keeps servicing the pump.
//

using System;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.Wpf.Interop.WebGpu;

internal static class WpfWebGpuWasmBoot
{
    private static async Task<int> Main(string[] args)
    {
        try
        {
            Console.WriteLine("WpfWebGpu: initializing WebGPU...");
            await WgpuBrowser.InitializeAsync();
            Console.WriteLine("WpfWebGpu: WebGPU ready, starting WPF...");

            MethodInfo entry = FindAppEntryPoint();
            if (entry == null)
            {
                Console.WriteLine("WpfWebGpu BOOT FAILED: no application entry point found " +
                    "(expected a static Main on a System.Windows.Application subclass or any other type).");
                return 1;
            }

            object result = entry.Invoke(null,
                entry.GetParameters().Length == 1 ? new object[] { args } : null);
            int rc = result is int i ? i : 0;
            Console.WriteLine($"WpfWebGpu: WPF started (pump running), Main rc={rc}");
            return rc;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"WpfWebGpu BOOT FAILED: {ex}");
            return 1;
        }
    }

    private static MethodInfo FindAppEntryPoint()
    {
        Assembly asm = typeof(WpfWebGpuWasmBoot).Assembly;
        const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        MethodInfo fallback = null;
        foreach (Type t in asm.GetTypes())
        {
            if (t == typeof(WpfWebGpuWasmBoot))
                continue;
            MethodInfo m = t.GetMethod("Main", flags, null,
                                new[] { typeof(string[]) }, null)
                        ?? t.GetMethod("Main", flags, null, Type.EmptyTypes, null);
            if (m == null)
                continue;
            // The markup-compiled XAML application (App.g.cs) wins; any other
            // static Main (a code-only Program) is the fallback.
            if (typeof(System.Windows.Application).IsAssignableFrom(t))
                return m;
            fallback ??= m;
        }
        return fallback;
    }
}
