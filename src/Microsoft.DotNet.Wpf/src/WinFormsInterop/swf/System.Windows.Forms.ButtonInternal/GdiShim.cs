// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// The button adapters in this folder are .NET's (System.Windows.Forms.ButtonInternal), and .NET draws
// some of what they draw straight through GDI: a pen on an HDC, LineTo, FillRect. The port has no
// HDC -- everything goes through Graphics to the GPU recorder -- so these are the same calls with
// the same pixel rules, done on the Graphics the adapter was handed:
//   * LineTo draws from the start point up to, NOT including, the end point (Bresenham).
//   * FillRect fills the rectangle's pixels exactly.
//   * FindNearestColor on a true-colour surface is the colour.
// The types keep .NET's names so the adapters compile as .NET wrote them.

using System.Drawing;

namespace System.Windows.Forms.ButtonInternal
{
	/// <summary>A device context: here, the Graphics it stands for.</summary>
	internal readonly struct HDC
	{
		internal readonly Graphics Graphics;
		internal HDC (Graphics g) { Graphics = g; }
		internal bool IsNull => Graphics == null;
	}

	internal readonly struct HPEN
	{
		internal readonly Color Color;
		internal HPEN (Color c) { Color = c; }
	}

	internal readonly struct HBRUSH
	{
		internal readonly Color Color;
		internal HBRUSH (Color c) { Color = c; }
	}

	internal enum ApplyGraphicsProperties
	{
		None = 0,
		Clipping = 1,
		TranslateTransform = 2,
		All = 3,
	}

	/// <summary>.NET's scope that pulls an HDC out of a Graphics or PaintEventArgs.</summary>
	internal sealed class DeviceContextHdcScope : IDisposable
	{
		private readonly Graphics _graphics;

		internal DeviceContextHdcScope (IDeviceContext deviceContext, ApplyGraphicsProperties apply = ApplyGraphicsProperties.All)
		{
			_graphics = deviceContext as Graphics ?? (deviceContext as PaintEventArgs)?.Graphics;
		}

		internal DeviceContextHdcScope (PaintEventArgs e, ApplyGraphicsProperties apply = ApplyGraphicsProperties.All)
		{
			_graphics = e.Graphics;
		}

		internal HDC HDC => new HDC (_graphics);

		public static implicit operator HDC (DeviceContextHdcScope scope) => scope.HDC;

		internal Color FindNearestColor (Color color) => color;

		public void Dispose () { }
	}

	internal sealed class CreatePenScope : IDisposable
	{
		internal readonly HPEN Pen;
		internal CreatePenScope (Color color, int width = 1) { Pen = new HPEN (color); }
		public static implicit operator HPEN (CreatePenScope scope) => scope.Pen;
		public void Dispose () { }
	}

	internal sealed class CreateBrushScope : IDisposable
	{
		internal readonly HBRUSH Brush;
		internal CreateBrushScope (Color color) { Brush = new HBRUSH (color); }
		public static implicit operator HBRUSH (CreateBrushScope scope) => scope.Brush;
		public void Dispose () { }
	}

	internal static class GdiShimExtensions
	{
		internal static DeviceContextHdcScope ToHdcScope (this IDeviceContext deviceContext, ApplyGraphicsProperties apply = ApplyGraphicsProperties.All)
			=> new DeviceContextHdcScope (deviceContext, apply);

		internal static Color FindNearestColor (this HDC hdc, Color color) => color;

		internal static void DrawLine (this DeviceContextHdcScope hdc, HPEN pen, Point p1, Point p2) => hdc.HDC.DrawLine (pen, p1.X, p1.Y, p2.X, p2.Y);
		internal static void DrawLine (this DeviceContextHdcScope hdc, HPEN pen, int x1, int y1, int x2, int y2) => hdc.HDC.DrawLine (pen, x1, y1, x2, y2);
		internal static void FillRectangle (this DeviceContextHdcScope hdc, HBRUSH brush, Rectangle r) => hdc.HDC.FillRectangle (brush, r);

		internal static void DrawLine (this HDC hdc, HPEN pen, Point p1, Point p2) => hdc.DrawLine (pen, p1.X, p1.Y, p2.X, p2.Y);

		/// <summary>The scaled radio dot (above 110% only): an ellipse filled and outlined.</summary>
		internal static void DrawAndFillEllipse (this DeviceContextHdcScope hdc, HPEN pen, HBRUSH brush, Rectangle r)
		{
			Graphics g = hdc.HDC.Graphics;
			if (g == null) return;
			using (var b = new SolidBrush (brush.Color)) g.FillEllipse (b, r);
			using (var p = new Pen (pen.Color)) g.DrawEllipse (p, r);
		}

		// .NET's argument orders.
		internal static void FillRectangle (this HDC hdc, Rectangle r, HBRUSH brush) => hdc.FillRectangle (brush, r);
		internal static void FillRectangle (this DeviceContextHdcScope hdc, Rectangle r, HBRUSH brush) => hdc.HDC.FillRectangle (brush, r);
		internal static void DrawRectangle (this DeviceContextHdcScope hdc, Rectangle r, HPEN pen) => hdc.HDC.DrawRectangle (r, pen);

		/// <summary>GDI's Rectangle with a hollow brush: the outline on the rectangle's outermost
		/// pixels, right and bottom edges at Right-1 and Bottom-1.</summary>
		internal static void DrawRectangle (this HDC hdc, Rectangle r, HPEN pen)
		{
			if (hdc.Graphics == null || r.Width <= 0 || r.Height <= 0)
				return;
			ControlPaint.DrawBorderSimple (hdc.Graphics, r, pen.Color);
		}

		/// <summary>MoveToEx + LineTo: every pixel from (x1,y1) up to but not including (x2,y2).</summary>
		internal static void DrawLine (this HDC hdc, HPEN pen, int x1, int y1, int x2, int y2)
		{
			Graphics g = hdc.Graphics;
			if (g == null || (x1 == x2 && y1 == y2))
				return;
			using (var brush = new SolidBrush (pen.Color)) {
				if (y1 == y2) {
					int a = Math.Min (x1, x2), b = Math.Max (x1, x2);
					// Leaving out the end point: from x1 toward x2, x2 excluded.
					if (x2 > x1) g.FillRectangle (brush, x1, y1, x2 - x1, 1);
					else g.FillRectangle (brush, x2 + 1, y1, x1 - x2, 1);
					return;
				}
				if (x1 == x2) {
					if (y2 > y1) g.FillRectangle (brush, x1, y1, 1, y2 - y1);
					else g.FillRectangle (brush, x1, y2 + 1, 1, y1 - y2);
					return;
				}
				int dx = Math.Abs (x2 - x1), dy = -Math.Abs (y2 - y1);
				int sx = x1 < x2 ? 1 : -1, sy = y1 < y2 ? 1 : -1;
				int err = dx + dy, x = x1, y = y1;
				while (x != x2 || y != y2) {
					g.FillRectangle (brush, x, y, 1, 1);
					int e2 = 2 * err;
					if (e2 >= dy) { err += dy; x += sx; }
					if (e2 <= dx) { err += dx; y += sy; }
				}
			}
		}

		internal static void FillRectangle (this HDC hdc, HBRUSH brush, Rectangle r)
		{
			if (hdc.Graphics == null || r.Width <= 0 || r.Height <= 0)
				return;
			using (var b = new SolidBrush (brush.Color))
				hdc.Graphics.FillRectangle (b, r);
		}
	}
}
