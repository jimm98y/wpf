// user32, the window functions: an HWND an app holds is one of
//   - a WPF window (or popup), behind an HwndSource: WindowInteropHelper.Handle gave it out;
//   - a WinForms control, behind Control.Handle;
//   - a window the app created itself with CreateWindowEx, typically a hidden message-only window that
//     exists to receive messages. Those are emulated here in full: the class's window procedure is
//     called for SendMessage, posted messages and timers arrive on the creating thread.
// Each function acts on whichever of the three the handle is, on that window's own UI thread.

using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace WpfMac.Win32;

public static unsafe partial class Exports
{
    private const uint WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000, WS_DISABLED = 0x08000000, WS_MINIMIZE = 0x20000000,
        WS_MAXIMIZE = 0x01000000, WS_OVERLAPPEDWINDOW = 0x00CF0000, WS_CLIPCHILDREN = 0x02000000, WS_CLIPSIBLINGS = 0x04000000;
    private const uint WS_EX_TOPMOST = 0x8, WS_EX_TOOLWINDOW = 0x80, WS_EX_WINDOWEDGE = 0x100, WS_EX_APPWINDOW = 0x40000;
    private const int GWL_WNDPROC = -4, GWL_HINSTANCE = -6, GWL_HWNDPARENT = -8, GWL_STYLE = -16, GWL_EXSTYLE = -20, GWL_USERDATA = -21, GWL_ID = -12;

    internal static readonly nint DesktopWindow = 0x10010;


    // ---- resolving a handle --------------------------------------------------------------------

    /// <summary>What an HWND is here: a WPF root (Window or popup), a WinForms Control, or an emulated window.</summary>
    internal static object? Resolve(nint hwnd)
    {
        if (hwnd == 0) return null;
        if (s_appWindows.TryGetValue(hwnd, out var own)) return own;
        try
        {
            if (HwndSource.FromHwnd(hwnd) is HwndSource source && !source.IsDisposed) return source;
        }
        catch
        {
        }
        try
        {
            return Forms.Control.FromHandle(hwnd);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Runs <paramref name="f"/> on the thread that owns <paramref name="o"/>, as SendMessage would.</summary>
    internal static T Ui<T>(object? o, Func<T> f)
    {
        switch (o)
        {
            case DispatcherObject d when !d.CheckAccess():
                return d.Dispatcher.Invoke(f);
            case Forms.Control c when c.InvokeRequired:
                return (T)c.Invoke(f)!;
            default:
                return f();
        }
    }

    private static nint HandleOf(object? o) => o switch
    {
        HwndSource s => s.Handle,
        Window w => new WindowInteropHelper(w).Handle,
        Forms.Control c when c.IsHandleCreated => c.Handle,
        AppWindow a => a.Handle,
        _ => 0,
    };

    private static Window? WindowOf(HwndSource s) => s.RootVisual as Window;

    /// <summary>Every top-level window, WPF's and WinForms', in no particular z-order.</summary>
    internal static List<object> TopLevels()
    {
        var list = new List<object>();
        try
        {
            foreach (PresentationSource ps in PresentationSource.CurrentSources)
            {
                if (ps is HwndSource s && !s.IsDisposed) list.Add(s);
            }
        }
        catch
        {
        }
        try
        {
            foreach (Forms.Form f in Forms.Application.OpenForms) list.Add(f);
        }
        catch
        {
        }
        list.AddRange(s_appWindows.Values.Where(a => a.Parent == 0));
        return list;
    }

    // ---- geometry ------------------------------------------------------------------------------

    private static RECT RectOf(double x, double y, double w, double h) => new()
    {
        Left = (int)Math.Round(x), Top = (int)Math.Round(y), Right = (int)Math.Round(x + w), Bottom = (int)Math.Round(y + h),
    };

    internal static RECT WindowRect(nint hwnd, out bool found)
    {
        object? o = Resolve(hwnd);
        found = o != null;
        return Ui(o, () => o switch
        {
            HwndSource s when WindowOf(s) is Window w => RectOf(double.IsNaN(w.Left) ? 0 : w.Left, double.IsNaN(w.Top) ? 0 : w.Top, w.ActualWidth, w.ActualHeight),
            HwndSource s when s.RootVisual is FrameworkElement fe && fe.IsLoaded => ScreenRectOf(fe),
            Forms.Form f => RectOf(f.Left, f.Top, f.Width, f.Height),
            Forms.Control c => ControlScreenRect(c),
            AppWindow a => a.Rect,
            _ => default,
        });
    }

    private static RECT ScreenRectOf(FrameworkElement fe)
    {
        Point p = fe.PointToScreen(new Point(0, 0));
        return RectOf(p.X, p.Y, fe.ActualWidth, fe.ActualHeight);
    }

    private static RECT ControlScreenRect(Forms.Control c)
    {
        // Without creating a window. RectangleToScreen reads the parent's Handle, and a parent that
        // has none yet (WinForms makes them lazily; on Windows a child's window cannot exist without
        // its parent's) was created by the question: DotNetBar's Bar, from inside its own
        // RecalcSize, whose OnHandleCreated then skipped the dock tab strip for good (app's
        // Explorer and Properties had no tabs). Ancestors without a window are walked by position.
        var origin = c.Location;
        Forms.Control? p = c.Parent;
        while (p != null && !p.IsHandleCreated)
        {
            origin.Offset(p.Left + (p.Width - p.ClientSize.Width) / 2, p.Top + (p.Height - p.ClientSize.Height) / 2);
            p = p.Parent;
        }
        if (p != null) origin = p.PointToScreen(origin);
        return RectOf(origin.X, origin.Y, c.Width, c.Height);
    }

    [Win32Export("GetWindowRect")]
    [Win32ExportOnWindows(A0, "GetWindowRect")]
    public static int GetWindowRectExport(nint hwnd, RECT* r)
    {
        RECT rect = WindowRect(hwnd, out bool found);
        if (!found) { SetLastError(ERROR_INVALID_WINDOW_HANDLE); return FALSE; }
        *r = rect;
        return TRUE;
    }

    [Win32Export("GetClientRect")]
    [Win32ExportOnWindows(A0, "GetClientRect")]
    public static int GetClientRect(nint hwnd, RECT* r)
    {
        object? o = Resolve(hwnd);
        if (o == null) { SetLastError(ERROR_INVALID_WINDOW_HANDLE); return FALSE; }
        *r = Ui(o, () => o switch
        {
            HwndSource s when WindowOf(s) is Window w && w.Content is FrameworkElement content => RectOf(0, 0, content.ActualWidth, content.ActualHeight),
            HwndSource s when s.RootVisual is FrameworkElement fe => RectOf(0, 0, fe.ActualWidth, fe.ActualHeight),
            // Inside the frame the stack draws within the window, as Windows keeps it out of the client.
            Forms.Control c => RectOf(0, 0, c.ClientSize.Width - 2 * DriverInt("NonClientInset", hwnd), c.ClientSize.Height - 2 * DriverInt("NonClientInset", hwnd)),
            AppWindow a => RectOf(0, 0, a.Rect.Right - a.Rect.Left, a.Rect.Bottom - a.Rect.Top),
            _ => default,
        });
        return TRUE;
    }

    /// <summary>The screen position of a window's client origin.</summary>
    private static POINT ClientOrigin(object? o) => Ui(o, () => o switch
    {
        HwndSource s when WindowOf(s) is Window w && w.Content is UIElement content && content.IsVisible => P(content.PointToScreen(new Point(0, 0))),
        HwndSource s when s.RootVisual is UIElement e && e.IsVisible => P(e.PointToScreen(new Point(0, 0))),
        Forms.Control c => new POINT { X = c.PointToScreen(System.Drawing.Point.Empty).X, Y = c.PointToScreen(System.Drawing.Point.Empty).Y },
        AppWindow a => new POINT { X = a.Rect.Left, Y = a.Rect.Top },
        _ => default,
    });

    private static POINT P(Point p) => new() { X = (int)Math.Round(p.X), Y = (int)Math.Round(p.Y) };

    [Win32Export("ClientToScreen")]
    [Win32ExportOnWindows(A0, "ClientToScreen")]
    public static int ClientToScreen(nint hwnd, POINT* p)
    {
        object? o = Resolve(hwnd);
        if (o == null) return FALSE;
        POINT origin = ClientOrigin(o);
        p->X += origin.X; p->Y += origin.Y;
        return TRUE;
    }

    [Win32Export("ScreenToClient")]
    [Win32ExportOnWindows(A0, "ScreenToClient")]
    public static int ScreenToClient(nint hwnd, POINT* p)
    {
        object? o = Resolve(hwnd);
        if (o == null) return FALSE;
        POINT origin = ClientOrigin(o);
        p->X -= origin.X; p->Y -= origin.Y;
        return TRUE;
    }

    [Win32Export("MapWindowPoints")]
    [Win32ExportOnWindows(A0 | A1, "MapWindowPoints")]
    public static int MapWindowPoints(nint from, nint to, POINT* points, uint count)
    {
        POINT a = from == 0 || from == DesktopWindow ? default : ClientOrigin(Resolve(from));
        POINT b = to == 0 || to == DesktopWindow ? default : ClientOrigin(Resolve(to));
        int dx = a.X - b.X, dy = a.Y - b.Y;
        for (int i = 0; i < count; i++) { points[i].X += dx; points[i].Y += dy; }
        return (dy << 16) | (dx & 0xFFFF);
    }

    private static void Place(object? o, int? x, int? y, int? w, int? h)
    {
        Ui(o, () =>
        {
            switch (o)
            {
                case HwndSource s when WindowOf(s) is Window win:
                    if (x.HasValue) win.Left = x.Value;
                    if (y.HasValue) win.Top = y.Value;
                    if (w.HasValue) win.Width = w.Value;
                    if (h.HasValue) win.Height = h.Value;
                    break;
                case Forms.Control c:
                    c.SetBounds(x ?? c.Left, y ?? c.Top, w ?? c.Width, h ?? c.Height);
                    break;
                case AppWindow a:
                    a.Rect = RectOf(x ?? a.Rect.Left, y ?? a.Rect.Top, w ?? a.Rect.Right - a.Rect.Left, h ?? a.Rect.Bottom - a.Rect.Top);
                    break;
            }
            return 0;
        });
    }

    [Win32Export("SetWindowPos")]
    [Win32ExportOnWindows(A0, "SetWindowPos")]
    public static int SetWindowPos(nint hwnd, nint after, int x, int y, int cx, int cy, uint flags)
    {
        object? o = Resolve(hwnd);
        if (o == null) { SetLastError(ERROR_INVALID_WINDOW_HANDLE); return FALSE; }
        bool move = (flags & 0x2) == 0, size = (flags & 0x1) == 0;
        Place(o, move ? x : null, move ? y : null, size ? cx : null, size ? cy : null);
        if ((flags & 0x4) == 0 && (after == -1 || after == -2)) SetTopmost(o, after == -1);   // HWND_TOPMOST / NOTOPMOST
        if ((flags & 0x40) != 0) Show(o, 5);     // SWP_SHOWWINDOW
        if ((flags & 0x80) != 0) Show(o, 0);     // SWP_HIDEWINDOW
        if ((flags & 0x20) != 0 && o is Forms.Control) RecalculateFrame(hwnd);   // SWP_FRAMECHANGED
        return TRUE;
    }

    /// <summary>SWP_FRAMECHANGED on a WinForms window: the driver asks it for its frame again
    /// (WM_NCCALCSIZE), as Windows does.</summary>
    private static void RecalculateFrame(nint hwnd)
    {
        if (Resolve(hwnd) is not Forms.Control c) return;
        Ui(c, () =>
        {
            object? driver = typeof(Forms.Control).Assembly.GetType("System.Windows.Forms.XplatUI")
                ?.GetField("driver", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)?.GetValue(null);
            driver?.GetType().GetMethod("RecalculateFrame", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                ?.Invoke(driver, new object[] { (IntPtr)hwnd });
            return 0;
        });
    }

    [Win32Export("MoveWindow")]
    [Win32ExportOnWindows(A0, "MoveWindow")]
    public static int MoveWindow(nint hwnd, int x, int y, int w, int h, int repaint)
    {
        object? o = Resolve(hwnd);
        if (o == null) { SetLastError(ERROR_INVALID_WINDOW_HANDLE); return FALSE; }
        Place(o, x, y, w, h);
        return TRUE;
    }

    private static void SetTopmost(object? o, bool topmost) => Ui(o, () =>
    {
        if (o is HwndSource s && WindowOf(s) is Window w) w.Topmost = topmost;
        else if (o is Forms.Form f) f.TopMost = topmost;
        // A top-level window that is not a form (a DotNetBar popup and its shadow): the driver's
        // topmost band, which Form.TopMost goes through too.
        else if (o is Forms.Control c && c.IsHandleCreated && c.Parent == null)
            typeof(Forms.Control).Assembly.GetType("System.Windows.Forms.XplatUI")
                ?.GetMethod("SetTopmost", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                ?.Invoke(null, new object[] { c.Handle, topmost });
        return 0;
    });

    // ---- state ---------------------------------------------------------------------------------

    [Win32Export("GetDesktopWindow", "GetShellWindow")] public static nint GetDesktopWindow() => DesktopWindow;
    [Win32Export("IsWindow")] [Win32ExportOnWindows(A0, "IsWindow")] public static int IsWindow(nint hwnd) => hwnd == DesktopWindow || Resolve(hwnd) != null ? TRUE : FALSE;

    private static nint ActiveWindowHandle()
    {
        // macOS: as Windows answers it, from the window manager -- the top-level window whose native
        // window is AppKit's key window, or the key window's parent (a form whose ElementHost's WPF
        // window has the keyboard). WinForms' own bookkeeping (Form.ActiveForm) lags a key change by a
        // poll: a double-click that moved the keyboard into a form's WPF content asked in that gap,
        // got no window, and the dialog it opened had no owner to centre on.
        if (OperatingSystem.IsMacOS() && KeyTopLevel() is nint key && key != 0) return key;
        foreach (object o in TopLevels())
        {
            bool active = Ui(o, () => o switch
            {
                HwndSource s when WindowOf(s) is Window w => w.IsActive,
                Forms.Form f => Forms.Form.ActiveForm == f,
                _ => false,
            });
            if (active) return HandleOf(o);
        }
        return 0;
    }

    private static nint KeyTopLevel()
    {
        IntPtr app = MacNative.Send(MacNative.Class("NSApplication"), MacNative.Sel("sharedApplication"));
        IntPtr keyWin = app == IntPtr.Zero ? IntPtr.Zero : MacNative.Send(app, MacNative.Sel("keyWindow"));
        if (keyWin == IntPtr.Zero) return 0;
        IntPtr parent = MacNative.Send(keyWin, MacNative.Sel("parentWindow"));
        var hostWindowOf = AppContext.GetData("WinFormsWebGpu.HostWindowOf") as Func<IntPtr, IntPtr>;
        foreach (object o in TopLevels())
        {
            nint h = HandleOf(o);
            if (h == 0) continue;
            // the native window behind it: a WPF window's handle is its content view; a form's is the
            // driver's, whose host view WinForms publishes
            IntPtr view = o is Forms.Control ? (hostWindowOf?.Invoke(h) ?? IntPtr.Zero) : h;
            IntPtr win = view == IntPtr.Zero ? IntPtr.Zero : MacNative.Send(view, MacNative.Sel("window"));
            if (win != IntPtr.Zero && (win == keyWin || win == parent)) return h;
        }
        return 0;
    }

    [Win32Export("GetActiveWindow", "GetForegroundWindow")] public static nint GetActiveWindow() => ActiveWindowHandle();

    [Win32Export("GetFocus")]
    public static nint GetFocus()
    {
        nint active = ActiveWindowHandle();
        object? o = Resolve(active);
        if (o is Forms.Form f)
        {
            nint focused = Ui(f, () => f.ActiveControl is { IsHandleCreated: true } c ? c.Handle : 0);
            if (focused != 0) return focused;
        }
        return active;
    }

    [Win32Export("SetFocus")]
    [Win32ExportOnWindows(A0, "SetFocus")]
    public static nint SetFocus(nint hwnd)
    {
        nint previous = ActiveWindowHandle();
        object? o = Resolve(hwnd);
        Ui(o, () => o switch
        {
            Forms.Control c => c.Focus(),
            HwndSource s when WindowOf(s) is Window w => w.Focus(),
            _ => false,
        });
        return previous;
    }

    private static bool Activate(object? o) => Ui(o, () => o switch
    {
        HwndSource s when WindowOf(s) is Window w => w.Activate(),
        Forms.Form f => Do(f.Activate),
        _ => false,
    });

    private static bool Do(Action a) { a(); return true; }

    [Win32Export("SetForegroundWindow", "BringWindowToTop")] [Win32ExportOnWindows(A0, "SetForegroundWindow", "BringWindowToTop")] public static int SetForegroundWindow(nint hwnd) => Activate(Resolve(hwnd)) ? TRUE : FALSE;

    [Win32Export("SetActiveWindow")]
    [Win32ExportOnWindows(A0, "SetActiveWindow")]
    public static nint SetActiveWindow(nint hwnd)
    {
        nint previous = ActiveWindowHandle();
        Activate(Resolve(hwnd));
        return previous;
    }

    private static bool IsVisible(object? o) => Ui(o, () => o switch
    {
        HwndSource s when WindowOf(s) is Window w => w.IsVisible,
        HwndSource s when s.RootVisual is UIElement e => e.IsVisible,
        Forms.Control c => c.Visible,
        AppWindow a => (a.Style & WS_VISIBLE) != 0,
        _ => false,
    });

    [Win32Export("IsWindowVisible")] [Win32ExportOnWindows(A0, "IsWindowVisible")] public static int IsWindowVisible(nint hwnd) => IsVisible(Resolve(hwnd)) ? TRUE : FALSE;

    private static bool IsEnabled(object? o) => Ui(o, () => o switch
    {
        HwndSource s when s.RootVisual is UIElement e => e.IsEnabled,
        Forms.Control c => c.Enabled,
        AppWindow a => (a.Style & WS_DISABLED) == 0,
        _ => false,
    });

    [Win32Export("IsWindowEnabled")] [Win32ExportOnWindows(A0, "IsWindowEnabled")] public static int IsWindowEnabled(nint hwnd) => IsEnabled(Resolve(hwnd)) ? TRUE : FALSE;

    // Touch registration on the stack's windows. They are not Windows windows, so user32 refused
    // them -- and DotNetBar's TouchHandler throws "Cannot register window" when RegisterTouchWindow
    // fails, out of every ItemControl's OnHandleCreated (app's "Configure your Control"). The
    // registration is kept per window and answered as Windows answers it; touch itself reaches the
    // application as pointer input through the host window (WM_POINTER), as for every other control.
    private static readonly ConcurrentDictionary<nint, uint> s_touchWindows = new();

    [Win32Export("RegisterTouchWindow")]
    [Win32ExportOnWindows(A0, "RegisterTouchWindow")]
    public static int RegisterTouchWindow(nint hwnd, uint flags)
    {
        if (Resolve(hwnd) == null) { SetLastError(ERROR_INVALID_WINDOW_HANDLE); return FALSE; }
        s_touchWindows[hwnd] = flags;
        return TRUE;
    }

    [Win32Export("UnregisterTouchWindow")]
    [Win32ExportOnWindows(A0, "UnregisterTouchWindow")]
    public static int UnregisterTouchWindow(nint hwnd)
    {
        if (Resolve(hwnd) == null) { SetLastError(ERROR_INVALID_WINDOW_HANDLE); return FALSE; }
        s_touchWindows.TryRemove(hwnd, out _);
        return TRUE;
    }

    [Win32Export("IsTouchWindow")]
    [Win32ExportOnWindows(A0, "IsTouchWindow")]
    public static int IsTouchWindow(nint hwnd, uint* flags)
    {
        bool on = s_touchWindows.TryGetValue(hwnd, out uint f);
        if (flags != null) *flags = on ? f : 0;
        return on ? TRUE : FALSE;
    }

    // Gesture configuration on the stack's windows, for the same reason: DotNetBar's ItemPanel asks
    // for gestures (SetGestureConfig) rather than raw touch, user32 refused the window, and the panel's
    // OnHandleCreated threw "Cannot register window" on any machine with a digitizer. The configuration
    // is kept per window and handed back by GetGestureConfig; the gestures themselves are not
    // synthesised (touch arrives as pointer input through the host window).
    [StructLayout(LayoutKind.Sequential)]
    public struct GESTURECONFIG { public uint dwID, dwWant, dwBlock; }

    private static readonly ConcurrentDictionary<nint, GESTURECONFIG[]> s_gestureConfigs = new();

    [Win32Export("SetGestureConfig")]
    [Win32ExportOnWindows(A0, "SetGestureConfig")]
    public static int SetGestureConfig(nint hwnd, uint reserved, uint count, GESTURECONFIG* configs, uint cbSize)
    {
        if (Resolve(hwnd) == null) { SetLastError(ERROR_INVALID_WINDOW_HANDLE); return FALSE; }
        if (reserved != 0 || (count != 0 && configs == null) || cbSize != (uint)sizeof(GESTURECONFIG))
        {
            SetLastError(ERROR_INVALID_PARAMETER);
            return FALSE;
        }
        var copy = new GESTURECONFIG[count];
        for (int i = 0; i < count; i++) copy[i] = configs[i];
        s_gestureConfigs[hwnd] = copy;
        return TRUE;
    }

    [Win32Export("GetGestureConfig")]
    [Win32ExportOnWindows(A0, "GetGestureConfig")]
    public static int GetGestureConfig(nint hwnd, uint reserved, uint flags, uint* count, GESTURECONFIG* configs, uint cbSize)
    {
        if (Resolve(hwnd) == null) { SetLastError(ERROR_INVALID_WINDOW_HANDLE); return FALSE; }
        if (count == null || cbSize != (uint)sizeof(GESTURECONFIG)) { SetLastError(ERROR_INVALID_PARAMETER); return FALSE; }
        // Asked by gesture ID, as Windows does: each requested ID gets what was set for it, or the
        // default (every gesture wanted, nothing blocked) when it was never configured.
        s_gestureConfigs.TryGetValue(hwnd, out GESTURECONFIG[] set);
        for (int i = 0; i < *count; i++)
        {
            uint id = configs[i].dwID;
            GESTURECONFIG found = new GESTURECONFIG { dwID = id, dwWant = 1, dwBlock = 0 };
            if (set != null)
                foreach (GESTURECONFIG g in set)
                    if (g.dwID == id || g.dwID == 0) { found = new GESTURECONFIG { dwID = id, dwWant = g.dwWant, dwBlock = g.dwBlock }; if (g.dwID == id) break; }
            configs[i] = found;
        }
        return TRUE;
    }

    [Win32Export("EnableWindow")]
    [Win32ExportOnWindows(A0, "EnableWindow")]
    public static int EnableWindow(nint hwnd, int enable)
    {
        object? o = Resolve(hwnd);
        bool wasDisabled = !IsEnabled(o);
        Ui(o, () =>
        {
            if (o is HwndSource s && s.RootVisual is UIElement e) e.IsEnabled = enable != 0;
            else if (o is Forms.Control c) c.Enabled = enable != 0;
            else if (o is AppWindow a) a.Style = enable != 0 ? a.Style & ~WS_DISABLED : a.Style | WS_DISABLED;
            return 0;
        });
        return wasDisabled ? TRUE : FALSE;
    }

    private static WindowState? StateOf(object? o) => Ui<WindowState?>(o, () => o switch
    {
        HwndSource s when WindowOf(s) is Window w => w.WindowState,
        Forms.Form f => f.WindowState switch { Forms.FormWindowState.Minimized => WindowState.Minimized, Forms.FormWindowState.Maximized => WindowState.Maximized, _ => WindowState.Normal },
        _ => null,
    });

    [Win32Export("IsIconic")] [Win32ExportOnWindows(A0, "IsIconic")] public static int IsIconic(nint hwnd) => StateOf(Resolve(hwnd)) == WindowState.Minimized ? TRUE : FALSE;
    [Win32Export("IsZoomed")] [Win32ExportOnWindows(A0, "IsZoomed")] public static int IsZoomed(nint hwnd) => StateOf(Resolve(hwnd)) == WindowState.Maximized ? TRUE : FALSE;

    private static bool Show(object? o, int cmd)
    {
        bool wasVisible = IsVisible(o);
        Ui(o, () =>
        {
            WindowState? state = cmd switch
            {
                2 or 6 or 7 or 11 => WindowState.Minimized,   // SW_SHOWMINIMIZED, SW_MINIMIZE, SW_SHOWMINNOACTIVE, SW_FORCEMINIMIZE
                3 => WindowState.Maximized,                    // SW_SHOWMAXIMIZED
                1 or 9 => WindowState.Normal,                  // SW_SHOWNORMAL, SW_RESTORE
                _ => null,
            };
            switch (o)
            {
                case HwndSource s when WindowOf(s) is Window w:
                    if (cmd == 0) w.Hide();
                    else
                    {
                        if (state.HasValue) w.WindowState = state.Value;
                        if (!w.IsVisible) w.Show();
                    }
                    break;
                case Forms.Form f:
                    if (cmd == 0) f.Hide();
                    else
                    {
                        if (state.HasValue)
                            f.WindowState = state.Value switch { WindowState.Minimized => Forms.FormWindowState.Minimized, WindowState.Maximized => Forms.FormWindowState.Maximized, _ => Forms.FormWindowState.Normal };
                        if (!f.Visible) f.Show();
                    }
                    break;
                case Forms.Control c:
                    c.Visible = cmd != 0;
                    break;
                case AppWindow a:
                    a.Style = cmd == 0 ? a.Style & ~WS_VISIBLE : a.Style | WS_VISIBLE;
                    break;
            }
            return 0;
        });
        return wasVisible;
    }

    [Win32Export("ShowWindow", "ShowWindowAsync")] [Win32ExportOnWindows(A0, "ShowWindow", "ShowWindowAsync")] public static int ShowWindow(nint hwnd, int cmd) => Show(Resolve(hwnd), cmd) ? TRUE : FALSE;

    // A WinForms window animates in the driver: its composited scene is rolled, slid, grown or
    // faded in or out (see XplatUIWebGpu.Animate.cs). The window's own state is what is shown or
    // hidden, not Control.Visible: popups that open this way (DotNetBar's menus) are created
    // hidden with Visible already true, and setting it again does nothing. Other windows are shown
    // or hidden straight away.
    [Win32Export("AnimateWindow")]
    [Win32ExportOnWindows(A0, "AnimateWindow")]
    public static int AnimateWindow(nint hwnd, uint time, uint flags)
    {
        const uint AW_HIDE = 0x10000, AW_ACTIVATE = 0x20000;
        object? o = Resolve(hwnd);
        if (o == null) return FALSE;
        if (o is Forms.Control c)
        {
            return Ui(o, () =>
            {
                if (!c.IsHandleCreated) return FALSE;
                object? driver = typeof(Forms.Control).Assembly.GetType("System.Windows.Forms.XplatUI")
                    ?.GetField("driver", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);
                MethodInfo? animate = driver?.GetType().GetMethod("AnimateWindow", BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { typeof(IntPtr), typeof(int), typeof(int) }, null);
                if (animate == null) return FALSE;
                return (bool)animate.Invoke(driver, new object[] { c.Handle, (int)time, (int)flags })! ? TRUE : FALSE;
            });
        }
        bool hide = (flags & AW_HIDE) != 0;
        if (IsVisible(o) != hide) return FALSE;
        Show(o, hide ? 0 : (flags & AW_ACTIVATE) != 0 ? 5 : 8);   // SW_HIDE, SW_SHOW, SW_SHOWNA
        return TRUE;
    }

    // ---- relationships ---------------------------------------------------------------------------

    private static nint OwnerOrParent(object? o) => Ui(o, () => o switch
    {
        HwndSource s when WindowOf(s) is Window w && w.Owner != null => new WindowInteropHelper(w.Owner).Handle,
        Forms.Form f when f.Owner != null => f.Owner.Handle,
        Forms.Control c when c.Parent != null => c.Parent.Handle,
        AppWindow a => a.Parent,
        _ => (nint)0,
    });

    [Win32Export("GetParent")] [Win32ExportOnWindows(A0, "GetParent")] public static nint GetParent(nint hwnd) => OwnerOrParent(Resolve(hwnd));

    [Win32Export("GetAncestor")]
    [Win32ExportOnWindows(A0, "GetAncestor")]
    public static nint GetAncestor(nint hwnd, uint flags)
    {
        object? o = Resolve(hwnd);
        if (flags == 1) return OwnerOrParent(o);                 // GA_PARENT
        nint h = hwnd;                                           // GA_ROOT, GA_ROOTOWNER: the top of the chain
        for (int guard = 0; guard < 64; guard++)
        {
            nint up = OwnerOrParent(Resolve(h));
            if (up == 0) return h;
            h = up;
        }
        return h;
    }

    [Win32Export("GetWindow")]
    [Win32ExportOnWindows(A0, "GetWindow")]
    public static nint GetWindow(nint hwnd, uint cmd)
    {
        object? o = Resolve(hwnd);
        return cmd switch
        {
            4 => OwnerOrParent(o) is nint owner && owner != 0 ? owner : TaskbarlessOwner(o),             // GW_OWNER
            5 => Ui(o, () => o is Forms.Control c && c.Controls.Count > 0 && c.Controls[0].IsHandleCreated ? c.Controls[0].Handle : 0),   // GW_CHILD
            _ => 0,
        };
    }

    /// <summary>.NET gives a form with ShowInTaskbar false and no owner a hidden owner window of
    /// its own (that is how it keeps it off the taskbar), so Windows answers GW_OWNER for it --
    /// and Process.MainWindowHandle, which takes the first visible UNOWNED window, passes it by.
    /// A splash screen answered "unowned" here and became the main window: app's view then
    /// decided it was not in the main window and showed no licence notice.</summary>
    private static nint TaskbarlessOwner(object? o)
    {
        bool owned = Ui(o, () => o is Forms.Form { TopLevel: true, Owner: null, ShowInTaskbar: false } f && f.Parent == null);
        if (!owned) return 0;
        if (s_parkingOwner == 0)
            s_parkingOwner = CreateWindowExW(0, "Static", "WinFormsParkingOwner", 0x80000000 /* WS_POPUP, hidden */, 0, 0, 0, 0, 0, 0, 0, 0);
        return s_parkingOwner;
    }

    private static nint s_parkingOwner;

    [DllImport("user32", CharSet = CharSet.Unicode)]
    private static extern nint CreateWindowExW(uint exStyle, string className, string title, uint style, int x, int y, int w, int h, nint parent, nint menu, nint instance, nint param);

    [Win32Export("SetParent")]
    [Win32ExportOnWindows(A0, "SetParent")]
    public static nint SetParent(nint child, nint parent)
    {
        object? o = Resolve(child);
        nint previous = OwnerOrParent(o);
        Ui(o, () =>
        {
            if (o is Forms.Control c && Resolve(parent) is Forms.Control p) c.Parent = p;
            else if (o is AppWindow a) a.Parent = parent;
            return 0;
        });
        return previous;
    }

    [Win32Export("IsChild")]
    [Win32ExportOnWindows(A0 | A1, "IsChild")]
    public static int IsChild(nint parent, nint child)
    {
        for (nint h = OwnerOrParent(Resolve(child)), guard = 0; h != 0 && guard < 64; h = OwnerOrParent(Resolve(h)), guard++)
        {
            if (h == parent) return TRUE;
        }
        return FALSE;
    }

    [Win32Export("GetWindowThreadProcessId")]
    [Win32ExportOnWindows(A0, "GetWindowThreadProcessId")]
    public static uint GetWindowThreadProcessId(nint hwnd, uint* pid)
    {
        if (pid != null) *pid = (uint)Environment.ProcessId;
        object? o = Resolve(hwnd);
        return o switch
        {
            DispatcherObject d => (uint)d.Dispatcher.Thread.ManagedThreadId,
            AppWindow a => (uint)a.ThreadId,
            null => 0,
            _ => (uint)Environment.CurrentManagedThreadId,
        };
    }

    // ---- window longs: the style bits are synthesised from the window's state, and set back -----

    private static readonly ConcurrentDictionary<(nint, int), nint> s_longs = new();

    private static uint StyleOf(object? o)
    {
        uint style = o is Forms.Control { Parent: not null } ? WS_CHILD | WS_CLIPSIBLINGS : WS_OVERLAPPEDWINDOW | WS_CLIPCHILDREN | WS_CLIPSIBLINGS;
        if (o is AppWindow a) return a.Style;
        if (IsVisible(o)) style |= WS_VISIBLE;
        if (!IsEnabled(o)) style |= WS_DISABLED;
        WindowState? state = StateOf(o);
        if (state == WindowState.Minimized) style |= WS_MINIMIZE;
        if (state == WindowState.Maximized) style |= WS_MAXIMIZE;
        return style;
    }

    private static uint ExStyleOf(object? o) => o is AppWindow a ? a.ExStyle : Ui(o, () => o switch
    {
        HwndSource s when WindowOf(s) is Window w => (w.Topmost ? WS_EX_TOPMOST : 0) | (w.WindowStyle == WindowStyle.ToolWindow ? WS_EX_TOOLWINDOW : WS_EX_APPWINDOW) | WS_EX_WINDOWEDGE,
        Forms.Form f => (f.TopMost ? WS_EX_TOPMOST : 0) | WS_EX_WINDOWEDGE | WS_EX_APPWINDOW,
        _ => 0u,
    });

    [Win32Export("GetWindowLongPtrW", "GetWindowLongPtr", "GetWindowLongPtrA", "GetWindowLongW", "GetWindowLong", "GetWindowLongA")]
    [Win32ExportOnWindows(A0, "GetWindowLongPtrW", "GetWindowLongPtrA", "GetWindowLongW", "GetWindowLongA")]
    public static nint GetWindowLongPtr(nint hwnd, int index)
    {
        object? o = Resolve(hwnd);
        if (o == null) { SetLastError(ERROR_INVALID_WINDOW_HANDLE); return 0; }
        SetLastError(0);
        return index switch
        {
            GWL_STYLE => (nint)(int)StyleOf(o),
            GWL_EXSTYLE => (nint)(int)ExStyleOf(o),
            GWL_HWNDPARENT => OwnerOrParent(o),
            GWL_HINSTANCE => ExeModule,
            GWL_WNDPROC when o is AppWindow a => a.WndProc,
            _ => s_longs.TryGetValue((hwnd, index), out nint v) ? v : 0,
        };
    }

    [Win32Export("SetWindowLongPtrW", "SetWindowLongPtr", "SetWindowLongPtrA", "SetWindowLongW", "SetWindowLong", "SetWindowLongA")]
    [Win32ExportOnWindows(A0, "SetWindowLongPtrW", "SetWindowLongPtrA", "SetWindowLongW", "SetWindowLongA")]
    public static nint SetWindowLongPtr(nint hwnd, int index, nint value)
    {
        object? o = Resolve(hwnd);
        if (o == null) { SetLastError(ERROR_INVALID_WINDOW_HANDLE); return 0; }
        SetLastError(0);
        switch (index)
        {
            case GWL_STYLE:
            {
                uint old = StyleOf(o), now = (uint)value;
                if (o is AppWindow a) { a.Style = now; return (nint)(int)old; }
                if (((old ^ now) & WS_VISIBLE) != 0) Show(o, (now & WS_VISIBLE) != 0 ? 5 : 0);
                if (((old ^ now) & WS_DISABLED) != 0)
                {
                    Ui(o, () => { if (o is HwndSource s && s.RootVisual is UIElement e) e.IsEnabled = (now & WS_DISABLED) == 0; else if (o is Forms.Control c) c.Enabled = (now & WS_DISABLED) == 0; return 0; });
                }
                // Caption buttons, frame and the rest are the host's to draw; the bits are remembered.
                s_longs[(hwnd, index)] = value;
                return (nint)(int)old;
            }
            case GWL_EXSTYLE:
            {
                uint old = ExStyleOf(o), now = (uint)value;
                if (o is AppWindow a) { a.ExStyle = now; return (nint)(int)old; }
                if (((old ^ now) & WS_EX_TOPMOST) != 0) SetTopmost(o, (now & WS_EX_TOPMOST) != 0);
                s_longs[(hwnd, index)] = value;
                return (nint)(int)old;
            }
            case GWL_HWNDPARENT:
            {
                nint old = OwnerOrParent(o);
                Ui(o, () =>
                {
                    if (o is HwndSource s && WindowOf(s) is Window w && Resolve(value) is HwndSource os && WindowOf(os) is Window owner) w.Owner = owner;
                    else if (o is AppWindow a) a.Parent = value;
                    return 0;
                });
                return old;
            }
            case GWL_WNDPROC:
            {
                if (o is AppWindow a) { nint old = a.WndProc; a.WndProc = value; return old; }
                // Subclassing a WPF or WinForms window by replacing its procedure: the stack's windows do
                // not run through a Win32 procedure here, so nothing would call the new one.
                LogOnce("subclass", "win32: SetWindowLongPtr(GWLP_WNDPROC) on a stack window is not supported; the window is not subclassed");
                return 0;
            }
            default:
            {
                s_longs.TryGetValue((hwnd, index), out nint old);
                s_longs[(hwnd, index)] = value;
                return old;
            }
        }
    }

    [Win32Export("GetClassLongPtrW", "GetClassLongPtr", "GetClassLongW", "GetClassLong")] [Win32ExportOnWindows(A0, "GetClassLongPtrW", "GetClassLongW")] public static nint GetClassLongPtr(nint hwnd, int index) => 0;
    [Win32Export("SetClassLongPtrW", "SetClassLongPtr", "SetClassLongPtrA", "SetClassLongW", "SetClassLong", "SetClassLongA")] [Win32ExportOnWindows(A0, "SetClassLongPtrW", "SetClassLongW")] public static nint SetClassLongPtr(nint hwnd, int index, nint value) => 0;

    // ---- names and text --------------------------------------------------------------------------

    private static string? ClassNameOf(object? o) => o switch
    {
        HwndSource => "HwndWrapper[" + System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name + ";;]",
        Forms.Control => "WindowsForms10.Window.8.app.0.141b42a_r6_ad1",
        AppWindow a => a.ClassName,
        _ => null,
    };

    [Win32Export("GetClassNameW")]
    [Win32ExportOnWindows(A0, "GetClassNameW")]
    public static int GetClassNameW(nint hwnd, char* buffer, int max)
    {
        string? name = ClassNameOf(Resolve(hwnd));
        if (name == null) { SetLastError(ERROR_INVALID_WINDOW_HANDLE); return 0; }
        return WriteUtf16(buffer, max, name);
    }

    [Win32Export("GetClassName", "GetClassNameA")]
    public static int GetClassNameBare(nint hwnd, byte* buffer, int max)
    {
        string? name = ClassNameOf(Resolve(hwnd));
        if (name == null) { SetLastError(ERROR_INVALID_WINDOW_HANDLE); return 0; }
        return WriteUtf8(buffer, max, name);
    }

    private static string TitleOf(object? o) => Ui(o, () => o switch
    {
        HwndSource s when WindowOf(s) is Window w => w.Title ?? "",
        Forms.Control c => c.Text ?? "",
        AppWindow a => a.Title,
        _ => "",
    });

    [Win32Export("GetWindowTextW")] [Win32ExportOnWindows(A0, "GetWindowTextW")] public static int GetWindowTextW(nint hwnd, char* buffer, int max) => WriteUtf16(buffer, max, TitleOf(Resolve(hwnd)));
    [Win32Export("GetWindowText", "GetWindowTextA")] public static int GetWindowTextBare(nint hwnd, byte* buffer, int max) => WriteUtf8(buffer, max, TitleOf(Resolve(hwnd)));
    [Win32Export("GetWindowTextLengthW", "GetWindowTextLength", "GetWindowTextLengthA")] [Win32ExportOnWindows(A0, "GetWindowTextLengthW")] public static int GetWindowTextLength(nint hwnd) => TitleOf(Resolve(hwnd)).Length;

    private static int SetTitle(nint hwnd, string? text)
    {
        object? o = Resolve(hwnd);
        if (o == null) { SetLastError(ERROR_INVALID_WINDOW_HANDLE); return FALSE; }
        Ui(o, () =>
        {
            if (o is HwndSource s && WindowOf(s) is Window w) w.Title = text ?? "";
            else if (o is Forms.Control c) c.Text = text ?? "";
            else if (o is AppWindow a) a.Title = text ?? "";
            return 0;
        });
        return TRUE;
    }

    [Win32Export("SetWindowTextW")] [Win32ExportOnWindows(A0, "SetWindowTextW")] public static int SetWindowTextW(nint hwnd, char* text) => SetTitle(hwnd, ReadUtf16(text));
    [Win32Export("SetWindowText", "SetWindowTextA")] public static int SetWindowTextBare(nint hwnd, byte* text) => SetTitle(hwnd, ReadEither(text));

    private static nint Find(IEnumerable<object> candidates, string? className, string? title, nint after)
    {
        bool passed = after == 0;
        foreach (object o in candidates)
        {
            nint h = HandleOf(o);
            if (!passed) { passed = h == after; continue; }
            if (title != null && TitleOf(o) != title) continue;
            if (className != null && !string.Equals(ClassNameOf(o), className, StringComparison.OrdinalIgnoreCase)) continue;
            return h;
        }
        return 0;
    }

    private static IEnumerable<object> ChildrenOf(nint parent)
    {
        if (parent == 0 || parent == DesktopWindow) return TopLevels();
        object? o = Resolve(parent);
        var list = new List<object>();
        Ui(o, () =>
        {
            if (o is Forms.Control c) foreach (Forms.Control child in c.Controls) list.Add(child);
            return 0;
        });
        list.AddRange(s_appWindows.Values.Where(a => a.Parent == parent));
        return list;
    }

    [Win32Export("FindWindowW")] public static nint FindWindowW(char* cls, char* title) => Find(TopLevels(), ClassArg(cls), ReadUtf16(title), 0);
    [Win32Export("FindWindow", "FindWindowA")] public static nint FindWindowBare(byte* cls, byte* title) => Find(TopLevels(), (nuint)cls < 0x10000 ? null : ReadEither(cls), ReadEither(title), 0);
    [Win32Export("FindWindowExW")] public static nint FindWindowExW(nint parent, nint after, char* cls, char* title) => Find(ChildrenOf(parent), ClassArg(cls), ReadUtf16(title), after);
    [Win32Export("FindWindowEx", "FindWindowExA")] public static nint FindWindowExBare(nint parent, nint after, byte* cls, byte* title) => Find(ChildrenOf(parent), (nuint)cls < 0x10000 ? null : ReadEither(cls), ReadEither(title), after);

    /// <summary>A class name argument: a string, or an atom from RegisterClass in the low word.</summary>
    private static string? ClassArg(char* cls)
    {
        if (cls == null) return null;
        if ((nuint)cls < 0x10000) return s_classes.Values.FirstOrDefault(c => c.Atom == (ushort)(nuint)cls)?.Name;
        return ReadUtf16(cls);
    }

    [Win32Export("EnumWindows")]
    public static int EnumWindows(delegate* unmanaged<nint, nint, int> callback, nint data)
    {
        foreach (object o in TopLevels())
        {
            nint h = HandleOf(o);
            if (h != 0 && callback(h, data) == FALSE) break;
        }
        return TRUE;
    }

    [Win32Export("EnumThreadWindows")]
    public static int EnumThreadWindows(uint thread, delegate* unmanaged<nint, nint, int> callback, nint data)
    {
        bool any = false;
        foreach (object o in TopLevels())
        {
            uint t = o switch { DispatcherObject d => (uint)d.Dispatcher.Thread.ManagedThreadId, AppWindow a => (uint)a.ThreadId, _ => (uint)Environment.CurrentManagedThreadId };
            if (t != thread) continue;
            nint h = HandleOf(o);
            if (h == 0) continue;
            any = true;
            if (callback(h, data) == FALSE) break;
        }
        return any ? TRUE : FALSE;
    }

    [Win32Export("EnumChildWindows")]
    [Win32ExportOnWindows(A0, "EnumChildWindows")]
    public static int EnumChildWindows(nint parent, delegate* unmanaged<nint, nint, int> callback, nint data)
    {
        foreach (object o in ChildrenOf(parent))
        {
            nint h = HandleOf(o);
            if (h != 0 && callback(h, data) == FALSE) break;
        }
        return TRUE;
    }

    [Win32Export("DestroyWindow")]
    [Win32ExportOnWindows(A0, "DestroyWindow")]
    public static int DestroyWindow(nint hwnd)
    {
        object? o = Resolve(hwnd);
        if (o == null) { SetLastError(ERROR_INVALID_WINDOW_HANDLE); return FALSE; }
        if (o is AppWindow a) { a.Destroy(); return TRUE; }
        Ui(o, () =>
        {
            if (o is HwndSource s && WindowOf(s) is Window w) w.Close();
            else if (o is Forms.Form f) f.Close();
            else if (o is Forms.Control c) c.Dispose();
            return 0;
        });
        return TRUE;
    }

    // WINDOWPLACEMENT: length, flags, showCmd, POINT ptMinPosition, POINT ptMaxPosition, RECT rcNormalPosition.
    [Win32Export("GetWindowPlacement")]
    [Win32ExportOnWindows(A0, "GetWindowPlacement")]
    public static int GetWindowPlacement(nint hwnd, uint* wp)
    {
        object? o = Resolve(hwnd);
        if (o == null) { SetLastError(ERROR_INVALID_WINDOW_HANDLE); return FALSE; }
        WindowState? state = StateOf(o);
        RECT normal = Ui(o, () => o switch
        {
            HwndSource s when WindowOf(s) is Window w && !w.RestoreBounds.IsEmpty => RectOf(w.RestoreBounds.X, w.RestoreBounds.Y, w.RestoreBounds.Width, w.RestoreBounds.Height),
            Forms.Form f => RectOf(f.RestoreBounds.X, f.RestoreBounds.Y, f.RestoreBounds.Width, f.RestoreBounds.Height),
            _ => WindowRect(hwnd, out _),
        });
        wp[1] = 0;
        wp[2] = !IsVisible(o) ? 0u : state == WindowState.Minimized ? 2u : state == WindowState.Maximized ? 3u : 1u;
        ((int*)wp)[3] = -1; ((int*)wp)[4] = -1; ((int*)wp)[5] = -1; ((int*)wp)[6] = -1;
        *(RECT*)(wp + 7) = normal;
        return TRUE;
    }

    [Win32Export("SetWindowPlacement")]
    [Win32ExportOnWindows(A0, "SetWindowPlacement")]
    public static int SetWindowPlacement(nint hwnd, uint* wp)
    {
        object? o = Resolve(hwnd);
        if (o == null) { SetLastError(ERROR_INVALID_WINDOW_HANDLE); return FALSE; }
        RECT r = *(RECT*)(wp + 7);
        Place(o, r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
        Show(o, (int)wp[2]);
        return TRUE;
    }

    private static void Invalidate(object? o) => Ui(o, () =>
    {
        if (o is HwndSource s && s.RootVisual is UIElement e) e.InvalidateVisual();
        else if (o is Forms.Control c) c.Invalidate(true);
        return 0;
    });

    [Win32Export("InvalidateRect")] [Win32ExportOnWindows(A0, "InvalidateRect")] public static int InvalidateRect(nint hwnd, RECT* r, int erase) { Invalidate(Resolve(hwnd)); return TRUE; }
    [Win32Export("ValidateRect")] [Win32ExportOnWindows(A0, "ValidateRect")] public static int ValidateRect(nint hwnd, RECT* r) { ValidateWinFormsWindow(hwnd, repaint: false); return TRUE; }
    [Win32Export("UpdateWindow")] [Win32ExportOnWindows(A0, "UpdateWindow")] public static int UpdateWindow(nint hwnd) { object? o = Resolve(hwnd); Ui(o, () => { if (o is Forms.Control c) c.Update(); return 0; }); return TRUE; }
    [Win32Export("RedrawWindow")] [Win32ExportOnWindows(A0, "RedrawWindow")] public static int RedrawWindow(nint hwnd, RECT* r, nint rgn, uint flags) { Invalidate(Resolve(hwnd)); return TRUE; }
    [Win32Export("LockWindowUpdate")] [Win32ExportOnWindows(A0, "LockWindowUpdate")] public static int LockWindowUpdate(nint hwnd) => TRUE;
    [Win32Export("SetWindowRgn")] [Win32ExportOnWindows(A0, "SetWindowRgn")] public static int SetWindowRgn(nint hwnd, nint rgn, int redraw) => TRUE;
    [Win32Export("SetLayeredWindowAttributes")] [Win32ExportOnWindows(A0, "SetLayeredWindowAttributes")] public static int SetLayeredWindowAttributes(nint hwnd, uint key, byte alpha, uint flags) => TRUE;
    [Win32Export("FlashWindow")] public static int FlashWindow(nint hwnd, uint invert) => FALSE;
    [Win32Export("FlashWindowEx")] public static int FlashWindowEx(void* info) => FALSE;

    // ---- messages ------------------------------------------------------------------------------

    private const uint WM_DESTROY = 0x2, WM_CLOSE = 0x10, WM_SETTEXT = 0xC, WM_GETTEXT = 0xD, WM_GETTEXTLENGTH = 0xE,
        WM_SYSCOMMAND = 0x112, WM_TIMER = 0x113, WM_NULL = 0, WM_SETREDRAW = 0xB;

    private static nint Send(nint hwnd, uint msg, nint w, nint l)
    {
        object? o = Resolve(hwnd);
        switch (o)
        {
            case AppWindow a:
                return a.Dispatch(msg, w, l);
            case Forms.Control c:
                return Ui(c, () => FormsSend(c, msg, w, l));
            case HwndSource s:
                return Ui(s, () => WpfSend(s, msg, w, l));
            default:
                if (hwnd == (nint)0xFFFF) return 0;   // HWND_BROADCAST: nobody else to tell
                SetLastError(ERROR_INVALID_WINDOW_HANDLE);
                return 0;
        }
    }

    // WinForms windows have a real Win32-shaped procedure (Control.WndProc), which Mono's message
    // pipeline calls with a Message; the message goes in through the same entry point.
    private static System.Reflection.MethodInfo? s_formsSendMessage;

    private static nint FormsSend(Forms.Control c, uint msg, nint w, nint l)
    {
        s_formsSendMessage ??= typeof(Forms.Control).Assembly.GetType("System.Windows.Forms.XplatUI")
            ?.GetMethod("SendMessage", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public,
                        new[] { typeof(IntPtr), typeof(Forms.Control).Assembly.GetType("System.Windows.Forms.Msg")!, typeof(IntPtr), typeof(IntPtr) });
        if (s_formsSendMessage == null) return 0;
        object msgValue = Enum.ToObject(s_formsSendMessage.GetParameters()[1].ParameterType, (int)msg);
        return (nint)(IntPtr)s_formsSendMessage.Invoke(null, new object[] { c.Handle, msgValue, (IntPtr)w, (IntPtr)l })!;
    }

    // A WPF window does not run through a Win32 procedure here; the messages that mean something to a
    // window as such are applied to it directly.
    private static nint WpfSend(HwndSource s, uint msg, nint w, nint l)
    {
        Window? win = WindowOf(s);
        switch (msg)
        {
            case WM_CLOSE:
                win?.Close();
                return 0;
            case WM_SYSCOMMAND:
                switch ((uint)w & 0xFFF0)
                {
                    case 0xF060: win?.Close(); break;                                         // SC_CLOSE
                    case 0xF020: if (win != null) win.WindowState = WindowState.Minimized; break;  // SC_MINIMIZE
                    case 0xF030: if (win != null) win.WindowState = WindowState.Maximized; break;  // SC_MAXIMIZE
                    case 0xF120: if (win != null) win.WindowState = WindowState.Normal; break;     // SC_RESTORE
                }
                return 0;
            case WM_GETTEXTLENGTH:
                return (win?.Title ?? "").Length;
            case WM_GETTEXT:
                return WriteUtf16((char*)l, (int)w, win?.Title ?? "");
            case WM_SETTEXT:
                if (win != null) win.Title = ReadUtf16((char*)l) ?? "";
                return TRUE;
            default:
                return 0;
        }
    }

    private static bool Post(nint hwnd, uint msg, nint w, nint l)
    {
        object? o = Resolve(hwnd);
        switch (o)
        {
            case AppWindow a:
                a.Post(msg, w, l);
                return true;
            case DispatcherObject d:
                d.Dispatcher.BeginInvoke(() => Send(hwnd, msg, w, l));
                return true;
            case Forms.Control c:
                c.BeginInvoke(() => Send(hwnd, msg, w, l));
                return true;
            default:
                if (hwnd == 0 || hwnd == (nint)0xFFFF) return true;
                SetLastError(ERROR_INVALID_WINDOW_HANDLE);
                return false;
        }
    }

    [Win32Export("SendMessageW", "SendMessage", "SendMessageA")] [Win32ExportOnWindows(A0, "SendMessageW", "SendMessageA")] public static nint SendMessageExport(nint hwnd, uint msg, nint w, nint l) => Send(hwnd, msg, w, l);

    [Win32Export("SendMessageTimeoutW", "SendMessageTimeout", "SendMessageTimeoutA")]
    [Win32ExportOnWindows(A0, "SendMessageTimeoutW", "SendMessageTimeoutA")]
    public static nint SendMessageTimeout(nint hwnd, uint msg, nint w, nint l, uint flags, uint timeout, nint* result)
    {
        nint r = Send(hwnd, msg, w, l);
        if (result != null) *result = r;
        return TRUE;
    }

    [Win32Export("PostMessageW", "PostMessage", "PostMessageA", "SendNotifyMessageW", "SendNotifyMessage")] [Win32ExportOnWindows(A0, "PostMessageW", "PostMessageA", "SendNotifyMessageW", "SendNotifyMessageA")] public static int PostMessageExport(nint hwnd, uint msg, nint w, nint l) => Post(hwnd, msg, w, l) ? TRUE : FALSE;
    [Win32Export("PostThreadMessageW", "PostThreadMessage", "PostThreadMessageA")] public static int PostThreadMessage(uint thread, uint msg, nint w, nint l) => AppWindow.PostToThread((int)thread, msg, w, l) ? TRUE : FALSE;
    [Win32Export("DefWindowProcW", "DefWindowProc", "DefWindowProcA")] [Win32ExportOnWindows(A0, "DefWindowProcW", "DefWindowProcA")] public static nint DefWindowProc(nint hwnd, uint msg, nint w, nint l) => AppWindow.DefaultProc(hwnd, msg, w, l);

    [Win32Export("CallWindowProcW", "CallWindowProc", "CallWindowProcA")]
    public static nint CallWindowProc(nint proc, nint hwnd, uint msg, nint w, nint l)
    {
        if (proc == 0) return 0;
        return ((delegate* unmanaged<nint, uint, nint, nint, nint>)proc)(hwnd, msg, w, l);
    }

    // ---- windows the app creates itself -----------------------------------------------------------

    internal sealed record WindowClass(string Name, ushort Atom, nint WndProc);

    private static readonly ConcurrentDictionary<string, WindowClass> s_classes = new(StringComparer.OrdinalIgnoreCase);
    private static int s_nextAtom = 0xC100;
    private static readonly ConcurrentDictionary<nint, AppWindow> s_appWindows = new();

    // WNDCLASSEXW: cbSize, style, lpfnWndProc, cbClsExtra, cbWndExtra, hInstance, hIcon, hCursor,
    // hbrBackground, lpszMenuName, lpszClassName, hIconSm. WNDCLASSW is the same without cbSize/hIconSm.
    private static ushort RegisterClassCore(nint wndProc, char* className, bool either = false)
    {
        string? name = either && (nuint)className >= 0x10000 ? ReadEither((byte*)className) : ClassArg(className);
        if (name == null) { SetLastError(ERROR_INVALID_PARAMETER); return 0; }
        if (s_classes.ContainsKey(name)) { SetLastError(1410); return 0; }   // ERROR_CLASS_ALREADY_EXISTS
        var cls = new WindowClass(name, (ushort)Interlocked.Increment(ref s_nextAtom), wndProc);
        s_classes[name] = cls;
        return cls.Atom;
    }

    // Windows checks cbSize first and fails with ERROR_INVALID_PARAMETER when it is not
    // sizeof(WNDCLASSEXW). That is not pedantry: a caller whose struct is laid out wrongly gets that
    // failure on Windows and copes with it, and reading its fields anyway would chase garbage pointers.
    [Win32Export("RegisterClassExW")]
    public static ushort RegisterClassExW(byte* wc)
    {
        if (*(uint*)wc != 80) { SetLastError(ERROR_INVALID_PARAMETER); return 0; }
        return RegisterClassCore(*(nint*)(wc + 8), *(char**)(wc + 64));
    }

    [Win32Export("RegisterClassW")] public static ushort RegisterClassW(byte* wc) => RegisterClassCore(*(nint*)(wc + 8), *(char**)(wc + 64));
    // The class name of the undecorated imports is UTF-8 or UTF-16 by the import's CharSet; see ReadEither.
    // (WNDCLASSW and WNDCLASSEXW put lpszClassName at the same offset on 64-bit.)
    [Win32Export("RegisterClass", "RegisterClassA")] public static ushort RegisterClassBare(byte* wc) => RegisterClassCore(*(nint*)(wc + 8), *(char**)(wc + 64), either: true);

    [Win32Export("RegisterClassEx", "RegisterClassExA")]
    public static ushort RegisterClassExBare(byte* wc)
    {
        if (*(uint*)wc != 80) { SetLastError(ERROR_INVALID_PARAMETER); return 0; }
        return RegisterClassCore(*(nint*)(wc + 8), *(char**)(wc + 64), either: true);
    }

    [Win32Export("UnregisterClassW", "UnregisterClass", "UnregisterClassA")]
    public static int UnregisterClass(char* className, nint instance)
    {
        string? name = ClassArg(className);
        return name != null && s_classes.TryRemove(name, out _) ? TRUE : FALSE;
    }

    private static nint CreateWindowCore(uint exStyle, string? className, string? title, uint style, int x, int y, int w, int h, nint parent, void* param)
    {
        if (className == null || !s_classes.TryGetValue(className, out var cls))
        {
            // A system class (BUTTON, EDIT, STATIC, a common control): there is no Win32 control
            // library behind it here.
            LogOnce("class:" + className, $"win32: CreateWindowEx of class '{className}' is not supported");
            SetLastError(1407);   // ERROR_CANNOT_FIND_WND_CLASS
            return 0;
        }
        var window = new AppWindow(cls, exStyle, style, title ?? "", parent == (nint)(-3) ? 0 : parent, RectOf(x == unchecked((int)0x80000000) ? 0 : x, y == unchecked((int)0x80000000) ? 0 : y, w == unchecked((int)0x80000000) ? 0 : w, h == unchecked((int)0x80000000) ? 0 : h));
        s_appWindows[window.Handle] = window;
        if (!window.Create(param))
        {
            s_appWindows.TryRemove(window.Handle, out _);
            return 0;
        }
        return window.Handle;
    }

    internal static void Forget(AppWindow w) => s_appWindows.TryRemove(w.Handle, out _);

    internal static nint DestroyAppWindow(nint hwnd)
    {
        if (s_appWindows.TryGetValue(hwnd, out var w)) w.Destroy();
        return 0;
    }

    [Win32Export("CreateWindowExW")]
    public static nint CreateWindowExW(uint ex, char* cls, char* title, uint style, int x, int y, int w, int h, nint parent, nint menu, nint instance, void* param) =>
        CreateWindowCore(ex, ClassArg(cls), ReadUtf16(title), style, x, y, w, h, parent, param);

    [Win32Export("CreateWindowEx", "CreateWindowExA")]
    public static nint CreateWindowExBare(uint ex, byte* cls, byte* title, uint style, int x, int y, int w, int h, nint parent, nint menu, nint instance, void* param) =>
        CreateWindowCore(ex, (nuint)cls < 0x10000 ? s_classes.Values.FirstOrDefault(c => c.Atom == (ushort)(nuint)cls)?.Name : ReadEither(cls), ReadEither(title), style, x, y, w, h, parent, param);

    // Timers: on an app window or with a TIMERPROC they are real. On a stack window the WM_TIMER has no
    // procedure to go to, so only the TIMERPROC form fires there.
    private static readonly ConcurrentDictionary<(nint, nuint), Timer> s_timers = new();
    private static long s_nextTimerId = 0x7000;

    [Win32Export("SetTimer")]
    [Win32ExportOnWindows(A0, "SetTimer")]
    public static nuint SetTimer(nint hwnd, nuint id, uint ms, void* proc)
    {
        if (hwnd == 0) id = (nuint)Interlocked.Increment(ref s_nextTimerId);
        var key = (hwnd, id);
        if (s_timers.TryRemove(key, out var old)) old.Dispose();
        object? target = Resolve(hwnd);
        SynchronizationContext? context = target is AppWindow aw ? aw.Context : SynchronizationContext.Current;
        nint procPtr = (nint)proc;
        void Fire()
        {
            uint tick = (uint)Environment.TickCount64;
            if (procPtr != 0) ((delegate* unmanaged<nint, uint, nuint, uint, void>)procPtr)(hwnd, WM_TIMER, id, tick);
            else if (target is AppWindow a) a.Dispatch(WM_TIMER, (nint)id, 0);
        }
        var timer = new Timer(_ =>
        {
            if (context != null) context.Post(_ => Fire(), null); else Fire();
        }, null, Math.Max(ms, 10), Math.Max(ms, 10));
        s_timers[key] = timer;
        return id;
    }

    [Win32Export("KillTimer")]
    [Win32ExportOnWindows(A0, "KillTimer")]
    public static int KillTimer(nint hwnd, nuint id)
    {
        if (s_timers.TryRemove((hwnd, id), out var t)) { t.Dispose(); return TRUE; }
        return FALSE;
    }

    private static readonly ConcurrentDictionary<(nint, string), nint> s_props = new();

    private static string PropKey(char* name) => (nuint)name < 0x10000 ? "#" + (nuint)name : ReadUtf16(name) ?? "";
    [Win32Export("SetPropW", "SetProp")] [Win32ExportOnWindows(A0, "SetPropW")] public static int SetPropW(nint hwnd, char* name, nint value) { s_props[(hwnd, PropKey(name))] = value; return TRUE; }
    [Win32Export("GetPropW", "GetProp")] [Win32ExportOnWindows(A0, "GetPropW")] public static nint GetPropW(nint hwnd, char* name) => s_props.TryGetValue((hwnd, PropKey(name)), out nint v) ? v : 0;
    [Win32Export("RemovePropW", "RemoveProp")] [Win32ExportOnWindows(A0, "RemovePropW")] public static nint RemovePropW(nint hwnd, char* name) => s_props.TryRemove((hwnd, PropKey(name)), out nint v) ? v : 0;

    // The ANSI ones: the same properties (Windows keeps one atom per name, whichever way it was given),
    // the name in the ANSI code page, which .NET off Windows marshals as UTF-8.
    private static string PropKeyA(byte* name) => (nuint)name < 0x10000 ? "#" + (nuint)name : Marshal.PtrToStringUTF8((IntPtr)name) ?? "";
    [Win32Export("SetPropA")] public static int SetPropA(nint hwnd, byte* name, nint value) { s_props[(hwnd, PropKeyA(name))] = value; return TRUE; }
    [Win32Export("GetPropA")] public static nint GetPropA(nint hwnd, byte* name) => s_props.TryGetValue((hwnd, PropKeyA(name)), out nint v) ? v : 0;
    [Win32Export("RemovePropA")] public static nint RemovePropA(nint hwnd, byte* name) => s_props.TryRemove((hwnd, PropKeyA(name)), out nint v) ? v : 0;
}

/// <summary>
/// A window the app created with CreateWindowEx from a class it registered: usually a hidden
/// message-only window. Its procedure is the app's; messages reach it on the creating thread.
/// </summary>
internal sealed unsafe class AppWindow
{
    private static long s_next = 0x30000;
    private static readonly ConcurrentDictionary<int, SynchronizationContext?> s_threadContexts = new();

    public nint Handle { get; } = (nint)Interlocked.Add(ref s_next, 4);
    public Exports.WindowClass Class { get; }
    public string ClassName => Class.Name;
    public nint WndProc { get; set; }
    public uint Style { get; set; }
    public uint ExStyle { get; set; }
    public string Title { get; set; }
    public nint Parent { get; set; }
    public RECT Rect { get; set; }
    public int ThreadId { get; } = Environment.CurrentManagedThreadId;
    public SynchronizationContext? Context { get; } = SynchronizationContext.Current;

    public AppWindow(Exports.WindowClass cls, uint exStyle, uint style, string title, nint parent, RECT rect)
    {
        Class = cls;
        WndProc = cls.WndProc;
        ExStyle = exStyle;
        Style = style;
        Title = title;
        Parent = parent;
        Rect = rect;
        s_threadContexts[ThreadId] = Context;
    }

    public nint Dispatch(uint msg, nint w, nint l)
    {
        if (WndProc == 0) return DefaultProc(Handle, msg, w, l);
        return ((delegate* unmanaged<nint, uint, nint, nint, nint>)WndProc)(Handle, msg, w, l);
    }

    public void Post(uint msg, nint w, nint l)
    {
        if (Context != null) Context.Post(_ => Dispatch(msg, w, l), null);
        else ThreadPool.QueueUserWorkItem(_ => Dispatch(msg, w, l));
    }

    public static bool PostToThread(int thread, uint msg, nint w, nint l) =>
        s_threadContexts.TryGetValue(thread, out var ctx) && ctx != null;   // no thread-message procedure to deliver to

    /// <summary>WM_NCCREATE then WM_CREATE, with a CREATESTRUCT as Windows passes it.</summary>
    public bool Create(void* param)
    {
        // CREATESTRUCTW: lpCreateParams, hInstance, hMenu, hwndParent, cy, cx, y, x, style, lpszName, lpszClass, dwExStyle.
        byte* cs = stackalloc byte[80];
        new Span<byte>(cs, 80).Clear();
        *(void**)cs = param;
        *(nint*)(cs + 8) = Exports.ExeModule;
        *(nint*)(cs + 24) = Parent;
        *(int*)(cs + 32) = Rect.Bottom - Rect.Top;
        *(int*)(cs + 36) = Rect.Right - Rect.Left;
        *(int*)(cs + 40) = Rect.Top;
        *(int*)(cs + 44) = Rect.Left;
        *(int*)(cs + 48) = (int)Style;
        *(uint*)(cs + 72) = ExStyle;
        if (Dispatch(0x81, 0, (nint)cs) == 0) return false;        // WM_NCCREATE
        return Dispatch(0x1, 0, (nint)cs) != -1;                    // WM_CREATE
    }

    public void Destroy()
    {
        Dispatch(0x2, 0, 0);       // WM_DESTROY
        Dispatch(0x82, 0, 0);      // WM_NCDESTROY
        Exports.Forget(this);
    }

    /// <summary>DefWindowProc: TRUE for WM_NCCREATE, destroy on WM_CLOSE, 0 otherwise.</summary>
    public static nint DefaultProc(nint hwnd, uint msg, nint w, nint l) => msg switch
    {
        0x81 => 1,
        0x10 => Exports.DestroyAppWindow(hwnd),
        _ => 0,
    };
}
