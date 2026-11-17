// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// A radial gradient under a rotation or a skew (Brush.Transform, a turned visual) is a turned ellipse: any affine
// image of a circle is one. Only its radii were kept, so a long thin highlight turned on its side lay along its old
// axis.
//

using System;
using System.Numerics;
using Microsoft.Wpf.Interop.WebGpu.Composition;
using WgpuInterop.Tests.Harness;
using Xunit;

namespace WgpuInterop.Tests.Brushes
{
    public sealed class RotatedRadialGradientTests : RendererTestBase
    {
        public RotatedRadialGradientTests(GpuFixture gpu) : base(gpu) { }

        private const int W = 64, H = 64;

        private static readonly GradientStop[] Stops =
        {
            new GradientStop(0f, RgbaColor.FromBytes(255, 0, 0, 255)),
            new GradientStop(1f, RgbaColor.FromBytes(0, 0, 255, 255)),
        };

        private static SceneVisual Scene(RadialGradientBrush brush, bool masked)
        {
            var root = new SceneVisual();
            var fill = new SceneVisual();
            Microsoft.Wpf.Interop.WebGpu.Composition.Geometry g = masked ? new EllipseGeometry(new Vector2(32, 32), 30, 30) : new RectangleGeometry(new Rect(0, 0, 64, 64));
            fill.Content.Add(new GeometryFill(g, brush));
            root.Children.Add(fill);
            return root;
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void ATurnedEllipse_LiesAlongItsTurnedAxis(bool masked)
        {
            // 24 long, 6 wide, turned a quarter: along the vertical
            var turned = new RadialGradientBrush(new Vector2(32, 32), 24, 6, Stops) { Rotation = new Vector2(0, 1) };
            var img = new Image(Render(Scene(turned, masked), W, H), W, H);
            Rgb below = img[32, 46], beside = img[46, 32];
            // 14 along a 24 radius: past half way down the ramp, but well inside -- red in it still
            Assert.True(below.R > 70, $"14 below the centre is inside the turned ellipse (red in it), was {below}");
            Assert.True(beside.B > 200 && beside.R < 60, $"14 beside it is outside (blue), was {beside}");
        }

        [Fact]
        public void FromAxes_FindsTheRadiiAndTheTurn()
        {
            // a unit circle's x going down 24, its y going left 6: the ellipse 24 by 6, turned a quarter
            var b = RadialGradientBrush.FromAxes(new Vector2(10, 10), new Vector2(0, 24), new Vector2(-6, 0), Stops,
                GradientSpreadMethod.Pad, new Vector2(10, 10));
            Assert.Equal(24f, b.RadiusX, 3);
            Assert.Equal(6f, b.RadiusY, 3);
            Assert.True(MathF.Abs(MathF.Abs(b.Rotation.Y) - 1f) < 1e-3f, $"turned a quarter, was {b.Rotation}");
            // a point 20 down from the centre is inside, 20 across is not (focal t below and above one)
            float T(Vector2 p) { Vector2 o = b.ToEllipseFrame(p); return RadialGradientBrush.FocalT(new Vector2(o.X / b.RadiusX, o.Y / b.RadiusY), b.UnitOrigin); }
            Assert.True(T(new Vector2(10, 30)) < 1f);
            Assert.True(T(new Vector2(30, 10)) > 1f);
        }
    }
}
