// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The part of Line Services (statically linked into gdiplus.dll) the full imager drives, as GDI+
// configures it (ols::ols @1800f2240: one text object and the reversal object; LSTXTCFG @1802b3330:
// tab 0009, end of paragraph CR 000d (deleted) / LF 000a; the breaking table LineBreakBehavior):
//
//   LsCreateLine @180045be8 -> CreateLineCore @18010fa60 -> FiniFormatGeneralCase @1800453f0
//       runs are fetched from the line's first cp and formatted with nominal character widths
//       (FormatRegularCharacters @1801198c0 / GetWidths @180124030) up to the margin plus a slack
//       of (margin / 32) * k, k doubling while the line turns out not to have overflowed once
//       its text has glyph advances (ApplyNominalToIdeal @180120bc0 -> ApplyGlyphsToRange
//       @1800494b8 -> GdipLscbkGetGlyphs / GdipLscbkGetGlyphPositions, at each chunk's end). So
//       the line is decided by the GLYPH advances of the text before the margin -- what this
//       formats with directly.
//   BreakGeneralCase @180113770   TruncateCore finds the character the margin falls in;
//       FindPrevBreakCore -> FindPrevBreakText @18011b8b0 walks back from it: after a run of
//       spaces (TryBreakAtSpace, the spaces hanging past the margin) when the classes either side
//       may break across spaces, between two characters (TryPrevBreakRegular @18011d338) when
//       LineBreakBehavior lets them break directly; with neither, ForceBreakCore @180114ac8 breaks
//       before the character that overflowed (after it, when it is the line's first).
//   HandleTab @180112f90   a tab advances to the first stop past the pen (FullTextImager::GetTabStops:
//       the format's stops, then every increment).
//   LSLINFO   ascent, descent and height the largest of the line's runs (GdipLscbkGetRunTextMetrics
//       @1800f93f0: round(cell ascent * k), round(cell descent * k), round(line spacing * k)),
//       cpLim, and whether the line ended its paragraph (endrEndPara = 2).
//

using System.Collections.Generic;
using Run = System.Drawing.WebGpuBackend.Gdip.GpFullTextImager.Run;

namespace System.Drawing.WebGpuBackend.Gdip
{
    internal static class GpLineServices
    {
        /// <summary>A display dnode: a run's glyphs, a tab, or the paragraph end.</summary>
        internal sealed class Seg
        {
            public int Kind;                 // 0 text, 1 tab, 2 end of paragraph, 3 deleted (CR)
            public Run Run;
            public int Cp, CpLim;
            public int Ur, Width;
            public int G0, GCount;           // the run's glyphs [G0, G0 + GCount)
            public int[] Adv, OffU, OffV;    // per glyph, ideal units
        }

        internal sealed class LsLine
        {
            public readonly List<Seg> Segs = new List<Seg> ();
            public int CpLim;
            public bool EndPara;
            public int UrLim, UrLimWithSpaces;
            public int Ascent, Descent, Height;
        }

        /// <summary>One character as the formatter sees it.</summary>
        struct Ch
        {
            public int Cp;
            public Run Run;
            public int Kind;                  // 0 text, 1 tab, 2 LF (end of paragraph), 3 CR (deleted), 4 end-of-paragraph run
            public bool Space;
            public int Width;                 // its cluster's glyph advances, on the cluster's first character
            public bool ClusterStart;
            public int Glyph;                 // the run's glyph index of its cluster
            public int Brk;                   // its breaking class
        }

        static bool IsSpace (int ch) => ch == ' ';

        public static LsLine CreateLine (GpFullTextImager fti, int cpFirst, int dua, bool charBreaks)
        {
            var line = new LsLine ();
            var chars = new List<Ch> ();
            string text = fti.Text;
            // Collect the line's characters, with glyph advances, until the paragraph ends or the
            // text has passed the margin by a line's worth (the break is always before the margin).
            int cp = cpFirst;
            int ur = 0;
            long limit = (long) dua + 0x40000;
            Run eop = null;
            bool endPara = false;
            while (true) {
                Run run = fti.RunAt (cp);
                if (run == null) break;
                if (run.Kind == 1) { eop = run; endPara = true; break; }
                int from = cp - run.Cp, count = run.Len - from;
                // The glyph advances of the run's part from here (one GetGlyphPositions over it).
                int g0 = run.Shape.ClusterMap [from];
                int ng = run.Shape.Glyphs.Length - g0;
                var adv = new int [ng]; var ou = new int [ng]; var ov = new int [ng];
                if (ng > 0) fti.GlyphPositions (run, g0, ng, adv, ou, ov);
                bool stop = false;
                for (int k = 0; k < count; k++) {
                    int c = text [run.Str + from + k];
                    var ch = new Ch { Cp = cp + k, Run = run, Kind = 0 };
                    int gi = run.Shape.ClusterMap [from + k];
                    ch.Glyph = gi;
                    ch.ClusterStart = k == 0 || run.Shape.ClusterMap [from + k - 1] != gi;
                    if (ch.ClusterStart) {
                        int gEnd = from + k + 1 < run.Len ? run.Shape.ClusterMap [from + k + 1] : run.Shape.Glyphs.Length;
                        // A cluster's glyphs: up to the next cluster's first.
                        int kk = from + k + 1;
                        while (kk < run.Len && run.Shape.ClusterMap [kk] == gi) kk++;
                        gEnd = kk < run.Len ? run.Shape.ClusterMap [kk] : run.Shape.Glyphs.Length;
                        int w = 0;
                        for (int g = gi; g < gEnd; g++) w += adv [g - g0];
                        ch.Width = w;
                    }
                    if (run.Script == GpTextTables.ScriptControl) {
                        ch.Width = 0;
                        if (c == '\t') ch.Kind = 1;
                        else if (c == '\r') ch.Kind = 3;   // deleted (FormatStartDelete)
                        else if (c == '\n') {
                            // The end of the paragraph (FormatStartEol): as wide as the visible
                            // paragraph mark U+00B6 (GetVisiCharDup), which only a line measured
                            // with its trailing spaces sees.
                            ch.Kind = 2;
                            ch.Width = fti.CharWidths (run, '¶');
                        }
                    }
                    ch.Space = IsSpace (c);
                    ch.Brk = BreakClass (fti, c, charBreaks);
                    if (ch.Kind == 1) {
                        ch.Width = NextTab (fti, run, ur) - ur;
                    }
                    chars.Add (ch);
                    ur += ch.Width;
                    if (ch.Kind == 2) { endPara = true; stop = true; break; }
                    if (ur > limit) { stop = true; break; }
                }
                if (stop) break;
                cp = run.Cp + run.Len;
            }

            // ---- where the line ends ----
            int end = chars.Count;          // characters [0, end) are on the line
            bool overflow = false;
            {
                int u = 0;
                for (int i = 0; i < chars.Count; i++) {
                    int nu = u + chars [i].Width;
                    // Spaces hang past the margin -- except inside a reversal object (a run nested deeper than
                    // the paragraph), whose subline is formatted with them.
                    bool hangs = chars [i].Space && (chars [i].Run == null || chars [i].Run.Level == fti.ParagraphLevel);
                    if (nu > dua && !hangs && chars [i].Kind != 2 && chars [i].Kind != 3) {
                        // The truncation character: the first whose right edge passes the margin.
                        overflow = true;
                        // A tab whose stop is past the margin breaks through it: the line ends
                        // after the tab (GdipLscbkGetBreakThroughTab).
                        end = chars [i].Kind == 1 ? i + 1 : FindBreak (chars, i);
                        break;
                    }
                    u = nu;
                }
            }
            if (overflow) endPara = false;
            else if (eop != null && end == chars.Count) { }
            // ---- the line's metrics and display nodes ----
            int pos = 0;
            int lastInk = 0;
            Seg seg = null;
            for (int i = 0; i < end; i++) {
                Ch ch = chars [i];
                int kind = ch.Kind == 1 ? 1 : ch.Kind == 2 ? 2 : ch.Kind == 3 ? 3 : 0;
                if (seg == null || seg.Kind != 0 || kind != 0 || seg.Run != ch.Run) {
                    seg = new Seg { Kind = kind, Run = ch.Run, Cp = ch.Cp, CpLim = ch.Cp, Ur = pos, G0 = ch.Glyph };
                    line.Segs.Add (seg);
                }
                seg.CpLim = ch.Cp + 1;
                seg.Width += ch.Width;
                pos += ch.Width;
                if (!ch.Space && ch.Kind != 2 && ch.Kind != 3) lastInk = pos;
            }
            // Glyph arrays for the text nodes.
            foreach (Seg s in line.Segs) {
                if (s.Kind != 0) continue;
                Run run = s.Run;
                int from = s.Cp - run.Cp, to = s.CpLim - run.Cp;
                int g0 = run.Shape.ClusterMap [from];
                int g1 = to < run.Len ? run.Shape.ClusterMap [to] : run.Shape.Glyphs.Length;
                s.G0 = g0; s.GCount = Math.Max (0, g1 - g0);
                s.Adv = new int [s.GCount]; s.OffU = new int [s.GCount]; s.OffV = new int [s.GCount];
                // The advances as the line formatted them (one GetGlyphPositions from the line's cp in the run).
                int lineFrom = Math.Max (cpFirst, run.Cp) - run.Cp;
                int lg0 = run.Shape.ClusterMap [lineFrom];
                int n = run.Shape.Glyphs.Length - lg0;
                var adv = new int [n]; var ou = new int [n]; var ov = new int [n];
                fti.GlyphPositions (run, lg0, n, adv, ou, ov);
                for (int g = 0; g < s.GCount; g++) {
                    s.Adv [g] = adv [g0 - lg0 + g];
                    s.OffU [g] = ou [g0 - lg0 + g];
                    s.OffV [g] = ov [g0 - lg0 + g];
                }
            }
            line.UrLim = lastInk;
            line.UrLimWithSpaces = pos;
            if (end < chars.Count) line.CpLim = chars [end].Cp;
            else if (eop != null) line.CpLim = eop.Cp + eop.Len;
            else line.CpLim = chars.Count > 0 ? chars [^1].Cp + 1 : cpFirst;
            line.EndPara = endPara && end == chars.Count;
            // LSLINFO heights: the largest of the line's runs (the end-of-paragraph run counts).
            var seen = new List<Run> ();
            void Metric (Run r)
            {
                if (r == null || seen.Contains (r)) return;
                seen.Add (r);
                RunMetrics (fti, r, out int a, out int d, out int h);
                line.Ascent = Math.Max (line.Ascent, a);
                line.Descent = Math.Max (line.Descent, d);
                line.Height = Math.Max (line.Height, h);
            }
            for (int i = 0; i < end; i++) Metric (chars [i].Run);
            if (eop != null && end == chars.Count) Metric (eop);
            if (seen.Count == 0) Metric (fti.RunAt (Math.Min (cpFirst, fti.Text.Length)));
            return line;
        }

        /// <summary>GdipLscbkGetRunTextMetrics.</summary>
        static void RunMetrics (GpFullTextImager fti, Run run, out int asc, out int desc, out int height)
        {
            // The formatting's family, style and em at the run (not a fallback run's face or em).
            var m = fti.Metrics;
            float k = fti.Em / m.Upem * fti.R;
            asc = (int) MathF.Floor (m.Ascent * k + 0.5f);
            desc = (int) MathF.Floor (m.Descent * k + 0.5f);
            height = (int) MathF.Floor ((ushort) (m.Gap + m.Descent + m.Ascent) * k + 0.5f);
        }

        /// <summary>GdipLscbkGetBreakingClasses: the class of a character's char class; with
        /// character trimming every character but a space is class 0 (a break anywhere).</summary>
        static int BreakClass (GpFullTextImager fti, int ch, bool charBreaks)
        {
            if (charBreaks && ch != ' ' && (ch & 0xf800) != 0xd800) return 0;
            return GpTextTables.BreakClass (ch, false);
        }

        /// <summary>FindPrevBreakText from the truncation character <paramref name="t"/>: the
        /// character index the line ends before.</summary>
        static int FindBreak (List<Ch> chars, int t)
        {
            // Back from the truncation point: the last opportunity at or before it.
            for (int p = t; p > 0; p--) {
                Ch after = chars [p], before = chars [p - 1];
                if (!after.ClusterStart) continue;
                if (after.Space) continue;
                if (before.Space) {
                    // After a run of spaces: the classes either side of the spaces (the spaces
                    // the line starts with may always break).
                    int q = p - 1;
                    while (q >= 0 && chars [q].Space) q--;
                    if (q < 0) return p;
                    if (GpTextTables.CanBreakAcrossSpaces (chars [q].Brk, after.Brk)) return p;
                    continue;
                }
                if (before.Kind == 1 || after.Kind == 1) return p;
                if (GpTextTables.CanBreakDirect (before.Brk, after.Brk)) return p;
            }
            // ForceBreakCore: before the truncation character, or after it when it is the first.
            int f = t;
            if (f == 0) {
                f = 1;
                while (f < chars.Count && !chars [f].ClusterStart) f++;
            }
            while (f > 0 && f < chars.Count && !chars [f].ClusterStart) f--;
            if (f == 0) f = 1;
            return f;
        }

        /// <summary>The tab stop after <paramref name="ur"/> (GetTabStops @1800f15e0, FindTab):
        /// the format's stops, then every increment.</summary>
        static int NextTab (GpFullTextImager fti, Run run, int ur)
        {
            GpTextFormat f = fti.Format;
            float r = fti.R;
            int increment;
            var stops = new List<int> ();
            if (f == null) increment = (int) MathF.Floor (fti.Em * 4f * r + 0.5f);
            else if (f.Tabs.Length == 0) increment = (int) MathF.Floor (r * f.FirstTab + 0.5f);
            else {
                float cum = f.FirstTab;
                foreach (float t in f.Tabs) { cum += t; stops.Add ((int) MathF.Round (r * cum, MidpointRounding.ToEven)); }
                increment = (int) MathF.Floor (f.Tabs [^1] * r + 0.5f);
            }
            foreach (int s in stops) if (s > ur) return s;
            if (increment <= 0) return ur;
            int last = stops.Count > 0 ? stops [^1] : 0;
            while (last <= ur) last += increment;
            return last;
        }
    }
}
