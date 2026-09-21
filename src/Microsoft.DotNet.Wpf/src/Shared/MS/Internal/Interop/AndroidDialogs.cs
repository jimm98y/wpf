// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Android file dialogs, reached through the head payload the same way printing, accessibility and
// the clipboard are.
//
// Before this file the Android head had no file dialog: CommonItemDialog fell through to
// "return false", so OpenFileDialog.ShowDialog reported a cancellation of a dialog nobody saw.
//
// Android has no file dialog in the sense the other heads mean. The Storage Access Framework is an
// ACTIVITY the app starts and whose answer arrives in onActivityResult, so there is no call that
// blocks and returns a path -- which is why these are Tasks and why CommonDialog needed
// ShowDialogAsync. Two consequences shape the seam:
//
//   * The head has to forward Activity.OnActivityResult, because only the Activity receives it.
//     That is one override in each app's MainActivity, and without it every pick hangs for ever
//     rather than failing, so the payload's implementation completes any outstanding request when
//     the activity is destroyed.
//
//   * A SAF result is a content:// URI, not a path. Nothing in .NET can open one, so the payload
//     copies the bytes into the app's own cache directory and returns THAT path -- the same shape
//     the iOS head uses for its security-scoped URLs and the browser head for its virtual file
//     system, and the reason application code can keep using File.ReadAllBytes on FileName.
//

using System;
using System.Threading.Tasks;

namespace MS.Internal.Interop
{
    /// <summary>
    ///  The file-dialog operations the Android head payload supplies. Implemented by AndroidHost.
    /// </summary>
    public interface IAndroidDialogHost
    {
        /// <summary>
        ///  Starts the Storage Access Framework picker and completes with local paths the app can
        ///  read, or an empty array when the user backed out.
        /// </summary>
        /// <param name="mimeTypes">MIME types to offer, or null for every file.</param>
        Task<string[]> PickFilesAsync(string[] mimeTypes, bool multiple, bool directory);

        /// <summary>
        ///  Starts the "create document" picker for a file the application has already written at
        ///  <paramref name="path"/>, copying it to wherever the user chooses. Completes with true
        ///  when the export happened.
        /// </summary>
        Task<bool> ExportFileAsync(string path, string mimeType);

        /// <summary>A path in the app's cache directory, for the write that precedes an export.</summary>
        string ReserveSavePath(string suggestedName);
    }

    /// <summary>
    /// The file-dialog backend for Android. Public for the head payload; everything it is handed is
    /// a primitive, so the payload never has to see a WindowsBase-internal type.
    /// </summary>
    // No [SupportedOSPlatform], matching AndroidWindow, AndroidPrint and AndroidClipboard next door:
    // the public WindowsBase surface carries no platform attributes, and adding one fails ApiCompat
    // against the hand-written reference assembly.
    public static class AndroidDialogs
    {
        /// <summary>Set by the head alongside AndroidWindow.Host.</summary>
        public static IAndroidDialogHost Host { get; set; }

        /// <summary>True once a head is attached that can start the picker.</summary>
        public static bool IsAvailable => OperatingSystem.IsAndroid() && Host is not null;

        internal static Task<string[]> ShowOpenPanelAsync(string[] mimeTypes, bool multiple, bool directory)
            => IsAvailable
                ? Host.PickFilesAsync(mimeTypes, multiple, directory)
                : Task.FromResult(Array.Empty<string>());

        internal static Task<bool> ExportFileAsync(string path, string mimeType)
            => IsAvailable ? Host.ExportFileAsync(path, mimeType) : Task.FromResult(false);

        internal static string ReserveSavePath(string suggestedName)
            => IsAvailable ? Host.ReserveSavePath(suggestedName) : null;
    }
}
