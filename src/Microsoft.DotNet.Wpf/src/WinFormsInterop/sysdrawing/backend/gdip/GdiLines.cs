// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// GDI's cosmetic (one-pixel) lines: the grid-intersection-quantised line of win32kfull's bLines
// @1401d60d0, ported from the binary. A line between two 28.4 points lights the pixels whose
// diamonds it crosses, its last pixel excluded:
//
//   the line is flipped to run left to right (FL_FLIP_H 0x20), downwards (FL_FLIP_V 8) and
//   x-major (FL_FLIP_D 5; FL_FLIP_SLOPE_ONE 0x10 at 45 degrees); gaflRound (14034be00) then says
//   whether x = 1/2 and y = 1/2 round down (0x80, 0x8000);
//   the first and last pixel come from where the line enters and leaves the diamonds of its end
//   points, the minor coordinate from the remainder dM (N0 + 1/2) - M0 dN;
//   the pixels are emitted as strips (vStripSolidHorizontal / Vertical / Diagonal @1401d5230 /
//   @1401d53a0 / @1401d5140): runs along the major axis, or diagonal runs once the slope passes
//   1/2 (FL_FLIP_HALF 2).
//
// EngStrokePath sends solid COPYPEN lines through vDrawLine @1401d6fb0 (bGIQtoIntegerLine
// @1401d6bc0 and the vLine32Octant* Bresenham loops) instead; that is the same quantisation, which
// is why only bLines is ported.
//
// Styled lines (PS_DOT ...) walk the style array in style units: every pixel advances the style
// position by the step of its major axis (xStyleStep / yStyleStep), the array scaled by
// denStyleStep (1, 1 and 3 here, the DIB engine's defaults); the on/off state flips at each entry.
//

using System.Collections.Generic;

namespace System.Drawing.WebGpuBackend.Gdip
{
    internal static class GdiLines
    {
        // gaflRound @14034be00, by (fl >> 2) & 7.
        static readonly int[] RoundTable = { 0x8080, 0x8080, 0x80, 0x8000, 0x8000, -1, 0x80, -1 };

        /// <summary>A cosmetic style: the dash array (style units, scaled by denStyleStep),
        /// whether it starts with a gap, and the running position (spNext) that carries from one
        /// segment to the next.</summary>
        internal sealed class Style
        {
            public int[] Array, Reversed;  // the entries times denStyleStep, and backwards
            public int Total;              // their sum
            public int StepX = 1, StepY = 1, Den = 3;
            public bool StartGap;          // LA_STARTGAP
            public int Next;               // spNext: the style position, carried line to line
            internal int StepOfLine;

            /// <summary>bStrokeCosmetic's style state from a LINEATTRS style array (style units)
            /// and its elStyleState.</summary>
            public static Style From(int[] entries, bool startGap, int styleState, int stepX = 1, int stepY = 1, int den = 3)
            {
                int n = Math.Min(entries.Length, 16);
                var st = new Style { StepX = stepX, StepY = stepY, Den = den, StartGap = startGap, Array = new int[n], Reversed = new int[n] };
                for (int i = 0; i < n; i++)
                {
                    st.Array[i] = entries[i] * den;
                    st.Reversed[n - 1 - i] = entries[i] * den;
                    st.Total += entries[i] * den;
                }
                int next = (styleState >> 16) * den + (styleState & 0xffff);
                if (next < 0) next = 0;
                if (2 * st.Total <= next) next %= 2 * st.Total;
                st.Next = next;
                return st;
            }

            /// <summary>LA_ALTERNATE: every other pixel.</summary>
            public static Style Alternate(int styleState)
                => new Style { Array = new[] { 1 }, Reversed = new[] { 1 }, Total = 1, Den = 1, Next = (styleState >> 16) & 1 };

            /// <summary>The style state written back to the LINEATTRS.</summary>
            public int State => ((Next / Den) << 16) | ((Next % Den) & 0xffff);
        }

        /// <summary>The pixels of the cosmetic line from (ax, ay) to (bx, by) (28.4), its last
        /// pixel excluded, with whether each is lit by the style (always, without one).</summary>
        public static void Line(int ax, int ay, int bx, int by, Style style, List<(int X, int Y)> on)
        {
            uint M0 = (uint)ax, dM = (uint)bx, N0 = (uint)ay, dN = (uint)by;
            int fl = 0;
            if ((int)dM < (int)M0)
            {
                (M0, dM) = (dM, M0);
                (N0, dN) = (dN, N0);
                fl |= 0x20;
            }
            if ((int)dN < (int)N0) { N0 = (uint)-(int)N0; dN = (uint)-(int)dN; fl |= 8; }
            dM -= M0;
            if ((int)dM < 0) return;
            dN -= N0;
            if ((int)dN < 0) return;
            uint major = dM, minor = dN;
            uint m0 = M0, n0 = N0;
            if (major <= minor)
            {
                if (minor == major) fl |= 0x10;
                else
                {
                    (major, minor) = (minor, major);
                    (m0, n0) = (n0, m0);
                    fl |= 5;
                }
            }
            fl |= RoundTable[(fl >> 2) & 7];
            int x = (int)m0 >> 4, y = (int)n0 >> 4;
            uint M = m0 & 15, N = n0 & 15;
            long gamma = (long)(N + 8) * (int)major - (long)M * (int)minor;
            if ((fl & 0x8000) != 0) gamma--;
            gamma >>= 4;
            long beta = ~gamma;
            uint N1 = (minor + N) & 15, M1 = (major + M) & 15;
            int x1 = (int)((major + M) >> 4);
            int x0, y0;
            int rd = (fl & 0x80) != 0 ? 1 : 0;
            if ((fl & 0x20) == 0)
            {
                int last = x1 - 1;
                if (M1 != 0)
                {
                    if (N1 == 0) { if ((int)(M1 - rd) + 8 > 15) last = x1; }
                    else if (Math.Abs((int)N1 - 8) <= (int)M1) last = x1;
                }
                x1 = last;
                bool skipX0 = false;
                if ((fl & 0x90) == 0x90)
                {
                    if (M1 != 0 && N1 == M1 + 8) x1--;
                    if (M != 0 && N == M + 8) skipX0 = true;
                }
                x0 = 0;
                if (!skipX0 && M != 0)
                {
                    if (N == 0) x0 = (int)(M - rd) + 8 > 15 ? 1 : 0;
                    else x0 = Math.Abs((int)N - 8) <= (int)M ? 1 : 0;
                }
                y0 = 0;
                if (gamma >= 0) y0 = (uint)(major - (minor & (uint)-x0)) <= (uint)gamma ? 1 : 0;
            }
            else
            {
                if (N1 == 0) { if ((int)(M1 - rd) + 8 > 15) x1++; }
                else if (Math.Abs((int)N1 - 8) + (int)M1 > 16) x1++;
                long add;
                bool jump = false;
                if ((fl & 0x90) == 0x10)
                {
                    if (N1 != 0 && M1 == N1 + 8) x1++;
                    if (N != 0 && M == N + 8) jump = true;
                }
                if (jump) { x0 = 2; add = minor; }
                else
                {
                    x0 = 1;
                    if (N == 0)
                    {
                        bool b = (int)(M - rd) + 8 < 16;
                        add = b ? 0 : minor;
                        x0 = b ? 1 : 2;
                    }
                    else
                    {
                        add = 0;
                        if (Math.Abs((int)N - 8) + (int)M > 16) { x0 = 2; add = minor; }
                    }
                }
                y0 = 0;
                long t = gamma + add;
                if (t >= 0)
                {
                    if ((t >> 32) < 1 && (uint)t < major * 2 - minor) { if (major - minor <= (uint)t) y0 = 1; }
                    else y0 = 2;
                }
            }
            if (x1 < x0) return;
            // Styling (bLines' FL_STYLED block): the style position at the first pixel drawn, the
            // per-step advances along the axis that styles the line, the on/off mask.
            int[] arr = null;
            int sIdx = 0, sRem = 0, dMaj = 0, dMin = 0, dDiag = 0;
            bool gap = false;
            if (style != null)
            {
                bool d = (fl & 5) != 0;
                int majStep = d ? style.StepY : style.StepX, minStep = d ? style.StepX : style.StepY;
                bool byMinor = majStep != minStep && (ulong)(uint)majStep * major < (ulong)(uint)minStep * minor;
                int count, offset;
                if (byMinor)
                {
                    ulong v = (ulong)(gamma + (long)x1 * minor);
                    int q = (int)(v / major);
                    dMaj = 0; dMin = minStep; dDiag = minStep;
                    count = q - y0;
                    offset = (fl & 0x20) == 0 ? 0 : q - y0 + 1;
                    style.StepOfLine = minStep;
                }
                else
                {
                    dMaj = majStep; dMin = 0; dDiag = majStep;
                    count = x1 - x0;
                    offset = (fl & 0x20) == 0 ? 0 : x1 - x0 + 1;
                    style.StepOfLine = majStep;
                }
                int step = style.StepOfLine;
                uint total2 = (uint)(2 * style.Total);
                uint start = (uint)(style.Next + step * offset);
                uint end = (uint)(style.Next + (count + 1) * step);
                if (total2 <= start) start %= total2;
                if (total2 <= end) end %= total2;
                style.Next = (int)end;
                arr = (fl & 0x20) == 0 ? style.Array : style.Reversed;
                gap = (fl & 0x20) == 0 ? style.StartGap : !style.StartGap;
                int pos = (int)start;
                if ((fl & 0x20) != 0)
                {
                    pos = -pos;
                    if (pos < 0) pos += (int)total2;
                }
                if (style.Total <= pos)
                {
                    pos -= style.Total;
                    if ((arr.Length & 1) != 0) gap = !gap;
                }
                while (arr[sIdx] <= pos) { pos -= arr[sIdx]; sIdx++; }
                sRem = arr[sIdx] - pos;
                if ((sIdx & 1) != 0) gap = !gap;
            }
            // The first pixel, back in device space.
            int X = x0 + x, Y = y0 + y;
            if ((fl & 5) != 0) { X = y0 + y; Y = x0 + x; }
            if ((fl & 8) != 0) Y = -Y;
            uint dMs = major, dNs = minor;
            int iy0 = y0;
            if (dMs < dNs << 1)
            {
                fl |= 2;
                beta = gamma - dMs;
                dNs = dMs - dNs;
                iy0 = x0 - y0;
            }
            int cPels = x1 - x0 + 1;
            var runs = new List<int>();
            if (dNs == 0) runs.Add(cPels);
            else
            {
                ulong t = (ulong)(beta + (long)(iy0 + 1) * dMs);
                ulong q = t / dNs;
                uint r = (uint)(t - q * dNs);
                int run = (int)q - x0 + 1;
                uint whole = dMs / dNs, frac = dMs - whole * dNs;
                while (cPels - run > 0)
                {
                    runs.Add(run);
                    cPels -= run;
                    r += frac;
                    run = (int)whole;
                    if (dNs <= r) { r -= dNs; run++; }
                }
                runs.Add(cPels);
            }
            // The strips: along the major axis a pixel at a time and across it after each run; or,
            // past a slope of 1/2, diagonal runs with an axial step between them.
            int yStep = (fl & 8) != 0 ? -1 : 1;
            bool vertical = (fl & 3) == 1, diagonal = (fl & 2) != 0, dflip = (fl & 5) != 0;
            int px = X, py = Y;
            void Lit(int x, int y) { if (!gap) on.Add((x, y)); }
            void Advance(int by)
            {
                if (arr == null) return;
                sRem -= by;
                if (sRem < 1)
                {
                    if (++sIdx >= arr.Length) sIdx = 0;
                    gap = !gap;
                    sRem += arr[sIdx];
                }
            }
            foreach (int len in runs)
            {
                if (diagonal)
                {
                    for (int i = 0; i < len; i++)
                    {
                        if (i > 0) { Advance(dDiag); px++; py += yStep; }
                        Lit(px, py);
                    }
                    Advance(dMaj);
                    if (!dflip) px++; else py += yStep;
                }
                else if (vertical)
                {
                    for (int i = 0; i < len; i++) { Lit(px, py); py += yStep; Advance(dMaj); }
                    px++;
                    Advance(dMin);
                }
                else
                {
                    for (int i = 0; i < len; i++) { Lit(px, py); px++; Advance(dMaj); }
                    py += yStep;
                    Advance(dMin);
                }
            }
        }
    }
}
