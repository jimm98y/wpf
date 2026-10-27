// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// A disabled window gets no mouse input on Windows, the hover that a button shows itself hot for among
// it: the pointer over a disabled button is over its parent, and the button is told nothing. The
// driver passed the disabled button's clicks on to its parent but tracked the hover on the button
// itself, so a disabled button in app's Find/Replace dialog lit up when the pointer crossed it.

using System;
using System.Drawing;
using System.Reflection;
using Xunit;
using SWF = System.Windows.Forms;

namespace Wpf.WinFormsInterop.Tests
{
    public class DisabledHoverTests
    {
        private static object Driver() => typeof(SWF.Control).Assembly.GetType("System.Windows.Forms.XplatUI")!
            .GetField("driver", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

        [Fact]
        public void ADisabledButton_IsNotHot_UnderThePointer()
        {
            using var form = new SWF.Form { StartPosition = SWF.FormStartPosition.Manual, Location = new Point(8000, 80), ClientSize = new Size(200, 120), ShowInTaskbar = false };
            var enabled = new SWF.Button { Text = "On", Bounds = new Rectangle(10, 10, 80, 30) };
            var disabled = new SWF.Button { Text = "Off", Bounds = new Rectangle(10, 60, 80, 30), Enabled = false };
            form.Controls.Add(enabled);
            form.Controls.Add(disabled);
            form.Show();
            SWF.Application.DoEvents();
            bool Over(SWF.Control c) => (bool)typeof(SWF.ButtonBase).GetProperty("MouseIsOver", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(c)!;

            Hover(form, enabled);
            Assert.True(Over(enabled), "an enabled button is hot under the pointer");
            Hover(form, disabled);
            Assert.False(Over(disabled), "a disabled button is not told the pointer is over it");
            Assert.False(Over(enabled), "the button the pointer left is not hot any more");
            form.Close();
        }

        private static void Hover(SWF.Form root, SWF.Control at)
        {
            object d = Driver();
            Point p = at.PointToScreen(new Point(at.Width / 2, at.Height / 2));
            d.GetType().GetMethod("InjectMouseMoveIn", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(d, new object[] { root.Handle, p.X, p.Y, false });
            SWF.Application.DoEvents();
        }
    }
}
