// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Managed axis-aligned bounds accumulator for a serialized PathGeometryData, used off-Windows
// in place of the native milcore MilUtility_PathGeometryBounds. It plugs into the existing
// PathGeometry.ParsePathGeometryData walker and unions every control/end point it sees.
//
// Beziers are bounded by the convex hull of their control points and arcs by the endpoint
// inflated by the ellipse radii -- both are valid supersets of the true curve, so the result
// is a correct (slightly loose) bounding box, which is what Shape.GetNaturalSize / layout need.
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

        private void Add(Point p)
        {
            if (double.IsNaN(p.X) || double.IsNaN(p.Y)) return;
            if (p.X < _left) _left = p.X;
            if (p.X > _right) _right = p.X;
            if (p.Y < _top) _top = p.Y;
            if (p.Y > _bottom) _bottom = p.Y;
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
            Add(point1);
            Add(point2);
            _current = point2;
        }

        public override void BezierTo(Point point1, Point point2, Point point3, bool isStroked, bool isSmoothJoin)
        {
            Add(point1);
            Add(point2);
            Add(point3);
            _current = point3;
        }

        public override void PolyLineTo(IList<Point> points, bool isStroked, bool isSmoothJoin)
            => AddPoly(points);

        public override void PolyQuadraticBezierTo(IList<Point> points, bool isStroked, bool isSmoothJoin)
            => AddPoly(points);

        public override void PolyBezierTo(IList<Point> points, bool isStroked, bool isSmoothJoin)
            => AddPoly(points);

        private void AddPoly(IList<Point> points)
        {
            if (points == null) return;
            foreach (Point p in points) Add(p);
            if (points.Count > 0) _current = points[points.Count - 1];
        }

        public override void ArcTo(Point point, Size size, double rotationAngle, bool isLargeArc,
            SweepDirection sweepDirection, bool isStroked, bool isSmoothJoin)
        {
            // Loose but valid: the arc lies within the endpoint box inflated by the radii.
            double rx = Math.Abs(size.Width), ry = Math.Abs(size.Height);
            Add(new Point(_current.X - rx, _current.Y - ry));
            Add(new Point(_current.X + rx, _current.Y + ry));
            Add(new Point(point.X - rx, point.Y - ry));
            Add(new Point(point.X + rx, point.Y + ry));
            _current = point;
        }

        internal override void SetClosedState(bool closed)
        {
        }
    }
}
