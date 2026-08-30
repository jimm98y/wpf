// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The TrueType hinting virtual machine: running the FONT'S OWN instructions.
//
// WHY, given there is already a grid fitter next door. GlyphHinter derives where a glyph's edges
// should go by analysing its outline, which is FreeType's autofitter approach and is what a face
// without hints needs. It gets close. It cannot get closer than close, because the answers Windows
// gives are not derived from anything -- they are a program the face's designer wrote, glyph by
// glyph, and where a judgement call was made no analysis recovers it. Segoe UI's '0' at nine point
// has its left stem exactly half a pixel from the origin: a dead tie between two columns that no
// rounding rule settles, and the face settles by hand.
//
// So this executes that program. A TrueType face carries three streams of bytecode -- 'fpgm' (the
// function library, run once), 'prep' (run whenever the size changes), and one per glyph -- written
// for a stack machine with 26.6 fixed-point arithmetic, a control-value table of the designer's
// reference measurements, and a graphics state that says which direction to measure in and how to
// round. Running them is what GDI does, so running them is how our glyphs come out where Windows
// puts them.
//
// WHAT IT IS FAITHFUL TO. FreeType's interpreter in its "v35" mode, which is the classic full
// hinting Windows' GDI does: both axes moved, nothing suppressed. (FreeType's default "v40" mode
// deliberately drops horizontal moves to imitate DirectWrite. That is the wrong target here --
// measured, GDI puts Segoe UI's 'm' stems on single columns, fully covered, so x is hinted and
// hinted hard.) GETINFO reports version 35 and greyscale rendering, which is what GDI reports when
// it is asked for ANTIALIASED_QUALITY.
//
// WHERE IT SITS. TrueTypeFont asks for this first. A face with no 'fpgm'/'prep'/glyph instructions
// -- or one whose program faults -- falls back to GlyphHinter, so nothing regresses for faces that
// carry no hints of their own.
//
// WHERE THE DIFFICULTY ACTUALLY IS, because it is not where it looks. Getting the two hundred
// instructions right is the easy half; they are written down. The hard half is that this is
// fixed-point arithmetic, and every one of the faults that survived into a rendered glyph was a
// rounding rule off by a 64th of a pixel:
//
//   * adding a half of the same sign and then SHIFTING, which floors, so a negative number moved a
//     whole step too far and -149 exactly came back as -150;
//   * MUL rounds to the nearest 64th and DIV does not, and making them agree is a regression;
//   * one scale used both to turn font units into pixels and to measure a composite's original
//     outline, which is already in pixels.
//
// None of those is visible in a glyph on its own. Each becomes a whole pixel only when the value it
// corrupted lands near a rounding boundary, which happens at one size and not its neighbours -- so
// the way to find them is a test that sweeps every size, and the way to lose them again is a test
// that checks a few. See WindowsGlyphParityTests.
//

using System;

namespace Microsoft.Wpf.Interop.WebGpu.Composition.Text
{
    /// <summary>A glyph in the shape the interpreter works on: every point of every contour in one
    /// array, the four phantom points after them, and the glyph's own instruction stream.</summary>
    internal sealed class GlyphProgram
    {
        public int[] X = Array.Empty<int>();          // font units, y up -- 26.6 pixels when Composite
        public int[] Y = Array.Empty<int>();
        public bool[] OnCurve = Array.Empty<bool>();
        public int[] EndPoints = Array.Empty<int>();  // last point index of each contour
        public byte[] Instructions = Array.Empty<byte>();
        public int PointCount;                        // real points; the array holds four more

        /// <summary>An accented letter and the like: this glyph's points are its components' points,
        /// each of which has ALREADY been hinted, so they arrive in 26.6 pixels rather than in font
        /// units and must not be scaled again.
        /// <para>A composite's own program is written against those hinted positions -- it nudges an
        /// accent that is already on the grid -- so the interpreter measures "where the point
        /// started" from them too, and its scale is the identity for the duration. Feeding it the
        /// UNHINTED positions instead is the other reading of the same rule, and it is wrong:
        /// measured against Windows it turned twenty-one disagreeing accents into seventy-four.
        /// </para></summary>
        public bool Composite;
    }

    internal sealed partial class TrueTypeInterpreter
    {
        // ---- what the face brings ------------------------------------------------------------

        private readonly byte[] _data;
        private readonly int _unitsPerEm;
        private readonly byte[] _fontProgram;
        private readonly byte[] _controlProgram;
        private readonly short[] _controlValues;      // 'cvt', font units

        private readonly int[] _storage;
        private readonly Function[] _functions;
        private readonly Function[] _instructionDefs = new Function[256];
        private readonly int _maxStack;
        private readonly int _twilightPoints;

        private readonly struct Function
        {
            public readonly byte[] Code;
            public readonly int Start;                // where the body begins, past the FDEF
            public readonly bool Defined;
            public Function(byte[] code, int start) { Code = code; Start = start; Defined = true; }
        }

        /// <summary>True when the face carries hinting worth running.</summary>
        public bool IsUsable { get; }

        public TrueTypeInterpreter(byte[] data, int unitsPerEm, byte[] fontProgram,
                                   byte[] controlProgram, short[] controlValues,
                                   int maxStorage, int maxFunctionDefs, int maxStack,
                                   int twilightPoints)
        {
            _data = data;
            _unitsPerEm = unitsPerEm > 0 ? unitsPerEm : 1000;
            _fontProgram = fontProgram;
            _controlProgram = controlProgram;
            _controlValues = controlValues;

            // The face declares its own limits in 'maxp'. Trusting them exactly means a face that
            // understates one drives us off the end of an array, so they are floors, not sizes.
            _storage = new int[Math.Max(64, maxStorage + 8)];
            _functions = new Function[Math.Max(64, maxFunctionDefs + 8)];
            _maxStack = Math.Max(256, maxStack + 32);
            _twilightPoints = Math.Max(16, twilightPoints + 4);

            IsUsable = fontProgram.Length > 0 || controlProgram.Length > 0 || controlValues.Length > 0;
        }

        // ---- fixed point ------------------------------------------------------------------------
        //
        // Two formats throughout, and mixing them up is the whole difficulty of reading this code:
        // 26.6 for distances on the pixel grid (64 = one pixel), and 2.14 for the unit vectors that
        // say which way to measure (0x4000 = one). Scales are 16.16.

        // Rounding a negative number is where this is easy to get wrong, and getting it wrong is
        // invisible until a glyph lands a whole pixel out. The rule throughout: add half A HALF OF
        // THE SAME SIGN, then throw away the fraction TOWARDS ZERO -- which is what dividing does in
        // C#, and what FreeType does. Shifting instead of dividing rounds towards minus infinity, so
        // a negative number gets the half added AND the fraction rounded away from zero, and moves a
        // whole step too far: -149 exactly, needing no rounding at all, came back as -150. Every
        // point below the baseline went through that, and it was worth a pixel wherever the value it
        // corrupted then landed on a rounding boundary -- the underscore, one row low at seventeen
        // pixels an em, was the plainest case.

        internal static int MulFix(int a, int b)
        {
            long v = (long)a * b;
            return (int)((v + (v >= 0 ? 0x8000 : -0x8000)) / 0x10000);
        }

        /// <summary>a*b/c, rounded to nearest, and sign-symmetric: the magnitudes are what is
        /// divided, so a negative c cannot turn the rounding round the other way.</summary>
        internal static int MulDiv(int a, int b, int c)
        {
            if (c == 0) return 0;
            long v = (long)a * b;
            int sign = 1;
            if (v < 0) { v = -v; sign = -sign; }
            long d = c;
            if (d < 0) { d = -d; sign = -sign; }
            long result = (v + d / 2) / d;
            return (int)(sign > 0 ? result : -result);
        }

        internal static int DivFix(int a, int b)
        {
            if (b == 0) return a >= 0 ? int.MaxValue : int.MinValue;

            int sign = 1;
            long n = a;
            if (n < 0) { n = -n; sign = -sign; }
            long d = b;
            if (d < 0) { d = -d; sign = -sign; }

            long q = ((n << 16) + d / 2) / d;
            return (int)(sign > 0 ? q : -q);
        }

        /// <summary>The 2.14 dot product two unit vectors and a displacement make: how far the
        /// displacement reaches along the direction, in the displacement's own units.</summary>
        private static int DotFix14(int x, int y, int ux, int uy)
        {
            long v = (long)x * ux + (long)y * uy;
            return (int)((v + (v >= 0 ? 0x2000 : -0x2000)) / 0x4000);
        }

        private static int Pix(int value) => (value + 32) & ~63;          // to the nearest whole pixel
        private static int Floor(int value) => value & ~63;
        private static int Ceil(int value) => (value + 63) & ~63;

        /// <summary>A 2.14 unit vector from an arbitrary one, which is what the vector-setting
        /// instructions need before they can store it.
        /// <para>By way of a 16.16 unit vector, then divided by four, because that is how the
        /// reference implementation does it and the last two bits of the answer decide which side of
        /// a pixel boundary a point on a long diagonal lands.</para></summary>
        private static void Normalize(int x, int y, out int nx, out int ny)
        {
            if (x == 0 && y == 0) { nx = 0x4000; ny = 0; return; }

            double length = Math.Sqrt((double)x * x + (double)y * y);
            if (length <= 0.0) { nx = 0x4000; ny = 0; return; }

            long ux = (long)Math.Round(x * 65536.0 / length);
            long uy = (long)Math.Round(y * 65536.0 / length);
            nx = (int)(ux / 4);          // toward zero, as the integer division there does
            ny = (int)(uy / 4);
        }

        // ---- the zones --------------------------------------------------------------------------

        private const byte TagOn = 0x01;
        private const byte TagTouchX = 0x02;
        private const byte TagTouchY = 0x04;
        private const byte TagTouchBoth = TagTouchX | TagTouchY;

        /// <summary>A set of points the machine can move. Zone 1 is the glyph; zone 0 is the
        /// "twilight", a scratch area of points that belong to no contour, which a program uses to
        /// build reference positions out of thin air.</summary>
        private sealed class Zone
        {
            public int[] CurX, CurY;      // where the points are now, 26.6
            public int[] OrgX, OrgY;      // where they started, scaled, 26.6
            public int[] OrusX, OrusY;    // where they started, unscaled -- font units
            public byte[] Tags;
            public int[] Contours;
            public int PointCount;

            public Zone(int points, int contours)
            {
                CurX = new int[points]; CurY = new int[points];
                OrgX = new int[points]; OrgY = new int[points];
                OrusX = new int[points]; OrusY = new int[points];
                Tags = new byte[points];
                Contours = new int[Math.Max(1, contours)];
                PointCount = points;
            }
        }

        private Zone _glyphZone = new(4, 1);
        private Zone _twilight = new(16, 1);

        // ---- the graphics state -------------------------------------------------------------------

        private struct GraphicsState
        {
            public int ProjX, ProjY;      // 2.14, the direction distances are MEASURED along
            public int FreeX, FreeY;      // 2.14, the direction points are MOVED along
            public int DualX, DualY;      // 2.14, measured against the ORIGINAL outline
            public int Rp0, Rp1, Rp2;
            public int Zp0, Zp1, Zp2;
            public int Loop;
            public int MinimumDistance;   // 26.6
            public int ControlValueCutIn; // 26.6
            public int SingleWidthCutIn;  // 26.6
            public int SingleWidthValue;  // 26.6
            public int DeltaBase, DeltaShift;
            public bool AutoFlip;
            public int RoundPeriod, RoundPhase, RoundThreshold;   // 26.6
            public RoundMode Round;
            public int ScanControl, ScanType, InstructControl;
        }

        private enum RoundMode
        {
            ToGrid, ToHalfGrid, ToDoubleGrid, DownToGrid, UpToGrid, Off, Super, Super45,
        }

        private GraphicsState _gs;
        private GraphicsState _prepState;      // what 'prep' left behind, restored per glyph
        private bool _prepRun;
        private bool _inPreProgram;
        private bool _inComposite;
        private bool _iupDone;
        private bool _prepClearType;
        private bool _prepBiLevel;

        /// <summary>Run this hint with the BI-LEVEL rules -- physical grid, full cut-in, full minimum
        /// distance, every delta applied -- whatever the ClearType defaults say.
        /// <para>Set around a second hinting pass, so a glyph can be fitted both ways and the two
        /// results compared. See TrueTypeFont's left-edge transfer.</para></summary>
        [ThreadStatic] internal static bool BiLevelPass;
        private float _prepPpem = -1f;

        private int _scale;                    // 16.16: font units -> 26.6 pixels

        /// <summary>The scale that turns a distance measured in the ORIGINAL outline into pixels.
        /// The same as <see cref="_scale"/> for an ordinary glyph, and the identity for a composite,
        /// whose original outline is its components' fitted one and is in pixels already.</summary>
        private int _measureScale;
        private int _ppem;
        private int _pointSize;
        private int _dotProduct;               // freedom . projection, 2.14

        private int[] _stack = Array.Empty<int>();
        private int _top;

        // A program that will not terminate must not hang the renderer. Segoe UI's longest glyph
        // program is a few thousand steps; a hundred thousand is far past anything legitimate.
        //
        // Counted PER PROGRAM, and it has to be: left running across glyphs it is not a guard on a
        // runaway loop, it is a quota on how much text may be drawn before every glyph starts
        // failing and falling back to the analysis. Which is what happened -- '&' and '@' come late
        // enough in a page to be over the line, and came out visibly heavier than Windows draws
        // them because they were never hinted at all.
        private const int StepLimit = 200000;
        private int _steps;

        private bool _faulted;

        // ---- running ------------------------------------------------------------------------------

        /// <summary>Run the face's hinting over one glyph at one size. The glyph's points come in as
        /// font units and go out as 26.6 pixels, y still up. False means the face's program faulted
        /// and the caller should fit the glyph some other way.</summary>
        public bool Hint(GlyphProgram glyph, float pixelsPerEm)
        {
            // The paper exempts composites from the delta rules: there a point flagged untouched may
            // have been touched while its component ran, so a delta moves the whole outline instead
            // of denting it -- which is how diacritics keep their distance from the base glyph.
            _inComposite = glyph.Composite;
            _iupDone = false;
            if (!IsUsable || pixelsPerEm <= 0f) return false;

            try
            {
                if (!PrepareSize(pixelsPerEm)) return false;
                LoadGlyph(glyph);
                _steps = 0;

                // A composite's "where the point started" is already in pixels, so the scale that
                // turns such a measurement into pixels is the identity for it -- and ONLY for it.
                // The size's real scale goes on being the size's real scale: a program that writes a
                // control value in font units (WCVTF) or sets the single-width (SSW) means font
                // units whatever glyph it is in. Using one scale for both was worth eighteen of the
                // twenty-one accents that disagreed with Windows -- Segoe UI's accented letters
                // write a control value and read it straight back to work out how far to slide the
                // accent, and with the identity they read 39 font units as 39 PIXELS.
                _measureScale = glyph.Composite ? 1 << 16 : _scale;

                if (glyph.Instructions.Length > 0)
                {
                    _gs = _prepState;
                    _gs.Loop = 1;
                    _gs.Rp0 = _gs.Rp1 = _gs.Rp2 = 0;
                    _gs.Zp0 = _gs.Zp1 = _gs.Zp2 = 1;

                    // AND THE VECTORS, back to the x axis. They do not carry over from 'prep' -- the
                    // spec resets them before every program and so does every other interpreter --
                    // and inheriting them is not a small error: whatever axis prep happened to finish
                    // on became the axis the glyph's first instructions moved points along.
                    //
                    // This is what garbled text in most faces. Arial's prep leaves the vector on Y,
                    // so its 'l' met an ALIGNRP meant to line the stem up SIDEWAYS and flattened it
                    // instead -- the top point went from 11.45 pixels to 1, which is exactly the
                    // collapse the fitted outlines showed. Segoe UI's prep happens to finish on x,
                    // which is the only reason the one face this hinter was measured against looked
                    // right.
                    _gs.ProjX = _gs.FreeX = _gs.DualX = 0x4000;
                    _gs.ProjY = _gs.FreeY = _gs.DualY = 0;
                    ResetProjection();

                    // The dump covers the GLYPH's own program only: the font program and prep run
                    // hundreds of instructions that are the same for every glyph and drown it.
                    bool dumping = TrueTypeInterpreter.s_dumpGlyph;
                    _dumpActive = dumping;
                    try { if (!Execute(glyph.Instructions, 0)) return false; }
                    finally { _dumpActive = false; }
                }

                StoreGlyph(glyph);
                return true;
            }
            catch (Exception e) when (e is IndexOutOfRangeException or ArgumentOutOfRangeException
                                           or DivideByZeroException or OverflowException)
            {
                // A face whose program is wrong for this interpreter is not a reason to draw
                // nothing: the caller falls back to fitting the outline by analysis.
                return false;
            }
        }


        /// <summary>Font units to 26.6 pixels at the size last prepared -- what a composite's
        /// component offsets have to be put through before they can be added to points that are
        /// already fitted.</summary>
        internal int ScaleToPixels(int fontUnits) => MulFix(fontUnits, _scale);

        /// <summary>A 26.6 distance rounded to a whole pixel, as the rasterizer rounds it.</summary>
        internal static int RoundToPixel(int f26d6) => Pix(f26d6);

        /// <summary>Make the size's programs ready without hinting anything, so a caller can scale
        /// with <see cref="ScaleToPixels"/> before it has a glyph to run.</summary>
        /// <summary>Whether GETINFO should say a CLEARTYPE rasterizer is asking.
        /// <para>A face's own program may branch on this and fit x differently -- that is the
        /// mechanism by which GDI's two modes can put the outline in different places without
        /// anything outside the font deciding to discard a movement.</para></summary>
        /// <summary>Whether x distances round on the lamp grid rather than the pixel grid.
        /// <para>MEASURED AND REJECTED, and the way it fails is worth keeping. On ONE glyph at ONE
        /// size it looks like the answer: Segoe UI's 'l' at 11ppem comes out lamp-for-lamp on GDI's,
        /// 0/0/58/148/255/199/100/22 against 0/0/73/153/255/197/111/36, where leaving x alone puts
        /// the stem a lamp left and never saturates a lamp. Over the whole repertoire and four faces
        /// it is TEN TIMES WORSE: structural 2598 against 274. The structural metric is the one that
        /// is blind to the contrast curve, so that is a statement about geometry and it is not
        /// close.</para>
        /// <para>So GDI's ClearType geometry really is near enough x-unhinted, and a single stem
        /// agreeing is not evidence -- this is the same narrow-probe trap that has now caught three
        /// separate x-fitting attempts.</para></summary>
        /// <summary>Whether the glyph is being fitted in a space stretched three times in x --
        /// GDI's own ClearType space, reachable through a stretched MAT2 and measured by stage D.
        /// <para>The outline arrives with x already tripled; what the interpreter has to add is the
        /// CONTROL VALUES, which are stem widths and so must be tripled too when they are applied
        /// along x. Everything else follows: a program rounding to the whole pixel it thinks it is
        /// on is rounding to a third of a real one, and each stem lands on a lamp.</para></summary>
        internal static bool XSpace3x => TrueTypeFont.XHintMode == 11;

        /// <summary>Whether x movement is refused AS THE PROGRAM RUNS rather than undone after.</summary>
        internal static bool XSuppress =>
            TrueTypeFont.XHintMode == 12 && TrueTypeFont.SubpixelFitting;

        internal static bool XThirdGrid =>
            TrueTypeFont.XHintMode == 6 || TrueTypeFont.XHintMode == 13
            || TrueTypeFont.XHintMode == 14;

        /// <summary>Whether an UNROUNDED MIRP takes its distance from the OUTLINE rather than the
        /// control value, on the x axis.
        /// <para>The character maps say GDI's stem is about one pixel: at 12ppem its 'H' is a
        /// saturated column and a faint neighbour. Traced, the control value for that stem is 1.25
        /// to 1.63 pixels across 11..14ppem while the OUTLINE distance is 0.875 to 1.11 -- about a
        /// pixel, which is what GDI draws. Mode 6 already puts the stems on Windows' own columns and
        /// is only too WIDE; this is the width to pair it with.</para></summary>
        /// <summary>Whether an UNROUNDED MIRP takes its distance from the OUTLINE rather than the
        /// control value, on the x axis.
        /// <para>Mode 16 pairs this with the program's OWN x fitting, and that pairing is what the
        /// instruction trace argues for. Segoe UI's 'l' does `MDAP[r]` on the stem's left edge --
        /// round this position to the grid, which is what puts a GDI stem on a pixel boundary and is
        /// the whole of the alignment -- and then `MIRP 0xE1`, whose round bit is off, for the other
        /// edge. Running the program in full (mode 5) therefore gets the POSITION right and only the
        /// WIDTH wrong, because that control value is 1.25-1.63px across 11..14ppem where the
        /// outline distance is 0.875-1.11 and GDI draws about a pixel. So keep the fitting and
        /// change only where the width comes from.</para></summary>
        internal static bool XOutlineWidths =>
            TrueTypeFont.XHintMode == 14 || TrueTypeFont.XHintMode == 16;


        /// <summary>Whether only COORDINATES round on the lamp grid, distances keeping the pixel
        /// grid: widths as GDI's greyscale has them, positions as ClearType places them.</summary>
        internal static bool XThirdPositions => TrueTypeFont.XHintMode == 7 || TrueTypeFont.XHintMode == 8;

        /// <summary>Whether a control value spent on an x distance is a WHOLE number of pixels.
        /// <para>Mode 13 pairs this with the third-pixel grid, and the pairing is what the character
        /// maps ask for: at 12ppem mode 6 already puts 'H's two stems on Windows' own columns, 3 and
        /// 8, and the only thing wrong with it is that each stem carries a spurious fringe -- ours
        /// `@=` and `:@.` against Windows' `@.` and `@.` -- because MIRP 0xE1 applies the scaled
        /// control value unrounded and the stem comes out a lamp wide of GDI's.</para></summary>
        internal static bool XPixelWidths =>
            TrueTypeFont.XHintMode == 8 || TrueTypeFont.XHintMode == 13;

        /// <summary>Whether GETINFO says a CLEARTYPE rasterizer is asking. ON, because this port
        /// draws ClearType -- which is what GDI answers when its DC carries a ClearType font.
        /// <para>THIS WAS THE INTERPRETER'S BIGGEST FIDELITY BUG. We reported greyscale, because
        /// answering ClearType had been tried once and judged on finished pixels with x discarded --
        /// which throws away the only thing the answer changes. Compared where it acts, on the
        /// fitted x COORDINATES against GDI's own over the repertoire:</para>
        /// <para>Segoe UI at 13ppem, 2 of 62 glyphs matched GDI exactly and the rest were 373 pixels
        /// out in total; answering ClearType makes it 61 of 62 and 0.0 pixels. 11ppem goes 11 -> 59,
        /// Consolas at 16ppem 8 -> 58. Arial does not branch on it and does not move. The mechanism
        /// is in the face's own 'prep': told ClearType, Segoe UI sets its stem control value to
        /// exactly 1.0 pixel where the greyscale branch leaves it at 1.25..1.94 across 10..20ppem --
        /// and GDI's stem measures 1.0 at every one of those sizes.</para>
        /// <para>Do NOT tie this to TrueTypeFont.SubpixelFitting. They agree at render time, but
        /// SubpixelFitting is also a knob the stage tests turn off to look at a full fit, so the
        /// answer a face got would depend on which test ran last. What GETINFO reports is a property
        /// of the RASTERIZER.</para></summary>
        /// <summary>Whether the instruction now running is in the CLEARTYPE DIRECTION -- the axis
        /// with the extra resolution, which for us is always x.
        /// <para>Microsoft's "TrueType and ClearType" paper is explicit about what changes there:
        /// "you can imagine a grid with sixteen closely spaced grid lines per pixel in the
        /// x-direction, and one gridline per pixel in the y-direction. Rounding instructions ... now
        /// apply to this virtual grid as do MDAP[R], MIAP[R], MDRP[...R...], and ROUND[...]" -- and
        /// in the y direction "the behavior is exactly how it was with bi-level".</para></summary>
        /// <summary>Whether the face has asked for the instruction set as SPECIFIED, turning off the
        /// backwards-compatibility behaviour ClearType applies to older fonts.
        /// <para>"The INSTCTRL instruction with selector flag three and a value of TRUE (4) should be
        /// used in new fonts and existing instructed fonts that have been verified to work correctly
        /// in the ClearType environment ... When the INSTCTRL ClearType instruction is used it returns
        /// instructions to the behavior as described in the TrueType instruction specification."</para>
        /// <para>Checked: Segoe UI, Arial and Consolas all leave it clear, so the compatibility rules
        /// really do apply to them -- which is worth knowing, because those rules are what the delta
        /// suppression rests on.</para></summary>
        internal bool NativeClearTypeMode => (_gs.InstructControl & 4) != 0;

        internal bool InClearTypeDirection =>
            ClearTypeInfo && IsHorizontalProjection && !_inPreProgram;

        /// <summary>Grid lines per pixel along the ClearType direction. SIXTEEN, from the paper --
        /// not three. A lamp is a third of a pixel and that is what gets DRAWN; the grid the
        /// interpreter ROUNDS against is finer still, and guessing thirds here is what made every
        /// earlier attempt at this quantise stroke weights that GDI leaves alone.</summary>
        internal const int ClearTypeGrid = 16;

        /// <summary>Whether a POSITION rounds on the physical grid instead of the virtual one. OFF.
        /// <para>And the measurement behind it is the most decisive one in this file. Rounding
        /// positions on the whole-pixel grid makes our fitted outline match the one GetGlyphOutline
        /// reports EXACTLY -- 'o' at 11ppem comes back 1 / 1.672 / 1.703 / 2 / 2.344 / 2.406 ... on
        /// both sides, digit for digit -- and the RENDERED text then gets 87% worse: the text
        /// specimen goes 1,074,897 to 2,013,995.</para>
        /// <para>So it is no longer an inference that ClearType draws a different outline from the
        /// one GGO reports. We can reproduce GGO's outline on demand, and doing so nearly doubles the
        /// error against what GDI actually puts on the screen. Stop trying to match the outline API.
        /// WPF_CT_POSGRID=physical turns it on for anyone who wants to see it.</para></summary>
        /// <summary>Grid for POSITIONS specifically, when it is not the virtual one. WPF_CT_POSGRID
        /// takes a number here -- 3 puts them on the lamp grid, which is the resolution the text is
        /// actually DRAWN at, as opposed to the sixteenth the program rounds against.</summary>
        /// <summary>WPF_CT_DISTGRID: the grid DISTANCES round on in the ClearType direction, as a
        /// divisor of a pixel (1 = whole pixels). 0 leaves them on the virtual grid.</summary>
        private static readonly int s_distanceGrid =
            Environment.GetEnvironmentVariable("WPF_CT_DISTGRID") == "physical" ? 1
            : int.TryParse(Environment.GetEnvironmentVariable("WPF_CT_DISTGRID"), out int dg) ? dg : 0;

        /// <summary>WPF_CT_STEMSNAP: distances at or below this many 64ths of a pixel round on the
        /// physical grid. 0 disables it.</summary>
        private static readonly int s_stemSnap =
            int.TryParse(Environment.GetEnvironmentVariable("WPF_CT_STEMSNAP"), out int ss) ? ss : 0;

        private static readonly int s_positionGrid =
            int.TryParse(Environment.GetEnvironmentVariable("WPF_CT_POSGRID"), out int pg) ? pg : 0;

        private static readonly bool s_positionsOnPhysicalGrid =
            Environment.GetEnvironmentVariable("WPF_CT_POSGRID") == "physical";

        internal static bool ClearTypeInfo =
            Environment.GetEnvironmentVariable("WPF_CT_INFO") != "0";

        internal bool PrepareForSize(float pixelsPerEm) => IsUsable && pixelsPerEm > 0f && PrepareSize(pixelsPerEm);

        /// <summary>Scale the control values and run 'fpgm' and 'prep'. Both are kept from one glyph
        /// to the next -- 'fpgm' defines the face's functions once, and 'prep' is what makes the
        /// state the glyphs start from, so re-running it per glyph would be pure cost.</summary>
        private bool PrepareSize(float pixelsPerEm)
        {
            int ppem = (int)MathF.Round(pixelsPerEm);
            if (ppem <= 0) return false;
            // 'prep' is cached by SIZE, and that is not enough: the face branches on what GETINFO
            // says, and Segoe UI's prep sets a different stem control value for ClearType than for
            // greyscale. Cached by ppem alone, whichever mode asked first fixed the control values
            // for every glyph afterwards -- so switching the answer appeared to do nothing at all.
            bool clearType = ClearTypeInfo;
            if (_prepRun && Math.Abs(_prepPpem - pixelsPerEm) < 0.001f && _prepClearType == clearType
                && _prepBiLevel == BiLevelPass)
                return !_faulted;

            _prepPpem = pixelsPerEm;
            _prepClearType = clearType;
            _prepBiLevel = BiLevelPass;
            _prepRun = true;
            _faulted = false;
            _ppem = ppem;
            _pointSize = ppem * 72 / 96;
            _scale = DivFix(ppem << 6, _unitsPerEm);
            _measureScale = _scale;

            _stack = new int[_maxStack];
            _top = 0;
            _steps = 0;
            Array.Clear(_storage);
            _twilight = new Zone(_twilightPoints, 1);

            ScaleControlValues();
            ResetGraphicsState();

            // "ROUND in PreProgram ... all rounding is done to the physical grid while in the
            // pre-program" -- Microsoft, TrueType and ClearType. Some faces round numbers there
            // without setting the vectors, and on the virtual grid that misplaces horizontal strokes.
            _inPreProgram = true;
            try
            {
                if (_fontProgram.Length > 0)
                {
                    Array.Clear(_functions);
                    Array.Clear(_instructionDefs);
                    if (!Execute(_fontProgram, 0)) { _faulted = true; return false; }
                }

                _top = 0;
                ResetGraphicsState();
                if (_controlProgram.Length > 0 && !Execute(_controlProgram, 0)) { _faulted = true; return false; }
            }
            finally { _inPreProgram = false; }

            _prepState = _gs;
            return true;
        }

        private int[] _scaledCvt = Array.Empty<int>();

        private void ScaleControlValues()
        {
            if (_scaledCvt.Length != _controlValues.Length)
                _scaledCvt = new int[_controlValues.Length];
            for (int i = 0; i < _controlValues.Length; i++)
                _scaledCvt[i] = MulFix(_controlValues[i], _scale);
        }

        private void ResetGraphicsState()
        {
            _gs = default;
            _gs.ProjX = _gs.FreeX = _gs.DualX = 0x4000;
            _gs.ProjY = _gs.FreeY = _gs.DualY = 0;
            _gs.Loop = 1;
            _gs.Zp0 = _gs.Zp1 = _gs.Zp2 = 1;
            _gs.MinimumDistance = 64;
            _gs.ControlValueCutIn = 68;        // 17/16 of a pixel, the specified default
            _gs.SingleWidthCutIn = 0;
            _gs.SingleWidthValue = 0;
            _gs.DeltaBase = 9;
            _gs.DeltaShift = 3;
            _gs.AutoFlip = true;
            _gs.Round = RoundMode.ToGrid;
            _gs.ScanControl = 0;
            _gs.ScanType = 0;
            _gs.InstructControl = 0;
            ResetProjection();
        }

        private void ResetProjection()
        {
            _dotProduct = (int)(((long)_gs.ProjX * _gs.FreeX + (long)_gs.ProjY * _gs.FreeY) >> 14);
            if (_dotProduct == 0) _dotProduct = 0x4000;
        }

        // ---- the glyph in and out -----------------------------------------------------------------

        private void LoadGlyph(GlyphProgram glyph)
        {
            int n = glyph.PointCount + 4;
            if (_glyphZone.CurX.Length < n || _glyphZone.Contours.Length < glyph.EndPoints.Length)
                _glyphZone = new Zone(n, glyph.EndPoints.Length);
            Zone z = _glyphZone;
            z.PointCount = n;

            for (int i = 0; i < n; i++)
            {
                // A composite arrives already fitted and already in pixels. Scaling it a second time
                // would shrink the accent to nothing; and "where it started", for a program that is
                // written to nudge a fitted accent, IS where the fitting left it -- so the original
                // and the current positions are one and the same, in both the scaled and the
                // unscaled reading of them. The scale is the identity to match, which is what makes
                // a distance measured against the unscaled original come back unchanged.
                if (glyph.Composite)
                {
                    z.OrusX[i] = z.OrgX[i] = z.CurX[i] = glyph.X[i];
                    z.OrusY[i] = z.OrgY[i] = z.CurY[i] = glyph.Y[i];
                }
                else
                {
                    z.OrusX[i] = glyph.X[i];
                    z.OrusY[i] = glyph.Y[i];
                    z.OrgX[i] = z.CurX[i] = MulFix(glyph.X[i], _scale);
                    z.OrgY[i] = z.CurY[i] = MulFix(glyph.Y[i], _scale);
                }

                // Assigning the tags is also what UNTOUCHES the points. A component's own program
                // will have touched some of them; the composite's program is entitled to move them
                // again, and IUP must not treat them as already placed.
                z.Tags[i] = glyph.OnCurve[i] ? TagOn : (byte)0;
            }

            // The two horizontal phantom points are rounded to the grid before hinting starts, the
            // way the rasterizer does it: a face's program reads them to find its own side bearings.
            z.CurX[glyph.PointCount] = Pix(z.CurX[glyph.PointCount]);
            z.CurX[glyph.PointCount + 1] = Pix(z.CurX[glyph.PointCount + 1]);

            for (int i = 0; i < glyph.EndPoints.Length; i++)
                z.Contours[i] = glyph.EndPoints[i];
            _contourCount = glyph.EndPoints.Length;
            _realPoints = glyph.PointCount;
        }

        private int _contourCount;
        private int _realPoints;

        private void StoreGlyph(GlyphProgram glyph)
        {
            Zone z = _glyphZone;
            for (int i = 0; i < glyph.PointCount + 4; i++)
            {
                glyph.X[i] = z.CurX[i];
                glyph.Y[i] = z.CurY[i];
                // FLIPPT and FLIPRG may have changed which points the curve passes THROUGH, and a
                // glyph rebuilt from the old flags is a different shape from the one just hinted.
                glyph.OnCurve[i] = (z.Tags[i] & TagOn) != 0;
            }
        }

        private Zone ZoneOf(int which) => which == 0 ? _twilight : _glyphZone;

        // ---- measuring and moving -------------------------------------------------------------------

        private int Project(int dx, int dy) => DotFix14(dx, dy, _gs.ProjX, _gs.ProjY);

        /// <summary>Whether distances are currently measured along x rather than y.</summary>
        private bool IsHorizontalProjection
            => (_gs.ProjX < 0 ? -_gs.ProjX : _gs.ProjX) > (_gs.ProjY < 0 ? -_gs.ProjY : _gs.ProjY);

        /// <summary>Whether the FREEDOM vector points along the ClearType direction.
        /// <para>Which vector a rule keys on is not a detail. The rounding rules are about the
        /// projection vector -- that is the axis a distance is measured along -- but the paper's
        /// delta rules are explicitly about the other one: "a backward compatible mode for ClearType
        /// when dealing with instructions using the ClearType direction FOR THE FREEDOM VECTOR".
        /// A DELTAP or SHPIX moves a point along freedom, so that is the vector that decides whether
        /// the move is the sloppy bi-level pixel-flip ClearType throws away.</para>
        /// <para>It matters most for DIAGONALS, which is where our worst glyphs are: M, W, N, m, w.
        /// A diagonal control sets a freedom vector that is not the x axis, and a delta along it is
        /// not a ClearType-direction delta at all.</para></summary>
        private bool IsHorizontalFreedom
            => (_gs.FreeX < 0 ? -_gs.FreeX : _gs.FreeX) > (_gs.FreeY < 0 ? -_gs.FreeY : _gs.FreeY);

        /// <summary>The delta rules' version of <see cref="InClearTypeDirection"/>, on freedom.</summary>
        internal bool DeltaInClearTypeDirection =>
            ClearTypeInfo && IsHorizontalFreedom && !_inPreProgram;

        private int DualProject(int dx, int dy) => DotFix14(dx, dy, _gs.DualX, _gs.DualY);

        /// <summary>How far apart two points are NOW, measured along the projection vector.</summary>
        private int MeasureCurrent(int zoneA, int a, int zoneB, int b)
        {
            Zone za = ZoneOf(zoneA), zb = ZoneOf(zoneB);
            if (a >= za.PointCount || b >= zb.PointCount) return 0;
            return Project(za.CurX[a] - zb.CurX[b], za.CurY[a] - zb.CurY[b]);
        }

        /// <summary>How far apart two points were in the ORIGINAL outline, on the pixel grid --
        /// the scaled starting positions, before anything moved them. What MDRP and MIRP measure.
        /// </summary>
        private int MeasureOriginal(int zoneA, int a, int zoneB, int b)
        {
            Zone za = ZoneOf(zoneA), zb = ZoneOf(zoneB);
            if (a >= za.PointCount || b >= zb.PointCount) return 0;
            return DualProject(za.OrgX[a] - zb.OrgX[b], za.OrgY[a] - zb.OrgY[b]);
        }

        /// <summary>The same distance taken from the outline in FONT UNITS and scaled afterwards,
        /// which is a hair more exact -- the points were rounded to the grid on the way in, and this
        /// has not been. Only MD asks for it, and only outside the twilight zone, whose points were
        /// never in the outline to begin with.</summary>
        private int MeasureOriginalExact(int zoneA, int a, int zoneB, int b)
        {
            Zone za = ZoneOf(zoneA), zb = ZoneOf(zoneB);
            if (a >= za.PointCount || b >= zb.PointCount) return 0;
            if (zoneA == 0 || zoneB == 0)
                return DualProject(za.OrgX[a] - zb.OrgX[b], za.OrgY[a] - zb.OrgY[b]);

            return MulFix(DualProject(za.OrusX[a] - zb.OrusX[b], za.OrusY[a] - zb.OrusY[b]), _measureScale);
        }

        /// <summary>Move a point by <paramref name="distance"/> ALONG THE PROJECTION vector, which
        /// means shifting it along the freedom vector by however much that takes.</summary>
        private void MovePoint(Zone zone, int point, int distance, bool touch = true)
        {
            if (point < 0 || point >= zone.PointCount) return;

            if (_gs.FreeX != 0)
            {
                // SUPPRESS THE MOVE, don't undo it afterwards. What we ship runs the program in full
                // and then puts x back where the scaled outline had it; this refuses the x movement
                // as it happens, so everything the program decides AFTER it -- reference-point
                // distances, IUP's interpolation of untouched points, any conditional that measures
                // where a point ended up -- sees the unfitted x, as it would in a rasterizer that
                // never fits x at all. The two are not the same run, and only the second is what
                // "x is not hinted" actually means. (FreeType calls this its v40 mode.)
                if (!XSuppress)
                {
                    zone.CurX[point] += MulDiv(distance, _gs.FreeX, _dotProduct);
                    if (touch) zone.Tags[point] |= TagTouchX;
                }
            }
            if (_gs.FreeY != 0)
            {
                zone.CurY[point] += MulDiv(distance, _gs.FreeY, _dotProduct);
                if (touch) zone.Tags[point] |= TagTouchY;
            }
        }

        /// <summary>Move a point without regard to the freedom vector: SHPIX's job, and the one
        /// place a program says "this far, in x and y" rather than "this far, that way".</summary>
        private static void MoveDirect(Zone zone, int point, int dx, int dy, byte touch)
        {
            if (point < 0 || point >= zone.PointCount) return;
            zone.CurX[point] += dx;
            zone.CurY[point] += dy;
            zone.Tags[point] |= touch;
        }

        // ---- rounding ---------------------------------------------------------------------------
        //
        // Every rounding a program can ask for, sign-symmetric: a distance rounds the same amount
        // whichever way it points, and never rounds through zero, which would turn a stem inside out.

        /// <summary>Rounds a distance, or -- with <paramref name="position"/> -- a coordinate.
        /// <para>The two are not the same job under ClearType and that is the whole point. A stem's
        /// WIDTH comes out of MIRP/MDRP rounding a distance, and GDI rounds those on the pixel grid
        /// exactly as a greyscale rasterizer does: measured against GDI, Segoe UI's 'l' holds a stem
        /// of 3.2 lamps -- one pixel -- unchanged from 11 through 14 pixels an em. Its PLACE comes
        /// out of MDAP/MIAP rounding a coordinate. Splitting them that way (WPF_X_HINT=7) was worth
        /// measuring and is worse than leaving x alone: structural 3155 against 274. Every division
        /// of this labour has now been tried -- both grids fine (6), positions only (7), widths to a
        /// whole pixel (8), all of it (5) -- and all four are far behind mode 0.</para></summary>
        private int RoundDistance(int distance, bool position = false)
        {
            bool negative = distance < 0;
            int value = negative ? -distance : distance;

            // Subpixel text measures x in THIRDS of a pixel, because that is the resolution it draws
            // in: a stem edge on a lamp boundary saturates that lamp, and one between two boundaries
            // is split across both. Rounding an x distance to a whole pixel -- which is what the
            // program asks for and what a grey rasterizer wants -- throws away two thirds of the
            // resolution the face is being fitted to. See TrueTypeFont.XHintMode for the measurement.
            if (TrueTypeFont.XHintMode == 1 && TrueTypeFont.SubpixelFitting && IsHorizontalProjection)
            {
                const int Third = 64;                       // 26.6 pixel, divided by three below
                int steps = (int)(((long)value * 3 + 32) / Third);
                value = (int)((long)steps * Third / 3);
                if (value < 0) value = 0;
                return negative ? -value : value;
            }

            // THE SAME RULES, ON A GRID THREE TIMES FINER. ClearType draws x at three times the
            // resolution, so a "pixel" in x is a lamp, and the grid the program rounds against is
            // three times finer there and only there. The first attempt at this replaced the
            // rounding outright, which also overrode RoundMode.Off -- distances the program asked
            // NOT to round -- and the SROUND period Segoe UI does most of its work with. Keep every
            // rule the program chose and change only the unit: measure the distance in thirds,
            // round it by the face's own rule, put it back.
            // The face's own rule, on the virtual grid where that grid applies.
            bool finer = XThirdGrid || (XThirdPositions && position);
            // A POSITION may round on the physical grid while a DISTANCE rounds on the virtual one.
            // Measured at 11ppem, GDI puts the left edge of 'o', 'e', 'n' and 'a' on a WHOLE pixel
            // (1.0, from unhinted 0.516/0.516/0.891/0.484) where the sixteenth grid leaves us at
            // 0.5/0.5/0.875/0.5 -- and the rendered pixels agree, our glyphs sitting a column left
            // of Windows'. WPF_CT_POSGRID=virtual restores the literal reading.
            bool physicalPosition = position && s_positionsOnPhysicalGrid;
            // Exactly the configuration that was MEASURED to reproduce GetGlyphOutline (55 of 62
            // glyphs byte-identical): POSITIONS on the physical grid, distances left on the
            // ClearType grid. An earlier version put distances there too -- which is not the tested
            // configuration, and it put 'o' at 0.06 where GDI's bi-level outline says 1.0.
            if (BiLevelPass) { finer = false; physicalPosition = position; }
            // A DISTANCE may round on the physical grid while a POSITION rounds on the virtual one --
            // the opposite pairing to WPF_CT_POSGRID, and the one the pixels argue for: Windows'
            // stems land square on a column ('m' at 11ppem reads "@." per stem) where ours straddle
            // two ("./%"), which is a stem WIDTH question, while our placement is already within a
            // sixteenth almost everywhere.
            int distanceGrid = !position && InClearTypeDirection && s_distanceGrid > 0 ? s_distanceGrid : 0;
            // A SHORT distance is a stem, and a stem about a pixel wide is the one measurement where
            // landing on the grid decides whether the stem is crisp or smeared across two columns.
            // A long one is a bowl or a bar, where the same rounding is just distortion. That is the
            // 11-vs-16ppem tension every global rule here has run into, stated as what it actually
            // depends on -- the size of the DISTANCE, not the size of the text.
            if (s_stemSnap > 0 && !position && InClearTypeDirection && Math.Abs(value) <= s_stemSnap)
                distanceGrid = 1;
            int thirds = distanceGrid > 0 ? distanceGrid
                       : finer && TrueTypeFont.SubpixelFitting && IsHorizontalProjection ? 3
                       : physicalPosition ? 1
                       : position && s_positionGrid > 0 && InClearTypeDirection ? s_positionGrid
                       : InClearTypeDirection ? ClearTypeGrid
                       : 1;
            value *= thirds;

            switch (_gs.Round)
            {
                case RoundMode.ToGrid: value = Pix(value); break;
                case RoundMode.ToHalfGrid: value = Floor(value) + 32; break;
                case RoundMode.ToDoubleGrid: value = (value + 16) & ~31; break;
                case RoundMode.DownToGrid: value = Floor(value); break;
                case RoundMode.UpToGrid: value = Ceil(value); break;
                case RoundMode.Off: return distance;
                case RoundMode.Super:
                case RoundMode.Super45:
                    {
                        int period = _gs.RoundPeriod;
                        if (period <= 0) break;
                        value = value - _gs.RoundPhase + _gs.RoundThreshold;
                        value = value >= 0 ? value / period * period : -((-value + period - 1) / period * period);
                        value += _gs.RoundPhase;
                        break;
                    }
            }

            value /= thirds;

            if (value < 0) value = 0;
            return negative ? -value : value;
        }

        /// <summary>SROUND and S45ROUND both take one byte and mean the same thing by it: a period,
        /// a phase within it, and how far past a step counts as reaching the next one.</summary>
        private void SetSuperRound(int selector, int gridPeriod)
        {
            switch (selector & 0xC0)
            {
                case 0x00: _gs.RoundPeriod = gridPeriod / 2; break;
                case 0x40: _gs.RoundPeriod = gridPeriod; break;
                case 0x80: _gs.RoundPeriod = gridPeriod * 2; break;
                default: _gs.RoundPeriod = gridPeriod; break;
            }

            _gs.RoundPhase = (selector & 0x30) switch
            {
                0x00 => 0,
                0x10 => _gs.RoundPeriod / 4,
                0x20 => _gs.RoundPeriod / 2,
                _ => _gs.RoundPeriod * 3 / 4,
            };

            int threshold = selector & 0x0F;
            _gs.RoundThreshold = threshold == 0
                ? _gs.RoundPeriod - 1
                : (threshold - 4) * _gs.RoundPeriod / 8;
        }

        // ---- the stack ----------------------------------------------------------------------------

        private void Push(int value)
        {
            if (_top >= _stack.Length) throw new IndexOutOfRangeException("hinting stack overflow");
            _stack[_top++] = value;
        }

        private int Pop()
        {
            if (_top <= 0) throw new IndexOutOfRangeException("hinting stack underflow");
            return _stack[--_top];
        }
    }
}
