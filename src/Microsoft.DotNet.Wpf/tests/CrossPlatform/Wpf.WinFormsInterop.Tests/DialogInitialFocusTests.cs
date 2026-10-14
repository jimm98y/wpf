// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// A dialog shown with nothing selected gives the keyboard to its first control in tab order, as a
// login dialog's user-name box gets it on Windows: the caret is there before anything is clicked.
//

using System;
using System.Drawing;
using System.Threading;
using Xunit;
using SWF = System.Windows.Forms;

namespace Wpf.WinFormsInterop.Tests
{
    public class DialogInitialFocusTests
    {
        [Fact]
        public void ShowDialog_FocusesTheFirstControlInTabOrder()
        {
            Exception? failure = null;
            string result = "";
            var t = new Thread(() =>
            {
                try
                {
                    using var owner = new SWF.Form { StartPosition = SWF.FormStartPosition.Manual, Location = new Point(40, 40), ShowInTaskbar = false };
                    owner.Show();
                    using var dialog = new SWF.Form { StartPosition = SWF.FormStartPosition.Manual, Location = new Point(60, 60), ClientSize = new Size(300, 120), ShowInTaskbar = false };
                    var label = new SWF.Label { Text = "User", Bounds = new Rectangle(10, 12, 60, 16), TabIndex = 0 };
                    var user = new SWF.ComboBox { Bounds = new Rectangle(80, 10, 150, 21), TabIndex = 1 };
                    var ok = new SWF.Button { Text = "OK", Bounds = new Rectangle(80, 60, 75, 23), TabIndex = 2 };
                    dialog.Controls.AddRange(new SWF.Control[] { ok, user, label });
                    dialog.Shown += (s, e) =>
                    {
                        for (int i = 0; i < 10; i++) { SWF.Application.DoEvents(); Thread.Sleep(10); }
                        result = $"{dialog.ActiveControl?.GetType().Name}/{user.ContainsFocus}";
                        dialog.Close();
                    };
                    dialog.ShowDialog(owner);
                    owner.Close();
                }
                catch (Exception ex) { failure = ex; }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            t.Join();
            if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
            Assert.Equal("ComboBox/True", result);
        }
    }
}
