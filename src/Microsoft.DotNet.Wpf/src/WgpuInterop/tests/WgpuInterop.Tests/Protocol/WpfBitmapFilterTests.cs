// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// A WPF bitmap is filtered as WPF's own rasterizer filters it (milcore CBilinearSpan, which stock
// RenderTargetBitmap and software-rendered windows use), not by the GPU's sampler: the 16.16 fixed
// point device-to-texture mapping, the weight truncated to 1/256, four premultiplied 8-bit texels
// in one sum (fs_textured_wpf). The GPU sampler weighed at its own precision (7 bits here) and on
// a float mapping, and a D3DImage stretched into a window came out one level off along a band
// edge: 344 pixels against stock.
//
// The expected values are stock's: rendered by stock WPF's RenderTargetBitmap from the same
// bitmap and rectangle (scratchpad harness d3dh, modes "bands" and "ramp2").
//

using System;
using Microsoft.Wpf.Interop.WebGpu.Composition.Protocol;
using WgpuInterop.Tests.Harness;
using Xunit;

namespace WgpuInterop.Tests.Protocol
{
    public sealed class WpfBitmapFilterTests : RendererTestBase
    {
        public WpfBitmapFilterTests(GpuFixture gpu) : base(gpu) { }

        private const uint HRoot = 2, HContent = 5, HImg = 10;

        private byte[] Render(byte[] rgba, int iw, int ih, double x, double y, double w, double h, int tw, int th)
        {
            var engine = new MilcoreEngine();
            engine.SetBitmap(HImg, rgba, iw, ih);
            engine.CreateOrAddRef(HRoot, MilResourceTypeId.Visual);
            return RenderContent(engine, MilCmd.DrawImageRecord(x, y, w, h, HImg), tw, th, hVisual: HRoot, hContent: HContent);
        }

        /// <summary>Three 64-row bands, 256x192, stretched to 344x241 at (20, 20): every row of stock's
        /// picture, including the two blended rows at the band edges.</summary>
        [Fact]
        public void AStretchedBitmap_MatchesStockRowForRow()
        {
            (byte R, byte G, byte B)[] bands = { (0x20, 0xC0, 0x80), (0x20, 0x80, 0xC0), (0xC0, 0x80, 0x20) };
            var px = new byte[256 * 192 * 4];
            for (int i = 0; i < 256 * 192; i++)
            {
                var c = bands[i / 256 / 64];
                px[i * 4] = c.R; px[i * 4 + 1] = c.G; px[i * 4 + 2] = c.B; px[i * 4 + 3] = 255;
            }
            byte[] got = Render(px, 256, 192, 20, 20, 344, 241, 384, 281);

            // stock RenderTargetBitmap, each row (the picture is uniform along a row)
            (int Y, byte R, byte G, byte B)[] stock = { (20, 32, 192, 128), (100, 32, 152, 168), (101, 32, 128, 192), (180, 90, 128, 134), (181, 192, 128, 32), (261, 255, 255, 255) };
            int wrong = 0;
            string first = "";
            for (int y = 0; y < 281; y++)
            {
                (byte R, byte G, byte B) want = (255, 255, 255);
                foreach (var s in stock) if (y >= s.Y) want = (s.R, s.G, s.B);
                for (int x = 20; x < 364; x++)
                {
                    int i = (y * 384 + x) * 4;
                    if (got[i] != want.R || got[i + 1] != want.G || got[i + 2] != want.B)
                    {
                        if (wrong++ == 0) first = $"({x},{y}) = {got[i]},{got[i + 1]},{got[i + 2]}, stock {want.R},{want.G},{want.B}";
                    }
                }
            }
            Assert.True(wrong == 0, $"{wrong} pixels differ from stock; first {first}");
        }

        /// <summary>A 2x2 stretched to 512x512: every pixel a different pair of weights. Stock's
        /// 786,432 samples fit milcore's C filter with no error; this is that filter.</summary>
        [Fact]
        public void EveryWeightPair_IsMilcoresBilinear()
        {
            // texels (R, G, B): (0,0,0) (255,255,100) / (0,255,37) (0,0,255)
            byte[] px = { 0, 0, 0, 255, 255, 255, 100, 255, 0, 255, 37, 255, 0, 0, 255, 255 };
            byte[] got = Render(px, 2, 2, 0, 0, 512, 512, 512, 512);
            int wrong = 0;
            string first = "";
            for (int y = 0; y < 512; y++)
                for (int x = 0; x < 512; x++)
                {
                    // milcore: u = (x + 0.5) * (2 / 512) - 0.5 texels, in 16.16; weight (u >> 8) & 0xff
                    int u = (x * 256) + 128 - 32768, v = (y * 256) + 128 - 32768;
                    int x0 = Math.Clamp(u >> 16, 0, 1), x1 = Math.Clamp((u >> 16) + 1, 0, 1);
                    int y0 = Math.Clamp(v >> 16, 0, 1), y1 = Math.Clamp((v >> 16) + 1, 0, 1);
                    int fx = (u >> 8) & 0xff, fy = (v >> 8) & 0xff;
                    for (int c = 0; c < 3; c++)
                    {
                        int T(int tx, int ty) => px[(ty * 2 + tx) * 4 + c];
                        int want = ((256 - fy) * ((T(x0, y0) << 8) + (T(x1, y0) - T(x0, y0)) * fx)
                                    + fy * ((T(x0, y1) << 8) + (T(x1, y1) - T(x0, y1)) * fx) + 32768) >> 16;
                        int have = got[(y * 512 + x) * 4 + c];
                        if (have != want && wrong++ == 0) first = $"({x},{y}) channel {c} = {have}, milcore {want}";
                    }
                }
            Assert.True(wrong == 0, $"{wrong} samples differ from milcore's filter; first {first}");
        }
    }
}
