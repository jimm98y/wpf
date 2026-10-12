// In-process screenshots for `wpfmac run --screenshots <dir>`, taken from the stack's own rendering
// rather than from the window server: macOS hands a process that has no screen-recording permission
// a blank image even of its own windows. Every few seconds each WPF window (RenderTargetBitmap of its
// root) and each top-level WinForms form (the frame its host presents) is saved as a PNG named
// <seconds>-<kind>-<handle>.png, so a run leaves its timeline behind.

using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Forms = System.Windows.Forms;

namespace WpfMac.Win32;

public static class Screenshots
{
    private static Timer? s_timer;
    private static readonly Stopwatch s_clock = Stopwatch.StartNew();

    public static void Start(string directory, int intervalSeconds)
    {
        Directory.CreateDirectory(directory);
        int ms = Math.Max(1, intervalSeconds) * 1000;
        s_timer = new Timer(_ => CaptureAll(directory), null, ms, ms);
    }

    private static void CaptureAll(string directory)
    {
        string stamp = ((int)s_clock.Elapsed.TotalSeconds).ToString("D4") + "s";
        try
        {
            foreach (PresentationSource ps in PresentationSource.CurrentSources)
            {
                if (ps is not HwndSource source || source.IsDisposed) continue;
                string path = Path.Combine(directory, $"{stamp}-wpf-{source.Handle:x}.png");
                source.Dispatcher.BeginInvoke(() => SaveWpf(source, path));
            }
        }
        catch (Exception e)
        {
            Exports.LogOnce("shots:wpf", "screenshots (WPF): " + e.Message);
        }

        try
        {
            Type? host = typeof(Forms.Form).Assembly.GetType("System.Windows.Forms.PresentationHost");
            if (host?.GetField("s_forms", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null) is System.Collections.IDictionary forms)
            {
                object? gate = host.GetField("s_lock", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);
                List<(object Host, Forms.Form Form)> snapshot = new();
                lock (gate ?? forms)
                {
                    foreach (System.Collections.DictionaryEntry e in forms)
                    {
                        if (e.Value is Forms.Form f && !f.IsDisposed) snapshot.Add((e.Key, f));
                    }
                }
                foreach (var (h, form) in snapshot)
                {
                    if (Environment.GetEnvironmentVariable("WPFMAC_DUMP_WINDOWS") == "1" && form.IsHandleCreated)
                    {
                        form.BeginInvoke(() => DumpWindows(form, stamp));
                    }
                    string path = Path.Combine(directory, $"{stamp}-winforms-{RuntimeHelpersId(form)}.png");
                    MethodInfo? save = h.GetType().GetMethod("SaveFrame", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (save == null || !form.IsHandleCreated) continue;
                    form.BeginInvoke(() => { try { save.Invoke(h, new object[] { path }); } catch { } });
                }
            }
        }
        catch (Exception e)
        {
            Exports.LogOnce("shots:winforms", "screenshots (WinForms): " + e.Message);
        }
    }

    /// <summary>WPFMAC_DUMP_WINDOWS=1: what the WinForms host composites for a form, to the compat log.</summary>
    private static void DumpWindows(Forms.Form form, string stamp)
    {
        try
        {
            object driver = typeof(Forms.Control).Assembly.GetType("System.Windows.Forms.XplatUI")!
                .GetField("driver", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
            var present = (long[])driver.GetType().GetMethod("GetPresentWindows", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(driver, new object[] { form.Handle })!;
            var lines = new System.Text.StringBuilder($"windows of '{form.Text}' at {stamp}:\n");
            for (int i = 0; i + 2 < present.Length; i += 3)
            {
                var c = Forms.Control.FromHandle((IntPtr)present[i]);
                lines.Append($"  0x{present[i]:x} at {present[i + 1]},{present[i + 2]} {c?.GetType().FullName ?? "?"} {c?.Bounds} visible={c?.Visible} parent={c?.Parent?.GetType().Name}\n");
            }
            Exports.LogOnce("dump:" + stamp + form.GetHashCode(), lines.ToString().TrimEnd());
        }
        catch (Exception e)
        {
            Exports.LogOnce("dump-fail", "window dump: " + e.Message);
        }
    }

    private static int RuntimeHelpersId(object o) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o);

    private static void SaveWpf(HwndSource source, string path)
    {
        if (source.IsDisposed || source.RootVisual is not FrameworkElement root || root.ActualWidth < 1 || root.ActualHeight < 1) return;
        var rtb = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth), (int)Math.Ceiling(root.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        rtb.Render(root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
