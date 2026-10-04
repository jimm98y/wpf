// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// GDI's world-to-device MATRIX and its conversions (win32kbase), all in IEEE single precision with
// no fused multiply-adds, as the ARM64 binary computes them:
//
//   DC::vUpdateWtoDXform @140084880   the matrix is the world transform times the page transform,
//                                     scaled by 16 so it maps logical units to 28.4 (FIX); its
//                                     translation is kept as a float and as a FIX rounded by bFToL
//   bFToL @14018a0f8 (mode 6)          float to integer: |v| + 1/2 truncated, the sign put back
//   bCvtPts @14018a1b0                 a point: round(m21 y + m11 x) + fxDx (round(m11 x) + fxDx
//                                     without rotation, 16 x + fxDx at unity)
//   EXFORMOBJR::bXformRound @140071390 in GM_COMPATIBLE the FIX result is snapped to whole pixels,
//                                     (v + 8) & ~15
//   bCvtVts @14018a768                 a vector: the same without the translation
//   EXFORMOBJ::bInverse @140070a60     the device-to-world inverse (FIX to logical)
//   EXFORMOBJ::bXform @1400710a0       a FIX vector through that inverse to logical units
//   EXFORMOBJ::bMultiply @140070c10    a world transform combined with another
//

namespace System.Drawing.WebGpuBackend.Gdip
{
    /// <summary>A GDI MATRIX: four float coefficients, a float translation and its integer
    /// rounding, and the accelerator flags.</summary>
    internal struct GdiXform
    {
        public float M11, M12, M21, M22, Dx, Dy;
        public int FxDx, FxDy;
        public int Accel;

        public const int Scale = 1, Unity = 2, FormatLToFx = 8, FormatFxToL = 0x10, FormatLToL = 0x20, NoTranslation = 0x40;

        public static GdiXform Identity => new GdiXform { M11 = 1, M22 = 1, Accel = Scale | Unity | FormatLToL | NoTranslation };

        /// <summary>bFToL in mode 6: round half away from zero; false past 2^31.</summary>
        public static bool FToL(float v, out int r)
        {
            int e = (BitConverter.SingleToInt32Bits(v) >> 23) & 0xff;
            if (e >= 0x9f) { r = 0; return false; }
            double a = Math.Abs((double)v);
            r = (int)Math.Floor(a + 0.5);
            if (v < 0) r = -r;
            return true;
        }

        public static int FToL(float v) { FToL(v, out int r); return r; }

        /// <summary>A world transform (an XFORM) made the world-to-page MATRIX (vConvertXformToMatrix
        /// @140084fe0).</summary>
        public static GdiXform FromXform(float m11, float m12, float m21, float m22, float dx, float dy)
        {
            var m = new GdiXform { M11 = m11, M12 = m12, M21 = m21, M22 = m22, Dx = dx, Dy = dy };
            FToL(dx, out m.FxDx); FToL(dy, out m.FxDy);
            int f = dx == dy && dy == 0 ? FormatLToL | NoTranslation : FormatLToL;
            if (m12 == 0 && m21 == 0)
            {
                f |= Scale;
                if (m11 == 1 && m22 == 1) f |= Unity;
            }
            m.Accel = f;
            return m;
        }

        /// <summary>EXFORMOBJ::bMultiply: a then b (row vectors, a applied first).</summary>
        public static GdiXform Multiply(in GdiXform a, in GdiXform b)
        {
            var r = new GdiXform();
            if (a.M12 == 0 && a.M21 == 0 && b.M12 == 0 && b.M21 == 0)
            {
                r.M11 = a.M11 * b.M11;
                r.M22 = a.M22 * b.M22;
            }
            else
            {
                r.M11 = b.M21 * a.M12 + a.M11 * b.M11;
                r.M12 = a.M12 * b.M22 + a.M11 * b.M12;
                r.M21 = a.M22 * b.M21 + a.M21 * b.M11;
                r.M22 = a.M21 * b.M12 + a.M22 * b.M22;
            }
            if (a.Dx == 0 && a.Dy == 0)
            {
                r.Dx = b.Dx; r.Dy = b.Dy; r.FxDx = b.FxDx; r.FxDy = b.FxDy;
            }
            else
            {
                r.Dx = a.Dy * b.M21 + b.Dx + a.Dx * b.M11;
                r.Dy = a.Dy * b.M22 + b.Dy + a.Dx * b.M12;
                FToL(r.Dx, out r.FxDx); FToL(r.Dy, out r.FxDy);
            }
            return r;
        }

        /// <summary>DC::vUpdateWtoDXform for MM_TEXT-like pages (the page a pure scale of 16 and an
        /// offset in FIX): the world transform scaled to FIX output.</summary>
        public static GdiXform WorldToDevice(in GdiXform world, float pageM11, float pageM22, float pageDx, float pageDy, bool pageIsSixteen)
        {
            var m = new GdiXform();
            if (pageIsSixteen)
            {
                m.M11 = world.M11 * 16f; m.M12 = world.M12 * 16f; m.M21 = world.M21 * 16f; m.M22 = world.M22 * 16f;
                m.Dx = world.Dx * 16f; m.Dy = world.Dy * 16f;
            }
            else
            {
                m.M11 = pageM11 * world.M11;
                m.M21 = world.M21 * pageM11;
                m.M12 = world.M12 * pageM22;
                m.M22 = world.M22 * pageM22;
                m.Dx = world.Dx * pageM11;
                m.Dy = world.Dy * pageM22;
            }
            m.Dx = pageDx + m.Dx;
            FToL(m.Dx, out m.FxDx);
            m.Dy = pageDy + m.Dy;
            FToL(m.Dy, out m.FxDy);
            int f;
            if (m.M12 == 0 && m.M21 == 0) f = m.M11 != 16f || m.M22 != 16f ? 9 : 0xb;
            else f = 8;
            if (m.FxDx == 0 && m.FxDy == 0) f |= NoTranslation;
            m.Accel = f;
            return m;
        }

        /// <summary>bCvtPts: a logical point to FIX.</summary>
        public void Point(int x, int y, out int fx, out int fy)
        {
            switch (Accel & 0xb)
            {
                case 0xb:
                    fx = FxDx + x * 16; fy = FxDy + y * 16;
                    return;
                case 9:
                case 1:
                    fx = FxDx + FToL((float)x * M11);
                    fy = FxDy + FToL((float)y * M22);
                    return;
                default:
                    fx = FxDx + FToL((float)y * M21 + M11 * (float)x);
                    fy = FxDy + FToL((float)y * M22 + (float)x * M12);
                    return;
            }
        }

        /// <summary>bCvtVts: a logical vector to FIX.</summary>
        public void Vector(int x, int y, out int fx, out int fy)
        {
            if ((Accel & Unity) != 0) { fx = x << 4; fy = y << 4; return; }
            if ((Accel & 3) == 1)
            {
                fx = FToL((float)x * M11);
                fy = FToL((float)y * M22);
                return;
            }
            fx = FToL(M21 * (float)y + M11 * (float)x);
            fy = FToL(M22 * (float)y + M12 * (float)x);
        }

        /// <summary>EXFORMOBJ::bInverse: the FIX-to-logical inverse of a world-to-device matrix.</summary>
        public bool Inverse(out GdiXform r)
        {
            r = new GdiXform { Accel = (Accel & ~0x1f) | (Accel & 7) | FormatFxToL };
            if ((Accel & Unity) != 0)
            {
                r.M11 = 0.0625f; r.M22 = 0.0625f;
                r.Dx = -Dx * 0.0625f; r.Dy = -Dy * 0.0625f;
                r.FxDx = -(FxDx >> 4); r.FxDy = -(FxDy >> 4);
                return true;
            }
            float det = M22 * M11 - M12 * M21;
            if (det == 0) return false;
            float m12, m21;
            if ((Accel & Scale) == 0) { m12 = -(M12 / det); m21 = -(M21 / det); }
            else { m12 = 0; m21 = 0; }
            r.M12 = m12; r.M21 = m21;
            r.M11 = M22 / det;
            float m22 = M11 / det;
            r.M22 = m22;
            if ((Accel & NoTranslation) != 0) return true;
            float dx = (M22 / det) * Dx;
            float dy;
            if ((Accel & Scale) == 0)
            {
                dx = m21 * Dy + dx;
                dy = m12 * Dx + m22 * Dy;
            }
            else dy = m22 * Dy;
            r.Dx = -dx; r.Dy = -dy;
            return FToL(r.Dx, out r.FxDx) && FToL(r.Dy, out r.FxDy);
        }

        /// <summary>EXFORMOBJ::bXform(VECTORFX, VECTORL) through an inverse: a FIX vector in
        /// logical units.</summary>
        public void VectorToLogical(int x, int y, out int lx, out int ly)
        {
            if ((Accel & Unity) != 0) { lx = x >> 4; ly = y >> 4; return; }
            if ((Accel & 3) == 1)
            {
                lx = FToL((float)x * M11);
                ly = FToL((float)y * M22);
                return;
            }
            lx = FToL(M21 * (float)y + M11 * (float)x);
            ly = FToL(M22 * (float)y + M12 * (float)x);
        }

        /// <summary>bCvtPts1 through an FXTOL inverse: a FIX device point to logical units.</summary>
        public void Point2(int fx, int fy, out int x, out int y)
        {
            if ((Accel & Unity) != 0) { x = (fx >> 4) + FxDx; y = (fy >> 4) + FxDy; return; }
            if ((Accel & Scale) != 0)
            {
                x = FToL((float)fx * M11) + FxDx;
                y = FToL((float)fy * M22) + FxDy;
                return;
            }
            x = FToL((float)fy * M21 + M11 * (float)fx) + FxDx;
            y = FToL((float)fy * M22 + (float)fx * M12) + FxDy;
        }
    }
}
