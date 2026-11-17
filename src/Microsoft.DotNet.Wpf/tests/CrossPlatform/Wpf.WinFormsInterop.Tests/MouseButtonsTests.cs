// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The mouse beyond the left and right buttons, as Windows delivers it: the middle button, the extra ones
// (WM_XBUTTONDOWN/UP, XButton1 and XButton2 in MouseDown, MouseUp and MouseClick, the button in the high
// word of wParam), every held button in a move's wParam (a right-button drag says MK_RBUTTON), and the
// horizontal wheel, WM_MOUSEHWHEEL, which a child that does not take it passes to its parent.
//

using System;
using System.Collections.Generic;
using System.Drawing;
using Xunit;
using SWF = System.Windows.Forms;

namespace Wpf.WinFormsInterop.Tests
{
    public class MouseButtonsTests
    {
        private sealed class Panel2 : SWF.Panel
        {
            public readonly List<string> Wheel = new();

            protected override void WndProc(ref SWF.Message m)
            {
                if (m.Msg == 0x20E) Wheel.Add($"hwheel {(short)((long)m.WParam >> 16)}");
                base.WndProc(ref m);
            }
        }

        private static (SWF.Form Form, Panel2 Outer, SWF.Panel Inner) Make(int y)
        {
            var form = new SWF.Form { StartPosition = SWF.FormStartPosition.Manual, Location = new Point(9500, y), ClientSize = new Size(300, 200), ShowInTaskbar = false };
            var outer = new Panel2 { Bounds = new Rectangle(10, 10, 260, 170) };
            var inner = new SWF.Panel { Bounds = new Rectangle(20, 20, 200, 120) };
            outer.Controls.Add(inner);
            form.Controls.Add(outer);
            form.Show();
            SWF.Application.DoEvents();
            return (form, outer, inner);
        }

        [Fact]
        public void TheExtraButtons_AreXButton1AndXButton2()
        {
            SWF.XplatUIWebGpu driver = SWF.XplatUIWebGpu.GetInstance();
            var (form, _, inner) = Make(2500);
            using var _f = form;
            var log = new List<string>();
            inner.MouseDown += (s, e) => log.Add($"down {e.Button}");
            inner.MouseUp += (s, e) => log.Add($"up {e.Button}");
            inner.MouseClick += (s, e) => log.Add($"click {e.Button}");
            Point at = inner.PointToScreen(new Point(30, 30));

            driver.InjectXButtonDown(at.X, at.Y, 1);
            Assert.Equal(SWF.MouseButtons.XButton1, SWF.Control.MouseButtons);
            driver.InjectXButtonUp(at.X, at.Y, 1);
            driver.InjectXButtonDown(at.X + 40, at.Y, 2);
            driver.InjectXButtonUp(at.X + 40, at.Y, 2);
            driver.InjectMiddleDown(at.X, at.Y + 40);
            driver.InjectMiddleUp(at.X, at.Y + 40);
            Assert.Equal(SWF.MouseButtons.None, SWF.Control.MouseButtons);

            Assert.Equal(new[] { "down XButton1", "click XButton1", "up XButton1", "down XButton2", "click XButton2", "up XButton2",
                                 "down Middle", "click Middle", "up Middle" }, log);
        }

        [Fact]
        public void ARightButtonDrag_SaysTheRightButtonIsDown()
        {
            SWF.XplatUIWebGpu driver = SWF.XplatUIWebGpu.GetInstance();
            var (form, _, inner) = Make(2900);
            using var _f = form;
            var moves = new List<SWF.MouseButtons>();
            inner.MouseMove += (s, e) => moves.Add(e.Button);
            Point at = inner.PointToScreen(new Point(30, 30));

            driver.InjectRightDown(at.X, at.Y);
            driver.InjectMouseMove(at.X + 5, at.Y, false);
            driver.InjectRightUp(at.X + 5, at.Y);
            driver.InjectMouseMove(at.X + 10, at.Y, false);

            Assert.Equal(SWF.MouseButtons.Right, moves[0]);
            Assert.Equal(SWF.MouseButtons.None, moves[^1]);
        }

        [Fact]
        public void TheHorizontalWheel_GoesToTheParentWhenTheChildDoesNotTakeIt()
        {
            SWF.XplatUIWebGpu driver = SWF.XplatUIWebGpu.GetInstance();
            var (form, outer, inner) = Make(3300);
            using var _f = form;
            Point at = inner.PointToScreen(new Point(30, 30));

            driver.InjectHWheel(at.X, at.Y, 120);
            driver.InjectHWheel(at.X, at.Y, -240);

            Assert.Equal(new[] { "hwheel 120", "hwheel -240" }, outer.Wheel);
        }
    }
}
