// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Grid-fitting: moving a glyph's edges onto whole pixels before it is rasterized.
//
// WHY. An outline scaled straight to a small size lands its stems wherever the arithmetic puts
// them, which is almost never on a pixel boundary. A stem 1.1 pixels wide straddling two columns
// covers each of them a little over half, so the darkest pixel in the whole glyph comes out grey.
// Measured against Windows drawing the same string at nine point: our darkest pixel was 20/255
// where GDI's was 0. That is the whole of the difference people mean by "the text looks soft" --
// the positions were already right.
//
// Windows solves it by hinting: GDI runs the font's own TrueType bytecode, which the font's
// designer wrote to say where the stems and the horizontal features must go at each size. This
// does the same job by ANALYSING the outline rather than executing the font's instructions -- the
// approach FreeType calls its autofitter, and the same one used for fonts that carry no hints at
// all. It needs no interpreter, no font-specific state, and cannot be broken by a font whose
// bytecode assumes a rasterizer we are not.
//
// WHAT IT DOES, in the order it does it:
//
//   1. SEGMENTS. Walk the outline; a run of points that moves along one axis while barely moving
//      across it is a straight edge -- the side of a stem, the flat top of an 'x'.
//   2. EDGES. Segments at (nearly) the same coordinate are one feature: the left side of the 'n's
//      two stems is one edge, however many segments make it up.
//   3. STEMS. An edge and the nearest edge facing the other way are the two sides of a stem, and
//      the distance between them is its width.
//   4. FITTING. Anchor the edges that sit on a BLUE ZONE (the baseline, the x-height, the cap
//      height -- the lines a reader's eye follows) to whole pixels; give every stem a whole number
//      of pixels of width, never less than one; round what is left.
//   5. INTERPOLATION. Everything that is not an edge -- the curves between them -- is carried
//      along by the fitted edges either side of it, so the glyph keeps its shape.
//
// BOTH AXES are fitted, which is what GDI does and what makes a stem land in one column instead
// of two. Fitting only the vertical (FreeType's "light", DirectWrite's "natural") leaves stems
// where they were and would not close the gap this exists to close.
//
// That claim was once made here without evidence, disproved with the wrong instrument, and then
// proved properly, so it is worth writing down which instrument answers it. Asking GDI+ --
// System.Drawing, TextRenderingHint.AntiAliasGridFit -- for Segoe UI's 'm' gives three stems all
// straddling two columns and none snapped, which says GDI does not fit x. GDI+ IS NOT GDI: it has
// its own rasterizer. Asking real GDI, the path WinForms' TextRenderer takes -- CreateFontIndirect
// with ANTIALIASED_QUALITY, ExtTextOut into a DIB -- gives the same 'm' with its stems on columns
// 58 and 62, fully covered, one column each; with NONANTIALIASED_QUALITY, which is the grid with
// nothing on top of it, they are single columns exactly. So x is fitted, and hard.
//
// The lesson is in WindowsGlyphParityTests, which draws through GDI and subtracts the images.
// Anything measured against GDI+, or against a screen capture (that is ClearType, which spreads a
// stem over three subpixels and makes every stem measure wider than it is), is measuring the wrong
// thing.
//

using System;
using System.Collections.Generic;
using System.Numerics;

namespace Microsoft.Wpf.Interop.WebGpu.Composition.Text
{
    /// <summary>One of the horizontal lines a reader's eye follows -- the baseline, the top of the
    /// lower-case letters, the top of the capitals -- in font units.</summary>
    /// <remarks>
    /// A zone has two references because round letters overshoot flat ones: the top of 'o' sits a
    /// little above the top of 'x' so that the two LOOK the same height. At a size where that
    /// overshoot is less than half a pixel it cannot be drawn and must not be rounded to a whole
    /// one, or every 'o' in the line stands a pixel taller than every 'x'.
    /// </remarks>
    internal readonly struct HintZone
    {
        public readonly float Flat;      // the flat reference ('x', 'H'), font units
        public readonly float Round;     // the round one ('o', 'O'); equal to Flat when unknown
        public readonly bool Top;        // whether this is the top of the letters or their foot

        public HintZone(float flat, float round, bool top)
        {
            Flat = flat; Round = round; Top = top;
        }
    }

    /// <summary>What the hinter needs to know about a face, measured once when the face is loaded:
    /// its blue zones and the width its stems are drawn at.</summary>
    internal sealed class HintMetrics
    {
        public float UnitsPerEm;
        public HintZone[] Zones = Array.Empty<HintZone>();
        /// <summary>The width of an upright stem in font units, 0 when it could not be measured.
        /// Used so that every stem in a face rounds to the SAME number of pixels -- rounding each
        /// one on its own merits gives an 'm' whose three stems are not the same weight.</summary>
        public float StandardStemX;
        /// <summary>The same for a horizontal bar.</summary>
        public float StandardStemY;

        public bool IsUsable => UnitsPerEm > 0f;
    }

    internal static class GlyphHinter
    {
        // Tolerances, all as a fraction of the em so they mean the same thing on any face.
        private const float FlatFraction = 1f / 64f;    // how far an edge may wander and still be flat
        private const float MinLengthFraction = 1f / 32f;   // how long a run must be to count as an edge
        private const float EdgeFraction = 1f / 48f;    // how close two segments must be to be one edge
        private const float MaxStemFraction = 1f / 3f;  // no stem is wider than a third of the em

        /// <summary>How near an edge must land to a blue zone, in PIXELS, to be snapped to it. Three
        /// quarters of a pixel: nearer than that and moving it is invisible, further and it is a
        /// different feature.</summary>
        /// <summary>How far a measured stem may sit from the face's own and still be taken for it.</summary>
        private const float SnapTolerance = 0.75f;

        /// <summary>Weighs how far a candidate runs alongside an edge against how far away it is,
        /// in font units for a 2048-unit em. FreeType's constant.</summary>
        private const float LinkLengthScore = 6000f;

        /// <summary>Two edges that barely run alongside each other are not the sides of a stem.</summary>
        private const float MinLinkOverlap = 8f;

        private const float BlueSnapPixels = 0.75f;

        /// <summary>To the nearest pixel, with a half going UP.
        /// <para>Not MathF.Round, which rounds a half to the EVEN neighbour. A grid fitter lands on
        /// exact halves constantly -- font units are integers and the scale is a ratio of small
        /// numbers -- and on those the two rules disagree. Segoe UI's '0' has its left stem at
        /// exactly 0.50 pixels at nine point: GDI puts it in the second column, MathF.Round puts it
        /// in the first, and the '0' comes out a column wider than Windows draws it.</para></summary>
        private static float Round(float pixels) => MathF.Floor(pixels + 0.5f);

        /// <summary>What the fitting decided for one glyph, as text: every edge it found, where that
        /// edge was, and where it put it. For working on the fitting itself -- reading the result off
        /// a picture of the glyph is guesswork, and guessing is how this took as long as it did.
        /// </summary>
        public static string Explain(List<(Vector2[] Pts, bool[] On)> contours, HintMetrics metrics,
                                     float ppem, bool horizontal)
        {
            var text = new System.Text.StringBuilder();
            if (contours.Count == 0 || metrics == null || !metrics.IsUsable) return "nothing to fit";
            float scale = ppem / metrics.UnitsPerEm;
            float em = metrics.UnitsPerEm;

            List<Segment> segments = FindSegments(contours, em * FlatFraction, em * MinLengthFraction, horizontal);
            List<Edge> edges = BuildEdges(segments, EdgeFuzz(em, scale), contours);
            LinkStems(edges, em * MaxStemFraction, em);
            FitEdges(edges, metrics, scale, horizontal);

            text.AppendLine($"{(horizontal ? "x" : "y")} axis: {segments.Count} segments, {edges.Count} edges");
            for (int i = 0; i < edges.Count; i++)
            {
                Edge e = edges[i];
                string link = e.Link >= 0
                    ? $"stem {MathF.Abs(e.Pos - edges[e.Link].Pos) * scale:0.00}px with #{e.Link}"
                    : "no stem";
                text.AppendLine($"  #{i} dir={e.Dir,2} len={e.Length,6:0} at {e.Pos,7:0.0} "
                                + $"({e.Pos * scale,6:0.00}px) -> {e.Fitted,5:0.00}px  {link}");
            }
            return text.ToString();
        }

        /// <summary>Fit a glyph, given in font units with y UP and the baseline at zero. The result is
        /// written back in PIXELS, still y-up, ready to be flipped and turned into a path.</summary>
        public static void Fit(List<(Vector2[] Pts, bool[] On)> contours, HintMetrics metrics, float ppem)
        {
            if (contours.Count == 0 || metrics == null || !metrics.IsUsable || ppem <= 0f)
                return;

            float scale = ppem / metrics.UnitsPerEm;

            // Both axes are analysed on the ORIGINAL font-unit outline, so the second pass is not
            // reading coordinates the first has already moved.
            float[][] fittedY = FitAxis(contours, metrics, scale, horizontal: false);
            float[][] fittedX = FitAxis(contours, metrics, scale, horizontal: true);

            for (int c = 0; c < contours.Count; c++)
            {
                Vector2[] pts = contours[c].Pts;
                for (int i = 0; i < pts.Length; i++)
                    pts[i] = new Vector2(fittedX[c][i], fittedY[c][i]);
            }
        }

        /// <summary>Fit one axis and give back, for every point, where it now sits in pixels.</summary>
        private static float[][] FitAxis(List<(Vector2[] Pts, bool[] On)> contours, HintMetrics metrics,
                                         float scale, bool horizontal)
        {
            float em = metrics.UnitsPerEm;
            List<Segment> segments = FindSegments(contours, em * FlatFraction, em * MinLengthFraction, horizontal);
            List<Edge> edges = BuildEdges(segments, EdgeFuzz(em, scale), contours);

            var result = new float[contours.Count][];
            for (int c = 0; c < contours.Count; c++)
                result[c] = new float[contours[c].Pts.Length];

            if (edges.Count == 0)
            {
                for (int c = 0; c < contours.Count; c++)
                {
                    Vector2[] pts = contours[c].Pts;
                    for (int i = 0; i < pts.Length; i++)
                        result[c][i] = Coord(pts[i], horizontal) * scale;
                }
                return result;
            }

            LinkStems(edges, em * MaxStemFraction, em);
            FitEdges(edges, metrics, scale, horizontal);

            // Which points an edge owns, so they can be MOVED rather than interpolated. Everything
            // else is carried between them, per contour, which is what keeps a curve a curve: a map
            // laid across the whole axis moves the far side of the glyph with the near one.
            var touched = new bool[contours.Count][];
            for (int c = 0; c < contours.Count; c++)
                touched[c] = new bool[contours[c].Pts.Length];

            foreach (Edge e in edges)
                foreach ((int Contour, int Point) p in e.Points)
                {
                    touched[p.Contour][p.Point] = true;
                    result[p.Contour][p.Point] = e.Fitted;
                }

            for (int c = 0; c < contours.Count; c++)
                Interpolate(contours[c].Pts, touched[c], result[c], scale, horizontal);

            return result;
        }

        /// <summary>Carry the points an edge does not own between the ones it does -- FreeType calls
        /// this interpolating the untouched points, and it is why a fitted 'o' is still round.
        /// <para>A point between two moved ones travels the same fraction of the way across the gap it
        /// started at. A point outside every moved one keeps its distance from the nearest.</para>
        /// </summary>
        private static void Interpolate(Vector2[] pts, bool[] touched, float[] result, float scale,
                                        bool horizontal)
        {
            int n = pts.Length;
            int first = -1;
            for (int i = 0; i < n; i++) if (touched[i]) { first = i; break; }
            if (first < 0)
            {
                for (int i = 0; i < n; i++) result[i] = Coord(pts[i], horizontal) * scale;
                return;
            }

            int a = first;
            for (int step = 0; step < n; step++)
            {
                int i = (first + step) % n;
                if (touched[i]) { a = i; continue; }

                // The next moved point, going forward round the contour.
                int b = a;
                for (int k = 1; k <= n; k++)
                {
                    int j = (i + k) % n;
                    if (!touched[j]) continue;
                    b = j;
                    break;
                }

                float va = Coord(pts[a], horizontal), vb = Coord(pts[b], horizontal);
                float v = Coord(pts[i], horizontal);
                float fa = result[a], fb = result[b];

                float lo = MathF.Min(va, vb), hi = MathF.Max(va, vb);
                if (v <= lo)
                {
                    float anchor = va <= vb ? fa : fb;
                    result[i] = anchor + (v - lo) * scale;
                }
                else if (v >= hi)
                {
                    float anchor = va >= vb ? fa : fb;
                    result[i] = anchor + (v - hi) * scale;
                }
                else if (MathF.Abs(vb - va) <= 1e-6f)
                {
                    result[i] = fa;
                }
                else
                {
                    float t = (v - va) / (vb - va);
                    result[i] = fa + t * (fb - fa);
                }
            }
        }

        private static float Coord(Vector2 p, bool horizontal) => horizontal ? p.X : p.Y;

        /// <summary>How close two segments must be to count as one edge: whichever is the larger of
        /// the design tolerance and half a pixel at the size being drawn.</summary>
        private static float EdgeFuzz(float em, float scale)
            => MathF.Max(em * EdgeFraction, scale > 1e-6f ? 0.5f / scale : 0f);

        // ---- segments and edges ------------------------------------------------------------------

        /// <summary>A straight run of the outline: it moves ALONG the axis being fitted and stays put
        /// across it. <see cref="Dir"/> is which way it was walked, which is how the two sides of a
        /// stem are told apart -- they are walked in opposite directions.</summary>
        private readonly struct Segment
        {
            public readonly float Pos;      // font units, across the axis
            public readonly float Length;   // font units, along it
            public readonly float Min, Max; // where along the axis it runs, so overlaps can be found
            public readonly int Dir;
            public readonly int Contour;    // where its points are, so the edge can move them
            public readonly int First;      // the first of them
            public readonly int Count;      // how many, walking forward round the contour

            public Segment(float pos, float length, float min, float max,
                           int dir, int contour, int first, int count)
            {
                Pos = pos; Length = length; Min = min; Max = max;
                Dir = dir; Contour = contour; First = first; Count = count;
            }
        }

        private sealed class Edge
        {
            public float Pos;               // font units
            public float Length;            // total, for choosing which edge to trust
            public float Min = float.MaxValue, Max = float.MinValue;   // its stretch along the axis
            public int Dir;
            public int Link = -1;           // the other side of this stem, or -1
            public float Fitted;            // pixels
            public bool Done;
            public readonly List<(int Contour, int Point)> Points = new();
        }

        /// <summary>Find the outline's edges: RUNS of it that travel along the axis being fitted.
        /// <para>The test is which way the outline is GOING, not how flat it is. A stem's side is flat
        /// and passes either test, but the side of an 'o' is a curve -- no two of its points share an
        /// x, so a flatness test finds nothing there at all. That is why round letters were left
        /// unfitted and soft while straight-sided ones came out crisp and black, and a page of that
        /// reads as broken.</para>
        /// <para>A run's position is the edge of its INK, not the middle of its arc: the leftmost
        /// point of an 'o's left side. Which end that is follows from the direction of travel and the
        /// winding -- a TrueType outer contour is drawn clockwise with y up, so the ink always lies
        /// ninety degrees clockwise from the way the outline is going.</para></summary>
        private static List<Segment> FindSegments(List<(Vector2[] Pts, bool[] On)> contours,
                                                  float flatMax, float minLength, bool horizontal)
        {
            var segments = new List<Segment>();
            for (int c = 0; c < contours.Count; c++)
            {
                Vector2[] pts = contours[c].Pts;
                int n = pts.Length;
                if (n < 3) continue;

                // Which way each step goes along the axis: +1, -1, or 0 for one that mostly crosses it
                // and so belongs to no edge -- the top of an 'n's arch, the diagonal of an 'A'.
                var step = new int[n];
                for (int i = 0; i < n; i++)
                {
                    Vector2 a = pts[i], b = pts[(i + 1) % n];
                    float across = horizontal ? b.X - a.X : b.Y - a.Y;
                    float along = horizontal ? b.Y - a.Y : b.X - a.X;
                    // Twelve times as far along as across -- FreeType's heuristic, and it has to be
                    // about that severe. Anything gentler takes in steps where the outline is merely
                    // LEANING the right way, and a lean is not an edge:
                    //   * at 2:1 the diagonal of an 'A' qualifies (it rises about 2.2 for every 1 it
                    //     crosses), so the whole side was taken for one upright edge, every point on
                    //     it was moved onto that edge's pixel, and the letter came out a solid box;
                    //   * gentler still and the turn where a stem runs into the bowl of a 'u' joins
                    //     the run, carrying its extreme past the stem's real side, and every stem
                    //     measures wider than it is.
                    // A curve is not excluded by this: where a round side reaches its extreme its
                    // tangent is exactly along the axis, so the step across is zero there.
                    step[i] = MathF.Abs(along) > 12f * MathF.Abs(across) ? (along >= 0f ? 1 : -1) : 0;
                }

                // Start where the direction changes, so a run that wraps the end of the array is not
                // cut in two and counted as two shorter edges.
                int start = 0;
                for (int i = 0; i < n; i++)
                    if (step[i] != step[(i - 1 + n) % n]) { start = i; break; }

                int at = 0;
                while (at < n)
                {
                    int first = (start + at) % n;
                    int dir = step[first];
                    int steps = 1;
                    while (steps < n && step[(start + at + steps) % n] == dir) steps++;

                    if (dir != 0)
                    {
                        float length = 0f, lo = float.MaxValue, hi = float.MinValue;
                        float from = float.MaxValue, to = float.MinValue;
                        for (int k = 0; k <= steps; k++)
                        {
                            Vector2 p = pts[(first + k) % n];
                            float pos = horizontal ? p.X : p.Y;
                            float along = horizontal ? p.Y : p.X;
                            lo = MathF.Min(lo, pos);
                            hi = MathF.Max(hi, pos);
                            from = MathF.Min(from, along);
                            to = MathF.Max(to, along);
                            if (k == steps) break;
                            Vector2 q = pts[(first + k + 1) % n];
                            length += MathF.Abs(horizontal ? q.Y - p.Y : q.X - p.X);
                        }

                        if (length >= minLength)
                        {
                            // A straight side gives the same answer either way; a curved one does not,
                            // and there the ink stops at the extreme, not at the average.
                            bool inkBelow = horizontal ? dir > 0 : dir < 0;
                            float edge = hi - lo <= flatMax ? (lo + hi) * 0.5f : (inkBelow ? lo : hi);
                            segments.Add(new Segment(edge, length, from, to, dir, c, first, steps + 1));
                        }
                    }
                    at += steps;
                }
            }
            return segments;
        }

        private static void AddPoints(Edge edge, Segment segment, List<(Vector2[] Pts, bool[] On)> contours)
        {
            int n = contours[segment.Contour].Pts.Length;
            for (int k = 0; k < segment.Count; k++)
                edge.Points.Add((segment.Contour, (segment.First + k) % n));
        }

        /// <param name="edgeFuzz">How close two segments must be to be one edge, in font units.
        /// <para>This has to account for the SIZE being drawn as well as the design. A 'u' has two
        /// left-facing edges down its right side -- the stem and the tail that leaves it -- half a
        /// pixel apart at nine point. Left as two edges they round to two different pixels and the
        /// stem comes out twice as wide as the face draws it. Anything that lands within half a pixel
        /// of something else is the same feature at this size, whatever it is in the design.</para>
        /// </param>
        private static List<Edge> BuildEdges(List<Segment> segments, float edgeFuzz,
                                             List<(Vector2[] Pts, bool[] On)> contours)
        {
            var edges = new List<Edge>();
            // Each direction is clustered on its own: the two sides of a hairline stem are close
            // enough to merge by position alone, and merging them would lose the stem.
            for (int pass = 0; pass < 2; pass++)
            {
                int dir = pass == 0 ? 1 : -1;
                var mine = new List<Segment>();
                foreach (Segment s in segments)
                    if (s.Dir == dir) mine.Add(s);
                mine.Sort((x, y) => x.Pos.CompareTo(y.Pos));

                Edge? current = null;
                float weighted = 0f;
                foreach (Segment s in mine)
                {
                    if (current != null && s.Pos - current.Pos <= edgeFuzz)
                    {
                        weighted += s.Pos * s.Length;
                        current.Length += s.Length;
                        current.Pos = weighted / current.Length;
                        current.Min = MathF.Min(current.Min, s.Min);
                        current.Max = MathF.Max(current.Max, s.Max);
                        AddPoints(current, s, contours);
                        continue;
                    }
                    current = new Edge { Pos = s.Pos, Length = s.Length, Dir = dir, Min = s.Min, Max = s.Max };
                    AddPoints(current, s, contours);
                    weighted = s.Pos * s.Length;
                    edges.Add(current);
                }
            }
            edges.Sort((x, y) => x.Pos.CompareTo(y.Pos));
            return edges;
        }

        /// <summary>Pair each edge with the one facing the other way that it is most likely to be a
        /// stem with, and the gap between them is the width that has to come out a whole number of
        /// pixels.
        /// <para>Not simply the NEAREST such edge. Two sides of a stem run alongside each other, so
        /// how far the pair runs together counts as much as how far apart they are: a curve broken
        /// into two pieces leaves a short stray edge just inside the real one, and by distance alone
        /// that stray wins. In the '6' of Segoe UI the counter's right side did exactly that -- it
        /// paired with a fragment half a pixel away instead of the outer edge one pixel away, was
        /// given the face's whole stem width to sit that fragment's distance from, and ended up a
        /// pixel INSIDE the counter, drawing the bowl twice as thick as the face does.</para>
        /// <para>The score is FreeType's: the gap, plus a constant divided by how far the two run
        /// alongside each other, so a long companion beats a slightly closer short one.</para></summary>
        private static void LinkStems(List<Edge> edges, float maxStem, float em)
        {
            float lenScore = LinkLengthScore * em / 2048f;
            float minOverlap = MinLinkOverlap * em / 2048f;

            for (int i = 0; i < edges.Count; i++)
            {
                float best = float.MaxValue;
                int bestIndex = -1;
                for (int j = 0; j < edges.Count; j++)
                {
                    if (j == i || edges[j].Dir == edges[i].Dir) continue;
                    float d = MathF.Abs(edges[j].Pos - edges[i].Pos);
                    if (d <= 0f || d > maxStem) continue;

                    float overlap = MathF.Min(edges[i].Max, edges[j].Max)
                                    - MathF.Max(edges[i].Min, edges[j].Min);
                    if (overlap < minOverlap) continue;

                    float score = d + lenScore / overlap;
                    if (score >= best) continue;
                    best = score;
                    bestIndex = j;
                }
                edges[i].Link = bestIndex;
            }

            // Only mutual pairs are stems. A single edge that happens to have something across from
            // it -- the side of an 'o' against the far side of the same 'o' -- is not one.
            for (int i = 0; i < edges.Count; i++)
            {
                int link = edges[i].Link;
                if (link >= 0 && edges[link].Link != i)
                    edges[i].Link = -1;
            }
        }

        // ---- fitting -----------------------------------------------------------------------------

        private static void FitEdges(List<Edge> edges, HintMetrics metrics, float scale, bool horizontal)
        {
            // The lines of the alphabet first: everything else is measured from them, so they are
            // what must land on a whole pixel. Only the vertical axis has them.
            if (!horizontal && metrics.Zones.Length > 0)
                SnapToZones(edges, metrics.Zones, scale);

            float standard = horizontal ? metrics.StandardStemX : metrics.StandardStemY;

            // ONE edge is placed on its own merits and everything else hangs off it. Which one
            // matters: the glyph is stretched or squeezed by however far that edge moved.
            //
            // The LONGEST, and it is worth saying why not FreeType's rule, which is the first stem in
            // position order with every later stem placed at a rounded distance from it. Measured
            // against GDI over the alphabet, the digits and the marks, that rule is better on digits
            // and clearly worse on capitals -- 'NOPQRSTUVWXYZ' went from one pixel's disagreement
            // with Windows to sixteen -- because placing a stem relative to the anchor adds the
            // anchor's own error to its own, and a capital's stems are far enough apart for the two
            // to reach a whole pixel. Rounding each stem on its own merits keeps the error bounded.
            var order = new List<int>();
            for (int i = 0; i < edges.Count; i++) order.Add(i);
            order.Sort((a, b) => edges[b].Length.CompareTo(edges[a].Length));

            foreach (int i in order)
            {
                Edge e = edges[i];
                if (e.Done) continue;

                int link = e.Link;
                if (link >= 0 && edges[link].Done)
                {
                    // The other side of this stem is already placed: put this one a whole number of
                    // pixels away, so the stem covers columns rather than straddling them.
                    e.Fitted = Across(e, edges[link], scale, standard);
                }
                else
                {
                    e.Fitted = Round(e.Pos * scale);
                }
                e.Done = true;
            }

            // Nothing may overtake anything else: the fitting moves edges by up to half a pixel and
            // two that started close could otherwise swap, which turns the glyph inside out.
            for (int i = 1; i < edges.Count; i++)
                if (edges[i].Fitted < edges[i - 1].Fitted)
                    edges[i].Fitted = edges[i - 1].Fitted;
        }

        /// <summary>Where the other side of a stem goes, once this side is placed: a whole number of
        /// pixels away, on the side it was already on.</summary>
        private static float Across(Edge edge, Edge placed, float scale, float standard)
        {
            float width = MathF.Abs(edge.Pos - placed.Pos) * scale;
            float fitted = FitStemWidth(width, standard * scale);
            return edge.Pos > placed.Pos ? placed.Fitted + fitted : placed.Fitted - fitted;
        }

        private static void SnapToZones(List<Edge> edges, HintZone[] zones, float scale)
        {
            foreach (HintZone zone in zones)
            {
                float flatFit = FitZone(zone.Flat * scale);
                // An overshoot smaller than half a pixel cannot be drawn; the round letters join the
                // flat ones rather than being rounded to a pixel of their own.
                float overshoot = MathF.Abs(zone.Round - zone.Flat) * scale;
                float roundFit = overshoot < 0.5f ? flatFit : FitZone(zone.Round * scale);

                foreach (Edge e in edges)
                {
                    if (e.Done) continue;

                    // The zone may only take an edge that FACES it: the top of the letters catches the
                    // tops of strokes, the baseline their feet. Without this the baseline caught BOTH
                    // edges of an 'o's bottom stroke -- its outside at 0.0 and its inside at 0.7, both
                    // inside the snapping window -- and put them on the same pixel, leaving the letter
                    // with no bottom at all. That is what "the round letters look broken" was.
                    // (Which way an edge faces comes out of the winding: with y up and a clockwise
                    // outer contour, a run travelling +x has its ink below it, so it is a stroke's
                    // top.)
                    bool facesUp = e.Dir > 0;
                    if (zone.Top != facesUp) continue;

                    float here = e.Pos * scale;
                    if (MathF.Abs(here - zone.Flat * scale) <= BlueSnapPixels)
                    {
                        e.Fitted = flatFit;
                        e.Done = true;
                    }
                    else if (MathF.Abs(here - zone.Round * scale) <= BlueSnapPixels)
                    {
                        e.Fitted = roundFit;
                        e.Done = true;
                    }
                }
            }
        }

        /// <summary>Where a blue zone lands, which is NOT simply the nearest whole pixel: it is
        /// biased five eighths of a pixel upwards, so a line only two fifths of the way past a pixel
        /// still reaches it. Both FreeType and the hints Windows runs do this, and letters want it --
        /// a cap height that rounds down loses a whole row from every capital on the page.
        /// <para>Measured: the regular face's cap height comes to 8.59 pixels at nine point and rounds
        /// to 9 either way, which is why the regular text looked right. The BOLD face's comes to 8.40
        /// -- Windows draws it 9 tall and rounding to nearest gave 8, so every bold capital and digit
        /// was a pixel short of its neighbours.</para></summary>
        private static float FitZone(float scaled) => MathF.Floor(scaled + 0.625f);

        /// <summary>A stem's width in whole pixels. Never nothing -- a stem that rounded to zero would
        /// leave a letter with a hole where its upright should be -- and a face's stems all come out
        /// the same width, which is what <paramref name="standard"/> is for.</summary>
        private static float FitStemWidth(float width, float standard)
        {
            // The FACE's width, not this stem's. A text face draws all its uprights the same, and the
            // differences that survive measurement at these sizes are noise -- an edge found a little
            // wide because the run that found it carried on into the curve the stem turns into. Left
            // to itself each stem rounds on its own merits and a word comes out with some uprights a
            // pixel wide and others two, which reads worse than uniformly soft text does.
            //
            // The correction is only for noise, so it reaches no further than the noise does: a stem
            // is taken to be the face's own if it lands within three quarters of a pixel of where the
            // face's stem rounds to. A stroke outside that is not a badly measured upright, it is a
            // different stroke -- every face draws the side of a bowl thinner than its uprights, and
            // in a bold face that difference is most of a pixel. Pulling one up to the full stem
            // width made the bold '6' two pixels round the outside of its bowl where it should be
            // one, and pushed the outer edge a whole pixel right, into the next letter's space.
            if (standard > 0f)
            {
                float rounded = Round(standard);
                bool close = width >= standard ? width < rounded + SnapTolerance
                                               : width > rounded - SnapTolerance;
                if (close)
                    width = standard;
            }
            if (width < 1.25f)
                return 1f;
            return Round(width);
        }

        // ---- measuring a face --------------------------------------------------------------------

        /// <summary>The characters whose outlines say where the lines of the alphabet are. Flat-topped
        /// letters give the reference, round ones the overshoot.</summary>
        internal const string ZoneFlatXHeight = "xzuvw";
        internal const string ZoneRoundXHeight = "oesc";
        internal const string ZoneFlatCapHeight = "HEZLTFI";
        internal const string ZoneRoundCapHeight = "OQCGS";
        internal const string ZoneFlatBaseline = "HEZLTxzuvw";
        internal const string ZoneRoundBaseline = "oescOQCGS";
        internal const string ZoneDescender = "pqgjy";
        internal const string ZoneAscender = "bdfhkl";

        /// <summary>The glyphs whose stems are measured for the face's standard width. 'o' and 'n'
        /// between them give an upright and a bar in nearly every latin face.</summary>
        internal const string StemProbes = "onlH";

        /// <summary>Build the metrics from a face, given something that can hand back a character's
        /// outline in font units. Everything is measured from the outlines themselves rather than
        /// from the tables: OS/2 carries an x-height and a cap height, but plenty of faces leave them
        /// at zero or wrong, and the whole point of this is to land on the ink.</summary>
        public static HintMetrics Measure(float unitsPerEm, Func<char, List<(Vector2[] Pts, bool[] On)>?> outlineOf)
        {
            var metrics = new HintMetrics { UnitsPerEm = unitsPerEm };
            if (unitsPerEm <= 0f || outlineOf == null)
                return metrics;

            var zones = new List<HintZone>();
            AddZone(zones, outlineOf, ZoneFlatXHeight, ZoneRoundXHeight, top: true);
            AddZone(zones, outlineOf, ZoneFlatCapHeight, ZoneRoundCapHeight, top: true);
            AddZone(zones, outlineOf, ZoneFlatBaseline, ZoneRoundBaseline, top: false);
            AddZone(zones, outlineOf, ZoneAscender, ZoneAscender, top: true);
            AddZone(zones, outlineOf, ZoneDescender, ZoneDescender, top: false);
            metrics.Zones = zones.ToArray();

            metrics.StandardStemX = MeasureStem(outlineOf, unitsPerEm, horizontal: true);
            metrics.StandardStemY = MeasureStem(outlineOf, unitsPerEm, horizontal: false);
            return metrics;
        }

        private static void AddZone(List<HintZone> zones, Func<char, List<(Vector2[] Pts, bool[] On)>?> outlineOf,
                                    string flatChars, string roundChars, bool top)
        {
            float flat = Extreme(outlineOf, flatChars, top);
            if (float.IsNaN(flat))
                return;
            float round = Extreme(outlineOf, roundChars, top);
            zones.Add(new HintZone(flat, float.IsNaN(round) ? flat : round, top));
        }

        /// <summary>The highest (or lowest) point any of these characters reaches -- the line they are
        /// drawn to. Taking the extreme of several is what makes this survive a face where one of
        /// them is missing or oddly drawn.</summary>
        private static float Extreme(Func<char, List<(Vector2[] Pts, bool[] On)>?> outlineOf, string chars, bool top)
        {
            var values = new List<float>();
            foreach (char c in chars)
            {
                List<(Vector2[] Pts, bool[] On)>? contours = outlineOf(c);
                if (contours == null || contours.Count == 0) continue;
                float best = top ? float.MinValue : float.MaxValue;
                foreach ((Vector2[] pts, bool[] on) in contours)
                    for (int i = 0; i < pts.Length; i++)
                    {
                        // On-curve points only: a control point is not on the letter. The two that
                        // carry the top of an 'o' stand a third of the curve's height above its
                        // apex, and taking them for the x-height put that line above the ink.
                        if (on != null && i < on.Length && !on[i]) continue;
                        best = top ? MathF.Max(best, pts[i].Y) : MathF.Min(best, pts[i].Y);
                    }
                if (best != float.MinValue && best != float.MaxValue)
                    values.Add(best);
            }
            if (values.Count == 0)
                return float.NaN;
            values.Sort();
            return values[values.Count / 2];      // the middle one, so one odd letter cannot set the line
        }

        /// <summary>The width a stem is drawn at in this face, taken as the middle of the widths found
        /// in a few letters that have one.</summary>
        private static float MeasureStem(Func<char, List<(Vector2[] Pts, bool[] On)>?> outlineOf,
                                         float unitsPerEm, bool horizontal)
        {
            float flatMax = unitsPerEm * FlatFraction;
            float minLength = unitsPerEm * MinLengthFraction;
            float edgeFuzz = EdgeFuzz(unitsPerEm, 12f / unitsPerEm);
            float maxStem = unitsPerEm * MaxStemFraction;

            var widths = new List<float>();
            foreach (char c in StemProbes)
            {
                List<(Vector2[] Pts, bool[] On)>? contours = outlineOf(c);
                if (contours == null || contours.Count == 0) continue;
                List<Segment> segments = FindSegments(contours, flatMax, minLength, horizontal);
                List<Edge> edges = BuildEdges(segments, edgeFuzz, contours);
                LinkStems(edges, maxStem, unitsPerEm);
                for (int i = 0; i < edges.Count; i++)
                {
                    int link = edges[i].Link;
                    if (link > i)
                        widths.Add(MathF.Abs(edges[link].Pos - edges[i].Pos));
                }
            }
            if (widths.Count == 0)
                return 0f;
            widths.Sort();
            return widths[widths.Count / 2];
        }
    }
}
