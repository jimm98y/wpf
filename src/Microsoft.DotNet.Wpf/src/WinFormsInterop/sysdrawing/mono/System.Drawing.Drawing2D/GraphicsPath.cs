//
// System.Drawing.Drawing2D.GraphicsPath.cs
//
// Authors:
//
//   Miguel de Icaza (miguel@ximian.com)
//   Duncan Mak (duncan@ximian.com)
//   Jordi Mas i Hernandez (jordi@ximian.com)
//   Ravindra (rkumar@novell.com)
//   Sebastien Pouliot  <sebastien@ximian.com>
//
// Copyright (C) 2004,2006-2007 Novell, Inc (http://www.novell.com)
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
//
// A managed GpPath (WebGpuBackend.Gdip.GpPath): GDI+'s points and type bytes, built by GDI+'s own
// rules (figure continuation, arc points, splines, flattening) as gdiplus.dll builds them.
//

using System.ComponentModel;
using System.Drawing.WebGpuBackend.Gdip;

namespace System.Drawing.Drawing2D
{
	public sealed class GraphicsPath : MarshalByRefObject, ICloneable, IDisposable
	{
		// 1/4 is the FlatnessDefault as defined in GdiPlusEnums.h
		private const float FlatnessDefault = 1.0f / 4.0f;

		internal GpPath gp;

		internal GraphicsPath (GpPath path)
		{
			gp = path;
		}

		public GraphicsPath ()
		{
			gp = new GpPath (FillMode.Alternate);
		}

		public GraphicsPath (FillMode fillMode)
		{
			CheckFillMode (fillMode);
			gp = new GpPath (fillMode);
		}

		public GraphicsPath (Point[] pts, byte[] types)
			: this (pts, types, FillMode.Alternate)
		{
		}

		public GraphicsPath (PointF[] pts, byte[] types)
			: this (pts, types, FillMode.Alternate)
		{
		}

		public GraphicsPath (Point[] pts, byte[] types, FillMode fillMode)
		{
			if (pts == null)
				throw new ArgumentNullException ("pts");
			if (pts.Length != types.Length)
				throw new ArgumentException ("Invalid parameter passed. Number of points and types must be same.");
			CheckFillMode (fillMode);
			gp = new GpPath (ToF (pts), types, fillMode);
		}

		public GraphicsPath (PointF[] pts, byte[] types, FillMode fillMode)
		{
			if (pts == null)
				throw new ArgumentNullException ("pts");
			if (pts.Length != types.Length)
				throw new ArgumentException ("Invalid parameter passed. Number of points and types must be same.");
			CheckFillMode (fillMode);
			gp = new GpPath (pts, types, fillMode);
		}

		static void CheckFillMode (FillMode mode)
		{
			if (mode < FillMode.Alternate || mode > FillMode.Winding)
				throw new InvalidEnumArgumentException ("fillMode", (int) mode, typeof (FillMode));
		}

		static PointF [] ToF (Point [] p)
		{
			var r = new PointF [p.Length];
			for (int i = 0; i < p.Length; i++) r [i] = new PointF (p [i].X, p [i].Y);
			return r;
		}

		public object Clone () => new GraphicsPath (gp.Clone ());

		public void Dispose ()
		{
			GC.SuppressFinalize (this);
		}

		static void Invalid ()
		{
			throw new ArgumentException ("Parameter is not valid.");
		}

		public FillMode FillMode {
			get => gp.FillMode;
			set {
				CheckFillMode (value);
				gp.FillMode = value;
			}
		}

		public PathData PathData {
			get {
				var pdata = new PathData ();
				pdata.Points = gp.PointArray ();
				pdata.Types = gp.TypeArray ();
				return pdata;
			}
		}

		public PointF [] PathPoints {
			get {
				if (gp.Count == 0)
					throw new ArgumentException ("PathPoints");
				return gp.PointArray ();
			}
		}

		public byte [] PathTypes {
			get {
				if (gp.Count == 0)
					throw new ArgumentException ("PathTypes");
				return gp.TypeArray ();
			}
		}

		public int PointCount => gp.Count;

		// No GDI+ object behind a path any more.
		internal IntPtr NativeObject => IntPtr.Zero;

		//
		// AddArc
		//
		public void AddArc (Rectangle rect, float startAngle, float sweepAngle) => AddArc ((float) rect.X, rect.Y, rect.Width, rect.Height, startAngle, sweepAngle);

		public void AddArc (RectangleF rect, float startAngle, float sweepAngle) => AddArc (rect.X, rect.Y, rect.Width, rect.Height, startAngle, sweepAngle);

		public void AddArc (int x, int y, int width, int height, float startAngle, float sweepAngle) => AddArc ((float) x, y, width, height, startAngle, sweepAngle);

		public void AddArc (float x, float y, float width, float height, float startAngle, float sweepAngle)
		{
			if (!gp.AddArc (x, y, width, height, startAngle, sweepAngle)) Invalid ();
		}

		//
		// AddBezier
		//
		public void AddBezier (Point pt1, Point pt2, Point pt3, Point pt4)
			=> gp.AddBezier (pt1.X, pt1.Y, pt2.X, pt2.Y, pt3.X, pt3.Y, pt4.X, pt4.Y);

		public void AddBezier (PointF pt1, PointF pt2, PointF pt3, PointF pt4)
			=> gp.AddBezier (pt1.X, pt1.Y, pt2.X, pt2.Y, pt3.X, pt3.Y, pt4.X, pt4.Y);

		public void AddBezier (int x1, int y1, int x2, int y2, int x3, int y3, int x4, int y4)
			=> gp.AddBezier (x1, y1, x2, y2, x3, y3, x4, y4);

		public void AddBezier (float x1, float y1, float x2, float y2, float x3, float y3, float x4, float y4)
			=> gp.AddBezier (x1, y1, x2, y2, x3, y3, x4, y4);

		//
		// AddBeziers
		//
		public void AddBeziers (params Point [] points)
		{
			if (points == null)
				throw new ArgumentNullException ("points");
			if (!gp.AddBeziers (ToF (points), points.Length)) Invalid ();
		}

		public void AddBeziers (PointF [] points)
		{
			if (points == null)
				throw new ArgumentNullException ("points");
			if (!gp.AddBeziers (points, points.Length)) Invalid ();
		}

		//
		// AddEllipse
		//
		public void AddEllipse (RectangleF rect) => gp.AddEllipse (rect.X, rect.Y, rect.Width, rect.Height);

		public void AddEllipse (float x, float y, float width, float height) => gp.AddEllipse (x, y, width, height);

		public void AddEllipse (Rectangle rect) => gp.AddEllipse (rect.X, rect.Y, rect.Width, rect.Height);

		public void AddEllipse (int x, int y, int width, int height) => gp.AddEllipse (x, y, width, height);

		//
		// AddLine
		//
		public void AddLine (Point pt1, Point pt2) => gp.AddLine (pt1.X, pt1.Y, pt2.X, pt2.Y);

		public void AddLine (PointF pt1, PointF pt2) => gp.AddLine (pt1.X, pt1.Y, pt2.X, pt2.Y);

		public void AddLine (int x1, int y1, int x2, int y2) => gp.AddLine (x1, y1, x2, y2);

		public void AddLine (float x1, float y1, float x2, float y2) => gp.AddLine (x1, y1, x2, y2);

		//
		// AddLines
		//
		public void AddLines (Point[] points)
		{
			if (points == null)
				throw new ArgumentNullException ("points");
			if (points.Length == 0)
				throw new ArgumentException ("points");
			if (!gp.AddLines (ToF (points), points.Length)) Invalid ();
		}

		public void AddLines (PointF[] points)
		{
			if (points == null)
				throw new ArgumentNullException ("points");
			if (points.Length == 0)
				throw new ArgumentException ("points");
			if (!gp.AddLines (points, points.Length)) Invalid ();
		}

		//
		// AddPie
		//
		public void AddPie (Rectangle rect, float startAngle, float sweepAngle) => AddPie ((float) rect.X, rect.Y, rect.Width, rect.Height, startAngle, sweepAngle);

		public void AddPie (int x, int y, int width, int height, float startAngle, float sweepAngle) => AddPie ((float) x, y, width, height, startAngle, sweepAngle);

		public void AddPie (float x, float y, float width, float height, float startAngle, float sweepAngle)
		{
			if (!(width > 1.1920929e-07f) || !(height > 1.1920929e-07f)) Invalid ();
			gp.AddPie (x, y, width, height, startAngle, sweepAngle);
		}

		//
		// AddPolygon
		//
		public void AddPolygon (Point [] points)
		{
			if (points == null)
				throw new ArgumentNullException ("points");
			if (!gp.AddPolygon (ToF (points), points.Length)) Invalid ();
		}

		public void AddPolygon (PointF [] points)
		{
			if (points == null)
				throw new ArgumentNullException ("points");
			if (!gp.AddPolygon (points, points.Length)) Invalid ();
		}

		//
		// AddRectangle
		//
		public void AddRectangle (Rectangle rect) => gp.AddRects (new [] { new RectangleF (rect.X, rect.Y, rect.Width, rect.Height) });

		public void AddRectangle (RectangleF rect) => gp.AddRects (new [] { rect });

		//
		// AddRectangles
		//
		public void AddRectangles (Rectangle [] rects)
		{
			if (rects == null)
				throw new ArgumentNullException ("rects");
			if (rects.Length == 0)
				throw new ArgumentException ("rects");
			var f = new RectangleF [rects.Length];
			for (int i = 0; i < f.Length; i++) f [i] = rects [i];
			gp.AddRects (f);
		}

		public void AddRectangles (RectangleF [] rects)
		{
			if (rects == null)
				throw new ArgumentNullException ("rects");
			if (rects.Length == 0)
				throw new ArgumentException ("rects");
			gp.AddRects (rects);
		}

		//
		// AddPath
		//
		public void AddPath (GraphicsPath addingPath, bool connect)
		{
			if (addingPath == null)
				throw new ArgumentNullException ("addingPath");
			gp.AddPath (addingPath.gp, connect);
		}

		public PointF GetLastPoint ()
		{
			if (gp.Count == 0) Invalid ();
			return gp.Points [gp.Count - 1];
		}

		//
		// AddClosedCurve
		//
		public void AddClosedCurve (Point [] points) => AddClosedCurve (points, 0.5f);

		public void AddClosedCurve (PointF [] points) => AddClosedCurve (points, 0.5f);

		public void AddClosedCurve (Point [] points, float tension)
		{
			if (points == null)
				throw new ArgumentNullException ("points");
			if (!gp.AddClosedCurve (ToF (points), points.Length, tension)) Invalid ();
		}

		public void AddClosedCurve (PointF [] points, float tension)
		{
			if (points == null)
				throw new ArgumentNullException ("points");
			if (!gp.AddClosedCurve (points, points.Length, tension)) Invalid ();
		}

		//
		// AddCurve
		//
		public void AddCurve (Point [] points) => AddCurve (points, 0.5f);

		public void AddCurve (PointF [] points) => AddCurve (points, 0.5f);

		public void AddCurve (Point [] points, float tension)
		{
			if (points == null)
				throw new ArgumentNullException ("points");
			if (!gp.AddCurve (ToF (points), points.Length, tension, 0, points.Length - 1)) Invalid ();
		}

		public void AddCurve (PointF [] points, float tension)
		{
			if (points == null)
				throw new ArgumentNullException ("points");
			if (!gp.AddCurve (points, points.Length, tension, 0, points.Length - 1)) Invalid ();
		}

		public void AddCurve (Point [] points, int offset, int numberOfSegments, float tension)
		{
			if (points == null)
				throw new ArgumentNullException ("points");
			if (!gp.AddCurve (ToF (points), points.Length, tension, offset, numberOfSegments)) Invalid ();
		}

		public void AddCurve (PointF [] points, int offset, int numberOfSegments, float tension)
		{
			if (points == null)
				throw new ArgumentNullException ("points");
			if (!gp.AddCurve (points, points.Length, tension, offset, numberOfSegments)) Invalid ();
		}

		public void Reset () => gp.Reset ();

		public void Reverse () => gp.Reverse ();

		public void Transform (Matrix matrix)
		{
			if (matrix == null)
				throw new ArgumentNullException ("matrix");
			gp.Transform (matrix.Gp);
		}

		public void AddString (string s, FontFamily family, int style, float emSize, Point origin, StringFormat format)
			=> AddString (s, family, style, emSize, new RectangleF (origin.X, origin.Y, 0, 0), format);

		public void AddString (string s, FontFamily family, int style, float emSize, PointF origin, StringFormat format)
			=> AddString (s, family, style, emSize, new RectangleF (origin.X, origin.Y, 0, 0), format);

		public void AddString (string s, FontFamily family, int style, float emSize, Rectangle layoutRect, StringFormat format)
			=> AddString (s, family, style, emSize, new RectangleF (layoutRect.X, layoutRect.Y, layoutRect.Width, layoutRect.Height), format);

		public void AddString (string s, FontFamily family, int style, float emSize, RectangleF layoutRect, StringFormat format)
		{
			if (family == null)
				throw new ArgumentException ("family");
			// note: the NullReferenceException on s.Length is the expected (MS) exception
			if (s.Length == 0) return;
			GpPathText.AddString (gp, s, family, style, emSize, layoutRect, format);
		}

		public void ClearMarkers () => gp.ClearMarkers ();

		public void CloseAllFigures () => gp.CloseFigures ();

		public void CloseFigure () => gp.CloseFigure ();

		public void Flatten () => Flatten (null, FlatnessDefault);

		public void Flatten (Matrix matrix) => Flatten (matrix, FlatnessDefault);

		public void Flatten (Matrix matrix, float flatness) => gp.Flatten (matrix?.Gp, flatness);

		public RectangleF GetBounds () => GetBounds (null, null);

		public RectangleF GetBounds (Matrix matrix) => GetBounds (matrix, null);

		/// <summary>GpPath::GetBounds: the control points' bounds (transformed as a rectangle's
		/// corners), widened by what a pen adds.</summary>
		public RectangleF GetBounds (Matrix matrix, Pen pen)
		{
			if (gp.Count == 0) return RectangleF.Empty;
			if (pen != null) return GpPen.WidenedBounds (gp, pen, matrix?.Gp);
			RectangleF b = gp.ControlBounds ();
			float l = b.X, t = b.Y, r = b.X + b.Width, bt = b.Y + b.Height;
			if (matrix != null && matrix.Gp.Complexity != 0) {
				GpMatrix m = matrix.Gp;
				if (m.IsTranslateScale) {
					float x0 = l, y0 = t, x1 = r, y1 = bt;
					m.Transform (ref x0, ref y0);
					m.Transform (ref x1, ref y1);
					l = Math.Min (x0, x1); r = Math.Max (x0, x1); t = Math.Min (y0, y1); bt = Math.Max (y0, y1);
				} else {
					var p = new [] { new PointF (l, t), new PointF (r, t), new PointF (r, bt), new PointF (l, bt) };
					m.Transform (p);
					l = r = p [0].X; t = bt = p [0].Y;
					for (int i = 1; i < 4; i++) { l = Math.Min (l, p [i].X); r = Math.Max (r, p [i].X); t = Math.Min (t, p [i].Y); bt = Math.Max (bt, p [i].Y); }
				}
			}
			float w = r - l, h = bt - t;
			if (w <= 1.1920929e-07f) w = 0f;
			if (h <= 1.1920929e-07f) h = 0f;
			return new RectangleF (l, t, w, h);
		}

		public bool IsOutlineVisible (Point point, Pen pen) => IsOutlineVisible ((float) point.X, point.Y, pen, null);

		public bool IsOutlineVisible (PointF point, Pen pen) => IsOutlineVisible (point.X, point.Y, pen, null);

		public bool IsOutlineVisible (int x, int y, Pen pen) => IsOutlineVisible ((float) x, y, pen, null);

		public bool IsOutlineVisible (float x, float y, Pen pen) => IsOutlineVisible (x, y, pen, null);

		public bool IsOutlineVisible (Point pt, Pen pen, Graphics graphics) => IsOutlineVisible ((float) pt.X, pt.Y, pen, graphics);

		public bool IsOutlineVisible (PointF pt, Pen pen, Graphics graphics) => IsOutlineVisible (pt.X, pt.Y, pen, graphics);

		public bool IsOutlineVisible (int x, int y, Pen pen, Graphics graphics) => IsOutlineVisible ((float) x, y, pen, graphics);

		/// <summary>GpPath::IsOutlineVisible: the path widened by the pen (dashes off) under the
		/// graphics' world-to-device transform, and the point tested against that.</summary>
		public bool IsOutlineVisible (float x, float y, Pen pen, Graphics graphics)
		{
			if (pen == null)
				throw new ArgumentNullException ("pen");
			GpMatrix m = graphics == null ? GpMatrix.CreateIdentity () : graphics.RegionWorldToDevice ();
			GpPath wide = GpPen.Widen (gp, pen, m, 0.25f, solid: true);
			if (wide == null) return false;
			m.Transform (ref x, ref y);
			DpRegion d = GpRegion.FromPath (wide.PointArray (), wide.TypeArray (), FillMode.Winding).Device (GpMatrix.CreateIdentity ());
			return d.Contains ((int) (x + 0.5f), (int) (y + 0.5f));
		}

		public bool IsVisible (Point point) => IsVisible ((float) point.X, point.Y, null);

		public bool IsVisible (PointF point) => IsVisible (point.X, point.Y, null);

		public bool IsVisible (int x, int y) => IsVisible ((float) x, y, null);

		public bool IsVisible (float x, float y) => IsVisible (x, y, null);

		public bool IsVisible (Point pt, Graphics graphics) => IsVisible ((float) pt.X, pt.Y, graphics);

		public bool IsVisible (PointF pt, Graphics graphics) => IsVisible (pt.X, pt.Y, graphics);

		public bool IsVisible (int x, int y, Graphics graphics) => IsVisible ((float) x, y, graphics);

		/// <summary>GpPath::IsVisible: the path as a region under the graphics' world-to-device
		/// transform, tested as a region tests a point.</summary>
		public bool IsVisible (float x, float y, Graphics graphics)
		{
			GpMatrix m = graphics == null ? GpMatrix.CreateIdentity () : graphics.RegionWorldToDevice ();
			DpRegion d = GpRegion.FromPath (gp.PointArray (), gp.TypeArray (), gp.FillMode).Device (m);
			m.Transform (ref x, ref y);
			return d.Contains ((int) (x + 0.5f), (int) (y + 0.5f));
		}

		public void SetMarkers () => gp.SetMarker ();

		public void StartFigure () => gp.StartFigure ();

		public void Warp (PointF[] destPoints, RectangleF srcRect)
			=> Warp (destPoints, srcRect, null, WarpMode.Perspective, FlatnessDefault);

		public void Warp (PointF[] destPoints, RectangleF srcRect, Matrix matrix)
			=> Warp (destPoints, srcRect, matrix, WarpMode.Perspective, FlatnessDefault);

		public void Warp (PointF[] destPoints, RectangleF srcRect, Matrix matrix, WarpMode warpMode)
			=> Warp (destPoints, srcRect, matrix, warpMode, FlatnessDefault);

		public void Warp (PointF[] destPoints, RectangleF srcRect, Matrix matrix, WarpMode warpMode, float flatness)
		{
			if (destPoints == null)
				throw new ArgumentNullException ("destPoints");
			GpPathWarp.Warp (gp, matrix?.Gp, destPoints, srcRect, warpMode, flatness);
		}

		// .NET widens at 2/3 (its GraphicsPath.Flatness), not GDI+'s FlatnessDefault.
		const float WidenFlatness = 2.0f / 3.0f;

		public void Widen (Pen pen) => Widen (pen, null, WidenFlatness);

		public void Widen (Pen pen, Matrix matrix) => Widen (pen, matrix, WidenFlatness);

		public void Widen (Pen pen, Matrix matrix, float flatness)
		{
			if (pen == null)
				throw new ArgumentNullException ("pen");
			if (PointCount == 0)
				return;
			GpPath wide = GpPen.Widen (gp, pen, matrix?.Gp ?? GpMatrix.CreateIdentity (), flatness, solid: false);
			if (wide == null) return;
			gp.Points.Clear (); gp.Points.AddRange (wide.Points);
			gp.Types.Clear (); gp.Types.AddRange (wide.Types);
			gp.FillMode = FillMode.Winding;   // GpPath::Widen: the widened path fills winding
			gp.Revalidate ();
		}
	}
}
