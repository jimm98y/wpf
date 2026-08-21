// The modern Windows look, as a theme of its own.
//
// ThemeWin32Classic draws what Windows drew before visual styles: two-pixel carved bevels around
// every input, and Office 2003's slate-blue gradients on tool strips. That is still exactly right
// for an application that asks for the classic look, so it is left alone -- this derives from it
// and overrides only what Windows itself changed.
//
// The other modern theme here, ThemeVisualStyles, cannot be used on this stack: it asks UXTheme to
// draw into an HDC, and in GPU-raster mode a Graphics has no native surface behind it. This one is
// managed drawing all the way down, so it works wherever the classic theme does.

using System.Drawing;

namespace System.Windows.Forms
{
	/// <summary>
	/// Windows 11's flat light-blue selection colours for tool strips and menus.
	/// </summary>
	/// <remarks>
	/// Every one of these is virtual on the base, so this changes nothing for anybody still using
	/// <see cref="ProfessionalColorTable"/> itself. The checked entries matter most: the classic
	/// table leaves them <see cref="Color.Empty"/>, which makes the professional renderer fall back
	/// to a 3D sunken frame for a toggled button -- the property grid's categorized/alphabetical
	/// pair, for instance, which is what reads as a decade old next to a stock WinForms build.
	/// </remarks>
	internal class ModernProfessionalColorTable : ProfessionalColorTable
	{
		// The three states Windows has used since 10: hover, selected/checked, and pressed.
		private static readonly Color Hover = Color.FromArgb (229, 243, 255);
		private static readonly Color Selected = Color.FromArgb (204, 232, 255);
		private static readonly Color Pressed = Color.FromArgb (153, 209, 255);
		private static readonly Color PressedBorder = Color.FromArgb (102, 176, 234);

		public override Color ButtonCheckedGradientBegin => Selected;
		public override Color ButtonCheckedGradientMiddle => Selected;
		public override Color ButtonCheckedGradientEnd => Selected;
		public override Color ButtonCheckedHighlight => Selected;
		public override Color ButtonCheckedHighlightBorder => Pressed;

		public override Color ButtonSelectedGradientBegin => Hover;
		public override Color ButtonSelectedGradientMiddle => Hover;
		public override Color ButtonSelectedGradientEnd => Hover;
		public override Color ButtonSelectedHighlight => Hover;
		public override Color ButtonSelectedHighlightBorder => Selected;
		public override Color ButtonSelectedBorder => Selected;

		public override Color ButtonPressedGradientBegin => Pressed;
		public override Color ButtonPressedGradientMiddle => Pressed;
		public override Color ButtonPressedGradientEnd => Pressed;
		public override Color ButtonPressedHighlight => Pressed;
		public override Color ButtonPressedHighlightBorder => PressedBorder;
		public override Color ButtonPressedBorder => PressedBorder;

		public override Color CheckBackground => Selected;
		public override Color CheckSelectedBackground => Pressed;
		public override Color CheckPressedBackground => Pressed;

		public override Color MenuItemSelected => Hover;
		public override Color MenuItemSelectedGradientBegin => Hover;
		public override Color MenuItemSelectedGradientEnd => Hover;
		public override Color MenuItemBorder => Selected;
		public override Color MenuItemPressedGradientBegin => Selected;
		public override Color MenuItemPressedGradientMiddle => Selected;
		public override Color MenuItemPressedGradientEnd => Selected;

		// Flat, not graduated. Windows stopped shading tool strips with a vertical gradient at the
		// same time it stopped bevelling them; leaving the Office 2003 gradient in place was the
		// remaining thing that made a tool bar look shaded next to a stock one.
		private static Color Surface => SystemColors.Control;

		public override Color ToolStripGradientBegin => Surface;
		public override Color ToolStripGradientMiddle => Surface;
		public override Color ToolStripGradientEnd => Surface;
		public override Color ToolStripPanelGradientBegin => Surface;
		public override Color ToolStripPanelGradientEnd => Surface;
		public override Color ToolStripContentPanelGradientBegin => Surface;
		public override Color ToolStripContentPanelGradientEnd => Surface;
		public override Color MenuStripGradientBegin => Surface;
		public override Color MenuStripGradientEnd => Surface;
		public override Color StatusStripGradientBegin => Surface;
		public override Color StatusStripGradientEnd => Surface;
		public override Color ImageMarginGradientBegin => Surface;
		public override Color ImageMarginGradientMiddle => Surface;
		public override Color ImageMarginGradientEnd => Surface;
	}

	internal class ThemeWin11 : ThemeWin32Classic
	{
		private readonly ProfessionalColorTable color_table = new ModernProfessionalColorTable ();

		public override ProfessionalColorTable ColorTable => color_table;

		/// <summary>Always the professional renderer with this theme's colours. The system renderer
		/// a "flat" tool bar would otherwise get draws the pre-visual-styles look, which is what put
		/// carved 3D frames on the property grid's toggled buttons instead of a blue fill.</summary>
		public override ToolStripRenderer CreateToolBarRenderer (ToolBarAppearance appearance)
		{
			return new ToolStripProfessionalRenderer (color_table);
		}

		// Windows draws a single hairline around an input, not a carved bevel. These are the two
		// greys it uses: #7A7A7A around something you type in, #ADADAD around something you press.
		private static readonly Color InputBorder = Color.FromArgb (122, 122, 122);
		private static readonly Color RaisedBorder = Color.FromArgb (173, 173, 173);
		private static readonly Color EtchedBorder = Color.FromArgb (223, 223, 223);

		public override void ComboBoxDrawBackground (ComboBox comboBox, Graphics g, Rectangle clippingArea, FlatStyle style)
		{
			if (!comboBox.Enabled)
				g.FillRectangle (ResPool.GetSolidBrush (ColorControl), comboBox.ClientRectangle);

			if (comboBox.DropDownStyle == ComboBoxStyle.Simple)
				g.FillRectangle (ResPool.GetSolidBrush (comboBox.Parent.BackColor), comboBox.ClientRectangle);

			if (style == FlatStyle.Popup && (comboBox.Entered || comboBox.Focused)) {
				Rectangle area = comboBox.TextArea;
				area.Height -= 1;
				area.Width -= 1;
				g.DrawRectangle (ResPool.GetPen (SystemColors.ControlDark), area);
				g.DrawLine (ResPool.GetPen (SystemColors.ControlDark), comboBox.ButtonArea.X - 1, comboBox.ButtonArea.Top, comboBox.ButtonArea.X - 1, comboBox.ButtonArea.Bottom);
			}

			bool is_flat = style == FlatStyle.Flat || style == FlatStyle.Popup;
			if (!is_flat && clippingArea.IntersectsWith (comboBox.TextArea)) {
				Rectangle border = comboBox.TextArea;
				border.Width -= 1;
				border.Height -= 1;
				g.DrawRectangle (ResPool.GetPen (InputBorder), border);
			}
		}

		/// <summary>
		/// Just the arrow. The classic drop-down button is a raised 3D button in its own right, and
		/// that chrome around the arrow is not something Windows has drawn on a combo box for a very
		/// long time -- it now blends into the field and only the glyph shows.
		/// </summary>
		public override void CPDrawComboButton (Graphics graphics, Rectangle rectangle, ButtonState state)
		{
			if ((state & ButtonState.Inactive) != 0) {
				DrawComboArrow (graphics, rectangle, SystemColors.GrayText);
				return;
			}

			// Pressed gets the same light-blue wash a pressed tool bar button gets, so the two agree.
			if ((state & (ButtonState.Pushed | ButtonState.Checked)) != 0)
				graphics.FillRectangle (ResPool.GetSolidBrush (Color.FromArgb (204, 232, 255)), rectangle);

			DrawComboArrow (graphics, rectangle, SystemColors.ControlText);
		}

		private void DrawComboArrow (Graphics graphics, Rectangle rectangle, Color color)
		{
			// A 7x4 triangle, centred: the proportions Windows uses.
			int cx = rectangle.X + rectangle.Width / 2;
			int cy = rectangle.Y + rectangle.Height / 2;
			var arrow = new Point [] {
				new Point (cx - 3, cy - 2),
				new Point (cx + 4, cy - 2),
				new Point (cx, cy + 2),
			};
			graphics.FillPolygon (ResPool.GetSolidBrush (color), arrow);
		}

		/// <summary>
		/// One hairline instead of the classic two-pixel bevel. Everything that asks for a 3D
		/// border comes through here -- text boxes, list and tree views, group boxes, status bar
		/// panels, the data grid -- so flattening it here flattens all of them, and only for this
		/// theme.
		/// </summary>
		public override void CPDrawBorder3D (Graphics graphics, Rectangle rectangle, Border3DStyle style,
						     Border3DSide sides, Color control_color)
		{
			// Adjust asks for the border to be drawn OUTSIDE the rectangle, as the classic
			// implementation does by growing it before drawing.
			Rectangle rect = rectangle;
			if ((style & Border3DStyle.Adjust) != 0) {
				rect.Y -= 2;
				rect.X -= 2;
				rect.Width += 4;
				rect.Height += 4;
			}

			Color color;
			switch (style & ~Border3DStyle.Adjust) {
			case Border3DStyle.Raised:
			case Border3DStyle.RaisedInner:
			case Border3DStyle.RaisedOuter:
				color = RaisedBorder;
				break;
			case Border3DStyle.Etched:
			case Border3DStyle.Bump:
			case Border3DStyle.Flat:
				color = EtchedBorder;
				break;
			default:
				color = InputBorder;
				break;
			}

			Pen pen = ResPool.GetPen (color);
			int right = rect.Right - 1;
			int bottom = rect.Bottom - 1;

			if ((sides & Border3DSide.Left) != 0)
				graphics.DrawLine (pen, rect.Left, rect.Top, rect.Left, bottom);
			if ((sides & Border3DSide.Top) != 0)
				graphics.DrawLine (pen, rect.Left, rect.Top, right, rect.Top);
			if ((sides & Border3DSide.Right) != 0)
				graphics.DrawLine (pen, right, rect.Top, right, bottom);
			if ((sides & Border3DSide.Bottom) != 0)
				graphics.DrawLine (pen, rect.Left, bottom, right, bottom);
		}
	}
}
