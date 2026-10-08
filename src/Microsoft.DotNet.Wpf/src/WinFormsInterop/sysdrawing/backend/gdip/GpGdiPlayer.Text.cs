// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// EMR_EXTTEXTOUTW played through GDI into GDI+'s playback DIB (GpGraphics::EnumEmf's DIB branch, see
// GpGdiPlayer.Raster.cs): GDI's own text, drawn by this port's GDI text pieces rather than through
// Graphics.DrawString.
//
//   the font      the record's LOGFONT realized as GDI realizes it (GpGdiFont: the face, tmAscent /
//                 tmDescent); MREXTTEXTOUT::bPlay (gdi32full @18006b7e0) plays a GM_COMPATIBLE record
//                 under SetFontXform(exScale, eyScale), and the ppem of each axis comes out of
//                 win32k's notional-to-device transform and ttfd's quantization (RealizedPpems)
//   the glyphs    ClearType: the DIB is a 32bpp surface and the record's quality the default, so
//                 GDI draws its ClearType text -- the same fit, scan, filter, dropout, smear,
//                 line-box clip and level sums the WinForms text is drawn with (GdiClearTypeRun:
//                 TrueTypeFont's GDI fit, PathRasterizer.RasterizeSubpixel) -- and blends each lamp
//                 into the DIB's pixel with win32k's memory-DC arithmetic (vClearTypeLookupTableLoop:
//                 out = B[A[d] + ((W[k] (A[ink] - A[d]) + 2^19) >> 20)], gamma 1.2). The paper is
//                 the DIB's 0xAA0D0B0C, so every pixel a glyph's filter reaches turns opaque and dark
//                 -- the heavy look GDI+'s played text has
//   the pens      ESTROBJ::vCharPos_H1 @1401aed88 / vCharPos_G1 @1401adf80: the reference point in
//                 28.4, each glyph's pen the advances summed through the mapping, rounded in 28.4,
//                 and the glyph put down at its pen's whole pixel (+8 >> 4)
//   alignment     TA_BASELINE / TA_TOP / TA_BOTTOM across, TA_LEFT / TA_CENTER / TA_RIGHT along
//   escapement    a quarter turn: the glyph fitted at its own two ppems, its outline turned onto
//                 the device and scanned and filtered there like any other; a vertical ('@')
//                 face's full-width glyphs take their 'vert' form, stand upright and are moved by
//                 vCalcXformVertical's shift. Any other angle (and a simulated italic at any
//                 angle but 0, whose slant bSetXform puts in the matrix) takes ttfd's general
//                 rotation (GeneralRealization): fitted at the folded rows' ppems, turned by
//                 scl_PostTransformGlyph, implied midpoints made after the turn, the 45-degree
//                 trick's one-unit shift. A mapping that turns is not drawn here (the old path)
//   ink           through the DC's clip and ETO_CLIPPED; the background (OPAQUE mode or ETO_OPAQUE)
//                 the text box in the background colour
//
// NOT EXACT: a simulated italic at exactly 180 degrees (m01 = 0 keeps compatible widths: a few
// pixels; the harness has no two-pass oracle for it), an upright simulated italic above ~30ppem
// (the scale-only path, not the slanted matrix); which glyphs ttfd counts full-width (its per-glyph bit set is not modelled) and the
// signs of the vertical shift (read off the fixture's ellipsis); a positive lfHeight's VDMX search.
//

using System.Drawing.Imaging;
using Microsoft.Wpf.Interop.WebGpu.Composition;
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
            GpGdiFont font = GpGdiFont.Get(lf.Face, lf.Height, lf.Escapement, lf.Weight, lf.Italic, lf.Underline, lf.StrikeOut, lf.Quality, lf.CharSet, lf.PitchAndFamily, lf.Width);
            if (font == null) return false;
            GpMat m = LogicalToTarget;
            if (m.M12 != 0f || m.M21 != 0f) return false;
            int q = ((font.Escapement / 900) % 4 + 4) % 4;
            int n = s.Length;
            if (n == 0) return true;
            float sx = Math.Abs(m.M11), sy = Math.Abs(m.M22);
            // The em through the mapping: GDI's realization (RealizedPpems), each axis of the glyph
            // at its own whole ppem.
            float em = font.Ppem;
            GdiXform wtod = TargetWtoD();
            GeneralFit gen = null;
            int rx, ry; bool rStretch;
            // A simulated italic turned by any angle is not a quarter turn to the scaler: bSetXform
            // adds the slant to the glyph's y row, so even 90 and 180 degrees take the general path.
            bool simItalic = font.UprightFace != null && font.Escapement % 3600 != 0;
            if (font.Escapement % 900 != 0 || simItalic)
            {
                // Any other angle: ttfd's general rotation (GeneralRealization).
                if (m.M11 <= 0f || m.M22 <= 0f) return false;
                gen = GeneralRealization(font, lf, wtod.M11, wtod.M22, font.Escapement, simItalic);
                if (gen == null) return true;
                (rx, ry, rStretch, q) = (gen.PpemX, gen.PpemY, gen.Stretched, 0);
            }
            else (rx, ry, rStretch) = RealizedPpems(font, lf, wtod.M11, wtod.M22, q == 0 && wtod.M22 > 0, q % 2 == 1);
            // bGetNtoD_Win31 turns the font AFTER the world-to-device scale (notional to world, times
            // world to device, times the escapement), so a turned glyph is still sized x by the
            // device x scale and y by the device y scale.
            int ppemAlong = rx, ppemAcross = ry;
            if (ppemAlong < 1 || ppemAcross < 1) return true;
            float along = q % 2 == 0 ? sx : sy;     // device pixels per logical unit along the baseline
            if (gen != null)
            {
                // RFONT +0x190 (bCalcLayoutUnits): along the base, the world-to-device length.
                float wl = MathF.Sqrt(gen.UbX / sx * (gen.UbX / sx) + gen.UbY / sy * (gen.UbY / sy));
                along = 1f / wl;
            }
            // Reference point in 28.4; alignment.
            int align = _dc.TextAlign;
            bool updateCp = (align & 1) != 0;
            PointF refp = ToTarget(updateCp ? _dc.Pos : logical);
            long fx = (long)Math.Floor(refp.X * 16.0 + 0.5), fy = (long)Math.Floor(refp.Y * 16.0 + 0.5);
            // The reference point through the DC's own 28.4 world-to-device transform (bCvtPts),
            // as GDI's ExtTextOut takes it, where the record's point is whole.
            PointF rl = updateCp ? _dc.Pos : logical;
            if (rl.X == MathF.Floor(rl.X) && rl.Y == MathF.Floor(rl.Y))
            {
                Fix(wtod, rl.X, rl.Y, out int rfx, out int rfy);
                fx = rfx; fy = rfy;
            }
            // Unit vectors along the baseline and down (device).
            int ax = q == 0 ? 1 : q == 2 ? -1 : 0, ay = q == 1 ? -1 : q == 3 ? 1 : 0;
            int dnx = q == 1 ? 1 : q == 3 ? -1 : 0, dny = q == 0 ? 1 : q == 2 ? -1 : 0;
            if (m.M11 < 0f) { ax = -ax; dnx = -dnx; }
            if (m.M22 < 0f) { ay = -ay; dny = -dny; }
            float scaleAcross = q % 2 == 0 ? sy : sx;
            int asc = (int)Math.Round(font.Ascent * scaleAcross), desc = (int)Math.Round(font.Descent * scaleAcross);
            if (gen == null && q == 0 && (scaleAcross != 1f || ppemAcross != font.Ppem))
            {
                // The device font's own tmAscent / tmDescent (the realization at the device ppem),
                // not the logical font's scaled.
                TrueTypeFont ft = font.Face;
                if (!ft.TryGetGdiLineMetrics(ppemAcross, out asc, out desc))
                {
                    asc = (int)Math.Round(ft.WinAscent * (double)ppemAcross / ft.UnitsPerEmForHinting);
                    desc = (int)Math.Round(ft.WinDescent * (double)ppemAcross / ft.UnitsPerEmForHinting);
                }
            }
            var pens = new long[n + 1];
            long sum = 0;
            if (dx == null && gen == null && q == 0)
            {
                // No advances: ESTROBJ sums the realized glyphs' own device advances (GLYPHDATA
                // fxD, whole pixels), and SetTextCharacterExtra's extra through the mapping.
                long dev = 0;
                int extra = _charExtra == 0 ? 0 : (int)Math.Floor(_charExtra * along + 0.5);
                for (int i = 0; i < n; i++)
                {
                    pens[i] = dev * 16;
                    int gid = glyphIndex ? s[i] : font.Face.GlyphIndex(s[i]);
                    dev += font.DeviceAdvance(gid, ppemAlong, ppemAcross, _rM00, _rSquare, rStretch) + extra;
                }
                pens[n] = dev * 16;
                sum = (long)Math.Floor(dev / along + 0.5);
            }
            else
            {
                for (int i = 0; i < n; i++)
                {
                    pens[i] = (long)Math.Floor(sum * along * 16.0 + 0.5);
                    int adv = dx != null && i < dx.Length ? dx[i] : font.CharAdvance(s[i]) + _charExtra;
                    sum += adv;
                }
                pens[n] = (long)Math.Floor(sum * along * 16.0 + 0.5);
            }
            long total = pens[n];
            int va = align & 0x18;
            if (va == 0 && gen == null) { fx += (long)asc * 16 * dnx; fy += (long)asc * 16 * dny; }
            else if (va == 8 && gen == null) { fx -= (long)desc * 16 * dnx; fy -= (long)desc * 16 * dny; }
            int ha = align & 6;
            // ESTROBJ::vInit @1401afcc0 after vCharPos_H1 (an unturned, unmirrored scale): the glyphs
            // already sit on whole pixels, and TA_CENTER / TA_RIGHT move them all back by whole
            // pixels, (total / 2 + 8) >> 4 or (total + 8) >> 4 (C's division); the turned paths
            // (vCharPos_G*) move the 28.4 reference point instead.
            bool hPath = gen == null && q == 0 && ax == 1 && dny == 1;
            int hShift = 0;
            if (hPath && ha == 6) hShift = (int)((total / 2 + 8) >> 4);
            else if (hPath && ha == 2) hShift = (int)((total + 8) >> 4);
            else if (ha == 6 && gen == null) { fx -= total / 2 * ax; fy -= total / 2 * ay; }
            else if (ha == 2 && gen == null) { fx -= total * ax; fy -= total * ay; }
            if (gen != null)
            {
                // ESTROBJ::vInit at an angle: the reference point moved along the unit vectors, each
                // product rounded in 28.4.
                if (va == 0) { fx -= R16((long)asc * 16 * gen.UaX); fy -= R16((long)asc * 16 * gen.UaY); }
                else if (va == 8) { fx += R16((long)desc * 16 * gen.UaX); fy += R16((long)desc * 16 * gen.UaY); }
                if (ha == 6) { fx -= R16(total / 2 * gen.UbX); fy -= R16(total / 2 * gen.UbY); }
                else if (ha == 2) { fx -= R16(total * gen.UbX); fy -= R16(total * gen.UbY); }
            }

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
                else if (_dc.BkMode == 2 && gen == null && q == 0 && ax == 1 && ay == 0 && dny == 1 && ppemAlong == ppemAcross)
                {
                    // ESTROBJ::vCharPos_H1 @1401aed88 (its glyph-data branch): along the baseline the
                    // least of 0 and each pen plus the glyph's GLYPHDATA fxA, to the greatest of the
                    // total advance and each pen plus fxAB -- the columns the glyph's bitmap lights;
                    // ESTROBJ::bOpaqueArea @1401ad4f0's horizontal box at the reference pixel
                    // (+8 >> 4), that left floored and right ceiled; and GrepExtTextOutWLocked
                    // @1401a8a98 widens a ClearType font's box by a pixel each side (rfont +0xc bit 28).
                    long xl = 0, xr = 0;
                    for (int i = 0; i < n; i++)
                    {
                        int gid = glyphIndex ? s[i] : font.Face.GlyphIndex(s[i]);
                        GdiPlusText.GreyGlyph g = GdiPlusText.Mono(font.Face, gid, ppemAlong, gridFit: true);
                        if (g.Width <= 0 || g.Height <= 0) continue;
                        xl = Math.Min(xl, pens[i] + g.Left * 16);
                        xr = Math.Max(xr, pens[i] + (g.Left + g.Width) * 16);
                    }
                    xr = Math.Max(xr, total);
                    int x0 = (int)((fx + 8) >> 4) - hShift, y0 = (int)((fy + 8) >> 4);
                    int l = x0 + (int)(xl >> 4) - 1, r = x0 + (int)((xr + 15) >> 4) + 1;
                    int t = y0 - asc, b = y0 + desc;
                    for (int y = t; y < b; y++)
                        for (int x = l; x < r; x++)
                            if (Visible(x, y)) px[y * _cw + x] = bk;
                }
                else if (_dc.BkMode == 2 && gen == null)
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
                {
                    // ClearType: GDI's fit and scan (GdiClearTypeRun), each lamp blended into the
                    // DIB's pixel by win32k's memory-DC arithmetic (vClearTypeLookupTableLoop):
                    // out = B[A[d] + ((W[k] (A[ink] - A[d]) + 2^19) >> 20)].
                    var gids = new int[n];
                    var xs = new int[n];
                    var ys = new int[n];
                    for (int i = 0; i < n; i++)
                    {
                        gids[i] = glyphIndex ? s[i] : font.Face.GlyphIndex(s[i]);
                        long gx = gen != null ? fx + R16(pens[i] * gen.UbX) : fx + pens[i] * ax, gy = gen != null ? fy + R16(pens[i] * gen.UbY) : fy + pens[i] * ay;
                        xs[i] = (int)((gx + 8) >> 4) - hShift;
                        ys[i] = (int)((gy + 8) >> 4);
                    }
                    bool[] upright = null;
                    if (font.Vertical && q % 2 == 1)
                    {
                        TrueTypeFont face = font.Face;
                        int upem = face.UnitsPerEmForHinting;
                        static int Px(long v16) => (int)(((v16 >> 15) + 1) >> 1);
                        for (int i = 0; i < n; i++)
                        {
                            if (!IsFullWidth(face, gids[i])) continue;
                            (upright ??= new bool[n])[i] = true;
                            gids[i] = VerticalForm(face, gids[i]);
                            // vShiftBitmapInfo @14008a2f0: the bitmap moved by vCalcXformVertical's
                            // shift, the typo descender through the glyph's y and the typo ascender
                            // through its x.
                            long d16 = ((long)face.TypoDescender << 16) * ppemAcross / upem;
                            long a16 = ((long)face.TypoAscender << 16) * ppemAlong / upem;
                            // The descender moves it against the down axis, the ascender along the
                            // baseline (signs as the fixture's ellipsis shows them).
                            int sd = -Px(d16), sa = Px(a16);
                            xs[i] += sd * dnx + sa * ax;
                            ys[i] += sd * dny + sa * ay;
                        }
                    }
                    // bRealizeFont gives the contrast palette only to a font whose ascender is upright:
                    // RFONT +0x140, fxMaxAscender times the unit ascender's x, must round to zero.
                    bool contrastOk = gen == null ? q % 2 == 0 : Math.Round(font.Ascent * 16.0 * gen.UaX) == 0;
                    CtLevels lv = GdiClearTypeRun(gen != null && gen.Slanted ? font.UprightFace : font.Face, gids, xs, ys, ppemAlong, ppemAcross, gen != null ? -1 : q, (ax, ay, dnx, dny), upright, gen != null ? gen.Stretched : rStretch, gen, contrastOk);
                    (byte[] A, byte[] B) = CtGamma();
                    uint inkRgb = Rgb(_dc.TextColor);
                    int ir = (int)(inkRgb >> 16) & 255, ig = (int)(inkRgb >> 8) & 255, ib = (int)inkRgb & 255;
                    if (lv != null)
                    for (int r = 0; r < lv.Height; r++)
                        for (int c = 0; c < lv.Width; c++)
                        {
                            int p = (r * lv.Width + c) * 3;
                            int kr = lv.Lvl[p], kg = lv.Lvl[p + 1], kb = lv.Lvl[p + 2];
                            if (kr == 0 && kg == 0 && kb == 0) continue;
                            int x = lv.Left + c, y = lv.Top + r;
                            if (!Visible(x, y)) continue;
                            uint d = (uint)px[y * _cw + x];
                            int dr = (int)(d >> 16) & 255, dg = (int)(d >> 8) & 255, db = (int)d & 255;
                            int Lamp(int k, int fg, int bg) => k >= 6 ? fg : B[Math.Clamp(A[bg] + ((s_lampWeight[k] * (A[fg] - A[bg]) + 0x80000) >> 20), 0, 255)];
                            px[y * _cw + x] = unchecked((int)(0xff000000u | (uint)Lamp(kr, ir, dr) << 16 | (uint)Lamp(kg, ig, dg) << 8 | (uint)Lamp(kb, ib, db)));
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

        /// <summary>How ttfd realizes a font turned by an angle that is not a quarter: the sizes it
        /// fits the glyph at, what GETINFO says of the matrix, and the matrix that turns the fit
        /// onto the device (scl_PostTransformGlyph).</summary>
        sealed class GeneralFit
        {
            public int PpemX, PpemY;            // scl_InitializeScaling's rounded row scales
            public int EmPpem;                  // the context's own ppem (+0x7c): what the gasp is read at
            public bool Rotated, Stretched;     // globals[0x169] bits 0 and 1
            public bool Slanted;                // FO_SIM_ITALIC's shear is in the matrix
            public bool Phase45;                // mth_Max45Trick: a row of the folded matrix within 0x22 of 45 degrees
            public bool Axial;                  // the context's matrix (unslanted) is diagonal or a quarter turn (TT_FONTCONTEXT +0x74 bits 0, 1)
            public bool M00Zero, M01Zero;       // what fs__NewTransformation reads off the matrix for the word
            public int P00, P01, P10, P11;      // the post-transform, 16.16, glyph y-up to device y-up
            public float UbX, UbY;              // the unit base vector on the screen (y down)
            public float UaX, UaY;              // the unit ascender on the screen
        }

        /// <summary>The realization of a font turned by <paramref name="esc"/> tenths of a degree,
        /// for a mapping that scales x and y by m11 and m22:
        /// <list type="bullet">
        /// <item>bGetNtoD_Win31 @1401e7458: the notional-to-device scales of each axis (as for a
        /// quarter turn, see RealizedPpems), then the escapement's rotation;</item>
        /// <item>bNewXform @14001c018: each entry to 16.16 (in the scaler's y-up frame);</item>
        /// <item>bComputeMaxGlyph @14001b198: the point size iHipot of the glyph's y row;</item>
        /// <item>bSetXform @14001c2c8: every entry FixMul'd by upem 72 2^32 / (dpi pt);</item>
        /// <item>scl_InitializeScaling @140040540: mth_FoldPointSizeResolution multiplies the entries
        /// back by (dpi pt + 36) / 72; x's scale is the larger magnitude in its row and y's in its,
        /// each rounded to the whole pixel; globals[0x169] are rotated (neither m00 nor m11 zero,
        /// m01 or m10 not) and stretched (the rows' squared lengths differ), of the matrix before
        /// the fold;</item>
        /// <item>scl_PostTransformGlyph @1400955f0 -> mth_IntelMul @140026b40: the folded matrix
        /// with row 0 divided by x's UNROUNDED scale and row 1 by y's.</item>
        /// </list></summary>
        GeneralFit GeneralRealization(GpGdiFont font, GdiFont lf, float m11, float m22, int esc, bool slant = false)
        {
            TrueTypeFont face = font.Face;
            int upem = face.UnitsPerEmForHinting;
            float h;
            if (lf.Height < 0) h = (float)-lf.Height / upem;
            else if (lf.Height > 0) h = (float)lf.Height / (face.WinAscent + face.WinDescent);
            else h = (float)font.Ppem / upem;
            float wx = NotionalX(face, lf, h, m11, m22);
            float sxs = MathF.Abs(wx * m11) * 0.0625f, sys = MathF.Abs(h * m22) * 0.0625f;
            float c = GdiTrig.Cos(GdiTrig.Degrees(esc)), s = GdiTrig.Sin(GdiTrig.Degrees(esc));
            static int Fx(float v) => v < 0f ? -(int)Math.Floor(-v * 65536.0 + 0.5) : (int)Math.Floor(v * 65536.0 + 0.5);
            // y-up device: glyph x -> (c, s) sx, glyph y -> (-s, c) sy.
            int m00 = Fx(sxs * c), m01 = Fx(sxs * s), m10 = Fx(-sys * s), mm11 = Fx(sys * c);
            const int dpi = 96;
            static long RoundDiv(long a, long d) => (a + (a < 0 ? -d / 2 : d / 2)) / d;
            long ry0 = RoundDiv((long)m10 * upem * 72, dpi), ry1 = RoundDiv((long)mm11 * upem * 72, dpi);
            int pt16 = IHipot(ry0, ry1);
            if (pt16 < 1) return null;
            long num = ((long)upem << 16) * (72L << 16), den = (long)dpi * pt16;
            int k = (int)((num + den / 2) / den);
            int n00 = TrueTypeInterpreter.DwFixMul(m00, k), n01 = TrueTypeInterpreter.DwFixMul(m01, k);
            int n10 = TrueTypeInterpreter.DwFixMul(m10, k), n11 = TrueTypeInterpreter.DwFixMul(mm11, k);
            // FO_SIM_ITALIC (context flag bit 14): bSetXform leans the glyph's y row by 87/256 of its
            // x row, after the matrix is divided by the point size -- x' = x + 0x5700 y in the glyph.
            if (slant)
            {
                n10 += TrueTypeInterpreter.DwFixMul(n00, 0x5700);
                n11 += TrueTypeInterpreter.DwFixMul(n01, 0x5700);
            }
            int f = (int)(((long)dpi * pt16 + 36) / 72);
            int f00 = TrueTypeInterpreter.DwFixMul(n00, f), f01 = TrueTypeInterpreter.DwFixMul(n01, f);
            int f10 = TrueTypeInterpreter.DwFixMul(n10, f), f11 = TrueTypeInterpreter.DwFixMul(n11, f);
            int sx = Math.Max(Math.Abs(f00), Math.Abs(f01)), sy = Math.Max(Math.Abs(f10), Math.Abs(f11));
            var g = new GeneralFit { PpemX = (sx + 0x8000) >> 16, PpemY = (sy + 0x8000) >> 16, EmPpem = (f + 0x8000) >> 16,
                                     Slanted = slant, M00Zero = n00 == 0, M01Zero = n01 == 0,
                                     Axial = (m01 == 0 && m10 == 0) || (m00 == 0 && mm11 == 0) };
            if (g.PpemX < 1 || g.PpemY < 1) return null;
            // mth_Max45Trick @14008e418 over clientRec+0x178, the folded matrix: | |a| - |b| | < 0x22.
            static bool Max45(int a, int b) => Math.Abs(Math.Abs(a) - Math.Abs(b)) < 0x22;
            g.Phase45 = Max45(f00, f01) || Max45(f10, f11);
            // globals[0x169], from the matrix before the fold.
            if (TrueTypeInterpreter.DwFixMul(n10, n00) + TrueTypeInterpreter.DwFixMul(n11, n01) == 0)
            {
                g.Rotated = !(n00 == 0 && n11 == 0) && (n01 != 0 || n10 != 0);
                g.Stretched = TrueTypeInterpreter.DwFixMul(n01, n01) + TrueTypeInterpreter.DwFixMul(n00, n00)
                              != TrueTypeInterpreter.DwFixMul(n11, n11) + TrueTypeInterpreter.DwFixMul(n10, n10);
            }
            else { g.Rotated = true; g.Stretched = true; }
            // mth_IntelMul: row 0 over x's unrounded scale (DWRITE_FixDiv), row 1 over y's.
            static int DivRound(int a, int d) => (int)(((long)a * 0x10000 + ((a < 0) != (d < 0) ? -(d / 2) : d / 2)) / d);
            g.P00 = sx == 0x10000 ? f00 : DivRound(f00, sx); g.P01 = sx == 0x10000 ? f01 : DivRound(f01, sx);
            g.P10 = sy == 0x10000 ? f10 : DivRound(f10, sy); g.P11 = sy == 0x10000 ? f11 : DivRound(f11, sy);
            // ttfd's unit vectors (bComputeMaxGlyph: the base 16 (M11, M12), the ascender -16
            // (M21, M22), each over its length), on the screen.
            float bx = sxs * c * 16f, by = -sxs * s * 16f, bl = MathF.Sqrt(bx * bx + by * by);
            float ax = -sys * s * 16f, ay = -sys * c * 16f, al = MathF.Sqrt(ax * ax + ay * ay);
            g.UbX = bx / bl; g.UbY = by / bl; g.UaX = ax / al; g.UaY = ay / al;
            return g;
        }

        /// <summary>iHipot @14001c800 (fontdrvhost): an integer hypotenuse.</summary>
        static int IHipot(long a, long b)
        {
            uint x = (uint)Math.Abs(a), y = (uint)Math.Abs(b);
            if (x == 0) return (int)y;
            if (y == 0) return (int)x;
            int sh = 0;
            while ((int)x > 0x8000 || (int)y > 0x8000) { x = (uint)((int)x >> 1); y = (uint)((int)y >> 1); sh++; }
            uint big = x, sq;
            if ((int)x <= (int)y) { sq = x * x; big = y; } else sq = y * y;
            uint acc = 0;
            if (sq != 0)
            {
                int step = (int)(big << 1);
                do { acc = (uint)(step + (int)acc + 1); big++; step += 2; } while (acc < sq);
            }
            return (int)(big << sh);
        }

        float _fontExScale, _fontEyScale;    // the record's SetFontXform, 0 for none (GM_ADVANCED)
        int _rM00 = 0x10000; bool _rSquare = true;  // RealizedPpems' context x scale (TT_FONTCONTEXT +0x50, 16.16) and whether it is y's

        /// <summary>bGetNtoW_Win31 @1401e7910's x scale (notional to world) for a font whose y scale
        /// is <paramref name="h"/>. Its ex / ey are the font xform SetFontXform put on the DC
        /// (dc+0x1c4 / +0x1c8: an EMF record played GM_COMPATIBLE), else the page's own scales
        /// (dcattr+0x14c / +0x13c and +0x150 / +0x140, viewport over window extent, or the
        /// world-to-device matrix over 16): a WMF played into a stretched placement realizes its
        /// font square on the device, y's scale on both axes.
        /// <list type="bullet">
        /// <item>lfWidth 0: x = |h ey| / ex;</item>
        /// <item>lfWidth w: x = w / fwdAveCharWidth (IFI +0x4c), unless round(avg h ey) is under a
        /// pixel or no more than round(w ex) / 256, which a TrueType face takes as lfWidth 0.</item>
        /// </list></summary>
        float NotionalX(TrueTypeFont face, GdiFont lf, float h, float m11, float m22)
        {
            float ex = _fontExScale, ey = _fontEyScale;
            if (ex == 0f || ey == 0f)
            {
                if (!_wmfCanvas) return h;
                ex = m11 * 0.0625f; ey = m22 * 0.0625f;
                if (ex == 0f || ey == 0f) return h;
            }
            float f15 = ey != 1f ? MathF.Abs(h * ey) : MathF.Abs(h);
            float wx = f15;
            if (lf.Width != 0)
            {
                float w = Math.Abs(lf.Width);
                bool fits = true;
                int iw = Math.Abs(lf.Width);
                if (ex != 1f)
                {
                    w *= ex;
                    if (MathF.Abs(w) >= 2.1e9f) fits = false; else iw = Math.Abs((int)MathF.Floor(MathF.Abs(w) + 0.5f));
                    w = MathF.Abs(w);
                }
                float avg = face.XAvgCharWidth;
                float f16 = avg * f15;
                int ia = MathF.Abs(f16) >= 2.1e9f ? -1 : (int)MathF.Floor(f16 + 0.5f);
                if (ia >= 1 && fits && ia > iw / 256) wx = w / avg;
            }
            if (ex != 1f) wx /= ex;
            return MathF.Abs(wx);
        }

        /// <summary>The whole ppem of each axis GDI realizes a scale-only font at.
        /// <list type="bullet">
        /// <item>bGetNtoW_Win31 @1401e7910 (win32kfull): notional to world, y = the height over the em
        /// (lfHeight &lt; 0) or over usWinAscent + usWinDescent (lfHeight &gt; 0); x = |y eyScale| /
        /// exScale under the font xform SetFontXform put on the DC (dc+0x1c4, dc+0x1c8);</item>
        /// <item>bGetNtoD_Win31 @1401e7458: times the world-to-device matrix, times 1/16;</item>
        /// <item>bNewXform @14001c018 (fontdrvhost, ttfd): each coefficient to 16.16, rounded;</item>
        /// <item>bComputeMaxGlyph @14001b198: y's ppem the 16.16 times the em, FixMul-rounded;</item>
        /// <item>bSetXform @14001c2c8: the scaler gets the point size of that ppem and a matrix whose
        /// x is m11 upem / ppem (16.16, rounded twice);</item>
        /// <item>scl_InitializeScaling @140040540: the axis scale is that times the ppem, rounded to a
        /// whole pixel (the face's integer-ppem flag).</item>
        /// </list></summary>
        (int X, int Y, bool Stretched) RealizedPpems(GpGdiFont font, GdiFont lf, float m11, float m22, bool scaleOnly, bool turned = false)
        {
            TrueTypeFont face = font.Face;
            int upem = face.UnitsPerEmForHinting;
            float h;
            if (lf.Height < 0) h = (float)-lf.Height / upem;
            else if (lf.Height > 0) h = (float)lf.Height / (face.WinAscent + face.WinDescent);
            else h = (float)font.Ppem / upem;
            float wx = NotionalX(face, lf, h, m11, m22);
            float dx = MathF.Abs(wx * m11) * 0.0625f, dy = MathF.Abs(h * m22) * 0.0625f;
            static int Fx(float v) => (int)Math.Floor(v * 65536.0 + 0.5);
            int m11fx = Fx(dx), m22fx = Fx(dy);
            if (turned)
            {
                // A quarter turn is not a plain scale (bNewXform @14001c018 leaves flag bit 0 clear),
                // and ttfd sizes it another way:
                //   bComputeMaxGlyph @14001b198  the point size (16.16) is iHipot of the glyph's y
                //                                row, each component m upem 72 / dpi rounded;
                //   bSetXform @14001c2c8         the matrix is divided by that size: every entry
                //                                FixMul'd by upem 72 2^32 / (dpi pt) (CompDiv);
                //   scl_InitializeScaling        mth_FoldPointSizeResolution multiplies it back by
                //     @140040540                 (dpi pt + 36) / 72 (FixMul), and each axis is the
                //                                larger entry of its row, rounded to a whole pixel.
                const int dpi = 96;
                long RoundDiv(long a, long d) => (a + (a < 0 ? -d / 2 : d / 2)) / d;
                long ptY = RoundDiv((long)m22fx * upem * 72, dpi);     // the y row (m10, 0): |m10|
                int pt16 = (int)Math.Abs(ptY);
                if (pt16 < 1) return (0, 0, false);
                long num = ((long)upem << 16) * (72L << 16), den = (long)dpi * pt16;
                int k = (int)((num + den / 2) / den);
                long f = ((long)dpi * pt16 + 36) / 72;
                int Axis(int e) => (int)((Math.Abs((long)TrueTypeInterpreter.DwFixMul(TrueTypeInterpreter.DwFixMul(e, k), (int)f)) + 0x8000) >> 16);
                int r0 = TrueTypeInterpreter.DwFixMul(m11fx, k), r1 = TrueTypeInterpreter.DwFixMul(m22fx, k);
                // STRETCHED (GETINFO selector 4) when the rows of that matrix differ in length.
                return (Axis(m11fx), Axis(m22fx), TrueTypeInterpreter.DwFixMul(r0, r0) != TrueTypeInterpreter.DwFixMul(r1, r1));
            }
            int ppemY = (int)(((long)m22fx * upem + 0x8000) >> 16);
            if (scaleOnly && face.HasVdmx && lf.Height > 0) ppemY = GpGdiFont.CellPpem(face, m22fx);
            if (ppemY < 1) return (0, 0, false);
            int m22eff = m22fx;
            // vQuantizeXform @14001ea70, for a face with a 'VDMX': y becomes exactly the ppem over
            // the em, and x follows it in proportion -- or becomes y, when the difference moves
            // the face's average character width (IFIMETRICS.fwdAveCharWidth) by less than half a
            // pixel, so a nearly square transform is made square.
            if (scaleOnly && face.HasVdmx)
            {
                int m22q = (int)((((long)ppemY << 16) + upem / 2) / upem);
                m22eff = m22q;
                if (m11fx == m22fx || ((long)(m11fx - m22q) * face.XAvgCharWidth + 0x8000) >> 16 == 0) m11fx = m22q;
                else m11fx = (int)(((long)m11fx * m22q + m22fx / 2) / m22fx);
            }
            long ratio = (((long)upem << 16) * 96 + ppemY * 96 / 2) / (ppemY * 96L);
            int m00 = (int)((ratio * m11fx + 0x8000) >> 16);
            int ppemX = (int)((((long)m00 * ppemY) + 0x8000) >> 16);
            // STRETCHED (GETINFO selector 4): bSetXform hands the scaler the identity when +0x50 equals
            // +0x60, else x is m00 over y's size, and the rows then differ in length.
            bool stretchedInfo = m11fx != m22eff && TrueTypeInterpreter.DwFixMul(m00, m00) != 0x10000;
            _rM00 = m11fx; _rSquare = m11fx == m22eff;
            return (ppemX, ppemY, stretchedInfo);
        }

        /// <summary>A run of GDI ClearType text, as channel LEVELS (0..6, three a pixel) over a box of
        /// device pixels.</summary>
        sealed class CtLevels
        {
            public int Left, Top, Width, Height;
            public byte[] Lvl;          // 3 per pixel, red green blue
        }

        static readonly byte[] s_rampToLevel = BuildRampToLevel();
        static byte[] BuildRampToLevel()
        {
            var t = new byte[256];
            for (int v = 0; v < 256; v++) t[v] = (byte)((v * 6 + 127) / 255);
            return t;
        }

        /// <summary>GDI's ClearType text for glyphs put down at whole device pixels: the face's GDI
        /// fit (TrueTypeFont.TryGetHintedOutline in ClearType mode -- the fontdrvhost interpreter
        /// the WinForms text is drawn with) at the realized ppem of each axis, and the scan, the
        /// 6x1 / 6x5 filter, the dropout, the bold smear, the line-box clip and the level sums of
        /// win32k's run (PathRasterizer.RasterizeSubpixel with the per-run configuration
        /// WgpuSceneRenderer.EmitStringRun gives a string run).</summary>
        static CtLevels GdiClearTypeRun(TrueTypeFont face, int[] gids, int[] xs, int[] ys, int ppemX, int ppemY, int quarter, (int Ax, int Ay, int Dx, int Dy) axes, bool[] upright = null, bool stretchInfo = false, GeneralFit gen = null, bool contrastOk = true)
        {
            int n = gids.Length;
            bool savedSub = TrueTypeFont.SubpixelFitting, savedCt = TrueTypeFont.ClearTypeRendering;
            int ssx = TrueTypeInterpreter.StretchPpemX, ssy = TrueTypeInterpreter.StretchPpemY;
            bool stretched = ppemX != ppemY;
            TrueTypeInterpreter.StretchPpemX = stretched ? ppemX : 0;
            TrueTypeInterpreter.StretchPpemY = stretched ? ppemY : 0;
            int savedStretchInfo = TrueTypeInterpreter.GdiStretchInfo;
            int savedWord = TrueTypeInterpreter.GdiWord, savedTurn = TrueTypeInterpreter.GdiTurn;
            TrueTypeInterpreter.GdiStretchInfo = stretchInfo ? 1 : 2;
            // A turned run is fitted, and its pre-program run, under the word and the device
            // mapping fs__NewTransformation and fs__Contour see (TurnedWord, GdiTurn).
            // A general rotation keeps neither compatible widths (m01 is not 0) nor toggles the axis (m00
            // is not 0): ClearType, symmetric if the gasp says so.
            int savedGasp = TrueTypeInterpreter.GdiGaspPpem;
            TrueTypeInterpreter.GdiGaspPpem = gen != null && gen.EmPpem != ppemY ? gen.EmPpem : 0;
            // fs__NewTransformation @1400254d0 on bSetXform's 0x03 / 0x23: compatible widths (bit 1)
            // only while m01 is 0, and bit 2 toggled when m00 is 0 for a word without bit 5.
            int runWord = gen != null ? (gen.M01Zero ? 0 : face.WantsSymmetricSmoothing(ppemY) ? 0x21 : gen.M00Zero ? 0x05 : 0x01)
                        : quarter % 2 == 1 ? TurnedWord(face, ppemY) : 0;
            int runTurn = quarter > 0 ? TrueTypeInterpreter.PackGdiTurn(axes.Ax, axes.Ay, axes.Dx, axes.Dy) : 0;
            bool savedRotated = TrueTypeInterpreter.GdiRotated;
            TrueTypeInterpreter.GdiRotated = (gen != null && gen.Rotated);
            TrueTypeInterpreter.GdiWord = runWord;
            TrueTypeInterpreter.GdiTurn = runTurn;
            if (!savedSub) TrueTypeFont.SubpixelFitting = true;
            if (!savedCt) TrueTypeFont.ClearTypeRendering = true;
            try
            {
                float ppem = ppemY;
                var figs = new System.Collections.Generic.List<PathFigure>();
                var owners = new System.Collections.Generic.List<int>();
                System.Collections.Generic.Dictionary<int, (int Top, int Bottom)> rowClip = null;
                System.Collections.Generic.Dictionary<int, (int Left, int Right)> colClip = null;
                System.Collections.Generic.Dictionary<int, int> dropouts = null;
                int clipAsc = 0, clipDesc = 0;
                if (quarter == 0 && face.TryGetGdiLineMetrics(ppemY, out clipAsc, out clipDesc))
                    rowClip = new System.Collections.Generic.Dictionary<int, (int, int)>();
                int colL = 0, colR = 0;
                if (quarter == 0 && face.GdiEmboldensBitmap && face.TryGetGdiColumnLimits(ppem, out colL, out colR))
                    colClip = new System.Collections.Generic.Dictionary<int, (int, int)>();
                for (int i = 0; i < n; i++)
                {
                    int ordinal = i + 1;
                    if (rowClip != null) rowClip[ordinal] = (ys[i] - clipAsc, ys[i] + clipDesc);
                    if (colClip != null) colClip[ordinal] = (xs[i] + colL, xs[i] + colR);
                    System.Collections.Generic.List<PathFigure> outline;
                    int? fitDropout = null;     // the glyph's own scan record, asked under the word it was fitted with
                    int bmpShiftX = 0, bmpShiftY = 0;   // a general rotation's bitmap placed off its own box (GeneralBitmapShift)
                    if (quarter == 0)
                    {
                        if (!face.TryGetHintedOutline(gids[i], ppem, out outline) || outline.Count == 0) continue;
                    }
                    else if (upright != null && upright[i])
                    {
                        // A full-width glyph of a vertical ('@') face: ttfd fits it under the
                        // vertical transform (vCalcXformVertical @14008a240), the font's turned
                        // one turned a quarter back, so it stands upright on the device -- its x
                        // sized by what was the glyph's y and the other way about.
                        TrueTypeInterpreter.StretchPpemX = stretched ? ppemY : 0;
                        TrueTypeInterpreter.StretchPpemY = stretched ? ppemX : 0;
                        TrueTypeInterpreter.GdiWord = 0;
                        TrueTypeInterpreter.GdiTurn = 0;
                        bool got;
                        try { got = face.TryGetHintedOutline(gids[i], ppemX, out outline); fitDropout = face.GlyphDropout(gids[i], ppemX); }
                        finally
                        {
                            TrueTypeInterpreter.StretchPpemX = stretched ? ppemX : 0;
                            TrueTypeInterpreter.StretchPpemY = stretched ? ppemY : 0;
                            TrueTypeInterpreter.GdiWord = runWord;
                            TrueTypeInterpreter.GdiTurn = runTurn;
                        }
                        if (!got || outline.Count == 0) continue;
                        outline = Turned(outline, (-axes.Dx, -axes.Dy, axes.Ax, axes.Ay));
                    }
                    else
                    {
                        // A turned font, fitted at its own two ppems (scl_InitializeScaling: each
                        // axis the larger component of its row of the matrix, rounded) and given
                        // back by scl_PostTransformGlyph turned onto the device, where it is scanned
                        // and filtered like any other glyph. A half turn keeps GDI's word; a quarter
                        // turn does not (TurnedWord).
                        if (!face.TryGetHintedOutline(gids[i], ppem, out outline) || outline.Count == 0) continue;
                        outline = gen != null ? GeneralTransformed(outline, gen) : Turned(outline, axes);
                        if (gen != null && gen.Phase45)
                        {
                            // fs_FindBitMapSize @140022a48: under the 45-degree trick (clientRec+0x19c,
                            // fs__NewTransformation's mth_Max45Trick @14008e418 of either row of the
                            // matrix) every point of the outline moves one unit right before the box is
                            // measured -- a unit of the overscaled ClearType x, 1/384 of a pixel -- so
                            // that no edge of a glyph turned by 45 degrees falls on a sample centre.
                            for (int k = 0; k < outline.Count; k++) outline[k] = Translated(outline[k], 1f / (64f * 6f), 0f);
                        }
                        // lGetGlyphBitmap asks the CONTEXT's flags (bNewXform: diagonal, quarter turn),
                        // which come from the FONTOBJ's matrix without FO_SIM_ITALIC's slant: a slanted
                        // glyph at a quarter or half turn keeps the axial placement.
                        if (gen != null && !gen.Axial) (bmpShiftX, bmpShiftY) = GeneralBitmapShift(outline, face.WantsSymmetricSmoothing(ppemY) ? 5 : 1);
                    }
                    if ((fitDropout ?? face.GlyphDropout(gids[i], ppem)) is int gd && gd >= 0) (dropouts ??= new()).Add(ordinal, gd);
                    foreach (PathFigure f in outline)
                    {
                        figs.Add(Translated(f, xs[i] + bmpShiftX, ys[i] + bmpShiftY));
                        owners.Add(ordinal);
                    }
                }
                if (figs.Count == 0) return null;
                bool sym = face.WantsSymmetricSmoothing(ppem);
                PathRasterizer.SubpixelRowsForRun = sym ? 5 : 0;
                PathRasterizer.PpemForRun = ppemY;
                PathRasterizer.SimBoldPixelsForRun = face.GdiEmboldensBitmap ? TrueTypeFont.SimBoldSmearPixels(ppemY) : 0;
                PathRasterizer.DropoutForRun = face.WantsDropoutControl(ppem, out int scanType) ? scanType + 1 : 0;
                PathRasterizer.SymmetricVerticalForRun = false;
                PathRasterizer.ContrastFilterForRun = face.GdiContrastPalette && contrastOk;
                PathRasterizer.FigureGlyphIdsForRun = owners.ToArray();
                PathRasterizer.GlyphRowClipForRun = rowClip;
                PathRasterizer.GlyphColClipForRun = colClip;
                PathRasterizer.GlyphDropoutForRun = dropouts;
                PathRasterizer.SubpixelMask sm;
                try { sm = PathRasterizer.RasterizeSubpixel(new PathGeometry(FillRule.NonZero, figs), CurveFlattener.GlyphTolerance); }
                finally
                {
                    PathRasterizer.SubpixelRowsForRun = 0; PathRasterizer.DropoutForRun = 0; PathRasterizer.SimBoldPixelsForRun = 0;
                    PathRasterizer.PpemForRun = 0; PathRasterizer.ContrastFilterForRun = false;
                    PathRasterizer.FigureGlyphIdsForRun = null; PathRasterizer.GlyphRowClipForRun = null;
                    PathRasterizer.GlyphColClipForRun = null; PathRasterizer.GlyphDropoutForRun = null;
                }
                if (sm.IsEmpty) return null;
                var r = new CtLevels { Left = (int)sm.OriginX, Top = (int)sm.OriginY, Width = sm.Width, Height = sm.Height, Lvl = new byte[sm.Width * sm.Height * 3] };
                for (int p = 0; p < sm.Width * sm.Height; p++)
                    for (int c = 0; c < 3; c++) r.Lvl[p * 3 + c] = s_rampToLevel[sm.Rgba[p * 4 + c]];
                return r;
            }
            finally
            {
                TrueTypeInterpreter.StretchPpemX = ssx; TrueTypeInterpreter.StretchPpemY = ssy; TrueTypeInterpreter.GdiStretchInfo = savedStretchInfo;
                TrueTypeInterpreter.GdiWord = savedWord; TrueTypeInterpreter.GdiTurn = savedTurn; TrueTypeInterpreter.GdiRotated = savedRotated;
                TrueTypeInterpreter.GdiGaspPpem = savedGasp;
                if (!savedSub) TrueTypeFont.SubpixelFitting = false;
                if (!savedCt) TrueTypeFont.ClearTypeRendering = false;
            }
        }

        /// <summary>The scaler word a quarter-turned glyph is fitted under. bSetXform @14001c2c8
        /// hands GDI's ClearType text 0x03 (ClearType, compatible widths), or 0x23 when the face's
        /// gasp asks for symmetric smoothing at the size; fs__NewTransformation @1400254d0 keeps
        /// bit 1 only while the matrix's m01 is 0 and, for a word without bit 5, toggles bit 2 when
        /// m00 is 0. A quarter turn has m00 = 0 and m01 &#8800; 0: no compatible widths (no bi-level
        /// pass, no phase), and without symmetric smoothing the glyph's y -- the device's x -- is
        /// the ClearType axis.</summary>
        static int TurnedWord(TrueTypeFont face, float ppem) => face.WantsSymmetricSmoothing(ppem) ? 0x21 : 0x05;

        /// <summary>IsFullWidthCharacter @14001d5f0 asks a per-glyph bit set made when the face is
        /// loaded; not modelled from the binary: here a glyph whose advance is the em or which has
        /// a 'vert' form.</summary>
        static bool IsFullWidth(TrueTypeFont face, int gid)
            => gid > 0 && (face.DesignAdvance(gid) >= face.UnitsPerEmForHinting || VerticalForm(face, gid) != gid);

        /// <summary>bCheckVerticalTable @14008a0f8 -> SearchGsubTable: the face's 'vert' form.</summary>
        static int VerticalForm(TrueTypeFont face, int gid)
        {
            GsubTable gsub = face.Gsub;
            if (gsub == null) return gid;
            foreach (string script in gsub.Scripts())
            {
                int v = gsub.Substitute(script, "vert", gid);
                if (v != gid) return v;
            }
            return gid;
        }

        /// <summary>A glyph outline (x along the baseline, y down) laid on the device: x along the
        /// device unit vector (Ax, Ay), y along (Dx, Dy).</summary>
        static System.Collections.Generic.List<PathFigure> Turned(System.Collections.Generic.List<PathFigure> figs, (int Ax, int Ay, int Dx, int Dy) a)
        {
            System.Numerics.Vector2 T(System.Numerics.Vector2 p) => new System.Numerics.Vector2(p.X * a.Ax + p.Y * a.Dx, p.X * a.Ay + p.Y * a.Dy);
            var r = new System.Collections.Generic.List<PathFigure>(figs.Count);
            foreach (PathFigure f in figs)
            {
                var c = new PathFigure(T(f.Start)) { Closed = f.Closed };
                foreach (PathSegment s in f.Segments)
                    switch (s)
                    {
                        case LineSegment l: c.Segments.Add(new LineSegment(T(l.Point))); break;
                        case QuadraticBezierSegment qq: c.Segments.Add(new QuadraticBezierSegment(T(qq.Control), T(qq.Point))); break;
                        case CubicBezierSegment b: c.Segments.Add(new CubicBezierSegment(T(b.Control1), T(b.Control2), T(b.Point))); break;
                    }
                r.Add(c);
            }
            return r;
        }

        /// <summary>Where GDI puts a generally rotated glyph's ClearType bitmap, relative to where
        /// its ink is. lGetGlyphBitmap @140011370 gives a glyph whose matrix is neither diagonal nor
        /// a quarter turn the origin vFillGLYPHDATA @140012568 hands back, the scaler's rounded
        /// devLeftSideBearing -- the box fs_FindBitMapSize makes of the OUTPUT outline (fs__Contour
        /// divides the overscaled points back with a truncating (v s + s/2) / s), columns
        /// (min + 0x1f) &gt;&gt; 6 and rows (max + 0x20) &gt;&gt; 6 -- while the bitmap the scan fills
        /// starts at fsc_MeasureGlyph's box of the OVERSCALED outline divided by the overscale
        /// (6 across, 5 down when smoothing symmetrically), floored on the left and ceiled at the
        /// top. Where the two disagree the drawn glyph sits that many pixels right and down of its
        /// ink. Returns the shift in device pixels (x right, y down).</summary>
        static (int X, int Y) GeneralBitmapShift(System.Collections.Generic.List<PathFigure> outline, int rows)
        {
            int minX = int.MaxValue, maxY = int.MinValue;
            void P(System.Numerics.Vector2 p)
            {
                int x = (int)Math.Round(p.X * 64f), y = (int)Math.Round(-p.Y * 64f);
                if (x < minX) minX = x;
                if (y > maxY) maxY = y;
            }
            foreach (PathFigure f in outline)
            {
                P(f.Start);
                foreach (PathSegment s in f.Segments)
                    switch (s)
                    {
                        case LineSegment l: P(l.Point); break;
                        case QuadraticBezierSegment q: P(q.Control); P(q.Point); break;
                        case CubicBezierSegment b: P(b.Control1); P(b.Control2); P(b.Point); break;
                    }
            }
            if (minX == int.MaxValue) return (0, 0);
            static int FloorDiv(int a, int b) => (int)Math.Floor(a / (double)b);
            static int CeilDiv(int a, int b) => (int)Math.Ceiling(a / (double)b);
            // fsc_MeasureGlyph's box in overscaled columns and rows; the bitmap starts at the pixel
            // holding its first column and ends at the row past its last, while devLeftSideBearing
            // is the same box divided by the overscale in 16.16, rounded half up.
            int c = (minX * 6 + 0x1f) >> 6, r = (maxY * rows + 0x20) >> 6;
            int ctLeft = FloorDiv(c, 6), ctTop = CeilDiv(r, rows);
            int biLeft = (int)Math.Floor(c / 6.0 + 0.5), biTop = (int)Math.Floor(r / (double)rows + 0.5);
            return (biLeft - ctLeft, ctTop - biTop);
        }

        /// <summary>win32k's float-to-28.4 rounding: half away from zero.</summary>
        static long R16(double v) => v < 0 ? -(long)Math.Floor(-v + 0.5) : (long)Math.Floor(v + 0.5);

        /// <summary>A glyph fitted at <see cref="GeneralFit"/>'s sizes (y down, pixels) turned onto
        /// the device as scl_PostTransformGlyph turns it: each 26.6 point (y up) through
        /// mth_IntelMul's per-product rounding, x' = x P00 + y P10, y' = x P01 + y P11. Only the
        /// outline's own points are turned: an implied on-curve point between two off-curve ones
        /// is made after the turn (fsc_FillGlyph @140034000 averages the two TURNED controls), so it
        /// is the exact average of the turned controls here, not the turned average.</summary>
        static System.Collections.Generic.List<PathFigure> GeneralTransformed(System.Collections.Generic.List<PathFigure> figs, GeneralFit g)
        {
            System.Numerics.Vector2 T(System.Numerics.Vector2 p)
            {
                int x = (int)Math.Round(p.X * 64f), y = (int)Math.Round(-p.Y * 64f);
                int nx = TrueTypeInterpreter.DwFixMul(x, g.P00) + TrueTypeInterpreter.DwFixMul(y, g.P10);
                int ny = TrueTypeInterpreter.DwFixMul(x, g.P01) + TrueTypeInterpreter.DwFixMul(y, g.P11);
                return new System.Numerics.Vector2(nx / 64f, -ny / 64f);
            }
            static System.Numerics.Vector2 Mid(System.Numerics.Vector2 a, System.Numerics.Vector2 b) => new((a.X + b.X) * 0.5f, (a.Y + b.Y) * 0.5f);
            var r = new System.Collections.Generic.List<PathFigure>(figs.Count);
            foreach (PathFigure f in figs)
            {
                int n = f.Segments.Count;
                // The turned controls, so each implied end can take the average of its own control
                // and the next one (the last segment's next is the first, round the closed contour).
                var ctl = new System.Numerics.Vector2?[n];
                for (int i = 0; i < n; i++)
                    if (f.Segments[i] is QuadraticBezierSegment qs) ctl[i] = T(qs.Control);
                System.Numerics.Vector2 End(int i, System.Numerics.Vector2 p)
                    => f.Segments[i] is QuadraticBezierSegment { ImpliedEnd: true } && ctl[i] is { } a && ctl[(i + 1) % n] is { } b ? Mid(a, b) : T(p);
                System.Numerics.Vector2 start = f.ImpliedStart && n > 0 && ctl[n - 1] is { } la && ctl[0] is { } fa ? Mid(la, fa) : T(f.Start);
                var c = new PathFigure(start) { Closed = f.Closed };
                for (int i = 0; i < n; i++)
                    switch (f.Segments[i])
                    {
                        case LineSegment l: c.Segments.Add(new LineSegment(T(l.Point))); break;
                        case QuadraticBezierSegment qq: c.Segments.Add(new QuadraticBezierSegment(ctl[i].Value, End(i, qq.Point))); break;
                        case CubicBezierSegment b: c.Segments.Add(new CubicBezierSegment(T(b.Control1), T(b.Control2), T(b.Point))); break;
                    }
                r.Add(c);
            }
            return r;
        }

        static PathFigure Translated(PathFigure f, float dx, float dy)
        {
            System.Numerics.Vector2 T(System.Numerics.Vector2 p) => new System.Numerics.Vector2(p.X + dx, p.Y + dy);
            var c = new PathFigure(T(f.Start)) { Closed = f.Closed };
            foreach (PathSegment s in f.Segments)
                switch (s)
                {
                    case LineSegment l: c.Segments.Add(new LineSegment(T(l.Point))); break;
                    case QuadraticBezierSegment q: c.Segments.Add(new QuadraticBezierSegment(T(q.Control), T(q.Point))); break;
                    case CubicBezierSegment b: c.Segments.Add(new CubicBezierSegment(T(b.Control1), T(b.Control2), T(b.Point))); break;
                }
            return c;
        }
    }

    /// <summary>win32k's sine and cosine of an escapement, which are not the libm ones:
    /// bGetNtoD_Win31 @1401e7458 (win32kfull) turns the notional-to-device scales by
    /// efCos / efSin (win32kbase) of the escapement's float tenths over 10f, and those
    /// interpolate a 33-entry quarter table.</summary>
    internal static class GdiTrig
    {
        // gaefSin @140291100 (win32kbase): sin(i pi / 64), i = 0..32, as stored.
        static readonly uint[] s_table =
        {
            0x00000000, 0x3d48fb30, 0x3dc8bd36, 0x3e164083, 0x3e47c5c2, 0x3e78cfcc, 0x3e94a031, 0x3eac7cd4,
            0x3ec3ef15, 0x3edae880, 0x3ef15aea, 0x3f039c3d, 0x3f0e39da, 0x3f187fc0, 0x3f226799, 0x3f2beb4a,
            0x3f3504f3, 0x3f3daef9, 0x3f45e403, 0x3f4d9f02, 0x3f54db31, 0x3f5b941a, 0x3f61c598, 0x3f676bd8,
            0x3f6c835e, 0x3f710908, 0x3f74fa0b, 0x3f7853f8, 0x3f7b14be, 0x3f7d3aac, 0x3f7ec46d, 0x3f7fb10f,
            0x3f800000,
        };

        static float T(int i) => BitConverter.UInt32BitsToSingle(s_table[i]);

        /// <summary>efSin @1400730b0: |degrees| times FP_SINE_FACTOR (32/90, 0x3eb60b61) splits
        /// into a whole step (truncated) and its fraction (eFraction @14018a170); bit 5 of the
        /// step mirrors the quarter, bit 6 (xor the argument's sign) negates; the two table
        /// entries are interpolated by the fraction, each operation rounded to float.</summary>
        internal static float Sin(float degrees)
        {
            bool neg = degrees < 0f;
            if (neg) degrees = -degrees;
            float x = BitConverter.UInt32BitsToSingle(0x3eb60b61) * degrees;
            int step = x < 2147483648f ? (int)x : 0;
            float frac = x < 1f ? x : x >= 8388608f ? 0f : x - (int)x;
            if ((step >> 5 & 2) != 0) neg = !neg;
            int i = step & 0x1f;
            float r;
            if ((step >> 5 & 1) == 0) { float a = T(i); r = (T(i + 1) - a) * frac + a; }
            else { float a = T(32 - i); r = -((a - T(31 - i)) * frac) + a; }
            return neg ? -r : r;
        }

        /// <summary>efCos @1400730a0: efSin(90f + degrees).</summary>
        internal static float Cos(float degrees) => Sin(90f + degrees);

        /// <summary>The escapement (tenths of a degree) as bGetNtoD_Win31 hands it to them.</summary>
        internal static float Degrees(int escapement) => escapement / 10f;
    }
}
