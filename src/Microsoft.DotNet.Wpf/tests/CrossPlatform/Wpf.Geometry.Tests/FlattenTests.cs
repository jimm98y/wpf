// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// GetFlattenedPathGeometry: curves reduced to line segments within a stated error.
//
// Flattening is one of the few graphics operations with an exact right answer, so these tests assert
// against closed-form geometry -- the area of a circle, the distance of every vertex from its centre
// -- rather than against a golden output. A tolerance-driven algorithm that is correct will satisfy
// them at any step count; one that hard-codes a step count will not.
//

using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using Xunit;

namespace Wpf.Geometry.Tests
{
    public class FlattenTests
    {
        [Fact]
        public void CircleVerticesStayWithinToleranceOfTheTrueRadius()
        {
            const double Radius = 100.0;
            const double Tolerance = 0.05;

            var circle = new EllipseGeometry(new Point(0, 0), Radius, Radius);
            List<Point> points = Vertices(circle.GetFlattenedPathGeometry(Tolerance, ToleranceType.Absolute));

            Assert.NotEmpty(points);
            foreach (Point p in points)
            {
                double r = Math.Sqrt(p.X * p.X + p.Y * p.Y);
                AssertOnCircle(r, Radius, Tolerance);
            }
        }

        [Fact]
        public void CircleAreaIsRight()
        {
            const double Radius = 100.0;
            const double Tolerance = 0.05;

            var circle = new EllipseGeometry(new Point(30, -20), Radius, Radius);
            double area = Math.Abs(SignedArea(Vertices(circle.GetFlattenedPathGeometry(Tolerance, ToleranceType.Absolute))));

            // An inscribed polygon always under-measures, and by a bounded amount: the area lost is
            // the sum of the circular segments cut off by the chords, which for a sagitta within
            // `tolerance` is under 2*pi*r*tolerance.
            double exact = Math.PI * Radius * Radius;
            Assert.InRange(area, exact - 2 * Math.PI * Radius * Tolerance, exact);
        }

        [Fact]
        public void StepCountAdaptsToSize()
        {
            const double Tolerance = 0.05;

            int small = Vertices(new EllipseGeometry(new Point(0, 0), 4, 4)
                .GetFlattenedPathGeometry(Tolerance, ToleranceType.Absolute)).Count;
            int large = Vertices(new EllipseGeometry(new Point(0, 0), 400, 400)
                .GetFlattenedPathGeometry(Tolerance, ToleranceType.Absolute)).Count;

            // The whole point of a tolerance: error scales with the shape, so the segment count has
            // to as well. A fixed subdivision (this file's predecessor used 24 steps per curve) gives
            // these two the same count, facets the large one and wastes work on the small one.
            Assert.True(large > small * 4,
                $"a 100x larger circle produced {large} vertices against {small} -- the step count is not adapting");
        }

        [Fact]
        public void TighterToleranceProducesMoreSegments()
        {
            var circle = new EllipseGeometry(new Point(0, 0), 100, 100);

            int coarse = Vertices(circle.GetFlattenedPathGeometry(0.5, ToleranceType.Absolute)).Count;
            int fine = Vertices(circle.GetFlattenedPathGeometry(0.005, ToleranceType.Absolute)).Count;

            Assert.True(fine > coarse, $"tightening the tolerance 100x changed the count from {coarse} to {fine}");
        }

        [Fact]
        public void ArcSegmentIsACurveNotAChord()
        {
            // The regression this suite exists for. The previous managed flattener had the comment
            // "Arcs are rare in control art; approximate by a line to the end point" -- so every
            // rounded rectangle, pie slice and gauge in a WPF app flattened to its corners.
            const double Radius = 100.0;

            // Clockwise, so the arc is the quarter centred on the ORIGIN. Sweep direction chooses
            // between the two circles that pass through both endpoints, and in WPF's y-down space
            // going (r,0) to (0,r) the positive angle direction reads as clockwise on screen.
            var figure = new PathFigure { StartPoint = new Point(Radius, 0) };
            figure.Segments.Add(new ArcSegment(
                point: new Point(0, Radius),
                size: new Size(Radius, Radius),
                rotationAngle: 0,
                isLargeArc: false,
                sweepDirection: SweepDirection.Clockwise,
                isStroked: true));

            var path = new PathGeometry();
            path.Figures.Add(figure);

            List<Point> points = Vertices(path.GetFlattenedPathGeometry(0.05, ToleranceType.Absolute));

            Assert.True(points.Count > 10, $"a quarter arc flattened to {points.Count} points -- it is being chorded");

            foreach (Point p in points)
            {
                // An arc is flattened from its true parameterization, not through a Bezier, so there
                // is no approximation slack to allow for here: the chord error alone.
                Assert.InRange(Math.Sqrt(p.X * p.X + p.Y * p.Y), Radius - 0.05, Radius + 1e-6);
            }
        }

        [Fact]
        public void ArcHonoursSweepDirectionAndLargeArcFlag()
        {
            const double Radius = 50.0;
            Point start = new Point(Radius, 0), end = new Point(0, Radius);

            double small = ArcLength(start, end, Radius, isLargeArc: false, SweepDirection.Counterclockwise);
            double large = ArcLength(start, end, Radius, isLargeArc: true, SweepDirection.Counterclockwise);

            // Quarter circle against three-quarters of one.
            Assert.InRange(small, 0.98 * Radius * Math.PI / 2, 1.02 * Radius * Math.PI / 2);
            Assert.InRange(large, 0.98 * Radius * 3 * Math.PI / 2, 1.02 * Radius * 3 * Math.PI / 2);
        }

        [Fact]
        public void RoundedRectangleKeepsItsCorners()
        {
            var rounded = new RectangleGeometry(new Rect(0, 0, 200, 100), 20, 20);
            double area = Math.Abs(SignedArea(Vertices(rounded.GetFlattenedPathGeometry(0.05, ToleranceType.Absolute))));

            // 200x100 minus the four corner offcuts: each corner removes r^2 - (pi/4)r^2.
            double exact = 200 * 100 - (4 - Math.PI) * 20 * 20;
            Assert.InRange(area, exact - 5, exact + 5);
        }

        [Fact]
        public void TransformIsBakedIn()
        {
            var circle = new EllipseGeometry(new Point(0, 0), 10, 10)
            {
                Transform = new ScaleTransform(3, 3),
            };

            List<Point> points = Vertices(circle.GetFlattenedPathGeometry(0.05, ToleranceType.Absolute));

            Assert.NotEmpty(points);
            foreach (Point p in points)
            {
                AssertOnCircle(Math.Sqrt(p.X * p.X + p.Y * p.Y), 30, 0.05);
            }
        }

        [Fact]
        public void ScaledUpGeometryIsNotFlattenedTooCoarsely()
        {
            // Flattening happens in the geometry's own space but the tolerance is stated in the
            // output's, so the transform's scale has to be divided out. Miss that and a shape
            // magnified 20x is flattened 20x too coarsely -- which is visible faceting, and exactly
            // what a fixed step count produced.
            var circle = new EllipseGeometry(new Point(0, 0), 5, 5)
            {
                Transform = new ScaleTransform(20, 20),
            };

            List<Point> points = Vertices(circle.GetFlattenedPathGeometry(0.05, ToleranceType.Absolute));

            foreach (Point p in points)
            {
                AssertOnCircle(Math.Sqrt(p.X * p.X + p.Y * p.Y), 100, 0.05);
            }
        }

        [Fact]
        public void RelativeToleranceScalesWithTheShape()
        {
            var small = new EllipseGeometry(new Point(0, 0), 10, 10);
            var large = new EllipseGeometry(new Point(0, 0), 1000, 1000);

            int smallCount = Vertices(small.GetFlattenedPathGeometry(0.001, ToleranceType.Relative)).Count;
            int largeCount = Vertices(large.GetFlattenedPathGeometry(0.001, ToleranceType.Relative)).Count;

            // "Relative" means a fraction of the shape's own size, so the same number should buy the
            // same visual fidelity -- and therefore a similar segment count -- at both scales.
            Assert.InRange(largeCount, smallCount / 2, smallCount * 2);
        }

        [Theory]
        [InlineData(0.0, 0.0)]
        [InlineData(double.NaN, 10.0)]
        [InlineData(10.0, double.NaN)]
        public void DegenerateInputsDoNotThrow(double width, double height)
        {
            // Flattening runs underneath drawing and hit testing, where "nothing to draw" is the
            // right answer and an exception is not.
            var ellipse = new EllipseGeometry(new Point(0, 0), width, height);
            PathGeometry flattened = ellipse.GetFlattenedPathGeometry(0.05, ToleranceType.Absolute);
            Assert.NotNull(flattened);
        }

        [Fact]
        public void EmptyGeometryFlattensToNothing()
        {
            PathGeometry flattened = System.Windows.Media.Geometry.Empty
                .GetFlattenedPathGeometry(0.05, ToleranceType.Absolute);

            Assert.NotNull(flattened);
            Assert.Empty(flattened.Figures);
        }

        [Fact]
        public void FlatteningIsIdempotent()
        {
            var circle = new EllipseGeometry(new Point(0, 0), 100, 100);

            PathGeometry once = circle.GetFlattenedPathGeometry(0.05, ToleranceType.Absolute);
            PathGeometry twice = once.GetFlattenedPathGeometry(0.05, ToleranceType.Absolute);

            Assert.Equal(Vertices(once).Count, Vertices(twice).Count);
        }

        // ---- helpers ---------------------------------------------------------------

        /// <summary>
        /// Asserts a flattened vertex sits on a circle of the given radius.
        ///
        /// Two errors stack, and they point opposite ways. Chords cut INSIDE the arc, so flattening
        /// can only lose up to `tolerance`. But an EllipseGeometry is not a circle to begin with:
        /// WPF describes it as four cubic Beziers, and that approximation bulges OUTWARD by about
        /// 2.7e-4 of the radius midway along each quadrant. Asserting a hard upper bound of exactly
        /// r would be testing the ellipse's construction, not the flattener's accuracy.
        /// </summary>
        private static void AssertOnCircle(double measured, double radius, double tolerance)
        {
            const double QuarterArcBezierError = 3e-4;
            Assert.InRange(measured, radius - tolerance, radius * (1 + QuarterArcBezierError));
        }

        private static double ArcLength(Point start, Point end, double radius, bool isLargeArc,
                                        SweepDirection direction)
        {
            var figure = new PathFigure { StartPoint = start };
            figure.Segments.Add(new ArcSegment(end, new Size(radius, radius), 0, isLargeArc, direction, true));

            var path = new PathGeometry();
            path.Figures.Add(figure);

            List<Point> points = Vertices(path.GetFlattenedPathGeometry(0.01, ToleranceType.Absolute));

            double length = 0;
            for (int i = 1; i < points.Count; i++)
            {
                double dx = points[i].X - points[i - 1].X, dy = points[i].Y - points[i - 1].Y;
                length += Math.Sqrt(dx * dx + dy * dy);
            }
            return length;
        }

        /// <summary>Every vertex of a flattened geometry, which must contain only PolyLineSegments.</summary>
        internal static List<Point> Vertices(PathGeometry geometry)
        {
            var points = new List<Point>();
            Matrix m = geometry.Transform?.Value ?? Matrix.Identity;

            foreach (PathFigure figure in geometry.Figures)
            {
                points.Add(m.Transform(figure.StartPoint));
                foreach (PathSegment segment in figure.Segments)
                {
                    // The contract of a FLATTENED geometry: polylines only. Anything else means a
                    // curve survived, which is the failure this assertion is here to name.
                    var line = Assert.IsType<PolyLineSegment>(segment);
                    foreach (Point p in line.Points) points.Add(m.Transform(p));
                }
            }

            return points;
        }

        /// <summary>Shoelace area of a closed polygon.</summary>
        internal static double SignedArea(List<Point> points)
        {
            double sum = 0;
            for (int i = 0, j = points.Count - 1; i < points.Count; j = i++)
            {
                sum += (points[j].X * points[i].Y) - (points[i].X * points[j].Y);
            }
            return sum / 2.0;
        }
    }
}
