// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
//
// Ported from dotnet/winforms: SelectionGlyphBase, GrabHandleGlyph, SelectionBorderGlyph, LockedHandleGlyph,
// LockedBorderGlyph, NoResizeHandleGlyph, NoResizeSelectionBorderGlyph, GrabHandleGlyphType,
// SelectionBorderGlyphType -- the glyphs ControlDesigner.GetGlyphs returns for a selected control -- and the part of
// DesignerUtils they measure and paint with. .NET paints the handles with GDI (RoundRect/Rectangle on the HDC);
// the same pixels are drawn here with the Graphics, which is what this stack's System.Drawing records.

using System.Drawing;
using System.Drawing.Drawing2D;

namespace System.Windows.Forms.Design.Behavior
{
	internal enum GrabHandleGlyphType
	{
		UpperLeft = 1,
		UpperRight,
		LowerLeft,
		LowerRight,
		MiddleTop,
		MiddleBottom,
		MiddleLeft,
		MiddleRight,
	}

	internal enum SelectionBorderGlyphType
	{
		Top = 1,
		Bottom,
		Left,
		Right,
		Body,
	}

	internal abstract class SelectionGlyphBase : Glyph
	{
		protected Rectangle bounds;         // the bounds of the selection glyph
		protected Rectangle hitBounds;      // the bounds used to hit test, which can differ from the glyph's own
		protected Cursor hitTestCursor;     // the cursor returned when the hit test is positive
		protected SelectionRules rules;     // how the control can change

		internal SelectionGlyphBase (Behavior behavior) : base (behavior)
		{
		}

		public SelectionRules SelectionRules => rules;

		public override Cursor GetHitTest (Point p) => hitBounds.Contains (p) ? hitTestCursor : null;

		public Cursor HitTestCursor => hitTestCursor;

		public override Rectangle Bounds => bounds;

		public override void Paint (PaintEventArgs pe)
		{
		}
	}

	internal class GrabHandleGlyph : SelectionGlyphBase
	{
		private readonly bool _isPrimary;

		internal GrabHandleGlyph (Rectangle controlBounds, GrabHandleGlyphType type, Behavior behavior, bool primarySelection)
			: base (behavior)
		{
			_isPrimary = primarySelection;
			hitTestCursor = Cursors.Default;
			rules = SelectionRules.None;
			int size = DesignerUtils.HandleSize, overlap = DesignerUtils.HandleOverlap;
			// Each handle overlaps the control by HandleOverlap pixels.
			switch (type) {
			case GrabHandleGlyphType.UpperLeft:
				bounds = new Rectangle (controlBounds.X + overlap - size, controlBounds.Y + overlap - size, size, size);
				hitTestCursor = Cursors.SizeNWSE;
				rules = SelectionRules.TopSizeable | SelectionRules.LeftSizeable;
				break;
			case GrabHandleGlyphType.UpperRight:
				bounds = new Rectangle (controlBounds.Right - overlap, controlBounds.Y + overlap - size, size, size);
				hitTestCursor = Cursors.SizeNESW;
				rules = SelectionRules.TopSizeable | SelectionRules.RightSizeable;
				break;
			case GrabHandleGlyphType.LowerRight:
				bounds = new Rectangle (controlBounds.Right - overlap, controlBounds.Bottom - overlap, size, size);
				hitTestCursor = Cursors.SizeNWSE;
				rules = SelectionRules.BottomSizeable | SelectionRules.RightSizeable;
				break;
			case GrabHandleGlyphType.LowerLeft:
				bounds = new Rectangle (controlBounds.X + overlap - size, controlBounds.Bottom - overlap, size, size);
				hitTestCursor = Cursors.SizeNESW;
				rules = SelectionRules.BottomSizeable | SelectionRules.LeftSizeable;
				break;
			case GrabHandleGlyphType.MiddleTop:
				// Only with room for the two corner handles' overlap, this handle, and one handle of padding.
				if (controlBounds.Width >= 2 * overlap + 2 * size) {
					bounds = new Rectangle (controlBounds.X + controlBounds.Width / 2 - size / 2, controlBounds.Y + overlap - size, size, size);
					hitTestCursor = Cursors.SizeNS;
					rules = SelectionRules.TopSizeable;
				}
				break;
			case GrabHandleGlyphType.MiddleBottom:
				if (controlBounds.Width >= 2 * overlap + 2 * size) {
					bounds = new Rectangle (controlBounds.X + controlBounds.Width / 2 - size / 2, controlBounds.Bottom - overlap, size, size);
					hitTestCursor = Cursors.SizeNS;
					rules = SelectionRules.BottomSizeable;
				}
				break;
			case GrabHandleGlyphType.MiddleLeft:
				if (controlBounds.Height >= 2 * overlap + 2 * size) {
					bounds = new Rectangle (controlBounds.X + overlap - size, controlBounds.Y + controlBounds.Height / 2 - size / 2, size, size);
					hitTestCursor = Cursors.SizeWE;
					rules = SelectionRules.LeftSizeable;
				}
				break;
			case GrabHandleGlyphType.MiddleRight:
				if (controlBounds.Height >= 2 * overlap + 2 * size) {
					bounds = new Rectangle (controlBounds.Right - overlap, controlBounds.Y + controlBounds.Height / 2 - size / 2, size, size);
					hitTestCursor = Cursors.SizeWE;
					rules = SelectionRules.RightSizeable;
				}
				break;
			}
			hitBounds = bounds;
		}

		public override void Paint (PaintEventArgs pe) => DesignerUtils.DrawGrabHandle (pe.Graphics, bounds, _isPrimary);
	}

	internal class SelectionBorderGlyph : SelectionGlyphBase
	{
		internal SelectionBorderGlyph (Rectangle controlBounds, SelectionRules selRules, SelectionBorderGlyphType type, Behavior behavior)
			: base (behavior)
		{
			rules = SelectionRules.None;
			hitTestCursor = Cursors.Default;
			bounds = DesignerUtils.GetBoundsForSelectionType (controlBounds, type);
			hitBounds = bounds;
			// The hit area of a border is wider than the border itself.
			int grow = DesignerUtils.SelectionBorderHitArea - DesignerUtils.SelectionBorderSize;
			switch (type) {
			case SelectionBorderGlyphType.Top:
			case SelectionBorderGlyphType.Bottom:
				SelectionRules side = type == SelectionBorderGlyphType.Top ? SelectionRules.TopSizeable : SelectionRules.BottomSizeable;
				if ((selRules & side) != 0) {
					hitTestCursor = Cursors.SizeNS;
					rules = side;
				}
				hitBounds.Y -= grow / 2;
				hitBounds.Height += grow;
				break;
			case SelectionBorderGlyphType.Left:
			case SelectionBorderGlyphType.Right:
				SelectionRules edge = type == SelectionBorderGlyphType.Left ? SelectionRules.LeftSizeable : SelectionRules.RightSizeable;
				if ((selRules & edge) != 0) {
					hitTestCursor = Cursors.SizeWE;
					rules = edge;
				}
				hitBounds.X -= grow / 2;
				hitBounds.Width += grow;
				break;
			}
		}

		public override void Paint (PaintEventArgs pe) => DesignerUtils.DrawSelectionBorder (pe.Graphics, bounds);
	}

	internal class LockedHandleGlyph : SelectionGlyphBase
	{
		private readonly bool _isPrimary;

		internal LockedHandleGlyph (Rectangle controlBounds, bool primarySelection) : base (null)
		{
			_isPrimary = primarySelection;
			hitTestCursor = Cursors.Default;
			rules = SelectionRules.None;
			bounds = new Rectangle (controlBounds.X + DesignerUtils.LockHandleOverlap - DesignerUtils.LockHandleWidth,
				controlBounds.Y + DesignerUtils.LockHandleOverlap - DesignerUtils.LockHandleHeight,
				DesignerUtils.LockHandleWidth, DesignerUtils.LockHandleHeight);
			hitBounds = bounds;
		}

		public override void Paint (PaintEventArgs pe) => DesignerUtils.DrawLockedHandle (pe.Graphics, bounds, _isPrimary);
	}

	internal class LockedBorderGlyph : SelectionGlyphBase
	{
		internal LockedBorderGlyph (Rectangle controlBounds, SelectionBorderGlyphType type) : base (null)
		{
			hitTestCursor = Cursors.Default;   // always the default cursor when locked
			rules = SelectionRules.None;       // never any selection rules when locked
			bounds = DesignerUtils.GetBoundsForSelectionType (controlBounds, type);
			hitBounds = bounds;
		}

		public override void Paint (PaintEventArgs pe) => DesignerUtils.DrawSelectionBorder (pe.Graphics, bounds);
	}

	internal class NoResizeHandleGlyph : SelectionGlyphBase
	{
		private readonly bool _isPrimary;

		internal NoResizeHandleGlyph (Rectangle controlBounds, SelectionRules selRules, bool primarySelection, Behavior behavior)
			: base (behavior)
		{
			_isPrimary = primarySelection;
			hitTestCursor = Cursors.Default;
			rules = SelectionRules.None;
			if ((selRules & SelectionRules.Moveable) != 0) {
				rules = SelectionRules.Moveable;
				hitTestCursor = Cursors.SizeAll;
			}
			// The handle is always at the upper left.
			int size = DesignerUtils.NoResizeHandleSize;
			bounds = new Rectangle (controlBounds.X - size, controlBounds.Y - size, size, size);
			hitBounds = bounds;
		}

		public override void Paint (PaintEventArgs pe) => DesignerUtils.DrawNoResizeHandle (pe.Graphics, bounds, _isPrimary);
	}

	internal class NoResizeSelectionBorderGlyph : SelectionGlyphBase
	{
		internal NoResizeSelectionBorderGlyph (Rectangle controlBounds, SelectionRules selRules, SelectionBorderGlyphType type, Behavior behavior)
			: base (behavior)
		{
			rules = SelectionRules.None;
			hitTestCursor = Cursors.Default;
			if ((selRules & SelectionRules.Moveable) != 0) {
				rules = SelectionRules.Moveable;
				hitTestCursor = Cursors.SizeAll;
			}
			bounds = DesignerUtils.GetBoundsForNoResizeSelectionType (controlBounds, type);
			hitBounds = bounds;
			int grow = DesignerUtils.SelectionBorderHitArea - DesignerUtils.SelectionBorderSize;
			switch (type) {
			case SelectionBorderGlyphType.Top:
			case SelectionBorderGlyphType.Bottom:
				hitBounds.Y -= grow / 2;
				hitBounds.Height += grow;
				break;
			case SelectionBorderGlyphType.Left:
			case SelectionBorderGlyphType.Right:
				hitBounds.X -= grow / 2;
				hitBounds.Width += grow;
				break;
			}
		}

		public override void Paint (PaintEventArgs pe) => DesignerUtils.DrawSelectionBorder (pe.Graphics, bounds);
	}

	/// <summary>The sizes and the painting of dotnet/winforms' DesignerUtils that the selection glyphs use.</summary>
	internal static class DesignerUtils
	{
		private static int Scale (int logical) => (int) Math.Round (logical * DpiScale);

		private static double DpiScale {
			get {
				try {
					using (Graphics g = Graphics.FromHwnd (IntPtr.Zero))
						return g.DpiX / 96.0;
				} catch {
					return 1.0;
				}
			}
		}

		internal static readonly int SelectionBorderSize = Scale (1);
		// The border is one pixel, its hit area three.
		internal static readonly int SelectionBorderHitArea = Scale (3);
		internal static readonly int HandleSize = Scale (7);
		internal static readonly int HandleOverlap = Scale (2);
		// The selection border is centred on the grab handles.
		internal static readonly int SelectionBorderOffset = (HandleSize - SelectionBorderSize) / 2 - HandleOverlap;
		internal static readonly int NoResizeHandleSize = Scale (5);
		internal static readonly int NoResizeBorderOffset = (NoResizeHandleSize - SelectionBorderSize) / 2;
		internal static readonly int LockHandleHeight = Scale (9);
		internal static readonly int LockHandleWidth = Scale (7);
		internal static readonly int LockHandleOverlap = Scale (2);
		internal static readonly int LockedHandleSizeUpper = Scale (5);
		internal static readonly int LockedHandleHeightLower = Scale (6);
		internal static readonly int LockedHandleWidthLower = Scale (7);
		internal static readonly int LockedHandleUpperOffset = (LockedHandleWidthLower - LockedHandleSizeUpper) / 2;
		internal static readonly int LockedHandleLowerOffset = LockHandleHeight - LockedHandleHeightLower;

		/// <summary>GDI's Rectangle(l, t, r, b) with a one-pixel pen: the outline on the outer pixels, the fill inside.</summary>
		private static void GdiRectangle (Graphics g, Rectangle r, Color pen, Color fill, bool round)
		{
			if (r.Width <= 0 || r.Height <= 0)
				return;
			using (var fillBrush = new SolidBrush (fill))
				g.FillRectangle (fillBrush, r.X + 1, r.Y + 1, r.Width - 2, r.Height - 2);
			using (var penBrush = new SolidBrush (pen)) {
				// RoundRect with a 2x2 corner ellipse leaves the four corner pixels out of the outline.
				int c = round ? 1 : 0;
				g.FillRectangle (penBrush, r.X + c, r.Y, r.Width - 2 * c, 1);
				g.FillRectangle (penBrush, r.X + c, r.Bottom - 1, r.Width - 2 * c, 1);
				g.FillRectangle (penBrush, r.X, r.Y + c, 1, r.Height - 2 * c);
				g.FillRectangle (penBrush, r.Right - 1, r.Y + c, 1, r.Height - 2 * c);
			}
		}

		internal static void DrawGrabHandle (Graphics graphics, Rectangle bounds, bool isPrimary)
			=> GdiRectangle (graphics, bounds,
				isPrimary ? SystemColors.ControlText : SystemColors.Window,
				isPrimary ? SystemColors.Window : SystemColors.ControlText, round: true);

		internal static void DrawNoResizeHandle (Graphics graphics, Rectangle bounds, bool isPrimary)
			=> GdiRectangle (graphics, bounds, SystemColors.ControlText,
				isPrimary ? SystemColors.Window : SystemColors.ControlText, round: false);

		internal static void DrawLockedHandle (Graphics graphics, Rectangle bounds, bool isPrimary)
		{
			// Upper part: always the primary fill.
			GdiRectangle (graphics, new Rectangle (bounds.Left + LockedHandleUpperOffset, bounds.Top, LockedHandleSizeUpper, LockedHandleSizeUpper),
				SystemColors.ControlText, SystemColors.Window, round: true);
			// Lower part: its fill follows the primary selection.
			GdiRectangle (graphics, Rectangle.FromLTRB (bounds.Left, bounds.Top + LockedHandleLowerOffset, bounds.Right, bounds.Bottom),
				SystemColors.ControlText, isPrimary ? SystemColors.Window : SystemColors.ControlText, round: false);
		}

		internal static void DrawSelectionBorder (Graphics graphics, Rectangle bounds)
		{
			using (var brush = new HatchBrush (HatchStyle.Percent50, SystemColors.ControlDarkDark, SystemColors.ControlDarkDark))
				graphics.FillRectangle (brush, bounds);
		}

		internal static Rectangle GetBoundsForSelectionType (Rectangle originalBounds, SelectionBorderGlyphType type, int borderSize)
		{
			switch (type) {
			case SelectionBorderGlyphType.Top:
				return new Rectangle (originalBounds.Left - borderSize, originalBounds.Top - borderSize, originalBounds.Width + 2 * borderSize, borderSize);
			case SelectionBorderGlyphType.Bottom:
				return new Rectangle (originalBounds.Left - borderSize, originalBounds.Bottom, originalBounds.Width + 2 * borderSize, borderSize);
			case SelectionBorderGlyphType.Left:
				return new Rectangle (originalBounds.Left - borderSize, originalBounds.Top - borderSize, borderSize, originalBounds.Height + 2 * borderSize);
			case SelectionBorderGlyphType.Right:
				return new Rectangle (originalBounds.Right, originalBounds.Top - borderSize, borderSize, originalBounds.Height + 2 * borderSize);
			case SelectionBorderGlyphType.Body:
				return originalBounds;
			default:
				return Rectangle.Empty;
			}
		}

		private static Rectangle GetBoundsForSelectionType (Rectangle originalBounds, SelectionBorderGlyphType type, int bordersize, int offset)
		{
			Rectangle bounds = GetBoundsForSelectionType (originalBounds, type, bordersize);
			if (offset != 0) {
				switch (type) {
				case SelectionBorderGlyphType.Top:
					bounds.Offset (-offset, -offset);
					bounds.Width += 2 * offset;
					break;
				case SelectionBorderGlyphType.Bottom:
					bounds.Offset (-offset, offset);
					bounds.Width += 2 * offset;
					break;
				case SelectionBorderGlyphType.Left:
					bounds.Offset (-offset, -offset);
					bounds.Height += 2 * offset;
					break;
				case SelectionBorderGlyphType.Right:
					bounds.Offset (offset, -offset);
					bounds.Height += 2 * offset;
					break;
				case SelectionBorderGlyphType.Body:
					bounds = originalBounds;
					break;
				}
			}
			return bounds;
		}

		internal static Rectangle GetBoundsForSelectionType (Rectangle originalBounds, SelectionBorderGlyphType type)
			=> GetBoundsForSelectionType (originalBounds, type, SelectionBorderSize, SelectionBorderOffset);

		internal static Rectangle GetBoundsForNoResizeSelectionType (Rectangle originalBounds, SelectionBorderGlyphType type)
			=> GetBoundsForSelectionType (originalBounds, type, SelectionBorderSize, NoResizeBorderOffset);
	}
}
