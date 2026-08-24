// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Ported from WgpuInterop.GlyphRunTest.
//
// Proves MilcoreEngine renders WPF's DrawGlyphRun from byte-exact MILCMD: MILCMD_GLYPHRUN_CREATE
// (already-shaped glyph indices, advances, baseline origin, em size) followed by the DrawGlyphRun
// record. In a live WPF process the FontResolver maps a native IDWriteFont pointer to a face; here a
// loaded TrueTypeFont is injected directly.
//
// The assertions are positional rather than "some ink exists". Ink in the band alone would pass even
// if every glyph landed on top of the first one, so the margins must be clear AND the run must end
// where its accumulated advances say -- that is what proves advances were applied per glyph.
//

using System;
using Microsoft.Wpf.Interop.WebGpu.Composition;
using Microsoft.Wpf.Interop.WebGpu.Composition.Protocol;
using Microsoft.Wpf.Interop.WebGpu.Composition.Text;
using WgpuInterop.Tests.Harness;
using Xunit;

namespace WgpuInterop.Tests.Protocol
{
    public sealed class GlyphRunDecodeTests : RendererTestBase
    {
        public GlyphRunDecodeTests(GpuFixture gpu) : base(gpu) { }

        private const int W = 160, H = 48;
        private const uint HRoot = 2, HBlack = 3, HRun = 20, HContent = 6;
        private const float EmSize = 32f;
        private const float OriginX = 6f, OriginY = 34f;

        private (byte[] Pixels, int TextEndX) RenderRun(string text)
        {
            TrueTypeFont font = TestFonts.Load();
            float advScale = EmSize / font.PixelsPerEm;

            var indices = new ushort[text.Length];
            var advances = new float[text.Length];
            for (int i = 0; i < text.Length; i++)
            {
                int gid = font.GlyphIndex(text[i]);
                indices[i] = (ushort)gid;
                advances[i] = font.Advance(gid) * advScale;
            }

            var engine = new MilcoreEngine { FontResolver = _ => font };
            engine.CreateOrAddRef(HRoot, MilResourceTypeId.Visual);
            engine.SubmitCommand(MilCmd.SolidColorBrush(HBlack, 0, 0, 0, 1));

            // A glyph run carries a variable-length tail, so it is registered then sent with
            // BeginCommand/EndCommand rather than SubmitCommand.
            engine.CreateOrAddRef(HRun, MilResourceTypeId.Null);
            engine.BeginCommand(MilCmd.GlyphRun(HRun, 0, OriginX, OriginY, EmSize, indices, advances));
            engine.EndCommand();

            byte[] px = RenderContent(engine, MilCmd.DrawGlyphRunRecord(HBlack, HRun), W, H,
                                      hVisual: HRoot, hContent: HContent);

            float total = 0;
            foreach (float a in advances) total += a;
            return (px, (int)(OriginX + total));
        }

        private static int CountInk(byte[] px, int x0, int y0, int x1, int y1)
        {
            int n = 0;
            for (int y = y0; y < y1; y++)
                for (int x = x0; x < x1; x++)
                    if (px[(y * W + x) * 4] < 128) n++;
            return n;
        }

        [Fact]
        public void DrawGlyphRun_PaintsInkInTheTextBand()
        {
            (byte[] px, int textEndX) = RenderRun("HELLO");
            int inBand = CountInk(px, (int)OriginX, 8, textEndX, 36);
            Assert.True(inBand > 40, $"the glyph band should carry real ink, found {inBand} dark pixels");
        }

        /// <summary>
        /// The margins must be clear. Without this, a decode that stacked every glyph at the origin
        /// would still satisfy "ink appears in the band".
        /// </summary>
        [Fact]
        public void DrawGlyphRun_LeavesTheMarginsClear()
        {
            (byte[] px, int textEndX) = RenderRun("HELLO");

            int left = CountInk(px, 0, 0, (int)OriginX - 1, H);
            Assert.True(left == 0, $"nothing should be painted before the run origin, found {left} dark pixels");

            int past = CountInk(px, Math.Min(textEndX + 8, W - 1), 0, W, H);
            Assert.True(past == 0,
                $"nothing should be painted past the accumulated advances (x>{textEndX + 8}), found {past} dark pixels");
        }

        /// <summary>
        /// A wider string must advance further than a narrower one. This is what proves the per-glyph
        /// advances in the command were actually applied, rather than a fixed pitch being assumed.
        /// </summary>
        [Fact]
        public void DrawGlyphRun_AppliesPerGlyphAdvances()
        {
            (byte[] widePx, int wideEnd) = RenderRun("WWW");
            (byte[] narrowPx, int narrowEnd) = RenderRun("lll");

            Assert.True(wideEnd > narrowEnd,
                $"'WWW' should advance further than 'lll': {wideEnd} vs {narrowEnd}");

            int wideInk = CountInk(widePx, (int)OriginX, 8, wideEnd, 36);
            int narrowInk = CountInk(narrowPx, (int)OriginX, 8, narrowEnd, 36);
            Assert.True(wideInk > narrowInk,
                $"'WWW' should lay down more ink than 'lll': {wideInk} vs {narrowInk}");
        }
    }
}
