// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
//
// Ported from dotnet/winforms (System.Windows.Forms.Design/src/System/Windows/Forms/Design/
// DockEditor.cs + DockEditor.DockUI.cs), replacing Mono's CheckBox-based DockEditorControl.
// SelectionPanelBase is link-compiled from swf/System.Drawing.Design. The port runs at 96 dpi, so
// every ScaleHelper value is its logical one.

using System.ComponentModel;
using System.Drawing;
using System.Drawing.Design;

namespace System.Windows.Forms.Design
{
	/// <summary>
	///  Implements the design time editor for specifying the <see cref="Control.Dock"/> property.
	/// </summary>
	public sealed partial class DockEditor : UITypeEditor
	{
		private DockUI _dockUI;

		public DockEditor ()
		{
		}

		public override object EditValue (ITypeDescriptorContext context, IServiceProvider provider, object value)
		{
			IWindowsFormsEditorService editorService = provider == null
				? null
				: provider.GetService (typeof (IWindowsFormsEditorService)) as IWindowsFormsEditorService;
			if (editorService == null)
				return value;

			if (_dockUI == null)
				_dockUI = new DockUI ();

			_dockUI.Start (editorService, value);
			editorService.DropDownControl (_dockUI);
			value = _dockUI.Value;
			_dockUI.End ();

			return value;
		}

		public override UITypeEditorEditStyle GetEditStyle (ITypeDescriptorContext context)
		{
			return UITypeEditorEditStyle.DropDown;
		}

		/// <summary>
		///  User interface for the DockEditor.
		/// </summary>
		private sealed class DockUI : SelectionPanelBase
		{
			private readonly ContainerPlaceholder _container;
			private readonly RadioButton _fill;
			private readonly RadioButton _left;
			private readonly RadioButton[] _leftRightOrder;
			private readonly RadioButton _none;
			private readonly RadioButton _right;
			private readonly RadioButton[] _tabOrder;
			private readonly RadioButton _top;
			private readonly RadioButton _bottom;
			private readonly RadioButton[] _upDownOrder;

			public DockUI ()
			{
				_container = new ContainerPlaceholder () {
					Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Bottom | AnchorStyles.Right,
					Dock = DockStyle.Fill
				};

				_none = new SelectionPanelRadioButton () {
					Dock = DockStyle.Bottom,
					Name = "_none",
					Text = DockStyle.None.ToString (),
					TabIndex = 0,
					TabStop = true,
					Appearance = Appearance.Button,
					AccessibleName = "None"
				};

				_right = new SelectionPanelRadioButton () {
					Dock = DockStyle.Right,
					TabIndex = 4,
					TabStop = true,
					Name = "_right",
					// Needs at least one character so focus rect will show.
					Text = " ",
					Appearance = Appearance.Button,
					AccessibleName = "Right"
				};

				_left = new SelectionPanelRadioButton () {
					Dock = DockStyle.Left,
					TabIndex = 2,
					TabStop = true,
					Name = "_left",
					Text = " ",
					Appearance = Appearance.Button,
					AccessibleName = "Left"
				};

				_top = new SelectionPanelRadioButton () {
					Dock = DockStyle.Top,
					TabIndex = 1,
					TabStop = true,
					Name = "_top",
					Text = " ",
					Appearance = Appearance.Button,
					AccessibleName = "Top"
				};

				_bottom = new SelectionPanelRadioButton () {
					Dock = DockStyle.Bottom,
					TabIndex = 5,
					TabStop = true,
					Name = "_bottom",
					Text = " ",
					Appearance = Appearance.Button,
					AccessibleName = "Bottom"
				};

				_fill = new SelectionPanelRadioButton () {
					Dock = DockStyle.Fill,
					TabIndex = 3,
					TabStop = true,
					Name = "_fill",
					Text = " ",
					Appearance = Appearance.Button,
					AccessibleName = "Fill"
				};

				InitializeComponent ();

				_upDownOrder = new RadioButton[] { _top, _fill, _bottom, _none };
				_leftRightOrder = new RadioButton[] { _left, _fill, _right };
				_tabOrder = new RadioButton[] { _top, _left, _fill, _right, _bottom, _none };
			}

			private DockStyle DockStyle {
				get {
					if (CheckedControl == _fill)
						return DockStyle.Fill;
					else if (CheckedControl == _left)
						return DockStyle.Left;
					else if (CheckedControl == _right)
						return DockStyle.Right;
					else if (CheckedControl == _top)
						return DockStyle.Top;
					else if (CheckedControl == _bottom)
						return DockStyle.Bottom;

					return DockStyle.None;
				}
				set {
					switch (value) {
					case DockStyle.None:
						CheckedControl = _none;
						break;
					case DockStyle.Fill:
						CheckedControl = _fill;
						break;
					case DockStyle.Left:
						CheckedControl = _left;
						break;
					case DockStyle.Right:
						CheckedControl = _right;
						break;
					case DockStyle.Top:
						CheckedControl = _top;
						break;
					case DockStyle.Bottom:
						CheckedControl = _bottom;
						break;
					}
				}
			}

			protected override ControlCollection SelectionOptions {
				get { return _container.Controls; }
			}

			// .NET's InitializeComponent(s_initialSystemDpi) at 96 dpi: the logical sizes as they are.
			private void InitializeComponent ()
			{
				const int LogicalNoneHeight = 24;
				const int LogicalNoneWidth = 90;
				const int LogicalControlWidth = 94;
				const int LogicalControlHeight = 116;
				const int LogicalContainerOffset = 2;
				const int LogicalContainerSize = 90;
				const int LogicalButtonSize = 20;

				SetBounds (0, 0, LogicalControlWidth, LogicalControlHeight);

				BackColor = SystemColors.Control;
				ForeColor = SystemColors.ControlText;
				AccessibleName = "Dock Picker";

				_none.Size = new Size (LogicalNoneWidth, LogicalNoneHeight);

				_container.Location = new Point (LogicalContainerOffset, LogicalContainerOffset);
				_container.Size = new Size (LogicalContainerSize, LogicalContainerSize);

				Size buttonSize = new Size (LogicalButtonSize, LogicalButtonSize);

				_right.Size = buttonSize;
				_left.Size = buttonSize;
				_top.Size = buttonSize;
				_bottom.Size = buttonSize;
				_fill.Size = buttonSize;

				Controls.Clear ();
				Controls.Add (_container);

				_container.Controls.Clear ();
				_container.Controls.AddRange (new Control[] {
					_fill,
					_left,
					_right,
					_top,
					_bottom,
					_none
				});

				ConfigureButtons ();
			}

			protected override RadioButton ProcessDownKey (RadioButton checkedControl)
			{
				return ProcessUpDown (checkedControl, false);
			}

			protected override RadioButton ProcessLeftKey (RadioButton checkedControl)
			{
				return ProcessLeftRight (checkedControl, true);
			}

			private RadioButton ProcessLeftRight (RadioButton checkedControl, bool leftDirection)
			{
				int maxI = _leftRightOrder.Length - 1;
				for (int i = 0; i <= maxI; i++) {
					if (_leftRightOrder[i] == checkedControl) {
						return leftDirection
							? _leftRightOrder[Math.Max (i - 1, 0)]
							: _leftRightOrder[Math.Min (i + 1, maxI)];
					}
				}

				return checkedControl;
			}

			protected override RadioButton ProcessRightKey (RadioButton checkedControl)
			{
				return ProcessLeftRight (checkedControl, false);
			}

			protected override RadioButton ProcessTabKey (Keys keyData)
			{
				for (int i = 0; i < _tabOrder.Length; i++) {
					if (_tabOrder[i] == CheckedControl) {
						i += (keyData & Keys.Shift) == 0 ? 1 : -1;
						i = i < 0 ? i + _tabOrder.Length : i % _tabOrder.Length;
						return _tabOrder[i];
					}
				}

				return CheckedControl;
			}

			protected override RadioButton ProcessUpKey (RadioButton checkedControl)
			{
				return ProcessUpDown (checkedControl, true);
			}

			private RadioButton ProcessUpDown (RadioButton checkedControl, bool upDirection)
			{
				// If we're going up or down from one of the 'sides', act like we're doing
				// it from the center
				if (checkedControl == _left || checkedControl == _right)
					checkedControl = _fill;

				int maxI = _upDownOrder.Length - 1;
				for (int i = 0; i <= maxI; i++) {
					if (_upDownOrder[i] == checkedControl) {
						return upDirection
							? _upDownOrder[Math.Max (i - 1, 0)]
							: _upDownOrder[Math.Min (i + 1, maxI)];
					}
				}

				return checkedControl;
			}

			protected override void SetInitialCheckedControl ()
			{
				DockStyle = Value is DockStyle ? (DockStyle) Value : DockStyle.None;
			}

			protected override void UpdateValue ()
			{
				Value = DockStyle;
			}

			private class ContainerPlaceholder : Control
			{
				public ContainerPlaceholder ()
				{
					BackColor = SystemColors.Control;
					TabStop = false;
				}

				protected override void OnPaint (PaintEventArgs e)
				{
					Rectangle rc = ClientRectangle;
					ControlPaint.DrawButton (e.Graphics, rc, ButtonState.Pushed);
				}
			}
		}
	}
}
