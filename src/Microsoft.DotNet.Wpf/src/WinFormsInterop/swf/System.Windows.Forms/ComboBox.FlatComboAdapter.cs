// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// From .NET's System.Windows.Forms (dotnet/winforms), adapted to the port where noted.

// .NET's ComboBox.FlatComboAdapter: how a Flat or Popup combo box is drawn -- Windows paints the
// control, then WM_PAINT draws these borders and the drop-down button over it.

using System.Drawing;

namespace System.Windows.Forms
{
	public partial class ComboBox
	{
		private FlatComboAdapter flat_combo_adapter;

		internal FlatComboAdapter FlatComboBoxAdapter {
			get {
				if (flat_combo_adapter == null || !flat_combo_adapter.IsValid (this))
					flat_combo_adapter = CreateFlatComboAdapterInstance ();
				return flat_combo_adapter;
			}
		}

		internal virtual FlatComboAdapter CreateFlatComboAdapterInstance ()
		{
			return new FlatComboAdapter (this, smallButton: false);
		}

		// .NET's MouseIsOver: the pointer is over the control.
		internal bool MouseIsOver => Entered;

		internal class FlatComboAdapter
		{
			private Rectangle _outerBorder;
			private Rectangle _innerBorder;
			private Rectangle _innerInnerBorder;
			internal Rectangle _dropDownRect;
			private Rectangle _whiteFillRect;
			private Rectangle _clientRect;
			private readonly RightToLeft _origRightToLeft;
			private const int WhiteFillRectWidth = 5;
			protected static int s_offsetPixels = 2;

			public FlatComboAdapter (ComboBox comboBox, bool smallButton)
			{
				_clientRect = comboBox.ClientRectangle;
				int arrowWidth = SystemInformation.HorizontalScrollBarArrowWidth;
				_outerBorder = new Rectangle (_clientRect.Location, new Size (_clientRect.Width - 1, _clientRect.Height - 1));
				_innerBorder = new Rectangle (_outerBorder.X + 1, _outerBorder.Y + 1, _outerBorder.Width - arrowWidth - 2, _outerBorder.Height - 2);
				_innerInnerBorder = new Rectangle (_innerBorder.X + 1, _innerBorder.Y + 1, _innerBorder.Width - 2, _innerBorder.Height - 2);
				_dropDownRect = new Rectangle (_innerBorder.Right + 1, _innerBorder.Y, arrowWidth, _innerBorder.Height + 1);
				if (smallButton) {
					_whiteFillRect = _dropDownRect;
					_whiteFillRect.Width = WhiteFillRectWidth;
					_dropDownRect.X += WhiteFillRectWidth;
					_dropDownRect.Width -= WhiteFillRectWidth;
				}
				_origRightToLeft = comboBox.RightToLeft;
				if (_origRightToLeft == RightToLeft.Yes) {
					_innerBorder.X = _clientRect.Width - _innerBorder.Right;
					_innerInnerBorder.X = _clientRect.Width - _innerInnerBorder.Right;
					_dropDownRect.X = _clientRect.Width - _dropDownRect.Right;
					_whiteFillRect.X = _clientRect.Width - _whiteFillRect.Right + 1;
				}
			}

			public bool IsValid (ComboBox combo)
			{
				return combo.ClientRectangle == _clientRect && combo.RightToLeft == _origRightToLeft;
			}

			public virtual void DrawFlatCombo (ComboBox comboBox, Graphics g)
			{
				if (comboBox.DropDownStyle == ComboBoxStyle.Simple)
					return;
				Color outerBorderColor = GetOuterBorderColor (comboBox);
				Color innerBorderColor = GetInnerBorderColor (comboBox);
				bool rtl = comboBox.RightToLeft == RightToLeft.Yes;
				DrawFlatComboDropDown (comboBox, g, _dropDownRect);
				if (_whiteFillRect.Width > 0 && _whiteFillRect.Height > 0) {
					using (var brush = new SolidBrush (innerBorderColor))
						g.FillRectangle (brush, _whiteFillRect);
				}
				using (var outer = new Pen (outerBorderColor)) {
					g.DrawRectangle (outer, _outerBorder);
					if (rtl)
						g.DrawRectangle (outer, new Rectangle (_outerBorder.X, _outerBorder.Y, _dropDownRect.Width + 1, _outerBorder.Height));
					else
						g.DrawRectangle (outer, new Rectangle (_dropDownRect.X, _outerBorder.Y, _outerBorder.Right - _dropDownRect.X, _outerBorder.Height));
					using (var inner = new Pen (innerBorderColor)) {
						g.DrawRectangle (inner, _innerBorder);
						g.DrawRectangle (inner, _innerInnerBorder);
					}
					if (comboBox.Enabled && comboBox.FlatStyle != FlatStyle.Popup)
						return;
					bool focused = comboBox.ContainsFocus || comboBox.MouseIsOver;
					using (var popup = new Pen (GetPopupOuterBorderColor (comboBox, focused))) {
						Pen pen = comboBox.Enabled ? popup : SystemPens.Control;
						if (rtl)
							g.DrawRectangle (pen, new Rectangle (_outerBorder.X, _outerBorder.Y, _dropDownRect.Width + 1, _outerBorder.Height));
						else
							g.DrawRectangle (pen, new Rectangle (_dropDownRect.X, _outerBorder.Y, _outerBorder.Right - _dropDownRect.X, _outerBorder.Height));
						g.DrawRectangle (popup, _outerBorder);
					}
				}
			}

			protected virtual void DrawFlatComboDropDown (ComboBox comboBox, Graphics g, Rectangle dropDownRect)
			{
				g.FillRectangle (SystemBrushes.Control, dropDownRect);
				Brush brush = comboBox.Enabled ? SystemBrushes.ControlText : SystemBrushes.ControlDark;
				Point middle = new Point (dropDownRect.Left + dropDownRect.Width / 2, dropDownRect.Top + dropDownRect.Height / 2);
				if (_origRightToLeft == RightToLeft.Yes)
					middle.X -= dropDownRect.Width % 2;
				else
					middle.X += dropDownRect.Width % 2;
				g.FillPolygon (brush, new[] {
					new Point (middle.X - s_offsetPixels, middle.Y - 1),
					new Point (middle.X + s_offsetPixels + 1, middle.Y - 1),
					new Point (middle.X, middle.Y + s_offsetPixels),
				});
			}

			protected virtual Color GetOuterBorderColor (ComboBox comboBox)
			{
				return comboBox.Enabled ? SystemColors.Window : SystemColors.ControlDark;
			}

			protected virtual Color GetPopupOuterBorderColor (ComboBox comboBox, bool focused)
			{
				if (!comboBox.Enabled)
					return SystemColors.ControlDark;
				return focused ? SystemColors.ControlDark : SystemColors.Window;
			}

			protected virtual Color GetInnerBorderColor (ComboBox comboBox)
			{
				return comboBox.Enabled ? comboBox.BackColor : SystemColors.Control;
			}
		}
	}
}
