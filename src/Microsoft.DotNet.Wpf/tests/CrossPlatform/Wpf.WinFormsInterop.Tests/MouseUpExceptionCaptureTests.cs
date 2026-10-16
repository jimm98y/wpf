// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// A press captures the mouse for the control under it, and its release lets go -- even when the
// control's Click or MouseUp handler throws. The exception goes to Application.ThreadException and an
// app may ignore it and carry on; a control left holding the capture took every later click in its
// window. app's column configurator threw from a TreeView mouse-up, and after Ignore nothing in
// the dialog could be clicked.
//

using System;
using System.Drawing;
using System.Threading;
using Xunit;
using SWF = System.Windows.Forms;

namespace Wpf.WinFormsInterop.Tests
{
    public class MouseUpExceptionCaptureTests
    {
        [Theory]
        [InlineData("MouseUp")]
        [InlineData("Click")]
        public void AThrowingMouseUpHandler_DoesNotKeepTheMouse(string handler)
        {
            SWF.XplatUIWebGpu driver = SWF.XplatUIWebGpu.GetInstance();
            int reported = 0, clicked = 0;
            ThreadExceptionEventHandler ignore = (s, e) => reported++;
            SWF.Application.ThreadException += ignore;
            try
            {
                using var form = new SWF.Form { ClientSize = new Size(300, 100) };
                var thrower = new SWF.Panel { Left = 10, Top = 10, Width = 100, Height = 50 };
                if (handler == "MouseUp") thrower.MouseUp += (s, e) => throw new InvalidCastException("from MouseUp");
                else thrower.Click += (s, e) => throw new InvalidCastException("from Click");
                var other = new SWF.Panel { Left = 150, Top = 10, Width = 100, Height = 50 };
                other.MouseUp += (s, e) => clicked++;
                form.Controls.Add(thrower);
                form.Controls.Add(other);
                form.CreateControl();
                IntPtr a = thrower.Handle, b = other.Handle;

                driver.InjectMouseDownAt(a, 20, 20);
                Assert.Equal(a, driver.GrabHandle);           // the press captured the mouse
                driver.InjectMouseUpAt(a, 20, 20);
                Assert.Equal(1, reported);                     // the handler threw, and it was reported
                Assert.Equal(IntPtr.Zero, driver.GrabHandle);  // ...and the release still let go

                driver.InjectMouseDownAt(b, 20, 20);
                Assert.Equal(b, driver.GrabHandle);           // the next press is the other control's
                driver.InjectMouseUpAt(b, 20, 20);
                Assert.Equal(1, clicked);
            }
            finally
            {
                SWF.Application.ThreadException -= ignore;
            }
        }
    }
}
