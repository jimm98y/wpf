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
            // FastTextImager::Initialize: a positive axis scale only. Translation-only here.
            if (m.M12 != 0f || m.M21 != 0f || m.M11 != 1f || m.M22 != 1f) return false;
            TrueTypeFont font = GdipText.Face (family, style);
            if (font == null) return false;
            int hint = ResolvedTextHint ();
            GdipText.Run run = GdipText.Layout (font, family, sizePt, s, layout.X, layout.Y, layout.Width, layout.Height,
                                                formatFlags, typographic, align, lineAlign, hotkey, hint,
                                                DpiY, biLevel: true);
            if (run == null) return false;
            run.Contrast = _ctx.TextContrast;
            if (run.Glyphs.Length == 0) return true;
            float[] xs = GdipText.GlyphXs (run, run.OriginX + m.Dx);
            float y = run.OriginY + m.Dy;
            if (s_trace) Console.Error.WriteLine ($"GPTEXT '{s}' mode={run.Mode} y={y} xs={string.Join (",", xs)} g={string.Join (",", run.Glyphs)}");

            GdipText.Levels lv;
            switch (run.Mode) {
            case 5: {
                var bits = new NaturalClearType.GlyphBits [run.Glyphs.Length];
                for (int i = 0; i < bits.Length; i++) bits [i] = GdipText.Glyph (font, run.Glyphs [i], run.Em);
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
            if (lv.Width == 0 || lv.Height == 0) return true;

            GpRegion saved = null;
            bool clipped = false;
            if (run.HasClip) {
                saved = _ctx.AppClip?.Clone ();
                clipped = true;
                CombineClip (new RectangleF (run.ClipX, run.ClipY, run.ClipW, run.ClipH), CombineMode.Intersect);
            }
            try {
                OutputText (lv, run.Mode, brush, run.Contrast);
            } finally {
                if (clipped) { _ctx.AppClip = saved; UpdateVisibleClip (); }
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
