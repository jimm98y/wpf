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

        /// <summary>The device advance this glyph gets at this size, ALWAYS -- the face's own
        /// table if it ships one, its hinted phantom if it does not, and the scaled advance if
        /// neither applies. TryGetDeviceAdvance answers only the first two and leaves the caller
        /// to invent the third, and the two callers that did invented it differently.</summary>
        float DeviceAdvance(int glyphId, float pixelsPerEm);

        /// <summary>Whether the face asks to be smoothed in BOTH directions at this size.</summary>
        bool WantsSymmetricSmoothing(float pixelsPerEm);

        /// <summary>Whether the face asks to be GRID-FITTED at this size. Symmetric
        /// smoothing means two different things depending on the answer -- see
        /// WgpuSceneRenderer's SymmetricRows.</summary>
        bool WantsGridFit(float pixelsPerEm);

        /// <summary>Whether the face's pre-program turns DROPOUT CONTROL on at this size
        /// (SCANCTRL), and which SCANTYPE it asks for. See TrueTypeInterpreter.PrepScanControl.</summary>
        bool WantsDropoutControl(float pixelsPerEm, out int scanType);
    }

    internal sealed class TrueTypeFont : IFont, IGlyphOutlineFont, IColorGlyphFont, IBitmapGlyphFont,
                                         IHintedGlyphFont,
                                         IOpenTypeShapingFont
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
        private int _ltshOffset = -1;   // per-glyph linear threshold, or -1 when the face ships none

        /// <summary>head.flags bit 4, "instructions may alter advance width". When a face leaves it
        /// CLEAR its advance is the scaled design advance rounded once, whatever its program
        /// leaves between the phantom points -- see <see cref="CompatibleAdvance"/>.</summary>
        private const int HeadInstructionsAlterAdvance = 0x10;
        private bool _instructionsMayAlterAdvance = true;
        private int _ltshGlyphs;
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
                ? os : 87f / 256f;                           // 0.33984375, GDI's own, see below
        // GDI'S OWN NUMBER, READ OFF ITS FITTED POINTS. GetGlyphOutline reports the simulated
        // italic as the UPRIGHT hinted outline sheared afterwards, x += shear * y, rounded to a
        // sixty-fourth. Pairing our upright fit with it (FittedPoints_AgainstGdisOwn shears ours
        // the same way) leaves the shear as the only unknown, and 'l', 'H' and 'd' at 96 and 250
        // pixels an em pin it to [0.339844, 0.339864]: 87/256 exactly, provided a half rounds up,
        // and Tahoma's simulated italic pairs 24 of 24 points of 'o' and 39 of 39 of 'd' with
        // nothing further than half a sixty-fourth from GDI's. The two paragraphs below are the
        // earlier measurements that got within 0.4% of it from the pixels alone.
        //
        // AND DO NOT RE-DERIVE IT FROM THE POINTS THAT ARE WRONG. 2026-09-20: Tahoma Italic 'X'
        // at 10ppem is wrong at eight sub-pixel phases, every one of them wanting exactly -1/64 on
        // one point, and solving `shear * y * 64` for the coefficient that would give GDI's term
        // at those four points lands on shear in [0.33520, 0.33647) -- a tight interval that
        // EXCLUDES 87/256 and is centred on 86/256. It is wrong, and expensively so: 86/256
        // measures 507,895 on the holdout against 28,183, and Tahoma Italic alone 4,128 ->
        // 483,840. The interval was fitted to the handful of points that disagree while ignoring
        // the hundreds that already agree, and moving the coefficient by one 256th moves all of
        // them. A shear derived from failures alone will always look tighter than it is.
        //
        // MEASURED OFF GDI'S OWN PIXELS, not swept against a specimen. Draw a vertical stem with
        // GDI's simulated italic, take the sub-pixel CENTROID of each row's ink -- for a single
        // stem that is its centre line -- and the slope through those centroids is the shear. Over
        // 'l', 'H' and 'I' at 24, 32, 40 and 48 pixels an em it reads 0.334 to 0.343, mean 0.3385,
        // which is 18.7 degrees. GdisSimulatedItalic_LeansByAMeasurableAmount is that measurement.
        // Sweeping the specimen agrees to within its own noise -- 168,812 at 0.3385 against 170,407
        // at 0.34 and 171,490 at a third -- so the two methods are independent and land together.
        // Not tan(20 degrees), which is what DirectWrite simulates with and what this used to hold:
        // that costs Tahoma's italic 195,634 against 168,812.
        //
        // AND THE MAGNITUDE WAS NEVER THE PROBLEM when this was set to 0.20. That was briefly set because
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
            _headXMin = (short) U16(head + 36);
            _headXMax = (short) U16(head + 40);
            _headYMin = (short) U16(head + 38);
            _headYMax = (short) U16(head + 42);
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
            GdiContrastPalette = ComputeGdiContrastPalette(tables);

            // Outlines are OPTIONAL, because a colour BITMAP font has none.
            //
            // Noto Color Emoji (Linux, Android) and its kin store every glyph as a PNG in CBDT and
            // ship no 'glyf' or 'loca' at all. Requiring them threw during construction, the font
            // resolver caught it and returned null, and emoji rendered as nothing. A font with
            // neither outlines nor bitmaps is still an error -- it can draw nothing whatsoever.
            bool hasOutlines = tables.TryGetValue("loca", out int loca) & tables.TryGetValue("glyf", out int glyf);
            _glyfOffset = hasOutlines ? glyf : -1;

            int glyphCount = U16(maxp + 4);
            // EBLC/EBDT is the same layout as CBLC/CBDT with monochrome images instead of PNGs,
            // so it is read by the same parser. The East Asian faces ship these -- and GDI draws
            // them in preference to the outline, which is why CJK is crisp at UI sizes.
            // ...BUT ONLY FOR A FACE THAT DECLARES A DOUBLE-BYTE CHARSET. ttfd's
            // vSetClearTypeState swaps the outline for a strike only when the font's flag word has
            // bit 8, which vFill_IFIMETRICS sets from IsAnyCharsetDbcs -- ShiftJIS, Hangeul,
            // GB2312 or Big5 among its charsets, OS/2 code pages 932/949/936/950. Calibri,
            // Cambria, Courier New and Lucida Console all ship EBDT strikes too, and GDI draws
            // their OUTLINES at every size: we were blitting Courier New's 1-bit strike and
            // came out 20-38% light against GDI's ClearType. A face with no outlines keeps its
            // strikes, since they are all it has. WPF_EBDT_DBCS_ONLY=0 uses every strike again.
            if (!tables.ContainsKey("CBLC") && tables.TryGetValue("EBLC", out int eblc)
                && tables.TryGetValue("EBDT", out int ebdt))
            {
                var strikes = new BitmapGlyphTable(_data, eblc, ebdt);
                if (strikes.HasStrikes)
                {
                    _metricStrikes = strikes;
                    if (!hasOutlines || !s_ebdtDbcsOnly || DeclaresDbcsCharset(tables)) _bitmaps = strikes;
                }
            }
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
            _instructionsMayAlterAdvance = (U16(head + 16) & HeadInstructionsAlterAdvance) != 0;
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

            // 'LTSH' -- the LINEAR THRESHOLD, one ppem per glyph: at and above it the glyph's
            // advance is exactly the scaled design advance, and below it the face's hinting moves
            // it. GDI consults this before it consults the hinted phantom, and we did not consult
            // it at all, so eight of Arial Italic's capitals came out a pixel wide at 9ppem: their
            // thresholds are 6 and 7, so Windows takes the linear advance (6.4995 -> 6) where we
            // ran the program and got exactly 6.5, which rounds to 7.
            if (tables.TryGetValue("LTSH", out int ltsh) && U16(ltsh) == 0)
            {
                int count = U16(ltsh + 2);
                if (count > 0 && ltsh + 4 + count <= _data.Length)
                {
                    _ltshOffset = ltsh + 4;
                    _ltshGlyphs = count;
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

            // The substitutions the face makes to its own glyphs -- the four Arabic positional
            // forms among them. Absent from most Latin faces, and absent here until Arabic was
            // measured and found to be drawing isolated letters.
            if (tables.TryGetValue("GSUB", out int gsub))
                Gsub = new GsubTable(_data, gsub);
        }

        private static readonly bool s_ebdtDbcsOnly =
            Environment.GetEnvironmentVariable("WPF_EBDT_DBCS_ONLY") != "0";

        /// <summary>IsAnyCharsetDbcs@14001eff8 on the face's OS/2 code-page bits: 932 (bit 17),
        /// 936 (18), 949 (19) or 950 (20) -- ShiftJIS, GB2312, Hangeul, Big5; not Johab.</summary>
        /// <summary>Whether GDI draws this face's ClearType through the CONTRAST palette.
        /// <para>ulClearTypeFilter_6x1 picks between its two 243-entry palettes on FONTOBJ+0x40,
        /// and the only thing that sets that field is a name list in win32k's
        /// RFONTOBJ::bRealizeFont@14019b8b4: under ClearType, for a face whose IFIMETRICS flInfo
        /// has FM_INFO_CONSTANT_WIDTH or FM_INFO_OPTICALLY_FIXED_PITCH, whose usWinWeight is
        /// under 401, and whose family name is -- case-insensitively -- Courier New, Rod, Rod
        /// Transparent, Fixed Miriam Transparent, Miriam Fixed or Simplified Arabic Fixed. So
        /// installed Courier New regular and italic get the heavy palette and its bold does not,
        /// and a copy of the same file under another family name does not either: thin
        /// monospaced faces Windows chose by name to darken. Without it Courier New R/I drew
        /// 38% of GDI's ink short at 8ppem. WPF_CT_CONTRAST=0/1 still forces it off/on.</para>
        /// </summary>
        internal bool GdiContrastPalette { get; }

        private static readonly string[] s_contrastFamilies =
        {
            "Courier New", "Rod", "Rod Transparent", "Fixed Miriam Transparent", "Miriam Fixed",
            "Simplified Arabic Fixed",
        };

        private bool ComputeGdiContrastPalette(Dictionary<string, int> tables)
        {
            if (!tables.TryGetValue("post", out int post) || post + 16 > _data.Length) return false;
            if (U32(post + 12) == 0) return false;                         // isFixedPitch
            if (!tables.TryGetValue("OS/2", out int os2) || os2 + 6 > _data.Length) return false;
            if (U16(os2 + 4) > 400) return false;                          // usWeightClass
            if (!FontFiles.ReadNames(_data, _sfntBase, out string? family, out _, out _)) return false;
            foreach (string f in s_contrastFamilies)
                if (string.Equals(f, family, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private bool DeclaresDbcsCharset(Dictionary<string, int> tables)
        {
            if (!tables.TryGetValue("OS/2", out int os2) || os2 + 82 > _data.Length) return false;
            if (U16(os2) < 1) return false;
            uint range1 = (uint) (U16(os2 + 78) << 16 | U16(os2 + 80));
            return (range1 & (0xFu << 17)) != 0;
        }

        /// <summary>The face's GSUB table, or null when it has none.</summary>
        public GsubTable? Gsub { get; }

        // ---- IColorGlyphFont ----

        /// <summary>WPF_COLR=0 draws a colour font's glyphs as plain outlines, which is what GDI
        /// does with one -- it has no COLR support at all. For asking what GDI drew.</summary>
        private static readonly bool s_noColor =
            System.Environment.GetEnvironmentVariable("WPF_COLR") == "0";

        public bool TryGetColorLayers(int glyphId, out IReadOnlyList<ColorGlyphLayer> layers)
        {
            if (s_noColor) { layers = System.Array.Empty<ColorGlyphLayer>(); return false; }
            if (_color != null) return _color.TryGetColorLayers(glyphId, out layers);
            layers = System.Array.Empty<ColorGlyphLayer>();
            return false;
        }

        // ---- IBitmapGlyphFont ----

        public bool TryGetGlyphBitmap(int glyphId, out BitmapGlyph glyph, int ppem = 0)
        {
            if (_bitmaps != null) return _bitmaps.TryGetGlyphBitmap(glyphId, out glyph, ppem);
            glyph = default;
            return false;
        }

        // ---- IShapingFont ----

        public int GlyphIndex(char c) => _cmap.Map(c);

        public float Advance(int glyphId) => AdvanceWidth(glyphId) * _scale;

        /// <summary>The space the design leaves to the RIGHT of the ink, in pixels: the advance
        /// less the left side bearing and less the ink's own width.</summary>
        internal float RightSideBearingForTest(int glyphId, float pixelsPerEm)
        {
            if (_glyfOffset < 0 || glyphId < 0 || glyphId >= _numGlyphs || _loca.Length == 0) return 0f;
            uint start = _loca[glyphId], end = _loca[glyphId + 1];
            if (end <= start) return 0f;
            int p = _glyfOffset + (int) start;
            int xMin = (short) U16(p + 2), xMax = (short) U16(p + 6);
            int lsb = MetricsLeftSideBearing(glyphId);
            float rsb = AdvanceWidth(glyphId) - lsb - (xMax - xMin);
            return rsb * pixelsPerEm / (float) _unitsPerEm;
        }

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
        /// <summary>The points of the glyph hinted last, once <see cref="TrueTypeInterpreter
        /// .s_capturePoints"/> is on -- the only way to see which points the face's program
        /// touched, which no fitted outline records.</summary>
        internal TrueTypeInterpreter.GlyphPoints? LastHintedPoints => _interpreter?.LastPoints;

        /// <summary>The x-shear a synthesized oblique adds to every point AFTER hinting (x += shear * y,
        /// y up), 0 for a face drawn as it is. The oracle tests need it: the interpreter's captured
        /// points are upright and GDI's report of a simulated italic is not.</summary>
        internal float ObliqueShearApplied => _shear;

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

            // The face's own answer to GETINFO's symmetric-rendering query, which it branches its
            // whole hinting program on. Wired here rather than passed per call: the interpreter is
            // built once per face and already knows the size it is running at.
            interpreter.FaceWantsSymmetricSmoothing = WantsSymmetricSmoothing;
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
            if (!TryGetDeviceAdvanceCore(glyphId, pixelsPerEm, out advance)) return false;
            if (GdiEmboldens) advance += SimBoldAdvancePixels((int) MathF.Round(pixelsPerEm));
            return true;
        }

        private bool TryGetDeviceAdvanceCore(int glyphId, float pixelsPerEm, out float advance)
        {
            advance = 0f;
            if (glyphId < 0 || glyphId >= _numGlyphs || pixelsPerEm <= 0f) return false;

            // Both sources are written for whole pixel sizes. Anything between two of them is a size
            // nothing was measured at, and the caller's own rounding is as good an answer as any.
            int ppem = (int)MathF.Round(pixelsPerEm);
            if (MathF.Abs(pixelsPerEm - ppem) > 0.01f || ppem <= 0 || ppem > 255) return false;

            if (TryGetHdmxAdvance(glyphId, ppem, out advance)) return true;
            if (!_instructionsMayAlterAdvance)      // head.flags bit 4 clear: linear, rounded once
            {
                advance = MathF.Round(Advance(glyphId) * pixelsPerEm / PixelsPerEm, MidpointRounding.AwayFromZero);
                return true;
            }

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
        private readonly int _headXMin, _headXMax, _headYMin, _headYMax;

        /// <summary>The glyph COLUMNS GDI keeps, relative to the pen: bComputeMaxGlyph@14001b198
        /// scales head.xMin/xMax into 28.4 and takes floor(min) and ceil(max) as the font
        /// context's +0x98/+0x9c, and vFillGLYPHDATA's _GMC drops the bitmap columns outside
        /// [+0x98, +0x9c) when it copies the glyph out -- before win32k filters the run, so the
        /// neighbouring pixel still takes the clipped lamps' filter spill. It only bites where a
        /// glyph is wider than the face's own box, which is FO_SIM_BOLD's extra pixel: Lucida
        /// Console Bold 'w' at 11ppem smeared into a column GDI cuts off.</summary>
        internal bool TryGetGdiColumnLimits(float pixelsPerEm, out int left, out int right)
        {
            left = right = 0;
            if (_unitsPerEm <= 0 || _headXMax <= _headXMin) return false;
            double k = pixelsPerEm * 16.0 / _unitsPerEm;
            // The simulated oblique widens the box first (bComputeMaxGlyph shears the corners by
            // 0x5700 before scaling them).
            double xMin = _headXMin, xMax = _headXMax;
            if (_shear != 0f)
            {
                double sh = Math.Abs(_shear);
                xMin += Math.Min(0.0, sh * _headYMin);
                xMax += Math.Max(0.0, sh * _headYMax);
            }
            int lo = (int) Math.Round(xMin * k, MidpointRounding.AwayFromZero);
            int hi = (int) Math.Round(xMax * k, MidpointRounding.AwayFromZero);
            // ...with the margins its device-metrics arm adds: two pixels left, one right.
            // Lucida Console Bold 'w' at 11ppem: GDI keeps pixel 7 of a box whose scaled xMax is
            // 6.93 and drops pixel 8.
            left = (lo >> 4) - 2;
            right = ((hi + 15) >> 4) + 1;
            return true;
        }

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
        private static int s_outlineCalls;
        private static readonly bool s_outlineProbe =
            Environment.GetEnvironmentVariable("WPF_TGHO_PROBE") == "1";
        private static readonly int s_probeGid =
            int.TryParse(Environment.GetEnvironmentVariable("WPF_TGHO_GID"), out int pg) ? pg : -1;

        private static readonly bool s_cacheByBiLevel =
            Environment.GetEnvironmentVariable("WPF_HINTCACHE_BILEVEL") != "0";

        /// <summary>WPF_CT_Y_BILEVEL=1: take the fitted Y from a bi-level pass. Diagnostic.</summary>
        private static readonly bool s_yFromBiLevel =
            Environment.GetEnvironmentVariable("WPF_CT_Y_BILEVEL") == "1";

        /// <summary>WPF_CT_TWOPASS=1: run the glyph program twice, as fs__Contour does under
        /// compatible widths. See the call site.</summary>
        private static readonly bool s_twoPassGlyph =
            Environment.GetEnvironmentVariable("WPF_CT_TWOPASS") == "1";

        private static readonly bool s_compatProbe =
            Environment.GetEnvironmentVariable("WPF_COMPAT_PROBE") == "1";

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

        /// <summary>Set while a bi-level run is measuring an advance, so that the compatible-width
        /// correction inside that run does not ask for the advance it is in the middle of
        /// computing. Without it the two call each other for ever.</summary>
        [ThreadStatic] private static bool s_measuringAdvance;

        public float DeviceAdvance(int glyphId, float pixelsPerEm)
            => CompatibleAdvance(glyphId, pixelsPerEm, (int) MathF.Round(pixelsPerEm))
               + (GdiEmboldens ? SimBoldAdvancePixels((int) MathF.Round(pixelsPerEm)) : 0);

        /// <summary>WHERE THE TEXT DIFFERENCE STANDS, once this and the margin were fixed.
        /// <para>PLACEMENT IS SOLVED. Letting every 16-pixel window of the specimen shift
        /// independently to its best offset removes 0% of the difference at 9 and 12ppem, and no
        /// row at 16 or 20ppem wants a shift at all. Every advance matches GDI across six faces,
        /// four styles and 9..20ppem. Nothing positional is left to find.</para>
        /// <para>THE SHADING IS RIGHT TOO. Subpixel gamma is a clean symmetric minimum at the
        /// default 1.20 -- 1.15 and 1.25 both cost about 137,000 at 12ppem -- which is exactly
        /// what this machine's FontSmoothingGamma of 1200 derives.</para>
        /// <para>SO WHAT REMAINS IS THE FITTED OUTLINE. 223 of 291 solved coordinates now land
        /// inside the interval GDI's own pixels allow, and the 68 that do not are the whole of the
        /// remaining difference. 'H' at 12ppem is still the clearest case: GDI's stem starts on a
        /// lamp boundary (73 153 255 197 111 36, 2.157px) and ours straddles one
        /// (36 111 197 197 111 36, 1.799px).</para>
        /// <para>The x-hint mode is not the lever -- re-swept on this geometry, mode 5 beats mode 6
        /// by 9,429,383 to 13,811,139 over 9/12/16/20ppem -- so this is the interpreter's own
        /// per-glyph fitting, and see stem-width notes in TrueTypeInterpreter for what has already
        /// been eliminated there.</para></summary>
        /// <summary>The advance a BI-LEVEL rasterizer would give this glyph -- what compatible
        /// widths means, and what the fitted glyph has to be corrected onto.
        /// <para>'hdmx' is a cache of exactly these numbers, so a face that ships one is answered
        /// from the table. A face that ships none has to be MEASURED, by running its program in
        /// bi-level mode, and that is not the same as scaling the design advance: Verdana's own
        /// program widens 'w' from 9.82 pixels to 11 and narrows 'm' from 11.67 to 11, and the
        /// rounded design advance -- what this used to fall back to -- gets both wrong along with
        /// fourteen other letters. Thirteen pixels of drift over a line of fifty, so every glyph
        /// past the third landed on different pixels from Windows'.</para>
        /// <para>Verdana is the only face in the specimen with neither 'hdmx' nor 'LTSH', which is
        /// the font telling you outright that its advances are not linear and there is no table to
        /// look them up in. Segoe UI, Arial, Times, Tahoma and Consolas all ship 'hdmx' and never
        /// reached this path, which is why the fallback could be wrong for as long as it was.</para>
        /// </summary>
        /// <summary>Internal so a probe can ask the PRODUCT rather than reimplement it: the
        /// advance test used to duplicate this arithmetic and then disagreed with the product
        /// after it was fixed, reporting a difference that had already been repaired.</summary>
        internal float CompatibleAdvance(int gid, float pixelsPerEm, int ppemI)
        {
            if (TryGetHdmxAdvance(gid, ppemI, out float hd)) return hd;
            // THEN THE FACE THAT SAYS ITS PROGRAM NEVER MOVES THE ADVANCE (head.flags bit 4 clear):
            // the design advance rounded once, and the program is not asked -- its phantom points
            // round differently (Consolas at 10ppem: 5.498 -> 5 here, 6 by the phantoms, 50 pixels
            // over a line). The long note below on the space explains how this was found.
            if (!_instructionsMayAlterAdvance)
                return MathF.Round(Advance(gid) * pixelsPerEm / PixelsPerEm,
                                   MidpointRounding.AwayFromZero);
            // THEN THE LINEAR THRESHOLD. At or above it the face declares its own advance linear,
            // so the scaled design advance IS the answer and the glyph program must not be asked.
            // Segoe UI's italic space is the case that shows both sides of this: its threshold is
            // 21, so at 20ppem it is NOT linear and takes the sixty-fourths path to 6, while the
            // regular face's threshold is 1 and the same size takes the linear path to 5.
            if (_ltshOffset >= 0 && gid >= 0 && gid < _ltshGlyphs)
            {
                int threshold = _data[_ltshOffset + gid];
                if (threshold > 0 && ppemI >= threshold)
                    return MathF.Round(Advance(gid) * pixelsPerEm / PixelsPerEm,
                                       MidpointRounding.AwayFromZero);
            }
            // THEN THE SIZES WHERE THE FACE'S 'gasp' TELLS A CLEARTYPE RASTERIZER NOT TO FIT. GDI
            // does not run the program there at all, so the advance is the scaled design advance
            // through the sixty-fourths below -- NOT the bi-level hinted one. Arial Regular at 7 and
            // 8ppem is the face that can tell: its program narrows 'k' 'v' 'x' 'y' from 4 to 3 and
            // 'm' from 6.66 to 6, and GDI's ClearType realization lays them out at 4 and 7 (a
            // one-bit DC, where gasp does not apply and the program runs, at 3 and 6 -- which is
            // the DC the advance probe used to measure on, and why this was invisible). The gasp
            // gate that used to sit here was right about the sizes and wrong about the rounding:
            // it rounded the design advance ONCE, and Segoe UI Italic 'n' (4.496 -> 288/64 -> 4.5
            // -> 5) and Verdana Bold 'o' (5.492 -> 6) looked like hinted advances until the
            // sixty-fourths were put back. A VERSION 0 table has no ClearType bits and GDI fits
            // those faces at every size: Arial Bold 'w' at 8ppem is 6.22 linear and 5 in GDI.
            if (!(ClearTypeRendering && GaspDeclinesClearTypeGridFit(pixelsPerEm))
                && TryGetHintedAdvance(gid, pixelsPerEm, out float hinted))
                return hinted;
            // AWAY FROM ZERO, because that is what GDI does and MathF.Round does not: its default
            // is banker's rounding, which sends a half DOWN to the even number.
            //
            // It costs a whole pixel wherever a scaled advance lands exactly on a half, and those
            // are not rare -- an advance of half an em at an even ppem is exact. Times New Roman's
            // SPACE is 512 units of a 2048 em, which is 2.5 pixels at 10ppem: we rounded it to 2
            // where Windows uses 3. The specimen line has two spaces in it, so every Times row came
            // out two pixels short of Windows' in all four styles.
            // THROUGH 26.6 FIRST. TrueType scales to a sixty-fourth of a pixel and rounds there,
            // and only then rounds to whole pixels; rounding once in float is not the same
            // function and differs exactly where the two roundings straddle a half.
            //
            // Segoe UI's italic space is 563 units of a 2048 em, which is 5.4980 pixels at 20ppem:
            // one rounding gives 5, but 5.4980 lands on 351.875 sixty-fourths, which rounds to 352
            // -- exactly 5.5 -- and then to 6, which is what Windows uses. Its REGULAR space is
            // 561 units, 5.4785 pixels, 350.625 sixty-fourths, 351, 5.484, 5 -- and Windows uses 5.
            // No single rounding of the float can separate those two.
            //
            // A space has no outline, so nothing else intercepts this: the specimen's Segoe UI
            // italic and bold-italic rows at 20ppem drifted a pixel per space, two by end of line.
            // ...but only where the face SAYS ITS PROGRAM MAY MOVE THE ADVANCE. Consolas at 10ppem
            // and Segoe UI italic at 20ppem have the SAME scaled advance, 5.498046875, and Windows
            // answers 5 for one and 6 for the other -- so it cannot be a function of that number
            // alone. What separates them is head.flags bit 4: Segoe UI sets it, Consolas does not,
            // and a face that declares its advances linear is spaced at the design advance rounded
            // ONCE, whatever its phantom points would have rounded to. Consolas is the proof, at
            // every size measured: 9ppem 4.948 -> 5, 10 5.498 -> 5, 30 16.494 -> 16 (the
            // sixty-fourths would say 17: 1055.625 -> 1056 -> 16.5 -> 17), 50 27.49 -> 27.
            //
            // This used to read the 'gasp' instead -- Consolas asks for no gridfit at 10ppem and
            // below, and that happened to separate the same two cases. It was the wrong table:
            // GDI's advance is the bi-level hinted advance at EVERY size, gasp or no gasp (its
            // GetCharWidth32 is identical under DRAFT, NONANTIALIASED, ANTIALIASED and CLEARTYPE
            // quality), and the gasp gate left Times Bold, Arial Bold, Verdana Bold and Segoe UI
            // Italic drifting up to seven pixels a line at 8ppem, where every one of them asks for
            // no gridfit and Windows spaces them by their programs regardless. (The bit-4 face has
            // already been answered above, before the program was asked.)
            // THIS IS THE SCALER'S ADVANCE -- its phantom points -- and a composite's phantoms are
            // its USE_MY_METRICS component's (fsg_ExecuteGlyph copies them over), not its own hmtx
            // entry. Comic Sans MS Italic 'ydieresis' is 883 units wide but borrows 'y's 1066, so
            // at 8ppem (unfitted, LTSH 255) GDI spaces it 4 where 883 gave 3, and every glyph after
            // it on the line moved a pixel. The linear and head-flag answers above stay the
            // composite's: they model ttfd reading hmtx directly. WPF_UNFITTED_ADV_BORROW=0 undoes.
            int advGid = gid;
            for (int depth = 0; s_unfittedAdvBorrow && depth < 4 && TryGetMetricsComponent(advGid, out int mcg); depth++)
                advGid = mcg;
            int sixtyFourths = (int) MathF.Round(Advance(advGid) * 64f * pixelsPerEm / PixelsPerEm,
                                                 MidpointRounding.AwayFromZero);
            return MathF.Round(sixtyFourths / 64f, MidpointRounding.AwayFromZero);
        }

        // Hinting a glyph to ask how wide it is costs as much as hinting it to draw it, and a run of
        // text asks for the same handful of glyphs over and over.
        private readonly Dictionary<(int Glyph, int Size), float> _hintedAdvances = new();

        /// <summary>The same measurement UNROUNDED, in sixty-fourths. The advance GDI lays a glyph
        /// out at is a whole number of pixels, but the PHASE SCALE is not built from that number:
        /// fs__Contour reads the bi-level pass's phantom points directly --
        /// <code>uVar43 = curX[last + 2] - curX[last + 1]</code> -- and divides by what the client
        /// answers for the same advance in font units. So the numerator keeps its sixty-fourths.
        /// </summary>
        private readonly Dictionary<(int Glyph, int Size), int> _hintedSpans = new();

        /// <summary>The distance the face's own program leaves between the two horizontal phantom
        /// points -- the advance the glyph is actually drawn with. False when there is no program to
        /// run, or the glyph has no outline for one to run on.</summary>
        /// <summary>The bi-level pass's phantom span in sixty-fourths, unrounded -- the numerator
        /// of fs__Contour's phase scale. Measured by the same run as <see cref="TryGetHintedAdvance"/>
        /// and cached beside it, so asking for one costs the other nothing.</summary>
        /// <summary>fs__Contour's ACTUAL numerator: the phantom span of a CLEARTYPE pass run with the
        /// factor at one. GDI runs the glyph program twice under compatible widths and both runs
        /// are the ClearType program -- its own instruction stream (ctharness TRACEOPS) shows the
        /// two passes executing identically until the second's pre-scaled x meets IUP -- so the
        /// span it divides by the linear advance is a SIXTEENTH-rounded ClearType span, not the
        /// whole-pixel bi-level one. Times Italic 'm' at 22ppem: GDI's factor is 0xffc0 (span
        /// 1016 over a linear 1017) where the bi-level span gave 1024/1017.</summary>
        internal bool TryGetClearTypeSpan64(int glyphId, float pixelsPerEm, out int span64)
        {
            var key = (glyphId, (int)MathF.Round(pixelsPerEm * 16f));
            if (_ctSpans.TryGetValue(key, out span64)) return span64 > 0;
            span64 = 0;
            TrueTypeInterpreter? interpreter = Interpreter();
            if (interpreter is null || s_measuringCtSpan) return false;
            bool savedBi = TrueTypeInterpreter.BiLevelPass;
            bool savedSub = SubpixelFitting;
            s_measuringCtSpan = true;
            TrueTypeInterpreter.BiLevelPass = false;
            SubpixelFitting = true;
            try
            {
                GlyphProgram? glyph = HintedProgram(interpreter, glyphId, pixelsPerEm, 0);
                if (glyph is not null) span64 = glyph.X[glyph.PointCount + 1] - glyph.X[glyph.PointCount];
            }
            finally
            {
                s_measuringCtSpan = false;
                TrueTypeInterpreter.BiLevelPass = savedBi;
                SubpixelFitting = savedSub;
            }
            if (_ctSpans.Count > HintedCacheLimit) _ctSpans.Clear();
            _ctSpans[key] = span64;
            return span64 > 0;
        }

        private readonly System.Collections.Generic.Dictionary<(int, int), int> _ctSpans = new();
        [ThreadStatic] private static bool s_measuringCtSpan;
        /// <summary>WPF_CT_SPAN_PASS=ct takes the numerator from TryGetClearTypeSpan64. REFUTED as a
        /// general rule, emphatically: holdout 58 -> 46,606,822 with 355 ratchets failing (the same
        /// verdict WPF_CT_PHASE_NUM=ct reached), and even Times Italic 'm'@22, the glyph it was
        /// derived from, goes 58 -> 2,785. The span GDI divides by is, almost everywhere, the
        /// whole-pixel one the bi-level pass gives; what makes 'm'@22 an exception is not this.</summary>
        private static readonly bool s_spanFromCtPass =
            Environment.GetEnvironmentVariable("WPF_CT_SPAN_PASS") == "ct";

        private readonly System.Collections.Generic.Dictionary<(int, int), bool> _hintedSpanTouched = new();

        /// <summary>Whether the bi-level measuring pass moved the advance phantom in x.</summary>
        internal bool HintedSpanTouched(int glyphId, float pixelsPerEm)
        {
            var key = (glyphId, (int)MathF.Round(pixelsPerEm * 16f));
            if (!_hintedSpanTouched.TryGetValue(key, out bool t))
            {
                TryGetHintedAdvance(glyphId, pixelsPerEm, out float _);
                _hintedSpanTouched.TryGetValue(key, out t);
            }
            return t;
        }

        internal bool TryGetHintedSpan64(int glyphId, float pixelsPerEm, out int span64)
        {
            var key = (glyphId, (int)MathF.Round(pixelsPerEm * 16f));
            if (!_hintedSpans.TryGetValue(key, out span64))
            {
                TryGetHintedAdvance(glyphId, pixelsPerEm, out float _);
                if (!_hintedSpans.TryGetValue(key, out span64)) { span64 = 0; return false; }
            }
            return span64 > 0;
        }

        private bool TryGetHintedAdvance(int glyphId, float pixelsPerEm, out float advance)
        {
            // THE FACE'S 'gasp' DOES NOT GOVERN THIS. It did for a while: TryGetHintedOutline
            // declines to fit where the designer says not to, and Consolas at 10ppem -- gasp says no
            // gridfit, scaled advance 5.498, Windows 5, fitted phantom 6, 49 pixels over a line of
            // fifty -- looked like the same rule. It was head.flags bit 4 (Consolas' program may not
            // alter its advances, and CompatibleAdvance answers that face before it gets here), and
            // gating on gasp cost every OTHER face its hinted advances at the sizes it asks not to
            // be fitted: Times Bold, Arial Bold, Verdana Bold and Segoe UI Italic all drifted up to
            // seven pixels a line at 8ppem, where Windows draws the unfitted outline but spaces it
            // by what the program did. A glyph is DRAWN by its gasp and SPACED by its program.

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
                bool savedMeasuring = s_measuringAdvance;
                bool savedInterpMeasuring = TrueTypeInterpreter.MeasuringAdvance;
                TrueTypeInterpreter.BiLevelPass = true;
                TrueTypeInterpreter.MeasuringAdvance = true;
                s_measuringAdvance = true;      // the run below must not ask itself this question
                try
                {
                    GlyphProgram? glyph = HintedProgram(interpreter, glyphId, pixelsPerEm, 0);
                    if (glyph is not null)
                    {
                        int span = glyph.X[glyph.PointCount + 1] - glyph.X[glyph.PointCount];
                        // The emboldening pass moved pp2 a pixel; that pixel is the phase's to see
                        // (it is in pass one's span) but the layout adds FO_SIM_BOLD's own, so the
                        // advance is taken from the span without it.
                        int advSpan = GdiEmboldens && span > 64 ? span - 64 : span;
                        if (advSpan > 0) advance = MathF.Round(advSpan / 64f, MidpointRounding.AwayFromZero);
                        if (_hintedSpans.Count > HintedCacheLimit) { _hintedSpans.Clear(); _hintedSpanTouched.Clear(); }
                        _hintedSpans[key] = span;
                        _hintedSpanTouched[key] = interpreter.AdvancePhantomTouchedX;
                    }
                }
                finally
                {
                    TrueTypeInterpreter.BiLevelPass = savedBi;
                    TrueTypeInterpreter.MeasuringAdvance = savedInterpMeasuring;
                    s_measuringAdvance = savedMeasuring;
                }
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

            // THE KEY HAS TO NAME EVERY MODE THAT CHANGES THE FIT. SubpixelFitting is a static on
            // this class; BiLevelPass is a separate static on the INTERPRETER, and the compatible
            // advance is measured by a bi-level pass that runs BEFORE CompatibleAdvance64 is set and
            // with the phase disabled. Leaving BiLevelPass out lets that measurement's unphased
            // outline answer the ClearType render for the same (glyph, size).
            // WPF_HINTCACHE_BILEVEL=0 restores the old key.
            var key = (glyphId, (int)MathF.Round(pixelsPerEm * 16f) * 4 + (SubpixelFitting ? 1 : 0)
                       + (s_cacheByBiLevel && TrueTypeInterpreter.BiLevelPass ? 2 : 0));
            int callNo = 0;
            bool probe = s_outlineProbe && glyphId == s_probeGid;
            if (probe)
            {
                callNo = System.Threading.Interlocked.Increment(ref s_outlineCalls);
                Console.Error.WriteLine($"TGHO#{callNo} gid={glyphId} ppem={pixelsPerEm} sub={SubpixelFitting}"
                    + $" bilevel={TrueTypeInterpreter.BiLevelPass} ct={ClearTypeRendering}"
                    + $" cached={_hintedCache.ContainsKey(key)}");
            }
            if (Environment.GetEnvironmentVariable("WPF_NO_HINTCACHE") != "1"
                && _hintedCache.TryGetValue(key, out List<PathFigure>? cached))
            {
                figures = cached;
                if (probe) Console.Error.WriteLine($"TGHO#{callNo} -> CACHED x0={(cached.Count>0?(int)MathF.Round(cached[0].Start.X*64):-1)}");
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
            // ...and the face's PRE-PROGRAM says so too, by a different route: INSTCTRL selector 1
            // at the sizes it does not want fitted, which switches the glyph programs off in the
            // rasterizer. Verdana uses both tables and they agree; a face that uses only this one
            // must be read here or its glyphs are fitted where Windows leaves them alone.
            if (!FaceWantsGridFit(pixelsPerEm) || PrepInhibitsGridFit(pixelsPerEm))
            {
                // The outline as drawn, SCALED TO THIS SIZE -- not a refusal. Callers of this method
                // are promised a device-pixel outline and scale everything else by the reciprocal of
                // the device scale; answering "no fitting available" sends them to the unfitted
                // outline in the face's own base pixels, which they then scale as if it were device
                // pixels. That is a factor of ppem/48 in the wrong direction and it showed: the
                // finished ClearType ink went to eighteen times GDI's.
                if (s_unfittedPoints && glyphId >= 0 && glyphId < _numGlyphs)
                {
                    List<PathFigure> atSize = BuildGlyphFiguresAt(
                        glyphId, pixelsPerEm, ScaleRoundsHalfUp(_unitsPerEm, pixelsPerEm));
                    _hintedCache[key] = atSize;
                    figures = atSize;
                    return atSize.Count > 0;
                }
                if (!TryGetGlyphOutline(glyphId, out List<PathFigure> plain))
                    return false;
                double num = pixelsPerEm, den = PixelsPerEm;
                bool halfUp = ScaleRoundsHalfUp(_unitsPerEm, pixelsPerEm);
                // ...AND ROUNDED TO SIXTY-FOURTHS, because GDI's scaler works in 26.6 and its
                // unfitted outline is the design scaled and rounded to 1/64 (GGO's unhinted
                // points for Verdana 'k'@8 are exactly ours before rounding: a stem edge at 193
                // units is 0.7539px, which GDI holds as 48/64 = 0.75 -- exactly on a lamp sample,
                // which counts IN -- where our float left it 1/256 to the right and the sample
                // OUT. Every stem at 8ppem drew one level lighter than GDI's on that alone.
                // WPF_UNFITTED_ROUND=0 keeps the float coordinates.
                var scaled = new List<PathFigure>(plain.Count);
                foreach (PathFigure f in plain)
                {
                    var copy = new PathFigure(P64(f.Start, num, den, halfUp)) { Closed = f.Closed };
                    foreach (PathSegment seg in f.Segments)
                        switch (seg)
                        {
                            case LineSegment l:
                                copy.Segments.Add(new LineSegment(P64(l.Point, num, den, halfUp)));
                                break;
                            case QuadraticBezierSegment q:
                                copy.Segments.Add(new QuadraticBezierSegment(P64(q.Control, num, den, halfUp), P64(q.Point, num, den, halfUp)));
                                break;
                            case CubicBezierSegment c:
                                copy.Segments.Add(new CubicBezierSegment(P64(c.Control1, num, den, halfUp), P64(c.Control2, num, den, halfUp), P64(c.Point, num, den, halfUp)));
                                break;
                        }
                    scaled.Add(copy);
                }
                _hintedCache[key] = scaled;
                figures = scaled;
                if (probe) Console.Error.WriteLine($"TGHO#{callNo} -> SCALED x0={(scaled.Count>0?(int)MathF.Round(scaled[0].Start.X*64):-1)}");
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
                if (probe) Console.Error.WriteLine($"TGHO#{callNo} -> HINTED x0={(hinted.Count>0?(int)MathF.Round(hinted[0].Start.X*64):-1)}");
                return hinted.Count > 0;
            }

            if (!TryGetFittedOutline(glyphId, pixelsPerEm, out figures)) return false;
            _hintedCache[key] = figures;
            return figures.Count > 0;
        }

        /// <summary>THE GUARD IS OFF. GDI never throws a fit away, and the interpreter this guarded
        /// against no longer runs programs wrongly: the only fits it still rejected were correct
        /// ones. Times New Roman Bold 'E-acute' at 9ppem is fitted point for point as GDI fits it
        /// -- its accent lifted a pixel and a half above the raw outline, which is past two
        /// pixels of slack on the box -- and the guard swapped in the unfitted outline, a row low
        /// throughout. Off: Latin-1 over six faces x four styles x 8..24ppem 1,009,878 -> 794,367,
        /// punctuation 350,630 -> 271,037, the holdout unchanged at 0. WPF_FIT_SLACK=2 restores
        /// the old two-pixel guard.</summary>
        private static readonly float s_fitSlack =
            float.TryParse(Environment.GetEnvironmentVariable("WPF_FIT_SLACK"),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float fs) && fs > 0 ? fs : float.PositiveInfinity;

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

        /// <summary>WPF_CT_GASP_NOSYM=1: answer NO to symmetric smoothing for a face with no
        /// 'gasp', as we used to. See the fallback in GaspFlags.</summary>
        private static readonly bool s_gaspNoSymDefault =
            Environment.GetEnvironmentVariable("WPF_CT_GASP_NOSYM") == "1";

        private static readonly int s_v0SymFrom =
            int.TryParse(Environment.GetEnvironmentVariable("WPF_CT_V0SYM"), out int vs) ? vs : 0;

        private const int GaspGridfit = 0x0001;
        private const int GaspSymmetricGridfit = 0x0004;
        private const int GaspSymmetricSmoothing = 0x0008;

        /// <summary>Whether a VERSION 1 'gasp' clears SYMMETRIC_GRIDFIT at this size -- the face
        /// telling a ClearType rasterizer not to run its program. A version 0 table cannot say
        /// so, and GDI fits those faces (Arial Bold, Arial Italic, Times Bold) at every size.
        /// See <see cref="CompatibleAdvance"/>.</summary>
        private bool GaspDeclinesClearTypeGridFit(float pixelsPerEm)
        {
            if (_gasp < 0 || U16(_gasp) == 0) return false;
            int ppem = (int) MathF.Round(pixelsPerEm);
            int ranges = U16(_gasp + 2);
            int at = _gasp + 4;
            for (int i = 0; i < ranges; i++, at += 4)
                if (ppem <= U16(at))
                    return (U16(at + 2) & GaspSymmetricGridfit) == 0;
            return false;
        }

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
            // A FACE WITH NO 'gasp' SMOOTHS SYMMETRICALLY. fontdrvhost's vSetClearTypeState
            // falls back, for a face with no usable table, to "ppem > 20, or these three fields
            // are clear", and for a face that ships nothing the fields ARE clear -- so the answer
            // is yes at every size. We answered no, which is the harder kind of wrong: it only
            // shows on a face without the table, and the faces this suite measures all ship one.
            // <para>What it did show on was the PROBES, every one of which builds a font with no
            // gasp. CoverageAtACrossing compares GDI's raster with ours over 11,690 lamps of
            // upright, slanted and tapered bars, and 473 of them differed with our ink 1.0147x
            // GDI's -- all of it at the top row of each stroke, which is exactly what averaging a
            // run's first row against the empty one above it does. Forcing smoothing on
            // (WPF_SYM_ALWAYS=1) takes that to 112, and with the span-end rule corrected as well
            // the probe reaches ZERO differing lamps and an ink ratio of 1.0000. So a probe
            // without the table was measuring GDI in symmetric mode against us in the other, and
            // every reading it ever gave about vertical sampling was comparing two different
            // rasterizers.</para>
            // <para>Measured on the real specimen: EXACTLY NEUTRAL, holdout and every ratchet,
            // because all six faces ship a gasp. WPF_CT_GASP_NOSYM=1 restores the old
            // answer.</para>
            if (_gasp < 0)
                return want == GaspSymmetricSmoothing && !s_gaspNoSymDefault;
            // A VERSION 0 TABLE DOES NOT HAVE THE SYMMETRIC BITS. It defines GRIDFIT and
            // DOGRAY and nothing else, so reading bit 2 or 3 out of it reads a bit the face
            // never wrote and answers NO to a question it was never asked. The rasterizer has
            // to supply its own behaviour there, and GDI's is evidently yes: Times and Arial
            // ship version 1 for their romans and version 0 for every styled face, so this is
            // the difference between arial.ttf answering symmetric at 20ppem and arialbd.ttf
            // not -- and answering yes for the version 0 files is worth 62,829 at 18ppem and
            // 49,059 at 20 on the text specimen.
            // WPF_CT_V0SYM=<n>: a DIAGNOSTIC of vSetClearTypeState@14001d1d8's version-0 branch,
            // which is not unconditional. A v0 table falls into the same branch as no table,
            // and there `ppem < 21 || !(flags & 0x10000000)` sends it through a check that
            // RETURNS WITHOUT 0x20000000 if any of three fields of the face's table-info
            // record (+0xa0, +0xa8, +0xb0) is nonzero, or the face is legacy East Asian. So
            // below 21 a v0 face smooths symmetrically only if those fields are clear. This
            // knob asks what happens if they are not: smoothing only at ppem >= n.
            // REFUTED, emphatically: WPF_CT_V0SYM=21 takes the holdout 12,585 -> 2,997,000. The
            // fields are clear for Times Bold, Times Italic and Arial Italic and GDI smooths them
            // symmetrically at every size, as the line below already says.
            if (want == GaspSymmetricSmoothing && U16(_gasp) == 0)
                return s_v0SymFrom <= 0 || (int) MathF.Round(pixelsPerEm) >= s_v0SymFrom;
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
        public bool WantsGridFit(float pixelsPerEm) => FaceWantsGridFit(pixelsPerEm);

        /// <summary>GDI'S SCAN TYPE IS PER GLYPH. fsg_ExecuteGlyph@14002e8c8 takes it from the
        /// graphics state the glyph's OWN program leaves behind -- fsg_DoScanControl(SCANCTRL, ppem)
        /// ? SCANTYPE : 2 (none) -- not from prep's: Palatino Linotype '6' runs SCANTYPE 4
        /// (smart, stubs NOT excluded) over prep's 5, and GDI fills a dropout there that the stub
        /// test would refuse. A composite merges its elements as each finishes, the first taken
        /// as it is and the rest as (child &amp; 3 | 4) &amp; parent, and its own program -- when
        /// it runs -- overwrites the result. Returns the renderer's DropoutForRun value (scan type
        /// + 1, or 0 for none), or -1 when nothing was recorded.</summary>
        internal int GlyphDropout(int gid, float pixelsPerEm)
            => s_perGlyphScan && _glyphScan.TryGetValue((gid, (int) MathF.Round(pixelsPerEm * 16f)), out int t)
               ? ((t & 2) != 0 ? 0 : t + 1) : -1;

        private static readonly bool s_perGlyphScan =
            Environment.GetEnvironmentVariable("WPF_CT_SCAN_PERGLYPH") != "0";

        private readonly Dictionary<(int, int), int> _glyphScan = new();
        [ThreadStatic] private static int[]? s_scanAcc;

        private void RecordScanType(TrueTypeInterpreter interpreter, GlyphProgram glyph, int gid,
                                    float pixelsPerEm, int depth)
        {
            int ctrl = glyph.ScanControl >= 0 ? glyph.ScanControl : interpreter.PrepScanControl;
            int type = glyph.ScanType >= 0 ? glyph.ScanType : interpreter.PrepScanType;
            int own = DoScanControl(ctrl, (int) MathF.Round(pixelsPerEm)) ? type : 2;
            s_scanAcc ??= new int[8];
            if (depth < s_scanAcc.Length - 1)
            {
                // The parent's accumulator starts empty (0xffff) for each composite.
                int acc = glyph.Composite ? s_scanAcc[depth + 1] : -1;
                int mine;
                if (s_scanChangedOnly)
                {
                    // AN ELEMENT ONLY SAYS SOMETHING IF ITS PROGRAM CHANGED THE SCAN STATE.
                    // itrp_ExecuteGlyphPgm@140037320 reports `gs+0x84 != gs+0x44` -- the glyph's
                    // scan control and type, as one 32-bit pair, against the defaults it started
                    // from -- and fsg_ExecuteGlyph writes the element's own value
                    // (DoScanControl ? SCANTYPE : 2) only when that is true. Otherwise the element
                    // keeps 0xffff, or for a composite whatever its children merged into it, and
                    // 0xffff is neutral in the merge. Palatino 'adieresis' at 20ppem: 'a' sets
                    // SCANTYPE 4, the dieresis and the composite's own program touch nothing, so GDI
                    // drops out with 4 (no stub test) and fills the two zero-width columns at the
                    // dots' vertical tangents, where the composite's unchanged 5 refused them.
                    // WPF_CT_SCAN_CHANGED=0 restores the old reading.
                    bool changed = glyph.ScanControl >= 0
                                   && (glyph.ScanControl != interpreter.PrepScanControl
                                       || glyph.ScanType != interpreter.PrepScanType);
                    mine = changed ? own : glyph.Composite ? acc : -1;
                }
                else
                    mine = glyph.Composite ? (glyph.ScanControl >= 0 ? own : (acc >= 0 ? acc : own)) : own;
                if (depth > 0)
                {
                    int p = s_scanAcc[depth];
                    if (mine >= 0) s_scanAcc[depth] = p < 0 ? mine : ((mine & 3) | 4) & p;
                }
                if (depth == 0)
                {
                    if (_glyphScan.Count > HintedCacheLimit) _glyphScan.Clear();
                    // Nothing in the tree changed it: the size's own, as the pre-program left it.
                    if (mine < 0)
                        mine = DoScanControl(interpreter.PrepScanControl, (int) MathF.Round(pixelsPerEm))
                               ? interpreter.PrepScanType : 2;
                    _glyphScan[(gid, (int) MathF.Round(pixelsPerEm * 16f))] = mine;
                }
            }
        }

        private static readonly bool s_scanChangedOnly =
            Environment.GetEnvironmentVariable("WPF_CT_SCAN_CHANGED") != "0";

        private static void ResetScanAcc(int depth)
        {
            s_scanAcc ??= new int[8];
            if (depth + 1 < s_scanAcc.Length) s_scanAcc[depth + 1] = -1;
        }

        /// <summary>fsg_DoScanControl@14002e5d0.</summary>
        private static bool DoScanControl(int ctrl, int ppem)
            => ((ctrl & 0x100) != 0 && ((ctrl & 0xFF) == 0xFF || ppem <= (ctrl & 0xFF)));

        public bool WantsDropoutControl(float pixelsPerEm, out int scanType)
        {
            scanType = 0;
            TrueTypeInterpreter? interpreter = Interpreter();
            if (interpreter is null || !interpreter.PrepareForSize(pixelsPerEm)) return false;
            int ctrl = interpreter.PrepScanControl;
            scanType = interpreter.PrepScanType;
            // SCANCTRL: bits 0-7 threshold ppem (0xFF = every size), bit 8 = dropout control
            // ON when ppem <= threshold, bit 11 = OFF when ppem > threshold. The rotation and
            // stretch conditions (bits 9/10/12/13) do not arise here.
            int threshold = ctrl & 0xFF;
            int ppem = (int) MathF.Round(pixelsPerEm);
            bool on = (ctrl & 0x100) != 0 && (threshold == 0xFF || ppem <= threshold);
            if ((ctrl & 0x800) != 0 && threshold != 0xFF && ppem > threshold) on = false;
            // SCANTYPE 2 and 3 mean no dropout control at all; 0/1 simple, 4/5 smart.
            if (s_dropoutTrace)
                Console.Error.WriteLine($"DROPOUT upem={_unitsPerEm} ppem={ppem} SCANCTRL=0x{ctrl:X} SCANTYPE={scanType} on={on}");
            return on && scanType is 0 or 1 or 4 or 5;
        }

        private static readonly bool s_dropoutTrace = Environment.GetEnvironmentVariable("WPF_DROPOUT_TRACE") == "1";

        /// <summary>WPF_GASP_FIT=always grid-fits whatever the face's gasp says.
        /// <para>For asking an empirical question the oracles cannot answer. Consolas' gasp clears
        /// GRIDFIT for ppem 10 and below, so we do not fit it there -- and GDI's GetGlyphOutline
        /// fits it anyway, because GGO hints the outline regardless of gasp, which is a fact about
        /// the ORACLE and not about what the renderer does. Consolas is also the worst face in the
        /// pixel comparison at those sizes, so the question is worth settling by rendering.</para>
        /// </summary>
        private static readonly bool s_alwaysFit =
            System.Environment.GetEnvironmentVariable("WPF_GASP_FIT") == "always";

        /// <summary>Whether the face's own pre-program, run for this size, inhibits grid-fitting
        /// (INSTCTRL selector 1). See <see cref="TrueTypeInterpreter.GridFitInhibited"/>.</summary>
        /// <summary>WPF_UNFITTED_ROUND=0: leave the unfitted outline in float instead of 26.6.</summary>
        private static readonly int s_unfittedRoundMode =
            Environment.GetEnvironmentVariable("WPF_UNFITTED_ROUND") switch
            { "0" => 0, "away" => 1, "down" => 3, _ => 2 };

        /// <summary>A base-pixel point scaled by <paramref name="k"/> and held to 26.6, as the
        /// scaler holds every coordinate before anything is drawn from it.</summary>
        /// <summary>A HALF GOES UP, NOT AWAY FROM ZERO, and it is scl_Scale that says so.
        /// <para>GDI's point scaler is `(units * mult + half) &gt;&gt; shift` -- an ARITHMETIC
        /// shift, so the divide floors and the added half makes it round toward +infinity. Away
        /// from zero is the same thing for a positive coordinate and the opposite for a negative
        /// one, and exact halves are not rare: at 8ppem in a 2048-unit em a coordinate lands on
        /// one whenever its design value is 2 mod 4, which is a quarter of them.</para>
        /// <para>AND THE Y AXIS IS ALREADY FLIPPED HERE. TryGetGlyphOutline negates y on the way
        /// out, so this array is y-down while GDI rounds y-up; rounding toward +infinity in GDI's
        /// space is rounding toward -infinity in this one. Getting that backwards would put every
        /// tied y a sixty-fourth out in exchange for fixing x.</para>
        /// <para>WPF_UNFITTED_ROUND=away restores the old symmetric rule, =0 keeps the float,
        /// =down rounds y the wrong way on purpose. Over ppem 8-10 the four measure 16,681 (this),
        /// 18,007 (away), 50,796 (no rounding at all, which is what leaving it to the scan walk's
        /// own 1/384 grid would mean, so GDI really does hold the unfitted outline at 26.6 before
        /// it scans) and 35,735 (y the wrong way). Holdout 47,402 -&gt; 46,605.</para></summary>
        /// <summary>MULTIPLY THEN DIVIDE, and in double, because the tie has to be exact to be
        /// rounded correctly. Forming `k = ppem / 48` first rounds 1/6 to a float and every
        /// coordinate then lands a hair off the value GDI computes in one fixed-point multiply --
        /// so a design coordinate that is exactly half a sixty-fourth at this size, which is a
        /// quarter of them in a 2048-unit em at 8ppem, rounds by whichever side the float noise
        /// fell on rather than by the rule. Multiplying by ppem and dividing by 48 leaves the
        /// quotient exactly representable and IEEE division returns it exactly.</summary>
        private static Vector2 P64(Vector2 v, double num, double den, bool halfUp)
        {
            float x = (float) (v.X * num / den), y = (float) (v.Y * num / den);
            if (s_unfittedRoundMode == 0) return new Vector2(x, y);
            if (s_unfittedRoundMode == 1 || !halfUp)
                return new Vector2(MathF.Round(x * 64f, MidpointRounding.AwayFromZero) / 64f,
                                   MathF.Round(y * 64f, MidpointRounding.AwayFromZero) / 64f);
            if (s_unfittedRoundMode == 3)
                return new Vector2(MathF.Floor(x * 64f + 0.5f) / 64f,
                                   MathF.Floor(y * 64f + 0.5f) / 64f);
            return new Vector2(MathF.Floor(x * 64f + 0.5f) / 64f,
                               -MathF.Floor(-y * 64f + 0.5f) / 64f);
        }

        /// <summary>WHICH OF GDI'S TWO SCALE ROUNDINGS THIS SIZE GETS, and it is not a choice --
        /// scl_InitializeScaling reduces the ratio and then looks at the DENOMINATOR:
        /// <code>
        ///   if ((den - 1 &amp; den) == 0 &amp;&amp; den != 0)  ->  scl_FRound
        ///        (mult * v + (den &gt;&gt; 1)) &gt;&gt; log2(den)     an arithmetic shift: half toward +inf
        ///   else                                    ->  scl_SRound
        ///        v &lt; 0 ? -(((den&gt;&gt;1) - mult*v) / den)        half AWAY FROM ZERO
        ///              :  ((den&gt;&gt;1) + mult*v) / den
        /// </code>
        /// Every face in the specimen has a 2048-unit em, so every one of them takes the shift and
        /// rounds toward +infinity; a 1000-unit em takes the symmetric one at most sizes. Getting
        /// this from the em rather than assuming one rule is why it is written out here.</summary>
        private static bool ScaleRoundsHalfUp(int unitsPerEm, float pixelsPerEm)
        {
            // REDUCED BY THE COMMON POWER OF TWO, not by the gcd: scl_InitializeScaling does
            // `while (((num | den) & 1) == 0) { num >>= 1; den >>= 1; }` and tests what is left.
            // The two agree on every power-of-two em, which is every face here, and the shift is
            // what the binary actually does.
            int p = (int) MathF.Round(pixelsPerEm);
            if (p <= 0 || unitsPerEm <= 0) return true;
            int num = p, den = unitsPerEm;
            while (((num | den) & 1) == 0) { num >>= 1; den >>= 1; }
            return den != 0 && (den & (den - 1)) == 0;
        }

        private bool PrepInhibitsGridFit(float pixelsPerEm)
        {
            TrueTypeInterpreter? interpreter = Interpreter();
            return interpreter is not null && interpreter.PrepareForSize(pixelsPerEm) && interpreter.GridFitInhibited;
        }

        private bool FaceWantsGridFit(float pixelsPerEm)
        {
            if (_gasp < 0 || s_alwaysFit) return true;

            // A VERSION 0 'gasp' IS NOT CONSULTED FOR CLEARTYPE. It only has the two bi-level bits
            // (GRIDFIT, DOGRAY), and GDI's ClearType rasterizer fits a version 0 face at every
            // size whatever its GRIDFIT bit says -- Arial Bold, Arial Italic and Times Bold all
            // clear it at 8ppem and below, and Windows' 8ppem 'w' in Arial Bold is 5 pixels wide
            // where the linear advance is 6.22: that width comes out of the program. Their
            // advances (CompatibleAdvance) already went that way; drawing them fitted too took
            // the text specimen from 6,309,064 to 5,847,329 -- Arial B 8 206,036 -> 33,088,
            // Arial I 8 171,215 -> 31,037, Times B 8 185,165 -> 37,703, everything else unmoved.
            // Version 1 tables carry SYMMETRIC_GRIDFIT, and GaspDeclinesClearTypeGridFit reads
            // that; GRIDFIT below is what the bi-level rasterizer would read.
            if (U16(_gasp) == 0) return true;

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
        /// <summary>A whole-run x offset in 64ths, for asking whether our glyphs sit where Windows'
        /// do. Swept 2026-08-30 on the six-face specimen: -4/64 2,950,670; -2/64 2,502,263; ZERO
        /// 2,197,658; +2/64 2,389,737; +4/64 2,697,720; +6/64 3,111,741. A clean minimum at zero,
        /// so our placement is right ON AVERAGE and what differs is per-glyph, not a shift.</summary>
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
        /// left side bearing (measured: no effect), 0 does neither. WPF_CT_COMPATWIDTH.</para>
        /// <para>WHAT IT IS ACTUALLY DOING, 2026-09-04, now that GDI's ClearType outline can be
        /// SOLVED FOR rather than guessed at (SolveGdisStemGeometry). It SQUEEZES THE GLYPH. Arial
        /// 'I' at 16ppem: the program computes a stem of 1.438 and this scaling delivers 1.078,
        /// where GDI draws 1.453. Turn it off and our widths track GDI's within six hundredths at
        /// every size tried:</para>
        /// <code>
        ///              with squeeze     without        GDI
        ///   ppem 12    1.063            1.063          1.125
        ///   ppem 13    0.891            1.188          1.125-1.203
        ///   ppem 16    1.078            1.438          1.453-1.500
        /// </code>
        /// <para>And yet turning it off costs the specimen 2,280,798 -> 2,978,594 at 12ppem. Both
        /// are true because it is COMPENSATING, not correcting: our glyph's ink sits too far RIGHT
        /// inside its advance box -- at 16ppem our left edge is 1.438 against GDI's 0.625 -- and
        /// squeezing the glyph drags it left. It buys a better position by paying in stem width,
        /// and on an aggregate that trade wins.</para>
        /// <para>So this stays ON, and it is not the fix. The fix is the POSITION: get the ink
        /// where GDI puts it inside the box and this can go, because compatible widths is a
        /// statement about the ADVANCE -- "glyphs adjusted post hinting in order to return advance
        /// widths exactly the same as bi-level" -- and an advance can be honoured without
        /// distorting the outline that sits inside it. Note the pen itself is NOT the problem:
        /// spaced glyphs land on GDI's sub-pixel centres exactly (WhereEachGlyphLands), so the
        /// advances are right and it is the ink within the box that is displaced.</para></summary>
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

        /// <summary>Widest link (64ths) mode 10 treats as a STEM to snap to whole pixels; wider is
        /// spacing. WPF_CT_STEMMAX.</summary>
        private static readonly int s_stemMax =
            int.TryParse(Environment.GetEnvironmentVariable("WPF_CT_STEMMAX"), out int sm10) ? sm10 : 192;

        private static readonly int CompatibleWidthTolerance =
            int.TryParse(Environment.GetEnvironmentVariable("WPF_CT_CWTOL"), out int ct2) ? ct2 : 25;

        /// <summary>WPF_CT_COMPATWIDTH_COMPOSITE: apply the compatible-width correction to
        /// composite glyphs too, which the spec does not exempt.</summary>
        private static readonly bool s_compatWidthComposite =
            Environment.GetEnvironmentVariable("WPF_CT_COMPATWIDTH_COMPOSITE") == "1";

        /// <summary>How wide a gap in x separates one feature from the next, in 64ths, for the
        /// piecewise displacement of mode 6. WPF_CT_STEMGAP.</summary>
        /// <summary>WPF_CT_CW_TRACE=1 prints the fitted advance, the target and their ratio.</summary>
        /// <summary>How the ONE-FEATURE case of CompatibleWidthMode 7 is realized. See the note
        /// at the use site: 0 = rigid slide (shipped), 1 = hold centre and scale, 2 = damped.</summary>
        private static readonly int s_cw1Mode =
            int.TryParse(Environment.GetEnvironmentVariable("WPF_CT_CW1"), out int c1) ? c1 : 0;
        /// <summary>A one-feature glyph is displaced RIGIDLY by (s-1)*(centre-p0). Above this
        /// slide (64ths), and for a glyph at least <see cref="s_minFeatInk"/> wide, the letter is
        /// left where the program put it and the side bearings take up the advance.
        /// <para>DEFAULT OFF, because it does NOT generalise. On the specimen's five sizes
        /// (8/10/12/16/24) it is worth 5,485,079 -> 5,433,971, and on the edge oracle Verdana
        /// 'w'@12 goes 17,510 -> 1,275 while still solving exactly. But swept over EVERY size from
        /// 8 to 24 it is a wash or worse: 19,753,494 off against 19,757,566 at 36/64, because the
        /// three sizes it helps (10, 12, 14 -- two of them specimen sizes, which is why it looked
        /// like a win) are paid for by the four it hurts (9 +32,325, 13 +15,943, 17 +51,835,
        /// 18 +255). No threshold beats off over the full range: 28 -> 19,857,658,
        /// 34 -> 19,771,482, 44 -> 19,853,597, and 56 never fires. Only 11 of 306 rows move at
        /// all, so this is a size-specific redistribution rather than a correction.
        /// WPF_CT_MINFEAT_SHIFT / _INK to re-enable.</para></summary>
        private static readonly int s_cwDemean =
            int.TryParse(Environment.GetEnvironmentVariable("WPF_CT_CW_DEMEAN"), out int cwm) ? cwm : 0;
        private static readonly int s_cwSpread =
            int.TryParse(Environment.GetEnvironmentVariable("WPF_CT_CW_SPREAD"), out int cws) ? cws : 1000;
        private static readonly int s_cwDamp =
            int.TryParse(Environment.GetEnvironmentVariable("WPF_CT_CW_DAMP"), out int cwd) ? cwd : 1000;
        private static readonly int s_minFeatS =
            int.TryParse(Environment.GetEnvironmentVariable("WPF_CT_MINFEAT_S"), out int mfS) ? mfS : 0;
        private static readonly int s_minFeatInk =
            int.TryParse(Environment.GetEnvironmentVariable("WPF_CT_MINFEAT_INK"), out int mfi) ? mfi : 256;   // width gate, only used when _SHIFT is on
        private static readonly int s_minFeatShift =
            int.TryParse(Environment.GetEnvironmentVariable("WPF_CT_MINFEAT_SHIFT"), out int mfs) ? mfs : 0;
        private static readonly int s_cw1MinInk =
            int.TryParse(Environment.GetEnvironmentVariable("WPF_CT_CW1_MININK"), out int c1i) ? c1i : 256;
        private static readonly int s_cw1Damp =
            int.TryParse(Environment.GetEnvironmentVariable("WPF_CT_CW1_DAMP"), out int c1d) ? c1d : 1000;

        private static readonly bool s_cwTrace =
            Environment.GetEnvironmentVariable("WPF_CT_CW_TRACE") == "1";

        private static readonly int StemGap64 =
            int.TryParse(Environment.GetEnvironmentVariable("WPF_CT_STEMGAP"), out int sg) && sg > 0
                ? sg : 48;

        /// <summary>WPF_CT_COMPATWIDTH: how a ClearType-fitted outline is brought to the advance
        /// the glyph is laid out at (the bi-level one, via hdmx). 0 not at all; 1 the tolerance-
        /// gated x scale that shipped first; 7 (the default) the per-feature rigid move described
        /// at its branch below; 2-6 and 8 are experiments kept expressible, each documented where
        /// it runs.</summary>
        private static readonly int s_minFeatures =
            int.TryParse(Environment.GetEnvironmentVariable("WPF_CT_MINFEAT"), out int mf) ? mf : 0;
        private static readonly int s_minFeatSqueezeOnly =
            int.TryParse(Environment.GetEnvironmentVariable("WPF_CT_MINFEAT_SQUEEZE"), out int ms) ? ms : 0;

        /// <summary>Which glyph WPF_OUTLINE_DUMP writes; -1 (the default) means the first one asked
        /// for, which is what a single-glyph probe wants.</summary>
        private static readonly int s_outlineDumpGid =
            int.TryParse(Environment.GetEnvironmentVariable("WPF_OUTLINE_DUMP_GID"), out int og) ? og : -1;

        /// <summary>WPF_CT_PHASE=0 puts the hand-tuned compatible-width correction back.
        /// <para>GDI does not scale a fitted outline onto its advance the way mode 7 does. It
        /// runs a PHASE pass -- ExecutePhaseControl, ported in TrueTypeInterpreter -- over the
        /// tree of which point was placed from which, and that is now what we do too. The two
        /// mechanisms do the same job, so they must not both run: mode 7 on top of the phase
        /// stretches an already-stretched glyph and measures twice as bad as neither.</para>
        /// <para>Measured against mode 7, over the weight report: 5,485,079 -> 4,813,476 on the
        /// five-size specimen and 19,753,494 -> 17,331,018 on the 306-row 8..24 holdout, so it
        /// is a 12% gain that GENERALISES rather than a fit to the specimen. On the per-glyph
        /// ratchets 264 cases improved against 69 that got worse, and not one of the 69 covers
        /// a different PIXEL from Windows -- every one is "0 pixels covered differently", a
        /// handful of pixels out by about one ClearType quantization level.</para>
        /// <para>WPF_CT_PHASE=0 restores MODE 7, not the exact number that shipped before the
        /// phase. It was bit-identical at 5,485,079 to begin with, and drifts as later rules
        /// that have nothing to do with the phase land unconditionally -- itrp_DeltaEngine's
        /// freedom test was the first, taking it to 5,481,176 (an improvement: the fix is right
        /// either way). Rules that exist only to compensate for one mechanism or the other ARE
        /// gated on this flag; rules that are simply correct are not.</para></summary>
        internal static readonly bool UseGdiPhase = Environment.GetEnvironmentVariable("WPF_CT_PHASE") != "0";

        internal static readonly int CompatibleWidthMode =
            int.TryParse(Environment.GetEnvironmentVariable("WPF_CT_COMPATWIDTH"), out int cw) ? cw
            : Environment.GetEnvironmentVariable("WPF_CT_PHASE") != "0" ? 0 : 7;

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
            // TWO PIXELS PLUS WHATEVER THE COMPATIBLE WIDTH LEGITIMATELY MOVES THE GLYPH.
            // The flat two was rejecting correct fits. Arial Italic 'w' at 16ppem has a linear
            // advance of 11.555 against an hdmx of 9, so the compatible-width phase compresses its
            // right edge by about 2.6px -- past the old limit -- and the WHOLE FIT was discarded,
            // falling back to the unfitted outline. That is why the phase looked like a no-op on
            // that glyph: with it on and off the fit was rejected either way and the same
            // uncompressed outline rendered. Widening the allowance by the correction itself keeps
            // the tight guard on every glyph that has no correction to justify the movement, and
            // it is what the collapses this exists to catch still trip over: measured on the
            // specimen, a FLAT slack of 3, 4, 8 and even 100 all give the identical 1,439,591, so
            // nothing in the corpus is rejected between 3px and 100px -- the guard's only live
            // rejections were these legitimate compressions.
            //     specimen 1,539,711 -> 1,439,591      holdout 8..24 4,772,773 -> 4,615,390
            // WPF_FIT_SLACK overrides the base allowance.
            float Slack = s_fitSlack;
            if (glyphId >= 0 && glyphId < _numGlyphs && !s_measuringAdvance)
            {
                float linAdv = Advance(glyphId) * pixelsPerEm / PixelsPerEm;
                float compatAdv = CompatibleAdvance(glyphId, pixelsPerEm,
                                                    (int) MathF.Round(pixelsPerEm));
                if (linAdv > 0f && compatAdv > 0f) Slack += MathF.Abs(compatAdv - linAdv);
            }
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
                        // NOT ROUNDED HERE. This outline is in BASE pixels and something else
                        // rescales it to the size being drawn, so putting the sheared x on a
                        // sixty-fourth of a BASE pixel is a rounding at the wrong scale that the
                        // real one then has to round again. Two roundings are not one: where the
                        // true coordinate is an exact half at the drawing size -- a quarter of
                        // them in a 2048-unit em at 8ppem -- the first can nudge it off the tie
                        // and send the second the wrong way. The fitted path at the other call
                        // site IS at the drawing size and does round.
                        // MEASURED EXACTLY NEUTRAL on this specimen -- 16,681 over ppem 8-10
                        // either way -- because the base em is 48 pixels and every size drawn
                        // here is smaller, so the first rounding is finer than the second and
                        // almost never moves a coordinate across its tie. It would stop being
                        // neutral above 48ppem, where the base grid is the coarser of the two.
                        // WPF_OBLIQUE_ROUND_BASE=1 rounds here as well, as it used to.
                        pts[i] = new Vector2(Sheared(pts[i], s_shearRoundBase), -pts[i].Y);   // y-down
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

            interpreter.ResetNudgeWatch();
            GlyphProgram? glyph = HintedProgram(interpreter, glyphId, pixelsPerEm, 0);
            if (glyph is null) return null;

            // A SECOND PASS, when the first one saw the face nudge two diagonal points against each
            // other. That pair is a width decision for the bi-level grid and ClearType does not take
            // it -- see the SHPIX site -- and the only way to not take it is to run the program
            // again without it, since by the time the second nudge arrives the first has already
            // been measured from and interpolated through.
            if (interpreter.SawOpposingDiagonalNudges)
            {
                interpreter.RefusingDiagonalNudges = true;
                try { glyph = HintedProgram(interpreter, glyphId, pixelsPerEm, 0) ?? glyph; }
                finally { interpreter.RefusingDiagonalNudges = false; }
            }

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
            // just put them on. (GdiEmbolden already did it, in the program's own 26.6.)
            if (_emboldenStrength > 0f && !GdiEmboldens)
                Embolden(working, _emboldenStrength * pixelsPerEm / BaseEmPixels);
            foreach ((Vector2[] pts, _) in working)
                for (int i = 0; i < pts.Length; i++)
                    // Plus, for the same reason: these are the HINTED points, still y-up.
                    pts[i] = new Vector2(Sheared(pts[i], true), -pts[i].Y);

            var built = new List<PathFigure>(working.Count);
            foreach ((Vector2[] pts, bool[] on) in working)
                built.Add(BuildContourFigure(pts, on));
            return built;
        }

        /// <summary>The simulated italic's x for a hinted point (y up, pixels): x + shear * y,
        /// ROUNDED TO A SIXTY-FOURTH the way the rasterizer's 26.6 arithmetic leaves it. With the
        /// shear 87/256 that rounding is a genuine tie at every y that is 2 mod 4 sixty-fourths,
        /// and GDI takes the upper value each time ('l' at 250ppem: 4132.5 -> 4133). Without it the
        /// float shear lands half a sixty-fourth short of GDI on every such point.</summary>
        /// <summary>WPF_OBLIQUE_ROUND=0: do not put the sheared x back on the sixty-fourth grid.
        /// <para>The rounding is right for a HINTED outline, where GDI shears the fitted points in
        /// its own 26.6 arithmetic -- that is what the GGO pairing at 96 and 250ppem proved. Below
        /// the face's gasp gridfit threshold there is no fitted outline to shear, so GDI can carry
        /// the slant in the scaling matrix and never see a 26.6 grid at all, and half a
        /// sixty-fourth is a whole sub-sample at 8ppem. This is here to measure which.</para>
        /// <para>REFUTED: Tahoma Italic over ppem 8-10 measures 4,490 rounded against 7,541
        /// unrounded, so GDI puts the sheared x on the sixty-fourth at the small sizes too. Asked
        /// because the free solver, run on the four worst Tahoma Italic glyphs at 8ppem, wants
        /// moves of one to three 128ths that vary LINEARLY with y -- 'W' wants +3 at the baseline
        /// and -3 at cap height -- which is the signature of a shear that disagrees, and the
        /// rounding was the only part of ours that could disagree by that little. It is not the
        /// rounding, and the shear itself is pinned to 87/256 by GGO at 96 and 250ppem, so
        /// whatever that linear-in-y residual is, it is not the slant.</para>
        /// </summary>
        private static readonly bool s_shearRound =
            Environment.GetEnvironmentVariable("WPF_OBLIQUE_ROUND") != "0";

        private static readonly bool s_shearRoundBase =
            Environment.GetEnvironmentVariable("WPF_OBLIQUE_ROUND_BASE") == "1";

        private float Sheared(Vector2 p, bool round)
            => _shear == 0f ? p.X
             : !round || !s_shearRound ? p.X + _shear * p.Y
             // AWAY FROM ZERO, AND IT IS NOT THE SAME RULE AS THE POINT SCALER'S. scl_Scale
             // rounds an exact half toward +infinity, and the obvious tidy-up is to make this
             // agree -- but it is measurably worse where it can be told apart: over ppem 11, 16
             // and 17, where the faces are fitted and this is the shear applied to the fitted
             // points, toward +infinity measures 10,617 against 10,088. At 8-10ppem the two are
             // identical (16,681), because there the shear runs on the unfitted outline in BASE
             // pixels and nothing lands on a tie. So the simulated italic's shear is symmetric
             // about zero and the scaler's is not; WPF_OBLIQUE_ROUND=up to re-measure.
             // TWO ROUNDINGS, NOT ONE, for the same reason the unfitted path takes them: a
             // transform's products are rounded separately by scl_Scale, so the slant is
             // `round(x) + round(shear*y)`. Here x is already a whole sixty-fourth -- it came out
             // of the interpreter -- so only the shear term is rounded, where we had been rounding
             // the SUM. WPF_OBLIQUE_MATRIX=0 rounds the sum.
             : s_obliqueMatrix
                 ? p.X + (s_shearRoundUp
                     ? MathF.Floor(_shear * p.Y * 64f + 0.5f) / 64f
                     : MathF.Round(_shear * p.Y * 64f, MidpointRounding.AwayFromZero) / 64f)
             : s_shearRoundUp
                 ? MathF.Floor((p.X + _shear * p.Y) * 64f + 0.5f) / 64f
                 : MathF.Round((p.X + _shear * p.Y) * 64f, MidpointRounding.AwayFromZero) / 64f;

        private static readonly bool s_shearRoundUp =
            Environment.GetEnvironmentVariable("WPF_OBLIQUE_ROUND") == "up";

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
        /// <summary>How many columns the ClearType scan converter puts in one pixel: GDI's
        /// globals[0x49a], six, which is three lamps of two samples each.</summary>
        internal const int ClearTypeOversample = 6;

        internal static bool SubpixelFitting { get; set; }

        /// <summary>WPF_CT_COMPOFF=1 rounds a component offset on the LAMP grid in x, as
        /// scl_CalcComponentOffset does. It is GDI's rule and it measures net positive on its
        /// own -- 78 ratchets improve against 34 that regress -- but 34 regressions is not
        /// nothing, so it waits with the rest of the phase work rather than dirtying a default
        /// that is currently clean.</summary>
        private static readonly bool s_ctComponentOffset =
            Environment.GetEnvironmentVariable("WPF_CT_COMPOFF") is { } co ? co == "1"
            : Environment.GetEnvironmentVariable("WPF_CT_PHASE") != "0";

        /// <summary>Fit Y ONLY, whatever XHintMode says -- the stage tests' third measurement.
        /// <para>Not a rendering knob: nothing but GdiStageTests sets it, and it exists because
        /// the XHintMode gate on the plainX capture had silently retired that stage.</para>
        /// </summary>
        internal static bool ForceYOnlyFit { get; set; }

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

        private static readonly bool s_fitBiLevel =
            Environment.GetEnvironmentVariable("WPF_CT_FIT_BILEVEL") == "1";

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
        /// <para>Shipped as 5 on the evidence of the text specimen -- rows of plain Labels drawn by
        /// both stacks, which is nothing but text and so cannot be confused by control chrome:
        /// 1,536,150 with the fitting discarded against 1,410,303 with it kept. That note used to
        /// end "the live CONTROL window still prefers discarding it"; it does not any more, and had
        /// not for some time. Discarding now costs 1,698,126 against 1,144,325 for keeping.</para>
        /// <para>SIX, NOT FIVE. Nobody had swept the modes against the live window since the
        /// ClearType-direction rules went in -- each was measured once, against whatever the
        /// objective was that day, and 5 was left in place. Swept now, on the window:</para>
        /// <para>6: 985,196   13: 988,410   8: 992,404   7: 993,858   5: 1,144,325   14: 1,158,009
        /// 16: 1,236,974   12: 1,551,638   11: 1,613,692   0: 1,698,126.</para>
        /// <para>Mode 6 is 159,129 better than what shipped, and it improves nearly every region --
        /// ListView by 38%, GroupBox by 33%, RichTextBox by 32%, LinkLabel by 44% -- against three
        /// that get worse (MonthCalendar, TextBoxes, DateTimePicker). The corroboration that it is
        /// RIGHT rather than merely better-scoring is the ink ratio, which is the weight half of the
        /// difference and therefore most of it: mode 5 leaves the regions near 0.99 and mode 6 puts
        /// them on 1.000 -- Label 1.0006, Panel 0.9999, ListBox 1.0004, StatusStrip 0.9996. Rounding
        /// on the lamp grid is what a ClearType rasterizer does, and the weight it produces is
        /// Windows'.</para>
        /// <para>The isolated-glyph parity metric DISAGREES -- 54,934 for mode 5 against 80,756 for
        /// mode 6 -- and it is the proxy, not the objective. It renders one glyph through GDI
        /// directly; the window is WinForms drawing through WinForms. Where the two disagree the
        /// window is what was asked for.</para>
        /// <para>ANSWERED, AND NEITHER HARNESS WAS WRONG. They measure different populations.
        /// Every candidate was eliminated first: the two oracles are byte-identical (TextRenderer
        /// against ExtTextOutW at CLEARTYPE_QUALITY is SUM|d| 0, drawn into the same DIB); the
        /// paper does not matter; both harnesses render through the same RenderToRgba under
        /// identical flags (WPF_FLAG_TRACE prints one line per process and they match field for
        /// field); and the live app rasterizes at scale 1, Segoe UI 9pt, which is ppem 12.</para>
        /// <para>Split the suite by case and mode 6 wins at regular@12 and NOWHERE ELSE:</para>
        /// <para>regular@11 92,701 -> 111,739;  regular@12 96,586 -> 89,997;  regular@13 108,156
        /// -> 128,003;  regular@16 51,691 -> 94,987;  regular@20 173,117 -> 210,261;  b@12 51,022
        /// -> 87,578.</para>
        /// <para>The window is one case -- regular at 12 -- and the suite averages 442 across four
        /// styles and eleven sizes. So the aggregate says 5, the window says 6, and both are right
        /// about what they measure.</para>
        /// <para>WHICH MEANS THIS IS TUNED TO ONE SIZE, and that is a liability, not a victory: at
        /// 125% DPI the same window is ppem 15 and at 150% it is 18, where mode 5 is better. Mode 6
        /// is almost certainly compensating for something else that is wrong at 12 rather than
        /// being right in general. Finding that -- and getting mode 5 plus a fix to beat mode 6
        /// everywhere -- is worth more than the 159,129 this earned, because it would hold at every
        /// size instead of one.</para>
        /// <para>AND THE REST WERE RE-SWEPT UNDERNEATH IT, because a knob measured against a
        /// different geometry is a knob measured against something that no longer exists -- which
        /// is exactly how 5 came to sit here after 6 had become better. Every other knob in the
        /// text path, re-measured on the window with mode 6 in place:</para>
        /// <para>gamma 1.16/1.20/1.24 -> 1,019,140 / 985,204 / 1,029,861;  stem fat 0/5/6/7 ->
        /// 1,191,587 / 1,142,649 / 985,204 / 1,010,779;  vertical rows 1/2/4 -> 985,204 /
        /// 1,252,810 / 986,601;  compatible widths 0/1/2 -> 1,076,391 / 985,204 / 1,076,367.</para>
        /// <para>AND regular@12 IS NOT A SUFFICIENT PROXY FOR THE WINDOW, which is worth knowing
        /// because it looks like it should be: the window is Segoe UI regular at 12ppem and that
        /// suite case is exactly that. Swept on it alone, modes 7, 8 and 13 all BEAT the shipped
        /// 6 -- 88,551, 88,450 and 89,304 against 89,997 -- and every one of them loses on the
        /// window, mode 8 by 9,612 (997,285 against 987,673), re-measured after the calendar
        /// fixes in case the ordering had moved. It had not.</para>
        /// <para>The suite draws black on white at a fixed pen. The window draws on control grey,
        /// on selection blue, in bold, and at real layout positions. Whatever the window prefers
        /// mode 6 for is in that difference, so the capture stays the arbiter and the fast case is
        /// a hint rather than a proxy.</para>
        /// <para>All four are already where they should be, so the move to 6 is a genuine joint
        /// improvement and not one knob paying for another. Two things are worth keeping from the
        /// numbers: four vertical samples is now within 1,400 of one and carries LESS weight error
        /// (664,007 against 672,115), so the argument for one row is thinner than it was; and stem
        /// fattening buys 127,000 of weight at the cost of 38,000 of position, which is a blunt
        /// instrument compensating for something not yet named.</para>
        /// <para>AND MODE 6 IS THE DOCUMENTED RULE, which turns a fitted constant into a derived
        /// one. Microsoft's own ClearType booklet (learn.microsoft.com/typography/cleartype/pdfs/
        /// nowreadthis.pdf, p.12) says: "even quite subtle x-direction details can be rendered
        /// simply by letting ClearType use the NEAREST SUBPIXEL BOUNDARY". A subpixel boundary is
        /// a third of a pixel, and mode 6 is exactly that -- RoundDistance multiplies by three and
        /// applies the face's own rule on the finer grid. The mode 5 it replaced rounds on
        /// ClearTypeGrid = 16, a sixteenth of a pixel, which corresponds to nothing described
        /// anywhere. So the sweep did not merely find a better number; it landed on the rule, and
        /// the 159,807 it was worth is the size of the error that reading the paper would have
        /// avoided. Cited so the next person can start from the rule rather than the sweep.</para>
        /// <para>The same page also settles GLYPH ORIGINS, and we already match: "in the earlier
        /// versions of ClearType, the space occupied by each glyph always began on a whole pixel
        /// boundary", subpixel positioning being the LATER technique. GDI classic ClearType --
        /// what WinForms asks for -- is the earlier behaviour, and WgpuSceneRenderer snaps a
        /// hinted run's origin to a whole device pixel and rounds each advance before the next
        /// glyph. Checked rather than assumed, along with 'gasp' (parsed, drives symmetric
        /// smoothing and gridfit) and 'hdmx' (consulted first for advances, which is the
        /// "compatible widths" of CLEARTYPE_QUALITY). None of the four needed changing.</para></summary>
        /// <para>BACK TO 5 on 2026-09-02, and the reason is which instrument was asked. Mode 6
        /// was chosen on the CONTROLS window, which is Segoe UI 9pt inside WinForms controls --
        /// one face, at one size, with control painting between the glyphs and the capture. The
        /// text-only specimen (PAIR_SPECIMEN=text: six faces, each regular, bold and italic) is
        /// the instrument for a question about text, and it says the opposite:</para>
        /// <code>
        ///   text specimen, six faces   mode 5 2,056,362   mode 6 2,906,888
        ///   text specimen, Segoe UI    mode 5   132,757   mode 6   163,112
        ///   parity suite               mode 5    50,409   mode 6    77,051
        ///   controls window            mode 5 1,135,620   mode 6   982,386
        /// </code>
        /// <para>Every clean text instrument prefers 5, by a lot, and Segoe UI ALONE prefers 5 on
        /// the specimen -- so this is not the old letters-and-digits story and not a face mix. The
        /// controls window is the only disagreement, and it is the one measurement with something
        /// other than text in it.</para>
        /// <para>That disagreement is now a LEAD rather than a tie-breaker: the same face at the
        /// same size prefers different rounding depending on whether a plain Label draws it or a
        /// control's paint path does. The difference between those two is where the text lands --
        /// sub-pixel phase at real layout positions -- so that is what to look at next.</para>
        /// <summary>WHAT THE REMAINING ERROR IS, stated as a number rather than a suspicion.
        /// <para>Turn our x hinting off entirely (XHintMode 0, which restores the scaled outline's
        /// x and keeps the hinted advance) and score every row of the specimen against Windows.
        /// Every row gets worse -- 2,343,248 to 3,993,314 -- with two exceptions:</para>
        /// <code>
        ///   Segoe UI I    x hinted  9,760      x not hinted  9,760      IDENTICAL
        ///   Segoe UI BI  x hinted 10,189      x not hinted 10,189      IDENTICAL
        /// </code>
        /// <para>Byte for byte the same, because Segoe UI's italic hints in y and does nothing in
        /// x -- it is not that we decline to fit it, which was checked: nothing is discarded. And
        /// those two rows are our BEST results by a factor of ten, at 0.016 and 0.010 error per
        /// unit of ink where the rest of the specimen runs 0.05 to 0.27.</para>
        /// <para>So: where neither renderer hints x we agree with GDI to about 0.013. Where both
        /// do, we are ten to twenty times worse. The whole of the remaining difference is that our
        /// x fitting is not GDI's x fitting -- and it is not the rounding grid (whole pixels are
        /// the worst result on the board, no rounding ties the sixteenth), not the stem fat (Tahoma
        /// moves 55 parts in 482,000 across its whole range), not the suppressed deltas (worth 2.9
        /// million in the right direction), not the cut-in divisor (a smooth minimum at 16), not
        /// the rasterizer (gamma is a symmetric minimum at 1.20 and the coverage chain is proved
        /// exact), not placement (every band wants a rigid shift of zero), not accumulation (no
        /// drift left to right along any line), and not the interpreter (which reproduces GDI's own
        /// fitted points exactly in the mode where GDI can be asked).</para>
        /// <para>AND THE PAPER'S OWN MODEL IS REFUTED, which is worth stating because it is the
        /// obvious thing to try. If ClearType simply turns x rounding off, the MIRP should take
        /// the CONTROL VALUE -- the designer's standard stem width -- and use it unrounded. That
        /// is WPF_CT_NOROUND_X=1 with WPF_CT_CUTIN_DIV=1, and it measures 3,788,143 against
        /// 2,343,248; without the stem fat, 3,817,113. What ships instead divides the cut-in by
        /// sixteen, which means the control value is almost never taken and the OUTLINE distance
        /// is used instead. So GDI does not merely stop rounding x in ClearType -- it stops
        /// listening to the control values as well, and fits x from the outline's own
        /// measurements. What still moves the glyph is the minimum-distance clamp, which is
        /// spec behaviour and not a rounding at all.</para>
        /// <para>It also sizes the prize. Twenty-four rows at the quality of the two that agree
        /// would be roughly 250,000 against 2,343,248 -- so the x fitting is worth about ninety
        /// percent of what is left, and nothing else is worth chasing before it.</para></summary>
        /// <summary>RE-SWEPT 2026-09-03 ON THE TEXT SPECIMEN, after the interpreter was proved
        /// exact against GetGlyphOutline, and every invented rule here came back a clear optimum.
        /// <para>The proof changed what these knobs mean. Our grid-fitting reproduces GDI's own
        /// fitted points exactly -- Consolas and Segoe UI at 16ppem are 62 of 62 glyphs and 0 of
        /// ~1700 points differing in either axis -- so what is left in the ClearType path is this
        /// layer and nothing underneath it. That made it worth asking whether the layer should
        /// exist at all. It should.</para>
        /// <para>Text specimen, six faces x four styles at 12ppem, SUM|d| against Windows,
        /// baseline 2,343,248:</para>
        /// <code>
        ///   XHintMode      2  2,343,248   16  2,351,674    7  2,822,569
        ///                  6  3,226,503   13  3,244,256    0  3,993,305   17  5,393,616
        ///   NOROUND_X                        2,378,210
        ///   stem fat       0  2,363,864    3  2,351,479    6  2,343,248 (shipped)
        ///                  9  2,365,051   12  2,378,821
        ///   x deltas   touched 2,796,868  inline 4,982,305   all 5,286,060
        /// </code>
        /// <para>Two of those are worth reading as facts about GDI rather than as scores.
        /// Rounding x to WHOLE PIXELS -- which is exactly what the interpreter does in the bi-level
        /// mode where it is provably exact -- is the worst result on the board at 5,393,616. GDI
        /// does not grid-fit x when it draws ClearType, and the sixteenth grid is close enough to
        /// not rounding (2,343,248 against 2,378,210) to be a statement of that rather than a rule
        /// in its own right. And suppressing the x deltas is worth 2.9 MILLION, more than
        /// everything else here put together.</para>
        /// <para>Arial alone says the same: 455,540 shipped, 470,257 without the rounding, 465,011
        /// without the stem fat. These are not constants fitted to Segoe UI that the other faces
        /// merely tolerate.</para>
        /// <para>AND THE ERROR IS NOT PLACEMENT. Every band of the specimen answers dx=0, dy=0 when
        /// asked for the rigid shift that would fit it best. What is left is per-glyph shape, and
        /// it is very unevenly spread -- error per unit of ink at 12ppem:</para>
        /// <code>
        ///   Segoe UI BI 0.010   Segoe UI I 0.016   Segoe UI B 0.053   Verdana B 0.065
        ///   Tahoma B 0.073      Tahoma BI 0.080    Times BI 0.099     Consolas BI 0.106
        ///   Segoe UI R 0.112    Arial I 0.119      Arial B 0.122      Consolas B 0.122
        ///   Times B 0.121       Times R 0.125      Arial BI 0.125     Consolas I 0.132
        ///   Verdana BI 0.140    Consolas R 0.147   Verdana I 0.158    Arial R 0.197
        ///   Verdana R 0.197     Tahoma I 0.218     Tahoma R 0.225     Times I 0.268
        /// </code>
        /// <para>Segoe UI's italics are twenty-five times better than the worst row and ten times
        /// better than Segoe UI's own roman. The thing an italic does not have is upright stems to
        /// grid-fit, so the x rules barely touch it -- which is the same statement as the table
        /// above, from the other side: where this layer does nothing we match GDI almost exactly,
        /// and where it acts we are ten times worse. It is a local optimum and it is also the whole
        /// of the remaining error.</para>
        /// <para>CORRECTION, 2026-09-04: "an italic is barely touched, and where this layer does
        /// nothing we match GDI almost exactly" is true of SEGOE UI's italic and not of italics.
        /// Measured per glyph, drawn spaced, as the ink centroid against GDI's at 12ppem:</para>
        /// <code>
        ///   Segoe UI italic      +0.027  +0.012  +0.014      (and Arial's is as good)
        ///   Times New Roman R    +0.067  +0.019  -0.012  -0.020  -0.021   -- exact
        ///   Times New Roman I    -0.254  -0.277  -0.407  -0.485  -0.526   -- a THIRD to a HALF
        ///                                                                    of a pixel
        /// </code>
        /// <para>Times' italic is one of the worst rows in the whole specimen (0.267 of its own ink
        /// at 12ppem) while its roman is one of the best, so this is not "italics are easy". Nor is
        /// the layer failing to touch it: turning x hinting off entirely makes Times' italic WORSE
        /// (-0.433 / -0.338 / -0.521 / -0.544 / -0.551), so the layer acts on it and helps, and
        /// there is a residual of about 0.45px underneath that it only partly corrects.</para>
        /// <para>Ruled out for that residual: the fitted points (against GetGlyphOutline in its own
        /// mode, Times italic is 7 of 10 glyphs exact with 2 points differing in x), a synthesized
        /// oblique on top of a real italic (head.macStyle declares italic and DeclaredStyle reads
        /// macStyle, not the name table), lsb != xMin (they are EQUAL for every glyph measured in
        /// timesi, times and segoeuii), and accumulating advance error (the deltas do not grow
        /// along the run -- 'n' after 'm' is -0.219 after -0.526). Unexplained, and named here so
        /// the next attempt starts from a face where the layer demonstrably matters.</para>
        /// </summary>
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
        /// <summary>HOW MUCH OF THE X FITTING TO KEEP, in thousandths. WPF_CT_XBLEND.
        /// <para>Asked because GDI treats two sibling faces completely differently and neither the
        /// gasp nor the GETINFO answers distinguish them: solved against GDI's own ClearType
        /// intervals, Verdana's ClearType x IS its bi-level x at every size (95 to 98 per cent,
        /// against 7 for the unhinted outline) while Tahoma's is nearer the UNHINTED outline than
        /// its bi-level fit (11 per cent against 44). If GDI damps the x fitting rather than
        /// applying or skipping it whole, a single fraction between the two should beat both ends
        /// -- and a fraction is one number for every face, not a table of them.</para>
        /// <para>MEASURED AND WRONG. On the specimen at 12ppem, blends of 0.7, 0.8, 0.9, 0.95 and
        /// 0.999 give 2,556,431 / 2,427,298 / 2,361,618 / 2,354,726 / 2,373,491 against 2,343,248
        /// for keeping all of it. The curve rises monotonically as the fraction falls, so GDI
        /// applies the x fitting WHOLE and the difference between the two faces is not a damping
        /// factor. Kept, default off, because it is the only way to re-ask the question.</para>
        /// <para>TWO CONFOUNDS, and the first is why this looked promising at all. Capturing
        /// plainX is what the blend needs, and the compatible-width correction is gated on NOT
        /// having captured it -- so the first sweep silently measured the loss of that correction
        /// too, put every blend above 3,000,000, and made 0.99 look 750,000 worse than 1.0. The
        /// guard now admits the blend. The second is still there and is why 0.999 measures 30,000
        /// short of 1.0 rather than equalling it: capturing plainX also fills plainPhantom, which
        /// turns on a shift inside CompatibleWidthMode 2 that is otherwise skipped. Neither
        /// changes the conclusion -- every blend is worse -- but a smaller effect measured this way
        /// would have been swamped.</para></summary>
        internal static readonly int XBlend =
            int.TryParse(Environment.GetEnvironmentVariable("WPF_CT_XBLEND"), out int xb) ? xb : 1000;

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

        /// <summary>WPF_CT_COMPONENT_FACTOR=own phases each component with its own factor.</summary>
        private static readonly bool s_componentFactorFromRoot =
            Environment.GetEnvironmentVariable("WPF_CT_COMPONENT_FACTOR") != "own";

        /// <summary>Set while a composite's components are hinted: they take the root's phase
        /// inputs rather than measuring their own.</summary>
        [ThreadStatic] private static bool s_inheritPhaseInputs;

        /// <summary>The compatible-width phase's inputs for one glyph: the advance it will be laid
        /// out at, and the unrounded pass-one span the factor divides.</summary>
        /// <summary>The root's linear advance carries FO_SIM_BOLD's units too, as the simple
        /// glyph's does (globals[0x1ac] += (2 upem - 1) / 100). WPF_CT_ROOTLINEAR_BOLD=0.</summary>
        private static readonly bool s_rootLinearBold =
            Environment.GetEnvironmentVariable("WPF_CT_ROOTLINEAR_BOLD") != "0";

        private static readonly bool s_strikeNumerator =
            Environment.GetEnvironmentVariable("WPF_CT_STRIKE_NUM") != "0";

        /// <summary>The face's EBLC/EBDT strikes for their METRICS only -- see SetPhaseInputs.
        /// Built whether or not the strikes are drawn.</summary>
        private readonly BitmapGlyphTable? _metricStrikes;

        private void SetPhaseInputs(int gid, float pixelsPerEm)
        {
            TrueTypeInterpreter.CompatibleAdvance64 =
                (int) MathF.Round(CompatibleAdvance(gid, pixelsPerEm,
                                                    (int) MathF.Round(pixelsPerEm)) * 64f);
            // AND THE UNROUNDED SPAN THE PHASE ACTUALLY WANTS -- from a ClearType pass at
            // factor one, as fs__Contour's first pass is (TryGetClearTypeSpan64), falling back
            // to the bi-level span. WPF_CT_SPAN_PASS=bilevel uses the bi-level span only.
            TrueTypeInterpreter.BiLevelSpan64 =
                s_spanFromCtPass && TryGetClearTypeSpan64(gid, pixelsPerEm, out int ct64) ? ct64
                : TryGetHintedSpan64(gid, pixelsPerEm, out int sp64) ? sp64 : 0;
            // ...UNLESS THE SIZE HAS AN EMBEDDED STRIKE. fs__Contour@140024220 takes the numerator
            // from pass one's phantom span only while clientRec+0x351 is clear; with a strike at
            // this size it calls sbit_CalcDevHorMetrics instead, and the numerator is the strike's
            // own horiAdvance -- whether or not GDI ever draws from the strike (it does not, for a
            // single-byte face). Cambria Bold 'y' at 16ppem ends pass one with pp2 at 576/64
            // (GDI's own trace), yet globals[0x1d0] is 0xf0f1 = 512/544: the 16ppem strike says 8.
            // Dividing our 576 phased the glyph 6% WIDER where GDI compresses it 6%, at exactly
            // the sizes Cambria Bold ships strikes for (12, 13, 15, 16, 17, 19).
            // WPF_CT_STRIKE_NUM=0 keeps the span.
            if (s_strikeNumerator && _metricStrikes is not null && TrueTypeInterpreter.BiLevelSpan64 > 0
                && _metricStrikes.TryGetStrikeAdvance(gid, (int) MathF.Round(pixelsPerEm), out int strikeAdvance))
                TrueTypeInterpreter.BiLevelSpan64 =
                    (strikeAdvance + (GdiEmboldens ? SimBoldAdvancePixels((int) MathF.Round(pixelsPerEm)) : 0)) * 64;
            TrueTypeInterpreter.BiLevelPhantomUntouched =
                TrueTypeInterpreter.BiLevelSpan64 > 0 && !HintedSpanTouched(gid, pixelsPerEm);
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

            GlyphProgram? glyph;
            if ((short)U16(_glyfOffset + (int)start) >= 0) glyph = ReadGlyphProgram(gid);
            else if (s_componentFactorFromRoot && depth == 0 && !s_measuringCtSpan
                     && !TrueTypeInterpreter.BiLevelPass)
            {
                // ONE FACTOR FOR THE WHOLE TREE, THE ROOT'S. fs__Contour runs pass one over every
                // element of the glyph tree, leaves globals[0x1ac] holding the ROOT's advance and
                // the outline's phantoms holding the ROOT's span, and computes globals[0x1d0] once
                // from those -- into the pass-two globals block that every element of pass two
                // then reads. So a component is phased with the composite's factor, not its own:
                // the dieresis in Arial 'a-dieresis' at 20ppem is compressed by 'a's factor, its
                // two dots 225/64 apart in GDI's fit where alone they are 240. Set the root's
                // inputs BEFORE the components are hinted, and have them inherit it.
                // WPF_CT_COMPONENT_FACTOR=own gives each component its own again.
                int sc = TrueTypeInterpreter.CompatibleAdvance64, ss = TrueTypeInterpreter.BiLevelSpan64;
                bool su = TrueTypeInterpreter.BiLevelPhantomUntouched, si = s_inheritPhaseInputs;
                int sl = TrueTypeInterpreter.RootLinear64;
                try
                {
                    SetPhaseInputs(gid, pixelsPerEm);
                    s_inheritPhaseInputs = true;
                    TrueTypeInterpreter.RootLinear64 = interpreter.PrepareForSize(pixelsPerEm)
                        ? interpreter.ScaleToPixels(RawAdvanceWidth(gid)
                                                    + (GdiEmboldens && s_rootLinearBold ? (2 * _unitsPerEm - 1) / 100 : 0)) : 0;
                    ResetScanAcc(depth);
                    glyph = ReadCompositeProgram(interpreter, gid, pixelsPerEm, depth);
                }
                finally
                {
                    TrueTypeInterpreter.CompatibleAdvance64 = sc;
                    TrueTypeInterpreter.BiLevelSpan64 = ss;
                    TrueTypeInterpreter.BiLevelPhantomUntouched = su;
                    TrueTypeInterpreter.RootLinear64 = sl;
                    s_inheritPhaseInputs = si;
                }
            }
            else { ResetScanAcc(depth); glyph = ReadCompositeProgram(interpreter, gid, pixelsPerEm, depth); }
            if (glyph is null) return null;

            // Where x is not being hinted, keep the scaled outline's own x and let the program have
            // the y. Taken BEFORE the program runs, because after it they are the hinted ones, and
            // scaled here because a simple glyph arrives in font units (a composite is already in
            // pixels and its components were dealt with one level down).
            // Subpixel text keeps the x the scaled outline gives it. Taken BEFORE the program runs,
            // because after it they are the hinted ones, and scaled here because a simple glyph
            // arrives in font units (a composite is already in pixels, one level down).
            int[]? plainX = null;
            // ForceYOnlyFit is the stage tests' way in, and it exists because without it stage Y
            // measured NOTHING. Every XHintMode this ships with is in the exclusion list below (5
            // is the default), so SubpixelFitting alone never captured plainX and the y-only stage
            // fitted identically to the x+y one -- two identical rows in the report under
            // different names. The capture is the only thing gated: with plainX in hand,
            // XHintMode 5 already falls to the branch that restores x wholesale, which IS the
            // y-only fit. Shipping behaviour is untouched -- nothing sets this but the tests.
            if ((ForceYOnlyFit
                 || (XBlend > 0 && XBlend < 1000 && SubpixelFitting)
                 || (SubpixelFitting && XHintMode != 1 && XHintMode != 2 && XHintMode != 5 && XHintMode != 6 && XHintMode != 7 && XHintMode != 8 && XHintMode != 11 && XHintMode != 12 && XHintMode != 13 && XHintMode != 14 && XHintMode != 16 && XHintMode != 17))
                && !glyph.Composite
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
            int savedBoldUnits = TrueTypeInterpreter.SimBoldAdvanceUnits;
            if (depth == 0)
                TrueTypeInterpreter.SimBoldAdvanceUnits = GdiEmboldens ? (2 * _unitsPerEm - 1) / 100 : 0;
            // The advance the glyph will be LAID OUT at, for advance-phantom mode 3. Never asked
            // for during a bi-level pass: that pass is how this number is computed in the first
            // place, and asking from inside it recurses.
            // The phase belongs to ONE glyph program. A composite's components are each hinted by
            // their own HintedProgram(depth + 1) call, so without this every component is phased
            // and the assembly inherits the sum -- which is 101 of the accented-glyph ratchets.
            int savedDepth = TrueTypeInterpreter.HintDepth;
            TrueTypeInterpreter.HintDepth = depth;
            int savedCompat = TrueTypeInterpreter.CompatibleAdvance64;
            int savedSpan = TrueTypeInterpreter.BiLevelSpan64;
            bool savedUntouched = TrueTypeInterpreter.BiLevelPhantomUntouched;
            if (s_measuringCtSpan)
            {
                // GDI's pass one: the ClearType program with the compatible-width factor at ONE --
                // no pre-scale, no phase -- whose phantom span becomes the factor's numerator.
                TrueTypeInterpreter.CompatibleAdvance64 = 0;
                TrueTypeInterpreter.BiLevelSpan64 = 0;
            }
            else if (!TrueTypeInterpreter.BiLevelPass && gid >= 0 && gid < _numGlyphs
                     && !(s_inheritPhaseInputs && depth > 0))
            {
                SetPhaseInputs(gid, pixelsPerEm);
                if (s_compatProbe)
                    Console.Error.WriteLine($"COMPAT gid={gid} ppem={pixelsPerEm:0.####}"
                        + $" ppemI={(int) MathF.Round(pixelsPerEm)}"
                        + $" hdmx={(TryGetHdmxAdvance(gid, (int) MathF.Round(pixelsPerEm), out float _h) ? _h.ToString("0.##") : "MISS")}"
                        + $" -> compat64={TrueTypeInterpreter.CompatibleAdvance64}");
            }
            // WPF_CT_FIT_BILEVEL=1: fit the glyph the BI-LEVEL way and render THAT through the
            // ClearType filter. GDI's own ClearType-fitted points for Verdana 'x' at 12ppem are
            // identical to its bi-level fit, and our bi-level fit is already exact against GDI's
            // (62 of 62 glyphs, 0 points differ) -- so if the two really are the same outline this
            // should render almost exactly. Set AFTER CompatibleAdvance64 is computed, because that
            // number is itself measured by a bi-level pass and asking from inside one recurses.
            if (s_fitBiLevel && !TrueTypeInterpreter.BiLevelPass) TrueTypeInterpreter.BiLevelPass = true;
            // WPF_CT_TWOPASS=1: run the glyph program TWICE and keep the second, which is what
            // fs__Contour does under compatible widths -- fsg_ExecuteGlyph at its line 429, then
            // the phase scale computed from the resulting phantoms, then fsg_ExecuteGlyph again.
            // It matters only if the program leaves state behind, and Arial Bold 'X' at 20ppem
            // does: its FDEFs call WS (0x42) and WCVTP (0x44), so a second run reads back what the
            // first wrote. At CLEARTYPE_NATURAL_QUALITY GDI runs it ONCE, which is the
            // configuration our unphased outline was shown to match exactly -- so this is the one
            // remaining way that comparison can be true and the compatible-width fits still
            // differ. REFUTED: 791,238 against 10,327 on Arial Bold at 20ppem. And the size of
            // that says something useful -- it is catastrophic rather than neutral, so OUR
            // interpreter plainly does carry CVT and storage across Hint calls, which means GDI's
            // two passes must NOT share theirs. They take different globals blocks (pfVar51 then
            // pfVar50 in fs__Contour), and this is the measurement that says those blocks carry
            // separate control values. So pass two starts clean, is equivalent to a single pass,
            // and GDI's compatible-width pre-phase fit really is its natural-width fit.
            try
            {
                if (s_twoPassGlyph && !TrueTypeInterpreter.BiLevelPass && SubpixelFitting)
                    interpreter.Hint(glyph, pixelsPerEm);
                hinted = interpreter.Hint(glyph, pixelsPerEm);
                if (hinted && !TrueTypeInterpreter.BiLevelPass && !s_measuringCtSpan)
                    RecordScanType(interpreter, glyph, gid, pixelsPerEm, depth);
                // THE SIMULATED BOLD, AS GDI APPLIES IT: once, to the whole glyph tree, after its
                // programs and in both passes -- see GdiEmbolden.
                if (hinted && depth == 0 && GdiEmboldens) GdiEmbolden(glyph, pixelsPerEm, true);
            }
            finally
            {
                TrueTypeInterpreter.BiLevelPass = savedBi;
                TrueTypeInterpreter.CompatibleAdvance64 = savedCompat;
                TrueTypeInterpreter.BiLevelSpan64 = savedSpan;
                TrueTypeInterpreter.BiLevelPhantomUntouched = savedUntouched;
                TrueTypeInterpreter.HintDepth = savedDepth;
                TrueTypeInterpreter.SimBoldAdvanceUnits = savedBoldUnits;
            }
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
            // WPF_CT_Y_BILEVEL=1: keep the ClearType fit's x and take its Y from a second,
            // BI-LEVEL pass. A DIAGNOSTIC, to answer one question -- Times 'n' at 16ppem renders a
            // bottom serif row of 9.93 lamps against GDI's 18.94, and the mode-6 glyph program
            // provably does not place those points in y at all (its y pass touches nine points and
            // leaves the serifs to IUP). If GDI's y here is the grid-fitted one, this hybrid
            // closes that row; if it is not, the row is telling us something else.
            if (s_yFromBiLevel && !glyph.Composite)
            {
                GlyphProgram? plainY = ReadGlyphProgram(gid);
                if (plainY is not null)
                {
                    bool fittedY;
                    bool savedYbi = TrueTypeInterpreter.BiLevelPass;
                    TrueTypeInterpreter.BiLevelPass = true;
                    try { fittedY = interpreter.Hint(plainY, pixelsPerEm); }
                    finally { TrueTypeInterpreter.BiLevelPass = savedYbi; }
                    if (fittedY && plainY.PointCount == glyph.PointCount)
                        for (int i = 0; i < glyph.PointCount && i < glyph.Y.Length
                                        && i < plainY.Y.Length; i++)
                            glyph.Y[i] = plainY.Y[i];
                }
            }
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
            // ...and NOT while measuring, because what a measuring run wants is the raw span the
            // program left. Correcting it there would correct it onto itself.
            // THE COMPOSITE EXEMPTION IS CORRECT, and WPF_CT_COMPATWIDTH_COMPOSITE=1 exists to
            // stop that being re-litigated. Microsoft's sentence is about "the glyphs for this
            // font size" and says nothing about composites, and the lamps that sit two or more
            // levels from GDI cluster on exactly those glyphs -- five of the worst fourteen are
            // accented composites -- so this reads like a difference we chose. It is not.
            // <para>Dropping the guard changes NOTHING: 54,934 against 54,934 across all 442
            // cases of the parity repertoire, not one of them moving by a pixel. The reason is
            // in ReadCompositeProgram, which builds each component with HintedProgram(...,
            // depth + 1) -- so every component is a SIMPLE glyph that has already had this
            // correction applied to it. A composite assembled from corrected parts is already
            // correct, and applying it again would correct it twice.</para>
            // The blend needs plainX captured, and this correction is gated on NOT having captured
            // it -- so without this clause, measuring the blend silently measures the loss of the
            // compatible-width correction as well, which is worth about 750,000 on its own and
            // makes a blend of 0.99 look 750,000 worse than a blend of 1.
            if (CompatibleWidthMode != 0 && (plainX is null || (XBlend > 0 && XBlend < 1000))
                && (s_compatWidthComposite || !glyph.Composite)
                && !s_measuringAdvance && glyph.X.Length > glyph.PointCount + 1)
            {
                int p0 = glyph.X[glyph.PointCount], p1 = glyph.X[glyph.PointCount + 1];
                int fitted = p1 - p0;
                if (Environment.GetEnvironmentVariable("WPF_CT_CW_TRACE") == "1" && gid >= 0 && gid < _numGlyphs)
                    Console.Error.WriteLine($"CWIN mode={CompatibleWidthMode} gid={gid}"
                        + $" p0={p0} p1={p1} fitted={fitted}");
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
                    float wanted4 = CompatibleAdvance(gid, pixelsPerEm, ppemI);
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
                    // hdmx if the face ships it, else the face's program run in bi-level to find
                    // out. NEVER TryGetDeviceAdvance -- it hints the glyph to answer and we are
                    // inside the hinter; CompatibleAdvance carries the guard that makes it safe.
                    float wanted = CompatibleAdvance(gid, pixelsPerEm, ppemI);
                    int target = (int) MathF.Round(wanted * 64f);
                    // IT STRETCHES THE STEMS, and that is the price. Verdana's 'l' at 19ppem fits to
                    // a 1.75px stem inside a 5.25px advance; the compatible advance is 6, so the
                    // glyph is scaled by 6/5.25 and the stem comes out at 2.0. GDI draws 1.663.
                    // Ours is worse than the unscaled fit for that ONE measurement -- and better
                    // everywhere it is measured whole, because an advance that is a pixel out
                    // displaces every glyph after it and a stem an eighth of a pixel wide does not.
                    // Re-measured over the repertoire at 10..20ppem on Verdana, which has no 'hdmx'
                    // and so takes the largest corrections of any face here:
                    //     scale 23,565    translate 25,226    no correction at all 26,338
                    // and on Segoe UI, which has one: 41,294 / 41,705 / --. Scaling wins at every
                    // size but 19 and 20, where the three are within noise of each other.
                    //
                    // Only a CORRECTION, never a rebuild. The scale is the ratio of two advances and
                    // it is applied to the INK, so where the fitted phantom points have gone astray
                    // it stretches the glyph instead of nudging it: 'f' at 11ppem comes out 4.41px
                    // wide against 3.33 unhinted, a third wider than the letter, and it is the second
                    // most expensive glyph in the whole repertoire. A glyph asking for a large
                    // correction is one whose fitted advance cannot be trusted -- leave it alone.
                    // WHAT THE CORRECTION IS CORRECTING. The scale is target/fitted, so it is a
                    // measure of how far our HINTED advance has drifted from the one Windows lays
                    // out with. Where that ratio is 1 there is nothing to correct and nothing to
                    // distort; where it is far from 1 the glyph is squeezed to make up for it. So
                    // the ratio is the actual defect, and the squeeze is its symptom.
                    if (s_cwTrace)
                        Console.Error.WriteLine($"CW gid={gid} ppem={ppemI} fitted={fitted / 64f:0.000}"
                            + $" target={target / 64f:0.000} ratio={(fitted == 0 ? 0 : target / (float) fitted):0.0000}");
                    int off = Math.Abs(target - fitted) * 100;
                    if (target > 0 && target != fitted && off <= fitted * CompatibleWidthTolerance)
                    {
                        for (int i = 0; i < glyph.X.Length; i++)
                            glyph.X[i] = p0 + (int) MathF.Round((glyph.X[i] - p0) * (target / (float) fitted));
                    }
                }
                // MODE 6: the displacement applied BETWEEN features and not within them.
                //
                // The scale beats a plain translation because GDI's displacement grows across the
                // glyph; it loses stem width because it also grows WITHIN each stem. So group the
                // points into features -- runs of x with no gap wider than a stem -- and move each
                // group as a unit by the scale's displacement at its own centre. Between groups the
                // spacing still stretches; inside one, every distance is preserved.
                //
                // Clustering on x is a proxy for "stem": it is what can be had without asking which
                // points the program touched, and it puts the two stems of an 'n' in two groups
                // where a per-CONTOUR rule would put them in one.
                else if (CompatibleWidthMode == 6 && fitted > 0 && gid >= 0 && gid < _numGlyphs)
                {
                    float wanted6 = CompatibleAdvance(gid, pixelsPerEm, ppemI);
                    int target6 = (int) MathF.Round(wanted6 * 64f);
                    int off6 = Math.Abs(target6 - fitted) * 100;
                    if (target6 > 0 && target6 != fitted && off6 <= fitted * CompatibleWidthTolerance
                        && glyph.PointCount > 0)
                    {
                        var order = new int[glyph.PointCount];
                        for (int i = 0; i < order.Length; i++) order[i] = i;
                        Array.Sort(order, (a, b) => glyph.X[a].CompareTo(glyph.X[b]));

                        float s6 = target6 / (float) fitted;
                        int gap = StemGap64;
                        int runStart = 0;
                        var shift = new int[glyph.PointCount];
                        for (int i = 1; i <= order.Length; i++)
                        {
                            bool split = i == order.Length
                                         || glyph.X[order[i]] - glyph.X[order[i - 1]] > gap;
                            if (!split) continue;
                            int lo6 = glyph.X[order[runStart]], hi6 = glyph.X[order[i - 1]];
                            int d = (int) MathF.Round((s6 - 1f) * ((lo6 + hi6) * 0.5f - p0));
                            for (int k = runStart; k < i; k++) shift[order[k]] = d;
                            runStart = i;
                        }
                        for (int i = 0; i < glyph.PointCount; i++) glyph.X[i] += shift[i];
                        if (glyph.X.Length > glyph.PointCount + 1)
                            glyph.X[glyph.PointCount + 1] = glyph.X[glyph.PointCount] + target6;
                    }
                }
                // MODE 7: GDI'S RULE, read off its solved edges rather than guessed.
                //
                // The per-edge solver (SolveGdisEdges) recovers where GDI put every vertical edge
                // of a straight-sided glyph, to a sixty-fourth, residual zero. Across Arial's
                // I H E F L T i l at 10-24ppem -- 338 edges -- the placement that lands 294 of
                // them inside GDI's run, and misses the rest by under a sixth of a pixel, is:
                //   run the program with the advance phantom on the UNROUNDED linear advance and
                //   the face's fractional x nudges executed (both arranged for by this mode);
                //   then move every FEATURE rigidly by (A/lin - 1) times its centre, where A is
                //   the advance the glyph is laid out at, lin the linear one, and a feature is
                //   the set of points the program placed from one another (the interpreter's
                //   union-find over MDRP/MIRP/MSIRP/ALIGNRP/SHP).
                // The mode-1 scale is this with three things wrong: it uses A/round(lin), it
                // scales WITHIN a stem (crushing it), and its tolerance gate turns it off exactly
                // where the displacement is largest. Points the program never touched in x are
                // moved by interpolating the displacement between their x-neighbours.
                //
                // SHIPPED (the default) since it scores 10,399,039 on the weight report against
                // mode 1's 11,584,399, with the interpreter tying only BLACK links into features
                // (WPF_CT_LINKTYPES) -- and 10,319,044 once a black link whose chord crosses a
                // COUNTER is read as the position it is (TrueTypeInterpreter.EffectiveLinkType,
                // Stamm's double-check; Arial 'd' MIRPs bowl-left to stem-right as black and GDI
                // scales it). What it still gets wrong, measured: Verdana's bold and
                // italic (683,018 -> 717,121 and 570,573 -> 752,685), Tahoma's bold (329,998 ->
                // 340,471) and Consolas' italic (286,761 -> 369,475); every other face and style
                // improves, Tahoma's roman by a third. The per-edge solver says GDI does NOT move
                // Consolas' 'l' at 16ppem (our raw outline is exact) nor Tahoma's 'l' at 12
                // though it moves both faces' 'I' -- Beat Stamm's "damage control" for glyphs
                // without counterforms -- and that Verdana Italic's slanted stems are neither
                // scaled nor left alone but drawn with a different slant; neither rule is known
                // yet, and the mode-1 tolerance gate that happened to skip them is not it either
                // (mode 1 is worse than mode 0 for Consolas, and equal elsewhere).
                else if (CompatibleWidthMode == 13 && fitted > 0 && gid >= 0 && gid < _numGlyphs)
                {
                    // GDI'S OWN UNIT IS THE LINKED PAIR, read from PhaseShift: when a point has a
                    // link partner, GDI computes
                    //     phase = CompDiv(0x20000, (x[partner] + x[p]) * (ctFactor - 1.0))
                    //           = centre_of_pair * (ctFactor - 1)
                    // and adds it to BOTH edges. So each STEM -- one link, reference and placed
                    // point -- moves RIGIDLY by its own centre's displacement. Mode 7 uses the
                    // union-find FEATURE instead, which chains every link in the glyph together
                    // ('H's crossbar ties its two stems into one), so a whole letter slides where
                    // GDI opens the gaps between its stems.
                    int n13 = glyph.PointCount;
                    float wanted13 = CompatibleAdvance(gid, pixelsPerEm, ppemI);
                    int target13 = (int) MathF.Round(wanted13 * 64f);
                    if (target13 > 0 && target13 != fitted && n13 > 0)
                    {
                        float f13 = target13 / (float) fitted - 1f;
                        var la = new int[n13 * 2]; var lb = new int[n13 * 2];
                        int nl = interpreter.ReadXLinks(la, lb);
                        var disp = new int[n13];
                        var have = new bool[n13];
                        // GDI only makes a PAIR when DoubleCheckLinkColor says so, and that
                        // function returns a colour (rather than passing the instruction's own
                        // through) ONLY when the two points are ADJACENT ON THE SAME CONTOUR and
                        // the segment between them is no steeper than 2:1. Everything else keeps
                        // its instruction colour, gets param_5 = 3, and so never becomes a pair.
                        bool Adjacent(int u, int v)
                        {
                            int start = 0;
                            for (int c = 0; c < glyph.EndPoints.Length; c++)
                            {
                                int end = glyph.EndPoints[c];
                                if (u >= start && u <= end && v >= start && v <= end)
                                {
                                    int len = end - start + 1;
                                    if (len < 2) return false;
                                    int du = u - start, dv = v - start;
                                    int diff = du - dv;
                                    if (diff < 0) diff = -diff;
                                    return diff == 1 || diff == len - 1;
                                }
                                start = end + 1;
                            }
                            return false;
                        }
                        for (int k = 0; k < nl; k++)
                        {
                            int r = la[k], q = lb[k];
                            if ((uint) r >= (uint) n13 || (uint) q >= (uint) n13) continue;
                            if (!Adjacent(r, q)) continue;
                            int sdx = glyph.X[q] - glyph.X[r], sdy = glyph.Y[q] - glyph.Y[r];
                            if (sdx < 0) sdx = -sdx;
                            if (sdy < 0) sdy = -sdy;
                            if (sdy > 2 * sdx) continue;              // steeper than 2:1
                            int d = (int) MathF.Round((glyph.X[r] + glyph.X[q]) * 0.5f * f13);
                            if (!have[r]) { disp[r] = d; have[r] = true; }
                            if (!have[q]) { disp[q] = d; have[q] = true; }
                        }
                        int anchored = 0;
                        for (int i = 0; i < n13; i++) if (have[i]) anchored++;
                        if (anchored > 0)
                        {
                            // Everything the pairs did not place is carried between them, in x.
                            var ord = new int[anchored];
                            for (int i = 0, k = 0; i < n13; i++) if (have[i]) ord[k++] = i;
                            Array.Sort(ord, (x1, x2) => glyph.X[x1].CompareTo(glyph.X[x2]));
                            for (int i = 0; i < n13; i++)
                            {
                                if (have[i]) continue;
                                int x = glyph.X[i];
                                if (x <= glyph.X[ord[0]]) { disp[i] = disp[ord[0]]; continue; }
                                if (x >= glyph.X[ord[^1]]) { disp[i] = disp[ord[^1]]; continue; }
                                int k = 1;
                                while (glyph.X[ord[k]] < x) k++;
                                int aa = ord[k - 1], bb = ord[k];
                                int span = glyph.X[bb] - glyph.X[aa];
                                disp[i] = span <= 0 ? disp[aa]
                                    : disp[aa] + (int) MathF.Round((disp[bb] - disp[aa]) * (x - glyph.X[aa]) / (float) span);
                            }
                            for (int i = 0; i < n13; i++) glyph.X[i] += disp[i];
                            if (glyph.X.Length > glyph.PointCount + 1)
                                glyph.X[glyph.PointCount + 1] = glyph.X[glyph.PointCount] + target13;
                        }
                    }
                }
                else if (CompatibleWidthMode == 12 && fitted > 0 && gid >= 0 && gid < _numGlyphs)
                {
                    float wanted12 = CompatibleAdvance(gid, pixelsPerEm, ppemI);
                    int target12 = (int) MathF.Round(wanted12 * 64f);
                    if (target12 > 0 && interpreter.ApplyNaturalStretch(glyph.X, glyph.PointCount, target12)
                        && glyph.X.Length > glyph.PointCount + 1)
                        glyph.X[glyph.PointCount + 1] = glyph.X[glyph.PointCount] + target12;
                }
                else if (CompatibleWidthMode == 11 && fitted > 0 && gid >= 0 && gid < _numGlyphs)
                {
                    // GDI'S OWN MECHANISM (fs_NewGlyph): ctFactor = deviceAdvance/linearAdvance,
                    // displacing every point by x*(ctFactor-1) propagated through the interpreter's
                    // PLACEMENT TREE. Mode 7 approximates this with feature centres; this is the
                    // thing itself.
                    float wanted11 = CompatibleAdvance(gid, pixelsPerEm, ppemI);
                    int target11 = (int) MathF.Round(wanted11 * 64f);
                    if (Environment.GetEnvironmentVariable("WPF_CT_CW_TRACE") == "1")
                        Console.Error.WriteLine($"CW11 gid={gid} fitted={fitted} target={target11} n={glyph.PointCount}");
                    if (target11 > 0 && target11 != fitted)
                    {
                        bool okPhase = interpreter.ApplyCompatPhase(glyph.X, glyph.PointCount, target11 / (float) fitted);
                        if (Environment.GetEnvironmentVariable("WPF_CT_CW_TRACE") == "1")
                            Console.Error.WriteLine($"CW11 applied={okPhase}");
                        // ...and REALIZE THE ADVANCE. Omitting this crippled the measurement: for
                        // Verdana 'H'@12 the displacement is ~0 (s = 0.9983) and the entire value
                        // of the compatible-width pass is putting the advance on its hdmx value,
                        // which took the edge oracle from 6,782 to 944 under mode 7 while mode 11
                        // scored identically to doing nothing at all.
                        if (glyph.X.Length > glyph.PointCount + 1)
                            glyph.X[glyph.PointCount + 1] = glyph.X[glyph.PointCount] + target11;
                    }
                }
                else if (CompatibleWidthMode == 7 && fitted > 0 && gid >= 0 && gid < _numGlyphs)
                {
                    int n7 = glyph.PointCount;
                    float wanted7 = CompatibleAdvance(gid, pixelsPerEm, ppemI);
                    int target7 = (int) MathF.Round(wanted7 * 64f);
                    if (target7 > 0 && target7 != fitted && n7 > 0)
                    {
                        var feature = new int[n7];
                        var touched = new bool[n7];
                        interpreter.ReadXFeatures(feature, touched);
                        float s7 = target7 / (float) fitted;

                        // The centre of each feature, over its touched points.
                        var lo7 = new int[n7]; var hi7 = new int[n7];
                        Array.Fill(lo7, int.MaxValue); Array.Fill(hi7, int.MinValue);
                        int touchedCount = 0;
                        for (int i = 0; i < n7; i++)
                        {
                            if (!touched[i]) continue;
                            touchedCount++;
                            int f = feature[i];
                            if (glyph.X[i] < lo7[f]) lo7[f] = glyph.X[i];
                            if (glyph.X[i] > hi7[f]) hi7[f] = glyph.X[i];
                        }
                        var shift = new int[n7];
                        // NOTHING FITTED IN X, NOTHING MOVED. Segoe UI's italic has no x
                        // instructions at all, and the edge solver finds GDI drawing its scaled
                        // outline untouched -- 'I', 'H' and 'l' at 12ppem exact as they are --
                        // where its linear advance (3.19) and the advance it is laid out at (3)
                        // differ as much as Arial's do. So the displacement is not a scale of the
                        // outline onto the advance; it is a displacement of what the program
                        // PLACED, and an outline the program left alone stays where it was.
                        // EXPERIMENT (WPF_CT_MINFEAT): damage control as 'no counterform to
                        // absorb the squeeze' -- a glyph whose program placed fewer than this
                        // many distinct features in x is left where the program put it.
                        int distinct7 = 0;
                        {
                            var seen7 = new bool[n7];
                            for (int i = 0; i < n7; i++)
                                if (touched[i] && !seen7[feature[i]]) { seen7[feature[i]] = true; distinct7++; }
                        }
                        if (s_minFeatures > 0)
                        {
                            // 1 = only when SQUEEZING, 2 = only when STRETCHING. The measured
                            // asymmetry: leaving a one-feature glyph where the program put it is
                            // right when s > 1 ('w' -38,670) and wrong when s < 1 ('l' +79,883,
                            // 'v' +46,612, 'j' +34,864) -- a glyph being squeezed still has to be
                            // slid onto its narrower advance, but one being stretched does not get
                            // dragged along with it.
                            bool sqOk = s_minFeatSqueezeOnly switch
                            {
                                1 => s7 < 1f,
                                2 => s7 > 1f,
                                _ => true,
                            };
                            if (distinct7 < s_minFeatures && sqOk) touchedCount = 0;
                        }
                        // A one-feature glyph is moved RIGIDLY by (s-1)*(centre-p0). That slide is
                        // what GDI does not do to a wide letter: 'w' is slid +41/64 and GDI leaves
                        // it within a couple of 64ths of where the program put it. Suppress only
                        // the LARGE slides, which is the quantity that separates 'w' from the
                        // narrow glyphs (whose centres are small, so whose slides are small).
                        int inkA = int.MaxValue, inkB = int.MinValue;
                        for (int i = 0; i < n7; i++)
                        {
                            if (glyph.X[i] < inkA) inkA = glyph.X[i];
                            if (glyph.X[i] > inkB) inkB = glyph.X[i];
                        }
                        // ...and only for a glyph WIDE enough that a slide of that size is a
                        // displacement of a letter rather than of a single stem: 'l' is one stem
                        // ~1.7px wide and still wants its slide (+19,192 without this), 'w' is 9px.
                        // The slide (s-1)*centre grows with ppem, so an ABSOLUTE threshold fires
                        // at different relative magnitudes at different sizes -- which shows up as
                        // a see-saw across adjacent rows (regular@19 -40,373 but regular@20
                        // +50,009). WPF_CT_MINFEAT_S gates on |s-1| instead, which is
                        // size-independent; 0 keeps the absolute test.
                        bool bigEnough = s_minFeatS > 0
                            ? MathF.Abs(s7 - 1f) * 1000f >= s_minFeatS
                            : s_minFeatShift > 0;
                        if (bigEnough && touchedCount > 0 && distinct7 == 1
                            && inkB - inkA >= s_minFeatInk)
                        {
                            int f1 = -1;
                            for (int i = 0; i < n7 && f1 < 0; i++) if (touched[i]) f1 = feature[i];
                            if (f1 >= 0)
                            {
                                float sl = (s7 - 1f) * ((lo7[f1] + hi7[f1]) * 0.5f - p0);
                                if (MathF.Abs(sl) >= s_minFeatShift) touchedCount = 0;
                            }
                        }
                        // ONE FEATURE means mode 7's per-feature rigid move degenerates into a
                        // pure TRANSLATION of the whole glyph -- it realizes the compatible width
                        // by sliding the ink sideways and never widening it. Verdana 'w'@12 is the
                        // worst case in the whole repertoire for exactly this reason (s=1.1192,
                        // every touched point in feature 6, every shift +41), and the edge oracle
                        // says GDI instead holds the ink's CENTRE and widens it. WPF_CT_CW1
                        // selects how that case is realized: 0 = the rigid slide (shipped),
                        // 1 = hold the centre and scale by s, 2 = hold the centre and scale by
                        // s damped by WPF_CT_CW1_DAMP/1000.
                        int distinctFeat = 0;
                        {
                            var seenF = new bool[n7];
                            for (int i = 0; i < n7; i++)
                                if (touched[i] && !seenF[feature[i]]) { seenF[feature[i]] = true; distinctFeat++; }
                        }
                        int inkLo = int.MaxValue, inkHi = int.MinValue;
                        for (int i = 0; i < n7; i++)
                        {
                            if (glyph.X[i] < inkLo) inkLo = glyph.X[i];
                            if (glyph.X[i] > inkHi) inkHi = glyph.X[i];
                        }
                        // ...but only where there is INK to absorb the widening. If the glyph IS
                        // one stem ('l', 'i', 'j', 'f'), scaling about its centre widens the STEM
                        // itself, which is plainly wrong and measures it: 'l' alone costs +87,540.
                        // A lone stem must be TRANSLATED; a wide glyph with interpolated strokes
                        // between its anchors must be WIDENED. WPF_CT_CW1_MININK is that gate, in
                        // 64ths of a pixel of ink width.
                        if (touchedCount > 0 && distinctFeat == 1 && s_cw1Mode != 0
                            && inkHi - inkLo >= s_cw1MinInk)
                        {
                            int minX = inkLo, maxX = inkHi;
                            float centre = (minX + maxX) * 0.5f;
                            float k = s_cw1Mode == 2 ? 1f + (s7 - 1f) * (s_cw1Damp / 1000f) : s7;
                            for (int i = 0; i < n7; i++)
                                shift[i] = (int) MathF.Round((k - 1f) * (glyph.X[i] - centre));
                            touchedCount = 0;          // shifts are already final for every point
                        }
                        if (touchedCount > 0)
                        {
                            for (int i = 0; i < n7; i++)
                                if (touched[i])
                                    shift[i] = (int) MathF.Round((s7 - 1f) * ((lo7[feature[i]] + hi7[feature[i]]) * 0.5f - p0));

                            // Untouched points: interpolate between the nearest touched points
                            // in x, clamped beyond the outermost.
                            var order = new int[touchedCount];
                            for (int i = 0, k = 0; i < n7; i++) if (touched[i]) order[k++] = i;
                            Array.Sort(order, (a, b) => glyph.X[a].CompareTo(glyph.X[b]));
                            for (int i = 0; i < n7; i++)
                            {
                                if (touched[i]) continue;
                                int x = glyph.X[i];
                                if (x <= glyph.X[order[0]]) { shift[i] = shift[order[0]]; continue; }
                                if (x >= glyph.X[order[^1]]) { shift[i] = shift[order[^1]]; continue; }
                                int k = 1;
                                while (glyph.X[order[k]] < x) k++;
                                int a = order[k - 1], b = order[k];
                                int span = glyph.X[b] - glyph.X[a];
                                shift[i] = span <= 0 ? shift[a]
                                    : shift[a] + (int) MathF.Round((shift[b] - shift[a]) * (x - glyph.X[a]) / (float) span);
                            }
                        }
                        if (s_cwTrace)
                        {
                            var tr = new System.Text.StringBuilder($"CW7 gid={gid} ppem={ppemI} fitted={fitted} target={target7} s={s7:0.0000} p0={p0}\n");
                            for (int i = 0; i < n7; i++)
                                tr.Append($"   pt{i,2} x={glyph.X[i],5} {(touched[i] ? "T" : ".")} feat={feature[i],2} shift={shift[i],3}\n");
                            Console.Error.Write(tr.ToString());
                        }
                        // GENERALISING THE ONE-FEATURE FIX. Suppressing the whole slide of a wide
                        // one-feature glyph measured; the multi-feature glyphs left over (Tahoma
                        // 'm'@16 6,028, Tahoma 'w'@12 4,626, Segoe UI 'm'@12 2,098 -- almost all
                        // POSITION) get the same treatment applied to their COMMON part only:
                        // subtract the mean slide, which removes the bulk translation of the
                        // letter and leaves the spacing between its features untouched.
                        if (s_cwDemean > 0 && touchedCount > 0 && distinct7 > 1)
                        {
                            long sum = 0; int cnt = 0;
                            for (int i = 0; i < n7; i++) if (touched[i]) { sum += shift[i]; cnt++; }
                            if (cnt > 0)
                            {
                                int mean = (int) MathF.Round(sum / (float) cnt);
                                if (Math.Abs(mean) >= s_cwDemean && inkB - inkA >= s_minFeatInk)
                                    for (int i = 0; i < n7; i++) shift[i] -= mean;
                            }
                        }
                        // GDI WIDENS THE INK LESS THAN THE ADVANCE. Verdana 'w'@12 and Tahoma
                        // 'w'@12 both put GDI's ink stretch (1.036, 1.054) at roughly a third of
                        // the advance stretch (1.119, 1.123), the rest being taken up by the side
                        // bearings. Mode 7 spends the whole of it on spreading the features apart.
                        // WPF_CT_CW_SPREAD damps the SPREAD of the slides about their mean while
                        // leaving the mean -- the glyph's overall displacement -- alone, so the
                        // letter still lands where it should but opens up less.
                        if (s_cwSpread != 1000 && touchedCount > 0)
                        {
                            long sum = 0; int cnt = 0;
                            for (int i = 0; i < n7; i++) { sum += shift[i]; cnt++; }
                            if (cnt > 0)
                            {
                                float mean = sum / (float) cnt, k = s_cwSpread / 1000f;
                                for (int i = 0; i < n7; i++)
                                    shift[i] = (int) MathF.Round(mean + (shift[i] - mean) * k);
                            }
                        }
                        // The slides come out systematically LARGE against GDI: the same rightward
                        // bias the one-feature gate above removes for 'w' shows up per-feature on
                        // multi-feature glyphs too (Tahoma 'm'@16 shifts 30/18/5, edges ~12/64
                        // right of GDI). WPF_CT_CW_DAMP scales every slide, in thousandths.
                        if (s_cwDamp != 1000)
                            for (int i = 0; i < n7; i++)
                                shift[i] = (int) MathF.Round(shift[i] * (s_cwDamp / 1000f));
                        for (int i = 0; i < n7; i++) glyph.X[i] += shift[i];
                        if (glyph.X.Length > glyph.PointCount + 1)
                            glyph.X[glyph.PointCount + 1] = glyph.X[glyph.PointCount] + target7;
                    }
                }
                // MODE 10: GDI'S ACTUAL STEM ALIGNMENT, read from dwrite.dll's GC* chain.
                //
                // The edge solver proved no whole-glyph model (mode 1's scale, mode 7's feature-
                // rigid move) can match GDI, because GDI does not treat the glyph as one body. Its
                // GCCalcLocs/GCFindLocs (in dwrite's classic scaler) grid-fit each STEM on its own:
                // the stem's centre is rounded to the pixel grid and its two edges are snapped to
                // whole pixels -- floor the left, ceil the right -- so every stem lands on a whole
                // number of pixels. Between stems the outline interpolates. This is Beat Stamm's
                // intelligent scaling; here is the first brick of it -- the per-stem whole-pixel
                // snap -- with the ratio-alignment and path-spacing still to come.
                //
                // A "stem" is ONE link the program made (ReadXLinks) -- the reference point and the
                // point placed from it -- NOT the union-find feature, which chains a whole glyph's
                // links together and would snap two stems as if they were one.
                else if (CompatibleWidthMode == 10 && fitted > 0 && gid >= 0 && gid < _numGlyphs)
                {
                    int n = glyph.PointCount;
                    if (n > 0)
                    {
                        var la = new int[n * 2]; var lb = new int[n * 2];
                        int links = interpreter.ReadXLinks(la, lb);

                        // GCCalcLocs + GCFindLocs, per stem (units are 64ths; 64 = one pixel): round
                        // the stem's CENTRE to the grid, floor the left edge, ceil the right, so the
                        // stem covers whole pixels. Accumulate each point's target and average, so a
                        // point shared by two links settles between them.
                        static int Floor64(int v) => (v >> 6) << 6;
                        static int Ceil64(int v) => ((v + 63) >> 6) << 6;
                        static int Round64(int v) => (int) MathF.Round(v / 64f) * 64;
                        var acc = new long[n]; var cnt = new int[n];
                        void Aim(int pt, int target) { acc[pt] += target; cnt[pt]++; }
                        for (int k = 0; k < links; k++)
                        {
                            int r = la[k], p = lb[k];
                            if ((uint) r >= (uint) n || (uint) p >= (uint) n) continue;
                            int e0 = Math.Min(glyph.X[r], glyph.X[p]);
                            int e1 = Math.Max(glyph.X[r], glyph.X[p]);
                            // Only a STEM is snapped to whole pixels; a wide black link is spacing
                            // (the far side of an 'n' placed across the whole glyph) and goes through
                            // a different GDI path. Gate on width. WPF_CT_STEMMAX, in 64ths.
                            if (e1 - e0 > s_stemMax) continue;
                            int centre = (e0 + e1) / 2;
                            int rc = Round64(centre);
                            int half = (e1 - e0) / 2;
                            int newLo = Floor64(rc - half);
                            int newHi = Ceil64(rc + half);
                            if (newHi < newLo + 64) newHi = newLo + 64;
                            bool rIsLo = glyph.X[r] <= glyph.X[p];
                            Aim(r, rIsLo ? newLo : newHi);
                            Aim(p, rIsLo ? newHi : newLo);
                        }

                        var shift = new int[n];
                        var order = new System.Collections.Generic.List<int>();
                        for (int i = 0; i < n; i++)
                            if (cnt[i] > 0) { shift[i] = (int) (acc[i] / cnt[i]) - glyph.X[i]; order.Add(i); }

                        // Points no link placed: interpolate between the nearest placed points in x.
                        order.Sort((a, b) => glyph.X[a].CompareTo(glyph.X[b]));
                        if (order.Count > 0)
                            for (int i = 0; i < n; i++)
                            {
                                if (cnt[i] > 0) continue;
                                int x = glyph.X[i];
                                if (x <= glyph.X[order[0]]) { shift[i] = shift[order[0]]; continue; }
                                if (x >= glyph.X[order[^1]]) { shift[i] = shift[order[^1]]; continue; }
                                int k = 1;
                                while (glyph.X[order[k]] < x) k++;
                                int a = order[k - 1], b = order[k];
                                int span = glyph.X[b] - glyph.X[a];
                                shift[i] = span <= 0 ? shift[a]
                                    : shift[a] + (int) MathF.Round((shift[b] - shift[a]) * (x - glyph.X[a]) / (float) span);
                            }

                        for (int i = 0; i < n; i++) glyph.X[i] += shift[i];

                        // The advance stays the compatible (hdmx) width, as GDI's does.
                        int ppemI10 = (int) MathF.Round(pixelsPerEm);
                        int target10 = (int) MathF.Round(CompatibleAdvance(gid, pixelsPerEm, ppemI10) * 64f);
                        if (target10 > 0 && glyph.X.Length > glyph.PointCount + 1)
                            glyph.X[glyph.PointCount + 1] = glyph.X[glyph.PointCount] + target10;
                    }
                }
                // MODE 5: buy the same MOVE without paying in stem width.
                //
                // The scale above is the largest correction in this path and it works, but the
                // solver says what it costs: GDI keeps the natural stem width and we crush it --
                // Arial 'I' at 16ppem fits to 1.438, the scale delivers 1.078, GDI draws 1.453.
                // The scale earns its place by dragging the ink LEFT, nearer where GDI puts it,
                // and it pays for that with every stem in the glyph.
                //
                // So: translate the ink by exactly what the scale would have moved its centre, and
                // set the advance to the target directly. Same displacement, same advance, stems
                // untouched.
                else if (CompatibleWidthMode == 5 && fitted > 0 && gid >= 0 && gid < _numGlyphs)
                {
                    float wanted5 = CompatibleAdvance(gid, pixelsPerEm, ppemI);
                    int target5 = (int) MathF.Round(wanted5 * 64f);
                    int off5 = Math.Abs(target5 - fitted) * 100;
                    if (target5 > 0 && target5 != fitted && off5 <= fitted * CompatibleWidthTolerance)
                    {
                        int lo = int.MaxValue, hi = int.MinValue;
                        for (int i = 0; i < glyph.PointCount; i++)
                        {
                            if (glyph.X[i] < lo) lo = glyph.X[i];
                            if (glyph.X[i] > hi) hi = glyph.X[i];
                        }
                        if (lo <= hi)
                        {
                            float s5 = target5 / (float) fitted;
                            int shift5 = (int) MathF.Round((s5 - 1f) * ((lo + hi) * 0.5f - p0));
                            for (int i = 0; i < glyph.PointCount; i++) glyph.X[i] += shift5;
                            if (glyph.X.Length > glyph.PointCount + 1)
                                glyph.X[glyph.PointCount + 1] = glyph.X[glyph.PointCount] + target5;
                        }
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
                else if (XBlend > 0 && XBlend < 1000)
                {
                    // Keep a FRACTION of what the fitting moved, rather than all of it or none.
                    for (int i = 0; i < glyph.PointCount; i++)
                        glyph.X[i] = plainX[i]
                                   + (int) (((long) (glyph.X[i] - plainX[i]) * XBlend) / 1000);
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
            // WPF_OUTLINE_DUMP=<path>: write the FITTED outline (26.6, y up, contour ends) so an
            // external renderer can be fed exactly what we produce. This is how we tell an outline
            // bug from a rasterizer bug: render OUR outline with a renderer already proven to
            // reproduce GDI, and see which side the difference is on.
            if (Environment.GetEnvironmentVariable("WPF_OUTLINE_DUMP") is { Length: > 0 } odPath
                && (s_outlineDumpGid < 0 || gid == s_outlineDumpGid))
            {
                var sb = new System.Text.StringBuilder();
                sb.Append("gid=").Append(gid).Append(" ppem=").Append((int) MathF.Round(pixelsPerEm))
                  .Append(" bilevel=").Append(TrueTypeInterpreter.BiLevelPass ? 1 : 0)
                  .Append(" subpixel=").Append(SubpixelFitting ? 1 : 0)
                  .Append(" points=").Append(glyph.PointCount).Append(" ends=");
                for (int i = 0; i < glyph.EndPoints.Length; i++)
                { if (i > 0) sb.Append(','); sb.Append(glyph.EndPoints[i]); }
                sb.Append('\n');
                for (int i = 0; i < glyph.PointCount; i++)
                    sb.Append(glyph.X[i]).Append(' ').Append(glyph.Y[i]).Append(' ')
                      .Append(glyph.OnCurve[i] ? 1 : 0).Append('\n');
                System.IO.File.AppendAllText(odPath, sb.ToString());
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
        /// <summary>WPF_CT_BORROW_EARLY=1: USE_MY_METRICS sets the composite's phantoms before its
        /// program runs, as it used to.</summary>
        private static readonly bool s_borrowAfterProgram =
            Environment.GetEnvironmentVariable("WPF_CT_BORROW_EARLY") != "1";

        /// <summary>WPF_CT_CHILD_SCALE=0: hint a transformed component at the plain scale and
        /// apply its matrix in floating point, as before.</summary>
        private static readonly bool s_childScale =
            Environment.GetEnvironmentVariable("WPF_CT_CHILD_SCALE") != "0";

        private static int FixMulAway(int v, int m16)
        {
            long p = (long)v * m16;
            return (int)((p + (p >> 63) + 0x8000) >> 16);
        }

        private static readonly bool s_shearOffsetSixteenth =
            Environment.GetEnvironmentVariable("WPF_CT_SHEAR_OFFSET") != "0";

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
            int borrowedOrigin = 0, borrowedAdvance = 0, borrowedOriginY = 0, borrowedAdvanceY = 0;

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

                // A transformed component is hinted at its matrix's magnitude and flipped or
                // rotated afterwards (see TrueTypeInterpreter.Hint). One magnitude for both axes:
                // a matrix that stretches x and y differently keeps the plain scale.
                bool xform = a != 1f || b != 0f || c != 0f || d != 1f;
                int m00 = (int)MathF.Round(a * 65536f), m01 = (int)MathF.Round(b * 65536f);
                int m10 = (int)MathF.Round(c * 65536f), m11 = (int)MathF.Round(d * 65536f);
                int magX = Math.Max(Math.Abs(m00), Math.Abs(m01)), magY = Math.Max(Math.Abs(m10), Math.Abs(m11));
                int savedChild = TrueTypeInterpreter.ChildScale16;
                if (s_childScale && xform && magX == magY && magX != 0x10000)
                    TrueTypeInterpreter.ChildScale16 = magX;
                GlyphProgram? part;
                try { part = HintedProgram(interpreter, componentGid, pixelsPerEm, depth + 1); }
                finally { TrueTypeInterpreter.ChildScale16 = savedChild; }
                if (part is null) continue;                 // a blank component places nothing

                int count = part.PointCount;
                var px = new int[count + 2];
                var py = new int[count + 2];
                for (int i = 0; i < count + 2; i++)         // the two horizontal phantoms travel too
                {
                    float fx = part.X[i], fy = part.Y[i];
                    if (s_childScale && xform)
                    {
                        // mth_IntelMul@140026b40: 16.16 products, each rounded half away from zero.
                        px[i] = FixMulAway(part.X[i], m00) + FixMulAway(part.Y[i], m10);
                        py[i] = FixMulAway(part.X[i], m01) + FixMulAway(part.Y[i], m11);
                        continue;
                    }
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
                        // ...but only to a WHOLE pixel off the ClearType axis.
                        // scl_CalcComponentOffset@180293d18 rounds the offset two ways: the
                        // ordinary (v + 0x20) & ~0x3f to a pixel, and, when ClearType is on and
                        // this is its axis, (v + 2) & ~3 -- the LAMP grid, a sixteenth of a
                        // pixel. Rounding x to a whole pixel put every accent up to half a
                        // pixel from where GDI puts it.
                        dx = s_ctComponentOffset && SubpixelFitting && !TrueTypeInterpreter.BiLevelPass
                            ? (dx + 2) & ~3
                            : TrueTypeInterpreter.RoundToPixel(dx);
                        // ...AND Y TOO, UNDER A SHEARED TRANSFORM. fsg_MergeGlyphData@140030040
                        // classifies the element's matrix -- 0 axis-aligned, 1 swapped, 2 anything
                        // with both a diagonal and an off-diagonal term -- and for class 2 under
                        // ClearType it rounds BOTH offsets to the sixteenth. A simulated italic is
                        // class 2: ttfd's bSetXform puts the shear (0x5700) into the scaler's
                        // matrix, and fontdrvhost's own fit of Tahoma 'N-tilde' at 20ppem with
                        // FO_SIM_ITALIC has the tilde 28/64 above the whole pixel it sits on
                        // without. WPF_CT_SHEAR_OFFSET=0 keeps y on the whole pixel.
                        dy = s_shearOffsetSixteenth && _shear != 0f && SubpixelFitting
                             && !TrueTypeInterpreter.BiLevelPass
                            ? (dy + 2) & ~3
                            : TrueTypeInterpreter.RoundToPixel(dy);
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
                    // THE PHANTOMS ARE NOT OFFSET. fsg_ExecuteGlyph's merge adds the component
                    // offset to points 0..lastPoint only and then stores the component's phantoms
                    // as they stand -- Arial Bold 'quoteright', a comma raised 1181 units, keeps
                    // the comma's pp1 on the baseline, and the re-anchor on it leaves the quote up.
                    int odx = s_borrowAfterProgram ? 0 : dx;
                    borrowedOrigin = px[count] + odx;
                    borrowedAdvance = px[count + 1] + odx;
                    borrowedOriginY = py[count];
                    borrowedAdvanceY = py[count + 1];
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
            if (borrowedMetrics && !s_borrowAfterProgram)
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
                CompositeAdvanceUnits = borrowedMetrics && !s_borrowAfterProgram ? -1 : RawAdvanceWidth(gid),
                BorrowedPhantoms = borrowedMetrics && s_borrowAfterProgram
                    ? new[] { borrowedOrigin, borrowedOriginY, borrowedAdvance, borrowedAdvanceY } : null,
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
        /// <summary>The unfitted outline AT A SIZE, with the 26.6 rounding applied to the POINTS
        /// and not to the figure.
        /// <para>Rounding the figure is a point too late. BuildContourFigure materialises the
        /// implied on-curve point between two consecutive off-curve ones as their exact average,
        /// and GDI keeps that average on the HALF sixty-fourth -- its own unhinted report for
        /// Times New Roman 'm' at 8ppem carries thirteen of them, every one an odd number of
        /// 128ths, against ours which had been rounded to a whole sixty-fourth. Scaling and
        /// rounding the points first and building the figure afterwards puts the midpoint back
        /// where GDI has it: halfway between two rounded neighbours.</para>
        /// <para>WPF_UNFITTED_PTS=0 goes back to rounding the finished figure.</para></summary>
        private static readonly bool s_unfittedPp1 =
            Environment.GetEnvironmentVariable("WPF_UNFITTED_PP1") != "0";

        private static readonly bool s_unfittedBorrowPp1 =
            Environment.GetEnvironmentVariable("WPF_UNFITTED_BORROW_PP1") != "0";

        /// <summary>The component a composite takes its metrics from (USE_MY_METRICS), if any.</summary>
        private bool TryGetMetricsComponent(int gid, out int component)
        {
            component = -1;
            if (gid < 0 || gid >= _numGlyphs || _glyfOffset < 0 || _loca.Length == 0) return false;
            uint start = _loca[gid], end = _loca[gid + 1];
            if (end <= start) return false;
            int p = _glyfOffset + (int) start;
            if ((short) U16(p) >= 0) return false;
            p += 10;
            for (int guard = 0; guard < 64; guard++)
            {
                int flags = U16(p), cg = U16(p + 2);
                p += 4 + ((flags & 0x0001) != 0 ? 4 : 2);
                if ((flags & 0x0008) != 0) p += 2;
                else if ((flags & 0x0040) != 0) p += 4;
                else if ((flags & 0x0080) != 0) p += 8;
                if ((flags & 0x0200) != 0 && cg < _numGlyphs && _loca[cg + 1] > _loca[cg]) { component = cg; return true; }
                if ((flags & 0x0020) == 0) break;
            }
            return false;
        }

        private static readonly bool s_unfittedAdvBorrow =
            Environment.GetEnvironmentVariable("WPF_UNFITTED_ADV_BORROW") != "0";

        private static readonly bool s_offsetHalfUp =
            Environment.GetEnvironmentVariable("WPF_UNFITTED_OFFSET_HALFUP") != "0";

        private static readonly bool s_unfittedComponents =
            Environment.GetEnvironmentVariable("WPF_UNFITTED_COMPONENTS") != "0";

        /// <summary>A COMPOSITE IS ASSEMBLED AT SIZE, NOT IN FONT UNITS, EVEN WHERE NOTHING IS
        /// FITTED. fsg_MergeGlyphData@140030040 runs whether or not a glyph program does: each
        /// component is scaled to 26.6 on its own and its offset is scaled separately and, under
        /// ROUND_XY_TO_GRID, rounded -- to a whole pixel in y, to the sixteenth in x under
        /// ClearType (both to the sixteenth under a sheared matrix). Resolving the composite in
        /// font units and scaling the result put Verdana Bold 'A-tilde' at 8ppem -- a size its
        /// gasp leaves unfitted -- with the tilde a row above GDI's. Appends y-DOWN, 26.6-rounded
        /// pixel contours to <paramref name="into"/>; false for a simple glyph, a point-matched
        /// component or a variable font (the caller then scales the resolved outline).
        /// WPF_UNFITTED_COMPONENTS=0 restores the font-unit assembly.</summary>
        private bool TryScaleCompositeAt(int gid, double k, bool halfUp, int depth,
                                         List<(Vector2[] Pts, bool[] On)> into)
        {
            if (depth > 5 || _glyfOffset < 0 || gid < 0 || gid >= _numGlyphs || _loca.Length == 0) return false;
            uint start = _loca[gid], end = _loca[gid + 1];
            if (end <= start) return false;
            int header = _glyfOffset + (int) start;
            if ((short) U16(header) >= 0) return false;         // simple: not ours to assemble

            const int ARG_1_AND_2_ARE_WORDS = 0x0001, ARGS_ARE_XY_VALUES = 0x0002;
            const int ROUND_XY_TO_GRID = 0x0004, WE_HAVE_A_SCALE = 0x0008, MORE_COMPONENTS = 0x0020;
            const int WE_HAVE_AN_X_AND_Y_SCALE = 0x0040, WE_HAVE_A_TWO_BY_TWO = 0x0080;
            const int SCALED_COMPONENT_OFFSET = 0x0800;
            int p = header + 10;
            bool more = true;
            var parts = new List<(Vector2[] Pts, bool[] On)>();
            while (more)
            {
                int flags = U16(p); p += 2;
                int compGid = U16(p); p += 2;
                int arg1, arg2;
                if ((flags & ARG_1_AND_2_ARE_WORDS) != 0) { arg1 = (short) U16(p); p += 2; arg2 = (short) U16(p); p += 2; }
                else { arg1 = (sbyte) _data[p++]; arg2 = (sbyte) _data[p++]; }
                float a = 1f, bb = 0f, c = 0f, d = 1f;
                if ((flags & WE_HAVE_A_SCALE) != 0) { a = d = F2Dot14(p); p += 2; }
                else if ((flags & WE_HAVE_AN_X_AND_Y_SCALE) != 0) { a = F2Dot14(p); p += 2; d = F2Dot14(p); p += 2; }
                else if ((flags & WE_HAVE_A_TWO_BY_TWO) != 0)
                { a = F2Dot14(p); p += 2; bb = F2Dot14(p); p += 2; c = F2Dot14(p); p += 2; d = F2Dot14(p); p += 2; }
                more = (flags & MORE_COMPONENTS) != 0;
                if ((flags & ARGS_ARE_XY_VALUES) == 0) return false;   // point matching: fall back

                // The component, at size, y-DOWN on the 26.6 grid (the plain path's frame).
                var comp = new List<(Vector2[] Pts, bool[] On)>();
                if (!TryScaleCompositeAt(compGid, k, halfUp, depth + 1, comp))
                {
                    comp.Clear();
                    foreach (Contour ct in ReadGlyphContours(compGid, depth + 1))
                    {
                        var pts = new Vector2[ct.Points.Length];
                        for (int i = 0; i < pts.Length; i++)
                            pts[i] = new Vector2(Round64((float) (ct.Points[i].X * k), halfUp),
                                                 Round64Y((float) (-ct.Points[i].Y * k), halfUp));
                        comp.Add((pts, ct.OnCurve));
                    }
                }

                bool identity = a == 1f && bb == 0f && c == 0f && d == 1f;
                float ox = (float) (arg1 * k), oy = (float) (arg2 * k);     // y-UP, as the font has it
                if ((flags & SCALED_COMPONENT_OFFSET) != 0 && !identity)
                { float tx = a * ox + c * oy; oy = bb * ox + d * oy; ox = tx; }
                // Font-unit scaling is floor(v + 1/2), as the fitted path's ScaleToPixels has it:
                // Consolas Italic 'A-tilde' at 8ppem offsets its tilde -10 units, -2.5/64, which
                // GDI takes to -2 (and the sixteenth to 0) where away-from-zero gave -3 (-4).
                int dx64 = s_offsetHalfUp ? (int) Math.Floor(ox * 64.0 + 0.5)
                                          : (int) MathF.Round(ox * 64f, MidpointRounding.AwayFromZero);
                int dy64 = s_offsetHalfUp ? (int) Math.Floor(oy * 64.0 + 0.5)
                                          : (int) MathF.Round(oy * 64f, MidpointRounding.AwayFromZero);
                if ((flags & ROUND_XY_TO_GRID) != 0)
                {
                    bool ct = s_ctComponentOffset && SubpixelFitting;
                    dx64 = ct ? (dx64 + 2) & ~3 : (dx64 + 32) & ~63;
                    dy64 = ct && s_shearOffsetSixteenth && _shear != 0f ? (dy64 + 2) & ~3 : (dy64 + 32) & ~63;
                }
                foreach ((Vector2[] pts, bool[] on) in comp)
                {
                    if (pts.Length < 2) continue;
                    var outp = new Vector2[pts.Length];
                    for (int i = 0; i < pts.Length; i++)
                    {
                        Vector2 q = pts[i];                      // y-down
                        if (!identity)
                        {
                            // the component's own matrix works in y-UP
                            float ux = q.X, uy = -q.Y;
                            q = new Vector2(Round64(a * ux + c * uy, halfUp), Round64Y(-(bb * ux + d * uy), halfUp));
                        }
                        outp[i] = new Vector2(q.X + dx64 / 64f, q.Y - dy64 / 64f);
                    }
                    parts.Add((outp, on));
                }
            }
            into.AddRange(parts);
            return true;
        }

        private List<PathFigure> BuildGlyphFiguresAt(int gid, float pixelsPerEm, bool halfUp)
        {
            double k = (double) pixelsPerEm / _unitsPerEm;
            var scaled = new List<(Vector2[] Pts, bool[] On)>();
            if (!(s_unfittedComponents && s_unfittedRoundMode != 0 && _variations is null
                  && TryScaleCompositeAt(gid, k, halfUp, 0, scaled)))
            {
                scaled.Clear();
                List<Contour> contours = ReadGlyphContours(gid, 0);
                foreach (Contour contour in contours)
                {
                    if (contour.Points.Length < 2) continue;
                    var pts = new Vector2[contour.Points.Length];
                    for (int i = 0; i < pts.Length; i++)
                        pts[i] = new Vector2((float) (contour.Points[i].X * k),
                                             (float) (-contour.Points[i].Y * k));
                    scaled.Add((pts, contour.OnCurve));
                }
            }

            // The simulations are in pixels at THIS size, which is what the base-pixel builder
            // does too once its own scale is taken out. Not GDI's simulated bold, though: that
            // never moves a point, fitted or not -- the weight is the lamp smear
            // (PathRasterizer.EmboldenLampRows). Offsetting the unfitted outline as well drew
            // every face without a bold file 22% too heavy at 8ppem, the size where their prep
            // turns instructions off.
            if (_emboldenStrength > 0f && !GdiEmboldens)
                Embolden(scaled, _emboldenStrength * pixelsPerEm / BaseEmPixels);

            // THE SLANT IS A COLUMN OF THE TRANSFORM, NOT A CORRECTION APPLIED AFTER IT.
            // <para>GDI scales an unfitted outline through a matrix, and scl_Scale rounds EACH
            // product to 26.6 on its own -- so a sheared x is `round(x*sx) + round(y*sxy)`, two
            // roundings summed, and not `round(x*sx + y*sxy)`. The two differ by up to half a
            // sixty-fourth, which is exactly what GDI's own unhinted report for Tahoma Italic 'c'
            // at 8ppem shows: thirteen points differ in x by one or two 128ths and none differ in
            // y at all.</para>
            // <para>WPF_OBLIQUE_MATRIX=0 shears the scaled float and rounds once.</para>
            bool rnd = s_unfittedRoundMode != 0;
            foreach ((Vector2[] pts, _) in scaled)
                for (int i = 0; i < pts.Length; i++)
                {
                    float x = pts[i].X, y = pts[i].Y;
                    if (_shear != 0f && s_obliqueMatrix && rnd)
                    {
                        // FROM THE ROUNDED Y. GDI's own unhinted report for Tahoma Italic 'W'
                        // at 8ppem puts its six cap-height points one sixty-fourth left of a
                        // shear taken on the unrounded y, and exactly where a shear taken on the
                        // 26.6 y puts them: cap height is 1489 units, 5.8164px unrounded and
                        // 5.8125 rounded, and 87/256 of those differ by a sixty-fourth after
                        // rounding. So the slant is applied to the outline AFTER it is on the
                        // 26.6 grid, not inside the scaling matrix.
                        float yr = Round64Y(y, halfUp);
                        x = Round64(x, halfUp) + Round64(-_shear * yr, halfUp);
                        pts[i] = new Vector2(x, yr);
                        continue;
                    }
                    if (_shear != 0f) x -= _shear * y;
                    pts[i] = rnd ? new Vector2(Round64(x, halfUp), Round64Y(y, halfUp))
                                 : new Vector2(x, y);
                }

            // THE OUTLINE IS PLACED ON ITS LEFT PHANTOM EVEN WHEN NOTHING IS FITTED. fs__Contour
            // translates every point by origin - curX[pp1] after the (here empty) glyph pass, and
            // rounds that only when grid fitting -- so an unfitted glyph whose side bearing is not
            // its xMin moves by the exact scaled difference. Times New Roman Italic's degree sign
            // (xMin 98, lsb 212) at 8ppem, a size its gasp leaves unfitted, sat 0.445px left of
            // GDI's. pp1's x is on the 26.6 grid like every other point. WPF_UNFITTED_PP1=0 skips.
            if (s_unfittedPp1 && gid >= 0 && gid < _numGlyphs && _glyfOffset >= 0 && _loca.Length > 0
                && _loca[gid + 1] > _loca[gid])
            {
                int xMin = (short) U16(_glyfOffset + (int) _loca[gid] + 2);
                int pp1Units = xMin - MetricsLeftSideBearing(gid);
                // A composite that borrows a component's metrics (USE_MY_METRICS) is placed on
                // THAT component's left phantom, unshifted, as fs_ExecuteGlyph does when it copies
                // the phantoms over. Times New Roman Bold 'U-dieresis' at 7ppem carries a side
                // bearing of 71 against 'U''s 48 and drew 4/64 right of GDI.
                if (s_unfittedBorrowPp1 && TryGetMetricsComponent(gid, out int mc))
                {
                    int cxMin = (short) U16(_glyfOffset + (int) _loca[mc] + 2);
                    pp1Units = cxMin - MetricsLeftSideBearing(mc);
                }
                // ...ON THE CLEARTYPE SIXTEENTH, as the fitted re-anchor is: Times Italic 'c', 'j'
                // and 'y' carry a side bearing four units off their xMin -- a sixty-fourth at
                // 8ppem -- and GDI does not move them, where the degree sign's 28.5/64 moves 28/64.
                float dx = 0f;
                if (pp1Units != 0)
                {
                    int d64 = -(int) MathF.Round(Round64((float) (pp1Units * k), halfUp) * 64f);
                    if (SubpixelFitting) d64 = (d64 + 2) & ~3;
                    dx = d64 / 64f;
                }
                if (dx != 0f)
                    foreach ((Vector2[] pts, _) in scaled)
                        for (int i = 0; i < pts.Length; i++) pts[i] = new Vector2(pts[i].X + dx, pts[i].Y);
            }

            var figures = new List<PathFigure>(scaled.Count);
            foreach ((Vector2[] pts, bool[] on) in scaled)
                figures.Add(BuildContourFigure(pts, on));
            return figures;
        }

        /// <summary>One coordinate on the 26.6 grid, by whichever of scl_Scale's two roundings
        /// this size takes. See ScaleRoundsHalfUp.</summary>
        private static float Round64(float v, bool halfUp)
            => s_unfittedRoundMode == 1 || !halfUp
                   ? MathF.Round(v * 64f, MidpointRounding.AwayFromZero) / 64f
                   : MathF.Floor(v * 64f + 0.5f) / 64f;

        /// <summary>The same on the y axis, which is STORED FLIPPED: GDI rounds toward +infinity
        /// in its own y-up space, which is toward -infinity here.</summary>
        private static float Round64Y(float v, bool halfUp)
            => s_unfittedRoundMode == 1 || !halfUp
                   ? MathF.Round(v * 64f, MidpointRounding.AwayFromZero) / 64f
                   : -MathF.Floor(-v * 64f + 0.5f) / 64f;

        private static readonly bool s_obliqueMatrix =
            Environment.GetEnvironmentVariable("WPF_OBLIQUE_MATRIX") != "0";

        private static readonly bool s_unfittedPoints =
            Environment.GetEnvironmentVariable("WPF_UNFITTED_PTS") != "0";

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
        private static readonly bool s_gdiEmbolden =
            Environment.GetEnvironmentVariable("WPF_GDI_EMBOLDEN") != "0";

        /// <summary>Whether this face's simulated bold is GDI's (fsg_Embold) rather than the
        /// symmetric dilation. WPF_GDI_EMBOLDEN=0 restores the dilation.</summary>
        private bool GdiEmboldens => s_gdiEmbolden && _emboldenStrength > 0f;

        /// <summary>Whether the renderer should smear this face's lamps (GDI's FO_SIM_BOLD).</summary>
        internal bool GdiEmboldensBitmap => GdiEmboldens && !s_embOutline;

        private static readonly bool s_embOutline =
            Environment.GetEnvironmentVariable("WPF_EMB_OUTLINE") == "1";

        /// <summary>How many pixels FO_SIM_BOLD adds to each glyph's advance: bComputeMaxGlyph's
        /// font-context field 0x190, (2 x ppem - 1) / 100 + 1 -- one pixel at every UI size --
        /// which vFillGLYPHDATA adds to the glyph's device advance.</summary>
        internal static int SimBoldAdvancePixels(int ppem) => (2 * ppem - 1) / 100 + 1;

        /// <summary>GDI'S SIMULATED BOLD, ported from fsg_Embold@14002e618 and
        /// EmboldPoint@14002b298. It is not a symmetric dilation.
        /// <para>scl_InitializeScaling@140040540 sets the amounts in whole pixels,
        /// (20 x ppem - 10) / 1000 + 1 in x and (20 x ppem - 10) / 1000 in y -- one pixel and none
        /// at every UI size (fontdrvhost's own globals: 1/0 up to 48ppem, 2/1 at 64). A GRID-FITTED
        /// glyph splits x unevenly, floor(x/2) to the left and the rest to the right, so the whole
        /// pixel goes RIGHT; an unfitted one splits it in halves and shifts right by the left
        /// half. Every point then moves by EmboldPoint's rule: each of its two edges is pushed out
        /// along its unit normal (-dy, dx) -- by the right amount for a normal with a positive x,
        /// the left amount otherwise, and likewise in y -- the two pushed edges are intersected
        /// (or averaged where they are near parallel), the move is clamped to the amounts, and
        /// the point is shifted by the left and bottom amounts. So a left-facing stem edge stays
        /// put and a right-facing one moves a pixel right: the glyph gains a pixel of weight on
        /// the right, and pp2 moves a pixel with it.</para></summary>
        private void GdiEmbolden(GlyphProgram glyph, float pixelsPerEm, bool fitted)
        {
            int ppem = (int) MathF.Round(pixelsPerEm);
            int ax = (20 * ppem - 10) / 1000 + 1, ay = (20 * ppem - 10) / 1000;
            int p8, p9, p10, p11;
            if (!fitted) { p8 = p9 = ax * 32; p10 = p11 = ay * 32; }
            else
            {
                p9 = (ax >> 1) << 6; p8 = (ax - (ax >> 1)) * 64;
                p10 = (ay >> 1) << 6; p11 = (ay - (ay >> 1)) * 64;
            }
            int n = glyph.PointCount;
            int[] X = glyph.X, Y = glyph.Y;
            if (X.Length > n + 1 && X[n + 1] != X[n]) X[n + 1] += 64;
            // UNDER CLEARTYPE THE POINTS DO NOT MOVE. A real draw sets the glyph input's +0x8c
            // (bSetXform, from ttfdQueryFontData's FO_SIM_BOLD test), fs__Contour hands that to
            // fsg_Embold as its "skip" argument in both passes, and the weight is added to the
            // bitmap instead -- PathRasterizer.EmboldenLampRows. Only pp2 moves, above.
            // WPF_EMB_OUTLINE=1 runs the point pass as well (what the harness does with +0x8c clear).
            if (!s_embOutline) return;
            int first = 0;
            foreach (int last in glyph.EndPoints)
            {
                GdiEmboldContour(X, Y, first, last, p8, p9, p10, p11);
                first = last + 1;
            }
        }

        private static void GdiEmboldContour(int[] X, int[] Y, int start, int end,
                                             int p8, int p9, int p10, int p11)
        {
            if (end - start < 2) return;
            int m = end - start + 1;
            var ox = new int[m]; var oy = new int[m];
            for (int k = 0; k < m; k++) { ox[k] = X[start + k]; oy[k] = Y[start + k]; }
            int i = 0;
            int px = ox[m - 1], py = oy[m - 1];
            while (i < m)
            {
                int j = i;
                // a run of identical points is one vertex; its neighbour is the first point after it
                while (j + 1 < m && ox[j + 1] == ox[i] && oy[j + 1] == oy[i]) j++;
                int nIdx = j + 1 < m ? j + 1 : 0;
                EmboldPoint(X, Y, start + i, start + j, px, py, ox[i], oy[i], ox[nIdx], oy[nIdx],
                            p8, p9, p10, p11);
                px = ox[i]; py = oy[i];
                i = j + 1;
            }
        }

        /// <summary>A unit normal component in 2.6 (64 = 1): fontdrvhost normalizes to 2.14 through
        /// FracSqrt and a rounding divide, adds half and shifts to 2.14, then takes >> 8.</summary>
        private static void UnitNormal(int vx, int vy, out int nx, out int ny)
        {
            if (vx == 0 && vy == 0) { nx = 0x4000 >> 8; ny = 0; return; }
            double len = Math.Sqrt((double) vx * vx + (double) vy * vy);
            int x14 = (int) Math.Floor(vx / len * 16384.0 + 0.5);
            int y14 = (int) Math.Floor(vy / len * 16384.0 + 0.5);
            nx = x14 >> 8; ny = y14 >> 8;
        }

        private static int CompDivRound(long num, int den)
        {
            // CompDiv: half the divisor added with the numerator's sign, so a tie rounds away from zero
            if (den == 0) return num < 0 ? int.MinValue : int.MaxValue;
            long half = Math.Abs((long) den) / 2;
            return (int) ((num + (num < 0 ? -half : half)) / den);
        }

        private static void EmboldPoint(int[] X, int[] Y, int first, int last,
                                        int Px, int Py, int Cx, int Cy, int Nx, int Ny,
                                        int p8, int p9, int p10, int p11)
        {
            // the two edges' outward normals, (-dy, dx) for a contour of the usual orientation
            UnitNormal(-(Cy - Py), Cx - Px, out int nix, out int niy);
            UnitNormal(-(Ny - Cy), Nx - Cx, out int nox, out int noy);
            int dxi = ((nix * (nix >= 1 ? p8 : p9)) + 32) >> 6;
            int dyi = ((niy * (niy < 0 ? p11 : p10)) + 32) >> 6;
            int dxo = ((nox * (nox >= 1 ? p8 : p9)) + 32) >> 6;
            int dyo = ((noy * (noy < 0 ? p11 : p10)) + 32) >> 6;
            int Pax = Px + dxi, Pay = Py + dyi;          // w4, w21
            int Cax = Cx + dxi, Cay = Cy + dyi;          // w11, w15
            int Nbx = Nx + dxo, Nby = Ny + dyo;          // w6, w26
            int Cbx = Cx + dxo, Cby = Cy + dyo;          // w5, w19
            int rx, ry;
            if (Cax == Cbx && Cay == Cby) { rx = Cbx; ry = Cby; goto Store; }
            {
                int odx = Nbx - Cbx, ody = Nby - Cby;    // w14, w6
                int idx = Cax - Pax, idy = Cay - Pay;    // w13, w12
                int w21, w8;
                if (idy == 0)
                {
                    if (odx == 0) { rx = Cbx; ry = Pay; goto Clamp; }
                    w21 = Cby - Pay; w8 = -ody;
                }
                else if (idx == 0)
                {
                    if (ody == 0) { rx = Pax; ry = Cby; goto Clamp; }
                    w21 = Cbx - Pax; w8 = -odx;
                }
                else if (Math.Abs(idx) >= Math.Abs(idy))
                {
                    int w0 = CompDivRound((long) (Cbx - Pax) * idy, idx);
                    w21 = (Cby - Pay) - w0;
                    w0 = CompDivRound((long) odx * idy, idx);
                    w8 = w0 - ody;
                }
                else
                {
                    int w0 = CompDivRound((long) (Cby - Pay) * idx, idy);
                    w21 = w0 + (Pax - Cbx);
                    w0 = CompDivRound((long) ody * idx, idy);
                    w8 = odx - w0;
                }
                if (Math.Abs(w8) <= 16)
                {
                    rx = (Cax + Cbx) >> 1; ry = (Cay + Cby) >> 1;
                }
                else
                {
                    rx = CompDivRound((long) odx * w21, w8) + Cbx;
                    ry = CompDivRound((long) ody * w21, w8) + Cby;
                }
            }
        Clamp:
            {
                int mx = rx - Cx, my = ry - Cy;
                if (mx > p8) rx = Cx + p8;
                if (mx < -p9) rx = Cx - p9;
                if (my < -p11) ry = Cy - p11;
                if (my > p10) ry = Cy + p11;
            }
        Store:
            rx += p9; ry += p11;
            for (int k = first; k <= last; k++) { X[k] = rx; Y[k] = ry; }
        }

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
            if (s_dropDuplicates) DropDuplicatePoints(ref pts, ref on);
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

        /// <summary>WPF_CT_DEDUP=0 hands the scan converter coincident points as they come.</summary>
        private static readonly bool s_dropDuplicates =
            Environment.GetEnvironmentVariable("WPF_CT_DEDUP") != "0";

        /// <summary>A POINT THAT REPEATS ITS PREDECESSOR IS DELETED, AND THE ONE KEPT IS ON-CURVE.
        /// <para>fs_FindBitMapSize@140022a48 does this to the fitted outline before
        /// fsc_MeasureGlyph and fsc_FillGlyph see it (the loop at 140022f40..14002303c). For each
        /// contour it walks i from the start to end - 1, and wherever point i + 1 equals point i
        /// on both axes it shifts points start..i - 1 up by one (so point i, flag and all, is
        /// overwritten), moves the contour's start forward by one, and ORs the ON-CURVE bit into
        /// point i + 1. After the walk, if the last point equals the (new) first one, the start
        /// moves forward again and the last point is made on-curve.</para>
        /// <para>That is not a no-op when one of the pair is off-curve. An off-curve point on top
        /// of an on-curve one reads, as drawn, as a degenerate spline from the vertex to the
        /// implied midpoint beyond it, followed by a spline starting FROM that midpoint; GDI
        /// instead draws one spline from the vertex through the next control point, and the
        /// midpoint never exists. Times New Roman Italic 'm' at 22ppem fits points 76 and 77 onto
        /// the same 26.6 position -- (188, 576) -- and the curve that follows then crosses the
        /// sub-row at y = 9.1px a sub-column further left in GDI's raster than in ours: one lamp,
        /// the last 58 of the holdout. Read off GDI's own flags (ctharness, clientRec+0x1e8 ol[6]
        /// after fs_FindBitMapSize: 01 01 00 00 01 01 where the font says 01 00 00 00 01 01) and
        /// its own crossing lists (AddHorizSmartScan hooked: row 45 at x-index 15, not 16).</para>
        /// </summary>
        private static void DropDuplicatePoints(ref Vector2[] pts, ref bool[] on)
        {
            int n = pts.Length;
            if (n < 2) return;
            int dup = 0;
            for (int i = 0; i < n; i++)
                if (pts[(i + 1) % n] == pts[i]) { dup++; break; }
            if (dup == 0) return;
            var p = (Vector2[])pts.Clone();
            var f = (bool[])on.Clone();
            int start = 0, end = n - 1;
            for (int i = 0; i < end; i++)
            {
                if (p[i + 1] != p[i]) continue;
                for (int k = i; k > start; k--) { p[k] = p[k - 1]; f[k] = f[k - 1]; }
                start++;
                f[i + 1] = true;
            }
            if (start != end && p[end] == p[start]) { start++; f[end] = true; }
            pts = p[start..(end + 1)];
            on = f[start..(end + 1)];
        }

        /// <summary>The implied on-curve point between two off-curve ones.
        /// <para>IT IS NOT THE EXACT AVERAGE. fsc_FillGlyph@140034000 walks the contour in 26.6
        /// integers and, wherever two consecutive points are both off-curve, materialises the join
        /// as <c>(a + b + 1) &gt;&gt; 1</c> on BOTH axes before handing the piece to
        /// EvaluateSpline -- an arithmetic shift, so a half lands on the larger value. A midpoint
        /// of two odd 26.6 coordinates is therefore a sixty-fourth to the right of and above the
        /// true middle, never on it, and never the half-sixty-fourth we were keeping.</para>
        /// <para>Half a sixty-fourth of a pixel sounds beneath notice, and would be if it fell
        /// anywhere. It falls on curve JOINS, which is where a bowl's outline is at its flattest
        /// and a whole lamp's worth of samples sits within a sixty-fourth of the edge. Curved
        /// glyphs carry 69% of the residual at 3.3x the per-row error of straight ones, and the
        /// point solver finds the differing point is an implied midpoint far more often than it is
        /// any real point of the outline.</para>
        /// <para>REFUTED, AND THE REASON IS THE OVERSAMPLING. Those 26.6 integers are not
        /// sixty-fourths of a PIXEL: fs_ContourScan multiplies the scan box by the ClearType
        /// overscale before fsc_FillGlyph walks it, so a unit is a sixty-fourth of a sub-column in
        /// x and of a sub-row in y -- 1/384 and 1/320 of a pixel. Every coordinate is then six
        /// times (or five times) a sixty-fourth of a pixel, two of them sum to an even number in
        /// x, and <c>(a + b + 1) &gt;&gt; 1</c> gives back the exact average; in y the tie can
        /// fall, and it moves the point by a six-hundred-and-fortieth of a pixel. So the rule is
        /// real and its effect is nothing. Rounding to a sixty-fourth of a PIXEL instead, as this
        /// did when it was written, is six times too coarse and measures 139,753 -&gt; 270,991.
        /// WPF_CT_MID64=1 turns the coarse version back on.</para></summary>
        /// <para>AND GDI'S OWN NUMBERS SAY IT IS THE EXACT HALF. GetGlyphOutline reports
        /// POINTFX, which is 16.16 and can carry a half of a sixty-fourth; the implied midpoints
        /// in its fitted list ARE halves -- Verdana 'c' at 17ppem comes back with 17.5, 22.5,
        /// 160.5, 410.5, 475.5 and 564.5 sixty-fourths among its coordinates, and every real
        /// point is whole. So GDI materialises the midpoint without rounding it to the
        /// sixty-fourth grid, exactly as the overscale argument above predicts, and any rule that
        /// snaps it is wrong however it measures. WPF_CT_MID64=1 is the ceiling and =even the
        /// round-half-to-even that SolveGdisOutlineXy's integer point arrays impose; both are
        /// kept only so that the solver's rounding can be reproduced on the shipping path, which
        /// is how the two were told apart.</para></summary>
        private static Vector2 Mid(Vector2 a, Vector2 b) => s_midMode switch
        {
            1 => new(HalfUp64(a.X, b.X), -HalfUp64(-a.Y, -b.Y)),
            2 => new(MathF.Round((a.X + b.X) * 32f) / 64f, MathF.Round((a.Y + b.Y) * 32f) / 64f),
            _ => new((a.X + b.X) * 0.5f, (a.Y + b.Y) * 0.5f),
        };

        private static readonly int s_midMode =
            Environment.GetEnvironmentVariable("WPF_CT_MID64") switch
            { "1" => 1, "even" => 2, _ => 0 };

        private static float HalfUp64(float p, float q)
        {
            int i = (int) MathF.Round(p * 64f), j = (int) MathF.Round(q * 64f);
            return ((i + j + 1) >> 1) / 64f;
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
