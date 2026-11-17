// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// A gradient's own transforms, as WPF lays them: Brush.RelativeTransform in the unit space of what it fills,
// Brush.Transform in the drawing's user space -- and an absolute gradient goes where its geometry goes under a
// PushTransform. All three were ignored: a gradient turned by its RelativeTransform ran the way it was declared,
// one scaled by its Transform did not stretch, and one in absolute units stood still while its rectangle moved.
//

using Microsoft.Wpf.Interop.WebGpu.Composition;
using Microsoft.Wpf.Interop.WebGpu.Composition.Protocol;
using WgpuInterop.Tests.Harness;
using Xunit;

namespace WgpuInterop.Tests.Brushes
{
    public sealed class BrushTransformTests : RendererTestBase
    {
        public BrushTransformTests(GpuFixture gpu) : base(gpu) { }

        private const int W = 64, H = 64;
        private const uint HRoot = 2, HContent = 3, HGrad = 4, HXf = 5;

        private static readonly (float, float, float, float, float)[] RedToBlue =
        {
            (0f, 1f, 0f, 0f, 1f),
            (1f, 0f, 0f, 1f, 1f),
        };

        [Fact]
        public void ARelativeTransform_TurnsTheGradientInTheFilledBounds()
        {
            var e = new MilcoreEngine();
            e.CreateOrAddRef(HRoot, MilResourceTypeId.Visual);
            // a quarter turn about the middle of the bounds (relative units): left-to-right becomes top-to-bottom
            e.SubmitCommand(MilCmd.RotateTransform(HXf, 90, 0.5, 0.5));
            e.SubmitCommand(MilCmd.LinearGradientBrush(HGrad, 0, 0.5, 1, 0.5, mappingMode: 1, RedToBlue, hRelativeTransform: HXf));
            byte[] content = MilCmd.DrawRectangleRecord(HGrad, 0, 16, 16, 32, 32);
            var img = new Image(RenderContent(e, content, W, H, hVisual: HRoot, hContent: HContent), W, H);

            Rgb top = img[32, 19], bottom = img[32, 45], left = img[19, 32], right = img[45, 32];
            Assert.True(top.R > top.B + 100, $"the red end is at the top, was {top}");
            Assert.True(bottom.B > bottom.R + 100, $"the blue end is at the bottom, was {bottom}");
            Assert.True(System.Math.Abs(left.R - right.R) < 40, $"across it does not change: {left} / {right}");
        }

        [Fact]
        public void ATransform_StretchesAnAbsoluteGradient()
        {
            var e = new MilcoreEngine();
            e.CreateOrAddRef(HRoot, MilResourceTypeId.Visual);
            // red at 0, blue at 16 -- doubled by the brush's transform, blue at 32
            e.SubmitCommand(MilCmd.ScaleTransform(HXf, 2, 1, 0, 0));
            e.SubmitCommand(MilCmd.LinearGradientBrush(HGrad, 0, 0, 16, 0, mappingMode: 0, RedToBlue, hTransform: HXf));
            byte[] content = MilCmd.DrawRectangleRecord(HGrad, 0, 0, 0, 64, 64);
            var img = new Image(RenderContent(e, content, W, H, hVisual: HRoot, hContent: HContent), W, H);

            Rgb mid = img[16, 32], end = img[40, 32];
            Assert.True(mid.R > 60 && mid.B > 60, $"halfway along the stretched ramp at 16, was {mid}");
            Assert.True(end.B > 200 && end.R < 40, $"past its end at 32 it is blue, was {end}");
        }

        [Fact]
        public void AnAbsoluteGradient_GoesWithItsGeometryUnderAPushTransform()
        {
            var e = new MilcoreEngine();
            e.CreateOrAddRef(HRoot, MilResourceTypeId.Visual);
            e.SubmitCommand(MilCmd.MatrixTransform(HXf, 1, 0, 0, 1, 32, 0));   // over by 32
            e.SubmitCommand(MilCmd.LinearGradientBrush(HGrad, 0, 0, 32, 0, mappingMode: 0, RedToBlue));
            byte[] content = MilCmd.Concat(
                MilCmd.PushTransformRecord(HXf),
                MilCmd.DrawRectangleRecord(HGrad, 0, 0, 0, 32, 64),
                MilCmd.PopRecord());
            var img = new Image(RenderContent(e, content, W, H, hVisual: HRoot, hContent: HContent), W, H);

            Rgb start = img[33, 32], end = img[62, 32];
            Assert.True(start.R > 200 && start.B < 60, $"the red end is at the rectangle's left, 32, was {start}");
            Assert.True(end.B > 200 && end.R < 60, $"the blue end at its right, was {end}");
        }
    }
}
