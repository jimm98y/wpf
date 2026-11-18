// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// A VisualBrush placed by its Stretch and alignment is drawn from its source rendered on the GPU, as a plain Fill
// one is -- not rendered and read back to the CPU every frame. Uniform fits the source's picture in the shape and
// leaves the rest unpainted; UniformToFill covers the shape with the part of it that fits.
//

using System.Collections;
using System.Reflection;
using Microsoft.Wpf.Interop.WebGpu.Composition;
using Microsoft.Wpf.Interop.WebGpu.Composition.Protocol;
using WgpuInterop.Tests.Harness;
using Xunit;

namespace WgpuInterop.Tests.Brushes
{
    public sealed class VisualBrushStretchTests : RendererTestBase
    {
        public VisualBrushStretchTests(GpuFixture gpu) : base(gpu) { }

        private const int W = 60, H = 20;
        private const uint HRoot = 1, HContent = 2, HSrc = 10, HSrcContent = 11, HRed = 12, HBlue = 13, HBrush = 14;

        private (Image Pixels, MilcoreEngine Engine) Draw(uint stretch)
        {
            var e = new MilcoreEngine();
            e.CreateOrAddRef(HRoot, MilResourceTypeId.Visual);
            e.CreateOrAddRef(HSrc, MilResourceTypeId.Visual);
            e.SubmitCommand(MilCmd.SolidColorBrush(HRed, 1, 0, 0, 1));
            e.SubmitCommand(MilCmd.SolidColorBrush(HBlue, 0, 0, 1, 1));
            // the source: a 20 x 20 square, its left half red, its right half blue
            var src = new System.Collections.Generic.List<byte>();
            src.AddRange(MilCmd.DrawRectangleRecord(HRed, 0, 0, 0, 10, 20));
            src.AddRange(MilCmd.DrawRectangleRecord(HBlue, 0, 10, 0, 10, 20));
            Realize(e, src.ToArray(), HSrc, HSrcContent);
            byte[] brush = MilCmd.ImageBrush(HBrush, HSrc, stretch);
            brush[0] = 0x83;   // MILCMD_VISUALBRUSH: the TileBrush layout, the source visual where the image would be
            e.SubmitCommand(brush);
            SceneVisual root = Realize(e, MilCmd.DrawRectangleRecord(HBrush, 0, 0, 0, W, H), HRoot, HContent);
            e.Realize();
            root = e.VisualByHandle(HRoot)!;
            return (new Image(NewRenderer().RenderToRgba(root, W, H, White), W, H), e);
        }

        private static int Count(MilcoreEngine e, string field) =>
            ((System.Collections.Generic.HashSet<uint>)typeof(MilcoreEngine).GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(e)!).Count;

        [Fact]
        public void Uniform_fits_the_source_in_the_middle_from_the_GPU()
        {
            var (img, e) = Draw(2);
            Assert.True(Count(e, "_brushesGpuLive") > 0 && Count(e, "_contentBrushes2D") == 0, "drawn from the GPU, not read back");
            Assert.True(img[5, 10].R > 240 && img[5, 10].B > 240, "left of the picture: unpainted");
            Assert.True(img[24, 10].R > 200 && img[24, 10].B < 60, "the picture's red half");
            Assert.True(img[35, 10].B > 200 && img[35, 10].R < 60, "the picture's blue half");
            Assert.True(img[55, 10].R > 240 && img[55, 10].G > 240, "right of it: unpainted");
        }

        [Fact]
        public void UniformToFill_covers_the_shape_with_the_middle_of_the_source()
        {
            var (img, e) = Draw(3);
            Assert.True(Count(e, "_brushesGpuLive") > 0 && Count(e, "_contentBrushes2D") == 0, "drawn from the GPU, not read back");
            // a 60-wide shape: the 20 x 20 source scaled 3x, its middle third of rows shown -- red left half, blue right
            Assert.True(img[10, 10].R > 200 && img[10, 10].B < 60, "red across the left half");
            Assert.True(img[50, 10].B > 200 && img[50, 10].R < 60, "blue across the right half");
        }
    }
}
