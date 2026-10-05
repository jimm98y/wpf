//
// System.Drawing.Drawing2D.PathGradientBrush.cs
//
// Authors:
//   Dennis Hayes (dennish@Raytek.com)
//   Andreas Nahr (ClassDevelopment@A-SoftTech.com)
//   Ravindra (rkumar@novell.com)
//
// Copyright (C) 2002/3 Ximian, Inc. http://www.ximian.com
// Copyright (C) 2004,2006 Novell, Inc (http://www.novell.com)
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

using System.ComponentModel;
using System.Drawing.WebGpuBackend.Gdip;

namespace System.Drawing.Drawing2D {

	// A managed GpPathGradient (gdiplus.dll), its state kept and answered as GDI+ does:
	// InitializeBrush @18004e1f0 (points), DefaultBrush @1801a1ec0 + PrepareBrush @18000b7a0 (path),
	// SetSurroundColors @180070300, GdipSet/GetPathGradientSurroundColorsWithCount @1800697e0 /
	// @1800604f0, SetBlend @18006f460 / GetBlend @1801abc28, SetPresetBlend @180070108 /
	// GetPresetBlend @18013e7b0, the copy ctor @18000acf0. GDI+ stores the blend and the preset
	// blend reversed and inverted (1 - x, last first), so a value read back is 1 - (1 - x).
	public sealed class PathGradientBrush : Brush {

		// +0x88 the path (null for a point-built brush), +0x90 the points, +0xa0 their count
		GpPath _path;
		PointF[] _points;
		int _n;
		// +0x98 surround colours, +0xa4 "they are all the first"
		int[] _surround;
		bool _oneSurround = true;
		// +0xb4, +0x110
		int _centerArgb;
		PointF _center;
		// +0x54
		RectangleF _rect;
		// +0x50
		WrapMode _wrap;
		// +0xa8 / +0xac
		float _focusX, _focusY;
		// +0x20
		GpMatrix _xf = GpMatrix.CreateIdentity ();
		// +0xd0 count, +0xc4 single factor, +0xe0 factors, +0xf8 positions (both stored as 1 - x, reversed)
		int _count = 1;
		float _factor0 = 1f;
		float[] _factors, _positions;
		// +0x78 preset flag, +0x70 preset colours (stored reversed)
		bool _preset;
		int[] _presetArgb;
		bool _gamma;

		static Exception Status (int s) => SafeNativeMethods.Gdip.StatusException (s);

		// For the managed GDI+ engine.
		internal GpPath GpPath => _path;
		internal PointF[] GpPoints => _points;
		internal int PointCount => _n;
		internal int[] SurroundArgb => _surround;
		internal bool OneSurround => _oneSurround;
		internal int CenterArgb => _centerArgb;
		internal GpMatrix Xform { get => _xf; set => _xf = value; }
		/// <summary>The wrap mode without the public setter's check (CreateOutputSpan swaps it).</summary>
		internal WrapMode WrapInternal { get => _wrap; set => _wrap = value; }
		internal int BlendCount => _count;
		internal float BlendFactor0 => _factor0;
		internal float[] StoredFactors => _factors;
		internal float[] StoredPositions => _positions;
		internal bool PresetSet => _preset;
		internal int[] StoredPresetArgb => _presetArgb;
		internal PointF FocusScalesInternal => new PointF (_focusX, _focusY);
		/// <summary>+0x1b8: above 1, the surround colour carried on past every edge to this multiple
		/// of the edge's distance from the centre (DriverPrint::PrivateFillRect sets 1.05 on the clone
		/// it rasterizes for a printer, so the bitmap has no gap at the shape's rim); 0 otherwise.</summary>
		internal float Inflate;
		// +0x1b0: the count of the flattened points when Flatten last flattened a curved path (the
		// points at +0x90 are then already in device space)
		int _flatCount;
		internal bool PointsFlattened => _flatCount != 0;

		/// <summary>GpPathGradient::GetPoint @18015a260.</summary>
		internal bool GetPoint (int i, out PointF p)
		{
			if (i < 0 || i >= _n) { p = default; return false; }
			p = _points [i];
			return true;
		}

		/// <summary>GpPathGradient::GetSurroundColor @1801a3240: false (and <paramref name="argb"/>
		/// untouched) for an index out of range.</summary>
		internal bool GetSurroundColor (int i, ref int argb)
		{
			if (i < 0 || i >= _n) return false;
			argb = _oneSurround ? _surround [0] : _surround [i];
			return true;
		}

		/// <summary>GpPathGradient::Flatten @18000b5f8, which the gradient spans call with their
		/// brush-to-device matrix: a path without curves lends its points (still in world space); a
		/// curved one is flattened at 0.25 through the matrix and the brush keeps the device-space
		/// points, its point count changing for good, the surround colours grown with the last one
		/// (white when there was a single colour).</summary>
		internal void Flatten (in GpMatrix m)
		{
			if (_path == null) return;
			if (!_path.HasBezier) {
				_n = _path.Count;
				_points = _path.PointArray ();
				return;
			}
			int old = _n;
			GpPath flat = _path.Clone ();
			flat.Flatten (m, 0.25f);
			int n = flat.Count;
			_flatCount = n;
			_n = n;
			_points = flat.PointArray ();
			if (old < n && _surround != null) {
				int fill = old < 2 ? unchecked ((int) 0xffffffff) : _surround [old - 1];
				int[] grown = new int [n];
				Array.Copy (_surround, grown, Math.Min (_surround.Length, n));
				for (int i = old; i < n; i++) grown [i] = fill;
				_surround = grown;
			}
		}

		PathGradientBrush ()
		{
		}

		public PathGradientBrush (GraphicsPath path)
		{
			if (path == null)
				throw new ArgumentNullException ("path");

			// GdipCreatePathGradientFromPath: DefaultBrush (white centre and surround, clamp), the
			// path copied, PrepareBrush (bounds and centroid of the path's points, control points
			// included).
			_wrap = WrapMode.Clamp;
			_centerArgb = unchecked ((int) 0xffffffff);
			_path = path.gp.Clone ();
			_n = _path.Count;
			_points = _path.PointArray ();
			_surround = new int [_n];
			for (int i = 0; i < _n; i++) _surround [i] = unchecked ((int) 0xffffffff);
			if (!Prepare (_points, _n))
				throw Status (SafeNativeMethods.Gdip.OutOfMemory);
		}

		// The bounds and the centroid, as PrepareBrush / InitializeBrush sum and compare in float;
		// false when the box is empty in either direction (GDI+ leaves the brush invalid).
		bool Prepare (PointF[] p, int n)
		{
			if (n <= 0) return false;
			float sx = p [0].X, sy = p [0].Y;
			float minX = sx, maxX = sx, minY = sy, maxY = sy;
			for (int i = 1; i < n; i++) {
				float x = p [i].X, y = p [i].Y;
				sx = x + sx; sy = y + sy;
				if (x <= minX) minX = x;
				if (maxX <= x) maxX = x;
				if (y < minY) minY = y;
				if (maxY <= y) maxY = y;
			}
			_rect = new RectangleF (minX, minY, maxX - minX, maxY - minY);
			if (!(0f < maxX - minX) || !(0f < maxY - minY)) return false;
			_center = new PointF (sx / (float) n, sy / (float) n);
			return true;
		}

		public PathGradientBrush (Point [] points) : this (points, WrapMode.Clamp)
		{
		}

		public PathGradientBrush (PointF [] points) : this (points, WrapMode.Clamp)
		{
		}

		public PathGradientBrush (Point [] points, WrapMode wrapMode) : this (ToF (points), wrapMode)
		{
		}

		static PointF[] ToF (Point[] points)
		{
			if (points == null)
				throw new ArgumentNullException ("points");
			var f = new PointF [points.Length];
			for (int i = 0; i < f.Length; i++) f [i] = points [i];
			return f;
		}

		public PathGradientBrush (PointF [] points, WrapMode wrapMode)
		{
			if (points == null)
				throw new ArgumentNullException ("points");
			if ((wrapMode < WrapMode.Tile) || (wrapMode > WrapMode.Clamp))
				throw new InvalidEnumArgumentException ("WrapMode");

			// GdipCreatePathGradient -> InitializeBrush: the centre colour is the elementary brush's
			// default (opaque black), the surround colours white.
			_wrap = wrapMode;
			_centerArgb = unchecked ((int) 0xff000000);
			_n = points.Length;
			if (!Prepare (points, _n))
				throw Status (SafeNativeMethods.Gdip.OutOfMemory);
			_points = (PointF []) points.Clone ();
			_surround = new int [_n];
			for (int i = 0; i < _n; i++) _surround [i] = unchecked ((int) 0xffffffff);
		}

		// Properties

		public Blend Blend {
			get {
				// GdipGetPathGradientBlend: one entry is the factor alone; more are read back
				// reversed and inverted.
				int count = _count;
				float [] factors = new float [count];
				float [] positions = new float [count];
				if (count == 1)
					factors [0] = _factor0;
				else
					for (int i = 0; i < count; i++) {
						if (_factors != null) factors [count - 1 - i] = 1f - _factors [i];
						if (_positions != null) positions [count - 1 - i] = 1f - _positions [i];
					}
				return new Blend { Factors = factors, Positions = positions };
			}
			set {
				// no null check, MS throws a NullReferenceException here
				float [] factors = value.Factors;
				float [] positions = value.Positions;
				int count = factors.Length;

				if (count == 0 || positions.Length == 0)
					throw new ArgumentException ("Invalid Blend object. It should have at least 2 elements in each of the factors and positions arrays.");

				if (count != positions.Length)
					throw new ArgumentException ("Invalid Blend object. It should contain the same number of factors and positions values.");

				if (positions [0] != 0.0F)
					throw new ArgumentException ("Invalid Blend object. The positions array must have 0.0 as its first element.");

				if (positions [count - 1] != 1.0F)
					throw new ArgumentException ("Invalid Blend object. The positions array must have 1.0 as its last element.");

				SetBlend (factors, positions, count);
			}
		}

		void SetBlend (float[] factors, float[] positions, int count)
		{
			if (count == 1) {
				_factors = _positions = null;
				_factor0 = factors [0];
			} else {
				if (!(MathF.Abs (positions [0]) <= GpGradient.Eps) || !(MathF.Abs (1f - positions [count - 1]) <= GpGradient.Eps))
					throw Status (SafeNativeMethods.Gdip.InvalidParameter);
				_factors = new float [count];
				_positions = new float [count];
				for (int i = 0; i < count; i++) {
					_factors [count - 1 - i] = 1f - factors [i];
					_positions [count - 1 - i] = 1f - positions [i];
				}
			}
			_count = count;
			_preset = false;
			_presetArgb = null;
		}

		public Color CenterColor {
			get { return Color.FromArgb (_centerArgb); }
			set { _centerArgb = value.ToArgb (); }
		}

		public PointF CenterPoint {
			get { return _center; }
			set { _center = value; }
		}

		public PointF FocusScales {
			get { return new PointF (_focusX, _focusY); }
			set { _focusX = value.X; _focusY = value.Y; }
		}

		/// <summary>Whether the gradient is drawn in linear light.</summary>
		internal bool GammaCorrection {
			get { return _gamma; }
			set { _gamma = value; }
		}

		public ColorBlend InterpolationColors {
			get {
				int count = _preset ? _count : 0;
				// if no failure, then the "managed" minimum is 1
				if (count < 1)
					count = 1;

				int [] intcolors = new int [count];
				float [] positions = new float [count];
				// GetPresetBlend would fail with a count under 2
				if (count > 1 && _presetArgb != null && _positions != null) {
					for (int i = 0; i < count; i++) {
						intcolors [count - 1 - i] = _presetArgb [i];
						positions [count - 1 - i] = 1f - _positions [i];
					}
				}

				Color [] colors = new Color [count];
				for (int i = 0; i < count; i++)
					colors [i] = Color.FromArgb (intcolors [i]);
				return new ColorBlend { Colors = colors, Positions = positions };
			}
			set {
				// no null check, MS throws a NullReferenceException here
				Color [] colors = value.Colors;
				float [] positions = value.Positions;
				int count = colors.Length;

				if (count == 0 || positions.Length == 0)
					throw new ArgumentException ("Invalid ColorBlend object. It should have at least 2 elements in each of the colors and positions arrays.");

				if (count != positions.Length)
					throw new ArgumentException ("Invalid ColorBlend object. It should contain the same number of positions and color values.");

				if (positions [0] != 0.0F)
					throw new ArgumentException ("Invalid ColorBlend object. The positions array must have 0.0 as its first element.");

				if (positions [count - 1] != 1.0F)
					throw new ArgumentException ("Invalid ColorBlend object. The positions array must have 1.0 as its last element.");

				if (count < 2)
					throw Status (SafeNativeMethods.Gdip.InvalidParameter);
				_presetArgb = new int [count];
				_positions = new float [count];
				for (int i = 0; i < count; i++) {
					_presetArgb [count - 1 - i] = colors [i].ToArgb ();
					_positions [count - 1 - i] = 1f - positions [i];
				}
				_factors = null;
				_preset = true;
				_count = count;
			}
		}

		public RectangleF Rectangle {
			get { return _rect; }
		}

		public Color [] SurroundColors {
			get {
				// GdipGetPathGradientSurroundColorsWithCount: every point's colour, the count cut back
				// to just past the last change.
				int count = _n;
				int used = 1;
				int prev = 0;
				for (int i = 0; i < count; i++) {
					int c = _oneSurround ? _surround [0] : _surround [i];
					if (i != 0 && c != prev) used = i + 1;
					prev = c;
				}
				if (count <= 0) used = 0;
				Color [] colors = new Color [used];
				for (int i = 0; i < used; i++)
					colors [i] = Color.FromArgb (_oneSurround ? _surround [0] : _surround [i]);
				return colors;
			}
			set {
				// no null check, MS throws a NullReferenceException here
				int count = value.Length;
				// GdipSetPathGradientSurroundColorsWithCount: one to as many as there are points;
				// the rest take the last one given.
				if (count > _n || count <= 0)
					throw Status (SafeNativeMethods.Gdip.InvalidParameter);
				var all = new int [_n];
				for (int i = 0; i < _n; i++)
					all [i] = value [i < count ? i : count - 1].ToArgb ();
				SetSurroundColors (all);
			}
		}

		void SetSurroundColors (int[] colors)
		{
			_surround = colors;
			_oneSurround = true;
			for (int i = 1; i < _n; i++)
				if (colors [i] != colors [0]) { _oneSurround = false; break; }
		}

		/// <summary>Radial-gradient approximation (centre + radii, centre colour -> surround colour)
		/// for the GPU-raster recorder.</summary>
		internal void GetGpuGradient (out PointF center, out float rx, out float ry, out float[] offsets, out int[] argb)
		{
			center = CenterPoint;
			RectangleF r = Rectangle;
			rx = r.Width / 2f; ry = r.Height / 2f;
			Color[] surr = SurroundColors;
			int edge = (surr != null && surr.Length > 0) ? surr[0].ToArgb () : CenterColor.ToArgb ();
			offsets = new[] { 0f, 1f };
			argb = new[] { CenterColor.ToArgb (), edge };
		}

		public Matrix Transform {
			get { return new Matrix (_xf); }
			set {
				if (value == null)
					throw new ArgumentNullException ("Transform");
				if (!value.Gp.IsInvertible)
					throw Status (SafeNativeMethods.Gdip.InvalidParameter);
				_xf = value.Gp;
			}
		}

		public WrapMode WrapMode {
			get { return _wrap; }
			set {
				if ((value < WrapMode.Tile) || (value > WrapMode.Clamp))
					throw new InvalidEnumArgumentException ("WrapMode");
				_wrap = value;
			}
		}

		// Methods

		static bool Append (MatrixOrder order)
		{
			if ((uint) order > 1) throw Status (SafeNativeMethods.Gdip.InvalidParameter);
			return order == MatrixOrder.Append;
		}

		public void MultiplyTransform (Matrix matrix)
		{
			MultiplyTransform (matrix, MatrixOrder.Prepend);
		}

		public void MultiplyTransform (Matrix matrix, MatrixOrder order)
		{
			if (matrix == null)
				throw new ArgumentNullException ("matrix");
			if (!matrix.Gp.IsInvertible)
				throw Status (SafeNativeMethods.Gdip.InvalidParameter);
			_xf.Multiply (matrix.Gp, Append (order));
		}

		public void ResetTransform ()
		{
			_xf = GpMatrix.CreateIdentity ();
		}

		public void RotateTransform (float angle)
		{
			RotateTransform (angle, MatrixOrder.Prepend);
		}

		public void RotateTransform (float angle, MatrixOrder order)
		{
			_xf.Rotate (angle, Append (order));
		}

		public void ScaleTransform (float sx, float sy)
		{
			ScaleTransform (sx, sy, MatrixOrder.Prepend);
		}

		public void ScaleTransform (float sx, float sy, MatrixOrder order)
		{
			_xf.Scale (sx, sy, Append (order));
		}

		public void SetBlendTriangularShape (float focus)
		{
			SetBlendTriangularShape (focus, 1.0F);
		}

		public void SetBlendTriangularShape (float focus, float scale)
		{
			if (focus < 0 || focus > 1 || scale < 0 || scale > 1)
				throw new ArgumentException ("Invalid parameter passed.");
			if (!GpGradient.LinearBlendArray (focus, scale, out float[] f, out float[] p))
				throw Status (SafeNativeMethods.Gdip.InvalidParameter);
			SetBlend (f, p, f.Length);
		}

		public void SetSigmaBellShape (float focus)
		{
			SetSigmaBellShape (focus, 1.0F);
		}

		public void SetSigmaBellShape (float focus, float scale)
		{
			if (focus < 0 || focus > 1 || scale < 0 || scale > 1)
				throw new ArgumentException ("Invalid parameter passed.");
			if (!GpGradient.SigmaBlendArray (focus, scale, out float[] f, out float[] p))
				throw Status (SafeNativeMethods.Gdip.InvalidParameter);
			SetBlend (f, p, f.Length);
		}

		public void TranslateTransform (float dx, float dy)
		{
			TranslateTransform (dx, dy, MatrixOrder.Prepend);
		}

		public void TranslateTransform (float dx, float dy, MatrixOrder order)
		{
			_xf.Translate (dx, dy, Append (order));
		}

		public override object Clone ()
		{
			var c = (PathGradientBrush) MemberwiseClone ();
			c._path = _path?.Clone ();
			c._points = (PointF []) _points?.Clone ();
			c._surround = (int []) _surround?.Clone ();
			c._factors = (float []) _factors?.Clone ();
			c._positions = (float []) _positions?.Clone ();
			c._presetArgb = (int []) _presetArgb?.Clone ();
			c._flatCount = 0;
			return c;
		}
	}
}
