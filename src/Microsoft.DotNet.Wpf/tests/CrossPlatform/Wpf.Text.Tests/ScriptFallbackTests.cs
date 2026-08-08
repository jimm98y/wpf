// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Font fallback for the scripts the app's own font does not cover.
//
// WPF resolves this by walking the FamilyMap Target lists in the .CompositeFont files, which name
// Windows families ("Microsoft YaHei UI", "Yu Gothic UI", "Malgun Gothic", ...). None of those
// exist off Windows, so every one of these tests failed before: Japanese, Chinese and Korean text
// resolved to whatever Latin face the catalog answered with and rendered as a row of missing-glyph
// boxes. The fix has three layers, and these tests hold each of them --
//
//   * the catalog answers honestly, so a family that is NOT installed resolves to null and the walk
//     moves on to the next candidate instead of stopping at a Latin face that cannot draw the text;
//   * the Windows CJK family names are aliased, per locale, onto the open pan-CJK families that
//     Linux, Android and macOS actually ship -- which is what keeps Japanese in Japanese forms
//     rather than merely in something that has the character;
//   * and when the named targets are exhausted, the installed fonts are asked directly, so a script
//     no FamilyMap mentions still finds a font.
//
// The assertions are about GLYPHS, not fonts: which file the text lands in is a machine's business,
// but a glyph id of 0 is .notdef -- the box -- on every font there is.
//

using System.Globalization;
using Xunit;

namespace Wpf.Text.Tests
{
    public class ScriptFallbackTests
    {
        // The families an app actually names. "Segoe UI" is what the Fluent theme uses, and the
        // composite font is what an unstyled control falls back to.
        public static TheoryData<string> UiFamilies => new() { "Segoe UI", "Global User Interface" };

        [Theory]
        [MemberData(nameof(UiFamilies))]
        public void JapaneseResolvesToAFontThatHasIt(string family) => AssertRenders("日本語のテキスト", family);

        [Theory]
        [MemberData(nameof(UiFamilies))]
        public void SimplifiedChineseResolvesToAFontThatHasIt(string family) => AssertRenders("简体中文文本", family);

        [Theory]
        [MemberData(nameof(UiFamilies))]
        public void TraditionalChineseResolvesToAFontThatHasIt(string family) => AssertRenders("繁體中文文字", family);

        [Theory]
        [MemberData(nameof(UiFamilies))]
        public void KoreanResolvesToAFontThatHasIt(string family) => AssertRenders("한국어 텍스트", family);

        [Theory]
        [MemberData(nameof(UiFamilies))]
        public void KanaResolvesToAFontThatHasIt(string family) => AssertRenders("ひらがなカタカナ", family);

        [Theory]
        [MemberData(nameof(UiFamilies))]
        public void FullwidthPunctuationResolvesToAFontThatHasIt(string family) => AssertRenders("「テスト」、句読点。", family);

        [Fact]
        public void MixedLatinAndCjkKeepsBothReadable()
        {
            FormattedRun run = TextHarness.Format("WPF on Linux で 日本語 mixed text");
            Assert.SkipUnless(TextHarness.AnyInstalledFontCovers(0x65E5), "no installed font covers CJK");

            Assert.True(run.AllGlyphsPresent, $"{run.NotdefCount} of {run.GlyphCount} glyphs missing");

            // Two scripts, so at least two fonts: the fallback applies to the CJK span alone rather
            // than dragging the whole line into a CJK face.
            Assert.True(run.Fonts.Count >= 2, $"expected Latin and CJK runs, got [{string.Join(", ", run.Fonts)}]");
        }

        /// <summary>
        /// A family name nothing provides must not swallow the fallback chain. This is the specific
        /// regression: the catalog used to answer every unknown name with a Latin face, which made
        /// the composite font's whole Target list resolve to that one face.
        /// </summary>
        [Fact]
        public void UnknownFamilyStillFallsBackForCjk()
        {
            Assert.SkipUnless(TextHarness.AnyInstalledFontCovers(0x65E5), "no installed font covers CJK");

            FormattedRun run = TextHarness.Format("日本語", "No Such Font Family Exists");
            Assert.True(run.AllGlyphsPresent, $"{run.NotdefCount} of {run.GlyphCount} glyphs missing");
        }

        /// <summary>
        /// Latin text must still land in the Latin font: the fallback work is only allowed to apply
        /// to what the requested family cannot draw.
        /// </summary>
        [Fact]
        public void LatinIsUnaffected()
        {
            FormattedRun run = TextHarness.Format("Hello world");

            Assert.True(run.AllGlyphsPresent);
            Assert.Single(run.Fonts);
        }

        /// <summary>
        /// The coverage backstop: after the composite font's named targets are exhausted, the
        /// installed fonts are asked directly. That is what carries scripts whose FamilyMap names
        /// only Windows fonts -- Thai, Devanagari, emoji -- and it is why the CJK fix is not a
        /// special case bolted on for CJK alone.
        /// </summary>
        [Theory]
        [InlineData("ภาษาไทย")]                      // Thai
        [InlineData("हिन्दी")]                          // Devanagari
        [InlineData("\U0001F600")]                    // emoji
        public void OtherScriptsReachAnInstalledFont(string text)
        {
            Assert.SkipUnless(TextHarness.AnyInstalledFontCovers(char.ConvertToUtf32(text, 0)),
                "no installed font covers this script");

            FormattedRun run = TextHarness.Format(text);
            Assert.True(run.AllGlyphsPresent, $"{run.NotdefCount} of {run.GlyphCount} glyphs missing");
        }

        /// <summary>
        /// Han characters are shared between the languages that use them, but their conventional
        /// SHAPES are not: 直 and 骨 are drawn differently in Japanese, Simplified Chinese and
        /// Traditional Chinese, and a reader notices. The composite fonts pick their target by
        /// language for exactly this reason, so the aliases are per locale too -- mapping every
        /// Windows CJK family onto one pan-CJK font would render Japanese in Chinese forms.
        ///
        /// The check is that the two resolve DIFFERENTLY rather than to any named font, because
        /// which families a machine has is its own business. Note that a shared file proves nothing:
        /// Noto and Source Han ship all four languages in a single .ttc, so this compares the family
        /// the run actually landed in.
        /// </summary>
        [Fact]
        public void JapaneseAndSimplifiedChinesePreferTheirOwnFamilies()
        {
            Assert.SkipUnless(
                TextHarness.AnyFamilyInstalled(TextHarness.JapaneseFamilies) &&
                TextHarness.AnyFamilyInstalled(TextHarness.SimplifiedChineseFamilies),
                "requires locale-specific CJK families to tell apart");

            // The run's language is the input, not a guess from the characters: 漢字 is written the
            // same way in both languages and drawn differently in each, which is the whole point.
            FormattedRun japanese = TextHarness.Format("漢字", culture: new CultureInfo("ja-JP"));
            FormattedRun chinese = TextHarness.Format("汉字", culture: new CultureInfo("zh-CN"));

            Assert.True(japanese.AllGlyphsPresent && chinese.AllGlyphsPresent);
            Assert.NotEqual(japanese.Families, chinese.Families);
        }

        private static void AssertRenders(string text, string family)
        {
            Assert.SkipUnless(TextHarness.AnyInstalledFontCovers(char.ConvertToUtf32(text, 0)),
                "no installed font covers this script");

            FormattedRun run = TextHarness.Format(text, family);

            Assert.True(run.GlyphCount > 0, "no glyphs produced at all");
            Assert.True(run.NotdefCount == 0,
                $"{run.NotdefCount} of {run.GlyphCount} glyphs rendered as .notdef in [{string.Join(", ", run.Fonts)}]");
        }
    }
}
