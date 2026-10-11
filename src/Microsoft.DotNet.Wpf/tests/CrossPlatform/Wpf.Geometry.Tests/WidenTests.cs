// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// GetWidenedPathGeometry: the region a pen covers, as a fillable shape.
//
// Every assertion here is closed-form. A straight line of length L stroked with a flat-capped pen of
// width w covers exactly L*w; add round caps and it covers L*w + pi*(w/2)^2. Those are facts about
// pens, not about this implementation, so the tests stay true if the algorithm is ever replaced.
//
// Areas are measured with FilledArea, which integrates under the fill rule. That is not incidental:
// the stroker emits overlapping contours on purpose, so adding up their shoelace areas would count
// the overlaps twice and quietly reward a stroker that produced too much.
//

using System;
using System.Windows;
using System.Windows.Media;
using Xunit;

namespace Wpf.Geometry.Tests
{
    public class WidenTests
    {
        private const double Length = 100.0;
        private const double Thickness = 10.0;

        [Fact]
        public void FlatCappedLineCoversLengthTimesWidth()
        {
            PathGeometry widened = Line().GetWidenedPathGeometry(new Pen(Brushes.Black, Thickness)
            {
                StartLineCap = PenLineCap.Flat,
                EndLineCap = PenLineCap.Flat,
            });

            FilledArea.AssertClose(Length * Thickness, FilledArea.Of(widened), 0.002, "flat-capped line");
        }

        [Fact]
        public void RoundCapsAddADiscSplitBetweenTheTwoEnds()
        {
            PathGeometry widened = Line().GetWidenedPathGeometry(new Pen(Brushes.Black, Thickness)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
            });

            // Two half-discs of radius w/2, which is one whole disc.
            double exact = Length * Thickness + Math.PI * (Thickness / 2) * (Thickness / 2);
            FilledArea.AssertClose(exact, FilledArea.Of(widened), 0.005, "round-capped line");
        }

        [Fact]
        public void SquareCapsExtendByHalfTheWidthAtEachEnd()
        {
            PathGeometry widened = Line().GetWidenedPathGeometry(new Pen(Brushes.Black, Thickness)
            {
                StartLineCap = PenLineCap.Square,
                EndLineCap = PenLineCap.Square,
            });

            // Each cap is a w/2-by-w rectangle, so the two together add w^2.
            FilledArea.AssertClose(Length * Thickness + Thickness * Thickness,
                                   FilledArea.Of(widened), 0.002, "square-capped line");
        }

        [Fact]
        public void TriangleCapsAddHalfWhatSquareCapsDo()
        {
            PathGeometry widened = Line().GetWidenedPathGeometry(new Pen(Brushes.Black, Thickness)
            {
                StartLineCap = PenLineCap.Triangle,
                EndLineCap = PenLineCap.Triangle,
            });

            // A triangle on a base of w reaching w/2 out is half the square cap's rectangle.
            FilledArea.AssertClose(Length * Thickness + Thickness * Thickness / 2,
                                   FilledArea.Of(widened), 0.005, "triangle-capped line");
        }

        [Fact]
        public void StartAndEndCapsAreIndependent()
        {
            // WPF has two cap properties and the renderer's stroker had one, so this is the seam
            // where a port loses a feature quietly: both ends come out looking like the start.
            PathGeometry widened = Line().GetWidenedPathGeometry(new Pen(Brushes.Black, Thickness)
            {
                StartLineCap = PenLineCap.Square,
                EndLineCap = PenLineCap.Flat,
            });

            // One square cap only: half of what two would add.
            FilledArea.AssertClose(Length * Thickness + Thickness * Thickness / 2,
                                   FilledArea.Of(widened), 0.002, "square start, flat end");
        }

        [Fact]
        public void ClosedSquareStrokesAsABandOnItsPerimeter()
        {
            const double Side = 100.0;

            var rect = new RectangleGeometry(new Rect(0, 0, Side, Side));
            PathGeometry widened = rect.GetWidenedPathGeometry(new Pen(Brushes.Black, Thickness));

            // The band is centred on the perimeter, so it runs from a square of side s-w to one of
            // side s+w: the difference is 4*s*w, independent of how the corners are joined (a miter
            // fills exactly the notch a bevel would leave).
            FilledArea.AssertClose(4 * Side * Thickness, FilledArea.Of(widened), 0.005, "stroked square");
        }

        [Fact]
        public void AClosedFigureHasNoCaps()
        {
            var rect = new RectangleGeometry(new Rect(0, 0, 100, 100));

            double flat = FilledArea.Of(rect.GetWidenedPathGeometry(
                new Pen(Brushes.Black, Thickness) { StartLineCap = PenLineCap.Flat }));
            double round = FilledArea.Of(rect.GetWidenedPathGeometry(
                new Pen(Brushes.Black, Thickness) { StartLineCap = PenLineCap.Round }));

            // A closed path has no ends, so the cap style must not change the covered area at all.
            FilledArea.AssertClose(flat, round, 0.001, "closed figure with caps set");
        }

        [Fact]
        public void MiterJoinFillsMoreThanBevelOnASharpCorner()
        {
            PathGeometry corner = Corner(15);   // a narrow wedge, where the miter spike is long

            double miter = FilledArea.Of(corner.GetWidenedPathGeometry(
                new Pen(Brushes.Black, Thickness) { LineJoin = PenLineJoin.Miter, MiterLimit = 100 }));
            double bevel = FilledArea.Of(corner.GetWidenedPathGeometry(
                new Pen(Brushes.Black, Thickness) { LineJoin = PenLineJoin.Bevel }));

            Assert.True(miter > bevel * 1.02,
                $"a miter on a 15-degree corner covered {miter:0.##} against a bevel's {bevel:0.##}");
        }

        [Fact]
        public void MiterLimitCutsTheSpikeBackToABevel()
        {
            PathGeometry corner = Corner(15);

            double limited = FilledArea.Of(corner.GetWidenedPathGeometry(
                new Pen(Brushes.Black, Thickness) { LineJoin = PenLineJoin.Miter, MiterLimit = 1 }));
            double bevel = FilledArea.Of(corner.GetWidenedPathGeometry(
                new Pen(Brushes.Black, Thickness) { LineJoin = PenLineJoin.Bevel }));

            // The limit exists because an almost-doubled-back corner has an unbounded miter. Past it
            // the join must fall back to exactly the bevel.
            FilledArea.AssertClose(bevel, limited, 0.001, "miter past its limit");
        }

        [Fact]
        public void RoundJoinIsBetweenBevelAndMiter()
        {
            PathGeometry corner = Corner(60);

            double bevel = FilledArea.Of(corner.GetWidenedPathGeometry(
                new Pen(Brushes.Black, Thickness) { LineJoin = PenLineJoin.Bevel }));
            double round = FilledArea.Of(corner.GetWidenedPathGeometry(
                new Pen(Brushes.Black, Thickness) { LineJoin = PenLineJoin.Round }));
            double miter = FilledArea.Of(corner.GetWidenedPathGeometry(
                new Pen(Brushes.Black, Thickness) { LineJoin = PenLineJoin.Miter, MiterLimit = 100 }));

            Assert.True(bevel < round && round < miter,
                $"bevel {bevel:0.##}, round {round:0.##}, miter {miter:0.##} are not in order");
        }

        [Fact]
        public void DashesRemoveTheGaps()
        {
            var pen = new Pen(Brushes.Black, Thickness)
            {
                StartLineCap = PenLineCap.Flat,
                EndLineCap = PenLineCap.Flat,
                DashCap = PenLineCap.Flat,
                // Units of pen thickness, so 1-on 1-off is a 10-unit period at this width.
                DashStyle = new DashStyle(new DoubleCollection { 1, 1 }, 0),
            };

            double dashed = FilledArea.Of(Line().GetWidenedPathGeometry(pen));

            FilledArea.AssertClose(Length * Thickness / 2, dashed, 0.02, "50% duty-cycle dashes");
        }

        [Fact]
        public void DashOffsetShiftsThePattern()
        {
            PathGeometry line = Line();

            double atZero = FirstDashStart(line, offset: 0);
            double atHalf = FirstDashStart(line, offset: 1);   // one thickness in, half the period

            Assert.True(atHalf > atZero + 1,
                $"offsetting the dash pattern moved the first dash from {atZero:0.##} to {atHalf:0.##}");
        }

        [Fact]
        public void ADegenerateDashPatternIsTreatedAsSolid()
        {
            // An all-zero pattern is a walk that never advances. Answering "solid" is the only
            // termination that also draws something.
            var pen = new Pen(Brushes.Black, Thickness)
            {
                StartLineCap = PenLineCap.Flat,
                EndLineCap = PenLineCap.Flat,
                DashStyle = new DashStyle(new DoubleCollection { 0, 0 }, 0),
            };

            FilledArea.AssertClose(Length * Thickness, FilledArea.Of(Line().GetWidenedPathGeometry(pen)),
                                   0.002, "zero-length dash pattern");
        }

        [Fact]
        public void ARoundCappedDotIsADisc()
        {
            // A figure with no length at all still marks the page under a round cap. This is how a
            // single click of a pen, or a dotted line's dot, is drawn.
            var figure = new PathFigure { StartPoint = new Point(50, 50) };
            figure.Segments.Add(new LineSegment(new Point(50, 50), true));

            var path = new PathGeometry();
            path.Figures.Add(figure);

            // The tolerance is stated rather than defaulted, and that is the point of this assertion
            // as much as the disc is. A round cap is a polygon, and how close to a circle it gets is
            // exactly what the tolerance buys: at WPF's default of 0.25 a radius-5 cap is a 10-gon,
            // which is 6% light. Ask for 0.01 and it is a 50-gon, within a quarter of a percent.
            PathGeometry widened = path.GetWidenedPathGeometry(
                new Pen(Brushes.Black, Thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round },
                0.01, ToleranceType.Absolute);

            FilledArea.AssertClose(Math.PI * 25, FilledArea.Of(widened), 0.01, "round-capped dot");
        }

        [Fact]
        public void CurvesAreStrokedAlongTheirLength()
        {
            const double Radius = 100.0;

            var circle = new EllipseGeometry(new Point(0, 0), Radius, Radius);
            PathGeometry widened = circle.GetWidenedPathGeometry(new Pen(Brushes.Black, Thickness));

            // An annulus centred on the circle: pi((r+w/2)^2 - (r-w/2)^2), which is 2*pi*r*w.
            FilledArea.AssertClose(2 * Math.PI * Radius * Thickness, FilledArea.Of(widened), 0.01,
                                   "stroked circle");
        }

        [Theory]
        [InlineData(0.0)]
        [InlineData(-5.0)]
        [InlineData(double.NaN)]
        public void ADegeneratePenCoversNothing(double thickness)
        {
            PathGeometry widened = Line().GetWidenedPathGeometry(new Pen(Brushes.Black, thickness));

            Assert.NotNull(widened);
            Assert.Empty(widened.Figures);
        }

        [Fact]
        public void WideningIsNotAffectedByTheSourceFillRule()
        {
            // The stroke follows the outline, and the fill rule describes the interior. Letting one
            // leak into the other is a classic stroker bug.
            PathGeometry evenOdd = Line();
            evenOdd.FillRule = FillRule.EvenOdd;

            var pen = new Pen(Brushes.Black, Thickness);
            FilledArea.AssertClose(FilledArea.Of(Line().GetWidenedPathGeometry(pen)),
                                   FilledArea.Of(evenOdd.GetWidenedPathGeometry(pen)),
                                   0.001, "even-odd source");
        }

        [Fact]
        public void TheResultFillsUnderTheNonZeroRule()
        {
            // Contracts, not areas: the stroker leans on nonzero to union its overlapping pieces, so
            // handing back an even-odd geometry would punch holes at exactly the joins.
            PathGeometry widened = Corner(60).GetWidenedPathGeometry(new Pen(Brushes.Black, Thickness));
            Assert.Equal(FillRule.Nonzero, widened.FillRule);
        }

        // ---- fixtures --------------------------------------------------------------

        private static PathGeometry Line()
        {
            var figure = new PathFigure { StartPoint = new Point(0, 0) };
            figure.Segments.Add(new LineSegment(new Point(Length, 0), true));

            var path = new PathGeometry();
            path.Figures.Add(figure);
            return path;
        }

        /// <summary>Two segments meeting at the origin with the given interior angle, in degrees.</summary>
        private static PathGeometry Corner(double degrees)
        {
            double radians = degrees * Math.PI / 180.0;

            var figure = new PathFigure { StartPoint = new Point(Length, 0) };
            figure.Segments.Add(new LineSegment(new Point(0, 0), true));
            figure.Segments.Add(new LineSegment(
                new Point(Length * Math.Cos(radians), Length * Math.Sin(radians)), true));

            var path = new PathGeometry();
            path.Figures.Add(figure);
            return path;
        }

        /// <summary>The x where the first dash begins, along a line lying on y = 0.</summary>
        private static double FirstDashStart(PathGeometry line, double offset)
        {
            var pen = new Pen(Brushes.Black, Thickness)
            {
                StartLineCap = PenLineCap.Flat,
                EndLineCap = PenLineCap.Flat,
                DashCap = PenLineCap.Flat,
                DashStyle = new DashStyle(new DoubleCollection { 1, 1 }, offset),
            };

            double leftmost = double.MaxValue;
            foreach (var contour in FilledArea.Contours(line.GetWidenedPathGeometry(pen)))
            {
                foreach (Point p in contour) leftmost = Math.Min(leftmost, p.X);
            }
            return leftmost;
        }
    }
}
