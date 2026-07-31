// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The platform-neutral windowing seam for non-Windows platforms. The off-Windows
// guards in the shared MS.Win32 wrappers (and HwndTarget/HwndWrapper) used to call
// CocoaWindow directly; they now go through this facade so a third backend
// (BrowserWindow on WebAssembly) plugs in without re-touching every guard.
// Windows never reaches these paths (the guards are all `!OperatingSystem.IsWindows()`).
//

using System;

namespace MS.Internal.Interop
{
    /// <summary>The per-window operations shared by the non-Windows windowing backends
    /// (CocoaWindow on macOS, BrowserWindow on WebAssembly). All rects follow the same
    /// conventions the Win32 guards expect: content sizes in points (DIPs at scale 1),
    /// pixel sizes in device pixels, screen origins in top-left device pixels.</summary>
    public interface IPlatformWindow
    {
        bool IsBorderless { get; }
        void SetContentSize(int width, int height);

        /// <summary>Resize the content area to a size given in DEVICE PIXELS (as WPF's SetWindowPos
        /// supplies it). Platforms convert to their own content units; macOS keeps fractional points
        /// so an odd pixel width round-trips exactly at Retina scale (see CocoaWindow).</summary>
        void SetContentSizePixels(int cx, int cy);
        void SetFrameOrigin(int xPixels, int yPixels);
        void GetContentSize(out int width, out int height);
        void GetPixelSize(out int width, out int height);

        /// <summary>OUTER window (frame) size in device pixels = content view plus the non-client caption.
        /// GetWindowRect reports this (GetClientRect reports GetPixelSize) so WPF sees a non-zero
        /// non-client frame and Window.Width/Height behave as the outer window size like Win32. Equals
        /// GetPixelSize where the platform has no caption (borderless popups; the browser head).</summary>
        void GetWindowPixelSize(out int width, out int height);
        void GetClientScreenOriginPixels(out int sx, out int sy);
        double GetBackingScale();
        void Destroy();

        /// <summary>Raised (on the UI/pump thread) when the window's backing scale factor changes,
        /// e.g. it was dragged onto a display with a different DPI. Carries the new scale. The host
        /// (HwndTarget) updates its DPI scale, re-lays-out, and reconfigures the render surface.</summary>
        event Action<double> ScaleChanged;
    }

    /// <summary>Dispatches the handle-based windowing queries to the platform backend.</summary>
    public static class PlatformWindow
    {
        public static IPlatformWindow FromHandle(IntPtr handle)
        {
            if (OperatingSystem.IsBrowser())
                return BrowserWindow.FromHandle(handle);
            // iOS before macOS everywhere in this file: both are Darwin, and iOS must never fall
            // into the AppKit path (see NativePlatform.Detect).
            if (OperatingSystem.IsIOS())
                return UIKitWindow.FromHandle(handle);
            if (OperatingSystem.IsMacOS())
                return CocoaWindow.FromHandle(handle);
            return null;
        }

        /// <summary>Topmost window whose (device-pixel, screen-coordinate) bounds contain the point.</summary>
        public static IntPtr HitTest(int x, int y)
        {
            if (OperatingSystem.IsBrowser())
                return BrowserWindow.HitTest(x, y);
            if (OperatingSystem.IsIOS())
                return UIKitWindow.HitTest(x, y);
            if (OperatingSystem.IsMacOS())
                return CocoaWindow.HitTest(x, y);
            return IntPtr.Zero;
        }

        /// <summary>The window handle currently holding WPF mouse capture (GetCapture stand-in).</summary>
        public static IntPtr MouseCaptureHandle
        {
            get
            {
                if (OperatingSystem.IsBrowser())
                    return BrowserWindow.MouseCaptureHandle;
                if (OperatingSystem.IsIOS())
                    return UIKitWindow.MouseCaptureHandle;
                if (OperatingSystem.IsMacOS())
                    return CocoaWindow.MouseCaptureHandle;
                return IntPtr.Zero;
            }
            set
            {
                if (OperatingSystem.IsBrowser())
                    BrowserWindow.MouseCaptureHandle = value;
                else if (OperatingSystem.IsIOS())
                    UIKitWindow.MouseCaptureHandle = value;
                else if (OperatingSystem.IsMacOS())
                    CocoaWindow.MouseCaptureHandle = value;
            }
        }

        /// <summary>Primary screen bounds and work area in top-left device pixels.</summary>
        public static bool GetPrimaryScreenPixels(
            out int monLeft, out int monTop, out int monRight, out int monBottom,
            out int workLeft, out int workTop, out int workRight, out int workBottom)
        {
            if (OperatingSystem.IsBrowser())
                return BrowserWindow.GetPrimaryScreenPixels(
                    out monLeft, out monTop, out monRight, out monBottom,
                    out workLeft, out workTop, out workRight, out workBottom);
            if (OperatingSystem.IsIOS())
                return UIKitWindow.GetPrimaryScreenPixels(
                    out monLeft, out monTop, out monRight, out monBottom,
                    out workLeft, out workTop, out workRight, out workBottom);
            if (OperatingSystem.IsMacOS())
                return CocoaWindow.GetPrimaryScreenPixels(
                    out monLeft, out monTop, out monRight, out monBottom,
                    out workLeft, out workTop, out workRight, out workBottom);
            monLeft = monTop = workLeft = workTop = 0;
            monRight = workRight = 1920;
            monBottom = workBottom = 1080;
            return false;
        }
    }
}
