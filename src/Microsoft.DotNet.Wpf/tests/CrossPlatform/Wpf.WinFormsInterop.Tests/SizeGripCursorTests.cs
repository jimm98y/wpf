// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// A sizable form's size grip is a corner of the form: only there does the pointer become the diagonal
// arrow (Windows' HTBOTTOMRIGHT). The grip set the WHOLE form's Cursor to it whenever the form's
// Enabled changed -- and a modal dialog disables its owner and enables it again when it closes, so a
// configuration dialog showed the diagonal arrow over its entire surface after its data browser closed.
//

using System;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;
using Xunit;

namespace Wpf.WinFormsInterop.Tests
{
    public class SizeGripCursorTests
    {
        [Fact]
        public void AFormWithASizeGripKeepsItsCursorThroughBeingDisabled()
        {
            RunOnSta(() =>
            {
                using var form = new Form { FormBorderStyle = FormBorderStyle.Sizable, SizeGripStyle = SizeGripStyle.Show, ClientSize = new Size(300, 200) };
                form.CreateControl();
                Assert.NotNull(typeof(Form).GetField("size_grip", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(form));
                Assert.Same(Cursors.Default, form.Cursor);
                form.Enabled = false;
                form.Enabled = true;
                Assert.Same(Cursors.Default, form.Cursor);

                // and the application's own cursor on the form stays the application's
                form.Cursor = Cursors.Hand;
                form.Enabled = false;
                form.Enabled = true;
                Assert.Same(Cursors.Hand, form.Cursor);
            });
        }

        private static void RunOnSta(Action body)
        {
            Exception? thrown = null;
            var t = new Thread(() => { try { body(); } catch (Exception e) { thrown = e; } });
            if (OperatingSystem.IsWindows()) t.SetApartmentState(ApartmentState.STA);
            t.Start();
            t.Join();
            if (thrown != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(thrown).Throw();
        }
    }
}
