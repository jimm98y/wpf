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
    internal enum PlatformKind { Windows, MacOS, Linux, Browser, Unknown }

    internal static class NativePlatform
    {
        /// <summary>The OS this process is running on (decided once).</summary>
        public static PlatformKind Current { get; } = Detect();

        private static PlatformKind Detect()
        {
            if (OperatingSystem.IsBrowser()) return PlatformKind.Browser;
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
#if WGPU_BROWSER
                case PlatformKind.Browser: return BrowserInterop.CreateSurface(instance, nativeWindow);
#endif
                default: return IntPtr.Zero;
            }
        }

        /// <summary>
        /// Re-sync the native surface's backing/contents scale to the window's current backing scale.
        /// Called when a window's DPI changes (dragged to a different-density display) so the
        /// CAMetalLayer keeps mapping its device-pixel drawable 1:1 onto the point-sized view. No-op
        /// where there is no such concept.
        /// </summary>
        public static void UpdateContentsScale(IntPtr nativeWindow)
        {
            if (Current == PlatformKind.MacOS && nativeWindow != IntPtr.Zero)
            {
                MacInterop.SetContentsScale(nativeWindow, MacInterop.BackingScale(nativeWindow));
            }
        }

        /// <summary>
        /// Commit the platform compositor's pending transaction after a present, so a just-shown frame
        /// reaches the screen immediately even when the window then goes idle (see MacInterop.FlushTransaction).
        /// No-op where the swap-chain present already displays without a compositor transaction.
        /// </summary>
        public static void CommitPresent()
        {
            if (Current == PlatformKind.MacOS)
                MacInterop.FlushTransaction();
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

        /// <summary>
        /// True where the OS has a per-pixel-alpha layered-window compositing path (Windows'
        /// UpdateLayeredWindow). Where false (macOS/Linux), popups are presented opaquely through a
        /// normal swap-chain surface instead.
        /// </summary>
        public static bool SupportsLayeredWindows => Current == PlatformKind.Windows;

        /// <summary>
        /// Whether the native window backing a surface is opaque. A window made non-opaque for a
        /// translucent backdrop (the macOS Mica substitute installs an NSVisualEffectView and clears
        /// the window) must present through a transparent surface (alpha mode + transparent clear).
        /// Opaque everywhere without a notion of window transparency.
        /// </summary>
        public static bool IsWindowOpaque(IntPtr nativeWindow)
        {
            return Current != PlatformKind.MacOS || MacInterop.IsWindowOpaque(nativeWindow);
        }
    }
}
