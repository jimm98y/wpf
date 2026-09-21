// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// ImmComposition's Android half: driving a composition from InputConnection.
//
// Of the four non-Windows backends this is the closest to IMM32, because Android's model is the same
// one: setComposingText carries the reading being converted and commitText the finished text, so the
// inline composition and its underline work here exactly as they do on Windows. (The browser, by
// contrast, cannot say which clause is being converted, and iOS shows the reading inside the
// keyboard rather than in the document.)
//
// The android.* side -- the InputConnection, the soft keyboard, the caret rectangle -- lives in
// AndroidHost.cs, the payload each app head compiles in, because WindowsBase cannot reference the
// Mono.Android bindings. This file only sees AndroidImeUpdate.
//

using MS.Internal.Interop;
using System.Windows.Interop;

namespace System.Windows.Documents
{
    internal partial class ImmComposition
    {
        /// <summary>True when this instance is the focused editor and the Android IME is live.</summary>
        private bool IsAndroidTextInputActive =>
            OperatingSystem.IsAndroid() && s_androidFocused == this && AndroidTextInput.IsEnabled;

        // One input method per app, routed to whichever editor holds focus.
        private static ImmComposition s_androidFocused;
        private static bool s_androidHooked;

        /// <summary>Raises the soft keyboard for this editor. Called from OnGotFocus.</summary>
        private void EnableAndroidTextInput()
        {
            if (!OperatingSystem.IsAndroid()) return;
            if (!AndroidTextInput.IsAvailable) return;

            if (!s_androidHooked)
            {
                s_androidHooked = true;
                AndroidTextInput.ImeUpdate += OnAndroidImeUpdate;
            }

            s_androidFocused = this;

            bool multiline = _editor?.AcceptsRichContent == true || IsAndroidMultilineTextBox();
            bool password = _editor?.UiScope is Controls.PasswordBox;

            try
            {
                AndroidTextInput.Enable(((IWin32Window)_source).Handle, multiline, password);
            }
            catch (InvalidOperationException) { return; }   // source torn down mid-focus

            UpdateNearCaretCompositionWindow();
        }

        /// <summary>Dismisses the soft keyboard. Called from OnLostFocus.</summary>
        private void DisableAndroidTextInput()
        {
            if (!OperatingSystem.IsAndroid()) return;
            if (s_androidFocused != this) return;

            s_androidFocused = null;
            AndroidTextInput.Disable();
        }

        private bool IsAndroidMultilineTextBox()
            => _editor?.UiScope is Controls.TextBox box && (box.AcceptsReturn || box.TextWrapping != TextWrapping.NoWrap);

        /// <summary>Where the caret is, so the candidate strip does not sit on top of it.</summary>
        private void ReportCaretRectangleToAndroidInputMethod(int x, int y, int width, int height)
        {
            if (!IsAndroidTextInputActive) return;
            AndroidTextInput.SetCursorRectangle(x, y, width, Math.Max(height, 1));
        }

        /// <summary>
        /// One change from the input method. Delivered on the Android UI thread, which is the
        /// dispatcher thread, so it reaches the document directly.
        /// </summary>
        private static void OnAndroidImeUpdate(AndroidImeUpdate update)
        {
            s_androidFocused?.ApplyAndroidImeUpdate(update);
        }

        private void ApplyAndroidImeUpdate(AndroidImeUpdate update)
        {
            if (_editor == null || !IsInKeyboardFocus || IsReadOnly) return;

            if (update.DeleteBefore > 0 || update.DeleteAfter > 0)
            {
                DeleteAroundCaret(update.DeleteBefore, update.DeleteAfter);
                if (update.CommitText == null && update.ComposingText == null) return;
            }

            bool hasCommit = !string.IsNullOrEmpty(update.CommitText);
            bool hasComposing = !string.IsNullOrEmpty(update.ComposingText);

            if (!hasCommit && !hasComposing)
            {
                // finishComposingText with nothing to show: the composition is over.
                if (IsComposition)
                {
                    CompleteComposition();
                }
                return;
            }

            char[] resultChars = hasCommit ? update.CommitText.ToCharArray() : null;
            char[] compositionChars = hasComposing ? update.ComposingText.ToCharArray() : null;

            // Android's newCursorPosition is relative to the text just supplied: 1 means "just after
            // it" (the overwhelmingly common case), <= 0 counts back from its start. Anything else
            // is clamped into the composition rather than trusted blindly.
            string active = hasComposing ? update.ComposingText : update.CommitText;
            int caretOffset = update.CursorPosition > 0
                ? active.Length
                : Math.Clamp(active.Length + update.CursorPosition, 0, active.Length);

            UpdateCompositionString(resultChars, compositionChars, caretOffset, 0,
                                    BuildAndroidComposingAttributes(update, resultChars?.Length ?? 0));

            UpdateNearCaretCompositionWindow();
        }

        /// <summary>
        /// Per-character attributes for the composition adorner. Android reports the composing SPAN
        /// but not which clause inside it is being converted (that lives in the IME's own spans,
        /// which a non-full editor never receives), so the composition takes the dotted ATTR_INPUT
        /// underline uniformly and committed characters take none.
        /// </summary>
        private static byte[] BuildAndroidComposingAttributes(AndroidImeUpdate update, int resultLength)
        {
            if (string.IsNullOrEmpty(update.ComposingText)) return null;

            var attributes = new byte[resultLength + update.ComposingText.Length];

            for (int i = 0; i < resultLength; i++)
                attributes[i] = (byte)MS.Win32.NativeMethods.ATTR_FIXEDCONVERTED;

            for (int i = resultLength; i < attributes.Length; i++)
                attributes[i] = (byte)MS.Win32.NativeMethods.ATTR_INPUT;

            return attributes;
        }
    }
}
