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

        // ---- glyphs under a transform the fast imager refuses (FullTextImager's path) ----------
        //
        // GpGraphics::DrawPlacedGlyphs passes the device transform's linear part to
        // CreateGlyphBitmapArray and asks for grid fitting only when it is axis-aligned or a quarter
        // turn (GpFaceRealization +0xbc / +0xc0, from bGetDEVICEMETRICS @1800a2c08); otherwise the
        // outline is scaled and turned unfitted (MakeRasterizerTransform with the matrix), scanned
        // once at the raster type's overscale, and placed at round-half-away of its sample position.


        /// <summary>The post-transform (16.16, glyph y-up to device y-up) DirectWrite's scaler puts an
        /// UNFITTED glyph through under a 2x2 transform (world y down) at an em; see
        /// <see cref="TransformedOutline"/>.</summary>
        internal static bool UnfittedTurnedPoints(TrueTypeFont font, float em, float m11, float m12, float m21, float m22,
                                                  out int p00, out int p01, out int p10, out int p11)
        {
            int upem = font.UnitsPerEmForHinting;
            static int Fx(float v) => (int)(((long)(v * 65536.0 * 65536.0) + 0x8000) >> 16);
            int a00 = Fx(m11 * em), a01 = Fx(-m12 * em), a10 = Fx(-m21 * em), a11 = Fx(m22 * em);
            int d = upem << 16;
            static int Div(int a, int d) => (int)(((long)a * 0x10000 + ((a < 0) != (d < 0) ? -(d / 2) : d / 2)) / d);
            p00 = Div(a00, d); p01 = Div(a01, d); p10 = Div(a10, d); p11 = Div(a11, d);
            return upem > 0 && (a00 | a01) != 0 && (a10 | a11) != 0;
        }

        /// <summary>One design point (font units, y up) through <see cref="UnfittedTurnedPoints"/>'
        /// matrix: its units times 64, each product FixMul-rounded, on the device (pixels, y down).</summary>
        internal static (float X, float Y) UnfittedTurnedPoint(System.Numerics.Vector2 pt, int p00, int p01, int p10, int p11)
        {
            static int Mul(int a, int b) { long p = (long)a * b; return (int)((p + (p >> 63) + 0x8000) >> 16); }
            int x = (int)Math.Round(pt.X) * 64, y = (int)Math.Round(pt.Y) * 64;
            int X = Mul(x, p00) + Mul(y, p10), Y = Mul(x, p01) + Mul(y, p11);
            return (X / 64f, -Y / 64f);
        }

        /// <summary>The design outline scaled to <paramref name="em"/> and put through the 2x2
        /// matrix (world y down), in the scaler's own arithmetic, moved by (dx, dy) device pixels.
        /// <list type="bullet">
        /// <item>MakeRasterizerTransform @18008fe48 (dwrite): the matrix times the em, each entry to
        /// 16.16 (round half up), in the scaler's y-up frame: (m11, -m12; -m21, m22) em;</item>
        /// <item>TrueTypeRasterizer::Implementation::NewTransform @18006c460 hands it over with
        /// +0xce set (no point size folded in) and, for a glyph that is not grid-fitted, +0xd6
        /// (~flags &amp; 1): scl_InitializeScaling's param_19, under which the outline is scaled
        /// by nothing -- its font units times 64 -- and scl_PostTransformGlyph divides the
        /// matrix by upem (+0x184/+0x188) where a fitted glyph is scaled at its rows' stretch;</item>
        /// <item>mth_IntelMul @140026b40: each row over that divisor (DWRITE_FixDiv), every
        /// product rounded on its own.</item>
        /// </list></summary>
        internal static List<PathFigure> TransformedOutline(TrueTypeFont font, int gid, float em,
                                                            float m11, float m12, float m21, float m22, float dx, float dy)
        {
            var figures = new List<PathFigure>();
            if (!UnfittedTurnedPoints(font, em, m11, m12, m21, m22, out int p00, out int p01, out int p10, out int p11))
                return figures;
            List<(System.Numerics.Vector2[] Points, bool[] OnCurve)> contours = font.DesignContours(gid);
            // The frame's points in 26.6: font units times 64. A simulated bold is fsg_Embold's on
            // them, unfitted, at the frame's own ppem -- the upem, so about 2% of the em each way.
            int total = 0;
            foreach (var c0 in contours) total += c0.Points.Length;
            var X26 = new int[total]; var Y26 = new int[total]; var ends = new int[contours.Count];
            int at = 0;
            for (int k = 0; k < contours.Count; k++)
            {
                foreach (System.Numerics.Vector2 v in contours[k].Points)
                {
                    X26[at] = (int)Math.Round(v.X) * 64; Y26[at] = (int)Math.Round(v.Y) * 64; at++;
                }
                ends[k] = at - 1;
            }
            if (font.SynthesizesBold) TrueTypeFont.GdiEmboldenUnfitted(X26, Y26, ends, font.UnitsPerEmForHinting);
            static int Mul(int a, int b) { long pr = (long)a * b; return (int)((pr + (pr >> 63) + 0x8000) >> 16); }
            int baseAt = 0;
            foreach ((System.Numerics.Vector2[] pts, bool[] on) in contours)
            {
                int n = pts.Length;
                int b0 = baseAt; baseAt += n;
                if (n < 2) continue;
                var p = new System.Numerics.Vector2[n];
                for (int i = 0; i < n; i++)
                {
                    int x = X26[b0 + i], y = Y26[b0 + i];
                    int Xd = Mul(x, p00) + Mul(y, p10), Yd = Mul(x, p01) + Mul(y, p11);
                    p[i] = new System.Numerics.Vector2(Xd / 64f + dx, -Yd / 64f + dy);
                }
                int s0 = Array.IndexOf(on, true);
                var q = new List<(System.Numerics.Vector2 P, bool On)>(n + 1);
                if (s0 < 0)
                {
                    q.Add(((p[0] + p[1]) * 0.5f, true));
                    for (int i = 1; i <= n; i++) q.Add((p[i % n], false));
                }
                else for (int i = 0; i < n; i++) q.Add((p[(s0 + i) % n], on[(s0 + i) % n]));
                var f = new PathFigure(q[0].P) { Closed = true };
                int c = q.Count, j = 1;
                while (j <= c)
                {
                    var (pt, isOn) = q[j % c];
                    if (isOn) { f.Segments.Add(new LineSegment(pt)); j++; continue; }
                    var (nx, nOn) = q[(j + 1) % c];
                    if (nOn) { f.Segments.Add(new QuadraticBezierSegment(pt, nx)); j += 2; }
                    else { f.Segments.Add(new QuadraticBezierSegment(pt, (pt + nx) * 0.5f)); j += 1; }
                }
                figures.Add(f);
            }
            return figures;
        }

        /// <summary>A bi-level run under a general transform (raster type 0, grid fitting off):
        /// each glyph's unfitted outline through the matrix, scanned one sample a pixel with the
        /// scan converter's own dropout control, placed at round-half-away of its origin, merged.</summary>
        internal static Levels ComposeMonoTransformed(TrueTypeFont font, IReadOnlyList<ushort> gids, float em,
                                                      float m11, float m12, float m21, float m22, float[] xs, float[] ys)
        {
            int n = gids.Count;
            var place = new (GreyGlyph G, int X, int Y)[n];
            int p0 = int.MaxValue, p1 = int.MinValue, r0 = int.MaxValue, r1 = int.MinValue;
            for (int i = 0; i < n; i++)
            {
                var g = new GreyGlyph();
                List<PathFigure> figures = TransformedOutline(font, gids[i], em, m11, m12, m21, m22, 0f, 0f);
                if (figures.Count > 0)
                {
                    float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
                    foreach (PathFigure f in figures)
                    {
                        Take(f.Start);
                        foreach (PathSegment sg in f.Segments)
                            if (sg is LineSegment l) Take(l.Point);
                            else if (sg is QuadraticBezierSegment q) { Take(q.Control); Take(q.Point); }
                    }
                    void Take(System.Numerics.Vector2 p)
                    {
                        if (p.X < x0) x0 = p.X; if (p.X > x1) x1 = p.X;
                        if (p.Y < y0) y0 = p.Y; if (p.Y > y1) y1 = p.Y;
                    }
                    int ox = (int)MathF.Floor(x0) - 1, oy = (int)MathF.Floor(y0) - 1;
                    int w = (int)MathF.Ceiling(x1) + 1 - ox, h = (int)MathF.Ceiling(y1) + 1 - oy;
                    bool[]? bits = PathRasterizer.ScanGlyphBits(new PathGeometry(FillRule.NonZero, figures), ox, oy, w, h, 1, UnfittedDropout, 1);
                    if (bits is not null)
                    {
                        var bytes = new byte[bits.Length];
                        for (int k = 0; k < bits.Length; k++) if (bits[k]) bytes[k] = 1;
                        CropInto(g, bytes, w, h, ox, oy, b => b != 0);
                    }
                }
                place[i] = (g, NaturalClearType.RoundHalfAway(xs[i]) + g.Left, NaturalClearType.RoundHalfAway(ys[i]) + g.Top);
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

        /// <summary>An antialiased run under a general transform: each glyph's transformed outline
        /// scanned 4x4 at its quarter-pixel phase, coverage by max (Levels.Index 0..15).</summary>
        internal static Levels ComposeGreyTransformed(TrueTypeFont font, IReadOnlyList<ushort> gids, float em,
                                                      float m11, float m12, float m21, float m22,
                                                      float[] xs, float[] ys, int dropout)
        {
            int n = gids.Count;
            var cov = new Dictionary<(int, int), int>();
            for (int i = 0; i < n; i++)
            {
                int qx = NaturalClearType.RoundHalfAway(xs[i] * 4f), qy = NaturalClearType.RoundHalfAway(ys[i] * 4f);
                int ix = FloorDiv(qx, 4), iy = FloorDiv(qy, 4);
                List<PathFigure> figs = TransformedOutline(font, gids[i], em, m11, m12, m21, m22, (qx - 4 * ix) / 4f, (qy - 4 * iy) / 4f);
                float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
                foreach (PathFigure f in figs)
                {
                    Take(f.Start);
                    foreach (PathSegment sg in f.Segments)
                        if (sg is LineSegment l) Take(l.Point);
                        else if (sg is QuadraticBezierSegment qb) { Take(qb.Control); Take(qb.Point); }
                }
                void Take(System.Numerics.Vector2 v) { if (v.X < x0) x0 = v.X; if (v.X > x1) x1 = v.X; if (v.Y < y0) y0 = v.Y; if (v.Y > y1) y1 = v.Y; }
                if (x0 > x1) continue;
                int ox = (int)MathF.Floor(x0) - 1, oy = (int)MathF.Floor(y0) - 1;
                int w = (int)MathF.Ceiling(x1) + 1 - ox, h = (int)MathF.Ceiling(y1) + 1 - oy;
                bool[]? bits = PathRasterizer.ScanGlyphBits(new PathGeometry(FillRule.NonZero, figs), ox, oy, w, h, 4, dropout, 4);
                if (bits is null) continue;
                var c = new int[w * h];
                for (int r = 0; r < h * 4; r++)
                    for (int col = 0; col < w * 4; col++)
                        if (bits[r * w * 4 + col]) c[(r >> 2) * w + (col >> 2)]++;
                for (int r = 0; r < h; r++)
                    for (int col = 0; col < w; col++)
                    {
                        int v = Math.Min(15, c[r * w + col]);
                        if (v == 0) continue;
                        var key = (ix + ox + col, iy + oy + r);
                        if (!cov.TryGetValue(key, out int old) || v > old) cov[key] = v;
                    }
            }
            var lv = new Levels { Grey = true };
            if (cov.Count == 0) return lv;
            int p0 = int.MaxValue, p1 = int.MinValue, r0 = int.MaxValue, r1 = int.MinValue;
            foreach (var key in cov.Keys)
            {
                p0 = Math.Min(p0, key.Item1); p1 = Math.Max(p1, key.Item1);
                r0 = Math.Min(r0, key.Item2); r1 = Math.Max(r1, key.Item2);
            }
            lv.Left = p0; lv.Top = r0; lv.Width = p1 - p0 + 1; lv.Height = r1 - r0 + 1;
            lv.Index = new byte[lv.Width * lv.Height];
            foreach (var kv in cov) lv.Index[(kv.Key.Item2 - r0) * lv.Width + kv.Key.Item1 - p0] = (byte)kv.Value;
            return lv;
        }
    }
}
