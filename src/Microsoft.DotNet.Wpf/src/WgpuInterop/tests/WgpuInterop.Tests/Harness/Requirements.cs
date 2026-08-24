// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// What a test needs from the machine it is running on, and what to do when the machine cannot
// provide it.
//
// The rule this file exists to enforce: a test that CANNOT run must SKIP WITH A REASON, never pass.
// A suite that quietly returns green on a machine with no GPU is worse than no suite, because it
// reports coverage it did not deliver -- and this suite is meant to run on three operating systems
// where the honest answer differs per box (no adapter in CI, no display in a container, no Wayland
// on macOS).
//
// Skips are evaluated ONCE per process and cached: probing for a GPU adapter costs a device
// creation, and doing that per test would dominate the run.
//

using System;
using System.Runtime.InteropServices;
using Microsoft.Wpf.Interop.WebGpu.Composition;
using Xunit;

namespace WgpuInterop.Tests.Harness
{
    internal static class Requirements
    {
        private static readonly Lazy<string?> s_gpuUnavailable = new(ProbeGpu);

        /// <summary>
        /// Null when a WebGPU adapter+device can be created, otherwise the reason it cannot.
        /// Probed once; the probe result is what every GPU test gates on.
        /// </summary>
        public static string? GpuUnavailable => s_gpuUnavailable.Value;

        private static string? ProbeGpu()
        {
            try
            {
                using WgpuContext ctx = WgpuContext.Create();
                return ctx is null ? "WgpuContext.Create returned null" : null;
            }
            catch (DllNotFoundException e)
            {
                return $"wgpu-native is not staged next to the test binary ({e.Message}). " +
                       "Run eng/fetch-wgpu.ps1 for this RID.";
            }
            catch (Exception e)
            {
                return $"no usable WebGPU adapter on this machine: {e.GetType().Name}: {e.Message}";
            }
        }

        /// <summary>Skip the calling test unless a GPU device is available.</summary>
        public static void RequireGpu()
        {
            string? why = GpuUnavailable;
            Assert.SkipWhen(why is not null, $"requires a WebGPU device: {why}");
        }

        /// <summary>
        /// Skip unless there is a display server to open a window on. Checked by environment rather
        /// than by trying, because a failed window creation on a headless box tends to abort rather
        /// than throw.
        /// </summary>
        public static void RequireDisplay()
        {
            bool has = OperatingSystem.IsWindows()
                    || OperatingSystem.IsMacOS()
                    || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"))
                    || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"));
            Assert.SkipUnless(has, "requires a display server (no WAYLAND_DISPLAY or DISPLAY set)");
        }

        /// <summary>Skip unless running on Linux under a Wayland session (the Linux windowing head).</summary>
        public static void RequireWayland()
        {
            Assert.SkipUnless(OperatingSystem.IsLinux(), "Wayland head: Linux only");
            Assert.SkipUnless(!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")),
                "Wayland head: requires a Wayland session (WAYLAND_DISPLAY is not set)");
        }

        public static void RequireMacOS()
            => Assert.SkipUnless(OperatingSystem.IsMacOS(), "Cocoa head: macOS only");

        public static void RequireWindows()
            => Assert.SkipUnless(OperatingSystem.IsWindows(), "Win32 head: Windows only");

        /// <summary>A one-line description of the machine, printed once so a run's scope is auditable.</summary>
        public static string Describe()
        {
            string os = OperatingSystem.IsWindows() ? "Windows"
                      : OperatingSystem.IsMacOS() ? "macOS"
                      : OperatingSystem.IsLinux() ? "Linux" : "unknown";
            string display = OperatingSystem.IsLinux()
                ? (Environment.GetEnvironmentVariable("WAYLAND_DISPLAY") is { Length: > 0 } w ? $"wayland:{w}"
                   : Environment.GetEnvironmentVariable("DISPLAY") is { Length: > 0 } x ? $"x11:{x}" : "headless")
                : "native";
            return $"{os}/{RuntimeInformation.ProcessArchitecture} display={display} " +
                   $"gpu={(GpuUnavailable is null ? "yes" : "NO -> " + GpuUnavailable)}";
        }
    }
}
