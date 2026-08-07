// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The smallest WPF app that exercises the Linux/Wayland head end to end, used for gates G4-G8.
//
// Deliberately code-only (no XAML, no markup compiler) and deliberately small: when something is
// wrong in the platform layer, a failure here points at one thing, whereas the same failure inside
// the full gallery is buried under thirty controls. Each control below is here because it proves a
// specific part of the head:
//
//   TextBlock  -> the managed font stack found a font and glyph rendering works
//   Button     -> mouse enter/leave/press/release and WPF's capture path
//   TextBox    -> keyboard focus, keysym->virtual-key mapping, text input, the I-beam cursor
//   ComboBox   -> a POPUP: its own surface, positioned through xdg_positioner
//   ContextMenu-> a popup opened at the pointer, exercising the coordinate write-back
//   ListBox    -> wheel scrolling (axis_value120)
//   the dialog -> a nested dispatcher frame, which only works because Linux keeps the blocking loop
//
//   dotnet run --project samples/wpf-linux-smoke
//

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var app = new Application();
        Window window = BuildWindow();

        // Accept drops, and say what arrived. Dropping a file from Files/Nautilus or selected text from
        // another app is the only way to exercise the drop-target half -- the compositor will not
        // synthesise a drag, so this cannot be driven from a flag.
        window.AllowDrop = true;
        window.DragEnter += (_, e) =>
            Console.Error.WriteLine($"[drop] DragEnter formats=[{string.Join(",", e.Data.GetFormats())}] effects={e.Effects}");
        window.DragOver += (_, e) => e.Effects = DragDropEffects.Copy;
        window.Drop += (_, e) =>
        {
            var what = new System.Text.StringBuilder();
            if (e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] files)
                what.Append($"files=[{string.Join(",", files)}] ");
            if (e.Data.GetDataPresent(DataFormats.UnicodeText))
                what.Append($"text=\"{e.Data.GetData(DataFormats.UnicodeText)}\"");
            Console.Error.WriteLine($"[drop] Drop {what}");
            e.Effects = DragDropEffects.Copy;
        };

        // --auto-popup opens the ComboBox dropdown a moment after the window maps, so popup
        // PLACEMENT can be checked from a log without anyone clicking. Placement is the part of the
        // Wayland head with no absolute coordinates to fall back on (see WaylandWindow's header),
        // so it is the piece most worth being able to test unattended.
        if (Array.IndexOf(args, "--auto-popup") >= 0)
        {
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                if (window.Content is Panel root)
                {
                    foreach (object child in root.Children)
                    {
                        if (child is not ComboBox cb) continue;

                        // WPF's own answer for where the dropdown should sit: the ComboBox's
                        // bottom-left in screen coordinates. Printing it lets the placement the
                        // backend computes be checked against the placement WPF asked for, instead
                        // of judging by eye.
                        Point topLeft = cb.PointToScreen(new Point(0, 0));
                        Point bottomLeft = cb.PointToScreen(new Point(0, cb.ActualHeight));
                        Console.Error.WriteLine(
                            $"[probe] combo topLeft(screen)={topLeft.X:0},{topLeft.Y:0} " +
                            $"bottomLeft(screen)={bottomLeft.X:0},{bottomLeft.Y:0} " +
                            $"actualHeight={cb.ActualHeight:0.#}");

                        cb.IsDropDownOpen = true;
                        break;
                    }
                }
            };
            timer.Start();
        }

        // --auto-desktop exercises the desktop-integration surfaces that are easy to write and easy to
        // never actually run: the theme read, MessageBox, and the portal file dialog. Each is called
        // for real and its exception (if any) reported, rather than being assumed to work.
        if (Array.IndexOf(args, "--auto-desktop") >= 0)
        {
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                Probe("highContrast", () => System.Windows.SystemParameters.HighContrast.ToString());
                Probe("windowGlassBrush", () => $"{System.Windows.SystemParameters.WindowGlassBrush != null}");
                Probe("dropShadow", () => System.Windows.SystemParameters.DropShadow.ToString());
                Probe("clientAreaAnimation", () => System.Windows.SystemParameters.ClientAreaAnimation.ToString());
                Probe("primaryScreenWidth", () => System.Windows.SystemParameters.PrimaryScreenWidth.ToString());
                Probe("workArea", () => System.Windows.SystemParameters.WorkArea.ToString());
                Probe("caretWidth", () => System.Windows.SystemParameters.CaretWidth.ToString());
                Probe("minimumHorizontalDragDistance", () => System.Windows.SystemParameters.MinimumHorizontalDragDistance.ToString());
                Probe("systemcolors", () => $"windowBrush={System.Windows.SystemColors.WindowBrush} " +
                                            $"controlText={System.Windows.SystemColors.ControlTextColor}");
                Probe("fonts", () => $"count={System.Windows.Media.Fonts.SystemFontFamilies.Count}");
                Probe("systemfonts", () => $"message={System.Windows.SystemFonts.MessageFontFamily} " +
                                           $"size={System.Windows.SystemFonts.MessageFontSize}");

                // Offscreen rendering and the imaging codecs: the paths an app uses to export or
                // thumbnail its own content, none of which the on-screen compositor exercises.
                Probe("RenderTargetBitmap", () =>
                {
                    var visual = new System.Windows.Media.DrawingVisual();
                    using (var dc = visual.RenderOpen())
                        dc.DrawRectangle(Brushes.Coral, null, new Rect(0, 0, 64, 32));
                    var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(64, 32, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                    rtb.Render(visual);
                    return $"{rtb.PixelWidth}x{rtb.PixelHeight}";
                });
                var sample = new System.Windows.Media.Imaging.RenderTargetBitmap(8, 8, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                {
                    var visual = new System.Windows.Media.DrawingVisual();
                    using (var dc = visual.RenderOpen())
                        dc.DrawRectangle(Brushes.SeaGreen, null, new Rect(0, 0, 8, 8));
                    sample.Render(visual);
                }
                Probe("BitmapFrame.Create", () => System.Windows.Media.Imaging.BitmapFrame.Create(sample).PixelWidth.ToString());
                byte[] encoded = null;
                Probe("PngBitmapEncoder.Save", () =>
                {
                    var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(sample));
                    using var stream = new System.IO.MemoryStream();
                    encoder.Save(stream);
                    encoded = stream.ToArray();
                    return $"{encoded.Length}B";
                });
                Probe("PngBitmapDecoder", () =>
                {
                    if (encoded == null) return "(skipped: nothing encoded)";
                    var decoded = new System.Windows.Media.Imaging.PngBitmapDecoder(
                        new System.IO.MemoryStream(encoded), System.Windows.Media.Imaging.BitmapCreateOptions.None,
                        System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
                    return $"{decoded.Frames[0].PixelWidth}x{decoded.Frames[0].PixelHeight}";
                });
                Probe("BitmapDecoder.Create(stream)", () =>
                {
                    if (encoded == null) return "(skipped)";
                    var d = System.Windows.Media.Imaging.BitmapDecoder.Create(
                        new System.IO.MemoryStream(encoded), System.Windows.Media.Imaging.BitmapCreateOptions.None,
                        System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
                    return $"{d.Frames[0].PixelWidth}x{d.Frames[0].PixelHeight}";
                });
                Probe("BitmapFrame.Create(stream)", () =>
                {
                    if (encoded == null) return "(skipped)";
                    var f = System.Windows.Media.Imaging.BitmapFrame.Create(new System.IO.MemoryStream(encoded));
                    return $"{f.PixelWidth}x{f.PixelHeight}";
                });
                Probe("BitmapImage(stream)", () =>
                {
                    if (encoded == null) return "(skipped)";
                    var bi = new System.Windows.Media.Imaging.BitmapImage();
                    bi.BeginInit();
                    bi.StreamSource = new System.IO.MemoryStream(encoded);
                    bi.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                    bi.EndInit();
                    return $"{bi.PixelWidth}x{bi.PixelHeight}";
                });
                byte[] jpeg = null;
                Probe("JpegBitmapEncoder.Save", () =>
                {
                    var encoder = new System.Windows.Media.Imaging.JpegBitmapEncoder { QualityLevel = 90 };
                    encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(sample));
                    using var stream = new System.IO.MemoryStream();
                    encoder.Save(stream);
                    jpeg = stream.ToArray();
                    return $"{jpeg.Length}B";
                });
                Probe("JpegBitmapDecoder(ours)", () =>
                {
                    if (jpeg == null) return "(skipped)";
                    var d = new System.Windows.Media.Imaging.JpegBitmapDecoder(
                        new System.IO.MemoryStream(jpeg), System.Windows.Media.Imaging.BitmapCreateOptions.None,
                        System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
                    return $"{d.Frames[0].PixelWidth}x{d.Frames[0].PixelHeight}";
                });

                Probe("FormattedText", () =>
                {
                    var ft = new System.Windows.Media.FormattedText("hello", System.Globalization.CultureInfo.InvariantCulture,
                        FlowDirection.LeftToRight,
                        new Typeface("Segoe UI"), 14, Brushes.Black, 96);
                    return $"{ft.Width:0.#}x{ft.Height:0.#}";
                });
                // Cascadia Code turns "!=" and "=>" into single glyphs through its GSUB 'liga'
                // feature. Comparing against the same number of non-ligating characters is what
                // shows whether shaping actually ran: equal widths mean it did not.
                Probe("ligatures", () =>
                {
                    var cascadia = new Typeface(new System.Windows.Media.FontFamily("Cascadia Code"),
                        FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
                    cascadia.TryGetGlyphTypeface(out System.Windows.Media.GlyphTypeface gt);
                    var ci = System.Globalization.CultureInfo.InvariantCulture;
                    var plain = new System.Windows.Media.FormattedText("xx", ci, FlowDirection.LeftToRight, cascadia, 20, Brushes.Black, 96);
                    var liga = new System.Windows.Media.FormattedText("!=", ci, FlowDirection.LeftToRight, cascadia, 20, Brushes.Black, 96);
                    var arrow = new System.Windows.Media.FormattedText("=>", ci, FlowDirection.LeftToRight, cascadia, 20, Brushes.Black, 96);
                    string face = gt == null ? "(none)" : System.Linq.Enumerable.FirstOrDefault(gt.FamilyNames.Values);
                    return $"face='{face}' xx={plain.Width:0.##} !={liga.Width:0.##} arrow={arrow.Width:0.##}";
                });

                // Control: DejaVu Sans has classic f-ligatures in a plain type-4 'liga' lookup. If
                // "fi" collapses to one glyph here but Cascadia's "!=" does not, the engine works and
                // the gap is specifically GSUB lookup type 6 (chained context), which is how
                // programming-ligature fonts do it.
                Probe("ligatures-control", () =>
                {
                    var dejavu = new Typeface(new System.Windows.Media.FontFamily("DejaVu Sans"),
                        FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
                    var ci = System.Globalization.CultureInfo.InvariantCulture;
                    var fi = new System.Windows.Media.FormattedText("fi", ci, FlowDirection.LeftToRight, dejavu, 20, Brushes.Black, 96);
                    return $"width(fi)={fi.Width:0.##}";
                });

                Probe("SystemSounds", () => { System.Media.SystemSounds.Beep.Play(); return "played"; });
                Probe("PrintDialog", () => new System.Windows.Controls.PrintDialog().PrintQueue?.FullName ?? "(no queue)");
                Probe("XpsDocumentWriter", () =>
                {
                    var server = new System.Printing.LocalPrintServer();
                    var queues = server.GetPrintQueues();
                    int count = 0;
                    foreach (var q in queues) count++;
                    return $"queues={count}";
                });
                // XAML has probably never run here: every sample in this repo is code-only, so the
                // markup path -- parser, type resolution, pack URIs, BAML-backed themes -- is exactly
                // the kind of thing that can be broken without anything noticing.
                Probe("XamlReader.Parse", () =>
                {
                    const string xaml =
                        "<StackPanel xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'" +
                        " xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>" +
                        "<TextBlock Text='hello' Foreground='Red'/>" +
                        "<Button Content='ok' Width='60'/>" +
                        "</StackPanel>";
                    var panel = (StackPanel)System.Windows.Markup.XamlReader.Parse(xaml);
                    var text = (TextBlock)panel.Children[0];
                    return $"children={panel.Children.Count} text='{text.Text}' brush={text.Foreground}";
                });
                Probe("XAML ResourceDictionary", () =>
                {
                    const string xaml =
                        "<ResourceDictionary xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'" +
                        " xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'" +
                        " xmlns:sys='clr-namespace:System;assembly=System.Runtime'>" +
                        "<SolidColorBrush x:Key='b' Color='#FF3366CC'/>" +
                        "<sys:Double x:Key='d'>12.5</sys:Double>" +
                        "</ResourceDictionary>";
                    var rd = (ResourceDictionary)System.Windows.Markup.XamlReader.Parse(xaml);
                    return $"brush={rd["b"]} double={rd["d"]}";
                });
                Probe("XAML Style+Template", () =>
                {
                    const string xaml =
                        "<Button xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'" +
                        " xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' Content='x'>" +
                        "<Button.Template><ControlTemplate TargetType='Button'>" +
                        "<Border Background='LightGreen'><ContentPresenter/></Border>" +
                        "</ControlTemplate></Button.Template></Button>";
                    var b = (Button)System.Windows.Markup.XamlReader.Parse(xaml);
                    b.Measure(new Size(200, 200));
                    return $"template={b.Template != null} desired={b.DesiredSize.Width:0}x{b.DesiredSize.Height:0}";
                });
                Probe("pack URI resource", () =>
                {
                    // The Fluent theme dictionary ships as BAML inside PresentationFramework.Fluent.
                    // ResourceDictionary.Source is the API that takes an absolute pack URI;
                    // Application.LoadComponent only accepts a relative one.
                    var rd = new ResourceDictionary
                    {
                        Source = new Uri("pack://application:,,,/PresentationFramework.Fluent;component/Themes/Fluent.xaml", UriKind.Absolute),
                    };
                    return $"keys={rd.Count} merged={rd.MergedDictionaries.Count}";
                });
                Probe("Storyboard animation", () =>
                {
                    var border = new Border { Width = 10 };
                    var anim = new System.Windows.Media.Animation.DoubleAnimation(10, 50, TimeSpan.FromMilliseconds(1));
                    var sb = new System.Windows.Media.Animation.Storyboard();
                    sb.Children.Add(anim);
                    System.Windows.Media.Animation.Storyboard.SetTarget(anim, border);
                    System.Windows.Media.Animation.Storyboard.SetTargetProperty(anim, new PropertyPath("Width"));
                    sb.Begin();
                    return "began";
                });
                Probe("Effects", () =>
                {
                    var b = new Border { Effect = new System.Windows.Media.Effects.BlurEffect { Radius = 4 } };
                    var d = new Border { Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 6 } };
                    return $"blur={b.Effect != null} dropShadow={d.Effect != null}";
                });
                Probe("Cursor(IBeam)", () => System.Windows.Input.Cursors.IBeam.ToString());
                Probe("Window.Icon", () =>
                {
                    var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(16, 16, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                    window.Icon = rtb;
                    return "set";
                });
                Probe("Topmost", () => { window.Topmost = true; window.Topmost = false; return "toggled"; });
                // MessageBox is shown and dismissed by a nested timer, because ShowDialog blocks on a
                // nested dispatcher frame -- which is exactly the thing worth proving still works.
                Probe("messagebox-deferred", () => "see [probe] messagebox below");
                var closer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                closer.Tick += (_, _) =>
                {
                    closer.Stop();
                    foreach (Window w in app.Windows)
                        if (w != window) { w.Close(); break; }
                };
                closer.Start();
                Probe("messagebox", () => MessageBox.Show("probe", "probe", MessageBoxButton.OKCancel).ToString());
                window.Close();
            };
            timer.Start();
        }

        // --jpeg-out DIR renders a known image and saves it as PNG plus JPEG at several qualities, so
        // the managed encoders can be checked against a real decoder (PIL/libjpeg) rather than against
        // our own. A colour gradient with hard edges is deliberate: flat fills would hide both chroma
        // subsampling and ringing.
        int jpegOutIndex = Array.IndexOf(args, "--jpeg-out");
        if (jpegOutIndex >= 0 && jpegOutIndex + 1 < args.Length)
        {
            string dir = args[jpegOutIndex + 1];
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                try
                {
                    const int w = 160, h = 120;
                    var visual = new System.Windows.Media.DrawingVisual();
                    using (var dc = visual.RenderOpen())
                    {
                        dc.DrawRectangle(new LinearGradientBrush(Colors.Red, Colors.Blue, 0), null, new Rect(0, 0, w, h));
                        dc.DrawRectangle(Brushes.Lime, null, new Rect(20, 20, 40, 40));
                        dc.DrawRectangle(Brushes.White, null, new Rect(90, 30, 50, 25));
                        dc.DrawRectangle(Brushes.Black, null, new Rect(60, 80, 60, 20));
                    }
                    var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(w, h, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                    rtb.Render(visual);

                    var png = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
                    using (var fs = System.IO.File.Create(System.IO.Path.Combine(dir, "ref.png"))) png.Save(fs);
                    Console.Error.WriteLine("[jpeg] wrote ref.png");

                    // Odd dimensions on purpose: neither is a multiple of 16, so the right and bottom
                    // MCUs are padded, which is where edge-handling bugs hide.
                    foreach ((int ow, int oh) in new[] { (13, 7), (17, 33) })
                    {
                        var odd = new System.Windows.Media.DrawingVisual();
                        using (var dc = odd.RenderOpen())
                        {
                            dc.DrawRectangle(new LinearGradientBrush(Colors.Yellow, Colors.Purple, 45), null, new Rect(0, 0, ow, oh));
                            dc.DrawRectangle(Brushes.Cyan, null, new Rect(1, 1, 3, 3));
                        }
                        var oddRtb = new System.Windows.Media.Imaging.RenderTargetBitmap(ow, oh, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                        oddRtb.Render(odd);

                        var oddPng = new System.Windows.Media.Imaging.PngBitmapEncoder();
                        oddPng.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(oddRtb));
                        using (var fs = System.IO.File.Create(System.IO.Path.Combine(dir, $"ref-{ow}x{oh}.png"))) oddPng.Save(fs);

                        var oddJpeg = new System.Windows.Media.Imaging.JpegBitmapEncoder { QualityLevel = 90 };
                        oddJpeg.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(oddRtb));
                        using (var fs = System.IO.File.Create(System.IO.Path.Combine(dir, $"out-{ow}x{oh}.jpg"))) oddJpeg.Save(fs);
                        Console.Error.WriteLine($"[jpeg] wrote {ow}x{oh} pair");
                    }

                    // BMP and TIFF are lossless, so these can be checked pixel-exact against ref.png
                    // rather than by a similarity metric.
                    var bmp = new System.Windows.Media.Imaging.BmpBitmapEncoder();
                    bmp.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
                    using (var fs = System.IO.File.Create(System.IO.Path.Combine(dir, "out.bmp"))) bmp.Save(fs);
                    Console.Error.WriteLine("[jpeg] wrote out.bmp");

                    var tiff = new System.Windows.Media.Imaging.TiffBitmapEncoder();
                    tiff.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
                    using (var fs = System.IO.File.Create(System.IO.Path.Combine(dir, "out.tiff"))) tiff.Save(fs);
                    Console.Error.WriteLine("[jpeg] wrote out.tiff");

                    var gif = new System.Windows.Media.Imaging.GifBitmapEncoder();
                    gif.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
                    using (var fs = System.IO.File.Create(System.IO.Path.Combine(dir, "out.gif"))) gif.Save(fs);
                    Console.Error.WriteLine("[jpeg] wrote out.gif");

                    // A few-colour image with transparency: GIF should keep this one exactly, and it is
                    // the case that exercises the transparent palette index.
                    {
                        const int fw = 40, fh = 30;
                        var flat = new System.Windows.Media.DrawingVisual();
                        using (var dc = flat.RenderOpen())
                        {
                            dc.DrawRectangle(Brushes.Red, null, new Rect(0, 0, fw, 10));
                            dc.DrawRectangle(Brushes.Lime, null, new Rect(0, 10, fw, 10));
                            dc.DrawRectangle(Brushes.Blue, null, new Rect(0, 20, 20, 10));
                            // bottom-right stays untouched -> fully transparent
                        }
                        var flatRtb = new System.Windows.Media.Imaging.RenderTargetBitmap(fw, fh, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                        flatRtb.Render(flat);

                        var flatPng = new System.Windows.Media.Imaging.PngBitmapEncoder();
                        flatPng.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(flatRtb));
                        using (var fs = System.IO.File.Create(System.IO.Path.Combine(dir, "ref-flat.png"))) flatPng.Save(fs);

                        var flatGif = new System.Windows.Media.Imaging.GifBitmapEncoder();
                        flatGif.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(flatRtb));
                        using (var fs = System.IO.File.Create(System.IO.Path.Combine(dir, "out-flat.gif"))) flatGif.Save(fs);
                        Console.Error.WriteLine("[jpeg] wrote flat gif pair");
                    }

                    // Far more than 256 distinct colours, so this is the case that actually exercises
                    // median-cut quantization rather than the exact-palette shortcut.
                    {
                        const int mw = 220, mh = 180;
                        var many = new System.Windows.Media.DrawingVisual();
                        using (var dc = many.RenderOpen())
                        {
                            dc.DrawRectangle(new LinearGradientBrush(Colors.Red, Colors.Blue, 0), null, new Rect(0, 0, mw, mh));
                            var fade = new LinearGradientBrush(Color.FromArgb(0, 255, 255, 0), Color.FromArgb(255, 0, 255, 0), 90);
                            dc.DrawRectangle(fade, null, new Rect(0, 0, mw, mh));
                        }
                        var manyRtb = new System.Windows.Media.Imaging.RenderTargetBitmap(mw, mh, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                        manyRtb.Render(many);

                        var manyPng = new System.Windows.Media.Imaging.PngBitmapEncoder();
                        manyPng.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(manyRtb));
                        using (var fs = System.IO.File.Create(System.IO.Path.Combine(dir, "ref-many.png"))) manyPng.Save(fs);

                        var manyGif = new System.Windows.Media.Imaging.GifBitmapEncoder();
                        manyGif.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(manyRtb));
                        using (var fs = System.IO.File.Create(System.IO.Path.Combine(dir, "out-many.gif"))) manyGif.Save(fs);
                        Console.Error.WriteLine("[jpeg] wrote many-colour gif pair");
                    }

                    foreach (int q in new[] { 30, 75, 95 })
                    {
                        var jpeg = new System.Windows.Media.Imaging.JpegBitmapEncoder { QualityLevel = q };
                        jpeg.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
                        string path = System.IO.Path.Combine(dir, $"out-q{q}.jpg");
                        using (var fs = System.IO.File.Create(path)) jpeg.Save(fs);
                        Console.Error.WriteLine($"[jpeg] wrote out-q{q}.jpg {new System.IO.FileInfo(path).Length}B");
                    }
                }
                catch (Exception e)
                {
                    Console.Error.WriteLine($"[jpeg] THREW {e.GetType().Name}: {e.Message}");
                }
                window.Close();
            };
            timer.Start();
        }

        // --auto-clipboard round-trips text through the compositor's selection. Both directions run in
        // this one process, which is the case most likely to deadlock: when we own the selection the
        // compositor asks US to write the data, and that only arrives if we are still dispatching.
        if (Array.IndexOf(args, "--auto-clipboard") >= 0)
        {
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                try
                {
                    string sent = "wpf-linux-clipboard-" + Environment.ProcessId;
                    Clipboard.SetText(sent);
                    string got = Clipboard.GetText();
                    Console.Error.WriteLine($"[probe] clipboard sent=\"{sent}\" got=\"{got}\" {(sent == got ? "MATCH" : "MISMATCH")}");
                }
                catch (Exception e)
                {
                    Console.Error.WriteLine($"[probe] clipboard THREW {e.GetType().Name}: {e.Message}");
                }
                window.Close();
            };
            timer.Start();
        }

        // --auto-dragdrop starts a drag programmatically. A real drag needs a human holding a button,
        // but the SOURCE entry point does not: DoDragDrop is reached the same way either case, so this
        // catches the failure mode where merely asking to drag throws.
        if (Array.IndexOf(args, "--auto-dragdrop") >= 0)
        {
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                // EXPECTED TO THROW off Windows, and checked so it stays a known quantity rather than a
                // surprise: DataObject's constructor registers in the COM Global Interface Table and
                // P/Invokes OLE32.dll. Apps hand DoDragDrop a plain value or their own IDataObject
                // instead, which is the path exercised below.
                Probe("DataObject (expected to throw off Windows)", () =>
                {
                    var probe = new DataObject();
                    return $"formats=[{string.Join(",", probe.GetFormats())}]";
                });

                // No button is held here, so a real drag cannot legally start and None is the correct
                // answer. What this proves is that asking no longer throws.
                Probe("DoDragDrop(string)", () => DragDrop.DoDragDrop(window, "dragged text", DragDropEffects.Copy).ToString());
                Probe("DoDragDrop(files)", () => DragDrop.DoDragDrop(window, new[] { "/tmp/a.txt" }, DragDropEffects.Copy).ToString());
                window.Close();
            };
            timer.Start();
        }

        return app.Run(window);
    }

    /// <summary>Run one probe, reporting either its value or the exception it threw.</summary>
    private static void Probe(string name, Func<string> body)
    {
        try
        {
            Console.Error.WriteLine($"[probe] {name} => {body()}");
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[probe] {name} THREW {e.GetType().Name}: {e.Message}");
            for (Exception inner = e.InnerException; inner != null; inner = inner.InnerException)
                Console.Error.WriteLine($"[probe]   inner {inner.GetType().Name}: {inner.Message}");
        }
    }

    private static Window BuildWindow()
    {
        var status = new TextBlock
        {
            Text = "ready",
            Margin = new Thickness(0, 8, 0, 0),
            Foreground = Brushes.DimGray,
        };

        var heading = new TextBlock
        {
            Text = "WPF on WebGPU — Linux/Wayland",
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
        };

        var button = new Button { Content = "Click me", Width = 120, HorizontalAlignment = HorizontalAlignment.Left };
        int clicks = 0;
        button.Click += (_, _) => status.Text = $"button clicked {++clicks}x";

        var textBox = new TextBox { Width = 260, HorizontalAlignment = HorizontalAlignment.Left };
        textBox.TextChanged += (_, _) => status.Text = $"typed: \"{textBox.Text}\"";

        var combo = new ComboBox { Width = 180, HorizontalAlignment = HorizontalAlignment.Left, SelectedIndex = 0 };
        foreach (string item in new[] { "Popup item one", "Popup item two", "Popup item three" })
            combo.Items.Add(item);
        combo.SelectionChanged += (_, _) => status.Text = $"combo: {combo.SelectedItem}";

        var list = new ListBox { Height = 90, Width = 260, HorizontalAlignment = HorizontalAlignment.Left };
        for (int i = 1; i <= 30; i++) list.Items.Add($"Scrollable row {i}");

        var dialogButton = new Button { Content = "Modal dialog", Width = 120, HorizontalAlignment = HorizontalAlignment.Left };
        dialogButton.Click += (_, _) =>
        {
            // A nested dispatcher frame. Works on Linux (and macOS) because the blocking run loop is
            // kept, unlike the iOS/Android/browser heads where ShowDialog throws.
            var dialog = new Window
            {
                Title = "Nested frame",
                Width = 320,
                Height = 160,
                Owner = Application.Current.MainWindow,
                Content = new StackPanel
                {
                    Margin = new Thickness(16),
                    Children =
                    {
                        new TextBlock { Text = "This is a modal dialog on a nested dispatcher frame.", TextWrapping = TextWrapping.Wrap },
                    },
                },
            };
            dialog.ShowDialog();
            status.Text = "modal dialog closed";
        };

        // --- desktop integration (gate G8) ---------------------------------------------------

        var clipboardBox = new TextBox { Width = 260, HorizontalAlignment = HorizontalAlignment.Left, Text = "copy me" };

        var copyButton = new Button { Content = "Copy", Width = 90 };
        copyButton.Click += (_, _) =>
        {
            Clipboard.SetText(clipboardBox.Text);
            status.Text = $"copied \"{clipboardBox.Text}\" — paste it into another app";
        };

        var pasteButton = new Button { Content = "Paste", Width = 90 };
        pasteButton.Click += (_, _) =>
        {
            string text = Clipboard.GetText();
            status.Text = string.IsNullOrEmpty(text) ? "clipboard is empty" : $"pasted: \"{text}\"";
        };

        var messageBoxButton = new Button { Content = "MessageBox", Width = 110 };
        messageBoxButton.Click += (_, _) =>
        {
            MessageBoxResult answer = MessageBox.Show(
                "This message box is drawn by WPF itself — Linux has no native one, and the portal does not provide one.",
                "MessageBox", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            status.Text = $"message box answered: {answer}";
        };

        var openFileButton = new Button { Content = "Open file…", Width = 110 };
        openFileButton.Click += (_, _) =>
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Pick a file" };
            status.Text = dialog.ShowDialog() == true
                ? $"chose: {dialog.FileName}"
                : "file dialog cancelled";
        };

        var themeButton = new Button { Content = "Read theme", Width = 110 };
        themeButton.Click += (_, _) =>
            status.Text = $"desktop colour scheme: {(IsDarkTheme() ? "dark" : "light")}";

        var integrationRow1 = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (UIElement b in new UIElement[] { copyButton, pasteButton })
        {
            ((FrameworkElement)b).Margin = new Thickness(0, 0, 8, 0);
            integrationRow1.Children.Add(b);
        }

        var integrationRow2 = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (UIElement b in new UIElement[] { messageBoxButton, openFileButton, themeButton })
        {
            ((FrameworkElement)b).Margin = new Thickness(0, 0, 8, 0);
            integrationRow2.Children.Add(b);
        }

        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(heading);
        panel.Children.Add(new TextBlock
        {
            Text = "Right-click anywhere for a context menu.",
            Margin = new Thickness(0, 4, 0, 12),
            Foreground = Brushes.DimGray,
        });
        foreach (UIElement child in new UIElement[]
                 { button, textBox, combo, list, dialogButton, clipboardBox, integrationRow1, integrationRow2 })
        {
            panel.Children.Add(child);
            ((FrameworkElement)child).Margin = new Thickness(0, 0, 0, 10);
        }
        panel.Children.Add(status);

        var menu = new ContextMenu();
        foreach (string item in new[] { "Context item A", "Context item B" })
        {
            var entry = new MenuItem { Header = item };
            string captured = item;
            entry.Click += (_, _) => status.Text = $"context menu: {captured}";
            menu.Items.Add(entry);
        }

        var window = new Window
        {
            Title = "WPF Linux smoke test",
            Width = 520,
            Height = 560,
            Content = panel,
            ContextMenu = menu,
        };

        window.SourceInitialized += (_, _) =>
        {
            // Setting Title after SourceInitialized used to throw DllNotFoundException off Windows
            // (SetWindowText P/Invoked user32 unguarded), so this doubles as a regression check.
            window.Title = "WPF Linux smoke test — initialized";
        };

        return window;
    }

    /// <summary>
    /// WPF's own view of the desktop theme. SystemParameters has no cross-platform colour-scheme
    /// property, so this reads what ThemeManager reads: the Fluent theme's active dictionary follows
    /// the same signal (xdg-desktop-portal's org.freedesktop.appearance color-scheme).
    /// </summary>
    private static bool IsDarkTheme()
    {
        Color background = SystemColors.WindowColor;
        return (background.R + background.G + background.B) / 3 < 128;
    }
}
