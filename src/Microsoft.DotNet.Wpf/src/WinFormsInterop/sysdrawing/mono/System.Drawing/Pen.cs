//
// System.Drawing.Pen.cs
//
// Authors:
//   Miguel de Icaza (miguel@ximian.com)
//   Alexandre Pigolkine (pigolkine@gmx.de)
//   Duncan Mak (duncan@ximian.com)
//   Ravindra (rkumar@novell.com)
//
// Copyright (C) Ximian, Inc.  http://www.ximian.com
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
using System.Drawing.Drawing2D;
using System.Drawing.WebGpuBackend.Gdip;

namespace System.Drawing
{
	// A managed GpPen (gdiplus.dll), every property kept as GDI+ keeps it and checked as its flat
	// API checks it: InitDefaultState @180150a90, SetDashStyleWithDashCap @18000bcc8, SetDashArray
	// @180071ca0, SetDashCap @180071d60, SetCompoundArray @180071bc0, SetPenAlignment @1801a60e0,
	// SetBrush @180071ae0 / SetColor @18000bb90 (the pen holds a COPY of the brush), the custom caps
	// (@180069c90: a copy, the cap type 0xff), MiterLimit clamped to 1 (@18006a180), a singular
	// transform refused (@18006a360).
	public sealed class Pen : MarshalByRefObject, ICloneable, IDisposable
	{
		internal bool isModifiable = true;

		// +0x20 the brush (a copy of the caller's)
		private Brush _brush;
		// The colour handed to the colour constructor or setter, cached as .NET caches it.
		private Color color;
		// DpPen +0x2c width, +0x34 start cap, +0x38 end cap, +0x3c join, +0x40 miter limit,
		// +0x44 alignment, +0x50 transform, +0x80 dash style, +0x84 dash cap, +0x88/+0x90 dash
		// array, +0x8c dash offset, +0x98/+0xa0 compound array, +0xa8/+0xb0 custom caps.
		private float _width = 1f;
		private LineCap _startCap = LineCap.Flat, _endCap = LineCap.Flat;
		private LineJoin _lineJoin = LineJoin.Miter;
		private float _miterLimit = 10f;
		private PenAlignment _alignment = PenAlignment.Center;
		private GpMatrix _xf = GpMatrix.CreateIdentity ();
		private DashStyle _dashStyle = DashStyle.Solid;
		private DashCap _dashCap = DashCap.Flat;
		private float [] _dash;
		private float _dashOffset;
		private float [] _compound;
		private CustomLineCap _customStart, _customEnd;

		static Exception Status (int s) => SafeNativeMethods.Gdip.StatusException (s);
		static Exception ReadOnly () => new ArgumentException (Locale.GetText ("This Pen object can't be modified."));

		// For the managed GDI+ engine and the recorders: the pen's own objects, not copies.
		internal Brush BrushRef => _brush;
		internal GpMatrix Xform => _xf;
		internal float [] DashArrayRef => _dash;
		internal float [] CompoundRef => _compound;
		internal CustomLineCap CustomStartRef => _customStart;
		internal CustomLineCap CustomEndRef => _customEnd;

		public Pen (Brush brush) : this (brush, 1.0F)
		{
		}

		public Pen (Color color) : this (color, 1.0F)
		{
		}

		public Pen (Brush brush, float width)
		{
			if (brush == null)
				throw new ArgumentNullException ("brush");
			_brush = (Brush) brush.Clone ();
			_width = 0f <= width ? width : 1f;
			color = Color.Empty;
		}

		public Pen (Color color, float width)
		{
			this.color = color;
			_brush = new SolidBrush (color);
			_width = 0f <= width ? width : 1f;
		}

		//
		// Properties
		//
		public PenAlignment Alignment {
			get { return _alignment; }
			set {
				if ((value < PenAlignment.Center) || (value > PenAlignment.Right))
					throw new InvalidEnumArgumentException ("Alignment", (int)value, typeof (PenAlignment));
				if (!isModifiable)
					throw ReadOnly ();
				// An inset pen cannot be compound.
				if (value == PenAlignment.Inset && _compound != null && _compound.Length != 0)
					throw Status (SafeNativeMethods.Gdip.NotImplemented);
				_alignment = value;
			}
		}

		public Brush Brush {
			get { return (Brush) _brush.Clone (); }
			set {
				if (value == null)
					throw new ArgumentNullException ("Brush");
				if (!isModifiable)
					throw ReadOnly ();
				_brush = (Brush) value.Clone ();
				color = Color.Empty;
			}
		}

		public Color Color {
			get {
				if (color.Equals (Color.Empty)) {
					// GdipGetPenColor: only a solid pen has one.
					if (!(_brush is SolidBrush sb))
						throw Status (SafeNativeMethods.Gdip.InvalidParameter);
					color = Color.FromArgb (sb.Color.ToArgb ());
				}
				return color;
			}
			set {
				if (!isModifiable)
					throw ReadOnly ();
				if (value != color) {
					color = value;
					_brush = new SolidBrush (value);
				}
			}
		}

		public float [] CompoundArray {
			get {
				return _compound == null ? new float [0] : (float []) _compound.Clone ();
			}
			set {
				if (!isModifiable)
					throw ReadOnly ();
				int length = value.Length;
				if (length < 2)
					throw new ArgumentException ("Invalid parameter.");
				foreach (float val in value)
					if (val < 0 || val > 1)
						throw new ArgumentException ("Invalid parameter.");
				// SetCompoundArray: an even count, ascending, in [0, 1]; not for an inset pen.
				if ((length & 1) != 0)
					throw Status (SafeNativeMethods.Gdip.InvalidParameter);
				if (_alignment == PenAlignment.Inset)
					throw Status (SafeNativeMethods.Gdip.NotImplemented);
				for (int i = 1; i < length; i++)
					if (!(value [i - 1] <= value [i]))
						throw Status (SafeNativeMethods.Gdip.InvalidParameter);
				_compound = (float []) value.Clone ();
			}
		}

		public CustomLineCap CustomEndCap {
			get { return (CustomLineCap) _customEnd?.Clone (); }
			set {
				if (!isModifiable)
					throw ReadOnly ();
				if (value == null)
					throw Status (SafeNativeMethods.Gdip.InvalidParameter);
				_customEnd = (CustomLineCap) value.Clone ();
				_endCap = LineCap.Custom;
			}
		}

		public CustomLineCap CustomStartCap {
			get { return (CustomLineCap) _customStart?.Clone (); }
			set {
				if (!isModifiable)
					throw ReadOnly ();
				if (value == null)
					throw Status (SafeNativeMethods.Gdip.InvalidParameter);
				_customStart = (CustomLineCap) value.Clone ();
				_startCap = LineCap.Custom;
			}
		}

		// SetDashCap keeps Round and Triangle; anything else is Flat.
		static DashCap MapDashCap (DashCap c) => c == DashCap.Round || c == DashCap.Triangle ? c : DashCap.Flat;

		public DashCap DashCap {
			get { return _dashCap; }
			set {
				if ((value < DashCap.Flat) || (value > DashCap.Triangle))
					throw new InvalidEnumArgumentException ("DashCap", (int)value, typeof (DashCap));
				if (!isModifiable)
					throw ReadOnly ();
				_dashCap = MapDashCap (value);
			}
		}

		public float DashOffset {
			get { return _dashOffset; }
			set {
				if (!isModifiable)
					throw ReadOnly ();
				_dashOffset = value;
			}
		}

		public float [] DashPattern {
			get {
				if (_dash != null && _dash.Length > 0)
					return (float []) _dash.Clone ();
				// special case (not handled inside GDI+)
				if (DashStyle == DashStyle.Custom)
					return new float [] { 1.0f };
				return new float [0];
			}
			set {
				if (!isModifiable)
					throw ReadOnly ();
				int length = value.Length;
				if (length == 0)
					throw new ArgumentException ("Invalid parameter.");
				foreach (float val in value)
					if (val <= 0)
						throw new ArgumentException ("Invalid parameter.");
				_dash = (float []) value.Clone ();
				_dashStyle = DashStyle.Custom;
			}
		}

		public DashStyle DashStyle {
			get { return _dashStyle; }
			set {
				if ((value < DashStyle.Solid) || (value > DashStyle.Custom))
					throw new InvalidEnumArgumentException ("DashStyle", (int)value, typeof (DashStyle));
				if (!isModifiable)
					throw ReadOnly ();
				SetDashStyle (value);
			}
		}

		// SetDashStyleWithDashCap: each named style is its pattern in pen widths; Custom keeps the
		// pattern there is.
		void SetDashStyle (DashStyle s)
		{
			switch (s) {
			case DashStyle.Solid: _dash = null; break;
			case DashStyle.Dash: _dash = new float [] { 3f, 1f }; break;
			case DashStyle.Dot: _dash = new float [] { 1f, 1f }; break;
			case DashStyle.DashDot: _dash = new float [] { 3f, 1f, 1f, 1f }; break;
			case DashStyle.DashDotDot: _dash = new float [] { 3f, 1f, 1f, 1f, 1f, 1f }; break;
			case DashStyle.Custom: break;
			}
			_dashStyle = s;
		}

		public LineCap StartCap {
			get { return _startCap; }
			set {
				if ((value < LineCap.Flat) || (value > LineCap.Custom))
					throw new InvalidEnumArgumentException ("StartCap", (int)value, typeof (LineCap));
				if (!isModifiable)
					throw ReadOnly ();
				_startCap = value;
				_customStart = null;
			}
		}

		public LineCap EndCap {
			get { return _endCap; }
			set {
				if ((value < LineCap.Flat) || (value > LineCap.Custom))
					throw new InvalidEnumArgumentException ("EndCap", (int)value, typeof (LineCap));
				if (!isModifiable)
					throw ReadOnly ();
				_endCap = value;
				_customEnd = null;
			}
		}

		public LineJoin LineJoin {
			get { return _lineJoin; }
			set {
				if ((value < LineJoin.Miter) || (value > LineJoin.MiterClipped))
					throw new InvalidEnumArgumentException ("LineJoin", (int)value, typeof (LineJoin));
				if (!isModifiable)
					throw ReadOnly ();
				_lineJoin = value;
			}
		}

		public float MiterLimit {
			get { return _miterLimit; }
			set {
				if (!isModifiable)
					throw ReadOnly ();
				_miterLimit = 1f <= value ? value : 1f;
			}
		}

		public PenType PenType {
			get {
				switch (_brush) {
				case SolidBrush _: return PenType.SolidColor;
				case HatchBrush _: return PenType.HatchFill;
				case TextureBrush _: return PenType.TextureFill;
				case PathGradientBrush _: return PenType.PathGradient;
				case LinearGradientBrush _: return PenType.LinearGradient;
				default: return (PenType) (-1);
				}
			}
		}

		public Matrix Transform {
			get { return new Matrix (_xf); }
			set {
				if (value == null)
					throw new ArgumentNullException ("Transform");
				if (!isModifiable)
					throw ReadOnly ();
				if (!value.Gp.IsInvertible)
					throw Status (SafeNativeMethods.Gdip.InvalidParameter);
				_xf = value.Gp;
			}
		}

		public float Width {
			get { return _width; }
			set {
				if (!isModifiable)
					throw ReadOnly ();
				_width = value;
			}
		}

		internal IntPtr NativePen => IntPtr.Zero;

		public object Clone ()
		{
			var c = (Pen) MemberwiseClone ();
			c.isModifiable = true;
			c._brush = (Brush) _brush.Clone ();
			c._dash = (float []) _dash?.Clone ();
			c._compound = (float []) _compound?.Clone ();
			c._customStart = (CustomLineCap) _customStart?.Clone ();
			c._customEnd = (CustomLineCap) _customEnd?.Clone ();
			return c;
		}

		public void Dispose ()
		{
			Dispose (true);
			System.GC.SuppressFinalize (this);
		}

		private void Dispose (bool disposing)
		{
			if (disposing && !isModifiable)
				throw ReadOnly ();
		}

		~Pen ()
		{
			Dispose (false);
		}

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

		public void SetLineCap (LineCap startCap, LineCap endCap, DashCap dashCap)
		{
			if (!isModifiable)
				throw ReadOnly ();
			// GdipSetPenLineCap197819 sets the cap types (a custom cap object is left where it is).
			_startCap = startCap;
			_endCap = endCap;
			_dashCap = MapDashCap (dashCap);
		}

		public void TranslateTransform (float dx, float dy)
		{
			TranslateTransform (dx, dy, MatrixOrder.Prepend);
		}

		public void TranslateTransform (float dx, float dy, MatrixOrder order)
		{
			_xf.Translate (dx, dy, Append (order));
		}
	}
}
