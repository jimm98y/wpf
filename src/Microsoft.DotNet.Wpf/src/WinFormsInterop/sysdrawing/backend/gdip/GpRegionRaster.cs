// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// A path as a region: DpRegion::Set(DpPath, GpMatrix) @1800dd228 -- not the antialiasing rasterizer
// the fills use but GDI+'s older one for regions (gdiplus.dll 10.0.26100, arm64):
//
//   GpPath::GetFlattenedPath @1800895b0   the path flattened under the matrix, flatness 0.25
//   Rasterizer @18012bf68                 points to 28.4 (floor(v * 16 + 0.5)); an edge for every
//                                         segment whose ends fall in different pixel rows
//                                         ((y + 15) >> 4), each subpath closed; oriented top-down
//                                         with direction +1 when it was drawn upwards; rows from the
//                                         ceiling of the least y to the ceiling of the greatest less
//                                         one; edges sorted on their top y (QuickSortIndex
//                                         @18017afe0, its own unstable partition); one subpath with
//                                         at most three direction changes is convex
//   ConvexRasterizer @18012b450           two DDAs walking the first two edges, each taking the
//                                         next edge in order when it ends, a span between them
//   NonConvexRasterizer @18012ba60        an active-edge list sorted by x each row (insertion
//                                         sort), spans by parity (Alternate) or winding
//   GpYDda::Init @18012b900 / Advance @18017a3f0 / DoneWithVector @18012b720   the x DDA: x in
//                                         28.4 stepped to the first pixel row, then to the next
//                                         whole pixel, x in pixels per row with an error term
//

using System.Collections.Generic;
using System.Drawing.Drawing2D;

namespace System.Drawing.WebGpuBackend.Gdip
{
    internal static class GpRegionRaster
    {
        struct Vec { public int X0, Y0, X1, Y1, Dir; }

        sealed class YDda
        {
            int _err, _inc, _dy, _step, _lastRow;
            public int X, Dir;

            public bool Init (int x1, int y1, int x2, int y2, int dir)
            {
                int dy = y2 - y1, dx = x2 - x1;
                if (dy < 1) return false;
                _dy = dy;
                int step, inc;
                if (dx < 0) {
                    int adx = -dx;
                    if (adx < dy) { step = -1; inc = dy - adx; }
                    else {
                        int q = (int) ((uint) adx / (uint) dy), r = adx - q * dy;
                        step = -q; inc = r;
                        if (r != 0) { step = -q - 1; inc = dy - r; }
                    }
                } else if (dx < dy) { step = 0; inc = dx; }
                else {
                    int q = (int) ((uint) dx / (uint) dy);
                    step = q; inc = dx - q * dy;
                }
                _step = step; _inc = inc;
                int err = -1, x = x1;
                int frac = y1 & 0xf;
                if (frac != 0) {
                    for (int k = 16 - frac; k > 0; k--) {
                        err += inc;
                        x += step;
                        if (err >= 0) { x += 1; err -= dy; }
                    }
                }
                if ((x & 0xf) != 0) {
                    err -= (16 - (x & 0xf)) * dy;
                    x += 0xf;
                }
                _err = err >> 4;
                X = x >> 4;
                Dir = dir;
                _lastRow = ((y2 + 0xf) >> 4) - 1;
                return true;
            }

            void Advance ()
            {
                int x = X;
                X = _step + x;
                int e = _inc + _err;
                _err = e;
                if (e >= 0) { _err = e - _dy; X = _step + x + 1; }
            }

            /// <summary>True when the edge has no rows past <paramref name="row"/>; otherwise steps on.</summary>
            public bool DoneWithVector (int row)
            {
                if (_lastRow > row) { Advance (); return false; }
                return true;
            }
        }

        static int Fix (float v) => (int) MathF.Floor (v * 16f + 0.5f);

        static bool Crosses (int ya, int yb) => (((ya + 0xf) ^ (yb + 0xf)) & ~0xf) != 0;

        public static DpRegion FromPath (PointF[] pts, byte[] types, FillMode fill, in GpMatrix m)
        {
            var b = new DpRegion.Builder ();
            if (pts == null || pts.Length < 2) return new DpRegion ();
            var path = new GpPath (pts, types, fill);
            path.Flatten (m, 0.25f);
            PointF[] p = path.PointArray ();
            byte[] t = path.TypeArray ();
            if (p.Length < 3) return new DpRegion ();

            var edges = new List<Vec> ();
            int minY = 0, maxY = 0;
            bool haveY = false;
            int dir = 0, lastDir = 0, changes = 0;
            bool many = false;
            int sx = Fix (p [0].X), sy = Fix (p [0].Y), px = sx, py = sy;

            void Edge (int ax, int ay, int bx, int by)
            {
                if (!Crosses (by, ay)) return;
                Vec v;
                if (by < ay) v = new Vec { X0 = bx, Y0 = by, X1 = ax, Y1 = ay, Dir = 1 };
                else v = new Vec { X0 = ax, Y0 = ay, X1 = bx, Y1 = by, Dir = -1 };
                edges.Add (v);
                if (!haveY) { minY = v.Y0; maxY = v.Y1; haveY = true; }
                else { minY = Math.Min (minY, v.Y0); maxY = Math.Max (maxY, v.Y1); }
                dir = v.Dir;
            }

            for (int i = 1; i < p.Length; i++) {
                int cx = Fix (p [i].X), cy = Fix (p [i].Y);
                if ((t [i] & 7) == 0) {
                    Edge (px, py, sx, sy);
                    sx = px = cx; sy = py = cy;
                    many = true;
                } else {
                    Edge (px, py, cx, cy);
                    px = cx; py = cy;
                    if (dir != lastDir) { changes++; lastDir = dir; }
                }
            }
            Edge (px, py, sx, sy);
            if (dir != lastDir) changes++;
            if (edges.Count < 2) return new DpRegion ();

            int yTop = (minY + 0xf) >> 4;
            int yBottom = ((maxY + 0xf) >> 4) - 1;
            var order = new int [edges.Count];
            for (int i = 0; i < order.Length; i++) order [i] = i;
            QuickSortIndex (edges, order, 0, order.Length - 1);

            if (!many && changes <= 3) Convex (yTop, yBottom, edges, order, b);
            else NonConvex (yTop, yBottom, edges, order, b, fill == FillMode.Alternate);
            return b.Build ();
        }

        // QuickSortIndex @18017afe0: Hoare partition on the edges' top y, pivot the middle element.
        static void QuickSortIndex (List<Vec> e, int[] a, int lo, int hi)
        {
            while (lo < hi) {
                int pivot = e [a [lo + (hi - lo) / 2]].Y0;
                int i = lo, j = hi;
                while (e [a [i]].Y0 < pivot) i++;
                while (true) {
                    while (e [a [j]].Y0 > pivot) j--;
                    if (i >= j) break;
                    (a [i], a [j]) = (a [j], a [i]);
                    if (e [a [i]].Y0 == e [a [j]].Y0) {
                        i++;
                        if (i >= j) break;
                    }
                    while (e [a [i]].Y0 < pivot) i++;
                }
                QuickSortIndex (e, a, lo, i - 1);
                lo = i + 1;
            }
        }

        static void Convex (int yTop, int yBottom, List<Vec> e, int[] order, DpRegion.Builder sink)
        {
            var d1 = new YDda ();
            var d2 = new YDda ();
            Vec a = e [order [0]], c = e [order [1]];
            if (!d1.Init (a.X0, a.Y0, a.X1, a.Y1, 1)) return;
            if (!d2.Init (c.X0, c.Y0, c.X1, c.Y1, 1)) return;
            int next = 2;
            int row = (c.Y0 + 0xf) >> 4;
            int r1 = (a.Y0 + 0xf) >> 4;
            for (int k = row - r1; k > 0; k--) d1.DoneWithVector (int.MinValue);
            for (; row <= yBottom; row++) {
                if (yTop <= row) {
                    int x2 = d2.X, x1 = d1.X;
                    if (x2 != x1) sink.OutputSpan (row, Math.Min (x1, x2), Math.Max (x1, x2));
                }
                if (d1.DoneWithVector (row)) {
                    if (next >= order.Length) break;
                    Vec v = e [order [next++]];
                    if (!d1.Init (v.X0, v.Y0, v.X1, v.Y1, 1)) return;
                }
                if (d2.DoneWithVector (row)) {
                    if (next >= order.Length) break;
                    Vec v = e [order [next++]];
                    if (!d2.Init (v.X0, v.Y0, v.X1, v.Y1, 1)) return;
                }
            }
        }

        static void NonConvex (int yTop, int yBottom, List<Vec> e, int[] order, DpRegion.Builder sink, bool alternate)
        {
            var active = new List<YDda> ();
            var pool = new List<YDda> ();
            int next = 0;
            for (int row = yTop; row <= yBottom; row++) {
                while (next < order.Length && row == ((e [order [next]].Y0 + 0xf) >> 4)) {
                    Vec v = e [order [next++]];
                    if (yTop <= ((v.Y1 + 0xf) >> 4)) {
                        YDda d = pool.Count > 0 ? pool [pool.Count - 1] : new YDda ();
                        if (pool.Count > 0) pool.RemoveAt (pool.Count - 1);
                        if (d.Init (v.X0, v.Y0, v.X1, v.Y1, v.Dir)) active.Add (d);
                        else pool.Add (d);
                    }
                }
                if (yTop <= row) {
                    for (int i = 1; i < active.Count; i++)
                        for (int j = i; j > 0 && active [j].X < active [j - 1].X; j--)
                            (active [j], active [j - 1]) = (active [j - 1], active [j]);
                    if (alternate) {
                        for (int i = 1; i < active.Count; i += 2)
                            if (active [i - 1].X < active [i].X) sink.OutputSpan (row, active [i - 1].X, active [i].X);
                    } else {
                        int w = 0;
                        for (int i = 1; i < active.Count; i += 2) {
                            int start = i - 1;
                            w += active [start].Dir;
                            for (; i < active.Count; i++) {
                                w += active [i].Dir;
                                if (w == 0) {
                                    if (active [start].X < active [i].X) sink.OutputSpan (row, active [start].X, active [i].X);
                                    break;
                                }
                            }
                        }
                    }
                }
                for (int i = 0; i < active.Count;) {
                    if (active [i].DoneWithVector (row)) { pool.Add (active [i]); active.RemoveAt (i); }
                    else i++;
                }
            }
        }
    }
}
