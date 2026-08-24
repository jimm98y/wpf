// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Ported from WgpuInterop.DrawingLeafTest.
//
// The LEAF Drawing types inside a DrawingGroup.
//
// DrawingBrush, DrawingGroup and GeometryDrawing were already decoded, so the Drawing object model
// LOOKED supported. It was not: ImageDrawing and GlyphRunDrawing were missing, and an unknown child
// handle just produces an empty visual -- so a DrawingGroup holding an image or text rendered that
// child as NOTHING, with no error anywhere and no missing command in any log.
//
// Every case therefore asserts PIXELS. "The command decodes" is not the same as "the content
// appears", and the gap between them is exactly where this class of bug lives.
//

using Microsoft.Wpf.Interop.WebGpu.Composition;
using Microsoft.Wpf.Interop.WebGpu.Composition.Protocol;
using Microsoft.Wpf.Interop.WebGpu.Composition.Text;
using WgpuInterop.Tests.Harness;
using Xunit;

namespace WgpuInterop.Tests.Protocol
{
    public sealed class DrawingLeafTests : RendererTestBase
    {
        public DrawingLeafTests(GpuFixture gpu) : base(gpu) { }

        private const int W = 64, H = 64;

        private static byte[] SolidRgba(int w, int h, byte r, byte g, byte b)
        {
            var px = new byte[w * h * 4];
            for (int i = 0; i < w * h; i++)
            { px[i * 4] = r; px[i * 4 + 1] = g; px[i * 4 + 2] = b; px[i * 4 + 3] = 255; }
            return px;
        }

        /// <summary>Build the Drawing tree at <paramref name="handle"/> into a visual and render it.</summary>
        private byte[] RenderDrawing(MilcoreEngine e, uint handle)
            => NewRenderer().RenderToRgba(e.BuildDrawingVisualForTest(handle), W, H, White);

        [Fact]
        public void ImageDrawing_InsideADrawingGroup_PaintsItsBitmap()
        {
            var e = new MilcoreEngine();
            e.SetBitmap(50, SolidRgba(4, 4, 220, 40, 40), 4, 4);
            e.SubmitCommand(MilCmd.ImageDrawing(41, 8, 8, 32, 32, 50));
            e.SubmitCommand(MilCmd.DrawingGroup(40, 1.0, new uint[] { 41 }));

            var img = new Image(RenderDrawing(e, 40), W, H);
            img.AssertPixel(24, 24, 220, 40, 40, tol: 40, "the ImageDrawing paints its bitmap");
            img.AssertPixel(2, 2, 255, 255, 255, tol: 40, "outside its rect is untouched");
        }

        /// <summary>
        /// A Drawing used as an ImageSource -- the shape vector icons take. It wraps another Drawing,
        /// so this also covers the recursive resolve.
        /// </summary>
        [Fact]
        public void DrawingImage_RendersTheDrawingItWraps()
        {
            var e = new MilcoreEngine();
            e.SetBitmap(50, SolidRgba(4, 4, 30, 90, 210), 4, 4);
            e.SubmitCommand(MilCmd.ImageDrawing(41, 8, 8, 32, 32, 50));
            e.SubmitCommand(MilCmd.DrawingImage(70, 41));

            new Image(RenderDrawing(e, 70), W, H)
                .AssertPixel(24, 24, 30, 90, 210, tol: 40, "DrawingImage renders the Drawing it wraps");
        }

        [Fact]
        public void GlyphRunDrawing_InsideADrawingGroup_PaintsGlyphs()
        {
            string fontPath = TestFonts.Require();
            var shaper = new TrueTypeFont(System.IO.File.ReadAllBytes(fontPath));

            const float em = 28f;
            float advScale = em / shaper.PixelsPerEm;
            const string text = "WPF";
            var gids = new ushort[text.Length];
            var advs = new float[text.Length];
            for (int i = 0; i < text.Length; i++)
            {
                int gid = shaper.GlyphIndex(text[i]);
                gids[i] = (ushort)gid;
                advs[i] = shaper.Advance(gid) * advScale;
            }

            // Glyph rasterization needs a font resolver; the sink installs one in the real app.
            var resolver = new ManagedFontResolver();
            var e = new MilcoreEngine { ManagedFontResolver = resolver.Resolve };
            e.SubmitCommand(MilCmd.SolidColorBrush(60, 0, 0, 0, 1));

            // A glyph run carries a variable-length tail, so it is registered then sent with
            // BeginCommand/EndCommand rather than SubmitCommand.
            e.CreateOrAddRef(61, MilResourceTypeId.Null);
            e.BeginCommand(MilCmd.GlyphRunWithManagedFont(61, 8, 40, em, gids, advs, fontPath));
            e.EndCommand();

            e.SubmitCommand(MilCmd.GlyphRunDrawing(62, 61, 60));
            e.SubmitCommand(MilCmd.DrawingGroup(63, 1.0, new uint[] { 62 }));

            byte[] px = RenderDrawing(e, 63);
            int dark = 0;
            for (int i = 0; i < W * H; i++) if (px[i * 4] < 100) dark++;

            Assert.True(dark >= 40,
                $"the GlyphRunDrawing painted only {dark} dark pixels; an undecoded child renders as nothing at all");
        }
    }
}
