//
// ToolStripRenderer.cs
//
// Permission is hereby granted, free of charge, to any person obtaining
// a copy of this software and associated documentation files (the
// "Software"), to deal in the Software without restriction, including
// without limitation the rights to use, copy, modify, merge, publish,
// distribute, sublicense, and/or sell copies of the Software, and to
// permit persons to whom the Software is furnished to do so, subject to
// the following conditions:
// 
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
// MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
// LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
// OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
// WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
//
// Copyright (c) Jonathan Pobst
//
// Authors:
//	Jonathan Pobst (monkey@jpobst.com)
//

using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Drawing2D;

namespace System.Windows.Forms
{
	public abstract class ToolStripRenderer
	{
		// .NET's DisabledImageColorMatrix (light mode): MultiplyColorMatrix(diag(1, 1, 1, 0.7, 0),
		// luminance-with-a-0.38-lift), which computes the SECOND times the first -- luminance,
		// the 0.38 lift kept, alpha at 70%.
		private static ColorMatrix grayscale_matrix = new ColorMatrix (new float[][] {
					  new float[] {0.2125f, 0.2125f, 0.2125f, 0, 0},
					  new float[] {0.2577f, 0.2577f, 0.2577f, 0, 0},
					  new float[] {0.0361f, 0.0361f, 0.0361f, 0, 0},
					  new float[] {0, 0, 0, 0.7f, 0},
					  new float[] {0.38f, 0.38f, 0.38f, 0, 0}
				  });

		protected ToolStripRenderer () 
		{
		}

		#region Public Methods
		public static Image CreateDisabledImage(Image normalImage)
		{
			if (normalImage == null)
				return null;
				
			// Code adapted from ThemeWin32Classic.cs
			ImageAttributes ia = new ImageAttributes();
			ia.SetColorMatrix (grayscale_matrix);
			
			Bitmap b = new Bitmap(normalImage.Width, normalImage.Height);
			using (Graphics g = Graphics.FromImage(b))
				g.DrawImage(normalImage, new Rectangle (0, 0, normalImage.Width, normalImage.Height), 0, 0, normalImage.Width, normalImage.Height, GraphicsUnit.Pixel, ia);
			
			return b;
		}
		
		public void DrawArrow (ToolStripArrowRenderEventArgs e)
		{ this.OnRenderArrow (e); }
		
		public void DrawButtonBackground (ToolStripItemRenderEventArgs e)
		{ this.OnRenderButtonBackground (e); }

		public void DrawDropDownButtonBackground (ToolStripItemRenderEventArgs e)
		{ this.OnRenderDropDownButtonBackground (e); }

		public void DrawGrip (ToolStripGripRenderEventArgs e)
		{ this.OnRenderGrip (e); }

		public void DrawImageMargin (ToolStripRenderEventArgs e)
		{ this.OnRenderImageMargin (e); }

		public void DrawItemBackground (ToolStripItemRenderEventArgs e)
		{ this.OnRenderItemBackground (e); }

		public void DrawItemCheck (ToolStripItemImageRenderEventArgs e)
		{ this.OnRenderItemCheck (e); }

		public void DrawItemImage (ToolStripItemImageRenderEventArgs e)
		{ this.OnRenderItemImage (e); }

		public void DrawItemText (ToolStripItemTextRenderEventArgs e)
		{ this.OnRenderItemText (e); }

		public void DrawLabelBackground (ToolStripItemRenderEventArgs e)
		{ this.OnRenderLabelBackground (e); }

		public void DrawMenuItemBackground (ToolStripItemRenderEventArgs e)
		{ this.OnRenderMenuItemBackground (e); }

		public void DrawOverflowButtonBackground (ToolStripItemRenderEventArgs e)
		{ this.OnRenderOverflowButtonBackground (e); }

		public void DrawSeparator (ToolStripSeparatorRenderEventArgs e)
		{ this.OnRenderSeparator (e); }

		public void DrawSplitButton (ToolStripItemRenderEventArgs e)
		{ this.OnRenderSplitButtonBackground (e); }

		public void DrawStatusStripSizingGrip (ToolStripRenderEventArgs e)
		{ this.OnRenderStatusStripSizingGrip (e); }

		public void DrawToolStripBackground (ToolStripRenderEventArgs e)
		{ this.OnRenderToolStripBackground (e); }

		public void DrawToolStripBorder (ToolStripRenderEventArgs e)
		{ this.OnRenderToolStripBorder (e); }

		public void DrawToolStripContentPanelBackground (ToolStripContentPanelRenderEventArgs e)
		{ this.OnRenderToolStripContentPanelBackground (e); }

		public void DrawToolStripPanelBackground (ToolStripPanelRenderEventArgs e)
		{ this.OnRenderToolStripPanelBackground (e); }

		public void DrawToolStripStatusLabelBackground (ToolStripItemRenderEventArgs e)
		{ this.OnRenderToolStripStatusLabelBackground (e); }
		#endregion

		#region Protected Methods
		protected internal virtual void Initialize (ToolStrip toolStrip) {}
		protected internal virtual void InitializeContentPanel (ToolStripContentPanel contentPanel) {}
		protected internal virtual void InitializeItem (ToolStripItem item) {}
		protected internal virtual void InitializePanel (ToolStripPanel toolStripPanel) {}

		protected virtual void OnRenderArrow (ToolStripArrowRenderEventArgs e)
		{
			// .NET's RenderArrowCore at 96 dpi: a filled triangle about the rectangle's centre.
			Rectangle r = e.ArrowRectangle;
			Point c = new Point (r.Left + r.Width / 2, r.Top + r.Height / 2);
			const int offset2X = 2, offset2Y = 2, offset4Y = 4, tip = 2;
			Point[] arrow;
			switch (e.Direction) {
				case ArrowDirection.Up:
					arrow = new[] { new Point (c.X - offset2X, c.Y + 1), new Point (c.X + offset2X + 1, c.Y + 1), new Point (c.X, c.Y - offset2Y) };
					break;
				case ArrowDirection.Left:
					arrow = new[] { new Point (c.X + offset2X, c.Y - offset4Y), new Point (c.X + offset2X, c.Y + offset4Y), new Point (c.X - tip, c.Y) };
					break;
				case ArrowDirection.Right:
					arrow = new[] { new Point (c.X - offset2X, c.Y - offset4Y), new Point (c.X - offset2X, c.Y + offset4Y), new Point (c.X + tip, c.Y) };
					break;
				default:
					arrow = new[] { new Point (c.X - offset2X, c.Y - 1), new Point (c.X + offset2X + 1, c.Y - 1), new Point (c.X, c.Y + offset2Y) };
					break;
			}
			using (SolidBrush b = new SolidBrush (e.ArrowColor))
				e.Graphics.FillPolygon (b, arrow);

			ToolStripArrowRenderEventHandler eh = (ToolStripArrowRenderEventHandler)Events[RenderArrowEvent];
			if (eh != null)
				eh (this, e);
		}

		protected virtual void OnRenderButtonBackground (ToolStripItemRenderEventArgs e)
		{
			ToolStripItemRenderEventHandler eh = (ToolStripItemRenderEventHandler)Events [RenderButtonBackgroundEvent];
			if (eh != null)
				eh (this, e);
		}

		protected virtual void OnRenderDropDownButtonBackground (ToolStripItemRenderEventArgs e)
		{
			ToolStripItemRenderEventHandler eh = (ToolStripItemRenderEventHandler)Events [RenderDropDownButtonBackgroundEvent];
			if (eh != null)
				eh (this, e);
		}
		
		protected virtual void OnRenderGrip (ToolStripGripRenderEventArgs e)
		{
			ToolStripGripRenderEventHandler eh = (ToolStripGripRenderEventHandler)Events [RenderGripEvent];
			if (eh != null)
				eh (this, e);
		}

		protected virtual void OnRenderImageMargin (ToolStripRenderEventArgs e)
		{
			ToolStripRenderEventHandler eh = (ToolStripRenderEventHandler)Events [RenderImageMarginEvent];
			if (eh != null)
				eh (this, e);
		}

		protected virtual void OnRenderItemBackground (ToolStripItemRenderEventArgs e)
		{
			if (e.Item.BackColor != Control.DefaultBackColor) {
				// Only paint the BackColor if it's not the default one,
				// to avoid painting a solid background color over the parent ToolStrip gradient.
				Rectangle item_bounds = new Rectangle (0, 0, e.Item.Width, e.Item.Height);
				e.Graphics.FillRectangle (ThemeEngine.Current.ResPool.GetSolidBrush (e.Item.BackColor), item_bounds);
			}

			if (e.Item.BackgroundImage != null) {
				Rectangle item_bounds = new Rectangle (0, 0, e.Item.Width, e.Item.Height);
				DrawBackground (e.Graphics, item_bounds, e.Item.BackgroundImage, e.Item.BackgroundImageLayout);
			}

			ToolStripItemRenderEventHandler eh = (ToolStripItemRenderEventHandler)Events [RenderItemBackgroundEvent];
			if (eh != null)
				eh (this, e);
		}

		protected virtual void OnRenderItemCheck (ToolStripItemImageRenderEventArgs e)
		{
			ToolStripItemImageRenderEventHandler eh = (ToolStripItemImageRenderEventHandler)Events [RenderItemCheckEvent];
			if (eh != null)
				eh (this, e);
		}

		protected virtual void OnRenderItemImage (ToolStripItemImageRenderEventArgs e)
		{
			bool need_dispose = false;
			Image i = e.Image;
			
			if (e.Item.RightToLeft == RightToLeft.Yes && e.Item.RightToLeftAutoMirrorImage == true) {
				i = CreateMirrorImage (i);
				need_dispose = true;
			}
				
			if (e.Item.ImageTransparentColor != Color.Empty) {
				ImageAttributes ia = new ImageAttributes ();
				ia.SetColorKey (e.Item.ImageTransparentColor, e.Item.ImageTransparentColor);
				e.Graphics.DrawImage (i, e.ImageRectangle, 0, 0, i.Width, i.Height, GraphicsUnit.Pixel, ia);
				ia.Dispose ();
			}
			else
				e.Graphics.DrawImage (i, e.ImageRectangle);
			
			if (need_dispose)
				i.Dispose ();
		
			ToolStripItemImageRenderEventHandler eh = (ToolStripItemImageRenderEventHandler)Events [RenderItemImageEvent];
			if (eh != null)
				eh (this, e);
		}

		protected virtual void OnRenderItemText (ToolStripItemTextRenderEventArgs e)
		{
			if (e.TextDirection == ToolStripTextDirection.Vertical90) {
				GraphicsState gs = e.Graphics.Save ();
				PointF p = new PointF (e.Graphics.Transform.OffsetX, e.Graphics.Transform.OffsetY);
				
				e.Graphics.ResetTransform ();
				e.Graphics.RotateTransform (90);
				
				RectangleF r = new RectangleF ((e.Item.Height - e.TextRectangle.Height) / 2, (e.TextRectangle.Width + p.X) * -1 - 18, e.TextRectangle.Height, e.TextRectangle.Width);
				
				StringFormat sf = new StringFormat ();
				sf.Alignment = StringAlignment.Center;
				
				e.Graphics.DrawString (e.Text, e.TextFont, ThemeEngine.Current.ResPool.GetSolidBrush (e.TextColor), r, sf);
				
				e.Graphics.Restore (gs);
			} else if (e.TextDirection == ToolStripTextDirection.Vertical270) {
				GraphicsState gs = e.Graphics.Save ();
				PointF p = new PointF (e.Graphics.Transform.OffsetX, e.Graphics.Transform.OffsetY);

				e.Graphics.ResetTransform ();
				e.Graphics.RotateTransform (270);

				RectangleF r = new RectangleF (-e.TextRectangle.Height - (e.Item.Height - e.TextRectangle.Height) / 2, (e.TextRectangle.Width + p.X) + 4, e.TextRectangle.Height, e.TextRectangle.Width);

				StringFormat sf = new StringFormat ();
				sf.Alignment = StringAlignment.Center;

				e.Graphics.DrawString (e.Text, e.TextFont, ThemeEngine.Current.ResPool.GetSolidBrush (e.TextColor), r, sf);

				e.Graphics.Restore (gs);
			} else
				TextRenderer.DrawText (e.Graphics, e.Text, e.TextFont, e.TextRectangle, e.TextColor, e.TextFormat);

			ToolStripItemTextRenderEventHandler eh = (ToolStripItemTextRenderEventHandler)Events[RenderItemTextEvent];
			if (eh != null)
				eh (this, e);
		}

		protected virtual void OnRenderLabelBackground (ToolStripItemRenderEventArgs e)
		{
			ToolStripItemRenderEventHandler eh = (ToolStripItemRenderEventHandler)Events [RenderLabelBackgroundEvent];
			if (eh != null)
				eh (this, e);
		}

		protected virtual void OnRenderMenuItemBackground (ToolStripItemRenderEventArgs e)
		{
			ToolStripItemRenderEventHandler eh = (ToolStripItemRenderEventHandler)Events [RenderMenuItemBackgroundEvent];
			if (eh != null)
				eh (this, e);
		}

		protected virtual void OnRenderOverflowButtonBackground (ToolStripItemRenderEventArgs e)
		{
			ToolStripItemRenderEventHandler eh = (ToolStripItemRenderEventHandler)Events [RenderOverflowButtonBackgroundEvent];
			if (eh != null)
				eh (this, e);
		}

		protected virtual void OnRenderSeparator (ToolStripSeparatorRenderEventArgs e)
		{
			ToolStripSeparatorRenderEventHandler eh = (ToolStripSeparatorRenderEventHandler)Events [RenderSeparatorEvent];
			if (eh != null)
				eh (this, e);
		}

		protected virtual void OnRenderSplitButtonBackground (ToolStripItemRenderEventArgs e)
		{
			ToolStripItemRenderEventHandler eh = (ToolStripItemRenderEventHandler)Events [RenderSplitButtonBackgroundEvent];
			if (eh != null)
				eh (this, e);
		}

		protected virtual void OnRenderStatusStripSizingGrip (ToolStripRenderEventArgs e)
		{
			StatusStrip ss = (StatusStrip)e.ToolStrip;
			
			if (ss.SizingGrip == true)
				PaintSizingGrip (e.Graphics, ss, SystemBrushes.ButtonHighlight, SystemBrushes.GrayText);
			
			ToolStripRenderEventHandler eh = (ToolStripRenderEventHandler)Events [RenderStatusStripSizingGripEvent];
			if (eh != null)
				eh (this, e);
		}

		protected virtual void OnRenderToolStripBackground (ToolStripRenderEventArgs e)
		{
			ToolStripRenderEventHandler eh = (ToolStripRenderEventHandler)Events [RenderToolStripBackgroundEvent];
			if (eh != null)
				eh (this, e);
		}

		protected virtual void OnRenderToolStripBorder (ToolStripRenderEventArgs e)
		{
			ToolStripRenderEventHandler eh = (ToolStripRenderEventHandler)Events [RenderToolStripBorderEvent];
			if (eh != null)
				eh (this, e);
		}

		protected virtual void OnRenderToolStripContentPanelBackground (ToolStripContentPanelRenderEventArgs e)
		{
			ToolStripContentPanelRenderEventHandler eh = (ToolStripContentPanelRenderEventHandler)Events [RenderToolStripContentPanelBackgroundEvent];
			if (eh != null)
				eh (this, e);
		}

		protected virtual void OnRenderToolStripPanelBackground (ToolStripPanelRenderEventArgs e)
		{
			ToolStripPanelRenderEventHandler eh = (ToolStripPanelRenderEventHandler)Events [RenderToolStripPanelBackgroundEvent];
			if (eh != null)
				eh (this, e);
		}

		protected virtual void OnRenderToolStripStatusLabelBackground (ToolStripItemRenderEventArgs e)
		{
			ToolStripItemRenderEventHandler eh = (ToolStripItemRenderEventHandler)Events [RenderToolStripStatusLabelBackgroundEvent];
			if (eh != null)
				eh (this, e);
		}
		#endregion

		#region Public Events
		EventHandlerList events;

		EventHandlerList Events {
			get {
				if (events == null)
					events = new EventHandlerList ();
				return events;
			}
		}

		static object RenderArrowEvent = new object ();
		static object RenderButtonBackgroundEvent = new object ();
		static object RenderDropDownButtonBackgroundEvent = new object ();
		static object RenderGripEvent = new object ();
		static object RenderImageMarginEvent = new object ();
		static object RenderItemBackgroundEvent = new object ();
		static object RenderItemCheckEvent = new object ();
		static object RenderItemImageEvent = new object ();
		static object RenderItemTextEvent = new object ();
		static object RenderLabelBackgroundEvent = new object ();
		static object RenderMenuItemBackgroundEvent = new object ();
		static object RenderOverflowButtonBackgroundEvent = new object ();
		static object RenderSeparatorEvent = new object ();
		static object RenderSplitButtonBackgroundEvent = new object ();
		static object RenderStatusStripSizingGripEvent = new object ();
		static object RenderToolStripBackgroundEvent = new object ();
		static object RenderToolStripBorderEvent = new object ();
		static object RenderToolStripContentPanelBackgroundEvent = new object ();
		static object RenderToolStripPanelBackgroundEvent = new object ();
		static object RenderToolStripStatusLabelBackgroundEvent = new object ();

		public event ToolStripArrowRenderEventHandler RenderArrow {
			add { Events.AddHandler (RenderArrowEvent, value); }
			remove {Events.RemoveHandler (RenderArrowEvent, value); }
		}
		public event ToolStripItemRenderEventHandler RenderButtonBackground {
			add { Events.AddHandler (RenderButtonBackgroundEvent, value); }
			remove {Events.RemoveHandler (RenderButtonBackgroundEvent, value); }
		}
		public event ToolStripItemRenderEventHandler RenderDropDownButtonBackground {
			add { Events.AddHandler (RenderDropDownButtonBackgroundEvent, value); }
			remove {Events.RemoveHandler (RenderDropDownButtonBackgroundEvent, value); }
		}
		public event ToolStripGripRenderEventHandler RenderGrip {
			add { Events.AddHandler (RenderGripEvent, value); }
			remove {Events.RemoveHandler (RenderGripEvent, value); }
		}
		public event ToolStripRenderEventHandler RenderImageMargin {
			add { Events.AddHandler (RenderImageMarginEvent, value); }
			remove {Events.RemoveHandler (RenderImageMarginEvent, value); }
		}
		public event ToolStripItemRenderEventHandler RenderItemBackground {
			add { Events.AddHandler (RenderItemBackgroundEvent, value); }
			remove {Events.RemoveHandler (RenderItemBackgroundEvent, value); }
		}
		public event ToolStripItemImageRenderEventHandler RenderItemCheck {
			add { Events.AddHandler (RenderItemCheckEvent, value); }
			remove {Events.RemoveHandler (RenderItemCheckEvent, value); }
		}
		public event ToolStripItemImageRenderEventHandler RenderItemImage {
			add { Events.AddHandler (RenderItemImageEvent, value); }
			remove {Events.RemoveHandler (RenderItemImageEvent, value); }
		}
		public event ToolStripItemTextRenderEventHandler RenderItemText {
			add { Events.AddHandler (RenderItemTextEvent, value); }
			remove {Events.RemoveHandler (RenderItemTextEvent, value); }
		}
		public event ToolStripItemRenderEventHandler RenderLabelBackground {
			add { Events.AddHandler (RenderLabelBackgroundEvent, value); }
			remove {Events.RemoveHandler (RenderLabelBackgroundEvent, value); }
		}
		public event ToolStripItemRenderEventHandler RenderMenuItemBackground {
			add { Events.AddHandler (RenderMenuItemBackgroundEvent, value); }
			remove {Events.RemoveHandler (RenderMenuItemBackgroundEvent, value); }
		}
		public event ToolStripItemRenderEventHandler RenderOverflowButtonBackground {
			add { Events.AddHandler (RenderOverflowButtonBackgroundEvent, value); }
			remove {Events.RemoveHandler (RenderOverflowButtonBackgroundEvent, value); }
		}
		public event ToolStripSeparatorRenderEventHandler RenderSeparator {
			add { Events.AddHandler (RenderSeparatorEvent, value); }
			remove {Events.RemoveHandler (RenderSeparatorEvent, value); }
		}
		public event ToolStripItemRenderEventHandler RenderSplitButtonBackground {
			add { Events.AddHandler (RenderSplitButtonBackgroundEvent, value); }
			remove {Events.RemoveHandler (RenderSplitButtonBackgroundEvent, value); }
		}
		public event ToolStripRenderEventHandler RenderStatusStripSizingGrip {
			add { Events.AddHandler (RenderStatusStripSizingGripEvent, value); }
			remove {Events.RemoveHandler (RenderStatusStripSizingGripEvent, value); }
		}
		public event ToolStripRenderEventHandler RenderToolStripBackground {
			add { Events.AddHandler (RenderToolStripBackgroundEvent, value); }
			remove {Events.RemoveHandler (RenderToolStripBackgroundEvent, value); }
		}
		public event ToolStripRenderEventHandler RenderToolStripBorder {
			add { Events.AddHandler (RenderToolStripBorderEvent, value); }
			remove {Events.RemoveHandler (RenderToolStripBorderEvent, value); }
		}
		public event ToolStripContentPanelRenderEventHandler RenderToolStripContentPanelBackground {
			add { Events.AddHandler (RenderToolStripContentPanelBackgroundEvent, value); }
			remove {Events.RemoveHandler (RenderToolStripContentPanelBackgroundEvent, value); }
		}
		public event ToolStripPanelRenderEventHandler RenderToolStripPanelBackground {
			add { Events.AddHandler (RenderToolStripPanelBackgroundEvent, value); }
			remove {Events.RemoveHandler (RenderToolStripPanelBackgroundEvent, value); }
		}
		public event ToolStripItemRenderEventHandler RenderToolStripStatusLabelBackground {
			add { Events.AddHandler (RenderToolStripStatusLabelBackgroundEvent, value); }
			remove {Events.RemoveHandler (RenderToolStripStatusLabelBackgroundEvent, value); }
		}
		#endregion
		
		#region Private Methods
		internal static Image CreateMirrorImage (Image normalImage)
		{
			if (normalImage == null)
				return null;

			Bitmap b = new Bitmap (normalImage);
			b.RotateFlip (RotateFlipType.RotateNoneFlipX);

			return b;
		}

		private void DrawBackground (Graphics g, Rectangle bounds, Image image, ImageLayout layout)
		{
			// Center and Tile don't matter if the image is larger than the control
			if (layout == ImageLayout.Center || layout == ImageLayout.Tile)
				if (image.Size.Width >= bounds.Size.Width && image.Size.Height >= bounds.Size.Height)
					layout = ImageLayout.None;
					
			switch (layout) {
				case ImageLayout.None:
					g.DrawImageUnscaledAndClipped (image, bounds);
					break;
				case ImageLayout.Tile:
					int x = 0;
					int y = 0;
					
					while (y < bounds.Height) {
						while (x < bounds.Width) {
							g.DrawImageUnscaledAndClipped (image, bounds);
							x += image.Width;	
						}
						x = 0;
						y += image.Height;
					}
					break;
				case ImageLayout.Center:
					Rectangle r = new Rectangle ((bounds.Size.Width - image.Size.Width) / 2, (bounds.Size.Height - image.Size.Height) / 2, image.Width, image.Height);
					g.DrawImageUnscaledAndClipped (image, r);
					break;
				case ImageLayout.Stretch:
					g.DrawImage (image, bounds);
					break;
				case ImageLayout.Zoom:
					if (((float)image.Height / (float)image.Width) < ((float)bounds.Height / (float)bounds.Width)) {
						Rectangle rzoom = new Rectangle (0, 0, bounds.Width, (int)((float)bounds.Width * ((float)image.Height / (float)image.Width)));
						rzoom.Y = (bounds.Height - rzoom.Height)/ 2;
						g.DrawImage (image, rzoom);
					} else {
						Rectangle rzoom = new Rectangle (0, 0, (int)((float)bounds.Height * ((float)image.Width / (float)image.Height)), bounds.Height);
						rzoom.X = (bounds.Width - rzoom.Width) / 2;
						g.DrawImage (image, rzoom);
					}
					break;
			}
		}

		internal static void DrawRightArrow (Graphics g, Pen p, int x, int y)
		{
			g.DrawLine (p, x, y, x, y + 6);
			g.DrawLine (p, x + 1, y + 1, x + 1, y + 5);
			g.DrawLine (p, x + 2, y + 2, x + 2, y + 4);
			g.DrawLine (p, x + 2, y + 3, x + 3, y + 3);
		}

		internal static void DrawDownArrow (Graphics g, Pen p, int x, int y)
		{
			g.DrawLine (p, x + 1, y, x + 5, y);
			g.DrawLine (p, x + 2, y + 1, x + 4, y + 1);
			g.DrawLine (p, x + 3, y + 1, x + 3, y + 2);
		}

		// THEMED, because the grip is one of the few things Windows 11 redrew outright: six tan
		// boxes became ten white dots. See Theme.StatusStripSizingGrip.
		private void DrawSizingGrip (Graphics g, Rectangle rect)
		{
			ThemeEngine.Current.StatusStripSizingGrip (g, rect);
		}

		// .NET's s_baseSizeGripRectangles: the dots of the grip, counted from the corner.
		private static readonly Rectangle [] s_baseSizeGripRectangles = {
			new Rectangle (12, 0, 2, 2), new Rectangle (8, 4, 2, 2), new Rectangle (4, 8, 2, 2), new Rectangle (0, 12, 2, 2),
			new Rectangle (8, 0, 2, 2), new Rectangle (4, 4, 2, 2), new Rectangle (0, 8, 2, 2),
			new Rectangle (4, 0, 2, 2), new Rectangle (0, 4, 2, 2), new Rectangle (1, 1, 2, 2),
		};

		/// <summary>.NET's OnRenderStatusStripSizingGrip: antialiased dots, each a highlight a pixel up and
		/// left of a shadow, laid out in a 20-unit design scaled to the grip's height and hung from the
		/// corner two pixels in. The scale is applied to the rectangles here rather than through the
		/// transform, which gives the same outlines.</summary>
		private static void PaintSizingGrip (Graphics g, StatusStrip strip, Brush highlight, Brush shadow)
		{
			Rectangle bounds = strip.SizeGripBounds;
			if (bounds.Width <= 0 || bounds.Height <= 0)
				return;
			float scale = bounds.Height / 20f;
			bool rtl = strip.RightToLeft == RightToLeft.Yes;
			const int CornerOffset = 2;
			float ox = rtl ? bounds.Left + CornerOffset : bounds.Right - CornerOffset, oy = bounds.Bottom - CornerOffset;
			SmoothingMode old = g.SmoothingMode;
			g.SmoothingMode = SmoothingMode.AntiAlias;
			RectangleF Place (Rectangle r) => new RectangleF (ox + r.X * scale, oy + r.Y * scale, r.Width * scale, r.Height * scale);
			foreach (Rectangle b in s_baseSizeGripRectangles) {
				var r = new Rectangle (rtl ? b.X : -b.X - b.Width, -b.Y - b.Height, b.Width, b.Height);
				Rectangle light = r, dark = r;
				light.Offset (-1, -1);
				dark.Offset (1, 1);
				g.FillEllipse (highlight, Place (light));
				g.FillEllipse (shadow, Place (dark));
			}
			g.SmoothingMode = old;
		}
		#endregion
	}
}
