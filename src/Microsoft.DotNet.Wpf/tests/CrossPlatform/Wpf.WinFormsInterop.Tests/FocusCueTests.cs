// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Focus cues, and the Tab navigation that reveals them.
//
// Both halves of this were wrong at once, and each hid the other. The port drew a focus rectangle
// from the moment a window opened, where Windows keeps UISF_HIDEFOCUS set until the user navigates
// with the keyboard -- so a form the user had only clicked on wore a dotted rectangle Windows does
// not draw. And Tab could not leave a LinkLabel at all: LinkLabel.ProcessDialogKey swallowed the key
// whether or not the label had another link to move to, and LinkLabel.Select cleared the focused
// index BEFORE searching from it, so a label with one link re-selected that link for ever.
//
// Fixing only the first would have been worse than leaving it: the cue would have been hidden with
// nothing able to bring it back, and a keyboard user would have had no focus indication anywhere.
// That is why they are asserted together.
//
// Measured against a stock WinForms window driven the same way -- click the caption, press Tab --
// which reports exactly this: focus cues off with the LinkLabel active, then cues ON with the Button
// active. Ours now reports the same two lines.
//

using System;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using Xunit;

namespace Wpf.WinFormsInterop.Tests
{
    public class FocusCueTests
    {
        /// <summary>ShowFocusCues is protected, and it is what the themes actually consult -- reading
        /// the private field instead would pass while every control still answered from something
        /// else, which is the bug this guards.</summary>
        private static bool ShowFocusCues(Control c)
            => (bool)typeof(Control)
                .GetProperty("ShowFocusCues", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(c)!;

        private static bool ProcessDialogKey(Control c, Keys k)
            => (bool)typeof(Control)
                .GetMethod("ProcessDialogKey", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(c, new object[] { k })!;

        private static bool FocusNextLink(LinkLabel l, bool forward)
            => (bool)typeof(LinkLabel)
                .GetMethod("FocusNextLink", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(l, new object[] { forward })!;

        private static Form BuildForm(out LinkLabel link, out Button button)
        {
            var f = new Form { ClientSize = new Size(300, 120) };
            link = new LinkLabel { Text = "LinkLabel", Left = 10, Top = 10, Width = 120 };
            button = new Button { Text = "Button", Left = 10, Top = 40, Width = 100 };
            f.Controls.Add(link);
            f.Controls.Add(button);
            _ = f.Handle;                     // the cue messages need a window to be sent to
            return f;
        }

        [Fact]
        public void AWindowNobodyHasTabbedIn_HidesItsFocusCues()
        {
            using Form f = BuildForm(out LinkLabel link, out Button button);

            Assert.False(ShowFocusCues(f),
                         "a form shows no focus rectangle until the keyboard has been used in it");
            Assert.False(ShowFocusCues(link));
            Assert.False(ShowFocusCues(button));
        }

        [Fact]
        public void NavigatingWithTheKeyboard_RevealsThemEverywhere()
        {
            using Form f = BuildForm(out LinkLabel link, out Button button);
            Assert.False(ShowFocusCues(f));

            ProcessDialogKey(f, Keys.Tab);

            // The children matter as much as the form: they answer out of the FORM's flag, so a
            // reveal that reaches only the children leaves every one of them still hidden.
            Assert.True(ShowFocusCues(f), "Tab has to bring the focus rectangle out");
            Assert.True(ShowFocusCues(button), "and every control has to see it, not just the form");
            Assert.True(ShowFocusCues(link));
        }

        [Fact]
        public void ArrowNavigation_RevealsThemToo()
        {
            using Form f = BuildForm(out LinkLabel _, out Button __);
            Assert.False(ShowFocusCues(f));

            // Tab is covered by ProcessTabKey, which sets the flag itself and always did. The arrows
            // navigate through SelectNextControl instead and set nothing, so they move the focus
            // without revealing it unless the form asks for the cue explicitly.
            ProcessDialogKey(f, Keys.Right);

            Assert.True(ShowFocusCues(f), "an arrow key moves the focus, so it must show it too");
        }

        [Fact]
        public void TheCueMessage_UpdatesTheWindowItselfAndNotOnlyItsChildren()
        {
            using Form f = BuildForm(out LinkLabel _, out Button button);
            Assert.False(ShowFocusCues(f));

            // WM_CHANGEUISTATE straight at the form, which is what Windows' own DefWindowProc turns
            // into a WM_UPDATEUISTATE for the window AND for everything under it. Forwarding it to
            // the children alone leaves the form's flag false -- and since that flag is the one every
            // child answers from, nothing changes anywhere.
            const int WM_CHANGEUISTATE = 0x0127;
            const int UIS_CLEAR = 2, UISF_HIDEFOCUS = 0x1;
            var m = Message.Create(f.Handle, WM_CHANGEUISTATE,
                                   (IntPtr)((UISF_HIDEFOCUS << 16) | UIS_CLEAR), IntPtr.Zero);
            typeof(Control).GetMethod("WndProc", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(f, new object[] { m });

            Assert.True(ShowFocusCues(f), "the window has to update its own cue state");
            Assert.True(ShowFocusCues(button));
        }

        [Fact]
        public void ALinkLabelWithOneLink_HasNowhereToTabTo()
        {
            using Form f = BuildForm(out LinkLabel link, out Button _);

            // First call takes the focus to the only link; the second has to say NO, which is what
            // lets the container move the focus out of the control.
            Assert.True(FocusNextLink(link, forward: true));
            Assert.False(FocusNextLink(link, forward: true),
                         "a label with one link must give Tab up rather than re-select that link");
        }

        [Fact]
        public void ALinkLabelWithTwoLinks_WalksThemBeforeGivingTabUp()
        {
            using Form f = BuildForm(out LinkLabel link, out Button _);
            link.Text = "one and two";
            link.Links.Clear();
            link.Links.Add(0, 3);
            link.Links.Add(8, 3);

            Assert.True(FocusNextLink(link, forward: true), "to the first link");
            Assert.True(FocusNextLink(link, forward: true), "to the second link");
            Assert.False(FocusNextLink(link, forward: true), "and then out of the control");

            // Backwards walks them the other way, which the old code could not do either: it reset
            // the index to -1 and then read it back as "start from the end" every time.
            Assert.True(FocusNextLink(link, forward: false), "back to the first link");
            Assert.False(FocusNextLink(link, forward: false), "and then out of the control");
        }
    }
}
