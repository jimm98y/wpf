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
        /// <summary>FileDialog.ShowHiddenFiles: hidden and system files shown (OFN_FORCESHOWHIDDEN).</summary>
        public bool ShowHiddenFiles;
        /// <summary>FileDialog.AddToRecent: what is chosen goes on the recent list (not OFN_DONTADDTORECENT).</summary>
        public bool AddToRecent = true;
        /// <summary>FileDialog.ClientGuid: the dialog's own remembered state (IFileDialog::SetClientGuid).</summary>
        public Guid? ClientGuid;
    }

    /// <summary>What a folder browser is asking for, in FolderBrowserDialog's terms.</summary>
    internal sealed class FolderDialogRequest
    {
        public string Description;
        public bool UseDescriptionForTitle;
        public string InitialDirectory;
        public string SelectedPath;
        /// <summary>FolderBrowserDialog.Multiselect: several folders may be chosen (FOS_ALLOWMULTISELECT).</summary>
        public bool Multiselect;
        public bool ShowHiddenFiles;
        public bool AddToRecent = true;
        /// <summary>FolderBrowserDialog.OkRequiresInteraction: OK disabled until the user navigates or edits.</summary>
        public bool OkRequiresInteraction;
        /// <summary>FolderBrowserDialog.ShowPinnedPlaces: the navigation pane's pinned places shown.</summary>
        public bool ShowPinnedPlaces = true;
        public Guid? ClientGuid;
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

        bool ShowFolder(FolderDialogRequest request, out string selectedPath);

        // The asynchronous forms, the only ones the browser, iOS and Android can serve: their run
        // loops cannot be re-entered, so a picker there answers later rather than from the call.
        // A bridge whose platform CAN block inherits these, which simply run the call.
        System.Threading.Tasks.Task<(bool ok, string[] fileNames, int filterIndex)> ShowOpenAsync(FileDialogRequest request)
        {
            bool ok = ShowOpen(request, out string[] names, out int index);
            return System.Threading.Tasks.Task.FromResult((ok, names, index));
        }

        System.Threading.Tasks.Task<(bool ok, string[] fileNames, int filterIndex)> ShowSaveAsync(FileDialogRequest request)
        {
            bool ok = ShowSave(request, out string[] names, out int index);
            return System.Threading.Tasks.Task.FromResult((ok, names, index));
        }

        System.Threading.Tasks.Task<(bool ok, string selectedPath)> ShowFolderAsync(FolderDialogRequest request)
        {
            bool ok = ShowFolder(request, out string path);
            return System.Threading.Tasks.Task.FromResult((ok, path));
        }

        /// <summary>The folders chosen, several where the request allows it (FolderBrowserDialog.Multiselect); a
        /// bridge whose picker takes one folder answers with that one.</summary>
        bool ShowFolders(FolderDialogRequest request, out string[] selectedPaths)
        {
            bool ok = ShowFolder(request, out string path);
            selectedPaths = ok ? new[] { path } : null;
            return ok;
        }

        async System.Threading.Tasks.Task<(bool ok, string[] selectedPaths)> ShowFoldersAsync(FolderDialogRequest request)
        {
            var (ok, path) = await ShowFolderAsync(request);
            return (ok, ok ? new[] { path } : null);
        }

        /// <summary>SaveFileDialog.CommitAsync: hand a file the application has written to the
        /// path a save dialog returned over to the user. Nothing to do where the user chose a real
        /// destination up front (every desktop); in the browser it is the download, on iOS and
        /// Android the export -- see WPF's SaveFileDialog.CommitAsync, which the WPF-hosted bridge
        /// delegates to.</summary>
        System.Threading.Tasks.Task<bool> CommitSaveAsync(string path)
            => System.Threading.Tasks.Task.FromResult(!string.IsNullOrEmpty(path));

        /// <summary>Whether ShowFolder is worth calling at all.
        /// <para>Needed because "the user cancelled" and "this bridge has no folder browser" are both
        /// false from ShowFolder, and they must not lead to the same place: a bridge that does files
        /// and not folders would otherwise make every FolderBrowserDialog return Cancel without ever
        /// showing anything. The managed tree is the fallback and it has to stay reachable.</para>
        /// </summary>
        bool SupportsFolder { get; }
    }

    internal partial class XplatUIWebGpu : XplatUIDriver
    {
        /// <summary>The platform file dialogs, or null when there are none to call.
        /// <para>A host that brings its own still wins -- the WPF integration layer installs one so a
        /// mixed application shows ONE browser rather than two. With nothing installed the platform's
        /// own is used where there is one, so a plain WinForms application on Windows gets the dialog
        /// Windows shows every other program rather than Mono's 2000-era stand-in. Where there is no
        /// common dialog to call (X11, the headless tests) this stays null and the managed dialog
        /// runs exactly as before.</para></summary>
        internal static IFileDialogBridge FileDialogBridge
        {
            get => s_fileDialogBridge ?? Win32FileDialogBridge.Default ?? MacFileDialogBridge.Default ?? BrowserDefault;
            set => s_fileDialogBridge = value;
        }

        private static IFileDialogBridge s_fileDialogBridge;

        // the browser's pickers (BrowserFileDialogBridge), on the head that has them
        private static IFileDialogBridge BrowserDefault => OperatingSystem.IsBrowser() ? BrowserFileDialogBridge.Default : null;
    }
}
