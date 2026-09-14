// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// CPU coverage rasterizer for arbitrary filled paths -- the cross-platform
// analog of milcore's software rasterizer (WpfGfx/core/sw) and the same
// technique production 2D engines (FreeType smooth, Skia analytic AA) use for
// complex paths. Figures (with line and Bézier segments) are flattened to
// polylines and filled per the fill rule into a single-channel coverage mask,
// which the renderer uploads as an R8 texture and composites on the GPU.
//
// Anti-aliasing: exact horizontal span coverage, supersampled 4x vertically.
// This handles concave, self-intersecting and multi-contour paths and both the
// non-zero and even-odd fill rules.
//

using System;
using System.Collections.Generic;
using System.Numerics;

namespace Microsoft.Wpf.Interop.WebGpu.Composition
{
    /// <summary>An 8-bit coverage mask placed at a local-space origin.</summary>
    internal readonly struct CoverageMask
    {
        public readonly byte[] Coverage;
        public readonly int Width;
        public readonly int Height;
        public readonly float OriginX;   // local-space position of the mask's top-left
        public readonly float OriginY;

        public CoverageMask(byte[] coverage, int width, int height, float originX, float originY)
        {
            Coverage = coverage; Width = width; Height = height; OriginX = originX; OriginY = originY;
        }

        public bool IsEmpty => Width <= 0 || Height <= 0;
    }

    internal static class PathRasterizer
    {
        private const int VerticalSamples = 4;       // subsamples per pixel row
        private readonly struct Edge
        {
            public readonly float X0, Y0, X1, Y1;
            public Edge(Vector2 a, Vector2 b) { X0 = a.X; Y0 = a.Y; X1 = b.X; Y1 = b.Y; }
        }

        /// <summary>
        /// Flattens the path to non-horizontal line edges appended to <paramref name="edges"/>
        /// as (x0, y0, x1, y1) quadruples, and reports the point bounds. Returns the edge
        /// count. This is the shared front half of both the CPU scanline fill below and the
        /// GPU coverage rasterizer (WgpuSceneRenderer fs_coverage), which evaluates the same
        /// 4x-vertical-subsample exact-horizontal coverage per pixel in a fragment shader.
        /// </summary>
        public static int FlattenToEdges(PathGeometry path, List<float> edges,
            out float minX, out float minY, out float maxX, out float maxY,
            float tolerance = 0f)
        {
            minX = float.MaxValue; minY = float.MaxValue; maxX = float.MinValue; maxY = float.MinValue;
            List<List<Vector2>> contours = Flatten(path, tolerance);
            int count = 0;
            foreach (List<Vector2> c in contours)
            {
                for (int i = 0; i < c.Count; i++)
                {
                    Vector2 a = c[i];
                    Vector2 b = c[(i + 1) % c.Count]; // implicitly closed for fill
                    minX = MathF.Min(minX, a.X); minY = MathF.Min(minY, a.Y);
                    maxX = MathF.Max(maxX, a.X); maxY = MathF.Max(maxY, a.Y);
                    if (a.Y == b.Y) continue;
                    edges.Add(a.X); edges.Add(a.Y); edges.Add(b.X); edges.Add(b.Y);
                    count++;
                }
            }
            return count;
        }

        // ---- GPU coverage: path as quadratic Bézier segments (no CPU flattening) ----


        /// <summary>
        /// Emits the path's outline as CUBIC Bézier segments (8 floats each: p0, c1, c2, p1)
        /// into <paramref name="segs"/>, and reports tight point bounds. Returns the count.
        ///
        /// Unlike the quadratic form this replaced, the conversion is EXACT and needs no
        /// tolerance: a line and a quadratic both degree-elevate to a cubic with no error, and
        /// a cubic is carried through unchanged. The only subdivision is at y-extrema, which
        /// the coverage shader requires for its monotone crossing test -- not an accuracy
        /// choice. Approximating a cubic by several quadratics (what the GPU path used to do)
        /// cost 6-12x more segments for a curved shape, and fs_coverage loops over every
        /// segment per fragment per subsample row, so that count is its inner-loop bound.
        /// </summary>
        public static int SegmentsToCubics(PathGeometry path, List<float> segs,
            out float minX, out float minY, out float maxX, out float maxY)
        {
            minX = float.MaxValue; minY = float.MaxValue; maxX = float.MinValue; maxY = float.MinValue;
            int count = 0;
            foreach (PathFigure figure in path.Figures)
            {
                Vector2 start = figure.Start;
                Vector2 cur = start;
                foreach (PathSegment seg in figure.Segments)
                {
                    switch (seg)
                    {
                        case LineSegment l:
                            count += EmitLineAsCubic(segs, cur, l.Point, ref minX, ref minY, ref maxX, ref maxY);
                            cur = l.Point;
                            break;
                        case QuadraticBezierSegment q:
                            // Exact degree elevation: (p0, (p0+2c)/3, (2c+p1)/3, p1).
                            count += EmitCubicMonotone(segs, cur, (cur + 2f * q.Control) / 3f,
                                (2f * q.Control + q.Point) / 3f, q.Point, ref minX, ref minY, ref maxX, ref maxY);
                            cur = q.Point;
                            break;
                        case CubicBezierSegment cb:
                            count += EmitCubicMonotone(segs, cur, cb.Control1, cb.Control2, cb.Point,
                                ref minX, ref minY, ref maxX, ref maxY);
                            cur = cb.Point;
                            break;
                    }
                }
                if (cur != start)   // implicitly close the figure for filling
                    count += EmitLineAsCubic(segs, cur, start, ref minX, ref minY, ref maxX, ref maxY);
            }
            return count;
        }

        private static int EmitLineAsCubic(List<float> segs, Vector2 a, Vector2 b,
            ref float minX, ref float minY, ref float maxX, ref float maxY)
        {
            Vector2 d = (b - a) / 3f;
            // A line is y-monotone already, so it never needs splitting.
            EmitCubicRaw(segs, a, a + d, b - d, b, ref minX, ref minY, ref maxX, ref maxY);
            return 1;
        }

        // Splits a cubic at its y-extrema so every emitted piece is y-MONOTONE, which
        // fs_coverage relies on: it brackets exactly one crossing per straddling segment and
        // solves for it, so a piece that turned around in y would be miscounted.
        private static int EmitCubicMonotone(List<float> segs, Vector2 p0, Vector2 c1, Vector2 c2, Vector2 p1,
            ref float minX, ref float minY, ref float maxX, ref float maxY)
        {
            // y'(t)/3 in power form: qa*t^2 + qb*t + qc, from the Bernstein differences.
            float dA = c1.Y - p0.Y, dB = c2.Y - c1.Y, dC = p1.Y - c2.Y;
            Span<float> ts = stackalloc float[2];
            int nt = SolveQuadraticInUnit(dA - 2f * dB + dC, 2f * (dB - dA), dA, ts);
            if (nt == 2 && ts[0] > ts[1]) { (ts[0], ts[1]) = (ts[1], ts[0]); }

            int emitted = 0;
            float prev = 0f;
            for (int i = 0; i < nt; i++)
            {
                float t = ts[i];
                if (t - prev < 1e-5f) continue;                     // degenerate sliver
                SubCubic(p0, c1, c2, p1, prev, t, out Vector2 a0, out Vector2 a1, out Vector2 a2, out Vector2 a3);
                EmitCubicRaw(segs, a0, a1, a2, a3, ref minX, ref minY, ref maxX, ref maxY);
                emitted++;
                prev = t;
            }
            if (1f - prev > 1e-5f || emitted == 0)
            {
                SubCubic(p0, c1, c2, p1, prev, 1f, out Vector2 b0, out Vector2 b1, out Vector2 b2, out Vector2 b3);
                EmitCubicRaw(segs, b0, b1, b2, b3, ref minX, ref minY, ref maxX, ref maxY);
                emitted++;
            }
            return emitted;
        }

        // Real roots of a*t^2 + b*t + c strictly inside (0,1). Uses the cancellation-free
        // q-formula for the same reason fs_coverage does -- a and b are both near zero for
        // the degree-elevated line and quadratic cases that dominate this input.
        private static int SolveQuadraticInUnit(float a, float b, float c, Span<float> outT)
        {
            const float Eps = 1e-4f;
            int n = 0;
            if (MathF.Abs(a) < 1e-9f)
            {
                if (MathF.Abs(b) > 1e-9f)
                {
                    float t = -c / b;
                    if (t > Eps && t < 1f - Eps) outT[n++] = t;
                }
                return n;
            }
            float disc = b * b - 4f * a * c;
            if (disc <= 0f) return 0;
            float sq = MathF.Sqrt(disc);
            float q = -0.5f * (b + (b >= 0f ? sq : -sq));
            foreach (float t in stackalloc float[] { q / a, c / q })
                if (t > Eps && t < 1f - Eps) outT[n++] = t;
            return n;
        }

        // The sub-cubic over [t0, t1], by two de Casteljau splits.
        private static void SubCubic(Vector2 p0, Vector2 c1, Vector2 c2, Vector2 p1, float t0, float t1,
            out Vector2 o0, out Vector2 o1, out Vector2 o2, out Vector2 o3)
        {
            // Right part at t0.
            Vector2 a1 = Vector2.Lerp(p0, c1, t0), a2 = Vector2.Lerp(c1, c2, t0), a3 = Vector2.Lerp(c2, p1, t0);
            Vector2 b1 = Vector2.Lerp(a1, a2, t0), b2 = Vector2.Lerp(a2, a3, t0);
            Vector2 m = Vector2.Lerp(b1, b2, t0);
            // Then the left part of that, at the remapped t1.
            float u = t1 >= 1f ? 1f : (t1 - t0) / MathF.Max(1f - t0, 1e-9f);
            Vector2 d1 = Vector2.Lerp(m, b2, u), d2 = Vector2.Lerp(b2, a3, u), d3 = Vector2.Lerp(a3, p1, u);
            Vector2 e1 = Vector2.Lerp(d1, d2, u), e2 = Vector2.Lerp(d2, d3, u);
            o0 = m; o1 = d1; o2 = e1; o3 = Vector2.Lerp(e1, e2, u);
        }

        private static void EmitCubicRaw(List<float> segs, Vector2 p0, Vector2 c1, Vector2 c2, Vector2 p1,
            ref float minX, ref float minY, ref float maxX, ref float maxY)
        {
            segs.Add(p0.X); segs.Add(p0.Y);
            segs.Add(c1.X); segs.Add(c1.Y);
            segs.Add(c2.X); segs.Add(c2.Y);
            segs.Add(p1.X); segs.Add(p1.Y);
            AccCubicBounds(p0, c1, c2, p1, ref minX, ref minY, ref maxX, ref maxY);
        }

        // Tight bounds of a cubic: endpoints plus the interior axis extrema.
        private static void AccCubicBounds(Vector2 p0, Vector2 c1, Vector2 c2, Vector2 p1,
            ref float minX, ref float minY, ref float maxX, ref float maxY)
        {
            Acc(p0.X, p0.Y, ref minX, ref minY, ref maxX, ref maxY);
            Acc(p1.X, p1.Y, ref minX, ref minY, ref maxX, ref maxY);
            Span<float> ts = stackalloc float[2];
            for (int axis = 0; axis < 2; axis++)
            {
                float v0 = axis == 0 ? p0.X : p0.Y, v1 = axis == 0 ? c1.X : c1.Y;
                float v2 = axis == 0 ? c2.X : c2.Y, v3 = axis == 0 ? p1.X : p1.Y;
                float dA = v1 - v0, dB = v2 - v1, dC = v3 - v2;
                int nt = SolveQuadraticInUnit(dA - 2f * dB + dC, 2f * (dB - dA), dA, ts);
                for (int i = 0; i < nt; i++)
                {
                    Vector2 pt = CubicPoint(p0, c1, c2, p1, ts[i]);
                    Acc(pt.X, pt.Y, ref minX, ref minY, ref maxX, ref maxY);
                }
            }
        }

        /// <summary>
        /// Flattens a path's figures to CENTRE-LINE polyline segments (4 floats each: a.x, a.y,
        /// b.x, b.y) for GPU signed-distance stroking, and reports point bounds (unpadded). Open
        /// figures are not closed; closed figures add the wrap segment. Returns the segment count.
        /// </summary>
        public static int FlattenCenterlineSegments(PathGeometry path, List<float> segs,
            out float minX, out float minY, out float maxX, out float maxY,
            float tolerance = 0f)
        {
            minX = float.MaxValue; minY = float.MaxValue; maxX = float.MinValue; maxY = float.MinValue;
            int count = 0;
            var pts = new List<Vector2>();
            foreach (PathFigure figure in path.Figures)
            {
                pts.Clear();
                pts.Add(figure.Start);
                Vector2 cur = figure.Start;
                foreach (PathSegment seg in figure.Segments)
                    cur = AppendSegment(pts, cur, seg, tolerance);
                if (pts.Count < 2) continue;
                foreach (Vector2 p in pts) Acc(p.X, p.Y, ref minX, ref minY, ref maxX, ref maxY);
                for (int i = 0; i < pts.Count - 1; i++)
                {
                    segs.Add(pts[i].X); segs.Add(pts[i].Y); segs.Add(pts[i + 1].X); segs.Add(pts[i + 1].Y);
                    count++;
                }
                if (figure.Closed && pts[^1] != pts[0])   // wrap segment for closed figures
                {
                    segs.Add(pts[^1].X); segs.Add(pts[^1].Y); segs.Add(pts[0].X); segs.Add(pts[0].Y);
                    count++;
                }
            }
            return count;
        }





        private static void Acc(float x, float y, ref float minX, ref float minY, ref float maxX, ref float maxY)
        {
            minX = MathF.Min(minX, x); minY = MathF.Min(minY, y);
            maxX = MathF.Max(maxX, x); maxY = MathF.Max(maxY, y);
        }


        private static Vector2 CubicPoint(Vector2 p0, Vector2 c1, Vector2 c2, Vector2 p1, float t)
        {
            float u = 1f - t;
            return u * u * u * p0 + 3f * u * u * t * c1 + 3f * u * t * t * c2 + t * t * t * p1;
        }

        private static Vector2 CubicTangent(Vector2 p0, Vector2 c1, Vector2 c2, Vector2 p1, float t)
        {
            float u = 1f - t;
            return 3f * u * u * (c1 - p0) + 6f * u * t * (c2 - c1) + 3f * t * t * (p1 - c2);
        }

        // Intersection of the lines (a0 + s*d0) and (a1 + r*d1); midpoint if near-parallel.
        private static Vector2 TangentIntersect(Vector2 a0, Vector2 d0, Vector2 a1, Vector2 d1)
        {
            float denom = d0.X * d1.Y - d0.Y * d1.X;
            if (MathF.Abs(denom) < 1e-6f)
                return (a0 + a1) * 0.5f;
            float s = ((a1.X - a0.X) * d1.Y - (a1.Y - a0.Y) * d1.X) / denom;
            return a0 + s * d0;
        }

        public static CoverageMask Rasterize(PathGeometry path, float tolerance = 0f)
        {
            List<List<Vector2>> contours = Flatten(path, tolerance);
            if (contours.Count == 0)
                return default;

            // Bounds (+1px padding so anti-aliased edges aren't clipped).
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            int pointCount = 0;
            foreach (List<Vector2> c in contours)
                foreach (Vector2 p in c)
                {
                    minX = MathF.Min(minX, p.X); minY = MathF.Min(minY, p.Y);
                    maxX = MathF.Max(maxX, p.X); maxY = MathF.Max(maxY, p.Y);
                    pointCount++;
                }
            if (pointCount == 0) return default;

            int originX = (int)MathF.Floor(minX) - 1;
            int originY = (int)MathF.Floor(minY) - 1;
            int width = (int)MathF.Ceiling(maxX) + 1 - originX;
            int height = (int)MathF.Ceiling(maxY) + 1 - originY;
            if (width <= 0 || height <= 0) return default;

            byte[] bytes = FillCoverage(contours, path.FillRule, originX, originY, width, height);
            return new CoverageMask(bytes, width, height, originX, originY);
        }

        // ---- subpixel (ClearType) coverage ---------------------------------------------------------
        //
        // A screen's pixel is three lamps in a row, not one dot, and ClearType lights them separately:
        // a stem that covers the left third of a pixel lights that pixel's RED lamp and leaves the
        // other two dark. It buys three times the horizontal resolution and costs a colour fringe,
        // which the filter below is what tames.
        //
        // The coverage this rasterizer already computes is ANALYTIC in x -- exact area per column --
        // so there is nothing to invent: scale the outline three times in x, rasterize into a buffer
        // three times as wide, and every column IS a subpixel's coverage.

        /// <summary>How many samples across a pixel: one per lamp.</summary>
        internal const int SubpixelsPerPixel = 3;

        /// <summary>The filter each lamp's coverage is spread over, so that a stem lighting one lamp
        /// does not read as a coloured line. Essentially a flat average over the pixel's own three
        /// lamps, with a little leaked to the neighbours either side.
        ///
        /// <para>ITS WIDTH IS THE KNOB THAT CONTROLS COLOUR, and it is easy to set too wide. The
        /// classic five-tap [1,2,3,2,1]/9 sat here first: it reaches two subpixels either side, which
        /// is two thirds of a pixel of smearing, and it carried only 0.89 of Windows' colour. Side by
        /// side our text read as GREY where Windows' was crisp.</para>
        ///
        /// <para>Measured over the whole repertoire in all four faces at every size from ten pixels
        /// an em to twenty, against GDI's own ClearType, the trade is monotone -- wider is better
        /// geometry and worse colour:</para>
        /// <code>
        ///   [0,1,1,1,0]      939+32 px wrong   mean 30.7   colour 0.982
        ///   [4,80,88,80,4]       939 px wrong   mean 27.6   colour 0.970   &lt;- here
        ///   [8,77,86,77,8]       916 px wrong   mean 27.4   colour 0.955
        ///   [1,2,3,2,1]          882 px wrong   mean 32.9   colour 0.892
        ///   [0,0,1,0,0]         2058 px wrong   mean  ---   colour 1.112
        /// </code>
        /// <para>RE-SWEPT 2026-08-28 after two of the instruments were corrected, and the answer did
        /// not change -- but the reasoning above did, so here is what actually holds. The colour
        /// column had been measured by summing the GREEN LAMP ALONE, and a subpixel filter does not
        /// conserve one lamp: ink that moves sideways into a neighbour's red or blue vanishes from
        /// the total, and the wider the filter the more vanishes. Weighed across ALL THREE lamps
        /// (TheWholeRepertoire_CarriesAsMuchInkAsWindows), the ink cost of widening nearly
        /// disappears -- mean deviation 1.14%, 1.12%, 1.13%, 1.36%, 2.02% down the list -- so the
        /// 0.982/0.970/0.955/0.892 spread above was mostly an artefact. Structural accuracy, meanwhile,
        /// improves monotonically with width: 598, 574, 559, 521, 1440.</para>
        ///
        /// <para>On those two numbers alone the five-tap [1,2,3,2,1]/9 wins. It is NOT what to use.
        /// Measured against a live stock window -- the authority, and the thing the numbers are proxies
        /// for -- widening makes it WORSE, and steeply: this filter 2,750,289, [8,77,86,77,8]
        /// 2,765,426, [1,2,3,2,1] 2,883,886, with position 365,527 / 376,617 / 471,685. The two
        /// instruments genuinely disagree in direction, because the parity suite's structural count
        /// compares one lamp's coverage while the window compares all three: a wider filter
        /// DESATURATES, which the one-lamp count cannot see and the eye and the window both can.
        /// Do not widen this to chase the structural number.</para>
        ///
        /// <para>A stock stem measured on screen carries the subpixel profile
        /// {0.286, 0.6, 1.0, 0.6, 0.286} -- a three-tap box with a darkening gamma of about 0.88 over
        /// it, not the classic five-tap.</para>
        ///
        /// <para>RE-SWEPT 2026-08-30 against the six-face specimen, after the baseline, advance,
        /// kerning and y-cut-in fixes. The box still wins and the trade is still monotone in width:
        /// [0,1,1,1,0] 2,197,658; [4,80,88,80,4] 2,380,749; [8,77,86,77,8] 2,532,549; [1,2,3,2,1]
        /// 3,343,483; no filter at all 5,279,402. Nothing here changes -- but the numbers it was
        /// last argued from were measured against a renderer that drew two of its six faces two
        /// pixels high, so they were not numbers about the filter.</para>
        ///
        /// <para>IT IS NOW THE BOX, [0,1,1,1,0]/3, and the reason is a COUNT rather than a sweep.
        /// GDI's ClearType output holds exactly seven distinct levels. SEVEN is right and k/6 is
        /// NOT: histogrammed off GDI's own pixels over the whole repertoire at 7 and 8 ppem, the
        /// set is 0, 58, 102, 144, 182, 219, 255 -- steps of 58, 44, 42, 38, 37, 36, a curve baked
        /// into the levels rather than an even ladder (k/6 would be 0, 42, 85, 128, 170, 213, 255).
        /// OURS EMITS THE SAME SEVEN, exactly, which is worth knowing before anyone goes looking
        /// for the residual in the quantiser. Three-valued lamps
        /// through a three-tap box produce exactly seven levels; through any wider filter they
        /// produce many more. So the box is not a tuning choice, it is the filter that makes our
        /// output live in the same value SET as GDI's -- and once the lamps became three-valued
        /// (see SubpixelLevels) the old five-tap had nothing left to win. Measured after that
        /// change: the parity harness calls them a tie (274 against 277 structural, ink 0.89% against
        /// 0.86%), and the live window prefers the box, 2,483,331 against 2,491,739, almost all of it
        /// weight. Gamma was re-swept over it and stays at 1.15.</para>
        ///
        /// <para>The paragraph above about widening still stands and is not contradicted by this:
        /// [1,2,3,2,1]/9 reaches TWO subpixels either side and desaturates; the box reaches none
        /// beyond the pixel's own three lamps. Narrower and wider are not the same axis.</para>
        /// </summary>
        /// <remarks>WPF_SUBPIXEL_FILTER overrides it with a comma-separated set of weights
        /// (normalized here), which is what made the re-sweep above cheap. Kept for the next one.
        /// </remarks>
        /// <summary>A curve to put through the lamps BEFORE they are filtered, or null to leave them
        /// alone. The renderer owns the curve and decides the order; this is only where it lands.
        /// </summary>
        internal static byte[]? PreFilterLut;

        private static readonly float[] SubpixelFilter = LoadFilter();

        /// <summary>How much of the light the filter passes, as distinct from how it spreads it.
        /// <para>These are two different facts and only one of them survives LoadFilter: the taps
        /// are divided by their own total on the way in, so any filter handed to us -- including
        /// the one solved off GDI's own pixels, whose taps sum to 0.899 -- arrives summing to 1
        /// with its gain quietly discarded. Spread and gain have to be carried separately or the
        /// second one cannot be expressed at all.</para>
        /// <para>Made expressible in order to test one, and the answer is that a gain does not
        /// belong here AT ALL. Sweeping it costs immediately and steeply -- 54,934 differing
        /// pixels over the parity suite at 1.0, against 340,310 at 0.96 and 374,007 at the 0.899
        /// solved off GDI's own pixels. The reason is that a gain is a straight line and dims
        /// FULL coverage along with partial: the solid interior of every stem stops being black.
        /// GDI keeps solid black solid. The 0.899 was solved on synthetic bars about a pixel
        /// wide, which are all edge and no interior, so it fitted the edges and was read as if
        /// it applied everywhere. Whatever pulls our edges down has to pin 1 to 1, which is a
        /// curve, not a factor -- and that curve is SubpixelGamma, already at its own sharp
        /// minimum. Left at 1 and kept only so the next person can re-run the disproof.</para></summary>
        private static readonly float SubpixelGain =
            float.TryParse(Environment.GetEnvironmentVariable("WPF_SUBPIXEL_GAIN"),
                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture,
                out float g) && g > 0f ? g : 1f;

        private static float[] LoadFilter()
        {
            string? s = Environment.GetEnvironmentVariable("WPF_SUBPIXEL_FILTER");
            if (!string.IsNullOrWhiteSpace(s))
            {
                string[] parts = s!.Split(',');
                var w = new float[parts.Length];
                float total = 0f;
                for (int i = 0; i < parts.Length; i++) { w[i] = float.Parse(parts[i], System.Globalization.CultureInfo.InvariantCulture); total += w[i]; }
                if (total > 0f) { for (int i = 0; i < w.Length; i++) w[i] /= total; return w; }
            }
            return new[] { 0f, 1f / 3f, 1f / 3f, 1f / 3f, 0f };
        }

        /// <summary>Per-channel coverage: one byte each for the red, green and blue lamp of every
        /// pixel, plus the average in alpha for the compositor to blend the destination with.</summary>
        internal readonly struct SubpixelMask
        {
            public readonly byte[] Rgba;
            public readonly int Width;
            public readonly int Height;
            public readonly float OriginX;
            public readonly float OriginY;

            public SubpixelMask(byte[] rgba, int width, int height, float originX, float originY)
            {
                Rgba = rgba; Width = width; Height = height; OriginX = originX; OriginY = originY;
            }

            public bool IsEmpty => Width <= 0 || Height <= 0;
        }

        /// <summary>The same outline this would rasterize to a grey mask, resolved onto the three
        /// lamps of each pixel instead.</summary>
        /// <summary>Vertical samples for the run being drawn, or 0 to use <see cref="SubpixelRows"/>.
        /// <para>Set by the renderer from the face's 'gasp' for the size in hand -- symmetric
        /// smoothing is a per-size property of the FACE, not a global choice. Ambient rather than a
        /// parameter for the same reason <see cref="PreFilterLut"/> is: the rasterization is reached
        /// through the generic shape path, which knows nothing about fonts.</para></summary>
        internal static int SubpixelRowsForRun;

        /// <summary>SCANTYPE + 1 when the run's face asks for dropout control at this size, else 0.
        /// Set by the renderer per run.</summary>
        /// <summary>Whether a sample exactly on a span's right edge is inside it. GDI's rule; see
        /// the note at the sampling test. WPF_CT_SPANEND=old for the half-open one.</summary>
        private static readonly bool s_spanEndInclusive =
            Environment.GetEnvironmentVariable("WPF_CT_SPANEND") != "old";

        internal static int DropoutForRun;

        /// <summary>How many bits the dropout pass set on the last rasterization, and how many
        /// times it ran. A diagnostic: the edge solver renders a GeometryFill rather than a glyph
        /// run, and this is how to tell whether the pass it is inverting is the one that draws.</summary>
        internal static int LastDropoutFills;
        internal static int LastDropoutRuns;
        private static readonly bool s_dropoutTrace = Environment.GetEnvironmentVariable("WPF_DROPOUT_TRACE") == "2";

        /// <summary>WPF_DROPOUT_TRACE=3: every INSIDE span ApplyVerticalDropout looks at, before
        /// its sample test rejects it. Level 2 traces GdiDropoutFills, which is the pass that
        /// actually draws; this one answers "why was this span never considered" for the other.
        /// <para>ApplyVerticalDropout IS DEAD ON THE SHIPPED PATH. RasterizeSubpixel fills its
        /// `samples` array and calls it, and then `if (UseGdiFilter)` -- true unless
        /// WPF_GDI_FILTER=0 -- rebuilds the raster from the contours through GdiTableFilterRowset
        /// and returns without ever reading `samples`. Its dropout fills come from
        /// GdiDropoutFills instead. Two hours went into a fix to this function that could not
        /// possibly have measured; if a dropout question is being asked, ask it of
        /// GdiDropoutFills.</para></summary>
        private static readonly bool s_dropoutAllSpans =
            Environment.GetEnvironmentVariable("WPF_DROPOUT_TRACE") == "3";

        /// <summary>WPF_CT_DROPOUT_EDGE=old: ask whether a span holds a row sample with the
        /// convention this pass used to use, which is not the fill's. See the test itself.</summary>
        private static readonly bool s_dropoutEdgeFill =
            Environment.GetEnvironmentVariable("WPF_CT_DROPOUT_EDGE") != "old";

        /// <summary>WPF_CT_DROPOUT_STUBS=0 fills stubs too (rule 3 without rule 4).</summary>
        private static readonly bool s_dropoutStubs =
            Environment.GetEnvironmentVariable("WPF_CT_DROPOUT_STUBS") != "0";

        /// <summary>Which edge of a span a row sample that lands EXACTLY on it belongs to. The slab
        /// probe says GDI excludes a sample on the span's bottom edge and includes one on its top
        /// (a slab of exactly 0.5px draws nothing, one of exactly 1.5px draws ONE row); we had it
        /// the other way. WPF_CT_ROWEDGE=old restores the old convention.</summary>
        /// <summary>WPF_CT_ROWEDGE=extremum: a row sample lying exactly on a local MAXIMUM in y
        /// (horizontal runs collapsed) is not a crossing, so the two edges meeting there
        /// contribute nothing instead of two.
        /// <para>This is fsc_CalcLine@1400435a0's rule, put in terms a crossing-based fill can
        /// use. Its run over the perpendicular axis is first = (((v+32) &amp; ~63) + 32) >> 6 and
        /// last = (v'-33) >> 6, which evaluated over every v are the smallest sample STRICTLY
        /// GREATER than v and the largest STRICTLY LESS than v': an edge ENDING on a sample takes
        /// no crossing from it. Applied to every edge that is catastrophic here -- 135,267,701
        /// and 228 failures -- and the fault is ours, not the reading's. GDI never flattens a
        /// curve (fsc_CalcSpline solves them per scanline), so its edges end where the FONT has
        /// points; ours end wherever the flattener put a vertex, and a crossing lost at one of
        /// those unbalances the winding. Restricting it to genuine extrema keeps every monotone
        /// vertex counted exactly once, which is all the winding needs.</para>
        /// <para>Times' bars are what ask for it. 'z' at 13ppem has its bottom bar between y=0
        /// and y=0.500, so the row sample lies exactly on the bar's top edge -- and that edge is
        /// a horizontal run whose two neighbours both arrive from above: a local maximum in
        /// device y. Counting them gives four crossings whose middle pair cancels and leaves a
        /// hole (`54.55` against GDI's `59995`); dropping them leaves the outer pair and one span
        /// across the whole bar. The TOP bar is the mirror image, a local minimum, which the
        /// existing (lo, hi] test already handles -- which is exactly why WPF_CT_ROWEDGE=old
        /// fixes the bottom bar and breaks the top one, and why no plain convention does
        /// both.</para>
        /// <para>NOT dropout control. DoVertDropout@140041ea8 and LookForDropouts@140042328 are
        /// read in full: the condition is on[k] == off[k] and the stub test is the three-way
        /// crossing count, both of which this file already implements exactly. The bar's on and
        /// off land on different rows, so GDI does not call it a dropout either -- the ink was
        /// never missing from its bitmap.</para></summary>
        /// <para>SHIPPED: holdout 8..24 605,280 -> 598,774, no ratchet worse and two better
        /// (i@14 2 -> 1, repertoire@14i 17 -> 16), Times 'z'@13 exact through the FILL.
        /// WPF_CT_ROWEDGE=gdi restores the plain (lo, hi] test.</para>
        private static readonly bool s_rowEdgeExtremum =
            Environment.GetEnvironmentVariable("WPF_CT_ROWEDGE") is null or "extremum";

        /// <summary>WPF_CT_ROWEDGE=strict: an edge crosses a row sample only STRICTLY between its
        /// ends, so an edge that BEGINS or ENDS exactly on the sample contributes no crossing.
        /// <para>fsc_CalcLine@1400435a0 says this outright. Its run over the perpendicular axis is
        /// set up as first = (((v+32) &amp; ~63) + 32) >> 6 and last = (v'-33) >> 6, and evaluated
        /// over every v those are the smallest sample STRICTLY GREATER than v and the largest
        /// STRICTLY LESS than v'. An edge whose endpoint lands on a sample is walked from the next
        /// one along.</para>
        /// <para>Times' 'z' at 13ppem is what it is for. Its bottom bar sits between y=0 and
        /// y=0.500, so the row sample lies exactly on the bar's top edge. The edges that bound the
        /// bar's flat stretch -- the diagonal arriving at pt15 and the serif leaving pt17 -- both
        /// END on that sample, so GDI takes no crossing from either and the row's only crossings
        /// are the outer two: one span, the whole bar, `59995`. Counting them gives four
        /// crossings whose middle pair cancels, which is a hole exactly where we drew one
        /// (`54.55`). No dropout rule is involved; the bar was never missing from GDI's
        /// bitmap.</para></summary>
        private static readonly bool s_rowEdgeStrict =
            Environment.GetEnvironmentVariable("WPF_CT_ROWEDGE") == "strict";

        /// <summary>WPF_CT_ROWEDGE=both: count an edge for a row sample lying exactly on either
        /// of its ends, so the vertical test matches the horizontal one.
        /// <para>Across a row this function tests a span [A, B] -- inclusive at both ends -- and
        /// 306 measured rows say that is right: Verdana Regular at 19ppem scores 74 out of
        /// 34,283,819 ink with it and 9,139 with the start made exclusive, and a row that exact
        /// cannot be using the wrong rule. Down a column the test was (lo, hi], inclusive at one
        /// end only. Both axes are the same question about the same tie and there is no reason for
        /// them to answer it differently.</para>
        /// <para>It is Times' bottom bars that ask. 'z' at 13ppem has its bar between y=0 and
        /// y=0.500, so the bar's top edge lands exactly on the row sample for its whole flat
        /// stretch; (lo, hi] throws that sample away and the row comes out with a hole in it
        /// (`54.55` against GDI's `59995`).</para></summary>
        private static readonly bool s_rowEdgeBoth =
            Environment.GetEnvironmentVariable("WPF_CT_ROWEDGE") == "both";

        private static readonly bool s_rowEdgeGdi =
            Environment.GetEnvironmentVariable("WPF_CT_ROWEDGE") != "old";

        /// <summary>VERTICAL DROPOUT CONTROL, TrueType rule 3 on columns: where the outline crosses
        /// a lamp column between two adjacent row samples -- entering and leaving without covering
        /// either sample -- the row is turned on for that lamp. GDI's ClearType does exactly this
        /// when the face's SCANCTRL says so (slab probe: any height down to 1/16px renders as a
        /// full row with Times' prep, nothing below half a row without), and it is what keeps
        /// Times New Roman's serifs: the face's ClearType branch leaves them 0.22px tall for IUP
        /// and the scan converter fills the row. The row chosen is the one holding the span's
        /// midpoint, which is where GDI's ink lands (the serif row itself, not the row below).
        /// Stubs (rule 4) are not distinguished.</summary>
        private static void ApplyVerticalDropout(List<List<Vector2>> contours, FillRule fillRule,
                                                 int originX, int originY, int subWidth, int height,
                                                 byte[] samples)
        {
            var edges = new List<Edge>();
            foreach (List<Vector2> c in contours)
                for (int i = 0; i < c.Count; i++)
                {
                    Vector2 a = c[i], b = c[(i + 1) % c.Count];
                    if (a.X != b.X) edges.Add(new Edge(a, b));
                }
            if (edges.Count == 0) return;
            var crossings = new List<(float Y, int Dir)>();
            for (int lx = 0; lx < subWidth; lx++)
            {
                float cx = originX + (lx + 0.5f) * HalfLamps;
                crossings.Clear();
                foreach (Edge e in edges)
                {
                    float xmin = MathF.Min(e.X0, e.X1), xmax = MathF.Max(e.X0, e.X1);
                    if (cx < xmin || cx >= xmax) continue;
                    float t = (cx - e.X0) / (e.X1 - e.X0);
                    crossings.Add((e.Y0 + t * (e.Y1 - e.Y0), e.X1 > e.X0 ? 1 : -1));
                }
                if (crossings.Count < 2) continue;
                crossings.Sort((p, q) => p.Y.CompareTo(q.Y));
                int winding = 0;
                for (int i = 0; i < crossings.Count - 1; i++)
                {
                    winding += crossings[i].Dir;
                    bool inside = fillRule == FillRule.NonZero ? winding != 0 : (winding & 1) != 0;
                    if (!inside) continue;
                    float ya = crossings[i].Y, yb = crossings[i + 1].Y;
                    if (yb <= ya) continue;
                    // DOES THE SPAN CONTAIN A ROW SAMPLE? ASK IT THE WAY THE FILL ASKS IT.
                    // The fill counts an edge for row sample `sy` when `sy > lo && sy <= hi` (see
                    // s_rowEdgeGdi), so a sample lying EXACTLY on a span's smaller y belongs to the
                    // span above and one exactly on its larger y belongs to this one. This test
                    // used the opposite convention at both ends -- smallest sample >= ya, counted
                    // while < yb -- and the two disagreements do not cancel. A span whose top edge
                    // lands exactly on a sample is then invisible twice over: the fill rejects the
                    // sample as belonging to the span above, and this pass declines to call it a
                    // dropout because it believes the fill took it. The row comes out EMPTY, which
                    // is a hole no rule of GDI's produces.
                    // <para>It is not a corner case in this face. Times' ClearType branch leaves
                    // 'z' at 13ppem with its bottom bar between y=0 and y=0.5 -- pt15 and pt16 at
                    // 0.500 exactly, a DELTAP having taken them down half a pixel from the bi-level
                    // 1.000 -- so the bar's top edge sits ON the row sample for its whole flat
                    // stretch. GDI draws that row `59995`; we drew `54.55`, a gap straight through
                    // the middle of the bar, and that one row IS the glyph's entire 1,470. Bars and
                    // serifs landing on a half pixel is what this face does at these sizes.</para>
                    // <para>The smallest sample STRICTLY GREATER than ya is floor(ya-0.5)+1.5 --
                    // which equals the old ceiling form whenever ya-0.5 is not an integer, so the
                    // change is confined to the exact-hit case -- and it counts while <= yb.
                    // WPF_CT_DROPOUT_EDGE=old restores the mismatched test.</para>
                    if (s_dropoutAllSpans)
                        Console.Error.WriteLine($"      span col={lx} y=({ya - originY:0.0000},"
                            + $"{yb - originY:0.0000}) originY={originY}");
                    float first = s_dropoutEdgeFill
                                ? MathF.Floor(ya - originY - 0.5f) + 1.5f + originY
                                : MathF.Ceiling(ya - originY - 0.5f) + 0.5f + originY;
                    if (s_dropoutEdgeFill ? first <= yb : first < yb) continue;   // fill took it
                    int py = (int) MathF.Floor((ya + yb) * 0.5f - originY);
                    if (py < 0 || py >= height) continue;
                    samples[py * subWidth + lx] = 255;
                }
            }
        }

        /// <summary>Use the CONTRAST-ENHANCED filter palette for the run being drawn.
        /// <para>Set by the renderer, ambient for the same reason <see cref="SubpixelRowsForRun"/>
        /// is. The two palettes are `fontdrvhost+0xa7a60` (the plain three-lamp box sum) and
        /// `+0xa7960`, and `ulClearTypeFilter_6x1` picks between them on its third argument.</para>
        /// </summary>
        internal static bool ContrastFilterForRun;

        /// <summary>`fontdrvhost+0xa7960` decoded through the code table at `+0xa7420`: for each of
        /// the 243 base-3 indices over five contiguous lamps (the first lamp most significant), the
        /// three channel levels 0..6. Read out of the binary, not fitted -- and the SAME extraction
        /// applied to `+0xa7a60` reproduces the box sum below exactly for all 243 entries, which is
        /// what says the decode is right.</summary>
        private static readonly byte[] GdiContrastLevels =
        {
            0,0,0,0,0,2,0,1,3,0,2,2,0,2,4,0,2,4,1,3,4,1,3,5,1,3,5,
            2,2,2,2,2,4,2,3,5,2,4,4,2,4,6,2,4,6,2,4,5,2,4,6,2,4,6,
            3,4,3,3,4,5,3,4,5,3,5,4,3,5,6,3,5,6,3,5,5,3,5,6,3,5,6,
            2,2,0,2,2,2,2,3,3,2,4,2,2,4,4,2,4,4,3,5,4,3,5,5,3,5,5,
            4,4,2,4,4,4,4,5,5,4,6,4,4,6,6,4,6,6,4,6,5,4,6,6,4,6,6,
            4,5,3,4,5,5,4,5,5,4,6,4,4,6,6,4,6,6,4,6,5,4,6,6,4,6,6,
            4,3,1,4,3,3,4,4,4,4,5,3,4,5,5,4,5,5,4,5,4,4,5,5,4,5,5,
            5,4,2,5,4,4,5,5,5,5,6,4,5,6,6,5,6,6,5,6,5,5,6,6,5,6,6,
            5,5,3,5,5,5,5,5,5,5,6,4,5,6,6,5,6,6,5,6,5,5,6,6,5,6,6,
            2,0,0,2,0,2,2,1,3,2,2,2,2,2,4,2,2,4,3,3,4,3,3,5,3,3,5,
            4,2,2,4,2,4,4,3,5,4,4,4,4,4,6,4,4,6,4,4,5,4,4,6,4,4,6,
            5,4,3,5,4,5,5,4,5,5,5,4,5,5,6,5,5,6,5,5,5,5,5,6,5,5,6,
            4,2,0,4,2,2,4,3,3,4,4,2,4,4,4,4,4,4,5,5,4,5,5,5,5,5,5,
            6,4,2,6,4,4,6,5,5,6,6,4,6,6,6,6,6,6,6,6,5,6,6,6,6,6,6,
            6,5,3,6,5,5,6,5,5,6,6,4,6,6,6,6,6,6,6,6,5,6,6,6,6,6,6,
            5,3,1,5,3,3,5,4,4,5,5,3,5,5,5,5,5,5,5,5,4,5,5,5,5,5,5,
            6,4,2,6,4,4,6,5,5,6,6,4,6,6,6,6,6,6,6,6,5,6,6,6,6,6,6,
            6,5,3,6,5,5,6,5,5,6,6,4,6,6,6,6,6,6,6,6,5,6,6,6,6,6,6,
            3,1,0,3,1,2,3,2,3,3,3,2,3,3,4,3,3,4,4,4,4,4,4,5,4,4,5,
            5,3,2,5,3,4,5,4,5,5,5,4,5,5,6,5,5,6,5,5,5,5,5,6,5,5,6,
            5,4,3,5,4,5,5,4,5,5,5,4,5,5,6,5,5,6,5,5,5,5,5,6,5,5,6,
            4,2,0,4,2,2,4,3,3,4,4,2,4,4,4,4,4,4,5,5,4,5,5,5,5,5,5,
            6,4,2,6,4,4,6,5,5,6,6,4,6,6,6,6,6,6,6,6,5,6,6,6,6,6,6,
            6,5,3,6,5,5,6,5,5,6,6,4,6,6,6,6,6,6,6,6,5,6,6,6,6,6,6,
            5,3,1,5,3,3,5,4,4,5,5,3,5,5,5,5,5,5,5,5,4,5,5,5,5,5,5,
            6,4,2,6,4,4,6,5,5,6,6,4,6,6,6,6,6,6,6,6,5,6,6,6,6,6,6,
            6,5,3,6,5,5,6,5,5,6,6,4,6,6,6,6,6,6,6,6,5,6,6,6,6,6,6
        };


        /// <summary>Whether the face asked for SYMMETRIC SMOOTHING at this size, set by the
        /// renderer from the gasp for the same reason SubpixelRowsForRun is.</summary>
        internal static bool SymmetricVerticalForRun;

        /// <summary>MEASURED AND WRONG IN THIS FORM, and kept because the gap it was built for
        /// is real and someone will try this first.
        /// <para>At 20ppem, where every face in the specimen has the gasp bit, a tent applied
        /// to the quantized lamps costs 8,795,316 at [1,2,1], 6,586,550 at [1,4,1] and
        /// 5,510,843 at [1,6,1], against 2,299,096 with no smoothing at all -- converging back
        /// toward off as the filter weakens, which says any amount of it hurts. So symmetric
        /// smoothing is not a vertical blur of the lamps, at least not after quantization and
        /// before the horizontal filter.</para>
        /// <para>The gap remains, and is the sharpest unexplained signature left in the text:
        /// Segoe UI's horizontal-edge disagreement runs 3,197 at 18ppem and 3,627 at 19, then
        /// 33,287 at 20 -- ten times, at exactly the size its gasp gains SYM_SMOOTHING -- with
        /// the count of such pixels going 63 to 536 and the signed sum from +3,565 to -21,149.
        /// Whatever GDI does there, it is named, it is size-gated by the face, and we do not
        /// do it.</para></summary>
        /// <summary>Soften each lamp against the rows above and below it, which is what
        /// symmetric smoothing does to a horizontal edge. WPF_SYM_FILTER gives the middle
        /// weight; 2 is the [1,2,1] tent.</summary>
        private static readonly int SymmetricMiddle =
            int.TryParse(Environment.GetEnvironmentVariable("WPF_SYM_FILTER"), out int sm)
                && sm >= 0 ? sm : 2;

        private static byte[] SmoothRows(byte[] src, int w, int h)
        {
            var dst = new byte[src.Length];
            int mid = SymmetricMiddle, total = mid + 2;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int up = src[(y > 0 ? y - 1 : y) * w + x];
                    int dn = src[(y < h - 1 ? y + 1 : y) * w + x];
                    dst[y * w + x] = (byte) ((up + src[y * w + x] * mid + dn) / total);
                }
            return dst;
        }

        public static SubpixelMask RasterizeSubpixel(PathGeometry path,
                                                     float tolerance = 0f)
        {
            var contourFigures = new List<int>();
            List<List<Vector2>> contours = Flatten(path, tolerance, contourFigures);
            if (contours.Count == 0) return default;

            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            int pointCount = 0;
            foreach (List<Vector2> c in contours)
                foreach (Vector2 p in c)
                {
                    minX = MathF.Min(minX, p.X); minY = MathF.Min(minY, p.Y);
                    maxX = MathF.Max(maxX, p.X); maxY = MathF.Max(maxY, p.Y);
                    pointCount++;
                }
            if (pointCount == 0) return default;

            // Two pixels of padding either side rather than one: the filter reaches two subpixels
            // beyond the ink, so a glyph rasterized to its own bounds would have its outermost lamp
            // filtered against nothing and lose the fringe that belongs there.
            int originX = (int)MathF.Floor(minX) - 2;
            int originY = (int)MathF.Floor(minY) - 1;
            int width = (int)MathF.Ceiling(maxX) + 2 - originX;
            int height = (int)MathF.Ceiling(maxY) + 1 - originY;
            if (width <= 0 || height <= 0) return default;

            // Three times as wide -- or HalfLamps times that again, when a lamp is to be built the
            // way GDI builds one -- and the outline stretched to match.
            int scale = SubpixelsPerPixel * HalfLamps;
            foreach (List<Vector2> c in contours)
                for (int i = 0; i < c.Count; i++)
                    c[i] = new Vector2(c[i].X * scale, c[i].Y);

            int subWidth = width * SubpixelsPerPixel;
            int rows = SubpixelRowsForRun > 0 ? SubpixelRowsForRun : SubpixelRows;
            byte[] samples = RowsThenThreshold(contours, path.FillRule, originX * scale, originY,
                                               subWidth, height, rows);
            if (DropoutForRun > 0)
                ApplyVerticalDropout(contours, path.FillRule, originX * scale, originY, subWidth, height, samples);
            // ulClearTypeFilter_6x5 @ fontdrvhost+0x217d8 is NOT a 2D kernel: it multiplies
            // the bitmap height by 5, runs the ORDINARY 6x1 horizontal filter over that
            // 5x-tall bitmap, and only THEN combines each group of five filtered rows into
            // one. So GDI filters horizontally FIRST, at 5x vertical resolution, and averages
            // afterwards -- where we average the samples and filter once. The orders are not
            // interchangeable: averaging first lets the horizontal filter mix neighbours whose
            // vertical coverage differs, which is why our sub-pixel-tall features can never
            // come out uniform the way GDI's do. WPF_VFILT_FIRST=1.
            // GDI's structure, both halves together: the NONLINEAR table filter applied to each
            // vertical subrow and the results averaged afterwards. Either half alone is a no-op
            // (our box filter is linear so the orders commute; the table alone still averages the
            // samples first), which is why they have to be measured as one change.
            if (UseGdiFilter)
            {
                var polysG = new List<List<Vector2>>(contours.Count);
                // The contours have already been stretched by `scale` for the lamp grid; this
                // sampler works in PIXEL space, so undo it.
                foreach (List<Vector2> c in contours)
                {
                    var cp = new List<Vector2>(c.Count);
                    foreach (Vector2 pt in c) cp.Add(new Vector2(pt.X / scale, pt.Y));
                    polysG.Add(cp);
                }
                // GDI KEEPS EVERYTHING IN SEVEN LEVELS UNTIL THE VERY END. ulClearTypeFilter_6x5 does
                // NOT average the filtered subrows: interpolatePixel_6x5 decodes each of the five to
                // three 0..6 channel levels, applies the kernel 4:9:10:9:4 (sum 36, the divisor table
                // at 0xa75f0 is exactly round(s/36)), and re-encodes. A plain mean is both the wrong
                // weights AND the wrong domain -- it averages coverages where GDI averages levels and
                // rounds once. Only then does the level become a pixel, through a seven-entry ramp.
                int nSub = FilterBeforeVerticalAverage && SubpixelRowsForRun > 1 ? SubpixelRowsForRun : 1;
                var lev = new byte[nSub][];
                // ONE GLYPH AT A TIME, because that is the only thing GDI ever does: ExtTextOutW
                // rasterizes each glyph into its OWN bitmap and blits it, so the dropout pass sees
                // one glyph's contours, one glyph's box, and no neighbours at all. Handing it a
                // whole run instead changes two things that matter -- the xMin/xMax/yMin/yMax the
                // fill row is clamped into becomes the RUN's box, and the stub test counts
                // crossings that belong to the next letter.
                // <para>Times New Roman is where it shows. Its baseline serifs ARE dropout fills:
                // 'n' at 15ppem scores 0 against GDI on its own and 2,550 with the pass disabled.
                // Put any descender in the run -- y, p, g, j, q -- and the run's box now reaches
                // below the baseline, the serif's fill row is no longer clamped up into it, and
                // the serif lands a row low: "yn" 4,088, "pn" 4,225, while "bn" (no descender)
                // stays 0. Nothing else in the corpus does this; every other face measures
                // identically for "n" and "yn".</para>
                List<(int Col, int SubRow)>? fills = null;
                if (DropoutForRun > 0)
                {
                    int[]? owners = s_dropoutPerGlyph ? FigureGlyphIdsForRun : null;
                    fills = new List<(int Col, int SubRow)>();
                    int first = 0;
                    while (first < polysG.Count)
                    {
                        int gid = GlyphOf(owners, contourFigures, first);
                        int last = first + 1;
                        while (last < polysG.Count && GlyphOf(owners, contourFigures, last) == gid) last++;
                        List<List<Vector2>> group = first == 0 && last == polysG.Count
                            ? polysG : polysG.GetRange(first, last - first);
                        fills.AddRange(GdiDropoutFills(group, path.FillRule, originX, originY,
                                                       width, height, nSub));
                        first = last;
                    }
                }
                LastDropoutFills = fills?.Count ?? -1; LastDropoutRuns++;
                for (int sI = 0; sI < nSub; sI++)
                    lev[sI] = GdiTableFilterRowset(polysG, path.FillRule, originX, originY,
                                                   width, height, (sI + GdiSubrowPhase) / nSub, nSub, sI, fills);
                var outG = new byte[width * height * 4];
                bool gdiKernel = nSub == GdiVerticalKernel.Length;
                int weightSum = 0;
                if (gdiKernel) foreach (int w in GdiVerticalKernel) weightSum += w;
                for (int px = 0; px < width * height; px++)
                {
                    int sumC = 0;
                    for (int ch = 0; ch < 3; ch++)
                    {
                        int s = 0;
                        if (gdiKernel)
                            for (int sI = 0; sI < nSub; sI++) s += GdiVerticalKernel[sI] * lev[sI][px * 4 + ch];
                        else
                            for (int sI = 0; sI < nSub; sI++) s += lev[sI][px * 4 + ch];
                        int div = gdiKernel ? weightSum : nSub;
                        int outLvl = (s + div / 2) / div;
                        if (outLvl > 6) outLvl = 6;
                        byte cov = GdiLevelRamp[outLvl];
                        outG[px * 4 + ch] = cov;
                        sumC += cov;
                    }
                    outG[px * 4 + 3] = (byte) (sumC / 3);
                }
                return new SubpixelMask(outG, width, height, originX, originY);
            }

            if (FilterBeforeVerticalAverage && SubpixelRowsForRun > 1)
            {
                int n = SubpixelRowsForRun;
                var acc = new int[width * height * 4];
                for (int sIdx = 0; sIdx < n; sIdx++)
                {
                    byte[] one = RowsThenThresholdAt(contours, path.FillRule, originX * scale,
                                                     originY, subWidth, height, (sIdx + 0.5f) / n);
                    if (PreFilterLut is byte[] pre0)
                        for (int i = 0; i < one.Length; i++) one[i] = pre0[one[i]];
                    byte[] f = FilterSubpixels(one, width, height);
                    for (int i = 0; i < acc.Length; i++) acc[i] += f[i];
                }
                var outp = new byte[width * height * 4];
                for (int i = 0; i < outp.Length; i++) outp[i] = (byte)(acc[i] / n);
                return new SubpixelMask(outp, width, height, originX, originY);
            }

            // SYMMETRIC SMOOTHING, when the face's gasp asks for it at this size. It is a
            // filter and not more samples: sweeping the vertical SAMPLE count at 20ppem gives
            // 2,299,096 / 3,281,860 / 2,303,143 / 2,496,104 for one to four, worst at the even
            // counts, because an odd count includes the scanline centre and GDI samples there.
            // What the gasp bit actually turns on is softening across ROWS, and its absence is
            // visible: Segoe UI's horizontal-edge disagreement is about 3,200 at 18 and 19ppem
            // and 33,287 at 20, which is exactly where its gasp gains the bit.
            if (SymmetricVerticalForRun && height > 2)
                samples = SmoothRows(samples, subWidth, height);
            // The contrast curve, if it is to be applied to the RAW LAMPS rather than to the filtered
            // result. Set by the renderer, which owns the curve; null means correct afterwards as
            // before. See WgpuSceneRenderer.s_correctBeforeFilter.
            if (PreFilterLut is byte[] pre)
                for (int i = 0; i < samples.Length; i++) samples[i] = pre[samples[i]];

            return new SubpixelMask(FilterSubpixels(samples, width, height), width, height, originX, originY);
        }

        /// <summary>How many BILEVEL samples a lamp is averaged from, or 1 to keep the exact area.
        /// <para>The description of ClearType everyone repeats -- six times horizontal oversampling,
        /// two samples per lamp -- is a sampling rule, and quantizing an exact area to three levels
        /// is only an approximation of it. They agree on a vertical edge and part company on a
        /// slanted one, where the area says "half" and the two samples say which half.
        /// WPF_SUBPIXEL_HALFLAMPS switches between them so the difference is a measurement rather
        /// than an argument.</para>
        /// <para>TWO, which is six samples per pixel and is what Microsoft's "TrueType and ClearType"
        /// says the rasterizer does: "a 6x1 filtering technique -- with six times the resolution
        /// along the x-axis and no resolution change along the y-axis".</para>
        /// <para>It was 1 for a long time on the strength of the CONTROL window (1 gave 2,491,739
        /// against 2,533,593 for 2), and that measurement was made with the interpreter still
        /// hinting as a bi-level rasterizer. Re-measured on the text specimen -- rows of plain
        /// Labels, nothing but text, repeatable to 41 parts in 1.4 million -- 2 wins: 1,408,586
        /// against 1,410,303 for one sample and 1,432,328 for three. Small, but forty times the
        /// noise, and it is the documented number.</para>
        /// </summary>
        private static readonly int HalfLamps =
            int.TryParse(Environment.GetEnvironmentVariable("WPF_SUBPIXEL_HALFLAMPS"), out int hl) && hl > 0
                ? hl : 2;

        /// <summary>How much of a half-lamp has to be covered for it to light. Half of it is the
        /// obvious reading of a bilevel sample -- the sample point is inside the shape or it is not,
        /// and the sample point is the middle. WPF_SUBPIXEL_THRESHOLD sweeps it, because "half"
        /// is an assumption about where in the half-lamp GDI takes its sample and a lower threshold
        /// widens every stem by exactly one half-lamp, which is the size of the gap measured
        /// between our stems and Windows'.</summary>
        /// <summary>Whether a fine sample is decided by asking if its CENTRE is inside the outline
        /// -- GDI's bi-level scan conversion -- rather than by integrating its area and thresholding
        /// at half. OFF: MEASURED AND REJECTED, but worth keeping for the reason it was tried.
        /// <para>The two rules agree for a straight edge and differ only for a sliver narrower than
        /// half a sample straddling the centre, which is the leading edge of a curve -- and 'o', 'e'
        /// and 'c' sit half a pixel right of Windows' where 'l' and 'n' are exact. It looked like
        /// the explanation. It is not: 'o' at 12ppem comes back pixel for pixel IDENTICAL with the
        /// rule changed, and the repertoire's structural disagreement goes 41,072 to 46,072. So the
        /// curve shift is in the FITTED OUTLINE, not in how the outline is sampled, and one more
        /// rasterizer rule is ruled out. WPF_SUBPIXEL_SAMPLE=centre.</para></summary>
        private static readonly bool CentreSample =
            Environment.GetEnvironmentVariable("WPF_SUBPIXEL_SAMPLE") == "centre";

        /// <summary>WHAT THE REMAINING WINDOW DIFFERENCE IS, measured per channel so that anyone
        /// reaching for this threshold knows what it can and cannot buy. Signed ink over the whole
        /// window, ours minus Windows':
        /// <para>    signed   R -40,471   G -83,266   B -39,801      sum   -163,538</para>
        /// <para>    absolute R 409,393   G 407,458   B 386,625      sum  1,203,476</para>
        /// <para>R and B are SYMMETRIC, so there is no sub-pixel shift -- a shifted run moves ink
        /// from one outer lamp to the other and would show them opposed. And the signed total is
        /// only 14% of the absolute one: the other 86% is ink in the wrong PLACE at lamp
        /// resolution, cancelling in the sum. So "our text is too light" is a small part of it,
        /// widening is at best a 14% lever, and that is why every global widening knob -- this
        /// threshold, the lamp grid, a blanket stroke correction -- buys a little and loses more
        /// somewhere else.</para>
        /// <para>Green being twice as short as the outer lamps is NOT, as I first wrote here, our
        /// strokes straddling pixel boundaries more. Counting saturated lamps says the opposite in
        /// one direction and neither in the other: of the window's inked pixels (ours 550,213 to
        /// Windows' 551,155 -- the same to a fifth of a percent), Windows has 7,658 with the green
        /// lamp full against our 6,316, while OURS has more pixels that are fully black, 3,082 to
        /// 2,793. So Windows carries more pixels whose CENTRE is saturated and whose edges are not,
        /// and we carry more that are solid through. Which of those is cause and which effect the
        /// counts do not say, and the honest reading is that they rule out the shift and leave the
        /// distribution question open.</para></summary>
        private static readonly int HalfLampThreshold =
            int.TryParse(Environment.GetEnvironmentVariable("WPF_SUBPIXEL_THRESHOLD"), out int ht)
                && ht > 0 ? ht : 128;

        /// <summary>WPF_SUBPIXEL_COLLAPSE=avg: box-downsample the half-lamps instead of thresholding.</summary>
        /// <summary>Whether the display's subpixels run blue, green, red from the left.
        /// <para>Read once from the same Windows setting the user sets in the ClearType tuner.
        /// WPF_SUBPIXEL_BGR=1/0 forces it, which is the only way to exercise the other order on a
        /// machine that is not built that way.</para></summary>
        internal static readonly bool LampsRunBlueFirst =
            Environment.GetEnvironmentVariable("WPF_SUBPIXEL_BGR") is string s2 && s2.Length > 0
                ? s2 == "1"
                : Platform.Win32Interop.FontSmoothingIsBgr();

        /// <summary>The exact half-lamp run length to lengthen by one on the right, or 0 for none.
        /// <para>GDI's Segoe UI 'l' at 11, 12 and 13ppem lights SEVEN half-lamps where ours lights
        /// six -- deconvolved, ours is three full lamps and GDI's is three full and a half on the
        /// right. Widening the OUTLINE to buy that half-lamp is measurably wrong: it moves every
        /// point after the stem, and the window's position error goes from 127,023 to 179,585. If
        /// GDI is doing this at all it is doing it here, where nothing moves.</para></summary>
        private static readonly int StemDilate =
            int.TryParse(Environment.GetEnvironmentVariable("WPF_CT_DILATE"), out int sd) ? sd : 0;

        /// <summary>Lengthen every maximal run of lit half-lamps of exactly <see cref="StemDilate"/>
        /// samples by one on the right, and rebuild the lamps that changed.</summary>
        private static void DilateRuns(byte[] fine, byte[] outp, int subWidth, int height)
        {
            int n = subWidth * HalfLamps;
            var lit = new bool[n];
            for (int y = 0; y < height; y++)
            {
                int fineRow = y * subWidth * HalfLamps;
                for (int i = 0; i < n; i++) lit[i] = fine[fineRow + i] >= HalfLampThreshold;

                for (int i = 0; i < n; i++)
                {
                    if (!lit[i] || (i > 0 && lit[i - 1])) continue;   // start of a run only
                    int j = i;
                    while (j + 1 < n && lit[j + 1]) j++;
                    if (j - i + 1 == StemDilate && j + 1 < n && !lit[j + 1]) lit[j + 1] = true;
                    i = j;
                }

                int row = y * subWidth;
                for (int x = 0; x < subWidth; x++)
                {
                    int count = 0;
                    for (int k = 0; k < HalfLamps; k++) if (lit[x * HalfLamps + k]) count++;
                    outp[row + x] = (byte)(count * 255 / HalfLamps);
                }
            }
        }

        private static readonly bool AverageHalfLamps =
            Environment.GetEnvironmentVariable("WPF_SUBPIXEL_COLLAPSE") == "avg";

        /// <summary>Threshold each half-lamp sample and average them back down to one value per lamp.
        /// </summary>
        private static byte[] CollapseHalfLamps(byte[] fine, int subWidth, int height)
        {
            var outp = new byte[subWidth * height];
            for (int y = 0; y < height; y++)
            {
                int fineRow = y * subWidth * HalfLamps;
                int row = y * subWidth;
                for (int x = 0; x < subWidth; x++)
                {
                    if (AverageHalfLamps)
                    {
                        // A PLAIN BOX DOWNSAMPLE. Thresholding each half-lamp rounds a sliver of
                        // coverage up to a whole one, and a sliver is exactly what the extremum of a
                        // CURVE leaves: measured against GDI at 11ppem, glyphs whose left edge is a
                        // curve ('c' 'd' 'e' 'g' 'o' 'q' 'C' 'G' 'O') start two lamps too far left
                        // while every straight-stemmed glyph ('l' 'i' 'H' 'I' 'M' 'N' ...) is exact
                        // to the lamp -- and a straight edge is the one case thresholding cannot get
                        // wrong, because it covers a half-lamp or it does not.
                        int total = 0;
                        for (int k = 0; k < HalfLamps; k++) total += fine[fineRow + x * HalfLamps + k];
                        outp[row + x] = (byte) (total / HalfLamps);
                    }
                    else
                    {
                        int lit = 0;
                        for (int k = 0; k < HalfLamps; k++)
                            if (fine[fineRow + x * HalfLamps + k] >= HalfLampThreshold) lit++;
                        outp[row + x] = (byte)(lit * 255 / HalfLamps);
                    }
                }
            }
            if (StemDilate > 0) DilateRuns(fine, outp, subWidth, height);
            return outp;
        }

        /// <summary>How many levels a single lamp's coverage is allowed before it is filtered.
        /// <para>MEASURED, not guessed: dump GDI's own ClearType output for a run and count the
        /// distinct values in it. There are SIX (0, 58, 102, 144, 182, 219) plus paper -- seven
        /// levels, which is k/6 -- where ours produced 151. Seven levels out of a three-tap filter is
        /// what you get when each lamp carries one of {0, 1/2, 1}, i.e. two BILEVEL samples averaged,
        /// which is the six-times-horizontal oversampling ClearType has always been described as
        /// doing. We compute exact area instead, which is why a stem GDI puts in one solid column we
        /// spread across two -- and why no curve over the result has ever helped: the scatter of
        /// GDI's value against ours at the same pixel has a standard deviation of 47-79 out of a
        /// range of 120, so our value simply does not predict theirs.</para>
        /// <para>Swept against both instruments, and they agree for once. Structural disagreement with
        /// GDI over the whole repertoire: 574 at exact area, 885 bilevel, 367 at three levels, 846 at
        /// four, 390 at five. In the live window three levels takes position 352,297 -> 343,332 and
        /// weight 2,030,177 -> 2,002,709. Bilevel being far the worst is why the earlier attempt at
        /// "threshold each subpixel" failed and was recorded as a dead end -- it is one level short of
        /// the model, not one too many.</para>
        /// <para>RE-SWEPT on the control window 2026-08-30: three 1,376,484; two 1,830,368; four
        /// 1,529,559; and zero is identical to three, because collapsing the half-lamps has already
        /// left each lamp on one of {0, 1/2, 1} and there is nothing for a further quantization to
        /// do.</para>
        /// <para>WPF_SUBPIXEL_QUANT overrides it: 0 or 1 leaves the exact area alone, 2 makes each
        /// lamp bilevel, 3 is the model above.</para></summary>
        private static readonly int SubpixelLevels =
            int.TryParse(Environment.GetEnvironmentVariable("WPF_SUBPIXEL_QUANT"), out int q) ? q : 3;

        /// <summary>Vertical samples a LAMP is built from. ONE -- the scanline centre -- because
        /// GDI's ClearType has no vertical antialiasing at all: its seven output levels say each
        /// lamp is two bilevel samples averaged, which leaves nowhere to put a partial row. Our
        /// four-sample vertical integration softened every horizontal edge GDI renders hard.
        /// Measured (WPF_SUBPIXEL_ROWS sweeps it): parity structural 367/332/277 for 4/2/1 rows,
        /// and the live WinForms window 2,689,806 -> 2,609,923 -> 2,555,219, improving BOTH its
        /// position and its weight halves. The grey path keeps VerticalSamples: shapes are not
        /// hinted onto the pixel grid, so they do need the vertical coverage.</summary>
        /// <summary>AND 20PPEM IS STILL THE WORST SIZE, which is not this. Structural disagreement
        /// per size runs 653 at 10ppem, about 1,100 to 1,700 from 11 to 19, and 2,948 at 20 -- twice
        /// its neighbour. Segoe UI's gasp gains SYM_SMOOTHING at exactly 20 and we implement no
        /// symmetric smoothing, so that looked like the answer and it is not: rendering at 20ppem is
        /// BYTE IDENTICAL with one vertical sample and with four, because y hinting has already put
        /// every horizontal edge on a pixel boundary and there is no partial row for the extra
        /// samples to find. That is the same reason one row is right here in the first place.
        /// <para>So 20ppem and up is a real second front, it is named (symmetric smoothing), and it
        /// is NOT reachable by turning this up. It is also outside every size the control window
        /// uses, which is why it has not been chased.</para></summary>
        /// <para>RE-SWEPT after RowsThenThreshold made extra samples safe -- under the old order
        /// they could only destroy a partial row, so the original sweep could not have found a
        /// larger count even if one were right. One is STILL best, and not marginally: ppem 12
        /// gives 2,280,798 / 2,857,655 / 2,944,613 and ppem 16 gives 2,802,363 / 3,608,890 /
        /// 3,723,098 for one, three and five. The reasoning above holds -- where the face is
        /// grid-fitted there is no partial row to find, and sampling for one only softens an edge
        /// GDI keeps sharp. Do not sweep this again without a reason that is not "more samples
        /// must be better".</para></summary>
        private static readonly int SubpixelRows =
            int.TryParse(Environment.GetEnvironmentVariable("WPF_SUBPIXEL_ROWS"), out int r) ? r : 1;

        /// <summary>The narrowest a vertical stem may be RENDERED, in lamps (3 = one whole pixel),
        /// or 0 to render it as the outline gives it.
        /// <para>This is the one difference left between us and GDI at small sizes, and it took
        /// ruling out everything else to see it. At 11 pixels an em the lamps under an 'l' are GDI's
        /// 73/153/255/197/111/36 against our 32/114/206/206/114/32: the run spans the same 85 columns
        /// on both sides, so the scale and the advances agree, but a three-tap box can only reach 255
        /// from a stem at least three lamps wide -- GDI's stem is a whole pixel where our smoothly
        /// scaled outline gives 0.81 of one. GDI inks 75 columns of that run to our 69.</para>
        /// <para>It has to be applied to the SPAN and not as a dilation, because a dilation widens
        /// every stem and the thick ones do not need it: emboldening by 0.30 of a lamp brings the
        /// regular face at 11ppem from 0.920 to 0.996 and takes BOLD from 1.007 to 1.056 with it.
        /// A minimum only touches what is under it.</para>
        /// <para>WPF_MIN_STEM sets it, in hundredths of a lamp. IT IS OFF, and the reason is the
        /// window. It does what it was built to do -- at one whole pixel the regular face at 11ppem
        /// goes 0.920 -> 0.981 while BOLD moves only 1.007 -> 1.012, which no dilation can manage --
        /// but the live WinForms window gets worse at every setting: 2,453,109 -> 2,463,512 ->
        /// 2,465,957 -> 2,509,718 for 0 / 2.40 / 2.70 / 3.00 lamps, in BOTH its position and its
        /// weight halves.</para>
        /// <para>That disagreement is the finding. The parity harness says our regular face at 12ppem
        /// carries 0.964 of GDI's ink; the live window's own regions say 0.985, and adding the
        /// difference overshoots. So the harness's per-size ink ratio is NOT a proxy for the window,
        /// and the weight still in the window is not a systematic lightness that more ink would fix.
        /// Anything aimed at the "regular face tilt" has to be confirmed against the window before it
        /// is believed.</para></summary>
        /// <summary>OFF, and the reason is two instruments disagreeing. Dropout control -- keeping a
        /// feature thinner than a sample rather than dropping it, the sample here being a lamp -- is
        /// a real thing to want, and on the six-face specimen one lamp of it measures BETTER: none
        /// 2,197,690; three quarters 2,195,755; ONE 2,195,041; one and a quarter 2,195,609; two
        /// 2,218,880. It costs the control window nothing either.
        /// <para>And it makes 308 of the per-glyph parity measurements worse against 15 better --
        /// whole runs of Segoe UI at 13ppem going 362 pixels wrong, and every capital and lowercase
        /// chunk picking up shade errors of 58/255. Two thousand six hundred out of 2.2 million is
        /// a tenth of a percent on one aggregate; the suite compares glyph by glyph against GDI's
        /// own pixels and says plainly that the shapes got worse. The aggregate does not outvote
        /// it.</para></summary>
        private static readonly float MinStemSubpixels =
            (int.TryParse(Environment.GetEnvironmentVariable("WPF_MIN_STEM"), out int ms) ? ms : 0) / 100f;

        private static void Quantize(byte[] samples)
        {
            if (SubpixelLevels < 2) return;
            int steps = SubpixelLevels - 1;
            for (int i = 0; i < samples.Length; i++)
            {
                int level = (samples[i] * steps + 127) / 255;          // nearest of `steps` bands
                samples[i] = (byte)(level * 255 / steps);
            }
        }

        /// <summary>Spread each lamp's coverage over its neighbours and pack the three into a pixel.
        /// </summary>
        /// <summary>The run's ppem, so the vertical-coverage rule can be limited to the sizes
        /// where features are genuinely sub-pixel tall. Set by the renderer per run.</summary>
        internal static int PpemForRun;

        /// <summary>Largest ppem at which post-filter vertical coverage applies. WPF_VCOV_MAXPPEM.</summary>
        internal static readonly int PostVerticalMaxPpem =
            int.TryParse(Environment.GetEnvironmentVariable("WPF_VCOV_MAXPPEM"), out int vm) ? vm : 8;

        /// <summary>GDI's ClearType filter as a table: bi-level 6x, then for each lamp a base-3
        /// index over the five lamps centred on it, into a 243-entry palette learned from GDI's
        /// own pixels (429k lamps, six faces). Stored as COVERAGE (255 = ink) pre-curve, so the
        /// renderer's existing correction reproduces GDI's pixel. WPF_GDI_FILTER=1.</summary>
        private static readonly byte[] GdiFilterPalette = { 0,0,0,43,43,43,85,85,85,43,43,43,85,85,85,127,127,127,85,85,85,127,127,127,170,170,170,43,255,181,85,99,113,127,127,170,85,85,99,127,127,127,170,170,170,127,127,127,170,170,170,212,212,212,85,58,29,96,64,33,170,170,147,127,96,64,170,170,170,212,212,170,170,170,170,212,212,212,255,255,255,0,0,0,43,43,43,85,127,85,43,43,43,71,85,127,127,127,127,56,71,85,127,127,127,170,170,170,43,43,43,85,85,85,127,127,127,85,85,85,127,127,127,170,170,170,127,127,127,170,170,170,212,212,212,85,99,113,127,127,170,170,170,192,127,127,127,170,170,170,212,212,212,170,170,170,212,212,212,255,255,255,0,0,0,96,69,43,120,103,85,121,96,69,137,120,103,170,127,127,154,137,120,170,170,127,170,170,170,43,43,43,85,127,85,127,127,127,71,85,127,127,127,127,170,170,170,127,127,127,170,170,170,212,212,212,85,85,85,127,127,127,170,170,170,127,127,127,170,170,170,212,212,212,170,170,170,212,212,212,255,255,255 };

        /// <summary>The seven coverages a ClearType channel can take, read off GDI's own
        /// pixels: a level 0..6 is all a channel ever carries.</summary>
        /// <summary>WPF_CT_SPANSTART=in: a sample exactly ON the left boundary of a span counts as
        /// inside, as it used to. See the comment at the span test.</summary>
        private static readonly bool s_spanStartExclusive =
            Environment.GetEnvironmentVariable("WPF_CT_SPANSTART") == "out";

        /// <summary>Where the six horizontal samples sit inside the pixel. WPF_HSUB_PHASE.</summary>
        private static readonly float GdiSamplePhase =
            float.TryParse(Environment.GetEnvironmentVariable("WPF_HSUB_PHASE"),
                System.Globalization.CultureInfo.InvariantCulture, out float hp) ? hp : 0.5f;

        /// <summary>Where the five vertical sub-scanlines sit inside the pixel. WPF_VSUB_PHASE.</summary>
        private static readonly float GdiSubrowPhase =
            float.TryParse(Environment.GetEnvironmentVariable("WPF_VSUB_PHASE"),
                System.Globalization.CultureInfo.InvariantCulture, out float vp) ? vp : 0.5f;

        private static readonly byte[] GdiLevelRamp = { 0, 43, 85, 127, 170, 212, 255 };

        /// <summary>interpolatePixel_6x5's vertical weights, from the tables at
        /// fontdrvhost+0xa7cb8 / +0xa7cd8 / +0xa7ed0 (4d, 9d, 10d per unit level).</summary>
        private static readonly int[] GdiVerticalKernel = { 4, 9, 10, 9, 4 };

        internal static readonly bool UseGdiFilter =
            Environment.GetEnvironmentVariable("WPF_GDI_FILTER") != "0";

        /// <summary>One vertical subrow through GDI's table filter: bi-level 6x lamp counts,
        /// per-lamp 5-window index, palette. Nonlinear, so unlike our box filter the order
        /// against the vertical average is load-bearing -- which is why this and
        /// FilterBeforeVerticalAverage only mean anything together.</summary>
        /// <summary>VERTICAL DROPOUT CONTROL on the GDI-filter path's own 6x sample grid: TrueType
        /// rule 3 applied to columns. Each of the six sample columns per pixel is intersected with
        /// the outline; an inside span that contains no row sample (originY + py + rowOffset) is a
        /// dropout, and that half-lamp is turned on in the row holding the span's midpoint.
        /// <para>Measured on GDI with the slab probe: once the probe's prep carries Times' own
        /// `SCANCTRL 303 / SCANTYPE 1`, a slab of ANY height down to 1/16px renders as a full row;
        /// without it nothing below half a row does. It is what keeps Times New Roman's serifs --
        /// the face's ClearType branch leaves them 0.22px tall for IUP and relies on the scan
        /// converter. GDI's ink lands in the serif row itself, hence the midpoint rule. Stubs
        /// (rule 4) are not distinguished.</para></summary>
        /// <summary>GDI's dropout control, read out of fontdrvhost's scan converter
        /// (fsc_FillGlyph -> fsc_CalcLine -> fsc_FillBitMap -> LookForDropouts ->
        /// DoVertDropout / DoHorizDropout, with the helpers VertCrossings 0x1400426c0,
        /// HorizCrossings 0x140042270, GetBitAbs 0x140042180, SetBitAbs 0x140042580).
        /// <para>It all happens in the scan converter's own frame: one bitmap column per
        /// horizontal sample (six per pixel, two per lamp), one row per vertical sub-row, and
        /// y UP -- SetBitAbs addresses row y at (yMax-1-y)*rowBytes, so the bitmap is stored
        /// top-down but indexed bottom-up. Coordinates are 26.6 and a sample centre is 64j+32.</para>
        /// <para>fsc_CalcLine files every crossing under an INTEGER scanline index, and
        /// everything downstream -- the fill, the dropout test, the neighbour counts -- compares
        /// those integers, never the positions. The index depends on the edge's DIRECTION:
        /// a contour is clockwise in y-up, so the bottom edge runs leftwards and the left edge
        /// upwards ("on" crossings, `((v-1+32)&~63)+32)>>6`, the first centre AT OR past the
        /// crossing) while the top edge runs rightwards and the right edge downwards ("off",
        /// `((v+32)&~63)+32)>>6`, the first centre STRICTLY past it). So a span fills
        /// [on, off) and a dropout is on == off: the span holds no sample at all. The two
        /// tie-breaks differ only when a crossing lands exactly on a centre, which is why this
        /// matters at all -- with one sub-row per pixel the centres are the half-pixels, and
        /// hinted outlines sit on those constantly.</para>
        /// <para>For each dropout at column C between the centres R-1 and R:</para>
        /// <list type="bullet">
        /// <item>stubs (SCANTYPE bit 0): the feature must CONTINUE on both sides --
        /// left = crossings of column C-1 filed under R, plus crossings of rows R and R-1 filed
        /// under C; right = the same with C+1; each sum must be >= 2, counting both the on and
        /// the off list;</item>
        /// <item>nothing is drawn if (C, R) or (C, R-1) is already set;</item>
        /// <item>simple fills R-1, smart fills floor(mid - 1/128) from the two exact crossing
        /// positions; either way clamped into [yMin, yMax).</item>
        /// </list>
        /// <para>Rows go first (DoHorizDropout, the mirror image: last row first, crossings left
        /// to right, filling column C-1 or the midpoint) and their bits are visible to the column
        /// pass, which runs left to right taking each column's crossings from the LAST back.
        /// Returns the bits set, as (sample column, sub-row from the top).</para></summary>
        private static List<(int Col, int SubRow)> GdiDropoutFills(List<List<Vector2>> polys,
            FillRule fillRule, int originX, int originY, int width, int height, int nSub)
        {
            int nCols = width * SubpixelsPerPixel * 2, nRows = height * nSub;
            int scanType = DropoutForRun - 1;
            bool stubs = s_dropoutStubs && (scanType & 1) != 0, smart = (scanType & 4) != 0;
            // Into the scan converter's frame: sample q at x'=q+1/2, sub-row j (counted from the
            // top of our bitmap) at y'=nRows-j-1/2.
            var P = new List<Vector2[]>(polys.Count);
            foreach (List<Vector2> poly in polys)
            {
                var a = new Vector2[poly.Count];
                for (int i = 0; i < a.Length; i++)
                    a[i] = new Vector2((poly[i].X - originX) * 6f + 0.5f - GdiSamplePhase,
                                       nRows - ((poly[i].Y - originY) * nSub + 0.5f - GdiSubrowPhase));
                P.Add(a);
            }
            // WHICH SAMPLE INDEX A CROSSING IS RECORDED AT. floor(v-0.5)+1 -- the first sample
            // strictly after it -- so a run covers [on, off) and a span reads { s : A < s <= B }.
            // fsc_CalcLine@1400435a0 sets its runs up as first = (((v+32) & ~63) + 32) >> 6 and
            // last = (v'-33) >> 6, which over every v are the smallest sample STRICTLY GREATER
            // than v and the largest STRICTLY LESS than v'; the horizontal-edge case makes the
            // same point in one line, biasing y by -1 for a right-to-left edge so that the two
            // directions land either side of a tie.
            // <para>OnIdx keeps ceil(v-0.5), which differs only for a crossing exactly on a
            // sample, and 306 measured rows say to leave it: the fill it has to agree with tests
            // a span [A, B] and Verdana Regular at 19ppem scores 74 out of 34,283,819 ink that
            // way against 9,139 with the start made exclusive. A row that exact is not using the
            // wrong rule. Making OnIdx floor(v-0.5)+1 to match the reading measures 607,089 and
            // ten failures; making the fill match it measures 895,745 and 121. So the tie is
            // decided somewhere this arithmetic does not reach -- the DDA's per-step rounding in
            // the loop at 140043900, still unread -- and the pair we have is self-consistent.</para>
            // <para>Changing OffIdx to ceil(v-0.5) was tried as well, on the strength of a first
            // and wrong reading of the setup above. It measured -6,390 on Times New Roman
            // Regular, which looked like a result and was a symptom: it reached the bar through
            // the column pass's stub crossing counts. s_rowEdgeExtremum fixes the same bar in the
            // fill where it belongs, for the same -6,390 and with no ratchet worse, after which
            // the OffIdx change measures EXACTLY ZERO further and still costs three. Removed.</para>
            static int OnIdx(float v) => (int) MathF.Ceiling(v - 0.5f);
            static int OffIdx(float v) => (int) MathF.Floor(v - 0.5f) + 1;

            // xMin/xMax/yMin/yMax are the GLYPH's box, not the target's: fs_FindBitMapSize sizes
            // the bitmap to the outline, so the samples run from the first centre inside it to the
            // first centre past it. Every bound below is one of those four, and the fill row is
            // CLAMPED into them -- which is why a sliver sitting on the baseline is drawn in the
            // bottom row of the glyph and not, as it was here, in the row underneath it (the 'X'
            // feet each put a lamp below the baseline, where GDI's bitmap does not even reach).
            float xLo = float.MaxValue, xHi = float.MinValue, yLo = float.MaxValue, yHi = float.MinValue;
            foreach (Vector2[] a in P)
                foreach (Vector2 pt in a)
                {
                    if (pt.X < xLo) xLo = pt.X;
                    if (pt.X > xHi) xHi = pt.X;
                    if (pt.Y < yLo) yLo = pt.Y;
                    if (pt.Y > yHi) yHi = pt.Y;
                }
            // The box is rounded OUTWARD to whole samples, not to centres: a feature thinner
            // than one sample still gets a row to live in, which is the whole point of dropout
            // control. The slab probe says so outright -- GDI draws a FULL row for every slab
            // from 1/16px up to half a sample (17.77 ink at 16ppem where we drew nothing),
            // because rounding to centres collapses the box and takes the row away.
            int xMin = Math.Max(0, (int) MathF.Floor(xLo)), xMax = Math.Min(nCols, (int) MathF.Ceiling(xHi));
            int yMin = Math.Max(0, (int) MathF.Floor(yLo)), yMax = Math.Min(nRows, (int) MathF.Ceiling(yHi));
            if (xMin >= xMax || yMin >= yMax) return new List<(int, int)>();

            // Four crossing lists, exactly the four arrays the scan converter keeps: per column
            // the y indices where ink starts and ends, per row the x indices. Each is sorted
            // ascending (AddVertSimpleScan inserts in order), and each entry keeps its exact
            // position too, for the smart fill's midpoint.
            var colOn = new List<(int I, float V)>[nCols];
            var colOff = new List<(int I, float V)>[nCols];
            var rowOn = new List<(int I, float V)>[nRows];
            var rowOff = new List<(int I, float V)>[nRows];
            // Which list a crossing joins is decided by the edge's DIRECTION, not by which side
            // of the ink it is on: fsc_CalcLine builds a quadrant number (ascending y = 1 or 2,
            // descending = 3 or 4; x decreasing adds a turn) and picks the pair of adders from
            // it -- ascending y feeds the row ON list, decreasing x the column ON list. On a
            // clockwise contour, which is what a real face has, that IS the bottom and the left
            // edge. On a contour wound the other way the two lists swap, GDI included: the slab
            // probe is wound counter-clockwise, and GDI renders its half-sample-tall slab as a
            // dropout over a row it would otherwise have sampled -- 16.70 ink, the stub-excluded
            // fill, not the 17.77 of a sampled row. Deriving the roles from the winding instead
            // (tried, and identical on every real face) loses exactly that.
            for (int C = 0; C < nCols; C++)
            {
                var on = new List<(int, float)>(); var off = new List<(int, float)>();
                float sx = C + 0.5f;
                foreach (Vector2[] a in P)
                    for (int i = 0; i < a.Length; i++)
                    {
                        Vector2 p = a[i], q = a[(i + 1) % a.Length];
                        if (p.X == q.X) continue;
                        float lo = MathF.Min(p.X, q.X), hi = MathF.Max(p.X, q.X);
                        if (sx < lo || sx >= hi) continue;
                        float v = p.Y + (sx - p.X) / (q.X - p.X) * (q.Y - p.Y);
                        if (q.X < p.X) on.Add((OnIdx(v), v)); else off.Add((OffIdx(v), v));
                    }
                on.Sort(static (u, v) => u.Item1.CompareTo(v.Item1));
                off.Sort(static (u, v) => u.Item1.CompareTo(v.Item1));
                colOn[C] = on; colOff[C] = off;
            }
            for (int R = 0; R < nRows; R++)
            {
                var on = new List<(int, float)>(); var off = new List<(int, float)>();
                float sy = R + 0.5f;
                foreach (Vector2[] a in P)
                    for (int i = 0; i < a.Length; i++)
                    {
                        Vector2 p = a[i], q = a[(i + 1) % a.Length];
                        if (p.Y == q.Y) continue;
                        float lo = MathF.Min(p.Y, q.Y), hi = MathF.Max(p.Y, q.Y);
                        if (sy < lo || sy >= hi) continue;
                        float v = p.X + (sy - p.Y) / (q.Y - p.Y) * (q.X - p.X);
                        if (q.Y > p.Y) on.Add((OnIdx(v), v)); else off.Add((OffIdx(v), v));
                    }
                on.Sort(static (u, v) => u.Item1.CompareTo(v.Item1));
                off.Sort(static (u, v) => u.Item1.CompareTo(v.Item1));
                rowOn[R] = on; rowOff[R] = off;
            }
            // VertCrossings / HorizCrossings walk the on and off lists together and count both.
            static int Count(List<(int I, float V)>[] on, List<(int I, float V)>[] off, int at, int idx)
            {
                if (at < 0 || at >= on.Length) return 0;
                int n = 0;
                foreach ((int I, float V) e in on[at]) if (e.I == idx) n++;
                foreach ((int I, float V) e in off[at]) if (e.I == idx) n++;
                return n;
            }

            // The bitmap as the ordinary fill leaves it (fsc_FillBitMap runs before
            // LookForDropouts), then updated by every dropout fill so the "already on" tests see it.
            var bits = new bool[nCols * nRows];
            for (int R = 0; R < nRows; R++)
            {
                List<(int I, float V)> on = rowOn[R], off = rowOff[R];
                for (int k = 0; k < on.Count && k < off.Count; k++)
                    for (int C = Math.Max(0, on[k].I); C < Math.Min(nCols, off[k].I); C++)
                        bits[R * nCols + C] = true;
            }
            bool Bit(int C, int R) => C >= 0 && C < nCols && R >= 0 && R < nRows && bits[R * nCols + C];
            var fills = new List<(int, int)>();
            void Set(int C, int R)
            {
                if (C < 0 || C >= nCols || R < 0 || R >= nRows) return;
                if (bits[R * nCols + C]) return;
                bits[R * nCols + C] = true;
                fills.Add((C, nRows - 1 - R));
            }
            // Rows first, the last one first, each walked from its first crossing: DoHorizDropout.
            for (int R = nRows - 1; R >= 0; R--)
            {
                List<(int I, float V)> on = rowOn[R], off = rowOff[R];
                for (int k = 0; k < on.Count && k < off.Count; k++)
                {
                    int C = on[k].I;
                    if (off[k].I != C || C < xMin || C > xMax) continue;
                    string? why = null;
                    if (stubs)
                    {
                        if (Count(rowOn, rowOff, R + 1, C)
                            + Count(colOn, colOff, C - 1, R + 1)
                            + Count(colOn, colOff, C, R + 1) < 2) why = "stub-above";
                        else if (Count(rowOn, rowOff, R - 1, C)
                            + Count(colOn, colOff, C - 1, R)
                            + Count(colOn, colOff, C, R) < 2) why = "stub-below";
                    }
                    if (why is null && C > xMin && Bit(C - 1, R)) why = "on-left";
                    if (why is null && C < xMax && Bit(C, R)) why = "on-right";
                    int fc = smart ? (int) MathF.Floor((on[k].V + off[k].V) * 0.5f - 1f / 128f) : C - 1;
                    if (fc < xMin) fc = xMin;
                    if (fc >= xMax) why ??= "past-right";
                    if (s_dropoutTrace)
                        Console.Error.WriteLine($"DROP H row={R} C={C} x=({on[k].V:F3},{off[k].V:F3})"
                            + $" -> {why ?? $"fill col {fc}"}");
                    if (why != null) continue;
                    Set(fc, R);
                }
            }
            // Then the columns, left to right, each from its LAST crossing: DoVertDropout.
            for (int C = 0; C < nCols; C++)
            {
                List<(int I, float V)> on = colOn[C], off = colOff[C];
                for (int k = Math.Min(on.Count, off.Count) - 1; k >= 0; k--)
                {
                    int R = on[k].I;
                    if (off[k].I != R || R < yMin || R > yMax) continue;
                    string? why = null;
                    if (stubs)
                    {
                        if (Count(colOn, colOff, C - 1, R)
                            + Count(rowOn, rowOff, R, C)
                            + Count(rowOn, rowOff, R - 1, C) < 2) why = "stub-left";
                        else if (Count(colOn, colOff, C + 1, R)
                            + Count(rowOn, rowOff, R, C + 1)
                            + Count(rowOn, rowOff, R - 1, C + 1) < 2) why = "stub-right";
                    }
                    if (why is null && R > yMin && Bit(C, R - 1)) why = "on-below";
                    if (why is null && R < yMax && Bit(C, R)) why = "on-above";
                    int fr = smart ? (int) MathF.Floor((on[k].V + off[k].V) * 0.5f - 1f / 128f) : R - 1;
                    if (fr < yMin) fr = yMin;
                    if (fr >= yMax) why ??= "past-top";
                    if (s_dropoutTrace)
                        Console.Error.WriteLine($"DROP V col={C} R={R} y=({on[k].V:F3},{off[k].V:F3})"
                            + $" -> {why ?? $"fill row {fr}"}");
                    if (why != null) continue;
                    Set(C, fr);
                }
            }
            return fills;
        }

        private static byte[] GdiTableFilterRowset(List<List<Vector2>> polys, FillRule fillRule,
                                                   int originX, int originY, int width, int height,
                                                   float rowOffset, int nSub = 1, int sI = 0,
                                                   List<(int Col, int SubRow)>? dropoutFills = null)
        {
            int subWidth = width * SubpixelsPerPixel;
            var lamp = new byte[subWidth * height];
            var spans = new List<(float A, float B)>();
            var cross = new List<(float X, int Dir)>();

            // WHICH VERTICES ARE LOCAL MAXIMA IN Y, horizontal runs collapsed: the nearest
            // differing y before and after are both smaller. See s_rowEdgeExtremum.
            bool[][]? maxY = null;
            if (s_rowEdgeExtremum)
            {
                maxY = new bool[polys.Count][];
                for (int p = 0; p < polys.Count; p++)
                {
                    List<Vector2> poly = polys[p];
                    int m = poly.Count;
                    var flags = new bool[m];
                    for (int v = 0; v < m; v++)
                    {
                        float y = poly[v].Y, py = y, ny = y;
                        int k = v;
                        for (int step = 0; step < m; step++)
                        {
                            k = (k - 1 + m) % m;
                            if (poly[k].Y != y) { py = poly[k].Y; break; }
                        }
                        k = v;
                        for (int step = 0; step < m; step++)
                        {
                            k = (k + 1) % m;
                            if (poly[k].Y != y) { ny = poly[k].Y; break; }
                        }
                        flags[v] = py < y && ny < y;
                    }
                    maxY[p] = flags;
                }
            }
            for (int py = 0; py < height; py++)
            {
                float sy = originY + py + rowOffset;
                cross.Clear();
                for (int pi = 0; pi < polys.Count; pi++)
                {
                    List<Vector2> poly = polys[pi];
                    for (int i2 = 0; i2 < poly.Count; i2++)
                    {
                        Vector2 a = poly[i2], b = poly[(i2 + 1) % poly.Count];
                        if (a.Y == b.Y) continue;
                        float lo = MathF.Min(a.Y, b.Y), hi = MathF.Max(a.Y, b.Y);
                        // WPF_CT_ROWEDGE=both: a row sample lying exactly on EITHER end of an
                        // edge belongs to it, which is the rule this same function already uses
                        // across a row -- a span is tested [A, B], inclusive at both ends. The
                        // vertical test was (lo, hi], inclusive at one end only, so the two axes
                        // disagreed about a tie. See the note at s_rowEdgeBoth.
                        if (s_rowEdgeStrict ? (sy <= lo || sy >= hi)
                           : s_rowEdgeBoth ? (sy < lo || sy > hi)
                           : s_rowEdgeGdi ? (sy <= lo || sy > hi)
                                          : (sy < lo || sy >= hi)) continue;
                        // A SAMPLE ON A LOCAL MAXIMUM IN Y IS NOT A CROSSING. See
                        // the note at s_rowEdgeExtremum.
                        if (maxY is not null && sy == hi
                            && maxY[pi][b.Y > a.Y ? (i2 + 1) % poly.Count : i2]) continue;
                        float t = (sy - a.Y) / (b.Y - a.Y);
                        cross.Add((a.X + t * (b.X - a.X), b.Y > a.Y ? 1 : -1));
                    }
                }
                cross.Sort(static (u, v) => u.X.CompareTo(v.X));
                spans.Clear();
                if (fillRule == FillRule.NonZero)
                {
                    int w = 0;
                    for (int i2 = 0; i2 < cross.Count - 1; i2++)
                    { w += cross[i2].Dir; if (w != 0) spans.Add((cross[i2].X, cross[i2 + 1].X)); }
                }
                else
                    for (int i2 = 0; i2 + 1 < cross.Count; i2 += 2)
                        spans.Add((cross[i2].X, cross[i2 + 1].X));
                int rowBase = py * subWidth;
                for (int c = 0; c < subWidth; c++)
                {
                    int cnt = 0;
                    for (int half = 0; half < 2; half++)
                    {
                        float sx = originX + (c * 2 + half + GdiSamplePhase) / 6f;
                        foreach ((float A, float B) sp in spans)
                            // THE RIGHT END IS INCLUSIVE AND A REAL GLYPH SAYS SO OUTRIGHT.
                            // Verdana 'd' at 19ppem scores ZERO against GDI with this test; its
                            // right stem reads `493` in GDI's raster and in ours, and `491` the
                            // moment the end is made exclusive (2,055 for the glyph). 'g', 'q',
                            // 'W' and 'Z' at the same size are the same story, and over the whole
                            // holdout the flip costs 598,774 -> 866,561 with 233 of 306 rows
                            // worse. That is not a compensation; a row cannot be 74 out of
                            // 34,283,819 ink (Verdana Regular at 19) on the wrong rule.
                            // <para>THE SYNTHETIC PROBE DISAGREES, AND IT IS NOT THE RULE.
                            // CoverageAtACrossing reaches zero differing lamps of 11,675 only with
                            // the end made exclusive, and exactly one of its bars is responsible:
                            // the one 240 font units wide, whose right edge lands on 6.25px --
                            // 400/64, a quarter pixel, and quarter pixels are the ONLY positions
                            // where a 26.6 coordinate can sit on a lamp sample (16 and 48 mod 64;
                            // the samples are at odd twelfths). The other four bars end at 310,
                            // 340, 370 and 430 sixty-fourths, none of them a tie, and all four
                            // agree with GDI under this test. So the probe's residual is ONE
                            // coordinate, in the UNHINTED path those bars use (they carry no glyph
                            // program), where GDI's edge sits a sixty-fourth left of ours -- not a
                            // rule the hinted path shares.</para>
                            // <para>Which retires "our fitted x is wrong and it is worth 275,000",
                            // written one commit earlier off the synthetic result alone. It is
                            // worth nothing; the rule was already right.</para>
                            // Originally: INCLUSIVE AT BOTH ENDS, which is what fsc_FillBitMap does. A row's
                            // span fills the indices [on, off) where `on` is the first sample centre
                            // AT OR past the left boundary and `off` the first STRICTLY past the
                            // right one -- so a sample landing exactly on the RIGHT edge is inside,
                            // where we had it outside. It bites whenever a fitted edge lands on a
                            // sample, i.e. at every quarter pixel, since the samples sit at odd
                            // twelfths. WPF_CT_SPANEND=old restores the half-open test.
                            // A SAMPLE EXACTLY ON THE LEFT BOUNDARY: we count it in, GDI counts it
                            // out, and the difference is REAL but must not be "fixed" here yet.
                            // <para>Measured with HowGdiWeighsAQuadraticArc, a program-free
                            // synthetic arc walked across a pixel in sixty-fourths. At every
                            // offset but one our 24-shape raster is BYTE-IDENTICAL to GDI's. The
                            // exception is the offset where the straight chord lands at exactly
                            // 7.75px, which is exactly sample 4 of six, and there GDI reads 292.11
                            // lamps against our 360.65. One sixty-fourth either side and the two
                            // agree exactly again (345.71 and 298.38, both ways), so it is a TIE
                            // and not a placement difference -- and our curve rasterization is
                            // otherwise exact, which is the thing that probe was built to
                            // settle.</para>
                            // <para>Ties are not rare: the samples sit at odd twelfths of a pixel,
                            // of which two per pixel (4/16 and 12/16) fall on the ClearType
                            // sixteenth grid that a fitted edge is quantised to -- one position in
                            // eight. But WPF_CT_SPANSTART=out, which makes the span (A, B] and
                            // matches GDI on the probe, costs 290,465 on the real holdout
                            // (901,600 against 611,135). It can only do that if our FITTED x at
                            // those positions is not GDI's, so that the inclusive test has been
                            // compensating. Flipping it is right only after that is fixed; until
                            // then this is a known, measured, deliberate difference.</para>
                            if ((s_spanStartExclusive ? sx > sp.A : sx >= sp.A)
                                && (s_spanEndInclusive ? sx <= sp.B : sx < sp.B))
                            { cnt++; break; }
                    }
                    lamp[rowBase + c] = (byte)cnt;
                }
            }
            if (dropoutFills != null)
                foreach ((int Col, int SubRow) f in dropoutFills)
                {
                    if (f.SubRow % nSub != sI) continue;
                    int idx = (f.SubRow / nSub) * subWidth + f.Col / 2;
                    if (lamp[idx] < 2) lamp[idx]++;
                }
            var rgba = new byte[width * height * 4];
            for (int py = 0; py < height; py++)
            {
                int rowBase = py * subWidth;
                for (int x = 0; x < width; x++)
                {
                    int o = (py * width + x) * 4;
                    int total = 0;
                    // THE OTHER TABLE. ulClearTypeFilter_6x1 chooses between two 243-entry palettes on
                    // its third argument -- `puVar4 = &UNK_1400a7a60; if (param_3 != 0) puVar4 =
                    // &DAT_1400a7960;` -- and the 6x5 filter passes the same argument straight through,
                    // loading it at the call site from a per-render field (`ldr w2,[x21,#0x40]`). The
                    // plain table is the three-lamp box sum below; the OTHER one carries 1.5021x as much
                    // ink over all 243 indices, and it saturates, so a fully covered stem is untouched
                    // while an all-partial glyph gets half as much ink again. Diagonals are all partial
                    // coverage, which is why they were the only thing short.
                    if (ContrastFilterForRun)
                    {
                        int c0 = x * SubpixelsPerPixel;
                        int i0 = c0 - 1 >= 0 ? lamp[rowBase + c0 - 1] : 0;
                        int i4 = c0 + 3 < subWidth ? lamp[rowBase + c0 + 3] : 0;
                        int idx = ((i0 * 3 + lamp[rowBase + c0]) * 3 + lamp[rowBase + c0 + 1]) * 3;
                        idx = (idx + lamp[rowBase + c0 + 2]) * 3 + i4;
                        for (int L = 0; L < SubpixelsPerPixel; L++)
                            rgba[o + (LampsRunBlueFirst ? 2 - L : L)] = GdiContrastLevels[idx * 3 + L];
                        continue;
                    }
                    for (int L = 0; L < SubpixelsPerPixel; L++)
                    {
                        int c = x * SubpixelsPerPixel + L;
                        int w0 = c - 2 >= 0 ? lamp[rowBase + c - 2] : 0;
                        int w1 = c - 1 >= 0 ? lamp[rowBase + c - 1] : 0;
                        int w2 = lamp[rowBase + c];
                        int w3 = c + 1 < subWidth ? lamp[rowBase + c + 1] : 0;
                        // ulClearTypeFilter_6x1's table at fontdrvhost+0xa7a60 IS a per-channel three-lamp
                        // BOX SUM -- verified against all 243 base-3 indices. GDI forms ONE index per pixel
                        // (prev.c0, this.c2, this.c1, this.c0, next.c2, which are five CONTIGUOUS lamps) and
                        // the entry decodes, through the table at 0xa7420, to three channel levels: 0..6 each,
                        // where channel k is the sum of the three lamps centred on lamp k. The outer two of the
                        // five only feed the OTHER two channels, so the window per lamp is three, not five.
                        int lvl = w1 + w2 + w3;
                        if (lvl > 6) lvl = 6;
                        rgba[o + (LampsRunBlueFirst ? 2 - L : L)] = (byte) lvl;
                    }
                }
            }
            return rgba;
        }

        internal static readonly bool FilterBeforeVerticalAverage =
            Environment.GetEnvironmentVariable("WPF_VFILT_FIRST") != "0";

        internal static readonly bool PostVerticalCoverage =
            Environment.GetEnvironmentVariable("WPF_VCOV_POST") == "1";

        /// <summary>Scale each filtered lamp by its own column vertical coverage. GDI's lamps for
        /// a sub-pixel-tall feature come out UNIFORM across a pixel (Tahoma I@8 serif: 58,58,58)
        /// where ours cannot, because we fold vertical coverage into the samples BEFORE the
        /// horizontal filter and the filter mixes neighbours of differing coverage. Filtering a
        /// vertically FULL row and scaling after keeps a pixel's three lamps in one ratio.</summary>
        private static void ScaleByVerticalCoverage(byte[] rgba, byte[] vFrac, int width, int height)
        {
            int subWidth = width * SubpixelsPerPixel;
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    int o = (y * width + x) * 4;
                    int total = 0;
                    for (int lamp = 0; lamp < SubpixelsPerPixel; lamp++)
                    {
                        int c = y * subWidth + x * SubpixelsPerPixel + lamp;
                        int idx = o + (LampsRunBlueFirst ? 2 - lamp : lamp);
                        rgba[idx] = (byte)(rgba[idx] * vFrac[c] / 255);
                        total += rgba[idx];
                    }
                    rgba[o + 3] = (byte)(total / SubpixelsPerPixel);
                }
        }

        private static byte[] FilterSubpixels(byte[] samples, int width, int height)
        {
            var rgba = new byte[width * height * 4];
            int subWidth = width * SubpixelsPerPixel;
            int radius = SubpixelFilter.Length / 2;

            for (int y = 0; y < height; y++)
            {
                int sampleRow = y * subWidth;
                int outRow = y * width * 4;
                for (int x = 0; x < width; x++)
                {
                    int outIndex = outRow + x * 4;
                    int total = 0;
                    for (int lamp = 0; lamp < SubpixelsPerPixel; lamp++)
                    {
                        int centre = x * SubpixelsPerPixel + lamp;
                        float sum = 0f;
                        for (int t = -radius; t <= radius; t++)
                        {
                            int s = centre + t;
                            if (s < 0 || s >= subWidth) continue;   // off the mask is bare paper
                            sum += samples[sampleRow + s] * SubpixelFilter[t + radius];
                        }
                        int value = Math.Clamp((int)MathF.Round(sum * SubpixelGain), 0, 255);
                        // The filter works in SPACE, so only the colour each lamp is handed to
                        // changes on a BGR panel: the leftmost third is blue there, not red.
                        rgba[outIndex + (LampsRunBlueFirst ? 2 - lamp : lamp)] = (byte)value;
                        total += value;
                    }
                    // Alpha is what the destination is dimmed by where the three disagree; the mean of
                    // the lamps is what the pixel's brightness comes to.
                    rgba[outIndex + 3] = (byte)(total / SubpixelsPerPixel);
                }
            }
            return rgba;
        }

        /// <summary>
        /// Rasterizes the path into a fixed-size coverage buffer aligned to device
        /// pixels (origin 0,0). Used to build a full-target clip mask.
        /// </summary>
        public static byte[] RasterizeInto(PathGeometry path, int width, int height)
            => RasterizeInto(path, width, height, 0, 0);

        /// <summary>
        /// Rasterizes the path into a fixed-size coverage buffer whose pixel (0,0)
        /// maps to device pixel (<paramref name="originX"/>, <paramref name="originY"/>).
        /// Used to build a region-sized clip/opacity mask aligned to a card-sized layer.
        /// </summary>
        public static byte[] RasterizeInto(PathGeometry path, int width, int height, int originX, int originY,
            float tolerance = 0f)
        {
            List<List<Vector2>> contours = Flatten(path, tolerance);
            return FillCoverage(contours, path.FillRule, originX, originY, width, height);
        }

        /// <summary>THRESHOLD EACH ROW, THEN AVERAGE THEM -- not the other way round.
        /// <para>The half-lamp threshold is a binarization and it is right: it sharpens a vertical
        /// edge and it is what makes our lamps GDI's. But it was applied to rows that had ALREADY
        /// been averaged together, which conflates the two axes. A horizontal bar covering 28% of a
        /// pixel row averages to about 71, thresholds to NOTHING, and the bar disappears; with two
        /// coarse samples the same bar rounds the other way and comes out solid black.</para>
        /// <para>Segoe UI's 'H' at 8ppem showed both. Its two stems matched GDI byte for byte on
        /// every row while its CROSSBAR was absent at sixteen samples and full black at two, where
        /// GDI draws it across two rows at 219 and 182.</para>
        /// <para>The comment on SubpixelRows states the assumption that made the old order safe --
        /// "y hinting has already put every horizontal edge on a pixel boundary and there is no
        /// partial row" -- and that is exactly false where the face's gasp declines to grid-fit:
        /// below 9ppem for all six specimen faces, and below 11 for Consolas, which is why Consolas
        /// is the one face that over-inks at 10ppem.</para>
        /// <para>One row is the overwhelmingly common case and takes the same path it always did.
        /// </para></summary>
        /// <summary>One vertical sample at a given offset within the row, thresholded and
        /// collapsed exactly as RowsThenThreshold does for a single row.</summary>
        private static byte[] RowsThenThresholdAt(List<List<Vector2>> contours, FillRule fillRule,
                                                  int originX, int originY, int subWidth, int height,
                                                  float offset)
        {
            byte[] one = FillCoverage(contours, fillRule, originX, originY,
                                      subWidth * HalfLamps, height, 1,
                                      MinStemSubpixels * HalfLamps, CentreSample, offset);
            one = HalfLamps > 1 ? CollapseHalfLamps(one, subWidth, height) : one;
            Quantize(one);
            return one;
        }

        private static byte[] RowsThenThreshold(List<List<Vector2>> contours, FillRule fillRule,
                                                int originX, int originY, int subWidth, int height,
                                                int rows)
        {
            if (rows < 1) rows = 1;
            byte[] One(float offset)
            {
                byte[] one = FillCoverage(contours, fillRule, originX, originY,
                                          subWidth * HalfLamps, height, 1,
                                          MinStemSubpixels * HalfLamps, CentreSample, offset);
                one = HalfLamps > 1 ? CollapseHalfLamps(one, subWidth, height) : one;
                Quantize(one);
                return one;
            }

            if (rows == 1) return One(0.5f);

            var accum = new int[subWidth * height];
            for (int r = 0; r < rows; r++)
            {
                byte[] one = One((r + 0.5f) / rows);
                for (int i = 0; i < accum.Length && i < one.Length; i++) accum[i] += one[i];
            }
            var samples = new byte[subWidth * height];
            for (int i = 0; i < samples.Length; i++) samples[i] = (byte) (accum[i] / rows);
            return samples;
        }

        private static byte[] FillCoverage(List<List<Vector2>> contours, FillRule fillRule, int originX, int originY, int width, int height, int verticalSamples = VerticalSamples, float minSpan = 0f, bool centreSample = false, float rowOffset = -1f)
        {
            var bytes = new byte[width * height];
            if (width <= 0 || height <= 0 || contours.Count == 0) return bytes;

            var edges = new List<Edge>();
            foreach (List<Vector2> c in contours)
            {
                for (int i = 0; i < c.Count; i++)
                {
                    Vector2 a = c[i];
                    Vector2 b = c[(i + 1) % c.Count]; // implicitly closed for fill
                    if (a.Y != b.Y) edges.Add(new Edge(a, b));
                }
            }

            // Rent the (largest) coverage scratch from the pool -- it's an internal accumulator returned
            // before this method exits, so it never escapes; this avoids a width*height*4 byte alloc per
            // rasterize (the dominant rasterizer allocation, e.g. animated/transformed shapes each frame).
            int area = width * height;
            float[] coverage = System.Buffers.ArrayPool<float>.Shared.Rent(area);
            Array.Clear(coverage, 0, area);
            if (verticalSamples < 1) verticalSamples = 1;
            float weight = 1f / verticalSamples;
            var crossings = new List<(float X, int Dir)>();

            for (int py = 0; py < height; py++)
            {
                int rowBase = py * width;
                for (int s = 0; s < verticalSamples; s++)
                {
                    // rowOffset places THE one sample when the caller is stepping the rows
                    // itself, so that each row can be thresholded before they are averaged.
                    float sampleY = originY + py
                                    + (rowOffset >= 0f ? rowOffset : (s + 0.5f) / verticalSamples);
                    crossings.Clear();
                    foreach (Edge e in edges)
                    {
                        float ymin = MathF.Min(e.Y0, e.Y1);
                        float ymax = MathF.Max(e.Y0, e.Y1);
                        if (sampleY < ymin || sampleY >= ymax) continue;
                        float t = (sampleY - e.Y0) / (e.Y1 - e.Y0);
                        float x = e.X0 + t * (e.X1 - e.X0);
                        crossings.Add((x, e.Y1 > e.Y0 ? 1 : -1));
                    }
                    if (crossings.Count < 2) continue;
                    crossings.Sort(static (a, b) => a.X.CompareTo(b.X));

                    if (fillRule == FillRule.NonZero)
                    {
                        int winding = 0;
                        for (int i = 0; i < crossings.Count - 1; i++)
                        {
                            winding += crossings[i].Dir;
                            if (winding != 0)
                                AddSpan(coverage, rowBase, width, originX, crossings[i].X, crossings[i + 1].X, weight, minSpan, centreSample);
                        }
                    }
                    else // EvenOdd
                    {
                        for (int i = 0; i < crossings.Count - 1; i++)
                            if ((i & 1) == 0)
                                AddSpan(coverage, rowBase, width, originX, crossings[i].X, crossings[i + 1].X, weight, minSpan, centreSample);
                    }
                }
            }

            for (int i = 0; i < bytes.Length; i++)
                bytes[i] = (byte)Math.Clamp((int)MathF.Round(coverage[i] * 255f), 0, 255);
            System.Buffers.ArrayPool<float>.Shared.Return(coverage);
            return bytes;
        }

        private static void AddSpan(float[] cov, int rowBase, int width, int originX, float xs, float xe, float weight,
                                    float minSpan = 0f, bool centreSample = false)
        {
            if (xe <= xs) return;

            // A MINIMUM STEM WIDTH, and only where it bites. A scanline span narrower than this is a
            // vertical stem crossed by this row -- a horizontal bar or a bowl gives a wide one and is
            // untouched -- so widening it about its own centre leaves bold and large sizes exactly as
            // they were, which is the whole reason this belongs here and not in a filter. See
            // MinStemSubpixels for the measurement.
            if (minSpan > 0f && xe - xs < minSpan)
            {
                float mid = (xs + xe) * 0.5f;
                xs = mid - minSpan * 0.5f;
                xe = mid + minSpan * 0.5f;
            }

            float left = xs - originX;   // pixel-space
            float right = xe - originX;
            int p0 = Math.Max(0, (int)MathF.Floor(left));
            int p1 = Math.Min(width - 1, (int)MathF.Ceiling(right) - 1);
            for (int px = p0; px <= p1; px++)
            {
                if (centreSample)
                {
                    // GDI'S RULE, not ours: a bi-level scan conversion asks whether the sample
                    // point is INSIDE the outline, where we integrate the area and threshold it at
                    // half. For a straight edge the two are the same -- covering at least half a
                    // sample is exactly having the edge left of its centre -- which is why every
                    // straight stem already matched. They differ on a SLIVER narrower than half a
                    // sample that straddles the centre: area says no, inside says yes. That sliver
                    // is the leading edge of a curve, and losing it is why 'o', 'e' and 'c' sat half
                    // a pixel right of Windows' while 'l' and 'n' were exact.
                    float centre = px + 0.5f;
                    if (centre >= left && centre < right) cov[rowBase + px] += weight;
                    continue;
                }
                float overlap = MathF.Min(right, px + 1) - MathF.Max(left, px);
                if (overlap > 0f) cov[rowBase + px] += overlap * weight;
            }
        }

        /// <summary>Which GLYPH each figure of the run belongs to, set by the glyph-run path for
        /// the length of one coverage mask and null everywhere else. A single glyph needs none of
        /// this: one group is the whole path, which is what a null array means.</summary>
        internal static int[]? FigureGlyphIdsForRun;

        /// <summary>WPF_CT_DROPOUT_PERGLYPH=0 hands the dropout pass the whole run again, which is
        /// what it used to get. A bisection handle, and the way the win stays reproducible: on the
        /// corrected (untruncated) specimen, run-wide measures 503,498 and per-glyph 355,518.
        /// <para>The -31.7% first reported for this change (458,613 -> 313,163) was measured before
        /// the specimen's bitmap was widened, so both halves of it were understated; the honest
        /// figure on one scale is -29.4%.</para></summary>
        private static readonly bool s_dropoutPerGlyph =
            Environment.GetEnvironmentVariable("WPF_CT_DROPOUT_PERGLYPH") != "0";

        private static int GlyphOf(int[]? owners, List<int> contourFigures, int contour)
        {
            if (owners is null || contour >= contourFigures.Count) return 0;
            int fig = contourFigures[contour];
            return (uint) fig < (uint) owners.Length ? owners[fig] : 0;
        }

        private static List<List<Vector2>> Flatten(PathGeometry path, float tolerance)
            => Flatten(path, tolerance, null);

        /// <summary>As above, and when <paramref name="owners"/> is given it receives the index of
        /// the FIGURE each contour came from. Degenerate figures are dropped, so the two lists are
        /// not index-parallel and a caller that needs the correspondence has to ask for it.</summary>
        private static List<List<Vector2>> Flatten(PathGeometry path, float tolerance,
                                                   List<int>? owners)
        {
            var contours = new List<List<Vector2>>();
            int figureIndex = 0;
            foreach (PathFigure figure in path.Figures)
            {
                var pts = new List<Vector2> { figure.Start };
                Vector2 current = figure.Start;
                foreach (PathSegment seg in figure.Segments)
                {
                    current = AppendSegment(pts, current, seg, tolerance);
                }
                if (pts.Count >= 3)
                {
                    contours.Add(pts);
                    owners?.Add(figureIndex);
                }
                figureIndex++;
            }
            return contours;
        }

        /// <summary>
        /// Appends a segment's flattened points (excluding the start point, which the caller
        /// already holds) and returns the new current point. Curves are subdivided to
        /// <paramref name="tolerance"/> rather than a fixed step count, so a large curve stops
        /// faceting and a small one stops over-tessellating. Shared by every CPU flattening
        /// site so they cannot drift apart.
        /// </summary>
        internal static Vector2 AppendSegment(List<Vector2> pts, Vector2 current, PathSegment seg, float tolerance)
        {
            switch (seg)
            {
                case LineSegment l:
                    pts.Add(l.Point);
                    return l.Point;
                case QuadraticBezierSegment q:
                {
                    int n = CurveFlattener.QuadraticSteps(current, q.Control, q.Point, tolerance);
                    for (int i = 1; i <= n; i++) pts.Add(Quadratic(current, q.Control, q.Point, i / (float)n));
                    return q.Point;
                }
                case CubicBezierSegment c:
                {
                    int n = CurveFlattener.CubicSteps(current, c.Control1, c.Control2, c.Point, tolerance);
                    for (int i = 1; i <= n; i++) pts.Add(Cubic(current, c.Control1, c.Control2, c.Point, i / (float)n));
                    return c.Point;
                }
                default:
                    return current;
            }
        }

        private static Vector2 Quadratic(Vector2 p0, Vector2 c, Vector2 p1, float t)
        {
            float u = 1f - t;
            return u * u * p0 + 2f * u * t * c + t * t * p1;
        }

        private static Vector2 Cubic(Vector2 p0, Vector2 c1, Vector2 c2, Vector2 p1, float t)
        {
            float u = 1f - t;
            return u * u * u * p0 + 3f * u * u * t * c1 + 3f * u * t * t * c2 + t * t * t * p1;
        }
    }
}
