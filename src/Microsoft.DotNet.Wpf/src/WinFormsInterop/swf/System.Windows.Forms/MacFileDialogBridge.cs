// The file and folder browsers macOS itself shows, for a standalone WinForms app on the Cocoa
// head: NSOpenPanel and NSSavePanel through the shared CocoaDialogs, the same panels WPF's
// Microsoft.Win32 dialogs open there. Without this a WinForms app on macOS browsed with Mono's
// Windows-2000 dialog.

using System;
using System.IO;

namespace MS.Internal.Interop
{
    internal static partial class CocoaDialogs
    {
        // The Cocoa host has made the shared NSApplication before any form is on screen, and a
        // dialog is only ever opened from one.
        private static partial void EnsureApplication () { }
    }
}

namespace System.Windows.Forms
{
    internal sealed class MacFileDialogBridge : IFileDialogBridge
    {
        internal static readonly IFileDialogBridge Default =
            OperatingSystem.IsMacOS () ? new MacFileDialogBridge () : null;

        public bool SupportsFolder => true;

        public bool ShowOpen (FileDialogRequest request, out string [] fileNames, out int filterIndex)
        {
            filterIndex = request.FilterIndex;
            fileNames = MS.Internal.Interop.CocoaDialogs.ShowOpenPanel (request.Title, request.InitialDirectory,
                request.Multiselect, chooseDirectories: false);
            return fileNames != null && fileNames.Length > 0;
        }

        public bool ShowSave (FileDialogRequest request, out string [] fileNames, out int filterIndex)
        {
            filterIndex = request.FilterIndex;
            fileNames = null;
            string suggested = string.IsNullOrEmpty (request.FileName) ? null : Path.GetFileName (request.FileName);
            string dir = request.InitialDirectory;
            if (string.IsNullOrEmpty (dir) && !string.IsNullOrEmpty (request.FileName))
                dir = Path.GetDirectoryName (request.FileName);
            string chosen = MS.Internal.Interop.CocoaDialogs.ShowSavePanel (request.Title, dir, suggested);
            if (chosen == null)
                return false;
            // NSSavePanel asks about replacing a file itself; it adds no extension, so WinForms'
            // AddExtension is applied here as FileDialog does for its own dialog.
            if (request.AddExtension && !string.IsNullOrEmpty (request.DefaultExt) && !Path.HasExtension (chosen))
                chosen += "." + request.DefaultExt.TrimStart ('.');
            fileNames = new [] { chosen };
            return true;
        }

        public bool ShowFolder (FolderDialogRequest request, out string selectedPath)
        {
            string start = request.InitialDirectory;
            if (string.IsNullOrEmpty (start) && !string.IsNullOrEmpty (request.SelectedPath))
                start = request.SelectedPath;
            string [] chosen = MS.Internal.Interop.CocoaDialogs.ShowOpenPanel (request.Description, start, false, chooseDirectories: true);
            selectedPath = chosen != null && chosen.Length > 0 ? chosen [0] : null;
            return selectedPath != null;
        }
    }
}
