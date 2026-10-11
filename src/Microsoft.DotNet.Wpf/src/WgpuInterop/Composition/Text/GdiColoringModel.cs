// A FAITHFUL, STANDALONE port of GDI's ClearType "coloring" of stems, transcribed from dwrite.dll's
// GC* chain (GCProcess -> EnterGlbCounter -> GlobalColoring -> FixBands -> GCFixOnePath, with
// ClumpCounters/SortGroupsByFrac and GCCalcLocs/Adjust/GCFindLocs). It is NOT wired into rendering:
// it exists to be validated per-stem against SolveGdisEdges before any of it is trusted. Values are
// 16.16 fixed point, matching the scaler, unless a field says 64ths.
//
// The decompiled C this is transcribed from lives in scratchpad/re/{gc_align,helpers,layer2,
// pathhelpers,counters,gcprocess}.c; the struct/algorithm map is scratchpad/re/stem_struct.md.
//
// STEM RECORD FIELDS (byte offsets, from the RE), kept as named fields here:
//   +0xc/+0x10  KeyA/KeyB   original edge coordinates (identity + the fit is computed from these)
//   +0x14/+0x18 Lo/Hi       scaled-original edge positions (16.16)
//   +0x1c/+0x20 YLo/YHi     the stem's y-extent (two stems counter iff these overlap)
//   +0x24/+0x28 E0/E1       the CURRENT fitted edges (what the pass adjusts)
//   +0x08       Width       |E1-E0| (set by GCProcess after the initial fit)
//   +0x0a       NCount      a per-stem integer weight (summed along a path)
//   +0x2a       Frac        fractional-group count (ClumpCounters/SortGroupsByFrac)
//   +0x2c       Clump       clump id
//   +0x48       Flags       bit1 = "done/anchored"

using System;
using System.Collections.Generic;

namespace Microsoft.Wpf.Interop.WebGpu.Composition.Text
{
    /// <summary>One stem = one x-link the font program made (a MIRP/MDRP pair), plus the y-extent
    /// of the points it spans. All x in 16.16 fixed point.</summary>
    internal sealed class GcStem
    {
        internal int KeyA, KeyB;     // original edge coords (font units)
        internal int Lo, Hi;         // scaled original edges (16.16)
        internal int YLo, YHi;       // y-extent (16.16) -- for counter adjacency
        internal int E0, E1;         // fitted edges (16.16), the thing being coloured
        internal int Width;          // |E1-E0|
        internal int NCount;         // per-stem weight
        internal short Frac;         // frac group
        internal byte Clump;         // clump id
        internal bool Merged;        // ClumpCounters scratch
        internal int Acc;            // ClumpCounters position accumulator (+0x20 in GDI reused; kept apart)
        internal bool Done;          // flag bit1
    }

    /// <summary>A counter = the white space between two y-overlapping stems (adjacent strokes).</summary>
    internal sealed class GcCounter
    {
        internal GcStem A, B;        // A on the left, B on the right (A.Hi ~ B.Lo)
        internal int Gap;            // A.Lo - B.Hi at build time (the recorded x-gap), GDI's +0x08 on the node
    }

    internal static class GdiColoringModel
    {
        internal const int One = 0x10000;   // 1.0 in 16.16
        internal const int Half = 0x8000;

        // ---- fixed-point primitives, byte-exact from the ASM ---------------------------------------
        internal static int FixRatio(int a, int b) =>
            b == 0 ? (a < 0 ? int.MinValue : int.MaxValue)
                   : (int)Math.Clamp(Math.Round((double)a * 65536.0 / b), int.MinValue, int.MaxValue);

        internal static int FracMul(int a, int b) =>
            (int)Math.Clamp(Math.Round((double)a * b / 65536.0), int.MinValue, int.MaxValue);

        internal static int FixMul(int a, int b) => (int)(((long)a * b) >> 16);
        internal static int FixDiv(int a, int b) => b == 0 ? 0 : (int)(((long)a << 16) / b);

        internal static int Round16(int v) => (v + Half) & unchecked((int)0xffff0000);

        // ---- EnterGlbCounter: do two stems form a counter? -----------------------------------------
        // A and B counter iff their y-extents overlap by enough that the narrower one's extent is at
        // most twice the overlap. The recorded gap is A.Lo - B.Hi.
        internal static bool CounterAdjacent(GcStem a, GcStem b, out int gap)
        {
            gap = 0;
            int lo = Math.Min(a.YHi, b.YHi);       // +0x20 = YHi
            int hi = Math.Max(a.YLo, b.YLo);       // +0x1c = YLo
            int overlap = lo - hi;
            if (overlap <= 0) return false;
            int narrow = Math.Min(a.YHi - a.YLo, b.YHi - b.YLo);
            if (narrow > overlap * 2) return false;
            gap = a.Lo - b.Hi;
            return true;
        }

        // ---- ClumpCounters: agglomerative clustering of the path's stems by proximity --------------
        // Repeatedly merge the adjacent pair with the smallest |width difference| into one clump.
        // Faithful to ClumpCounters@1802f32f8: it initialises Clump=index, Acc=Width, then merges.
        internal static void ClumpCounters(List<GcStem> stems, int slopeCap)
        {
            int n = stems.Count;
            for (int i = 0; i < n; i++)
            {
                stems[i].Clump = (byte)i;
                stems[i].Acc = stems[i].Width;
                stems[i].Merged = false;
            }
            // Merge the closest adjacent (by Width) unmerged pair until none remain within the cap.
            while (true)
            {
                int best = -1, bestDist = int.MaxValue;
                for (int i = 0; i + 1 < n; i++)
                {
                    if (stems[i + 1].Merged) continue;
                    int d = Math.Abs(stems[i].Width - stems[i + 1].Width);
                    if (d < bestDist) { bestDist = d; best = i; }
                }
                if (best < 0) break;
                // (transcription in progress: the merge step and the slopeCap gate need the exact
                // ClumpCounters tail; left explicit so the validation harness can exercise what exists)
                stems[best + 1].Merged = true;
                stems[best + 1].Clump = stems[best].Clump;
                if (bestDist > slopeCap) break;
            }
        }

        // ---- SortGroupsByFrac: order the path's stems, then group by clump ------------------------
        // Selection sort by CounterGt (the fractional position of E1), keeping equal-clump stems
        // contiguous. Faithful shape of SortGroupsByFrac@1802f41e8 + CounterGt@1802f34e0, which
        // compares the low 16 bits (the sub-pixel fraction) of the fitted edge.
        internal static void SortGroupsByFrac(List<GcStem> stems)
        {
            int Frac(GcStem s) => s.E1 & 0xffff;                 // sub-pixel remainder
            stems.Sort((a, b) => Frac(a).CompareTo(Frac(b)));
            // (GDI then coalesces runs of equal Clump; with our per-stem clumping this is a no-op
            // until ClumpCounters' merge is complete -- kept explicit for when it is.)
        }

        // ---- GCFixOnePath: distribute whole pixels along one path of stems ------------------------
        // Transcribed from GCFixOnePath@18016bae8. `path` is the ordered stems of one connected run
        // (built by FixBands from the counters). Each stem's E0/E1 are nudged by whole pixels so the
        // path spans a whole-pixel count, the leftover distributed in clump/frac order.
        // param3Slope is the path's scale-derived weight; returns false if the path is degenerate.
        internal static bool FixOnePath(List<GcStem> path)
        {
            int n = path.Count;
            if (n == 0) return false;
            GcStem first = path[0], last = path[n - 1];

            // NCount summed along the path.
            int nCount = 0;
            foreach (var s in path) nCount += s.NCount;

            // slope = fixdiv((first.Hi-first.Lo)*12, first.KeyB-first.KeyA), capped at 0.6 (0x999a).
            int slope = FixDiv((first.Hi - first.Lo) * 12, first.KeyB - first.KeyA);
            if (slope > 0x999a) slope = 0x999a;

            ClumpCounters(path, slope);
            SortGroupsByFrac(path);

            int fracSum = 0;
            foreach (var s in path) fracSum += s.Frac;

            // The path's span, in fitted or scaled-original terms depending on whether the ends are
            // anchored (Done).
            int hiEnd = last.Done ? last.E0 : last.Lo;
            int loEnd = first.Done ? first.E1 : first.Hi;
            int span = loEnd - hiEnd;

            // Target whole-pixel adjustment.
            // NOTE ON +0x28 (E1): from here to the placement, GCFixOnePath repurposes E1 as a
            // per-stem PIXEL-COUNT accumulator (not the edge). It is floored/ceiled to hand out the
            // fractional pixel, then the real edges are computed at the end from those counts.
            int u = (fracSum - (Round16(span) >> 16)) + nCount;
            while (u + n < 0) { foreach (var s in path) s.E1 += One; u += n; }
            while (n < u) { foreach (var s in path) s.E1 -= One; u -= n; }
            int remain = u;                              // uVar8: leftover whole pixels, 0..n-1

            // Clump-respecting split point: whole-pixel count implied by span*slope decides how many
            // stems round DOWN vs UP, adjusted so a clump rounds together.
            int whole = Round16(FixMul(span, slope)) >> 16;
            if (whole > 0 && remain > 0)
            {
                int lastClump = path[remain - 1].Clump;
                if (lastClump != remain - 1)
                {
                    int below = 0;
                    while (below < n && path[below].Clump < lastClump) below++;
                    int pick = below;
                    if (whole < remain - below) pick = remain;
                    remain = pick;
                    if (whole < remain - below && (lastClump - pick) < whole) remain = lastClump + 1;
                }
            }

            // Distribute: the first `remain` stems floor their fraction, the rest ceil -- so
            // (n-remain) stems each gain a whole pixel. (E1's low-16 sentinel 0xffff -> exactly 1px.)
            int fracAcc = 0;
            for (int i = 0; i < n; i++)
            {
                GcStem s = path[i];
                if ((s.E1 & 0xffff) == 0xffff) { s.E1 = One; remain++; }
                else if (remain <= i) s.E1 = (s.E1 & unchecked((int)0xffff0000)) + One;   // ceil
                else s.E1 &= unchecked((int)0xffff0000);                                   // floor
                fracAcc += s.Frac;
            }

            // Place the anchor, then propagate along the path preserving each stem's width.
            int spanLeft = span - fracAcc * One - nCount * One;
            if (!first.Done)
            {
                int width = Math.Abs(first.E1 - first.E0);
                int posHi;
                if (!last.Done)
                {
                    int fSum = first.Lo + first.Hi, lSum = last.Lo + last.Hi;
                    int candA = Round16((fSum - spanLeft + width) / 2);
                    int lE0 = last.E0, lE1 = last.E1;
                    int candCentre = Round16(((lE0 - lE1) + lSum + spanLeft) / 2);
                    int candB = candCentre + (fracAcc + nCount) * One;
                    // pick whichever candidate lands the path's two ends closer to their scaled span
                    int errA = Math.Abs(((fracAcc * One - candA * 2 + nCount * One) * 2 - lE1) + lE0 + lSum + fSum + width);
                    int errB = Math.Abs(((lE0 + (candCentre + candB) * -2) - lE1) + lSum + fSum + width);
                    posHi = errB <= errA ? candB : candA;
                }
                else posHi = last.E0 + (fracAcc + nCount) * One;
                first.E0 = posHi - Math.Abs(first.E1 - first.E0);
                first.E1 = posHi;
                first.Done = true;
            }
            // propagate: each subsequent stem sits a fixed distance from the prior, width preserved.
            for (int i = 1; i < n; i++)
            {
                GcStem prev = path[i - 1], s = path[i];
                if (s.Done) break;
                int width = Math.Abs(s.E1 - s.E0);
                int hi = prev.E0 - 0;      // GDI reads the counter node's accumulated gap here; with
                                           // our ordered path the inter-stem gap is prev.E0 - counterGap
                s.E1 = hi;
                s.E0 = hi - width;
                s.Done = true;
            }
            return true;
        }
    }
}
