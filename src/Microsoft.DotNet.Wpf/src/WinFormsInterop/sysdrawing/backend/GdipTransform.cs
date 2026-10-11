// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Image.RotateFlip on a managed frame: the pixels moved, in their own format (an indexed bitmap
// stays indexed with its palette; nothing is converted), as GdipImageRotateFlip moves them.
//

using System.Drawing.Imaging;

namespace System.Drawing.WebGpuBackend
{
    internal static class GdipTransform
    {
        internal static GdipFrame RotateFlip (GdipFrame f, RotateFlipType type)
        {
            int t = (int) type & 7;
            if (t == 0) return f;
            bool swap = (t & 1) != 0;          // 90 and 270 degrees
            int w = f.Width, h = f.Height;
            int nw = swap ? h : w, nh = swap ? w : h;
            var dst = new GdipFrame (nw, nh, f.Format) { DpiX = swap ? f.DpiY : f.DpiX, DpiY = swap ? f.DpiX : f.DpiY };
            if (f.Palette != null) { dst.Palette = (Color[]) f.Palette.Clone (); dst.PaletteFlags = f.PaletteFlags; dst.PaletteIsDefault = f.PaletteIsDefault; }
            int bpp = f.BitsPerPixel;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++) {
                    int dx, dy;
                    switch (t) {
                    case 1: dx = h - 1 - y; dy = x; break;                 // 90
                    case 2: dx = w - 1 - x; dy = h - 1 - y; break;         // 180
                    case 3: dx = y; dy = w - 1 - x; break;                 // 270
                    case 4: dx = w - 1 - x; dy = y; break;                 // flip X
                    case 5: dx = y; dy = x; break;                         // 90 + flip X
                    case 6: dx = x; dy = h - 1 - y; break;                 // flip Y
                    default: dx = h - 1 - y; dy = w - 1 - x; break;        // 270 + flip X
                    }
                    Copy (f, x, y, dst, dx, dy, bpp);
                }
            return dst;
        }

        static void Copy (GdipFrame s, int sx, int sy, GdipFrame d, int dx, int dy, int bpp)
        {
            if (bpp >= 8) {
                int n = bpp / 8;
                Buffer.BlockCopy (s.Bits, sy * s.Stride + sx * n, d.Bits, dy * d.Stride + dx * n, n);
                return;
            }
            int sbit = sx * bpp, dbit = dx * bpp, mask = (1 << bpp) - 1;
            int v = (s.Bits [sy * s.Stride + (sbit >> 3)] >> (8 - bpp - (sbit & 7))) & mask;
            int o = dy * d.Stride + (dbit >> 3), shift = 8 - bpp - (dbit & 7);
            d.Bits [o] = (byte) ((d.Bits [o] & ~(mask << shift)) | v << shift);
        }
    }
}
