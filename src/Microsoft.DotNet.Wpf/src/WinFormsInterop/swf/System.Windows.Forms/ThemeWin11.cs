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

		private static Color Surface => SystemColors.Control;

		// Windows does still shade a tool strip, gently: measured down a stock one it runs from
		// #FCFCFC at the top to about #F0F0F0 at the foot, with a #F2F2F2 rule along the bottom.
		// Reporting one colour at both ends was what left ours a flat grey band. The renderer takes
		// only the two ends, so the middle just names the average.
		public override Color ToolStripGradientBegin => Color.FromArgb (252, 252, 252);
		public override Color ToolStripGradientMiddle => Color.FromArgb (246, 246, 246);
		public override Color ToolStripGradientEnd => Color.FromArgb (240, 240, 240);
		public override Color ToolStripBorder => Color.FromArgb (242, 242, 242);
		public override Color ToolStripPanelGradientBegin => Surface;
		public override Color ToolStripPanelGradientEnd => Surface;
		public override Color ToolStripContentPanelGradientBegin => Surface;
		public override Color ToolStripContentPanelGradientEnd => Surface;
		// A menu bar is not flat: Windows runs it from #F0F0F0 on the left to nearly white on the
		// right, measured across a stock one. Reporting the same colour at both ends made it a
		// plain grey band.
		public override Color MenuStripGradientBegin => Color.FromArgb (240, 240, 240);
		public override Color MenuStripGradientEnd => Color.FromArgb (251, 251, 251);
		public override Color StatusStripGradientBegin => Surface;
		public override Color StatusStripGradientEnd => Surface;
		// A menu is white behind its entries -- #FDFDFD, measured off a stock one -- with the
		// shading only in the strip down the left where an icon would go. Ours took the grey the
		// base table names, and the whole menu came out a shade of grey.
		public override Color ToolStripDropDownBackground => Color.FromArgb (253, 253, 253);
		public override Color ImageMarginGradientBegin => Surface;
		public override Color ImageMarginGradientMiddle => Surface;
		public override Color ImageMarginGradientEnd => Surface;
	}

	/// <summary>The same colours, without the shading along a tool strip's background. A button row
	/// embedded in another control -- the property grid's -- is flat in Windows.</summary>
	/// <summary>The colours Windows draws an old-style menu in. It draws those itself rather
	/// than leaving them to the application, and it draws them lighter than the ones it gives a
	/// strip menu: a #E5E5E5 hairline round a #F9F9F9 body, where a strip menu gets a #808080
	/// line round #FDFDFD. Measured off both, side by side.</summary>
	internal class SystemMenuColorTable : ModernProfessionalColorTable
	{
		public override Color MenuBorder => Color.FromArgb (229, 229, 229);
		public override Color ToolStripDropDownBackground => Color.FromArgb (249, 249, 249);
	}

	internal class FlatSurfaceColorTable : ModernProfessionalColorTable
	{
		public override Color ToolStripGradientBegin => SystemColors.Control;
		public override Color ToolStripGradientMiddle => SystemColors.Control;
		public override Color ToolStripGradientEnd => SystemColors.Control;
		public override Color ToolStripBorder => SystemColors.Control;
	}

	internal class ThemeWin11 : ThemeWin32Classic
	{
		private readonly ProfessionalColorTable color_table = new ModernProfessionalColorTable ();

		public override ProfessionalColorTable ColorTable => color_table;

		/// <summary>The professional renderer with this theme's colours, but a flat background. The
		/// system renderer a "flat" tool bar would otherwise get draws the pre-visual-styles look,
		/// which is what put carved 3D frames on the property grid's toggled buttons instead of a blue
		/// fill; the shading a free-standing tool strip carries does not belong on a button row inside
		/// another control, and Windows draws the property grid's flat.</summary>
		public override ToolStripRenderer CreateToolBarRenderer (ToolBarAppearance appearance)
		{
			return new ToolStripProfessionalRenderer (flat_color_table);
		}

		private readonly ProfessionalColorTable flat_color_table = new FlatSurfaceColorTable ();

		/// <summary>Measured against a stock "Go to today": Windows makes its own menu 79 pixels
		/// wide where the same caption in a strip menu comes to 64. Asking for the whole fifteen
		/// does not get it -- the drop-down's layout takes about ten of them and stops, for a
		/// reason not yet found -- so this asks for more than it needs and lands a few pixels
		/// short rather than a dozen.</summary>
		public override int SystemMenuExtraWidth {
			get { return 30; }
		}

		/// <summary>The renderer for a menu of the old kind, which Windows draws in its own
		/// lighter colours rather than the ones it gives a strip menu.</summary>
		public override ToolStripRenderer CreateSystemMenuRenderer ()
		{
			return new ToolStripProfessionalRenderer (system_menu_color_table);
		}

		private readonly ProfessionalColorTable system_menu_color_table = new SystemMenuColorTable ();

		// Windows draws a single hairline around an input, not a carved bevel. These are the two
		// greys it uses: #7A7A7A around something you type in, #ADADAD around something you press.
		// Measured off Windows: a text box or a list is outlined #838383, a combo box the lighter
		// #BCBCBC. One colour for all of them was wrong in both directions at once.
		private static readonly Color ComboBorder = Color.FromArgb (188, 188, 188);
		private static readonly Color InputBorder = Color.FromArgb (131, 131, 131);
		private static readonly Color RaisedBorder = Color.FromArgb (173, 173, 173);
		private static readonly Color EtchedBorder = Color.FromArgb (223, 223, 223);

		// Measured off a stock combo box with its list down: the field carries a pale #CCE4F7 with
		// the accent round it, while the list below highlights its selected row in the full #0078D7.
		// Ours used the selection colour for both, so the closed field came out as a solid blue
		// slab. The list's own frame is #646464, not the light grey a plain bordered control gets.
		private static readonly Color ComboFieldOpenFace = Color.FromArgb (204, 228, 247);
		private static readonly Color ComboListSelection = Color.FromArgb (0, 120, 215);
		private static readonly Color PopupBorder = Color.FromArgb (100, 100, 100);

		public override void DrawComboBoxItem (ComboBox ctrl, DrawItemEventArgs e)
		{
			// A list without an editable part draws its own field, so this is where the text in the box
			// gets its background. It follows the control: washed while the pointer is over it or the
			// list is down, plain otherwise -- and never the list's selection colour, because the text in
			// the box is not "a selected item", it is what the control says.
			bool inField = (e.State & DrawItemState.ComboBoxEdit) == DrawItemState.ComboBoxEdit;
			bool selected = (e.State & DrawItemState.Selected) == DrawItemState.Selected;
			if (!inField && !selected) {
				base.DrawComboBoxItem (ctrl, e);
				return;
			}

			Color back = inField ? ComboBoxFieldBackColor (ctrl) : ComboListSelection;
			Color fore = inField ? ColorControlText : ColorHighlightText;
			if (!ctrl.Enabled)
				fore = ColorInactiveCaptionText;

			e.Graphics.FillRectangle (ResPool.GetSolidBrush (back), e.Bounds);
			if (e.Index != -1) {
				var format = new StringFormat {
					FormatFlags = StringFormatFlags.LineLimit | StringFormatFlags.NoWrap,
					LineAlignment = StringAlignment.Center,
				};
				e.Graphics.DrawString (ctrl.GetItemText (ctrl.Items[e.Index]), e.Font,
						       ResPool.GetSolidBrush (fore), e.Bounds, format);
				format.Dispose ();
			}
		}

		/// <summary>The whole control lights up when the pointer is over it or its list is down, not
		/// merely the button at the end: Windows washes the field, the text and the chevron together.
		/// </summary>
		public override bool CombBoxBackgroundHasHotElementStyle (ComboBox comboBox)
		{
			return comboBox.Enabled;
		}

		public override Color ComboBoxFieldBackColor (ComboBox comboBox)
		{
			if (!comboBox.Enabled)
				return ColorControl;
			return comboBox.DroppedDown || comboBox.PointerOver
				? ComboFieldOpenFace : comboBox.BackColor;
		}

		public override void ComboBoxDrawBackground (ComboBox comboBox, Graphics g, Rectangle clippingArea, FlatStyle style)
		{
			if (!comboBox.Enabled)
				g.FillRectangle (ResPool.GetSolidBrush (ColorControl), comboBox.ClientRectangle);
			else if (comboBox.DroppedDown || comboBox.PointerOver)
				g.FillRectangle (ResPool.GetSolidBrush (ComboFieldOpenFace), comboBox.ClientRectangle);

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
				// The light hairline Windows puts round an input, with its corner pixel dropped --
				// not the dark #7A7A7A of the classic sunken field.
				Rectangle border = comboBox.TextArea;
				border.Width -= 1;
				border.Height -= 1;
				DrawRoundedOutline (g, border, comboBox.Focused ? ButtonBorderHover : ComboBorder);
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

		// Read off Windows' own rendering rather than guessed at. A button is very nearly white --
		// #E1E1E1 was the XP-era face and made every button look pressed beside the real thing --
		// and the accent is #005FB8, not the #0078D7 of a decade ago.
		private static readonly Color ButtonFaceNormal = Color.FromArgb (253, 253, 253);
		private static readonly Color ButtonBorderNormal = Color.FromArgb (209, 209, 209);
		private static readonly Color ButtonFaceHover = Color.FromArgb (224, 238, 249);
		private static readonly Color ButtonBorderHover = Color.FromArgb (0, 95, 184);
		private static readonly Color ButtonFacePressed = Color.FromArgb (204, 228, 247);
		private static readonly Color ButtonBorderPressed = Color.FromArgb (0, 76, 148);
		private static readonly Color ButtonFaceDisabled = Color.FromArgb (249, 249, 249);
		private static readonly Color ButtonBorderDisabled = Color.FromArgb (205, 205, 205);
		// An unchecked box is not white: Windows fills it #F3F3F3 and outlines it #626262, both
		// measured off its own rendering. White with a pale border read as the greyer of the two
		// even though it was the lighter one.
		private static readonly Color GlyphBorder = Color.FromArgb (98, 98, 98);
		private static readonly Color GlyphFace = Color.FromArgb (243, 243, 243);

		private const int ButtonCornerRadius = 3;

		/// <summary>Paint a rounded rectangle without a GraphicsPath. The recorder flattens a path
		/// into unjoined line segments, which at these radii turns the corners inside out -- they
		/// came out cut on one side and bulging on the other. Deciding per pixel whether it is inside
		/// the corner's quarter circle is exact, needs no antialiasing, and looks the same every
		/// time.</summary>
		/// <param name="surround">What is behind the control, painted back over the corners the
		/// rounding cuts away.</param>
		private void PaintRoundedRect (Graphics g, Rectangle r, int radius, Color face, Color border,
					       Color surround)
		{
			if (r.Width <= 1 || r.Height <= 1)
				return;
			if (radius < 1 || r.Width <= radius * 2 || r.Height <= radius * 2) {
				g.FillRectangle (ResPool.GetSolidBrush (face), r);
				g.DrawRectangle (ResPool.GetPen (border), r);
				return;
			}

			SmoothingMode old = g.SmoothingMode;
			g.SmoothingMode = SmoothingMode.None;
			g.FillRectangle (ResPool.GetSolidBrush (face), r);

			// The straight runs, each stopping where its corner begins.
			Pen pen = ResPool.GetPen (border);
			g.DrawLine (pen, r.X + radius, r.Y, r.Right - radius, r.Y);
			g.DrawLine (pen, r.X + radius, r.Bottom, r.Right - radius, r.Bottom);
			g.DrawLine (pen, r.X, r.Y + radius, r.X, r.Bottom - radius);
			g.DrawLine (pen, r.Right, r.Y + radius, r.Right, r.Bottom - radius);

			// The four corners, a pixel at a time: outside the quarter circle goes back to the
			// surround, on it takes the border colour, inside keeps the face.
			Brush outside = ResPool.GetSolidBrush (surround);
			Brush edge = ResPool.GetSolidBrush (border);
			float limit = radius - 0.5f;
			for (int dy = 0; dy < radius; dy++) {
				for (int dx = 0; dx < radius; dx++) {
					float ox = limit - dx, oy = limit - dy;
					double dist = Math.Sqrt (ox * ox + oy * oy);
					Brush use = dist > limit + 0.5 ? outside
						  : dist > limit - 0.5 ? edge : null;
					if (use == null)
						continue;
					g.FillRectangle (use, r.X + dx, r.Y + dy, 1, 1);
					g.FillRectangle (use, r.Right - dx, r.Y + dy, 1, 1);
					g.FillRectangle (use, r.X + dx, r.Bottom - dy, 1, 1);
					g.FillRectangle (use, r.Right - dx, r.Bottom - dy, 1, 1);
				}
			}
			g.SmoothingMode = old;
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

			// Windows insets the button's frame by a pixel all round rather than painting it hard
			// against the control's bounds -- a 26-pixel button draws a 24-pixel face.
			Rectangle r = Rectangle.Inflate (button.ClientRectangle, -1, -1);
			r.Width -= 1;
			r.Height -= 1;
			if (r.Width <= 0 || r.Height <= 0)
				return;

			Color behind = button.Parent != null ? button.Parent.BackColor : ColorControl;
			PaintRoundedRect (dc, r, ButtonCornerRadius, face, border, behind);
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
		/// <summary>Round a small glyph's corners the way Windows does: put the corner pixel back
		/// to whatever is behind the glyph. Drawing a curve at this size does not survive the
		/// recorder -- a flattened path bulges inwards and an arc antialiases outside the shape --
		/// and at a one-pixel radius the corner pixel IS the rounding.</summary>
		/// <summary>A one-pixel frame whose corners are rounded by exactly one pixel -- the corner
		/// pixel is simply not painted. Every input on this theme is outlined with it.</summary>
		private void DrawRoundedOutline (Graphics g, Rectangle r, Color colour)
		{
			if (r.Width <= 1 || r.Height <= 1)
				return;
			Pen pen = ResPool.GetPen (colour);
			SmoothingMode old = g.SmoothingMode;
			g.SmoothingMode = SmoothingMode.None;
			g.DrawLine (pen, r.X + 1, r.Y, r.Right - 1, r.Y);
			g.DrawLine (pen, r.X + 1, r.Bottom, r.Right - 1, r.Bottom);
			g.DrawLine (pen, r.X, r.Y + 1, r.X, r.Bottom - 1);
			g.DrawLine (pen, r.Right, r.Y + 1, r.Right, r.Bottom - 1);
			g.SmoothingMode = old;
		}

		/// <summary>Round a filled glyph's corners with partial coverage, so the curve reads as a
		/// curve. Clearing whole corner pixels was the previous attempt and it showed: four hard
		/// light squares where Windows has a smooth arc. A pixel's colour is the fill and the
		/// surround mixed by how much of it falls inside the corner's quarter circle -- which is what
		/// antialiasing is, done where we can control it rather than left to the renderer.</summary>
		/// <summary>Rounds the corners of a box that has already been filled and outlined.
		/// <para>Only two kinds of pixel are touched: one that falls outside the rounded rectangle,
		/// which becomes the surround, and one the arc passes through, which becomes the border
		/// colour blended by how much of it the arc covers. Pixels inside are left exactly as the
		/// fill left them.</para>
		/// <para>Painting a blend of the border and the surround across the whole corner instead --
		/// which is what this did -- overwrites the fill near the corners with a pale wash. On an
		/// unchecked box the fill is nearly white and it passed unnoticed; on a checked one the fill
		/// is the accent colour, and the corners came out with the colour missing.</para></summary>
		private void RoundGlyphCorners (Graphics g, Rectangle box, Color border, Color surround, int radius)
		{
			if (radius < 1 || box.Width < radius * 2 || box.Height < radius * 2)
				return;
			SmoothingMode old = g.SmoothingMode;
			g.SmoothingMode = SmoothingMode.None;
			for (int dy = 0; dy < radius; dy++) {
				for (int dx = 0; dx < radius; dx++) {
					// Distance from the arc's centre to this pixel's centre.
					double ox = radius - (dx + 0.5), oy = radius - (dy + 0.5);
					double dist = Math.Sqrt (ox * ox + oy * oy);
					Color paint;
					if (radius - dist >= 0.5)
						continue;                               // well inside: the fill already has it
					if (dist > radius) {
						paint = surround;                       // its centre falls outside the arc
					} else {
						double t = 0.5 + (radius - dist);       // how much of the arc covers it
						paint = Color.FromArgb (
							(int) Math.Round (surround.R + (border.R - surround.R) * t),
							(int) Math.Round (surround.G + (border.G - surround.G) * t),
							(int) Math.Round (surround.B + (border.B - surround.B) * t));
					}
					Brush brush = ResPool.GetSolidBrush (paint);
					g.FillRectangle (brush, box.X + dx, box.Y + dy, 1, 1);
					g.FillRectangle (brush, box.Right - dx, box.Y + dy, 1, 1);
					g.FillRectangle (brush, box.X + dx, box.Bottom - dy, 1, 1);
					g.FillRectangle (brush, box.Right - dx, box.Bottom - dy, 1, 1);
				}
			}
			g.SmoothingMode = old;
		}

		private void DrawModernCheck (Graphics g, Rectangle box, bool ticked, bool mixed,
					      bool enabled, bool hot, Color surround)
		{
			Color fill, border;
			if (!enabled) {
				fill = ticked || mixed ? ButtonFaceDisabled : ColorWindow; border = ButtonBorderDisabled;
			} else if (ticked || mixed) {
				fill = border = hot ? ButtonBorderPressed : ButtonBorderHover;      // accent
			} else {
				fill = GlyphFace; border = hot ? ButtonBorderHover : GlyphBorder;
			}

			// Explicitly unsmoothed: a one-pixel outline drawn with antialiasing on spreads half its
			// ink onto the pixel outside the box, which showed up as a pale halo around the corners.
			SmoothingMode boxMode = g.SmoothingMode;
			g.SmoothingMode = SmoothingMode.None;
			g.FillRectangle (ResPool.GetSolidBrush (fill), box);
			g.DrawRectangle (ResPool.GetPen (border), box);
			// The BORDER colour, not the fill: a corner pixel sits on the outline, and blending the
			// fill there erased the outline where it curved -- on a checked box the two are the same
			// colour so it went unnoticed, on an unchecked one the box came out with open corners.
			// Three, measured off a stock box: its corner ramps over three pixels, and at two the
			// curve is too tight to read as one.
			RoundGlyphCorners (g, box, border, surround, 3);
			g.SmoothingMode = boxMode;

			if (mixed) {
				var inner = Rectangle.Inflate (box, -3, -3);
				g.FillRectangle (ResPool.GetSolidBrush (enabled ? ColorWindow : ColorControlDark), inner);
				return;
			}
			if (!ticked)
				return;

			// The tick, on the box's own scale so it holds at any size. These proportions and the
			// stroke width are measured off Windows' own glyph: the vertex sits low and left of
			// centre, the short arm is about half the long one, and the stroke is barely wider than
			// a pixel. Ours was a full pixel heavier and reached further into the corners, which read
			// as a different mark rather than the same one drawn a little off.
			SmoothingMode old = g.SmoothingMode;
			g.SmoothingMode = SmoothingMode.AntiAlias;
			using (var pen = new Pen (enabled ? ColorWindow : ColorControlDark, 1.1f)) {
				float x = box.X, y = box.Y, w = box.Width, h = box.Height;
				g.DrawLines (pen, new PointF [] {
					new PointF (x + w * 0.22f, y + h * 0.47f),
					new PointF (x + w * 0.40f, y + h * 0.66f),
					new PointF (x + w * 0.76f, y + h * 0.28f),
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

			Rectangle box = CentredGlyph (glyphArea);
			box.Width = Math.Max (box.Width - 1, 0);
			box.Height = Math.Max (box.Height - 1, 0);
			if (box.Width <= 0 || box.Height <= 0)
				return;
			// The same drawing the primitive uses, so a CheckedListBox and a CheckBox cannot drift.
			DrawModernCheck (g, box, cb.CheckState == CheckState.Checked,
					 cb.CheckState == CheckState.Indeterminate, cb.Enabled, cb.Entered,
					 cb.Parent != null ? cb.Parent.BackColor : ColorControl);
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
						    size, size);

			Color border = !rb.Enabled ? ButtonBorderDisabled
				     : rb.Checked ? ButtonBorderHover
				     : rb.Entered ? ButtonBorderHover : GlyphBorder;

			SmoothingMode old = g.SmoothingMode;
			g.SmoothingMode = SmoothingMode.AntiAlias;
			// A checked radio button is an accent-coloured disc with a light dot punched out of it,
			// not a light disc with an accent dot on it -- which is what this drew, and read as the
			// colours being swapped.
			Color face = !rb.Enabled ? ColorControl
				   : rb.Checked ? border : ColorWindow;
			g.FillEllipse (ResPool.GetSolidBrush (face), circle);
			g.DrawEllipse (ResPool.GetPen (border), circle);

			if (rb.Checked) {
				// Five pixels across in a thirteen pixel disc, measured off a stock radio button.
				// A third of the disc gave four, which at this size reads as a square rather than a
				// dot -- there is no room left for the corners to be rounded away.
				int span = circle.Width;
				int dotSize = Math.Max (3, span * 5 / 13);
				var dot = new Rectangle (circle.X + (span - dotSize) / 2, circle.Y + (span - dotSize) / 2,
						     dotSize, dotSize);
				g.FillEllipse (ResPool.GetSolidBrush (rb.Enabled ? ColorWindow : ColorControl), dot);
			}
			g.SmoothingMode = old;
		}

		/// <summary>The drop-down button belongs to the field, not to a button of its own: fill it
		/// with the combo's background so the whole control reads as one box with a glyph in it.
		/// </summary>
		public override Color ComboBoxDropDownButtonBackColor (ComboBox comboBox)
		{
			if (!comboBox.Enabled)
				return ColorControl;
			// With the list down the whole field takes the pale accent, chevron and all. Leaving the
			// button on the control's own background left a white notch at the end of a blue field.
			return comboBox.DroppedDown || comboBox.PointerOver
				? ComboFieldOpenFace : comboBox.BackColor;
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
			DrawChevron (graphics, rectangle, color, false);
		}

		private void DrawChevron (Graphics graphics, Rectangle rectangle, Color color, bool up)
		{
			// A thin chevron. Windows stopped drawing the filled triangle a long time ago, and it
			// is the single most recognisable thing about a modern combo box.
			int cx = rectangle.X + rectangle.Width / 2;
			int cy = rectangle.Y + rectangle.Height / 2;
			int reach = Math.Max (2, Math.Min (4, rectangle.Height / 5));
			SmoothingMode old = graphics.SmoothingMode;
			graphics.SmoothingMode = SmoothingMode.AntiAlias;
			using (var pen = new Pen (color, 1.3f))
				graphics.DrawLines (pen, new Point [] {
					new Point (cx - reach, up ? cy + reach / 2 : cy - reach / 2),
					new Point (cx, up ? cy - reach + reach / 2 : cy + reach - reach / 2),
					new Point (cx + reach, up ? cy + reach / 2 : cy - reach / 2),
				});
			graphics.SmoothingMode = old;
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

		// Measured off a stock text box: three sides, the underline at rest, and the frame a list
		// or grid gets instead.
		private static readonly Color InputFrameLight = Color.FromArgb (236, 236, 236);
		private static readonly Color InputUnderline = Color.FromArgb (131, 131, 131);
		private static readonly Color ListFrame = Color.FromArgb (130, 135, 144);
		// A spin box keeps one even frame all round rather than the input's light sides and dark
		// underline; measured off a stock NumericUpDown.
		private static readonly Color SpinnerFrame = Color.FromArgb (171, 173, 179);

		private static bool IsSpinner (Control control)
		{
			for (Type t = control?.GetType (); t != null; t = t.BaseType)
				if (t.Name == "UpDownBase") return true;
			return false;
		}

		/// <summary>The dotted one-pixel outline that says where the focus is. The classic theme
		/// draws it with a HatchBrush, which this stack's recorder has no notion of, so nothing
		/// appeared at all -- no button, check box or radio button ever showed its focus. Every
		/// other pixel, painted one at a time, needs nothing but a solid fill.</summary>
		public override void CPDrawFocusRectangle (Graphics dc, Rectangle rectangle, Color foreColor, Color backColor)
		{
			if (rectangle.Width <= 1 || rectangle.Height <= 1)
				return;
			// An empty background is the common case -- ControlPaint.DrawFocusRectangle (g, rect)
			// passes no colours at all -- and Color.Empty reports a brightness of zero, which chose
			// white dots on a white control: the rectangle was drawn but invisible, except where it
			// happened to cross dark text.
			bool light = backColor.IsEmpty || backColor.GetBrightness () >= 0.5;
			Color dot = light ? Color.Black : Color.White;
			Brush brush = ResPool.GetSolidBrush (dot);
			int right = rectangle.Right - 1, bottom = rectangle.Bottom - 1;
			SmoothingMode old = dc.SmoothingMode;
			dc.SmoothingMode = SmoothingMode.None;
			for (int x = rectangle.X; x <= right; x++)
				if (((x - rectangle.X) & 1) == 0) {
					dc.FillRectangle (brush, x, rectangle.Y, 1, 1);
					dc.FillRectangle (brush, x, bottom, 1, 1);
				}
			for (int y = rectangle.Y; y <= bottom; y++)
				if (((y - rectangle.Y) & 1) == 0) {
					dc.FillRectangle (brush, rectangle.X, y, 1, 1);
					dc.FillRectangle (brush, right, y, 1, 1);
				}
			dc.SmoothingMode = old;
		}

		/// <summary>A drop-down's list, which Windows frames like a menu rather than like a
		/// control.</summary>
		private static bool IsPopupList (Control control)
		{
			return control != null && control.GetType ().Name == "ComboListBox";
		}

		/// <summary>Whether this is something you type into, as opposed to a list you pick from.
		/// Windows frames the two differently.</summary>
		private static bool IsTextInput (Control control)
		{
			for (Type t = control?.GetType (); t != null; t = t.BaseType) {
				switch (t.Name) {
					case "TextBoxBase":
					case "ComboBox":
					case "DateTimePicker":
						return true;
				}
			}
			return false;
		}

		/// <summary>One ring of a carved bevel: lit from the top left.</summary>
		private void DrawBevel (Graphics dc, Rectangle r, Color topLeft, Color bottomRight)
		{
			if (r.Width <= 1 || r.Height <= 1)
				return;
			Pen tl = ResPool.GetPen (topLeft), br = ResPool.GetPen (bottomRight);
			dc.DrawLine (tl, r.X, r.Y, r.Right - 1, r.Y);
			dc.DrawLine (tl, r.X, r.Y, r.X, r.Bottom - 1);
			dc.DrawLine (br, r.X, r.Bottom - 1, r.Right - 1, r.Bottom - 1);
			dc.DrawLine (br, r.Right - 1, r.Y, r.Right - 1, r.Bottom - 1);
		}

		/// <summary>Whether Windows draws this control's frame with the visual style. It themes
		/// what holds content -- text, lists, trees, grids -- and nothing else.</summary>
		private static bool IsThemedFrame (Control control)
		{
			if (control == null)
				return true;
			for (Type t = control.GetType (); t != null; t = t.BaseType) {
				switch (t.Name) {
					case "TextBoxBase":
					case "ComboBox":
					case "ListBox":
					case "ListView":
					case "TreeView":
					case "DataGridView":
					case "UpDownBase":
					case "PropertyGrid":
						return true;
					case "PictureBox":
					case "Panel":
					// Windows never themed a rich text box's frame either; it keeps the carved one.
					case "RichTextBox":
						return false;
				}
			}
			return true;
		}

		public override void DrawControlBorder (Graphics dc, Rectangle bounds, Control control, bool sunken)
		{
			if (bounds.Width <= 1 || bounds.Height <= 1)
				return;
			// Windows themes the controls that hold content you can edit or pick from, and leaves the
			// rest with the classic carved bevel -- a picture box still has the two-tone sunken frame,
			// #A0A0A0 outside and #696969 in. Giving everything the hairline flattened those.
			if (sunken && !IsThemedFrame (control)) {
				// Drawn here rather than through CPDrawBorder3D, which this theme flattens to a
				// hairline -- routing through it gave the picture box the same single line again.
				// Two rings of one colour each is a frame, not a bevel: the light has to come from the
				// top left, so those edges are dark and the opposite ones light. Measured off a stock
				// picture box, outer #A0A0A0 / #FFFFFF and inner #696969 / #E3E3E3.
				DrawBevel (dc, bounds, ColorControlDark, ColorControlLightLight);
				DrawBevel (dc, Rectangle.Inflate (bounds, -1, -1), ColorControlDarkDark, ColorControlLight);
				return;
			}

			var frame = new Rectangle (bounds.X, bounds.Y, bounds.Width - 1, bounds.Height - 1);

			// A text input is not a uniform box in Windows 11. Three of its sides are a very light
			// #ECECEC and the bottom carries a darker line -- #838383 at rest, the accent colour while
			// it has the focus. That underline is how Windows says where you are typing; ours drew the
			// bottom colour on all four sides, which made every input heavier than stock's and left the
			// focus saying nothing at all.
			if (sunken && IsTextInput (control)) {
				bool enabled = control == null || control.Enabled;
				Color sides = enabled ? InputFrameLight : ButtonBorderDisabled;
				Color under = !enabled ? ButtonBorderDisabled
					    : control != null && control.Focused ? ButtonBorderHover
					    : InputUnderline;
				DrawRoundedOutline (dc, frame, sides);
				dc.DrawLine (ResPool.GetPen (under), frame.X + 1, frame.Bottom, frame.Right - 1, frame.Bottom);
				return;
			}

			// A list, tree or grid keeps a single even frame. It does not take the accent when it has
			// the focus: selecting a row in a list box turned the whole frame blue, which Windows does
			// not do -- the selection says which row, the frame says nothing.
			Color edge;
			if (IsPopupList (control))
				edge = PopupBorder;
			else if (!sunken)
				// A plain WS_BORDER -- what a panel with FixedSingle asks for -- is black in Windows,
				// not the light grey a themed frame gets.
				edge = ColorWindowText;
			else if (control != null && !control.Enabled)
				edge = ButtonBorderDisabled;
			else
				edge = IsSpinner (control) ? SpinnerFrame : ListFrame;

			// Square, not rounded. A list, a grid and a spin box are plain rectangles in Windows;
			// dropping their corner pixels made them look softened at every corner.
			dc.DrawRectangle (ResPool.GetPen (edge), frame.X, frame.Y, frame.Width, frame.Height);
		}

		// The spin buttons came out as the carved 1995 arrows, because the base theme draws them
		// with ControlPaint.DrawScrollButton. Measured off a stock NumericUpDown, each button is
		// its own box one pixel in from the control's sides: face #FAFAFA, a hairline #D2D2D2
		// frame with a darker #BCBCBC bottom edge, and a small solid triangle in #1A1A1A -- a
		// triangle, not the chevron a combo box gets.
		public override void UpDownBaseDrawButton (Graphics g, Rectangle bounds, bool top,
					    VisualStyles.PushButtonState state)
		{
			if (bounds.Width <= 4 || bounds.Height <= 4)
				return;

			var box = new Rectangle (bounds.X + 1, bounds.Y, bounds.Width - 3, bounds.Height - 1);

			// A hovered spin button takes a tint of the accent rather than a grey wash, and its arrow
			// goes accent blue with it.
			bool hot = state == VisualStyles.PushButtonState.Hot;
			bool pushed = state == VisualStyles.PushButtonState.Pressed;
			Color face = pushed ? ButtonFaceHover
				   : hot ? HeaderHotFace
				   : Color.FromArgb (250, 250, 250);

			SmoothingMode old = g.SmoothingMode;
			g.SmoothingMode = SmoothingMode.None;
			g.FillRectangle (ResPool.GetSolidBrush (face), box.X, box.Y, box.Width + 1, box.Height + 1);
			// Under the pointer the button is outlined in the accent, which is what separates it from
			// the field behind it; at rest it keeps the hairline and its darker bottom edge.
			if (hot || pushed) {
				DrawRoundedOutline (g, box, ButtonBorderHover);
			} else {
				DrawRoundedOutline (g, box, Color.FromArgb (210, 210, 210));
				g.DrawLine (ResPool.GetPen (Color.FromArgb (188, 188, 188)),
						     box.X + 1, box.Bottom, box.Right - 1, box.Bottom);
			}

			// Three solid rows, one/three/five pixels wide, apex away from the middle. Drawn as
			// rows rather than a filled path: a path is flattened into unjoined segments here and
			// would not close into a triangle.
			Color glyph = state == VisualStyles.PushButtonState.Disabled ? ButtonBorderDisabled
				    : hot || pushed ? ButtonBorderHover
				    : Color.FromArgb (26, 26, 26);
			Brush brush = ResPool.GetSolidBrush (glyph);
			int cx = box.X + box.Width / 2 + 1;
			int cy = box.Y + box.Height / 2;
			for (int i = 0; i < 3; i++) {
				int half = top ? i : 2 - i;
				g.FillRectangle (brush, cx - half, cy - 1 + i, half * 2 + 1, 1);
			}
			g.SmoothingMode = old;
		}

		// So the buttons light up under the pointer, which is the whole reason a flat button reads
		// as a button at all.
				public override bool UpDownBaseHasHotButtonStyle => true;

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


		/// <summary>The room a tab leaves round its label. One pixel more above and below than the
		/// classic metric, which is what makes the row of tabs the height Windows draws it: the
		/// tabs stood two pixels short, and since the row's height is what the page is laid out
		/// below, padding it here moves the page down with it rather than leaving the two to
		/// disagree.</summary>
		public override Point TabControlDefaultPadding {
			get { return new Point (6, 4); }
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

		/// <summary>A grid's headers. Flat and white like a list view's, but ruled with the grid's
		/// own colour from edge to edge: the list view's separator stops three pixels short at each
		/// end, which down a column of row headers reads as a dashed line rather than a rule.
		/// <para>A row header on a selected row takes a light wash instead of the full selection
		/// colour, which is what lets its arrow stay black.</para></summary>
		private bool DrawHeaderCell (DataGridViewHeaderCell cell, Graphics g, Rectangle bounds)
		{
			DataGridView grid = cell == null ? null : cell.DataGridView;
			if (grid == null || bounds.Width <= 0 || bounds.Height <= 0)
				return false;
			// Washed when the whole row is selected, and only then: a single cell selected somewhere
			// along the row leaves its header the plain face. Not the header cell's own Selected --
			// a header cell is never itself selected, so asking it marks nothing ever.
			bool marked = false;
			if (cell is DataGridViewRowHeaderCell) {
				try {
					DataGridViewRow row = cell.OwningRow;
					marked = row != null && !row.IsShared && row.Selected;
				} catch (Exception) {
				}
			}
			g.FillRectangle (ResPool.GetSolidBrush (marked ? HeaderMarkedFace : ColorWindow), bounds);
			Pen pen = ResPool.GetPen (grid.GridColor);
			g.DrawLine (pen, bounds.Right - 1, bounds.Y, bounds.Right - 1, bounds.Bottom - 1);
			g.DrawLine (pen, bounds.X, bounds.Bottom - 1, bounds.Right - 1, bounds.Bottom - 1);
			// The grid is ruled on all four sides, so the headers along its top and down its left
			// carry the outer lines too. Windows draws them, and against the control's own border they
			// read as a two-pixel edge -- which is why leaving them out made ours look a pixel thin all
			// the way round the first row and the first column.
			if (cell is DataGridViewRowHeaderCell || cell is DataGridViewTopLeftHeaderCell)
				g.DrawLine (pen, bounds.X, bounds.Y, bounds.X, bounds.Bottom - 1);
			if (cell is DataGridViewColumnHeaderCell)
				g.DrawLine (pen, bounds.X, bounds.Y, bounds.Right - 1, bounds.Y);
			return true;
		}

		/// <summary>The wash over a row header whose row is selected.</summary>
		private static readonly Color HeaderMarkedFace = Color.FromArgb (188, 220, 244);

		// Measured down a stock grid: every rule, header and cell alike, is this one grey.
		public override Color DataGridViewGridColor {
			get { return Color.FromArgb (100, 100, 100); }
		}

		public override Color DataGridViewRowHeaderCellForeColor (DataGridViewRowHeaderCell cell,
									  DataGridViewCellStyle style, bool selected)
		{
			// The header is washed, not filled, so its ink never turns white.
			return style.ForeColor;
		}

		public override bool DataGridViewRowHeaderCellDrawSelectionBackground (DataGridViewRowHeaderCell cell)
		{
			// Already washed by the background above; the classic fill would bury it.
			return cell != null && cell.DataGridView != null;
		}

		// ---- track bar ---------------------------------------------------------------
		//
		// A flat pale channel with a solid accent-coloured slider, in place of the sunken groove and
		// the bevelled grey pointer the classic theme carves. The shapes and their positions are the
		// classic ones -- the control works out where the thumb goes, and hit testing has to agree
		// with what is drawn -- so only the painting changes.

		private static readonly Color TrackChannel = Color.FromArgb (231, 234, 234);
		private static readonly Color TrackChannelEdge = Color.FromArgb (214, 214, 214);
		private static readonly Color TrackTick = Color.FromArgb (196, 196, 196);

		private void FillChannel (Graphics dc, Rectangle channel)
		{
			if (channel.Width <= 0 || channel.Height <= 0)
				return;
			dc.FillRectangle (ResPool.GetSolidBrush (TrackChannel), channel);
			dc.DrawRectangle (ResPool.GetPen (TrackChannelEdge), channel.X, channel.Y,
					  channel.Width - 1, channel.Height - 1);
		}

		protected override void TrackBarDrawHorizontalTrack (Graphics dc, Rectangle thumb_area,
								     Point channel_startpoint, Rectangle clippingArea)
		{
			FillChannel (dc, new Rectangle (channel_startpoint.X, channel_startpoint.Y,
							thumb_area.Width, 4));
		}

		protected override void TrackBarDrawVerticalTrack (Graphics dc, Rectangle thumb_area,
								   Point channel_startpoint, Rectangle clippingArea)
		{
			FillChannel (dc, new Rectangle (channel_startpoint.X, channel_startpoint.Y,
							4, thumb_area.Height));
		}

		/// <summary>The slider, as a pointer with its tip on the given side, or a plain bar when
		/// <paramref name="tip"/> is none. Sampled rather than filled as a polygon: the recorder
		/// leaves a polygon's diagonals hard, and against the pale channel behind it a stepped edge
		/// on a shape this small is the whole of what one sees.</summary>
		private void FillPointer (Graphics dc, Rectangle body, TrackBarTip tip, Color colour, Color behind)
		{
			Rectangle bounds = body;
			double cx = body.X + body.Width / 2.0, cy = body.Y + body.Height / 2.0;
			Func<double, double, bool> inside;
			switch (tip) {
			case TrackBarTip.Bottom:
				inside = (x, y) => {
					if (y <= body.Bottom - PointerTip)
						return x >= body.X && x <= body.Right;
					double t = (body.Bottom - y) / (double) PointerTip;
					double half = body.Width / 2.0 * t;
					return x >= cx - half && x <= cx + half;
				};
				break;
			case TrackBarTip.Top:
				inside = (x, y) => {
					if (y >= body.Y + PointerTip)
						return x >= body.X && x <= body.Right;
					double t = (y - body.Y) / (double) PointerTip;
					double half = body.Width / 2.0 * t;
					return x >= cx - half && x <= cx + half;
				};
				break;
			case TrackBarTip.Right:
				inside = (x, y) => {
					if (x <= body.Right - PointerTip)
						return y >= body.Y && y <= body.Bottom;
					double t = (body.Right - x) / (double) PointerTip;
					double half = body.Height / 2.0 * t;
					return y >= cy - half && y <= cy + half;
				};
				break;
			case TrackBarTip.Left:
				inside = (x, y) => {
					if (x >= body.X + PointerTip)
						return y >= body.Y && y <= body.Bottom;
					double t = (x - body.X) / (double) PointerTip;
					double half = body.Height / 2.0 * t;
					return y >= cy - half && y <= cy + half;
				};
				break;
			default:
				dc.FillRectangle (ResPool.GetSolidBrush (colour), body);
				return;
			}
			FillAntialiased (dc, bounds, colour, behind, inside);
		}

		private enum TrackBarTip { None, Top, Bottom, Left, Right }

		/// <summary>How far along the slider its point runs.</summary>
		private const int PointerTip = 4;

		private Color ThumbColour (TrackBar bar)
		{
			return bar != null && !bar.Enabled ? ColorGrayText : ColorHighlight;
		}

		protected override void TrackBarDrawHorizontalThumbBottom (Graphics dc, Rectangle thumb_pos,
									   Brush br_thumb, Rectangle clippingArea,
									   TrackBar trackBar)
		{
			FillPointer (dc, new Rectangle (thumb_pos.X, thumb_pos.Y, 11, 21), TrackBarTip.Bottom,
				     ThumbColour (trackBar), trackBar.BackColor);
		}

		protected override void TrackBarDrawHorizontalThumbTop (Graphics dc, Rectangle thumb_pos,
									Brush br_thumb, Rectangle clippingArea,
									TrackBar trackBar)
		{
			FillPointer (dc, new Rectangle (thumb_pos.X, thumb_pos.Y, 11, 21), TrackBarTip.Top,
				     ThumbColour (trackBar), trackBar.BackColor);
		}

		protected override void TrackBarDrawHorizontalThumb (Graphics dc, Rectangle thumb_pos,
								     Brush br_thumb, Rectangle clippingArea,
								     TrackBar trackBar)
		{
			FillPointer (dc, new Rectangle (thumb_pos.X, thumb_pos.Y, 11, 21), TrackBarTip.None,
				     ThumbColour (trackBar), trackBar.BackColor);
		}

		protected override void TrackBarDrawVerticalThumbRight (Graphics dc, Rectangle thumb_pos,
									Brush br_thumb, Rectangle clippingArea,
									TrackBar trackBar)
		{
			FillPointer (dc, new Rectangle (thumb_pos.X, thumb_pos.Y, 21, 11), TrackBarTip.Right,
				     ThumbColour (trackBar), trackBar.BackColor);
		}

		protected override void TrackBarDrawVerticalThumbLeft (Graphics dc, Rectangle thumb_pos,
								       Brush br_thumb, Rectangle clippingArea,
								       TrackBar trackBar)
		{
			FillPointer (dc, new Rectangle (thumb_pos.X, thumb_pos.Y, 21, 11), TrackBarTip.Left,
				     ThumbColour (trackBar), trackBar.BackColor);
		}

		protected override void TrackBarDrawVerticalThumb (Graphics dc, Rectangle thumb_pos,
								   Brush br_thumb, Rectangle clippingArea,
								   TrackBar trackBar)
		{
			FillPointer (dc, new Rectangle (thumb_pos.X, thumb_pos.Y, 21, 11), TrackBarTip.None,
				     ThumbColour (trackBar), trackBar.BackColor);
		}

		protected override ITrackBarTickPainter GetTrackBarTickPainter (Graphics g)
		{
			return new ModernTickPainter (g, ResPool.GetPen (TrackTick));
		}

		private class ModernTickPainter : ITrackBarTickPainter
		{
			private readonly Graphics g;
			private readonly Pen pen;

			public ModernTickPainter (Graphics graphics, Pen tickPen)
			{
				g = graphics;
				pen = tickPen;
			}

			public void Paint (float x1, float y1, float x2, float y2)
			{
				g.DrawLine (pen, x1, y1, x2, y2);
			}
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

		// Stock gives a column header ten pixels of air around the text, not the classic theme's
		// five: measured against a stock list view, its first row starts five pixels lower than
		// ours did.
		public override int ListViewGetHeaderHeight (ListView listView, Font font)
		{
			return font.Height + 8;
		}

		/// <summary>Measured off a Windows 11 menu: a two-entry File menu comes out 104 pixels
		/// wide against a widest caption of 36. Ours came out twelve wider, because the item's own
		/// padding is counted into the caption before this is added and Windows counts the margins
		/// once.</summary>
		public override int ToolStripDropDownMenuMargin (bool imageMargin)
		{
			return imageMargin ? 56 : 35;
		}

		public override int ListViewDetailsRowGap {
			get { return 1; }
		}

		protected override void ListViewDrawColumnHeaderBackground (ListView listView, ColumnHeader columnHeader,
									    Graphics g, Rectangle area, Rectangle clippingArea)
		{
			bool pressed = listView.HeaderStyle == ColumnHeaderStyle.Clickable && columnHeader.Pressed;
			bool hot = !pressed && listView.header_control != null
				&& listView.header_control.EnteredColumnHeader == columnHeader;
			DrawModernHeaderCell (g, area, pressed, false, hot);
		}

		// ---- progress bar, combo arrow, check box primitive --------------------------

		// So the control keeps repainting while the highlight travels.
		public override bool ProgressBarAnimates => true;

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
					 (state & ButtonState.Inactive) == 0, (state & ButtonState.Pushed) != 0,
					 ColorWindow);
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
			//
			// Not on the control's edge, though: Windows leaves a pixel of the calendar's own
			// background outside the frame on every side, and the hairline it draws is #979797.
			Rectangle r = mc.ClientRectangle;
			if (r.Width > 3 && r.Height > 3) {
				r.Inflate (-1, -1);
				dc.DrawRectangle (ResPool.GetPen (Color.FromArgb (151, 151, 151)),
						   r.X, r.Y, r.Width - 1, r.Height - 1);
			}
		}

		// The marker's right edge lines up with the third column's, which is where Windows puts it.
		// The same frame a combo box's list gets: a popup is a popup.
		protected override Color MonthCalendarPopupBorderColor (MonthCalendar mc)
		{
			return PopupBorder;
		}

		// Windows lines the units up down each column: a single digit sits exactly where the units
		// digit of a double one does. Padding the string with a space does not achieve it -- the
		// typographic format these are drawn with trims a leading space away again -- so give every
		// number the width of two digits, centred in the cell, and range it right inside that.
		protected override Rectangle MonthCalendarDateBounds (MonthCalendar mc, Graphics dc, Rectangle cell)
		{
			if (dc == null || mc == null || cell.Width <= 0)
				return cell;
			int digits = (int) Math.Ceiling (dc.MeasureString ("00", mc.Font).Width);
			if (digits <= 0 || digits >= cell.Width)
				return cell;
			return new Rectangle (cell.X + (cell.Width - digits) / 2, cell.Y, digits, cell.Height);
		}

		private StringFormat units_format;

		protected override StringFormat MonthCalendarDateFormat (MonthCalendar mc)
		{
			if (units_format == null)
				units_format = new StringFormat (StringFormat.GenericTypographic) {
					Alignment = StringAlignment.Far,
					LineAlignment = StringAlignment.Center,
					FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.MeasureTrailingSpaces,
				};
			return units_format;
		}

		protected override int MonthCalendarTodayIndent (MonthCalendar mc, int client_width, Size cell, int margin)
		{
			return margin + 2 * cell.Width;
		}

		// Windows colours the day under the pointer rather than shading behind it: the number
		// itself goes accent blue and the cell stays white.
		protected override Color MonthCalendarHoverForeColor (MonthCalendar mc)
		{
			return ButtonBorderHover;
		}

		protected override Color MonthCalendarHoverBackColor (MonthCalendar mc)
		{
			return HeaderHotFace;
		}

		// The current cell of a zoomed view is boxed rather than washed: a light face inside a
		// hairline, which is what Windows draws around the month or year you are on.
		protected override void MonthCalendarDrawZoomedSelection (Graphics dc, MonthCalendar mc, Rectangle cell)
		{
			Rectangle box = Rectangle.Inflate (cell, -4, -2);
			if (box.Width <= 1 || box.Height <= 1)
				return;
			dc.FillRectangle (ResPool.GetSolidBrush (GlyphFace), box);
			DrawRoundedOutline (dc, new Rectangle (box.X, box.Y, box.Width - 1, box.Height - 1),
				      ButtonBorderHover);
		}

		// The month and year take the accent under the pointer, which is how Windows says the
		// heading is something you can click.
		protected override Color MonthCalendarTitleForeColor (MonthCalendar mc)
		{
			return mc.HoverTitle ? ButtonBorderHover : ColorControlText;
		}

		// Stock draws these in plain black, not the grey the classic theme uses.
		protected override Color MonthCalendarDayNameColor (MonthCalendar mc) => ColorControlText;

		// Measured off a stock MonthCalendar at Segoe UI 9: the cell is 31 x 15 where the classic
		// formula gives 29 x 16, because Windows sizes it to the widest abbreviated day name plus
		// padding rather than to 1.8 times the font height.
		public override Size MonthCalendarCellSize (MonthCalendar mc, Size proposed)
		{
			int height = Math.Max (1, proposed.Height - 1);
			return new Size ((int) Math.Round (height * 31.0 / 15.0), height);
		}

		// Five pixels between the frame and the grid, on every side.
		public override int MonthCalendarMargin (MonthCalendar mc) => 5;

		protected override void MonthCalendarDrawDayNameDivider (Graphics dc, MonthCalendar mc,
					     int x1, int x2, int y)
		{
			// There is a rule under the day names, but at #F5F5F5 it is almost invisible -- faint
			// enough that reading it off a screen grab took a threshold tight enough to separate it
			// from white. The classic theme draws the same line in the fore colour, which is black.
			dc.DrawLine (ResPool.GetPen (Color.FromArgb (245, 245, 245)), x1, y, x2, y);
		}

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

			// No chrome, ever: the header is one flat strip and the arrow says everything by its own
			// colour. A wash behind a pressed one was chrome Windows does not draw.

			int cx = button.X + button.Width / 2;
			int cy = button.Y + button.Height / 2;
			int h = Math.Max (3, Math.Min (5, button.Height / 3));
			int w = Math.Max (2, h - 1);
			SmoothingMode old = dc.SmoothingMode;
			dc.SmoothingMode = SmoothingMode.AntiAlias;
			// Blue under the pointer and while held, the way the heading beside it goes blue.
			bool hovered = is_previous ? mc.HoverPrevious : mc.HoverNext;
			Color ink = !mc.Enabled ? ColorGrayText
				  : clicked || hovered ? ButtonBorderHover
				  : ColorControlText;
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
		// Timed off a stock progress bar: the crest travels about 300 pixels a second, so it
		// crosses the fill in a little over half a second, and the cycle repeats every second and
		// a half with a pause in between. Its crest is #34AA34 through the middle of the bar and
		// much lighter on the top and bottom rows, which is what gives it the rounded look.
		private const int SweepPeriod = 1500;
		private const double SweepTravel = 0.6;       // of the period spent moving
		private const double SweepWidth = 0.36;       // half-width, of the fill
		private static readonly Color SweepCrest = Color.FromArgb (52, 170, 52);
		private static readonly Color SweepEdge = Color.FromArgb (128, 198, 128);
		private static readonly object SweepKey = new object ();

		/// <summary>The strip of a progress bar the highlight is crossing just now, widened by one
		/// frame's travel in each direction so the band it is about to leave is repainted too.
		/// Empty while the highlight is between passes, when nothing is changing at all.</summary>
		private static Rectangle SweepBand (ProgressBar ctrl)
		{
			Rectangle bounds = ctrl.ClientRectangle;
			if (bounds.Width <= 2 || bounds.Height <= 2)
				return Rectangle.Empty;
			int range = ctrl.Maximum - ctrl.Minimum;
			if (range <= 0)
				return Rectangle.Empty;
			double fraction = (double) (ctrl.Value - ctrl.Minimum) / range;
			Rectangle fill = Rectangle.Inflate (bounds, -1, -1);
			fill.Width = (int) Math.Round (fill.Width * Math.Min (1.0, fraction));
			if (fill.Width <= 0)
				return Rectangle.Empty;

			double phase = Animation.Value (ctrl, SweepKey);
			if (phase > SweepTravel)
				return Rectangle.Empty;
			int half = Math.Max (1, (int) Math.Round (SweepWidth * fill.Width));
			int crest = fill.X - half + (int) Math.Round (phase / SweepTravel * (fill.Width + 2 * half));
			// One frame of travel, so the trailing edge is cleaned up as the crest moves on.
			int step = Math.Max (2, (int) Math.Round ((fill.Width + 2 * half) * 16.0 / (SweepPeriod * SweepTravel)));
			int left = Math.Max (bounds.X, crest - half - step);
			int right = Math.Min (bounds.Right, crest + half + step);
			return right <= left ? Rectangle.Empty
				    : new Rectangle (left, bounds.Y, right - left, bounds.Height);
		}
		private static readonly Color SweepNear = Color.FromArgb (63, 185, 63);

		// #0F7B0F, read off a stock progress bar. Ours was a brighter, yellower green.
		private static readonly Color ProgressFill = Color.FromArgb (15, 123, 15);
		private static readonly Color TabPaneFace = Color.FromArgb (249, 249, 249);
		private static readonly Color TabItemFace = Color.FromArgb (240, 240, 240);
		/// <summary>A tab that is not the one on show. Just off the control's own grey, so the row
		/// of tabs reads as a row rather than as part of the form behind it.</summary>
		private static readonly Color TabRestFace = Color.FromArgb (243, 243, 243);
		private static readonly Color TabEdge = Color.FromArgb (229, 229, 229);
		/// <summary>How far the tab on show stands above its neighbours.</summary>
		private const int TabRise = 2;
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
			if (fill.Width <= 0 || fill.Height <= 0)
				return;
			dc.FillRectangle (ResPool.GetSolidBrush (ProgressFill), fill);

			// A highlight sweeps along the fill and repeats, which is why a stock progress bar whose
			// value never changes is still not a static image.
			// The same clock the scroll bars fade on. Asking for it here keeps the bar repainting for
			// as long as it is drawn, and stops the moment it is not -- and only the band the highlight
			// is passing over is repainted, not the whole bar.
			Animation.Loop (ctrl, SweepKey, SweepPeriod, () => SweepBand (ctrl));
			double phase = Animation.Value (ctrl, SweepKey);
			if (phase > SweepTravel)
				return;                                         // the pause between passes

			int half = Math.Max (1, (int) Math.Round (SweepWidth * fill.Width));
			double travel = phase / SweepTravel;                // 0 .. 1 across one pass
			int crest = fill.X - half + (int) Math.Round (travel * (fill.Width + 2 * half));

			for (int x = Math.Max (fill.X, crest - half); x < Math.Min (fill.Right, crest + half); x++) {
				double d = (x - crest) / (double) half;             // -1 .. 1 across the crest
				double lift = Math.Max (0.0, 1.0 - d * d);          // and nothing at either end
				// Over the WHOLE height, frame included. The highlight lifts the top and bottom rules
				// most of all -- that is what makes the bar look rounded rather than flat -- and
				// confining it to the fill left those two rows untouched, so the effect went missing.
				for (int y = bounds.Y; y < bounds.Bottom; y++) {
					int depth = Math.Min (y - bounds.Y, bounds.Bottom - 1 - y);
					Color from = depth == 0 ? HairLine : ProgressFill;
					Color to = depth == 0 ? SweepEdge : depth <= 2 ? SweepNear : SweepCrest;
					Color c = Color.FromArgb (
						(int) Math.Round (from.R + (to.R - from.R) * lift),
						(int) Math.Round (from.G + (to.G - from.G) * lift),
						(int) Math.Round (from.B + (to.B - from.B) * lift));
					dc.FillRectangle (ResPool.GetSolidBrush (c), x, y, 1, 1);
				}
			}
		}

		/// <summary>A Windows 11 scroll bar. At rest it is a thin line and nothing else -- no
		/// track, no arrows. When the pointer arrives the track fades in, the line grows into a
		/// rounded thumb and the arrows appear; when it leaves, all of that fades back out. The
		/// fade is driven by the shared animation clock, which is also what carries the progress
		/// bar's highlight along.</summary>
		private void DrawModernScrollBar (Graphics dc, ScrollBar bar, Rectangle client,
					  Rectangle first, Rectangle second, Rectangle thumb)
		{
			// How far open the bar is. The bar itself decides when to open and shut -- see its mouse
			// enter and leave -- and the theme only draws whatever that has arrived at.
			double open = Animation.Value (bar, ScrollBar.HoverKey);
			if (bar.Capture)
				open = 1.0;                                     // dragging holds it open

			// The track is always there. A scroll bar inside a list or a grid keeps its channel in
			// Windows whether the pointer is near it or not -- it is the thumb and the arrows that come
			// and go. Fading the track out to the control's own colour left it white where a stock one
			// is grey.
			dc.FillRectangle (ResPool.GetSolidBrush (ScrollTrack), client);

			// Nothing at all below a twentieth: the last few per cent of a fade are still a visible
			// grey against the track, and left there they read as an arrow that never went away.
			if (open > 0.05) {
				DrawScrollArrow (dc, first, bar.vert ? ArrowDirection.Up : ArrowDirection.Left,
						  bar.Enabled, open);
				DrawScrollArrow (dc, second, bar.vert ? ArrowDirection.Down : ArrowDirection.Right,
						  bar.Enabled, open);
			}

			if (!bar.Enabled || thumb.Width <= 0 || thumb.Height <= 0)
				return;

			// Two pixels at rest, seven when open, and rounded at both ends either way.
			int thickness = (int) Math.Round (ScrollRestThickness
				 + (ScrollOpenThickness - ScrollRestThickness) * open);
			Rectangle slim = thumb;
			if (bar.vert) {
				int inset = Math.Max (0, (thumb.Width - thickness) / 2);
				slim.X += inset;
				slim.Width = Math.Max (1, thumb.Width - inset * 2);
			} else {
				int inset = Math.Max (0, (thumb.Height - thickness) / 2);
				slim.Y += inset;
				slim.Height = Math.Max (1, thumb.Height - inset * 2);
			}
			FillCapsule (dc, slim, Blend (ScrollThumbRest, ScrollThumb, open), ScrollTrack);
		}

		private const int ScrollRestThickness = 2;
		private const int ScrollOpenThickness = 7;
		// Darker than it looks it should be on paper: at two pixels wide with both edges softened
		// there is hardly a solid core left, so a paler colour washes out altogether. Windows barely
		// changes the colour between resting and open at all -- it is the width that changes.
		private static readonly Color ScrollThumbRest = Color.FromArgb (138, 138, 138);

		/// <summary>Mix two colours, <paramref name="t"/> of the way from the first to the
		/// second.</summary>
		private static Color Blend (Color from, Color to, double t)
		{
			if (t <= 0) return from;
			if (t >= 1) return to;
			return Color.FromArgb (
				(int) Math.Round (from.R + (to.R - from.R) * t),
				(int) Math.Round (from.G + (to.G - from.G) * t),
				(int) Math.Round (from.B + (to.B - from.B) * t));
		}

		/// <summary>A rectangle with semicircular ends -- the shape of a modern scroll thumb.
		/// Drawn a row at a time: a path here is flattened into unjoined segments and a stroked
		/// arc fringes outside its own bounds.</summary>
		/// <summary>Fill whatever <paramref name="inside"/> describes, softening its edges by how
		/// much of each pixel the shape actually covers.
		/// <para>This recorder antialiases nothing of its own for these shapes -- a path is
		/// flattened into unjoined segments and a stroked arc fringes outside its own bounds -- so
		/// they have been drawn a whole pixel at a time, and a diagonal came out as a staircase.
		/// Sixteen samples a pixel is enough to read as a smooth edge at these sizes.</para></summary>
		private void FillAntialiased (Graphics dc, Rectangle bounds, Color colour, Color behind,
					        Func<double, double, bool> inside)
		{
			if (bounds.Width <= 0 || bounds.Height <= 0)
				return;
			SmoothingMode old = dc.SmoothingMode;
			dc.SmoothingMode = SmoothingMode.None;
			const int Samples = 4;
			const int Full = Samples * Samples;
			Brush solid = ResPool.GetSolidBrush (colour);
			for (int y = bounds.Y; y < bounds.Bottom; y++) {
				int run = -1;	// where the current stretch of fully covered pixels began
				for (int x = bounds.X; x <= bounds.Right; x++) {
					int hits = 0;
					if (x < bounds.Right)
						for (int sy = 0; sy < Samples; sy++)
							for (int sx = 0; sx < Samples; sx++)
								if (inside (x + (sx + 0.5) / Samples, y + (sy + 0.5) / Samples))
									hits++;
					if (hits == Full) {
						if (run < 0)
							run = x;
						continue;
					}
					// The stretch ends here, so lay it down in one piece. A row of one-pixel rectangles is
					// not the same thing to this renderer -- each comes out short of the colour asked for,
					// so a shape filled that way reads as translucent -- and it is a great many more shapes
					// than the row needs.
					if (run >= 0) {
						dc.FillRectangle (solid, run, y, x - run, 1);
						run = -1;
					}
					if (hits > 0)
						dc.FillRectangle (ResPool.GetSolidBrush (
							       Blend (behind, colour, hits / (double) Full)), x, y, 1, 1);
				}
			}
			dc.SmoothingMode = old;
		}

		private void FillCapsule (Graphics dc, Rectangle r, Color colour)
		{
			FillCapsule (dc, r, colour, ColorWindow);
		}

		/// <summary>A rectangle with semicircular ends -- the shape of a modern scroll thumb.</summary>
		private void FillCapsule (Graphics dc, Rectangle r, Color colour, Color behind)
		{
			if (r.Width <= 0 || r.Height <= 0)
				return;
			bool vertical = r.Height >= r.Width;
			double radius = (vertical ? r.Width : r.Height) / 2.0;
			if (radius < 0.5) {
				dc.FillRectangle (ResPool.GetSolidBrush (colour), r);
				return;
			}

			// The two centres the end caps turn about; between them the shape is a plain rectangle.
			double ax = vertical ? r.X + radius : r.X + radius;
			double ay = vertical ? r.Y + radius : r.Y + radius;
			double bx = vertical ? ax : r.Right - radius;
			double by = vertical ? r.Bottom - radius : ay;
			FillAntialiased (dc, r, colour, behind, (px, py) => {
				if (vertical) {
					if (py >= ay && py <= by)
						return px >= r.X && px <= r.Right;
					double cy = py < ay ? ay : by;
					double dx = px - ax, dy = py - cy;
					return dx * dx + dy * dy <= radius * radius;
				}
				if (px >= ax && px <= bx)
					return py >= r.Y && py <= r.Bottom;
				double cx2 = px < ax ? ax : bx;
				double ddx = px - cx2, ddy = py - ay;
				return ddx * ddx + ddy * ddy <= radius * radius;
			});
		}

		private void DrawScrollArrow (Graphics dc, Rectangle area, ArrowDirection direction, bool enabled)
		{
			DrawScrollArrow (dc, area, direction, enabled, 1.0);
		}

		/// <summary>The arrow at one end of a scroll bar, faded in by <paramref name="open"/> as the
		/// bar opens under the pointer. It has no arrows at all at rest.</summary>
		private void DrawScrollArrow (Graphics dc, Rectangle area, ArrowDirection direction, bool enabled,
					      double open)
		{
			if (area.Width <= 0 || area.Height <= 0 || open <= 0.01)
				return;
			int cx = area.X + area.Width / 2;
			int cy = area.Y + area.Height / 2;
			int r = Math.Max (2, Math.Min (4, Math.Min (area.Width, area.Height) / 4));
			// Fading toward the track rather than toward white, so the arrow dissolves into the bar
			// it sits on instead of flashing pale against it.
			Color ink = Blend (ScrollTrack, enabled ? ScrollArrow : ColorGrayText, open);

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

			// Sampled rather than handed to FillPolygon: this recorder gives a filled path no
			// antialiasing, and a small triangle drawn that way is all staircase.
			Point a = arrow[0], b = arrow[1], c = arrow[2];
			var box = new Rectangle (
				Math.Min (a.X, Math.Min (b.X, c.X)) - 1, Math.Min (a.Y, Math.Min (b.Y, c.Y)) - 1,
				Math.Max (a.X, Math.Max (b.X, c.X)) - Math.Min (a.X, Math.Min (b.X, c.X)) + 3,
				Math.Max (a.Y, Math.Max (b.Y, c.Y)) - Math.Min (a.Y, Math.Min (b.Y, c.Y)) + 3);
			FillAntialiased (dc, box, ink, ScrollTrack, (px, py) => {
				// Inside when the point falls on the same side of all three edges.
				double d1 = (px - b.X) * (a.Y - b.Y) - (a.X - b.X) * (py - b.Y);
				double d2 = (px - c.X) * (b.Y - c.Y) - (b.X - c.X) * (py - c.Y);
				double d3 = (px - a.X) * (c.Y - a.Y) - (c.X - a.X) * (py - a.Y);
				bool negative = d1 < 0 || d2 < 0 || d3 < 0;
				bool positive = d1 > 0 || d2 > 0 || d3 > 0;
				return !(negative && positive);
			});
		}

		// Windows washes a column header with a pale tint of the accent under the pointer, and
		// tracks it only if the theme says it has a hot style -- the classic one says no, so
		// nothing was ever invalidated and the header never lit up.
		private static readonly Color HeaderHotFace = Color.FromArgb (233, 242, 250);

		public override bool ListViewHasHotHeaderStyle => true;

		private void DrawModernHeaderCell (Graphics g, Rectangle area, bool pressed)
		{
			DrawModernHeaderCell (g, area, pressed, true);
		}

		// A data grid rules off its header row; a list view does not, and drawing one there put a
		// line across the top of the list that a stock one has no trace of.
		private void DrawModernHeaderCell (Graphics g, Rectangle area, bool pressed, bool bottomRule)
		{
			DrawModernHeaderCell (g, area, pressed, bottomRule, false);
		}

		private void DrawModernHeaderCell (Graphics g, Rectangle area, bool pressed, bool bottomRule,
					    bool hot)
		{
			if (area.Width <= 0 || area.Height <= 0)
				return;
			// Flat and white, separated by a hairline -- no raised bevel.
			Color face = pressed ? TabItemFace : hot ? HeaderHotFace : ColorWindow;
			g.FillRectangle (ResPool.GetSolidBrush (face), area);
			Pen pen = ResPool.GetPen (HairLine);
			g.DrawLine (pen, area.Right - 1, area.Y + 3, area.Right - 1, area.Bottom - 4);
			if (bottomRule)
				g.DrawLine (pen, area.X, area.Bottom - 1, area.Right - 1, area.Bottom - 1);
		}

		private void DrawModernTabControl (Graphics dc, Rectangle area, TabControl tab)
		{
			dc.FillRectangle (ResPool.GetSolidBrush (tab.BackColor), area);
			if (tab.TabCount == 0)
				return;

			Pen edge = ResPool.GetPen (TabEdge);

			// Where the row of tabs sits. Measured from one that is not on show: the rectangle the
			// control keeps for the selected tab is already nudged outwards, so measuring the row from
			// that one put the body a pixel below the others' feet and left a seam of bare control
			// between the two.
			Rectangle row = tab.GetTabRect (0);
			bool measured = false;
			for (int i = 0; i < tab.TabCount && !measured; i++) {
				if (i == tab.SelectedIndex)
					continue;
				row = tab.GetTabRect (i);
				measured = true;
			}
			if (!measured)
				// Nothing but the tab on show to go by, so take its nudge back off.
				row = new Rectangle (row.X, row.Y + TabRise, row.Width, Math.Max (1, row.Height - TabRise));

			// The body is the whole control below the row of tabs, framed on all four sides, and its top
			// edge is the very line the tabs stand on -- one line, not one each. It is the control's own
			// rectangle and not its display rectangle: the display rectangle is where the page goes,
			// already inset by the padding the frame and its margin take up, so framing that drew the
			// border a couple of pixels in from the edge it belongs on.
			int shoulder = Math.Max (area.Y, row.Bottom - 1);
			var body = new Rectangle (area.X, shoulder, area.Width, Math.Max (0, area.Bottom - shoulder));
			if (body.Width > 0 && body.Height > 0) {
				dc.FillRectangle (ResPool.GetSolidBrush (TabPaneFace), body);
				dc.DrawRectangle (edge, body.X, body.Y, body.Width - 1, body.Height - 1);
			}

			// The others first and the one on show last, so it covers its neighbour's edge rather than
			// being covered by it. It also stands a little taller and opens straight onto the body --
			// no line along its foot -- which is what makes it read as the front of the page rather
			// than another button in the row.
			for (int pass = 0; pass < 2; pass++) {
				for (int i = 0; i < tab.TabCount; i++) {
					bool selected = i == tab.SelectedIndex;
					if (selected != (pass == 1))
						continue;
					Rectangle bounds = tab.GetTabRect (i);
					if (bounds.Width <= 0 || bounds.Height <= 0)
						continue;

					// The one on show stands a little taller and reaches down past the body's edge, so the
					// two run into one another; the rest stand on that edge.
					Rectangle face = selected
						? Rectangle.FromLTRB (bounds.X, row.Y - TabRise, bounds.Right,
									Math.Min (area.Bottom, body.Y + 2))
						: new Rectangle (bounds.X, row.Y, bounds.Width, row.Height);
					if (face.Right > area.Right)
						face.Width = Math.Max (0, area.Right - face.X);
					if (face.Width <= 0)
						continue;

					dc.FillRectangle (ResPool.GetSolidBrush (selected ? TabPaneFace : TabRestFace), face);
					if (selected) {
						dc.DrawLine (edge, face.X, face.Y, face.Right - 1, face.Y);
						dc.DrawLine (edge, face.X, face.Y, face.X, face.Bottom - 1);
						dc.DrawLine (edge, face.Right - 1, face.Y, face.Right - 1, face.Bottom - 1);
					} else {
						dc.DrawRectangle (edge, face.X, face.Y, face.Width - 1, face.Height - 1);
					}

					TabPage page = tab.TabPages[i];
					var format = new StringFormat {
						Alignment = StringAlignment.Center,
						LineAlignment = StringAlignment.Center,
						HotkeyPrefix = System.Drawing.Text.HotkeyPrefix.Show,
						FormatFlags = StringFormatFlags.NoWrap,
					};
					// In the tab's own rectangle rather than the face just drawn, so the label sits at the
					// same height whether or not its tab is the one standing proud.
					Color fore = page.Enabled ? tab.ForeColor : ColorGrayText;
					dc.DrawString (page.Text, tab.Font, ResPool.GetSolidBrush (fore),
						       new Rectangle (bounds.X, row.Y, bounds.Width, row.Height), format);
				}
			}
		}

		// ---- checked list box --------------------------------------------------------

		public override void DrawCheckedListBoxItem (CheckedListBox ctrl, DrawItemEventArgs e)
		{
			// The classic layout puts the box two pixels from the edge and starts the text the
			// instant the box ends, so the tick and the first letter touch. Windows indents the box
			// and leaves a gap after it.
			// Measured against a stock list: the box sits one pixel in from the row, and the row
			// itself already begins inside the frame.
			const int Indent = 1;
			// The text is laid out with a margin of its own -- see Graphics.Overhang -- so the gap
			// asked for here is only what is wanted beyond that.
			const int Gap = 2;

			Rectangle item = e.Bounds;
			bool selected = (e.State & DrawItemState.Selected) == DrawItemState.Selected;
			Color back = selected ? ColorHighlight : e.BackColor;
			Color fore = selected ? ColorHighlightText : e.ForeColor;

			int size = Math.Min (13, Math.Max (0, item.Height - 2));
			var box = new Rectangle (item.X + Indent, item.Y + (item.Height - size) / 2,
						 Math.Max (size - 1, 0), Math.Max (size - 1, 0));

			Rectangle text = item;
			text.X = box.Right + Gap;
			text.Width = Math.Max (0, item.Right - text.X);

			// The row's own background everywhere, and the selection only behind the text.
			// Filling the whole row put the highlight behind the check box as well, where
			// Windows leaves it standing on the control's background.
			e.Graphics.FillRectangle (ResPool.GetSolidBrush (e.BackColor), item);
			if (selected && text.Width > 0)
				e.Graphics.FillRectangle (ResPool.GetSolidBrush (back), text);

			if (box.Width > 0 && box.Height > 0)
				DrawModernCheck (e.Graphics, box,
						 (e.State & DrawItemState.Checked) == DrawItemState.Checked, false,
						 (e.State & DrawItemState.Inactive) != DrawItemState.Inactive, false,
						 e.BackColor);

			if (text.Width > 0)
				e.Graphics.DrawString (ctrl.GetItemText (ctrl.Items[e.Index]), e.Font,
						       ResPool.GetSolidBrush (fore), text, ctrl.StringFormat);

			if ((e.State & DrawItemState.Focus) == DrawItemState.Focus)
				CPDrawFocusRectangle (e.Graphics, text, fore, back);
		}

		// ---- date picker -------------------------------------------------------------

		/// <summary>Windows draws a calendar and a chevron in a date picker's drop-down, and the
		/// glyph is a bitmap in its theme rather than anything derivable from lines and arcs. So it
		/// is extracted once, as the property grid's icons were, and embedded here: Microsoft's
		/// artwork, but a file in our tree, so nothing at run time depends on Windows to draw it.
		/// </summary>
		private static Bitmap s_calendarGlyph;
		private static bool s_calendarGlyphTried;

		private static Bitmap CalendarGlyph {
			get {
				if (!s_calendarGlyphTried) {
					s_calendarGlyphTried = true;
					try { s_calendarGlyph = new Bitmap (typeof (DateTimePicker), "datetimepicker-calendar.png"); }
					catch (Exception) { s_calendarGlyph = null; }
				}
				return s_calendarGlyph;
			}
		}

		/// <summary>Wide enough for the glyph. The classic button is one scroll bar wide, which fits
		/// an arrow and nothing else.</summary>
		// The base theme reserves a scroll bar's width for the button and no more, so widening the
		// button for the calendar glyph left the date area overlapping it -- and the date area is
		// filled after the button is drawn, which painted out all but the last few columns of the
		// glyph.
		public override Rectangle DateTimePickerGetDateArea (DateTimePicker dateTimePicker)
		{
			Rectangle rect = base.DateTimePickerGetDateArea (dateTimePicker);
			if (dateTimePicker.ShowUpDown)
				return rect;
			Rectangle button = DateTimePickerGetDropDownButtonArea (dateTimePicker);
			if (button.Width > 0 && rect.Right > button.X)
				rect.Width = Math.Max (button.X - rect.X, 0);
			return rect;
		}

		/// <summary>The button lights up under the pointer, as a spin button does. The classic theme
		/// draws a 3D button that never changes, so it answered no; ours has a hot face to show.
		/// <summary>Windows centres the date in the field. Pinning it two pixels below the frame
		/// left it riding two pixels high, which is where ours sat against a stock one.</summary>
		public override int DateTimePickerTextTop (DateTimePicker dateTimePicker, int textHeight)
		{
			Rectangle area = DateTimePickerGetDateArea (dateTimePicker);
			return area.Y + Math.Max (0, (area.Height - textHeight) / 2);
		}

		public override bool DateTimePickerDropDownButtonHasHotElementStyle {
			get { return true; }
		}

		/// <summary>Also true, and not for a border of its own: the control repaints when the pointer
		/// arrives and when it leaves ONLY if this says yes. Saying no meant a pointer that left
		/// straight for another control cleared the button's hot flag and never redrew it, so the
		/// highlight stayed behind on a control the pointer was no longer anywhere near.</summary>
		public override bool DateTimePickerBorderHasHotElementStyle {
			get { return true; }
		}

		public override Rectangle DateTimePickerGetDropDownButtonArea (DateTimePicker dateTimePicker)
		{
			Bitmap glyph = CalendarGlyph;
			if (glyph == null)
				return base.DateTimePickerGetDropDownButtonArea (dateTimePicker);
			Rectangle rect = dateTimePicker.ClientRectangle;
			int want = glyph.Width + 8;
			if (rect.Width <= want + 2)
				return base.DateTimePickerGetDropDownButtonArea (dateTimePicker);
			rect.X = rect.Right - want - 2;
			rect.Width = want;
			rect.Inflate (0, -2);
			return rect;
		}

		protected override void DateTimePickerDrawDropDownButton (DateTimePicker dateTimePicker, Graphics g,
										  Rectangle clippingArea)
		{
			Rectangle r = dateTimePicker.drop_down_arrow_rect;
			if (r.Width <= 0 || r.Height <= 0)
				return;

			// The button belongs to the field, so it takes the field's own background -- no chrome of
			// its own until the pointer is on it, and then the same pale wash a spin button takes.
			g.FillRectangle (ResPool.GetSolidBrush (dateTimePicker.Enabled ? ColorWindow : ColorControl), r);
			if (dateTimePicker.is_drop_down_visible)
				g.FillRectangle (ResPool.GetSolidBrush (ComboFieldOpenFace), r);
			else if (dateTimePicker.Enabled && dateTimePicker.DropDownButtonEntered) {
				g.FillRectangle (ResPool.GetSolidBrush (HeaderHotFace), r);
				// And a frame in the accent, which is what tells a hovered button from a merely tinted
				// patch of field.
				g.DrawRectangle (ResPool.GetPen (ButtonBorderHover), r.X, r.Y,
						 Math.Max (0, r.Width - 1), Math.Max (0, r.Height - 1));
			}

			Bitmap glyph = CalendarGlyph;
			if (glyph == null) {
				CPDrawComboButton (g, r, dateTimePicker.is_drop_down_visible ? ButtonState.Pushed : ButtonState.Normal);
				return;
			}
			int x = r.X + (r.Width - glyph.Width) / 2;
			int y = r.Y + (r.Height - glyph.Height) / 2;
			if (dateTimePicker.Enabled)
				g.DrawImage (glyph, x, y);
			else
				CPDrawImageDisabled (g, glyph, x, y, ColorControl);
		}
	}
}
