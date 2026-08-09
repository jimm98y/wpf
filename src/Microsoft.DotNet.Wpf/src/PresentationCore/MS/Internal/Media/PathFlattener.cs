// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Geometry to polylines: the managed stand-in for milcore's MilUtility_PathGeometryFlatten, and the
// foundation the managed stroker and boolean clipper both stand on.
//
// Every one of those three operations begins the same way -- reduce curves to line segments within a
// stated error -- so it lives here once rather than three times, and the two existing copies in this
// codebase are what motivated writing it:
//
//   * Geometry.FlattenSegment (hit testing) subdivided every curve into a fixed 24 steps and
//     approximated an ArcSegment BY A STRAIGHT LINE TO ITS ENDPOINT. A rounded rectangle hit-tested
//     as a plain rectangle, and a pie slice as a triangle.
//   * WgpuInterop's CurveFlattener got the step counts right, but works on float/Vector2 for the
//     per-frame path and lives in an assembly PresentationCore cannot reference (the dependency runs
//     the other way). Its error analysis is reproduced here in double.
//
// A fixed step count is wrong in both directions: it facets a large curve (a 400px circle got 24
// segments per quadrant, about 2px of visible chord error) and wastes work on a small one. Each
// count below is the smallest whose worst-case deviation from the true curve is within tolerance,
// in the same units as the control points.
//

using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace MS.Internal.Media
{
    /// <summary>One flattened figure: a polyline, and whether it closes back on itself.</summary>
    internal readonly struct PolylineFigure
    {
        internal PolylineFigure(List<Point> points, bool closed)
        {
            Points = points;
            Closed = closed;
        }

        internal List<Point> Points { get; }

        internal bool Closed { get; }
    }

    internal static class PathFlattener
    {
        /// <summary>
        /// Max deviation from the true curve, in the geometry's own units.
        ///
        /// Matches the renderer's CurveFlattener.DefaultTolerance, which was measured rather than
        /// picked: at 0.025 the flattening error sits just above that rasterizer's own anti-aliasing
        /// floor, so halving it again buys almost nothing and doubles the segment count.
        /// </summary>
        internal const double DefaultTolerance = 0.025;

        // Upper bounds. A degenerate control point -- NaN, or a coordinate near double.Max -- must not
        // be able to turn one curve into an unbounded vertex stream.
        private const int MaxCurveSteps = 256;
        private const int MaxArcSteps = 512;

        /// <summary>
        /// Flattens any geometry to polylines in its own coordinate space, with the geometry's
        /// Transform already applied.
        /// </summary>
        /// <remarks>
        /// Returns an empty list rather than throwing when the geometry cannot be reduced to figures
        /// (a CombinedGeometry whose operands are themselves unresolvable, say). Every caller here is
        /// on a drawing path where "nothing to draw" is a better answer than an exception.
        /// </remarks>
        internal static List<PolylineFigure> Flatten(Geometry geometry, double tolerance)
        {
            var result = new List<PolylineFigure>();
            if (geometry == null) return result;

            PathGeometry pg;
            try { pg = geometry.GetAsPathGeometry(); }
            catch (InvalidOperationException) { return result; }
            catch (NotSupportedException) { return result; }

            if (pg?.Figures == null) return result;

            Matrix m = pg.Transform?.Value ?? Matrix.Identity;
            AppendFigures(pg.Figures, m, tolerance, result);
            return result;
        }

        /// <summary>
        /// Flattens a geometry and rebuilds it as a polygonal PathGeometry, carrying the source's
        /// fill rule across. This is the whole of GetFlattenedPathGeometry off Windows.
        /// </summary>
        internal static PathGeometry FlattenToGeometry(Geometry geometry, double tolerance)
        {
            FillRule fillRule = FillRule.Nonzero;
            if (geometry != null)
            {
                try
                {
                    PathGeometry source = geometry.GetAsPathGeometry();
                    if (source != null) fillRule = source.FillRule;
                }
                catch (InvalidOperationException) { }
                catch (NotSupportedException) { }
            }

            return ToPathGeometry(Flatten(geometry, tolerance), fillRule);
        }

        /// <summary>Flattens a figure collection through an explicit transform.</summary>
        internal static List<PolylineFigure> Flatten(PathFigureCollection figures, Matrix transform, double tolerance)
        {
            var result = new List<PolylineFigure>();
            if (figures != null) AppendFigures(figures, transform, tolerance, result);
            return result;
        }

        private static void AppendFigures(PathFigureCollection figures, Matrix m, double tolerance,
                                          List<PolylineFigure> result)
        {
            if (!(tolerance > 0.0) || double.IsNaN(tolerance)) tolerance = DefaultTolerance;

            foreach (PathFigure figure in figures)
            {
                if (figure == null) continue;

                var points = new List<Point>();
                Point current = m.Transform(figure.StartPoint);
                points.Add(current);

                if (figure.Segments != null)
                {
                    foreach (PathSegment segment in figure.Segments)
                    {
                        FlattenSegment(segment, m, tolerance, ref current, points);
                    }
                }

                RemoveDuplicates(points);

                // A single point is not a figure -- except under a round cap, and the stroker deals
                // with that case itself because only it knows the pen.
                if (points.Count >= 2 || figure.IsClosed)
                {
                    result.Add(new PolylineFigure(points, figure.IsClosed));
                }
            }
        }

        /// <summary>
        /// Appends one segment's flattened points, excluding the segment's start (already present).
        /// </summary>
        internal static void FlattenSegment(PathSegment segment, Matrix m, double tolerance,
                                            ref Point current, List<Point> points)
        {
            switch (segment)
            {
                case LineSegment ls:
                    current = m.Transform(ls.Point);
                    points.Add(current);
                    break;

                case PolyLineSegment pls:
                    if (pls.Points != null)
                    {
                        foreach (Point p in pls.Points)
                        {
                            current = m.Transform(p);
                            points.Add(current);
                        }
                    }
                    break;

                case BezierSegment bs:
                    AppendCubic(ref current, m.Transform(bs.Point1), m.Transform(bs.Point2),
                                m.Transform(bs.Point3), tolerance, points);
                    break;

                case PolyBezierSegment pbs:
                    if (pbs.Points != null)
                    {
                        PointCollection pc = pbs.Points;
                        for (int k = 0; k + 2 < pc.Count; k += 3)
                        {
                            AppendCubic(ref current, m.Transform(pc[k]), m.Transform(pc[k + 1]),
                                        m.Transform(pc[k + 2]), tolerance, points);
                        }
                    }
                    break;

                case QuadraticBezierSegment qs:
                    AppendQuadratic(ref current, m.Transform(qs.Point1), m.Transform(qs.Point2),
                                    tolerance, points);
                    break;

                case PolyQuadraticBezierSegment pqs:
                    if (pqs.Points != null)
                    {
                        PointCollection pc = pqs.Points;
                        for (int k = 0; k + 1 < pc.Count; k += 2)
                        {
                            AppendQuadratic(ref current, m.Transform(pc[k]), m.Transform(pc[k + 1]),
                                            tolerance, points);
                        }
                    }
                    break;

                case ArcSegment arc:
                    AppendArc(ref current, arc, m, tolerance, points);
                    break;
            }
        }

        // ---- curves ----------------------------------------------------------------

        /// <summary>
        /// Cubic Bezier. Uses the degree-n bound: over k uniform spans the deviation is at most
        /// (n(n-1)/8)k^-2 max||second difference||, which is 0.75k^-2 for n = 3.
        /// </summary>
        private static void AppendCubic(ref Point p0, Point c1, Point c2, Point p1, double tolerance,
                                        List<Point> points)
        {
            double m = Math.Max(SecondDifference(p0, c1, c2), SecondDifference(c1, c2, p1));
            int steps = StepsFromSquareLaw(0.75 * m, tolerance);

            for (int i = 1; i <= steps; i++)
            {
                double t = (double)i / steps;
                double u = 1 - t;
                double w0 = u * u * u, w1 = 3 * u * u * t, w2 = 3 * u * t * t, w3 = t * t * t;
                points.Add(new Point(w0 * p0.X + w1 * c1.X + w2 * c2.X + w3 * p1.X,
                                     w0 * p0.Y + w1 * c1.Y + w2 * c2.Y + w3 * p1.Y));
            }

            p0 = p1;
        }

        /// <summary>
        /// Quadratic Bezier. Here the chord error is EXACT rather than bounded: B(t) - chord(t) is
        /// -t(1-t)d with d = p0 - 2c + p1, so the worst deviation over k spans is |d| / (4k^2).
        /// </summary>
        private static void AppendQuadratic(ref Point p0, Point c, Point p1, double tolerance,
                                            List<Point> points)
        {
            int steps = StepsFromSquareLaw(SecondDifference(p0, c, p1) * 0.25, tolerance);

            for (int i = 1; i <= steps; i++)
            {
                double t = (double)i / steps;
                double u = 1 - t;
                double w0 = u * u, w1 = 2 * u * t, w2 = t * t;
                points.Add(new Point(w0 * p0.X + w1 * c.X + w2 * p1.X,
                                     w0 * p0.Y + w1 * c.Y + w2 * p1.Y));
            }

            p0 = p1;
        }

        /// <summary>
        /// Elliptical arc, through the endpoint-to-centre conversion the SVG specification defines
        /// (F.6.5), which is the same parameterization WPF's ArcSegment uses.
        ///
        /// This is the segment the previous managed flattener gave up on and drew as a straight line
        /// to the endpoint. Arcs are how every rounded rectangle, pie slice and gauge in a WPF app is
        /// described, so that was not a small approximation.
        /// </summary>
        private static void AppendArc(ref Point p0, ArcSegment arc, Matrix m, double tolerance,
                                      List<Point> points)
        {
            Point end = m.Transform(arc.Point);

            double rx = Math.Abs(arc.Size.Width);
            double ry = Math.Abs(arc.Size.Height);

            // Degenerate radii mean a straight line, which the specification states explicitly.
            if (!(rx > 0.0) || !(ry > 0.0) || double.IsNaN(rx) || double.IsNaN(ry))
            {
                points.Add(end);
                p0 = end;
                return;
            }

            // The arc is described in the geometry's own space; flatten it there and transform the
            // resulting points, so a non-uniform or rotated transform is honoured exactly rather than
            // being applied to an already-elliptical approximation.
            Matrix inverse = m;
            bool invertible = true;
            try { inverse.Invert(); }
            catch (InvalidOperationException) { invertible = false; }

            Point localStart = invertible ? inverse.Transform(p0) : p0;
            Point localEnd = arc.Point;

            double phi = arc.RotationAngle * Math.PI / 180.0;
            double cosPhi = Math.Cos(phi), sinPhi = Math.Sin(phi);

            double dx2 = (localStart.X - localEnd.X) / 2.0;
            double dy2 = (localStart.Y - localEnd.Y) / 2.0;
            double x1p = cosPhi * dx2 + sinPhi * dy2;
            double y1p = -sinPhi * dx2 + cosPhi * dy2;

            // Radii too small to span the two endpoints are scaled up until they just fit (F.6.6).
            double lambda = (x1p * x1p) / (rx * rx) + (y1p * y1p) / (ry * ry);
            if (lambda > 1.0)
            {
                double s = Math.Sqrt(lambda);
                rx *= s;
                ry *= s;
            }

            double sign = (arc.IsLargeArc != (arc.SweepDirection == SweepDirection.Clockwise)) ? 1.0 : -1.0;
            double numerator = rx * rx * ry * ry - rx * rx * y1p * y1p - ry * ry * x1p * x1p;
            double denominator = rx * rx * y1p * y1p + ry * ry * x1p * x1p;
            double coefficient = (denominator > 0.0 && numerator > 0.0)
                ? sign * Math.Sqrt(numerator / denominator)
                : 0.0;

            double cxp = coefficient * rx * y1p / ry;
            double cyp = -coefficient * ry * x1p / rx;

            double cx = cosPhi * cxp - sinPhi * cyp + (localStart.X + localEnd.X) / 2.0;
            double cy = sinPhi * cxp + cosPhi * cyp + (localStart.Y + localEnd.Y) / 2.0;

            double startAngle = Angle(1.0, 0.0, (x1p - cxp) / rx, (y1p - cyp) / ry);
            double sweep = Angle((x1p - cxp) / rx, (y1p - cyp) / ry, (-x1p - cxp) / rx, (-y1p - cyp) / ry);

            if (arc.SweepDirection == SweepDirection.Counterclockwise && sweep > 0) sweep -= 2 * Math.PI;
            else if (arc.SweepDirection == SweepDirection.Clockwise && sweep < 0) sweep += 2 * Math.PI;

            // Step count from the sagitta of a chord on the LARGER radius, which is the worst case,
            // inverted exactly rather than through the small-angle approximation so that a coarse
            // tolerance does not under-count.
            int steps = ArcSteps(Math.Max(rx, ry), Math.Abs(sweep), ScaleOf(m), tolerance);

            for (int i = 1; i <= steps; i++)
            {
                double angle = startAngle + sweep * i / steps;
                double lx = cx + rx * Math.Cos(angle) * cosPhi - ry * Math.Sin(angle) * sinPhi;
                double ly = cy + rx * Math.Cos(angle) * sinPhi + ry * Math.Sin(angle) * cosPhi;
                points.Add(m.Transform(new Point(lx, ly)));
            }

            p0 = end;
        }

        /// <summary>Signed angle between two vectors, as SVG F.6.5.4 defines it.</summary>
        private static double Angle(double ux, double uy, double vx, double vy)
        {
            double dot = ux * vx + uy * vy;
            double len = Math.Sqrt((ux * ux + uy * uy) * (vx * vx + vy * vy));
            if (!(len > 0.0)) return 0.0;

            double cos = dot / len;
            if (cos < -1.0) cos = -1.0;
            else if (cos > 1.0) cos = 1.0;

            double a = Math.Acos(cos);
            return (ux * vy - uy * vx < 0.0) ? -a : a;
        }

        // ---- step counts -----------------------------------------------------------

        private static double SecondDifference(Point a, Point b, Point c)
        {
            double dx = a.X - 2 * b.X + c.X;
            double dy = a.Y - 2 * b.Y + c.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        /// <summary>Inverse of an error that decays as k^-2: err(k) = coefficient / k^2.</summary>
        private static int StepsFromSquareLaw(double coefficient, double tolerance)
        {
            if (!(tolerance > 0.0) || double.IsNaN(coefficient)) return 1;
            if (coefficient <= tolerance) return 1;

            double k = Math.Sqrt(coefficient / tolerance);
            return Clamp((int)Math.Ceiling(k), MaxCurveSteps);
        }

        /// <summary>
        /// Segments for an arc of the given radius and sweep. The sagitta of a chord subtending an
        /// angle t is r(1 - cos(t/2)); solving for the t that keeps it within tolerance gives the
        /// count directly.
        /// </summary>
        private static int ArcSteps(double radius, double sweep, double scale, double tolerance)
        {
            if (!(sweep > 0.0) || double.IsNaN(sweep)) return 1;
            if (!(radius > 0.0) || !(tolerance > 0.0)) return 1;

            // The tolerance is stated in the OUTPUT space, but the arc is being flattened in the
            // geometry's own space, so it has to be pulled back through the transform's scale --
            // otherwise a shape magnified 10x is flattened 10x too coarsely, which is exactly the
            // faceting a fixed step count produced.
            double local = (scale > 1e-9) ? tolerance / scale : tolerance;
            if (local >= radius) return Math.Max(1, (int)Math.Ceiling(sweep / (Math.PI / 2.0)));

            double halfAngle = Math.Acos(1.0 - local / radius);
            if (!(halfAngle > 0.0)) return MaxArcSteps;

            return Clamp((int)Math.Ceiling(sweep / (2.0 * halfAngle)), MaxArcSteps);
        }

        /// <summary>Scale magnitude of a matrix's linear part: the larger of the two axis scales.</summary>
        internal static double ScaleOf(Matrix m)
        {
            double sx = Math.Sqrt(m.M11 * m.M11 + m.M12 * m.M12);
            double sy = Math.Sqrt(m.M21 * m.M21 + m.M22 * m.M22);
            return Math.Max(sx, sy);
        }

        private static int Clamp(int k, int max) => k < 1 ? 1 : (k > max ? max : k);

        // ---- output ----------------------------------------------------------------

        /// <summary>
        /// Drops points that repeat the one before. Zero-length segments carry no shape but do carry
        /// an undefined direction, which is what makes a stroker emit a spurious join or a clipper
        /// see a degenerate edge.
        /// </summary>
        internal static void RemoveDuplicates(List<Point> points)
        {
            const double EpsilonSquared = 1e-18;

            int write = 1;
            for (int read = 1; read < points.Count; read++)
            {
                Point previous = points[write - 1];
                double dx = points[read].X - previous.X;
                double dy = points[read].Y - previous.Y;
                if (dx * dx + dy * dy > EpsilonSquared)
                {
                    points[write++] = points[read];
                }
            }

            if (write < points.Count) points.RemoveRange(write, points.Count - write);
        }

        /// <summary>Rebuilds flattened figures into a PathGeometry of PolyLineSegments.</summary>
        internal static PathGeometry ToPathGeometry(List<PolylineFigure> figures, FillRule fillRule)
        {
            var result = new PathGeometry { FillRule = fillRule };

            foreach (PolylineFigure figure in figures)
            {
                List<Point> points = figure.Points;
                if (points.Count < 2) continue;

                var pathFigure = new PathFigure { StartPoint = points[0], IsClosed = figure.Closed, IsFilled = true };

                var rest = new PointCollection(points.Count - 1);
                for (int i = 1; i < points.Count; i++) rest.Add(points[i]);

                pathFigure.Segments.Add(new PolyLineSegment(rest, true));
                result.Figures.Add(pathFigure);
            }

            return result;
        }
    }
}
