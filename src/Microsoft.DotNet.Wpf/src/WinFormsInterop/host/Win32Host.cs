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

internal sealed unsafe class Win32Host : IWinFormsHost, WinFormsWebGpu.Accessibility.IA11yHostSite
{
    private readonly Form _form;

    // The form's driver window, taken once. Control.Handle CREATES the handle if it has gone, and
    // on a disposed control that throws ObjectDisposedException -- which is exactly the state a
    // dialog is in between its OK button disposing it and Windows delivering the paint that
    // follows. Asking the live control every frame therefore took the process down every time a
    // dialog was closed with a button rather than the window close box.
    private IntPtr _formHandle;

    /// <summary>Whether there is still a form behind this window. A disposed one has no pixels to
    /// present and must not be touched.</summary>
    private bool FormGone => _form == null || _form.IsDisposed;
    private readonly object _driver;
    private readonly MethodInfo _injectClick, _down, _up, _move, _char, _keyDown, _getPresent, _getScene, _getVersion, _getCaret, _getSubtree, _keyUp, _setModifiers, _wheel, _tickTimers, _sysKeyDown, _sysChar;
    private readonly MethodInfo _isPopup;
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
        _wheel = M("InjectWheel"); _tickTimers = M("TickTimers");
        _sysKeyDown = M("InjectSysKeyDown"); _sysChar = M("InjectSysChar");
        _getVersion = M("GetPaintVersion"); _getCaret = M("GetCaret");
        _getSubtree = M("GetSubtreeWindows"); _isPopup = M("IsPopupWindow");
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
            hCursor = LoadCursorW(IntPtr.Zero, 32512),  // IDC_ARROW
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
        switch (_form.StartPosition)
        {
            case FormStartPosition.Manual:
                return (_form.Left, _form.Top);

            case FormStartPosition.CenterScreen:
            case FormStartPosition.CenterParent:
            {
                int sw = GetSystemMetrics(SM_CXSCREEN), sh = GetSystemMetrics(SM_CYSCREEN);
                if (sw <= 0 || sh <= 0) return (CW_USEDEFAULT, CW_USEDEFAULT);
                return (Math.Max(0, (sw - _form.Width) / 2), Math.Max(0, (sh - _form.Height) / 2));
            }

            default:
                return (CW_USEDEFAULT, CW_USEDEFAULT);
        }
    }

    public void Show()
    {
        SetProcessDpiAwarenessContext((IntPtr)(-4)); // PER_MONITOR_AWARE_V2 -> real DPI, crisp text
        _hinstance = GetModuleHandleW(null);
        EnsureWindowClass(_hinstance);

        IntPtr title = Marshal.StringToHGlobalUni(_form.Text ?? "WinForms");
        try
        {
            // Claim the window being created, so the shared WndProc can route its very first
            // messages -- they arrive from inside CreateWindowExW, before it has returned a handle.
            s_creating = this;
            (uint style, uint exStyle) = WindowStyles();
            (int wx, int wy) = StartLocation();
            _hwnd = CreateWindowExW(exStyle, s_classNamePtr, title, style,
                wx, wy, _form.Width, _form.Height, IntPtr.Zero, IntPtr.Zero, _hinstance, IntPtr.Zero);
            if (_hwnd != IntPtr.Zero) s_byHwnd[_hwnd] = this;
        }
        finally { s_creating = null; Marshal.FreeHGlobal(title); }
        if (_hwnd == IntPtr.Zero)
            throw new InvalidOperationException($"CreateWindowExW failed (0x{Marshal.GetLastWin32Error():x})");

        _scale = GetDpiForWindow(_hwnd) / 96f;   // DPI scale: render at device pixels (crisp)

        // CreateWindowEx sizes the WHOLE window, caption and borders included, but the surface we
        // present is the form-sized CLIENT area. Left as it was, the client area came out smaller than
        // the form (864x521 for an 880x560 form) and the compositor stretched the frame down into it:
        // everything drew ~7% short vertically, and because ClientDip assumes a 1:1 client the hit-test
        // point drifted further off the further down the window you clicked -- a click landed on the
        // control ABOVE the one under the cursor. Grow the window so the CLIENT is exactly form-sized.
        ResizeClientTo(_form.Width, _form.Height);

        if (_gpuRaster)
        {
            var ctx = Microsoft.Wpf.Interop.WebGpu.Composition.WgpuContext.Create();
            IntPtr surface = Microsoft.Wpf.Interop.WebGpu.Composition.Platform.NativePlatform.CreateWindowSurface(ctx.Instance, _hwnd);
            if (surface == IntPtr.Zero) throw new InvalidOperationException("NativePlatform.CreateWindowSurface returned null");
            _wgpu = new WgpuPresenter(ctx, surface, _form.Width, _form.Height, _scale, srgb: true);
            Console.WriteLine($"WebGPU present path active (HWND 0x{_hwnd:x}, format {_wgpu.Format}, scale {_scale}, client {ClientSize()})");
        }
        // Let embedded non-WinForms content (an ElementHost's WPF tree) reach the real window and its
        // scale now that both exist. Inert when nothing is embedded.
        EmbeddedScenes.PublishHostWindow(_hwnd, _scale);
        Present();
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

        uint style = WS_VISIBLE, exStyle = 0;
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
        return (style, exStyle);
    }

    public void Present()
    {
        if (_wgpu == null) return;
        // The driver's paint version covers the WinForms controls; embedded content (a hosted WPF
        // tree) changes on its own clock, so fold its version in or a WPF-only animation never
        // reaches the screen.
        int ver = (int)_getVersion.Invoke(_driver, null) + EmbeddedScenes.CurrentVersion();
        bool caretOn = CaretOn();
        string save = Environment.GetEnvironmentVariable("WF_WEBGPU_SAVE");
        bool wantSave = !string.IsNullOrEmpty(save) && !_savedGpu;
        if (ver == _lastVer && caretOn == _lastCaretOn && _lastPresentOk && !wantSave) return;

        var scenes = GetScenes(out int ox, out int oy);
        Rectangle? caret = GetCaretRect(ox, oy);
        _lastPresentOk = _wgpu.PresentScenes(scenes, caret, _form.Width, _form.Height);
        _lastVer = ver; _lastCaretOn = caretOn;
        if (wantSave)
        {
            _savedGpu = true;
            byte[] rgba = _wgpu.RenderScenesToRgba(scenes, caret);
            CocoaHost.SaveRgbaPng(rgba, _wgpu.DeviceWidth, _wgpu.DeviceHeight, save);
            Console.WriteLine($"saved GPU-rendered frame -> {save}");
        }
    }

    /// <summary>Take the window down because the form closed itself (an OK button rather than the
    /// window close box). Idempotent.</summary>
    public void Close()
    {
        if (_hwnd == IntPtr.Zero) return;
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
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }
        return !_quit;
    }

    // WndProc routes input to the driver (client pixels -> DIPs), then repaints + presents so
    // pressed/hover animation and caret updates show immediately (like the Cocoa pump).
    private IntPtr WindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
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
            case 0x0200: MouseMove(lParam); Frame(); return IntPtr.Zero;        // WM_MOUSEMOVE
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
            case 0x0005: OnClientResized(); return IntPtr.Zero;                  // WM_SIZE
            // WM_DPICHANGED: the window moved to a display with a different DPI. Nothing acted
            // on it, so _scale kept the DPI the window was created at while the client area
            // changed underneath it -- OnClientResized then read the new pixels through the old
            // scale, grew the form to match, and every control drew at half the size it should.
            case 0x02E0: OnDpiChanged(lParam); return IntPtr.Zero;
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
    private void ResizeClientTo(int logicalW, int logicalH)
    {
        var r = new RECT { left = 0, top = 0, right = (int)Math.Round(logicalW * _scale), bottom = (int)Math.Round(logicalH * _scale) };
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
    private void OnDpiChanged(IntPtr suggested)
    {
        if (_hwnd == IntPtr.Zero)
            return;
        _scale = GetDpiForWindow(_hwnd) / 96f;
        if (suggested != IntPtr.Zero)
        {
            RECT r = Marshal.PtrToStructure<RECT>(suggested);
            SetWindowPos(_hwnd, IntPtr.Zero, r.left, r.top, r.right - r.left, r.bottom - r.top,
                0x0004 | 0x0010);                        // SWP_NOZORDER | SWP_NOACTIVATE
        }
        _wgpu?.SetScale(_scale);
        EmbeddedScenes.PublishHostWindow(_hwnd, _scale);
        _lastVer = -1;                                   // force a present at the new scale
        OnClientResized();
        Present();
    }

    private void OnClientResized()
    {
        if (_wgpu == null) return;
        GetClientRect(_hwnd, out RECT r);
        int lw = (int)Math.Round((r.right - r.left) / _scale), lh = (int)Math.Round((r.bottom - r.top) / _scale);
        if (lw <= 0 || lh <= 0) return;                     // minimised
        if (lw == _form.Width && lh == _form.Height) return;
        _form.Width = lw;
        _form.Height = lh;
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

    private System.Collections.Generic.List<(object, int, int)> GetScenes(out int ox, out int oy)
    {
        long[] wins = PresentWindows();
        ox = wins.Length >= 3 ? (int)wins[1] : 0;
        oy = wins.Length >= 3 ? (int)wins[2] : 0;
        _ox = ox; _oy = oy;                 // input maps through the same origin; see ClientDip
        var list = new System.Collections.Generic.List<(object, int, int)>(wins.Length / 3);
        for (int i = 0; i + 2 < wins.Length; i += 3)
        {
            object scene = _getScene.Invoke(_driver, new object[] { (IntPtr)wins[i] });
            if (scene != null) list.Add((scene, (int)wins[i + 1] - ox, (int)wins[i + 2] - oy));
        }
        // Embedded non-WinForms content LAST, so it draws over the control whose area it occupies.
        var embedded = EmbeddedScenes.Get(ox, oy);
        if (embedded != null) list.AddRange(embedded);
        return list;
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

    private Rectangle? GetCaretRect(int ox, int oy)
    {
        if (_getCaret == null) return null;
        object[] a = { 0, 0, 0, 0 };
        if (!(bool)_getCaret.Invoke(_driver, a)) return null;
        if ((_blink.ElapsedMilliseconds / 530) % 2 != 0) return null;
        return new Rectangle((int)a[0] - ox, (int)a[1] - oy, Math.Max(1, (int)a[2]), (int)a[3]);
    }
    private bool CaretOn()
    {
        if (_getCaret == null) return false;
        object[] a = { 0, 0, 0, 0 };
        if (!(bool)_getCaret.Invoke(_driver, a)) return false;
        return (_blink.ElapsedMilliseconds / 530) % 2 == 0;
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
    [DllImport("user32", SetLastError = true)] private static extern IntPtr LoadCursorW(IntPtr h, int id);
    [DllImport("user32", SetLastError = true)] private static extern ushort RegisterClassExW(ref WNDCLASSEXW c);
    [DllImport("user32")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32", SetLastError = true)] private static extern IntPtr CreateWindowExW(uint ex, IntPtr cls, IntPtr name, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32")] private static extern IntPtr DefWindowProcW(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32")] private static extern bool DestroyWindow(IntPtr h);
    [DllImport("user32")] private static extern void PostQuitMessage(int code);
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
    [DllImport("user32", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtrW(IntPtr hWnd, int index);
}
