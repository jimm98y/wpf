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
    /// (CocoaWindow on macOS, UIKitWindow on iOS, AndroidWindow on Android, BrowserWindow on
    /// WebAssembly). All rects follow the same
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

        /// <summary>Show or hide the window, for WPF's ShowWindow(SW_HIDE / SW_SHOW*) — i.e.
        /// Window.Hide(), Visibility=Collapsed, and the second Show() that follows.</summary>
        /// <remarks>
        /// Destroy() used to be the only way off the screen, so Hide() was silently a no-op on every
        /// non-Windows head: a window, once up, could not be taken down and put back. Anything that
        /// reuses a cached overlay window therefore accumulated them on screen. WPF's docking
        /// adorners are the clearest case — one hidden overlay per drag target, shown and hidden
        /// again on every drag — and they piled up as stale windows nothing would ever close.
        /// </remarks>
        void SetVisible(bool visible);

        /// <summary>
        /// As above, plus <paramref name="activate"/>: whether showing should also take focus —
        /// Win32's SW_SHOW vs SW_SHOWNA / SetWindowPos's SWP_NOACTIVATE.
        /// </summary>
        /// <remarks>
        /// WPF uses that flag to say which windows may steal focus: a Popup is always shown
        /// non-activating, an ordinary Window is not. Backends that cannot distinguish the two just
        /// show the window, which is what the single-argument overload always did.
        /// </remarks>
        void SetVisible(bool visible, bool activate) => SetVisible(visible);

        /// <summary>
        /// Bring this window to the front and give it focus — Win32's SetForegroundWindow /
        /// SetActiveWindow, i.e. what Window.Activate() comes down to.
        /// </summary>
        /// <remarks>
        /// Backends with no notion of activation do nothing, which is what these calls did everywhere
        /// off-Windows before: SetActiveWindow returned zero without acting, and SetForegroundWindow
        /// was a raw user32 P/Invoke, so Window.Activate() threw DllNotFoundException outright.
        /// </remarks>
        void Activate() { }

        /// <summary>
        /// The refresh rate of the display this window is on, in Hz, or 0 when the head cannot say.
        /// </summary>
        /// <remarks>
        /// WPF paces itself against the display: MediaContext schedules the next commit from the
        /// refresh period, and only falls back to a hardcoded "about a vblank" of 17ms when nothing
        /// has told it the rate -- which off Windows was every frame, because the notification that
        /// carries it (MilMessage.Presented) is sent by milcore and had no managed equivalent. 17ms
        /// is 58.8Hz, so every head ran at 60fps whatever the panel could do, and a 100Hz or 120Hz
        /// display was simply left on the table.
        ///
        /// Zero means unknown and keeps the old fallback, which is the right answer for a backend
        /// that genuinely cannot ask.
        /// </remarks>
        double GetRefreshRateHz() => 0;

        /// <summary>Begin an interactive window move, for WPF's caption drag (Window.DragMove and
        /// WindowChrome's caption area) — the window follows the mouse until the button is released.
        /// </summary>
        /// <remarks>
        /// Win32 runs a modal move loop inside DefWindowProc for this; each platform has its own
        /// equivalent, and a window with app-drawn chrome has no other way to be moved at all.
        /// </remarks>
        void BeginMoveDrag();

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
            if (OperatingSystem.IsAndroid())
                return AndroidWindow.FromHandle(handle);
            if (OperatingSystem.IsMacOS())
                return CocoaWindow.FromHandle(handle);
            // Linux LAST among the concrete probes, and after Android: Android is Linux as far as
            // OperatingSystem is concerned, exactly as iOS is Darwin.
            if (OperatingSystem.IsLinux())
                return Wayland.WaylandWindow.FromHandle(handle);
            return null;
        }

        /// <summary>Topmost window whose (device-pixel, screen-coordinate) bounds contain the point.</summary>
        public static IntPtr HitTest(int x, int y)
        {
            if (OperatingSystem.IsBrowser())
                return BrowserWindow.HitTest(x, y);
            if (OperatingSystem.IsIOS())
                return UIKitWindow.HitTest(x, y);
            if (OperatingSystem.IsAndroid())
                return AndroidWindow.HitTest(x, y);
            if (OperatingSystem.IsMacOS())
                return CocoaWindow.HitTest(x, y);
            if (OperatingSystem.IsLinux())
                return Wayland.WaylandWindow.HitTest(x, y);
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
                if (OperatingSystem.IsAndroid())
                    return AndroidWindow.MouseCaptureHandle;
                if (OperatingSystem.IsMacOS())
                    return CocoaWindow.MouseCaptureHandle;
                if (OperatingSystem.IsLinux())
                    return Wayland.WaylandWindow.MouseCaptureHandle;
                return IntPtr.Zero;
            }
            set
            {
                if (OperatingSystem.IsBrowser())
                    BrowserWindow.MouseCaptureHandle = value;
                else if (OperatingSystem.IsIOS())
                    UIKitWindow.MouseCaptureHandle = value;
                else if (OperatingSystem.IsAndroid())
                    AndroidWindow.MouseCaptureHandle = value;
                else if (OperatingSystem.IsMacOS())
                    CocoaWindow.MouseCaptureHandle = value;
                else if (OperatingSystem.IsLinux())
                    Wayland.WaylandWindow.MouseCaptureHandle = value;
            }
        }

        /// <summary>
        /// Set a window's title. Exists because the off-Windows path had no route for it at all:
        /// Window.UpdateTitle called SetWindowText, which P/Invokes user32 UNGUARDED, so setting
        /// Window.Title after SourceInitialized threw DllNotFoundException on every non-Windows
        /// platform -- macOS included, where CocoaWindow could have answered it all along.
        /// </summary>
        public static void SetTitle(IntPtr handle, string title)
        {
            if (handle == IntPtr.Zero) return;
            if (OperatingSystem.IsIOS() || OperatingSystem.IsAndroid() || OperatingSystem.IsBrowser())
                return;   // no window chrome to put a title in
            if (OperatingSystem.IsMacOS())
                CocoaWindow.SetTitle(handle, title);
            else if (OperatingSystem.IsLinux())
                Wayland.WaylandWindow.SetTitle(handle, title);
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
            if (OperatingSystem.IsAndroid())
                return AndroidWindow.GetPrimaryScreenPixels(
                    out monLeft, out monTop, out monRight, out monBottom,
                    out workLeft, out workTop, out workRight, out workBottom);
            if (OperatingSystem.IsMacOS())
                return CocoaWindow.GetPrimaryScreenPixels(
                    out monLeft, out monTop, out monRight, out monBottom,
                    out workLeft, out workTop, out workRight, out workBottom);
            if (OperatingSystem.IsLinux())
                return Wayland.WaylandWindow.GetPrimaryScreenPixels(
                    out monLeft, out monTop, out monRight, out monBottom,
                    out workLeft, out workTop, out workRight, out workBottom);
            monLeft = monTop = workLeft = workTop = 0;
            monRight = workRight = 1920;
            monBottom = workBottom = 1080;
            return false;
        }
    }
}
