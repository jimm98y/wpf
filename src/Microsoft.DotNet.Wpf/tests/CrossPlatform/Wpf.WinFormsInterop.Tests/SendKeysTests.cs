// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// SendKeys types into the focused window as the keyboard would: characters (capitals with Shift), the
// special keys in braces, and Shift/Ctrl held for what follows. Nothing was sent at all; and a character
// queued as a key of its own code was the wrong key ('a' is VK_NUMPAD1).
//

using System;
using System.Drawing;
using Xunit;
using SWF = System.Windows.Forms;

namespace Wpf.WinFormsInterop.Tests
{
    public class SendKeysTests
    {
        [Fact]
        public void SendKeys_TypesIntoTheFocusedTextBox()
        {
            SWF.XplatUIWebGpu driver = SWF.XplatUIWebGpu.GetInstance();
            using var form = new SWF.Form { StartPosition = SWF.FormStartPosition.Manual, Location = new Point(9500, 4500), ClientSize = new Size(300, 100), ShowInTaskbar = false };
            var box = new SWF.TextBox { Bounds = new Rectangle(10, 10, 250, 24) };
            form.Controls.Add(box);
            form.Show();
            driver.HostActivate(form.Handle.ToInt64(), true);
            box.Focus();
            SWF.Application.DoEvents();

            SWF.SendKeys.Send("ab+cD1 x{BACKSPACE}");
            SWF.Application.DoEvents();
            Assert.Equal("abCD1 ", box.Text);

            SWF.SendKeys.Send("^a");            // Ctrl+A selects everything
            SWF.SendKeys.Send("z");
            Assert.Equal("z", box.Text);
            Assert.Equal(SWF.Keys.None, SWF.Control.ModifierKeys);   // nothing left held
        }
    }
}
