// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// ImmComposition's Linux half: driving a composition from zwp_text_input_v3 instead of IMM32.
//
// On Wayland an engaged input method CONSUMES the keystrokes -- the compositor routes them to
// fcitx5/ibus/... and the client never sees them. What comes back instead is a preedit string (the
// in-progress composition, which the client draws inline) and a commit string (the final text).
// Without this file, typing Japanese, Chinese or Korean into a WPF app on Linux produced nothing at
// all: the keys went to the input method and their result had nowhere to land.
//
// Only the TRANSPORT differs from Windows. Everything that turns text into a composition -- building
// a FrameworkTextComposition, raising the public TextInputStart/TextInputUpdate/TextInput events,
// drawing the underline through CompositionAdorner -- lives in ImmComposition.cs and is platform
// neutral, so this file translates the input method's batches into the same UpdateCompositionString
// calls the IMM32 message handlers make, and nothing else.
//
// The protocol itself (and every P/Invoke behind it) belongs to WaylandTextInput, next to the rest
// of the Wayland bindings; this file holds no native calls of its own.
//

using System.Windows.Interop;
using MS.Internal.Interop.Wayland;
using MS.Win32;

namespace System.Windows.Documents
{
    internal partial class ImmComposition
    {
        /// <summary>True when this instance is the focused editor and the Wayland IME is live.</summary>
        private bool IsLinuxTextInputActive =>
            OperatingSystem.IsLinux() && s_linuxFocused == this &&
            WaylandTextInput.IsEnabled;

        // The instance the input method is currently talking to. There is one input-method channel
        // per seat, not per window, so the batches it sends have to be routed to whichever editor
        // holds focus -- exactly what the compositor means by sending them at all.
        private static ImmComposition s_linuxFocused;
        private static bool s_linuxHooked;

        /// <summary>
        /// Starts (or moves) the input-method channel to this editor. Called from OnGotFocus.
        /// </summary>
        private void EnableLinuxTextInput()
        {
            if (!OperatingSystem.IsLinux()) return;
            if (!WaylandTextInput.IsAvailable) return;

            if (!s_linuxHooked)
            {
                s_linuxHooked = true;
                WaylandTextInput.ImeUpdate += OnLinuxImeUpdate;
                WaylandTextInput.FocusChanged += OnLinuxTextInputFocusChanged;
            }

            s_linuxFocused = this;

            bool multiline = _editor?.AcceptsRichContent == true || IsMultilineTextBox();
            bool password = _editor?.UiScope is Controls.PasswordBox;

            WaylandTextInput.Enable(multiline, password);

            // enable() resets everything staged on the compositor side, so the context the input
            // method needs has to follow it, not precede it.
            UpdateNearCaretCompositionWindow();
            ReportSurroundingTextToLinuxInputMethod();
        }

        /// <summary>Tells the input method no field is being edited. Called from OnLostFocus.</summary>
        private void DisableLinuxTextInput()
        {
            if (!OperatingSystem.IsLinux()) return;
            if (s_linuxFocused != this) return;

            s_linuxFocused = null;
            WaylandTextInput.Disable();
        }

        /// <summary>
        /// The input method attached to (or left) a surface. The two focus notions are independent:
        /// a TextBox can take keyboard focus before the compositor has told the input method which
        /// surface it is on, in which case the enable() sent then went nowhere -- so it is sent
        /// again here, once there is something to send it to.
        /// </summary>
        private static void OnLinuxTextInputFocusChanged(IntPtr surface, bool entered)
        {
            if (!entered) return;

            ImmComposition focused = s_linuxFocused;
            if (focused == null || WaylandTextInput.IsEnabled) return;

            FrameworkElement scope = focused.UiScope;
            if (scope != null && !scope.Dispatcher.CheckAccess())
            {
                scope.Dispatcher.BeginInvoke(
                    (Action)(() => OnLinuxTextInputFocusChanged(surface, entered)),
                    Threading.DispatcherPriority.Input);
                return;
            }

            if (focused.IsInKeyboardFocus)
            {
                focused.EnableLinuxTextInput();
            }
        }

        private bool IsMultilineTextBox()
            => _editor?.UiScope is Controls.TextBox box && (box.AcceptsReturn || box.TextWrapping != TextWrapping.NoWrap);

        /// <summary>
        /// Where the caret is, so the candidate window appears under the text being typed. The
        /// rectangle arrives in device pixels relative to the window's client area; the protocol
        /// wants surface-local logical units, which WaylandTextInput converts.
        /// </summary>
        private void ReportCaretRectangleToLinuxInputMethod(int x, int y, int width, int height)
        {
            if (!IsLinuxTextInputActive || _source == null) return;

            try
            {
                WaylandTextInput.SetCursorRectangleForWindow(
                    ((IWin32Window)_source).Handle, x, y, width, Math.Max(height, 1));
            }
            catch (InvalidOperationException) { }   // source torn down mid-update
        }

        /// <summary>
        /// The text on either side of the caret. Input methods use it for predictive conversion and
        /// to work out what delete_surrounding_text should remove; the same helper the IMM32
        /// reconversion path uses supplies it.
        /// </summary>
        private void ReportSurroundingTextToLinuxInputMethod()
        {
            if (!IsLinuxTextInputActive || _editor == null) return;

            try
            {
                ITextRange selection = _editor.Selection;
                if (selection == null) return;

                string surrounding = GetSurroundingText(selection, out int offsetStart);
                if (surrounding == null) return;

                // The selection's own text sits between anchor and cursor within the window.
                int anchor = Math.Clamp(offsetStart, 0, surrounding.Length);
                int cursor = Math.Clamp(offsetStart + (selection.Text?.Length ?? 0), 0, surrounding.Length);

                WaylandTextInput.SetSurroundingText(surrounding, cursor, anchor);
            }
            catch (InvalidOperationException) { }   // the selection moved under us; the next update carries it
        }

        /// <summary>
        /// One atomic batch from the input method. The protocol fixes the order -- delete the
        /// surrounding text, insert the commit string, then show the new preedit -- and each part is
        /// optional.
        /// </summary>
        private static void OnLinuxImeUpdate(WaylandImeUpdate update)
        {
            ImmComposition target = s_linuxFocused;
            if (target == null) return;

            // The Wayland events are dispatched from the run loop on the UI thread, but a compositor
            // that delivers them from a read thread must not reach the document directly.
            FrameworkElement scope = target.UiScope;
            if (scope != null && !scope.Dispatcher.CheckAccess())
            {
                scope.Dispatcher.BeginInvoke(
                    (Action)(() => OnLinuxImeUpdate(update)), Threading.DispatcherPriority.Input);
                return;
            }

            target.ApplyImeUpdate(update);
        }

        private void ApplyImeUpdate(WaylandImeUpdate update)
        {
            if (_editor == null || !IsInKeyboardFocus || IsReadOnly) return;

            if (update.DeleteBeforeBytes > 0 || update.DeleteAfterBytes > 0)
            {
                DeleteSurroundingText(update.DeleteBeforeBytes, update.DeleteAfterBytes);
            }

            bool hasCommit = !string.IsNullOrEmpty(update.CommitText);
            bool hasPreedit = !string.IsNullOrEmpty(update.PreeditText);

            if (!hasCommit && !hasPreedit)
            {
                // The input method ended the composition without producing text (the user pressed
                // Escape, or backspaced the last kana away).
                if (IsComposition)
                {
                    CompleteComposition();
                }
                return;
            }

            char[] resultChars = hasCommit ? update.CommitText.ToCharArray() : null;
            char[] compositionChars = hasPreedit ? update.PreeditText.ToCharArray() : null;

            // The caret sits where the input method put it within the preedit; with no preedit it
            // follows the committed text.
            int caretOffset = hasPreedit
                ? (update.PreeditCursorBegin >= 0 ? update.PreeditCursorBegin : update.PreeditText.Length)
                : update.CommitText.Length;

            UpdateCompositionString(resultChars, compositionChars, caretOffset, 0,
                                    BuildPreeditAttributes(update, resultChars?.Length ?? 0));

            // The caret has moved, so the candidate window has to follow it.
            UpdateNearCaretCompositionWindow();
            ReportSurroundingTextToLinuxInputMethod();
        }

        /// <summary>
        /// Turns the preedit's cursor span into the per-character attribute array the composition
        /// adorner draws from: the span the input method has selected (the clause being converted)
        /// gets the solid underline IMM32 calls ATTR_TARGET_CONVERTED, the rest the dotted one.
        /// Committed characters carry no attribute -- they are final text, not composition.
        /// </summary>
        private static byte[] BuildPreeditAttributes(WaylandImeUpdate update, int resultLength)
        {
            if (string.IsNullOrEmpty(update.PreeditText)) return null;

            var attributes = new byte[resultLength + update.PreeditText.Length];

            for (int i = 0; i < resultLength; i++)
                attributes[i] = (byte)NativeMethods.ATTR_FIXEDCONVERTED;

            int begin = update.PreeditCursorBegin;
            int end = update.PreeditCursorEnd;
            bool hasSelection = begin >= 0 && end > begin;

            for (int i = 0; i < update.PreeditText.Length; i++)
            {
                attributes[resultLength + i] = (byte)(hasSelection && i >= begin && i < end
                    ? NativeMethods.ATTR_TARGET_CONVERTED
                    : NativeMethods.ATTR_INPUT);
            }

            return attributes;
        }

        /// <summary>
        /// Removes text around the caret at the input method's request. The protocol counts in UTF-8
        /// BYTES relative to the surrounding text we last reported, so the counts are converted back
        /// to characters against that same text rather than assumed to be character counts -- for
        /// CJK, where every character is three bytes, assuming otherwise deletes three times too much.
        /// </summary>
        private void DeleteSurroundingText(uint beforeBytes, uint afterBytes)
        {
            ITextRange selection = _editor?.Selection;
            if (selection == null) return;

            string surrounding = GetSurroundingText(selection, out int offsetStart);
            if (surrounding == null) return;

            int caret = Math.Clamp(offsetStart, 0, surrounding.Length);
            int before = CharsForBytesBefore(surrounding, caret, (int)beforeBytes);
            int after = CharsForBytesAfter(surrounding, Math.Clamp(offsetStart + (selection.Text?.Length ?? 0), 0, surrounding.Length), (int)afterBytes);

            if (before <= 0 && after <= 0) return;

            ITextPointer start = selection.Start.CreatePointer();
            ITextPointer end = selection.End.CreatePointer();

            if (before > 0) start = start.CreatePointer(-before);
            if (after > 0) end = end.CreatePointer(after);

            if (start != null && end != null && start.CompareTo(end) < 0)
            {
                _editor.Selection.Select(start, end);
                _editor.Selection.Text = string.Empty;
            }
        }

        // Number of UTF-16 characters immediately before `index` that make up `bytes` UTF-8 bytes.
        private static int CharsForBytesBefore(string text, int index, int bytes)
        {
            int chars = 0, counted = 0;
            while (counted < bytes && index - chars > 0)
            {
                int step = (index - chars >= 2 && Char.IsLowSurrogate(text[index - chars - 1])) ? 2 : 1;
                counted += System.Text.Encoding.UTF8.GetByteCount(text.AsSpan(index - chars - step, step));
                chars += step;
            }
            return chars;
        }

        // Number of UTF-16 characters immediately after `index` that make up `bytes` UTF-8 bytes.
        private static int CharsForBytesAfter(string text, int index, int bytes)
        {
            int chars = 0, counted = 0;
            while (counted < bytes && index + chars < text.Length)
            {
                int step = (index + chars + 1 < text.Length && Char.IsHighSurrogate(text[index + chars])) ? 2 : 1;
                counted += System.Text.Encoding.UTF8.GetByteCount(text.AsSpan(index + chars, step));
                chars += step;
            }
            return chars;
        }
    }
}
