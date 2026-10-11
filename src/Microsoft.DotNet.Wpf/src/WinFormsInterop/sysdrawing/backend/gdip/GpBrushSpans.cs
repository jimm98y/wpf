// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Hatch and linear-gradient brush spans (gdiplus.dll 10.0.26100, arm64, public PDB):
//
//   DpOutputHatchSpan::DpOutputHatchSpan @1800bffb8   fore and back premultiplied; the third colour
//        (pattern byte 0x80) is (3 back + fore) / 4 per channel; ForwardDiagonal, BackwardDiagonal
//        and DiagonalCross move the fore colour towards the back by (fore - back) * 0.9142135 + back
//        (their lines are drawn thinner); the 8x8 pattern from GpHatch::InitializeBrush
//   DpOutputHatchSpan::OutputSpan @1800c0460          pattern[(x - originX) & 7, (y - originY) & 7]
//        with the rendering origin of the context
//
//   DpOutputLinearGradientSpan::DpOutputLinearGradientSpan @180027e98   the brush transform times the
//        world-to-device; a colour table of 32 entries (128 when w + h > 128, 512 when > 512, for a
//        blend or preset of more than 3 entries) over (0,0)-(F,F), F = size << 16 / 2, mapped onto the
//        brush rectangle; the table's 16.16 steps rounded floor(v + 0.5) (frintm, fcvtzs) -- the
//        Feature_GdiPlusOptimizations_Misc branch is off -- and the origin RoundSat; the end colours
//        premultiplied in float, through the sRGB-to-linear table when the brush is gamma corrected
//        and back through GammaUnlinearizePremultiplied128 @180029010
//   DpOutputLinearGradientSpan::OutputSpan @180029170 16.16 walk, an 8-bit fraction between two
//        packed 16-bit-lane entries
//

using System.Drawing.Drawing2D;

namespace System.Drawing.WebGpuBackend.Gdip
{
    internal sealed class HatchSpan : GpSpan
    {
        readonly uint _fore, _back, _mid;
        readonly int _ox, _oy;
        readonly byte[] _pattern = new byte [64];

        public HatchSpan (GpScan scan, HatchBrush brush, Point origin) : base (scan)
        {
            int style = (int) brush.HatchStyle;
            uint f = SolidSpan.Premultiply ((uint) brush.ForegroundColor.ToArgb ());
            uint b = SolidSpan.Premultiply ((uint) brush.BackgroundColor.ToArgb ());
            _back = b;
            _mid = Mix (b, f, 0) | Mix (b, f, 8) << 8 | Mix (b, f, 16) << 16 | Mix (b, f, 24) << 24;
            if (style == 2 || style == 3 || style == 5) {
                const float K = 0.9142135381698608f;
                uint fa = f >> 24, ba = b >> 24;
                uint a = fa;
                if (fa != 0xff || ba != 0xff) a = (uint) ((float) ((int) fa - (int) ba) * K + (float) ba);
                f = (a & 0xff) << 24 | Lerp (f, b, 16, K) << 16 | Lerp (f, b, 8, K) << 8 | Lerp (f, b, 0, K);
            }
            _fore = f;
            if ((uint) style < 0x35) Array.Copy (GpTables.Hatch, style * 64, _pattern, 0, 64);
            _ox = origin.X; _oy = origin.Y;
        }

        static uint Mix (uint back, uint fore, int shift)
        {
            uint bb = back >> shift & 0xff, ff = fore >> shift & 0xff;
            return (bb * 2 + bb + ff) >> 2 & 0xff;
        }

        static uint Lerp (uint fore, uint back, int shift, float k)
        {
            float v = (float) ((int) (fore >> shift & 0xff) - (int) (back >> shift & 0xff)) * k + (float) (back >> shift & 0xff);
            return v <= 255f ? (uint) v & 0xff : 0xff;
        }

        protected override void Fill (uint[] buf, int y, int x, int n)
        {
            int row = ((y - _oy) & 7) << 3;
            for (int i = 0; i < n; i++) {
                byte p = _pattern [((x + i - _ox) & 7) | row];
                buf [i] = p == 0xff ? _fore : p == 0 ? _back : _mid;
            }
        }
    }

    internal sealed class LineGradientSpan : GpSpan
    {
        readonly int _dx, _dy, _origin, _mask;
        readonly int _ox, _oy;     // the surface's pixel (0, 0) on the device the walk runs in
        readonly ulong[] _start, _end;

        public bool Valid { get; }

        public LineGradientSpan (GpScan scan, LinearGradientBrush lg, in GpMatrix worldToDevice, int originX = 0, int originY = 0) : base (scan)
        {
            _ox = originX; _oy = originY;
            GpMatrix m = GpMatrix.Multiply (lg.Xform, worldToDevice);
            RectangleF rect = lg.Rectangle;
            bool preset = lg.PresetSet;
            int count = lg.BlendCount;
            bool adjust = count != 1 || lg.BlendFactor0 != 1f;
            uint size = 0x20;
            if ((preset || adjust) && count > 3) {
                float wh = rect.Height + rect.Width;
                if (wh > 512f) size = 0x200;
                else if (wh > 128f) size = 0x80;
            }
            _mask = (int) (size - 1);
            uint half = size >> 1;
            float F = (float) (size << 16) * 0.5f + 0.0f;
            float right = rect.X + rect.Width, bottom = rect.Y + rect.Height;
            float sx = (right - rect.X) / (F - 0.0f);
            float sy = (bottom - rect.Y) / (F - 0.0f);
            float tx = right - F * sx, ty = bottom - F * sy;
            var e = new GpMatrix (
                m.M11 * sx + m.M21 * 0f, m.M12 * sx + m.M22 * 0f,
                m.M21 * sy + m.M11 * 0f, m.M22 * sy + m.M12 * 0f,
                (m.M11 * tx + m.M21 * ty) + m.Dx, (m.M12 * tx + m.M22 * ty) + m.Dy);
            if (!e.Invert ()) { Valid = false; _start = _end = new ulong [1]; return; }
            _dx = Round (e.M11);
            _dy = Round (e.M21);
            _origin = RoundSat (e.Dx);

            bool gamma = lg.GammaCorrection;
            Premul ((uint) lg.gradient_color1.ToArgb (), gamma, out float a1, out float r1, out float g1, out float b1);
            Premul ((uint) lg.gradient_color2.ToArgb (), gamma, out float a2, out float r2, out float g2, out float b2);
            float[] factors = lg.BlendFactors, positions = lg.BlendPositions;
            int[] presetArgb = lg.PresetArgb;
            var tab = new ulong [half + 1];
            float t = 0f, step = 1f / (float) half;
            for (uint i = 0; i <= half; i++) {
                float A, R, G, B;
                if (!preset) {
                    float s = adjust ? SlowAdjust (t, count, lg.BlendFactor0, factors, positions) : t;
                    float u = 1f - s;
                    A = a1 * u + a2 * s;
                    R = r1 * u + r2 * s;
                    G = g2 * s + g1 * u;
                    B = b2 * s + b1 * u;
                } else
                    InterpolatePreset (t, count, presetArgb, positions, gamma, out A, out R, out G, out B);
                ulong lo, hi;
                if (!gamma) {
                    lo = (uint) Round (B) | ((uint) Round (R) << 16);
                    hi = (uint) Round (G) | ((uint) Round (A) << 16);
                } else {
                    uint c = Unlinearize (A, R, G, B);
                    lo = (c & 0xff) | ((c >> 16 & 0xff) << 16);
                    hi = (c >> 8 & 0xff) | ((c >> 24) << 16);
                }
                tab [i] = lo | (hi << 32);
                t = step + t;
            }
            _start = new ulong [size];
            _end = new ulong [size];
            for (uint i = 0; i < half; i++) { _start [i] = tab [i]; _end [i] = tab [i + 1]; }
            int w = (int) lg.WrapMode;
            if (((w - 1) & ~2) == 0) {
                for (uint j = 0; j < half; j++) {
                    _start [half + j] = _end [half - j - 1];
                    _end [half + j] = _start [half - j - 1];
                }
            } else {
                Array.Copy (_start, 0, _start, half, half);
                Array.Copy (_end, 0, _end, half, half);
            }
            Valid = true;
        }

        // floor(v + 0.5): frintm then fcvtzs, the non-optimised branch.
        static int Round (float v) => (int) MathF.Floor (v + 0.5f);   // frintm then fcvtzs

        static int RoundSat (float v)
        {
            if (v < -2147483648f) return int.MinValue;
            if (v >= 2147483648f) return int.MaxValue;
            return (int) MathF.Floor (v + 0.5f);
        }

        const float Eps = 1.1920928955078125e-07f;

        // GammaLinearizeAndPremultiply @180028f70.
        internal static void Premul (uint argb, bool gamma, out float a, out float r, out float g, out float b)
        {
            a = (float) (argb >> 24);
            if (!(MathF.Abs (a) >= Eps)) { r = g = b = 0f; return; }
            int ri = (int) (argb >> 16 & 0xff), gi = (int) (argb >> 8 & 0xff), bi = (int) (argb & 0xff);
            if (!gamma) { r = ri; g = gi; b = bi; }
            else { r = GpTables.Linear (ri); g = GpTables.Linear (gi); b = GpTables.Linear (bi); }
            if (MathF.Abs (a - 255f) >= Eps) {
                float k = a / 255f;
                r *= k; g *= k; b *= k;
            }
        }

        // GammaUnlinearizePremultiplied128 @180029010.
        internal static uint Unlinearize (float A, float R, float G, float B)
        {
            int a = (int) (long) (A + 0.5f);
            if (a > 255) a = 255;
            if (a < 0) a = 0;
            int ri = 0, gi = 0, bi = 0;
            if (a != 0) {
                float k = a == 255 ? 4.0117645263671875f : 1023f / A;
                ri = (int) (long) (R * k + 0.5f);
                gi = (int) (long) (G * k + 0.5f);
                bi = (int) (long) (B * k + 0.5f);
                if (bi > 1023) bi = 1023;
            }
            bi = Math.Clamp (bi, 0, 1023); gi = Math.Clamp (gi, 0, 1023); ri = Math.Clamp (ri, 0, 1023);
            byte[] u = GpTables.Unlinear;
            uint c = (uint) a << 24 | (uint) u [ri] << 16 | (uint) u [gi] << 8 | u [bi];
            return SolidSpan.Premultiply (c);
        }

        // slowAdjustValue @1802086e8.
        internal static float SlowAdjust (float t, int count, float factor0, float[] factors, float[] positions)
        {
            if (count == 1) {
                if (factor0 != 1f && factor0 > 0f && t >= 0f && t <= 1f)
                    t = (float) Math.Pow (t, factor0);
                return t;
            }
            if (count < 2 || factors == null || positions == null || t < 0f || t > 1f) return t;
            int i = 1;
            if (t - positions [1] > Eps) {
                do {
                    if (i >= count) return t;
                    i++;
                } while (i < count && t - positions [i] > Eps);
                if (i >= count) return t;
            }
            float span = positions [i] - positions [i - 1];
            if (span <= 0f) return (factors [i] + factors [i - 1]) * 0.5f;
            return ((t - positions [i - 1]) / span) * (factors [i] - factors [i - 1]) + factors [i - 1];
        }

        // interpolatePresetColors @180029ab8.
        internal static void InterpolatePreset (float t, int count, int[] colors, float[] pos, bool gamma,
                                               out float A, out float R, out float G, out float B)
        {
            A = R = G = B = 0f;
            if (count <= 1 || colors == null || pos == null) return;
            if (t < 0f || t > 1f) {
                Premul ((uint) (t > 0f ? colors [count - 1] : colors [0]), gamma, out A, out R, out G, out B);
                return;
            }
            int i = 1;
            while (i < count && !(t <= pos [i])) i++;
            if (i >= count) { Premul ((uint) colors [count - 1], gamma, out A, out R, out G, out B); return; }
            Premul ((uint) colors [i - 1], gamma, out float a0, out float r0, out float g0, out float b0);
            Premul ((uint) colors [i], gamma, out float a1, out float r1, out float g1, out float b1);
            float d = pos [i] - pos [i - 1];
            if (d <= 0f) {
                B = (b1 + b0) * 0.5f; G = (g1 + g0) * 0.5f; R = (r1 + r0) * 0.5f; A = (a1 + a0) * 0.5f;
            } else {
                float f = (t - pos [i - 1]) / d;
                B = (b1 - b0) * f + b0; G = (g1 - g0) * f + g0; R = (r1 - r0) * f + r0; A = (a1 - a0) * f + a0;
            }
        }

        protected override void Fill (uint[] buf, int y, int x, int n)
        {
            int u0 = unchecked (_dy * (y + _oy) + _dx * (x + _ox) + _origin);
            for (int i = 0; i < n; i++) {
                int u = unchecked (u0 + _dx * i);
                int idx = (u >> 16) & _mask;
                ulong f = (uint) (u >> 8) & 0xff;
                ulong q = (_end [idx] * f + 0x0080008000800080UL + _start [idx] * (0x100 - f)) & 0xff00ff00ff00ff00UL;
                buf [i] = (uint) (q >> 32) | ((uint) q >> 8);
            }
        }
    }
}
