// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// A task dialog, as .NET's TaskDialog shows one (comctl32's TaskDialogIndirect), drawn here as a form of this WinForms:
// the same code on every head, Windows included. Laid out as Windows 11 lays it out -- the content area (icon, heading
// in large blue type, text, command links, radio buttons, progress bar, expanded text) on the window colour; under it
// the control-coloured bar with the expander's button and the verification check box at the left and the buttons at
// the right; then the footnote -- and answering what the page changes while it is shown, as comctl32 answers TDM_*.

using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace System.Windows.Forms
{
	public partial class TaskDialog : IWin32Window
	{
		private TaskDialogPage _boundPage;
		private TaskDialogForm _form;
		private TaskDialogButton _result;

		private TaskDialog ()
		{
		}

		/// <summary>The dialog's window while it is shown; IntPtr.Zero otherwise.</summary>
		public IntPtr Handle => _form != null && _form.IsHandleCreated ? _form.Handle : IntPtr.Zero;

		/// <summary>Shows <paramref name="page"/> modally, and returns the button that closed it.</summary>
		public static TaskDialogButton ShowDialog (TaskDialogPage page, TaskDialogStartupLocation startupLocation = TaskDialogStartupLocation.CenterOwner)
			=> ShowDialog (IntPtr.Zero, page, startupLocation);

		/// <summary>Shows <paramref name="page"/> modally over <paramref name="owner"/>.</summary>
		public static TaskDialogButton ShowDialog (IWin32Window owner, TaskDialogPage page, TaskDialogStartupLocation startupLocation = TaskDialogStartupLocation.CenterOwner)
		{
			if (owner == null)
				throw new ArgumentNullException (nameof (owner));
			return ShowDialogInternal (owner, page, startupLocation);
		}

		/// <summary>Shows <paramref name="page"/> modally over the window <paramref name="hwndOwner"/>.</summary>
		public static TaskDialogButton ShowDialog (IntPtr hwndOwner, TaskDialogPage page, TaskDialogStartupLocation startupLocation = TaskDialogStartupLocation.CenterOwner)
		{
			IWin32Window owner = hwndOwner == IntPtr.Zero ? null : (Control.FromHandle (hwndOwner) as IWin32Window ?? new NativeOwner (hwndOwner));
			return ShowDialogInternal (owner, page, startupLocation);
		}

		/// <summary>Shows <paramref name="page"/> modally, not waiting: the task gives the button that closed it.</summary>
		public static Task<TaskDialogButton> ShowDialogAsync (TaskDialogPage page, TaskDialogStartupLocation startupLocation = TaskDialogStartupLocation.CenterOwner)
			=> ShowDialogAsync (IntPtr.Zero, page, startupLocation);

		public static Task<TaskDialogButton> ShowDialogAsync (IWin32Window owner, TaskDialogPage page, TaskDialogStartupLocation startupLocation = TaskDialogStartupLocation.CenterOwner)
		{
			if (owner == null)
				throw new ArgumentNullException (nameof (owner));
			return ShowDialogAsync (owner.Handle, page, startupLocation);
		}

		public static Task<TaskDialogButton> ShowDialogAsync (IntPtr hwndOwner, TaskDialogPage page, TaskDialogStartupLocation startupLocation = TaskDialogStartupLocation.CenterOwner)
		{
			if (page == null)
				throw new ArgumentNullException (nameof (page));
			if (SynchronizationContext.Current == null)
				WindowsFormsSynchronizationContext.InstallIfNeeded ();
			SynchronizationContext context = SynchronizationContext.Current
				?? throw new InvalidOperationException ("No WindowsFormsSynchronizationContext to show the task dialog asynchronously.");
			var completion = new TaskCompletionSource<TaskDialogButton> (TaskCreationOptions.RunContinuationsAsynchronously);
			context.Post (_ => {
				try {
					completion.TrySetResult (ShowDialog (hwndOwner, page, startupLocation));
				} catch (Exception ex) {
					completion.TrySetException (ex);
				}
			}, null);
			return completion.Task;
		}

		private static TaskDialogButton ShowDialogInternal (IWin32Window owner, TaskDialogPage page, TaskDialogStartupLocation startupLocation)
		{
			if (page == null)
				throw new ArgumentNullException (nameof (page));
			var dialog = new TaskDialog ();
			return dialog.Show (owner, page, startupLocation);
		}

		private TaskDialogButton Show (IWin32Window owner, TaskDialogPage page, TaskDialogStartupLocation startupLocation)
		{
			page.Bind (this);
			_boundPage = page;
			try {
				using (_form = new TaskDialogForm (this)) {
					_form.StartPosition = startupLocation == TaskDialogStartupLocation.CenterOwner && owner != null
						? FormStartPosition.CenterParent : FormStartPosition.CenterScreen;
					_form.Build ();
					if (owner != null)
						_form.ShowDialog (owner);
					else
						_form.ShowDialog ();
				}
			} finally {
				TaskDialogPage shown = _boundPage;
				_form = null;
				_boundPage = null;
				if (shown != null) {
					shown.Unbind ();
					shown.OnDestroyed (EventArgs.Empty);
				}
			}
			return _result ?? page.FindStandard (TaskDialogResult.Cancel) ?? TaskDialogButton.Cancel;
		}

		/// <summary>Closes the dialog as Cancel would, whether or not the page has a Cancel button.</summary>
		public void Close ()
		{
			if (_form == null)
				return;
			_result = _boundPage?.FindStandard (TaskDialogResult.Cancel) ?? TaskDialogButton.Cancel;
			_form.CloseFromDialog ();
		}

		internal TaskDialogPage BoundPage => _boundPage;

		internal void ClickButton (TaskDialogButton button)
		{
			if (_form == null)
				return;
			if (button.IsStandardButton && button.StandardButtonResult == TaskDialogResult.Help) {
				// comctl32's TDN_HELP: Help asks for help, it does not close
				button.HandleButtonClicked ();
				_boundPage?.OnHelpRequest (EventArgs.Empty);
				return;
			}
			TaskDialogPage page = _boundPage;
			bool close = button.HandleButtonClicked ();
			// the Click handler may have navigated (another page now) or closed the dialog
			if (close && _form != null && ReferenceEquals (page, _boundPage)) {
				_result = button;
				_form.CloseFromDialog ();
			}
		}

		internal void Cancel ()
		{
			TaskDialogButton cancel = _boundPage?.FindStandard (TaskDialogResult.Cancel);
			if (cancel != null) {
				ClickButton (cancel);
				return;
			}
			_result = TaskDialogButton.Cancel;
			_form?.CloseFromDialog ();
		}

		internal void Navigate (TaskDialogPage page)
		{
			if (_form == null)
				throw new InvalidOperationException ("The task dialog is not shown.");
			TaskDialogPage old = _boundPage;
			page.Bind (this);
			if (old != null) {
				old.Unbind ();
				old.OnDestroyed (EventArgs.Empty);
			}
			_boundPage = page;
			_form.Build ();
			page.OnCreated (EventArgs.Empty);
		}

		internal void ControlChanged (TaskDialogControl control, bool relayout)
		{
			if (_form == null)
				return;
			if (relayout)
				_form.Build ();
			else
				_form.Refresh (control);
		}

		internal void PageChanged (bool relayout)
		{
			if (_form == null)
				return;
			if (relayout)
				_form.Build ();
			else
				_form.Text = _boundPage?.Caption ?? Application.ProductName;
		}

		internal void Shown () => _boundPage?.OnCreated (EventArgs.Empty);

		private sealed class NativeOwner : IWin32Window
		{
			public NativeOwner (IntPtr handle) => Handle = handle;

			public IntPtr Handle { get; }
		}
	}

	/// <summary>The task dialog's window.</summary>
	internal sealed class TaskDialogForm : Form
	{
		// comctl32's spacing at 96 DPI
		private const int Margin = 12, IconSize = 32, Gap = 10, ButtonGap = 6, BarPad = 10;
		private static readonly Color HeadingColor = Color.FromArgb (0, 51, 153);
		private static readonly Color SeparatorColor = Color.FromArgb (223, 223, 223);

		private readonly TaskDialog _dialog;
		private readonly Dictionary<TaskDialogControl, Control> _shown = new Dictionary<TaskDialogControl, Control> ();
		private readonly Panel _content = new DoubleBufferedPanel ();
		private readonly Panel _bar = new DoubleBufferedPanel ();
		private readonly Panel _footer = new DoubleBufferedPanel ();
		private bool _closingFromDialog, _built;

		public TaskDialogForm (TaskDialog dialog)
		{
			_dialog = dialog;
			FormBorderStyle = FormBorderStyle.FixedDialog;
			MaximizeBox = false;
			ShowInTaskbar = false;
			KeyPreview = true;
			AutoScaleMode = AutoScaleMode.None;
			_content.BackColor = SystemColors.Window;
			_bar.BackColor = SystemColors.Control;
			_footer.BackColor = SystemColors.Control;
			_bar.Paint += (s, e) => TopLine (e.Graphics, _bar);
			_footer.Paint += (s, e) => TopLine (e.Graphics, _footer);
			Controls.Add (_content);
			Controls.Add (_bar);
			Controls.Add (_footer);
		}

		private TaskDialogPage Page => _dialog.BoundPage;

		private float Scale => DeviceDpi / 96f;

		private int S (int value) => (int) Math.Round (value * Scale);

		private static void TopLine (Graphics g, Control c)
		{
			using (var pen = new Pen (SeparatorColor))
				g.DrawLine (pen, 0, 0, c.Width, 0);
		}

		/// <summary>Lays the page out anew: shown first, navigated, or changed in what it shows.</summary>
		public void Build ()
		{
			TaskDialogPage page = Page;
			if (page == null)
				return;

			object focused = null;
			foreach (var pair in _shown)
				if (pair.Value.ContainsFocus)
					focused = pair.Key;

			SuspendLayout ();
			_content.SuspendLayout ();
			_bar.SuspendLayout ();
			_footer.SuspendLayout ();
			foreach (Control c in new List<Control> (EnumerateChildren ()))
				c.Dispose ();
			_content.Controls.Clear ();
			_bar.Controls.Clear ();
			_footer.Controls.Clear ();
			_shown.Clear ();

			Text = string.IsNullOrEmpty (page.Caption) ? Application.ProductName : page.Caption;
			MinimizeBox = page.AllowMinimize;
			ControlBox = true;
			RightToLeft = page.RightToLeftLayout ? RightToLeft.Yes : RightToLeft.No;
			RightToLeftLayout = page.RightToLeftLayout;

			Font font = Font;
			using var headingFont = new Font (font.FontFamily, font.SizeInPoints * 12f / 9f, FontStyle.Regular, GraphicsUnit.Point);

			var buttons = new List<TaskDialogButton> (page.ShownButtons ());
			bool commandLinks = buttons.Exists (b => b is TaskDialogCommandLinkButton);

			// the width: comctl32's for the text (wider for a long heading), or what the content needs
			int width = S (page.SizeToContent ? 300 : 360);
			int iconSpace = 0;
			Image icon = page.Icon?.ImageAt (S (IconSize));
			Color bar = page.Icon?.BarColor ?? Color.Empty;
			if (icon != null)
				iconSpace = S (IconSize) + S (Gap);
			int longest = Math.Max (MeasureLine (page.Heading, headingFont), MeasureLine (page.Text, font) / (page.SizeToContent ? 1 : 2));
			width = Math.Max (width, Math.Min (S (page.SizeToContent ? 900 : 560), longest + iconSpace + 2 * S (Margin)));
			int textLeft = S (Margin) + iconSpace;
			int textWidth = width - textLeft - S (Margin);

			// ---- content
			int y = S (Margin);
			if (!bar.IsEmpty) {
				// a shield bar: the heading in white on the bar, the icon at its left
				int barHeight = Math.Max (S (IconSize) + 2 * S (6), Measure (page.Heading, headingFont, textWidth) + 2 * S (6));
				var strip = new Panel { BackColor = bar, Location = new Point (0, 0), Size = new Size (width, barHeight) };
				if (icon != null)
					strip.Controls.Add (new PictureBox { Image = icon, SizeMode = PictureBoxSizeMode.Zoom, BackColor = bar, Location = new Point (S (Margin), (barHeight - S (IconSize)) / 2), Size = new Size (S (IconSize), S (IconSize)) });
				if (!string.IsNullOrEmpty (page.Heading))
					strip.Controls.Add (new Label { Text = page.Heading, Font = new Font (headingFont, FontStyle.Regular), ForeColor = bar.GetBrightness () > 0.6f ? Color.Black : Color.White, BackColor = bar, Location = new Point (textLeft, (barHeight - Measure (page.Heading, headingFont, textWidth)) / 2), Size = new Size (textWidth, Measure (page.Heading, headingFont, textWidth)), UseMnemonic = false });
				_content.Controls.Add (strip);
				y = barHeight + S (Margin);
			} else {
				if (icon != null)
					_content.Controls.Add (new PictureBox { Image = icon, SizeMode = PictureBoxSizeMode.Zoom, BackColor = SystemColors.Window, Location = new Point (S (Margin), y), Size = new Size (S (IconSize), S (IconSize)) });
				if (!string.IsNullOrEmpty (page.Heading)) {
					int h = Measure (page.Heading, headingFont, textWidth);
					_content.Controls.Add (new Label { Text = page.Heading, Font = new Font (headingFont, FontStyle.Regular), ForeColor = HeadingColor, BackColor = SystemColors.Window, Location = new Point (textLeft, y), Size = new Size (textWidth, h), UseMnemonic = false });
					y += h + S (Gap);
				}
			}

			if (!string.IsNullOrEmpty (page.Text)) {
				Control text = TextControl (page.Text, font, textWidth, page.EnableLinks);
				text.Location = new Point (textLeft, y);
				_content.Controls.Add (text);
				y += text.Height + S (Gap);
			}

			TaskDialogExpander expander = page.Expander;
			bool expanderShown = expander != null && expander.IsCreated;
			if (expanderShown && expander.Expanded && expander.Position == TaskDialogExpanderPosition.AfterText) {
				Control expanded = TextControl (expander.Text, font, textWidth, page.EnableLinks);
				expanded.Location = new Point (textLeft, y);
				_content.Controls.Add (expanded);
				y += expanded.Height + S (Gap);
			}

			TaskDialogProgressBar progress = page.ProgressBar;
			if (progress != null && progress.IsCreated) {
				var bar2 = new TaskProgress { Location = new Point (textLeft, y), Size = new Size (textWidth, S (15)) };
				bar2.Apply (progress);
				_content.Controls.Add (bar2);
				_shown [progress] = bar2;
				y += bar2.Height + S (Gap);
			}

			foreach (TaskDialogRadioButton radio in page.RadioButtons) {
				if (!radio.IsCreated)
					continue;
				var rb = new RadioButton { Text = radio.Text, Checked = radio.Checked, Enabled = radio.Enabled, AutoSize = false, BackColor = SystemColors.Window, Font = font };
				int h = Math.Max (S (20), Measure (radio.Text, font, textWidth - S (20)) + S (4));
				rb.Location = new Point (textLeft, y);
				rb.Size = new Size (textWidth, h);
				TaskDialogRadioButton model = radio;
				rb.CheckedChanged += (s, e) => { if (rb.Checked) model.UserChecked (); };
				_content.Controls.Add (rb);
				_shown [radio] = rb;
				y += h + S (2);
			}

			if (commandLinks) {
				foreach (TaskDialogButton button in buttons) {
					if (button is not TaskDialogCommandLinkButton link)
						continue;
					var cl = new CommandLink (link.ShownText, link.DescriptionText, font, headingFont) { Enabled = link.Enabled, Width = textWidth };
					cl.Location = new Point (textLeft, y);
					TaskDialogButton model = link;
					cl.Click += (s, e) => _dialog.ClickButton (model);
					_content.Controls.Add (cl);
					_shown [link] = cl;
					y += cl.Height + S (4);
				}
			}

			_content.Location = Point.Empty;
			_content.Size = new Size (width, y + S (Margin) - S (Gap));

			// ---- the bar: expander and verification at the left, the buttons at the right
			int barY = S (BarPad), right = width - S (Margin);
			int rowHeight = S (23);
			var row = new List<Button> ();
			foreach (TaskDialogButton button in OrderBottom (buttons)) {
				var b = new Button { Text = button.ShownText, Enabled = button.Enabled, Font = font, UseVisualStyleBackColor = true };
				b.Size = new Size (Math.Max (S (75), TextRenderer.MeasureText (button.ShownText.Replace ("&", ""), font).Width + S (20)), rowHeight);
				if (button.ShowShieldIcon)
					ApplyShield (b);
				TaskDialogButton model = button;
				b.Click += (s, e) => _dialog.ClickButton (model);
				row.Add (b);
				_shown [button] = b;
			}
			for (int i = row.Count - 1; i >= 0; i--) {
				right -= row [i].Width;
				row [i].Location = new Point (right, barY);
				right -= S (ButtonGap);
				_bar.Controls.Add (row [i]);
			}

			int leftY = barY;
			if (expanderShown) {
				string label = expander.Expanded ? (expander.ExpandedButtonText ?? "Hide details") : (expander.CollapsedButtonText ?? "See details");
				var toggle = new ExpanderButton (label, expander.Expanded, font) { Location = new Point (S (Margin), leftY) };
				TaskDialogExpander model = expander;
				toggle.Click += (s, e) => model.UserToggled ();
				_bar.Controls.Add (toggle);
				_shown [expander] = toggle;
				leftY += Math.Max (toggle.Height, rowHeight) + S (6);
			}
			TaskDialogVerificationCheckBox verification = page.Verification;
			if (verification != null && verification.IsCreated) {
				var cb = new CheckBox { Text = verification.Text, Checked = verification.Checked, AutoSize = true, Font = font, BackColor = SystemColors.Control };
				cb.Location = new Point (S (Margin), leftY + (expanderShown ? 0 : (rowHeight - S (17)) / 2));
				TaskDialogVerificationCheckBox model = verification;
				cb.CheckedChanged += (s, e) => model.Checked = cb.Checked;
				_bar.Controls.Add (cb);
				_shown [verification] = cb;
				leftY += S (20);
			}
			int barHeight2 = Math.Max (barY + rowHeight, leftY) + S (BarPad);
			_bar.Location = new Point (0, _content.Bottom);
			_bar.Size = new Size (width, barHeight2);
			// (Visible reads false until the form is shown: what is shown is kept here)
			bool barShown = row.Count > 0 || expanderShown || (verification != null && verification.IsCreated);
			_bar.Visible = barShown;

			// ---- the footnote, and the expanded text after it
			int fy = S (BarPad);
			TaskDialogFootnote footnote = page.Footnote;
			if (footnote != null && footnote.IsCreated) {
				int fx = S (Margin);
				Image small = footnote.Icon?.ImageAt (S (16));
				if (small != null) {
					_footer.Controls.Add (new PictureBox { Image = small, SizeMode = PictureBoxSizeMode.Zoom, BackColor = SystemColors.Control, Location = new Point (fx, fy), Size = new Size (S (16), S (16)) });
					fx += S (16) + S (6);
				}
				Control note = TextControl (footnote.Text, font, width - fx - S (Margin), page.EnableLinks);
				note.BackColor = SystemColors.Control;
				note.Location = new Point (fx, fy);
				_footer.Controls.Add (note);
				fy += Math.Max (note.Height, small != null ? S (16) : 0) + S (BarPad);
			}
			if (expanderShown && expander.Expanded && expander.Position == TaskDialogExpanderPosition.AfterFootnote) {
				Control expanded = TextControl (expander.Text, font, width - 2 * S (Margin), page.EnableLinks);
				expanded.BackColor = SystemColors.Control;
				expanded.Location = new Point (S (Margin), fy);
				_footer.Controls.Add (expanded);
				fy += expanded.Height + S (BarPad);
			}
			bool footerShown = fy > S (BarPad);
			_footer.Location = new Point (0, barShown ? _bar.Bottom : _content.Bottom);
			_footer.Size = new Size (width, footerShown ? fy : 0);
			_footer.Visible = footerShown;

			ClientSize = new Size (width, footerShown ? _footer.Bottom : (barShown ? _bar.Bottom : _content.Bottom));

			// the buttons Enter and Esc press
			TaskDialogButton defaultButton = page.DefaultButton;
			Control accept = null;
			if (defaultButton != null) {
				foreach (TaskDialogButton b in buttons)
					if (b.Equals (defaultButton) && _shown.TryGetValue (b, out Control c))
						accept = c;
			}
			accept ??= row.Count > 0 ? row [0] : null;
			AcceptButton = accept as IButtonControl;
			_content.ResumeLayout (false);
			_bar.ResumeLayout (false);
			_footer.ResumeLayout (false);
			ResumeLayout (false);

			Control refocus = null;
			if (focused is TaskDialogControl again && _shown.TryGetValue (again, out Control c2))
				refocus = c2;
			(refocus ?? accept)?.Select ();
			_built = true;
		}

		private IEnumerable<Control> EnumerateChildren ()
		{
			foreach (Control c in _content.Controls)
				yield return c;
			foreach (Control c in _bar.Controls)
				yield return c;
			foreach (Control c in _footer.Controls)
				yield return c;
		}

		// comctl32's order: custom buttons, then the standard ones in its fixed order
		private static IEnumerable<TaskDialogButton> OrderBottom (List<TaskDialogButton> buttons)
		{
			foreach (TaskDialogButton b in buttons)
				if (!b.IsStandardButton && b is not TaskDialogCommandLinkButton)
					yield return b;
			TaskDialogResult [] order = {
				TaskDialogResult.OK, TaskDialogResult.Yes, TaskDialogResult.No, TaskDialogResult.Abort, TaskDialogResult.Retry,
				TaskDialogResult.Ignore, TaskDialogResult.TryAgain, TaskDialogResult.Continue, TaskDialogResult.Cancel,
				TaskDialogResult.Close, TaskDialogResult.Help,
			};
			foreach (TaskDialogResult result in order)
				foreach (TaskDialogButton b in buttons)
					if (b.IsStandardButton && b.StandardButtonResult == result)
						yield return b;
		}

		private static void ApplyShield (Button b)
		{
			b.Image = new Icon (SystemIcons.Shield, 16, 16).ToBitmap ();
			b.ImageAlign = ContentAlignment.MiddleLeft;
			b.TextImageRelation = TextImageRelation.ImageBeforeText;
			b.Width += 20;
		}

		/// <summary>What changed of a control, in place: its enabled and checked state, a progress bar's value.</summary>
		public void Refresh (TaskDialogControl control)
		{
			if (!_built || !_shown.TryGetValue (control, out Control shown))
				return;
			switch (control) {
			case TaskDialogButton button:
				shown.Enabled = button.Enabled;
				if (shown is Button b && button.ShowShieldIcon && b.Image == null)
					ApplyShield (b);
				else if (shown is Button b2 && !button.ShowShieldIcon && b2.Image != null)
					b2.Image = null;
				break;
			case TaskDialogRadioButton radio:
				shown.Enabled = radio.Enabled;
				((RadioButton) shown).Checked = radio.Checked;
				break;
			case TaskDialogVerificationCheckBox verification:
				((CheckBox) shown).Checked = verification.Checked;
				break;
			case TaskDialogProgressBar progress:
				((TaskProgress) shown).Apply (progress);
				break;
			}
		}

		public void CloseFromDialog ()
		{
			_closingFromDialog = true;
			DialogResult = DialogResult.OK;
			Close ();
		}

		protected override void OnShown (EventArgs e)
		{
			base.OnShown (e);
			_dialog.Shown ();
		}

		protected override void OnFormClosing (FormClosingEventArgs e)
		{
			base.OnFormClosing (e);
			if (_closingFromDialog || e.Cancel)
				return;
			// the close box, Alt+F4: Cancel, where the page can be cancelled
			if (Page != null && !Page.CanBeCanceled) {
				e.Cancel = true;
				return;
			}
			e.Cancel = true;
			BeginInvoke ((Action) _dialog.Cancel);
		}

		protected override bool ProcessDialogKey (Keys keyData)
		{
			if (keyData == Keys.Escape) {
				if (Page != null && Page.CanBeCanceled)
					_dialog.Cancel ();
				return true;
			}
			if (keyData == Keys.F1) {
				Page?.OnHelpRequest (EventArgs.Empty);
				return true;
			}
			return base.ProcessDialogKey (keyData);
		}

		private int Measure (string text, Font font, int width) =>
			string.IsNullOrEmpty (text) ? 0 : TextRenderer.MeasureText (text, font, new Size (width, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height;

		private static int MeasureLine (string text, Font font)
		{
			if (string.IsNullOrEmpty (text))
				return 0;
			int widest = 0;
			foreach (string line in text.Replace ("\r\n", "\n").Split ('\n'))
				widest = Math.Max (widest, TextRenderer.MeasureText (line, font, Size.Empty, TextFormatFlags.NoPrefix).Width);
			return widest;
		}

		/// <summary>A block of text: a label, or a link label where the page's links are on and the text has any.</summary>
		private Control TextControl (string markup, Font font, int width, bool links)
		{
			if (links && markup.IndexOf ("<a", StringComparison.OrdinalIgnoreCase) >= 0) {
				string plain = ParseLinks (markup, out List<(int Start, int Length, string Href)> found);
				var label = new LinkLabel { Text = plain, Font = font, AutoSize = false, BackColor = SystemColors.Window, UseMnemonic = false };
				label.Links.Clear ();
				foreach (var link in found)
					label.Links.Add (link.Start, link.Length, link.Href);
				label.LinkClicked += (s, e) => Page?.OnLinkClicked (new TaskDialogLinkClickedEventArgs ((string) e.Link.LinkData ?? string.Empty));
				label.Size = new Size (width, Math.Max (Measure (plain, font, width), label.GetPreferredSize (new Size (width, 0)).Height) + 2);
				return label;
			}
			string shown = links ? markup : markup;
			var plainLabel = new Label { Text = shown, Font = font, AutoSize = false, BackColor = SystemColors.Window, UseMnemonic = false };
			plainLabel.Size = new Size (width, Math.Max (Measure (shown, font, width), plainLabel.GetPreferredSize (new Size (width, 0)).Height) + 2);
			return plainLabel;
		}

		/// <summary>comctl32's link markup: &lt;a href="x"&gt;text&lt;/a&gt; (the href in either quote, or none).</summary>
		internal static string ParseLinks (string markup, out List<(int Start, int Length, string Href)> links)
		{
			links = new List<(int, int, string)> ();
			var plain = new StringBuilder ();
			int i = 0;
			while (i < markup.Length) {
				int open = markup.IndexOf ("<a", i, StringComparison.OrdinalIgnoreCase);
				int tagEnd = open < 0 ? -1 : markup.IndexOf ('>', open);
				int close = tagEnd < 0 ? -1 : markup.IndexOf ("</a>", tagEnd, StringComparison.OrdinalIgnoreCase);
				if (open < 0 || tagEnd < 0 || close < 0) {
					plain.Append (markup, i, markup.Length - i);
					break;
				}
				plain.Append (markup, i, open - i);
				string tag = markup.Substring (open + 2, tagEnd - open - 2);
				string href = string.Empty;
				int h = tag.IndexOf ("href", StringComparison.OrdinalIgnoreCase);
				if (h >= 0) {
					int eq = tag.IndexOf ('=', h);
					if (eq >= 0) {
						string value = tag.Substring (eq + 1).Trim ();
						href = value.Length > 1 && (value [0] == '"' || value [0] == '\'')
							? value.Substring (1, Math.Max (0, value.IndexOf (value [0], 1) - 1))
							: value.Split (' ') [0];
					}
				}
				string text = markup.Substring (tagEnd + 1, close - tagEnd - 1);
				links.Add ((plain.Length, text.Length, href));
				plain.Append (text);
				i = close + 4;
			}
			return plain.ToString ();
		}

		private sealed class DoubleBufferedPanel : Panel
		{
			public DoubleBufferedPanel () => SetStyle (ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
		}

		/// <summary>A command link: the arrow, the text in large type, the description under it; a light frame
		/// while hot or pressed, as comctl32's BS_COMMANDLINK.</summary>
		private sealed class CommandLink : Button
		{
			private readonly string _title, _note;
			private readonly Font _titleFont, _noteFont;
			private bool _hot, _pressed;

			public CommandLink (string title, string note, Font noteFont, Font titleFont)
			{
				_title = title;
				_note = note;
				_noteFont = noteFont;
				_titleFont = new Font (titleFont.FontFamily, titleFont.SizeInPoints * 0.9f, FontStyle.Regular, GraphicsUnit.Point);
				Text = title;
				SetStyle (ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
				int titleHeight = TextRenderer.MeasureText (title ?? "", _titleFont).Height;
				int noteHeight = string.IsNullOrEmpty (note) ? 0 : TextRenderer.MeasureText (note, noteFont).Height;
				Height = titleHeight + noteHeight + 16;
			}

			protected override void OnMouseEnter (EventArgs e) { _hot = true; Invalidate (); base.OnMouseEnter (e); }
			protected override void OnMouseLeave (EventArgs e) { _hot = false; _pressed = false; Invalidate (); base.OnMouseLeave (e); }
			protected override void OnMouseDown (MouseEventArgs e) { _pressed = true; Invalidate (); base.OnMouseDown (e); }
			protected override void OnMouseUp (MouseEventArgs e) { _pressed = false; Invalidate (); base.OnMouseUp (e); }

			protected override void OnPaint (PaintEventArgs e)
			{
				Graphics g = e.Graphics;
				g.Clear (SystemColors.Window);
				Rectangle r = new Rectangle (0, 0, Width - 1, Height - 1);
				if (Enabled && (_hot || _pressed || Focused)) {
					using var fill = new SolidBrush (_pressed ? Color.FromArgb (204, 228, 247) : Color.FromArgb (229, 241, 251));
					using var edge = new Pen (_pressed ? Color.FromArgb (0, 84, 153) : Color.FromArgb (0, 120, 215));
					if (_hot || _pressed)
						g.FillRectangle (fill, r);
					g.DrawRectangle (edge, r);
				}
				Color ink = Enabled ? HeadingColor : SystemColors.GrayText;
				// the arrow
				SmoothingMode smoothing = g.SmoothingMode;
				g.SmoothingMode = SmoothingMode.AntiAlias;
				using (var pen = new Pen (Enabled ? Color.FromArgb (0, 120, 215) : SystemColors.GrayText, 2f)) {
					int ax = 10, ay = 8 + TextRenderer.MeasureText ("X", _titleFont).Height / 2;
					g.DrawLine (pen, ax, ay, ax + 12, ay);
					g.DrawLine (pen, ax + 7, ay - 5, ax + 12, ay);
					g.DrawLine (pen, ax + 7, ay + 5, ax + 12, ay);
				}
				g.SmoothingMode = smoothing;
				int x = 30;
				TextRenderer.DrawText (g, _title, _titleFont, new Rectangle (x, 6, Width - x - 6, Height), ink, TextFormatFlags.WordBreak);
				if (!string.IsNullOrEmpty (_note)) {
					int y = 6 + TextRenderer.MeasureText (_title ?? "", _titleFont).Height + 2;
					TextRenderer.DrawText (g, _note, _noteFont, new Rectangle (x, y, Width - x - 6, Height - y), Enabled ? SystemColors.ControlText : SystemColors.GrayText, TextFormatFlags.WordBreak);
				}
			}
		}

		/// <summary>The expander's button: a round chevron and its text.</summary>
		private sealed class ExpanderButton : Control
		{
			private readonly bool _expanded;
			private bool _hot;

			public ExpanderButton (string text, bool expanded, Font font)
			{
				Text = text;
				Font = font;
				_expanded = expanded;
				SetStyle (ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Selectable, true);
				Size = new Size (24 + TextRenderer.MeasureText (text, font).Width + 8, Math.Max (21, font.Height + 6));
				Cursor = Cursors.Hand;
				TabStop = true;
			}

			protected override void OnMouseEnter (EventArgs e) { _hot = true; Invalidate (); base.OnMouseEnter (e); }
			protected override void OnMouseLeave (EventArgs e) { _hot = false; Invalidate (); base.OnMouseLeave (e); }

			protected override void OnKeyDown (KeyEventArgs e)
			{
				if (e.KeyCode is Keys.Space or Keys.Enter)
					OnClick (EventArgs.Empty);
				base.OnKeyDown (e);
			}

			protected override void OnPaint (PaintEventArgs e)
			{
				Graphics g = e.Graphics;
				g.Clear (SystemColors.Control);
				SmoothingMode smoothing = g.SmoothingMode;
				g.SmoothingMode = SmoothingMode.AntiAlias;
				int d = Height - 6, cx = 3 + d / 2, cy = Height / 2;
				using (var circle = new Pen (_hot ? Color.FromArgb (0, 120, 215) : SystemColors.ControlDark))
					g.DrawEllipse (circle, 3, 3, d, d);
				int half = d / 4, rise = _expanded ? -half / 2 : half / 2;
				using (var pen = new Pen (SystemColors.ControlText, 1.5f))
					g.DrawLines (pen, new [] { new Point (cx - half, cy - rise), new Point (cx, cy + rise), new Point (cx + half, cy - rise) });
				g.SmoothingMode = smoothing;
				TextRenderer.DrawText (g, Text, Font, new Rectangle (d + 10, 0, Width - d - 10, Height), SystemColors.ControlText, TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
				if (Focused)
					ControlPaint.DrawFocusRectangle (g, new Rectangle (d + 8, 2, Width - d - 10, Height - 4));
			}
		}

		/// <summary>The progress bar comctl32's task dialog shows: green, yellow paused, red for an error, or a marquee.</summary>
		private sealed class TaskProgress : Control
		{
			private readonly System.Windows.Forms.Timer _marquee = new System.Windows.Forms.Timer ();
			private int _min, _max = 100, _value, _offset;
			private TaskDialogProgressBarState _state;

			public TaskProgress ()
			{
				SetStyle (ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
				_marquee.Tick += (s, e) => { _offset = (_offset + 4) % Math.Max (1, Width + Width / 3); Invalidate (); };
			}

			public void Apply (TaskDialogProgressBar bar)
			{
				_min = bar.Minimum;
				_max = bar.Maximum;
				_value = bar.Value;
				_state = bar.State;
				_marquee.Interval = bar.MarqueeSpeed > 0 ? bar.MarqueeSpeed : 30;
				_marquee.Enabled = _state == TaskDialogProgressBarState.Marquee;
				Invalidate ();
			}

			protected override void Dispose (bool disposing)
			{
				if (disposing)
					_marquee.Dispose ();
				base.Dispose (disposing);
			}

			protected override void OnPaint (PaintEventArgs e)
			{
				Graphics g = e.Graphics;
				Rectangle r = new Rectangle (0, 0, Width - 1, Height - 1);
				using (var back = new SolidBrush (Color.FromArgb (230, 230, 230)))
					g.FillRectangle (back, r);
				Color fill = _state switch {
					TaskDialogProgressBarState.Paused or TaskDialogProgressBarState.MarqueePaused => Color.FromArgb (218, 203, 38),
					TaskDialogProgressBarState.Error => Color.FromArgb (218, 38, 38),
					_ => Color.FromArgb (6, 176, 37),
				};
				using (var brush = new SolidBrush (fill)) {
					if (_state is TaskDialogProgressBarState.Marquee or TaskDialogProgressBarState.MarqueePaused) {
						int w = Width / 3;
						g.FillRectangle (brush, new Rectangle (_offset - w, 1, w, Height - 2));
					} else if (_max > _min) {
						int w = (int) ((long) (Width - 2) * (_value - _min) / (_max - _min));
						g.FillRectangle (brush, new Rectangle (1, 1, w, Height - 2));
					}
				}
				using (var edge = new Pen (Color.FromArgb (188, 188, 188)))
					g.DrawRectangle (edge, r);
			}
		}
	}
}
