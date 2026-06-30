// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// A pure-managed reader for OpenType fonts with PostScript (CFF) outlines -- the
// counterpart to TrueTypeFont for the large family of fonts that store cubic
// outlines in a 'CFF ' table instead of quadratic 'glyf' contours (most Adobe
// fonts, Source/Noto Sans, many UI faces). It parses the sfnt container for
// metrics (head/maxp/hhea/hmtx/cmap) and runs a Type 2 charstring interpreter to
// turn each glyph into the engine's PathGeometry, which the shared PathRasterizer
// fills -- so CFF text reuses the same anti-aliased path as everything else.
//
// Supports: Type 2 charstrings (all path + arithmetic operators, local/global
// subrs, the flex family), CID-keyed fonts (FDArray/FDSelect per-glyph subrs),
// cmap formats 4/12/6/0, and DirectWrite-style synthetic bold/oblique. Not
// supported: the legacy 'seac' accent composition and CFF2 -- both rare in the
// shipping fonts this targets.
//

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Text;
using Microsoft.Wpf.Interop.WebGpu.Composition;

namespace Microsoft.Wpf.Interop.WebGpu.Composition.Text
{
    internal sealed class CffFont : IFont, IGlyphOutlineFont, IColorGlyphFont
    {
        private const int BaseEmPixels = 48;
        private const float ObliqueShear = 0.36397023f;   // tan(20°)
        private const float EmboldenFraction = 0.02f;

        private readonly byte[] _data;
        private readonly float _scale;          // font units -> base pixels
        private readonly int _numGlyphs;
        private readonly ushort[] _advanceWidths;
        private readonly int _numHMetrics;
        private readonly CmapTable _cmap;
        private readonly ColorTable? _color;    // COLR/CPAL color glyphs, null if absent
        private readonly float _emboldenStrength;
        private readonly float _shear;

        // CFF charstrings + subrs.
        private readonly CffIndex _charStrings;
        private readonly CffIndex _globalSubrs;
        private readonly int _gsubrBias;
        private readonly CffIndex _localSubrs;   // non-CID
        private readonly int _lsubrBias;

        // CID-keyed: each glyph selects a font dict (FDSelect) with its own local subrs.
        private readonly bool _isCid;
        private readonly byte[]? _fdSelect;       // per-glyph font-dict index
        private readonly CffIndex[]? _fdLocalSubrs;
        private readonly int[]? _fdLsubrBias;

        public int PixelsPerEm => BaseEmPixels;
        public int GlyphCount => _numGlyphs;
        /// <summary>True for a CID-keyed CFF (glyphs select per-FD local subrs).</summary>
        public bool IsCidKeyed => _isCid;

        /// <summary>True if the sfnt at <paramref name="sfntOffset"/> has PostScript (CFF) outlines.</summary>
        public static bool IsCff(byte[] data, int sfntOffset = 0)
        {
            // 'OTTO' sfnt version, or any sfnt carrying a 'CFF ' table.
            if (data.Length >= sfntOffset + 4 && data[sfntOffset] == (byte)'O' && data[sfntOffset + 1] == (byte)'T'
                && data[sfntOffset + 2] == (byte)'T' && data[sfntOffset + 3] == (byte)'O')
                return true;
            return TableDirectory(data, sfntOffset).ContainsKey("CFF ");
        }

        // <paramref name="sfntOffset"/> is the byte offset of this face's sfnt header,
        // non-zero when the face lives inside an OpenType Collection (.ttc/.otc).
        public CffFont(byte[] data, bool synthesizeBold = false, bool synthesizeOblique = false, int sfntOffset = 0)
        {
            _data = data;
            if (synthesizeBold) _emboldenStrength = BaseEmPixels * EmboldenFraction;
            if (synthesizeOblique) _shear = ObliqueShear;

            Dictionary<string, int> tables = TableDirectory(data, sfntOffset);
            int head = Require(tables, "head");
            int maxp = Require(tables, "maxp");
            int hhea = Require(tables, "hhea");
            int hmtx = Require(tables, "hmtx");
            int cmap = Require(tables, "cmap");
            int cff = Require(tables, "CFF ");

            int unitsPerEm = U16(head + 18);
            _scale = BaseEmPixels / (float)unitsPerEm;
            _numGlyphs = U16(maxp + 4);
            _numHMetrics = U16(hhea + 34);

            _advanceWidths = new ushort[_numHMetrics];
            for (int i = 0; i < _numHMetrics; i++)
                _advanceWidths[i] = (ushort)U16(hmtx + i * 4);

            _cmap = new CmapTable(_data, cmap);
            if (tables.TryGetValue("COLR", out int colr) && tables.TryGetValue("CPAL", out int cpal))
                _color = new ColorTable(_data, colr, cpal);

            // ---- CFF: header -> Name INDEX -> Top DICT INDEX -> String INDEX -> Global Subr INDEX ----
            int hdrSize = _data[cff + 2];
            int p = cff + hdrSize;
            CffIndex nameIndex = ReadIndex(p, out p);
            CffIndex topDictIndex = ReadIndex(p, out p);
            CffIndex stringIndex = ReadIndex(p, out p);
            _globalSubrs = ReadIndex(p, out p);
            _gsubrBias = Bias(_globalSubrs.Count);
            _ = nameIndex; _ = stringIndex;

            (int topStart, int topEnd) = topDictIndex.Range(0);
            Dictionary<int, double[]> topDict = ParseDict(topStart, topEnd);

            int charStringsOff = cff + (int)topDict[17][0];
            _charStrings = ReadIndex(charStringsOff, out _);
            if (_charStrings.Count != 0 && _charStrings.Count != _numGlyphs)
                _numGlyphs = _charStrings.Count; // CFF is authoritative for the glyph count

            _isCid = topDict.ContainsKey(1230); // ROS operator marks a CID-keyed font
            if (_isCid)
            {
                LoadCidSubrs(cff, topDict, out _fdSelect, out _fdLocalSubrs, out _fdLsubrBias);
            }
            else if (topDict.TryGetValue(18, out double[]? priv) && priv.Length == 2)
            {
                int privSize = (int)priv[0];
                int privOff = cff + (int)priv[1];
                Dictionary<int, double[]> privDict = ParseDict(privOff, privOff + privSize);
                if (privDict.TryGetValue(19, out double[]? subrsRel))
                {
                    _localSubrs = ReadIndex(privOff + (int)subrsRel[0], out _);
                    _lsubrBias = Bias(_localSubrs.Count);
                }
            }
        }

        // ---- IShapingFont / IGlyphSource ----

        public int GlyphIndex(char c) => _cmap.Map(c);
        public float Advance(int glyphId) => AdvanceWidth(glyphId) * _scale;
        public bool TryGetKerning(int leftGlyph, int rightGlyph, out float kerning) { kerning = 0f; return false; }

        private ushort AdvanceWidth(int gid)
            => _advanceWidths.Length == 0 ? (ushort)0 : _advanceWidths[gid < _numHMetrics ? gid : _numHMetrics - 1];

        public bool TryGetGlyph(int gid, out GlyphBitmap glyph)
        {
            glyph = default;
            if (gid < 0 || gid >= _numGlyphs) return false;

            int advance = (int)MathF.Round(AdvanceWidth(gid) * _scale);
            List<PathFigure> figures = BuildGlyphFigures(gid);
            if (figures.Count == 0)
            {
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
            int bearingY = (int)MathF.Round(-mask.OriginY);
            glyph = new GlyphBitmap(mask.Coverage, mask.Width, mask.Height, advance, bearingX, bearingY);
            return true;
        }

        // ---- IColorGlyphFont ----

        public bool TryGetColorLayers(int glyphId, out IReadOnlyList<ColorGlyphLayer> layers)
        {
            if (_color != null) return _color.TryGetColorLayers(glyphId, out layers);
            layers = Array.Empty<ColorGlyphLayer>();
            return false;
        }

        // ---- IGlyphOutlineFont ----

        public bool TryGetGlyphOutline(int glyphId, out List<PathFigure> figures)
        {
            figures = (glyphId >= 0 && glyphId < _numGlyphs) ? BuildGlyphFigures(glyphId) : new List<PathFigure>();
            return figures.Count > 0;
        }

        // Runs the glyph's charstring, then maps the resulting font-unit (y-up)
        // outline into base-pixel y-down space and applies synthetic bold/oblique.
        private List<PathFigure> BuildGlyphFigures(int gid)
        {
            if (gid < 0 || gid >= _charStrings.Count) return new List<PathFigure>();

            var interp = new Type2Interp(this, LocalSubrsFor(gid), LocalBiasFor(gid));
            (int cs, int csEnd) = _charStrings.Range(gid);
            interp.Run(cs, csEnd);
            List<PathFigure> figures = interp.Figures;

            // Font units (y up) -> base pixels (y down).
            TransformFigures(figures, p => new Vector2(p.X * _scale, -p.Y * _scale));
            if (_emboldenStrength > 0f) EmboldenFigures(figures, _emboldenStrength);
            if (_shear != 0f) TransformFigures(figures, p => new Vector2(p.X - _shear * p.Y, p.Y));
            return figures;
        }

        private CffIndex LocalSubrsFor(int gid)
        {
            if (_isCid && _fdSelect != null && _fdLocalSubrs != null)
                return _fdLocalSubrs[_fdSelect[gid]];
            return _localSubrs;
        }

        private int LocalBiasFor(int gid)
        {
            if (_isCid && _fdSelect != null && _fdLsubrBias != null)
                return _fdLsubrBias[_fdSelect[gid]];
            return _lsubrBias;
        }

        // ---- CID font dicts (FDArray + FDSelect) ----

        private void LoadCidSubrs(int cff, Dictionary<int, double[]> topDict,
            out byte[]? fdSelect, out CffIndex[]? fdLocalSubrs, out int[]? fdLsubrBias)
        {
            fdSelect = topDict.TryGetValue(1237, out double[]? fdsel) ? ReadFdSelect(cff + (int)fdsel[0]) : null;

            if (topDict.TryGetValue(1236, out double[]? fdarr))
            {
                CffIndex fdIndex = ReadIndex(cff + (int)fdarr[0], out _);
                fdLocalSubrs = new CffIndex[fdIndex.Count];
                fdLsubrBias = new int[fdIndex.Count];
                for (int i = 0; i < fdIndex.Count; i++)
                {
                    (int s, int e) = fdIndex.Range(i);
                    Dictionary<int, double[]> fontDict = ParseDict(s, e);
                    if (fontDict.TryGetValue(18, out double[]? priv) && priv.Length == 2)
                    {
                        int privSize = (int)priv[0];
                        int privOff = cff + (int)priv[1];
                        Dictionary<int, double[]> privDict = ParseDict(privOff, privOff + privSize);
                        if (privDict.TryGetValue(19, out double[]? subrsRel))
                        {
                            fdLocalSubrs[i] = ReadIndex(privOff + (int)subrsRel[0], out _);
                            fdLsubrBias[i] = Bias(fdLocalSubrs[i].Count);
                        }
                    }
                }
            }
            else { fdLocalSubrs = null; fdLsubrBias = null; }
        }

        private byte[] ReadFdSelect(int pos)
        {
            var map = new byte[_numGlyphs];
            int format = _data[pos];
            if (format == 0)
            {
                for (int i = 0; i < _numGlyphs; i++) map[i] = _data[pos + 1 + i];
            }
            else if (format == 3)
            {
                int nRanges = U16(pos + 1);
                int rp = pos + 3;
                for (int i = 0; i < nRanges; i++)
                {
                    int first = U16(rp);
                    int fd = _data[rp + 2];
                    int next = U16(rp + 3);
                    for (int g = first; g < next && g < _numGlyphs; g++) map[g] = (byte)fd;
                    rp += 3;
                }
            }
            return map;
        }

        // ---- Type 2 charstring interpreter ----

        private sealed class Type2Interp
        {
            private readonly CffFont _font;
            private readonly CffIndex _localSubrs;
            private readonly int _lsubrBias;

            private readonly double[] _stack = new double[48];
            private int _sp;
            private float _x, _y;
            private int _nStems;
            private bool _widthParsed;
            private bool _ended;
            private PathFigure? _open;

            public readonly List<PathFigure> Figures = new();

            public Type2Interp(CffFont font, CffIndex localSubrs, int lsubrBias)
            {
                _font = font; _localSubrs = localSubrs; _lsubrBias = lsubrBias;
            }

            public void Run(int start, int end) => Exec(start, end, 0);

            private void Push(double v) { if (_sp < _stack.Length) _stack[_sp++] = v; }
            private void Clear() => _sp = 0;

            private void MoveTo(float nx, float ny)
            {
                if (_open != null) _open.Closed = true;
                _x = nx; _y = ny;
                _open = new PathFigure(new Vector2(_x, _y)) { Closed = true };
                Figures.Add(_open);
            }
            private void LineTo(float nx, float ny)
            {
                _x = nx; _y = ny;
                _open?.Segments.Add(new LineSegment(new Vector2(_x, _y)));
            }
            private void CurveTo(float c1x, float c1y, float c2x, float c2y, float nx, float ny)
            {
                _open?.Segments.Add(new CubicBezierSegment(new Vector2(c1x, c1y), new Vector2(c2x, c2y), new Vector2(nx, ny)));
                _x = nx; _y = ny;
            }

            // The first stack-clearing operator may carry a leading width; drop it.
            private void TakeWidth(bool oddIsWidth, int evenExpected)
            {
                if (_widthParsed) return;
                _widthParsed = true;
                bool hasWidth = oddIsWidth ? (_sp & 1) == 1 : _sp > evenExpected;
                if (hasWidth) { Array.Copy(_stack, 1, _stack, 0, --_sp); }
            }

            private void Exec(int p, int end, int depth)
            {
                if (depth > 10) return;
                while (p < end && !_ended)
                {
                    int b0 = _font._data[p++];
                    if (b0 >= 32 || b0 == 28)
                    {
                        // operand
                        if (b0 == 28) { Push((short)((_font._data[p] << 8) | _font._data[p + 1])); p += 2; }
                        else if (b0 < 247) Push(b0 - 139);
                        else if (b0 < 251) { Push((b0 - 247) * 256 + _font._data[p] + 108); p++; }
                        else if (b0 < 255) { Push(-(b0 - 251) * 256 - _font._data[p] - 108); p++; }
                        else { // 255: 16.16 fixed
                            int v = (_font._data[p] << 24) | (_font._data[p + 1] << 16) | (_font._data[p + 2] << 8) | _font._data[p + 3];
                            Push(v / 65536.0); p += 4;
                        }
                        continue;
                    }

                    switch (b0)
                    {
                        case 1: case 3: case 18: case 23: // h/v stem (hm)
                            TakeWidth(oddIsWidth: true, 0);
                            _nStems += _sp / 2; Clear();
                            break;
                        case 19: case 20: // hintmask / cntrmask
                            TakeWidth(oddIsWidth: true, 0);
                            _nStems += _sp / 2; Clear();
                            p += (_nStems + 7) / 8; // skip mask bytes
                            break;
                        case 21: // rmoveto
                            TakeWidth(oddIsWidth: false, 2);
                            MoveTo(_x + (float)_stack[0], _y + (float)_stack[1]); Clear();
                            break;
                        case 22: // hmoveto
                            TakeWidth(oddIsWidth: false, 1);
                            MoveTo(_x + (float)_stack[0], _y); Clear();
                            break;
                        case 4: // vmoveto
                            TakeWidth(oddIsWidth: false, 1);
                            MoveTo(_x, _y + (float)_stack[0]); Clear();
                            break;
                        case 5: // rlineto
                            for (int i = 0; i + 1 < _sp; i += 2) LineTo(_x + (float)_stack[i], _y + (float)_stack[i + 1]);
                            Clear();
                            break;
                        case 6: // hlineto (alternating, start horizontal)
                            AlternatingLines(horizontalFirst: true); Clear();
                            break;
                        case 7: // vlineto (alternating, start vertical)
                            AlternatingLines(horizontalFirst: false); Clear();
                            break;
                        case 8: // rrcurveto
                            for (int i = 0; i + 5 < _sp; i += 6) RelCurve(i);
                            Clear();
                            break;
                        case 24: // rcurveline
                        {
                            int i = 0;
                            for (; i + 5 < _sp - 2; i += 6) RelCurve(i);
                            LineTo(_x + (float)_stack[i], _y + (float)_stack[i + 1]);
                            Clear();
                            break;
                        }
                        case 25: // rlinecurve
                        {
                            int i = 0;
                            for (; i + 1 < _sp - 6; i += 2) LineTo(_x + (float)_stack[i], _y + (float)_stack[i + 1]);
                            RelCurve(i);
                            Clear();
                            break;
                        }
                        case 26: VvCurveto(); Clear(); break;
                        case 27: HhCurveto(); Clear(); break;
                        case 30: VhCurveto(startVertical: true); Clear(); break;
                        case 31: VhCurveto(startVertical: false); Clear(); break;
                        case 10: // callsubr
                        {
                            int idx = (int)_stack[--_sp] + _lsubrBias;
                            if (idx >= 0 && idx < _localSubrs.Count)
                            {
                                (int s, int e) = _localSubrs.Range(idx);
                                Exec(s, e, depth + 1);
                            }
                            break;
                        }
                        case 29: // callgsubr
                        {
                            int idx = (int)_stack[--_sp] + _font._gsubrBias;
                            if (idx >= 0 && idx < _font._globalSubrs.Count)
                            {
                                (int s, int e) = _font._globalSubrs.Range(idx);
                                Exec(s, e, depth + 1);
                            }
                            break;
                        }
                        case 11: // return
                            return;
                        case 14: // endchar (seac accent composition not supported)
                            TakeWidth(oddIsWidth: false, 0);
                            if (_open != null) _open.Closed = true;
                            _ended = true;
                            return;
                        case 12: // escape: two-byte operators
                        {
                            int b1 = _font._data[p++];
                            switch (b1)
                            {
                                case 34: HFlex(); break;
                                case 35: Flex(); break;
                                case 36: HFlex1(); break;
                                case 37: Flex1(); break;
                                default: break; // arithmetic/other escapes: ignore
                            }
                            Clear();
                            break;
                        }
                        default:
                            Clear();
                            break;
                    }
                }
            }

            private void RelCurve(int i)
            {
                float c1x = _x + (float)_stack[i], c1y = _y + (float)_stack[i + 1];
                float c2x = c1x + (float)_stack[i + 2], c2y = c1y + (float)_stack[i + 3];
                CurveTo(c1x, c1y, c2x, c2y, c2x + (float)_stack[i + 4], c2y + (float)_stack[i + 5]);
            }

            private void AlternatingLines(bool horizontalFirst)
            {
                bool horizontal = horizontalFirst;
                for (int i = 0; i < _sp; i++)
                {
                    if (horizontal) LineTo(_x + (float)_stack[i], _y);
                    else LineTo(_x, _y + (float)_stack[i]);
                    horizontal = !horizontal;
                }
            }

            // vvcurveto: {dxa? dya dxb dyb dyc}+  (optional leading dx1 if arg count % 4 == 1)
            private void VvCurveto()
            {
                int i = 0;
                float dx1 = 0f;
                if ((_sp & 3) == 1) { dx1 = (float)_stack[0]; i = 1; }
                for (; i + 3 < _sp; i += 4)
                {
                    float c1x = _x + dx1, c1y = _y + (float)_stack[i];
                    float c2x = c1x + (float)_stack[i + 1], c2y = c1y + (float)_stack[i + 2];
                    CurveTo(c1x, c1y, c2x, c2y, c2x, c2y + (float)_stack[i + 3]);
                    dx1 = 0f;
                }
            }

            // hhcurveto: {dya? dxa dxb dyb dxc}+  (optional leading dy1 if arg count % 4 == 1)
            private void HhCurveto()
            {
                int i = 0;
                float dy1 = 0f;
                if ((_sp & 3) == 1) { dy1 = (float)_stack[0]; i = 1; }
                for (; i + 3 < _sp; i += 4)
                {
                    float c1x = _x + (float)_stack[i], c1y = _y + dy1;
                    float c2x = c1x + (float)_stack[i + 1], c2y = c1y + (float)_stack[i + 2];
                    CurveTo(c1x, c1y, c2x, c2y, c2x + (float)_stack[i + 3], c2y);
                    dy1 = 0f;
                }
            }

            // vhcurveto / hvcurveto: alternating tangent directions, optional final df.
            private void VhCurveto(bool startVertical)
            {
                int i = 0;
                bool vertical = startVertical;
                int remaining = _sp;
                while (remaining >= 4)
                {
                    bool last = remaining < 8;
                    float lastExtra = (last && (remaining == 5)) ? (float)_stack[i + 4] : 0f;
                    if (vertical)
                    {
                        float c1x = _x, c1y = _y + (float)_stack[i];
                        float c2x = c1x + (float)_stack[i + 1], c2y = c1y + (float)_stack[i + 2];
                        float nx = c2x + (float)_stack[i + 3], ny = c2y + lastExtra;
                        CurveTo(c1x, c1y, c2x, c2y, nx, ny);
                    }
                    else
                    {
                        float c1x = _x + (float)_stack[i], c1y = _y;
                        float c2x = c1x + (float)_stack[i + 1], c2y = c1y + (float)_stack[i + 2];
                        float nx = c2x + lastExtra, ny = c2y + (float)_stack[i + 3];
                        CurveTo(c1x, c1y, c2x, c2y, nx, ny);
                    }
                    i += 4; remaining -= 4; vertical = !vertical;
                }
            }

            // flex variants (12 34..37): two curves, output as-is.
            private void Flex()
            {
                float c1x = _x + (float)_stack[0], c1y = _y + (float)_stack[1];
                float c2x = c1x + (float)_stack[2], c2y = c1y + (float)_stack[3];
                float jx = c2x + (float)_stack[4], jy = c2y + (float)_stack[5];
                CurveTo(c1x, c1y, c2x, c2y, jx, jy);
                float d1x = jx + (float)_stack[6], d1y = jy + (float)_stack[7];
                float d2x = d1x + (float)_stack[8], d2y = d1y + (float)_stack[9];
                CurveTo(d1x, d1y, d2x, d2y, d2x + (float)_stack[10], d2y + (float)_stack[11]);
            }
            private void HFlex()
            {
                float c1x = _x + (float)_stack[0], c1y = _y;
                float c2x = c1x + (float)_stack[1], c2y = c1y + (float)_stack[2];
                float jx = c2x + (float)_stack[3], jy = c2y;
                CurveTo(c1x, c1y, c2x, c2y, jx, jy);
                float d1x = jx + (float)_stack[4], d1y = jy;
                float d2x = d1x + (float)_stack[5], d2y = _y;
                CurveTo(d1x, d1y, d2x, d2y, d2x + (float)_stack[6], d2y);
            }
            private void HFlex1()
            {
                float startY = _y;
                float c1x = _x + (float)_stack[0], c1y = _y + (float)_stack[1];
                float c2x = c1x + (float)_stack[2], c2y = c1y + (float)_stack[3];
                float jx = c2x + (float)_stack[4], jy = c2y;
                CurveTo(c1x, c1y, c2x, c2y, jx, jy);
                float d1x = jx + (float)_stack[5], d1y = jy;
                float d2x = d1x + (float)_stack[6], d2y = d1y + (float)_stack[7];
                CurveTo(d1x, d1y, d2x, d2y, d2x + (float)_stack[8], startY);
            }
            private void Flex1()
            {
                float startX = _x, startY = _y;
                float dx = 0f, dy = 0f;
                for (int k = 0; k < 10; k += 2) { dx += (float)_stack[k]; dy += (float)_stack[k + 1]; }
                float c1x = _x + (float)_stack[0], c1y = _y + (float)_stack[1];
                float c2x = c1x + (float)_stack[2], c2y = c1y + (float)_stack[3];
                float jx = c2x + (float)_stack[4], jy = c2y + (float)_stack[5];
                CurveTo(c1x, c1y, c2x, c2y, jx, jy);
                float d1x = jx + (float)_stack[6], d1y = jy + (float)_stack[7];
                float d2x = d1x + (float)_stack[8], d2y = d1y + (float)_stack[9];
                // The last delta (d6) is applied to whichever axis moved less overall.
                float ex, ey;
                if (Math.Abs(dx) > Math.Abs(dy)) { ex = d2x + (float)_stack[10]; ey = startY; }
                else { ex = startX; ey = d2y + (float)_stack[10]; }
                CurveTo(d1x, d1y, d2x, d2y, ex, ey);
            }
        }

        private static int Bias(int nSubrs) => nSubrs < 1240 ? 107 : nSubrs < 33900 ? 1131 : 32768;

        // ---- synthetic bold / oblique over figures (CFF outlines are explicit cubics) ----

        private static void TransformFigures(List<PathFigure> figs, Func<Vector2, Vector2> f)
        {
            foreach (PathFigure fig in figs)
            {
                fig.Start = f(fig.Start);
                List<PathSegment> segs = fig.Segments;
                for (int k = 0; k < segs.Count; k++)
                    segs[k] = segs[k] switch
                    {
                        LineSegment l => new LineSegment(f(l.Point)),
                        QuadraticBezierSegment q => new QuadraticBezierSegment(f(q.Control), f(q.Point)),
                        CubicBezierSegment c => new CubicBezierSegment(f(c.Control1), f(c.Control2), f(c.Point)),
                        var s => s,
                    };
            }
        }

        // Offsets every node (anchors and control points) outward along the contour
        // normal, with a single global fill orientation so holes shrink -- the same
        // emboldening model as TrueTypeFont/FreeType, applied to the explicit cubics.
        private static void EmboldenFigures(List<PathFigure> figs, float strength)
        {
            float total = 0f;
            foreach (PathFigure fig in figs) total += FigureArea(CollectPoints(fig));
            float sense = total >= 0f ? -1f : 1f;

            foreach (PathFigure fig in figs)
            {
                List<Vector2> pts = CollectPoints(fig);
                int n = pts.Count;
                if (n < 2) continue;
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
                WritePoints(fig, shifted);
            }
        }

        private static List<Vector2> CollectPoints(PathFigure fig)
        {
            var pts = new List<Vector2> { fig.Start };
            foreach (PathSegment s in fig.Segments)
                switch (s)
                {
                    case LineSegment l: pts.Add(l.Point); break;
                    case QuadraticBezierSegment q: pts.Add(q.Control); pts.Add(q.Point); break;
                    case CubicBezierSegment c: pts.Add(c.Control1); pts.Add(c.Control2); pts.Add(c.Point); break;
                }
            return pts;
        }

        private static void WritePoints(PathFigure fig, Vector2[] pts)
        {
            int i = 0;
            fig.Start = pts[i++];
            List<PathSegment> segs = fig.Segments;
            for (int k = 0; k < segs.Count; k++)
                segs[k] = segs[k] switch
                {
                    LineSegment => new LineSegment(pts[i++]),
                    QuadraticBezierSegment => new QuadraticBezierSegment(pts[i++], pts[i++]),
                    CubicBezierSegment => new CubicBezierSegment(pts[i++], pts[i++], pts[i++]),
                    var s => s,
                };
        }

        private static float FigureArea(List<Vector2> p)
        {
            float a = 0f;
            for (int i = 0; i < p.Count; i++)
            {
                Vector2 u = p[i], v = p[(i + 1) % p.Count];
                a += u.X * v.Y - v.X * u.Y;
            }
            return a * 0.5f;
        }

        private static Vector2 Perp(Vector2 v) => new(-v.Y, v.X);
        private static Vector2 Norm(Vector2 v) { float l = v.Length(); return l > 1e-4f ? v / l : default; }

        // ---- CFF INDEX + DICT ----

        private readonly struct CffIndex
        {
            public readonly int Count;
            private readonly int[] _offsets;   // (Count+1) 1-based offsets
            private readonly int _dataBase;    // byte preceding the object data

            public CffIndex(int count, int[] offsets, int dataBase)
            { Count = count; _offsets = offsets; _dataBase = dataBase; }

            public (int start, int end) Range(int i) => (_dataBase + _offsets[i], _dataBase + _offsets[i + 1]);
        }

        private CffIndex ReadIndex(int pos, out int next)
        {
            int count = U16(pos);
            if (count == 0) { next = pos + 2; return new CffIndex(0, new[] { 1, 1 }, pos); }

            int offSize = _data[pos + 2];
            int op = pos + 3;
            var offsets = new int[count + 1];
            for (int i = 0; i <= count; i++) { offsets[i] = ReadOffset(op, offSize); op += offSize; }
            int dataBase = op - 1;
            next = dataBase + offsets[count];
            return new CffIndex(count, offsets, dataBase);
        }

        private int ReadOffset(int pos, int offSize)
        {
            int v = 0;
            for (int i = 0; i < offSize; i++) v = (v << 8) | _data[pos + i];
            return v;
        }

        // Parses a DICT byte range into operator -> operands. Two-byte operators
        // (escape 12) are keyed as 1200 + second byte.
        private Dictionary<int, double[]> ParseDict(int start, int end)
        {
            var dict = new Dictionary<int, double[]>();
            var operands = new List<double>();
            int p = start;
            while (p < end)
            {
                int b0 = _data[p];
                if (b0 <= 21)
                {
                    int op = b0; p++;
                    if (b0 == 12) { op = 1200 + _data[p]; p++; }
                    dict[op] = operands.ToArray();
                    operands.Clear();
                }
                else if (b0 == 28) { operands.Add((short)((_data[p + 1] << 8) | _data[p + 2])); p += 3; }
                else if (b0 == 29) { operands.Add((int)U32(p + 1)); p += 5; }
                else if (b0 == 30) { operands.Add(ParseReal(p + 1, out int np)); p = np; }
                else if (b0 >= 32 && b0 <= 246) { operands.Add(b0 - 139); p++; }
                else if (b0 >= 247 && b0 <= 250) { operands.Add((b0 - 247) * 256 + _data[p + 1] + 108); p += 2; }
                else if (b0 >= 251 && b0 <= 254) { operands.Add(-(b0 - 251) * 256 - _data[p + 1] - 108); p += 2; }
                else p++; // reserved
            }
            return dict;
        }

        // Nibble-encoded real number (operator 30 payload), terminated by nibble 0xf.
        private double ParseReal(int p, out int next)
        {
            var sb = new StringBuilder();
            bool done = false;
            while (!done)
            {
                int b = _data[p++];
                for (int half = 0; half < 2; half++)
                {
                    int nib = half == 0 ? (b >> 4) : (b & 0xF);
                    switch (nib)
                    {
                        case <= 9: sb.Append((char)('0' + nib)); break;
                        case 0xa: sb.Append('.'); break;
                        case 0xb: sb.Append('E'); break;
                        case 0xc: sb.Append("E-"); break;
                        case 0xe: sb.Append('-'); break;
                        case 0xf: done = true; break;
                        default: break; // 0xd reserved
                    }
                    if (done) break;
                }
            }
            next = p;
            return double.TryParse(sb.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : 0.0;
        }

        // ---- sfnt table directory + big-endian primitives ----

        private static Dictionary<string, int> TableDirectory(byte[] data, int sfntBase = 0)
        {
            int numTables = (data[sfntBase + 4] << 8) | data[sfntBase + 5];
            var tables = new Dictionary<string, int>(numTables);
            int p = sfntBase + 12;
            for (int i = 0; i < numTables && p + 16 <= data.Length; i++)
            {
                string tag = Encoding.ASCII.GetString(data, p, 4);
                int offset = (int)(((uint)data[p + 8] << 24) | ((uint)data[p + 9] << 16) | ((uint)data[p + 10] << 8) | data[p + 11]);
                tables[tag] = offset;
                p += 16;
            }
            return tables;
        }

        private static int Require(Dictionary<string, int> tables, string tag)
            => tables.TryGetValue(tag, out int off) ? off : throw new InvalidOperationException($"CFF font missing '{tag}' table.");

        private int U16(int offset) => (_data[offset] << 8) | _data[offset + 1];
        private uint U32(int offset)
            => ((uint)_data[offset] << 24) | ((uint)_data[offset + 1] << 16) | ((uint)_data[offset + 2] << 8) | _data[offset + 3];
    }
}
