// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// FontFamily.LineSpacing and Baseline as stock WPF reports them.
//
// They come from DWrite's DWRITE_FONT_METRICS, which for a font without USE_TYPO_METRICS takes the
// box from usWinAscent/usWinDescent and the LINE GAP from how much taller hhea's line is than that
// box. The managed metrics set the gap to zero, so every Arial and Times line was 3% short against
// stock WPF, and a stack of TextBlocks drifted a pixel every few lines. The expected values are the
// ones stock WPF (Microsoft.WindowsDesktop.App 10) reports on Windows 11.
//
// Skips where the font is not installed, like the rest of this suite.
//

using System.Windows.Media;
using Xunit;

namespace Wpf.Text.Tests
{
    public class FontFamilyMetricsTests
    {
        [Theory]
        // family, LineSpacing, Baseline -- stock WPF's values
        [InlineData("Arial", 1.1499, 0.9216)]             // hhea gap 67 of 2048: (1854+434+67), (1854+33.5)
        [InlineData("Times New Roman", 1.1499, 0.9124)]   // hhea gap 87
        [InlineData("Verdana", 1.2153, 1.0054)]           // hhea line == usWin box: no gap
        public void LineSpacingIncludesTheGapHheaAddsToTheWinBox(string family, double lineSpacing, double baseline)
        {
            var ff = new FontFamily(family);
            var typeface = new Typeface(ff, System.Windows.FontStyles.Normal, System.Windows.FontWeights.Normal,
                                        System.Windows.FontStretches.Normal);
            Assert.SkipUnless(typeface.TryGetGlyphTypeface(out GlyphTypeface? gt)
                              && System.Linq.Enumerable.Contains(gt!.Win32FamilyNames.Values, family),
                              $"{family} is not installed");
            Assert.Equal(lineSpacing, ff.LineSpacing, 4);
            Assert.Equal(baseline, ff.Baseline, 4);
        }
    }
}
