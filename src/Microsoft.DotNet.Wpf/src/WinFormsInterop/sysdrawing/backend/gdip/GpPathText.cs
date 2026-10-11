// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// GpPath::AddString @18001b460: the full imager over the string (GpFullTextImager, the same one
// the surfaces and metafiles draw with), rendered into the path (FullTextImager::AddToPath
// @1800efa40: Render with no device; DrawGlyphs' path branch at Line Services' own advances, a
// marker before each cluster and after each run; GdipLscbkDrawUnderline's AddRects):
//
//   GpFaceRealization::GetGlyphPath @1800a1878   the glyph's outline at an em of unitsPerEm FITTED
//       by the scaler (mode word 0x40, no ClearType: Georgia's and Verdana Italic's programs move
//       points by fractions of a unit even there; a simulated bold is fsg_Embold's on the fitted
//       points), then scaled -- the same outline at every size -- through a GeometryAdapterSink (BeginFigure @1801ebc20,
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
        /// <summary>The scaler's mode word GetGlyphPath's realization fits with: 0x40, no ClearType
        /// (WF_PATH_FIT=0 takes the design outline instead).</summary>
        static readonly int s_fitFlags = int.TryParse (Environment.GetEnvironmentVariable ("WF_PATH_FIT"), System.Globalization.NumberStyles.HexNumber, null, out int f) ? f : 0x40;

        public static partial void AddString (GpPath path, string s, FontFamily family, int style, float emSize, RectangleF layout, StringFormat format)
        {
            if (string.IsNullOrEmpty (s) || !(emSize > 0f)) return;
            string name = family.Name;
            if (GdipText.Face (name, style & 3) == null) return;
            GpTextFormat fmt = GpTextFormat.From (format);
            // A negative extent along the lines is an empty imager.
            float along = fmt != null && fmt.IsVertical ? layout.Height : layout.Width;
            if (!(along >= 0f)) return;
            var fti = new GpFullTextImager (s, layout.Width, layout.Height, name, style, emSize, fmt);
            if (!fti.Valid) return;
            fti.Draw (new PathTarget (path), layout.Location);
            path.SubpathActive = false;
        }

        /// <summary>FullTextImager::AddToPath's target: DrawGlyphs' path branch (each glyph's outline
        /// through GetFontTransform -- em / upem, a quarter turn for a sideways glyph -- at its
        /// origin; a marker before each cluster and after the run) and the underlines as rectangles.</summary>
        sealed class PathTarget : IGpTextTarget
        {
            readonly GpPath _path;
            public PathTarget (GpPath path) { _path = path; }
            public GpMatrix? WorldToDevice => null;
            public int RealizationMode (TrueTypeFont face, string family, float emDevice, bool square) => 0;
            public void DrawPlacedGlyphs (GpFullTextImager.Run run, int mode, ushort[] glyphs, PointF[] o, string chars, ushort[] map, int flags) { }
            public void DrawLine (float w, PointF a, PointF b) { }
            public object PushClip (RectangleF layout) => null;
            public void PopClip (object saved) { }

            public void AddGlyphs (GpFullTextImager.Run run, ushort[] glyphs, ushort[] props, PointF[] o)
            {
                TrueTypeFont font = run.Face;
                float k = run.Em / font.UnitsPerEmForHinting;
                bool sideways = (run.ItemFlags & 0x20) != 0 && (run.ItemFlags & 0x8) == 0;
                // GetFontTransform's quarter turn (cos 90 as a float).
                const float c90 = -4.371139e-08f;
                Matrix2 m = sideways ? new Matrix2 (c90 * k, k, -k, c90 * k) : new Matrix2 (k, 0f, 0f, k);
                for (int i = 0; i < glyphs.Length; i++) {
                    if ((props [i] & GpTextShaper.PropClusterStart) != 0) _path.SetMarker ();
                    AddGlyph (_path, font, glyphs [i], m, o [i].X, o [i].Y, font.SynthesizesOblique);
                }
                _path.SetMarker ();
            }

            public void AddRect (RectangleF r) => _path.AddRects (new[] { r });
        }

        /// <summary>A glyph's outline (the path realization's, at em / upem, y down) into a path at
        /// its world origin.</summary>
        internal static void AddGlyphOutline (GpPath path, TrueTypeFont font, int gid, float em, float ox, float oy)
        {
            float k = em / font.UnitsPerEmForHinting;
            AddGlyph (path, font, gid, new Matrix2 (k, 0f, 0f, k), ox, oy, font.SynthesizesOblique);
        }

        /// <summary>The same for a glyph of a full-imager run: a sideways run's glyph is turned by
        /// GetFontTransform's quarter turn, as AddGlyphs turns it.</summary>
        internal static void AddRunGlyphOutline (GpPath path, GpFullTextImager.Run run, int gid, float ox, float oy)
        {
            TrueTypeFont font = run.Face;
            float k = run.Em / font.UnitsPerEmForHinting;
            bool sideways = (run.ItemFlags & 0x20) != 0 && (run.ItemFlags & 0x8) == 0;
            const float c90 = -4.371139e-08f;
            Matrix2 m = sideways ? new Matrix2 (c90 * k, k, -k, c90 * k) : new Matrix2 (k, 0f, 0f, k);
            AddGlyph (path, font, gid, m, ox, oy, font.SynthesizesOblique);
        }

        readonly struct Matrix2
        {
            public readonly float M11, M12, M21, M22;
            public Matrix2 (float m11, float m12, float m21, float m22) { M11 = m11; M12 = m12; M21 = m21; M22 = m22; }
        }

        /// <summary>One glyph's outline into the path; false when it has none.</summary>
        static bool AddGlyph (GpPath path, TrueTypeFont font, int gid, in Matrix2 k, float ox, float oy, bool oblique)
        {
            List<(Vector2[] Points, bool[] OnCurve)> contours = null;
            if (s_fitFlags != 0) contours = font.DWriteFittedContours (gid, font.UnitsPerEmForHinting, s_fitFlags);
            contours ??= font.DesignContours (gid);
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
                // The figure starts at the contour's first point; when that is off the curve, at
                // its last point if that is on it (Segoe UI Bold Italic 'e'), else at the first
                // on-curve point (an all-off contour at the first midpoint).
                int s0 = on [0] ? 0 : on [n - 1] ? n - 1 : Array.IndexOf (on, true);
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

        static void AddPoint (GpPath path, Vector2 v, byte type, in Matrix2 k, float ox, float oy)
        {
            float x, y;
            if (k.M12 == 0f && k.M21 == 0f) { x = v.X * k.M11; y = v.Y * k.M22; }
            else { x = v.X * k.M11 + v.Y * k.M21; y = v.X * k.M12 + v.Y * k.M22; }
            path.Points.Add (new PointF (x + ox, y + oy));
            path.Types.Add (type);
        }
    }
}
