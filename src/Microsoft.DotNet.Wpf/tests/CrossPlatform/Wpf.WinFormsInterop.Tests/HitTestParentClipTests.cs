// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// A child window exists only inside its parents' client areas: Windows clips it there for the mouse as
// for painting. The driver tested a point against each window's own rectangle, so a control running
// past its parent took the clicks meant for what is drawn there. GraphWorX's Toolbox -- a tall ToolStrip
// in a scrolling panel of the left dock -- took every click on the dock's tab strip below it: the
// Explorer tab could only be clicked once the Toolbox was scrolled to its end. The hosted WPF window of
// an ElementHost that runs past its parent lets the mouse through there in the same way.
//

using System;
using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms.Integration;
using System.Windows.Interop;
using Xunit;
using SWF = System.Windows.Forms;

namespace Wpf.WinFormsInterop.Tests
{
    public class HitTestParentClipTests
    {
        private static object Driver() => typeof(SWF.Control).Assembly.GetType("System.Windows.Forms.XplatUI")!
            .GetField("driver", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

        private static void Click(SWF.Form root, SWF.Control at, Point local)
        {
            object d = Driver();
            Point p = at.PointToScreen(local);
            var t = d.GetType();
            t.GetMethod("InjectMouseDownIn", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(d, new object[] { root.Handle, p.X, p.Y });
            t.GetMethod("InjectMouseUpIn", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(d, new object[] { root.Handle, p.X, p.Y });
            SWF.Application.DoEvents();
        }

        private static void OnSta(Action a)
        {
            Exception? failure = null;
            var t = new Thread(() => { try { a(); } catch (Exception ex) { failure = ex; } });
            t.TrySetApartmentState(ApartmentState.STA);
            t.Start();
            t.Join();
            if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }

        /// <summary>The dock: a scrolling panel (Fill) whose content is taller than it is, and a tab strip
        /// docked at the bottom -- added last, so it is behind the panel in z-order, as DotNetBar's Bar adds
        /// its strip. A click on the strip is the strip's.</summary>
        [Fact]
        public void AControlRunningPastItsParent_DoesNotTakeClicksOutsideIt()
        {
            OnSta(() =>
            {
                using var form = new SWF.Form { StartPosition = SWF.FormStartPosition.Manual, Location = new Point(40, 40), ClientSize = new Size(200, 200), ShowInTaskbar = false };
                var panel = new SWF.Panel { Bounds = new Rectangle(0, 0, 200, 150), AutoScroll = false };
                int tallClicks = 0, tabClicks = 0;
                var tall = new SWF.Button { Bounds = new Rectangle(0, 0, 180, 400), Text = "Toolbox items" };
                tall.Click += (s, e) => tallClicks++;
                panel.Controls.Add(tall);
                var tab = new SWF.Button { Bounds = new Rectangle(0, 160, 120, 30), Text = "Explorer" };
                tab.Click += (s, e) => tabClicks++;
                form.Controls.Add(panel);
                form.Controls.Add(tab);          // behind the panel, as the dock's strip is
                form.Show();
                SWF.Application.DoEvents();
                Click(form, tab, new Point(60, 15));
                Assert.Equal(1, tabClicks);       // the strip, below the panel: not the content running past it
                Assert.Equal(0, tallClicks);
                Click(form, tall, new Point(90, 50));
                Assert.Equal(1, tallClicks);      // inside the panel the content is clicked as before
                form.Close();
            });
        }

        [Fact]
        public void AHostedWindowRunningPastItsParent_LetsTheMouseThroughThere()
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "the hosted WPF tree is a real child window only on Windows");
            OnSta(() =>
            {
                using var form = new SWF.Form { Text = "HitTestParentClip", StartPosition = SWF.FormStartPosition.Manual, Location = new Point(40, 40), ClientSize = new Size(300, 240), ShowInTaskbar = false };
                var panel = new SWF.Panel { Bounds = new Rectangle(0, 0, 300, 150) };
                var host = new ElementHost { Bounds = new Rectangle(0, 0, 280, 400), Child = new System.Windows.Controls.Border { Background = System.Windows.Media.Brushes.LightBlue } };
                panel.Controls.Add(host);
                var tab = new SWF.Button { Bounds = new Rectangle(0, 170, 120, 30), Text = "Explorer" };
                form.Controls.Add(panel);
                form.Controls.Add(tab);
                form.Show();
                FieldInfo? f = typeof(ElementHost).GetField("_source", BindingFlags.NonPublic | BindingFlags.Instance);
                IntPtr hosted = IntPtr.Zero;
                Assert.True(Pump(() => (hosted = (f?.GetValue(host) as HwndSource)?.Handle ?? IntPtr.Zero) != IntPtr.Zero, 10000));
                Pump(() => false, 300);
                Point inside = host.PointToScreen(new Point(100, 50));
                Point overTab = tab.PointToScreen(new Point(60, 15));
                Assert.NotEqual(-1, HitTest(hosted, inside));    // inside its panel: the WPF window takes it
                Assert.Equal(-1, HitTest(hosted, overTab));      // past its panel, over the strip: through
                form.Close();
            });
        }

        private static int HitTest(IntPtr hwnd, Point screen)
            => (int)SendMessageW(hwnd, 0x0084, IntPtr.Zero, (IntPtr)((screen.Y << 16) | (screen.X & 0xFFFF)));

        private static bool Pump(Func<bool> done, int ms)
        {
            var end = DateTime.UtcNow.AddMilliseconds(ms);
            while (DateTime.UtcNow < end)
            {
                SWF.Application.DoEvents();
                s_tick?.Invoke(null, null);
                if (done()) return true;
                Thread.Sleep(15);
            }
            return done();
        }

        private static readonly MethodInfo? s_tick = typeof(SWF.Control).Assembly.GetType("System.Windows.Forms.PresentationHost")
            ?.GetMethod("Tick", BindingFlags.NonPublic | BindingFlags.Static);

        [DllImport("user32")] private static extern IntPtr SendMessageW(IntPtr h, int msg, IntPtr w, IntPtr l);
    }
}
