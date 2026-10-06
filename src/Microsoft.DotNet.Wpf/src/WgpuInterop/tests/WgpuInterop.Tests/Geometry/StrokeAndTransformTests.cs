// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Ported from WgpuInterop.StrokeTest, TransformTest and GeometryGroupTest.
//

using System;
using System.Collections.Generic;
using System.Numerics;
using Microsoft.Wpf.Interop.WebGpu.Composition;
using Microsoft.Wpf.Interop.WebGpu.Composition.Protocol;
using WgpuInterop.Tests.Harness;
using Xunit;

namespace WgpuInterop.Tests.Geometry
{
    public sealed class StrokeTests : RendererTestBase
    {
        public StrokeTests(GpuFixture gpu) : base(gpu) { }

        private const int W = 64, H = 64;

        /// <summary>
        /// An open diagonal line (thickness 8, round cap) and a CLOSED square (thickness 4). The
        /// square is what separates a stroke from a fill: its border inks and its centre does not.
        /// </summary>
        private static SceneVisual StrokeScene()
        {
            var root = new SceneVisual();
            var content = new SceneVisual();
            var black = RgbaColor.FromBytes(0, 0, 0, 255);

            var line = new PathFigure(new Vector2(12, 12)) { Closed = false };
            line.Segments.Add(new LineSegment(new Vector2(52, 52)));
            content.Content.Add(new GeometryStroke(
                new PathGeometry(FillRule.NonZero, new List<PathFigure> { line }),
                black, new StrokeStyle(8, LineCap.Round)));

            var square = new PathFigure(new Vector2(8, 40)) { Closed = true };
            square.Segments.Add(new LineSegment(new Vector2(24, 40)));
            square.Segments.Add(new LineSegment(new Vector2(24, 56)));
            square.Segments.Add(new LineSegment(new Vector2(8, 56)));
            content.Content.Add(new GeometryStroke(
                new PathGeometry(FillRule.NonZero, new List<PathFigure> { square }),
                black, new StrokeStyle(4)));

            root.Children.Add(content);
            return root;
        }

        /// <summary>
        /// Line (12,12)-(52,52) at thickness 8 (half-width 4) with a round cap, and a stroked square.
        /// The (10,10) probe is the cap test specifically: it sits BEYOND the line's endpoint, so a
        /// butt cap would leave it blank.
        /// </summary>
        [Theory]
        [InlineData(32, 32, 0, 0, 0, "on the stroked line")]
        [InlineData(34, 30, 0, 0, 0, "within the stroke thickness")]
        [InlineData(40, 20, 255, 255, 255, "clear of the line")]
        [InlineData(10, 10, 0, 0, 0, "round cap extends past the endpoint")]
        [InlineData(8, 48, 0, 0, 0, "square border is stroked")]
        [InlineData(16, 48, 255, 255, 255, "square centre is hollow (stroke, not fill)")]
        public void Stroke_BehavesLikeAPen(int x, int y, int r, int g, int b, string what)
            => new Image(Render(StrokeScene(), W, H), W, H).AssertPixel(x, y, r, g, b, tol: 6, what);

        [Fact]
        public void Stroke_EdgesAreAntiAliased()
            => AssertAntiAliased(Render(StrokeScene(), W, H), "45-degree stroke edges");

        [Fact]
        public void Stroke_SurvivesProtocolRoundTrip()
            => AssertProtocolRoundTripIsIdentical(StrokeScene(), W, H);
    }

    public sealed class GeometryGroupTests : RendererTestBase
    {
        public GeometryGroupTests(GpuFixture gpu) : base(gpu) { }

        private const int W = 64, H = 48;

        /// <summary>
        /// A rounded rect and an ellipse combined under EvenOdd, so the ellipse cuts a hole and the
        /// result is a frame. Mixing geometry KINDS is the point: the group has to combine them
        /// after each realizes to a path, not special-case a shared shape.
        /// </summary>
        private static SceneVisual GroupScene()
        {
            var root = new SceneVisual();
            var fill = new SceneVisual();
            var group = new GeometryGroup(FillRule.EvenOdd, new List<Microsoft.Wpf.Interop.WebGpu.Composition.Geometry>
            {
                new RoundedRectangleGeometry(new Rect(8, 8, 48, 32), 8, 8),
                new EllipseGeometry(new Vector2(32, 24), 12, 8),
            });
            fill.Content.Add(new GeometryFill(group, RgbaColor.FromBytes(0, 0, 0, 255)));
            root.Children.Add(fill);
            return root;
        }

        [Theory]
        [InlineData(32, 12, 0, 0, 0, "band above the hole is filled")]
        [InlineData(12, 24, 0, 0, 0, "band beside the hole is filled")]
        [InlineData(32, 24, 255, 255, 255, "ellipse cuts a hole")]
        [InlineData(9, 9, 255, 255, 255, "rounded-rect corner is still cut away")]
        [InlineData(2, 2, 255, 255, 255, "outside everything is background")]
        public void EvenOddGroup_CutsAHole(int x, int y, int r, int g, int b, string what)
            => new Image(Render(GroupScene(), W, H), W, H).AssertPixel(x, y, r, g, b, tol: 6, what);

        [Fact]
        public void GeometryGroup_EdgesAreAntiAliased()
            => AssertAntiAliased(Render(GroupScene(), W, H), "geometry group edges");

        /// <summary>Children are serialized recursively, so this exercises nested resource encoding.</summary>
        [Fact]
        public void GeometryGroup_SurvivesProtocolRoundTrip()
            => AssertProtocolRoundTripIsIdentical(GroupScene(), W, H);
    }

    /// <summary>
    /// Ported from WgpuInterop.TransformTest. Unlike the others here this drives MILCMD directly,
    /// because the thing under test is the DECODE of the transform resources: WPF realizes
    /// RenderTransform/LayoutTransform as typed resources that MILCMD_VISUAL_SETTRANSFORM links to a
    /// visual, and a scene built in-process would bypass exactly that step.
    /// </summary>
    public sealed class TransformDecodeTests : RendererTestBase
    {
        public TransformDecodeTests(GpuFixture gpu) : base(gpu) { }

        private const int W = 32, H = 32;

        private byte[] RenderWithTransform(byte[] transform, double x, double y, double w, double h)
        {
            var engine = new MilcoreEngine();
            engine.CreateOrAddRef(2, MilResourceTypeId.Visual);
            engine.SubmitCommand(MilCmd.SolidColorBrush(3, 0, 0, 0, 1));
            engine.SubmitCommand(transform);                     // transform resource at handle 4
            engine.SubmitCommand(MilCmd.VisualSetTransform(2, 4));
            return RenderContent(engine, MilCmd.DrawRectangleRecord(3, 0, x, y, w, h), W, H,
                                 hVisual: 2, hContent: 5);
        }

        private static bool IsInk(Image img, int x, int y)
        {
            Rgb c = img[x, y];
            return c.R < 128 && c.G < 128 && c.B < 128;   // black square on white
        }

        private void AssertInk(byte[] px, int x, int y, bool wantInk, string what)
        {
            var img = new Image(px, W, H);
            Assert.True(IsInk(img, x, y) == wantInk,
                $"{what}: pixel ({x},{y}) was {img[x, y]}, expected {(wantInk ? "ink" : "background")}");
        }

        /// <summary>Translate(10,5): the square (0,0,8,8) must move to (10,5,8,8) and vacate its old position.</summary>
        [Fact]
        public void TranslateTransform_MovesTheGeometry()
        {
            byte[] img = RenderWithTransform(MilCmd.TranslateTransform(4, 10, 5), 0, 0, 8, 8);
            AssertInk(img, 14, 9, true, "translate: square moved to (10,5)");
            AssertInk(img, 4, 4, false, "translate: original position is empty");
        }

        /// <summary>Scale(2,2) about the origin: (0,0,8,8) must become (0,0,16,16).</summary>
        [Fact]
        public void ScaleTransform_EnlargesTheGeometry()
        {
            byte[] img = RenderWithTransform(MilCmd.ScaleTransform(4, 2, 2, 0, 0), 0, 0, 8, 8);
            AssertInk(img, 14, 14, true, "scale: square enlarged to 16x16");
            AssertInk(img, 20, 20, false, "scale: nothing past the scaled bounds");
        }

        /// <summary>
        /// Rotate 90 degrees about (16,16): a HORIZONTAL bar (8,14,16,4) must become a VERTICAL one
        /// (14,8,4,16). A bar rather than a square because a square is rotation-invariant about its
        /// own centre and would pass whatever the transform did.
        /// </summary>
        [Fact]
        public void RotateTransform_RotatesAboutItsCentre()
        {
            byte[] img = RenderWithTransform(MilCmd.RotateTransform(4, 90, 16, 16), 8, 14, 16, 4);
            AssertInk(img, 16, 10, true, "rotate: bar is now vertical (top)");
            AssertInk(img, 16, 22, true, "rotate: bar is now vertical (bottom)");
            AssertInk(img, 10, 16, false, "rotate: the original horizontal extent is empty");
        }
    }
}
