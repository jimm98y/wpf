// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Colour BITMAP glyphs (CBDT/CBLC) -- the emoji format Linux and Android actually ship.
//
// This is the other half of colour text. COLR/CPAL (CompositeAndColorGlyphTests) describes a glyph
// as coloured outlines and is what Windows and the SDK's vendored face use; CBDT/CBLC stores whole
// PNGs and is what Noto Color Emoji is. A font in that format has no 'glyf' or 'loca' at all, so it
// used to fail to load outright, the resolver returned null, and emoji rendered as nothing.
//
// The font is not vendored: it is ~10 MB and only needed to test with. The tests skip when the
// machine has none, and WGPU_TEST_EMOJI_BITMAP points at one explicitly.
//

using System;
using System.IO;
using Microsoft.Wpf.Interop.WebGpu.Composition;
using Microsoft.Wpf.Interop.WebGpu.Composition.Protocol;
using Microsoft.Wpf.Interop.WebGpu.Composition.Text;
using WgpuInterop.Tests.Harness;
using Xunit;

namespace WgpuInterop.Tests.Text
{
    public sealed class BitmapGlyphTests : RendererTestBase
    {
        public BitmapGlyphTests(GpuFixture gpu) : base(gpu) { }

        // Emoji that exist in every colour emoji font.
        private static readonly int[] Candidates = { 0x1F600 /* grin */, 0x2764 /* heart */, 0x1F602, 0x26A1 };

        private static TrueTypeFont RequireBitmapFont()
        {
            string? path = TestFonts.FindBitmapEmojiFont();
            Assert.SkipWhen(path is null,
                "no CBDT/CBLC colour bitmap emoji font on this machine (set WGPU_TEST_EMOJI_BITMAP to one)");

            return new TrueTypeFont(File.ReadAllBytes(path!));
        }

        /// <summary>
        /// The load itself is the regression. A CBDT font has no outlines, and requiring 'glyf' and
        /// 'loca' threw here -- which is what made emoji invisible off Windows, several layers away
        /// from anything that mentioned fonts.
        /// </summary>
        [Fact]
        public void ABitmapOnlyFontLoads()
        {
            TrueTypeFont font = RequireBitmapFont();

            Assert.True(font is IBitmapGlyphFont, "TrueTypeFont does not implement IBitmapGlyphFont");
            Assert.True(font.GlyphCount > 0, "the font reports no glyphs");
        }

        [Fact]
        public void AnEmojiGlyphHasAColourBitmap()
        {
            TrueTypeFont font = RequireBitmapFont();
            (int gid, BitmapGlyph glyph) = FindBitmap(font);

            Assert.True(gid != 0, "no probe emoji resolved to a glyph");
            Assert.True(glyph.Png is { Length: > 8 }, "the glyph carries no image data");
            Assert.True(glyph.PixelWidth > 0 && glyph.PixelHeight > 0,
                $"degenerate bitmap size {glyph.PixelWidth}x{glyph.PixelHeight}");
            Assert.True(glyph.PpemY > 0, "the strike reports no pixels-per-em");

            // The payload really is a PNG; anything else and the decode below is meaningless.
            Assert.True(glyph.Png[0] == 0x89 && glyph.Png[1] == (byte)'P' && glyph.Png[2] == (byte)'N' && glyph.Png[3] == (byte)'G',
                "the image data is not a PNG");
        }

        /// <summary>
        /// End to end for the decoder: emoji PNGs are RGBA with the full set of row filters, which
        /// the original PngReader (8-bit RGB, filter 0 only, being the exact inverse of PngWriter)
        /// could not read at all.
        /// </summary>
        [Fact]
        public void TheGlyphBitmapDecodesToColouredPixels()
        {
            TrueTypeFont font = RequireBitmapFont();
            (_, BitmapGlyph glyph) = FindBitmap(font);

            byte[] rgba = PngReader.Decode(glyph.Png, out int w, out int h);

            Assert.True(w > 0 && h > 0, $"decoded to {w}x{h}");
            Assert.Equal(w * h * 4, rgba.Length);

            int opaque = 0, maxSat = 0;
            for (int i = 0; i < rgba.Length; i += 4)
            {
                if (rgba[i + 3] > 128) opaque++;
                int r = rgba[i], g = rgba[i + 1], b = rgba[i + 2];
                int max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
                if (rgba[i + 3] > 128) maxSat = Math.Max(maxSat, max - min);
            }

            Assert.True(opaque > 20, $"the decoded emoji is almost entirely transparent ({opaque} opaque pixels)");
            Assert.True(maxSat > 40, $"the decoded emoji has no colour in it (max saturation {maxSat})");
        }

        /// <summary>
        /// An emoji font is transparent outside the glyph. A decoder that ignored the alpha channel
        /// (or read RGB where the file says RGBA) would produce a fully opaque square, which draws
        /// as a coloured block over the text behind it.
        /// </summary>
        [Fact]
        public void TheGlyphBitmapHasTransparency()
        {
            TrueTypeFont font = RequireBitmapFont();
            (_, BitmapGlyph glyph) = FindBitmap(font);

            byte[] rgba = PngReader.Decode(glyph.Png, out _, out _);

            int transparent = 0;
            for (int i = 3; i < rgba.Length; i += 4) if (rgba[i] < 16) transparent++;

            Assert.True(transparent > 0,
                "no transparent pixels: the alpha channel was dropped, so the glyph is an opaque block");
        }

        /// <summary>
        /// End to end: a CBDT glyph reaches the screen in colour, through the protocol text path.
        ///
        /// Everything above tests the parse and the decode; this is the one that says the pixels
        /// arrive. It goes through GlyphRunPainter, which turns a bitmap glyph into an ordinary
        /// geometry + ImageBrush, so both text paths get it without the renderer knowing that colour
        /// bitmap glyphs exist at all.
        /// </summary>
        [Fact]
        public void ABitmapGlyphRendersInColour()
        {
            TrueTypeFont font = RequireBitmapFont();
            (int gid, _) = FindBitmap(font);

            const int W = 96, H = 96;
            const float emSize = 64f;
            const uint hRoot = 2, hBlack = 3, hRun = 20, hContent = 6;

            var engine = new MilcoreEngine { FontResolver = _ => font };
            engine.CreateOrAddRef(hRoot, MilResourceTypeId.Visual);
            engine.SubmitCommand(MilCmd.SolidColorBrush(hBlack, 0, 0, 0, 1));

            var indices = new ushort[] { (ushort)gid };
            var advances = new float[] { font.Advance(gid) * (emSize / font.PixelsPerEm) };

            engine.CreateOrAddRef(hRun, MilResourceTypeId.Null);
            engine.BeginCommand(MilCmd.GlyphRun(hRun, 0, 12f, 76f, emSize, indices, advances));
            engine.EndCommand();

            byte[] img = RenderContent(engine, MilCmd.DrawGlyphRunRecord(hBlack, hRun),
                                       W, H, hVisual: hRoot, hContent: hContent);

            int ink = 0, maxSat = 0;
            for (int i = 0; i < img.Length; i += 4)
            {
                int r = img[i], g = img[i + 1], b = img[i + 2];
                int max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
                if (max < 250) ink++;
                maxSat = Math.Max(maxSat, max - min);
            }

            Assert.True(ink > 50, $"the bitmap emoji rendered almost nothing ({ink} ink pixels)");
            Assert.True(maxSat > 40,
                $"max saturation was {maxSat}; the bitmap glyph did not reach the page in colour");
        }

        private static (int Gid, BitmapGlyph Glyph) FindBitmap(TrueTypeFont font)
        {
            foreach (int cp in Candidates)
            {
                // The probe code points above the BMP need the surrogate pair the cmap is keyed by;
                // GlyphIndex takes a char, so only the BMP ones can be looked up that way.
                if (cp > 0xFFFF) continue;
                int gid = font.GlyphIndex((char)cp);
                if (gid != 0 && font.TryGetGlyphBitmap(gid, out BitmapGlyph g)) return (gid, g);
            }

            // Otherwise take the first glyph in the font that has a bitmap at all: which code points
            // a given emoji font covers is its own business, but it must have SOME colour bitmap.
            for (int gid = 1; gid < font.GlyphCount; gid++)
                if (font.TryGetGlyphBitmap(gid, out BitmapGlyph g)) return (gid, g);

            Assert.Fail("the font has CBDT/CBLC tables but no glyph in it yielded a bitmap");
            return default;
        }
    }
}
