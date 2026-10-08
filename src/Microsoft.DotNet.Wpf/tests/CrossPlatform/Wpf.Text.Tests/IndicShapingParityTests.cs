// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Indic syllables as stock WPF (DirectWrite's Indic engine) shapes them. The managed shaper applied
// the whole feature set in logical order, so a pre-base matra stayed after its consonant -- "hindi"
// drew its i-sign on the wrong side -- and the reph stayed in front. Now, per syllable: the basic
// forms, then the reorder (a reph to the end of the syllable, before the syllable modifiers; a
// pre-base consonant form and a pre-base matra -- the first part of a two-part vowel too -- to the
// front), then the presentation forms ('init' on a word-initial syllable); the syllable is one
// cluster. Tamil's pulli ends a syllable instead of binding the next consonant. Bengali's reph
// precedes a post-base matra, Devanagari's follows it; Malayalam's pre-base ra goes before the
// consonant it follows, not the syllable; Kannada's ii is its i-sign and length mark.
//
// Every expected value is stock WPF's (Microsoft.WindowsDesktop.App 10, Windows 11, Ideal mode),
// glyph indices of Nirmala UI and caret positions in ideal units (1/300 DIP).
//

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.TextFormatting;
using Xunit;

namespace Wpf.Text.Tests
{
    public class IndicShapingParityTests
    {
        [Theory]
        [InlineData("हिन्दी", "302 286 573 305", new[] { 0, 4137, 4137, 9779, 9779, 9779, 9779 })]
        [InlineData("र्थि", "302 264 330", new[] { 0, 4306, 4306, 4306, 4306 })]
        [InlineData("र्थं", "264 333", new[] { 0, 3059, 3059, 3059, 3059 })]
        [InlineData("र्थु", "264 306 330", new[] { 0, 3059, 3059, 3059, 3059 })]
        [InlineData("மொழி", "2966 2946 2959 3055", new[] { 0, 12278, 12278, 17774, 17774 })]
        [InlineData("ள்ளை", "2951 2972 2968 2951", new[] { 0, 4898, 4898, 15942, 15942 })]
        [InlineData("কেমন", "941 885 909 904", new[] { 0, 6189, 6189, 9677, 12984 })]
        [InlineData("പ്രകൃതി", "1618 1472 1451 1497 1466 1491", new[] { 0, 6169, 6169, 6169, 14494, 14494, 21945, 21945 })]
        [InlineData("\u09B0\u09CD\u09AC\u09BE", "907 954 922", new[] { 0, 4125, 4125, 4125, 4125 })]
        [InlineData("\u0D38\u0D4D\u0D24\u0D4D\u0D30\u0D40", "1486 1505 1618 1466 1492", new[] { 0, 15589, 15589, 15589, 15589, 15589, 15589 })]
        [InlineData("\u0CAA\u0CCD\u0CB0\u0CC0", "3498 3187 3144", new[] { 0, 7108, 7108, 7108, 7108 })]
        [InlineData("\u0938\u0930\u094D\u0935\u094B", "285 281 342", new[] { 0, 3457, 7526, 7526, 7526, 7526 })]
        public void SyllablesAreReorderedAsDirectWriteDoes(string text, string glyphs, int[] carets)
        {
            var typeface = new Typeface(new FontFamily("Nirmala UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            Assert.SkipUnless(typeface.TryGetGlyphTypeface(out GlyphTypeface? gt)
                              && gt!.Win32FamilyNames.Values.Contains("Nirmala UI"), "Nirmala UI is not installed");

            var properties = new RunProperties("Nirmala UI", 16);
            using TextLine line = TextFormatter.Create().FormatLine(
                new StringTextSource(text, properties), 0, 10000, new ParagraphProperties(properties), null);

            var visual = new DrawingVisual();
            using (DrawingContext context = visual.RenderOpen())
            {
                line.Draw(context, new Point(0, 0), InvertAxes.None);
            }
            var drawn = new List<ushort>();
            void Walk(Drawing? drawing)
            {
                if (drawing is DrawingGroup group) foreach (Drawing child in group.Children) Walk(child);
                else if (drawing is GlyphRunDrawing run) drawn.AddRange(run.GlyphRun.GlyphIndices);
            }
            Walk(visual.Drawing);

            Assert.Equal(glyphs, string.Join(" ", drawn));
            Assert.Equal(carets, Enumerable.Range(0, text.Length + 1)
                .Select(i => (int)Math.Round(line.GetDistanceFromCharacterHit(new CharacterHit(i, 0)) * 300)).ToArray());
        }
    }
}
