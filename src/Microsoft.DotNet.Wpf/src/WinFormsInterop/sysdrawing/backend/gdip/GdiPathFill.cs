// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// GDI's path and its fill, as win32k builds and fills them (not GDI+'s): device coordinates in
// 28.4 (FIX), subpaths of lines and Bezier triples, and the global-edge-table scan converter.
//
//   bConstructGET @140059cf0 / AddEdgeToGET @140051a88   every subpath closed by an edge back to its
//                                                        start; an edge covers the scan lines whose
//                                                        y, (y0 + 15) >> 4 .. (y1 + 15) >> 4 less
//                                                        one, its top through its bottom; its x on a
//                                                        scan is the DDA from the top point, stepped
//                                                        a FIX at a time to the first scan and
//                                                        rounded up to a pixel
//   bFill @1401d1fc8 (win32kfull)                        the active edges sorted by that pixel x
//                                                        (vXSortAETEdges, a bubble sort), spans
//                                                        [x0, x1) by alternate pairs or by winding
//                                                        count; vAdvanceAETEdges steps them
//   EPATHOBJ::bFlatten @1400719d0 (win32kbase)            each Bezier replaced by its points from
//                                                        pprFlattenRec, the start point excluded
//
// bPaintPath @1401d27a8 hands a single subpath of up to 40 points to bFastFill @1401d1100, the convex
// filler; it walks the same DDA (x0 + ceil(k dx / dy), then up to the pixel) and so gives the same
// spans, which is why only bFill is here.
//

using System.Collections.Generic;

namespace System.Drawing.WebGpuBackend.Gdip
{
    /// <summary>A GDI path: device points in 28.4, each subpath its points with a flag for those
    /// that are Bezier control points (in triples after an on-curve point).</summary>
    internal sealed class GdiPath
    {
        internal sealed class Figure
        {
            public readonly List<int> X = new List<int>(), Y = new List<int>();
            public readonly List<bool> Bezier = new List<bool>();
            public bool Closed;
            public int Count => X.Count;
            public bool HasBeziers { get { foreach (bool b in Bezier) if (b) return true; return false; } }
        }

        public readonly List<Figure> Figures = new List<Figure>();
        int _mx, _my;
        bool _pending;                   // a MoveTo not yet followed by a point

        public bool Empty => Figures.Count == 0;
        public int CurrentX { get; private set; }
        public int CurrentY { get; private set; }

        public void MoveTo(int x, int y)
        {
            _mx = x; _my = y; _pending = true;
            CurrentX = x; CurrentY = y;
        }

        Figure Open()
        {
            if (_pending || Figures.Count == 0 || Figures[Figures.Count - 1].Closed)
            {
                var f = new Figure();
                f.X.Add(_pending ? _mx : CurrentX); f.Y.Add(_pending ? _my : CurrentY); f.Bezier.Add(false);
                Figures.Add(f);
                _pending = false;
            }
            return Figures[Figures.Count - 1];
        }

        public void LineTo(int x, int y)
        {
            Figure f = Open();
            f.X.Add(x); f.Y.Add(y); f.Bezier.Add(false);
            CurrentX = x; CurrentY = y;
        }

        /// <summary>One cubic from the current point: two control points and the end.</summary>
        public void BezierTo(int x1, int y1, int x2, int y2, int x3, int y3)
        {
            Figure f = Open();
            f.X.Add(x1); f.Y.Add(y1); f.Bezier.Add(true);
            f.X.Add(x2); f.Y.Add(y2); f.Bezier.Add(true);
            f.X.Add(x3); f.Y.Add(y3); f.Bezier.Add(true);
            CurrentX = x3; CurrentY = y3;
        }

        /// <summary>EPATHOBJ::bCloseFigure: the subpath is closed and the next point starts another
        /// at its start.</summary>
        public void CloseFigure()
        {
            if (Figures.Count == 0 || _pending) return;
            Figure f = Figures[Figures.Count - 1];
            if (f.Closed) return;
            f.Closed = true;
            CurrentX = f.X[0]; CurrentY = f.Y[0];
        }

        public void CloseAll()
        {
            foreach (Figure f in Figures) f.Closed = true;
        }

        public void Append(GdiPath p)
        {
            foreach (Figure f in p.Figures) Figures.Add(f);
            CurrentX = p.CurrentX; CurrentY = p.CurrentY;
            _pending = false;
        }

        public bool HasBeziers { get { foreach (Figure f in Figures) if (f.HasBeziers) return true; return false; } }

        /// <summary>EPATHOBJ::bFlatten: every Bezier replaced by its flattened points.</summary>
        public GdiPath Flattened()
        {
            if (!HasBeziers) return this;
            var r = new GdiPath();
            var bez = new int[8];
            var buf = new int[2 * 64];
            foreach (Figure f in Figures)
            {
                var g = new Figure { Closed = f.Closed };
                g.X.Add(f.X[0]); g.Y.Add(f.Y[0]); g.Bezier.Add(false);
                for (int i = 1; i < f.Count; i++)
                {
                    if (!f.Bezier[i] || i + 2 >= f.Count)
                    {
                        g.X.Add(f.X[i]); g.Y.Add(f.Y[i]); g.Bezier.Add(false);
                        continue;
                    }
                    bez[0] = f.X[i - 1]; bez[1] = f.Y[i - 1];
                    for (int k = 0; k < 3; k++) { bez[2 + 2 * k] = f.X[i + k]; bez[3 + 2 * k] = f.Y[i + k]; }
                    var flat = new BezierFlattener(bez, null, true);
                    bool more;
                    do
                    {
                        int n = flat.Flatten(buf, 0, 64, out more);
                        for (int k = 0; k < n; k++) { g.X.Add(buf[2 * k]); g.Y.Add(buf[2 * k + 1]); g.Bezier.Add(false); }
                    } while (more);
                    i += 2;
                }
                r.Figures.Add(g);
            }
            r.CurrentX = CurrentX; r.CurrentY = CurrentY;
            return r;
        }

        /// <summary>The bounds in 28.4 (left, top, right, bottom inclusive); false when empty.</summary>
        public bool Bounds(out int l, out int t, out int r, out int b)
        {
            l = t = int.MaxValue; r = b = int.MinValue;
            foreach (Figure f in Figures)
                for (int i = 0; i < f.Count; i++)
                {
                    if (f.X[i] < l) l = f.X[i];
                    if (f.X[i] > r) r = f.X[i];
                    if (f.Y[i] < t) t = f.Y[i];
                    if (f.Y[i] > b) b = f.Y[i];
                }
            return l <= r;
        }

        public void Offset(int dx, int dy)
        {
            foreach (Figure f in Figures)
                for (int i = 0; i < f.Count; i++) { f.X[i] += dx; f.Y[i] += dy; }
            CurrentX += dx; CurrentY += dy;
        }
    }

    /// <summary>A run of pixels [X0, X1) on scan line Y.</summary>
    internal readonly struct GdiSpan
    {
        public readonly int Y, X0, X1;
        public GdiSpan(int y, int x0, int x1) { Y = y; X0 = x0; X1 = x1; }
    }

    /// <summary>win32k's path fill (bFill over bConstructGET's edge table).</summary>
    internal static class GdiFill
    {
        sealed class Edge
        {
            public Edge Next;
            public int Y;                 // the first scan line
            public int Count;             // scan lines left
            public int X;                 // the pixel x on the current scan
            public long Err, ErrUp, ErrDown;
            public int Whole, Dir;
            public int Winding;
        }

        /// <summary>The spans a flattened <paramref name="path"/> covers, scan line by scan line,
        /// each line's spans left to right. <paramref name="clip"/>: left, top, right, bottom in
        /// pixels (exclusive right and bottom), or null.</summary>
        public static List<GdiSpan> Spans(GdiPath path, bool winding, int[] clip = null)
        {
            var spans = new List<GdiSpan>();
            path = path.Flattened();
            // bFill's clip rectangle in FIX for the edge table: its top and bottom.
            int clipTop = 0, clipBottom = 0;
            bool clipped = clip != null;
            if (clipped) { clipTop = clip[1] << 4; clipBottom = clip[3] << 4; }
            var get = new Edge { Y = int.MaxValue };   // the global edge table's head (sorted list)
            get.Next = get;
            foreach (GdiPath.Figure f in path.Figures)
            {
                int n = f.Count;
                if (n < 1) continue;
                for (int i = 1; i < n; i++) Add(get, f.X[i - 1], f.Y[i - 1], f.X[i], f.Y[i], clipped, clipTop, clipBottom);
                Add(get, f.X[n - 1], f.Y[n - 1], f.X[0], f.Y[0], clipped, clipTop, clipBottom);
            }
            var aet = new Edge { Y = int.MaxValue, X = int.MaxValue };   // its x stops vMoveNewEdges
            aet.Next = aet;
            int y = int.MinValue;
            while (true)
            {
                if (aet.Next != aet) Advance(aet);
                if (aet.Next == aet)
                {
                    if (get.Next == get) break;
                    y = get.Next.Y;
                }
                else if (aet.Next.Next != aet) XSort(aet);
                if (get.Next != get && get.Next.Y == y) MoveNew(get, aet, y);
                for (Edge e = aet.Next; e != aet; e = e.Next)
                {
                    int x0 = e.X;
                    if (winding)
                    {
                        int w = e.Winding;
                        do { e = e.Next; w += e.Winding; } while (w != 0 && e != aet);
                        if (e == aet) break;
                    }
                    else
                    {
                        e = e.Next;
                        if (e == aet) break;
                    }
                    int x1 = e.X;
                    if (clipped)
                    {
                        if (x0 < clip[0]) x0 = clip[0];
                        if (x1 > clip[2]) x1 = clip[2];
                    }
                    if (x0 < x1) spans.Add(new GdiSpan(y, x0, x1));
                }
                y++;
            }
            return spans;
        }

        /// <summary>AddEdgeToGET: the edge from (x0, y0) to (x1, y1), its DDA started on its first scan.</summary>
        static void Add(Edge get, int ax, int ay, int bx, int by, bool clipped, int clipTop, int clipBottom)
        {
            int dy = by - ay;
            bool down = dy >= 0;
            int xTop = down ? ax : bx, yTop = down ? ay : by;
            int xBot = down ? bx : ax, yBot = down ? by : ay;
            int adY = down ? dy : -dy;
            var e = new Edge { Winding = down ? 1 : -1 };
            int yStart = yTop, yEnd = yBot, from = yTop;
            bool clipStart = false;
            if (clipped)
            {
                if (yBot < clipTop || clipBottom < yTop) return;
                if (yTop < clipTop) { yStart = clipTop; clipStart = true; }
                if (yEnd > clipBottom) yEnd = clipBottom;
            }
            e.Y = (yStart + 15) >> 4;
            e.Count = ((yEnd + 15) >> 4) - e.Y;
            if (e.Count <= 0) return;
            int dx = xBot - xTop;
            long err;
            if (dx < 0) { dx = -dx; err = -(long)adY; e.Dir = -1; }
            else { err = -1; e.Dir = 1; }
            int rem;
            if (dx < adY) { e.Whole = 0; rem = dx; }
            else
            {
                e.Whole = dx / adY;
                if (e.Dir == -1) e.Whole = -e.Whole;
                rem = dx % adY;
            }
            int to = clipStart ? clipTop : (yStart + 15) & ~15;
            int x = xTop;
            for (int k = to - from; k > 0; k--)
            {
                x += e.Whole;
                err += rem;
                if (err >= 0) { err -= adY; x += e.Dir; }
            }
            e.X = (x + 15) >> 4;
            if (e.Dir == 1) err -= (long)(((x + 15) & ~15) - x) * adY;
            else err -= (long)((x - 1) & 15) * adY;
            e.Err = err;
            e.ErrUp = (long)rem << 4;
            e.ErrDown = (long)adY << 4;
            // Sorted by first scan, then x.
            Edge p = get;
            while (true)
            {
                Edge q = p.Next;
                if (q.Y < e.Y || (q.Y == e.Y && q.X < e.X)) { p = q; continue; }
                break;
            }
            e.Next = p.Next;
            p.Next = e;
        }

        static void Advance(Edge aet)
        {
            Edge prev = aet;
            for (Edge e = aet.Next; e != aet; e = e.Next)
            {
                if (--e.Count == 0) { prev.Next = e.Next; continue; }
                e.X += e.Whole;
                e.Err += e.ErrUp;
                if (e.Err >= 0) { e.Err -= e.ErrDown; e.X += e.Dir; }
                prev = e;
            }
        }

        static void XSort(Edge aet)
        {
            bool swapped;
            do
            {
                swapped = false;
                Edge prev = aet, a = aet.Next;
                while (a.Next != aet)
                {
                    Edge b = a.Next;
                    if (b.X < a.X)
                    {
                        prev.Next = b; a.Next = b.Next; b.Next = a;
                        swapped = true;
                        prev = b;
                    }
                    else { prev = a; a = b; }
                }
            } while (swapped);
        }

        static void MoveNew(Edge get, Edge aet, int y)
        {
            Edge after = aet;
            do
            {
                Edge e = get.Next;
                Edge q = after.Next;
                while (q.X < e.X) { after = q; q = q.Next; }
                get.Next = e.Next;
                e.Next = after.Next;
                after.Next = e;
                after = e;
            } while (get.Next.Y == y);
        }
    }
}
