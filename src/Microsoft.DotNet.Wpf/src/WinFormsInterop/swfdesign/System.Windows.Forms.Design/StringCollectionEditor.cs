//
// System.Windows.Forms.Design.StringCollectionEditor
//
// Author:
//   Ivan N. Zlatev <contact@i-nz.net>
//
// (C) 2007 Ivan N. Zlatev
//
//
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

using System;
using System.ComponentModel;
using System.ComponentModel.Design;
using System.Drawing;
using System.Drawing.Design;
using System.Windows.Forms;

namespace System.Windows.Forms.Design
{
	internal class StringCollectionEditor : CollectionEditor
	{
		/// <summary>.NET's StringCollectionEditor.StringCollectionForm with its StringCollectionEditor.resx
		/// applied in code: a two-column table -- the instruction across the top, a 6-pixel gap, the
		/// text box filling the rest, OK and Cancel at the bottom right -- in a font-scaled form
		/// (designed at 6 x 13) with 12/9/12/10 padding, a help button and no icon. The port's own
		/// form was a fixed 402 x 228 layout of its own design.</summary>
		private class StringCollectionForm : CollectionEditor.CollectionForm
		{
			private Label _instruction;
			private TextBox _textEntry;
			private Button _okButton;
			private Button _cancelButton;
			private TableLayoutPanel _overarchingLayoutPanel;
			private readonly StringCollectionEditor _editor;

			public StringCollectionForm (CollectionEditor editor) : base (editor)
			{
				_editor = (StringCollectionEditor) editor;
				InitializeComponent ();
				HookEvents ();
			}

			private void Edit1_keyDown (object sender, KeyEventArgs e)
			{
				if (e.KeyCode != Keys.Escape)
					return;
				_cancelButton.PerformClick ();
				e.Handled = true;
			}

			private void StringCollectionEditor_HelpButtonClicked (object sender, CancelEventArgs e)
			{
				e.Cancel = true;
				_editor.ShowHelpInternal ();
			}

			private void Form_HelpRequested (object sender, HelpEventArgs e)
			{
				_editor.ShowHelpInternal ();
			}

			private void HookEvents ()
			{
				_textEntry.KeyDown += Edit1_keyDown;
				_okButton.Click += OKButton_click;
				HelpButtonClicked += StringCollectionEditor_HelpButtonClicked;
			}

			private void InitializeComponent ()
			{
				_instruction = new Label ();
				_textEntry = new TextBox ();
				_okButton = new Button ();
				_cancelButton = new Button ();
				_overarchingLayoutPanel = new TableLayoutPanel ();
				_overarchingLayoutPanel.SuspendLayout ();
				SuspendLayout ();

				_instruction.AutoSize = true;
				_instruction.Location = new Point (12, 10);
				_instruction.Margin = new Padding (0);
				_instruction.Size = new Size (227, 13);
				_instruction.TabIndex = 0;
				_instruction.Text = "&Enter the strings in the collection (one per line):";
				_instruction.Name = "instruction";

				_textEntry.Dock = DockStyle.Fill;
				_textEntry.Location = new Point (12, 27);
				_textEntry.Margin = new Padding (0);
				_textEntry.Multiline = true;
				_textEntry.ScrollBars = ScrollBars.Both;
				_textEntry.Size = new Size (451, 213);
				_textEntry.TabIndex = 0;
				_textEntry.WordWrap = false;
				_textEntry.AcceptsTab = true;
				_textEntry.AcceptsReturn = true;
				_textEntry.Name = "textEntry";

				_okButton.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
				_okButton.AutoSize = true;
				_okButton.Location = new Point (304, 248);
				_okButton.Margin = new Padding (0, 5, 3, 0);
				_okButton.Size = new Size (75, 23);
				_okButton.TabIndex = 1;
				_okButton.Text = "OK";
				_okButton.DialogResult = DialogResult.OK;
				_okButton.Name = "okButton";

				_cancelButton.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
				_cancelButton.AutoSize = true;
				_cancelButton.Location = new Point (385, 248);
				_cancelButton.Margin = new Padding (3, 5, 0, 0);
				_cancelButton.Size = new Size (75, 23);
				_cancelButton.TabIndex = 2;
				_cancelButton.Text = "Cancel";
				_cancelButton.DialogResult = DialogResult.Cancel;
				_cancelButton.Name = "cancelButton";

				_overarchingLayoutPanel.ColumnCount = 2;
				_overarchingLayoutPanel.ColumnStyles.Add (new ColumnStyle (SizeType.Percent, 100F));
				_overarchingLayoutPanel.ColumnStyles.Add (new ColumnStyle (SizeType.AutoSize, 78F));
				_overarchingLayoutPanel.RowCount = 4;
				_overarchingLayoutPanel.RowStyles.Add (new RowStyle (SizeType.AutoSize, 13F));
				_overarchingLayoutPanel.RowStyles.Add (new RowStyle (SizeType.Absolute, 6F));
				_overarchingLayoutPanel.RowStyles.Add (new RowStyle (SizeType.Percent, 100F));
				_overarchingLayoutPanel.RowStyles.Add (new RowStyle (SizeType.AutoSize, 29F));
				_overarchingLayoutPanel.Dock = DockStyle.Fill;
				_overarchingLayoutPanel.Location = new Point (12, 9);
				_overarchingLayoutPanel.Size = new Size (451, 262);
				_overarchingLayoutPanel.TabIndex = 0;
				_overarchingLayoutPanel.Controls.Add (_instruction, 0, 0);
				_overarchingLayoutPanel.Controls.Add (_textEntry, 0, 2);
				_overarchingLayoutPanel.Controls.Add (_okButton, 0, 3);
				_overarchingLayoutPanel.Controls.Add (_cancelButton, 1, 3);
				_overarchingLayoutPanel.SetColumnSpan (_instruction, 2);
				_overarchingLayoutPanel.SetColumnSpan (_textEntry, 2);
				_overarchingLayoutPanel.Name = "overarchingLayoutPanel";

				Location = new Point (7, 7);
				AutoScaleDimensions = new SizeF (6F, 13F);
				AutoScaleMode = AutoScaleMode.Font;
				ClientSize = new Size (475, 281);
				MinimumSize = new Size (300, 200);
				Padding = new Padding (12, 9, 12, 10);
				StartPosition = FormStartPosition.CenterScreen;
				Text = "String Collection Editor";
				Controls.Add (_overarchingLayoutPanel);
				HelpButton = true;
				MaximizeBox = false;
				MinimizeBox = false;
				Name = "StringCollectionEditor";
				ShowIcon = false;
				ShowInTaskbar = false;
				_overarchingLayoutPanel.ResumeLayout (false);
				_overarchingLayoutPanel.PerformLayout ();
				HelpRequested += Form_HelpRequested;
				ResumeLayout (false);
				PerformLayout ();
			}

			private void OKButton_click (object sender, EventArgs e)
			{
				string[] lines = _textEntry.Text.Split ('\n');
				for (int i = 0; i < lines.Length; i++)
					lines[i] = lines[i].TrimEnd ('\r');

				if (lines.Length != Items.Length) {
					UpdateItems (lines);
					return;
				}
				for (int i = 0; i < lines.Length; ++i) {
					if (!lines[i].Equals (Items[i]?.ToString ())) {
						UpdateItems (lines);
						return;
					}
				}
				DialogResult = DialogResult.Cancel;
			}

			private void UpdateItems (string[] newLines)
			{
				// A last empty line is not an item.
				if (newLines.Length > 0 && newLines[newLines.Length - 1].Length == 0)
					Array.Resize (ref newLines, newLines.Length - 1);
				Items = newLines;
			}

			protected override void OnEditValueChanged ()
			{
				_textEntry.Text = string.Join (Environment.NewLine, Items);
			}
		}

		public StringCollectionEditor (Type type) : base (type)
		{
		}

		internal void ShowHelpInternal () => ShowHelp ();

		protected override CollectionEditor.CollectionForm CreateCollectionForm ()
		{
			return new StringCollectionForm (this);
		}
	}
}
