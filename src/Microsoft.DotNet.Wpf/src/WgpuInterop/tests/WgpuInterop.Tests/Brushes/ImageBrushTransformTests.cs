// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// An image brush's Transform and RelativeTransform move its picture after it is laid out over the filled bounds
// (RelativeTransform in their unit space, Transform in user space), as for any brush. Both were ignored: a flipped
// or turned image brush was drawn as declared.
//

using Microsoft.Wpf.Interop.WebGpu.Composition;
using Microsoft.Wpf.Interop.WebGpu.Composition.Protocol;
using WgpuInterop.Tests.Harness;
using Xunit;

namespace WgpuInterop.Tests.Brushes
{
    public sealed class ImageBrushTransformTests : RendererTestBase
    {
        public ImageBrushTransformTests(GpuFixture gpu) : base(gpu) { }

        private const int W = 32, H = 32;
        private const uint HRoot = 2, HContent = 3, HImg = 4, HBrush = 5, HTransform = 6;

        // a wide 4 x 1 image: red, red, green, green (each half solid under the smoothing)
        private static readonly byte[] Wide = { 255, 0, 0, 255, 255, 0, 0, 255, 0, 255, 0, 255, 0, 255, 0, 255 };

        private static bool Red(Rgb p) => p.R > 200 && p.G < 80 && p.B < 80;
        private static bool Green(Rgb p) => p.G > 120 && p.R < 80 && p.B < 80;
        private static bool Empty(Rgb p) => p.R > 220 && p.G > 220 && p.B > 220;

        private Image Draw(byte[] transform, bool relative, double w = 32, double h = 32)
        {
            var e = new MilcoreEngine();
            e.CreateOrAddRef(HRoot, MilResourceTypeId.Visual);
            e.SetBitmap(HImg, Wide, 4, 1);
            e.SubmitCommand(transform);
            e.SubmitCommand(MilCmd.ImageBrush(HBrush, HImg, stretch: 1, hTransform: relative ? 0 : HTransform, hRelativeTransform: relative ? HTransform : 0));
            return new Image(RenderContent(e, MilCmd.DrawRectangleRecord(HBrush, 0, 0, 0, w, h), W, H, hVisual: HRoot, hContent: HContent), W, H);
        }

        [Fact]
        public void ARelativeQuarterTurn_PutsTheLeftHalfOnTop()
        {
            Image img = Draw(MilCmd.RotateTransform(HTransform, 90, 0.5, 0.5), relative: true);
            Assert.True(Red(img[16, 6]), $"red on top, was {img[16, 6]}");
            Assert.True(Green(img[16, 26]), $"green below, was {img[16, 26]}");
        }

        [Fact]
        public void AMirroringTransform_PutsTheLeftHalfOnTheRight()
        {
            Image img = Draw(MilCmd.ScaleTransform(HTransform, -1, 1, 16, 0), relative: false);
            Assert.True(Green(img[6, 16]), $"green on the left, was {img[6, 16]}");
            Assert.True(Red(img[26, 16]), $"red on the right, was {img[26, 16]}");
        }

        [Fact]
        public void AQuarterTurnOfAWidePicture_StandsUpInTheMiddle_Undistorted()
        {
            // a 32 x 16 rectangle; the picture turned about its centre is 16 wide and 32 tall, so the
            // rectangle shows its middle 16 columns: red above, green below, nothing either side
            Image img = Draw(MilCmd.RotateTransform(HTransform, 90, 16, 8), relative: false, h: 16);
            Assert.True(Empty(img[3, 8]) && Empty(img[28, 8]), $"nothing beside the turned picture: {img[3, 8]} {img[28, 8]}");
            Assert.True(Red(img[16, 3]), $"red at the top, was {img[16, 3]}");
            Assert.True(Green(img[16, 13]), $"green at the bottom, was {img[16, 13]}");
        }
    }
}
