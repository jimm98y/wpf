// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// GDI+ 1.1's palettes and dithers, in managed code: GdipBitmapConvertFormat and GdipInitializePalette
// as gdiplus.dll (10.0.26100, arm64) runs them, read off the binary with its public symbols.
//
// ConvertFormat (GdipBitmapConvertFormat 0x180053390 -> CopyOnWriteBitmap::ConvertFormat 0x18007e758)
//   The alpha threshold is ByteSaturate(floor(percent * 2.55f + 0.5f)); a dither type above 9 is
//   InvalidParameter. Converting to the bitmap's own format does nothing unless that format is
//   indexed. Otherwise a new bitmap of the format is filled row by row through an EpAlphaBlender
//   (EpAlphaBlender::InitializeFormatConversion 0x18002b810): the source row is read as straight
//   32bpp ARGB (an indexed source through its palette, padded to the full size with opaque black by
//   CloneColorPaletteResize 0x1800fc590), then
//     - into a direct format: the ordinary conversion, except that DitherTypeOrdered4x4 into a 16bpp
//       format turns on Dither_sRGB_555/565's 4x4 dither (0x1800c5f10/0x1800c5fd0);
//     - into an indexed format: a CHalftone (CHalftone::Initialize 0x1800bcf88) picks each pixel's
//       index and Quantize_8_1/8_4_Unaligned packs them.
//   A palette passed in becomes the new bitmap's palette (GpMemoryBitmap::SetPalette 0x1800fe760,
//   flags as given), even when the format is not indexed. The image's flags, raw format and
//   resolution are the cached ImageInfo's and do not change.
//
// CHalftone::Initialize
//   No palette is InvalidParameter. PaletteType Custom counts as Optimal; with Optimal or
//   FixedBlackAndWhite only DitherTypeSolid and ErrorDiffusion are allowed. With a fixed halftone
//   type (3..9) and an ordered or spiral dither, the type's own palette (GetFixedPalette) is built and
//   a permutation table maps each of its entries to the nearest entry of the TARGET palette; the
//   dither picks an entry of the fixed palette and the table turns it into the target's. A target
//   palette with more entries than the format has indices is InvalidParameter. The transparent
//   index -- what a pixel below the alpha threshold takes -- is the target's entry nearest 0.
//     None, Solid      ScanOperation::NearestColor 0x1800bd710: the 4096-entry table of the nearest
//                      target entry to (r,g,b)*17 for the top four bits of each channel
//                      (CHalftone::BuildNearestEntryTable 0x1800bc308, FindNearestColor 0x180157d90:
//                      squared ARGB distance, the first of equals).
//     4x4/8x8 ordered, spiral, dual spiral
//                      StandardDither 0x1802047c0 / StandardDither8x8 0x180204700: a table per
//                      pattern cell and channel value of the fixed-palette index contribution, the
//                      level floor(v / (255/(n-1))) plus one level when error * (N' / (255/(n-1)))
//                      reaches the cell's threshold, where error = v - floor(level * (255/(n-1)) +
//                      0.001) and N' is the pattern's threshold count (plus one for a two-level
//                      channel); all in float32.
//     16x16 ordered    Ordered16Dither 0x180204320: per value a level and an error scaled by n-1,
//                      compared against the 16x16 Bayer cell -- with the RED and BLUE errors
//                      exchanged, as the table is built (red's error is scaled by blue's levels).
//     ErrorDiffusion   ScanOperation::ErrorDiffusion 0x1800bc980: serpentine Floyd-Steinberg in
//                      sixteenths over the nearest-entry table.
//
// GdipInitializePalette (0x1800634e0 -> CHalftone::InitializePalette 0x1800bd4e0)
//   Custom leaves the palette as it is. A fixed type is CHalftone::GetFixedPalette (0x180203760):
//   levels per channel from the table at 0x1802ae5d0, each (int)(255f/(n-1) * i), red slowest, then
//   (except for 252, 256 and black-and-white) the sixteen VGA colours it lacks
//   (PaletteInsertUniqueColors 0x180204410); flags = type << 8. With a transparent colour the last
//   entry of a full palette is dropped and transparent black appended. Optimal needs a bitmap and
//   2..256 colours (one fewer when a transparent colour is appended): CHalftone::CreateMedianCutPalette
//   (0x1800bc6d0) runs CColorReduceMC (the median cut below) over the bitmap's pixels as 24bpp RGB;
//   flags 0.
//

using System.Drawing.Imaging;

namespace System.Drawing
{
    internal static class GdipHalftone
    {
        // ---- the tables -------------------------------------------------------------------------

        // Levels of red, green and blue per palette type (0x1802ae5d0).
        private static readonly int[] s_levels = {
            0, 0, 0,  0, 0, 0,  0, 0, 0,  2, 2, 2,  3, 3, 3,  4, 4, 4,  5, 5, 5,  6, 6, 6,  6, 7, 6,  8, 8, 4,
        };

        // The colours PaletteInsertUniqueColors adds when missing (0x1802ae650).
        private static readonly uint[] s_unique = {
            0xff000000, 0xffffffff, 0xffc0c0c0, 0xff808080, 0xff800000, 0xffff0000, 0xff008000, 0xff00ff00,
            0xff000080, 0xff0000ff, 0xff808000, 0xffffff00, 0xff800080, 0xffff00ff, 0xff008080, 0xff00ffff,
        };

        /// <summary>A DITHERPATTERN: width, height, threshold count and the cells, row by row.</summary>
        private sealed class Pattern
        {
            internal readonly int W, H, N;
            internal readonly byte[] Cells;
            internal Pattern (int w, int h, int n, byte[] cells) { W = w; H = h; N = n; Cells = cells; }
        }

        private static readonly Pattern s_ordered16 = new Pattern (16, 16, 255, new byte [] {
            1, 129, 33, 161, 9, 137, 41, 169, 3, 131, 35, 163, 11, 139, 43, 171,
            193, 65, 225, 97, 201, 73, 233, 105, 195, 67, 227, 99, 203, 75, 235, 107,
            49, 177, 17, 145, 57, 185, 25, 153, 51, 179, 19, 147, 59, 187, 27, 155,
            241, 113, 209, 81, 249, 121, 217, 89, 243, 115, 211, 83, 251, 123, 219, 91,
            13, 141, 45, 173, 5, 133, 37, 165, 15, 143, 47, 175, 7, 135, 39, 167,
            205, 77, 237, 109, 197, 69, 229, 101, 207, 79, 239, 111, 199, 71, 231, 103,
            61, 189, 29, 157, 53, 181, 21, 149, 63, 191, 31, 159, 55, 183, 23, 151,
            253, 125, 221, 93, 245, 117, 213, 85, 255, 127, 223, 95, 247, 119, 215, 87,
            4, 132, 36, 164, 12, 140, 44, 172, 2, 130, 34, 162, 10, 138, 42, 170,
            196, 68, 228, 100, 204, 76, 236, 108, 194, 66, 226, 98, 202, 74, 234, 106,
            52, 180, 20, 148, 60, 188, 28, 156, 50, 178, 18, 146, 58, 186, 26, 154,
            244, 116, 212, 84, 252, 124, 220, 92, 242, 114, 210, 82, 250, 122, 218, 90,
            16, 144, 48, 176, 8, 136, 40, 168, 14, 142, 46, 174, 6, 134, 38, 166,
            208, 80, 240, 112, 200, 72, 232, 104, 206, 78, 238, 110, 198, 70, 230, 102,
            64, 192, 32, 160, 56, 184, 24, 152, 62, 190, 30, 158, 54, 182, 22, 150,
            255, 128, 224, 96, 248, 120, 216, 88, 254, 126, 222, 94, 246, 118, 214, 86,
        });

        // Per dither type (0x1802ae580): Solid, Ordered4x4/8x8/16x16, Spiral4x4/8x8, DualSpiral4x4/8x8.
        private static readonly Pattern[] s_patterns = {
            null,
            new Pattern (1, 1, 1, new byte [] { 1 }),
            new Pattern (4, 4, 16, new byte [] { 1, 9, 3, 11, 13, 5, 15, 7, 4, 12, 2, 10, 16, 8, 14, 6 }),
            new Pattern (8, 8, 64, new byte [] {
                1, 33, 9, 41, 3, 35, 11, 43, 49, 17, 57, 25, 51, 19, 59, 27, 13, 45, 5, 37, 15, 47, 7, 39, 61, 29, 53, 21, 63, 31, 55, 23,
                4, 36, 12, 44, 2, 34, 10, 42, 52, 20, 60, 28, 50, 18, 58, 26, 16, 48, 8, 40, 14, 46, 6, 38, 64, 32, 56, 24, 62, 30, 54, 22 }),
            s_ordered16,
            new Pattern (4, 4, 16, new byte [] { 16, 5, 9, 13, 12, 1, 2, 6, 8, 4, 3, 10, 15, 11, 7, 14 }),
            new Pattern (8, 8, 64, new byte [] {
                62, 58, 46, 34, 29, 41, 53, 61, 54, 50, 28, 21, 17, 25, 49, 57, 42, 38, 16, 5, 9, 13, 37, 45, 32, 20, 12, 1, 2, 6, 22, 33,
                35, 24, 8, 4, 3, 10, 18, 30, 47, 39, 15, 11, 7, 14, 40, 44, 59, 51, 27, 19, 23, 26, 52, 56, 63, 55, 43, 31, 36, 48, 60, 64 }),
            new Pattern (4, 4, 8, new byte [] { 1, 2, 6, 7, 4, 3, 5, 8, 6, 7, 1, 2, 5, 8, 4, 3 }),
            new Pattern (8, 8, 32, new byte [] {
                16, 5, 9, 13, 20, 24, 28, 17, 12, 1, 2, 6, 27, 32, 29, 21, 8, 4, 3, 10, 23, 31, 30, 25, 15, 11, 7, 14, 19, 29, 22, 18,
                20, 24, 28, 17, 16, 5, 9, 13, 27, 32, 29, 21, 12, 1, 2, 6, 23, 31, 30, 25, 8, 4, 3, 10, 19, 29, 22, 18, 15, 11, 7, 14 }),
            null,
        };

        internal const int Custom = 0, Optimal = 1, FixedBW = 2, ErrorDiffusion = 9;

        private static ArgumentException Invalid () => new ArgumentException ("Parameter is not valid.");

        // ---- palettes ---------------------------------------------------------------------------

        /// <summary>CHalftone::GetFixedPalette: the entries of fixed palette <paramref name="type"/>
        /// (2..9) and its flags (type &lt;&lt; 8).</summary>
        internal static uint[] FixedPalette (int type, out int flags)
        {
            flags = type << 8;
            if (type == FixedBW) return new uint [] { 0xff000000, 0xffffffff };
            int nr = s_levels [type * 3], ng = s_levels [type * 3 + 1], nb = s_levels [type * 3 + 2];
            var p = new uint [256];
            int n = 0;
            for (int r = 0; r < nr; r++)
                for (int g = 0; g < ng; g++)
                    for (int b = 0; b < nb; b++) {
                        int rv = (int) (255f / (float) (nr - 1) * (float) r);
                        int gv = (int) (255f / (float) (ng - 1) * (float) g);
                        int bv = (int) ((float) b * (255f / (float) (nb - 1)));
                        p [n++] = 0xff000000u | (uint) (byte) rv << 16 | (uint) (byte) gv << 8 | (byte) bv;
                    }
            if (type < 8)
                foreach (uint c in s_unique) {
                    if (n > 0xff) break;
                    if (Array.IndexOf (p, c, 0, n) < 0) p [n++] = c;
                }
            Array.Resize (ref p, n);
            return p;
        }

        /// <summary>GdipInitializePalette: a palette of <paramref name="type"/>; for Optimal, of
        /// <paramref name="colorCount"/> colours chosen from <paramref name="bitmap"/>'s pixels. With
        /// <paramref name="transparent"/>, transparent black is its last entry. Null for Custom
        /// (GDI+ leaves the caller's palette as it was).</summary>
        internal static uint[] InitializePalette (int type, int colorCount, bool transparent, GdipFrame bitmap, out int flags)
        {
            flags = 0;
            if (type == Custom) return null;
            uint[] p;
            if (type == Optimal) {
                if ((uint) (colorCount - 2) >= 0xffu || bitmap == null) throw Invalid ();
                p = MedianCutPalette (bitmap, transparent ? colorCount - 1 : colorCount);
            } else if (type >= FixedBW && type <= 9) {
                p = FixedPalette (type, out flags);
                if (transparent && p.Length > 0xff) Array.Resize (ref p, p.Length - 1);
            } else {
                throw Invalid ();
            }
            if (transparent) {
                Array.Resize (ref p, p.Length + 1);
                p [p.Length - 1] = 0;
            }
            return p;
        }

        // ---- the median cut (CColorReduceMC) ----------------------------------------------------
        //
        // A 32x32x32 histogram of the top five bits of red, green and blue, every cell STARTING AT 1
        // (CColorReduceMC::Reset 0x18020d790), counts saturating at 0x1fffff (AddPixels 0x1800c4d70),
        // and the first (max + 1) distinct colours in the order they occur (AddUniqueColor
        // 0x180031cc8). GenerateLOGPALETTE (0x1800c4e10): when the image has no more distinct colours
        // than are wanted, they are the palette, in order. Otherwise the whole cube is one box, and the
        // box with the greatest (extent * count) on one axis -- red on ties, then green; boxes holding
        // a single empty cell skipped, the first box on equal products (SplitBestBox 0x1800c53d8) -- is
        // cut after its weighted mean on that axis, until there are max boxes or none can be cut. The
        // boxes are sorted by count, largest first, stably (SortBoxes 0x18020d7f8), and each gives its
        // weighted mean, (mean * 255 + 15) / 31 per channel.

        private sealed class Box
        {
            internal int R0, R1, G0, G1, B0, B1;
            internal uint SumR, SumG, SumB, Count;
            internal int Index;
            internal Box Copy () => (Box) MemberwiseClone ();
        }

        internal static uint[] MedianCutPalette (GdipFrame f, int max)
        {
            var hist = new uint [32768];
            for (int i = 0; i < hist.Length; i++) hist [i] = 1;
            var unique = new uint [max + 1];
            int nUnique = 0;
            var row = new uint [f.Width];
            // The bitmap is read as 24bpp RGB (a 32bppPArgb one therefore as its premultiplied colour).
            PixelFormat read = f.Format == PixelFormat.Format32bppPArgb ? PixelFormat.Format32bppArgb : f.Format;
            for (int y = 0; y < f.Height; y++) {
                GdipPixels.ReadArgb (f.Bits, y * f.Stride, read, f.Palette, 0, f.Width, row, 0);
                for (int x = 0; x < f.Width; x++) {
                    uint c = row [x];
                    int r = (int) (c >> 16) & 0xff, g = (int) (c >> 8) & 0xff, b = (int) c & 0xff;
                    if (nUnique <= max) {
                        uint key = (uint) (r | g << 8 | b << 16);
                        if (Array.IndexOf (unique, key, 0, nUnique) < 0) unique [nUnique++] = key;
                    }
                    int idx = (b >> 3) << 10 | (g >> 3) << 5 | r >> 3;
                    if (hist [idx] != 0x1fffff) hist [idx]++;
                }
            }
            if (max <= 0) throw Invalid ();
            uint[] pal;
            if (nUnique <= max) {
                pal = new uint [nUnique];
                for (int i = 0; i < nUnique; i++) {
                    uint k = unique [i];
                    pal [i] = 0xff000000u | (k & 0xff) << 16 | (k & 0xff00) | (k >> 16) & 0xff;
                }
                return pal;
            }
            var boxes = new Box [max];
            boxes [0] = new Box { R0 = 0, R1 = 31, G0 = 0, G1 = 31, B0 = 0, B1 = 31 };
            Shrink (hist, boxes [0]);
            if (boxes [0].Count == 0) throw Invalid ();
            int n = 1;
            while (n < max && Split (hist, boxes, n)) n++;
            // SortBoxes: an insertion sort of the indices by count, largest first.
            for (int i = 0; i < n; i++) boxes [i].Index = i;
            for (int i = 1; i < n; i++) {
                int v = boxes [i].Index, j = i;
                do {
                    if (boxes [v].Count <= boxes [boxes [j - 1].Index].Count) break;
                    boxes [j].Index = boxes [j - 1].Index;
                    j--;
                } while (j > 0);
                boxes [j].Index = v;
            }
            pal = new uint [n];
            for (int i = 0; i < n; i++) {
                Box bx = boxes [boxes [i].Index];
                uint r = Level (bx.SumR / bx.Count), g = Level (bx.SumG / bx.Count), b = Level (bx.SumB / bx.Count);
                pal [i] = 0xff000000u | (r & 0xff) << 16 | (g & 0xff) << 8 | (b & 0xff);
            }
            return pal;

            static uint Level (uint mean) => (uint) ((int) ((mean & 0xffff) * 255 + 15) / 31);
        }

        // CColorReduceMC::ShrinkBox 0x1800c5238: the box's populated extent and its weighted sums.
        private static void Shrink (uint[] hist, Box bx)
        {
            int r0 = bx.R1, r1 = bx.R0, g0 = bx.G1, g1 = bx.G0, b0 = bx.B1, b1 = bx.B0;
            uint sr = 0, sg = 0, sb = 0, count = 0;
            for (int b = bx.B0; b <= bx.B1; b++)
                for (int g = bx.G0; g <= bx.G1; g++)
                    for (int r = bx.R0; r <= bx.R1; r++) {
                        uint c = hist [(b << 5 | g) << 5 | r];
                        if (c == 0) continue;
                        sr += (uint) r * c; sg += (uint) g * c; sb += (uint) b * c; count += c;
                        if (r < r0) r0 = r;
                        if (r > r1) r1 = r;
                        if (g < g0) g0 = g;
                        if (g > g1) g1 = g;
                        if (b < b0) b0 = b;
                        if (b > b1) b1 = b;
                    }
            bx.R0 = r0; bx.R1 = r1; bx.G0 = g0; bx.G1 = g1; bx.B0 = b0; bx.B1 = b1;
            bx.SumR = sr; bx.SumG = sg; bx.SumB = sb; bx.Count = count;
        }

        // CColorReduceMC::SplitBestBox.
        private static bool Split (uint[] hist, Box[] boxes, int n)
        {
            Box best = null;
            int axis = 0;
            uint bestV = 0;
            for (int i = 0; i < n; i++) {
                Box bx = boxes [i];
                if (bx.Count == 1) continue;
                uint er = (uint) (bx.R1 - bx.R0) * bx.Count, eg = (uint) (bx.G1 - bx.G0) * bx.Count, eb = (uint) (bx.B1 - bx.B0) * bx.Count;
                if (er < eg || er < eb) {
                    if (eg < eb) { if (bestV < eb) { axis = 2; best = bx; bestV = eb; } }
                    else if (bestV < eg) { axis = 1; best = bx; bestV = eg; }
                } else if (bestV < er) { axis = 0; best = bx; bestV = er; }
            }
            if (best == null) return false;
            Box nb = best.Copy ();
            switch (axis) {
            case 0: { int m = (byte) (best.SumR / best.Count); nb.R1 = m; best.R0 = (byte) (m + 1); break; }
            case 1: { int m = (byte) (best.SumG / best.Count); nb.G1 = m; best.G0 = (byte) (m + 1); break; }
            default: { int m = (byte) (best.SumB / best.Count); nb.B1 = m; best.B0 = (byte) (m + 1); break; }
            }
            boxes [n] = nb;
            Shrink (hist, best);
            Shrink (hist, nb);
            return true;
        }

        // ---- ConvertFormat ----------------------------------------------------------------------

        private static Color[] ToColors (uint[] p)
        {
            var c = new Color [p.Length];
            for (int i = 0; i < p.Length; i++) c [i] = Color.FromArgb (unchecked ((int) p [i]));
            return c;
        }

        /// <summary>GDI+'s ByteSaturate(floor(percent * 2.55f + 0.5f)), each step in float32.</summary>
        internal static int AlphaThreshold (float percent)
        {
            float v = percent * 2.55f;
            v += 0.5f;
            int t = (int) MathF.Floor (v);
            return t < 0 ? 0 : t > 255 ? 255 : t;
        }

        /// <summary>CopyOnWriteBitmap::ConvertFormat: <paramref name="src"/> as a new frame in
        /// <paramref name="format"/>, or null when there is nothing to do. Throws ArgumentException for
        /// what GDI+ refuses.</summary>
        internal static GdipFrame Convert (GdipFrame src, PixelFormat format, int dither, int paletteType, Color[] palette, int paletteFlags, int threshold)
        {
            if ((uint) dither > 9) throw Invalid ();
            bool indexed = (format & PixelFormat.Indexed) != 0;
            if (format == src.Format && !indexed) return null;
            if (!GdipPixels.Convertible (format) || !GdipPixels.Convertible (src.Format)) throw Invalid ();
            var dst = new GdipFrame (src.Width, src.Height, format) { DpiX = src.DpiX, DpiY = src.DpiY };
            int w = src.Width;
            var row = new uint [w];
            if (!indexed) {
                bool dither16 = dither == 2 && (format == PixelFormat.Format16bppRgb555 || format == PixelFormat.Format16bppRgb565);
                for (int y = 0; y < src.Height; y++) {
                    if (dither16) {
                        // 32bppPArgb reaches a 16bpp format as straight ARGB (GdipPixels.Transfer).
                        GdipPixels.ReadArgb (src.Bits, y * src.Stride, src.Format == PixelFormat.Format32bppPArgb ? PixelFormat.Format32bppArgb : src.Format,
                            src.Palette, 0, w, row, 0);
                        Dither16 (dst.Bits, y * dst.Stride, format == PixelFormat.Format16bppRgb565, row, w, y);
                    } else {
                        GdipPixels.Transfer (src.Bits, y * src.Stride, src.Format, src.Palette, 0, w, dst.Bits, y * dst.Stride, format, null, 0, row);
                    }
                }
            } else {
                var ht = new Halftone (paletteType, dither, threshold, palette, w);
                if (palette.Length > 1 << Image.GetPixelFormatSize (format)) throw Invalid ();
                var idx = new byte [w];
                for (int y = 0; y < src.Height; y++) {
                    GdipPixels.ReadArgb (src, 0, y, w, row, 0);
                    ht.Row (row, idx, y);
                    Pack (dst.Bits, y * dst.Stride, format, idx);
                }
            }
            if (palette != null) {
                dst.Palette = (Color[]) palette.Clone ();
                dst.PaletteFlags = paletteFlags;
            }
            return dst;
        }

        // Quantize_8_1/8_4_Unaligned and the 8bpp copy: indices into the destination row.
        private static void Pack (byte[] dst, int row, PixelFormat format, byte[] idx)
        {
            switch (format) {
            case PixelFormat.Format8bppIndexed:
                Buffer.BlockCopy (idx, 0, dst, row, idx.Length);
                break;
            case PixelFormat.Format4bppIndexed:
                for (int i = 0; i < idx.Length; i++) {
                    int o = row + (i >> 1);
                    if ((i & 1) == 0) dst [o] = (byte) ((dst [o] & 0x0f) | (idx [i] & 15) << 4);
                    else dst [o] = (byte) ((dst [o] & 0xf0) | (idx [i] & 15));
                }
                break;
            default:
                for (int i = 0; i < idx.Length; i++) {
                    int o = row + (i >> 3), s = 7 - (i & 7);
                    dst [o] = (byte) ((dst [o] & ~(1 << s)) | (idx [i] & 1) << s);
                }
                break;
            }
        }

        // Dither_sRGB_555/565 with the dither on: per pixel offsets from a 4x4 matrix (0x1802b14e0,
        // scaled per channel at 0x1802b1520/0x1802b1560/0x1802b16b0) added before the truncation,
        // the result clamped by table (0x1802b16f0 / 0x1802b15a0).
        private static readonly int[] s_dither16 = { 0, 4, 1, 5, 6, 2, 7, 3, 1, 5, 0, 4, 7, 3, 6, 2 };
        private static readonly int[] s_dither565g = { 0, 2, 0, 2, 3, 1, 3, 1, 0, 2, 0, 2, 3, 1, 3, 1 };

        private static void Dither16 (byte[] dst, int row, bool is565, uint[] src, int w, int y)
        {
            for (int x = 0; x < w; x++) {
                int k = (x & 3) | (y & 3) << 2;
                uint c = src [x];
                int b = Math.Min (31, (s_dither16 [k] + (int) (c & 0xff)) >> 3);
                int r = Math.Min (31, ((s_dither16 [k] << 16) + (int) (c & 0xff0000)) >> 19);
                int v;
                if (is565) {
                    int g = Math.Min (63, ((s_dither565g [k] << 8) + (int) (c & 0xff00)) >> 10);
                    v = b + (g + r * 64) * 32;
                } else {
                    int g = Math.Min (31, ((s_dither16 [k] << 8) + (int) (c & 0xff00)) >> 11);
                    v = b + (g + r * 32) * 32;
                }
                dst [row + x * 2] = (byte) v; dst [row + x * 2 + 1] = (byte) (v >> 8);
            }
        }

        /// <summary>A CHalftone: what turns a row of straight ARGB into indices of the target
        /// palette.</summary>
        private sealed class Halftone
        {
            private readonly int _dither, _threshold;
            private readonly byte _transparent;
            private readonly Color[] _target;
            private readonly int[] _targetArgb;
            private GdipPixels.IndexMap _nearest;
            private readonly byte[] _perm = new byte [256];
            private readonly int _nr = 2, _ng = 2, _nb = 2;
            private readonly Pattern _pattern;
            private readonly byte[] _table;
            private int[] _cur, _next;

            internal Halftone (int type, int dither, int threshold, Color[] target, int width)
            {
                if (target == null) throw Invalid ();
                _dither = dither;
                _threshold = threshold;
                _target = target;
                _targetArgb = new int [target.Length];
                for (int i = 0; i < target.Length; i++) _targetArgb [i] = target [i].ToArgb ();
                bool nearest = dither == 1 || dither == ErrorDiffusion;
                if (type == Custom) type = Optimal;
                if ((type == Optimal || type == FixedBW) && !nearest) throw Invalid ();
                if (type < 0 || type > 9) throw Invalid ();
                _pattern = s_patterns [dither];
                _nr = s_levels [type * 3]; _ng = s_levels [type * 3 + 1]; _nb = s_levels [type * 3 + 2];
                if (type > Optimal && !nearest) {
                    uint[] fixedPal = FixedPalette (type, out _);
                    for (int i = 0; i < fixedPal.Length; i++) _perm [i] = GdipPixels.FindNearest (target, fixedPal [i]);
                }
                _transparent = GdipPixels.FindNearest (target, 0);
                if (_pattern != null && !nearest && _nb > 1 && _ng > 1 && _nr > 1)
                    _table = _pattern.W * _pattern.H <= 64 ? CellTable () : Ordered16Table ();
                if (dither == ErrorDiffusion) { _cur = new int [(width + 6) * 3]; _next = new int [(width + 6) * 3]; }
            }

            // CHalftone::RedValue/GrnValue/BluValue and the *Error twins, in float32.
            private static int Value (int v, int n) => (int) MathF.Floor ((float) v / (255f / (float) (n - 1)));
            private static int Error (int v, int n) => v - (int) MathF.Floor ((float) Value (v, n) * (255f / (float) (n - 1)) + 0.001f);

            private byte[] CellTable ()
            {
                Pattern p = _pattern;
                int cells = p.W * p.H, gb = _ng * _nb;
                var t = new byte [cells * 0x300];
                float n = p.N, n1 = n + 1f;
                float sr = _nr != 2 ? n : n1, sg = _ng == 2 ? n1 : n, sb = _nb == 2 ? n1 : n;
                for (int v = 0; v < 256; v++) {
                    int rv = Value (v, _nr) * gb, gv = Value (v, _ng) * _nb, bv = Value (v, _nb);
                    float er = (float) Error (v, _nr) * (sr / (255f / (float) (_nr - 1)));
                    float eg = (float) Error (v, _ng) * (sg / (255f / (float) (_ng - 1)));
                    float eb = (float) Error (v, _nb) * (sb / (255f / (float) (_nb - 1)));
                    for (int c = 0; c < cells; c++) {
                        float th = p.Cells [c];
                        t [c * 0x300 + v] = (byte) ((er >= th ? gb : 0) + rv);
                        t [c * 0x300 + 0x100 + v] = (byte) ((eg >= th ? _nb : 0) + gv);
                        t [c * 0x300 + 0x200 + v] = (byte) ((eb >= th ? 1 : 0) + bv);
                    }
                }
                return t;
            }

            // The 16x16 table: blue level, red's error times (red levels - 1) -- the exchange is
            // GDI+'s -- green level, green error, red level, blue's error times (blue levels - 1).
            private byte[] Ordered16Table ()
            {
                var t = new byte [0x600];
                int gb = _ng * _nb;
                for (int v = 0; v < 256; v++) {
                    t [v] = (byte) Value (v, _nb);
                    t [0x200 + v] = (byte) (Value (v, _ng) * _nb);
                    t [0x400 + v] = (byte) (Value (v, _nr) * gb);
                    t [0x100 + v] = (byte) (int) MathF.Floor ((float) (_nr - 1) * (float) Error (v, _nb));
                    t [0x300 + v] = (byte) (int) MathF.Floor ((float) (_ng - 1) * (float) Error (v, _ng));
                    t [0x500 + v] = (byte) (int) MathF.Floor ((float) (_nb - 1) * (float) Error (v, _nr));
                }
                return t;
            }

            internal void Row (uint[] argb, byte[] idx, int y)
            {
                int w = argb.Length;
                switch (_dither) {
                case 0: case 1: case 2: case 3: case 5: case 6: case 7: case 8:
                    if (_table == null || _dither < 2) {
                        _nearest ??= new GdipPixels.IndexMap (_target);
                        for (int x = 0; x < w; x++) {
                            uint c = argb [x];
                            idx [x] = (c >> 24) >= (uint) _threshold ? (byte) _nearest [c] : _transparent;
                        }
                    } else {
                        Pattern p = _pattern;
                        int rowBase = (y % p.H) * p.W * 0x300;
                        for (int x = 0, col = 0; x < w; x++) {
                            uint c = argb [x];
                            if ((c >> 24) >= (uint) _threshold) {
                                int o = rowBase + col * 0x300;
                                int k = (byte) (_table [o + (int) ((c >> 16) & 0xff)] + _table [o + 0x100 + (int) ((c >> 8) & 0xff)] + _table [o + 0x200 + (int) (c & 0xff)]);
                                idx [x] = _perm [k];
                            } else idx [x] = _transparent;
                            if (++col >= p.W) col = 0;
                        }
                    }
                    break;
                case 4:
                    if (_table == null) goto case 0;
                    for (int x = 0; x < w; x++) {
                        uint c = argb [x];
                        byte th = s_ordered16.Cells [(x & 15) + ((y & 15) << 4)];
                        if ((c >> 24) >= (uint) _threshold) {
                            int r = (int) (c >> 16) & 0xff, g = (int) (c >> 8) & 0xff, b = (int) c & 0xff;
                            int k = _table [b];
                            if (th < _table [0x100 + b]) k++;
                            k = (byte) (k + _table [0x200 + g]);
                            if (th < _table [0x300 + g]) k = (byte) (k + _nb);
                            k = (byte) (k + _table [0x400 + r]);
                            if (th < _table [0x500 + r]) k = (byte) (k + (byte) (_ng * _nb));
                            idx [x] = _perm [k];
                        } else idx [x] = _transparent;
                    }
                    break;
                default:
                    Diffuse (argb, idx, y);
                    break;
                }
            }

            // ScanOperation::ErrorDiffusion: errors in sixteenths carried in two row buffers, even
            // rows right to left and odd rows left to right, the colour clamped and looked up
            // through the nearest-entry table, the error measured from that entry (whatever the
            // alpha says), 7/16 ahead, 3/16 below behind, 5/16 below, 1/16 below ahead.
            private void Diffuse (uint[] argb, byte[] idx, int y)
            {
                _nearest ??= new GdipPixels.IndexMap (_target);
                int w = argb.Length;
                (_cur, _next) = (_next, _cur);
                Array.Clear (_next);
                int[] cur = _cur, next = _next;
                for (int i = 0; i < w; i++) {
                    uint c = argb [i];
                    cur [3 * i + 6] += (int) ((c >> 16) & 0xff) * 16;
                    cur [3 * i + 7] += (int) ((c >> 8) & 0xff) * 16;
                    cur [3 * i + 8] += (int) (c & 0xff) * 16;
                }
                bool leftToRight = (y & 1) != 0;
                int step = leftToRight ? 1 : -1;
                for (int n = 0, i = leftToRight ? 0 : w - 1; n < w; n++, i += step) {
                    int o = 3 * i + 6;
                    int r = Math.Clamp (cur [o] >> 4, 0, 255), g = Math.Clamp (cur [o + 1] >> 4, 0, 255), b = Math.Clamp (cur [o + 2] >> 4, 0, 255);
                    int k = _nearest [(uint) (r << 16 | g << 8 | b)];
                    int pk = k < _targetArgb.Length ? _targetArgb [k] : 0;
                    idx [i] = (argb [i] >> 24) >= (uint) _threshold ? (byte) k : _transparent;
                    int er = r - ((pk >> 16) & 0xff), eg = g - ((pk >> 8) & 0xff), eb = b - (pk & 0xff);
                    int ahead = o + 3 * step, behind = o - 3 * step;
                    cur [ahead] += er * 7; cur [ahead + 1] += eg * 7; cur [ahead + 2] += eb * 7;
                    next [behind] += er * 3; next [behind + 1] += eg * 3; next [behind + 2] += eb * 3;
                    next [o] += er * 5; next [o + 1] += eg * 5; next [o + 2] += eb * 5;
                    next [ahead] += er; next [ahead + 1] += eg; next [ahead + 2] += eb;
                }
            }
        }
    }
}
