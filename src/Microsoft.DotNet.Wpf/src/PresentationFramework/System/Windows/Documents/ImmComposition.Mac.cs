// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// ImmComposition's macOS half: driving a composition from NSTextInputClient instead of IMM32.
//
// The shape of the problem is the Linux one -- an engaged input method takes the keystrokes and
// hands back a composition rather than characters -- but AppKit's half of the conversation is not
// batched. Wayland sends one atomic done() carrying delete + commit + preedit together; AppKit sends
// -setMarkedText:... and -insertText:... as separate messages, each of which arrives here as its own
// CocoaImeUpdate. That is the only structural difference, and it is why this file is a sibling of
// ImmComposition.Linux.cs rather than a shared abstraction with it: the two backends answer to
// different protocols and are kept self-contained on purpose.
//
// Everything downstream is platform neutral. UpdateCompositionString, the FrameworkTextComposition,
// the public TextInputStart/TextInputUpdate/TextInput events and the CompositionAdorner underline
// all live in ImmComposition.cs, and this file's whole job is to reach them with the same arguments
// the IMM32 message handlers use.
//
// The Objective-C runtime work (the client class, NSTextInputContext, the coordinate conversion)
// belongs to CocoaTextInput, next to the rest of the Cocoa bindings; there are no native calls here.
//

using MS.Internal.Interop;
using System.Windows.Interop;

namespace System.Windows.Documents
{
    internal partial class ImmComposition
    {
        /// <summary>True when this instance is the focused editor and the Cocoa IME is live.</summary>
        private bool IsMacTextInputActive =>
            OperatingSystem.IsMacOS() && s_macFocused == this && CocoaTextInput.IsEnabled;

        // The instance the input method is talking to. There is one input context for the process,
        // not one per window, so its callbacks have to be routed to whichever editor holds focus.
        private static ImmComposition s_macFocused;
        private static bool s_macHooked;

        /// <summary>
        /// Starts (or moves) the input-method session to this editor. Called from OnGotFocus.
        /// </summary>
        private void EnableMacTextInput()
        {
            if (!OperatingSystem.IsMacOS()) return;
            if (!CocoaTextInput.IsAvailable) return;

            if (!s_macHooked)
            {
                s_macHooked = true;
                CocoaTextInput.ImeUpdate += OnMacImeUpdate;
            }

            s_macFocused = this;

            bool multiline = _editor?.AcceptsRichContent == true || IsMacMultilineTextBox();
            bool password = _editor?.UiScope is Controls.PasswordBox;

            CocoaTextInput.Enable(multiline, password);

            // Activating resets what the input method knows, so the context it needs follows it.
            UpdateNearCaretCompositionWindow();
            ReportSurroundingTextToMacInputMethod();
        }

        /// <summary>Ends the session. Called from OnLostFocus.</summary>
        private void DisableMacTextInput()
        {
            if (!OperatingSystem.IsMacOS()) return;
            if (s_macFocused != this) return;

            s_macFocused = null;
            CocoaTextInput.Disable();
        }

        private bool IsMacMultilineTextBox()
            => _editor?.UiScope is Controls.TextBox box && (box.AcceptsReturn || box.TextWrapping != TextWrapping.NoWrap);

        /// <summary>
        /// Where the caret is, so the candidate window appears under the text being typed. The
        /// rectangle arrives in device pixels relative to the window's client area; the screen
        /// points AppKit asks for are CocoaTextInput's conversion to make.
        /// </summary>
        private void ReportCaretRectangleToMacInputMethod(int x, int y, int width, int height)
        {
            if (!IsMacTextInputActive || _source == null) return;

            try
            {
                CocoaTextInput.SetCursorRectangleForWindow(
                    ((IWin32Window)_source).Handle, x, y, width, Math.Max(height, 1));
            }
            catch (InvalidOperationException) { }   // source torn down mid-update
        }

        /// <summary>
        /// The text on either side of the caret, which the input method uses for predictive
        /// conversion and to resolve the replacement ranges it sends back.
        /// </summary>
        private void ReportSurroundingTextToMacInputMethod()
        {
            if (!IsMacTextInputActive || _editor == null) return;

            try
            {
                ITextRange selection = _editor.Selection;
                if (selection == null) return;

                string surrounding = GetSurroundingText(selection, out int offsetStart);
                if (surrounding == null) return;

                int anchor = Math.Clamp(offsetStart, 0, surrounding.Length);
                int cursor = Math.Clamp(offsetStart + (selection.Text?.Length ?? 0), 0, surrounding.Length);

                CocoaTextInput.SetSurroundingText(surrounding, cursor, anchor);
            }
            catch (InvalidOperationException) { }   // the selection moved under us
        }

        /// <summary>
        /// One change from the input method. Unlike the Wayland path this is not an atomic batch:
        /// a conversion arrives as a marked-text update, and accepting it arrives later as a commit.
        /// </summary>
        private static void OnMacImeUpdate(CocoaImeUpdate update)
        {
            ImmComposition target = s_macFocused;
            if (target == null) return;

            // The callbacks come off -handleEvent: on the pump thread, which is the UI thread; the
            // check is here because reaching the document from anywhere else must not be silent.
            FrameworkElement scope = target.UiScope;
            if (scope != null && !scope.Dispatcher.CheckAccess())
            {
                scope.Dispatcher.BeginInvoke(
                    (Action)(() => OnMacImeUpdate(update)), Threading.DispatcherPriority.Input);
                return;
            }

            target.ApplyMacImeUpdate(update);
        }

        private void ApplyMacImeUpdate(CocoaImeUpdate update)
        {
            if (_editor == null || !IsInKeyboardFocus || IsReadOnly) return;

            if (update.ReplaceBeforeChars > 0 || update.ReplaceAfterChars > 0)
            {
                DeleteAroundCaret(update.ReplaceBeforeChars, update.ReplaceAfterChars);
            }

            bool hasCommit = !string.IsNullOrEmpty(update.CommitText);
            bool hasPreedit = !string.IsNullOrEmpty(update.PreeditText);

            if (!hasCommit && !hasPreedit)
            {
                // The composition ended with nothing to show for it: Escape, or the last kana
                // backspaced away.
                if (IsComposition)
                {
                    CompleteComposition();
                }
                return;
            }

            char[] resultChars = hasCommit ? update.CommitText.ToCharArray() : null;
            char[] compositionChars = hasPreedit ? update.PreeditText.ToCharArray() : null;

            // The caret sits where the input method put it inside the composition; with no
            // composition left it follows the text just committed.
            int caretOffset = hasPreedit
                ? (update.PreeditCursorBegin >= 0 ? update.PreeditCursorBegin : update.PreeditText.Length)
                : update.CommitText.Length;

            UpdateCompositionString(resultChars, compositionChars, caretOffset, 0,
                                    BuildMacPreeditAttributes(update, resultChars?.Length ?? 0));

            // The caret moved, so the candidate window has to follow it.
            UpdateNearCaretCompositionWindow();
            ReportSurroundingTextToMacInputMethod();
        }

        /// <summary>
        /// Per-character attributes for the composition adorner. The clause the input method has
        /// selected -- what it is converting right now -- takes the solid underline IMM32 calls
        /// ATTR_TARGET_CONVERTED; the rest of the composition takes the dotted one. Committed
        /// characters carry no attribute, being final text rather than composition.
        /// </summary>
        private static byte[] BuildMacPreeditAttributes(CocoaImeUpdate update, int resultLength)
        {
            if (string.IsNullOrEmpty(update.PreeditText)) return null;

            var attributes = new byte[resultLength + update.PreeditText.Length];

            for (int i = 0; i < resultLength; i++)
                attributes[i] = (byte)MS.Win32.NativeMethods.ATTR_FIXEDCONVERTED;

            int begin = update.PreeditCursorBegin;
            int end = update.PreeditCursorEnd;
            bool hasSelection = begin >= 0 && end > begin;

            for (int i = 0; i < update.PreeditText.Length; i++)
            {
                attributes[resultLength + i] = (byte)(hasSelection && i >= begin && i < end
                    ? MS.Win32.NativeMethods.ATTR_TARGET_CONVERTED
                    : MS.Win32.NativeMethods.ATTR_INPUT);
            }

            return attributes;
        }

    }
}
