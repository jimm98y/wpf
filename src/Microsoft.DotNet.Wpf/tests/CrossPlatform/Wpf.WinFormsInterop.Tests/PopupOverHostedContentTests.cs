// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// A WinForms popup over hosted WPF content (a context menu opened on an ElementHost's canvas).
//
// The hosted tree is a real child window under the host's frame, so Windows gives it the mouse
// wherever it is -- also where one of the driver's popups is drawn over it. Clicks aimed at a menu
// then reached the canvas instead: a quick second right-click opened another menu, and DotNetBar
// left the first one's popup window behind on the canvas, empty, for good. Over a popup the hosted
// window now answers WM_NCHITTEST with HTTRANSPARENT, so the host window (and through it the popup)
// takes the input; elsewhere it keeps it.
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
    public class PopupOverHostedContentTests
    {
        private sealed class Popup : SWF.Control
        {
            protected override SWF.CreateParams CreateParams
            {
                get
                {
                    var cp = base.CreateParams;
                    cp.Style = unchecked((int)0x86000000);   // WS_POPUP | WS_CLIPSIBLINGS | WS_CLIPCHILDREN, as DotNetBar's
                    cp.ExStyle = 0x80;                        // WS_EX_TOOLWINDOW
                    return cp;
                }
            }
        }

        [Fact]
        public void HostedWindowLetsTheMouseThroughOnlyWhereAPopupIsDrawn()
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "the hosted WPF tree is a real child window only on Windows");
            Exception? failure = null;
            var t = new Thread(() =>
            {
                try { Run(); }
                catch (Exception ex) { failure = ex; }
            });
            t.TrySetApartmentState(ApartmentState.STA);
            t.Start();
            t.Join();
            if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }

        private static void Run()
        {
            using var form = new SWF.Form { Text = "PopupOverHosted", StartPosition = SWF.FormStartPosition.Manual, Location = new Point(40, 40), ClientSize = new Size(400, 300) };
            var host = new ElementHost { Dock = SWF.DockStyle.Fill, Child = new System.Windows.Controls.Border { Background = System.Windows.Media.Brushes.White } };
            form.Controls.Add(host);
            form.Show();
            HwndSource? source = null;
            FieldInfo? f = typeof(ElementHost).GetField("_source", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.True(Pump(() => (source = f?.GetValue(host) as HwndSource) != null, 10000), "the hosted window never came up");

            // A point in the middle of the hosted area, on screen.
            Point center = host.PointToScreen(new Point(200, 150));
            POINT real = ClientCenter(source!.Handle);
            Assert.NotEqual(HTTRANSPARENT, HitTest(source.Handle, real));            // nothing over it: WPF keeps it

            using var popup = new Popup { Bounds = new Rectangle(center.X - 50, center.Y - 50, 100, 100) };
            popup.CreateControl();
            SWF.Application.DoEvents();
            ShowWindow(popup.Handle);
            Pump(() => false, 300);
            Assert.Equal(HTTRANSPARENT, HitTest(source.Handle, real));               // under the popup: through

            popup.Hide();
            Pump(() => false, 200);
            Assert.NotEqual(HTTRANSPARENT, HitTest(source.Handle, real));            // popup gone: WPF again
            form.Close();
        }

        private static void ShowWindow(IntPtr h)
        {
            // As DotNetBar shows its popup containers.
            MethodInfo? set = typeof(SWF.Control).Assembly.GetType("System.Windows.Forms.XplatUI")
                ?.GetMethod("SetVisible", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
            set?.Invoke(null, new object[] { h, true, false });
        }

        private static bool Pump(Func<bool> done, int ms)
        {
            var end = DateTime.UtcNow.AddMilliseconds(ms);
            while (DateTime.UtcNow < end)
            {
                // A message loop's turn: the driver's queue and the hosts' frame (which runs the
                // hosted WPF tree and puts the windows on screen).
                SWF.Application.DoEvents();
                s_tick?.Invoke(null, null);
                if (done()) return true;
                Thread.Sleep(15);
            }
            return done();
        }

        private static readonly MethodInfo? s_tick = typeof(SWF.Control).Assembly.GetType("System.Windows.Forms.PresentationHost")
            ?.GetMethod("Tick", BindingFlags.NonPublic | BindingFlags.Static);

        private const int HTTRANSPARENT = -1;

        private static POINT ClientCenter(IntPtr hwnd)
        {
            GetClientRect(hwnd, out RECT r);
            var p = new POINT { x = r.right / 2, y = r.bottom / 2 };
            ClientToScreen(hwnd, ref p);
            return p;
        }

        private static int HitTest(IntPtr hwnd, POINT screen)
            => (int)SendMessageW(hwnd, 0x0084, IntPtr.Zero, (IntPtr)((screen.y << 16) | (screen.x & 0xFFFF)));

        [StructLayout(LayoutKind.Sequential)] private struct POINT { public int x, y; }
        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int left, top, right, bottom; }
        [DllImport("user32")] private static extern IntPtr SendMessageW(IntPtr h, int msg, IntPtr w, IntPtr l);
        [DllImport("user32")] private static extern bool GetClientRect(IntPtr h, out RECT r);
        [DllImport("user32")] private static extern bool ClientToScreen(IntPtr h, ref POINT p);
    }
}
