// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Stroke-to-fill: converts a path + pen into the filled region the stroke
// covers, then that region is rasterized exactly like any other filled path
// (so strokes anti-alias identically). The cross-platform analog of milcore's
// MilUtility_PathGeometryWiden.
//
// Each polyline segment becomes a rectangle offset by half the pen width. Joins
// (miter/bevel/round) fill the gap on the outer side of each corner; caps
// (butt/square/round) shape the ends of open figures. Dashes split a polyline
// into "on" runs that are each stroked as an open figure. Every emitted contour
// is normalized to one winding so the non-zero rule unions them (overlaps
// accumulate winding rather than cancelling).
//

using System;
using System.Collections.Generic;
using System.Numerics;

namespace Microsoft.Wpf.Interop.WebGpu.Composition
{
    internal static class PathStroker
    {
        private const int BezierSteps = 24;
        private const int DiscSegments = 24;

        public static PathGeometry Stroke(PathGeometry geometry, StrokeStyle style)
        {
            float half = (float)(style.Thickness / 2.0);
            var contours = new List<PathFigure>();
            if (half <= 0f) return new PathGeometry(FillRule.NonZero, contours);

            bool dashed = style.DashArray is { Length: > 0 } && Sum(style.DashArray) > 0;

            foreach (PathFigure figure in geometry.Figures)
            {
                List<Vector2> pts = FlattenFigure(figure);
                RemoveDuplicates(pts);
                if (pts.Count < 2)
                {
                    if (pts.Count == 1 && style.Cap == LineCap.Round) AddDisc(contours, pts[0], half);
                    continue;
                }

                if (dashed)
                {
                    foreach (List<Vector2> run in DashPolyline(pts, figure.Closed, style.DashArray!, style.DashOffset))
                        StrokePolyline(run, closed: false, style, half, contours);
                }
                else
                {
                    StrokePolyline(pts, figure.Closed, style, half, contours);
                }
            }

            return new PathGeometry(FillRule.NonZero, contours);
        }

        private static void StrokePolyline(List<Vector2> pts, bool closed, StrokeStyle style, float half, List<PathFigure> contours)
        {
            int n = pts.Count;
            if (n < 2) return;

            int segments = closed ? n : n - 1;
            for (int i = 0; i < segments; i++)
            {
                Vector2 a = pts[i];
                Vector2 b = pts[(i + 1) % n];
                Vector2 d = b - a;
                float len = d.Length();
                if (len < 1e-4f) continue;
                Vector2 normal = Perp(d) / len * half;
                AddContour(contours, new[] { a + normal, b + normal, b - normal, a - normal });
            }

            // Joins: every vertex for closed figures, interior vertices for open ones.
            int first = closed ? 0 : 1;
            int last = closed ? n - 1 : n - 2;
            for (int i = first; i <= last; i++)
            {
                Vector2 prev = pts[(i - 1 + n) % n];
                Vector2 cur = pts[i];
                Vector2 next = pts[(i + 1) % n];
                AddJoin(contours, prev, cur, next, half, style.Join, style.MiterLimit);
            }

            // Caps on open figures.
            if (!closed)
            {
                AddCap(contours, pts[1], pts[0], half, style.Cap);
                AddCap(contours, pts[n - 2], pts[n - 1], half, style.Cap);
            }
        }

        private static void AddCap(List<PathFigure> contours, Vector2 inner, Vector2 end, float half, LineCap cap)
        {
            Vector2 d = end - inner;
            float len = d.Length();
            if (len < 1e-4f) { if (cap == LineCap.Round) AddDisc(contours, end, half); return; }
            d /= len; // outward direction, past the end point

            switch (cap)
            {
                case LineCap.Round:
                    AddDisc(contours, end, half);
                    break;
                case LineCap.Square:
                {
                    Vector2 nrm = Perp(d) * half;
                    Vector2 ext = d * half;
                    AddContour(contours, new[] { end + nrm, end + nrm + ext, end - nrm + ext, end - nrm });
                    break;
                }
                // Butt: nothing.
            }
        }

        private static void AddJoin(List<PathFigure> contours, Vector2 prev, Vector2 cur, Vector2 next,
            float half, LineJoin join, double miterLimit)
        {
            Vector2 d0 = prev == cur ? Vector2.Zero : Vector2.Normalize(cur - prev);
            Vector2 d1 = next == cur ? Vector2.Zero : Vector2.Normalize(next - cur);
            if (d0 == Vector2.Zero || d1 == Vector2.Zero) return;

            float cross = d0.X * d1.Y - d0.Y * d1.X;
            if (MathF.Abs(cross) < 1e-4f) return; // straight (or anti-parallel): no join gap

            if (join == LineJoin.Round) { AddDisc(contours, cur, half); return; }

            // Outer side: derived so the apex lands on the convex side of the turn.
            float s = cross > 0f ? -1f : 1f;
            Vector2 pIn = cur + s * Perp(d0) * half;
            Vector2 pOut = cur + s * Perp(d1) * half;

            if (join == LineJoin.Miter && Intersect(pIn, d0, pOut, d1, out Vector2 m))
            {
                if ((m - cur).Length() <= miterLimit * half)
                {
                    AddContour(contours, new[] { cur, pIn, m, pOut });
                    return;
                }
            }

            AddContour(contours, new[] { cur, pIn, pOut }); // bevel (also the miter-limit fallback)
        }

        private static void AddDisc(List<PathFigure> contours, Vector2 center, float radius)
        {
            var ring = new Vector2[DiscSegments];
            for (int i = 0; i < DiscSegments; i++)
            {
                float t = i / (float)DiscSegments * MathF.PI * 2f;
                ring[i] = new Vector2(center.X + MathF.Cos(t) * radius, center.Y + MathF.Sin(t) * radius);
            }
            AddContour(contours, ring);
        }

        // Adds a contour, normalized to a consistent (positive-area) winding.
        private static void AddContour(List<PathFigure> contours, Vector2[] points)
        {
            if (SignedArea(points) < 0f)
                Array.Reverse(points);

            var figure = new PathFigure(points[0]) { Closed = true };
            for (int i = 1; i < points.Length; i++)
                figure.Segments.Add(new LineSegment(points[i]));
            contours.Add(figure);
        }

        // ---- dashing ----

        private static List<List<Vector2>> DashPolyline(List<Vector2> pts, bool closed, double[] dash, double offset)
        {
            var runs = new List<List<Vector2>>();
            var poly = new List<Vector2>(pts);
            if (closed) poly.Add(pts[0]);

            double period = Sum(dash);
            int idx = 0;
            double remaining = dash[0];
            bool on = true;

            // Apply the dash offset by consuming the pattern.
            double o = ((offset % period) + period) % period;
            while (o > 0)
            {
                if (o >= remaining) { o -= remaining; idx = (idx + 1) % dash.Length; remaining = dash[idx]; on = !on; }
                else { remaining -= o; o = 0; }
            }

            List<Vector2>? current = on ? new List<Vector2> { poly[0] } : null;

            for (int i = 0; i < poly.Count - 1; i++)
            {
                Vector2 a = poly[i];
                Vector2 b = poly[i + 1];
                Vector2 seg = b - a;
                double segLen = seg.Length();
                if (segLen < 1e-6) continue;
                Vector2 dir = seg / (float)segLen;

                double t = 0;
                while (t < segLen - 1e-6)
                {
                    double step = Math.Min(remaining, segLen - t);
                    t += step;
                    remaining -= step;
                    Vector2 point = a + dir * (float)t;

                    if (on) current!.Add(point);

                    if (remaining <= 1e-6)
                    {
                        if (on && current!.Count >= 2) runs.Add(current);
                        on = !on;
                        idx = (idx + 1) % dash.Length;
                        remaining = dash[idx];
                        current = on ? new List<Vector2> { point } : null;
                    }
                }
            }

            if (on && current is { Count: >= 2 }) runs.Add(current);
            return runs;
        }

        // ---- geometry helpers ----

        private static Vector2 Perp(Vector2 v) => new(-v.Y, v.X);

        private static bool Intersect(Vector2 p0, Vector2 d0, Vector2 p1, Vector2 d1, out Vector2 m)
        {
            float denom = d0.X * d1.Y - d0.Y * d1.X;
            if (MathF.Abs(denom) < 1e-5f) { m = default; return false; }
            Vector2 delta = p1 - p0;
            float t = (delta.X * d1.Y - delta.Y * d1.X) / denom;
            m = p0 + d0 * t;
            return true;
        }

        private static float SignedArea(Vector2[] p)
        {
            float area = 0f;
            for (int i = 0; i < p.Length; i++)
            {
                Vector2 a = p[i];
                Vector2 b = p[(i + 1) % p.Length];
                area += a.X * b.Y - b.X * a.Y;
            }
            return area * 0.5f;
        }

        private static double Sum(double[] values)
        {
            double s = 0;
            foreach (double v in values) s += v;
            return s;
        }

        private static List<Vector2> FlattenFigure(PathFigure figure)
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
                        {
                            float t = i / (float)BezierSteps, u = 1f - t;
                            pts.Add(u * u * current + 2f * u * t * q.Control + t * t * q.Point);
                        }
                        current = q.Point;
                        break;
                    case CubicBezierSegment c:
                        for (int i = 1; i <= BezierSteps; i++)
                        {
                            float t = i / (float)BezierSteps, u = 1f - t;
                            pts.Add(u * u * u * current + 3f * u * u * t * c.Control1 + 3f * u * t * t * c.Control2 + t * t * t * c.Point);
                        }
                        current = c.Point;
                        break;
                }
            }
            return pts;
        }

        private static void RemoveDuplicates(List<Vector2> pts)
        {
            for (int i = pts.Count - 1; i > 0; i--)
                if (Vector2.DistanceSquared(pts[i], pts[i - 1]) < 1e-6f)
                    pts.RemoveAt(i);
        }
    }
}
