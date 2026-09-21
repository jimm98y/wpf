// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Bidirectional reordering: does a line that mixes directions put its runs where they belong?
//
// Bidi has two halves. The ANALYSIS -- which characters are right-to-left and at what embedding
// level -- is WPF's own managed Bidi class and runs off Windows unchanged. The REORDERING is the
// other half: a line's runs are produced in logical order and have to be laid out in visual order,
// which off Windows is `ManagedLineServices.ReorderRunsVisually` (rule L2 of UAX #9, over runs).
// Before it existed the managed line engine placed runs in logical order and every mixed line came
// out scrambled -- and a lone Hebrew run was drawn a full run-width to the left of its slot, on top
// of whatever preceded it, because a right-to-left run is anchored at its RIGHT edge.
//
// The assertions are about GEOMETRY -- where each run's box is -- rather than about glyph ids, so
// they hold in any font. Two properties carry most of the weight:
//
//   * the runs TILE the line: sorted by position they are contiguous, with no gap and no overlap.
//     Every placement bug found here showed up as a violation of that one invariant.
//   * the visual ORDER of the runs is the one bidi prescribes.
//
// Hebrew is used rather than Arabic throughout: it needs no shaping to be laid out, so a failure
// here is a reordering failure and not a shaping one.
//

using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.TextFormatting;
using Xunit;

namespace Wpf.Text.Tests
{
    public class BidiReorderingTests
    {
        private const int HebrewAlef = 0x05D0;
        private const string Shalom = "שלום";   // שלום
        private const string Olam = "עולם";     // עולם
        private const string Mil = "מיל";            // מיל

        /// <summary>
        ///  The invariant every placement bug violated: the runs of a line cover it exactly once.
        /// </summary>
        [Theory]
        [InlineData("abc def", false)]                    // pure LTR
        [InlineData(Shalom + " " + Olam, false)]          // pure RTL in an LTR paragraph
        [InlineData("abc " + Shalom + " def", false)]     // LTR paragraph, RTL island
        [InlineData("abc " + Shalom + " def", true)]      // RTL paragraph, LTR islands
        [InlineData(Shalom + " 123 " + Olam, false)]      // digits inside RTL
        [InlineData(Shalom + " " + Olam, true)]           // RTL paragraph, RTL text
        public void RunsTileTheLineExactly(string text, bool rtlParagraph)
        {
            Assert.SkipUnless(TextHarness.AnyInstalledFontCovers(HebrewAlef), "no font covers Hebrew");

            IReadOnlyList<Placed> runs = Layout(text, rtlParagraph);
            Assert.SkipWhen(runs.Count == 0, "no runs produced");

            var sorted = new List<Placed>(runs);
            sorted.Sort((a, b) => a.Left.CompareTo(b.Left));

            for (int i = 1; i < sorted.Count; i++)
            {
                double gap = sorted[i].Left - sorted[i - 1].Right;
                Assert.True(Math.Abs(gap) < 0.5,
                    $"run '{sorted[i].Text}' starts {gap:F2} from the end of '{sorted[i - 1].Text}' " +
                    $"-- the runs do not tile the line ({Describe(sorted)})");
            }
        }

        /// <summary>
        ///  A pure right-to-left line reads right to left: the FIRST logical word is the RIGHTMOST.
        ///  In logical order it would be leftmost, which is what used to happen.
        /// </summary>
        [Theory]
        [InlineData(false)]   // LTR paragraph containing only RTL text
        [InlineData(true)]    // RTL paragraph
        public void RightToLeftTextIsOrderedRightToLeft(bool rtlParagraph)
        {
            Assert.SkipUnless(TextHarness.AnyInstalledFontCovers(HebrewAlef), "no font covers Hebrew");

            IReadOnlyList<Placed> runs = Layout(Mil + " " + Shalom, rtlParagraph);
            Assert.SkipWhen(runs.Count < 3, "no runs produced");

            Placed first = Find(runs, Mil);
            Placed second = Find(runs, Shalom);

            Assert.True(first.Left > second.Right,
                $"the first Hebrew word is not to the right of the second ({Describe(runs)})");
        }

        /// <summary>
        ///  An RTL island inside an LTR paragraph stays between its Latin neighbours -- it neither
        ///  jumps nor, as it did, draws a run-width to the left of its own slot.
        /// </summary>
        [Fact]
        public void AnRtlIslandStaysBetweenItsLatinNeighbours()
        {
            Assert.SkipUnless(TextHarness.AnyInstalledFontCovers(HebrewAlef), "no font covers Hebrew");

            IReadOnlyList<Placed> runs = Layout("abc " + Shalom + " def", rtlParagraph: false);
            Assert.SkipWhen(runs.Count < 3, "no runs produced");

            Placed abc = Find(runs, "abc");
            Placed hebrew = Find(runs, Shalom);
            Placed def = Find(runs, "def");

            Assert.True(abc.Right <= hebrew.Left + 0.5,
                $"the Hebrew run overlaps 'abc' ({Describe(runs)})");
            Assert.True(hebrew.Right <= def.Left + 0.5,
                $"the Hebrew run overlaps 'def' ({Describe(runs)})");
        }

        /// <summary>
        ///  In an RTL paragraph the reading order reverses: the first logical Latin word is rightmost
        ///  and the last is leftmost.
        /// </summary>
        [Fact]
        public void AnRtlParagraphReversesItsLatinRuns()
        {
            Assert.SkipUnless(TextHarness.AnyInstalledFontCovers(HebrewAlef), "no font covers Hebrew");

            IReadOnlyList<Placed> runs = Layout("abc " + Shalom + " def", rtlParagraph: true);
            Assert.SkipWhen(runs.Count < 3, "no runs produced");

            Placed abc = Find(runs, "abc");
            Placed def = Find(runs, "def");

            Assert.True(abc.Left > def.Right,
                $"'abc' should be to the right of 'def' in an RTL paragraph ({Describe(runs)})");
        }

        /// <summary>
        ///  Digits inside RTL text keep their own left-to-right order while sitting in the
        ///  right-to-left flow: the Hebrew before them is to their RIGHT, the Hebrew after to their
        ///  LEFT.
        /// </summary>
        [Fact]
        public void DigitsInsideRightToLeftTextKeepTheirPlace()
        {
            Assert.SkipUnless(TextHarness.AnyInstalledFontCovers(HebrewAlef), "no font covers Hebrew");

            IReadOnlyList<Placed> runs = Layout(Shalom + " 123 " + Olam, rtlParagraph: false);
            Assert.SkipWhen(runs.Count < 3, "no runs produced");

            Placed before = Find(runs, Shalom);
            Placed digits = Find(runs, "123");
            Placed after = Find(runs, Olam);

            Assert.True(before.Left >= digits.Right - 0.5,
                $"the Hebrew preceding the digits should be to their right ({Describe(runs)})");
            Assert.True(after.Right <= digits.Left + 0.5,
                $"the Hebrew following the digits should be to their left ({Describe(runs)})");
        }

        /// <summary>
        ///  A left-to-right line is untouched by any of this: runs in logical order, left to right.
        /// </summary>
        [Fact]
        public void LeftToRightLinesAreUnchanged()
        {
            IReadOnlyList<Placed> runs = Layout("abc def", rtlParagraph: false);
            Assert.SkipWhen(runs.Count < 2, "no runs produced");

            Placed abc = Find(runs, "abc");
            Placed def = Find(runs, "def");

            Assert.True(abc.Right <= def.Left + 0.5, $"'abc' is not left of 'def' ({Describe(runs)})");
            Assert.True(abc.Left < 0.5, $"the line does not start at x=0 ({Describe(runs)})");
        }

        /// <summary>
        ///  Caret geometry follows the same reordering. A hit test at the middle of a character's
        ///  box must return that character -- in a right-to-left run too, where measuring forward
        ///  from the run's left edge (which is what the engine used to do) lands on the mirror
        ///  image of the character actually under the point.
        /// </summary>
        [Theory]
        [InlineData("abc def", false)]
        [InlineData(Shalom + " " + Olam, false)]
        [InlineData("abc " + Shalom + " def", false)]
        public void HitTestingAgreesWithWhereCharactersAreDrawn(string text, bool rtlParagraph)
        {
            Assert.SkipUnless(TextHarness.AnyInstalledFontCovers(HebrewAlef), "no font covers Hebrew");

            var runProperties = new RunProperties("Arial", 20);
            var paragraphProperties = new BidiParagraphProperties(runProperties, rtlParagraph);

            TextFormatter formatter = TextFormatter.Create();
            using TextLine line = formatter.FormatLine(
                new StringTextSource(text, runProperties), 0, 1000, paragraphProperties, null);

            for (int i = 0; i < text.Length; i++)
            {
                if (char.IsWhiteSpace(text[i])) continue;   // a space's box abuts two others

                IList<TextBounds> bounds = line.GetTextBounds(i, 1);
                if (bounds is null || bounds.Count == 0) continue;

                Rect box = bounds[0].Rectangle;
                if (box.Width <= 0) continue;

                CharacterHit hit = line.GetCharacterHitFromDistance(box.Left + box.Width / 2);

                Assert.True(hit.FirstCharacterIndex == i,
                    $"the middle of the box for character {i} ('{text[i]}', x {box.Left:F1}..{box.Right:F1}) " +
                    $"hit-tested to character {hit.FirstCharacterIndex}");
            }
        }

        #region Harness

        /// <summary>A run's horizontal extent, with the direction already resolved.</summary>
        private readonly struct Placed
        {
            public readonly double Left;
            public readonly double Right;
            public readonly string Text;

            public Placed(double left, double right, string text)
            {
                Left = left; Right = right; Text = text;
            }
        }

        /// <summary>
        ///  Formats a line and reports where each glyph run actually sits.
        /// </summary>
        /// <remarks>
        ///  A GlyphRun's BaselineOrigin is its LEADING edge, which for an odd bidi level is the
        ///  run's RIGHT edge -- so resolving the box means knowing the direction. That asymmetry is
        ///  the thing most easily got wrong, which is why the tests go through this one place.
        /// </remarks>
        private static IReadOnlyList<Placed> Layout(string text, bool rtlParagraph)
        {
            var runProperties = new RunProperties("Arial", 20);
            var paragraphProperties = new BidiParagraphProperties(runProperties, rtlParagraph);

            TextFormatter formatter = TextFormatter.Create();
            using TextLine line = formatter.FormatLine(
                new StringTextSource(text, runProperties), 0, 1000, paragraphProperties, null);

            var visual = new DrawingVisual();
            using (DrawingContext context = visual.RenderOpen())
            {
                line.Draw(context, new Point(0, 0), InvertAxes.None);
            }

            var glyphRuns = new List<GlyphRun>();
            Collect(visual.Drawing, glyphRuns);

            var placed = new List<Placed>();
            foreach (GlyphRun run in glyphRuns)
            {
                double width = 0;
                foreach (double advance in run.AdvanceWidths) width += advance;

                double origin = run.BaselineOrigin.X;
                bool rightToLeft = (run.BidiLevel & 1) != 0;
                double left = rightToLeft ? origin - width : origin;

                string chars = run.Characters is null
                    ? string.Empty
                    : new string(System.Linq.Enumerable.ToArray(run.Characters));

                placed.Add(new Placed(left, left + width, chars));
            }
            return placed;
        }

        private static Placed Find(IReadOnlyList<Placed> runs, string text)
        {
            foreach (Placed run in runs)
            {
                if (run.Text == text) return run;
            }
            Assert.Fail($"no run with text '{text}' among [{Describe(runs)}]");
            return default;
        }

        private static string Describe(IReadOnlyList<Placed> runs)
        {
            var parts = new List<string>();
            foreach (Placed run in runs)
            {
                parts.Add($"'{run.Text}'@{run.Left:F1}..{run.Right:F1}");
            }
            return string.Join(" ", parts);
        }

        private static void Collect(Drawing? drawing, List<GlyphRun> runs)
        {
            if (drawing is DrawingGroup group)
            {
                foreach (Drawing child in group.Children) Collect(child, runs);
            }
            else if (drawing is GlyphRunDrawing g && g.GlyphRun is GlyphRun run)
            {
                runs.Add(run);
            }
        }

        internal sealed class BidiParagraphProperties : TextParagraphProperties
        {
            private readonly TextRunProperties _defaults;
            private readonly bool _rtl;

            public BidiParagraphProperties(TextRunProperties defaults, bool rtl)
            {
                _defaults = defaults;
                _rtl = rtl;
            }

            public override FlowDirection FlowDirection
                => _rtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
            public override TextAlignment TextAlignment => TextAlignment.Left;
            public override double LineHeight => 0;
            public override bool FirstLineInParagraph => true;
            public override TextRunProperties DefaultTextRunProperties => _defaults;
            public override TextWrapping TextWrapping => TextWrapping.Wrap;
            public override TextMarkerProperties? TextMarkerProperties => null;
            public override double Indent => 0;
        }

        #endregion
    }
}
