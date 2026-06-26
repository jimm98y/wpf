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

            var coverage = new float[width * height];
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

                    if (path.FillRule == FillRule.NonZero)
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

            var bytes = new byte[width * height];
            for (int i = 0; i < bytes.Length; i++)
                bytes[i] = (byte)Math.Clamp((int)MathF.Round(coverage[i] * 255f), 0, 255);

            return new CoverageMask(bytes, width, height, originX, originY);
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
