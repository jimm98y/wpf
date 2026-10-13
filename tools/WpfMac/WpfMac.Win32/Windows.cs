// Windows: the user32 functions that act on the stack's own windows when an app hands them one.
//
// On Windows the app's P/Invokes reach the real user32 (WpfMac.Bootstrap/Win32/WindowsShim.cs), except
// for the functions registered here: each runs the implementation below when one of its handle
// arguments is a window of the stack's WinForms (a Control's Handle, which Windows has never seen),
// and the real function for every other handle. The implementations are the ones macOS uses for every
// window (User32.Windows.cs), plus the device contexts, which differ: Windows has real GDI, so a DC
// for one of the stack's windows is a real memory DC (DeviceContexts below), and every gdi32 call an
// app makes on it is the real one.

using System.Runtime.InteropServices;
using Forms = System.Windows.Forms;

namespace WpfMac.Win32;

public static unsafe partial class Exports
{
    private static Func<IntPtr, bool>? s_isStackWindow;
    private static Architecture s_arch;
    private static Action<IntPtr>? s_flushHdc;

    /// <summary>Whether the process is the Windows comparison head (the real Windows API is underneath).</summary>
    internal static bool OnWindows { get; private set; }

    // Masks: bit k says argument k is a window handle; bit 16+k that it is a device context.
    private const int A0 = 1, A1 = 2;

    /// <summary>
    /// Windows: hands the bootstrap each user32 export that has an implementation for the stack's
    /// windows, with the arguments that carry handles, and returns the classifier the dispatcher calls
    /// (see ShimImage): <c>nint classify(entry*, saved argument registers*)</c>.
    /// </summary>
    public static IntPtr RegisterWindows(Action<string, string, IntPtr, int> add, Action<string, string> logOnce, Architecture arch)
    {
        s_logOnce = logOnce;
        s_arch = arch;
        OnWindows = true;
        void W(void* fn, int mask, params string[] names)
        {
            foreach (string name in names) add("user32", name, (IntPtr)fn, mask);
        }

        // Device contexts.
        W((delegate* unmanaged<nint, nint>)&WinGetDC, A0, "GetDC");
        W((delegate* unmanaged<nint, nint>)&WinGetWindowDC, A0, "GetWindowDC");
        W((delegate* unmanaged<nint, nint, uint, nint>)&WinGetDCEx, A0, "GetDCEx");
        W((delegate* unmanaged<nint, nint, int>)&WinReleaseDC, A0, "ReleaseDC");
        W((delegate* unmanaged<nint, byte*, nint>)&WinBeginPaint, A0, "BeginPaint");
        W((delegate* unmanaged<nint, byte*, int>)&WinEndPaint, A0, "EndPaint");
        W((delegate* unmanaged<nint, RECT*, int, int>)&GetUpdateRect, A0, "GetUpdateRect");

        // State and geometry.
        W((delegate* unmanaged<nint, int>)&IsWindow, A0, "IsWindow");
        W((delegate* unmanaged<nint, nint>)&SetFocus, A0, "SetFocus");
        W((delegate* unmanaged<nint, int>)&SetForegroundWindow, A0, "SetForegroundWindow", "BringWindowToTop");
        W((delegate* unmanaged<nint, nint>)&SetActiveWindow, A0, "SetActiveWindow");
        W((delegate* unmanaged<nint, int>)&IsWindowVisible, A0, "IsWindowVisible");
        W((delegate* unmanaged<nint, int>)&IsWindowEnabled, A0, "IsWindowEnabled");
        W((delegate* unmanaged<nint, int, int>)&EnableWindow, A0, "EnableWindow");
        W((delegate* unmanaged<nint, int>)&IsIconic, A0, "IsIconic");
        W((delegate* unmanaged<nint, int>)&IsZoomed, A0, "IsZoomed");
        W((delegate* unmanaged<nint, int, int>)&ShowWindow, A0, "ShowWindow", "ShowWindowAsync");
        W((delegate* unmanaged<nint, RECT*, int>)&GetWindowRectExport, A0, "GetWindowRect");
        W((delegate* unmanaged<nint, RECT*, int>)&GetClientRect, A0, "GetClientRect");
        W((delegate* unmanaged<nint, POINT*, int>)&ClientToScreen, A0, "ClientToScreen");
        W((delegate* unmanaged<nint, POINT*, int>)&ScreenToClient, A0, "ScreenToClient");
        W((delegate* unmanaged<nint, nint, POINT*, uint, int>)&MapWindowPoints, A0 | A1, "MapWindowPoints");
        W((delegate* unmanaged<nint, nint, int, int, int, int, uint, int>)&SetWindowPos, A0, "SetWindowPos");
        W((delegate* unmanaged<nint, int, int, int, int, int, int>)&MoveWindow, A0, "MoveWindow");
        W((delegate* unmanaged<nint, nint>)&GetParent, A0, "GetParent");
        W((delegate* unmanaged<nint, uint, nint>)&GetAncestor, A0, "GetAncestor");
        W((delegate* unmanaged<nint, uint, nint>)&GetWindow, A0, "GetWindow");
        W((delegate* unmanaged<nint, nint, nint>)&SetParent, A0, "SetParent");
        W((delegate* unmanaged<nint, nint, int>)&IsChild, A0 | A1, "IsChild");
        W((delegate* unmanaged<nint, uint*, uint>)&GetWindowThreadProcessId, A0, "GetWindowThreadProcessId");
        W((delegate* unmanaged<nint, int, nint>)&GetWindowLongPtr, A0, "GetWindowLongPtrW", "GetWindowLongPtrA", "GetWindowLongW", "GetWindowLongA");
        W((delegate* unmanaged<nint, int, nint, nint>)&SetWindowLongPtr, A0, "SetWindowLongPtrW", "SetWindowLongPtrA", "SetWindowLongW", "SetWindowLongA");
        W((delegate* unmanaged<nint, int, nint>)&GetClassLongPtr, A0, "GetClassLongPtrW", "GetClassLongW");
        W((delegate* unmanaged<nint, int, nint, nint>)&SetClassLongPtr, A0, "SetClassLongPtrW", "SetClassLongW");
        W((delegate* unmanaged<nint, char*, int, int>)&GetClassNameW, A0, "GetClassNameW");
        W((delegate* unmanaged<nint, char*, int, int>)&GetWindowTextW, A0, "GetWindowTextW");
        W((delegate* unmanaged<nint, int>)&GetWindowTextLength, A0, "GetWindowTextLengthW");
        W((delegate* unmanaged<nint, char*, int>)&SetWindowTextW, A0, "SetWindowTextW");
        W((delegate* unmanaged<nint, delegate* unmanaged<nint, nint, int>, nint, int>)&EnumChildWindows, A0, "EnumChildWindows");
        W((delegate* unmanaged<nint, int>)&DestroyWindow, A0, "DestroyWindow");
        W((delegate* unmanaged<nint, uint*, int>)&GetWindowPlacement, A0, "GetWindowPlacement");
        W((delegate* unmanaged<nint, uint*, int>)&SetWindowPlacement, A0, "SetWindowPlacement");
        W((delegate* unmanaged<nint, RECT*, int, int>)&InvalidateRect, A0, "InvalidateRect");
        W((delegate* unmanaged<nint, RECT*, int>)&ValidateRect, A0, "ValidateRect");
        W((delegate* unmanaged<nint, int>)&UpdateWindow, A0, "UpdateWindow");
        W((delegate* unmanaged<nint, RECT*, nint, uint, int>)&RedrawWindow, A0, "RedrawWindow");
        W((delegate* unmanaged<nint, int>)&LockWindowUpdate, A0, "LockWindowUpdate");
        W((delegate* unmanaged<nint, nint, int, int>)&SetWindowRgn, A0, "SetWindowRgn");
        W((delegate* unmanaged<nint, uint, byte, uint, int>)&SetLayeredWindowAttributes, A0, "SetLayeredWindowAttributes");

        // Messages, timers and properties.
        W((delegate* unmanaged<nint, uint, nint, nint, nint>)&SendMessageExport, A0, "SendMessageW", "SendMessageA");
        W((delegate* unmanaged<nint, uint, nint, nint, uint, uint, nint*, nint>)&SendMessageTimeout, A0, "SendMessageTimeoutW", "SendMessageTimeoutA");
        W((delegate* unmanaged<nint, uint, nint, nint, int>)&PostMessageExport, A0, "PostMessageW", "PostMessageA", "SendNotifyMessageW", "SendNotifyMessageA");
        W((delegate* unmanaged<nint, uint, nint, nint, nint>)&DefWindowProc, A0, "DefWindowProcW", "DefWindowProcA");
        W((delegate* unmanaged<nint, nuint, uint, void*, nuint>)&SetTimer, A0, "SetTimer");
        W((delegate* unmanaged<nint, nuint, int>)&KillTimer, A0, "KillTimer");
        W((delegate* unmanaged<nint, char*, nint, int>)&SetPropW, A0, "SetPropW");
        W((delegate* unmanaged<nint, char*, nint>)&GetPropW, A0, "GetPropW");
        W((delegate* unmanaged<nint, char*, nint>)&RemovePropW, A0, "RemovePropW");

        // GDI calls that read or write a DC a Graphics may be drawing into (Graphics.FromHdc): the
        // real function, after System.Drawing has written that Graphics' drawing to the DC (GDI+ draws
        // into a DC as it goes; this stack's System.Drawing writes back on Flush, see Graphics.FlushHdc).
        void F(string module, int mask, params string[] names)
        {
            foreach (string name in names) add(module, name, 0, mask);
        }
        const int D0 = 1 << 16, D1 = 1 << 17, D2 = 1 << 18, D5 = 1 << 21;
        F("gdi32", D0 | D5, "BitBlt", "StretchBlt", "MaskBlt", "GdiAlphaBlend", "GdiTransparentBlt");
        F("gdi32", D0 | D2, "PlgBlt");
        F("gdi32", D0, "PatBlt", "GetPixel", "SetPixel", "SetPixelV", "StretchDIBits", "SetDIBitsToDevice", "GetDIBits", "SetDIBits",
            "ExtTextOutW", "ExtTextOutA", "TextOutW", "TextOutA", "PolyTextOutW", "Rectangle", "Ellipse", "RoundRect", "Pie", "Chord",
            "Polygon", "Polyline", "PolylineTo", "PolyPolygon", "PolyPolyline", "PolyBezier", "PolyBezierTo", "LineTo", "ArcTo", "Arc",
            "FillRgn", "FrameRgn", "PaintRgn", "InvertRgn", "FillPath", "StrokePath", "StrokeAndFillPath", "GradientFill",
            "SelectObject", "DeleteDC", "FloodFill", "ExtFloodFill");
        F("msimg32", D0 | D5, "AlphaBlend", "TransparentBlt");
        F("msimg32", D0, "GradientFill");
        F("user32", D0, "FillRect", "FrameRect", "InvertRect", "DrawTextW", "DrawTextA", "DrawTextExW", "DrawTextExA", "DrawEdge",
            "DrawFocusRect", "DrawIconEx", "DrawIcon", "DrawFrameControl", "DrawStateW", "GrayStringW", "TabbedTextOutW");
        F("uxtheme", D1, "DrawThemeBackground", "DrawThemeBackgroundEx", "DrawThemeText", "DrawThemeTextEx", "DrawThemeEdge",
            "DrawThemeIcon", "DrawThemeParentBackground", "DrawThemeParentBackgroundEx");

        return (IntPtr)(delegate* unmanaged<byte*, nint*, nint>)&Classify;
    }

    /// <summary>Whether <paramref name="h"/> is a window of the stack's WinForms. Never a real HWND:
    /// the driver's handles are not Windows' (see XplatUIWebGpu.CreateWindow).</summary>
    internal static bool IsStackWindow(nint h)
    {
        if (h == 0) return false;
        if (s_isStackWindow == null)
        {
            var hwndType = typeof(Forms.Control).Assembly.GetType("System.Windows.Forms.Hwnd");
            var from = hwndType?.GetMethod("ObjectFromHandle", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            if (from == null) return false;
            var f = (Func<IntPtr, object?>)Delegate.CreateDelegate(typeof(Func<IntPtr, object?>), from);
            s_isStackWindow = p => f(p) != null;
        }
        return s_isStackWindow((IntPtr)h);
    }

    /// <summary>
    /// Called by the dispatcher for every call of a registered export: the managed implementation when
    /// a handle argument is the stack's, otherwise the real function. Leaves the thread's last error as
    /// it found it, so a real function that succeeds without setting one reads what the caller expects.
    /// </summary>
    [UnmanagedCallersOnly]
    private static nint Classify(byte* entry, nint* saved)
    {
        nint real = *(nint*)entry, managed = *(nint*)(entry + 8);
        int mask = *(int*)(entry + 16);
        int error = Marshal.GetLastSystemError();
        try
        {
            if ((mask >> 16) != 0 && (s_flushHdc ??= AppContext.GetData("System.Drawing.FlushHdc") as Action<IntPtr>) is Action<IntPtr> flush)
            {
                for (int k = 0; k < 16; k++)
                {
                    if ((mask & (1 << (16 + k))) != 0) flush(*(nint*)((byte*)saved + ArgumentOffset(k)));
                }
            }
            if (managed == 0) return real;
            for (int k = 0; k < 16; k++)
            {
                if ((mask & (1 << k)) == 0) continue;
                nint h = *(nint*)((byte*)saved + ArgumentOffset(k));
                if (IsStackWindow(h)) return managed;
            }
        }
        catch
        {
        }
        finally
        {
            Marshal.SetLastSystemError(error);
        }
        return real;
    }

    private static int ArgumentOffset(int k) => s_arch == Architecture.Arm64
        ? (k < 8 ? 8 * k : 160 + 8 * (k - 8))
        : (k < 4 ? 8 * k : 0x90 + 8 * (k - 4));
}
