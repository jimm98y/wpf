// Real OS mouse input, for the sample's self-test.
//
// Deliberately not synthetic window messages. WPF drops mouse messages sent to a window that is
// neither active, holding capture, nor under the real cursor ("spurious mouse event"), so a posted
// WM_LBUTTONDOWN simply does nothing. More importantly, going through the OS is what makes the test
// worth running: the click enters at a SCREEN position and has to survive the whole chain --
// screen -> host client pixels -> DIPs -> the driver's virtual screen, or the child window's client
// space -> WPF hit-testing -- so it only passes if what is DRAWN and what is CLICKABLE line up.
// A test that computed its click point from the same numbers the code under test uses would not.

using System;
using System.Runtime.InteropServices;

internal static class OsInput
{
    /// <summary>Put the real cursor at a point in the host window's CLIENT space (device pixels).</summary>
    internal static bool MoveCursorToClientPoint(IntPtr hostWindow, int clientX, int clientY)
    {
        if (!OperatingSystem.IsWindows()) return false;
        var pt = new POINT { x = clientX, y = clientY };
        if (!ClientToScreen(hostWindow, ref pt)) return false;
        if (Environment.GetEnvironmentVariable("WF_TRACE_INPUT") == "1")
            Console.WriteLine($"osinput: client({clientX},{clientY}) -> screen({pt.x},{pt.y})");
        // Capture (and so ButtonBase's press latch) is refused unless the window is foreground, and a
        // test launched from a console has left the console foreground.
        SetForegroundWindow(hostWindow);
        return SetCursorPos(pt.x, pt.y);
    }

    /// <summary>Map a point from one window's client space to another's, via screen coordinates —
    /// the OS's own view of where the two windows sit relative to each other.</summary>
    internal static bool TryMapClientToClient(IntPtr from, IntPtr to, ref System.Drawing.Point pt)
    {
        if (!OperatingSystem.IsWindows()) return false;
        var p = new POINT { x = pt.X, y = pt.Y };
        if (!ClientToScreen(from, ref p) || !ScreenToClient(to, ref p)) return false;
        pt = new System.Drawing.Point(p.x, p.y);
        return true;
    }

    /// <summary>The window's client area in device pixels — the rect the compositor actually maps the
    /// presented surface onto.</summary>
    internal static bool TryGetClientSize(IntPtr hWnd, out int width, out int height)
    {
        width = height = 0;
        if (!OperatingSystem.IsWindows() || !GetClientRect(hWnd, out RECT r)) return false;
        width = r.right - r.left;
        height = r.bottom - r.top;
        return true;
    }

    internal static void PressLeft() => Send(MOUSEEVENTF_LEFTDOWN);
    internal static void ReleaseLeft() => Send(MOUSEEVENTF_LEFTUP);

    private static void Send(uint flags)
    {
        if (!OperatingSystem.IsWindows()) return;
        var input = new INPUT { type = INPUT_MOUSE };
        input.mi.dwFlags = flags;
        if (SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>()) != 1)
            Console.Error.WriteLine($"SendInput(0x{flags:x}) rejected (0x{Marshal.GetLastWin32Error():x})");
    }

    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002, MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint INPUT_MOUSE = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x, y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int left, top, right, bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }

    // INPUT is { DWORD type; union { MOUSEINPUT; KEYBDINPUT; HARDWAREINPUT } }. MOUSEINPUT is the
    // largest arm, so declaring it directly gives the exact 40-byte 64-bit layout -- and the size
    // matters: SendInput rejects the call outright if cbSize is not exactly right.
    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint type; public MOUSEINPUT mi; }

    [DllImport("user32")] private static extern bool ClientToScreen(IntPtr hWnd, ref POINT pt);
    [DllImport("user32")] private static extern bool ScreenToClient(IntPtr hWnd, ref POINT pt);
    [DllImport("user32")] private static extern bool GetClientRect(IntPtr hWnd, out RECT r);
    [DllImport("user32")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32", SetLastError = true)] private static extern uint SendInput(uint n, INPUT[] inputs, int size);
}
