// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Ported from WgpuInterop.StyledStrokeTest and BrushStrokeTest.
//
// Pen styling beyond the round-everything default, and brushes painted onto COVERAGE rather than
// solid colour. Every probe here is chosen to fail under the wrong style rather than merely to
// confirm ink exists: a square cap is checked at a corner a round cap would clip away, a miter at an
// apex a bevel cuts off, a dash gap where a solid line would ink.
//

using System.Collections.Generic;
using System.Numerics;
using Microsoft.Wpf.Interop.WebGpu.Composition;
using WgpuInterop.Tests.Harness;
using Xunit;

namespace WgpuInterop.Tests.Geometry
{
    public sealed class PenStyleTests : RendererTestBase
    {
        public PenStyleTests(GpuFixture gpu) : base(gpu) { }

        private const int W = 96, H = 96;

        private static PathGeometry Path(params (float x, float y)[] points)
        {
            var figure = new PathFigure(new Vector2(points[0].x, points[0].y)) { Closed = false };
            for (int i = 1; i < points.Length; i++)
                figure.Segments.Add(new LineSegment(new Vector2(points[i].x, points[i].y)));
            return new PathGeometry(FillRule.NonZero, new List<PathFigure> { figure });
        }

        private static SceneVisual StyledScene()
        {
            var root = new SceneVisual();
            var content = new SceneVisual();
            var black = RgbaColor.FromBytes(0, 0, 0, 255);

            // Square cap: line y=16, x from 10 to 40, half-width 4.
            content.Content.Add(new GeometryStroke(
                Path((10, 16), (40, 16)), black, new StrokeStyle(8, LineCap.Square)));

            // Miter join at the corner (20,40).
            content.Content.Add(new GeometryStroke(
                Path((20, 60), (20, 40), (40, 40)), black, new StrokeStyle(8, LineCap.Butt, LineJoin.Miter)));

            // Bevel join at the same shape's corner (60,40), for contrast.
            content.Content.Add(new GeometryStroke(
                Path((60, 60), (60, 40), (80, 40)), black, new StrokeStyle(8, LineCap.Butt, LineJoin.Bevel)));

            // Dashes: 8 on / 8 off from x=10 along y=80.
            content.Content.Add(new GeometryStroke(
                Path((10, 80), (86, 80)), black,
                new StrokeStyle(6, LineCap.Butt, LineJoin.Miter, 10, new double[] { 8, 8 }, 0)));

            root.Children.Add(content);
            return root;
        }

        [Theory]
        [InlineData(25, 16, 0, 0, 0, "square-cap line body")]
        [InlineData(25, 4, 255, 255, 255, "clear above the line")]
        [InlineData(43, 13, 0, 0, 0, "square cap corner past the endpoint (a round cap would clip it)")]
        public void SquareCap_ExtendsAsARectangle(int x, int y, int r, int g, int b, string what)
            => new Image(Render(StyledScene(), W, H), W, H).AssertPixel(x, y, r, g, b, tol: 6, what);

        /// <summary>
        /// Miter and bevel at the SAME right-angle corner. The apex probes are the discriminator: a
        /// miter extends a sharp point into (17,37), a bevel cuts it and leaves (57,37) blank.
        /// </summary>
        [Theory]
        [InlineData(20, 50, 0, 0, 0, "miter L arm")]
        [InlineData(17, 37, 0, 0, 0, "miter apex is filled")]
        [InlineData(60, 50, 0, 0, 0, "bevel L arm")]
        [InlineData(59, 39, 0, 0, 0, "bevel fills near the corner")]
        [InlineData(57, 37, 255, 255, 255, "bevel cuts the apex")]
        public void MiterAndBevel_DifferAtTheApex(int x, int y, int r, int g, int b, string what)
            => new Image(Render(StyledScene(), W, H), W, H).AssertPixel(x, y, r, g, b, tol: 6, what);

        [Theory]
        [InlineData(13, 0, 0, 0, "dash 1 (on)")]
        [InlineData(22, 255, 255, 255, "gap 1 (off)")]
        [InlineData(30, 0, 0, 0, "dash 2 (on)")]
        [InlineData(38, 255, 255, 255, "gap 2 (off)")]
        public void Dashes_AlternateInkAndGap(int x, int r, int g, int b, string what)
            => new Image(Render(StyledScene(), W, H), W, H).AssertPixel(x, 80, r, g, b, tol: 6, what);

        [Fact]
        public void StyledStroke_EdgesAreAntiAliased()
            => AssertAntiAliased(Render(StyledScene(), W, H), "styled stroke edges");

        [Fact]
        public void StyledStroke_SurvivesProtocolRoundTrip()
            => AssertProtocolRoundTripIsIdentical(StyledScene(), W, H);
    }

    /// <summary>
    /// Gradient and image brushes painted onto COVERAGE geometry -- filled paths AND strokes -- by
    /// baking the brush per coverage texel, rather than the solid-colour fast path.
    /// </summary>
    public sealed class BrushOnCoverageTests : RendererTestBase
    {
        public BrushOnCoverageTests(GpuFixture gpu) : base(gpu) { }

        private const int W = 96, H = 96;

        private static PathGeometry ClosedRect(float x, float y, float w, float h)
        {
            var f = new PathFigure(new Vector2(x, y)) { Closed = true };
            f.Segments.Add(new LineSegment(new Vector2(x + w, y)));
            f.Segments.Add(new LineSegment(new Vector2(x + w, y + h)));
            f.Segments.Add(new LineSegment(new Vector2(x, y + h)));
            return new PathGeometry(FillRule.NonZero, new List<PathFigure> { f });
        }

        private static SceneVisual BrushScene()
        {
            var root = new SceneVisual();
            var content = new SceneVisual();
            var black = RgbaColor.FromBytes(0, 0, 0, 255);

            GradientStop[] redBlue =
            {
                new(0f, RgbaColor.FromBytes(255, 0, 0, 255)),
                new(1f, RgbaColor.FromBytes(0, 0, 255, 255)),
            };

            content.Content.Add(new GeometryFill(
                ClosedRect(4, 2, 40, 10),
                new LinearGradientBrush(new Vector2(4, 2), new Vector2(44, 2), redBlue)));

            var strokeLine = new PathFigure(new Vector2(4, 29)) { Closed = false };
            strokeLine.Segments.Add(new LineSegment(new Vector2(44, 29)));
            content.Content.Add(new GeometryStroke(
                new PathGeometry(FillRule.NonZero, new List<PathFigure> { strokeLine }),
                new LinearGradientBrush(new Vector2(4, 29), new Vector2(44, 29), redBlue),
                new StrokeStyle(10, LineCap.Round)));

            byte[] checker = { 255, 0, 0, 255, 0, 255, 0, 255, 0, 0, 255, 255, 255, 255, 0, 255 };
            content.Content.Add(new GeometryFill(ClosedRect(4, 40, 24, 24), new ImageBrush(checker, 2, 2)));

            var dashLine = new PathFigure(new Vector2(4, 76)) { Closed = false };
            dashLine.Segments.Add(new LineSegment(new Vector2(80, 76)));
            content.Content.Add(new GeometryStroke(
                new PathGeometry(FillRule.NonZero, new List<PathFigure> { dashLine }),
                black, new StrokeStyle(8, LineCap.Round, LineJoin.Round, 10, new double[] { 10, 16 }, 0)));

            root.Children.Add(content);
            return root;
        }

        private static void AssertReddish(Image img, int x, int y, string what)
        {
            Rgb c = img[x, y];
            Assert.True(c.R > 150 && c.B < 110, $"{what}: expected reddish, was {c}");
        }

        private static void AssertBluish(Image img, int x, int y, string what)
        {
            Rgb c = img[x, y];
            Assert.True(c.B > 150 && c.R < 110, $"{what}: expected bluish, was {c}");
        }

        [Fact]
        public void Gradient_PaintsAFilledPath()
        {
            var img = new Image(Render(BrushScene(), W, H), W, H);
            AssertReddish(img, 7, 7, "gradient fill-path red end");
            AssertBluish(img, 41, 7, "gradient fill-path blue end");
        }

        /// <summary>The stroked BAND itself must carry the ramp, not just the path it was built from.</summary>
        [Fact]
        public void Gradient_PaintsAStroke()
        {
            var img = new Image(Render(BrushScene(), W, H), W, H);
            AssertReddish(img, 8, 29, "gradient stroke red end");
            AssertBluish(img, 40, 29, "gradient stroke blue end");
        }

        // Image brushes are bilinear-sampled (matching WPF), so these probe near clamped texel
        // corners rather than mid-texel, where the value would be the interpolation.
        [Theory]
        [InlineData(6, 42, 255, 0, 0, "image path: top-left red")]
        [InlineData(22, 58, 255, 255, 0, "image path: bottom-right yellow")]
        public void Image_PaintsAFilledPath(int x, int y, int r, int g, int b, string what)
            => new Image(Render(BrushScene(), W, H), W, H).AssertPixel(x, y, r, g, b, tol: 8, what);

        /// <summary>
        /// Dash runs must honour the cap style: a ROUND cap extends each dash past its nominal
        /// length, so x=16 inks even though the dash nominally ends at 14, while x=22 (beyond both
        /// caps) stays blank. A butt cap would leave x=16 blank and a solid line would ink x=22.
        /// </summary>
        [Theory]
        [InlineData(8, 0, 0, 0, "dash body")]
        [InlineData(16, 0, 0, 0, "round cap extends the dash past x=14")]
        [InlineData(22, 255, 255, 255, "true gap beyond both round caps is blank")]
        public void RoundCappedDashes_ExtendPastTheirNominalLength(int x, int r, int g, int b, string what)
            => new Image(Render(BrushScene(), W, H), W, H).AssertPixel(x, 76, r, g, b, tol: 6, what);

        [Fact]
        public void BrushOnCoverage_EdgesAreAntiAliased()
            => AssertAntiAliased(Render(BrushScene(), W, H), "brush-on-coverage edges");

        [Fact]
        public void BrushOnCoverage_SurvivesProtocolRoundTrip()
            => AssertProtocolRoundTripIsIdentical(BrushScene(), W, H);
    }
}
