// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// OpenType shaping off Windows: does a run of text come out as the glyphs the font actually
// prescribes, rather than as one nominal cmap glyph per character?
//
// Off Windows there is no DWrite, so the managed text backend produces nominal glyphs and
// ManagedOpenTypeShaper applies the font's GSUB and GPOS on top. These tests are about what that
// layer is FOR, so they assert on shaped output rather than on the shaper's internals:
//
//   * Arabic joins. A letter's glyph depends on its neighbours, so the same letter in the middle of
//     a word and on its own must be DIFFERENT glyphs. This is the assertion that fails against a
//     nominal run, and it needs no knowledge of any particular font's glyph ids.
//   * marks are positioned. A combining mark has no advance of its own and is placed by GPOS
//     attachment; without it every mark sits at its cell origin. So a run with marks must carry a
//     non-zero glyph offset somewhere.
//   * Arabic renders AT ALL. The regression that motivated most of this: a font whose GSUB grows
//     the run (ccmp decomposition) overflowed a fixed glyph buffer, the truncated run no longer
//     agreed with its cluster map, and the whole thing was dropped -- an empty drawing and text
//     that simply was not there. Noto Nastaliq Urdu does exactly that, and it is what the fallback
//     chain picks for Arabic on a machine with no Windows fonts.
//
// Everything skips rather than fails when the machine has no font for the script, matching
// ScriptFallbackTests: which fonts a box has is its own business.
//

using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.TextFormatting;
using Xunit;

namespace Wpf.Text.Tests
{
    public class ShapingTests
    {
        private const int ArabicMeem = 0x0645;      // م
        private const int HebrewAlef = 0x05D0;      // א
        private const int Devanagari = 0x0939;      // ह

        /// <summary>
        ///  The joining test, and the one that cannot pass without script-aware shaping: ب between
        ///  two letters takes its medial form, and alone takes its isolated form. A nominal run gives
        ///  the same glyph both times.
        /// </summary>
        [Fact]
        public void ArabicLettersTakeTheirPositionalForms()
        {
            Assert.SkipUnless(TextHarness.AnyInstalledFontCovers(ArabicMeem), "no font covers Arabic");

            // بـب: the same letter twice -- initial then final -- so a shaped run has two DIFFERENT
            // glyphs where a nominal one repeats a single isolated form.
            IReadOnlyList<ushort> joined = GlyphsOf("بب");
            Assert.SkipWhen(joined.Count < 2, "the Arabic fallback font produced no run");

            Assert.True(joined[0] != joined[1],
                $"both forms of beh came out as glyph {joined[0]}: the run was not shaped");
        }

        /// <summary>
        ///  The same letter isolated and in a word must differ. Complements the test above from the
        ///  other direction: that one proves two positions differ within a run, this one proves the
        ///  shaped form differs from the isolated one.
        /// </summary>
        [Fact]
        public void AnArabicLetterDiffersBetweenIsolatedAndJoined()
        {
            Assert.SkipUnless(TextHarness.AnyInstalledFontCovers(ArabicMeem), "no font covers Arabic");

            IReadOnlyList<ushort> isolated = GlyphsOf("ب");
            IReadOnlyList<ushort> joined = GlyphsOf("بب");
            Assert.SkipWhen(isolated.Count < 1 || joined.Count < 2, "the Arabic fallback font produced no run");

            Assert.True(isolated[0] != joined[0],
                $"beh is glyph {isolated[0]} both alone and word-initial: the run was not shaped");
        }

        /// <summary>
        ///  Arabic must produce a drawable run in whatever font the fallback chain picks. This is the
        ///  buffer-overflow regression: it was not that Arabic looked wrong, it was that Arabic
        ///  produced no glyph runs at all and the text was invisible.
        /// </summary>
        [Theory]
        [InlineData("مرحبا")]
        [InlineData("السلام عليكم")]
        public void ArabicProducesGlyphs(string text)
        {
            Assert.SkipUnless(TextHarness.AnyInstalledFontCovers(ArabicMeem), "no font covers Arabic");

            FormattedRun run = TextHarness.Format(text);

            Assert.True(run.GlyphCount > 0, "Arabic produced no glyph runs at all -- the text is invisible");
            Assert.True(run.NotdefCount == 0,
                $"{run.NotdefCount} of {run.GlyphCount} glyphs are .notdef in [{string.Join(", ", run.Fonts)}]");
        }

        /// <summary>
        ///  A font that grows the run during substitution must still render. Kept separate from the
        ///  test above because it names the specific shape of the bug: more glyphs out than in.
        /// </summary>
        [Fact]
        public void ArabicRendersInAFontWhoseSubstitutionGrowsTheRun()
        {
            Assert.SkipUnless(TextHarness.FamilyInstalled("Noto Nastaliq Urdu"), "Noto Nastaliq Urdu not installed");

            FormattedRun run = TextHarness.Format("مرحبا", "Noto Nastaliq Urdu");

            // Five characters in, more than five glyphs out, and all of them drawn.
            Assert.True(run.GlyphCount > 5,
                $"expected substitution to grow the run past 5 glyphs, got {run.GlyphCount}");
            Assert.True(run.NotdefCount == 0, $"{run.NotdefCount} .notdef glyphs");
        }

        /// <summary>
        ///  GPOS attaches combining marks to their base. Without it a mark keeps the zero offset it
        ///  was given and lands on the following character's origin instead of over its own base.
        /// </summary>
        [Theory]
        [InlineData("مَرْحَبًا", ArabicMeem)]        // Arabic with full harakat
        [InlineData("שָׁלוֹם", HebrewAlef)]           // Hebrew with niqqud
        public void CombiningMarksArePositioned(string text, int probeCodepoint)
        {
            Assert.SkipUnless(TextHarness.AnyInstalledFontCovers(probeCodepoint), "no font covers this script");

            IReadOnlyList<Point> offsets = OffsetsOf(text);
            Assert.SkipWhen(offsets.Count == 0, "no run produced");

            bool anyPositioned = false;
            foreach (Point offset in offsets)
            {
                if (offset.X != 0 || offset.Y != 0) { anyPositioned = true; break; }
            }

            Assert.True(anyPositioned,
                "every glyph offset is zero: GPOS mark attachment did not run, so the marks are not on their bases");
        }

        /// <summary>
        ///  Devanagari conjuncts: हिन्दी is six characters and fewer than six glyphs once the
        ///  half-form and conjunct substitutions have run.
        ///
        ///  Note this does NOT assert correct Devanagari. The Indic feature set is applied but the
        ///  syllable reordering that moves a pre-base matra ahead of its consonant is not implemented,
        ///  so the output is better than nominal and still not right. The assertion is deliberately
        ///  limited to what is actually claimed.
        /// </summary>
        [Fact]
        public void DevanagariFormsConjuncts()
        {
            Assert.SkipUnless(TextHarness.AnyInstalledFontCovers(Devanagari), "no font covers Devanagari");

            IReadOnlyList<ushort> glyphs = GlyphsOf("हिन्दी");
            Assert.SkipWhen(glyphs.Count == 0, "no run produced");

            Assert.True(glyphs.Count < 6,
                $"6 characters produced {glyphs.Count} glyphs: no conjunct or half-form substitution ran");
        }

        /// <summary>
        ///  Latin must be unaffected by all of the above: same glyph count as characters for text with
        ///  no ligatures in it, and no .notdef.
        /// </summary>
        [Fact]
        public void LatinIsUnchanged()
        {
            IReadOnlyList<ushort> glyphs = GlyphsOf("Hello");

            Assert.Equal(5, glyphs.Count);
            Assert.DoesNotContain((ushort)0, glyphs);
        }

        /// <summary>
        ///  A right-to-left run is marked as one. The renderer keys its leftward pen walk off this, so
        ///  a run that loses its bidi level draws mirrored and in the wrong place.
        /// </summary>
        [Theory]
        [InlineData("مرحبا", ArabicMeem)]
        [InlineData("שלום", HebrewAlef)]
        public void RightToLeftRunsCarryAnOddBidiLevel(string text, int probeCodepoint)
        {
            Assert.SkipUnless(TextHarness.AnyInstalledFontCovers(probeCodepoint), "no font covers this script");

            IReadOnlyList<GlyphRun> runs = RunsOf(text);
            Assert.SkipWhen(runs.Count == 0, "no run produced");

            foreach (GlyphRun run in runs)
            {
                Assert.True((run.BidiLevel & 1) != 0,
                    $"an RTL run came through with bidi level {run.BidiLevel}");
            }
        }

        /// <summary>
        ///  A trimmed line's COLLAPSING SYMBOL is shaped like everything else.
        /// </summary>
        /// <remarks>
        ///  The symbol a collapsed line ends with is glyphed by FormattedTextSymbols, which asks the
        ///  text backend for glyphs in a single call instead of going through the two LineServices
        ///  callbacks -- a third place nominal glyphs can reach the screen. Only a custom
        ///  TextCollapsingProperties.Symbol reaches it (the default ellipsis is one Latin character),
        ///  and it takes an OVERFLOWING line to reach at all: Collapse returns the line untouched
        ///  when it fits, so the paragraph must be narrow AND non-wrapping.
        /// </remarks>
        [Fact]
        public void TheCollapsingSymbolIsShaped()
        {
            Assert.SkipUnless(TextHarness.AnyInstalledFontCovers(ArabicMeem), "no font covers Arabic");

            var runProperties = new RunProperties("Segoe UI", 32);
            var paragraphProperties = new NoWrapParagraphProperties(runProperties);

            TextFormatter formatter = TextFormatter.Create();
            using TextLine line = formatter.FormatLine(
                new StringTextSource("wide enough that it will certainly be collapsed somewhere", runProperties),
                0, 200, paragraphProperties, null);

            Assert.True(line.HasOverflowed, "the line must overflow before Collapse will do anything");

            const string symbolText = "بب";
            var symbol = new TextCharacters(symbolText, runProperties);
            using TextLine collapsed = line.Collapse(new ArabicEllipsis(200, symbol));

            Assert.True(collapsed.HasCollapsed, "the line did not collapse");

            var visual = new DrawingVisual();
            using (DrawingContext context = visual.RenderOpen())
            {
                collapsed.Draw(context, new Point(0, 0), InvertAxes.None);
            }

            var runs = new List<GlyphRun>();
            Collect(visual.Drawing, runs);

            GlyphRun? symbolRun = null;
            foreach (GlyphRun run in runs)
            {
                if (run.Characters is null) continue;
                if (new string(System.Linq.Enumerable.ToArray(run.Characters)) == symbolText) symbolRun = run;
            }

            Assert.SkipWhen(symbolRun is null, "the collapsing symbol produced no run");

            // Unshaped, both behs are the same isolated glyph. Shaped, they are not: the pair takes
            // initial + final forms, and in a Nastaliq font the run grows past two glyphs as well.
            bool allSame = true;
            for (int i = 1; i < symbolRun!.GlyphIndices.Count; i++)
            {
                if (symbolRun.GlyphIndices[i] != symbolRun.GlyphIndices[0]) { allSame = false; break; }
            }

            Assert.False(allSame && symbolRun.GlyphIndices.Count > 1,
                $"the collapsing symbol came out as glyph {symbolRun.GlyphIndices[0]} repeated " +
                $"{symbolRun.GlyphIndices.Count} times: it was not shaped");
        }

        /// <summary>A collapsing rule whose symbol the test supplies.</summary>
        private sealed class ArabicEllipsis : TextCollapsingProperties
        {
            private readonly double _width;
            private readonly TextRun _symbol;

            public ArabicEllipsis(double width, TextRun symbol) { _width = width; _symbol = symbol; }

            public override double Width => _width;
            public override TextRun Symbol => _symbol;
            public override TextCollapsingStyle Style => TextCollapsingStyle.TrailingCharacter;
        }

        /// <summary>A paragraph that does not wrap, so a long line overflows instead of breaking.</summary>
        private sealed class NoWrapParagraphProperties : TextParagraphProperties
        {
            private readonly TextRunProperties _defaults;

            public NoWrapParagraphProperties(TextRunProperties defaults) { _defaults = defaults; }

            public override FlowDirection FlowDirection => FlowDirection.LeftToRight;
            public override TextAlignment TextAlignment => TextAlignment.Left;
            public override double LineHeight => 0;
            public override bool FirstLineInParagraph => true;
            public override TextRunProperties DefaultTextRunProperties => _defaults;
            public override TextWrapping TextWrapping => TextWrapping.NoWrap;
            public override TextMarkerProperties? TextMarkerProperties => null;
            public override double Indent => 0;
        }

        #region Harness


        private static IReadOnlyList<ushort> GlyphsOf(string text, string family = "Segoe UI")
        {
            var glyphs = new List<ushort>();
            foreach (GlyphRun run in RunsOf(text, family))
            {
                glyphs.AddRange(run.GlyphIndices);
            }
            return glyphs;
        }

        private static IReadOnlyList<Point> OffsetsOf(string text, string family = "Segoe UI")
        {
            var offsets = new List<Point>();
            foreach (GlyphRun run in RunsOf(text, family))
            {
                if (run.GlyphOffsets is null) continue;
                offsets.AddRange(run.GlyphOffsets);
            }
            return offsets;
        }

        /// <summary>
        ///  Formats one line and returns the glyph runs it drew. The public TextFormatter path, so
        ///  what is under test is what a TextBlock would get.
        /// </summary>
        private static IReadOnlyList<GlyphRun> RunsOf(string text, string family = "Segoe UI", double emSize = 32)
        {
            var runProperties = new RunProperties(family, emSize);
            var paragraphProperties = new ParagraphProperties(runProperties);

            TextFormatter formatter = TextFormatter.Create();
            using TextLine line = formatter.FormatLine(
                new StringTextSource(text, runProperties), 0, 1000, paragraphProperties, null);

            var visual = new DrawingVisual();
            using (DrawingContext context = visual.RenderOpen())
            {
                line.Draw(context, new Point(0, 0), InvertAxes.None);
            }

            var runs = new List<GlyphRun>();
            Collect(visual.Drawing, runs);
            return runs;
        }

        private static void Collect(Drawing? drawing, List<GlyphRun> runs)
        {
            if (drawing is DrawingGroup group)
            {
                foreach (Drawing child in group.Children) Collect(child, runs);
            }
            else if (drawing is GlyphRunDrawing glyphDrawing && glyphDrawing.GlyphRun is GlyphRun run)
            {
                runs.Add(run);
            }
        }

        #endregion
    }
}
