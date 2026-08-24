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
// Scope: TrueType simple AND composite glyphs, the cmap formats CmapTable reads,
// horizontal metrics, colour glyphs (COLR/CPAL and CBDT/sbix), and OpenType font
// VARIATIONS -- fvar/avar/gvar, so a variable font draws at the weight and slant
// asked for instead of at its default master. See VariableFont.cs.
//
// PostScript (CFF) outlines are the sibling reader, CffFont; complex-script
// shaping is PresentationCore's ManagedOpenTypeShaper, above this seam. CFF2 --
// a variable font with PostScript outlines -- is read by neither.
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

    internal sealed class TrueTypeFont : IFont, IGlyphOutlineFont, IColorGlyphFont, IBitmapGlyphFont
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
        private readonly BitmapGlyphTable? _bitmaps;   // CBDT/CBLC colour bitmap glyphs, null if absent
        private readonly Dictionary<(int, int), float> _kerning = new(); // base pixels

        // Synthetic style (DirectWrite font simulations): when WPF requests a weight/
        // style the family has no real face for, DWrite returns the regular outlines
        // flagged BOLD/OBLIQUE and synthesizes the look. We replicate that here.
        private readonly float _emboldenStrength;   // base-pixel outline dilation per side (0 = none)
        private readonly float _shear;              // oblique x-shear coefficient (0 = none)

        private readonly VariableFont? _variations;  // fvar/avar/gvar, null on a static font

        // Advance deltas fall out of the same gvar read that varies the outline (the phantom points),
        // so they are kept as that read produces them rather than computed a second time.
        private readonly Dictionary<int, float> _advanceDeltas = new();

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

            Dictionary<string, int> tables = ReadTableDirectory();
            int head = Require(tables, "head");
            int maxp = Require(tables, "maxp");
            int hhea = Require(tables, "hhea");
            int hmtx = Require(tables, "hmtx");
            int cmap = Require(tables, "cmap");

            // Outlines are OPTIONAL, because a colour BITMAP font has none.
            //
            // Noto Color Emoji (Linux, Android) and its kin store every glyph as a PNG in CBDT and
            // ship no 'glyf' or 'loca' at all. Requiring them threw during construction, the font
            // resolver caught it and returned null, and emoji rendered as nothing. A font with
            // neither outlines nor bitmaps is still an error -- it can draw nothing whatsoever.
            bool hasOutlines = tables.TryGetValue("loca", out int loca) & tables.TryGetValue("glyf", out int glyf);
            _glyfOffset = hasOutlines ? glyf : -1;

            int glyphCount = U16(maxp + 4);
            if (tables.TryGetValue("CBLC", out int cblc) && tables.TryGetValue("CBDT", out int cbdt))
            {
                var bitmaps = new BitmapGlyphTable(_data, cblc, cbdt);
                if (bitmaps.HasStrikes) _bitmaps = bitmaps;
            }
            else if (tables.TryGetValue("sbix", out int sbix))
            {
                // Apple Color Emoji. Unlike CBDT this usually sits ALONGSIDE outlines (the glyphs
                // have blank or placeholder contours), so reaching here does not mean the font is
                // bitmap-only -- it means its colour artwork lives in sbix.
                var bitmaps = new BitmapGlyphTable(_data, sbix, glyphCount, sbix: true);
                if (bitmaps.HasStrikes) _bitmaps = bitmaps;
            }

            if (!hasOutlines && _bitmaps is null)
                throw new InvalidOperationException("TrueType font has neither outlines ('glyf'/'loca') nor colour bitmaps ('CBDT'/'CBLC').");

            int unitsPerEm = U16(head + 18);
            int indexToLocFormat = (short)U16(head + 50);
            _scale = BaseEmPixels / (float)unitsPerEm;
            _numGlyphs = U16(maxp + 4);
            _numHMetrics = U16(hhea + 34);

            _advanceWidths = new ushort[_numHMetrics];
            for (int i = 0; i < _numHMetrics; i++)
                _advanceWidths[i] = (ushort)U16(hmtx + i * 4);

            _loca = new uint[hasOutlines ? _numGlyphs + 1 : 0];
            for (int i = 0; i < _loca.Length; i++)
                _loca[i] = indexToLocFormat == 0 ? (uint)U16(loca + i * 2) * 2 : U32(loca + i * 4);

            _cmap = new CmapTable(_data, cmap);

            // Font variations. Asking the font for the weight or slant it was DESIGNED with beats
            // faking one from the default master, so the simulation flags become an instance request
            // wherever the face has an axis that can answer them, and only fall back to dilating and
            // shearing the outline where it has not. See SelectInstance.
            _variations = VariableFont.TryRead(_data, tables);
            bool variedBold = false, variedOblique = false;
            if (_variations is not null)
                SelectInstance(_variations, synthesizeBold, synthesizeOblique, out variedBold, out variedOblique);

            if (synthesizeBold && !variedBold) _emboldenStrength = BaseEmPixels * EmboldenFraction;
            if (synthesizeOblique && !variedOblique) _shear = ObliqueShear;

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

        // ---- IBitmapGlyphFont ----

        public bool TryGetGlyphBitmap(int glyphId, out BitmapGlyph glyph)
        {
            if (_bitmaps != null) return _bitmaps.TryGetGlyphBitmap(glyphId, out glyph);
            glyph = default;
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
        // Built outlines, by glyph id.
        //
        // A glyph's outline in font units is the same every time it is asked for, and it is asked for
        // a great deal: MilcoreEngine builds the geometry of EVERY glyph of every text run while
        // parsing render data, so a visual holding a paragraph rebuilds hundreds of outlines from the
        // font tables -- contours, composites and variation deltas -- each time its render data
        // changes. Dragging a splitter changes it on every frame.
        //
        // Handing back the cached list is safe because nobody mutates it: the callers (GlyphRunPainter
        // .ScaleFigures, WgpuSceneRenderer.TransformGeometry) all build new figures rather than moving
        // these. Bounded by the glyphs the application actually draws.
        private readonly Dictionary<int, List<PathFigure>> _outlineCache = new();

        public bool TryGetGlyphOutline(int glyphId, out List<PathFigure> figures)
        {
            if (_outlineCache.TryGetValue(glyphId, out List<PathFigure>? cached))
            {
                figures = cached;
                return figures.Count > 0;
            }

            figures = (glyphId >= 0 && glyphId < _numGlyphs) ? BuildGlyphFigures(glyphId) : new List<PathFigure>();
            _outlineCache[glyphId] = figures;
            return figures.Count > 0;
        }

        /// <summary>
        ///  Turns the bold/oblique simulation flags into a point in the font's own design space.
        /// </summary>
        /// <remarks>
        ///  A variable font already contains the bold the caller is asking for; emboldening its
        ///  default master instead produces a shape the designer never drew, with the wrong stem
        ///  contrast and the wrong sidebearings. So when there is a 'wght' axis, Bold means "go as
        ///  far towards 700 as this axis goes", and only a font without one gets the dilation.
        ///  Italic is the same story told twice, because a family may express it as a 0/1 'ital'
        ///  switch or as a continuous 'slnt' angle in degrees (negative leans right).
        /// </remarks>
        private static void SelectInstance(VariableFont variations, bool bold, bool oblique,
                                           out bool variedBold, out bool variedOblique)
        {
            variedBold = variedOblique = false;
            var request = new Dictionary<uint, float>();

            if (bold && variations.TryGetAxis(VariableFont.AxisWeight, out VariationAxis weight)
                && weight.Max > weight.Default)
            {
                request[VariableFont.AxisWeight] = Math.Min(700f, weight.Max);
                variedBold = true;
            }

            if (oblique)
            {
                if (variations.TryGetAxis(VariableFont.AxisItalic, out VariationAxis ital) && ital.Max >= 1f)
                {
                    request[VariableFont.AxisItalic] = 1f;
                    variedOblique = true;
                }
                else if (variations.TryGetAxis(VariableFont.AxisSlant, out VariationAxis slnt) && slnt.Min < 0f)
                {
                    // 'slnt' is degrees of clockwise lean, so an italic is NEGATIVE. -20 matches the
                    // synthetic shear this replaces (tan 20 degrees).
                    request[VariableFont.AxisSlant] = Math.Max(-20f, slnt.Min);
                    variedOblique = true;
                }
            }

            if (request.Count > 0) variations.SetInstance(request);
        }

        private float AdvanceWidth(int gid)
        {
            float advance = _advanceWidths[gid < _numHMetrics ? gid : _numHMetrics - 1];
            if (_variations is null || !_variations.IsVaried) return advance;

            // An instance moves the advance as well as the outline, and the two come from the same
            // deltas -- so the glyph has to have been read for the answer to exist. Reading it here
            // is what keeps a caller that only ever asks for metrics (measuring a line before
            // drawing it) from getting the default master's widths.
            if (!_advanceDeltas.TryGetValue(gid, out float delta))
            {
                ReadGlyphContours(gid, 0);
                _advanceDeltas.TryGetValue(gid, out delta);
            }
            return advance + delta;
        }

        /// <summary>True if the glyph for <paramref name="c"/> is a composite glyph.</summary>
        public bool IsCompositeGlyph(char c)
        {
            int gid = _cmap.Map(c);
            if (gid == 0 || _loca.Length == 0) return false;
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
            // _loca is empty on a colour BITMAP font (CBDT/CBLC), which has no outlines at all.
            if (depth > 5 || gid < 0 || gid >= _numGlyphs || _loca.Length == 0) return new List<Contour>();
            uint start = _loca[gid];
            uint end = _loca[gid + 1];
            if (end <= start) return new List<Contour>(); // no outline (e.g. space)

            int p = _glyfOffset + (int)start;
            int numContours = (short)U16(p);
            return numContours >= 0
                ? ReadSimpleContours(p + 10, numContours, gid)
                : ReadCompositeContours(p + 10, depth, gid);
        }

        private List<Contour> ReadSimpleContours(int p, int numContours, int gid)
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

            // One array of every point in the glyph, plus the four PHANTOM points gvar addresses as
            // if they were ordinary ones. Their absolute positions do not matter here -- they take
            // part in no contour, so nothing interpolates against them -- but the gap between the
            // first two IS the advance width, so their deltas are where a variable font says how
            // much wider Bold is than Regular.
            var points = new Vector2[numPoints + 4];
            for (int i = 0; i < numPoints; i++) points[i] = new Vector2(xs[i], ys[i]);   // font units, y up
            points[numPoints + 1] = new Vector2(RawAdvanceWidth(gid), 0f);

            ApplyVariations(gid, points, numPoints, endPts);

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
                        pts[k] = points[idx];
                        on[k] = (flags[idx] & 0x01) != 0;
                    }
                    contours.Add(new Contour(pts, on));
                }
                contourStart = contourEnd + 1;
            }
            return contours;
        }

        private ushort RawAdvanceWidth(int gid)
            => _advanceWidths.Length == 0 ? (ushort)0 : _advanceWidths[gid < _numHMetrics ? gid : _numHMetrics - 1];

        /// <summary>
        ///  Moves a glyph's points to the selected instance, and records what that did to its advance.
        /// </summary>
        private void ApplyVariations(int gid, Vector2[] points, int realPointCount, int[] contourEnds)
        {
            if (_variations is null || !_variations.IsVaried || !_variations.HasOutlineDeltas) return;

            Vector2[]? deltas = _variations.GetGlyphDeltas(gid, points.Length, contourEnds, points);
            if (deltas is null)
            {
                _advanceDeltas[gid] = 0f;
                return;
            }

            for (int i = 0; i < points.Length; i++) points[i] += deltas[i];

            // The advance is the distance between the two horizontal phantom points, so what the
            // instance did to it is the difference of their deltas.
            _advanceDeltas[gid] = deltas[realPointCount + 1].X - deltas[realPointCount].X;
        }

        /// <summary>One component of a composite glyph: which glyph, and where it sits.</summary>
        private struct Component
        {
            public int Gid;
            public float A, B, C, D;   // 2x2 transform
            public float Dx, Dy;       // offset, font units
        }

        // Composite glyph: each component references another glyph with a 2x2
        // transform + offset (font units). Components are read recursively and
        // their points transformed into this glyph's space.
        //
        // A variable font varies a composite by moving its COMPONENTS, not their outlines: gvar
        // treats each component's offset as one point, so an accented letter's accent shifts as the
        // weight changes. Reading the whole component list first is what makes that possible -- the
        // deltas are indexed by component number, so they cannot be applied while still parsing.
        private List<Contour> ReadCompositeContours(int p, int depth, int gid)
        {
            var result = new List<Contour>();
            const int ARG_1_AND_2_ARE_WORDS = 0x0001;
            const int ARGS_ARE_XY_VALUES = 0x0002;
            const int WE_HAVE_A_SCALE = 0x0008;
            const int MORE_COMPONENTS = 0x0020;
            const int WE_HAVE_AN_X_AND_Y_SCALE = 0x0040;
            const int WE_HAVE_A_TWO_BY_TWO = 0x0080;

            var components = new List<Component>();
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

                components.Add(new Component { Gid = compGid, A = a, B = b, C = c, D = d, Dx = dx, Dy = dy });
                more = (flags & MORE_COMPONENTS) != 0;
            }

            VaryComponents(gid, components);

            foreach (Component comp in components)
            {
                foreach (Contour c in ReadGlyphContours(comp.Gid, depth + 1))
                {
                    var pts = new Vector2[c.Points.Length];
                    for (int i = 0; i < pts.Length; i++)
                    {
                        Vector2 q = c.Points[i];
                        pts[i] = new Vector2(comp.A * q.X + comp.C * q.Y + comp.Dx,
                                             comp.B * q.X + comp.D * q.Y + comp.Dy);
                    }
                    result.Add(new Contour(pts, c.OnCurve));
                }
            }
            return result;
        }

        /// <summary>
        ///  Moves a composite's components to the selected instance.
        /// </summary>
        /// <remarks>
        ///  The "points" of a composite are its component offsets, one each, followed by the same
        ///  four phantom points a simple glyph has. There is no contour to interpolate along, so a
        ///  component the tuple does not mention simply does not move -- which is why no contour ends
        ///  are passed.
        /// </remarks>
        private void VaryComponents(int gid, List<Component> components)
        {
            if (_variations is null || !_variations.IsVaried || !_variations.HasOutlineDeltas) return;

            int n = components.Count;
            var points = new Vector2[n + 4];
            for (int i = 0; i < n; i++) points[i] = new Vector2(components[i].Dx, components[i].Dy);
            points[n + 1] = new Vector2(RawAdvanceWidth(gid), 0f);

            Vector2[]? deltas = _variations.GetGlyphDeltas(gid, points.Length, Array.Empty<int>(), points);
            if (deltas is null)
            {
                _advanceDeltas[gid] = 0f;
                return;
            }

            for (int i = 0; i < n; i++)
            {
                Component c = components[i];
                c.Dx += deltas[i].X;
                c.Dy += deltas[i].Y;
                components[i] = c;
            }
            _advanceDeltas[gid] = deltas[n + 1].X - deltas[n].X;
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
