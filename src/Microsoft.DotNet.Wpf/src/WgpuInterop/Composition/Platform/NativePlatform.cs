// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The one place the cross-platform WebGPU engine talks to an OS windowing API.
// Everything platform-specific (creating a presentable wgpu surface from a native
// window, pushing a bitmap to a transparent/layered popup window) is funnelled
// through here and dispatched to a per-OS backend: Win32Interop (Windows),
// MacInterop (macOS/Metal), IosInterop (UIKit/Metal), AndroidInterop (ANativeWindow),
// LinuxInterop (X11 -- stub). No raw P/Invoke to a platform API lives anywhere else
// in the engine, so porting to a new OS means adding one backend, not hunting Win32
// calls across the codebase.
//

using System;
using System.Runtime.InteropServices;

namespace Microsoft.Wpf.Interop.WebGpu.Composition.Platform
{
    internal enum PlatformKind { Windows, MacOS, Linux, Browser, IOS, Android, Unknown }

    internal static class NativePlatform
    {
        /// <summary>The OS this process is running on (decided once).</summary>
        public static PlatformKind Current { get; } = Detect();

        private static PlatformKind Detect()
        {
            if (OperatingSystem.IsBrowser()) return PlatformKind.Browser;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return PlatformKind.Windows;
            // iOS BEFORE macOS: both are Darwin, and an OSX probe can answer true on iOS, which
            // would send us down the AppKit path (NSView/NSWindow/NSScreen) on a UIKit process.
            if (OperatingSystem.IsIOS() || OperatingSystem.IsMacCatalyst() || OperatingSystem.IsTvOS())
                return PlatformKind.IOS;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return PlatformKind.MacOS;
            // Android BEFORE Linux, for the same reason iOS comes before macOS: Android IS Linux as
            // far as OSPlatform is concerned, so the Linux probe answers true there and would send
            // us down the X11 path on a device that has no X server at all.
            if (OperatingSystem.IsAndroid()) return PlatformKind.Android;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return PlatformKind.Linux;
            return PlatformKind.Unknown;
        }

        /// <summary>
        /// Create a presentable wgpu surface for a native window handle. The handle's
        /// meaning is platform-specific: an HWND on Windows, an NSView* on macOS, a
        /// UIView* on iOS, an X11 Window on Linux, and on Android a synthetic handle
        /// standing for a view whose ANativeWindow comes and goes (see AndroidInterop).
        /// Returns IntPtr.Zero if the platform is unsupported -- or, on Android, if the
        /// window has no live Surface yet, in which case the caller should retry.
        /// </summary>
        public static IntPtr CreateWindowSurface(IntPtr instance, IntPtr nativeWindow)
        {
            switch (Current)
            {
                case PlatformKind.Windows: return Win32Interop.CreateSurface(instance, nativeWindow);
                case PlatformKind.MacOS: return MacInterop.CreateSurface(instance, nativeWindow);
                case PlatformKind.IOS: return IosInterop.CreateSurface(instance, nativeWindow);
                case PlatformKind.Android: return AndroidInterop.CreateSurface(instance, nativeWindow);
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
            else if (Current == PlatformKind.IOS && nativeWindow != IntPtr.Zero)
            {
                IosInterop.SetContentsScale(nativeWindow, IosInterop.BackingScale(nativeWindow));
            }
        }

        /// <summary>
        /// The identity of the native window a surface was actually built on, used to notice that it
        /// has been REPLACED underneath a stable window handle. Only Android does that (its Surface is
        /// destroyed and recreated across every activity stop/start), so everywhere else this is the
        /// handle itself and the comparison never fires.
        /// </summary>
        public static IntPtr GetNativeWindow(IntPtr windowHandle)
            => Current == PlatformKind.Android ? AndroidInterop.GetNativeWindow(windowHandle) : windowHandle;

        /// <summary>
        /// Commit the platform compositor's pending transaction after a present, so a just-shown frame
        /// reaches the screen immediately even when the window then goes idle (see MacInterop.FlushTransaction).
        /// No-op where the swap-chain present already displays without a compositor transaction.
        /// </summary>
        public static void CommitPresent()
        {
            // iOS BEFORE macOS: iOS reports as macOS to OperatingSystem, and the two need the same
            // thing for the same reason -- both present through a CAMetalLayer, whose drawable
            // reaches the render server only when a Core Animation transaction commits. iOS was
            // missing from here entirely, which is a plain omission: everything the macOS comment
            // below describes applies to it unchanged.
            //
            // Found while chasing a second window failing to appear on iOS, and it did NOT fix that
            // -- so it is here on its own merits, not as that fix. See ManagedMessageBox's header
            // for what that investigation did establish.
            if (Current == PlatformKind.IOS)
                IosInterop.FlushTransaction();
            else if (Current == PlatformKind.MacOS)
                MacInterop.FlushTransaction();
            // Wayland's analogue: a just-presented frame sits in the connection's outgoing buffer
            // until something flushes it, so an app that then goes idle shows the PREVIOUS frame
            // until some unrelated event happens to wake the loop.
            else if (Current == PlatformKind.Linux)
                LinuxInterop.FlushDisplayHook?.Invoke();
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
        /// Whether this window can be presented to through a swap chain of its own, or must instead
        /// be drawn into the surface of the window it belongs to.
        /// </summary>
        /// <remarks>
        /// Only iOS answers false, and only for a popup, whose view the head deliberately creates
        /// WITHOUT a Metal layer because it expects the popup to be composited into its owner. The
        /// compositor used to infer that from MilTarget.IsLayered, which is a Windows per-pixel-alpha
        /// flag and is false for every popup off Windows -- so it built a Metal swap chain on a plain
        /// CALayer and all presentation stopped. Asking the platform is the honest question.
        /// </remarks>
        public static bool CanPresentToWindow(IntPtr nativeWindow)
            => Current != PlatformKind.IOS || IosInterop.CanPresentTo(nativeWindow);

        /// <summary>
        /// Where a window sits inside its owner, in device pixels. Only meaningful where popups are
        /// child views of one host window (see <see cref="PopupsShareOwnerSurface"/>); elsewhere a
        /// popup is its own OS window and presents itself, so this is never asked for.
        /// </summary>
        public static void GetWindowOrigin(IntPtr windowHandle, out int x, out int y)
        {
            x = y = 0;
            if (Current == PlatformKind.Android) AndroidInterop.GetWindowOrigin(windowHandle, out x, out y);
            else if (Current == PlatformKind.IOS) IosInterop.GetWindowOrigin(windowHandle, out x, out y);
            else if (Current == PlatformKind.Linux) LinuxInterop.OriginQuery?.Invoke(windowHandle, out x, out y);
        }

        /// <summary>
        /// True where a WPF popup cannot present through its own surface and must instead be drawn
        /// INTO the surface of the window it belongs to.
        ///
        /// That is the two mobile heads. On both, a popup is a child VIEW of the app's single window
        /// (an Android activity, an iOS UIWindow), so it already shares the owner's coordinate space
        /// and can simply be drawn into it. Everywhere else a popup is its own OS window that must
        /// present itself, and SupportsLayeredWindows/IsWindowOpaque decide how.
        ///
        /// On Android it is not merely nicer, it is the only thing that works: when wgpu falls back
        /// to GLES a popup surface cannot be transparent at all -- wgpu-hal hardcodes
        /// `composite_alpha_modes: vec![Opaque]` (unchanged through trunk; gfx-rs/wgpu#687 was closed
        /// by a PR covering only Metal and Vulkan) -- so clearing it transparent, which is what a
        /// Popup's rounded chrome and drop shadow need, scans out as solid BLACK around the popup.
        ///
        /// iOS has the same shape of problem for a different reason: it has no layered-window path
        /// either (SupportsLayeredWindows is false off Windows), so popups there used to present
        /// opaquely through their own swap chain and lost their shadow and rounded corners.
        ///
        /// Compositing sidesteps the popup surface entirely on both, and is the better arrangement
        /// regardless of backend: one swap chain instead of several, correct alpha, and popups stack
        /// in the order WPF asked for rather than by the platform's own view/layer ordering rules.
        /// </summary>
        /// <remarks>
        /// Linux joins the mobile heads for the Android reason exactly, now measured rather than
        /// assumed: on a GNOME/Wayland session the surface advertises `alphaModes: Opaque` and
        /// nothing else (tests/WaylandSpike prints the capability list), because wgpu-hal's GLES
        /// backend hardcodes `composite_alpha_modes: vec![Opaque]` and the GL backend is the one
        /// that reaches the GPU under VirGL. A popup surface that cannot be transparent clears to
        /// solid BLACK around the popup's rounded chrome and drop shadow.
        ///
        /// WPF_LINUX_COMPOSITE_POPUPS=0 opts out, for a Linux box whose Vulkan is real hardware and
        /// therefore does advertise premultiplied alpha.
        /// </remarks>
        public static bool PopupsShareOwnerSurface
            => Current is PlatformKind.Android or PlatformKind.IOS
               || (Current == PlatformKind.Linux &&
                   Environment.GetEnvironmentVariable("WPF_LINUX_COMPOSITE_POPUPS") != "0");

        /// <summary>
        /// Whether a window is currently on screen and worth presenting to.
        ///
        /// This exists for Wayland. A compositor stops delivering wl_surface.frame callbacks to a
        /// surface that is minimised or fully occluded, and a Fifo swap chain throttles on exactly
        /// those callbacks -- so wgpuSurfaceGetCurrentTexture blocks the UI thread until the window
        /// comes back. The app looks frozen, and on this head Fifo is often the ONLY present mode
        /// the surface advertises, so it cannot simply be avoided. Skipping the frame is correct
        /// anyway: nothing would have been seen.
        /// </summary>
        public static bool IsWindowVisible(IntPtr windowHandle)
        {
            if (Current == PlatformKind.Linux && LinuxInterop.VisibleQuery is not null)
                return LinuxInterop.VisibleQuery(windowHandle);
            return true;
        }

        /// <summary>
        /// Whether the native window backing a surface is opaque. A window made non-opaque for a
        /// translucent backdrop (the macOS Mica substitute installs an NSVisualEffectView and clears
        /// the window) must present through a transparent surface (alpha mode + transparent clear).
        /// Opaque everywhere without a notion of window transparency.
        /// </summary>
        public static bool IsWindowOpaque(IntPtr nativeWindow)
        {
            if (Current == PlatformKind.MacOS) return MacInterop.IsWindowOpaque(nativeWindow);
            // Windows: a window with a DWM system backdrop (Mica/Acrylic/Tabbed) is composited by DWM
            // OVER the desktop material and its native caption buttons — so it must present through a
            // transparent surface (premultiplied alpha) with the WPF-cleared transparent background, or
            // the opaque swapchain hides both the Mica and the caption buttons.
            if (Current == PlatformKind.Windows) return Win32Interop.IsWindowOpaque(nativeWindow);
            // Android: a WPF popup is its own view with a translucent surface (see AndroidInterop).
            if (Current == PlatformKind.Android) return AndroidInterop.IsWindowOpaque(nativeWindow);
            // Linux: borderless windows (popups, menus, tooltips) want a transparent surface where
            // the backend can give them one; the windowing layer answers, defaulting to opaque.
            if (Current == PlatformKind.Linux && LinuxInterop.OpaqueQuery is not null)
                return LinuxInterop.OpaqueQuery(nativeWindow);
            return true;
        }
    }
}
