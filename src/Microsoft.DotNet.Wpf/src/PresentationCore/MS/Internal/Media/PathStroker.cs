// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Stroke to fill: the region a pen covers when it draws a path, as a fillable geometry. The managed
// stand-in for milcore's MilUtility_PathGeometryWiden.
//
// The algorithm is the renderer's PathStroker (WgpuInterop/Composition/PathStroker.cs), reworked
// against System.Windows.Media types. It cannot simply be referenced: WgpuInterop depends on
// PresentationCore rather than the other way round, and it works in float/Vector2 for the per-frame
// path where this needs the double precision the public Geometry API is specified in. What is added
// here is the rest of WPF's pen model, which the renderer does not need: separate start and end
// caps, the triangle cap, and the dash cap.
//
// The output is a set of OVERLAPPING contours -- one quad per segment, one wedge or disc per join,
// one shape per cap -- every one wound the same way, so the nonzero fill rule unions them. This is
// the important design decision, and it is what makes the code short enough to trust: computing a
// single non-self-intersecting outline for a stroke means solving offset-curve self-intersection,
// which is the hardest part of stroking and buys nothing, because every consumer of the result
// (filling, clipping, hit testing, PDF's `f` operator) is defined in terms of a fill rule anyway.
//
// The consequence to know about: summing this geometry's contour areas double-counts the overlaps.
// Anything measuring area has to integrate under the fill rule, not add up shoelaces.
//

using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace MS.Internal.Media
{
    internal static class PathStroker
    {
        // Below this a segment carries no direction, so offsetting it would produce a normal built
        // from noise, and a join at either end would be placed by that noise.
        private const double MinSegmentLength = 1e-9;

        /// <summary>
        /// The contour of <paramref name="pen"/> stroking <paramref name="geometry"/>, as a nonzero
        /// PathGeometry. Never null; an empty geometry for a degenerate pen.
        /// </summary>
        internal static PathGeometry Widen(Geometry geometry, Pen pen, double tolerance)
        {
            if (geometry == null || pen == null) return new PathGeometry();

            double half = pen.Thickness / 2.0;
            if (!(half > 0.0) || double.IsNaN(half)) return new PathGeometry();

            List<PolylineFigure> figures = PathFlattener.Flatten(geometry, tolerance);
            return Widen(figures, pen, half, tolerance);
        }

        internal static PathGeometry Widen(List<PolylineFigure> figures, Pen pen, double half, double tolerance)
        {
            var contours = new List<PathFigure>();
            double[] dashes = ResolveDashes(pen, out double dashOffset);

            foreach (PolylineFigure figure in figures)
            {
                List<Point> points = figure.Points;

                if (points.Count < 2)
                {
                    // A figure that is a single point still marks the page under a round or square
                    // cap -- this is how a dot is drawn -- and marks nothing under a flat one.
                    if (points.Count == 1) AddDegenerateDot(contours, points[0], half, pen.StartLineCap, tolerance);
                    continue;
                }

                if (dashes != null)
                {
                    foreach (List<Point> run in Dash(points, figure.Closed, dashes, dashOffset))
                    {
                        // Every dash is its own open figure, and its ends take the DASH cap rather
                        // than the figure's -- which is why WPF has a separate DashCap at all.
                        StrokePolyline(contours, run, closed: false, pen, half, tolerance,
                                       startCap: pen.DashCap, endCap: pen.DashCap);
                    }
                }
                else
                {
                    StrokePolyline(contours, points, figure.Closed, pen, half, tolerance,
                                   pen.StartLineCap, pen.EndLineCap);
                }
            }

            var result = new PathGeometry { FillRule = FillRule.Nonzero };
            foreach (PathFigure contour in contours) result.Figures.Add(contour);
            return result;
        }

        private static void StrokePolyline(List<PathFigure> contours, List<Point> points, bool closed,
                                           Pen pen, double half, double tolerance,
                                           PenLineCap startCap, PenLineCap endCap)
        {
            int n = points.Count;
            if (n < 2) return;

            // The segments themselves: each becomes a rectangle offset by half the pen width.
            int segments = closed ? n : n - 1;
            for (int i = 0; i < segments; i++)
            {
                Point a = points[i];
                Point b = points[(i + 1) % n];

                if (!Direction(a, b, out double dx, out double dy)) continue;

                double nx = -dy * half, ny = dx * half;
                AddContour(contours, new[]
                {
                    new Point(a.X + nx, a.Y + ny),
                    new Point(b.X + nx, b.Y + ny),
                    new Point(b.X - nx, b.Y - ny),
                    new Point(a.X - nx, a.Y - ny),
                });
            }

            // Joins fill the wedge left on the outer side of each corner. A closed figure joins at
            // every vertex including the one where it meets itself; an open one only in the middle.
            int first = closed ? 0 : 1;
            int last = closed ? n - 1 : n - 2;
            for (int i = first; i <= last; i++)
            {
                AddJoin(contours, points[(i - 1 + n) % n], points[i], points[(i + 1) % n],
                        half, pen.LineJoin, pen.MiterLimit, tolerance);
            }

            if (!closed)
            {
                AddCap(contours, points[1], points[0], half, startCap, tolerance);
                AddCap(contours, points[n - 2], points[n - 1], half, endCap, tolerance);
            }
        }

        // ---- caps ------------------------------------------------------------------

        private static void AddCap(List<PathFigure> contours, Point inner, Point end, double half,
                                   PenLineCap cap, double tolerance)
        {
            if (cap == PenLineCap.Flat) return;

            if (!Direction(inner, end, out double dx, out double dy))
            {
                if (cap == PenLineCap.Round) AddDisc(contours, end, half, tolerance);
                return;
            }

            double nx = -dy * half, ny = dx * half;   // across the stroke
            double ex = dx * half, ey = dy * half;    // past the end point

            switch (cap)
            {
                case PenLineCap.Round:
                    AddDisc(contours, end, half, tolerance);
                    break;

                case PenLineCap.Square:
                    AddContour(contours, new[]
                    {
                        new Point(end.X + nx, end.Y + ny),
                        new Point(end.X + nx + ex, end.Y + ny + ey),
                        new Point(end.X - nx + ex, end.Y - ny + ey),
                        new Point(end.X - nx, end.Y - ny),
                    });
                    break;

                case PenLineCap.Triangle:
                    AddContour(contours, new[]
                    {
                        new Point(end.X + nx, end.Y + ny),
                        new Point(end.X + ex, end.Y + ey),
                        new Point(end.X - nx, end.Y - ny),
                    });
                    break;
            }
        }

        /// <summary>A figure with no length at all: a dot, if the cap is one that draws one.</summary>
        private static void AddDegenerateDot(List<PathFigure> contours, Point at, double half,
                                             PenLineCap cap, double tolerance)
        {
            switch (cap)
            {
                case PenLineCap.Round:
                    AddDisc(contours, at, half, tolerance);
                    break;

                case PenLineCap.Square:
                    AddContour(contours, new[]
                    {
                        new Point(at.X - half, at.Y - half),
                        new Point(at.X + half, at.Y - half),
                        new Point(at.X + half, at.Y + half),
                        new Point(at.X - half, at.Y + half),
                    });
                    break;
            }
        }

        // ---- joins -----------------------------------------------------------------

        private static void AddJoin(List<PathFigure> contours, Point previous, Point current, Point next,
                                    double half, PenLineJoin join, double miterLimit, double tolerance)
        {
            if (!Direction(previous, current, out double d0x, out double d0y)) return;
            if (!Direction(current, next, out double d1x, out double d1y)) return;

            double cross = d0x * d1y - d0y * d1x;

            // Collinear: the two segment rectangles already meet flush, so a join would add nothing.
            // Anti-parallel (a spike doubling back) is treated the same, since there is no outer side.
            if (Math.Abs(cross) < 1e-12) return;

            if (join == PenLineJoin.Round)
            {
                AddDisc(contours, current, half, tolerance);
                return;
            }

            // The apex has to land on the convex side of the turn, which is the side the cross
            // product's sign names.
            double s = cross > 0 ? -half : half;
            var pIn = new Point(current.X - d0y * s, current.Y + d0x * s);
            var pOut = new Point(current.X - d1y * s, current.Y + d1x * s);

            if (join == PenLineJoin.Miter &&
                Intersect(pIn, d0x, d0y, pOut, d1x, d1y, out Point apex))
            {
                double reach = Math.Sqrt((apex.X - current.X) * (apex.X - current.X) +
                                         (apex.Y - current.Y) * (apex.Y - current.Y));

                // WPF's MiterLimit is a ratio to the pen's HALF width, matching how the property is
                // documented; past it the spike is cut back to a bevel, which is the whole purpose
                // of the limit -- an almost-doubled-back corner has an unbounded miter.
                if (reach <= miterLimit * half)
                {
                    AddContour(contours, new[] { current, pIn, apex, pOut });
                    return;
                }
            }

            AddContour(contours, new[] { current, pIn, pOut });   // bevel, and the miter fallback
        }

        // ---- dashes ----------------------------------------------------------------

        /// <summary>
        /// The dash pattern in absolute units, or null when the pen is solid. WPF states dashes as
        /// multiples of the pen thickness, so they scale with the pen rather than with the page.
        /// </summary>
        private static double[] ResolveDashes(Pen pen, out double offset)
        {
            offset = 0;

            DashStyle style = pen.DashStyle;
            if (style?.Dashes == null || style.Dashes.Count == 0) return null;

            double total = 0;
            var dashes = new double[style.Dashes.Count];
            for (int i = 0; i < dashes.Length; i++)
            {
                double d = style.Dashes[i] * pen.Thickness;
                if (double.IsNaN(d) || d < 0) d = 0;
                dashes[i] = d;
                total += d;
            }

            // An all-zero pattern would be an infinite loop rather than an invisible line.
            if (!(total > 0)) return null;

            // An odd-length pattern repeats with on and off swapped, so double it and let the walk
            // below stay a simple alternation.
            if ((dashes.Length & 1) != 0)
            {
                var doubled = new double[dashes.Length * 2];
                Array.Copy(dashes, doubled, dashes.Length);
                Array.Copy(dashes, 0, doubled, dashes.Length, dashes.Length);
                dashes = doubled;
                total *= 2;
            }

            offset = style.Offset * pen.Thickness;
            return dashes;
        }

        /// <summary>Splits a polyline into the "on" runs of a dash pattern.</summary>
        private static List<List<Point>> Dash(List<Point> points, bool closed, double[] dashes, double offset)
        {
            var runs = new List<List<Point>>();

            var path = new List<Point>(points);
            if (closed && points.Count > 0) path.Add(points[0]);

            double period = 0;
            foreach (double d in dashes) period += d;

            int index = 0;
            double remaining = dashes[0];
            bool on = true;

            // Consume the pattern up to the offset, so the dash phase is where the caller asked.
            double skip = ((offset % period) + period) % period;
            while (skip > 0)
            {
                if (skip >= remaining)
                {
                    skip -= remaining;
                    index = (index + 1) % dashes.Length;
                    remaining = dashes[index];
                    on = !on;
                }
                else
                {
                    remaining -= skip;
                    skip = 0;
                }

                // A zero-length entry advances the pattern without consuming any of the offset;
                // without this guard an offset into a pattern containing a 0 never terminates.
                if (remaining <= 0 && skip > 0)
                {
                    index = (index + 1) % dashes.Length;
                    remaining = dashes[index];
                    on = !on;
                }
            }

            List<Point> current = on && path.Count > 0 ? new List<Point> { path[0] } : null;

            for (int i = 0; i + 1 < path.Count; i++)
            {
                Point a = path[i], b = path[i + 1];
                double segmentLength = Distance(a, b);
                if (segmentLength < MinSegmentLength) continue;

                double dx = (b.X - a.X) / segmentLength, dy = (b.Y - a.Y) / segmentLength;

                double travelled = 0;
                while (travelled < segmentLength - 1e-12)
                {
                    double step = Math.Min(remaining, segmentLength - travelled);
                    travelled += step;
                    remaining -= step;

                    var point = new Point(a.X + dx * travelled, a.Y + dy * travelled);
                    current?.Add(point);

                    if (remaining <= 1e-12)
                    {
                        if (on && current != null && current.Count >= 2) runs.Add(current);
                        on = !on;
                        index = (index + 1) % dashes.Length;
                        remaining = dashes[index];
                        current = on ? new List<Point> { point } : null;
                    }
                }
            }

            if (on && current != null && current.Count >= 2) runs.Add(current);
            return runs;
        }

        // ---- contour construction --------------------------------------------------

        /// <summary>
        /// The ring for a round join or cap. The segment count follows the pen radius: a fixed count
        /// visibly polygonizes a thick pen while wasting vertices on a hairline.
        /// </summary>
        private static void AddDisc(List<PathFigure> contours, Point centre, double radius, double tolerance)
        {
            int steps = CircleSteps(radius, tolerance);

            var ring = new Point[steps];
            for (int i = 0; i < steps; i++)
            {
                double t = i / (double)steps * Math.PI * 2.0;
                ring[i] = new Point(centre.X + Math.Cos(t) * radius, centre.Y + Math.Sin(t) * radius);
            }

            AddContour(contours, ring);
        }

        private static int CircleSteps(double radius, double tolerance)
        {
            const int MaxRingSteps = 512;

            if (!(radius > 0.0) || double.IsNaN(radius)) return 3;
            if (!(tolerance > 0.0) || tolerance >= radius) return 3;

            double theta = Math.Acos(1.0 - tolerance / radius);
            if (!(theta > 0.0)) return MaxRingSteps;

            int k = (int)Math.Ceiling(Math.PI / theta);
            return Math.Max(3, Math.Min(k, MaxRingSteps));
        }

        /// <summary>
        /// Adds one closed contour, wound positive.
        ///
        /// The winding normalization is what lets the nonzero rule act as a union: two contours of
        /// opposite winding would cancel where they overlap, punching holes at exactly the joins and
        /// caps that are meant to fill the gaps.
        /// </summary>
        private static void AddContour(List<PathFigure> contours, Point[] points)
        {
            if (points.Length < 3) return;
            if (SignedArea(points) < 0) Array.Reverse(points);

            var figure = new PathFigure { StartPoint = points[0], IsClosed = true, IsFilled = true };

            var rest = new PointCollection(points.Length - 1);
            for (int i = 1; i < points.Length; i++) rest.Add(points[i]);
            figure.Segments.Add(new PolyLineSegment(rest, true));

            contours.Add(figure);
        }

        // ---- small helpers ---------------------------------------------------------

        /// <summary>Unit direction from a to b; false when they are the same point.</summary>
        private static bool Direction(Point a, Point b, out double dx, out double dy)
        {
            dx = b.X - a.X;
            dy = b.Y - a.Y;

            double length = Math.Sqrt(dx * dx + dy * dy);
            if (!(length > MinSegmentLength)) { dx = dy = 0; return false; }

            dx /= length;
            dy /= length;
            return true;
        }

        private static bool Intersect(Point p0, double d0x, double d0y, Point p1, double d1x, double d1y,
                                      out Point crossing)
        {
            double denominator = d0x * d1y - d0y * d1x;
            if (Math.Abs(denominator) < 1e-12) { crossing = default; return false; }

            double t = ((p1.X - p0.X) * d1y - (p1.Y - p0.Y) * d1x) / denominator;
            crossing = new Point(p0.X + d0x * t, p0.Y + d0y * t);
            return true;
        }

        private static double SignedArea(Point[] points)
        {
            double area = 0;
            for (int i = 0, j = points.Length - 1; i < points.Length; j = i++)
            {
                area += points[j].X * points[i].Y - points[i].X * points[j].Y;
            }
            return area / 2.0;
        }

        private static double Distance(Point a, Point b)
        {
            double dx = b.X - a.X, dy = b.Y - a.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }
    }
}
