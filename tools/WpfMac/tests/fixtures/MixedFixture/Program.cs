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
        form.Controls.Add(log);
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
                foreach (string f in s_failures) log.Items.Add("FAIL " + f);
                Console.WriteLine(s_failures.Count == 0 ? "fixture: PASS" : "fixture: FAILED");
                window.Close();
                int i = Array.IndexOf(args, "--seconds");
                if (i >= 0 && i + 1 < args.Length && double.TryParse(args[i + 1], out double seconds))
                {
                    var close = new Forms.Timer { Interval = (int)(seconds * 1000) };
                    close.Tick += (_, _) => { close.Stop(); form.Close(); };
                    close.Start();
                }
            };
            check.Start();
        };

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

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool IsWindow(IntPtr hwnd);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [LibraryImport("user32.dll")] private static partial int GetSystemMetrics(int index);
}
