// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The moves Windows makes up when the capture changes, as measured on Windows 11 (SendInput, no
// movement): a press -- in which WinForms takes the capture -- is followed by WM_MOUSEMOVE at the same
// point with MK_LBUTTON, and the release (the capture let go) by one with no buttons. A capture taken
// from code with no button down is followed by nothing. Control.MouseButtons, meanwhile, is the buttons
// as those messages have them.
//
// DotNetBar's application menu opens over the File button under a still pointer and ignores moves that
// repeat its first one; the press's own move, with its button, is the one that differs, makes the item
// under the pointer hot, and lets the release close the menu. Without it a click on File did nothing.
//

using System.Collections.Generic;
using System.Drawing;
using Xunit;
using SWF = System.Windows.Forms;

namespace Wpf.WinFormsInterop.Tests
{
    public class CaptureMouseMoveTests
    {
        private sealed class Recorder : SWF.Panel
        {
            public readonly List<string> Log = new();

            protected override void WndProc(ref SWF.Message m)
            {
                if (m.Msg is 0x200 or 0x201 or 0x202)
                    Log.Add($"{(m.Msg == 0x200 ? "move" : m.Msg == 0x201 ? "down" : "up")} {(long)m.WParam & 0x13:x} ({(short)((long)m.LParam & 0xffff)},{(short)((long)m.LParam >> 16)})");
                base.WndProc(ref m);
            }
        }

        [Fact]
        public void APressThatDoesNotMove_IsFollowedByAMoveWithItsButton()
        {
            SWF.XplatUIWebGpu driver = SWF.XplatUIWebGpu.GetInstance();
            using var form = new SWF.Form { StartPosition = SWF.FormStartPosition.Manual, Location = new Point(9100, 700), ClientSize = new Size(300, 200), ShowInTaskbar = false };   // where no other test's form is
            var panel = new Recorder { Bounds = new Rectangle(10, 10, 200, 150) };
            form.Controls.Add(panel);
            form.Show();   // the release's move goes to the window under the cursor: there has to be one
            SWF.Application.DoEvents();
            panel.Log.Clear();

            Point at = panel.PointToScreen(new Point(30, 20));
            driver.InjectMouseDownAt(panel.Handle, at.X, at.Y);
            Assert.Equal(SWF.MouseButtons.Left, SWF.Control.MouseButtons);
            SWF.Application.DoEvents();
            driver.InjectMouseUpAt(panel.Handle, at.X, at.Y);
            Assert.Equal(SWF.MouseButtons.None, SWF.Control.MouseButtons);
            SWF.Application.DoEvents();

            Assert.Equal(new[] { "down 1 (30,20)", "move 1 (30,20)", "up 0 (30,20)", "move 0 (30,20)" }, panel.Log);
        }

        [Fact]
        public void ACaptureTakenWithNoButtonDown_PostsNoMove()
        {
            using var form = new SWF.Form { ClientSize = new Size(300, 200) };
            var panel = new Recorder { Bounds = new Rectangle(10, 10, 200, 150) };
            form.Controls.Add(panel);
            form.CreateControl();
            SWF.Application.DoEvents();
            panel.Log.Clear();

            panel.Capture = true;
            SWF.Application.DoEvents();
            Assert.Empty(panel.Log);
            panel.Capture = false;
        }
    }
}
