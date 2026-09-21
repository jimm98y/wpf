// Permission is hereby granted, free of charge, to any person obtaining
// a copy of this software and associated documentation files (the
// "Software"), to deal in the Software without restriction, including
// without limitation the rights to use, copy, modify, merge, publish,
// distribute, sublicense, and/or sell copies of the Software, and to
// permit persons to whom the Software is furnished to do so, subject to
// the following conditions:
//
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
// MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
// LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
// OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
// WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
//
// Copyright (c) 2004-2006 Novell, Inc.
//
// Authors:
//	Jordi Mas i Hernandez	(jordi@ximian.com)
//	Benjamin Dasnois	(benjamin.dasnois@gmail.com)
//	Robert Thompson		(rmt@corporatism.org)
//	Peter Bartok		(pbartok@novell.com)
//
// TODO:
//	- Add support for MessageBoxOptions and MessageBoxDefaultButton.
//


// NOT COMPLETE

using System;
using System.Drawing;
using System.Globalization;
using System.Resources;

namespace System.Windows.Forms
{
	public class MessageBox
	{
		#region Private MessageBoxForm class
		internal class MessageBoxForm : Form
		{
			#region MessageBoxFrom Local Variables
			// MEASURED off three real Windows message boxes (a warning with Yes/No/Cancel, an
			// information with OK, and one with no icon at all), because the shape of this dialog is
			// not the shape of a form full of controls: Windows gives it a WHITE message area with a
			// grey FOOTER under it, and right-aligns the buttons in that footer.
			//
			// The three agreed on every one of these: the footer is 42 tall whatever is above it, the
			// buttons are 73x21 sitting 11 down from the top of the footer, the last one ends 16 from
			// the right edge, they are pitched 83 apart, and the icon is a 32x32 box 21 in from the
			// left, centred in the white area. Client sizes 371x120, 206x120 and 195x101 fall out of
			// those numbers rather than being fitted to.
			const int space_border = 10;
			const int button_width = 73;
			const int button_height = 21;
			const int button_space = 10;              // gap between buttons: pitch is width + this
			const int button_right_margin = 16;       // from the last button's right edge to the frame
			const int footer_height = 42;
			const int button_top_in_footer = 11;
			const int icon_left = 21;
			const int icon_box = 32;
			const int icon_text_gap = 12;
			const int white_pad = 23;                 // above and below the message area's content
			const int space_image_text= 10;

			string			msgbox_text;
			bool			size_known	= false;
			Icon			icon_image;
			RectangleF		text_rect;
			MessageBoxButtons	msgbox_buttons;
			MessageBoxDefaultButton	msgbox_default;
			bool			buttons_placed	= false;
			int			button_left;
			Button[]		buttons = new Button[4];
			bool                    show_help;
			string help_file_path;
			string help_keyword;
			HelpNavigator help_navigator;
			object help_param;
			AlertType		alert_type;
			#endregion	// MessageBoxFrom Local Variables
			
			#region MessageBoxForm Constructors
			public MessageBoxForm (IWin32Window owner, string text, string caption,
					       MessageBoxButtons buttons, MessageBoxIcon icon,
					       bool displayHelpButton)
			{
				show_help = displayHelpButton;

				switch (icon) {
					case MessageBoxIcon.None: {
						icon_image = null;
						alert_type = AlertType.Default;
						break;
					}

					case MessageBoxIcon.Error: {		// Same as MessageBoxIcon.Hand and MessageBoxIcon.Stop
						icon_image = SystemIcons.Error;
						alert_type = AlertType.Error;
						break;
					}

					case MessageBoxIcon.Question: {
 						icon_image = SystemIcons.Question;
						alert_type = AlertType.Question;
						break;
					}

					case MessageBoxIcon.Asterisk: {		// Same as MessageBoxIcon.Information
						icon_image = SystemIcons.Information;
						alert_type = AlertType.Information;
						break;
					}

					case MessageBoxIcon.Warning: {		// Same as MessageBoxIcon.Exclamation:
						icon_image = SystemIcons.Warning;
						alert_type = AlertType.Warning;
						break;
					}
				}

				msgbox_text = text;
				msgbox_buttons = buttons;
				msgbox_default = MessageBoxDefaultButton.Button1;

				if (owner != null) {
					Owner = Control.FromHandle(owner.Handle).FindForm();
				} else {
					if (Application.MWFThread.Current.Context != null) {
						Owner = Application.MWFThread.Current.Context.MainForm;
					}
				}
				this.Text = caption;
				this.ControlBox = true;
				this.MinimizeBox = false;
				this.MaximizeBox = false;
				this.ShowInTaskbar = (Owner == null);
				this.FormBorderStyle = FormBorderStyle.FixedDialog;
			}

			public MessageBoxForm (IWin32Window owner, string text, string caption,
					MessageBoxButtons buttons, MessageBoxIcon icon,
					MessageBoxDefaultButton defaultButton, MessageBoxOptions options, bool displayHelpButton)
				: this (owner, text, caption, buttons, icon, displayHelpButton)
			{
				msgbox_default = defaultButton;
			}

			public MessageBoxForm (IWin32Window owner, string text, string caption,
					       MessageBoxButtons buttons, MessageBoxIcon icon)
				: this (owner, text, caption, buttons, icon, false)
			{
			}
			#endregion	// MessageBoxForm Constructors

			#region Protected Instance Properties
			protected override CreateParams CreateParams {
				get {
					CreateParams cp = base.CreateParams;;

					cp.Style |= (int)(WindowStyles.WS_DLGFRAME | WindowStyles.WS_CLIPCHILDREN | WindowStyles.WS_CLIPSIBLINGS | WindowStyles.WS_CAPTION);
					
					if (!is_enabled)
						cp.Style |= (int)(WindowStyles.WS_DISABLED);

					return cp;
				}
			}
			#endregion	// Protected Instance Properties

			#region MessageBoxForm Methods
			public void SetHelpData (string file_path, string keyword, HelpNavigator navigator, object param)
			{
				help_file_path = file_path;
				help_keyword = keyword;
				help_navigator = navigator;
				help_param = param;
			}
			
			internal string HelpFilePath {
				get { return help_file_path; }
			}
			
			internal string HelpKeyword {
				get { return help_keyword; }
			}
			
			internal HelpNavigator HelpNavigator {
				get { return help_navigator; }
			}
			
			internal object HelpParam {
				get { return help_param; }
			}
			
			public DialogResult RunDialog ()
			{
				this.StartPosition = FormStartPosition.CenterScreen;

				if (size_known == false) {
					InitFormsSize ();
				}

				if (Owner != null)
					TopMost = Owner.TopMost;
					
				XplatUI.AudibleAlert (alert_type);
				this.ShowDialog ();

				return this.DialogResult;
			}

			internal override void OnPaintInternal (PaintEventArgs e)
			{
				// The message sits on WHITE and the buttons on the form's own grey; the join is what
				// makes this look like a Windows message box rather than a small grey form.
				int white = ClientSize.Height - footer_height;
				if (white > 0)
					e.Graphics.FillRectangle (ThemeEngine.Current.ResPool.GetSolidBrush (Color.White),
					                          0, 0, ClientSize.Width, white);

				e.Graphics.DrawString (msgbox_text, this.Font, ThemeEngine.Current.ResPool.GetSolidBrush (SystemColors.ControlText), text_rect);
				if (icon_image != null)
					e.Graphics.DrawIcon (icon_image, new Rectangle (icon_left, (white - icon_box) / 2,
					                                               icon_box, icon_box));
			}

			private void InitFormsSize ()
			{
				int tb_width = 0;

				// Max width of messagebox must be 60% of screen width
				int max_width = (int) (Screen.GetWorkingArea (this).Width * 0.6);
				if (max_width > 500) {
					float dx;
					using (Graphics g = this.CreateGraphics ()) {
						dx = g.DpiX;
					}
					int new_max_width = (int) (dx * 5.0);	// aim for text no wider than 5.0 inches
					if (new_max_width < max_width)
						max_width = new_max_width;
				}
				// First we have to know the size of text + image
				int iconImageWidth = 0;
				if (icon_image != null)
					iconImageWidth = icon_left + icon_box + icon_text_gap;
				// NoPadding: MeasureText otherwise adds a margin of its own, and this measurement is used to
				// SIZE the dialog -- so that margin lands on top of the padding the layout already adds
				// and every message box comes out wider than Windows' by it.
				Drawing.SizeF tsize = TextRenderer.MeasureText (msgbox_text, this.Font, new Size (max_width - iconImageWidth, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);

				// The MESSAGE AREA is one block -- icon beside text -- centred in its own white band,
				// which is what Windows does and what the old arithmetic did not: it pinned the text to
				// the top and dropped the icon below it. Keep the raw text height; padding is added
				// once, here, rather than folded into the measurement and then added again.
				float raw_text_h = tsize.Height;
				int content_h = Math.Max (icon_image != null ? icon_box : 0, (int) raw_text_h);
				int white_h = content_h + white_pad * 2;

				text_rect = new RectangleF ();
				text_rect.Height = raw_text_h;
				text_rect.X = icon_image != null ? icon_left + icon_box + icon_text_gap : white_pad;
				text_rect.Y = (white_h - raw_text_h) / 2f;

				// The WIDTH the message needs is where the text starts plus the text plus the same
				// padding on the right. Adding a separate border afterwards, as this used to, does not
				// know where the text was actually put -- which is how the no-icon box came out
				// narrower than its own text and clipped the last two words off it.
				tsize.Width = text_rect.X + tsize.Width + white_pad;
				tsize.Height = white_h;

				// Now we want to know the amount of buttons
				int buttoncount;
				switch (msgbox_buttons) {
					case MessageBoxButtons.OK:
						buttoncount = 1;
						break;

					case MessageBoxButtons.OKCancel:
						buttoncount = 2;
						break;

					case MessageBoxButtons.AbortRetryIgnore:
						buttoncount = 3;
						break;

					case MessageBoxButtons.YesNoCancel:
						buttoncount = 3;
						break;

					case MessageBoxButtons.YesNo:
						buttoncount = 2;
						break;

					case MessageBoxButtons.RetryCancel:
						buttoncount = 2;
						break;
					
					default:
						buttoncount = 0;
						break;
				
				}
				if (show_help)
					buttoncount ++;
				
				// Calculate the width based on amount of buttons: the pitch for all but the last,
				// then the last one's own width and the margin on either side of the group.
				tb_width = (button_width + button_space) * buttoncount - button_space
				           + button_right_margin * 2;

				// The form caption can also make us bigger
				SizeF caption = TextRenderer.MeasureString (Text, new Font (DefaultFont, FontStyle.Bold));
				
				// Use the bigger of the caption size (plus some arbitrary borders/close button)
				// or the text size, up to 60% of the screen (max_size)
				Size new_size = new SizeF (Math.Min (Math.Max (caption.Width + 40, tsize.Width), max_width), tsize.Height).ToSize ();
				
				this.ClientSize = new Size (Math.Max (new_size.Width, tb_width), white_h + footer_height);

				// SIZED from the unpadded measurement, DRAWN with the padded one. They are different
				// numbers and each is right for its own job: sizing to the padded width makes every
				// box wider than Windows', and drawing into the unpadded width clips the last word.
				text_rect.Width = ClientSize.Width - text_rect.X - white_pad
				                  + (TextRenderer.MeasureText (msgbox_text, this.Font).Width
				                     - TextRenderer.MeasureText (msgbox_text, this.Font,
				                           new Size (int.MaxValue, int.MaxValue),
				                           TextFormatFlags.NoPadding).Width);

				// The LAST button ends button_right_margin from the frame; the rest step back from it.
				button_left = this.ClientSize.Width - button_right_margin
				              - (button_width + button_space) * buttoncount + button_space;
				AddButtons ();
				size_known = true;

				// Still needs to implement defaultButton and options
				switch(msgbox_default) {
					case MessageBoxDefaultButton.Button2: {
						if (this.buttons[1] != null) {
							ActiveControl = this.buttons[1];
						}
						break;
					}

					case MessageBoxDefaultButton.Button3: {
						if (this.buttons[2] != null) {
							ActiveControl = this.buttons[2];
						}
						break;
					}
				}
			}

			protected override bool ProcessDialogKey(Keys keyData) {
				if (keyData == Keys.Escape) {
					this.CancelClick(this, null);
					return true;
				}

				if (((keyData & Keys.Modifiers) == Keys.Control) &&
					(((keyData & Keys.KeyCode) == Keys.C) ||
					 ((keyData & Keys.KeyCode) == Keys.Insert))) {  
					Copy();
				}

				return base.ProcessDialogKey (keyData);
			}

			protected override bool ProcessDialogChar (char charCode)
			{
				// Shortcut keys, kinda like mnemonics, except you don't have to press Alt
				if ((charCode == 'N' || charCode == 'n') && (CancelButton != null && (CancelButton as Button).Text == "No"))
					CancelButton.PerformClick ();
				else if ((charCode == 'Y' || charCode == 'y') && (AcceptButton as Button).Text == "Yes")
					AcceptButton.PerformClick ();
				else if ((charCode == 'A' || charCode == 'a') && (CancelButton != null && (CancelButton as Button).Text == "Abort"))
					CancelButton.PerformClick ();
				else if ((charCode == 'R' || charCode == 'r') && (AcceptButton as Button).Text == "Retry")
					AcceptButton.PerformClick ();
				else if ((charCode == 'I' || charCode == 'i') && buttons.Length >= 3 && buttons[2].Text == "Ignore")
					buttons[2].PerformClick ();
				
				return base.ProcessDialogChar (charCode);
			}
			
			private void Copy ()
			{
				string separator = "---------------------------" + Environment.NewLine;

				System.Text.StringBuilder contents = new System.Text.StringBuilder ();

				contents.Append (separator);
				contents.Append (this.Text).Append (Environment.NewLine);
				contents.Append (separator);
				contents.Append (msgbox_text).Append (Environment.NewLine);
				contents.Append (separator);

				foreach (Button btn in buttons) {
					if (btn == null)
						break;
					contents.Append (btn.Text).Append ("   ");;
				}

				contents.Append (Environment.NewLine);
				contents.Append (separator);

				DataObject obj = new DataObject(DataFormats.Text, contents.ToString());
				Clipboard.SetDataObject (obj);
			}

			#endregion	// MessageBoxForm Methods

			#region Functions for Adding buttons
			private void AddButtons()
			{
				if (!buttons_placed) {
					switch (msgbox_buttons) {
						case MessageBoxButtons.OK: {
							buttons[0] = AddOkButton (0);
							break;
						}

						case MessageBoxButtons.OKCancel: {
							buttons[0] = AddOkButton (0);
							buttons[1] = AddCancelButton (1);
							break;
						}

						case MessageBoxButtons.AbortRetryIgnore: {
							buttons[0] = AddAbortButton (0);
							buttons[1] = AddRetryButton (1);
							buttons[2] = AddIgnoreButton (2);
							break;
						}

						case MessageBoxButtons.YesNoCancel: {
							buttons[0] = AddYesButton (0);
							buttons[1] = AddNoButton (1);
							buttons[2] = AddCancelButton (2);
							break;
						}

						case MessageBoxButtons.YesNo: {
							buttons[0] = AddYesButton (0);
							buttons[1] = AddNoButton (1);
							break;
						}

						case MessageBoxButtons.RetryCancel: {
							buttons[0] = AddRetryButton (0);
							buttons[1] = AddCancelButton (1);
							break;
						}
					}

					if (show_help) {
						for (int i = 0; i <= 3; i++) {
							if (buttons [i] == null) {
								AddHelpButton (i);
								break;
							}
						}
					}

					buttons_placed = true;
				}
			}

			private Button AddButton (string text, int left, EventHandler click_event)
			{
				Button button = new Button ();
				button.Text = Locale.GetText(text);
				button.Width = button_width;
				button.Height = button_height;
				// Right-aligned in the footer, counting back from the last one. The old arithmetic
				// centred them, which is what a grey form does and not what Windows does.
				button.Top = this.ClientSize.Height - footer_height + button_top_in_footer;
				button.Left = button_left + (button_width + button_space) * left;
				
				if (click_event != null)
					button.Click += click_event;
				
				if ((text == "OK") || (text == "Retry") || (text == "Yes")) 
					AcceptButton = button;
				else if ((text == "Cancel") || (text == "Abort") || (text == "No"))
					CancelButton = button;
				
				this.Controls.Add (button);

				return button;
			}

			private Button AddOkButton (int left)
			{
				return AddButton ("OK", left, new EventHandler (OkClick));
			}

			private Button AddCancelButton (int left)
			{
				return AddButton ("Cancel", left, new EventHandler (CancelClick));
			}

			private Button AddAbortButton (int left)
			{
				return AddButton ("Abort", left, new EventHandler (AbortClick));
			}

			private Button AddRetryButton(int left)
			{
				return AddButton ("Retry", left, new EventHandler (RetryClick));
			}

			private Button AddIgnoreButton (int left)
			{
				return AddButton ("Ignore", left, new EventHandler (IgnoreClick));
			}

			private Button AddYesButton (int left)
			{
				return AddButton ("Yes", left, new EventHandler (YesClick));
			}

			private Button AddNoButton (int left)
			{
				return AddButton ("No", left, new EventHandler (NoClick));
			}

			private Button AddHelpButton (int left)
			{
				Button button = AddButton ("Help", left, null);
				button.Click += delegate { Owner.RaiseHelpRequested (new HelpEventArgs (Owner.Location)); };
				return button;
			}
			#endregion

			#region Button click handlers
			private void OkClick (object sender, EventArgs e)
			{
				this.DialogResult = DialogResult.OK;
				this.Close ();
			}

			private void CancelClick (object sender, EventArgs e)
			{
				this.DialogResult = DialogResult.Cancel;
				this.Close ();
			}

			private void AbortClick (object sender, EventArgs e)
			{
				this.DialogResult = DialogResult.Abort;
				this.Close ();
			}

			private void RetryClick (object sender, EventArgs e)
			{
				this.DialogResult = DialogResult.Retry;
				this.Close ();
			}

			private void IgnoreClick (object sender, EventArgs e)
			{
				this.DialogResult = DialogResult.Ignore;
				this.Close ();
			}

			private void YesClick (object sender, EventArgs e)
			{
				this.DialogResult = DialogResult.Yes;
				this.Close ();
			}

			private void NoClick (object sender, EventArgs e)
			{
				this.DialogResult = DialogResult.No;
				this.Close ();
			}
			#endregion

			#region UIA Framework: Methods, Properties and Events

			internal string UIAMessage {
				get { return msgbox_text; }
			}

			internal Rectangle UIAMessageRectangle {
				get { 
					return new Rectangle ((int) text_rect.X,
					                      (int) text_rect.Y, 
					                      (int) text_rect.Width, 
					                      (int) text_rect.Height); 
				}
			}

			internal Rectangle UIAIconRectangle {
				get { 
					return new Rectangle (space_border, 
					                      space_border, 
							      icon_image == null ? -1 : icon_image.Width, 
							      icon_image == null ? -1 : icon_image.Height);
				}
			}

			#endregion
		}
		#endregion	// Private MessageBoxForm class


		#region	Constructors
		private MessageBox ()
		{
		}
		#endregion	// Constructors

		#region Public Static Methods
		public static DialogResult Show (string text)
		{
			MessageBoxForm form = new MessageBoxForm (null, text, string.Empty, MessageBoxButtons.OK, MessageBoxIcon.None);

			return form.RunDialog ();
		}

		public static DialogResult Show (IWin32Window owner, string text)
		{
			MessageBoxForm form = new MessageBoxForm (owner, text, string.Empty, MessageBoxButtons.OK, MessageBoxIcon.None);
				
			return form.RunDialog ();
		}

		public static DialogResult Show (string text, string caption)
		{
			MessageBoxForm form = new MessageBoxForm (null, text, caption, MessageBoxButtons.OK, MessageBoxIcon.None);

			return form.RunDialog ();
		}

		public static DialogResult Show (string text, string caption, MessageBoxButtons buttons)
		{
			MessageBoxForm form = new MessageBoxForm (null, text, caption, buttons, MessageBoxIcon.None);
				
			return form.RunDialog ();
		}

		public static DialogResult Show (IWin32Window owner, string text, string caption,
						 MessageBoxButtons buttons)
		{
			MessageBoxForm form = new MessageBoxForm (owner, text, caption, buttons, MessageBoxIcon.None);
				
			return form.RunDialog ();
		}

		public static DialogResult Show (IWin32Window owner, string text, string caption,
						 MessageBoxButtons buttons, MessageBoxIcon icon)
		{
			MessageBoxForm form = new MessageBoxForm (owner, text, caption, buttons, icon);
				
			return form.RunDialog ();
		}


		public static DialogResult Show (IWin32Window owner, string text, string caption)
		{
			MessageBoxForm form = new MessageBoxForm (owner, text, caption, MessageBoxButtons.OK, MessageBoxIcon.None);
				
			return form.RunDialog ();
		}


		public static DialogResult Show (string text, string caption, MessageBoxButtons buttons,
				MessageBoxIcon icon)
		{
			MessageBoxForm form = new MessageBoxForm (null, text, caption, buttons, icon);
				
			return form.RunDialog ();
		}

		public static DialogResult Show (string text, string caption, MessageBoxButtons buttons,
						 MessageBoxIcon icon, MessageBoxDefaultButton defaultButton)
		{

			MessageBoxForm form = new MessageBoxForm (null, text, caption, buttons,
								  icon, defaultButton, MessageBoxOptions.DefaultDesktopOnly, false);
				
			return form.RunDialog ();
		}

		public static DialogResult Show (IWin32Window owner, string text, string caption,
						 MessageBoxButtons buttons, MessageBoxIcon icon,
						 MessageBoxDefaultButton defaultButton)
		{
			MessageBoxForm form = new MessageBoxForm (owner, text, caption, buttons,
								  icon, defaultButton, MessageBoxOptions.DefaultDesktopOnly, false);
				
			return form.RunDialog ();
		}

		public static DialogResult Show (string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon,
						 MessageBoxDefaultButton defaultButton, MessageBoxOptions options)
		{
			MessageBoxForm form = new MessageBoxForm (null, text, caption, buttons,
								  icon, defaultButton, options, false);
				
			return form.RunDialog ();
		}

		public static DialogResult Show (IWin32Window owner, string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon,
						 MessageBoxDefaultButton defaultButton, MessageBoxOptions options)
		{
			MessageBoxForm form = new MessageBoxForm (owner, text, caption, buttons,
								  icon, defaultButton, options, false);
				
			return form.RunDialog ();
		}
		#endregion	// Public Static Methods

		public static DialogResult Show (string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon,
						 MessageBoxDefaultButton defaultButton, MessageBoxOptions options,
						 bool displayHelpButton)
		{
			MessageBoxForm form = new MessageBoxForm (null, text, caption, buttons,
								  icon, defaultButton, options, displayHelpButton);
			return form.RunDialog ();
		}
		
		[MonoTODO ("Help is not implemented")]
		public static DialogResult Show (string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon,
						 MessageBoxDefaultButton defaultButton, MessageBoxOptions options,
						 string helpFilePath)
		{
			MessageBoxForm form = new MessageBoxForm (null, text, caption, buttons,
								  icon, defaultButton, options, true);
			form.SetHelpData (helpFilePath, null, HelpNavigator.TableOfContents, null);
			return form.RunDialog ();
		}
		
		[MonoTODO ("Help is not implemented")]
		public static DialogResult Show (string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon,
						 MessageBoxDefaultButton defaultButton, MessageBoxOptions options,
						 string helpFilePath, string keyword)
		{
			MessageBoxForm form = new MessageBoxForm (null, text, caption, buttons,
								  icon, defaultButton, options, true);
			form.SetHelpData (helpFilePath, keyword, HelpNavigator.TableOfContents, null);
			return form.RunDialog ();
		}
		
		[MonoTODO ("Help is not implemented")]
		public static DialogResult Show (string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon,
						 MessageBoxDefaultButton defaultButton, MessageBoxOptions options,
						 string helpFilePath, HelpNavigator navigator)
		{
			MessageBoxForm form = new MessageBoxForm (null, text, caption, buttons,
								  icon, defaultButton, options, true);
			form.SetHelpData (helpFilePath, null, navigator, null);
			return form.RunDialog ();
		}
		
		[MonoTODO ("Help is not implemented")]
		public static DialogResult Show (string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon,
						 MessageBoxDefaultButton defaultButton, MessageBoxOptions options,
						 string helpFilePath, HelpNavigator navigator, object param)
		{
			MessageBoxForm form = new MessageBoxForm (null, text, caption, buttons,
								  icon, defaultButton, options, true);
			form.SetHelpData (helpFilePath, null, navigator, param);
			return form.RunDialog ();
		}
		
		[MonoTODO ("Help is not implemented")]
		public static DialogResult Show (IWin32Window owner, string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon,
						 MessageBoxDefaultButton defaultButton, MessageBoxOptions options,
						 string helpFilePath)
		{
			MessageBoxForm form = new MessageBoxForm (owner, text, caption, buttons,
								  icon, defaultButton, options, true);
			form.SetHelpData (helpFilePath, null, HelpNavigator.TableOfContents, null);
			return form.RunDialog ();
		}
		
		[MonoTODO ("Help is not implemented")]
		public static DialogResult Show (IWin32Window owner, string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon,
						 MessageBoxDefaultButton defaultButton, MessageBoxOptions options,
						 string helpFilePath, string keyword)
		{
			MessageBoxForm form = new MessageBoxForm (owner, text, caption, buttons,
								  icon, defaultButton, options, true);
			form.SetHelpData (helpFilePath, keyword, HelpNavigator.TableOfContents, null);
			return form.RunDialog ();
		}
		
		[MonoTODO ("Help is not implemented")]
		public static DialogResult Show (IWin32Window owner, string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon,
						 MessageBoxDefaultButton defaultButton, MessageBoxOptions options,
						 string helpFilePath, HelpNavigator navigator)
		{
			MessageBoxForm form = new MessageBoxForm (owner, text, caption, buttons,
								  icon, defaultButton, options, true);
			form.SetHelpData (helpFilePath, null, navigator, null);
			return form.RunDialog ();
		}
		
		[MonoTODO ("Help is not implemented")]
		public static DialogResult Show (IWin32Window owner, string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon,
						 MessageBoxDefaultButton defaultButton, MessageBoxOptions options,
						 string helpFilePath, HelpNavigator navigator, object param)
		{
			MessageBoxForm form = new MessageBoxForm (owner, text, caption, buttons,
								  icon, defaultButton, options, true);
			form.SetHelpData (helpFilePath, null, navigator, param);
			return form.RunDialog ();
		}
	}
}

