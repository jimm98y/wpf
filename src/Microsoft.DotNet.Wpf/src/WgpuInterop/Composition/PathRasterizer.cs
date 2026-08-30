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
            float tolerance = CurveFlattener.DefaultTolerance)
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
            float tolerance = CurveFlattener.DefaultTolerance)
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

        public static CoverageMask Rasterize(PathGeometry path, float tolerance = CurveFlattener.DefaultTolerance)
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
        /// GDI's ClearType output holds exactly seven distinct levels (k/6). Three-valued lamps
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

        public static SubpixelMask RasterizeSubpixel(PathGeometry path,
                                                     float tolerance = CurveFlattener.DefaultTolerance)
        {
            List<List<Vector2>> contours = Flatten(path, tolerance);
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
            byte[] samples = FillCoverage(contours, path.FillRule, originX * scale, originY,
                                          subWidth * HalfLamps, height,
                                          SubpixelRowsForRun > 0 ? SubpixelRowsForRun : SubpixelRows,
                                          MinStemSubpixels * HalfLamps);
            // Filtering the 6x samples STRAIGHT into lamps was tried, on the grounds that the paper
            // calls this "a 6x1 filtering technique" -- one operation, six samples in and three
            // lamps out -- where ours is two, thresholding each half-lamp at 128 and then running a
            // three-lamp box over the result. The threshold in the middle is a binarization the
            // documented pipeline does not have. Measured with the same curve and level cap in the
            // same order it costs 759,520 -> 803,759, and 822,317 / 841,529 at other gammas. The
            // threshold earns its place: it sharpens, and averaging the fine samples instead gives
            // away the edge.
            samples = HalfLamps > 1 ? CollapseHalfLamps(samples, subWidth, height) : samples;
            Quantize(samples);
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
                        int value = Math.Clamp((int)MathF.Round(sum), 0, 255);
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
            float tolerance = CurveFlattener.DefaultTolerance)
        {
            List<List<Vector2>> contours = Flatten(path, tolerance);
            return FillCoverage(contours, path.FillRule, originX, originY, width, height);
        }

        private static byte[] FillCoverage(List<List<Vector2>> contours, FillRule fillRule, int originX, int originY, int width, int height, int verticalSamples = VerticalSamples, float minSpan = 0f)
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
                    float sampleY = originY + py + (s + 0.5f) / verticalSamples;
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
                                AddSpan(coverage, rowBase, width, originX, crossings[i].X, crossings[i + 1].X, weight, minSpan);
                        }
                    }
                    else // EvenOdd
                    {
                        for (int i = 0; i < crossings.Count - 1; i++)
                            if ((i & 1) == 0)
                                AddSpan(coverage, rowBase, width, originX, crossings[i].X, crossings[i + 1].X, weight, minSpan);
                    }
                }
            }

            for (int i = 0; i < bytes.Length; i++)
                bytes[i] = (byte)Math.Clamp((int)MathF.Round(coverage[i] * 255f), 0, 255);
            System.Buffers.ArrayPool<float>.Shared.Return(coverage);
            return bytes;
        }

        private static void AddSpan(float[] cov, int rowBase, int width, int originX, float xs, float xe, float weight,
                                    float minSpan = 0f)
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
                float overlap = MathF.Min(right, px + 1) - MathF.Max(left, px);
                if (overlap > 0f) cov[rowBase + px] += overlap * weight;
            }
        }

        private static List<List<Vector2>> Flatten(PathGeometry path, float tolerance)
        {
            var contours = new List<List<Vector2>>();
            foreach (PathFigure figure in path.Figures)
            {
                var pts = new List<Vector2> { figure.Start };
                Vector2 current = figure.Start;
                foreach (PathSegment seg in figure.Segments)
                {
                    current = AppendSegment(pts, current, seg, tolerance);
                }
                if (pts.Count >= 3)
                    contours.Add(pts);
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
