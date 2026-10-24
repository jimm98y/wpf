// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// An ElementHost's hosted WPF tree is a REAL child window of the form's host window on Windows, while
// the WinForms controls around it are only drawn. In app an ElementHost on one tab of a DotNetBar
// dock kept its window visible after another tab was selected, over the other tabs, and took every click
// meant for them (Properties/Dynamics/Preferences on the right, Explorer/Symbols after the Toolbox on the
// left). The hosted window now follows the control as a child window would: hidden with any hidden
// ancestor or when taken off the form, and lets the mouse through (HTTRANSPARENT) where it is covered by a control
// above it in z-order -- hidden when wholly covered, as a deselected dock tab brought behind is.
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
    public class ElementHostOcclusionTests
    {
        private static void OnSta(Action body)
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "the hosted WPF tree is a real child window only on Windows");
            Exception? failure = null;
            var t = new Thread(() => { try { body(); } catch (Exception ex) { failure = ex; } });
            t.TrySetApartmentState(ApartmentState.STA);
            t.Start();
            t.Join();
            if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }

        private static ElementHost Host() => new ElementHost
        {
            Dock = SWF.DockStyle.Fill,
            Child = new System.Windows.Controls.Border { Background = System.Windows.Media.Brushes.LightBlue },
        };

        private static IntPtr HostedWindow(ElementHost host)
        {
            FieldInfo? f = typeof(ElementHost).GetField("_source", BindingFlags.NonPublic | BindingFlags.Instance);
            IntPtr h = IntPtr.Zero;
            Assert.True(Pump(() => (h = (f?.GetValue(host) as HwndSource)?.Handle ?? IntPtr.Zero) != IntPtr.Zero, 10000), "the hosted window never came up");
            return h;
        }

        private static SWF.Form NewForm(string text) => new SWF.Form
        {
            Text = text, StartPosition = SWF.FormStartPosition.Manual, Location = new Point(40, 40), ClientSize = new Size(400, 300),
        };

        /// <summary>Whether the hosted window takes the mouse at <paramref name="clientPoint"/> of
        /// <paramref name="ctl"/>: visible, over the point, and not letting the mouse through there.</summary>
        private static bool Takes(IntPtr hosted, SWF.Control ctl, Point clientPoint)
        {
            if (!IsWindowVisible(hosted)) return false;
            Point s = ctl.PointToScreen(clientPoint);
            GetWindowRect(hosted, out RECT wr);
            // The driver's screen space is the real one here (no test origin, scale 1 in the test runs).
            int x = s.X - wr.left, y = s.Y - wr.top;
            if (x < 0 || y < 0 || x >= wr.right - wr.left || y >= wr.bottom - wr.top) return false;
            // What Windows asks a window before giving it the mouse there.
            int ht = (int)SendMessageW(hosted, 0x0084, IntPtr.Zero, (IntPtr)((s.Y << 16) | (s.X & 0xFFFF)));
            return ht != -1;   // HTTRANSPARENT: through to the window below
        }

        [Fact]
        public void TwoPanelsInOnePlace_TheHostedWindowFollowsWhicheverIsInFront()
        {
            OnSta(() =>
            {
                using var form = NewForm("OcclusionFront");
                var withHost = new SWF.Panel { Bounds = new Rectangle(0, 0, 400, 300) };
                var host = Host(); withHost.Controls.Add(host);
                var withButton = new SWF.Panel { Bounds = new Rectangle(0, 0, 400, 300) };
                withButton.Controls.Add(new SWF.Button { Text = "Other tab", Bounds = new Rectangle(10, 10, 120, 30) });
                form.Controls.Add(withButton);
                form.Controls.Add(withHost);
                withHost.BringToFront();
                form.Show();
                IntPtr hosted = HostedWindow(host);
                Assert.True(Pump(() => Takes(hosted, host, new Point(200, 150)), 3000), "in front: the hosted window takes the mouse");

                // A dock tab switch by z-order: the other panel in front, this one still Visible.
                withButton.BringToFront();
                Assert.True(Pump(() => !IsWindowVisible(hosted), 3000), "wholly covered by the panel in front: hidden");
                Assert.False(Takes(hosted, host, new Point(70, 25)));

                withHost.BringToFront();
                Assert.True(Pump(() => Takes(hosted, host, new Point(200, 150)), 3000), "in front again: shown");
                form.Close();
            });
        }

        [Fact]
        public void ATabControl_HidesTheHostedWindowWithItsPage()
        {
            OnSta(() =>
            {
                using var form = NewForm("OcclusionTabs");
                var tabs = new SWF.TabControl { Dock = SWF.DockStyle.Fill };
                var page1 = new SWF.TabPage("WPF"); var page2 = new SWF.TabPage("Button");
                var host = Host(); page1.Controls.Add(host);
                page2.Controls.Add(new SWF.Button { Text = "Click me", Bounds = new Rectangle(10, 10, 120, 30) });
                tabs.TabPages.Add(page1); tabs.TabPages.Add(page2);
                form.Controls.Add(tabs);
                form.Show();
                IntPtr hosted = HostedWindow(host);
                Assert.True(Pump(() => IsWindowVisible(hosted), 3000));
                tabs.SelectedTab = page2;
                Assert.True(Pump(() => !IsWindowVisible(hosted), 3000), "its page deselected: hidden");
                tabs.SelectedTab = page1;
                Assert.True(Pump(() => IsWindowVisible(hosted), 3000), "its page selected again: shown");
                form.Close();
            });
        }

        [Fact]
        public void AHiddenPanel_OrRemovalFromTheForm_HidesTheHostedWindow()
        {
            OnSta(() =>
            {
                using var form = NewForm("OcclusionHidden");
                var panel = new SWF.Panel { Dock = SWF.DockStyle.Fill };
                var host = Host(); panel.Controls.Add(host);
                form.Controls.Add(panel);
                form.Show();
                IntPtr hosted = HostedWindow(host);
                Assert.True(Pump(() => IsWindowVisible(hosted), 3000));
                panel.Visible = false;
                Assert.True(Pump(() => !IsWindowVisible(hosted), 3000), "parent hidden: hidden");
                panel.Visible = true;
                Assert.True(Pump(() => IsWindowVisible(hosted), 3000), "parent shown: shown");
                panel.Controls.Remove(host);
                Assert.True(Pump(() => !IsWindowVisible(hosted), 3000), "taken off the form: hidden, not left where it was");
                host.Dispose();
                form.Close();
            });
        }

        [Fact]
        public void AControlAboveItPartly_IsCutOutOfTheHostedWindow()
        {
            OnSta(() =>
            {
                using var form = NewForm("OcclusionPartial");
                var host = Host();
                var button = new SWF.Button { Text = "Above", Bounds = new Rectangle(250, 100, 120, 60) };
                form.Controls.Add(host);
                form.Controls.Add(button);
                button.BringToFront();
                form.Show();
                IntPtr hosted = HostedWindow(host);
                Assert.True(Pump(() => Takes(hosted, host, new Point(50, 50)) && !Takes(hosted, host, new Point(300, 130)), 3000),
                    "the button's area is cut out of the hosted window; the rest takes the mouse");
                button.SendToBack();
                Assert.True(Pump(() => Takes(hosted, host, new Point(300, 130)), 3000), "the button sent behind: the whole host again");
                form.Close();
            });
        }

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

        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int left, top, right, bottom; }
        [DllImport("user32")] private static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32")] private static extern IntPtr SendMessageW(IntPtr h, int msg, IntPtr w, IntPtr l);
    }
}
