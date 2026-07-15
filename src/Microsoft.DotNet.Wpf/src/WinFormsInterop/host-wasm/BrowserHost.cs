// Browser windowing shell for the WinForms-on-WebGPU host: presents the SAME XplatUIWebGpu
// driver scenes (built by our Mono System.Windows.Forms + vendored System.Drawing) onto a
// <canvas> WebGPU surface, and feeds queued DOM input back into the driver. The present path
// (WgpuPresenter.PresentScenes) and the driver are identical to the mac/win hosts — only the
// surface (canvas) and the event source (DOM, not NSEvent/HWND) differ.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Reflection;
using System.Text.Json;
using System.Windows.Forms;
using Microsoft.Wpf.Interop.WebGpu.Composition;
using Microsoft.Wpf.Interop.WebGpu.Composition.Platform;

internal sealed class BrowserHost : IWinFormsHost
{
    private readonly Form _form;
    private readonly object _driver;
    private readonly MethodInfo _down, _up, _move, _char, _keyDown, _wheel, _getPresent, _getScene, _getVersion, _getCaret;
    private WgpuPresenter _wgpu;
    private readonly System.Diagnostics.Stopwatch _blink = System.Diagnostics.Stopwatch.StartNew();
    private int _lastVer = -1;
    private bool _lastCaretOn, _lastPresentOk, _leftDown;

    internal BrowserHost(Form form)
    {
        _form = form;
        Type dt = typeof(Control).Assembly.GetType("System.Windows.Forms.XplatUI")
            .GetField("driver", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null).GetType();
        _driver = typeof(Control).Assembly.GetType("System.Windows.Forms.XplatUI")
            .GetField("driver", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
        MethodInfo M(string n) => dt.GetMethod(n, BindingFlags.NonPublic | BindingFlags.Instance);
        _down = M("InjectMouseDown"); _up = M("InjectMouseUp"); _move = M("InjectMouseMove");
        _char = M("InjectChar"); _keyDown = M("InjectKeyDown"); _wheel = M("InjectWheel");
        _getPresent = M("GetPresentWindows"); _getScene = M("GetWindowScene");
        _getVersion = M("GetPaintVersion"); _getCaret = M("GetCaret");
    }

    // ---- present (identical scene path to CocoaHost) ---------------------------------

    public void Show()
    {
        int handle = WinFormsBrowserJs.CreateCanvas(_form.Width, _form.Height);
        double scale = WinFormsBrowserJs.Dpr();
        var ctx = WgpuContext.Create();
        IntPtr surface = NativePlatform.CreateWindowSurface(ctx.Instance, (IntPtr)handle);
        if (surface == IntPtr.Zero) throw new InvalidOperationException("CreateWindowSurface returned null");
        _wgpu = new WgpuPresenter(ctx, surface, _form.Width, _form.Height, scale, srgb: true);
        Console.WriteLine($"BrowserHost: canvas={handle} surface=0x{surface:x} scale={scale}");
        Present();
    }

    public void Present()
    {
        if (_wgpu == null) return;
        int ver = (int)_getVersion.Invoke(_driver, null);
        bool caretOn = CaretOn();
        if (ver == _lastVer && caretOn == _lastCaretOn && _lastPresentOk) return;   // present-on-change

        var scenes = GetScenes(out int ox, out int oy);
        Rectangle? caret = GetCaretRect(ox, oy);
        _lastPresentOk = _wgpu.PresentScenes(scenes, caret, _form.Width, _form.Height);
        _lastVer = ver; _lastCaretOn = caretOn;
    }

    private List<(object, int, int)> GetScenes(out int ox, out int oy)
    {
        long[] wins = (long[])_getPresent.Invoke(_driver, new object[] { _form.Handle });
        ox = wins.Length >= 3 ? (int)wins[1] : 0;
        oy = wins.Length >= 3 ? (int)wins[2] : 0;
        var list = new List<(object, int, int)>(wins.Length / 3);
        for (int i = 0; i + 2 < wins.Length; i += 3)
        {
            object scene = _getScene.Invoke(_driver, new object[] { (IntPtr)wins[i] });
            if (scene != null) list.Add((scene, (int)wins[i + 1] - ox, (int)wins[i + 2] - oy));
        }
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

    // ---- input: drain queued DOM events -> driver ------------------------------------

    public bool Pump()
    {
        string json = WinFormsBrowserJs.DrainEvents();
        if (string.IsNullOrEmpty(json)) return true;
        using JsonDocument doc = JsonDocument.Parse(json);
        foreach (JsonElement e in doc.RootElement.EnumerateArray())
        {
            string t = e.GetProperty("t").GetString();
            if (t == "m")
            {
                int x = e.GetProperty("x").GetInt32(), y = e.GetProperty("y").GetInt32();
                switch (e.GetProperty("k").GetInt32())
                {
                    case 0: _move.Invoke(_driver, new object[] { x, y, _leftDown }); break;
                    case 1: _leftDown = true; _down.Invoke(_driver, new object[] { x, y }); break;
                    case 2: _leftDown = false; _up.Invoke(_driver, new object[] { x, y }); break;
                }
            }
            else if (t == "w")
            {
                int x = e.GetProperty("x").GetInt32(), y = e.GetProperty("y").GetInt32();
                _wheel?.Invoke(_driver, new object[] { x, y, e.GetProperty("d").GetInt32() });
            }
            else if (t == "k" && e.GetProperty("d").GetInt32() == 1)
            {
                string key = e.GetProperty("key").GetString();
                if (key != null && key.Length == 1)                       // printable char
                    _char.Invoke(_driver, new object[] { key[0] });
                else
                {
                    int vk = VirtualKey(e.GetProperty("c").GetString());
                    if (vk != 0) _keyDown.Invoke(_driver, new object[] { vk });
                }
            }
        }
        return true;
    }

    // DOM KeyboardEvent.code -> Win32 virtual-key for the non-printable keys WinForms controls act on.
    private static int VirtualKey(string code) => code switch
    {
        "Backspace" => 0x08, "Tab" => 0x09, "Enter" => 0x0D, "Escape" => 0x1B,
        "Delete" => 0x2E, "ArrowLeft" => 0x25, "ArrowUp" => 0x26, "ArrowRight" => 0x27,
        "ArrowDown" => 0x28, "Home" => 0x24, "End" => 0x23, "Space" => 0x20,
        _ => 0,
    };

    public void InjectClickScreen(int x, int y)
    {
        _move.Invoke(_driver, new object[] { x, y, false });
        _down.Invoke(_driver, new object[] { x, y });
        _up.Invoke(_driver, new object[] { x, y });
    }

    public void SaveFrame(string path) { /* no screen readback on the browser present path */ }
}
