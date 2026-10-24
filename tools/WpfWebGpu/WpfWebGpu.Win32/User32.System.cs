// user32, the parts that describe the system rather than a window: metrics, parameters, monitors,
// DPI, the cursor and keyboard, registered messages, hooks, system colours.
//
// Coordinates are the stack's: macOS points, origin at the top-left of the main display, which is
// what a WPF window's Left/Top are here. DPI is 96: the host scales the whole surface for the display,
// so a Windows app sees the 100% case it was written for.

using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using WpfWebGpu.ComInterop;
using Forms = System.Windows.Forms;

namespace WpfWebGpu.Win32;

[StructLayout(LayoutKind.Sequential)]
public struct RECT { public int Left, Top, Right, Bottom; }

[StructLayout(LayoutKind.Sequential)]
public struct POINT { public int X, Y; }

public static unsafe partial class Exports
{

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

    [Win32Export("GetSystemMetrics")] public static int GetSystemMetrics(int index) => SystemMetric(index);
    [Win32Export("GetSystemMetricsForDpi")] public static int GetSystemMetricsForDpi(int index, uint dpi) => SystemMetric(index);

    private static int SystemParameter(uint action, uint param, void* pv, bool wide = true)
    {
        switch (action)
        {
            case 0x0030:    // SPI_GETWORKAREA
                *(RECT*)pv = Screens.All()[0].Work;
                return TRUE;
            case 0x0029:    // SPI_GETNONCLIENTMETRICS
                WriteNonClientMetrics((byte*)pv, wide);
                return TRUE;
            case 0x001F:    // SPI_GETICONTITLELOGFONT
                WriteLogFont((byte*)pv, -12, "Segoe UI", wide);
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
    /// <summary>NONCLIENTMETRICSW, or NONCLIENTMETRICSA (<paramref name="wide"/> false: its five fonts are
    /// LOGFONTA, 60 bytes rather than 92).</summary>
    private static void WriteNonClientMetrics(byte* p, bool wide = true)
    {
        uint size = *(uint*)p;
        int lf = wide ? 92 : 60;
        int* i = (int*)p;
        i[1] = 1; i[2] = 17; i[3] = 17; i[4] = 36; i[5] = 23;
        byte* q = p + 24;
        WriteLogFont(q, -12, "Segoe UI", wide); q += lf;               // lfCaptionFont
        *(int*)q = 22; *(int*)(q + 4) = 22; q += 8;                    // iSmCaptionWidth, iSmCaptionHeight
        WriteLogFont(q, -12, "Segoe UI", wide); q += lf;               // lfSmCaptionFont
        *(int*)q = 19; *(int*)(q + 4) = 19; q += 8;                    // iMenuWidth, iMenuHeight
        WriteLogFont(q, -12, "Segoe UI", wide); q += lf;               // lfMenuFont
        WriteLogFont(q, -12, "Segoe UI", wide); q += lf;               // lfStatusFont
        WriteLogFont(q, -12, "Segoe UI", wide); q += lf;               // lfMessageFont
        if (size >= (uint)(q - p) + 4) *(int*)q = 4;                   // iPaddedBorderWidth
    }

    /// <summary>LOGFONTW, or LOGFONTA (<paramref name="wide"/> false: the face name in 32 ANSI bytes).</summary>
    private static void WriteLogFont(byte* lf, int height, string face, bool wide = true)
    {
        NativeMemory.Clear(lf, wide ? 92u : 60u);
        *(int*)lf = height;
        *(int*)(lf + 16) = 400;     // FW_NORMAL
        lf[23] = 1;                 // DEFAULT_CHARSET
        lf[26] = 5;                 // CLEARTYPE_QUALITY
        if (wide) WriteUtf16((char*)(lf + 28), 32, face);
        else WriteUtf8(lf + 28, 32, face);
    }

    [Win32Export("SystemParametersInfoW", "SystemParametersInfo")] public static int SystemParametersInfoW(uint action, uint param, void* pv, uint winIni) => SystemParameter(action, param, pv);
    [Win32Export("SystemParametersInfoA")] public static int SystemParametersInfoA(uint action, uint param, void* pv, uint winIni) => SystemParameter(action, param, pv, wide: false);
    [Win32Export("SystemParametersInfoForDpi")] public static int SystemParametersInfoForDpi(uint action, uint param, void* pv, uint winIni, uint dpi) => SystemParameter(action, param, pv);
    [Win32Export("GetDoubleClickTime")] public static uint GetDoubleClickTime() => 500;
    [Win32Export("GetCaretBlinkTime")] public static uint GetCaretBlinkTime() => 530;

    // The Windows 10 default system colours, COLORREF (0x00BBGGRR).
    private static readonly uint[] s_sysColors =
    {
        0xC8C8C8, 0x000000, 0xD1B499, 0xDBCDBF, 0xF0F0F0, 0xFFFFFF, 0x646464, 0x000000, 0x000000, 0x000000,
        0xB4B4B4, 0xFCF7F4, 0xABABAB, 0xD77800, 0xFFFFFF, 0xF0F0F0, 0xA0A0A0, 0x6D6D6D, 0x000000, 0x000000,
        0xFFFFFF, 0x696969, 0xE3E3E3, 0x000000, 0xE1FFFF, 0x000000, 0xCC6600, 0xEAD1B9, 0xF2E4D7, 0xFF9933, 0xF0F0F0,
    };

    [Win32Export("GetSysColor")] public static uint GetSysColor(int index) => SysColor(index);

    /// <summary>GetSysColor's answer, for the stack's own code (the SystemColors detour), whose
    /// imports do not reach this library.</summary>
    public static uint SysColor(int index) => (uint)index < (uint)s_sysColors.Length ? s_sysColors[index] : 0;
    [Win32Export("GetSysColorBrush")] public static nint GetSysColorBrush(int index) => (uint)index < (uint)s_sysColors.Length ? 0x1000 + index : 0;

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

    [Win32Export("MonitorFromWindow")]
    public static nint MonitorFromWindow(nint hwnd, uint flags)
    {
        RECT r = WindowRect(hwnd, out bool found);
        return found ? MonitorAt((r.Left + r.Right) / 2, (r.Top + r.Bottom) / 2, flags) : (flags == 0 ? 0 : 1);
    }

    [Win32Export("MonitorFromPoint")] public static nint MonitorFromPoint(POINT pt, uint flags) => MonitorAt(pt.X, pt.Y, flags);
    [Win32Export("MonitorFromRect")] public static nint MonitorFromRect(RECT* r, uint flags) => MonitorAt((r->Left + r->Right) / 2, (r->Top + r->Bottom) / 2, flags);

    [Win32Export("GetMonitorInfoW", "GetMonitorInfo", "GetMonitorInfoA")]
    public static int GetMonitorInfoW(nint monitor, uint* info)
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

    [Win32Export("EnumDisplayMonitors")]
    public static int EnumDisplayMonitors(nint hdc, RECT* clip, delegate* unmanaged<nint, nint, RECT*, nint, int> callback, nint data)
    {
        var screens = Screens.All();
        for (int i = 0; i < screens.Count; i++)
        {
            RECT r = screens[i].Bounds;
            int go = Callbacks.Of((nint)callback) is { } d ? (int)Callbacks.Invoke(d, (nint)(i + 1), hdc, (nint)(&r), data) : callback((nint)(i + 1), hdc, &r, data);
            if (go == FALSE) break;
        }
        return TRUE;
    }

    // DPI as the application sees it: its high-DPI mode is WinForms' (Application.HighDpiMode, which
    // a SetProcessDpiAwareness* call below sets as Windows would), and the DPI is the one it lays
    // out at -- the display's for a per-monitor-aware application, 96 on the surface the host
    // scales for any other.
    private static Forms.HighDpiMode Mode => Forms.Application.HighDpiMode;
    private static uint SystemDpi => (uint)Forms.Win32Bridge.InitialSystemDpi;

    [Win32Export("GetDpiForWindow")] public static uint GetDpiForWindow(nint hwnd)
    {
        int dpi = Resolve(hwnd) is Forms.Control ? Forms.Win32Bridge.GetDpiForWindow(hwnd) : 0;
        return dpi > 0 ? (uint)dpi : SystemDpi;
    }
    [Win32Export("GetDpiForSystem")] public static uint GetDpiForSystem() => SystemDpi;
    [Win32Export("GetDpiForMonitor")] public static int GetDpiForMonitor(nint monitor, int type, uint* x, uint* y) { *x = SystemDpi; *y = SystemDpi; return S_OK; }
    [Win32Export("GetProcessDpiAwareness")] public static int GetProcessDpiAwareness(nint process, int* value) { *value = Awareness(); return S_OK; }
    [Win32Export("IsProcessDPIAware", "SetProcessDPIAware")] public static int IsProcessDPIAware() => Awareness() > 0 ? TRUE : FALSE;
    // The application asks for an awareness: it is its high-DPI mode (fails, as on Windows, once set).
    [Win32Export("SetProcessDpiAwareness")] public static int SetProcessDpiAwareness(int value) =>
        Request(value switch { 0 => Forms.HighDpiMode.DpiUnaware, 1 => Forms.HighDpiMode.SystemAware, _ => Forms.HighDpiMode.PerMonitor }) ? S_OK : unchecked((int)0x80070005);
    [Win32Export("SetProcessDpiAwarenessContext")] public static int SetProcessDpiAwarenessContext(nint ctx) =>
        Request(ctx switch { -1 => Forms.HighDpiMode.DpiUnaware, -2 => Forms.HighDpiMode.SystemAware, -3 => Forms.HighDpiMode.PerMonitor, -5 => Forms.HighDpiMode.DpiUnawareGdiScaled, _ => Forms.HighDpiMode.PerMonitorV2 }) ? TRUE : FALSE;
    [Win32Export("SetThreadDpiAwarenessContext")] public static nint SetThreadDpiAwarenessContext(nint ctx) => Context();
    [Win32Export("GetThreadDpiAwarenessContext", "GetWindowDpiAwarenessContext")] public static nint GetThreadDpiAwarenessContext() => Context();

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
    [Win32Export("GetAwarenessFromDpiAwarenessContext")] public static int GetAwarenessFromDpiAwarenessContext(nint ctx) => ctx switch { -1 or -5 => 0, -2 => 1, _ => 2 };
    [Win32Export("AreDpiAwarenessContextsEqual")] public static int AreDpiAwarenessContextsEqual(nint a, nint b) => a == b ? TRUE : FALSE;

    // ---- cursor and keyboard -----------------------------------------------------------------------

    [Win32Export("GetCursorPos", "GetPhysicalCursorPos")]
    public static int GetCursorPos(POINT* p)
    {
        if (!Platform.Cursor(out int x, out int y)) { p->X = p->Y = 0; return FALSE; }
        p->X = x;
        p->Y = y;
        return TRUE;
    }

    [Win32Export("SetCursorPos", "SetPhysicalCursorPos")] public static int SetCursorPos(int x, int y) => Platform.SetCursor(x, y) ? TRUE : FALSE;

    private static bool KeyDown(int vk) => Platform.KeyDown(vk);

    [Win32Export("GetKeyState", "GetAsyncKeyState")]
    public static short GetKeyState(int vk)
    {
        if (vk == 0x14) return (short)(KeyDown(0x14) ? 1 : 0);   // caps lock: the toggle bit
        return KeyDown(vk) ? unchecked((short)0x8000) : (short)0;
    }

    [Win32Export("GetKeyboardState")]
    public static int GetKeyboardState(byte* state)
    {
        for (int vk = 0; vk < 256; vk++) state[vk] = (byte)(KeyDown(vk) ? 0x80 : 0);
        if (KeyDown(0x14)) state[0x14] |= 1;
        return TRUE;
    }

    [Win32Export("MessageBeep")] public static int MessageBeep(uint type) => TRUE;

    private static readonly ConcurrentDictionary<string, uint> s_messages2 = new(StringComparer.OrdinalIgnoreCase);
    private static int s_nextMessage = 0xC000;

    private static uint RegisterMessage(string? name)
    {
        if (string.IsNullOrEmpty(name)) { SetLastError(ERROR_INVALID_PARAMETER); return 0; }
        return s_messages2.GetOrAdd(name, _ => (uint)Interlocked.Increment(ref s_nextMessage));
    }

    [Win32Export("RegisterWindowMessageW")] public static uint RegisterWindowMessageW(char* name) => RegisterMessage(ReadUtf16(name));
    [Win32Export("RegisterWindowMessage", "RegisterWindowMessageA")] public static uint RegisterWindowMessageBare(byte* name) => RegisterMessage(ReadEither(name));

    // Hooks are accepted and never called: there is no system-wide message stream to hook, and an app
    // that installs one to watch for activity must not fail to start because of it.
    [Win32Export("SetWindowsHookExW", "SetWindowsHookEx", "SetWindowsHookExA")] public static nint SetWindowsHookEx(int id, void* proc, nint module, uint thread) => NewHandle(new object());
    [Win32Export("UnhookWindowsHookEx")] public static int UnhookWindowsHookEx(nint hook) { s_handles.TryRemove(hook, out _); return TRUE; }
    [Win32Export("CallNextHookEx")] public static nint CallNextHookEx(nint hook, int code, nint w, nint l) => 0;
    [Win32Export("ChangeWindowMessageFilter")] public static int ChangeWindowMessageFilter(uint msg, uint action) => TRUE;
    [Win32Export("ChangeWindowMessageFilterEx")] public static int ChangeWindowMessageFilterEx(nint hwnd, uint msg, uint action, void* status) => TRUE;

    private static readonly nint s_windowStation = 0x50;
    private static readonly nint s_desktop = 0x54;
    [Win32Export("GetProcessWindowStation")] public static nint GetProcessWindowStation() => s_windowStation;
    [Win32Export("GetThreadDesktop")] public static nint GetThreadDesktop(uint thread) => s_desktop;

    [Win32Export("GetUserObjectInformationW", "GetUserObjectInformation", "GetUserObjectInformationA")]   // only UOI_FLAGS, no text: one for both
    public static int GetUserObjectInformationW(nint obj, int index, void* info, uint length, uint* needed)
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

    [Win32Export("MapVirtualKeyW", "MapVirtualKey", "MapVirtualKeyA")]
    public static uint MapVirtualKey(uint code, uint type)
    {
        // MAPVK_VK_TO_CHAR (2): letters and digits map to themselves, which is what apps use it for.
        if (type == 2 && ((code >= 'A' && code <= 'Z') || (code >= '0' && code <= '9'))) return code;
        return 0;
    }

    // The input locale: the current UI culture, in the low word and the device word alike.
    [Win32Export("GetKeyboardLayout")] public static nint GetKeyboardLayout(uint thread) { uint lcid = (uint)System.Globalization.CultureInfo.CurrentUICulture.KeyboardLayoutId & 0xFFFF; return (nint)(lcid | (lcid << 16)); }

    [Win32Export("GetKeyboardLayoutList")]
    public static int GetKeyboardLayoutList(int count, nint* list)
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
        foreach (var (b, w) in Platform.Displays()) list.Add(new Screen(b, w));
        s_cache = list;
        s_cachedAt = now;
        return list;
    }
}
