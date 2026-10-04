// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The imagers a recording's DrawString and DrawDriverString go through down-level (gdiplus.dll
// 10.0.26100, arm64), up to the glyphs they hand DrawPlacedGlyphs (see GpMetaDriver.Text.cs):
//
//   GpGraphics::DrawString @180010f00   on a metafile: the EMF+ record, then -- DownLevel -- the
//       FullTextImager (newTextImager @1800ebe90; never the fast imager) drawn with the metafile
//       detached, so the clip it sets and the lines it strokes record nothing of their own.
//   FullTextImager, Line Services: the lines in ideal units (GpTextLayout; r = 2048 / em per world
//       unit, FTI +0x34), each line a run;
//     BuiltLine::BuiltLine @1800f3670   margins round(margin * em * r) (+0x44 / +0x48), the line
//       start (+0x5c) the leading margin plus GetPhysicalAlignment's share of what the line leaves
//       of round(width * r) (all of it, or half; of nothing when there is no width);
//     FullTextImager::DrawGlyphs ($$h @18023f4d8) -> GlyphImager::Initialize @1800f5df0: the
//       device advances (GetGdiCompatibleGlyphPlacements under world to device, GDI natural for
//       ClearType) in ideal units rounded, then AdjustGlyphAdvances @1800f4e18 brings them back
//       towards Line Services' nominal ones -- leading and trailing spaces nominal, the rest
//       absorbed by the margins and the alignment, spread over the spaces or the gaps between
//       letters -- and moves the cell origin (+0x220) by what the alignment took;
//     GlyphImager::DrawGlyphs @1800f56f8: GetDisplayCellOrigin @18003d690 puts the run's origin on
//       the device grid of an axis-scale transform, GlyphPlacementToGlyphOrigins @1800f59d0 steps
//       the adjusted advances from it, through world to device, to DrawPlacedGlyphs;
//     GdipLscbkDrawUnderline: the underline of each run a line with an aliased pen of
//       max(1, round(|w * m11|)) pixels, round(-post.underlinePosition) ideal units off the baseline;
//     FullTextImager::Draw @1800f0360: the layout rectangle intersected into the clip unless NoClip.
//   DriverStringImager (GpGraphics::DrawDriverString @1800ea638, DrawGlyphRange @1800ea708): a
//       font with an underline or strikeout draws nothing (status 2); every origin is the
//       caller's point through world to device; the realization's matrix takes the record matrix's
//       2x2 part only; the characters are the string when the glyphs were looked up (CmapLookup).
//

using System.Collections.Generic;
using System.Drawing.Drawing2D;
using Microsoft.Wpf.Interop.WebGpu.Composition.Text;
using GdipText = Microsoft.Wpf.Interop.WebGpu.Composition.Text.GdiPlusText;

namespace System.Drawing.WebGpuBackend.Gdip
{
    internal sealed partial class GpMetafileRecorder
    {
        const int IdealEm = 2048;

        /// <summary>GpGraphics::DrawString's em: the font's size in world units.</summary>
        float EmWorld(Font font)
        {
            PageMultipliers(_state.PageUnit, _state.PageScale, out float pmx, out _);
            float f;
            switch (font.Unit)
            {
                case GraphicsUnit.Point: f = DpiY / 72f / pmx; break;
                case GraphicsUnit.Inch: f = DpiY / pmx; break;
                case GraphicsUnit.Document: f = DpiY / 300f / pmx; break;
                case GraphicsUnit.Millimeter: f = DpiY / 25.4f / pmx; break;
                default: f = 1f; break;
            }
            return font.Size * f;
        }

        /// <summary>The realization's render mode for the context's hint (CalculateTextRenderingHintInternal
        /// and GpFaceRealization::Realize): 5 ClearType, 3/4 antialiased, 1/2 bi-level.</summary>
        int RealizationMode(TrueTypeFont face, string family, float emDev, bool square)
        {
            int hint = _state.TextHint;
            if (hint == 0) hint = GdipText.HintClearTypeGridFit;
            int ppem = (int)MathF.Floor(emDev + 0.5f);
            if (hint == GdipText.HintClearTypeGridFit
                && ((square && face.EmbeddedBitmapCount(ppem) > 100) || string.Equals(family, "Marlett", StringComparison.OrdinalIgnoreCase)))
                return 1;
            if (hint == GdipText.HintAntiAliasGridFit && !face.GaspDoGray(ppem)) return 1;
            return hint;
        }

        /// <summary>GetGlyphStringDeviceAdvanceVector of one glyph: its device advance in pixels.</summary>
        static float DeviceAdvancePx(TrueTypeFont face, int gid, float em, float sx, float sy, int mode)
        {
            int upem = face.UnitsPerEmForHinting;
            if (mode == 5)
            {
                GdipText.NaturalMetrics(face, gid, em, sx, sy, out int adv, out _, out _);
                return MathF.Floor(adv * (em * sx / upem) + 0.5f);
            }
            if (mode == 1 || mode == 3)
            {
                GdipText.ClassicMetrics(face, gid, em * sx, out int adv, out _, out _);
                return MathF.Floor(adv * (em * sx / upem) + 0.5f);
            }
            return face.DesignAdvance(gid) * (em * sx / upem);
        }

        /// <summary>DrawPlacedGlyphs' draw rectangle: the union of the glyphs' bitmaps (GetGlyphPos),
        /// empty when none has ink.</summary>
        static Rectangle GlyphUnion(TrueTypeFont face, ushort[] glyphs, PointF[] o, float emDev, Func<int, NaturalClearType.GlyphBits> bitsOf = null)
        {
            int l = int.MaxValue, t = int.MaxValue, r = int.MinValue, b = int.MinValue;
            for (int i = 0; i < glyphs.Length; i++)
            {
                if (glyphs[i] == 0xffff) continue;
                NaturalClearType.GlyphBits g = bitsOf != null ? bitsOf(i) : GdipText.Glyph(face, glyphs[i], emDev);
                if (g.IsEmpty) continue;
                (int s, int row) = GdipText.Place(g, o[i].X, o[i].Y);
                int pl = FloorDiv(s, 6), pr = -FloorDiv(-(s + g.Width), 6);
                l = Math.Min(l, pl); r = Math.Max(r, pr);
                t = Math.Min(t, row); b = Math.Max(b, row + g.Height);
            }
            if (l >= r || t >= b) return Rectangle.Empty;
            return new Rectangle(l, t, r - l, b - t);
        }

        static int FloorDiv(int a, int b) => a >= 0 ? a / b : -((-a + b - 1) / b);

        // ---- DrawDriverString -----------------------------------------------------------------------

        void GdiDrawDriverString(ushort[] text, Font font, Brush brush, PointF[] positions, int flags, Matrix matrix)
        {
            if (!Gdi || positions == null) return;
            if ((font.Style & (FontStyle.Underline | FontStyle.Strikeout)) != 0) return;
            string family = font.FontFamily.Name;
            int style = (int)font.Style & 3;
            TrueTypeFont face = GdipText.Face(family, style);
            if (face == null) return;
            int n = Math.Min(text.Length, positions.Length);
            if ((flags & 4) != 0) n = text.Length;      // RealizedAdvance: one origin
            if (n < 1) return;
            float em = EmWorld(font);
            GpMat wtd = WorldToDevice;
            GpMat rm = wtd;
            if (matrix != null)
            {
                float[] e = matrix.Elements;
                rm = GpMat.Multiply(new GpMat(e[0], e[1], e[2], e[3], 0f, 0f), wtd);
            }
            int upem = face.UnitsPerEmForHinting;
            float k = em / upem;
            float sx = MathF.Sqrt(rm.M11 * rm.M11 + rm.M12 * rm.M12), sy = MathF.Sqrt(rm.M21 * rm.M21 + rm.M22 * rm.M22);
            int mode = RealizationMode(face, family, em * sx, sx == sy);
            var gl = new ushort[n];
            var org = new PointF[n];
            bool cmap = (flags & 1) != 0;
            for (int i = 0; i < n; i++)
            {
                gl[i] = cmap ? (ushort)face.GlyphIndex((char)text[i]) : text[i];
                if ((flags & 4) != 0 && i > 0)
                {
                    float adv = DeviceAdvancePx(face, gl[i - 1], em, sx, sy, mode);
                    org[i] = new PointF(org[i - 1].X + adv, org[i - 1].Y);
                }
                else
                {
                    PointF p = positions[(flags & 4) != 0 ? 0 : i];
                    org[i] = new PointF(wtd.M11 * p.X + wtd.M21 * p.Y + wtd.Dx, wtd.M12 * p.X + wtd.M22 * p.Y + wtd.Dy);
                }
            }
            Rectangle draw = GlyphUnion(face, gl, org, em * sx);
            if (draw.Width <= 0 || draw.Height <= 0 || TotallyClipped(draw)) return;
            var gd = new GlyphDraw
            {
                Draw = draw, Glyphs = gl, Origins = org, Brush = brush,
                Text = cmap ? new string(Array.ConvertAll(text, c => (char)c), 0, n) : null,
                LastAdvance = (int)MathF.Floor(DeviceAdvancePx(face, gl[n - 1], em, sx, sy, mode) + 0.5f),
                Family = family, EmUnits = upem, Style = style,
                M11 = rm.M11 * k, M12 = rm.M12 * k, M21 = rm.M21 * k, M22 = rm.M22 * k,
            };
            lock (GpMetaDriverState.Lock)
                DriverDrawGlyphs(gd);
        }

        // ---- DrawString: FullTextImager ---------------------------------------------------------------

        void GdiDrawString(string s, Font font, RectangleF layout, StringFormat format, Brush brush)
        {
            if (!Gdi || string.IsNullOrEmpty(s)) return;
            string family = font.FontFamily.Name;
            int style = (int)font.Style;
            TrueTypeFont face = GdipText.Face(family, style & 3);
            GpFontFamily.Metrics? mm = GpFontFamily.Get(family, (FontStyle)(style & 3));
            if (face == null || mm == null) return;
            GpMat m = WorldToDevice;
            if (m.M12 != 0f || m.M21 != 0f || !(m.M11 > 0f) || !(m.M22 > 0f)) return;   // axis scales only
            int formatFlags = format != null ? (int)format.FormatFlags : 0;
            bool typographic = format != null && format.IsTypographic;
            float em = EmWorld(font);
            if (!(em > 0f)) return;
            if (layout.Width < 0f || layout.Height < 0f) return;
            if ((formatFlags & 2) != 0)
            {
                VerticalString(s, font, face, mm.Value, em, layout, format, formatFlags, typographic, brush);
                return;
            }
            if ((formatFlags & 1) != 0) return;     // right to left: not modelled
            HorizontalString(s, face, mm.Value, family, style, em, layout, format, formatFlags, typographic, brush);
        }

        /// <summary>FullTextImager::Draw: the layout rectangle into the clip, unless NoClip, for the
        /// time the imager draws; nothing of it is recorded.</summary>
        sealed class SavedClip { public GpRegion Clip; }

        SavedClip PushLayoutClip(RectangleF layout, int formatFlags)
        {
            if ((formatFlags & 0x4000) != 0 || layout.Width == 0f || layout.Height == 0f) return null;
            var saved = new SavedClip { Clip = _state.Clip };
            GpRegion r = GpRegion.FromRect(layout);
            r.Transform(DeviceMatrix);
            GpRegion cur = _state.Clip == null ? GpRegion.Infinite() : _state.Clip.Clone();
            cur.Combine(r, CombineMode.Intersect);
            _state.Clip = cur;
            return saved;
        }

        void PopLayoutClip(SavedClip saved)
        {
            if (saved != null) _state.Clip = saved.Clip;
        }

        void HorizontalString(string s, TrueTypeFont face, GpFontFamily.Metrics fm, string family, int style, float em,
                              RectangleF layout, StringFormat format, int formatFlags, bool typographic, Brush brush)
        {
            bool hotkey = format != null && format.HotkeyPrefix != System.Drawing.Text.HotkeyPrefix.None;
            GpTextLayout L = GpTextLayout.Build(face, fm, s, em, layout.Width, formatFlags, typographic, hotkey);
            GpMat m = WorldToDevice;
            float sx = m.M11, sy = m.M22;
            int upem = face.UnitsPerEmForHinting;
            float res = IdealEm / em;
            int Du(int du) => upem == IdealEm ? du : (int)Math.Round(du * (double)IdealEm / upem);
            int Rnd(float v) => (int)MathF.Floor(v + 0.5f);
            int mode = RealizationMode(face, family, em * sx, sx == sy);
            float lmF = typographic ? 0f : 1f / 6f;
            if (format != null && !typographic) lmF = 1f / 6f;
            int lm = Rnd(lmF * em * res), rmg = lm;
            int W = Rnd(layout.Width * res);
            int align = format != null ? (int)format.Alignment : 0;
            int lineAlign = format != null ? (int)format.LineAlignment : 0;
            int lineH = Du(fm.LineSpacing);
            int ascent = Du(fm.Ascent);
            // The lines that show: with LineLimit only those wholly inside the rectangle's height.
            int H = Rnd(layout.Height * res);
            int count = L.Lines.Count;
            if (layout.Height > 0f)
            {
                bool lineLimit = (formatFlags & 0x2000) != 0;
                int fit = 0;
                for (int k = 0; k < count; k++)
                {
                    int top = k * lineH, bottom = (k + 1) * lineH;
                    if (lineLimit ? bottom <= H : top < H) fit = k + 1;
                }
                if (lineLimit) count = fit;
                else count = Math.Max(1, fit);
            }
            if (count == 0) return;
            int total = count * lineH + (typographic ? 0 : IdealEm / 8);
            int vTop = 0;
            if (layout.Height > 0f)
            {
                if (lineAlign == 1) vTop = (H - total) / 2;
                else if (lineAlign == 2) vTop = H - total;
            }
            else if (lineAlign == 1) vTop = -total / 2;
            else if (lineAlign == 2) vTop = -total;
            int space = face.GlyphIndex(' ');
            int spaceNom = Rnd(em * face.DesignAdvance(space) * res / upem);
            float f78 = res / sx;        // ideal units per device pixel
            SavedClip saved = PushLayoutClip(layout, formatFlags);
            try
            {
                for (int li = 0; li < count; li++)
                {
                    GpTextLayout.Line line = L.Lines[li];
                    int n = line.Glyphs.Count;
                    if (n == 0) continue;
                    var nom = new int[n];
                    for (int k = 0; k < n; k++) nom[k] = (k + 1 < n ? line.X[k + 1] : line.WidthWithSpaces) - line.X[k];
                    var dev = new int[n];
                    var gl = new ushort[n];
                    for (int k = 0; k < n; k++)
                    {
                        gl[k] = (ushort)line.Glyphs[k];
                        float px = DeviceAdvancePx(face, gl[k], em, sx, sy, mode);
                        dev[k] = Rnd(px / sx * res);
                    }
                    int lineLen = line.Width;
                    int share = 0;
                    if (align != 0)
                    {
                        int used = lm + lineLen + rmg;
                        share = W < 1 ? -used : W - used;
                        if (align == 1) share /= 2;
                    }
                    int u0 = share + lm;
                    int m70 = lm, m74 = rmg;
                    int shift = AdjustGlyphAdvances(face, gl, nom, dev, space, spaceNom, formatFlags, align, true, true,
                                                    ref m70, ref m74, em, res, f78, sx);
                    int v = vTop + ascent + li * lineH;
                    // GetDisplayCellOrigin.
                    float cx = layout.X + u0 / res + shift / res;
                    float cy = layout.Y + v / res;
                    cx = MathF.Floor(cx * m.M11 + 0.5f) / m.M11;
                    cy = MathF.Floor(cy * m.M22 + 0.5f) / m.M22;
                    var org = new PointF[n];
                    float x = cx;
                    for (int k = 0; k < n; k++)
                    {
                        org[k] = new PointF(m.M11 * x + m.M21 * cy + m.Dx, m.M12 * x + m.M22 * cy + m.Dy);
                        x = dev[k] / res + x;
                    }
                    var chars = new System.Text.StringBuilder();
                    var map = new ushort[n];
                    for (int k = 0; k < n; k++) { chars.Append(s[line.Chars[k]]); map[k] = (ushort)k; }
                    Rectangle draw = GlyphUnion(face, gl, org, em * sx);
                    if (draw.Width > 0 && draw.Height > 0 && !TotallyClipped(draw))
                    {
                        var gd = new GlyphDraw
                        {
                            Draw = draw, Glyphs = gl, Origins = org, Brush = brush, Text = chars.ToString(), Map = map,
                            LastAdvance = (int)MathF.Floor(DeviceAdvancePx(face, gl[n - 1], em, sx, sy, mode) + 0.5f),
                            Family = family, EmUnits = upem, Style = style & 3,
                            M11 = m.M11 * em / upem, M22 = m.M22 * em / upem,
                        };
                        lock (GpMetaDriverState.Lock)
                            DriverDrawGlyphs(gd);
                    }
                    if ((style & 12) != 0)
                    {
                        int len = line.WidthWithSpaces;
                        float y0 = layout.Y + v / res;
                        if ((style & 4) != 0)
                            DecorationLine(face, brush, em, true, layout.X + u0 / res, y0, len / res, res, horizontal: true);
                        if ((style & 8) != 0)
                            DecorationLine(face, brush, em, false, layout.X + u0 / res, y0, len / res, res, horizontal: true);
                    }
                }
            }
            finally { PopLayoutClip(saved); }
        }

        /// <summary>GdipLscbkDrawUnderline (and the strikeout): a line along the run, off the
        /// baseline by the face's post.underlinePosition (OS/2 strikeout position) in ideal units,
        /// with an aliased pen of the device width.</summary>
        void DecorationLine(TrueTypeFont face, Brush brush, float em, bool underline, float x, float y, float len, float res, bool horizontal)
        {
            int upem = face.UnitsPerEmForHinting;
            int Rnd(float v) => (int)MathF.Floor(v + 0.5f);
            int pos = underline ? -face.UnderlinePosition : face.UnderlinePosition;   // strikeout: not modelled exactly
            int off = Rnd(pos * (em / upem) * res);
            float w = Rnd(face.UnderlineThickness * (em / upem) * res) / res;
            GpMatrix dm = DeviceMatrix;
            float devW = MathF.Max(1f, MathF.Floor(MathF.Abs(w * dm.M11) + 0.5f));
            PointF[] pts = horizontal
                ? new[] { new PointF(x, y + off / res), new PointF(x + len, y + off / res) }
                : new[] { new PointF(x - off / res, y), new PointF(x - off / res, y + len) };
            var path = new GpPath(pts, new byte[] { 0, 1 }, FillMode.Alternate);
            var dp = new DpPen { Width = devW, Unit = 2, Brush = brush };
            Rectangle? draw;
            if (!GpStroke.BoundsToRect(GpStroke.Bounds(path, dm, dp, ContextDpiX), out Rectangle dr)) return;
            draw = dr;
            if (TotallyClipped(draw.Value)) return;
            lock (GpMetaDriverState.Lock)
                DriverStrokePath(draw.Value, path, dp, true);
        }

        /// <summary>GlyphImager::AdjustGlyphAdvances over a whole run at a line's both ends: the
        /// device advances (ideal units) moved towards the nominal ones; returns the cell origin's
        /// shift (+0x220).</summary>
        static int AdjustGlyphAdvances(TrueTypeFont face, ushort[] gl, int[] nom, int[] dev, int space, int spaceNom,
                                       int formatFlags, int align, bool lead, bool trail, ref int m70, ref int m74,
                                       float em, float res, float f78, float sx)
        {
            int n = gl.Length;
            int shift = 0;
            int ls = 0;
            while (ls < n && gl[ls] == space && dev[ls] != 0) { dev[ls] = spaceNom; ls++; }
            int ts = 0, k2 = n, done = ls;
            while (done < n && gl[--k2] == space && dev[k2] != 0) { done++; dev[k2] = spaceNom; ts++; }
            int mid = n - ts - ls;
            if (mid < 2)
            {
                if (mid == 1) dev[ls] = nom[ls];
                return shift;
            }
            if (ls != 0) m70 += spaceNom * ls;
            int spDev = 0, spCount = 0, spNom = 0, nsDev = 0, nsNom = 0, nsCount = 0;
            for (int k = ls; k < ls + mid; k++)
            {
                if (gl[k] == space && dev[k] != 0) { spDev += dev[k]; spCount++; spNom += nom[k]; }
                else { nsDev += dev[k]; nsNom += nom[k]; nsCount++; }
            }
            int delta = spNom - spDev - nsDev + nsNom;
            bool rtlFlag = (formatFlags & 1) != 0 && (formatFlags & 2) == 0;
            int al = align;
            if (rtlFlag) al = al == 0 ? 2 : al == 2 ? 0 : al;
            int Rnd(float v) => (int)MathF.Floor(v + 0.5f);
            if ((formatFlags & 4) == 0 && (lead || trail))
            {
                SideBearings(face, gl, ls, mid, em, sx, out int lsb16, out int rsb16);
                bool skipZero = false;
                if (lead)
                {
                    int v = Rnd(f78 * lsb16 * 0.0625f);
                    if (v < 0)
                    {
                        v += m70; m70 = v;
                        if (v < 0) { m70 = 0; delta += v; shift -= v; }
                    }
                    else if (v > 0 && delta < 0 && al != 0) { delta += v; shift -= v; }
                }
                if (trail)
                {
                    int v = Rnd(f78 * rsb16 * 0.0625f);
                    if (v < 0)
                    {
                        v += m74; m74 = v;
                        if (v < 0) { delta += v; m74 = 0; }
                    }
                    else if (v >= 1 && delta < 0)
                    {
                        if (al != 2) delta += v;
                        else skipZero = true;
                    }
                }
                if (!skipZero && delta == 0) return shift;
            }
            else if (delta == 0) return shift;

            if (nsCount + spCount < 2)
            {
                shift += delta / 2;
                return shift;
            }
            int emIdeal = Rnd(em * res);
            int taken = 0;
            if (al == 0)
            {
                if (trail)
                {
                    if (delta < -m74) { delta += m74; taken = -m74; }
                    else
                    {
                        if (delta < emIdeal) return shift;
                        delta -= emIdeal; taken = emIdeal;
                    }
                }
            }
            else if (al == 1)
            {
                if (lead && trail)
                {
                    int mn = Math.Min(m70, m74);
                    if (-2 * mn <= delta) { shift += delta / 2; return shift; }
                    delta += -2 * mn;
                    shift += mn;
                    taken = mn;
                }
            }
            else if (al == 2 && lead)
            {
                if (delta < -m70) { delta += m70; shift += -m70; }
                else
                {
                    if (delta < emIdeal) { shift += delta; return shift; }
                    delta -= emIdeal; shift += emIdeal;
                }
            }
            int minSp = Rnd(em * res / 6f);
            bool spreadOverSpaces = false;
            if (spCount >= 1)
            {
                if (!(spNom < delta))
                {
                    int lim = Math.Max(spNom / 2, spCount * minSp);
                    if (!(delta < lim - spNom)) spreadOverSpaces = true;
                }
                if (!spreadOverSpaces && delta >= 1)
                {
                    // Latin script: the rest split about the run (the 542c path).
                    shift += delta / 2;
                    return shift;
                }
            }
            else if (delta > 0)
            {
                shift += delta / 2;
                return shift;
            }
            if (spreadOverSpaces)
            {
                int per = spCount != 0 ? (spDev + spCount / 2 + delta) / spCount : 0;
                for (int k = ls; k < ls + mid; k++)
                    if (gl[k] == space && dev[k] != 0) dev[k] = per;
                return shift;
            }
            // Over the gaps between letters.
            int spW = minSp;
            if (spCount == 0) spW = 0;
            else
            {
                if (delta >= 0) spW = (2 * spNom) / spCount;
                delta = spDev - spCount * spW + delta;
            }
            int runs = 0;
            for (int j = 0; j < mid;)
            {
                int i = ls + j;
                if (gl[i] == space && dev[i] != 0)
                {
                    do { j++; } while (j < mid && gl[ls + j] == space && dev[ls + j] != 0);
                    runs++;
                }
                else
                {
                    if (j >= mid) break;
                    while (j < mid && !(gl[ls + j] == space && dev[ls + j] != 0)) j++;
                }
            }
            int gaps = nsCount - runs - 1;
            int px = Rnd(f78);
            int per2, extra;
            if (gaps < 1)
            {
                if (spCount == 0) return shift;
                spW += (delta + spCount / 2) / spCount;
                per2 = 0; extra = 0;
            }
            else if (px < 1)
            {
                per2 = delta / gaps; extra = 0;
            }
            else
            {
                int perPx = delta / px;
                int q = perPx / gaps;
                extra = (-(px * q * gaps) - px / 2 + delta) / px;
                if (extra < 0) { per2 = Rnd((q - 1) * f78); extra += gaps; }
                else per2 = Rnd(q * f78);
            }
            bool prevSpace = gl[ls] == space && dev[ls] != 0;
            for (int j = 1; j <= mid; j++)
            {
                int i = ls + j;
                if (prevSpace)
                {
                    dev[i - 1] = spW;
                    if (j < mid) prevSpace = gl[i] == space && dev[i] != 0;
                }
                else if (j < mid)
                {
                    if (!(gl[i] == space && dev[i] != 0))
                    {
                        int add = extra >= 1 ? px : 0;
                        extra--;
                        dev[i - 1] += add + per2;
                    }
                    prevSpace = gl[i] == space && dev[i] != 0;
                }
            }
            return shift;
        }

        /// <summary>GpFaceRealization::GetGlyphStringSidebearings: the run's least left and right
        /// side bearings in 1/16 pixel, over the glyphs within the line height of each end.</summary>
        static void SideBearings(TrueTypeFont face, ushort[] gl, int from, int count, float em, float sx, out int left, out int right)
        {
            int upem = face.UnitsPerEmForHinting;
            float scale = em * sx / upem;
            GdipText.DeviceAscentDescent(face, em / upem * sx, out int asc, out int desc);
            int lim = (asc + desc) * 32;
            int cum = 0;
            left = lim;
            for (int i = from; i < from + count; i++)
            {
                if (cum >= lim) break;
                GdipText.NaturalMetrics(face, gl[i], em, sx, sx, out int adv, out int lsb, out _);
                left = Math.Min(left, cum + (int)(lsb * scale * 16f));
                cum += (int)(adv * scale * 16f);
            }
            cum = 0;
            right = lim;
            for (int i = from + count - 1; i >= from; i--)
            {
                if (cum >= lim) break;
                GdipText.NaturalMetrics(face, gl[i], em, sx, sx, out int adv, out _, out int rsb);
                right = Math.Min(right, cum + (int)(rsb * scale * 16f));
                cum += (int)(adv * scale * 16f);
            }
        }

        // ---- vertical (one line) --------------------------------------------------------------------

        /// <summary>FullTextImager for one vertical line, as GpGraphics.DrawStringVertical models it
        /// (BuiltLine, the tab stops, EllipsisWord trimming, the cell origins), handed to the metafile
        /// driver run by run: each sideways glyph its own run under GetFontTransform's quarter turn,
        /// the ellipsis upright (DrawPlacedGlyphs does not move a vertical glyph by its origin offsets
        /// on the metafile driver), and each run's underline after it.</summary>
        void VerticalString(string s, Font font, TrueTypeFont face, GpFontFamily.Metrics fm, float em, RectangleF layout,
                            StringFormat format, int formatFlags, bool typographic, Brush brush)
        {
            if ((formatFlags & 0x1000) == 0 || (formatFlags & 1) != 0) return;
            int style = (int)font.Style;
            string family = font.FontFamily.Name;
            int hotkey = format != null ? (int)format.HotkeyPrefix : 0;
            foreach (char c in s)
                if ((c < 0x20 && c != '\t') || c >= 0x590 || (hotkey != 0 && c == '&')) return;
            int trimming = format != null ? (int)format.Trimming : 1;
            float firstTab = 0f;
            float[] tabs = format?.GetTabStops(out firstTab);
            GpMat m = WorldToDevice;
            int upem = face.UnitsPerEmForHinting;
            const int Ideal = GpTextLayout.Ideal;
            float r = Ideal / em;
            static int Rnd(float v) => (int)MathF.Floor(v + 0.5f);
            int Du(int du) => upem == Ideal ? du : (int)Math.Round(du * (double)Ideal / upem);
            int lm = typographic ? 0 : Rnd(em * r / 6f), tm = lm;
            int extent = Rnd(layout.Height * r);
            int room = extent < 1 ? 0x1000000 : Math.Max(0, extent - lm - tm);
            var stops = new List<int>();
            int increment;
            if (tabs != null && tabs.Length > 0)
            {
                float cum = firstTab;
                foreach (float t in tabs) { cum += t; stops.Add(Rnd(r * cum)); }
                increment = Rnd(tabs[tabs.Length - 1] * r);
            }
            else increment = Rnd(r * firstTab);
            int NextStop(int pen)
            {
                foreach (int st in stops) if (st > pen) return st;
                if (increment <= 0) return pen;
                int last = stops.Count > 0 ? stops[stops.Count - 1] : 0;
                while (last <= pen) last += increment;
                return last;
            }
            int n = s.Length;
            var gids = new int[n];
            var penAt = new int[n + 1];
            int pen = 0;
            for (int i = 0; i < n; i++)
            {
                penAt[i] = pen;
                char c = s[i];
                if (c == '\t') { gids[i] = -1; pen = NextStop(pen); continue; }
                int g = face.GlyphIndex(c);
                if (g <= 0) return;
                gids[i] = g;
                int a = face.DesignAdvance(g);
                if (!typographic) a = Rnd(a * 1.03f);
                pen += Du(a);
            }
            penAt[n] = pen;
            int End(int count)
            {
                int k = count;
                while (k > 0 && (s[k - 1] == ' ' || s[k - 1] == '\t')) k--;
                return penAt[k];
            }
            int keep = n;
            bool ellipsis = false;
            if (End(n) > room)
            {
                if (trimming == 1)
                {
                    keep = 0;
                    while (keep < n && penAt[keep + 1] <= room) keep++;
                }
                else if (trimming == 4)
                {
                    int ellW = Du(face.TypoAscender - face.TypoDescender);
                    int room2 = room - ellW;
                    keep = 0;
                    int k = 0;
                    while (k < n)
                    {
                        int w0 = k;
                        while (k < n && s[k] != ' ' && s[k] != '\t') k++;
                        int wordEnd = k;
                        while (k < n && (s[k] == ' ' || s[k] == '\t')) k++;
                        if (penAt[wordEnd] > room2 && w0 > 0) break;
                        if (penAt[wordEnd] > room2) return;
                        keep = k;
                    }
                    ellipsis = true;
                }
            }
            int contentEnd = ellipsis ? penAt[keep] : End(keep);
            int ellAdv = ellipsis ? Du(face.TypoAscender - face.TypoDescender) : 0;
            int L = contentEnd + ellAdv;
            int alignV = format != null ? (int)format.Alignment : 0;
            int lineAlign = format != null ? (int)format.LineAlignment : 0;
            int u0 = lm;
            if (extent >= 1)
            {
                if (alignV == 1) u0 += (extent - (L + lm + tm)) / 2;
                else if (alignV == 2) u0 += extent - (L + lm + tm);
            }
            int lineH = Du(fm.LineSpacing * upem / fm.Em) + (typographic ? 0 : Ideal / 8);
            int across = Rnd(layout.Width * r), vTop = 0;
            if (layout.Width > 0f)
            {
                if (lineAlign == 1) vTop = (across - lineH) / 2;
                else if (lineAlign == 2) vTop = across - lineH;
            }
            int v = vTop + Du(face.WinDescent) + (typographic ? 0 : Ideal / 8);
            PointF Cell(int vv, int uu)
            {
                float x = layout.X + vv / r, y = layout.Y + uu / r;
                x = MathF.Floor(x * m.M11 + 0.5f) / m.M11;
                y = MathF.Floor(y * m.M22 + 0.5f) / m.M22;
                return new PointF(m.M11 * x + m.M21 * y + m.Dx, m.M12 * x + m.M22 * y + m.Dy);
            }
            float k0 = em / upem;
            // GetFontTransform's quarter turn of the realization (cos 90 as a float).
            const float c90 = -4.371139e-08f;
            int ppAlong = GdipText.AxisPpem(em * m.M22), ppAcross = GdipText.AxisPpem(em * m.M11);
            SavedClip saved = PushLayoutClip(layout, formatFlags);
            try
            {
                int i = 0;
                while (i < keep)
                {
                    // A run: a glyph, or a tab.
                    int j = i + 1;
                    if (gids[i] >= 0 && s[i] != ' ')
                    {
                        PointF d = Cell(v, u0 + penAt[i]);
                        var gl = new[] { (ushort)gids[i] };
                        var org = new[] { d };
                        var bits = GdipText.GlyphSideways(face, gids[i], ppAlong, ppAcross);
                        Rectangle draw = GlyphUnion(face, gl, org, em * m.M11, _ => bits);
                        if (draw.Width > 0 && draw.Height > 0 && !TotallyClipped(draw))
                        {
                            GdipText.SidewaysMetrics(face, gids[i], em, m.M11, m.M22, out int advDu, out _);
                            var gd = new GlyphDraw
                            {
                                Draw = draw, Glyphs = gl, Origins = org, Brush = brush, Text = s.Substring(i, 1), Map = new ushort[] { 0 },
                                LastAdvance = (int)MathF.Floor(advDu * (em * m.M22 / upem) + 0.5f),
                                Family = family, EmUnits = upem, Style = style & 3,
                                M11 = c90 * m.M11 * k0, M12 = m.M22 * k0, M21 = -m.M11 * k0, M22 = c90 * m.M22 * k0,
                            };
                            lock (GpMetaDriverState.Lock)
                                DriverDrawGlyphs(gd);
                        }
                    }
                    if ((style & 4) != 0)
                        VerticalUnderline(face, brush, em, r, layout, v, u0 + penAt[i], penAt[j] - penAt[i]);
                    i = j;
                }
                if (ellipsis)
                {
                    PointF d = Cell(v, u0 + contentEnd);
                    var gl = new ushort[] { 0 };
                    var org = new[] { d };
                    var bits = GdipText.Glyph(face, 0, GdipText.AxisPpem(em * m.M11), GdipText.AxisPpem(em * m.M22));
                    Rectangle draw = GlyphUnion(face, gl, org, em * m.M11, _ => bits);
                    if (draw.Width > 0 && draw.Height > 0 && !TotallyClipped(draw))
                    {
                        var gd = new GlyphDraw
                        {
                            Draw = draw, Glyphs = gl, Origins = org, Brush = brush, Text = "…", Map = new ushort[] { 0 },
                            LastAdvance = (int)MathF.Floor(DeviceAdvancePx(face, 0, em, m.M11, m.M22, 5) + 0.5f),
                            Sideways = true,
                            Family = family, EmUnits = upem, Style = style & 3,
                            M11 = m.M11 * k0, M22 = m.M22 * k0,
                        };
                        lock (GpMetaDriverState.Lock)
                            DriverDrawGlyphs(gd);
                    }
                }
            }
            finally { PopLayoutClip(saved); }
        }

        void VerticalUnderline(TrueTypeFont face, Brush brush, float em, float r, RectangleF layout, int v, int u, int len)
        {
            if (len <= 0) return;
            int upem = face.UnitsPerEmForHinting;
            static int Rnd(float x) => (int)MathF.Floor(x + 0.5f);
            int ulOff = Rnd(-face.UnderlinePosition * (em / upem) * r);
            float ulW = Rnd(face.UnderlineThickness * (em / upem) * r) / r;
            GpMatrix dm = DeviceMatrix;
            float devW = MathF.Max(1f, MathF.Floor(MathF.Abs(ulW * dm.M11) + 0.5f));
            float x = layout.X + (v - ulOff) / r;
            var pts = new[] { new PointF(x, layout.Y + u / r), new PointF(x, layout.Y + (u + len) / r) };
            var path = new GpPath(pts, new byte[] { 0, 1 }, FillMode.Alternate);
            var dp = new DpPen { Width = devW, Unit = 2, Brush = brush };
            if (!GpStroke.BoundsToRect(GpStroke.Bounds(path, dm, dp, ContextDpiX), out Rectangle dr)) return;
            if (TotallyClipped(dr)) return;
            lock (GpMetaDriverState.Lock)
                DriverStrokePath(dr, path, dp, true);
        }
    }
}
