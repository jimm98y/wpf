// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
#nullable enable
// Bit-exact reproduction of GDI+ (gdiplus.dll, Windows 11 arm64X build in re/gp) filling with a
// LinearGradientBrush. Every step below is transcribed from the arm64 disassembly of the named
// function (no fitted constants). Addresses are Ghidra addresses in re/gp/gdiplus.dll.
//
//  GpLineGradient ctor (rect, c1, c2, LinearGradientMode)   ??0GpLineGradient..LinearGradientMode  1801a9da0
//    -> SetLineGradient                                      GpLineGradient::SetLineGradient        18006ffc0
//    -> CalcLinearGradientXform (brush transform)            CalcLinearGradientXform                18000b068
//  GpRectGradient::CreateOutputSpan picks the span           GpRectGradient::CreateOutputSpan       18000b460
//  DpOutputLinearGradientSpan ctor (matrix + colour table)   DpOutputLinearGradientSpan::ctor       180027e98
//    GpMatrix::Invert                                        1800348a8,  GpMatrix::InferAffineMatrix 1800346a8
//    slowAdjustValue (Blend)                                 1802086e8
//    interpolatePresetColors (InterpolationColors)           180029ab8
//    GammaLinearizeAndPremultiply                            180028f70
//  DpOutputLinearGradientSpan::OutputSpan (per pixel)        180029170
//
// Scope: world transform = identity (Graphics.FromImage default), GammaCorrection = false,
// brush Transform = identity. The value produced is the PREMULTIPLIED ARGB the span writes into
// GDI+'s scan buffer; for opaque colours that is the final pixel under SourceOver.
using System;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace System.Drawing.WebGpuBackend
{
public static class GdipLinearGradient
{
    /// <summary>Single-pixel convenience wrapper. Builds the span each call (slow; use <see cref="Span"/> for many pixels).</summary>
    public static uint PixelArgb(RectangleF brushRect, Color c1, Color c2, LinearGradientMode mode, int x, int y,
                                 WrapMode wrap = WrapMode.Tile,
                                 float[]? blendFactors = null, float[]? blendPositions = null,
                                 Color[]? presetColors = null, float[]? presetPositions = null)
        => new Span(brushRect, c1, c2, mode, wrap, blendFactors, blendPositions, presetColors, presetPositions).Pixel(x, y);

    public sealed class Span
    {
        // DpOutputLinearGradientSpan fields: +0x110 dx (per x), +0x114 dy (per y), +0x118 origin, +0x120 mask.
        readonly int _dx, _dy, _origin, _mask;
        // +0x128 start table, +0x2128 end table; each entry packs 16-bit lanes (B | R<<16) | (G | A<<16)<<32.
        readonly ulong[] _start, _end;

        /// <summary>LinearGradientBrush(rect, c1, c2, LinearGradientMode).</summary>
        public Span(RectangleF rect, Color c1, Color c2, LinearGradientMode mode, WrapMode wrap = WrapMode.Tile,
                    float[]? blendFactors = null, float[]? blendPositions = null,
                    Color[]? presetColors = null, float[]? presetPositions = null)
            : this(rect, c1, c2, ModeAngle(mode), true, wrap, blendFactors, blendPositions, presetColors, presetPositions) { }

        /// <summary>LinearGradientBrush(rect, c1, c2, angle, isAngleScalable).</summary>
        public Span(RectangleF rect, Color c1, Color c2, float angle, bool isAngleScalable, WrapMode wrap = WrapMode.Tile,
                    float[]? blendFactors = null, float[]? blendPositions = null,
                    Color[]? presetColors = null, float[]? presetPositions = null)
        {
            // ---- brush transform: CalcLinearGradientXform (18000b068) ----
            Mat b = CalcLinearGradientXform(angle, isAngleScalable, rect);

            // ---- span ctor (180027e98) ----
            // Brush state as SetLineGradient/SetBlend/SetPresetBlend leave it:
            //   +0x78 preset flag, +0xd0 count, +0xc4 single blend factor, +0xe0 factors, +0xf8 positions.
            bool preset = presetColors != null;
            int count; float factor0 = 1f; float[]? factors = null, positions = null; uint[]? presetArgb = null;
            if (preset)
            {
                count = presetColors!.Length;
                presetArgb = new uint[count];
                for (int i = 0; i < count; i++) presetArgb[i] = (uint)presetColors[i].ToArgb();
                positions = presetPositions;
            }
            else if (blendFactors != null)
            {
                count = blendFactors.Length;
                if (count == 1) factor0 = blendFactors[0];      // SetBlend count==1 stores factor in +0xc4
                else { factors = blendFactors; positions = blendPositions; }
            }
            else count = 1;
            bool adjust = count != 1 || factor0 != 1f;

            // Table size: 32 unless (preset or blend) with count > 3; then 128 if w+h > 128, 512 if w+h > 512.
            uint size = 0x20;
            if ((preset || adjust) && count > 3)
            {
                float wh = rect.Height + rect.Width;
                if (wh > 512f) size = 0x200;
                else if (wh > 128f) size = 0x80;
            }
            _mask = (int)(size - 1);
            uint half = size >> 1;

            // Matrix E maps (0,0,F,F) onto the brush rect, F = half * 65536 (InferAffineMatrix inlined).
            float F = (float)(size << 16) * 0.5f + 0.0f;
            float right = rect.X + rect.Width, bottom = rect.Y + rect.Height;
            float sx = (right - rect.X) / (F - 0.0f);
            float sy = (bottom - rect.Y) / (F - 0.0f);
            float tx = right - F * sx;
            float ty = bottom - F * sy;
            // M = E * B (B = brush xform * world-to-device; world = identity so B == brush xform exactly).
            var m = new Mat
            {
                M11 = b.M11 * sx + b.M21 * 0f,
                M12 = b.M12 * sx + b.M22 * 0f,
                M21 = b.M21 * sy + b.M11 * 0f,
                M22 = b.M22 * sy + b.M12 * 0f,
                Dx = (b.M11 * tx + b.M21 * ty) + b.Dx,
                Dy = (b.M12 * tx + b.M22 * ty) + b.Dy,
            };
            if (!Invert(ref m)) throw new InvalidOperationException("GDI+ would leave the span invalid (non-invertible).");
            _dx = GpRound(m.M11);             // +0x110 (+0x11c is the same value)
            _dy = GpRound(m.M21);             // +0x114
            _origin = RoundSat(m.Dx);         // +0x118

            // End colours, premultiplied in float (inline copy of GammaLinearizeAndPremultiply, gamma off).
            Premul((uint)c1.ToArgb(), out float a1, out float r1, out float g1, out float b1);
            Premul((uint)c2.ToArgb(), out float a2, out float r2, out float g2, out float b2);

            var e = new ulong[half + 1];
            float t = 0f;
            float step = 1f / (float)half;
            for (uint i = 0; i <= half; i++)
            {
                float A, R, G, B;
                if (!preset)
                {
                    float s = adjust ? SlowAdjustValue(t, count, factor0, factors, positions) : t;
                    float u = 1f - s;
                    A = a1 * u + a2 * s;
                    R = r1 * u + r2 * s;
                    G = g2 * s + g1 * u;
                    B = b2 * s + b1 * u;
                }
                else
                    InterpolatePresetColors(t, count, presetArgb!, positions!, out A, out R, out G, out B);
                ulong lo = (uint)GpRound(B) | ((uint)GpRound(R) << 16);
                ulong hi = (uint)GpRound(G) | ((uint)GpRound(A) << 16);
                e[i] = lo | (hi << 32);
                t = step + t;
            }
            _start = new ulong[size];
            _end = new ulong[size];
            for (uint i = 0; i < half; i++) { _start[i] = e[i]; _end[i] = e[i + 1]; }
            int w = (int)wrap;
            if (((w - 1) & ~2) == 0)          // TileFlipX (1) / TileFlipXY (3): mirrored second half
            {
                for (uint j = 0; j < half; j++)
                {
                    _start[half + j] = _end[half - j - 1];
                    _end[half + j] = _start[half - j - 1];
                }
            }
            else                               // Tile, TileFlipY, Clamp: repeat
            {
                Array.Copy(_start, 0, _start, half, half);
                Array.Copy(_end, 0, _end, half, half);
            }
        }

        /// <summary>DpOutputLinearGradientSpan::OutputSpan (180029170): premultiplied ARGB of device pixel (x, y).</summary>
        /// <summary>The per-x and per-y steps through the table (16.16): zero when the gradient does not vary that way.</summary>
        public int StepX => _dx;
        public int StepY => _dy;

        public uint Pixel(int x, int y)
        {
            int u = unchecked(_dy * y + _dx * x + _origin);            // 16.16 table coordinate
            int idx = (u >> 16) & _mask;
            ulong f = (uint)(u >> 8) & 0xff;
            ulong q = (_end[idx] * f + 0x0080008000800080UL + _start[idx] * (0x100 - f)) & 0xff00ff00ff00ff00UL;
            return (uint)(q >> 32) | ((uint)q >> 8);
        }
    }

    // ------------------------------------------------------------------------------------------

    struct Mat { public float M11, M12, M21, M22, Dx, Dy; }

    static float ModeAngle(LinearGradientMode mode) => mode switch
    {   // constants at 1801a9ee4/8/c (ctor 1801a9da0)
        LinearGradientMode.Horizontal => 0f,
        LinearGradientMode.Vertical => 90f,
        LinearGradientMode.ForwardDiagonal => 45f,
        LinearGradientMode.BackwardDiagonal => 135f,
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    // GpRound as the span ctor inlines it: frintm(x + 0.5f) then fcvtzs.
    static int GpRound(float v) => (int)MathF.Floor(v + 0.5f);

    // FPUStateSaver::RoundSat (inlined at 18002836c).
    static int RoundSat(float v)
    {
        if (v < -2147483648f) return int.MinValue;
        if (v >= 2147483648f) return int.MaxValue;
        return (int)MathF.Floor(v + 0.5f);
    }

    const float Eps = 1.1920928955078125e-07f; // FLT_EPSILON, 180028788 / 180029dd8 / 1802087f0

    // GammaLinearizeAndPremultiply (180028f70), gamma off.
    static void Premul(uint argb, out float a, out float r, out float g, out float b)
    {
        a = (float)(argb >> 24);
        if (MathF.Abs(a) < Eps) { r = g = b = 0f; return; }
        r = (float)((argb >> 16) & 0xff); g = (float)((argb >> 8) & 0xff); b = (float)(argb & 0xff);
        if (MathF.Abs(a - 255f) >= Eps)
        {
            float k = a / 255f;
            r *= k; g *= k; b *= k;
        }
    }

    // slowAdjustValue (1802086e8)
    static float SlowAdjustValue(float t, int count, float factor0, float[]? factors, float[]? positions)
    {
        if (count == 1)
        {
            if (factor0 != 1f && factor0 > 0f && t >= 0f && t <= 1f)
                t = (float)Math.Pow(t, factor0);
            return t;
        }
        if (count < 2 || factors == null || positions == null || t < 0f || t > 1f) return t;
        int i = 1;
        if (t - positions[1] > Eps)
        {
            do
            {
                if (i >= count) return t;
                i++;
            } while (t - positions[i] > Eps);   // (positions[count] would be read only if count reached; guarded above)
            if (i >= count) return t;
        }
        float span = positions[i] - positions[i - 1];
        if (span <= 0f) return (factors[i] + factors[i - 1]) * 0.5f;
        return ((t - positions[i - 1]) / span) * (factors[i] - factors[i - 1]) + factors[i - 1];
    }

    // interpolatePresetColors (180029ab8), gamma off.
    static void InterpolatePresetColors(float t, int count, uint[] colors, float[] pos,
                                        out float A, out float R, out float G, out float B)
    {
        A = R = G = B = 0f;
        if (count <= 1) return;
        if (t < 0f || t > 1f)
        {
            Premul(t > 0f ? colors[count - 1] : colors[0], out A, out R, out G, out B);
            return;
        }
        int i = 1;
        while (i < count && !(t <= pos[i])) i++;
        if (i >= count) { Premul(colors[count - 1], out A, out R, out G, out B); return; }
        Premul(colors[i - 1], out float a0, out float r0, out float g0, out float b0);
        Premul(colors[i], out float a1, out float r1, out float g1, out float b1);
        float d = pos[i] - pos[i - 1];
        if (d <= 0f)
        {
            B = (b1 + b0) * 0.5f; G = (g1 + g0) * 0.5f; R = (r1 + r0) * 0.5f; A = (a1 + a0) * 0.5f;
        }
        else
        {
            float f = (t - pos[i - 1]) / d;
            B = (b1 - b0) * f + b0; G = (g1 - g0) * f + g0; R = (r1 - r0) * f + r0; A = (a1 - a0) * f + a0;
        }
    }

    /// <summary>CalcLinearGradientXform as the brush stores it (the matrix GetLineTransform returns);
    /// false when the rectangle is degenerate (GDI+ leaves the brush invalid).</summary>
    internal static bool TryLineXform(float angle, bool scalable, RectangleF r, out Gdip.GpMatrix xf)
    {
        xf = Gdip.GpMatrix.CreateIdentity();
        float rx = r.X, ry = r.Y, rw = r.Width, rh = r.Height;
        float det = ((ry + rh) * (rw + rx) - ry * rx) + (rx * -rh - ry * rw);
        if (MathF.Abs(det) < Eps) return false;
        Mat m = CalcLinearGradientXform(angle, scalable, r);
        xf = new Gdip.GpMatrix(m.M11, m.M12, m.M21, m.M22, m.Dx, m.Dy);
        return true;
    }

    // GpMatrix::Invert (1800348a8)
    static bool Invert(ref Mat m)
    {
        float det = m.M11 * m.M22 - m.M12 * m.M21;
        float den = det == 0f ? 1f : det;
        if (!(MathF.Abs((0f - det) / den) >= 1.1920928955078125e-06f)) return false;
        float inv = 1f / det;
        float n11 = m.M22 * inv, n12 = -m.M12 * inv, n21 = -m.M21 * inv, n22 = m.M11 * inv;
        float ndx = (m.Dy * m.M21 - m.Dx * m.M22) * inv;
        float ndy = (m.Dx * m.M12 - m.Dy * m.M11) * inv;
        if (!float.IsFinite(n11) || !float.IsFinite(n12) || !float.IsFinite(n21) || !float.IsFinite(n22)
            || !float.IsFinite(ndx) || !float.IsFinite(ndy)) return false;
        m = new Mat { M11 = n11, M12 = n12, M21 = n21, M22 = n22, Dx = ndx, Dy = ndy };
        return true;
    }

    // CalcLinearGradientXform (18000b068) incl. the inlined InferAffineMatrix(PointF[3], RectF).
    static Mat CalcLinearGradientXform(float angle, bool scalable, RectangleF r)
    {
        float a;
        if (angle > 0f) a = angle - (float)(int)(angle / 360f) * 360f;
        else if (angle < 0f)
        {
            float n = -angle;
            a = n - (float)(int)(n / 360f) * 360f;
            if (a > 0f) a = 360f - a;
        }
        else a = 0f;

        int quad;
        if (a < 90f) quad = 0;
        else if (a < 180f) { quad = 1; a = 180f - a; }
        else if (a < 270f) { quad = 2; a = a - 180f; }
        else { quad = 3; a = 360f - a; }

        double rad = (double)a * 3.141592653589793 / 180.0;
        double sin = Math.Sin(rad), cos = Math.Cos(rad);
        double x, y, w, h;
        if (!scalable) { x = r.X; y = r.Y; w = r.Width; h = r.Height; }
        else { x = 0; y = 0; w = 1; h = 1; }

        double d10, d3, d9, d11, d7;
        switch (quad)
        {
            case 0: d10 = w * sin; d3 = sin; d11 = y; d9 = x; d7 = cos; break;
            case 1: d10 = h * cos; d7 = -cos; d3 = sin; d9 = w + x; d11 = y; break;
            case 2: d10 = w * sin; d7 = -cos; d3 = -sin; d9 = w + x; d11 = h + y; break;
            default: d10 = h * cos; d3 = -sin; d11 = h + y; d9 = x; d7 = cos; break;
        }
        double d4 = h * cos + w * sin;
        double d5 = h * sin + w * cos;
        float p0x = (float)(d3 * d10 + d9);
        float p0y = (float)(d11 - d7 * d10);
        float s13 = (float)(d7 * d5);
        float s11 = (float)(d7 * d4);
        float s12 = (float)(d3 * d5);
        float s10 = (float)(-d4 * d3);
        if (scalable)
        {
            float W = r.Width, H = r.Height;
            p0x = W * p0x + r.X;
            s13 = W * s13; s12 = H * s12; s10 = W * s10; s11 = H * s11;
            p0y = H * p0y + r.Y;
        }
        float p1x = s13 + p0x, p1y = s12 + p0y;   // image of the rect's top-right
        float p2x = s10 + p0x, p2y = s11 + p0y;   // image of the rect's bottom-left

        // inlined GpMatrix::InferAffineMatrix(points, rect)
        float rx = r.X, ry = r.Y, rw = r.Width, rh = r.Height;
        float nh = -rh;
        float det = ((ry + rh) * (rw + rx) - ry * rx) + (rx * nh - ry * rw);
        if (MathF.Abs(det) < Eps) throw new InvalidOperationException("degenerate brush rect");
        float inv = 1f / det;
        var m = new Mat();
        m.M11 = (p0x * nh + rh * p1x) * inv;
        m.M12 = (p0y * nh + rh * p1y) * inv;
        float nw = -rw;
        m.M21 = (p0x * nw + rw * p2x) * inv;
        m.M22 = (p0y * nw + rw * p2y) * inv;
        float k = (rx + rw) * (ry + rh) - rx * ry;
        float kx = -rx * rh;
        float ky = -ry * rw;
        m.Dx = ((p0x * k + kx * p1x) + ky * p2x) * inv;
        m.Dy = ((p0y * k + kx * p1y) + ky * p2y) * inv;
        return m;
    }
}
}
