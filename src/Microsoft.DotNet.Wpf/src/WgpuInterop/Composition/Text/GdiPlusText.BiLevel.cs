// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// GDI+'s bi-level glyphs (render modes 1 and 2): what it draws for SingleBitPerPixelGridFit,
// SingleBitPerPixel, an AntiAliasGridFit size the face's 'gasp' does not grey, and a ClearType size
// drawn from embedded bitmaps. Read out of the binaries:
//
//   GpGraphics::DrawPlacedGlyphs @180010398 (gdiplus)   CreateGlyphBitmapArray with raster type 0
//       for modes 1 and 2, and grid fitting ENABLED (2) only for an axis-aligned or quarter-turned
//       transform and a mode other than 2 -- SingleBitPerPixel scans the unfitted outline.
//   GetRenderingModeAttributes (dwrite, table @180373140)   raster type 0 is 0x800011: overscale
//       1x1, bit 23 (embedded bitmaps).
//   MakeRasterizerFlagsForRendering @180090b18, TrueTypeRasterizer::NewTransform @18006bc80   the
//       flags are 0x61 fitted (0x41 when fs_FindBlocForPpem finds no strike at the size), and an
//       x overscale of 1 hands the scaler the word 0 -- the classic bi-level fit, no ClearType bit
//       (TrueTypeFont.DWriteBiLevelWord). Unfitted, the flags are 0 and the program does not run.
//   MakeRasterizerTransform @18008f638   fitted at the WHOLE ppem (round(em) << 16); unfitted at
//       the em itself in 16.16.
//   GlyphBitmapArray::GetGlyphBitmapBounds @18012b1d0   the bitmap is made once and placed at
//       round-half-away(x) + left, round-half-away(y) - top: x is not quantized for these modes.
//   GpFaceRealization::GetGlyphPos @180023d18   each byte bit-reversed (table @1802abfc0), one bit a
//       pixel; DWriteOutputSolidNormalTextOptimized @1800a42c0 then paints the premultiplied brush
//       where a bit is set, through the ordinary blend.
//

using System;
using System.Collections.Generic;

namespace Microsoft.Wpf.Interop.WebGpu.Composition.Text
{
    internal static partial class GdiPlusText
    {
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<TrueTypeFont, Dictionary<(int, int, bool), GreyGlyph>>
            s_mono = new();

        /// <summary>DirectWrite's raster type 0 glyph for GDI+: <paramref name="gridFit"/> the bi-level
        /// fit at the whole ppem (or the size's embedded strike), else the outline scaled to the em;
        /// scanned one sample a pixel with the scan converter's dropout control. Coverage is 15
        /// where a bit is set; Left/Top are relative to the pixel the origin rounds to.</summary>
        internal static GreyGlyph Mono(TrueTypeFont font, int gid, float em, bool gridFit)
        {
            var cache = s_mono.GetValue(font, _ => new Dictionary<(int, int, bool), GreyGlyph>());
            var key = (gid, BitConverter.SingleToInt32Bits(em), gridFit);
            lock (cache)
                if (cache.TryGetValue(key, out GreyGlyph? hit)) return hit;
            GreyGlyph g = BuildMono(font, gid, em, gridFit);
            lock (cache)
            {
                if (cache.Count > 8192) cache.Clear();
                cache[key] = g;
            }
            return g;
        }

        private static GreyGlyph BuildMono(TrueTypeFont font, int gid, float em, bool gridFit)
        {
            var g = new GreyGlyph();
            int ppem = Floor(em + 0.5f);
            if (gridFit && font.TryGetStrikeGlyph(gid, ppem, out BitmapGlyph bmp))
            {
                // The strike's own bits, its bearing from the origin: x right, y up to the top row.
                CropInto(g, bmp.Png, bmp.PixelWidth, bmp.PixelHeight, bmp.BearingX, -bmp.BearingY, b => b != 0);
                return g;
            }
            List<PathFigure> figures;
            int dropout;
            if (!gridFit || !font.TryGetDWriteFittedOutline(gid, ppem, TrueTypeFont.DWriteBiLevelWord, out figures, out dropout))
            {
                // Unfitted: no program has run, so nothing set SCANCTRL; the scan converter's own
                // default control is what fills the thin features.
                dropout = UnfittedDropout;
                if (!font.TryGetScaledOutline(gid, em, out figures)) return g;
            }
            float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
            foreach (PathFigure f in figures)
            {
                Take(f.Start);
                foreach (PathSegment sg in f.Segments)
                    switch (sg)
                    {
                        case LineSegment l: Take(l.Point); break;
                        case QuadraticBezierSegment q: Take(q.Control); Take(q.Point); break;
                        case CubicBezierSegment c: Take(c.Control1); Take(c.Control2); Take(c.Point); break;
                    }
            }
            void Take(System.Numerics.Vector2 p)
            {
                if (p.X < x0) x0 = p.X; if (p.X > x1) x1 = p.X;
                if (p.Y < y0) y0 = p.Y; if (p.Y > y1) y1 = p.Y;
            }
            if (x0 > x1) return g;
            int ox = (int)MathF.Floor(x0) - 1, oy = (int)MathF.Floor(y0) - 1;
            int w = (int)MathF.Ceiling(x1) + 1 - ox, h = (int)MathF.Ceiling(y1) + 1 - oy;
            bool[]? bits = PathRasterizer.ScanGlyphBits(new PathGeometry(FillRule.NonZero, figures), ox, oy, w, h, 1, dropout, 1);
            if (bits is null) return g;
            var bytes = new byte[bits.Length];
            for (int i = 0; i < bits.Length; i++) if (bits[i]) bytes[i] = 1;
            CropInto(g, bytes, w, h, ox, oy, b => b != 0);
            return g;
        }

        /// <summary>The dropout mode an unfitted bi-level scan runs with (PathRasterizer's terms,
        /// SCANTYPE + 1): measured against DirectWrite's unfitted raster type 0.</summary>
        private const int UnfittedDropout = 2;

        private static void CropInto(GreyGlyph g, byte[] src, int w, int h, int left, int top, Func<byte, bool> on)
        {
            int c0 = w, c1 = -1, r0 = h, r1 = -1;
            for (int r = 0; r < h; r++)
                for (int c = 0; c < w; c++)
                    if (on(src[r * w + c]))
                    {
                        if (c < c0) c0 = c; if (c > c1) c1 = c;
                        if (r < r0) r0 = r; if (r > r1) r1 = r;
                    }
            if (c1 < 0) return;
            g.Left = left + c0; g.Top = top + r0; g.Width = c1 - c0 + 1; g.Height = r1 - r0 + 1;
            g.Coverage = new byte[g.Width * g.Height];
            for (int r = 0; r < g.Height; r++)
                for (int c = 0; c < g.Width; c++)
                    if (on(src[(r0 + r) * w + c0 + c])) g.Coverage[r * g.Width + c] = 15;
        }

        /// <summary>A bi-level run: each glyph's bits at round-half-away of its origin, the glyphs
        /// merged (a set bit stays set). Levels.Index holds 15 where a pixel is ink.</summary>
        internal static Levels ComposeMono(TrueTypeFont font, IReadOnlyList<ushort> gids, float em, float[] xs, float y, bool gridFit)
        {
            int n = gids.Count;
            var place = new (GreyGlyph G, int X, int Y)[n];
            int p0 = int.MaxValue, p1 = int.MinValue, r0 = int.MaxValue, r1 = int.MinValue;
            int iy = NaturalClearType.RoundHalfAway(y);
            for (int i = 0; i < n; i++)
            {
                GreyGlyph g = Mono(font, gids[i], em, gridFit);
                place[i] = (g, NaturalClearType.RoundHalfAway(xs[i]) + g.Left, iy + g.Top);
                if (g.Width == 0) continue;
                p0 = Math.Min(p0, place[i].X); p1 = Math.Max(p1, place[i].X + g.Width - 1);
                r0 = Math.Min(r0, place[i].Y); r1 = Math.Max(r1, place[i].Y + g.Height - 1);
            }
            var lv = new Levels { Grey = true };
            if (p0 > p1) return lv;
            lv.Left = p0; lv.Top = r0; lv.Width = p1 - p0 + 1; lv.Height = r1 - r0 + 1;
            lv.Index = new byte[lv.Width * lv.Height];
            foreach ((GreyGlyph g, int gx, int gy) in place)
                for (int r = 0; r < g.Height; r++)
                    for (int c = 0; c < g.Width; c++)
                        if (g.Coverage[r * g.Width + c] != 0)
                            lv.Index[(gy + r - lv.Top) * lv.Width + gx + c - lv.Left] = 15;
            return lv;
        }
    }
}
