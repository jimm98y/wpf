// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// A popup that does not fit in its host window gets a real window of its own.
//
// The driver's popups that are not forms -- a ComboBox's drop-down list, a ToolStrip drop-down -- are
// drawn by the host into its own surface, at their place in the frame. On Windows they are top-level
// windows (ComboLBox, a menu window) that may extend past the window they dropped out of; drawn into
// the host they were cut off at its edge: app's zoom box in the status bar opened a list that
// the window's bottom edge cut in half.
//
// So a popup that reaches past the host's client area is presented by a borderless, owned,
// never-activated window of its own, exactly over where it is drawn, and the host stops drawing it.
// The mouse over that window is the host's: its messages are handed to the host's window procedure
// in the host's client coordinates (the driver works in one coordinate space, so a point past the
// host's edge still lands on the popup), and a press captures the host as any press does. A popup
// that fits is drawn by the host as before.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;

using System.Windows.Forms;

internal sealed unsafe partial class Win32Host
{
    private sealed class PopupWindow
    {
        public IntPtr Hwnd;
        public WgpuPresenter Presenter;
        public int DevX, DevY, DevW, DevH;      // host-client device pixels
        public bool Shown;
        public bool Used;
    }

    // driver handle of the popup's top-level window -> its real window
    private readonly Dictionary<IntPtr, PopupWindow> _popupWindows = new();
    private static readonly Dictionary<IntPtr, Win32Host> s_popupOwner = new();
    private static readonly WndProcDelegate s_popupWndProc = PopupWindowProc;
    private static IntPtr s_popupClass;

    private static readonly bool s_noPopupWindows = Environment.GetEnvironmentVariable("WF_POPUP_WINDOWS") == "0";

    /// <summary>For tests: the real windows popups are presented by, keyed by the popup's handle.</summary>
    internal IReadOnlyDictionary<IntPtr, IntPtr> PopupWindowHandles()
    {
        var d = new Dictionary<IntPtr, IntPtr>();
        foreach (var kv in _popupWindows) if (kv.Value.Shown) d[kv.Key] = kv.Value.Hwnd;
        return d;
    }

    /// <summary>Takes the scenes of every popup that reaches past this host's client area out of
    /// <paramref name="scenes"/> and presents them in windows of their own; hides the windows of the
    /// popups that no longer need one.</summary>
    private List<(object, int, int)> PresentOverflowingPopups(List<(object, int, int)> scenes, List<IntPtr> owners)
    {
        foreach (PopupWindow pw in _popupWindows.Values) pw.Used = false;
        List<(object, int, int)> keep = scenes;
        if (!s_noPopupWindows && owners != null && owners.Count == scenes.Count && _isPopup != null && !FormGone)
        {
            int cw = _form.ClientSize.Width, ch = _form.ClientSize.Height;
            HashSet<IntPtr> checkedTops = null;
            for (int i = 0; i < scenes.Count; i++)
            {
                IntPtr top = owners[i];
                if (top == IntPtr.Zero || top == _formHandle) continue;
                checkedTops ??= new HashSet<IntPtr>();
                if (!checkedTops.Add(top)) continue;
                Control c = Control.FromHandle(top);
                if (c == null || c is Form || !c.Visible) continue;
                if (!(bool)_isPopup.Invoke(_driver, new object[] { top })) continue;
                // Where the popup is, in this host's frame (form points from the frame's origin).
                int px = c.Left - _ox, py = c.Top - _oy;
                int pw0 = c.Width, ph0 = c.Height;
                if (pw0 <= 0 || ph0 <= 0) continue;
                bool fits = px >= 0 && py >= 0 && px + pw0 <= cw && py + ph0 <= ch;
                if (fits) continue;
                if (!PresentPopupWindow(top, px, py, pw0, ph0, scenes, owners)) continue;
                if (ReferenceEquals(keep, scenes)) keep = new List<(object, int, int)>(scenes);
            }
            if (!ReferenceEquals(keep, scenes))
            {
                // The host draws everything but the popups that are now windows of their own.
                var outl = new List<(object, int, int)>(scenes.Count);
                for (int i = 0; i < scenes.Count; i++)
                    if (!(_popupWindows.TryGetValue(owners[i], out PopupWindow pw) && pw.Used)) outl.Add(scenes[i]);
                keep = outl;
            }
        }
        foreach (PopupWindow pw in _popupWindows.Values)
            if (!pw.Used && pw.Shown)
            {
                pw.Shown = false;
                PopupShowWindow(pw.Hwnd, 0 /* SW_HIDE */);
            }
        return keep;
    }

    private bool PresentPopupWindow(IntPtr top, int x, int y, int w, int h, List<(object, int, int)> scenes, List<IntPtr> owners)
    {
        if (!_popupWindows.TryGetValue(top, out PopupWindow pw))
        {
            pw = CreatePopupWindow();
            if (pw == null) return false;
            _popupWindows[top] = pw;
        }
        pw.Used = true;
        int dx = (int)Math.Round(x * _scale), dy = (int)Math.Round(y * _scale);
        int dw = Math.Max(1, (int)Math.Round(w * _scale)), dh = Math.Max(1, (int)Math.Round(h * _scale));
        var origin = new POINT { x = 0, y = 0 };
        ClientToScreen(_hwnd, ref origin);
        if (!pw.Shown || dx != pw.DevX || dy != pw.DevY || dw != pw.DevW || dh != pw.DevH)
        {
            pw.DevX = dx; pw.DevY = dy; pw.DevW = dw; pw.DevH = dh;
            PopupSetWindowPos(pw.Hwnd, IntPtr.Zero, origin.x + dx, origin.y + dy, dw, dh, 0x0010 | 0x0004);   // SWP_NOACTIVATE | SWP_NOZORDER
        }
        var mine = new List<(object, int, int)>();
        for (int i = 0; i < scenes.Count; i++)
            if (owners[i] == top) mine.Add((scenes[i].Item1, scenes[i].Item2 - x, scenes[i].Item3 - y));
        pw.Presenter.PresentScenes(mine, null, w, h);
        if (!pw.Shown)
        {
            pw.Shown = true;
            PopupShowWindow(pw.Hwnd, 4 /* SW_SHOWNOACTIVATE */);
            // Above whatever else the application has up, as the popup it stands for is.
            PopupSetWindowPos(pw.Hwnd, IntPtr.Zero, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);   // HWND_TOP, NOSIZE|NOMOVE|NOACTIVATE
        }
        return true;
    }

    private PopupWindow CreatePopupWindow()
    {
        if (s_popupClass == IntPtr.Zero)
        {
            s_popupClass = Marshal.StringToHGlobalUni("WinFormsWebGpuPopup");
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)sizeof(WNDCLASSEXW),
                style = 0x0003,                // CS_HREDRAW | CS_VREDRAW (no drop shadow: the popup draws its own)
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(s_popupWndProc),
                hInstance = _hinstance,
                hCursor = IntPtr.Zero,
                lpszClassName = s_popupClass,
            };
            if (RegisterClassExW(ref wc) == 0) { Console.Error.WriteLine($"popup window class failed (0x{Marshal.GetLastWin32Error():x})"); return null; }
        }
        const uint WS_POPUP = 0x80000000;
        const uint WS_EX_TOOLWINDOW = 0x00000080, WS_EX_NOACTIVATE = 0x08000000;
        IntPtr hwnd = CreateWindowExW(WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE, s_popupClass, IntPtr.Zero, WS_POPUP,
            0, 0, 1, 1, _hwnd, IntPtr.Zero, _hinstance, IntPtr.Zero);
        if (hwnd == IntPtr.Zero) { Console.Error.WriteLine($"popup window failed (0x{Marshal.GetLastWin32Error():x})"); return null; }
        lock (s_popupOwner) s_popupOwner[hwnd] = this;
        try
        {
            var ctx = Microsoft.Wpf.Interop.WebGpu.Composition.ProcessGpu.Acquire();
            IntPtr surface = Microsoft.Wpf.Interop.WebGpu.Composition.Platform.NativePlatform.CreateWindowSurface(ctx.Instance, hwnd);
            var presenter = new WgpuPresenter(ctx, surface, 1, 1, _scale, srgb: true, nativeWindow: hwnd, threadDevice: true);
            return new PopupWindow { Hwnd = hwnd, Presenter = presenter };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("popup window surface failed: " + ex.Message);
            lock (s_popupOwner) s_popupOwner.Remove(hwnd);
            DestroyWindow(hwnd);
            return null;
        }
    }

    private void DestroyPopupWindows()
    {
        foreach (PopupWindow pw in _popupWindows.Values)
        {
            try { pw.Presenter?.Dispose(); } catch (Exception) { }
            lock (s_popupOwner) s_popupOwner.Remove(pw.Hwnd);
            DestroyWindow(pw.Hwnd);
        }
        _popupWindows.Clear();
    }

    /// <summary>Whether <paramref name="hwnd"/> is one of this host's popup windows.</summary>
    private bool IsMyPopupWindow(IntPtr hwnd)
    {
        foreach (PopupWindow pw in _popupWindows.Values) if (pw.Hwnd == hwnd && pw.Shown) return true;
        return false;
    }

    private static IntPtr PopupWindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        Win32Host host;
        lock (s_popupOwner) s_popupOwner.TryGetValue(hwnd, out host);
        if (host == null || host._hwnd == IntPtr.Zero) return DefWindowProcW(hwnd, msg, wParam, lParam);
        switch (msg)
        {
            case 0x0021: return (IntPtr)3;                    // WM_MOUSEACTIVATE: MA_NOACTIVATE -- a popup never takes activation
            case 0x0084: return (IntPtr)1;                    // WM_NCHITTEST: all client
            case 0x020A:                                      // WM_MOUSEWHEEL: screen coordinates already
            case 0x020E:
            case 0x0020:                                      // WM_SETCURSOR
                return host.WindowProcCore(host._hwnd, msg, msg == 0x0020 ? host._hwnd : wParam, lParam);
        }
        if (msg >= 0x0200 && msg <= 0x020D)
        {
            // The popup window's client point, as a point of the host's client area.
            var p = new POINT { x = (short)((long)lParam & 0xFFFF), y = (short)(((long)lParam >> 16) & 0xFFFF) };
            ClientToScreen(hwnd, ref p);
            ScreenToClient(host._hwnd, ref p);
            IntPtr lp = (IntPtr)(((p.y & 0xFFFF) << 16) | (p.x & 0xFFFF));
            return host.WindowProcCore(host._hwnd, msg, wParam, lp);
        }
        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    [DllImport("user32", EntryPoint = "ShowWindow")] private static extern bool PopupShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32", EntryPoint = "SetWindowPos")] private static extern bool PopupSetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
}
