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

    internal sealed class TrueTypeFont : IFont, IGlyphOutlineFont
    {
        // Glyphs are rasterized with the em square at this many pixels; the
        // renderer scales the atlas quad to the requested EmSize.
        private const int BaseEmPixels = 48;

        private readonly byte[] _data;
        private readonly float _scale;          // font units -> base pixels
        private readonly int _numGlyphs;
        private readonly int _glyfOffset;
        private readonly uint[] _loca;          // numGlyphs+1 glyph data offsets
        private readonly ushort[] _advanceWidths;
        private readonly int _numHMetrics;
        private readonly Dictionary<char, int> _cmap = new();
        private readonly Dictionary<(int, int), float> _kerning = new(); // base pixels

        public int PixelsPerEm => BaseEmPixels;

        public int GlyphCount => _numGlyphs;

        public TrueTypeFont(byte[] data)
        {
            _data = data;

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

            ParseCmap(cmap);
            if (tables.TryGetValue("kern", out int kern))
                ParseKern(kern);
        }

        // ---- IShapingFont ----

        public int GlyphIndex(char c) => _cmap.TryGetValue(c, out int gid) ? gid : 0;

        public float Advance(int glyphId) => AdvanceWidth(glyphId) * _scale;

        public bool TryGetKerning(int leftGlyph, int rightGlyph, out float kerning)
            => _kerning.TryGetValue((leftGlyph, rightGlyph), out kerning);

        /// <summary>Convenience char-based rasterization (returns false if unmapped).</summary>
        public bool TryGetGlyph(char c, out GlyphBitmap glyph)
        {
            if (!_cmap.TryGetValue(c, out int gid))
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
            if (!_cmap.TryGetValue(c, out int gid)) return false;
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

        // Converts a glyph's font-unit contours to screen-space (y-down) figures.
        private List<PathFigure> BuildGlyphFigures(int gid)
        {
            List<Contour> contours = ReadGlyphContours(gid, 0);
            var figures = new List<PathFigure>(contours.Count);
            foreach (Contour contour in contours)
            {
                if (contour.Points.Length < 2) continue;
                var pts = new Vector2[contour.Points.Length];
                for (int i = 0; i < pts.Length; i++)
                    pts[i] = new Vector2(contour.Points[i].X * _scale, -contour.Points[i].Y * _scale);
                figures.Add(BuildContourFigure(pts, contour.OnCurve));
            }
            return figures;
        }

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

        // ---- cmap (format 4) ----

        private void ParseCmap(int cmap)
        {
            int numTables = U16(cmap + 2);
            int best = -1, bestScore = -1;
            for (int i = 0; i < numTables; i++)
            {
                int rec = cmap + 4 + i * 8;
                int platform = U16(rec);
                int encoding = U16(rec + 2);
                int sub = cmap + (int)U32(rec + 4);

                // Only format 4 (segment-mapped BMP) is supported; a font may
                // also carry format 12/6/0 subtables which we skip here.
                if (U16(sub) != 4) continue;

                int score = (platform, encoding) switch
                {
                    (3, 1) => 4,
                    (0, _) => 3,
                    (3, 0) => 1,
                    _ => 0,
                };
                if (score > bestScore) { bestScore = score; best = sub; }
            }
            if (best < 0) return; // no format-4 subtable

            int segCount = U16(best + 6) / 2;
            int endCodes = best + 14;
            int startCodes = endCodes + segCount * 2 + 2; // + reservedPad
            int idDeltas = startCodes + segCount * 2;
            int idRangeOffsets = idDeltas + segCount * 2;

            for (char c = (char)0x20; c < 0x2FF; c++)
            {
                int gid = MapCharFormat4(c, segCount, endCodes, startCodes, idDeltas, idRangeOffsets);
                if (gid != 0) _cmap[c] = gid;
            }
        }

        private int MapCharFormat4(char c, int segCount, int endCodes, int startCodes, int idDeltas, int idRangeOffsets)
        {
            for (int i = 0; i < segCount; i++)
            {
                if (c > U16(endCodes + i * 2)) continue;
                int start = U16(startCodes + i * 2);
                if (c < start) return 0;

                int idDelta = (short)U16(idDeltas + i * 2);
                int idRangeOffset = U16(idRangeOffsets + i * 2);
                if (idRangeOffset == 0)
                    return (c + idDelta) & 0xFFFF;

                // idRangeOffset is relative to its own slot.
                int glyphIndexAddr = idRangeOffsets + i * 2 + idRangeOffset + (c - start) * 2;
                int gid = U16(glyphIndexAddr);
                return gid == 0 ? 0 : (gid + idDelta) & 0xFFFF;
            }
            return 0;
        }

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
            int numTables = U16(4);
            var tables = new Dictionary<string, int>(numTables);
            int p = 12;
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
