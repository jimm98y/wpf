// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// GDI+'s antialiased fill (SmoothingMode.AntiAlias), read out of gdiplus.dll (arm64, public PDB):
//
//   GpPath::AddEllipse          13 points, unit points * w/2 + x + w/2 in float, kappa 0.5522847.
//   GpMatrix::Transform         to 28.4 as a CEILING: v' = (m * v + d) * 16 + 0.5 truncated, then
//                               (v' + 15) >> 4 -- the device matrix already carries the x16.
//   Bezier32::bInit / cFlatten  hybrid forward differencing in 28.4 (wpfgfx's bezier.cpp is the same
//                               code: tolerance 24 << 10, steady 24 << 13, output (e0 + 0x1000) >> 13).
//   InitializeEdges             half a pixel added and scaled to subsamples: x' = (x + 8) * 8,
//                               y' = (y + 8) << 2 -- EIGHT samples across and FOUR down; each subrow
//                               takes the edge's x as ceil(x' / 16) (the DDA is exact).
//   EpAntialiasedFiller         alternate spans [left, right) counted per pixel; alpha is
//                               round(count * 255 / 32).
//
// Verified against real GDI+ on 60 random ellipses: every pixel identical.
//

using System;
using System.Collections.Generic;

namespace System.Drawing.WebGpuBackend
{
    public static class GdipAntialias
    {
        const float Kappa = 0.5522847f;

        static readonly float [] s_unit = {
            1, 0, 1, Kappa, Kappa, 1, 0, 1, -Kappa, 1, -1, Kappa, -1, 0,
            -1, -Kappa, -Kappa, -1, 0, -1, Kappa, -1, 1, -Kappa, 1, 0,
        };

        /// <summary>GpPath::AddEllipse's points: a start point and four Beziers.</summary>
        public static PointF [] Ellipse (float x, float y, float w, float h)
        {
            var p = new PointF [13];
            for (int i = 0; i < 13; i++)
                p [i] = new PointF (s_unit [2 * i] * w * 0.5f + x + w * 0.5f, s_unit [2 * i + 1] * h * 0.5f + y + h * 0.5f);
            return p;
        }

        /// <summary>Coverage of a closed figure of Beziers (<paramref name="p"/>: a start point and
        /// 3n more) under the world matrix (m11, m12, m21, m22, dx, dy), as GDI+ antialiases it:
        /// alpha 0..255 per pixel of the returned rectangle. False when the 32-bit flattener cannot
        /// take a curve (GDI+ then uses its 64-bit one, which is not ported).</summary>
        public static bool Fill (PointF [] p, float m11, float m12, float m21, float m22, float dx, float dy,
                                   out byte [] alpha, out Rectangle bounds)
        {
            alpha = null;
            bounds = Rectangle.Empty;
            // The device matrix carries the scale to 28.4.
            float a = m11 * 16f, b = m12 * 16f, c = m21 * 16f, d = m22 * 16f, ex = dx * 16f, ey = dy * 16f;
            bool axis = m12 == 0f && m21 == 0f;
            var pts = new (int X, int Y) [p.Length];
            for (int i = 0; i < p.Length; i++) {
                float fx = axis ? (a * p [i].X + ex) * 16f + 0.5f : (c * p [i].Y + a * p [i].X + ex) * 16f + 0.5f;
                float fy = axis ? (p [i].Y * d + ey) * 16f + 0.5f : (d * p [i].Y + b * p [i].X + ey) * 16f + 0.5f;
                pts [i] = (((int) fx + 15) >> 4, ((int) fy + 15) >> 4);
            }
            var poly = new List<(int X, int Y)> { pts [0] };
            for (int i = 1; i + 2 < pts.Length; i += 3)
                if (!Flatten (pts [i - 1], pts [i], pts [i + 1], pts [i + 2], poly))
                    return false;

            // Subsample space: x' = (x + 8) * 8, y' = (y + 8) << 2, both still in sixteenths.
            int n = poly.Count;
            var sx = new long [n];
            var sy = new long [n];
            long minY = long.MaxValue, maxY = long.MinValue;
            for (int i = 0; i < n; i++) {
                sx [i] = (poly [i].X + 8L) * 8;
                sy [i] = (poly [i].Y + 8L) << 2;
                minY = Math.Min (minY, sy [i]); maxY = Math.Max (maxY, sy [i]);
            }
            long k0 = CeilDiv (minY, 16), k1 = CeilDiv (maxY, 16);
            if (k1 <= k0)
                return false;
            var rows = new List<long> [k1 - k0];
            for (int i = 0; i < n; i++) {
                long x0 = sx [i], y0 = sy [i], x1 = sx [(i + 1) % n], y1 = sy [(i + 1) % n];
                if (y0 == y1)
                    continue;
                if (y1 < y0) { (x0, x1) = (x1, x0); (y0, y1) = (y1, y0); }
                long dM = x1 - x0, dN = y1 - y0;
                for (long k = CeilDiv (y0, 16); k < CeilDiv (y1, 16); k++)
                    (rows [k - k0] ??= new List<long> ()).Add (CeilDiv (x0 * dN + dM * (16 * k - y0), 16 * dN));
            }
            long sMin = long.MaxValue, sMax = long.MinValue;
            foreach (List<long> r in rows)
                if (r != null)
                    foreach (long v in r) { sMin = Math.Min (sMin, v); sMax = Math.Max (sMax, v); }
            if (sMin > sMax)
                return false;
            int px0 = (int) (sMin >> 3), px1 = (int) ((sMax - 1) >> 3) + 1;
            int py0 = (int) (k0 >> 2), py1 = (int) ((k1 - 1) >> 2) + 1;
            int w = px1 - px0, h = py1 - py0;
            if (w <= 0 || h <= 0 || (long) w * h > 1 << 22)
                return false;
            var count = new int [w * h];
            for (int ri = 0; ri < rows.Length; ri++) {
                List<long> r = rows [ri];
                if (r == null)
                    continue;
                r.Sort ();
                int row = (int) ((k0 + ri) >> 2) - py0;
                for (int j = 0; j + 1 < r.Count; j += 2)
                    for (long s = r [j]; s < r [j + 1]; s++)
                        count [row * w + (int) (s >> 3) - px0]++;
            }
            alpha = new byte [w * h];
            for (int i = 0; i < count.Length; i++)
                alpha [i] = (byte) Math.Min (255, (int) (count [i] * 255 / 32.0 + 0.5));
            bounds = new Rectangle (px0, py0, w, h);
            return true;
        }

        static long CeilDiv (long a, long b)
        {
            long q = a / b;
            if (a % b != 0 && (a > 0) == (b > 0))
                q++;
            return q;
        }

        // ---- Bezier32 ----------------------------------------------------------------------

        struct Hfd
        {
            public int E0, E1, E2, E3;
            public bool Init (int p1, int p2, int p3, int p4)
            {
                E0 = p1 << 10; E1 = (p4 - p1) << 10;
                E2 = 6 * (p2 - p3 - p3 + p4) << 10; E3 = 6 * (p1 - p2 - p2 + p3) << 10;
                return true;
            }
            public int Error => Math.Max (Math.Abs (E2), Math.Abs (E3));
            public int ParentErrorBy4 => Math.Max (Math.Abs (E3), Math.Abs (E2 + E2 - E3));
            public void LazyHalve (int shift) { E2 = (E2 + E3) >> 1; E1 = (E1 - (E2 >> shift)) >> 1; }
            public void Steady (int shift)
            {
                E0 <<= 3; E1 <<= 3;
                int l = shift - 3;
                if (l < 0) { E2 <<= -l; E3 <<= -l; } else { E2 >>= l; E3 >>= l; }
            }
            public void Halve () { E2 = (E2 + E3) >> 3; E1 = (E1 - E2) >> 1; E3 >>= 2; }
            public void Double () { E1 += E1 + E2; E3 <<= 2; E2 = (E2 << 3) - E3; }
            public void Step () { E0 += E1; int t = E2; E1 += t; E2 += t - E3; E3 = t; }
            public int Value => (E0 + 0x1000) >> 13;
        }

        static bool Flatten ((int X, int Y) p0, (int X, int Y) p1, (int X, int Y) p2, (int X, int Y) p3, List<(int X, int Y)> output)
        {
            int left = Math.Min (Math.Min (p0.X, p1.X), Math.Min (p2.X, p3.X)) - 16;
            int top = Math.Min (Math.Min (p0.Y, p1.Y), Math.Min (p2.Y, p3.Y)) - 16;
            int or = (p0.X - left) | (p1.X - left) | (p2.X - left) | (p3.X - left)
                   | (p0.Y - top) | (p1.Y - top) | (p2.Y - top) | (p3.Y - top);
            if ((or & unchecked ((int) 0xffffc000)) != 0)
                return false;
            Hfd x = default, y = default;
            x.Init (p0.X - left, p1.X - left, p2.X - left, p3.X - left);
            y.Init (p0.Y - top, p1.Y - top, p2.Y - top, p3.Y - top);
            int steps = 1, shift = 0;
            while (true) {
                int t = 0x6000 << shift;
                if (x.Error <= t && y.Error <= t)
                    break;
                shift += 2;
                x.LazyHalve (shift); y.LazyHalve (shift);
                steps <<= 1;
            }
            x.Steady (shift); y.Steady (shift);
            x.Step (); y.Step ();
            steps--;
            while (true) {
                output.Add ((x.Value + left, y.Value + top));
                if (steps == 0)
                    return true;
                if (Math.Max (x.Error, y.Error) > 0x30000) {
                    x.Halve (); y.Halve ();
                    steps <<= 1;
                }
                while ((steps & 1) == 0 && x.ParentErrorBy4 <= 0xc000 && y.ParentErrorBy4 <= 0xc000) {
                    x.Double (); y.Double ();
                    steps >>= 1;
                }
                steps--;
                x.Step (); y.Step ();
            }
        }
    }
}
