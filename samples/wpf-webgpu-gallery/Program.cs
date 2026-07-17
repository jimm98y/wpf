// WPF on WebGPU — Feature Gallery.
//
// A code-only WPF app (no XAML markup compiler) that showcases the managed WebGPU
// compositor: each card exercises one capability the wgpu-native backend decodes and
// renders from WPF's real MILCMD stream. Run against a from-source WPF + the backend
// with WPF_USE_WEBGPU_COMPOSITION=1 (see run-gallery.ps1).

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Shapes;
using System.Windows.Threading;

internal static class Program
{
    private static readonly Color Ink = Color.FromRgb(0x22, 0x28, 0x33);
    private static readonly Color CardBg = Colors.White;


    // internal (not private): the WebAssembly head (wpf-webgpu-gallery-wasm) compiles this
    // file and chains here from its own async entry point after GPU pre-initialization.
    [STAThread]
    internal static int Main(string[] args)
    {
        // Enable the managed WebGPU compositor (the Windows run script and the browser boot also set this;
        // setting it here lets the macOS head just launch the exe).
        if (Environment.GetEnvironmentVariable("WPF_USE_WEBGPU_COMPOSITION") == null)
            Environment.SetEnvironmentVariable("WPF_USE_WEBGPU_COMPOSITION", "1");

        var app = new Application();

        // Animated / interactive controls (state driven by the timer below -> live re-composition).
        var progress = new ProgressBar { Minimum = 0, Maximum = 100, Value = 0, Height = 18 };
        var liveSlider = new Slider { Minimum = 0, Maximum = 100, Value = 50 };
        var autoCheck = new CheckBox { Content = "Live re-render", IsChecked = true, Margin = new Thickness(0, 10, 0, 0) };
        ListBox list = MakeList();
        TabControl tabs = MakeTabs();

        // Interactive controls used to reproduce/verify hover + popup visual states.
        var hoverCheck = new CheckBox { Content = "Hover me", IsChecked = true, Margin = new Thickness(0, 6, 0, 10) };
        var stateCombo = new ComboBox { Margin = new Thickness(0, 0, 0, 8) };
        stateCombo.Items.Add("wgpu-native");
        stateCombo.Items.Add("Vulkan");
        stateCombo.Items.Add("Direct3D 12");
        stateCombo.SelectedIndex = 0;
        var clickCount = 0;
        var clickLabel = new TextBlock { Text = "Clicks: 0", FontSize = 12, Margin = new Thickness(0, 4, 0, 0) };
        var clickButton = new Button { Content = "Click / tap me", Padding = new Thickness(12, 5, 12, 5), HorizontalAlignment = HorizontalAlignment.Left };
        clickButton.Click += (s, e) => { clickCount++; clickLabel.Text = $"Clicks: {clickCount}"; };
        var echoBox = new TextBox { Margin = new Thickness(0, 6, 0, 4) };
        var interactive = new StackPanel { Width = 196 };
        interactive.Children.Add(clickButton);
        interactive.Children.Add(clickLabel);
        interactive.Children.Add(hoverCheck);
        interactive.Children.Add(stateCombo);
        interactive.Children.Add(echoBox);

        var cards = new WrapPanel { Margin = new Thickness(16, 8, 16, 16) };
        cards.Children.Add(Card("Interactive", interactive));
        // A Mono WinForms control tree hosted via WindowsFormsHost — its scene composites DIRECTLY into
        // this same WebGPU frame (no bitmap). Shared across all heads (mac / Windows / browser).
        cards.Children.Add(Card("WinForms (WindowsFormsHost)", WinFormsHost.CreateDemoCardOrFallback()));
        cards.Children.Add(Card("Shapes", ShapesDemo()));
        cards.Children.Add(Card("Gradients", GradientsDemo()));
        cards.Children.Add(Card("Strokes & dashes", StrokesDemo()));
        cards.Children.Add(Card("Paths & geometry", PathsDemo()));
        cards.Children.Add(Card("Text", TextDemo()));
        cards.Children.Add(Card("Transforms", TransformsDemo(out RotateTransform spin)));
        cards.Children.Add(Card("Clipping", ClippingDemo()));
        cards.Children.Add(Card("Effects", EffectsDemo()));
        cards.Children.Add(Card("Images", ImageDemo()));
        cards.Children.Add(Card("Tile brushes", TileBrushDemo()));
        cards.Children.Add(Card("Opacity mask", OpacityMaskDemo()));
        cards.Children.Add(Card("Buttons", ButtonsDemo()));
        cards.Children.Add(Card("Text input", TextInputDemo()));
        cards.Children.Add(Card("Toggles", TogglesDemo()));
        cards.Children.Add(Card("Range & lists", RangeDemo()));
        cards.Children.Add(Card("3D (Viewport3D)", Viewport3DDemo(out AxisAngleRotation3D rot3d)));
        cards.Children.Add(Card("Tab control", Fixed(tabs)));
        cards.Children.Add(Card("Selectable list", Fixed(list)));
        cards.Children.Add(Card("Menu & tree", MenuTreeDemo(out MenuItem fileMenuItem)));
        cards.Children.Add(Card("Live values", LiveDemo(progress, liveSlider, autoCheck)));

        var scroll = new ScrollViewer
        {
            Content = cards,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };

        var root = new DockPanel { LastChildFill = true };
        UIElement header = Header();
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        root.Children.Add(scroll);

        // Always-visible FPS overlay pinned to the top-right corner. It floats above the whole UI
        // (last child of an overlay Grid = top of the z-order) and is non-hit-testable so it never
        // steals input. The rate is measured from CompositionTarget.Rendering — one tick per frame
        // the composition system renders — which is the true render/present cadence on this path.
        var fpsText = new TextBlock
        {
            Text = "— fps",
            FontSize = 13,
            FontFamily = new FontFamily("Menlo, Consolas, Courier New, monospace"),
            Foreground = Brushes.White,
        };
        var fpsBadge = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 12, 14, 0),
            Padding = new Thickness(10, 4, 10, 4),
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Color.FromArgb(0xB8, 0x11, 0x18, 0x27)),
            Child = fpsText,
            IsHitTestVisible = false,
        };
        var overlay = new Grid();
        overlay.Children.Add(root);
        overlay.Children.Add(fpsBadge);

        // WPF_GALLERY_SCROLL=<0..1> (or arg "scroll=<0..1>", for the browser where env vars aren't
        // settable): park the scroll at a fixed fraction of scrollable height (for deterministic
        // screenshots of lower cards, e.g. the 3D card). Applied every frame so it survives layout.
        double parkScroll = double.TryParse(Environment.GetEnvironmentVariable("WPF_GALLERY_SCROLL"),
            System.Globalization.CultureInfo.InvariantCulture, out double ps) ? ps : -1;
        foreach (string a in args)
            if (a.StartsWith("scroll=") && double.TryParse(a.Substring(7),
                System.Globalization.CultureInfo.InvariantCulture, out double sf))
                parkScroll = sf;

        // 3D-panel interactivity probes: "3dprobe" prints the hosted Button's centre PROJECTED to
        // window coords (via TransformToAncestor across the 2D->3D->2D boundary) so an external
        // driver can aim real mouse input at it; "3dclick" (mac) self-injects a mouse move+click
        // there through the Cocoa input path to prove the full chain without a real mouse.
        bool probe3D = Environment.GetEnvironmentVariable("WPF_GALLERY_3DPROBE") == "1" || Array.IndexOf(args, "3dprobe") >= 0;
        bool click3D = Environment.GetEnvironmentVariable("WPF_GALLERY_3DCLICK") == "1" || Array.IndexOf(args, "3dclick") >= 0;

        // WPF_GALLERY_FPSLOG=1 (or arg "fpslog"): also print the fps to the console once a second
        // (diagnoses platforms where CompositionTarget.Rendering doesn't tick -> frozen badge).
        bool fpsLog = Environment.GetEnvironmentVariable("WPF_GALLERY_FPSLOG") == "1" || Array.IndexOf(args, "fpslog") >= 0;
        int renderTicks = 0;
        bool renderingSeen = false;
        var fpsClock = System.Diagnostics.Stopwatch.StartNew();
        CompositionTarget.Rendering += (s, e) =>
        {
            if (!renderingSeen)
            {
                renderingSeen = true;
                if (fpsLog) Console.WriteLine("FPS first CompositionTarget.Rendering tick");
            }
            if (parkScroll >= 0 && scroll.ScrollableHeight > 0)
                scroll.ScrollToVerticalOffset(parkScroll * scroll.ScrollableHeight);
            renderTicks++;
            double elapsed = fpsClock.Elapsed.TotalSeconds;
            if (elapsed >= 0.5)
            {
                fpsText.Text = $"{renderTicks / elapsed:0} fps";
                if (fpsLog) Console.WriteLine($"FPS {renderTicks / elapsed:0}");
                renderTicks = 0;
                fpsClock.Restart();
            }
        };

        var window = new Window
        {
            Title = "WPF on WebGPU — Feature Gallery",
            Width = 1180,
            Height = 760,
            WindowState = WindowState.Maximized,
            Background = new SolidColorBrush(Color.FromRgb(0xEC, 0xEF, 0xF3)),
            Content = overlay,
        };

        // A little live motion so it's obvious frames are composited continuously.
        int frame = 0;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        timer.Tick += (s, e) =>
        {
            // Drive control state every frame -> WPF re-renders -> the sink re-composites live.
            spin.Angle = (frame * 3) % 360;
            rot3d.Angle = (frame * 1.5) % 360;
            progress.Value = frame % 101;
            liveSlider.Value = 50 + 45 * Math.Sin(frame * 0.08);
            if (list.Items.Count > 0) list.SelectedIndex = (frame / 25) % list.Items.Count;
            if (tabs.Items.Count > 0) tabs.SelectedIndex = (frame / 45) % tabs.Items.Count;
            autoCheck.IsChecked = (frame / 40) % 2 == 0;

            // Reproduce interactive visual states deterministically (no mouse needed):
            // force the checkbox hover state and open the combo dropdown popup.
            if (frame == 12 && Array.IndexOf(args, "states") >= 0)
            {
                stateCombo.IsDropDownOpen = true;     // popup present test
            }
            if (frame == 12 && Array.IndexOf(args, "hover") >= 0)
            {
                // Drive a REAL mouse-over so the checkbox enters its hover visual state
                // (Aero2 uses triggers on IsMouseOver, which only true input can set).
                try
                {
                    InputDemo.SetForegroundWindow(new System.Windows.Interop.WindowInteropHelper(window).Handle);
                    Point p = hoverCheck.PointToScreen(new Point(9, hoverCheck.ActualHeight / 2));
                    InputDemo.MoveCursor(p);
                }
                catch { }
            }
            if (frame == 12 && Array.IndexOf(args, "menuhover") >= 0)
            {
                try
                {
                    InputDemo.SetForegroundWindow(new System.Windows.Interop.WindowInteropHelper(window).Handle);
                    Point p = fileMenuItem.PointToScreen(new Point(fileMenuItem.ActualWidth / 2, fileMenuItem.ActualHeight / 2));
                    InputDemo.MoveCursor(p);
                }
                catch { }
            }
            // RenderTargetBitmap smoke test (env WPF_GALLERY_RTB=1 or arg "rtb"): renders the live
            // window content into an offscreen bitmap (sync-channel render + GPU readback under
            // managed composition), verifies pixels via CopyPixels, and displays the result as the
            // fps badge's background (proving the RTB also marshals back INTO the scene).
            if (frame == 20 && (Environment.GetEnvironmentVariable("WPF_GALLERY_RTB") == "1" || Array.IndexOf(args, "rtb") >= 0))
            {
                try
                {
                    var rtb = new RenderTargetBitmap(300, 200, 96, 96, PixelFormats.Pbgra32);
                    rtb.Render(overlay);
                    var px = new byte[300 * 200 * 4];
                    rtb.CopyPixels(px, 300 * 4, 0);
                    long opaque = 0;
                    for (int i = 3; i < px.Length; i += 4)
                        if (px[i] != 0) opaque++;
                    int c = (10 * 300 + 10) * 4;   // a pixel inside the purple header
                    Console.WriteLine($"RTB-TEST opaque={opaque}/60000 px(10,10)=B{px[c]},G{px[c + 1]},R{px[c + 2]},A{px[c + 3]}");
                    fpsBadge.Background = new ImageBrush(rtb);

                    // And save it through the PNG encoder (managed off-Windows).
                    string save = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "wpf-rtb-test.png");
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(rtb));
                    using (var fs = System.IO.File.Create(save))
                        encoder.Save(fs);
                    Console.WriteLine($"RTB-PNG saved {save} ({new System.IO.FileInfo(save).Length} bytes)");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"RTB-TEST FAILED {ex.GetType().Name}: {ex.Message}");
                }
            }

            // WriteableBitmap smoke test (env WPF_GALLERY_WB=1 or arg "wb"): create + WritePixels a
            // gradient (managed back buffer off-Windows), verify a CopyPixels round-trip, display
            // it as the fps badge's background, then LIVE-update a stripe a couple seconds later
            // (proving dirty updates re-marshal to the compositor).
            if (frame == 20 && (Environment.GetEnvironmentVariable("WPF_GALLERY_WB") == "1" || Array.IndexOf(args, "wb") >= 0))
            {
                try
                {
                    _wbTest = new WriteableBitmap(120, 80, 96, 96, PixelFormats.Bgra32, null);
                    var buf = new byte[120 * 80 * 4];
                    for (int y = 0; y < 80; y++)
                        for (int x = 0; x < 120; x++)
                        {
                            int i = (y * 120 + x) * 4;
                            buf[i] = (byte)(255 - x * 2); buf[i + 1] = (byte)(y * 3); buf[i + 2] = (byte)(x * 2); buf[i + 3] = 255;
                        }
                    _wbTest.WritePixels(new Int32Rect(0, 0, 120, 80), buf, 120 * 4, 0);
                    var check = new byte[120 * 80 * 4];
                    _wbTest.CopyPixels(check, 120 * 4, 0);
                    Console.WriteLine($"WB-TEST roundtrip={buf.AsSpan().SequenceEqual(check)} backbuffer=0x{_wbTest.BackBuffer:x} stride={_wbTest.BackBufferStride}");
                    fpsBadge.Background = new ImageBrush(_wbTest);
                }
                catch (Exception ex) { Console.WriteLine($"WB-TEST FAILED {ex.GetType().Name}: {ex.Message}"); }
            }
            if (frame == 40 && _wbTest != null)
            {
                try
                {
                    var stripe = new byte[120 * 20 * 4];
                    for (int i = 0; i < stripe.Length; i += 4) { stripe[i + 1] = 255; stripe[i + 3] = 255; }   // green
                    _wbTest.WritePixels(new Int32Rect(0, 30, 120, 20), stripe, 120 * 4, 0);
                    Console.WriteLine("WB-TEST live update written");
                }
                catch (Exception ex) { Console.WriteLine($"WB-TEST UPDATE FAILED {ex.GetType().Name}: {ex.Message}"); }
            }

            // Dialog smoke test (arg "dlg"): exercise MessageBox + Open/Save file dialogs. On a
            // real desktop these are modal AppKit windows the user interacts with; headless (no
            // display) they return the declared default / cancellation without blocking.
            if (frame == 20 && Array.IndexOf(args, "dlg") >= 0)
            {
                try
                {
                    MessageBoxResult r = MessageBox.Show("Save changes before closing?", "Gallery",
                        MessageBoxButton.YesNoCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
                    Console.WriteLine($"DLG-MSGBOX result={r}");

                    var ofd = new Microsoft.Win32.OpenFileDialog { Title = "Pick an image", Filter = "PNG|*.png" };
                    bool? ok = ofd.ShowDialog();
                    Console.WriteLine($"DLG-OPEN ok={ok} file={(ok == true ? ofd.FileName : "<none>")}");

                    var sfd = new Microsoft.Win32.SaveFileDialog { Title = "Save as", FileName = "export.png" };
                    bool? sok = sfd.ShowDialog();
                    Console.WriteLine($"DLG-SAVE ok={sok} file={(sok == true ? sfd.FileName : "<none>")}");
                }
                catch (Exception ex) { Console.WriteLine($"DLG-TEST FAILED {ex.GetType().Name}: {ex.Message}"); }
            }

            // Image decode smoke test (env WPF_GALLERY_IMG=1 or arg "img"): PNG round-trip through
            // the managed encoder AND decoder (save a generated pattern, load it back via
            // BitmapImage, compare bytes), display the decoded image, and optionally decode a
            // real-world file given in WPF_IMG_FILE.
            if (frame == 20 && (Environment.GetEnvironmentVariable("WPF_GALLERY_IMG") == "1" || Array.IndexOf(args, "img") >= 0))
            {
                try
                {
                    BitmapSource pattern = MakePattern(96);
                    string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "wpf-img-test.png");
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(pattern));
                    using (var fs = System.IO.File.Create(path))
                        encoder.Save(fs);

                    var loaded = new BitmapImage(new Uri(path));
                    var a = new byte[96 * 96 * 4];
                    var b = new byte[96 * 96 * 4];
                    pattern.CopyPixels(a, 96 * 4, 0);
                    loaded.CopyPixels(b, 96 * 4, 0);
                    Console.WriteLine($"IMG-TEST {loaded.PixelWidth}x{loaded.PixelHeight} roundtrip={a.AsSpan().SequenceEqual(b)}");
                    fpsBadge.Background = new ImageBrush(loaded);

                    string extra = Environment.GetEnvironmentVariable("WPF_IMG_FILE");
                    if (!string.IsNullOrEmpty(extra))
                    {
                        var real = new BitmapImage(new Uri(extra));
                        var px = new byte[real.PixelWidth * real.PixelHeight * 4];
                        real.CopyPixels(px, real.PixelWidth * 4, 0);
                        long nonZero = 0;
                        for (int i = 3; i < px.Length; i += 4) if (px[i] != 0) nonZero++;
                        Console.WriteLine($"IMG-FILE {extra}: {real.PixelWidth}x{real.PixelHeight} dpi={real.DpiX:0} opaquePx={nonZero}");
                    }
                }
                catch (Exception ex) { Console.WriteLine($"IMG-TEST FAILED {ex.GetType().Name}: {ex.Message}"); }
            }

            // 3D-panel interactivity probe/self-test (see probe3D/click3D above). The projection
            // is only meaningful once the scroll has parked and the button has a size.
            if ((probe3D || click3D) && Panel3DButton is { } pb && frame % 30 == 20 && pb.ActualWidth > 0)
            {
                try
                {
                    GeneralTransform toWindow = pb.TransformToAncestor(window);
                    if (toWindow.TryTransform(new Point(pb.ActualWidth / 2, pb.ActualHeight / 2), out Point c))
                    {
                        if (probe3D) Console.WriteLine($"3DPANEL-BTN {c.X:0},{c.Y:0}");
                        if (click3D && frame == 80) Inject3DClick(window, c);
                    }
                    else if (probe3D) Console.WriteLine("3DPANEL-BTN untransformable");
                }
                catch (Exception ex) { Console.WriteLine($"3DPANEL-BTN EX {ex.GetType().Name}: {ex.Message}"); }
            }

            frame++;
            // "close" arg: exercise the REAL window-close path (same as the titlebar close
            // button) instead of app.Shutdown, to catch teardown crashes.
            if (frame == 60 && Array.IndexOf(args, "close") >= 0)
            {
                window.Close();
            }
            // Optional bounded run for automated verification: pass seconds as arg[0].
            if (args.Length > 0 && int.TryParse(args[0], out int secs) && frame > secs * 30)
            {
                timer.Stop();
                app.Shutdown();
            }
        };
        window.Loaded += (s, e) => timer.Start();

        return app.Run(window);
    }

    // ---- chrome -------------------------------------------------------------------

    private static UIElement Header()
    {
        var title = new TextBlock
        {
            Text = "WPF on WebGPU",
            FontSize = 26,
            FontWeight = FontWeights.Bold,
            Foreground = Brushes.White,
        };
        var subtitle = new TextBlock
        {
            Text = "WPF's visual tree composited by the managed wgpu-native backend",
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)),
            Margin = new Thickness(0, 2, 0, 0),
        };
        var stack = new StackPanel { Margin = new Thickness(22, 14, 22, 16) };
        stack.Children.Add(title);
        stack.Children.Add(subtitle);
        return new Border
        {
            Child = stack,
            Background = new LinearGradientBrush(
                Color.FromRgb(0x3B, 0x82, 0xF6), Color.FromRgb(0x7C, 0x3A, 0xED), 0),
        };
    }

    private static UIElement Card(string title, UIElement content)
    {
        var header = new TextBlock
        {
            Text = title,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Ink),
            Margin = new Thickness(0, 0, 0, 8),
        };
        var stack = new StackPanel();
        stack.Children.Add(header);
        stack.Children.Add(content);

        return new Border
        {
            Width = 224,
            Height = 196,
            Margin = new Thickness(9),
            Padding = new Thickness(14),
            CornerRadius = new CornerRadius(12),
            Background = new SolidColorBrush(CardBg),
            Child = stack,
            Effect = new DropShadowEffect
            {
                Color = Colors.Black,
                BlurRadius = 16,
                ShadowDepth = 3,
                Opacity = 0.18,
            },
        };
    }

    private static Canvas Stage() => new Canvas { Width = 196, Height = 132 };

    // ---- demos --------------------------------------------------------------------

    private static UIElement ShapesDemo()
    {
        var c = Stage();
        Add(c, new Rectangle { Width = 60, Height = 44, Fill = Brush(0x3B, 0x82, 0xF6) }, 0, 0);
        Add(c, new Rectangle { Width = 60, Height = 44, RadiusX = 12, RadiusY = 12, Fill = Brush(0x10, 0xB9, 0x81) }, 70, 0);
        Add(c, new Ellipse { Width = 60, Height = 44, Fill = Brush(0xF5, 0x9E, 0x0B) }, 140, 0);
        Add(c, new Line { X1 = 0, Y1 = 60, X2 = 196, Y2 = 60, Stroke = Brush(0x64, 0x74, 0x8B), StrokeThickness = 3 }, 0, 0);
        Add(c, new Polygon
        {
            Points = new PointCollection { new Point(20, 124), new Point(40, 78), new Point(60, 124) },
            Fill = Brush(0xEF, 0x44, 0x44),
        }, 0, 0);
        Add(c, new Rectangle { Width = 110, Height = 22, RadiusX = 11, RadiusY = 11, Fill = Brush(0x8B, 0x5C, 0xF6) }, 76, 96);
        return c;
    }

    private static UIElement GradientsDemo()
    {
        var c = Stage();
        var linear = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
        linear.GradientStops.Add(new GradientStop(Color.FromRgb(0xF4, 0x3F, 0x5E), 0));
        linear.GradientStops.Add(new GradientStop(Color.FromRgb(0xF5, 0x9E, 0x0B), 0.5));
        linear.GradientStops.Add(new GradientStop(Color.FromRgb(0x10, 0xB9, 0x81), 1));
        Add(c, new Rectangle { Width = 92, Height = 132, RadiusX = 8, RadiusY = 8, Fill = linear }, 0, 0);

        var radial = new RadialGradientBrush();
        radial.GradientStops.Add(new GradientStop(Colors.White, 0));
        radial.GradientStops.Add(new GradientStop(Color.FromRgb(0x3B, 0x82, 0xF6), 0.6));
        radial.GradientStops.Add(new GradientStop(Color.FromRgb(0x1E, 0x3A, 0x8A), 1));
        Add(c, new Ellipse { Width = 96, Height = 132, Fill = radial }, 100, 0);
        return c;
    }

    private static UIElement StrokesDemo()
    {
        var c = Stage();
        Add(c, new Ellipse { Width = 80, Height = 80, Stroke = Brush(0x8B, 0x5C, 0xF6), StrokeThickness = 8 }, 4, 4);
        Add(c, new Rectangle
        {
            Width = 84,
            Height = 80,
            Stroke = Brush(0xEF, 0x44, 0x44),
            StrokeThickness = 4,
            StrokeDashArray = new DoubleCollection { 3, 2 },
        }, 104, 4);
        Add(c, new Polyline
        {
            Points = new PointCollection { new Point(0, 0), new Point(40, 30), new Point(90, 0), new Point(140, 30), new Point(190, 0) },
            Stroke = Brush(0x10, 0xB9, 0x81),
            StrokeThickness = 5,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
        }, 0, 96);
        return c;
    }

    private static UIElement PathsDemo()
    {
        var c = Stage();
        Add(c, new Path { Data = Star(40, 40, 38, 16, 5), Fill = Brush(0xF5, 0x9E, 0x0B) }, 6, 6);
        Add(c, new Path
        {
            Data = Geometry.Parse("M 0,20 L 40,20 L 40,8 L 70,30 L 40,52 L 40,40 L 0,40 Z"),
            Fill = Brush(0x3B, 0x82, 0xF6),
        }, 110, 18);
        Add(c, new Path
        {
            Data = Geometry.Parse("M 0,30 C 30,-10 60,70 90,30 S 150,-10 190,30"),
            Stroke = Brush(0xEF, 0x44, 0x44),
            StrokeThickness = 4,
        }, 0, 92);
        return c;
    }

    private static UIElement TextDemo()
    {
        var stack = new StackPanel { Width = 196, Height = 132 };
        var grad = new LinearGradientBrush(Color.FromRgb(0x3B, 0x82, 0xF6), Color.FromRgb(0xEC, 0x48, 0x99), 0);
        stack.Children.Add(new TextBlock { Text = "WebGPU", FontSize = 38, FontWeight = FontWeights.Bold, Foreground = grad });
        stack.Children.Add(new TextBlock { Text = "Real glyphs via DirectWrite", FontSize = 14, Foreground = new SolidColorBrush(Ink) });
        stack.Children.Add(new TextBlock
        {
            Text = "italic • bold • colour",
            FontSize = 15,
            FontStyle = FontStyles.Italic,
            Foreground = Brush(0x10, 0xB9, 0x81),
            Margin = new Thickness(0, 6, 0, 0),
        });
        return stack;
    }

    private static UIElement TransformsDemo(out RotateTransform spin)
    {
        var c = Stage();
        Add(c, new Rectangle { Width = 46, Height = 46, Fill = Brush(0x3B, 0x82, 0xF6), RenderTransform = new ScaleTransform(1, 0.6, 23, 23) }, 4, 40);
        Add(c, new Rectangle { Width = 46, Height = 46, Fill = Brush(0x10, 0xB9, 0x81), RenderTransform = new SkewTransform(-20, 0, 23, 23) }, 72, 40);
        spin = new RotateTransform(0, 23, 23);
        Add(c, new Rectangle { Width = 46, Height = 46, Fill = Brush(0xF5, 0x9E, 0x0B), RenderTransform = spin }, 140, 40);
        return c;
    }

    private static UIElement ClippingDemo()
    {
        var c = Stage();
        var grad = new LinearGradientBrush(Color.FromRgb(0x06, 0xB6, 0xD4), Color.FromRgb(0x7C, 0x3A, 0xED), 0);
        Add(c, new Rectangle { Width = 88, Height = 88, Fill = grad, Clip = new EllipseGeometry(new Point(44, 44), 44, 44) }, 4, 22);
        Add(c, new Rectangle { Width = 88, Height = 88, Fill = Brush(0xEF, 0x44, 0x44), Clip = Star(44, 44, 44, 18, 5) }, 104, 22);
        return c;
    }

    private static UIElement EffectsDemo()
    {
        var c = Stage();
        Add(c, new Rectangle
        {
            Width = 80, Height = 80, RadiusX = 14, RadiusY = 14, Fill = Brush(0x3B, 0x82, 0xF6),
            Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 14, ShadowDepth = 5, Opacity = 0.5 },
        }, 8, 12);
        Add(c, new Ellipse
        {
            Width = 80, Height = 80, Fill = Brush(0xEC, 0x48, 0x99),
            Effect = new BlurEffect { Radius = 8 },
        }, 108, 12);
        return c;
    }

    private static UIElement ImageDemo()
    {
        var c = Stage();
        BitmapSource bmp = MakePattern(64);
        Add(c, new Rectangle
        {
            Width = 132, Height = 132, RadiusX = 10, RadiusY = 10,
            Fill = new ImageBrush(bmp) { Stretch = Stretch.UniformToFill },
        }, 6, 0);
        Add(c, new Ellipse { Width = 56, Height = 56, Fill = new ImageBrush(bmp) { Stretch = Stretch.Uniform } }, 138, 38);
        return c;
    }

    private static UIElement OpacityMaskDemo()
    {
        var c = Stage();
        var rainbow = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        rainbow.GradientStops.Add(new GradientStop(Color.FromRgb(0xEF, 0x44, 0x44), 0));
        rainbow.GradientStops.Add(new GradientStop(Color.FromRgb(0x10, 0xB9, 0x81), 0.5));
        rainbow.GradientStops.Add(new GradientStop(Color.FromRgb(0x3B, 0x82, 0xF6), 1));
        var fade = new LinearGradientBrush(Colors.Black, Colors.Transparent, 0);
        Add(c, new Rectangle { Width = 196, Height = 56, Fill = rainbow, OpacityMask = fade }, 0, 8);

        var vfade = new LinearGradientBrush(Colors.Black, Colors.Transparent, 90);
        Add(c, new Ellipse { Width = 90, Height = 56, Fill = Brush(0x7C, 0x3A, 0xED), OpacityMask = vfade }, 52, 76);
        return c;
    }

    // ---- real WPF controls (rendered via their templates through WebGPU) ----------

    private static UIElement ButtonsDemo()
    {
        var s = new StackPanel { Width = 196 };
        s.Children.Add(new Button { Content = "Click me", Padding = new Thickness(14, 5, 14, 5), Margin = new Thickness(0, 2, 0, 8), HorizontalAlignment = HorizontalAlignment.Left });
        s.Children.Add(new Button
        {
            Content = "Accent action",
            Padding = new Thickness(14, 5, 14, 5),
            Margin = new Thickness(0, 0, 0, 8),
            HorizontalAlignment = HorizontalAlignment.Left,
            Background = Brush(0x3B, 0x82, 0xF6),
            Foreground = Brushes.White,
            BorderBrush = Brush(0x1D, 0x4E, 0xD8),
        });
        s.Children.Add(new Button { Content = "Disabled", Padding = new Thickness(14, 5, 14, 5), IsEnabled = false, HorizontalAlignment = HorizontalAlignment.Left });
        return s;
    }

    private static UIElement TextInputDemo()
    {
        var s = new StackPanel { Width = 196 };
        s.Children.Add(new Label { Content = "Name", Padding = new Thickness(0, 0, 0, 2) });
        s.Children.Add(new TextBox { Text = "Ada Lovelace", Margin = new Thickness(0, 0, 0, 8) });
        s.Children.Add(new PasswordBox { Password = "secret", Margin = new Thickness(0, 0, 0, 8) });
        s.Children.Add(new TextBox { Text = "Multi-line input\nrendered via WebGPU", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 44 });
        return s;
    }

    private static UIElement TogglesDemo()
    {
        var s = new StackPanel { Width = 196 };
        s.Children.Add(new CheckBox { Content = "Anti-aliasing", IsChecked = true, Margin = new Thickness(0, 4, 0, 6) });
        s.Children.Add(new CheckBox { Content = "Hardware accel.", IsChecked = true, Margin = new Thickness(0, 0, 0, 6) });
        s.Children.Add(new CheckBox { Content = "Wireframe", IsChecked = false, Margin = new Thickness(0, 0, 0, 10) });
        s.Children.Add(new RadioButton { Content = "WebGPU backend", IsChecked = true, Margin = new Thickness(0, 0, 0, 6) });
        s.Children.Add(new RadioButton { Content = "Native milcore" });
        return s;
    }

    private static UIElement RangeDemo()
    {
        var s = new StackPanel { Width = 196 };
        s.Children.Add(new Slider { Minimum = 0, Maximum = 100, Value = 65, Margin = new Thickness(0, 4, 0, 10) });
        s.Children.Add(new ProgressBar { Minimum = 0, Maximum = 100, Value = 72, Height = 16, Margin = new Thickness(0, 0, 0, 12) });
        var combo = new ComboBox { Margin = new Thickness(0, 0, 0, 8) };
        combo.Items.Add("wgpu-native");
        combo.Items.Add("Vulkan");
        combo.Items.Add("Direct3D 12");
        combo.SelectedIndex = 0;
        s.Children.Add(combo);
        return s;
    }

    // A real WPF Viewport3D exercising the WebGPU backend's 3D pipeline end to end:
    //   * a TEXTURED cube (DiffuseMaterial with an ImageBrush),
    //   * a SPECULAR sphere (MaterialGroup of diffuse + SpecularMaterial -> Blinn-Phong highlight),
    //   * three orbiting coloured POINT lights (attenuated), each marked by a small EMISSIVE sphere,
    //   * and 2D-IN-3D: a tilted panel textured with a VisualBrush of live 2D WPF content.
    private static UIElement Viewport3DDemo(out AxisAngleRotation3D rotation)
    {
        var viewport = new Viewport3D { Width = 196, Height = 150 };

        // ORBIT CAMERA: left-drag orbits around the scene centre (yaw/pitch on a sphere), scroll
        // wheel zooms the radius. The initial pose matches the old fixed camera.
        var camTarget = new Point3D(0, 0.9, 0);
        double camYaw = 0, camPitch = 7 * Math.PI / 180, camRadius = 6.45;
        var camera = new PerspectiveCamera
        {
            UpDirection = new Vector3D(0, 1, 0),
            FieldOfView = 45,
            NearPlaneDistance = 0.1,
            FarPlaneDistance = 100,
        };
        void UpdateCamera()
        {
            var offset = new Vector3D(
                camRadius * Math.Cos(camPitch) * Math.Sin(camYaw),
                camRadius * Math.Sin(camPitch),
                camRadius * Math.Cos(camPitch) * Math.Cos(camYaw));
            camera.Position = camTarget + offset;
            camera.LookDirection = -offset;
        }
        UpdateCamera();
        viewport.Camera = camera;

        var group = new Model3DGroup();
        group.Children.Add(new AmbientLight(Color.FromRgb(0x20, 0x20, 0x28)));
        // A soft key DirectionalLight so the textured cube and the 2D-in-3D panel read clearly;
        // the orbiting coloured point lights below add movement and colour on top.
        group.Children.Add(new DirectionalLight(Color.FromRgb(0x9A, 0x9A, 0xA0), new Vector3D(-0.3, -0.45, -1.0)));

        // Orbiting coloured POINT lights + an emissive marker sphere at each, all under one animated
        // rotation so they sweep the scene (shows multiple lights, point attenuation and emissive).
        var orbit = new AxisAngleRotation3D(new Vector3D(0, 1, 0), 0);
        var orbiters = new Model3DGroup { Transform = new RotateTransform3D(orbit) };
        (Color c, double deg)[] pts =
        {
            (Colors.Red, 0),
            (Color.FromRgb(0x2E, 0xE0, 0x66), 120),
            (Color.FromRgb(0x53, 0x93, 0xFF), 240),
        };
        foreach ((Color c, double deg) in pts)
        {
            double a = deg * Math.PI / 180.0;
            var pos = new Point3D(3.2 * Math.Cos(a), 1.6, 3.2 * Math.Sin(a));
            orbiters.Children.Add(new PointLight(c, pos)
            {
                Range = 16,
                ConstantAttenuation = 1.0,
                LinearAttenuation = 0.12,
                QuadraticAttenuation = 0.02,
            });
            orbiters.Children.Add(new GeometryModel3D(SphereMesh(0.13, 10),
                new EmissiveMaterial(new SolidColorBrush(c)))
            {
                Transform = new TranslateTransform3D(pos.X, pos.Y, pos.Z),
            });
        }
        group.Children.Add(orbiters);

        // TEXTURED rotating cube (ImageBrush of a generated colour-wheel bitmap). Driven by the
        // gallery timer via `rotation`.
        rotation = new AxisAngleRotation3D(new Vector3D(0.25, 1, 0.12), 0);
        var cube = new GeometryModel3D(TexturedCubeMesh(0.85), new DiffuseMaterial(new ImageBrush(MakePattern(96))))
        {
            Transform = new Transform3DGroup { Children = { new RotateTransform3D(rotation), new TranslateTransform3D(-1.9, 0.1, 0) } },
        };
        group.Children.Add(cube);

        // SPECULAR shiny sphere: MaterialGroup layering a purple diffuse and a white specular.
        var shiny = new MaterialGroup();
        shiny.Children.Add(new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(0x7C, 0x3A, 0xED))));
        shiny.Children.Add(new SpecularMaterial(new SolidColorBrush(Colors.White), 48));
        group.Children.Add(new GeometryModel3D(SphereMesh(0.95, 26), shiny)
        {
            Transform = new TranslateTransform3D(1.9, 0.1, 0),
        });

        viewport.Children.Add(new ModelVisual3D { Content = group });

        // 2D-IN-3D, LIVE + INTERACTIVE (Viewport2DVisual3D): WPF's real mechanism for hosting
        // interactive 2D content on a 3D surface. Its internal brush IS a VisualBrush, which the
        // engine renders into a GPU texture each frame with the ordinary 2D shaders — no CPU
        // rasterization, no readback. Input hit-tests through the 3D projection (camera ray →
        // mesh triangle → barycentric UV → 2D point), so the hosted Button gets REAL mouse
        // events. Diffuse+Emissive host materials make the panel a self-lit "screen" that still
        // catches the coloured scene lights.
        UIElement panelVisual = BuildLive3DPanel(out SolidColorBrush led, out ScaleTransform progress,
            out TranslateTransform dot);
        // Only ONE material may be the interactive host; the diffuse host carries the live
        // visual, and the scene's key light keeps the panel readable.
        var diffuseHost = new DiffuseMaterial(Brushes.White);
        Viewport2DVisual3D.SetIsVisualHostMaterial(diffuseHost, true);
        Transform3D PanelPose() => new Transform3DGroup
        {
            Children =
            {
                new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(0, 1, 0), 18)),
                new TranslateTransform3D(0, 1.9, -2.4),
            },
        };
        viewport.Children.Add(new Viewport2DVisual3D
        {
            Geometry = QuadMesh(3.0, 1.6),
            Visual = panelVisual,
            Material = diffuseHost,
            Transform = PanelPose(),
        });
        // BACKMATERIAL: a coplanar quad with ONLY a BackMaterial gives the one-sided panel a dark
        // "chassis" when orbited behind (its front is culled, the panel's front faces the other
        // way — the two never draw together, so no z-fighting).
        group.Children.Add(new GeometryModel3D(QuadMesh(3.0, 1.6), null)
        {
            BackMaterial = new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(0x2A, 0x33, 0x44))),
            Transform = PanelPose(),
        });

        // Self-animate the orbiting lights and the live 2D panel (cube spin comes from the gallery
        // timer). Only render-affecting properties are touched (transforms + brush colour), so the
        // orphan panel tree needs no layout pass — the invalidations flow to the compositor and the
        // panel texture re-renders on the GPU.
        int panelFrame = 0;
        CompositionTarget.Rendering += (s, e) =>
        {
            orbit.Angle = (orbit.Angle + 0.8) % 360.0;
            panelFrame++;
            bool on = (panelFrame / 30) % 2 == 0;
            led.Color = on ? Color.FromRgb(0x34, 0xD3, 0x99) : Color.FromRgb(0x14, 0x3A, 0x30);
            progress.ScaleX = (panelFrame % 120) / 120.0;
            dot.X = (Math.Sin(panelFrame * 0.06) * 0.5 + 0.5) * 300;
        };

        // Interaction surface for the orbit camera. A transparent Border catches mouse events over
        // the WHOLE card area (Viewport3D itself only hit-tests where 3D geometry is). The hosted
        // panel Button still wins its clicks (ButtonBase marks MouseLeftButtonDown handled, so a
        // press on it never starts a drag); wheel zoom marks the event handled so the gallery's
        // ScrollViewer doesn't scroll while zooming.
        var surface = new Border { Background = Brushes.Transparent, Child = viewport };
        bool dragging = false;
        Point dragLast = default;
        surface.MouseLeftButtonDown += (s, e) =>
        {
            dragging = surface.CaptureMouse();
            dragLast = e.GetPosition(surface);
        };
        surface.MouseMove += (s, e) =>
        {
            if (!dragging) return;
            Point p = e.GetPosition(surface);
            camYaw -= (p.X - dragLast.X) * 0.012;
            camPitch = Math.Clamp(camPitch + (p.Y - dragLast.Y) * 0.012, -75 * Math.PI / 180, 85 * Math.PI / 180);
            dragLast = p;
            UpdateCamera();
        };
        surface.MouseLeftButtonUp += (s, e) =>
        {
            dragging = false;
            surface.ReleaseMouseCapture();
        };
        surface.MouseWheel += (s, e) =>
        {
            camRadius = Math.Clamp(camRadius * Math.Pow(1.0011, -e.Delta), 2.5, 14.0);
            UpdateCamera();
            e.Handled = true;
        };
        return surface;
    }

    // The Button hosted on the 3D panel (exposed for the projected-coordinate probe/self-test).
    internal static Button Panel3DButton;

    // WriteableBitmap under test (see the "wb" gallery arg).
    private static WriteableBitmap _wbTest;

    // macOS self-test: feed a mouse move + left click at the given window point (DIPs) into the
    // SAME entry real AppKit events use (CocoaWindow.MouseInput -> HwndMouseInputProvider ->
    // InputManager), proving hit-testing + routing through the 3D projection without a physical
    // mouse. The event is raised via reflection (only CocoaWindow itself can invoke it); no-op
    // when the Cocoa input path isn't active (e.g. browser).
    private static void Inject3DClick(Window window, Point clientDip)
    {
#if LIBREWPF
        // CocoaWindow is this fork's macOS windowing type; not present on other WPF platforms.
        Console.WriteLine("3DPANEL-SELFTEST unavailable on this platform build");
#else
        var field = typeof(MS.Internal.Interop.CocoaWindow).GetField(
            "MouseInput", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        if (field?.GetValue(null) is not Action<MS.Internal.Interop.CocoaWindow.CocoaMouseMessage> raise)
        {
            Console.WriteLine("3DPANEL-SELFTEST no cocoa input sink");
            return;
        }
        PresentationSource src = PresentationSource.FromVisual(window);
        Point px = src?.CompositionTarget != null
            ? src.CompositionTarget.TransformToDevice.Transform(clientDip)
            : clientDip;
        IntPtr view = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        int x = (int)Math.Round(px.X), y = (int)Math.Round(px.Y), t = Environment.TickCount;
        Console.WriteLine($"3DPANEL-SELFTEST move+click at client px {x},{y}");
        raise(new MS.Internal.Interop.CocoaWindow.CocoaMouseMessage(view, 5, 0, x, y, 0, t));   // NSMouseMoved
        raise(new MS.Internal.Interop.CocoaWindow.CocoaMouseMessage(view, 1, 0, x, y, 0, t));   // NSLeftMouseDown
        raise(new MS.Internal.Interop.CocoaWindow.CocoaMouseMessage(view, 2, 0, x, y, 0, t));   // NSLeftMouseUp
#endif
    }

    // The live+interactive 2D UI shown on the 3D quad: a dark "screen" panel with real text, a
    // blinking status LED, an animated progress bar, a bouncing dot — and a REAL Button that
    // receives mouse input hit-tested through the 3D projection. Animations touch only
    // render-only transform/colour properties (no layout pass needed).
    private static UIElement BuildLive3DPanel(out SolidColorBrush led, out ScaleTransform progress,
        out TranslateTransform dot)
    {
        const double W = 360, H = 200;
        var root = new Border { Width = W, Height = H, Background = new SolidColorBrush(Color.FromRgb(0x0B, 0x12, 0x22)) };
        var canvas = new Canvas();
        root.Child = canvas;

        // Header bar + title + blinking LED.
        canvas.Children.Add(Place(new Rectangle { Width = W, Height = 52, Fill = new SolidColorBrush(Color.FromRgb(0x15, 0x22, 0x3C)) }, 0, 0));
        canvas.Children.Add(Place(new TextBlock
        {
            Text = "2D in 3D — live",
            FontSize = 24,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x38, 0xBD, 0xF8)),
        }, 22, 11));
        led = new SolidColorBrush(Color.FromRgb(0x34, 0xD3, 0x99));
        canvas.Children.Add(Place(new Ellipse { Width = 18, Height = 18, Fill = led }, 313, 17));

        // A REAL interactive Button + click counter. Hover/click prove the whole input chain:
        // window mouse event -> Viewport3DVisual ray hit test -> Viewport2DVisual3D UV mapping ->
        // 2D hit test -> routed events on the hosted element.
        var button = new Button { Content = "Click me", Width = 150, Height = 40, FontSize = 16 };
        var counter = new TextBlock
        {
            Text = "Clicks: 0",
            FontSize = 16,
            Foreground = new SolidColorBrush(Color.FromRgb(0xC8, 0xD4, 0xE6)),
        };
        int clicks = 0;
        button.Click += (s, e) =>
        {
            clicks++;
            counter.Text = $"Clicks: {clicks}";
            Console.WriteLine($"3DPANEL CLICK {clicks}");
        };
        button.MouseEnter += (s, e) => Console.WriteLine("3DPANEL ENTER");
        button.MouseLeave += (s, e) => Console.WriteLine("3DPANEL LEAVE");
        canvas.Children.Add(Place(button, 22, 64));
        canvas.Children.Add(Place(counter, 190, 74));
        Panel3DButton = button;

        // Progress bar: fixed track, fill animated with a ScaleTransform (render-only).
        canvas.Children.Add(Place(new Rectangle { Width = 316, Height = 16, RadiusX = 8, RadiusY = 8, Fill = new SolidColorBrush(Color.FromRgb(0x1E, 0x2B, 0x44)) }, 22, 124));
        progress = new ScaleTransform(0.0, 1.0);
        canvas.Children.Add(Place(new Rectangle
        {
            Width = 316, Height = 16, RadiusX = 8, RadiusY = 8,
            Fill = new SolidColorBrush(Color.FromRgb(0x38, 0xBD, 0xF8)),
            RenderTransform = progress,
        }, 22, 124));

        // Bouncing dot along a rail (TranslateTransform, render-only).
        canvas.Children.Add(Place(new Rectangle { Width = 316, Height = 4, Fill = new SolidColorBrush(Color.FromRgb(0x1E, 0x2B, 0x44)) }, 22, 168));
        dot = new TranslateTransform(0, 0);
        canvas.Children.Add(Place(new Ellipse
        {
            Width = 22, Height = 22,
            Fill = new SolidColorBrush(Color.FromRgb(0x34, 0xD3, 0x99)),
            RenderTransform = dot,
        }, 19, 159));

        root.Measure(new Size(W, H));
        root.Arrange(new Rect(0, 0, W, H));
        return root;

        static UIElement Place(UIElement e, double x, double y)
        {
            Canvas.SetLeft(e, x);
            Canvas.SetTop(e, y);
            return e;
        }
    }

    // A cube with per-face texture coordinates, half-extent `s`.
    private static MeshGeometry3D TexturedCubeMesh(double s)
    {
        var mesh = new MeshGeometry3D();
        void Face(Point3D a, Point3D b, Point3D c, Point3D d, Vector3D n)
        {
            int i = mesh.Positions.Count;
            mesh.Positions.Add(a); mesh.Positions.Add(b); mesh.Positions.Add(c); mesh.Positions.Add(d);
            mesh.Normals.Add(n); mesh.Normals.Add(n); mesh.Normals.Add(n); mesh.Normals.Add(n);
            mesh.TextureCoordinates.Add(new Point(0, 1)); mesh.TextureCoordinates.Add(new Point(1, 1));
            mesh.TextureCoordinates.Add(new Point(1, 0)); mesh.TextureCoordinates.Add(new Point(0, 0));
            foreach (int k in new[] { i, i + 1, i + 2, i, i + 2, i + 3 }) mesh.TriangleIndices.Add(k);
        }
        Point3D P(double x, double y, double z) => new Point3D(x, y, z);
        Face(P(-s, -s, s), P(s, -s, s), P(s, s, s), P(-s, s, s), new Vector3D(0, 0, 1));      // front
        Face(P(s, -s, -s), P(-s, -s, -s), P(-s, s, -s), P(s, s, -s), new Vector3D(0, 0, -1));  // back
        Face(P(-s, -s, -s), P(-s, -s, s), P(-s, s, s), P(-s, s, -s), new Vector3D(-1, 0, 0));  // left
        Face(P(s, -s, s), P(s, -s, -s), P(s, s, -s), P(s, s, s), new Vector3D(1, 0, 0));      // right
        Face(P(-s, s, s), P(s, s, s), P(s, s, -s), P(-s, s, -s), new Vector3D(0, 1, 0));      // top
        Face(P(-s, -s, -s), P(s, -s, -s), P(s, -s, s), P(-s, -s, s), new Vector3D(0, -1, 0));  // bottom
        return mesh;
    }

    // A UV sphere (radius, latitude/longitude segments) with normals and texture coordinates.
    private static MeshGeometry3D SphereMesh(double radius, int segments)
    {
        var mesh = new MeshGeometry3D();
        for (int lat = 0; lat <= segments; lat++)
        {
            double theta = lat * Math.PI / segments;      // 0..pi
            double st = Math.Sin(theta), ct = Math.Cos(theta);
            for (int lon = 0; lon <= segments; lon++)
            {
                double phi = lon * 2 * Math.PI / segments; // 0..2pi
                var n = new Vector3D(st * Math.Cos(phi), ct, st * Math.Sin(phi));
                mesh.Positions.Add(new Point3D(n.X * radius, n.Y * radius, n.Z * radius));
                mesh.Normals.Add(n);
                mesh.TextureCoordinates.Add(new Point(lon / (double)segments, lat / (double)segments));
            }
        }
        int stride = segments + 1;
        for (int lat = 0; lat < segments; lat++)
            for (int lon = 0; lon < segments; lon++)
            {
                int a = lat * stride + lon, b = a + stride;
                // Counter-clockwise from OUTSIDE (WPF's front-face winding; back faces are culled
                // when no BackMaterial is set).
                mesh.TriangleIndices.Add(a); mesh.TriangleIndices.Add(a + 1); mesh.TriangleIndices.Add(b);
                mesh.TriangleIndices.Add(a + 1); mesh.TriangleIndices.Add(b + 1); mesh.TriangleIndices.Add(b);
            }
        return mesh;
    }

    // A flat quad in the XY plane (facing +Z) with full [0,1] texture coordinates.
    private static MeshGeometry3D QuadMesh(double w, double h)
    {
        double hw = w / 2, hh = h / 2;
        var mesh = new MeshGeometry3D();
        mesh.Positions.Add(new Point3D(-hw, -hh, 0)); mesh.Positions.Add(new Point3D(hw, -hh, 0));
        mesh.Positions.Add(new Point3D(hw, hh, 0)); mesh.Positions.Add(new Point3D(-hw, hh, 0));
        for (int i = 0; i < 4; i++) mesh.Normals.Add(new Vector3D(0, 0, 1));
        mesh.TextureCoordinates.Add(new Point(0, 1)); mesh.TextureCoordinates.Add(new Point(1, 1));
        mesh.TextureCoordinates.Add(new Point(1, 0)); mesh.TextureCoordinates.Add(new Point(0, 0));
        foreach (int k in new[] { 0, 1, 2, 0, 2, 3 }) mesh.TriangleIndices.Add(k);
        return mesh;
    }

    // DrawingBrush (a tiled vector motif) + VisualBrush (a live mirror of another element) — both
    // rasterized by the WebGPU backend and tiled through the ImageBrush path.
    private static UIElement TileBrushDemo()
    {
        var panel = new StackPanel { Width = 196 };

        // DrawingBrush: a small two-shape motif tiled across a rectangle.
        var motif = new DrawingGroup();
        motif.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6)), null, new EllipseGeometry(new Point(11, 11), 7, 7)));
        motif.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromRgb(0x10, 0xB9, 0x81)), null, new RectangleGeometry(new Rect(0, 0, 5, 5))));
        var drawingBrush = new DrawingBrush(motif)
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, 24, 24),
            ViewportUnits = BrushMappingMode.Absolute,
            Stretch = Stretch.Uniform,
        };
        panel.Children.Add(new TextBlock { Text = "DrawingBrush (tiled)", FontSize = 12, Foreground = new SolidColorBrush(Ink) });
        panel.Children.Add(new Rectangle { Width = 184, Height = 48, Fill = drawingBrush, Margin = new Thickness(0, 3, 0, 12) });

        // VisualBrush: a live reflection of the element above it (flipped + faded).
        var original = new Border
        {
            Width = 150,
            Height = 30,
            CornerRadius = new CornerRadius(5),
            Background = new LinearGradientBrush(Color.FromRgb(0xF5, 0x9E, 0x0B), Color.FromRgb(0xEF, 0x44, 0x44), 0),
            Child = new TextBlock { Text = "VisualBrush", Foreground = Brushes.White, FontWeight = FontWeights.Bold, FontSize = 13, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        panel.Children.Add(original);
        var reflection = new Rectangle
        {
            Width = 150,
            Height = 20,
            Margin = new Thickness(0, 1, 0, 0),
            Fill = new VisualBrush(original) { Stretch = Stretch.Fill },
            OpacityMask = new LinearGradientBrush(Color.FromArgb(0x90, 0xFF, 0xFF, 0xFF), Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 90),
            RenderTransform = new ScaleTransform(1, -1, 0, 10),
        };
        panel.Children.Add(reflection);
        return panel;
    }

    private static ListBox MakeList()
    {
        var lb = new ListBox { Height = 132, BorderThickness = new Thickness(1) };
        foreach (string s in new[] { "wgpu-native", "Vulkan", "Direct3D 12", "Metal", "OpenGL" })
            lb.Items.Add(s);
        lb.SelectedIndex = 0;
        return lb;
    }

    private static TabControl MakeTabs()
    {
        var tc = new TabControl { Height = 132 };
        tc.Items.Add(new TabItem { Header = "Shapes", Content = TabBody("Vector shapes\n& geometry", 0x3B, 0x82, 0xF6) });
        tc.Items.Add(new TabItem { Header = "Text", Content = TabBody("DirectWrite\nglyph runs", 0x10, 0xB9, 0x81) });
        tc.Items.Add(new TabItem { Header = "Media", Content = TabBody("Images &\neffects", 0xF5, 0x9E, 0x0B) });
        return tc;
    }

    private static UIElement TabBody(string text, byte r, byte g, byte b)
    {
        return new Border
        {
            Padding = new Thickness(8),
            Child = new TextBlock { Text = text, FontSize = 14, Foreground = Brush(r, g, b), FontWeight = FontWeights.SemiBold },
        };
    }

    private static UIElement MenuTreeDemo() => MenuTreeDemo(out _);

    private static UIElement MenuTreeDemo(out MenuItem fileItem)
    {
        var s = new StackPanel { Width = 196, Height = 132 };
        var menu = new Menu();
        var file = new MenuItem { Header = "File" };
        fileItem = file;
        file.Items.Add(new MenuItem { Header = "New" });
        file.Items.Add(new MenuItem { Header = "Open" });
        menu.Items.Add(file);
        menu.Items.Add(new MenuItem { Header = "Edit" });
        menu.Items.Add(new MenuItem { Header = "View" });
        s.Children.Add(menu);

        var tree = new TreeView { Height = 104, BorderThickness = new Thickness(0), Margin = new Thickness(0, 4, 0, 0) };
        var root = new TreeViewItem { Header = "Renderer", IsExpanded = true };
        root.Items.Add(new TreeViewItem { Header = "WebGPU device" });
        var comp = new TreeViewItem { Header = "Compositor", IsExpanded = true };
        comp.Items.Add(new TreeViewItem { Header = "Scene graph" });
        comp.Items.Add(new TreeViewItem { Header = "Swap chain" });
        root.Items.Add(comp);
        tree.Items.Add(root);
        s.Children.Add(tree);
        return s;
    }

    private static UIElement LiveDemo(ProgressBar progress, Slider slider, CheckBox check)
    {
        var s = new StackPanel { Width = 196 };
        s.Children.Add(new TextBlock { Text = "Compositing frames…", FontSize = 13, Foreground = new SolidColorBrush(Ink), Margin = new Thickness(0, 2, 0, 8) });
        progress.Margin = new Thickness(0, 0, 0, 14);
        s.Children.Add(progress);
        s.Children.Add(slider);
        s.Children.Add(check);
        return s;
    }

    // ---- helpers ------------------------------------------------------------------

    private static UIElement Fixed(UIElement e)
    {
        return new Border { Width = 196, Height = 132, Child = e };
    }

    private static SolidColorBrush Brush(byte r, byte g, byte b) => new SolidColorBrush(Color.FromRgb(r, g, b));

    private static void Add(Canvas c, UIElement e, double x, double y)
    {
        Canvas.SetLeft(e, x);
        Canvas.SetTop(e, y);
        c.Children.Add(e);
    }

    private static Geometry Star(double cx, double cy, double rOuter, double rInner, int points)
    {
        var g = new StreamGeometry();
        using (StreamGeometryContext ctx = g.Open())
        {
            for (int i = 0; i < points * 2; i++)
            {
                double r = (i % 2 == 0) ? rOuter : rInner;
                double a = Math.PI / points * i - Math.PI / 2;
                var p = new Point(cx + r * Math.Cos(a), cy + r * Math.Sin(a));
                if (i == 0) ctx.BeginFigure(p, true, true);
                else ctx.LineTo(p, true, false);
            }
        }
        g.Freeze();
        return g;
    }

    private static BitmapSource MakePattern(int n)
    {
        var px = new byte[n * n * 4];
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                double dx = x - n / 2.0, dy = y - n / 2.0;
                double rad = Math.Min(1.0, Math.Sqrt(dx * dx + dy * dy) / (n / 2.0));
                double hue = (Math.Atan2(dy, dx) / (2 * Math.PI)) + 0.5;
                (byte r, byte g, byte b) = Hsv(hue, 0.2 + 0.8 * rad, 1.0);
                int i = (y * n + x) * 4;
                px[i] = b; px[i + 1] = g; px[i + 2] = r; px[i + 3] = 255;   // Bgra32
            }
        return BitmapSource.Create(n, n, 96, 96, PixelFormats.Bgra32, null, px, n * 4);
    }

    private static (byte, byte, byte) Hsv(double h, double s, double v)
    {
        h = (h % 1.0 + 1.0) % 1.0 * 6.0;
        int i = (int)h;
        double f = h - i, p = v * (1 - s), q = v * (1 - s * f), t = v * (1 - s * (1 - f));
        double r, g, b;
        switch (i % 6)
        {
            case 0: r = v; g = t; b = p; break;
            case 1: r = q; g = v; b = p; break;
            case 2: r = p; g = v; b = t; break;
            case 3: r = p; g = q; b = v; break;
            case 4: r = t; g = p; b = v; break;
            default: r = v; g = p; b = q; break;
        }
        return ((byte)(r * 255), (byte)(g * 255), (byte)(b * 255));
    }
}
