// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The Android text-input channel: InputConnection.
//
// Before this file the Android head had no text input of any kind. A WPF window there is a
// SurfaceView, and a SurfaceView is not a text editor: it never asks for the soft keyboard, so
// nothing could be typed into a TextBox at all -- Japanese least of all.
//
// Android's contract for "I am something you can type into" is InputConnection, and it is a good
// fit for what ImmComposition already wants. Unlike UIKeyInput on iOS it DOES have composing text
// (setComposingText, the underlined reading a Japanese IME shows while converting), so the inline
// composition works here exactly as it does on Windows, Linux and macOS.
//
// The half that talks to android.* lives in AndroidHost.cs, the payload each app head compiles in,
// because WindowsBase cannot reference the Mono.Android bindings (see the header of AndroidWindow.cs).
// This file is the WindowsBase end: the host pushes text in through the Notify* entry points on
// AndroidWindow, and it comes out here as an AndroidImeUpdate.
//

using System;
using System.Runtime.Versioning;

namespace MS.Internal.Interop
{
    /// <summary>
    /// One change from the Android input method. The parts are independent: an update may carry
    /// composing text, committed text, or a deletion around the caret.
    /// </summary>
    internal readonly struct AndroidImeUpdate
    {
        /// <summary>Final text to insert, or null if the input method committed nothing.</summary>
        public string CommitText { get; }

        /// <summary>The in-progress composition to display, or null to end/clear it.</summary>
        public string ComposingText { get; }

        /// <summary>
        /// Where the input method put the caret within the composing text, in UTF-16 offsets, or -1
        /// when it did not say. Android expresses this as newCursorPosition relative to the text.
        /// </summary>
        public int CursorPosition { get; }

        /// <summary>Characters to remove either side of the caret first (UTF-16 units, Android's own
        /// unit for deleteSurroundingText).</summary>
        public int DeleteBefore { get; }
        public int DeleteAfter { get; }

        public AndroidImeUpdate(string commit, string composing, int cursorPosition,
                                int deleteBefore, int deleteAfter)
        {
            CommitText = commit;
            ComposingText = composing;
            CursorPosition = cursorPosition;
            DeleteBefore = deleteBefore;
            DeleteAfter = deleteAfter;
        }
    }

    [SupportedOSPlatform("android")]
    internal static class AndroidTextInput
    {
        /// <summary>Raised on the UI thread when the input method changes the text.</summary>
        public static event Action<AndroidImeUpdate> ImeUpdate;

        /// <summary>True once a host is attached that can raise the soft keyboard.</summary>
        internal static bool IsAvailable => OperatingSystem.IsAndroid() && AndroidWindow.Host != null;

        /// <summary>True while a text field has focus and the keyboard has been asked for.</summary>
        internal static bool IsEnabled { get; private set; }

        /// <summary>
        /// True while the input method holds an unfinished composition. Android tells us directly:
        /// setComposingText starts one, commitText and finishComposingText end it.
        /// </summary>
        internal static bool IsComposing { get; private set; }

        // The view holding the keyboard, so it can be dismissed even if its window is torn down.
        private static IntPtr _keyboardTarget;

        /// <summary>Raises the soft keyboard over the view backing the focused window.</summary>
        public static void Enable(IntPtr handle, bool multiline, bool password)
        {
            if (!IsAvailable || handle == IntPtr.Zero) return;

            AndroidWindow.Host.ShowSoftKeyboard(handle, multiline, password);
            _keyboardTarget = handle;
            IsEnabled = true;
        }

        /// <summary>Dismisses the soft keyboard.</summary>
        public static void Disable()
        {
            if (!IsAvailable || !IsEnabled) return;

            AndroidWindow.Host.HideSoftKeyboard(_keyboardTarget);
            _keyboardTarget = IntPtr.Zero;
            IsEnabled = false;
            IsComposing = false;
        }

        /// <summary>
        /// Where the caret is, in device pixels relative to the client area. Android input methods
        /// use it to keep their candidate strip clear of the text being typed.
        /// </summary>
        public static void SetCursorRectangle(int x, int y, int width, int height)
        {
            if (!IsAvailable || !IsEnabled) return;
            AndroidWindow.Host.SetImeCursorRect(_keyboardTarget, x, y, Math.Max(1, width), Math.Max(1, height));
        }

        // ---- entry points, called by the host's InputConnection --------------------

        /// <summary>The composition changed (InputConnection.setComposingText).</summary>
        internal static void NotifyComposingText(string text, int cursorPosition)
        {
            IsComposing = !string.IsNullOrEmpty(text);
            Raise(new AndroidImeUpdate(null, string.IsNullOrEmpty(text) ? null : text, cursorPosition, 0, 0));
        }

        /// <summary>The input method committed text (InputConnection.commitText).</summary>
        internal static void NotifyCommitText(string text, int cursorPosition)
        {
            IsComposing = false;
            Raise(new AndroidImeUpdate(string.IsNullOrEmpty(text) ? null : text, null, cursorPosition, 0, 0));
        }

        /// <summary>The composition ended without a commit (InputConnection.finishComposingText).</summary>
        internal static void NotifyFinishComposing()
        {
            if (!IsComposing) return;
            IsComposing = false;
            Raise(new AndroidImeUpdate(null, null, -1, 0, 0));
        }

        /// <summary>The input method asked for text around the caret to be removed.</summary>
        internal static void NotifyDeleteSurrounding(int before, int after)
        {
            if (before <= 0 && after <= 0) return;
            Raise(new AndroidImeUpdate(null, null, -1, before, after));
        }

        private static void Raise(AndroidImeUpdate update)
        {
            try { ImeUpdate?.Invoke(update); }
            catch (InvalidOperationException) { }   // the editor went away mid-composition
        }
    }
}
