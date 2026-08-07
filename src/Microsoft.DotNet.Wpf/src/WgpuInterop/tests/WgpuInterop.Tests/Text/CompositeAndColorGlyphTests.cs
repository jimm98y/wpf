// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Ported from WgpuInterop.CompositeGlyphTest and ColorGlyphTest.
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using Microsoft.Wpf.Interop.WebGpu.Composition;
using Microsoft.Wpf.Interop.WebGpu.Composition.Text;
using WgpuInterop.Tests.Harness;
using Xunit;

namespace WgpuInterop.Tests.Text
{
    /// <summary>
    /// Accented letters are usually TrueType COMPOSITE glyphs: a base glyph plus a diacritic placed
    /// by a transform. These used to render blank (advance only), which is invisible in any test
    /// that only asks "did something render" of ASCII.
    /// </summary>
    public sealed class CompositeGlyphTests : RendererTestBase
    {
        public CompositeGlyphTests(GpuFixture gpu) : base(gpu) { }

        // Accented letter -> its base letter. Code points rather than literals keep this source pure
        // ASCII regardless of how the file is encoded or read.
        private static readonly (char Accented, char Base)[] Candidates =
        {
            ((char)0x00E9, 'e'), ((char)0x00E8, 'e'), ((char)0x00E0, 'a'), ((char)0x00F1, 'n'),
            ((char)0x00FC, 'u'), ((char)0x00F4, 'o'), ((char)0x00E7, 'c'),
        };

        /// <summary>
        /// Whether a letter is composite is a property of the FONT, not of Unicode: a face may draw
        /// e-acute as a single contour set. So the pair is discovered rather than hard-coded, and the
        /// test skips if this machine's font has none.
        /// </summary>
        private static (TrueTypeFont Font, char Accented, char Base) FindComposite()
        {
            TrueTypeFont font = TestFonts.Load();
            foreach ((char a, char b) in Candidates)
                if (font.IsCompositeGlyph(a) && font.TryGetGlyph(b, out _))
                    return (font, a, b);

            Assert.Skip($"no composite accented glyph in {Path.GetFileName(TestFonts.Path)}");
            return default;
        }

        private static int Ink(GlyphBitmap g)
        {
            int n = 0;
            foreach (byte b in g.Coverage) if (b > 128) n++;
            return n;
        }

        [Fact]
        public void CompositeGlyph_IsTallerAndInkierThanItsBase()
        {
            (TrueTypeFont font, char accented, char baseChar) = FindComposite();

            Assert.True(font.TryGetGlyph(accented, out GlyphBitmap acc) && acc.Width >= 2 && acc.Height >= 2,
                $"'{accented}' did not rasterize");
            Assert.True(font.TryGetGlyph(baseChar, out GlyphBitmap b), $"base '{baseChar}' did not rasterize");

            Assert.True(acc.Height > b.Height,
                $"the accented glyph should be taller than its base: {acc.Height} vs {b.Height}");
            Assert.True(acc.BearingY > b.BearingY,
                $"the accented glyph should rise higher above the baseline: {acc.BearingY} vs {b.BearingY}");
            Assert.True(Ink(acc) > Ink(b),
                $"the accent should add ink: {Ink(acc)} vs {Ink(b)}");
        }

        [Fact]
        public void CompositeGlyph_RendersInkEndToEnd()
        {
            (TrueTypeFont font, char accented, _) = FindComposite();

            using WgpuSceneRenderer renderer = NewRenderer(font);
            var root = new SceneVisual();
            var text = new SceneVisual();
            text.Content.Add(new GlyphRunDraw(accented.ToString(), new Vector2(8, 44), 40f,
                RgbaColor.FromBytes(0, 0, 0, 255)));
            root.Children.Add(text);

            byte[] px = renderer.RenderToRgba(root, 64, 64, White);
            int ink = 0;
            for (int i = 0; i < px.Length; i += 4) if (px[i] < 64) ink++;
            Assert.True(ink > 30, $"the composite glyph rendered only {ink} ink pixels");
        }
    }

    /// <summary>
    /// COLR/CPAL colour layers, parsed purely managed (no COM).
    ///
    /// Availability is genuinely uncertain and the skips reflect that in two separate steps. COLR is
    /// the Windows/Google flavour; Apple ships sbix and many Linux distros ship CBDT/CBLC bitmap
    /// emoji. So FINDING an emoji font does not mean finding a COLR one, and the layer check is a
    /// second, separately-reported gate rather than a failure -- an unavailable optional asset must
    /// not keep the suite permanently red off Windows.
    /// </summary>
    public sealed class ColorGlyphTests : RendererTestBase
    {
        public ColorGlyphTests(GpuFixture gpu) : base(gpu) { }

        private static readonly int[] Candidates =
        {
            0x26BD /* soccer */, 0x2764 /* heart */, 0x2600 /* sun */, 0x2728 /* sparkles */, 0x26A1 /* zap */,
        };

        private static (TrueTypeFont Font, int Gid, IReadOnlyList<ColorGlyphLayer> Layers) FindColorGlyph()
        {
            string? path = TestFonts.FindEmojiFont();
            Assert.SkipWhen(path is null, "no emoji font on this OS (set WGPU_TEST_EMOJI to run)");

            TrueTypeFont font;
            try { font = new TrueTypeFont(File.ReadAllBytes(path!)); }
            catch (Exception e)
            {
                // Two very different situations reach here and the message has to tell them apart,
                // because one is expected and the other is a real problem:
                //
                //   * a BITMAP emoji font (CBDT/CBLC, e.g. Noto Color Emoji on most Linux distros, or
                //     sbix on macOS) has no 'loca' and no outlines at all. It is simply not a COLR
                //     font, and nothing is wrong;
                //   * a .ttc handed to WGPU_TEST_EMOJI needs a face offset, so the sfnt parse finds
                //     no 'head'. That is a mis-set variable.
                //
                // Reporting only the parse error would make the first look like a bug.
                string why = HasBitmapGlyphTables(path!)
                    ? "it is a BITMAP emoji font (CBDT/CBLC), not a COLR/CPAL one, so there are no colour layers to read"
                    : $"it is not a usable single-face sfnt font ({e.Message}); a .ttc needs a face offset";
                Assert.Skip($"{Path.GetFileName(path)}: {why}");
                return default;
            }

            Assert.True(font is IColorGlyphFont, "TrueTypeFont does not implement IColorGlyphFont");
            var color = (IColorGlyphFont)font;

            foreach (int cp in Candidates)
            {
                int gid = font.GlyphIndex((char)cp);
                if (gid == 0) continue;
                if (!color.TryGetColorLayers(gid, out IReadOnlyList<ColorGlyphLayer> layers) || layers.Count == 0) continue;
                foreach (ColorGlyphLayer l in layers)
                    if (l.Color is not null) return (font, gid, layers);
            }

            Assert.Skip($"{Path.GetFileName(path)} has no COLR/CPAL layers for the probe code points " +
                        "(it is probably an sbix or CBDT/CBLC bitmap emoji font)");
            return default;
        }

        [Fact]
        public void ColrFont_DecomposesAGlyphIntoColouredLayers()
        {
            (TrueTypeFont font, _, IReadOnlyList<ColorGlyphLayer> layers) = FindColorGlyph();

            Assert.True(layers.Count >= 1, "the colour glyph reported no layers");

            bool anyColoured = false;
            foreach (ColorGlyphLayer l in layers) if (l.Color is not null) anyColoured = true;
            Assert.True(anyColoured, "no layer carries a palette colour");

            // Every layer must name a glyph the same font can resolve. Some layers are legitimately
            // blank, so this checks resolvability rather than requiring contours.
            foreach (ColorGlyphLayer l in layers)
                Assert.True(font.TryGetGlyphOutline(l.GlyphId, out _),
                    $"layer glyph id {l.GlyphId} does not resolve through the font's outline source");
        }

        /// <summary>
        /// End to end: a monochrome outline fallback would render black or grey on white, so a
        /// SATURATED pixel is the thing only real colour layers can produce.
        /// </summary>
        [Fact]
        public void ColrGlyph_RendersSaturatedColour()
        {
            (TrueTypeFont font, int gid, _) = FindColorGlyph();

            const int W = 64, H = 64;
            const float emSize = 48f;
            using WgpuSceneRenderer renderer = NewRenderer(font);

            var root = new SceneVisual();
            var text = new SceneVisual();
            text.Content.Add(new GlyphRunDraw(char.ConvertFromUtf32(CodePointFor(font, gid)),
                new Vector2(8, 52), emSize, RgbaColor.FromBytes(0, 0, 0, 255)));
            root.Children.Add(text);

            byte[] img = renderer.RenderToRgba(root, W, H, White);

            int maxSat = 0, ink = 0;
            for (int i = 0; i < img.Length; i += 4)
            {
                int r = img[i], g = img[i + 1], b = img[i + 2];
                int max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
                if (max < 250) ink++;
                maxSat = Math.Max(maxSat, max - min);
            }

            Assert.True(ink > 20, $"the colour glyph rendered almost nothing ({ink} ink pixels)");
            Assert.True(maxSat > 40,
                $"max saturation was {maxSat}; a monochrome fallback cannot be distinguished from colour layers below this");
        }


        /// <summary>Whether the sfnt carries CBDT/CBLC, i.e. is a bitmap colour-emoji font.</summary>
        private static bool HasBitmapGlyphTables(string path)
        {
            try
            {
                byte[] f = File.ReadAllBytes(path);
                int n = (f[4] << 8) | f[5];
                for (int i = 0; i < n; i++)
                {
                    string tag = System.Text.Encoding.ASCII.GetString(f, 12 + i * 16, 4);
                    if (tag == "CBDT" || tag == "sbix") return true;
                }
            }
            catch { }
            return false;
        }

        /// <summary>The first probe code point that maps to this glyph id.</summary>
        private static int CodePointFor(TrueTypeFont font, int gid)
        {
            foreach (int cp in Candidates)
                if (font.GlyphIndex((char)cp) == gid) return cp;
            return Candidates[0];
        }
    }
}
