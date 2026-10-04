// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// A GDI region in device pixels: horizontal bands, each a sorted run of disjoint [x0, x1) intervals,
// as RGNOBJ keeps its scans. Combined (AND, OR, XOR, DIFF, COPY) a band at a time.
//

using System.Collections.Generic;

namespace System.Drawing.WebGpuBackend.Gdip
{
    internal sealed class GdiRgn
    {
        internal readonly struct Band
        {
            public readonly int Y0, Y1;
            public readonly int[] X;     // x0, x1, x0, x1, ...
            public Band(int y0, int y1, int[] x) { Y0 = y0; Y1 = y1; X = x; }
        }

        readonly List<Band> _bands = new List<Band>();

        public IReadOnlyList<Band> Bands => _bands;
        public bool Empty => _bands.Count == 0;

        public GdiRgn Clone()
        {
            var r = new GdiRgn();
            r._bands.AddRange(_bands);
            return r;
        }

        public static GdiRgn FromRect(int l, int t, int r, int b)
        {
            var g = new GdiRgn();
            if (l < r && t < b) g._bands.Add(new Band(t, b, new[] { l, r }));
            return g;
        }

        /// <summary>A region from rectangles (any order, may overlap).</summary>
        public static GdiRgn FromRects(IEnumerable<(int L, int T, int R, int B)> rects)
        {
            var g = new GdiRgn();
            foreach (var q in rects) g = Combine(g, FromRect(q.L, q.T, q.R, q.B), 2);
            return g;
        }

        /// <summary>A region from fill spans (scan line by scan line, each line left to right).</summary>
        public static GdiRgn FromSpans(List<GdiSpan> spans)
        {
            var g = new GdiRgn();
            int i = 0;
            var row = new List<int>();
            while (i < spans.Count)
            {
                int y = spans[i].Y;
                row.Clear();
                for (; i < spans.Count && spans[i].Y == y; i++)
                {
                    int x0 = spans[i].X0, x1 = spans[i].X1;
                    if (row.Count > 0 && x0 <= row[row.Count - 1]) { if (x1 > row[row.Count - 1]) row[row.Count - 1] = x1; }
                    else { row.Add(x0); row.Add(x1); }
                }
                g.Append(y, y + 1, row.ToArray());
            }
            return g;
        }

        void Append(int y0, int y1, int[] x)
        {
            if (x.Length == 0 || y0 >= y1) return;
            if (_bands.Count > 0)
            {
                Band last = _bands[_bands.Count - 1];
                if (last.Y1 == y0 && Same(last.X, x)) { _bands[_bands.Count - 1] = new Band(last.Y0, y1, last.X); return; }
            }
            _bands.Add(new Band(y0, y1, x));
        }

        static bool Same(int[] a, int[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        /// <summary>The intervals of the band covering y, or null.</summary>
        public int[] Row(int y)
        {
            int lo = 0, hi = _bands.Count - 1;
            while (lo <= hi)
            {
                int m = (lo + hi) >> 1;
                Band b = _bands[m];
                if (y < b.Y0) hi = m - 1;
                else if (y >= b.Y1) lo = m + 1;
                else return b.X;
            }
            return null;
        }

        public bool Bounds(out int l, out int t, out int r, out int b)
        {
            l = t = int.MaxValue; r = b = int.MinValue;
            foreach (Band band in _bands)
            {
                if (band.Y0 < t) t = band.Y0;
                if (band.Y1 > b) b = band.Y1;
                if (band.X[0] < l) l = band.X[0];
                if (band.X[band.X.Length - 1] > r) r = band.X[band.X.Length - 1];
            }
            return _bands.Count > 0;
        }

        public GdiRgn Offset(int dx, int dy)
        {
            var g = new GdiRgn();
            foreach (Band b in _bands)
            {
                var x = new int[b.X.Length];
                for (int i = 0; i < x.Length; i++) x[i] = b.X[i] + dx;
                g._bands.Add(new Band(b.Y0 + dy, b.Y1 + dy, x));
            }
            return g;
        }

        /// <summary>RGN_AND 1, RGN_OR 2, RGN_XOR 3, RGN_DIFF 4, RGN_COPY 5.</summary>
        public static GdiRgn Combine(GdiRgn a, GdiRgn b, int mode)
        {
            if (mode == 5) return a.Clone();
            var ys = new SortedSet<int>();
            foreach (Band x in a._bands) { ys.Add(x.Y0); ys.Add(x.Y1); }
            foreach (Band x in b._bands) { ys.Add(x.Y0); ys.Add(x.Y1); }
            var r = new GdiRgn();
            int? prev = null;
            foreach (int y in ys)
            {
                if (prev.HasValue)
                {
                    int y0 = prev.Value;
                    int[] ra = a.Row(y0) ?? Array.Empty<int>(), rb = b.Row(y0) ?? Array.Empty<int>();
                    int[] x = Combine1(ra, rb, mode);
                    r.Append(y0, y, x);
                }
                prev = y;
            }
            return r;
        }

        static int[] Combine1(int[] a, int[] b, int mode)
        {
            var edges = new SortedSet<int>();
            foreach (int v in a) edges.Add(v);
            foreach (int v in b) edges.Add(v);
            var o = new List<int>();
            int? prev = null;
            foreach (int x in edges)
            {
                if (prev.HasValue)
                {
                    int x0 = prev.Value;
                    bool ia = In(a, x0), ib = In(b, x0);
                    bool on = mode switch { 1 => ia && ib, 2 => ia || ib, 3 => ia != ib, 4 => ia && !ib, _ => ia };
                    if (on)
                    {
                        if (o.Count > 0 && o[o.Count - 1] == x0) o[o.Count - 1] = x;
                        else { o.Add(x0); o.Add(x); }
                    }
                }
                prev = x;
            }
            return o.ToArray();
        }

        static bool In(int[] row, int x)
        {
            for (int i = 0; i < row.Length; i += 2)
            {
                if (x < row[i]) return false;
                if (x < row[i + 1]) return true;
            }
            return false;
        }

        /// <summary>The rectangles, band by band.</summary>
        public IEnumerable<(int L, int T, int R, int B)> Rects()
        {
            foreach (Band b in _bands)
                for (int i = 0; i < b.X.Length; i += 2) yield return (b.X[i], b.Y0, b.X[i + 1], b.Y1);
        }
    }
}
