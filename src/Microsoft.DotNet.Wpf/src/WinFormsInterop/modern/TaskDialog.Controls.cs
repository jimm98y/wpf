// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// The parts of a task dialog, ported from .NET's TaskDialog* classes (Dialogs/TaskDialog): buttons (standard and
// custom, command links), radio buttons, the verification check box, the expander, the footnote, the progress bar and
// the icons. Their rules are .NET's -- a control belongs to one page; what shapes the dialog cannot change while the
// page is shown (DenyIfBound), what comctl32 can change while it is shown can (Enabled, Checked, a progress bar's
// value and state, the texts). Shown, a change goes to the dialog that shows them (TaskDialog.Managed.cs), as .NET's
// sends comctl32 a TDM_* message.

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Drawing;

namespace System.Windows.Forms
{
	/// <summary>A standard button's result: its MessageBox result code (IDOK ...).</summary>
	internal enum TaskDialogResult
	{
		None = 0,
		OK = 1,
		Cancel = 2,
		Abort = 3,
		Retry = 4,
		Ignore = 5,
		Yes = 6,
		No = 7,
		Close = 8,
		Help = 9,
		TryAgain = 10,
		Continue = 11,
	}

	internal enum TaskDialogStandardIcon
	{
		None = 0,
		Information = ushort.MaxValue - 2,
		Warning = ushort.MaxValue,
		Error = ushort.MaxValue - 1,
		Shield = ushort.MaxValue - 3,
		ShieldBlueBar = ushort.MaxValue - 4,
		ShieldGrayBar = ushort.MaxValue - 8,
		ShieldWarningYellowBar = ushort.MaxValue - 5,
		ShieldErrorRedBar = ushort.MaxValue - 6,
		ShieldSuccessGreenBar = ushort.MaxValue - 7,
	}

	public enum TaskDialogStartupLocation
	{
		CenterScreen = 0,
		CenterOwner = 1,
	}

	public enum TaskDialogExpanderPosition
	{
		AfterText = 0,
		AfterFootnote = 1,
	}

	public enum TaskDialogProgressBarState
	{
		Normal = 0,
		Paused = 1,
		Error = 2,
		Marquee = 3,
		MarqueePaused = 4,
		None = 5,
	}

	public class TaskDialogLinkClickedEventArgs : EventArgs
	{
		public TaskDialogLinkClickedEventArgs (string linkHref) => LinkHref = linkHref ?? throw new ArgumentNullException (nameof (linkHref));

		public string LinkHref { get; }
	}

	/// <summary>A task dialog's icon: a standard one, or an icon or bitmap of the application's.</summary>
	public class TaskDialogIcon : IDisposable
	{
		public static readonly TaskDialogIcon None = new TaskDialogIcon (TaskDialogStandardIcon.None);
		public static readonly TaskDialogIcon Information = new TaskDialogIcon (TaskDialogStandardIcon.Information);
		public static readonly TaskDialogIcon Warning = new TaskDialogIcon (TaskDialogStandardIcon.Warning);
		public static readonly TaskDialogIcon Error = new TaskDialogIcon (TaskDialogStandardIcon.Error);
		public static readonly TaskDialogIcon Shield = new TaskDialogIcon (TaskDialogStandardIcon.Shield);
		public static readonly TaskDialogIcon ShieldBlueBar = new TaskDialogIcon (TaskDialogStandardIcon.ShieldBlueBar);
		public static readonly TaskDialogIcon ShieldGrayBar = new TaskDialogIcon (TaskDialogStandardIcon.ShieldGrayBar);
		public static readonly TaskDialogIcon ShieldWarningYellowBar = new TaskDialogIcon (TaskDialogStandardIcon.ShieldWarningYellowBar);
		public static readonly TaskDialogIcon ShieldErrorRedBar = new TaskDialogIcon (TaskDialogStandardIcon.ShieldErrorRedBar);
		public static readonly TaskDialogIcon ShieldSuccessGreenBar = new TaskDialogIcon (TaskDialogStandardIcon.ShieldSuccessGreenBar);

		private readonly TaskDialogStandardIcon? _standardIcon;
		private readonly Icon _icon;
		private readonly Bitmap _bitmap;
		private readonly IntPtr? _iconHandle;

		public TaskDialogIcon (Bitmap image)
		{
			_bitmap = image ?? throw new ArgumentNullException (nameof (image));
			_iconHandle = IntPtr.Zero;
		}

		public TaskDialogIcon (Icon icon)
		{
			_icon = icon ?? throw new ArgumentNullException (nameof (icon));
			_iconHandle = icon.Handle;
		}

		public TaskDialogIcon (IntPtr iconHandle)
		{
			_iconHandle = iconHandle;
			try {
				_icon = Icon.FromHandle (iconHandle);
			} catch (Exception) {
			}
		}

		private TaskDialogIcon (TaskDialogStandardIcon standardIcon) => _standardIcon = standardIcon;

		public void Dispose ()
		{
		}

		/// <summary>The icon's handle; a standard icon has none.</summary>
		public IntPtr IconHandle => _iconHandle ?? throw new InvalidOperationException ();

		internal bool IsStandardIcon => _standardIcon != null;

		internal TaskDialogStandardIcon StandardIcon => _standardIcon ?? throw new InvalidOperationException ();

		internal bool IsHandleIcon => _iconHandle != null;

		/// <summary>The image shown at <paramref name="size"/> pixels square; null for none.</summary>
		internal Image ImageAt (int size)
		{
			if (_bitmap != null)
				return _bitmap;
			if (_icon != null)
				return new Icon (_icon, size, size).ToBitmap ();
			Icon system = _standardIcon switch {
				TaskDialogStandardIcon.Information => SystemIcons.Information,
				TaskDialogStandardIcon.Warning => SystemIcons.Warning,
				TaskDialogStandardIcon.ShieldWarningYellowBar => SystemIcons.Shield,
				TaskDialogStandardIcon.Error => SystemIcons.Error,
				TaskDialogStandardIcon.Shield or TaskDialogStandardIcon.ShieldBlueBar or TaskDialogStandardIcon.ShieldGrayBar
					or TaskDialogStandardIcon.ShieldErrorRedBar or TaskDialogStandardIcon.ShieldSuccessGreenBar => SystemIcons.Shield,
				_ => null,
			};
			return system == null ? null : new Icon (system, size, size).ToBitmap ();
		}

		/// <summary>The colour of the bar a shield icon's heading is shown on; Empty for none.</summary>
		internal Color BarColor => _standardIcon switch {
			TaskDialogStandardIcon.ShieldBlueBar => Color.FromArgb (0, 76, 151),
			TaskDialogStandardIcon.ShieldGrayBar => Color.FromArgb (96, 96, 96),
			TaskDialogStandardIcon.ShieldWarningYellowBar => Color.FromArgb (246, 191, 39),
			TaskDialogStandardIcon.ShieldErrorRedBar => Color.FromArgb (181, 28, 28),
			TaskDialogStandardIcon.ShieldSuccessGreenBar => Color.FromArgb (48, 128, 48),
			_ => Color.Empty,
		};
	}

	/// <summary>A part of a task dialog page.</summary>
	public abstract class TaskDialogControl
	{
		private protected TaskDialogControl ()
		{
		}

		public object Tag { get; set; }

		/// <summary>The page the control is part of while it is shown; null otherwise.</summary>
		public TaskDialogPage BoundPage { get; private set; }

		internal virtual bool IsCreatable => true;

		internal bool IsCreated { get; private set; }

		internal void Bind (TaskDialogPage page)
		{
			BoundPage = page ?? throw new ArgumentNullException (nameof (page));
			IsCreated = IsCreatable;
		}

		internal void Unbind ()
		{
			IsCreated = false;
			BoundPage = null;
		}

		private protected void DenyIfBound () => BoundPage?.DenyIfBound ();

		private protected void DenyIfNotBound ()
		{
			if (BoundPage == null)
				throw new InvalidOperationException ("This control is not bound to a task dialog.");
		}

		private protected void DenyIfBoundAndNotCreated ()
		{
			if (BoundPage != null && !IsCreated)
				throw new InvalidOperationException ("This control has not been created in the task dialog.");
		}

		/// <summary>Shown: the dialog shows what changed.</summary>
		private protected void Changed (bool relayout = false)
		{
			if (IsCreated)
				BoundPage?.BoundDialog?.ControlChanged (this, relayout);
		}
	}

	public class TaskDialogButton : TaskDialogControl
	{
		private readonly TaskDialogResult? _standardButtonResult;
		private bool _enabled = true, _showShieldIcon, _visible = true;
		private string _text;

		public event EventHandler Click;

		public TaskDialogButton ()
		{
		}

		public TaskDialogButton (string text, bool enabled = true, bool allowCloseDialog = true)
		{
			_text = text;
			_enabled = enabled;
			AllowCloseDialog = allowCloseDialog;
		}

		internal TaskDialogButton (TaskDialogResult standardButtonResult)
		{
			_standardButtonResult = standardButtonResult;
			_text = standardButtonResult.ToString ();
		}

		public static TaskDialogButton OK => new TaskDialogButton (TaskDialogResult.OK);
		public static TaskDialogButton Cancel => new TaskDialogButton (TaskDialogResult.Cancel);
		public static TaskDialogButton Abort => new TaskDialogButton (TaskDialogResult.Abort);
		public static TaskDialogButton Retry => new TaskDialogButton (TaskDialogResult.Retry);
		public static TaskDialogButton Ignore => new TaskDialogButton (TaskDialogResult.Ignore);
		public static TaskDialogButton Yes => new TaskDialogButton (TaskDialogResult.Yes);
		public static TaskDialogButton No => new TaskDialogButton (TaskDialogResult.No);
		public static TaskDialogButton Close => new TaskDialogButton (TaskDialogResult.Close);
		public static TaskDialogButton Help => new TaskDialogButton (TaskDialogResult.Help);
		public static TaskDialogButton TryAgain => new TaskDialogButton (TaskDialogResult.TryAgain);
		public static TaskDialogButton Continue => new TaskDialogButton (TaskDialogResult.Continue);

		/// <summary>Whether clicking the button closes the dialog (after its Click).</summary>
		public bool AllowCloseDialog { get; set; } = true;

		public bool Enabled {
			get => _enabled;
			set {
				_enabled = value;
				Changed ();
			}
		}

		/// <summary>Whether the button shows the shield (the action needs elevation).</summary>
		public bool ShowShieldIcon {
			get => _showShieldIcon;
			set {
				_showShieldIcon = value;
				Changed ();
			}
		}

		public bool Visible {
			get => _visible;
			set {
				DenyIfBound ();
				_visible = value;
			}
		}

		public string Text {
			get => _text;
			set {
				if (IsStandardButton)
					throw new InvalidOperationException ("The text of a standard button cannot be set.");
				DenyIfBound ();
				_text = value;
			}
		}

		internal override bool IsCreatable => base.IsCreatable && _visible;

		internal bool IsStandardButton => _standardButtonResult != null;

		internal TaskDialogResult StandardButtonResult => _standardButtonResult ?? throw new InvalidOperationException ();

		internal TaskDialogButtonCollection Collection { get; set; }

		/// <summary>The text a standard button shows: comctl32's, with its access key.</summary>
		internal string ShownText => !IsStandardButton ? (_text ?? string.Empty) : StandardButtonResult switch {
			TaskDialogResult.OK => "OK",
			TaskDialogResult.Cancel => "Cancel",
			TaskDialogResult.Abort => "&Abort",
			TaskDialogResult.Retry => "&Retry",
			TaskDialogResult.Ignore => "&Ignore",
			TaskDialogResult.Yes => "&Yes",
			TaskDialogResult.No => "&No",
			TaskDialogResult.Close => "&Close",
			TaskDialogResult.Help => "Help",
			TaskDialogResult.TryAgain => "&Try Again",
			TaskDialogResult.Continue => "&Continue",
			_ => string.Empty,
		};

		/// <summary>Clicks the button as the user would: Click, then the dialog closes if the button allows it.</summary>
		public void PerformClick ()
		{
			DenyIfNotBound ();
			BoundPage.BoundDialog.ClickButton (this);
		}

		internal bool HandleButtonClicked ()
		{
			OnClick (EventArgs.Empty);
			return AllowCloseDialog;
		}

		private protected void OnClick (EventArgs e) => Click?.Invoke (this, e);

		public static bool operator == (TaskDialogButton b1, TaskDialogButton b2) => Equals (b1, b2);

		public static bool operator != (TaskDialogButton b1, TaskDialogButton b2) => !(b1 == b2);

		/// <summary>Standard buttons are equal when their results are ("result == TaskDialogButton.Yes"); others when
		/// they are the same button.</summary>
		public override bool Equals (object obj)
		{
			if (IsStandardButton && obj is TaskDialogButton other && other.IsStandardButton)
				return _standardButtonResult.Value == other._standardButtonResult.Value;
			return base.Equals (obj);
		}

		public override int GetHashCode () => IsStandardButton ? (int) _standardButtonResult.Value : base.GetHashCode ();

		public override string ToString () => _text ?? base.ToString () ?? string.Empty;
	}

	/// <summary>A button shown as a command link: a large button in the content area with a description under it.</summary>
	public sealed class TaskDialogCommandLinkButton : TaskDialogButton
	{
		private string _descriptionText;

		public TaskDialogCommandLinkButton ()
		{
		}

		public TaskDialogCommandLinkButton (string text, string descriptionText = null, bool enabled = true, bool allowCloseDialog = true)
			: base (text, enabled, allowCloseDialog)
		{
			_descriptionText = descriptionText;
		}

		public string DescriptionText {
			get => _descriptionText;
			set {
				DenyIfBound ();
				_descriptionText = value;
			}
		}
	}

	public class TaskDialogButtonCollection : Collection<TaskDialogButton>
	{
		public TaskDialogButtonCollection ()
		{
		}

		internal TaskDialogPage BoundPage { get; set; }

		public TaskDialogButton Add (string text, bool enabled = true, bool allowCloseDialog = true)
		{
			var button = new TaskDialogButton (text, enabled, allowCloseDialog);
			Add (button);
			return button;
		}

		protected override void SetItem (int index, TaskDialogButton item)
		{
			BoundPage?.DenyIfBound ();
			DenyIfHasOtherCollection (item);
			DenyIfDuplicate (item, index);
			TaskDialogButton old = this [index];
			base.SetItem (index, item);
			old.Collection = null;
			item.Collection = this;
		}

		protected override void InsertItem (int index, TaskDialogButton item)
		{
			BoundPage?.DenyIfBound ();
			DenyIfHasOtherCollection (item);
			DenyIfDuplicate (item, -1);
			item.Collection = this;
			base.InsertItem (index, item);
		}

		protected override void RemoveItem (int index)
		{
			BoundPage?.DenyIfBound ();
			TaskDialogButton old = this [index];
			old.Collection = null;
			base.RemoveItem (index);
		}

		protected override void ClearItems ()
		{
			BoundPage?.DenyIfBound ();
			foreach (TaskDialogButton button in this)
				button.Collection = null;
			base.ClearItems ();
		}

		private void DenyIfHasOtherCollection (TaskDialogButton item)
		{
			if (item == null)
				throw new ArgumentNullException (nameof (item));
			if (item.Collection != null && item.Collection != this)
				throw new InvalidOperationException ("This control is already part of a different collection.");
		}

		// a standard button once: two would be one result
		private void DenyIfDuplicate (TaskDialogButton item, int replacing)
		{
			for (int i = 0; i < Count; i++) {
				if (i == replacing)
					continue;
				if (ReferenceEquals (this [i], item) || (item.IsStandardButton && this [i].Equals (item)))
					throw new InvalidOperationException ("A button with the same result is already in the collection.");
			}
		}
	}

	public sealed class TaskDialogRadioButton : TaskDialogControl
	{
		private string _text;
		private bool _enabled = true, _checked;

		public event EventHandler CheckedChanged;

		public TaskDialogRadioButton ()
		{
		}

		public TaskDialogRadioButton (string text) => _text = text;

		public bool Enabled {
			get => _enabled;
			set {
				_enabled = value;
				Changed ();
			}
		}

		public string Text {
			get => _text;
			set {
				DenyIfBound ();
				_text = value;
			}
		}

		/// <summary>Whether the button is the page's checked one; checking it unchecks the others.</summary>
		public bool Checked {
			get => _checked;
			set {
				DenyIfBoundAndNotCreated ();
				if (_checked == value)
					return;
				if (!value && BoundPage != null)
					throw new InvalidOperationException ("A radio button of a shown task dialog cannot be unchecked; check another one instead.");
				_checked = value;
				if (value && Collection != null) {
					foreach (TaskDialogRadioButton other in Collection) {
						if (!ReferenceEquals (other, this) && other._checked) {
							other._checked = false;
							other.OnCheckedChanged (EventArgs.Empty);
							other.Changed ();
						}
					}
				}
				OnCheckedChanged (EventArgs.Empty);
				Changed ();
			}
		}

		internal TaskDialogRadioButtonCollection Collection { get; set; }

		internal override bool IsCreatable => base.IsCreatable && !string.IsNullOrEmpty (_text);

		/// <summary>The user chose it (a click, or its access key).</summary>
		internal void UserChecked ()
		{
			if (_checked)
				return;
			Checked = true;
		}

		private void OnCheckedChanged (EventArgs e) => CheckedChanged?.Invoke (this, e);

		public override string ToString () => _text ?? base.ToString () ?? string.Empty;
	}

	public class TaskDialogRadioButtonCollection : Collection<TaskDialogRadioButton>
	{
		public TaskDialogRadioButtonCollection ()
		{
		}

		internal TaskDialogPage BoundPage { get; set; }

		public TaskDialogRadioButton Add (string text)
		{
			var button = new TaskDialogRadioButton (text);
			Add (button);
			return button;
		}

		protected override void SetItem (int index, TaskDialogRadioButton item)
		{
			BoundPage?.DenyIfBound ();
			Check (item, index);
			TaskDialogRadioButton old = this [index];
			base.SetItem (index, item);
			old.Collection = null;
			item.Collection = this;
		}

		protected override void InsertItem (int index, TaskDialogRadioButton item)
		{
			BoundPage?.DenyIfBound ();
			Check (item, -1);
			item.Collection = this;
			base.InsertItem (index, item);
		}

		protected override void RemoveItem (int index)
		{
			BoundPage?.DenyIfBound ();
			this [index].Collection = null;
			base.RemoveItem (index);
		}

		protected override void ClearItems ()
		{
			BoundPage?.DenyIfBound ();
			foreach (TaskDialogRadioButton button in this)
				button.Collection = null;
			base.ClearItems ();
		}

		private void Check (TaskDialogRadioButton item, int replacing)
		{
			if (item == null)
				throw new ArgumentNullException (nameof (item));
			if (item.Collection != null && item.Collection != this)
				throw new InvalidOperationException ("This control is already part of a different collection.");
			for (int i = 0; i < Count; i++)
				if (i != replacing && ReferenceEquals (this [i], item))
					throw new InvalidOperationException ("This control is already in the collection.");
		}
	}

	public sealed class TaskDialogVerificationCheckBox : TaskDialogControl
	{
		private string _text;
		private bool _checked;

		public event EventHandler CheckedChanged;

		public TaskDialogVerificationCheckBox ()
		{
		}

		public TaskDialogVerificationCheckBox (string text, bool isChecked = false)
		{
			_text = text;
			_checked = isChecked;
		}

		public static implicit operator TaskDialogVerificationCheckBox (string verificationText) => new TaskDialogVerificationCheckBox (verificationText);

		public string Text {
			get => _text;
			set {
				DenyIfBound ();
				_text = value;
			}
		}

		public bool Checked {
			get => _checked;
			set {
				DenyIfBoundAndNotCreated ();
				if (_checked == value)
					return;
				_checked = value;
				CheckedChanged?.Invoke (this, EventArgs.Empty);
				Changed ();
			}
		}

		internal override bool IsCreatable => base.IsCreatable && !string.IsNullOrEmpty (_text);

		public override string ToString () => _text ?? base.ToString () ?? string.Empty;
	}

	public sealed class TaskDialogExpander : TaskDialogControl
	{
		private string _text, _expandedButtonText, _collapsedButtonText;
		private bool _expanded;
		private TaskDialogExpanderPosition _position;

		public event EventHandler ExpandedChanged;

		public TaskDialogExpander ()
		{
		}

		public TaskDialogExpander (string text) => _text = text;

		/// <summary>The text shown while expanded; may be changed while shown (not emptied, not set where it was empty).</summary>
		public string Text {
			get => _text;
			set {
				DenyIfBoundAndNotCreated ();
				if (BoundPage != null && string.IsNullOrEmpty (value))
					throw new InvalidOperationException ("The text of a shown expander cannot be emptied.");
				_text = value;
				Changed (relayout: true);
			}
		}

		/// <summary>The button's text while expanded ("Hide details").</summary>
		public string ExpandedButtonText {
			get => _expandedButtonText;
			set {
				DenyIfBound ();
				_expandedButtonText = value;
			}
		}

		/// <summary>The button's text while collapsed ("See details").</summary>
		public string CollapsedButtonText {
			get => _collapsedButtonText;
			set {
				DenyIfBound ();
				_collapsedButtonText = value;
			}
		}

		public bool Expanded {
			get => _expanded;
			set {
				DenyIfBound ();
				_expanded = value;
			}
		}

		public TaskDialogExpanderPosition Position {
			get => _position;
			set {
				if (value < TaskDialogExpanderPosition.AfterText || value > TaskDialogExpanderPosition.AfterFootnote)
					throw new InvalidEnumArgumentException (nameof (value), (int) value, typeof (TaskDialogExpanderPosition));
				DenyIfBound ();
				_position = value;
			}
		}

		internal override bool IsCreatable => base.IsCreatable && !string.IsNullOrEmpty (_text);

		/// <summary>The user toggled it.</summary>
		internal void UserToggled ()
		{
			_expanded = !_expanded;
			ExpandedChanged?.Invoke (this, EventArgs.Empty);
			Changed (relayout: true);
		}

		public override string ToString () => _text ?? base.ToString () ?? string.Empty;
	}

	public sealed class TaskDialogFootnote : TaskDialogControl
	{
		private string _text;
		private TaskDialogIcon _icon;

		public TaskDialogFootnote ()
		{
		}

		public TaskDialogFootnote (string text) => _text = text;

		public static implicit operator TaskDialogFootnote (string footnoteText) => new TaskDialogFootnote (footnoteText);

		public string Text {
			get => _text;
			set {
				DenyIfBoundAndNotCreated ();
				if (BoundPage != null && string.IsNullOrEmpty (value))
					throw new InvalidOperationException ("The text of a shown footnote cannot be emptied.");
				_text = value;
				Changed (relayout: true);
			}
		}

		public TaskDialogIcon Icon {
			get => _icon;
			set {
				DenyIfBoundAndNotCreated ();
				_icon = value;
				Changed (relayout: true);
			}
		}

		internal override bool IsCreatable => base.IsCreatable && !string.IsNullOrEmpty (_text);

		public override string ToString () => _text ?? base.ToString () ?? string.Empty;
	}

	public sealed class TaskDialogProgressBar : TaskDialogControl
	{
		private TaskDialogProgressBarState _state;
		private int _minimum, _maximum = 100, _value, _marqueeSpeed;

		public TaskDialogProgressBar ()
		{
		}

		public TaskDialogProgressBar (TaskDialogProgressBarState state) => _state = state;

		public TaskDialogProgressBarState State {
			get => _state;
			set {
				if (value < TaskDialogProgressBarState.Normal || value > TaskDialogProgressBarState.None)
					throw new InvalidEnumArgumentException (nameof (value), (int) value, typeof (TaskDialogProgressBarState));
				DenyIfBoundAndNotCreated ();
				// shown, a bar cannot go away (None) nor come where there was none
				if (BoundPage != null && value == TaskDialogProgressBarState.None)
					throw new InvalidOperationException ("The progress bar of a shown task dialog cannot be set to None.");
				_state = value;
				Changed ();
			}
		}

		public int Minimum {
			get => _minimum;
			set {
				if (value < 0 || value > ushort.MaxValue)
					throw new ArgumentOutOfRangeException (nameof (value));
				DenyIfBoundAndNotCreated ();
				_minimum = value;
				if (_maximum < _minimum)
					_maximum = _minimum;
				if (_value < _minimum)
					_value = _minimum;
				Changed ();
			}
		}

		public int Maximum {
			get => _maximum;
			set {
				if (value < 0 || value > ushort.MaxValue)
					throw new ArgumentOutOfRangeException (nameof (value));
				DenyIfBoundAndNotCreated ();
				_maximum = value;
				if (_minimum > _maximum)
					_minimum = _maximum;
				if (_value > _maximum)
					_value = _maximum;
				Changed ();
			}
		}

		public int Value {
			get => _value;
			set {
				if (value < 0 || value > ushort.MaxValue)
					throw new ArgumentOutOfRangeException (nameof (value));
				DenyIfBoundAndNotCreated ();
				_value = Math.Clamp (value, _minimum, Math.Max (_minimum, _maximum));
				Changed ();
			}
		}

		/// <summary>Milliseconds between the marquee's steps; 0 for the default (30).</summary>
		public int MarqueeSpeed {
			get => _marqueeSpeed;
			set {
				DenyIfBoundAndNotCreated ();
				_marqueeSpeed = value;
				Changed ();
			}
		}

		internal bool IsMarquee => _state is TaskDialogProgressBarState.Marquee or TaskDialogProgressBarState.MarqueePaused;

		internal override bool IsCreatable => base.IsCreatable && _state != TaskDialogProgressBarState.None;
	}
}
