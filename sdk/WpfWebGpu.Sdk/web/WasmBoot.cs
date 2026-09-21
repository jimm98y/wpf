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
            InstallFirstChanceLogging();

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

    /// <summary>
    ///  With WPF_WEBGPU_LOG_FIRST_CHANCE=1 (?firstchance=1 on the page), print EVERY exception the
    ///  moment it is thrown, with its stack.
    /// </summary>
    /// <remarks>
    ///  There is no debugger to attach to a wasm page, and an app that catches an exception and shows
    ///  only its Message leaves nothing to diagnose from -- a browser port hits a run of
    ///  PlatformNotSupportedExceptions whose Message is the same generic sentence whatever the cause,
    ///  so the stack is the only thing that identifies WHICH api is missing. Off by default: this
    ///  fires for exceptions the app handles perfectly well, so it is noisy by design.
    /// </remarks>
    private static void InstallFirstChanceLogging()
    {
        if (Environment.GetEnvironmentVariable("WPF_WEBGPU_LOG_FIRST_CHANCE") != "1") return;

        AppDomain.CurrentDomain.FirstChanceException += (_, e) =>
        {
            try
            {
                Console.WriteLine($"FIRSTCHANCE {e.Exception.GetType().FullName}: {e.Exception.Message}\n{e.Exception.StackTrace}");
            }
            catch { }   // logging must never itself break the app
        };
        Console.WriteLine("WpfWebGpu: first-chance exception logging enabled");
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
