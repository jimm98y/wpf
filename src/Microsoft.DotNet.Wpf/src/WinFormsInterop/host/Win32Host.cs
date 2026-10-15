// Win32 windowing shell for the WinForms-on-WebGPU host — the Windows sibling of CocoaHost. It
// creates a top-level HWND (+ message pump), a wgpu surface over that HWND (via the cross-platform
// NativePlatform.CreateWindowSurface -> WGPUSurfaceSourceWindowsHWND), and drives the SAME
// WgpuPresenter scene path. Everything below the surface (driver, scene recorder, WebGPU present) is
// identical to the mac host. NOTE: authored on macOS mirroring CocoaHost + the fork's tested
// Win32Window; the Win32 P/Invoke paths compile everywhere but have not been run on Windows yet.
//
// Win32 input is simpler than Cocoa's: WM_CHAR already yields backspace (0x08)/enter (0x0D), and
// WM_KEYDOWN's VK codes equal WinForms' Keys values, so nav keys pass straight through.

using System;
using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

internal sealed unsafe partial class Win32Host : IWinFormsHost, WinFormsWebGpu.Accessibility.IA11yHostSite
{
    private readonly Form _form;

    // The form's driver window, taken once. Control.Handle CREATES the handle if it has gone, and
    // on a disposed control that throws ObjectDisposedException -- which is exactly the state a
    // dialog is in between its OK button disposing it and Windows delivering the paint that
    // follows. Asking the live control every frame therefore took the process down every time a
    // dialog was closed with a button rather than the window close box.
    private IntPtr _formHandle;
    private MethodInfo _hostActivate;

    /// <summary>Whether there is still a form behind this window. A disposed one has no pixels to
    /// present and must not be touched.</summary>
    private bool FormGone => _form == null || _form.IsDisposed;
    private readonly object _driver;
    private readonly MethodInfo _injectClick, _down, _up, _move, _char, _keyDown, _getPresent, _getScene, _getVersion, _getCaret, _getSubtree, _keyUp, _setModifiers, _wheel, _tickTimers, _sysKeyDown, _sysChar, _rightDown, _rightUp;
    private readonly MethodInfo _getRubberBands;
    private readonly MethodInfo _isPopup;
    private readonly MethodInfo _isUnderPopup;
    private readonly MethodInfo _hasLayeredImage, _getLayeredBits;
    private readonly MethodInfo _getCursor;
    // On unless switched off; see XplatUIWebGpu.s_gpuRaster for why it cannot be opt-in.
    private readonly bool _gpuRaster = Environment.GetEnvironmentVariable("WF_GPU_RASTER") != "0"
        && Environment.GetEnvironmentVariable("WF_WEBGPU") != "0";
    private readonly Stopwatch _blink = Stopwatch.StartNew();
    private IntPtr _hwnd, _hinstance;
    private WgpuPresenter _wgpu;
    private float _scale = 1f;
    private bool _quit, _savedGpu;
    private int _lastVer = -1;
    private int _ox, _oy;               // this form's origin in the driver's screen space
    private bool _lastCaretOn, _lastPresentOk;
    // Set when a key-down was taken by keyboard navigation, so the character Windows
    // translates that key into is not also delivered.
    private bool _swallowChar;
    private const int FrameTimerId = 1;
    // WF_TRACE_CURSOR=1 prints what shape the pointer is being asked for, which is the only way
    // to tell "the control never asked" from "the shape never arrived".
    private static readonly bool s_traceCursor = Environment.GetEnvironmentVariable("WF_TRACE_CURSOR") == "1";

    internal Win32Host(Form form)
    {
        _form = form;
        var xplat = typeof(Control).Assembly.GetType("System.Windows.Forms.XplatUI");
        _driver = xplat.GetField("driver", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
        Type dt = _driver.GetType();
        MethodInfo M(string n) => dt.GetMethod(n, BindingFlags.NonPublic | BindingFlags.Instance);
        _injectClick = M("InjectClick"); _down = M("InjectMouseDown"); _up = M("InjectMouseUp");
        _move = M("InjectMouseMove"); _char = M("InjectChar"); _keyDown = M("InjectKeyDown");
        _getPresent = M("GetPresentWindows"); _getScene = M("GetWindowScene");
        _keyUp = M("InjectKeyUp"); _setModifiers = M("SetModifierKeys");
        _wheel = M("InjectWheel");
        // TickTimers() and TickTimers(bool) both exist: by name alone the lookup is ambiguous and threw,
        // so no WinForms window could open on Windows.
        _tickTimers = dt.GetMethod("TickTimers", BindingFlags.NonPublic | BindingFlags.Instance, Type.EmptyTypes);
        _rightDown = M("InjectRightDown"); _rightUp = M("InjectRightUp");
        _sysKeyDown = M("InjectSysKeyDown"); _sysChar = M("InjectSysChar");
        _getVersion = M("GetPaintVersion"); _getCaret = M("GetCaret");
        _hostActivate = M("HostActivate");
        _getRubberBands = M("GetReversibleRects");
        _getSubtree = M("GetSubtreeWindows"); _isPopup = M("IsInPopupWindow"); _isUnderPopup = M("IsUnderPopup"); _hasLayeredImage = M("HasLayeredImage"); _getLayeredBits = M("GetLayeredBits");
        _getCursor = M("GetActiveCursor");
        _leaveAll = M("InjectMouseLeaveAll");
        // Register as the on-screen host for THIS form, so the driver's message loop drives this
        // window rather than creating a second one of its own.
        _formHandle = form.Handle;
        PresentationHost.Attach(this, form);
    }

    // A window class is process-wide, so registering one per host failed the second time with
    // ERROR_CLASS_ALREADY_EXISTS (0x582) -- which is what happened the moment an app opened a second
    // window, message box included. Register once, and route the shared WndProc back to the host
    // that owns each window.
    private static readonly WndProcDelegate s_wndProc = StaticWindowProc;   // rooted for the process
    private static readonly System.Collections.Generic.Dictionary<IntPtr, Win32Host> s_byHwnd
        = new System.Collections.Generic.Dictionary<IntPtr, Win32Host>();
    private static Win32Host s_creating;
    private static IntPtr s_classNamePtr;
    private static bool s_classRegistered;

    private static void EnsureWindowClass(IntPtr hinstance)
    {
        if (s_classRegistered) return;
        s_classNamePtr = Marshal.StringToHGlobalUni("WinFormsWebGpuHost");
        var wc = new WNDCLASSEXW
        {
            cbSize = (uint)sizeof(WNDCLASSEXW),
            style = 0x0003,  // CS_HREDRAW | CS_VREDRAW
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(s_wndProc),
            hInstance = hinstance,
            // No class cursor: this window sets its own from whatever control is under the
            // pointer, and a class cursor is re-applied on the paths that do not reach our
            // WM_SETCURSOR, which would flick the arrow back on.
            hCursor = IntPtr.Zero,
            lpszClassName = s_classNamePtr,
        };
        if (RegisterClassExW(ref wc) == 0)
            throw new InvalidOperationException($"RegisterClassExW failed (0x{Marshal.GetLastWin32Error():x})");
        s_classRegistered = true;
    }

    private static IntPtr StaticWindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        Win32Host host;
        if (!s_byHwnd.TryGetValue(hwnd, out host))
        {
            host = s_creating;
            if (host != null) s_byHwnd[hwnd] = host;
        }
        if (host == null) return DefWindowProcW(hwnd, msg, wParam, lParam);
        if (msg == 0x0002) s_byHwnd.Remove(hwnd);       // WM_DESTROY: the handle is about to die
        return host.WindowProc(hwnd, msg, wParam, lParam);
    }

    private const int CW_USEDEFAULT = unchecked((int)0x80000000);
    private const int SM_CXSCREEN = 0, SM_CYSCREEN = 1;

    /// <summary>Where to put the window on screen. This used to be the literal (100, 100), so a form
    /// that asked to be somewhere was ignored: Form.Location with StartPosition.Manual did nothing,
    /// a dialog that wanted to be centred was not, and two windows opened together landed exactly on
    /// top of each other. Honour what the form asked for, and where it asked for nothing let Windows
    /// cascade them as it does for every other application.</summary>
    private (int, int) StartLocation()
    {
        // A popup -- a drop-down, a menu, a tooltip -- is positioned by whatever opened it, and
        // in the driver's coordinates rather than the screen's. Win32 ignores CW_USEDEFAULT for
        // a WS_POPUP window for the same reason: nobody wants a menu cascaded into the corner of
        // the desktop. Ours took the default, so a property grid's colour picker opened wherever
        // Windows felt like putting it instead of under the row it belongs to.
        if (_form.FormBorderStyle == FormBorderStyle.None)
        {
            if (TryMapPopup(_form.Left, _form.Top, out int px, out int py)) return (px, py);
            return ToPhysical(_form.Left, _form.Top);
        }

        switch (_form.StartPosition)
        {
            case FormStartPosition.Manual:
                return ToPhysical(_form.Left, _form.Top);

            case FormStartPosition.CenterScreen:
            case FormStartPosition.CenterParent:
            {
                // .NET's CenterToScreen / CenterToParent: the WORKING AREA of the owner's monitor
                // (the cursor's, with no owner) -- not the whole screen, which put a dialog half a
                // taskbar lower than stock -- and for CenterParent the owner's window rectangle,
                // kept inside that working area.
                IntPtr ownerHwnd = (PresentationHost.HostOf(_form.Owner) as Win32Host)?._hwnd ?? IntPtr.Zero;
                IntPtr mon;
                if (ownerHwnd != IntPtr.Zero) mon = MonitorFromWindow(ownerHwnd, 2 /* MONITOR_DEFAULTTONEAREST */);
                else { GetCursorPos(out POINT cur); mon = MonitorFromPoint(cur, 2); }
                var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                if (mon == IntPtr.Zero || !GetMonitorInfoW(mon, ref mi)) return (CW_USEDEFAULT, CW_USEDEFAULT);
                RECT work = mi.rcWork;
                // The window's size on screen: an application that is not per-monitor aware is
                // laid out in 96-dpi pixels and shown at the monitor's scale.
                float k = System.Windows.Forms.ScaleHelper.IsDeviceScaled ? 1f : MonitorDpi(mon) / 96f;
                int w = (int)Math.Round(_form.Width * k), h = (int)Math.Round(_form.Height * k);
                if (_form.StartPosition == FormStartPosition.CenterParent && ownerHwnd != IntPtr.Zero && GetWindowRect(ownerHwnd, out RECT o))
                {
                    int x = o.left + (o.right - o.left - w) / 2, y = o.top + (o.bottom - o.top - h) / 2;
                    x = Math.Max(work.left, Math.Min(x, work.right - w));
                    y = Math.Max(work.top, Math.Min(y, work.bottom - h));
                    return (x, y);
                }
                return (work.left + (work.right - work.left - w) / 2, work.top + (work.bottom - work.top - h) / 2);
            }

            default:
                return (CW_USEDEFAULT, CW_USEDEFAULT);
        }
    }

    /// <summary>
    /// A screen point of an application that is not per-monitor aware, where Windows' DPI
    /// virtualisation puts it: such an application sees each monitor at 96 dpi, so its coordinates
    /// are the monitor's physical ones divided by the monitor's scale, and are multiplied back
    /// here (stock GwxConfigApp's splash, centred at (555, 332) in its own units on a 200% display,
    /// is at (1110, 665) on screen). A per-monitor-aware application's points are physical already.
    /// </summary>
    private static (int, int) ToPhysical(int x, int y)
    {
        if (System.Windows.Forms.ScaleHelper.IsDeviceScaled || x == CW_USEDEFAULT)
            return (x, y);
        foreach (var (mon, r) in Monitors())
        {
            float k = MonitorDpi(mon) / 96f;
            // The monitor as the application sees it: its origin and size over the scale.
            int lx = (int)Math.Round(r.left / k), ly = (int)Math.Round(r.top / k);
            int lw = (int)Math.Round((r.right - r.left) / k), lh = (int)Math.Round((r.bottom - r.top) / k);
            if (x >= lx && x < lx + lw && y >= ly && y < ly + lh)
                return (r.left + (int)Math.Round((x - lx) * k), r.top + (int)Math.Round((y - ly) * k));
        }
        // Off every monitor (a window parked off screen): the primary monitor's scale.
        float kp = MonitorDpi(MonitorFromPoint(new POINT { x = 0, y = 0 }, 1 /* MONITOR_DEFAULTTOPRIMARY */)) / 96f;
        return ((int)Math.Round(x * kp), (int)Math.Round(y * kp));
    }

    private static int MonitorDpi(IntPtr mon)
        => mon != IntPtr.Zero && GetDpiForMonitor(mon, 0 /* MDT_EFFECTIVE_DPI */, out uint dx, out _) == 0 && dx > 0 ? (int)dx : 96;

    private static System.Collections.Generic.List<(IntPtr, RECT)> Monitors()
    {
        var list = new System.Collections.Generic.List<(IntPtr, RECT)>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr m, IntPtr dc, ref RECT r, IntPtr l) => { list.Add((m, r)); return true; }, IntPtr.Zero);
        return list;
    }

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr dc, ref RECT rect, IntPtr data);
    [DllImport("user32")] private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorEnumProc proc, IntPtr data);
    [DllImport("shcore")] private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);

    /// <summary>Turn a point in the driver's screen space into a real screen point, using the
    /// host that owns the window this popup drops out of. Its client area is where the driver's
    /// origin actually landed.</summary>
    private bool TryMapPopup(int driverX, int driverY, out int screenX, out int screenY)
    {
        screenX = screenY = 0;
        var anchor = (PresentationHost.HostOf(_form.Owner) ?? PresentationHost.Current) as Win32Host;
        if (anchor == null || ReferenceEquals(anchor, this) || anchor._hwnd == IntPtr.Zero)
            return false;
        var origin = new POINT { x = 0, y = 0 };
        if (!ClientToScreen(anchor._hwnd, ref origin))
            return false;
        screenX = origin.x + (int)Math.Round((driverX - anchor._ox) * anchor._scale);
        screenY = origin.y + (int)Math.Round((driverY - anchor._oy) * anchor._scale);
        return true;
    }

    /// <summary>A popup that is moved after it is up -- the property grid nudges its drop-down
    /// back into place once it knows how big it came out -- has to take its window with it.
    /// Nothing followed the form before, so the second position was simply lost.</summary>
    private void OnFormMoved(object sender, EventArgs e)
    {
        if (_hwnd == IntPtr.Zero || _movingSelf || _form.FormBorderStyle != FormBorderStyle.None)
            return;
        // A popup goes where its anchor's window says; a borderless window of its own (a splash
        // screen that UpdateLayeredWindow moves) goes where the application put it.
        if (!TryMapPopup(_form.Left, _form.Top, out int px, out int py))
            (px, py) = ToPhysical(_form.Left, _form.Top);
        _movingSelf = true;
        try
        {
            SetWindowPos(_hwnd, IntPtr.Zero, px, py, 0, 0,
                0x0001 | 0x0004 | 0x0010);          // SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE
        }
        finally { _movingSelf = false; }
    }

    private bool _movingSelf;

    /// <summary>Follow the form on and off screen. A form that is hidden rather than closed -- a
    /// drop-down between uses -- kept its window: it stayed on screen holding the keyboard, so
    /// once a picker had been closed the window underneath took no further input, and opening the
    /// picker again showed nothing because the window was already there.</summary>
    /// <summary>Look away from a popup and it is finished with.
    /// <para>Windows watches for this with a global mouse hook -- see PropertyGridView.MouseHook --
    /// which sees a click anywhere at all: in the window the drop-down came from, or in another
    /// application entirely. There is no hook here, but a click landing in this application reaches
    /// the driver and the drop-down's own loop acts on it, and the OS says so when one lands
    /// outside. Windows shows a drop-down WITHOUT activating it (SW_SHOWNA) and so do we, which
    /// makes the application being deactivated the signal that matters; the other two cost nothing
    /// and cover a popup that did take the keyboard.</para>
    /// <para>Nothing told the popup before. It is a window of its own, and a click elsewhere is not
    /// a message the driver ever sees, so a picker clicked away from stayed open behind the window
    /// with its nested message loop still spinning, and the application took no further
    /// input.</para></summary>
    private void DismissPopup(string why)
    {
        if (FormGone || !_form.dismiss_on_deactivate || !_form.Visible)
            return;
        if (s_tracePresent)
            Console.Error.WriteLine($"popup dismissed by {why}");
        _form.Hide();
    }

    private void OnFormVisibleChanged(object sender, EventArgs e)
    {
        if (s_tracePresent)
            Console.Error.WriteLine($"visible[{(FormGone ? "gone" : _form.GetType().Name)}] -> " +
                (FormGone ? "?" : _form.Visible.ToString()) + $" hwnd=0x{_hwnd.ToInt64():x}");
        if (_hwnd == IntPtr.Zero || FormGone)
            return;
        if (_form.Visible)
        {
            if (_form.FormBorderStyle == FormBorderStyle.None
                && TryMapPopup(_form.Left, _form.Top, out int px, out int py))
                SetWindowPos(_hwnd, IntPtr.Zero, px, py, 0, 0, 0x0001 | 0x0004 | 0x0010);
            ShowWindow(_hwnd, SW_SHOWNA);
            _settleUntil = Environment.TickCount64 + 600;   // as on first showing (Show)
            _lastVer = -1;                       // it has no scene from while it was away
            Present();
            return;
        }

        ShowWindow(_hwnd, SW_HIDE);
        // Hand the keyboard back to whatever this dropped out of, or Windows gives it to some other
        // application entirely.
        var anchor = (PresentationHost.HostOf(_form.Owner) ?? PresentationHost.Current) as Win32Host;
        if (anchor != null && !ReferenceEquals(anchor, this) && anchor._hwnd != IntPtr.Zero)
            SetForegroundWindow(anchor._hwnd);
    }

    private const int SW_HIDE = 0, SW_SHOW = 5, SW_SHOWNA = 8;

    /// <summary>WF_TRACE_FRAMES=1: when the window came up and when each present happened or was
    /// skipped, in milliseconds since the process started -- the first-paint delay made visible.</summary>
    private static readonly bool s_traceFrames = Environment.GetEnvironmentVariable("WF_TRACE_FRAMES") == "1";
    private static double SinceStart => (DateTime.Now - System.Diagnostics.Process.GetCurrentProcess().StartTime).TotalMilliseconds;
    private int _framesTraced;

    /// <summary>The loop polls the OS queue and sleeps between frames (PresentationHost.Idle), and a
    /// sleep lasts until the system tick: 15.6 ms unless the process asks for better. So every frame
    /// of an animation paid its own cost AND a whole tick -- a month calendar's 10 ms zoom timer drew
    /// every 35-50 ms where comctl32 draws every 15-20. Asked for once, as the managed WPF compositor
    /// does (Common/Graphics/exports.cs); WF_TIMER_RESOLUTION=0 leaves the system tick alone.</summary>
    private static void EnsureFineTimer()
    {
        if (s_fineTimer) return;
        s_fineTimer = true;
        if (Environment.GetEnvironmentVariable("WF_TIMER_RESOLUTION") == "0") return;
        try { timeBeginPeriod(1); } catch { /* winmm missing: keep the system tick */ }
    }

    private static bool s_fineTimer;

    [DllImport("winmm.dll")] private static extern int timeBeginPeriod(int periodMs);

    public void Show()
    {
        if (s_traceFrames) Console.WriteLine($"[frames] {SinceStart:0} ms: Show ({DateTime.Now:HH:mm:ss.fff}, {_form.GetType().Name})");
        EnsureFineTimer();
        SetProcessDpiAwarenessContext((IntPtr)(-4)); // PER_MONITOR_AWARE_V2 -> real DPI, crisp text
        _hinstance = GetModuleHandleW(null);
        EnsureWindowClass(_hinstance);

        System.Windows.Forms.XplatUIWebGpu.FrameChanged += OnFrameChanged;
        IntPtr title = Marshal.StringToHGlobalUni(_form.Text ?? "WinForms");
        try
        {
            // Claim the window being created, so the shared WndProc can route its very first
            // messages -- they arrive from inside CreateWindowExW, before it has returned a handle.
            s_creating = this;
            (uint style, uint exStyle) = WindowStyles();
            if (_form.TopMost) exStyle |= 0x00000008;   // WS_EX_TOPMOST
            (int wx, int wy) = StartLocation();
            // An owned form's window is OWNED by its owner's, as on Windows: it stays above it and
            // goes with it. Created unowned, a dialog could fall behind the window it belongs to.
            IntPtr ownerHwnd = (PresentationHost.HostOf(_form.Owner) as Win32Host)?._hwnd ?? IntPtr.Zero;
            _hwnd = CreateWindowExW(exStyle, s_classNamePtr, title, style,
                wx, wy, _form.Width, _form.Height, ownerHwnd, IntPtr.Zero, _hinstance, IntPtr.Zero);
            _topmostApplied = _form.TopMost;
            if (_hwnd != IntPtr.Zero) s_byHwnd[_hwnd] = this;
            // Which WinForms window this real one shows: what an app asking Windows about its own
            // windows (EnumWindows, GetForegroundWindow, Process.MainWindowHandle) is answered with,
            // since the form's Handle is the stack's and not this HWND (WpfMac.Win32, Windows.cs).
            if (_hwnd != IntPtr.Zero && _form.IsHandleCreated) SetPropW(_hwnd, StackHandleProperty, _form.Handle);
        }
        finally { s_creating = null; Marshal.FreeHGlobal(title); }
        if (_hwnd == IntPtr.Zero)
            throw new InvalidOperationException($"CreateWindowExW failed (0x{Marshal.GetLastWin32Error():x})");

        // DPI: a per-monitor-aware application lays out in device pixels itself (ScaleHelper), so
        // its window renders 1:1 and the driver learns the display's DPI; any other has its 96-dpi
        // layout scaled to device pixels here (crisp either way).
        int windowDpi = (int)GetDpiForWindow(_hwnd);
        if (System.Windows.Forms.ScaleHelper.IsDeviceScaled)
        {
            _scale = 1f;
            if (_driver is System.Windows.Forms.XplatUIWebGpu dpiDriver) dpiDriver.SetWindowDpi(_formHandle, windowDpi);
        }
        else _scale = windowDpi / 96f;

        // CreateWindowEx sizes the WHOLE window, caption and borders included, but the surface we
        // present is the form-sized CLIENT area. Left as it was, the client area came out smaller than
        // the form (864x521 for an 880x560 form) and the compositor stretched the frame down into it:
        // everything drew ~7% short vertically, and because ClientDip assumes a 1:1 client the hit-test
        // point drifted further off the further down the window you clicked -- a click landed on the
        // control ABOVE the one under the cursor. Grow the window so the CLIENT is exactly form-sized.
        // The form's Size is its WINDOW (frame included, as on Windows); the client is what the
        // surface covers.
        ResizeClientTo(_form.ClientSize.Width, _form.ClientSize.Height);

        if (_gpuRaster)
        {
            var ctx = Microsoft.Wpf.Interop.WebGpu.Composition.WgpuContext.Create();
            IntPtr surface = Microsoft.Wpf.Interop.WebGpu.Composition.Platform.NativePlatform.CreateWindowSurface(ctx.Instance, _hwnd);
            if (surface == IntPtr.Zero) throw new InvalidOperationException("NativePlatform.CreateWindowSurface returned null");
            _wgpu = new WgpuPresenter(ctx, surface, _form.ClientSize.Width, _form.ClientSize.Height, _scale, srgb: true);
            Console.WriteLine($"WebGPU present path active (HWND 0x{_hwnd:x}, format {_wgpu.Format}, scale {_scale}, client {ClientSize()})");
        }
        // Let embedded non-WinForms content (an ElementHost's WPF tree) reach the real window and its
        // scale now that both exist. Inert when nothing is embedded.
        EmbeddedScenes.PublishHostWindow(_hwnd, _scale, _form);
        // What this surface can show is what the driver calls the screen: a popup placed past its
        // edge is not on any screen at all. A popup's own host does not speak for it -- it is
        // smaller than the window it drops out of.
        if (_form.Owner == null)
            XplatUIWebGpu.SetScreenSize(_form.ClientSize.Width, _form.ClientSize.Height);
        _form.LocationChanged += OnFormMoved;
        _form.SizeChanged += OnFormResized;
        _form.VisibleChanged += OnFormVisibleChanged;
        if (s_traceFrames) Console.WriteLine($"[frames] {SinceStart:0} ms: window + surface ready");
        // The first frame is drawn BEFORE the window is shown. It takes the UI thread a second or
        // two (fonts, glyphs, pipelines), and a window on screen that answers no messages for that
        // long is one Windows calls "Not Responding" -- stock WinForms shows its window already
        // painted. So the window stays hidden until the swap chain holds the frame, then appears
        // with it.
        Present();
        ShowWindow(_hwnd, SW_SHOW);
        if (s_traceFrames) Console.WriteLine($"[frames] {SinceStart:0} ms: window shown");
        // Go on presenting the whole frame for a moment now that the window is on screen. What is
        // presented while it is hidden, or just as it appears, is not always what DWM goes on to
        // show: now and then a window came up with its client area white -- caption drawn, content
        // never -- and stayed so until something in it changed, which in a dialog nobody touches
        // is never.
        _settleUntil = Environment.TickCount64 + 600;
        Present();
        _form.HostWindowShown();
    }

    // The OS frame has to say what the form says. Every window used to be created
    // WS_OVERLAPPEDWINDOW, so a FixedDialog came up with a sizing border and a maximise box -- and
    // dragging that border resized the window while the form inside kept its fixed layout, which
    // looks exactly like "resizing does not resize the content". WinForms would not have let you
    // drag it at all.
    private (uint, uint) WindowStyles()
    {
        const uint WS_CAPTION = 0x00C00000, WS_SYSMENU = 0x00080000, WS_THICKFRAME = 0x00040000;
        const uint WS_MINIMIZEBOX = 0x00020000, WS_MAXIMIZEBOX = 0x00010000;
        const uint WS_POPUP = 0x80000000, WS_VISIBLE = 0x10000000;
        const uint WS_EX_TOOLWINDOW = 0x00000080;

        // Not WS_VISIBLE: Show() makes the window visible once its first frame is in the swap chain.
        uint style = 0, exStyle = 0;
        switch (_form.FormBorderStyle)
        {
            case FormBorderStyle.None:
                style |= WS_POPUP;
                return (style, exStyle);

            case FormBorderStyle.FixedToolWindow:
                style |= WS_CAPTION | WS_SYSMENU;
                exStyle |= WS_EX_TOOLWINDOW;
                return (style, exStyle);

            case FormBorderStyle.SizableToolWindow:
                style |= WS_CAPTION | WS_SYSMENU | WS_THICKFRAME;
                exStyle |= WS_EX_TOOLWINDOW;
                return (style, exStyle);

            case FormBorderStyle.Sizable:
                style |= WS_CAPTION | WS_SYSMENU | WS_THICKFRAME;
                break;

            default:        // FixedSingle, Fixed3D, FixedDialog: caption, no sizing border
                style |= WS_CAPTION | WS_SYSMENU;
                break;
        }

        if (_form.MinimizeBox) style |= WS_MINIMIZEBOX;
        if (_form.MaximizeBox && _form.FormBorderStyle == FormBorderStyle.Sizable) style |= WS_MAXIMIZEBOX;
        // .NET's Form.CreateParams: no icon means a modal-dialog frame, which is what keeps Windows
        // from putting its default application icon in the caption (this class registers none).
        if (!_form.ShowIcon) exStyle |= 0x00000001;   // WS_EX_DLGMODALFRAME
        return (style, exStyle);
    }

    /// <summary>EnableWindow on the real window, as Windows does to every other window of the thread
    /// while a modal dialog runs: the OS then refuses input to it and points at the dialog.</summary>
    public void SetEnabled(bool enable)
    {
        if (_hwnd != IntPtr.Zero) EnableWindowReal(_hwnd, enable);
    }

    [DllImport("user32", EntryPoint = "EnableWindow")] private static extern bool EnableWindowReal(IntPtr hwnd, bool enable);

    private bool _topmostApplied;
    private long _settleUntil;

    /// <summary>Form.TopMost on the real window: WS_EX_TOPMOST, kept in step as it changes.
    /// Nothing carried it before, so a TopMost form went behind whatever was activated next.</summary>
    private void SyncTopmost()
    {
        if (_hwnd == IntPtr.Zero || FormGone) return;
        bool want = _form.TopMost;
        if (want == _topmostApplied) return;
        _topmostApplied = want;
        SetWindowPos(_hwnd, want ? (IntPtr)(-1) : (IntPtr)(-2), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);   // HWND_(NO)TOPMOST, NOSIZE|NOMOVE|NOACTIVATE
    }

    private bool _repaintAsked;

    public void Present()
    {
        if (_wgpu == null) return;
        SyncTopmost();
        // The driver's paint version covers the WinForms controls; embedded content (a hosted WPF
        // tree) changes on its own clock, so fold its version in or a WPF-only animation never
        // reaches the screen.
        int ver = (int)_getVersion.Invoke(_driver, null) + EmbeddedScenes.CurrentVersion();
        bool caretOn = CaretOn();
        string save = Environment.GetEnvironmentVariable("WF_WEBGPU_SAVE");
        bool wantSave = !string.IsNullOrEmpty(save) && !_savedGpu;
        // WF_WEBGPU_SAVE_AFTER_MS holds the save until the window has settled (the first present is
        // before layout finishes). Needed where the screen cannot be read -- a locked session.
        if (wantSave && int.TryParse(Environment.GetEnvironmentVariable("WF_WEBGPU_SAVE_AFTER_MS"), out int saveAfter)
            && SinceStart < saveAfter)
        {
            wantSave = false;
            _repaintAsked = true;   // come back: the save is still owed
        }
        // WF_WEBGPU_SAVE_QUIET=1 holds it further, until no progress bar is in the middle of its
        // highlight sweep -- the moment a comparison with another capture can hold still.
        if (wantSave && Environment.GetEnvironmentVariable("WF_WEBGPU_SAVE_QUIET") == "1" && ActiveHighlights() > 0)
        {
            wantSave = false;
            _repaintAsked = true;
        }
        // Just shown: keep presenting the whole frame for a moment (see Show).
        bool settling = _settleUntil != 0 && Environment.TickCount64 < _settleUntil;
        if (settling) _wgpu.InvalidateFrame();
        else _settleUntil = 0;
        if (ver == _lastVer && caretOn == _lastCaretOn && _lastPresentOk && !wantSave && !_repaintAsked && !settling) return;
        _repaintAsked = false;

        double t0 = s_traceFrames ? SinceStart : 0;
        var scenes = GetScenes(out int ox, out int oy);
        Rectangle? caret = GetCaretRect(ox, oy);
        double t1 = s_traceFrames ? SinceStart : 0;
        _lastPresentOk = WantsLayeredHost() && PresentLayered(scenes, caret)
            || _wgpu.PresentScenes(scenes, caret, RubberBands(ox, oy), _form.ClientSize.Width, _form.ClientSize.Height);
        if (s_traceFrames && _framesTraced++ < 4000)
            Console.WriteLine($"[frames] {t0:0} ms: present v{ver} scenes={scenes.Count} ok={_lastPresentOk} took {SinceStart - t0:0} ms (paint {t1 - t0:0}, gpu {SinceStart - t1:0}) damage {_wgpu.LastDamage}");
        _lastVer = ver; _lastCaretOn = caretOn;
        if (wantSave)
        {
            _savedGpu = true;
            byte[] rgba = _wgpu.RenderScenesToRgba(scenes, caret);
            // One file per WINDOW. Every host writes once, and with a dialog on screen there are at
            // least two of them -- sharing one name meant whichever presented last silently replaced
            // the one that was wanted, which is indistinguishable from the wanted one never rendering.
            string named = System.IO.Path.Combine(
                System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(save)),
                System.IO.Path.GetFileNameWithoutExtension(save) + "-" + _form.Text.Replace(' ', '_')
                    + System.IO.Path.GetExtension(save));
            CocoaHost.SaveRgbaPng(rgba, _wgpu.DeviceWidth, _wgpu.DeviceHeight, named);
            Console.WriteLine($"saved GPU-rendered frame -> {named}");
        }
    }

    private static FieldInfo s_activeHighlights;

    private static int ActiveHighlights()
    {
        s_activeHighlights ??= typeof(ProgressBar).GetField("s_active_highlights", BindingFlags.NonPublic | BindingFlags.Static);
        return s_activeHighlights?.GetValue(null) is int n ? n : 0;
    }

    /// <summary>Take the window down because the form closed itself (an OK button rather than the
    /// window close box). Idempotent.</summary>
    public void Close()
    {
        if (_hwnd == IntPtr.Zero) return;
        System.Windows.Forms.XplatUIWebGpu.FrameChanged -= OnFrameChanged;
        IntPtr hwnd = _hwnd;
        _hwnd = IntPtr.Zero;
        _quit = true;
        DestroyWindow(hwnd);
    }

    public bool Pump()
    {
        while (PeekMessageW(out MSG msg, IntPtr.Zero, 0, 0, 0x0001)) // PM_REMOVE
        {
            if (msg.message == 0x0012) { _quit = true; return false; } // WM_QUIT
            if (OfferToMessageFilters(ref msg)) continue;
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }
        return !_quit;
    }

    /// <summary>Input for a real window of this thread that is not a host window -- a hosted WPF
    /// tree's HwndSource, above all -- offered to the application's message filters before it is
    /// dispatched, as WinForms' own message loop offers everything it takes off the queue. Stock
    /// WinForms pumps those windows' messages through Application's loop, so a filter sees a click
    /// or a key in hosted WPF content; DotNetBar closes its menus from one, and a context menu
    /// opened over a WPF canvas could not be closed by clicking elsewhere or by Esc. Host windows'
    /// own input already reaches the filters as the driver's messages (XplatUIWebGpu.FilteredOut).
    /// True: a filter took the message.</summary>
    [ThreadStatic] private static bool t_draining;
    private static readonly bool s_traceFilter = Environment.GetEnvironmentVariable("WF_TRACE_FILTER") == "1";
    private static bool OfferToMessageFilters(ref MSG msg)
    {
        uint m = msg.message;
        bool input = (m >= 0x0100 && m <= 0x0109) || (m >= 0x0200 && m <= 0x020E) || (m >= 0x00A0 && m <= 0x00AD);
        if (!input || msg.hwnd == IntPtr.Zero) return false;
        lock (s_byHwnd) if (s_byHwnd.ContainsKey(msg.hwnd)) return false;
        try
        {
            // One queue, as on Windows: what WinForms posted before a press runs before it (see
            // ElementHost's message-filter hook for the menu this goes wrong for).
            bool press = m == 0x0201 || m == 0x0204 || m == 0x0207 || m == 0x020B || m == 0x00A1 || m == 0x00A4 || m == 0x00A7
                         || m == 0x0100 || m == 0x0104;
            if (press && !t_draining)
            {
                t_draining = true;
                try { Application.DoEvents(); }
                finally { t_draining = false; }
            }
            var message = Message.Create(msg.hwnd, (int)m, msg.wParam, msg.lParam);
            bool taken = Application.FilterMessage(ref message);
            if (s_traceFilter && m != 0x0200 && m != 0x00A0) Console.WriteLine($"win32host filter: msg=0x{m:x} hwnd=0x{msg.hwnd.ToInt64():x} taken={taken}");
            return taken;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("win32host: message filter failed: " + ex.Message);
            return false;
        }
    }

    // WndProc routes input to the driver (client pixels -> DIPs), then repaints + presents so
    // pressed/hover animation and caret updates show immediately (like the Cocoa pump).
    private IntPtr WindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        // WF_TRACE_WINDOWS=1 shows what a POPUP window is actually told, which is the only way to
        // know why one is not dismissing itself.
        if (s_tracePresent && !FormGone && _form.FormBorderStyle == FormBorderStyle.None
            && msg != 0x0200 && msg != 0x0084 && msg != 0x0020 && msg != 0x000F)
            Console.Error.WriteLine($"popup msg 0x{msg:x4} w=0x{(long)wParam:x} l=0x{(long)lParam:x}");

        switch (msg)
        {
            // Take the keyboard back on a click in the WinForms area. Without this, a hosted child
            // window that grabbed focus (an ElementHost's WPF tree) would keep it forever and the
            // WinForms text box would stop receiving typed characters.
            // Capture the mouse for the duration of a drag, exactly as a Win32 app does. Without it
            // the OS stops delivering moves the moment the pointer leaves this window, so a drag
            // that goes outside simply stopped being reported: dragging a ListView column edge past
            // the dialog's edge could not widen the column beyond the dialog.
            case 0x0201: Trace("WM_LBUTTONDOWN", lParam); SetFocus(hwnd); SetCapture(hwnd);
                MouseAt(lParam, _down); Frame(); return IntPtr.Zero;                // WM_LBUTTONDOWN
            case 0x0202: Trace("WM_LBUTTONUP", lParam); ReleaseCapture();
                MouseAt(lParam, _up); Frame(); return IntPtr.Zero;                  // WM_LBUTTONUP
            // The right button. Focus follows it, as it does in Windows -- the menu it raises
            // acts on the control that has the keyboard -- but no capture: the menu takes its
            // own. WinForms turns the release into WM_CONTEXTMENU itself.
            case 0x0204: Trace("WM_RBUTTONDOWN", lParam); SetFocus(hwnd);
                MouseAt(lParam, _rightDown); Frame(); return IntPtr.Zero;           // WM_RBUTTONDOWN
            case 0x0205: Trace("WM_RBUTTONUP", lParam);
                MouseAt(lParam, _rightUp); Frame(); return IntPtr.Zero;             // WM_RBUTTONUP
            // WM_ACTIVATE. A popup is dismissed by looking away from it: clicking the window it dropped
            // out of, or another application altogether. Nothing told it so -- it is a window of its own,
            // and a click elsewhere is not a message the driver ever sees -- so a colour picker that was
            // clicked away from stayed open behind the window, and the nested loop it runs in went on
            // spinning: the application took no further input. Windows closes a menu on exactly this.
            case 0x0006:                                   // WM_ACTIVATE
                if (((long)wParam & 0xFFFF) == 0) DismissPopup("WM_ACTIVATE");
                // The form learns it was activated -- which is when a WinForms window takes focus.
                if (_formHandle != IntPtr.Zero)
                    _hostActivate?.Invoke(_driver, new object[] { _formHandle.ToInt64(), ((long)wParam & 0xFFFF) != 0 });
                return DefWindowProcW(hwnd, msg, wParam, lParam);
            case 0x0008:                                   // WM_KILLFOCUS: the keyboard went elsewhere
                DismissPopup("WM_KILLFOCUS");
                return DefWindowProcW(hwnd, msg, wParam, lParam);
            case 0x001C:                                   // WM_ACTIVATEAPP: another application entirely
                // Every window of the application is told; the consequence is the application's, not this
                // window's, so it is said once in a place every head shares.
                if (wParam == IntPtr.Zero) PresentationHost.ApplicationDeactivated();
                return DefWindowProcW(hwnd, msg, wParam, lParam);
            // WM_MOUSEWHEEL carries SCREEN coordinates and the notch count in the wParam high word.
            // Nothing forwarded it, so the wheel did nothing anywhere -- no list, grid or text box
            // scrolled.
            case 0x020A:
                if (_wheel != null)
                {
                    int delta = (short)((long)wParam >> 16);
                    var pt = new POINT { x = (short)((long)lParam & 0xFFFF), y = (short)(((long)lParam >> 16) & 0xFFFF) };
                    ScreenToClient(hwnd, ref pt);
                    _wheel.Invoke(_driver, new object[] { _ox + (int)(pt.x / _scale), _oy + (int)(pt.y / _scale), delta });
                    Frame();
                }
                return IntPtr.Zero;
            // WM_MOUSEMOVE: deliver the move, then set the cursor. Doing it here rather than
            // waiting for WM_SETCURSOR means the control under the pointer has already had its
            // chance to ask for a shape -- a list view's header asks for the double arrow from
            // its own MouseMove -- so the answer is never a move behind.
            case 0x0200:
                // Ask to be told when the pointer leaves the window. Without it nothing ever left:
                // a pointer that crossed a control and went off the window kept that control hot
                // (a month calendar's heading stayed in the hover blue).
                if (!_trackingLeave)
                {
                    var tme = new TRACKMOUSEEVENT { cbSize = (uint)Marshal.SizeOf<TRACKMOUSEEVENT>(), dwFlags = 0x2 /* TME_LEAVE */, hwndTrack = hwnd };
                    _trackingLeave = TrackMouseEvent(ref tme);
                }
                MouseMove(lParam); ApplyCursor(); Frame(); return IntPtr.Zero;
            case 0x02A3:        // WM_MOUSELEAVE
                _trackingLeave = false;
                // Not always a leave: Windows cancels the tracking -- and posts this -- around a
                // capture change as well, so a click on a list's scroll track "left" the window with
                // the pointer still on the bar, and the bar shut under it. Only a pointer that is
                // really over another window has left.
                if (GetCursorPos(out POINT at) && WindowFromPoint(at) == hwnd)
                {
                    var again = new TRACKMOUSEEVENT { cbSize = (uint)Marshal.SizeOf<TRACKMOUSEEVENT>(), dwFlags = 0x2 /* TME_LEAVE */, hwndTrack = hwnd };
                    _trackingLeave = TrackMouseEvent(ref again);
                    return IntPtr.Zero;
                }
                _leaveAll?.Invoke(_driver, null);
                Frame();
                return IntPtr.Zero;
            // WM_SETCURSOR: the window class carries a plain arrow, so without answering this
            // the pointer stayed an arrow no matter what a control asked for -- no I-beam over
            // text, no double arrow over a column divider. Only the client area is ours; the
            // frame and buttons belong to the default handling.
            case 0x0020:
                if ((((long)lParam) & 0xFFFF) == 1 && ApplyCursor())   // HTCLIENT
                    return (IntPtr)1;
                return DefWindowProcW(hwnd, msg, wParam, lParam);
            case 0x0102:                                                          // WM_CHAR
                if (_swallowChar) { _swallowChar = false; return IntPtr.Zero; }
                char typed = (char)(int)wParam;
                // A control combination (Ctrl+C is 0x03) arrives here as a control code as well as
                // a key-down. The key-down is the one a control acts on; inserting the code too put
                // a stray character in the text box.
                if (typed < ' ' && (ModifierState() & Keys.Control) != 0) return IntPtr.Zero;
                _char.Invoke(_driver, new object[] { typed }); Frame(); return IntPtr.Zero;
            case 0x0100:                                                          // WM_KEYDOWN
                int vk = (int)wParam;
                PublishModifiers();
                // Win32 VK == WinForms Keys, so keys pass straight through. Backspace(8)
                // Return(13) and Escape(27) are the exception: the driver synthesises their WM_CHAR
                // from the key-down, and TranslateMessage sends one as well, so forwarding those
                // here would act on them twice.
                //
                // Tab(9) used to be excluded on the same grounds, and that is why Tab did nothing
                // at all: it is the key that moves the focus, and the driver never saw it. Forward
                // it, and when navigation takes it, drop the tab character that follows so it does
                // not also get typed into whatever just received the focus.
                if (vk == 9)
                {
                    _swallowChar = _keyDown.Invoke(_driver, new object[] { vk }) is bool b && b;
                    Frame();
                }
                else if (vk != 8 && vk != 13 && vk != 27)
                { _keyDown.Invoke(_driver, new object[] { vk }); Frame(); }
                return IntPtr.Zero;
            case 0x0101:                                                          // WM_KEYUP
                PublishModifiers();
                _keyUp?.Invoke(_driver, new object[] { (int)wParam });
                return IntPtr.Zero;
            // Alt combinations arrive as the SYS variants and nothing forwarded them, so a mnemonic
            // (Alt+C for a "&Copy" button) never reached WinForms at all.
            case 0x0104:                                                          // WM_SYSKEYDOWN
                PublishModifiers();
                _sysKeyDown?.Invoke(_driver, new object[] { (int)wParam });
                Frame();
                return IntPtr.Zero;
            case 0x0106:                                                          // WM_SYSCHAR
                _sysChar?.Invoke(_driver, new object[] { (char)(int)wParam });
                Frame();
                return IntPtr.Zero;
            // WM_NCCALCSIZE: where the client goes. A form that draws its own caption has said so to the
            // driver (its own answer to WM_NCCALCSIZE); the window then has that frame, not Windows' --
            // usually none, the form painting its title bar itself, as stock WinForms shows it.
            case 0x0083 when lParam != IntPtr.Zero && OwnFrame(out System.Windows.Forms.Padding own):
            {
                // wParam FALSE (the first, from CreateWindowEx): one RECT. TRUE: NCCALCSIZE_PARAMS, whose first
                // RECT is the same one. Both answer by shrinking it.
                RECT* r = (RECT*)lParam;
                if (s_traceFrame) Console.Error.WriteLine($"[frame] host NCCALCSIZE window {r->left},{r->top},{r->right},{r->bottom} own {own}");
                r->left += (int)Math.Round(own.Left * _scale); r->top += (int)Math.Round(own.Top * _scale);
                r->right -= (int)Math.Round(own.Right * _scale); r->bottom -= (int)Math.Round(own.Bottom * _scale);
                return IntPtr.Zero;
            }
            case 0x0005: OnClientResized(); return IntPtr.Zero;                  // WM_SIZE
            // WM_PAINT: Windows wants the window's pixels again -- it has just been shown, uncovered
            // or moved, and the surface's contents were not kept (they are not, on the GL backend).
            // Nothing answered it: DefWindowProc validated the window and drew nothing, and the
            // present path skips a frame whose scene has not changed, so the window stayed white or
            // black until something in it happened to change. That was the "first render takes
            // seconds" -- or never comes: the one frame presented from Show can land before the
            // window is on screen at all. Validate, then present the current scene regardless.
            case 0x000F:
                ValidateRect(hwnd, IntPtr.Zero);
                _repaintAsked = true;
                Present();
                return IntPtr.Zero;
            case 0x0014: return (IntPtr)1;                                          // WM_ERASEBKGND: the frame covers it
            // Dragging or resizing a window puts Windows into a modal loop of its own inside
            // DefWindowProc, and this window's frame loop does not run again until it ends -- so
            // every animation froze for as long as the window was being moved. A Win32 timer is
            // dispatched by that loop when nothing else is, which is exactly what it is for.
            case 0x0231:                                                          // WM_ENTERSIZEMOVE
                SetTimer(hwnd, FrameTimerId, 16, IntPtr.Zero);
                return DefWindowProcW(hwnd, msg, wParam, lParam);
            case 0x0232:                                                          // WM_EXITSIZEMOVE
                KillTimer(hwnd, FrameTimerId);
                return DefWindowProcW(hwnd, msg, wParam, lParam);
            case 0x0113:                                                          // WM_TIMER
                if ((int)wParam == FrameTimerId) { Frame(); return IntPtr.Zero; }
                return DefWindowProcW(hwnd, msg, wParam, lParam);
            // WM_DPICHANGED: the window moved to a display with a different DPI. Nothing acted
            // on it, so _scale kept the DPI the window was created at while the client area
            // changed underneath it -- OnClientResized then read the new pixels through the old
            // scale, grew the form to match, and every control drew at half the size it should.
            case 0x02E0: OnDpiChanged(wParam, lParam); return IntPtr.Zero;
            // WM_GETDPISCALEDSIZE: a per-monitor-aware form says which size it wants at the new
            // DPI (AutoScaleMode.Font's, or the one it had there) instead of the linear one.
            case 0x02E4:
                if (System.Windows.Forms.ScaleHelper.IsThreadPerMonitorV2Aware && !FormGone && _form.GetDpiScaledSizeForHost((int)((long)wParam & 0xFFFF), out Size wanted))
                {
                    Marshal.WriteInt32(lParam, 0, wanted.Width);
                    Marshal.WriteInt32(lParam, 4, wanted.Height);
                    return (IntPtr)1;
                }
                return IntPtr.Zero;
            // WM_GETOBJECT: the controls in this window have no handles of their own, so the
            // automation tree Windows builds out of handles stopped here and everything below
            // was invisible to a screen reader. Answer for the tree ourselves.
            case 0x003D:
                if (WinFormsWebGpu.Accessibility.Uia.TryAnswerGetObject(this, wParam, lParam, out IntPtr uia))
                    return uia;
                return DefWindowProcW(hwnd, msg, wParam, lParam);
            // WM_CLOSE: close the FORM, not just its window. Destroying the window on its own left
            // a dialog's modal loop running with nothing on screen, and the form still visible --
            // so the next tick promptly gave it a new window. Honour a cancelled OnClosing too.
            case 0x0010:
                _form.Close();
                if (!_form.Visible) DestroyWindow(hwnd);
                return IntPtr.Zero;
            // WM_DESTROY: retire this host and let the loop end on its own once no window is left
            // (Tick returns false when there are no hosts). Deliberately NO PostQuitMessage: a
            // thread quit is not this window's to post. A dialog closing would take the whole
            // application with it, and in a WPF app -- where the dispatcher owns the thread queue --
            // so would the splash screen: SharpDevelop shut down the moment its splash closed.
            case 0x0002:
                _quit = true;
                PresentationHost.Detach(this);
                return IntPtr.Zero;
            default: return DefWindowProcW(hwnd, msg, wParam, lParam);
        }
    }

    // The driver has no keyboard: read the real modifier state and push it in, so Control.ModifierKeys
    // answers truthfully and shortcuts like Ctrl+C resolve.
    private static Keys ModifierState()
    {
        Keys mods = Keys.None;
        if ((GetKeyState(0x11) & 0x8000) != 0) mods |= Keys.Control;   // VK_CONTROL
        if ((GetKeyState(0x10) & 0x8000) != 0) mods |= Keys.Shift;     // VK_SHIFT
        if ((GetKeyState(0x12) & 0x8000) != 0) mods |= Keys.Alt;       // VK_MENU
        return mods;
    }

    private void PublishModifiers() => _setModifiers?.Invoke(_driver, new object[] { ModifierState() });

    // One frame's worth of work in response to input. Timers tick here as well as in the message
    // loop: while the pointer is moving there is always another message waiting, so the loop never
    // reaches its idle path and WinForms timers were starved for as long as the interaction lasted
    // -- any animation stuttered exactly while the user was doing something.
    private void Frame()
    {
        _tickTimers?.Invoke(_driver, null);
        Application.DoEvents();
        Present();
    }

    private static readonly bool s_trace = Environment.GetEnvironmentVariable("WF_TRACE_INPUT") == "1";
    private void Trace(string what, IntPtr lParam)
    {
        if (!s_trace) return;
        (int x, int y) = ClientDip(lParam);
        // Where the click lands in the driver's space, which window it resolves to, and what
        // control that is -- the three things needed to tell "the point is wrong" from "the point
        // is right but the wrong window owns it".
        string who = "?";
        try
        {
            var at = _driver.GetType().GetMethod("WindowAtPoint",
                BindingFlags.NonPublic | BindingFlags.Instance);
            IntPtr h = (IntPtr)at.Invoke(_driver, new object[] { x, y });
            Control c = Control.FromHandle(h);
            who = $"win=0x{h.ToInt64():x} control={(c == null ? "<none>" : c.GetType().Name + " '" + c.Name + "'")}";
        }
        catch (Exception ex) { who = "lookup failed: " + ex.Message; }
        Console.WriteLine($"win32host: {what} driver ({x},{y}) origin ({_ox},{_oy}) {who}");
        TraceGeometryOnce();
    }

    private bool _tracedGeometry;

    // The form's own coordinate space versus the window it is presented into. If a form auto-scales
    // itself (AutoScaleMode.Dpi) its controls move in ITS units, while the window, the surface and
    // the incoming clicks are in the host's -- and a click then lands somewhere else entirely.
    private void TraceGeometryOnce()
    {
        if (_tracedGeometry) return;
        _tracedGeometry = true;
        GetClientRect(_hwnd, out RECT r);
        Console.WriteLine($"win32host: form '{_form.Text}' bounds={_form.Bounds} client={_form.ClientSize} " +
                          $"autoScale={_form.AutoScaleMode} dims={_form.AutoScaleDimensions} " +
                          $"current={_form.CurrentAutoScaleDimensions} | hwnd client={r.right - r.left}x{r.bottom - r.top} scale={_scale}");
        DumpTree(_form, 1);
    }

    private static void DumpTree(Control c, int depth)
    {
        foreach (Control child in c.Controls)
        {
            Console.WriteLine($"win32host:   {new string(' ', depth * 2)}{child.GetType().Name} '{child.Name}' {child.Bounds}");
            if (depth < 3) DumpTree(child, depth + 1);
        }
    }

    private string ClientSize()
    {
        GetClientRect(_hwnd, out RECT r);
        return $"{r.right - r.left}x{r.bottom - r.top}";
    }

    // Grow/shrink the window so its CLIENT area is exactly logicalW x logicalH points at the current
    // DPI -- i.e. exactly the surface the presenter configures. Any mismatch is not a cosmetic border:
    // the compositor rescales the presented frame into the client rect, which silently breaks the
    // 1:1 mapping ClientDip relies on to turn a click into a driver coordinate.
    /// <summary>The frame the form drew for itself, in its own pixels, when it has one (see
    /// XplatUIWebGpu.AskFrame); false for a form in an ordinary Windows frame.</summary>
    private static readonly bool s_traceFrame = Environment.GetEnvironmentVariable("WF_TRACE_FRAME") == "1";

    private bool OwnFrame(out System.Windows.Forms.Padding frame)
    {
        frame = System.Windows.Forms.Padding.Empty;
        bool own = _formHandle != IntPtr.Zero && _driver is System.Windows.Forms.XplatUIWebGpu d && d.HasOwnFrame(_formHandle, out frame);
        if (s_traceFrame) Console.Error.WriteLine($"[frame] host OwnFrame form 0x{_formHandle:x} driver {_driver?.GetType().Name} -> {own} {frame}");
        return own;
    }

    // The form answered WM_NCCALCSIZE differently: Windows asks this window again.
    private void OnFrameChanged(IntPtr handle)
    {
        if (s_traceFrame) Console.Error.WriteLine($"[frame] host frame changed 0x{handle:x} form 0x{_formHandle:x} hwnd 0x{_hwnd:x}");
        if (handle != _formHandle || _hwnd == IntPtr.Zero) return;
        SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0004 | 0x0010 | 0x0020);   // NOSIZE|NOMOVE|NOZORDER|NOACTIVATE|FRAMECHANGED
    }

    private void ResizeClientTo(int logicalW, int logicalH)
    {
        var r = new RECT { left = 0, top = 0, right = (int)Math.Round(logicalW * _scale), bottom = (int)Math.Round(logicalH * _scale) };
        if (OwnFrame(out System.Windows.Forms.Padding own))
        {
            SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, r.right + (int)Math.Round(own.Horizontal * _scale), r.bottom + (int)Math.Round(own.Vertical * _scale),
                         0x0002 | 0x0004 | 0x0010 | 0x0020);   // and FRAMECHANGED: the size may be the same
            return;
        }
        int style = (int)GetWindowLongPtrW(_hwnd, -16);     // GWL_STYLE
        int exStyle = (int)GetWindowLongPtrW(_hwnd, -20);   // GWL_EXSTYLE
        // The per-DPI variant accounts for this window's DPI rather than the process default; it only
        // exists on Windows 10 1607+, so fall back to the classic call.
        if (!AdjustWindowRectExForDpi(ref r, style, false, exStyle, GetDpiForWindow(_hwnd)))
            AdjustWindowRectEx(ref r, style, false, exStyle);
        SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, r.right - r.left, r.bottom - r.top,
                     0x0002 | 0x0004 | 0x0010);            // SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE
    }

    // The user resized the window: adopt the new client area as the form's size so the WinForms
    // layout, the driver's windows and the swap chain all agree again (Present reconfigures when the
    // size it is handed changes).
    // Windows suggests where the window should go at the new DPI -- honouring it is what keeps
    // the window the same apparent size across the move. The presenter needs the new scale too,
    // or the swap chain stays at the old device-pixel size.
    private void OnDpiChanged(IntPtr wParam, IntPtr suggested)
    {
        if (_hwnd == IntPtr.Zero)
            return;
        if (System.Windows.Forms.ScaleHelper.IsDeviceScaled)
        {
            // The application rescales itself, as on Windows: the form and its children are told
            // (WM_DPICHANGED_BEFOREPARENT, WM_DPICHANGED, ..._AFTERPARENT) and the form takes the
            // suggested rectangle, which moves and sizes this window through the driver.
            int dpi = (int)((long)wParam & 0xFFFF);
            Rectangle rect = default;
            if (suggested != IntPtr.Zero)
            {
                RECT r = Marshal.PtrToStructure<RECT>(suggested);
                rect = Rectangle.FromLTRB(r.left, r.top, r.right, r.bottom);
            }
            if (System.Windows.Forms.ScaleHelper.IsThreadPerMonitorV2Aware && _driver is System.Windows.Forms.XplatUIWebGpu d && !FormGone)
                d.HostDpiChanged(_formHandle, dpi, rect);
            _lastVer = -1;
            Present();
            return;
        }
        _scale = GetDpiForWindow(_hwnd) / 96f;
        if (suggested != IntPtr.Zero)
        {
            RECT r = Marshal.PtrToStructure<RECT>(suggested);
            SetWindowPos(_hwnd, IntPtr.Zero, r.left, r.top, r.right - r.left, r.bottom - r.top,
                0x0004 | 0x0010);                        // SWP_NOZORDER | SWP_NOACTIVATE
        }
        _wgpu?.SetScale(_scale);
        EmbeddedScenes.PublishHostWindow(_hwnd, _scale, _form);
        _lastVer = -1;                                   // force a present at the new scale
        OnClientResized();
        Present();
    }

    /// <summary>The application resized its form (Size, ClientSize, a new MinimumSize): the window
    /// follows. Only the user's resizing reached the window before, so a dialog that grows to show
    /// more -- a login dialog's "Advanced" -- kept its old window, the rest of it cut off.</summary>
    private void OnFormResized(object sender, EventArgs e)
    {
        if (_hwnd == IntPtr.Zero || FormGone || _form.WindowState != FormWindowState.Normal) return;
        GetClientRect(_hwnd, out RECT r);
        int lw = (int)Math.Round((r.right - r.left) / _scale), lh = (int)Math.Round((r.bottom - r.top) / _scale);
        Size want = _form.ClientSize;
        if (want.Width <= 0 || want.Height <= 0 || (lw == want.Width && lh == want.Height)) return;
        ResizeClientTo(want.Width, want.Height);
    }

    private void OnClientResized()
    {
        if (_wgpu == null) return;
        GetClientRect(_hwnd, out RECT r);
        int lw = (int)Math.Round((r.right - r.left) / _scale), lh = (int)Math.Round((r.bottom - r.top) / _scale);
        if (lw <= 0 || lh <= 0) return;                     // minimised
        if (lw == _form.ClientSize.Width && lh == _form.ClientSize.Height) return;
        // As Win32 tells a window it was sized (see XplatUIWebGpu.HostResized), not through ClientSize.
        if (_driver is System.Windows.Forms.XplatUIWebGpu d && !FormGone) d.HostResized(_formHandle, lw, lh);
        else _form.ClientSize = new Size(lw, lh);
        Application.DoEvents();                             // let the WinForms layout settle first
        _lastVer = -1;                                      // force a present at the new size
        Present();
    }

    private void MouseAt(IntPtr lParam, MethodInfo inject)
    {
        (int x, int y) = ClientDip(lParam);
        inject.Invoke(_driver, new object[] { x, y });
    }
    private void MouseMove(IntPtr lParam)
    {
        (int x, int y) = ClientDip(lParam);
        _move.Invoke(_driver, new object[] { x, y, (GetKeyState(0x01) & 0x8000) != 0 }); // left button down?
    }
    private (int, int) ClientDip(IntPtr lParam)
    {
        int lp = (int)lParam;
        int px = (short)(lp & 0xFFFF), py = (short)((lp >> 16) & 0xFFFF);   // client PIXELS
        // The driver addresses windows in ITS OWN screen space, and this form sits at some origin
        // in it -- which Present subtracts when it composites the scenes into this window. Input has
        // to add it back. Without it a click was delivered to whatever happened to be at the same
        // offset from the driver's origin, so a dialog centred at (1072,471) sent every click to the
        // windows behind it and none of its own controls ever responded. A form at (0,0) -- an
        // application's main window, which is all there was to test -- worked by accident.
        return (_ox + (int)(px / _scale), _oy + (int)(py / _scale));        // -> driver DIPs
    }

    /// <summary>Show whatever cursor the control under the pointer asked for. The driver names
    /// shapes rather than handles -- it has no idea what a Windows cursor is -- so the mapping
    /// from a shape to this platform's cursor lives here, and the Cocoa host maps the same
    /// names to NSCursor.</summary>
    private bool ApplyCursor()
    {
        if (_getCursor == null) return false;
        int id;
        try { id = (int)_getCursor.Invoke(_driver, null); }
        catch (Exception) { return false; }
        if (s_traceCursor) Console.WriteLine($"WM_SETCURSOR -> StdCursor {id}");
        if (id < 0) return false;

        int idc;
        switch (id)
        {
            case 1: idc = 32650; break;                       // AppStarting
            case 3: idc = 32515; break;                       // Cross
            case 4: idc = 32649; break;                       // Hand
            case 5: idc = 32651; break;                       // Help
            case 6: idc = 32645; break;                       // HSplit  -> size N-S
            case 7: idc = 32513; break;                       // IBeam
            case 8: idc = 32648; break;                       // No
            case 9: case 10: case 11: idc = 32646; break;     // NoMove* -> size all
            case 12: case 13: case 14: case 15:
            case 16: case 17: case 18: case 19: idc = 32646; break;   // Pan*
            case 20: idc = 32646; break;                      // SizeAll
            case 21: idc = 32643; break;                      // SizeNESW
            case 22: idc = 32645; break;                      // SizeNS
            case 23: idc = 32642; break;                      // SizeNWSE
            case 24: idc = 32644; break;                      // SizeWE
            case 25: idc = 32516; break;                      // UpArrow
            case 26: idc = 32644; break;                      // VSplit  -> size W-E
            case 27: idc = 32514; break;                      // Wait
            default: idc = 32512; break;                      // Default / Arrow
        }
        IntPtr cursor = LoadCursorW(IntPtr.Zero, idc);
        if (cursor == IntPtr.Zero) return false;
        SetCursor(cursor);
        return true;
    }

    // ---- accessibility site ------------------------------------------------------
    //
    // The stack renders to a virtual 96-DPI screen which this window magnifies, so a control's
    // own coordinates are neither client pixels nor screen pixels. Both directions go through
    // the same origin and scale the input path uses; see ClientDip.

    IntPtr WinFormsWebGpu.Accessibility.IA11yHostSite.Handle => _hwnd;
    Form WinFormsWebGpu.Accessibility.IA11yHostSite.Form => FormGone ? null : _form;

    bool WinFormsWebGpu.Accessibility.IA11yHostSite.TryMapToScreen(Rectangle driverRect,
        out double x, out double y, out double width, out double height)
    {
        x = y = width = height = 0;
        if (_hwnd == IntPtr.Zero)
            return false;
        var origin = new POINT { x = 0, y = 0 };
        if (!ClientToScreen(_hwnd, ref origin))
            return false;
        x = origin.x + (driverRect.X - _ox) * _scale;
        y = origin.y + (driverRect.Y - _oy) * _scale;
        width = driverRect.Width * _scale;
        height = driverRect.Height * _scale;
        return true;
    }

    bool WinFormsWebGpu.Accessibility.IA11yHostSite.TryWindowRect(out double x, out double y,
        out double width, out double height)
    {
        x = y = width = height = 0;
        if (_hwnd == IntPtr.Zero) return false;
        RECT r;
        if (!GetWindowRect(_hwnd, out r)) return false;
        x = r.left; y = r.top; width = r.right - r.left; height = r.bottom - r.top;
        return true;
    }

    /// <summary>Where the real pointer is, in the driver's screen space: through the host window
    /// under it, or the top host. The driver only sees the pointer over its own windows, so over a
    /// hosted WPF area (an ElementHost's real child HWND) its own record went stale -- and a
    /// context menu opened from there at Control.MousePosition landed in the form's corner.</summary>
    /// <summary>Whether one of the driver's popups is drawn at this real screen point, over a host
    /// window's content: a hosted WPF tree uses it to pass such input on to the host (see
    /// ElementHost's WM_NCHITTEST). Without it the canvas under a context menu took the clicks
    /// aimed at the menu -- a second right-click opened another menu, and DotNetBar orphaned the
    /// first one's popup window, empty, on the canvas.</summary>
    internal static bool IsDriverPopupAt(IntPtr hostedWindow, int realX, int realY)
    {
        var pt = new POINT { x = realX, y = realY };
        // From the hosted window itself, not WindowFromPoint: that hit-tests, and this is asked from
        // inside a hit test.
        IntPtr root = hostedWindow == IntPtr.Zero ? IntPtr.Zero : GetAncestor(hostedWindow, 2 /* GA_ROOT */);
        Win32Host host = null;
        lock (s_byHwnd)
            if (root != IntPtr.Zero) s_byHwnd.TryGetValue(root, out host);
        if (host == null || host._hwnd == IntPtr.Zero || host._isUnderPopup == null || host.FormGone) return false;
        var c = pt;
        if (!ScreenToClient(host._hwnd, ref c)) return false;
        int dx = host._ox + (int)Math.Floor(c.x / host._scale), dy = host._oy + (int)Math.Floor(c.y / host._scale);
        try { return (bool)host._isUnderPopup.Invoke(host._driver, new object[] { host._formHandle, dx, dy }); }
        catch { return false; }
    }

    internal static bool TryRealCursor(out int x, out int y)
    {
        x = y = 0;
        if (!GetCursorPos(out POINT pt)) return false;
        Win32Host host = null;
        IntPtr under = WindowFromPoint(pt);
        IntPtr root = under == IntPtr.Zero ? IntPtr.Zero : GetAncestor(under, 2 /* GA_ROOT */);
        lock (s_byHwnd)
            if (root != IntPtr.Zero) s_byHwnd.TryGetValue(root, out host);
        host ??= PresentationHost.Current as Win32Host;
        if (host == null || host._hwnd == IntPtr.Zero) return false;
        var c = pt;
        if (!ScreenToClient(host._hwnd, ref c)) return false;
        x = host._ox + (int)Math.Floor(c.x / host._scale);
        y = host._oy + (int)Math.Floor(c.y / host._scale);
        return true;
    }

    [DllImport("user32")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

    bool WinFormsWebGpu.Accessibility.IA11yHostSite.TryMapFromScreen(double x, double y, out Point driverPoint)
    {
        driverPoint = Point.Empty;
        if (_hwnd == IntPtr.Zero)
            return false;
        var pt = new POINT { x = (int)Math.Round(x), y = (int)Math.Round(y) };
        if (!ScreenToClient(_hwnd, ref pt))
            return false;
        driverPoint = new Point(_ox + (int)(pt.x / _scale), _oy + (int)(pt.y / _scale));
        return true;
    }

    public void InjectClickScreen(int x, int y) => _injectClick.Invoke(_driver, new object[] { x, y });
    public void SaveFrame(string path)
    {
        if (_wgpu == null) return;
        var scenes = GetScenes(out int ox, out int oy);
        byte[] rgba = _wgpu.RenderScenesToRgba(scenes, GetCaretRect(ox, oy));
        CocoaHost.SaveRgbaPng(rgba, _wgpu.DeviceWidth, _wgpu.DeviceHeight, path);
    }

    // ---- shared driver-bridge helpers (mirror CocoaHost) -------------------------

    private static readonly bool s_traceScenes = Environment.GetEnvironmentVariable("WF_TRACE_SCENES") == "1";
    private string _lastSceneTrace;

    private System.Collections.Generic.List<(object, int, int)> GetScenes(out int ox, out int oy)
    {
        long[] wins = PresentWindows();
        ox = wins.Length >= 3 ? (int)wins[1] : 0;
        oy = wins.Length >= 3 ? (int)wins[2] : 0;
        _ox = ox; _oy = oy;                 // input maps through the same origin; see ClientDip
        var list = new System.Collections.Generic.List<(object, int, int)>(wins.Length / 3);
        var tops = new System.Collections.Generic.List<IntPtr>(wins.Length / 3);
        for (int i = 0; i + 2 < wins.Length; i += 3)
        {
            object scene = _getScene.Invoke(_driver, new object[] { (IntPtr)wins[i] });
            if (scene == null) continue;
            list.Add((scene, (int)wins[i + 1] - ox, (int)wins[i + 2] - oy));
            Control c = Control.FromHandle((IntPtr)wins[i]);
            Control top = c?.TopLevelControl;
            tops.Add(top != null && top.IsHandleCreated ? top.Handle : (IntPtr)wins[i]);
        }
        // Embedded non-WinForms content right AFTER the scenes of the window it is in, so it draws
        // over the control whose area it occupies -- and UNDER every window above that one. Appended
        // at the very end it covered the menus, drop-downs and tooltips the driver puts on top: a
        // context menu opened over a hosted WPF canvas came up behind the canvas.
        var embedded = EmbeddedScenes.GetWithOwners(ox, oy);
        if (s_traceScenes)
        {
            var sb = new System.Text.StringBuilder("win32host scenes:");
            for (int i = 0; i < tops.Count; i++) sb.Append($" {wins[i * 3]:x}/{tops[i].ToInt64():x}");
            if (embedded != null) foreach (var e in embedded) sb.Append($" [embedded top {e.TopLevel.ToInt64():x}]");
            string line = sb.ToString();
            if (line != _lastSceneTrace) { _lastSceneTrace = line; Console.WriteLine(line); }
        }
        if (embedded == null) return list;
        var ordered = new System.Collections.Generic.List<(object, int, int)>(list.Count + embedded.Count);
        var placed = new bool[embedded.Count];
        for (int i = 0; i < list.Count; i++)
        {
            ordered.Add(list[i]);
            if (tops.LastIndexOf(tops[i]) != i) continue;          // not that window's last scene
            for (int k = 0; k < embedded.Count; k++)
                if (!placed[k] && embedded[k].TopLevel == tops[i]) { ordered.Add((embedded[k].Scene, embedded[k].X, embedded[k].Y)); placed[k] = true; }
        }
        // What is left sits in a window this host does not draw. Content inside ANOTHER form is that
        // form's host's to show (or nobody's, if that form is hidden): drawn here too it was laid
        // over everything -- a modal login dialog opened over an app with a hosted WPF canvas came
        // up as a blank white window, the canvas covering its controls. Only content that is in no
        // form at all is drawn last, as before.
        for (int k = 0; k < embedded.Count; k++)
            if (!placed[k] && !OtherHostsWindow(embedded[k].TopLevel)) ordered.Add((embedded[k].Scene, embedded[k].X, embedded[k].Y));
        return ordered;
    }

    // Which of the driver's windows this host puts on screen, as {handle, screenX, screenY} triples.
    //
    // With a single host the answer is "all of them": GetPresentWindows returns every visible
    // window sorted with this form's subtree first, and menus, combo drop-downs and tooltips -- all
    // top-level windows of their own, in no form's subtree -- come along for free.
    //
    // That is wrong the moment a dialog opens a second window: each host would draw the other's
    // content, positioned against its own origin. So each host takes its own subtree, and the
    // newest host additionally claims everything no other host owns, which is where the popups of
    // whichever window the user is working in live.
    /// <summary>Whether <paramref name="topLevel"/> is some form's window other than this host's
    /// own: another host's, or a form that is hidden or gone and so shows nothing at all.</summary>
    private bool OtherHostsWindow(IntPtr topLevel) => topLevel != IntPtr.Zero && topLevel != _formHandle;

    private long[] PresentWindows()
    {
        if (FormGone) return System.Array.Empty<long>();
        if (_getSubtree == null)
            return (long[])_getPresent.Invoke(_driver, new object[] { _formHandle });

        long[] mine = (long[])_getSubtree.Invoke(_driver, new object[] { _formHandle });
        if (!PresentationHost.IsTopHost(this)) return mine;

        // Everything another host owns, or that is a compositing container belonging to a WPF
        // element, is somebody else's to draw. For a plain WinForms app there are neither, so this
        // still comes out as "every window" -- the behaviour that path has always had.
        var claimed = new System.Collections.Generic.HashSet<long>();
        AddSubtree(claimed, mine);
        foreach (Form other in PresentationHost.OtherHostedForms(this)) ClaimForm(claimed, other);
        foreach (Form container in PresentationHost.SuppressedForms()) ClaimForm(claimed, container);
        // ...and everything a WPF element composites. Those windows belong to a hosted pad or panel
        // and are positioned by that element, not by the driver, so drawing them here put the
        // workbench's pads inside an unrelated dialog.
        foreach (IntPtr composited in PresentationHost.CompositedWindows())
            AddSubtree(claimed, (long[])_getSubtree.Invoke(_driver, new object[] { composited }));

        long[] all = (long[])_getPresent.Invoke(_driver, new object[] { _formHandle });
        var outl = new System.Collections.Generic.List<long>(mine.Length + 12);
        outl.AddRange(mine);
        for (int i = 0; i + 2 < all.Length; i += 3)
        {
            if (claimed.Contains(all[i])) continue;
            // ...and only if it is actually a popup. "Unclaimed" also describes a hosted pad whose
            // WPF element has not built its HwndHost yet: SharpDevelop's Tools sidebar sits at the
            // driver's origin and so was drawn into the top-left corner of every dialog this host
            // put on screen, including the unhandled-exception box.
            if (_isPopup != null && !(bool)_isPopup.Invoke(_driver, new object[] { (IntPtr)all[i] }))
                continue;
            outl.Add(all[i]); outl.Add(all[i + 1]); outl.Add(all[i + 2]);
        }

        if (s_tracePresent) TracePresent(mine, outl);
        return outl.ToArray();
    }

    private static void AddSubtree(System.Collections.Generic.HashSet<long> into, long[] triples)
    {
        for (int i = 0; i + 2 < triples.Length; i += 3) into.Add(triples[i]);
    }

    private void ClaimForm(System.Collections.Generic.HashSet<long> into, Form form)
    {
        if (form == null || form.IsDisposed || !form.IsHandleCreated) return;
        AddSubtree(into, (long[])_getSubtree.Invoke(_driver, new object[] { form.Handle }));
    }

    // WF_TRACE_WINDOWS=1 prints, once per host, what this window actually composites: the windows
    // of its own form, then anything unclaimed it picked up. Enough to tell "the scene is empty"
    // from "something else was drawn over it".
    private static readonly bool s_tracePresent = Environment.GetEnvironmentVariable("WF_TRACE_WINDOWS") == "1";
    private bool _tracedPresent;

    private void TracePresent(long[] mine, System.Collections.Generic.List<long> all)
    {
        if (_tracedPresent) return;
        _tracedPresent = true;
        Console.Error.WriteLine($"present[{_form.Text}] form=0x{_formHandle.ToInt64():x} " +
                                $"{_form.Width}x{_form.Height}: own={mine.Length / 3} total={all.Count / 3}");
        for (int i = 0; i + 2 < all.Count; i += 3)
        {
            object scene = _getScene.Invoke(_driver, new object[] { (IntPtr)all[i] });
            Console.Error.WriteLine($"   win 0x{all[i]:x} at ({all[i + 1]},{all[i + 2]}) " +
                                    $"{(i < mine.Length ? "own" : "extra")} scene={(scene == null ? "null" : "ok")}");
        }
    }

    /// <summary>The rubber bands the driver is holding, in this window's client space.</summary>
    private System.Collections.Generic.List<(Rectangle Rect, int Width)> RubberBands(int ox, int oy)
    {
        if (_getRubberBands == null) return null;
        long[] raw = (long[])_getRubberBands.Invoke(_driver, null);
        if (raw == null || raw.Length == 0) return null;
        var bands = new System.Collections.Generic.List<(Rectangle, int)>(raw.Length / 5);
        for (int i = 0; i + 4 < raw.Length; i += 5)
            bands.Add((new Rectangle((int)raw[i] - ox, (int)raw[i + 1] - oy, (int)raw[i + 2], (int)raw[i + 3]),
                       (int)raw[i + 4]));
        return bands;
    }

    // The driver's blink clock (restarted when the caret is created, moved or shown, as user32's
    // is); the host's own stopwatch only if the driver has none.
    private MethodInfo _caretBlink;
    private long BlinkMs()
    {
        _caretBlink ??= _driver.GetType().GetMethod("GetCaretBlinkElapsed", BindingFlags.NonPublic | BindingFlags.Instance);
        return _caretBlink != null ? (long)_caretBlink.Invoke(_driver, null) : _blink.ElapsedMilliseconds;
    }

    private Rectangle? GetCaretRect(int ox, int oy)
    {
        if (_getCaret == null) return null;
        object[] a = { 0, 0, 0, 0 };
        if (!(bool)_getCaret.Invoke(_driver, a)) return null;
        if ((BlinkMs() / 530) % 2 != 0) return null;
        return new Rectangle((int)a[0] - ox, (int)a[1] - oy, Math.Max(1, (int)a[2]), (int)a[3]);
    }
    private bool CaretOn()
    {
        if (_getCaret == null) return false;
        object[] a = { 0, 0, 0, 0 };
        if (!(bool)_getCaret.Invoke(_driver, a)) return false;
        return (BlinkMs() / 530) % 2 == 0;
    }

    // ---- Win32 interop ----------------------------------------------------------

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct WNDCLASSEXW
    {
        public uint cbSize, style; public IntPtr lpfnWndProc; public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground, lpszMenuName, lpszClassName, hIconSm;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int ptX, ptY; }

    [DllImport("kernel32", SetLastError = true)] private static extern IntPtr GetModuleHandleW(string lpModuleName);
    /// <summary>The window property naming the form a host window shows (see CreateWindow).</summary>
    internal const string StackHandleProperty = "WinFormsWebGpu.Handle";
    [DllImport("user32", CharSet = CharSet.Unicode)] private static extern bool SetPropW(IntPtr h, string name, IntPtr value);
    [DllImport("user32", SetLastError = true)] private static extern IntPtr LoadCursorW(IntPtr h, int id);
    [DllImport("user32")] private static extern IntPtr SetCursor(IntPtr cursor);
    [DllImport("user32")] private static extern IntPtr SetTimer(IntPtr h, int id, uint ms, IntPtr proc);
    [DllImport("user32")] private static extern bool KillTimer(IntPtr h, int id);
    [DllImport("user32", SetLastError = true)] private static extern ushort RegisterClassExW(ref WNDCLASSEXW c);
    [DllImport("user32")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32", SetLastError = true)] private static extern IntPtr CreateWindowExW(uint ex, IntPtr cls, IntPtr name, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32")] private static extern IntPtr DefWindowProcW(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32")] private static extern bool DestroyWindow(IntPtr h);
    [DllImport("user32")] private static extern void PostQuitMessage(int code);
    [DllImport("user32")] private static extern bool ValidateRect(IntPtr hwnd, IntPtr rect);
    [DllImport("user32")] private static extern bool PeekMessageW(out MSG m, IntPtr h, uint min, uint max, uint remove);
    [DllImport("user32")] private static extern bool TranslateMessage(ref MSG m);
    [DllImport("user32")] private static extern IntPtr DispatchMessageW(ref MSG m);
    [DllImport("user32")] private static extern uint GetDpiForWindow(IntPtr h);
    [DllImport("user32")] private static extern IntPtr SetProcessDpiAwarenessContext(IntPtr ctx);
    [DllImport("user32")] private static extern short GetKeyState(int vk);
    [DllImport("user32")] private static extern IntPtr SetCapture(IntPtr hwnd);
    [DllImport("user32")] private static extern bool ReleaseCapture();
    [DllImport("user32")] private static extern bool ScreenToClient(IntPtr hwnd, ref POINT pt);
    [DllImport("user32")] private static extern bool ClientToScreen(IntPtr hwnd, ref POINT pt);
    private struct POINT { public int x, y; }
    [DllImport("user32")] private static extern IntPtr SetFocus(IntPtr hWnd);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int left, top, right, bottom; }

    [DllImport("user32")] private static extern bool GetClientRect(IntPtr hWnd, out RECT r);
    [DllImport("user32")] private static extern bool AdjustWindowRectEx(ref RECT r, int style, bool menu, int exStyle);
    [DllImport("user32")] private static extern bool AdjustWindowRectExForDpi(ref RECT r, int style, bool menu, int exStyle, uint dpi);
    [DllImport("user32")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32")] private static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
    [DllImport("user32")] private static extern bool GetMonitorInfoW(IntPtr mon, ref MONITORINFO mi);
    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }
    [DllImport("user32")] private static extern bool ShowWindow(IntPtr hWnd, int cmd);
    [StructLayout(LayoutKind.Sequential)] private struct TRACKMOUSEEVENT { public uint cbSize, dwFlags; public IntPtr hwndTrack; public uint dwHoverTime; }
    [DllImport("user32")] private static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT e);
    [DllImport("user32")] private static extern bool GetCursorPos(out POINT p);
    [DllImport("user32")] private static extern IntPtr WindowFromPoint(POINT p);
    private readonly MethodInfo _leaveAll;
    private bool _trackingLeave;
    [DllImport("user32")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtrW(IntPtr hWnd, int index);
    [DllImport("user32", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtrW(IntPtr hWnd, int index, IntPtr value);
    [DllImport("user32")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hdc);
}
