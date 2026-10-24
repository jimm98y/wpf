// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The behavior service's adorner window lies over the whole design surface, as in .NET, and must neither hide
// the designed controls nor take their mouse: it is see-through, and it answers WM_NCHITTEST with HTTRANSPARENT
// where no glyph wants the pointer -- which the driver honours for a WS_EX_TRANSPARENT child, as Windows does --
// and over a glyph that does, the glyph's behavior gets the press. Off the desktop (x = 8000).
//

using System;
using System.ComponentModel.Design;
using System.Drawing;
using System.Reflection;
using System.Threading;
using System.Windows.Forms.Design;
using System.Windows.Forms.Design.Behavior;
using Xunit;
using SWF = System.Windows.Forms;

namespace Wpf.WinFormsInterop.Tests
{
    public class DesignerAdornerWindowTests
    {
        private sealed class QuietForm : SWF.Form
        {
            protected override bool ShowWithoutActivation => true;
        }

        private sealed class PressBehavior : Behavior
        {
            public int Presses;
            public override bool OnMouseDown(Glyph g, SWF.MouseButtons button, Point mouseLoc) { Presses++; return true; }
        }

        private sealed class BoxGlyph : Glyph
        {
            private readonly Rectangle _r;
            public BoxGlyph(Rectangle r, Behavior b) : base(b) => _r = r;
            public override Rectangle Bounds => _r;
            public override SWF.Cursor GetHitTest(Point p) => _r.Contains(p) ? SWF.Cursors.Hand : null!;
            public override void Paint(SWF.PaintEventArgs pe) => pe.Graphics.FillRectangle(Brushes.Lime, _r);
        }

        private static object? HostFor(SWF.Form form)
        {
            var hostOf = typeof(SWF.Form).Assembly.GetType("System.Windows.Forms.PresentationHost")!
                .GetMethod("HostOf", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!;
            return hostOf.Invoke(null, new object[] { form });
        }

        private static IntPtr WindowAt(Point screen)
        {
            object driver = typeof(SWF.Control).Assembly.GetType("System.Windows.Forms.XplatUI")!
                .GetField("driver", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
            return (IntPtr)driver.GetType().GetMethod("WindowAtPoint", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(driver, new object[] { screen.X, screen.Y })!;
        }

        [Fact]
        public void TheAdornerWindow_ShowsAndPassesThroughTheDesignedControls_AndAGlyphTakesItsPress()
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "a Windows host window");
            Exception? failure = null;
            var t = new Thread(() =>
            {
                try { Run(); }
                catch (Exception ex) { failure = ex; }
            });
            t.TrySetApartmentState(ApartmentState.STA);
            t.Start();
            Assert.True(t.Join(TimeSpan.FromSeconds(60)), "the UI thread did not finish");
            if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }

        private static void Run()
        {
            using var surface = new DesignSurface();
            surface.BeginLoad(typeof(SWF.UserControl));
            var host = (IDesignerHost)surface.GetService(typeof(IDesignerHost))!;
            var root = (SWF.UserControl)host.RootComponent;
            root.Size = new Size(200, 150);
            var button = (SWF.Button)host.CreateComponent(typeof(SWF.Button), "button1");
            button.Bounds = new Rectangle(20, 20, 80, 40);
            button.BackColor = Color.Red;
            button.FlatStyle = SWF.FlatStyle.Flat;
            root.Controls.Add(button);
            var behaviors = (BehaviorService)surface.GetService(typeof(BehaviorService))!;

            using var form = new QuietForm
            {
                StartPosition = SWF.FormStartPosition.Manual, Location = new Point(8000, 80), ClientSize = new Size(400, 300),
                ShowInTaskbar = false, FormBorderStyle = SWF.FormBorderStyle.None,
            };
            var view = (SWF.Control)surface.View;
            view.Dock = SWF.DockStyle.Fill;
            form.Controls.Add(view);

            Exception? inLoop = null;
            int ticks = 0;
            var timer = new SWF.Timer { Interval = 30 };
            timer.Tick += (s, e) =>
            {
                object? h = HostFor(form);
                if (++ticks < 300 && (h == null || ticks < 15)) return;
                timer.Stop();
                try
                {
                    Point mid = button.PointToScreen(new Point(40, 20));
                    // Nothing over the button wants the pointer: it reaches the button.
                    Assert.Equal(button.Handle, WindowAt(mid));

                    // ...and the button shows: its red, not a blank adorner window.
                    var render = h!.GetType().GetMethod("RenderLastPresented", BindingFlags.Instance | BindingFlags.NonPublic)!;
                    object?[] args = { null, null };
                    var rgba = (byte[])render.Invoke(h, args)!;
                    int w = (int)args[0]!;
                    Point inForm = form.PointToClient(mid);
                    double scale = form.DeviceDpi / 96.0;
                    int i = ((int)(inForm.Y * scale) * w + (int)(inForm.X * scale)) * 4;
                    Assert.True(rgba[i] > 200 && rgba[i + 1] < 60 && rgba[i + 2] < 60,
                        $"under the adorner window the button shows rgb({rgba[i]},{rgba[i + 1]},{rgba[i + 2]})");

                    // A glyph over the button takes the pointer there, and its behavior the press.
                    var press = new PressBehavior();
                    var adorner = new Adorner();
                    Rectangle inAdorner = behaviors.ControlRectInAdornerWindow(button);
                    adorner.Glyphs.Add(new BoxGlyph(new Rectangle(inAdorner.X, inAdorner.Y, 20, 20), press));
                    behaviors.Adorners.Add(adorner);
                    Point onGlyph = button.PointToScreen(new Point(5, 5));
                    IntPtr took = WindowAt(onGlyph);
                    Assert.NotEqual(button.Handle, took);
                    var adornerWindow = SWF.Control.FromHandle(took);
                    Assert.Equal("AdornerWindow", adornerWindow?.Text);
                    // A press there, as the driver delivers one: to the window under the point.
                    object driver = typeof(SWF.Control).Assembly.GetType("System.Windows.Forms.XplatUI")!
                        .GetField("driver", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
                    driver.GetType().GetMethod("InjectMouseDown", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(driver, new object[] { onGlyph.X, onGlyph.Y });
                    driver.GetType().GetMethod("InjectMouseUp", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(driver, new object[] { onGlyph.X, onGlyph.Y });
                    SWF.Application.DoEvents();
                    Assert.Equal(1, press.Presses);
                    // Elsewhere the button still gets it.
                    Assert.Equal(button.Handle, WindowAt(mid));
                }
                catch (Exception ex) { inLoop = ex; }
                form.Close();
            };
            form.Shown += (s, e) => timer.Start();
            SWF.Application.Run(form);
            timer.Dispose();
            if (inLoop != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(inLoop).Throw();
        }

    }
}
