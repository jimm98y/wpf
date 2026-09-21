// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// FlowDocument.IsOptimalParagraphEnabled: does the document actually break its paragraphs
// differently when asked to?
//
// On Windows this property selected PTS's optimal-paragraph mode. This port ships no PTS -- the
// whole engine is replaced by ManagedFlowLayout -- so the property used to be accepted and then
// ignored, and every paragraph broke greedily. It now selects ManagedFlowLayout's own minimum-
// raggedness breaker.
//
// Greedy breaking fills each line as far as it will go and never reconsiders, so it leaves a short
// last line whenever a long word lands badly. Optimal breaking spreads the slack across the
// paragraph. The tests below are about that difference being real and being an improvement, not
// about matching PTS line for line -- which nothing here could do.
//

using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Documents;
using Xunit;

namespace Wpf.Document.Tests
{
    public class OptimalParagraphTests
    {
        // Greedy breaks this into FOUR lines, the last holding a single short word; spreading the
        // slack fits the same text into THREE. Nonsense words, because the property under test is
        // the arithmetic of word widths and a real sentence that happens to break badly at one font
        // size stops doing so at the next.
        private const string DropsALine =
            "gujdvuvzekavs qdztgddxsxuhjmw tqcnuircaeol uspfjniyuq sicminlic qjsfdhl " +
            "scprhxefczzovz pv gftmsfjg jwaf wbbox ae dlmjoo jdjsbgcgjki tv ctsrtxl ukbxqbgvurm";

        /// <summary>
        ///  The property changes the layout at all. Before, the two were identical.
        /// </summary>
        [Fact]
        public void OptimalBreakingChangesWhereLinesBreak()
        {
            IReadOnlyList<string> greedy = LinesOf(DropsALine, optimal: false);
            IReadOnlyList<string> optimal = LinesOf(DropsALine, optimal: true);

            Assert.True(greedy.Count > 1, $"the sample must wrap: got [{string.Join(" | ", greedy)}]");
            Assert.NotEqual(greedy, optimal);

            // The specific improvement: greedy needs an extra line to hold what it spilled.
            Assert.True(optimal.Count < greedy.Count,
                $"optimal used {optimal.Count} lines, greedy {greedy.Count}; " +
                $"greedy=[{string.Join(" | ", greedy)}] optimal=[{string.Join(" | ", optimal)}]");
        }

        /// <summary>
        ///  No text is lost or duplicated by the reflow: both strategies lay out the same words in
        ///  the same order, only split differently.
        /// </summary>
        [Fact]
        public void OptimalBreakingPreservesTheText()
        {
            string greedy = string.Join(" ", LinesOf(DropsALine, optimal: false));
            string optimal = string.Join(" ", LinesOf(DropsALine, optimal: true));

            Assert.Equal(Normalize(greedy), Normalize(optimal));
        }

        /// <summary>
        ///  Off by default, so nothing changes for a document that does not ask.
        /// </summary>
        [Fact]
        public void TheDefaultIsGreedy()
        {
            var doc = new FlowDocument();
            Assert.False((bool)doc.GetValue(FlowDocument.IsOptimalParagraphEnabledProperty));
        }

        /// <summary>
        ///  A word wider than the column still lays out rather than making the paragraph
        ///  unbreakable -- the one input that can make a naive minimum-cost breaker find no solution
        ///  at all.
        /// </summary>
        [Fact]
        public void AWordWiderThanTheColumnStillLaysOut()
        {
            const string text = "short Pneumonoultramicroscopicsilicovolcanoconiosisandthensome short";

            IReadOnlyList<string> lines = LinesOf(text, optimal: true, width: 200);

            Assert.True(lines.Count > 0, "nothing was laid out at all");
            Assert.Contains(lines, l => l.Contains("Pneumono", StringComparison.Ordinal));
        }

        #region Harness

        private static string Describe(IReadOnlyList<double> widths)
        {
            var parts = new List<string>();
            foreach (double w in widths) parts.Add(w.ToString("F0"));
            return "[" + string.Join(",", parts) + "]";
        }

        private static string Normalize(string s) => string.Join(" ", s.Split((char[])null, StringSplitOptions.RemoveEmptyEntries));

        private static FlowDocument Document(string text, bool optimal)
        {
            var doc = new FlowDocument();
            doc.SetValue(FlowDocument.IsOptimalParagraphEnabledProperty, optimal);
            doc.Blocks.Add(DocumentHarness.Para(text));
            return doc;
        }

        /// <summary>The visual lines of the paginated paragraph, top to bottom.</summary>
        private static IReadOnlyList<string> LinesOf(string text, bool optimal, double width = 400)
        {
            PageContent page = DocumentHarness.Paginate(Document(text, optimal), width);
            return GroupIntoLines(page).ConvertAll(l => l.Text);
        }

        /// <summary>
        ///  The paginated path emits one run per word, so visual lines are recovered by grouping
        ///  runs that share a baseline and ordering them left to right.
        /// </summary>
        private static List<(string Text, double Width)> GroupIntoLines(PageContent page)
        {
            var byLine = new SortedDictionary<int, List<(string Text, Rect Bounds)>>();
            foreach ((string runText, Rect bounds) in page.Runs)
            {
                int key = (int)Math.Round(bounds.Top / 2.0);   // tolerate sub-pixel baseline drift
                if (!byLine.TryGetValue(key, out List<(string, Rect)> bucket))
                {
                    bucket = new List<(string, Rect)>();
                    byLine[key] = bucket;
                }
                bucket.Add((runText, bounds));
            }

            var lines = new List<(string, double)>();
            foreach (KeyValuePair<int, List<(string Text, Rect Bounds)>> entry in byLine)
            {
                List<(string Text, Rect Bounds)> bucket = entry.Value;
                bucket.Sort((a, b) => a.Bounds.Left.CompareTo(b.Bounds.Left));

                var words = new List<string>();
                double left = double.MaxValue, right = 0;
                foreach ((string runText, Rect bounds) in bucket)
                {
                    words.Add(runText);
                    left = Math.Min(left, bounds.Left);
                    right = Math.Max(right, bounds.Right);
                }
                lines.Add((string.Join(" ", words), right - left));
            }
            return lines;
        }

        #endregion
    }
}
