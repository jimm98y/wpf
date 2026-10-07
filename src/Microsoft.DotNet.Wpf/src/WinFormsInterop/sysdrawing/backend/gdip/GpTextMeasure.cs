// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// GpGraphics::MeasureString @180077318 and GpGraphics::MeasureCharacterRanges @1800771d0, for every
// Graphics the port has (a bitmap's engine, a window's or a printed page's recording, a metafile's):
//
//   MeasureString   not on a metafile, the fast imager first: FastTextImager::Initialize @1800393c0
//       (its refusals -- the transform, the format's direction flags and tab stops, the characters
//       CharacterAttributes sends on, a second hot-key marker, a character the face lacks, a size
//       past 65536 / 65536 of the em per device pixel, a string wider than the room) and
//       FastTextImager::MeasureString @1800ebcc8: the nominal width (+0x9c, trailing blanks off
//       unless MeasureTrailingSpaces) plus both margins by the cell height (+0xe0), at
//       GetWorldTextRectangleOrigin @180039320 (the alignments' share of the rectangle), one
//       line, every character; clamped to the rectangle unless NoClip. Otherwise the full imager
//       (FullTextImager::Measure: the lines' extent), placed by the line alignment and clamped.
//   MeasureCharacterRanges   always the full imager (GpFullTextImager.Ranges.cs); on a device the
//       ranges sit where the device puts the glyphs, on a metafile where Line Services does.
//

using System.Drawing.Text;
using Microsoft.Wpf.Interop.WebGpu.Composition.Text;
using GdipText = Microsoft.Wpf.Interop.WebGpu.Composition.Text.GdiPlusText;

namespace System.Drawing.WebGpuBackend.Gdip
{
    internal sealed partial class GpGraphics
    {
        /// <summary>GetScaleForAlternatePageUnit for a context the engine does not hold: the font's
        /// size in world units under a page unit and scale at a device's resolution.</summary>
        internal static float EmWorldFor (float size, GraphicsUnit fontUnit, GraphicsUnit pageUnit, float pageScale, float dpiX, float dpiY)
        {
            float mx;
            switch (pageUnit) {
            case GraphicsUnit.Display: mx = 1f; break;
            case GraphicsUnit.Pixel: mx = pageScale; break;
            case GraphicsUnit.Inch: mx = dpiX * pageScale; break;
            case GraphicsUnit.Point: mx = dpiX * pageScale / 72f; break;
            case GraphicsUnit.Document: mx = dpiX * pageScale / 300f; break;
            case GraphicsUnit.Millimeter: mx = dpiX * pageScale / 25.4f; break;
            default: mx = dpiX * pageScale / 100f; break;
            }
            switch (fontUnit) {
            case GraphicsUnit.Point: return size * (dpiY / 72f / mx);
            case GraphicsUnit.Inch: return size * (dpiY / mx);
            case GraphicsUnit.Document: return size * (dpiY / 300f / mx);
            case GraphicsUnit.Millimeter: return size * (dpiY / 25.4f / mx);
            default: return size;
            }
        }

        /// <summary>GpGraphics::MeasureString. False where the managed stack cannot resolve the
        /// font (the caller measures its own way).</summary>
        internal static bool MeasureStringFor (string s, Font font, float emWorld, RectangleF layout, StringFormat format,
                                               in GpMatrix w2d, bool metafile, out RectangleF box, out int chars, out int lines)
        {
            box = RectangleF.Empty; chars = lines = 0;
            if (string.IsNullOrEmpty (s) || font == null) return false;
            string family = font.FontFamily?.Name;
            if (string.IsNullOrEmpty (family)) return false;
            int style = (int) font.Style;
            TrueTypeFont face = GdipText.Face (family, style & 3);
            GpFontFamily.Metrics? mm = GpFontFamily.Get (family, (FontStyle) (style & 3));
            if (face == null || mm == null || !(emWorld > 0f)) return false;
            GpTextFormat fmt = GpTextFormat.From (format);
            if (!metafile && !TakesFullImager (w2d, s, face, fmt)
                && FastMeasure (s, face, mm.Value, emWorld, layout, fmt, w2d, out box, out chars, out lines))
                return true;
            // newTextImager: a negative extent along the lines is InvalidParameter.
            float along = fmt != null && fmt.IsVertical ? layout.Height : layout.Width;
            if (!(along >= 0f)) { box = RectangleF.Empty; return true; }
            var fti = new GpFullTextImager (s, layout.Width, layout.Height, family, style, emWorld, fmt);
            if (!fti.Valid) return false;
            box = fti.MeasureRect (layout, out chars, out lines);
            return true;
        }

        /// <summary>FastTextImager::Initialize's layout-independent half and
        /// FastTextImager::MeasureString. False where Initialize answers 6 (the full imager).</summary>
        static bool FastMeasure (string s, TrueTypeFont face, GpFontFamily.Metrics m, float em, RectangleF layout, GpTextFormat fmt,
                                 in GpMatrix w2d, out RectangleF box, out int chars, out int lines)
        {
            box = RectangleF.Empty; chars = s.Length; lines = 1;
            float lm, rm, tracking;
            int flags = 0, align = 0, lineAlign = 0;
            float ww = layout.Width;           // +0xd0
            if (fmt == null) { lm = rm = em * (1f / 6f); tracking = 1.03f; }
            else {
                lm = fmt.LeadMargin * em; rm = fmt.TrailMargin * em; tracking = fmt.Tracking;
                flags = fmt.Flags; align = fmt.Align; lineAlign = fmt.LineAlign;
                if ((flags & GpTextFormat.NoWrap) != 0 && fmt.Trimming == 0) ww = 0f;
            }
            int upem = m.Em;
            float cellH = (float) (m.Ascent + m.Descent) * em / upem;   // +0xe0
            if (lm != 0f) cellH = em * 0.125f + cellH;
            // +0xe4: the em per device pixel in 16.16; past one the full imager draws it.
            float scale = em / upem * w2d.M11;
            if ((int) MathF.Floor (scale * 65536f + 0.5f) > 0x10000) return false;
            // The glyphs, a hot-key marker removed (RemoveHotkeys @18016a238: "&&" keeps one).
            var gids = new System.Collections.Generic.List<int> (s.Length);
            foreach (char c in s) gids.Add (face.GlyphIndex (c));
            if (fmt != null && fmt.Hotkey != 0) {
                int i = s.IndexOf ('&');
                if (i >= 0) {
                    if (i == s.Length - 1) gids.RemoveAt (gids.Count - 1);
                    else {
                        gids.RemoveAt (i);
                        if (s.IndexOf ('&', i + 2) >= 0) return false;
                    }
                }
            }
            int n = gids.Count;
            float total = 0f;
            if (n > 0) {
                var nom = new int [n];
                long sum = 0;
                for (int i = 0; i < n; i++) {
                    int a = GpTextShaper.DesignAdvance (face, gids [i]);
                    nom [i] = tracking != 1f ? (int) (a * tracking + 0.5f) : a;
                    sum += nom [i];
                }
                total = (float) sum * em / upem;
                if (ww > 0f && ww < total + lm + rm) return false;
                int space = face.GlyphIndex (' ');
                if ((flags & GpTextFormat.MeasureTrailingSpaces) == 0)
                    while (n > 0 && gids [n - 1] == space) { total -= nom [n - 1] * em / upem; n--; }
            }
            // GetWorldTextRectangleOrigin.
            float w = total + lm + rm;
            float x = layout.X, y = layout.Y;
            if (align == 1) x = (layout.Width - w) * 0.5f + x;
            else if (align == 2) x = (layout.Width - w) + x;
            if (fmt != null) {
                if (lineAlign == 1) y = (layout.Height - cellH) * 0.5f + y;
                else if (lineAlign == 2) y = (layout.Height - cellH) + y;
            }
            box = new RectangleF (x, y, w, cellH);
            if ((flags & GpTextFormat.NoClip) == 0) {
                if (layout.Height > 0f && !(cellH <= layout.Height)) box.Height = layout.Height;
                if (layout.Width > 0f && !(w <= layout.Width)) { box.X = layout.X; box.Width = layout.Width; }
            }
            return true;
        }

        /// <summary>GpGraphics::MeasureCharacterRanges: the full imager's regions. Null where the
        /// managed stack cannot resolve the font; throws for a range outside the string.</summary>
        internal static GpRegion[] MeasureCharacterRangesFor (string s, Font font, float emWorld, RectangleF layout, StringFormat format,
                                                             int count, in GpMatrix w2d, int hint, bool metafile)
        {
            string family = font.FontFamily?.Name;
            if (string.IsNullOrEmpty (family)) return null;
            int style = (int) font.Style;
            if (GdipText.Face (family, style & 3) == null || GpFontFamily.Get (family, (FontStyle) (style & 3)) == null) return null;
            GpTextFormat fmt = GpTextFormat.From (format);
            if (fmt.Ranges.Length > count) throw new ArgumentException ("stringFormat");
            float along = fmt.IsVertical ? layout.Height : layout.Width;
            if (!(along >= 0f) || !(emWorld > 0f)) throw new ArgumentException ("layoutRect");
            var fti = new GpFullTextImager (s, layout.Width, layout.Height, family, style, emWorld, fmt);
            if (!fti.Valid) return null;
            GpRegion[] r = fti.MeasureRanges (new MeasureTarget (w2d, hint), metafile, layout.Location, fmt.Ranges, count);
            if (r == null) throw new ArgumentException ("stringFormat");
            return r;
        }

        /// <summary>The device a measurement is made for: its world-to-device transform and the
        /// hint its realization takes. Nothing is drawn.</summary>
        sealed class MeasureTarget : IGpTextTarget
        {
            readonly GpMatrix _m; readonly int _hint;
            public MeasureTarget (in GpMatrix m, int hint) { _m = m; _hint = hint == 0 ? GdipText.HintClearTypeGridFit : hint; }
            public GpMatrix? WorldToDevice => _m;
            public int RealizationMode (TrueTypeFont face, string family, float em, float emDevice, bool square)
                => ScreenTarget.RealizationModeFor (_hint, face, family, emDevice, square, em);
            public int RealizationMode (TrueTypeFont face, string family, float emDevice, bool square)
                => ScreenTarget.RealizationModeFor (_hint, face, family, emDevice, square);
            public void DrawPlacedGlyphs (GpFullTextImager.Run run, int mode, ushort[] glyphs, PointF[] o, string chars, ushort[] map, int flags) { }
            public void DrawLine (float w, PointF a, PointF b) { }
            public object PushClip (RectangleF layout) => null;
            public void PopClip (object saved) { }
        }
    }
}
