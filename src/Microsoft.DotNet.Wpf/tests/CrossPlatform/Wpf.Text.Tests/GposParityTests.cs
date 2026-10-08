// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Where stock WPF (DirectWrite) puts marks and which forms it picks, glyph for glyph -- the cases
// the managed shaper got wrong:
//
//  * an offset is TRUNCATED into ideal units, (int)(advanceOffset * 300) of DWrite's float offset
//    (the forwarder's TextAnalyzer.cpp); rounding put Arial's sheva one unit off;
//  * right to left, a mark is placed from its base's physical XPlacement, while the base itself
//    reports XAdvance - XPlacement (Calibri's reh before hah kerns -300/-300);
//  * mark-to-mark stacks only on one ligature component (Arial's Allah ligature: the damma on the
//    heh is not stacked on the shadda+fatha of the lam);
//  * a mark decomposed out of a character that joined a ligature keeps that character, so it finds
//    its component (Microsoft Uighur's lam-alef with hamza);
//  * HEH GOAL is dual-joining (Segoe UI's Urdu medial heh goal and final yeh barree).
//
// Offsets are the glyph run's, which WPF stores in thousandths of an em. Every expected value is
// stock WPF's (Microsoft.WindowsDesktop.App 10, Windows 11, Ideal mode).
//

using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.TextFormatting;
using Xunit;

namespace Wpf.Text.Tests
{
    public class GposParityTests
    {
        private static (string Glyphs, string Offsets) Shape(string family, double size, string text,
                                                            TextFormattingMode mode = TextFormattingMode.Ideal)
        {
            var properties = new RunProperties(family, size) { PixelsPerDip = 1 };
            var source = new StringTextSource(text, properties) { PixelsPerDip = 1 };
            using TextLine line = TextFormatter.Create(mode).FormatLine(
                source, 0, 10000, new ParagraphProperties(properties), null);

            var visual = new DrawingVisual();
            using (DrawingContext context = visual.RenderOpen())
            {
                line.Draw(context, new Point(0, 0), InvertAxes.None);
            }

            var glyphs = new List<string>();
            var offsets = new List<string>();
            void Walk(Drawing? drawing)
            {
                if (drawing is DrawingGroup group)
                {
                    foreach (Drawing child in group.Children) Walk(child);
                }
                else if (drawing is GlyphRunDrawing glyphRun)
                {
                    GlyphRun run = glyphRun.GlyphRun;
                    glyphs.AddRange(run.GlyphIndices.Select(g => g.ToString(CultureInfo.InvariantCulture)));
                    for (int i = 0; i < run.GlyphIndices.Count; i++)
                    {
                        Point o = run.GlyphOffsets?[i] ?? default;
                        offsets.Add(o.X.ToString("0.###", CultureInfo.InvariantCulture) + ","
                                    + o.Y.ToString("0.###", CultureInfo.InvariantCulture));
                    }
                }
            }
            Walk(visual.Drawing);
            return (string.Join(" ", glyphs), string.Join(" ", offsets));
        }

        [Theory]
        [InlineData("Arial", 12, "בְּ", "718 653", "0,0 -2.688,0")]
        [InlineData("Arial", 16, "اللَّهُ", "4101 841 757", "0,0 -4.016,-0.816 0.16,-2.496")]
        [InlineData("Calibri", 12, "لرَّحم", "5218 4933 4697 4832 5251", "0,0 0,0 -1.608,4.092 0,0 0,0")]
        [InlineData("Microsoft Uighur", 14, "لأ", "572 111", "0,0 -0.98,7.154")]
        [InlineData("Segoe UI", 14, "ٹہے", "2396 2402 4022", "0,0 0,0 0,0")]
        public void MarksAndFormsAreDirectWrites(string family, double size, string text, string glyphs, string offsets)
        {
            var typeface = new Typeface(new FontFamily(family), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            Assert.SkipUnless(typeface.TryGetGlyphTypeface(out GlyphTypeface? gt)
                              && gt!.Win32FamilyNames.Values.Contains(family), $"{family} is not installed");

            (string actualGlyphs, string actualOffsets) = Shape(family, size, text);
            Assert.Equal(glyphs, actualGlyphs);
            Assert.Equal(offsets, actualOffsets);
        }

        // Display mode places marks in DEVICE PIXELS, every anchor rounded on its own
        // (GetGdiCompatibleGlyphPlacements): the kasra under initial beh at 16 ppem is
        // round(-155/128) - round(95/128) = -2, the sukun on seen 0,-1 where the exact
        // difference rounds to -1,0.
        [Fact]
        public void DisplayModeMarksSitOnWholePixels()
        {
            var typeface = new Typeface(new FontFamily("Arial"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            Assert.SkipUnless(typeface.TryGetGlyphTypeface(out GlyphTypeface? gt)
                              && gt!.Win32FamilyNames.Values.Contains("Arial"), "Arial is not installed");

            (_, string offsets) = Shape("Arial", 16, "بِسْمِ", TextFormattingMode.Display);
            Assert.Equal("0,0 0,-2 0,0 0,-1 0,0 -2,-2", offsets);
        }
    }
}
