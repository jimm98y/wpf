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

        void RenderLine (IGpTextTarget target, PointF origin, Line line, int top)
        {
            int v;
            if (IsVertical && !IsRightToLeft)
                v = line.Descent + (Format == null || Format.LeadMargin != 0f ? 256 : 0);
            else v = line.Ascent;
            v += top;
            LogicalToXY (line, 0, v, out int x0, out int y0);
            // Underlines are drawn after the line's dnodes, one per run (GdipLscbkFInterruptUnderline
            // always interrupts), each from its run's start over its dnodes' advances.
            var uls = new List<(Run Run, int Ur, int Dup, int Lead, int Trail)> ();
            foreach (GpLineServices.Seg seg in line.Ls.Segs) {
                int la = 0, ta = 0;
                if (seg.Kind == 0 && seg.GCount > 0) {
                    // The dnode's point: along the line from the start, the run's baseline offset across.
                    int px, py;
                    if (!IsVertical) { px = IsRightToLeft ? x0 - seg.Ur : x0 + seg.Ur; py = (int) MathF.Floor (R * seg.Run.BaseOffset + y0 + 0.5f); }
                    else { px = x0; py = y0 + seg.Ur; }
                    DrawRunGlyphs (target, origin, line, seg, px, py, out la, out ta);
                } else if (seg.Kind != 1) continue;
                if (seg.Run.Underline == 0) continue;
                if (uls.Count > 0 && uls [^1].Run == seg.Run)
                    uls [^1] = (seg.Run, uls [^1].Ur, uls [^1].Dup + seg.Width, uls [^1].Lead, ta);
                else uls.Add ((seg.Run, seg.Ur, seg.Width, la, ta));
            }
            if (target.WorldToDevice != null)
                foreach (var u in uls) {
                    if ((u.Run.Underline & 1) != 0) DrawUnderline (target, origin, line, u.Run, x0, y0, u.Ur, u.Dup, u.Lead, u.Trail, true);
                    if ((u.Run.Underline & 2) != 0) DrawUnderline (target, origin, line, u.Run, x0, y0, u.Ur, u.Dup, u.Lead, u.Trail, false);
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
            if (w2dN == null) {
                // A path: the nominal layout, no device (FullTextImager::AddToPath).
                var gi0 = new GpGlyphImager ();
                gi0.Initialize (this, run, seg, GpMatrix.CreateIdentity (), 2, 0, 0, false, false);
                PointF[] o0 = gi0.Origins (cell, vertical);
                target.DrawPlacedGlyphs (run, 0, gi0.Glyphs, o0, null, null, 0);
                return;
            }
            GpMatrix w2d = w2dN.Value;
            float sx = MathF.Sqrt (w2d.M11 * w2d.M11 + w2d.M12 * w2d.M12);
            float sy = MathF.Sqrt (w2d.M21 * w2d.M21 + w2d.M22 * w2d.M22);
            int mode = target.RealizationMode (run.Face, run.Family, run.Em * sx, sx == sy);
            var gi = new GpGlyphImager ();
            gi.Initialize (this, run, seg, w2d, mode, lead, trail, atStart, atEnd);
            PointF snapped = gi.CellOrigin (cell);
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
            float w = PenWidth (target.WorldToDevice.Value, th);
            PointF a, b;
            if (!IsVertical) { a = new PointF (u / R + origin.X, v / R + origin.Y); b = new PointF (a.X + len / R, a.Y); }
            else { a = new PointF (v / R + origin.X, u / R + origin.Y); b = new PointF (a.X, a.Y + len / R); }
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
