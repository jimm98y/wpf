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
//                 vCalcXformVertical's shift. Other angles, and a mapping that turns, are not drawn
//                 here (the old path draws them)
//   ink           through the DC's clip and ETO_CLIPPED; the background (OPAQUE mode or ETO_OPAQUE)
//                 the text box in the background colour
//
// NOT EXACT: a turned glyph's fit (Times New Roman 'A' at 21 x 29 in the text scenario, about
// 100 pixels); which glyphs ttfd counts full-width (its per-glyph bit set is not modelled) and the
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
            GpGdiFont font = GpGdiFont.Get(lf.Face, lf.Height, lf.Escapement, lf.Weight, lf.Italic, lf.Underline, lf.StrikeOut, lf.Quality, lf.CharSet, lf.PitchAndFamily);
            if (font == null) return false;
            GpMat m = LogicalToTarget;
            if (m.M12 != 0f || m.M21 != 0f) return false;
            int q = ((font.Escapement / 900) % 4 + 4) % 4;
            if (font.Escapement % 900 != 0) return false;
            int n = s.Length;
            if (n == 0) return true;
            float sx = Math.Abs(m.M11), sy = Math.Abs(m.M22);
            // The em through the mapping: GDI's realization (RealizedPpems), each axis of the glyph
            // at its own whole ppem.
            float em = font.Ppem;
            GdiXform wtod = TargetWtoD();
            (int rx, int ry) = RealizedPpems(font, lf, wtod.M11, wtod.M22, q == 0 && wtod.M22 > 0);
            // bGetNtoD_Win31 turns the font AFTER the world-to-device scale (notional to world, times
            // world to device, times the escapement), so a turned glyph is still sized x by the
            // device x scale and y by the device y scale.
            int ppemAlong = rx, ppemAcross = ry;
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
                        long gx = fx + pens[i] * ax, gy = fy + pens[i] * ay;
                        xs[i] = (int)((gx + 8) >> 4);
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
                    CtLevels lv = GdiClearTypeRun(font.Face, gids, xs, ys, ppemAlong, ppemAcross, q, (ax, ay, dnx, dny), upright);
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

        float _fontExScale, _fontEyScale;    // the record's SetFontXform, 0 for none (GM_ADVANCED)

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
        (int X, int Y) RealizedPpems(GpGdiFont font, GdiFont lf, float m11, float m22, bool scaleOnly)
        {
            TrueTypeFont face = font.Face;
            int upem = face.UnitsPerEmForHinting;
            float h;
            if (lf.Height < 0) h = (float)-lf.Height / upem;
            else if (lf.Height > 0) h = (float)lf.Height / (face.WinAscent + face.WinDescent);
            else h = (float)font.Ppem / upem;
            float wx = h;
            float ex = _fontExScale, ey = _fontEyScale;
            if (ex != 0f && ey != 0f)
            {
                wx = MathF.Abs(h * ey);
                if (ex != 1f) wx = wx / ex;
            }
            float dx = MathF.Abs(wx * m11) * 0.0625f, dy = MathF.Abs(h * m22) * 0.0625f;
            static int Fx(float v) => (int)Math.Floor(v * 65536.0 + 0.5);
            int m11fx = Fx(dx), m22fx = Fx(dy);
            int ppemY = (int)(((long)m22fx * upem + 0x8000) >> 16);
            if (ppemY < 1) return (0, 0);
            // vQuantizeXform @14001ea70, for a face with a 'VDMX': y becomes exactly the ppem over
            // the em, and x follows it in proportion -- or becomes y, when the difference moves
            // the face's average character width (IFIMETRICS.fwdAveCharWidth) by less than half a
            // pixel, so a nearly square transform is made square.
            if (scaleOnly && face.HasVdmx && lf.Height <= 0)
            {
                int m22q = (int)((((long)ppemY << 16) + upem / 2) / upem);
                if (m11fx == m22fx || ((long)(m11fx - m22q) * face.XAvgCharWidth + 0x8000) >> 16 == 0) m11fx = m22q;
                else m11fx = (int)(((long)m11fx * m22q + m22fx / 2) / m22fx);
            }
            long ratio = (((long)upem << 16) * 96 + ppemY * 96 / 2) / (ppemY * 96L);
            int m00 = (int)((ratio * m11fx + 0x8000) >> 16);
            int ppemX = (int)((((long)m00 * ppemY) + 0x8000) >> 16);
            return (ppemX, ppemY);
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
        static CtLevels GdiClearTypeRun(TrueTypeFont face, int[] gids, int[] xs, int[] ys, int ppemX, int ppemY, int quarter, (int Ax, int Ay, int Dx, int Dy) axes, bool[] upright = null)
        {
            int n = gids.Length;
            bool savedSub = TrueTypeFont.SubpixelFitting, savedCt = TrueTypeFont.ClearTypeRendering;
            int ssx = TrueTypeInterpreter.StretchPpemX, ssy = TrueTypeInterpreter.StretchPpemY;
            bool stretched = ppemX != ppemY;
            TrueTypeInterpreter.StretchPpemX = stretched ? ppemX : 0;
            TrueTypeInterpreter.StretchPpemY = stretched ? ppemY : 0;
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
                        bool got;
                        try { got = face.TryGetHintedOutline(gids[i], ppemX, out outline); }
                        finally
                        {
                            TrueTypeInterpreter.StretchPpemX = stretched ? ppemX : 0;
                            TrueTypeInterpreter.StretchPpemY = stretched ? ppemY : 0;
                        }
                        if (!got || outline.Count == 0) continue;
                        outline = Turned(outline, (-axes.Dx, -axes.Dy, axes.Ax, axes.Ay));
                    }
                    else
                    {
                        // A turned font. fs__NewTransformation @1400254d0 toggles the ClearType word's
                        // bit 2 (ClearType on the glyph's y) for m00 == 0 only when the word lacks
                        // bit 5, and bSetXform @14001c2c8 hands GDI's ClearType text 0x23: so the
                        // glyph is fitted as ever, at its own two ppems, and scl_PostTransformGlyph
                        // gives the fit back turned onto the device, where it is scanned and filtered
                        // like any other glyph.
                        if (!face.TryGetHintedOutline(gids[i], ppem, out outline) || outline.Count == 0) continue;
                        outline = Turned(outline, axes);
                    }
                    if (face.GlyphDropout(gids[i], ppem) is int gd && gd >= 0) (dropouts ??= new()).Add(ordinal, gd);
                    foreach (PathFigure f in outline)
                    {
                        figs.Add(Translated(f, xs[i], ys[i]));
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
                PathRasterizer.ContrastFilterForRun = face.GdiContrastPalette;
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
                TrueTypeInterpreter.StretchPpemX = ssx; TrueTypeInterpreter.StretchPpemY = ssy;
                if (!savedSub) TrueTypeFont.SubpixelFitting = false;
                if (!savedCt) TrueTypeFont.ClearTypeRendering = false;
            }
        }

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
}
