// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Graphics.DrawString and MeasureString on a PRINTED page.
//
// The screen path (Graphics.DrawString's GPU branch, GdiPlusText) is tuned pixel for pixel against
// GDI+ on a 96 dpi display: hinted advances at the screen ppem, whole-pixel margins, ClearType. None
// of that means anything on paper. A printer is 600 dpi or more and a PDF has no resolution at all,
// so GDI+ lays printer text out at device resolution where hinting has all but vanished, and what a
// page needs is the face's own DESIGN metrics, scaled: advances, the cell ascent the first baseline
// sits on, the line spacing lines step by. That is what this lays out, in the caller's world units,
// glyph by glyph, so the page carries real text -- glyph ids of a named font -- rather than shapes.
//
// What it follows of GDI+'s layout (StringFormat): the em/6 leading and trailing margins of the
// default format (none for GenericTypographic), word wrapping inside the layout rectangle unless
// NoWrap, LineLimit, the three alignments in both directions, trailing spaces left out of a line's
// width, hot-key prefixes, tabs, explicit line breaks; and font LINKING, so a character the face
// lacks comes from a face that has it rather than drawing a .notdef box.
//

using System;
using System.Collections.Generic;
using Microsoft.Wpf.Interop.WebGpu.Composition;
using Microsoft.Wpf.Interop.WebGpu.Composition.Text;

namespace System.Drawing.WebGpuBackend
{
    /// <summary>The design metrics GDI+ reads from a face, in font units.</summary>
    internal sealed class FaceMetrics
    {
        public int UnitsPerEm = 2048;
        public int CellAscent, CellDescent, LineSpacing;
        public int UnderlinePosition, UnderlineThickness, StrikeoutPosition, StrikeoutSize;
        public int CapHeight, XMin, YMin, XMax, YMax;
        public float ItalicAngle;

        private static readonly Dictionary<TrueTypeFont, FaceMetrics> s_cache = new(ReferenceEqualityComparer.Instance);

        internal static FaceMetrics Of(TrueTypeFont face)
        {
            lock (s_cache)
            {
                if (s_cache.TryGetValue(face, out FaceMetrics m)) return m;
                m = Read(face.FontData, face.SfntOffset);
                s_cache[face] = m;
                return m;
            }
        }

        private static FaceMetrics Read(byte[] d, int sfnt)
        {
            var m = new FaceMetrics();
            int Table(string tag)
            {
                if (sfnt + 12 > d.Length) return -1;
                int n = U16(d, sfnt + 4);
                for (int i = 0; i < n; i++)
                {
                    int rec = sfnt + 12 + i * 16;
                    if (rec + 16 > d.Length) break;
                    if (d[rec] == tag[0] && d[rec + 1] == tag[1] && d[rec + 2] == tag[2] && d[rec + 3] == tag[3])
                        return (int)U32(d, rec + 8);
                }
                return -1;
            }
            int head = Table("head"), hhea = Table("hhea"), os2 = Table("OS/2"), post = Table("post");
            if (head >= 0 && head + 54 <= d.Length)
            {
                m.UnitsPerEm = U16(d, head + 18);
                m.XMin = S16(d, head + 36); m.YMin = S16(d, head + 38);
                m.XMax = S16(d, head + 40); m.YMax = S16(d, head + 42);
            }
            int hAsc = 0, hDesc = 0, hGap = 0;
            if (hhea >= 0 && hhea + 10 <= d.Length) { hAsc = S16(d, hhea + 4); hDesc = S16(d, hhea + 6); hGap = S16(d, hhea + 8); }
            int winAsc = hAsc, winDesc = -hDesc;
            if (os2 >= 0 && os2 + 78 <= d.Length)
            {
                winAsc = U16(d, os2 + 74); winDesc = U16(d, os2 + 76);
                m.StrikeoutSize = S16(d, os2 + 26); m.StrikeoutPosition = S16(d, os2 + 28);
                if (U16(d, os2) >= 2 && os2 + 90 <= d.Length) m.CapHeight = S16(d, os2 + 88);
            }
            m.CellAscent = winAsc;
            m.CellDescent = winDesc;
            // GDI+'s line spacing: the larger of the cell (win ascent + descent) and the typographic
            // line (hhea ascender - descender + line gap). Arial 2355, Times 2355, Tahoma 2472.
            m.LineSpacing = Math.Max(winAsc + winDesc, hAsc - hDesc + hGap);
            if (post >= 0 && post + 12 <= d.Length)
            {
                m.ItalicAngle = (int)U32(d, post + 4) / 65536f;
                m.UnderlinePosition = S16(d, post + 8);
                m.UnderlineThickness = S16(d, post + 10);
            }
            if (m.UnderlineThickness <= 0) m.UnderlineThickness = m.UnitsPerEm / 14;
            if (m.UnderlinePosition == 0) m.UnderlinePosition = -m.UnitsPerEm / 10;
            if (m.StrikeoutSize <= 0) m.StrikeoutSize = m.UnderlineThickness;
            if (m.StrikeoutPosition <= 0) m.StrikeoutPosition = m.UnitsPerEm * 26 / 100;
            if (m.CapHeight <= 0) m.CapHeight = winAsc * 7 / 10;
            return m;
        }

        private static int U16(byte[] d, int o) => o + 2 <= d.Length ? (d[o] << 8) | d[o + 1] : 0;
        private static int S16(byte[] d, int o) => (short)U16(d, o);
        private static uint U32(byte[] d, int o) => o + 4 <= d.Length ? (uint)((d[o] << 24) | (d[o + 1] << 16) | (d[o + 2] << 8) | d[o + 3]) : 0;
    }

    /// <summary>A string laid out for a page, in world units.</summary>
    internal sealed class PrintTextLayout
    {
        internal sealed class Run
        {
            public TrueTypeFont Face;
            public string Family;
            public ushort[] Glyphs;
            public float[] Xs;          // each glyph's x, relative to the line's start
            public string Chars;
            public int[] Clusters;      // per glyph, its first character in Chars
        }

        internal sealed class Line
        {
            public float X, Baseline, Width;
            public readonly List<Run> Runs = new();
            public float MnemonicX = float.NaN, MnemonicW;
        }

        public readonly List<Line> Lines = new();
        public float Em, Width, Height, LineHeight, Ascent, Descent;
        public FaceMetrics Metrics;
        public TrueTypeFont Face;
        public int CharactersFitted;
    }

    internal static class PrintText
    {
        // StringFormatFlags
        private const int NoWrap = 0x1000, LineLimit = 0x2000, MeasureTrailingSpaces = 0x800;

        /// <summary>The face a family and style (1 bold, 2 italic) resolve to, as the renderer loads it.</summary>
        internal static TrueTypeFont Face(string family, int style) => GdiPlusText.Face(family ?? "Arial", style)
            ?? GdiPlusText.Face("Arial", style) ?? GdiPlusText.Face("Segoe UI", style);

        /// <summary>The line spacing GDI+ steps by, in units of the em.</summary>
        internal static bool Metrics(string family, int style, out FaceMetrics metrics)
        {
            TrueTypeFont face = Face(family, style);
            metrics = face != null ? FaceMetrics.Of(face) : null;
            return metrics != null;
        }

        /// <summary>Lays <paramref name="text"/> out in a rectangle (zero width or height: unbounded
        /// that way). <paramref name="align"/> and <paramref name="lineAlign"/> are StringAlignment;
        /// <paramref name="hotkey"/> is HotkeyPrefix (0 none, 1 show, 2 hide).</summary>
        internal static PrintTextLayout Layout(string text, string family, int style, float em,
                                               float x, float y, float w, float h,
                                               int flags, bool typographic, int align, int lineAlign, int hotkey,
                                               float tabWidth)
        {
            var layout = new PrintTextLayout { Em = em };
            TrueTypeFont face = Face(family, style);
            if (face == null || string.IsNullOrEmpty(text) || !(em > 0f)) return layout;
            FaceMetrics fm = FaceMetrics.Of(face);
            layout.Face = face;
            layout.Metrics = fm;
            float unit = em / fm.UnitsPerEm;
            layout.LineHeight = fm.LineSpacing * unit;
            layout.Ascent = fm.CellAscent * unit;
            layout.Descent = fm.CellDescent * unit;

            // Hot-key prefixes: '&' marks the next character, "&&" is a literal ampersand.
            int mnemonic = -1;
            if (hotkey != 0)
            {
                var sb = new System.Text.StringBuilder(text.Length);
                for (int i = 0; i < text.Length; i++)
                {
                    if (text[i] == '&' && i + 1 < text.Length)
                    {
                        i++;
                        if (text[i] != '&' && mnemonic < 0) mnemonic = sb.Length;
                    }
                    sb.Append(text[i]);
                }
                text = sb.ToString();
                if (hotkey != 1) mnemonic = -1;
            }

            float margin = typographic ? 0f : em / 6f;
            float avail = w > 0 && (flags & NoWrap) == 0 ? Math.Max(0f, w - 2f * margin) : float.PositiveInfinity;
            if (tabWidth <= 0f) tabWidth = 4f * Advance(face, ' ', unit, style, family, out _, out _);

            // Characters with their advance and the face they come from.
            int n = text.Length;
            var adv = new float[n];
            var faces = new TrueTypeFont[n];
            var famOf = new string[n];
            var gids = new ushort[n];
            for (int i = 0; i < n; i++)
            {
                char c = text[i];
                if (c == '\r' || c == '\n') continue;
                if (c == '\t') { faces[i] = face; famOf[i] = family; gids[i] = (ushort)Math.Max(0, face.GlyphIndex(' ')); continue; }
                adv[i] = Advance(face, c, unit, style, family, out faces[i], out famOf[i], out gids[i]);
                // GDI+'s default format tracks nominal advances by 1.03 (GenericTypographic does not):
                // the width it measures a line at, and the width its imager keeps a line to.
                if (!typographic) adv[i] = MathF.Floor(adv[i] / unit * 1.03f + 0.5f) * unit;
            }

            // Lines: explicit breaks, then wrapping at spaces.
            var lines = new List<(int Start, int End)>();
            int s0 = 0;
            for (int i = 0; i <= n; i++)
            {
                if (i < n && text[i] != '\n') continue;
                int end = i;
                if (end > s0 && text[end - 1] == '\r') end--;
                Wrap(text, adv, s0, end, avail, tabWidth, lines);
                s0 = i + 1;
            }

            int maxLines = int.MaxValue;
            if (h > 0 && layout.LineHeight > 0)
            {
                maxLines = (flags & LineLimit) != 0
                    ? Math.Max(1, (int)Math.Floor(h / layout.LineHeight + 1e-4))
                    : Math.Max(1, (int)Math.Ceiling(h / layout.LineHeight - 1e-4));
            }
            if (lines.Count > maxLines) lines.RemoveRange(maxLines, lines.Count - maxLines);

            float total = lines.Count * layout.LineHeight;
            float top = y;
            if (h > 0)
            {
                if (lineAlign == 1) top = y + (h - total) / 2f;
                else if (lineAlign == 2) top = y + h - total;
            }

            float widest = 0f;
            for (int li = 0; li < lines.Count; li++)
            {
                (int a, int b) = lines[li];
                // Trailing white space is not part of the line GDI+ aligns.
                int visibleEnd = b;
                if ((flags & MeasureTrailingSpaces) == 0)
                    while (visibleEnd > a && (text[visibleEnd - 1] == ' ' || text[visibleEnd - 1] == '\t')) visibleEnd--;
                var line = new PrintTextLayout.Line { Baseline = top + li * layout.LineHeight + layout.Ascent };
                float pen = 0f;
                PrintTextLayout.Run run = null;
                var g = new List<ushort>(); var xs = new List<float>(); var cl = new List<int>();
                int runStart = a;
                void Flush(int upTo)
                {
                    if (run == null) return;
                    run.Glyphs = g.ToArray(); run.Xs = xs.ToArray();
                    run.Chars = text.Substring(runStart, upTo - runStart);
                    var clusters = cl.ToArray();
                    for (int k = 0; k < clusters.Length; k++) clusters[k] -= runStart;
                    run.Clusters = clusters;
                    if (run.Glyphs.Length > 0) line.Runs.Add(run);
                    run = null; g.Clear(); xs.Clear(); cl.Clear();
                }
                for (int i = a; i < visibleEnd; i++)
                {
                    char c = text[i];
                    if (c == '\r') continue;
                    float advance = c == '\t' ? (float)(Math.Floor(pen / tabWidth + 1e-4) + 1) * tabWidth - pen : adv[i];
                    if (i == mnemonic) { line.MnemonicX = pen; line.MnemonicW = advance; }
                    // Spaces are glyphs too: a reader that extracts the page's text finds its words
                    // by them. Tabs are not; they are only a jump of the pen.
                    if (c != '\t' && faces[i] != null)
                    {
                        if (run == null || run.Face != faces[i])
                        {
                            Flush(i);
                            run = new PrintTextLayout.Run { Face = faces[i], Family = famOf[i] };
                            runStart = i;
                        }
                        g.Add(gids[i]); xs.Add(pen); cl.Add(i);
                    }
                    pen += advance;
                }
                Flush(visibleEnd);
                line.Width = pen;
                float lx = x + margin;
                if (w > 0)
                {
                    if (align == 1) lx = x + (w - pen) / 2f;
                    else if (align == 2) lx = x + w - margin - pen;
                }
                line.X = lx;
                widest = Math.Max(widest, pen);
                layout.Lines.Add(line);
                layout.CharactersFitted = b;
            }
            layout.Width = lines.Count == 0 ? 0f : widest + 2f * margin;
            // What MeasureString answers: GDI+'s text box -- the last line its CELL (win ascent plus
            // descent) rather than its spacing, and the default format's em/8 of extra height.
            layout.Height = lines.Count == 0 ? 0f
                : (lines.Count - 1) * layout.LineHeight + layout.Ascent + layout.Descent + (typographic ? 0f : em / 8f);
            return layout;
        }

        private static float Advance(TrueTypeFont face, char c, float unit, int style, string family,
                                     out TrueTypeFont from, out string fromFamily)
            => Advance(face, c, unit, style, family, out from, out fromFamily, out _);

        private static readonly Dictionary<(string, char, int), (TrueTypeFont Face, string Family)> s_linked = new();

        // A character's advance in world units, and the face it is drawn from: the requested one
        // when it has the character, else the face GDI's font linking would pick.
        private static float Advance(TrueTypeFont face, char c, float unit, int style, string family,
                                     out TrueTypeFont from, out string fromFamily, out ushort gid)
        {
            from = face; fromFamily = family;
            int g = char.IsSurrogate(c) ? 0 : face.GlyphIndex(c);
            if (g <= 0 && !char.IsWhiteSpace(c) && !char.IsControl(c) && !char.IsSurrogate(c))
            {
                (TrueTypeFont Face, string Family) linked;
                lock (s_linked)
                {
                    if (!s_linked.TryGetValue((family, c, style), out linked))
                    {
                        TrueTypeFont found = null; string foundFamily = null;
                        FontFiles.LinkedFamily(family, c, f =>
                        {
                            TrueTypeFont candidate = GdiPlusText.Face(f, style);
                            if (candidate == null || candidate.GlyphIndex(c) <= 0) return false;
                            found = candidate; foundFamily = f;
                            return true;
                        });
                        linked = (found, foundFamily);
                        s_linked[(family, c, style)] = linked;
                    }
                }
                if (linked.Face != null)
                {
                    from = linked.Face; fromFamily = linked.Family;
                    g = linked.Face.GlyphIndex(c);
                    FaceMetrics lm = FaceMetrics.Of(linked.Face);
                    gid = (ushort)g;
                    return linked.Face.DesignAdvance(g) * (unit * FaceMetrics.Of(face).UnitsPerEm / lm.UnitsPerEm);
                }
            }
            gid = (ushort)Math.Max(0, g);
            return face.DesignAdvance(Math.Max(0, g)) * unit;
        }

        // Breaks [start, end) into lines no wider than avail, at spaces where it can.
        private static void Wrap(string text, float[] adv, int start, int end, float avail, float tabWidth,
                                 List<(int, int)> lines)
        {
            if (start >= end) { lines.Add((start, start)); return; }
            int lineStart = start;
            while (lineStart < end)
            {
                float pen = 0f;
                int lastBreak = -1;
                int i = lineStart;
                for (; i < end; i++)
                {
                    char c = text[i];
                    float a = c == '\t' ? (float)(Math.Floor(pen / tabWidth + 1e-4) + 1) * tabWidth - pen : adv[i];
                    if (c == ' ' || c == '\t') { pen += a; lastBreak = i + 1; continue; }
                    if (pen + a > avail + 1e-3f && i > lineStart) break;
                    pen += a;
                    if (c == '-') lastBreak = i + 1;
                }
                if (i >= end) { lines.Add((lineStart, end)); return; }
                int brk = lastBreak > lineStart ? lastBreak : i;
                lines.Add((lineStart, brk));
                lineStart = brk;
                // The spaces a line broke at do not start the next one.
                while (lineStart < end && text[lineStart] == ' ') lineStart++;
            }
        }
    }
}
