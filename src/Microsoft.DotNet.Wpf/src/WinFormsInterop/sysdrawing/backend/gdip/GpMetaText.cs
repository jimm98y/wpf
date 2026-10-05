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

        /// <summary>The device box of glyphs realized under a turned matrix: each outline's points
        /// (design units, y down, through em / upem and the matrix's linear part) at its origin,
        /// the union put out to whole pixels.</summary>
        static readonly int s_turnPad = int.TryParse(Environment.GetEnvironmentVariable("WF_TURN_PAD"), out int tp) ? tp : 1;

        static Rectangle TurnedUnion(TrueTypeFont face, ushort[] glyphs, PointF[] o, float k, GpMat m)
        {
            float l = float.MaxValue, t = float.MaxValue, r = float.MinValue, b = float.MinValue;
            for (int i = 0; i < glyphs.Length; i++)
            {
                if (glyphs[i] == 0xffff) continue;
                float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
                foreach ((System.Numerics.Vector2[] pts, bool[] _) in face.DesignContours(glyphs[i]))
                    foreach (System.Numerics.Vector2 p in pts)
                    {
                        x0 = Math.Min(x0, p.X); x1 = Math.Max(x1, p.X); y0 = Math.Min(y0, p.Y); y1 = Math.Max(y1, p.Y);
                    }
                if (!(x0 <= x1)) continue;
                // The glyph's box, its four corners through the matrix.
                foreach ((float px, float py) in new[] { (x0, y0), (x1, y0), (x0, y1), (x1, y1) })
                {
                    float x = px * k, y = -py * k;
                    float dx = x * m.M11 + y * m.M21 + o[i].X, dy = x * m.M12 + y * m.M22 + o[i].Y;
                    l = Math.Min(l, dx); r = Math.Max(r, dx); t = Math.Min(t, dy); b = Math.Max(b, dy);
                }
            }
            if (!(l < r) || !(t < b)) return Rectangle.Empty;
            int il = (int)MathF.Floor(l) - s_turnPad, it = (int)MathF.Floor(t) - s_turnPad, ir = (int)MathF.Ceiling(r) + s_turnPad, ib = (int)MathF.Ceiling(b) + s_turnPad;
            return new Rectangle(il, it, ir - il, ib - it);
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

        /// <summary>GpGraphics::DrawString on a metafile, down-level: always FullTextImager (the
        /// same GpFullTextImager the surfaces draw with), its glyph runs handed to the metafile
        /// driver.</summary>
        void GdiDrawString(string s, Font font, RectangleF layout, StringFormat format, Brush brush)
        {
            if (!Gdi || string.IsNullOrEmpty(s)) return;
            float em = EmWorld(font);
            if (!(em > 0f)) return;
            if (layout.Width < 0f || layout.Height < 0f) return;
            var fti = new GpFullTextImager(s, layout.Width, layout.Height, font.FontFamily.Name, (int)font.Style, em,
                                           GpTextFormat.From(format));
            if (!fti.Valid) return;
            fti.Draw(new MetaTarget(this, brush), layout.Location);
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

        /// <summary>The full imager's target on a metafile: DrawPlacedGlyphs through DriverMeta's
        /// DrawGlyphs (the realization the system's ClearType makes; a sideways glyph of vertical
        /// text under GetFontTransform's quarter turn, an upright one unturned), the decoration
        /// lines through the driver's StrokePath with an aliased pen.</summary>
        sealed class MetaTarget : IGpTextTarget
        {
            readonly GpMetafileRecorder _r; readonly Brush _brush;
            public MetaTarget(GpMetafileRecorder r, Brush brush) { _r = r; _brush = brush; }

            public GpMatrix? WorldToDevice => _r.DeviceMatrix;

            public int RealizationMode(TrueTypeFont face, string family, float emDevice, bool square)
                => _r.RealizationMode(face, family, emDevice, square);

            public void DrawPlacedGlyphs(GpFullTextImager.Run run, int mode, ushort[] glyphs, PointF[] o, string chars, ushort[] map, int flags)
            {
                GpTextTrace.ReportPlaced(mode, run.Em, glyphs, o);
                if (glyphs.Length == 0) return;
                GpMat m = _r.WorldToDevice;
                TrueTypeFont face = run.Face;
                float em = run.Em;
                int upem = face.UnitsPerEmForHinting;
                float k0 = em / upem;
                bool vertical = (run.ItemFlags & 0x20) != 0;
                bool sideways = vertical && (run.ItemFlags & 0x8) == 0;
                // GetFontTransform's quarter turn of the realization (cos 90 as a float).
                const float c90 = -4.371139e-08f;
                Func<int, NaturalClearType.GlyphBits> bitsOf = null;
                Rectangle? turnedDraw = null;
                int last;
                if (sideways) {
                    int ppAlong = GdipText.AxisPpem(em * m.M22), ppAcross = GdipText.AxisPpem(em * m.M11);
                    bitsOf = i => GdipText.GlyphSideways(face, glyphs[i], ppAlong, ppAcross);
                    GdipText.SidewaysMetrics(face, glyphs[glyphs.Length - 1], em, m.M11, m.M22, out int advDu, out _);
                    last = (int)MathF.Floor(advDu * (em * m.M22 / upem) + 0.5f);
                } else {
                    if (vertical) bitsOf = i => GdipText.Glyph(face, glyphs[i], GdipText.AxisPpem(em * m.M11), GdipText.AxisPpem(em * m.M22));
                    float sx = MathF.Sqrt(m.M11 * m.M11 + m.M12 * m.M12), sy = MathF.Sqrt(m.M21 * m.M21 + m.M22 * m.M22);
                    float tl = MathF.Max(sx, sy) / 65536f;
                    bool axis = MathF.Abs(m.M12) <= tl && MathF.Abs(m.M21) <= tl;
                    bool quarter = MathF.Abs(m.M11) <= tl && MathF.Abs(m.M22) <= tl;
                    int lastMode = axis || quarter ? mode : 2;
                    last = (int)MathF.Floor(GpTextShaper.DeviceAdvancePx(face, glyphs[glyphs.Length - 1], em, sx, sy, lastMode) + 0.5f);
                    if (!axis && !vertical)
                    {
                        // A turned realization: a clockwise quarter turn is the sideways glyph;
                        // any other turn bounded by its outline through the matrix.
                        if (quarter && m.M12 > 0f && m.M21 < 0f)
                        {
                            int pa = GdipText.AxisPpem(em * sx), pc = GdipText.AxisPpem(em * sy);
                            bitsOf = i => GdipText.GlyphSideways(face, glyphs[i], pa, pc);
                        }
                        else turnedDraw = TurnedUnion(face, glyphs, o, k0, m);
                    }
                }
                Rectangle draw = turnedDraw ?? GlyphUnion(face, glyphs, o, em * m.M11, bitsOf);
                if (draw.Width <= 0 || draw.Height <= 0 || _r.TotallyClipped(draw)) return;
                var gd = new GlyphDraw {
                    Draw = draw, Glyphs = glyphs, Origins = o, Brush = _brush, Text = chars, Map = map,
                    LastAdvance = last, Family = run.Family, EmUnits = upem, Style = run.Style & 3,
                };
                if (sideways) { gd.M11 = c90 * m.M11 * k0; gd.M12 = m.M22 * k0; gd.M21 = -m.M11 * k0; gd.M22 = c90 * m.M22 * k0; }
                else { gd.M11 = m.M11 * k0; gd.M12 = m.M12 * k0; gd.M21 = m.M21 * k0; gd.M22 = m.M22 * k0; gd.Sideways = vertical; }
                lock (GpMetaDriverState.Lock)
                    _r.DriverDrawGlyphs(gd);
            }

            public void DrawLine(float devicePenWidth, PointF a, PointF b)
            {
                GpTextTrace.ReportLine(devicePenWidth, a, b);
                var path = new GpPath(new[] { a, b }, new byte[] { 0, 1 }, FillMode.Alternate);
                var dp = new DpPen { Width = devicePenWidth, Unit = 2, Brush = _brush };
                if (!GpStroke.BoundsToRect(GpStroke.Bounds(path, _r.DeviceMatrix, dp, _r.ContextDpiX), out Rectangle dr)) return;
                if (_r.TotallyClipped(dr)) return;
                lock (GpMetaDriverState.Lock)
                    _r.DriverStrokePath(dr, path, dp, true);
            }

            public object PushClip(RectangleF layout) => _r.PushLayoutClip(layout, 0);

            public void PopClip(object saved) => _r.PopLayoutClip((SavedClip)saved);
        }
    }
}
