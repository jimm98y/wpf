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

internal sealed unsafe class Win32Host : IWinFormsHost
{
    private readonly Form _form;
    private readonly object _driver;
    private readonly MethodInfo _injectClick, _down, _up, _move, _char, _keyDown, _getPresent, _getScene, _getVersion, _getCaret, _getSubtree, _keyUp, _setModifiers;
    // On unless switched off; see XplatUIWebGpu.s_gpuRaster for why it cannot be opt-in.
    private readonly bool _gpuRaster = Environment.GetEnvironmentVariable("WF_GPU_RASTER") != "0"
        && Environment.GetEnvironmentVariable("WF_WEBGPU") != "0";
    private readonly Stopwatch _blink = Stopwatch.StartNew();
    private IntPtr _hwnd, _hinstance;
    private WgpuPresenter _wgpu;
    private float _scale = 1f;
    private bool _quit, _savedGpu;
    private int _lastVer = -1;
    private bool _lastCaretOn, _lastPresentOk;

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
        _getVersion = M("GetPaintVersion"); _getCaret = M("GetCaret");
        _getSubtree = M("GetSubtreeWindows");
        // Register as the on-screen host for THIS form, so the driver's message loop drives this
        // window rather than creating a second one of its own.
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
            _hwnd = CreateWindowExW(0, s_classNamePtr, title, 0x00CF0000 | 0x10000000, // WS_OVERLAPPEDWINDOW|WS_VISIBLE
                100, 100, _form.Width, _form.Height, IntPtr.Zero, IntPtr.Zero, _hinstance, IntPtr.Zero);
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
            case 0x0201: Trace("WM_LBUTTONDOWN", lParam); SetFocus(hwnd); MouseAt(lParam, _down); Frame(); return IntPtr.Zero;   // WM_LBUTTONDOWN
            case 0x0202: MouseAt(lParam, _up); Frame(); return IntPtr.Zero;     // WM_LBUTTONUP
            case 0x0200: MouseMove(lParam); Frame(); return IntPtr.Zero;        // WM_MOUSEMOVE
            case 0x0102:                                                          // WM_CHAR
                char typed = (char)(int)wParam;
                // A control combination (Ctrl+C is 0x03) arrives here as a control code as well as
                // a key-down. The key-down is the one a control acts on; inserting the code too put
                // a stray character in the text box.
                if (typed < ' ' && (ModifierState() & Keys.Control) != 0) return IntPtr.Zero;
                _char.Invoke(_driver, new object[] { typed }); Frame(); return IntPtr.Zero;
            case 0x0100:                                                          // WM_KEYDOWN
                int vk = (int)wParam;
                PublishModifiers();
                // Win32 VK == WinForms Keys, so keys pass straight through. Backspace(8) Tab(9)
                // Return(13) and Escape(27) are the exception: the driver synthesises their WM_CHAR
                // from the key-down, and TranslateMessage sends one as well, so forwarding those
                // here would act on them twice.
                if (vk != 8 && vk != 9 && vk != 13 && vk != 27)
                { _keyDown.Invoke(_driver, new object[] { vk }); Frame(); }
                return IntPtr.Zero;
            case 0x0101:                                                          // WM_KEYUP
                PublishModifiers();
                _keyUp?.Invoke(_driver, new object[] { (int)wParam });
                return IntPtr.Zero;
            case 0x0005: OnClientResized(); return IntPtr.Zero;                  // WM_SIZE
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

    private void Frame() { Application.DoEvents(); Present(); }

    private static readonly bool s_trace = Environment.GetEnvironmentVariable("WF_TRACE_INPUT") == "1";
    private void Trace(string what, IntPtr lParam)
    {
        if (!s_trace) return;
        (int x, int y) = ClientDip(lParam);
        Console.WriteLine($"win32host: {what} client dip ({x},{y})");
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
        return ((int)(px / _scale), (int)(py / _scale));                    // -> DIPs
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
        if (_getSubtree == null)
            return (long[])_getPresent.Invoke(_driver, new object[] { _form.Handle });

        long[] mine = (long[])_getSubtree.Invoke(_driver, new object[] { _form.Handle });
        if (!PresentationHost.IsTopHost(this)) return mine;

        // Everything another host owns, or that is a compositing container belonging to a WPF
        // element, is somebody else's to draw. For a plain WinForms app there are neither, so this
        // still comes out as "every window" -- the behaviour that path has always had.
        var claimed = new System.Collections.Generic.HashSet<long>();
        AddSubtree(claimed, mine);
        foreach (Form other in PresentationHost.OtherHostedForms(this)) ClaimForm(claimed, other);
        foreach (Form container in PresentationHost.SuppressedForms()) ClaimForm(claimed, container);

        long[] all = (long[])_getPresent.Invoke(_driver, new object[] { _form.Handle });
        var outl = new System.Collections.Generic.List<long>(mine.Length + 12);
        outl.AddRange(mine);
        for (int i = 0; i + 2 < all.Length; i += 3)
            if (!claimed.Contains(all[i])) { outl.Add(all[i]); outl.Add(all[i + 1]); outl.Add(all[i + 2]); }

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
        Console.Error.WriteLine($"present[{_form.Text}] form=0x{_form.Handle.ToInt64():x} " +
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
    [DllImport("user32")] private static extern IntPtr SetFocus(IntPtr hWnd);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int left, top, right, bottom; }

    [DllImport("user32")] private static extern bool GetClientRect(IntPtr hWnd, out RECT r);
    [DllImport("user32")] private static extern bool AdjustWindowRectEx(ref RECT r, int style, bool menu, int exStyle);
    [DllImport("user32")] private static extern bool AdjustWindowRectExForDpi(ref RECT r, int style, bool menu, int exStyle, uint dpi);
    [DllImport("user32")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtrW(IntPtr hWnd, int index);
}
