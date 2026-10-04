// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Drawing onto a bitmap's pixels as gdiplus.dll does (EpScanBitmap + EpAlphaBlender):
//
//   EpScanBitmap::Start @1800c0d10    the bitmap is locked read/write in its own format when that is
//                                     one GDI+ blends into directly (1, 4, 8bpp, 555, 565, 24, 32RGB,
//                                     ARGB, PARGB), otherwise as 32bpp ARGB (1555, 16bpp grey, 48, 64,
//                                     64P), converted on the lock and back on the unlock
//   EpScanBitmap::NextBuffer @18002bda0   the brush writes a span of premultiplied ARGB into a scan
//                                     buffer; the previous span is blended out when the next is asked
//                                     for, clamped to the bitmap
//   EpAlphaBlender::BuildPipeline @18002a9e8   which ScanOperations blend a span into which format:
//        SourceOver, low quality:  PARGB, 32RGB  Blend_sRGB_sRGB in place
//                                  24RGB         Blend_sRGB_24 in place
//                                  ARGB          AlphaMultiply, Blend_sRGB_sRGB, AlphaDivide
//                                  555, 565      Dither_Blend_sRGB_555/565
//        SourceCopy:               PARGB copy; ARGB AlphaDivide; 24RGB/32RGB the premultiplied
//                                  colour as it is (Quantize_sRGB_24 / _32RGB)
//        High quality / gamma corrected: the 64-bit linear pipeline (GammaConvert_sRGB_sRGB64,
//                                  Blend_sRGB64_sRGB64, GammaConvert_sRGB64_sRGB)
//
//   Blend_sRGB_sRGB @180162890        d = s + ((d * (255 - sa) + 128) + ((...) >> 8)) >> 8 per
//                                     channel; sa == 0 leaves d, sa == 255 stores s
//

using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace System.Drawing.WebGpuBackend.Gdip
{
    internal sealed class GpScan
    {
        readonly GdipFrame _frame;
        readonly GdipFrame _work;      // the frame as locked: itself, or an ARGB copy
        readonly bool _converted;
        readonly PixelFormat _format;
        readonly bool _sourceCopy;
        readonly bool _linear;         // high quality / gamma corrected compositing
        public uint[] Buffer = new uint [256];
        int _x, _y, _n;
        bool _pending;

        /// <summary>Whether a bitmap of this format can be drawn on (EpScanBitmap locks any format
        /// GDI+ can convert to and from ARGB).</summary>
        public static bool Supports (PixelFormat f) => (f & PixelFormat.Indexed) == 0 && GdipPixels.Convertible (f);

        public int Width => _frame.Width;
        public int Height => _frame.Height;

        public GpScan (GdipFrame frame, CompositingMode mode, CompositingQuality quality)
        {
            _frame = frame;
            _sourceCopy = mode == CompositingMode.SourceCopy;
            _linear = quality == CompositingQuality.HighQuality || quality == CompositingQuality.GammaCorrected;
            switch (frame.Format) {
            case PixelFormat.Format1bppIndexed: case PixelFormat.Format4bppIndexed: case PixelFormat.Format8bppIndexed:
            case PixelFormat.Format16bppRgb555: case PixelFormat.Format16bppRgb565: case PixelFormat.Format24bppRgb:
            case PixelFormat.Format32bppRgb: case PixelFormat.Format32bppArgb: case PixelFormat.Format32bppPArgb:
                _work = frame;
                break;
            default:
                _work = GdipPixels.Convert (frame, new Rectangle (0, 0, frame.Width, frame.Height), PixelFormat.Format32bppArgb, false);
                _converted = true;
                break;
            }
            _format = _work.Format;
        }

        /// <summary>The scan buffer for [x, x + n) on row y, after blending out the previous one.</summary>
        public uint[] Next (int x, int y, int n)
        {
            Flush ();
            if (Buffer.Length < n) Buffer = new uint [Math.Max (n, Buffer.Length * 2)];
            _pending = !(y < 0 || y >= _work.Height || x < 0 || x >= _work.Width);
            _x = x; _y = y;
            _n = Math.Min (n, _work.Width - x);
            return Buffer;
        }

        public void Flush ()
        {
            if (!_pending || _n <= 0) { _pending = false; return; }
            _pending = false;
            Blend (_x, _y, _n);
        }

        public void End ()
        {
            Flush ();
            if (_converted) {
                GdipFrame back = GdipPixels.Convert (_work, new Rectangle (0, 0, _work.Width, _work.Height), _frame.Format, false);
                System.Buffer.BlockCopy (back.Bits, 0, _frame.Bits, 0, Math.Min (back.Bits.Length, _frame.Bits.Length));
            }
        }

        // ---- the blend --------------------------------------------------------------------------------

        void Blend (int x, int y, int n)
        {
            byte[] bits = _work.Bits;
            int row = y * _work.Stride;
            uint[] s = Buffer;
            bool is16 = _format == PixelFormat.Format16bppRgb565 || _format == PixelFormat.Format16bppRgb555;
            if (is16) { Blend16 (bits, row, x, y, n, _format == PixelFormat.Format16bppRgb565); return; }
            if (_linear && !_sourceCopy) {
                if (_format == PixelFormat.Format32bppRgb) BlendLinear32Rgb (bits, row, x, n);
                else BlendLinear (bits, row, x, n);
                return;
            }
            switch (_format) {
            case PixelFormat.Format32bppPArgb:
            case PixelFormat.Format32bppRgb:
                if (_sourceCopy) {
                    for (int i = 0; i < n; i++) {
                        uint c = s [i];
                        // 32RGB too: the premultiplied colour as it is, alpha byte and all (the oracle
                        // reads it back raw: a Clear to 80FF8040 leaves 80804020).
                        Put32 (bits, row + (x + i) * 4, c);
                    }
                } else {
                    for (int i = 0; i < n; i++) {
                        int o = row + (x + i) * 4;
                        uint c = s [i];
                        uint a = c >> 24;
                        if (a == 0) continue;
                        if (a != 255) c = Over (c, Get32 (bits, o));
                        Put32 (bits, o, c);
                    }
                }
                break;
            case PixelFormat.Format32bppArgb:
                for (int i = 0; i < n; i++) {
                    int o = row + (x + i) * 4;
                    uint c = s [i];
                    if (_sourceCopy) {
                        Put32 (bits, o, GdipPixels.UnpremultiplyArgb (c));
                        continue;
                    }
                    uint d = GdipPixels.PremultiplyArgb (Get32 (bits, o));
                    uint a = c >> 24;
                    if (a == 255) d = c;
                    else if (a != 0) d = Over (c, d);
                    Put32 (bits, o, GdipPixels.UnpremultiplyArgb (d));
                }
                break;
            case PixelFormat.Format24bppRgb:
                for (int i = 0; i < n; i++) {
                    int o = row + (x + i) * 3;
                    uint c = s [i];
                    if (!_sourceCopy) {
                        uint a = c >> 24;
                        if (a == 0) continue;
                        if (a != 255) c = Over (c, 0xff000000u | (uint) bits [o + 2] << 16 | (uint) bits [o + 1] << 8 | bits [o]);
                    }
                    bits [o] = (byte) c; bits [o + 1] = (byte) (c >> 8); bits [o + 2] = (byte) (c >> 16);
                }
                break;
            default: {
                // 1555 and the rest: through ARGB.
                var tmp = new uint [n];
                GdipPixels.ReadArgb (_work, x, y, n, tmp, 0);
                for (int i = 0; i < n; i++) {
                    uint c = s [i];
                    if (_sourceCopy) { tmp [i] = c | 0xff000000u; continue; }
                    uint a = c >> 24;
                    if (a == 0) continue;
                    tmp [i] = a == 255 ? c : Over (c, tmp [i]);
                }
                GdipPixels.WriteArgb (_work, x, y, n, tmp, 0);
                break;
            }
            }
        }


        // ---- 16bpp: ordered dither (Dither_sRGB_565 @1800c5fd0, Dither_Blend_sRGB_565 @1800c5db0, the
        // 555 pair @1800c5f10 / @1800c5c60), and the high-quality BlendLinear_sRGB_565/555<0>
        // @1800c6fa8 (partial-alpha runs blended in 64 bits, opaque runs dithered, transparent left).
        // The dither phase is (x & 3) | (y & 3) << 2, x counted from the start of the call -- which
        // the high-quality op makes once per run, each starting again at the span x.

        static readonly int[] s_d5 = { 0, 4, 1, 5, 6, 2, 7, 3, 1, 5, 0, 4, 7, 3, 6, 2 };
        static readonly int[] s_d6 = { 0, 2, 0, 2, 3, 1, 3, 1, 0, 2, 0, 2, 3, 1, 3, 1 };

        static int Q5 (int v) => v > 31 ? 31 : v;
        static int Q6 (int v) => v > 63 ? 63 : v;

        static ushort Quantize (uint b8, uint g16, uint r24, int k, bool is565)
        {
            int b = Q5 ((int) (((uint) s_d5 [k] + b8) >> 3));
            int r = Q5 ((int) ((((uint) s_d5 [k] << 16) + r24) >> 19));
            if (is565) {
                int g = Q6 ((int) ((((uint) s_d6 [k] << 8) + g16) >> 10));
                return (ushort) (b + (g + r * 0x40) * 0x20);
            } else {
                int g = Q5 ((int) ((((uint) s_d5 [k] << 8) + g16) >> 11));
                return (ushort) (b + (g + r * 0x20) * 0x20);
            }
        }

        static uint Expand16 (ushort p, bool is565)
        {
            uint r5, g, b5 = (uint) (p & 0x1f);
            if (is565) { r5 = (uint) (p >> 11); uint g6 = (uint) (p >> 5 & 0x3f); g = g6 << 2 | g6 >> 4; }
            else { r5 = (uint) (p >> 10 & 0x1f); uint g5 = (uint) (p >> 5 & 0x1f); g = g5 << 3 | g5 >> 2; }
            return 0xff000000u | (r5 << 3 | r5 >> 2) << 16 | g << 8 | (b5 << 3 | b5 >> 2);
        }

        void Blend16 (byte[] bits, int row, int x, int y, int n, bool is565)
        {
            uint[] s = Buffer;
            int yk = (y & 3) << 2;
            if (_sourceCopy || !_linear) {
                for (int i = 0; i < n; i++) {
                    int k = ((x + i) & 3) | yk;
                    uint c = s [i];
                    int o = row + (x + i) * 2;
                    if (_sourceCopy) {
                        Put16 (bits, o, Quantize (c & 0xff, c & 0xff00, c & 0xff0000, k, is565));
                        continue;
                    }
                    uint a = c >> 24;
                    if (a == 0) continue;
                    uint rb = c & 0xff0000, gg = c & 0xff00, bb = c & 0xff;
                    if (a != 0xff) {
                        ushort d = Get16 (bits, o);
                        uint ia = 0xff - a;
                        uint gd, t;
                        if (is565) {
                            uint g6 = (uint) (d >> 5 & 0x3f);
                            gd = (g6 << 2 | g6 >> 4) * ia + 0x80;
                            uint r5 = (uint) (d >> 11);
                            t = ((uint) ((d & 0x1f) >> 2) | ((uint) (d & 0x1f) | (r5 << 3 | (uint) (d >> 13)) << 13) << 3) * ia + 0x800080;
                        } else {
                            uint g5 = (uint) (d >> 5 & 0x1f);
                            gd = (g5 << 3 | g5 >> 2) * ia + 0x80;
                            uint r5 = (uint) (d >> 10 & 0x1f);
                            t = ((uint) ((d & 0x1f) >> 2) | ((uint) (d & 0x1f) | (r5 << 3 | r5 >> 2) << 13) << 3) * ia + 0x800080;
                        }
                        t = ((t >> 8) & 0xff00ff) + t >> 8;
                        gg = ((gd >> 8 & 0xff) + gd & 0xff00) + gg;
                        bb = (t & 0xff) + bb;
                        rb = (t & 0xff00ff) + rb;
                    }
                    Put16 (bits, o, Quantize (bb, gg, rb, k, is565));
                }
                return;
            }
            // BlendLinear_sRGB_565<0>: runs of partial alpha through 64 bits; opaque runs dithered.
            int i0 = 0;
            while (i0 < n) {
                int run = 0;
                while (i0 + run < n && (uint) ((s [i0 + run] >> 24) - 1) <= 0xfd) run++;
                if (run > 0) {
                    for (int j = 0; j < run; j++) {
                        int o = row + (x + i0 + j) * 2;
                        ulong dst = GpScanLinear.ToLinear (Expand16 (Get16 (bits, o), is565));
                        ulong src = GpScanLinear.AlphaMultiply64 (GpScanLinear.ToLinear (GdipPixels.UnpremultiplyArgb (s [i0 + j])));
                        uint c = GpScanLinear.ToSrgb (GpScanLinear.Blend64 (src, dst));
                        int k = ((x + j) & 3) | yk;
                        Put16 (bits, o, Quantize (c & 0xff, c & 0xff00, c & 0xff0000, k, is565));
                    }
                    i0 += run;
                    continue;
                }
                int op = 0;
                while (i0 + op < n && (s [i0 + op] & 0xff000000u) == 0xff000000u) op++;
                for (int j = 0; j < op; j++) {
                    uint c = s [i0 + j];
                    int k = ((x + j) & 3) | yk;
                    Put16 (bits, row + (x + i0 + j) * 2, Quantize (c & 0xff, c & 0xff00, c & 0xff0000, k, is565));
                }
                i0 += op;
                while (i0 < n && s [i0] <= 0xffffff) i0++;
            }
        }

        static ushort Get16 (byte[] b, int o) => (ushort) (b [o] | b [o + 1] << 8);
        static void Put16 (byte[] b, int o, ushort v) { b [o] = (byte) v; b [o + 1] = (byte) (v >> 8); }

        // BlendLinear_sRGB_32RGB<0> @1800c6230: opaque copied, transparent left, partial alpha blended
        // in 64 bits with the destination taken as it is (its alpha byte too), no alpha divide.
        void BlendLinear32Rgb (byte[] bits, int row, int x, int n)
        {
            uint[] s = Buffer;
            for (int i = 0; i < n; i++) {
                uint c = s [i];
                int o = row + (x + i) * 4;
                uint a = c >> 24;
                if (a == 0) continue;
                if (a == 0xff) { Put32 (bits, o, c); continue; }
                ulong dst = GpScanLinear.ToLinear (Get32 (bits, o));
                ulong src = GpScanLinear.AlphaMultiply64 (GpScanLinear.ToLinear (GdipPixels.UnpremultiplyArgb (c)));
                Put32 (bits, o, GpScanLinear.ToSrgb (GpScanLinear.Blend64 (src, dst)));
            }
        }

        static uint Get32 (byte[] b, int o) => (uint) (b [o] | b [o + 1] << 8 | b [o + 2] << 16 | b [o + 3] << 24);

        static void Put32 (byte[] b, int o, uint c)
        {
            b [o] = (byte) c; b [o + 1] = (byte) (c >> 8); b [o + 2] = (byte) (c >> 16); b [o + 3] = (byte) (c >> 24);
        }

        /// <summary>Blend_sRGB_sRGB: premultiplied source over premultiplied destination.</summary>
        public static uint Over (uint s, uint d)
        {
            uint ia = 255 - (s >> 24);
            uint ag = ((d >> 8) & 0xff00ffu) * ia + 0x800080u;
            uint rb = (d & 0xff00ffu) * ia + 0x800080u;
            return ((((rb >> 8) & 0xff00ffu) + rb) >> 8 & 0xff00ffu) + ((((ag >> 8) & 0xff00ffu) + ag) & 0xff00ff00u) + s;
        }

        // ---- the 64-bit linear pipeline (CompositingQuality.HighQuality / GammaCorrected) -----------------
        //
        // Source: AlphaDivide, GammaConvert_sRGB_sRGB64, AlphaMultiply_sRGB64. Destination:
        // GammaConvert_sRGB_sRGB64 (+ AlphaMultiply_sRGB64 when it has alpha), Blend_sRGB64_sRGB64,
        // then back. (Ported in GpScanLinear.)

        // The high-quality / gamma-corrected pipeline (EpAlphaBlender::BuildPipeline, its generic
        // path): the source unpremultiplied (AlphaDivide_sRGB_Original), linearised
        // (GammaConvert_sRGB_sRGB64) and premultiplied in 64 bits (AlphaMultiply_sRGB64); the
        // destination brought to ARGB (ConvertIntoCanonical), linearised and premultiplied; the
        // two blended (Blend_sRGB64_sRGB64); the result divided (AlphaDivide_sRGB64), back to sRGB
        // (GammaConvert_sRGB64_sRGB) and to the destination's format. Every pixel of the span makes
        // the round trip, blended or not.
        void BlendLinear (byte[] bits, int row, int x, int n)
        {
            var d = new uint [n];
            GdipPixels.ReadArgb (_work, x, _y, n, d, 0);
            for (int i = 0; i < n; i++) {
                ulong dst = GpScanLinear.AlphaMultiply64 (GpScanLinear.ToLinear (d [i]));
                ulong src = GpScanLinear.AlphaMultiply64 (GpScanLinear.ToLinear (GdipPixels.UnpremultiplyArgb (Buffer [i])));
                d [i] = GpScanLinear.ToSrgb (GpScanLinear.AlphaDivide64 (GpScanLinear.Blend64 (src, dst)));
            }
            GdipPixels.WriteArgb (_work, x, _y, n, d, 0);
        }
    }

    /// <summary>The sRGB64 scan operations: 16-bit lanes, alpha 0x2000 = 1.</summary>
    internal static class GpScanLinear
    {
        public static readonly ushort[] LinAlpha = {
            0, 32, 64, 96, 128, 160, 192, 224, 257, 289, 321, 353, 385, 417, 449, 481,
            514, 546, 578, 610, 642, 674, 706, 738, 771, 803, 835, 867, 899, 931, 963, 995,
            1028, 1060, 1092, 1124, 1156, 1188, 1220, 1252, 1285, 1317, 1349, 1381, 1413, 1445, 1477, 1509,
            1542, 1574, 1606, 1638, 1670, 1702, 1734, 1766, 1799, 1831, 1863, 1895, 1927, 1959, 1991, 2023,
            2056, 2088, 2120, 2152, 2184, 2216, 2248, 2280, 2313, 2345, 2377, 2409, 2441, 2473, 2505, 2537,
            2570, 2602, 2634, 2666, 2698, 2730, 2762, 2794, 2827, 2859, 2891, 2923, 2955, 2987, 3019, 3051,
            3084, 3116, 3148, 3180, 3212, 3244, 3276, 3308, 3341, 3373, 3405, 3437, 3469, 3501, 3533, 3565,
            3598, 3630, 3662, 3694, 3726, 3758, 3790, 3822, 3855, 3887, 3919, 3951, 3983, 4015, 4047, 4079,
            4112, 4144, 4176, 4208, 4240, 4272, 4304, 4336, 4369, 4401, 4433, 4465, 4497, 4529, 4561, 4593,
            4626, 4658, 4690, 4722, 4754, 4786, 4818, 4850, 4883, 4915, 4947, 4979, 5011, 5043, 5075, 5107,
            5140, 5172, 5204, 5236, 5268, 5300, 5332, 5364, 5397, 5429, 5461, 5493, 5525, 5557, 5589, 5621,
            5654, 5686, 5718, 5750, 5782, 5814, 5846, 5878, 5911, 5943, 5975, 6007, 6039, 6071, 6103, 6135,
            6168, 6200, 6232, 6264, 6296, 6328, 6360, 6392, 6425, 6457, 6489, 6521, 6553, 6585, 6617, 6649,
            6682, 6714, 6746, 6778, 6810, 6842, 6874, 6906, 6939, 6971, 7003, 7035, 7067, 7099, 7131, 7163,
            7196, 7228, 7260, 7292, 7324, 7356, 7388, 7420, 7453, 7485, 7517, 7549, 7581, 7613, 7645, 7677,
            7710, 7742, 7774, 7806, 7838, 7870, 7902, 7934, 7967, 7999, 8031, 8063, 8095, 8127, 8159, 8192
        };
        public static readonly ushort[] LinColor = {
            0, 2, 5, 7, 10, 12, 15, 17, 20, 22, 25, 27, 30, 33, 36, 39,
            42, 46, 50, 53, 57, 61, 66, 70, 75, 80, 85, 90, 95, 101, 106, 112,
            118, 125, 131, 138, 145, 152, 159, 166, 174, 182, 190, 198, 206, 215, 224, 233,
            242, 252, 261, 271, 281, 292, 302, 313, 324, 335, 347, 358, 370, 382, 395, 407,
            420, 433, 446, 460, 474, 488, 502, 516, 531, 546, 561, 576, 592, 608, 624, 641,
            657, 674, 691, 709, 726, 744, 762, 781, 799, 818, 838, 857, 877, 897, 917, 937,
            958, 979, 1001, 1022, 1044, 1066, 1088, 1111, 1134, 1157, 1181, 1204, 1228, 1253, 1277, 1302,
            1327, 1353, 1378, 1404, 1431, 1457, 1484, 1511, 1539, 1566, 1594, 1623, 1651, 1680, 1709, 1739,
            1768, 1798, 1829, 1859, 1890, 1921, 1953, 1985, 2017, 2049, 2082, 2115, 2148, 2182, 2216, 2250,
            2285, 2320, 2355, 2390, 2426, 2462, 2498, 2535, 2572, 2610, 2647, 2685, 2723, 2762, 2801, 2840,
            2880, 2920, 2960, 3000, 3041, 3082, 3124, 3166, 3208, 3250, 3293, 3336, 3380, 3423, 3467, 3512,
            3557, 3602, 3647, 3693, 3739, 3785, 3832, 3879, 3927, 3974, 4022, 4071, 4120, 4169, 4218, 4268,
            4318, 4369, 4419, 4471, 4522, 4574, 4626, 4679, 4732, 4785, 4838, 4892, 4947, 5001, 5056, 5111,
            5167, 5223, 5280, 5336, 5393, 5451, 5509, 5567, 5625, 5684, 5743, 5803, 5863, 5923, 5984, 6045,
            6106, 6168, 6230, 6293, 6356, 6419, 6482, 6546, 6611, 6675, 6740, 6806, 6871, 6938, 7004, 7071,
            7138, 7206, 7274, 7342, 7411, 7480, 7550, 7619, 7690, 7760, 7831, 7903, 7974, 8047, 8119, 8192
        };
        public static readonly short[] InvThreshold = {
            1, 3, 6, 8, 11, 13, 16, 18, 21, 23, 26, 28, 31, 34, 37, 40,
            44, 47, 51, 55, 59, 63, 67, 72, 77, 82, 87, 92, 97, 103, 109, 115,
            121, 127, 134, 141, 148, 155, 162, 169, 177, 185, 193, 202, 210, 219, 228, 237,
            246, 256, 266, 276, 286, 296, 307, 318, 329, 340, 352, 364, 376, 388, 400, 413,
            426, 439, 453, 466, 480, 494, 508, 523, 538, 553, 568, 584, 599, 616, 632, 648,
            665, 682, 699, 717, 735, 753, 771, 790, 808, 827, 847, 866, 886, 906, 927, 947,
            968, 989, 1011, 1033, 1054, 1077, 1099, 1122, 1145, 1168, 1192, 1216, 1240, 1265, 1289, 1314,
            1340, 1365, 1391, 1417, 1443, 1470, 1497, 1524, 1552, 1580, 1608, 1636, 1665, 1694, 1723, 1753,
            1783, 1813, 1843, 1874, 1905, 1937, 1968, 2000, 2033, 2065, 2098, 2131, 2165, 2198, 2232, 2267,
            2302, 2337, 2372, 2408, 2443, 2480, 2516, 2553, 2590, 2628, 2666, 2704, 2742, 2781, 2820, 2859,
            2899, 2939, 2980, 3020, 3061, 3103, 3144, 3186, 3228, 3271, 3314, 3357, 3401, 3445, 3489, 3534,
            3579, 3624, 3669, 3715, 3762, 3808, 3855, 3902, 3950, 3998, 4046, 4095, 4144, 4193, 4243, 4293,
            4343, 4393, 4444, 4496, 4547, 4599, 4652, 4705, 4758, 4811, 4865, 4919, 4973, 5028, 5083, 5139,
            5195, 5251, 5307, 5364, 5422, 5479, 5537, 5596, 5654, 5713, 5773, 5832, 5893, 5953, 6014, 6075,
            6137, 6199, 6261, 6324, 6387, 6450, 6514, 6578, 6642, 6707, 6772, 6838, 6904, 6970, 7037, 7104,
            7172, 7239, 7308, 7376, 7445, 7514, 7584, 7654, 7724, 7795, 7866, 7938, 8010, 8082, 8155, 32767
        };
        public static readonly byte[] InvStart = {
            0, 49, 71, 86, 99, 110, 120, 129, 137, 145, 152, 158, 165, 171, 177, 182,
            188, 193, 198, 202, 207, 212, 216, 220, 225, 229, 233, 237, 240, 244, 248, 251,
            255, 0, 0, 0, 0, 0, 0, 0, 128, 128, 128, 128, 128, 128, 128, 128,
            128, 64, 32, 16, 8, 4, 2, 1, 1, 2, 4, 8, 16, 32, 64, 128,
            255, 128, 128, 128, 128, 128, 128, 128, 129, 66, 36, 24, 24, 36, 66, 129,
            128, 0, 0, 0, 8, 0, 0, 0, 128, 0, 8, 0, 128, 0, 8, 0,
            136, 0, 34, 0, 136, 0, 34, 0, 136, 34, 136, 34, 136, 34, 136, 34,
            170, 68, 170, 17, 170, 68, 170, 17, 170, 85, 170, 81, 170, 85, 170, 21,
            170, 85, 170, 85, 170, 85, 170, 85, 238, 85, 187, 85, 238, 85, 187, 85,
            119, 221, 119, 221, 119, 221, 119, 221, 119, 255, 221, 255, 119, 255, 221, 255,
            239, 255, 254, 255, 239, 255, 254, 255, 255, 255, 255, 247, 255, 255, 255, 127,
            136, 68, 34, 17, 136, 68, 34, 17, 17, 34, 68, 136, 17, 34, 68, 136,
            204, 102, 51, 153, 204, 102, 51, 153, 51, 102, 204, 153, 51, 102, 204, 153,
            193, 224, 112, 56, 28, 14, 7, 131, 131, 7, 14, 28, 56, 112, 224, 193,
            136, 136, 136, 136, 136, 136, 136, 136, 255, 0, 0, 0, 255, 0, 0, 0,
            85, 85, 85, 85, 85, 85, 85, 85, 255, 0, 255, 0, 255, 0, 255, 0
        };

        // GammaConvert_sRGB_sRGB64 @180162510 (LinearizeLUT @1802aa450, LinearizeAlphaLUT @1802aa050).
        public static ulong ToLinear (uint c)
        {
            ulong lo = LinColor [c & 0xff] | (ulong) LinColor [c >> 8 & 0xff] << 16;
            ulong hi = LinColor [c >> 16 & 0xff] | (ulong) LinAlpha [c >> 24] << 16;
            return lo | hi << 32;
        }

        static short Lane (ulong v, int i) => (short) (v >> (16 * i));

        static ulong Pack (int b, int g, int r, int a)
            => (ulong) (ushort) b | (ulong) (ushort) g << 16 | (ulong) (ushort) r << 32 | (ulong) (ushort) a << 48;

        // AlphaMultiply_sRGB64 @180033010.
        public static ulong AlphaMultiply64 (ulong v)
        {
            int a = Lane (v, 3);
            if (a == 0x2000) return v;
            if (a == 0) return 0;
            int M (int c) => (short) ((uint) (c * a * 8) >> 16);
            return Pack (M (Lane (v, 0)), M (Lane (v, 1)), M (Lane (v, 2)), a);
        }

        // AlphaDivide_sRGB64 @1801620b0.
        public static ulong AlphaDivide64 (ulong v)
        {
            int a = Lane (v, 3);
            if (((a + 0xffff) & 0xffff) >= 0x2001) return v;
            int D (int c) => (short) ((c << 13) / a);
            return Pack (D (Lane (v, 0)), D (Lane (v, 1)), D (Lane (v, 2)), a);
        }

        // Blend_sRGB64_sRGB64 @180032f50.
        public static ulong Blend64 (ulong s, ulong d)
        {
            int sa = Lane (s, 3);
            if (sa == 0) return d;
            if (sa == 0x2000) return s;
            int k = 0x2000 - sa;
            int B (int i) => (short) (Lane (s, i) + (short) ((Lane (d, i) * k + 0x1000) >> 13));
            return Pack (B (0), B (1), B (2), B (3));
        }

        // sRGB::ConvertTosRGB @1800330c8.
        public static uint ToSrgb (ulong v)
        {
            int a = Lane (v, 3);
            uint ab = a < 1 ? 0u : a < 0x2000 ? (uint) (byte) (a * 0xff >> 13) : 0xffu;
            uint C (int c)
            {
                if (c < 1) return 0;
                if (c >= 0x2000) return 0xff;
                int b = InvStart [(c >> 8) & 0xff];
                while (InvThreshold [b] < c) b++;
                return (uint) (byte) b;
            }
            return ab << 24 | C (Lane (v, 2)) << 16 | C (Lane (v, 1)) << 8 | C (Lane (v, 0));
        }
    }
}
