// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Ported from WgpuInterop.FontTest and WgpuInterop.IconFallbackTest.
//
// The font tests deliberately assert STRUCTURAL properties rather than exact pixels: which font a
// machine has is not under the suite's control, so "the counter of an 'o' is hollow" holds for every
// real face while "pixel (12,20) is black" holds for exactly one.
//

using System;
using Microsoft.Wpf.Interop.WebGpu.Composition;
using Microsoft.Wpf.Interop.WebGpu.Composition.Text;
using System.Numerics;
using WgpuInterop.Tests.Harness;
using Xunit;

namespace WgpuInterop.Tests.Text
{
    public sealed class TrueTypeFontTests : RendererTestBase
    {
        public TrueTypeFontTests(GpuFixture gpu) : base(gpu) { }

        [Fact]
        public void RealOutlines_HaveHollowCounters_AndAntiAliasedEdges()
        {
            TrueTypeFont font = TestFonts.Load();
            Assert.True(font.GlyphCount > 0, "font reports no glyphs");
            Assert.Equal(48, font.PixelsPerEm);

            Assert.True(font.TryGetGlyph('o', out GlyphBitmap o) && o.Width >= 4 && o.Height >= 4,
                "'o' did not rasterize to a usable bitmap");

            // A hollow centre with an inked ring is only produced by real contours plus a non-zero
            // fill of a reverse-wound inner loop -- it is the cheapest proof the whole outline path
            // works rather than a bounding box being filled.
            byte centre = o.Coverage[(o.Height / 2) * o.Width + (o.Width / 2)];
            byte ring = o.Coverage[(o.Height / 2) * o.Width + Math.Max(1, o.Width / 8)];
            Assert.True(centre < 40, $"'o' counter should be hollow, centre coverage was {centre}");
            Assert.True(ring > 200, $"'o' ring should be inked, coverage was {ring}");

            bool hasAa = false;
            foreach (byte b in o.Coverage) if (b > 10 && b < 245) { hasAa = true; break; }
            Assert.True(hasAa, "'o' has no partial coverage at all, so its edges are not anti-aliased");
        }

        [Fact]
        public void RealMetrics_AreProportional()
        {
            TrueTypeFont font = TestFonts.Load();
            Assert.True(font.TryGetGlyph('W', out GlyphBitmap w), "'W' did not rasterize");
            Assert.True(font.TryGetGlyph('l', out GlyphBitmap l), "'l' did not rasterize");
            Assert.True(w.Advance > l.Advance,
                $"advances must be proportional, not monospace: W={w.Advance}, l={l.Advance}");
        }

        [Fact]
        public void RealFont_RendersInkEndToEnd()
        {
            const int W = 160, H = 64;
            TrueTypeFont font = TestFonts.Load();
            using WgpuSceneRenderer renderer = NewRenderer(font);

            var root = new SceneVisual();
            var text = new SceneVisual();
            text.Content.Add(new GlyphRunDraw("Wol", new Vector2(6, 44), 40f, RgbaColor.FromBytes(0, 0, 0, 255)));
            root.Children.Add(text);
            byte[] px = renderer.RenderToRgba(root, W, H, RgbaColor.FromBytes(255, 255, 255, 255));

            int ink = 0, background = 0;
            for (int i = 0; i < px.Length; i += 4)
            {
                if (px[i] < 64) ink++;
                else if (px[i] > 250) background++;
            }
            Assert.True(ink > 50, $"real font produced almost no ink ({ink} pixels)");
            Assert.True(background > ink, $"text should be glyphs, not a solid block (ink={ink}, background={background})");
        }
    }

    /// <summary>
    /// Ported from WgpuInterop.IconFallbackTest. Needs no GPU, so it is not in the GPU collection and
    /// runs even on a machine with no adapter.
    /// </summary>
    public sealed class FluentIconFallbackTests
    {
        // Must mirror OpenTypeFontData.TryRemapFluentIcon. Duplicated deliberately: that method is
        // private to another assembly, and a copy that drifts is exactly what this test would flag.
        public static TheoryData<uint, uint, string> Table => new()
        {
            { 0xE9AE, 0xE738, "CheckBox indeterminate dash -> Remove" },
            { 0xEB3C, 0xE790, "Colors -> Color (palette)" },
            { 0xED58, 0xE76E, "Icons -> Emoji2" },
            { 0xEF58, 0xE9D9, "User Dashboard -> Analytics" },
            { 0xF246, 0xF0E2, "Layout -> GridView" },
        };

        /// <summary>
        /// Off-Windows there is no "Segoe Fluent Icons", so the fork substitutes the vendored
        /// MDL2-based Symbols.ttf and remaps Fluent-only codepoints onto MDL2 equivalents. The table
        /// is a handful of reviewed constants; the FRAGILE half is the font. If a font drop loses a
        /// substitute glyph the remap silently points at .notdef and tofu reappears somewhere else,
        /// and nothing else in the build would notice.
        /// </summary>
        [Theory]
        [MemberData(nameof(Table))]
        public void FluentRemap_TargetsAGlyphTheVendoredFontHas(uint fluent, uint mdl2, string what)
        {
            TrueTypeFont font = TestFonts.RequireRepoFont("Symbols.ttf");

            Assert.True(font.GlyphIndex((char)fluent) == 0,
                $"{what}: U+{fluent:X4} EXISTS in the vendored font, so remapping it hides the real glyph");
            Assert.True(font.GlyphIndex((char)mdl2) != 0,
                $"{what}: substitute U+{mdl2:X4} is MISSING from the vendored font, so the remap yields .notdef");
        }
    }
}
