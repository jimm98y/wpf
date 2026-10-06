using System;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

internal static class Host
{
    private static int Main(string[] args)
    {
        Application.ThreadException += (s, e) => Console.Error.WriteLine("THREADEX: " + e.Exception);
        // Deliberately opt into the GDI TextRenderer path (as the VS WinForms template does) to prove
        // the driver forces GDI+ text back on in GPU-raster mode — no host-side setup needed.
        Application.SetCompatibleTextRenderingDefault(false);

        int seconds = args.Length > 0 && int.TryParse(args[0], out int s) ? s : 0;

        // GPU-raster seam test: draw with REAL System.Drawing.Graphics verbs (incl. Mono's own
        // ControlPaint.DrawButton) with the recorder attached, so the pixels come from WGSL, not
        // libgdiplus. Proves control drawing flows through WebGPU inside our System.Drawing.
        if (Array.IndexOf(args, "gpuraster-test") >= 0)
            return GpuRasterTest(args.Length > 1 ? args[1] : "gpu-controlpaint.png");

        var f = new Form { Text = "WinForms on macOS", Width = 460, Height = 340, BackColor = Color.FromArgb(0xEC, 0xEC, 0xE4) };
        var gb = new GroupBox { Text = "Options", Left = 12, Top = 8, Width = 210, Height = 120 };
        var r1 = new RadioButton { Text = "Fast", Left = 14, Top = 24, Width = 120, Checked = true };
        var r2 = new RadioButton { Text = "Accurate", Left = 14, Top = 50, Width = 140 };
        var cb = new CheckBox { Text = "Verbose", Left = 14, Top = 80, Width = 140, Checked = true };
        gb.Controls.Add(r1); gb.Controls.Add(r2); gb.Controls.Add(cb);
        var combo = new ComboBox { Left = 236, Top = 12, Width = 200, DropDownStyle = ComboBoxStyle.DropDownList };
        combo.Items.AddRange(new object[] { "Direct3D 12", "Vulkan", "Metal", "OpenGL" }); combo.SelectedIndex = 2;
        var list = new ListBox { Left = 236, Top = 48, Width = 200, Height = 96 };
        list.Items.AddRange(new object[] { "alpha", "bravo", "charlie", "delta", "echo" }); list.SelectedIndex = 1;
        var lbl = new Label { Text = "Name:", Left = 12, Top = 150 };
        var tb = new TextBox { Left = 70, Top = 147, Width = 150, Text = "Ada Lovelace" };
        var prog = new ProgressBar { Left = 12, Top = 182, Width = 210, Height = 20, Value = 40 };
        var btn = new Button { Text = "Click me", Left = 12, Top = 220, Width = 120, Height = 32 };
        var counter = new Label { Text = "Clicks: 0", Left = 148, Top = 226, Width = 140 };
        f.Controls.Add(gb); f.Controls.Add(combo); f.Controls.Add(list); f.Controls.Add(lbl);
        f.Controls.Add(tb); f.Controls.Add(prog); f.Controls.Add(btn); f.Controls.Add(counter);

        int clicks = 0;
        btn.Click += (s2, e) => { clicks++; counter.Text = $"Clicks: {clicks}"; counter.Invalidate(); prog.Value = Math.Min(100, prog.Value + 10); prog.Invalidate(); Console.WriteLine($"Button.Click -> {clicks}"); };
        cb.CheckedChanged += (s2, e) => Console.WriteLine($"CheckBox -> {cb.Checked}");
        list.SelectedIndexChanged += (s2, e) => Console.WriteLine($"List -> {list.SelectedItem}");

        // From here on this is an ORDINARY WinForms app: the window, the message pump and the WebGPU
        // present all belong to System.Windows.Forms now (WinFormsInterop/host), so Application.Run
        // is all it takes. It used to need a hand-rolled window + pump + present loop right here.
        bool selftest = Array.IndexOf(args, "selftest") >= 0;
        int exit = 0;

        if (seconds > 0)
        {
            var close = new System.Windows.Forms.Timer { Interval = seconds * 1000 };
            close.Tick += (s2, e) => { close.Stop(); f.Close(); };
            close.Start();
        }

        // Self-test: inject clicks through the driver like real window messages do, proving the
        // on-screen window is interactive (click -> handler -> label update -> re-present).
        if (selftest)
        {
            int step = 0;
            var probe = new System.Windows.Forms.Timer { Interval = 150 };
            probe.Tick += (s2, e) =>
            {
                switch (++step)
                {
                    case 2: ClickAt(btn.Left + btn.Width / 2, btn.Top + btn.Height / 2); break;
                    case 4: ClickAt(btn.Left + btn.Width / 2, btn.Top + btn.Height / 2); break;
                    case 6: var p = cb.PointToScreen(new Point(8, cb.Height / 2)); ClickAt(p.X, p.Y); break;
                    case 8:
                        exit = clicks == 2 && !cb.Checked ? 0 : 5;
                        probe.Stop();
                        f.Close();
                        break;
                }
            };
            probe.Start();
        }

        Console.WriteLine("WinForms window shown. Interact with it, or wait for the timeout.");
        Application.Run(f);
        Console.WriteLine($"done. total clicks={clicks} checkbox={cb.Checked}");
        return exit;
    }

    // A click at a screen point through the driver's own hit-testing — the same entry real window
    // messages take, minus the physical mouse (which would lose races with whoever is at the keyboard).
    private static void ClickAt(int x, int y)
    {
        object driver = typeof(Control).Assembly.GetType("System.Windows.Forms.XplatUI")
            .GetField("driver", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            .GetValue(null);
        driver.GetType().GetMethod("InjectClick",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
              .Invoke(driver, new object[] { x, y });
    }

    // Draw real WinForms controls through the GPU-raster seam and render the recorded scene with WGSL.
    private static int GpuRasterTest(string outPath)
    {
        const int W = 300, H = 140;
        object scene;
        using (var bmp = new System.Drawing.Bitmap(W, H))
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        {
            System.Drawing.WebGpuBackend.GpuRaster.Begin(g);           // subsequent verbs -> WebGPU scene (data only)
            using var face = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(212, 208, 200));
            g.FillRectangle(face, 0, 0, W, H);
            // Mono's OWN theme drawing code (draws the 3D button via DrawLine/FillRectangle):
            ControlPaint.DrawButton(g, new System.Drawing.Rectangle(30, 30, 110, 34), ButtonState.Normal);
            ControlPaint.DrawButton(g, new System.Drawing.Rectangle(30, 84, 110, 34), ButtonState.Pushed);
            using var font = new System.Drawing.Font(System.Drawing.FontFamily.GenericSansSerif, 9f);
            g.DrawString("Click me", font, System.Drawing.Brushes.Black, 52, 38);
            g.DrawString("Pressed", font, System.Drawing.Brushes.Black, 55, 92);
            // A DrawImage: a small checkerboard bitmap drawn 1:1 and stretched (exercises the image verb).
            using (var icon = new System.Drawing.Bitmap(24, 24))
            {
                for (int yy = 0; yy < 24; yy++)
                    for (int xx = 0; xx < 24; xx++)
                        icon.SetPixel(xx, yy, ((xx / 6 + yy / 6) % 2 == 0) ? System.Drawing.Color.OrangeRed : System.Drawing.Color.SteelBlue);
                g.DrawImage(icon, 200, 30);                 // 1:1
                g.DrawImage(icon, 200, 66, 48, 24);         // stretched 2x wide
            }
            // Multi-stop linear gradient rect (red->green->blue via InterpolationColors).
            using (var grad = new System.Drawing.Drawing2D.LinearGradientBrush(
                new System.Drawing.Rectangle(160, 100, 120, 16), System.Drawing.Color.Red, System.Drawing.Color.Blue,
                System.Drawing.Drawing2D.LinearGradientMode.Horizontal))
            {
                grad.InterpolationColors = new System.Drawing.Drawing2D.ColorBlend
                {
                    Colors = new[] { System.Drawing.Color.Red, System.Drawing.Color.Lime, System.Drawing.Color.Blue },
                    Positions = new[] { 0f, 0.5f, 1f },
                };
                g.FillRectangle(grad, 160, 100, 120, 16);
            }
            // Radial gradient (PathGradientBrush) filling an ellipse: white centre -> navy edge.
            using (var path = new System.Drawing.Drawing2D.GraphicsPath())
            {
                path.AddEllipse(160, 120, 40, 20);
                using var rad = new System.Drawing.Drawing2D.PathGradientBrush(path)
                {
                    CenterColor = System.Drawing.Color.White,
                    SurroundColors = new[] { System.Drawing.Color.Navy },
                };
                g.FillEllipse(rad, 160, 120, 40, 20);
            }
            // Arcs: a full-circle ring (radio-style) + a half arc.
            using (var pen = new System.Drawing.Pen(System.Drawing.Color.Black))
            {
                g.DrawArc(pen, 160, 30, 24, 24, 0, 359);
                g.DrawArc(pen, 160, 62, 30, 20, 20, 200);
            }
            scene = System.Drawing.WebGpuBackend.GpuRaster.EndScene(g);
        }
        // Render the recorded scene with WGSL (offscreen) and save.
        using var ctx = Microsoft.Wpf.Interop.WebGpu.Composition.WgpuContext.Create();
        var tf = new Microsoft.Wpf.Interop.WebGpu.Composition.Text.TrueTypeFont(
            System.IO.File.ReadAllBytes("/System/Library/Fonts/Supplemental/Arial.ttf"));
        using var renderer = new Microsoft.Wpf.Interop.WebGpu.Composition.WgpuSceneRenderer(
            ctx, tf, new Microsoft.Wpf.Interop.WebGpu.Composition.Text.SimpleTextShaper());
        byte[] rgba = renderer.RenderToRgba((Microsoft.Wpf.Interop.WebGpu.Composition.SceneVisual)scene,
            W, H, Microsoft.Wpf.Interop.WebGpu.Composition.RgbaColor.FromBytes(212, 208, 200, 255), srgbOutput: true);
        Microsoft.Wpf.Interop.WebGpu.Composition.PngWriter.Write(outPath, rgba, W, H, maxWidth: W);
        Console.WriteLine($"GPU-rasterized real Graphics/ControlPaint drawing -> {outPath}");
        return 0;
    }
}
