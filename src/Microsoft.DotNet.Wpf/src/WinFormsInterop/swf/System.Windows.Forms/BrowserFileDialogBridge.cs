// The file and folder pickers the browser itself shows, for WinForms on the browser head: the page's
// File System Access pickers where it has them (an <input type=file> and a download where it does not),
// through the shared BrowserDialogs that WPF's Microsoft.Win32 dialogs use there too. Without this a
// WinForms app in a page browsed with Mono's Windows-2000 dialog -- app's Open showed it, and
// hung as soon as its places bar was asked for the Desktop.
//
// ShowDialog waits for the picker, as it does for any modal dialog on the multithreaded runtime (see
// BrowserLoops); the picked file is in the page's file system at the path answered, and a saved one is
// sent on to the file the user chose once the app has written it.

extern alias WindowsBase;

using System.IO;
using System.Threading.Tasks;
using BrowserDialogs = WindowsBase::MS.Internal.Interop.BrowserDialogs;
using BrowserBlocking = WindowsBase::MS.Internal.Interop.BrowserBlocking;

namespace System.Windows.Forms
{
	[System.Runtime.Versioning.SupportedOSPlatform ("browser")]
	internal sealed class BrowserFileDialogBridge : IFileDialogBridge
	{
		internal static readonly IFileDialogBridge Default =
			OperatingSystem.IsBrowser () && BrowserBlocking.CanBlock ? new BrowserFileDialogBridge () : null;

		public bool SupportsFolder => true;

		private static string Suggested (FileDialogRequest request)
		{
			string name = string.IsNullOrEmpty (request.FileName) ? null : Path.GetFileName (request.FileName);
			if (!string.IsNullOrEmpty (name) && request.AddExtension && !string.IsNullOrEmpty (request.DefaultExt) && !Path.HasExtension (name))
				name += "." + request.DefaultExt.TrimStart ('.');
			return name;
		}

		// A saved name is the picker's, extension included (it adds the chosen type's): the path answered
		// is the one linked to the user's file, so it is not changed here.
		private static string WithExtension (FileDialogRequest request, string chosen) => chosen;

		public bool ShowOpen (FileDialogRequest request, out string [] fileNames, out int filterIndex)
		{
			filterIndex = request.FilterIndex;
			fileNames = BrowserDialogs.ShowOpenPanel (request.Multiselect, directory: false, request.Filter);
			return fileNames != null && fileNames.Length > 0;
		}

		public bool ShowSave (FileDialogRequest request, out string [] fileNames, out int filterIndex)
		{
			filterIndex = request.FilterIndex;
			string chosen = WithExtension (request, BrowserDialogs.ShowSavePanel (Suggested (request), request.Filter));
			fileNames = chosen == null ? null : new [] { chosen };
			return chosen != null;
		}

		public bool ShowFolder (FolderDialogRequest request, out string selectedPath)
		{
			string [] chosen = BrowserDialogs.ShowOpenPanel (false, directory: true, null);
			selectedPath = chosen != null && chosen.Length > 0 ? chosen [0] : null;
			return selectedPath != null;
		}

		public async Task<(bool ok, string [] fileNames, int filterIndex)> ShowOpenAsync (FileDialogRequest request)
		{
			string [] names = await BrowserDialogs.ShowOpenPanelAsync (null, request.Multiselect, false, request.Filter);
			return (names != null && names.Length > 0, names, request.FilterIndex);
		}

		public async Task<(bool ok, string [] fileNames, int filterIndex)> ShowSaveAsync (FileDialogRequest request)
		{
			string chosen = WithExtension (request, await BrowserDialogs.ShowSavePanelAsync (Suggested (request), request.Filter));
			return (chosen != null, chosen == null ? null : new [] { chosen }, request.FilterIndex);
		}

		public async Task<(bool ok, string selectedPath)> ShowFolderAsync (FolderDialogRequest request)
		{
			string [] chosen = await BrowserDialogs.ShowOpenPanelAsync (null, false, true, null);
			string path = chosen != null && chosen.Length > 0 ? chosen [0] : null;
			return (path != null, path);
		}

		// the page sends a saved file on by itself once it has been written
		public Task<bool> CommitSaveAsync (string path) => Task.FromResult (!string.IsNullOrEmpty (path));
	}
}
