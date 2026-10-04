//
// System.Drawing.Drawing2D.Matrix.cs
//
// Authors:
//   Stefan Maierhofer <sm@cg.tuwien.ac.at>
//   Dennis Hayes (dennish@Raytek.com)
//   Duncan Mak (duncan@ximian.com)
//   Ravindra (rkumar@novell.com)
//
// (C) Ximian, Inc.  http://www.ximian.com
// Copyright (C) 2004, 2006 Novell, Inc (http://www.novell.com)
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

using System.Drawing.WebGpuBackend.Gdip;

namespace System.Drawing.Drawing2D
{
	// A managed GpMatrix (WebGpuBackend.Gdip.GpMatrix): GDI+'s six floats (m11 m12 m21 m22 dx dy,
	// row vectors) with GDI+'s arithmetic for every operation, read out of gdiplus.dll -- the
	// operand order of each product and sum, the complexity flags, the invertibility tolerance, and
	// the flat API's own rounding of integer points ((int)(v + 0.5), truncated).
	public sealed class Matrix : MarshalByRefObject, IDisposable
	{
		internal GpMatrix Gp;

		internal Matrix (GpMatrix m)
		{
			Gp = m;
		}

		public Matrix ()
		{
			Gp = GpMatrix.CreateIdentity ();
		}

		public Matrix (Rectangle rect, Point[] plgpts)
		{
			if (plgpts == null)
				throw new ArgumentNullException ("plgpts");
			if (plgpts.Length != 3)
				throw new ArgumentException ("plgpts");
			Init (new RectangleF (rect.X, rect.Y, rect.Width, rect.Height), new PointF [] { plgpts [0], plgpts [1], plgpts [2] });
		}

		public Matrix (RectangleF rect, PointF[] plgpts)
		{
			if (plgpts == null)
				throw new ArgumentNullException ("plgpts");
			if (plgpts.Length != 3)
				throw new ArgumentException ("plgpts");
			Init (rect, plgpts);
		}

		public Matrix (float m11, float m12, float m21, float m22, float dx, float dy)
		{
			Gp = new GpMatrix (m11, m12, m21, m22, dx, dy);
		}

		public Matrix (System.Numerics.Matrix3x2 matrix) : this (matrix.M11, matrix.M12, matrix.M21, matrix.M22, matrix.M31, matrix.M32)
		{
		}

		/// <summary>GpMatrix::InferAffineMatrix(points, rect) @180034750.</summary>
		void Init (RectangleF r, PointF [] p)
		{
			float x = r.X, y = r.Y, w = r.Width, h = r.Height;
			float nh = -h;
			float det = ((w + x) * (y + h) - y * x) + (x * nh - y * w);
			if (!(1.1920929e-07f <= Math.Abs (det)))
				throw new ArgumentException ("Parameter is not valid.");
			float inv = 1f / det;
			var m = new GpMatrix ();
			m.M11 = (h * p [1].X + nh * p [0].X) * inv;
			m.M12 = (h * p [1].Y + nh * p [0].Y) * inv;
			m.M21 = (w * p [2].X + -w * p [0].X) * inv;
			m.M22 = (w * p [2].Y + -w * p [0].Y) * inv;
			float t = (x + w) * (y + h) - x * y;
			float a = -x * h, b = -y * w;
			m.Dx = (a * p [1].X + t * p [0].X + b * p [2].X) * inv;
			m.Dy = (a * p [1].Y + t * p [0].Y + b * p [2].Y) * inv;
			m.Complexity = m.ComputeComplexity ();
			Gp = m;
		}

		public float[] Elements => new float [] { Gp.M11, Gp.M12, Gp.M21, Gp.M22, Gp.Dx, Gp.Dy };

		public System.Numerics.Matrix3x2 MatrixElements {
			get => new System.Numerics.Matrix3x2 (Gp.M11, Gp.M12, Gp.M21, Gp.M22, Gp.Dx, Gp.Dy);
			set => Gp = new GpMatrix (value.M11, value.M12, value.M21, value.M22, value.M31, value.M32);
		}

		public bool IsIdentity => Gp.Complexity == 0;

		public bool IsInvertible => Gp.IsInvertible;

		public float OffsetX => Gp.Dx;

		public float OffsetY => Gp.Dy;

		public Matrix Clone () => new Matrix (Gp);

		public void Dispose ()
		{
			GC.SuppressFinalize (this);
		}

		public override bool Equals (object obj)
		{
			if (!(obj is Matrix other)) return false;
			if (ReferenceEquals (this, other)) return true;
			return Gp.M11 == other.Gp.M11 && Gp.M12 == other.Gp.M12 && Gp.M21 == other.Gp.M21
				&& Gp.M22 == other.Gp.M22 && Gp.Dx == other.Gp.Dx && Gp.Dy == other.Gp.Dy;
		}

		public override int GetHashCode ()
		{
			return base.GetHashCode ();
		}

		public void Invert ()
		{
			if (!Gp.Invert ())
				throw new ArgumentException ("Parameter is not valid.");
		}

		public void Multiply (Matrix matrix)
		{
			Multiply (matrix, MatrixOrder.Prepend);
		}

		public void Multiply (Matrix matrix, MatrixOrder order)
		{
			if (matrix == null)
				throw new ArgumentNullException ("matrix");
			CheckOrder (order);
			Gp.Multiply (matrix.Gp, order == MatrixOrder.Append);
		}

		static void CheckOrder (MatrixOrder order)
		{
			if ((uint) order > 1)
				throw new ArgumentException ("Parameter is not valid.");
		}

		public void Reset ()
		{
			Gp = GpMatrix.CreateIdentity ();
		}

		public void Rotate (float angle)
		{
			Rotate (angle, MatrixOrder.Prepend);
		}

		public void Rotate (float angle, MatrixOrder order)
		{
			CheckOrder (order);
			Gp.Rotate (angle, order == MatrixOrder.Append);
		}

		public void RotateAt (float angle, PointF point)
		{
			RotateAt (angle, point, MatrixOrder.Prepend);
		}

		// System.Drawing's own managed RotateAt (it is not a GDI+ entry point): the elements set
		// through GdipSetMatrixElements.
		public void RotateAt (float angle, PointF point, MatrixOrder order)
		{
			if ((order < MatrixOrder.Prepend) || (order > MatrixOrder.Append))
				throw new ArgumentException ("order");

			angle *= (float) (Math.PI / 180.0);  // degrees to radians
			float cos = (float) Math.Cos (angle);
			float sin = (float) Math.Sin (angle);
			float e4 = -point.X * cos + point.Y * sin + point.X;
			float e5 = -point.X * sin - point.Y * cos + point.Y;
			float [] m0 = Elements;

			if (order == MatrixOrder.Prepend)
				Gp = new GpMatrix (cos * m0[0] + sin * m0[2],
								cos * m0[1] + sin * m0[3],
								-sin * m0[0] + cos * m0[2],
								-sin * m0[1] + cos * m0[3],
								e4 * m0[0] + e5 * m0[2] + m0[4],
								e4 * m0[1] + e5 * m0[3] + m0[5]);
			else
				Gp = new GpMatrix (m0[0] * cos + m0[1] * -sin,
								m0[0] * sin + m0[1] * cos,
								m0[2] * cos + m0[3] * -sin,
								m0[2] * sin + m0[3] * cos,
								m0[4] * cos + m0[5] * -sin + e4,
								m0[4] * sin + m0[5] * cos + e5);
		}

		public void Scale (float scaleX, float scaleY)
		{
			Scale (scaleX, scaleY, MatrixOrder.Prepend);
		}

		public void Scale (float scaleX, float scaleY, MatrixOrder order)
		{
			CheckOrder (order);
			Gp.Scale (scaleX, scaleY, order == MatrixOrder.Append);
		}

		public void Shear (float shearX, float shearY)
		{
			Shear (shearX, shearY, MatrixOrder.Prepend);
		}

		public void Shear (float shearX, float shearY, MatrixOrder order)
		{
			CheckOrder (order);
			Gp.Shear (shearX, shearY, order == MatrixOrder.Append);
		}

		// GdipTransformMatrixPointsI @18006b040: through floats, then (int)(v + 0.5) truncated.
		public void TransformPoints (Point[] pts)
		{
			if (pts == null)
				throw new ArgumentNullException ("pts");
			for (int i = 0; i < pts.Length; i++) {
				PointF p = Gp.Transform (new PointF (pts [i].X, pts [i].Y));
				pts [i] = new Point ((int) MathF.Floor (p.X + 0.5f), (int) MathF.Floor (p.Y + 0.5f));
			}
		}

		public void TransformPoints (PointF[] pts)
		{
			if (pts == null)
				throw new ArgumentNullException ("pts");
			Gp.Transform (pts);
		}

		public void TransformVectors (Point[] pts)
		{
			if (pts == null)
				throw new ArgumentNullException ("pts");
			for (int i = 0; i < pts.Length; i++) {
				PointF p = Gp.VectorTransform (new PointF (pts [i].X, pts [i].Y));
				pts [i] = new Point ((int) MathF.Floor (p.X + 0.5f), (int) MathF.Floor (p.Y + 0.5f));
			}
		}

		public void TransformVectors (PointF[] pts)
		{
			if (pts == null)
				throw new ArgumentNullException ("pts");
			for (int i = 0; i < pts.Length; i++) pts [i] = Gp.VectorTransform (pts [i]);
		}

		public void Translate (float offsetX, float offsetY)
		{
			Translate (offsetX, offsetY, MatrixOrder.Prepend);
		}

		public void Translate (float offsetX, float offsetY, MatrixOrder order)
		{
			CheckOrder (order);
			Gp.Translate (offsetX, offsetY, order == MatrixOrder.Append);
		}

		public void VectorTransformPoints (Point[] pts)
		{
			TransformVectors (pts);
		}
	}
}
