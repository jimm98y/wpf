// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// GDI's box shapes, as win32kfull builds their paths:
//
//   EBOX::EBOX @1401a1008      the bounding rectangle in device units. In GM_ADVANCED its three
//                              corners (top right A, top left B, bottom left C) go through the
//                              transform unrounded and the box is inclusive. In GM_COMPATIBLE
//                              the two corners are snapped to pixels, the far edges pulled in a
//                              pixel (the box excludes its right and bottom), and a box under a
//                              pixel either way draws nothing. A PS_NULL pen nudges integer
//                              corners out by a quarter pixel (and, compatibly, the exclusion
//                              becomes two pixels). A PS_INSIDEFRAME geometric pen shrinks the
//                              box by half its width, or, when wider than the box, the whole
//                              shape is filled with the pen.
//   bEllipse @1401a1578        four Beziers round the box, the control points the corners less
//                              0x729d7775 / 2^32 of the half sides.
//   bRoundRect @1401a1cf0      the same per corner, the corner ellipse in the ratio of
//                              (cx, cy) to the box (efHalfDiff), at most the half box.
//

namespace System.Drawing.WebGpuBackend.Gdip
{
    internal sealed class GdiBox
    {
        public bool Empty, FillInsideFrame;
        public int Ax, Ay, Bx, By, Cx, Cy, Dx, Dy, Mx, My;   // corners, D bottom right, M centre
        public int Hx, Hy, Vx, Vy;                            // half A - B, half B - C
        public int L, T, R, B;                                // the logical rectangle, ordered

        const long K = 0x729d7775;
        static int MulHi(int v) => (int)((v * K) >> 32);

        /// <summary>efHalfDiff @1401a2220: (a - b) / 2 in float.</summary>
        public static float HalfDiff(int a, int b)
        {
            float f = (float)((a >> 1) - (b >> 1));
            if (((a ^ b) & 1) != 0) f = (a & 1) == 0 ? f - 0.5f : f + 0.5f;
            return f;
        }

        /// <summary>EBOX::EBOX(XDCOBJ&, RECTL&, LINEATTRS*, BOOL): <paramref name="nullNudge"/> is
        /// the last argument (Ellipse, RoundRect, the arcs pass it; Rectangle does not);
        /// <paramref name="penStyle"/> the pen's style, <paramref name="geometricWidth"/> its
        /// width when geometric (0 otherwise).</summary>
        public static GdiBox Make(int l, int t, int r, int b, in GdiXform m, bool advanced, bool nullNudge, int penStyle, int geometricWidth, bool clockwise = false)
        {
            var e = new GdiBox();
            if (advanced)
            {
                if (r < l) (l, r) = (r, l);
                if (b < t) (t, b) = (b, t);
            }
            else
            {
                // A compatible DC orders the box by the page's flips (PTOD_EFM11_NEGATIVE,
                // PTOD_EFM22_NEGATIVE): ascending in device units.
                if (m.M11 < 0 ? l < r : r < l) (l, r) = (r, l);
                if (m.M22 < 0 ? t < b : b < t) (t, b) = (b, t);
            }
            if (clockwise) (t, b) = (b, t);   // DCPATH_CLOCKWISE: the figure runs the other way
            e.L = l; e.T = t; e.R = r; e.B = b;
            bool shrink = false;
            if ((penStyle & 0xf) == 6 && geometricWidth > 0)
            {
                float half = (float)(geometricWidth >> 1);
                if ((geometricWidth & 1) != 0) half += 0.5f;
                float hx = Math.Abs(HalfDiff(l, r)), hy = Math.Abs(HalfDiff(t, b));
                if (half <= hx && half <= hy) shrink = true;
                else e.FillInsideFrame = true;
            }
            if (advanced || shrink || e.FillInsideFrame)
            {
                m.Point(r, t, out e.Ax, out e.Ay);
                m.Point(l, t, out e.Bx, out e.By);
                m.Point(l, b, out e.Cx, out e.Cy);
                if (!advanced)
                {
                    e.Ax = (e.Ax + 8) & ~15; e.Ay = (e.Ay + 8) & ~15; e.Bx = (e.Bx + 8) & ~15;
                    e.By = (e.By + 8) & ~15; e.Cx = (e.Cx + 8) & ~15; e.Cy = (e.Cy + 8) & ~15;
                }
                if (nullNudge && penStyle == 5 && ((e.Ax | e.Ay | e.Cx | e.Cy) & 15) == 0)
                {
                    int ix = e.Cx < e.Ax ? 4 : -4;
                    e.Bx -= ix; e.Cx -= ix; e.Ax += ix;
                    int iy = e.Ay < e.Cy ? 4 : -4;
                    e.Ay -= iy; e.By -= iy; e.Cy += iy;
                }
                if (shrink)
                {
                    int wx = r < l ? -geometricWidth : geometricWidth, wy = b < t ? -geometricWidth : geometricWidth;
                    m.Vector(-wx, wy, out int v0x, out int v0y);
                    m.Vector(wx, wy, out int v1x, out int v1y);
                    e.Ax += (v0x + 1) >> 1; e.Ay += (v0y + 1) >> 1;
                    e.Bx += (v1x + 1) >> 1; e.By += (v1y + 1) >> 1;
                    e.Cx -= (v0x + 1) >> 1; e.Cy -= (v0y + 1) >> 1;
                }
            }
            else
            {
                m.Point(l, t, out int x0, out int y0);
                m.Point(r, b, out int x1, out int y1);
                x0 = (x0 + 8) & ~15; y0 = (y0 + 8) & ~15; x1 = (x1 + 8) & ~15; y1 = (y1 + 8) & ~15;
                int min = 16;
                if (nullNudge && penStyle == 5)
                {
                    int ix = x1 > x0 ? 4 : -4;
                    x1 += ix; x0 -= ix;
                    int iy = y1 > y0 ? 4 : -4;
                    y0 -= iy; y1 += iy;
                    min = 32;
                }
                int dx = x1 - x0, dy = y1 - y0;
                if (Math.Abs(dx) < min || Math.Abs(dy) < min) { e.Empty = true; return e; }
                if (dx > 0) x1 -= min; else x0 -= min;
                if (dy > 0) y1 -= min; else y0 -= min;
                e.Ax = x1; e.Ay = y0; e.Bx = x0; e.By = y0; e.Cx = x0; e.Cy = y1;
            }
            int abx = e.Ax - e.Bx, aby = e.Ay - e.By, bcx = e.Bx - e.Cx, bcy = e.By - e.Cy;
            e.Dx = e.Cx + abx; e.Dy = e.Cy + aby;
            e.Hx = (abx + 1) >> 1; e.Hy = (aby + 1) >> 1;
            e.Vx = (bcx + 1) >> 1; e.Vy = (bcy + 1) >> 1;
            e.Mx = e.Cx + e.Hx + e.Vx; e.My = e.Cy + e.Hy + e.Vy;
            return e;
        }

        /// <summary>bEllipse @1401a1578.</summary>
        public void Ellipse(GdiPath p)
        {
            int kvx = MulHi(Vx), kvy = MulHi(Vy), khx = MulHi(Hx), khy = MulHi(Hy);
            p.MoveTo(Vx + Dx, Dy + Vy);
            p.BezierTo(Ax - kvx, Ay - kvy, Ax - khx, Ay - khy, Ax - Hx, Ay - Hy);
            p.BezierTo(Bx + khx, By + khy, Bx - kvx, By - kvy, Bx - Vx, By - Vy);
            p.BezierTo(Cx + kvx, Cy + kvy, Cx + khx, Cy + khy, Cx + Hx, Cy + Hy);
            p.BezierTo(Dx - khx, Dy - khy, Dx + kvx, Dy + kvy, Dx + Vx, Dy + Vy);
            p.CloseFigure();
        }

        /// <summary>bRoundRect @1401a1cf0 with the corner ellipse's width and height.</summary>
        public void RoundRect(GdiPath p, int cx, int cy)
        {
            float hx = HalfDiff(L, R), hy = HalfDiff(T, B);
            float rx, ry;
            if (hx == 0f || hy == 0f) { rx = 0f; ry = 0f; }
            else
            {
                rx = (float)Math.Abs(cx) / Math.Abs(hx);
                ry = (float)Math.Abs(cy) / Math.Abs(hy);
            }
            rx = rx <= 2f ? rx * 0.5f : 1f;
            ry = ry <= 2f ? ry * 0.5f : 1f;
            int p0 = GdiXform.FToL((float)Hx * rx), p1 = GdiXform.FToL((float)Hy * rx);
            int p2 = GdiXform.FToL((float)Vx * ry), p3 = GdiXform.FToL((float)Vy * ry);
            int k0 = MulHi(p0), k1 = MulHi(p1), k2 = MulHi(p2), k3 = MulHi(p3);
            p.MoveTo(Ax - p2, Ay - p3);
            p.BezierTo(Ax - k2, Ay - k3, Ax - k0, Ay - k1, Ax - p0, Ay - p1);
            p.LineTo(Bx + p0, By + p1);
            p.BezierTo(Bx + k0, By + k1, Bx - k2, By - k3, Bx - p2, By - p3);
            p.LineTo(Cx + p2, Cy + p3);
            p.BezierTo(Cx + k2, Cy + k3, Cx + k0, Cy + k1, Cx + p0, Cy + p1);
            p.LineTo(Dx - p0, Dy - p1);
            p.BezierTo(Dx - k0, Dy - k1, Dx + k2, Dy + k3, Dx + p2, Dy + p3);
            p.CloseFigure();
        }

        /// <summary>EBOX::ptlXform @1401a2258: a point of the unit circle (cos, sin) on the box:
        /// the centre plus the half sides scaled, each rounded half away from zero.</summary>
        public void Xform(float c, float s, out int x, out int y)
        {
            x = Mx + GdiXform.FToL((float)Hx * c + (float)Vx * s);
            y = My + GdiXform.FToL((float)Hy * c + (float)Vy * s);
        }

        /// <summary>GrepRectangle's path (A, B, C, D, closed).</summary>
        public void Rectangle(GdiPath p)
        {
            p.MoveTo(Ax, Ay);
            p.LineTo(Bx, By);
            p.LineTo(Cx, Cy);
            p.LineTo(Dx, Dy);
            p.CloseFigure();
        }
    }

    /// <summary>
    /// GDI's arcs (win32kfull NtGdiArcInternal @1401a2660): the start and end radials as angles
    /// (vArctan @1401a2338, over the box's centre and half sides from efHalfDiff, in the logical
    /// rectangle EBOX ordered), their points on the unit circle (vCosSin @1401a24f8 from the 33
    /// entry gaefSin table, or the Taylor series of vCosSinPrecise @1402efc20 when the sweep is
    /// under three degrees), and the arc as one Bezier per quadrant crossed (bPartialArc
    /// @1401a1778): a partial quadrant from the two tangents' crossing scaled by 4/3 cos / (1 +
    /// cos) of the half angle (bPartialQuadrantArc @1401a1a30, efCos from the same table), a whole
    /// one as bEllipse draws it.
    /// </summary>
    internal static class GdiArc
    {
        // win32kbase's tables: atan(i / 32) in degrees, sin(i * 90 / 32 degrees).
        static readonly float[] Arctan = Floats(
            0x00000000, 0x3fe51bca, 0x4064e2aa, 0x40ab62eb, 0x40e40022, 0x410e172e, 0x4129ea1c, 0x41456ce7,
            0x41609474, 0x417b5695, 0x418ad50b, 0x4197c365, 0x41a472c8, 0x41b0e026, 0x41bd08f7, 0x41c8eb2f,
            0x41d4853a, 0x41dfd5f7, 0x41eadcae, 0x41f59908, 0x42000583, 0x4205197c, 0x420a08ba, 0x420ed3a7,
            0x42137ac6, 0x4217feb4, 0x421c601d, 0x42209fbe, 0x4224be63, 0x4228bcdf, 0x422c9c0c, 0x42305ccb,
            0x42340000, 0x00000000);   // a ratio of exactly one reads the word after the table
        static readonly float[] Sine = Floats(
            0x00000000, 0x3d48fb30, 0x3dc8bd36, 0x3e164083, 0x3e47c5c2, 0x3e78cfcc, 0x3e94a031, 0x3eac7cd4,
            0x3ec3ef15, 0x3edae880, 0x3ef15aea, 0x3f039c3d, 0x3f0e39da, 0x3f187fc0, 0x3f226799, 0x3f2beb4a,
            0x3f3504f3, 0x3f3daef9, 0x3f45e403, 0x3f4d9f02, 0x3f54db31, 0x3f5b941a, 0x3f61c598, 0x3f676bd8,
            0x3f6c835e, 0x3f710908, 0x3f74fa0b, 0x3f7853f8, 0x3f7b14be, 0x3f7d3aac, 0x3f7ec46d, 0x3f7fb10f,
            0x3f800000);
        static readonly float[] AxisCoord = { 0f, 1f, 0f, -1f };
        static readonly float[] AxisAngle = { 0f, 90f, 180f, 270f };
        static readonly byte[] Quadrants = { 0, 1, 3, 2, 0, 1, 3, 2 };
        const float SineFactor = 0.35555556f;          // FP_SINE_FACTOR 0x3eb60b61 (32 / 90)
        const float Pi = 3.1415925f;                   // FP_PI 0x40490fda
        const float Epsilon = 1.52587890625e-05f;      // FP_EPSILON 0x37800000
        const float FourThirds = 1.3333334f;           // FP_4DIV3 0x3faaaaab

        static float[] Floats(params uint[] bits)
        {
            var r = new float[bits.Length];
            for (int i = 0; i < r.Length; i++) r[i] = BitConverter.Int32BitsToSingle((int)bits[i]);
            return r;
        }

        /// <summary>eFraction: the fractional part (of a non-negative value).</summary>
        static float Fraction(float x)
        {
            int e = (BitConverter.SingleToInt32Bits(x) >> 23) & 0xff;
            if (e < 0x7f) return x;
            if (e >= 0x96) return 0f;
            int m = (BitConverter.SingleToInt32Bits(x) & 0x7fffff) | 0x800000;
            return x - (float)(m >> (0x96 - e));
        }

        /// <summary>The inline float to integer: toward zero, 0 past 2^31.</summary>
        static int Trunc(float v)
        {
            int e = (BitConverter.SingleToInt32Bits(v) >> 23) & 0xff;
            if (e > 0x9e) return 0;
            return (int)v;
        }

        /// <summary>vArctan: the angle of (x, y) in degrees, 0 .. 360, and its quadrant.</summary>
        public static void Atan(float x, float y, out float angle, out int quadrant)
        {
            int q = 0;
            if (x < 0) { x = -x; q |= 1; }
            if (y < 0) { y = -y; q |= 2; }
            if (y > x) { (x, y) = (y, x); q |= 4; }
            if (x == 0f) { angle = 0f; quadrant = 0; return; }
            float r = (32f * y) / x;
            int i = Trunc(r);
            float t0 = Arctan[i];
            float f = Fraction(r);
            float a = f * (Arctan[i + 1] - t0) + t0;
            switch (q)
            {
                case 4: a = 90f - a; break;
                case 3: a = a + 180f; break;
                case 2: a = 360f - a; break;
                case 6: a = 270f + a; break;
                case 1: a = 180f - a; break;
                case 5: a = a + 90f; break;
                case 7: a = 270f - a; break;
            }
            angle = a;
            quadrant = Quadrants[q];
        }

        static float Lerp(int i, float f, bool mirrored)
        {
            if (!mirrored) { float s0 = Sine[i]; return (Sine[i + 1] - s0) * f + s0; }
            float s1 = Sine[32 - i];
            return -((s1 - Sine[31 - i]) * f) + s1;
        }

        /// <summary>vCosSin: from the sine table, linearly between its entries.</summary>
        public static void CosSin(float angle, out float cos, out float sin)
        {
            bool neg = angle < 0;
            if (neg) angle = -angle;
            float v = SineFactor * angle;
            int n = Trunc(v);
            float f = Fraction(v);
            int q = n >> 5, i = n & 31;
            float s = Lerp(i, f, (q & 1) != 0);
            if ((q & 2) != 0 ? !neg : neg) s = -s;
            sin = s;
            int q1 = q + 1;
            float c = Lerp(i, f, (q1 & 1) != 0);
            if ((q1 & 2) != 0) c = -c;
            cos = c;
        }

        /// <summary>efSin (win32kbase @1400730b0), the same table.</summary>
        static float Sin(float x)
        {
            bool neg = x < 0;
            if (neg) x = -x;
            float v = SineFactor * x;
            int n = Trunc(v);
            float f = Fraction(v);
            int q = n >> 5, i = n & 31;
            float s = Lerp(i, f, (q & 1) != 0);
            if ((q & 2) != 0 ? !neg : neg) s = -s;
            return s;
        }

        static float Cos(float x) => Sin(90f + x);

        /// <summary>vCosSinPrecise: the angle reduced to the first quadrant in radians, then the
        /// Taylor series to the twelfth power.</summary>
        public static void CosSinPrecise(float angle, out float cos, out float sin)
        {
            bool neg = angle < 0, flipSin = false, flipCos = false;
            if (neg) angle = -angle;
            float a = Fraction(angle / 360f) * 360f;
            if (180f - a < 0) { flipSin = true; a = 360f - a; }
            if (90f - a < 0) { flipCos = true; a = 180f - a; }
            float x = (Pi * a) / 180f;
            float s = x, c = 1f, term = x, fact = 2f, k = 2f;
            for (int n = 2; n < 13; n++)
            {
                term = term * x;
                float d = term / fact;
                if ((n & 2) != 0) d = -d;
                if ((n & 1) != 0) s = d + s; else c = d + c;
                k = 1f + k;
                fact = k * fact;
            }
            if (neg != flipSin) s = -s;
            if (flipCos) c = -c;
            cos = c; sin = s;
        }

        /// <summary>bPartialQuadrantArc: one Bezier from (c0, s0) at a0 to (c3, s3) at a3, both in a
        /// quadrant; <paramref name="type"/> 1 starts a figure there, 2 draws a line to it.</summary>
        static void Quadrant(int type, GdiPath p, GdiBox e, float c0, float s0, float a0, float c3, float s3, float a3)
        {
            float c1 = c0, s1 = s0, c2 = c3, s2 = s3;
            float cross = c0 * s3 - c3 * s0;
            if (cross < 0) cross = -cross;
            if (cross > Epsilon)
            {
                float tx = (s3 - s0) / cross, ty = (c0 - c3) / cross;
                float h = Cos((a3 - a0) * 0.5f);
                if (h < 0) h = -h;
                float k = (FourThirds * h) / (1f + h);
                float kx = k * tx, ky = k * ty, rest = 1f - k;
                c1 = rest * c0 + kx; s1 = rest * s0 + ky;
                c2 = rest * c3 + kx; s2 = rest * s3 + ky;
            }
            if (type != 0)
            {
                e.Xform(c0, s0, out int x0, out int y0);
                if (type == 1) p.MoveTo(x0, y0); else p.LineTo(x0, y0);
            }
            e.Xform(c1, s1, out int x1, out int y1);
            e.Xform(c2, s2, out int x2, out int y2);
            e.Xform(c3, s3, out int x3, out int y3);
            p.BezierTo(x1, y1, x2, y2, x3, y3);
        }

        const long K = 0x729d7775;
        static int MulHi(int v) => (int)((v * K) >> 32);

        /// <summary>bPartialArc: the start quadrant's part, every whole quadrant between, the end
        /// quadrant's part (or, when both lie in one quadrant and the arc does not wrap, one part).</summary>
        static void Partial(int type, GdiPath p, GdiBox e, float c0, float s0, int q0, float a0, float c3, float s3, int q3, float a3, bool wrap)
        {
            if (!wrap) { Quadrant(type, p, e, c0, s0, a0, c3, s3, a3); return; }
            int q = (q0 + 1) & 3;
            Quadrant(type, p, e, c0, s0, a0, AxisCoord[(q + 1) & 3], AxisCoord[q], AxisAngle[q]);
            int kHx = MulHi(e.Hx), kHy = MulHi(e.Hy), kVx = MulHi(e.Vx), kVy = MulHi(e.Vy);
            while (q != q3)
            {
                switch (q)
                {
                    case 0: p.BezierTo(e.Ax - kVx, e.Ay - kVy, e.Ax - kHx, e.Ay - kHy, e.Ax - e.Hx, e.Ay - e.Hy); break;
                    case 1: p.BezierTo(e.Bx + kHx, e.By + kHy, e.Bx - kVx, e.By - kVy, e.Bx - e.Vx, e.By - e.Vy); break;
                    case 2: p.BezierTo(e.Cx + kVx, e.Cy + kVy, e.Cx + kHx, e.Cy + kHy, e.Cx + e.Hx, e.Cy + e.Hy); break;
                    default: p.BezierTo(e.Dx - kHx, e.Dy - kHy, e.Dx + kVx, e.Dy + kVy, e.Dx + e.Vx, e.Dy + e.Vy); break;
                }
                q = (q + 1) & 3;
            }
            Quadrant(0, p, e, AxisCoord[(q3 + 1) & 3], AxisCoord[q3], AxisAngle[q3], c3, s3, a3);
        }

        /// <summary>NtGdiArcInternal's path: <paramref name="kind"/> 0 Arc, 1 ArcTo (a line from the
        /// current point first), 2 Chord (closed), 3 Pie (to the centre, closed).</summary>
        public static void Build(GdiPath p, GdiBox e, int kind, int xs, int ys, int xe, int ye)
        {
            int l = e.L, t = e.T, r = e.R, b = e.B;
            float cy = GdiBox.HalfDiff(t, -b), cx = GdiBox.HalfDiff(l, -r);
            float a0 = 0f, a1 = 0f;
            int q0 = 0, q1 = 0;
            if (l != r && t != b)
            {
                float hw = GdiBox.HalfDiff(r, l), hh = GdiBox.HalfDiff(t, b);
                Atan(((float)xs - cx) / hw, ((float)ys - cy) / hh, out a0, out q0);
                Atan(((float)xe - cx) / hw, ((float)ye - cy) / hh, out a1, out q1);
            }
            float d = a1 - a0;
            if (d < 0) d = -d;
            float c0, s0, c1, s1;
            if (d - 3f < 0 && d != 0f)
            {
                CosSinPrecise(a0, out c0, out s0);
                CosSinPrecise(a1, out c1, out s1);
            }
            else
            {
                CosSin(a0, out c0, out s0);
                CosSin(a1, out c1, out s1);
            }
            bool wrap = q0 != q1 || a1 <= a0;
            Partial(kind == 1 ? 2 : 1, p, e, c0, s0, q0, a0, c1, s1, q1, a1, wrap);
            if (kind == 2) p.CloseFigure();
            else if (kind == 3) { p.LineTo(e.Mx, e.My); p.CloseFigure(); }
        }
    }
}
