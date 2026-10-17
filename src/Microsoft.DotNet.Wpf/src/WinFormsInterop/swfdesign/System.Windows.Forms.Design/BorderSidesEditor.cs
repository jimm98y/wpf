// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
//
// Ported from dotnet/winforms (BorderSidesEditor and its BorderSidesEditorUI): the drop-down of check boxes a
// ToolStripStatusLabel's BorderSides is edited with -- None and All above a divider, then Top, Bottom, Left and
// Right; All checks the four, None clears them, and the four together are All. The layout values are .NET's
// (BorderSidesEditor.resx). Both members of the Mono stub threw NotImplementedException.

using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Design;

namespace System.Windows.Forms.Design
{
	public class BorderSidesEditor : UITypeEditor
	{
		private BorderSidesEditorUI _borderSidesEditorUI;

		public BorderSidesEditor ()
		{
		}

		public override object EditValue (ITypeDescriptorContext context, IServiceProvider provider, object value)
		{
			if (!(provider?.GetService (typeof (IWindowsFormsEditorService)) is IWindowsFormsEditorService editorService))
				return value;
			_borderSidesEditorUI ??= new BorderSidesEditorUI ();
			_borderSidesEditorUI.Start (editorService, value);
			editorService.DropDownControl (_borderSidesEditorUI);
			if (_borderSidesEditorUI.Value != null)
				value = _borderSidesEditorUI.Value;
			_borderSidesEditorUI.End ();
			return value;
		}

		public override UITypeEditorEditStyle GetEditStyle (ITypeDescriptorContext context) => UITypeEditorEditStyle.DropDown;

		internal class BorderSidesEditorUI : UserControl
		{
			private CheckBox _allCheckBox;
			private bool _allChecked;
			private CheckBox _bottomCheckBox;
			private CheckBox _leftCheckBox;
			private CheckBox _noneCheckBox;
			private bool _noneChecked;
			private CheckBox _rightCheckBox;
			private Label _splitterLabel;
			private TableLayoutPanel _tableLayoutPanel;
			private CheckBox _topCheckBox;
			private bool _updateCurrentValue;

			public BorderSidesEditorUI ()
			{
				End ();
				InitializeComponent ();
				Size = PreferredSize;
			}

			public IWindowsFormsEditorService EditorService { get; private set; }

			public object Value { get; private set; }

			internal CheckBox NoneBox => _noneCheckBox;
			internal CheckBox AllBox => _allCheckBox;
			internal CheckBox TopBox => _topCheckBox;
			internal CheckBox BottomBox => _bottomCheckBox;
			internal CheckBox LeftBox => _leftCheckBox;
			internal CheckBox RightBox => _rightCheckBox;

			public void End ()
			{
				EditorService = null;
				Value = null;
				_updateCurrentValue = false;
			}

			protected override void OnGotFocus (EventArgs e)
			{
				base.OnGotFocus (e);
				_noneCheckBox.Focus ();
			}

			private static CheckBox Box (string name, string text, Point location, Size size, int tabIndex, Padding margin)
				=> new CheckBox { Name = name, Text = text, AutoSize = true, Location = location, Size = size, TabIndex = tabIndex, Margin = margin };

			private void InitializeComponent ()
			{
				_tableLayoutPanel = new TableLayoutPanel ();
				_noneCheckBox = Box ("noneCheckBox", "&None", new Point (3, 3), new Size (48, 17), 0, new Padding (3, 3, 3, 1));
				_allCheckBox = Box ("allCheckBox", "&All", new Point (3, 27), new Size (33, 17), 2, new Padding (3, 3, 3, 1));
				_topCheckBox = Box ("topCheckBox", "&Top", new Point (20, 50), new Size (41, 17), 3, new Padding (20, 1, 3, 1));
				_bottomCheckBox = Box ("bottomCheckBox", "&Bottom", new Point (20, 73), new Size (55, 17), 4, new Padding (20, 1, 3, 1));
				_rightCheckBox = Box ("rightCheckBox", "&Right", new Point (20, 119), new Size (47, 17), 5, new Padding (20, 1, 3, 1));
				_leftCheckBox = Box ("leftCheckBox", "&Left", new Point (20, 96), new Size (40, 17), 6, new Padding (20, 1, 3, 1));
				_splitterLabel = new Label {
					Name = "splitterLabel", Anchor = AnchorStyles.Left | AnchorStyles.Right, Location = new Point (3, 23),
					Size = new Size (100, 1), TabIndex = 7, BackColor = SystemColors.ControlDark,
				};
				_tableLayoutPanel.SuspendLayout ();
				SuspendLayout ();

				_tableLayoutPanel.AutoSize = true;
				_tableLayoutPanel.ColumnCount = 1;
				_tableLayoutPanel.AutoSizeMode = AutoSizeMode.GrowAndShrink;
				_tableLayoutPanel.BackColor = SystemColors.Window;
				_tableLayoutPanel.ColumnStyles.Add (new ColumnStyle ());
				_tableLayoutPanel.Controls.Add (_noneCheckBox, 0, 0);
				_tableLayoutPanel.Controls.Add (_allCheckBox, 0, 2);
				_tableLayoutPanel.Controls.Add (_topCheckBox, 0, 3);
				_tableLayoutPanel.Controls.Add (_bottomCheckBox, 0, 4);
				_tableLayoutPanel.Controls.Add (_rightCheckBox, 0, 6);
				_tableLayoutPanel.Controls.Add (_leftCheckBox, 0, 5);
				_tableLayoutPanel.Controls.Add (_splitterLabel, 0, 1);
				_tableLayoutPanel.Name = "tableLayoutPanel1";
				_tableLayoutPanel.RowCount = 7;
				for (int i = 0; i < 7; i++)
					_tableLayoutPanel.RowStyles.Add (new RowStyle ());
				_tableLayoutPanel.Location = new Point (0, 0);
				_tableLayoutPanel.Size = new Size (106, 139);
				_tableLayoutPanel.TabIndex = 0;
				_tableLayoutPanel.Margin = new Padding (0);

				AutoSize = true;
				Controls.Add (_tableLayoutPanel);
				Padding = new Padding (1, 1, 1, 1);
				AutoSizeMode = AutoSizeMode.GrowAndShrink;
				AutoScaleMode = AutoScaleMode.Font;
				AutoScaleDimensions = new SizeF (6F, 13F);
				_tableLayoutPanel.ResumeLayout (false);
				_tableLayoutPanel.PerformLayout ();
				ResumeLayout (false);
				PerformLayout ();

				_rightCheckBox.CheckedChanged += SideCheckedChanged;
				_leftCheckBox.CheckedChanged += SideCheckedChanged;
				_bottomCheckBox.CheckedChanged += SideCheckedChanged;
				_topCheckBox.CheckedChanged += SideCheckedChanged;
				_noneCheckBox.CheckedChanged += NoneCheckedChanged;
				_allCheckBox.CheckedChanged += AllCheckedChanged;
				_noneCheckBox.Click += (s, e) => { if (_noneChecked) _noneCheckBox.Checked = true; };
				_allCheckBox.Click += (s, e) => { if (_allChecked) _allCheckBox.Checked = true; };
			}

			// A side checked clears None; a side cleared clears All.
			private void SideCheckedChanged (object sender, EventArgs e)
			{
				if (((CheckBox) sender).Checked)
					_noneCheckBox.Checked = false;
				else if (_allCheckBox.Checked)
					_allCheckBox.Checked = false;
				UpdateCurrentValue ();
			}

			private void NoneCheckedChanged (object sender, EventArgs e)
			{
				if (((CheckBox) sender).Checked) {
					_allCheckBox.Checked = false;
					_topCheckBox.Checked = false;
					_bottomCheckBox.Checked = false;
					_leftCheckBox.Checked = false;
					_rightCheckBox.Checked = false;
				}
				UpdateCurrentValue ();
			}

			private void AllCheckedChanged (object sender, EventArgs e)
			{
				if (((CheckBox) sender).Checked) {
					_noneCheckBox.Checked = false;
					_topCheckBox.Checked = true;
					_bottomCheckBox.Checked = true;
					_leftCheckBox.Checked = true;
					_rightCheckBox.Checked = true;
				}
				UpdateCurrentValue ();
			}

			private void ResetCheckBoxState ()
			{
				_allCheckBox.Checked = false;
				_noneCheckBox.Checked = false;
				_topCheckBox.Checked = false;
				_bottomCheckBox.Checked = false;
				_leftCheckBox.Checked = false;
				_rightCheckBox.Checked = false;
			}

			private void SetCheckBoxCheckState (ToolStripStatusLabelBorderSides sides)
			{
				ResetCheckBoxState ();
				if ((sides & ToolStripStatusLabelBorderSides.All) == ToolStripStatusLabelBorderSides.All) {
					_allCheckBox.Checked = true;
					_topCheckBox.Checked = true;
					_bottomCheckBox.Checked = true;
					_leftCheckBox.Checked = true;
					_rightCheckBox.Checked = true;
					_allCheckBox.Checked = true;
				} else {
					_noneCheckBox.Checked = sides == ToolStripStatusLabelBorderSides.None;
					_topCheckBox.Checked = (sides & ToolStripStatusLabelBorderSides.Top) != 0;
					_bottomCheckBox.Checked = (sides & ToolStripStatusLabelBorderSides.Bottom) != 0;
					_leftCheckBox.Checked = (sides & ToolStripStatusLabelBorderSides.Left) != 0;
					_rightCheckBox.Checked = (sides & ToolStripStatusLabelBorderSides.Right) != 0;
				}
			}

			public void Start (IWindowsFormsEditorService edSvc, object value)
			{
				EditorService = edSvc;
				Value = value;
				if (value is ToolStripStatusLabelBorderSides currentSides) {
					SetCheckBoxCheckState (currentSides);
					_updateCurrentValue = true;
				}
			}

			private void UpdateCurrentValue ()
			{
				if (!_updateCurrentValue)
					return;
				ToolStripStatusLabelBorderSides valueSide = ToolStripStatusLabelBorderSides.None;
				if (_allCheckBox.Checked) {
					Value = ToolStripStatusLabelBorderSides.All;
					_allChecked = true;
					_noneChecked = false;
					return;
				}
				if (_topCheckBox.Checked) valueSide |= ToolStripStatusLabelBorderSides.Top;
				if (_bottomCheckBox.Checked) valueSide |= ToolStripStatusLabelBorderSides.Bottom;
				if (_leftCheckBox.Checked) valueSide |= ToolStripStatusLabelBorderSides.Left;
				if (_rightCheckBox.Checked) valueSide |= ToolStripStatusLabelBorderSides.Right;
				if (valueSide == ToolStripStatusLabelBorderSides.None) {
					_allChecked = false;
					_noneChecked = true;
					_noneCheckBox.Checked = true;
				}
				if (valueSide == ToolStripStatusLabelBorderSides.All) {
					_allChecked = true;
					_noneChecked = false;
					_allCheckBox.Checked = true;
				}
				Value = valueSide;
			}
		}
	}
}
