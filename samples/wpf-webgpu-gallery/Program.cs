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

    [STAThread]
    private static int Main(string[] args)
    {
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

        int renderTicks = 0;
        var fpsClock = System.Diagnostics.Stopwatch.StartNew();
        CompositionTarget.Rendering += (s, e) =>
        {
            renderTicks++;
            double elapsed = fpsClock.Elapsed.TotalSeconds;
            if (elapsed >= 0.5)
            {
                fpsText.Text = $"{renderTicks / elapsed:0} fps";
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
            frame++;
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

    // A real WPF Viewport3D: perspective camera, a lit + animated cube, decoded from the 3D
    // MILCMD stream and rendered by the WebGPU backend's depth-tested 3D pass.
    private static UIElement Viewport3DDemo(out AxisAngleRotation3D rotation)
    {
        var viewport = new Viewport3D { Width = 196, Height = 150 };
        viewport.Camera = new PerspectiveCamera
        {
            Position = new Point3D(2.6, 2.2, 4.0),
            LookDirection = new Vector3D(-2.6, -2.2, -4.0),
            UpDirection = new Vector3D(0, 1, 0),
            FieldOfView = 45,
            NearPlaneDistance = 0.1,
            FarPlaneDistance = 100,
        };

        var group = new Model3DGroup();
        group.Children.Add(new AmbientLight(Color.FromRgb(0x3A, 0x3A, 0x46)));
        group.Children.Add(new DirectionalLight(Colors.White, new Vector3D(-1.0, -1.5, -2.0)));

        rotation = new AxisAngleRotation3D(new Vector3D(0.3, 1, 0.2), 0);
        var cube = new GeometryModel3D(CubeMesh(), new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6))))
        {
            Transform = new RotateTransform3D(rotation),
        };
        group.Children.Add(cube);

        viewport.Children.Add(new ModelVisual3D { Content = group });
        return viewport;
    }

    private static MeshGeometry3D CubeMesh()
    {
        var mesh = new MeshGeometry3D();
        void Face(Point3D a, Point3D b, Point3D c, Point3D d, Vector3D n)
        {
            int i = mesh.Positions.Count;
            mesh.Positions.Add(a); mesh.Positions.Add(b); mesh.Positions.Add(c); mesh.Positions.Add(d);
            mesh.Normals.Add(n); mesh.Normals.Add(n); mesh.Normals.Add(n); mesh.Normals.Add(n);
            foreach (int k in new[] { i, i + 1, i + 2, i, i + 2, i + 3 }) mesh.TriangleIndices.Add(k);
        }
        const double s = 1.0;
        Point3D P(double x, double y, double z) => new Point3D(x, y, z);
        Face(P(-s, -s, s), P(s, -s, s), P(s, s, s), P(-s, s, s), new Vector3D(0, 0, 1));    // front
        Face(P(s, -s, -s), P(-s, -s, -s), P(-s, s, -s), P(s, s, -s), new Vector3D(0, 0, -1)); // back
        Face(P(-s, -s, -s), P(-s, -s, s), P(-s, s, s), P(-s, s, -s), new Vector3D(-1, 0, 0)); // left
        Face(P(s, -s, s), P(s, -s, -s), P(s, s, -s), P(s, s, s), new Vector3D(1, 0, 0));     // right
        Face(P(-s, s, s), P(s, s, s), P(s, s, -s), P(-s, s, -s), new Vector3D(0, 1, 0));     // top
        Face(P(-s, -s, -s), P(s, -s, -s), P(s, -s, s), P(-s, -s, s), new Vector3D(0, -1, 0)); // bottom
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
