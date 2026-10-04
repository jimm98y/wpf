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

using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace System.Drawing.Drawing2D
{
	// MANAGED WHERE GDI+ IS NOT THERE. A matrix used to be a handle to a GDI+ object and nothing
	// else, so with no GDI+ (the browser, WF_NO_GDIPLUS) every Matrix was a silent identity: the
	// constructor returned early, every operation then handed GDI+ a null handle, and a recording
	// Graphics could not be scaled, rotated or read back at all. A printed page is drawn through
	// exactly such a Graphics, in hundredths of an inch, millimetres or points, so the arithmetic
	// GDI+ did is done here instead: six floats in GDI+'s order (m11 m12 m21 m22 dx dy), row
	// vectors, behind a handle ManagedMatrix resolves for the path and region code.
	public sealed class Matrix : MarshalByRefObject, IDisposable
	{
		internal IntPtr nativeMatrix;
		// Non-null exactly when this matrix is managed (GDI+ absent).
		internal float [] m;

		internal Matrix (IntPtr ptr)
		{
			nativeMatrix = ptr;
		}

		public Matrix ()
		{
			if (!GDIPlus.Initialized) { InitManaged (1, 0, 0, 1, 0, 0); return; }
			Status status = GDIPlus.GdipCreateMatrix (out nativeMatrix);
			GDIPlus.CheckStatus (status);
		}

		public Matrix (Rectangle rect, Point[] plgpts)
		{
			if (plgpts == null)
				throw new ArgumentNullException ("plgpts");
			if (plgpts.Length != 3)
				throw new ArgumentException ("plgpts");
			if (!GDIPlus.Initialized) {
				InitParallelogram (rect, new PointF [] { plgpts [0], plgpts [1], plgpts [2] });
				return;
			}
			Status status = GDIPlus.GdipCreateMatrix3I (ref rect, plgpts, out nativeMatrix);
			GDIPlus.CheckStatus (status);
		}

		public Matrix (RectangleF rect, PointF[] plgpts)
		{
			if (plgpts == null)
				throw new ArgumentNullException ("plgpts");
			if (plgpts.Length != 3)
				throw new ArgumentException ("plgpts");
			if (!GDIPlus.Initialized) { InitParallelogram (rect, plgpts); return; }
			Status status = GDIPlus.GdipCreateMatrix3 (ref rect, plgpts, out nativeMatrix);
			GDIPlus.CheckStatus (status);
		}

		public Matrix (float m11, float m12, float m21, float m22, float dx, float dy)
		{
			if (!GDIPlus.Initialized) { InitManaged (m11, m12, m21, m22, dx, dy); return; }
			Status status = GDIPlus.GdipCreateMatrix2 (m11, m12, m21, m22, dx, dy, out nativeMatrix);
			GDIPlus.CheckStatus (status);
		}

		void InitManaged (float m11, float m12, float m21, float m22, float dx, float dy)
		{
			m = new float [] { m11, m12, m21, m22, dx, dy };
			nativeMatrix = ManagedMatrix.Register (m);
		}

		// The matrix that maps rect's top-left, top-right and bottom-left corners onto the three points.
		void InitParallelogram (RectangleF rect, PointF [] p)
		{
			if (rect.Width == 0 || rect.Height == 0)
				throw new ArgumentException ("rect");
			float m11 = (p [1].X - p [0].X) / rect.Width, m12 = (p [1].Y - p [0].Y) / rect.Width;
			float m21 = (p [2].X - p [0].X) / rect.Height, m22 = (p [2].Y - p [0].Y) / rect.Height;
			InitManaged (m11, m12, m21, m22,
				p [0].X - rect.X * m11 - rect.Y * m21,
				p [0].Y - rect.X * m12 - rect.Y * m22);
		}

		// r = a * b, row vectors: a is applied first.
		internal static void Mul (float [] a, float [] b, float [] r)
		{
			float m11 = a [0] * b [0] + a [1] * b [2];
			float m12 = a [0] * b [1] + a [1] * b [3];
			float m21 = a [2] * b [0] + a [3] * b [2];
			float m22 = a [2] * b [1] + a [3] * b [3];
			float dx = a [4] * b [0] + a [5] * b [2] + b [4];
			float dy = a [4] * b [1] + a [5] * b [3] + b [5];
			r [0] = m11; r [1] = m12; r [2] = m21; r [3] = m22; r [4] = dx; r [5] = dy;
		}

		void Combine (float [] other, MatrixOrder order)
		{
			if (order == MatrixOrder.Prepend) Mul (other, m, m);
			else Mul (m, other, m);
		}

		// properties
		public float[] Elements {
			get {
				if (m != null) return (float []) m.Clone ();
				if (nativeMatrix == IntPtr.Zero) return new float [] { 1f, 0f, 0f, 1f, 0f, 0f };
				float [] retval = new float [6];
				IntPtr tmp = Marshal.AllocHGlobal (Marshal.SizeOf (typeof (float)) * 6);
				try {
					Status status = GDIPlus.GdipGetMatrixElements (nativeMatrix, tmp);
					GDIPlus.CheckStatus (status);
					Marshal.Copy (tmp, retval, 0, 6);
				}
				finally {
					Marshal.FreeHGlobal (tmp);
				}
				return retval;
			}
		}

		public bool IsIdentity {
			get {
				if (m != null) return m [0] == 1 && m [1] == 0 && m [2] == 0 && m [3] == 1 && m [4] == 0 && m [5] == 0;
				if (nativeMatrix == IntPtr.Zero) return true;
				bool retval;
				Status status = GDIPlus.GdipIsMatrixIdentity (nativeMatrix, out retval);
				GDIPlus.CheckStatus (status);
				return retval;
			}
		}

		public bool IsInvertible {
			get {
				if (m != null) return Determinant () != 0f;
				bool retval;
				Status status = GDIPlus.GdipIsMatrixInvertible (nativeMatrix, out retval);
				GDIPlus.CheckStatus (status);
				return retval;
			}
		}

		float Determinant () => m [0] * m [3] - m [1] * m [2];

		public float OffsetX {
			get {
				return this.Elements [4];
			}
		}

		public float OffsetY {
			get {
				return this.Elements [5];
			}
		}

		public Matrix Clone()
		{
			if (m != null) {
				var c = new Matrix (IntPtr.Zero);
				c.InitManaged (m [0], m [1], m [2], m [3], m [4], m [5]);
				return c;
			}
			IntPtr retval;
			Status status = GDIPlus.GdipCloneMatrix (nativeMatrix, out retval);
			GDIPlus.CheckStatus (status);
			return new Matrix (retval);
		}


		public void Dispose ()
		{
			if (m != null) {
				ManagedMatrix.Unregister (nativeMatrix);
				nativeMatrix = IntPtr.Zero;
			} else if (nativeMatrix != IntPtr.Zero) {
				Status status = GDIPlus.GdipDeleteMatrix (nativeMatrix);
				GDIPlus.CheckStatus (status);
				nativeMatrix = IntPtr.Zero;
			}

			GC.SuppressFinalize (this);
		}

		public override bool Equals (object obj)
		{
			Matrix other = obj as Matrix;

			if (other != null) {
				if (m != null || other.m != null) {
					float [] a = Elements, b = other.Elements;
					for (int i = 0; i < 6; i++) if (a [i] != b [i]) return false;
					return true;
				}
				bool retval;
				Status status = GDIPlus.GdipIsMatrixEqual (nativeMatrix, other.nativeMatrix, out retval);
				GDIPlus.CheckStatus (status);
				return retval;

			} else
				return false;
		}

		~Matrix()
		{
			Dispose ();
		}

		public override int GetHashCode ()
		{
			return base.GetHashCode ();
		}

		public void Invert ()
		{
			if (m != null) {
				float det = Determinant ();
				if (det == 0f)
					throw new ArgumentException ("The matrix is not invertible.");
				float a = m [0], b = m [1], c = m [2], d = m [3], e = m [4], f = m [5];
				m [0] = d / det; m [1] = -b / det; m [2] = -c / det; m [3] = a / det;
				m [4] = (c * f - d * e) / det; m [5] = (b * e - a * f) / det;
				return;
			}
			Status status = GDIPlus.GdipInvertMatrix (nativeMatrix);
			GDIPlus.CheckStatus (status);
		}

		public void Multiply (Matrix matrix)
		{
			Multiply (matrix, MatrixOrder.Prepend);
		}

		public void Multiply (Matrix matrix, MatrixOrder order)
		{
			if (matrix == null)
				throw new ArgumentNullException ("matrix");
			if (m != null) { Combine (matrix.Elements, order); return; }

			Status status = GDIPlus.GdipMultiplyMatrix (nativeMatrix, matrix.nativeMatrix, order);
			GDIPlus.CheckStatus (status);
		}

		public void Reset()
		{
			if (m != null) { m [0] = 1; m [1] = 0; m [2] = 0; m [3] = 1; m [4] = 0; m [5] = 0; return; }
			Status status = GDIPlus.GdipSetMatrixElements (nativeMatrix, 1, 0, 0, 1, 0, 0);
			GDIPlus.CheckStatus (status);
		}

		public void Rotate (float angle)
		{
			Rotate (angle, MatrixOrder.Prepend);
		}

		public void Rotate (float angle, MatrixOrder order)
		{
			if (m != null) {
				double r = angle * Math.PI / 180.0;
				float cos = (float) Math.Cos (r), sin = (float) Math.Sin (r);
				Combine (new float [] { cos, sin, -sin, cos, 0, 0 }, order);
				return;
			}
			Status status = GDIPlus.GdipRotateMatrix (nativeMatrix, angle, order);
			GDIPlus.CheckStatus (status);
		}

		public void RotateAt (float angle, PointF point)
		{
			RotateAt (angle, point, MatrixOrder.Prepend);
		}

		public void RotateAt (float angle, PointF point, MatrixOrder order)
		{
			if ((order < MatrixOrder.Prepend) || (order > MatrixOrder.Append))
				throw new ArgumentException ("order");

			angle *= (float) (Math.PI / 180.0);  // degrees to radians
			float cos = (float) Math.Cos (angle);
			float sin = (float) Math.Sin (angle);
			float e4 = -point.X * cos + point.Y * sin + point.X;
			float e5 = -point.X * sin - point.Y * cos + point.Y;

			if (m != null) {
				Combine (new float [] { cos, sin, -sin, cos, e4, e5 }, order);
				return;
			}

			float[] m0 = this.Elements;

			Status status;

			if (order == MatrixOrder.Prepend)
				status = GDIPlus.GdipSetMatrixElements (nativeMatrix,
								cos * m0[0] + sin * m0[2],
								cos * m0[1] + sin * m0[3],
								-sin * m0[0] + cos * m0[2],
								-sin * m0[1] + cos * m0[3],
								e4 * m0[0] + e5 * m0[2] + m0[4],
								e4 * m0[1] + e5 * m0[3] + m0[5]);
			else
				status = GDIPlus.GdipSetMatrixElements (nativeMatrix,
								m0[0] * cos + m0[1] * -sin,
								m0[0] * sin + m0[1] * cos,
								m0[2] * cos + m0[3] * -sin,
								m0[2] * sin + m0[3] * cos,
								m0[4] * cos + m0[5] * -sin + e4,
								m0[4] * sin + m0[5] * cos + e5);
			GDIPlus.CheckStatus (status);
		}

		public void Scale (float scaleX, float scaleY)
		{
			Scale (scaleX, scaleY, MatrixOrder.Prepend);
		}

		public void Scale (float scaleX, float scaleY, MatrixOrder order)
		{
			if (m != null) { Combine (new float [] { scaleX, 0, 0, scaleY, 0, 0 }, order); return; }
			Status status = GDIPlus.GdipScaleMatrix (nativeMatrix, scaleX, scaleY, order);
			GDIPlus.CheckStatus (status);
		}

		public void Shear (float shearX, float shearY)
		{
			Shear (shearX, shearY, MatrixOrder.Prepend);
		}

		public void Shear (float shearX, float shearY, MatrixOrder order)
		{
			if (m != null) { Combine (new float [] { 1, shearY, shearX, 1, 0, 0 }, order); return; }
			Status status = GDIPlus.GdipShearMatrix (nativeMatrix, shearX, shearY, order);
			GDIPlus.CheckStatus (status);
		}

		public void TransformPoints (Point[] pts)
		{
			if (pts == null)
				throw new ArgumentNullException ("pts");
			if (m != null) {
				for (int i = 0; i < pts.Length; i++) {
					float x = pts [i].X, y = pts [i].Y;
					pts [i] = new Point ((int) Math.Round (x * m [0] + y * m [2] + m [4]), (int) Math.Round (x * m [1] + y * m [3] + m [5]));
				}
				return;
			}

			Status status = GDIPlus.GdipTransformMatrixPointsI (nativeMatrix, pts, pts.Length);
			GDIPlus.CheckStatus (status);
		}

		public void TransformPoints (PointF[] pts)
		{
			if (pts == null)
				throw new ArgumentNullException ("pts");
			if (m != null) {
				for (int i = 0; i < pts.Length; i++) {
					float x = pts [i].X, y = pts [i].Y;
					pts [i] = new PointF (x * m [0] + y * m [2] + m [4], x * m [1] + y * m [3] + m [5]);
				}
				return;
			}

			Status status = GDIPlus.GdipTransformMatrixPoints (nativeMatrix, pts, pts.Length);
			GDIPlus.CheckStatus (status);
		}

		public void TransformVectors (Point[] pts)
		{
			if (pts == null)
				throw new ArgumentNullException ("pts");
			if (m != null) {
				for (int i = 0; i < pts.Length; i++) {
					float x = pts [i].X, y = pts [i].Y;
					pts [i] = new Point ((int) Math.Round (x * m [0] + y * m [2]), (int) Math.Round (x * m [1] + y * m [3]));
				}
				return;
			}

			Status status = GDIPlus.GdipVectorTransformMatrixPointsI (nativeMatrix, pts, pts.Length);
			GDIPlus.CheckStatus (status);
		}

		public void TransformVectors (PointF[] pts)
		{
			if (pts == null)
				throw new ArgumentNullException ("pts");
			if (m != null) {
				for (int i = 0; i < pts.Length; i++) {
					float x = pts [i].X, y = pts [i].Y;
					pts [i] = new PointF (x * m [0] + y * m [2], x * m [1] + y * m [3]);
				}
				return;
			}

			Status status = GDIPlus.GdipVectorTransformMatrixPoints (nativeMatrix, pts, pts.Length);
			GDIPlus.CheckStatus (status);
		}

		public void Translate (float offsetX, float offsetY)
		{
			Translate (offsetX, offsetY, MatrixOrder.Prepend);
		}

		public void Translate (float offsetX, float offsetY, MatrixOrder order)
		{
			if (m != null) { Combine (new float [] { 1, 0, 0, 1, offsetX, offsetY }, order); return; }
			Status status = GDIPlus.GdipTranslateMatrix (nativeMatrix, offsetX, offsetY, order);
			GDIPlus.CheckStatus (status);
		}

		public void VectorTransformPoints (Point[] pts)
		{
			TransformVectors (pts);
		}

		internal IntPtr NativeObject
		{
			get{
				return nativeMatrix;
			}
			set	{
				nativeMatrix = value;
			}
		}
	}

	/// <summary>The elements of a managed Matrix by its handle, so code handed a matrix as an IntPtr
	/// (the path and region wrappers in gdipFunctions) can read it without GDI+.</summary>
	internal static class ManagedMatrix
	{
		private static readonly Dictionary<IntPtr, float []> s_matrices = new Dictionary<IntPtr, float []> ();
		private static long s_next = 0x4D400000;

		internal static IntPtr Register (float [] m)
		{
			lock (s_matrices) {
				var h = new IntPtr (++s_next);
				s_matrices [h] = m;
				return h;
			}
		}

		internal static void Unregister (IntPtr h)
		{
			lock (s_matrices) s_matrices.Remove (h);
		}

		/// <summary>A copy of the six elements, or null when the handle is not a managed matrix.</summary>
		internal static float [] Elements (IntPtr h)
		{
			lock (s_matrices)
				return s_matrices.TryGetValue (h, out float [] m) ? (float []) m.Clone () : null;
		}
	}
}
