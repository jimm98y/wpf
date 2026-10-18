// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// GDI+'s device-space region (DpRegion): integer y bands, each a sorted list of disjoint [x0, x1)
// spans, adjacent identical bands merged. The clip every drawing call goes through (the context's
// visible clip = the app clip's device region AND the surface bounds), and what GetRegionScans hands
// out.
//
// DpClipRegion::OutputSpan splits a span by the band it falls in; a rectangle clip is the special
// case GDI+ also short-cuts.
//

using System.Collections.Generic;

namespace System.Drawing.WebGpuBackend.Gdip
{
    internal sealed class DpRegion
    {
        // Infinite is GDI+'s: a rectangle of +-(2^22) on a side.
        public const int InfiniteMin = -4194304, InfiniteMax = 4194304;

        internal struct Band
        {
            public int Top, Bottom;
            public int[] X;   // x0, x1, x0, x1, ...
        }

        internal readonly List<Band> Bands = new List<Band> ();

        public DpRegion () { }

        public static DpRegion FromRect (int x, int y, int w, int h)
        {
            var r = new DpRegion ();
            if (w > 0 && h > 0) r.Bands.Add (new Band { Top = y, Bottom = y + h, X = new[] { x, x + w } });
            return r;
        }

        public static DpRegion Infinite () => FromRect (InfiniteMin, InfiniteMin, InfiniteMax - InfiniteMin, InfiniteMax - InfiniteMin);

        public DpRegion Clone ()
        {
            var r = new DpRegion ();
            foreach (Band b in Bands) r.Bands.Add (new Band { Top = b.Top, Bottom = b.Bottom, X = (int[]) b.X.Clone () });
            return r;
        }

        public bool IsEmpty => Bands.Count == 0;

        public bool IsInfinite
            => Bands.Count == 1 && Bands [0].Top <= InfiniteMin && Bands [0].Bottom >= InfiniteMax
               && Bands [0].X.Length == 2 && Bands [0].X [0] <= InfiniteMin && Bands [0].X [1] >= InfiniteMax;

        public bool IsRect => Bands.Count == 1 && Bands [0].X.Length == 2;

        public Rectangle Bounds
        {
            get {
                if (Bands.Count == 0) return Rectangle.Empty;
                int l = int.MaxValue, r = int.MinValue;
                foreach (Band b in Bands) { l = Math.Min (l, b.X [0]); r = Math.Max (r, b.X [b.X.Length - 1]); }
                return Rectangle.FromLTRB (l, Bands [0].Top, r, Bands [Bands.Count - 1].Bottom);
            }
        }

        /// <summary>Builds a region from spans output row by row (a rasterized path).</summary>
        internal sealed class Builder : ISpanSink
        {
            readonly SortedDictionary<int, List<int>> _rows = new SortedDictionary<int, List<int>> ();
            public void OutputSpan (int y, int left, int right)
            {
                if (right <= left) return;
                if (!_rows.TryGetValue (y, out List<int> l)) _rows [y] = l = new List<int> ();
                l.Add (left); l.Add (right);
            }
            public DpRegion Build ()
            {
                var r = new DpRegion ();
                foreach (var kv in _rows) {
                    int[] x = Normalize (kv.Value);
                    r.AppendRow (kv.Key, kv.Key + 1, x);
                }
                return r;
            }
        }

        static int[] Normalize (List<int> spans)
        {
            var pairs = new List<(int, int)> ();
            for (int i = 0; i + 1 < spans.Count; i += 2) pairs.Add ((spans [i], spans [i + 1]));
            pairs.Sort ();
            var o = new List<int> ();
            foreach ((int a, int b) in pairs) {
                if (o.Count > 0 && a <= o [o.Count - 1]) { o [o.Count - 1] = Math.Max (o [o.Count - 1], b); continue; }
                o.Add (a); o.Add (b);
            }
            return o.ToArray ();
        }

        void AppendRow (int top, int bottom, int[] x)
        {
            if (x.Length == 0 || bottom <= top) return;
            if (Bands.Count > 0) {
                Band last = Bands [Bands.Count - 1];
                if (last.Bottom == top && SameSpans (last.X, x)) { last.Bottom = bottom; Bands [Bands.Count - 1] = last; return; }
            }
            Bands.Add (new Band { Top = top, Bottom = bottom, X = x });
        }

        static bool SameSpans (int[] a, int[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a [i] != b [i]) return false;
            return true;
        }

        // ---- combining ------------------------------------------------------------------------------

        public enum Op { And, Or, Xor, Exclude, Complement }

        // Both band lists are in y order without overlap, and each band's spans in x order without
        // overlap or touching (Normalize, CombineSpans and FromRect all keep that), so a combine is two
        // merges walked side by side. It was a SortedSet per row and a linear band search per y, and a
        // DotNetBar repaint -- a clip set and restored per item -- spent most of its time in the GC.

        [ThreadStatic] static int[] t_ys;
        [ThreadStatic] static List<int> t_spans;

        public static DpRegion Combine (DpRegion a, DpRegion b, Op op)
        {
            int na = a.Bands.Count, nb = b.Bands.Count;
            int[] ys = t_ys;
            if (ys == null || ys.Length < 2 * (na + nb)) t_ys = ys = new int [Math.Max (64, 2 * (na + nb))];
            int n = 0;
            foreach (Band x in a.Bands) { ys [n++] = x.Top; ys [n++] = x.Bottom; }
            foreach (Band x in b.Bands) { ys [n++] = x.Top; ys [n++] = x.Bottom; }
            System.Array.Sort (ys, 0, n);
            var r = new DpRegion ();
            int ia = 0, ib = 0;
            bool ordered = a.Ordered () && b.Ordered ();
            bool have = false; int y0 = 0;
            for (int k = 0; k < n; k++) {
                int y = ys [k];
                if (have && y == y0) continue;          // unique
                if (have) {
                    int[] sa, sb;
                    if (ordered) {
                        while (ia < na && a.Bands [ia].Bottom <= y0) ia++;
                        while (ib < nb && b.Bands [ib].Bottom <= y0) ib++;
                        sa = ia < na && a.Bands [ia].Top <= y0 ? a.Bands [ia].X : System.Array.Empty<int> ();
                        sb = ib < nb && b.Bands [ib].Top <= y0 ? b.Bands [ib].X : System.Array.Empty<int> ();
                    } else { sa = a.SpansAt (y0); sb = b.SpansAt (y0); }
                    r.AppendRow (y0, y, CombineSpans (sa, sb, op));
                }
                y0 = y; have = true;
            }
            return r;
        }

        /// <summary>Bands in y order and not overlapping, as every builder here makes them.</summary>
        bool Ordered ()
        {
            for (int i = 0; i < Bands.Count; i++) {
                if (Bands [i].Bottom <= Bands [i].Top) return false;
                if (i > 0 && Bands [i].Top < Bands [i - 1].Bottom) return false;
            }
            return true;
        }

        int[] SpansAt (int y)
        {
            foreach (Band b in Bands)
                if (b.Top <= y && y < b.Bottom) return b.X;
            return System.Array.Empty<int> ();
        }

        static int[] CombineSpans (int[] a, int[] b, Op op)
        {
            if (!Strict (a) || !Strict (b)) return CombineSpansGeneral (a, b, op);
            List<int> o = t_spans ??= new List<int> ();
            o.Clear ();
            int i = 0, j = 0;              // next unread boundary in a and in b
            bool have = false; int x0 = 0;
            while (i < a.Length || j < b.Length) {
                int x;
                if (j >= b.Length || (i < a.Length && a [i] <= b [j])) {
                    x = a [i++];
                    if (j < b.Length && b [j] == x) j++;
                } else x = b [j++];
                if (have && x == x0) continue;          // a repeated boundary bounds nothing
                if (have) {
                    // inside a / b over [x0, x): an odd number of a's boundaries are at or before x0
                    bool ia = InsideBefore (a, i, x), ib = InsideBefore (b, j, x);
                    bool keep = op switch {
                        Op.And => ia && ib, Op.Or => ia || ib, Op.Xor => ia != ib,
                        Op.Exclude => ia && !ib, _ => ib && !ia,
                    };
                    if (keep) {
                        if (o.Count > 0 && o [o.Count - 1] == x0) o [o.Count - 1] = x;
                        else { o.Add (x0); o.Add (x); }
                    }
                }
                x0 = x; have = true;
            }
            return o.Count == 0 ? System.Array.Empty<int> () : o.ToArray ();
        }

        static bool Strict (int[] s)
        {
            if ((s.Length & 1) != 0) return false;
            for (int i = 1; i < s.Length; i++) if (s [i] <= s [i - 1]) return false;
            return true;
        }

        /// <summary>Any spans, sorted or not, overlapping or not: every boundary of either, and each
        /// interval between two kept or dropped by asking each side whether it covers its start.</summary>
        static int[] CombineSpansGeneral (int[] a, int[] b, Op op)
        {
            var xs = new SortedSet<int> ();
            foreach (int v in a) xs.Add (v);
            foreach (int v in b) xs.Add (v);
            var o = new List<int> ();
            int? prev = null;
            foreach (int x in xs) {
                if (prev is int x0) {
                    bool ia = Inside (a, x0), ib = Inside (b, x0);
                    bool keep = op switch {
                        Op.And => ia && ib, Op.Or => ia || ib, Op.Xor => ia != ib,
                        Op.Exclude => ia && !ib, _ => ib && !ia,
                    };
                    if (keep) {
                        if (o.Count > 0 && o [o.Count - 1] == x0) o [o.Count - 1] = x;
                        else { o.Add (x0); o.Add (x); }
                    }
                }
                prev = x;
            }
            return o.ToArray ();
        }

        /// <summary>Whether the interval just before boundary <paramref name="x"/> is inside spans
        /// <paramref name="s"/>, given <paramref name="read"/> of its boundaries are at or before x:
        /// the boundaries strictly before x number read, less one if s[read-1] == x.</summary>
        static bool InsideBefore (int[] s, int read, int x)
        {
            int before = read;
            if (before > 0 && s [before - 1] == x) before--;   // x itself is not before x
            return (before & 1) == 1;
        }

        static bool Inside (int[] s, int x)
        {
            for (int i = 0; i + 1 < s.Length; i += 2)
                if (s [i] <= x && x < s [i + 1]) return true;
            return false;
        }

        public void Offset (int dx, int dy)
        {
            for (int i = 0; i < Bands.Count; i++) {
                Band b = Bands [i];
                b.Top += dy; b.Bottom += dy;
                for (int k = 0; k < b.X.Length; k++) b.X [k] += dx;
                Bands [i] = b;
            }
        }

        public bool Contains (int x, int y)
        {
            foreach (Band b in Bands)
                if (b.Top <= y && y < b.Bottom) return Inside (b.X, x);
            return false;
        }

        /// <summary>DpRegion::GetRectVisibility: whether any part of [x, x+w) x [y, y+h) is inside.</summary>
        public bool Intersects (Rectangle r)
        {
            foreach (Band b in Bands) {
                if (b.Bottom <= r.Top || b.Top >= r.Bottom) continue;
                for (int i = 0; i + 1 < b.X.Length; i += 2)
                    if (b.X [i] < r.Right && b.X [i + 1] > r.Left) return true;
            }
            return false;
        }

        public IEnumerable<Rectangle> Rects ()
        {
            foreach (Band b in Bands)
                for (int i = 0; i + 1 < b.X.Length; i += 2)
                    yield return Rectangle.FromLTRB (b.X [i], b.Top, b.X [i + 1], b.Bottom);
        }
    }

    /// <summary>The visible clip of a drawing call (DpClipRegion): what spans are cut to.</summary>
    internal sealed class GpClip
    {
        public readonly DpRegion Region;
        public readonly Rectangle Bounds;
        readonly bool _rect;

        public GpClip (DpRegion region)
        {
            Region = region;
            Bounds = region.Bounds;
            _rect = region.IsRect || region.IsEmpty;
        }

        public bool IsEmpty => Region.IsEmpty;

        public bool IsVisible (Rectangle r) => !Region.IsEmpty && Region.Intersects (r);

        /// <summary>A sink that cuts spans to the clip before they reach <paramref name="inner"/>.</summary>
        public ISpanSink Wrap (ISpanSink inner) => _rect ? new RectClipper (Bounds, inner) : new BandClipper (Region, inner);

        sealed class RectClipper : ISpanSink
        {
            readonly Rectangle _r;
            readonly ISpanSink _inner;
            public RectClipper (Rectangle r, ISpanSink inner) { _r = r; _inner = inner; }
            public void OutputSpan (int y, int left, int right)
            {
                if (y < _r.Top || y >= _r.Bottom) return;
                if (left < _r.Left) left = _r.Left;
                if (right > _r.Right) right = _r.Right;
                if (left < right) _inner.OutputSpan (y, left, right);
            }
        }

        sealed class BandClipper : ISpanSink
        {
            readonly DpRegion _region;
            readonly ISpanSink _inner;
            int _band;
            public BandClipper (DpRegion region, ISpanSink inner) { _region = region; _inner = inner; }
            public void OutputSpan (int y, int left, int right)
            {
                var bands = _region.Bands;
                if (bands.Count == 0) return;
                if (_band >= bands.Count || bands [_band].Top > y) _band = 0;
                while (_band < bands.Count && bands [_band].Bottom <= y) _band++;
                if (_band >= bands.Count || bands [_band].Top > y) return;
                int[] x = bands [_band].X;
                for (int i = 0; i + 1 < x.Length; i += 2) {
                    int l = Math.Max (left, x [i]), r = Math.Min (right, x [i + 1]);
                    if (l < r) _inner.OutputSpan (y, l, r);
                }
            }
        }
    }
}
