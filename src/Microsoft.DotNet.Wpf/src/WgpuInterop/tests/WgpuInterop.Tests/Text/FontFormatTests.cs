// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Ported from WgpuInterop.CffTest and WgpuInterop.TtcTest.
//
// These need font FORMATS rather than just a font: a CFF/PostScript face and a multi-face collection.
// Neither is guaranteed on any given machine, so both skip with a reason rather than failing -- and
// neither can be substituted, because a .ttf does not exercise CffFont's Type 2 charstring
// interpreter at all and a single-face file does not exercise per-face sfnt offsets.
//
// No GPU: these run the rasterizer on the CPU and read the coverage bitmap directly, so they are
// outside the GPU collection and run on an adapter-less box.
//

using System;
using System.IO;
using System.Text;
using Microsoft.Wpf.Interop.WebGpu.Composition.Text;
using WgpuInterop.Tests.Harness;
using Xunit;

namespace WgpuInterop.Tests.Text
{
    public sealed class CffFontTests
    {
        private static byte[] RequireCff()
        {
            string? p = TestFonts.FindCff();
            Assert.SkipWhen(p is null,
                "no CFF/OpenType (.otf) font on this machine; set WGPU_TEST_CFF to one");
            return File.ReadAllBytes(p!);
        }

        private static int Ink(GlyphBitmap g)
        {
            int sum = 0;
            foreach (byte b in g.Coverage) sum += b;
            return sum;
        }

        [Fact]
        public void CffFace_IsDetectedAndParsed()
        {
            byte[] bytes = RequireCff();
            Assert.True(CffFont.IsCff(bytes), "the file was not detected as CFF/OpenType");

            var font = new CffFont(bytes);
            Assert.True(font.GlyphCount > 0, "font reports no glyphs");
            Assert.Equal(48, font.PixelsPerEm);
            Assert.True(font.GlyphIndex('A') != 0, "cmap does not map 'A'");
        }

        /// <summary>
        /// The same structural checks the TrueType path gets, against Type 2 charstrings: a hollow
        /// counter proves real contours plus a correct non-zero fill of the reverse-wound inner loop.
        /// </summary>
        [Fact]
        public void CffOutlines_HaveHollowCountersAndAntiAliasing()
        {
            var font = new CffFont(RequireCff());
            int oGid = font.GlyphIndex('o');
            Assert.True(oGid != 0, "the CFF face does not map 'o'");
            Assert.True(font.TryGetGlyph(oGid, out GlyphBitmap o) && o.Width >= 4 && o.Height >= 4,
                "'o' did not rasterize from the CFF outline");

            byte centre = o.Coverage[(o.Height / 2) * o.Width + (o.Width / 2)];
            byte ring = o.Coverage[(o.Height / 2) * o.Width + Math.Max(1, o.Width / 8)];
            Assert.True(centre < 60, $"'o' counter should be hollow, centre coverage was {centre}");
            Assert.True(ring > 180, $"'o' ring should be inked, coverage was {ring}");

            bool hasAa = false;
            foreach (byte b in o.Coverage) if (b > 10 && b < 245) { hasAa = true; break; }
            Assert.True(hasAa, "'o' has no partial coverage, so its edges are not anti-aliased");
        }

        [Fact]
        public void CffMetrics_AreProportional()
        {
            var font = new CffFont(RequireCff());
            float w = font.Advance(font.GlyphIndex('W'));
            float l = font.Advance(font.GlyphIndex('l'));
            Assert.True(w > l, $"advances must be proportional: W={w:0.0}, l={l:0.0}");
        }

        /// <summary>
        /// Synthetic bold: the SAME outline emboldened must lay down strictly more ink while leaving
        /// the counter mostly hollow. Both halves matter -- an emboldening that floods the counter is
        /// how "bold" degenerates into a blob at small sizes.
        /// </summary>
        [Fact]
        public void SyntheticBold_AddsInkWithoutFloodingTheCounter()
        {
            byte[] bytes = RequireCff();
            var regular = new CffFont(bytes);
            var bold = new CffFont(bytes, synthesizeBold: true);

            Assert.True(regular.TryGetGlyph(regular.GlyphIndex('o'), out GlyphBitmap o), "'o' did not rasterize");
            Assert.True(bold.TryGetGlyph(bold.GlyphIndex('o'), out GlyphBitmap ob), "bold 'o' did not rasterize");

            Assert.True(Ink(ob) > Ink(o) * 1.05f,
                $"synthetic bold should add ink: bold={Ink(ob)}, regular={Ink(o)}");
            byte boldCentre = ob.Coverage[(ob.Height / 2) * ob.Width + (ob.Width / 2)];
            Assert.True(boldCentre < 110, $"bold 'o' counter should stay mostly hollow, centre was {boldCentre}");
        }

        /// <summary>Synthetic oblique: shearing a tall narrow glyph must widen its bounding box.</summary>
        [Fact]
        public void SyntheticOblique_ShearsTheGlyphWider()
        {
            byte[] bytes = RequireCff();
            var regular = new CffFont(bytes);
            var oblique = new CffFont(bytes, synthesizeOblique: true);

            Assert.True(regular.TryGetGlyph(regular.GlyphIndex('I'), out GlyphBitmap i), "'I' did not rasterize");
            Assert.True(oblique.TryGetGlyph(oblique.GlyphIndex('I'), out GlyphBitmap io), "oblique 'I' did not rasterize");

            Assert.True(io.Width > i.Width,
                $"oblique should shear 'I' wider: oblique={io.Width}px, regular={i.Width}px");
        }
    }

    /// <summary>
    /// A collection (.ttc) packs several faces into one file and DirectWrite selects one by index.
    /// The header is parsed here and faces are loaded by their sfnt offsets.
    /// </summary>
    public sealed class FontCollectionTests
    {
        private static byte[] RequireTtc()
        {
            string? p = TestFonts.FindTtc();
            Assert.SkipWhen(p is null, "no .ttc collection on this machine; set WGPU_TEST_TTC to one");
            byte[] bytes = File.ReadAllBytes(p!);
            Assert.True(bytes.Length >= 16 && bytes[0] == 't' && bytes[1] == 't' && bytes[2] == 'c' && bytes[3] == 'f',
                $"{p} is not a 'ttcf' collection");
            return bytes;
        }

        private static uint BE32(byte[] b, int o)
            => ((uint)b[o] << 24) | ((uint)b[o + 1] << 16) | ((uint)b[o + 2] << 8) | b[o + 3];

        private static IGlyphOutlineFont LoadFace(byte[] bytes, int sfntOffset)
            => CffFont.IsCff(bytes, sfntOffset)
                ? new CffFont(bytes, sfntOffset: sfntOffset)
                : new TrueTypeFont(bytes, sfntOffset: sfntOffset);

        [Fact]
        public void EveryFace_RendersRealOutlines()
        {
            byte[] bytes = RequireTtc();
            uint numFonts = BE32(bytes, 8);
            Assert.True(numFonts >= 1, "the collection reports no faces");

            for (int i = 0; i < Math.Min(numFonts, 4); i++)
            {
                int sfntOffset = (int)BE32(bytes, 12 + i * 4);
                IGlyphOutlineFont font = LoadFace(bytes, sfntOffset);
                var src = (IGlyphSource)font;
                var shaping = (IShapingFont)font;

                int gid = shaping.GlyphIndex('o');
                Assert.True(gid != 0, $"face {i}: cmap does not map 'o'");
                Assert.True(src.TryGetGlyph(gid, out GlyphBitmap o) && o.Width >= 4 && o.Height >= 4,
                    $"face {i}: 'o' did not rasterize");

                byte centre = o.Coverage[(o.Height / 2) * o.Width + (o.Width / 2)];
                Assert.True(centre < 80, $"face {i}: 'o' counter should be hollow, centre was {centre}");

                bool aa = false;
                foreach (byte b in o.Coverage) if (b > 10 && b < 245) { aa = true; break; }
                Assert.True(aa, $"face {i}: 'o' is not anti-aliased");
            }
        }

        /// <summary>
        /// Per-face indexing works iff face 1 parses a DIFFERENT sfnt table directory than face 0.
        ///
        /// Comparing directories rather than glyph output is deliberate and collection-independent:
        /// faces that share outlines (Cambria / Cambria Math, say) would render identically while
        /// still being distinct faces, so an ink comparison would report a false failure on some
        /// machines and a false pass on others.
        /// </summary>
        [Fact]
        public void FacesAreIndexedByTheirOwnSfntOffset()
        {
            byte[] bytes = RequireTtc();
            uint numFonts = BE32(bytes, 8);
            Assert.SkipWhen(numFonts < 2, $"the available collection has only {numFonts} face(s)");

            int off0 = (int)BE32(bytes, 12), off1 = (int)BE32(bytes, 16);
            Assert.True(off0 != off1, "the two faces report the same sfnt offset");
            Assert.True(DirSignature(bytes, off0) != DirSignature(bytes, off1),
                "face 1 reads the same table directory as face 0, so per-face offsets are not applied");
        }

        /// <summary>tag:offset pairs of the sfnt table directory at a given base.</summary>
        private static string DirSignature(byte[] b, int sfntBase)
        {
            int numTables = (b[sfntBase + 4] << 8) | b[sfntBase + 5];
            var sb = new StringBuilder();
            int p = sfntBase + 12;
            for (int i = 0; i < numTables; i++, p += 16)
                sb.Append(Encoding.ASCII.GetString(b, p, 4)).Append(':').Append(BE32(b, p + 8)).Append(';');
            return sb.ToString();
        }
    }
}
