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
// Copyright (c) 2006, Alexander Olk
//
// Authors:
//	Alexander Olk	alex.olk@googlemail.com
//

// The managed font dialog, laid out and behaving as Windows' own (comdlg32's FORMATDLGORD31
// template, as Windows 11 shows it). It is the only font dialog the port has, on every platform.
//
// What Windows' dialog is made of, read off the running dialog (control ids, rectangles at 96 DPI,
// styles), and reproduced here control for control:
//   stc1 "&Font:"        11,11  60x15     cmb1  11,26 147x119  CBS_SIMPLE, owner-drawn, sorted
//   stc2 "Font st&yle:" 165,11  66x15     cmb2 165,26 111x124  CBS_SIMPLE, owner-drawn
//   stc3 "&Size:"       284,11  45x15     cmb3 285,26  54x115  CBS_SIMPLE, owner-drawn
//   grp1 "Effects"       11,158 147x117   chx1 "Stri&keout" 20,179  chx2 "&Underline" 20,200
//   stc4 "&Color:"       20,221 (hidden)  cmb4 20,237 123x20 (hidden unless ShowColor)
//   grp2 "Sample"       165,158 174x70    stc5 (the sample's rectangle) 177,180 150x37
//   stc7 "Sc&ript:"     165,239 45x15     cmb5 165,255 174x20  CBS_DROPDOWNLIST
//   stc6 (description)   11,280 329x33    IDOK 347,26 68x23  IDCANCEL 347,52  Apply 347,78  Help 347,104
// in a 431x319 client, in the dialog font (MS Shell Dlg, 8 point). The three lists are simple
// combo boxes -- an edit field over a list that is always open -- as Windows' are; the font and
// style lists draw each entry in the face it names.

using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System;

namespace System.Windows.Forms
{
	[DefaultProperty( "Font" )]
	[DefaultEvent("Apply")]
	public class FontDialog : CommonDialog
	{
		protected static readonly object EventApply = new object ();

		private Font font;
		private Color color = Color.Black;
		private bool allowSimulations = true;
		private bool allowVectorFonts = true;
		private bool allowVerticalFonts = true;
		private bool allowScriptChange = true;
		private bool fixedPitchOnly = false;
		private int maxSize = 0;
		private int minSize = 0;
		private bool scriptsOnly = false;
		private bool showApply = false;
		private bool showColor = false;
		private bool showEffects = true;
		private bool showHelp = false;
		private bool fontMustExist = false;

		private Label fontLabel, styleLabel, sizeLabel, scriptLabel, colorLabel, descriptionLabel;
		private ComboBox fontCombo, styleCombo, sizeCombo, scriptCombo;
		private ColorComboBox colorCombo;
		private GroupBox effectsGroup, sampleGroup;
		private CheckBox strikeoutCheck, underlineCheck;
		private SampleBox sample;
		private Button okButton, cancelButton, applyButton, helpButton;

		// The families on offer, and the one being shown.
		private List<FontDialogFamily> families;
		private FontDialogFamily currentFamily;
		private FontDialogFace currentFace;
		private float currentSize = 9;
		private bool internal_change;

		// The ladder Windows offers for a scalable face.
		private static readonly int [] a_sizes = { 8, 9, 10, 11, 12, 14, 16, 18, 20, 22, 24, 26, 28, 36, 48, 72 };

		#region Public Constructors
		public FontDialog ()
		{
			form = new DialogForm (this);
			form.SuspendLayout ();

			form.Text = "Font";
			form.FormBorderStyle = FormBorderStyle.FixedDialog;
			form.MinimizeBox = false;
			form.MaximizeBox = false;
			form.ShowIcon = false;
			form.ShowInTaskbar = false;
			form.StartPosition = FormStartPosition.CenterScreen;
			form.AutoScaleMode = AutoScaleMode.None;
			form.Font = DialogFont;
			form.ClientSize = new Size (431, 319);

			fontLabel = MakeLabel ("&Font:", 11, 11, 60, 15);
			styleLabel = MakeLabel ("Font st&yle:", 165, 11, 66, 15);
			sizeLabel = MakeLabel ("&Size:", 284, 11, 45, 15);
			scriptLabel = MakeLabel ("Sc&ript:", 165, 239, 45, 15);
			colorLabel = MakeLabel ("&Color:", 20, 221, 45, 15);
			descriptionLabel = MakeLabel ("", 11, 280, 329, 33);
			descriptionLabel.UseMnemonic = false;

			fontCombo = MakeList (11, 26, 147, 119, 19);
			fontCombo.DrawItem += OnDrawFontItem;
			// Windows' combo window is 124 high, its list cut to whole items (99): the same picture.
			styleCombo = MakeList (165, 26, 111, 119, 19);
			styleCombo.DrawItem += OnDrawStyleItem;
			sizeCombo = MakeList (285, 26, 54, 115, 13);
			sizeCombo.DrawItem += OnDrawSizeItem;

			effectsGroup = new GroupBox { Text = "Effects", Location = new Point (11, 158), Size = new Size (147, 117), TabStop = false };
			sampleGroup = new GroupBox { Text = "Sample", Location = new Point (165, 158), Size = new Size (174, 70), TabStop = false };
			strikeoutCheck = new SystemCheckBox { Text = "Stri&keout", Location = new Point (20, 179), Size = new Size (74, 16) };
			underlineCheck = new SystemCheckBox { Text = "&Underline", Location = new Point (20, 200), Size = new Size (77, 16) };
			colorCombo = new ColorComboBox (this) { Location = new Point (20, 237), Size = new Size (123, 20) };
			sample = new SampleBox (this) { Location = new Point (177, 180), Size = new Size (150, 37) };

			// Owner-drawn in Windows' dialog too, which is what makes it 20 high rather than 21.
			scriptCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, DrawMode = DrawMode.OwnerDrawFixed,
				ItemHeight = 14, SelectionItemHeight = 14, Location = new Point (165, 255), Size = new Size (174, 20) };
			scriptCombo.DrawItem += OnDrawScriptItem;

			okButton = MakeButton ("OK", 26);
			cancelButton = MakeButton ("Cancel", 52);
			applyButton = MakeButton ("&Apply", 78);
			helpButton = MakeButton ("&Help", 104);

			// Windows' tab order, which is also its z-order: the group boxes come after what they
			// frame, so they are drawn beneath it.
			Control [] order = { fontLabel, fontCombo, styleLabel, styleCombo, sizeLabel, sizeCombo,
				strikeoutCheck, underlineCheck, colorLabel, colorCombo, sample, effectsGroup, sampleGroup,
				descriptionLabel, scriptLabel, scriptCombo, okButton, cancelButton, applyButton, helpButton };
			for (int i = 0; i < order.Length; i++)
				order [i].TabIndex = i;
			form.Controls.AddRange (order);

			form.AcceptButton = okButton;
			form.CancelButton = cancelButton;
			okButton.DialogResult = DialogResult.OK;
			cancelButton.DialogResult = DialogResult.Cancel;

			applyButton.Hide ();
			helpButton.Hide ();
			colorLabel.Hide ();
			colorCombo.Hide ();

			form.ResumeLayout (false);

			fontCombo.SelectedIndexChanged += OnFontSelected;
			styleCombo.SelectedIndexChanged += OnStyleSelected;
			sizeCombo.SelectedIndexChanged += OnSizeSelected;
			scriptCombo.SelectedIndexChanged += OnScriptSelected;
			fontCombo.TextUpdate += OnFontTyped;
			styleCombo.TextUpdate += OnStyleTyped;
			sizeCombo.TextUpdate += OnSizeTyped;
			strikeoutCheck.CheckedChanged += (s, e) => sample.Invalidate ();
			underlineCheck.CheckedChanged += (s, e) => sample.Invalidate ();
			applyButton.Click += OnApplyButton;
			form.FormClosed += (s, e) => {
				if (form.DialogResult == DialogResult.OK)
					BuildResult ();
			};

			PopulateFontList ();
			CreateFontSizeListItems ();
		}
		#endregion	// Public Constructors

		/// <summary>The dialog font: MS Shell Dlg at 8 point, which Windows maps to Microsoft Sans
		/// Serif (FontSubstitutes). Asked for by its real name so every platform gets the same face
		/// where it is installed, and its fallback where it is not.</summary>
		private static Font DialogFont => new Font ("Microsoft Sans Serif", 8.25f);

		private Label MakeLabel (string text, int x, int y, int w, int h)
			=> new StaticText { Text = text, Location = new Point (x, y), Size = new Size (w, h), AutoSize = false };

		/// <summary>A BS_AUTOCHECKBOX as comctl32 v6 draws it: the themed box at the left, centred
		/// down the control, and the caption three pixels after it -- DrawText(DT_VCENTER |
		/// DT_SINGLELINE) with no padding, which the standard adapter adds.</summary>
		private sealed class SystemCheckBox : CheckBox
		{
			protected override void OnPaint (PaintEventArgs e)
			{
				using (var b = new SolidBrush (BackColor))
					e.Graphics.FillRectangle (b, ClientRectangle);
				var state = Enabled
					? (Checked ? VisualStyles.CheckBoxState.CheckedNormal : VisualStyles.CheckBoxState.UncheckedNormal)
					: (Checked ? VisualStyles.CheckBoxState.CheckedDisabled : VisualStyles.CheckBoxState.UncheckedDisabled);
				if (Enabled && MouseIsDown)
					state += 2;
				else if (Enabled && MouseIsOver)
					state += 1;
				CheckBoxRenderer.DrawCheckBox (e.Graphics, new Point (0, (Height - 13) / 2), state);
				var text = new Rectangle (16, 0, Width - 16, Height);
				TextFormatFlags flags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding;
				if (!ShowKeyboardCues)
					flags |= TextFormatFlags.HidePrefix;
				TextRenderer.DrawText (e.Graphics, Text, Font, text, Enabled ? ForeColor : SystemColors.GrayText, flags);
				if (Focused && ShowFocusCues) {
					Size size = TextRenderer.MeasureText (e.Graphics, Text, Font, text.Size, flags);
					var focus = new Rectangle (text.X - 1, (Height - size.Height) / 2 - 1, size.Width + 2, size.Height + 2);
					ControlPaint.DrawFocusRectangle (e.Graphics, focus);
				}
			}
		}

		/// <summary>An SS_LEFT static, as the dialog's labels are: DrawText(DT_LEFT | DT_WORDBREAK |
		/// DT_EXPANDTABS) at the control's origin. A Label pads its text (TextRenderer's
		/// glyph-overhang margin), which put every caption here three pixels right of Windows'.</summary>
		private sealed class StaticText : Label
		{
			protected override void OnPaint (PaintEventArgs e)
			{
				TextFormatFlags flags = TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak
					| TextFormatFlags.ExpandTabs | TextFormatFlags.NoPadding;
				if (!UseMnemonic)
					flags |= TextFormatFlags.NoPrefix;
				else if (!ShowKeyboardCues)
					flags |= TextFormatFlags.HidePrefix;
				Color color = Enabled ? ForeColor : SystemColors.GrayText;
				TextRenderer.DrawText (e.Graphics, Text, Font, ClientRectangle, color, flags);
			}
		}

		private ComboBox MakeList (int x, int y, int w, int h, int itemHeight)
			=> new ComboBox {
				DropDownStyle = ComboBoxStyle.Simple,
				DrawMode = DrawMode.OwnerDrawFixed,
				ItemHeight = itemHeight,
				// comdlg32 answers 14 for the selection field: a 20-pixel edit over the list.
				SelectionItemHeight = 14,
				// The heights are Windows' own lists, already whole items; snapping again would cut one.
				IntegralHeight = false,
				Location = new Point (x, y),
				Size = new Size (w, h),
			};

		private Button MakeButton (string text, int y)
			=> new Button { Text = text, Location = new Point (347, y), Size = new Size (68, 23) };

		#region Public Instance Properties
		public Font Font
		{
			get { return font; }
			set {
				if (value != null) {
					font = new Font (value, value.Style);
					currentSize = font.SizeInPoints;
					strikeoutCheck.Checked = font.Strikeout;
					underlineCheck.Checked = font.Underline;
				}
			}
		}

		[DefaultValue(false)]
		public bool FontMustExist {
			get { return fontMustExist; }
			set { fontMustExist = value; }
		}

		[DefaultValue ("Color [Black]")]
		public Color Color {
			set {
				color = value;
				sample?.Invalidate ();
			}
			get { return color; }
		}

		[DefaultValue(true)]
		public bool AllowSimulations {
			set { allowSimulations = value; }
			get { return allowSimulations; }
		}

		[DefaultValue(true)]
		public bool AllowVectorFonts {
			set { allowVectorFonts = value; }
			get { return allowVectorFonts; }
		}

		[DefaultValue(true)]
		public bool AllowVerticalFonts {
			set { allowVerticalFonts = value; }
			get { return allowVerticalFonts; }
		}

		[DefaultValue(true)]
		public bool AllowScriptChange {
			set { allowScriptChange = value; }
			get { return allowScriptChange; }
		}

		[DefaultValue(false)]
		public bool FixedPitchOnly {
			set {
				if (fixedPitchOnly != value) {
					fixedPitchOnly = value;
					PopulateFontList ();
				}
			}
			get { return fixedPitchOnly; }
		}

		[DefaultValue(0)]
		public int MaxSize {
			set {
				maxSize = Math.Max (0, value);
				if (maxSize < minSize)
					minSize = maxSize;
				CreateFontSizeListItems ();
			}
			get { return maxSize; }
		}

		[DefaultValue(0)]
		public int MinSize {
			set {
				minSize = Math.Max (0, value);
				if (minSize > maxSize)
					maxSize = minSize;
				CreateFontSizeListItems ();
			}
			get { return minSize; }
		}

		[DefaultValue(false)]
		public bool ScriptsOnly {
			set { scriptsOnly = value; }
			get { return scriptsOnly; }
		}

		[DefaultValue(false)]
		public bool ShowApply {
			set {
				showApply = value;
				applyButton.Visible = value;
			}
			get { return showApply; }
		}

		[DefaultValue(false)]
		public bool ShowColor {
			set {
				showColor = value;
				colorLabel.Visible = value && showEffects;
				colorCombo.Visible = value && showEffects;
			}
			get { return showColor; }
		}

		[DefaultValue(true)]
		public bool ShowEffects {
			set {
				showEffects = value;
				effectsGroup.Visible = value;
				strikeoutCheck.Visible = value;
				underlineCheck.Visible = value;
				colorLabel.Visible = value && showColor;
				colorCombo.Visible = value && showColor;
			}
			get { return showEffects; }
		}

		[DefaultValue(false)]
		public bool ShowHelp {
			set {
				showHelp = value;
				helpButton.Visible = value;
			}
			get { return showHelp; }
		}
		#endregion	// Public Instance Properties

		#region Protected Instance Properties
		protected int Options {
			get { return 0; }
		}
		#endregion	// Protected Instance Properties

		#region Public Instance Methods
		public override void Reset ()
		{
			color = Color.Black;
			allowSimulations = true;
			allowVectorFonts = true;
			allowVerticalFonts = true;
			allowScriptChange = true;
			fixedPitchOnly = false;
			maxSize = 0;
			minSize = 0;
			scriptsOnly = false;
			ShowApply = false;
			ShowColor = false;
			ShowEffects = true;
			ShowHelp = false;
			fontMustExist = false;
			font = null;
			PopulateFontList ();
			CreateFontSizeListItems ();
		}

		public override string ToString ()
		{
			if (font == null)
				return base.ToString ();
			return String.Concat (base.ToString (), ", Font: ", font.ToString ());
		}
		#endregion	// Public Instance Methods

		#region Protected Instance Methods
		protected override IntPtr HookProc (IntPtr hWnd, int msg, IntPtr wparam, IntPtr lparam)
		{
			return base.HookProc (hWnd, msg, wparam, lparam);
		}

		protected override bool RunDialog (IntPtr hWndOwner)
		{
			// What Windows does on WM_INITDIALOG: select the face the font names, then its style
			// and size, and put the focus in the font name with the whole of it selected.
			Font initial = font ?? form.Font;
			internal_change = true;
			int index = FindFamily (initial);
			fontCombo.SelectedIndex = index;
			if (index >= 0)
				fontCombo.TopIndex = index;
			currentFamily = index >= 0 ? families [index] : null;
			FillStyles (initial.Style & (FontStyle.Bold | FontStyle.Italic), initial.Name);
			currentSize = initial.SizeInPoints;
			SelectSize ();
			// DEFAULT_CHARSET stands for the system's ANSI code page's character set.
			FillScripts (initial.GdiCharSet == 1 ? FontDialogFamily.CharSetOfCodePage (FontDialogFamily.AnsiCodePage) : initial.GdiCharSet);
			strikeoutCheck.Checked = initial.Strikeout;
			underlineCheck.Checked = initial.Underline;
			internal_change = false;
			form.ActiveControl = fontCombo;
			fontCombo.SelectAll ();
			sample.Invalidate ();
			return true;
		}

		internal void OnApplyButton (object sender, EventArgs e)
		{
			BuildResult ();
			OnApply (e);
		}

		protected virtual void OnApply (EventArgs e)
		{
			EventHandler apply = (EventHandler) Events [EventApply];
			if (apply != null)
				apply (this, e);
		}
		#endregion	// Protected Instance Methods

		/// <summary>The font the dialog currently describes, as OK hands it back.</summary>
		private Font CurrentFont ()
		{
			FontStyle style = currentFace != null ? currentFace.Style : FontStyle.Regular;
			if (strikeoutCheck.Checked) style |= FontStyle.Strikeout;
			if (underlineCheck.Checked) style |= FontStyle.Underline;
			string name = currentFace != null ? currentFace.GdiName : currentFamily != null ? currentFamily.Name : form.Font.Name;
			try {
				return new Font (name, currentSize, style);
			} catch (ArgumentException) {
				return new Font (form.Font.FontFamily, currentSize, style);
			}
		}

		private void BuildResult ()
		{
			font = CurrentFont ();
		}

		#region The lists
		private int FindFamily (Font f)
		{
			if (families == null)
				return -1;
			for (int i = 0; i < families.Count; i++)
				if (string.Equals (families [i].Name, f.Name, StringComparison.OrdinalIgnoreCase))
					return i;
			// A face's own legacy name ("Segoe UI Semibold") belongs to the family that holds it.
			for (int i = 0; i < families.Count; i++)
				foreach (FontDialogFace face in families [i].Faces)
					if (string.Equals (face.GdiName, f.Name, StringComparison.OrdinalIgnoreCase))
						return i;
			return families.Count > 0 ? 0 : -1;
		}

		private void FillStyles (FontStyle wanted, string gdiName)
		{
			styleCombo.BeginUpdate ();
			styleCombo.Items.Clear ();
			int select = -1;
			if (currentFamily != null) {
				for (int i = 0; i < currentFamily.Faces.Count; i++) {
					FontDialogFace face = currentFamily.Faces [i];
					styleCombo.Items.Add (face.Name);
					if (select < 0 && face.Style == wanted && string.Equals (face.GdiName, gdiName, StringComparison.OrdinalIgnoreCase))
						select = i;
				}
				if (select < 0)
					for (int i = 0; i < currentFamily.Faces.Count; i++)
						if (currentFamily.Faces [i].Style == wanted && !currentFamily.Faces [i].Simulated) { select = i; break; }
				if (select < 0 && styleCombo.Items.Count > 0)
					select = 0;
			}
			styleCombo.EndUpdate ();
			styleCombo.SelectedIndex = select;
			if (select >= 0)
				styleCombo.TopIndex = select;
			currentFace = select >= 0 ? currentFamily.Faces [select] : null;
		}

		private void SelectSize ()
		{
			string text = ((int) Math.Round (currentSize)).ToString ();
			int index = sizeCombo.FindStringExact (text);
			sizeCombo.SelectedIndex = index;
			if (index >= 0)
				sizeCombo.TopIndex = index;
			else
				sizeCombo.Text = text;
		}

		private void FillScripts (byte charset)
		{
			scriptCombo.BeginUpdate ();
			scriptCombo.Items.Clear ();
			int select = 0;
			if (currentFamily != null)
				for (int i = 0; i < currentFamily.Scripts.Count; i++) {
					scriptCombo.Items.Add (currentFamily.Scripts [i].Name);
					if (currentFamily.Scripts [i].CharSet == charset)
						select = i;
				}
			scriptCombo.EndUpdate ();
			if (scriptCombo.Items.Count > 0)
				scriptCombo.SelectedIndex = select;
		}

		private void CreateFontSizeListItems ()
		{
			sizeCombo.BeginUpdate ();
			sizeCombo.Items.Clear ();
			foreach (int i in a_sizes)
				if ((minSize == 0 || i >= minSize) && (maxSize == 0 || i <= maxSize))
					sizeCombo.Items.Add (i.ToString ());
			sizeCombo.EndUpdate ();
		}

		private void PopulateFontList ()
		{
			families = FontDialogFamily.Enumerate (fixedPitchOnly);
			fontCombo.BeginUpdate ();
			fontCombo.Items.Clear ();
			foreach (FontDialogFamily family in families)
				fontCombo.Items.Add (family.Name);
			fontCombo.EndUpdate ();
		}

		private void OnFontSelected (object sender, EventArgs e)
		{
			if (internal_change || fontCombo.SelectedIndex < 0)
				return;
			internal_change = true;
			currentFamily = families [fontCombo.SelectedIndex];
			FontStyle keep = currentFace != null ? currentFace.Style : FontStyle.Regular;
			FillStyles (keep, null);
			FillScripts (scriptCombo.SelectedIndex >= 0 && currentFamily.Scripts.Count > 0 ? CurrentCharSet () : (byte) 1);
			internal_change = false;
			sample.Invalidate ();
			styleCombo.Invalidate ();
		}

		private byte CurrentCharSet ()
		{
			string name = scriptCombo.SelectedItem as string;
			foreach (FontDialogScript s in currentFamily.Scripts)
				if (s.Name == name)
					return s.CharSet;
			return 1;
		}

		private void OnStyleSelected (object sender, EventArgs e)
		{
			if (internal_change || styleCombo.SelectedIndex < 0 || currentFamily == null)
				return;
			currentFace = currentFamily.Faces [styleCombo.SelectedIndex];
			sample.Invalidate ();
		}

		private void OnSizeSelected (object sender, EventArgs e)
		{
			if (internal_change || sizeCombo.SelectedIndex < 0)
				return;
			currentSize = float.Parse ((string) sizeCombo.Items [sizeCombo.SelectedIndex]);
			sample.Invalidate ();
		}

		private void OnScriptSelected (object sender, EventArgs e)
		{
			sample.Invalidate ();
		}

		private void OnFontTyped (object sender, EventArgs e)
		{
			int found = fontCombo.FindStringExact (fontCombo.Text);
			if (found < 0)
				found = fontCombo.FindString (fontCombo.Text);
			if (found >= 0)
				fontCombo.TopIndex = found;
		}

		private void OnStyleTyped (object sender, EventArgs e)
		{
			int found = styleCombo.FindString (styleCombo.Text);
			if (found >= 0)
				styleCombo.TopIndex = found;
		}

		private void OnSizeTyped (object sender, EventArgs e)
		{
			if (float.TryParse (sizeCombo.Text, out float size) && size > 0) {
				currentSize = size;
				sample.Invalidate ();
			}
		}
		#endregion

		#region Drawing
		private readonly Dictionary<string, Font> preview_fonts = new Dictionary<string, Font> ();

		/// <summary>The face an entry is drawn in, at the dialog font's size. A face that will not
		/// instantiate falls back to the dialog font rather than taking the dialog down.</summary>
		private Font PreviewFont (string gdiName, FontStyle style)
		{
			string key = gdiName + "/" + (int) style;
			if (preview_fonts.TryGetValue (key, out Font f))
				return f;
			try {
				f = new Font (gdiName, PreviewSize, style);
			} catch {
				f = null;
			}
			preview_fonts [key] = f;
			return f;
		}

		/// <summary>The size the font and style lists draw their entries at.</summary>
		private const float PreviewSize = 10.5f;

		private void DrawEntry (DrawItemEventArgs e, string text, Font face, int indent = 0)
		{
			bool selected = (e.State & DrawItemState.Selected) != 0;
			Color back = selected ? SystemColors.Highlight : SystemColors.Window;
			Color fore = selected ? SystemColors.HighlightText : SystemColors.WindowText;
			using (var b = new SolidBrush (back))
				e.Graphics.FillRectangle (b, e.Bounds);
			Rectangle r = e.Bounds;
			r.X += indent;
			r.Width -= indent;
			// Clipped to the item, as the list's own DC is: the last row's text must not reach the frame.
			System.Drawing.Region clip = e.Graphics.Clip;
			e.Graphics.SetClip (e.Bounds, System.Drawing.Drawing2D.CombineMode.Intersect);
			TextRenderer.DrawText (e.Graphics, text, face ?? form.Font, r, fore,
				TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix
				| TextFormatFlags.NoPadding | TextFormatFlags.NoClipping);
			e.Graphics.Clip = clip;
		}

		private void OnDrawFontItem (object sender, DrawItemEventArgs e)
		{
			if (e.Index < 0 || e.Index >= families.Count)
				return;
			FontDialogFamily family = families [e.Index];
			FontDialogFace face = family.Faces.Count > 0 ? family.DefaultFace : null;
			string preview = !string.IsNullOrEmpty (family.PreviewName) ? family.PreviewName : face?.GdiName;
			DrawEntry (e, family.Name, preview != null ? PreviewFont (preview, FontStyle.Regular) : null);
		}

		private void OnDrawStyleItem (object sender, DrawItemEventArgs e)
		{
			if (currentFamily == null || e.Index < 0 || e.Index >= currentFamily.Faces.Count)
				return;
			FontDialogFace face = currentFamily.Faces [e.Index];
			DrawEntry (e, face.Name, PreviewFont (face.GdiName, face.Style));
		}

		private void OnDrawSizeItem (object sender, DrawItemEventArgs e)
		{
			if (e.Index < 0 || e.Index >= sizeCombo.Items.Count)
				return;
			// Entries in the dialog font stand a pixel further in than the font list's previews.
			DrawEntry (e, (string) sizeCombo.Items [e.Index], null, 1);
		}

		private void OnDrawScriptItem (object sender, DrawItemEventArgs e)
		{
			if (e.Index < 0 || e.Index >= scriptCombo.Items.Count)
				return;
			// The closed list's field draws its item a pixel further in again.
			DrawEntry (e, (string) scriptCombo.Items [e.Index], null, (e.State & DrawItemState.ComboBoxEdit) != 0 ? 2 : 1);
		}

		/// <summary>The sample: the script's sample text in the font being built, centred in the
		/// rectangle Windows keeps a hidden static for.</summary>
		private sealed class SampleBox : Control
		{
			private readonly FontDialog owner;

			public SampleBox (FontDialog owner)
			{
				this.owner = owner;
				SetStyle (ControlStyles.Selectable, false);
				TabStop = false;
			}

			protected override void OnPaint (PaintEventArgs e)
			{
				using (var b = new SolidBrush (SystemColors.Control))
					e.Graphics.FillRectangle (b, ClientRectangle);
				string text = owner.SampleText ();
				using (Font f = owner.CurrentFont ())
					TextRenderer.DrawText (e.Graphics, text, f, ClientRectangle,
						owner.showEffects ? owner.color : SystemColors.ControlText,
						TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
			}
		}

		private string SampleText ()
		{
			if (currentFamily != null && scriptCombo.SelectedIndex >= 0 && scriptCombo.SelectedIndex < currentFamily.Scripts.Count)
				return currentFamily.Scripts [scriptCombo.SelectedIndex].Sample;
			return "AaBbYyZz";
		}
		#endregion

		internal class ColorComboBox : ComboBox
		{
			internal class ColorComboBoxItem
			{
				public ColorComboBoxItem (Color color, string name)
				{
					Color = color;
					Name = name;
				}

				public Color Color { get; set; }
				public string Name { get; set; }
				public override string ToString () => Name;
			}

			private readonly FontDialog fontDialog;

			public ColorComboBox (FontDialog fontDialog)
			{
				this.fontDialog = fontDialog;
				DropDownStyle = ComboBoxStyle.DropDownList;
				DrawMode = DrawMode.OwnerDrawFixed;
				Items.AddRange (new object [] {
					new ColorComboBoxItem (Color.Black, "Black"),
					new ColorComboBoxItem (Color.Maroon, "Maroon"),
					new ColorComboBoxItem (Color.Green, "Green"),
					new ColorComboBoxItem (Color.Olive, "Olive"),
					new ColorComboBoxItem (Color.Navy, "Navy"),
					new ColorComboBoxItem (Color.Purple, "Purple"),
					new ColorComboBoxItem (Color.Teal, "Teal"),
					new ColorComboBoxItem (Color.Gray, "Gray"),
					new ColorComboBoxItem (Color.Silver, "Silver"),
					new ColorComboBoxItem (Color.Red, "Red"),
					new ColorComboBoxItem (Color.Lime, "Lime"),
					new ColorComboBoxItem (Color.Yellow, "Yellow"),
					new ColorComboBoxItem (Color.Blue, "Blue"),
					new ColorComboBoxItem (Color.Fuchsia, "Fuchsia"),
					new ColorComboBoxItem (Color.Aqua, "Aqua"),
					new ColorComboBoxItem (Color.White, "White") });
				SelectedIndex = 0;
				MaxDropDownItems = 16;
			}

			protected override void OnDrawItem (DrawItemEventArgs e)
			{
				if (e.Index == -1)
					return;
				var item = (ColorComboBoxItem) Items [e.Index];
				bool selected = (e.State & DrawItemState.Selected) != 0;
				using (var back = new SolidBrush (selected ? SystemColors.Highlight : SystemColors.Window))
					e.Graphics.FillRectangle (back, e.Bounds);
				using (var swatch = new SolidBrush (item.Color))
					e.Graphics.FillRectangle (swatch, e.Bounds.X + 3, e.Bounds.Y + 3, 16, e.Bounds.Height - 6);
				e.Graphics.DrawRectangle (Pens.Black, e.Bounds.X + 2, e.Bounds.Y + 2, 17, e.Bounds.Height - 5);
				Rectangle r = e.Bounds;
				r.X += 24;
				r.Width -= 24;
				TextRenderer.DrawText (e.Graphics, item.Name, Font, r, selected ? SystemColors.HighlightText : SystemColors.WindowText,
					TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
			}

			protected override void OnSelectedIndexChanged (EventArgs e)
			{
				base.OnSelectedIndexChanged (e);
				if (SelectedIndex >= 0)
					fontDialog.Color = ((ColorComboBoxItem) Items [SelectedIndex]).Color;
			}
		}

		public event EventHandler Apply {
			add { Events.AddHandler (EventApply, value); }
			remove { Events.RemoveHandler (EventApply, value); }
		}
	}

	/// <summary>A family as the font dialog lists it: its name, its faces in the order Windows
	/// shows them, and the scripts it covers.</summary>
	internal sealed class FontDialogFamily
	{
		public string Name;
		/// <summary>The GDI face the font list draws the family's name in.</summary>
		public string PreviewName;
		public List<FontDialogFace> Faces = new List<FontDialogFace> ();
		public List<FontDialogScript> Scripts = new List<FontDialogScript> ();

		/// <summary>The face the font list draws the family's name in: its regular one.</summary>
		public FontDialogFace DefaultFace {
			get {
				foreach (FontDialogFace f in Faces)
					if (!f.Simulated && f.Style == FontStyle.Regular)
						return f;
				return Faces [0];
			}
		}

		/// <summary>The families installed, as Windows' font dialog lists them (ChooseFontModel: fms.dll's
		/// grouping and naming, comdlg32's orders), built once from the font files in load order.</summary>
		internal static List<FontDialogFamily> Enumerate (bool fixedPitchOnly)
		{
			var list = new List<FontDialogFamily> ();
			foreach (ChooseFontModel.Family f in Model) {
				var family = new FontDialogFamily { Name = f.Name, PreviewName = f.RepresentativeGdiName };
				foreach (ChooseFontModel.Face face in f.Faces) {
					FontStyle style = FontStyle.Regular;
					if (face.GdiWeight >= 700) style |= FontStyle.Bold;
					if (face.GdiItalic) style |= FontStyle.Italic;
					family.Faces.Add (new FontDialogFace { Name = face.Name, GdiName = face.GdiFamilyName, Style = style, Simulated = face.IsSimulated });
				}
				foreach (ChooseFontModel.Script script in f.Scripts)
					family.Scripts.Add (new FontDialogScript { Name = script.Name, CharSet = script.CharSet, Sample = script.Sample });
				if (family.Faces.Count > 0)
					list.Add (family);
			}
			return list;
		}

		private static List<ChooseFontModel.Family> s_model;

		private static List<ChooseFontModel.Family> Model {
			get {
				if (s_model == null)
					s_model = ChooseFontModel.Build (Microsoft.Wpf.Interop.WebGpu.Composition.Text.FontFiles.LoadOrder (), AnsiCodePage, SystemLanguageId);
				return s_model;
			}
		}

		[System.Runtime.InteropServices.DllImport ("kernel32.dll")]
		private static extern int GetACP ();

		[System.Runtime.InteropServices.DllImport ("kernel32.dll")]
		private static extern ushort GetSystemDefaultLangID ();

		/// <summary>The system ANSI code page, whose script the dialog lists first and DEFAULT_CHARSET means.</summary>
		internal static int AnsiCodePage {
			get {
				if (OperatingSystem.IsWindows ())
					try { return GetACP (); } catch (EntryPointNotFoundException) { }
				return System.Globalization.CultureInfo.InstalledUICulture.TextInfo.ANSICodePage;
			}
		}

		/// <summary>The system locale's LANGID, which picks the legacy (GDI) family names.</summary>
		internal static int SystemLanguageId {
			get {
				if (OperatingSystem.IsWindows ())
					try { return GetSystemDefaultLangID (); } catch (EntryPointNotFoundException) { }
				return System.Globalization.CultureInfo.InstalledUICulture.LCID & 0xffff;
			}
		}

		/// <summary>The GDI character set of a Windows ANSI code page.</summary>
		internal static byte CharSetOfCodePage (int cp) => cp switch {
			874 => 222, 932 => 128, 936 => 134, 949 => 129, 950 => 136, 1250 => 238, 1251 => 204,
			1253 => 161, 1254 => 162, 1255 => 177, 1256 => 178, 1257 => 186, 1258 => 163, _ => 0,
		};
	}

	internal sealed class FontDialogFace
	{
		public string Name;
		/// <summary>The legacy (GDI) family name the face is reached by, and its style there.</summary>
		public string GdiName;
		public FontStyle Style;
		public bool Simulated;
	}

	internal sealed class FontDialogScript
	{
		public string Name;
		public byte CharSet;
		public string Sample;
	}
}
