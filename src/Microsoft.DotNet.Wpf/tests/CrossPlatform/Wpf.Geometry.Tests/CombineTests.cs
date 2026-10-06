// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Geometry.Combine and GetOutlinedPathGeometry: boolean arithmetic on shapes.
//
// These are the tests that would have caught the thing they replace. The previous managed answer
// reduced each operand to the bounding rectangles of its figures, so two overlapping circles
// intersected to a square, and Exclude and Xor returned the first operand untouched. Every one of
// those failures produces a shape, and a plausible-looking one, which is why it survived: nothing
// asserted on the ANSWER.
//
// So the assertions here are areas against closed-form values, plus the identities a boolean has to
// satisfy whatever the shapes are -- union plus intersection equals the sum of the parts, xor is
// union minus intersection, excluding something disjoint changes nothing.
//

using System;
using System.Windows;
using System.Windows.Media;
using Xunit;

// The suite's own namespace is Wpf.Geometry.Tests, so the bare name Geometry resolves to a
// namespace rather than to WPF's type. Alias it once instead of qualifying at every use.
using MediaGeometry = System.Windows.Media.Geometry;

namespace Wpf.Geometry.Tests
{
    public class CombineTests
    {
        // Two 100x100 squares overlapping over a 50x50 corner.
        private static readonly Rect Left = new Rect(0, 0, 100, 100);
        private static readonly Rect Right = new Rect(50, 50, 100, 100);

        private const double Overlap = 50 * 50;
        private const double Each = 100 * 100;

        [Fact]
        public void UnionOfOverlappingSquares()
        {
            Measure(GeometryCombineMode.Union, 2 * Each - Overlap, "union");
        }

        [Fact]
        public void IntersectionOfOverlappingSquares()
        {
            Measure(GeometryCombineMode.Intersect, Overlap, "intersection");
        }

        [Fact]
        public void XorOfOverlappingSquares()
        {
            // The regression case: Xor was not implemented and returned the first operand, which is
            // 10000 rather than 15000, and looks entirely reasonable on screen.
            Measure(GeometryCombineMode.Xor, 2 * Each - 2 * Overlap, "xor");
        }

        [Fact]
        public void ExcludeOfOverlappingSquares()
        {
            Measure(GeometryCombineMode.Exclude, Each - Overlap, "exclude");
        }

        [Fact]
        public void IntersectionOfTwoCirclesIsNotASquare()
        {
            // The other regression case, and the one that gave this away visually: bounding-rectangle
            // arithmetic answers a circle-circle intersection with the rectangle where the two boxes
            // overlap, which is nearly three times too much area.
            const double Radius = 50.0;

            var a = new EllipseGeometry(new Point(0, 0), Radius, Radius);
            var b = new EllipseGeometry(new Point(Radius, 0), Radius, Radius);

            double measured = FilledArea.Of(MediaGeometry.Combine(a, b, GeometryCombineMode.Intersect, null,
                                                             0.01, ToleranceType.Absolute));

            // Two circular segments: 2r^2(theta - sin(theta)cos(theta)) with cos(theta) = d/2r = 1/2,
            // so theta = pi/3. Comes to r^2(2pi/3 - sqrt(3)/2).
            double exact = Radius * Radius * (2 * Math.PI / 3 - Math.Sqrt(3) / 2);
            FilledArea.AssertClose(exact, measured, 0.01, "circle-circle intersection");

            // Stated against the answer the bounding-rectangle code gave, so the test names the
            // regression rather than merely happening to catch it: the two circles' boxes overlap
            // over 50x100, so it reported 5000 where the truth is a little over 3000.
            const double BoundingOverlap = 50 * 100;
            Assert.True(measured < 0.7 * BoundingOverlap,
                $"the intersection measured {measured:0.#}, close to the {BoundingOverlap} of the bounding overlap");
        }

        [Fact]
        public void UnionPlusIntersectionEqualsTheSumOfTheParts()
        {
            // An identity, not a measurement: whatever the shapes, |A|+|B| = |A∪B|+|A∩B|. It holds
            // for any correct implementation and for almost no incorrect one.
            var a = new EllipseGeometry(new Point(0, 0), 60, 40);
            var b = new RectangleGeometry(new Rect(20, -30, 80, 60), 10, 10);

            double areaA = FilledArea.Of(a.GetFlattenedPathGeometry(0.01, ToleranceType.Absolute));
            double areaB = FilledArea.Of(b.GetFlattenedPathGeometry(0.01, ToleranceType.Absolute));
            double union = Area(a, b, GeometryCombineMode.Union);
            double intersect = Area(a, b, GeometryCombineMode.Intersect);

            FilledArea.AssertClose(areaA + areaB, union + intersect, 0.01, "|A|+|B| against |union|+|intersect|");
        }

        [Fact]
        public void XorIsUnionMinusIntersection()
        {
            var a = new EllipseGeometry(new Point(0, 0), 60, 40);
            var b = new RectangleGeometry(new Rect(20, -30, 80, 60));

            double xor = Area(a, b, GeometryCombineMode.Xor);
            double union = Area(a, b, GeometryCombineMode.Union);
            double intersect = Area(a, b, GeometryCombineMode.Intersect);

            FilledArea.AssertClose(union - intersect, xor, 0.02, "xor against union minus intersection");
        }

        [Fact]
        public void ExcludeIsAMinusTheIntersection()
        {
            var a = new EllipseGeometry(new Point(0, 0), 60, 40);
            var b = new RectangleGeometry(new Rect(20, -30, 80, 60));

            double areaA = FilledArea.Of(a.GetFlattenedPathGeometry(0.01, ToleranceType.Absolute));
            double exclude = Area(a, b, GeometryCombineMode.Exclude);
            double intersect = Area(a, b, GeometryCombineMode.Intersect);

            FilledArea.AssertClose(areaA - intersect, exclude, 0.02, "exclude against A minus the intersection");
        }

        [Fact]
        public void DisjointShapesUnionToTheSumAndIntersectToNothing()
        {
            var a = new RectangleGeometry(new Rect(0, 0, 10, 10));
            var b = new RectangleGeometry(new Rect(100, 100, 10, 10));

            // Measured at a finer sampling than the default. These two shapes sit at opposite
            // corners of a mostly empty 110x110 box, so the scanline integrator spends its rows on
            // emptiness and its discretization error lands almost entirely on the two small shapes.
            // That is the measuring tool's limit, not the clipper's.
            const int Fine = 40000;

            FilledArea.AssertClose(200, FilledArea.Of(Combined(a, b, GeometryCombineMode.Union), Fine),
                                   0.001, "disjoint union");
            Assert.Equal(0.0, Area(a, b, GeometryCombineMode.Intersect), 3);
            FilledArea.AssertClose(100, FilledArea.Of(Combined(a, b, GeometryCombineMode.Exclude), Fine),
                                   0.001, "exclude something disjoint");
        }

        [Fact]
        public void SharedBordersDoNotProduceSlivers()
        {
            // Two rectangles meeting exactly along an edge. Coincident edges are where boolean
            // implementations classically fail -- either the border survives as a zero-width sliver
            // or the union splits in two.
            var a = new RectangleGeometry(new Rect(0, 0, 50, 100));
            var b = new RectangleGeometry(new Rect(50, 0, 50, 100));

            PathGeometry union = MediaGeometry.Combine(a, b, GeometryCombineMode.Union, null, 0.01, ToleranceType.Absolute);

            FilledArea.AssertClose(100 * 100, FilledArea.Of(union), 0.001, "union across a shared border");
            Assert.Equal(0.0, Area(a, b, GeometryCombineMode.Intersect), 3);
        }

        [Fact]
        public void AShapeCombinedWithItself()
        {
            var a = new EllipseGeometry(new Point(0, 0), 50, 50);

            double self = FilledArea.Of(a.GetFlattenedPathGeometry(0.01, ToleranceType.Absolute));

            FilledArea.AssertClose(self, Area(a, a, GeometryCombineMode.Union), 0.01, "A union A");
            FilledArea.AssertClose(self, Area(a, a, GeometryCombineMode.Intersect), 0.01, "A intersect A");
            Assert.Equal(0.0, Area(a, a, GeometryCombineMode.Xor), 0);
            Assert.Equal(0.0, Area(a, a, GeometryCombineMode.Exclude), 0);
        }

        [Fact]
        public void AHoleSurvivesTheCombination()
        {
            // A ring intersected with a square that covers it: the hole has to come through. This is
            // the test for contour ORIENTATION -- a hole must wind opposite to the shape around it,
            // or the nonzero rule fills it in and the ring becomes a disc.
            var ring = Ring(outer: 50, inner: 25);
            var square = new RectangleGeometry(new Rect(-100, -100, 200, 200));

            double area = Area(ring, square, GeometryCombineMode.Intersect);
            double exact = Math.PI * (50 * 50 - 25 * 25);

            FilledArea.AssertClose(exact, area, 0.02, "ring intersected with a covering square");
        }

        [Fact]
        public void ContainmentIsRecognised()
        {
            var big = new RectangleGeometry(new Rect(0, 0, 100, 100));
            var small = new RectangleGeometry(new Rect(25, 25, 50, 50));

            FilledArea.AssertClose(10000, Area(big, small, GeometryCombineMode.Union), 0.001, "union with a contained shape");
            FilledArea.AssertClose(2500, Area(big, small, GeometryCombineMode.Intersect), 0.001, "intersect with a contained shape");

            // Excluding an interior shape punches a hole, which is again an orientation question.
            FilledArea.AssertClose(7500, Area(big, small, GeometryCombineMode.Exclude), 0.005, "exclude a contained shape");
        }

        [Fact]
        public void EvenOddOperandsAreReadUnderTheirOwnFillRule()
        {
            // A ring drawn as two same-wound circles reads as a disc under nonzero and as a ring
            // under even-odd. The combiner has to ask each operand under ITS OWN rule.
            PathGeometry evenOddRing = Ring(50, 25);
            evenOddRing.FillRule = FillRule.EvenOdd;

            PathGeometry nonZeroRing = Ring(50, 25);
            nonZeroRing.FillRule = FillRule.Nonzero;

            var cover = new RectangleGeometry(new Rect(-100, -100, 200, 200));

            double asRing = Area(evenOddRing, cover, GeometryCombineMode.Intersect);
            double asDisc = Area(nonZeroRing, cover, GeometryCombineMode.Intersect);

            FilledArea.AssertClose(Math.PI * (2500 - 625), asRing, 0.02, "even-odd ring");
            FilledArea.AssertClose(Math.PI * 2500, asDisc, 0.02, "same figures read as nonzero");
        }

        [Fact]
        public void TheTransformAppliesToTheResult()
        {
            var a = new RectangleGeometry(new Rect(0, 0, 10, 10));
            var b = new RectangleGeometry(new Rect(0, 0, 10, 10));

            PathGeometry scaled = MediaGeometry.Combine(a, b, GeometryCombineMode.Union, new ScaleTransform(3, 3),
                                                   0.01, ToleranceType.Absolute);

            FilledArea.AssertClose(900, FilledArea.Of(scaled), 0.001, "union scaled 3x");
        }

        [Fact]
        public void EmptyOperandsBehave()
        {
            var a = new RectangleGeometry(new Rect(0, 0, 10, 10));
            MediaGeometry empty = MediaGeometry.Empty;

            FilledArea.AssertClose(100, Area(a, empty, GeometryCombineMode.Union), 0.001, "union with empty");
            Assert.Equal(0.0, Area(a, empty, GeometryCombineMode.Intersect), 3);
            FilledArea.AssertClose(100, Area(a, empty, GeometryCombineMode.Exclude), 0.001, "exclude empty");
            Assert.Equal(0.0, Area(empty, a, GeometryCombineMode.Exclude), 3);
        }

        [Fact]
        public void OutlineRemovesSelfIntersection()
        {
            // A five-pointed star drawn as one self-crossing contour. Under even-odd its centre is a
            // hole; outlining must re-express that as contours that no longer cross, WITHOUT changing
            // which region is filled.
            PathGeometry star = Star(points: 5, outer: 100);
            star.FillRule = FillRule.EvenOdd;

            double before = FilledArea.Of(star.GetFlattenedPathGeometry(0.01, ToleranceType.Absolute));
            PathGeometry outlined = star.GetOutlinedPathGeometry(0.01, ToleranceType.Absolute);

            FilledArea.AssertClose(before, FilledArea.Of(outlined), 0.02, "star area through outlining");

            // The point of outlining: the answer no longer relies on a fill rule to be read.
            Assert.Equal(FillRule.Nonzero, outlined.FillRule);
        }

        [Fact]
        public void OutlineOfASimpleShapeIsItself()
        {
            var square = new RectangleGeometry(new Rect(0, 0, 100, 50));

            double outlined = FilledArea.Of(square.GetOutlinedPathGeometry(0.01, ToleranceType.Absolute));
            FilledArea.AssertClose(5000, outlined, 0.002, "outline of a plain rectangle");
        }

        [Fact]
        public void DegenerateInputsAnswerRatherThanThrow()
        {
            var a = new RectangleGeometry(new Rect(0, 0, 10, 10));
            var nan = new RectangleGeometry(new Rect(0, 0, double.NaN, 10));
            var zero = new RectangleGeometry(new Rect(0, 0, 0, 0));

            // Combining runs underneath clipping, so an exception here would take out rendering for
            // a shape the user merely cannot see.
            Assert.NotNull(MediaGeometry.Combine(a, nan, GeometryCombineMode.Union, null, 0.01, ToleranceType.Absolute));
            Assert.NotNull(MediaGeometry.Combine(a, zero, GeometryCombineMode.Intersect, null, 0.01, ToleranceType.Absolute));
            Assert.NotNull(MediaGeometry.Combine(nan, zero, GeometryCombineMode.Xor, null, 0.01, ToleranceType.Absolute));
        }

        // ---- helpers ---------------------------------------------------------------

        private static void Measure(GeometryCombineMode mode, double exact, string what)
        {
            double measured = Area(new RectangleGeometry(Left), new RectangleGeometry(Right), mode);
            FilledArea.AssertClose(exact, measured, 0.002, what);
        }

        private static double Area(MediaGeometry a, MediaGeometry b, GeometryCombineMode mode)
            => FilledArea.Of(Combined(a, b, mode));

        private static PathGeometry Combined(MediaGeometry a, MediaGeometry b, GeometryCombineMode mode)
            => MediaGeometry.Combine(a, b, mode, null, 0.01, ToleranceType.Absolute);

        /// <summary>An annulus as two concentric circles wound the same way.</summary>
        private static PathGeometry Ring(double outer, double inner)
        {
            var group = new GeometryGroup();
            group.Children.Add(new EllipseGeometry(new Point(0, 0), outer, outer));
            group.Children.Add(new EllipseGeometry(new Point(0, 0), inner, inner));
            group.FillRule = FillRule.EvenOdd;

            return group.GetFlattenedPathGeometry(0.01, ToleranceType.Absolute);
        }

        /// <summary>A star as a single self-crossing contour.</summary>
        private static PathGeometry Star(int points, double outer)
        {
            var figure = new PathFigure { IsClosed = true };
            var vertices = new PointCollection();

            for (int i = 0; i <= points; i++)
            {
                // Stepping two points at a time is what makes the contour cross itself.
                double angle = i * 2 * (2 * Math.PI / points) - Math.PI / 2;
                var p = new Point(Math.Cos(angle) * outer, Math.Sin(angle) * outer);

                if (i == 0) figure.StartPoint = p;
                else vertices.Add(p);
            }

            figure.Segments.Add(new PolyLineSegment(vertices, true));

            var path = new PathGeometry();
            path.Figures.Add(figure);
            return path;
        }
    }
}
