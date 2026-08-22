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
			path.AddArc (r.Right - d - 1, r.Y, d, d, 270, 90);
			path.AddArc (r.Right - d - 1, r.Bottom - d - 1, d, d, 0, 90);
			path.AddArc (r.X, r.Bottom - d - 1, d, d, 90, 90);
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

			// Windows itself draws this one; see UxTheme. The hand-drawn version below stays as
			// the fallback for the heads that have no uxtheme (and for WF_UXTHEME=0).
			if (UxTheme.Draw (dc, "BUTTON", UxTheme.BP_PUSHBUTTON, PushButtonState (button),
					  button.ClientRectangle))
				return;

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
			// Button.OnPaint calls DrawButton, which calls this. Overriding the other one left
			// every ordinary button on the classic bevel while check boxes and radio buttons
			// (which DO come through ButtonBase_DrawButton) had already gone modern.
			if (button.FlatStyle != FlatStyle.Flat && button.FlatStyle != FlatStyle.Popup &&
			    UxTheme.Draw (g, "BUTTON", UxTheme.BP_PUSHBUTTON, PushButtonState (button),
					  button.ClientRectangle))
				return;
			base.DrawButtonBackground (g, button, clipArea);
		}

		/// <summary>A push button's state in uxtheme's numbering. A default button gets its own
		/// state rather than a border of a different colour.</summary>
		private static int PushButtonState (ButtonBase button)
		{
			if (!button.Enabled) return UxTheme.PBS_DISABLED;
			if (button.Pressed) return UxTheme.PBS_PRESSED;
			if (button.Entered) return UxTheme.PBS_HOT;
			if (button is Button b && b.InternalSelected) return UxTheme.PBS_DEFAULTED;
			if (button.IsDefault || button.Focused) return UxTheme.PBS_DEFAULTED;
			return UxTheme.PBS_NORMAL;
		}

		/// <summary>Check box and radio button states share one numbering: unchecked, checked and
		/// mixed each run normal / hot / pressed / disabled.</summary>
		private static int GlyphState (ButtonBase button, int baseState)
		{
			int offset = !button.Enabled ? 3 : button.Pressed ? 2 : button.Entered ? 1 : 0;
			return baseState + offset;
		}

		public override void DrawCheckBoxGlyph (Graphics g, CheckBox cb, Rectangle glyphArea)
		{
			if (cb.Appearance == Appearance.Button || cb.FlatStyle == FlatStyle.Flat) {
				base.DrawCheckBoxGlyph (g, cb, glyphArea);
				return;
			}

			int baseState = cb.CheckState == CheckState.Checked ? UxTheme.CBS_CHECKEDNORMAL
				      : cb.CheckState == CheckState.Indeterminate ? UxTheme.CBS_MIXEDNORMAL
				      : UxTheme.CBS_UNCHECKEDNORMAL;
			if (UxTheme.Draw (g, "BUTTON", UxTheme.BP_CHECKBOX, GlyphState (cb, baseState),
					  CentredGlyph (glyphArea)))
				return;

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

			if (UxTheme.Draw (g, "BUTTON", UxTheme.BP_RADIOBUTTON,
					  GlyphState (rb, rb.Checked ? UxTheme.CBS_CHECKEDNORMAL : UxTheme.CBS_UNCHECKEDNORMAL),
					  CentredGlyph (glyphArea)))
				return;

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
			// Windows draws a thin chevron, not the filled triangle the classic theme uses -- and
			// it draws it as a part of its own, so ask for that rather than approximating a glyph.
			int comboState = (state & ButtonState.Inactive) != 0 ? UxTheme.CBB_DISABLED
				       : (state & (ButtonState.Pushed | ButtonState.Checked)) != 0 ? UxTheme.CBB_PRESSED
				       : UxTheme.CBB_NORMAL;
			if (UxTheme.Draw (graphics, "COMBOBOX", UxTheme.CP_DROPDOWNBUTTONRIGHT, comboState, rectangle))
				return;
			if (UxTheme.Draw (graphics, "COMBOBOX", UxTheme.CP_DROPDOWNBUTTON, comboState, rectangle))
				return;

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

		// ---- borders, scroll bars and tabs ------------------------------------------
		//
		// All three were reconstructions until now, and all three were wrong in ways no amount of
		// eyeballing was going to fix: an input had no border at all, the scroll bars were the
		// hatched 1995 ones, and the tab headers were sunken boxes. Windows draws them.

		public override void DrawControlBorder (Graphics dc, Rectangle bounds, Control control, bool sunken)
		{
			if (sunken) {
				int state = control == null || control.Enabled
					  ? (control != null && control.Focused ? UxTheme.EPSN_FOCUSED
					     : control != null && control.Entered ? UxTheme.EPSN_HOT : UxTheme.EPSN_NORMAL)
					  : UxTheme.EPSN_DISABLED;
				if (UxTheme.Draw (dc, "EDIT", UxTheme.EP_EDITBORDER_NOSCROLL, state, bounds, true))
					return;
			}

			// No uxtheme: one hairline, not the classic carved bevel -- this theme's whole point.
			dc.DrawRectangle (ResPool.GetPen (sunken ? InputBorder : RaisedBorder),
					  bounds.X, bounds.Y, bounds.Width - 1, bounds.Height - 1);
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

			int trackPart = bar.vert ? UxTheme.SBP_UPPERTRACKVERT : UxTheme.SBP_UPPERTRACKHORZ;
			int thumbPart = bar.vert ? UxTheme.SBP_THUMBBTNVERT : UxTheme.SBP_THUMBBTNHORZ;
			int gripPart = bar.vert ? UxTheme.SBP_GRIPPERVERT : UxTheme.SBP_GRIPPERHORZ;
			int trackState = bar.Enabled ? UxTheme.SCRBS_NORMAL : UxTheme.SCRBS_DISABLED;

			if (!UxTheme.Draw (dc, "SCROLLBAR", trackPart, trackState, client)) {
				base.DrawScrollBar (dc, clip, bar);
				return;
			}

			int firstArrow = bar.vert ? UxTheme.ABS_UPNORMAL : UxTheme.ABS_LEFTNORMAL;
			int secondArrow = bar.vert ? UxTheme.ABS_DOWNNORMAL : UxTheme.ABS_RIGHTNORMAL;
			UxTheme.Draw (dc, "SCROLLBAR", UxTheme.SBP_ARROWBTN, firstArrow + ArrowOffset (bar.firstbutton_state, bar.Enabled), first);
			UxTheme.Draw (dc, "SCROLLBAR", UxTheme.SBP_ARROWBTN, secondArrow + ArrowOffset (bar.secondbutton_state, bar.Enabled), second);

			if (bar.Enabled && thumb.Width > 0 && thumb.Height > 0) {
				int thumbState = bar.thumb_moving == ScrollBar.ThumbMoving.Forward
					      || bar.thumb_moving == ScrollBar.ThumbMoving.Backwards
					       ? UxTheme.SCRBS_PRESSED : UxTheme.SCRBS_NORMAL;
				UxTheme.Draw (dc, "SCROLLBAR", thumbPart, thumbState, thumb);
				UxTheme.Draw (dc, "SCROLLBAR", gripPart, thumbState, thumb);
			}
		}

		/// <summary>Each arrow direction has its own run of four states, in the usual normal / hot
		/// / pressed / disabled order.</summary>
		private static int ArrowOffset (ButtonState state, bool enabled)
		{
			if (!enabled) return 3;
			return (state & ButtonState.Pushed) != 0 ? 2 : 0;
		}

		public override void DrawTabControl (Graphics dc, Rectangle area, TabControl tab)
		{
			if (!UxTheme.Available || tab.Alignment != TabAlignment.Top || tab.Appearance != TabAppearance.Normal) {
				base.DrawTabControl (dc, area, tab);
				return;
			}

			dc.FillRectangle (ResPool.GetSolidBrush (tab.BackColor), area);
			if (tab.TabCount == 0) {
				base.DrawTabControl (dc, area, tab);
				return;
			}

			// The pane runs under every tab but the selected one, which sits on top of it.
			Rectangle pane = tab.DisplayRectangle;
			pane.Inflate (2, 2);
			if (!UxTheme.Draw (dc, "TAB", UxTheme.TABP_PANE, 1, pane)) {
				base.DrawTabControl (dc, area, tab);
				return;
			}

			for (int i = 0; i < tab.TabCount; i++) {
				if (i == tab.SelectedIndex) continue;
				DrawTabItem (dc, tab, i, UxTheme.TIS_NORMAL);
			}
			if (tab.SelectedIndex >= 0 && tab.SelectedIndex < tab.TabCount)
				DrawTabItem (dc, tab, tab.SelectedIndex, UxTheme.TIS_SELECTED);
		}

		private void DrawTabItem (Graphics dc, TabControl tab, int index, int state)
		{
			Rectangle bounds = tab.GetTabRect (index);
			if (bounds.Width <= 0 || bounds.Height <= 0) return;
			// The selected tab is drawn a little larger, overlapping the pane edge, which is how
			// Windows lifts it out of the row.
			if (state == UxTheme.TIS_SELECTED)
				bounds.Inflate (2, 0);

			int part = tab.TabCount == 1 ? UxTheme.TABP_TABITEMBOTHEDGE
				 : index == 0 ? UxTheme.TABP_TABITEMLEFTEDGE
				 : index == tab.TabCount - 1 ? UxTheme.TABP_TABITEMRIGHTEDGE
				 : UxTheme.TABP_TABITEM;
			if (!UxTheme.Draw (dc, "TAB", part, state, bounds))
				UxTheme.Draw (dc, "TAB", UxTheme.TABP_TABITEM, state, bounds);

			TabPage page = tab.TabPages[index];
			var format = new StringFormat {
				Alignment = StringAlignment.Center,
				LineAlignment = StringAlignment.Center,
				HotkeyPrefix = System.Drawing.Text.HotkeyPrefix.Show,
				FormatFlags = StringFormatFlags.NoWrap,
			};
			Color fore = page.Enabled ? tab.ForeColor : ColorGrayText;
			dc.DrawString (page.Text, tab.Font, ResPool.GetSolidBrush (fore), bounds, format);
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
			return UxTheme.Available && cell.DataGridView != null;
		}

		public override bool DataGridViewRowHeaderCellDrawBackground (DataGridViewRowHeaderCell cell,
									      Graphics g, Rectangle bounds)
		{
			return DrawHeaderCell (cell, g, bounds);
		}

		public override bool DataGridViewRowHeaderCellDrawBorder (DataGridViewRowHeaderCell cell,
									  Graphics g, Rectangle bounds)
		{
			return UxTheme.Available && cell.DataGridView != null;
		}

		private static bool DrawHeaderCell (DataGridViewHeaderCell cell, Graphics g, Rectangle bounds)
		{
			if (cell == null || cell.DataGridView == null)
				return false;
			return UxTheme.Draw (g, "HEADER", UxTheme.HP_HEADERITEM, UxTheme.HIS_NORMAL, bounds);
		}

		// ---- scroll bar metrics ------------------------------------------------------
		//
		// Windows sizes a scroll bar at 17 pixels; the classic theme says 16, which for its own
		// drawing was fine. It is not fine for the Windows parts: the thumb is a nine-grid with
		// eight pixels of non-stretching margin on each side, so a 16-pixel bar has nothing left in
		// the middle to stretch and the thumb comes out as a one-pixel hairline instead of the two
		// -pixel bar next door. Ask Windows for its own numbers.

		private static readonly int ScrollBarWidth = UxTheme.SystemMetric (UxTheme.SM_CXVSCROLL, 17);
		private static readonly int ScrollBarHeight = UxTheme.SystemMetric (UxTheme.SM_CYHSCROLL, 17);

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
			if (UxTheme.Draw (dc, "SCROLLBAR", UxTheme.SBP_UPPERTRACKVERT, UxTheme.SCRBS_NORMAL, area))
				return;
			base.DrawScrollBarCorner (dc, area);
		}

		// ---- list view headers -------------------------------------------------------
		//
		// The same HEADER class the data grid uses. A ListView in Details view is what SharpDevelop's
		// About box shows its assembly list in, so this is the one that was actually on screen.

		protected override void ListViewDrawColumnHeaderBackground (ListView listView, ColumnHeader columnHeader,
									    Graphics g, Rectangle area, Rectangle clippingArea)
		{
			int state = listView.HeaderStyle == ColumnHeaderStyle.Clickable && columnHeader.Pressed
				  ? UxTheme.HIS_PRESSED : UxTheme.HIS_NORMAL;
			if (UxTheme.Draw (g, "HEADER", UxTheme.HP_HEADERITEM, state, area))
				return;
			base.ListViewDrawColumnHeaderBackground (listView, columnHeader, g, area, clippingArea);
		}

		// ---- progress bar, combo arrow, check box primitive --------------------------

		public override void DrawProgressBar (Graphics dc, Rectangle clip_rect, ProgressBar ctrl)
		{
			// The classic bar is a row of separate blocks with gaps. Windows has drawn one
			// continuous fill inside a rounded trough for a long time.
			Rectangle bounds = ctrl.ClientRectangle;
			if (!UxTheme.Draw (dc, "PROGRESS", UxTheme.PP_BAR, 1, bounds)) {
				base.DrawProgressBar (dc, clip_rect, ctrl);
				return;
			}

			int range = ctrl.Maximum - ctrl.Minimum;
			if (range <= 0)
				return;
			double fraction = (double) (ctrl.Value - ctrl.Minimum) / range;
			if (fraction <= 0)
				return;

			Rectangle fill = ctrl.client_area;
			fill.Width = (int) Math.Round (fill.Width * Math.Min (1.0, fraction));
			if (fill.Width <= 0 || fill.Height <= 0)
				return;
			UxTheme.Draw (dc, "PROGRESS", UxTheme.PP_FILL, UxTheme.PBFS_NORMAL, fill);
		}

		/// <summary>The check box primitive, which is what a CheckedListBox and a few other
		/// controls draw their boxes with -- they never reach DrawCheckBoxGlyph.</summary>
		public override void CPDrawCheckBox (Graphics dc, Rectangle rectangle, ButtonState state)
		{
			int baseState = (state & ButtonState.Checked) != 0 ? UxTheme.CBS_CHECKEDNORMAL
				      : UxTheme.CBS_UNCHECKEDNORMAL;
			int offset = (state & ButtonState.Inactive) != 0 ? 3
				   : (state & ButtonState.Pushed) != 0 ? 2 : 0;
			if (UxTheme.Draw (dc, "BUTTON", UxTheme.BP_CHECKBOX, baseState + offset, CentredGlyph (rectangle)))
				return;
			base.CPDrawCheckBox (dc, rectangle, state);
		}

		// ---- month calendar ----------------------------------------------------------
		//
		// The classic calendar is a blue caption bar with raised arrow buttons, day names in that
		// same blue, and a hand-drawn red ring around today. Windows draws a plain header with the
		// month in bold, chevrons with no button around them, grey day names, and a light box on
		// today's cell.

		protected override Color MonthCalendarTitleBackColor (MonthCalendar mc) => mc.BackColor;

		protected override Font MonthCalendarTodayFont (MonthCalendar mc) => mc.Font;

		// Windows marks the selected day with a pale accent fill and leaves the number dark; the
		// classic theme fills it with the caption colour and reverses the text out of it.
		protected override Color MonthCalendarSelectionBackColor (MonthCalendar mc)
			=> Color.FromArgb (204, 232, 255);

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
			int reach = Math.Max (2, Math.Min (4, button.Height / 4));
			SmoothingMode old = dc.SmoothingMode;
			dc.SmoothingMode = SmoothingMode.AntiAlias;
			using (var pen = new Pen (mc.Enabled ? ColorControlText : ColorGrayText, 1.4f)) {
				int dx = is_previous ? reach : -reach;
				dc.DrawLines (pen, new Point [] {
					new Point (cx + dx / 2, cy - reach),
					new Point (cx - dx / 2, cy),
					new Point (cx + dx / 2, cy + reach),
				});
			}
			dc.SmoothingMode = old;
		}

		protected override void DrawTodayCircle (Graphics dc, Rectangle rectangle)
		{
			if (rectangle.Width <= 2 || rectangle.Height <= 2)
				return;
			// Windows outlines today's cell instead of ringing the number in red.
			var box = new Rectangle (rectangle.X, rectangle.Y + 1,
						 Math.Max (rectangle.Width - 1, 0), Math.Max (rectangle.Height - 2, 0));
			SmoothingMode old = dc.SmoothingMode;
			dc.SmoothingMode = SmoothingMode.AntiAlias;
			using (GraphicsPath path = RoundedRect (box, 2))
				dc.DrawPath (ResPool.GetPen (ButtonBorderHover), path);
			dc.SmoothingMode = old;
		}
	}
}
