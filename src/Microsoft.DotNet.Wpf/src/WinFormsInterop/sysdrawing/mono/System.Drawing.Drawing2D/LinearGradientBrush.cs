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
using System.Runtime.InteropServices;

namespace System.Drawing.Drawing2D {

	public sealed class LinearGradientBrush : Brush
	{
		RectangleF rectangle;
		private bool _interpolationColorsWasSet;

		// Gradient geometry + endpoint colours captured at construction, so the WebGPU GPU-raster
		// backend can reproduce the gradient (the native line-brush direction isn't otherwise readable).
		internal PointF gradient_start, gradient_end;
		internal Color gradient_color1 = Color.Black, gradient_color2 = Color.White;

		/// <summary>Start/end points + the two endpoint colours, for the GPU-raster recorder.</summary>
		internal void GetGpuGradient (out PointF start, out PointF end, out Color c1, out Color c2)
		{ start = gradient_start; end = gradient_end; c1 = gradient_color1; c2 = gradient_color2; }

		/// <summary>All gradient stops (multi-stop InterpolationColors if set, else the two endpoints)
		/// as parallel offset/ARGB arrays, for the GPU-raster recorder.</summary>
		internal void GetGpuStops (out float[] offsets, out int[] argb)
		{
			if (_interpolationColorsWasSet) {
				ColorBlend cb = InterpolationColors;
				offsets = cb.Positions;
				argb = new int[cb.Colors.Length];
				for (int i = 0; i < argb.Length; i++) argb[i] = cb.Colors[i].ToArgb ();
			} else {
				offsets = new[] { 0f, 1f };
				argb = new[] { gradient_color1.ToArgb (), gradient_color2.ToArgb () };
			}
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

		internal LinearGradientBrush (IntPtr native)
		{
			Status status = GDIPlus.GdipGetLineRect (native, out rectangle);
			SetNativeBrush (native);
			GDIPlus.CheckStatus (status);
			gradient_start = new PointF (rectangle.X, rectangle.Y);
			gradient_end = new PointF (rectangle.Right, rectangle.Y);
		}

		public LinearGradientBrush (Point point1, Point point2, Color color1, Color color2)
		{
			IntPtr nativeObject;
			Status status = GDIPlus.GdipCreateLineBrushI (ref point1, ref point2, color1.ToArgb (), color2.ToArgb (), WrapMode.Tile, out nativeObject);
			GDIPlus.CheckStatus (status);
			SetNativeBrush (nativeObject);

			status = GDIPlus.GdipGetLineRect (nativeObject, out rectangle);
			GDIPlus.CheckStatus (status);
			gradient_start = point1; gradient_end = point2; gradient_color1 = color1; gradient_color2 = color2;
		}

		public LinearGradientBrush (PointF point1, PointF point2, Color color1, Color color2)
		{
			IntPtr nativeObject;
			Status status = GDIPlus.GdipCreateLineBrush (ref point1, ref point2, color1.ToArgb (), color2.ToArgb (), WrapMode.Tile, out nativeObject);
			GDIPlus.CheckStatus (status);
			SetNativeBrush (nativeObject);

			status = GDIPlus.GdipGetLineRect (nativeObject, out rectangle);
			GDIPlus.CheckStatus (status);
			gradient_start = point1; gradient_end = point2; gradient_color1 = color1; gradient_color2 = color2;
		}

		public LinearGradientBrush (Rectangle rect, Color color1, Color color2, LinearGradientMode linearGradientMode)
		{
			if (linearGradientMode < LinearGradientMode.Horizontal || linearGradientMode > LinearGradientMode.BackwardDiagonal) {
				throw new InvalidEnumArgumentException (nameof (linearGradientMode), unchecked ((int)linearGradientMode), typeof (LinearGradientMode));
			}

			if (rect.Width == 0 || rect.Height == 0) {
				throw new ArgumentException( string.Format ("Rectangle '{0}' cannot have a width or height equal to 0.", rect.ToString ()));
			}

			IntPtr nativeObject;
			Status status = GDIPlus.GdipCreateLineBrushFromRectI (ref rect, color1.ToArgb (), color2.ToArgb (), linearGradientMode, WrapMode.Tile, out nativeObject);
			GDIPlus.CheckStatus (status);
			SetNativeBrush (nativeObject);

			rectangle = (RectangleF) rect;
			PointsFromMode (rectangle, linearGradientMode, out gradient_start, out gradient_end);
			gradient_color1 = color1; gradient_color2 = color2;
		}

		public LinearGradientBrush (Rectangle rect, Color color1, Color color2, float angle) : this (rect, color1, color2, angle, false)
		{
		}

		public LinearGradientBrush (RectangleF rect, Color color1, Color color2, LinearGradientMode linearGradientMode)
		{
			if (linearGradientMode < LinearGradientMode.Horizontal || linearGradientMode > LinearGradientMode.BackwardDiagonal) {
				throw new InvalidEnumArgumentException (nameof (linearGradientMode), unchecked ((int)linearGradientMode), typeof (LinearGradientMode));
			}

			if (rect.Width == 0.0 || rect.Height == 0.0) {
				throw new ArgumentException (string.Format ("Rectangle '{0}' cannot have a width or height equal to 0.", rect.ToString ()));
			}

			IntPtr nativeObject;
			Status status = GDIPlus.GdipCreateLineBrushFromRect (ref rect, color1.ToArgb (), color2.ToArgb (), linearGradientMode, WrapMode.Tile, out nativeObject);
			GDIPlus.CheckStatus (status);
			SetNativeBrush (nativeObject);

			rectangle = rect;
			PointsFromMode (rectangle, linearGradientMode, out gradient_start, out gradient_end);
			gradient_color1 = color1; gradient_color2 = color2;
		}

		public LinearGradientBrush (RectangleF rect, Color color1, Color color2, float angle) : this (rect, color1, color2, angle, false)
		{
		}

		public LinearGradientBrush (Rectangle rect, Color color1, Color color2, float angle, bool isAngleScaleable)
		{
			if (rect.Width == 0 || rect.Height == 0) {
				throw new ArgumentException (string.Format ("Rectangle '{0}' cannot have a width or height equal to 0.", rect.ToString ()));
			}

			IntPtr nativeObject;
			Status status = GDIPlus.GdipCreateLineBrushFromRectWithAngleI (ref rect, color1.ToArgb (), color2.ToArgb (), angle, isAngleScaleable, WrapMode.Tile, out nativeObject);
			GDIPlus.CheckStatus (status);
			SetNativeBrush (nativeObject);

			rectangle = (RectangleF) rect;
			PointsFromAngle (rectangle, angle, out gradient_start, out gradient_end);
			gradient_color1 = color1; gradient_color2 = color2;
		}

		public LinearGradientBrush (RectangleF rect, Color color1, Color color2, float angle, bool isAngleScaleable)
		{
			if (rect.Width == 0 || rect.Height == 0) {
				throw new ArgumentException (string.Format ("Rectangle '{0}' cannot have a width or height equal to 0.", rect.ToString ()));
			}

			IntPtr nativeObject;
			Status status = GDIPlus.GdipCreateLineBrushFromRectWithAngle (ref rect, color1.ToArgb (), color2.ToArgb (), angle, isAngleScaleable, WrapMode.Tile, out nativeObject);
			GDIPlus.CheckStatus (status);
			SetNativeBrush (nativeObject);

			rectangle = rect;
			PointsFromAngle (rectangle, angle, out gradient_start, out gradient_end);
			gradient_color1 = color1; gradient_color2 = color2;
		}

		// Public Properties

		public Blend Blend {
			get {
				// Interpolation colors and blends don't work together very well. Getting the Blend when InterpolationColors
				// is set set puts the Brush into an unusable state afterwards.
				// Bail out here to avoid that.
				if (_interpolationColorsWasSet)
				{
					return null;
				}

				int count;
				Status status = GDIPlus.GdipGetLineBlendCount (NativeBrush, out count);
				GDIPlus.CheckStatus (status);
				float [] factors = new float [count];
				float [] positions = new float [count];
				status = GDIPlus.GdipGetLineBlend (NativeBrush, factors, positions, count);
				GDIPlus.CheckStatus (status);

				Blend blend = new Blend ();
				blend.Factors = factors;
				blend.Positions = positions;

				return blend;
			}
			set {
				// no null check, MS throws a NullReferenceException here
				int count;
				float [] factors = value.Factors;
				float [] positions = value.Positions;
				count = factors.Length;

				if (count == 0 || positions.Length == 0)
					throw new ArgumentException ("Invalid Blend object. It should have at least 2 elements in each of the factors and positions arrays.");

				if (count != positions.Length)
					throw new ArgumentException ("Invalid Blend object. It should contain the same number of factors and positions values.");

				if (positions [0] != 0.0F)
					throw new ArgumentException ("Invalid Blend object. The positions array must have 0.0 as its first element.");

				if (positions [count - 1] != 1.0F)
					throw new ArgumentException ("Invalid Blend object. The positions array must have 1.0 as its last element.");

				Status status = GDIPlus.GdipSetLineBlend (NativeBrush, factors, positions, count);
				GDIPlus.CheckStatus (status);
			}
		}

		[MonoTODO ("The GammaCorrection value is ignored when using libgdiplus.")]
		public bool GammaCorrection {
			get {
				bool gammaCorrection;
				Status status = GDIPlus.GdipGetLineGammaCorrection (NativeBrush, out gammaCorrection);
				GDIPlus.CheckStatus (status);
				return gammaCorrection;
			}
			set {
				Status status = GDIPlus.GdipSetLineGammaCorrection (NativeBrush, value);
				GDIPlus.CheckStatus (status);
			}
		}

		public ColorBlend InterpolationColors {
			get {
				if (!_interpolationColorsWasSet)
				{
					throw new ArgumentException("Property must be set to a valid ColorBlend object to use interpolation colors.");
				}

				int count;
				Status status = GDIPlus.GdipGetLinePresetBlendCount (NativeBrush, out count);
				GDIPlus.CheckStatus (status);
				int [] intcolors = new int [count];
				float [] positions = new float [count];
				status = GDIPlus.GdipGetLinePresetBlend (NativeBrush, intcolors, positions, count);
				GDIPlus.CheckStatus (status);

				ColorBlend interpolationColors = new ColorBlend ();
				Color [] colors = new Color [count];
				for (int i = 0; i < count; i++)
					colors [i] = Color.FromArgb (intcolors [i]);
				interpolationColors.Colors = colors;
				interpolationColors.Positions = positions;

				return interpolationColors;
			}
			set {
				if (value == null)
					throw new ArgumentException ("InterpolationColors is null");
				int count;
				Color [] colors = value.Colors;
				float [] positions = value.Positions;
				count = colors.Length;

				if (count == 0 || positions.Length == 0)
					throw new ArgumentException ("Invalid ColorBlend object. It should have at least 2 elements in each of the colors and positions arrays.");

				if (count != positions.Length)
					throw new ArgumentException ("Invalid ColorBlend object. It should contain the same number of positions and color values.");

				if (positions [0] != 0.0F)
					throw new ArgumentException ("Invalid ColorBlend object. The positions array must have 0.0 as its first element.");

				if (positions [count - 1] != 1.0F)
					throw new ArgumentException ("Invalid ColorBlend object. The positions array must have 1.0 as its last element.");

				int [] blend = new int [colors.Length];
				for (int i = 0; i < colors.Length; i++)
					blend [i] = colors [i].ToArgb ();

				Status status = GDIPlus.GdipSetLinePresetBlend (NativeBrush, blend, positions, count);
				GDIPlus.CheckStatus (status);

				_interpolationColorsWasSet = true;
			}
		}

		public Color [] LinearColors {
			get {
				int [] colors = new int [2];
				Status status = GDIPlus.GdipGetLineColors (NativeBrush, colors);
				GDIPlus.CheckStatus (status);
				Color [] linearColors = new Color [2];
				linearColors [0] = Color.FromArgb (colors [0]);
				linearColors [1] = Color.FromArgb (colors [1]);

				return linearColors;
			}
			set {
				// no null check, MS throws a NullReferenceException here
				Status status = GDIPlus.GdipSetLineColors (NativeBrush, value [0].ToArgb (), value [1].ToArgb ());
				GDIPlus.CheckStatus (status);
			}
		}

		public RectangleF Rectangle {
			get {
				return rectangle;
			}
		}

		public Matrix Transform {
			get {
				Matrix matrix = new Matrix ();
				Status status = GDIPlus.GdipGetLineTransform (NativeBrush, matrix.nativeMatrix);
				GDIPlus.CheckStatus (status);

				return matrix;
			}
			set {
				if (value == null)
					throw new ArgumentNullException ("Transform");

				Status status = GDIPlus.GdipSetLineTransform (NativeBrush, value.nativeMatrix);
				GDIPlus.CheckStatus (status);
			}
		}

		public WrapMode WrapMode {
			get {
				WrapMode wrapMode;
				Status status = GDIPlus.GdipGetLineWrapMode (NativeBrush, out wrapMode);
				GDIPlus.CheckStatus (status);

				return wrapMode;
			}
			set {
				// note: Clamp isn't valid (context wise) but it is checked in libgdiplus
				if ((value < WrapMode.Tile) || (value > WrapMode.Clamp))
					throw new InvalidEnumArgumentException ("WrapMode");

				Status status = GDIPlus.GdipSetLineWrapMode (NativeBrush, value);
				GDIPlus.CheckStatus (status);
			}
		}

		// Public Methods

		public void MultiplyTransform (Matrix matrix)
		{
			MultiplyTransform (matrix, MatrixOrder.Prepend);
		}

		public void MultiplyTransform (Matrix matrix, MatrixOrder order)
		{
			if (matrix == null)
				throw new ArgumentNullException ("matrix");

			Status status = GDIPlus.GdipMultiplyLineTransform (NativeBrush, matrix.nativeMatrix, order);
			GDIPlus.CheckStatus (status);
		}

		public void ResetTransform ()
		{
			Status status = GDIPlus.GdipResetLineTransform (NativeBrush);
			GDIPlus.CheckStatus (status);
		}

		public void RotateTransform (float angle)
		{
			RotateTransform (angle, MatrixOrder.Prepend);
		}

		public void RotateTransform (float angle, MatrixOrder order)
		{
			Status status = GDIPlus.GdipRotateLineTransform (NativeBrush, angle, order);
			GDIPlus.CheckStatus (status);
		}

		public void ScaleTransform (float sx, float sy)
		{
			ScaleTransform (sx, sy, MatrixOrder.Prepend);
		}

		public void ScaleTransform (float sx, float sy, MatrixOrder order)
		{
			Status status = GDIPlus.GdipScaleLineTransform (NativeBrush, sx, sy, order);
			GDIPlus.CheckStatus (status);
		}

		public void SetBlendTriangularShape (float focus)
		{
			SetBlendTriangularShape (focus, 1.0F);
		}

		public void SetBlendTriangularShape (float focus, float scale)
		{
			if (focus < 0 || focus > 1 || scale < 0 || scale > 1)
				throw new ArgumentException ("Invalid parameter passed.");

			Status status = GDIPlus.GdipSetLineLinearBlend (NativeBrush, focus, scale);
			GDIPlus.CheckStatus (status);

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

			Status status = GDIPlus.GdipSetLineSigmaBlend (NativeBrush, focus, scale);
			GDIPlus.CheckStatus (status);
			
			_interpolationColorsWasSet = false;
		}

		public void TranslateTransform (float dx, float dy)
		{
			TranslateTransform (dx, dy, MatrixOrder.Prepend);
		}

		public void TranslateTransform (float dx, float dy, MatrixOrder order)
		{
			Status status = GDIPlus.GdipTranslateLineTransform (NativeBrush, dx, dy, order);
			GDIPlus.CheckStatus (status);
		}

		public override object Clone ()
		{
			IntPtr clonePtr;
			Status status = (Status) GDIPlus.GdipCloneBrush (new HandleRef (this, NativeBrush), out clonePtr);
			GDIPlus.CheckStatus (status);

			return new LinearGradientBrush (clonePtr);
		}
	}
}
