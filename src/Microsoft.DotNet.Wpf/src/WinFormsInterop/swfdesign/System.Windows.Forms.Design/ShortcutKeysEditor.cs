// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
//
// Ported from dotnet/winforms (ShortcutKeysEditor and its ShortcutKeysUI): the drop-down a ToolStripMenuItem's
// ShortcutKeys is edited with -- Ctrl, Shift and Alt check boxes, a list of the keys a shortcut may use (a key
// outside it shown as "(Unknown)" and kept), and Reset; Esc puts the starting value back. The layout values are
// .NET's (ShortcutKeysEditor.resx). Both members of the Mono stub threw NotImplementedException.

using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Design;

namespace System.Windows.Forms.Design
{
	public class ShortcutKeysEditor : UITypeEditor
	{
		private ShortcutKeysUI _shortcutKeysUI;

		public ShortcutKeysEditor ()
		{
		}

		public override object EditValue (ITypeDescriptorContext context, IServiceProvider provider, object value)
		{
			if (!(provider?.GetService (typeof (IWindowsFormsEditorService)) is IWindowsFormsEditorService editorService))
				return value;
			_shortcutKeysUI ??= new ShortcutKeysUI { BackColor = SystemColors.Control };
			_shortcutKeysUI.Start (value);
			editorService.DropDownControl (_shortcutKeysUI);
			if (_shortcutKeysUI.Value != null)
				value = _shortcutKeysUI.Value;
			_shortcutKeysUI.End ();
			return value;
		}

		public override UITypeEditorEditStyle GetEditStyle (ITypeDescriptorContext context) => UITypeEditorEditStyle.DropDown;

		internal class ShortcutKeysUI : UserControl
		{
			private static readonly Keys [] s_validKeys = {
				Keys.A, Keys.B, Keys.C, Keys.D, Keys.D0, Keys.D1, Keys.D2, Keys.D3, Keys.D4, Keys.D5, Keys.D6, Keys.D7,
				Keys.D8, Keys.D9, Keys.Delete, Keys.Down, Keys.E, Keys.End, Keys.F, Keys.F1, Keys.F10, Keys.F11,
				Keys.F12, Keys.F13, Keys.F14, Keys.F15, Keys.F16, Keys.F17, Keys.F18, Keys.F19, Keys.F2, Keys.F20,
				Keys.F21, Keys.F22, Keys.F23, Keys.F24, Keys.F3, Keys.F4, Keys.F5, Keys.F6, Keys.F7, Keys.F8, Keys.F9,
				Keys.G, Keys.H, Keys.I, Keys.Insert, Keys.J, Keys.K, Keys.L, Keys.Left, Keys.M, Keys.N, Keys.NumLock,
				Keys.NumPad0, Keys.NumPad1, Keys.NumPad2, Keys.NumPad3, Keys.NumPad4, Keys.NumPad5, Keys.NumPad6,
				Keys.NumPad7, Keys.NumPad8, Keys.NumPad9, Keys.O, Keys.OemBackslash, Keys.OemClear, Keys.OemCloseBrackets,
				Keys.Oemcomma, Keys.OemMinus, Keys.OemOpenBrackets, Keys.OemPeriod, Keys.OemPipe, Keys.Oemplus,
				Keys.OemQuestion, Keys.OemQuotes, Keys.OemSemicolon, Keys.Oemtilde, Keys.P, Keys.Pause, Keys.Q, Keys.R,
				Keys.Right, Keys.S, Keys.Space, Keys.T, Keys.Tab, Keys.U, Keys.Up, Keys.V, Keys.W, Keys.X, Keys.Y, Keys.Z,
			};

			private const string InvalidKey = "(Unknown)";

			private Button _resetButton;
			private CheckBox _altCheckBox;
			private CheckBox _ctrlCheckBox;
			private CheckBox _shiftCheckBox;
			private ComboBox _keyComboBox;
			private TypeConverter _keysConverter;
			private Label _keyLabel;
			private Label _modifiersLabel;
			private object _originalValue;
			private object _currentValue;
			private TableLayoutPanel _innerPanel;
			private TableLayoutPanel _outerPanel;
			private Keys _unknownKeyCode;
			private bool _updateCurrentValue;

			public ShortcutKeysUI ()
			{
				InitializeComponent ();
				End ();
				AdjustSize ();
			}

			private TypeConverter KeysConverter => _keysConverter ??= TypeDescriptor.GetConverter (typeof (Keys));

			internal CheckBox CtrlBox => _ctrlCheckBox;
			internal CheckBox AltBox => _altCheckBox;
			internal CheckBox ShiftBox => _shiftCheckBox;
			internal ComboBox KeyBox => _keyComboBox;
			internal Button ResetButton => _resetButton;

			/// <summary>The edited keys; a combination with no key at all is None.</summary>
			public object Value => _currentValue is Keys currentKeys && (currentKeys & Keys.KeyCode) == 0 ? Keys.None : _currentValue;

			private void OnResetButtonClick (object sender, EventArgs e)
			{
				_ctrlCheckBox.Checked = false;
				_altCheckBox.Checked = false;
				_shiftCheckBox.Checked = false;
				_keyComboBox.SelectedIndex = -1;
			}

			public void End ()
			{
				_originalValue = null;
				_currentValue = null;
				_updateCurrentValue = false;
				if (_unknownKeyCode != Keys.None) {
					_keyComboBox.Items.RemoveAt (0);
					_unknownKeyCode = Keys.None;
				}
			}

			private static CheckBox Box (string name, string text, string accessibleName, int tabIndex)
				=> new CheckBox { Name = name, Text = text, AccessibleName = accessibleName, AutoSize = true, Dock = DockStyle.Fill, TabIndex = tabIndex };

			private void InitializeComponent ()
			{
				_outerPanel = new TableLayoutPanel ();
				_modifiersLabel = new Label { Name = "lblModifiers", Text = "Modifiers:", Dock = DockStyle.Fill, Size = new Size (53, 14), TabIndex = 1 };
				_ctrlCheckBox = Box ("chkCtrl", "&Ctrl", "Control key", 2);
				_shiftCheckBox = Box ("chkShift", "&Shift", "Shift key", 3);
				_altCheckBox = Box ("chkAlt", "&Alt", "Alt key", 4);
				_innerPanel = new TableLayoutPanel ();
				_keyLabel = new Label { Name = "lblKey", Text = "&Key:", Dock = DockStyle.Fill, Size = new Size (27, 14), TabIndex = 1 };
				_keyComboBox = new ComboBox {
					Name = "cmbKey", AccessibleName = "Shortcut key", Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right,
					Dock = DockStyle.None, TabIndex = 2, DropDownStyle = ComboBoxStyle.DropDownList,
				};
				_resetButton = new Button {
					Name = "btnReset", Text = "&Reset", AccessibleName = "Reset", Anchor = AnchorStyles.Top | AnchorStyles.Left,
					AutoSize = true, Size = new Size (41, 23), TabIndex = 3,
				};
				_outerPanel.SuspendLayout ();
				_innerPanel.SuspendLayout ();
				SuspendLayout ();

				_outerPanel.Name = "tlpOuter";
				_outerPanel.AutoSize = true;
				_outerPanel.Dock = DockStyle.Top;
				_outerPanel.Size = new Size (185, 112);
				_outerPanel.TabIndex = 0;
				_outerPanel.ColumnCount = 3;
				for (int i = 0; i < 3; i++)
					_outerPanel.ColumnStyles.Add (new ColumnStyle ());
				_outerPanel.Controls.Add (_modifiersLabel, 0, 0);
				_outerPanel.Controls.Add (_ctrlCheckBox, 0, 1);
				_outerPanel.Controls.Add (_shiftCheckBox, 1, 1);
				_outerPanel.Controls.Add (_altCheckBox, 2, 1);
				_outerPanel.RowCount = 2;
				_outerPanel.RowStyles.Add (new RowStyle (SizeType.Absolute, 20F));
				_outerPanel.RowStyles.Add (new RowStyle (SizeType.Absolute, 24F));
				_outerPanel.SetColumnSpan (_modifiersLabel, 3);
				// Left-aligned with the key list, indented from the labels.
				_ctrlCheckBox.Margin = new Padding (12, 3, 3, 3);
				_ctrlCheckBox.CheckedChanged += (s, e) => UpdateCurrentValue ();
				_altCheckBox.CheckedChanged += (s, e) => UpdateCurrentValue ();
				_shiftCheckBox.CheckedChanged += (s, e) => UpdateCurrentValue ();

				_innerPanel.Name = "tlpInner";
				_innerPanel.Anchor = AnchorStyles.Left | AnchorStyles.Right;
				_innerPanel.AutoSize = true;
				_innerPanel.Dock = DockStyle.Fill;
				_innerPanel.Size = new Size (196, 49);
				_innerPanel.TabIndex = 5;
				_innerPanel.ColumnCount = 2;
				_innerPanel.ColumnStyles.Add (new ColumnStyle (SizeType.AutoSize));
				_innerPanel.ColumnStyles.Add (new ColumnStyle (SizeType.AutoSize));
				_innerPanel.Controls.Add (_keyLabel, 0, 0);
				_innerPanel.Controls.Add (_keyComboBox, 0, 1);
				_innerPanel.Controls.Add (_resetButton, 1, 1);
				_innerPanel.RowCount = 2;
				_innerPanel.RowStyles.Add (new RowStyle (SizeType.Absolute, 20F));
				_innerPanel.RowStyles.Add (new RowStyle (SizeType.AutoSize));
				_innerPanel.SetColumnSpan (_keyLabel, 2);

				// Aligned with Ctrl and indented from the labels; the top margin lines it up with Reset.
				_keyComboBox.Margin = new Padding (12, 4, 3, 3);
				_keyComboBox.Padding = _keyComboBox.Margin;
				foreach (Keys keyCode in s_validKeys)
					_keyComboBox.Items.Add (KeysConverter.ConvertToString (keyCode));
				_keyComboBox.SelectedIndexChanged += (s, e) => UpdateCurrentValue ();
				_resetButton.Click += OnResetButtonClick;

				AccessibleName = "Shortcut Keys Editor";
				AutoScaleMode = AutoScaleMode.Font;
				AutoScaleDimensions = new SizeF (6F, 13F);
				AutoSize = true;
				AutoSizeMode = AutoSizeMode.GrowAndShrink;
				Size = new Size (189, 100);
				Controls.AddRange (new Control [] { _innerPanel, _outerPanel });
				Name = "ShortcutKeysUI";
				Padding = new Padding (4);
				_outerPanel.ResumeLayout (false);
				_outerPanel.PerformLayout ();
				_innerPanel.ResumeLayout (false);
				_innerPanel.PerformLayout ();
				ResumeLayout (false);
				PerformLayout ();
			}

			// The Reset button may come out wider than its designed 41 pixels (a localized label): the drop-down grows.
			private void AdjustSize ()
			{
				Size = new Size (Size.Width + _resetButton.Size.Width - 41, Size.Height);
			}

			private static bool IsValidKey (Keys keyCode) => Array.IndexOf (s_validKeys, keyCode) >= 0;

			protected override void OnGotFocus (EventArgs e)
			{
				base.OnGotFocus (e);
				_ctrlCheckBox.Focus ();
			}

			protected override bool ProcessDialogKey (Keys keyData)
			{
				Keys keyCode = keyData & Keys.KeyCode;
				Keys keyModifiers = keyData & Keys.Modifiers;
				switch (keyCode) {
				case Keys.Tab:
					if (keyModifiers == Keys.Shift && _ctrlCheckBox.Focused) {
						_resetButton.Focus ();
						return true;
					}
					break;
				case Keys.Left:
					if ((keyModifiers & (Keys.Control | Keys.Alt)) == 0 && _ctrlCheckBox.Focused) {
						_resetButton.Focus ();
						return true;
					}
					break;
				case Keys.Right:
					if ((keyModifiers & (Keys.Control | Keys.Alt)) == 0) {
						if (_altCheckBox.Focused) {
							_keyComboBox.Focus ();
							return true;
						}
						if (_resetButton.Focused) {
							_ctrlCheckBox.Focus ();
							return true;
						}
					}
					break;
				case Keys.Escape:
					if (!_keyComboBox.Focused || (keyModifiers & (Keys.Control | Keys.Alt)) != 0 || !_keyComboBox.DroppedDown)
						_currentValue = _originalValue;
					break;
				}
				return base.ProcessDialogKey (keyData);
			}

			public void Start (object value)
			{
				_originalValue = _currentValue = value;
				Keys keys = value is Keys k ? k : Keys.None;
				_ctrlCheckBox.Checked = (keys & Keys.Control) != 0;
				_altCheckBox.Checked = (keys & Keys.Alt) != 0;
				_shiftCheckBox.Checked = (keys & Keys.Shift) != 0;
				Keys keyCode = keys & Keys.KeyCode;
				if (keyCode == Keys.None) {
					_keyComboBox.SelectedIndex = -1;
				} else if (IsValidKey (keyCode)) {
					_keyComboBox.SelectedItem = KeysConverter.ConvertToString (keyCode);
				} else {
					_keyComboBox.Items.Insert (0, InvalidKey);
					_keyComboBox.SelectedIndex = 0;
					_unknownKeyCode = keyCode;
				}
				_updateCurrentValue = true;
			}

			private void UpdateCurrentValue ()
			{
				if (!_updateCurrentValue)
					return;
				int index = _keyComboBox.SelectedIndex;
				Keys valueKeys = Keys.None;
				if (_ctrlCheckBox.Checked) valueKeys |= Keys.Control;
				if (_altCheckBox.Checked) valueKeys |= Keys.Alt;
				if (_shiftCheckBox.Checked) valueKeys |= Keys.Shift;
				if (_unknownKeyCode != Keys.None && index == 0)
					valueKeys |= _unknownKeyCode;
				else if (index != -1)
					valueKeys |= s_validKeys [_unknownKeyCode == Keys.None ? index : index - 1];
				_currentValue = valueKeys;
			}
		}
	}
}
