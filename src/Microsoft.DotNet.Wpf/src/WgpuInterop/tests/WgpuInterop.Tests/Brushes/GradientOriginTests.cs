// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// A radial gradient's GradientOrigin -- its focus -- as WPF draws it: the ramp starts there (offset 0) and runs out
// to the ellipse, so a focus off the centre makes the bright spot of a glossy button sit up and to the left. It
// was ignored, and every radial gradient started at its centre.
//

using System.Numerics;
using Microsoft.Wpf.Interop.WebGpu.Composition;
using WgpuInterop.Tests.Harness;
using Xunit;

namespace WgpuInterop.Tests.Brushes
{
    public sealed class GradientOriginTests : RendererTestBase
    {
        public GradientOriginTests(GpuFixture gpu) : base(gpu) { }

        private const int W = 64, H = 64;

        private static SceneVisual Scene(Vector2 origin, bool masked)
        {
            var root = new SceneVisual();
            var fill = new SceneVisual();
            var stops = new[]
            {
                new GradientStop(0f, RgbaColor.FromBytes(255, 255, 255, 255)),
                new GradientStop(1f, RgbaColor.FromBytes(0, 0, 0, 255)),
            };
            var brush = new RadialGradientBrush(new Vector2(32, 32), 24, 24, stops, GradientSpreadMethod.Pad, origin);
            // a geometry the GPU masks (an ellipse) as well as a plain rectangle: both paths
            Microsoft.Wpf.Interop.WebGpu.Composition.Geometry g = masked
                ? new EllipseGeometry(new Vector2(32, 32), 24, 24)
                : new RectangleGeometry(new Rect(8, 8, 48, 48));
            fill.Content.Add(new GeometryFill(g, brush));
            root.Children.Add(fill);
            return root;
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void TheRampStartsAtTheFocus(bool masked)
        {
            // the focus half the radius left of the centre
            var img = new Image(Render(Scene(new Vector2(20, 32), masked), W, H), W, H);
            int atFocus = img[20, 32].R, centre = img[32, 32].R, left = img[14, 32].R, right = img[50, 32].R;
            Assert.True(atFocus > 230, $"at the focus it is the first stop (white), was {atFocus}");
            Assert.True(centre < atFocus - 20, $"the centre is no longer the brightest: centre {centre}, focus {atFocus}");
            // 18 from the centre on both sides: the near side of the focus is far brighter than the far side
            Assert.True(left > right + 40, $"the side of the focus is brighter: left {left}, right {right}");
        }

        [Fact]
        public void AFocusAtTheCentre_IsTheGradientAsBefore()
        {
            var img = new Image(Render(Scene(new Vector2(32, 32), masked: false), W, H), W, H);
            Assert.True(img[32, 32].R > 230, $"the centre is the first stop, was {img[32, 32]}");
            Assert.True(System.Math.Abs(img[20, 32].R - img[43, 32].R) <= 8, $"symmetric (pixel centres 11.5 either side): {img[20, 32]} / {img[43, 32]}");
        }
    }
}
