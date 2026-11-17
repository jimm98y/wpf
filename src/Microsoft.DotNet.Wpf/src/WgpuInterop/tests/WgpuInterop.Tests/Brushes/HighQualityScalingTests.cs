// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// RenderOptions.BitmapScalingMode = HighQuality (Fant): an image drawn smaller than it is is sampled from its
// mipmaps, trilinearly, as milcore's hardware path samples it. Sampled bilinearly from the full image, a
// downscaled picture skips most of its pixels: fine lines come and go (moire) and it shimmers as it moves.
// The default (Unspecified, Linear) stays bilinear, as in WPF 4 and later.
//

using Microsoft.Wpf.Interop.WebGpu.Composition;
using Microsoft.Wpf.Interop.WebGpu.Composition.Protocol;
using WgpuInterop.Tests.Harness;
using Xunit;

namespace WgpuInterop.Tests.Brushes
{
    public sealed class HighQualityScalingTests : RendererTestBase
    {
        public HighQualityScalingTests(GpuFixture gpu) : base(gpu) { }

        private const int W = 24, H = 24, Side = 64, Drawn = 21;
        private const uint HRoot = 2, HContent = 3, HImg = 4, HBrush = 5;

        // one-pixel vertical stripes, black and white
        private static byte[] Stripes()
        {
            var px = new byte[Side * Side * 4];
            for (int y = 0; y < Side; y++)
                for (int x = 0; x < Side; x++)
                {
                    int o = (y * Side + x) * 4;
                    byte v = (byte)(x % 2 == 0 ? 0 : 255);
                    px[o] = px[o + 1] = px[o + 2] = v; px[o + 3] = 255;
                }
            return px;
        }

        private (int Min, int Max) Range(uint scalingMode)
        {
            var e = new MilcoreEngine();
            e.CreateOrAddRef(HRoot, MilResourceTypeId.Visual);
            e.SetBitmap(HImg, Stripes(), Side, Side);
            e.SubmitCommand(MilCmd.SetRenderOptions(HRoot, flags: 0x1, edgeMode: 0, compositingMode: 0, bitmapScalingMode: scalingMode,
                                                    clearTypeHint: 0, textRenderingMode: 0, textHintingMode: 0));
            e.SubmitCommand(MilCmd.ImageBrush(HBrush, HImg, stretch: 1));
            var img = new Image(RenderContent(e, MilCmd.DrawRectangleRecord(HBrush, 0, 0, 0, Drawn, Drawn), W, H, hVisual: HRoot, hContent: HContent), W, H);
            int min = 255, max = 0;
            for (int y = 2; y < Drawn - 2; y++)
                for (int x = 2; x < Drawn - 2; x++) { min = System.Math.Min(min, img[x, y].G); max = System.Math.Max(max, img[x, y].G); }
            return (min, max);
        }

        [Fact]
        public void HighQuality_AveragesWhatADownscaleLeavesOut()
        {
            (int min, int max) = Range(2);
            Assert.True(min > 90 && max < 170, $"stripes a third of a pixel wide come out grey, were {min}..{max}");
        }

        [Fact]
        public void TheDefault_StaysBilinear()
        {
            (int min, int max) = Range(0);
            Assert.True(min < 60 || max > 195, $"bilinear from the full image keeps picking single stripes, was {min}..{max}");
        }
    }
}
