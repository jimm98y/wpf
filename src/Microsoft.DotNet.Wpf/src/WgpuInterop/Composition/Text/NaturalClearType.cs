// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// WPF's text as DirectWrite renders it: the alpha texture IDWriteGlyphRunAnalysis::CreateAlphaTexture
// hands wpfgfx for a glyph run in DWRITE_RENDERING_MODE_NATURAL, which is what stock WPF draws ideal-
// mode text with (CGlyphRunResource::GetDWriteRenderingMode defers to GetRecommendedRenderingMode,
// and at text sizes that answers NATURAL or NATURAL_SYMMETRIC).
//
// Read out of dwrite.dll rather than inferred:
//
//   GlyphRunAnalysis::GlyphRunAnalysis   the mode's attributes (DAT_18036e030): NATURAL oversamples
//                                        6 across and 1 down, NATURAL_SYMMETRIC 6 and 5.
//   TrueTypeRasterizer::NewTransform     the scaler's mode word: 0x51 / 0x71 -- ClearType, sub-pixel
//                                        positioned, no compatible widths
//                                        (TrueTypeFont.TryGetDWriteFittedOutline).
//   GetGlyphBitmaps                      each glyph is scan-converted ONCE, to a 1-bit bitmap at the
//                                        oversampled resolution, and cached; its position never
//                                        reaches the rasterizer.
//   ComputeGlyphBitmapPositions          ...because the glyph is placed at round(6 x) samples
//                                        (round-half-up), i.e. the pen is quantized to 1/6 pixel.
//   CreateAlphaTexture3x1                the glyphs are ORed into one run bitmap (MergeGlyph1Bit),
//                                        and each output pixel reads the ten samples
//                                        [6p-2, 6p+8) through a 1024-entry table that is exactly a
//                                        SIX-SAMPLE BOX per channel, the channels two samples apart
//                                        (verified on all 1024 entries), then g_AlphaNormalizationTable6x1
//                                        = 0 43 85 128 170 213 255.
//   ApplyFilterImpl<AlphaTextureTarget,5> NATURAL_SYMMETRIC: the same box on each of FIVE sub-rows
//                                        (g_classicPlattFilterRGB carries the identical table), the
//                                        five levels weighted 4:9:10:9:4 and the sum S (0..216)
//                                        through g_AlphaNormalizationTable6x5 = round(S * 255 / 216).
//
// What wpfgfx then does with the texture (contrast, gamma, the fractional pen) is not here; this is
// the texture alone, and it is exact where the fitted outline is.
//

using System;
using System.Collections.Generic;

namespace Microsoft.Wpf.Interop.WebGpu.Composition.Text
{
    internal static class NaturalClearType
    {
        /// <summary>The level ramp after the box, g_AlphaNormalizationTable6x1.</summary>
        internal static ReadOnlySpan<byte> Ramp6x1 => new byte[] { 0, 43, 85, 128, 170, 213, 255 };

        /// <summary>DirectWrite's mode word for the scaler (TrueTypeRasterizer::NewTransform).</summary>
        internal const int NaturalFlags = 0x51, SymmetricFlags = 0x71;

        /// <summary>One glyph's 1-bit bitmap, positioned relative to its own origin.</summary>
        internal sealed class GlyphBits
        {
            /// <summary>Sample column of bit column 0, relative to the glyph origin (six a pixel).</summary>
            public int Left;
            /// <summary>Sub-row of bit row 0, relative to the baseline, y down (nSub a pixel).</summary>
            public int Top;
            public int Width, Height;
            /// <summary>bits[row * Width + column].</summary>
            public bool[] Bits = Array.Empty<bool>();
            public bool IsEmpty => Width == 0 || Height == 0;
        }

        private static readonly GlyphBits s_empty = new();

        /// <summary>The glyph as GetGlyphBitmaps makes it: fitted with DirectWrite's mode word where the
        /// face has a program for it (else the scaled outline), then scan-converted at six samples a
        /// pixel across and <paramref name="nSub"/> rows down.</summary>
        internal static GlyphBits Rasterize(TrueTypeFont font, int glyphId, float pixelsPerEm, int nSub,
                                            bool gridFit = true, int scalerFlags = 0, bool forceGridFit = false)
        {
            // scalerFlags overrides the mode word: GDI+'s glyphs are fitted with word 1 (see GdiPlusText).
            int flags = scalerFlags != 0 ? scalerFlags : nSub > 1 ? SymmetricFlags : NaturalFlags;
            int dropout = 0;
            List<PathFigure> figures;
            // Simulated bold: the outline is emboldened where the scaler is not given the bitmap
            // smear (see EmboldenOutline): NATURAL_SYMMETRIC, and past 50ppem.
            int ppem = (int)MathF.Floor(pixelsPerEm + 0.5f);
            bool outlineBold = font.SynthesizesBold && (nSub > 1 || ppem > 50);
            if (!gridFit || (!forceGridFit && !font.WantsGridFit(pixelsPerEm))
                || !font.TryGetDWriteFittedOutline(glyphId, pixelsPerEm, flags, out figures, out dropout,
                                                   outlineBold ? (x, y, ends) => EmboldenOutline(x, y, ends, ppem) : null))
            {
                dropout = 0;
                if (!font.TryGetScaledOutline(glyphId, pixelsPerEm, out figures)) return s_empty;
            }
            GlyphBits bits = Scan(figures, nSub, dropout);
            return font.SynthesizesBold && !outlineBold ? Embolden(bits, ppem) : bits;
        }

        /// <summary>DirectWrite's simulated bold, fs_ContourScan -> fsc_OverscaleToBold on the 6x
        /// oversampled bitmap (the outline itself is not emboldened). At 24ppem and under, for a
        /// bitmap of at most 48 rows, each right edge of ink is followed down the rows it continues
        /// through (FindNext), the clear run to its right is measured on every row of that chain,
        /// and the whole chain is smeared right by the rounded mean of what each row can spare --
        /// nothing where the gap is 4 samples or less, gap - 4 below 10, the full 6 beyond -- but
        /// never less than 2, and never over ink already there. So a counter or the space between
        /// two close strokes stays open. Above that, EmboldenOverscaleConst: every row smeared by
        /// the full 6. Ported from dwrite.dll (ARM64) instruction by instruction, tables verbatim;
        /// the bitmap is packed the rasterizer's way, MSB first, and padded so no edge is lost.</summary>
        internal static GlyphBits Embolden(GlyphBits g, int ppemY, int overscale = 6)
        {
            if (g.IsEmpty) return g;
            if (ppemY > 24 || g.Height > 48) return Smear(g, overscale);

            const int padLeft = 8;
            int cols = padLeft + g.Width + overscale + 24;
            int rowBytes = (cols + 7) / 8, rows = g.Height;
            cols = rowBytes * 8;
            var src = new byte[rowBytes * rows];
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < g.Width; c++)
                    if (g.Bits[r * g.Width + c])
                    {
                        int x = padLeft + c;
                        src[r * rowBytes + (x >> 3)] |= (byte)(0x80 >> (x & 7));
                    }
            byte[] dst = (byte[])src.Clone();

            int low = (overscale * 0xaa + 0x80) >> 8;             // 4: a gap this small spares nothing
            int minimum = ((overscale * 85 + 0x80) >> 8) & 0xff;   // 2
            int maxGap = overscale + low;                          // 10
            Span<int> chainPtr = stackalloc int[48];
            Span<int> chainBit = stackalloc int[48];

            for (int row = 0; row < rows; row++)
            {
                int rowStart = row * rowBytes;
                int right = -1;                                    // the output byte to the right; -1 = none
                int i = rowBytes - 1;
                while (i >= 0)
                {
                    int cur = rowStart + i;
                    byte b = src[cur];
                    int rightMsb = right < 0 ? 0 : dst[right] >> 7;
                    if (b == 0 || (b == 0xff && (rightMsb == 1 || right < 0))) { right = cur; i--; continue; }
                    int index = (((~(b | dst[cur]) & 0x7f) << 1) | (~rightMsb & 1)) & b;
                    int t = D2E0[index];
                    if (t == 8) { right = cur; i--; continue; }

                    // The chain: this edge, and where it continues on the rows below.
                    int count = 0, gapSum = 0;
                    int ptr = cur, bit = t, chainRow = row, chainStart = rowStart;
                    while (true)
                    {
                        chainPtr[count] = ptr; chainBit[count] = bit;
                        int rowLast = chainStart + rowBytes - 1;
                        int gap = -bit - 1, scan = ptr;
                        bool first = true, found = false;
                        while (gap < maxGap)
                        {
                            if (scan > rowLast) gap = maxGap;
                            else
                            {
                                int v = first ? (D2D1[bit] & src[scan]) : src[scan];
                                first = false;
                                int lz = D3E0[v];
                                found = lz < 8;
                                gap += lz;
                                scan++;
                            }
                            if (found) break;
                        }
                        gapSum += gap <= low ? 0 : gap < maxGap ? gap - low : overscale;
                        count++;
                        if (!FindNext(src, rows, rowBytes, chainRow, overscale, chainStart, ptr, bit, out int nextPtr, out int nextBit))
                            break;
                        ptr = nextPtr; bit = nextBit; chainRow++; chainStart += rowBytes;
                    }

                    int mean = ((gapSum + (count >> 1)) / count) & 0xff;
                    int amount = mean < minimum ? minimum : mean;
                    int last = rowStart + rowBytes - 1;
                    for (int k = 0; k < count; k++, last += rowBytes)
                    {
                        int tk = chainBit[k], p = chainPtr[k];
                        int m = D1B8[amount];
                        int within = m >> (tk + 1), spill = (m << (7 - tk)) & 0xff;
                        int stop = 8;
                        if (within != 0)
                        {
                            stop = D3E0[D2D1[tk] & dst[p]];
                            dst[p] |= (byte)(within & D1B8[stop]);
                        }
                        if (spill != 0 && stop == 8 && p < last)
                            dst[p + 1] |= (byte)(spill & D1B8[D3E0[dst[p + 1]]]);
                    }
                    // Look at this byte again: the smeared edge is no edge now, the next one left is.
                }
            }

            var s = new GlyphBits { Left = g.Left - padLeft, Top = g.Top, Width = cols, Height = rows };
            s.Bits = new bool[cols * rows];
            for (int r = 0; r < rows; r++)
                for (int x = 0; x < cols; x++)
                    s.Bits[r * cols + x] = (dst[r * rowBytes + (x >> 3)] & (0x80 >> (x & 7))) != 0;
            return Crop(s);
        }

        /// <summary>fsc_OverscaleToBold's FindNext: where the right edge at (<paramref name="ptr"/>,
        /// <paramref name="bit"/>) continues on the row below -- straight down, or up to a few samples
        /// left or right while the ink on the two rows stays joined.</summary>
        private static bool FindNext(byte[] a, int rows, int rowBytes, int row, int overscale, int rowStart,
                                     int ptr, int bit, out int nextPtr, out int nextBit)
        {
            nextPtr = 0; nextBit = 0;
            if (rows - 1 <= row) return false;
            int below = ptr + rowBytes;
            bool lastByte = ptr == rowStart + rowBytes - 1;
            int belowNext = lastByte ? -1 : below + 1;
            int after = belowNext < 0 ? 0 : a[belowNext];
            int bv = a[below];
            int up = bit + 1, down = 7 - bit;
            int window = (bv << up) | (after >> down);
            if ((D2C8[bit] & bv) == 0)
            {
                if (((window >> 7) & 1) == 0)
                {
                    // Nothing under the edge or right of it: the edge moved LEFT.
                    int prevVal = 0, belowPrev = -1, curPrev = -1;
                    if (ptr != rowStart) { belowPrev = below - 1; prevVal = a[belowPrev]; curPrev = ptr - 1; }
                    int c = D4E0[((bv >> down) | (prevVal << up)) & 0xff];
                    if (c > 7) return false;
                    int lim = overscale < 9 ? 8 - overscale : 0;
                    if (c < lim) return false;
                    int mask = D2D1[c];
                    int curWin = (((curPrev < 0 ? 0 : a[curPrev]) << up) & 0xff) | (a[ptr] >> down);
                    if ((mask & curWin) != mask) return false;
                    int moved = c + bit - 7;
                    if (moved >= 0) { nextPtr = below; nextBit = moved; }
                    else
                    {
                        if (belowPrev < 0) return false;
                        nextPtr = belowPrev; nextBit = c + bit + 1;
                    }
                    return true;
                }
            }
            else if (((window >> 7) & 1) == 0)
            {
                nextPtr = below; nextBit = bit;             // straight down
                return true;
            }
            // Ink right of the edge on the row below: the edge moved RIGHT, to the end of that run.
            int curAfter = lastByte ? 0 : a[ptr + 1];
            int run = D5E0[window & 0xff];
            if (run == 8) return false;
            int lim2 = overscale < 3 ? 1 : overscale - 2;
            if (run != lim2 && lim2 <= run) return false;
            if ((D1B8[run + 2] & ((a[ptr] << up) | (curAfter >> down)) & 0xff) != 0) return false;
            int nr = run + bit + 1;
            if (nr < 8) { nextPtr = below; nextBit = nr; }
            else
            {
                if (belowNext < 0) return false;
                nextPtr = belowNext; nextBit = run + bit - 7;
            }
            return true;
        }

        /// <summary>DirectWrite's OUTLINE bold, for the cases fsc_OverscaleToBold is not given:
        /// RasterizeInternal only asks for the bitmap smear (fs input +0x8c = 3) with one row a pixel
        /// (NATURAL, not NATURAL_SYMMETRIC), a 6 or 8 times overscale, 1..50ppem and an unrotated
        /// transform. Otherwise -- NATURAL_SYMMETRIC, and anything past 50ppem -- NewTransform's strengths (20 per mille each way, input +0xc2/+0xc4)
        /// reach scl_InitializeScaling, which makes the amounts x = (20 ppem - 10) / 1000 + 1 and
        /// y = (20 ppem - 10) / 1000 whole pixels, and fs__Contour runs fsg_Embold over the fitted
        /// points. With the x amount split (floor half left, the rest right) and one pixel of it,
        /// EmboldPoint takes its simple branch: an edge running down the glyph's right side moves
        /// one pixel right, a left side stays. From two pixels on (past 50ppem) its general branch
        /// moves each edge out along its own unit normal (itrp_Normalize), x by the right or left
        /// amount and y by the lower or upper one. Either way each point goes to where its two
        /// moved edges meet, clamped to the amounts. Ported from dwrite.dll.</summary>
        internal static bool EmboldenOutline(int[] x, int[] y, int[] endPoints, int ppem)
        {
            int amountX = (20 * ppem - 10) / 1000 + 1, amountY = (20 * ppem - 10) / 1000;
            int right = (amountX - (amountX >> 1)) * 64, left = (amountX >> 1) * 64;
            // EmboldPoint's y clamps: the floor half above, the rest below (and added at the end).
            int up = (amountY >> 1) * 64, down = (amountY - (amountY >> 1)) * 64;

            int start = 0;
            foreach (int end in endPoints)
            {
                if (end - start > 1)
                {
                    int px = x[end], py = y[end], cx = x[start], cy = y[start];
                    int nx = x[start + 1], ny = y[start + 1];
                    int sx = cx, sy = cy;                           // the contour's first point, original
                    int idx = start;
                    while (true)
                    {
                        int last = idx;
                        while (nx == cx && ny == cy && last < end)
                        {
                            last++;
                            if (last < end) { nx = x[last + 1]; ny = y[last + 1]; }
                            else { nx = sx; ny = sy; }
                        }
                        EmboldPoint(x, y, idx, last, px, py, cx, cy, nx, ny, amountX == 1, right, left, up, down);
                        int next = last + 1;
                        px = cx; py = cy; cx = nx; cy = ny;
                        if (next > end) break;
                        idx = next;
                        if (next < end) { nx = x[next + 1]; ny = y[next + 1]; }
                        else { nx = sx; ny = sy; }
                    }
                }
                start = end + 1;
            }
            return true;
        }

        /// <summary>EmboldPoint (dwrite.dll): the point (cx, cy) between the edges from (px, py) and
        /// to (nx, ny), all ORIGINAL positions, written to points first..last.</summary>
        private static void EmboldPoint(int[] xs, int[] ys, int first, int last, int px, int py, int cx, int cy,
                                        int nx, int ny, bool simple, int right, int left, int up, int down)
        {
            // Contour direction flag 0: y up, clockwise outer contours, so a right side runs down.
            int c1x, c1y, c2x, c2y;
            if (simple)
            {
                int inDown = py - cy;                               // > 0: the incoming edge runs down
                int outDown = cy - ny;                              // > 0: the outgoing edge runs down
                c1x = inDown >= 1 ? cx + right : cx; c1y = cy;      // the point on the moved incoming edge
                c2x = outDown > 0 ? cx + right : cx; c2y = cy;      // ... on the moved outgoing edge
            }
            else
            {
                // Each edge out along its unit normal (2.14, taken to 2.6), scaled by the amount on
                // that side: (component * amount + 32) >> 6.
                TrueTypeInterpreter.Normalize(py - cy, cx - px, out int inX, out int inY);
                TrueTypeInterpreter.Normalize(cy - ny, nx - cx, out int outX, out int outY);
                inX >>= 8; inY >>= 8; outX >>= 8; outY >>= 8;
                int inDx = (inX * (inX >= 1 ? right : left) + 0x20) >> 6;
                int inDy = (inY * (inY < 0 ? down : up) + 0x20) >> 6;
                int outDx = (outX * (outX >= 1 ? right : left) + 0x20) >> 6;
                int outDy = (outY * (outY < 0 ? down : up) + 0x20) >> 6;
                px += inDx; py += inDy; c1x = cx + inDx; c1y = cy + inDy;
                nx += outDx; ny += outDy; c2x = cx + outDx; c2y = cy + outDy;
            }
            int X, Y;
            if (c1x == c2x && c1y == c2y) { X = c2x; Y = c2y; }
            else
            {
                int ax = c1x - px, ay = c1y - py;                   // the incoming edge, as moved
                int bx = nx - c2x, by = ny - c2y;                   // the outgoing edge, as moved
                int num, den;
                bool solved = true;
                X = 0; Y = 0;
                if (ay == 0)
                {
                    X = c2x; Y = py;
                    if (bx != 0) { num = c2y - py; den = -by; }
                    else { solved = false; num = den = 0; }
                }
                else if (ax == 0)
                {
                    Y = c2y; X = px;
                    if (by != 0) { num = c2x - px; den = -bx; }
                    else { solved = false; num = den = 0; }
                }
                else if (Math.Abs(ax) < Math.Abs(ay))
                {
                    num = RoundDiv((long)(c2y - py) * ax, ay) - c2x + px;
                    den = bx - RoundDiv((long)by * ax, ay);
                }
                else
                {
                    num = c2y - RoundDiv((long)(c2x - px) * ay, ax) - py;
                    den = RoundDiv((long)bx * ay, ax) - by;
                }
                if (solved)
                {
                    if (Math.Abs(den) < 0x11) { Y = (c1y + c2y) >> 1; X = (c1x + c2x) >> 1; }
                    else
                    {
                        X = RoundDivSat((long)bx * num, den) + c2x;
                        Y = RoundDivSat((long)by * num, den) + c2y;
                    }
                }
            }
            int x0 = xs[first], y0 = ys[first];
            if (right < X - x0) X = x0 + right;
            if (X - x0 < -left) X = x0 - left;
            int yy = Y;
            if (Y - y0 < -down) yy = y0 - down;
            if (up < Y - y0) yy = down + y0;
            X += left; yy += down;
            for (int i = first; i <= last; i++) { xs[i] = X; ys[i] = yy; }
        }

        /// <summary>The scaler's rounded division: half the divisor added with the quotient's sign,
        /// then truncated.</summary>
        private static int RoundDiv(long num, int den)
        {
            long half = den / 2;
            if ((num < 0) != (den < 0)) half = -half;
            return den == 0 ? 0 : (int)((num + half) / den);
        }

        private static int RoundDivSat(long num, int den)
        {
            long half = den / 2;
            if ((num < 0) != (den < 0)) half = -half;
            num += half;
            if (den == 0) return num < 0 ? int.MinValue : int.MaxValue;
            return (int)(num / den);
        }

        private static GlyphBits Crop(GlyphBits g)
        {
            int c0 = g.Width, c1 = -1, r0 = g.Height, r1 = -1;
            for (int r = 0; r < g.Height; r++)
                for (int c = 0; c < g.Width; c++)
                    if (g.Bits[r * g.Width + c])
                    {
                        if (c < c0) c0 = c; if (c > c1) c1 = c;
                        if (r < r0) r0 = r; if (r > r1) r1 = r;
                    }
            if (c1 < 0) return s_empty;
            var o = new GlyphBits { Left = g.Left + c0, Top = g.Top + r0, Width = c1 - c0 + 1, Height = r1 - r0 + 1 };
            o.Bits = new bool[o.Width * o.Height];
            for (int r = 0; r < o.Height; r++)
                Array.Copy(g.Bits, (r0 + r) * g.Width + c0, o.Bits, r * o.Width, o.Width);
            return o;
        }

        // fsc_OverscaleToBold's tables, dwrite.dll .rdata: D1B8 the left masks (n high bits), D2D1
        // the bits right of a position, D2C8 a position's own bit, D2E0 the position of the lowest
        // set bit, D3E0 of the highest, D4E0 the nearest set pixel left of a window's end, D5E0 a
        // leading run of ones less one (8 = none).
        private static readonly byte[] D1B8 =
        {
            0, 128, 192, 224, 240, 248, 252, 254, 255,
        };
        private static readonly byte[] D2D1 =
        {
            127, 63, 31, 15, 7, 3, 1, 0,
        };
        private static readonly byte[] D2C8 =
        {
            128, 64, 32, 16, 8, 4, 2, 1, 255,
        };
        private static readonly byte[] D2E0 =
        {
            8, 7, 6, 7, 5, 7, 6, 7, 4, 7, 6, 7, 5, 7, 6, 7, 3, 7, 6, 7, 5, 7, 6, 7, 4, 7, 6, 7, 5, 7, 6, 7,
            2, 7, 6, 7, 5, 7, 6, 7, 4, 7, 6, 7, 5, 7, 6, 7, 3, 7, 6, 7, 5, 7, 6, 7, 4, 7, 6, 7, 5, 7, 6, 7,
            1, 7, 6, 7, 5, 7, 6, 7, 4, 7, 6, 7, 5, 7, 6, 7, 3, 7, 6, 7, 5, 7, 6, 7, 4, 7, 6, 7, 5, 7, 6, 7,
            2, 7, 6, 7, 5, 7, 6, 7, 4, 7, 6, 7, 5, 7, 6, 7, 3, 7, 6, 7, 5, 7, 6, 7, 4, 7, 6, 7, 5, 7, 6, 7,
            0, 7, 6, 7, 5, 7, 6, 7, 4, 7, 6, 7, 5, 7, 6, 7, 3, 7, 6, 7, 5, 7, 6, 7, 4, 7, 6, 7, 5, 7, 6, 7,
            2, 7, 6, 7, 5, 7, 6, 7, 4, 7, 6, 7, 5, 7, 6, 7, 3, 7, 6, 7, 5, 7, 6, 7, 4, 7, 6, 7, 5, 7, 6, 7,
            1, 7, 6, 7, 5, 7, 6, 7, 4, 7, 6, 7, 5, 7, 6, 7, 3, 7, 6, 7, 5, 7, 6, 7, 4, 7, 6, 7, 5, 7, 6, 7,
            2, 7, 6, 7, 5, 7, 6, 7, 4, 7, 6, 7, 5, 7, 6, 7, 3, 7, 6, 7, 5, 7, 6, 7, 4, 7, 6, 7, 5, 7, 6, 7,
        };
        private static readonly byte[] D3E0 =
        {
            8, 7, 6, 6, 5, 5, 5, 5, 4, 4, 4, 4, 4, 4, 4, 4, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3,
            2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2,
            1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
            1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        };
        private static readonly byte[] D4E0 =
        {
            8, 8, 6, 8, 5, 5, 6, 8, 4, 4, 6, 4, 5, 5, 6, 8, 3, 3, 6, 3, 5, 5, 6, 3, 4, 4, 6, 4, 5, 5, 6, 8,
            2, 2, 6, 2, 5, 5, 6, 2, 4, 4, 6, 4, 5, 5, 6, 2, 3, 3, 6, 3, 5, 5, 6, 3, 4, 4, 6, 4, 5, 5, 6, 8,
            1, 1, 6, 1, 5, 5, 6, 1, 4, 4, 6, 4, 5, 5, 6, 1, 3, 3, 6, 3, 5, 5, 6, 3, 4, 4, 6, 4, 5, 5, 6, 1,
            2, 2, 6, 2, 5, 5, 6, 2, 4, 4, 6, 4, 5, 5, 6, 2, 3, 3, 6, 3, 5, 5, 6, 3, 4, 4, 6, 4, 5, 5, 6, 8,
            0, 0, 6, 0, 5, 5, 6, 0, 4, 4, 6, 4, 5, 5, 6, 0, 3, 3, 6, 3, 5, 5, 6, 3, 4, 4, 6, 4, 5, 5, 6, 0,
            2, 2, 6, 2, 5, 5, 6, 2, 4, 4, 6, 4, 5, 5, 6, 2, 3, 3, 6, 3, 5, 5, 6, 3, 4, 4, 6, 4, 5, 5, 6, 0,
            1, 1, 6, 1, 5, 5, 6, 1, 4, 4, 6, 4, 5, 5, 6, 1, 3, 3, 6, 3, 5, 5, 6, 3, 4, 4, 6, 4, 5, 5, 6, 1,
            2, 2, 6, 2, 5, 5, 6, 2, 4, 4, 6, 4, 5, 5, 6, 2, 3, 3, 6, 3, 5, 5, 6, 3, 4, 4, 6, 4, 5, 5, 6, 8,
        };
        private static readonly byte[] D5E0 =
        {
            8, 8, 6, 8, 5, 5, 6, 8, 4, 4, 4, 4, 5, 5, 6, 8, 3, 3, 3, 3, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 6, 8,
            2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 3, 3, 3, 3, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 6, 8,
            1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
            2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 3, 3, 3, 3, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 6, 8,
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
            1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
            2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 3, 3, 3, 3, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 6, 8,
        };

        /// <summary>DirectWrite's simulated bold: the outline is left alone and the oversampled
        /// bitmap is smeared, every set sample also setting the <paramref name="samples"/> to its
        /// right (fs_ContourScan -> fsc_OverscaleToBold -> EmboldenOverscaleConst, OR-ing each row
        /// with itself shifted by one sample, overscale times). A stem gains exactly one pixel, on
        /// its right, and the left edge and every row stay where they were.</summary>
        internal static GlyphBits Smear(GlyphBits g, int samples)
        {
            if (g.IsEmpty) return g;
            var s = new GlyphBits { Left = g.Left, Top = g.Top, Width = g.Width + samples, Height = g.Height };
            s.Bits = new bool[s.Width * s.Height];
            for (int r = 0; r < g.Height; r++)
            {
                int run = -1;   // samples still to set after the last set one
                for (int c = 0; c < s.Width; c++)
                {
                    if (c < g.Width && g.Bits[r * g.Width + c]) run = samples;
                    else if (run >= 0) run--;
                    s.Bits[r * s.Width + c] = run >= 0;
                }
            }
            return s;
        }

        private static readonly bool s_noDropout = Environment.GetEnvironmentVariable("WPF_NCT_NODROPOUT") == "1";

        /// <summary>A glyph turned a quarter clockwise (GDI+'s sideways glyph in vertical text): the
        /// fit the scaler makes before its post-transform, the figures mapped (x, y) -> (-y, x) --
        /// glyph up to device right, the advance down -- and scanned as any other.</summary>
        internal static GlyphBits RasterizeQuarterTurn(TrueTypeFont font, int glyphId, float pixelsPerEm, int scalerFlags)
        {
            if (!font.TryGetDWriteFittedOutline(glyphId, pixelsPerEm, scalerFlags, out List<PathFigure> figures, out int dropout))
            {
                dropout = 0;
                if (!font.TryGetScaledOutline(glyphId, pixelsPerEm, out figures)) return s_empty;
            }
            static System.Numerics.Vector2 T(System.Numerics.Vector2 p) => new(-p.Y, p.X);
            var turned = new List<PathFigure>(figures.Count);
            foreach (PathFigure f in figures)
            {
                var nf = new PathFigure(T(f.Start)) { Closed = f.Closed };
                foreach (PathSegment sg in f.Segments)
                    nf.Segments.Add(sg switch
                    {
                        LineSegment l => new LineSegment(T(l.Point)),
                        QuadraticBezierSegment q => new QuadraticBezierSegment(T(q.Control), T(q.Point)),
                        CubicBezierSegment c => new CubicBezierSegment(T(c.Control1), T(c.Control2), T(c.Point)),
                        _ => sg,
                    });
                turned.Add(nf);
            }
            return Scan(turned, 1, dropout);
        }

        /// <summary>A glyph under a turned transform as DirectWrite's scaler gives it unfitted: the
        /// design outline in font units times 64 through the em-scaled matrix over upem
        /// (GdiPlusText.UnfittedTurnedPoints: NewTransform @18006c460 sets +0xd6 for a glyph that
        /// is not grid-fitted, scl_InitializeScaling's param_19), each product rounded on its own,
        /// then scanned 6x1 as any other. <paramref name="em"/> and the matrix are the world em and
        /// the world-to-device linear part.</summary>
        internal static GlyphBits RasterizeTransformedUnfitted(TrueTypeFont font, int glyphId, float em,
                                                               float m11, float m12, float m21, float m22)
        {
            List<PathFigure> turned = GdiPlusText.TransformedOutline(font, glyphId, em, m11, m12, m21, m22, 0f, 0f);
            if (turned.Count == 0) return s_empty;
            int dmode = GdiPlusText.TurnedDropout(font, em, m21, m22, GdiPlusText.NaturalScalerWord);
            GlyphBits bits = Scan(turned, 1, dmode);
            // A simulated bold is the scan's own (fs_ContourScan -> fsc_OverscaleToBold), as upright.
            return bits;
        }

        private static GlyphBits Scan(List<PathFigure> figures, int nSub, int dropout)
        {
            if (s_noDropout) dropout = 0;
            if (figures.Count == 0) return s_empty;

            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            void Take(System.Numerics.Vector2 p)
            {
                if (p.X < minX) minX = p.X; if (p.X > maxX) maxX = p.X;
                if (p.Y < minY) minY = p.Y; if (p.Y > maxY) maxY = p.Y;
            }
            foreach (PathFigure f in figures)
            {
                Take(f.Start);
                foreach (PathSegment s in f.Segments)
                    switch (s)
                    {
                        case LineSegment l: Take(l.Point); break;
                        case QuadraticBezierSegment q: Take(q.Control); Take(q.Point); break;
                        case CubicBezierSegment c: Take(c.Control1); Take(c.Control2); Take(c.Point); break;
                    }
            }
            if (minX > maxX) return s_empty;

            // A pixel of room on every side: dropout control can light a sample just outside the
            // outline's own box.
            int originX = (int)MathF.Floor(minX) - 1, originY = (int)MathF.Floor(minY) - 1;
            int width = (int)MathF.Ceiling(maxX) + 1 - originX, height = (int)MathF.Ceiling(maxY) + 1 - originY;
            bool[]? bits = PathRasterizer.ScanGlyphBits(new PathGeometry(FillRule.NonZero, figures),
                                                        originX, originY, width, height, nSub, dropout);
            if (bits is null) return s_empty;

            // Crop to the ink, so runs merge only what is there.
            int cols = width * 6, rows = height * nSub;
            int c0 = cols, c1 = -1, r0 = rows, r1 = -1;
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < cols; c++)
                    if (bits[r * cols + c])
                    {
                        if (c < c0) c0 = c; if (c > c1) c1 = c;
                        if (r < r0) r0 = r; if (r > r1) r1 = r;
                    }
            if (c1 < 0) return s_empty;
            var g = new GlyphBits
            {
                Left = originX * 6 + c0, Top = originY * nSub + r0,
                Width = c1 - c0 + 1, Height = r1 - r0 + 1,
            };
            g.Bits = new bool[g.Width * g.Height];
            for (int r = 0; r < g.Height; r++)
                Array.Copy(bits, (r0 + r) * cols + c0, g.Bits, r * g.Width, g.Width);
            return g;
        }

        /// <summary>The glyph as DWRITE_RENDERING_MODE_GDI_CLASSIC (WPF's Display formatting mode)
        /// makes it. NewTransform hands the scaler word 3 there -- ClearType WITH compatible widths --
        /// which is GDI's own ClearType fit, the one <see cref="TrueTypeFont.TryGetHintedOutline"/>
        /// reproduces for WinForms; the scan and the filter are the natural mode's (6x1).</summary>
        internal static GlyphBits RasterizeGdiClassic(TrueTypeFont font, int glyphId, float pixelsPerEm)
        {
            if (!TryGetGdiClassicOutline(font, glyphId, pixelsPerEm, out List<PathFigure>? figures, out int dropout))
                return s_empty;
            // GDI_CLASSIC is scanned 6x1 too, so a simulated bold is the bitmap smear up to 50ppem.
            GlyphBits bits = Scan(figures, 1, dropout);
            return font.SynthesizesBold ? Embolden(bits, (int)MathF.Floor(pixelsPerEm + 0.5f)) : bits;
        }

        /// <summary>The GDI_CLASSIC fit itself (see <see cref="RasterizeGdiClassic"/>), in device pixels.</summary>
        internal static bool TryGetGdiClassicOutline(TrueTypeFont font, int glyphId, float pixelsPerEm,
                                                     out List<PathFigure> figures, out int dropout)
        {
            bool savedSub = TrueTypeFont.SubpixelFitting, savedCt = TrueTypeFont.ClearTypeRendering;
            bool? savedSym = TrueTypeInterpreter.SymmetricAnswerOverride;
            bool savedMove = TrueTypeInterpreter.DWriteMovePoint;
            dropout = 0;
            try
            {
                TrueTypeFont.SubpixelFitting = true;
                TrueTypeFont.ClearTypeRendering = true;
                TrueTypeInterpreter.SymmetricAnswerOverride = false;
                TrueTypeInterpreter.DWriteMovePoint = true;
                if (!((IHintedGlyphFont)font).TryGetHintedOutline(glyphId, pixelsPerEm, out List<PathFigure>? got) || got is null)
                {
                    figures = new List<PathFigure>();
                    return false;
                }
                figures = got;
                dropout = Math.Max(0, font.GlyphDropout(glyphId, pixelsPerEm));
                return true;
            }
            finally
            {
                TrueTypeFont.SubpixelFitting = savedSub;
                TrueTypeFont.ClearTypeRendering = savedCt;
                TrueTypeInterpreter.SymmetricAnswerOverride = savedSym;
                TrueTypeInterpreter.DWriteMovePoint = savedMove;
            }
        }

        /// <summary>DirectWrite's rounding of a float to an int in GlyphRunAnalysis: truncate, then
        /// step away from zero when the rest is at least a half.</summary>
        internal static int RoundHalfAway(float v)
        {
            int i = (int)v;
            if (v < 0f) { if (0.5f < i - v) i--; }
            else if (i - v <= -0.5f) i++;
            return i;
        }

        /// <summary>The vertical weights of the symmetric filter, one per sub-row.</summary>
        private static ReadOnlySpan<byte> SymmetricWeights => new byte[] { 4, 9, 10, 9, 4 };

        /// <summary>The alpha texture of a run in DWRITE_RENDERING_MODE_NATURAL (<paramref name="nSub"/>
        /// 1) or NATURAL_SYMMETRIC (5), as CreateAlphaTexture3x1 makes it: three bytes a pixel, rows
        /// <paramref name="top"/>.. from the baseline, pixels <paramref name="left"/>.. from the run
        /// origin. <paramref name="xs"/> and <paramref name="ys"/> are each glyph's origin relative to
        /// the run's, in pixels; the glyphs' bitmaps must have been made with the same sub-row count.</summary>
        internal static byte[] RunTexture(IReadOnlyList<GlyphBits> glyphs, IReadOnlyList<float> xs,
                                          IReadOnlyList<float> ys, out int left, out int top,
                                          out int width, out int height, int nSub = 1)
            => Build(glyphs, xs, ys, nSub, texture: true, out left, out top, out width, out height);

        /// <summary>Where <see cref="RunTexture"/>'s texture would lie, without making it.</summary>
        internal static void RunBounds(IReadOnlyList<GlyphBits> glyphs, IReadOnlyList<float> xs,
                                       IReadOnlyList<float> ys, int nSub, out int left, out int top,
                                       out int width, out int height)
            => Build(glyphs, xs, ys, nSub, texture: false, out left, out top, out width, out height);

        private static byte[] Build(IReadOnlyList<GlyphBits> glyphs, IReadOnlyList<float> xs,
                                    IReadOnlyList<float> ys, int nSub, bool texture, out int left, out int top,
                                    out int width, out int height)
        {
            // Samples and rows of every glyph, in the run's frame.
            int n = glyphs.Count;
            var sx = new int[n]; var sy = new int[n];
            int s0 = int.MaxValue, s1 = int.MinValue, y0 = int.MaxValue, y1 = int.MinValue;
            for (int i = 0; i < n; i++)
            {
                GlyphBits g = glyphs[i];
                if (g.IsEmpty) continue;
                sx[i] = RoundHalfAway(6f * xs[i]) + g.Left;
                sy[i] = RoundHalfAway(nSub * ys[i]) + g.Top;
                s0 = Math.Min(s0, sx[i]); s1 = Math.Max(s1, sx[i] + g.Width);
                y0 = Math.Min(y0, sy[i]); y1 = Math.Max(y1, sy[i] + g.Height);
            }
            if (s0 > s1) { left = top = width = height = 0; return Array.Empty<byte>(); }

            // Every pixel whose ten-sample window [6p-2, 6p+8) reaches ink; whole pixel rows
            // (GlyphRunAnalysis rounds the sub-row bounds out to a multiple of the oversample).
            left = FloorDiv(s0 - 7, 6);
            int right = FloorDiv(s1 + 1, 6) + 1;
            int r0 = FloorDiv(y0, nSub), r1 = FloorDiv(y1 + nSub - 1, nSub);
            top = r0; height = r1 - r0; width = right - left;
            y0 = r0 * nSub;
            if (!texture) return Array.Empty<byte>();
            int subRows = height * nSub;

            // The merged bitmap, starting at sample 6*left - 2 as the texture's does.
            int b0 = 6 * left - 2, cols = 6 * width + 4;
            var bits = new bool[subRows * cols];
            for (int i = 0; i < n; i++)
            {
                GlyphBits g = glyphs[i];
                if (g.IsEmpty) continue;
                for (int r = 0; r < g.Height; r++)
                {
                    int row = sy[i] + r - y0;
                    for (int c = 0; c < g.Width; c++)
                        if (g.Bits[r * g.Width + c]) bits[row * cols + sx[i] + c - b0] = true;
                }
            }

            ReadOnlySpan<byte> ramp = Ramp6x1;
            ReadOnlySpan<byte> weights = SymmetricWeights;
            var tex = new byte[width * height * 3];
            for (int row = 0; row < height; row++)
                for (int p = 0; p < width; p++)
                    for (int k = 0; k < 3; k++)
                    {
                        int sum = 0;
                        for (int sr = 0; sr < nSub; sr++)
                        {
                            int level = 0, at = (row * nSub + sr) * cols + 6 * p + 2 * k;
                            for (int j = 0; j < 6; j++) if (bits[at + j]) level++;
                            sum += nSub == 1 ? level : weights[sr] * level;
                        }
                        tex[(row * width + p) * 3 + k] = nSub == 1 ? ramp[sum] : (byte)((sum * 255 + 108) / 216);
                    }
            return tex;
        }

        private static int FloorDiv(int a, int b) => a >= 0 ? a / b : -((-a + b - 1) / b);
    }
}
