// WPF inside Windows Forms — WPF on WebGPU.
//
// Deliberately an ORDINARY Windows Forms app: it builds a Form, drops a
// System.Windows.Forms.Integration.ElementHost on it, sets ElementHost.Child to a WPF element tree,
// and calls Application.Run. There is no host loop, no windowing code and no compositor code here --
// that all lives in the WinForms assembly and in ElementHost, which is the point: existing
// WinForms+WPF code compiles and runs on this stack unchanged.
//
// What it demonstrates beyond "it draws":
//   * live WPF animation inside the WinForms window (the spinner runs off a WPF DispatcherTimer);
//   * WinForms -> WPF: the trackbar drives a WPF Slider, the radios mutate a shared SolidColorBrush,
//     the checkbox adds/removes a WPF DropShadowEffect;
//   * WPF -> WinForms: clicking the WPF button updates a WinForms label;
//   * real input on both sides — the WPF tree is a real HwndSource, so it hit-tests, hovers,
//     focuses and takes the keyboard for itself.
//
// Run:  WpfInWinForms.exe [seconds] [selftest] [mouse]

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using SD = System.Drawing;
using SWF = System.Windows.Forms;
using System.Windows.Forms.Integration;

internal static class Program
{
    private static int s_wpfClicks;
    private static SWF.Label s_fromWpf;
    private static int s_exitCode;

    [STAThread]
    private static int Main(string[] args)
    {
        // The WinForms GPU-raster paint path and WPF's managed WebGPU compositor. Both must be set
        // before either stack initializes. (An SDK-built app gets these from the SDK instead.)
        Environment.SetEnvironmentVariable("WF_GPU_RASTER", "1");
        if (Environment.GetEnvironmentVariable("WPF_USE_WEBGPU_COMPOSITION") == null)
            Environment.SetEnvironmentVariable("WPF_USE_WEBGPU_COMPOSITION", "1");

        SWF.Application.ThreadException += (s, e) => Console.Error.WriteLine("THREADEX: " + e.Exception);
        SWF.Application.SetCompatibleTextRenderingDefault(false);

        int seconds = args.Length > 0 && int.TryParse(args[0], out int sec) ? sec : 0;
        bool mouseTest = Array.IndexOf(args, "mouse") >= 0;
        bool selftest = mouseTest || Array.IndexOf(args, "selftest") >= 0;

        // ---- the WinForms side ------------------------------------------------------------
        var form = new SWF.Form
        {
            Text = "WPF inside Windows Forms — WebGPU",
            Width = 880,
            Height = 560,
            BackColor = SD.Color.FromArgb(0xEC, 0xEC, 0xE4),
        };

        var banner = new SWF.Label
        {
            Text = "Windows Forms host — the panel on the right is WPF",
            Left = 16, Top = 12, Width = 520, Height = 20,
        };
        var group = new SWF.GroupBox { Text = "WinForms controls", Left = 16, Top = 40, Width = 380, Height = 190 };
        var check = new SWF.CheckBox { Text = "Show the WPF drop shadow", Left = 14, Top = 26, Width = 320, Checked = true };
        var radioA = new SWF.RadioButton { Text = "Teal", Left = 14, Top = 56, Width = 120, Checked = true };
        var radioB = new SWF.RadioButton { Text = "Amber", Left = 140, Top = 56, Width = 120 };
        var sliderLabel = new SWF.Label { Text = "Drive the WPF gauge:", Left = 14, Top = 92, Width = 200 };
        var track = new SWF.TrackBar { Left = 12, Top = 112, Width = 350, Minimum = 0, Maximum = 100, Value = 62, TickFrequency = 10 };
        var wfButton = new SWF.Button { Text = "WinForms button", Left = 14, Top = 152, Width = 160, Height = 28 };
        var wfCount = new SWF.Label { Text = "WinForms clicks: 0", Left = 186, Top = 158, Width = 180 };
        group.Controls.AddRange(new SWF.Control[] { check, radioA, radioB, sliderLabel, track, wfButton, wfCount });

        var echoGroup = new SWF.GroupBox { Text = "From the WPF side", Left = 16, Top = 240, Width = 380, Height = 90 };
        s_fromWpf = new SWF.Label { Text = "The WPF button has not been clicked yet.", Left = 14, Top = 28, Width = 350, Height = 40 };
        echoGroup.Controls.Add(s_fromWpf);

        var typeLabel = new SWF.Label { Text = "WinForms text box (keeps its own caret):", Left = 16, Top = 344, Width = 320 };
        var typeBox = new SWF.TextBox { Left = 16, Top = 366, Width = 380, Text = "type here" };

        form.Controls.AddRange(new SWF.Control[] { banner, group, echoGroup, typeLabel, typeBox });

        // ---- the WPF side, hosted by the stock ElementHost API -----------------------------
        WpfPanel wpf = BuildWpfPanel();
        var host = new ElementHost
        {
            Left = 420, Top = 40, Width = 424, Height = 440,
            Child = wpf.Root,
        };
        form.Controls.Add(host);

        // ---- the two directions of interaction ---------------------------------------------
        int wfClicks = 0;
        wfButton.Click += (s, e) => { wfClicks++; wfCount.Text = $"WinForms clicks: {wfClicks}"; wfCount.Invalidate(); };
        track.ValueChanged += (s, e) => wpf.Gauge.Value = track.Value;                 // WinForms -> WPF
        check.CheckedChanged += (s, e) => wpf.Card.Effect = check.Checked ? wpf.Shadow : null;
        radioA.CheckedChanged += (s, e) => { if (radioA.Checked) wpf.SetAccent(Color.FromRgb(0x0E, 0x7C, 0x86)); };
        radioB.CheckedChanged += (s, e) => { if (radioB.Checked) wpf.SetAccent(Color.FromRgb(0xC2, 0x77, 0x0A)); };
        wpf.Button.Click += (s, e) =>                                                   // WPF -> WinForms
        {
            s_wpfClicks++;
            s_fromWpf.Text = $"The WPF button was clicked {s_wpfClicks} time(s).";
            s_fromWpf.Invalidate();
        };
        wpf.Gauge.Value = track.Value;

        if (Environment.GetEnvironmentVariable("WF_TRACE_INPUT") == "1")
        {
            wpf.Button.MouseEnter += (s, e) => Console.WriteLine("wpf trace: MouseEnter");
            wpf.Button.PreviewMouseLeftButtonDown += (s, e) => Console.WriteLine($"wpf trace: down over={wpf.Button.IsMouseOver}");
            wpf.Button.PreviewMouseLeftButtonUp += (s, e) => Console.WriteLine($"wpf trace: up captured={System.Windows.Input.Mouse.Captured != null} pressed={wpf.Button.IsPressed}");
        }

        if (seconds > 0) AutoClose(form, seconds);
        if (selftest) SelfTest.Arm(form, host, wpf, wfButton, track, radioB, check, mouseTest,
                                   () => wfClicks, () => s_wpfClicks, code => s_exitCode = code);

        SWF.Application.Run(form);
        Console.WriteLine($"done. winforms clicks={wfClicks} wpf clicks={s_wpfClicks}");
        return s_exitCode;
    }

    private static void AutoClose(SWF.Form form, int seconds)
    {
        var timer = new SWF.Timer { Interval = seconds * 1000 };
        timer.Tick += (s, e) => { timer.Stop(); form.Close(); };
        timer.Start();
    }

    // The hosted WPF tree, plus the handles the WinForms side drives it through.
    internal sealed class WpfPanel
    {
        internal FrameworkElement Root;
        internal Border Card;
        internal DropShadowEffect Shadow;
        internal Button Button;
        internal Slider Gauge;
        internal SolidColorBrush Accent;
        // One brush shared by the heading and the bar: mutating its Color is the classic WPF way to
        // re-theme a tree, and it must reach the screen without any element being touched.
        internal void SetAccent(Color c) => Accent.Color = c;
    }

    private static WpfPanel BuildWpfPanel()
    {
        var p = new WpfPanel { Accent = new SolidColorBrush(Color.FromRgb(0x0E, 0x7C, 0x86)) };

        var title = new TextBlock
        {
            Text = "This is WPF",
            FontSize = 26,
            FontWeight = FontWeights.SemiBold,
            Foreground = p.Accent,
        };
        var subtitle = new TextBlock
        {
            Text = "Rendered by the managed WebGPU compositor, composited into the WinForms frame in the same pass.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 14),
            Foreground = new SolidColorBrush(Color.FromRgb(0x44, 0x4C, 0x59)),
        };

        // Vector content: the thing WinForms cannot draw without a lot of help.
        var spin = new RotateTransform();
        var petals = new Canvas { Width = 120, Height = 120, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = spin };
        for (int i = 0; i < 6; i++)
        {
            var petal = new Ellipse
            {
                Width = 44, Height = 96,
                Fill = new LinearGradientBrush(Color.FromArgb(0xCC, 0x2E, 0xA8, 0xB4), Color.FromArgb(0x66, 0x7A, 0x4B, 0xD8), 60),
                Opacity = 0.75,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new RotateTransform(i * 30),
            };
            Canvas.SetLeft(petal, 38);
            Canvas.SetTop(petal, 12);
            petals.Children.Add(petal);
        }

        p.Gauge = new Slider { Minimum = 0, Maximum = 100, Value = 62, Width = 200 };
        var gaugeText = new TextBlock { Margin = new Thickness(0, 2, 0, 0), Foreground = Brushes.DimGray };
        p.Gauge.ValueChanged += (s, e) => gaugeText.Text = $"driven from WinForms: {p.Gauge.Value:0}";
        gaugeText.Text = $"driven from WinForms: {p.Gauge.Value:0}";

        var bar = new Rectangle { Height = 14, RadiusX = 7, RadiusY = 7, Fill = p.Accent, HorizontalAlignment = HorizontalAlignment.Left };
        p.Gauge.ValueChanged += (s, e) => bar.Width = 2 + p.Gauge.Value * 1.98;
        bar.Width = 2 + p.Gauge.Value * 1.98;

        p.Button = new Button
        {
            Content = "WPF button — click me",
            Padding = new Thickness(14, 7, 14, 7),
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 14, 0, 0),
        };
        var wpfBox = new TextBox { Margin = new Thickness(0, 10, 0, 0), Text = "WPF text box" };

        var stack = new StackPanel { Margin = new Thickness(20) };
        stack.Children.Add(title);
        stack.Children.Add(subtitle);
        stack.Children.Add(petals);
        stack.Children.Add(new TextBlock { Text = "Gauge", Margin = new Thickness(0, 14, 0, 2), FontWeight = FontWeights.SemiBold });
        stack.Children.Add(p.Gauge);
        stack.Children.Add(gaugeText);
        stack.Children.Add(bar);
        stack.Children.Add(p.Button);
        stack.Children.Add(wpfBox);

        p.Shadow = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 3, Opacity = 0.28, Direction = 270 };
        p.Card = new Border
        {
            Background = Brushes.White,
            CornerRadius = new CornerRadius(12),
            Margin = new Thickness(10),
            Child = stack,
            Effect = p.Shadow,
        };
        // The root paints the area behind the card; it must match the WinForms form colour, because
        // the WPF window covers this control's whole rect.
        p.Root = new Border { Background = new SolidColorBrush(Color.FromRgb(0xEC, 0xEC, 0xE4)), Child = p.Card };

        // Live WPF animation on WPF's own clock, inside the WinForms window.
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        double angle = 0;
        timer.Tick += (s, e) => { angle = (angle + 2.5) % 360; spin.Angle = angle; };
        timer.Start();
        return p;
    }
}
