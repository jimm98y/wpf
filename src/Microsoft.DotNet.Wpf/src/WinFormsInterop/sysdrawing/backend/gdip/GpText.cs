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
        internal int ResolvedTextHint ()
        {
            int h = (int) _ctx.TextHint;
            if (Image.GetPixelFormatSize (Frame.Format) <= 8) return GdipText.HintSingleBitPerPixelGridFit;
            return h == 0 ? GdipText.HintClearTypeGridFit : h;
        }

        /// <summary>GpGraphics::DrawString @180010f00's em: the font's size in world units
        /// (GetScaleForAlternatePageUnit: World and Pixel as they are, the physical units through
        /// the device's dpi and the page multiplier).</summary>
        public float EmWorld (float size, GraphicsUnit unit)
        {
            GetPageMultipliers (out float mx, out _);
            switch (unit) {
            case GraphicsUnit.Point: return size * (DpiY / 72f / mx);
            case GraphicsUnit.Inch: return size * (DpiY / mx);
            case GraphicsUnit.Document: return size * (DpiY / 300f / mx);
            case GraphicsUnit.Millimeter: return size * (DpiY / 25.4f / mx);
            default: return size;
            }
        }

        /// <summary>GpGraphics::DrawString: the fast imager where GDI+ would take it (and the port
        /// models it), FullTextImager where GDI+ hands the string to it. False where this engine
        /// does not draw the string: the caller draws it its old way.</summary>
        public bool DrawString (string s, Font f, Brush brush, RectangleF layout, StringFormat format)
        {
            if (string.IsNullOrEmpty (s) || brush == null || !CanFill (brush)) return false;
            string family = f.FontFamily?.Name;
            if (string.IsNullOrEmpty (family)) return false;
            int style = (int) f.Style;
            GpTextFormat fmt = GpTextFormat.From (format);
            float emWorld = EmWorld (f.Size, f.Unit);
            if (!(emWorld > 0f)) return false;
            TrueTypeFont face = GdipText.Face (family, style & 3);
            if (face == null) return false;
            if (!TakesFullImager (WorldToDevice, s, face, fmt)) {
                int flags = fmt?.Flags ?? 0;
                bool typographic = fmt != null && fmt.LeadMargin == 0f;
                bool hotkey = fmt != null && fmt.Hotkey != 0;
                GdipText.LastFull = false;
                if ((style & 12) == 0
                    && DrawString (s, family, style & 3, f.SizeInPoints, brush, layout, flags, typographic,
                                   fmt?.Align ?? 0, fmt?.LineAlign ?? 0, hotkey, fmt?.Trimming ?? 1))
                    return true;
                if (!GdipText.LastFull) return false;
            }
            return DrawStringFull (s, family, style, emWorld, brush, layout, fmt);
        }

        /// <summary>FastTextImager::Initialize's refusals the port decides here, from GDI+'s own
        /// tables (the width, black-box and transform refusals are GdiPlusText.Layout's).</summary>
        internal static bool TakesFullImager (in GpMatrix m, string s, TrueTypeFont face, GpTextFormat fmt)
        {
            if (!(m.M11 > 0f) || m.M12 != 0f || m.M21 != 0f || m.M22 == 0f) return true;
            if (fmt != null && ((fmt.Flags & 0x40000003) != 0 || fmt.Tabs.Length > 0)) return true;
            int all = 0;
            foreach (char c in s) all |= GpTextTables.Flags (c);
            if ((all & 0x80) != 0) return true;
            if (fmt != null && fmt.Hotkey != 0) {
                // RemoveHotkeys: one marker is the fast imager's, a second sends the string on.
                int first = s.IndexOf ('&');
                if (first >= 0 && first + 1 < s.Length && s.IndexOf ('&', first + 2) >= 0) return true;
            }
            if (fmt == null || (fmt.Flags & GpTextFormat.NoFontFallback) == 0)
                foreach (char c in s)
                    if (face.GlyphIndex (c) == 0 && (fmt == null || fmt.Hotkey == 0 || c != '&')) return true;
            return false;
        }

        /// <summary>GDI+'s FullTextImager into this surface.</summary>
        public bool DrawStringFull (string s, string family, int style, float emWorld, Brush brush, RectangleF layout, GpTextFormat fmt)
        {
            if (layout.Width < 0f || layout.Height < 0f) return true;
            var fti = new GpFullTextImager (s, layout.Width, layout.Height, family, style, emWorld, fmt);
            if (!fti.Valid) return false;
            fti.Draw (new ScreenTarget (this, brush), layout.Location);
            return true;
        }

        /// <summary>The full imager's target on a surface: GpGraphics::DrawPlacedGlyphs into the
        /// pixels (the realization the surface's hint makes), DrawLines with an aliased pen, the
        /// layout rectangle into the clip.</summary>
        sealed class ScreenTarget : IGpTextTarget
        {
            readonly GpGraphics _g; readonly Brush _brush;
            public ScreenTarget (GpGraphics g, Brush brush) { _g = g; _brush = brush; }

            public GpMatrix? WorldToDevice => _g.WorldToDevice;

            public int RealizationMode (TrueTypeFont face, string family, float emDevice, bool square)
                => RealizationModeFor (_g.ResolvedTextHint (), face, family, emDevice, square);

            /// <summary>GpFaceRealization's render mode for a resolved hint: ClearType falls back to
            /// bi-level for a face drawn from its embedded bitmaps at this size (and Marlett),
            /// AntiAliasGridFit for a size the 'gasp' does not grey.</summary>
            internal static int RealizationModeFor (int hint, TrueTypeFont face, string family, float emDevice, bool square)
            {
                int ppem = (int) MathF.Floor (emDevice + 0.5f);
                if (hint == GdipText.HintClearTypeGridFit
                    && ((square && face.EmbeddedBitmapCount (ppem) > 100) || string.Equals (family, "Marlett", StringComparison.OrdinalIgnoreCase)))
                    return 1;
                if (hint == GdipText.HintAntiAliasGridFit && !face.GaspDoGray (ppem)) return 1;
                return hint;
            }

            public void DrawPlacedGlyphs (GpFullTextImager.Run run, int mode, ushort[] glyphs, PointF[] o, string chars, ushort[] map, int flags)
            {
                GpTextTrace.ReportPlaced (mode, run.Em, glyphs, o);
                if (glyphs.Length == 0) return;
                GpMatrix m = _g.WorldToDevice;
                float sx = MathF.Sqrt (m.M11 * m.M11 + m.M12 * m.M12), sy = MathF.Sqrt (m.M21 * m.M21 + m.M22 * m.M22);
                var xs = new float [o.Length]; var ys = new float [o.Length];
                for (int i = 0; i < o.Length; i++) { xs [i] = o [i].X; ys [i] = o [i].Y; }
                TrueTypeFont face = run.Face;
                float em = run.Em;
                bool sideways = (run.ItemFlags & 0x20) != 0 && (run.ItemFlags & 0x8) == 0;
                bool upright = (run.ItemFlags & 0x28) == 0x28;
                float tl = MathF.Max (sx, sy) / 65536f;
                bool axis = MathF.Abs (m.M12) <= tl && MathF.Abs (m.M21) <= tl;
                bool quarter = MathF.Abs (m.M11) <= tl && MathF.Abs (m.M22) <= tl;
                if (!axis && (run.ItemFlags & 0x20) == 0) {
                    // A clockwise quarter turn of the world realizes the glyphs sideways, as a
                    // vertical line's are.
                    if (quarter && m.M12 > 0f && m.M21 < 0f) sideways = true;
                    else if (mode == 5) {
                        // Any other turn under ClearType: the unfitted outline through the turn,
                        // scanned 6x1 and filtered as an upright glyph is.
                        var tb = new NaturalClearType.GlyphBits [glyphs.Length];
                        float s = sx;
                        for (int i = 0; i < tb.Length; i++)
                            tb [i] = NaturalClearType.RasterizeTransformed (face, glyphs [i], em * s, m.M11 / s, m.M12 / s, m.M21 / s, m.M22 / s);
                        GdipText.Levels tl5 = GdipText.Compose (tb, xs, ys, 0f, face.GdiContrastPalette);
                        if (tl5.Width == 0 || tl5.Height == 0) return;
                        _g.OutputText (tl5, 5, _brush, _g._ctx.TextContrast);
                        return;
                    } else if (mode == 3 || mode == 4) {
                        // Any other turn, antialiased: GpGraphics::DrawPlacedGlyphs hands the
                        // device transform's linear part to CreateGlyphBitmapArray, which grid-fits
                        // only an axis-aligned or quarter-turned realization (GpFaceRealization
                        // +0xbc / +0xc0); otherwise each glyph's outline is scaled and turned
                        // unfitted (MakeRasterizerTransform), scanned 4x4 at its quarter-pixel
                        // phase and combined by max, as the upright antialiased glyphs are.
                        var gl = new System.Collections.Generic.List<ushort> (glyphs.Length);
                        var gx = new System.Collections.Generic.List<float> (glyphs.Length);
                        var gy = new System.Collections.Generic.List<float> (glyphs.Length);
                        for (int i = 0; i < glyphs.Length; i++)
                            if (glyphs [i] != 0xffff) { gl.Add (glyphs [i]); gx.Add (xs [i]); gy.Add (ys [i]); }
                        GdipText.Levels gv = GdipText.ComposeGreyTransformed (face, gl, em, m.M11, m.M12, m.M21, m.M22, gx.ToArray (), gy.ToArray (), 0);
                        if (gv.Width == 0 || gv.Height == 0) return;
                        _g.OutputText (gv, 4, _brush, _g._ctx.TextContrast);
                        return;
                    } else {
                        // Any other turn: the glyphs' outlines through the transform, antialiased.
                        FillTurned (run, glyphs, o, m);
                        return;
                    }
                }
                GdipText.Levels lv;
                if (upright && mode == 5) {
                    // An upright glyph in vertical text: GetGlyphStringVerticalOriginOffsets @180024800,
                    // ((cell ascent - advance + cell descent) / 2 - cell descent, vertical origin y)
                    // through the realization, the glyph unturned.
                    int upem = face.UnitsPerEmForHinting;
                    float kx = em / upem * m.M11, ky = em / upem * m.M22;
                    var ub = new NaturalClearType.GlyphBits [glyphs.Length];
                    for (int i = 0; i < glyphs.Length; i++) {
                        GdipText.SidewaysMetrics (face, glyphs [i], em, m.M11, m.M22, out int advW, out int voy);
                        float ox = (face.WinAscent - advW + face.WinDescent) * 0.5f - face.WinDescent;
                        xs [i] += ox * kx; ys [i] += voy * ky;
                        ub [i] = GdipText.Glyph (face, glyphs [i], GdipText.AxisPpem (em * m.M11), GdipText.AxisPpem (em * m.M22));
                    }
                    lv = GdipText.Compose (ub, xs, ys, 0f, face.GdiContrastPalette);
                } else if (mode == 5) {
                    var bits = new NaturalClearType.GlyphBits [glyphs.Length];
                    int ppA = GdipText.AxisPpem (em * sy), ppX = GdipText.AxisPpem (em * sx);
                    for (int i = 0; i < bits.Length; i++)
                        bits [i] = sideways ? GdipText.GlyphSideways (face, glyphs [i], ppA, ppX)
                                 : sx == sy ? GdipText.Glyph (face, glyphs [i], em * sx)
                                 : GdipText.Glyph (face, glyphs [i], ppX, GdipText.AxisPpem (em * sy));
                    lv = GdipText.Compose (bits, xs, ys, 0f, face.GdiContrastPalette);
                } else if (mode == 3 || mode == 4) {
                    lv = GdipText.ComposeGrey (face, glyphs, em * sx, xs, ys [0]);
                } else {
                    lv = GdipText.ComposeMono (face, glyphs, em * sx, xs, ys [0], gridFit: mode == 1);
                }
                if (lv.Width == 0 || lv.Height == 0) return;
                _g.OutputText (lv, mode == 5 ? 5 : mode, _brush, _g._ctx.TextContrast);
            }

            /// <summary>Glyphs under a turned transform: their outlines at the world origins, filled
            /// through the transform with antialiasing.</summary>
            void FillTurned (GpFullTextImager.Run run, ushort[] glyphs, PointF[] deviceOrigins, GpMatrix m)
            {
                GpMatrix inv = m;
                if (!inv.Invert ()) return;
                var world = (PointF[]) deviceOrigins.Clone ();
                inv.Transform (world);
                var path = new GpPath (FillMode.Winding);
                for (int i = 0; i < glyphs.Length; i++)
                    if (glyphs [i] != 0xffff) GpPathText.AddGlyphOutline (path, run.Face, glyphs [i], run.Em, world [i].X, world [i].Y);
                if (path.Points.Count == 0) return;
                SmoothingMode sm = _g._ctx.Smoothing;
                _g._ctx.Smoothing = SmoothingMode.AntiAlias;
                try { _g.FillPath (_brush, path.Points.ToArray (), path.Types.ToArray (), FillMode.Winding); }
                finally { _g._ctx.Smoothing = sm; }
            }

            public void DrawLine (float devicePenWidth, PointF a, PointF b)
            {
                GpTextTrace.ReportLine (devicePenWidth, a, b);
                var path = new GpPath (new[] { a, b }, new byte[] { 0, 1 }, FillMode.Alternate);
                var dp = new DpPen { Width = devicePenWidth, Unit = 2, Brush = _brush };
                SmoothingMode sm = _g._ctx.Smoothing;
                // SetTextLinesAntialiasMode: aliased lines unless the text is antialiased.
                int hint = _g.ResolvedTextHint ();
                _g._ctx.Smoothing = hint == GdipText.HintAntiAlias ? SmoothingMode.AntiAlias : SmoothingMode.None;
                try { _g.RenderDrawPath (GpStroke.Bounds (path, _g.WorldToDevice, dp, _g.DpiX), path, dp); }
                finally { _g._ctx.Smoothing = sm; }
            }

            public object PushClip (RectangleF layout)
            {
                var saved = new Box { Clip = _g._ctx.AppClip?.Clone () };
                _g.CombineClip (layout, CombineMode.Intersect);
                return saved;
            }

            public void PopClip (object saved)
            {
                _g._ctx.AppClip = ((Box) saved).Clip;
                _g.UpdateVisibleClip ();
            }

            sealed class Box { public GpRegion Clip; }
        }

        /// <summary>The fast imager's DrawString. False where it does not draw the string (see
        /// <see cref="GdipText.LastFull"/> for whether GDI+ would hand it to the full imager).</summary>
        public bool DrawString (string s, string family, int style, float sizePt, Brush brush, RectangleF layout,
                                int formatFlags, bool typographic, int align, int lineAlign, bool hotkey, int trimming = 1)
        {
            if (string.IsNullOrEmpty (s) || brush == null || !CanFill (brush)) return false;
            GpMatrix m = WorldToDevice;
            TrueTypeFont font = GdipText.Face (family, style);
            if (font == null) return false;
            int hint = ResolvedTextHint ();
            if (s_trace) Console.Error.WriteLine ($"GPTEXT DrawString '{s}' {family} {sizePt}pt m=[{m.M11} {m.M12} {m.M21} {m.M22} {m.Dx} {m.Dy}] hint={hint} flags={formatFlags:x}");
            // FastTextImager::Initialize +0xd0: the width a string must fit, none for NoWrap without trimming.
            float ww = (formatFlags & 0x1000) != 0 && trimming == 0 ? 0f : layout.Width;
            // FastTextImager::Initialize: a positive axis scale only (m11 > 0, m12 = m21 = 0,
            // m22 != 0; m22 > 0 modelled). A turned or sheared transform goes the full imager's way.
            bool axisScale = m.M12 == 0f && m.M21 == 0f && m.M11 > 0f && m.M22 > 0f;
            bool identity = axisScale && m.M11 == 1f && m.M22 == 1f;
            GdipText.Run run = null;
            if (axisScale && !identity)
                run = GdipText.Layout (font, family, sizePt, s, layout.X, layout.Y, layout.Width, layout.Height,
                                       formatFlags, typographic, align, lineAlign, hotkey, hint,
                                       DpiY, biLevel: true, sx: m.M11, sy: m.M22, wrapWidth: ww);
            if (!identity) {
                if (run != null) { DrawRun (font, run, brush, run.HasClip); return true; }
                if (GdipText.LastFull || !axisScale) { GdipText.LastFull = true; return false; }
                return DrawTransformed (s, font, family, style, sizePt, brush, layout, formatFlags, typographic, align, lineAlign, hotkey, hint);
            }
            run = GdipText.Layout (font, family, sizePt, s, layout.X, layout.Y, layout.Width, layout.Height,
                                   formatFlags, typographic, align, lineAlign, hotkey, hint,
                                   DpiY, biLevel: true, wrapWidth: ww);
            if (run == null && GdipText.LastFull) return false;
            if (run == null)
                return DrawLines (s, font, family, style, sizePt, brush, layout, formatFlags, typographic, align, lineAlign, hotkey, hint);
            DrawRun (font, run, brush, run.HasClip);
            return true;
        }

        /// <summary>GDI+'s FullTextImager for ONE vertical line (StringFormatFlags.DirectionVertical
        /// with NoWrap): the Line Services layout GDI+ builds for it, in ideal units (2048 to the
        /// em, r = 2048 / em per world unit), and the glyphs, the ellipsis and the underline drawn
        /// the way FullTextImager::Render / RenderLine / GdipLscbkDrawGlyphs / GlyphImager draw them.
        /// <list type="bullet">
        /// <item>BuiltLine::BuiltLine @1800f3670: the margins are round(format margin * em * r)
        /// (341 for the default 1/6), the room is round(extent * r) less both margins, and the line
        /// starts at the margin plus GetPhysicalAlignment's share of what the line leaves of the
        /// extent ((W - L) / 2 for centre, W - L for far, in integers).</item>
        /// <item>Advances along the line: each glyph's design advance, tracked by 1.03 and rounded
        /// unless typographic. A tab moves to the first stop past the pen (GetTabStops @1800f15e0:
        /// round(r * (firstTabOffset + the tab stops so far)), every later stop the last interval
        /// apart; no stops at all, no tab width).</item>
        /// <item>EllipsisWord trimming (BuiltLine::CreateLine / RecreateLineEllipsis): the words that
        /// fit in the room less the ellipsis, trailing white space hanging, then the ellipsis.
        /// EllipsisInfo::EllipsisInfo @1800ef130 is U+2026; in a vertical format it asks the face
        /// for a vertical variant and, when the face has none, keeps the glyph id the query left
        /// behind -- 0, the .notdef -- drawn upright, its advance the design advance height.</item>
        /// <item>Across the lines (Render @18003c900, RenderLine @18003cbe8, LogicalToXY @18003d548):
        /// the text is lines x line spacing + 256 (em / 8) high, placed in round(width * r) by the
        /// line alignment; a vertical line's baseline is its top plus the descent plus 256.</item>
        /// <item>GlyphImager::GetDisplayCellOrigin @18003d690: each run's cell origin is put on the
        /// device grid of an axis-scale transform (round(x * m11) / m11, likewise y), and the run's
        /// glyphs follow at their hinted advances. A sideways glyph is realized under
        /// GetFontTransform's quarter turn; the upright ellipsis takes GetGlyphStringVerticalOriginOffsets
        /// @180024800: ((cellAscent - advance + cellDescent) / 2 - cellDescent, verticalOriginY)
        /// through the realization.</item>
        /// <item>GdipLscbkGetRunUnderlineInfo @1800f9580 / GdipLscbkDrawUnderline @1800f8940: the
        /// underline round(-post.underlinePosition) ideal units off the baseline, drawn as a line
        /// with an aliased pen of GetDevicePenWidth @1800770e8 pixels (max(1, round |w * m11|))
        /// from the line start along the underlined runs.</item>
        /// <item>FullTextImager::Draw @1800f0360: the layout rectangle intersected into the clip
        /// unless NoClip.</item>
        /// </list>
        /// Modelled for ClearType under an axis-scale transform, single-glyph runs of sideways
        /// (Latin) text, trimming None / Character / EllipsisWord; false otherwise.</summary>
        public bool DrawStringVertical (string s, string family, int style, float sizePt, Brush brush, RectangleF layout,
                                        int formatFlags, bool typographic, int align, int lineAlign, int hotkey,
                                        int trimming, float firstTab, float[] tabs)
        {
            if (string.IsNullOrEmpty (s) || brush == null || !CanFill (brush)) return false;
            if ((formatFlags & 0x2) == 0 || (formatFlags & 0x1000) == 0 || (formatFlags & 0x1) != 0) return false;
            if ((style & 8) != 0) return false;                       // strikeout
            GpMatrix m = WorldToDevice;
            if (m.M12 != 0f || m.M21 != 0f || !(m.M11 > 0f) || !(m.M22 > 0f)) return false;
            if (ResolvedTextHint () != GdipText.HintClearTypeGridFit) return false;
            TrueTypeFont font = GdipText.Face (family, style & 3);
            if (font == null || font.SynthesizesBold || font.SynthesizesOblique || font.HasVerticalMetrics) return false;
            GpFontFamily.Metrics? mm = GpFontFamily.Get (family, (FontStyle) (style & 3));
            if (mm == null) return false;
            GpFontFamily.Metrics fm = mm.Value;
            GsubTable gsub = font.Gsub;
            if (gsub != null && (gsub.HasFeature ("DFLT", "vert") || gsub.HasFeature ("latn", "vert")
                                 || gsub.HasFeature ("DFLT", "vrt2") || gsub.HasFeature ("latn", "vrt2"))) return false;
            foreach (char c in s)
                if ((c < 0x20 && c != '\t') || c >= 0x590 || (hotkey != 0 && c == '&')) return false;
            if (trimming != 0 && trimming != 1 && trimming != 4) return false;
            float em = sizePt * (DpiY / 72f);
            if (!(em > 0f)) return false;
            int upem = font.UnitsPerEmForHinting;
            if (upem <= 0) return false;
            const int Ideal = GpTextLayout.Ideal;
            float r = Ideal / em;                                    // ideal units per world unit
            static int Rnd (float v) => (int) MathF.Floor (v + 0.5f);
            int Du (int du) => upem == Ideal ? du : (int) Math.Round (du * (double) Ideal / upem);
            int lm = typographic ? 0 : Rnd (em * r / 6f), tm = lm;  // format margins 1/6 em
            int extent = Rnd (layout.Height * r);
            int room = extent < 1 ? 0x1000000 : Math.Max (0, extent - lm - tm);

            // ---- Line Services: the pen along the line, in ideal units ----
            var stops = new System.Collections.Generic.List<int> ();
            int increment;
            if (tabs != null && tabs.Length > 0) {
                float cum = firstTab;
                foreach (float t in tabs) { cum += t; stops.Add (Rnd (r * cum)); }
                increment = Rnd (tabs [tabs.Length - 1] * r);
            } else increment = Rnd (r * firstTab);
            int NextStop (int pen)
            {
                foreach (int st in stops) if (st > pen) return st;
                if (increment <= 0) return pen;
                int last = stops.Count > 0 ? stops [stops.Count - 1] : 0;
                while (last <= pen) last += increment;
                return last;
            }
            int n = s.Length;
            var gids = new int [n];
            var penAt = new int [n + 1];
            int pen = 0;
            for (int i = 0; i < n; i++) {
                penAt [i] = pen;
                char c = s [i];
                if (c == '\t') { gids [i] = -1; pen = NextStop (pen); continue; }
                int g = font.GlyphIndex (c);
                if (g <= 0) return false;                             // font fallback not modelled
                gids [i] = g;
                int a = font.DesignAdvance (g);
                if (!typographic) a = Rnd (a * 1.03f);
                pen += Du (a);
            }
            penAt [n] = pen;
            // A run longer than one glyph places its later glyphs at hinted advances; not modelled.
            for (int i = 1; i < n; i++)
                if (gids [i] > 0 && gids [i - 1] > 0 && s [i] != ' ' && s [i - 1] != ' ') return false;

            // The line end without trailing white space, and whether it overflows.
            int End (int count)
            {
                int k = count;
                while (k > 0 && (s [k - 1] == ' ' || s [k - 1] == '\t')) k--;
                return penAt [k];
            }
            int keep = n;
            bool ellipsis = false;
            if (End (n) > room) {
                if (trimming == 0) { }
                else if (trimming == 1) {
                    keep = 0;
                    while (keep < n && penAt [keep + 1] <= room) keep++;
                } else {
                    int ellW = Du (font.TypoAscender - font.TypoDescender);
                    int room2 = room - ellW;
                    keep = 0;
                    int k = 0;
                    while (k < n) {
                        int w0 = k;
                        while (k < n && s [k] != ' ' && s [k] != '\t') k++;
                        int wordEnd = k;
                        while (k < n && (s [k] == ' ' || s [k] == '\t')) k++;
                        if (penAt [wordEnd] > room2 && w0 > 0) break;
                        if (penAt [wordEnd] > room2) return false;    // not even the first word
                        keep = k;
                    }
                    ellipsis = true;
                }
            }
            int contentEnd = ellipsis ? penAt [keep] : End (keep);
            int ellAdv = ellipsis ? Du (font.TypoAscender - font.TypoDescender) : 0;
            int L = contentEnd + ellAdv;
            int u0 = lm;
            if (extent >= 1) {
                if (align == 1) u0 += (extent - (L + lm + tm)) / 2;
                else if (align == 2) u0 += extent - (L + lm + tm);
            }

            // ---- across the lines ----
            int lineH = Du (fm.LineSpacing * upem / fm.Em) + (typographic ? 0 : Ideal / 8);
            int across = Rnd (layout.Width * r), vTop = 0;
            if (layout.Width > 0f) {
                if (lineAlign == 1) vTop = (across - lineH) / 2;
                else if (lineAlign == 2) vTop = across - lineH;
            }
            int v = vTop + Du (font.WinDescent) + (typographic ? 0 : Ideal / 8);

            // ---- draw ----
            GpRegion saved = null;
            bool clip = (formatFlags & 0x4000) == 0 && layout.Width != 0f && layout.Height != 0f;
            if (clip) { saved = _ctx.AppClip?.Clone (); CombineClip (layout, CombineMode.Intersect); }
            try {
                // GetDisplayCellOrigin: the world cell origin onto the device grid.
                PointF Cell (int vv, int uu)
                {
                    float x = layout.X + vv / r, y = layout.Y + uu / r;
                    x = MathF.Floor (x * m.M11 + 0.5f) / m.M11;
                    y = MathF.Floor (y * m.M22 + 0.5f) / m.M22;
                    return m.Transform (new PointF (x, y));
                }
                int ppAlong = GdipText.AxisPpem (em * m.M22), ppAcross = GdipText.AxisPpem (em * m.M11);
                bool fixedFilter = font.GdiContrastPalette;
                for (int i = 0; i < keep; i++) {
                    if (gids [i] < 0 || s [i] == ' ') continue;
                    PointF d = Cell (v, u0 + penAt [i]);
                    var bits = GdipText.GlyphSideways (font, gids [i], ppAlong, ppAcross);
                    GdipText.Levels lv = GdipText.Compose (new [] { bits }, new [] { d.X }, d.Y, fixedFilter);
                    if (lv.Width > 0 && lv.Height > 0) OutputText (lv, 5, brush, _ctx.TextContrast);
                }
                if (ellipsis) {
                    PointF d = Cell (v, u0 + contentEnd);
                    const int g0 = 0;
                    // GetGlyphStringVerticalOriginOffsets: the realization's sideways GDI metrics.
                    GdipText.SidewaysMetrics (font, g0, em, m.M11, m.M22, out int advW, out int voy);
                    float ox = ((font.WinAscent - advW + font.WinDescent) * 0.5f - font.WinDescent);
                    float sx = em / upem * m.M11, sy = em / upem * m.M22;
                    float px = d.X + ox * sx, py = d.Y + voy * sy;
                    var bits = GdipText.Glyph (font, g0, GdipText.AxisPpem (em * m.M11), GdipText.AxisPpem (em * m.M22));
                    GdipText.Levels lv = GdipText.Compose (new [] { bits }, new [] { px }, py, fixedFilter);
                    if (lv.Width > 0 && lv.Height > 0) OutputText (lv, 5, brush, _ctx.TextContrast);
                }
                if ((style & 4) != 0 && keep > 0) {
                    int ulOff = Rnd (-font.UnderlinePosition * (em / upem) * r);
                    float ulW = Rnd (font.UnderlineThickness * (em / upem) * r) / r;
                    float devW = MathF.Max (1f, MathF.Floor (MathF.Abs (ulW * m.M11) + 0.5f));
                    int len = penAt [keep];
                    float x = layout.X + (v - ulOff) / r;
                    var pts = new [] { new PointF (x, layout.Y + u0 / r), new PointF (x, layout.Y + (u0 + len) / r) };
                    var path = new GpPath (pts, new byte [] { 0, 1 }, FillMode.Alternate);
                    var dp = new DpPen { Width = devW, Unit = 2, Brush = brush };
                    SmoothingMode sm = _ctx.Smoothing;
                    _ctx.Smoothing = SmoothingMode.None;
                    try { RenderDrawPath (GpStroke.Bounds (path, WorldToDevice, dp, DpiX), path, dp); }
                    finally { _ctx.Smoothing = sm; }
                }
            } finally {
                if (clip) { _ctx.AppClip = saved; UpdateVisibleClip (); }
            }
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
            if (GpTextTrace.Placed != null) {
                var ys = new float [xs.Length];
                for (int i = 0; i < ys.Length; i++) ys [i] = y;
                GpTextTrace.ReportPlaced (run.Mode, run.Em, run.Glyphs, xs, ys);
            }

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
