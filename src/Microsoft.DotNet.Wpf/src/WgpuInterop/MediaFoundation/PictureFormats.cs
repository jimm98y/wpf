// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The rarer layouts of the video processor's and color converter's subtypes (Pictures.Decode's and Encode's beside):
// those of more than 8 bits (A2R10G10B10, A16B16G16R16F, Y210, Y216, Y410, Y416, v410, V410, V216), of a palette (P8,
// RGB8: Windows' halftone palette where the type has none), and the rarer packings (P208, Y41P, Y41T, Y42T, YVU9, I422,
// I444) -- as Windows' read and write them (MFFormatsProbe, Formats.windows.txt): to 8 bits a component by the high
// bits.
//

using System;

namespace Microsoft.Wpf.Interop.WebGpu.MediaFoundation
{
    internal static partial class Pictures
    {
        private static readonly Guid P208 = F("P208"), Y210 = F("Y210"), Y216 = F("Y216"), Y410 = F("Y410"), Y416 = F("Y416"), v410 = F("v410");
        private static readonly Guid V410 = F("V410"), V216 = F("V216"), Y41P = F("Y41P"), Y41T = F("Y41T"), Y42T = F("Y42T"), YVU9 = F("YVU9");
        internal static readonly Guid RGB8 = new("e436eb7a-524f-11ce-9f53-0020af0ba770");

        /// <summary>A frame into one of the rarer output subtypes (I422, I444, V216, V410, RGB8); null of another.</summary>
        private static byte[]? EncodeMore(Picture p, Guid s, bool bottomUp, bool converter)
        {
            int w = p.W, h = p.H;
            if (s == I422 || s == I444)
            {
                int sx = s == I444 ? 1 : 2, cw = (w + sx - 1) / sx;
                var yv = p.AsYuv();
                var o = new byte[w * h + 2 * cw * h];
                Array.Copy(yv.C0, o, w * h);
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < cw; x++) { var (u, v) = yv.ChromaFor(x, y, sx, 1); o[w * h + y * cw + x] = u; o[w * h + cw * h + y * cw + x] = v; }
                return o;
            }
            if (s == V216 || s == V410)
            {
                var yv = converter ? p.AsYuvFloat(s == V216 ? 2 : 1, 1) : p.AsYuv();
                var o = new byte[w * h * 4];
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        int i = y * w + x;
                        if (s == V410)
                        {
                            var (u, v) = yv.ChromaFor(x, y, 1, 1);
                            uint word = (uint)u << 4 | (uint)yv.C0[i] << 14 | (uint)v << 24;             // 10-bit fields at bits 2, 12, 22
                            BitConverter.TryWriteBytes(o.AsSpan(i * 4), word);
                        }
                        else if ((x & 1) == 0)
                        {
                            var (u, v) = yv.ChromaFor(x / 2, y, 2, 1);
                            int at = i * 4;
                            o[at + 1] = u; o[at + 3] = yv.C0[i]; o[at + 5] = v; o[at + 7] = yv.C0[Math.Min(i + 1, y * w + w - 1)];
                        }
                    }
                return o;
            }
            if (s == RGB8)
            {
                // the halftone palette's nearest entry
                var q = p.AsRgb(); var pal = MFColorConverter.s_palette;
                int row = (w + 3) & ~3;
                var o = new byte[row * h];
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        int i = y * w + x, best = 0, bd = int.MaxValue;
                        for (int k = 0; k < 256; k++)
                        {
                            int db = pal[k * 4] - q.C2[i], dg = pal[k * 4 + 1] - q.C1[i], dr = pal[k * 4 + 2] - q.C0[i];
                            int d = db * db + dg * dg + dr * dr;
                            if (d < bd) { bd = d; best = k; }
                        }
                        o[(bottomUp ? h - 1 - y : y) * row + x] = (byte)best;
                    }
                return o;
            }
            return null;
        }

        /// <summary>A frame of one of the rarer subtypes; null of another.</summary>
        private static Picture? DecodeMore(byte[] b, Guid s, int w, int h, bool bottomUp)
        {
            byte At(int i) => i < b.Length ? b[i] : (byte)0;
            ushort U16(int i) => (ushort)(At(i) | At(i + 1) << 8);
            uint U32(int i) => (uint)(At(i) | At(i + 1) << 8 | At(i + 2) << 16 | At(i + 3) << 24);
            int Row(int y) => bottomUp ? h - 1 - y : y;

            if (s == MFVideoProcessor.P8 || s == RGB8)
            {
                // the palette's entries (RGBQUADs: blue, green, red), of each byte's index
                var pal = MFColorConverter.s_palette;
                var p = new Picture(w, h, true);
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        int k = At(Row(y) * ((w + 3) & ~3) + x) * 4, i = y * w + x;
                        p.C2[i] = pal[k]; p.C1[i] = pal[k + 1]; p.C0[i] = pal[k + 2]; p.A[i] = pal[k + 3];      // (the RGBQUAD's reserved byte, 0: Windows' alpha)
                    }
                return p;
            }
            if (s == MFVideoProcessor.A2R10G10B10)
            {
                var p = new Picture(w, h, true);
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        uint v = U32((Row(y) * w + x) * 4); int i = y * w + x;
                        p.C0[i] = (byte)(v >> 22 & 0xFF); p.C1[i] = (byte)(v >> 12 & 0xFF); p.C2[i] = (byte)(v >> 2 & 0xFF); p.A[i] = (byte)((v >> 30) * 85);
                    }
                return p;
            }
            if (s == MFVideoProcessor.A16B16G16R16F)
            {
                var p = new Picture(w, h, true);
                static byte Unit(Half f) => (byte)Math.Clamp((int)Math.Round((float)f * 255f), 0, 255);
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        int at = (Row(y) * w + x) * 8, i = y * w + x;
                        p.C0[i] = Unit(BitConverter.ToHalf(b, at)); p.C1[i] = Unit(BitConverter.ToHalf(b, at + 2));
                        p.C2[i] = Unit(BitConverter.ToHalf(b, at + 4)); p.A[i] = Unit(BitConverter.ToHalf(b, at + 6));
                    }
                return p;
            }
            if (s == P208)
            {
                // 4:2:2 of two planes: the luma, then the chroma of each row interleaved
                var p = new Picture(w, h, false, 2, 1);
                for (int i = 0; i < w * h; i++) p.C0[i] = At(i);
                for (int y = 0; y < h; y++) for (int cx = 0; cx < p.CW; cx++) { int o = w * h + y * w + cx * 2; p.C1[y * p.CW + cx] = At(o); p.C2[y * p.CW + cx] = At(o + 1); }
                return p;
            }
            if (s == Y210 || s == Y216 || s == V216)
            {
                // 4:2:2 of 16-bit words: Y0 U Y1 V (V216: U Y0 V Y1), of their high bytes
                var p = new Picture(w, h, false, 2, 1);
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x += 2)
                    {
                        int o = (y * w + x) * 4;
                        if (s == V216) { p.C1[y * p.CW + x / 2] = At(o + 1); p.C0[y * w + x] = At(o + 3); p.C2[y * p.CW + x / 2] = At(o + 5); if (x + 1 < w) p.C0[y * w + x + 1] = At(o + 7); }
                        else { p.C0[y * w + x] = At(o + 1); p.C1[y * p.CW + x / 2] = At(o + 3); if (x + 1 < w) p.C0[y * w + x + 1] = At(o + 5); p.C2[y * p.CW + x / 2] = At(o + 7); }
                    }
                return p;
            }
            if (s == Y410 || s == V410 || s == v410)
            {
                // 4:4:4 of 10-bit fields: Y410's U, Y, V, A from the low bits; v410's and V410's U, Y, V from bits 2, 12, 22
                var p = new Picture(w, h, false);
                for (int i = 0; i < w * h; i++)
                {
                    uint v = U32(i * 4);
                    if (s == v410 || s == V410) { p.C1[i] = (byte)(v >> 4 & 0xFF); p.C0[i] = (byte)(v >> 14 & 0xFF); p.C2[i] = (byte)(v >> 24 & 0xFF); }
                    else { p.C1[i] = (byte)(v >> 2 & 0xFF); p.C0[i] = (byte)(v >> 12 & 0xFF); p.C2[i] = (byte)(v >> 22 & 0xFF); p.A[i] = (byte)((v >> 30) * 85); }
                }
                return p;
            }
            if (s == Y416)
            {
                var p = new Picture(w, h, false);
                for (int i = 0; i < w * h; i++) { p.C1[i] = At(i * 8 + 1); p.C0[i] = At(i * 8 + 3); p.C2[i] = At(i * 8 + 5); p.A[i] = At(i * 8 + 7); }
                return p;
            }
            if (s == Y41P || s == Y41T)
            {
                // 4:1:1 packed, 12 bytes of 8 pixels: U0 Y0 V0 Y1 U4 Y2 V4 Y3 Y4 Y5 Y6 Y7 (Y41T's luma's low bit its transparency)
                var p = new Picture(w, h, false, 4, 1);
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x += 8)
                    {
                        int o = (y * w + x) * 12 / 8;
                        int[] ys = { 1, 3, 5, 7, 8, 9, 10, 11 };
                        for (int k = 0; k < 8 && x + k < w; k++) p.C0[y * w + x + k] = At(o + ys[k]);
                        p.C1[y * p.CW + x / 4] = At(o); p.C2[y * p.CW + x / 4] = At(o + 2);
                        if (x / 4 + 1 < p.CW) { p.C1[y * p.CW + x / 4 + 1] = At(o + 4); p.C2[y * p.CW + x / 4 + 1] = At(o + 6); }
                    }
                return p;
            }
            if (s == Y42T)
            {
                // UYVY, the luma's low bit its transparency
                var p = new Picture(w, h, false, 2, 1);
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x += 2)
                    {
                        int o = (y * w + x) * 2;
                        p.C1[y * p.CW + x / 2] = At(o); p.C0[y * w + x] = At(o + 1); p.C2[y * p.CW + x / 2] = At(o + 2); if (x + 1 < w) p.C0[y * w + x + 1] = At(o + 3);
                    }
                return p;
            }
            if (s == YVU9)
            {
                // planar: the luma, then V and U of each 4x4
                var p = new Picture(w, h, false, 4, 4);
                for (int i = 0; i < w * h; i++) p.C0[i] = At(i);
                int n = p.CW * p.CH;
                for (int i = 0; i < n; i++) { p.C2[i] = At(w * h + i); p.C1[i] = At(w * h + n + i); }
                return p;
            }
            return null;
        }
    }
}
