// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Geometry.Bounds, which this port computes managed on every platform.
//
// It used to call the native milcore helper (MilUtility_PathGeometryBounds) on Windows, and that
// lives in wpfgfx_cor3.dll, which the port does not ship -- so a plain FlowDocumentScrollViewer threw
// DllNotFoundException, because a scrollbar's template contains Path shapes and Shape.MeasureOverride
// asks a shape for its bounds. Nothing exotic: any Path, Ellipse or curved geometry reaches this.
//
// The expected values below are worked out from the geometry rather than recorded from a previous
// run. That matters here more than usual: the managed implementation this replaced returned a
// deliberately LOOSE box (the convex hull of each Bezier's control points, and each arc inflated by
// its full radii), which is a valid superset and would sail past any assertion phrased as "contains
// the shape". Bounds feed layout, so loose means curved shapes measure bigger than they draw; these
// tests therefore pin the TIGHT answer.
//

using System;
using System.Windows;
using System.Windows.Media;
using Xunit;

namespace Wpf.Text.Tests
{
    public class GeometryBoundsTests
    {
        // Curve extremes are irrational in general; a hundredth of a pixel is far tighter than any
        // layout cares about and far looser than the difference between tight and hull bounds.
        private const double Tolerance = 0.01;

        private static void AssertBounds(Rect expected, Rect actual)
        {
            Assert.True(
                Math.Abs(expected.Left - actual.Left) < Tolerance &&
                Math.Abs(expected.Top - actual.Top) < Tolerance &&
                Math.Abs(expected.Right - actual.Right) < Tolerance &&
                Math.Abs(expected.Bottom - actual.Bottom) < Tolerance,
                $"expected {expected}, got {actual}");
        }

        [Fact]
        public void ALineHasTheBoundsOfItsEndpoints()
        {
            var g = new PathGeometry();
            var f = new PathFigure { StartPoint = new Point(10, 20) };
            f.Segments.Add(new LineSegment(new Point(40, 5), true));
            g.Figures.Add(f);

            AssertBounds(new Rect(10, 5, 30, 15), g.Bounds);
        }

        [Fact]
        public void ARectangleGeometryHasExactBounds()
        {
            var g = new RectangleGeometry(new Rect(3, 7, 20, 11));

            AssertBounds(new Rect(3, 7, 20, 11), g.Bounds);
        }

        /// <summary>
        /// A circle of radius r about c occupies exactly [c-r, c+r]. WPF builds an EllipseGeometry
        /// from four Beziers, so this is really asking whether curved bounds are solved: the control
        /// hull of those Beziers is noticeably larger than the circle, which is what the previous
        /// implementation would have returned.
        /// </summary>
        [Fact]
        public void ACircleIsBoundedByItsRadiusNotItsControlHull()
        {
            var g = new EllipseGeometry(new Point(50, 50), 30, 30);

            AssertBounds(new Rect(20, 20, 60, 60), g.Bounds);
        }

        [Fact]
        public void AnEllipseIsBoundedByItsRadii()
        {
            var g = new EllipseGeometry(new Point(0, 0), 40, 15);

            AssertBounds(new Rect(-40, -15, 80, 30), g.Bounds);
        }

        /// <summary>
        /// A symmetric cubic whose controls sit well outside the curve. Both ends are at y=0 and both
        /// controls at y=90; the curve's peak is at t=0.5, which is 3/4 of the way to the control
        /// height, so y spans [0, 67.5] and not [0, 90]. x is monotonic, so it spans the endpoints.
        /// </summary>
        [Fact]
        public void ACubicIsBoundedByItsCurveNotItsControls()
        {
            var g = new PathGeometry();
            var f = new PathFigure { StartPoint = new Point(0, 0) };
            f.Segments.Add(new BezierSegment(new Point(0, 90), new Point(100, 90), new Point(100, 0), true));
            g.Figures.Add(f);

            AssertBounds(new Rect(0, 0, 100, 67.5), g.Bounds);
        }

        /// <summary>
        /// The quadratic equivalent: ends at y=0, control at y=80, peak at t=0.5 reaching half the
        /// control height.
        /// </summary>
        [Fact]
        public void AQuadraticIsBoundedByItsCurveNotItsControl()
        {
            var g = new PathGeometry();
            var f = new PathFigure { StartPoint = new Point(0, 0) };
            f.Segments.Add(new QuadraticBezierSegment(new Point(50, 80), new Point(100, 0), true));
            g.Figures.Add(f);

            AssertBounds(new Rect(0, 0, 100, 40), g.Bounds);
        }

        /// <summary>
        /// A quarter circle from (100,0) to (0,100) about the origin, going anticlockwise. It stays
        /// inside the first quadrant's square, so the bounds are exactly [0,100] on both axes -- the
        /// previous implementation would have inflated both endpoints by the full radius and returned
        /// [-100, 200], twice the size in each direction.
        /// </summary>
        [Fact]
        public void AnArcIsBoundedByTheArcNotItsRadii()
        {
            var g = new PathGeometry();
            var f = new PathFigure { StartPoint = new Point(100, 0) };
            f.Segments.Add(new ArcSegment(new Point(0, 100), new Size(100, 100), 0,
                                          isLargeArc: false, SweepDirection.Counterclockwise, true));
            g.Figures.Add(f);

            AssertBounds(new Rect(0, 0, 100, 100), g.Bounds);
        }

        /// <summary>
        /// The same endpoints, radii and direction as above, differing ONLY in the large-arc flag,
        /// which is what isolates it.
        ///
        /// Two circles of radius 100 pass through both points, and the flag chooses between them:
        /// the small counterclockwise arc curves about (100,100) and stays in [0,100], while the
        /// large one curves about the origin and sweeps past (0,-100) and (-100,0), so it occupies
        /// the whole circle. Only the extreme angles the sweep actually crosses can tell these
        /// apart -- the endpoints are identical.
        /// </summary>
        [Fact]
        public void TheLargeArcBetweenTheSameEndpointsIsBigger()
        {
            var g = new PathGeometry();
            var f = new PathFigure { StartPoint = new Point(100, 0) };
            f.Segments.Add(new ArcSegment(new Point(0, 100), new Size(100, 100), 0,
                                          isLargeArc: true, SweepDirection.Counterclockwise, true));
            g.Figures.Add(f);

            AssertBounds(new Rect(-100, -100, 200, 200), g.Bounds);
        }

        /// <summary>
        /// A stroked geometry grows by half the pen thickness on every side; layout depends on it,
        /// so a shape's rendered bounds include its outline.
        /// </summary>
        [Fact]
        public void APenInflatesTheBoundsByHalfItsThickness()
        {
            var g = new RectangleGeometry(new Rect(10, 10, 20, 20));

            Rect stroked = g.GetRenderBounds(new Pen(Brushes.Black, 6));

            AssertBounds(new Rect(7, 7, 26, 26), stroked);
        }

        /// <summary>
        /// The geometry's own transform has to be applied, and to the TIGHT box: scaling a loose box
        /// scales the error with it.
        /// </summary>
        [Fact]
        public void TheGeometryTransformIsApplied()
        {
            var g = new EllipseGeometry(new Point(0, 0), 10, 10)
            {
                Transform = new ScaleTransform(3, 2),
            };

            AssertBounds(new Rect(-30, -20, 60, 40), g.Bounds);
        }

        [Fact]
        public void AnEmptyGeometryHasEmptyBounds()
        {
            var g = new PathGeometry();

            Assert.True(g.Bounds.IsEmpty, $"an empty geometry should have empty bounds, got {g.Bounds}");
        }

        /// <summary>
        /// Several figures union. A geometry built from disjoint pieces must report the box that
        /// covers all of them.
        /// </summary>
        [Fact]
        public void MultipleFiguresUnion()
        {
            var g = new PathGeometry();

            var a = new PathFigure { StartPoint = new Point(0, 0) };
            a.Segments.Add(new LineSegment(new Point(10, 10), true));
            g.Figures.Add(a);

            var b = new PathFigure { StartPoint = new Point(90, 80) };
            b.Segments.Add(new LineSegment(new Point(100, 100), true));
            g.Figures.Add(b);

            AssertBounds(new Rect(0, 0, 100, 100), g.Bounds);
        }
    }
}
