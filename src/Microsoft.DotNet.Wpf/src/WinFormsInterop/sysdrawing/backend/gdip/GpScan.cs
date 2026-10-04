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
            if (_linear && !_sourceCopy) { BlendLinear (bits, row, x, n); return; }
            switch (_format) {
            case PixelFormat.Format32bppPArgb:
            case PixelFormat.Format32bppRgb:
                if (_sourceCopy) {
                    for (int i = 0; i < n; i++) {
                        uint c = s [i];
                        if (_format == PixelFormat.Format32bppRgb) c |= 0xff000000u;
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
                // 555 / 565: through ARGB (Dither_Blend_* is not yet reproduced).
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

        void BlendLinear (byte[] bits, int row, int x, int n)
        {
            var d = new uint [n];
            GdipPixels.ReadArgb (_work, x, _y, n, d, 0);
            bool hasAlpha = _format == PixelFormat.Format32bppArgb || _format == PixelFormat.Format32bppPArgb;
            for (int i = 0; i < n; i++) {
                uint c = Buffer [i];
                if ((c >> 24) == 0) continue;
                d [i] = GpScanLinear.Over (c, _format == PixelFormat.Format32bppPArgb ? GdipPixels.UnpremultiplyArgb (Get32 (bits, row + (x + i) * 4)) : d [i], hasAlpha);
            }
            if (_format == PixelFormat.Format32bppPArgb) {
                for (int i = 0; i < n; i++) if ((Buffer [i] >> 24) != 0) Put32 (bits, row + (x + i) * 4, GdipPixels.PremultiplyArgb (d [i]));
                return;
            }
            GdipPixels.WriteArgb (_work, x, _y, n, d, 0);
        }
    }

    /// <summary>Placeholder for the 64-bit linear blend; replaced by the port of the sRGB64 ops.</summary>
    internal static class GpScanLinear
    {
        public static uint Over (uint srcPargb, uint dstArgb, bool hasAlpha)
        {
            uint s = GdipPixels.UnpremultiplyArgb (srcPargb);
            float sa = (s >> 24) / 255f, da = hasAlpha ? (dstArgb >> 24) / 255f : 1f;
            float oa = sa + da * (1 - sa);
            uint r = 0;
            for (int sh = 0; sh < 24; sh += 8) {
                float sc = Lin ((s >> sh) & 0xff), dc = Lin ((dstArgb >> sh) & 0xff);
                float oc = oa > 0 ? (sc * sa + dc * da * (1 - sa)) / oa : 0;
                r |= (uint) Math.Clamp ((int) MathF.Round (Srgb (oc) * 255f), 0, 255) << sh;
            }
            return r | (uint) Math.Clamp ((int) MathF.Round (oa * 255f), 0, 255) << 24;
        }

        static float Lin (uint c) { float v = c / 255f; return v <= 0.04045f ? v / 12.92f : MathF.Pow ((v + 0.055f) / 1.055f, 2.4f); }
        static float Srgb (float v) => v <= 0.0031308f ? v * 12.92f : 1.055f * MathF.Pow (v, 1 / 2.4f) - 0.055f;
    }
}
