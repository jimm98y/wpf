// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// A brush is laid out over what it fills in the space that is drawn in, so it turns with a rotation
// pushed around the drawing. Baking the rotation into the geometry and laying the brush over the
// result's axis-aligned box drew it straight: a left-to-right gradient in a quarter-turned square
// still ran left to right, and a VisualBrush in a rotated DrawingGroup (an editor's rotation
// preview) showed its picture upright, stretched over the box and cut by the turned outline.
//

using Microsoft.Wpf.Interop.WebGpu.Composition;
using Microsoft.Wpf.Interop.WebGpu.Composition.Protocol;
using WgpuInterop.Tests.Harness;
using Xunit;

namespace WgpuInterop.Tests.Brushes
{
    public sealed class RotatedBrushTests : RendererTestBase
    {
        public RotatedBrushTests(GpuFixture gpu) : base(gpu) { }

        private const int W = 64, H = 64;
        private const uint HRoot = 2, HContent = 3, HGrad = 4, HRotate = 5;

        [Fact]
        public void AGradientTurnsWithAPushedRotation()
        {
            var e = new MilcoreEngine();
            e.CreateOrAddRef(HRoot, MilResourceTypeId.Visual);
            e.SubmitCommand(MilCmd.LinearGradientBrush(HGrad, 0, 0, 1, 0, mappingMode: 1, new[]
            {
                (0f, 1f, 0f, 0f, 1f),
                (1f, 0f, 0f, 1f, 1f),
            }));
            // a quarter turn clockwise about the square's centre: its left edge goes to the top
            e.SubmitCommand(MilCmd.RotateTransform(HRotate, 90, 32, 32));
            byte[] content = MilCmd.Concat(
                MilCmd.PushTransformRecord(HRotate),
                MilCmd.DrawRectangleRecord(HGrad, 0, 16, 16, 32, 32),
                MilCmd.PopRecord());
            var img = new Image(RenderContent(e, content, W, H, hVisual: HRoot, hContent: HContent), W, H);

            Rgb top = img[32, 19], bottom = img[32, 45], left = img[19, 32], right = img[45, 32];
            Assert.True(top.R > top.B + 100, $"the red end is at the top, was {top}");
            Assert.True(bottom.B > bottom.R + 100, $"the blue end is at the bottom, was {bottom}");
            Assert.True(System.Math.Abs(left.R - right.R) < 40, $"across the turned square it does not change: {left} / {right}");
        }
    }
}
