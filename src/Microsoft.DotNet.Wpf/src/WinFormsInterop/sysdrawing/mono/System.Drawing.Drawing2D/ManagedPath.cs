// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// GraphicsPath WITHOUT GDI+.
//
// In the browser there is no libgdiplus, and System.Drawing runs "managed-only" (GDIPlus.Initialized
// is false): Graphics records into the WebGPU scene instead of drawing into a GDI+ surface. But a
// GraphicsPath was still a handle to a GDI+ object, so `new GraphicsPath()` threw
// DllNotFoundException -- and the recorder flattens every FillPath/DrawPath THROUGH GDI+ (Clone,
// Flatten, PathPoints). Since ThemeWin11 draws its check boxes (and rounded buttons) as paths, a
// WinForms form in the browser died on its first paint, and with it the WPF gallery's WinForms card.
//
// This is the path model GDI+ keeps -- points plus a type byte each -- held in managed memory behind
// a fake handle, with the GdipXxxPath entry points implemented against it (GDIPlus routes to them
// when GDI+ is absent; see the wrappers in gdipFunctions). It follows GDI+'s own semantics where it
// matters for pixels: a line, arc, Bezier or curve CONTINUES the open figure (its first point joined
// to the last by a line), a rectangle, ellipse, pie or polygon is a closed figure of its own; arc
// angles are GDI+'s ray angles on the ellipse, not parametric ones; cardinal splines use GDI+'s
// tension convention (a third of tension times the neighbour chord). Operations that need other
// GDI+ objects -- a Matrix, a Pen, a font for AddString -- still report NotImplemented here.
//

using System.Collections.Generic;

namespace System.Drawing.Drawing2D
{
	internal static class ManagedPath
	{
		private sealed class PathState
		{
			public readonly List<PointF> Points = new List<PointF> ();
			public readonly List<byte> Types = new List<byte> ();
			public FillMode FillMode;
			public bool StartNew = true;        // the next add starts a figure rather than continuing one

			public PathState Clone ()
			{
				var c = new PathState { FillMode = FillMode, StartNew = StartNew };
				c.Points.AddRange (Points);
				c.Types.AddRange (Types);
				return c;
			}
		}

		private const byte Start = 0, Line = 1, Bezier = 3, TypeMask = 7, Marker = 0x20, Close = 0x80;

		// Handles are only ever compared and looked up; they never reach native code, since GDI+ is
		// not there to receive them.
		private static readonly Dictionary<IntPtr, PathState> s_paths = new Dictionary<IntPtr, PathState> ();
		private static long s_next = 0x4D500000;

		private static IntPtr Register (PathState s)
		{
			lock (s_paths) {
				var h = new IntPtr (++s_next);
				s_paths [h] = s;
				return h;
			}
		}

		private static PathState Get (IntPtr h)
		{
			lock (s_paths)
				return s_paths.TryGetValue (h, out PathState s) ? s : null;
		}

		/// <summary>True for a handle this class issued -- a GDI+ call that is handed one while GDI+ is
		/// present would be a bug, and this lets the wrappers tell.</summary>
		internal static bool Owns (IntPtr h) => Get (h) != null;

		// ---- lifetime ------------------------------------------------------------------

		internal static Status Create (FillMode mode, out IntPtr path)
		{
			path = Register (new PathState { FillMode = mode });
			return Status.Ok;
		}

		internal static Status Create (PointF [] points, byte [] types, int count, FillMode mode, out IntPtr path)
		{
			path = IntPtr.Zero;
			if (points == null || types == null || count < 0 || count > points.Length || count > types.Length)
				return Status.InvalidParameter;
			var s = new PathState { FillMode = mode };
			for (int i = 0; i < count; i++) { s.Points.Add (points [i]); s.Types.Add (types [i]); }
			s.StartNew = count == 0 || (types [count - 1] & Close) != 0;
			path = Register (s);
			return Status.Ok;
		}

		internal static Status Create (Point [] points, byte [] types, int count, FillMode mode, out IntPtr path)
			=> Create (ToF (points, count), types, count, mode, out path);

		internal static Status Clone (IntPtr path, out IntPtr clone)
		{
			clone = IntPtr.Zero;
			PathState s = Get (path);
			if (s == null) return Status.InvalidParameter;
			clone = Register (s.Clone ());
			return Status.Ok;
		}

		internal static Status Delete (IntPtr path)
		{
			lock (s_paths) s_paths.Remove (path);
			return Status.Ok;
		}

		internal static Status Reset (IntPtr path)
		{
			PathState s = Get (path);
			if (s == null) return Status.InvalidParameter;
			s.Points.Clear (); s.Types.Clear (); s.StartNew = true; s.FillMode = FillMode.Alternate;
			return Status.Ok;
		}

		// ---- queries -------------------------------------------------------------------

		internal static Status GetPointCount (IntPtr path, out int count)
		{
			PathState s = Get (path);
			count = s?.Points.Count ?? 0;
			return s == null ? Status.InvalidParameter : Status.Ok;
		}

		internal static Status GetTypes (IntPtr path, byte [] types, int count)
		{
			PathState s = Get (path);
			if (s == null || types == null || count > s.Types.Count || count > types.Length) return Status.InvalidParameter;
			s.Types.CopyTo (0, types, 0, count);
			return Status.Ok;
		}

		internal static Status GetPoints (IntPtr path, PointF [] points, int count)
		{
			PathState s = Get (path);
			if (s == null || points == null || count > s.Points.Count || count > points.Length) return Status.InvalidParameter;
			s.Points.CopyTo (0, points, 0, count);
			return Status.Ok;
		}

		internal static Status GetPoints (IntPtr path, Point [] points, int count)
		{
			PathState s = Get (path);
			if (s == null || points == null || count > s.Points.Count || count > points.Length) return Status.InvalidParameter;
			for (int i = 0; i < count; i++)
				points [i] = new Point ((int) Math.Round (s.Points [i].X), (int) Math.Round (s.Points [i].Y));
			return Status.Ok;
		}

		internal static Status GetFillMode (IntPtr path, out FillMode mode)
		{
			PathState s = Get (path);
			mode = s?.FillMode ?? FillMode.Alternate;
			return s == null ? Status.InvalidParameter : Status.Ok;
		}

		internal static Status SetFillMode (IntPtr path, FillMode mode)
		{
			PathState s = Get (path);
			if (s == null) return Status.InvalidParameter;
			s.FillMode = mode;
			return Status.Ok;
		}

		internal static Status GetLastPoint (IntPtr path, out PointF last)
		{
			PathState s = Get (path);
			last = PointF.Empty;
			if (s == null || s.Points.Count == 0) return Status.InvalidParameter;
			last = s.Points [s.Points.Count - 1];
			return Status.Ok;
		}

		/// <summary>The control-point bounds, as GDI+ reports without a pen. A matrix or a pen needs
		/// the GDI+ objects behind them, which are not here.</summary>
		internal static Status GetBounds (IntPtr path, out RectangleF bounds, IntPtr matrix, IntPtr pen)
		{
			bounds = RectangleF.Empty;
			PathState s = Get (path);
			if (s == null) return Status.InvalidParameter;
			if (matrix != IntPtr.Zero || pen != IntPtr.Zero) return Status.NotImplemented;
			if (s.Points.Count == 0) return Status.Ok;
			float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
			foreach (PointF p in s.Points) {
				x0 = Math.Min (x0, p.X); y0 = Math.Min (y0, p.Y);
				x1 = Math.Max (x1, p.X); y1 = Math.Max (y1, p.Y);
			}
			bounds = new RectangleF (x0, y0, x1 - x0, y1 - y0);
			return Status.Ok;
		}

		internal static Status GetBounds (IntPtr path, out Rectangle bounds, IntPtr matrix, IntPtr pen)
		{
			Status st = GetBounds (path, out RectangleF f, matrix, pen);
			bounds = Rectangle.Truncate (f);
			return st;
		}

		/// <summary>Whether a point is inside the filled path, by its fill mode, on the flattened
		/// outline (every figure implicitly closed, as a fill closes it).</summary>
		internal static Status IsVisible (IntPtr path, float x, float y, out bool result)
		{
			result = false;
			PathState s = Get (path);
			if (s == null) return Status.InvalidParameter;
			int winding = 0, crossings = 0;
			foreach (List<PointF> poly in FlattenFigures (s, FlatnessDefault)) {
				for (int i = 0, n = poly.Count; i < n; i++) {
					PointF a = poly [i], b = poly [(i + 1) % n];
					if ((a.Y <= y) == (b.Y <= y)) continue;
					float xAt = a.X + (y - a.Y) * (b.X - a.X) / (b.Y - a.Y);
					if (xAt <= x) continue;
					crossings++;
					winding += b.Y > a.Y ? 1 : -1;
				}
			}
			result = s.FillMode == FillMode.Winding ? winding != 0 : (crossings & 1) != 0;
			return Status.Ok;
		}

		// ---- figures -------------------------------------------------------------------

		internal static Status StartFigure (IntPtr path)
		{
			PathState s = Get (path);
			if (s == null) return Status.InvalidParameter;
			s.StartNew = true;
			return Status.Ok;
		}

		internal static Status CloseFigure (IntPtr path)
		{
			PathState s = Get (path);
			if (s == null) return Status.InvalidParameter;
			int n = s.Types.Count;
			if (n > 0 && (s.Types [n - 1] & TypeMask) != Start)
				s.Types [n - 1] = (byte) (s.Types [n - 1] | Close);
			s.StartNew = true;
			return Status.Ok;
		}

		internal static Status CloseFigures (IntPtr path)
		{
			PathState s = Get (path);
			if (s == null) return Status.InvalidParameter;
			for (int i = 0; i < s.Types.Count; i++) {
				bool lastOfFigure = i + 1 == s.Types.Count || (s.Types [i + 1] & TypeMask) == Start;
				if (lastOfFigure && (s.Types [i] & TypeMask) != Start)
					s.Types [i] = (byte) (s.Types [i] | Close);
			}
			s.StartNew = true;
			return Status.Ok;
		}

		internal static Status SetMarker (IntPtr path)
		{
			PathState s = Get (path);
			if (s == null) return Status.InvalidParameter;
			if (s.Types.Count > 0) s.Types [s.Types.Count - 1] = (byte) (s.Types [s.Types.Count - 1] | Marker);
			return Status.Ok;
		}

		internal static Status ClearMarkers (IntPtr path)
		{
			PathState s = Get (path);
			if (s == null) return Status.InvalidParameter;
			for (int i = 0; i < s.Types.Count; i++) s.Types [i] = (byte) (s.Types [i] & ~Marker);
			return Status.Ok;
		}

		/// <summary>Reverses every figure, and their order, keeping each segment's kind with the segment
		/// (the type byte of a point describes the segment that ENDS at it, so reversing shifts it).</summary>
		internal static Status Reverse (IntPtr path)
		{
			PathState s = Get (path);
			if (s == null) return Status.InvalidParameter;
			var pts = new List<PointF> (s.Points.Count);
			var types = new List<byte> (s.Types.Count);
			var figures = new List<(int Start, int End)> ();
			for (int i = 0; i < s.Types.Count; i++)
				if ((s.Types [i] & TypeMask) == Start || i == 0) figures.Add ((i, i));
				else figures [figures.Count - 1] = (figures [figures.Count - 1].Start, i);
			for (int f = figures.Count - 1; f >= 0; f--) {
				(int a, int b) = figures [f];
				bool closed = (s.Types [b] & Close) != 0;
				for (int k = b; k >= a; k--) {
					pts.Add (s.Points [k]);
					if (k == b) types.Add (Start);
					else types.Add ((byte) (s.Types [k + 1] & (TypeMask | Marker)));
				}
				if (closed && b > a) types [types.Count - 1] = (byte) (types [types.Count - 1] | Close);
			}
			s.Points.Clear (); s.Points.AddRange (pts);
			s.Types.Clear (); s.Types.AddRange (types);
			return Status.Ok;
		}

		// ---- open segments: they continue the current figure ------------------------------

		private static void Begin (PathState s, PointF first)
		{
			if (s.StartNew || s.Points.Count == 0) {
				s.Points.Add (first); s.Types.Add (Start);
				s.StartNew = false;
				return;
			}
			PointF last = s.Points [s.Points.Count - 1];
			if (last != first) { s.Points.Add (first); s.Types.Add (Line); }
		}

		internal static Status AddLine (IntPtr path, float x1, float y1, float x2, float y2)
		{
			PathState s = Get (path);
			if (s == null) return Status.InvalidParameter;
			Begin (s, new PointF (x1, y1));
			s.Points.Add (new PointF (x2, y2)); s.Types.Add (Line);
			return Status.Ok;
		}

		internal static Status AddLines (IntPtr path, PointF [] points, int count)
		{
			PathState s = Get (path);
			if (s == null || points == null || count <= 0 || count > points.Length) return Status.InvalidParameter;
			Begin (s, points [0]);
			for (int i = 1; i < count; i++) { s.Points.Add (points [i]); s.Types.Add (Line); }
			return Status.Ok;
		}

		internal static Status AddLines (IntPtr path, Point [] points, int count) => AddLines (path, ToF (points, count), count);

		internal static Status AddBezier (IntPtr path, float x1, float y1, float x2, float y2, float x3, float y3, float x4, float y4)
		{
			PathState s = Get (path);
			if (s == null) return Status.InvalidParameter;
			Begin (s, new PointF (x1, y1));
			s.Points.Add (new PointF (x2, y2)); s.Types.Add (Bezier);
			s.Points.Add (new PointF (x3, y3)); s.Types.Add (Bezier);
			s.Points.Add (new PointF (x4, y4)); s.Types.Add (Bezier);
			return Status.Ok;
		}

		internal static Status AddBeziers (IntPtr path, PointF [] points, int count)
		{
			PathState s = Get (path);
			if (s == null || points == null || count < 4 || (count - 1) % 3 != 0 || count > points.Length) return Status.InvalidParameter;
			Begin (s, points [0]);
			for (int i = 1; i < count; i++) { s.Points.Add (points [i]); s.Types.Add (Bezier); }
			return Status.Ok;
		}

		internal static Status AddBeziers (IntPtr path, Point [] points, int count) => AddBeziers (path, ToF (points, count), count);

		internal static Status AddArc (IntPtr path, float x, float y, float w, float h, float start, float sweep)
		{
			PathState s = Get (path);
			if (s == null) return Status.InvalidParameter;
			AppendArc (s, x, y, w, h, start, sweep, connect: true);
			return Status.Ok;
		}

		internal static Status AddCurve (IntPtr path, PointF [] points, int count, int offset, int segments, float tension)
		{
			PathState s = Get (path);
			if (s == null || points == null || count < 2 || count > points.Length) return Status.InvalidParameter;
			if (offset < 0 || segments < 1 || offset + segments >= count) return Status.InvalidParameter;
			float t = tension / 3f;
			Begin (s, points [offset]);
			for (int i = offset; i < offset + segments; i++) {
				PointF p0 = points [Math.Max (i - 1, 0)], p1 = points [i], p2 = points [i + 1], p3 = points [Math.Min (i + 2, count - 1)];
				s.Points.Add (new PointF (p1.X + t * (p2.X - p0.X), p1.Y + t * (p2.Y - p0.Y))); s.Types.Add (Bezier);
				s.Points.Add (new PointF (p2.X - t * (p3.X - p1.X), p2.Y - t * (p3.Y - p1.Y))); s.Types.Add (Bezier);
				s.Points.Add (p2); s.Types.Add (Bezier);
			}
			return Status.Ok;
		}

		internal static Status AddCurve (IntPtr path, Point [] points, int count, int offset, int segments, float tension)
			=> AddCurve (path, ToF (points, count), count, offset, segments, tension);

		// ---- closed shapes: each a figure of its own ---------------------------------------

		internal static Status AddClosedCurve (IntPtr path, PointF [] points, int count, float tension)
		{
			PathState s = Get (path);
			if (s == null || points == null || count < 3 || count > points.Length) return Status.InvalidParameter;
			float t = tension / 3f;
			s.StartNew = true;
			Begin (s, points [0]);
			for (int i = 0; i < count; i++) {
				PointF p0 = points [(i - 1 + count) % count], p1 = points [i], p2 = points [(i + 1) % count], p3 = points [(i + 2) % count];
				s.Points.Add (new PointF (p1.X + t * (p2.X - p0.X), p1.Y + t * (p2.Y - p0.Y))); s.Types.Add (Bezier);
				s.Points.Add (new PointF (p2.X - t * (p3.X - p1.X), p2.Y - t * (p3.Y - p1.Y))); s.Types.Add (Bezier);
				s.Points.Add (p2); s.Types.Add (Bezier);
			}
			return CloseFigure (path);
		}

		internal static Status AddClosedCurve (IntPtr path, Point [] points, int count, float tension)
			=> AddClosedCurve (path, ToF (points, count), count, tension);

		internal static Status AddRectangle (IntPtr path, float x, float y, float w, float h)
		{
			PathState s = Get (path);
			if (s == null) return Status.InvalidParameter;
			if (w == 0 || h == 0) return Status.Ok;    // GDI+ adds nothing for an empty rectangle
			s.StartNew = true;
			Begin (s, new PointF (x, y));
			s.Points.Add (new PointF (x + w, y)); s.Types.Add (Line);
			s.Points.Add (new PointF (x + w, y + h)); s.Types.Add (Line);
			s.Points.Add (new PointF (x, y + h)); s.Types.Add (Line);
			return CloseFigure (path);
		}

		internal static Status AddRectangles (IntPtr path, RectangleF [] rects, int count)
		{
			if (rects == null || count > rects.Length) return Status.InvalidParameter;
			for (int i = 0; i < count; i++) {
				Status st = AddRectangle (path, rects [i].X, rects [i].Y, rects [i].Width, rects [i].Height);
				if (st != Status.Ok) return st;
			}
			return Status.Ok;
		}

		internal static Status AddRectangles (IntPtr path, Rectangle [] rects, int count)
		{
			if (rects == null || count > rects.Length) return Status.InvalidParameter;
			for (int i = 0; i < count; i++) {
				Status st = AddRectangle (path, rects [i].X, rects [i].Y, rects [i].Width, rects [i].Height);
				if (st != Status.Ok) return st;
			}
			return Status.Ok;
		}

		internal static Status AddEllipse (IntPtr path, float x, float y, float w, float h)
		{
			PathState s = Get (path);
			if (s == null) return Status.InvalidParameter;
			s.StartNew = true;
			AppendArc (s, x, y, w, h, 0f, 360f, connect: true);
			return CloseFigure (path);
		}

		internal static Status AddPie (IntPtr path, float x, float y, float w, float h, float start, float sweep)
		{
			PathState s = Get (path);
			if (s == null) return Status.InvalidParameter;
			s.StartNew = true;
			Begin (s, new PointF (x + w / 2f, y + h / 2f));
			AppendArc (s, x, y, w, h, start, sweep, connect: true);
			return CloseFigure (path);
		}

		internal static Status AddPolygon (IntPtr path, PointF [] points, int count)
		{
			PathState s = Get (path);
			if (s == null || points == null || count < 3 || count > points.Length) return Status.InvalidParameter;
			s.StartNew = true;
			Begin (s, points [0]);
			for (int i = 1; i < count; i++) { s.Points.Add (points [i]); s.Types.Add (Line); }
			return CloseFigure (path);
		}

		internal static Status AddPolygon (IntPtr path, Point [] points, int count) => AddPolygon (path, ToF (points, count), count);

		internal static Status AddPath (IntPtr path, IntPtr adding, bool connect)
		{
			PathState s = Get (path), a = Get (adding);
			if (s == null || a == null) return Status.InvalidParameter;
			for (int i = 0; i < a.Points.Count; i++) {
				byte t = a.Types [i];
				if (i == 0 && connect && !s.StartNew && s.Points.Count > 0)
					t = (byte) ((t & ~TypeMask) | Line);
				s.Points.Add (a.Points [i]); s.Types.Add (t);
			}
			if (a.Points.Count > 0) s.StartNew = a.StartNew;
			return Status.Ok;
		}

		/// <summary>Replaces every Bezier with line segments no further than
		/// <paramref name="flatness"/> from the curve. A transform needs a GDI+ Matrix, so only the
		/// identity (IntPtr.Zero) is accepted.</summary>
		internal static Status Flatten (IntPtr path, IntPtr matrix, float flatness)
		{
			PathState s = Get (path);
			if (s == null) return Status.InvalidParameter;
			if (matrix != IntPtr.Zero) return Status.NotImplemented;
			var pts = new List<PointF> (s.Points.Count);
			var types = new List<byte> (s.Types.Count);
			for (int i = 0; i < s.Points.Count; i++) {
				byte t = s.Types [i];
				if ((t & TypeMask) == Bezier && i + 2 < s.Points.Count && pts.Count > 0) {
					PointF p0 = pts [pts.Count - 1];
					byte endFlags = (byte) (s.Types [i + 2] & (Close | Marker));
					var flat = new List<PointF> ();
					Subdivide (p0, s.Points [i], s.Points [i + 1], s.Points [i + 2], Math.Max (flatness, 1e-3f), flat, 0);
					for (int k = 0; k < flat.Count; k++) {
						pts.Add (flat [k]);
						types.Add (k == flat.Count - 1 ? (byte) (Line | endFlags) : Line);
					}
					i += 2;
					continue;
				}
				pts.Add (s.Points [i]); types.Add (t);
			}
			s.Points.Clear (); s.Points.AddRange (pts);
			s.Types.Clear (); s.Types.AddRange (types);
			return Status.Ok;
		}

		/// <summary>Applies an affine matrix given as GDI+'s six elements (m11 m12 m21 m22 dx dy).</summary>
		internal static Status Transform (IntPtr path, float [] m)
		{
			PathState s = Get (path);
			if (s == null || m == null || m.Length < 6) return Status.InvalidParameter;
			for (int i = 0; i < s.Points.Count; i++) {
				PointF p = s.Points [i];
				s.Points [i] = new PointF (p.X * m [0] + p.Y * m [2] + m [4], p.X * m [1] + p.Y * m [3] + m [5]);
			}
			return Status.Ok;
		}

		/// <summary>The path's points, types and fill mode, copied (for a native GDI+ copy).</summary>
		internal static bool Snapshot (IntPtr path, out PointF [] points, out byte [] types, out FillMode mode)
		{
			PathState s = Get (path);
			points = s?.Points.ToArray ();
			types = s?.Types.ToArray ();
			mode = s?.FillMode ?? FillMode.Alternate;
			return s != null;
		}

		/// <summary>Replaces the path's contents (what a GDI+ operation on a native copy produced).</summary>
		internal static void Replace (IntPtr path, PointF [] points, byte [] types)
		{
			PathState s = Get (path);
			if (s == null) return;
			s.Points.Clear (); s.Points.AddRange (points);
			s.Types.Clear (); s.Types.AddRange (types);
			s.StartNew = types.Length == 0 || (types [types.Length - 1] & Close) != 0;
		}

		// ---- geometry -------------------------------------------------------------------

		internal const float FlatnessDefault = 0.25f;

		/// <summary>An elliptical arc as cubic Beziers of at most 90 degrees each. GDI+'s angles are
		/// RAY angles -- the arc starts where a ray at that angle from the centre meets the ellipse --
		/// so they are converted to the ellipse's parameter before the usual arc-to-Bezier formula.</summary>
		private static void AppendArc (PathState s, float x, float y, float w, float h, float startDeg, float sweepDeg, bool connect)
		{
			float rx = w / 2f, ry = h / 2f, cx = x + rx, cy = y + ry;
			if (sweepDeg > 360f) sweepDeg = 360f;
			if (sweepDeg < -360f) sweepDeg = -360f;
			int n = Math.Max (1, (int) Math.Ceiling (Math.Abs (sweepDeg) / 90f - 1e-4f));
			double step = sweepDeg / n;
			for (int k = 0; k < n; k++) {
				double a0 = RayToParam (startDeg + step * k, rx, ry);
				double a1 = RayToParam (startDeg + step * (k + 1), rx, ry);
				// Keep the parametric sweep in the ray sweep's direction and size (atan2 wraps).
				double d = a1 - a0;
				double want = step * Math.PI / 180.0;
				while (d - want > Math.PI) d -= 2 * Math.PI;
				while (want - d > Math.PI) d += 2 * Math.PI;
				double alpha = 4.0 / 3.0 * Math.Tan (d / 4.0);
				double c0 = Math.Cos (a0), s0 = Math.Sin (a0), c1 = Math.Cos (a0 + d), s1 = Math.Sin (a0 + d);
				var p0 = new PointF ((float) (cx + rx * c0), (float) (cy + ry * s0));
				var p1 = new PointF ((float) (cx + rx * (c0 - alpha * s0)), (float) (cy + ry * (s0 + alpha * c0)));
				var p2 = new PointF ((float) (cx + rx * (c1 + alpha * s1)), (float) (cy + ry * (s1 - alpha * c1)));
				var p3 = new PointF ((float) (cx + rx * c1), (float) (cy + ry * s1));
				if (k == 0) Begin (s, p0);
				s.Points.Add (p1); s.Types.Add (Bezier);
				s.Points.Add (p2); s.Types.Add (Bezier);
				s.Points.Add (p3); s.Types.Add (Bezier);
			}
		}

		private static double RayToParam (double deg, float rx, float ry)
		{
			double r = deg * Math.PI / 180.0;
			if (rx <= 0 || ry <= 0) return r;
			return Math.Atan2 (rx * Math.Sin (r), ry * Math.Cos (r));
		}

		private static void Subdivide (PointF p0, PointF p1, PointF p2, PointF p3, float tol, List<PointF> outPts, int depth)
		{
			// Flat enough when both inner control points lie within tol of the chord.
			float dx = p3.X - p0.X, dy = p3.Y - p0.Y;
			float len = (float) Math.Sqrt (dx * dx + dy * dy);
			float d1, d2;
			if (len < 1e-6f) {
				d1 = Dist (p0, p1); d2 = Dist (p0, p2);
			} else {
				d1 = Math.Abs ((p1.X - p0.X) * dy - (p1.Y - p0.Y) * dx) / len;
				d2 = Math.Abs ((p2.X - p0.X) * dy - (p2.Y - p0.Y) * dx) / len;
			}
			if (depth >= 16 || Math.Max (d1, d2) <= tol) { outPts.Add (p3); return; }
			PointF a = Mid (p0, p1), b = Mid (p1, p2), c = Mid (p2, p3), ab = Mid (a, b), bc = Mid (b, c), m = Mid (ab, bc);
			Subdivide (p0, a, ab, m, tol, outPts, depth + 1);
			Subdivide (m, bc, c, p3, tol, outPts, depth + 1);
		}

		private static IEnumerable<List<PointF>> FlattenFigures (PathState s, float flatness)
		{
			List<PointF> cur = null;
			for (int i = 0; i < s.Points.Count; i++) {
				byte t = (byte) (s.Types [i] & TypeMask);
				if (t == Start) {
					if (cur != null && cur.Count > 2) yield return cur;
					cur = new List<PointF> { s.Points [i] };
				} else if (t == Bezier && i + 2 < s.Points.Count && cur != null) {
					Subdivide (cur [cur.Count - 1], s.Points [i], s.Points [i + 1], s.Points [i + 2], flatness, cur, 0);
					i += 2;
				} else {
					cur?.Add (s.Points [i]);
				}
			}
			if (cur != null && cur.Count > 2) yield return cur;
		}

		private static PointF Mid (PointF a, PointF b) => new PointF ((a.X + b.X) / 2f, (a.Y + b.Y) / 2f);
		private static float Dist (PointF a, PointF b) => (float) Math.Sqrt ((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

		private static PointF [] ToF (Point [] points, int count)
		{
			if (points == null) return null;
			var f = new PointF [Math.Min (count, points.Length)];
			for (int i = 0; i < f.Length; i++) f [i] = points [i];
			return f;
		}
	}
}
