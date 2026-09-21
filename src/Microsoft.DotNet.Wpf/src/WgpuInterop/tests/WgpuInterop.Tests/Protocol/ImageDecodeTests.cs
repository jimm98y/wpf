// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Ported from WgpuInterop.ImageTest.
//
// WPF's DrawImage / ImageBrush reference a bitmap whose pixels arrive OUT OF BAND: PresentationCore
// copies them with its managed imaging stack (no COM) and hands the sink straight BGRA32 bytes via
// IMilCompositionSink.SendBitmap. Two ingestion routes are covered -- MilcoreEngine.SetBitmap
// directly, and the real WpfCompositionSink.SendBitmap byte path, which also has to convert
// BGRA -> RGBA.
//
// Sampling note that applies throughout: image brushes are bilinear-sampled (matching WPF), so an
// upscaled checker blends across texel boundaries. Every probe sits near the OUTER corner of a texel
// region, well away from the centre boundary, where bilinear clamps to the pure texel colour.
// Sampling mid-region would measure the interpolation instead of the mapping.
//

using Microsoft.Wpf.Interop.WebGpu.Composition;
using Microsoft.Wpf.Interop.WebGpu.Composition.Protocol;
using WgpuInterop.Tests.Harness;
using Xunit;

namespace WgpuInterop.Tests.Protocol
{
    public sealed class ImageDecodeTests : RendererTestBase
    {
        public ImageDecodeTests(GpuFixture gpu) : base(gpu) { }

        private const int W = 80, H = 80;
        private const uint HRoot = 2, HContent = 5;
        private const uint HImg = 10, HImgBrush = 11, HImgWide = 12,
                           HImgBrushUniform = 13, HImg4 = 14, HImgBrushViewbox = 16;
        private const uint HImgBrushUtf = 15;

        // 2x2 checker, row-major straight RGBA: row0 = red, green; row1 = blue, yellow.
        private static readonly byte[] Checker =
        {
            255, 0, 0, 255,   0, 255, 0, 255,
            0, 0, 255, 255,   255, 255, 0, 255,
        };

        // 2x1 for the Uniform letterbox case.
        private static readonly byte[] Wide = { 255, 0, 0, 255, 0, 255, 0, 255 };

        /// <summary>4x4 RGBA, rows red/green/blue/yellow, for the UniformToFill crop case.</summary>
        private static byte[] Rows4()
        {
            (byte, byte, byte)[] rows = { (255, 0, 0), (0, 255, 0), (0, 0, 255), (255, 255, 0) };
            var px = new byte[4 * 4 * 4];
            for (int y = 0; y < 4; y++)
                for (int x = 0; x < 4; x++)
                {
                    int i = (y * 4 + x) * 4;
                    px[i] = rows[y].Item1; px[i + 1] = rows[y].Item2; px[i + 2] = rows[y].Item3; px[i + 3] = 255;
                }
            return px;
        }

        private byte[] RenderAllCases()
        {
            var engine = new MilcoreEngine();
            engine.SetBitmap(HImg, Checker, 2, 2);
            engine.SetBitmap(HImgWide, Wide, 2, 1);
            engine.SetBitmap(HImg4, Rows4(), 4, 4);

            engine.CreateOrAddRef(HRoot, MilResourceTypeId.Visual);
            engine.SubmitCommand(MilCmd.ImageBrush(HImgBrush, HImg, stretch: 1));             // Fill
            engine.SubmitCommand(MilCmd.ImageBrush(HImgBrushUniform, HImgWide, stretch: 2));  // Uniform
            engine.SubmitCommand(MilCmd.ImageBrush(HImgBrushUtf, HImg4, stretch: 3));         // UniformToFill
            // Viewbox crop: the RIGHT column of the checker (green, yellow), stretched to Fill.
            engine.SubmitCommand(MilCmd.ImageBrush(HImgBrushViewbox, HImg, 1, 0.5, 0, 0.5, 1));

            byte[] content = MilCmd.Concat(
                MilCmd.DrawImageRecord(4, 4, 32, 32, HImg),
                MilCmd.DrawRoundedRectangleRecord(HImgBrush, 4, 44, 32, 32),
                MilCmd.DrawRoundedRectangleRecord(HImgBrushUniform, 44, 4, 32, 32),
                MilCmd.DrawRoundedRectangleRecord(HImgBrushUtf, 44, 44, 32, 16),
                MilCmd.DrawRoundedRectangleRecord(HImgBrushViewbox, 44, 62, 32, 16));

            return RenderContent(engine, content, W, H, hVisual: HRoot, hContent: HContent);
        }

        /// <summary>DrawImage stretches the bitmap across its rectangle: quadrants of 16px within 4..36.</summary>
        [Theory]
        [InlineData(8, 8, 255, 0, 0, "DrawImage top-left = red")]
        [InlineData(32, 8, 0, 255, 0, "DrawImage top-right = green")]
        [InlineData(8, 32, 0, 0, 255, "DrawImage bottom-left = blue")]
        [InlineData(32, 32, 255, 255, 0, "DrawImage bottom-right = yellow")]
        public void DrawImage_StretchesAcrossItsRect(int x, int y, int r, int g, int b, string what)
            => new Image(RenderAllCases(), W, H).AssertPixel(x, y, r, g, b, tol: 8, what);

        /// <summary>An ImageBrush maps the bitmap across the SHAPE's bounds rather than a given rect.</summary>
        [Theory]
        [InlineData(8, 48, 255, 0, 0, "ImageBrush top-left = red")]
        [InlineData(32, 48, 0, 255, 0, "ImageBrush top-right = green")]
        [InlineData(8, 72, 0, 0, 255, "ImageBrush bottom-left = blue")]
        [InlineData(32, 72, 255, 255, 0, "ImageBrush bottom-right = yellow")]
        public void ImageBrush_Fill_MapsAcrossTheShapeBounds(int x, int y, int r, int g, int b, string what)
            => new Image(RenderAllCases(), W, H).AssertPixel(x, y, r, g, b, tol: 8, what);

        /// <summary>
        /// Uniform preserves aspect and LETTERBOXES: a 2x1 image in a 32x32 rect (44..76, 4..36) fills a
        /// centred band (12..28) and leaves the top and bottom clear. The letterbox probes are the point
        /// -- Fill would cover them, so they distinguish the two stretch modes.
        /// </summary>
        [Theory]
        [InlineData(48, 20, 255, 0, 0, "Uniform: band left = red")]
        [InlineData(72, 20, 0, 255, 0, "Uniform: band right = green")]
        [InlineData(60, 8, 255, 255, 255, "Uniform: top letterbox is clear")]
        [InlineData(60, 32, 255, 255, 255, "Uniform: bottom letterbox is clear")]
        public void ImageBrush_Uniform_Letterboxes(int x, int y, int r, int g, int b, string what)
            => new Image(RenderAllCases(), W, H).AssertPixel(x, y, r, g, b, tol: 8, what);

        /// <summary>
        /// UniformToFill preserves aspect and CROPS: the 4x4 (red/green/blue/yellow rows) into 32x16
        /// covers fully and shows the centre rows. The corner probe proves there is no letterbox.
        /// </summary>
        [Theory]
        [InlineData(60, 46, 0, 255, 0, "UniformToFill: centre crop top row = green")]
        [InlineData(60, 58, 0, 0, 255, "UniformToFill: centre crop bottom row = blue")]
        [InlineData(46, 46, 0, 255, 0, "UniformToFill: corner is covered, no letterbox")]
        public void ImageBrush_UniformToFill_Crops(int x, int y, int r, int g, int b, string what)
            => new Image(RenderAllCases(), W, H).AssertPixel(x, y, r, g, b, tol: 8, what);

        /// <summary>A Viewbox selects a sub-rect of the SOURCE: here the right column (green, yellow).</summary>
        [Theory]
        [InlineData(60, 64, 0, 255, 0, "Viewbox: cropped source top = green")]
        [InlineData(60, 76, 255, 255, 0, "Viewbox: cropped source bottom = yellow")]
        public void ImageBrush_Viewbox_CropsTheSource(int x, int y, int r, int g, int b, string what)
            => new Image(RenderAllCases(), W, H).AssertPixel(x, y, r, g, b, tol: 8, what);

        /// <summary>
        /// The REAL ingestion path: WpfCompositionSink.SendBitmap receives straight BGRA32 (as
        /// PresentationCore marshals it with no COM) and must convert to RGBA. Feeding the same
        /// checker in BGRA and getting the same quadrants back is what proves the swap happens --
        /// a missing conversion would swap red and blue, which every other test here would miss
        /// because they inject RGBA directly.
        /// </summary>
        [Theory]
        [InlineData(8, 8, 255, 0, 0, "sink BGRA path top-left = red")]
        [InlineData(32, 8, 0, 255, 0, "sink BGRA path top-right = green")]
        [InlineData(8, 32, 0, 0, 255, "sink BGRA path bottom-left = blue")]
        [InlineData(32, 32, 255, 255, 0, "sink BGRA path bottom-right = yellow")]
        public void SinkSendBitmap_ConvertsBgraToRgba(int x, int y, int r, int g, int b, string what)
        {
            // The same checker in straight BGRA32: red, green, blue, yellow.
            byte[] bgra =
            {
                0, 0, 255, 255,   0, 255, 0, 255,
                255, 0, 0, 255,   0, 255, 255, 255,
            };

            using var sink = new WpfCompositionSink();
            sink.SendBitmap(0, HImg, 2, 2, 8, bgra);

            MilcoreEngine engine = sink.Engine;
            engine.CreateOrAddRef(HRoot, MilResourceTypeId.Visual);
            SceneVisual root = Realize(engine, MilCmd.DrawImageRecord(4, 4, 32, 32, HImg),
                                       hVisual: HRoot, hContent: HContent);

            byte[] px = sink.Renderer.RenderToRgba(root, W, H, White);
            new Image(px, W, H).AssertPixel(x, y, r, g, b, tol: 8, what);
        }
    }
}
