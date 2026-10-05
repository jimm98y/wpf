//
// System.Drawing.Drawing2D.LinearGradientBrush.cs
//
// Authors:
//   Dennis Hayes (dennish@Raytek.com)
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

	// A managed GpLineGradient (gdiplus.dll): the state GDI+ keeps, set and read back the way its
	// flat API does. GpLineGradient ctors @18006c968/18006ca58/18006caf0, SetLineGradient @18006ffc0,
	// GpLineGradient::SetBlend @18006f450 / SetPresetBlend @18000b8b0, GdipGetLineBlend @18005efd0,
	// GdipGetLinePresetBlend @18005f2a0, GpElementaryBrush::SetTransform @180050810 /
	// MultiplyTransform @18006f3d8, GdipResetLineTransform @180066930.
	public sealed class LinearGradientBrush : Brush
	{
		// +0x54 the brush rectangle
		RectangleF rectangle;
		// +0xb4 / +0xb8 the two colours (+0xbc / +0xc0 hold copies)
		Color _c1, _c2;
		// +0x50
		WrapMode _wrap = WrapMode.Tile;
		// +0x20 the brush transform: CalcLinearGradientXform's matrix, which is what Transform returns
		GpMatrix _xf = GpMatrix.CreateIdentity ();
		// +0xd0 count, +0xc4 the factor of a one-entry blend, +0xe0 factors, +0xf8 positions
		int _count = 1;
		float _factor0 = 1f;
		float[] _factors, _positions;
		// +0x78 preset flag, +0x70 preset colours (positions in _positions)
		bool _preset;
		int[] _presetArgb;
		bool _gamma;

		private bool _interpolationColorsWasSet;

		// The constructor's angle and whether it scales with the rectangle (what the brush's xform
		// was computed from), and the end points the GPU recorder shades between.
		internal PointF gradient_start, gradient_end;
		internal Color gradient_color1 => _c1;
		internal Color gradient_color2 => _c2;
		internal bool exact_known;
		internal bool InterpolationColorsWereSet => _interpolationColorsWasSet;
		internal bool user_transformed;
		internal float exact_angle;
		internal bool exact_scalable;

		// For the managed GDI+ engine.
		internal GpMatrix Xform => _xf;
		internal int BlendCount => _count;
		internal float BlendFactor0 => _factor0;
		internal float[] BlendFactors => _factors;
		internal float[] BlendPositions => _positions;
		internal bool PresetSet => _preset;
		internal int[] PresetArgb => _presetArgb;

		static Exception Status (int s) => SafeNativeMethods.Gdip.StatusException (s);

		/// <summary>Start/end points + the two endpoint colours, for the GPU-raster recorder.</summary>
		internal void GetGpuGradient (out PointF start, out PointF end, out Color c1, out Color c2)
		{ start = gradient_start; end = gradient_end; c1 = _c1; c2 = _c2; }

		/// <summary>All gradient stops (multi-stop InterpolationColors if set, else the two endpoints)
		/// as parallel offset/ARGB arrays, for the GPU-raster recorder.</summary>
		internal void GetGpuStops (out float[] offsets, out int[] argb)
		{
			if (_interpolationColorsWasSet && _preset) {
				offsets = (float []) _positions.Clone ();
				argb = (int []) _presetArgb.Clone ();
			} else if (!_preset && _count > 1 && _factors != null) {
				// A Blend is a stop list in disguise: at each position, the factor of the way from
				// the first colour to the second.
				offsets = (float []) _positions.Clone ();
				argb = new int [_count];
				for (int i = 0; i < argb.Length; i++) argb [i] = Lerp (_c1, _c2, _factors [i]).ToArgb ();
			} else {
				offsets = new[] { 0f, 1f };
				argb = new[] { _c1.ToArgb (), _c2.ToArgb () };
			}
		}

		static Color Lerp (Color a, Color b, float t)
		{
			t = Math.Clamp (t, 0f, 1f);
			return Color.FromArgb ((int) Math.Round (a.A + (b.A - a.A) * t), (int) Math.Round (a.R + (b.R - a.R) * t),
				(int) Math.Round (a.G + (b.G - a.G) * t), (int) Math.Round (a.B + (b.B - a.B) * t));
		}

		static void PointsFromMode (RectangleF r, LinearGradientMode m, out PointF s, out PointF e)
		{
			switch (m) {
			case LinearGradientMode.Vertical: s = new PointF (r.X, r.Y); e = new PointF (r.X, r.Bottom); break;
			case LinearGradientMode.ForwardDiagonal: s = new PointF (r.X, r.Y); e = new PointF (r.Right, r.Bottom); break;
			case LinearGradientMode.BackwardDiagonal: s = new PointF (r.Right, r.Y); e = new PointF (r.X, r.Bottom); break;
			default: s = new PointF (r.X, r.Y); e = new PointF (r.Right, r.Y); break;   // Horizontal
			}
		}

		static void PointsFromAngle (RectangleF r, float angleDeg, out PointF s, out PointF e)
		{
			double a = angleDeg * Math.PI / 180.0;
			float dx = (float) Math.Cos (a), dy = (float) Math.Sin (a);
			var c = new PointF (r.X + r.Width / 2f, r.Y + r.Height / 2f);
			float ext = (Math.Abs (dx) * r.Width + Math.Abs (dy) * r.Height) / 2f;
			s = new PointF (c.X - dx * ext, c.Y - dy * ext);
			e = new PointF (c.X + dx * ext, c.Y + dy * ext);
		}

		// SetLineGradient: colours, wrap, a one-entry blend of 1, and the xform; a degenerate
		// rectangle leaves GDI+'s brush invalid, which the flat API reports as OutOfMemory.
		void SetLineGradient (RectangleF rect, Color c1, Color c2, float angle, bool scalable, WrapMode wrap)
		{
			_wrap = wrap; _c1 = c1; _c2 = c2;
			_count = 1; _factor0 = 1f; _factors = _positions = null; _preset = false; _presetArgb = null;
			if (!WebGpuBackend.GdipLinearGradient.TryLineXform (angle, scalable, rect, out _xf))
				throw Status (SafeNativeMethods.Gdip.OutOfMemory);
			rectangle = rect;
			exact_angle = angle; exact_scalable = scalable;
		}

		// GpLineGradient(PointF, PointF, ...): the points' rectangle, the angle between them, not scalable.
		void FromPoints (PointF p1, PointF p2, Color c1, Color c2)
		{
			if (!GpGradient.RectFromPoints (p1, p2, out RectangleF r))
				throw Status (SafeNativeMethods.Gdip.OutOfMemory);
			SetLineGradient (r, c1, c2, GpGradient.PointsAngle (p1, p2), false, WrapMode.Tile);
			gradient_start = p1; gradient_end = p2;
		}

		public LinearGradientBrush (Point point1, Point point2, Color color1, Color color2)
		{
			FromPoints (point1, point2, color1, color2);
		}

		public LinearGradientBrush (PointF point1, PointF point2, Color color1, Color color2)
		{
			FromPoints (point1, point2, color1, color2);
		}

		static float ModeAngle (LinearGradientMode m)
			=> m == LinearGradientMode.Vertical ? 90f : m == LinearGradientMode.ForwardDiagonal ? 45f
			 : m == LinearGradientMode.BackwardDiagonal ? 135f : 0f;

		void FromMode (RectangleF rect, Color color1, Color color2, LinearGradientMode linearGradientMode)
		{
			if (linearGradientMode < LinearGradientMode.Horizontal || linearGradientMode > LinearGradientMode.BackwardDiagonal) {
				throw new InvalidEnumArgumentException (nameof (linearGradientMode), unchecked ((int)linearGradientMode), typeof (LinearGradientMode));
			}
			if (rect.Width == 0.0 || rect.Height == 0.0) {
				throw new ArgumentException (string.Format ("Rectangle '{0}' cannot have a width or height equal to 0.", rect.ToString ()));
			}
			SetLineGradient (rect, color1, color2, ModeAngle (linearGradientMode), true, WrapMode.Tile);
			PointsFromMode (rectangle, linearGradientMode, out gradient_start, out gradient_end);
			exact_known = true;
		}

		void FromAngle (RectangleF rect, Color color1, Color color2, float angle, bool isAngleScaleable)
		{
			if (rect.Width == 0 || rect.Height == 0) {
				throw new ArgumentException (string.Format ("Rectangle '{0}' cannot have a width or height equal to 0.", rect.ToString ()));
			}
			SetLineGradient (rect, color1, color2, angle, isAngleScaleable, WrapMode.Tile);
			PointsFromAngle (rectangle, angle, out gradient_start, out gradient_end);
			exact_known = true;
		}

		public LinearGradientBrush (Rectangle rect, Color color1, Color color2, LinearGradientMode linearGradientMode)
			=> FromMode (rect, color1, color2, linearGradientMode);

		public LinearGradientBrush (Rectangle rect, Color color1, Color color2, float angle) : this (rect, color1, color2, angle, false)
		{
		}

		public LinearGradientBrush (RectangleF rect, Color color1, Color color2, LinearGradientMode linearGradientMode)
			=> FromMode (rect, color1, color2, linearGradientMode);

		public LinearGradientBrush (RectangleF rect, Color color1, Color color2, float angle) : this (rect, color1, color2, angle, false)
		{
		}

		public LinearGradientBrush (Rectangle rect, Color color1, Color color2, float angle, bool isAngleScaleable)
			=> FromAngle (rect, color1, color2, angle, isAngleScaleable);

		public LinearGradientBrush (RectangleF rect, Color color1, Color color2, float angle, bool isAngleScaleable)
			=> FromAngle (rect, color1, color2, angle, isAngleScaleable);

		// Public Properties

		public Blend Blend {
			get {
				// Interpolation colors and blends don't work together very well. Getting the Blend when InterpolationColors
				// is set set puts the Brush into an unusable state afterwards.
				// Bail out here to avoid that.
				if (_interpolationColorsWasSet)
					return null;
				// GdipGetLineBlend: a one-entry blend is its factor alone (its position is not written).
				float [] factors = new float [_count];
				float [] positions = new float [_count];
				if (_count == 1)
					factors [0] = _factor0;
				else if (_factors != null) {
					Array.Copy (_factors, factors, _count);
					Array.Copy (_positions, positions, _count);
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

		// GpLineGradient::SetBlend: one entry is a factor alone; more need positions starting at 0 and
		// ending at 1 (within FLT_EPSILON); either way the preset blend is dropped.
		void SetBlend (float[] factors, float[] positions, int count)
		{
			if (count == 1) {
				_factors = _positions = null;
				_factor0 = factors [0];
			} else {
				if (!(MathF.Abs (positions [0]) <= GpGradient.Eps) || !(MathF.Abs (1f - positions [count - 1]) <= GpGradient.Eps))
					throw Status (SafeNativeMethods.Gdip.InvalidParameter);
				_factors = new float [count]; Array.Copy (factors, _factors, count);
				_positions = new float [count]; Array.Copy (positions, _positions, count);
			}
			_count = count;
			_preset = false;
			_presetArgb = null;
		}

		public bool GammaCorrection {
			get { return _gamma; }
			set { _gamma = value; }
		}

		public ColorBlend InterpolationColors {
			get {
				if (!_interpolationColorsWasSet)
					throw new ArgumentException("Property must be set to a valid ColorBlend object to use interpolation colors.");
				int count = _preset ? _count : 0;
				if (count < 2 || _presetArgb == null || _positions == null)
					throw Status (count < 2 ? SafeNativeMethods.Gdip.InvalidParameter : SafeNativeMethods.Gdip.GenericError);
				Color [] colors = new Color [count];
				float [] positions = new float [count];
				for (int i = 0; i < count; i++)
					colors [i] = Color.FromArgb (_presetArgb [i]);
				Array.Copy (_positions, positions, count);
				return new ColorBlend { Colors = colors, Positions = positions };
			}
			set {
				if (value == null)
					throw new ArgumentException ("InterpolationColors is null");
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

				// GdipSetLinePresetBlend -> GpLineGradient::SetPresetBlend (needs two or more).
				if (count < 2)
					throw Status (SafeNativeMethods.Gdip.InvalidParameter);
				_presetArgb = new int [count];
				for (int i = 0; i < count; i++)
					_presetArgb [i] = colors [i].ToArgb ();
				_positions = new float [count]; Array.Copy (positions, _positions, count);
				_factors = null;
				_preset = true;
				_count = count;
				_interpolationColorsWasSet = true;
			}
		}

		/// <summary>GpRectGradient::ColorAdjust @18006d500: the end colours, and the preset colours
		/// when there are two or more, through the recolor object.</summary>
		internal void ColorAdjust (System.Drawing.WebGpuBackend.Gdip.GpRecolorObject o)
		{
			var c = new uint [] { (uint) _c1.ToArgb (), (uint) _c2.ToArgb () };
			o.ColorAdjust (c, 0, 2);
			_c1 = Color.FromArgb ((int) c [0]); _c2 = Color.FromArgb ((int) c [1]);
			if (_preset && _count > 1 && _presetArgb != null) {
				var p = new uint [_count];
				for (int i = 0; i < _count; i++) p [i] = (uint) _presetArgb [i];
				o.ColorAdjust (p, 0, _count);
				for (int i = 0; i < _count; i++) _presetArgb [i] = (int) p [i];
			}
		}

		public Color [] LinearColors {
			get { return new Color [] { Color.FromArgb (_c1.ToArgb ()), Color.FromArgb (_c2.ToArgb ()) }; }
			set {
				// no null check, MS throws a NullReferenceException here
				_c1 = value [0]; _c2 = value [1];
			}
		}

		public RectangleF Rectangle {
			get {
				return rectangle;
			}
		}

		public Matrix Transform {
			get { return new Matrix (_xf); }
			set {
				if (value == null)
					throw new ArgumentNullException ("Transform");
				// GpElementaryBrush::SetTransform refuses a singular matrix.
				if (!value.Gp.IsInvertible)
					throw Status (SafeNativeMethods.Gdip.InvalidParameter);
				user_transformed = true;
				_xf = value.Gp;
			}
		}

		public WrapMode WrapMode {
			get { return _wrap; }
			set {
				// note: Clamp isn't valid (context wise) but it is checked in libgdiplus
				if ((value < WrapMode.Tile) || (value > WrapMode.Clamp))
					throw new InvalidEnumArgumentException ("WrapMode");
				// GdipSetLineWrapMode: a line brush cannot clamp.
				if (value == WrapMode.Clamp)
					throw Status (SafeNativeMethods.Gdip.InvalidParameter);
				_wrap = value;
			}
		}

		// Public Methods

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
			user_transformed = true;
		}

		public void ResetTransform ()
		{
			user_transformed = false;
			_xf = GpMatrix.CreateIdentity ();
		}

		public void RotateTransform (float angle)
		{
			RotateTransform (angle, MatrixOrder.Prepend);
		}

		public void RotateTransform (float angle, MatrixOrder order)
		{
			_xf.Rotate (angle, Append (order));
			user_transformed = true;
		}

		public void ScaleTransform (float sx, float sy)
		{
			ScaleTransform (sx, sy, MatrixOrder.Prepend);
		}

		public void ScaleTransform (float sx, float sy, MatrixOrder order)
		{
			_xf.Scale (sx, sy, Append (order));
			user_transformed = true;
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
			_interpolationColorsWasSet = false;
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
			_interpolationColorsWasSet = false;
		}

		public void TranslateTransform (float dx, float dy)
		{
			TranslateTransform (dx, dy, MatrixOrder.Prepend);
		}

		public void TranslateTransform (float dx, float dy, MatrixOrder order)
		{
			_xf.Translate (dx, dy, Append (order));
			user_transformed = true;
		}

		public override object Clone ()
		{
			var c = (LinearGradientBrush) MemberwiseClone ();
			c._factors = (float []) _factors?.Clone ();
			c._positions = (float []) _positions?.Clone ();
			c._presetArgb = (int []) _presetArgb?.Clone ();
			return c;
		}
	}
}
