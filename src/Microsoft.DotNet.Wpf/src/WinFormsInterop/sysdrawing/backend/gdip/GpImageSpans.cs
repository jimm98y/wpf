// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The image spans of gdiplus.dll (arm64, 10.0.26100, public PDB): each samples a locked band of the
// source bitmap (premultiplied ARGB, DpBitmap) for the device pixels of one span.
//
//   CreateOutputSpan @180030378           the span for an interpolation mode: an integer translate is
//                                         Bilinear whatever was asked; HighQualityBicubic and High on a
//                                         translate/scale matrix -> DpOutputSpanStretch<1>, otherwise
//                                         bicubic; Bicubic -> bicubic; NearestNeighbor -> nearest;
//                                         HighQualityBilinear on translate/scale -> DpOutputSpanStretch<0>;
//                                         the rest bilinear (the _Identity copy for an integer translate
//                                         with Tile or Clamp; the MMX span is off: HasMMX = 0)
//   DpOutputNearestNeighborSpan / DpOutputBicubicImageSpan ctor @1800c8140 / @1800c8758
//                                         device-to-source = (InferAffine(points <- srcRect) x world-to-
//                                         device)^-1
//   DpOutputBicubicImageSpan::OutputSpan @1800c84e0 (shared by both)   the span's two ends mapped to the
//                                         source, 16.16 start and step: floor(v * 65536 + 0.5)
//   DpOutputNearestNeighborSpan::OutputSpanIncremental @1800c8640   (x + 0x8000) >> 16
//   DpOutputBicubicImageSpan::OutputSpanIncremental @1800c8bb0   a 4x4 neighbourhood, wrapped as a
//                                         whole unless it lies inside; columns first then the row,
//                                         Do1DBicubic @1800c8a88 on GpBitmapScaler::cubicCoeffTable
//                                         (64 phases, 16.16 weights), clamped to [0,255] and colour <= A
//   DpOutputBilinearSpan::OutputSpan @18002a0a0   float stepping along the span, 11-bit weights
//                                         floor(frac * 2048 + 0.5), (.. + 0x200000) >> 22
//   DpOutputBilinearSpan_Identity::OutputSpan @18002a650   a straight copy at -round(translation)
//   ApplyWrapMode @180159608 / Apply1DWrapModeX @1800302b0
//

namespace System.Drawing.WebGpuBackend.Gdip
{
    /// <summary>DpBitmap: a locked band of the source, premultiplied ARGB, rows of <see cref="Width"/>.</summary>
    internal sealed class DpBitmapSrc
    {
        public int Width, Height;
        public uint[] Px;

        public DpBitmapSrc (int w, int h, uint[] px) { Width = w; Height = h; Px = px; }
    }

    /// <summary>DpImageAttributes: wrap mode, clamp colour (used as it is, as a premultiplied pixel),
    /// and the source-rectangle clamp flag.</summary>
    internal struct DpImageAttr
    {
        public int Wrap;
        public uint Clamp;
        public int SrcRectClamp;
        public static DpImageAttr Default => new DpImageAttr { Wrap = 4 };
    }

    internal static class GpWrap
    {
        static int Mod (int v, int n) => v < 0 ? n - (~v - ~v / n * n) - 1 : v - v / n * n;

        static int ModFlip (int v, int n)
        {
            int m = Mod (v, n);
            return ((v - m) / n & 1) != 0 ? n - m - 1 : m;
        }

        /// <summary>ApplyWrapMode @180159608.</summary>
        public static void Apply (int mode, ref int x, ref int y, int w, int h)
        {
            switch (mode) {
            case 0: x = Mod (x, w); y = Mod (y, h); break;
            case 1: x = ModFlip (x, w); y = Mod (y, h); break;
            case 2: x = Mod (x, w); y = ModFlip (y, h); break;
            case 3: x = ModFlip (x, w); y = ModFlip (y, h); break;
            }
        }

        /// <summary>Apply1DWrapModeX @1800302b0 (and its Y twin).</summary>
        public static void Apply1D (int mode, ref int x, int w, bool isY)
        {
            bool flip = isY ? mode == 2 || mode == 3 : mode == 1 || mode == 3;
            if (mode < 0 || mode > 3) return;
            x = flip ? ModFlip (x, w) : Mod (x, w);
        }
    }

    /// <summary>The spans that step through the source in 16.16 (nearest neighbour, bicubic).</summary>
    internal abstract class GpIncrementalImageSpan : GpSpan
    {
        protected readonly DpBitmapSrc Src;
        protected readonly int Wrap;
        protected readonly uint Clamp;
        GpMatrix _deviceToSrc;

        protected GpIncrementalImageSpan (GpScan scan, DpBitmapSrc src, in GpMatrix worldToDevice, DpImageAttr ia, PointF[] pts, RectangleF srcRect)
            : base (scan)
        {
            Src = src; Wrap = ia.Wrap; Clamp = ia.Clamp;
            GpMatrix m = GpMatrix.CreateIdentity ();
            GpGraphics.InferAffine (pts, srcRect, ref m);
            m = GpMatrix.Multiply (m, worldToDevice);
            _deviceToSrc = m;
            if (!_deviceToSrc.IsInvertible || !_deviceToSrc.Invert ()) {
                _deviceToSrc = worldToDevice;
                _deviceToSrc.Invert ();
            }
        }

        protected override void Fill (uint[] buf, int y, int x, int n)
        {
            float x0 = x, y0 = y, x1 = x + n, y1 = y;
            _deviceToSrc.Transform (ref x0, ref y0);
            _deviceToSrc.Transform (ref x1, ref y1);
            int fx = (int) MathF.Floor (x0 * 65536f + 0.5f) - Bias;
            int fy = (int) MathF.Floor (y0 * 65536f + 0.5f);
            float fn = n;
            int dx = (int) MathF.Floor ((x1 - x0) * 65536f / fn + 0.5f);
            int dy = (int) MathF.Floor ((y1 - y0) * 65536f / fn + 0.5f);
            if (GpGraphics.Dbg) Console.Error.WriteLine ($"  inc y={y} x={x} n={n} inv={_deviceToSrc} p0=({x0:R},{y0:R}) p1=({x1:R}) fx={fx} fy={fy} dx={dx} dy={dy}");
            Incremental (buf, n, fx, fy, dx, dy);
        }

        static readonly int Bias = int.TryParse (Environment.GetEnvironmentVariable ("GP_BIAS"), out int b) ? b : 0;
        protected abstract void Incremental (uint[] buf, int n, int x, int y, int dx, int dy);

        protected uint Pixel (int x, int y) => x < 0 || y < 0 || x >= Src.Width || y >= Src.Height ? Clamp : Src.Px [y * Src.Width + x];
    }

    /// <summary>DpOutputNearestNeighborSpan.</summary>
    internal sealed class GpNearestSpan : GpIncrementalImageSpan
    {
        public GpNearestSpan (GpScan scan, DpBitmapSrc src, in GpMatrix w2d, DpImageAttr ia, PointF[] pts, RectangleF srcRect)
            : base (scan, src, w2d, ia, pts, srcRect) { }

        protected override void Incremental (uint[] buf, int n, int x, int y, int dx, int dy)
        {
            int w = Src.Width, h = Src.Height;
            int ax = x + 0x8000, ay = y + 0x8000;
            for (int i = 0; i < n; i++) {
                int ix = ax >> 16, iy = ay >> 16;
                if ((uint) ix >= (uint) w || (uint) iy >= (uint) h) GpWrap.Apply (Wrap, ref ix, ref iy, w, h);
                buf [i] = Pixel (ix, iy);
                ax += dx; ay += dy;
            }
        }
    }

    /// <summary>DpOutputBicubicImageSpan.</summary>
    internal sealed class GpBicubicSpan : GpIncrementalImageSpan
    {
        public GpBicubicSpan (GpScan scan, DpBitmapSrc src, in GpMatrix w2d, DpImageAttr ia, PointF[] pts, RectangleF srcRect)
            : base (scan, src, w2d, ia, pts, srcRect) { }

        /// <summary>GpBitmapScaler::cubicCoeffTable @1802b1780: the kernel at distance i/64, 16.16.</summary>
        internal static readonly int[] Cubic = {
            65536, 65496, 65379, 65186, 64920, 64583, 64177, 63705, 63168, 62569, 61911, 61195, 60424, 59600, 58725, 57802,
            56832, 55818, 54763, 53668, 52536, 51369, 50169, 48939, 47680, 46395, 45087, 43757, 42408, 41042, 39661, 38268,
            36864, 35452, 34035, 32614, 31192, 29771, 28353, 26941, 25536, 24141, 22759, 21391, 20040, 18708, 17397, 16110,
            14848, 13614, 12411, 11240, 10104, 9005, 7945, 6927, 5952, 5023, 4143, 3313, 2536, 1814, 1149, 544,
            0, -496, -961, -1395, -1800, -2176, -2523, -2843, -3136, -3403, -3645, -3862, -4056, -4227, -4375, -4502,
            -4608, -4694, -4761, -4809, -4840, -4854, -4851, -4833, -4800, -4753, -4693, -4620, -4536, -4441, -4335, -4220,
            -4096, -3964, -3825, -3679, -3528, -3372, -3211, -3047, -2880, -2711, -2541, -2370, -2200, -2031, -1863, -1698,
            -1536, -1378, -1225, -1077, -936, -802, -675, -557, -448, -349, -261, -184, -120, -69, -31, -8,
            0,
        };

        /// <summary>DpOutputBicubicImageSpanNS::Do1DBicubic @1800c8a88.</summary>
        internal static uint Do1D (uint[] p, int o, int f)
        {
            int w1 = Cubic [f], w0 = Cubic [64 + f], w2 = Cubic [64 - f], w3 = Cubic [128 - f];
            int Ch (int sh) => (int) ((p [o + 3] >> sh & 0xff) * w3 + (p [o + 2] >> sh & 0xff) * w2 + (p [o + 1] >> sh & 0xff) * w1 + (p [o] >> sh & 0xff) * w0) >> 16;
            int a = Ch (24);
            a = a < 0 ? 0 : a > 255 ? 255 : a;
            int r = Ch (16), g = Ch (8), b = Ch (0);
            r = r < 0 ? 0 : r > a ? a : r;
            g = g < 0 ? 0 : g > a ? a : g;
            b = b < 0 ? 0 : b > a ? a : b;
            return (uint) (a << 24 | r << 16 | g << 8 | b);
        }

        readonly int[] _xs = new int [4], _ys = new int [4];
        readonly uint[] _b = new uint [16];

        protected override void Incremental (uint[] buf, int n, int x, int y, int dx, int dy)
        {
            int w = Src.Width, h = Src.Height;
            for (int i = 0; i < n; i++) {
                int ix = x >> 16, iy = y >> 16;
                for (int k = 0; k < 4; k++) { _xs [k] = ix - 1 + k; _ys [k] = iy - 1 + k; }
                if (Wrap != 4) {
                    bool inside = (uint) (ix - 1) < (uint) Math.Max (w - 4, 0) && (uint) (iy - 1) < (uint) Math.Max (h - 4, 0);
                    if (!inside)
                        for (int k = 0; k < 4; k++) GpWrap.Apply (Wrap, ref _xs [k], ref _ys [k], w, h);
                }
                for (int r = 0; r < 4; r++)
                    for (int c = 0; c < 4; c++)
                        _b [r + 4 * c] = Pixel (_xs [c], _ys [r]);
                int fy = (y >> 10) & 0x3f, fx = (x >> 10) & 0x3f;
                for (int c = 0; c < 4; c++) _b [c] = Do1D (_b, 4 * c, fy);
                buf [i] = Do1D (_b, 0, fx);
                x += dx; y += dy;
            }
        }
    }

    /// <summary>DpOutputBilinearSpan (the non-MMX one).</summary>
    internal sealed class GpBilinearSpan : GpSpan
    {
        static readonly bool Fix = Environment.GetEnvironmentVariable ("GP_FIX") == "1";
        static readonly int DbgX = int.TryParse (Environment.GetEnvironmentVariable ("GP_DX"), out int a) ? a : -1;
        static readonly int DbgY = int.TryParse (Environment.GetEnvironmentVariable ("GP_DY"), out int b) ? b : -1;
        readonly DpBitmapSrc _src;
        readonly int _wrap;
        readonly uint _clamp;
        readonly GpMatrix _inv;

        public GpBilinearSpan (GpScan scan, DpBitmapSrc src, in GpMatrix srcToDevice, DpImageAttr ia) : base (scan)
        {
            _src = src; _wrap = ia.Wrap; _clamp = ia.Clamp;
            _inv = GpMatrix.CreateIdentity ();
            GpMatrix m = srcToDevice;
            if (m.IsInvertible) { _inv = m; _inv.Invert (); }
        }

        /// <summary>For a texture: the matrix and wrap mode of the brush.</summary>
        public GpBilinearSpan (GpScan scan, DpBitmapSrc src, in GpMatrix inverse, int wrap) : base (scan)
        {
            _src = src; _wrap = wrap; _clamp = 0; _inv = inverse;
        }

        protected override void Fill (uint[] buf, int y, int left, int n)
        {
            float fy = y, xl = left, xr = left + n;
            float x0 = xl, y0 = fy, x1 = xr, y1 = fy;
            _inv.Transform (ref x0, ref y0);
            _inv.Transform (ref x1, ref y1);
            float sx = (x1 - x0) / (float) n, sy = (y1 - y0) / (float) n;
            int w = _src.Width, h = _src.Height;
            uint[] px = _src.Px;
            float u = x0, v = y0;
            long x0f = (long) MathF.Floor (x0 * 65536f + 0.5f), y0f = (long) MathF.Floor (y0 * 65536f + 0.5f);
            long sxf = (long) MathF.Floor (sx * 65536f + 0.5f), syf = (long) MathF.Floor (sy * 65536f + 0.5f);
            if (Fix) { u = (float) (x0f / 65536.0); v = (float) (y0f / 65536.0); }
            for (int i = 0; i < n; i++) {
                int ix = (int) MathF.Floor (u), iy = (int) MathF.Floor (v);
                int fx = (int) MathF.Floor ((u - (float) ix) * 2048f + 0.5f);
                int fyw = (int) MathF.Floor ((v - (float) iy) * 2048f + 0.5f);
                int ix1 = ix + 1, iy1 = iy + 1;
                if ((uint) (w - 1) <= (uint) ix || (uint) (h - 1) <= (uint) iy) {
                    GpWrap.Apply (_wrap, ref ix, ref iy, w, h);
                    GpWrap.Apply (_wrap, ref ix1, ref iy1, w, h);
                }
                int row0 = iy < 0 || iy >= h ? -1 : iy * w;
                int row1 = iy1 < 0 || iy1 >= h ? -1 : iy1 * w;
                uint p00, p01, p10, p11;
                if (ix < 0 || ix >= w) p00 = p01 = _clamp;
                else {
                    p00 = row0 < 0 ? _clamp : px [row0 + ix];
                    p01 = row1 < 0 ? _clamp : px [row1 + ix];
                }
                if (ix1 < 0 || ix1 >= w) p10 = p11 = _clamp;
                else {
                    p10 = row0 < 0 ? _clamp : px [row0 + ix1];
                    p11 = row1 < 0 ? _clamp : px [row1 + ix1];
                }
                if (ix1 < 0 || w <= ix || iy1 < 0 || h <= iy) buf [i] = _clamp;
                else {
                    int gy = 2048 - fyw;
                    uint r = 0;
                    for (int sh = 0; sh < 32; sh += 8) {
                        int a00 = (int) (p00 >> sh & 0xff), a10 = (int) (p10 >> sh & 0xff), a01 = (int) (p01 >> sh & 0xff), a11 = (int) (p11 >> sh & 0xff);
                        int t = ((a10 - a00) * fx + a00 * 0x800) * gy + ((a11 - a01) * fx + a01 * 0x800) * fyw + 0x200000;
                        r |= ((uint) t >> 22 & 0xff) << sh;
                    }
                    buf [i] = r;
                }
                if (GpGraphics.Dbg && y == DbgY && left + i == DbgX) Console.Error.WriteLine ($"  bil ({left + i},{y}) u={u:R} v={v:R} ix={ix} iy={iy} fx={fx} fy={fyw} p={p00:x8} {p10:x8} {p01:x8} {p11:x8} -> {buf [i]:x8}");
                if (Fix) { u = (float) ((x0f + (long) (i + 1) * sxf) / 65536.0); v = (float) ((y0f + (long) (i + 1) * syf) / 65536.0); }
                else { u += sx; v += sy; }
            }
        }
    }

    /// <summary>DpOutputBilinearSpan_Identity: an integer translation, copied.</summary>
    internal sealed class GpIdentitySpan : GpSpan
    {
        readonly DpBitmapSrc _src;
        readonly int _wrap, _offX, _offY;
        readonly uint _clamp;
        readonly bool _pow2;

        public GpIdentitySpan (GpScan scan, DpBitmapSrc src, in GpMatrix srcToDevice, DpImageAttr ia) : base (scan)
        {
            _src = src; _wrap = ia.Wrap; _clamp = ia.Clamp;
            uint w = (uint) src.Width, h = (uint) src.Height;
            _pow2 = ((w - 1) & w) == 0 && ((h - 1) & h) == 0;
            _offX = -(int) MathF.Floor (srcToDevice.Dx + 0.5f);
            _offY = -(int) MathF.Floor (srcToDevice.Dy + 0.5f);
        }

        static int Mod (int v, int n) => v < 0 ? n - (~v - ~v / n * n) - 1 : v - v / n * n;

        protected override void Fill (uint[] buf, int y, int left, int n)
        {
            int sx = _offX + left, sy = _offY + y;
            int w = _src.Width, h = _src.Height;
            uint[] px = _src.Px;
            int o = 0;
            if (_wrap == 0) {
                if (_pow2) { sx &= w - 1; sy &= h - 1; }
                else {
                    if ((uint) w <= (uint) sx) sx = Mod (sx, w);
                    if ((uint) h <= (uint) sy) sy = Mod (sy, h);
                }
                int row = sy * w;
                int k = Math.Min (w - sx, n);
                int rest = n - k;
                for (int i = 0; i < k; i++) buf [o++] = px [row + sx + i];
                while (rest > 0) {
                    int c = Math.Min (w, rest);
                    rest -= c;
                    for (int i = 0; i < c; i++) buf [o++] = px [row + i];
                }
                return;
            }
            if ((uint) sy < (uint) h && sx < w && sx + n > 0) {
                int row = sy * w, from = sx, cnt = n;
                if (sx < 0) {
                    for (int i = 0; i < -sx; i++) buf [o++] = _clamp;
                    cnt += sx; from = 0;
                }
                int avail = w - from;
                int c = Math.Min (cnt, avail);
                for (int i = 0; i < c; i++) buf [o++] = px [row + from + i];
                for (int i = c; i < cnt; i++) buf [o++] = _clamp;
            } else {
                for (int i = 0; i < n; i++) buf [i] = _clamp;
            }
        }
    }
}
