// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// EMR_EXTTEXTOUTW played through GDI into GDI+'s playback DIB (GpGraphics::EnumEmf's DIB branch, see
// GpGdiPlayer.Raster.cs): GDI's own text, drawn by this port's GDI text pieces rather than through
// Graphics.DrawString.
//
//   the font      the record's LOGFONT realized as GDI realizes it (GpGdiFont: the face, tmAscent /
//                 tmDescent); MREXTTEXTOUT::bPlay (gdi32full @18006b7e0) plays a GM_COMPATIBLE record
//                 under SetFontXform(exScale, eyScale), so the glyph is sized by the playback's
//                 mapping axis by axis -- its x by the device x scale (truncated to a whole ppem),
//                 its y by the device y scale (rounded) -- and hinted at those two ppems
//   the glyphs    ClearType: the DIB is a 32bpp surface and the record's quality the default, so
//                 GDI draws its ClearType text (the compatible-width fit, scaler word 3, scanned 6x1,
//                 the run composed and filtered as GdiPlusText.Compose does -- the same
//                 fsc_OverscaleToSubPixel / ulClearTypeFilter / level sums) and blends each lamp
//                 into the DIB's pixel with win32k's memory-DC arithmetic (vClearTypeLookupTableLoop:
//                 out = B[A[d] + ((W[k] (A[ink] - A[d]) + 2^19) >> 20)], gamma 1.2). The paper is
//                 the DIB's 0xAA0D0B0C, so every pixel a glyph's filter reaches turns opaque and dark
//                 -- the heavy look GDI+'s played text has
//   the pens      ESTROBJ::vCharPos_H1 @1401aed88 / vCharPos_G1 @1401adf80: the reference point in
//                 28.4, each glyph's pen the advances summed through the mapping, rounded in 28.4,
//                 and the glyph put down at its pen's whole pixel (+8 >> 4)
//   alignment     TA_BASELINE / TA_TOP / TA_BOTTOM across, TA_LEFT / TA_CENTER / TA_RIGHT along
//   escapement    a quarter turn: the glyph's levels made in its own frame and laid down turned,
//                 one level for all three lamps (what the recorded fixtures show); other angles,
//                 and a mapping that turns, are not drawn here (the old path draws them)
//   ink           through the DC's clip and ETO_CLIPPED; the background (OPAQUE mode or ETO_OPAQUE)
//                 the text box in the background colour
//
// NOT EXACT: the ppem rounding per axis is read off the recorded pixels, not out of win32k; a
// vertical ('@') face's glyphs are not moved onto the vertical baseline GDI centres them on.
//

using System.Drawing.Imaging;
using Microsoft.Wpf.Interop.WebGpu.Composition.Text;

namespace System.Drawing.WebGpuBackend.Gdip
{
    internal sealed partial class GpGdiPlayer
    {
        /// <summary>The text record into the playback DIB; false where it is not drawn here.</summary>
        bool CanvasText(string s, PointF logical, int options, RectangleF? clipRect, int[] dx, bool glyphIndex)
        {
            GdiFont lf = _dc.Font;
            if (lf == null) return false;
            GpGdiFont font = GpGdiFont.Get(lf.Face, lf.Height, lf.Escapement, lf.Weight, lf.Italic, lf.Underline, lf.StrikeOut, lf.Quality);
            if (font == null) return false;
            GpMat m = LogicalToTarget;
            if (m.M12 != 0f || m.M21 != 0f) return false;
            int q = ((font.Escapement / 900) % 4 + 4) % 4;
            if (font.Escapement % 900 != 0) return false;
            int n = s.Length;
            if (n == 0) return true;
            float sx = Math.Abs(m.M11), sy = Math.Abs(m.M22);
            // The em through the mapping (GM_COMPATIBLE: the record's font xform, SetFontXform), each
            // axis of the glyph its own: x by the device x scale, truncated; y by the device y scale,
            // rounded. A turned font is turned after.
            float em = font.Ppem;
            int ppemAlong = (int)(em * sx);
            int ppemAcross = GdiPlusText.AxisPpem(em * sy);
            if (ppemAlong < 1 || ppemAcross < 1) return true;
            float along = q % 2 == 0 ? sx : sy;     // device pixels per logical unit along the baseline
            // Reference point in 28.4; alignment.
            int align = _dc.TextAlign;
            bool updateCp = (align & 1) != 0;
            PointF refp = ToTarget(updateCp ? _dc.Pos : logical);
            long fx = (long)Math.Floor(refp.X * 16.0 + 0.5), fy = (long)Math.Floor(refp.Y * 16.0 + 0.5);
            // Unit vectors along the baseline and down (device).
            int ax = q == 0 ? 1 : q == 2 ? -1 : 0, ay = q == 1 ? -1 : q == 3 ? 1 : 0;
            int dnx = q == 1 ? 1 : q == 3 ? -1 : 0, dny = q == 0 ? 1 : q == 2 ? -1 : 0;
            if (m.M11 < 0f) { ax = -ax; dnx = -dnx; }
            if (m.M22 < 0f) { ay = -ay; dny = -dny; }
            float scaleAcross = q % 2 == 0 ? sy : sx;
            int asc = (int)Math.Round(font.Ascent * scaleAcross), desc = (int)Math.Round(font.Descent * scaleAcross);
            var pens = new long[n + 1];
            long sum = 0;
            for (int i = 0; i < n; i++)
            {
                pens[i] = (long)Math.Floor(sum * along * 16.0 + 0.5);
                int adv = dx != null && i < dx.Length ? dx[i] : font.CharAdvance(s[i]);
                sum += adv;
            }
            pens[n] = (long)Math.Floor(sum * along * 16.0 + 0.5);
            long total = pens[n];
            int va = align & 0x18;
            if (va == 0) { fx += (long)asc * 16 * dnx; fy += (long)asc * 16 * dny; }
            else if (va == 8) { fx -= (long)desc * 16 * dnx; fy -= (long)desc * 16 * dny; }
            int ha = align & 6;
            if (ha == 6) { fx -= total / 2 * ax; fy -= total / 2 * ay; }
            else if (ha == 2) { fx -= total * ax; fy -= total * ay; }

            bool[] mask = ClipMask();
            Rectangle? etoClip = null;
            if (clipRect.HasValue && (options & 6) != 0)
            {
                RectangleF c = clipRect.Value;
                PointF a = ToTarget(new PointF(c.Left, c.Top)), b = ToTarget(new PointF(c.Right, c.Bottom));
                int l = Round(Math.Min(a.X, b.X)), t = Round(Math.Min(a.Y, b.Y)), r = Round(Math.Max(a.X, b.X)), bt = Round(Math.Max(a.Y, b.Y));
                etoClip = Rectangle.FromLTRB(l, t, r, bt);
            }
            BitmapData bd = _canvas.LockBits(new Rectangle(0, 0, _cw, _ch), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
            try
            {
                var px = new int[_cw * _ch];
                System.Runtime.InteropServices.Marshal.Copy(bd.Scan0, px, 0, px.Length);
                bool Visible(int x, int y)
                    => x >= 0 && y >= 0 && x < _cw && y < _ch && (mask == null || mask[y * _cw + x])
                       && (!etoClip.HasValue || (options & 4) == 0 || etoClip.Value.Contains(x, y));
                // The background: ETO_OPAQUE's rectangle, or in OPAQUE mode the text's box.
                int bk = unchecked((int)(0xff000000u | Rgb(_dc.BkColor)));
                if (etoClip.HasValue && (options & 2) != 0)
                {
                    Rectangle e = etoClip.Value;
                    for (int y = e.Top; y < e.Bottom; y++)
                        for (int x = e.Left; x < e.Right; x++)
                            if (x >= 0 && y >= 0 && x < _cw && y < _ch && (mask == null || mask[y * _cw + x])) px[y * _cw + x] = bk;
                }
                else if (_dc.BkMode == 2)
                {
                    int x0 = (int)((fx + 8) >> 4), y0 = (int)((fy + 8) >> 4);
                    int tot = (int)((total + 8) >> 4);
                    int l = Math.Min(x0 + 0, x0 + ax * tot) + Math.Min(-asc * dnx, desc * dnx);
                    int r = Math.Max(x0, x0 + ax * tot) + Math.Max(-asc * dnx, desc * dnx);
                    int t = Math.Min(y0, y0 + ay * tot) + Math.Min(-asc * dny, desc * dny);
                    int b = Math.Max(y0, y0 + ay * tot) + Math.Max(-asc * dny, desc * dny);
                    if (dnx == 0) { r = Math.Max(r, l); }
                    for (int y = t; y < b; y++)
                        for (int x = l; x < r; x++)
                            if (Visible(x, y)) px[y * _cw + x] = bk;
                }
                if (q == 0)
                {
                    // ClearType: GDI's fit (the compatible-width word) scanned 6x1, the run composed
                    // and filtered (fsc_OverscaleToSubPixel, ulClearTypeFilter, the level sums), each
                    // lamp blended into the DIB's pixel by win32k's memory-DC arithmetic
                    // (vClearTypeLookupTableLoop): out = B[A[d] + ((W[k] (A[ink] - A[d]) + 2^19) >> 20)].
                    var bits = new System.Collections.Generic.List<NaturalClearType.GlyphBits>(n);
                    var xs = new float[n];
                    var ys = new float[n];
                    for (int i = 0; i < n; i++)
                    {
                        int gid = glyphIndex ? s[i] : font.Face.GlyphIndex(s[i]);
                        bits.Add(GdiClearTypeGlyph(font.Face, gid, ppemAlong, ppemAcross));
                        long gx = fx + pens[i] * ax, gy = fy + pens[i] * ay;
                        xs[i] = (int)((gx + 8) >> 4);
                        ys[i] = (int)((gy + 8) >> 4);
                    }
                    GdiPlusText.Levels lv = GdiPlusText.Compose(bits, xs, ys, 0f, font.Face.GdiContrastPalette);
                    (byte[] A, byte[] B) = CtGamma();
                    uint inkRgb = Rgb(_dc.TextColor);
                    int ir = (int)(inkRgb >> 16) & 255, ig = (int)(inkRgb >> 8) & 255, ib = (int)inkRgb & 255;
                    for (int r = 0; r < lv.Height; r++)
                        for (int c = 0; c < lv.Width; c++)
                        {
                            int idx = lv.Index[r * lv.Width + c];
                            if (idx == 0) continue;
                            int x = lv.Left + c, y = lv.Top + r;
                            if (!Visible(x, y)) continue;
                            (int kr, int kg, int kb) = GdiPlusText.LevelsOf(idx);
                            if (kr == 0 && kg == 0 && kb == 0) continue;
                            uint d = (uint)px[y * _cw + x];
                            int dr = (int)(d >> 16) & 255, dg = (int)(d >> 8) & 255, db = (int)d & 255;
                            int Lamp(int k, int fg, int bg) => k >= 6 ? fg : B[Math.Clamp(A[bg] + ((s_lampWeight[k] * (A[fg] - A[bg]) + 0x80000) >> 20), 0, 255)];
                            px[y * _cw + x] = unchecked((int)(0xff000000u | (uint)Lamp(kr, ir, dr) << 16 | (uint)Lamp(kg, ig, dg) << 8 | (uint)Lamp(kb, ib, db)));
                        }
                }
                else
                {
                    // A quarter turn: the glyph's ClearType levels made in its own frame, laid down
                    // turned, one level for all three lamps (the filter ran along the glyph's x,
                    // which is no longer the device's lamp axis).
                    (byte[] A, byte[] B) = CtGamma();
                    uint inkRgb = Rgb(_dc.TextColor);
                    int ir = (int)(inkRgb >> 16) & 255, ig = (int)(inkRgb >> 8) & 255, ib = (int)inkRgb & 255;
                    int Lamp(int k, int fg, int bg) => k >= 6 ? fg : B[Math.Clamp(A[bg] + ((s_lampWeight[k] * (A[fg] - A[bg]) + 0x80000) >> 20), 0, 255)];
                    for (int i = 0; i < n; i++)
                    {
                        int gid = glyphIndex ? s[i] : font.Face.GlyphIndex(s[i]);
                        var gb = GdiClearTypeGlyph(font.Face, gid, ppemAlong, ppemAcross);
                        if (gb.IsEmpty) continue;
                        GdiPlusText.Levels lv = GdiPlusText.Compose(new[] { gb }, new[] { 0f }, null, 0f, font.Face.GdiContrastPalette);
                        long gx = fx + pens[i] * ax, gy = fy + pens[i] * ay;
                        int ox = (int)((gx + 8) >> 4), oy = (int)((gy + 8) >> 4);
                        for (int r = 0; r < lv.Height; r++)
                            for (int c = 0; c < lv.Width; c++)
                            {
                                int idx = lv.Index[r * lv.Width + c];
                                if (idx == 0) continue;
                                (int kr, int kg, int kb) = GdiPlusText.LevelsOf(idx);
                                int k = (kr + kg + kb + 1) / 3;
                                if (k == 0) continue;
                                // Glyph pixel (u along, v down) to the device.
                                int u = lv.Left + c, v = lv.Top + r;
                                int x, y;
                                switch (q)
                                {
                                    case 1: x = ox + v * dnx; y = oy + u * ay - 1; break;
                                    case 2: x = ox + u * ax - 1; y = oy + v * dny - 1; break;
                                    default: x = ox + v * dnx - 1; y = oy + u * ay; break;
                                }
                                if (!Visible(x, y)) continue;
                                uint d = (uint)px[y * _cw + x];
                                int dr = (int)(d >> 16) & 255, dg = (int)(d >> 8) & 255, db = (int)d & 255;
                                px[y * _cw + x] = unchecked((int)(0xff000000u | (uint)Lamp(k, ir, dr) << 16 | (uint)Lamp(k, ig, dg) << 8 | (uint)Lamp(k, ib, db)));
                            }
                    }
                }
                System.Runtime.InteropServices.Marshal.Copy(px, 0, bd.Scan0, px.Length);
            }
            finally { _canvas.UnlockBits(bd); }
            if (updateCp)
                _dc.Pos = new PointF(_dc.Pos.X + sum, _dc.Pos.Y);
            return true;
        }
        static readonly int[] s_lampWeight = { 0, 174763, 349525, 524288, 699051, 873813, 1048576 };
        static (byte[] A, byte[] B)? s_ctGamma;

        /// <summary>EngCTGetGammaTable's A and B for the ClearType contrast (1200, Windows' default:
        /// A = round(255 x^1.2), B = round(255 x^(1/1.2))).</summary>
        static (byte[] A, byte[] B) CtGamma()
        {
            if (s_ctGamma is { } t) return t;
            const float g = 1.2f;
            var a = new byte[256]; var b = new byte[256];
            for (int i = 0; i < 256; i++)
            {
                a[i] = (byte)MathF.Floor(255f * MathF.Pow(i / 255f, g) + 0.5f);
                b[i] = (byte)MathF.Floor(255f * MathF.Pow(i / 255f, 1f / g) + 0.5f);
            }
            return (s_ctGamma = (a, b)).Value;
        }

        /// <summary>GDI's ClearType glyph: the compatible-width fit (scaler word 3) at the whole ppem of
        /// each axis, scanned at six samples a pixel.</summary>
        static NaturalClearType.GlyphBits GdiClearTypeGlyph(TrueTypeFont face, int gid, int ppemX, int ppemY)
        {
            int sx = TrueTypeInterpreter.StretchPpemX, sy = TrueTypeInterpreter.StretchPpemY;
            TrueTypeInterpreter.StretchPpemX = ppemX == ppemY ? 0 : ppemX;
            TrueTypeInterpreter.StretchPpemY = ppemX == ppemY ? 0 : ppemY;
            try { return NaturalClearType.Rasterize(face, gid, Math.Max(ppemX, ppemY), 1, gridFit: true, scalerFlags: 3, forceGridFit: true); }
            finally { TrueTypeInterpreter.StretchPpemX = sx; TrueTypeInterpreter.StretchPpemY = sy; }
        }
    }
}
