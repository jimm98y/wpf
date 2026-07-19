using System;
using System.Collections.Generic;
using System.Reflection;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using SWF = System.Windows.Forms;
using SD = System.Drawing;
using Microsoft.Wpf.Interop.WebGpu.Composition;

// A lightweight WPF control that embeds a Mono System.Windows.Forms control tree and composites its
// WebGPU scene DIRECTLY into the WPF scene (no bitmap): the WinForms XplatUIWebGpu driver records the
// SAME SceneVisual type WPF's compositor renders, so each hosted window's scene is registered with
// EmbeddedContent and overlaid by WpfCompositionSink at this element's device rect. (A functional
// stand-in for WindowsFormsHost, which is bound to the Windows-only real WinForms.) Shared by every
// gallery head (mac / Windows / browser) — the bridge is platform-neutral.
internal sealed class WinFormsHost : FrameworkElement
{
    // Resilient entry point for the shared gallery: returns the live WinForms card, or a small labelled
    // placeholder if the WinForms stack can't initialize on this head (e.g. the browser, where the Mono
    // WinForms driver is not yet proven). Keeps the rest of the gallery rendering regardless.
    public static FrameworkElement CreateDemoCardOrFallback()
    {
        try { return CreateDemoCard(); }
        catch (Exception ex)
        {
            Console.Error.WriteLine("WINFORMS-CARD-FAILED: " + ex);
            return new System.Windows.Controls.TextBlock
            {
                Text = "WinForms host unavailable on this platform\n(" + ex.GetType().Name + ")",
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.Gray,
                Width = 224,
                Height = 156,
            };
        }
    }

    // Builds the gallery's "WinForms" card: sets up the WinForms driver, a small interactive form, and
    // returns a host element whose scene composites into the WPF frame. Call once, from the WPF thread.
    public static FrameworkElement CreateDemoCard()
    {
        Environment.SetEnvironmentVariable("WF_WEBGPU", "1");        // WinForms XplatUIWebGpu driver
        Environment.SetEnvironmentVariable("WF_GPU_RASTER", "1");    // WinForms controls -> WebGPU scene
        SWF.Application.SetCompatibleTextRenderingDefault(false);

        // Fit the card's CONTENT area (Card is 224x196 with 14px padding and a ~21px header, so the
        // demo region is ~196x147). The embedded scene is composited at the host's device rect and is
        // NOT subject to WPF's card clip, so an oversized form would spill past the card — size to fit.
        const int FormW = 196, FormH = 132;
        var form = new SWF.Form { Width = FormW, Height = FormH, BackColor = SD.Color.FromArgb(0xF2, 0xF2, 0xEC) };
        // Bump from the 8.25pt default (~11px) to ~13px for readability. Reuse the form's EXISTING default
        // family (already constructed) rather than SD.FontFamily.GenericSansSerif — the generic-family ctor
        // P/Invokes gdiplus, which is absent in the browser (libgdiplus-free) and throws DllNotFoundException.
        form.Font = new SD.Font(form.Font.FontFamily, 9.75f);
        var btn = new SWF.Button { Text = "WinForms Button", Left = 10, Top = 8, Width = 176, Height = 26 };
        var chk = new SWF.CheckBox { Text = "A WinForms checkbox", Left = 10, Top = 42, Width = 176, Checked = true };
        var lbl = new SWF.Label { Text = "Clicks: 0", Left = 10, Top = 70, Width = 176 };
        var tb  = new SWF.TextBox { Left = 10, Top = 94, Width = 176, Text = "editable" };
        int clicks = 0;
        btn.Click += (s, e) => { clicks++; lbl.Text = $"Clicks: {clicks}"; lbl.Invalidate(); };
        form.Controls.Add(btn); form.Controls.Add(chk); form.Controls.Add(lbl); form.Controls.Add(tb);
        return new WinFormsHost(form) { Width = FormW, Height = FormH };
    }

        private readonly SWF.Form _form;                 // the hosted WinForms surface (off-screen; never shown as its own window)
        private readonly object _driver;
        private readonly MethodInfo _getPresent, _getScene, _getVersion, _getSize, _getCaret;
        private readonly MethodInfo _down, _up, _move, _char, _keyDown, _wheel;
        private int _formOx, _formOy;                    // the form's origin in the driver's screen space (updated each render)
        private bool _leftDown;
        private bool _logged;
        private bool _caretSelfClicked;
        private int _framesSeen;

        public WinFormsHost(SWF.Form form)
        {
            _form = form;
            var xplat = typeof(SWF.Control).Assembly.GetType("System.Windows.Forms.XplatUI");
            _driver = xplat.GetField("driver", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
            var dt = _driver.GetType();
            MethodInfo M(string n) => dt.GetMethod(n, BindingFlags.NonPublic | BindingFlags.Instance);
            _getPresent = M("GetPresentWindows"); _getScene = M("GetWindowScene");
            _getVersion = M("GetPaintVersion"); _getSize = M("GetWindowSizePacked");
            _getCaret = M("GetCaret");
            _down = M("InjectMouseDown"); _up = M("InjectMouseUp"); _move = M("InjectMouseMove");
            _char = M("InjectChar"); _keyDown = M("InjectKeyDown"); _wheel = M("InjectWheel");

            Focusable = true;                             // so the embedded controls can receive typed text
            _form.CreateControl();
            _form.Show();                                 // registers the window tree in the driver + paints (no CocoaHost = not a real window)
            foreach (SWF.Control c in Flatten(_form)) c.Invalidate(true);
            SWF.Application.DoEvents();

            // Self-drive: pump WinForms + re-register the scene every WPF render frame, and keep frames
            // coming, so the embedded controls stay live regardless of the host app's own render loop.
            CompositionTarget.Rendering += (s, e) => { UpdateFrame(); InvalidateVisual(); };
        }

        // ---- input: forward WPF mouse/keyboard over this element to the WinForms driver -------------
        // A WPF point (DIPs, relative to this element = the form's client origin) maps to the driver's
        // screen coords by adding the form origin — WinForms logical units and WPF DIPs are both 96dpi.

        private (int, int) ToDriver(Point p) => (_formOx + (int)Math.Round(p.X), _formOy + (int)Math.Round(p.Y));

        // DIAG: inject a click at a form-client point (bypasses WPF events) to headlessly verify the
        // embedded controls are interactive.
        internal void DebugClick(int clientX, int clientY)
        {
            int x = _formOx + clientX, y = _formOy + clientY;
            _move.Invoke(_driver, new object[] { x, y, false });
            _down.Invoke(_driver, new object[] { x, y });
            _up.Invoke(_driver, new object[] { x, y });
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            var (x, y) = ToDriver(e.GetPosition(this));
            _move.Invoke(_driver, new object[] { x, y, _leftDown });
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            Focus(); CaptureMouse();
            var (x, y) = ToDriver(e.GetPosition(this));
            _leftDown = true;
            _move.Invoke(_driver, new object[] { x, y, false });
            _down.Invoke(_driver, new object[] { x, y });
            e.Handled = true;
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            var (x, y) = ToDriver(e.GetPosition(this));
            _leftDown = false;
            _up.Invoke(_driver, new object[] { x, y });
            ReleaseMouseCapture();
            e.Handled = true;
        }

        protected override void OnMouseWheel(MouseWheelEventArgs e)
        {
            var (x, y) = ToDriver(e.GetPosition(this));
            _wheel?.Invoke(_driver, new object[] { x, y, e.Delta > 0 ? 120 : -120 });
            e.Handled = true;
        }

        protected override void OnTextInput(TextCompositionEventArgs e)
        {
            foreach (char ch in e.Text) _char.Invoke(_driver, new object[] { ch });
            e.Handled = true;
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            int vk = VirtualKey(e.Key);
            if (vk != 0) { _keyDown.Invoke(_driver, new object[] { vk }); e.Handled = true; }
        }

        // WPF Key -> Win32 virtual-key for the non-text keys WinForms editors act on.
        private static int VirtualKey(Key k) => k switch
        {
            Key.Back => 0x08, Key.Tab => 0x09, Key.Enter => 0x0D, Key.Escape => 0x1B, Key.Delete => 0x2E,
            Key.Left => 0x25, Key.Up => 0x26, Key.Right => 0x27, Key.Down => 0x28,
            Key.Home => 0x24, Key.End => 0x23, _ => 0,
        };

        protected override Size MeasureOverride(Size availableSize) => new Size(_form.Width, _form.Height);

        // The embedded scene is injected by the sink, not drawn here — but WPF only routes mouse input to
        // an element that has HIT-TEST geometry, so fill our bounds with a transparent rect (invisible,
        // hit-testable) or clicks pass straight through and never reach the WinForms controls.
        protected override void OnRender(DrawingContext dc)
            => dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));

        // Called every frame from the app's render tick (NOT OnRender — invalidating the window doesn't
        // re-run a child's OnRender, so DoEvents/repaint would never pump). Pumps WinForms so controls
        // repaint, then registers every hosted window's scene at this element's device rect.
        public void UpdateFrame()
        {
            SWF.Application.DoEvents();

            var src = PresentationSource.FromVisual(this);
            if (src == null || !IsVisible) return;         // not laid out yet
            double dpi = src.CompositionTarget.TransformToDevice.M11;
            Point o;
            try { o = this.TransformToAncestor(src.RootVisual).Transform(new Point(0, 0)); }
            catch { return; }                              // transient during layout/teardown
            float hostDevX = (float)(o.X * dpi), hostDevY = (float)(o.Y * dpi);

            long[] wins = (long[])_getPresent.Invoke(_driver, new object[] { _form.Handle });
            int ox = wins.Length >= 3 ? (int)wins[1] : 0, oy = wins.Length >= 3 ? (int)wins[2] : 0;
            _formOx = ox; _formOy = oy;   // for input coordinate mapping
            if (!_caretSelfClicked && Environment.GetEnvironmentVariable("WF_DIAG_CARET") == "1" && _framesSeen++ > 10)
            { _caretSelfClicked = true; DebugClick(98, 105); }   // focus the textbox to raise the caret
            bool diag = !_logged && Environment.GetEnvironmentVariable("WF_DIAG_EMBED") == "1";
            if (diag)
            { _logged = true; Console.Error.WriteLine($"EMBED dpi={dpi} hostDIP=({o.X},{o.Y}) hostDev=({hostDevX},{hostDevY}) form={_form.Width}x{_form.Height} formOrigin=({ox},{oy}) renderSize={RenderSize} nwins={wins.Length / 3}"); }
            var items = new List<EmbeddedItem>(wins.Length / 3);
            for (int i = 0; i + 2 < wins.Length; i += 3)
            {
                IntPtr h = (IntPtr)wins[i];
                object scene = _getScene.Invoke(_driver, new object[] { h });
                if (scene == null) continue;
                long sz = (long)_getSize.Invoke(_driver, new object[] { h });
                int w = (int)(sz >> 32), hh = (int)(sz & 0xFFFFFFFF);
                float px = hostDevX + ((int)wins[i + 1] - ox) * (float)dpi;
                float py = hostDevY + ((int)wins[i + 2] - oy) * (float)dpi;
                if (diag) Console.Error.WriteLine($"  win h=0x{((long)h):x} scr=({wins[i + 1]},{wins[i + 2]}) size={w}x{hh} -> dev rect=({px},{py} {w * (float)dpi}x{hh * (float)dpi})");
                items.Add(new EmbeddedItem { Scene = scene, DeviceX = px, DeviceY = py,
                                             DeviceW = w * (float)dpi, DeviceH = hh * (float)dpi, Scale = (float)dpi });
            }
            EmbeddedContent.Set(items);

            // Text caret: the driver tracks CreateCaret/SetCaretPos + blink (toggled by WinForms' blink
            // timer, which our DoEvents pump advances). Place it in device pixels on top of the controls.
            if (_getCaret != null)
            {
                object[] a = { 0, 0, 0, 0 };
                bool vis = (bool)_getCaret.Invoke(_driver, a);
                if (vis)
                {
                    int cx = (int)a[0], cy = (int)a[1], cw = (int)a[2], ch = (int)a[3];
                    EmbeddedContent.SetCaret(hostDevX + (cx - ox) * (float)dpi, hostDevY + (cy - oy) * (float)dpi,
                                             Math.Max(1, cw) * (float)dpi, ch * (float)dpi, true);
                }
                else EmbeddedContent.SetCaret(0, 0, 0, 0, false);
            }
        }

        private static IEnumerable<SWF.Control> Flatten(SWF.Control c)
        { yield return c; foreach (SWF.Control ch in c.Controls) foreach (var g in Flatten(ch)) yield return g; }
    }
