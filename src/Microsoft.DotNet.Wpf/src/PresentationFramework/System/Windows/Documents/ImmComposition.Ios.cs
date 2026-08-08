// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// ImmComposition's iOS half: driving a composition from UITextInput.
//
// UIKit does not push text at this file the way the other input methods do. It INTERROGATES the
// document -- what is at this offset, where does the caret sit, how wide is that character -- and
// issues edits against the answers, so the bridge here is a document adapter rather than an event
// handler. UIKitTextInput owns the protocol and the Objective-C side of it; this file is the part
// that knows what the document actually is.
//
// Offsets are UTF-16 indices into the editor's plain text, which is what UIKit counts in. They are
// converted to ITextPointer positions here, so the same code serves a TextBox and a RichTextBox.
//
// With this in place the iOS head reaches the same fidelity as everywhere else: the reading appears
// inline and underlined, converts in place, and commits into the document. The keyboard no longer
// keeps the composition to itself.
//

using MS.Internal.Interop;
using System.Windows.Interop;
using System.Windows.Media;

namespace System.Windows.Documents
{
    internal partial class ImmComposition : IUIKitTextDocument
    {
        /// <summary>True when this instance is the focused editor and the iOS keyboard is up.</summary>
        private bool IsIosTextInputActive =>
            (OperatingSystem.IsIOS() || OperatingSystem.IsMacCatalyst())
            && s_iosFocused == this && UIKitTextInput.IsEnabled;

        private static ImmComposition s_iosFocused;

        /// <summary>Raises the keyboard for this editor. Called from OnGotFocus.</summary>
        private void EnableIosTextInput()
        {
            if (!OperatingSystem.IsIOS() && !OperatingSystem.IsMacCatalyst()) return;
            if (!UIKitTextInput.IsAvailable) return;

            s_iosFocused = this;

            // The protocol reads through this instance from the moment the view becomes first
            // responder, so the document has to be installed before the keyboard is asked for.
            UIKitTextInput.Document = this;

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
            UIKitTextInput.Document = null;
            UIKitTextInput.Disable();
        }

        private bool IsIosMultilineTextBox()
            => _editor?.UiScope is Controls.TextBox box && (box.AcceptsReturn || box.TextWrapping != TextWrapping.NoWrap);

        /// <summary>
        /// The caret moved, so tell UIKit to re-read the selection. Nothing is pushed: it will ask
        /// for the rectangle itself, through caretRectForPosition:.
        /// </summary>
        private void ReportCaretRectangleToIosInputMethod(int x, int y, int width, int height)
        {
            if (!IsIosTextInputActive) return;
            UIKitTextInput.NotifyDocumentChanged();
        }

        // ---- IUIKitTextDocument ----------------------------------------------------
        //
        // Every member is called from an Objective-C IMP on the UI thread. They are deliberately
        // total: an offset UIKit asks about may already have been invalidated by a WPF edit, so each
        // one clamps rather than throwing back into UIKit.

        int IUIKitTextDocument.Length => DocumentText?.Length ?? 0;

        string IUIKitTextDocument.TextInRange(int start, int end)
        {
            string text = DocumentText;
            if (string.IsNullOrEmpty(text)) return string.Empty;

            start = Math.Clamp(start, 0, text.Length);
            end = Math.Clamp(end, start, text.Length);
            return text.Substring(start, end - start);
        }

        void IUIKitTextDocument.GetSelection(out int start, out int end)
        {
            start = end = 0;

            ITextRange selection = _editor?.Selection;
            ITextPointer documentStart = _editor?.TextContainer?.Start;
            if (selection == null || documentStart == null) return;

            start = Math.Max(0, documentStart.GetOffsetToPosition(selection.Start));
            end = Math.Max(start, documentStart.GetOffsetToPosition(selection.End));
        }

        void IUIKitTextDocument.SetSelection(int start, int end)
        {
            ITextPointer from = PositionAtOffset(start);
            ITextPointer to = PositionAtOffset(end);
            if (from == null || to == null) return;

            try { _editor?.Selection?.Select(from, to); }
            catch (InvalidOperationException) { }   // the document moved under us
        }

        bool IUIKitTextDocument.TryGetMarkedRange(out int start, out int end)
        {
            start = end = 0;
            if (!IsComposition || _startComposition == null || _endComposition == null) return false;

            ITextPointer documentStart = _editor?.TextContainer?.Start;
            if (documentStart == null) return false;

            start = Math.Max(0, documentStart.GetOffsetToPosition(_startComposition));
            end = Math.Max(start, documentStart.GetOffsetToPosition(_endComposition));
            return true;
        }

        void IUIKitTextDocument.SetMarkedText(string text, int selectionStart, int selectionLength)
        {
            if (_editor == null || !IsInKeyboardFocus || IsReadOnly) return;

            if (string.IsNullOrEmpty(text))
            {
                // UIKit cancels a composition with an empty marked string.
                if (IsComposition) CompleteComposition();
                return;
            }

            // The caret sits where the input method put it inside the reading. UIKit gives that as a
            // range within the marked text, and its start is where the cursor is drawn.
            int caret = Math.Clamp(selectionStart, 0, text.Length);

            UpdateCompositionString(null, text.ToCharArray(), caret, 0, MarkedAttributes(text.Length));
        }

        void IUIKitTextDocument.UnmarkText()
        {
            if (IsComposition) CompleteComposition();
        }

        void IUIKitTextDocument.ReplaceRange(int start, int end, string text)
        {
            if (_editor == null || !IsInKeyboardFocus || IsReadOnly) return;

            // A replacement ENDS any composition: this is UIKit committing, autocorrecting or
            // deleting, none of which leave a reading in flight.
            if (IsComposition)
            {
                CompleteComposition();
            }

            ITextPointer from = PositionAtOffset(start);
            ITextPointer to = PositionAtOffset(end);
            if (from == null || to == null) return;

            try
            {
                if (from.CompareTo(to) != 0)
                {
                    _editor.Selection.Select(from, to);
                    _editor.Selection.Text = string.Empty;
                }
                else
                {
                    _editor.Selection.Select(from, from);
                }

                if (!string.IsNullOrEmpty(text))
                {
                    UpdateCompositionString(text.ToCharArray(), null, text.Length, 0, null);
                }
            }
            catch (InvalidOperationException) { }   // the document moved under us
        }

        bool IUIKitTextDocument.TryGetCaretRect(int offset, out double x, out double y, out double width, out double height)
        {
            x = y = width = height = 0;

            ITextPointer position = PositionAtOffset(offset);
            ITextView view = _editor?.TextView;
            if (position == null || view == null || !view.IsValid) return false;

            try
            {
                Rect rect = view.GetRectangleFromTextPosition(position.CreatePointer(LogicalDirection.Forward));
                if (rect == Rect.Empty) return false;

                // Same conversion the IMM32 path makes: element space to device pixels.
                if (!GetTransformToDevice(out GeneralTransform transform, out Matrix toDevice)) return false;

                if (!transform.TryTransform(new Point(rect.Left, rect.Top), out Point topLeft) ||
                    !transform.TryTransform(new Point(rect.Left, rect.Bottom), out Point bottomLeft))
                {
                    return false;
                }

                topLeft = toDevice.Transform(topLeft);
                bottomLeft = toDevice.Transform(bottomLeft);

                x = topLeft.X;
                y = topLeft.Y;
                width = 1;
                height = Math.Max(1, bottomLeft.Y - topLeft.Y);
                return true;
            }
            catch (InvalidOperationException) { return false; }
        }

        // ---- helpers ---------------------------------------------------------------

        /// <summary>The editor's plain text, which is the string UIKit believes it is editing.</summary>
        private string DocumentText
        {
            get
            {
                ITextContainer container = _editor?.TextContainer;
                if (container == null) return string.Empty;

                try
                {
                    return new TextRange(container.Start, container.End).Text ?? string.Empty;
                }
                catch (InvalidOperationException) { return string.Empty; }
            }
        }

        private ITextPointer PositionAtOffset(int offset)
        {
            ITextPointer start = _editor?.TextContainer?.Start;
            if (start == null) return null;

            try
            {
                int max = _editor.TextContainer.Start.GetOffsetToPosition(_editor.TextContainer.End);
                return start.CreatePointer(Math.Clamp(offset, 0, Math.Max(0, max)));
            }
            catch (ArgumentException) { return null; }        // offset outside the container
            catch (InvalidOperationException) { return null; }
        }

        private bool GetTransformToDevice(out GeneralTransform transform, out Matrix toDevice)
        {
            transform = null;
            toDevice = Matrix.Identity;

            if (_source == null || UiScope == null) return false;

            CompositionTarget target = _source.CompositionTarget;
            if (target == null) return false;

            transform = UiScope.TransformToAncestor(target.RootVisual);
            toDevice = target.TransformToDevice;
            return transform != null;
        }

        /// <summary>
        /// The composition draws with one dotted underline. iOS reports no clause selection of its
        /// own (its keyboards convert the whole reading at once), so marking part of it as converted
        /// would be an invention.
        /// </summary>
        private static byte[] MarkedAttributes(int length)
        {
            var attributes = new byte[length];
            for (int i = 0; i < length; i++)
                attributes[i] = (byte)MS.Win32.NativeMethods.ATTR_INPUT;
            return attributes;
        }
    }
}
