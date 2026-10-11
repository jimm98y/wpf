// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Managed axis-aligned bounds for a serialized PathGeometryData, in place of the native milcore
// MilUtility_PathGeometryBounds. It plugs into the existing PathGeometry.ParsePathGeometryData
// walker.
//
// The bounds are TIGHT, and that is the whole point of the file.
//
// An earlier version unioned the control points of each Bezier and bounded each arc by its endpoints
// inflated by the full radii. Both are valid supersets of the true curve, and for a while that was
// enough, because this ran only where the native helper did not exist. It is not enough now that it
// runs everywhere: these bounds are what Shape.MeasureOverride asks for, so a loose answer makes
// every curved shape in an application MEASURE BIGGER THAN IT DRAWS and quietly shifts layout around
// it. A small arc of a large ellipse was the worst case -- endpoint ± radii is enormous next to the
// arc itself.
//
// So each curve is solved rather than approximated:
//
//   * a Bezier's extremes are its endpoints plus the points where a derivative crosses zero, which
//     is a quadratic per axis for a cubic and a linear one for a quadratic;
//   * an arc is converted from SVG endpoint form to centre form and the four axis-extreme angles of
//     the (possibly rotated) ellipse are taken, keeping only those the sweep actually passes through.
//
// Both are exact, so the result matches what the curve really occupies.
//

using System.Collections.Generic;

namespace System.Windows.Media
{
    internal sealed class PathBoundsAccumulator : CapacityStreamGeometryContext
    {
        private double _left = double.PositiveInfinity;
        private double _top = double.PositiveInfinity;
        private double _right = double.NegativeInfinity;
        private double _bottom = double.NegativeInfinity;

        private Point _current;

        internal bool IsEmpty => _right < _left || _bottom < _top;

        internal Rect Bounds => IsEmpty
            ? Rect.Empty
            : new Rect(_left, _top, _right - _left, _bottom - _top);

        private void Add(Point p) => Add(p.X, p.Y);

        private void Add(double x, double y)
        {
            if (double.IsNaN(x) || double.IsNaN(y)) return;
            if (x < _left) _left = x;
            if (x > _right) _right = x;
            if (y < _top) _top = y;
            if (y > _bottom) _bottom = y;
        }

        public override void BeginFigure(Point startPoint, bool isFilled, bool isClosed)
        {
            _current = startPoint;
            Add(startPoint);
        }

        public override void LineTo(Point point, bool isStroked, bool isSmoothJoin)
        {
            Add(point);
            _current = point;
        }

        public override void QuadraticBezierTo(Point point1, Point point2, bool isStroked, bool isSmoothJoin)
        {
            AddQuadratic(_current, point1, point2);
            _current = point2;
        }

        public override void BezierTo(Point point1, Point point2, Point point3, bool isStroked, bool isSmoothJoin)
        {
            AddCubic(_current, point1, point2, point3);
            _current = point3;
        }

        public override void PolyLineTo(IList<Point> points, bool isStroked, bool isSmoothJoin)
        {
            if (points == null) return;
            foreach (Point p in points) Add(p);
            if (points.Count > 0) _current = points[points.Count - 1];
        }

        public override void PolyQuadraticBezierTo(IList<Point> points, bool isStroked, bool isSmoothJoin)
        {
            if (points == null) return;
            // Points come in control/end pairs, each continuing from the previous end point.
            for (int i = 0; i + 1 < points.Count; i += 2)
            {
                AddQuadratic(_current, points[i], points[i + 1]);
                _current = points[i + 1];
            }
        }

        public override void PolyBezierTo(IList<Point> points, bool isStroked, bool isSmoothJoin)
        {
            if (points == null) return;
            // Triples: two controls and an end point, each continuing from the previous end point.
            for (int i = 0; i + 2 < points.Count; i += 3)
            {
                AddCubic(_current, points[i], points[i + 1], points[i + 2]);
                _current = points[i + 2];
            }
        }

        public override void ArcTo(Point point, Size size, double rotationAngle, bool isLargeArc,
            SweepDirection sweepDirection, bool isStroked, bool isSmoothJoin)
        {
            AddArc(_current, point, size, rotationAngle, isLargeArc, sweepDirection);
            _current = point;
        }

        internal override void SetClosedState(bool closed)
        {
        }

        // ---- curves ------------------------------------------------------------------------------

        // A cubic's extremes: the two endpoints, plus any point where the derivative crosses zero.
        // B'(t)/3 = at^2 + bt + c with a = -P0 + 3P1 - 3P2 + P3, b = 2P0 - 4P1 + 2P2, c = P1 - P0.
        private void AddCubic(Point p0, Point p1, Point p2, Point p3)
        {
            Add(p0);
            Add(p3);

            AddCubicAxisExtremes(p0.X, p1.X, p2.X, p3.X, p0, p1, p2, p3);
            AddCubicAxisExtremes(p0.Y, p1.Y, p2.Y, p3.Y, p0, p1, p2, p3);
        }

        private void AddCubicAxisExtremes(double v0, double v1, double v2, double v3,
                                          Point p0, Point p1, Point p2, Point p3)
        {
            double a = -v0 + 3 * v1 - 3 * v2 + v3;
            double b = 2 * v0 - 4 * v1 + 2 * v2;
            double c = v1 - v0;

            foreach (double t in QuadraticRoots(a, b, c))
            {
                Add(CubicAt(p0, p1, p2, p3, t));
            }
        }

        private static Point CubicAt(Point p0, Point p1, Point p2, Point p3, double t)
        {
            double u = 1 - t;
            double w0 = u * u * u, w1 = 3 * u * u * t, w2 = 3 * u * t * t, w3 = t * t * t;
            return new Point(w0 * p0.X + w1 * p1.X + w2 * p2.X + w3 * p3.X,
                             w0 * p0.Y + w1 * p1.Y + w2 * p2.Y + w3 * p3.Y);
        }

        // A quadratic's extremes: the endpoints, plus t = (P0 - P1) / (P0 - 2P1 + P2) per axis.
        private void AddQuadratic(Point p0, Point p1, Point p2)
        {
            Add(p0);
            Add(p2);

            AddQuadraticAxisExtreme(p0.X, p1.X, p2.X, p0, p1, p2);
            AddQuadraticAxisExtreme(p0.Y, p1.Y, p2.Y, p0, p1, p2);
        }

        private void AddQuadraticAxisExtreme(double v0, double v1, double v2, Point p0, Point p1, Point p2)
        {
            double denom = v0 - 2 * v1 + v2;
            if (Math.Abs(denom) < 1e-12) return;   // derivative is constant: no interior extreme

            double t = (v0 - v1) / denom;
            if (t <= 0 || t >= 1) return;

            double u = 1 - t;
            Add(u * u * p0.X + 2 * u * t * p1.X + t * t * p2.X,
                u * u * p0.Y + 2 * u * t * p1.Y + t * t * p2.Y);
        }

        // Real roots of at^2 + bt + c strictly inside (0,1); anything outside cannot be an interior
        // extreme, and the endpoints are added separately.
        private static IEnumerable<double> QuadraticRoots(double a, double b, double c)
        {
            if (Math.Abs(a) < 1e-12)
            {
                if (Math.Abs(b) >= 1e-12)
                {
                    double tl = -c / b;
                    if (tl > 0 && tl < 1) yield return tl;
                }
                yield break;
            }

            double disc = b * b - 4 * a * c;
            if (disc < 0) yield break;

            double root = Math.Sqrt(disc);
            double t1 = (-b + root) / (2 * a);
            double t2 = (-b - root) / (2 * a);
            if (t1 > 0 && t1 < 1) yield return t1;
            if (t2 > 0 && t2 < 1) yield return t2;
        }

        // ---- arcs --------------------------------------------------------------------------------

        // WPF's ArcSegment is an SVG endpoint arc: where it ends, the two radii, how far the ellipse
        // is rotated, and two flags choosing which of the four possible arcs is meant. The extremes
        // of the underlying ellipse are easy in CENTRE form, so convert (the standard SVG F.6.5
        // procedure) and then keep only the extreme angles the sweep actually passes through.
        private void AddArc(Point start, Point end, Size size, double rotationDegrees,
                            bool isLargeArc, SweepDirection sweepDirection)
        {
            Add(start);
            Add(end);

            double rx = Math.Abs(size.Width), ry = Math.Abs(size.Height);
            if (rx < 1e-12 || ry < 1e-12) return;    // degenerate: the arc is the straight line already added
            if (double.IsNaN(rx) || double.IsNaN(ry) || double.IsNaN(rotationDegrees)) return;

            double phi = rotationDegrees * Math.PI / 180.0;
            double cosPhi = Math.Cos(phi), sinPhi = Math.Sin(phi);
            bool sweep = sweepDirection == SweepDirection.Clockwise;

            // Step 1: the endpoints in the ellipse's own frame, relative to their midpoint.
            double dx2 = (start.X - end.X) / 2.0, dy2 = (start.Y - end.Y) / 2.0;
            double x1 = cosPhi * dx2 + sinPhi * dy2;
            double y1 = -sinPhi * dx2 + cosPhi * dy2;

            // Step 2: grow the radii if they are too small to span the endpoints, as SVG requires.
            double lambda = (x1 * x1) / (rx * rx) + (y1 * y1) / (ry * ry);
            if (lambda > 1)
            {
                double s = Math.Sqrt(lambda);
                rx *= s;
                ry *= s;
            }

            // Step 3: the centre, in the ellipse's frame and then in the caller's.
            double rxSq = rx * rx, rySq = ry * ry;
            double num = rxSq * rySq - rxSq * y1 * y1 - rySq * x1 * x1;
            double den = rxSq * y1 * y1 + rySq * x1 * x1;
            if (den < 1e-12) return;

            double factor = Math.Sqrt(Math.Max(0, num / den));
            if (isLargeArc == sweep) factor = -factor;

            double cx1 = factor * (rx * y1) / ry;
            double cy1 = factor * -(ry * x1) / rx;
            double cx = cosPhi * cx1 - sinPhi * cy1 + (start.X + end.X) / 2.0;
            double cy = sinPhi * cx1 + cosPhi * cy1 + (start.Y + end.Y) / 2.0;

            // Step 4: where the sweep starts and how far it goes.
            double startAngle = Math.Atan2((y1 - cy1) / ry, (x1 - cx1) / rx);
            double endAngle = Math.Atan2((-y1 - cy1) / ry, (-x1 - cx1) / rx);
            double delta = endAngle - startAngle;
            if (sweep && delta < 0) delta += 2 * Math.PI;
            else if (!sweep && delta > 0) delta -= 2 * Math.PI;

            // The angles at which the rotated ellipse reaches its extreme x and y. Each has a partner
            // half a turn away (the opposite side of the ellipse).
            double tX = Math.Atan2(-ry * sinPhi, rx * cosPhi);
            double tY = Math.Atan2(ry * cosPhi, rx * sinPhi);

            AddArcAngleIfSwept(tX, startAngle, delta, cx, cy, rx, ry, cosPhi, sinPhi);
            AddArcAngleIfSwept(tX + Math.PI, startAngle, delta, cx, cy, rx, ry, cosPhi, sinPhi);
            AddArcAngleIfSwept(tY, startAngle, delta, cx, cy, rx, ry, cosPhi, sinPhi);
            AddArcAngleIfSwept(tY + Math.PI, startAngle, delta, cx, cy, rx, ry, cosPhi, sinPhi);
        }

        // Includes the ellipse point at angle t, but only when the sweep passes through it.
        private void AddArcAngleIfSwept(double t, double startAngle, double delta,
                                        double cx, double cy, double rx, double ry,
                                        double cosPhi, double sinPhi)
        {
            // How far past the start this angle lies, measured in the sweep's own direction, so the
            // test is a single range check regardless of which way the arc goes or where it wraps.
            double offset = t - startAngle;
            double travel = delta >= 0
                ? Norm(offset)                 // counter-clockwise in angle terms: 0 .. 2pi
                : -Norm(-offset);              // clockwise: 0 .. -2pi

            if (delta >= 0 ? (travel > delta) : (travel < delta)) return;

            Add(cx + rx * Math.Cos(t) * cosPhi - ry * Math.Sin(t) * sinPhi,
                cy + rx * Math.Cos(t) * sinPhi + ry * Math.Sin(t) * cosPhi);
        }

        /// <summary>An angle wrapped into [0, 2pi).</summary>
        private static double Norm(double angle)
        {
            const double TwoPi = 2 * Math.PI;
            angle %= TwoPi;
            return angle < 0 ? angle + TwoPi : angle;
        }
    }
}
