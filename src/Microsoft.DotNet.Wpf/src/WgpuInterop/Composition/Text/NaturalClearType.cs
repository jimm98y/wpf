// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// WPF's text as DirectWrite renders it: the alpha texture IDWriteGlyphRunAnalysis::CreateAlphaTexture
// hands wpfgfx for a glyph run in DWRITE_RENDERING_MODE_NATURAL, which is what stock WPF draws ideal-
// mode text with (CGlyphRunResource::GetDWriteRenderingMode defers to GetRecommendedRenderingMode,
// and at text sizes that answers NATURAL or NATURAL_SYMMETRIC).
//
// Read out of dwrite.dll rather than inferred:
//
//   GlyphRunAnalysis::GlyphRunAnalysis   the mode's attributes (DAT_18036e030): NATURAL oversamples
//                                        6 across and 1 down, NATURAL_SYMMETRIC 6 and 5.
//   TrueTypeRasterizer::NewTransform     the scaler's mode word: 0x51 / 0x71 -- ClearType, sub-pixel
//                                        positioned, no compatible widths
//                                        (TrueTypeFont.TryGetDWriteFittedOutline).
//   GetGlyphBitmaps                      each glyph is scan-converted ONCE, to a 1-bit bitmap at the
//                                        oversampled resolution, and cached; its position never
//                                        reaches the rasterizer.
//   ComputeGlyphBitmapPositions          ...because the glyph is placed at round(6 x) samples
//                                        (round-half-up), i.e. the pen is quantized to 1/6 pixel.
//   CreateAlphaTexture3x1                the glyphs are ORed into one run bitmap (MergeGlyph1Bit),
//                                        and each output pixel reads the ten samples
//                                        [6p-2, 6p+8) through a 1024-entry table that is exactly a
//                                        SIX-SAMPLE BOX per channel, the channels two samples apart
//                                        (verified on all 1024 entries), then g_AlphaNormalizationTable6x1
//                                        = 0 43 85 128 170 213 255.
//   ApplyFilterImpl<AlphaTextureTarget,5> NATURAL_SYMMETRIC: the same box on each of FIVE sub-rows
//                                        (g_classicPlattFilterRGB carries the identical table), the
//                                        five levels weighted 4:9:10:9:4 and the sum S (0..216)
//                                        through g_AlphaNormalizationTable6x5 = round(S * 255 / 216).
//
// What wpfgfx then does with the texture (contrast, gamma, the fractional pen) is not here; this is
// the texture alone, and it is exact where the fitted outline is.
//

using System;
using System.Collections.Generic;

namespace Microsoft.Wpf.Interop.WebGpu.Composition.Text
{
    internal static class NaturalClearType
    {
        /// <summary>The level ramp after the box, g_AlphaNormalizationTable6x1.</summary>
        internal static ReadOnlySpan<byte> Ramp6x1 => new byte[] { 0, 43, 85, 128, 170, 213, 255 };

        /// <summary>DirectWrite's mode word for the scaler (TrueTypeRasterizer::NewTransform).</summary>
        internal const int NaturalFlags = 0x51, SymmetricFlags = 0x71;

        /// <summary>One glyph's 1-bit bitmap, positioned relative to its own origin.</summary>
        internal sealed class GlyphBits
        {
            /// <summary>Sample column of bit column 0, relative to the glyph origin (six a pixel).</summary>
            public int Left;
            /// <summary>Sub-row of bit row 0, relative to the baseline, y down (nSub a pixel).</summary>
            public int Top;
            public int Width, Height;
            /// <summary>bits[row * Width + column].</summary>
            public bool[] Bits = Array.Empty<bool>();
            public bool IsEmpty => Width == 0 || Height == 0;
        }

        private static readonly GlyphBits s_empty = new();

        /// <summary>The glyph as GetGlyphBitmaps makes it: fitted with DirectWrite's mode word where the
        /// face has a program for it (else the scaled outline), then scan-converted at six samples a
        /// pixel across and <paramref name="nSub"/> rows down.</summary>
        internal static GlyphBits Rasterize(TrueTypeFont font, int glyphId, float pixelsPerEm, int nSub,
                                            bool gridFit = true)
        {
            int flags = nSub > 1 ? SymmetricFlags : NaturalFlags;
            int dropout = 0;
            List<PathFigure> figures;
            if (!gridFit || !font.WantsGridFit(pixelsPerEm)
                || !font.TryGetDWriteFittedOutline(glyphId, pixelsPerEm, flags, out figures, out dropout))
            {
                dropout = 0;
                if (!font.TryGetScaledOutline(glyphId, pixelsPerEm, out figures)) return s_empty;
            }
            if (figures.Count == 0) return s_empty;

            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            void Take(System.Numerics.Vector2 p)
            {
                if (p.X < minX) minX = p.X; if (p.X > maxX) maxX = p.X;
                if (p.Y < minY) minY = p.Y; if (p.Y > maxY) maxY = p.Y;
            }
            foreach (PathFigure f in figures)
            {
                Take(f.Start);
                foreach (PathSegment s in f.Segments)
                    switch (s)
                    {
                        case LineSegment l: Take(l.Point); break;
                        case QuadraticBezierSegment q: Take(q.Control); Take(q.Point); break;
                        case CubicBezierSegment c: Take(c.Control1); Take(c.Control2); Take(c.Point); break;
                    }
            }
            if (minX > maxX) return s_empty;

            // A pixel of room on every side: dropout control can light a sample just outside the
            // outline's own box.
            int originX = (int)MathF.Floor(minX) - 1, originY = (int)MathF.Floor(minY) - 1;
            int width = (int)MathF.Ceiling(maxX) + 1 - originX, height = (int)MathF.Ceiling(maxY) + 1 - originY;
            bool[]? bits = PathRasterizer.ScanGlyphBits(new PathGeometry(FillRule.NonZero, figures),
                                                        originX, originY, width, height, nSub, dropout);
            if (bits is null) return s_empty;

            // Crop to the ink, so runs merge only what is there.
            int cols = width * 6, rows = height * nSub;
            int c0 = cols, c1 = -1, r0 = rows, r1 = -1;
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < cols; c++)
                    if (bits[r * cols + c])
                    {
                        if (c < c0) c0 = c; if (c > c1) c1 = c;
                        if (r < r0) r0 = r; if (r > r1) r1 = r;
                    }
            if (c1 < 0) return s_empty;
            var g = new GlyphBits
            {
                Left = originX * 6 + c0, Top = originY * nSub + r0,
                Width = c1 - c0 + 1, Height = r1 - r0 + 1,
            };
            g.Bits = new bool[g.Width * g.Height];
            for (int r = 0; r < g.Height; r++)
                Array.Copy(bits, (r0 + r) * cols + c0, g.Bits, r * g.Width, g.Width);
            return g;
        }

        /// <summary>DirectWrite's rounding of a float to an int in GlyphRunAnalysis: truncate, then
        /// step away from zero when the rest is at least a half.</summary>
        internal static int RoundHalfAway(float v)
        {
            int i = (int)v;
            if (v < 0f) { if (0.5f < i - v) i--; }
            else if (i - v <= -0.5f) i++;
            return i;
        }

        /// <summary>The vertical weights of the symmetric filter, one per sub-row.</summary>
        private static ReadOnlySpan<byte> SymmetricWeights => new byte[] { 4, 9, 10, 9, 4 };

        /// <summary>The alpha texture of a run in DWRITE_RENDERING_MODE_NATURAL (<paramref name="nSub"/>
        /// 1) or NATURAL_SYMMETRIC (5), as CreateAlphaTexture3x1 makes it: three bytes a pixel, rows
        /// <paramref name="top"/>.. from the baseline, pixels <paramref name="left"/>.. from the run
        /// origin. <paramref name="xs"/> and <paramref name="ys"/> are each glyph's origin relative to
        /// the run's, in pixels; the glyphs' bitmaps must have been made with the same sub-row count.</summary>
        internal static byte[] RunTexture(IReadOnlyList<GlyphBits> glyphs, IReadOnlyList<float> xs,
                                          IReadOnlyList<float> ys, out int left, out int top,
                                          out int width, out int height, int nSub = 1)
            => Build(glyphs, xs, ys, nSub, texture: true, out left, out top, out width, out height);

        /// <summary>Where <see cref="RunTexture"/>'s texture would lie, without making it.</summary>
        internal static void RunBounds(IReadOnlyList<GlyphBits> glyphs, IReadOnlyList<float> xs,
                                       IReadOnlyList<float> ys, int nSub, out int left, out int top,
                                       out int width, out int height)
            => Build(glyphs, xs, ys, nSub, texture: false, out left, out top, out width, out height);

        private static byte[] Build(IReadOnlyList<GlyphBits> glyphs, IReadOnlyList<float> xs,
                                    IReadOnlyList<float> ys, int nSub, bool texture, out int left, out int top,
                                    out int width, out int height)
        {
            // Samples and rows of every glyph, in the run's frame.
            int n = glyphs.Count;
            var sx = new int[n]; var sy = new int[n];
            int s0 = int.MaxValue, s1 = int.MinValue, y0 = int.MaxValue, y1 = int.MinValue;
            for (int i = 0; i < n; i++)
            {
                GlyphBits g = glyphs[i];
                if (g.IsEmpty) continue;
                sx[i] = RoundHalfAway(6f * xs[i]) + g.Left;
                sy[i] = RoundHalfAway(nSub * ys[i]) + g.Top;
                s0 = Math.Min(s0, sx[i]); s1 = Math.Max(s1, sx[i] + g.Width);
                y0 = Math.Min(y0, sy[i]); y1 = Math.Max(y1, sy[i] + g.Height);
            }
            if (s0 > s1) { left = top = width = height = 0; return Array.Empty<byte>(); }

            // Every pixel whose ten-sample window [6p-2, 6p+8) reaches ink; whole pixel rows
            // (GlyphRunAnalysis rounds the sub-row bounds out to a multiple of the oversample).
            left = FloorDiv(s0 - 7, 6);
            int right = FloorDiv(s1 + 1, 6) + 1;
            int r0 = FloorDiv(y0, nSub), r1 = FloorDiv(y1 + nSub - 1, nSub);
            top = r0; height = r1 - r0; width = right - left;
            y0 = r0 * nSub;
            if (!texture) return Array.Empty<byte>();
            int subRows = height * nSub;

            // The merged bitmap, starting at sample 6*left - 2 as the texture's does.
            int b0 = 6 * left - 2, cols = 6 * width + 4;
            var bits = new bool[subRows * cols];
            for (int i = 0; i < n; i++)
            {
                GlyphBits g = glyphs[i];
                if (g.IsEmpty) continue;
                for (int r = 0; r < g.Height; r++)
                {
                    int row = sy[i] + r - y0;
                    for (int c = 0; c < g.Width; c++)
                        if (g.Bits[r * g.Width + c]) bits[row * cols + sx[i] + c - b0] = true;
                }
            }

            ReadOnlySpan<byte> ramp = Ramp6x1;
            ReadOnlySpan<byte> weights = SymmetricWeights;
            var tex = new byte[width * height * 3];
            for (int row = 0; row < height; row++)
                for (int p = 0; p < width; p++)
                    for (int k = 0; k < 3; k++)
                    {
                        int sum = 0;
                        for (int sr = 0; sr < nSub; sr++)
                        {
                            int level = 0, at = (row * nSub + sr) * cols + 6 * p + 2 * k;
                            for (int j = 0; j < 6; j++) if (bits[at + j]) level++;
                            sum += nSub == 1 ? level : weights[sr] * level;
                        }
                        tex[(row * width + p) * 3 + k] = nSub == 1 ? ramp[sum] : (byte)((sum * 255 + 108) / 216);
                    }
            return tex;
        }

        private static int FloorDiv(int a, int b) => a >= 0 ? a / b : -((-a + b - 1) / b);
    }
}
