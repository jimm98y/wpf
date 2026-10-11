// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// East Asian faces against DirectWrite's own alpha texture, glyph by glyph, in the mode it recommends
// (what WPF's Ideal text draws with) and in GDI_CLASSIC (Display text). Two things kept them apart:
//
//  * EMBEDDED STRIKES. DirectWrite draws a "legacy East Asian" face's 1-bit EBDT strike in every
//    mode but NATURAL_SYMMETRIC, unfiltered, every channel full (NaturalClearType.DWriteStrike).
//    WPF's path handed MS Gothic's strike to the outline painter at the font's own scale, so every
//    glyph came out a few pixels across.
//  * THE GASP. DirectWrite's natural modes fit where a version 1 'gasp' sets SYMMETRIC_GRIDFIT, not
//    GRIDFIT, and GDI_CLASSIC fits always (ResolveGridFitMode): Microsoft YaHei above 21ppem and
//    MS Gothic above 20 were fitted in Ideal and left unfitted in Display -- both the wrong way round.
//  * THE UNFITTED GLYPH is drawn at the whole ppem (24.5 as 25), through the scaler's own
//    arithmetic, and a simulated bold emboldens it in the unscaled frame.
//
// The ceilings are RATCHETS.
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Wpf.Interop.WebGpu.Composition.Text;
using Xunit;

namespace WgpuInterop.Tests.Text
{
    public class CjkNaturalParityTests
    {
        private const string Text = "\u65E5\u672C\u8A9E\u306E\u30C6\u30AD\u30B9\u30C8\u3002\u4E2D\u6587\u672C\uFF0C\u6D4B\u8BD5\u4E00\u4E8C\u4E09\uD55C\uAD6D\uC5B4\uD14D\uC2A4\uD2B8abc123";

        private static readonly float[] Sizes = { 11f, 14f, 18f, 22f, 24.5f, 28f, 36f, 37.33f };

        public static IEnumerable<object[]> Faces() => new[]
        {
            new object[] { "msgothic.ttc", false, 0, 0 }, new object[] { "msyh.ttc", false, 0, 0 },
            new object[] { "simsun.ttc", false, 0, 0 }, new object[] { "YuGothR.ttc", false, 0, 0 },
            new object[] { "malgun.ttf", false, 0, 0 },
            // Simulated bold: a strike, or a GDI_CLASSIC size's bi-level fit, is expanded to the
            // oversample and emboldened there (NaturalClearType.Embolden), then filtered.
            new object[] { "msgothic.ttc", true, 0, 0 }, new object[] { "simsun.ttc", true, 0, 0 },
            // One sample of YaHei's U+6D4B at 25ppem (and so 24.5) is the one left.
            new object[] { "msyh.ttc", true, 1, 0 },
        };

        private static (TrueTypeFont Font, IntPtr Face)? Load(string file, bool bold = false)
        {
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), file);
            if (!OperatingSystem.IsWindows() || !File.Exists(path)) return null;
            byte[] data = File.ReadAllBytes(path);
            int offset = data[0] == 't' && data[1] == 't' && data[2] == 'c' && data[3] == 'f'
                ? data[12] << 24 | data[13] << 16 | data[14] << 8 | data[15] : 0;
            return (new TrueTypeFont(data, synthesizeBold: bold, sfntOffset: offset), DWriteOracle.FontFace(path, 0, bold ? 1 : 0));
        }

        private static long Diff(byte[] a, int al, int at, int aw, int ah, byte[] b, int bl, int bt, int bw, int bh)
        {
            int l = Math.Min(al, bl), t = Math.Min(at, bt), r = Math.Max(al + aw, bl + bw), btm = Math.Max(at + ah, bt + bh);
            long d = 0;
            for (int y = t; y < btm; y++)
                for (int x = l; x < r; x++)
                    for (int k = 0; k < 3; k++)
                    {
                        int va = x >= al && x < al + aw && y >= at && y < at + ah ? a[((y - at) * aw + x - al) * 3 + k] : 0;
                        int vb = x >= bl && x < bl + bw && y >= bt && y < bt + bh ? b[((y - bt) * bw + x - bl) * 3 + k] : 0;
                        d += Math.Abs(va - vb);
                    }
            return d;
        }

        [Theory]
        [MemberData(nameof(Faces))]
        public void EachGlyphIsDirectWritesTexture(string file, bool bold, int naturalCeiling, int classicCeiling)
        {
            var loaded = Load(file, bold);
            Assert.SkipWhen(loaded is null, "needs the Windows face and DirectWrite");
            (TrueTypeFont font, IntPtr face) = loaded!.Value;

            var natural = new List<string>();
            var classic = new List<string>();
            foreach (float em in Sizes)
            {
                int mode = DWriteOracle.RecommendedMode(face, em);
                int nSub = mode == 5 ? 5 : 1;
                foreach (char c in Text.Distinct())
                {
                    int gid = font.GlyphIndex(c);
                    if (gid <= 0) continue;

                    byte[] theirs = DWriteOracle.AlphaTexture(face, em, new[] { (ushort)gid }, new[] { 0f }, null, mode,
                        out int tl, out int tt, out int tr, out int tb);
                    NaturalClearType.GlyphBits bits = (nSub == 1 ? NaturalClearType.DWriteStrike(font, gid, em) : null)
                                                      ?? NaturalClearType.Rasterize(font, gid, em, nSub);
                    byte[] ours = NaturalClearType.RunTexture(new[] { bits }, new[] { 0f }, new[] { 0f },
                        out int ol, out int ot, out int ow, out int oh, nSub);
                    if (Diff(theirs, tl, tt, tr - tl, tb - tt, ours, ol, ot, ow, oh) != 0) natural.Add($"{em}:U+{(int)c:X4}");

                    theirs = DWriteOracle.AlphaTexture(face, em, new[] { (ushort)gid }, new[] { 0f }, null, 2,
                        out tl, out tt, out tr, out tb);
                    bits = NaturalClearType.DWriteStrike(font, gid, em, gdiClassic: true)
                           ?? NaturalClearType.RasterizeGdiClassic(font, gid, em);
                    ours = NaturalClearType.RunTexture(new[] { bits }, new[] { 0f }, new[] { 0f },
                        out ol, out ot, out ow, out oh, 1);
                    if (Diff(theirs, tl, tt, tr - tl, tb - tt, ours, ol, ot, ow, oh) != 0) classic.Add($"{em}:U+{(int)c:X4}");
                }
            }
            Console.WriteLine($"CJK {file} bold={bold}: natural {natural.Count} [{string.Join(" ", natural)}], classic {classic.Count} [{string.Join(" ", classic)}]");
            Assert.True(natural.Count <= naturalCeiling, $"natural: {natural.Count} differ (ceiling {naturalCeiling}): {string.Join(" ", natural)}");
            Assert.True(classic.Count <= classicCeiling, $"GDI_CLASSIC: {classic.Count} differ (ceiling {classicCeiling}): {string.Join(" ", classic)}");
        }

        /// <summary>A strike glyph is placed on a whole pixel and merged over a filtered outline
        /// glyph unfiltered: MS Gothic's 'A' (a strike at 12ppem) beside U+01CD, which has none.</summary>
        [Fact]
        public void StrikeAndOutlineGlyphsShareARun()
        {
            var loaded = Load("msgothic.ttc");
            Assert.SkipWhen(loaded is null, "needs the Windows face and DirectWrite");
            (TrueTypeFont font, IntPtr face) = loaded!.Value;
            int a = font.GlyphIndex('A'), caron = font.GlyphIndex('\u01CD');
            Assert.NotNull(NaturalClearType.DWriteStrike(font, a, 12f));
            Assert.Null(NaturalClearType.DWriteStrike(font, caron, 12f));

            foreach (float origin in new[] { 0f, 0.33f, 0.5f })
            {
                float[] xs = { origin, origin + 6.4f };
                var glyphs = new[] { (ushort)a, (ushort)caron };
                byte[] theirs = DWriteOracle.AlphaTexture(face, 12f, glyphs, new[] { 6.4f, 0f }, new[] { origin, 0f, origin, 0f }, 4,
                    out int tl, out int tt, out int tr, out int tb);
                var bits = glyphs.Select(g => NaturalClearType.DWriteStrike(font, g, 12f) ?? NaturalClearType.Rasterize(font, g, 12f, 1)).ToArray();
                byte[] ours = NaturalClearType.RunTexture(bits, xs, new[] { 0f, 0f }, out int ol, out int ot, out int ow, out int oh, 1);
                Assert.Equal(0, Diff(theirs, tl, tt, tr - tl, tb - tt, ours, ol, ot, ow, oh));
            }
        }

        [Theory]
        [InlineData("msgothic.ttc", true)]
        [InlineData("simsun.ttc", true)]
        [InlineData("msyh.ttc", false)]
        [InlineData("calibri.ttf", false)]
        [InlineData("cour.ttf", false)]
        public void LegacyEastAsianIsDirectWritesTest(string file, bool expected)
        {
            var loaded = Load(file);
            Assert.SkipWhen(loaded is null, "needs the Windows face");
            Assert.Equal(expected, loaded!.Value.Font.DWriteLegacyEastAsian);
        }
    }
}
