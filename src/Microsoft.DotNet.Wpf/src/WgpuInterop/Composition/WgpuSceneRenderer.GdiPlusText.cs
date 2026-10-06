// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// GDI+'s Graphics.DrawString (the fast imager), drawn the way gdiplus.dll draws it: the layout is
// already done (Text.GdiPlusText.Layout, on the caller's side); here the glyphs are placed on the
// device grid, their 6x1 bitmaps run through GDI's ClearType filter and composed by level sums,
// and every pixel is blended with GDI+'s CTBlendSolid against the paper under it (the antialiased
// hints: 4x4 glyphs composed by max, through GDI+'s text gamma table).
//
// The blend is a function of the destination byte, which a GPU blend state cannot express, so --
// as for WPF's natural text -- the paper is read from the draw list (PaperUnder) and each channel of
// the mask is chosen so the two-draw subpixel composite lands exactly on the byte GDI+ writes.
// Where the paper cannot be told, the levels go through as plain coverage.
//

using System;
using System.Collections.Generic;
using System.Numerics;
using static Microsoft.Wpf.Interop.WebGpu.Wgpu;

namespace Microsoft.Wpf.Interop.WebGpu.Composition
{
    internal sealed unsafe partial class WgpuSceneRenderer
    {
        private readonly Dictionary<(Text.TrueTypeFont Font, int Glyph, float Em), Text.NaturalClearType.GlyphBits>
            _gdiPlusGlyphs = new();

        /// <summary>WPF_GDIPLUS_TRACE=1 reports each GDI+ run and how it was drawn.</summary>
        private static readonly bool s_gdiPlusTrace = Environment.GetEnvironmentVariable("WPF_GDIPLUS_TRACE") == "1";

        private void EmitGdiPlusText(GdiPlusTextDraw draw, Matrix3x2 world, double opacity, Scissor clip,
            int width, int height, WGPUTextureFormat format, DrawData data)
        {
            if (clip.IsEmpty) return;
            Text.GdiPlusText.Run run = draw.Run;
            // FastTextImager takes only a positive axis scale; this port lays out at 96 dpi, so the
            // device transform has to be a translation. Anything else draws the ordinary string run.
            bool drawable = s_gammaComposite && !_transparentTarget
                            && world.M11 == 1f && world.M22 == 1f && world.M12 == 0f && world.M21 == 0f
                            && FontFor(draw.Style, draw.FontFamily) is Text.TrueTypeFont;
            if (s_gdiPlusTrace)
                Console.Error.WriteLine($"[gdiplus] glyphs={run.Glyphs.Length} em={run.Em} origin=({run.OriginX},{run.OriginY}) "
                                        + $"world=({world.M31},{world.M32}) drawable={drawable}");
            if (!drawable)
            {
                EmitText(draw.Fallback, world, opacity, clip, width, height, format, data);
                return;
            }
            var font = (Text.TrueTypeFont)FontFor(draw.Style, draw.FontFamily);
            int n = run.Glyphs.Length;
            if (n == 0) return;

            // Device positions, as GDI+ forms them (float sums from the rounded origin).
            float[] xs = Text.GdiPlusText.GlyphXs(run, run.OriginX + world.M31);
            float y = run.OriginY + world.M32;
            Text.GdiPlusText.Levels lv;
            if (run.Mode == Text.GdiPlusText.HintAntiAlias || run.Mode == Text.GdiPlusText.HintAntiAliasGridFit)
                lv = Text.GdiPlusText.ComposeGrey(font, run.Glyphs, run.Em, xs, y);
            else
            {
                var bits = new Text.NaturalClearType.GlyphBits[n];
                for (int i = 0; i < n; i++)
                {
                    var gkey = (font, (int)run.Glyphs[i], run.Em);
                    if (!_gdiPlusGlyphs.TryGetValue(gkey, out Text.NaturalClearType.GlyphBits? gb))
                    {
                        if (_gdiPlusGlyphs.Count > 20000) _gdiPlusGlyphs.Clear();
                        gb = Text.GdiPlusText.Glyph(font, run.Glyphs[i], run.Em);
                        _gdiPlusGlyphs[gkey] = gb;
                    }
                    bits[i] = gb;
                }
                lv = Text.GdiPlusText.Compose(bits, xs, y, run.FixedFilter);
            }
            if (lv.Width == 0 || lv.Height == 0) return;

            // The layout rectangle as GDI+ clips to it: pixels [ceil(l), ceil(r)) x [ceil(t), ceil(b)).
            if (run.HasClip)
            {
                float l = run.ClipX + world.M31, t = run.ClipY + world.M32;
                int cx0 = (int)MathF.Ceiling(l), cy0 = (int)MathF.Ceiling(t);
                int cx1 = (int)MathF.Ceiling(run.ClipX + run.ClipW + world.M31);
                int cy1 = (int)MathF.Ceiling(run.ClipY + run.ClipH + world.M32);
                clip = Intersect(clip, new Scissor(cx0, cy0, cx1 - cx0, cy1 - cy0));
                if (clip.IsEmpty) return;
            }

            int argb = draw.Argb;
            byte br = (byte)(argb >> 16), bgc = (byte)(argb >> 8), bb = (byte)argb;
            int alpha = (int)Math.Clamp(Math.Round(((argb >> 24) & 0xff) * Math.Clamp(opacity, 0.0, 1.0)), 0, 255);
            if (alpha == 0) return;

            int px0 = lv.Left, py0 = lv.Top, mw = lv.Width, mh = lv.Height;
            Func<int, int, RgbaColor?>? field = null;
            RgbaColor? paper = s_textPaper
                ? PaperUnder(data, px0, py0, px0 + mw, py0 + mh,
                    nd => new Vector2((nd.X + 1f) * 0.5f * width + _devOX, (1f - nd.Y) * 0.5f * height + _devOY), out field)
                : null;

            long key = 23;
            unchecked
            {
                key = key * 31 + font.GetHashCode();
                key = key * 31 + BitConverter.SingleToInt32Bits(run.Em);
                key = key * 31 + (run.FixedFilter ? 1 : 0) + 2 * run.Mode + 16 * run.Contrast;
                for (int i = 0; i < n; i++)
                {
                    key = key * 31 + run.Glyphs[i];
                    key = key * 31 + BitConverter.SingleToInt32Bits(xs[i] - px0);
                }
                key = key * 31 + BitConverter.SingleToInt32Bits(y - py0);
                key = key * 397 ^ ((long)(argb & 0xffffff) | (long)alpha << 24);
                if (paper is { } pp)
                    key = key * 397 ^ (1L << 40 | (long)ToByte(pp.R) << 16 | (long)ToByte(pp.G) << 8 | ToByte(pp.B));
                else if (field is not null)
                    key = key * 397 ^ (2L << 40 | (long)px0 << 20 | (uint)py0);   // a per-pixel paper: keyed by place
                key = (key * 397 ^ (long)format) * 7 + 5;
            }

            if (!_maskCache.TryGetValue(key, out CachedMask? cm))
            {
                PerfCoverage++;
                byte[] rgba = GdiPlusMask(lv, br, bgc, bb, alpha, paper, field, run.Contrast);
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
            var ink = new RgbaColor(br / 255f, bgc / 255f, bb / 255f, 1f);
            EmitCachedSolidMask(cm, ink, 1.0, clip, width, height, data, px0, py0);
        }

        /// <summary>The per-channel mask for a run's levels: on known paper, the value whose two-draw
        /// result is the byte CTBlendSolid writes; elsewhere the level as coverage.</summary>
        private static byte[] GdiPlusMask(Text.GdiPlusText.Levels lv, byte br, byte bg, byte bb, int alpha,
            RgbaColor? paper, Func<int, int, RgbaColor?>? field, int contrast)
        {
            int mw = lv.Width, mh = lv.Height;
            var rgba = new byte[mw * mh * 4];
            byte[] fg = { br, bg, bb };
            for (int row = 0; row < mh; row++)
                for (int col = 0; col < mw; col++)
                {
                    int idx = lv.Index[row * mw + col];
                    if (idx == 0) continue;
                    RgbaColor? under = paper ?? field?.Invoke(lv.Left + col, lv.Top + row);
                    (int l0, int l1, int l2) = lv.Grey ? (idx, idx, idx) : Text.GdiPlusText.LevelsOf(idx);
                    int o = (row * mw + col) * 4, total = 0;
                    if (under is { } p)
                    {
                        byte pr = ToByte(p.R), pg = ToByte(p.G), pb = ToByte(p.B);
                        (byte r, byte g, byte b) = lv.Grey
                            ? Text.GdiPlusText.BlendGreyPixel(idx, br, bg, bb, alpha, pr, pg, pb, contrast)
                            : Text.GdiPlusText.BlendPixel(idx, br, bg, bb, alpha, pr, pg, pb, contrast);
                        byte[] bgp = { pr, pg, pb };
                        int[] target = { r, g, b };
                        int[] lev = { l0, l1, l2 };
                        for (int k = 0; k < 3; k++)
                        {
                            byte m;
                            if (target[k] == bgp[k]) m = 0;
                            else
                            {
                                float want = fg[k] == bgp[k] ? lev[k] / (lv.Grey ? 15f : 6f)
                                                             : (target[k] - bgp[k]) / (float)(fg[k] - bgp[k]);
                                m = CoverageForTarget(fg[k], bgp[k], target[k], Math.Clamp(want, 0f, 1f));
                            }
                            rgba[o + k] = m;
                            total += m;
                        }
                    }
                    else
                    {
                        int[] lev = { l0, l1, l2 };
                        for (int k = 0; k < 3; k++)
                        {
                            byte m = (byte)(lev[k] * alpha / (lv.Grey ? 15 : 6));
                            rgba[o + k] = m;
                            total += m;
                        }
                    }
                    rgba[o + 3] = (byte)(total / 3);
                }
            return rgba;
        }
    }
}
