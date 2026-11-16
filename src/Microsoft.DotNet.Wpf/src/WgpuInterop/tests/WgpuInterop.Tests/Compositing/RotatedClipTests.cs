// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// A rectangular clip turns with the visual it clips. As a scissor -- the device bounding box of the
// turned rectangle -- a rotated control's content showed outside its own outline, up to that box:
// a grid's rows ran past its turned border, a tree's scroll bar hung out below it.
//

using System;
using System.Numerics;
using Microsoft.Wpf.Interop.WebGpu.Composition;
using WgpuInterop.Tests.Harness;
using Xunit;

namespace WgpuInterop.Tests.Compositing
{
    public sealed class RotatedClipTests : RendererTestBase
    {
        public RotatedClipTests(GpuFixture gpu) : base(gpu) { }

        private const int W = 64, H = 64;

        [Fact]
        public void ARotatedVisualsRectangleClipIsTurnedToo()
        {
            var root = new SceneVisual();
            var turned = new SceneVisual
            {
                // an eighth of a turn about the centre: the clip square becomes a diamond
                Transform = Matrix3x2.CreateRotation(MathF.PI / 4, new Vector2(32, 32)),
                Clip = new Rect(16, 16, 32, 32),
            };
            turned.Content.Add(new GeometryFill(new RectangleGeometry(new Rect(-32, -32, 128, 128)),
                new SolidColorBrush(RgbaColor.FromBytes(255, 0, 0, 255))));
            root.Children.Add(turned);

            var img = new Image(Render(root, W, H), W, H);
            img.AssertPixel(32, 32, 255, 0, 0, tol: 6, "inside the turned clip");
            img.AssertPixel(32, 14, 255, 0, 0, tol: 6, "the diamond's top point reaches past the square");
            img.AssertPixel(47, 47, 255, 255, 255, tol: 6, "a corner of its bounding box, outside the diamond");
            img.AssertPixel(17, 17, 255, 255, 255, tol: 6, "the opposite corner");
        }
    }
}
