// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// A tile brush places its picture by its Stretch and alignment, in the viewport and in every tile: an image fitted
// (Uniform) to the top of its area leaves the rest empty, and tiled, each tile holds it so. The alignment was
// ignored (the picture always centred), and a tiled picture was stretched over each tile whatever its Stretch.
//

using Microsoft.Wpf.Interop.WebGpu.Composition;
using Microsoft.Wpf.Interop.WebGpu.Composition.Protocol;
using WgpuInterop.Tests.Harness;
using Xunit;

namespace WgpuInterop.Tests.Brushes
{
    public sealed class TileBrushAlignmentTests : RendererTestBase
    {
        public TileBrushAlignmentTests(GpuFixture gpu) : base(gpu) { }

        private const int W = 32, H = 32;
        private const uint HRoot = 2, HContent = 3, HImg = 4, HBrush = 5;

        // a wide 2 x 1 image: red, green
        private static readonly byte[] Wide = { 255, 0, 0, 255, 0, 255, 0, 255 };

        private static bool Painted(Rgb p) => p.B < 60;   // red or green; the background is white

        [Fact]
        public void AUniformPictureAlignedToTheTop_LeavesTheBottomEmpty()
        {
            var e = new MilcoreEngine();
            e.CreateOrAddRef(HRoot, MilResourceTypeId.Visual);
            e.SetBitmap(HImg, Wide, 2, 1);
            e.SubmitCommand(MilCmd.ImageBrush(HBrush, HImg, stretch: 2, alignmentY: 0));   // Uniform, Top
            var img = new Image(RenderContent(e, MilCmd.DrawRectangleRecord(HBrush, 0, 0, 0, 32, 32), W, H, hVisual: HRoot, hContent: HContent), W, H);

            Assert.True(Painted(img[8, 4]) && Painted(img[24, 12]), $"the top half holds the picture: {img[8, 4]} {img[24, 12]}");
            Assert.False(Painted(img[8, 22]), $"the bottom half is empty, was {img[8, 22]}");
            Assert.False(Painted(img[24, 28]), $"the bottom half is empty, was {img[24, 28]}");
        }

        [Fact]
        public void TiledAndUniform_EachTileHoldsThePictureAsPlaced()
        {
            var e = new MilcoreEngine();
            e.CreateOrAddRef(HRoot, MilResourceTypeId.Visual);
            e.SetBitmap(HImg, Wide, 2, 1);
            // four 16 x 16 tiles; in each the picture is 16 x 8, at the top
            e.SubmitCommand(MilCmd.ImageBrush(HBrush, HImg, stretch: 2, tileMode: 4, vpW: 0.5, vpH: 0.5, alignmentY: 0));
            var img = new Image(RenderContent(e, MilCmd.DrawRectangleRecord(HBrush, 0, 0, 0, 32, 32), W, H, hVisual: HRoot, hContent: HContent), W, H);

            Assert.True(Painted(img[4, 3]) && Painted(img[20, 19]), $"the top of each tile holds the picture: {img[4, 3]} {img[20, 19]}");
            Assert.False(Painted(img[4, 12]), $"the bottom of a tile is empty, was {img[4, 12]}");
            Assert.False(Painted(img[20, 28]), $"the bottom of a tile is empty, was {img[20, 28]}");
        }
    }
}
