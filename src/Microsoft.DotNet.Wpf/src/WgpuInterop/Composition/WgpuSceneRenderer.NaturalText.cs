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
        /// them, read where DirectWrite reads them (Win32Interop.DWriteMonitorParams): Windows'
        /// defaults -- gamma 1.8, enhanced contrast 0.5 -- unless Avalon.Graphics says otherwise.</summary>
        private static readonly float WpfTextGamma = MonitorParam(0), WpfTextContrast = MonitorParam(1);

        private static float MonitorParam(int which)
        {
            Platform.Win32Interop.DWriteMonitorParams(out float gamma, out float contrast, out _);
            return which == 0 ? gamma : contrast;
        }

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

        /// <summary>The enhanced contrast table for a run. DWriteGlyphRunAnalysis::GetAlphaBlendParams
        /// @18000cd90 adds 0.5 to the contrast it reports when the analysis' thin flag is set
        /// (GlyphRunAnalysis::GlyphRunAnalysis @180127960 copies the face's thin bit, +0x60 bit 2,
        /// set by OpenTypeFontFaceBuilder::IsThinFontFamily @18002fb58): the monitor's 0.5 becomes
        /// 1.0 for natural text, and GDI-classic text, otherwise given none, gets 0.5.</summary>
        private static byte[] ContrastTableFor(Text.TrueTypeFont font, bool display)
        {
            if (!font.DWriteThinFace) return display ? s_identityTable : s_wpfContrastTable;
            return display ? s_thinDisplayContrastTable : s_thinContrastTable;
        }

        private static readonly byte[] s_thinContrastTable = BuildContrastTable(WpfTextContrast + 0.5f);
        private static readonly byte[] s_thinDisplayContrastTable = BuildContrastTable(0.5f);

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
            // The x scale may be negative -- an RTL FlowDirection visual -- when the run's render
            // data mirrored it back; only a run that ends up upright is drawn here.
            float scale = MathF.Abs(world.M11);
            // An axis-aligned rectangle clip, in device pixels. wpfgfx antialiases its edges the way
            // its rasterizer does everything (see EdgeCoverage): the text in a pixel an edge cuts is
            // lerped from the paper by the clip's share of the samples.
            float cx0 = 0, cy0 = 0, cx1 = 0, cy1 = 0;
            bool rectClip = false;
            if (run.ClipRect is Rect cr && world.M12 == 0f && world.M21 == 0f)
            {
                Vector2 c0 = Vector2.Transform(new Vector2((float)cr.X, (float)cr.Y), world);
                Vector2 c1 = Vector2.Transform(new Vector2((float)(cr.X + cr.Width), (float)(cr.Y + cr.Height)), world);
                cx0 = MathF.Min(c0.X, c1.X); cy0 = MathF.Min(c0.Y, c1.Y);
                cx1 = MathF.Max(c0.X, c1.X); cy1 = MathF.Max(c0.Y, c1.Y);
                int x0 = (int)MathF.Floor(cx0), y0 = (int)MathF.Floor(cy0);
                int x1 = (int)MathF.Ceiling(cx1), y1 = (int)MathF.Ceiling(cy1);
                clip = Intersect(clip, new Scissor(x0, y0, x1 - x0, y1 - y0));
                if (clip.IsEmpty) return;
                rectClip = true;
            }
            float ppem = run.EmSize * scale;
            // wpfgfx realizes ClearType only for an opaque target and an axis-aligned, unskewed scale.
            bool drawable = ClearType && !_transparentTarget
                            && world.M12 == 0f && world.M21 == 0f && world.M22 == scale && scale > 0f
                            && world.M11 * run.Mirror > 0f
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
                    // A legacy East Asian face's embedded strike, where DirectWrite draws it: every
                    // mode but NATURAL_SYMMETRIC (see DWriteStrike).
                    gb = nSub == 1 ? Text.NaturalClearType.DWriteStrike(run.Font, run.Glyphs[i], ppem, display) : null;
                    if (gb is null)
                    {
                        gb = display ? Text.NaturalClearType.RasterizeGdiClassic(run.Font, run.Glyphs[i], ppem)
                                     : Text.NaturalClearType.Rasterize(run.Font, run.Glyphs[i], ppem, nSub);
                        // A thin face's oversampled bitmap is thickened (GdiPlusText.ThinEmbolden).
                        gb = Text.GdiPlusText.ThinEmbolden(run.Font, gb);
                    }
                    _naturalGlyphs[gkey] = gb;
                }
                bits[i] = gb;
                xs[i] = run.X[i] * world.M11;
                ys[i] = run.Y[i] * scale;
                // Right to left, a glyph sits one nominal advance left of the pen; a display run's
                // nominal advance is GDI's whole pixels at this size, not the scaled design width.
                if (display && run.RtlNominal is { } nominal)
                {
                    int gdi = (int)MathF.Round(run.Font.DeviceAdvance(run.Glyphs[i], (int)MathF.Round(ppem, MidpointRounding.AwayFromZero)));
                    xs[i] += nominal[i] * scale - gdi;
                }
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

            // The clip's samples over the mask's columns and rows, where an edge cuts them.
            byte[]? colCover = null, rowCover = null;
            if (rectClip)
            {
                colCover = EdgeCoverage(px0, mw, cx0, cx1, 16);
                rowCover = EdgeCoverage(py0, mh, cy0, cy1, 8);
                unchecked
                {
                    if (colCover is not null) foreach (byte b in colCover) key = key * 11 + b;
                    if (rowCover is not null) foreach (byte b in rowCover) key = key * 13 + b;
                }
            }

            if (!_maskCache.TryGetValue(key, out CachedMask? cm))
            {
                // Made only on a miss: the texture, then wpfgfx's resolve of it on this pixel grid.
                PerfCoverage++;
                byte[] tex = Text.NaturalClearType.RunTexture(bits, xs, ys, out _, out _, out _, out _, nSub);
                byte[] rgba = WpfNaturalMask(tex, tl, tw, th, frac, px0 - oxi, mw, ink, alpha, paper,
                    display ? s_wpfDisplayGammaIndex : s_wpfGammaIndex, ContrastTableFor(run.Font, display),
                    colCover, rowCover);
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
            int maskLeft, int maskWidth, RgbaColor ink, float alpha, RgbaColor? paper, int gi, byte[] ect,
            byte[]? colCover = null, byte[]? rowCover = null)
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
                        // A clip edge through this pixel: the text's share of it (see EdgeCoverage).
                        int cover = (colCover?[j] ?? 16) * (rowCover?[row] ?? 8);
                        if (cover < 128)
                        {
                            a2 *= cover / 128f;
                            if (cover == 0) a = 0f;
                        }
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

        /// <summary>How many of wpfgfx's rasterizer samples in each of <paramref name="count"/>
        /// pixels from <paramref name="first"/> fall inside [lo, hi) -- or null when all of them do.
        /// The edges are snapped to 28.4 fixed point first; a pixel has 16 samples across and 8
        /// down (<paramref name="samples"/>), each 1/32 px into its cell. Measured on five clip
        /// edges: left 28.3 -> 11/16 and 41.7 -> 5/16; bottom 365.267 -> 2/8, 388.067 -> 1/8 and
        /// 439.417 -> 4/8.</summary>
        private static byte[]? EdgeCoverage(int first, int count, float lo, float hi, int samples)
        {
            float lo16 = MathF.Floor(lo * 16f + 0.5f) / 16f, hi16 = MathF.Floor(hi * 16f + 0.5f) / 16f;
            byte[]? cover = null;
            for (int i = 0; i < count; i++)
            {
                int inside = 0;
                for (int k = 0; k < samples; k++)
                {
                    float sample = first + i + (float)k / samples + 1f / 32f;
                    if (sample >= lo16 && sample < hi16) inside++;
                }
                if (inside < samples)
                {
                    if (cover is null) { cover = new byte[count]; Array.Fill(cover, (byte)samples); }
                    cover[i] = (byte)inside;
                }
            }
            return cover;
        }
    }
}
