// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// A minimal, pure-managed TrueType (glyf-outline) font reader implementing
// IGlyphSource. It parses the tables needed to turn characters into real glyph
// outlines (head/maxp/cmap/loca/glyf/hhea/hmtx), converts each glyph's
// quadratic-Bézier contours into the engine's PathGeometry, and rasterizes them
// with the shared PathRasterizer -- so real fonts reuse the same anti-aliased
// fill as everything else, with no native dependency (works on every platform).
//
// Scope: TrueType simple glyphs, Unicode cmap format 4 (BMP), horizontal
// metrics. This delivers real outlines, counters (holes) and proportional
// advances. Complex-script shaping (HarfBuzz: ligatures, marks, GSUB/GPOS),
// CFF/OTF PostScript outlines and composite glyphs are future work behind this
// same seam.
//

using System;
using System.Collections.Generic;
using System.Numerics;
using Microsoft.Wpf.Interop.WebGpu.Composition;

namespace Microsoft.Wpf.Interop.WebGpu.Composition.Text
{
    /// <summary>
    /// A font that can produce a glyph's filled outline (as PathFigures) by glyph
    /// index. This is the seam the milcore glyph-run decoder uses to render WPF text:
    /// WPF supplies already-shaped glyph indices + advances, and the outlines come
    /// from whichever font the run referenced.
    /// </summary>
    internal interface IGlyphOutlineFont
    {
        /// <summary>The pixels-per-em the returned figures are scaled to.</summary>
        int PixelsPerEm { get; }

        /// <summary>
        /// Get the glyph outline (in <see cref="PixelsPerEm"/> units, baseline at y=0,
        /// y-down) for <paramref name="glyphId"/>. Returns false for blank/missing glyphs.
        /// </summary>
        bool TryGetGlyphOutline(int glyphId, out List<PathFigure> figures);
    }

    internal sealed class TrueTypeFont : IFont, IGlyphOutlineFont, IColorGlyphFont
    {
        // Glyphs are rasterized with the em square at this many pixels; the
        // renderer scales the atlas quad to the requested EmSize.
        private const int BaseEmPixels = 48;

        private readonly byte[] _data;
        private readonly int _sfntBase;         // offset of this face's sfnt header (non-zero inside a .ttc)
        private readonly float _scale;          // font units -> base pixels
        private readonly int _numGlyphs;
        private readonly int _glyfOffset;
        private readonly uint[] _loca;          // numGlyphs+1 glyph data offsets
        private readonly ushort[] _advanceWidths;
        private readonly int _numHMetrics;
        private readonly CmapTable _cmap;
        private readonly ColorTable? _color;    // COLR/CPAL color glyphs (emoji), null if absent
        private readonly Dictionary<(int, int), float> _kerning = new(); // base pixels

        // Synthetic style (DirectWrite font simulations): when WPF requests a weight/
        // style the family has no real face for, DWrite returns the regular outlines
        // flagged BOLD/OBLIQUE and synthesizes the look. We replicate that here.
        private readonly float _emboldenStrength;   // base-pixel outline dilation per side (0 = none)
        private readonly float _shear;              // oblique x-shear coefficient (0 = none)

        public int PixelsPerEm => BaseEmPixels;

        public int GlyphCount => _numGlyphs;

        // DirectWrite oblique simulation slants glyphs by 20 degrees.
        private const float ObliqueShear = 0.36397023f;     // tan(20°)
        // Bold simulation thickens stems by ~2% of the em on each side.
        private const float EmboldenFraction = 0.02f;

        // <paramref name="sfntOffset"/> is the byte offset of this face's sfnt header,
        // non-zero when the face lives inside a TrueType Collection (.ttc).
        public TrueTypeFont(byte[] data, bool synthesizeBold = false, bool synthesizeOblique = false, int sfntOffset = 0)
        {
            _data = data;
            _sfntBase = sfntOffset;
            if (synthesizeBold) _emboldenStrength = BaseEmPixels * EmboldenFraction;
            if (synthesizeOblique) _shear = ObliqueShear;

            Dictionary<string, int> tables = ReadTableDirectory();
            int head = Require(tables, "head");
            int maxp = Require(tables, "maxp");
            int hhea = Require(tables, "hhea");
            int hmtx = Require(tables, "hmtx");
            int loca = Require(tables, "loca");
            _glyfOffset = Require(tables, "glyf");
            int cmap = Require(tables, "cmap");

            int unitsPerEm = U16(head + 18);
            int indexToLocFormat = (short)U16(head + 50);
            _scale = BaseEmPixels / (float)unitsPerEm;
            _numGlyphs = U16(maxp + 4);
            _numHMetrics = U16(hhea + 34);

            _advanceWidths = new ushort[_numHMetrics];
            for (int i = 0; i < _numHMetrics; i++)
                _advanceWidths[i] = (ushort)U16(hmtx + i * 4);

            _loca = new uint[_numGlyphs + 1];
            for (int i = 0; i <= _numGlyphs; i++)
                _loca[i] = indexToLocFormat == 0 ? (uint)U16(loca + i * 2) * 2 : U32(loca + i * 4);

            _cmap = new CmapTable(_data, cmap);

            // Color glyphs (emoji): COLR layers reference outline glyphs in this same
            // font, coloured from the CPAL palette.
            if (tables.TryGetValue("COLR", out int colr) && tables.TryGetValue("CPAL", out int cpal))
                _color = new ColorTable(_data, colr, cpal);

            if (tables.TryGetValue("kern", out int kern))
                ParseKern(kern);
        }

        // ---- IColorGlyphFont ----

        public bool TryGetColorLayers(int glyphId, out IReadOnlyList<ColorGlyphLayer> layers)
        {
            if (_color != null) return _color.TryGetColorLayers(glyphId, out layers);
            layers = System.Array.Empty<ColorGlyphLayer>();
            return false;
        }

        // ---- IShapingFont ----

        public int GlyphIndex(char c) => _cmap.Map(c);

        public float Advance(int glyphId) => AdvanceWidth(glyphId) * _scale;

        public bool TryGetKerning(int leftGlyph, int rightGlyph, out float kerning)
            => _kerning.TryGetValue((leftGlyph, rightGlyph), out kerning);

        /// <summary>Convenience char-based rasterization (returns false if unmapped).</summary>
        public bool TryGetGlyph(char c, out GlyphBitmap glyph)
        {
            int gid = _cmap.Map(c);
            if (gid == 0)
            {
                glyph = default;
                return false;
            }
            return TryGetGlyph(gid, out glyph);
        }

        public bool TryGetGlyph(int gid, out GlyphBitmap glyph)
        {
            glyph = default;
            if (gid < 0 || gid >= _numGlyphs)
                return false;

            int advance = (int)MathF.Round(AdvanceWidth(gid) * _scale);

            List<PathFigure> figures = BuildGlyphFigures(gid);
            if (figures.Count == 0)
            {
                // Blank glyph (e.g. space): advance only.
                glyph = new GlyphBitmap(Array.Empty<byte>(), 0, 0, advance, 0, BaseEmPixels);
                return true;
            }

            CoverageMask mask = PathRasterizer.Rasterize(new PathGeometry(FillRule.NonZero, figures));
            if (mask.IsEmpty)
            {
                glyph = new GlyphBitmap(Array.Empty<byte>(), 0, 0, advance, 0, BaseEmPixels);
                return true;
            }

            int bearingX = (int)MathF.Round(mask.OriginX);
            int bearingY = (int)MathF.Round(-mask.OriginY); // mask origin is above the baseline (negative y)
            glyph = new GlyphBitmap(mask.Coverage, mask.Width, mask.Height, advance, bearingX, bearingY);
            return true;
        }

        /// <summary>IGlyphOutlineFont: glyph outline by index (at BaseEmPixels, baseline y=0).</summary>
        public bool TryGetGlyphOutline(int glyphId, out List<PathFigure> figures)
        {
            figures = (glyphId >= 0 && glyphId < _numGlyphs) ? BuildGlyphFigures(glyphId) : new List<PathFigure>();
            return figures.Count > 0;
        }

        private ushort AdvanceWidth(int gid)
            => _advanceWidths[gid < _numHMetrics ? gid : _numHMetrics - 1];

        /// <summary>True if the glyph for <paramref name="c"/> is a composite glyph.</summary>
        public bool IsCompositeGlyph(char c)
        {
            int gid = _cmap.Map(c);
            if (gid == 0) return false;
            uint start = _loca[gid], end = _loca[gid + 1];
            if (end <= start) return false;
            return (short)U16(_glyfOffset + (int)start) < 0;
        }

        // ---- glyf outline reading ----

        // A contour in font units (y up): on-curve flags parallel the points.
        private readonly struct Contour
        {
            public readonly Vector2[] Points;
            public readonly bool[] OnCurve;
            public Contour(Vector2[] points, bool[] onCurve) { Points = points; OnCurve = onCurve; }
        }

        // Reads a glyph's contours in font units, resolving composite components.
        private List<Contour> ReadGlyphContours(int gid, int depth)
        {
            if (depth > 5 || gid < 0 || gid >= _numGlyphs) return new List<Contour>();
            uint start = _loca[gid];
            uint end = _loca[gid + 1];
            if (end <= start) return new List<Contour>(); // no outline (e.g. space)

            int p = _glyfOffset + (int)start;
            int numContours = (short)U16(p);
            return numContours >= 0
                ? ReadSimpleContours(p + 10, numContours)
                : ReadCompositeContours(p + 10, depth);
        }

        private List<Contour> ReadSimpleContours(int p, int numContours)
        {
            var contours = new List<Contour>(numContours);
            if (numContours == 0) return contours;

            var endPts = new int[numContours];
            for (int i = 0; i < numContours; i++) { endPts[i] = U16(p); p += 2; }
            int numPoints = endPts[numContours - 1] + 1;
            if (numPoints <= 0) return contours;

            int instructionLength = U16(p); p += 2 + instructionLength;

            var flags = new byte[numPoints];
            for (int i = 0; i < numPoints;)
            {
                byte f = _data[p++];
                flags[i++] = f;
                if ((f & 0x08) != 0) // repeat
                {
                    int repeat = _data[p++];
                    while (repeat-- > 0 && i < numPoints) flags[i++] = f;
                }
            }

            var xs = new int[numPoints];
            int x = 0;
            for (int i = 0; i < numPoints; i++)
            {
                byte f = flags[i];
                if ((f & 0x02) != 0) { int dx = _data[p++]; x += (f & 0x10) != 0 ? dx : -dx; }
                else if ((f & 0x10) == 0) { x += (short)U16(p); p += 2; }
                xs[i] = x;
            }
            var ys = new int[numPoints];
            int y = 0;
            for (int i = 0; i < numPoints; i++)
            {
                byte f = flags[i];
                if ((f & 0x04) != 0) { int dy = _data[p++]; y += (f & 0x20) != 0 ? dy : -dy; }
                else if ((f & 0x20) == 0) { y += (short)U16(p); p += 2; }
                ys[i] = y;
            }

            int contourStart = 0;
            for (int ci = 0; ci < numContours; ci++)
            {
                int contourEnd = endPts[ci];
                int n = contourEnd - contourStart + 1;
                if (n >= 2)
                {
                    var pts = new Vector2[n];
                    var on = new bool[n];
                    for (int k = 0; k < n; k++)
                    {
                        int idx = contourStart + k;
                        pts[k] = new Vector2(xs[idx], ys[idx]); // font units, y up
                        on[k] = (flags[idx] & 0x01) != 0;
                    }
                    contours.Add(new Contour(pts, on));
                }
                contourStart = contourEnd + 1;
            }
            return contours;
        }

        // Composite glyph: each component references another glyph with a 2x2
        // transform + offset (font units). Components are read recursively and
        // their points transformed into this glyph's space.
        private List<Contour> ReadCompositeContours(int p, int depth)
        {
            var result = new List<Contour>();
            const int ARG_1_AND_2_ARE_WORDS = 0x0001;
            const int ARGS_ARE_XY_VALUES = 0x0002;
            const int WE_HAVE_A_SCALE = 0x0008;
            const int MORE_COMPONENTS = 0x0020;
            const int WE_HAVE_AN_X_AND_Y_SCALE = 0x0040;
            const int WE_HAVE_A_TWO_BY_TWO = 0x0080;

            bool more = true;
            while (more)
            {
                int flags = U16(p); p += 2;
                int compGid = U16(p); p += 2;

                int arg1, arg2;
                if ((flags & ARG_1_AND_2_ARE_WORDS) != 0) { arg1 = (short)U16(p); p += 2; arg2 = (short)U16(p); p += 2; }
                else { arg1 = (sbyte)_data[p++]; arg2 = (sbyte)_data[p++]; }

                float a = 1f, b = 0f, c = 0f, d = 1f;
                if ((flags & WE_HAVE_A_SCALE) != 0) { a = d = F2Dot14(p); p += 2; }
                else if ((flags & WE_HAVE_AN_X_AND_Y_SCALE) != 0) { a = F2Dot14(p); p += 2; d = F2Dot14(p); p += 2; }
                else if ((flags & WE_HAVE_A_TWO_BY_TWO) != 0)
                {
                    a = F2Dot14(p); p += 2; b = F2Dot14(p); p += 2;
                    c = F2Dot14(p); p += 2; d = F2Dot14(p); p += 2;
                }

                // Point-matching args are not supported; treat only XY offsets.
                float dx = (flags & ARGS_ARE_XY_VALUES) != 0 ? arg1 : 0f;
                float dy = (flags & ARGS_ARE_XY_VALUES) != 0 ? arg2 : 0f;

                foreach (Contour comp in ReadGlyphContours(compGid, depth + 1))
                {
                    var pts = new Vector2[comp.Points.Length];
                    for (int i = 0; i < pts.Length; i++)
                    {
                        Vector2 q = comp.Points[i];
                        pts[i] = new Vector2(a * q.X + c * q.Y + dx, b * q.X + d * q.Y + dy);
                    }
                    result.Add(new Contour(pts, comp.OnCurve));
                }

                more = (flags & MORE_COMPONENTS) != 0;
            }
            return result;
        }

        // Converts a glyph's font-unit contours to screen-space (y-down) figures,
        // applying synthetic bold/oblique simulations when requested.
        private List<PathFigure> BuildGlyphFigures(int gid)
        {
            List<Contour> contours = ReadGlyphContours(gid, 0);

            // Scale to base pixels and flip to y-down screen space.
            var scaled = new List<(Vector2[] Pts, bool[] On)>(contours.Count);
            foreach (Contour contour in contours)
            {
                if (contour.Points.Length < 2) continue;
                var pts = new Vector2[contour.Points.Length];
                for (int i = 0; i < pts.Length; i++)
                    pts[i] = new Vector2(contour.Points[i].X * _scale, -contour.Points[i].Y * _scale);
                scaled.Add((pts, contour.OnCurve));
            }

            if (_emboldenStrength > 0f) Embolden(scaled, _emboldenStrength);
            if (_shear != 0f)
                foreach ((Vector2[] pts, _) in scaled)
                    for (int i = 0; i < pts.Length; i++)
                        pts[i] = new Vector2(pts[i].X - _shear * pts[i].Y, pts[i].Y); // lean right above the baseline

            var figures = new List<PathFigure>(scaled.Count);
            foreach ((Vector2[] pts, bool[] on) in scaled)
                figures.Add(BuildContourFigure(pts, on));
            return figures;
        }

        // Emulates DirectWrite's bold simulation: offset every point (on- and
        // off-curve) outward along the contour normal. A single global fill
        // orientation (from the summed signed area) is used for all contours so
        // outer contours grow and holes (reverse-wound) shrink -- adding ink
        // everywhere, the same effect as FreeType's outline embolden.
        private static void Embolden(List<(Vector2[] Pts, bool[] On)> contours, float strength)
        {
            float totalArea = 0f;
            foreach ((Vector2[] pts, _) in contours) totalArea += SignedArea(pts);
            float sense = totalArea >= 0f ? -1f : 1f;

            foreach ((Vector2[] pts, _) in contours)
            {
                int n = pts.Length;
                var shifted = new Vector2[n];
                for (int i = 0; i < n; i++)
                {
                    Vector2 cur = pts[i];
                    Vector2 nIn = Perp(Norm(cur - pts[(i - 1 + n) % n]));
                    Vector2 nOut = Perp(Norm(pts[(i + 1) % n] - cur));
                    Vector2 nrm = nIn + nOut;
                    float len = nrm.Length();
                    shifted[i] = len > 1e-4f ? cur + sense * strength * (nrm / len) : cur;
                }
                Array.Copy(shifted, pts, n);
            }
        }

        private static float SignedArea(Vector2[] p)
        {
            float a = 0f;
            for (int i = 0; i < p.Length; i++)
            {
                Vector2 u = p[i], v = p[(i + 1) % p.Length];
                a += u.X * v.Y - v.X * u.Y;
            }
            return a * 0.5f;
        }

        private static Vector2 Perp(Vector2 v) => new(-v.Y, v.X);
        private static Vector2 Norm(Vector2 v) { float l = v.Length(); return l > 1e-4f ? v / l : default; }

        private float F2Dot14(int offset) => (short)U16(offset) / 16384f;

        // Converts a TrueType quadratic contour (with implied on-curve midpoints
        // between consecutive off-curve points) into a closed PathFigure.
        private static PathFigure BuildContourFigure(Vector2[] pts, bool[] on)
        {
            int n = pts.Length;
            int firstOn = -1;
            for (int i = 0; i < n; i++) if (on[i]) { firstOn = i; break; }

            Vector2 startPoint = firstOn >= 0 ? pts[firstOn] : Mid(pts[0], pts[n - 1]);
            var figure = new PathFigure(startPoint) { Closed = true };

            int startIndex = firstOn >= 0 ? firstOn : 0;
            bool havePendingControl = false;
            Vector2 control = default;

            for (int k = 1; k <= n; k++)
            {
                int i = (startIndex + k) % n;
                Vector2 p = pts[i];
                bool pOn = firstOn >= 0 ? on[i] : false; // all-off contour: treat every point as off

                if (pOn)
                {
                    if (havePendingControl) { figure.Segments.Add(new QuadraticBezierSegment(control, p)); havePendingControl = false; }
                    else figure.Segments.Add(new LineSegment(p));
                }
                else if (!havePendingControl)
                {
                    control = p;
                    havePendingControl = true;
                }
                else
                {
                    Vector2 mid = Mid(control, p);     // implied on-curve point
                    figure.Segments.Add(new QuadraticBezierSegment(control, mid));
                    control = p;
                }
            }

            // Close back to the start.
            if (havePendingControl) figure.Segments.Add(new QuadraticBezierSegment(control, startPoint));
            return figure;
        }

        private static Vector2 Mid(Vector2 a, Vector2 b) => new((a.X + b.X) * 0.5f, (a.Y + b.Y) * 0.5f);

        // ---- kern (legacy pairwise kerning, format 0) ----

        // Many modern fonts carry kerning in GPOS instead; this reads the simple
        // legacy 'kern' table when present. The shaping seam works regardless;
        // fonts without a kern table simply report no kerning.
        private void ParseKern(int kern)
        {
            if (U16(kern) != 0) return; // OpenType 'kern' version 0 only
            int nTables = U16(kern + 2);
            int p = kern + 4;
            for (int t = 0; t < nTables; t++)
            {
                int length = U16(p + 2);
                int coverage = U16(p + 4);
                int format = coverage >> 8;
                bool horizontal = (coverage & 0x1) != 0;
                if (format == 0 && horizontal)
                {
                    int nPairs = U16(p + 6);
                    int pair = p + 14; // after subtable header (6) + format-0 header (8)
                    for (int i = 0; i < nPairs; i++)
                    {
                        int left = U16(pair);
                        int right = U16(pair + 2);
                        int value = (short)U16(pair + 4);
                        _kerning[(left, right)] = value * _scale;
                        pair += 6;
                    }
                }
                p += length;
            }
        }

        // ---- big-endian primitives + table directory ----

        private Dictionary<string, int> ReadTableDirectory()
        {
            // Table records start after the 12-byte sfnt header; table offsets within
            // are absolute file offsets (so they stay valid for a face inside a .ttc).
            int numTables = U16(_sfntBase + 4);
            var tables = new Dictionary<string, int>(numTables);
            int p = _sfntBase + 12;
            for (int i = 0; i < numTables; i++)
            {
                string tag = System.Text.Encoding.ASCII.GetString(_data, p, 4);
                int offset = (int)U32(p + 8);
                tables[tag] = offset;
                p += 16;
            }
            return tables;
        }

        private static int Require(Dictionary<string, int> tables, string tag)
            => tables.TryGetValue(tag, out int off) ? off : throw new InvalidOperationException($"TrueType font missing '{tag}' table.");

        private int U16(int offset) => (_data[offset] << 8) | _data[offset + 1];

        private uint U32(int offset)
            => ((uint)_data[offset] << 24) | ((uint)_data[offset + 1] << 16) | ((uint)_data[offset + 2] << 8) | _data[offset + 3];
    }
}
