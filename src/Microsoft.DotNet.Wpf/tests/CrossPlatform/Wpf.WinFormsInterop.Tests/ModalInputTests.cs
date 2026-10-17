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

        /// <summary>A dialog opened from a button's Click runs while that button still holds the
        /// mouse capture (WinForms releases it after OnClick), and its owner is then disabled.
        /// Windows releases a disabled window's capture; ours kept it, so every mouse message of the
        /// dialog went to the disabled button: nothing in the dialog could be hovered or clicked,
        /// while the keyboard still worked (GraphWorX's Security Login, opened from the status bar).</summary>
        [Fact]
        public void ADialogOpenedFromAClick_TakesTheMouse()
        {
            OnSta(() =>
            {
                using var owner = new SWF.Form { StartPosition = SWF.FormStartPosition.Manual, Location = new Point(40, 40), ClientSize = new Size(300, 200), ShowInTaskbar = false };
                // A plain control (DotNetBar's status bar is one): its Click comes before it lets go
                // of the capture, unlike a Button's.
                var opener = new SWF.Panel { Bounds = new Rectangle(10, 10, 80, 30), BackColor = Color.Gray };
                owner.Controls.Add(opener);
                using var dialog = new SWF.Form { StartPosition = SWF.FormStartPosition.Manual, Location = new Point(80, 80), ClientSize = new Size(160, 80), ShowInTaskbar = false };
                var inner = new SWF.Button { Bounds = new Rectangle(10, 10, 80, 30), Text = "Keypad" };
                dialog.Controls.Add(inner);
                int innerClicks = 0, entered = 0;
                IntPtr grabAtShown = IntPtr.Zero;
                inner.Click += (s, e) => innerClicks++;
                inner.MouseEnter += (s, e) => entered++;
                dialog.Shown += (s, e) =>
                {
                    object d = Driver();
                    grabAtShown = (IntPtr)d.GetType().GetProperty("GrabHandle", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(d)!;
                    Point p = inner.PointToScreen(new Point(inner.Width / 2, inner.Height / 2));
                    d.GetType().GetMethod("InjectMouseMoveIn", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(d, new object[] { dialog.Handle, p.X, p.Y, false });
                    SWF.Application.DoEvents();
                    Click(dialog, inner);
                    dialog.Close();
                };
                opener.Click += (s, e) => dialog.ShowDialog(owner);
                owner.Show();
                SWF.Application.DoEvents();
                IntPtr openerHandle = opener.Handle;
                Click(owner, opener);
                owner.Close();
                Assert.True(entered > 0, $"the dialog's button was never hovered (capture at Shown: 0x{grabAtShown:x}, opener 0x{openerHandle:x})");
                Assert.Equal(1, innerClicks);
            });
        }

        /// <summary>A modal opened from a modal (GraphWorX: Security Login, then Change Password from it):
        /// the inner dialog's Cancel must take its click, and once it closes the outer dialog must take
        /// clicks again, with every window's enabled state -- the driver's and the real host windows' --
        /// what Windows would have at each step.</summary>
        [Fact]
        public void AModalOpenedFromAModal_TakesItsClicks_AndTheOuterOneDoesAfterwards()
        {
            OnSta(() =>
            {
                using var owner = new SWF.Form { Text = "NestedOwner", StartPosition = SWF.FormStartPosition.Manual, Location = new Point(40, 40), ClientSize = new Size(300, 200), ShowInTaskbar = false };
                var opener = new SWF.Panel { Bounds = new Rectangle(10, 10, 80, 30), BackColor = Color.Gray };
                owner.Controls.Add(opener);
                using var login = new SWF.Form { Text = "NestedLogin", StartPosition = SWF.FormStartPosition.Manual, Location = new Point(60, 60), ClientSize = new Size(220, 120), ShowInTaskbar = false };
                var change = new SWF.Button { Bounds = new Rectangle(10, 10, 120, 30), Text = "Change Password" };
                var loginOk = new SWF.Button { Bounds = new Rectangle(10, 60, 80, 30), Text = "OK", DialogResult = SWF.DialogResult.OK };
                login.Controls.Add(change); login.Controls.Add(loginOk);
                using var changeDlg = new SWF.Form { Text = "NestedChange", StartPosition = SWF.FormStartPosition.Manual, Location = new Point(80, 80), ClientSize = new Size(200, 100), ShowInTaskbar = false };
                var cancel = new SWF.Button { Bounds = new Rectangle(10, 50, 80, 30), Text = "Cancel", DialogResult = SWF.DialogResult.Cancel };
                changeDlg.Controls.Add(cancel);
                changeDlg.CancelButton = cancel;

                var log = new System.Collections.Generic.List<string>();
                var mismatches = new System.Collections.Generic.List<string>();
                void Same(string when, SWF.Form f) { if (IsEnabled(f.Handle) != RealEnabled(f)) mismatches.Add($"{when}: {f.Text} driver {IsEnabled(f.Handle)} real {RealEnabled(f)}"); }
                int cancelClicks = 0, okClicks = 0;
                cancel.Click += (s, e) => cancelClicks++;
                loginOk.Click += (s, e) => okClicks++;
                SWF.DialogResult inner = SWF.DialogResult.None, outer = SWF.DialogResult.None;

                changeDlg.Shown += (s, e) =>
                {
                    log.Add($"change shown: owner {IsEnabled(owner.Handle)}/{RealEnabled(owner)} login {IsEnabled(login.Handle)}/{RealEnabled(login)} change {IsEnabled(changeDlg.Handle)}/{RealEnabled(changeDlg)}");
                    Same("change shown", owner); Same("change shown", login); Same("change shown", changeDlg);
                    Hover(changeDlg, cancel);
                    Click(changeDlg, cancel);
                };
                change.Click += (s, e) =>
                {
                    inner = changeDlg.ShowDialog(login);
                    log.Add($"change closed ({inner}): owner {IsEnabled(owner.Handle)}/{RealEnabled(owner)} login {IsEnabled(login.Handle)}/{RealEnabled(login)}");
                    // Back in the login dialog's loop, after the inner one: its own buttons work.
                    login.BeginInvoke(new Action(() =>
                    {
                        Hover(login, loginOk);
                        Click(login, loginOk);
                    }));
                };
                login.Shown += (s, e) =>
                {
                    log.Add($"login shown: owner {IsEnabled(owner.Handle)}/{RealEnabled(owner)} login {IsEnabled(login.Handle)}/{RealEnabled(login)}");
                    Same("login shown", owner); Same("login shown", login);
                    Hover(login, change);
                    Click(login, change);
                };
                // GraphWorX makes the inner dialog (its handle) before the outer one runs: the outer loop must
                // not leave a hidden window it disabled disabled when that window then runs modally itself.
                _ = changeDlg.Handle;
                opener.Click += (s, e) => outer = login.ShowDialog(owner);
                owner.Show();
                SWF.Application.DoEvents();
                Click(owner, opener);
                bool ownerAfter = IsEnabled(owner.Handle), ownerRealAfter = RealEnabled(owner);
                owner.Close();
                string trace = string.Join(" | ", log);
                Assert.True(cancelClicks == 1 && inner == SWF.DialogResult.Cancel, $"the inner dialog's Cancel took its click: {trace}");
                Assert.True(okClicks == 1 && outer == SWF.DialogResult.OK, $"the outer dialog took its click after the inner one closed: {trace}");
                Assert.True(mismatches.Count == 0, "a real window's enabled state is not the driver's: " + string.Join("; ", mismatches));
                Assert.True(ownerAfter && ownerRealAfter, $"the owner is enabled again: {trace}");
            });
        }

        private static void Hover(SWF.Form root, SWF.Control at)
        {
            object d = Driver();
            Point p = at.PointToScreen(new Point(at.Width / 2, at.Height / 2));
            d.GetType().GetMethod("InjectMouseMoveIn", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(d, new object[] { root.Handle, p.X, p.Y, false });
            SWF.Application.DoEvents();
        }

        /// <summary>The form's real host window's OS enabled state (true when it has no host).</summary>
        private static bool RealEnabled(SWF.Form form)
        {
            object? host = typeof(SWF.Form).Assembly.GetType("System.Windows.Forms.PresentationHost")!
                .GetMethod("HostOf", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!.Invoke(null, new object[] { form });
            if (host == null) return true;
            var h = (IntPtr)host.GetType().GetField("_hwnd", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;
            return h == IntPtr.Zero || IsWindowEnabledReal(h);
        }

        [System.Runtime.InteropServices.DllImport("user32", EntryPoint = "IsWindowEnabled")] private static extern bool IsWindowEnabledReal(IntPtr h);

        private static bool IsEnabled(IntPtr h)
            => (bool)typeof(SWF.Control).Assembly.GetType("System.Windows.Forms.XplatUI")!
                .GetMethod("IsEnabled", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)!.Invoke(null, new object[] { h })!;
    }
}
