// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// GpGraphics::DrawString on a bitmap: the string laid out the way gdiplus.dll lays it out
// (GdiPlusText.Layout, FastTextImager) and its glyphs composited the way DpDriver::DrawGlyphs
// @1800a43b0 composites them into the surface:
//
//   DpDriver::SolidText @1800a5960 / BrushText @1800a3de0   by render mode:
//     1, 2  DWriteOutputSolidNormalTextOptimized @1800a42c0 -- the premultiplied brush where a glyph
//           bit is set, through the ordinary blend (EpScanBufferNative, scan type 0); with another
//           brush, DWriteOutputBrushNormalText @1800a3e50: the brush's span over each run of set bits
//     3, 4  OutputSolidAntiAliasText8BPPOptimized -- coverage 0..15 by max over the glyphs, each
//           level's colour from TextColorGammaTable::CreateTextColorGammaTable @180024a20
//           (PremultiplyWithCoverage of the brush by 255 - Inv[contrast][255 - 17k]), the ordinary
//           blend; with another brush, OutputBrushAntiAliasText8BPP @1800a4948: the brush's span,
//           each pixel GpColor::MultiplyCoverage'd by the same table value
//     5     OutputSolidClearTypeText @1800a5338 -- the levels composed (RenderGlyph), each row through
//           the clip into a ClearType scan (type 3) that CTBlendSolid's into the canonical ARGB
//           destination; with another brush OutputBrushClearTypeText @1800a4db0 (scan type 2,
//           ClearTypeBlend<ClearTypeCARGBBlend>) with the brush span's colours
//   FastTextImager::DrawString @180038298   the layout rectangle intersected into the clip
//           (GpGraphics::SetClip(rect, Intersect)) when the text spills out of it, put back after
//
// Hint resolution (GpGraphics::CalculateTextRenderingHintInternal @1800100c0): SystemDefault is
// ClearTypeGridFit (a ClearType desktop, the same answer on every platform); a surface of 8bpp or
// less draws SingleBitPerPixelGridFit.
//

using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using Microsoft.Wpf.Interop.WebGpu.Composition.Text;
using GdipText = Microsoft.Wpf.Interop.WebGpu.Composition.Text.GdiPlusText;

namespace System.Drawing.WebGpuBackend.Gdip
{
    internal sealed partial class GpGraphics
    {
        static readonly bool s_trace = Environment.GetEnvironmentVariable ("WF_GPTEXT_TRACE") == "1";

        /// <summary>The hint DrawString realizes with (CalculateTextRenderingHintInternal).</summary>
        int ResolvedTextHint ()
        {
            int h = (int) _ctx.TextHint;
            if (Image.GetPixelFormatSize (Frame.Format) <= 8) return GdipText.HintSingleBitPerPixelGridFit;
            return h == 0 ? GdipText.HintClearTypeGridFit : h;
        }

        /// <summary>GpGraphics::DrawString. False where this engine does not draw the string (GDI+'s
        /// full imager, a transform the fast imager refuses, a brush it cannot fill with): the
        /// caller draws it its old way.</summary>
        public bool DrawString (string s, string family, int style, float sizePt, Brush brush, RectangleF layout,
                                int formatFlags, bool typographic, int align, int lineAlign, bool hotkey)
        {
            if (string.IsNullOrEmpty (s) || brush == null || !CanFill (brush)) return false;
            GpMatrix m = WorldToDevice;
            TrueTypeFont font = GdipText.Face (family, style);
            if (font == null) return false;
            int hint = ResolvedTextHint ();
            if (s_trace) Console.Error.WriteLine ($"GPTEXT DrawString '{s}' {family} {sizePt}pt m=[{m.M11} {m.M12} {m.M21} {m.M22} {m.Dx} {m.Dy}] hint={hint} flags={formatFlags:x}");
            // FastTextImager::Initialize: a positive axis scale only (m11 > 0, m12 = m21 = 0,
            // m22 != 0; m22 > 0 modelled). A turned or sheared transform goes the full imager's way.
            bool axisScale = m.M12 == 0f && m.M21 == 0f && m.M11 > 0f && m.M22 > 0f;
            bool identity = axisScale && m.M11 == 1f && m.M22 == 1f;
            GdipText.Run run = null;
            if (axisScale && !identity)
                run = GdipText.Layout (font, family, sizePt, s, layout.X, layout.Y, layout.Width, layout.Height,
                                       formatFlags, typographic, align, lineAlign, hotkey, hint,
                                       DpiY, biLevel: true, sx: m.M11, sy: m.M22);
            if (!identity) {
                if (run != null) { DrawRun (font, run, brush, run.HasClip); return true; }
                return DrawTransformed (s, font, family, style, sizePt, brush, layout, formatFlags, typographic, align, lineAlign, hotkey, hint);
            }
            run = GdipText.Layout (font, family, sizePt, s, layout.X, layout.Y, layout.Width, layout.Height,
                                   formatFlags, typographic, align, lineAlign, hotkey, hint,
                                   DpiY, biLevel: true);
            if (run == null)
                return DrawLines (s, font, family, style, sizePt, brush, layout, formatFlags, typographic, align, lineAlign, hotkey, hint);
            DrawRun (font, run, brush, run.HasClip);
            return true;
        }

        /// <summary>GpGraphics::DrawDriverString @1800ea638 -> DriverStringImager (ctor @1800e9a00,
        /// Draw @1800ea508, DrawGlyphRange @1800ea708): a font with an underline or a strikeout is
        /// refused (status 2); each origin is the caller's world point through the world-to-device
        /// transform alone (GetDriverStringGlyphOrigins @1800eab20, without RealizedAdvance); the
        /// realization's matrix is world-to-device * em / upem * the record's matrix, of which only
        /// the 2x2 part reaches the face (FD_XFORM), so a translation in it moves nothing; and the
        /// glyphs go to DrawPlacedGlyphs at those device origins, x snapped to a sixth by
        /// GetGlyphPos, y as it falls. Modelled for ClearType under an axis scale, without the
        /// vertical and realized-advance options; false otherwise.</summary>
        public bool DrawDriverString (ushort[] glyphs, string family, int style, float sizePt, Brush brush,
                                      PointF[] positions, int options, Matrix matrix)
        {
            if (brush == null || !CanFill (brush) || (options & ~1) != 0) return false;
            if ((style & 12) != 0) return true;   // InvalidParameter: GDI+ draws nothing
            if (glyphs.Length == 0 || positions.Length < glyphs.Length) return false;
            GpMatrix m = WorldToDevice;
            if (m.M12 != 0f || m.M21 != 0f || !(m.M11 > 0f) || !(m.M22 > 0f)) return false;
            float sx = m.M11, sy = m.M22;
            if (matrix != null) {
                float[] e = matrix.Elements;
                if (e [1] != 0f || e [2] != 0f || !(e [0] > 0f) || !(e [3] > 0f)) return false;
                sx *= e [0]; sy *= e [3];
            }
            TrueTypeFont font = GdipText.Face (family, style & 3);
            if (font == null || font.SynthesizesBold) return false;
            if (ResolvedTextHint () != GdipText.HintClearTypeGridFit) return false;
            float em = sizePt * (DpiY / 72f);
            if (!(em > 0f)) return false;
            if (sx == sy && font.EmbeddedBitmapCount ((int) MathF.Floor (em * sx + 0.5f)) > 100) return false;
            if (string.Equals (family, "Marlett", StringComparison.OrdinalIgnoreCase)) return false;
            var run = new GdipText.Run { Em = em, Mode = 5, Hint = 5, FixedFilter = font.GdiContrastPalette,
                                         Sx = sx, Sy = sy, Contrast = _ctx.TextContrast };
            var gids = new System.Collections.Generic.List<ushort> ();
            var xs = new System.Collections.Generic.List<float> ();
            var ys = new System.Collections.Generic.List<float> ();
            for (int i = 0; i < glyphs.Length; i++) {
                int gid = (options & 1) != 0 ? font.GlyphIndex ((char) glyphs [i]) : glyphs [i];
                if (gid == 0xffff) continue;
                PointF d = m.Transform (positions [i]);
                gids.Add ((ushort) gid); xs.Add (d.X); ys.Add (d.Y);
            }
            run.Glyphs = gids.ToArray ();
            if (run.Glyphs.Length == 0) return true;
            var bits = new NaturalClearType.GlyphBits [run.Glyphs.Length];
            for (int i = 0; i < bits.Length; i++) bits [i] = run.GlyphBits (font, i);
            GdipText.Levels lv = GdipText.Compose (bits, xs.ToArray (), ys.ToArray (), 0f, run.FixedFilter);
            if (lv.Width == 0 || lv.Height == 0) return true;
            OutputText (lv, 5, brush, run.Contrast);
            return true;
        }

        /// <summary>One fast-imager run into the surface: its glyphs composed for the render mode,
        /// the layout rectangle intersected into the clip when the run asks.</summary>
        void DrawRun (TrueTypeFont font, GdipText.Run run, Brush brush, bool clipToLayout)
        {
            GpMatrix m = WorldToDevice;
            run.Contrast = _ctx.TextContrast;
            if (run.Glyphs.Length == 0) return;
            // GetDeviceBaselineOrigin: the world origin through the transform (an axis scale and
            // a translation), then FastDrawGlyphsGridFit rounds the device x.
            float[] xs = GdipText.GlyphXs (run, run.Sx == 1f ? run.OriginX + m.Dx : m.M11 * run.OriginX + m.Dx);
            float y = run.Sy == 1f ? run.OriginY + m.Dy : m.M22 * run.OriginY + m.Dy;
            if (s_trace) Console.Error.WriteLine ($"GPTEXT mode={run.Mode} y={y} xs={string.Join (",", xs)} g={string.Join (",", run.Glyphs)}");

            GdipText.Levels lv;
            switch (run.Mode) {
            case 5: {
                var bits = new NaturalClearType.GlyphBits [run.Glyphs.Length];
                for (int i = 0; i < bits.Length; i++) bits [i] = run.GlyphBits (font, i);
                lv = GdipText.Compose (bits, xs, y, run.FixedFilter);
                break;
            }
            case 3: case 4:
                lv = GdipText.ComposeGrey (font, run.Glyphs, run.Em, xs, y);
                break;
            default:
                lv = GdipText.ComposeMono (font, run.Glyphs, run.Em, xs, y, gridFit: run.Mode == 1);
                break;
            }
            if (lv.Width == 0 || lv.Height == 0) return;

            GpRegion saved = null;
            bool clipped = false;
            if (clipToLayout) {
                saved = _ctx.AppClip?.Clone ();
                clipped = true;
                CombineClip (new RectangleF (run.ClipX, run.ClipY, run.ClipW, run.ClipH), CombineMode.Intersect);
            }
            try {
                OutputText (lv, run.Mode, brush, run.Contrast);
            } finally {
                if (clipped) { _ctx.AppClip = saved; UpdateVisibleClip (); }
            }
        }

        /// <summary>FullTextImager under a transform that is not an axis scale: the nominal layout
        /// (GpTextLayout, the ideal-unit origins GraphicsPath.AddString uses) taken to the device,
        /// each glyph's outline turned with the transform and not fitted. Modelled for the
        /// antialiased realizations; the others keep the old path.</summary>
        bool DrawTransformed (string s, TrueTypeFont font, string family, int style, float sizePt, Brush brush, RectangleF layout,
                              int formatFlags, bool typographic, int align, int lineAlign, bool hotkey, int hint)
        {
            if (hint != GdipText.HintAntiAlias && hint != GdipText.HintAntiAliasGridFit) return false;
            if ((formatFlags & 0x40000003) != 0 || font.SynthesizesBold || font.SynthesizesOblique) return false;
            foreach (char c in s)
                if ((c < 0x20 && c != (char) 10 && c != (char) 13) || (c >= 0x590 && c < 0x1E00) || char.IsSurrogate (c)) return false;
            GpFontFamily.Metrics? mm = GpFontFamily.Get (family, (FontStyle) (style & 3));
            if (mm == null) return false;
            GpFontFamily.Metrics fm = mm.Value;
            GpMatrix m = WorldToDevice;
            float em = sizePt * (DpiY / 72f);
            GpTextLayout L = GpTextLayout.Build (font, fm, s, em, layout.Width, formatFlags, typographic, hotkey);
            int margin = typographic ? 0 : GpTextLayout.IdealMargin;
            int yTop = 0;
            if (lineAlign != 0 && layout.Height > 0f) {
                double rh = layout.Height * GpTextLayout.Ideal / (double) em;
                double th = L.Lines.Count * (double) fm.LineSpacing * GpTextLayout.Ideal / fm.Em + (typographic ? 0 : GpTextLayout.Ideal / 8);
                yTop = (int) Math.Floor (lineAlign == 1 ? (rh - th) / 2 : rh - th);
            }
            var gids = new System.Collections.Generic.List<ushort> ();
            var xs = new System.Collections.Generic.List<float> ();
            var ys = new System.Collections.Generic.List<float> ();
            for (int li = 0; li < L.Lines.Count; li++) {
                GpTextLayout.Line line = L.Lines [li];
                int xIdeal = margin;
                if (align != 0 && layout.Width > 0f) {
                    double rw = layout.Width * GpTextLayout.Ideal / (double) em;
                    xIdeal += (int) Math.Floor (align == 1 ? (rw - line.Width - 2 * margin) / 2 : rw - line.Width - 2 * margin);
                }
                int baseIdeal = (int) Math.Round ((fm.Ascent + li * (double) fm.LineSpacing) * GpTextLayout.Ideal / fm.Em) + yTop;
                float oy = layout.Y + (float) (baseIdeal * (double) em / GpTextLayout.Ideal);
                for (int g = 0; g < line.Glyphs.Count; g++) {
                    float ox = layout.X + (float) ((xIdeal + line.X [g]) * (double) em / GpTextLayout.Ideal);
                    PointF d = m.Transform (new PointF (ox, oy));
                    gids.Add ((ushort) line.Glyphs [g]); xs.Add (d.X); ys.Add (d.Y);
                }
            }
            if (gids.Count == 0) return true;
            if (s_trace) {
                var t = new System.Text.StringBuilder ("GPTEXTX");
                for (int i = 0; i < xs.Count; i++) t.Append (' ').Append (xs [i].ToString ("R")).Append (',').Append (ys [i].ToString ("R"));
                Console.Error.WriteLine (t);
            }
            GdipText.Levels lv = GdipText.ComposeGreyTransformed (font, gids, em, m.M11, m.M12, m.M21, m.M22,
                                                                  xs.ToArray (), ys.ToArray (), 0);
            if (lv.Width == 0 || lv.Height == 0) return true;
            OutputText (lv, 4, brush, _ctx.TextContrast);
            return true;
        }

        /// <summary>A string the fast imager refuses because it breaks into lines (a wrapping width,
        /// or line feeds): FullTextImager's lines (GpTextLayout's breaks), each laid out and drawn
        /// as the fast imager draws a line -- hinted advances from the rounded origin -- on a
        /// baseline a rounded line spacing below the last, the whole clipped to the layout
        /// rectangle unless NoClip. (Line Services' own ideal-unit placement, which puts glyphs a
        /// few 512ths of a pixel off the fast imager's, is not distinguished.)</summary>
        bool DrawLines (string s, TrueTypeFont font, string family, int style, float sizePt, Brush brush, RectangleF layout,
                        int formatFlags, bool typographic, int align, int lineAlign, bool hotkey, int hint)
        {
            if ((formatFlags & 0x40000003) != 0) return false;
            foreach (char c in s)
                if ((c < 0x20 && c != (char) 10 && c != (char) 13) || (c >= 0x590 && c < 0x1E00) || char.IsSurrogate (c)) return false;
            GpFontFamily.Metrics? mm = GpFontFamily.Get (family, (FontStyle) (style & 3));
            if (mm == null) return false;
            float em = sizePt * (DpiY / 72f);
            GpTextLayout L = GpTextLayout.Build (font, mm.Value, s, em, layout.Width, formatFlags, typographic, hotkey);
            if (L.Lines.Count < 2 && s.IndexOf ((char) 10) < 0) return false;
            float ls = (float) (mm.Value.LineSpacing * (double) em / mm.Value.Em);
            float totalH = L.Lines.Count * ls + (typographic ? 0f : em / 8f);
            float top = layout.Y;
            if (layout.Height > 0f && lineAlign == 1) top += (layout.Height - totalH) * 0.5f;
            else if (layout.Height > 0f && lineAlign == 2) top += layout.Height - totalH;
            var runs = new System.Collections.Generic.List<GdipText.Run> ();
            float y0 = float.NaN;
            for (int li = 0; li < L.Lines.Count; li++) {
                GpTextLayout.Line line = L.Lines [li];
                var sb = new System.Text.StringBuilder ();
                foreach (int c in line.Chars) if (s [c] != (char) 13) sb.Append (s [c]);
                string text = sb.ToString ();
                if (text.Trim (' ').Length == 0) continue;
                GdipText.Run run = GdipText.Layout (font, family, sizePt, text, layout.X, top, layout.Width, 0f,
                                                    (formatFlags & ~0x1000) | 0x4000, typographic, align, 0, false, hint,
                                                    DpiY, biLevel: true);
                if (run == null) return false;
                if (float.IsNaN (y0)) y0 = run.OriginY - li * ls;
                run.OriginY = MathF.Floor (y0 + li * ls + 0.5f);
                runs.Add (run);
            }
            bool clip = (formatFlags & 0x4000) == 0 && (layout.Width > 0f || layout.Height > 0f);
            GpRegion saved = null;
            if (clip) {
                saved = _ctx.AppClip?.Clone ();
                var r = new RectangleF (layout.X, layout.Y, layout.Width > 0f ? layout.Width : 1e6f, layout.Height > 0f ? layout.Height : 1e6f);
                CombineClip (r, CombineMode.Intersect);
            }
            try {
                foreach (GdipText.Run run in runs) DrawRun (font, run, brush, false);
            } finally {
                if (clip) { _ctx.AppClip = saved; UpdateVisibleClip (); }
            }
            return true;
        }

        /// <summary>A composed run's levels into the surface, row by row through the clip.</summary>
        void OutputText (GdipText.Levels lv, int mode, Brush brush, int contrast)
        {
            GpClip clip = Clip;
            if (clip.IsEmpty) return;
            var bounds = new Rectangle (lv.Left, lv.Top, lv.Width, lv.Height);
            if (!clip.IsVisible (bounds)) return;
            GpScan scan = NewScan ();
            SolidBrush solid = brush as SolidBrush;
            uint argb = solid != null ? (uint) solid.Color.ToArgb () : 0u;
            GpSpan span = solid != null ? null : CreateSpan (brush, scan, bounds);
            if (solid == null && span == null) return;
            ISpanSink sink;
            if (mode == 5) sink = new ClearTypeSink (scan, lv, argb, span, contrast);
            else sink = new CoverageSink (scan, lv, argb, span, contrast, mode >= 3);
            ISpanSink clipped = clip.Wrap (sink);
            for (int r = 0; r < lv.Height; r++) {
                int py = lv.Top + r;
                // The row's ink: the span runs over it whole (a level of 0 leaves the pixel alone).
                int c0 = -1, c1 = -1;
                for (int c = 0; c < lv.Width; c++)
                    if (lv.Index [r * lv.Width + c] != 0) { if (c0 < 0) c0 = c; c1 = c; }
                if (c0 < 0) continue;
                clipped.OutputSpan (py, lv.Left + c0, lv.Left + c1 + 1);
            }
            scan.End ();
        }

        /// <summary>DpOutputClearTypeSolidOptimizedSpan / DpOutputClearTypeBrushOptimizedSpan: the
        /// row's levels handed to the scan's ClearType blend.</summary>
        sealed class ClearTypeSink : ISpanSink
        {
            readonly GpScan _scan; readonly GdipText.Levels _lv; readonly uint _argb; readonly GpSpan _brush; readonly int _contrast;
            uint[] _colors;
            public ClearTypeSink (GpScan scan, GdipText.Levels lv, uint argb, GpSpan brush, int contrast)
            { _scan = scan; _lv = lv; _argb = argb; _brush = brush; _contrast = contrast; }
            public void OutputSpan (int y, int left, int right)
            {
                int n = right - left;
                if (n <= 0) return;
                int at = (y - _lv.Top) * _lv.Width + left - _lv.Left;
                uint[] colors = null;
                if (_brush != null) {
                    if (_colors == null || _colors.Length < n) _colors = new uint [Math.Max (n, 64)];
                    _brush.Colors (y, left, n, _colors);
                    colors = _colors;
                }
                _scan.ClearType (left, y, n, _lv.Index, at, _argb, colors, _contrast);
            }
        }

        /// <summary>The bi-level and antialiased spans: each pixel the brush scaled by the coverage's
        /// text-gamma value (15 = the whole brush for bi-level ink).</summary>
        sealed class CoverageSink : ISpanSink
        {
            readonly GpScan _scan; readonly GdipText.Levels _lv; readonly GpSpan _brush;
            readonly uint[] _table = new uint [16];
            readonly byte[] _cov = new byte [16];
            readonly bool _grey;
            public CoverageSink (GpScan scan, GdipText.Levels lv, uint argb, GpSpan brush, int contrast, bool grey)
            {
                _scan = scan; _lv = lv; _brush = brush; _grey = grey;
                if (contrast < 0 || contrast > 12) contrast = 12;
                for (int k = 0; k < 16; k++) {
                    int c = !grey ? (k == 0 ? 0 : 255) : TextGamma (k, contrast);
                    _cov [k] = (byte) c;
                    _table [k] = PremultiplyWithCoverage (argb, c);
                }
            }
            public void OutputSpan (int y, int left, int right)
            {
                int n = right - left;
                if (n <= 0) return;
                int at = (y - _lv.Top) * _lv.Width + left - _lv.Left;
                uint[] buf = _scan.Next (left, y, n);
                if (_brush != null) {
                    _brush.Colors (y, left, n, buf);
                    for (int i = 0; i < n; i++) {
                        int k = _lv.Index [at + i];
                        buf [i] = k == 0 ? 0u : _grey ? MultiplyCoverage (buf [i], _cov [k]) : buf [i];
                    }
                } else
                    for (int i = 0; i < n; i++) buf [i] = _table [_lv.Index [at + i]];
            }
        }

        /// <summary>TextColorGammaTable's coverage for level k (0..15) at a contrast:
        /// 255 - Inv[contrast][255 - 17k], 17k at contrast 0.</summary>
        static int TextGamma (int k, int contrast)
        {
            int k17 = k * 255 / 15;
            if (contrast == 0) return k17;
            return 255 - GdipText.ContrastInverse (contrast, 255 - k17);
        }

        /// <summary>GpColor::PremultiplyWithCoverage: alpha a*cov and each channel by it, the
        /// ((x + 128) + ((x + 128) >> 8)) >> 8 rounding.</summary>
        static uint PremultiplyWithCoverage (uint argb, int cov)
        {
            int u = (int) (argb >> 24) * cov + 0x80;
            int a = (u + (u >> 8)) >> 8;
            if (a == 0) return 0;
            uint M (uint v) { int w = (int) v * a + 0x80; return (uint) (((w + (w >> 8)) >> 8) & 0xff); }
            return (uint) a << 24 | M ((argb >> 16) & 0xff) << 16 | M ((argb >> 8) & 0xff) << 8 | M (argb & 0xff);
        }

        /// <summary>GpColor::MultiplyCoverage of a premultiplied colour.</summary>
        static uint MultiplyCoverage (uint c, int cov)
        {
            if (cov == 255) return c;
            uint M (uint v) { int w = (int) v * cov + 0x80; return (uint) (((w + (w >> 8)) >> 8) & 0xff); }
            return M (c >> 24) << 24 | M ((c >> 16) & 0xff) << 16 | M ((c >> 8) & 0xff) << 8 | M (c & 0xff);
        }
    }
}
