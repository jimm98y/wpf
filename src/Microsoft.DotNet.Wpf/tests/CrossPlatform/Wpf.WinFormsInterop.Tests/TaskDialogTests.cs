// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// TaskDialog (src/WinFormsInterop/modern/TaskDialog*.cs): .NET's task dialog, shown as a form of this WinForms on every
// head. Each test drives the dialog from its page's Created event, as an application's code would from a handler, with
// the window moved off the screen.
//

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using Xunit;
using SWF = System.Windows.Forms;

namespace Wpf.WinFormsInterop.Tests
{
    public class TaskDialogTests
    {
        private static T OnUiThread<T>(Func<T> body)
        {
            T result = default!;
            Exception? failure = null;
            var t = new Thread(() =>
            {
                try { result = body(); }
                catch (Exception ex) { failure = ex; }
            });
            t.TrySetApartmentState(ApartmentState.STA);
            t.Start();
            Assert.True(t.Join(TimeSpan.FromSeconds(30)), "the task dialog did not close");
            if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
            return result;
        }

        // the page's Created: off the screen, then what the test does there
        private static void WhenShown(SWF.TaskDialogPage page, Action<SWF.TaskDialog> act)
        {
            EventHandler? created = null;
            created = (s, e) =>
            {
                page.Created -= created;
                SWF.TaskDialog dialog = page.BoundDialog!;
                var form = (SWF.Form)SWF.Control.FromHandle(dialog.Handle)!;
                form.Location = new Point(8000, 80);
                form.BeginInvoke(new Action(() => act(dialog)));
            };
            page.Created += created;
        }

        [Fact]
        public void AStandardButtonsClick_ClosesWithIt_AndEqualsTheStandardButton()
        {
            var page = new SWF.TaskDialogPage
            {
                Caption = "TaskDialogTest", Heading = "Save changes?", Text = "Your document has changed.",
                Icon = SWF.TaskDialogIcon.Warning, Buttons = { SWF.TaskDialogButton.Yes, SWF.TaskDialogButton.No },
            };
            int created = 0, destroyed = 0;
            page.Created += (s, e) => created++;
            page.Destroyed += (s, e) => destroyed++;
            WhenShown(page, d => page.Buttons[1].PerformClick());
            SWF.TaskDialogButton result = OnUiThread(() => SWF.TaskDialog.ShowDialog(page));
            Assert.True(result == SWF.TaskDialogButton.No);
            Assert.Same(page.Buttons[1], result);
            Assert.Equal(1, created);
            Assert.Equal(1, destroyed);
            Assert.Null(page.BoundDialog);
        }

        [Fact]
        public void APageWithoutButtons_HasOK_AndCloseIsCancel()
        {
            var page = new SWF.TaskDialogPage { Text = "Done." };
            WhenShown(page, d => d.Close());
            SWF.TaskDialogButton result = OnUiThread(() => SWF.TaskDialog.ShowDialog(page));
            Assert.True(result == SWF.TaskDialogButton.Cancel);
        }

        [Fact]
        public void AButtonThatDoesNotClose_RaisesClick_AndHelpRaisesHelpRequest()
        {
            var keepOpen = new SWF.TaskDialogButton("Apply", allowCloseDialog: false);
            var page = new SWF.TaskDialogPage { Buttons = { keepOpen, SWF.TaskDialogButton.Help, SWF.TaskDialogButton.OK } };
            int clicks = 0, help = 0;
            keepOpen.Click += (s, e) => clicks++;
            page.HelpRequest += (s, e) => help++;
            WhenShown(page, d =>
            {
                keepOpen.PerformClick();
                page.Buttons[1].PerformClick();
                Assert.NotNull(page.BoundDialog);
                page.Buttons[2].PerformClick();
            });
            SWF.TaskDialogButton result = OnUiThread(() => SWF.TaskDialog.ShowDialog(page));
            Assert.True(result == SWF.TaskDialogButton.OK);
            Assert.Equal(1, clicks);
            Assert.Equal(1, help);
        }

        [Fact]
        public void RadioButtons_AreOneChecked_TheVerificationAndExpanderFollow_AndTheBoundPageRefusesNewParts()
        {
            var page = new SWF.TaskDialogPage
            {
                Text = "Choose", RadioButtons = { "First", "Second" },
                Verification = new SWF.TaskDialogVerificationCheckBox("Don't ask again"),
                Expander = new SWF.TaskDialogExpander("The details."),
                ProgressBar = new SWF.TaskDialogProgressBar { Value = 10 },
                Buttons = { SWF.TaskDialogButton.OK },
            };
            page.RadioButtons[0].Checked = true;
            var changes = new List<string>();
            page.RadioButtons[1].CheckedChanged += (s, e) => changes.Add("second " + page.RadioButtons[1].Checked);
            page.Expander!.ExpandedChanged += (s, e) => changes.Add("expanded " + page.Expander.Expanded);
            Exception? denied = null;
            WhenShown(page, d =>
            {
                page.RadioButtons[1].Checked = true;
                page.Verification!.Checked = true;
                page.ProgressBar!.Value = 60;
                page.Heading = "Changed while shown";
                try { page.Buttons.Add("More"); } catch (InvalidOperationException ex) { denied = ex; }
                page.Buttons[0].PerformClick();
            });
            OnUiThread(() => SWF.TaskDialog.ShowDialog(page));
            Assert.False(page.RadioButtons[0].Checked);
            Assert.True(page.RadioButtons[1].Checked);
            Assert.True(page.Verification!.Checked);
            Assert.Equal(60, page.ProgressBar!.Value);
            Assert.Equal(new[] { "second True" }, changes);
            Assert.NotNull(denied);
        }

        [Fact]
        public void Navigate_ShowsTheOtherPage_InTheSameDialog()
        {
            var second = new SWF.TaskDialogPage { Heading = "Second", Buttons = { SWF.TaskDialogButton.Close } };
            var first = new SWF.TaskDialogPage { Heading = "First", Buttons = { new SWF.TaskDialogButton("Next", allowCloseDialog: false) } };
            first.Buttons[0].Click += (s, e) => first.Navigate(second);
            int firstDestroyed = 0, secondCreated = 0;
            first.Destroyed += (s, e) => firstDestroyed++;
            second.Created += (s, e) =>
            {
                secondCreated++;
                second.BoundDialog!.Close();
            };
            WhenShown(first, d => first.Buttons[0].PerformClick());
            SWF.TaskDialogButton result = OnUiThread(() => SWF.TaskDialog.ShowDialog(first));
            Assert.Equal(1, firstDestroyed);
            Assert.Equal(1, secondCreated);
            Assert.True(result == SWF.TaskDialogButton.Cancel);
        }

        [Fact]
        public void StandardButtons_AreEqualByResult_AndACollectionTakesEachOnce()
        {
            Assert.True(SWF.TaskDialogButton.OK == SWF.TaskDialogButton.OK);
            Assert.False(SWF.TaskDialogButton.OK == SWF.TaskDialogButton.Cancel);
            var custom = new SWF.TaskDialogButton("A");
            Assert.False(custom == new SWF.TaskDialogButton("A"));
            var buttons = new SWF.TaskDialogButtonCollection { SWF.TaskDialogButton.OK };
            Assert.Throws<InvalidOperationException>(() => buttons.Add(SWF.TaskDialogButton.OK));
            Assert.Throws<InvalidOperationException>(() => SWF.TaskDialogButton.Yes.Text = "Sure");
        }
    }
}
