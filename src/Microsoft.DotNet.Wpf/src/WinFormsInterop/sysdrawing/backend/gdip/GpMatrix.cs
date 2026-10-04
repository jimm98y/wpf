// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// GDI+'s GpMatrix, operation for operation (gdiplus.dll 10.0.26100, arm64, public PDB). Every float
// expression is evaluated in the order and with the roundings of the binary: MSVC arm64 /fp:precise
// emits no fused multiply-adds here, so each product and sum is rounded to float on its own.
//
//   ComputeComplexity  @180034598   bit 0 translation, bit 1 scale, bit 2 rotation, bit 3 shear;
//                                   tolerance 5.9604645e-4 relative to the largest element
//   Rotate             @1800da680   radians = degrees * 0.017453292f, CRT sinf/cosf
//   Scale/Shear/Translate/MultiplyMatrix/Invert   as decompiled
//   Transform(PointF -> 28.4 POINT) @180034b60    (v * 16 + 0.5) truncated, then (v + 15) >> 4
//

namespace System.Drawing.WebGpuBackend.Gdip
{
    internal struct GpMatrix
    {
        public float M11, M12, M21, M22, Dx, Dy;
        public int Complexity;

        public const int Identity = 0, TranslationBit = 1, ScaleBit = 2, RotationBit = 4, ShearBit = 8;

        public static GpMatrix CreateIdentity () => new GpMatrix { M11 = 1, M22 = 1 };

        public GpMatrix (float m11, float m12, float m21, float m22, float dx, float dy)
        {
            M11 = m11; M12 = m12; M21 = m21; M22 = m22; Dx = dx; Dy = dy;
            Complexity = 0;
            Complexity = ComputeComplexity ();
        }

        public bool IsIdentity => Complexity == 0;
        public bool IsTranslate => (Complexity & ~TranslationBit) == 0;
        public bool IsTranslateScale => (Complexity & ~(TranslationBit | ScaleBit)) == 0;

        const float Tolerance = 0.00059604645f;   // @1800346a0

        public int ComputeComplexity ()
        {
            float a11 = MathF.Abs (M11), a22 = MathF.Abs (M22), a12 = MathF.Abs (M12), a21 = MathF.Abs (M21);
            // The largest of the four, as the binary picks it (max of the diagonal pair vs the off pair).
            float d = a11 <= a22 ? M22 : M11;
            float o = a12 <= a21 ? M21 : M12;
            float big = (a11 <= a22 ? a22 : a11) <= (a12 <= a21 ? a21 : a12) ? o : d;
            float tol = MathF.Abs (big) * Tolerance;
            int c = 0xf;
            bool offDiagonal = true;
            if (a12 < tol && !float.IsNaN (a21) && !float.IsNaN (tol))
                offDiagonal = tol <= a21;
            if (offDiagonal) {
                if (MathF.Abs (M11 - M22) < tol && MathF.Abs (M21 + M12) < tol) {
                    c = 5;
                    if (Tolerance <= MathF.Abs ((M12 * M12 + M11 * M11) - 1f))
                        c = 7;
                }
            } else {
                c = 3;
                if (MathF.Abs (M11 - 1f) < Tolerance && MathF.Abs (M22 - 1f) < Tolerance)
                    c = 1;
            }
            if (Dx == 0f && Dy == 0f)
                c &= ~1;
            return c;
        }

        void Update () => Complexity = ComputeComplexity ();

        // MatrixOrder: Prepend = 0, Append = 1.
        public void Rotate (float degrees, bool append)
        {
            float r = degrees * 0.017453292f;
            float s = Crt.Sinf (r), c = Crt.Cosf (r);
            float m11 = M11, m12 = M12, m21 = M21;
            float n11, n12, n21, n22;
            if (!append) {
                n11 = m11 * c + m21 * s;
                n12 = M22 * s + m12 * c;
                n21 = m21 * c - m11 * s;
                n22 = M22 * c - m12 * s;
            } else {
                n11 = m11 * c - m12 * s;
                n12 = m11 * s + m12 * c;
                float dx = Dx;
                n21 = m21 * c - M22 * s;
                n22 = M22 * c + m21 * s;
                Dx = dx * c - Dy * s;
                Dy = Dy * c + dx * s;
            }
            M11 = n11; M12 = n12; M21 = n21; M22 = n22;
            Update ();
        }

        public void Scale (float sx, float sy, bool append)
        {
            if (!append) {
                M11 *= sx; M12 *= sx; M21 *= sy; M22 *= sy;
            } else {
                M21 *= sx; M11 *= sx; M12 *= sy; M22 *= sy; Dx *= sx; Dy *= sy;
            }
            Update ();
        }

        public void Shear (float shx, float shy, bool append)
        {
            float m21 = M21, m11 = M11, m12 = M12;
            if (!append) {
                M21 = m11 * shx + m21;
                M11 = m21 * shy + m11;
                M12 = M22 * shy + m12;
                M22 = m12 * shx + M22;
            } else {
                M11 = m12 * shx + m11;
                M12 = m11 * shy + m12;
                M21 = M22 * shx + m21;
                M22 = m21 * shy + M22;
                float dx = Dx;
                Dx = Dy * shx + dx;
                Dy = dx * shy + Dy;
            }
            Update ();
        }

        public void Translate (float dx, float dy, bool append)
        {
            if (!append) {
                float ty = M12 * dx + M22 * dy;
                dx = M11 * dx + M21 * dy;
                dy = ty;
            }
            Dx = Dx + dx;
            Dy = Dy + dy;
            Update ();
        }

        /// <summary>GpMatrix::MultiplyMatrix(result, a, b) = a * b (a applied first).</summary>
        public static GpMatrix Multiply (in GpMatrix a, in GpMatrix b)
        {
            var r = new GpMatrix {
                M11 = b.M21 * a.M12 + b.M11 * a.M11,
                M12 = b.M12 * a.M11 + a.M12 * b.M22,
                M21 = a.M22 * b.M21 + a.M21 * b.M11,
                M22 = b.M12 * a.M21 + a.M22 * b.M22,
                Dx = a.Dy * b.M21 + a.Dx * b.M11 + b.Dx,
                Dy = a.Dx * b.M12 + a.Dy * b.M22 + b.Dy,
            };
            r.Update ();
            return r;
        }

        /// <summary>Matrix.Multiply(m, order): Prepend = m * this, Append = this * m.</summary>
        public void Multiply (in GpMatrix m, bool append)
        {
            this = append ? Multiply (this, m) : Multiply (m, this);
        }

        public bool IsInvertible
        {
            get {
                if (Complexity == 0) return true;
                float det = M11 * M22 - M12 * M21;
                float d = det != 0f ? det : 1f;
                return 1.1920929e-06f <= MathF.Abs ((0f - det) / d);
            }
        }

        public bool Invert ()
        {
            if (Complexity == 0) return true;
            float m21 = M21, m22 = M22, m11 = M11, m12 = M12;
            float det = m11 * m22 - m12 * m21;
            float d = det != 0f ? det : 1f;
            if (!(1.1920929e-06f <= MathF.Abs ((0f - det) / d))) return false;
            float inv = 1f / det;
            float ndx = (Dy * m21 - Dx * m22) * inv;
            float ndy = (Dx * m12 - Dy * m11) * inv;
            float n11 = m22 * inv, n12 = -m12 * inv, n21 = -m21 * inv, n22 = m11 * inv;
            if (!float.IsFinite (n11) || !float.IsFinite (n12) || !float.IsFinite (n21) || !float.IsFinite (n22) || !float.IsFinite (ndx) || !float.IsFinite (ndy))
                return false;
            M11 = n11; M12 = n12; M21 = n21; M22 = n22; Dx = ndx; Dy = ndy;
            Update ();
            return true;
        }

        /// <summary>GpMatrix::Transform(PointF*, int) @180034a90, in place.</summary>
        public void Transform (ref float x, ref float y)
        {
            if (Complexity == 0) return;
            if ((Complexity & ~1) == 0) { x = Dx + x; y = y + Dy; return; }
            if ((Complexity & ~3) == 0) { x = M11 * x + Dx; y = y * M22 + Dy; return; }
            float px = x;
            x = M11 * px + M21 * y + Dx;
            y = M22 * y + M12 * px + Dy;
        }

        public PointF Transform (PointF p)
        {
            float x = p.X, y = p.Y;
            Transform (ref x, ref y);
            return new PointF (x, y);
        }

        public void Transform (PointF[] pts)
        {
            for (int i = 0; i < pts.Length; i++) pts [i] = Transform (pts [i]);
        }

        /// <summary>GpMatrix::VectorTransform: the linear part only.</summary>
        public PointF VectorTransform (PointF p)
        {
            if ((Complexity & ~1) == 0) return p;
            if ((Complexity & ~3) == 0) return new PointF (M11 * p.X, p.Y * M22);
            return new PointF (M11 * p.X + M21 * p.Y, M22 * p.Y + M12 * p.X);
        }

        /// <summary>RasterizerCeiling @18000c0e8: v (already x16) to 28.4, rounded up.</summary>
        public static int RasterizerCeiling (float v)
        {
            float f = v * 16f + 0.5f;
            return ((int) f + 15) >> 4;
        }

        /// <summary>GpMatrix::Transform(PointF*, POINT*, int) @180034b60. This matrix already carries
        /// the x16 to 28.4: the result is 28.4 with a ceiling taken in sixteenths of a sixteenth.</summary>
        public void TransformFix (PointF p, out int fx, out int fy)
        {
            if ((Complexity & ~1) == 0) {
                fx = RasterizerCeiling (p.X + Dx);
                fy = RasterizerCeiling (p.Y + Dy);
                return;
            }
            float vx, vy;
            if ((Complexity & ~3) == 0) {
                vx = (M11 * p.X + Dx) * 16f + 0.5f;
                vy = (p.Y * M22 + Dy) * 16f + 0.5f;
            } else {
                vx = (M21 * p.Y + M11 * p.X + Dx) * 16f + 0.5f;
                vy = (M22 * p.Y + M12 * p.X + Dy) * 16f + 0.5f;
            }
            fx = ((int) vx + 15) >> 4;
            fy = ((int) vy + 15) >> 4;
        }

        public override string ToString () => $"[{M11} {M12} {M21} {M22} {Dx} {Dy}] c{Complexity}";
    }

    /// <summary>The C runtime's single-precision trigonometry, which GDI+ calls (sinf/cosf from the
    /// UCRT). .NET's MathF calls the same CRT on Windows; elsewhere this is where a bit-exact
    /// implementation goes if the platform's libm disagrees.</summary>
    internal static class Crt
    {
        public static float Sinf (float x) => MathF.Sin (x);
        public static float Cosf (float x) => MathF.Cos (x);
    }
}
