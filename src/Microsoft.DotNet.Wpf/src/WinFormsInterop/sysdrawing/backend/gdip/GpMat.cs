// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// GDI+'s GpMatrix arithmetic, in single precision exactly as gdiplus.dll does it (10.0.26100):
// ComputeComplexity @180034598, Transform @180034a90, MultiplyMatrix @1800349e0, Scale @1800da788,
// Translate @1800da968, Rotate @1800da680, InferAffineMatrix @1800346a8 (rect to rect) and
// @180034750 (rect to parallelogram). A metafile's recorded points (DrawImage's parallelogram) and
// its bounds come out of this arithmetic, so it is ported rather than delegated to Matrix.
//

using System.Drawing.Drawing2D;

namespace System.Drawing.WebGpuBackend.Gdip
{
    internal struct GpMat
    {
        public float M11, M12, M21, M22, Dx, Dy;
        public int Complexity;   // bit 0 translate, 1 scale, 2 rotate, 3 shear (0 = identity)

        public static GpMat Identity => new GpMat { M11 = 1f, M22 = 1f };

        public GpMat(float m11, float m12, float m21, float m22, float dx, float dy)
        {
            M11 = m11; M12 = m12; M21 = m21; M22 = m22; Dx = dx; Dy = dy;
            Complexity = 0;
            Complexity = ComputeComplexity();
        }

        public static GpMat From(Matrix m)
        {
            if (m == null) return Identity;
            float[] e = m.Elements;
            return new GpMat(e[0], e[1], e[2], e[3], e[4], e[5]);
        }

        public Matrix ToMatrix() => new Matrix(M11, M12, M21, M22, Dx, Dy);

        public bool IsIdentity => Complexity == 0;

        /// <summary>ComputeComplexity.</summary>
        public int ComputeComplexity()
        {
            const float eps = 1.1920928955078125e-07f;
            float a11 = Math.Abs(M11), a22 = Math.Abs(M22), a12 = Math.Abs(M12), a21 = Math.Abs(M21);
            float big = a11 <= a22 ? a22 : a11;
            float other = a12 <= a21 ? a21 : a12;
            float pick;
            if (big <= other) pick = a12 <= a21 ? M21 : M12;
            else pick = a11 <= a22 ? M22 : M11;
            float tol = Math.Abs(pick) * eps;
            bool rot = true;
            if (a12 < tol && !float.IsNaN(a21) && !float.IsNaN(tol)) rot = tol <= a21;
            int c = 0xf;
            if (rot)
            {
                if (Math.Abs(M11 - M22) < tol && Math.Abs(M21 + M12) < tol)
                {
                    c = 5;
                    if (eps <= Math.Abs((M12 * M12 + M11 * M11) - 1.0f)) c = 7;
                }
            }
            else
            {
                c = 3;
                if (Math.Abs(M11 - 1.0f) < eps && Math.Abs(M22 - 1.0f) < eps) c = 1;
            }
            if (Dx == 0f && Dy == 0f) c &= ~1;
            return c;
        }

        /// <summary>Transform(PointF*, n).</summary>
        public void Transform(PointF[] p)
        {
            Transform(p, 0, p.Length);
        }

        public void Transform(PointF[] p, int o, int n)
        {
            int c = Complexity;
            if (n <= 0 || c == 0) return;
            if ((c & ~1) == 0)
            {
                for (int i = o; i < o + n; i++) p[i] = new PointF(Dx + p[i].X, p[i].Y + Dy);
                return;
            }
            if ((c & ~3) == 0)
            {
                for (int i = o; i < o + n; i++) p[i] = new PointF(M11 * p[i].X + Dx, p[i].Y * M22 + Dy);
                return;
            }
            for (int i = o; i < o + n; i++)
            {
                float x = p[i].X, y = p[i].Y;
                p[i] = new PointF(M11 * x + M21 * y + Dx, M22 * y + M12 * x + Dy);
            }
        }

        /// <summary>MultiplyMatrix(result, a, b): result = a then b.</summary>
        public static GpMat Multiply(GpMat a, GpMat b)
        {
            var r = new GpMat
            {
                M11 = b.M21 * a.M12 + b.M11 * a.M11,
                M12 = b.M12 * a.M11 + a.M12 * b.M22,
                M21 = a.M22 * b.M21 + a.M21 * b.M11,
                M22 = b.M12 * a.M21 + a.M22 * b.M22,
                Dx = a.Dy * b.M21 + a.Dx * b.M11 + b.Dx,
                Dy = a.Dx * b.M12 + a.Dy * b.M22 + b.Dy,
            };
            r.Complexity = r.ComputeComplexity();
            return r;
        }

        public void Scale(float sx, float sy, MatrixOrder order)
        {
            if (order == MatrixOrder.Prepend)
            {
                M11 *= sx; M12 *= sx; M21 *= sy; M22 *= sy;
            }
            else
            {
                M21 *= sx; M11 *= sx; M12 *= sy; M22 *= sy; Dx *= sx; Dy *= sy;
            }
            Complexity = ComputeComplexity();
        }

        public void Translate(float tx, float ty, MatrixOrder order)
        {
            float ox = tx, oy = ty;
            if (order == MatrixOrder.Prepend)
            {
                oy = M12 * tx + M22 * ty;
                ox = M11 * tx + M21 * ty;
            }
            Dx += ox;
            Dy += oy;
            Complexity |= 1;
        }

        public void Rotate(float angle, MatrixOrder order)
        {
            float r = angle * 0.017453292f;
            float s = MathF.Sin(r), c = MathF.Cos(r);
            float m11 = M11, m12 = M12, m21 = M21;
            float n11, n12, n21, n22;
            if (order == MatrixOrder.Prepend)
            {
                n11 = m11 * c + m21 * s;
                n12 = M22 * s + m12 * c;
                n21 = m21 * c - m11 * s;
                n22 = M22 * c - m12 * s;
            }
            else
            {
                n11 = m11 * c - m12 * s;
                n12 = m11 * s + m12 * c;
                n21 = m21 * c - M22 * s;
                n22 = M22 * c + m21 * s;
                float dx = Dx;
                Dx = dx * c - Dy * s;
                Dy = Dy * c + dx * s;
            }
            M11 = n11; M12 = n12; M21 = n21; M22 = n22;
            Complexity = ComputeComplexity();
        }

        public void Multiply(GpMat m, MatrixOrder order)
        {
            this = order == MatrixOrder.Prepend ? Multiply(m, this) : Multiply(this, m);
        }

        /// <summary>InferAffineMatrix(destRect, srcRect): false when the source has no area.</summary>
        public static bool InferAffine(RectangleF dst, RectangleF src, out GpMat m)
        {
            m = Identity;
            float sx = src.X, sy = src.Y, dx0 = dst.X, dy0 = dst.Y;
            float sr = src.Width + sx, dr = dst.Width + dx0, db = dst.Height + dy0;
            if (sx == sr) return false;
            float sb = src.Height + sy;
            if (sy == sb) return false;
            float a = (dr - dx0) / (sr - sx);
            float d = (db - dy0) / (sb - sy);
            m = new GpMat { M11 = a, M22 = d, M12 = 0f, M21 = 0f, Dx = dr - a * sr, Dy = db - d * sb };
            m.Complexity = m.ComputeComplexity();
            return true;
        }

        /// <summary>InferAffineMatrix(destPoints[3], srcRect).</summary>
        public static bool InferAffine(PointF[] dst, RectangleF src, out GpMat m)
        {
            m = Identity;
            float x = src.X, y = src.Y, h = src.Height;
            float x0 = dst[0].X, y0 = dst[0].Y, x1 = dst[1].X, y1 = dst[1].Y, x2 = dst[2].X, y2 = dst[2].Y;
            float nh = -h;
            float det = ((src.Width + x) * (y + h) - y * x) + (x * nh - y * src.Width);
            if (Math.Abs(det) < 1.1920928955078125e-07f) return false;
            float inv = 1.0f / det;
            var r = new GpMat();
            r.M11 = (h * x1 + nh * x0) * inv;
            r.M12 = (h * y1 + nh * y0) * inv;
            float w = src.Width;
            r.M21 = (w * x2 + -w * x0) * inv;
            r.M22 = (w * y2 + -w * y0) * inv;
            float sx = src.X, sy = src.Y;
            float k = (sx + src.Width) * (sy + src.Height) - sx * sy;
            float a = -sx * src.Height;
            float b = -sy * src.Width;
            r.Dx = (a * x1 + k * x0 + b * x2) * inv;
            r.Dy = (a * y1 + k * y0 + b * y2) * inv;
            r.Complexity = r.ComputeComplexity();
            m = r;
            return true;
        }

        /// <summary>The bounds of a rectangle under the matrix: two corners when there is no
        /// rotation, four otherwise (FillRects / DrawRects / GetBounds all do this).</summary>
        public void TransformBounds(ref float l, ref float t, ref float r, ref float b)
        {
            if (Complexity == 0) return;
            if ((Complexity & ~3) == 0)
            {
                var p = new[] { new PointF(l, t), new PointF(r, b) };
                Transform(p);
                l = Math.Min(p[0].X, p[1].X); r = Math.Max(p[0].X, p[1].X);
                t = Math.Min(p[0].Y, p[1].Y); b = Math.Max(p[0].Y, p[1].Y);
                // GDI+ picks with "if (a < b)", which keeps the second on a tie; equal either way.
                return;
            }
            var q = new[] { new PointF(l, t), new PointF(r, b), new PointF(l, b), new PointF(r, t) };
            Transform(q);
            float minx = q[0].X, maxx = q[0].X, miny = q[0].Y, maxy = q[0].Y;
            for (int i = 1; i < 4; i++)
            {
                if (q[i].X <= minx) minx = q[i].X;
                if (maxx <= q[i].X) maxx = q[i].X;
                if (q[i].Y <= miny) miny = q[i].Y;
                if (maxy <= q[i].Y) maxy = q[i].Y;
            }
            l = minx; r = maxx; t = miny; b = maxy;
        }
    }
}
