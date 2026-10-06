// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Ported from WgpuInterop.TextTest and WgpuInterop.KerningTest.
//
// These use the BUILT-IN bitmap font rather than a system one, which is why they can assert exact
// pixels: its coverage is binary and its metrics are integral, so at EmSize 28 over a 7px ascent
// every font texel is exactly a 4x4 device block and there is no anti-aliasing to tolerance away.
// That makes them the only text tests in the suite that pin down glyph POSITIONING to the pixel,
// which is what keeps the atlas packing and the per-glyph quad maths honest.
//

using System;
using System.Numerics;
using Microsoft.Wpf.Interop.WebGpu.Composition;
using Microsoft.Wpf.Interop.WebGpu.Composition.Protocol;
using Microsoft.Wpf.Interop.WebGpu.Composition.Text;
using WgpuInterop.Tests.Harness;
using Xunit;

namespace WgpuInterop.Tests.Text
{
    public sealed class GlyphAtlasTests : RendererTestBase
    {
        public GlyphAtlasTests(GpuFixture gpu) : base(gpu) { }

        private const int W = 96, H = 64;

        private static SceneVisual WpfScene()
        {
            var root = new SceneVisual();
            var text = new SceneVisual();
            text.Content.Add(new GlyphRunDraw("WPF", new Vector2(8, 44), 28f, RgbaColor.FromBytes(0, 0, 0, 255)));
            root.Children.Add(text);
            return root;
        }

        /// <summary>
        /// Each case is the centre of a 4x4 device block for a known font texel: strokes must be the
        /// text colour and counters must show the background. "WPF" starts at x=8 with baseline
        /// y=44, so every glyph top is y=16 and glyphs advance 24px (W@8, P@32, F@56).
        /// </summary>
        [Theory]
        [InlineData(90, 2, 255, 255, 255, "background")]
        [InlineData(10, 18, 0, 0, 0, "W left stem (stroke)")]
        [InlineData(14, 18, 255, 255, 255, "W top gap (hole)")]
        [InlineData(18, 30, 0, 0, 0, "W centre prong (stroke)")]
        [InlineData(34, 38, 0, 0, 0, "P left stem (stroke)")]
        [InlineData(42, 34, 255, 255, 255, "P lower-right (hole)")]
        [InlineData(70, 18, 0, 0, 0, "F top bar (stroke)")]
        [InlineData(74, 30, 255, 255, 255, "F right of mid bar (hole)")]
        public void BuiltInFont_RendersExactGlyphPixels(int x, int y, int r, int g, int b, string what)
        {
            byte[] px = NewRenderer().RenderToRgba(WpfScene(), W, H, RgbaColor.FromBytes(255, 255, 255, 255));
            new Image(px, W, H).AssertPixel(x, y, r, g, b, tol: 4, what);
        }

        /// <summary>
        /// A glyph run must survive encode/decode through the DUCE command protocol with no pixel
        /// changing. Byte-identical, not "close": the run carries indices, advances and an origin,
        /// and a marshalling slip in any of them moves glyphs rather than discolouring them.
        /// </summary>
        [Fact]
        public void GlyphRun_SurvivesProtocolRoundTrip_ByteIdentical()
        {
            SceneVisual root = WpfScene();
            var bg = RgbaColor.FromBytes(255, 255, 255, 255);
            WgpuSceneRenderer renderer = NewRenderer();

            byte[] direct = renderer.RenderToRgba(root, W, H, bg);

            byte[] batch = CompositionChannel.EncodeScene(root);
            var engine = new CompositionEngine();
            engine.ProcessBatch(batch);
            SceneVisual rebuilt = engine.Root ?? throw new InvalidOperationException("no root after ProcessBatch");
            byte[] viaProtocol = renderer.RenderToRgba(rebuilt, W, H, bg);

            int diffs = 0;
            for (int i = 0; i < direct.Length; i++) if (direct[i] != viaProtocol[i]) diffs++;
            Assert.True(diffs == 0, $"{diffs} of {direct.Length} bytes differ after a {batch.Length}-byte protocol round-trip");
        }

        // ---- kerning / the shaping seam ----------------------------------------------------
        //
        // Text is shaped (string -> positioned glyphs) by a pluggable ITextShaper before layout, so
        // kerning, ligatures and mark positioning can adjust advances and offsets. The built-in font
        // defines a synthetic A/W kern of -2 cells, so the kerning shaper must pull the second glyph
        // left by exactly 2 * scale device pixels and change nothing else.

        private const float KernEm = 28f;   // scale = EmSize / 7 = 4
        private const int Scale = 4;
        private const int KernCells = 2;
        private const int KW = 64, KH = 64;

        private static SceneVisual AwScene()
        {
            var root = new SceneVisual();
            var text = new SceneVisual();
            text.Content.Add(new GlyphRunDraw("AW", new Vector2(4, 44), KernEm, RgbaColor.FromBytes(0, 0, 0, 255)));
            root.Children.Add(text);
            return root;
        }

        [Fact]
        public void KerningShaper_PullsThePairTogether_ByExactlyTheKern()
        {
            var white = RgbaColor.FromBytes(255, 255, 255, 255);
            using var simple = new WgpuSceneRenderer(Gpu, font: null, shaper: new SimpleTextShaper());
            using var kerned = new WgpuSceneRenderer(Gpu, font: null, shaper: new KerningTextShaper());

            byte[] simplePx = simple.RenderToRgba(AwScene(), KW, KH, white);
            byte[] kernedPx = kerned.RenderToRgba(AwScene(), KW, KH, white);

            int simpleRight = RightmostInk(simplePx);
            int kernedRight = RightmostInk(kernedPx);

            Assert.True(simpleRight > 0 && kernedRight > 0,
                $"both strings must render (simple right edge={simpleRight}, kerned={kernedRight})");
            Assert.True(kernedRight < simpleRight,
                $"kerning must pull the second glyph left (simple={simpleRight}, kerned={kernedRight})");

            int shift = simpleRight - kernedRight;
            Assert.True(Math.Abs(shift - KernCells * Scale) <= 1,
                $"shift must equal kern * scale: got {shift}, expected {KernCells * Scale}");

            Assert.True(LeftmostInk(simplePx) == LeftmostInk(kernedPx),
                "the FIRST glyph must not move: a pair kern adjusts the second glyph only");
        }

        private static int RightmostInk(byte[] px)
        {
            for (int x = KW - 1; x >= 0; x--)
                for (int y = 0; y < KH; y++)
                    if (px[(y * KW + x) * 4] < 128) return x;
            return -1;
        }

        private static int LeftmostInk(byte[] px)
        {
            for (int x = 0; x < KW; x++)
                for (int y = 0; y < KH; y++)
                    if (px[(y * KW + x) * 4] < 128) return x;
            return -1;
        }
    }
}
