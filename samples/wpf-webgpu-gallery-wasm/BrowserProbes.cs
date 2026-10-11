// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Browser-only probes for the parts of the port that only this head can exercise on a Windows
// machine: the web view (an <iframe> overlay), the page's own prompts and pickers behind WPF's and
// WinForms' dialogs, and WindowsFormsHost on the browser's single thread. Off unless the page is
// opened with ?args=probes. Adds a panel over the gallery; every outcome is a console line starting
// "PROBE", and every button announces where it is ("PROBE-BTN <name> <x> <y>", CSS pixels) so a
// DevTools-protocol driver can click it with real input -- the pickers need a user gesture.
//

using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using SWF = System.Windows.Forms;

internal static class BrowserProbes
{
    private static Window s_window;
    private static WebBrowser s_web;

    internal static void Start(string[] args)
    {
        if (Array.IndexOf(args, "probes") < 0) return;
        var wait = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        wait.Tick += (s, e) =>
        {
            Window w = Application.Current?.MainWindow;
            if (w == null || !w.IsLoaded || w.Content is not Grid) return;
            wait.Stop();
            try { Build(w); }
            catch (Exception ex) { Console.WriteLine($"PROBE build FAILED {ex}"); }
        };
        wait.Start();
    }

    private static void Build(Window w)
    {
        s_window = w;
        var overlay = (Grid)w.Content;
        var buttons = new WrapPanel();
        var stack = new StackPanel();
        stack.Children.Add(buttons);
        var panel = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(30, 96, 0, 0),
            Width = 660,
            Padding = new Thickness(8),
            Background = Brushes.White,
            BorderBrush = Brushes.DarkSlateBlue,
            BorderThickness = new Thickness(2),
            Child = stack,
        };
        overlay.Children.Add(panel);

        Button Add(string name, Func<Task> run)
        {
            var b = new Button { Content = name, Margin = new Thickness(0, 0, 6, 6), Padding = new Thickness(8, 3, 8, 3) };
            b.Click += async (s, e) =>
            {
                Console.WriteLine($"PROBE click {name}");
                try { await run(); }
                catch (Exception ex) { Console.WriteLine($"PROBE {name} FAILED {ex.GetType().Name}: {ex.Message}"); }
            };
            buttons.Children.Add(b);
            return b;
        }

        // ---- WPF dialogs ----
        Add("wpfAlert", () =>
        {
            Console.WriteLine($"PROBE wpfAlert result={MessageBox.Show("Alert from WPF", "Probe")}");
            return Task.CompletedTask;
        });
        Add("wpfConfirm", () =>
        {
            Console.WriteLine($"PROBE wpfConfirm result={MessageBox.Show("Confirm from WPF?", "Probe", MessageBoxButton.OKCancel)}");
            return Task.CompletedTask;
        });
        Add("wpfYesNoAsync", async () =>
        {
            MessageBoxResult r = await MessageBox.ShowAsync("Yes or no?", "Probe", MessageBoxButton.YesNo);
            Console.WriteLine($"PROBE wpfYesNoAsync result={r}");
        });
        Add("wpfOpen", async () =>
        {
            var d = new Microsoft.Win32.OpenFileDialog { Filter = "Text|*.txt|All|*.*" };
            bool? ok = await d.ShowDialogAsync();
            string text = ok == true ? System.IO.File.ReadAllText(d.FileName) : null;
            Console.WriteLine($"PROBE wpfOpen ok={ok} file={d.FileName} text={text}");
        });
        Add("wpfSave", async () =>
        {
            var d = new Microsoft.Win32.SaveFileDialog { FileName = "probe-wpf.txt" };
            bool? ok = await d.ShowDialogAsync();
            Console.WriteLine($"PROBE wpfSave ok={ok} file={d.FileName}");
            if (ok == true)
            {
                using (var s = d.OpenFile())
                using (var sw = new System.IO.StreamWriter(s))
                    sw.Write("saved by WPF");
                Console.WriteLine($"PROBE wpfSave wrote {new System.IO.FileInfo(d.FileName).Length} bytes");
                // The browser's last step: hand the written file to the user as a download.
                Console.WriteLine($"PROBE wpfSave committed={await d.CommitAsync()}");
            }
        });
        Add("wpfSyncOpen", () =>
        {
            // Must refuse loudly on this head, not report a cancellation.
            bool? ok = new Microsoft.Win32.OpenFileDialog().ShowDialog();
            Console.WriteLine($"PROBE wpfSyncOpen returned {ok}");
            return Task.CompletedTask;
        });

        // ---- WinForms dialogs, through the bridges a WindowsFormsHost installs ----
        var wfPanel = new SWF.FlowLayoutPanel { Width = 640, Height = 34, BackColor = System.Drawing.Color.FromArgb(0xE8, 0xEE, 0xF8) };
        SWF.Button WfButton(string name, Func<Task> run)
        {
            var b = new SWF.Button { Text = name, Width = 96, Height = 26 };
            b.Click += async (s, e) =>
            {
                Console.WriteLine($"PROBE click {name}");
                try { await run(); }
                catch (Exception ex) { Console.WriteLine($"PROBE {name} FAILED {ex.GetType().Name}: {ex.Message}"); }
            };
            wfPanel.Controls.Add(b);
            return b;
        }
        var wfColor = WfButton("wfColor", async () =>
        {
            var d = new SWF.ColorDialog { Color = System.Drawing.Color.FromArgb(0x11, 0x22, 0x33) };
            SWF.DialogResult r = await d.ShowDialogAsync();
            Console.WriteLine($"PROBE wfColor result={r} color=#{d.Color.R:x2}{d.Color.G:x2}{d.Color.B:x2}");
        });
        var wfColorSync = WfButton("wfColorSync", () =>
        {
            SWF.DialogResult r = new SWF.ColorDialog().ShowDialog();
            Console.WriteLine($"PROBE wfColorSync returned {r}");
            return Task.CompletedTask;
        });
        var wfMsg = WfButton("wfMsg", async () =>
        {
            SWF.DialogResult r = await SWF.MessageBox.ShowAsync("OK/Cancel from WinForms?", "Probe", SWF.MessageBoxButtons.OKCancel);
            Console.WriteLine($"PROBE wfMsg result={r}");
        });
        var wfOpen = WfButton("wfOpen", async () =>
        {
            var d = new SWF.OpenFileDialog();
            SWF.DialogResult r = await d.ShowDialogAsync();
            Console.WriteLine($"PROBE wfOpen result={r} file={d.FileName}");
        });
        var wfSave = WfButton("wfSave", async () =>
        {
            var d = new SWF.SaveFileDialog { FileName = "probe-wf.txt" };
            SWF.DialogResult r = await d.ShowDialogAsync();
            Console.WriteLine($"PROBE wfSave result={r} file={d.FileName}");
            if (r == SWF.DialogResult.OK)
            {
                System.IO.File.WriteAllText(d.FileName, "saved by WinForms");
                Console.WriteLine($"PROBE wfSave committed={await d.CommitAsync()}");
            }
        });
        var wfh = new System.Windows.Forms.Integration.WindowsFormsHost { Child = wfPanel, Width = 640, Height = 34, Margin = new Thickness(0, 2, 0, 6) };
        stack.Children.Add(wfh);

        // ---- WebBrowser ----
        var status = new TextBlock { Text = "web: -", Margin = new Thickness(0, 0, 0, 4) };
        stack.Children.Add(status);
        s_web = new WebBrowser { Width = 640, Height = 200 };
        s_web.ObjectForScripting = new ScriptBridge();
        s_web.Navigating += (s, e) => Console.WriteLine($"PROBE web Navigating {e.Uri}");
        s_web.Navigated += (s, e) => Console.WriteLine($"PROBE web Navigated {e.Uri}");
        s_web.LoadCompleted += (s, e) =>
        {
            Console.WriteLine($"PROBE web LoadCompleted {e.Uri} source={s_web.Source}");
            status.Text = "web: loaded " + (e.Uri?.ToString() ?? "(string)");
        };
        stack.Children.Add(s_web);

        Add("webString", () =>
        {
            s_web.NavigateToString(
                "<html><head><title>probe page</title></head><body style='background:#ffe9a8;font:16px sans-serif'>" +
                "<h3 id='h'>NavigateToString page</h3>" +
                "<script>function add(a,b){return a+b;}" +
                "window.addEventListener('message',function(e){document.getElementById('h').textContent='got: '+JSON.stringify(e.data);});" +
                "</script></body></html>");
            return Task.CompletedTask;
        });
        Add("webScript", () =>
        {
            object sum = s_web.InvokeScript("add", 2, 3);
            Console.WriteLine($"PROBE webScript add(2,3)={sum}");
            object title = s_web.InvokeScript("eval", "document.title");
            Console.WriteLine($"PROBE webScript title={title}");
            return Task.CompletedTask;
        });
        Add("webExternal", () =>
        {
            // The page calls back into ObjectForScripting through window.external.
            s_web.InvokeScript("eval", "window.external.Greet('from the page').then(function(r){document.getElementById('h').textContent='external said: '+r;})");
            return Task.CompletedTask;
        });
        Add("webNavigate", () =>
        {
            s_web.Navigate(new Uri(new Uri(BrowserLocation()), "probe-frame.html"));
            return Task.CompletedTask;
        });
        Add("webCross", () =>
        {
            s_web.Navigate(new Uri("https://example.com/"));
            return Task.CompletedTask;
        });
        Add("webCrossScript", () =>
        {
            object r = s_web.InvokeScript("eval", "document.title");
            Console.WriteLine($"PROBE webCrossScript returned {r}");
            return Task.CompletedTask;
        });
        Add("webHide", () =>
        {
            s_web.Visibility = s_web.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
            Console.WriteLine($"PROBE webHide visibility={s_web.Visibility}");
            return Task.CompletedTask;
        });

        // ---- WebView2 (the fork's Microsoft.Web.WebView2.Wpf over the same seam) ----
        var wv2 = new Microsoft.Web.WebView2.Wpf.WebView2 { Width = 640, Height = 120, Margin = new Thickness(0, 6, 0, 0) };
        stack.Children.Add(wv2);
        Add("wv2Init", async () =>
        {
            await wv2.EnsureCoreWebView2Async();
            wv2.CoreWebView2.WebMessageReceived += (s, e) =>
            {
                string text;
                try { text = e.TryGetWebMessageAsString(); } catch (Exception ex) { text = "<" + ex.GetType().Name + ">"; }
                Console.WriteLine($"PROBE wv2 WebMessageReceived string={text} json={e.WebMessageAsJson}");
            };
            wv2.CoreWebView2.NavigationCompleted += (s, e) => Console.WriteLine($"PROBE wv2 NavigationCompleted success={e.IsSuccess}");
            wv2.NavigateToString(
                "<html><body style='background:#d8e8ff;font:15px sans-serif'><h3 id='h'>WebView2 page</h3><script>" +
                "window.chrome.webview.addEventListener('message', function (e) {" +
                "  document.getElementById('h').textContent = 'host said: ' + e.data;" +
                "  window.chrome.webview.postMessage('echo:' + e.data); });" +
                "window.chrome.webview.postMessage('hello from the page');" +
                "</script></body></html>");
            Console.WriteLine("PROBE wv2Init done");
        });
        Add("wv2Post", () =>
        {
            wv2.CoreWebView2.PostWebMessageAsString("ping");
            return Task.CompletedTask;
        });
        Add("wv2Script", async () =>
        {
            string r = await wv2.CoreWebView2.ExecuteScriptAsync("document.getElementById('h').textContent");
            Console.WriteLine($"PROBE wv2Script result={r}");
        });

        // ---- WinForms' WebBrowser and WebView2, inside a WindowsFormsHost ----
        // Their iframes must follow the WinForms control within the host, the host within the WPF
        // window, and the visibility of both.
        var wfWebPanel = new SWF.Panel { Width = 640, Height = 90, BackColor = System.Drawing.Color.FromArgb(0xF0, 0xE6, 0xD8) };
        wfWebPanel.Controls.Add(new SWF.Label { Text = "WinForms web:", Left = 4, Top = 36, Width = 100 });
        var wfWeb = new SWF.WebBrowser { Left = 110, Top = 2, Width = 250, Height = 86 };
        var wfWv2 = new Microsoft.Web.WebView2.WinForms.WebView2 { Left = 370, Top = 2, Width = 266, Height = 86 };
        wfWebPanel.Controls.Add(wfWeb);
        wfWebPanel.Controls.Add(wfWv2);
        var wfWebHost = new System.Windows.Forms.Integration.WindowsFormsHost
        {
            Child = wfWebPanel, Width = 640, Height = 90, Margin = new Thickness(0, 6, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        stack.Children.Add(wfWebHost);
        Add("wfWebLoad", async () =>
        {
            wfWeb.DocumentText = "<html><body style='margin:0;background:#cfe8cf;font:14px sans-serif'>" +
                "<div style='border:3px solid #2a7a2a;height:calc(100vh - 6px);box-sizing:border-box;padding:4px'>WinForms WebBrowser</div></body></html>";
            await wfWv2.EnsureCoreWebView2Async();
            wfWv2.NavigateToString("<html><body style='margin:0;background:#e8cfe8;font:14px sans-serif'>" +
                "<div style='border:3px solid #7a2a7a;height:calc(100vh - 6px);box-sizing:border-box;padding:4px'>WinForms WebView2</div></body></html>");
            Console.WriteLine($"PROBE wfWebLoad web={wfWeb.Bounds} wv2={wfWv2.Bounds}");
        });
        Add("wfWebHide", () =>
        {
            wfWeb.Visible = !wfWeb.Visible;
            Console.WriteLine($"PROBE wfWebHide visible={wfWeb.Visible}");
            return Task.CompletedTask;
        });
        Add("wfWebMove", () =>
        {
            bool moved = wfWeb.Left != 110;
            wfWeb.SetBounds(moved ? 110 : 150, moved ? 2 : 20, moved ? 250 : 180, moved ? 86 : 60);
            Console.WriteLine($"PROBE wfWebMove bounds={wfWeb.Bounds}");
            return Task.CompletedTask;
        });
        Add("wfHostHide", () =>
        {
            wfWebHost.Visibility = wfWebHost.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
            Console.WriteLine($"PROBE wfHostHide visibility={wfWebHost.Visibility}");
            return Task.CompletedTask;
        });
        Add("wfHostMove", () =>
        {
            wfWebHost.Margin = new Thickness(wfWebHost.Margin.Left == 0 ? 60 : 0, 6, 0, 0);
            Console.WriteLine($"PROBE wfHostMove margin={wfWebHost.Margin.Left}");
            return Task.CompletedTask;
        });

        // ---- RenderTargetBitmap: the readback is a Promise here ----
        var rtbRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        var rtbImage = new Image { Width = 200, Height = 60, Stretch = Stretch.Fill };
        var rtbSample = new Border
        {
            Width = 200, Height = 60,
            Background = new LinearGradientBrush(Colors.OrangeRed, Colors.SteelBlue, 0),
            Child = new TextBlock { Text = "RTB source", FontSize = 20, Foreground = Brushes.White, Margin = new Thickness(8) },
        };
        rtbRow.Children.Add(rtbSample);
        rtbRow.Children.Add(new TextBlock { Text = " -> ", VerticalAlignment = VerticalAlignment.Center });
        rtbRow.Children.Add(new Border { BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), Child = rtbImage });
        stack.Children.Add(rtbRow);
        static string Census(System.Windows.Media.Imaging.RenderTargetBitmap rtb)
        {
            var px = new byte[rtb.PixelWidth * rtb.PixelHeight * 4];
            rtb.CopyPixels(px, rtb.PixelWidth * 4, 0);
            long opaque = 0;
            for (int i = 3; i < px.Length; i += 4) if (px[i] != 0) opaque++;
            int c = (10 * rtb.PixelWidth + 10) * 4;
            return $"opaque={opaque}/{px.Length / 4} px(10,10)=B{px[c]},G{px[c + 1]},R{px[c + 2]},A{px[c + 3]}";
        }
        static System.Windows.Media.Imaging.RenderTargetBitmap NewRtb()
            => new System.Windows.Media.Imaging.RenderTargetBitmap(200, 60, 96, 96, PixelFormats.Pbgra32);
        Add("rtbSync", async () =>
        {
            // What existing code does: Render, then read straight away. On this head the read sees
            // the previous contents; the Image still updates when the pixels land.
            var rtb = NewRtb();
            rtb.Render(rtbSample);
            Console.WriteLine($"PROBE rtbSync immediately {Census(rtb)}");
            rtbImage.Source = rtb;
            await Task.Delay(500);
            Console.WriteLine($"PROBE rtbSync later {Census(rtb)}");
        });
        Add("rtbAsync", async () =>
        {
            var rtb = NewRtb();
            await rtb.RenderAsync(rtbSample);
            Console.WriteLine($"PROBE rtbAsync awaited {Census(rtb)}");
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
            using var ms = new System.IO.MemoryStream();
            enc.Save(ms);
            Console.WriteLine($"PROBE rtbAsync png={ms.Length} bytes");
            rtbImage.Source = rtb;
        });
        Add("rtbTwice", async () =>
        {
            // A second Render before the first readback lands: the later one wins, and awaiting
            // the earlier one still ends with the bitmap rendered.
            var rtb = NewRtb();
            Task first = rtb.RenderAsync(rtbSample);
            Task second = rtb.RenderAsync(rtbSample);
            await first;
            Console.WriteLine($"PROBE rtbTwice first {Census(rtb)} secondDone={second.IsCompleted}");
            await second;
            rtb.Clear();
            Console.WriteLine($"PROBE rtbTwice cleared {Census(rtb)}");
        });

        // Tell the driver where everything is, once layout has placed it.
        var announce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        announce.Tick += (s, e) =>
        {
            announce.Stop();
            foreach (UIElement c in buttons.Children)
                if (c is Button b) Announce((string)b.Content, b, b.ActualWidth / 2, b.ActualHeight / 2);
            foreach (SWF.Control c in wfPanel.Controls)
                Announce(c.Text, wfh, c.Left + c.Width / 2.0, c.Top + c.Height / 2.0);
            Announce("webview", s_web, 0, 0);
            Console.WriteLine("PROBE ready");
        };
        announce.Start();
    }

    private static void Announce(string name, FrameworkElement e, double x, double y)
    {
        Point p = e.TransformToAncestor(s_window).Transform(new Point(x, y));
        Console.WriteLine($"PROBE-BTN {name} {p.X:0} {p.Y:0}");
    }

    private static string BrowserLocation()
        => (string)System.Runtime.InteropServices.JavaScript.JSHost.GlobalThis.GetPropertyAsJSObject("location").GetPropertyAsString("href");

    [ComVisible(true)]
    public sealed class ScriptBridge
    {
        public string Greet(string who)
        {
            Console.WriteLine($"PROBE web external Greet({who})");
            return "hello " + who;
        }
    }
}
