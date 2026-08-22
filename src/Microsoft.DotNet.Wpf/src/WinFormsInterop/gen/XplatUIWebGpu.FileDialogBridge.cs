// Browsing for a file or a folder, handed to the platform.
//
// Mono ships its own managed file dialog -- a Form with a places bar, a tree and a file view -- for
// the good reason that X11 has no common dialog to call. Windows does, and WPF already calls it
// (Microsoft.Win32.OpenFileDialog and friends drive the Vista-era IFileDialog). An application that
// mixes the two toolkits showing two completely different browsers, one of them a decade behind and
// unable to reach shell namespaces at all, is the kind of seam this stack exists to remove.
//
// Same shape as the clipboard bridge next door: System.Windows.Forms cannot reference
// PresentationFramework, so the integration layer installs an implementation and the dialogs use it
// when it is there. With no bridge -- a plain WinForms app on X11, the headless tests -- the managed
// dialog runs exactly as before.

namespace System.Windows.Forms
{
    /// <summary>What a file dialog is asking for, in the terms WinForms states them.</summary>
    internal sealed class FileDialogRequest
    {
        public string Title;
        /// <summary>WinForms filter syntax: "Text|*.txt|All|*.*". WPF uses the same.</summary>
        public string Filter;
        /// <summary>1-based, as WinForms counts it.</summary>
        public int FilterIndex = 1;
        public string InitialDirectory;
        public string FileName;
        public string DefaultExt;
        public bool AddExtension = true;
        public bool CheckFileExists;
        public bool Multiselect;
        public bool OverwritePrompt;
    }

    /// <summary>
    /// The platform's file and folder browsers. Installed by the integration layer, which can reach
    /// WPF's Microsoft.Win32 dialogs; null when nothing is hosting, and then Mono's own managed
    /// dialog is used.
    /// </summary>
    internal interface IFileDialogBridge
    {
        /// <summary>False if the user cancelled; <paramref name="fileNames"/> is untouched then.</summary>
        bool ShowOpen(FileDialogRequest request, out string[] fileNames, out int filterIndex);

        bool ShowSave(FileDialogRequest request, out string[] fileNames, out int filterIndex);

        bool ShowFolder(string description, string initialPath, out string selectedPath);
    }

    internal partial class XplatUIWebGpu : XplatUIDriver
    {
        /// <summary>The platform file dialogs, or null when nothing installed them.</summary>
        internal static IFileDialogBridge FileDialogBridge;
    }
}
