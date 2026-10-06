// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Justification: TextAlignment.Justify, in the managed line engine.
//
// A justified line is stretched to fill its column by widening the spaces between words. Off
// Windows that is `ManagedLineServices.JustifyLine`; before it existed the alignment was accepted
// and then ignored, so a justified TextBlock was indistinguishable from a left-aligned one.
//
// The rules being held here are the ones that make justified text look like justified text rather
// than like stretched text:
//
//   * a wrapped line reaches the column edge EXACTLY;
//   * the LAST line of a paragraph does not -- stretching it turns a two-word final line into two
//     words at opposite edges;
//   * the stretch goes into the spaces, not the letters;
//   * trailing whitespace hangs past the edge instead of being stretched, so the last word of a
//     line ends flush with it.
//

using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.TextFormatting;
using Xunit;

namespace Wpf.Text.Tests
{
    public class JustificationTests
    {
        private const string Paragraph =
            "the quick brown fox jumps over the lazy dog and keeps running onward";
        private const double Column = 300;

        /// <summary>
        ///  Every line but the last fills the column exactly. This is the whole feature, and it is
        ///  what a no-op justification fails.
        /// </summary>
        [Fact]
        public void WrappedLinesFillTheColumn()
        {
            IReadOnlyList<Measured> lines = Format(Paragraph, TextAlignment.Justify);
            Assert.True(lines.Count >= 2, "the sample must wrap for this to mean anything");

            for (int i = 0; i < lines.Count - 1; i++)
            {
                Assert.True(Math.Abs(lines[i].Width - Column) < 1.0,
                    $"justified line {i} is {lines[i].Width:F1} wide, not {Column} ('{lines[i].Text}')");
            }
        }

        /// <summary>
        ///  The last line stays ragged. It is the one line justification must leave alone.
        /// </summary>
        [Fact]
        public void TheLastLineIsNotStretched()
        {
            IReadOnlyList<Measured> lines = Format(Paragraph, TextAlignment.Justify);
            Measured last = lines[lines.Count - 1];

            Assert.True(last.Width < Column - 10,
                $"the last line was stretched to {last.Width:F1} ('{last.Text}')");
        }

        /// <summary>
        ///  Justification is off by default: left-aligned text is untouched, and narrower than the
        ///  column. Guards against justifying everything.
        /// </summary>
        [Fact]
        public void LeftAlignedTextIsNotStretched()
        {
            IReadOnlyList<Measured> lines = Format(Paragraph, TextAlignment.Left);

            foreach (Measured line in lines)
            {
                Assert.True(line.Width < Column,
                    $"left-aligned line is {line.Width:F1} wide, at or past the {Column} column ('{line.Text}')");
            }
        }

        /// <summary>
        ///  The stretch goes into the SPACES. Widening the letters instead would justify the line
        ///  just as well and look wrong, so this checks the mechanism and not only the total.
        /// </summary>
        [Fact]
        public void TheExtraWidthGoesIntoTheSpaces()
        {
            IReadOnlyList<double> ragged = FirstLineAdvances(Paragraph, TextAlignment.Left, out string raggedText);
            IReadOnlyList<double> justified = FirstLineAdvances(Paragraph, TextAlignment.Justify, out string justifiedText);

            Assert.SkipWhen(ragged.Count == 0 || ragged.Count != justified.Count,
                "the two runs do not correspond glyph for glyph");
            Assert.Equal(raggedText, justifiedText);

            // The line's own trailing space is not an expansion point -- it hangs past the edge --
            // so the spaces that must grow are the ones with a word still to come after them.
            int lastNonSpace = raggedText.Length - 1;
            while (lastNonSpace >= 0 && raggedText[lastNonSpace] == ' ') lastNonSpace--;

            int grewCount = 0;
            for (int i = 0; i < ragged.Count; i++)
            {
                bool isInteriorSpace = i < lastNonSpace && raggedText[i] == ' ';
                double grew = justified[i] - ragged[i];

                if (isInteriorSpace)
                {
                    Assert.True(grew > 0, $"interior space at {i} did not grow");
                    grewCount++;
                }
                else if (i < raggedText.Length && raggedText[i] != ' ')
                {
                    Assert.True(Math.Abs(grew) < 0.01,
                        $"non-space glyph at {i} ('{raggedText[i]}') changed width by {grew:F3}");
                }
            }

            Assert.True(grewCount > 0, "no space grew at all");
        }

        /// <summary>
        ///  Trailing whitespace hangs past the column rather than being stretched into it: the
        ///  measured width excludes it, so the last WORD of the line ends flush with the edge.
        /// </summary>
        [Fact]
        public void TrailingWhitespaceHangsPastTheEdge()
        {
            IReadOnlyList<Measured> lines = Format(Paragraph, TextAlignment.Justify);
            Measured first = lines[0];

            Assert.True(first.WidthIncludingTrailing >= first.Width,
                "width including trailing whitespace cannot be the smaller of the two");
            Assert.True(Math.Abs(first.Width - Column) < 1.0,
                $"the justified width should be the column, not {first.Width:F1}");
        }

        #region Harness

        private readonly record struct Measured(double Width, double WidthIncludingTrailing, string Text);

        private static IReadOnlyList<Measured> Format(string text, TextAlignment alignment)
        {
            var runProperties = new RunProperties("Arial", 16);
            var paragraphProperties = new AlignedParagraphProperties(runProperties, alignment);
            var source = new StringTextSource(text, runProperties);
            TextFormatter formatter = TextFormatter.Create();

            var lines = new List<Measured>();
            int position = 0;
            while (position < text.Length && lines.Count < 20)
            {
                using TextLine line = formatter.FormatLine(source, position, Column, paragraphProperties, null);
                if (line.Length <= 0) break;

                lines.Add(new Measured(
                    line.Width,
                    line.WidthIncludingTrailingWhitespace,
                    text.Substring(position, Math.Min(line.Length, text.Length - position)).Trim()));

                position += line.Length;
            }
            return lines;
        }

        /// <summary>The advance of every glyph on the first line, with the characters beside them.</summary>
        private static IReadOnlyList<double> FirstLineAdvances(string text, TextAlignment alignment, out string chars)
        {
            var runProperties = new RunProperties("Arial", 16);
            var paragraphProperties = new AlignedParagraphProperties(runProperties, alignment);
            TextFormatter formatter = TextFormatter.Create();

            using TextLine line = formatter.FormatLine(
                new StringTextSource(text, runProperties), 0, Column, paragraphProperties, null);

            var visual = new DrawingVisual();
            using (DrawingContext context = visual.RenderOpen())
            {
                line.Draw(context, new Point(0, 0), InvertAxes.None);
            }

            var runs = new List<GlyphRun>();
            Collect(visual.Drawing, runs);

            var advances = new List<double>();
            var builder = new System.Text.StringBuilder();
            foreach (GlyphRun run in runs)
            {
                // One glyph per character is the case this test needs; anything shaped is skipped by
                // the caller's count check rather than silently compared out of step.
                foreach (double advance in run.AdvanceWidths) advances.Add(advance);
                if (run.Characters is not null)
                {
                    foreach (char c in run.Characters) builder.Append(c);
                }
            }

            chars = builder.ToString();
            return advances;
        }

        private static void Collect(Drawing? drawing, List<GlyphRun> runs)
        {
            if (drawing is DrawingGroup group)
            {
                foreach (Drawing child in group.Children) Collect(child, runs);
            }
            else if (drawing is GlyphRunDrawing g && g.GlyphRun is GlyphRun run) runs.Add(run);
        }

        internal sealed class AlignedParagraphProperties : TextParagraphProperties
        {
            private readonly TextRunProperties _defaults;
            private readonly TextAlignment _alignment;

            public AlignedParagraphProperties(TextRunProperties defaults, TextAlignment alignment)
            {
                _defaults = defaults;
                _alignment = alignment;
            }

            public override FlowDirection FlowDirection => FlowDirection.LeftToRight;
            public override TextAlignment TextAlignment => _alignment;
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
