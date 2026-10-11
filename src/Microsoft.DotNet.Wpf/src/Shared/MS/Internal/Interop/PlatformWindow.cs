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
using System.Collections.Generic;

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

        /// <summary>
        /// Minimize, maximize or restore. <paramref name="state"/> is the SW_* value WPF asked for.
        /// </summary>
        /// <remarks>
        /// A head that cannot express these does nothing, and the window stays the size it was --
        /// which is what every head except Wayland did before, except that the window ALSO never
        /// appeared: ShowWindow only called SetVisible for the SW_* values it recognised, and
        /// SW_SHOWMAXIMIZED was not among them. WPF asks for exactly that when a Window is created
        /// with WindowState=Maximized, so such a window was built, given a surface, told to composite
        /// -- and never ordered onto the screen. See UnsafeNativeMethodsCLR.ShowWindow.
        /// </remarks>
        void SetWindowState(int state) { }

        /// <summary>
        /// The state the window is in NOW, as the SW_* value <see cref="SetWindowState"/> takes:
        /// SW_SHOWMAXIMIZED, SW_SHOWMINIMIZED, or SW_NORMAL for everything else.
        /// </summary>
        /// <remarks>
        /// Setting a state was only ever half of it. WPF learns a window's state from the WM_SIZE it
        /// gets when the state changes -- the wParam says SIZE_MAXIMIZED, SIZE_MINIMIZED or
        /// SIZE_RESTORED -- and off Windows that message is synthesised from the head's resize
        /// notification, which had nothing to put in the wParam and always said SIZE_RESTORED.
        ///
        /// So maximizing was invisible to the framework. Window.WindowState stayed Normal when the
        /// user hit the zoom button, StateChanged never fired for any transition, and WindowChrome --
        /// which watches for SIZE_MAXIMIZED to add the padding that keeps a custom-chromed window's
        /// content off the screen edge -- never adjusted. See Window.WmSizeChanged.
        ///
        /// A head that cannot tell says SW_NORMAL, which is what the synthesised message said before
        /// and is the truth for the heads whose window IS the screen.
        /// </remarks>
        int GetWindowState() => 1;   // SW_NORMAL

        /// <summary>
        /// Where this window will be when it is restored: its outer rect in SCREEN device pixels,
        /// top-left origin, as it was the last time the window was in the normal state.
        /// </summary>
        /// <remarks>
        /// This is what Window.RestoreBounds reports, through GetWindowPlacement's rcNormalPosition,
        /// and it is the value an application persists to reopen where the user left it. The point of
        /// it is that a MAXIMIZED window still answers with its unmaximized rect -- so a head has to
        /// remember, because a maximized window no longer knows.
        ///
        /// Off Windows this used to be fabricated: the origin was always (0,0) and the size was
        /// whatever the window measured NOW, so RestoreBounds was wrong for every window and reported
        /// the full-screen size for a maximized one, which is the single case it exists to get right.
        ///
        /// The default answers with the window as it stands, which is correct whenever the window is
        /// not maximized and is the best a head that does not track this can do.
        /// </remarks>
        void GetRestoreBoundsPixels(out int x, out int y, out int width, out int height)
        {
            GetWindowScreenOriginPixels(out x, out y);
            GetWindowPixelSize(out width, out height);
        }

        /// <summary>
        /// The OUTER window's top-left corner in screen device pixels -- what Win32's GetWindowRect
        /// reports, and the origin WPF's Window.Left and Window.Top are read from.
        /// </summary>
        /// <remarks>
        /// Off Windows this was not reported at all: GetWindowRect answered with the right size at a
        /// hardcoded origin of (0,0). Everything downstream then followed from a window WPF believed
        /// was in the top-left corner. Window.Left and Window.Top read 0 wherever the window actually
        /// was; the SetWindowPos that WPF issues while showing a window carried that same (0,0), so it
        /// looked like a request to move the window to the corner; and the shim defended against that
        /// by ignoring moves for anything that was not a popup -- which is why setting Window.Left did
        /// nothing at all. Reporting the real origin removes the reason for all three.
        ///
        /// Defaults to the CLIENT origin, which is what a head with no separate window frame has.
        /// </remarks>
        void GetWindowScreenOriginPixels(out int x, out int y) => GetClientScreenOriginPixels(out x, out y);

        /// <summary>
        /// Keep this window above ordinary windows, or stop doing so -- WPF's Window.Topmost.
        /// </summary>
        /// <remarks>
        /// Topmost travels as a Z-ORDER argument: Window.OnTopmostChanged calls SetWindowPos with
        /// hWndInsertAfter set to HWND_TOPMOST or HWND_NOTOPMOST and no other instruction. Off Windows
        /// that argument was dropped on the floor -- "position/z-order are owned by AppKit" -- so the
        /// property did nothing at all, silently, on every head.
        ///
        /// A head with no say over stacking does nothing, which is the honest answer for Wayland: an
        /// xdg-shell client cannot ask to be kept on top, because the compositor decides stacking and
        /// offers no protocol for it.
        /// </remarks>
        void SetTopmost(bool topmost) { }

        /// <summary>
        /// Whether the user may resize this window, and whether it may be minimized -- WPF's
        /// ResizeMode, which is carried as WS_THICKFRAME and WS_MINIMIZEBOX (Window.CreateResizibility).
        /// </summary>
        /// <remarks>
        /// The head was never told. It receives only "borderless" and "chromeless", both derived from
        /// other bits of the same style word, so every window came out fully resizable whatever
        /// ResizeMode said: a NoResize dialog could be dragged bigger by its edge and zoomed by its
        /// green button, on every head, at creation and for ever after.
        ///
        /// Two booleans rather than the enum, because that is all a head can act on: macOS has no
        /// separate "maximize" permission -- the zoom button follows resizability -- so
        /// CanResize and CanResizeWithGrip are the same window here, which is also true of the grip
        /// itself (WPF draws that, not the platform).
        /// </remarks>
        void SetResizeMode(bool canResize, bool canMinimize) { }

        /// <summary>
        /// The chrome this window HAS, the counterpart of <see cref="SetResizeMode"/>.
        /// </summary>
        /// <remarks>
        /// Needed because WPF's style writes are READ-MODIFY-WRITE: HwndStyleManager reads the
        /// current style through GetWindowLong, ORs its change into it, and flushes the whole word
        /// back. A read that cannot answer for WS_THICKFRAME makes the flush strip resizability off
        /// a window that never asked -- which is why applying style changes at all had to wait for
        /// this. Defaults to "fully resizable", which is what every head did before it was asked.
        /// </remarks>
        void GetResizeMode(out bool canResize, out bool canMinimize)
        {
            canResize = true;
            canMinimize = true;
        }

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
        /// <summary>How many displays are attached. One, for a head whose window IS the screen.</summary>
        public static int GetMonitorCount()
        {
            if (OperatingSystem.IsWindows()) return WindowsMonitors().Count;
            return OperatingSystem.IsMacOS() ? CocoaWindow.GetMonitorCount() : 1;
        }

        // ---- Windows ------------------------------------------------------------------
        //
        // Forwarded to user32 rather than answered with a single display. Windows has had the real
        // answer all along -- the Win32 shims pass MonitorFromWindow and GetMonitorInfo straight
        // through there -- but the facade above them did not, so anything asking THROUGH it (this
        // repo's tests included) was told there was one display on a machine with several.
        //
        // The primary is moved to index 0 so "index 0 is the primary" holds on every platform.
        // EnumDisplayMonitors makes no promise about order, and the macOS head takes that ordering
        // from NSScreen.screens, where the primary is first by definition.

        [ThreadStatic] private static List<IntPtr> t_monitors;

        private static List<IntPtr> WindowsMonitors()
        {
            var found = new List<IntPtr>();
            t_monitors = found;
            try
            {
                MS.Win32.UnsafeNativeMethods.EnumDisplayMonitors(
                    IntPtr.Zero, IntPtr.Zero, CollectMonitor, IntPtr.Zero);
            }
            finally
            {
                t_monitors = null;
            }

            // Primary first. MONITORINFOF_PRIMARY is the only way to tell, and it is worth one extra
            // GetMonitorInfo per display: every caller of this facade indexes from the primary.
            for (int i = 0; i < found.Count; i++)
            {
                if (!IsPrimaryMonitor(found[i])) continue;
                if (i != 0) (found[0], found[i]) = (found[i], found[0]);
                break;
            }

            return found;
        }

        private static bool CollectMonitor(IntPtr hMonitor, IntPtr hdc, ref MS.Win32.NativeMethods.RECT rect, IntPtr data)
        {
            t_monitors?.Add(hMonitor);
            return true;   // keep enumerating
        }

        private static bool IsPrimaryMonitor(IntPtr hMonitor)
        {
            var info = new MS.Win32.NativeMethods.MONITORINFOEX
            {
                cbSize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(MS.Win32.NativeMethods.MONITORINFOEX)),
            };
            MS.Win32.SafeNativeMethods.GetMonitorInfo(
                new System.Runtime.InteropServices.HandleRef(null, hMonitor), info);
            return (info.dwFlags & 1) != 0;   // MONITORINFOF_PRIMARY
        }

        /// <summary>
        /// One display's full and working bounds in device pixels, top-left origin. Index 0 is the
        /// primary. Heads with a single display answer for it whatever index is asked for, which is
        /// what they did when there was only one answer to give.
        /// </summary>
        public static bool GetMonitorPixels(
            int index,
            out int monLeft, out int monTop, out int monRight, out int monBottom,
            out int workLeft, out int workTop, out int workRight, out int workBottom,
            out bool isPrimary)
        {
            if (OperatingSystem.IsWindows())
            {
                monLeft = monTop = monRight = monBottom = 0;
                workLeft = workTop = workRight = workBottom = 0;
                isPrimary = false;

                List<IntPtr> monitors = WindowsMonitors();
                if (index < 0 || index >= monitors.Count) return false;

                var info = new MS.Win32.NativeMethods.MONITORINFOEX
                {
                    cbSize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(MS.Win32.NativeMethods.MONITORINFOEX)),
                };
                MS.Win32.SafeNativeMethods.GetMonitorInfo(
                    new System.Runtime.InteropServices.HandleRef(null, monitors[index]), info);

                monLeft = info.rcMonitor.left; monTop = info.rcMonitor.top;
                monRight = info.rcMonitor.right; monBottom = info.rcMonitor.bottom;
                workLeft = info.rcWork.left; workTop = info.rcWork.top;
                workRight = info.rcWork.right; workBottom = info.rcWork.bottom;
                isPrimary = (info.dwFlags & 1) != 0;
                return monRight > monLeft && monBottom > monTop;
            }

            if (OperatingSystem.IsMacOS())
            {
                return CocoaWindow.GetMonitorPixels(index,
                    out monLeft, out monTop, out monRight, out monBottom,
                    out workLeft, out workTop, out workRight, out workBottom, out isPrimary);
            }

            isPrimary = true;
            return GetPrimaryScreenPixels(
                out monLeft, out monTop, out monRight, out monBottom,
                out workLeft, out workTop, out workRight, out workBottom);
        }

        /// <summary>
        /// The VIRTUAL SCREEN: the bounding box of every display, in device pixels, top-left origin.
        /// </summary>
        /// <remarks>
        /// What SM_XVIRTUALSCREEN and friends report, and therefore what SystemParameters.VirtualScreen*
        /// is built from -- values an application can bind to straight from XAML through
        /// SystemResourceKey. Off Windows they were a hardcoded 1920x1080 at the origin, which is not
        /// merely stale: the ORIGIN is wrong whenever a display sits left of or above the primary,
        /// which is where a second monitor most often goes.
        ///
        /// On a mixed-DPI arrangement this box is not expressible in any single scale, because each
        /// display is described in its own (see CocoaWindow's note on the coordinate space). The union
        /// is the best available answer and is the same one every other consumer of these rects works
        /// in; Windows has the same problem and resolves it the same way.
        /// </remarks>
        public static bool GetVirtualScreenPixels(out int left, out int top, out int width, out int height)
        {
            left = top = width = height = 0;

            int count = GetMonitorCount();
            bool any = false;
            int minLeft = int.MaxValue, minTop = int.MaxValue, maxRight = int.MinValue, maxBottom = int.MinValue;

            for (int i = 0; i < count; i++)
            {
                if (!GetMonitorPixels(i, out int ml, out int mt, out int mr, out int mb,
                                      out _, out _, out _, out _, out _))
                {
                    continue;
                }

                if (ml < minLeft) minLeft = ml;
                if (mt < minTop) minTop = mt;
                if (mr > maxRight) maxRight = mr;
                if (mb > maxBottom) maxBottom = mb;
                any = true;
            }

            if (!any) return false;

            left = minLeft;
            top = minTop;
            width = maxRight - minLeft;
            height = maxBottom - minTop;
            return width > 0 && height > 0;
        }

        /// <summary>The display a window is on, as an index; 0 when the head has only one.</summary>
        public static int MonitorIndexFromWindow(IntPtr handle)
        {
            if (OperatingSystem.IsWindows())
            {
                const int MONITOR_DEFAULTTONEAREST = 0x00000002;
                return IndexOfWindowsMonitor(MS.Win32.SafeNativeMethods.MonitorFromWindow(
                    new System.Runtime.InteropServices.HandleRef(null, handle), MONITOR_DEFAULTTONEAREST));
            }

            return FromHandle(handle) is CocoaWindow cocoa ? cocoa.GetMonitorIndex() : 0;
        }

        /// <summary>The display containing a point in device pixels, or the nearest one.</summary>
        public static int MonitorIndexFromPointPixels(int x, int y)
        {
            if (OperatingSystem.IsWindows())
            {
                const int MONITOR_DEFAULTTONEAREST = 0x00000002;
                var point = new MS.Win32.NativeMethods.POINT(x, y);
                return IndexOfWindowsMonitor(
                    MS.Win32.SafeNativeMethods.MonitorFromPoint(point, MONITOR_DEFAULTTONEAREST));
            }

            return OperatingSystem.IsMacOS() ? CocoaWindow.MonitorIndexFromPointPixels(x, y) : 0;
        }

        private static int IndexOfWindowsMonitor(IntPtr hMonitor)
        {
            List<IntPtr> monitors = WindowsMonitors();
            int index = monitors.IndexOf(hMonitor);
            return index < 0 ? 0 : index;
        }

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

            // WINDOWS. This head fell through to the 1920x1080 below and returned false, so
            // SystemParameters.PrimaryScreen* and everything built on it -- window centring, maximized
            // bounds, Screen.PrimaryScreen -- answered with a made-up display. It went unnoticed
            // because the value is a plausible one: it is only wrong on a machine that is not
            // 1920x1080, and it took a desktop of 1710x1073 to make the virtual-screen test say so.
            // The monitor enumeration right above has always read the real thing; this asks the same
            // API for the display flagged primary.
            if (OperatingSystem.IsWindows())
            {
                int count = GetMonitorCount();
                for (int i = 0; i < count; i++)
                {
                    if (GetMonitorPixels(i, out monLeft, out monTop, out monRight, out monBottom,
                                         out workLeft, out workTop, out workRight, out workBottom,
                                         out bool isPrimary) && isPrimary)
                    {
                        return true;
                    }
                }

                // No display claimed to be primary (a remote session mid-reconnect can look like
                // this). The first one that answered is a better guess than a constant.
                if (count > 0 &&
                    GetMonitorPixels(0, out monLeft, out monTop, out monRight, out monBottom,
                                     out workLeft, out workTop, out workRight, out workBottom, out _))
                {
                    return true;
                }
            }

            monLeft = monTop = workLeft = workTop = 0;
            monRight = workRight = 1920;
            monBottom = workBottom = 1080;
            return false;
        }
    }
}
