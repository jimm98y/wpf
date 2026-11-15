// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// A task dialog's page, ported from .NET's TaskDialogPage: what the dialog shows -- caption, heading, text, icon, the
// controls -- and the events it raises (Created, Destroyed, HelpRequest, LinkClicked). Bound while shown: its controls
// cannot be swapped then, its texts and icon change in place, and Navigate shows another page in the same dialog.

using System.Collections.Generic;

namespace System.Windows.Forms
{
	public class TaskDialogPage
	{
		private TaskDialogButtonCollection _buttons = new TaskDialogButtonCollection ();
		private TaskDialogRadioButtonCollection _radioButtons = new TaskDialogRadioButtonCollection ();
		private TaskDialogVerificationCheckBox _checkBox = new TaskDialogVerificationCheckBox ();
		private TaskDialogExpander _expander = new TaskDialogExpander ();
		private TaskDialogFootnote _footnote = new TaskDialogFootnote ();
		private TaskDialogProgressBar _progressBar = new TaskDialogProgressBar (TaskDialogProgressBarState.None);
		private TaskDialogIcon _icon;
		private string _caption, _heading, _text;
		private bool _allowCancel, _rightToLeftLayout, _allowMinimize, _sizeToContent, _enableLinks;

		public event EventHandler Created;
		public event EventHandler Destroyed;
		public event EventHandler HelpRequest;
		public event EventHandler<TaskDialogLinkClickedEventArgs> LinkClicked;

		public TaskDialogPage ()
		{
		}

		public TaskDialogButtonCollection Buttons {
			get => _buttons;
			set {
				DenyIfBound ();
				_buttons = value ?? throw new ArgumentNullException (nameof (value));
			}
		}

		/// <summary>The button focused when the page is shown; the first one where none is.</summary>
		public TaskDialogButton DefaultButton { get; set; }

		public TaskDialogRadioButtonCollection RadioButtons {
			get => _radioButtons;
			set {
				DenyIfBound ();
				_radioButtons = value ?? throw new ArgumentNullException (nameof (value));
			}
		}

		public TaskDialogVerificationCheckBox Verification {
			get => _checkBox;
			set {
				DenyIfBound ();
				_checkBox = value;
			}
		}

		public TaskDialogExpander Expander {
			get => _expander;
			set {
				DenyIfBound ();
				_expander = value;
			}
		}

		public TaskDialogFootnote Footnote {
			get => _footnote;
			set {
				DenyIfBound ();
				_footnote = value;
			}
		}

		public TaskDialogProgressBar ProgressBar {
			get => _progressBar;
			set {
				DenyIfBound ();
				_progressBar = value;
			}
		}

		/// <summary>The window's title; the application's name where it is empty.</summary>
		public string Caption {
			get => _caption;
			set {
				_caption = value;
				BoundDialog?.PageChanged (relayout: false);
			}
		}

		/// <summary>The main instruction, in large type above the text.</summary>
		public string Heading {
			get => _heading;
			set {
				_heading = value;
				BoundDialog?.PageChanged (relayout: true);
			}
		}

		public string Text {
			get => _text;
			set {
				_text = value;
				BoundDialog?.PageChanged (relayout: true);
			}
		}

		public TaskDialogIcon Icon {
			get => _icon;
			set {
				if (BoundDialog != null && (value?.IsHandleIcon ?? false) != (_icon?.IsHandleIcon ?? false))
					throw new InvalidOperationException ("The icon of a shown task dialog cannot change between a standard icon and an icon of the application's.");
				_icon = value;
				BoundDialog?.PageChanged (relayout: true);
			}
		}

		/// <summary>Whether Esc, Alt+F4 and the close box close the dialog (as Cancel) though it has no Cancel button.</summary>
		public bool AllowCancel {
			get => _allowCancel;
			set {
				DenyIfBound ();
				_allowCancel = value;
			}
		}

		public bool RightToLeftLayout {
			get => _rightToLeftLayout;
			set {
				DenyIfBound ();
				_rightToLeftLayout = value;
			}
		}

		public bool AllowMinimize {
			get => _allowMinimize;
			set {
				DenyIfBound ();
				_allowMinimize = value;
			}
		}

		/// <summary>Whether the dialog is as wide as its content needs, rather than comctl32's width for its text.</summary>
		public bool SizeToContent {
			get => _sizeToContent;
			set {
				DenyIfBound ();
				_sizeToContent = value;
			}
		}

		/// <summary>Whether &lt;a href="..."&gt;links&lt;/a&gt; in the text, the expander and the footnote are links
		/// (LinkClicked), rather than the markup as it is.</summary>
		public bool EnableLinks {
			get => _enableLinks;
			set {
				DenyIfBound ();
				_enableLinks = value;
			}
		}

		/// <summary>The dialog the page is shown in; null while it is not.</summary>
		public TaskDialog BoundDialog { get; private set; }

		/// <summary>Shows <paramref name="page"/> in the dialog this page is shown in, in place of this one.</summary>
		public void Navigate (TaskDialogPage page)
		{
			if (page == null)
				throw new ArgumentNullException (nameof (page));
			if (BoundDialog == null)
				throw new InvalidOperationException ("This page is not shown in a task dialog: there is nothing to navigate from.");
			BoundDialog.Navigate (page);
		}

		internal void DenyIfBound ()
		{
			if (BoundDialog != null)
				throw new InvalidOperationException ("This cannot be changed while the page is shown in a task dialog.");
		}

		/// <summary>The buttons shown in the bottom row (standard and custom) and as command links, in order.</summary>
		internal IEnumerable<TaskDialogButton> ShownButtons ()
		{
			bool any = false;
			foreach (TaskDialogButton button in _buttons) {
				if (button.IsCreated) {
					any = true;
					yield return button;
				}
			}
			// a page with no button has OK, as comctl32 gives it
			if (!any)
				yield return ImplicitOk ??= new TaskDialogButton (TaskDialogResult.OK);
		}

		internal TaskDialogButton ImplicitOk { get; private set; }

		internal bool CanBeCanceled =>
			_allowCancel || FindStandard (TaskDialogResult.Cancel) != null;

		internal TaskDialogButton FindStandard (TaskDialogResult result)
		{
			foreach (TaskDialogButton button in _buttons)
				if (button.IsStandardButton && button.StandardButtonResult == result)
					return button;
			return null;
		}

		internal void Bind (TaskDialog dialog)
		{
			if (BoundDialog != null)
				throw new InvalidOperationException ("This page is already shown in a task dialog.");
			// one checked radio button at most
			int checkedCount = 0;
			foreach (TaskDialogRadioButton radio in _radioButtons)
				if (radio.Checked && ++checkedCount > 1)
					throw new InvalidOperationException ("Only one radio button can be checked.");
			if (DefaultButton != null && !_buttons.Contains (DefaultButton) && !DefaultButton.IsStandardButton)
				throw new InvalidOperationException ("The default button is not one of the page's buttons.");

			BoundDialog = dialog;
			_buttons.BoundPage = this;
			_radioButtons.BoundPage = this;
			foreach (TaskDialogButton button in _buttons)
				button.Bind (this);
			foreach (TaskDialogRadioButton radio in _radioButtons)
				radio.Bind (this);
			_checkBox?.Bind (this);
			_expander?.Bind (this);
			_footnote?.Bind (this);
			_progressBar?.Bind (this);
		}

		internal void Unbind ()
		{
			foreach (TaskDialogButton button in _buttons)
				button.Unbind ();
			foreach (TaskDialogRadioButton radio in _radioButtons)
				radio.Unbind ();
			_checkBox?.Unbind ();
			_expander?.Unbind ();
			_footnote?.Unbind ();
			_progressBar?.Unbind ();
			_buttons.BoundPage = null;
			_radioButtons.BoundPage = null;
			ImplicitOk = null;
			BoundDialog = null;
		}

		protected internal void OnCreated (EventArgs e) => Created?.Invoke (this, e);

		protected internal void OnDestroyed (EventArgs e) => Destroyed?.Invoke (this, e);

		protected internal void OnHelpRequest (EventArgs e) => HelpRequest?.Invoke (this, e);

		protected internal void OnLinkClicked (TaskDialogLinkClickedEventArgs e) => LinkClicked?.Invoke (this, e);
	}
}
