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
}
