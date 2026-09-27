// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// A line is as wide as its glyphs advance once KERNED, as stock WPF measures it.
//
// Two things kept the kerning out. The managed line layout measured each run with its characters'
// nominal widths, so GPOS kerning moved glyphs inside a run without narrowing it. And the managed
// itemizer cut the text at every space, so the text store made one run per word and a pair across
// a word boundary (" A", " T") was never shaped at all -- DWrite gives a space the script of the
// text around it, and LineServices shapes neighbouring runs of one item as one string. Every Arial,
// Times New Roman and Verdana line came out wider than stock WPF's by exactly its kerning.
//
// The expected widths are the ones stock WPF (Microsoft.WindowsDesktop.App 10) reports on Windows 11,
// in the default Ideal formatting mode. Skips where the font is not installed.
//

using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Xunit;

namespace Wpf.Text.Tests
{
    public class KerningWidthTests
    {
        [Theory]
        // family, text, size, stock WPF's WidthIncludingTrailingWhitespace
        [InlineData("Arial", "AVAVAV To Ty", 100, 626.22)]
        [InlineData("Times New Roman", "AVAVAV To Ty", 100, 621.63)]
        [InlineData("Verdana", "AVAVAV To Ty", 100, 688.4333)]
        [InlineData("Segoe UI", "AVAVAV To Ty", 100, 598.88)]
        // " A" is the only kerning pair here, and it crosses a word boundary
        [InlineData("Arial", "Hamburgefonstiv quick brown fox 0123456789 Arial", 20, 456.95)]
        // "rg" and "Ti" inside words, " T" across one
        [InlineData("Times New Roman", "Hamburgefonstiv quick brown fox 0123456789 Times New Roman", 20, 541.27)]
        [InlineData("Verdana", "Hamburgefonstiv quick brown fox 0123456789 Verdana", 20, 562.3767)]
        public void WidthIsTheKernedWidth(string family, string text, double size, double stockWidth)
        {
            var typeface = new Typeface(new FontFamily(family), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            Assert.SkipUnless(typeface.TryGetGlyphTypeface(out GlyphTypeface? gt)
                              && System.Linq.Enumerable.Contains(gt!.Win32FamilyNames.Values, family),
                              $"{family} is not installed");

            var formatted = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                                              typeface, size, Brushes.Black, 1.0);

            Assert.Equal(stockWidth, formatted.WidthIncludingTrailingWhitespace, 3);
        }
    }
}
