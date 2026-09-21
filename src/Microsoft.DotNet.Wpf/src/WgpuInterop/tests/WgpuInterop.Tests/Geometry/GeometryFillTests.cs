// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Ported from WgpuInterop.EllipseTest, RoundedRectTest, ClipTest and DrawGeometryTest.
//
// All four drive the analytic-AA coverage path and ask the same three questions of it: are the right
// pixels covered, is the edge anti-aliased, and does the geometry survive the DUCE protocol. The
// per-shape pixel probes are the interesting part and are kept verbatim, because each was chosen to
// distinguish the shape from the one it would degrade into if the geometry were wrong -- an ellipse
// from a circle, a rounded rect from its bounding box, a clip from no clip at all.
//

using System.Collections.Generic;
using System.Numerics;
using Microsoft.Wpf.Interop.WebGpu.Composition;
using WgpuInterop.Tests.Harness;
using Xunit;

namespace WgpuInterop.Tests.Geometry
{
    public sealed class GeometryFillTests : RendererTestBase
    {
        public GeometryFillTests(GpuFixture gpu) : base(gpu) { }

        // ---- ellipse -----------------------------------------------------------------------

        private const int EW = 64, EH = 48;

        private static SceneVisual EllipseScene()
        {
            var root = new SceneVisual();
            var fill = new SceneVisual();
            fill.Content.Add(new GeometryFill(
                new EllipseGeometry(new Vector2(32, 24), 24, 16), RgbaColor.FromBytes(0, 0, 0, 255)));
            root.Children.Add(fill);
            return root;
        }

        /// <summary>
        /// Centre (32,24), rx=24, ry=16 -- wider than tall. The asymmetric probes are the point: 18px
        /// from the centre is INSIDE on x (rx=24) and OUTSIDE on y (ry=16), so a circle would fail
        /// them even though it would pass "interior is filled".
        /// </summary>
        [Theory]
        [InlineData(32, 24, 0, 0, 0, "interior is filled")]
        [InlineData(9, 9, 255, 255, 255, "bounding-box corner is outside the ellipse")]
        [InlineData(50, 24, 0, 0, 0, "wide axis: 18px out on x is still inside")]
        [InlineData(32, 42, 255, 255, 255, "tall axis: 18px out on y is outside")]
        [InlineData(32, 14, 0, 0, 0, "10px out on y is inside")]
        [InlineData(60, 24, 255, 255, 255, "28px out on x is outside")]
        public void Ellipse_CoversTheRightPixels(int x, int y, int r, int g, int b, string what)
        {
            byte[] px = Render(EllipseScene(), EW, EH);
            new Image(px, EW, EH).AssertPixel(x, y, r, g, b, tol: 6, what);
        }

        [Fact]
        public void Ellipse_EdgeIsAntiAliased()
            => AssertAntiAliased(Render(EllipseScene(), EW, EH), "ellipse edge");

        [Fact]
        public void Ellipse_SurvivesProtocolRoundTrip()
            => AssertProtocolRoundTripIsIdentical(EllipseScene(), EW, EH);

        // ---- rounded rectangle -------------------------------------------------------------

        private static SceneVisual RoundedRectScene()
        {
            var root = new SceneVisual();
            var fill = new SceneVisual();
            fill.Content.Add(new GeometryFill(
                new RoundedRectangleGeometry(new Rect(8, 8, 48, 32), 12, 12), RgbaColor.FromBytes(0, 0, 0, 255)));
            root.Children.Add(fill);
            return root;
        }

        /// <summary>Rect (8,8)-(56,40), corner radius 12, so the top-left corner centre is (20,20).</summary>
        [Theory]
        [InlineData(32, 24, 0, 0, 0, "interior is filled")]
        [InlineData(32, 9, 0, 0, 0, "straight top edge is filled")]
        [InlineData(9, 24, 0, 0, 0, "straight left edge is filled")]
        [InlineData(16, 16, 0, 0, 0, "inside the corner arc is filled")]
        [InlineData(9, 9, 255, 255, 255, "bounding-box corner is rounded away")]
        [InlineData(54, 38, 255, 255, 255, "opposite bounding-box corner is rounded away")]
        public void RoundedRect_CoversTheRightPixels(int x, int y, int r, int g, int b, string what)
        {
            byte[] px = Render(RoundedRectScene(), EW, EH);
            new Image(px, EW, EH).AssertPixel(x, y, r, g, b, tol: 6, what);
        }

        [Fact]
        public void RoundedRect_CornerIsAntiAliased()
            => AssertAntiAliased(Render(RoundedRectScene(), EW, EH), "rounded corner");

        [Fact]
        public void RoundedRect_SurvivesProtocolRoundTrip()
            => AssertProtocolRoundTripIsIdentical(RoundedRectScene(), EW, EH);

        // ---- arbitrary clip geometry -------------------------------------------------------

        private const int CW = 64, CH = 64;

        /// <summary>
        /// A rectangle covering nearly the whole canvas, clipped to a triangle. The rectangle is what
        /// makes the test meaningful: every masked-away probe would also be background if the fill
        /// had simply not been drawn, so the fill must be known to cover them.
        /// </summary>
        private static SceneVisual ClipScene()
        {
            var root = new SceneVisual();
            var triangle = new PathFigure(new Vector2(32, 8)) { Closed = true };
            triangle.Segments.Add(new LineSegment(new Vector2(56, 56)));
            triangle.Segments.Add(new LineSegment(new Vector2(8, 56)));

            var clipped = new SceneVisual
            {
                ClipGeometry = new PathGeometry(FillRule.NonZero, new List<PathFigure> { triangle }),
            };
            clipped.Content.Add(new GeometryFill(new RectangleGeometry(new Rect(8, 8, 48, 48)),
                RgbaColor.FromBytes(0, 0, 0, 255)));
            root.Children.Add(clipped);
            return root;
        }

        [Theory]
        [InlineData(32, 40, 0, 0, 0, "inside the clip triangle (painted)")]
        [InlineData(32, 12, 0, 0, 0, "near the apex, inside (painted)")]
        [InlineData(12, 12, 255, 255, 255, "outside the clip, though the rect covers it")]
        [InlineData(52, 15, 255, 255, 255, "outside the clip on the other side")]
        public void Clip_MasksToArbitraryGeometry(int x, int y, int r, int g, int b, string what)
        {
            byte[] px = Render(ClipScene(), CW, CH);
            new Image(px, CW, CH).AssertPixel(x, y, r, g, b, tol: 6, what);
        }

        [Fact]
        public void Clip_EdgeIsAntiAliased()
            => AssertAntiAliased(Render(ClipScene(), CW, CH), "clip edge");

        [Fact]
        public void Clip_SurvivesProtocolRoundTrip()
            => AssertProtocolRoundTripIsIdentical(ClipScene(), CW, CH);

        // ---- fill + stroke in one primitive ------------------------------------------------

        /// <summary>WPF's DrawGeometry(brush, pen, geometry): grey fill, black stroke of width 4.</summary>
        private static SceneVisual DrawGeometryScene()
        {
            var root = new SceneVisual();
            var v = new SceneVisual();
            v.Content.Add(new GeometryDrawing(
                new EllipseGeometry(new Vector2(32, 24), 20, 14),
                new SolidColorBrush(RgbaColor.FromBytes(200, 200, 200, 255)),
                new SolidColorBrush(RgbaColor.FromBytes(0, 0, 0, 255)),
                new StrokeStyle(4)));
            root.Children.Add(v);
            return root;
        }

        [Theory]
        [InlineData(32, 24, 200, 200, 200, "interior is the fill colour")]
        [InlineData(40, 24, 200, 200, 200, "interior off-centre is still the fill")]
        [InlineData(52, 24, 0, 0, 0, "boundary is the stroke, drawn over the fill")]
        [InlineData(60, 24, 255, 255, 255, "outside the stroke is background")]
        public void DrawGeometry_FillsAndStrokes(int x, int y, int r, int g, int b, string what)
        {
            byte[] px = Render(DrawGeometryScene(), EW, EH);
            new Image(px, EW, EH).AssertPixel(x, y, r, g, b, tol: 8, what);
        }

        [Fact]
        public void DrawGeometry_EdgesAreAntiAliased()
            => AssertAntiAliased(Render(DrawGeometryScene(), EW, EH), "fill/stroke edges", hi: 190);

        [Fact]
        public void DrawGeometry_SurvivesProtocolRoundTrip()
            => AssertProtocolRoundTripIsIdentical(DrawGeometryScene(), EW, EH);
    }
}
