// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// What a form auto-scaled for a high DPI got wrong in its controls, as .NET's WinForms has them:
//
//   * A list box the control draws itself has the font's row height. A localizable form's resources
//     carry ItemHeight = 13; taken as the row height, every row of a 200% list box was 13 pixels and
//     its text was cut in half.
//   * An up-down's buttons are 16 pixels at the control's DPI, whatever the form's scaling: scaled
//     again with the form's children they took most of the control, and the value had no room.
//   * An up-down passes its focus on to its text box. Clearing the active control when that took the
//     focus refocused the up-down, which focused the text box again: an up-down that was the first
//     control of a form overflowed the stack as the form came up.
//

using System;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;
using Xunit;

namespace Wpf.WinFormsInterop.Tests
{
    [Collection(ProcessDpiStateCollection.Name)]
    public class SystemAwareLayoutTests
    {
        [Fact]
        public void AListBoxTheControlDrawsHasTheFontsRowHeightWhateverItemHeightSays()
        {
            RunOnSta(() =>
            {
                using var lb = new ListBox();
                int fontRow = lb.ItemHeight;
                lb.ItemHeight = 13;                       // what a localizable form's resources set
                Assert.Equal(fontRow, lb.ItemHeight);
                lb.Items.Add("Local Alarm Server");
                Assert.Equal(fontRow, lb.GetItemRectangle(0).Height);

                lb.Font = new Font(lb.Font.FontFamily, lb.Font.Size * 2);
                Assert.True(lb.ItemHeight > fontRow, "a bigger font, taller rows");

                // owner-drawn rows are as tall as they are told
                using var owner = new ListBox { DrawMode = DrawMode.OwnerDrawFixed };
                owner.ItemHeight = 13;
                Assert.Equal(13, owner.ItemHeight);
            });
        }

        [Fact]
        public void AnUpDownsButtonsKeepTheirWidthWhenTheFormScales()
        {
            RunOnSta(() =>
            {
                using var form = new Form();
                var nud = new NumericUpDown { Location = new Point(10, 10), Size = new Size(80, 20) };
                form.Controls.Add(nud);
                int buttons = nud.Controls[1].Width;
                form.Scale(new SizeF(2f, 2f));
                nud.PerformLayout();
                Assert.Equal(160, nud.Width);
                Control spinner = nud.Controls[1], edit = nud.Controls[0];
                Assert.Equal(buttons, spinner.Width);
                Assert.True(edit.Width > nud.Width / 2, $"the value has room: edit {edit.Width} of {nud.Width}");
            });
        }

        [Fact]
        public void AnUpDownThatTakesTheFocusPassesItToItsTextBoxOnce()
        {
            RunOnSta(() =>
            {
                using var form = new Form();
                var nud = new NumericUpDown { Location = new Point(10, 10) };
                form.Controls.Add(nud);
                form.CreateControl();
                nud.CreateControl();
                Message m = Message.Create(nud.Handle, 0x0007 /* WM_SETFOCUS */, IntPtr.Zero, IntPtr.Zero);
                InvokeWndProc(nud, ref m);
                Assert.Same(nud.Controls[0], nud.ActiveControl);
                m = Message.Create(nud.Handle, 0x0008 /* WM_KILLFOCUS */, IntPtr.Zero, IntPtr.Zero);
                InvokeWndProc(nud, ref m);
                Assert.Same(nud.Controls[0], nud.ActiveControl);   // .NET keeps it
            });
        }

        private static void InvokeWndProc(Control control, ref Message m)
        {
            var method = typeof(UpDownBase).GetMethod("WndProc", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            object[] args = { m };
            method.Invoke(control, args);
            m = (Message)args[0];
        }

        private static void RunOnSta(Action body)
        {
            Exception? thrown = null;
            var t = new Thread(() =>
            {
                try { body(); }
                catch (Exception e) { thrown = e; }
            }, 16 * 1024 * 1024);
            if (OperatingSystem.IsWindows()) t.SetApartmentState(ApartmentState.STA);
            t.Start();
            t.Join();
            if (thrown != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(thrown).Throw();
        }
    }
}
