// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// A child's frame is the child's. A framed control (a sunken text box, a list) keeps its border in
// the non-client area round its client (XplatUIWebGpu.ChildFrame), and Windows hit-tests that
// border as the child's: a click there is the child's WM_NCLBUTTONDOWN with HTBORDER, and none of
// it reaches the window behind. Hit-testing the client alone gave the click to the parent.
//

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Reflection;
using System.Threading;
using Xunit;
using SWF = System.Windows.Forms;

namespace Wpf.WinFormsInterop.Tests
{
    public class NonClientHitTests
    {
        private const int WM_NCLBUTTONDOWN = 0x00A1, WM_LBUTTONDOWN = 0x0201, HTBORDER = 18;

        private sealed class Recording : SWF.TextBox
        {
            public readonly List<(int Msg, IntPtr WParam)> Seen = new List<(int, IntPtr)>();
            protected override void WndProc(ref SWF.Message m)
            {
                if (m.Msg == WM_NCLBUTTONDOWN || m.Msg == WM_LBUTTONDOWN) Seen.Add((m.Msg, m.WParam));
                base.WndProc(ref m);
            }
        }

        private static object Driver() => typeof(SWF.Control).Assembly.GetType("System.Windows.Forms.XplatUI")!
            .GetField("driver", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

        private static void Press(SWF.Form root, Point screen)
        {
            object d = Driver();
            var t = d.GetType();
            t.GetMethod("InjectMouseDownIn", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(d, new object[] { root.Handle, screen.X, screen.Y });
            t.GetMethod("InjectMouseUpIn", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(d, new object[] { root.Handle, screen.X, screen.Y });
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

        [Fact]
        public void AClickOnAChildsBorder_IsTheChildsNonClientHit()
        {
            OnSta(() =>
            {
                using var form = new SWF.Form { StartPosition = SWF.FormStartPosition.Manual, Location = new Point(40, 40), ClientSize = new Size(240, 120), ShowInTaskbar = false };
                var panel = new SWF.Panel { Bounds = new Rectangle(0, 0, 240, 120) };
                var box = new Recording { Bounds = new Rectangle(20, 20, 150, 30), BorderStyle = SWF.BorderStyle.Fixed3D };
                int panelDowns = 0;
                panel.MouseDown += (s, e) => panelDowns++;
                panel.Controls.Add(box);
                form.Controls.Add(panel);
                form.Show();
                SWF.Application.DoEvents();

                // The client starts inside the border: a point a pixel left of it is on the frame.
                Assert.True(box.Width > box.ClientSize.Width, "a Fixed3D text box keeps a frame");
                Point onBorder = box.PointToScreen(new Point(-1, box.ClientSize.Height / 2));
                Press(form, onBorder);
                Point inside = box.PointToScreen(new Point(box.ClientSize.Width / 2, box.ClientSize.Height / 2));
                Press(form, inside);
                form.Close();

                Assert.Equal(0, panelDowns);
                Assert.Contains((WM_NCLBUTTONDOWN, (IntPtr)HTBORDER), box.Seen);
                Assert.Contains(box.Seen, s => s.Msg == WM_LBUTTONDOWN);
            });
        }
    }
}
