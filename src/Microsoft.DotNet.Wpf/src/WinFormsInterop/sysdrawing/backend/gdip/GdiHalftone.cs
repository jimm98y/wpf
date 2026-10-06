// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// GDI's HALFTONE StretchDIBits onto a 32bpp surface, as win32kfull.sys (arm64, 10.0.26100) does it:
// GreStretchDIBitsInternalImpl @1401b7720 -> EngStretchBltNew @140178858 -> EngHTBlt @1401bd868
// -> HT_HalftoneBitmap @1401412c8 -> AAHalftoneBitmap @1401417e8, the "AA" halftone engine.
//
// The engine reads each source scan as 3-byte B,G,R ("AA24"), runs an x pass (the *DIB_CX functions)
// and a y pass (the *DIB_CY functions) and hands each destination scan to OutputAATo32BPP_RGB
// @140146720, which writes ~LUT[c] per channel; the LUT (ComputeRGBLUTAA @140144b90) is the identity
// complement for a 32bpp DIB under the default COLORADJUSTMENT, and the 32K BGR mapping
// (ComputeBGRMappingTable) is skipped (pDCIAdjClr sets DCI flag 0x40000000).
//

namespace System.Drawing.WebGpuBackend.Gdip
{
    internal static class GdiHalftone
    {
        // AAHEADER flags (SetupAAHeader @140148260 local_e8, ComputeAABBP @1401479b0)
        const int AAHF_FLIP_X = 0x1, AAHF_FLIP_Y = 0x2, AAHF_FIXUP = 0x40, AAHF_NO_AA = 0x200,
                  AAHF_FAST_EXP = 0x4000, AAHF_SHRINK_AREA = 0x80000;

        /// HALFTONE StretchDIBits(SRCCOPY) of a DIB onto a 32bpp surface: the destination's dw x dh pixels
        /// (|dw|, |dh|; a negative extent mirrors as GDI does), top-down, 0x00RRGGBB, or null where GDI draws
        /// nothing / would not use the halftone path.
        public static uint[] Stretch(byte[] info, int infoOffset, byte[] bits, int bitsOffset, int xSrc, int ySrc, int wSrc, int hSrc, int dw, int dh)
        {
            if (info == null || bits == null || dw == 0 || dh == 0 || wSrc == 0 || hSrc == 0) return null;
            Source src = Source.Parse(info, infoOffset, bits, bitsOffset);
            if (src == null) return null;

            // GreStretchDIBitsInternalImpl: the destination rectangle is ordered with the +1 exchange
            // (left > right means the extent was negative), the source y is counted from the DIB's bottom.
            bool flipX = false, flipY = false;
            int aw = Math.Abs(dw), ah = Math.Abs(dh);
            if (dw < 0) flipX = !flipX;
            if (dh < 0) flipY = !flipY;
            int ys = src.Height - ySrc - hSrc;
            int sl = xSrc, sr = xSrc + wSrc, st = ys, sb = ys + hSrc;
            if (sr < sl) { int t = sl; sl = sr + 1; sr = t + 1; flipX = !flipX; }
            if (sb < st) { int t = st; st = sb + 1; sb = t + 1; flipY = !flipY; }
            if (sr <= 0 || sb <= 0 || sl >= src.Width || st >= src.Height || sl >= sr || st >= sb) return null;

            var eng = new Engine(src, sl, st, sr, sb, aw, ah, flipX, flipY);
            return eng.Run();
        }

        // ---------------------------------------------------------------------------------------------
        // The source DIB, decoded to AA24 a scan at a time (Input1BPPToAA24 @14014a030, Input4BPPToAA24
        // @14014a3b0, Input8BPPToAA24 @14014a540, InputAABFDATAToAA24 @14014a600; the channel shifts are
        // ComputeInputColorInfo @140147ee8's: the top 8 bits of each mask, the bits below them zero).
        // ---------------------------------------------------------------------------------------------
        sealed class Source
        {
            public int Width, Height, Bpp, Stride, BitsOffset;
            public bool TopDown;
            public byte[] Bits;
            public byte[] Pal;          // B,G,R per entry
            public int[] RShift = new int[3], LShift = new int[3], Mask = new int[3];   // B, G, R

            public static Source Parse(byte[] info, int o, byte[] bits, int bo)
            {
                if (info.Length - o < 40) return null;
                int size = BitConverter.ToInt32(info, o);
                if (size < 40) return null;
                int w = BitConverter.ToInt32(info, o + 4), h = BitConverter.ToInt32(info, o + 8);
                int bpp = BitConverter.ToUInt16(info, o + 14), comp = BitConverter.ToInt32(info, o + 16);
                int clrUsed = BitConverter.ToInt32(info, o + 32);
                if (w <= 0 || h == 0 || h == int.MinValue) return null;
                var s = new Source { Width = w, Height = Math.Abs(h), Bpp = bpp, TopDown = h < 0, Bits = bits, BitsOffset = bo };
                uint[] masks = null;
                if (comp == 3)
                {
                    if (bpp != 16 && bpp != 32) return null;
                    int mo = o + 40;
                    if (mo + 12 > info.Length) return null;
                    masks = new uint[] { BitConverter.ToUInt32(info, mo), BitConverter.ToUInt32(info, mo + 4), BitConverter.ToUInt32(info, mo + 8) };
                }
                else if (comp == 0)
                {
                    if (bpp == 16) masks = new uint[] { 0x7C00, 0x3E0, 0x1F };
                    else if (bpp == 24 || bpp == 32) masks = new uint[] { 0xFF0000, 0xFF00, 0xFF };
                    else if (bpp == 1 || bpp == 4 || bpp == 8)
                    {
                        int n = 1 << bpp, used = clrUsed != 0 && (uint)clrUsed < (uint)n ? clrUsed : n;
                        if (o + size + used * 4 > info.Length) return null;
                        s.Pal = new byte[n * 3];
                        for (int i = 0; i < used; i++)
                        {
                            int p = o + size + 4 * i;
                            s.Pal[3 * i] = info[p]; s.Pal[3 * i + 1] = info[p + 1]; s.Pal[3 * i + 2] = info[p + 2];
                        }
                    }
                    else return null;
                }
                else return null;
                if (masks != null)
                {
                    // masks[0] red, [1] green, [2] blue -> channel 2, 1, 0
                    for (int c = 0; c < 3; c++)
                    {
                        uint m = masks[c];
                        if (m == 0) return null;
                        int shift = System.Numerics.BitOperations.TrailingZeroCount(m), nb = System.Numerics.BitOperations.PopCount(m);
                        if (nb < 32 && (m >> shift) != (1u << nb) - 1) return null;
                        int ch = 2 - c;
                        if (nb < 8)
                        {
                            int r = shift - (8 - nb);
                            s.Mask[ch] = (0xFF << (8 - nb)) & 0xFF;
                            if (r < 0) { s.RShift[ch] = 0; s.LShift[ch] = -r; } else { s.RShift[ch] = r; s.LShift[ch] = 0; }
                        }
                        else { s.RShift[ch] = shift + nb - 8; s.LShift[ch] = 0; s.Mask[ch] = 0xFF; }
                    }
                }
                s.Stride = ((w * bpp + 31) >> 5) * 4;
                if ((long)s.Stride * s.Height > bits.Length - (long)bo) return null;
                return s;
            }

            // Source row r in top-down (surface) order, pixels x0..x0+n-1 into dst at off.
            public void Row(int r, int x0, int n, byte[] dst, int off)
            {
                int row = BitsOffset + (TopDown ? r : Height - 1 - r) * Stride;
                byte[] b = Bits;
                switch (Bpp)
                {
                    case 1:
                        for (int i = 0; i < n; i++) { int x = x0 + i; int idx = (b[row + (x >> 3)] >> (7 - (x & 7))) & 1; Pal3(idx, dst, off + 3 * i); }
                        break;
                    case 4:
                        for (int i = 0; i < n; i++) { int x = x0 + i; int v = b[row + (x >> 1)]; int idx = (x & 1) == 0 ? v >> 4 : v & 15; Pal3(idx, dst, off + 3 * i); }
                        break;
                    case 8:
                        for (int i = 0; i < n; i++) Pal3(b[row + x0 + i], dst, off + 3 * i);
                        break;
                    default:
                        int bp = Bpp >> 3;
                        for (int i = 0; i < n; i++)
                        {
                            int p = row + (x0 + i) * bp;
                            uint v = bp == 2 ? (uint)(b[p] | b[p + 1] << 8) : bp == 3 ? (uint)(b[p] | b[p + 1] << 8 | b[p + 2] << 16) : BitConverter.ToUInt32(b, p);
                            for (int c = 0; c < 3; c++)
                                dst[off + 3 * i + c] = (byte)(((v >> RShift[c]) << LShift[c]) & (uint)Mask[c]);
                        }
                        break;
                }
            }

            void Pal3(int idx, byte[] dst, int o) { dst[o] = Pal[3 * idx]; dst[o + 1] = Pal[3 * idx + 1]; dst[o + 2] = Pal[3 * idx + 2]; }
        }

        // ---------------------------------------------------------------------------------------------
        // The scan reader (the input functions' tail at +0x34/+0x28: once the row count runs out the
        // pointer stops advancing, so every later read repeats the last scan) and GetFixupScan @140149da8.
        // ---------------------------------------------------------------------------------------------
        sealed class Reader
        {
            readonly Source _s; readonly int _x0, _cx, _top, _rows;
            int _cur, _remaining; bool _advance = true;
            public Reader(Source s, int x0, int cx, int top, int rows) { _s = s; _x0 = x0; _cx = cx; _top = top; _rows = rows; _cur = top; _remaining = rows; }
            public int Remaining => _remaining;
            public void Read(byte[] dst, int off)
            {
                if (dst != null) _s.Row(_cur, _x0, _cx, dst, off);
                if (_advance)
                {
                    if (_remaining == 0 || --_remaining == 0) _advance = false;
                    else _cur++;
                }
            }
            // AAHEADER flag 0x20 in GetFixupScan: back up one scan
            public void StepBack()
            {
                _remaining++;
                if (_remaining > _rows) _remaining = _rows;
                _cur = _top + (_rows - _remaining);
                _advance = true;
            }
        }

        // ---------------------------------------------------------------------------------------------
        // FixupColorScan @140149480 with InitializeFUDI @140149f18: a four-scan window (B0..B3, each with a
        // pixel of padding at both ends) in which every 2x2 checker of two colours is either flattened to
        // their average (when the checker continues two pixels on) or has its brighter (by B+4R+8G)
        // diagonal pulled 4/16 toward the other one; the fixed copies of B1 and B2 are B4 and B5.
        // ---------------------------------------------------------------------------------------------
        sealed class Fixup
        {
            readonly Reader _rd; readonly int _cx, _size;
            readonly byte[][] _b = new byte[6][];
            int _r;
            public bool StepBackFlag;

            public Fixup(Reader rd, int cx, int rows)
            {
                _rd = rd; _cx = cx; _size = (cx + 4) * 3;
                for (int i = 0; i < 6; i++) _b[i] = new byte[_size + 8];
                _r = rows;
                for (int i = 2; i <= 3; i++)
                {
                    byte[] p = _b[i];
                    rd.Read(p, 3);
                    Pad(p);
                }
                Array.Copy(_b[2], _b[5], _size);
                Array.Copy(_b[3], _b[1], _size);
            }

            void Pad(byte[] p)
            {
                // mirrored: the pads are the second pixel in from each end
                p[0] = p[6]; p[1] = p[7]; p[2] = p[8];
                int e = 3 * _cx;
                p[e + 3] = p[e - 3]; p[e + 4] = p[e - 2]; p[e + 5] = p[e - 1];
            }

            static uint Px(byte[] p, int i) => (uint)(p[3 * i] | p[3 * i + 1] << 8 | p[3 * i + 2] << 16);

            public void Scan(byte[] dst, int off)
            {
                byte[] outp;
                if (StepBackFlag)
                {
                    StepBackFlag = false;
                    outp = _b[4];
                    _r++;
                }
                else if (_r <= 1) outp = _b[5];
                else
                {
                    byte[] b0 = _b[0];
                    _b[0] = _b[1]; _b[1] = _b[2]; _b[2] = _b[3]; _b[3] = _b[4]; _b[4] = _b[5]; _b[5] = b0;
                    Array.Copy(_b[2], _b[5], _size);
                    byte[] B0 = _b[0], B1 = _b[1], B2 = _b[2], B3 = _b[3], B4 = _b[4], B5 = _b[5];
                    if (_rd.Remaining > 0) { _rd.Read(B3, 3); Pad(B3); }
                    else Array.Copy(B1, B3, _size);
                    for (int k = 1; k < _cx; k++)
                    {
                        uint a = Px(B1, k), b = Px(B1, k + 1), c = Px(B2, k), d = Px(B2, k + 1);
                        if (a == b || c != b || d != a) continue;
                        uint b1m = Px(B1, k - 1), b1p = Px(B1, k + 2), b2m = Px(B2, k - 1), b2p = Px(B2, k + 2);
                        uint b0k = Px(B0, k), b0p = Px(B0, k + 1), b3k = Px(B3, k), b3p = Px(B3, k + 1);
                        if ((b2p == c && b2m == d && b1m == b && b1p == a) || (b3p == b && b0p == d && b0k == c && b3k == a))
                        {
                            for (int ch = 0; ch < 3; ch++)
                            {
                                byte v = (byte)((B1[3 * k + ch] + B1[3 * k + 3 + ch] + 1) >> 1);
                                B4[3 * k + ch] = v; B4[3 * k + 3 + ch] = v; B5[3 * k + ch] = v; B5[3 * k + 3 + ch] = v;
                            }
                            continue;
                        }
                        uint ka = (uint)(B1[3 * k] + 4 * (B1[3 * k + 2] + 2 * B1[3 * k + 1]));
                        uint kb = (uint)(B1[3 * k + 3] + 4 * (B1[3 * k + 5] + 2 * B1[3 * k + 4]));
                        if (ka >= kb)
                        {
                            // a (B4[k]) along B1[k+2] and B3[k]; d (B5[k+1]) along B2[k-1] and B0[k+1]
                            uint fa = Px(B4, k);
                            B4[3 * k] = (byte)(((fa & 0xff) * 12 + (b3k & 0xff) + (b1p & 0xff) + (b & 0xff) + (c & 0xff) + 8) >> 4);
                            B4[3 * k + 1] = (byte)((((fa & 0xff00) * 12 + (b3k & 0xff00) + (b1p & 0xff00) + (b & 0xff00) + (c & 0xff00) + 0x800) >> 12) & 0xff);
                            B4[3 * k + 2] = (byte)((((fa & 0xff0000) * 12 + (b3k & 0xff0000) + (b & 0xff0000) + (c & 0xff0000) + b1p + 0x80000) >> 20) & 0xff);
                            uint fd = Px(B5, k + 1);
                            B5[3 * k + 3] = (byte)(((fd & 0xff) * 12 + (b2m & 0xff) + (b0p & 0xff) + (b & 0xff) + (c & 0xff) + 8) >> 4);
                            B5[3 * k + 4] = (byte)((((fd & 0xff00) * 12 + (b2m & 0xff00) + (b0p & 0xff00) + (b & 0xff00) + (c & 0xff00) + 0x800) >> 12) & 0xff);
                            B5[3 * k + 5] = (byte)((((fd & 0xff0000) * 12 + (b2m & 0xff0000) + (b0p & 0xff0000) + (b & 0xff0000) + (c & 0xff0000) + 0x80000) >> 20) & 0xff);
                        }
                        else
                        {
                            // b (B4[k+1]) along B3[k+1] and B1[k-1]; c (B5[k]) along B2[k+2] and B0[k]
                            uint fb = Px(B4, k + 1);
                            B4[3 * k + 3] = (byte)(((fb & 0xff) * 12 + (b3p & 0xff) + (b1m & 0xff) + (a & 0xff) + (d & 0xff) + 8) >> 4);
                            B4[3 * k + 4] = (byte)((((fb & 0xff00) * 12 + (b3p & 0xff00) + (b1m & 0xff00) + (a & 0xff00) + (d & 0xff00) + 0x800) >> 12) & 0xff);
                            B4[3 * k + 5] = (byte)((((fb & 0xff0000) * 12 + (b3p & 0xff0000) + (b1m & 0xff0000) + (a & 0xff0000) + (d & 0xff0000) + 0x80000) >> 20) & 0xff);
                            uint fc = Px(B5, k);
                            B5[3 * k] = (byte)(((fc & 0xff) * 12 + (b2p & 0xff) + (b0k & 0xff) + (a & 0xff) + (d & 0xff) + 8) >> 4);
                            B5[3 * k + 1] = (byte)((((fc & 0xff00) * 12 + (b2p & 0xff00) + (b0k & 0xff00) + (a & 0xff00) + (d & 0xff00) + 0x800) >> 12) & 0xff);
                            B5[3 * k + 2] = (byte)((((fc & 0xff0000) * 12 + (b2p & 0xff0000) + (b0k & 0xff0000) + (a & 0xff0000) + (d & 0xff0000) + 0x80000) >> 20) & 0xff);
                        }
                    }
                    outp = B4;
                }
                if (dst != null) Array.Copy(outp, 3, dst, off, 3 * _cx);
                _r--;
            }
        }

        // ---------------------------------------------------------------------------------------------
        // BuildRepData @140147180: the Bresenham run lengths of nearest-neighbour stretching -- per source
        // pixel the number of destination pixels when enlarging, per destination pixel the number of
        // source pixels when shrinking.
        // ---------------------------------------------------------------------------------------------
        sealed class RepData
        {
            public int SrcMin, SrcMax, DstMin, DstMax;     // the in-range extremes, max exclusive
            public byte LeadBack, Ahead, Lead, Trail;       // +0x20..+0x23
            public ushort[] Runs = Array.Empty<ushort>();
        }

        static RepData BuildRepData(int cIn, int cOut, int srcMin, int srcMax, int clipL, int clipR, int src, int dst)
        {
            int small, big; bool expand;
            if (cIn < cOut) { small = cIn; big = cOut; expand = true; }
            else if (cIn > cOut) { small = cOut; big = cIn; expand = false; }
            else return null;
            int w23 = 2 * big, w21 = 2 * small, w10 = w23 + small;
            int w12 = 0, w3 = 0, w9 = -1, w5 = 0, w13 = 0, w6 = 0, lead = 0;
            if (dst >= clipR) return null;
            var runs = new System.Collections.Generic.List<ushort>();
            while (true)
            {
                w10 -= w21;
                if (w10 < 0)
                {
                    if (expand) src++; else dst++;
                    if (w9 != -1) runs.Add((ushort)w12);
                    w10 += w23; w12 = 0; w3 = 0;
                }
                w3++;
                bool inRange = !(src < srcMin || dst < clipL || dst >= clipR || src >= srcMax);
                if (inRange)
                {
                    w12++;
                    if (w9 == -1) { lead = (w3 - 1) & 0xff; w9 = src; w5 = dst; }
                    w13 = src; w6 = dst;
                }
                else if (w9 != -1) break;
                if (expand) dst++; else src++;
                if (dst >= clipR) { if (w9 == -1) return null; break; }
            }
            int trail = 0;
            if (w12 != 0)
            {
                runs.Add((ushort)w12);
                int e = w10 - w21;
                while (e >= 0) { trail = (trail + 1) & 0xff; e -= w21; }
            }
            int nBack = expand ? 2 : lead, nAhead = expand ? 2 : trail;
            int x = w9;
            while (nBack != 0 && x > srcMin) { x--; nBack--; }
            int leadBack = w9 - x;
            x = w13;
            while (nAhead != 0 && x < srcMax - 1) { x++; nAhead--; }
            int ahead = x - w13;
            return new RepData
            {
                LeadBack = (byte)leadBack, Ahead = (byte)ahead, Lead = (byte)lead, Trail = (byte)trail,
                SrcMin = w9, SrcMax = w13 + 1, DstMin = w5, DstMax = w6 + 1, Runs = runs.ToArray(),
            };
        }

        // ---------------------------------------------------------------------------------------------
        // AAINFO: one axis' plan, built by BuildBltAAInfo @140146800 / BuildExpandAAInfo @1401469a0 /
        // BuildShrinkAAInfo @140147400.
        // ---------------------------------------------------------------------------------------------
        sealed class AAInfo
        {
            public int CIn, COut;
            public int SrcFirst, SrcLast, DstFirst, DstLast;
            public RepData Rep;
        }

        static AAInfo BuildBltAAInfo(int srcL, int srcR, int srcW, int dstL, int dstR, int clipL, int clipR)
        {
            int cDst = dstR - dstL, cSrc = srcR - srcL;
            if (cDst <= 0 || clipL >= clipR || cSrc != cDst) return null;
            int lo = Math.Max(srcL, 0), hi = Math.Min(srcR, srcW);
            int first = -1, last = 0, fd = 0, ld = 0;
            for (int i = 0, s = srcL, d = dstL; i < cDst; i++, s++, d++)
            {
                if (s < lo || s >= hi || d < clipL || d >= clipR) { if (first != -1) break; }
                else { last = s; ld = d; if (first == -1) { first = s; fd = d; } }
            }
            if (first == -1) return null;
            return new AAInfo { SrcFirst = first, SrcLast = last, DstFirst = fd, DstLast = ld, CIn = last - first + 1, COut = ld - fd + 1 };
        }

        // ---------------------------------------------------------------------------------------------
        // The engine (SetupAAHeader @140148260 + the CY function it picks).
        // ---------------------------------------------------------------------------------------------
        sealed class Engine
        {
            readonly Source _s;
            readonly int _sl, _st, _sr, _sb, _dw, _dh;
            readonly bool _flipX, _flipY;
            int _flags;
            byte _cyMode, _cxMode;
            AAInfo _ax, _ay;
            Reader _rd;
            Fixup _fix;
            uint[] _out;
            int _outRow, _dstX0, _dstY0;

            public Engine(Source s, int sl, int st, int sr, int sb, int dw, int dh, bool flipX, bool flipY)
            { _s = s; _sl = sl; _st = st; _sr = sr; _sb = sb; _dw = dw; _dh = dh; _flipX = flipX; _flipY = flipY; }

            public uint[] Run()
            {
                ComputeAABBP();
                // SetupAAHeader: no fixup where antialiasing is off already
                if ((_flags & AAHF_NO_AA) != 0) _flags &= ~AAHF_FIXUP;
                if ((_flags & AAHF_FIXUP) != 0)
                {
                    CheckBMPNeedFixup();
                    if ((_flags & AAHF_SHRINK_AREA) != 0) _flags &= ~AAHF_NO_AA;     // DCI flag 0x80000 clear
                }
                if ((_flags & AAHF_NO_AA) != 0) _flags &= ~AAHF_FAST_EXP;

                int sl = _sl, sr = _sr, cl = 0, cr = _dw;
                _ax = BuildAxis(ref sl, ref sr, _s.Width, 0, _dw, ref cl, ref cr);
                if (_ax == null || _ax.CIn == 0 || _ax.COut == 0) return null;
                int st = _st, sb = _sb, ct = 0, cb = _dh;
                _ay = BuildAxis(ref st, ref sb, _s.Height, 0, _dh, ref ct, ref cb);
                if (_ay == null || _ay.CIn == 0 || _ay.COut == 0) return null;

                _out = new uint[_dw * _dh];
                _rd = new Reader(_s, sl, _ax.CIn, st, _ay.CIn);
                if ((_flags & AAHF_FIXUP) != 0) _fix = new Fixup(_rd, _ax.CIn, _ay.CIn);
                _dstX0 = cl; _dstY0 = ct;

                if ((_flags & AAHF_NO_AA) != 0)
                {
                    if (_cyMode == 1) BltDIB_CY();
                    else if (_cyMode == 2 || _cyMode == 3) SkipDIB_CY();
                    else RepDIB_CY();
                }
                else
                {
                    switch (_cyMode)
                    {
                        case 1: BltDIB_CY(); break;
                        default: return null;
                    }
                }
                return _out;
            }

            // ComputeAABBP @1401479b0 (the rectangles are already ordered; only the no-clip, no-band case).
            void ComputeAABBP()
            {
                int f = 0;
                if (_flipX) f |= AAHF_FLIP_X;
                if (_flipY) f |= AAHF_FLIP_Y;
                int scx = _sr - _sl, scy = _sb - _st, dcx = _dw, dcy = _dh;
                if ((dcx * 1000 + 500) / scx > 667 && (dcy * 1000 + 500) / scy > 667) f |= AAHF_FIXUP;
                f |= (long)dcy * dcx < (long)scy * scx ? 0x80010 : 8;
                if (scy == dcy) _cyMode = 1;
                else if (scy < dcy)
                {
                    if (scx < dcx)
                    {
                        if ((f & AAHF_NO_AA) == 0 && dcy <= scy * 5 && dcx <= scx * 5) f |= AAHF_FAST_EXP;
                        _cyMode = 5;
                    }
                    else _cyMode = 4;
                }
                else _cyMode = (byte)(dcx < scx ? 3 : 2);
                if (scx == dcx) _cxMode = 0;
                else if (scx < dcx) _cxMode = 2;
                else { _cxMode = 1; f |= 0x2000; }
                _flags = f;
            }

            // CheckBMPNeedFixup @140148f88: 1bpp and 4bpp always get the fixup without antialiasing;
            // deeper sources count their colours.
            void CheckBMPNeedFixup()
            {
                if (_s.Bpp <= 4) { _flags |= AAHF_FIXUP | AAHF_NO_AA; return; }
                int l = Math.Max(_sl, 0), t = Math.Max(_st, 0), r = Math.Min(_sr, _s.Width), b = Math.Min(_sb, _s.Height);
                if (l >= r || t >= b) { _flags |= AAHF_NO_AA; return; }
                int cx = r - l, cy = b - t;
                int n = cy * cx;
                if (n <= 0x900) { _flags |= AAHF_NO_AA; return; }
                int limit, rowStep = 1, rows = cy;
                if (n <= 0x4000) limit = n >> 3;
                else { rows = (cy + 5) / 6; rowStep = 6; limit = 20; }
                var seen = new System.Collections.Generic.List<uint>();
                var line = new byte[cx * 3];
                int count = 0;
                for (int y = 0; y < rows; y++)
                {
                    _s.Row(t + y * rowStep, l, cx, line, 0);
                    bool fresh = false, over = false;
                    for (int i = 0; i < cx; i++)
                    {
                        uint key = (uint)(line[3 * i + 1] | line[3 * i + 2] << 8 | line[3 * i] << 16);
                        if (line[3 * i + 2] == line[3 * i]) key &= 0xfcfcfcfc;
                        bool found = false;
                        for (int j = seen.Count - 1; j >= 0; j--) if (seen[j] == key) { found = true; break; }
                        if (!found)
                        {
                            count++;
                            if (count > limit) { over = true; break; }
                            seen.Add(key); fresh = true;
                        }
                    }
                    if (!over && limit != 20 && !fresh)
                    {
                        n -= cx;
                        if (n <= 0x900) { _flags |= AAHF_NO_AA; break; }
                        limit = n >> 4;
                    }
                    if (count > limit) break;
                }
                if (count < 20) _flags |= AAHF_NO_AA;
                if (limit == 20) { if (count > 20) _flags &= ~AAHF_FIXUP; }
                else if (count > 20 || count <= limit) _flags &= ~AAHF_FIXUP;
            }

            AAInfo BuildAxis(ref int srcL, ref int srcR, int srcW, int dstL, int dstR, ref int clipL, ref int clipR)
            {
                int cs = srcR - srcL, cd = dstR - dstL;
                AAInfo a;
                if (cs == cd) a = BuildBltAAInfo(srcL, srcR, srcW, dstL, dstR, clipL, clipR);
                else if (cs < cd) a = BuildExpandAAInfo(srcL, srcR, srcW, dstL, dstR, clipL, clipR);
                else a = BuildShrinkAAInfo(srcL, srcR, srcW, dstL, dstR, clipL, clipR);
                if (a == null) return null;
                srcL = a.SrcFirst; srcR = a.SrcLast; clipL = a.DstFirst; clipR = a.DstLast;
                return a;
            }

            AAInfo BuildExpandAAInfo(int srcL, int srcR, int srcW, int dstL, int dstR, int clipL, int clipR)
            {
                int cOut = dstR - dstL, cIn = srcR - srcL;
                if (cIn < 1 || clipR <= clipL || cOut <= cIn) return null;
                int lo = Math.Max(srcL, 0), hi = Math.Min(srcR, srcW);
                var a = new AAInfo();
                if ((_flags & 0x4a80) != 0)
                {
                    a.Rep = BuildRepData(cIn, cOut, lo, hi, clipL, clipR, srcL, dstL);
                    if (a.Rep == null) return null;
                }
                if ((_flags & 0x4200) == 0) return null;   // weights: not yet
                a.SrcFirst = a.Rep.SrcMin; a.SrcLast = a.Rep.SrcMax - 1; a.DstFirst = a.Rep.DstMin; a.DstLast = a.Rep.DstMax - 1;
                a.CIn = a.SrcLast - a.SrcFirst + 1; a.COut = a.DstLast - a.DstFirst + 1;
                return a;
            }

            AAInfo BuildShrinkAAInfo(int srcL, int srcR, int srcW, int dstL, int dstR, int clipL, int clipR)
            {
                int cIn = srcR - srcL, cOut = dstR - dstL;
                if (srcR < srcL || dstR <= dstL) return null;
                if (clipR <= clipL - 1 || cIn <= cOut) return null;
                int lo = Math.Max(srcL, 0), hi = Math.Min(srcR, srcW);
                var a = new AAInfo();
                if ((_flags & 0x4a80) != 0)
                {
                    a.Rep = BuildRepData(cIn, cOut, lo, hi, clipL, clipR, srcL, dstL);
                    if (a.Rep == null) return null;
                }
                if ((_flags & AAHF_NO_AA) == 0) return null;   // weights: not yet
                a.SrcFirst = a.Rep.SrcMin; a.SrcLast = a.Rep.SrcMax - 1; a.DstFirst = a.Rep.DstMin; a.DstLast = a.Rep.DstMax - 1;
                a.CIn = a.SrcLast - a.SrcFirst + 1; a.COut = a.DstLast - a.DstFirst + 1;
                return a;
            }

            // ---- scan input (GetFixupScan) ----
            void GetScan(byte[] dst)
            {
                if (_fix != null) _fix.Scan(dst, 0);
                else _rd.Read(dst, 0);
            }

            void GetScanSkip()
            {
                if (_fix != null) _fix.Scan(null, 0);
                else _rd.Read(null, 0);
            }

            // ---- the x pass ----
            void CX(byte[] src, byte[] bgr)
            {
                int n = _ax.COut;
                if ((_flags & AAHF_NO_AA) != 0 && _cxMode == 1)
                {
                    // SkipDIB_CX @1402d9160: the last source pixel of each run
                    int s = 0;
                    for (int i = 0; i < n; i++) { s += _ax.Rep.Runs[i]; Copy3(src, s - 1, bgr, i); }
                }
                else if ((_flags & AAHF_NO_AA) != 0 && _cxMode == 2)
                {
                    // RepDIB_CX @14014e0f0
                    var runs = _ax.Rep.Runs;
                    int ri = 0, s = 0, cur = 0; uint left = 1;
                    for (int i = 0; i < n; i++)
                    {
                        if (--left == 0)
                        {
                            left = ri < runs.Length ? runs[ri] : 0u;
                            cur = s;
                            if (ri < runs.Length) { ri++; s++; }
                        }
                        Copy3(src, cur, bgr, i);
                    }
                }
                else if (_cxMode == 0)
                {
                    // CopyDIB_CX @14014b270
                    for (int i = 0; i < n; i++) Copy3(src, i, bgr, i);
                }
                else throw new NotSupportedException();
            }

            static void Copy3(byte[] s, int si, byte[] d, int di) { d[3 * di] = s[3 * si]; d[3 * di + 1] = s[3 * si + 1]; d[3 * di + 2] = s[3 * si + 2]; }

            // ---- output (OutputAATo32BPP_RGB with the identity LUT) ----
            void Output(byte[] bgr)
            {
                int y = _dstY0 + _outRow++;
                if (_flipY) y = _dh - 1 - y;
                int n = _ax.COut;
                for (int i = 0; i < n; i++)
                {
                    int x = _dstX0 + i;
                    if (_flipX) x = _dw - 1 - x;
                    _out[y * _dw + x] = (uint)(bgr[3 * i] | bgr[3 * i + 1] << 8 | bgr[3 * i + 2] << 16);
                }
            }

            // BltDIB_CY @14014afc0
            void BltDIB_CY()
            {
                var line = new byte[_ax.CIn * 3 + 16];
                var bgr = new byte[_ax.COut * 3 + 16];
                for (int r = 0; r < _ay.COut; r++)
                {
                    GetScan(line);
                    CX(line, bgr);
                    Output(bgr);
                }
            }

            // RepDIB_CY @14014e150
            void RepDIB_CY()
            {
                var line = new byte[_ax.CIn * 3 + 16];
                var bgr = new byte[_ax.COut * 3 + 16];
                var runs = _ay.Rep.Runs;
                int ri = 0; uint left = 1;
                for (int r = 0; r < _ay.COut; r++)
                {
                    if (--left == 0)
                    {
                        left = ri < runs.Length ? runs[ri] : 0u;
                        if (ri < runs.Length) { GetScan(line); CX(line, bgr); ri++; }
                    }
                    Output(bgr);
                }
            }

            // SkipDIB_CY @14014fa30
            void SkipDIB_CY()
            {
                var line = new byte[_ax.CIn * 3 + 16];
                var bgr = new byte[_ax.COut * 3 + 16];
                var runs = _ay.Rep.Runs;
                for (int r = 0; r < _ay.COut; r++)
                {
                    int cnt = runs[r];
                    while (--cnt >= 1) GetScanSkip();
                    GetScan(line);
                    CX(line, bgr);
                    Output(bgr);
                }
            }
        }
    }
}
