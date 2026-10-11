// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// ImmComposition's browser half: driving a composition from DOM composition events.
//
// Same shape as the Cocoa half, and for the same reason: compositionupdate and compositionend arrive
// as separate messages, so each becomes its own update rather than the single atomic batch Wayland
// delivers. The transport (an invisible contenteditable that the IME attaches to, and the event
// queue it feeds) belongs to BrowserTextInput next to the rest of the browser bindings; there are no
// interop calls here.
//
// One capability is genuinely missing on this platform rather than merely unimplemented: the DOM
// does not expose which clause of the composition the IME is currently converting. IMM32 says so
// with ATTR_TARGET_CONVERTED and Wayland with preedit cursor_begin/end, and both drive the solid
// underline under the active clause. compositionupdate carries the string and nothing else, so the
// whole preedit is marked ATTR_INPUT and draws with one dotted underline. Everything else -- the
// inline composition, the caret, the commit -- behaves as it does everywhere else.
//

using MS.Internal.Interop;

namespace System.Windows.Documents
{
    internal partial class ImmComposition
    {
        /// <summary>True when this instance is the focused editor and the browser IME is live.</summary>
        private bool IsBrowserTextInputActive =>
            OperatingSystem.IsBrowser() && s_browserFocused == this && BrowserTextInput.IsEnabled;

        // The instance the input method is talking to. The browser focuses one element at a time, so
        // like the Cocoa input context this is a single channel routed to whoever holds focus.
        private static ImmComposition s_browserFocused;
        private static bool s_browserHooked;

        /// <summary>Starts (or moves) the input-method session to this editor. Called from OnGotFocus.</summary>
        private void EnableBrowserTextInput()
        {
            if (!OperatingSystem.IsBrowser()) return;
            if (!BrowserTextInput.IsAvailable) return;

            if (!s_browserHooked)
            {
                s_browserHooked = true;
                BrowserTextInput.ImeUpdate += OnBrowserImeUpdate;
            }

            s_browserFocused = this;

            bool multiline = _editor?.AcceptsRichContent == true || IsBrowserMultilineTextBox();
            bool password = _editor?.UiScope is Controls.PasswordBox;

            BrowserTextInput.Enable(multiline, password);

            // The editable element has to sit at the caret before the IME engages, or the first
            // candidate window opens wherever it was last parked.
            UpdateNearCaretCompositionWindow();
        }

        /// <summary>Ends the session. Called from OnLostFocus.</summary>
        private void DisableBrowserTextInput()
        {
            if (!OperatingSystem.IsBrowser()) return;
            if (s_browserFocused != this) return;

            s_browserFocused = null;
            BrowserTextInput.Disable();
        }

        private bool IsBrowserMultilineTextBox()
            => _editor?.UiScope is Controls.TextBox box && (box.AcceptsReturn || box.TextWrapping != TextWrapping.NoWrap);

        /// <summary>
        /// Where the caret is. On this platform the rectangle is not sent to the input method: it
        /// moves the focused element, and the browser anchors its candidate window to that.
        /// </summary>
        private void ReportCaretRectangleToBrowserInputMethod(int x, int y, int width, int height)
        {
            if (!IsBrowserTextInputActive) return;

            BrowserTextInput.SetCursorRectangle(x, y, width, Math.Max(height, 1));
        }

        /// <summary>
        /// One composition change. Already on the UI thread: the events are drained by the
        /// dispatcher's browser pump, and wasm has no other thread to deliver them from.
        /// </summary>
        private static void OnBrowserImeUpdate(BrowserImeUpdate update)
        {
            s_browserFocused?.ApplyBrowserImeUpdate(update);
        }

        private void ApplyBrowserImeUpdate(BrowserImeUpdate update)
        {
            if (_editor == null || !IsInKeyboardFocus || IsReadOnly) return;

            bool hasCommit = !string.IsNullOrEmpty(update.CommitText);
            bool hasPreedit = !string.IsNullOrEmpty(update.PreeditText);

            if (!hasCommit && !hasPreedit)
            {
                // The composition ended with nothing to show for it (Escape, or everything deleted).
                if (IsComposition)
                {
                    CompleteComposition();
                }
                return;
            }

            char[] resultChars = hasCommit ? update.CommitText.ToCharArray() : null;
            char[] compositionChars = hasPreedit ? update.PreeditText.ToCharArray() : null;

            // No clause information from the DOM, so the caret goes to the end of the composition --
            // which is where every browser IME puts it anyway while typing.
            int caretOffset = hasPreedit ? update.PreeditText.Length : update.CommitText.Length;

            UpdateCompositionString(resultChars, compositionChars, caretOffset, 0,
                                    BuildBrowserPreeditAttributes(update));

            // The caret moved, so the element the candidates hang off has to follow it.
            UpdateNearCaretCompositionWindow();
        }

        /// <summary>
        /// Per-character attributes for the composition adorner. Uniformly ATTR_INPUT: the dotted
        /// "not yet converted" underline, which is honest about what the DOM tells us. Marking a
        /// clause as converted would mean guessing which one, and a wrong guess underlines the wrong
        /// characters on every keystroke.
        /// </summary>
        private static byte[] BuildBrowserPreeditAttributes(BrowserImeUpdate update)
        {
            if (string.IsNullOrEmpty(update.PreeditText)) return null;

            var attributes = new byte[update.PreeditText.Length];
            for (int i = 0; i < attributes.Length; i++)
                attributes[i] = (byte)MS.Win32.NativeMethods.ATTR_INPUT;

            return attributes;
        }
    }
}
