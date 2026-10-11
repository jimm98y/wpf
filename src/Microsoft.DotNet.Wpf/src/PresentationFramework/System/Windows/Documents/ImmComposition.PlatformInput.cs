// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Which input-method transport a platform has, and the four hooks ImmComposition.cs calls without
// caring which one answered.
//
// This is a switch, not a shared abstraction: each backend stays whole in its own file
// (ImmComposition.Linux.cs speaks zwp_text_input_v3, ImmComposition.Mac.cs speaks NSTextInputClient,
// ImmComposition.cs itself speaks IMM32), because the protocols agree on nothing except that a
// composition eventually becomes text. What they do share is where they enter the editor, and that
// is exactly what this file names.
//

namespace System.Windows.Documents
{
    internal partial class ImmComposition
    {
        /// <summary>
        /// Whether this platform has an input-method channel at all. Drives whether TextEditor
        /// creates an ImmComposition off Windows; evaluated per call rather than cached because a
        /// backend can come up lazily (the Wayland connection does), after this type is initialized.
        /// </summary>
        internal static bool IsPlatformInputMethodAvailable
            => s_immEnabled
               || (OperatingSystem.IsLinux() && MS.Internal.Interop.Wayland.WaylandTextInput.IsAvailable)
               || (OperatingSystem.IsMacOS() && MS.Internal.Interop.CocoaTextInput.IsAvailable)
               || (OperatingSystem.IsBrowser() && MS.Internal.Interop.BrowserTextInput.IsAvailable)
               || (OperatingSystem.IsAndroid() && MS.Internal.Interop.AndroidTextInput.IsAvailable)
               || ((OperatingSystem.IsIOS() || OperatingSystem.IsMacCatalyst())
                   && MS.Internal.Interop.UIKitTextInput.IsAvailable);

        // Windows' half of the answer never changes, and asking the system metric on every focus
        // change is what the static this replaced was avoiding.
        private static readonly bool s_immEnabled = MS.Win32.SafeSystemMetrics.IsImmEnabled;

        /// <summary>True when this editor is the one the platform's input method is talking to.</summary>
        private bool IsPlatformTextInputActive
            => IsLinuxTextInputActive || IsMacTextInputActive || IsBrowserTextInputActive
               || IsAndroidTextInputActive || IsIosTextInputActive;

        /// <summary>Starts (or moves) the input-method session to this editor.</summary>
        private void EnablePlatformTextInput()
        {
            EnableLinuxTextInput();
            EnableMacTextInput();
            EnableBrowserTextInput();
            EnableAndroidTextInput();
            EnableIosTextInput();
        }

        /// <summary>Tells the input method no field is being edited.</summary>
        private void DisablePlatformTextInput()
        {
            DisableLinuxTextInput();
            DisableMacTextInput();
            DisableBrowserTextInput();
            DisableAndroidTextInput();
            DisableIosTextInput();
        }

        /// <summary>Reports the caret rectangle, in client device pixels, to whichever backend is live.</summary>
        private void ReportCaretRectangleToInputMethod(int x, int y, int width, int height)
        {
            ReportCaretRectangleToLinuxInputMethod(x, y, width, height);
            ReportCaretRectangleToMacInputMethod(x, y, width, height);
            ReportCaretRectangleToBrowserInputMethod(x, y, width, height);
            ReportCaretRectangleToAndroidInputMethod(x, y, width, height);
            ReportCaretRectangleToIosInputMethod(x, y, width, height);
        }

        /// <summary>
        /// Removes text around the caret at an input method's request, counted in UTF-16 units.
        ///
        /// Shared rather than per-backend because there is no platform in it: it is document
        /// editing, and Cocoa and Android both ask for exactly this (Wayland does not, counting in
        /// UTF-8 bytes, so its own version converts first and lives with the Wayland code). It began
        /// in the Cocoa half and was called across from the Android one, which is precisely the
        /// coupling the per-backend split exists to prevent.
        /// </summary>
        private void DeleteAroundCaret(int beforeChars, int afterChars)
        {
            ITextRange selection = _editor?.Selection;
            ITextContainer container = _editor?.TextContainer;
            if (selection == null || container == null) return;

            // Clamp to what the document actually holds. An input method asks to delete relative to
            // ITS view of the text, which can be ahead of the document -- Android sends a backspace
            // for a composition it believes exists, and the obvious CreatePointer(-1) then runs off
            // the start and throws ArgumentException out of an InputConnection callback, killing the
            // app. (Seen exactly that way: backspace in an empty TextBox.)
            int available = Math.Max(0, container.Start.GetOffsetToPosition(selection.Start));
            int remaining = Math.Max(0, selection.End.GetOffsetToPosition(container.End));

            beforeChars = Math.Min(beforeChars, available);
            afterChars = Math.Min(afterChars, remaining);
            if (beforeChars <= 0 && afterChars <= 0) return;

            ITextPointer start = selection.Start.CreatePointer();
            ITextPointer end = selection.End.CreatePointer();

            try
            {
                if (beforeChars > 0) start = start.CreatePointer(-beforeChars);
                if (afterChars > 0) end = end.CreatePointer(afterChars);
            }
            catch (ArgumentException)
            {
                // The offsets above count characters, but a pointer distance also counts element
                // edges in a RichTextBox, so a clamped count can still land outside. Nothing to
                // delete is a better answer than an exception the input method cannot handle.
                return;
            }

            if (start != null && end != null && start.CompareTo(end) < 0)
            {
                _editor.Selection.Select(start, end);
                _editor.Selection.Text = string.Empty;
            }
        }
        /// <summary>Reports the text around the caret to whichever backend is live.</summary>
        private void ReportSurroundingTextToInputMethod()
        {
            ReportSurroundingTextToLinuxInputMethod();
            ReportSurroundingTextToMacInputMethod();
        }
    }
}
