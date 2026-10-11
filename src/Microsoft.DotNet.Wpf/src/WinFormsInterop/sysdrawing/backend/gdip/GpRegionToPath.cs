// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// A region's outline as a path (GpPath::GpPath(DpRegion*) @1800872d8), read out of gdiplus.dll:
//
//   DpRegion::GetOutlinePoints @1800dbea0   each contour traced over the y-bands: down an edge,
//                                across the bottom, up the other edge, across the top, every
//                                x-coordinate visited once; a rectangle region is its four corners
//   RegionToPath::ConvertRegionToPath @1800aad20 / DiagonalizePath @1800aaea8 / FetchNextPoint
//                                @1801e2328 / WritePoint @1800ab140   each staircase contour walked
//                                in a window of three points: a corner whose step is one pixel
//                                (in the senses the binary tests) is cut, and runs of equal steps
//                                become one segment
//   ConvertRegionOutputToWinding @180088830   a contour inside an odd number of other contours'
//                                boxes is reversed, so the outline fills winding
//

using System.Collections.Generic;
using System.Drawing.Drawing2D;

namespace System.Drawing.WebGpuBackend.Gdip
{
    internal static class GpRegionToPath
    {
        public static GpPath Convert (DpRegion r)
        {
            if (r.IsEmpty) return null;
            var pts = new List<Point> ();
            var types = new List<byte> ();
            if (r.IsRect) {
                Rectangle b = r.Bounds;
                pts.Add (new Point (b.Left, b.Top)); pts.Add (new Point (b.Right, b.Top));
                pts.Add (new Point (b.Right, b.Bottom)); pts.Add (new Point (b.Left, b.Bottom));
                types.AddRange (new byte [] { 0, 1, 1, 0x81 });
            } else {
                Outline (r, out List<Point> op, out List<byte> ot);
                int at = 0;
                while (at < op.Count) {
                    int end = at;
                    while (end < op.Count && (ot [end] & 0x80) == 0) end++;
                    Diagonalize (op, ot, at, end, pts, types);
                    at = end + 1;
                }
            }
            if (pts.Count < 1) return null;
            var f = new PointF [pts.Count];
            for (int i = 0; i < f.Length; i++) f [i] = new PointF (pts [i].X, pts [i].Y);
            return new GpPath (f, types.ToArray (), FillMode.Winding);
        }

        // ---- GetOutlinePoints -------------------------------------------------------------------

        static void Outline (DpRegion r, out List<Point> op, out List<byte> ot)
        {
            op = new List<Point> (); ot = new List<byte> ();
            int ns = r.Bands.Count;
            var y0 = new int [ns]; var y1 = new int [ns]; var xi = new int [ns]; var xc = new int [ns];
            var xs = new List<int> ();
            for (int i = 0; i < ns; i++) {
                DpRegion.Band b = r.Bands [i];
                y0 [i] = b.Top; y1 [i] = b.Bottom; xi [i] = xs.Count; xc [i] = b.X.Length;
                xs.AddRange (b.X);
            }
            int[] X = xs.ToArray ();
            var seen = new bool [X.Length];
            for (int s0 = 0; s0 < ns; s0++) {
                for (int u0 = 0; u0 < xc [s0]; u0++) {
                    if (seen [xi [s0] + u0]) continue;
                    op.Add (new Point (X [xi [s0] + u0], y0 [s0])); ot.Add (0);
                    int par = u0 & 1;
                    seen [xi [s0] + u0] = true;
                    int cur = s0, u = u0;
                    while (true) {
                        // Down the edge.
                        int dir = 1;
                        while (true) {
                            int nx = cur + 1;
                            if (nx >= ns || y1 [cur] != y0 [nx]) break;
                            int c = X [xi [cur] + u];
                            int last = xc [nx] - par - 1;
                            int k = par;
                            if (X [xi [nx] + par] <= c) {
                                if (X [xi [nx] + last] <= c) break;
                                int lo = par, hi = last, mid = (lo + hi) >> 1;
                                while (mid != lo) {
                                    if (c < X [xi [nx] + mid]) hi = mid; else lo = mid;
                                    mid = (lo + hi) >> 1;
                                }
                                k = hi;
                            }
                            if ((k & 1) == par) {
                                if (X [xi [cur] + u + 1] <= X [xi [nx] + k]) break;
                            } else {
                                if (!(u < 1 || X [xi [cur] + u - 1] <= X [xi [nx] + k - 1])) { dir = -1; break; }
                                k--;
                            }
                            int bx = X [xi [nx] + k];
                            if (c != bx) {
                                op.Add (new Point (c, y1 [cur])); ot.Add (1);
                                op.Add (new Point (bx, y1 [cur])); ot.Add (1);
                            }
                            cur = nx;
                            seen [xi [nx] + k] = true;
                            u = k;
                        }
                        // Across the bottom.
                        op.Add (new Point (X [xi [cur] + u], y1 [cur])); ot.Add (1);
                        op.Add (new Point (X [xi [cur] + u + dir], y1 [cur])); ot.Add (1);
                        int u11 = u + dir;
                        seen [xi [cur] + u11] = true;
                        dir = -1;
                        // Up the other edge.
                        while (true) {
                            int pv = cur - 1;
                            if (pv < 0 || y0 [cur] != y1 [pv]) break;
                            int c = X [xi [cur] + u11];
                            int last = xc [pv] - par - 1;
                            int k = last;
                            if (c <= X [xi [pv] + last]) {
                                if (c <= X [xi [pv] + par]) break;
                                int lo = par, hi = last, mid = (lo + hi) >> 1;
                                while (mid != lo) {
                                    if (c <= X [xi [pv] + mid]) hi = mid; else lo = mid;
                                    mid = (lo + hi) >> 1;
                                }
                                k = lo;
                            }
                            if ((k & 1) != par) {
                                if (X [xi [pv] + k] <= X [xi [cur] + u11 - 1]) break;
                            } else {
                                if (!(xc [cur] - 1 <= u11 || X [xi [pv] + k + 1] <= X [xi [cur] + u11 + 1])) { dir = 1; break; }
                                k++;
                            }
                            int bx = X [xi [pv] + k];
                            if (c != bx) {
                                op.Add (new Point (c, y0 [cur])); ot.Add (1);
                                op.Add (new Point (bx, y0 [cur])); ot.Add (1);
                            }
                            u11 = k;
                            seen [xi [pv] + k] = true;
                            cur = pv;
                        }
                        // Across the top, or the contour closes.
                        if (cur == s0 && u0 == u11 - 1) {
                            op.Add (new Point (X [xi [cur] + u11], y0 [cur])); ot.Add (0x81);
                            break;
                        }
                        op.Add (new Point (X [xi [cur] + u11], y0 [cur])); ot.Add (1);
                        op.Add (new Point (X [xi [cur] + u11 + dir], y0 [cur])); ot.Add (1);
                        u = u11 + dir;
                        seen [xi [cur] + u] = true;
                    }
                }
            }
        }

        // ---- DiagonalizePath --------------------------------------------------------------------

        sealed class Walker
        {
            readonly List<Point> _p; readonly List<byte> _t;
            int _at; readonly int _start, _end;
            bool _done;
            public readonly Point[] P = new Point [3];
            public readonly bool[] F = new bool [3];
            int _w;
            public Walker (List<Point> p, List<byte> t, int start, int end) { _p = p; _t = t; _start = start; _end = end; _at = start; }
            public int WriteSlot => _w;
            public void Fetch ()
            {
                int k = _w;
                _w = (_w + 1) % 3;
                if (!_done) {
                    P [k] = _p [_at];
                    bool c = _at >= _end || (_t [_at] & 0x80) != 0;
                    if (c) _done = true;
                    F [k] = c;
                    _at++;
                } else { F [k] = false; P [k] = _p [_start]; }
            }
        }

        static void Diagonalize (List<Point> op, List<byte> ot, int start, int end, List<Point> pts, List<byte> types)
        {
            var w = new Walker (op, ot, start, Math.Min (end, op.Count - 1));
            int state = 0;
            Point wa = default, wb = default; int ddx = 0, ddy = 0;
            void Write ()
            {
                Point q = w.P [w.WriteSlot];
                if (state == 2) {
                    if (q.X - wb.X != ddx || q.Y - wb.Y != ddy) {
                        pts.Add (wa); types.Add (1);
                        wa = wb;
                        ddx = q.X - wb.X; ddy = q.Y - wb.Y;
                    }
                    wb = q;
                } else if (state == 0) { wa = q; state = 1; }
                else { wb = q; ddx = wb.X - wa.X; ddy = wb.Y - wa.Y; state = 2; }
            }
            w.Fetch (); w.Fetch (); w.Fetch ();
            bool horiz;
            int d25;
            if (w.P [2].Y == w.P [1].Y) { horiz = true; d25 = w.P [1].Y - w.P [0].Y; }
            else { horiz = false; d25 = w.P [1].X - w.P [0].X; }
            pts.Add (w.P [0]); types.Add (0);
            int A = 0, B = 1, C = 2;
            while (true) {
                if (w.F [A]) {
                    pts.Add (wa); types.Add (1);
                    pts.Add (wb); types.Add (0x81);
                    return;
                }
                if (horiz) {
                    int d12 = w.P [C].X - w.P [B].X;
                    if (0 < d25 && (d25 == 1 || d12 == -1)) {
                        if (w.F [B]) { pts.Add (wa); types.Add (1); pts.Add (wb); types.Add (0x81); return; }
                        w.Fetch (); w.Fetch ();
                        int a = A; A = C; C = B; B = a;
                        d25 = w.P [B].Y - w.P [A].Y;
                    } else {
                        w.Fetch ();
                        horiz = false;
                        int a = A; A = B; B = C; C = a;
                        d25 = d12;
                    }
                } else {
                    int d12 = w.P [C].Y - w.P [B].Y;
                    if (d12 < 0 && (d25 == 1 || d12 == -1)) {
                        if (w.F [B]) { pts.Add (wa); types.Add (1); pts.Add (wb); types.Add (0x81); return; }
                        w.Fetch (); w.Fetch ();
                        int a = A; A = C; C = B; B = a;
                        d25 = w.P [B].X - w.P [A].X;
                    } else {
                        w.Fetch ();
                        horiz = true;
                        int a = A; A = B; B = C; C = a;
                        d25 = d12;
                    }
                }
                Write ();
            }
        }

        // ---- ConvertRegionOutputToWinding -------------------------------------------------------

        public static GpPath ToWinding (GpPath p)
        {
            var subs = new List<(int s, int e, RectangleF b, bool rev)> ();
            int n = p.Count, s = 0;
            while (s < n) {
                int e = s;
                while (e + 1 < n && (p.Types [e + 1] & 7) != 0) e++;
                float l = p.Points [s].X, t = p.Points [s].Y, r = l, bt = t;
                for (int i = s + 1; i <= e; i++) {
                    PointF q = p.Points [i];
                    if (q.X < l) l = q.X;
                    if (r < q.X) r = q.X;
                    if (q.Y < t) t = q.Y;
                    if (bt < q.Y) bt = q.Y;
                }
                subs.Add ((s, e, RectangleF.FromLTRB (l, t, r, bt), false));
                s = e + 1;
            }
            for (int k = 1; k < subs.Count; k++)
                for (int j = k - 1; j >= 0; j--) {
                    if (Contains (subs [k].b, subs [j].b)) { var x = subs [j]; x.rev = !x.rev; subs [j] = x; }
                    else if (Contains (subs [j].b, subs [k].b)) { var x = subs [k]; x.rev = !x.rev; subs [k] = x; }
                }
            var o = new GpPath (FillMode.Winding);
            foreach (var sb in subs) {
                int c = sb.e - sb.s + 1;
                var sp = new GpPath (p.Points.GetRange (sb.s, c).ToArray (), p.Types.GetRange (sb.s, c).ToArray (), FillMode.Alternate);
                if (sb.rev) sp.Reverse ();
                o.AddPath (sp, false);
            }
            return o;
        }

        /// <summary>Contains(a, b): b's box inside a's.</summary>
        static bool Contains (RectangleF a, RectangleF b)
            => !(b.Left < a.Left || b.Top < a.Top || a.Right < b.Right || a.Bottom < b.Bottom);
    }
}
