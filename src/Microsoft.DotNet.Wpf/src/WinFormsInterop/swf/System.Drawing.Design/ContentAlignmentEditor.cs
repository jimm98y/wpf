// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
//
// Ported from dotnet/winforms (System.Windows.Forms.Design/src/System/Drawing/Design/
// ContentAlignmentEditor.cs + ContentAlignmentEditor.ContentUI.cs): nine Appearance.Button radio
// buttons laid out as .NET lays them out. The port runs at 96 dpi, so every ScaleHelper value is
// its logical one.

using System.ComponentModel;
using System.Windows.Forms;
using System.Windows.Forms.Design;

namespace System.Drawing.Design
{
	/// <summary>
	///  Provides a <see cref="UITypeEditor"/> for visually editing content alignment.
	/// </summary>
	public partial class ContentAlignmentEditor : UITypeEditor
	{
		private ContentUI _contentUI;

		/// <summary>
		///  Edits the given object value using the editor style provided by GetEditStyle.
		/// </summary>
		public override object EditValue (ITypeDescriptorContext context, IServiceProvider provider, object value)
		{
			IWindowsFormsEditorService editorService = provider == null
				? null
				: provider.GetService (typeof (IWindowsFormsEditorService)) as IWindowsFormsEditorService;
			if (editorService == null)
				return value;

			if (_contentUI == null)
				_contentUI = new ContentUI ();

			_contentUI.Start (editorService, value);
			editorService.DropDownControl (_contentUI);
			value = _contentUI.Value;
			_contentUI.End ();

			return value;
		}

		public override UITypeEditorEditStyle GetEditStyle (ITypeDescriptorContext context)
		{
			return UITypeEditorEditStyle.DropDown;
		}

		/// <summary>
		///  Control we use to provide the content alignment UI.
		/// </summary>
		private sealed class ContentUI : SelectionPanelBase
		{
			private readonly SelectionPanelRadioButton _topLeft = new SelectionPanelRadioButton ();
			private readonly SelectionPanelRadioButton _topCenter = new SelectionPanelRadioButton ();
			private readonly SelectionPanelRadioButton _topRight = new SelectionPanelRadioButton ();
			private readonly SelectionPanelRadioButton _middleLeft = new SelectionPanelRadioButton ();
			private readonly SelectionPanelRadioButton _middleCenter = new SelectionPanelRadioButton ();
			private readonly SelectionPanelRadioButton _middleRight = new SelectionPanelRadioButton ();
			private readonly SelectionPanelRadioButton _bottomLeft = new SelectionPanelRadioButton ();
			private readonly SelectionPanelRadioButton _bottomCenter = new SelectionPanelRadioButton ();
			private readonly SelectionPanelRadioButton _bottomRight = new SelectionPanelRadioButton ();

			public ContentUI ()
			{
				InitComponent ();
			}

			private ContentAlignment Align {
				get {
					if (CheckedControl == _topLeft)
						return ContentAlignment.TopLeft;
					else if (CheckedControl == _topCenter)
						return ContentAlignment.TopCenter;
					else if (CheckedControl == _topRight)
						return ContentAlignment.TopRight;
					else if (CheckedControl == _middleLeft)
						return ContentAlignment.MiddleLeft;
					else if (CheckedControl == _middleCenter)
						return ContentAlignment.MiddleCenter;
					else if (CheckedControl == _middleRight)
						return ContentAlignment.MiddleRight;
					else if (CheckedControl == _bottomLeft)
						return ContentAlignment.BottomLeft;
					else if (CheckedControl == _bottomCenter)
						return ContentAlignment.BottomCenter;
					else
						return ContentAlignment.BottomRight;
				}
				set {
					switch (value) {
					case ContentAlignment.TopLeft:
						CheckedControl = _topLeft;
						break;
					case ContentAlignment.TopCenter:
						CheckedControl = _topCenter;
						break;
					case ContentAlignment.TopRight:
						CheckedControl = _topRight;
						break;
					case ContentAlignment.MiddleLeft:
						CheckedControl = _middleLeft;
						break;
					case ContentAlignment.MiddleCenter:
						CheckedControl = _middleCenter;
						break;
					case ContentAlignment.MiddleRight:
						CheckedControl = _middleRight;
						break;
					case ContentAlignment.BottomLeft:
						CheckedControl = _bottomLeft;
						break;
					case ContentAlignment.BottomCenter:
						CheckedControl = _bottomCenter;
						break;
					case ContentAlignment.BottomRight:
						CheckedControl = _bottomRight;
						break;
					}
				}
			}

			protected override ControlCollection SelectionOptions {
				get { return Controls; }
			}

			private void InitComponent ()
			{
				BackColor = SystemColors.Control;
				ForeColor = SystemColors.ControlText;
				AccessibleName = "Alignment Picker";

				_topLeft.TabIndex = 8;
				_topLeft.Text = string.Empty;
				_topLeft.Name = "_topLeft";
				_topLeft.Appearance = Appearance.Button;
				_topLeft.AccessibleName = "Top Left";

				_topCenter.TabIndex = 0;
				_topCenter.Text = string.Empty;
				_topCenter.Name = "_topCenter";
				_topCenter.Appearance = Appearance.Button;
				_topCenter.AccessibleName = "Top Center";

				_topRight.TabIndex = 1;
				_topRight.Text = string.Empty;
				_topRight.Name = "_topRight";
				_topRight.Appearance = Appearance.Button;
				_topRight.AccessibleName = "Top Right";

				_middleLeft.TabIndex = 2;
				_middleLeft.Text = string.Empty;
				_middleLeft.Name = "_middleLeft";
				_middleLeft.Appearance = Appearance.Button;
				_middleLeft.AccessibleName = "Middle Left";

				_middleCenter.TabIndex = 3;
				_middleCenter.Text = string.Empty;
				_middleCenter.Name = "_middleCenter";
				_middleCenter.Appearance = Appearance.Button;
				_middleCenter.AccessibleName = "Middle Center";

				_middleRight.TabIndex = 4;
				_middleRight.Text = string.Empty;
				_middleRight.Name = "_middleRight";
				_middleRight.Appearance = Appearance.Button;
				_middleRight.AccessibleName = "Middle Right";

				_bottomLeft.TabIndex = 5;
				_bottomLeft.Text = string.Empty;
				_bottomLeft.Name = "_bottomLeft";
				_bottomLeft.Appearance = Appearance.Button;
				_bottomLeft.AccessibleName = "Bottom Left";

				_bottomCenter.TabIndex = 6;
				_bottomCenter.Text = string.Empty;
				_bottomCenter.Name = "_bottomCenter";
				_bottomCenter.Appearance = Appearance.Button;
				_bottomCenter.AccessibleName = "Bottom Middle";

				_bottomRight.TabIndex = 7;
				_bottomRight.Text = string.Empty;
				_bottomRight.Name = "_bottomRight";
				_bottomRight.Appearance = Appearance.Button;
				_bottomRight.AccessibleName = "Bottom Right";

				SetDimensions ();
				ConfigureButtons ();
			}

			private void ResetAnchorStyle ()
			{
				const AnchorStyles DefaultCenterAnchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
				const AnchorStyles DefaultRightAnchor = AnchorStyles.Top | AnchorStyles.Right;

				_topCenter.Anchor = DefaultCenterAnchor;
				_topRight.Anchor = DefaultRightAnchor;
				_middleCenter.Anchor = DefaultCenterAnchor;
				_middleRight.Anchor = DefaultRightAnchor;
				_bottomCenter.Anchor = DefaultCenterAnchor;
				_bottomRight.Anchor = DefaultRightAnchor;
			}

			// .NET's SetDimensions(ScaleHelper.InitialSystemDpi) at 96 dpi: the logical sizes as they
			// are. (RescaleConstantsForDpi, which re-ran this at a new dpi, has no counterpart here.)
			private void SetDimensions ()
			{
				SuspendLayout ();
				try {
					// This is to invoke parent changed message that help rescaling the controls based on parent font (when it changed)
					Controls.Clear ();

					Size = new Size (125, 89);

					_topLeft.Size = new Size (24, 25);

					_topCenter.Location = new Point (32, 0);
					_topCenter.Size = new Size (59, 25);

					_topRight.Location = new Point (99, 0);
					_topRight.Size = new Size (24, 25);

					_middleLeft.Location = new Point (0, 32);
					_middleLeft.Size = new Size (24, 25);

					_middleCenter.Location = new Point (32, 32);
					_middleCenter.Size = new Size (59, 25);

					_middleRight.Location = new Point (99, 32);
					_middleRight.Size = new Size (24, 25);

					_bottomLeft.Location = new Point (0, 64);
					_bottomLeft.Size = new Size (24, 25);

					_bottomCenter.Location = new Point (32, 64);
					_bottomCenter.Size = new Size (59, 25);

					_bottomRight.Location = new Point (99, 64);
					_bottomRight.Size = new Size (24, 25);

					ResetAnchorStyle ();
					Controls.AddRange (new Control[] {
						_bottomRight,
						_bottomCenter,
						_bottomLeft,
						_middleRight,
						_middleCenter,
						_middleLeft,
						_topRight,
						_topCenter,
						_topLeft
					});
				} finally {
					ResumeLayout ();
				}
			}

			/// <summary>
			///  Imagine a grid to choose alignment:
			///
			///   [TL] [TC] [TR]
			///   [ML] [MC] [MR]
			///   [BL] [BC] [BR]
			///
			///  Pressing Down on any of these will lead to the same column but
			///  a lower row; and pressing Down on the bottom row is meaningless
			/// </summary>
			protected override RadioButton ProcessDownKey (RadioButton checkedControl)
			{
				if (checkedControl == _topRight)
					return _middleRight;
				else if (checkedControl == _middleRight)
					return _bottomRight;
				else if (checkedControl == _topCenter)
					return _middleCenter;
				else if (checkedControl == _middleCenter)
					return _bottomCenter;
				else if (checkedControl == _topLeft)
					return _middleLeft;
				else if (checkedControl == _middleLeft)
					return _bottomLeft;

				return checkedControl;
			}

			/// <summary>
			///  Pressing Up on any of these will lead to the same column but
			///  a higher row; and pressing Up on the top row is meaningless
			/// </summary>
			protected override RadioButton ProcessUpKey (RadioButton checkedControl)
			{
				if (checkedControl == _bottomRight)
					return _middleRight;
				else if (checkedControl == _middleRight)
					return _topRight;
				else if (checkedControl == _bottomCenter)
					return _middleCenter;
				else if (checkedControl == _middleCenter)
					return _topCenter;
				else if (checkedControl == _bottomLeft)
					return _middleLeft;
				else if (checkedControl == _middleLeft)
					return _topLeft;

				return checkedControl;
			}

			/// <summary>
			///  Pressing Right on any of these will lead to the same row but a farther Right column;
			///  and pressing right on the right-most column is meaningless.
			/// </summary>
			protected override RadioButton ProcessRightKey (RadioButton checkedControl)
			{
				if (checkedControl == _bottomLeft)
					return _bottomCenter;
				else if (checkedControl == _middleLeft)
					return _middleCenter;
				else if (checkedControl == _topLeft)
					return _topCenter;
				else if (checkedControl == _bottomCenter)
					return _bottomRight;
				else if (checkedControl == _middleCenter)
					return _middleRight;
				else if (checkedControl == _topCenter)
					return _topRight;

				return checkedControl;
			}

			/// <summary>
			///  Pressing Left on any of these will lead to the same row but a farther left column; and pressing Left
			///  on the left-most column is meaningless
			/// </summary>
			protected override RadioButton ProcessLeftKey (RadioButton checkedControl)
			{
				if (checkedControl == _bottomRight)
					return _bottomCenter;
				else if (checkedControl == _middleRight)
					return _middleCenter;
				else if (checkedControl == _topRight)
					return _topCenter;
				else if (checkedControl == _bottomCenter)
					return _bottomLeft;
				else if (checkedControl == _middleCenter)
					return _middleLeft;
				else if (checkedControl == _topCenter)
					return _topLeft;

				return checkedControl;
			}

			protected override RadioButton ProcessTabKey (Keys keyData)
			{
				int nextTabIndex = CheckedControl.TabIndex + ((keyData & Keys.Shift) == 0 ? 1 : -1);
				if (nextTabIndex < 0)
					nextTabIndex = Controls.Count - 1;
				else if (nextTabIndex >= Controls.Count)
					nextTabIndex = 0;

				for (int i = 0; i < Controls.Count; i++) {
					RadioButton button = Controls[i] as RadioButton;
					if (button != null && Controls[i].TabIndex == nextTabIndex)
						return button;
				}

				return CheckedControl;
			}

			protected override void SetInitialCheckedControl ()
			{
				Align = Value is ContentAlignment ? (ContentAlignment) Value : ContentAlignment.MiddleLeft;
			}

			protected override void UpdateValue ()
			{
				Value = Align;
			}
		}
	}
}
