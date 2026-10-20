// user32, the parts that describe the system rather than a window: metrics, parameters, monitors,
// DPI, the cursor and keyboard, registered messages, hooks, system colours.
//
// Coordinates are the stack's: macOS points, origin at the top-left of the main display, which is
// what a WPF window's Left/Top are here. DPI is 96: the host scales the whole surface for the display,
// so a Windows app sees the 100% case it was written for.

using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Forms = System.Windows.Forms;

namespace WpfMac.Win32;

[StructLayout(LayoutKind.Sequential)]
internal struct RECT { public int Left, Top, Right, Bottom; }

[StructLayout(LayoutKind.Sequential)]
internal struct POINT { public int X, Y; }

public static unsafe partial class Exports
{
    private static void RegisterUser32System()
    {
        R((delegate* unmanaged<int, int>)&GetSystemMetrics, "GetSystemMetrics");
        R((delegate* unmanaged<int, uint, int>)&GetSystemMetricsForDpi, "GetSystemMetricsForDpi");
        R((delegate* unmanaged<uint, uint, void*, uint, int>)&SystemParametersInfoW, "SystemParametersInfoW", "SystemParametersInfo");
        R((delegate* unmanaged<uint, uint, void*, uint, uint, int>)&SystemParametersInfoForDpi, "SystemParametersInfoForDpi");
        R((delegate* unmanaged<uint>)&GetDoubleClickTime, "GetDoubleClickTime");
        R((delegate* unmanaged<uint>)&GetCaretBlinkTime, "GetCaretBlinkTime");
        R((delegate* unmanaged<int, uint>)&GetSysColor, "GetSysColor");
        R((delegate* unmanaged<int, nint>)&GetSysColorBrush, "GetSysColorBrush");

        R((delegate* unmanaged<nint, uint, nint>)&MonitorFromWindow, "MonitorFromWindow");
        R((delegate* unmanaged<POINT, uint, nint>)&MonitorFromPoint, "MonitorFromPoint");
        R((delegate* unmanaged<RECT*, uint, nint>)&MonitorFromRect, "MonitorFromRect");
        R((delegate* unmanaged<nint, uint*, int>)&GetMonitorInfoW, "GetMonitorInfoW", "GetMonitorInfo", "GetMonitorInfoA");
        R((delegate* unmanaged<nint, RECT*, delegate* unmanaged<nint, nint, RECT*, nint, int>, nint, int>)&EnumDisplayMonitors, "EnumDisplayMonitors");
        R((delegate* unmanaged<nint, uint>)&GetDpiForWindow, "GetDpiForWindow");
        R((delegate* unmanaged<uint>)&GetDpiForSystem, "GetDpiForSystem");
        R((delegate* unmanaged<nint, int, uint*, uint*, int>)&GetDpiForMonitor, "GetDpiForMonitor");
        R((delegate* unmanaged<nint, int*, int>)&GetProcessDpiAwareness, "GetProcessDpiAwareness");
        R((delegate* unmanaged<int>)&IsProcessDPIAware, "IsProcessDPIAware", "SetProcessDPIAware");
        R((delegate* unmanaged<int, int>)&SetProcessDpiAwareness, "SetProcessDpiAwareness");
        R((delegate* unmanaged<nint, int>)&SetProcessDpiAwarenessContext, "SetProcessDpiAwarenessContext");
        R((delegate* unmanaged<nint, nint>)&SetThreadDpiAwarenessContext, "SetThreadDpiAwarenessContext");
        R((delegate* unmanaged<nint>)&GetThreadDpiAwarenessContext, "GetThreadDpiAwarenessContext", "GetWindowDpiAwarenessContext");
        R((delegate* unmanaged<nint, int>)&GetAwarenessFromDpiAwarenessContext, "GetAwarenessFromDpiAwarenessContext");
        R((delegate* unmanaged<nint, nint, int>)&AreDpiAwarenessContextsEqual, "AreDpiAwarenessContextsEqual");

        R((delegate* unmanaged<POINT*, int>)&GetCursorPos, "GetCursorPos", "GetPhysicalCursorPos");
        R((delegate* unmanaged<int, int, int>)&SetCursorPos, "SetCursorPos", "SetPhysicalCursorPos");
        R((delegate* unmanaged<int, short>)&GetKeyState, "GetKeyState", "GetAsyncKeyState");
        R((delegate* unmanaged<byte*, int>)&GetKeyboardState, "GetKeyboardState");
        R((delegate* unmanaged<uint, int>)&MessageBeep, "MessageBeep");
        R((delegate* unmanaged<char*, uint>)&RegisterWindowMessageW, "RegisterWindowMessageW");
        R((delegate* unmanaged<byte*, uint>)&RegisterWindowMessageBare, "RegisterWindowMessage", "RegisterWindowMessageA");
        R((delegate* unmanaged<int, void*, nint, uint, nint>)&SetWindowsHookEx, "SetWindowsHookExW", "SetWindowsHookEx", "SetWindowsHookExA");
        R((delegate* unmanaged<nint, int>)&UnhookWindowsHookEx, "UnhookWindowsHookEx");
        R((delegate* unmanaged<nint, int, nint, nint, nint>)&CallNextHookEx, "CallNextHookEx");
        R((delegate* unmanaged<uint, uint, int>)&ChangeWindowMessageFilter, "ChangeWindowMessageFilter");
        R((delegate* unmanaged<nint, uint, uint, void*, int>)&ChangeWindowMessageFilterEx, "ChangeWindowMessageFilterEx");
        R((delegate* unmanaged<nint>)&GetProcessWindowStation, "GetProcessWindowStation");
        R((delegate* unmanaged<uint, nint>)&GetThreadDesktop, "GetThreadDesktop");
        R((delegate* unmanaged<nint, int, void*, uint, uint*, int>)&GetUserObjectInformationW, "GetUserObjectInformationW", "GetUserObjectInformation");
        R((delegate* unmanaged<uint, uint, uint>)&MapVirtualKey, "MapVirtualKeyW", "MapVirtualKey", "MapVirtualKeyA");
        R((delegate* unmanaged<uint, nint>)&GetKeyboardLayout, "GetKeyboardLayout");
        R((delegate* unmanaged<int, nint*, int>)&GetKeyboardLayoutList, "GetKeyboardLayoutList");
    }

    // ---- metrics -------------------------------------------------------------------------------

    private static RECT ToRect(CGRect r) => new()
    {
        Left = (int)Math.Round(r.Origin.X),
        Top = (int)Math.Round(r.Origin.Y),
        Right = (int)Math.Round(r.Origin.X + r.Size.Width),
        Bottom = (int)Math.Round(r.Origin.Y + r.Size.Height),
    };

    private static RECT VirtualScreen()
    {
        RECT v = default;
        bool first = true;
        foreach (var s in Screens.All())
        {
            if (first) { v = s.Bounds; first = false; continue; }
            v.Left = Math.Min(v.Left, s.Bounds.Left); v.Top = Math.Min(v.Top, s.Bounds.Top);
            v.Right = Math.Max(v.Right, s.Bounds.Right); v.Bottom = Math.Max(v.Bottom, s.Bounds.Bottom);
        }
        return v;
    }

    private static int SystemMetric(int index)
    {
        var main = Screens.All()[0];
        RECT v = VirtualScreen();
        return index switch
        {
            0 => main.Bounds.Right - main.Bounds.Left,                       // SM_CXSCREEN
            1 => main.Bounds.Bottom - main.Bounds.Top,                       // SM_CYSCREEN
            2 or 21 or 10 => 17,                                            // SM_CXVSCROLL, SM_CXHSCROLL, SM_CXHTHUMB
            3 or 20 or 9 => 17,                                             // SM_CYHSCROLL, SM_CYVSCROLL, SM_CYVTHUMB
            4 => 23,                                                        // SM_CYCAPTION
            5 or 6 => 1,                                                    // SM_CXBORDER, SM_CYBORDER
            7 or 8 => 3,                                                    // SM_CXDLGFRAME, SM_CYDLGFRAME
            11 or 12 or 13 or 14 => 32,                                     // icons and cursors
            15 => 20,                                                       // SM_CYMENU
            16 => main.Work.Right - main.Work.Left,                         // SM_CXFULLSCREEN
            17 => main.Work.Bottom - main.Work.Top - 23,                    // SM_CYFULLSCREEN
            19 => 1,                                                        // SM_MOUSEPRESENT
            28 or 34 => 136, 29 or 35 => 39,                                // SM_CXMIN/SM_CXMINTRACK, SM_CYMIN/SM_CYMINTRACK
            30 => 36, 31 => 22,                                             // SM_CXSIZE, SM_CYSIZE
            32 or 33 => 4,                                                  // SM_CXFRAME, SM_CYFRAME
            36 or 37 or 68 or 69 => 4,                                      // double-click and drag rectangles
            38 or 39 => 75,                                                 // icon spacing
            43 => 3,                                                        // SM_CMOUSEBUTTONS
            45 or 46 => 2,                                                  // SM_CXEDGE, SM_CYEDGE
            47 or 57 => 160, 48 or 58 => 28,                                // minimized spacing / size
            49 or 50 => 16,                                                 // SM_CXSMICON, SM_CYSMICON
            51 => 23, 52 or 53 => 22,                                       // small caption
            54 or 55 => 19,                                                 // SM_CXMENUSIZE, SM_CYMENUSIZE
            56 => 8,                                                        // SM_ARRANGE
            59 => v.Right - v.Left + 12, 60 => v.Bottom - v.Top + 12,       // SM_CXMAXTRACK, SM_CYMAXTRACK
            61 => main.Work.Right - main.Work.Left + 16,                    // SM_CXMAXIMIZED
            62 => main.Work.Bottom - main.Work.Top + 16,                    // SM_CYMAXIMIZED
            63 => 3,                                                        // SM_NETWORK
            71 or 72 => 15,                                                 // menu check marks
            75 => 1,                                                        // SM_MOUSEWHEELPRESENT
            76 => v.Left, 77 => v.Top,                                      // SM_XVIRTUALSCREEN, SM_YVIRTUALSCREEN
            78 => v.Right - v.Left, 79 => v.Bottom - v.Top,                 // SM_CXVIRTUALSCREEN, SM_CYVIRTUALSCREEN
            80 => Screens.All().Count,                                      // SM_CMONITORS
            81 => 1,                                                        // SM_SAMEDISPLAYFORMAT
            82 => 1,                                                        // SM_IMMENABLED
            83 or 84 => 1,                                                  // focus border
            92 => 4,                                                        // SM_CXPADDEDBORDER
            _ => 0,                                                         // everything else: off / absent
        };
    }

    [UnmanagedCallersOnly] private static int GetSystemMetrics(int index) => SystemMetric(index);
    [UnmanagedCallersOnly] private static int GetSystemMetricsForDpi(int index, uint dpi) => SystemMetric(index);

    private static int SystemParameter(uint action, uint param, void* pv)
    {
        switch (action)
        {
            case 0x0030:    // SPI_GETWORKAREA
                *(RECT*)pv = Screens.All()[0].Work;
                return TRUE;
            case 0x0029:    // SPI_GETNONCLIENTMETRICS
                WriteNonClientMetrics((byte*)pv);
                return TRUE;
            case 0x001F:    // SPI_GETICONTITLELOGFONT
                WriteLogFont((byte*)pv, -12, "Segoe UI");
                return TRUE;
            case 0x0068:    // SPI_GETWHEELSCROLLLINES
                *(uint*)pv = 3;
                return TRUE;
            case 0x006C:    // SPI_GETWHEELSCROLLCHARS
                *(uint*)pv = 3;
                return TRUE;
            case 0x0016:    // SPI_GETKEYBOARDDELAY
                *(int*)pv = 1;
                return TRUE;
            case 0x000A:    // SPI_GETKEYBOARDSPEED
                *(int*)pv = 31;
                return TRUE;
            case 0x0070:    // SPI_GETMOUSESPEED
                *(int*)pv = 10;
                return TRUE;
            case 0x0026:    // SPI_GETDRAGFULLWINDOWS
            case 0x1002:    // SPI_GETMENUANIMATION
            case 0x1004:    // SPI_GETCOMBOBOXANIMATION
            case 0x1006:    // SPI_GETLISTBOXSMOOTHSCROLLING
            case 0x1008:    // SPI_GETGRADIENTCAPTIONS
            case 0x100A:    // SPI_GETKEYBOARDCUES
            case 0x1012:    // SPI_GETMENUFADE
            case 0x1014:    // SPI_GETSELECTIONFADE
            case 0x1016:    // SPI_GETTOOLTIPANIMATION
            case 0x1018:    // SPI_GETTOOLTIPFADE
            case 0x101A:    // SPI_GETCURSORSHADOW
            case 0x1042:    // SPI_GETCLIENTAREAANIMATION
            case 0x0048:    // SPI_GETANIMATION (the struct's flag is at offset 4, see below)
            case 0x004A:    // SPI_GETFONTSMOOTHING
            case 0x1024:    // SPI_GETUIEFFECTS
                if (action == 0x0048) ((int*)pv)[1] = TRUE; else *(int*)pv = TRUE;
                return TRUE;
            case 0x0042:    // SPI_GETHIGHCONTRAST: off
                ((uint*)pv)[1] = 0;
                return TRUE;
            case 0x200C:    // SPI_GETFOCUSBORDERWIDTH
            case 0x200E:    // SPI_GETFOCUSBORDERHEIGHT
                *(uint*)pv = 1;
                return TRUE;
            case 0x2012:    // SPI_GETFONTSMOOTHINGTYPE: ClearType
                *(uint*)pv = 2;
                return TRUE;
            case 0x200A:    // SPI_GETCARETWIDTH
                *(uint*)pv = 1;
                return TRUE;
            case 0x005B:    // SPI_GETSCREENREADER
            case 0x0010:    // SPI_GETSCREENSAVEACTIVE
            case 0x101C:    // SPI_GETMOUSESONAR
            case 0x1026:    // SPI_GETMOUSEVANISH
            case 0x0018:    // SPI_GETICONTITLEWRAP
                *(int*)pv = FALSE;
                return TRUE;
            default:
                LogOnce("spi:" + action, $"win32: SystemParametersInfo(0x{action:x}) is not answered");
                SetLastError(ERROR_INVALID_PARAMETER);
                return FALSE;
        }
    }

    // NONCLIENTMETRICSW: cbSize, iBorderWidth, iScrollWidth, iScrollHeight, iCaptionWidth, iCaptionHeight,
    // LOGFONTW lfCaptionFont, iSmCaptionWidth, iSmCaptionHeight, LOGFONTW lfSmCaptionFont, iMenuWidth,
    // iMenuHeight, LOGFONTW lfMenuFont, lfStatusFont, lfMessageFont, iPaddedBorderWidth. LOGFONTW is 92 bytes.
    private static void WriteNonClientMetrics(byte* p)
    {
        uint size = *(uint*)p;
        int* i = (int*)p;
        i[1] = 1; i[2] = 17; i[3] = 17; i[4] = 36; i[5] = 23;
        WriteLogFont(p + 24, -12, "Segoe UI");
        *(int*)(p + 116) = 22; *(int*)(p + 120) = 22;
        WriteLogFont(p + 124, -12, "Segoe UI");
        *(int*)(p + 216) = 19; *(int*)(p + 220) = 19;
        WriteLogFont(p + 224, -12, "Segoe UI");
        WriteLogFont(p + 316, -12, "Segoe UI");
        WriteLogFont(p + 408, -12, "Segoe UI");
        if (size >= 504) *(int*)(p + 500) = 4;
    }

    private static void WriteLogFont(byte* lf, int height, string face)
    {
        NativeMemory.Clear(lf, 92);
        *(int*)lf = height;
        *(int*)(lf + 16) = 400;     // FW_NORMAL
        lf[23] = 1;                 // DEFAULT_CHARSET
        lf[26] = 5;                 // CLEARTYPE_QUALITY
        WriteUtf16((char*)(lf + 28), 32, face);
    }

    [UnmanagedCallersOnly] private static int SystemParametersInfoW(uint action, uint param, void* pv, uint winIni) => SystemParameter(action, param, pv);
    [UnmanagedCallersOnly] private static int SystemParametersInfoForDpi(uint action, uint param, void* pv, uint winIni, uint dpi) => SystemParameter(action, param, pv);
    [UnmanagedCallersOnly] private static uint GetDoubleClickTime() => 500;
    [UnmanagedCallersOnly] private static uint GetCaretBlinkTime() => 530;

    // The Windows 10 default system colours, COLORREF (0x00BBGGRR).
    private static readonly uint[] s_sysColors =
    {
        0xC8C8C8, 0x000000, 0xD1B499, 0xDBCDBF, 0xF0F0F0, 0xFFFFFF, 0x646464, 0x000000, 0x000000, 0x000000,
        0xB4B4B4, 0xFCF7F4, 0xABABAB, 0xD77800, 0xFFFFFF, 0xF0F0F0, 0xA0A0A0, 0x6D6D6D, 0x000000, 0x000000,
        0xFFFFFF, 0x696969, 0xE3E3E3, 0x000000, 0xE1FFFF, 0x000000, 0xCC6600, 0xEAD1B9, 0xF2E4D7, 0xFF9933, 0xF0F0F0,
    };

    [UnmanagedCallersOnly] private static uint GetSysColor(int index) => SysColor(index);

    /// <summary>GetSysColor's answer, for the stack's own code (the SystemColors detour), whose
    /// imports do not reach this library.</summary>
    public static uint SysColor(int index) => (uint)index < (uint)s_sysColors.Length ? s_sysColors[index] : 0;
    [UnmanagedCallersOnly] private static nint GetSysColorBrush(int index) => (uint)index < (uint)s_sysColors.Length ? 0x1000 + index : 0;

    // ---- monitors ------------------------------------------------------------------------------

    // HMONITOR values are 1-based display indices, main display first.
    private static nint MonitorAt(int x, int y, uint flags)
    {
        var screens = Screens.All();
        for (int i = 0; i < screens.Count; i++)
        {
            RECT b = screens[i].Bounds;
            if (x >= b.Left && x < b.Right && y >= b.Top && y < b.Bottom) return i + 1;
        }
        return flags == 0 ? 0 : 1;   // MONITOR_DEFAULTTONULL, else the primary (or nearest; close enough)
    }

    [UnmanagedCallersOnly]
    private static nint MonitorFromWindow(nint hwnd, uint flags)
    {
        RECT r = WindowRect(hwnd, out bool found);
        return found ? MonitorAt((r.Left + r.Right) / 2, (r.Top + r.Bottom) / 2, flags) : (flags == 0 ? 0 : 1);
    }

    [UnmanagedCallersOnly] private static nint MonitorFromPoint(POINT pt, uint flags) => MonitorAt(pt.X, pt.Y, flags);
    [UnmanagedCallersOnly] private static nint MonitorFromRect(RECT* r, uint flags) => MonitorAt((r->Left + r->Right) / 2, (r->Top + r->Bottom) / 2, flags);

    [UnmanagedCallersOnly]
    private static int GetMonitorInfoW(nint monitor, uint* info)
    {
        var screens = Screens.All();
        int i = (int)monitor - 1;
        if ((uint)i >= (uint)screens.Count) { SetLastError(ERROR_INVALID_PARAMETER); return FALSE; }
        // MONITORINFO: cbSize, RECT rcMonitor, RECT rcWork, DWORD dwFlags; MONITORINFOEXW adds szDevice[32].
        uint size = info[0];
        *(RECT*)(info + 1) = screens[i].Bounds;
        *(RECT*)(info + 5) = screens[i].Work;
        info[9] = i == 0 ? 1u : 0u;    // MONITORINFOF_PRIMARY
        if (size >= 104) WriteUtf16((char*)(info + 10), 32, $@"\\.\DISPLAY{i + 1}");
        return TRUE;
    }

    [UnmanagedCallersOnly]
    private static int EnumDisplayMonitors(nint hdc, RECT* clip, delegate* unmanaged<nint, nint, RECT*, nint, int> callback, nint data)
    {
        var screens = Screens.All();
        for (int i = 0; i < screens.Count; i++)
        {
            RECT r = screens[i].Bounds;
            if (callback((nint)(i + 1), hdc, &r, data) == FALSE) break;
        }
        return TRUE;
    }

    // DPI as the application sees it: its high-DPI mode is WinForms' (Application.HighDpiMode, which
    // a SetProcessDpiAwareness* call below sets as Windows would), and the DPI is the one it lays
    // out at -- the display's for a per-monitor-aware application, 96 on the surface the host
    // scales for any other.
    private static Forms.HighDpiMode Mode => Forms.Application.HighDpiMode;
    private static uint SystemDpi => (uint)(s_systemDpi ??= (int)(typeof(Forms.Control).Assembly.GetType("System.Windows.Forms.ScaleHelper")
        ?.GetProperty("InitialSystemDpi", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)?.GetValue(null) ?? 96));
    private static int? s_systemDpi;

    [UnmanagedCallersOnly] private static uint GetDpiForWindow(nint hwnd)
    {
        int dpi = Resolve(hwnd) is Forms.Control ? DriverInt("GetDpiForWindow", hwnd) : 0;
        return dpi > 0 ? (uint)dpi : SystemDpi;
    }
    [UnmanagedCallersOnly] private static uint GetDpiForSystem() => SystemDpi;
    [UnmanagedCallersOnly] private static int GetDpiForMonitor(nint monitor, int type, uint* x, uint* y) { *x = SystemDpi; *y = SystemDpi; return S_OK; }
    [UnmanagedCallersOnly] private static int GetProcessDpiAwareness(nint process, int* value) { *value = Awareness(); return S_OK; }
    [UnmanagedCallersOnly] private static int IsProcessDPIAware() => Awareness() > 0 ? TRUE : FALSE;
    // The application asks for an awareness: it is its high-DPI mode (fails, as on Windows, once set).
    [UnmanagedCallersOnly] private static int SetProcessDpiAwareness(int value) =>
        Request(value switch { 0 => Forms.HighDpiMode.DpiUnaware, 1 => Forms.HighDpiMode.SystemAware, _ => Forms.HighDpiMode.PerMonitor }) ? S_OK : unchecked((int)0x80070005);
    [UnmanagedCallersOnly] private static int SetProcessDpiAwarenessContext(nint ctx) =>
        Request(ctx switch { -1 => Forms.HighDpiMode.DpiUnaware, -2 => Forms.HighDpiMode.SystemAware, -3 => Forms.HighDpiMode.PerMonitor, -5 => Forms.HighDpiMode.DpiUnawareGdiScaled, _ => Forms.HighDpiMode.PerMonitorV2 }) ? TRUE : FALSE;
    [UnmanagedCallersOnly] private static nint SetThreadDpiAwarenessContext(nint ctx) => Context();
    [UnmanagedCallersOnly] private static nint GetThreadDpiAwarenessContext() => Context();

    private static bool Request(Forms.HighDpiMode mode)
    {
        try { return Forms.Application.SetHighDpiMode(mode); }
        catch (Exception) { return false; }
    }

    /// <summary>PROCESS_DPI_AWARENESS for the mode.</summary>
    private static int Awareness() => Mode switch
    {
        Forms.HighDpiMode.SystemAware => 1,
        Forms.HighDpiMode.PerMonitor or Forms.HighDpiMode.PerMonitorV2 => 2,
        _ => 0,
    };

    /// <summary>The DPI_AWARENESS_CONTEXT pseudo-handle for the mode.</summary>
    private static nint Context() => Mode switch
    {
        Forms.HighDpiMode.SystemAware => -2,
        Forms.HighDpiMode.PerMonitor => -3,
        Forms.HighDpiMode.PerMonitorV2 => -4,
        Forms.HighDpiMode.DpiUnawareGdiScaled => -5,
        _ => -1,
    };
    [UnmanagedCallersOnly] private static int GetAwarenessFromDpiAwarenessContext(nint ctx) => ctx switch { -1 or -5 => 0, -2 => 1, _ => 2 };
    [UnmanagedCallersOnly] private static int AreDpiAwarenessContextsEqual(nint a, nint b) => a == b ? TRUE : FALSE;

    // ---- cursor and keyboard -----------------------------------------------------------------------

    [UnmanagedCallersOnly]
    private static int GetCursorPos(POINT* p)
    {
        CGPoint c = MacNative.CursorPosition();
        p->X = (int)Math.Round(c.X);
        p->Y = (int)Math.Round(c.Y);
        return TRUE;
    }

    [UnmanagedCallersOnly] private static int SetCursorPos(int x, int y) => MacNative.CGWarpMouseCursorPosition(new CGPoint { X = x, Y = y }) == 0 ? TRUE : FALSE;

    // Windows virtual-key codes to macOS key codes (kVK_*), for the keys apps actually poll.
    private static readonly Dictionary<int, ushort> s_vkToMac = new()
    {
        [0x08] = 0x33, [0x09] = 0x30, [0x0D] = 0x24, [0x1B] = 0x35, [0x20] = 0x31,
        [0x21] = 0x74, [0x22] = 0x79, [0x23] = 0x77, [0x24] = 0x73, [0x25] = 0x7B, [0x26] = 0x7E, [0x27] = 0x7C, [0x28] = 0x7D,
        [0x2E] = 0x75, [0x70] = 0x7A, [0x71] = 0x78, [0x72] = 0x63, [0x73] = 0x76, [0x74] = 0x60, [0x75] = 0x61,
        [0x76] = 0x62, [0x77] = 0x64, [0x78] = 0x65, [0x79] = 0x6D, [0x7A] = 0x67, [0x7B] = 0x6F,
        ['A'] = 0x00, ['S'] = 0x01, ['D'] = 0x02, ['F'] = 0x03, ['H'] = 0x04, ['G'] = 0x05, ['Z'] = 0x06, ['X'] = 0x07,
        ['C'] = 0x08, ['V'] = 0x09, ['B'] = 0x0B, ['Q'] = 0x0C, ['W'] = 0x0D, ['E'] = 0x0E, ['R'] = 0x0F, ['Y'] = 0x10,
        ['T'] = 0x11, ['1'] = 0x12, ['2'] = 0x13, ['3'] = 0x14, ['4'] = 0x15, ['6'] = 0x16, ['5'] = 0x17, ['9'] = 0x19,
        ['7'] = 0x1A, ['8'] = 0x1C, ['0'] = 0x1D, ['O'] = 0x1F, ['U'] = 0x20, ['I'] = 0x22, ['P'] = 0x23, ['L'] = 0x25,
        ['J'] = 0x26, ['K'] = 0x28, ['N'] = 0x2D, ['M'] = 0x2E,
    };

    private static bool KeyDown(int vk)
    {
        ulong flags = MacNative.CGEventSourceFlagsState(MacNative.CombinedSessionState);
        switch (vk)
        {
            case 0x10: case 0xA0: case 0xA1: return (flags & 0x20000) != 0;   // shift
            case 0x11: case 0xA2: case 0xA3: return (flags & 0x100000) != 0;  // control <- Command, as the stack maps it
            case 0x12: case 0xA4: case 0xA5: return (flags & 0x80000) != 0;   // alt <- Option
            case 0x14: return (flags & 0x10000) != 0;                          // caps lock
            case 0x01: return MacNative.CGEventSourceButtonState(MacNative.CombinedSessionState, 0);
            case 0x02: return MacNative.CGEventSourceButtonState(MacNative.CombinedSessionState, 1);
            case 0x04: return MacNative.CGEventSourceButtonState(MacNative.CombinedSessionState, 2);
        }
        return s_vkToMac.TryGetValue(vk, out ushort key) && MacNative.CGEventSourceKeyState(MacNative.CombinedSessionState, key);
    }

    [UnmanagedCallersOnly]
    private static short GetKeyState(int vk)
    {
        if (vk == 0x14) return (short)(KeyDown(0x14) ? 1 : 0);   // caps lock: the toggle bit
        return KeyDown(vk) ? unchecked((short)0x8000) : (short)0;
    }

    [UnmanagedCallersOnly]
    private static int GetKeyboardState(byte* state)
    {
        for (int vk = 0; vk < 256; vk++) state[vk] = (byte)(KeyDown(vk) ? 0x80 : 0);
        if (KeyDown(0x14)) state[0x14] |= 1;
        return TRUE;
    }

    [UnmanagedCallersOnly] private static int MessageBeep(uint type) => TRUE;

    private static readonly ConcurrentDictionary<string, uint> s_messages2 = new(StringComparer.OrdinalIgnoreCase);
    private static int s_nextMessage = 0xC000;

    private static uint RegisterMessage(string? name)
    {
        if (string.IsNullOrEmpty(name)) { SetLastError(ERROR_INVALID_PARAMETER); return 0; }
        return s_messages2.GetOrAdd(name, _ => (uint)Interlocked.Increment(ref s_nextMessage));
    }

    [UnmanagedCallersOnly] private static uint RegisterWindowMessageW(char* name) => RegisterMessage(ReadUtf16(name));
    [UnmanagedCallersOnly] private static uint RegisterWindowMessageBare(byte* name) => RegisterMessage(ReadEither(name));

    // Hooks are accepted and never called: there is no system-wide message stream to hook, and an app
    // that installs one to watch for activity must not fail to start because of it.
    [UnmanagedCallersOnly] private static nint SetWindowsHookEx(int id, void* proc, nint module, uint thread) => NewHandle(new object());
    [UnmanagedCallersOnly] private static int UnhookWindowsHookEx(nint hook) { s_handles.TryRemove(hook, out _); return TRUE; }
    [UnmanagedCallersOnly] private static nint CallNextHookEx(nint hook, int code, nint w, nint l) => 0;
    [UnmanagedCallersOnly] private static int ChangeWindowMessageFilter(uint msg, uint action) => TRUE;
    [UnmanagedCallersOnly] private static int ChangeWindowMessageFilterEx(nint hwnd, uint msg, uint action, void* status) => TRUE;

    private static readonly nint s_windowStation = 0x50;
    private static readonly nint s_desktop = 0x54;
    [UnmanagedCallersOnly] private static nint GetProcessWindowStation() => s_windowStation;
    [UnmanagedCallersOnly] private static nint GetThreadDesktop(uint thread) => s_desktop;

    [UnmanagedCallersOnly]
    private static int GetUserObjectInformationW(nint obj, int index, void* info, uint length, uint* needed)
    {
        // UOI_FLAGS on the window station: WSF_VISIBLE, i.e. an interactive session. .NET's own
        // Environment.UserInteractive asks exactly this.
        if (index == 1)
        {
            if (needed != null) *needed = 12;
            if (length < 12) { SetLastError(ERROR_INSUFFICIENT_BUFFER); return FALSE; }
            ((uint*)info)[0] = 0; ((uint*)info)[1] = 0; ((uint*)info)[2] = 1;
            return TRUE;
        }
        SetLastError(ERROR_INVALID_PARAMETER);
        return FALSE;
    }

    [UnmanagedCallersOnly]
    private static uint MapVirtualKey(uint code, uint type)
    {
        // MAPVK_VK_TO_CHAR (2): letters and digits map to themselves, which is what apps use it for.
        if (type == 2 && ((code >= 'A' && code <= 'Z') || (code >= '0' && code <= '9'))) return code;
        return 0;
    }

    // The input locale: the current UI culture, in the low word and the device word alike.
    [UnmanagedCallersOnly] private static nint GetKeyboardLayout(uint thread) { uint lcid = (uint)System.Globalization.CultureInfo.CurrentUICulture.KeyboardLayoutId & 0xFFFF; return (nint)(lcid | (lcid << 16)); }

    [UnmanagedCallersOnly]
    private static int GetKeyboardLayoutList(int count, nint* list)
    {
        if (count > 0 && list != null)
        {
            uint lcid = (uint)System.Globalization.CultureInfo.CurrentUICulture.KeyboardLayoutId & 0xFFFF;
            list[0] = (nint)(lcid | (lcid << 16));
        }
        return 1;
    }
}

/// <summary>The displays, as Windows describes monitors: bounds and work area, main display first.</summary>
internal static class Screens
{
    internal readonly record struct Screen(RECT Bounds, RECT Work);

    private static List<Screen>? s_cache;
    private static long s_cachedAt;

    public static List<Screen> All()
    {
        // Displays come and go; re-read at most twice a second.
        long now = Environment.TickCount64;
        if (s_cache != null && now - s_cachedAt < 500) return s_cache;
        var list = new List<Screen>();
        foreach (var (_, bounds) in MacNative.Displays())
        {
            RECT b = new()
            {
                Left = (int)bounds.Origin.X, Top = (int)bounds.Origin.Y,
                Right = (int)(bounds.Origin.X + bounds.Size.Width), Bottom = (int)(bounds.Origin.Y + bounds.Size.Height),
            };
            // The menu bar takes the top of the main display; the Dock's share is not known from here.
            RECT w = b;
            if (list.Count == 0) w.Top += 25;
            list.Add(new Screen(b, w));
        }
        s_cache = list;
        s_cachedAt = now;
        return list;
    }
}
