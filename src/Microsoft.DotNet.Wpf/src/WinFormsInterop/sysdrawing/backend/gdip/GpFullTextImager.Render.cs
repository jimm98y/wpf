// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// FullTextImager::Draw @1800f0360 / Render @18003c900 / RenderLine @18003cbe8 and what Line
// Services' LsDisplayLine calls back for each line:
//
//   Draw      the layout rectangle into the clip unless NoClip or an empty side.
//   Render    the lines in order: the window is round(height * r) shifted by the line alignment's
//             share of what the text overflows it by (total - extent, or half); a line wholly
//             above the window is skipped, and drawing stops below it -- unless NoClip without
//             LineLimit.
//   RenderLine   the baseline: the line's top plus its ascent (a vertical line: its descent plus
//             em / 8 when the format has margins); LogicalToXY adds the line alignment's offset
//             (round(extent * r) - round(total * r), or half) and gives the start (+0x54).
//   GdipLscbkDrawGlyphs / FullTextImager::DrawGlyphs   per text dnode: the GlyphImager with the
//             line's margins at its ends, the cell origin pt / r + the layout origin.
//   GdipLscbkDrawUnderline   the run's underline (or strikeout) from its start, adjusted by what
//             the GlyphImager moved at the line's ends, over the dnodes' advances.
//

using System.Collections.Generic;

namespace System.Drawing.WebGpuBackend.Gdip
{
    internal sealed partial class GpFullTextImager
    {
        static readonly bool s_trace = Environment.GetEnvironmentVariable ("WF_FTI_TRACE") == "1";

        /// <summary>FullTextImager::Draw.</summary>
        public void Draw (IGpTextTarget target, PointF origin)
        {
            if (!Valid) return;
            BuildLines ();
            object clip = null;
            if ((FormatFlags & GpTextFormat.NoClip) == 0 && _width != 0f && _height != 0f)
                clip = target.PushClip (new RectangleF (origin.X, origin.Y, _width, _height));
            try { Render (target, origin); }
            finally { if (clip != null) target.PopClip (clip); }
        }

        void Render (IGpTextTarget target, PointF origin)
        {
            if (LineCount < 1) return;
            int total = Rnd (TextHeight * R), extent = Rnd (_across * R);
            int window = 0, limit = total;
            if (extent > 0) {
                int la = Format?.LineAlign ?? 0;
                window = la == 1 ? (total - extent) / 2 : la == 2 ? total - extent : 0;
                limit = extent + window;
            }
            int flags = FormatFlags;
            bool noClip = (flags & GpTextFormat.NoClip) != 0, lineLimit = (flags & GpTextFormat.LineLimit) != 0;
            if (s_trace)
                foreach (Line l in Lines)
                    Console.Error.WriteLine ($"FTI line cp={l.CpFirst}+{l.CpCount} str={l.StrFirst}+{l.Chars} asc={l.Ascent} desc={l.Descent} h={l.Height} len={l.Length} start={l.Start} trim={l.Trimmed} total={total} extent={extent} window={window} limit={limit}");
            int top = 0;
            foreach (Line line in Lines) {
                if (top > limit && (!noClip || lineLimit)) break;
                if (noClip || line.Ascent + line.Descent + top >= window) RenderLine (target, origin, line, top);
                top += line.Height;
            }
        }

        /// <summary>BuiltLine::LogicalToXY.</summary>
        void LogicalToXY (Line line, int u, int v, out int x, out int y)
        {
            if (Format != null && Format.LineAlign != 0) {
                int total = Rnd (TextHeight * R), across = Rnd (_across * R);
                if (Format.LineAlign == 1) v += (across - total) / 2;
                else if (Format.LineAlign == 2) v += across - total;
            }
            if (!IsVertical) {
                x = line.Origin + (IsRightToLeft ? -u : u);
                y = v;
            } else {
                if (IsRightToLeft) v = Rnd (R * _width) - v;
                x = v;
                y = line.Origin + u;
            }
        }

        /// <summary>Line Services' reversal objects (CreateLevelChangeRuns): each dnode's u along the
        /// line's flow once every run of runs nested deeper than the paragraph's level is mirrored
        /// within its span, the deepest first.</summary>
        int[] DisplayUr (Line line)
        {
            if (line.DisplayUr != null) return line.DisplayUr;
            var segs = line.Ls.Segs;
            int n = segs.Count, p = ParagraphLevel, max = p;
            var u = new int [n]; var lv = new int [n];
            for (int i = 0; i < n; i++) {
                u [i] = segs [i].Ur;
                lv [i] = segs [i].Kind == 2 || segs [i].Run == null ? p : segs [i].Run.Level;
                if (lv [i] > max) max = lv [i];
            }
            for (int level = max; level > p; level--)
                for (int i = 0; i < n;) {
                    if (lv [i] < level) { i++; continue; }
                    int j = i;
                    while (j < n && lv [j] >= level) j++;
                    int u0 = int.MaxValue, u1 = int.MinValue;
                    for (int k = i; k < j; k++) { u0 = Math.Min (u0, u [k]); u1 = Math.Max (u1, u [k] + segs [k].Width); }
                    // ReverseDisplay @180111030 lays the subline out from dup - 1 of the object's own
                    // presentation: the trailing spaces of a subline submitted for the trailing
                    // area have no width there (GpLineServices), so they sit at its ink's end.
                    for (int k = i; k < j; k++) u [k] = u0 + u1 - u [k] - segs [k].Width;
                    i = j;
                }
            line.DisplayUr = u;
            return u;
        }

        /// <summary>The point Line Services hands a dnode's DrawGlyphs: its pen start -- its left for
        /// a left-to-right run, its right for a right-to-left one -- and its baseline.</summary>
        void DnodePoint (Line line, GpLineServices.Seg seg, int ur, int x0, int y0, out int px, out int py)
        {
            // A vertical line's paragraph is left to right: a right-to-left run's pen starts at its
            // far end, dup - 1 of its span (DrawGlyphs @18003b720 measures the run's ends from
            // there, so it meets the line's end and takes the trailing margin).
            if (IsVertical) { px = x0; py = seg.Run.Rtl ? y0 + ur + seg.Width - 1 : y0 + ur; return; }
            bool opposite = seg.Run.Rtl != IsRightToLeft;
            // A right-to-left subline of a left-to-right line: ReverseDisplay (@180111030) starts it
            // at dup - 1 of its object, the last unit of its span [a, b).
            if (!IsRightToLeft) px = opposite ? x0 + ur + seg.Width - 1 : x0 + ur;
            // A left-to-right subline of a right-to-left line: its u span [a, b) covers the pixels
            // (x0 - b, x0 - a], so its left is one unit right of x0 - b.
            else px = opposite ? x0 - ur - seg.Width + 1 : x0 - ur;
            py = (int) MathF.Floor (R * seg.Run.BaseOffset + y0 + 0.5f);
        }

        int _snapDv;   // the last DrawGlyphs' baseline snap, ideal units

        /// <summary>Per segment, the advance of the white space the line ends in (Line Services
        /// does not underline the trailing spaces): from the last text segment back, the spaces at
        /// its end, a segment of nothing but spaces wholly, up to the first that has more.</summary>
        int[] TrailingSpaceWidths (Line line)
        {
            var segs = line.Ls.Segs;
            var hang = new int [segs.Count];
            for (int i = segs.Count - 1; i >= 0; i--) {
                GpLineServices.Seg seg = segs [i];
                if (seg.Kind == 2 || seg.Kind == 3) continue;
                if (seg.Kind != 0 || seg.GCount == 0) break;
                Run run = seg.Run;
                if (run.EllipsisText != null) break;
                int strFrom = run.Str + (seg.Cp - run.Cp), nChars = seg.CpLim - seg.Cp;
                int c = nChars;
                while (c > 0 && Text [strFrom + c - 1] == ' ') c--;
                if (c == nChars) break;
                // The spaces' glyphs (one each), wherever the direction puts them.
                int w = 0, last = -1;
                for (int k = c; k < nChars; k++) {
                    int g = run.Shape.ClusterMap [seg.Cp - run.Cp + k] - seg.G0;
                    if (g != last && g >= 0 && g < seg.Adv.Length) w += seg.Adv [g];
                    last = g;
                }
                hang [i] = c == 0 ? seg.Width : w;
                if (c > 0) break;
            }
            return hang;
        }

        void RenderLine (IGpTextTarget target, PointF origin, Line line, int top)
        {
            _snapDv = 0;
            int v;
            if (IsVertical && !IsRightToLeft)
                v = line.Descent + (Format == null || Format.LeadMargin != 0f ? 256 : 0);
            else v = line.Ascent;
            v += top;
            LogicalToXY (line, 0, v, out int x0, out int y0);
            // Underlines are drawn after the line's dnodes, one per run (GdipLscbkFInterruptUnderline
            // always interrupts), each from its run's start over its dnodes' advances.
            var uls = new List<(Run Run, int Ur, int Dup, int Lead, int Trail)> ();
            int[] dur = DisplayUr (line);
            int[] hang = TrailingSpaceWidths (line);
            for (int i = 0; i < line.Ls.Segs.Count; i++) {
                GpLineServices.Seg seg = line.Ls.Segs [i];
                int la = 0, ta = 0;
                if (seg.Kind == 0 && seg.GCount > 0) {
                    // The dnode's point: along the line from the start, the run's baseline offset across.
                    DnodePoint (line, seg, dur [i], x0, y0, out int px, out int py);
                    DrawRunGlyphs (target, origin, line, seg, px, py, out la, out ta);
                } else if (seg.Kind != 1) continue;
                if (seg.Run.Underline == 0) continue;
                // The white space the line ends in is not underlined.
                int width = seg.Width - hang [i];
                if (width <= 0 && hang [i] > 0) continue;
                if (uls.Count > 0 && uls [^1].Run == seg.Run)
                    uls [^1] = (seg.Run, Math.Min (uls [^1].Ur, dur [i]), uls [^1].Dup + width, uls [^1].Lead, ta);
                else uls.Add ((seg.Run, dur [i], width, la, ta));
            }
            foreach (var u in uls) {
                    // Line Services draws a run's strikethrough before its underline.
                    if ((u.Run.Underline & 2) != 0) DrawUnderline (target, origin, line, u.Run, x0, y0 + _snapDv, u.Ur, u.Dup, u.Lead, u.Trail, false);
                    if ((u.Run.Underline & 1) != 0) DrawUnderline (target, origin, line, u.Run, x0, y0 + _snapDv, u.Ur, u.Dup, u.Lead, u.Trail, true);
            }
            // RenderLine: an ellipsis-trimmed line's ellipsis after it (LogicalToXY at +0x58).
            if (line.EllipsisAt >= 0 && line.Trimmed >= 3 && line.Trimmed <= 5 && Ellipsis != null) {
                LogicalToXY (line, line.EllipsisAt, v, out int ex, out int ey);
                var eseg = new GpLineServices.Seg {
                    Kind = 0, Run = Ellipsis, Cp = 0, CpLim = Ellipsis.Len, Ur = line.EllipsisAt, Width = _ellipsisWidth,
                    G0 = 0, GCount = Ellipsis.Shape.Glyphs.Length, Adv = (int[]) EllipsisAdvances.Clone (),
                    OffU = new int [Ellipsis.Shape.Glyphs.Length], OffV = new int [Ellipsis.Shape.Glyphs.Length],
                };
                DrawRunGlyphs (target, origin, line, eseg, ex, ey, out _, out _);
            }
        }

        /// <summary>GdipLscbkDrawGlyphs -> FullTextImager::DrawGlyphs for one dnode.</summary>
        void DrawRunGlyphs (IGpTextTarget target, PointF origin, Line line, GpLineServices.Seg seg, int px, int py,
                            out int leadAdj, out int trailAdj)
        {
            leadAdj = trailAdj = 0;
            Run run = seg.Run;
            GpMatrix? w2dN = target.WorldToDevice;
            int width = 0;
            foreach (int a in seg.Adv) width += a;
            bool vertical = IsVertical;
            int start;
            if (vertical) start = py;
            else if (!run.Rtl) start = px - ((run.Rtl ? 0 : FormatFlags & 1) != 0 ? 1 : 0);
            else start = px + ((FormatFlags & 1) == 0 ? 1 : 0) - width;
            int end = start + width;
            int lineStart = line.Start, lineEnd = line.Start + line.Length;
            int lead = 0, trail = 0;
            bool atStart, atEnd;
            if (vertical || !run.Rtl) {
                atStart = start <= lineStart;
                if (atStart) lead = line.LeadMargin;
                atEnd = lineEnd <= end;
                if (atEnd) trail = line.TrailMargin;
            } else {
                atStart = lineEnd <= end;
                if (atStart) lead = line.TrailMargin;
                atEnd = start <= lineStart;
                if (atEnd) trail = line.LeadMargin;
            }
            var cell = new PointF (px / R + origin.X, py / R + origin.Y);
            if (_recording != null) {
                RecordDisplay (target, line, seg, cell, lead, trail, atStart, atEnd);
                return;
            }
            if (w2dN == null) {
                // A path: the nominal layout, no device (FullTextImager::AddToPath).
                var gi0 = new GpGlyphImager ();
                gi0.Path = true;
                gi0.Initialize (this, run, seg, GpMatrix.CreateIdentity (), 2, 0, 0, false, false);
                PointF[] o0 = gi0.Origins (cell, vertical);
                target.AddGlyphs (run, gi0.Glyphs, gi0.GlyphProps, o0);
                return;
            }
            GpMatrix w2d = w2dN.Value;
            float sx = MathF.Sqrt (w2d.M11 * w2d.M11 + w2d.M12 * w2d.M12);
            float sy = MathF.Sqrt (w2d.M21 * w2d.M21 + w2d.M22 * w2d.M22);
            // A turn that is not a quarter is never grid-fitted (GpFaceRealization +0xbc / +0xc0),
            // so it is never drawn from the face's embedded strikes either.
            float tl = MathF.Max (sx, sy) / 65536f;
            bool turned = !(MathF.Abs (w2d.M12) <= tl && MathF.Abs (w2d.M21) <= tl) && !(MathF.Abs (w2d.M11) <= tl && MathF.Abs (w2d.M22) <= tl);
            int mode = target.RealizationMode (run.Face, run.Family, run.Em, run.Em * sx, sx == sy && !turned);
            if (turned && !vertical && !run.Rtl && target.DrawsAsPath (run.Face, run.Em, mode)) {
                // SwitchToPath: no GlyphImager -- a design realization's ideal advances from the
                // cell (GetGlyphStringIdealAdvanceVector), each glyph's path added there, filled.
                var gp = new GpGlyphImager ();
                gp.Path = true;
                gp.Initialize (this, run, seg, GpMatrix.CreateIdentity (), 2, 0, 0, false, false);
                target.FillGlyphOutlines (run, gp.Glyphs, gp.Origins (cell, false));
                return;
            }
            var gi = new GpGlyphImager ();
            gi.Initialize (this, run, seg, w2d, mode, lead, trail, atStart, atEnd);
            PointF snapped = gi.CellOrigin (cell);
            // GdipLscbkDrawUnderline (@1800f8940) moves the line by the GlyphImager's (+0x270)
            // baseline snap (+0x70) -- the last dnode drawn's.
            if (!vertical) _snapDv = (int) MathF.Round ((snapped.Y - cell.Y) * R);
            PointF[] origins = gi.Origins (snapped, vertical);
            // The characters (for a target that records them) and each character's glyph.
            int strFrom = run.Str + (seg.Cp - run.Cp), nChars = seg.CpLim - seg.Cp;
            var chars = run.EllipsisText ?? Text.Substring (strFrom, nChars);
            var map = new ushort [nChars];
            for (int k = 0; k < nChars; k++) map [k] = (ushort) (run.Shape.ClusterMap [seg.Cp - run.Cp + k] - seg.G0);
            int flags = (FormatFlags < 0 || (run.ItemFlags & 0x8) != 0) ? 4 : 0;
            target.DrawPlacedGlyphs (run, mode, gi.Glyphs, origins, chars, map, flags);
            if (gi.Fitted) {
                leadAdj = atStart ? gi.Shift : 0;
                trailAdj = atEnd ? gi.TrailOut : 0;
            }
            if (_hotkeys.Count > 0 && run.EllipsisText == null)
                DrawHotkeyUnderline (target, run, snapped, strFrom, nChars, map, gi.Glyphs.Length, gi.FinalAdvances,
                                     gi.Fitted && atEnd ? gi.TrailOut : 0);
        }

        /// <summary>FullTextImager::DrawHotkeyUnderline (@1800f0480), from DrawGlyphs after the
        /// dnode's glyphs when the format shows hot keys: for each marker whose key character is in
        /// the dnode, a line under that character's glyphs (its cluster) -- from the cell origin
        /// along the advances before them, as long as theirs (the dnode's trailing adjustment added
        /// for the last cluster) -- post.underlinePosition below the baseline, a
        /// GetDevicePenWidth(underlineThickness) pen.</summary>
        void DrawHotkeyUnderline (IGpTextTarget target, Run run, PointF p, int strFrom, int nChars, ushort[] map,
                                  int glyphCount, int[] adv, int trail)
        {
            float k = run.Em / Metrics.Upem;
            float off = Metrics.UlPos * k, th = (ushort) Metrics.UlThick * k;
            GpMatrix? w2d = target.WorldToDevice;
            float w = w2d == null ? th : PenWidth (w2d.Value, th);
            foreach (int marker in _hotkeys) {
                int c = marker + 1 - strFrom;
                if (c < 0 || c >= nChars) continue;
                int g0 = map [c], g1 = glyphCount;
                for (int j = c + 1; j < nChars; j++)
                    if (map [j] != g0) { g1 = map [j]; break; }
                int before = 0, len = 0;
                for (int j = 0; j < g0 && j < adv.Length; j++) before += adv [j];
                for (int j = g0; j < g1 && j < adv.Length; j++) len += adv [j];
                if (g1 == glyphCount) len += trail;
                PointF a, b;
                if (!IsVertical) {
                    float y = p.Y - off;
                    if (!run.Rtl) { a = new PointF (p.X + before / R, y); b = new PointF (p.X + (before + len) / R, y); }
                    else { a = new PointF (p.X - (before + len) / R, y); b = new PointF (p.X - before / R, y); }
                } else {
                    float x = p.X + off;
                    if (!run.Rtl) { a = new PointF (x, p.Y + before / R); b = new PointF (x, p.Y + (before + len) / R); }
                    else { a = new PointF (x, p.Y - (before + len) / R); b = new PointF (x, p.Y - before / R); }
                }
                if (w2d == null) {
                    if (!IsVertical) target.AddRect (new RectangleF (a.X, a.Y - th * 0.5f, b.X - a.X, th));
                    else target.AddRect (new RectangleF (a.X - th * 0.5f, a.Y, th, b.Y - a.Y));
                } else target.DrawLine (w, a, b);
            }
        }

        /// <summary>GdipLscbkGetRunUnderlineInfo / -StrikethroughInfo and GdipLscbkDrawUnderline.</summary>
        void DrawUnderline (IGpTextTarget target, PointF origin, Line line, Run run, int x0, int y0, int ur, int dup,
                            int leadAdj, int trailAdj, bool underline)
        {
            float k = run.Em / Metrics.Upem * R;
            int off, thick;
            if (underline) {
                off = (int) MathF.Floor (-Metrics.UlPos * k + 0.5f);
                thick = (int) MathF.Floor ((ushort) Metrics.UlThick * k + 0.5f);
            } else {
                off = -(int) MathF.Floor (Metrics.StPos * k + 0.5f);
                thick = (int) MathF.Floor ((ushort) Metrics.StSize * k + 0.5f);
            }
            int len = trailAdj - leadAdj + dup;
            if (len < 1) return;
            float u, v;
            if (!IsVertical) {
                u = (IsRightToLeft ? x0 - ur - dup : x0 + ur) + leadAdj;
                v = y0 + off;
            } else {
                u = y0 + ur + leadAdj;
                v = x0 - off;
            }
            float th = thick / R;
            PointF a, b;
            if (!IsVertical) { a = new PointF (u / R + origin.X, v / R + origin.Y); b = new PointF (a.X + len / R, a.Y); }
            else { a = new PointF (v / R + origin.X, u / R + origin.Y); b = new PointF (a.X, a.Y + len / R); }
            if (target.WorldToDevice == null) {
                // A path: the line as a rectangle th high, centred on it.
                if (!IsVertical) target.AddRect (new RectangleF (a.X, a.Y - th * 0.5f, len / R, th));
                else target.AddRect (new RectangleF (a.X - th * 0.5f, a.Y, th, len / R));
                return;
            }
            float w = PenWidth (target.WorldToDevice.Value, th);
            target.DrawLine (w, a, b);
        }

        /// <summary>GpGraphics::GetDevicePenWidth @1800770e8: |(w, 0)| through world to device, rounded, at least 1.</summary>
        internal static float PenWidth (GpMatrix m, float w)
        {
            float x = m.M11 * w, y = m.M12 * w;
            int i = (int) MathF.Floor (MathF.Sqrt (y * y + x * x) + 0.5f);
            return 1f <= i ? i : 1f;
        }
    }
}
