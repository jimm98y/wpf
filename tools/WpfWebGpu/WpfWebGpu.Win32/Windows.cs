// Windows: the user32 functions that act on the stack's own windows when an app hands them one.
//
// On Windows the app's P/Invokes reach the real user32, except for the functions tagged
// [Win32ExportOnWindows]: the P/Invoke rewriter (WpfWebGpu.ComRewrite, ComRewriter.PInvoke.cs) makes each of
// those run the implementation when one of its handle arguments is a window of the stack's WinForms (a
// Control's Handle, which Windows has never seen), and the real function for every other handle. The
// implementations are the ones macOS uses for every window (User32.Windows.cs), plus the device contexts,
// which differ: Windows has real GDI, so a DC for one of the stack's windows is a real memory DC
// (DeviceContexts below), and every gdi32 call an app makes on it is the real one.

using System.Runtime.InteropServices;
using Forms = System.Windows.Forms;

namespace WpfWebGpu.Win32;

public static unsafe partial class Exports
{
    private static Func<IntPtr, bool>? s_isStackWindow;
    private static Action<IntPtr>? s_flushHdc;

    /// <summary>Whether the process is the Windows comparison head (the real Windows API is underneath).</summary>
    internal static bool OnWindows { get; private set; }

    // Masks: bit k says argument k is a window handle; bit 16+k that it is a device context.
    private const int A0 = 1, A1 = 2;
    // Bit 15: the managed implementation always runs. For the functions that take no window but hand
    // one back (EnumWindows, GetForegroundWindow): it calls the real one and answers with the stack's
    // window where Windows names the host window a form is shown in.
    private const int Always = 1 << 15;
    private const int D0 = 1 << 16, D1 = 1 << 17, D2 = 1 << 18, D5 = 1 << 21;

    // GDI calls that read or write a DC a Graphics may be drawing into (Graphics.FromHdc): the real
    // function, after System.Drawing has written that Graphics' drawing to the DC (GDI+ draws into a DC
    // as it goes; this stack's System.Drawing writes back on Flush, see Graphics.FlushHdc). A mask with
    // device-context bits alone names no implementation: the method below is only where they are listed.
    [Win32ExportOnWindows(D0 | D5, "BitBlt", "StretchBlt", "MaskBlt", "GdiAlphaBlend", "GdiTransparentBlt", "AlphaBlend", "TransparentBlt")]
    [Win32ExportOnWindows(D0 | D2, "PlgBlt")]
    [Win32ExportOnWindows(D0, "PatBlt", "GetPixel", "SetPixel", "SetPixelV", "StretchDIBits", "SetDIBitsToDevice", "GetDIBits", "SetDIBits",
        "ExtTextOutW", "ExtTextOutA", "TextOutW", "TextOutA", "PolyTextOutW", "Rectangle", "Ellipse", "RoundRect", "Pie", "Chord",
        "Polygon", "Polyline", "PolylineTo", "PolyPolygon", "PolyPolyline", "PolyBezier", "PolyBezierTo", "LineTo", "ArcTo", "Arc",
        "FillRgn", "FrameRgn", "PaintRgn", "InvertRgn", "FillPath", "StrokePath", "StrokeAndFillPath", "GradientFill",
        "SelectObject", "DeleteDC", "FloodFill", "ExtFloodFill",
        "FillRect", "FrameRect", "InvertRect", "DrawTextW", "DrawTextA", "DrawTextExW", "DrawTextExA", "DrawEdge",
        "DrawFocusRect", "DrawIconEx", "DrawIcon", "DrawFrameControl", "DrawStateW", "GrayStringW", "TabbedTextOutW")]
    [Win32ExportOnWindows(D1, "DrawThemeBackground", "DrawThemeBackgroundEx", "DrawThemeText", "DrawThemeTextEx", "DrawThemeEdge",
        "DrawThemeIcon", "DrawThemeParentBackground", "DrawThemeParentBackgroundEx")]
    private static void FlushesDeviceContexts() { }

    /// <summary>Windows, the rewritten P/Invokes' question: whether <paramref name="h"/> is one of the
    /// stack's windows (its implementation runs) or not (the real function). Leaves the thread's last
    /// error as it found it.</summary>
    public static bool OnStack(nint h)
    {
        int error = Marshal.GetLastSystemError();
        try { return IsStackWindow(h); }
        catch { return false; }
        finally { Marshal.SetLastSystemError(error); }
    }

    /// <summary>Windows: the drawing a Graphics has pending on device context <paramref name="hdc"/>,
    /// written to it before a real GDI function reads or draws on it.</summary>
    public static void FlushDC(nint hdc)
    {
        int error = Marshal.GetLastSystemError();
        try { (s_flushHdc ??= AppContext.GetData("System.Drawing.FlushHdc") as Action<IntPtr>)?.Invoke(hdc); }
        catch { }
        finally { Marshal.SetLastSystemError(error); }
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
}
