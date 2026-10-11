// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The metafile driver's text (gdiplus.dll 10.0.26100, arm64, public PDB): every imager hands its
// placed glyphs to GpGraphics::DrawPlacedGlyphs @180010398, which on the metafile driver measures
// the glyphs' bitmaps (GetGlyphPos: the draw rectangle is their union, nothing when it is empty),
// takes the last glyph's device advance rounded, and calls DriverMeta::DrawGlyphs @1800d41e0:
//
//   SplitTransform @180168fa8   the realization's font matrix into a scale and an angle; the
//                               escapement is round(3600 - angle * 1800 / pi) less 3600 past 3599
//   SetBkMode(TRANSPARENT), SetTextColor(ToCOLORREF(brush)), SetTextAlign(TA_BASELINE)
//   the HFONT the context keeps (+0x2a8), made again when the face, the font matrix or the
//                               style changed (DpContext::UpdateCurrentHFont @1800de7b0: height
//                               -round(em units * scale y), escapement and orientation the angle
//                               -- a sideways run's less 900 and an '@' face --, weight 400/700,
//                               italic, OUT_TT_ONLY_PRECIS, the quality from the context's text
//                               hint (0 -> 0, 1-2 -> NONANTIALIASED, 3-4 -> ANTIALIASED, 5 ->
//                               CLEARTYPE), GpFontFace::GetCharset @1800865c0; the old one deleted
//                               by DeleteCurrentHFont @180227160), selected (SelectCurrentHFont
//                               @180227530)
//   SetupClipping / RestoreClipping around the drawing, the draw rectangle the glyphs' union
//   with a string (DrawString, a DrawDriverString with CmapLookup): GdiString @1800d53e0
//                               -- characters with ExtTextOutW, one call when every glyph shares
//                               its baseline (advances: each cluster's glyph steps rounded, the
//                               last the device advance, a cluster of several characters split
//                               evenly with the rest on its last), else one call per cluster at
//                               its glyph's rounded origin with no advances; a string with no map
//                               one call per run of equal y, the advances the rounded steps
//   without (glyph indices): DpDriver::GdiText @1800a4530 -- ETO_GLYPH_INDEX, one call with the
//                               differences of the rounded origins when the run is level and
//                               unrotated, else one per glyph
//   the old font selected back (DpContext::ReleaseTextOutputHdc @1800de768)
//

using System.Collections.Generic;

namespace System.Drawing.WebGpuBackend.Gdip
{
    internal sealed partial class GpMetafileRecorder
    {
        /// <summary>DrawGlyphData, as DrawPlacedGlyphs fills it for the metafile driver.</summary>
        internal sealed class GlyphDraw
        {
            public Rectangle Draw;          // +0x10: the glyph bitmaps' union, device pixels
            public ushort[] Glyphs;         // +0x40
            public ushort[] Map;            // +0x48: character -> glyph, or null
            public PointF[] Origins;        // +0x50: device
            public string Text;             // +0x60: the characters, or null
            public int LastAdvance;         // +0x70: the last (first, right to left) glyph's device advance
            public bool RightToLeft;        // +0x74
            public bool Sideways;           // +0x78 bit 31
            public Brush Brush;             // +0x30
            // the realization (+0x38): its face and font matrix (design units to device)
            public string Family;
            public int EmUnits;
            public int Style;               // GpFontFace::GetFaceStyle: bold 1, italic 2
            public float M11, M12, M21, M22, Dx, Dy;
        }

        // DpContext's text HFONT and what it was made for (+0x2a8 .. +0x2f0).
        GpEmfDc.FontObject _hfont;
        string _hfontFamily;
        float[] _hfontMatrix;
        int _hfontStyle;

        /// <summary>SplitTransform: a matrix's x scale, angle (radians, y down), y scale.</summary>
        static void SplitTransform(float a, float b, float c, float d, out float sx, out float angle, out float sy)
        {
            float n = b * b + a * a;
            sx = MathF.Sqrt(n);
            bool nonNeg;
            if (b < 0f)
            {
                nonNeg = float.IsNaN(a) || 0f <= a;
            }
            else
            {
                nonNeg = 0f <= a;
                if (0f < a) { angle = (float)Math.Atan(b / a); goto done; }
            }
            if (nonNeg)
            {
                if (0f <= b || a <= 0f) { angle = b <= 0f ? 4.71238899230957f : 1.5707963705062866f; goto done; }
                angle = (float)(Math.Atan(b / a) + 6.283185307179586);
            }
            else angle = (float)(Math.Atan(b / a) + 3.141592653589793);
        done:
            sy = (d * a - c * b) * sx / n;
        }

        /// <summary>The context's TextRenderingHint as a LOGFONT quality.</summary>
        int TextQuality()
        {
            int h = _state.TextHint;
            if (h == 0) return 0;
            if (h == 1 || h == 2) return 3;
            if (h == 3 || h == 4) return 4;
            return h == 5 ? 5 : 0;
        }

        /// <summary>DpContext::UpdateCurrentHFont.</summary>
        void UpdateCurrentHFont(GpEmfDc dc, GlyphDraw g, float scaleY, int escapement)
        {
            TrueTypeFontOf(g.Family, g.Style, out var face);
            byte charset = GpGdiFont.Charset(face);
            if (_hfont != null) dc.DeleteObject(_hfont);
            _hfont = null;
            int height = (int)MathF.Floor((float)g.EmUnits * scaleY + 0.5f);
            int esc = escapement;
            if (g.Sideways)
            {
                esc = escapement - 900;
                if (esc < 0) esc = escapement + 2700;
            }
            string name = g.Sideways ? "@" + g.Family : g.Family;
            if (name.Length + 1 > 32) return;
            var lf = new byte[92];
            Le.W32(lf, 0, -height);
            Le.W32(lf, 8, esc);
            Le.W32(lf, 12, esc);
            Le.W32(lf, 16, (g.Style & 1) != 0 ? 700 : 400);
            lf[20] = (byte)((g.Style & 2) != 0 ? 1 : 0);
            lf[23] = charset;
            lf[24] = 7;
            lf[26] = (byte)TextQuality();
            for (int i = 0; i < name.Length; i++) Le.W16(lf, 28 + i * 2, name[i]);
            _hfont = GpEmfDc.CreateFontIndirect(lf);
        }

        static void TrueTypeFontOf(string family, int style, out Microsoft.Wpf.Interop.WebGpu.Composition.Text.TrueTypeFont face)
            => face = Microsoft.Wpf.Interop.WebGpu.Composition.Text.GdiPlusText.Face(family, style & 3);

        /// <summary>DriverMeta::DrawGlyphs.</summary>
        void DriverDrawGlyphs(GlyphDraw g)
        {
            GpEmfDc dc = ContextHdc();
            SplitTransform(g.M11, g.M12, g.M21, g.M22, out _, out float angle, out float scaleY);
            float t = (float)(3600.0 - (double)angle * 1800.0 / 3.141592653589793) + 0.5f;
            int tenths = (int)MathF.Floor(t);
            if (tenths > 0xe0f) tenths -= 0xe10;
            dc.SetBkMode(1);
            dc.SetTextColor(ToColorRef(g.Brush));
            dc.SetTextAlign(0x18);

            string text = g.Text;
            ushort[] map = g.Map;
            int len = text?.Length ?? 0;
            bool useString = text != null && len > 0;
            if (useString && map != null && text.IndexOf('￿') >= 0)
            {
                // The characters GDI+ marked hidden (0xffff) left out, the map with them.
                var sb = new System.Text.StringBuilder();
                var m2 = new List<ushort>();
                for (int i = 0; i < len; i++)
                    if (text[i] != '￿') { sb.Append(text[i]); m2.Add(map[i]); }
                if (sb.Length == 0) return;
                text = sb.ToString(); map = m2.ToArray(); len = text.Length;
            }

            var matrix = new[] { g.M11, g.M12, g.M21, g.M22, g.Dx, g.Dy };
            bool same = _hfont != null && _hfontFamily == g.Family && _hfontStyle == g.Style && _hfontMatrix != null;
            if (same)
                for (int i = 0; i < 6; i++) if (_hfontMatrix[i] != matrix[i]) { same = false; break; }
            if (!same)
            {
                _hfontFamily = g.Family; _hfontMatrix = matrix; _hfontStyle = g.Style;
                UpdateCurrentHFont(dc, g, scaleY, tenths);
                if (_hfont == null) return;
            }
            GpEmfDc.GdiObject old = dc.SelectObject(_hfont);
            bool saved = SetupClipping(dc, g.Draw);
            if (useString) GdiString(dc, text, g.Glyphs, map, g.Origins, tenths, g.LastAdvance, g.RightToLeft);
            else GdiText(dc, tenths, g.Glyphs, g.Origins);
            RestoreClipping(dc, saved);
            dc.SelectObject(old);
        }

        static int Rnd(float v) => (int)MathF.Floor(v + 0.5f);   // frintm + fcvtzs

        /// <summary>DriverMeta::GdiString.</summary>
        static void GdiString(GpEmfDc dc, string s, ushort[] glyphs, ushort[] map, PointF[] p, int angle, int lastAdvance, bool rtl)
        {
            int n = s.Length, ng = glyphs.Length;
            if (n < 1) return;
            if (map == null)
            {
                // Runs of one baseline, each its own call; the advances the rounded steps.
                var dx = new int[n];
                dx[n - 1] = 0;
                int i = 0;
                while (i < n)
                {
                    float y0 = p[i].Y;
                    int j = i;
                    while (++j < n)
                    {
                        int step = Math.Abs(Rnd(p[j].X - p[j - 1].X));
                        dx[j - 1] = step;
                        if (p[j].Y != y0) break;
                    }
                    var seg = new int[j - i];
                    Array.Copy(dx, i, seg, 0, j - i);
                    dc.ExtTextOutW(Rnd(p[i].X), Rnd(p[i].Y), 0, null, s.Substring(i, j - i), seg);
                    i = j;
                }
                return;
            }
            bool level = false;
            if (angle == 0)
            {
                int k = 1;
                while (k < ng && Math.Abs(Rnd(p[k].Y - p[k - 1].Y)) == 0) k++;
                level = k >= ng;
            }
            if (level)
            {
                var dx = new int[n];
                int i = 0;
                while (i < n)
                {
                    int g0 = map[i];
                    int j = i;
                    int next;
                    while (true)
                    {
                        j++;
                        if (j >= n) { next = ng; break; }
                        if (map[j] != g0) { next = map[j]; break; }
                    }
                    int w;
                    if (!rtl)
                    {
                        int last = Math.Min(next, ng - 1);
                        w = next > ng - 1 ? lastAdvance : 0;
                        for (int k = g0; k < last; k++) w += Rnd(p[k + 1].X - p[k].X);
                    }
                    else
                    {
                        int k0 = g0 == 0 ? 1 : g0;
                        w = g0 == 0 ? lastAdvance : 0;
                        for (int k = k0; k < next; k++) w += Rnd(p[k - 1].X - p[k].X);
                    }
                    int c = j - i;
                    if (c < 2) dx[i] = w;
                    else
                    {
                        int each = w / c;
                        for (int k = i; k < j; k++) dx[k] = each;
                        dx[j - 1] += w - each * c;
                    }
                    i = j;
                }
                float ox = rtl ? p[ng - 1].X : p[0].X;
                dc.ExtTextOutW(Rnd(ox), Rnd(p[0].Y), rtl ? 0x80 : 0, null, s, dx);
                return;
            }
            if (!rtl)
            {
                int i = 0;
                while (i < n)
                {
                    while (i < n && glyphs[map[i]] == 0xffff) i++;
                    if (i >= n) return;
                    int j = i;
                    while (++j < n && map[i] == map[j] && glyphs[map[j]] != 0xffff) { }
                    PointF o = p[map[i]];
                    dc.ExtTextOutW(Rnd(o.X), Rnd(o.Y), 0, null, s.Substring(i, j - i), null);
                    i = j;
                }
            }
            else
            {
                PointF o = p[ng - 1];
                dc.ExtTextOutW(Rnd(o.X), Rnd(o.Y), 0x80, null, s, null);
            }
        }

        /// <summary>DpDriver::GdiText: glyph indices.</summary>
        static void GdiText(GpEmfDc dc, int angle, ushort[] glyphs, PointF[] p)
        {
            int n = glyphs.Length;
            if (n >= 2 && angle == 0)
            {
                int k = 1;
                while (k < n && Math.Abs(Rnd(p[k].Y - p[k - 1].Y)) == 0) k++;
                if (k == n)
                {
                    var dx = new int[n];
                    int prev = Rnd(p[0].X);
                    for (int i = 0; i < n - 1; i++) { int x = Rnd(p[i + 1].X); dx[i] = x - prev; prev = x; }
                    dx[n - 1] = 0;
                    var cs = new char[n];
                    for (int i = 0; i < n; i++) cs[i] = (char)glyphs[i];
                    dc.ExtTextOutW(Rnd(p[0].X), Rnd(p[0].Y), 0x10, null, new string(cs), dx);
                    return;
                }
            }
            for (int i = 0; i < n; i++)
            {
                if (glyphs[i] == 0xffff) continue;
                dc.ExtTextOutW(Rnd(p[i].X), Rnd(p[i].Y), 0x10, null, ((char)glyphs[i]).ToString(), null);
            }
        }
    }
}
