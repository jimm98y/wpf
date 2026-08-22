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
using System.Drawing.Drawing2D;

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
		// ---- buttons, check boxes and radio buttons -------------------------------------
		//
		// The classic theme builds these out of light/dark bevels. Windows draws a flat, slightly
		// rounded face with a hairline border, and fills a ticked box with the accent colour.

		private static readonly Color ButtonFaceNormal = Color.FromArgb (225, 225, 225);
		private static readonly Color ButtonBorderNormal = Color.FromArgb (173, 173, 173);
		private static readonly Color ButtonFaceHover = Color.FromArgb (229, 241, 251);
		private static readonly Color ButtonBorderHover = Color.FromArgb (0, 120, 215);
		private static readonly Color ButtonFacePressed = Color.FromArgb (204, 228, 247);
		private static readonly Color ButtonBorderPressed = Color.FromArgb (0, 84, 153);
		private static readonly Color ButtonFaceDisabled = Color.FromArgb (204, 204, 204);
		private static readonly Color ButtonBorderDisabled = Color.FromArgb (191, 191, 191);
		private static readonly Color GlyphBorder = Color.FromArgb (122, 122, 122);

		private const int ButtonCornerRadius = 3;

		private static GraphicsPath RoundedRect (Rectangle r, int radius)
		{
			var path = new GraphicsPath ();
			int d = radius * 2;
			if (d <= 0 || r.Width <= d || r.Height <= d) {
				path.AddRectangle (r);
				return path;
			}
			path.AddArc (r.X, r.Y, d, d, 180, 90);
			path.AddArc (r.Right - d, r.Y, d, d, 270, 90);
			path.AddArc (r.Right - d, r.Bottom - d, d, d, 0, 90);
			path.AddArc (r.X, r.Bottom - d, d, d, 90, 90);
			path.CloseFigure ();
			return path;
		}

		protected override void ButtonBase_DrawButton (ButtonBase button, Graphics dc)
		{
			// A check box or radio button rendered AS a button, and the flat styles, keep the base
			// behaviour: those have their own drawing and their own reasons.
			if (button is CheckBox || button is RadioButton ||
			    button.FlatStyle == FlatStyle.Flat || button.FlatStyle == FlatStyle.Popup) {
				base.ButtonBase_DrawButton (button, dc);
				return;
			}

			Color face, border;
			if (!button.Enabled) {
				face = ButtonFaceDisabled; border = ButtonBorderDisabled;
			} else if (button.Pressed) {
				face = ButtonFacePressed; border = ButtonBorderPressed;
			} else if (button.Entered) {
				face = ButtonFaceHover; border = ButtonBorderHover;
			} else {
				face = ButtonFaceNormal;
				// The default button, and a focused one, are outlined in the accent colour.
				border = button.IsDefault || button.Focused ? ButtonBorderHover : ButtonBorderNormal;
			}

			Rectangle r = button.ClientRectangle;
			r.Width -= 1;
			r.Height -= 1;
			if (r.Width <= 0 || r.Height <= 0)
				return;

			SmoothingMode old = dc.SmoothingMode;
			dc.SmoothingMode = SmoothingMode.AntiAlias;
			using (GraphicsPath path = RoundedRect (r, ButtonCornerRadius)) {
				dc.FillPath (ResPool.GetSolidBrush (face), path);
				dc.DrawPath (ResPool.GetPen (border), path);
			}
			dc.SmoothingMode = old;
		}

		public override void DrawButtonBackground (Graphics g, Button button, Rectangle clipArea)
		{
			// This -- not ButtonBase_DrawButton -- is what a plain Button paints through:
			// Button.OnPaint calls DrawButton, which calls this.
			ButtonBase_DrawButton (button, g);
		}



		/// <summary>The modern check box, drawn by hand. Shared by the control and by
		/// CPDrawCheckBox, which is the primitive a CheckedListBox and others reach for -- they
		/// never come through DrawCheckBoxGlyph, so without this they kept the classic tick.
		/// </summary>
		private void DrawModernCheck (Graphics g, Rectangle box, bool ticked, bool mixed,
					      bool enabled, bool hot)
		{
			Color fill, border;
			if (!enabled) {
				fill = ticked || mixed ? ButtonFaceDisabled : ColorWindow; border = ButtonBorderDisabled;
			} else if (ticked || mixed) {
				fill = border = hot ? ButtonBorderPressed : ButtonBorderHover;      // accent
			} else {
				fill = ColorWindow; border = hot ? ButtonBorderHover : GlyphBorder;
			}

			g.FillRectangle (ResPool.GetSolidBrush (fill), box);
			g.DrawRectangle (ResPool.GetPen (border), box);

			if (mixed) {
				var inner = Rectangle.Inflate (box, -3, -3);
				g.FillRectangle (ResPool.GetSolidBrush (enabled ? ColorWindow : ColorControlDark), inner);
				return;
			}
			if (!ticked)
				return;

			SmoothingMode old = g.SmoothingMode;
			g.SmoothingMode = SmoothingMode.AntiAlias;
			using (var pen = new Pen (enabled ? ColorWindow : ColorControlDark, 1.6f)) {
				float x = box.X, y = box.Y, w = box.Width, h = box.Height;
				g.DrawLines (pen, new PointF [] {
					new PointF (x + w * 0.22f, y + h * 0.52f),
					new PointF (x + w * 0.42f, y + h * 0.72f),
					new PointF (x + w * 0.78f, y + h * 0.28f),
				});
			}
			g.SmoothingMode = old;
		}

		public override void DrawCheckBoxGlyph (Graphics g, CheckBox cb, Rectangle glyphArea)
		{
			if (cb.Appearance == Appearance.Button || cb.FlatStyle == FlatStyle.Flat) {
				base.DrawCheckBoxGlyph (g, cb, glyphArea);
				return;
			}

			// Windows draws a 13x13 box; centre it in whatever space the layout gave us.
			int size = Math.Min (13, Math.Min (glyphArea.Width, glyphArea.Height));
			var box = new Rectangle (glyphArea.X + (glyphArea.Width - size) / 2,
						 glyphArea.Y + (glyphArea.Height - size) / 2,
						 size - 1, size - 1);

			bool ticked = cb.CheckState != CheckState.Unchecked;
			Color fill, border;
			if (!cb.Enabled) {
				fill = ticked ? ButtonFaceDisabled : ColorWindow; border = ButtonBorderDisabled;
			} else if (ticked) {
				fill = border = cb.Entered ? ButtonBorderPressed : ButtonBorderHover;   // accent
			} else {
				fill = ColorWindow; border = cb.Entered ? ButtonBorderHover : GlyphBorder;
			}

			g.FillRectangle (ResPool.GetSolidBrush (fill), box);
			g.DrawRectangle (ResPool.GetPen (border), box);

			if (cb.CheckState == CheckState.Indeterminate) {
				var inner = Rectangle.Inflate (box, -3, -3);
				g.FillRectangle (ResPool.GetSolidBrush (cb.Enabled ? ColorWindow : ColorControlDark), inner);
				return;
			}

			if (!ticked)
				return;

			// A tick, drawn as two strokes on the box's own scale so it stays centred at any size.
			SmoothingMode old = g.SmoothingMode;
			g.SmoothingMode = SmoothingMode.AntiAlias;
			using (var pen = new Pen (cb.Enabled ? ColorWindow : ColorControlDark, 1.6f)) {
				float x = box.X, y = box.Y, w = box.Width, h = box.Height;
				g.DrawLines (pen, new PointF [] {
					new PointF (x + w * 0.22f, y + h * 0.52f),
					new PointF (x + w * 0.42f, y + h * 0.72f),
					new PointF (x + w * 0.78f, y + h * 0.28f),
				});
			}
			g.SmoothingMode = old;
		}

		/// <summary>The 13x13 cell Windows draws a check box or radio button in, centred in
		/// whatever space the layout gave us.</summary>
		private static Rectangle CentredGlyph (Rectangle glyphArea)
		{
			int size = Math.Min (13, Math.Min (glyphArea.Width, glyphArea.Height));
			return new Rectangle (glyphArea.X + (glyphArea.Width - size) / 2,
					      glyphArea.Y + (glyphArea.Height - size) / 2, size, size);
		}

		public override void DrawRadioButtonGlyph (Graphics g, RadioButton rb, Rectangle glyphArea)
		{
			if (rb.Appearance == Appearance.Button || rb.FlatStyle == FlatStyle.Flat) {
				base.DrawRadioButtonGlyph (g, rb, glyphArea);
				return;
			}

			int size = Math.Min (13, Math.Min (glyphArea.Width, glyphArea.Height));
			var circle = new Rectangle (glyphArea.X + (glyphArea.Width - size) / 2,
						    glyphArea.Y + (glyphArea.Height - size) / 2,
						    size - 1, size - 1);

			Color border = !rb.Enabled ? ButtonBorderDisabled
				     : rb.Checked ? ButtonBorderHover
				     : rb.Entered ? ButtonBorderHover : GlyphBorder;

			SmoothingMode old = g.SmoothingMode;
			g.SmoothingMode = SmoothingMode.AntiAlias;
			g.FillEllipse (ResPool.GetSolidBrush (rb.Enabled ? ColorWindow : ColorControl), circle);
			g.DrawEllipse (ResPool.GetPen (border), circle);

			if (rb.Checked) {
				// The dot is the accent colour, inset by a third of the circle.
				var dot = Rectangle.Inflate (circle, -(circle.Width / 3), -(circle.Height / 3));
				g.FillEllipse (ResPool.GetSolidBrush (rb.Enabled ? border : ButtonBorderDisabled), dot);
			}
			g.SmoothingMode = old;
		}

		/// <summary>The drop-down button belongs to the field, not to a button of its own: fill it
		/// with the combo's background so the whole control reads as one box with a glyph in it.
		/// </summary>
		public override Color ComboBoxDropDownButtonBackColor (ComboBox comboBox)
		{
			return comboBox.Enabled ? comboBox.BackColor : ColorControl;
		}

		/// <summary>A chevron, which is how Windows expands a tree. The boxed +/- belongs to a much
		/// older shell.</summary>
		public override void DrawPropertyGridExpander (Graphics dc, Rectangle bounds, bool expanded, bool category, Color foreColor)
		{
			// Same 8x8 cell the boxed glyph occupied, so nothing around it has to move.
			int cx = bounds.X + bounds.Width / 2;
			int cy = bounds.Y + bounds.Height / 2;
			Point [] chevron = expanded
				? new Point [] { new Point (cx - 3, cy - 1), new Point (cx + 3, cy - 1), new Point (cx, cy + 3) }
				: new Point [] { new Point (cx - 1, cy - 3), new Point (cx + 3, cy),     new Point (cx - 1, cy + 3) };
			dc.FillPolygon (ResPool.GetSolidBrush (foreColor), chevron);
		}

		public override void CPDrawComboButton (Graphics graphics, Rectangle rectangle, ButtonState state)
		{
			if ((state & ButtonState.Inactive) != 0) {
				DrawComboArrow (graphics, rectangle, SystemColors.GrayText);
				return;
			}
			// Pressed gets the same light wash a pressed tool bar button gets, so the two agree.
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

		// ---- borders, scroll bars and tabs ------------------------------------------
		//
		// All three were reconstructions until now, and all three were wrong in ways no amount of
		// eyeballing was going to fix: an input had no border at all, the scroll bars were the
		// hatched 1995 ones, and the tab headers were sunken boxes. Windows draws them.

		public override void DrawControlBorder (Graphics dc, Rectangle bounds, Control control, bool sunken)
		{
			if (bounds.Width <= 1 || bounds.Height <= 1)
				return;
			// One hairline, not the classic carved bevel -- this theme's whole point. An input picks
			// up the accent colour when it has the focus, which is what Windows does with it.
			Color edge;
			if (!sunken)
				edge = RaisedBorder;
			else if (control != null && !control.Enabled)
				edge = ButtonBorderDisabled;
			else if (control != null && control.Focused)
				edge = ButtonBorderHover;
			else if (control != null && control.Entered)
				edge = GlyphBorder;
			else
				edge = InputBorder;
			dc.DrawRectangle (ResPool.GetPen (edge), bounds.X, bounds.Y, bounds.Width - 1, bounds.Height - 1);
		}

		public override void DrawScrollBar (Graphics dc, Rectangle clip, ScrollBar bar)
		{
			Rectangle thumb = bar.ThumbPos;
			Rectangle client = bar.ClientRectangle;
			int buttonSize = bar.vert ? bar.scrollbutton_height : bar.scrollbutton_width;

			Rectangle first = bar.vert ? new Rectangle (0, 0, bar.Width, buttonSize)
						     : new Rectangle (0, 0, buttonSize, bar.Height);
			Rectangle second = bar.vert ? new Rectangle (0, client.Height - buttonSize, bar.Width, buttonSize)
						      : new Rectangle (client.Width - buttonSize, 0, buttonSize, bar.Height);
			bar.FirstArrowArea = first;
			bar.SecondArrowArea = second;
			if (bar.vert) thumb.Width = bar.Width; else thumb.Height = bar.Height;
			bar.ThumbPos = thumb;

			DrawModernScrollBar (dc, bar, client, first, second, thumb);
		}


		public override void DrawTabControl (Graphics dc, Rectangle area, TabControl tab)
		{
			if (tab.Alignment != TabAlignment.Top || tab.Appearance != TabAppearance.Normal) {
				base.DrawTabControl (dc, area, tab);
				return;
			}
			DrawModernTabControl (dc, area, tab);
		}

		// ---- data grid -------------------------------------------------------------
		//
		// Mono draws a column header as a raised classic bevel. Windows has drawn them flat, white
		// and separated by hairlines for a long time, and that is the HEADER class -- the same one
		// a ListView's column headers come from, so both get it at once.

		public override bool DataGridViewColumnHeaderCellDrawBackground (DataGridViewColumnHeaderCell cell,
										Graphics g, Rectangle bounds)
		{
			return DrawHeaderCell (cell, g, bounds);
		}

		public override bool DataGridViewColumnHeaderCellDrawBorder (DataGridViewColumnHeaderCell cell,
									     Graphics g, Rectangle bounds)
		{
			// The part carries its own separator, so the classic three-line border would double it.
			return cell.DataGridView != null;
		}

		public override bool DataGridViewRowHeaderCellDrawBackground (DataGridViewRowHeaderCell cell,
									      Graphics g, Rectangle bounds)
		{
			return DrawHeaderCell (cell, g, bounds);
		}

		public override bool DataGridViewRowHeaderCellDrawBorder (DataGridViewRowHeaderCell cell,
									  Graphics g, Rectangle bounds)
		{
			return cell.DataGridView != null;
		}

		private bool DrawHeaderCell (DataGridViewHeaderCell cell, Graphics g, Rectangle bounds)
		{
			if (cell == null || cell.DataGridView == null)
				return false;
			DrawModernHeaderCell (g, bounds, false);
			return true;
		}

		// ---- scroll bar metrics ------------------------------------------------------
		//
		// Windows sizes a scroll bar at 17 pixels; the classic theme says 16, which for its own
		// drawing was fine. It is not fine for the Windows parts: the thumb is a nine-grid with
		// eight pixels of non-stretching margin on each side, so a 16-pixel bar has nothing left in
		// the middle to stretch and the thumb comes out as a one-pixel hairline instead of the two
		// -pixel bar next door. Ask Windows for its own numbers.

		private static readonly int ScrollBarWidth = 17;
		private static readonly int ScrollBarHeight = 17;

		public override int VerticalScrollBarWidth => ScrollBarWidth;
		public override int HorizontalScrollBarHeight => ScrollBarHeight;
		public override int ScrollBarButtonSize => ScrollBarWidth;

		public override void DrawScrollBarCorner (Graphics dc, Rectangle area)
		{
			if (area.Width <= 0 || area.Height <= 0)
				return;
			// The size box part draws a resize GRIP, which belongs to the bottom-right corner of a
			// sizable window, not to the gap between two scroll bars inside a control. Windows fills
			// that gap with the same panel the track is drawn on, so use the track.
			dc.FillRectangle (ResPool.GetSolidBrush (ScrollTrack), area);
		}

		// ---- list view headers -------------------------------------------------------
		//
		// Flat and white with a hairline separator, the same drawing the data grid's headers use.
		// A ListView in Details view is what SharpDevelop's About box shows its assembly list in.

		protected override void ListViewDrawColumnHeaderBackground (ListView listView, ColumnHeader columnHeader,
									    Graphics g, Rectangle area, Rectangle clippingArea)
		{
			bool pressed = listView.HeaderStyle == ColumnHeaderStyle.Clickable && columnHeader.Pressed;
			DrawModernHeaderCell (g, area, pressed);
		}

		// ---- progress bar, combo arrow, check box primitive --------------------------

		public override void DrawProgressBar (Graphics dc, Rectangle clip_rect, ProgressBar ctrl)
		{
			DrawModernProgressBar (dc, ctrl);
		}

		/// <summary>The check box primitive, which is what a CheckedListBox and a few other
		/// controls draw their boxes with -- they never reach DrawCheckBoxGlyph.</summary>
		public override void CPDrawCheckBox (Graphics dc, Rectangle rectangle, ButtonState state)
		{
			Rectangle box = CentredGlyph (rectangle);
			box.Width = Math.Max (box.Width - 1, 0);
			box.Height = Math.Max (box.Height - 1, 0);
			if (box.Width <= 0 || box.Height <= 0)
				return;
			DrawModernCheck (dc, box, (state & ButtonState.Checked) != 0, false,
					 (state & ButtonState.Inactive) == 0, (state & ButtonState.Pushed) != 0);
		}

		// ---- month calendar ----------------------------------------------------------
		//
		// The classic calendar is a blue caption bar with raised arrow buttons, day names in that
		// same blue, and a hand-drawn red ring around today. Windows draws a plain header with the
		// month in bold, chevrons with no button around them, grey day names, and a light box on
		// today's cell.

		protected override Color MonthCalendarTitleBackColor (MonthCalendar mc) => mc.BackColor;

		// The grey Windows fills a selected day with, measured off its own calendar.
		private static readonly Color CalendarSelection = Color.FromArgb (217, 217, 217);

		protected override Font MonthCalendarTodayFont (MonthCalendar mc) => mc.Font;

		protected override Font MonthCalendarTitleFont (MonthCalendar mc) => mc.Font;

		protected override bool MonthCalendarCentersToday (MonthCalendar mc) => true;

		// Windows marks the selected day with a pale accent fill and leaves the number dark; the
		// classic theme fills it with the caption colour and reverses the text out of it.
		// Windows fills the selected day with a plain grey and rings it in the accent colour --
		// the ring being the today marker drawn over it -- rather than washing the cell blue.
		protected override Color MonthCalendarSelectionBackColor (MonthCalendar mc)
			=> CalendarSelection;

		protected override Color MonthCalendarSelectionForeColor (MonthCalendar mc) => ColorControlText;

		public override void DrawMonthCalendar (Graphics dc, Rectangle clip_rectangle, MonthCalendar mc)
		{
			base.DrawMonthCalendar (dc, clip_rectangle, mc);

			// A standalone calendar draws its own "border" in the background colour -- which is to
			// say none at all -- and relies on the control having one. Windows shows a hairline
			// around the whole thing, so draw it.
			Rectangle r = mc.ClientRectangle;
			if (r.Width > 1 && r.Height > 1)
				dc.DrawRectangle (ResPool.GetPen (RaisedBorder), r.X, r.Y, r.Width - 1, r.Height - 1);
		}

		protected override Color MonthCalendarTitleForeColor (MonthCalendar mc) => ColorControlText;

		protected override Color MonthCalendarDayNameColor (MonthCalendar mc) => ColorGrayText;

		protected override void DrawMonthCalendarButton (Graphics dc, Rectangle rectangle, MonthCalendar mc,
								 Size title_size, int x_offset, Size button_size,
								 bool is_previous)
		{
			bool clicked = is_previous ? mc.is_previous_clicked : mc.is_next_clicked;
			Rectangle button = is_previous
				? new Rectangle (rectangle.X + 1 + x_offset,
						 rectangle.Y + 1 + ((title_size.Height - button_size.Height) / 2),
						 Math.Max (button_size.Width - 1, 0), Math.Max (button_size.Height - 1, 0))
				: new Rectangle (rectangle.Right - 1 - x_offset - button_size.Width,
						 rectangle.Y + 1 + ((title_size.Height - button_size.Height) / 2),
						 Math.Max (button_size.Width - 1, 0), Math.Max (button_size.Height - 1, 0));
			if (button.Width <= 0 || button.Height <= 0)
				return;

			// A pressed chevron gets the same light wash a pressed tool bar button gets; an idle one
			// has no chrome at all, which is what makes the header read as one flat strip.
			if (clicked)
				dc.FillRectangle (ResPool.GetSolidBrush (Color.FromArgb (204, 232, 255)), button);

			int cx = button.X + button.Width / 2;
			int cy = button.Y + button.Height / 2;
			int h = Math.Max (3, Math.Min (5, button.Height / 3));
			int w = Math.Max (2, h - 1);
			SmoothingMode old = dc.SmoothingMode;
			dc.SmoothingMode = SmoothingMode.AntiAlias;
			Color ink = mc.Enabled ? ColorControlText : ColorGrayText;
			Point [] arrow = is_previous
				? new Point [] { new Point (cx + w / 2, cy - h), new Point (cx + w / 2, cy + h), new Point (cx - w, cy) }
				: new Point [] { new Point (cx - w / 2, cy - h), new Point (cx - w / 2, cy + h), new Point (cx + w, cy) };
			dc.FillPolygon (ResPool.GetSolidBrush (ink), arrow);
			dc.SmoothingMode = old;
		}


		// Windows fills a selected day with a plain rectangle. The classic theme fills a pie -- a
		// circle for a lone day -- so once FillPie actually drew something the cell came out as an
		// ellipse.
		protected override void MonthCalendarFillSelection (Graphics dc, MonthCalendar mc, Rectangle rect,
					   Brush brush, float startAngle, float sweepAngle)
		{
			if (rect.Width > 0 && rect.Height > 0)
				dc.FillRectangle (brush, rect);
		}

		/// <summary>Windows marks today with a one-pixel frame whose corners are rounded by exactly
		/// one pixel -- which is to say the corner pixel is simply not painted. Drawing it as a curve
		/// does not survive: a path is flattened into unjoined segments and bulges inwards, an arc or
		/// a diagonal antialiases outwards and fringes the corner, and a half-pixel offset blurs the
		/// whole stroke. Four edges that each stop a pixel short need none of that.</summary>
		protected override void DrawTodayCircle (Graphics dc, Rectangle rectangle)
		{
			if (rectangle.Width <= 2 || rectangle.Height <= 2)
				return;
			// The same rectangle the selected-day fill occupies: this method is handed the cell less
			// one pixel while the fill gets it inset by one on every side, so a frame drawn on the rect
			// as given sat inside the fill and the grey showed past it on two edges.
			var box = new Rectangle (rectangle.X + 1, rectangle.Y + 1,
						   Math.Max (rectangle.Width - 1, 0), Math.Max (rectangle.Height - 1, 0));
			if (box.Width <= 1 || box.Height <= 1)
				return;

			Pen pen = ResPool.GetPen (ColorHotTrack);
			SmoothingMode old = dc.SmoothingMode;
			dc.SmoothingMode = SmoothingMode.None;
			dc.DrawLine (pen, box.X + 1, box.Y, box.Right - 1, box.Y);
			dc.DrawLine (pen, box.X + 1, box.Bottom, box.Right - 1, box.Bottom);
			dc.DrawLine (pen, box.X, box.Y + 1, box.X, box.Bottom - 1);
			dc.DrawLine (pen, box.Right, box.Y + 1, box.Right, box.Bottom - 1);
			dc.SmoothingMode = old;
		}

		// ---- the same drawing, without Windows ---------------------------------------
		//
		// Everything above asks Windows for its own artwork and falls through to here when there is
		// none to ask -- macOS, Linux, the browser, or WF_UXTHEME=0. That fall-through used to land
		// on the CLASSIC theme, which meant those heads got a 1995 progress bar and hatched scroll
		// bars while Windows got modern ones. Same theme, same look, wherever it runs.

		private static readonly Color ScrollTrack = Color.FromArgb (240, 240, 240);
		private static readonly Color ScrollThumb = Color.FromArgb (133, 133, 133);
		private static readonly Color ScrollArrow = Color.FromArgb (96, 96, 96);
		private static readonly Color ProgressTrough = Color.FromArgb (230, 230, 230);
		private static readonly Color ProgressFill = Color.FromArgb (6, 176, 37);
		private static readonly Color TabPaneFace = Color.FromArgb (249, 249, 249);
		private static readonly Color TabItemFace = Color.FromArgb (240, 240, 240);
		private static readonly Color HairLine = Color.FromArgb (217, 217, 217);

		private void DrawModernProgressBar (Graphics dc, ProgressBar ctrl)
		{
			Rectangle bounds = ctrl.ClientRectangle;
			if (bounds.Width <= 0 || bounds.Height <= 0)
				return;
			dc.FillRectangle (ResPool.GetSolidBrush (ProgressTrough), bounds);
			dc.DrawRectangle (ResPool.GetPen (HairLine), bounds.X, bounds.Y,
					  bounds.Width - 1, bounds.Height - 1);

			int range = ctrl.Maximum - ctrl.Minimum;
			if (range <= 0)
				return;
			double fraction = (double) (ctrl.Value - ctrl.Minimum) / range;
			if (fraction <= 0)
				return;

			// One continuous fill, not the classic row of blocks.
			Rectangle fill = Rectangle.Inflate (bounds, -1, -1);
			fill.Width = (int) Math.Round (fill.Width * Math.Min (1.0, fraction));
			if (fill.Width > 0 && fill.Height > 0)
				dc.FillRectangle (ResPool.GetSolidBrush (ProgressFill), fill);
		}

		private void DrawModernScrollBar (Graphics dc, ScrollBar bar, Rectangle client,
						  Rectangle first, Rectangle second, Rectangle thumb)
		{
			dc.FillRectangle (ResPool.GetSolidBrush (ScrollTrack), client);
			DrawScrollArrow (dc, first, bar.vert ? ArrowDirection.Up : ArrowDirection.Left, bar.Enabled);
			DrawScrollArrow (dc, second, bar.vert ? ArrowDirection.Down : ArrowDirection.Right, bar.Enabled);

			if (!bar.Enabled || thumb.Width <= 0 || thumb.Height <= 0)
				return;

			// A slim bar centred in the channel, which is what Windows draws now -- not a raised
			// button filling the whole width.
			Rectangle slim = thumb;
			if (bar.vert) {
				int inset = Math.Max (0, (thumb.Width - 6) / 2);
				slim.X += inset;
				slim.Width = Math.Max (2, thumb.Width - inset * 2);
			} else {
				int inset = Math.Max (0, (thumb.Height - 6) / 2);
				slim.Y += inset;
				slim.Height = Math.Max (2, thumb.Height - inset * 2);
			}
			dc.FillRectangle (ResPool.GetSolidBrush (ScrollThumb), slim);
		}

		private void DrawScrollArrow (Graphics dc, Rectangle area, ArrowDirection direction, bool enabled)
		{
			if (area.Width <= 0 || area.Height <= 0)
				return;
			int cx = area.X + area.Width / 2;
			int cy = area.Y + area.Height / 2;
			int r = Math.Max (2, Math.Min (4, Math.Min (area.Width, area.Height) / 4));
			Color ink = enabled ? ScrollArrow : ColorGrayText;

			Point [] arrow;
			switch (direction) {
			case ArrowDirection.Up:
				arrow = new [] { new Point (cx - r, cy + r / 2), new Point (cx + r, cy + r / 2), new Point (cx, cy - r) };
				break;
			case ArrowDirection.Down:
				arrow = new [] { new Point (cx - r, cy - r / 2), new Point (cx + r, cy - r / 2), new Point (cx, cy + r) };
				break;
			case ArrowDirection.Left:
				arrow = new [] { new Point (cx + r / 2, cy - r), new Point (cx + r / 2, cy + r), new Point (cx - r, cy) };
				break;
			default:
				arrow = new [] { new Point (cx - r / 2, cy - r), new Point (cx - r / 2, cy + r), new Point (cx + r, cy) };
				break;
			}

			SmoothingMode old = dc.SmoothingMode;
			dc.SmoothingMode = SmoothingMode.AntiAlias;
			dc.FillPolygon (ResPool.GetSolidBrush (ink), arrow);
			dc.SmoothingMode = old;
		}

		private void DrawModernHeaderCell (Graphics g, Rectangle area, bool pressed)
		{
			if (area.Width <= 0 || area.Height <= 0)
				return;
			// Flat and white, separated by a hairline -- no raised bevel.
			g.FillRectangle (ResPool.GetSolidBrush (pressed ? TabItemFace : ColorWindow), area);
			Pen pen = ResPool.GetPen (HairLine);
			g.DrawLine (pen, area.Right - 1, area.Y + 3, area.Right - 1, area.Bottom - 4);
			g.DrawLine (pen, area.X, area.Bottom - 1, area.Right - 1, area.Bottom - 1);
		}

		private void DrawModernTabControl (Graphics dc, Rectangle area, TabControl tab)
		{
			dc.FillRectangle (ResPool.GetSolidBrush (tab.BackColor), area);
			if (tab.TabCount == 0)
				return;

			Rectangle pane = tab.DisplayRectangle;
			pane.Inflate (2, 2);
			dc.FillRectangle (ResPool.GetSolidBrush (TabPaneFace), pane);
			dc.DrawRectangle (ResPool.GetPen (HairLine), pane.X, pane.Y, pane.Width - 1, pane.Height - 1);

			for (int i = 0; i < tab.TabCount; i++) {
				bool selected = i == tab.SelectedIndex;
				Rectangle bounds = tab.GetTabRect (i);
				if (bounds.Width <= 0 || bounds.Height <= 0)
					continue;
				if (selected)
					bounds.Inflate (2, 0);

				dc.FillRectangle (ResPool.GetSolidBrush (selected ? TabPaneFace : TabItemFace), bounds);
				dc.DrawRectangle (ResPool.GetPen (HairLine), bounds.X, bounds.Y,
						  bounds.Width - 1, bounds.Height - 1);

				TabPage page = tab.TabPages[i];
				var format = new StringFormat {
					Alignment = StringAlignment.Center,
					LineAlignment = StringAlignment.Center,
					HotkeyPrefix = System.Drawing.Text.HotkeyPrefix.Show,
					FormatFlags = StringFormatFlags.NoWrap,
				};
				Color fore = page.Enabled ? tab.ForeColor : ColorGrayText;
				dc.DrawString (page.Text, tab.Font, ResPool.GetSolidBrush (fore), bounds, format);
			}
		}
	}
}
