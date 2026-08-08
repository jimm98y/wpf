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

        /// <summary>Reports the text around the caret to whichever backend is live.</summary>
        private void ReportSurroundingTextToInputMethod()
        {
            ReportSurroundingTextToLinuxInputMethod();
            ReportSurroundingTextToMacInputMethod();
        }
    }
}
