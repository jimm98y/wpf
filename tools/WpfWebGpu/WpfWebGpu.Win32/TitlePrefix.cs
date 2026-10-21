// `wpfrun run --title-prefix "[WebGPU] "`: puts a prefix on every top-level window title of the app,
// so that the WebGPU instance can be told from the stock one in `wpfrun compare` (and in a task bar).
// WPF windows and WinForms forms alike, including ones that change their title later. Polled, twice a
// second, on each window's own thread: there is no single event that covers both stacks and every
// way an app sets a title.

using System.Windows;
using System.Windows.Interop;
using Forms = System.Windows.Forms;

namespace WpfWebGpu.Win32;

public static class TitlePrefix
{
    private static Timer? s_timer;
    private static string s_prefix = "";

    public static void Install(string prefix)
    {
        if (string.IsNullOrEmpty(prefix)) return;
        s_prefix = prefix;
        s_timer = new Timer(_ => Apply(), null, 250, 500);
    }

    private static void Apply()
    {
        try
        {
            foreach (PresentationSource ps in PresentationSource.CurrentSources)
            {
                if (ps is HwndSource { IsDisposed: false, RootVisual: Window window })
                {
                    window.Dispatcher.BeginInvoke(() =>
                    {
                        string title = window.Title ?? "";
                        if (!title.StartsWith(s_prefix, StringComparison.Ordinal)) window.Title = s_prefix + title;
                    });
                }
            }
        }
        catch
        {
        }

        try
        {
            foreach (Forms.Form form in Forms.Application.OpenForms.Cast<Forms.Form>().ToArray())
            {
                if (form.IsDisposed || !form.IsHandleCreated || form.Parent != null || !form.TopLevel) continue;
                form.BeginInvoke(() =>
                {
                    string text = form.Text ?? "";
                    if (!text.StartsWith(s_prefix, StringComparison.Ordinal)) form.Text = s_prefix + text;
                });
            }
        }
        catch
        {
        }
    }
}
