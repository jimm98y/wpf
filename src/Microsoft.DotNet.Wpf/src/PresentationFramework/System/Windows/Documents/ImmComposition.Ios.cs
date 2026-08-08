// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// ImmComposition's iOS half: text from UIKeyInput.
//
// The simplest of the backends, because iOS keeps the composition to itself. A Japanese keyboard on
// iOS shows its reading and candidate list INSIDE the keyboard rather than inline in the document,
// and hands the app the finished string through -insertText:. So there is no preedit to draw and no
// composition to adorn: text arrives already committed, and the only other thing the keyboard sends
// is a backspace.
//
// That is a real difference in fidelity from the other platforms, not a gap in this file. Inline
// composition would mean adopting the full UITextInput protocol; see the header of UIKitTextInput.cs
// for what that costs.
//

using MS.Internal.Interop;
using System.Windows.Interop;

namespace System.Windows.Documents
{
    internal partial class ImmComposition
    {
        /// <summary>True when this instance is the focused editor and the iOS keyboard is up.</summary>
        private bool IsIosTextInputActive =>
            (OperatingSystem.IsIOS() || OperatingSystem.IsMacCatalyst())
            && s_iosFocused == this && UIKitTextInput.IsEnabled;

        private static ImmComposition s_iosFocused;
        private static bool s_iosHooked;

        /// <summary>Raises the keyboard for this editor. Called from OnGotFocus.</summary>
        private void EnableIosTextInput()
        {
            if (!OperatingSystem.IsIOS() && !OperatingSystem.IsMacCatalyst()) return;
            if (!UIKitTextInput.IsAvailable) return;

            if (!s_iosHooked)
            {
                s_iosHooked = true;
                UIKitTextInput.ImeUpdate += OnIosImeUpdate;
            }

            s_iosFocused = this;

            bool multiline = _editor?.AcceptsRichContent == true || IsIosMultilineTextBox();
            bool password = _editor?.UiScope is Controls.PasswordBox;

            try
            {
                UIKitTextInput.Enable(((IWin32Window)_source).Handle, multiline, password);
            }
            catch (InvalidOperationException) { }   // source torn down mid-focus
        }

        /// <summary>Dismisses the keyboard. Called from OnLostFocus.</summary>
        private void DisableIosTextInput()
        {
            if (!OperatingSystem.IsIOS() && !OperatingSystem.IsMacCatalyst()) return;
            if (s_iosFocused != this) return;

            s_iosFocused = null;
            UIKitTextInput.Disable();
        }

        private bool IsIosMultilineTextBox()
            => _editor?.UiScope is Controls.TextBox box && (box.AcceptsReturn || box.TextWrapping != TextWrapping.NoWrap);

        /// <summary>
        /// Text (or a backspace) from the keyboard. Delivered on the UI thread: UIKit calls
        /// -insertText: on the main thread, which is where the dispatcher runs.
        /// </summary>
        private static void OnIosImeUpdate(UIKitImeUpdate update)
        {
            s_iosFocused?.ApplyIosImeUpdate(update);
        }

        private void ApplyIosImeUpdate(UIKitImeUpdate update)
        {
            if (_editor == null || !IsInKeyboardFocus || IsReadOnly) return;

            if (update.DeleteBackward)
            {
                // With no key-event path on iOS, the delete key IS this callback; a selection is
                // replaced by the delete rather than one more character being taken from behind it.
                ITextRange selection = _editor.Selection;
                if (selection != null && !selection.IsEmpty)
                {
                    selection.Text = string.Empty;
                }
                else
                {
                    DeleteAroundCaret(1, 0);
                }
                return;
            }

            if (string.IsNullOrEmpty(update.CommitText)) return;

            // Straight to a commit: no composition ever starts, so there are no per-character
            // attributes and the caret follows the inserted text.
            char[] resultChars = update.CommitText.ToCharArray();
            UpdateCompositionString(resultChars, null, resultChars.Length, 0, null);
        }
    }
}
