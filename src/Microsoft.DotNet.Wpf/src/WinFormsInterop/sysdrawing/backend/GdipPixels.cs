// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// GDI+'s pixel-format arithmetic, in managed code: what a GpBitmap does to a pixel on its way between
// its own format and straight 32bpp ARGB, which is the format every conversion passes through.
//
// Each rule below was MEASURED against gdiplus.dll exhaustively (every value of every channel, every
// alpha; scratchpad img/oracle/Tables.cs), not taken from documentation:
//
//   premultiply      c' = (c*a + 127) / 255                      (rounded)
//   unpremultiply    c  = min(255, c' * ((255 << 16) / a) >> 16)  (a reciprocal table, truncated);
//                    alpha 0 passes the colour through untouched
//   5- and 6-bit     store c >> 3 (c >> 2), read v << 3 | v >> 2 (v << 2 | v >> 4)
//   1555 alpha       set when a >= 128; reads back as 255 or 0
//   48/64bpp         13-bit LINEAR light: round(sRGB->linear(c) * 8192), alpha a*8192/255; read back
//                    through the inverse curve rounded, values above 8192 clamp, negative (as signed
//                    16-bit) read as 0; 64bppPArgb premultiplies in linear light, (lin*a13) >> 13
//   32bppRgb         keeps the fourth byte it is given; reads it back as 255
//   24bppRgb, 32bppRgb, 16bpp drop alpha without compositing onto anything
//
// Conversion INTO an indexed format picks the nearest palette entry; gdiplus.dll uses its own
// halftone mapping there, which this does not reproduce (see the image layer's notes).
//

using System.Collections.Generic;
using System.Drawing.Imaging;

namespace System.Drawing
{
    internal static class GdipPixels
    {
        // ---- tables -----------------------------------------------------------------------------

        // sRGB byte -> 13-bit linear, and back.
        private static readonly ushort[] s_toLinear = BuildToLinear ();
        private static readonly byte[] s_fromLinear = BuildFromLinear ();
        // 0xFF0000 / a, the reciprocal GDI+ unpremultiplies with.
        private static readonly int[] s_unpremul = BuildUnpremul ();

        private static ushort[] BuildToLinear ()
        {
            var t = new ushort [256];
            for (int c = 0; c < 256; c++) {
                double v = c / 255.0;
                double l = v <= 0.04045 ? v / 12.92 : Math.Pow ((v + 0.055) / 1.055, 2.4);
                t [c] = (ushort) (int) (l * 8192 + 0.5);
            }
            return t;
        }

        private static byte[] BuildFromLinear ()
        {
            var t = new byte [8193];
            for (int v = 0; v <= 8192; v++) {
                double x = v / 8192.0;
                double s = x <= 0.0031308 ? 12.92 * x : 1.055 * Math.Pow (x, 1 / 2.4) - 0.055;
                t [v] = (byte) Math.Min (255, (int) (s * 255 + 0.5));
            }
            return t;
        }

        private static int[] BuildUnpremul ()
        {
            var t = new int [256];
            for (int a = 1; a < 256; a++) t [a] = (255 << 16) / a;
            return t;
        }

        internal static int Premultiply (int c, int a) => (c * a + 127) / 255;

        internal static int Unpremultiply (int c, int a)
        {
            if (a == 0) return c;
            int v = (c * s_unpremul [a]) >> 16;
            return v > 255 ? 255 : v;
        }

        private static byte FromLinear (int v)
        {
            short s = (short) v;
            if (s <= 0) return 0;
            return s >= 8192 ? (byte) 255 : s_fromLinear [s];
        }

        private static byte AlphaFrom13 (int v)
        {
            short s = (short) v;
            if (s <= 0) return 0;
            int a = (s * 255) >> 13;
            return a > 255 ? (byte) 255 : (byte) a;
        }

        internal static uint PremultiplyArgb (uint argb)
        {
            int a = (int) (argb >> 24);
            if (a == 255) return argb;
            int r = Premultiply ((int) (argb >> 16) & 0xff, a), g = Premultiply ((int) (argb >> 8) & 0xff, a), b = Premultiply ((int) argb & 0xff, a);
            return (uint) (a << 24 | r << 16 | g << 8 | b);
        }

        internal static uint UnpremultiplyArgb (uint pargb)
        {
            int a = (int) (pargb >> 24);
            if (a == 255) return pargb;
            int r = Unpremultiply ((int) (pargb >> 16) & 0xff, a), g = Unpremultiply ((int) (pargb >> 8) & 0xff, a), b = Unpremultiply ((int) pargb & 0xff, a);
            return (uint) (a << 24 | r << 16 | g << 8 | b);
        }

        // ---- palettes ---------------------------------------------------------------------------

        private static readonly uint[] s_vga = {
            0xff000000, 0xff800000, 0xff008000, 0xff808000, 0xff000080, 0xff800080, 0xff008080, 0xff808080,
            0xffc0c0c0, 0xffff0000, 0xff00ff00, 0xffffff00, 0xff0000ff, 0xffff00ff, 0xff00ffff, 0xffffffff,
        };

        /// <summary>The palette GDI+ gives a NEW indexed bitmap: black and white (grey scale) at
        /// 1bpp, the sixteen VGA colours at 4bpp, and at 8bpp its halftone palette -- those sixteen,
        /// 24 empty entries, then the 6x6x6 cube in steps of 0x33.</summary>
        internal static Color[] DefaultPalette (PixelFormat format, out int flags)
        {
            switch (format) {
            case PixelFormat.Format1bppIndexed:
                flags = (int) PaletteFlags.GrayScale;
                return new [] { Color.FromArgb (unchecked ((int) 0xff000000)), Color.FromArgb (unchecked ((int) 0xffffffff)) };
            case PixelFormat.Format4bppIndexed:
                flags = 0;
                return ToColors (s_vga);
            default:
                flags = (int) PaletteFlags.Halftone;
                var p = new Color [256];
                for (int i = 0; i < 16; i++) p [i] = Color.FromArgb (unchecked ((int) s_vga [i]));
                for (int i = 16; i < 40; i++) p [i] = Color.FromArgb (0);
                for (int r = 0; r < 6; r++)
                    for (int g = 0; g < 6; g++)
                        for (int b = 0; b < 6; b++)
                            p [40 + r * 36 + g * 6 + b] = Color.FromArgb (255, r * 0x33, g * 0x33, b * 0x33);
                return p;
            }
        }

        /// <summary>The palette GDI+ gives a bitmap CONVERTED to an indexed format (Clone): at 8bpp
        /// the cube then the eight system colours it lacks; at 4bpp the eight primaries then the
        /// eight system colours; at 1bpp black and white.</summary>
        internal static Color[] ConversionPalette (PixelFormat format, out int flags)
        {
            uint[] extra = { 0xffc0c0c0, 0xff808080, 0xff800000, 0xff008000, 0xff000080, 0xff808000, 0xff800080, 0xff008080 };
            switch (format) {
            case PixelFormat.Format1bppIndexed:
                flags = 0x200;
                return new [] { Color.FromArgb (unchecked ((int) 0xff000000)), Color.FromArgb (unchecked ((int) 0xffffffff)) };
            case PixelFormat.Format4bppIndexed:
                flags = 0x300;
                var p4 = new List<uint> { 0xff000000, 0xff0000ff, 0xff00ff00, 0xff00ffff, 0xffff0000, 0xffff00ff, 0xffffff00, 0xffffffff };
                p4.AddRange (extra);
                return ToColors (p4.ToArray ());
            default:
                flags = 0x700;
                var p8 = new List<uint> ();
                for (int r = 0; r < 6; r++)
                    for (int g = 0; g < 6; g++)
                        for (int b = 0; b < 6; b++)
                            p8.Add (0xff000000u | (uint) (r * 0x33) << 16 | (uint) (g * 0x33) << 8 | (uint) (b * 0x33));
                p8.AddRange (extra);
                return ToColors (p8.ToArray ());
            }
        }

        private static Color[] ToColors (uint[] words)
        {
            var c = new Color [words.Length];
            for (int i = 0; i < words.Length; i++) c [i] = Color.FromArgb (unchecked ((int) words [i]));
            return c;
        }

        /// <summary>The palette entry nearest to <paramref name="argb"/> (squared RGB distance, alpha
        /// matched where the palette carries it).</summary>
        internal static int Nearest (Color[] palette, uint argb, Dictionary<uint, int> cache)
        {
            if (cache != null && cache.TryGetValue (argb, out int hit)) return hit;
            int r = (int) (argb >> 16) & 0xff, g = (int) (argb >> 8) & 0xff, b = (int) argb & 0xff, a = (int) (argb >> 24);
            int best = 0;
            long bestD = long.MaxValue;
            for (int i = 0; i < palette.Length; i++) {
                Color c = palette [i];
                int dr = c.R - r, dg = c.G - g, db = c.B - b, da = c.A - a;
                long d = (long) dr * dr + dg * dg + db * db + (long) da * da;
                if (d < bestD) { bestD = d; best = i; if (d == 0) break; }
            }
            if (cache != null) cache [argb] = best;
            return best;
        }

        // ---- one row, own format <-> ARGB -------------------------------------------------------

        /// <summary>Reads <paramref name="n"/> pixels of row <paramref name="y"/> from column
        /// <paramref name="x"/> as straight ARGB.</summary>
        internal static void ReadArgb (GdipFrame f, int x, int y, int n, uint[] dst, int dstOff)
            => ReadArgb (f.Bits, y * f.Stride, f.Format, f.Palette, x, n, dst, dstOff);

        internal static void ReadArgb (byte[] src, int row, PixelFormat format, Color[] palette, int x, int n, uint[] dst, int dstOff)
        {
            switch (format) {
            case PixelFormat.Format1bppIndexed:
            case PixelFormat.Format4bppIndexed:
            case PixelFormat.Format8bppIndexed: {
                int bits = Image.GetPixelFormatSize (format);
                for (int i = 0; i < n; i++) {
                    int px = x + i, idx;
                    if (bits == 8) idx = src [row + px];
                    else if (bits == 4) idx = (src [row + (px >> 1)] >> ((px & 1) == 0 ? 4 : 0)) & 15;
                    else idx = (src [row + (px >> 3)] >> (7 - (px & 7))) & 1;
                    dst [dstOff + i] = palette != null && idx < palette.Length ? (uint) palette [idx].ToArgb () : 0xff000000u;
                }
                return;
            }
            case PixelFormat.Format16bppRgb555:
            case PixelFormat.Format16bppArgb1555:
            case PixelFormat.Format16bppRgb565: {
                for (int i = 0; i < n; i++) {
                    int o = row + (x + i) * 2;
                    int v = src [o] | (src [o + 1] << 8);
                    int r, g, b, a = 255;
                    if (format == PixelFormat.Format16bppRgb565) {
                        r = (v >> 11) & 31; g = (v >> 5) & 63; b = v & 31;
                        r = r << 3 | r >> 2; g = g << 2 | g >> 4; b = b << 3 | b >> 2;
                    } else {
                        r = (v >> 10) & 31; g = (v >> 5) & 31; b = v & 31;
                        r = r << 3 | r >> 2; g = g << 3 | g >> 2; b = b << 3 | b >> 2;
                        if (format == PixelFormat.Format16bppArgb1555 && (v & 0x8000) == 0) a = 0;
                    }
                    dst [dstOff + i] = (uint) (a << 24 | r << 16 | g << 8 | b);
                }
                return;
            }
            case PixelFormat.Format24bppRgb:
                for (int i = 0; i < n; i++) {
                    int o = row + (x + i) * 3;
                    dst [dstOff + i] = 0xff000000u | (uint) src [o + 2] << 16 | (uint) src [o + 1] << 8 | src [o];
                }
                return;
            case PixelFormat.Format32bppRgb:
                for (int i = 0; i < n; i++) {
                    int o = row + (x + i) * 4;
                    dst [dstOff + i] = 0xff000000u | (uint) src [o + 2] << 16 | (uint) src [o + 1] << 8 | src [o];
                }
                return;
            case PixelFormat.Format32bppArgb:
                for (int i = 0; i < n; i++) {
                    int o = row + (x + i) * 4;
                    dst [dstOff + i] = (uint) src [o + 3] << 24 | (uint) src [o + 2] << 16 | (uint) src [o + 1] << 8 | src [o];
                }
                return;
            case PixelFormat.Format32bppPArgb:
                for (int i = 0; i < n; i++) {
                    int o = row + (x + i) * 4;
                    dst [dstOff + i] = UnpremultiplyArgb ((uint) src [o + 3] << 24 | (uint) src [o + 2] << 16 | (uint) src [o + 1] << 8 | src [o]);
                }
                return;
            case PixelFormat.Format48bppRgb:
                for (int i = 0; i < n; i++) {
                    int o = row + (x + i) * 6;
                    int b = src [o] | src [o + 1] << 8, g = src [o + 2] | src [o + 3] << 8, r = src [o + 4] | src [o + 5] << 8;
                    dst [dstOff + i] = 0xff000000u | (uint) FromLinear (r) << 16 | (uint) FromLinear (g) << 8 | FromLinear (b);
                }
                return;
            case PixelFormat.Format64bppArgb:
            case PixelFormat.Format64bppPArgb:
                for (int i = 0; i < n; i++) {
                    int o = row + (x + i) * 8;
                    int b = src [o] | src [o + 1] << 8, g = src [o + 2] | src [o + 3] << 8, r = src [o + 4] | src [o + 5] << 8, a = src [o + 6] | src [o + 7] << 8;
                    if (format == PixelFormat.Format64bppPArgb) {
                        short sa = (short) a;
                        if (sa > 0) {
                            r = Math.Min (8192, (short) r * 8192 / sa); g = Math.Min (8192, (short) g * 8192 / sa); b = Math.Min (8192, (short) b * 8192 / sa);
                        }
                    }
                    dst [dstOff + i] = (uint) AlphaFrom13 (a) << 24 | (uint) FromLinear (r) << 16 | (uint) FromLinear (g) << 8 | FromLinear (b);
                }
                return;
            default:
                throw new ArgumentException ("Parameter is not valid.");
            }
        }

        /// <summary>Writes <paramref name="n"/> straight ARGB pixels into row <paramref name="y"/>
        /// from column <paramref name="x"/>, in the frame's format.</summary>
        internal static void WriteArgb (GdipFrame f, int x, int y, int n, uint[] src, int srcOff, Dictionary<uint, int> cache = null)
            => WriteArgb (f.Bits, y * f.Stride, f.Format, f.Palette, x, n, src, srcOff, cache);

        internal static void WriteArgb (byte[] dst, int row, PixelFormat format, Color[] palette, int x, int n, uint[] src, int srcOff,
                                        Dictionary<uint, int> cache = null)
        {
            switch (format) {
            case PixelFormat.Format1bppIndexed:
            case PixelFormat.Format4bppIndexed:
            case PixelFormat.Format8bppIndexed: {
                int bits = Image.GetPixelFormatSize (format);
                cache ??= new Dictionary<uint, int> ();
                for (int i = 0; i < n; i++) {
                    int px = x + i;
                    int idx = palette == null || palette.Length == 0 ? 0 : Nearest (palette, src [srcOff + i], cache);
                    if (bits == 8) dst [row + px] = (byte) idx;
                    else if (bits == 4) {
                        int o = row + (px >> 1), shift = (px & 1) == 0 ? 4 : 0;
                        dst [o] = (byte) ((dst [o] & ~(15 << shift)) | (idx & 15) << shift);
                    } else {
                        int o = row + (px >> 3), shift = 7 - (px & 7);
                        dst [o] = (byte) ((dst [o] & ~(1 << shift)) | (idx & 1) << shift);
                    }
                }
                return;
            }
            case PixelFormat.Format16bppRgb555:
            case PixelFormat.Format16bppArgb1555:
            case PixelFormat.Format16bppRgb565:
                for (int i = 0; i < n; i++) {
                    uint c = src [srcOff + i];
                    int r = (int) (c >> 16) & 0xff, g = (int) (c >> 8) & 0xff, b = (int) c & 0xff;
                    int v;
                    if (format == PixelFormat.Format16bppRgb565) v = (r >> 3) << 11 | (g >> 2) << 5 | b >> 3;
                    else {
                        v = (r >> 3) << 10 | (g >> 3) << 5 | b >> 3;
                        if (format == PixelFormat.Format16bppArgb1555 && (c >> 24) >= 128) v |= 0x8000;
                    }
                    int o = row + (x + i) * 2;
                    dst [o] = (byte) v; dst [o + 1] = (byte) (v >> 8);
                }
                return;
            case PixelFormat.Format24bppRgb:
                for (int i = 0; i < n; i++) {
                    uint c = src [srcOff + i];
                    int o = row + (x + i) * 3;
                    dst [o] = (byte) c; dst [o + 1] = (byte) (c >> 8); dst [o + 2] = (byte) (c >> 16);
                }
                return;
            case PixelFormat.Format32bppRgb:
            case PixelFormat.Format32bppArgb:
                for (int i = 0; i < n; i++) {
                    uint c = src [srcOff + i];
                    int o = row + (x + i) * 4;
                    dst [o] = (byte) c; dst [o + 1] = (byte) (c >> 8); dst [o + 2] = (byte) (c >> 16); dst [o + 3] = (byte) (c >> 24);
                }
                return;
            case PixelFormat.Format32bppPArgb:
                for (int i = 0; i < n; i++) {
                    uint c = PremultiplyArgb (src [srcOff + i]);
                    int o = row + (x + i) * 4;
                    dst [o] = (byte) c; dst [o + 1] = (byte) (c >> 8); dst [o + 2] = (byte) (c >> 16); dst [o + 3] = (byte) (c >> 24);
                }
                return;
            case PixelFormat.Format48bppRgb:
                for (int i = 0; i < n; i++) {
                    uint c = src [srcOff + i];
                    int o = row + (x + i) * 6;
                    Put16 (dst, o, s_toLinear [c & 0xff]); Put16 (dst, o + 2, s_toLinear [(c >> 8) & 0xff]); Put16 (dst, o + 4, s_toLinear [(c >> 16) & 0xff]);
                }
                return;
            case PixelFormat.Format64bppArgb:
            case PixelFormat.Format64bppPArgb:
                for (int i = 0; i < n; i++) {
                    uint c = src [srcOff + i];
                    int o = row + (x + i) * 8;
                    int a13 = (int) (c >> 24) * 8192 / 255;
                    int b = s_toLinear [c & 0xff], g = s_toLinear [(c >> 8) & 0xff], r = s_toLinear [(c >> 16) & 0xff];
                    if (format == PixelFormat.Format64bppPArgb) { b = (b * a13) >> 13; g = (g * a13) >> 13; r = (r * a13) >> 13; }
                    Put16 (dst, o, b); Put16 (dst, o + 2, g); Put16 (dst, o + 4, r); Put16 (dst, o + 6, a13);
                }
                return;
            default:
                throw new ArgumentException ("Parameter is not valid.");
            }
        }

        private static void Put16 (byte[] d, int o, int v)
        {
            d [o] = (byte) v; d [o + 1] = (byte) (v >> 8);
        }

        /// <summary>Whether pixels of this format can be read and written at all (GDI+ refuses
        /// 16bpp greyscale and the CMYK/extended formats for anything but storage).</summary>
        internal static bool Convertible (PixelFormat format) => format switch
        {
            PixelFormat.Format1bppIndexed or PixelFormat.Format4bppIndexed or PixelFormat.Format8bppIndexed
                or PixelFormat.Format16bppRgb555 or PixelFormat.Format16bppRgb565 or PixelFormat.Format16bppArgb1555
                or PixelFormat.Format24bppRgb or PixelFormat.Format32bppRgb or PixelFormat.Format32bppArgb
                or PixelFormat.Format32bppPArgb or PixelFormat.Format48bppRgb or PixelFormat.Format64bppArgb
                or PixelFormat.Format64bppPArgb => true,
            _ => false,
        };

        /// <summary>Whether a Bitmap may be created in this format.</summary>
        internal static bool Creatable (PixelFormat format) => Convertible (format) || format == PixelFormat.Format16bppGrayScale;

        // ---- whole frames -----------------------------------------------------------------------

        /// <summary>A rectangle of the frame as straight ARGB, row-major.</summary>
        internal static uint[] ToArgb (GdipFrame f, Rectangle r)
        {
            var argb = new uint [r.Width * r.Height];
            for (int y = 0; y < r.Height; y++) ReadArgb (f, r.X, r.Y + y, r.Width, argb, y * r.Width);
            return argb;
        }

        /// <summary>The whole frame as straight RGBA bytes (what a scene's ImageBrush takes).</summary>
        internal static byte[] ToRgba (GdipFrame f, Rectangle r)
        {
            uint[] argb = ToArgb (f, r);
            var rgba = new byte [argb.Length * 4];
            for (int i = 0; i < argb.Length; i++) {
                uint c = argb [i];
                rgba [i * 4] = (byte) (c >> 16); rgba [i * 4 + 1] = (byte) (c >> 8); rgba [i * 4 + 2] = (byte) c; rgba [i * 4 + 3] = (byte) (c >> 24);
            }
            return rgba;
        }

        /// <summary>A copy of <paramref name="r"/> of the frame in <paramref name="format"/> (a
        /// straight copy when the format is the frame's own and the rows start on a byte).</summary>
        internal static GdipFrame Convert (GdipFrame f, Rectangle r, PixelFormat format, bool conversionPalette)
        {
            var dst = new GdipFrame (r.Width, r.Height, format) { DpiX = f.DpiX, DpiY = f.DpiY };
            if (format == f.Format && (r.X * f.BitsPerPixel) % 8 == 0) {
                int bytes = (r.Width * f.BitsPerPixel + 7) / 8, x0 = r.X * f.BitsPerPixel / 8;
                for (int y = 0; y < r.Height; y++)
                    Buffer.BlockCopy (f.Bits, (r.Y + y) * f.Stride + x0, dst.Bits, y * dst.Stride, bytes);
                if (f.Palette != null) { dst.Palette = (Color[]) f.Palette.Clone (); dst.PaletteFlags = f.PaletteFlags; }
                return dst;
            }
            if (dst.IsIndexed && conversionPalette)
                dst.Palette = ConversionPalette (format, out dst.PaletteFlags);
            var row = new uint [r.Width];
            var cache = new Dictionary<uint, int> ();
            for (int y = 0; y < r.Height; y++) {
                ReadArgb (f, r.X, r.Y + y, r.Width, row, 0);
                WriteArgb (dst, 0, y, r.Width, row, 0, cache);
            }
            return dst;
        }
    }
}
