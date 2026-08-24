// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The minimum TextFormatter needs to format a line: a run-properties, a paragraph-properties and a
// text source. WPF's text APIs are abstract about where text comes from, so there is no built-in
// "just format this string" entry point -- every caller supplies these three, and so do we.
//
// Formatting is the level these tests work at deliberately. It is where font fallback and line
// breaking actually happen, and it needs no window, no dispatcher and no GPU, so the same tests run
// on every platform and in CI.
//

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.TextFormatting;

namespace Wpf.Text.Tests
{
    internal sealed class RunProperties : TextRunProperties
    {
        private readonly Typeface _typeface;
        private readonly double _size;
        private readonly CultureInfo _culture;

        public RunProperties(string family, double size, CultureInfo? culture = null)
        {
            _typeface = new Typeface(family);
            _size = size;
            _culture = culture ?? CultureInfo.InvariantCulture;
        }

        public override Typeface Typeface => _typeface;
        public override double FontRenderingEmSize => _size;
        public override double FontHintingEmSize => _size;
        public override TextDecorationCollection? TextDecorations => null;
        public override Brush ForegroundBrush => Brushes.Black;
        public override Brush? BackgroundBrush => null;
        // The run's language is what a composite font's FamilyMap selects its target by, so this is
        // how a caller says "this is Japanese" and gets Japanese Han forms rather than Chinese ones.
        public override CultureInfo CultureInfo => _culture;
        public override TextEffectCollection? TextEffects => null;
    }

    internal sealed class ParagraphProperties : TextParagraphProperties
    {
        private readonly TextRunProperties _defaults;

        public ParagraphProperties(TextRunProperties defaults) { _defaults = defaults; }

        public override FlowDirection FlowDirection => FlowDirection.LeftToRight;
        public override TextAlignment TextAlignment => TextAlignment.Left;
        public override double LineHeight => 0;
        public override bool FirstLineInParagraph => true;
        public override TextRunProperties DefaultTextRunProperties => _defaults;
        public override TextWrapping TextWrapping => TextWrapping.Wrap;
        public override TextMarkerProperties? TextMarkerProperties => null;
        public override double Indent => 0;
    }

    internal sealed class StringTextSource : TextSource
    {
        private readonly string _text;
        private readonly TextRunProperties _properties;

        public StringTextSource(string text, TextRunProperties properties)
        {
            _text = text;
            _properties = properties;
        }

        public override TextRun GetTextRun(int index)
            => index >= _text.Length
                ? new TextEndOfParagraph(1)
                : new TextCharacters(_text, index, _text.Length - index, _properties);

        public override TextSpan<CultureSpecificCharacterBufferRange> GetPrecedingText(int index)
            => new TextSpan<CultureSpecificCharacterBufferRange>(
                0, new CultureSpecificCharacterBufferRange(null, CharacterBufferRange.Empty));

        public override int GetTextEffectCharacterIndexFromTextSourceCharacterIndex(int index) => index;
    }

    /// <summary>What formatting a string produced: which fonts it landed in, and how many of the
    /// glyphs came out as the missing-glyph box.</summary>
    internal sealed record FormattedRun(
        IReadOnlyList<string> Fonts,
        IReadOnlyList<string> Families,
        int GlyphCount,
        int NotdefCount,
        double Width)
    {
        public bool AllGlyphsPresent => GlyphCount > 0 && NotdefCount == 0;
    }

    internal static class TextHarness
    {
        /// <summary>
        /// Formats one line and reports the glyph runs behind it. Glyph id 0 is .notdef -- the box a
        /// font draws for a character it does not have -- so a notdef count is exactly the "renders
        /// as tofu" measure these tests are about.
        /// </summary>
        public static FormattedRun Format(string text, string fontFamily = "Segoe UI", double emSize = 16,
                                          double width = 1000, CultureInfo? culture = null)
        {
            var runProperties = new RunProperties(fontFamily, emSize, culture);
            var paragraphProperties = new ParagraphProperties(runProperties);

            TextFormatter formatter = TextFormatter.Create();
            using TextLine line = formatter.FormatLine(
                new StringTextSource(text, runProperties), 0, width, paragraphProperties, null);

            var fonts = new List<string>();
            var families = new List<string>();
            int glyphs = 0, notdef = 0;

            var visual = new DrawingVisual();
            using (DrawingContext context = visual.RenderOpen())
            {
                line.Draw(context, new Point(0, 0), InvertAxes.None);
            }

            Walk(visual.Drawing, fonts, families, ref glyphs, ref notdef);
            return new FormattedRun(fonts, families, glyphs, notdef, line.Width);
        }

        /// <summary>Formats a paragraph into lines of at most <paramref name="width"/>.</summary>
        public static List<(string Text, double Width)> Wrap(string text, string fontFamily, double emSize, double width)
        {
            var runProperties = new RunProperties(fontFamily, emSize);
            var paragraphProperties = new ParagraphProperties(runProperties);
            var source = new StringTextSource(text, runProperties);
            TextFormatter formatter = TextFormatter.Create();

            var lines = new List<(string, double)>();
            int position = 0;

            // The guard is not paranoia: a breaker that fails to advance would otherwise hang the
            // test run rather than fail it.
            while (position < text.Length && lines.Count < 100)
            {
                using TextLine line = formatter.FormatLine(source, position, width, paragraphProperties, null);
                if (line.Length <= 0) break;

                lines.Add((text.Substring(position, Math.Min(line.Length, text.Length - position)), line.Width));
                position += line.Length;
            }

            return lines;
        }

        /// <summary>
        /// True if any installed font has a glyph for the codepoint, so a test can skip on a machine
        /// that simply has no font for the script instead of failing.
        ///
        /// Asks the fonts directly rather than formatting the text: formatting is what these tests
        /// are checking, and a check built on it would turn "fallback is broken" into "skipped".
        /// </summary>
        public static bool AnyInstalledFontCovers(int codepoint)
        {
            if (s_coverage.TryGetValue(codepoint, out bool covered)) return covered;

            covered = false;
            foreach (FontFamily family in Fonts.SystemFontFamilies)
            {
                foreach (Typeface typeface in family.GetTypefaces())
                {
                    if (typeface.TryGetGlyphTypeface(out GlyphTypeface? glyphTypeface) &&
                        glyphTypeface is not null &&
                        glyphTypeface.CharacterToGlyphMap.ContainsKey(codepoint))
                    {
                        covered = true;
                        break;
                    }
                }

                if (covered) break;
            }

            s_coverage[codepoint] = covered;
            return covered;
        }

        // Enumerating every installed face is slow enough to be worth doing once per codepoint.
        private static readonly Dictionary<int, bool> s_coverage = new();

        /// <summary>True if a family of this exact name is installed.</summary>
        public static bool FamilyInstalled(string name)
        {
            foreach (FontFamily family in Fonts.SystemFontFamilies)
            {
                if (string.Equals(family.Source, name, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        /// <summary>True if any one of these families is installed.</summary>
        public static bool AnyFamilyInstalled(params string[] names)
        {
            foreach (string name in names)
            {
                if (FamilyInstalled(name)) return true;
            }
            return false;
        }

        // The families a machine has to have before "Japanese and Chinese pick their own" is a
        // question worth asking: something Japanese AND something Simplified Chinese, under whatever
        // names this platform ships them. Naming only the Noto pair skipped the test everywhere but
        // Linux -- macOS has had Hiragino and Heiti/Songti all along, and the behaviour under test
        // works there, so it was going untested on the one platform where the fallback chain reaches
        // these families by a completely different route (system fonts, not app-deployed Noto).
        public static readonly string[] JapaneseFamilies =
        {
            "Noto Sans CJK JP", "Noto Sans JP", "Source Han Sans JP",  // Linux / Android / app-deployed
            "Hiragino Sans", "Hiragino Kaku Gothic ProN", "Hiragino Mincho ProN",  // macOS
            "Yu Gothic UI", "Yu Gothic", "MS Gothic", "Meiryo",        // Windows
        };

        public static readonly string[] SimplifiedChineseFamilies =
        {
            "Noto Sans CJK SC", "Noto Sans SC", "Source Han Sans SC",  // Linux / Android / app-deployed
            "PingFang SC", "Heiti SC", "Songti SC", "Hiragino Sans GB",  // macOS
            "Microsoft YaHei UI", "Microsoft YaHei", "SimSun",         // Windows
        };

        private static string FamilyNameOf(GlyphTypeface? glyphTypeface)
        {
            if (glyphTypeface is null) return "(none)";

            foreach (KeyValuePair<CultureInfo, string> name in glyphTypeface.FamilyNames)
            {
                return name.Value;
            }
            return "(unnamed)";
        }

        private static void Walk(Drawing? drawing, List<string> fonts, List<string> families, ref int glyphs, ref int notdef)
        {
            if (drawing is DrawingGroup group)
            {
                foreach (Drawing child in group.Children)
                {
                    Walk(child, fonts, families, ref glyphs, ref notdef);
                }
            }
            else if (drawing is GlyphRunDrawing glyphDrawing && glyphDrawing.GlyphRun is GlyphRun run)
            {
                string file = run.GlyphTypeface?.FontUri?.LocalPath ?? "(none)";
                if (!fonts.Contains(file)) fonts.Add(file);

                // The FILE does not identify the face: the pan-CJK collections put the Japanese,
                // Korean and both Chinese families in one .ttc, so locale fidelity is only visible
                // in the family name.
                string family = FamilyNameOf(run.GlyphTypeface);
                if (!families.Contains(family)) families.Add(family);

                foreach (ushort glyph in run.GlyphIndices)
                {
                    glyphs++;
                    if (glyph == 0) notdef++;
                }
            }
        }
    }
}
