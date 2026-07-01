// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The one place the cross-platform WebGPU engine talks to an OS windowing API.
// Everything platform-specific (creating a presentable wgpu surface from a native
// window, pushing a bitmap to a transparent/layered popup window) is funnelled
// through here and dispatched to a per-OS backend: Win32Interop (Windows),
// MacInterop (macOS/Metal), LinuxInterop (X11 -- stub). No raw P/Invoke to a
// platform API lives anywhere else in the engine, so porting to a new OS means
// adding one backend, not hunting Win32 calls across the codebase.
//

using System;
using System.Runtime.InteropServices;

namespace Microsoft.Wpf.Interop.WebGpu.Composition.Platform
{
    internal enum PlatformKind { Windows, MacOS, Linux, Unknown }

    internal static class NativePlatform
    {
        /// <summary>The OS this process is running on (decided once).</summary>
        public static PlatformKind Current { get; } = Detect();

        private static PlatformKind Detect()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return PlatformKind.Windows;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return PlatformKind.MacOS;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return PlatformKind.Linux;
            return PlatformKind.Unknown;
        }

        /// <summary>
        /// Create a presentable wgpu surface for a native window handle. The handle's
        /// meaning is platform-specific: an HWND on Windows, an NSView* on macOS, an
        /// X11 Window on Linux. Returns IntPtr.Zero if the platform is unsupported.
        /// </summary>
        public static IntPtr CreateWindowSurface(IntPtr instance, IntPtr nativeWindow)
        {
            switch (Current)
            {
                case PlatformKind.Windows: return Win32Interop.CreateSurface(instance, nativeWindow);
                case PlatformKind.MacOS: return MacInterop.CreateSurface(instance, nativeWindow);
                case PlatformKind.Linux: return LinuxInterop.CreateSurface(instance, nativeWindow);
                default: return IntPtr.Zero;
            }
        }

        /// <summary>
        /// Push a premultiplied, top-down RGBA frame to a transparent/layered popup
        /// window (WPF Popups on Windows). Returns false when the platform has no
        /// layered-window compositing path (macOS/Linux today), in which case the
        /// caller should fall back to a normal swap-chain present.
        /// </summary>
        public static bool TryPresentLayered(IntPtr nativeWindow, byte[] rgbaPremul, int width, int height)
        {
            switch (Current)
            {
                case PlatformKind.Windows: return Win32Interop.PresentLayered(nativeWindow, rgbaPremul, width, height);
                default: return false;   // no per-pixel-alpha popup path yet on macOS/Linux
            }
        }
    }
}
