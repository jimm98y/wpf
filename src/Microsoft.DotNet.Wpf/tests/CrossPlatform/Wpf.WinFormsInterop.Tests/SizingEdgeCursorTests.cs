// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// A form that draws its own frame answers WM_NCHITTEST with HTLEFT..HTBOTTOMRIGHT along its edges, and
// Windows shows the size cursor there: WM_SETCURSOR carries the hit test, and DefWindowProc picks
// IDC_SIZEWE, IDC_SIZENS, IDC_SIZENWSE or IDC_SIZENESW for it, whatever the control under the pointer
// would show in its client. A ribbon form's edges showed the arrow.
//

using System;
using System.Drawing;
using System.Reflection;
using Xunit;
using SWF = System.Windows.Forms;

namespace Wpf.WinFormsInterop.Tests
{
    public class SizingEdgeCursorTests
    {
        /// <summary>A form whose whole window is client (as a ribbon form makes it) and whose hit test
        /// says where its sizing edges are, 6 pixels in from each side.</summary>
        private sealed class OwnFrameForm : SWF.Form
        {
            protected override void WndProc(ref SWF.Message m)
            {
                if (m.Msg == 0x84)   // WM_NCHITTEST
                {
                    var p = PointToClient(new Point((short)((long)m.LParam & 0xffff), (short)((long)m.LParam >> 16)));
                    bool l = p.X < 6, r = p.X >= ClientSize.Width - 6, t = p.Y < 6, b = p.Y >= ClientSize.Height - 6;
                    int hit = t && l ? 13 : t && r ? 14 : b && l ? 16 : b && r ? 17 : l ? 10 : r ? 11 : t ? 12 : b ? 15 : 1;
                    m.Result = (IntPtr)hit;
                    return;
                }
                base.WndProc(ref m);
            }
        }

        [Theory]
        [InlineData(2, 100, "SizeWE")]
        [InlineData(297, 100, "SizeWE")]
        [InlineData(150, 2, "SizeNS")]
        [InlineData(150, 197, "SizeNS")]
        [InlineData(2, 2, "SizeNWSE")]
        [InlineData(297, 197, "SizeNWSE")]
        [InlineData(297, 2, "SizeNESW")]
        [InlineData(2, 197, "SizeNESW")]
        public void ASizingEdge_ShowsTheSizeCursor(int x, int y, string expected)
        {
            SWF.XplatUIWebGpu driver = SWF.XplatUIWebGpu.GetInstance();
            using var form = new OwnFrameForm { StartPosition = SWF.FormStartPosition.Manual, Location = new Point(9500, 1300), ClientSize = new Size(300, 200), ShowInTaskbar = false };
            // the form shows a cursor of its own in its client: on an edge the size cursor wins
            form.Cursor = SWF.Cursors.IBeam;
            form.Controls.Add(new SWF.Panel { Bounds = new Rectangle(20, 20, 260, 160) });
            form.Show();
            SWF.Application.DoEvents();

            Point at = form.PointToScreen(new Point(x, y));
            driver.InjectMouseMove(at.X, at.Y, false);
            Assert.Equal(expected, Name(driver.GetActiveCursor()));

            Point inside = form.PointToScreen(new Point(10, 10));
            driver.InjectMouseMove(inside.X, inside.Y, false);
            Assert.Equal("IBeam", Name(driver.GetActiveCursor()));   // the client's own cursor inside
        }

        /// <summary>A ribbon form's caption: the form calls its top 30 pixels caption, and a strip across
        /// them answers HTCLIENT where its caption buttons are.</summary>
        private sealed class CaptionForm : SWF.Form
        {
            protected override void WndProc(ref SWF.Message m)
            {
                if (m.Msg == 0x84)
                {
                    var p = PointToClient(new Point((short)((long)m.LParam & 0xffff), (short)((long)m.LParam >> 16)));
                    m.Result = (IntPtr)(p.X < 6 ? 10 : p.Y < 30 ? 2 : 1);   // HTLEFT, HTCAPTION, HTCLIENT
                    return;
                }
                base.WndProc(ref m);
            }
        }

        [Fact]
        public void TheSizingEdgeIsTheFormsAndTheRestTheDeepestWindows()
        {
            SWF.XplatUIWebGpu driver = SWF.XplatUIWebGpu.GetInstance();
            using var form = new CaptionForm { StartPosition = SWF.FormStartPosition.Manual, Location = new Point(9500, 1700), ClientSize = new Size(300, 200), ShowInTaskbar = false };
            var buttons = new SWF.Panel { Bounds = new Rectangle(0, 0, 300, 30) };   // reaches the left edge too
            form.Controls.Add(buttons);
            form.Show();
            SWF.Application.DoEvents();

            Point edge = form.PointToScreen(new Point(2, 15)), onButtons = form.PointToScreen(new Point(250, 15));
            Assert.Equal(10, driver.FrameHitTest(form, edge.X, edge.Y));            // HTLEFT, though the strip is there
            Assert.Equal(0, driver.FrameHitTest(form, onButtons.X, onButtons.Y));   // the strip's client, not caption
            buttons.Visible = false;
            Assert.Equal(2, driver.FrameHitTest(form, onButtons.X, onButtons.Y));   // with nothing over it, caption
        }

        private static string Name(int id)
        {
            Type std = typeof(SWF.Control).Assembly.GetType("System.Windows.Forms.StdCursor")!;
            return Enum.GetName(std, id) ?? id.ToString();
        }
    }
}
