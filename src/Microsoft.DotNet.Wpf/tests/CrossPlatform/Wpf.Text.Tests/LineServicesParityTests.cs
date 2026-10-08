// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// How stock WPF cuts a line into glyph runs, and where it puts the caret -- the parts of the text
// stack that are neither shaping nor rasterization, and that the specimen's pixels depend on all
// the same: a glyph run's origin is placed at its own ideal position, so two runs where stock draws
// one move every glyph of the second by its advances' rounding.
//
//  * The FAST PATH. PresentationNative's classification marks Latin letters, digits, the space and
//    ASCII punctuation CharacterFastText, so a run of them in a face with no required Latin
//    typography (Tahoma, Consolas: no GPOS kerning) skips shaping and draws its nominal glyphs, with
//    no offsets, as one run. The managed classification never set the flag, and every such run was
//    shaped and split at its leading space.
//  * DNODES. LineServices formats a text run in pieces (LSTXTFMT.C LsFmtText): a run that starts with
//    spaces has them as a piece of their own; a hyphen, a dash, a no-break or typographic space
//    stands alone; a tab is a piece as wide as the stop it reaches, drawn as a space. Each piece is
//    drawn as its own glyph run, though neighbouring pieces are SHAPED together -- the space in
//    " AVA" is kerned against the A.
//  * QUERIES. LineServices answers caret and hit-test queries in the paragraph's own direction and
//    describes a cell by the sublines around it, so a right-to-left character's leading edge is its
//    right edge, and a run reading against the paragraph sits one ideal unit inside its edge.
//
// Every expected value is stock WPF's (Microsoft.WindowsDesktop.App 10, Windows 11, Ideal mode),
// read through the same public APIs. Distances are in ideal units, 1/300 DIP.
//

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.TextFormatting;
using Xunit;

namespace Wpf.Text.Tests
{
    public class LineServicesParityTests
    {
        private sealed class Paragraph : TextParagraphProperties
        {
            private readonly TextRunProperties _defaults;
            private readonly bool _rtl;
            public Paragraph(TextRunProperties defaults, bool rtl) { _defaults = defaults; _rtl = rtl; }
            public override FlowDirection FlowDirection => _rtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
            public override TextAlignment TextAlignment => TextAlignment.Left;
            public override double LineHeight => 0;
            public override bool FirstLineInParagraph => true;
            public override TextRunProperties DefaultTextRunProperties => _defaults;
            public override TextWrapping TextWrapping => TextWrapping.NoWrap;
            public override TextMarkerProperties? TextMarkerProperties => null;
            public override double Indent => 0;
        }

        private sealed record Run(double X, ushort[] Glyphs, bool Shaped);

        private static (TextLine Line, List<Run> Runs) Format(string family, double size, string text, bool rtl = false)
        {
            var properties = new RunProperties(family, size);
            TextLine line = TextFormatter.Create().FormatLine(
                new StringTextSource(text, properties), 0, 0, new Paragraph(properties, rtl), null);

            var visual = new DrawingVisual();
            using (DrawingContext context = visual.RenderOpen())
            {
                line.Draw(context, new Point(0, 0), InvertAxes.None);
            }

            var runs = new List<Run>();
            void Walk(Drawing? drawing)
            {
                if (drawing is DrawingGroup group)
                {
                    foreach (Drawing child in group.Children) Walk(child);
                }
                else if (drawing is GlyphRunDrawing glyphs)
                {
                    GlyphRun run = glyphs.GlyphRun;
                    runs.Add(new Run(run.BaselineOrigin.X, run.GlyphIndices.ToArray(), run.GlyphOffsets != null));
                }
            }
            Walk(visual.Drawing);
            return (line, runs);
        }

        private static void SkipUnlessInstalled(string family)
        {
            var typeface = new Typeface(new FontFamily(family), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            Assert.SkipUnless(typeface.TryGetGlyphTypeface(out GlyphTypeface? gt)
                              && gt!.Win32FamilyNames.Values.Contains(family), $"{family} is not installed");
        }

        private static int[] Carets(TextLine line, int length)
            => Enumerable.Range(0, length + 1)
                         .Select(i => (int)Math.Round(line.GetDistanceFromCharacterHit(new CharacterHit(i, 0)) * 300))
                         .ToArray();

        [Theory]
        [InlineData("Tahoma")]
        [InlineData("Consolas")]
        public void PlainLatinTakesTheFastPath(string family)
        {
            SkipUnlessInstalled(family);
            (TextLine line, List<Run> runs) = Format(family, 16, " abc");
            using (line)
            {
                // One run of nominal glyphs, as stock draws it -- not a space and a word, shaped.
                Run run = Assert.Single(runs);
                Assert.False(run.Shaped);
                Assert.Equal(4, run.Glyphs.Length);
            }
        }

        [Fact]
        public void LeadingSpacesAreTheirOwnGlyphRunButShapeWithTheWord()
        {
            SkipUnlessInstalled("Arial");
            (TextLine line, List<Run> runs) = Format("Arial", 100, " AVA");
            using (line)
            {
                Assert.Equal(new ushort[] { 3 }, runs[0].Glyphs);
                Assert.Equal(new ushort[] { 36, 57, 36 }, runs[1].Glyphs);
                // " A" is a kerning pair: the space is 22.27 wide, not its nominal 27.78.
                Assert.Equal(new[] { 0, 6680, 24463, 42246, 62256 }, Carets(line, 4));
                Assert.Equal(207.52, line.WidthIncludingTrailingWhitespace, 2);
            }
        }

        [Theory]
        // text, stock's carets, stock's glyph runs
        [InlineData("a\tb", new[] { 0, 2670, 19200, 21870 }, "68|3|69")]
        [InlineData("T\t\tT", new[] { 0, 2932, 19200, 38400, 41332 }, "55|3|3|55")]
        [InlineData("a-b", new[] { 0, 2670, 4268, 6938 }, "68|16|69")]
        [InlineData("a\u00A0b", new[] { 0, 2670, 4004, 6674 }, "68|3|69")]
        [InlineData("a\u2003b", new[] { 0, 2670, 7470, 10140 }, "68|3|69")]
        [InlineData("a\u00ADb", new[] { 0, 2670, 2670, 5340 }, "68|69")]
        public void SpecialCharactersAreDnodesOfTheirOwn(string text, int[] carets, string glyphRuns)
        {
            SkipUnlessInstalled("Arial");
            (TextLine line, List<Run> runs) = Format("Arial", 16, text);
            using (line)
            {
                Assert.Equal(carets, Carets(line, text.Length));
                Assert.Equal(glyphRuns, string.Join("|", runs.Select(r => string.Join(" ", r.Glyphs))));
            }
        }

        [Theory]
        [InlineData(false, new[] { 0, 2520, 5173, 7388, 20383, 16469, 13488, 12173, 20384, 21884, 24537, 27064, 28592 })]
        [InlineData(true, new[] { 7387, 4867, 2214, 7388, 8888, 12802, 15783, 17098, 20384, 28591, 25938, 23411, 28592 })]
        public void CaretsRunInTheParagraphsDirection(bool rtl, int[] carets)
        {
            SkipUnlessInstalled("Tahoma");
            const string text = "abc \u05E9\u05DC\u05D5\u05DD def";
            (TextLine line, _) = Format("Tahoma", 16, text, rtl);
            using (line)
            {
                Assert.Equal(carets, Carets(line, text.Length));
            }
        }

        [Fact]
        public void TextBoundsOfAReversedRunAreItsOwnDirection()
        {
            SkipUnlessInstalled("Tahoma");
            const string text = "\u05E9\u05DC\u05D5\u05DD abc";
            (TextLine line, _) = Format("Tahoma", 16, text);
            using (line)
            {
                var bounds = Enumerable.Range(0, text.Length)
                    .SelectMany(i => line.GetTextBounds(i, 1))
                    .Select(b => $"{Math.Round(b.Rectangle.X * 300)}+{Math.Round(b.Rectangle.Width * 300)}"
                                 + (b.FlowDirection == FlowDirection.RightToLeft ? "r" : ""));
                Assert.Equal("7581+3914r 4600+2981r 3285+1315r -1+3286r 11496+1500 12996+2520 15516+2653 18169+2215",
                             string.Join(" ", bounds));
            }
        }

        // Items are cut where DirectWrite's script analysis changes script -- the Unicode Script
        // property, so Han, Hiragana and Katakana are three items and three glyph runs -- and a
        // control character with no visual (ZWSP, BOM, bidi controls) is an item of its own, drawn
        // as the blank glyph with no advance (GetBlankGlyphsForControlCharacters).
        [Fact]
        public void ItemsFollowDirectWritesScripts()
        {
            SkipUnlessInstalled("Yu Gothic UI");
            const string text = "\u65E5\u672C\u8A9E\u306E\u30C6\u30AD\u30B9\u30C8\u3002";
            (TextLine line, List<Run> runs) = Format("Yu Gothic UI", 16, text);
            using (line)
            {
                Assert.Equal(new[] { 0.0, 48.0, 61.0533 }, runs.Select(r => Math.Round(r.X, 4)).ToArray());
                Assert.Equal(new[] { 0, 4800, 9600, 14400, 18316, 21916, 25516, 29163, 32475, 35663 }, Carets(line, 9));
            }
        }

        [Theory]
        [InlineData("a\uFEFFb")]
        [InlineData("a\u200Bb")]
        public void ControlCharactersAreBlankAndWithoutAdvance(string text)
        {
            SkipUnlessInstalled("Arial");
            (TextLine line, List<Run> runs) = Format("Arial", 16, text);
            using (line)
            {
                Assert.Equal("68|3|69", string.Join("|", runs.Select(r => string.Join(" ", r.Glyphs))));
                Assert.Equal(new[] { 0, 2670, 2670, 5340 }, Carets(line, 3));
            }
        }

        // DWrite composes a base and its combining marks into the precomposed character (TextShaping's
        // CDM tables), all or nothing, and keeps a mark in its base's cluster either way.
        [Theory]
        [InlineData("a\u0301", "105", new[] { 0, 2670, 2670 })]
        [InlineData("e\u0302\u0301", "1219", new[] { 0, 2670, 2670, 2670 })]
        [InlineData("a\u0301\u0302", "68 1171 2114", new[] { 0, 2670, 2670, 2670 })]
        [InlineData("\u0391\u0301", "497", new[] { 0, 3204, 3204 })]
        public void CombiningMarksComposeAndCluster(string text, string glyphs, int[] carets)
        {
            SkipUnlessInstalled("Arial");
            (TextLine line, List<Run> runs) = Format("Arial", 16, text);
            using (line)
            {
                Assert.Equal(glyphs, string.Join(" ", runs.SelectMany(r => r.Glyphs)));
                Assert.Equal(carets, Carets(line, text.Length));
            }
        }

        [Fact]
        public void HebrewPointsShareTheirLettersCaret()
        {
            SkipUnlessInstalled("Segoe UI");
            const string text = "\u05E9\u05B8\u05C1\u05DC\u05D5\u05B9\u05DD";
            (TextLine line, _) = Format("Segoe UI", 16, text);
            using (line)
            {
                Assert.Equal(new[] { 11026, 7260, 7260, 7260, 4616, 3329, 3329, 11027 }, Carets(line, text.Length));
            }
        }
    }
}
