// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The geometry every head uses to turn what its platform measures about a pen into PenState.
//
// Pure arithmetic, and the kind that is wrong in ways no one notices: a tilt that is merely a few
// degrees short on a diagonal, a twist that comes out negative, a contact box that is the ellipse's
// axes rather than its bounds. Each looks plausible in a debugger and draws subtly wrong ink.
//

using System;
using MS.Internal.Interop;
using Xunit;

namespace Wpf.Platform.Tests
{
    public class PenGeometryTests
    {
        [Fact]
        public void AnUprightPenHasNoTilt()
        {
            PenGeometry.TiltFromSpherical(0, 1, 0, out double x, out double y);
            Assert.Equal(0, x, 6);
            Assert.Equal(0, y, 6);
        }

        [Fact]
        public void ALeanAlongOneAxisIsThatAxisAlone()
        {
            PenGeometry.TiltFromSpherical(30 * Math.PI / 180, 1, 0, out double x, out double y);
            Assert.Equal(30, x, 6);
            Assert.Equal(0, y, 6);

            PenGeometry.TiltFromSpherical(40 * Math.PI / 180, 0, -1, out x, out y);
            Assert.Equal(0, x, 6);
            Assert.Equal(-40, y, 6);
        }

        /// <summary>
        ///  The case the linear shortcut gets wrong: 60 degrees towards a corner is atan(tan 60 *
        ///  cos 45) = 50.77 along each axis, not 60 * cos 45 = 42.4.
        /// </summary>
        [Fact]
        public void ADiagonalLeanIsResolvedExactly()
        {
            double d = Math.Sqrt(0.5);
            PenGeometry.TiltFromSpherical(60 * Math.PI / 180, d, d, out double x, out double y);
            double expected = Math.Atan(Math.Tan(60 * Math.PI / 180) * d) * 180 / Math.PI;
            Assert.Equal(expected, x, 6);
            Assert.Equal(expected, y, 6);
            Assert.True(x > 50, $"a 60 degree diagonal lean came out as {x:F1} degrees per axis");
        }

        /// <summary>A pen lying flat is +/-90 along its lean, not NaN from an infinite tangent.</summary>
        [Fact]
        public void APenLyingFlatIsNinetyDegrees()
        {
            PenGeometry.TiltFromSpherical(Math.PI / 2, -1, 0, out double x, out double y);
            Assert.Equal(-90, x, 6);
            Assert.Equal(0, y, 6);
        }

        [Theory]
        [InlineData(0, 0)]
        [InlineData(359.5, 359.5)]
        [InlineData(360, 0)]
        [InlineData(-90, 270)]
        [InlineData(725, 5)]
        public void TwistFoldsIntoOneTurn(double degrees, double expected)
            => Assert.Equal(expected, PenGeometry.NormalizeDegrees(degrees), 6);

        [Fact]
        public void AnUnreportedTwistStaysUnreported()
            => Assert.True(double.IsNaN(PenGeometry.NormalizeDegrees(double.NaN)));

        /// <summary>
        ///  The contact box is the ellipse's BOUNDS: an upright finger is its axes, one turned a
        ///  quarter is the same box on its side, and one at 45 degrees is a square between the two.
        /// </summary>
        [Fact]
        public void AContactEllipseBecomesItsBoundingBox()
        {
            PenGeometry.EllipseBounds(20, 10, 0, out double w, out double h);
            Assert.Equal(10, w, 6);
            Assert.Equal(20, h, 6);

            PenGeometry.EllipseBounds(20, 10, Math.PI / 2, out w, out h);
            Assert.Equal(20, w, 6);
            Assert.Equal(10, h, 6);

            PenGeometry.EllipseBounds(20, 10, Math.PI / 4, out w, out h);
            Assert.Equal(Math.Sqrt(250), w, 6);
            Assert.Equal(Math.Sqrt(250), h, 6);
        }

        [Fact]
        public void APenStateReportsOnlyWhatWasMeasured()
        {
            PenState finger = PenState.None;
            Assert.False(finger.HasPressure);
            Assert.False(finger.HasTilt);
            Assert.False(finger.HasTwist);
            Assert.False(finger.HasContactSize);
            Assert.Null(finger.BarrelButton);

            PenState pen = new PenState(0.5, 10, 20, true, 45, 3, 4, false);
            Assert.True(pen.HasPressure && pen.HasTilt && pen.HasTwist && pen.HasContactSize);
            Assert.False(pen.BarrelButton!.Value);

            PenState scaled = pen.WithContactSize(1.5, 2);
            Assert.Equal(1.5, scaled.Width);
            Assert.Equal(45, scaled.Twist);
            Assert.True(scaled.IsInverted);
        }
    }
}
