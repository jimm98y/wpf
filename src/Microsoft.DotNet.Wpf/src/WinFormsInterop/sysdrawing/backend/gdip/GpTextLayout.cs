// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// GDI+'s device-independent text layout (FullTextImager, the Line Services path), as MeasureString
// and GraphicsPath.AddString see it. Measured against gdiplus.dll (native arm64) rather than read
// instruction by instruction -- the Line Services callbacks are many -- over four faces, three sizes,
// six strings, default and typographic formats, with and without a wrapping width (288 measurements
// and 30 paths, all exact):
//
//   ideal units      2048 to the em (GlyphImager::Initialize's ideal resolution); the default
//                    format's margins are floor(2048 / 6) = 341 of them each side
//   advances         the design advance, tracked by 1.03 and rounded (floor(a * 1.03 + 0.5)) for the
//                    default format, untracked for GenericTypographic; the face's GPOS 'kern' pairs
//                    added (DirectWrite's shaping)
//   lines            broken after spaces, greedily, when the line without its trailing spaces and
//                    the margins would pass the layout width; '\n' ends a line
//   MeasureString    width = the widest line without trailing spaces + both margins (em / 6 each
//                    for one line, 341 ideal units each for more); height = cell ascent + descent
//                    for one line, lines x line spacing for more, + em / 8 with the default format
//   AddString        each glyph's outline at the design em (GetGlyphPath), quadratics raised to
//                    cubics with DirectWrite's 2/3 (0x3f2aaaaa) and 1/3, scaled by em / upem and
//                    offset to its ideal origin (GpPath::AddGlyphPath); the baseline at the cell
//                    ascent, each further line a line spacing down; centring floors in ideal units
//                    against lines x line spacing + em / 8
//

using System.Collections.Generic;
using Microsoft.Wpf.Interop.WebGpu.Composition.Text;

namespace System.Drawing.WebGpuBackend.Gdip
{
    internal sealed class GpTextLayout
    {
        public const int Ideal = 2048;
        public const int IdealMargin = Ideal / 6;   // 341

        public sealed class Line
        {
            public int Start, Length;             // characters, trailing spaces included
            public readonly List<int> Glyphs = new List<int> ();
            public readonly List<int> Chars = new List<int> ();   // the character each glyph is
            public readonly List<int> X = new List<int> ();       // ideal units from the line start
            public int Width;                     // ideal units, trailing spaces excluded
            public int WidthWithSpaces;
        }

        public readonly List<Line> Lines = new List<Line> ();
        public TrueTypeFont Font;
        public float Em;
        public bool Typographic;
        public int Upem, Ascent, Descent, LineSpacing;

        /// <summary>The margin each side in pixels as MeasureString reports it.</summary>
        public float MarginPx => Typographic ? 0f : Lines.Count > 1 ? (float) (IdealMargin * (double) Em / Ideal) : Em / 6f;

        public static GpTextLayout Build (TrueTypeFont font, GpFontFamily.Metrics m, string text, float em, float width,
                                          int flags, bool typographic, bool hotkey)
        {
            var L = new GpTextLayout { Font = font, Em = em, Typographic = typographic, Upem = m.Em, Ascent = m.Ascent,
                                       Descent = m.Descent, LineSpacing = m.LineSpacing, TextLength = text.Length };
            bool noWrap = (flags & 0x1000) != 0 || !(width > 0f);
            float avail = width - (typographic ? 0f : 2f * em / 6f);
            GposTable gpos = font.Gpos;
            // A kerning pair is tracked like an advance.
            int Kern (int ku)
            {
                int kv = typographic ? ku : (int) Math.Floor (ku * 1.03 + 0.5);
                return m.Em == Ideal ? kv : (int) Math.Round (kv * (double) Ideal / m.Em);
            }
            string script = gpos == null ? null : gpos.HasFeature ("latn", "kern") ? "latn" : gpos.HasFeature ("DFLT", "kern") ? "DFLT" : null;

            int Advance (int gid)
            {
                int a = font.DesignAdvance (gid);
                if (!typographic) a = (int) Math.Floor (a * 1.03 + 0.5);
                return m.Em == Ideal ? a : (int) Math.Round (a * (double) Ideal / m.Em);
            }

            var line = new Line ();
            int i = 0, n = text.Length;
            // The line being built is kept as tokens (a word and the spaces after it).
            while (i <= n) {
                if (i == n || text [i] == '\n') {
                    Finish (line, text, i);
                    L.Lines.Add (line);
                    if (i == n) break;
                    i++;
                    line = new Line { Start = i };
                    continue;
                }
                // The next token: up to and including the spaces after a word.
                int t0 = i;
                while (i < n && text [i] != '\n' && text [i] != ' ') i++;
                while (i < n && text [i] == ' ') i++;
                var tg = new List<int> (); var tc = new List<int> ();
                for (int k = t0; k < i; k++) {
                    char c = text [k];
                    if (c == '\r' || (hotkey && c == '&')) continue;
                    tg.Add (font.GlyphIndex (c)); tc.Add (k);
                }
                int startX = line.WidthWithSpaces;
                int prev = line.Glyphs.Count > 0 ? line.Glyphs [line.Glyphs.Count - 1] : -1;
                // Width of the line with this token, its trailing spaces excluded.
                int x = startX, wNoSp = line.Width, kernFirst = 0;
                var xs = new List<int> ();
                for (int k = 0; k < tg.Count; k++) {
                    int p = k == 0 ? prev : tg [k - 1];
                    if (p >= 0 && script != null && gpos.TryPairAdjustment (script, "kern", p, tg [k], out int ku) && ku != 0) {
                        int kv = Kern (ku);
                        x += kv;
                        if (k == 0) kernFirst = kv;
                    }
                    xs.Add (x);
                    x += Advance (tg [k]);
                    if (text [tc [k]] != ' ') wNoSp = x;
                }
                bool overflow = !noWrap && line.Glyphs.Count > 0 && wNoSp * (double) em / Ideal > avail;
                if (overflow) {
                    Finish (line, text, t0);
                    L.Lines.Add (line);
                    line = new Line { Start = t0 };
                    i = t0;
                    // Re-run the token at the start of the new line.
                    x = 0; wNoSp = 0; xs.Clear ();
                    for (int k = 0; k < tg.Count; k++) {
                        if (k > 0 && script != null && gpos.TryPairAdjustment (script, "kern", tg [k - 1], tg [k], out int ku) && ku != 0)
                            x += Kern (ku);
                        xs.Add (x);
                        x += Advance (tg [k]);
                        if (text [tc [k]] != ' ') wNoSp = x;
                    }
                    while (i < n && text [i] != '\n' && text [i] != ' ') i++;
                    while (i < n && text [i] == ' ') i++;
                }
                line.Glyphs.AddRange (tg); line.Chars.AddRange (tc); line.X.AddRange (xs);
                line.WidthWithSpaces = x;
                line.Width = wNoSp;
            }
            return L;

            static void Finish (Line ln, string s, int end) => ln.Length = end - ln.Start;
        }

        // ---- MeasureString ---------------------------------------------------------------------

        /// <summary>The size MeasureString answers (the lines visible in <paramref name="height"/>
        /// when it is positive).</summary>
        public SizeF Measure (float height, int flags, out int chars, out int lines)
        {
            int count = Lines.Count;
            float lsPx = (float) (LineSpacing * (double) Em / Upem);
            if (height > 0f && count > 1) {
                bool lineLimit = (flags & 0x2000) != 0;
                int fit = 0;
                for (int k = 0; k < count; k++) {
                    float top = k * lsPx, bottom = (k + 1) * lsPx;
                    if (lineLimit ? bottom <= height : top < height) fit = k + 1;
                }
                count = Math.Max (1, fit);
            }
            bool trailing = (flags & 0x800) != 0;
            int widest = 0;
            for (int k = 0; k < count; k++)
                widest = Math.Max (widest, trailing ? Lines [k].WidthWithSpaces : Lines [k].Width);
            chars = count == Lines.Count ? TextLength : Lines [count - 1].Start + Lines [count - 1].Length;
            lines = count;
            float W, H;
            if (count == 1) {
                // One line: pixels from design units, the em / 6 margins added in float.
                float lm = Typographic ? 0f : Em / 6f;
                float w = (float) (widest * (double) Em / Ideal);
                W = widest == 0 && Lines [0].Glyphs.Count == 0 ? 0f : (w + lm) + lm;
                H = (float) ((Ascent + Descent) * (double) Em / Upem);
                if (!Typographic) H += Em / 8f;
            } else {
                // More: ideal units divided by the ideal resolution (2048 / em, a float).
                float ipp = Ideal / Em;
                W = (widest + (Typographic ? 0 : 2 * IdealMargin)) / ipp;
                int ls = Upem == Ideal ? LineSpacing : (int) Math.Round (LineSpacing * (double) Ideal / Upem);
                H = count * ls / ipp;
                if (!Typographic) H += Em / 8f;
            }
            if (Environment.GetEnvironmentVariable ("WF_GPMEAS_TRACE") == "1")
                Console.Error.WriteLine ($"GPMEAS lines={count} widest={widest} W={W:R} H={H:R} em={Em:R}");
            return new SizeF (W, H);
        }

        public int TextLength;
    }
}
