// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// .NET's ToolStripProfessionalRenderer. Mono's was its own drawing of the same idea -- a checked
// button filled pale blue where .NET's is near-white inside a blue line, the grip's dots in the wrong
// column, the strip's edge a plain line -- so this follows .NET's, with its colour table
// (ProfessionalColorTable). What is left out: the high-contrast and 8-bit renderers .NET swaps in
// (RendererOverride), and per-monitor rescaling of the few pixel constants below, which the port
// draws at 96 dpi.

using System.Drawing;
using System.Drawing.Drawing2D;

namespace System.Windows.Forms
{
	public class ToolStripProfessionalRenderer : ToolStripRenderer
	{
		private const int GripPadding = 4;
		private const int IconWellGradientWidth = 12;
		private const int OverflowButtonWidth = 12;
		private const int OverflowArrowWidth = 9;
		private const int OverflowArrowHeight = 5;
		private const int OverflowArrowOffsetY = 8;
		private const int Offset2X = 2;
		private const int Offset2Y = 2;

		private static readonly Size OnePix = new Size (1, 1);
		private static readonly Padding DropDownMenuItemPaintPadding = new Padding (2, 0, 1, 0);

		private readonly ProfessionalColorTable color_table;
		private bool rounded_edges = true;

		#region Public Constructor
		public ToolStripProfessionalRenderer () : this (new ProfessionalColorTable ())
		{
		}

		public ToolStripProfessionalRenderer (ProfessionalColorTable professionalColorTable) : base ()
		{
			color_table = professionalColorTable;
		}
		#endregion

		#region Public Properties
		public ProfessionalColorTable ColorTable {
			get { return this.color_table; }
		}

		public bool RoundedEdges {
			get { return this.rounded_edges; }
			set { this.rounded_edges = value; }
		}

		/// <summary>.NET's GetTransparentRegion: the pixels at the corners of a rounded tool strip
		/// (not a drop-down, menu or status strip) that show the parent instead.</summary>
		internal Rectangle [] GetTransparentRects (ToolStrip toolStrip)
		{
			if (toolStrip is ToolStripDropDown || toolStrip is MenuStrip || toolStrip is StatusStrip || !RoundedEdges
			    || toolStrip.Parent == null)
				return null;
			var r = new Rectangle (Point.Empty, toolStrip.Size);
			var topRight = new Point (r.Width - 1, 0);
			var bottomLeft = new Point (0, r.Height - 1);
			var bottomRight = new Point (r.Width - 1, r.Height - 1);
			bool overflow = toolStrip.OverflowButton.Visible;
			return new [] {
				new Rectangle (0, 0, 1, 1),
				new Rectangle (bottomLeft, new Size (2, 1)),
				new Rectangle (bottomLeft.X, bottomLeft.Y - 1, 1, 2),
				new Rectangle (bottomRight.X - 1, bottomRight.Y, 2, 1),
				new Rectangle (bottomRight.X, bottomRight.Y - 1, 1, 2),
				overflow ? new Rectangle (topRight.X - 1, topRight.Y, 1, 1) : new Rectangle (topRight.X - 2, topRight.Y, 2, 1),
				overflow ? new Rectangle (topRight.X, topRight.Y, 1, 2) : new Rectangle (topRight.X, topRight.Y, 1, 3),
			};
		}
		#endregion

		private bool UseSystemColors => ColorTable.UseSystemColors || !ToolStripManager.VisualStylesEnabled;

		// ToolStripRenderer.ShouldPaintBackground: no BackColor of the control's own, and no image.
		private static bool ShouldPaintBackground (Control control)
		{
			return !control.ShouldSerializeBackColor () && control.BackgroundImage == null;
		}

		#region Protected Methods
		protected override void OnRenderToolStripBackground (ToolStripRenderEventArgs e)
		{
			ToolStrip toolStrip = e.ToolStrip;
			if (!ShouldPaintBackground (toolStrip))
				return;
			if (toolStrip is ToolStripDropDown)
				RenderToolStripDropDownBackground (e);
			else if (toolStrip is MenuStrip)
				RenderMenuStripBackground (e);
			else if (toolStrip is StatusStrip)
				RenderStatusStripBackground (e);
			else
				RenderToolStripBackgroundInternal (e);
		}

		protected override void OnRenderOverflowButtonBackground (ToolStripItemRenderEventArgs e)
		{
			ToolStripItem item = e.Item;
			Graphics g = e.Graphics;
			bool rtl = item.RightToLeft == RightToLeft.Yes;
			RenderOverflowBackground (e, rtl);
			bool horizontal = e.ToolStrip != null && e.ToolStrip.Orientation == Orientation.Horizontal;
			Rectangle arrow = rtl
				? new Rectangle (0, item.Height - OverflowArrowOffsetY, OverflowArrowWidth, OverflowArrowHeight)
				: new Rectangle (item.Width - OverflowButtonWidth, item.Height - OverflowArrowOffsetY, OverflowArrowWidth, OverflowArrowHeight);
			ArrowDirection direction = horizontal ? ArrowDirection.Down : ArrowDirection.Right;
			int shift = (rtl && horizontal) ? -1 : 1;
			arrow.Offset (shift, 1);
			RenderArrowInternal (g, arrow, direction, SystemBrushes.ButtonHighlight);
			arrow.Offset (-shift, -1);
			Point middle = RenderArrowInternal (g, arrow, direction, SystemBrushes.ControlText);
			if (horizontal) {
				shift = rtl ? -2 : 0;
				g.DrawLine (SystemPens.ControlText, middle.X - Offset2X, arrow.Y - Offset2Y, middle.X + Offset2X, arrow.Y - Offset2Y);
				g.DrawLine (SystemPens.ButtonHighlight, middle.X - Offset2X + 1 + shift, arrow.Y - Offset2Y + 1, middle.X + Offset2X + 1 + shift, arrow.Y - Offset2Y + 1);
			} else {
				g.DrawLine (SystemPens.ControlText, arrow.X, middle.Y - Offset2Y, arrow.X, middle.Y + Offset2Y);
				g.DrawLine (SystemPens.ButtonHighlight, arrow.X + 1, middle.Y - Offset2Y + 1, arrow.X + 1, middle.Y + Offset2Y + 1);
			}
		}

		protected override void OnRenderDropDownButtonBackground (ToolStripItemRenderEventArgs e)
		{
			if (e.Item is ToolStripDropDownItem item && item.Pressed && item.HasDropDownItems)
				RenderPressedGradient (e.Graphics, new Rectangle (Point.Empty, item.Size));
			else
				RenderItemInternal (e, true);
		}

		protected override void OnRenderSeparator (ToolStripSeparatorRenderEventArgs e)
		{
			RenderSeparatorInternal (e.Graphics, e.Item, new Rectangle (Point.Empty, e.Item.Size), e.Vertical);
		}

		protected override void OnRenderSplitButtonBackground (ToolStripItemRenderEventArgs e)
		{
			Graphics g = e.Graphics;
			if (!(e.Item is ToolStripSplitButton split))
				return;
			Rectangle bounds = new Rectangle (Point.Empty, split.Size);
			if (split.BackgroundImage != null) {
				Rectangle clip = split.Selected ? split.ContentRectangle : bounds;
				ControlPaint.DrawBackgroundImage (g, split.BackgroundImage, split.BackColor, split.BackgroundImageLayout, bounds, clip);
			}
			bool hot = split.Pressed || split.ButtonPressed || split.Selected || split.ButtonSelected;
			if (hot)
				RenderItemInternal (e, true);
			if (split.ButtonPressed) {
				Rectangle button = split.ButtonBounds;
				Padding deflate = split.RightToLeft == RightToLeft.Yes ? new Padding (0, 1, 1, 1) : new Padding (1, 1, 0, 1);
				button = new Rectangle (button.X + deflate.Left, button.Y + deflate.Top,
				                        button.Width - deflate.Horizontal, button.Height - deflate.Vertical);
				RenderPressedButtonFill (g, button);
			} else if (split.Pressed) {
				RenderPressedGradient (g, bounds);
			}
			if (hot && !split.Pressed) {
				using (SolidBrush b = new SolidBrush (ColorTable.ButtonSelectedBorder))
					g.FillRectangle (b, split.SplitterBounds);
			}
			DrawArrow (new ToolStripArrowRenderEventArgs (g, split, split.DropDownButtonBounds, SystemColors.ControlText, ArrowDirection.Down));
		}

		protected override void OnRenderToolStripStatusLabelBackground (ToolStripItemRenderEventArgs e)
		{
			RenderLabelInternal (e);
			if (e.Item is ToolStripStatusLabel label)
				ControlPaint.DrawBorder3D (e.Graphics, new Rectangle (0, 0, label.Width, label.Height), label.BorderStyle, (Border3DSide) label.BorderSides);
		}

		protected override void OnRenderLabelBackground (ToolStripItemRenderEventArgs e)
		{
			RenderLabelInternal (e);
		}

		protected override void OnRenderButtonBackground (ToolStripItemRenderEventArgs e)
		{
			ToolStripButton button = e.Item as ToolStripButton;
			Graphics g = e.Graphics;
			Rectangle bounds = new Rectangle (Point.Empty, button?.Size ?? Size.Empty);
			if (button != null && button.CheckState == CheckState.Unchecked) {
				RenderItemInternal (e, true);
				return;
			}
			Rectangle clip = button != null && button.Selected ? button.ContentRectangle : bounds;
			if (button?.BackgroundImage != null)
				ControlPaint.DrawBackgroundImage (g, button.BackgroundImage, button.BackColor, button.BackgroundImageLayout, bounds, clip);
			if (button != null && button.Selected)
				RenderPressedButtonFill (g, bounds);
			else
				RenderCheckedButtonFill (g, bounds);
			using (Pen p = new Pen (ColorTable.ButtonSelectedBorder))
				g.DrawRectangle (p, bounds.X, bounds.Y, bounds.Width - 1, bounds.Height - 1);
		}

		protected override void OnRenderToolStripBorder (ToolStripRenderEventArgs e)
		{
			ToolStrip toolStrip = e.ToolStrip;
			Graphics g = e.Graphics;
			if (toolStrip is ToolStripDropDown) {
				RenderToolStripDropDownBorder (e);
				return;
			}
			if (toolStrip is MenuStrip)
				return;
			if (toolStrip is StatusStrip) {
				RenderStatusStripBorder (e);
				return;
			}
			Rectangle bounds = new Rectangle (Point.Empty, toolStrip.Size);
			using (Pen p = new Pen (ColorTable.ToolStripBorder)) {
				if (toolStrip.Orientation == Orientation.Horizontal) {
					g.DrawLine (p, bounds.Left, bounds.Height - 1, bounds.Right, bounds.Height - 1);
					if (RoundedEdges)
						g.DrawLine (p, bounds.Width - 2, bounds.Height - 2, bounds.Width - 1, bounds.Height - 3);
				} else {
					g.DrawLine (p, bounds.Width - 1, 0, bounds.Width - 1, bounds.Height - 1);
					if (RoundedEdges)
						g.DrawLine (p, bounds.Width - 2, bounds.Height - 2, bounds.Width - 1, bounds.Height - 3);
				}
			}
			if (!RoundedEdges)
				return;
			if (toolStrip.OverflowButton.Visible) {
				RenderOverflowButtonEffectsOverBorder (e);
				return;
			}
			Rectangle edge = toolStrip.Orientation == Orientation.Horizontal
				? new Rectangle (bounds.Width - 1, 3, 1, bounds.Height - 3)
				: new Rectangle (3, bounds.Height - 1, bounds.Width - 3, bounds.Height - 1);
			FillWithDoubleGradient (ColorTable.OverflowButtonGradientBegin, ColorTable.OverflowButtonGradientMiddle, ColorTable.OverflowButtonGradientEnd,
			                        g, edge, IconWellGradientWidth, IconWellGradientWidth, LinearGradientMode.Vertical, false);
			RenderToolStripCurve (e);
		}

		protected override void OnRenderGrip (ToolStripGripRenderEventArgs e)
		{
			Graphics g = e.Graphics;
			Rectangle grip = e.GripBounds;
			ToolStrip toolStrip = e.ToolStrip;
			bool rtl = toolStrip.RightToLeft == RightToLeft.Yes;
			bool horizontal = toolStrip.Orientation == Orientation.Horizontal;
			int length = horizontal ? grip.Height : grip.Width;
			int across = horizontal ? grip.Width : grip.Height;
			int count = (length - GripPadding * 2) / 4;
			if (count <= 0)
				return;
			int start = toolStrip is MenuStrip ? 2 : 0;
			Rectangle[] dots = new Rectangle[count];
			int along = GripPadding + 1 + start;
			int middle = across / 2;
			for (int i = 0; i < count; i++) {
				dots[i] = horizontal ? new Rectangle (middle, along, 2, 2) : new Rectangle (along, middle, 2, 2);
				along += 4;
			}
			int shift = rtl ? 1 : -1;
			if (rtl)
				for (int i = 0; i < count; i++)
					dots[i].Offset (-shift, 0);
			using (SolidBrush light = new SolidBrush (ColorTable.GripLight))
				g.FillRectangles (light, dots);
			for (int i = 0; i < count; i++)
				dots[i].Offset (shift, -1);
			using (SolidBrush dark = new SolidBrush (SystemColors.ControlText))
				g.FillRectangles (dark, dots);
		}

		protected override void OnRenderMenuItemBackground (ToolStripItemRenderEventArgs e)
		{
			ToolStripItem item = e.Item;
			Graphics g = e.Graphics;
			Rectangle bounds = new Rectangle (Point.Empty, item.Size);
			if (bounds.Width == 0 || bounds.Height == 0 || item is MdiControlStrip.SystemMenuItem)
				return;
			if (item.IsOnDropDown) {
				bounds = Deflate (bounds, DropDownMenuItemPaintPadding);
				if (item.Selected) {
					Color border = ColorTable.MenuItemBorder;
					if (item.Enabled) {
						if (UseSystemColors) {
							border = SystemColors.Highlight;
							RenderSelectedButtonFill (g, bounds);
						} else {
							using (Brush b = new LinearGradientBrush (bounds, ColorTable.MenuItemSelectedGradientBegin, ColorTable.MenuItemSelectedGradientEnd, LinearGradientMode.Vertical))
								g.FillRectangle (b, bounds);
						}
					}
					using (Pen p = new Pen (border))
						g.DrawRectangle (p, bounds.X, bounds.Y, bounds.Width - 1, bounds.Height - 1);
					return;
				}
				if (item.BackgroundImage != null) {
					ControlPaint.DrawBackgroundImage (g, item.BackgroundImage, item.BackColor, item.BackgroundImageLayout, bounds, bounds);
				} else if (item.Owner != null && item.BackColor != item.Owner.BackColor) {
					using (SolidBrush b = new SolidBrush (item.BackColor))
						g.FillRectangle (b, bounds);
				}
				return;
			}
			if (item.Pressed) {
				RenderPressedGradient (g, bounds);
				return;
			}
			if (item.Selected) {
				Color border = ColorTable.MenuItemBorder;
				if (item.Enabled) {
					if (UseSystemColors) {
						border = SystemColors.Highlight;
						RenderSelectedButtonFill (g, bounds);
					} else {
						using (Brush b = new LinearGradientBrush (bounds, ColorTable.MenuItemSelectedGradientBegin, ColorTable.MenuItemSelectedGradientEnd, LinearGradientMode.Vertical))
							g.FillRectangle (b, bounds);
					}
				}
				using (Pen p = new Pen (border))
					g.DrawRectangle (p, bounds.X, bounds.Y, bounds.Width - 1, bounds.Height - 1);
				return;
			}
			if (item.BackgroundImage != null) {
				ControlPaint.DrawBackgroundImage (g, item.BackgroundImage, item.BackColor, item.BackgroundImageLayout, bounds, bounds);
				return;
			}
			if (item.Owner != null && item.BackColor != item.Owner.BackColor) {
				using (SolidBrush b = new SolidBrush (item.BackColor))
					g.FillRectangle (b, bounds);
				return;
			}
			if (item is ToolStripMenuItem menuItem && menuItem.CheckState == CheckState.Checked) {
				using (Pen p = new Pen (ColorTable.MenuItemBorder))
					g.DrawRectangle (p, bounds.X, bounds.Y, bounds.Width - 1, bounds.Height - 1);
			}
		}

		protected override void OnRenderArrow (ToolStripArrowRenderEventArgs e)
		{
			if (e.Item is ToolStripDropDownItem)
				e.ArrowColor = e.Item.Enabled ? SystemColors.ControlText : SystemColors.ControlDark;
			base.OnRenderArrow (e);
		}

		protected override void OnRenderImageMargin (ToolStripRenderEventArgs e)
		{
			Rectangle bounds = e.AffectedBounds;
			bounds.Y += 2;
			bounds.Height -= 4;
			bool rtl = e.ToolStrip.RightToLeft == RightToLeft.Yes;
			Color begin = rtl ? ColorTable.ImageMarginGradientEnd : ColorTable.ImageMarginGradientBegin;
			Color end = rtl ? ColorTable.ImageMarginGradientBegin : ColorTable.ImageMarginGradientEnd;
			FillWithDoubleGradient (begin, ColorTable.ImageMarginGradientMiddle, end, e.Graphics, bounds,
			                        IconWellGradientWidth, IconWellGradientWidth, LinearGradientMode.Horizontal, rtl);
		}

		protected override void OnRenderItemText (ToolStripItemTextRenderEventArgs e)
		{
			if (e.Item is ToolStripMenuItem && (e.Item.Selected || e.Item.Pressed))
				e.TextColor = e.Item.ForeColor;
			base.OnRenderItemText (e);
		}

		protected override void OnRenderItemCheck (ToolStripItemImageRenderEventArgs e)
		{
			RenderCheckBackground (e);
			base.OnRenderItemCheck (e);
		}

		protected override void OnRenderItemImage (ToolStripItemImageRenderEventArgs e)
		{
			Rectangle rect = e.ImageRectangle;
			Image image = e.Image;
			if (e.Item is ToolStripMenuItem menuItem && menuItem.CheckState != CheckState.Unchecked
			    && menuItem.GetCurrentParent () is ToolStripDropDownMenu menu && !menu.ShowCheckMargin && menu.ShowImageMargin)
				RenderCheckBackground (e);
			if (rect == Rectangle.Empty || image == null)
				return;
			if (!e.Item.Enabled)
				base.OnRenderItemImage (e);
			else if (e.Item.ImageScaling == ToolStripItemImageScaling.None)
				e.Graphics.DrawImage (image, rect, new Rectangle (Point.Empty, rect.Size), GraphicsUnit.Pixel);
			else
				e.Graphics.DrawImage (image, rect);
		}

		protected override void OnRenderToolStripPanelBackground (ToolStripPanelRenderEventArgs e)
		{
			ToolStripPanel panel = e.ToolStripPanel;
			if (!ShouldPaintBackground (panel))
				return;
			e.Handled = true;
			RenderBackgroundGradient (e.Graphics, panel, ColorTable.ToolStripPanelGradientBegin, ColorTable.ToolStripPanelGradientEnd, Orientation.Horizontal);
		}

		protected override void OnRenderToolStripContentPanelBackground (ToolStripContentPanelRenderEventArgs e)
		{
			if (!ShouldPaintBackground (e.ToolStripContentPanel))
				return;
			e.Handled = true;
			e.Graphics.Clear (ColorTable.ToolStripContentPanelGradientEnd);
		}
		#endregion

		#region Private Methods
		private static Rectangle Deflate (Rectangle r, Padding p)
		{
			return new Rectangle (r.X + p.Left, r.Y + p.Top, r.Width - p.Horizontal, r.Height - p.Vertical);
		}

		private void RenderOverflowButtonEffectsOverBorder (ToolStripRenderEventArgs e)
		{
			ToolStrip toolStrip = e.ToolStrip;
			ToolStripItem overflow = toolStrip.OverflowButton;
			if (!overflow.Visible)
				return;
			Graphics g = e.Graphics;
			Color corner, top;
			if (overflow.Pressed) {
				corner = top = ColorTable.ButtonPressedGradientBegin;
			} else if (overflow.Selected) {
				corner = top = ColorTable.ButtonSelectedGradientMiddle;
			} else {
				corner = ColorTable.ToolStripBorder;
				top = ColorTable.ToolStripGradientMiddle;
			}
			using (SolidBrush b = new SolidBrush (corner)) {
				g.FillRectangle (b, toolStrip.Width - 1, toolStrip.Height - 2, 1, 1);
				g.FillRectangle (b, toolStrip.Width - 2, toolStrip.Height - 1, 1, 1);
			}
			using (SolidBrush b = new SolidBrush (top)) {
				g.FillRectangle (b, toolStrip.Width - 2, 0, 1, 1);
				g.FillRectangle (b, toolStrip.Width - 1, 1, 1, 1);
			}
		}

		private static void FillWithDoubleGradient (Color beginColor, Color middleColor, Color endColor, Graphics g, Rectangle bounds,
		                                            int firstGradientWidth, int secondGradientWidth, LinearGradientMode mode, bool flipHorizontal)
		{
			if (bounds.Width == 0 || bounds.Height == 0)
				return;
			Rectangle end = bounds;
			Rectangle begin = bounds;
			bool roomForMiddle;
			if (mode == LinearGradientMode.Horizontal) {
				if (flipHorizontal) {
					Color c = endColor;
					endColor = beginColor;
					beginColor = c;
				}
				begin.Width = firstGradientWidth;
				end.Width = secondGradientWidth + 1;
				end.X = bounds.Right - end.Width;
				roomForMiddle = bounds.Width > firstGradientWidth + secondGradientWidth;
			} else {
				begin.Height = firstGradientWidth;
				end.Height = secondGradientWidth + 1;
				end.Y = bounds.Bottom - end.Height;
				roomForMiddle = bounds.Height > firstGradientWidth + secondGradientWidth;
			}
			if (roomForMiddle) {
				using (SolidBrush b = new SolidBrush (middleColor))
					g.FillRectangle (b, bounds);
				using (Brush b = new LinearGradientBrush (begin, beginColor, middleColor, mode))
					g.FillRectangle (b, begin);
				using (LinearGradientBrush b = new LinearGradientBrush (end, middleColor, endColor, mode)) {
					if (mode == LinearGradientMode.Horizontal) {
						end.X++;
						end.Width--;
					} else {
						end.Y++;
						end.Height--;
					}
					g.FillRectangle (b, end);
				}
				return;
			}
			using (Brush b = new LinearGradientBrush (bounds, beginColor, endColor, mode))
				g.FillRectangle (b, bounds);
		}

		private void RenderStatusStripBorder (ToolStripRenderEventArgs e)
		{
			using (Pen p = new Pen (ColorTable.StatusStripBorder))
				e.Graphics.DrawLine (p, 0, 0, e.ToolStrip.Width, 0);
		}

		private void RenderStatusStripBackground (ToolStripRenderEventArgs e)
		{
			if (e.ToolStrip is StatusStrip status)
				RenderBackgroundGradient (e.Graphics, status, ColorTable.StatusStripGradientBegin, ColorTable.StatusStripGradientEnd, status.Orientation);
		}

		private void RenderCheckBackground (ToolStripItemImageRenderEventArgs e)
		{
			Rectangle rect = new Rectangle (e.ImageRectangle.Left - 2, 1, e.ImageRectangle.Width + 4, e.Item.Height - 2);
			Graphics g = e.Graphics;
			if (!UseSystemColors) {
				Color c = e.Item.Selected ? ColorTable.CheckSelectedBackground : ColorTable.CheckBackground;
				c = e.Item.Pressed ? ColorTable.CheckPressedBackground : c;
				using (SolidBrush b = new SolidBrush (c))
					g.FillRectangle (b, rect);
				using (Pen p = new Pen (ColorTable.ButtonSelectedBorder))
					g.DrawRectangle (p, rect.X, rect.Y, rect.Width - 1, rect.Height - 1);
				return;
			}
			if (e.Item.Pressed)
				RenderPressedButtonFill (g, rect);
			else
				RenderSelectedButtonFill (g, rect);
			g.DrawRectangle (SystemPens.Highlight, rect.X, rect.Y, rect.Width - 1, rect.Height - 1);
		}

		private void RenderPressedGradient (Graphics g, Rectangle bounds)
		{
			if (bounds.Width == 0 || bounds.Height == 0)
				return;
			using (Brush b = new LinearGradientBrush (bounds, ColorTable.MenuItemPressedGradientBegin, ColorTable.MenuItemPressedGradientEnd, LinearGradientMode.Vertical))
				g.FillRectangle (b, bounds);
			using (Pen p = new Pen (ColorTable.MenuBorder))
				g.DrawRectangle (p, bounds.X, bounds.Y, bounds.Width - 1, bounds.Height - 1);
		}

		private void RenderMenuStripBackground (ToolStripRenderEventArgs e)
		{
			RenderBackgroundGradient (e.Graphics, e.ToolStrip, ColorTable.MenuStripGradientBegin, ColorTable.MenuStripGradientEnd, e.ToolStrip.Orientation);
		}

		private static void RenderLabelInternal (ToolStripItemRenderEventArgs e)
		{
			ToolStripItem item = e.Item;
			Rectangle bounds = new Rectangle (Point.Empty, item.Size);
			Rectangle clip = item.Selected ? item.ContentRectangle : bounds;
			if (item.BackgroundImage != null)
				ControlPaint.DrawBackgroundImage (e.Graphics, item.BackgroundImage, item.BackColor, item.BackgroundImageLayout, bounds, clip);
		}

		// A horizontal strip is shaded as a slice of one gradient across its PARENT, so strips side by
		// side continue each other's ramp.
		private static void RenderBackgroundGradient (Graphics g, Control control, Color beginColor, Color endColor, Orientation orientation)
		{
			if (control.RightToLeft == RightToLeft.Yes) {
				Color c = beginColor;
				beginColor = endColor;
				endColor = c;
			}
			if (orientation != Orientation.Horizontal) {
				using (SolidBrush b = new SolidBrush (beginColor))
					g.FillRectangle (b, new Rectangle (Point.Empty, control.Size));
				return;
			}
			Control parent = control.Parent;
			if (parent != null) {
				Rectangle whole = new Rectangle (Point.Empty, parent.Size);
				if (whole.Width <= 0 || whole.Height <= 0)
					return;
				using (LinearGradientBrush b = new LinearGradientBrush (whole, beginColor, endColor, LinearGradientMode.Horizontal)) {
					b.TranslateTransform (parent.Width - control.Location.X, parent.Height - control.Location.Y);
					g.FillRectangle (b, new Rectangle (Point.Empty, control.Size));
				}
				return;
			}
			Rectangle own = new Rectangle (Point.Empty, control.Size);
			if (own.Width <= 0 || own.Height <= 0)
				return;
			using (LinearGradientBrush b = new LinearGradientBrush (own, beginColor, endColor, LinearGradientMode.Horizontal))
				g.FillRectangle (b, own);
		}

		private void RenderToolStripBackgroundInternal (ToolStripRenderEventArgs e)
		{
			ToolStrip toolStrip = e.ToolStrip;
			Rectangle bounds = new Rectangle (Point.Empty, toolStrip.Size);
			LinearGradientMode mode = toolStrip.Orientation == Orientation.Horizontal ? LinearGradientMode.Vertical : LinearGradientMode.Horizontal;
			FillWithDoubleGradient (ColorTable.ToolStripGradientBegin, ColorTable.ToolStripGradientMiddle, ColorTable.ToolStripGradientEnd,
			                        e.Graphics, bounds, IconWellGradientWidth, IconWellGradientWidth, mode, false);
		}

		private void RenderToolStripDropDownBackground (ToolStripRenderEventArgs e)
		{
			using (SolidBrush b = new SolidBrush (ColorTable.ToolStripDropDownBackground))
				e.Graphics.FillRectangle (b, new Rectangle (Point.Empty, e.ToolStrip.Size));
		}

		private void RenderToolStripDropDownBorder (ToolStripRenderEventArgs e)
		{
			if (!(e.ToolStrip is ToolStripDropDown dropDown))
				return;
			Graphics g = e.Graphics;
			Rectangle bounds = new Rectangle (Point.Empty, dropDown.Size);
			using (Pen p = new Pen (ColorTable.MenuBorder))
				g.DrawRectangle (p, bounds.X, bounds.Y, bounds.Width - 1, bounds.Height - 1);
			if (!(dropDown is ToolStripOverflow)) {
				using (SolidBrush b = new SolidBrush (ColorTable.ToolStripDropDownBackground))
					g.FillRectangle (b, e.ConnectedArea);
			}
		}

		private void RenderOverflowBackground (ToolStripItemRenderEventArgs e, bool rightToLeft)
		{
			Graphics g = e.Graphics;
			ToolStripOverflowButton overflow = e.Item as ToolStripOverflowButton;
			Rectangle bounds = new Rectangle (Point.Empty, e.Item.Size);
			Rectangle within = bounds;
			bool rounded = RoundedEdges && !(overflow?.GetCurrentParent () is MenuStrip);
			bool horizontal = e.ToolStrip != null && e.ToolStrip.Orientation == Orientation.Horizontal;
			if (horizontal) {
				bounds.X += bounds.Width - OverflowButtonWidth + 1;
				bounds.Width = OverflowButtonWidth;
				if (rightToLeft)
					bounds.X = within.Right - bounds.Right + within.X;
			} else {
				bounds.Y = bounds.Height - OverflowButtonWidth + 1;
				bounds.Height = OverflowButtonWidth;
			}
			Color begin, middle, end, cornerLine, corner;
			if (overflow != null && overflow.Pressed) {
				begin = ColorTable.ButtonPressedGradientBegin;
				middle = ColorTable.ButtonPressedGradientMiddle;
				end = ColorTable.ButtonPressedGradientEnd;
				cornerLine = corner = ColorTable.ButtonPressedGradientBegin;
			} else if (overflow != null && overflow.Selected) {
				begin = ColorTable.ButtonSelectedGradientBegin;
				middle = ColorTable.ButtonSelectedGradientMiddle;
				end = ColorTable.ButtonSelectedGradientEnd;
				cornerLine = corner = ColorTable.ButtonSelectedGradientMiddle;
			} else {
				begin = ColorTable.OverflowButtonGradientBegin;
				middle = ColorTable.OverflowButtonGradientMiddle;
				end = ColorTable.OverflowButtonGradientEnd;
				cornerLine = ColorTable.ToolStripBorder;
				corner = horizontal ? ColorTable.ToolStripGradientMiddle : ColorTable.ToolStripGradientEnd;
			}
			if (rounded) {
				using (Pen p = new Pen (cornerLine)) {
					Point a = new Point (bounds.Left - 1, bounds.Height - 2);
					Point b = new Point (bounds.Left, bounds.Height - 2);
					if (rightToLeft) {
						a.X = bounds.Right + 1;
						b.X = bounds.Right;
					}
					g.DrawLine (p, a, b);
				}
			}
			FillWithDoubleGradient (begin, middle, end, g, bounds, IconWellGradientWidth, IconWellGradientWidth,
			                        horizontal ? LinearGradientMode.Vertical : LinearGradientMode.Horizontal, false);
			if (!rounded)
				return;
			using (SolidBrush b = new SolidBrush (corner)) {
				if (horizontal) {
					Point a = new Point (bounds.X - 2, 0);
					Point c = new Point (bounds.X - 1, 1);
					if (rightToLeft) {
						a.X = bounds.Right + 1;
						c.X = bounds.Right;
					}
					g.FillRectangle (b, a.X, a.Y, 1, 1);
					g.FillRectangle (b, c.X, c.Y, 1, 1);
				} else {
					g.FillRectangle (b, bounds.Width - 3, bounds.Top - 1, 1, 1);
					g.FillRectangle (b, bounds.Width - 2, bounds.Top - 2, 1, 1);
				}
			}
			using (SolidBrush b = new SolidBrush (begin)) {
				if (horizontal) {
					Rectangle r = new Rectangle (bounds.X - 1, 0, 1, 1);
					if (rightToLeft)
						r.X = bounds.Right;
					g.FillRectangle (b, r);
				} else {
					g.FillRectangle (b, bounds.X, bounds.Top - 1, 1, 1);
				}
			}
		}

		// The strip's corners are softened by painting a pixel or two of its own shading over them,
		// wherever the item area does not reach.
		private void RenderToolStripCurve (ToolStripRenderEventArgs e)
		{
			Rectangle bounds = new Rectangle (Point.Empty, e.ToolStrip.Size);
			Rectangle display = e.ToolStrip.DisplayRectangle;
			Graphics g = e.Graphics;
			Point topRight = new Point (bounds.Width - 1, 0);
			Point bottomLeft = new Point (0, bounds.Height - 1);
			using (SolidBrush b = new SolidBrush (ColorTable.ToolStripGradientMiddle)) {
				Rectangle r1 = new Rectangle (Point.Empty, OnePix);
				r1.X++;
				Rectangle r2 = new Rectangle (Point.Empty, OnePix);
				r2.Y++;
				Rectangle r3 = new Rectangle (topRight, OnePix);
				r3.X -= 2;
				Rectangle r4 = r3;
				r4.Y++;
				r4.X++;
				foreach (Rectangle r in new[] { r1, r2, r3, r4 })
					if (!display.IntersectsWith (r))
						g.FillRectangle (b, r);
			}
			using (SolidBrush b = new SolidBrush (ColorTable.ToolStripGradientEnd)) {
				Point p = bottomLeft;
				p.Offset (1, -1);
				if (!display.Contains (p))
					g.FillRectangle (b, new Rectangle (p, OnePix));
				Rectangle r = new Rectangle (bottomLeft.X, bottomLeft.Y - 2, 1, 1);
				if (!display.IntersectsWith (r))
					g.FillRectangle (b, r);
			}
		}

		private void RenderSelectedButtonFill (Graphics g, Rectangle bounds)
		{
			if (bounds.Width == 0 || bounds.Height == 0)
				return;
			if (!UseSystemColors) {
				using (Brush b = new LinearGradientBrush (bounds, ColorTable.ButtonSelectedGradientBegin, ColorTable.ButtonSelectedGradientEnd, LinearGradientMode.Vertical))
					g.FillRectangle (b, bounds);
				return;
			}
			using (SolidBrush b = new SolidBrush (ColorTable.ButtonSelectedHighlight))
				g.FillRectangle (b, bounds);
		}

		private void RenderCheckedButtonFill (Graphics g, Rectangle bounds)
		{
			if (bounds.Width == 0 || bounds.Height == 0)
				return;
			if (!UseSystemColors) {
				using (Brush b = new LinearGradientBrush (bounds, ColorTable.ButtonCheckedGradientBegin, ColorTable.ButtonCheckedGradientEnd, LinearGradientMode.Vertical))
					g.FillRectangle (b, bounds);
				return;
			}
			using (SolidBrush b = new SolidBrush (ColorTable.ButtonCheckedHighlight))
				g.FillRectangle (b, bounds);
		}

		private void RenderSeparatorInternal (Graphics g, ToolStripItem item, Rectangle bounds, bool vertical)
		{
			bool isSeparator = item is ToolStripSeparator;
			bool standalone = false;
			if (isSeparator) {
				if (vertical) {
					if (!item.IsOnDropDown) {
						bounds.Y += 3;
						bounds.Height = Math.Max (0, bounds.Height - 6);
					}
				} else if (item.GetCurrentParent () is ToolStripDropDownMenu menu) {
					if (menu.RightToLeft == RightToLeft.No) {
						bounds.X += menu.Padding.Left - 2;
						bounds.Width = menu.Width - bounds.X;
					} else {
						bounds.X += 2;
						bounds.Width = menu.Width - bounds.X - menu.Padding.Right;
					}
				} else {
					standalone = true;
				}
			}
			using (Pen dark = new Pen (ColorTable.SeparatorDark))
			using (Pen light = new Pen (ColorTable.SeparatorLight)) {
				if (vertical) {
					if (bounds.Height >= 4)
						bounds.Inflate (0, -2);
					bool rtl = item.RightToLeft == RightToLeft.Yes;
					Pen first = rtl ? light : dark;
					Pen second = rtl ? dark : light;
					int x = bounds.Width / 2;
					g.DrawLine (first, x, bounds.Top, x, bounds.Bottom - 1);
					x++;
					g.DrawLine (second, x, bounds.Top + 1, x, bounds.Bottom);
				} else {
					if (standalone && bounds.Width >= 4)
						bounds.Inflate (-2, 0);
					int y = bounds.Height / 2;
					g.DrawLine (dark, bounds.Left, y, bounds.Right - 1, y);
					if (!isSeparator || standalone) {
						y++;
						g.DrawLine (light, bounds.Left + 1, y, bounds.Right - 1, y);
					}
				}
			}
		}

		private void RenderPressedButtonFill (Graphics g, Rectangle bounds)
		{
			if (bounds.Width == 0 || bounds.Height == 0)
				return;
			if (!UseSystemColors) {
				using (Brush b = new LinearGradientBrush (bounds, ColorTable.ButtonPressedGradientBegin, ColorTable.ButtonPressedGradientEnd, LinearGradientMode.Vertical))
					g.FillRectangle (b, bounds);
				return;
			}
			using (SolidBrush b = new SolidBrush (ColorTable.ButtonPressedHighlight))
				g.FillRectangle (b, bounds);
		}

		private void RenderItemInternal (ToolStripItemRenderEventArgs e, bool useHotBorder)
		{
			Graphics g = e.Graphics;
			ToolStripItem item = e.Item;
			Rectangle bounds = new Rectangle (Point.Empty, item.Size);
			bool border = false;
			Rectangle clip = item.Selected ? item.ContentRectangle : bounds;
			if (item.BackgroundImage != null)
				ControlPaint.DrawBackgroundImage (g, item.BackgroundImage, item.BackColor, item.BackgroundImageLayout, bounds, clip);
			if (item.Pressed) {
				RenderPressedButtonFill (g, bounds);
				border = useHotBorder;
			} else if (item.Selected) {
				RenderSelectedButtonFill (g, bounds);
				border = useHotBorder;
			} else if (item.Owner != null && item.BackColor != item.Owner.BackColor) {
				using (SolidBrush b = new SolidBrush (item.BackColor))
					g.FillRectangle (b, bounds);
			}
			if (border) {
				using (Pen p = new Pen (ColorTable.ButtonSelectedBorder))
					g.DrawRectangle (p, bounds.X, bounds.Y, bounds.Width - 1, bounds.Height - 1);
			}
		}

		private static Point RenderArrowInternal (Graphics g, Rectangle dropDownRect, ArrowDirection direction, Brush brush)
		{
			Point middle = new Point (dropDownRect.Left + dropDownRect.Width / 2, dropDownRect.Top + dropDownRect.Height / 2);
			middle.X += dropDownRect.Width % 2;
			Point[] arrow;
			switch (direction) {
			case ArrowDirection.Up:
				arrow = new[] { new Point (middle.X - Offset2X, middle.Y + 1), new Point (middle.X + Offset2X + 1, middle.Y + 1), new Point (middle.X, middle.Y - Offset2Y) };
				break;
			case ArrowDirection.Left:
				arrow = new[] { new Point (middle.X + Offset2X, middle.Y - Offset2Y - 1), new Point (middle.X + Offset2X, middle.Y + Offset2Y + 1), new Point (middle.X - 1, middle.Y) };
				break;
			case ArrowDirection.Right:
				arrow = new[] { new Point (middle.X - Offset2X, middle.Y - Offset2Y - 1), new Point (middle.X - Offset2X, middle.Y + Offset2Y + 1), new Point (middle.X + 1, middle.Y) };
				break;
			default:
				arrow = new[] { new Point (middle.X - Offset2X, middle.Y - 1), new Point (middle.X + Offset2X + 1, middle.Y - 1), new Point (middle.X, middle.Y + Offset2Y) };
				break;
			}
			g.FillPolygon (brush, arrow);
			return middle;
		}
		#endregion
	}
}
