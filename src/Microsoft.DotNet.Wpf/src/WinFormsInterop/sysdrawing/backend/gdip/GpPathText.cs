// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// GpPath::AddString @18001b460: a FullTextImager over the string (GpTextLayout), rendered into the
// path (FullTextImager::AddToPath @1800efa40) glyph by glyph:
//
//   GpFaceRealization::GetGlyphPath @1800a1878   IDWriteFontFace::GetGlyphRunOutline at an em of
//       unitsPerEm -- the design outline -- through a GeometryAdapterSink (BeginFigure @1801ebc20,
//       AddLines @180023900, AddBeziers @180023800, EndFigure @1800239f0: a closed figure is
//       CloseFigure'd); DirectWrite's StreamGlyphOutlines @18008fb78 raises each quadratic to a cubic
//       with c1 = c * 0.6666666 (0x3f2aaaaa) + p0 / 3, c2 = c * 0.6666666 + p1 / 3
//   GpPath::AddGlyphPath @18001adb0   each point through the font matrix (em / upem, y down) and
//       offset by the glyph's origin; the glyph's last point carries the marker bit (0x20)
//   A simulated oblique shears the design outline by 87/256 of its height, in 26.6 design units
//   (DirectWrite's simulated face).
//

using System.Collections.Generic;
using System.Drawing.Drawing2D;
using System.Numerics;
using Microsoft.Wpf.Interop.WebGpu.Composition.Text;
using GdipText = Microsoft.Wpf.Interop.WebGpu.Composition.Text.GdiPlusText;

namespace System.Drawing.WebGpuBackend.Gdip
{
    internal static partial class GpPathText
    {
        static readonly float TwoThirds = BitConverter.Int32BitsToSingle (0x3f2aaaaa);
        static readonly float OneThird = 1f / 3f;

        public static partial void AddString (GpPath path, string s, FontFamily family, int style, float emSize, RectangleF layout, StringFormat format)
        {
            if (string.IsNullOrEmpty (s) || !(emSize > 0f)) return;
            string name = family.Name;
            TrueTypeFont font = GdipText.Face (name, style & 3);
            GpFontFamily.Metrics? mm = GpFontFamily.Get (name, (FontStyle) (style & 3));
            if (font == null || mm == null) return;
            GpFontFamily.Metrics m = mm.Value;
            int flags = format == null ? 0 : (int) format.FormatFlags;
            bool typographic = format != null && format.IsTypographic;
            bool hotkey = format != null && format.HotkeyPrefix != Text.HotkeyPrefix.None;
            int align = format == null ? 0 : (int) format.Alignment;
            int lineAlign = format == null ? 0 : (int) format.LineAlignment;
            GpTextLayout L = GpTextLayout.Build (font, m, s, emSize, layout.Width, flags, typographic, hotkey);

            double em = emSize;
            int margin = typographic ? 0 : GpTextLayout.IdealMargin;
            // Vertical alignment, in ideal units against the lines' spacing.
            int yIdealTop = 0;
            if (lineAlign != 0 && layout.Height > 0f) {
                double rh = layout.Height * GpTextLayout.Ideal / em;
                double th = L.Lines.Count * (double) m.LineSpacing * GpTextLayout.Ideal / m.Em + (typographic ? 0 : GpTextLayout.Ideal / 8);
                yIdealTop = (int) Math.Floor (lineAlign == 1 ? (rh - th) / 2 : rh - th);
            }
            float k = (float) (em / m.Em);
            bool oblique = font.SynthesizesOblique;
            for (int li = 0; li < L.Lines.Count; li++) {
                GpTextLayout.Line line = L.Lines [li];
                int xIdeal = margin;
                if (align != 0 && layout.Width > 0f) {
                    double rw = layout.Width * GpTextLayout.Ideal / em;
                    double tw = line.Width + 2 * margin;
                    xIdeal += (int) Math.Floor (align == 1 ? (rw - tw) / 2 : rw - tw);
                }
                int baseIdeal = (int) Math.Round ((m.Ascent + li * (double) m.LineSpacing) * GpTextLayout.Ideal / m.Em) + yIdealTop;
                float oy = layout.Y + (float) (baseIdeal * em / GpTextLayout.Ideal);
                for (int g = 0; g < line.Glyphs.Count; g++) {
                    float ox = layout.X + (float) ((xIdeal + line.X [g]) * em / GpTextLayout.Ideal);
                    if (AddGlyph (path, font, line.Glyphs [g], k, ox, oy, oblique))
                        path.Types [path.Types.Count - 1] |= GpPath.Marker;
                }
            }
            path.SubpathActive = false;
        }

        /// <summary>One glyph's outline into the path; false when it has none.</summary>
        static bool AddGlyph (GpPath path, TrueTypeFont font, int gid, float k, float ox, float oy, bool oblique)
        {
            List<(Vector2[] Points, bool[] OnCurve)> contours = font.DesignContours (gid);
            bool any = false;
            foreach ((Vector2[] raw, bool[] on) in contours) {
                int n = raw.Length;
                if (n < 2) continue;
                // Design space, y down (DirectWrite streams -y), sheared for a simulated italic.
                var p = new Vector2 [n];
                for (int i = 0; i < n; i++) {
                    float x = raw [i].X, y = raw [i].Y;
                    // The shear in the scaler's 26.6 design units (FixMul by 0x5700, half away from 0).
                    if (oblique) x += (float) (Math.Round (y * 64.0 * 0.33984375, MidpointRounding.AwayFromZero) / 64.0);
                    p [i] = new Vector2 (x, -y);
                }
                // Start on an on-curve point (an all-off contour starts at the first midpoint).
                int s0 = Array.IndexOf (on, true);
                var pts = new List<Vector2> (n + 1); var onc = new List<bool> (n + 1);
                if (s0 < 0) {
                    pts.Add ((p [0] + p [1]) * 0.5f); onc.Add (true);
                    for (int i = 1; i <= n; i++) { pts.Add (p [i % n]); onc.Add (false); }
                } else
                    for (int i = 0; i < n; i++) { pts.Add (p [(s0 + i) % n]); onc.Add (on [(s0 + i) % n]); }
                // The figure: its start, then segments back round to the start.
                Vector2 start = pts [0], cur = start;
                int count = pts.Count;
                AddPoint (path, start, GpPath.Start, k, ox, oy);
                int j = 1;
                while (j <= count) {
                    Vector2 q = pts [j % count];
                    bool qOn = onc [j % count];
                    if (qOn) {
                        if (j == count) break;   // back at the start: the close makes that line
                        AddPoint (path, q, GpPath.Line, k, ox, oy);
                        cur = q; j++;
                        continue;
                    }
                    Vector2 next = pts [(j + 1) % count];
                    bool nextOn = onc [(j + 1) % count];
                    Vector2 end;
                    if (nextOn) { end = next; j += 2; }
                    else { end = new Vector2 ((q.X + next.X) * 0.5f, (q.Y + next.Y) * 0.5f); j += 1; }
                    var c1 = new Vector2 (q.X * TwoThirds + cur.X * OneThird, q.Y * TwoThirds + cur.Y * OneThird);
                    var c2 = new Vector2 (q.X * TwoThirds + end.X * OneThird, q.Y * TwoThirds + end.Y * OneThird);
                    AddPoint (path, c1, GpPath.Bezier, k, ox, oy);
                    AddPoint (path, c2, GpPath.Bezier, k, ox, oy);
                    AddPoint (path, end, GpPath.Bezier, k, ox, oy);
                    path.HasBezier = true;
                    cur = end;
                }
                path.Types [path.Types.Count - 1] |= GpPath.Close;
                path.SubpathCount++;
                any = true;
            }
            return any;
        }

        static void AddPoint (GpPath path, Vector2 v, byte type, float k, float ox, float oy)
        {
            float x = v.X * k, y = v.Y * k;
            path.Points.Add (new PointF (x + ox, y + oy));
            path.Types.Add (type);
        }
    }
}
