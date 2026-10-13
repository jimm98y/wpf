// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Input to disabled windows. While a dialog runs modally Windows disables every other window of the
// thread, and a disabled window takes no input: the buttons behind a login dialog could still be
// clicked, and so could any button whose Enabled was false. Clicks are delivered through the
// driver's own input path (what a host does with an OS click), not through the screen.
//

using System;
using System.Drawing;
using System.Reflection;
using System.Threading;
using Xunit;
using SWF = System.Windows.Forms;

namespace Wpf.WinFormsInterop.Tests
{
    public class ModalInputTests
    {
        private static object Driver() => typeof(SWF.Control).Assembly.GetType("System.Windows.Forms.XplatUI")!
            .GetField("driver", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

        /// <summary>A click at a control's centre, in the driver's screen space, as a host delivers one.</summary>
        private static void Click(SWF.Form root, SWF.Control at)
        {
            object d = Driver();
            Point p = at.PointToScreen(new Point(at.Width / 2, at.Height / 2));
            var t = d.GetType();
            t.GetMethod("InjectMouseDownIn", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(d, new object[] { root.Handle, p.X, p.Y });
            t.GetMethod("InjectMouseUpIn", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(d, new object[] { root.Handle, p.X, p.Y });
            SWF.Application.DoEvents();
        }

        private static void OnSta(Action a)
        {
            Exception? failure = null;
            var t = new Thread(() => { try { a(); } catch (Exception ex) { failure = ex; } });
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            t.Join();
            if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }

        [Fact]
        public void DisabledButton_NeverClicks()
        {
            OnSta(() =>
            {
                using var form = new SWF.Form { StartPosition = SWF.FormStartPosition.Manual, Location = new Point(40, 40), ClientSize = new Size(200, 100), ShowInTaskbar = false };
                var on = new SWF.Button { Bounds = new Rectangle(10, 10, 80, 30), Text = "On" };
                var off = new SWF.Button { Bounds = new Rectangle(100, 10, 80, 30), Text = "Off", Enabled = false };
                int onClicks = 0, offClicks = 0;
                on.Click += (s, e) => onClicks++;
                off.Click += (s, e) => offClicks++;
                form.Controls.Add(on);
                form.Controls.Add(off);
                form.Show();
                SWF.Application.DoEvents();
                Click(form, on);
                Click(form, off);
                form.Close();
                Assert.Equal(1, onClicks);      // the path delivers clicks at all
                Assert.Equal(0, offClicks);
            });
        }

        [Fact]
        public void ClickOnTheOwner_DuringShowDialog_IsIgnored()
        {
            OnSta(() =>
            {
                using var owner = new SWF.Form { StartPosition = SWF.FormStartPosition.Manual, Location = new Point(40, 40), ClientSize = new Size(300, 200), ShowInTaskbar = false };
                var behind = new SWF.Button { Bounds = new Rectangle(10, 150, 80, 30), Text = "Behind" };
                int clicks = 0;
                behind.Click += (s, e) => clicks++;
                owner.Controls.Add(behind);
                owner.Show();
                SWF.Application.DoEvents();
                Click(owner, behind);
                Assert.Equal(1, clicks);

                using var dialog = new SWF.Form { StartPosition = SWF.FormStartPosition.Manual, Location = new Point(60, 60), ClientSize = new Size(120, 60), ShowInTaskbar = false };
                bool ownerEnabledDuring = true;
                dialog.Shown += (s, e) =>
                {
                    ownerEnabledDuring = IsEnabled(owner.Handle);
                    Click(owner, behind);
                    dialog.Close();
                };
                dialog.ShowDialog(owner);
                Assert.False(ownerEnabledDuring);   // the modal loop disabled the owner
                Assert.Equal(1, clicks);            // and its button took no click
                Assert.True(IsEnabled(owner.Handle));   // enabled again afterwards
                owner.Close();
            });
        }

        private static bool IsEnabled(IntPtr h)
            => (bool)typeof(SWF.Control).Assembly.GetType("System.Windows.Forms.XplatUI")!
                .GetMethod("IsEnabled", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)!.Invoke(null, new object[] { h })!;
    }
}
