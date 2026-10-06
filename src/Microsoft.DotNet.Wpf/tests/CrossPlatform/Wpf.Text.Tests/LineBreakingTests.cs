// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Where a wrapped paragraph may be cut.
//
// Japanese, Chinese and Korean are written without spaces, so a breaker that only knows whitespace
// finds no opportunity anywhere in a CJK paragraph. That was the state here: such a paragraph only
// wrapped by way of the formatter's emergency "at least one character" path, which cut wherever the
// column happened to land -- one character PAST the edge, because the fitted-character count the
// emergency path used includes the first character that crosses it, and with no regard for the
// characters that may not begin or end a line.
//
// The fixtures use a font whose CJK advances are exactly one em, so "six characters fit in 100px at
// a 16px em" is arithmetic rather than a measurement of a particular font's metrics.
//

using System;
using System.Collections.Generic;
using Xunit;

namespace Wpf.Text.Tests
{
    public class LineBreakingTests
    {
        private const double EmSize = 16;
        private const double Column = 100;      // exactly 6.25 em: six full-width characters fit
        private const int Fits = 6;

        // A pan-CJK font, if the machine has one. Named explicitly rather than relying on fallback
        // so a failure here is about breaking, not about font resolution.
        private static readonly string[] CjkCandidates =
        {
            "Noto Sans CJK JP", "Source Han Sans", "Noto Sans JP", "Hiragino Sans",
            "Yu Gothic UI", "MS Gothic", "Meiryo",
        };

        [Fact]
        public void CjkWrapsBetweenIdeographs()
        {
            string font = RequireCjkFont();
            const string paragraph = "日本語のテキストは空白で区切られないので";

            List<(string Text, double Width)> lines = TextHarness.Wrap(paragraph, font, EmSize, Column);

            Assert.True(lines.Count > 1, "a CJK paragraph wider than the column must wrap");
            Assert.Equal(paragraph, string.Concat(lines.ConvertAll(l => l.Text)));
        }

        /// <summary>
        /// The regression that made every wrapped CJK line hang one character over its container:
        /// the break was taken at the count of characters MEASURED, which includes the one that
        /// crossed the boundary, rather than the count that fit.
        /// </summary>
        [Fact]
        public void WrappedCjkLinesStayInsideTheColumn()
        {
            string font = RequireCjkFont();

            foreach ((string text, double width) in
                     TextHarness.Wrap("日本語のテキストは空白で区切られないので", font, EmSize, Column))
            {
                Assert.True(width <= Column, $"line \"{text}\" is {width}px wide in a {Column}px column");
            }
        }

        [Fact]
        public void LatinStillBreaksAtSpaces()
        {
            const string paragraph = "the quick brown fox jumps over the lazy dog";

            foreach ((string text, double width) in TextHarness.Wrap(paragraph, "Segoe UI", EmSize, Column))
            {
                Assert.True(width <= Column, $"line \"{text}\" is {width}px wide in a {Column}px column");

                // Every line but the last ends at a word boundary, never mid-word.
                string trimmed = text.TrimEnd();
                Assert.True(trimmed.Length == 0 || paragraph.Contains(trimmed + " ") || paragraph.EndsWith(trimmed),
                    $"\"{trimmed}\" is not a whole-word prefix");
            }
        }

        /// <summary>
        /// Kinsoku shori: the characters that may not begin a line. Each case puts one exactly where
        /// the greedy break would land, and the breaker has to back up a character so it stays with
        /// the text it belongs to.
        /// </summary>
        [Theory]
        [InlineData('、')]     // ideographic comma
        [InlineData('。')]     // ideographic full stop
        [InlineData('」')]     // closing corner bracket
        [InlineData('）')]     // fullwidth closing parenthesis
        [InlineData('ゃ')]     // small kana
        [InlineData('ー')]     // prolonged sound mark
        public void CharactersThatMayNotStartALineAreHeldBack(char prohibited)
        {
            string font = RequireCjkFont();

            // The prohibited character sits at index 6 -- the first position on line two if the
            // break were taken purely on width.
            string paragraph = "日本語のテキ" + prohibited + "ストです";

            List<(string Text, double Width)> lines = TextHarness.Wrap(paragraph, font, EmSize, Column);

            Assert.True(lines.Count > 1, "the paragraph must wrap for this to mean anything");
            Assert.False(lines[1].Text.StartsWith(prohibited),
                $"line 2 begins with '{prohibited}', which may not start a line");
        }

        /// <summary>The mirror rule: characters that may not END a line move down with what follows.</summary>
        [Theory]
        [InlineData('「')]     // opening corner bracket
        [InlineData('（')]     // fullwidth opening parenthesis
        public void CharactersThatMayNotEndALineMoveDown(char prohibited)
        {
            string font = RequireCjkFont();

            // At index 5, so the greedy break would leave it as the last character of line one.
            string paragraph = "日本語のテ" + prohibited + "キストです";

            List<(string Text, double Width)> lines = TextHarness.Wrap(paragraph, font, EmSize, Column);

            Assert.True(lines.Count > 1, "the paragraph must wrap for this to mean anything");
            Assert.False(lines[0].Text.EndsWith(prohibited),
                $"line 1 ends with '{prohibited}', which may not end a line");
        }

        [Fact]
        public void UnrestrictedCjkUsesTheWholeColumn()
        {
            string font = RequireCjkFont();

            List<(string Text, double Width)> lines = TextHarness.Wrap("日本語のテキストです", font, EmSize, Column);

            // Nothing prohibits a break here, so the first line takes every character that fits.
            Assert.Equal(Fits, lines[0].Text.Length);
        }

        // Every character these fixtures count on: an ideograph, the kana the paragraphs are built
        // from, and the punctuation the kinsoku theories place at the break. All of it has to be
        // full-width for "six characters fit in 100px" to mean anything.
        private const string FullWidthSample = "日本語のテキストです、。」）「（";

        private static string RequireCjkFont()
        {
            foreach (string candidate in CjkCandidates)
            {
                FormattedRun run = TextHarness.Format(FullWidthSample, candidate, EmSize);

                // Measuring the whole sample rather than one ideograph is the point. A named family
                // that is not installed resolves elsewhere, and what it lands on is usually a
                // PROPORTIONAL UI face -- Yu Gothic UI is the one Windows answers with -- whose
                // kanji are a full em but whose kana and punctuation are visibly narrower. Checking
                // 日 alone accepted such a font and then seven characters fit in the six-character
                // column, which reads as a line-breaking bug and is nothing of the sort.
                if (run.AllGlyphsPresent &&
                    Math.Abs(run.Width - FullWidthSample.Length * EmSize) < 0.5)
                {
                    return candidate;
                }
            }

            Assert.Skip("no full-width CJK font installed");
            return string.Empty;   // unreachable: Assert.Skip throws
        }
    }
}
