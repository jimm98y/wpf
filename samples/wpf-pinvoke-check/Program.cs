// Calls a handful of Windows functions the way Windows WPF apps call them, and reports what each
// answered: implemented ones (window text and geometry, metrics, key state, tick count), one the stack
// does not implement (it fails as on a Windows without it), and one no Windows DLL exports (it throws
// EntryPointNotFoundException, as there).

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;

public static class Program
{
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }

    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool SetWindowText(IntPtr hwnd, string text);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern short GetKeyState(int vk);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("kernel32.dll")] private static extern ulong GetTickCount64();
    [DllImport("kernel32.dll")] private static extern uint GetCurrentProcessId();
    [DllImport("user32.dll", SetLastError = true)] private static extern bool LockWorkStation();
    [DllImport("user32.dll", EntryPoint = "NoWindowsDllExportsThis")] private static extern int NotExported();

    private static readonly List<string> s_lines = new();

    [STAThread]
    public static void Main()
    {
        var app = new Application();
        var text = new TextBlock { Margin = new Thickness(12), FontFamily = new System.Windows.Media.FontFamily("Consolas") };
        var window = new Window { Title = "P/Invoke check", Width = 520, Height = 360, Content = text };
        window.Loaded += (_, _) =>
        {
            Check(window);
            text.Text = string.Join("\n", s_lines);
        };
        app.Run(window);
    }

    private static void Check(Window window)
    {
        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        Report("GetSystemMetrics(SM_CXSCREEN, SM_CYSCREEN)", () => $"{GetSystemMetrics(0)} x {GetSystemMetrics(1)}");
        Report("GetWindowRect", () => GetWindowRect(hwnd, out RECT r) ? $"{r.Left},{r.Top} - {r.Right},{r.Bottom}" : $"FALSE, error {Marshal.GetLastPInvokeError()}");
        Report("GetWindowText", () => { var sb = new StringBuilder(256); int n = GetWindowText(hwnd, sb, sb.Capacity); return $"{n} \"{sb}\""; });
        Report("SetWindowText, then Window.Title", () => $"{SetWindowText(hwnd, "Renamed by SetWindowText")} \"{window.Title}\"");
        Report("IsWindowVisible", () => IsWindowVisible(hwnd).ToString());
        Report("GetDpiForWindow", () => GetDpiForWindow(hwnd).ToString());
        Report("GetKeyState(VK_SHIFT)", () => $"0x{(ushort)GetKeyState(0x10):x4}");
        Report("GetCursorPos", () => GetCursorPos(out POINT p) ? $"{p.X},{p.Y}" : "FALSE");
        Report("GetTickCount64", () => GetTickCount64() > 0 ? "> 0" : "0");
        Report("GetCurrentProcessId", () => GetCurrentProcessId().ToString());
        Report("LockWorkStation (not implemented)", () => $"{LockWorkStation()}, error {Marshal.GetLastPInvokeError()}");
        Report("NoWindowsDllExportsThis (no such export)", () => NotExported().ToString());
    }

    private static void Report(string what, Func<string> call)
    {
        string result;
        try { result = call(); }
        catch (Exception e) { result = e.GetType().Name; }
        string line = $"pinvoke: {what} = {result}";
        s_lines.Add(line);
        Console.WriteLine(line);
    }
}
