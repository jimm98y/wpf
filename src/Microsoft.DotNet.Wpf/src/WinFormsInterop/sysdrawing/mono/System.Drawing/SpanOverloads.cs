// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// The span overloads .NET 9 gave System.Drawing: each is its array overload's GDI+ call over the span's items,
// so here each hands the array overload the span's items (the checks that differ are .NET's own, kept). The
// transforms write their results back through the span, as .NET's do -- "read-only" is only the parameter's
// type there, GDI+ transforms the points in place.

using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace System.Drawing
{
	public sealed partial class Graphics
	{
		public void DrawBeziers (Pen pen, params ReadOnlySpan<PointF> points) => DrawBeziers (pen, points.ToArray ());

		public void DrawBeziers (Pen pen, params ReadOnlySpan<Point> points) => DrawBeziers (pen, points.ToArray ());

		public void DrawClosedCurve (Pen pen, params ReadOnlySpan<PointF> points) => DrawClosedCurve (pen, points.ToArray ());

		public void DrawClosedCurve (Pen pen, ReadOnlySpan<PointF> points, float tension, FillMode fillmode) => DrawClosedCurve (pen, points.ToArray (), tension, fillmode);

		public void DrawClosedCurve (Pen pen, params ReadOnlySpan<Point> points) => DrawClosedCurve (pen, points.ToArray ());

		public void DrawClosedCurve (Pen pen, ReadOnlySpan<Point> points, float tension, FillMode fillmode) => DrawClosedCurve (pen, points.ToArray (), tension, fillmode);

		public void DrawCurve (Pen pen, params ReadOnlySpan<PointF> points) => DrawCurve (pen, points.ToArray ());

		public void DrawCurve (Pen pen, ReadOnlySpan<PointF> points, int offset, int numberOfSegments) => DrawCurve (pen, points.ToArray (), offset, numberOfSegments);

		public void DrawCurve (Pen pen, ReadOnlySpan<PointF> points, int offset, int numberOfSegments, float tension) => DrawCurve (pen, points.ToArray (), offset, numberOfSegments, tension);

		public void DrawCurve (Pen pen, ReadOnlySpan<PointF> points, float tension) => DrawCurve (pen, points.ToArray (), tension);

		public void DrawCurve (Pen pen, params ReadOnlySpan<Point> points) => DrawCurve (pen, points.ToArray ());

		public void DrawCurve (Pen pen, ReadOnlySpan<Point> points, int offset, int numberOfSegments, float tension) => DrawCurve (pen, points.ToArray (), offset, numberOfSegments, tension);

		public void DrawCurve (Pen pen, ReadOnlySpan<Point> points, float tension) => DrawCurve (pen, points.ToArray (), tension);

		public void DrawLines (Pen pen, params ReadOnlySpan<PointF> points) => DrawLines (pen, points.ToArray ());

		public void DrawLines (Pen pen, params ReadOnlySpan<Point> points) => DrawLines (pen, points.ToArray ());

		public void DrawPolygon (Pen pen, params ReadOnlySpan<PointF> points) => DrawPolygon (pen, points.ToArray ());

		public void DrawPolygon (Pen pen, params ReadOnlySpan<Point> points) => DrawPolygon (pen, points.ToArray ());

		public void DrawRectangles (Pen pen, params ReadOnlySpan<RectangleF> rects) => DrawRectangles (pen, rects.ToArray ());

		public void DrawRectangles (Pen pen, params ReadOnlySpan<Rectangle> rects) => DrawRectangles (pen, rects.ToArray ());

		public void FillClosedCurve (Brush brush, params ReadOnlySpan<PointF> points) => FillClosedCurve (brush, points.ToArray ());

		public void FillClosedCurve (Brush brush, ReadOnlySpan<PointF> points, FillMode fillmode) => FillClosedCurve (brush, points.ToArray (), fillmode);

		public void FillClosedCurve (Brush brush, ReadOnlySpan<PointF> points, FillMode fillmode, float tension) => FillClosedCurve (brush, points.ToArray (), fillmode, tension);

		public void FillClosedCurve (Brush brush, params ReadOnlySpan<Point> points) => FillClosedCurve (brush, points.ToArray ());

		public void FillClosedCurve (Brush brush, ReadOnlySpan<Point> points, FillMode fillmode) => FillClosedCurve (brush, points.ToArray (), fillmode);

		public void FillClosedCurve (Brush brush, ReadOnlySpan<Point> points, FillMode fillmode, float tension) => FillClosedCurve (brush, points.ToArray (), fillmode, tension);

		public void FillPolygon (Brush brush, params ReadOnlySpan<PointF> points) => FillPolygon (brush, points.ToArray ());

		public void FillPolygon (Brush brush, ReadOnlySpan<PointF> points, FillMode fillMode) => FillPolygon (brush, points.ToArray (), fillMode);

		public void FillPolygon (Brush brush, params ReadOnlySpan<Point> points) => FillPolygon (brush, points.ToArray ());

		public void FillPolygon (Brush brush, ReadOnlySpan<Point> points, FillMode fillMode) => FillPolygon (brush, points.ToArray (), fillMode);

		public void FillRectangles (Brush brush, params ReadOnlySpan<RectangleF> rects) => FillRectangles (brush, rects.ToArray ());

		public void FillRectangles (Brush brush, params ReadOnlySpan<Rectangle> rects) => FillRectangles (brush, rects.ToArray ());

		public void TransformPoints (CoordinateSpace destSpace, CoordinateSpace srcSpace, params ReadOnlySpan<PointF> pts)
		{
			PointF [] a = pts.ToArray ();
			TransformPoints (destSpace, srcSpace, a);
			a.CopyTo (Writable (pts));
		}

		public void TransformPoints (CoordinateSpace destSpace, CoordinateSpace srcSpace, params ReadOnlySpan<Point> pts)
		{
			Point [] a = pts.ToArray ();
			TransformPoints (destSpace, srcSpace, a);
			a.CopyTo (Writable (pts));
		}

		internal static Span<T> Writable<T> (ReadOnlySpan<T> span) => MemoryMarshal.CreateSpan (ref MemoryMarshal.GetReference (span), span.Length);

		// ---- text ----------------------------------------------------------------------------------------

		public void DrawString (ReadOnlySpan<char> s, Font font, Brush brush, PointF point) => DrawString (s.ToString (), font, brush, point);

		public void DrawString (ReadOnlySpan<char> s, Font font, Brush brush, PointF point, StringFormat format) => DrawString (s.ToString (), font, brush, point, format);

		public void DrawString (ReadOnlySpan<char> s, Font font, Brush brush, RectangleF layoutRectangle) => DrawString (s.ToString (), font, brush, layoutRectangle);

		public void DrawString (ReadOnlySpan<char> s, Font font, Brush brush, RectangleF layoutRectangle, StringFormat format) => DrawString (s.ToString (), font, brush, layoutRectangle, format);

		public void DrawString (ReadOnlySpan<char> s, Font font, Brush brush, float x, float y) => DrawString (s.ToString (), font, brush, x, y);

		public void DrawString (ReadOnlySpan<char> s, Font font, Brush brush, float x, float y, StringFormat format) => DrawString (s.ToString (), font, brush, x, y, format);

		public Region [] MeasureCharacterRanges (ReadOnlySpan<char> text, Font font, RectangleF layoutRect, StringFormat stringFormat) =>
			MeasureCharacterRanges (text.ToString (), font, layoutRect, stringFormat);

		public SizeF MeasureString (ReadOnlySpan<char> text, Font font) => MeasureString (text.ToString (), font);

		public SizeF MeasureString (ReadOnlySpan<char> text, Font font, PointF origin, StringFormat stringFormat) => MeasureString (text.ToString (), font, origin, stringFormat);

		public SizeF MeasureString (ReadOnlySpan<char> text, Font font, SizeF layoutArea) => MeasureString (text.ToString (), font, layoutArea);

		public SizeF MeasureString (ReadOnlySpan<char> text, Font font, SizeF layoutArea, StringFormat stringFormat) => MeasureString (text.ToString (), font, layoutArea, stringFormat);

		public SizeF MeasureString (ReadOnlySpan<char> text, Font font, SizeF layoutArea, StringFormat stringFormat, out int charactersFitted, out int linesFilled) =>
			MeasureString (text.ToString (), font, layoutArea, stringFormat, out charactersFitted, out linesFilled);

		public SizeF MeasureString (ReadOnlySpan<char> text, Font font, int width) => MeasureString (text.ToString (), font, width);

		public SizeF MeasureString (ReadOnlySpan<char> text, Font font, int width, StringFormat format) => MeasureString (text.ToString (), font, width, format);

		/// <summary>.NET's: GdipMeasureString in the layout rectangle (its origin moves the box, not its size).</summary>
		public SizeF MeasureStringInternal (ReadOnlySpan<char> text, Font font, RectangleF layoutArea, StringFormat stringFormat, out int charactersFitted, out int linesFilled) =>
			MeasureString (text.ToString (), font, layoutArea.Size, stringFormat, out charactersFitted, out linesFilled);
	}
}

namespace System.Drawing.Drawing2D
{
	public sealed partial class GraphicsPath
	{
		public GraphicsPath (ReadOnlySpan<PointF> pts, ReadOnlySpan<byte> types, FillMode fillMode = FillMode.Alternate)
			: this (Same (pts, types).ToArray (), types.ToArray (), fillMode)
		{
		}

		public GraphicsPath (ReadOnlySpan<Point> pts, ReadOnlySpan<byte> types, FillMode fillMode = FillMode.Alternate)
			: this (Same (pts, types).ToArray (), types.ToArray (), fillMode)
		{
		}

		// .NET's: points and types of one length, or InvalidParameter before GDI+ is asked.
		static ReadOnlySpan<T> Same<T> (ReadOnlySpan<T> pts, ReadOnlySpan<byte> types)
		{
			if (pts.Length != types.Length)
				GDIPlus.CheckStatus (Status.InvalidParameter);
			return pts;
		}

		public void AddBeziers (params ReadOnlySpan<PointF> points) => AddBeziers (points.ToArray ());

		public void AddBeziers (params ReadOnlySpan<Point> points) => AddBeziers (points.ToArray ());

		public void AddClosedCurve (params ReadOnlySpan<PointF> points) => AddClosedCurve (points.ToArray ());

		public void AddClosedCurve (ReadOnlySpan<PointF> points, float tension) => AddClosedCurve (points.ToArray (), tension);

		public void AddClosedCurve (params ReadOnlySpan<Point> points) => AddClosedCurve (points.ToArray ());

		public void AddClosedCurve (ReadOnlySpan<Point> points, float tension) => AddClosedCurve (points.ToArray (), tension);

		public void AddCurve (params ReadOnlySpan<PointF> points) => AddCurve (points.ToArray ());

		public void AddCurve (ReadOnlySpan<PointF> points, float tension) => AddCurve (points.ToArray (), tension);

		public void AddCurve (ReadOnlySpan<Point> points) => AddCurve (points.ToArray ());

		public void AddCurve (ReadOnlySpan<Point> points, float tension) => AddCurve (points.ToArray (), tension);

		public void AddLines (params ReadOnlySpan<PointF> points) => AddLines (points.ToArray ());

		public void AddLines (params ReadOnlySpan<Point> points) => AddLines (points.ToArray ());

		public void AddPolygon (params ReadOnlySpan<PointF> points) => AddPolygon (points.ToArray ());

		public void AddPolygon (params ReadOnlySpan<Point> points) => AddPolygon (points.ToArray ());

		public void AddRectangles (params ReadOnlySpan<RectangleF> rects) => AddRectangles (rects.ToArray ());

		public void AddRectangles (params ReadOnlySpan<Rectangle> rects) => AddRectangles (rects.ToArray ());

		public void Warp (ReadOnlySpan<PointF> destPoints, RectangleF srcRect, Matrix matrix = null, WarpMode warpMode = WarpMode.Perspective, float flatness = 0.25f) =>
			Warp (destPoints.ToArray (), srcRect, matrix, warpMode, flatness);

		/// <summary>The path's points into the span, as many as it holds; the path's point count.</summary>
		public int GetPathPoints (Span<PointF> destination)
		{
			if (destination.IsEmpty)
				return 0;
			PointF [] points = PathPoints;
			points.AsSpan (0, Math.Min (points.Length, destination.Length)).CopyTo (destination);
			return points.Length;
		}

		/// <summary>The path's point types into the span, as many as it holds; the path's point count.</summary>
		public int GetPathTypes (Span<byte> destination)
		{
			if (destination.IsEmpty)
				return 0;
			byte [] types = PathTypes;
			types.AsSpan (0, Math.Min (types.Length, destination.Length)).CopyTo (destination);
			return types.Length;
		}
	}

	public sealed partial class GraphicsPathIterator
	{
		public int Enumerate (Span<PointF> points, Span<byte> types)
		{
			if (points.Length != types.Length || points.Length < Count)
				throw SafeNativeMethods.Gdip.StatusException (SafeNativeMethods.Gdip.InvalidParameter);
			if (points.Length == 0)
				return 0;
			PointF [] p = new PointF [points.Length];
			byte [] t = new byte [types.Length];
			int n = Enumerate (ref p, ref t);
			p.AsSpan (0, n).CopyTo (points);
			t.AsSpan (0, n).CopyTo (types);
			return n;
		}

		public int CopyData (Span<PointF> points, Span<byte> types, int startIndex, int endIndex)
		{
			if (points.Length != types.Length || endIndex < 0 || startIndex < 0 || endIndex < startIndex
				|| endIndex - startIndex + 1 > points.Length || endIndex >= Count)
				throw SafeNativeMethods.Gdip.StatusException (SafeNativeMethods.Gdip.InvalidParameter);
			PointF [] p = new PointF [points.Length];
			byte [] t = new byte [types.Length];
			int n = CopyData (ref p, ref t, startIndex, endIndex);
			p.AsSpan (0, n).CopyTo (points);
			t.AsSpan (0, n).CopyTo (types);
			return n;
		}
	}

	public sealed partial class Matrix
	{
		public void TransformPoints (params ReadOnlySpan<PointF> pts)
		{
			PointF [] a = pts.ToArray ();
			TransformPoints (a);
			a.CopyTo (Graphics.Writable (pts));
		}

		public void TransformPoints (params ReadOnlySpan<Point> pts)
		{
			Point [] a = pts.ToArray ();
			TransformPoints (a);
			a.CopyTo (Graphics.Writable (pts));
		}

		public void TransformVectors (params ReadOnlySpan<PointF> pts)
		{
			PointF [] a = pts.ToArray ();
			TransformVectors (a);
			a.CopyTo (Graphics.Writable (pts));
		}

		public void TransformVectors (params ReadOnlySpan<Point> pts)
		{
			Point [] a = pts.ToArray ();
			TransformVectors (a);
			a.CopyTo (Graphics.Writable (pts));
		}

		public void VectorTransformPoints (params ReadOnlySpan<Point> pts) => TransformVectors (pts);
	}

	public sealed partial class PathGradientBrush
	{
		public PathGradientBrush (params ReadOnlySpan<PointF> points) : this (points.ToArray ())
		{
		}

		public PathGradientBrush (params ReadOnlySpan<Point> points) : this (points.ToArray ())
		{
		}

		public PathGradientBrush (WrapMode wrapMode, params ReadOnlySpan<PointF> points) : this (points.ToArray (), wrapMode)
		{
		}

		public PathGradientBrush (WrapMode wrapMode, params ReadOnlySpan<Point> points) : this (points.ToArray (), wrapMode)
		{
		}
	}
}

namespace System.Drawing.Imaging
{
	public sealed partial class ColorMatrix
	{
		/// <summary>The 25 elements, row by row.</summary>
		public ColorMatrix (params ReadOnlySpan<float> newColorMatrix)
		{
			ArgumentOutOfRangeException.ThrowIfNotEqual (newColorMatrix.Length, 25, nameof (newColorMatrix));
			var rows = new float [5][];
			for (int i = 0; i < 5; i++)
				rows [i] = newColorMatrix.Slice (i * 5, 5).ToArray ();
			SetMatrix (rows);
		}
	}
}
