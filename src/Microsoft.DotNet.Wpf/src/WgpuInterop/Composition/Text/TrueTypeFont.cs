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

    /// <summary>A face that can fit a glyph to a particular size before handing it over: the outline
    /// comes back in DEVICE PIXELS with its stems and its horizontal features standing on whole ones.
    /// <para>Separate from <see cref="IGlyphOutlineFont"/> because the two answer different questions.
    /// That one is asked for the shape and knows nothing about how big it will be drawn; this one
    /// cannot answer at all without being told, since the whole of the difference is which pixel grid
    /// the shape is being fitted to.</para></summary>
    internal interface IHintedGlyphFont
    {
        /// <summary>The glyph at <paramref name="pixelsPerEm"/>, baseline at y=0, y-down, grid-fitted.
        /// False when the glyph is blank or the face cannot be measured.</summary>
        bool TryGetHintedOutline(int glyphId, float pixelsPerEm, out List<PathFigure> figures);

        /// <summary>The whole-pixel advance the FACE gives this glyph at this size, if it ships one.
        /// False when the face has no table for the size, and the advance has to be computed.</summary>
        bool TryGetDeviceAdvance(int glyphId, float pixelsPerEm, out float advance);

        /// <summary>Whether the face asks to be smoothed in BOTH directions at this size.</summary>
        bool WantsSymmetricSmoothing(float pixelsPerEm);
    }

    internal sealed class TrueTypeFont : IFont, IGlyphOutlineFont, IColorGlyphFont, IBitmapGlyphFont,
                                         IHintedGlyphFont
    {
        // Glyphs are rasterized with the em square at this many pixels; the
        // renderer scales the atlas quad to the requested EmSize.
        private const int BaseEmPixels = 48;

        private readonly byte[] _data;
        private readonly int _sfntBase;         // offset of this face's sfnt header (non-zero inside a .ttc)
        private readonly float _scale;          // font units -> base pixels
        private readonly int _unitsPerEm;       // the design grid the outlines are drawn on
        private readonly int _numGlyphs;
        private int _hmtxOffset = -1;
        private Dictionary<string, int> _tableLengths = new();
        private int _fontProgramTable = -1, _controlProgramTable = -1, _controlValueTable = -1;
        private int _maxpOffset = -1;
        private TrueTypeInterpreter? _interpreter;
        private bool _interpreterTried;
        private int _hdmxOffset = -1;   // first device-metrics row, or -1 when the face ships none
        private int _hdmxStride;        // bytes per row
        private int _hdmxRecords;       // how many rows
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

        // The slant a SYNTHESIZED italic gets, for a face that ships no italic of its own -- Tahoma
        // is the common one. 20 degrees is DirectWrite's simulation, and we are matching GDI.
        // WPF_OBLIQUE_SHEAR overrides it so the difference is a measurement, not an assumption.
        private static readonly float ObliqueShear =
            float.TryParse(Environment.GetEnvironmentVariable("WPF_OBLIQUE_SHEAR"),
                           System.Globalization.NumberStyles.Float,
                           System.Globalization.CultureInfo.InvariantCulture, out float os)
                ? os : 0.36397023f;                          // tan(20 degrees)
        // AND THE MAGNITUDE WAS NEVER THE PROBLEM. This was briefly set to 0.20 because that
        // measured better than 0.364 -- both were leaning the glyph the WRONG WAY, so the sweep was
        // choosing between two wrong answers and the whole range was flat to within 3%. With the
        // sign corrected, Tahoma's synthesized italic goes 785,245 -> 283,963, and the optimum sits
        // at 0.34-0.36, which is tan(20 degrees) as it always was.
        // The tell was that shear 0 -- no slant at all -- measured BETTER than any slant we applied
        // (775,497), while Windows' italic is plainly slanted. A metric that prefers no effect to
        // the effect is not choosing a magnitude, it is telling you the sign is wrong.
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
            _gasp = tables.TryGetValue("gasp", out int gasp) ? gasp : -1;
            // The x-height and cap height, for telling a lowercase letter with an ascender from a
            // capital of the same bounding height. OS/2 carries both from version 2 on.
            if (tables.TryGetValue("OS/2", out int os2))
            {
                if (U16(os2) >= 2)
                {
                    _sxHeight = (short) U16(os2 + 86);
                    _sCapHeight = (short) U16(os2 + 88);
                }
                // usWinAscent / usWinDescent: the fallback line box, for a face with no usable VDMX.
                _winAscent = U16(os2 + 74);
                _winDescent = U16(os2 + 76);
            }
            _vdmx = tables.TryGetValue("VDMX", out int vdmx) ? vdmx : -1;

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
            _unitsPerEm = unitsPerEm;
            _scale = BaseEmPixels / (float)unitsPerEm;
            _numGlyphs = U16(maxp + 4);
            _numHMetrics = U16(hhea + 34);

            _hmtxOffset = hmtx;
            _advanceWidths = new ushort[_numHMetrics];
            for (int i = 0; i < _numHMetrics; i++)
                _advanceWidths[i] = (ushort)U16(hmtx + i * 4);

            _loca = new uint[hasOutlines ? _numGlyphs + 1 : 0];
            for (int i = 0; i < _loca.Length; i++)
                _loca[i] = indexToLocFormat == 0 ? (uint)U16(loca + i * 2) * 2 : U32(loca + i * 4);

            _cmap = new CmapTable(_data, cmap);

            // The face's own hinting: three streams of bytecode and a table of the designer's
            // reference measurements. Running them is what puts our glyphs where Windows puts its
            // own; without them the outline is fitted by analysis instead. See TrueTypeInterpreter.
            _fontProgramTable = tables.TryGetValue("fpgm", out int fpgm) ? fpgm : -1;
            _controlProgramTable = tables.TryGetValue("prep", out int prep) ? prep : -1;
            _controlValueTable = tables.TryGetValue("cvt ", out int cvt) ? cvt : -1;
            _maxpOffset = maxp;

            // 'hdmx' is the face's OWN answer to "how wide is this glyph at this pixel size", one
            // row per size, worked out by the designer from the hinted outline. Windows spaces text
            // with it, so text spaced any other way drifts against Windows' -- rounding each advance
            // arithmetically loses up to half a pixel per letter and a six-letter word came out two
            // pixels short of the same word beside it.
            if (tables.TryGetValue("hdmx", out int hdmx) && U16(hdmx) == 0)
            {
                int records = (short)U16(hdmx + 2);
                int stride = (int)U32(hdmx + 4);
                // Each row is a size, a maximum, then one byte per glyph; a row too short for this
                // face's glyphs is a damaged table and is better ignored than read past.
                if (records > 0 && stride >= _numGlyphs + 2)
                {
                    _hdmxOffset = hdmx + 8;
                    _hdmxStride = stride;
                    _hdmxRecords = records;
                }
            }

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

        /// <summary>Where the program left the two horizontal phantom points, in pixels, before any
        /// rounding of ours. Tells apart "the program widened the advance and we lost it" from "the
        /// program never touched the advance".</summary>
        internal (float Pp1, float Pp2) HintedPhantomsForTest(int glyphId, float pixelsPerEm)
        {
            TrueTypeInterpreter? interpreter = Interpreter();
            if (interpreter is null) return (float.NaN, float.NaN);
            GlyphProgram? glyph = HintedProgram(interpreter, glyphId, pixelsPerEm, 0);
            if (glyph is null) return (float.NaN, float.NaN);
            return (glyph.X[glyph.PointCount] / 64f, glyph.X[glyph.PointCount + 1] / 64f);
        }

        /// <summary>The advance before anything fits it -- 'hmtx' scaled to the size. What a
        /// device advance should NOT be equal to, on a face whose program touches the phantom
        /// points.</summary>
        internal float LinearAdvanceForTest(int glyphId, float pixelsPerEm)
            => AdvanceWidth(glyphId) * pixelsPerEm / _unitsPerEm;

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

        // ---- grid fitting ------------------------------------------------------------------------
        //
        // What a face needs measuring for -- where its lines of the alphabet are, how wide its stems
        // are drawn -- costs several glyph reads, so it is done once, on the first glyph anyone asks
        // to have fitted, and never for a face nothing is drawn in.
        private HintMetrics? _hintMetrics;
        private bool _hintMetricsTried;

        // Fitted outlines, by glyph and by size. A run of text asks for the same handful of glyphs at
        // one size over and over, and fitting is the expensive part of drawing them; the size is held
        // to a sixteenth of a pixel so that a smooth zoom does not fill this with near-duplicates.
        private readonly Dictionary<(int Glyph, int Size), List<PathFigure>> _hintedCache = new();
        private const int HintedCacheLimit = 4096;

        /// <summary>The design grid this face's outlines are drawn on, for a caller that wants to
        /// measure it the way the fitting does.</summary>
        internal int UnitsPerEmForHinting => _unitsPerEm;

        /// <summary>One glyph's contours in font units, y up -- what the fitting works on.</summary>
        internal List<(Vector2[] Pts, bool[] On)>? ContoursForHinting(int glyphId)
        {
            List<Contour> contours = ReadGlyphContours(glyphId, 0);
            if (contours.Count == 0) return null;
            var copy = new List<(Vector2[] Pts, bool[] On)>(contours.Count);
            foreach (Contour contour in contours)
                copy.Add(((Vector2[])contour.Points.Clone(), contour.OnCurve));
            return copy;
        }

        private HintMetrics? HintMetricsOfFace()
        {
            if (_hintMetricsTried)
                return _hintMetrics;
            _hintMetricsTried = true;
            try
            {
                _hintMetrics = GlyphHinter.Measure(_unitsPerEm, c =>
                {
                    int gid = _cmap.Map(c);
                    if (gid <= 0) return null;
                    List<Contour> contours = ReadGlyphContours(gid, 0);
                    if (contours.Count == 0) return null;
                    var copy = new List<(Vector2[] Pts, bool[] On)>(contours.Count);
                    foreach (Contour contour in contours)
                        copy.Add(((Vector2[])contour.Points.Clone(), contour.OnCurve));
                    return copy;
                });
            }
            catch (Exception)
            {
                _hintMetrics = null;     // a face this cannot be measured on is drawn unfitted
            }
            return _hintMetrics;
        }

        /// <summary>The face's own hinting machine, built once. Null when the face carries no
        /// hints, or when its tables are unreadable -- either way the outline is fitted by analysis
        /// instead, which is what a face without hints has always had.</summary>
        private TrueTypeInterpreter? Interpreter()
        {
            if (_interpreterTried) return _interpreter;
            _interpreterTried = true;

            if (_glyfOffset < 0 || _maxpOffset < 0) return null;

            // 'maxp' version 1.0 is the one carrying the limits; 0.5 belongs to a CFF face, which
            // has no TrueType hinting to run in the first place.
            if (U32(_maxpOffset) != 0x00010000) return null;

            byte[] fontProgram = TableBytes("fpgm", _fontProgramTable);
            byte[] controlProgram = TableBytes("prep", _controlProgramTable);
            if (fontProgram.Length == 0 && controlProgram.Length == 0) return null;

            int cvtLength = _controlValueTable >= 0 && _tableLengths.TryGetValue("cvt ", out int cl) ? cl : 0;
            var controlValues = new short[cvtLength / 2];
            for (int i = 0; i < controlValues.Length; i++)
                controlValues[i] = (short)U16(_controlValueTable + i * 2);

            var interpreter = new TrueTypeInterpreter(
                _data, _unitsPerEm, fontProgram, controlProgram, controlValues,
                maxStorage: U16(_maxpOffset + 18),
                maxFunctionDefs: U16(_maxpOffset + 20),
                maxStack: U16(_maxpOffset + 24),
                twilightPoints: U16(_maxpOffset + 16));

            return _interpreter = interpreter.IsUsable ? interpreter : null;
        }

        private byte[] TableBytes(string tag, int offset)
        {
            if (offset < 0 || !_tableLengths.TryGetValue(tag, out int length)) return Array.Empty<byte>();
            if (length <= 0 || offset + length > _data.Length) return Array.Empty<byte>();
            var bytes = new byte[length];
            Array.Copy(_data, offset, bytes, 0, length);
            return bytes;
        }

        /// <summary>IHintedGlyphFont: the whole-pixel advance this glyph is spaced by at this size.
        /// </summary>
        /// <remarks>
        ///  Two sources, in the order Windows consults them. 'hdmx' is the designer's own table of
        ///  device widths, and where it has a row for the size it is the answer. It does NOT have a
        ///  row for every size -- Segoe UI carries 11, 12, 13, 15, 16, 17, 19, 21 and up, and no
        ///  10, 14, 18 or 20 -- and at the sizes it skips the advance is whatever the face's own
        ///  program leaves between the phantom points, which is what the rasterizer running that
        ///  program ends up with.
        ///  <para>Rounding the scaled design advance instead, which is what this used to do at those
        ///  sizes, is a third answer that agrees with neither. It is wrong by less than a pixel per
        ///  letter, and that is exactly what makes it bad: the error is the same sign every time, so
        ///  it accumulates along the line. Thirteen letters of Segoe UI Italic at fourteen pixels an
        ///  em drifted far enough that every glyph after the first landed on different pixels from
        ///  Windows' -- a hundred and forty-two of them -- while the first one was exact.</para>
        /// </remarks>
        public bool TryGetDeviceAdvance(int glyphId, float pixelsPerEm, out float advance)
        {
            advance = 0f;
            if (glyphId < 0 || glyphId >= _numGlyphs || pixelsPerEm <= 0f) return false;

            // Both sources are written for whole pixel sizes. Anything between two of them is a size
            // nothing was measured at, and the caller's own rounding is as good an answer as any.
            int ppem = (int)MathF.Round(pixelsPerEm);
            if (MathF.Abs(pixelsPerEm - ppem) > 0.01f || ppem <= 0 || ppem > 255) return false;

            if (TryGetHdmxAdvance(glyphId, ppem, out advance)) return true;

            return TryGetHintedAdvance(glyphId, pixelsPerEm, out advance);
        }

        /// <summary>The line box GDI gives this face at this size: the ascent and descent a
        /// TEXTMETRIC reports, which is what every control that sizes itself to a line of text is
        /// measured against.
        /// <para>NOT the scaled design metrics. GDI reads 'VDMX', the table recording how far the
        /// HINTED outlines actually reach at each whole pixel size -- hinting moves them, so the
        /// design values are the wrong answer by more than rounding. Arial at twelve pixels an em
        /// has usWinAscent 1854 on a 2048 em, which scales to 10.86 pixels; GDI says TWELVE,
        /// because that is what Arial's own hinting program leaves the tallest glyph reaching.
        /// Times New Roman is the same. Both came out three pixels short, which put every line of
        /// them TWO PIXELS ABOVE Windows' on the specimen -- six of its twenty-four bands, and more
        /// error than every hinting fix in this file put together.</para>
        /// <para>A ratio range only counts when its bCharSet is 1. Tahoma is the face that proves
        /// it: it ships a VDMX whose single range is bCharSet 0, its numbers disagree with GDI from
        /// twelve pixels an em upwards (13/3 against 12/2), and GDI ignores it and scales the design
        /// metrics instead. Every other face measured here -- Segoe UI, Arial, Times, Verdana --
        /// carries bCharSet 1 and GDI follows its VDMX EXACTLY at all of 8..20 pixels an em.</para>
        /// <para>The fallback rounds; it does not truncate. Consolas ships no VDMX at all and its
        /// TEXTMETRIC is round(usWinAscent x scale) / round(usWinDescent x scale) at every one of
        /// those sizes, truncation being wrong at seven of them.</para></summary>
        public bool TryGetGdiLineMetrics(int ppem, out int ascent, out int descent)
        {
            ascent = descent = 0;
            if (ppem <= 0 || _unitsPerEm <= 0) return false;

            if (TryGetVdmxExtents(ppem, out int yMax, out int yMin))
            {
                ascent = yMax;
                descent = -yMin;
                return true;
            }

            if (_winAscent <= 0 && _winDescent <= 0) return false;
            ascent = (int) MathF.Round(_winAscent * (float) ppem / _unitsPerEm);
            descent = (int) MathF.Round(_winDescent * (float) ppem / _unitsPerEm);
            return true;
        }

        /// <summary>How far the hinted outlines reach at this size, from 'VDMX'. False when the face
        /// ships none, when no ratio range applies, or when the size is outside the range recorded.
        /// </summary>
        private bool TryGetVdmxExtents(int ppem, out int yMax, out int yMin)
        {
            yMax = yMin = 0;
            if (_vdmx < 0 || ppem <= 0 || ppem > 0xFFFF) return false;
            if (_vdmx + 6 > _data.Length) return false;

            int numRatios = U16(_vdmx + 4);
            if (numRatios <= 0) return false;
            int ratios = _vdmx + 6;
            int offsets = ratios + numRatios * 4;
            if (offsets + numRatios * 2 > _data.Length) return false;

            for (int i = 0; i < numRatios; i++)
            {
                int r = ratios + i * 4;
                // bCharSet 1 is the only one GDI honours -- see the remarks on TryGetGdiLineMetrics.
                if (_data[r] != 1) continue;
                // Square pixels: the aspect ratio is 1, so the range has to bracket it. An xRatio of
                // zero is the "applies to everything" record.
                int xRatio = _data[r + 1], yStart = _data[r + 2], yEnd = _data[r + 3];
                if (xRatio != 0 && !(xRatio == 1 && yStart <= 1 && yEnd >= 1)) continue;

                int group = _vdmx + U16(offsets + i * 2);
                if (group + 4 > _data.Length) continue;
                int recs = U16(group);
                int startSize = _data[group + 2], endSize = _data[group + 3];
                if (ppem < startSize || ppem > endSize) continue;
                if (group + 4 + recs * 6 > _data.Length) continue;

                for (int j = 0; j < recs; j++)
                {
                    int e = group + 4 + j * 6;
                    int size = U16(e);
                    if (size < ppem) continue;
                    // The records are ordered by size; the first one at or above the size asked for
                    // is the one that governs, so a gap in the table rounds UP rather than missing.
                    if (size > ppem) break;
                    yMax = (short) U16(e + 2);
                    yMin = (short) U16(e + 4);
                    return yMax != 0 || yMin != 0;
                }
            }
            return false;
        }

        /// <summary>The advance the FACE ships for this size, read straight from 'hdmx".
        /// <para>Separate from TryGetDeviceAdvance because that one falls back to HINTING the glyph
        /// to find out how wide it is -- which is fine for a caller measuring text and fatal for a
        /// caller inside the hinter: compatible widths asked for the advance from within
        /// HintedProgram and the recursion quietly produced glyphs with no ink at all. The parity
        /// total "improved" from 3,629,242 to 637,342 because most of the repertoire had stopped
        /// drawing (GDI's inked pixel count fell with it, 275,589 -> 29,609, which is the tell).</para>
        /// </summary>
        private bool TryGetHdmxAdvance(int glyphId, int ppem, out float advance)
        {
            advance = 0f;
            if (_hdmxOffset < 0) return false;
            for (int i = 0; i < _hdmxRecords; i++)
            {
                int row = _hdmxOffset + i * _hdmxStride;
                if (_data[row] != ppem) continue;
                advance = _data[row + 2 + glyphId];
                return advance > 0f;
            }
            return false;
        }

        // Hinting a glyph to ask how wide it is costs as much as hinting it to draw it, and a run of
        // text asks for the same handful of glyphs over and over.
        private readonly Dictionary<(int Glyph, int Size), float> _hintedAdvances = new();

        /// <summary>The distance the face's own program leaves between the two horizontal phantom
        /// points -- the advance the glyph is actually drawn with. False when there is no program to
        /// run, or the glyph has no outline for one to run on.</summary>
        private bool TryGetHintedAdvance(int glyphId, float pixelsPerEm, out float advance)
        {
            // NOT keyed by the hinting mode, because it is not measured in one: see below.
            var key = (glyphId, (int)MathF.Round(pixelsPerEm * 16f));
            if (_hintedAdvances.TryGetValue(key, out advance)) return advance > 0f;

            advance = 0f;
            TrueTypeInterpreter? interpreter = Interpreter();
            if (interpreter is not null)
            {
                // MEASURED BI-LEVEL, whatever mode we are drawing in. An advance under ClearType is
                // a COMPATIBLE width: the same number bi-level text would use, so that turning
                // ClearType on does not reflow the page. 'hdmx' is nothing more than a cache of
                // those numbers, which is why a face that ships one has always agreed with Windows
                // here. A face that ships none fell through to this, measured the ClearType-fitted
                // glyph -- reduced cut-in, halved minimum distance, deltas suppressed -- and got a
                // narrower answer.
                //
                // Verdana ships no 'hdmx'. It came out a pixel short on a b d e g o p q s w z W:
                // invisible on any one letter, THIRTEEN PIXELS of drift by the end of a line, and
                // every glyph past the third landing on different pixels from Windows'. The band
                // read as a fitting problem for a long time because that is what drift looks like.
                bool savedBi = TrueTypeInterpreter.BiLevelPass;
                TrueTypeInterpreter.BiLevelPass = true;
                try
                {
                    GlyphProgram? glyph = HintedProgram(interpreter, glyphId, pixelsPerEm, 0);
                    if (glyph is not null)
                    {
                        int span = glyph.X[glyph.PointCount + 1] - glyph.X[glyph.PointCount];
                        if (span > 0) advance = MathF.Round(span / 64f);
                    }
                }
                finally { TrueTypeInterpreter.BiLevelPass = savedBi; }
            }

            if (_hintedAdvances.Count > HintedCacheLimit) _hintedAdvances.Clear();
            _hintedAdvances[key] = advance;
            return advance > 0f;
        }

        /// <summary>IHintedGlyphFont: the glyph fitted to a pixel grid of the given size.</summary>
        public bool TryGetHintedOutline(int glyphId, float pixelsPerEm, out List<PathFigure> figures)
        {
            figures = s_noFigures;
            if (pixelsPerEm <= 0f || glyphId < 0 || glyphId >= _numGlyphs)
                return false;

            var key = (glyphId, (int)MathF.Round(pixelsPerEm * 16f) * 2 + (SubpixelFitting ? 1 : 0));
            if (_hintedCache.TryGetValue(key, out List<PathFigure>? cached))
            {
                figures = cached;
                return figures.Count > 0;
            }

            // A zoom asks for every size it passes through, so this cannot grow for ever. Text sits
            // at a handful of sizes; anything past that is an animation, and starting again costs one
            // frame of fitting rather than a growing heap.
            if (_hintedCache.Count > HintedCacheLimit)
                _hintedCache.Clear();

            // THE FACE'S OWN HINTS FIRST. Where the designer wrote a program for this glyph, it
            // is the answer -- it is what GDI runs, so it is what Windows' pixels come from. Only a
            // face that carries none, or one whose program will not run, falls through to fitting
            // the outline by analysis.
            // THE FACE'S OWN ANSWER FIRST, and it governs BOTH fitters. Skipping only the face's
            // program still left the analysis fitter running, which is our own invention and fits
            // just as hard -- at 7ppem it took Segoe UI from 88,963 of ink to 136,090 where GDI, told
            // the same thing by the same table, stays at 89,485. Where the designer says do not fit,
            // the outline as drawn is the answer.
            if (!FaceWantsGridFit(pixelsPerEm))
            {
                // The outline as drawn, SCALED TO THIS SIZE -- not a refusal. Callers of this method
                // are promised a device-pixel outline and scale everything else by the reciprocal of
                // the device scale; answering "no fitting available" sends them to the unfitted
                // outline in the face's own base pixels, which they then scale as if it were device
                // pixels. That is a factor of ppem/48 in the wrong direction and it showed: the
                // finished ClearType ink went to eighteen times GDI's.
                if (!TryGetGlyphOutline(glyphId, out List<PathFigure> plain))
                    return false;
                float k = pixelsPerEm / PixelsPerEm;
                var scaled = new List<PathFigure>(plain.Count);
                foreach (PathFigure f in plain)
                {
                    var copy = new PathFigure(new Vector2(f.Start.X * k, f.Start.Y * k)) { Closed = f.Closed };
                    foreach (PathSegment seg in f.Segments)
                        switch (seg)
                        {
                            case LineSegment l:
                                copy.Segments.Add(new LineSegment(new Vector2(l.Point.X * k, l.Point.Y * k)));
                                break;
                            case QuadraticBezierSegment q:
                                copy.Segments.Add(new QuadraticBezierSegment(
                                    new Vector2(q.Control.X * k, q.Control.Y * k),
                                    new Vector2(q.Point.X * k, q.Point.Y * k)));
                                break;
                            case CubicBezierSegment c:
                                copy.Segments.Add(new CubicBezierSegment(
                                    new Vector2(c.Control1.X * k, c.Control1.Y * k),
                                    new Vector2(c.Control2.X * k, c.Control2.Y * k),
                                    new Vector2(c.Point.X * k, c.Point.Y * k)));
                                break;
                        }
                    scaled.Add(copy);
                }
                _hintedCache[key] = scaled;
                figures = scaled;
                return scaled.Count > 0;
            }

            List<PathFigure>? hinted = RunFaceHints(glyphId, pixelsPerEm);
            if (hinted is not null && !FitIsPlausible(glyphId, pixelsPerEm, hinted))
            {
                hinted = null;
                System.Threading.Interlocked.Increment(ref s_implausibleFits);
                if (s_traceFits)
                    Console.Error.WriteLine($"[fit] rejected gid={glyphId} at {pixelsPerEm}ppem");
            }
            if (hinted is not null)
            {
                _hintedCache[key] = hinted;
                figures = hinted;
                return hinted.Count > 0;
            }

            if (!TryGetFittedOutline(glyphId, pixelsPerEm, out figures)) return false;
            _hintedCache[key] = figures;
            return figures.Count > 0;
        }

        private static int s_implausibleFits;

        private static readonly bool s_traceFits =
            Environment.GetEnvironmentVariable("WPF_FIT_TRACE") == "1";

        /// <summary>How many times a face's own fitting has been thrown away as implausible since
        /// the process started. Counted so the interpreter's accuracy is a NUMBER that a test can
        /// hold to rather than something noticed when a screenshot looks wrong.</summary>
        internal static int ImplausibleFits => System.Threading.Volatile.Read(ref s_implausibleFits);

        internal static void ResetImplausibleFits() => System.Threading.Interlocked.Exchange(ref s_implausibleFits, 0);

        private int _gasp = -1;
        private int _sxHeight, _sCapHeight;
        private int _winAscent, _winDescent;
        private int _vdmx = -1;         // 'VDMX' table offset, or -1 when the face ships none

        private const int GaspGridfit = 0x0001;
        private const int GaspSymmetricSmoothing = 0x0008;

        /// <summary>Whether the face asks for SYMMETRIC SMOOTHING at this size -- antialiasing in
        /// both directions rather than along the lamps only.
        /// <para>This is the whole of the 7-8ppem anomaly, and it was sitting in a table we already
        /// read. Segoe UI's 'gasp' says DOGRAY+SYMMETRIC_SMOOTHING at 8ppem and below, plain
        /// GRIDFIT+SYMMETRIC_GRIDFIT from 9 to 19, and symmetric smoothing again above that.
        /// Consolas moves the same boundary to 10. So GDI antialiases vertically at the small sizes
        /// and NOT in the middle band -- where we sampled one row at the scanline centre at every
        /// size, which is right for 9-19 and wrong below and above.</para>
        /// <para>It shows up as colour: measured over the whole repertoire, our lamp spread against
        /// GDI's is 1.193 at 7ppem and 1.153 at 8 -- we fringe far harder than GDI -- while 11-19ppem
        /// sit at 0.97-1.04. A vertical sample softens a horizontal edge without moving ink sideways,
        /// which is exactly the difference. No filter can produce it: filter width moves every size
        /// together, and this is a DIFFERENCE between sizes.</para></summary>
        public bool WantsSymmetricSmoothing(float pixelsPerEm) => GaspFlags(pixelsPerEm, GaspSymmetricSmoothing);

        private bool GaspFlags(float pixelsPerEm, int want)
        {
            if (_gasp < 0) return false;
            int ppem = (int) MathF.Round(pixelsPerEm);
            int ranges = U16(_gasp + 2);
            int at = _gasp + 4;
            for (int i = 0; i < ranges; i++, at += 4)
                if (ppem <= U16(at))
                    return (U16(at + 2) & want) != 0;
            return false;
        }

        /// <summary>Whether the face asks to be GRID-FITTED at this size.
        /// <para>The 'gasp' table is the designer saying at which sizes fitting helps and at which it
        /// does harm, and GDI obeys it. We did not read it at all, and the cost is measurable: Segoe
        /// UI asks for no gridfit at 8 pixels an em and below, where GDI's rendering carries 89,485
        /// of ink against its own unfitted 88,956 -- that is, it does not fit -- while ours fitted the
        /// same glyphs to 139,345. Half as much ink again, at the sizes where a fitting has the least
        /// room to be right.</para>
        /// <para>A face with no 'gasp' is fitted at every size, which is what a rasterizer does with
        /// one and what we did with all of them.</para></summary>
        private bool FaceWantsGridFit(float pixelsPerEm)
        {
            if (_gasp < 0) return true;

            int ppem = (int) MathF.Round(pixelsPerEm);
            int ranges = U16(_gasp + 2);
            int at = _gasp + 4;
            for (int i = 0; i < ranges; i++, at += 4)
            {
                // Ranges are listed in ascending order and the last one ends at 0xFFFF, so the first
                // whose limit is not below this size is the one that applies.
                if (ppem <= U16(at))
                    return (U16(at + 2) & GaspGridfit) != 0;
            }
            return true;
        }

        /// <summary>A sub-pixel x offset applied to the fitted outline, in 64ths of a pixel.</summary>
        internal static readonly int XOffset64 =
            int.TryParse(Environment.GetEnvironmentVariable("WPF_X_OFFSET"), out int xo) ? xo : 0;

        /// <summary>Whether a fitted glyph is scaled back onto the bi-level advance.
        /// <para>ON, mode 1. It implements what the paper calls compatible widths -- "the glyphs for
        /// this font size will be adjusted post hinting in order to return advance widths that are
        /// exactly the same as bi-level rendering" -- as a proportional scale of x onto the hdmx
        /// advance. We answer the GETINFO selector that says ClearType, so the face is entitled to
        /// expect it.</para>
        /// <para>It was OFF here for a long time on a measurement that said it did nothing, taken
        /// when the surrounding fitting was different. Re-measured against the text specimen it is
        /// the largest single correction found: 1,074,897 -> 870,799, and -- unlike every rounding
        /// rule tried beside it -- it improves the bands that are wrong WITHOUT touching the ones
        /// that are right. Lowercase at 11ppem 196,453 -> 130,130 and the sentence 180,450 ->
        /// 110,679, while the digits (20,168) and everything at 16ppem (50,593) do not move by a
        /// single unit. That is the signature of a correction that belongs: the glyphs whose ink had
        /// drifted inside their advance box are pulled back, and the ones already in place stay.</para>
        /// <para>NEVER call TryGetDeviceAdvance from here for the target width -- it hints the glyph
        /// to answer and we are inside the hinter. The recursion silently stops most of the
        /// repertoire drawing and reports itself as a large improvement.</para>
        /// <para>1 scales x onto the hdmx advance, 2 translates the glyph back onto its original
        /// left side bearing (measured: no effect), 0 does neither. WPF_CT_COMPATWIDTH.</para></summary>
        /// <summary>How far the fitted advance may be from the bi-level one, in percent, and still be
        /// scaled onto it.
        /// <para>25. Swept on the text specimen: 15% costs 1,025,604 (too many real corrections
        /// refused), 18-30% is a flat plateau at 852,397-855,257, and 40% and up returns to 870,831.
        /// The width of that plateau is why this is a fair value and not a fitted one -- anywhere in
        /// a twelve-point range gives the same answer to within 0.3%.</para>
        /// <para>WPF_CT_CWTOL.</para></summary>
        /// <summary>Glyphs no taller than this many TENTHS of a pixel are fitted with the bi-level
        /// rules instead of the ClearType ones.
        /// <para>58, i.e. 5.8 pixels. Measured band by band on the text specimen: the ClearType
        /// rounding is right nearly everywhere -- it reproduces Windows' digits almost pixel for
        /// pixel -- but at 11ppem the LOWERCASE is far better fitted bi-level, 117,862 -> 88,434 and
        /// 104,545 -> 69,553. What separates them is not the size of the text, because the same
        /// lowercase at 16ppem wants the ClearType fit like everything else: it is the height of the
        /// glyph. At 11ppem an x-height letter stands about 5.7 pixels tall while the digits and
        /// capitals beside it stand about 8.</para>
        /// <para>Sweeping it: 5.6 costs 854,135, 5.8 gives 787,977, then 795,253 / 806,228 / 821,476
        /// at 6.0 / 6.2 / 6.4 as taller glyphs start being caught by it. And the ten bands that are
        /// NOT lowercase-at-11ppem come out byte-identical, which is the whole argument for it --
        /// every global rounding rule tried instead traded one band against another.</para>
        /// <para>OFF, AND THE REASON IS A LESSON. Every number above was measured on a specimen of
        /// ONE FACE IN ONE STYLE -- Segoe UI regular. Put five more faces and bold and italic in
        /// front of it and the rule is net harmful: 10,362,413 with it against 10,094,299 without.
        /// It fires only where an x-height is under 5.8 pixels, so almost everything is untouched,
        /// and where it does fire it does this:</para>
        /// <para>Segoe UI REGULAR at 8.25pt   75,517 with, 99,012 without -- it helps, by 23,495.
        /// Segoe UI BOLD at the same size    306,424 with, 40,308 without -- it hurts, by 266,116.
        /// The same rule, the same face, one weight apart, and seven times worse on the bold.</para>
        /// <para>A rule that helps one face and style and destroys the next is not a rule, it is a
        /// fit to whatever was in front of it. The bold was never in front of it. Both this and the
        /// extender rule below it are off; WPF_CT_SMALLGLYPH=58 restores them together, since the
        /// extender test lives inside this one's guard.</para>
        /// <para>What DOES generalise, measured on the same six faces: compatible widths (worth
        /// 242,000), its 25% tolerance, and the gamma-space blend (worth 222,000). Those were
        /// derived from the same one-face specimen and survive the wider one, which is the
        /// difference between a mechanism and a fit.</para></summary>
        private static readonly int SmallGlyphPixels =
            int.TryParse(Environment.GetEnvironmentVariable("WPF_CT_SMALLGLYPH"), out int sg) ? sg : 0;

        /// <summary>WPF_CT_EXTENDER=0 turns off treating x-height letters with ascenders or
        /// descenders as small glyphs.</summary>
        private static readonly bool ExtenderRule =
            Environment.GetEnvironmentVariable("WPF_CT_EXTENDER") != "0";

        /// <summary>How far above the cap height a glyph must reach to count as having an ascender,
        /// in percent. WPF_CT_EXTMARGIN.</summary>
        private static readonly int ExtenderMargin =
            int.TryParse(Environment.GetEnvironmentVariable("WPF_CT_EXTMARGIN"), out int em) ? em : 3;

        private static readonly int CompatibleWidthTolerance =
            int.TryParse(Environment.GetEnvironmentVariable("WPF_CT_CWTOL"), out int ct2) ? ct2 : 25;

        internal static readonly int CompatibleWidthMode =
            int.TryParse(Environment.GetEnvironmentVariable("WPF_CT_COMPATWIDTH"), out int cw) ? cw : 1;

        /// <summary>Whether a fitted outline is still the glyph it started as.
        /// <para>GRID FITTING MOVES EDGES TO THE GRID -- by definition less than a pixel, plus a
        /// little for a stem that gets rounded outwards. It does not change a letter's size. So a
        /// fitted box that has drifted far from the scaled outline's box is not a fitting, it is a
        /// program this interpreter ran wrongly, and the unhinted outline is the better answer.</para>
        /// <para>This is a GUARD, not a fix, and it was earned: with it absent, text in most faces
        /// came out garbled while Segoe UI -- the only face the hinter was ever measured against --
        /// was perfect. At 16 pixels an em Arial fitted its 'I' and 'l' to a height of 2 where the
        /// outline asks for 11.5, its 'Y' to 4 and its 'B' to 8, and pushed 'b' nine pixels below
        /// the baseline; Segoe UI's worst glyph at the same size is within half a pixel. The real
        /// fix is to find the instruction being mis-run -- WPF_HINT_TRACE and the
        /// FaceOutlines_AreWholeGlyphs report are the way in -- and this guard should get quieter as
        /// that happens, never louder.</para></summary>
        private bool FitIsPlausible(int glyphId, float pixelsPerEm, List<PathFigure> fitted)
        {
            if (!TryGetGlyphOutline(glyphId, out List<PathFigure> raw) || raw.Count == 0)
                return true;                       // nothing to compare against

            Box(raw, out float rx0, out float ry0, out float rx1, out float ry1);
            Box(fitted, out float fx0, out float fy0, out float fx1, out float fy1);
            if (rx1 <= rx0 && ry1 <= ry0) return true;

            // The raw outline is in the face's base pixels; bring it to the size being fitted.
            float k = pixelsPerEm / PixelsPerEm;
            rx0 *= k; ry0 *= k; rx1 *= k; ry1 *= k;

            // Two pixels on any edge. One is what fitting is allowed to move; two leaves room for a
            // stem rounded outwards at both ends and for the flattening tolerance, and is still far
            // inside the collapses this exists to catch.
            const float Slack = 2f;
            return MathF.Abs(fx0 - rx0) <= Slack && MathF.Abs(fy0 - ry0) <= Slack
                && MathF.Abs(fx1 - rx1) <= Slack && MathF.Abs(fy1 - ry1) <= Slack;
        }

        private static void Box(List<PathFigure> figures,
                                out float x0, out float y0, out float x1, out float y1)
        {
            float ax0 = float.MaxValue, ay0 = float.MaxValue, ax1 = float.MinValue, ay1 = float.MinValue;

            void Take(Vector2 v)
            {
                if (v.X < ax0) ax0 = v.X;
                if (v.Y < ay0) ay0 = v.Y;
                if (v.X > ax1) ax1 = v.X;
                if (v.Y > ay1) ay1 = v.Y;
            }

            foreach (PathFigure f in figures)
            {
                Take(f.Start);
                foreach (PathSegment seg in f.Segments)
                    switch (seg)
                    {
                        case LineSegment l: Take(l.Point); break;
                        case QuadraticBezierSegment q: Take(q.Control); Take(q.Point); break;
                        case CubicBezierSegment c: Take(c.Control1); Take(c.Control2); Take(c.Point); break;
                    }
            }

            x0 = ax0; y0 = ay0; x1 = ax1; y1 = ay1;
        }

        /// <summary>The outline fitted by ANALYSIS -- GlyphHinter -- with the face's own hinting
        /// left out of it. What a face carrying no hints gets, and what the tests that cover the
        /// analysis have to call to be testing the analysis.</summary>
        internal bool TryGetFittedOutline(int glyphId, float pixelsPerEm, out List<PathFigure> figures)
        {
            figures = s_noFigures;
            if (pixelsPerEm <= 0f || glyphId < 0 || glyphId >= _numGlyphs) return false;

            HintMetrics? metrics = HintMetricsOfFace();
            if (metrics is null || !metrics.IsUsable)
                return false;

            List<Contour> contours = ReadGlyphContours(glyphId, 0);
            var working = new List<(Vector2[] Pts, bool[] On)>(contours.Count);
            foreach (Contour contour in contours)
            {
                if (contour.Points.Length < 2) continue;
                working.Add(((Vector2[])contour.Points.Clone(), contour.OnCurve));
            }

            if (working.Count > 0)
            {
                GlyphHinter.Fit(working, metrics, pixelsPerEm);

                // The simulated styles are applied AFTER fitting and in pixels: emboldening a fitted
                // outline keeps the stems on the grid they were just put on, where fitting a
                // thickened one would fit a shape the face does not contain.
                if (_emboldenStrength > 0f)
                    Embolden(working, _emboldenStrength * pixelsPerEm / BaseEmPixels);
                foreach ((Vector2[] pts, _) in working)
                    for (int i = 0; i < pts.Length; i++)
                        // PLUS, because y is still up here -- the flip to y-down is the second
                        // component of this very expression. Minus leans the glyph backwards.
                        pts[i] = new Vector2(pts[i].X + _shear * pts[i].Y, -pts[i].Y);   // y-down
            }

            var built = new List<PathFigure>(working.Count);
            foreach ((Vector2[] pts, bool[] on) in working)
                built.Add(BuildContourFigure(pts, on));

            figures = built;
            return built.Count > 0;
        }

        /// <summary>Whether the face's own hinting ran for this glyph, as opposed to the outline
        /// being fitted by analysis. Diagnostic: a glyph that quietly fell back looks like a hinting
        /// bug and is not one.</summary>
        internal bool FaceHintsGlyph(int glyphId, float pixelsPerEm)
            => RunFaceHints(glyphId, pixelsPerEm) is not null;

        /// <summary>Run the face's own hinting over one glyph, and turn what comes back into
        /// contours. Null when there is nothing to run or the program faulted.</summary>
        private List<PathFigure>? RunFaceHints(int glyphId, float pixelsPerEm)
        {
            TrueTypeInterpreter? interpreter = Interpreter();
            if (interpreter is null) return null;

            GlyphProgram? glyph = HintedProgram(interpreter, glyphId, pixelsPerEm, 0);
            if (glyph is null) return null;

            // Back out as contours, in pixels. The machine works in 26.6 fixed point with y up; the
            // rest of the stack wants floating point with y down.
            var working = new List<(Vector2[] Pts, bool[] On)>(glyph.EndPoints.Length);
            int first = 0;
            foreach (int last in glyph.EndPoints)
            {
                int n = last - first + 1;
                if (n >= 2)
                {
                    var pts = new Vector2[n];
                    var on = new bool[n];
                    for (int k = 0; k < n; k++)
                    {
                        pts[k] = new Vector2(glyph.X[first + k] / 64f, glyph.Y[first + k] / 64f);
                        on[k] = glyph.OnCurve[first + k];
                    }
                    working.Add((pts, on));
                }
                first = last + 1;
            }
            if (working.Count == 0) return null;

            // The simulated styles go on AFTER hinting and in pixels, for the same reason they do
            // on the fitted path: thickening a hinted outline keeps its stems on the grid the face
            // just put them on.
            if (_emboldenStrength > 0f)
                Embolden(working, _emboldenStrength * pixelsPerEm / BaseEmPixels);
            foreach ((Vector2[] pts, _) in working)
                for (int i = 0; i < pts.Length; i++)
                    // Plus, for the same reason: these are the HINTED points, still y-up.
                    pts[i] = new Vector2(pts[i].X + _shear * pts[i].Y, -pts[i].Y);

            var built = new List<PathFigure>(working.Count);
            foreach ((Vector2[] pts, bool[] on) in working)
                built.Add(BuildContourFigure(pts, on));
            return built;
        }

        private static readonly List<PathFigure> s_noFigures = new();

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

        /// <summary>A glyph as the hinting machine wants it: every point of every contour in ONE
        /// array with the contour ends beside it, the four phantom points after them, and the
        /// glyph's own instructions. Null for a glyph with no outline, or a composite -- a composite
        /// is assembled from components that were hinted in their own right, and re-running the
        /// container's program over the result needs the component machinery this does not have.
        /// </summary>
        /// <summary>One glyph, hinted by the face's own program, with its points left in 26.6 pixels.
        /// The recursive step of the whole business: an accented letter is assembled out of glyphs
        /// that come back through here, each already fitted.</summary>
        /// <summary>Fit the glyph the way a rasterizer with three lamps to a pixel fits it: the
        /// vertical hinting runs in full, and x keeps the PLAIN SCALED value the outline was drawn
        /// with -- no grid, no thirds, no rounding at all.
        /// <para>The comment here used to say "x on a grid three times finer" and point at
        /// TrueTypeInterpreter.RoundToThirds. That method no longer exists and the code below keeps
        /// the scaled x, so the claim it carried -- that stem POSITION reproduces GDI exactly -- was
        /// unsupported, and it had been steering the text investigation. Measured 2026-08-28: our
        /// kept x sits on average 0.286 px from a pixel boundary, which is what arbitrary fractions
        /// look like.</para>
        /// <para>Rounding it was then swept against all three instruments and NONE is right:</para>
        /// <code>
        ///   x round   outline-vs-GDI-box   rendered coverage   mean ink
        ///   none                    1034                 574      1.12%   &lt;- here
        ///   third                    842                 678      1.33%
        ///   whole                     265                2684      1.73%
        /// </code>
        /// <para>Whole-pixel rounding makes our OUTLINES agree with GDI's hinted metrics four times
        /// better and the RENDERED pixels nearly five times worse, and that contradiction is the
        /// answer: GetGlyphOutline(GGO_METRICS) reports GDI's default x+y hinting, while ClearType
        /// renders from a y-only fitting that keeps natural x. The metrics are a reference for the
        /// wrong mode. Rendered coverage is the one to trust, and it says leave x alone -- which is
        /// also what "GDI keeps natural widths" has said all along.</para></summary>
        internal static bool SubpixelFitting { get; set; }

        /// <summary>Whether ClearType is the mode being DRAWN, as opposed to the fitting in use.
        /// <para>The interpreter tells a face what GETINFO says and rounds on the ClearType grid
        /// when it is on. Off it, GDI answers no and rounds on whole pixels, so drawing grey text
        /// while still saying yes fits the glyph for a rasterizer that is not running.</para>
        /// <para>Separate from <see cref="SubpixelFitting"/> on purpose: the stage tests toggle that
        /// to fit a glyph both ways, and doing so must not change what the face is told.</para>
        /// </summary>
        internal static bool ClearTypeRendering { get; set; } = true;

        /// <summary>Snap the fitted glyph's left edge onto a whole pixel: 1 nearest, 2 ceil, 3 floor.
        /// <para>WPF_X_LSBSNAP.</para></summary>
        /// <summary>WPF_X_SHIFTS: a file of "SHIFT ppem glyphId sixteenths" lines. Diagnostic.</summary>
        private static readonly Dictionary<(int Ppem, int Gid), int>? ShiftTable = LoadShiftTable();

        private static Dictionary<(int, int), int>? LoadShiftTable()
        {
            string? path = Environment.GetEnvironmentVariable("WPF_X_SHIFTS");
            if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)) return null;
            var table = new Dictionary<(int, int), int>();
            foreach (string line in System.IO.File.ReadAllLines(path))
            {
                string[] parts = line.Split(' ');
                if (parts.Length == 4 && parts[0] == "SHIFT"
                    && int.TryParse(parts[1], out int pp) && int.TryParse(parts[2], out int g)
                    && int.TryParse(parts[3], out int k))
                    table[(pp, g)] = k;
            }
            return table.Count > 0 ? table : null;
        }

        /// <summary>How far the bi-level width may differ from the fitted one, in percent, and the
        /// two still count as the same shape. WPF_X_SPANTOL.</summary>
        private static readonly string? SpanDump = Environment.GetEnvironmentVariable("WPF_SPAN_DUMP");
        private static readonly object SpanLock = new object();

        private static readonly int SpanTolerance =
            int.TryParse(Environment.GetEnvironmentVariable("WPF_X_SPANTOL"), out int st) ? st : 5;

        private static readonly int LsbSnapMode =
            int.TryParse(Environment.GetEnvironmentVariable("WPF_X_LSBSNAP"), out int ls) ? ls : 0;

        /// <summary>What to do with the x the face's own program produces, when subpixel fitting is on.
        /// <para>0 -- DISCARD it and keep the scaled outline's x (the default, and what GDI's rendered
        /// output has always looked like). 1 -- KEEP it, with x distances rounded on a THIRD-pixel
        /// grid. 2 -- keep it with the program's own rounding, which is whole pixels.</para>
        /// <para>Mode 1 exists because of a measurement, not a theory. At 11 pixels an em our stems
        /// carry 0.905 of GDI's ink and at 14 they carry 1.02, uniformly across every glyph and every
        /// ROW of every glyph -- so the outlines are the same height and our stems are simply
        /// narrower at small sizes and wider at large. GDI's own stem widths, measured, are multiples
        /// of a third of a pixel that HOLD across several sizes before stepping (1.67, 2.00, 2.00,
        /// 2.00, 1.67 ...), which is what a control value rounded on a third-pixel grid does and what
        /// a smoothly scaled outline never does. Modes rounding to a WHOLE pixel and to nothing at all
        /// have both been measured before and were far too heavy (ink 1.247 and 1.244); a third of a
        /// pixel is the granularity those measured GDI widths actually sit on and had never been
        /// tried. WPF_X_HINT selects it.</para>
        /// <para>MEASURED, AND IT FAILS -- like both attempts before it, and for the same reason.
        /// Mode 1 gives ink 1.10/1.20/1.25/1.19 at 11/12/14/19 and structural 2598 against 274; mode 2
        /// gives 1.09/1.18/1.26/1.20 and 5470. MIRP applies the control value's distance and makes a
        /// stem far wider than GDI's however finely the result is rounded, so the granularity was
        /// never the problem.</para>
        /// <para>AND THE OBSERVATION THAT MOTIVATED ALL THREE ATTEMPTS WAS AN ARTEFACT. "GDI's stem
        /// widths are multiples of a third of a pixel" was measured off a three-lamp rendering, where
        /// any support measured from lamp coverage is quantized to thirds BY CONSTRUCTION. It is not
        /// evidence of third-pixel fitting and never was. GDI leaves x alone; do not spend a fourth
        /// attempt on this.</para></summary>
        /// <summary>What to do with the x fitting. FIVE: keep what the program produced.
        /// <para>It was 0 -- throw the x movement away -- for a long time, and every attempt to keep
        /// it measured far worse. That was real, and the reason was that our x fitting was WRONG: we
        /// ran the face's instructions as a bi-level rasterizer would. Microsoft's "TrueType and
        /// ClearType" paper spells out what changes in the ClearType direction (a virtual grid of
        /// sixteen lines per pixel for the rounding instructions, a cut-in reduced to a sixteenth, a
        /// minimum distance halved, the cut-in honoured even by an un-rounded MIRP, and physical-grid
        /// rounding inside the pre-program). With those in, keeping the fitting stops being a loss
        /// and becomes the best thing available:</para>
        /// <para>parity SUM|d|, x discarded 4,071,456 -> 3,894,842; x kept 7,404,226 -> 3,629,242.
        /// Keeping it now BEATS discarding it, which had never happened before.</para>
        /// <para>SHIPPED, on the evidence of the text specimen -- rows of plain Labels drawn by both
        /// stacks, which is nothing but text and so cannot be confused by control chrome: 1,536,150
        /// with the fitting discarded against 1,410,303 with it kept. The live CONTROL window still
        /// prefers discarding it, and that disagreement is now known to be about the CONTROLS rather
        /// than about text.</para></summary>
        internal static readonly int XHintMode =
            int.TryParse(Environment.GetEnvironmentVariable("WPF_X_HINT"), out int xh) ? xh : 5;


        /// <summary>How many parts of a pixel the natural x may land on, or 0 to leave it alone.
        /// <para>Measured against GDI, a stem lands in ONE SATURATED COLUMN plus a fringe where ours
        /// straddles two: 'H' at 12ppem comes back 2295/543/255/255/255/255/2295/324 from GDI and
        /// 1908/591/255/255/255/1271/1143 from us. Splitting a stem across two columns costs ink,
        /// because the contrast curve is convex and pulls both halves down further than it pulls one
        /// whole -- which is the regular face's size tilt, seen directly.</para>
        /// <para>WPF_X_GRID sets the divisor: 3 puts every stem edge on a LAMP boundary, which is
        /// where GDI's measured third-pixel positions sit and what makes a lamp saturate.</para>
        /// <para>IT IS OFF, because it was measured and it does not work -- structural disagreement
        /// 274 natural, 412 on thirds, 447 on halves, 294 on sixths, 2240 on whole pixels, and none
        /// of them lifts the regular face's tilt (regular@11 goes 0.920 -> 0.908 on thirds, the wrong
        /// way). Snapping every point moves a stem's two edges INDEPENDENTLY, so it fixes the
        /// positions the earlier investigation measured and mangles the widths at the same time.
        /// This is the second time the third-pixel grid has been tried and rejected -- the first was
        /// under the old coverage model, so it was worth re-testing, and now it is not.</para>
        /// <para>The observation that prompted it is still true and still unexplained: GDI's 'l' at
        /// 12ppem carries 2619 of ink against our 2295 while its 'I' carries 2295 against our 2286.
        /// GDI keeps a width difference between those two stems that we do not -- so whatever it
        /// does to x, it is not a grid, and it is not nothing.</para>
        /// </summary>
        private static readonly int XGrid =
            int.TryParse(Environment.GetEnvironmentVariable("WPF_X_GRID"), out int xg) && xg > 0 ? xg : 0;

        /// <summary>Round a 26.6 x onto that grid. Symmetric about zero: rounding toward negative
        /// infinity biases every left side bearing one way and is how a run drifts.</summary>
        private static int SnapX(int f26d6)
        {
            if (XGrid <= 0) return f26d6;
            float step = 64f / XGrid;
            return (int)MathF.Round(MathF.Round(f26d6 / step) * step);
        }

        private GlyphProgram? HintedProgram(TrueTypeInterpreter interpreter, int gid, float pixelsPerEm,
                                            int depth)
        {
            // A component that references its own composite is a font that would hang us. Five deep
            // is more than any real face needs -- a letter, its accent, and the accent's own parts.
            if (depth > 5 || _glyfOffset < 0 || gid < 0 || gid >= _numGlyphs || _loca.Length == 0)
                return null;
            uint start = _loca[gid], end = _loca[gid + 1];
            if (end <= start) return null;                       // a blank: nothing to hint

            GlyphProgram? glyph = (short)U16(_glyfOffset + (int)start) >= 0
                ? ReadGlyphProgram(gid)
                : ReadCompositeProgram(interpreter, gid, pixelsPerEm, depth);
            if (glyph is null) return null;

            // Where x is not being hinted, keep the scaled outline's own x and let the program have
            // the y. Taken BEFORE the program runs, because after it they are the hinted ones, and
            // scaled here because a simple glyph arrives in font units (a composite is already in
            // pixels and its components were dealt with one level down).
            // Subpixel text keeps the x the scaled outline gives it. Taken BEFORE the program runs,
            // because after it they are the hinted ones, and scaled here because a simple glyph
            // arrives in font units (a composite is already in pixels, one level down).
            int[]? plainX = null;
            if (SubpixelFitting && XHintMode != 1 && XHintMode != 2 && XHintMode != 5 && XHintMode != 6 && XHintMode != 7 && XHintMode != 8 && XHintMode != 11 && XHintMode != 12 && XHintMode != 13 && XHintMode != 14 && XHintMode != 16 && !glyph.Composite
                && interpreter.PrepareForSize(pixelsPerEm))
            {
                plainX = new int[glyph.X.Length];
                for (int i = 0; i < plainX.Length; i++)
                    plainX[i] = SnapX(interpreter.ScaleToPixels(glyph.X[i]));
            }

            // FIT IN GDI'S CLEARTYPE SPACE: triple x, run the program, divide back. Stage D reads
            // that space out of GDI through a stretched MAT2, and what it shows is that GDI puts
            // each STEM on a lamp -- our 'm' at 12ppem carries its second and third stems a third of
            // a pixel right of GDI's -- which no whole-glyph offset can repair (sliding ours along
            // the lamp grid doubles the disagreement either way).
            bool space3x = SubpixelFitting && XHintMode == 11 && !glyph.Composite;
            if (space3x)
                for (int i = 0; i < glyph.X.Length; i++) glyph.X[i] *= 3;

            // The LEFT EDGE the glyph had before any fitting, in 64ths. Mode 3 puts it back there:
            // measured at 11ppem, 'm' and 'f' -- between them a fifth of all the per-glyph error
            // left after compatible widths -- come out a whole pixel wider than GDI's on the LEFT,
            // their first stem a column early, while their right edge lands correctly. Scaling about
            // the origin cannot see that, because the origin is not where the ink starts.
            int plainLeft = int.MaxValue;
            if (!glyph.Composite && interpreter.PrepareForSize(pixelsPerEm))
                for (int i = 0; i < glyph.PointCount && i < glyph.X.Length; i++)
                {
                    int sx = interpreter.ScaleToPixels(glyph.X[i]);
                    if (sx < plainLeft) plainLeft = sx;
                }

            // Where the glyph's origin sat BEFORE the program ran, so a run can be given back the
            // ink-to-origin offset the fitting moved. Taken here because after Hint it is gone.
            int plainPhantom = glyph.X.Length > glyph.PointCount
                ? glyph.X[glyph.PointCount] : int.MinValue;

            // SMALL GLYPHS ARE FITTED HARD. Measured band by band against Windows: the ClearType
            // rounding wins everywhere except lowercase at 11ppem, where the bi-level fit is far
            // better (117,862 -> 71,276 and 104,545 -> 57,945) -- and at that size lowercase stands
            // about five pixels tall while the digits and capitals beside it, which the ClearType
            // fit gets very nearly exactly right, stand about eight. It is the height of the GLYPH
            // that separates them, not the size of the text: the same lowercase at 16ppem is eight
            // pixels tall and wants the ClearType fit like everything else.
            bool small = false;
            if (SmallGlyphPixels > 0 && !glyph.Composite && gid >= 0 && gid < _numGlyphs
                && _loca.Length > gid + 1 && _loca[gid + 1] > _loca[gid])
            {
                int hp = _glyfOffset + (int) _loca[gid];
                int yMin = (short) U16(hp + 4), yMax = (short) U16(hp + 8);
                // Through the interpreter's own scale. PixelsPerEm here is a BASE em size, not the
                // units per em, so scaling font units by pixelsPerEm/PixelsPerEm gives hundreds of
                // pixels and silently never fires -- which is exactly what the first version did.
                if (interpreter.PrepareForSize(pixelsPerEm))
                {
                    float tall = interpreter.ScaleToPixels(yMax - yMin) / 64f;
                    small = tall > 0f && tall * 10f <= SmallGlyphPixels;

                    // AND A LETTER WHOSE BODY SITS AT X-HEIGHT counts as small even though its box
                    // is tall: 'b' 'd' 'k' 'p' 'q' 'g' are drawn around the same little bowl as 'o',
                    // with an ascender or a descender hung off it, and after the height rule went in
                    // they are exactly what is left dear at 11ppem. A bounding box cannot tell them
                    // from a capital -- 'b' is 8.1px tall and 'H' is 8.0 -- but the font can: an
                    // ascender reaches ABOVE the cap height, and a descender below the baseline,
                    // and a capital does neither.
                    // Fitting every CURVE-FREE glyph bi-level was tried here, on the reasoning that
                    // at 12ppem 'H' wants its stems on whole pixels (GDI puts them at 1 and 7 where
                    // our sixteenth grid leaves 1.125 and 6.812) while the digits beside it, exactly
                    // as tall, are reproduced almost pixel for pixel by the ClearType fit. It does
                    // help the capitals, 94,146 -> 87,305, and it costs far more everywhere else:
                    // 759,520 -> 944,858. Curvature is not the discriminator either.

                    if (!small && ExtenderRule && _sxHeight > 0 && _sCapHeight > 0)
                    {
                        float xh = interpreter.ScaleToPixels(_sxHeight) / 64f;
                        // With a MARGIN over the cap height, because lining figures are drawn a
                        // shade taller than the capitals in most faces -- Segoe UI's are -- and a
                        // bare "taller than a capital" catches every digit. Caught, they are fitted
                        // small and the digit band goes 20,168 -> 156,808, which is how this was
                        // found.
                        // And the descender test needs its own margin, for the same reason in the
                        // other direction: a ROUND glyph overshoots the baseline by a hair, so a
                        // bare "yMin < 0" calls '0' '3' '6' '8' '9' -- and 'O' 'C' 'G' 'S' --
                        // descenders. A real descender drops about an x-height below the line; an
                        // overshoot is a percent of an em.
                        bool extender = yMax * 100 > _sCapHeight * (100 + ExtenderMargin)
                                        || yMin < -(_sxHeight / 4);
                        small = extender && xh > 0f && xh * 10f <= SmallGlyphPixels;
                    }
                }
            }
            bool savedBi = TrueTypeInterpreter.BiLevelPass;
            if (small) TrueTypeInterpreter.BiLevelPass = true;
            bool hinted;
            try { hinted = interpreter.Hint(glyph, pixelsPerEm); }
            finally { TrueTypeInterpreter.BiLevelPass = savedBi; }
            if (!hinted) return null;

            // SNAP THE GLYPH ONTO THE PIXEL GRID, KEEPING THE SHAPE THE FITTING GAVE IT.
            // The physical grid reproduces GDI's bi-level outline and renders twice as badly, but it
            // does not fail everywhere: it fixes lowercase at 11ppem and breaks every other band. Per
            // glyph, 65% of the 11ppem error is recoverable by SLIDING the glyph -- and the glyphs
            // asking to move are the round ones ('d' 'g' 'o' 'q' 'u' 'O' +0.50). So the position wants
            // the pixel grid while the stems want the fine one: take the translation from the coarse
            // rule and the shape from the fine one, instead of choosing between them.
            // MODE 4: fit the glyph a SECOND time under the bi-level rules and move the ClearType
            // fitting sideways onto the bi-level left edge. Snapping to the nearest whole pixel was
            // the first attempt and it is wrong: it moves glyphs whose edge was ALREADY right (the
            // digits, which want no offset at all) and by its own probe it made 11ppem worse, 19
            // glyphs wanting zero falling to 6. The bi-level edge is not "a whole pixel", it is
            // where GDI puts that particular glyph, and for the digits it is where we already were.
            if (LsbSnapMode == 4 && !glyph.Composite)
            {
                GlyphProgram? plain = ReadGlyphProgram(gid);
                if (plain is not null)
                {
                    bool fitted;
                    TrueTypeInterpreter.BiLevelPass = true;
                    try { fitted = interpreter.Hint(plain, pixelsPerEm); }
                    finally { TrueTypeInterpreter.BiLevelPass = false; }
                    if (fitted)
                    {
                        int biL = int.MaxValue, biR = int.MinValue, ctL = int.MaxValue, ctR = int.MinValue;
                        for (int i = 0; i < plain.PointCount && i < plain.X.Length; i++)
                        { if (plain.X[i] < biL) biL = plain.X[i]; if (plain.X[i] > biR) biR = plain.X[i]; }
                        for (int i = 0; i < glyph.PointCount && i < glyph.X.Length; i++)
                        { if (glyph.X[i] < ctL) ctL = glyph.X[i]; if (glyph.X[i] > ctR) ctR = glyph.X[i]; }
                        // ONLY WHEN THE TWO FITS AGREE ON THE SHAPE. Where bi-level merely puts our
                        // glyph somewhere else, its position is the one GDI draws and we should
                        // follow it: 'o' at 11ppem comes out 5.19px wide against bi-level's 5.00 and
                        // sits exactly half a pixel left of it. Where bi-level RESHAPES the glyph it
                        // has gone its own way and ours should stand: '0' at the same size is 5.13px
                        // wide against bi-level's 6.00, a whole pixel of widening that ClearType does
                        // not do -- and forcing our digits onto it costs band 0 eight times its error.
                        if (SpanDump is string dump)
                            lock (SpanLock)
                                System.IO.File.AppendAllText(dump,
                                    $"SPAN {(int) MathF.Round(pixelsPerEm)} {gid} {ctL} {ctR} {biL} {biR}" + System.Environment.NewLine);
                        long ctW = ctR - ctL, biW = biR - biL;
                        bool sameShape = ctW > 0 && biW > 0
                            && Math.Abs(biW - ctW) * 100 <= ctW * SpanTolerance;
                        if (biL != int.MaxValue && ctL != int.MaxValue && sameShape)
                        {
                            // Moving the glyph onto the bi-level LEFT edge fixes every left edge --
                            // measured, mean error -0.220px -> -0.054px, the round glyphs exactly on
                            // it -- and then overshoots on the right, because our fitting comes out
                            // about a quarter pixel WIDER than GDI's at every size. So match the
                            // span, not just its start: "the glyphs for this font size will be
                            // adjusted POST HINTING in order to return advance widths that are
                            // exactly the same as bi-level rendering" (Microsoft, TrueType and
                            // ClearType). Interior points keep their fine fitting, proportionally.
                            for (int i = 0; i < glyph.X.Length; i++)
                                glyph.X[i] = ctR > ctL && biR > biL
                                    ? biL + (int) ((long) (glyph.X[i] - ctL) * (biR - biL) / (ctR - ctL))
                                    : glyph.X[i] + (biL - ctL);
                        }
                    }
                }
            }
            else if (LsbSnapMode != 0 && !glyph.Composite)
            {
                int left = int.MaxValue;
                for (int i = 0; i < glyph.PointCount && i < glyph.X.Length; i++)
                    if (glyph.X[i] < left) left = glyph.X[i];
                if (left != int.MaxValue)
                {
                    int target = LsbSnapMode switch
                    {
                        2 => (left + 63) & ~63,                  // ceil
                        3 => left & ~63,                         // floor
                        _ => (left + 32) & ~63,                  // nearest
                    };
                    int shift = target - left;
                    for (int i = 0; i < glyph.X.Length; i++) glyph.X[i] += shift;
                }
            }

            // THE CEILING. A table of the best possible x shift for every (size, glyph), measured
            // against GDI's own lamps one glyph at a time and fed back in here. It is not a rule and
            // could never ship -- it is how much a perfect placement rule would be WORTH, so that
            // the search for one can be called off if the answer is "not much".
            if (ShiftTable is not null)
            {
                int px = (int) MathF.Round(pixelsPerEm);
                if (ShiftTable.TryGetValue((px, gid), out int sixteenths) && sixteenths != 0)
                    for (int i = 0; i < glyph.X.Length; i++) glyph.X[i] += sixteenths * 4;
            }

            // A SUB-LAMP x offset, for measuring only. At 11ppem our stems sit a fraction of a pixel
            // left of Windows' -- ours spill into the column before and never saturate, where GDI's
            // fill one column outright -- and a whole-lamp slide cannot express that: the lamp test
            // still puts its minimum at zero. In 64ths of a pixel.
            if (XOffset64 != 0)
                for (int i = 0; i < glyph.X.Length; i++) glyph.X[i] += XOffset64;

            // COMPATIBLE WIDTHS. "Compatible Width ClearType ... the glyphs for this font size will
            // be adjusted POST HINTING in order to return advance widths that are exactly the same as
            // bi-level rendering" -- Microsoft, TrueType and ClearType. We answer that GETINFO
            // selector and never did the adjustment, and the cost is visible the moment the x fitting
            // is kept: our advances already equal GDI's exactly, but the fitted INK moves inside the
            // advance box and nothing pulls it back, so the error accumulates along a run. The live
            // window's POSITION half doubles, 280,803 -> 479,090.
            if (CompatibleWidthMode != 0 && plainX is null && !glyph.Composite
                && glyph.X.Length > glyph.PointCount + 1)
            {
                int p0 = glyph.X[glyph.PointCount], p1 = glyph.X[glyph.PointCount + 1];
                int fitted = p1 - p0;
                // MODE 2: put the glyph back on its ORIGINAL left side bearing instead of scaling it
                // onto the advance. A run places each glyph at pen + advance and takes the ink from
                // the outline, so what a run needs is the ink's offset from the origin preserved --
                // a translation. Scaling (mode 1) changes the shape the fitting just produced and
                // measured worse; this keeps it and moves it.
                if (CompatibleWidthMode == 2)
                {
                    int before = plainPhantom;
                    if (before != int.MinValue && before != p0)
                    {
                        int shift = before - p0;
                        for (int i = 0; i < glyph.X.Length; i++) glyph.X[i] += shift;
                    }
                }
                int ppemI = (int) MathF.Round(pixelsPerEm);
                // MODE 4, measured and NOT the default: 815,655 against mode 1's 759,520, with
                // 829,983 for no correction at all. So it is a real correction and the smaller half
                // of the right one -- the advance genuinely does need fixing, and stretching the
                // glyph onto it beats sliding the glyph inside it. Move the ink instead of
                // stretching it. "Adjusted post hinting to return
                // advance widths the same as bi-level" says the ADVANCE must come out right; mode 1
                // reads that as scaling the glyph, which also changes every stem width the fitting
                // just chose. This reads it as changing only the SPACE around the glyph: the left
                // side bearing scales with the advance and the ink rides along unchanged.
                if (CompatibleWidthMode == 4 && fitted > 0 && gid >= 0 && gid < _numGlyphs)
                {
                    float wanted4 = TryGetHdmxAdvance(gid, ppemI, out float hd4)
                        ? hd4
                        : MathF.Round(Advance(gid) * pixelsPerEm / PixelsPerEm);
                    int target4 = (int) MathF.Round(wanted4 * 64f);
                    int off4 = Math.Abs(target4 - fitted) * 100;
                    if (target4 > 0 && target4 != fitted && off4 <= fitted * CompatibleWidthTolerance)
                    {
                        int inkLeft = int.MaxValue;
                        for (int i = 0; i < glyph.PointCount && i < glyph.X.Length; i++)
                            if (glyph.X[i] < inkLeft) inkLeft = glyph.X[i];
                        if (inkLeft != int.MaxValue)
                        {
                            int lsb = inkLeft - p0;
                            int shift = (int) ((long) lsb * target4 / fitted) - lsb;
                            for (int i = 0; i < glyph.X.Length; i++) glyph.X[i] += shift;
                            if (glyph.X.Length > glyph.PointCount + 1)
                                glyph.X[glyph.PointCount + 1] = glyph.X[glyph.PointCount] + target4;
                        }
                    }
                }
                if ((CompatibleWidthMode == 1 || CompatibleWidthMode == 3) && fitted > 0 && gid >= 0 && gid < _numGlyphs)
                {
                    // hdmx if the face ships it, else the scaled advance rounded to a pixel, which is
                    // what a bi-level rasterizer would have produced. NEVER TryGetDeviceAdvance --
                    // it hints the glyph to answer, and we are inside the hinter.
                    float wanted = TryGetHdmxAdvance(gid, ppemI, out float hd)
                        ? hd
                        : MathF.Round(Advance(gid) * pixelsPerEm / PixelsPerEm);
                    int target = (int) MathF.Round(wanted * 64f);
                    // Only a CORRECTION, never a rebuild. The scale is the ratio of two advances and
                    // it is applied to the INK, so where the fitted phantom points have gone astray
                    // it stretches the glyph instead of nudging it: 'f' at 11ppem comes out 4.41px
                    // wide against 3.33 unhinted, a third wider than the letter, and it is the second
                    // most expensive glyph in the whole repertoire. A glyph asking for a large
                    // correction is one whose fitted advance cannot be trusted -- leave it alone.
                    int off = Math.Abs(target - fitted) * 100;
                    if (target > 0 && target != fitted && off <= fitted * CompatibleWidthTolerance)
                    {
                        for (int i = 0; i < glyph.X.Length; i++)
                            glyph.X[i] = p0 + (int) MathF.Round((glyph.X[i] - p0) * (target / (float) fitted));
                    }
                }
            }

            // MODE 3: compatible widths, and then put the glyph back on the left edge it had before
            // the fitting touched it. The advance still comes out at the bi-level width; only where
            // the ink sits inside that advance changes.
            if (CompatibleWidthMode == 3 && plainLeft != int.MaxValue && !glyph.Composite)
            {
                int left = int.MaxValue;
                for (int i = 0; i < glyph.PointCount && i < glyph.X.Length; i++)
                    if (glyph.X[i] < left) left = glyph.X[i];
                if (left != int.MaxValue && left != plainLeft)
                    for (int i = 0; i < glyph.X.Length; i++) glyph.X[i] += plainLeft - left;
            }

            if (space3x)
                for (int i = 0; i < glyph.X.Length; i++)
                    glyph.X[i] = (int) MathF.Round(glyph.X[i] / 3f);

            if (plainX is not null)
            {
                // The two phantom points keep what the program made of them: they are the side
                // bearings, which is spacing rather than shape, and a run has to keep landing where
                // the advances say it does.
                //
                // But the program moves the LEFT phantom point as well, and that is not spacing -- it
                // is where the glyph's origin ended up, so the outline belongs at the same offset. We
                // restored the outline to its unhinted absolute x and kept a hinted origin, which
                // leaves the ink a fraction of a pixel from where the side bearing says it is.
                // Measured at 11 pixels an em, the lamps under an 'l' are ours 0/32/114/206/206/114/
                // 32 against GDI's 0/0/73/153/255/197/111/36 -- a quarter of a pixel apart, with GDI
                // saturating a lamp where we straddle two. Carrying the origin's movement across was
                // MEASURED and changes nothing: the program does not move that phantom point in x, so
                // the shift is zero for every glyph. A pure x offset was swept too, over a third of a
                // pixel either way, and moves the regular face at 11ppem by at most 1.2% -- ink is
                // conserved by every linear step, so position reaches the total only through the
                // contrast curve, and barely. Neither earns a knob.
                if (XHintMode == 4)
                {
                    // KEEP THE FINE ADJUSTMENT, DROP THE GRID SNAP.
                    //
                    // Fitting x does two things at once: it adjusts a stem's WIDTH by a fraction
                    // of a pixel, and it SLIDES the stem onto a whole pixel. The evidence says to
                    // keep the first and refuse the second. With the curve calibrated where no
                    // fitting happens (gamma 1.35 at 7-8ppem, exact on both), GDI's finished ink
                    // at 11ppem implies a fitted geometry of 283,730 against our x+y fitting's
                    // 283,952 -- the same ink to within a tenth of a percent. So GDI is not
                    // keeping less ink than a full fitting, it is putting the same ink in a
                    // different PLACE: keeping every whole-pixel move (WPF_X_HINT=3) leaves the
                    // total right and the finished pixels 6% heavy, because ink snapped onto whole
                    // columns saturates lamps and a convex curve pays more for that.
                    //
                    // So each point keeps only the sub-pixel part of what the program moved it.
                    const int Pixel = 64;                    // 26.6
                    for (int i = 0; i < glyph.PointCount; i++)
                    {
                        int moved = glyph.X[i] - plainX[i];
                        int snap = (int) MathF.Round(moved / (float) Pixel) * Pixel;
                        glyph.X[i] = plainX[i] + (moved - snap);
                    }
                }
                else if (XHintMode == 3)
                {
                    // WIDTHS from the fitting, POSITION from the outline. Discarding x movement
                    // altogether is what costs the ink: our y-only fitted glyphs carry about 0.85
                    // of what our x+y fitted ones do, and the finished ClearType pixels need that
                    // 15% back (with the curve at the value the no-gridfit sizes measure, 1.35,
                    // the fitted sizes come out at 0.86-0.91). Fitting x snaps a stem to whole
                    // pixels, which mostly WIDENS it; that width is what GDI keeps. What it does
                    // not keep is the sideways shift, so the glyph is slid back to where the
                    // unhinted outline put it and only the shape it gained is retained.
                    int hintedMin = int.MaxValue, plainMin = int.MaxValue;
                    for (int i = 0; i < glyph.PointCount; i++)
                    {
                        if (glyph.X[i] < hintedMin) hintedMin = glyph.X[i];
                        if (plainX[i] < plainMin) plainMin = plainX[i];
                    }
                    if (hintedMin != int.MaxValue)
                    {
                        int shift = plainMin - hintedMin;
                        for (int i = 0; i < glyph.PointCount; i++) glyph.X[i] += shift;
                    }
                }
                else
                {
                    for (int i = 0; i < glyph.PointCount; i++)
                        glyph.X[i] = plainX[i];

                    if (XHintMode == 9)
                    {
                        // SNAP THE GLYPH TO THE LAMP GRID, without touching its shape.
                        //
                        // Sliding our whole page along the lamp grid and scoring it against GDI's
                        // says the two grids ARE aligned -- offset 0 wins at every size -- but the
                        // minimum is deep at 16ppem (433,676 against 1,097,818 a lamp over) and
                        // nearly flat at 11 (511,039 against 531,403, four percent). So at small
                        // sizes our glyphs sit about half a lamp left of where GDI puts them, which
                        // is a PLACEMENT error, and the evidence has been saying placement for a
                        // while: the signed error is +0.60 against an absolute 25.41, so the ink is
                        // the right amount in the wrong place.
                        //
                        // A translation is the one correction that cannot distort the glyph, which
                        // separates it from every x-fitting attempt: those all moved points relative
                        // to each other and all made the solid band worse.
                        const int Lamp = 64;                    // thirds of a 26.6 pixel, below
                        int minX = int.MaxValue;
                        for (int i = 0; i < glyph.PointCount; i++)
                            if (glyph.X[i] < minX) minX = glyph.X[i];
                        if (minX != int.MaxValue)
                        {
                            int thirds = (int) MathF.Round(minX * 3f / Lamp);
                            int shift = (int) MathF.Round(thirds * Lamp / 3f) - minX;
                            for (int i = 0; i < glyph.PointCount; i++) glyph.X[i] += shift;
                        }
                    }
                }
            }
            return glyph;
        }

        /// <summary>An accented letter, put together out of its parts and ready for its own program.
        /// </summary>
        /// <remarks>
        ///  Each component is hinted FIRST, on its own, and arrives here already standing on the
        ///  pixel grid; this places it and hands the assembly back for the composite's own program to
        ///  nudge. That order is what the face's designers wrote against -- an 'e' is fitted as an
        ///  'e' whether it is alone or under an acute, and the composite's program exists to move the
        ///  accent clear of it, not to fit either one from scratch.
        ///  <para>Doing it the other way round -- assembling in font units and fitting the result --
        ///  would fit a shape the face never measured, and the letter under the accent would come out
        ///  a different weight from the same letter beside it in the same word.</para>
        /// </remarks>
        private GlyphProgram? ReadCompositeProgram(TrueTypeInterpreter interpreter, int gid,
                                                   float pixelsPerEm, int depth)
        {
            const int ARG_1_AND_2_ARE_WORDS = 0x0001;
            const int ARGS_ARE_XY_VALUES = 0x0002;
            const int ROUND_XY_TO_GRID = 0x0004;
            const int WE_HAVE_A_SCALE = 0x0008;
            const int MORE_COMPONENTS = 0x0020;
            const int WE_HAVE_AN_X_AND_Y_SCALE = 0x0040;
            const int WE_HAVE_A_TWO_BY_TWO = 0x0080;
            const int WE_HAVE_INSTRUCTIONS = 0x0100;
            const int USE_MY_METRICS = 0x0200;
            const int SCALED_COMPONENT_OFFSET = 0x0800;

            // The size has to be prepared before a component offset can be scaled, and the first
            // thing that would have done it is hinting a component -- which happens after.
            if (!interpreter.PrepareForSize(pixelsPerEm)) return null;

            int header = _glyfOffset + (int)_loca[gid];
            int xMin = (short)U16(header + 2);
            int p = header + 10;

            var xs = new List<int>();
            var ys = new List<int>();
            var onCurve = new List<bool>();
            var ends = new List<int>();

            // USE_MY_METRICS: the composite is spaced as one of its components is, so that a letter
            // and its accented form keep the same sidebearings.
            bool borrowedMetrics = false;
            int borrowedOrigin = 0, borrowedAdvance = 0;

            int flags = 0;
            bool more = true;
            for (int component = 0; more; component++)
            {
                if (component > 64) return null;            // a component list this long is damage
                flags = U16(p); p += 2;
                int componentGid = U16(p); p += 2;

                int arg1, arg2;
                if ((flags & ARG_1_AND_2_ARE_WORDS) != 0)
                {
                    arg1 = (short)U16(p); p += 2;
                    arg2 = (short)U16(p); p += 2;
                }
                else { arg1 = (sbyte)_data[p++]; arg2 = (sbyte)_data[p++]; }

                float a = 1f, b = 0f, c = 0f, d = 1f;
                if ((flags & WE_HAVE_A_SCALE) != 0) { a = d = F2Dot14(p); p += 2; }
                else if ((flags & WE_HAVE_AN_X_AND_Y_SCALE) != 0) { a = F2Dot14(p); p += 2; d = F2Dot14(p); p += 2; }
                else if ((flags & WE_HAVE_A_TWO_BY_TWO) != 0)
                {
                    a = F2Dot14(p); p += 2; b = F2Dot14(p); p += 2;
                    c = F2Dot14(p); p += 2; d = F2Dot14(p); p += 2;
                }

                more = (flags & MORE_COMPONENTS) != 0;

                GlyphProgram? part = HintedProgram(interpreter, componentGid, pixelsPerEm, depth + 1);
                if (part is null) continue;                 // a blank component places nothing

                int count = part.PointCount;
                var px = new int[count + 2];
                var py = new int[count + 2];
                for (int i = 0; i < count + 2; i++)         // the two horizontal phantoms travel too
                {
                    float fx = part.X[i], fy = part.Y[i];
                    px[i] = (int)MathF.Round(a * fx + c * fy);
                    py[i] = (int)MathF.Round(b * fx + d * fy);
                }

                int dx, dy;
                if ((flags & ARGS_ARE_XY_VALUES) != 0)
                {
                    dx = interpreter.ScaleToPixels(arg1);
                    dy = interpreter.ScaleToPixels(arg2);

                    // Whether the offset goes through the component's transform is the font's to
                    // say, and the default -- what every face that says nothing means -- is that it
                    // does not.
                    if ((flags & SCALED_COMPONENT_OFFSET) != 0)
                    {
                        int tx = (int)MathF.Round(a * dx + c * dy);
                        dy = (int)MathF.Round(b * dx + d * dy);
                        dx = tx;
                    }

                    // ROUND_XY_TO_GRID is why an accent sits on a whole pixel above a letter that is
                    // already on one, instead of half a pixel above it and grey on both rows.
                    if ((flags & ROUND_XY_TO_GRID) != 0)
                    {
                        dx = TrueTypeInterpreter.RoundToPixel(dx);
                        dy = TrueTypeInterpreter.RoundToPixel(dy);
                    }
                }
                else
                {
                    // The other way of saying where a component goes: line THIS point of what has
                    // been assembled up with THAT point of the component.
                    if (arg1 < 0 || arg1 >= xs.Count || arg2 < 0 || arg2 >= count) return null;
                    dx = xs[arg1] - px[arg2];
                    dy = ys[arg1] - py[arg2];
                }

                int baseIndex = xs.Count;
                for (int i = 0; i < count; i++)
                {
                    xs.Add(px[i] + dx);
                    ys.Add(py[i] + dy);
                    onCurve.Add(part.OnCurve[i]);
                }
                foreach (int last in part.EndPoints) ends.Add(last + baseIndex);

                if ((flags & USE_MY_METRICS) != 0)
                {
                    borrowedMetrics = true;
                    borrowedOrigin = px[count] + dx;
                    borrowedAdvance = px[count + 1] + dx;
                }
            }

            if (xs.Count == 0 || ends.Count == 0) return null;

            // The composite's own program, which follows the component list when there is one.
            var instructions = Array.Empty<byte>();
            if ((flags & WE_HAVE_INSTRUCTIONS) != 0)
            {
                int length = U16(p); p += 2;
                if (length > 0 && p + length <= _data.Length)
                {
                    instructions = new byte[length];
                    Array.Copy(_data, p, instructions, 0, length);
                }
            }

            int points = xs.Count;
            var X = new int[points + 4];
            var Y = new int[points + 4];
            var On = new bool[points + 4];
            for (int i = 0; i < points; i++) { X[i] = xs[i]; Y[i] = ys[i]; On[i] = onCurve[i]; }

            // The phantom points, in pixels like everything else here -- a composite is not scaled
            // again on the way in, so nothing else will convert them.
            if (borrowedMetrics)
            {
                X[points] = borrowedOrigin;
                X[points + 1] = borrowedAdvance;
            }
            else
            {
                int origin = xMin - MetricsLeftSideBearing(gid);
                X[points] = interpreter.ScaleToPixels(origin);
                X[points + 1] = interpreter.ScaleToPixels(origin + RawAdvanceWidth(gid));
            }
            return new GlyphProgram
            {
                X = X,
                Y = Y,
                OnCurve = On,
                EndPoints = ends.ToArray(),
                Instructions = instructions,
                PointCount = points,
                Composite = true,
            };
        }

        private GlyphProgram? ReadGlyphProgram(int gid)
        {
            if (_glyfOffset < 0 || gid < 0 || gid >= _numGlyphs || _loca.Length == 0) return null;
            uint start = _loca[gid], end = _loca[gid + 1];
            if (end <= start) return null;

            int p = _glyfOffset + (int)start;
            int numContours = (short)U16(p);
            if (numContours <= 0) return null;
            int xMin = (short)U16(p + 2);
            p += 10;

            var endPts = new int[numContours];
            for (int i = 0; i < numContours; i++) { endPts[i] = U16(p); p += 2; }
            int numPoints = endPts[numContours - 1] + 1;
            if (numPoints <= 0) return null;

            int instructionLength = U16(p); p += 2;
            var instructions = new byte[instructionLength];
            Array.Copy(_data, p, instructions, 0, instructionLength);
            p += instructionLength;

            var flags = new byte[numPoints];
            for (int i = 0; i < numPoints;)
            {
                byte f = _data[p++];
                flags[i++] = f;
                if ((f & 0x08) != 0)
                {
                    int repeat = _data[p++];
                    while (repeat-- > 0 && i < numPoints) flags[i++] = f;
                }
            }

            var xs = new int[numPoints + 4];
            int x = 0;
            for (int i = 0; i < numPoints; i++)
            {
                byte f = flags[i];
                if ((f & 0x02) != 0) { int dx = _data[p++]; x += (f & 0x10) != 0 ? dx : -dx; }
                else if ((f & 0x10) == 0) { x += (short)U16(p); p += 2; }
                xs[i] = x;
            }
            var ys = new int[numPoints + 4];
            int y = 0;
            for (int i = 0; i < numPoints; i++)
            {
                byte f = flags[i];
                if ((f & 0x04) != 0) { int dy = _data[p++]; y += (f & 0x20) != 0 ? dy : -dy; }
                else if ((f & 0x20) == 0) { y += (short)U16(p); p += 2; }
                ys[i] = y;
            }

            // The phantom points. A program aligns a glyph's left edge by reading the first of
            // them, so where it sits matters: it is the glyph's ORIGIN, which is its bounding box
            // less its side bearing -- zero for a well-formed face, and not zero for one whose
            // 'hmtx' and 'glyf' disagree. Putting the side bearing there instead moves the origin
            // into the middle of the letter and everything measured from it with it.
            int origin = xMin - LeftSideBearing(gid, xs, numPoints);
            xs[numPoints] = origin;
            xs[numPoints + 1] = origin + RawAdvanceWidth(gid);

            var onCurve = new bool[numPoints + 4];
            for (int i = 0; i < numPoints; i++) onCurve[i] = (flags[i] & 0x01) != 0;

            return new GlyphProgram
            {
                X = xs,
                Y = ys,
                OnCurve = onCurve,
                EndPoints = endPts,
                Instructions = instructions,
                PointCount = numPoints,
            };
        }

        /// <summary>Where the glyph's ink starts relative to its origin. 'hmtx' carries it, and a
        /// program that lines a stem up against the left edge reads the phantom point that holds
        /// it -- so getting it wrong moves every hinted glyph sideways.</summary>
        private int LeftSideBearing(int gid, int[] xs, int numPoints)
        {
            if (TryMetricsLeftSideBearing(gid, out int bearing)) return bearing;

            int min = int.MaxValue;
            for (int i = 0; i < numPoints; i++) min = Math.Min(min, xs[i]);
            return min == int.MaxValue ? 0 : min;
        }

        /// <summary>The side bearing 'hmtx' records, for a caller with no points to fall back on --
        /// a composite, whose points are already in pixels and cannot be measured in font units.
        /// </summary>
        private int MetricsLeftSideBearing(int gid)
            => TryMetricsLeftSideBearing(gid, out int bearing) ? bearing : 0;

        private bool TryMetricsLeftSideBearing(int gid, out int bearing)
        {
            bearing = 0;
            if (_hmtxOffset < 0) return false;

            if (gid < _numHMetrics)
            {
                bearing = (short)U16(_hmtxOffset + gid * 4 + 2);
                return true;
            }

            // Past the last full metric the table holds bearings only, one per glyph.
            int at = _hmtxOffset + _numHMetrics * 4 + (gid - _numHMetrics) * 2;
            if (at + 1 >= _data.Length) return false;
            bearing = (short)U16(at);
            return true;
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
            _tableLengths = new Dictionary<string, int>(numTables);
            int p = _sfntBase + 12;
            for (int i = 0; i < numTables; i++)
            {
                string tag = System.Text.Encoding.ASCII.GetString(_data, p, 4);
                int offset = (int)U32(p + 8);
                tables[tag] = offset;
                // The hinting tables are byte streams, not structures, so their LENGTH is the only
                // thing that says where they stop.
                _tableLengths[tag] = (int)U32(p + 12);
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
