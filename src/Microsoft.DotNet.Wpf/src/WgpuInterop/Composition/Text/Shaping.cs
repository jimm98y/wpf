// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The text-shaping seam. Shaping turns a string into a sequence of *positioned
// glyphs* -- the HarfBuzz model (glyph infos + glyph positions). Separating it
// from rendering lets the renderer consume glyph ids + advances + offsets
// without assuming one character maps to one glyph at a fixed advance, which is
// what real shaping (kerning, ligatures, mark positioning, contextual forms,
// bidi) requires.
//
// Implemented here: the seam, a pass-through shaper, and a kerning shaper. More
// complex shaping (OpenType GSUB/GPOS, HarfBuzz) plugs in behind ITextShaper.
//

using System.Collections.Generic;

namespace Microsoft.Wpf.Interop.WebGpu.Composition.Text
{
    /// <summary>A shaped glyph: which glyph, how far to advance, and its offset.</summary>
    internal readonly struct ShapedGlyph
    {
        public readonly int GlyphId;
        public readonly float Advance;   // x advance, base pixels
        public readonly float XOffset;   // placement offset, base pixels
        public readonly float YOffset;

        /// <summary>The pair adjustment to the space AFTER this glyph, in base pixels.
        /// <para>Carried SEPARATELY from Advance, not folded into it, because a caller drawing at a
        /// device size does not use Advance at all: it asks the face for the advance that size was
        /// fitted to ('hdmx', or the hinted span) and steps by that. A kern folded into Advance is
        /// silently dropped by every one of those callers, which is what happened when the kerning
        /// shaper was first written and why no caller ever used it.</para></summary>
        public readonly float Kern;

        public ShapedGlyph(int glyphId, float advance, float xOffset = 0f, float yOffset = 0f,
                           float kern = 0f)
        {
            GlyphId = glyphId; Advance = advance; XOffset = xOffset; YOffset = yOffset; Kern = kern;
        }
    }


    /// <summary>Which way a run reads, and the glyph order that follows from it.
    /// <para>The renderer steps its pen left to right, which is the whole story for Latin and the
    /// wrong one for Hebrew and Arabic: their words read right to left, so a run laid out in
    /// LOGICAL order comes out mirrored. WPF's own text path already knows this -- a glyph run
    /// arrives with a BidiLevel and MilcoreEngine walks the odd ones backwards -- but a caller that
    /// hands the renderer a STRING (WinForms' DrawString, and this port's own parity harness) has
    /// no such level, and GDI does the reordering inside ExtTextOutW. Hebrew measured ink
    /// identical to GDI's with every glyph in the wrong column.</para>
    /// <para>This is the Unicode algorithm's first two levels and not the whole of it: strong
    /// characters give a run its direction, neutrals take the direction of what surrounds them
    /// (the paragraph's at either end), digits stay left-to-right inside a right-to-left run, and
    /// the runs are then emitted in the paragraph's order. Explicit embedding controls, isolates
    /// and mirrored brackets are not here. It is applied only where shaping kept one glyph per
    /// character, since a reordering shaper (Indic) has already placed its glyphs.</para></summary>
    internal static class BidiOrder
    {
        private enum Dir { Neutral, Left, Right, Digit }

        /// <summary>Puts <paramref name="glyphs"/> into visual order for <paramref name="text"/>.
        /// </summary>
        public static void ToVisual(string text, List<ShapedGlyph> glyphs)
        {
            if (s_off || glyphs.Count != text.Length || text.Length < 2) return;

            var dirs = new Dir[text.Length];
            bool anyRight = false;
            for (int i = 0; i < text.Length; i++)
            {
                dirs[i] = Classify(text[i]);
                if (dirs[i] == Dir.Right) anyRight = true;
            }
            if (!anyRight) return;                 // nothing reads backwards

            // The paragraph's direction is its first strong character's.
            bool paragraphRtl = false;
            foreach (Dir d in dirs)
            {
                if (d == Dir.Left) { paragraphRtl = false; break; }
                if (d == Dir.Right) { paragraphRtl = true; break; }
            }

            // A neutral belongs to the run around it when both sides agree, and to the paragraph
            // otherwise; a digit is a left-to-right island whichever run it sits in.
            var resolved = new bool[text.Length];  // true = right to left
            for (int i = 0; i < text.Length; i++)
            {
                if (dirs[i] == Dir.Right) { resolved[i] = true; continue; }
                if (dirs[i] == Dir.Left || dirs[i] == Dir.Digit) { resolved[i] = false; continue; }
                int before = -1, after = -1;
                for (int j = i - 1; j >= 0; j--)
                    if (dirs[j] == Dir.Left || dirs[j] == Dir.Right) { before = j; break; }
                for (int j = i + 1; j < text.Length; j++)
                    if (dirs[j] == Dir.Left || dirs[j] == Dir.Right) { after = j; break; }
                bool leftSide = before >= 0 ? dirs[before] == Dir.Right : paragraphRtl;
                bool rightSide = after >= 0 ? dirs[after] == Dir.Right : paragraphRtl;
                resolved[i] = leftSide && rightSide ? leftSide : paragraphRtl;
            }

            // Maximal runs of one direction, emitted in the paragraph's order, each right-to-left
            // run reversed within itself.
            var runs = new List<(int Start, int End, bool Rtl)>();
            int at = 0;
            while (at < text.Length)
            {
                int end = at;
                while (end + 1 < text.Length && resolved[end + 1] == resolved[at]) end++;
                runs.Add((at, end, resolved[at]));
                at = end + 1;
            }

            var order = new List<int>(text.Length);
            for (int r = 0; r < runs.Count; r++)
            {
                (int start, int end, bool rtl) = runs[paragraphRtl ? runs.Count - 1 - r : r];
                if (rtl) for (int i = end; i >= start; i--) order.Add(i);
                else for (int i = start; i <= end; i++) order.Add(i);
            }

            var reordered = new ShapedGlyph[order.Count];
            for (int i = 0; i < order.Count; i++) reordered[i] = glyphs[order[i]];
            glyphs.Clear();
            glyphs.AddRange(reordered);
        }

        /// <summary>WPF_BIDI=0 lays every run out in logical order, as this used to.</summary>
        private static readonly bool s_off =
            System.Environment.GetEnvironmentVariable("WPF_BIDI") == "0";

        private static Dir Classify(char c)
        {
            // Hebrew, Arabic, Syriac, Thaana, N'Ko and the Arabic presentation blocks read right to
            // left; the digits among them do not.
            if (c >= '\u0590' && c <= '\u08FF') return c >= '\u0660' && c <= '\u0669'
                                                    || c >= '\u06F0' && c <= '\u06F9' ? Dir.Digit : Dir.Right;
            if (c >= '\uFB1D' && c <= '\uFDFF') return Dir.Right;
            if (c >= '\uFE70' && c <= '\uFEFC') return Dir.Right;
            if (c >= '0' && c <= '9') return Dir.Digit;
            if (char.IsLetter(c)) return Dir.Left;
            return Dir.Neutral;
        }
    }

    /// <summary>Font data a shaper needs: char-to-glyph, advances and kerning.</summary>
    internal interface IShapingFont
    {
        /// <summary>Maps a character to a glyph index (0 = .notdef / unmapped).</summary>
        int GlyphIndex(char c);

        /// <summary>The glyph's default horizontal advance, in base pixels.</summary>
        float Advance(int glyphId);

        /// <summary>Kerning adjustment (base pixels, usually negative) between a pair.</summary>
        bool TryGetKerning(int leftGlyph, int rightGlyph, out float kerning);
    }

    /// <summary>A font that can both shape text and rasterize glyphs.</summary>
    internal interface IFont : IGlyphSource, IShapingFont
    {
    }

    internal interface ITextShaper
    {
        void Shape(IShapingFont font, string text, List<ShapedGlyph> output);
    }

    /// <summary>Pass-through shaper: one glyph per character at its default advance.</summary>
    internal sealed class SimpleTextShaper : ITextShaper
    {
        public void Shape(IShapingFont font, string text, List<ShapedGlyph> output)
        {
            output.Clear();
            foreach (char c in text)
            {
                int gid = font.GlyphIndex(c);
                output.Add(new ShapedGlyph(gid, font.Advance(gid)));
            }
        }
    }

    /// <summary>Adds pairwise kerning on top of the simple mapping.
    /// <para>WINDOWS KERNS A STRING RUN, and until this was wired up we did not. Arial's line of
    /// 'AVAVAV...' came out 140 pixels wide against Windows' 121 -- a whole pixel per pair, all of
    /// it accumulating -- and one kern pair in the middle of a line puts every glyph after it on
    /// the wrong column. The specimen line hid it behind a single pair, (space, A), which is why
    /// it read as "Arial's capitals are misplaced" for so long.</para></summary>
    internal sealed class KerningTextShaper : ITextShaper
    {
        private static readonly bool s_off =
            System.Environment.GetEnvironmentVariable("WPF_KERN") == "0";

        public void Shape(IShapingFont font, string text, List<ShapedGlyph> output)
        {
            output.Clear();
            foreach (char c in text)
            {
                int gid = font.GlyphIndex(c);
                output.Add(new ShapedGlyph(gid, font.Advance(gid)));
            }

            if (s_off) return;      // WPF_KERN=0, to measure what kerning is worth region by region
            for (int i = 0; i < output.Count - 1; i++)
            {
                if (font.TryGetKerning(output[i].GlyphId, output[i + 1].GlyphId, out float k))
                {
                    ShapedGlyph g = output[i];
                    output[i] = new ShapedGlyph(g.GlyphId, g.Advance, g.XOffset, g.YOffset, k);
                }
            }
        }
    }
}
