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
        private const int BezierSteps = 24;          // flattening resolution

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
            out float minX, out float minY, out float maxX, out float maxY)
        {
            minX = float.MaxValue; minY = float.MaxValue; maxX = float.MinValue; maxY = float.MinValue;
            List<List<Vector2>> contours = Flatten(path);
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

        private const int CubicToQuadSpans = 4;   // quadratics per cubic (tangent-matched)

        /// <summary>
        /// Emits the path's outline as QUADRATIC Bézier segments (6 floats each: p0, control,
        /// p1) into <paramref name="segs"/>, and reports tight point bounds. A line is the
        /// degenerate quadratic (control = midpoint); cubics are split into a few
        /// tangent-matched quadratics. The GPU coverage shader (fs_coverage) solves scanline
        /// crossings analytically from these, so curves are flattened on the GPU, not here.
        /// Returns the segment count.
        /// </summary>
        public static int SegmentsToQuadratics(PathGeometry path, List<float> segs,
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
                            count += EmitQuad(segs, cur, (cur + l.Point) * 0.5f, l.Point, ref minX, ref minY, ref maxX, ref maxY);
                            cur = l.Point;
                            break;
                        case QuadraticBezierSegment q:
                            count += EmitQuad(segs, cur, q.Control, q.Point, ref minX, ref minY, ref maxX, ref maxY);
                            cur = q.Point;
                            break;
                        case CubicBezierSegment cb:
                            count += CubicToQuads(segs, cur, cb.Control1, cb.Control2, cb.Point, ref minX, ref minY, ref maxX, ref maxY);
                            cur = cb.Point;
                            break;
                    }
                }
                // Implicitly close the figure for filling.
                if (cur != start)
                    count += EmitQuad(segs, cur, (cur + start) * 0.5f, start, ref minX, ref minY, ref maxX, ref maxY);
            }
            return count;
        }

        /// <summary>
        /// Flattens a path's figures to CENTRE-LINE polyline segments (4 floats each: a.x, a.y,
        /// b.x, b.y) for GPU signed-distance stroking, and reports point bounds (unpadded). Open
        /// figures are not closed; closed figures add the wrap segment. Returns the segment count.
        /// </summary>
        public static int FlattenCenterlineSegments(PathGeometry path, List<float> segs,
            out float minX, out float minY, out float maxX, out float maxY)
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
                {
                    switch (seg)
                    {
                        case LineSegment l:
                            pts.Add(l.Point); cur = l.Point; break;
                        case QuadraticBezierSegment q:
                            for (int i = 1; i <= BezierSteps; i++) pts.Add(Quadratic(cur, q.Control, q.Point, i / (float)BezierSteps));
                            cur = q.Point; break;
                        case CubicBezierSegment c:
                            for (int i = 1; i <= BezierSteps; i++) pts.Add(Cubic(cur, c.Control1, c.Control2, c.Point, i / (float)BezierSteps));
                            cur = c.Point; break;
                    }
                }
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

        // Emits a quadratic segment, first splitting it at its y-extremum so every emitted segment
        // is y-MONOTONE. The GPU coverage shader relies on this for its robust endpoint-based
        // scanline crossing test (a non-monotone segment could cross a scanline twice, which the
        // single-root test would miscount).
        private static int EmitQuad(List<float> segs, Vector2 p0, Vector2 c, Vector2 p1,
            ref float minX, ref float minY, ref float maxX, ref float maxY)
        {
            float denom = p0.Y - 2f * c.Y + p1.Y;
            if (MathF.Abs(denom) > 1e-9f)
            {
                float ts = (p0.Y - c.Y) / denom;   // y-extremum parameter
                if (ts > 1e-4f && ts < 1f - 1e-4f)
                {
                    Vector2 m0 = Vector2.Lerp(p0, c, ts);
                    Vector2 m1 = Vector2.Lerp(c, p1, ts);
                    Vector2 mid = Vector2.Lerp(m0, m1, ts);
                    EmitQuadRaw(segs, p0, m0, mid, ref minX, ref minY, ref maxX, ref maxY);
                    EmitQuadRaw(segs, mid, m1, p1, ref minX, ref minY, ref maxX, ref maxY);
                    return 2;
                }
            }
            EmitQuadRaw(segs, p0, c, p1, ref minX, ref minY, ref maxX, ref maxY);
            return 1;
        }

        private static void EmitQuadRaw(List<float> segs, Vector2 p0, Vector2 c, Vector2 p1,
            ref float minX, ref float minY, ref float maxX, ref float maxY)
        {
            segs.Add(p0.X); segs.Add(p0.Y);
            segs.Add(c.X); segs.Add(c.Y);
            segs.Add(p1.X); segs.Add(p1.Y);
            AccQuadBounds(p0, c, p1, ref minX, ref minY, ref maxX, ref maxY);
        }

        // Tight bounds of a quadratic: endpoints plus the interior axis extrema.
        private static void AccQuadBounds(Vector2 p0, Vector2 c, Vector2 p1,
            ref float minX, ref float minY, ref float maxX, ref float maxY)
        {
            Acc(p0.X, p0.Y, ref minX, ref minY, ref maxX, ref maxY);
            Acc(p1.X, p1.Y, ref minX, ref minY, ref maxX, ref maxY);
            AccQuadAxisExtremum(p0.X, c.X, p1.X, p0, c, p1, axisX: true, ref minX, ref minY, ref maxX, ref maxY);
            AccQuadAxisExtremum(p0.Y, c.Y, p1.Y, p0, c, p1, axisX: false, ref minX, ref minY, ref maxX, ref maxY);
        }

        private static void AccQuadAxisExtremum(float a0, float a1, float a2, Vector2 p0, Vector2 c, Vector2 p1,
            bool axisX, ref float minX, ref float minY, ref float maxX, ref float maxY)
        {
            float denom = a0 - 2f * a1 + a2;
            if (MathF.Abs(denom) < 1e-9f) return;
            float t = (a0 - a1) / denom;
            if (t <= 0f || t >= 1f) return;
            float mt = 1f - t;
            Vector2 p = mt * mt * p0 + 2f * mt * t * c + t * t * p1;
            Acc(p.X, p.Y, ref minX, ref minY, ref maxX, ref maxY);
        }

        private static void Acc(float x, float y, ref float minX, ref float minY, ref float maxX, ref float maxY)
        {
            minX = MathF.Min(minX, x); minY = MathF.Min(minY, y);
            maxX = MathF.Max(maxX, x); maxY = MathF.Max(maxY, y);
        }

        // Splits a cubic into tangent-matched quadratics: evaluate the cubic and its derivative
        // at span boundaries; each quadratic's control is the intersection of the endpoint
        // tangents (midpoint if parallel). Far more accurate per segment than line flattening.
        private static int CubicToQuads(List<float> segs, Vector2 p0, Vector2 c1, Vector2 c2, Vector2 p1,
            ref float minX, ref float minY, ref float maxX, ref float maxY)
        {
            int n = 0;
            Vector2 prev = p0;
            Vector2 prevTan = CubicTangent(p0, c1, c2, p1, 0f);
            for (int i = 1; i <= CubicToQuadSpans; i++)
            {
                float t = i / (float)CubicToQuadSpans;
                Vector2 pt = CubicPoint(p0, c1, c2, p1, t);
                Vector2 tan = CubicTangent(p0, c1, c2, p1, t);
                Vector2 ctrl = TangentIntersect(prev, prevTan, pt, tan);
                n += EmitQuad(segs, prev, ctrl, pt, ref minX, ref minY, ref maxX, ref maxY);
                prev = pt; prevTan = tan;
            }
            return n;
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

        public static CoverageMask Rasterize(PathGeometry path)
        {
            List<List<Vector2>> contours = Flatten(path);
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
        public static byte[] RasterizeInto(PathGeometry path, int width, int height, int originX, int originY)
        {
            List<List<Vector2>> contours = Flatten(path);
            return FillCoverage(contours, path.FillRule, originX, originY, width, height);
        }

        private static byte[] FillCoverage(List<List<Vector2>> contours, FillRule fillRule, int originX, int originY, int width, int height)
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
            float weight = 1f / VerticalSamples;
            var crossings = new List<(float X, int Dir)>();

            for (int py = 0; py < height; py++)
            {
                int rowBase = py * width;
                for (int s = 0; s < VerticalSamples; s++)
                {
                    float sampleY = originY + py + (s + 0.5f) / VerticalSamples;
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
                                AddSpan(coverage, rowBase, width, originX, crossings[i].X, crossings[i + 1].X, weight);
                        }
                    }
                    else // EvenOdd
                    {
                        for (int i = 0; i < crossings.Count - 1; i++)
                            if ((i & 1) == 0)
                                AddSpan(coverage, rowBase, width, originX, crossings[i].X, crossings[i + 1].X, weight);
                    }
                }
            }

            for (int i = 0; i < bytes.Length; i++)
                bytes[i] = (byte)Math.Clamp((int)MathF.Round(coverage[i] * 255f), 0, 255);
            System.Buffers.ArrayPool<float>.Shared.Return(coverage);
            return bytes;
        }

        private static void AddSpan(float[] cov, int rowBase, int width, int originX, float xs, float xe, float weight)
        {
            if (xe <= xs) return;
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

        private static List<List<Vector2>> Flatten(PathGeometry path)
        {
            var contours = new List<List<Vector2>>();
            foreach (PathFigure figure in path.Figures)
            {
                var pts = new List<Vector2> { figure.Start };
                Vector2 current = figure.Start;
                foreach (PathSegment seg in figure.Segments)
                {
                    switch (seg)
                    {
                        case LineSegment l:
                            pts.Add(l.Point);
                            current = l.Point;
                            break;
                        case QuadraticBezierSegment q:
                            for (int i = 1; i <= BezierSteps; i++)
                                pts.Add(Quadratic(current, q.Control, q.Point, i / (float)BezierSteps));
                            current = q.Point;
                            break;
                        case CubicBezierSegment c:
                            for (int i = 1; i <= BezierSteps; i++)
                                pts.Add(Cubic(current, c.Control1, c.Control2, c.Point, i / (float)BezierSteps));
                            current = c.Point;
                            break;
                    }
                }
                if (pts.Count >= 3)
                    contours.Add(pts);
            }
            return contours;
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
