// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// WPF's ideal-mode text, drawn the way stock WPF draws it on its hardware path.
//
// The texture is DirectWrite's (Text.NaturalClearType). What wpfgfx does with it is read from its own
// source, which is in this repository:
//
//   CGlyphRunRealization::RealizeAlphaBoundsAndTextures   the texture goes through the ENHANCED
//       CONTRAST table (EnhancedContrastTable::ReInit, k from the monitor's rendering params):
//       a' = round(255 * a(k+1) / (a k + 1)).
//   CD3DGlyphRunPainter / CVertM1_CT + hlslTextShaders20A CTSB   one quad per run at the run's
//       FRACTIONAL origin, the texture held in sub-pixel texels (three per pixel) and sampled
//       BILINEARLY: green at the pixel's own position, red one texel left, blue one right (the blue
//       sub-pixel offset is ClearTypeLevel/3 of a pixel, i.e. one texel). Then per channel
//       a = sample * brushAlpha,  a' = a + a(1-a)((g1 f + g2) a + (g3 f + g4))
//       with f the brush channel and g1..g4 CGammaHandler::sc_gammaRatios[gammaIndex], and
//   CD3DRenderState::SetRenderState_Text_ClearType_SolidBrush   out = brush a' + dst (1 - a'), in the
//       target's own eight-bit values.
//   CBaseGlyphRunPainter / the text line's PushGuidelineY1   the baseline is on a whole pixel.
//
// The blend is made with the two-draw subpixel composite the rest of the text uses, so a mask value
// per channel carries a'; where the paper under the run is one known colour the value is chosen so
// the two draws land on exactly the byte wpfgfx's blend gives.
//

using System;
using System.Collections.Generic;
using System.Numerics;
using static Microsoft.Wpf.Interop.WebGpu.Wgpu;

namespace Microsoft.Wpf.Interop.WebGpu.Composition
{
    internal sealed unsafe partial class WgpuSceneRenderer
    {
        /// <summary>CGammaHandler::sc_gammaRatios (Gamma.cpp) with the shader's times-four undone:
        /// g1..g4 for gamma 1.0, 1.1, ... 2.2.</summary>
        private static readonly float[,] s_wpfGammaRatios =
        {
            { 0f, 0f, 0f, 0f },                     { 0.0166f, -0.0807f, 0.2227f, -0.0751f },
            { 0.0350f, -0.1760f, 0.4325f, -0.1370f }, { 0.0543f, -0.2821f, 0.6302f, -0.1876f },
            { 0.0739f, -0.3963f, 0.8167f, -0.2287f }, { 0.0933f, -0.5161f, 0.9926f, -0.2616f },
            { 0.1121f, -0.6395f, 1.1588f, -0.2877f }, { 0.1300f, -0.7649f, 1.3159f, -0.3080f },
            { 0.1469f, -0.8911f, 1.4644f, -0.3234f }, { 0.1627f, -1.0170f, 1.6051f, -0.3347f },
            { 0.1773f, -1.1420f, 1.7385f, -0.3426f }, { 0.1908f, -1.2652f, 1.8650f, -0.3476f },
            { 0.2031f, -1.3864f, 1.9851f, -0.3501f },
        };

        /// <summary>The monitor's DirectWrite rendering parameters as GetAlphaBlendParams reports
        /// them. Windows' defaults -- gamma 1.8, enhanced contrast 0.5, ClearType level 1 -- are what
        /// every run on the reference machine reported.</summary>
        private const float WpfTextGamma = 1.8f, WpfTextContrast = 0.5f;

        /// <summary>CDisplaySet::CompileSettings: (UINT)((gamma - 1) * 10) in single precision, which for
        /// 1.8 is SEVEN (1.8f - 1 is 0.79999995), clamped to the table.</summary>
        private static readonly int s_wpfGammaIndex = Math.Clamp((int)((WpfTextGamma - 1.0f) * 10.0f), 0, 12);

        /// <summary>EnhancedContrastTable::ReInit for k.</summary>
        private static readonly byte[] s_wpfContrastTable = BuildContrastTable(WpfTextContrast);

        /// <summary>DISPLAY-mode text (DWRITE_RENDERING_MODE_GDI_CLASSIC): GetAlphaBlendParams reports
        /// GDI's own values there -- the ClearType contrast as the gamma (1200 -> 1.2) and NO enhanced
        /// contrast, so wpfgfx applies no table at all (GetEnhancedContrastTable skips k = 0).</summary>
        private static readonly int s_wpfDisplayGammaIndex = Math.Clamp((int)(
            ((Platform.Win32Interop.FontSmoothingContrast() is int c && c > 0 ? c : 1200) / 1000f - 1.0f) * 10.0f), 0, 12);

        /// <summary>The identity: no enhanced contrast.</summary>
        private static readonly byte[] s_identityTable = BuildContrastTable(0f);

        private static byte[] BuildContrastTable(float k)
        {
            var t = new byte[256];
            for (int a = 1; a < 255; a++)
            {
                float real = a * (1.0f / 255);
                t[a] = (byte)((real * (k + 1)) / (real * k + 1) * 255 + 0.5f);
            }
            t[255] = 255;
            return t;
        }

        private readonly Dictionary<(Text.TrueTypeFont Font, int Glyph, float Ppem, int Rows), Text.NaturalClearType.GlyphBits>
            _naturalGlyphs = new();

        /// <summary>WPF_NATURAL_TRACE=1 reports each run and whether it took this path.</summary>
        private static readonly bool s_naturalTrace = Environment.GetEnvironmentVariable("WPF_NATURAL_TRACE") == "1";

        private void EmitWpfNaturalText(WpfTextRunDraw run, Matrix3x2 world, double opacity, Scissor clip,
            int width, int height, WGPUTextureFormat format, DrawData data)
        {
            if (clip.IsEmpty) return;
            float scale = world.M11;
            float ppem = run.EmSize * scale;
            // wpfgfx realizes ClearType only for an opaque target and an axis-aligned, unskewed scale.
            bool drawable = ClearType && !_transparentTarget
                            && world.M12 == 0f && world.M21 == 0f && world.M22 == scale && scale > 0f
                            && ppem > 0f && ppem <= 200f;
            // NATURAL_SYMMETRIC where the face's gasp asks for symmetric smoothing, which is exactly
            // what GetRecommendedRenderingMode answers (checked over 6..60ppem for twelve faces).
            // Display text is GDI_CLASSIC at every size, and GDI_CLASSIC is one row a pixel.
            bool display = run.Display;
            int nSub = !display && run.Font.WantsSymmetricSmoothing(ppem) ? 5 : 1;
            if (s_naturalTrace)
                Console.Error.WriteLine($"[natural] ppem={ppem:0.###} glyphs={run.Glyphs.Length} drawable={drawable}"
                                        + $" symmetric={run.Font.WantsSymmetricSmoothing(ppem)}");
            if (!drawable)
            {
                foreach (DrawingPrimitive p in run.Fallback) EmitPrimitive(p, world, opacity, clip, width, height, format, data);
                return;
            }

            // The glyphs' 1-bit bitmaps, each made once.
            int n = run.Glyphs.Length;
            var bits = new Text.NaturalClearType.GlyphBits[n];
            var xs = new float[n]; var ys = new float[n];
            for (int i = 0; i < n; i++)
            {
                var gkey = (run.Font, (int)run.Glyphs[i], ppem, display ? -1 : nSub);
                if (!_naturalGlyphs.TryGetValue(gkey, out Text.NaturalClearType.GlyphBits? gb))
                {
                    if (_naturalGlyphs.Count > 20000) _naturalGlyphs.Clear();
                    gb = display ? Text.NaturalClearType.RasterizeGdiClassic(run.Font, run.Glyphs[i], ppem)
                                 : Text.NaturalClearType.Rasterize(run.Font, run.Glyphs[i], ppem, nSub);
                    _naturalGlyphs[gkey] = gb;
                }
                bits[i] = gb;
                xs[i] = run.X[i] * scale;
                ys[i] = run.Y[i] * scale;
            }

            // The run's device origin: x where it falls, y on the baseline's whole pixel.
            Vector2 o = Vector2.Transform(run.Origin, world);
            int oy = (int)MathF.Floor(o.Y + 0.5f);
            // A display-measured run is pixel-snapped in x as well (CBaseGlyphRunPainter::Init rounds
            // m_20 when IsDisplayMeasured).
            float ox = display ? MathF.Floor(o.X + 0.5f) : o.X;
            int oxi = (int)MathF.Floor(ox);
            float frac = ox - oxi;

            RgbaColor ink = run.Color;
            float alpha = (float)Math.Clamp(ink.A * opacity, 0.0, 1.0);
            if (alpha <= 0f) return;

            // One mask per (glyphs, positions, ppem, ink, fraction, paper).
            long key = 17;
            unchecked
            {
                key = key * 31 + run.Font.GetHashCode();
                key = key * 31 + BitConverter.SingleToInt32Bits(ppem);
                key = key * 31 + (display ? -1 : nSub);
                for (int i = 0; i < n; i++)
                {
                    key = key * 31 + run.Glyphs[i];
                    key = key * 31 + Text.NaturalClearType.RoundHalfAway(6f * xs[i]);
                    key = key * 31 + Text.NaturalClearType.RoundHalfAway(nSub * ys[i]);
                }
                key = key * 31 + BitConverter.SingleToInt32Bits(frac);
                key = key * 397 ^ ((long)ToByte(ink.R) << 16 | (long)ToByte(ink.G) << 8 | ToByte(ink.B));
                key = key * 31 + BitConverter.SingleToInt32Bits(alpha);
                key = (key * 397 ^ (long)format) * 7 + 3;
            }

            Text.NaturalClearType.RunBounds(bits, xs, ys, nSub, out int tl, out int tt, out int tw, out int th);
            if (tw == 0 || th == 0) return;

            // The quad's device pixels: every one a sample of any channel can reach.
            int px0 = oxi + tl - 1, px1 = oxi + tl + tw + 2;
            int py0 = oy + tt, mw = px1 - px0, mh = th;

            RgbaColor? paper = null;
            if (s_textPaper && alpha >= 0.999f)
            {
                paper = PaperUnder(data, px0, py0, px1, py0 + mh,
                    nd => new Vector2((nd.X + 1f) * 0.5f * width + _devOX, (1f - nd.Y) * 0.5f * height + _devOY));
                if (paper is { } pp)
                    key = key * 397 ^ (1L << 40 | (long)ToByte(pp.R) << 16 | (long)ToByte(pp.G) << 8 | ToByte(pp.B));
            }

            if (!_maskCache.TryGetValue(key, out CachedMask? cm))
            {
                // Made only on a miss: the texture, then wpfgfx's resolve of it on this pixel grid.
                PerfCoverage++;
                byte[] tex = Text.NaturalClearType.RunTexture(bits, xs, ys, out _, out _, out _, out _, nSub);
                byte[] rgba = WpfNaturalMask(tex, tl, tw, th, frac, px0 - oxi, mw, ink, alpha, paper,
                    display ? s_wpfDisplayGammaIndex : s_wpfGammaIndex, display ? s_identityTable : s_wpfContrastTable);
                (IntPtr t, IntPtr view) = CreateRgbaTexture(rgba, mw, mh);
                cm = new CachedMask
                {
                    Tex = t, View = view,
                    BindGroup = CreateSampledBindGroup(format, FillKind.TextSubpixelMultiply, view, NearestSampler()),
                    BindGroupAdd = CreateSampledBindGroup(format, FillKind.TextSubpixelAdd, view, NearestSampler()),
                    Subpixel = true, Sampler = NearestSampler(), Format = format,
                    Ox = 0, Oy = 0, W = mw, H = mh,
                };
                _maskCache[key] = cm;
            }
            cm.LastFrame = _frameId;
            var solidInk = new RgbaColor(ink.R, ink.G, ink.B, 1f);
            EmitCachedSolidMask(cm, solidInk, 1.0, clip, width, height, data, px0, py0);
        }

        /// <summary>The per-channel mask for a run texture, as wpfgfx's CTSB shader and blend would
        /// resolve it on this pixel grid (see the file header). <paramref name="maskLeft"/> is the
        /// mask's first column relative to the origin's whole pixel.</summary>
        private static byte[] WpfNaturalMask(byte[] tex, int texLeft, int texWidth, int texHeight, float frac,
            int maskLeft, int maskWidth, RgbaColor ink, float alpha, RgbaColor? paper, int gi, byte[] ect)
        {
            float g1 = s_wpfGammaRatios[gi, 0], g2 = s_wpfGammaRatios[gi, 1];
            float g3 = s_wpfGammaRatios[gi, 2], g4 = s_wpfGammaRatios[gi, 3];
            float[] f = { ink.R, ink.G, ink.B };
            byte[] fg = { ToByte(ink.R), ToByte(ink.G), ToByte(ink.B) };
            byte[]? bg = paper is { } p ? new[] { ToByte(p.R), ToByte(p.G), ToByte(p.B) } : null;
            int texels = 3 * texWidth;

            var rgba = new byte[maskWidth * texHeight * 4];
            for (int row = 0; row < texHeight; row++)
            {
                int trow = row * texels;
                for (int j = 0; j < maskWidth; j++)
                {
                    // This pixel's centre, relative to the origin's whole pixel, in the texture's
                    // sub-pixel texels (texel 0 is the texture's left edge).
                    float centre = maskLeft + j + 0.5f - frac;
                    float s = 3f * (centre - texLeft) - 0.5f;
                    int o = (row * maskWidth + j) * 4;
                    int total = 0;
                    for (int k = 0; k < 3; k++)
                    {
                        float sk = s + (k - 1);                  // red one texel left, blue one right
                        int i0 = (int)MathF.Floor(sk);
                        float w = sk - i0;
                        float t0 = i0 >= 0 && i0 < texels ? ect[tex[trow + i0]] : 0;
                        float t1 = i0 + 1 >= 0 && i0 + 1 < texels ? ect[tex[trow + i0 + 1]] : 0;
                        float a = (t0 * (1f - w) + t1 * w) / 255f * alpha;
                        float a2 = a + a * (1f - a) * ((g1 * f[k] + g2) * a + (g3 * f[k] + g4));
                        a2 = Math.Clamp(a2, 0f, 1f);
                        byte m;
                        if (a <= 0f) m = 0;
                        else if (bg is not null)
                        {
                            int target = (int)MathF.Floor(fg[k] * a2 + bg[k] * (1f - a2) + 0.5f);
                            m = CoverageForTarget(fg[k], bg[k], target,
                                fg[k] == bg[k] ? a2 : (target - bg[k]) / (float)(fg[k] - bg[k]));
                        }
                        else m = (byte)MathF.Floor(a2 * 255f + 0.5f);
                        rgba[o + k] = m;
                        total += m;
                    }
                    rgba[o + 3] = (byte)(total / 3);
                }
            }
            return rgba;
        }
    }
}
