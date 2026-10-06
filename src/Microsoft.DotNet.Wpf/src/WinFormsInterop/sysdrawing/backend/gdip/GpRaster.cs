// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// GDI+'s path rasterizer, read out of gdiplus.dll (arm64, public PDB) with WPF's aarasterizer.cpp --
// the same code base, carried into wpfgfx -- as the readable reference:
//
//   RasterizePath            @18002efd8   aliased (EpAliasedFiller) or antialiased (EpAntialiasedFiller,
//                                          8x4 for AntiAlias/HighQuality, 8x8 for AntiAlias8x8)
//   FixedPointPathEnumerate  @18002de00   28.4 points through GpMatrix::Transform's ceiling; Beziers
//                                          through Bezier32 (Bezier64 when the hull is too big)
//   InitializeEdges          @18002e290   the DDA set-up; for antialiasing x' = (x + 8) * 8 and
//                                          y' = (y + 8) << 2 (or << 3)
//   EpAntialiasedFiller::OutputSpan @18002eb70   coverage scales the span's premultiplied pixels:
//                                          (c * cov + 2^(s+2)) >> (s+3) on partial pixels, a run of
//                                          whole pixels at one depth k as (c * k + 2^(s-1)) >> s
//
// Pixel centres sit on integer coordinates: an aliased pixel (i, j) is filled when (i, j) is inside,
// left and top edges in, right and bottom out.
//

using System.Collections.Generic;

namespace System.Drawing.WebGpuBackend.Gdip
{
    /// <summary>Anything that takes horizontal pixel spans: a brush, a clipper, an antialiaser.</summary>
    internal interface ISpanSink
    {
        void OutputSpan (int y, int left, int right);
    }

    internal sealed class Edge
    {
        public Edge Next;
        public int X, Dx, Error, ErrorUp, ErrorDown, StartY, EndY, Winding;
    }

    internal static class GpRaster
    {
        // SmoothingMode -> filler: None/HighSpeed/Default 0, HighQuality/AntiAlias 1 (8x4), AntiAlias8x8 2.
        public static int AntialiasMode (Drawing2D.SmoothingMode m)
            => (int) m == 2 || (int) m == 4 ? 1 : (int) m == 5 ? 2 : 0;

        sealed class EdgeContext
        {
            public int MaxY = int.MinValue;
            public bool HasClip;
            public int ClipLeft, ClipTop, ClipRight, ClipBottom;   // 28.4
            public int Aa;                                         // 0, 1, 2
            public readonly List<Edge> Edges = new List<Edge> ();
        }

        /// <summary>RasterizePath. <paramref name="toDevice"/> is the world-to-device matrix (pixel offset
        /// included); spans go to <paramref name="sink"/> through <paramref name="clip"/>.</summary>
        public static void FillPath (PointF[] pts, byte[] types, int count, in GpMatrix toDevice, bool winding, int aa,
                                     ISpanSink sink, GpClip clip, Rectangle drawBounds)
            => FillPath (pts, types, count, toDevice, winding, aa, sink, clip, drawBounds, false);

        /// <summary>RasterizePath with its nominal flag: <paramref name="nominal"/> strokes the path as
        /// one-pixel lines (InitializeNominal, enumeration type 0, winding fill) instead of filling it.</summary>
        public static void FillPath (PointF[] pts, byte[] types, int count, in GpMatrix toDevice, bool winding, int aa,
                                     ISpanSink sink, GpClip clip, Rectangle drawBounds, bool nominal)
        {
            if (count < 2) return;
            var ctx = new EdgeContext { Aa = aa };
            if (clip != null) {
                if (!clip.IsVisible (drawBounds)) return;
                Rectangle cb = clip.Bounds;
                int shift = aa == 2 ? 3 : 2;
                if (cb.Left < -0x800000 || cb.Top < (int.MinValue >> (shift + 5)) || cb.Left > 0x7fffff
                    || cb.Top > (int.MaxValue >> (shift + 5)) || cb.Right - cb.Left > 0x7fffff || cb.Bottom - cb.Top > (int.MaxValue >> (shift + 5)))
                    return;
                ctx.HasClip = true;
                ctx.ClipLeft = cb.Left << 4; ctx.ClipTop = cb.Top << 4; ctx.ClipRight = cb.Right << 4; ctx.ClipBottom = cb.Bottom << 4;
            }
            var m = new GpMatrix { M11 = toDevice.M11 * 16f, M12 = toDevice.M12 * 16f, M21 = toDevice.M21 * 16f, M22 = toDevice.M22 * 16f,
                                   Dx = toDevice.Dx * 16f, Dy = toDevice.Dy * 16f };
            m.Complexity = m.ComputeComplexity ();
            int[] clipFix = ctx.HasClip ? new[] { ctx.ClipLeft, ctx.ClipTop, ctx.ClipRight, ctx.ClipBottom } : null;
            if (nominal)
                Enumerate (pts, types, count, m, clipFix, 0, (b, n, term) => InitializeNominal (ctx, b, n));
            else
                Enumerate (pts, types, count, m, clipFix, 1, (b, n, term) => InitializeEdges (ctx, b, n));
            if (ctx.Edges.Count == 0) return;
            // csinc w27, w14, wzr, eq: a nominal stroke always fills winding.
            Rasterize (ctx, nominal || winding, aa, sink, clip);
        }

        // ---- path enumeration -----------------------------------------------------------------------

        /// <summary>A batch of 28.4 points (x0,y0,x1,y1,...) and how it ends: 0 the figure goes on,
        /// 1 it ended open, 2 it ended closed (its start point repeated at the end).</summary>
        internal delegate void PointBatch (int[] buf, int n, int termination);

        const int BufferPoints = 32;

        /// <summary>FixedPointPathEnumerate @18002de00. <paramref name="enumType"/> 1 (fill) closes every
        /// figure; 0 and 2 close only the figures the path closes, and 2 reports even a lone point.</summary>
        internal static void Enumerate (PointF[] pts, byte[] types, int count, in GpMatrix m, int[] clip, int enumType, PointBatch batch)
        {
            var buf = new int [BufferPoints * 2];
            var bez = new int [8];
            int i = 0;
            int last = count - 1;
            while (i < last) {
                m.TransformFix (pts [i], out int sx, out int sy);
                buf [0] = sx; buf [1] = sy;
                int n = 1;
                int j = i + 1;
                while (true) {
                    int t = types [j] & 7;
                    if (t == 1) {
                        int end = j + 1;
                        while (end < count && (types [end] & 7) == 1) end++;
                        for (; j < end; j++) {
                            m.TransformFix (pts [j], out buf [n * 2], out buf [n * 2 + 1]);
                            n++;
                            if (n == BufferPoints) {
                                batch (buf, n, 0);
                                buf [0] = buf [(n - 1) * 2]; buf [1] = buf [(n - 1) * 2 + 1];
                                n = 1;
                            }
                        }
                    } else {
                        if (count < j + 3) { j = count; break; }
                        for (int k = 0; k < 4; k++) m.TransformFix (pts [j - 1 + k], out bez [k * 2], out bez [k * 2 + 1]);
                        j += 3;
                        var flat = new BezierFlattener (bez, clip);
                        bool more;
                        do {
                            int got = flat.Flatten (buf, n, BufferPoints - n, out more);
                            n += got;
                            if (n < BufferPoints) break;
                            batch (buf, n, 0);
                            buf [0] = buf [(n - 1) * 2]; buf [1] = buf [(n - 1) * 2 + 1];
                            n = 1;
                        } while (more);
                    }
                    if (j >= count || (types [j] & 7) == 0) break;
                }
                int term = 0;
                if (enumType == 1 || (types [j - 1] & 0x80) != 0) {
                    buf [n * 2] = sx; buf [n * 2 + 1] = sy;
                    n++;
                    term = 1;
                }
                if (n > 1 || enumType == 2) batch (buf, n, term + 1);
                i = j;
            }
        }

        internal static void EnumerateForFlatten (GpPath path, in GpMatrix m, PointBatch batch)
            => Enumerate (path.PointArray (), path.TypeArray (), path.Count, m, null, 2, batch);

        // ---- InitializeNominal @18002e7b0 -------------------------------------------------------------
        //
        // A polyline of 28.4 points becomes the outline of a one-pixel-wide band: each segment is
        // offset by half a pixel along its minor axis (the octant picks one of four nominal vectors),
        // the ends pushed out half a pixel along the major axis, and at a change of octant the outer
        // side takes the corner point. The two sides go to InitializeEdges separately, the right side
        // in reverse, so together they close the band.

        static readonly int[] s_octantDir = { 0, 2, 0, 2, 3, 3, 1, 1 };            // @1802af6a0
        static readonly int[] s_nomX = { 0, -8, 0, 8 }, s_nomY = { -8, 0, 8, 0 };  // @1802af6c0

        static int Octant (int dx, int dy)
        {
            int o = dx < 0 ? 1 : 0;
            if (dy < 0) o |= 2;
            if (Math.Abs (dx) < Math.Abs (dy)) o |= 4;
            return s_octantDir [o];
        }

        static void InitializeNominal (EdgeContext ctx, int[] b, int n)
        {
            if (n < 2) return;
            var left = new List<int> ();
            var right = new List<int> ();   // in the order GDI+ writes it, downwards
            int px = b [0], py = b [1];
            int dir = Octant (b [2] - px, b [3] - py);
            left.Add (px - s_nomX [dir]); left.Add (py - s_nomY [dir]);
            int d1 = (dir + 1) & 3;
            left.Add (px + s_nomX [d1]); left.Add (py + s_nomY [d1]);
            int segs = n - 1;
            for (int i = 1; ; i++) {
                int x0 = b [i * 2 - 2], y0 = b [i * 2 - 1], x1 = b [i * 2], y1 = b [i * 2 + 1];
                int vx = s_nomX [dir], vy = s_nomY [dir];
                left.Add (x0 + vx); left.Add (y0 + vy);
                right.Add (x0 - vx); right.Add (y0 - vy);
                left.Add (x1 + vx); left.Add (y1 + vy);
                right.Add (x1 - vx); right.Add (y1 - vy);
                if (--segs == 0) {
                    int dm = (dir - 1) & 3;
                    left.Add (x1 + s_nomX [dm]); left.Add (y1 + s_nomY [dm]);
                    left.Add (x1 - vx); left.Add (y1 - vy);
                    InitializeEdges (ctx, left.ToArray (), left.Count / 2);
                    if (right.Count >= 4) {
                        var r = new int [right.Count];
                        for (int k = 0, m = right.Count / 2; k < m; k++) { r [k * 2] = right [(m - 1 - k) * 2]; r [k * 2 + 1] = right [(m - 1 - k) * 2 + 1]; }
                        InitializeEdges (ctx, r, r.Length / 2);
                    }
                    return;
                }
                int x2 = b [i * 2 + 2], y2 = b [i * 2 + 3];
                int nd = Octant (x2 - x1, y2 - y1);
                if (nd != dir) {
                    if ((long) (y1 - y0) * (x2 - x1) <= (long) (x1 - x0) * (y2 - y1)) {
                        int t = (dir - 1) & 3;
                        if (nd != t) { left.Add (x1 + s_nomX [t]); left.Add (y1 + s_nomY [t]); }
                        right.Add (x1); right.Add (y1);
                    } else {
                        int t = (dir + 1) & 3;
                        if (nd != t) { right.Add (x1 - s_nomX [t]); right.Add (y1 - s_nomY [t]); }
                        left.Add (x1); left.Add (y1);
                    }
                }
                dir = nd;
            }
        }

        // ---- InitializeEdges ------------------------------------------------------------------------

        static void InitializeEdges (EdgeContext ctx, int[] buf, int vertexCount)
        {
            int shift = ctx.Aa == 2 ? 3 : 2;
            int yClipTopInteger, yClipTop = 0, yClipBottom = 0, xClipLeft = 0, xClipRight = 0;
            if (!ctx.HasClip) {
                yClipTopInteger = int.MinValue >> shift;
            } else {
                yClipTopInteger = ctx.ClipTop >> 4;
                yClipTop = ctx.ClipTop;
                yClipBottom = ctx.ClipBottom;
                xClipLeft = ctx.ClipLeft;
                xClipRight = ctx.ClipRight;
            }
            var p = (int[]) buf.Clone ();
            if (ctx.Aa != 0) {
                for (int k = 0; k < vertexCount; k++) {
                    p [k * 2] = (p [k * 2] + 8) * 8;
                    p [k * 2 + 1] = (p [k * 2 + 1] + 8) << shift;
                }
                yClipTopInteger <<= shift;
                yClipTop <<= shift;
                yClipBottom <<= shift;
                xClipLeft <<= 3;
                xClipRight <<= 3;
            }
            yClipBottom -= 16;
            int edgeCount = vertexCount - 1;
            for (int e = 0; e < edgeCount; e++) {
                int o = e * 2;
                if (yClipBottom >= 0) {
                    bool clipHigh = p [o + 1] <= yClipTop && p [o + 3] <= yClipTop;
                    bool clipLow = p [o + 1] > yClipBottom && p [o + 3] > yClipBottom;
                    if (clipHigh || clipLow) continue;
                    if (edgeCount - e > 1) {
                        if (p [o] < xClipLeft && p [o + 2] < xClipLeft && p [o + 4] < xClipLeft) {
                            p [o + 2] = p [o]; p [o + 3] = p [o + 1];
                            continue;
                        }
                        if (p [o] > xClipRight && p [o + 2] > xClipRight && p [o + 4] > xClipRight) {
                            p [o + 2] = p [o]; p [o + 3] = p [o + 1];
                            continue;
                        }
                    }
                }
                int dM = p [o + 2] - p [o];
                int dN = p [o + 3] - p [o + 1];
                int xStart, yStart, yStartInteger, yEndInteger, winding;
                if (dN >= 0) {
                    xStart = p [o]; yStart = p [o + 1];
                    yStartInteger = (yStart + 15) >> 4;
                    yEndInteger = (p [o + 3] + 15) >> 4;
                    winding = 1;
                } else {
                    dN = -dN; dM = -dM;
                    xStart = p [o + 2]; yStart = p [o + 3];
                    yStartInteger = (yStart + 15) >> 4;
                    yEndInteger = (p [o + 1] + 15) >> 4;
                    winding = -1;
                }
                if (yEndInteger <= yStartInteger) continue;
                ctx.MaxY = Math.Max (ctx.MaxY, yEndInteger);
                int dMOriginal = dM;
                int dX, errorUp;
                if (dM < 0) {
                    dM = -dM;
                    if (dM < dN) { dX = -1; errorUp = dN - dM; }
                    else {
                        int q = (int) ((uint) dM / (uint) dN), r = (int) ((uint) dM % (uint) dN);
                        dX = -q; errorUp = r;
                        if (r > 0) { dX = -q - 1; errorUp = dN - r; }
                    }
                } else {
                    if (dM < dN) { dX = 0; errorUp = dM; }
                    else { dX = (int) ((uint) dM / (uint) dN); errorUp = (int) ((uint) dM % (uint) dN); }
                }
                int error = -1;
                if ((yStart & 15) != 0) {
                    for (int k = 16 - (yStart & 15); k != 0; k--) {
                        xStart += dX;
                        error += errorUp;
                        if (error >= 0) { error -= dN; xStart++; }
                    }
                }
                if ((xStart & 15) != 0) {
                    error -= dN * (16 - (xStart & 15));
                    xStart += 15;
                }
                xStart >>= 4;
                error >>= 4;
                var edge = new Edge {
                    X = xStart, Dx = dX, Error = error, ErrorUp = errorUp, ErrorDown = dN,
                    Winding = winding, StartY = yStartInteger, EndY = yEndInteger,
                };
                if (yClipTopInteger > yStartInteger)
                    ClipEdge (edge, yClipTopInteger, dMOriginal);
                ctx.Edges.Add (edge);
            }
        }

        static void ClipEdge (Edge e, int yClipTopInteger, int dMOriginal)
        {
            int dN = e.ErrorDown;
            long big = (long) dMOriginal * (yClipTopInteger - e.StartY) + (e.Error + dN);
            int xDelta, error;
            if (big >= 0) {
                xDelta = (int) ((ulong) big / (uint) dN);
                error = (int) ((ulong) big % (uint) dN);
            } else {
                big = -big;
                xDelta = (int) ((ulong) big / (uint) dN);
                error = (int) ((ulong) big % (uint) dN);
                xDelta = -xDelta;
                if (error != 0) { xDelta--; error = dN - error; }
            }
            e.StartY = yClipTopInteger;
            e.X += xDelta;
            e.Error = error - dN;
        }

        // ---- the scan ---------------------------------------------------------------------------------

        static void Rasterize (EdgeContext ctx, bool winding, int aa, ISpanSink sink, GpClip clip)
        {
            List<Edge> edges = ctx.Edges;
            // Inactive array sorted by (StartY, X): GDI+ quick-sorts then insertion-sorts with the
            // same 64-bit key, so a stable sort on the key gives the same order for equal keys only
            // when the quicksort would too -- equal keys are equal edges for the scan, so any order
            // between them draws the same spans.
            var keys = new long [edges.Count];
            var order = new int [edges.Count];
            for (int k = 0; k < edges.Count; k++) {
                keys [k] = ((long) edges [k].StartY << 32) | (uint) (edges [k].X + int.MaxValue);
                order [k] = k;
            }
            Array.Sort (keys, order);
            var inactive = new Edge [edges.Count + 1];
            for (int k = 0; k < edges.Count; k++) inactive [k] = edges [order [k]];
            var tail = new Edge { X = int.MaxValue, StartY = int.MaxValue, EndY = int.MinValue };
            inactive [edges.Count] = tail;
            var head = new Edge { X = int.MinValue, Next = tail };

            int y = inactive [0].StartY;
            int ip = 0;
            int shift = aa == 2 ? 3 : 2;
            int yBottom = ctx.MaxY;
            if (clip != null) {
                if (aa == 0) yBottom = Math.Min (yBottom, clip.Bounds.Bottom);
                else yBottom = Math.Min (yBottom, clip.Bounds.Bottom << shift);
            }
            if (yBottom <= y) return;

            IFiller filler = aa == 0 ? new AliasedFiller (clip != null ? clip.Wrap (sink) : sink) : new AntialiasedFiller (clip, (IAaTarget) sink, shift);
            int yNextInactive = Insert (head, y, inactive, ref ip);
            filler.Fill (head, y, winding);
            while (++y < yBottom) {
                Advance (head, y);
                if (y == yNextInactive)
                    yNextInactive = Insert (head, y, inactive, ref ip);
                filler.Fill (head, y, winding);
            }
            filler.End (y);
        }

        static int Insert (Edge active, int y, Edge[] inactive, ref int ip)
        {
            do {
                Edge n = inactive [ip];
                Edge a = active;
                while (a.Next.X < n.X) a = a.Next;
                n.Next = a.Next;
                a.Next = n;
                ip++;
            } while (inactive [ip].StartY == y);
            return inactive [ip].StartY;
        }

        static void Advance (Edge list, int y)
        {
            int outOfOrder = 0;
            Edge prev = list, cur = list.Next;
            while (true) {
                if (cur.EndY <= y) {
                    if (cur.EndY == int.MinValue) break;
                    cur = cur.Next;
                    prev.Next = cur;
                    continue;
                }
                cur.X += cur.Dx;
                cur.Error += cur.ErrorUp;
                if (cur.Error >= 0) { cur.Error -= cur.ErrorDown; cur.X++; }
                if (prev.X > cur.X) outOfOrder++;
                prev = cur;
                cur = cur.Next;
            }
            if (outOfOrder != 0) Sort (list);
        }

        static void Sort (Edge list)
        {
            bool swapped;
            do {
                swapped = false;
                Edge prev = list, cur = list.Next, next = cur.Next;
                int nextX = next.X;
                do {
                    if (nextX < cur.X) {
                        swapped = true;
                        prev.Next = next;
                        cur.Next = next.Next;
                        next.Next = cur;
                        (next, cur) = (cur, next);
                    }
                    prev = cur;
                    cur = next;
                    next = next.Next;
                } while ((nextX = next.X) != int.MaxValue);
            } while (swapped);
        }

        interface IFiller
        {
            void Fill (Edge active, int y, bool winding);
            void End (int y);
        }

        sealed class AliasedFiller : IFiller
        {
            readonly ISpanSink _out;
            public AliasedFiller (ISpanSink o) { _out = o; }

            public void Fill (Edge active, int y, bool winding)
            {
                Edge s = active.Next;
                if (!winding) {
                    while (s.X != int.MaxValue) {
                        Edge e = s.Next;
                        int left = s.X;
                        if (left != e.X) {
                            int right;
                            while ((right = e.X) == e.Next.X) e = e.Next.Next;
                            _out.OutputSpan (y, left, right);
                        }
                        s = e.Next;
                    }
                } else {
                    while (s.X != int.MaxValue) {
                        Edge e = s.Next;
                        int wv = s.Winding;
                        while ((wv += e.Winding) != 0) e = e.Next;
                        int left = s.X;
                        if (left != e.X) {
                            int right;
                            while ((right = e.X) == e.Next.X) {
                                s = e.Next;
                                e = s.Next;
                                wv = s.Winding;
                                while ((wv += e.Winding) != 0) e = e.Next;
                            }
                            _out.OutputSpan (y, left, right);
                        }
                        s = e.Next;
                    }
                }
            }

            public void End (int y) { }
        }

        // ---- the antialiased filler: an interval list per subrow set, coverage per pixel ----------------

        sealed class AntialiasedFiller : IFiller, ISpanSink
        {
            // Intervals: sorted boundaries in 1/8 pixel, each with the number of subrows covering
            // [X, next X). A sentinel at int.MaxValue ends the list.
            readonly List<int> _x = new List<int> ();
            readonly List<int> _d = new List<int> ();
            readonly ISpanSink _clipped;    // the clipper, which hands pieces back to OutputSpan below
            readonly IAaTarget _target;     // the brush span, which writes the pixels this scales
            readonly int _shift;

            public AntialiasedFiller (GpClip clip, IAaTarget target, int shift)
            {
                _target = target;
                _shift = shift;
                _clipped = clip != null ? clip.Wrap (this) : this;
                Reset ();
            }

            void Reset ()
            {
                _x.Clear (); _d.Clear ();
                _x.Add (int.MinValue); _d.Add (0);
                _x.Add (int.MaxValue); _d.Add (0);
            }

            // +1 over [l, r) (EpAntialiasedFiller's interval insert).
            void Add (int l, int r)
            {
                int i = Find (l);
                if (_x [i] != l) { _x.Insert (i, l); _d.Insert (i, _d [i - 1]); }
                int j = Find (r);
                if (_x [j] != r) { _x.Insert (j, r); _d.Insert (j, _d [j - 1]); }
                for (int k = i; k < j; k++) _d [k]++;
            }

            // First index with X >= v.
            int Find (int v)
            {
                int lo = 0, hi = _x.Count - 1;
                while (lo < hi) {
                    int mid = (lo + hi) >> 1;
                    if (_x [mid] < v) lo = mid + 1; else hi = mid;
                }
                return lo;
            }

            public void Fill (Edge active, int y, bool winding)
            {
                Edge s = active.Next;
                if (!winding) {
                    while (s.X != int.MaxValue) {
                        Edge e = s.Next;
                        int left = s.X;
                        if (left != e.X) {
                            Edge n = e.Next;
                            while (e.X == n.X) { e = n.Next; n = e.Next; }
                            Add (left, e.X);
                        }
                        s = e.Next;
                    }
                } else {
                    while (s.X != int.MaxValue) {
                        Edge e = s.Next;
                        int wv = s.Winding;
                        while ((wv += e.Winding) != 0) e = e.Next;
                        int left = s.X;
                        if (left != e.X) {
                            while (e.X == e.Next.X) {
                                s = e.Next;
                                e = s.Next;
                                wv = s.Winding;
                                while ((wv += e.Winding) != 0) e = e.Next;
                            }
                            Add (left, e.X);
                        }
                        s = e.Next;
                    }
                }
                int mask = (1 << _shift) - 1;
                if (((y + 1) & mask) == 0) Output (y);
            }

            public void End (int y)
            {
                // The rows of a last, partial pixel row.
                if ((y & ((1 << _shift) - 1)) != 0) Output (y);
            }

            int _row;

            void Output (int y)
            {
                _row = y >> _shift;
                // Spans of nonzero coverage, extended to whole pixels.
                int i = 1;
                while (_x [i] != int.MaxValue) {
                    if (_d [i] == 0) { i++; continue; }
                    int start = _x [i];
                    int k = i;
                    while (_d [k] != 0 || ((_x [k + 1] ^ _x [k]) & ~7) == 0) {
                        k++;
                        if (_x [k] == int.MaxValue) break;
                    }
                    _clipped.OutputSpan (_row, start >> 3, (_x [k] + 7) >> 3);
                    i = k;
                    if (_x [i] == int.MaxValue) break;
                    i++;
                }
                Reset ();
            }

            // A clipped piece: the brush paints it, then coverage scales it.
            public void OutputSpan (int y, int left, int right)
            {
                _target.OutputSpan (y, left, right);
                uint[] buf = _target.Buffer;
                int s = _shift;
                int x0 = left * 8, x1 = right * 8;
                int iv = Find (x0 + 1) - 1;   // the interval holding x0
                int p = 0;
                int px = x0;
                while (px < x1) {
                    int pe = px + 8;
                    int cov = 0;
                    int k = iv;
                    while (_x [k + 1] <= px) k++;
                    int kk = k;
                    while (true) {
                        int a = Math.Max (_x [kk], px), b = Math.Min (_x [kk + 1], pe);
                        if (b > a) cov += (b - a) * _d [kk];
                        if (_x [kk + 1] >= pe) break;
                        kk++;
                    }
                    buf [p] = Scale (buf [p], cov, s + 3);
                    p++;
                    // Whole pixels after this one inside the interval kk.
                    int runEnd = Math.Min (_x [kk + 1], x1);
                    int run = (runEnd - pe) >> 3;
                    if (run > 0) {
                        int depth = _d [kk];
                        if (depth != 1 << s)
                            for (int r = 0; r < run; r++) buf [p + r] = Scale (buf [p + r], depth, s);
                        p += run;
                    }
                    px = pe + (run > 0 ? run * 8 : 0);
                    iv = kk;
                }
            }

            static uint Scale (uint c, int k, int sh)
            {
                int round = 1 << (sh - 1);
                uint a = (uint) (((int) (c >> 24) * k + round) >> sh);
                uint r = (uint) (((int) ((c >> 16) & 0xff) * k + round) >> sh);
                uint g = (uint) (((int) ((c >> 8) & 0xff) * k + round) >> sh);
                uint b = (uint) (((int) (c & 0xff) * k + round) >> sh);
                return a << 24 | r << 16 | g << 8 | b;
            }
        }
    }

    /// <summary>A brush span the antialiaser scales: it writes the span's premultiplied pixels into
    /// <see cref="Buffer"/> (index 0 = the span's left pixel) on OutputSpan.</summary>
    internal interface IAaTarget : ISpanSink
    {
        uint[] Buffer { get; }
    }
}
