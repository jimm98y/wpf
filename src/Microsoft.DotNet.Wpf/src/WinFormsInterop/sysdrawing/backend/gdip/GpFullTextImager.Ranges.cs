// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// GpGraphics::MeasureCharacterRanges @1800771d0 -> FullTextImager::MeasureRanges @1800f1a10: every
// string, whatever the fast imager would have made of it, through the full imager.
//
//   MeasureRanges   each of the format's ranges into its region (MeasureRangeRegion), the region
//       intersected with the layout rectangle unless NoClip.
//   MeasureRangeRegion @1800f1880   a negative length counts back from First; a range outside the
//       string is InvalidParameter; each line the range touches adds its part
//       (BuiltLine::GetSelectionTrailRegion), at the line's top (the sum of the heights before it)
//       plus GetBaselineOffset @18003d490's across offset (em / 8 for a vertical line with margins).
//   BuiltLine::GetSelectionTrailRegion @1800f4048   the insertion trail to the range's start XOR the
//       one to its end (GetInsertionTrailRegion @1800f3e90: from the line's start to the cp's
//       position, LsQueryLineCpPpoint + TranslateSubline @1800f4608; a cp at or past the line's
//       last run spans the whole line), then the line's first and last runs' display adjustments
//       (what the GlyphImager moved at the line's ends) excluded or added at the line's ends.
//   BuiltLine::CheckDisplayPlacements @1800f39d0   on a device (not a metafile), the line is
//       "displayed" once (LsDisplayLine with FullTextImager +0x2c0 bit 2): FullTextImager::DrawGlyphs
//       draws nothing but hands each fitted run's adjusted advances (GlyphImager::
//       GetAdjustedGlyphAdvances @1800f5908) to BuiltLine::RecordDisplayPlacements @1800f4258, which
//       keeps every character's right edge from its run's start -- a cluster's advance shared out
//       among its characters -- so a cell inside a run sits where the device put it, a run's start
//       where Line Services put it.
//   BuiltLine::UpdateTrailRegion @1800f4d08   (u, v) .. (u + du, v + ascent + descent) through
//       LogicalToXY, / r, from the measuring origin, combined into the region.
//

using System.Collections.Generic;
using System.Drawing.Drawing2D;

namespace System.Drawing.WebGpuBackend.Gdip
{
    internal sealed partial class GpFullTextImager
    {
        /// <summary>A line's display placements (BuiltLine +0x60 / +0x68) and its lsruns' adjustments.</summary>
        sealed class Display
        {
            public int[] Placements;                       // null: none recorded (+0x68 = -1)
            public readonly Dictionary<Run, (int Lead, int Trail)> Adj = new Dictionary<Run, (int, int)> ();
            public Run LastRun;                            // +0x60
        }

        Display _recording;
        readonly Dictionary<Line, Display> _displays = new Dictionary<Line, Display> ();
        PointF _measureOrigin;                             // +0x260

        /// <summary>FullTextImager::MeasureRanges. Null where a range is outside the string
        /// (InvalidParameter).</summary>
        public GpRegion[] MeasureRanges (IGpTextTarget target, bool metafile, PointF origin, CharacterRange[] ranges, int count)
        {
            var regions = new GpRegion [count];
            for (int i = 0; i < count; i++) regions [i] = GpRegion.Infinite ();
            if (!Valid) return regions;
            BuildLines ();
            _measureOrigin = origin;
            int n = Math.Min (count, ranges?.Length ?? 0);
            for (int i = 0; i < n; i++) {
                if (!MeasureRangeRegion (target, metafile, ranges [i].First, ranges [i].Length, regions [i])) return null;
                if ((FormatFlags & GpTextFormat.NoClip) == 0)
                    regions [i].Combine (GpRegion.FromRect (new RectangleF (origin.X, origin.Y, _width, _height)), CombineMode.Intersect);
            }
            return regions;
        }

        bool MeasureRangeRegion (IGpTextTarget target, bool metafile, int first, int length, GpRegion region)
        {
            region.MakeEmpty ();
            if (length == 0) return true;
            if (length < 0) first += length;
            if (first < 0 || first > _n) return false;
            int len = Math.Abs (length);
            if (len + first > _n) return false;
            if (LineCount < 1) return true;
            int last = len + first - 1;
            int lineStr = 0, top = 0;
            foreach (Line line in Lines) {
                if (first <= line.Consumed + lineStr - 1) {
                    if (last < lineStr) return true;
                    int v = BaselineOffset ();
                    int s = Math.Max (first, lineStr);
                    int e = Math.Min (last, lineStr + line.Consumed - 1);
                    SelectionTrailRegion (target, metafile, line, v + top, s - lineStr, e - s + 1, region);
                }
                lineStr += line.Consumed;
                top += line.Height;
            }
            return true;
        }

        /// <summary>BuiltLine::GetBaselineOffset's across offset: em / 8 for a vertical
        /// (left-to-right) format with margins, else none.</summary>
        int BaselineOffset ()
            => IsVertical && !IsRightToLeft && Format.LeadMargin != 0f ? 256 : 0;

        Display CheckDisplayPlacements (IGpTextTarget target, bool metafile, Line line)
        {
            if (_displays.TryGetValue (line, out Display d)) return d;
            d = new Display ();
            _displays [line] = d;
            if (target != null && target.WorldToDevice != null && !metafile) {
                _recording = d;
                try {
                    LogicalToXY (line, 0, 0, out int x0, out int y0);
                    foreach (GpLineServices.Seg seg in line.Ls.Segs) {
                        if (seg.Kind != 0 || seg.GCount <= 0) continue;
                        int px, py;
                        if (!IsVertical) { px = IsRightToLeft ? x0 - seg.Ur : x0 + seg.Ur; py = (int) MathF.Floor (R * seg.Run.BaseOffset + y0 + 0.5f); }
                        else { px = x0; py = y0 + seg.Ur; }
                        DrawRunGlyphs (target, _measureOrigin, line, seg, px, py, out _, out _);
                    }
                } finally { _recording = null; }
            }
            return d;
        }

        /// <summary>FullTextImager::DrawGlyphs with +0x2c0 bit 2: GetAdjustedGlyphAdvances and
        /// RecordDisplayPlacements, the run's adjustments (lsrun +0x54 / +0x58) set.</summary>
        void RecordDisplay (IGpTextTarget target, Line line, GpLineServices.Seg seg, PointF cell, int lead, int trail, bool atStart, bool atEnd)
        {
            Display d = _recording;
            Run run = seg.Run;
            // GdipLscbkDrawGlyphs: the line's last text run.
            if (run.Kind == 0 && run != d.LastRun && (d.LastRun == null || d.LastRun.Cp <= seg.Cp)) d.LastRun = run;
            d.Adj [run] = (0, 0);
            GpMatrix? w2dN = target.WorldToDevice;
            if (w2dN == null) return;
            GpMatrix w2d = w2dN.Value;
            float sx = MathF.Sqrt (w2d.M11 * w2d.M11 + w2d.M12 * w2d.M12);
            float sy = MathF.Sqrt (w2d.M21 * w2d.M21 + w2d.M22 * w2d.M22);
            int mode = target.RealizationMode (run.Face, run.Family, run.Em * sx, sx == sy);
            var gi = new GpGlyphImager ();
            gi.Initialize (this, run, seg, w2d, mode, lead, trail, atStart, atEnd);
            if (!gi.Fitted) return;
            // GetAdjustedGlyphAdvances: the lead is how far the display cell origin moved.
            int shift;
            if ((FormatFlags & GpTextFormat.Vertical) != 0) shift = gi.Shift;
            else {
                PointF snapped = gi.CellOrigin (cell);
                int k = (int) MathF.Floor ((snapped.X - cell.X) * R + 0.5f);
                shift = run.Rtl ? -k : k;
            }
            RecordDisplayPlacements (d, line, run, seg, gi.FinalAdvances, shift);
            d.Adj [run] = (atStart ? shift : 0, atEnd ? gi.TrailOut : 0);
        }

        /// <summary>BuiltLine::RecordDisplayPlacements: per character, its right edge from the
        /// run's start (a cluster's advance shared among its characters, the remainder a unit at a
        /// time as GDI+ hands it out).</summary>
        void RecordDisplayPlacements (Display d, Line line, Run run, GpLineServices.Seg seg, int[] adv, int lead)
        {
            if (d.Placements == null) d.Placements = new int [Math.Max (0, line.Chars)];
            int dir = ParagraphLevel == (run.Level & 1) ? 1 : -1;
            int nChars = seg.CpLim - seg.Cp, nGlyphs = seg.GCount;
            int off = seg.Cp - run.Cp;
            int Map (int k) => run.Shape.ClusterMap [off + k] - seg.G0;
            int cum = dir * lead;
            int c = 0;
            while (nChars > 0) {
                int span = 1;
                while (span < nChars && Map (c + span) == Map (c)) span++;
                int gEnd = span != nChars ? Map (c + span) : nGlyphs;
                int sum = 0;
                for (int g = Map (c); g < gEnd; g++) sum += adv [g];
                uint total = unchecked ((uint) (dir * sum));
                uint per = total / (uint) span;
                int rem = unchecked ((int) (total - per * (uint) span));
                for (int k = c; k < c + span; k++) {
                    cum = unchecked ((rem != 0 ? dir : 0) + (int) per + cum);
                    int at = StringPosition (seg.Cp) - line.StrFirst + k;
                    if (at >= 0 && at < d.Placements.Length) d.Placements [at] = cum;
                    rem--;
                }
                nChars -= span;
                c += span;
            }
        }

        /// <summary>BuiltLine::GetSelectionTrailRegion.</summary>
        void SelectionTrailRegion (IGpTextTarget target, bool metafile, Line line, int v, int start, int count, GpRegion region)
        {
            Display d = CheckDisplayPlacements (target, metafile, line);
            InsertionTrailRegion (line, d, v, start, region);
            if (count == 0) return;
            int end = start + count;
            InsertionTrailRegion (line, d, v, end, region);
            Run first = RunAt (line.CpFirst);
            if (first == null) return;
            int level = ParagraphLevel;
            d.Adj.TryGetValue (first, out var fa);
            int runStart, lead, trail, runEnd;
            if (level == (first.Level & 1)) { runStart = first.Str; lead = fa.Lead; trail = fa.Trail; runEnd = first.Str + first.Len; }
            else { trail = -fa.Lead; runEnd = first.Str; lead = -fa.Trail; runStart = first.Str + first.Len; }
            if (lead != 0) {
                CombineMode m = lead >= 0 || runStart - line.StrFirst < start || end < runStart - line.StrFirst ? CombineMode.Exclude : CombineMode.Union;
                UpdateTrailRegion (line, region, v, 0, lead, m);
            }
            Run lastRun = d.LastRun;
            if (lastRun != null && lastRun != first) {
                d.Adj.TryGetValue (lastRun, out var la);
                if (level == (lastRun.Level & 1)) { trail = la.Trail + trail; runEnd = lastRun.Str + lastRun.Len; }
                else { runEnd = lastRun.Str; trail -= la.Lead; }
            }
            if (trail != 0) {
                CombineMode m = trail < 1 || runEnd - line.StrFirst < start || end < runEnd - line.StrFirst ? CombineMode.Exclude : CombineMode.Union;
                UpdateTrailRegion (line, region, v, line.Length, trail, m);
            }
        }

        /// <summary>BuiltLine::GetInsertionTrailRegion: from the line's start to the position of
        /// its <paramref name="count"/>'th character, XOR'd into the region.</summary>
        void InsertionTrailRegion (Line line, Display d, int v, int count, GpRegion region)
        {
            if (count <= 0) return;
            int visible;
            if ((FormatFlags & GpTextFormat.MeasureTrailingSpaces) == 0 && d.LastRun != null)
                visible = d.LastRun.Str + d.LastRun.Len - line.StrFirst;
            else visible = line.Chars;
            if (visible <= count) {
                UpdateTrailRegion (line, region, v, 0, line.Length, CombineMode.Xor);
                return;
            }
            // A hot-key marker before the position does not count.
            while (count > 0 && Text [line.StrFirst + count - 1] == '￿') count--;
            if (count == 0) return;
            int cp = LsCp (line.StrFirst + count);
            int u = CpPosition (line, d, cp);
            UpdateTrailRegion (line, region, v, 0, u, CombineMode.Xor);
        }

        /// <summary>LsQueryLineCpPpoint + TranslateSubline (main subline, left to right): the cp's
        /// cell, from its run's start at the device's placements when the line has them, at Line
        /// Services' otherwise; inside a cell, its share of the cell's width.</summary>
        int CpPosition (Line line, Display d, int cp)
        {
            GpLineServices.Seg seg = null;
            foreach (GpLineServices.Seg s in line.Ls.Segs)
                if (cp >= s.Cp && cp < s.CpLim) { seg = s; break; }
            if (seg == null) {
                var segs = line.Ls.Segs;
                return segs.Count == 0 ? 0 : segs [^1].Ur + segs [^1].Width;
            }
            int cellStart, cellU, dup, cChars;
            if (seg.Kind == 0 && seg.GCount > 0) {
                Run run = seg.Run;
                ushort[] map = run.Shape.ClusterMap;
                int k = cp - run.Cp;
                int gi = map [k];
                int ks = k;
                while (ks > seg.Cp - run.Cp && map [ks - 1] == gi) ks--;
                int ke = k + 1;
                while (ke < seg.CpLim - run.Cp && map [ke] == gi) ke++;
                int gEnd = ke < seg.CpLim - run.Cp ? map [ke] : seg.G0 + seg.GCount;
                cellStart = run.Cp + ks;
                cChars = ke - ks;
                cellU = seg.Ur;
                for (int g = seg.G0; g < gi; g++) cellU += seg.Adv [g - seg.G0];
                dup = 0;
                for (int g = gi; g < gEnd; g++) dup += seg.Adv [g - seg.G0];
            } else {
                cellStart = seg.Cp; cellU = seg.Ur; dup = seg.Width; cChars = seg.CpLim - seg.Cp;
            }
            int pos;
            if (d.Placements == null) pos = cellU;
            else {
                pos = seg.Ur;
                int at = StringPosition (cellStart) - line.StrFirst;
                if (cellStart > seg.Cp && at - 1 >= 0 && at - 1 < d.Placements.Length) pos += d.Placements [at - 1];
            }
            int inCell = cp - cellStart;
            if (inCell > 0 && cChars > 0) {
                if (inCell > cChars) inCell = cChars;
                pos += MulDiv (dup, inCell, cChars);
            }
            return pos;
        }

        /// <summary>Win32 MulDiv: a * b / c rounded half away from zero.</summary>
        static int MulDiv (int a, int b, int c)
        {
            long p = (long) a * b;
            long q = (Math.Abs (p) + Math.Abs ((long) c) / 2) / Math.Abs ((long) c);
            return (int) ((p < 0) != (c < 0) ? -q : q);
        }

        /// <summary>BuiltLine::UpdateTrailRegion.</summary>
        void UpdateTrailRegion (Line line, GpRegion region, int v, int u, int du, CombineMode mode)
        {
            if (u == 0 && du == 0 && mode != CombineMode.Intersect) return;
            LogicalToXY (line, u, v, out int x0, out int y0);
            LogicalToXY (line, du + u, line.Descent + line.Ascent + v, out int x1, out int y1);
            int l = x0, r = x1;
            if (x1 - x0 < 0) { l = x1; r = x0; }
            int t = y0, b = y1;
            if (y1 - y0 < 0) { t = y1; b = y0; }
            float R0 = R;
            var rect = new RectangleF ((float) l / R0 + _measureOrigin.X, (float) t / R0 + _measureOrigin.Y,
                                       (float) (r - l) / R0, (float) (b - t) / R0);
            region.Combine (GpRegion.FromRect (rect), mode);
        }
    }
}
