// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// What .NET's common dialogs have that Mono's did not, ported from .NET's FileDialog, OpenFileDialog, SaveFileDialog and
// FolderBrowserDialog: the shell dialog's options (the recent list, hidden items, the client GUID its state is kept
// under, OK needing interaction, the pinned places, the preview pane, the expanded save dialog), a folder browser
// that picks several folders and starts in a folder of its own, and the read-only and write-access checks. Each goes
// to the platform's dialog as far as it has the option (IFileDialogBridge's requests).

using System.ComponentModel;

namespace System.Windows.Forms
{
	public abstract partial class FileDialog
	{
		private bool _addToRecent = true;

		/// <summary>Whether the file chosen goes on the recent list (not OFN_DONTADDTORECENT).</summary>
		[MWFCategory ("Behavior")]
		[DefaultValue (true)]
		public bool AddToRecent {
			get => _addToRecent;
			set => _addToRecent = value;
		}

		/// <summary>The GUID the dialog keeps its state under (folder, position, size); null for the application's.</summary>
		[Localizable (false)]
		[Browsable (false)]
		[DesignerSerializationVisibility (DesignerSerializationVisibility.Hidden)]
		public Guid? ClientGuid { get; set; }

		/// <summary>Whether hidden and system files are shown, whatever the user's setting (OFN_FORCESHOWHIDDEN).</summary>
		[MWFCategory ("Behavior")]
		[DefaultValue (false)]
		public bool ShowHiddenFiles { get; set; }

		/// <summary>Whether OK stays disabled until the user navigates or edits the file name.</summary>
		[MWFCategory ("Behavior")]
		[DefaultValue (false)]
		public bool OkRequiresInteraction { get; set; }

		/// <summary>Whether the navigation pane shows its pinned places.</summary>
		[MWFCategory ("Behavior")]
		[DefaultValue (true)]
		public bool ShowPinnedPlaces { get; set; } = true;

		private void ResetModernOptions ()
		{
			_addToRecent = true;
			ClientGuid = null;
			ShowHiddenFiles = false;
			OkRequiresInteraction = false;
			ShowPinnedPlaces = true;
		}
	}

	public sealed partial class OpenFileDialog
	{
		/// <summary>Whether a read-only file may be chosen (not OFN_NOREADONLYRETURN).</summary>
		[DefaultValue (true)]
		public bool SelectReadOnly { get; set; } = true;

		/// <summary>Whether the preview pane is shown.</summary>
		[DefaultValue (false)]
		public bool ShowPreview { get; set; }
	}

	public sealed partial class SaveFileDialog
	{
		/// <summary>Whether the dialog checks that the file can be created (not OFN_NOTESTFILECREATE).</summary>
		[DefaultValue (true)]
		public bool CheckWriteAccess { get; set; } = true;

		/// <summary>Whether the dialog opens expanded, showing the folders (FOS_DEFAULTNOMINIMODE).</summary>
		[DefaultValue (true)]
		public bool ExpandedMode { get; set; } = true;
	}

	public sealed partial class FolderBrowserDialog
	{
		private string [] _selectedPaths = Array.Empty<string> ();
		private string _initialDirectory = string.Empty;
		private bool _addToRecent = true, _showPinnedPlaces = true;

		/// <summary>Whether the folder chosen goes on the recent list.</summary>
		[DefaultValue (true)]
		public bool AddToRecent {
			get => _addToRecent;
			set => _addToRecent = value;
		}

		/// <summary>Whether the modern folder picker is used (always, here: the platform's, or the managed tree
		/// where there is none).</summary>
		[DefaultValue (true)]
		public bool AutoUpgradeEnabled { get; set; } = true;

		/// <summary>Whether several folders may be chosen (<see cref="SelectedPaths"/>).</summary>
		[DefaultValue (false)]
		public bool Multiselect { get; set; }

		/// <summary>Whether OK stays disabled until the user navigates or edits.</summary>
		[DefaultValue (false)]
		public bool OkRequiresInteraction { get; set; }

		/// <summary>Whether hidden and system folders are shown.</summary>
		[DefaultValue (false)]
		public bool ShowHiddenFiles { get; set; }

		/// <summary>Whether the navigation pane shows its pinned places.</summary>
		[DefaultValue (true)]
		public bool ShowPinnedPlaces {
			get => _showPinnedPlaces;
			set => _showPinnedPlaces = value;
		}

		/// <summary>The GUID the dialog keeps its state under.</summary>
		[Localizable (false)]
		[Browsable (false)]
		[DesignerSerializationVisibility (DesignerSerializationVisibility.Hidden)]
		public Guid? ClientGuid { get; set; }

		/// <summary>The folders chosen: one, or several with <see cref="Multiselect"/>.</summary>
		[Browsable (false)]
		[DesignerSerializationVisibility (DesignerSerializationVisibility.Hidden)]
		public string [] SelectedPaths => _selectedPaths.Length > 0 ? (string []) _selectedPaths.Clone () : Array.Empty<string> ();

		/// <summary>The folder the dialog starts in, where <see cref="SelectedPath"/> does not say.</summary>
		[DefaultValue ("")]
		public string InitialDirectory {
			get => _initialDirectory;
			set => _initialDirectory = value ?? string.Empty;
		}

		private void Chosen (string [] paths)
		{
			_selectedPaths = paths ?? Array.Empty<string> ();
			selectedPath = _selectedPaths.Length > 0 ? _selectedPaths [0] : string.Empty;
		}

		private void ResetModernOptions ()
		{
			_selectedPaths = Array.Empty<string> ();
			_initialDirectory = string.Empty;
			_addToRecent = true;
			_showPinnedPlaces = true;
			Multiselect = false;
			OkRequiresInteraction = false;
			ShowHiddenFiles = false;
			ClientGuid = null;
		}
	}
}
