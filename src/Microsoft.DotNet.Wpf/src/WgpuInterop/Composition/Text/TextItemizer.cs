// A line of text as GDI draws it: in runs, each in one direction and one face, laid out in visual order.
//
// GDI (Uniscribe under ExtTextOut and DrawText) does two things to a string before it draws a glyph. It runs
// the Unicode bidi algorithm over it -- a left-to-right paragraph unless DT_RTLREADING says otherwise -- so
// Hebrew and Arabic read right to left and the runs come out in visual order. And it draws each character
// from a face that has it: the one asked for, or a linked one, character by character. A string run here was
// one face and one direction, so "Hello שלום 123" in Segoe UI drew the Latin and left the Hebrew blank (the
// face covers some of the run, so no link was looked for), in logical order.
//
// The levels come from GDI+'s own bidi analyzer (GpBidi, the port of UnicodeBidiAnalyze); runs break where
// the level or the face changes; whitespace and punctuation stay in the face of the text before them when it
// has them, so a Hebrew sentence is one run and not a run per word; and the runs are put in visual order by
// the algorithm's rule L2 (from the highest level down, every sequence at that level or above reversed).

using System;
using System.Collections.Generic;
using System.Drawing.WebGpuBackend.Gdip;

namespace Microsoft.Wpf.Interop.WebGpu.Composition.Text
{
    internal readonly struct TextSegment
    {
        public TextSegment(int start, int length, bool rightToLeft, IFont font)
        {
            Start = start; Length = length; RightToLeft = rightToLeft; Font = font;
        }

        /// <summary>The segment's characters, in logical order: <c>text.Substring(Start, Length)</c>.</summary>
        public int Start { get; }
        public int Length { get; }

        /// <summary>The segment reads right to left: its glyphs are laid out last character first.</summary>
        public bool RightToLeft { get; }

        /// <summary>The face its characters are drawn from.</summary>
        public IFont Font { get; }
    }

    internal static class TextItemizer
    {
        /// <summary>The segments of <paramref name="text"/> (one line), in visual order, or null when it is a single
        /// left-to-right run in <paramref name="primary"/> -- which is nearly every string, and drawn as before.
        /// <paramref name="link"/> answers a face for a character <paramref name="primary"/> lacks, or null.</summary>
        public static List<TextSegment>? Itemize(string text, bool rightToLeftParagraph, IFont primary, Func<char, IFont?> link)
        {
            if (string.IsNullOrEmpty(text) || primary is not IShapingFont shaping) return null;
            bool bidi = rightToLeftParagraph;
            bool uncovered = false;
            foreach (char c in text)
            {
                if (c >= '֐' && c <= 'ࣿ' || c >= 'יִ' && c <= 'ﻼ' || c >= '‎' && c <= '‏'
                    || c >= '‪' && c <= '‮' || c >= '⁦' && c <= '⁩')
                    bidi = true;
                if (!uncovered && !char.IsWhiteSpace(c) && !char.IsControl(c) && !char.IsSurrogate(c) && shaping.GlyphIndex(c) <= 0)
                    uncovered = true;
            }
            if (!bidi && !uncovered) return null;

            int n = text.Length;
            byte[] levels;
            if (bidi)
            {
                var flags = rightToLeftParagraph ? GpBidi.Flags.DirectionRightToLeft : GpBidi.Flags.DirectionLeftToRight;
                if (!GpBidi.Analyze(text.ToCharArray(), n, n, flags, null, out levels, out _))
                    levels = new byte[n];
            }
            else
                levels = new byte[n];

            // the face of each character
            var faces = new IFont[n];
            var cache = new Dictionary<char, IFont?>();
            for (int i = 0; i < n; i++)
            {
                char c = text[i];
                bool neutral = char.IsWhiteSpace(c) || char.IsControl(c) || char.IsPunctuation(c);
                IFont before = i > 0 ? faces[i - 1] : primary;
                if (neutral && i > 0 && levels[i] == levels[i - 1] && Covers(before, c))
                    faces[i] = before;      // a space or a comma stays with the text it follows, in its direction
                else if (char.IsSurrogate(c) || Covers(primary, c) || char.IsWhiteSpace(c) || char.IsControl(c))
                    faces[i] = primary;
                else
                {
                    if (!cache.TryGetValue(c, out IFont? linked))
                        cache[c] = linked = link(c);
                    faces[i] = linked ?? primary;
                }
            }

            // logical runs: one level, one face
            var runs = new List<(int Start, int Length, byte Level, IFont Font)>();
            for (int i = 0; i < n;)
            {
                int j = i + 1;
                while (j < n && levels[j] == levels[i] && ReferenceEquals(faces[j], faces[i])) j++;
                runs.Add((i, j - i, levels[i], faces[i]));
                i = j;
            }
            if (runs.Count == 1 && (runs[0].Level & 1) == 0 && ReferenceEquals(runs[0].Font, primary)) return null;

            // into visual order (L2)
            int max = 0, minOdd = int.MaxValue;
            foreach (var r in runs)
            {
                max = Math.Max(max, r.Level);
                if ((r.Level & 1) != 0) minOdd = Math.Min(minOdd, r.Level);
            }
            for (int level = max; level >= minOdd && level > 0; level--)
                for (int i = 0; i < runs.Count;)
                {
                    if (runs[i].Level < level) { i++; continue; }
                    int j = i;
                    while (j + 1 < runs.Count && runs[j + 1].Level >= level) j++;
                    runs.Reverse(i, j - i + 1);
                    i = j + 1;
                }

            var segments = new List<TextSegment>(runs.Count);
            foreach (var r in runs)
                segments.Add(new TextSegment(r.Start, r.Length, (r.Level & 1) != 0, r.Font));
            return segments;
        }

        private static bool Covers(IFont font, char c) => font is IShapingFont s && s.GlyphIndex(c) > 0;

        /// <summary>Whether the text has Arabic letters, which join: shaped by the OpenType shaper's Arabic
        /// features whatever kerning the run asked for, as Uniscribe shapes them under every GDI call.</summary>
        public static bool Joins(string text)
        {
            foreach (char c in text)
                if (c >= '\u0600' && c <= '\u06FF' || c >= '\u0750' && c <= '\u077F' || c >= '\u08A0' && c <= '\u08FF'
                    || c >= '\uFB50' && c <= '\uFDFF' || c >= '\uFE70' && c <= '\uFEFF')
                    return true;
            return false;
        }

        internal static readonly ITextShaper JoiningShaper = new OpenTypeTextShaper(kern: true);
        internal static readonly ITextShaper JoiningPlainShaper = new OpenTypeTextShaper(kern: false);
    }
}
