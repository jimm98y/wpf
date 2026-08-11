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
    private readonly MethodInfo _injectClick, _down, _up, _move, _char, _keyDown, _getPresent, _getScene, _getVersion, _getCaret;
    private readonly bool _gpuRaster = Environment.GetEnvironmentVariable("WF_GPU_RASTER") == "1";
    private readonly Stopwatch _blink = Stopwatch.StartNew();
    private readonly WndProcDelegate _wndProc;  // rooted for the window's lifetime
    private IntPtr _hwnd, _hinstance, _classNamePtr;
    private WgpuPresenter _wgpu;
    private float _scale = 1f;
    private bool _quit, _savedGpu;
    private int _lastVer = -1;
    private bool _lastCaretOn, _lastPresentOk;

    internal Win32Host(Form form)
    {
        _form = form;
        _wndProc = WindowProc;
        var xplat = typeof(Control).Assembly.GetType("System.Windows.Forms.XplatUI");
        _driver = xplat.GetField("driver", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
        Type dt = _driver.GetType();
        MethodInfo M(string n) => dt.GetMethod(n, BindingFlags.NonPublic | BindingFlags.Instance);
        _injectClick = M("InjectClick"); _down = M("InjectMouseDown"); _up = M("InjectMouseUp");
        _move = M("InjectMouseMove"); _char = M("InjectChar"); _keyDown = M("InjectKeyDown");
        _getPresent = M("GetPresentWindows"); _getScene = M("GetWindowScene");
        _getVersion = M("GetPaintVersion"); _getCaret = M("GetCaret");
    }

    public void Show()
    {
        SetProcessDpiAwarenessContext((IntPtr)(-4)); // PER_MONITOR_AWARE_V2 -> real DPI, crisp text
        _hinstance = GetModuleHandleW(null);
        _classNamePtr = Marshal.StringToHGlobalUni("WinFormsWebGpuHost");
        var wc = new WNDCLASSEXW
        {
            cbSize = (uint)sizeof(WNDCLASSEXW),
            style = 0x0003,  // CS_HREDRAW | CS_VREDRAW
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = _hinstance,
            hCursor = LoadCursorW(IntPtr.Zero, 32512),  // IDC_ARROW
            lpszClassName = _classNamePtr,
        };
        if (RegisterClassExW(ref wc) == 0)
            throw new InvalidOperationException($"RegisterClassExW failed (0x{Marshal.GetLastWin32Error():x})");

        IntPtr title = Marshal.StringToHGlobalUni(_form.Text ?? "WinForms");
        try
        {
            _hwnd = CreateWindowExW(0, _classNamePtr, title, 0x00CF0000 | 0x10000000, // WS_OVERLAPPEDWINDOW|WS_VISIBLE
                100, 100, _form.Width, _form.Height, IntPtr.Zero, IntPtr.Zero, _hinstance, IntPtr.Zero);
        }
        finally { Marshal.FreeHGlobal(title); }
        if (_hwnd == IntPtr.Zero)
            throw new InvalidOperationException($"CreateWindowExW failed (0x{Marshal.GetLastWin32Error():x})");

        if (_gpuRaster)
        {
            var ctx = Microsoft.Wpf.Interop.WebGpu.Composition.WgpuContext.Create();
            IntPtr surface = Microsoft.Wpf.Interop.WebGpu.Composition.Platform.NativePlatform.CreateWindowSurface(ctx.Instance, _hwnd);
            if (surface == IntPtr.Zero) throw new InvalidOperationException("NativePlatform.CreateWindowSurface returned null");
            _scale = GetDpiForWindow(_hwnd) / 96f;   // DPI scale: render at device pixels (crisp)
            _wgpu = new WgpuPresenter(ctx, surface, _form.Width, _form.Height, _scale, srgb: true);
            Console.WriteLine($"WebGPU present path active (HWND 0x{_hwnd:x}, format {_wgpu.Format}, scale {_scale})");
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
            case 0x0201: SetFocus(hwnd); MouseAt(lParam, _down); Frame(); return IntPtr.Zero;   // WM_LBUTTONDOWN
            case 0x0202: MouseAt(lParam, _up); Frame(); return IntPtr.Zero;     // WM_LBUTTONUP
            case 0x0200: MouseMove(lParam); Frame(); return IntPtr.Zero;        // WM_MOUSEMOVE
            case 0x0102: _char.Invoke(_driver, new object[] { (char)(int)wParam }); Frame(); return IntPtr.Zero; // WM_CHAR
            case 0x0100:                                                          // WM_KEYDOWN
                int vk = (int)wParam;
                // Win32 VK == WinForms Keys for nav keys: PageUp33 PageDn34 End35 Home36 Left37 Up38
                // Right39 Down40 Delete46.
                if (vk is 33 or 34 or 35 or 36 or 37 or 38 or 39 or 40 or 46)
                { _keyDown.Invoke(_driver, new object[] { vk }); Frame(); }
                return IntPtr.Zero;
            case 0x0010: DestroyWindow(hwnd); return IntPtr.Zero;                // WM_CLOSE
            case 0x0002: PostQuitMessage(0); return IntPtr.Zero;                 // WM_DESTROY
            default: return DefWindowProcW(hwnd, msg, wParam, lParam);
        }
    }

    private void Frame() { Application.DoEvents(); Present(); }

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
        long[] wins = (long[])_getPresent.Invoke(_driver, new object[] { _form.Handle });
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
}
