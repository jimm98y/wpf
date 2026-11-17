// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Notification-area icons on Windows: the shell's own (Shell_NotifyIconW), as WinForms' NotifyIcon has them. The
// driver's SystrayAdd/Change/Remove were stubs here -- no icon showed -- and a balloon tip (NIF_INFO, which Windows 10
// shows as a toast) was never asked for. The shell tells a hidden message-only window of clicks and of the balloon's
// fate in the icon's callback message; they go on to the NotifyIcon's window as WM_USER with the mouse or NIN_*
// message in lParam, which is how NotifyIcon reads them.
//

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

internal sealed unsafe partial class Win32Host
{
    private const uint WM_TRAYCALLBACK = 0x8000 + 0x7C1;   // WM_APP + n: the shell's message to the tray window
    private const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2;
    private const uint NIF_MESSAGE = 0x1, NIF_ICON = 0x2, NIF_TIP = 0x4, NIF_INFO = 0x10, NIF_SHOWTIP = 0x80;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID, uFlags, uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState, dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32", CharSet = CharSet.Unicode)] private static extern bool Shell_NotifyIconW(uint message, ref NOTIFYICONDATAW data);
    [DllImport("user32", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessageW(string name);
    [DllImport("user32")] private static extern IntPtr CreateIconIndirect(ref ICONINFO info);
    [DllImport("user32")] private static extern bool DestroyIcon(IntPtr icon);
    [DllImport("gdi32")] private static extern IntPtr CreateBitmap(int w, int h, uint planes, uint bpp, byte[] bits);

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO { public int fIcon; public int xHotspot, yHotspot; public IntPtr hbmMask, hbmColor; }

    private sealed class TrayIcon
    {
        public uint Id;
        public IntPtr Window;     // the NotifyIcon's window (the driver's handle)
        public IntPtr HIcon;
        public string Tip = "";
    }

    private static readonly Dictionary<IntPtr, TrayIcon> s_trayIcons = new();
    private static readonly WndProcDelegate s_trayWndProc = TrayWindowProc;   // rooted for the process
    private static IntPtr s_trayHwnd, s_trayClass;
    private static readonly bool s_traceTray = Environment.GetEnvironmentVariable("WF_TRACE_TRAY") == "1";
    private static uint s_nextTrayId, s_taskbarCreated;

    /// <summary>Called as the host starts: the driver's notification-area calls are the shell's.</summary>
    internal static void InstallTray()
    {
        System.Windows.Forms.XplatUIWebGpu.SystrayAdded ??= (h, tip, icon) => TrayShow(h, tip, icon, NIM_ADD);
        System.Windows.Forms.XplatUIWebGpu.SystrayChanged ??= (h, tip, icon) => TrayShow(h, tip, icon, NIM_MODIFY);
        System.Windows.Forms.XplatUIWebGpu.SystrayRemoved ??= TrayDelete;
        System.Windows.Forms.XplatUIWebGpu.SystrayBalloonShown ??= TrayBalloon;
    }

    /// <summary>The hidden window the shell sends the icons' messages to (message-only: no taskbar, never shown).</summary>
    private static IntPtr TrayHwnd()
    {
        if (s_trayHwnd != IntPtr.Zero) return s_trayHwnd;
        IntPtr hinstance = GetModuleHandleW(null);
        if (s_trayClass == IntPtr.Zero)
        {
            s_trayClass = Marshal.StringToHGlobalUni("WinFormsWebGpuTray");
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)sizeof(WNDCLASSEXW),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(s_trayWndProc),
                hInstance = hinstance,
                lpszClassName = s_trayClass,
            };
            if (RegisterClassExW(ref wc) == 0) return IntPtr.Zero;
            // Explorer restarted: every icon is to be added again
            s_taskbarCreated = RegisterWindowMessageW("TaskbarCreated");
        }
        // a top-level that is never shown, not HWND_MESSAGE: only top-levels hear the TaskbarCreated broadcast
        s_trayHwnd = CreateWindowExW(0, s_trayClass, IntPtr.Zero, 0, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, hinstance, IntPtr.Zero);
        return s_trayHwnd;
    }

    private static NOTIFYICONDATAW TrayData(TrayIcon icon, uint flags) => new NOTIFYICONDATAW
    {
        cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
        hWnd = TrayHwnd(),
        uID = icon.Id,
        uFlags = flags,
        uCallbackMessage = WM_TRAYCALLBACK,
        hIcon = icon.HIcon,
        szTip = icon.Tip.Length > 127 ? icon.Tip.Substring(0, 127) : icon.Tip,
        szInfo = "",
        szInfoTitle = "",
    };

    private static bool TrayShow(IntPtr window, string tip, Icon icon, uint message)
    {
        if (TrayHwnd() == IntPtr.Zero) return false;
        bool known = s_trayIcons.TryGetValue(window, out TrayIcon item);
        if (!known)
        {
            item = new TrayIcon { Id = ++s_nextTrayId, Window = window };
            s_trayIcons[window] = item;
            message = NIM_ADD;
        }
        IntPtr old = item.HIcon;
        item.HIcon = icon != null ? IconHandle(icon) : IntPtr.Zero;
        item.Tip = tip ?? "";
        NOTIFYICONDATAW data = TrayData(item, NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP);
        bool ok = Shell_NotifyIconW(message, ref data);
        if (s_traceTray) Console.WriteLine($"tray: {(message == NIM_ADD ? "add" : "modify")} {item.Id} hwnd=0x{data.hWnd:x} icon=0x{item.HIcon:x} ok={ok} err={Marshal.GetLastWin32Error()} thread={Environment.CurrentManagedThreadId}");
        if (old != IntPtr.Zero) DestroyIcon(old);
        return ok;
    }

    private static void TrayDelete(IntPtr window)
    {
        if (!s_trayIcons.Remove(window, out TrayIcon item)) return;
        NOTIFYICONDATAW data = TrayData(item, 0);
        Shell_NotifyIconW(NIM_DELETE, ref data);
        if (item.HIcon != IntPtr.Zero) DestroyIcon(item.HIcon);
    }

    /// <summary>A balloon tip: NIF_INFO on the icon (a toast on Windows 10), its title, text and icon kind.</summary>
    private static void TrayBalloon(IntPtr window, int timeout, string title, string text, ToolTipIcon icon)
    {
        if (!s_trayIcons.TryGetValue(window, out TrayIcon item)) { if (s_traceTray) Console.WriteLine($"tray: balloon for unknown 0x{window:x}"); return; }
        NOTIFYICONDATAW data = TrayData(item, NIF_INFO);
        data.szInfo = (text ?? "").Length > 255 ? text.Substring(0, 255) : text ?? "";
        data.szInfoTitle = (title ?? "").Length > 63 ? title.Substring(0, 63) : title ?? "";
        data.uTimeoutOrVersion = (uint)Math.Max(0, timeout);
        data.dwInfoFlags = icon switch { ToolTipIcon.Info => 1u, ToolTipIcon.Warning => 2u, ToolTipIcon.Error => 3u, _ => 0u };
        bool ok = Shell_NotifyIconW(NIM_MODIFY, ref data);
        if (s_traceTray) Console.WriteLine($"tray: balloon on {item.Id} ok={ok}");
    }

    private static IntPtr TrayWindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_TRAYCALLBACK)
        {
            if (s_traceTray) Console.WriteLine($"tray: callback id={wParam} lParam=0x{lParam.ToInt64():x}");
            // version 0: wParam the icon's id, lParam the mouse message or NIN_*; told to the icon's window as the
            // shell's callback is told to a NotifyIcon's (WM_USER, the message in lParam)
            uint id = (uint)wParam.ToInt64();
            foreach (TrayIcon item in s_trayIcons.Values)
                if (item.Id == id)
                {
                    System.Windows.Forms.XplatUI.PostMessage(item.Window, (System.Windows.Forms.Msg)0x0400, IntPtr.Zero, (IntPtr)(lParam.ToInt64() & 0xFFFF));
                    break;
                }
            return IntPtr.Zero;
        }
        if (msg == s_taskbarCreated && s_taskbarCreated != 0)
        {
            foreach (TrayIcon item in s_trayIcons.Values)
            {
                NOTIFYICONDATAW data = TrayData(item, NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP);
                Shell_NotifyIconW(NIM_ADD, ref data);
            }
            return IntPtr.Zero;
        }
        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    /// <summary>A real HICON of the icon's picture: a 32-bit colour bitmap with its alpha, and an empty mask.</summary>
    private static IntPtr IconHandle(Icon icon)
    {
        using Bitmap bmp = icon.ToBitmap();
        int w = bmp.Width, h = bmp.Height;
        var bgra = new byte[w * h * 4];
        BitmapData d = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (int y = 0; y < h; y++) Marshal.Copy(d.Scan0 + y * d.Stride, bgra, y * w * 4, w * 4);
        }
        finally { bmp.UnlockBits(d); }
        IntPtr color = CreateBitmap(w, h, 1, 32, bgra);
        IntPtr mask = CreateBitmap(w, h, 1, 1, new byte[((w + 15) / 16 * 2) * h]);
        var info = new ICONINFO { fIcon = 1, hbmColor = color, hbmMask = mask };
        IntPtr hicon = CreateIconIndirect(ref info);
        DeleteObject(color);
        DeleteObject(mask);
        return hicon;
    }
}
