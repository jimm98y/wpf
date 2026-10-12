using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using System.Windows.Forms.Integration;
using Microsoft.Win32;
using Forms = System.Windows.Forms;
using Wpf = System.Windows;
using WpfControls = System.Windows.Controls;

namespace MixedFixture;

// A WinForms shell hosting WPF (ElementHost) that opens a WPF window hosting WinForms
// (WindowsFormsHost), the way large line-of-business apps mix the two, plus the Windows services
// such apps lean on: the registry, the event log, WMI and user32. Prints "fixture: PASS" or the
// failures; --seconds N closes it after N seconds. Behaves the same run as the stock .exe on Windows.
internal static partial class Program
{
    private static readonly List<string> s_failures = new();

    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        var wpfApp = new Wpf.Application { ShutdownMode = Wpf.ShutdownMode.OnExplicitShutdown };

        var form = new Forms.Form { Text = "MixedFixture", ClientSize = new System.Drawing.Size(560, 360) };
        var wpfButton = new WpfControls.Button { Content = "A WPF button inside WinForms", Padding = new Wpf.Thickness(8) };
        var host = new ElementHost { Dock = Forms.DockStyle.Top, Height = 80, Child = wpfButton };
        var log = new Forms.ListBox { Dock = Forms.DockStyle.Fill };
        var clickMe = new Forms.Button { Text = "Click me", Dock = Forms.DockStyle.Bottom, Height = 40 };
        form.Controls.Add(log);
        form.Controls.Add(clickMe);
        form.Controls.Add(host);

        form.Shown += (_, _) =>
        {
            var formsButton = new Forms.Button { Text = "A WinForms button inside WPF" };
            var window = new Wpf.Window
            {
                Title = "MixedFixture (WPF)",
                Width = 420,
                Height = 200,
                Content = new WpfControls.DockPanel
                {
                    Children =
                    {
                        new WpfControls.TextBlock { Text = "WPF text above a WindowsFormsHost", Margin = new Wpf.Thickness(8) },
                        new WindowsFormsHost { Child = formsButton },
                    },
                },
            };
            WpfControls.DockPanel.SetDock(((WpfControls.DockPanel)window.Content).Children[0], WpfControls.Dock.Top);
            window.Show();

            // Let both trees lay out before looking at them.
            var check = new Forms.Timer { Interval = 1500 };
            check.Tick += (_, _) =>
            {
                check.Stop();
                Run("ElementHost child laid out", () => wpfButton.ActualWidth > 0 ? null : "the WPF button has no width");
                Run("WindowsFormsHost child created", () => formsButton.IsHandleCreated && formsButton.Width > 0 ? null : "the WinForms button has no handle or width");
                Run("registry round trip", Registry_);
                Run("event logs", EventLog_);
                Run("WMI", Wmi);
                Run("user32", () => User32(form.Handle));
                window.Close();
                if (OperatingSystem.IsMacOS()) MacWindowFollows(form, args, () => MacClickReachesControl(form, clickMe, Finish));
                else Finish();
            };
            check.Start();
        };

        void Finish()
        {
            foreach (string f in s_failures) log.Items.Add("FAIL " + f);
            Console.WriteLine(s_failures.Count == 0 ? "fixture: PASS" : "fixture: FAILED");
            int i = Array.IndexOf(args, "--seconds");
            if (i >= 0 && i + 1 < args.Length && double.TryParse(args[i + 1], out double seconds))
            {
                var close = new Forms.Timer { Interval = (int)(seconds * 1000) };
                close.Tick += (_, _) => { close.Stop(); form.Close(); };
                close.Start();
            }
        }

        Forms.Application.Run(form);
        wpfApp.Shutdown();
        return s_failures.Count == 0 ? 0 : 1;
    }

    private static void Run(string name, Func<string?> check)
    {
        string? failure;
        try { failure = check(); }
        catch (Exception e) { failure = $"{e.GetType().Name}: {e.Message}"; }
        Console.WriteLine($"fixture: {(failure == null ? "ok  " : "FAIL")} {name}{(failure == null ? "" : ": " + failure)}");
        if (failure != null) s_failures.Add($"{name}: {failure}");
    }

    private static string? Registry_()
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(@"Software\WpfMacFixture");
        string stamp = DateTime.UtcNow.Ticks.ToString();
        key.SetValue("Stamp", stamp);
        key.SetValue("Count", 42, RegistryValueKind.DWord);
        if ((string?)key.GetValue("Stamp") != stamp) return "a string did not read back";
        if (key.GetValueKind("Count") != RegistryValueKind.DWord || (int)key.GetValue("Count")! != 42) return "a DWORD did not read back as one";
        Registry.CurrentUser.DeleteSubKeyTree(@"Software\WpfMacFixture");
        return null;
    }

    private static string? EventLog_()
    {
        string[] logs = EventLog.GetEventLogs().Select(l => l.Log).ToArray();
        return logs.Contains("Application") ? null : "no Application log among " + string.Join(", ", logs);
    }

    private static string? Wmi()
    {
        using var searcher = new ManagementObjectSearcher("SELECT NumberOfLogicalProcessors, TotalPhysicalMemory FROM Win32_ComputerSystem");
        foreach (ManagementBaseObject o in searcher.Get())
        {
            uint cpus = (uint)o["NumberOfLogicalProcessors"];
            ulong memory = (ulong)o["TotalPhysicalMemory"];
            Console.WriteLine($"fixture: WMI says {cpus} logical processors, {memory / (1 << 20)} MB");
            return cpus > 0 && memory > 0 ? null : "zero processors or memory";
        }
        return "Win32_ComputerSystem has no instance";
    }

    private static string? User32(IntPtr hwnd)
    {
        if (!IsWindow(hwnd)) return "IsWindow(form) is false";
        if (!GetWindowRect(hwnd, out RECT r) || r.Right <= r.Left || r.Bottom <= r.Top) return $"GetWindowRect gave {r.Left},{r.Top},{r.Right},{r.Bottom}";
        if (GetSystemMetrics(0) <= 0) return "GetSystemMetrics(SM_CXSCREEN) is 0";
        return null;
    }

    // ---- macOS: the native window and the form stay in step ---------------------------------------
    // What a user does to the window (here through AppKit, as a drag or resize would) must reach the
    // form -- its layout and its Move/Resize handlers -- and what code does to the form must reach
    // the window.

    private static void MacWindowFollows(Forms.Form form, string[] args, Action done)
    {
        IntPtr window = MacWindowTitled(form.Text);
        if (window == IntPtr.Zero)
        {
            Run("macOS window follows", () => "no NSWindow titled " + form.Text);
            done();
            return;
        }
        int moved = 0, resized = 0;
        form.Move += (_, _) => moved++;
        form.Resize += (_, _) => resized++;
        System.Drawing.Size before = form.Size;
        NSRect frame = SendRect(window, Sel("frame"));
        // Narrower and shorter, and 40 points to the right (Cocoa's origin is the bottom-left).
        SendVoidRectBool(window, Sel("setFrame:display:"), new NSRect { X = frame.X + 40, Y = frame.Y + 30, W = frame.W - 60, H = frame.H - 30 }, true);

        var step = new Forms.Timer { Interval = 500 };
        int phase = 0;
        System.Drawing.Point requested = default;
        step.Tick += (_, _) =>
        {
            if (phase == 0)
            {
                Run("user resize and move reach the form", () =>
                    form.Width != before.Width - 60 || form.Height != before.Height - 30 ? $"form is {form.Size}, expected {before.Width - 60}x{before.Height - 30}"
                    : resized == 0 ? "no Resize event" : moved == 0 ? "no Move event" : null);
                requested = new System.Drawing.Point(form.Left + 25, form.Top + 15);
                form.Location = requested;
                phase = 1;
                return;
            }
            step.Stop();
            Run("form moved from code moves the window", () =>
            {
                NSRect now = SendRect(window, Sel("frame"));
                NSRect content = SendRectRect(window, Sel("contentRectForFrameRect:"), now);
                double screenHeight = SendRect(SendPtrNUInt(Send(objc_getClass("NSScreen"), Sel("screens")), Sel("objectAtIndex:"), 0), Sel("frame")).H;
                int left = (int)Math.Round(content.X), top = (int)Math.Round(screenHeight - content.Y - content.H);
                return left == requested.X && top == requested.Y ? null : $"window content is at {left},{top}, form asked for {requested.X},{requested.Y}";
            });
            done();
        };
        step.Start();
    }

    // A real click (NSEvent, through the app's event queue) on a WinForms button must raise its
    // Click, wherever the window is on screen.
    private static void MacClickReachesControl(Forms.Form form, Forms.Button button, Action done)
    {
        IntPtr window = MacWindowTitled(form.Text);
        int clicks = 0;
        button.Click += (_, _) => clicks++;
        System.Drawing.Point inForm = form.PointToClient(button.PointToScreen(new System.Drawing.Point(button.Width / 2, button.Height / 2)));
        NSRect content = SendRect(Send(window, Sel("contentView")), Sel("frame"));
        var location = new NSPoint { X = inForm.X, Y = content.H - inForm.Y };
        nint number = (nint)Send(window, Sel("windowNumber"));
        IntPtr app = Send(objc_getClass("NSApplication"), Sel("sharedApplication"));
        foreach (nuint type in new nuint[] { 1, 2 })   // left mouse down, up
        {
            IntPtr evt = SendMouseEvent(objc_getClass("NSEvent"), Sel("mouseEventWithType:location:modifierFlags:timestamp:windowNumber:context:eventNumber:clickCount:pressure:"),
                                        type, location, 0, 0, number, IntPtr.Zero, 0, 1, type == 1 ? 1f : 0f);
            SendPostEvent(app, Sel("postEvent:atStart:"), evt, false);
        }
        var wait = new Forms.Timer { Interval = 700 };
        wait.Tick += (_, _) =>
        {
            wait.Stop();
            Run($"a click reaches the control under it (form at {form.Location})", () => clicks == 1 ? null : $"{clicks} Click events");
            done();
        };
        wait.Start();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NSPoint { public double X, Y; }

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial IntPtr SendMouseEvent(IntPtr cls, IntPtr sel, nuint type, NSPoint location, nuint flags, double timestamp, nint windowNumber, IntPtr context, nint eventNumber, nint clickCount, float pressure);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial void SendPostEvent(IntPtr app, IntPtr sel, IntPtr evt, [MarshalAs(UnmanagedType.U1)] bool atStart);

    private static IntPtr MacWindowTitled(string title)
    {
        IntPtr windows = Send(Send(objc_getClass("NSApplication"), Sel("sharedApplication")), Sel("windows"));
        nuint count = (nuint)Send(windows, Sel("count"));
        for (nuint i = 0; i < count; i++)
        {
            IntPtr w = SendPtrNUInt(windows, Sel("objectAtIndex:"), i);
            IntPtr t = Send(w, Sel("title"));
            if (t != IntPtr.Zero && Marshal.PtrToStringUTF8(Send(t, Sel("UTF8String"))) == title) return w;
        }
        return IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NSRect { public double X, Y, W, H; }

    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    private static IntPtr Sel(string name) => sel_registerName(name);
    [LibraryImport(ObjC, StringMarshalling = StringMarshalling.Utf8)] private static partial IntPtr objc_getClass(string name);
    [LibraryImport(ObjC, StringMarshalling = StringMarshalling.Utf8)] private static partial IntPtr sel_registerName(string name);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")] private static partial IntPtr Send(IntPtr r, IntPtr s);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")] private static partial IntPtr SendPtrNUInt(IntPtr r, IntPtr s, nuint a);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")] private static partial NSRect SendRect(IntPtr r, IntPtr s);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")] private static partial NSRect SendRectRect(IntPtr r, IntPtr s, NSRect a);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")] private static partial void SendVoidRectBool(IntPtr r, IntPtr s, NSRect a, [MarshalAs(UnmanagedType.U1)] bool b);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool IsWindow(IntPtr hwnd);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [LibraryImport("user32.dll")] private static partial int GetSystemMetrics(int index);
}
