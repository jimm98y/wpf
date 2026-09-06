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

        // ...except when FONT UNITS are being scaled to the pixel grid. There Windows' rasterizer
        // adds half and shifts, so an exact half rounds UP for either sign: -31.5 sixty-fourths is
        // -31 to it and -32 to the symmetric rule above. Ties are rare and the difference looks
        // like nothing, until a program branches on one. Times New Roman Italic's 'i' at 12ppem
        // reads cvt[134] (-84 units, exactly -31.5 there), rounds it and adds it to the side
        // bearing: at -31 the sum lands on a pixel and the function returns early; at -32 it does
        // not, the function rewrites the side-bearing cvt to its fractional residue, and the MIRP
        // that follows puts the glyph's leftmost point a whole pixel left of where Windows puts it,
        // and every other point with it (42 of 42 points, all -64/64). Against GDI's own fitted
        // points, floor-rounding the cvt fixed that glyph and Arial@16 (x 14 -> 3) and moved
        // nothing else; floor-rounding the outline points as well took Consolas Italic@12 from 13
        // differing points to 1 and Times Italic@16 from 36 to 29, again with no row worse.
        // FreeType, incidentally, rounds symmetrically here too -- and so did we, from it.
        internal static int ScaleUnits(int fontUnits, int scale16)
        {
            long v = (long)fontUnits * scale16;
            return (int)((v + 0x8000) >> 16);
        }

        /// <summary>Font units to 26.6 pixels at the size last prepared, rounded the way Windows'
        /// rasterizer rounds them (<see cref="ScaleUnits"/>).</summary>
        private int Scale(int fontUnits) => ScaleUnits(fontUnits, _scale);

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
        /// displacement reaches along the direction, in the displacement's own units.
        /// <para>TWO PRODUCTS, EACH ROUNDED TO 26.6 ON ITS OWN, then added -- not one exact sum
        /// rounded once. That is how Windows' rasterizer projects (x * pv.x, rounded; plus y * pv.y,
        /// rounded; each with half added and shifted, so a negative product rounds towards minus
        /// infinity), and the difference between the two is the last bit of every slanted
        /// measurement, which is to say every ITALIC and every DIAGONAL. Times New Roman Italic 'V'
        /// at 12ppem showed it: its program projects along the italic slant (15744, -4533) and
        /// MIRPs the first point from the origin phantom, moving along x. The point sits at
        /// (117, -12) sixty-fourths; the exact projection is 115.75 and rounds to 116, but
        /// 112.43 + 3.32 rounded separately is 112 + 3 = 115. One sixty-fourth in the projection
        /// is 13.5 rather than 12.5 to move, which rounds the other way, and after the -1px DELTAP
        /// that follows the point stands at 64 where the exact sum puts it at 62. Every point on
        /// the left arm hangs off that one, so 27 of 34 points were two sixty-fourths left of GDI's
        /// -- and only at 12ppem, because that is the size where both roundings sit on a boundary.
        /// Against GDI's own fitted points, per face (x differing at 12/16): Arial Italic 6/31 ->
        /// 0/0, Times Italic 35/13 -> 3/6, Verdana Italic 23/9 -> 6/1, Tahoma Italic 6/6 -> 1/0,
        /// Arial 4/3 -> 0/1, Times Bold 1/11 -> 0/4, with the y column better nearly everywhere as
        /// well; Times Regular alone gave back three points at each size. Weight 5,604,278 ->
        /// 5,600,974, advances unmoved. WPF_PROJ_MODE=0 is the single exact sum; 2 rounds each
        /// product symmetrically instead of by the shift, which is one point worse at three sizes
        /// and better at none.</para></summary>
        private static int DotFix14(int x, int y, int ux, int uy)
        {
            if (s_projMode == 1)
                return (int)((((long)x * ux + 0x2000) >> 14) + (((long)y * uy + 0x2000) >> 14));
            if (s_projMode == 2)
            {
                long a = (long)x * ux, b = (long)y * uy;
                return (int)((a + (a >= 0 ? 0x2000 : -0x2000)) / 0x4000 + (b + (b >= 0 ? 0x2000 : -0x2000)) / 0x4000);
            }
            long v = (long)x * ux + (long)y * uy;
            return (int)((v + (v >= 0 ? 0x2000 : -0x2000)) / 0x4000);
        }

        /// <summary>How <see cref="DotFix14"/> rounds: 1 (default) as Windows does, 0 one exact
        /// sum, 2 separate symmetric roundings.</summary>
        private static readonly int s_projMode =
            int.TryParse(Environment.GetEnvironmentVariable("WPF_PROJ_MODE"), out int m) ? m : 1;

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
            public int[] InkX;            // where they started in x BEFORE the compatible-width
                                          // pre-scale -- what a BLACK distance is measured on
            public byte[] Tags;
            public int[] Contours;
            public int PointCount;

            public Zone(int points, int contours)
            {
                CurX = new int[points]; CurY = new int[points];
                OrgX = new int[points]; OrgY = new int[points];
                OrusX = new int[points]; OrusY = new int[points];
                InkX = new int[points];
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

        /// <summary>Whether the pre-program run for the current size set INSTCTRL selector 1,
        /// "inhibit grid-fitting": no glyph program runs, and the glyph is drawn and spaced as
        /// scaled. Valid after <see cref="PrepareForSize"/>.</summary>
        internal bool GridFitInhibited => (_prepState.InstructControl & 1) != 0;
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

        /// <summary>Whether the FACE asks for symmetric smoothing at a given size, from its
        /// 'gasp'. Set by TrueTypeFont when the interpreter is built.
        /// <para>GETINFO's symmetric-rendering bit is not a global truth about the rasterizer, it
        /// is a property of THIS face at THIS size -- and a face branches its whole hinting
        /// program on the answer. Segoe UI asks for it only at 20ppem and above.</para></summary>
        internal Func<float, bool>? FaceWantsSymmetricSmoothing;

        /// <summary>What to answer GETINFO's symmetric-rendering query at the size in hand.</summary>
        internal bool SymmetricRenderingAnswer =>
            s_symmetricInfoForced ?? (FaceWantsSymmetricSmoothing?.Invoke(_ppem) ?? false);
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
            _nudgeCount = 0; _nudgeDx = 0;
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

                // A PRE-PROGRAM MAY SWITCH THE GLYPH PROGRAMS OFF. INSTCTRL selector 1 is "inhibit
                // grid-fitting", and a face sets it in 'prep' at the sizes it does not want hinted
                // -- Verdana below 9ppem -- after which the rasterizer runs no glyph program at all:
                // the outline stays as scaled and the phantom points stay where the scaling rounded
                // them. We ran the programs anyway. Verdana's 'i' at 8ppem then placed its advance
                // phantom stem-right plus a rounded side bearing, 3 pixels, where GDI's unrun glyph
                // keeps round(2.195) = 2; 'l' the same, two pixels of drift a line.
                if (glyph.Instructions.Length > 0 && !GridFitInhibited)
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
                    // The trace prints each instruction's state BEFORE it runs, so the last one --
                    // which is nearly always an IUP, and nearly always the one in question -- never
                    // shows its own result. Print the finished outline.
                    if (dumping) DumpFinal();
                }

                // MODE 9, DAMAGE CONTROL: a glyph whose program never touched a point in x has
                // nothing GDI could recognise as a stroke or a position to scale -- and GDI leaves
                // it alone. Segoe UI Italic's 'a' at 12ppem runs one SVTCA[x] and then hints only y;
                // GDI's ink is 5.68px wide, the UNSCALED width, while the hdmx advance is 7 against
                // a linear 6.5. Pre-scaled it came out 6.11px wide and 776,000 worse. So: undo the
                // pre-scale when the program did not constrain x at all.
                if (_preScaled && s_blackOnInk && !AnyTouchedX())
                    for (int i = 0; i < _realPoints; i++)
                        _glyphZone.CurX[i] = _glyphZone.OrgX[i] = _glyphZone.InkX[i];
                if (s_ctColor && !BiLevelPass && TrueTypeFont.SubpixelFitting) ColorStems();
                if (s_ctColorValidate && !BiLevelPass && TrueTypeFont.SubpixelFitting) ValidateColoring();
                if (s_ctPhase != 0 && !BiLevelPass && TrueTypeFont.SubpixelFitting) ApplyPhaseControl();
                if (s_capturePoints) CapturePoints(glyph);
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
        internal int ScaleToPixels(int fontUnits) => Scale(fontUnits);

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

        /// <summary>Whether x movement is refused AS THE PROGRAM RUNS rather than undone after.
        /// <para>This is rule 1 of what FreeType calls BACKWARD COMPATIBILITY MODE, its emulation of
        /// ClearType. Its four rules, from ttinterp.h: x movement is ignored; points are not moved
        /// post-IUP on either axis except the x component of diagonal moves; SHPIX and DELTAP do not
        /// execute unless moving a composite on y or a previously y-touched point; and the hdmx
        /// table and phantom-point changes are ignored. It is disabled by `#PUSH 4,3 INSTCTRL[]`,
        /// which is the selector NativeClearTypeMode already reads.</para>
        /// <para>MEASURED, and it is not GDI's mode. All four rules together cost 681,433 ->
        /// 1,550,791; rule 1 alone 1,472,831; rule 2 alone 684,721. Our delta suppression already
        /// matches their rule 3 and WPF_CT_DELTA=touched is that clause verbatim, but rules 1 and 4
        /// contradict what measures best here -- GDI hints x, and compatible widths (which rule 4
        /// forbids) is worth 70,000 on its own. FreeType is emulating DirectWrite's ClearType;
        /// GDI's is the compatible-widths mode, and it is a different animal.</para></summary>
        internal static bool XSuppress =>
            TrueTypeFont.XHintMode == 12 && TrueTypeFont.SubpixelFitting;

        /// <summary>Mode 17: DO NOTHING OF OUR OWN IN X -- round it on the whole-pixel grid, the
        /// way a bi-level interpreter does, and let the FACE decide what ClearType means.
        /// <para>Every other mode here is a guess at a rule GDI applies on top of the program.
        /// Once the rasterizer version says 42, Segoe UI runs its own ClearType branch (see
        /// WhatGdiAnswersGetInfo) and no longer needs one invented for it -- and GDI's own answers
        /// say the same thing from the other side: compatible widths SET and sub-pixel positioning
        /// CLEAR is a rasterizer that grid-fits x exactly like the bi-level one and differs only in
        /// how it shades the result.</para>
        /// <para>Not the same as mode 0, which throws the program's x work away and keeps the
        /// scaled outline. This keeps every bit of it and only declines to re-round it.</para>
        /// </summary>
        internal static bool XWholePixelGrid => TrueTypeFont.XHintMode == 17;

        /// <summary>AND WHAT THE BAND IS MADE OF: STEM SIDES, MISPLACED RATHER THAN MIS-SIZED.
        /// <para>The structural report classifies each differing pixel by the direction of GDI's
        /// own gradient there -- a mostly horizontal gradient is a VERTICAL edge, which is the side
        /// of a stem. Tahoma's roman, at its band size and at the two sizes either side:</para>
        /// <code>
        ///        differing   sum      hist tail    edge  vertical/horizontal/diagonal
        ///   @14      1513    78,984   232/24        36,649 /  7,190 / 35,145
        ///   @16      3140   198,252   850/226      117,990 / 12,382 / 67,880
        ///   @18      1674    82,211   104/31        26,251 /  6,704 / 49,256
        /// </code>
        /// <para>At the band size the vertical-edge term is three to four times its neighbours',
        /// 1766 of the 3140 differing pixels lie on one, and the tail of the histogram -- pixels
        /// wrong by a LOT rather than a little -- goes up eight-fold. So the band is stem sides,
        /// which is what its name always said, now shown on a face and a size that have nothing to
        /// do with the original observation.</para>
        /// <para>And they are MISPLACED, not mis-sized: the signed sum at 16 is -1,782 against an
        /// absolute 198,252, so what is wrong cancels almost exactly between the two sides of a
        /// stem. That is the signature of a stem sitting a fraction of a pixel to one side, not of
        /// one drawn too thin or too fat -- which agrees with the per-coordinate solve, where
        /// Tahoma's 'H' has a correct left stem and a right stem 0.11px too far right, and with
        /// the face's total indifference to the stem-fat constant.</para>
        /// <para>The specimen's net LIGHTNESS at that size is a separate and much smaller effect:
        /// ink against Windows is 0.9855 at 16 against 0.9914 at 14 and 0.9982 at 18, about a
        /// sixth of the error, and the stem-fat correction cannot recover it -- switched on at 16
        /// it moves the ink ratio only to 0.9863, because at that size the fattening quantizes
        /// away.</para></summary>
        /// <summary>THE "11 TO 13 BAND" IS NOT ABOUT THOSE SIZES. Every face has a band, and it
        /// sits somewhere else.
        /// <para>That band has been treated throughout as a property of the sizes 11, 12 and 13,
        /// because Segoe UI is the face every measurement was taken on. Asked of the other faces,
        /// error per unit of ink on the isolated-glyph harness:</para>
        /// <code>
        ///               @11   @12   @13   @14   @15   @16   @17   @18
        ///   Segoe UI    27.9  26.3  27.5  11.1  10.4   9.8   8.2   9.0
        ///   Tahoma      90.1  35.0  28.1  15.6  27.2  31.6  12.2  10.0
        ///   Arial                         16.6  22.8  25.5  21.0  14.0
        ///   Verdana                       18.7  30.9  15.6   9.4  11.1
        /// </code>
        /// <para>Segoe UI's band is 11 to 13 and it is clean by 14. Tahoma's and Arial's are at 15
        /// and 16, where Segoe UI is at its best. Verdana's is at 15. So the band is a property of
        /// the FACE, not of the size -- and the same statement holds on the specimen, where the
        /// error peaks at 16 for exactly the four faces whose bands are there and Segoe UI is
        /// exempt.</para>
        /// <para>This unifies two mysteries that have been chased separately: the 11-13 band and
        /// the specimen's non-monotonic size curve (1,623,912 / 2,343,248 / 1,982,506 / 2,882,808 /
        /// 1,990,770 / 2,331,757 at 10 to 20) are the same phenomenon, seen once per face. Which
        /// also means the band is not evidence for a size-gated rule, and the ppem gates in this
        /// file -- the stem-fat band at 10 to 13 above all -- are fitted to where ONE face's band
        /// happens to fall. Widening that band was measured and loses 128,241 across six sizes, so
        /// the constant is right for the corpus and wrong in principle.</para>
        /// <para>Excluded for the peak, all measured at 16ppem: placement (every band wants a rigid
        /// shift of zero), the contrast curve (gamma is a clean minimum at 1.20 there as at 12),
        /// the suppressed x deltas at any fraction, and the stem-fat band. What the peak does carry
        /// is a systematic LIGHTNESS: ink against Windows runs 0.9914 at 14, 0.9855 at 16 and
        /// 0.9982 at 18, so the worst size is also the lightest, and about a sixth of the error
        /// there is a net ink deficit rather than misplaced ink.</para></summary>
        /// <summary>WHERE THE TEXT WORK ENDS, stated as three numbers.
        /// <para>OUR STEM PLACEMENT IS UNBIASED. Sweeping a global x offset against GDI's own
        /// allowed ranges over 42 exactly solved stems, zero is the optimum -- 57 per cent of
        /// left edges inside, against 55 at -3/64, 48 at -8/64, 43 at +2/64 and 24 at +8/64 --
        /// and the signed error runs -12/64 to +11/64 with a median of -2. There is no
        /// systematic shift to remove; the error is scatter about a correct centre.</para>
        /// <para>AND IT IS GEOMETRY, NOT RASTERIZATION -- the last alternative, checked rather
        /// than assumed. On the no-program synthetic bars, which are pure rasterization with no
        /// hinting anywhere, our lamp coverage against GDI's differs in 0 lamps at 8, 9, 18 and
        /// 19ppem and in 24 to 160 elsewhere, out of 115,000 to 322,000 compared at each size:
        /// six hundredths of one per cent at worst. Nothing that small can produce a scatter of
        /// twelve sixty-fourths in where a stem lands.</para>
        /// <para>THE SCATTER IS ABOUT TWICE THE TOLERANCE. Our stems land within +/-12/64 of
        /// GDI's, and a geometry error under 6/64 cannot appear in the output at all (see
        /// HowFinelyOurRasterizerResolves). Half the scatter therefore shows and half does
        /// not, which is exactly the 52 to 57 per cent of stems that measure indistinguishable
        /// from GDI's.</para>
        /// <para>So the arithmetic of what is left: to do better the scatter has to halve, and
        /// halving it means reproducing GDI's per-stem choice. Every mechanism that could --
        /// the rounding grid, the control values, the cut-in, the minimum distance, the
        /// deltas, the anchor, a damping factor, a grid on the left edge or the right or the
        /// centre -- has been measured and closed. What remains is not a rule waiting to be
        /// found but a per-stem quantity, and the rendered output cannot supply it: its own
        /// resolution is 6/64, coarser than the correction needed.</para></summary>
        /// <summary>AND WHY VERDANA AND TAHOMA DIFFER: MIRP against MDRP. Not a missing rule.
        /// <para>The face-dependence looked like the last big clue. It is not a clue, it is a
        /// consequence, and the mechanism is visible in one census of the x pass:</para>
        /// <code>
        ///   Verdana 'H'   10 MIRP, 8 of them taking a control value in x, 0 MDRP
        ///   Tahoma  'H'    6 MDRP, 1 MIRP, 0 control values taken in x
        /// </code>
        /// <para>MIRP measures against a CONTROL VALUE; MDRP measures the OUTLINE. Verdana's
        /// control values agree closely with its own outline distances -- it is a regularly hinted
        /// face -- so the cut-in never rejects them and the grid has nothing to quantize, and its
        /// ClearType fit comes out equal to its bi-level fit. Tahoma's MDRPs have no control value
        /// at all, so their ONLY quantization is the rounding grid: whole pixels under bi-level,
        /// sixteenths under ClearType, and the two modes therefore share almost nothing.</para>
        /// <para>Measured as displacement from the unhinted outline, which is the same statement
        /// without the percentages:</para>
        /// <code>
        ///               GDI bi-level moves    our ClearType moves
        ///   Verdana          0.321px               0.321px      identical
        ///   Tahoma           0.426px               0.156px
        ///   Arial            0.302px               0.090px
        ///   Segoe UI         0.254px               0.094px
        /// </code>
        /// <para>So we track GDI in BOTH regimes: full for Verdana, where GDI's ClearType is its
        /// bi-level fit and ours is too, and damped for the other three, where GDI's ClearType is
        /// nothing like its bi-level fit and ours is not either. There is no third behaviour hiding
        /// behind the split, and the model is structurally right for every face tried.</para>
        /// <para>Which also says where Tahoma's remaining error must live. Its x is decided
        /// entirely by rounding MDRP's outline distance on the fine grid; the grid constant is a
        /// measured optimum at every size; so what is left is detail below a sixteenth of a pixel
        /// and not a mechanism.</para></summary>
        /// <summary>WHAT GDI'S CLEARTYPE X IS, ASKED WITHOUT A MODEL.
        /// <para>Two oracles had never been put side by side. GetGlyphOutline gives GDI's own
        /// BI-LEVEL fitted x exactly; the lamp solver gives the interval its CLEARTYPE pixels
        /// allow. So ask which of GDI's own two answers -- its bi-level fit, or the plain scaled
        /// outline it starts from -- lands inside its own ClearType interval. Neither is a guess:
        /// both are things GDI itself produced. Ours is scored over the same coordinates.</para>
        /// <code>
        ///                 GDI bi-level   plain outline   OURS
        ///   Segoe UI          41.8%          49.2%       70.3%   (256 coordinates)
        ///   Tahoma            13.9%          47.2%       64.8%   (352)
        ///   Arial             48.3%          47.2%       56.1%   (180)
        ///   Verdana           94.1%           8.4%       94.1%   (322)
        /// </code>
        /// <para>Three things follow. GDI's ClearType x is NOT its bi-level x and NOT the plain
        /// outline -- it is a third thing, which is what this layer has always assumed but never
        /// demonstrated. Our model beats both of GDI's own answers on every face, by a wide margin
        /// on three of them, so it is right in KIND and the remaining third is precision. And
        /// shipping the bi-level fit, which the stage notes kept raising as the obvious thing to
        /// try, is dead: it scores 13.9 per cent on Tahoma.</para>
        /// <para>The relationship is FACE-DEPENDENT, which is the new fact. Verdana's ClearType x
        /// is its bi-level x -- 94.1 per cent, against 8.4 for the unhinted outline, so the
        /// intervals are tight and discriminating rather than permissive -- and we match it exactly
        /// there. Tahoma's is nothing like its bi-level x. Whatever distinguishes the two faces is
        /// worth more than another rule fitted across all of them.</para>
        /// <para>READ THE PERCENTAGE FOR WHAT IT IS. Each interval is computed with the OTHER
        /// coordinates held at their solved values, so it says whether one coordinate is
        /// individually defensible, not whether the glyph as a whole renders like GDI's. Verdana
        /// scores 94 per cent here and still measures 0.197 error per unit of ink on the specimen;
        /// those are not in contradiction.</para></summary>
        /// <summary>WHICH DISTANCE GDI USED, ASKED PER COORDINATE -- and what is left after it.
        /// <para>Aggregate scores can only say that dividing the control-value cut-in by sixteen
        /// beats dividing it by one. They cannot say which distance GDI used at any particular
        /// point. Fitting the same glyph TWICE in one process -- once as we ship it, once with the
        /// cut-in whole so the control value is taken instead -- gives every coordinate two
        /// candidate positions, and GDI's own pixels can then be asked which of the two it
        /// allows.</para>
        /// <code>
        ///   where the two disagree      outline   control value   either   neither
        ///   Segoe UI                          5               0        0         0
        ///   Tahoma                            3               0        0         3
        ///   Arial                            39               2        0        14
        /// </code>
        /// <para>So the outline is the right choice, per coordinate and not merely on average.
        /// What the same table says more usefully is how RARELY the choice arises at all: five
        /// coordinates of Segoe UI's fifty-four, six of Tahoma's fifty-two. The cut-in question is
        /// settled and it is not what is wrong with the other forty-odd.</para>
        /// <para>ASK FOR THE RIGID SHIFT. Several glyphs are wrong by a single small displacement
        /// of the whole outline rather than by their shape:</para>
        /// <code>
        ///   Tahoma 'H'   a rigid  -8/64 takes  6 of 12 coordinates to 12 of 12
        ///   Arial  'H'   a rigid  -6/64 takes  6 of 12 coordinates to 12 of 12
        ///   Arial  'c'   a rigid +19/64 takes 17 of 27 to 26
        ///   Arial  'd'   a rigid +24/64 takes 14 of 30 to 26
        ///   Tahoma 'd'   a rigid +19/64 takes 21 of 39 to 28
        ///   totals   Tahoma 228 -> 265 of 554   Arial 194 -> 237 of 471   Segoe UI 180 -> 201 of 440
        /// </code>
        /// <para>An 'H' whose every coordinate comes good under one eighth of a pixel is not a
        /// mis-shaped glyph, it is a correctly shaped one in the wrong place -- and both stems
        /// moving together says the stem widths are already right, which is what Tahoma's total
        /// indifference to the stem fat says too.</para>
        /// <para>Read the TOTALS with care, though: a free per-glyph shift is a fitted parameter,
        /// and on intervals this wide some of that five to nine per cent is what any free parameter
        /// picks up. The individual glyphs are the real evidence -- 6 of 12 to 12 of 12, or 14 of
        /// 30 to 26 of 30, is not something a shift finds by luck. Treat the totals as an upper
        /// bound on what a placement rule could ever buy.</para>
        /// <para>The search was ORIGINALLY plus or minus eight sixty-fourths and several glyphs
        /// reported a best shift of exactly the limit, which means the search was choosing the
        /// answer rather than the glyph. Widened to plus or minus thirty-two the shifts run to
        /// 24/64, a third of a pixel, and differ per glyph in size and sign -- so this is not a
        /// global offset to subtract; it is whatever decides where a glyph's first anchor
        /// lands.</para>
        /// <para>NOT the left side bearing, which is the obvious candidate: if GDI put a glyph's
        /// left edge on a whole pixel and we left it where the outline falls, the shift would be
        /// exactly what rounding that edge costs. Printed beside the measured shift it agrees for
        /// Segoe UI's 'H' and 'P' (-7 predicted against -8 measured) and for nothing else -- and
        /// those two share a left edge of 1.109, so that is one coincidence rather than two. Tahoma
        /// 'H' wants -8 and the rule predicts +7.</para>
        /// <para>It is NOT that GDI rounds positions on a coarser grid than distances, which is the
        /// obvious form for such a rule. Swept on the specimen, position grids of 2, 3, 4, 6 and 8
        /// give 2,923,379 / 2,769,651 / 2,639,246 / 2,527,726 / 2,473,521 -- monotonically
        /// approaching, and never reaching, the 2,343,248 of rounding positions exactly as finely
        /// as everything else.</para></summary>
        /// <summary>AND WHICH COORDINATES ARE WRONG: the ones the program PLACES, not the ones
        /// IUP carries.
        /// <para>The same instrument, asked a second question. For every coordinate, is the value
        /// we ship one that GDI's own pixels allow -- and was it placed by a fitting instruction or
        /// interpolated between two that were:</para>
        /// <code>
        ///                 program PLACED        IUP interpolated
        ///   Segoe UI     30 of  54  55.6%      150 of 202  74.3%
        ///   Tahoma       19 of  52  36.5%      209 of 300  69.7%
        ///   Arial        55 of  98  56.1%      139 of 190  73.2%
        /// </code>
        /// <para>Consistent across three faces that share no hinting style: the coordinates a
        /// fitting instruction decides are wrong roughly twice as often as the ones IUP carries,
        /// and the interpolated ones inherit their error from those anchors rather than adding one.
        /// So the fault is in placement, not interpolation, and the two want completely different
        /// fixes -- which is why separating them matters more than the totals do.</para>
        /// <para>It also sharpens the contradiction worth handing on. Our model of GDI's ClearType
        /// x is "measure the outline, barely round it": the cut-in divided by sixteen means the
        /// control value is almost never taken. That model is a smooth optimum -- divisors 8 and 32
        /// are both worse, and taking the control value always is catastrophic -- yet it leaves
        /// half the placed coordinates outside what GDI allows. So GDI takes the control value
        /// SOMETIMES, in a pattern a single threshold does not capture, and the next thing to look
        /// for is what decides it.</para></summary>
        /// <summary>WHOSE TOUCH SET IS IT: the first DERIVED fact about the residue.
        /// <para>WhichPointsGdiTouchedInX asks the question the coordinate comparisons could not.
        /// IUP can only put an untouched point where its two nearest touched neighbours put it, so
        /// feeding it GDI's OWN anchor positions gives the value GDI would have to have if it
        /// interpolated that point. Where GDI has something else, GDI touched it. That is a
        /// derivation, not a preference between two fits.</para>
        /// <para>At 12ppem over 23 glyphs: 542 points, 413 of which we interpolate, and 36 of those
        /// (four more are the solver's own coordinate-sharing and are excluded) CANNOT be what GDI
        /// has. So our touch set is not GDI's -- but it is very nearly right, and it is EXACTLY
        /// right for eight whole glyphs: H, I, l, T, E, F, L and m have nothing left to explain at
        /// the level of the touch set.</para>
        /// <para>Where the rest sit is the shape of the answer. M's middle V (points 7 to 12) is
        /// the largest single group, and GDI draws it NARROWER at both tops -- inward on the left
        /// arm and inward on the right. N's two diagonal ends go the same way. The other class is
        /// the top of a bowl at the x-height line: d, q and c all miss at the point whose start y
        /// is 6.14, and d and q miss it by 24/64 of a pixel, the largest differences in the
        /// table.</para>
        /// <para>And it PEAKS WHERE THE ERROR PEAKS. Same 23 glyphs, same 413 interpolated
        /// points, by size: 11 impossible at 9ppem, 14 at 10, 40 at 12, 28 at 16. The band
        /// 11 to 13 that measures three times worse than its neighbours is the band where our
        /// touch set diverges most from GDI's, which is the first account of that band that
        /// does not have to invoke a stem-width rule nobody can find.</para>
        /// <para>Which retires the reading of the whole-pixel-MDAP table that suggested this: N and
        /// M becoming near-exact under the coarse grid is a COINCIDENCE, the anchors moving so far
        /// that IUP happens to drop the diagonals near where GDI has them. The grid was never the
        /// question.</para>
        /// <para>Three mechanisms checked and eliminated, so that they are not tried again:</para>
        /// <para>The MODE IS NOT THE GATE. storage[2] is 6 here -- bi-level 0, plus 2 for the
        /// ClearType bit, plus 4 for compatible widths -- and every GETINFO answer feeding it has
        /// been measured against GDI's own. We run the instructions GDI runs.</para>
        /// <para>OUR IUP IS NOT THE DIFFERENCE. Carry is instruction-for-instruction the reference
        /// implementation, including the part that catches people out: the inside/outside test on
        /// the SCALED start but the proportion in FONT UNITS. The first version of the test above
        /// interpolated in pixels for both and manufactured four findings out of the 64th of a
        /// pixel between them.</para>
        /// <para>AND IT IS NOT THE X DELTAS. Segoe UI's M ends with three DELTAPs in x after IUP,
        /// one of them encoding this very size, and SkipDeltaInClearTypeDirection drops all three.
        /// Restoring them is exactly the shape of "GDI moves a point we interpolate" and it is
        /// wrong: WPF_CT_DELTA=all takes the impossible count from 36 to 60. WPF_CT_DELTA=touched
        /// changes nothing at all. The rule that ships is the rule GDI uses.</para>
        /// <para>So the instructions, the interpolation between them, and the deltas after them are
        /// all accounted for, and GDI still places three dozen points where none of them can. What
        /// is left is that GDI moves those points with something this interpreter does not run at
        /// all -- and the group it moves, diagonals and bowl tops, is the group a rasterizer would
        /// treat specially rather than one a font program would single out.</para></summary>
        /// <summary>WHAT WHOLE-PIXEL MDAP DOES PER GLYPH, and two hypotheses it kills.
        /// <para>Solved against GDI's own coordinates at 12ppem, coordinates landing inside the
        /// interval GDI's pixels allow, default sixteenth grid against POSGRID=physical:</para>
        /// <code>
        ///   improves   H 1->4   N 9->14  M 13->20  n 8->10  c 11->16  P 7->9   q 14->16
        ///              h 7->9   L 1->2   I 0->1    d 14->15
        ///   worsens    8 84->47  s 51->39  0 20->4  o 12->6  a 16->12  T 2->0   E 4->2
        ///   unchanged  m g l F u
        ///   totals     327 against 276, so the fine grid wins overall
        /// </code>
        /// <para>DEAD: 'the round state tells them apart'. Every glyph tried -- the ones that want
        /// the coarse grid and the ones that do not -- executes RTG and nothing else, so the state
        /// MDAP rounds under is identical and cannot be the discriminator.</para>
        /// <para>DEAD: 'straight glyphs want whole pixels, round ones want the fine grid'. It fits
        /// H, N, M against o, a, 8 and then fails both ways: c, d and q are round and IMPROVE,
        /// while T, E and F are straight and get WORSE.</para>
        /// <para>What the table does show is that the loss tracks the number of COORDINATES, not
        /// the shape: the three worst are the three most complex glyphs (90, 54 and 26 coordinates)
        /// while the simple ones improve sharply. That is the signature of an ANCHOR moving and IUP
        /// dragging everything after it, which would mean whole-pixel MDAP is right and our
        /// handling of the points it drags is what is wrong. Testing that means comparing touch
        /// sets glyph by glyph, which is the tool the stem-width notes describe and which nothing
        /// has yet built.</para></summary>
        /// <summary>AND THE IP IS INNOCENT -- it is the MDAP after it, which makes the remaining
        /// problem a CONFLICT rather than a bug.
        /// <para>Point 1 of 'H' is interpolated between rp1 = point 5 and rp2 = point 13, the
        /// ADVANCE phantom. That phantom is rounded before the program runs -- 8.537 pixels to 9 --
        /// while its unscaled original stays 8.537, so IP legitimately stretches everything between
        /// them by 9.000/8.537. Working it in the font units IP actually uses:</para>
        /// <code>
        ///   1.13 + 910 units * 7.87px / 1268 units = 6.778
        /// </code>
        /// <para>which is exactly what we produce, so our IP matches the specification and GDI
        /// arrives at the same 6.778. The whole difference is the MDAP[r] that follows: GDI rounds
        /// 6.778 to 7.0 and we round it to 6.8125. Both of 'H''s stem edges are whole pixels in
        /// GDI (1.0 and 7.0) and sixteenths in ours (1.125 and 6.8125), so for THIS glyph a
        /// whole-pixel MDAP is exactly right.</para>
        /// <para>It is not right generally: making MDAP round on whole pixels costs 9,137,908
        /// against 5,226,015. So some glyphs want the coarse grid and others do not, and the
        /// question is what tells them apart -- not which single grid is correct, because neither
        /// is. Note that not rounding x AT ALL costs only 1.7%, so the fine grid is nearly the same
        /// as no rounding; the coarse one is the only real choice being made.</para>
        /// <para>A promising place to look: MDAP[r] rounds with the round state the PROGRAM set,
        /// and ours multiplies by the ClearType grid before applying it, which overrides what the
        /// face asked for. Segoe UI's 'H' runs with roundState=ToGrid -- whole pixels -- and that
        /// is what GDI gives it. Check what round state the glyphs that PREFER the fine grid are
        /// running under before changing anything.</para></summary>
        /// <summary>'H' AT 12PPEM, RUN TO THE INSTRUCTION. The smallest fully-characterised case
        /// of what is left, so the next attempt has somewhere concrete to start.
        /// <para>GDI's geometry is exactly recoverable here -- the solver reaches residual 0 -- and
        /// it is 1.0, 2.094, 7.0, 8.094 against our 1.125, 2.219, 6.812, 7.906. Both stems are
        /// 1.094 pixels wide on BOTH sides, so the MIRP that sets stroke weight is right and
        /// s_stemFat is right with it. The whole difference is where the two stems SIT: GDI puts
        /// their left edges on whole pixels 6.0 apart, we put them 5.687 apart.</para>
        /// <para>The second stem's edge is point 1, and the trace says exactly what happens to it:
        /// IP at ip 99 interpolates it from 6.44 to 6.78, MDAP[r] at ip 100 rounds that to 6.8125
        /// on the sixteenth grid, and the DELTA at ip 103 does not move it because x-direction
        /// deltas are suppressed under ClearType. GDI ends at 7.0.</para>
        /// <para>So the question is narrow: is GDI's 7.0 a different ROUNDING of our 6.78, or a
        /// different INTERPOLATION that lands near 7.0 and rounds to it? The first is ruled out --
        /// every position grid was re-swept and all are worse (see below), and whole-pixel MDAP
        /// costs 9.1M against 5.2M. So look at IP: what it interpolates between, and whether GDI's
        /// reference points are the ones we use.</para></summary>
        /// <summary>THE ROUNDING FAMILY WAS RE-SWEPT AFTER THE PLACEMENT WORK AND IS STILL CLOSED.
        /// <para>Everything below had been rejected against the OLD geometry -- before the margin
        /// came from GDI's tmHeight, before advances went through 26.6 and LTSH -- so the
        /// rejections were worth re-testing rather than inheriting. Measured on the text specimen,
        /// 12ppem plus 16ppem, against 5,226,015 for what ships:</para>
        /// <code>
        ///   positions on whole pixels (POSGRID=physical)   9,137,908
        ///   the same, MDAP only       (POSGRID=mdap)       9,137,908
        ///   positions on halves       (POSGRID=2)          6,839,925
        ///   positions on lamps/thirds (POSGRID=3)          6,262,097
        ///   positions on sixths       (POSGRID=6)          5,656,160
        ///   x not rounded at all      (NOROUND_X=1)        5,314,730
        ///   x-hint mode 6             (WPF_X_HINT=6)      13,811,139 over 9/12/16/20
        /// </code>
        /// <para>Every one is worse, so the shipped rules stand. Note how little NOROUND_X costs --
        /// 1.7% -- which says our rounding is close to neutral and is NOT what separates us from
        /// GDI. 'H' at 12ppem is the case to think with: our stem is 1.09 pixels wide against
        /// GDI's 1.078, so the WIDTH is right, and the whole difference is that our left edge lands
        /// on 1.125 (the sixteenth grid) where GDI has it on 1.0. No grid produces both that and
        /// the rest of the repertoire.</para>
        /// <para>So stop looking for a grid. What is left is which INSTRUCTIONS run and what they
        /// compute -- the touch-set comparison described in the stem-width notes.</para></summary>

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

        /// <summary>Whether the distance being rounded is one ClearType measures on the finer grid.
        /// <para>Requiring the FREEDOM vector to be horizontal as well was tried, because Arial's 'K'
        /// controls its diagonal with `MDRP pv=(-12221,10912) fv=(0,16384)` -- measure along the
        /// diagonal, move along Y -- and the projection being more x than y makes this test call a
        /// VERTICAL movement the ClearType direction. Plausible, and measured worse: 10,355,224 ->
        /// 10,362,490 on the six-face specimen, fixing no glyph's fitted y.</para></summary>
        internal bool InClearTypeDirection =>
            ClearTypeInfo && IsHorizontalProjection && (s_ctInPrep || !_inPreProgram);

        /// <summary>WPF_CT_PREP=1: let 'prep' round on the ClearType grid too.
        /// <para>'prep' is where a face rounds its stem CONTROL VALUES, and excluding it means those
        /// land on whole pixels: Segoe UI's stem control value comes out at exactly 1.0000px at 12
        /// pixels an em, and GDI draws that stem 1.165 wide. On the sixteenth grid the same rounding
        /// would leave 1.1875. That is the shape of the whole remaining difference, so it was worth a
        /// measurement rather than an assumption.</para>
        /// <para>MEASURED AND WORSE: the six-face specimen goes 2,197,699 -> 2,311,500. And it does
        /// not touch the stem that prompted it -- Segoe UI's 'l' at 12ppem stays at 3.00 lamps
        /// against GDI's 3.49 either way -- which says that control value is not being rounded by
        /// 'prep' at all, so where 'prep' rounds was never the question. Fourteenth parameter
        /// measured, current setting kept.</para></summary>
        private static readonly bool s_ctInPrep =
            Environment.GetEnvironmentVariable("WPF_CT_PREP") == "1";

        /// <summary>Grid lines per pixel along the ClearType direction. SIXTEEN, from the paper --
        /// not three. A lamp is a third of a pixel and that is what gets DRAWN; the grid the
        /// interpreter ROUNDS against is finer still, and guessing thirds here is what made every
        /// earlier attempt at this quantise stroke weights that GDI leaves alone.</summary>
        /// <summary>WPF_GRID_TRACE=1: report ONCE what grid the first rounded distance used, and
        /// the flags that chose it. Cheap answer to "is this knob reaching the window at all".</summary>
        private static readonly bool s_traceGrid =
            Environment.GetEnvironmentVariable("WPF_GRID_TRACE") == "1";
        private static readonly int[] s_gridHist = new int[20];
        private static int s_gridTotal;

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
        /// divisor of a pixel (1 = whole pixels). 0 leaves them on the virtual grid.
        /// <para>THE LAMP GRID (3) IS REFUTED, 2026-09-04. It is the principled value -- the
        /// resolution the text is actually drawn at -- and it is worse on the specimen at both
        /// sizes tried: 2,280,836 -> 2,758,075 at 12ppem and 2,802,395 -> 3,630,114 at 16. Putting
        /// POSITIONS on it too (WPF_CT_POSGRID=3) is worse again, 3,130,907 / 4,140,016, and
        /// positions alone are 2,699,699 / 3,412,076.</para>
        /// <para>AND IT LOOKED LIKE A WIN FIRST, which is the part worth keeping. Measured as the
        /// mean per-glyph displacement over seven to nine letters it improved Times' italic 0.242
        /// -> 0.162 and Segoe UI 0.116 -> 0.094, and summed over four faces it was ahead. The
        /// specimen -- twenty-four rows of about fifty characters -- says the opposite by twenty
        /// percent. The per-glyph report is for DIAGNOSIS, naming which glyphs are wrong; it is far
        /// too small a sample to CHOOSE a knob with. Tune on the specimen.</para></summary>
        private static readonly int s_distanceGrid =
            Environment.GetEnvironmentVariable("WPF_CT_DISTGRID") == "physical" ? 1
            : int.TryParse(Environment.GetEnvironmentVariable("WPF_CT_DISTGRID"), out int dg) ? dg : 0;

        /// <summary>WPF_CT_STEMSNAP: distances at or below this many 64ths of a pixel round on the
        /// physical grid. 0 disables it.</summary>
        private static readonly int s_stemSnap =
            int.TryParse(Environment.GetEnvironmentVariable("WPF_CT_STEMSNAP"), out int ss) ? ss : 0;

        /// <summary>RE-MEASURED 2026-08-31, after the stroke-weight correction changed the
        /// landscape, and STILL NOT TAKEN -- but for a reason worth recording, because on the
        /// window alone it looks like the biggest win available.
        /// <para>WPF_CT_POSGRID=3, the LAMP grid, takes the control window from 1,216,266 to
        /// 1,064,610. A hundred and fifty thousand, and the rationale is sound: ClearType draws x
        /// in thirds, so a stem edge on a lamp boundary saturates that lamp. The earlier
        /// measurement had it worse, so the improvement is real and new.</para>
        /// <para>It is OVERFITTING, and the per-size structural numbers say so plainly. It helps
        /// exactly one size -- the one the window is set in -- and hurts every other:</para>
        /// <para>            @11    @12    @13    @19   b@12   b@19</para>
        /// <para>  sixteenth 1587   1673   1982   1516   1102   1547</para>
        /// <para>  lamp      1770   1528   2187   2677   1659   2712</para>
        /// <para>regular@12 improves by 145 and regular@19 gets 75% worse. The window is all
        /// 12ppem, so the window cannot see the cost. A metric that contains only one size cannot
        /// choose a rule that has to hold at all of them.</para></summary>
        /// <summary>WHERE THE CURVE GLYPHS ACTUALLY DIFFER, measured lamp by lamp so the next
        /// attempt starts from a number. 'o', 'e' and 'c' at 12ppem have IDENTICAL leading edges as
        /// each other, and this is what they read (GDI first, then ours at three position grids):
        /// <para>  GDI            0,  36, 111, 197, 255      deconvolves to [0, half, 1, 1]</para>
        /// <para>  ours 1/16      0,  73, 153, 255           deconvolves to [0, 1, 1]</para>
        /// <para>  ours lamp      0,  73, 153, 255           unchanged -- the extremum does not move</para>
        /// <para>  ours physical  0,   0,  73, 153, 255      peak now on GDI's lamp</para>
        /// <para>Two facts. GDI's edge carries a HALF-LIT lamp and none of ours does, at any grid --
        /// its edge falls in the middle of a lamp where ours falls on a boundary, a sixth of a pixel
        /// apart. And whole-pixel rounding puts our PEAK on GDI's lamp while the sixteenth leaves it
        /// one lamp left, so GDI's curve extremum sits about a sixth of a pixel BEFORE where whole-
        /// pixel rounding would put it -- 0.833 against our unhinted 0.5625 at 12ppem.</para>
        /// <para>No grid tried reproduces that: 0.5625 rounds to 0.5 on sixths, 0.667 on thirds,
        /// 0.5625 on sixteenths, 1.0 on whole pixels. And the lamp grid does not move this point at
        /// all, which says the leading edge is not set by the MDAP that rounds it but by a
        /// neighbour IUP carries.</para></summary>
        private static readonly int s_positionGrid =
            int.TryParse(Environment.GetEnvironmentVariable("WPF_CT_POSGRID"), out int pg) ? pg : 0;

        private static readonly bool s_positionsOnPhysicalGrid =
            Environment.GetEnvironmentVariable("WPF_CT_POSGRID") == "physical";

        /// <summary>Whether only MDAP -- rounding a point where it already sits -- takes the
        /// physical grid, leaving MIAP on the ClearType one.</summary>
        private static readonly bool s_posGridMdapOnly =
            Environment.GetEnvironmentVariable("WPF_CT_POSGRID") == "mdap";

        /// <summary>What GETINFO tells the face's program about the rasterizer running it.
        /// <para>A BI-LEVEL PASS MUST ANSWER NO. It is not a mode of ours, it is an impersonation --
        /// "what would a bi-level rasterizer have made of this glyph" -- and a face that asks the
        /// question branches on the answer. Verdana asks: its program equalizes the advances of the
        /// round lowercase (a b d e g o p q all land on 8 at twelve pixels an em, from linear widths
        /// spread over 7.15 to 7.48) on the bi-level branch, and leaves them alone on the ClearType
        /// one. Answering "ClearType" during a compatible-width measurement got us the ClearType
        /// branch's advances, which are not what Windows lays out with.</para>
        /// <para>And it is NOT the other way round either, tested 2026-09-05 when Arial Regular's
        /// 7 and 8ppem advances came out hinted where GDI's ClearType realization spaces them
        /// linearly: letting the bi-level pass hear ClearType -- everywhere, or in fpgm/prep only,
        /// with and without symmetric smoothing, grey, stripes, versions 35 to 42 -- never made
        /// Arial 8 linear (best 212 of 213 pixels a line) and broke Arial and Times at 9, 10, 14
        /// and 20 (+5 to +14 a line). The answer was the 'gasp', see
        /// TrueTypeFont.GaspDeclinesClearTypeGridFit.</para></summary>
        internal static bool ClearTypeInfo =>
            s_ctInfoAllowed && TrueTypeFont.ClearTypeRendering && !BiLevelPass;

        /// <summary>How the ADVANCE phantom is quantized before the program runs. 0 round (the
        /// default), 1 ceil, 2 not at all. WPF_PP2_ROUND.
        /// <para>Verdana is why this is a knob. It ships no 'hdmx', so its advances come from this
        /// phantom, and they came out thirteen pixels short over a line of fifty. Its program never
        /// MOVES the phantom -- it only reads it, as the rp0 of the MIRP that places the right edge
        /// -- so whatever GDI has there, it has before the first instruction.</para>
        /// <para>NOT quantizing it (2) is the only knob in this file that moves Times' italic,
        /// the worst row in the specimen: mean |per-glyph displacement| 0.311 -> 0.274. It is
        /// still not the answer, because it BREAKS the faces that were right: Segoe UI's italic
        /// goes 0.010 -> 0.188 and Times' own roman 0.025 -> 0.056, while Arial's roman improves
        /// 0.234 -> 0.201. A knob whose best value differs per face is a sign that GDI is doing
        /// something here we are approximating, not a setting to pick. Default stays 0.</para></summary>
        /// <summary>The advance the glyph will actually be laid out at, in 64ths, or 0 when it is
        /// not known. Set by the font before a ClearType run; used only by advance-phantom mode 3.
        /// </summary>
        internal static int CompatibleAdvance64;

        private static readonly int s_advancePhantom =
            int.TryParse(Environment.GetEnvironmentVariable("WPF_PP2_ROUND"), out int pp) ? pp
            // Mode 7 of the compatible-width correction moves features by how far the LINEAR
            // advance is from the one the glyph is laid out at, so its run starts with the
            // advance phantom on the linear advance, unrounded.
            : TrueTypeFont.CompatibleWidthMode == 7 ? 2 : TrueTypeFont.CompatibleWidthMode is 8 or 9 ? 4 : 0;

        /// <summary>MODE 9: the pre-scale of mode 8, with BLACK distances measured on the outline
        /// as it was before the pre-scale. Mode 8 scaled everything, so an MDRP across a stem
        /// measured a squeezed stem and rounded that; here the positions arrive scaled (white
        /// space, SHPIX nudges and interpolations ride on the scaled outline) while a black
        /// MDRP/MIRP still sees the stem at its true width. This is the reading of GDI's own
        /// edges that reproduces Consolas 'l'/'I', Tahoma 'l' at 12 and 16 and Verdana 'l' at
        /// once, where mode 7's feature centres cannot: the stem of Consolas 'l' rides with the
        /// foot it is interpolated inside and does not move, while 'I', anchored by MDAP, moves
        /// with its scaled anchor.</summary>
        private static readonly bool s_blackOnInk = TrueTypeFont.CompatibleWidthMode == 9;
        private bool _preScaled;
        private float _preScaleRatio = 1f;
        /// <summary>WPF_CT_WHITECVT=1: under mode 9, scale white/grey control values by the pre-scale ratio.</summary>
        private static readonly bool s_scaleWhiteCvt = Environment.GetEnvironmentVariable("WPF_CT_WHITECVT") == "1";

        /// <summary>STAMM'S DOUBLE-CHECK. Compatible widths keep stroke weights and scale stroke
        /// positions, and the rasterizer tells them apart by "patterns of TrueType code
        /// conventionally associated with constraining stroke weights and positions" -- but then
        /// "sometimes I tried to 'double-check': maybe this 'white link' was meant to be a 'black
        /// link?'" (Raster Tragedy ch.4). The reverse check is what this is: a link the program
        /// calls BLACK whose chord runs through a COUNTER is not a stroke's weight, it is a
        /// position, and GDI scales it. Arial 'd' MIRPs bowl-left to stem-right (cvt[116], 10.84px
        /// at 24ppem) as black; GDI's ink is WIDER than natural there (bowl-left 1.19, stem-right
        /// 12.33 = 10.84 x 1.049), and reading that link as white takes the edge-solver residual
        /// of 'd'@24 from 8,312 to 2,058 with the stem-right edge exact.
        /// <para>The test is the midpoint of the chord, sampled a hair to either side (the two
        /// ends can be ADJACENT points -- the angled cut of Arial Italic 'e''s terminal -- and then
        /// the chord IS the outline). Specimen: 10,399,039 -> 10,319,044, no face worse.</para>
        /// <para>WPF_CT_BLACKMAX picks the rule: unset/-1 this midpoint test; 0 off (mode 7 as
        /// shipped before); N>0 a milli-em ceiling on a stroke instead (200: 10,337,183, catches
        /// Arial's diagonal 'v'/'W'/'x' links and Consolas '0' bowl-to-bowl along its slash, but
        /// flips bold stems below 180 and every Times Italic 'f'@24 link); -2 the whole chord in
        /// ink (10,340,031: Times Italic +23k); -4 midpoint AND the link no longer than 1.5x the
        /// horizontal ink run it crosses (10,332,950: wins the diagonals at 24ppem and loses them
        /// at 10-16, so GDI's diagonal rule is not this); -5 midpoint AND a pixel ceiling
        /// WPF_CT_BLACKMAXPX in 1/64 px (320: 10,321,848). WPF_CT_BLACKMID=1 adds the midpoint
        /// test to a milli-em ceiling (200: 10,320,295).</para></summary>
        private static readonly bool s_blackMidToo = Environment.GetEnvironmentVariable("WPF_CT_BLACKMID") == "1";
        private static readonly int s_blackMaxPx =
            int.TryParse(Environment.GetEnvironmentVariable("WPF_CT_BLACKMAXPX"), out int bpx) ? bpx : 0;
        private static readonly int s_blackMaxMilliEm =
            int.TryParse(Environment.GetEnvironmentVariable("WPF_CT_BLACKMAX"), out int bm) ? bm : -1;

        /// <summary>The distance type a link is treated as: the program's, unless it is a black
        /// link that measures a position rather than a stroke (see <see cref="s_blackMaxMilliEm"/>).</summary>
        private int EffectiveLinkType(int programType, int zoneP, int p, int zoneR, int r)
        {
            if (programType != 1 || s_blackMaxMilliEm == 0 || !IsHorizontalProjection) return programType;
            if (s_blackMaxMilliEm < 0 || s_blackMidToo)
            {
                if (zoneP != 1 || zoneR != 1 || p >= _realPoints || r >= _realPoints) return programType;
                Zone z = _glyphZone;
                float mx = (z.OrusX[p] + z.OrusX[r]) / 2f, my = (z.OrusY[p] + z.OrusY[r]) / 2f;
                float dx = z.OrusX[p] - z.OrusX[r], dy = z.OrusY[p] - z.OrusY[r];
                float len = MathF.Sqrt(dx * dx + dy * dy), eps = _unitsPerEm / 128f;
                float nx = len > 0 ? -dy / len * eps : 0, ny = len > 0 ? dx / len * eps : eps;
                bool inInk = InInk(z, mx + nx, my + ny) || InInk(z, mx - nx, my - ny);
                if (inInk && s_blackMaxMilliEm == -2)
                    for (int k = 1; k <= 7 && inInk; k += 2)
                    {
                        float t = k / 8f, sx = z.OrusX[r] + dx * t, sy = z.OrusY[r] + dy * t;
                        inInk = InInk(z, sx + nx, sy + ny) || InInk(z, sx - nx, sy - ny);
                    }
                if (inInk && s_blackMaxMilliEm == -4)
                {
                    float run = HorizontalInkRun(z, mx, my + ny, dx < 0 ? -nx : nx);
                    if (run <= 0) run = HorizontalInkRun(z, mx, my - ny, dx < 0 ? -nx : nx);
                    if (MathF.Abs(dx) > 1.5f * run + eps) inInk = false;
                }
                if (inInk && s_blackMaxMilliEm == -5 && s_blackMaxPx > 0
                    && Math.Abs(MeasureOriginal(zoneP, p, zoneR, r, black: true)) > s_blackMaxPx)
                    inInk = false;
                if (_dumpActive && !inInk)
                    Console.Error.WriteLine($"      black link {r}->{p} ({z.OrusX[r]},{z.OrusY[r]})->({z.OrusX[p]},{z.OrusY[p]}) spans a counter: WHITE");
                if (!inInk) return 2;
                if (s_blackMaxMilliEm < 0) return 1;
            }
            int ink = Math.Abs(MeasureOriginal(zoneP, p, zoneR, r, black: true));
            long threshold = (long) s_blackMaxMilliEm * _ppem * 64 / 1000;
            if (_dumpActive && ink > threshold && zoneP == 1 && zoneR == 1 && p < _realPoints && r < _realPoints)
            {
                Zone z = _glyphZone;
                Console.Error.WriteLine($"      black link {r}->{p} ({z.OrusX[r]},{z.OrusY[r]})->({z.OrusX[p]},{z.OrusY[p]}) {ink / 64f:0.00}px over threshold: WHITE");
            }
            return ink > threshold ? 2 : programType;
        }

        /// <summary>The length of the horizontal run of ink through (x + ox, y), marched outwards
        /// in steps of 1/256 em until the outline is left on each side.</summary>
        private float HorizontalInkRun(Zone z, float x, float y, float ox)
        {
            float step = _unitsPerEm / 256f;
            if (!InInk(z, x + ox, y)) return 0;
            float left = x + ox, right = x + ox;
            for (int i = 0; i < 512 && InInk(z, left - step, y); i++) left -= step;
            for (int i = 0; i < 512 && InInk(z, right + step, y); i++) right += step;
            return right - left;
        }

        /// <summary>Non-zero winding of the unscaled outline at (x, y) in font units. Its control
        /// polygon stands in for the curves -- exact enough for a point deep in a stem or a
        /// counter, which is all this is asked about.</summary>
        private bool InInk(Zone z, float x, float y)
        {
            int winding = 0, start = 0;
            for (int c = 0; c < _contourCount; c++)
            {
                int end = z.Contours[c];
                if (end < start) { start = end + 1; continue; }
                for (int i = start; i <= end; i++)
                {
                    int j = i == end ? start : i + 1;
                    float x0 = z.OrusX[i], y0 = z.OrusY[i], x1 = z.OrusX[j], y1 = z.OrusY[j];
                    if (y0 <= y ? y1 > y : y1 <= y)
                    {
                        float cross = (x1 - x0) * (y - y0) - (x - x0) * (y1 - y0);
                        if (y1 > y0 ? cross > 0 : cross < 0) winding += y1 > y0 ? 1 : -1;
                    }
                }
                start = end + 1;
            }
            return winding != 0;
        }

        private static readonly bool s_ctInfoAllowed =
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
            // WPF_PREP_DUMP traces the pre-program the way WPF_HINT_DUMP traces a glyph's.
            _dumpActive = Environment.GetEnvironmentVariable("WPF_PREP_DUMP") == "1";
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
            finally { _inPreProgram = false; _dumpActive = false; }

            _prepState = _gs;
            // WPF_CVT_DUMP writes the control values the pre-program LEAVES BEHIND, in pixels and
            // again divided by ppem. The second column is the point: a control value that is simply
            // scaled is a constant there at every size, so a per-size branch in the pre-program --
            // the one thing not yet ruled out for the 11-to-13 anomaly -- shows up as an entry that
            // moves when nothing else does. Comparing whole prep TRACES across sizes cannot show
            // that; they differ everywhere for uninteresting reasons.
            string? cvtDump = Environment.GetEnvironmentVariable("WPF_CVT_DUMP");
            if (!string.IsNullOrEmpty(cvtDump))
            {
                var sb = new System.Text.StringBuilder();
                for (int i = 0; i < _scaledCvt.Length; i++)
                    sb.Append($"font {_controlValues.Length}_{_fontProgram.Length} ppem {_ppem} cvt[{i}] {_scaledCvt[i] / 64f:0.0000} "
                              + $"{(_ppem > 0 ? _scaledCvt[i] / 64f / _ppem : 0):0.00000}"
                              + System.Environment.NewLine);
                System.IO.File.AppendAllText(cvtDump!, sb.ToString());
            }
            return true;
        }

        private int[] _scaledCvt = Array.Empty<int>();

        private void ScaleControlValues()
        {
            if (_scaledCvt.Length != _controlValues.Length)
                _scaledCvt = new int[_controlValues.Length];
            for (int i = 0; i < _controlValues.Length; i++)
                _scaledCvt[i] = Scale(_controlValues[i]);
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
                    z.OrgX[i] = z.CurX[i] = Scale(glyph.X[i]);
                    z.OrgY[i] = z.CurY[i] = Scale(glyph.Y[i]);
                }
                z.InkX[i] = z.OrgX[i];

                // Assigning the tags is also what UNTOUCHES the points. A component's own program
                // will have touched some of them; the composite's program is entitled to move them
                // again, and IUP must not treat them as already placed.
                z.Tags[i] = glyph.OnCurve[i] ? TagOn : (byte)0;
            }

            // The two horizontal phantom points are rounded to the grid before hinting starts, the
            // way a BI-LEVEL rasterizer does it: a face's program reads them to find its own side
            // bearings. Under ClearType, x carries sixteen times the resolution and quantizing the
            // advance to a whole pixel throws that away before the program has even started.
            // Verdana's 'o' at 12ppem is what it costs: the advance is 7.283px, this made it 7.0,
            // and the right side bearing MIRP then placed the right edge at 7.0 - 0.625 = 6.375
            // instead of 6.658 -- the glyph came out 0.28px narrow, which is most of why Verdana
            // was the worst face on the specimen.
            // KEEP IT, even under ClearType, and the reason is worth the paragraph. Leaving them
            // unrounded IS better geometry -- Verdana's 'o' comes out 0.28px wider, its true width
            // -- and nearly every band improves, Consolas by 39,931. But rounding them is what makes
            // the FITTED advance equal the bi-level advance, and that is exactly what compatible
            // widths means: "the hints are executed once to determine the width in bi-level
            // rendering". Unrounded, the fitted span differs from the bi-level advance by a fraction
            // for every glyph, compatible widths scales every glyph's ink by that fraction, and
            // Segoe UI italic at 9pt goes 9,760 -> 152,480. Rounding the span instead of the
            // phantoms fixes the italics and loses the rest: 10,686,251 against 10,072,594 here.
            // Measured every way round; this is the best of them.
            // MODE 4: PRE-SCALE. The glyph arrives already stretched onto the advance it will be
            // laid out at -- every x, phantoms included, scaled by that advance over the linear
            // one -- and the program then runs on it with the control values left alone. A stem
            // placed by MIRP keeps its control-value width; a point placed relative to an
            // untouched neighbour rides on the scaled outline. Nothing is corrected afterwards.
            _preScaled = false;
            if (s_advancePhantom == 4 && !glyph.Composite && CompatibleAdvance64 > 0 && !BiLevelPass)
            {
                int lin = z.CurX[glyph.PointCount + 1] - z.CurX[glyph.PointCount];
                if (lin > 0 && lin != CompatibleAdvance64)
                {
                    float ratio = CompatibleAdvance64 / (float) lin;
                    _preScaled = true;
                    _preScaleRatio = ratio;
                    for (int i = 0; i < n; i++)
                    {
                        z.OrgX[i] = z.CurX[i] = (int) MathF.Round(z.CurX[i] * ratio);
                        z.OrusX[i] = (int) MathF.Round(z.OrusX[i] * ratio);
                    }
                }
            }
            z.CurX[glyph.PointCount] = Pix(z.CurX[glyph.PointCount]);
            z.CurX[glyph.PointCount + 1] = s_advancePhantom switch
            {
                // THE BI-LEVEL PASS IS THE MEASUREMENT OF THE ADVANCE, and a bi-level rasterizer
                // rounds the phantom before the first instruction, whatever mode the drawing run
                // is in. Mode 7 leaves it unrounded for the ClearType run (that is what mode 7 is),
                // and this measurement inherited that: Verdana 'x' at 9ppem starts pp2 at 5.33,
                // its two DELTAs add 2 and a third takes 0.75 off -- 6.58, rounds to 7 -- where
                // GDI starts at 5, ends at 6.25 and spaces the glyph at 6. Same for 'i' and 'l'
                // at 8ppem: 2.195 + 1 - 0.25 = 2.95 -> 3 against GDI's 2.
                _ when BiLevelPass => Pix(z.CurX[glyph.PointCount + 1]),
                1 => (z.CurX[glyph.PointCount + 1] + 63) & ~63,      // ceil
                2 => z.CurX[glyph.PointCount + 1],                   // leave it alone
                // 3: THE BOX THE PROGRAM SHOULD BE FITTING INSIDE. Compatible widths means the
                // glyph is laid out at the BI-LEVEL advance, and that is the advance Windows uses:
                // Arial's 'I' at 16ppem advances 3px, not the 4 its linear width rounds to. Start
                // the ClearType run with that box and the program is positioning within the same
                // space GDI's is, instead of inside a box a pixel too wide that a later scale then
                // has to squeeze -- which is what damages the stems.
                3 or 4 when CompatibleAdvance64 > 0
                    => z.CurX[glyph.PointCount] + CompatibleAdvance64,
                _ => Pix(z.CurX[glyph.PointCount + 1]),              // round, as a bi-level rasterizer does
            };

            for (int i = 0; i < glyph.EndPoints.Length; i++)
                z.Contours[i] = glyph.EndPoints[i];
            _contourCount = glyph.EndPoints.Length;
            _realPoints = glyph.PointCount;
            if (_xLink.Length < _realPoints) _xLink = new int[_realPoints];
            for (int i = 0; i < _realPoints; i++) _xLink[i] = i;
            if (_linkA.Length < _realPoints * 2) { _linkA = new int[_realPoints * 2]; _linkB = new int[_realPoints * 2]; }
            _linkCount = 0;
            if (_stemA.Length < _realPoints * 2) { _stemA = new int[_realPoints * 2]; _stemB = new int[_realPoints * 2]; }
            _stemCount = 0;
            int np = _realPoints + 4;
            if (_phaseP0.Length < np)
            {
                _phaseP0 = new int[np]; _phaseP1 = new int[np];
                _phaseVal = new int[np]; _phaseDone = new bool[np];
            }
            for (int i = 0; i < np; i++) { _phaseP0[i] = -1; _phaseP1[i] = -1; }
        }

        // ---- x features ------------------------------------------------------------------------

        /// <summary>WHICH POINTS THE PROGRAM TIED TOGETHER IN X. Every MDRP, MIRP, MSIRP and ALIGNRP
        /// places a point at a distance from a reference point, and every SHP moves one by what its
        /// reference moved: the two are one feature -- the two sides of a stem, the bar of a 'T'
        /// riding on its stem, the arm ends of an 'E' spaced from each other. Union-find over the
        /// glyph's outline points, reset per glyph. IP does not link (it interpolates BETWEEN two
        /// features), nor does SHPIX (a nudge, not a placement), nor anything from the phantoms
        /// or twilight -- a stem MIRP'd from the advance phantom is its own feature.</summary>
        private int[] _xLink = Array.Empty<int>();

        private int FindX(int p)
        {
            while (_xLink[p] != p) { _xLink[p] = _xLink[_xLink[p]]; p = _xLink[p]; }
            return p;
        }

        /// <summary>WPF_CT_LINKTYPES: which MDRP/MIRP distance types tie points into one feature --
        /// bit 0 grey, bit 1 black, bit 2 white; default black only. Beat Stamm's account of GDI's
        /// compatible widths has the rasterizer telling "black links" (stroke weights, kept) from
        /// "white links" (positions, scaled), so the partition is worth a knob -- and it is the
        /// knob that mattered: over the weight report (six faces, three styles, five sizes) black
        /// only scores 10,399,039 against 10,867,238 for all three, 10,777,519 for grey+black and
        /// 10,466,790 for black+white. A grey MDRP is how a slanted stem's far corner is placed
        /// from its near one (Verdana Italic's 'l': 0xC0 from the bottom-right to the top-right),
        /// and a white one spaces strokes apart; tying either into the feature makes the whole
        /// glyph one rigid body, which is exactly the scale-within-a-stem mode 7 exists to avoid.
        /// </summary>
        /// <summary>Width (64ths) above which an x-link is SPACING between features rather than
        /// the two sides of one feature, so it does not merge them. See the use site.</summary>
        private static readonly int s_featLink =
            int.TryParse(Environment.GetEnvironmentVariable("WPF_CT_FEATLINK"), out int fl) ? fl : int.MaxValue;

        private static readonly int s_linkTypes =
            int.TryParse(Environment.GetEnvironmentVariable("WPF_CT_LINKTYPES"), out int lt) ? lt : 2;

        private void LinkX(int zoneP, int p, int zoneR, int r, int distanceType = -1)
        {
            if (_inPreProgram || zoneP != 1 || zoneR != 1) return;
            // PHASE tree: record p's parent for EVERY link -- any colour, horizontal or diagonal,
            // and even when the reference is a PHANTOM (the advance/lsb). The phase ORIGINATES at
            // the phantoms and flows to whatever was placed from them, so filtering by link colour
            // (as the stem path below does) starves the tree and leaves most of the glyph unmoved.
            if ((uint) p < (uint) _phaseP0.Length && (uint) r < (uint) _phaseP0.Length && _phaseP0[p] < 0)
                _phaseP0[p] = PhaseAncestor(r, p);
            if (distanceType >= 0 && (s_linkTypes & (1 << distanceType)) == 0) return;
            if ((uint) p >= (uint) _realPoints || (uint) r >= (uint) _realPoints) return;
            // ALL links, horizontal or DIAGONAL, kept for the coloring model: a 'w's diagonal
            // strokes are placed by diagonal MIRPs (SDPVTL) that the horizontal-only path below
            // skips, yet each is one of GDI's stems. r and p are the stroke's two edges.
            if (_stemCount < _stemA.Length) { _stemA[_stemCount] = r; _stemB[_stemCount] = p; _stemCount++; }
            if (!IsHorizontalFreedom) return;
            // The INDIVIDUAL link (r -> p), kept as its own pair. The union-find below merges the
            // whole glyph's links into features, but GDI's stem records are one link each -- an 'H'
            // crossbar ties both stems into one feature yet they are two stems -- so the compatible
            // -width pass that grid-fits each stem needs the pairs, not the closure.
            if (_linkCount < _linkA.Length) { _linkA[_linkCount] = r; _linkB[_linkCount] = p; _linkCount++; }
            // A NARROW link ties two edges of ONE feature (a stem's two sides); a WIDE link is
            // SPACING between two features. Merging both makes a 'w' a single feature -- all four
            // diagonal strokes chained through the wide anchor links -- so the compatible-width
            // pass can only slide the whole letter, never open the gaps between its strokes, which
            // is what GDI does. WPF_CT_FEATLINK is the width (64ths) above which a link no longer
            // merges. Default is unbounded, i.e. the old closure.
            if (s_featLink < int.MaxValue)
            {
                int dx = _glyphZone.CurX[p] - _glyphZone.CurX[r];
                if ((dx < 0 ? -dx : dx) > s_featLink) return;
            }
            int a = FindX(p), b = FindX(r);
            if (a != b) _xLink[a] = b;
        }

        private int[] _linkA = new int[64], _linkB = new int[64];
        private int _linkCount;
        private int[] _stemA = new int[128], _stemB = new int[128];   // all links incl. diagonal
        private int _stemCount;
        private int[] _phaseP0 = new int[128], _phaseP1 = new int[128];  // per-point placement parents
        private int[] _phaseVal = new int[128];                          // memoised phase, 26.6
        private bool[] _phaseDone = new bool[128];

        /// <summary>The individual (reference, placed) x-links the program made -- one stem each.
        /// Returns the count; fills the caller's arrays.</summary>
        internal int ReadXLinks(int[] a, int[] b)
        {
            int n = Math.Min(_linkCount, Math.Min(a.Length, b.Length));
            Array.Copy(_linkA, a, n); Array.Copy(_linkB, b, n);
            return n;
        }

        /// <summary>After a Hint: each outline point's feature (the index of a representative
        /// point) and whether the program touched it in x.</summary>
        internal void ReadXFeatures(int[] feature, bool[] touchedX)
        {
            int n = Math.Min(feature.Length, _realPoints);
            for (int i = 0; i < n; i++)
            {
                feature[i] = FindX(i);
                touchedX[i] = (_glyphZone.Tags[i] & TagTouchX) != 0;
            }
        }

        private bool AnyTouchedX()
        {
            for (int i = 0; i < _realPoints; i++)
                if ((_glyphZone.Tags[i] & TagTouchX) != 0) return true;
            return false;
        }

        private int _contourCount;
        private int _realPoints;

        /// <summary>Where every point of the last hinted glyph started, where it finished, and
        /// WHICH OF THEM THE PROGRAM TOUCHED IN X.
        /// <para>The touch set is the thing the fitted coordinates cannot be read for directly and
        /// the thing that decides everything after it: a point the program touches is placed by an
        /// instruction, a point it does not is placed by IUP interpolating between the two nearest
        /// touched ones. Two interpreters that touch different sets produce different outlines from
        /// identical instructions, and no amount of comparing coordinates says which set was used.
        /// GDI's set is not observable either -- but it is DERIVABLE, because IUP can only ever put
        /// an untouched point where its anchors put it, so a coordinate GDI has that no IUP could
        /// produce proves GDI touched that point.</para></summary>
        internal sealed class GlyphPoints
        {
            public float[] StartX = Array.Empty<float>();   // scaled, unfitted, pixels
            public float[] FitX = Array.Empty<float>();      // after the glyph's program
            public float[] StartY = Array.Empty<float>();
            public int[] OrusX = Array.Empty<int>();      // font units -- what IUP takes its ratio in
            public float[] FitY = Array.Empty<float>();
            public bool[] TouchedX = Array.Empty<bool>();
            public bool[] OnCurve = Array.Empty<bool>();
            public int[] EndPoints = Array.Empty<int>();
            public int PointCount;
        }

        internal static bool s_capturePoints;
        internal GlyphPoints? LastPoints;

        private void CapturePoints(GlyphProgram glyph)
        {
            Zone z = _glyphZone;
            int n = glyph.PointCount;
            var g = new GlyphPoints
            {
                StartX = new float[n], FitX = new float[n],
                StartY = new float[n], FitY = new float[n], OrusX = new int[n],
                TouchedX = new bool[n], OnCurve = new bool[n],
                EndPoints = (int[]) glyph.EndPoints.Clone(), PointCount = n,
            };
            for (int i = 0; i < n; i++)
            {
                g.StartX[i] = z.OrgX[i] / 64f;
                g.FitX[i] = z.CurX[i] / 64f;
                g.StartY[i] = z.OrgY[i] / 64f;
                g.OrusX[i] = z.OrusX[i];
                g.FitY[i] = z.CurY[i] / 64f;
                g.TouchedX[i] = (z.Tags[i] & TagTouchX) != 0;
                g.OnCurve[i] = (z.Tags[i] & TagOn) != 0;
            }
            LastPoints = g;
        }

        /// <summary>WPF_CT_COLOR=1: GDI's ClearType "coloring" of stems, ported from dwrite's GC*
        /// chain, done INSIDE the interpreter so the outline follows via the real IUP rather than a
        /// post-pass edge-snap. Each narrow black-link stem is snapped to cover whole ClearType
        /// cells -- floor the left edge, ceil the right, centre kept -- then IUP in x re-flows the
        /// untouched points, as AlignIsolatedStems' isolated path does (position kept, width fixed).
        /// <para>MEASURED AND IT DOES NOT HELP, which is itself the finding. The score falls
        /// monotonically as the cell shrinks -- 22.7M at a whole 64th pixel, 12.9M at a half, 10.1M
        /// at a ClearType third (21), 8.4M at 16 -- toward the 5,485,079 baseline of doing nothing.
        /// Snapping our already-fitted stems to ANY grid moves them AWAY from GDI, because our
        /// interpreter's ClearType x ALREADY embodies GDI's coloring: the font program does the
        /// stem control and our fit keeps it, landing within 6% of GDI's pixels. GDI's GC* pass is
        /// its rasterizer computing that same result from originals; re-applying a coarse copy on
        /// top double-processes. The residual 6% is in the exact per-stem arithmetic (CalcHW2 width
        /// regularization, FixBands, Adjust's round-state), reachable only by fitting stems GDI's
        /// way from the start -- a from-scratch replacement of our x-fitting, not a bolt-on -- and
        /// with real doubt it would beat 6%. Kept default-off as the record of the port attempt;
        /// see the reverse-engineering memory for the full decompiled algorithm.</para></summary>
        private static readonly bool s_ctColor =
            Environment.GetEnvironmentVariable("WPF_CT_COLOR") == "1";

        private static readonly int s_ctColorStemMax =
            int.TryParse(Environment.GetEnvironmentVariable("WPF_CT_COLOR_STEMMAX"), out int cm) ? cm : 192;

        /// <summary>The ClearType grid unit in 64ths. ClearType oversamples x by 3, so a "pixel" to
        /// the coloring pass is 64/3 in our space. WPF_CT_COLOR_UNIT overrides.</summary>
        private static readonly int s_ctColorUnit =
            int.TryParse(Environment.GetEnvironmentVariable("WPF_CT_COLOR_UNIT"), out int cu) && cu > 0 ? cu : 21;

        private void ColorStems()
        {
            Zone z = _glyphZone;
            bool moved = false;
            for (int k = 0; k < _linkCount; k++)
            {
                int r = _linkA[k], p = _linkB[k];
                if ((uint) r >= (uint) _realPoints || (uint) p >= (uint) _realPoints) continue;
                if ((z.Tags[r] & TagTouchX) == 0 || (z.Tags[p] & TagTouchX) == 0) continue;
                int lo = r, hi = p;
                if (z.CurX[hi] < z.CurX[lo]) (lo, hi) = (hi, lo);
                int w = z.CurX[hi] - z.CurX[lo];
                if (w <= 0 || w > s_ctColorStemMax) continue;         // narrow stems only
                // Keep the stem where the program put it; snap it to cover whole CLEARTYPE cells.
                // ClearType oversamples x by 3, so its grid unit is a THIRD of a real pixel, not a
                // whole one -- snapping to 64ths fattened every stem 3x. Unit is WPF_CT_COLOR_UNIT
                // (64/3 by default).
                int u = s_ctColorUnit;
                int newLo = (z.CurX[lo] / u) * u;                      // floor to a cell
                int newHi = ((z.CurX[hi] + u - 1) / u) * u;           // ceil to a cell
                if (newHi < newLo + u) newHi = newLo + u;
                if (z.CurX[lo] != newLo) { z.CurX[lo] = newLo; moved = true; }
                if (z.CurX[hi] != newHi) { z.CurX[hi] = newHi; moved = true; }
            }
            if (moved) InterpolateUntouched(horizontal: true);
        }

        /// <summary>WPF_CT_COLOR_VALIDATE dumps the faithful coloring model's input and output for the
        /// current glyph so it can be diffed against SolveGdisEdges by hand. Reads nothing back into
        /// the render; a pure diagnostic for building the port. Prints each stem's original and
        /// fitted edges and y-extent, the counters, and the model's coloured edges.</summary>
        private static readonly bool s_ctColorValidate =
            Environment.GetEnvironmentVariable("WPF_CT_COLOR_VALIDATE") is "1" or "links";

        private void ValidateColoring()
        {
            Zone z = _glyphZone;
            var stems = new System.Collections.Generic.List<GcStem>();
            for (int k = 0; k < _stemCount; k++)
            {
                int r = _stemA[k], p = _stemB[k];
                if ((uint) r >= (uint) _realPoints || (uint) p >= (uint) _realPoints) continue;
                int lo = r, hi = p;
                if (z.CurX[hi] < z.CurX[lo]) (lo, hi) = (hi, lo);
                var s = new GcStem
                {
                    KeyA = z.OrusX[lo], KeyB = z.OrusX[hi],
                    Lo = z.OrgX[lo] << 10, Hi = z.OrgX[hi] << 10,   // 26.6 -> 16.16
                    E0 = z.CurX[lo] << 10, E1 = z.CurX[hi] << 10,
                    YLo = Math.Min(z.CurY[lo], z.CurY[hi]) << 10,
                    YHi = Math.Max(z.CurY[lo], z.CurY[hi]) << 10,
                    Width = (z.CurX[hi] - z.CurX[lo]) << 10,
                    NCount = 1,
                };
                stems.Add(s);
            }
            if (Environment.GetEnvironmentVariable("WPF_CT_COLOR_VALIDATE") == "links")
            {
                var lb = new System.Text.StringBuilder("=== RAW LINKS: " + _stemCount + "\n");
                for (int k = 0; k < _stemCount; k++)
                {
                    int r = _stemA[k], p = _stemB[k];
                    if ((uint) r >= (uint) _realPoints || (uint) p >= (uint) _realPoints) continue;
                    lb.Append($"  link r{r}=({z.CurX[r] / 64f:0.00},{z.CurY[r] / 64f:0.00}) "
                            + $"p{p}=({z.CurX[p] / 64f:0.00},{z.CurY[p] / 64f:0.00}) "
                            + $"dx={(z.CurX[p] - z.CurX[r]) / 64f:0.00} dy={(z.CurY[p] - z.CurY[r]) / 64f:0.00}\n");
                }
                Console.Error.WriteLine(lb.ToString());
                return;
            }
            stems.Sort((a, b) => a.E0.CompareTo(b.E0));
            var sb = new System.Text.StringBuilder("=== COLOR VALIDATE: " + stems.Count + " stems\n");
            foreach (var s in stems)
                sb.Append($"  stem orig[{s.KeyA},{s.KeyB}] fitted[{s.E0 / 65536f:0.00},{s.E1 / 65536f:0.00}] y[{s.YLo / 65536f:0.0}..{s.YHi / 65536f:0.0}] w={s.Width / 65536f:0.00}\n");
            // counters between y-overlapping neighbours
            for (int i = 0; i + 1 < stems.Count; i++)
                if (GdiColoringModel.CounterAdjacent(stems[i + 1], stems[i], out int gap))
                    sb.Append($"  counter {i}<->{i + 1} gap={gap / 65536f:0.00}\n");
            // run the whole set as one path (approximation for validation)
            var path = new System.Collections.Generic.List<GcStem>(stems);
            GdiColoringModel.FixOnePath(path);
            sb.Append("  MODEL coloured E1 (px): ");
            foreach (var s in path) sb.Append($"{s.E1 / 65536f:0.00} ");
            Console.Error.WriteLine(sb.ToString());
        }

        /// <summary>WPF_CT_PHASE=1: GDI's per-point ClearType x PHASE control, ported from dwrite's
        /// ExecutePhaseControl/PhaseShift/CalcAvgXPhaseShift. Each point's sub-pixel x phase is
        /// derived from the PLACEMENT TREE (the reference points it was measured from): a root gets
        /// x*(ctFactor-1); a point with one parent inherits it; a point interpolated between two
        /// (an IP) gets the linear interpolation of its parents' phases. The phase is added to x.
        /// ctFactor is WPF_CT_PHASE_FACTOR in thousandths (default 0 -> off effect until tuned).</summary>
        private static readonly int s_ctPhase =
            int.TryParse(Environment.GetEnvironmentVariable("WPF_CT_PHASE"), out int pm) ? pm : 0;

        private static readonly int s_ctPhaseFactor =
            int.TryParse(Environment.GetEnvironmentVariable("WPF_CT_PHASE_FACTOR"), out int pf) ? pf : 0;

        // phase for a phantom = x * ctFrac; ctFrac = (ctFactor-1) in GDI's terms. fs_NewGlyph
        // computes ctFactor per GLYPH as |FixDiv(A,B)| of two advance-like quantities (default
        // 1.0), so mode 2/3 derive it from this glyph's advances; mode 1 keeps the swept constant.
        private float _ctFrac;

        /// <summary>AddDistance's parent choice, read from its ARM64 (0x1db0c4-0x1db118): the
        /// parent of a placed point is NOT the reference, it is the TOPMOST ancestor reachable
        /// from the reference through links whose endpoints share the same ORIGINAL x (the loop
        /// compares elem+0x20, the scaled original array, not the fitted one). Points stacked at
        /// one x therefore collapse to a single node, which is what makes a stem move as one.
        /// <para>Guarded like IndirectlyDependsOn: if the reference already depends on the point
        /// being placed, the link would close a cycle, so the reference is kept as-is.</para></summary>
        private int PhaseAncestor(int r, int placed)
        {
            int cur = r;
            for (int guard = 0; guard < 50; guard++)
            {
                int par = _phaseP0[cur];
                if (par < 0 || par == placed) break;
                if (_glyphZone.OrgX[par] != _glyphZone.OrgX[cur]) break;
                cur = par;
            }
            return cur;
        }

        private int[] _phasePartner = new int[128];

        /// <summary>DoubleCheckLinkColor's precondition: the two points are consecutive on one
        /// contour and the segment between them is no steeper than 2:1.</summary>
        private bool PhaseAdjacent(int u, int v, int pointCount)
        {
            int start = 0;
            for (int c = 0; c < _contourCount; c++)
            {
                int end = Math.Min(_glyphZone.Contours[c], pointCount - 1);
                if (u >= start && u <= end && v >= start && v <= end)
                {
                    int len = end - start + 1;
                    if (len < 2) return false;
                    int d = (u - start) - (v - start);
                    if (d < 0) d = -d;
                    if (d != 1 && d != len - 1) return false;
                    int dx = _glyphZone.CurX[v] - _glyphZone.CurX[u];
                    int dy = _glyphZone.CurY[v] - _glyphZone.CurY[u];
                    if (dx < 0) dx = -dx;
                    if (dy < 0) dy = -dy;
                    return dy <= 2 * dx;
                }
                start = end + 1;
            }
            return false;
        }

        private int PhaseOf(int p)
        {
            if ((uint) p >= (uint) _phaseDone.Length) return 0;
            if (_phaseDone[p]) return _phaseVal[p];
            _phaseDone[p] = true;                 // guard cycles
            int phase;
            int a = _phaseP0[p], b = _phaseP1[p];
            // THE PAIR OVERRIDES THE TREE. PhaseShift computes, for a point with a link partner,
            //     phase = CompDiv(0x20000, (x[partner] + x[p]) * (ctFactor - 1))
            //           = centre_of_pair * ctFrac
            // and applies it to BOTH edges, in preference to whatever the parent chain would have
            // handed down. That is what makes a stem move by ITS OWN centre: Verdana 'n'@12's
            // right stem (x 355 and 431) wants 393*ctFrac = 21, which is GDI's answer, where
            // inheriting from the advance phantom gives 486*ctFrac = 26.
            int mate = p < _phasePartner.Length ? _phasePartner[p] : -1;
            if (mate >= 0 && mate < _glyphZone.CurX.Length)
            {
                phase = (int) MathF.Round((_glyphZone.CurX[p] + _glyphZone.CurX[mate]) * 0.5f * _ctFrac);
                _phaseVal[p] = phase;
                return phase;
            }
            if (p >= _realPoints)                 // PHANTOM: the phase originates here (the advance)
                phase = (int) MathF.Round(_glyphZone.CurX[p] * _ctFrac);
            else if (a < 0)                        // a regular root the program left unanchored
                // GDI gives it 0 only when PhaseShift's param_3 is clear; otherwise it takes the
                // SAME direct x*(ctFactor-1) the phantoms do (the CompDiv branch reduces to it).
                phase = s_phaseRootDirect ? (int) MathF.Round(_glyphZone.CurX[p] * _ctFrac) : 0;
            else if (b < 0)                        // single parent: inherit its phase
            {
                // ...unless the parent is the LSB phantom, which sits at x = 0, so its phase is
                // 0*ctFrac = 0 and every point anchored to it inherits NO displacement at all.
                // Verdana 'n'@12 shows the damage: its left stem gets 0 where mode 7 gives 5, and
                // the glyph stretches from a dead left edge. Treat that anchor as no anchor.
                phase = (s_phaseLsbDirect && a >= _realPoints && _glyphZone.CurX[a] == 0)
                    ? (int) MathF.Round(_glyphZone.CurX[p] * _ctFrac)
                    : PhaseOf(a);
            }
            else                                   // between two references: interpolate (CalcAvgXPhase)
                phase = CalcAvgXPhase(a, p, b, PhaseOf(a), PhaseOf(b));
            _phaseVal[p] = phase;
            return phase;
        }

        // Linear interpolation of the phase at p between references a and b, by p's x position.
        private int CalcAvgXPhase(int a, int p, int b, int phA, int phB)
        {
            int xa = _glyphZone.CurX[a], xb = _glyphZone.CurX[b], xp = _glyphZone.CurX[p];
            int lo = Math.Min(xa, xb), hi = Math.Max(xa, xb);
            if (xa >= xb) (phA, phB) = (phB, phA);   // GDI orders by x, swapping the phases
            if (lo == hi) return (phA + phB) / 2;
            return (int) (((long) (xp - lo) * phB + (long) (hi - xp) * phA) / (hi - lo));
        }

        private static readonly bool s_phasePairAdjacent =
            Environment.GetEnvironmentVariable("WPF_CT_PHASE_ADJ") != "0";

        private static readonly bool s_phasePairs =
            Environment.GetEnvironmentVariable("WPF_CT_PHASE_PAIRS") != "0";

        private static readonly bool s_phaseLsbDirect =
            Environment.GetEnvironmentVariable("WPF_CT_PHASE_LSB") != "0";

        private static readonly bool s_phaseRootDirect =
            Environment.GetEnvironmentVariable("WPF_CT_PHASE_ROOT") != "0";

        private static readonly bool s_ctPhaseRaw =
            Environment.GetEnvironmentVariable("WPF_CT_PHASE_BASE") == "raw";

        /// <summary>GDI's ACTUAL compatible-width mechanism, from fs_NewGlyph: ctFactor =
        /// |FixDiv(deviceAdvance, linearAdvance)| and every point is displaced by
        /// x*(ctFactor-1) propagated through the PLACEMENT TREE -- the phase originates at the
        /// phantoms, a one-parent point inherits it, an interpolated point interpolates it.
        /// This is what CompatibleWidthMode 7 approximates with feature centres.
        /// <para>Returns false when there is no tree to propagate through.</para></summary>
        internal bool ApplyCompatPhase(int[] x, int pointCount, float ctFactor)
        {
            if (_phaseP0.Length < pointCount + 4 || _realPoints != pointCount) return false;
            _ctFrac = ctFactor - 1f;
            if (_ctFrac == 0f) return true;
            // Phase reads the CURRENT x of the phantoms and of the interpolation references, so
            // point it at the caller's array (which is the fitted outline about to be corrected).
            int[] saveX = _glyphZone.CurX;
            var pre = new int[x.Length];
            Array.Copy(x, pre, x.Length);
            var tmp = new int[saveX.Length];
            Array.Copy(saveX, tmp, saveX.Length);
            for (int i = 0; i < x.Length && i < tmp.Length; i++) tmp[i] = x[i];
            _glyphZone.CurX = tmp;
            try
            {
                // 1. Phase the points the program TOUCHED (and the phantoms). GDI runs this from
                //    itrp_IUP, so only touched points move here.
                Array.Clear(_phaseDone, 0, _phaseDone.Length);
                // Build the partner map from the individual links the program made (one link =
                // one stem: its reference and the point placed from it).
                if (_phasePartner.Length < pointCount + 4) _phasePartner = new int[pointCount + 4];
                for (int i = 0; i < _phasePartner.Length; i++) _phasePartner[i] = -1;
                if (s_phasePairs)
                    for (int k = 0; k < _linkCount; k++)
                    {
                        int r = _linkA[k], q = _linkB[k];
                        if ((uint) r >= (uint) pointCount || (uint) q >= (uint) pointCount) continue;
                        // GDI only records a PARTNER when DoubleCheckLinkColor gives it one, and
                        // that happens only for points ADJACENT ON THE SAME CONTOUR whose segment
                        // is no steeper than 2:1 -- i.e. the two sides of one stroke. Verdana 'w'
                        // is the counter-example that matters: its three links are WIDE SPACING
                        // links between strokes, and pairing them hands the whole letter a
                        // centre_of_pair displacement (edge oracle 18,582).
                        if (s_phasePairAdjacent && !PhaseAdjacent(r, q, pointCount)) continue;
                        if (_phasePartner[r] < 0 && _phasePartner[q] < 0)
                        { _phasePartner[r] = q; _phasePartner[q] = r; }
                    }
                for (int i = 0; i < pointCount + 2 && i < x.Length; i++)
                {
                    if (i < pointCount && (_glyphZone.Tags[i] & TagTouchX) == 0) continue;
                    int ph = PhaseOf(i);
                    if (ph != 0) { x[i] += ph; tmp[i] = x[i]; }
                }
                // 2. ...and then IUP carries the untouched ones between them, which is the half
                //    that makes the displacement a glyph rather than a scatter.
                int point = 0;
                for (int contour = 0; contour < _contourCount; contour++)
                {
                    int endPoint = Math.Min(_glyphZone.Contours[contour], pointCount - 1);
                    int firstPoint = point;
                    if (endPoint < firstPoint) { point = endPoint + 1; continue; }
                    while (point <= endPoint && (_glyphZone.Tags[point] & TagTouchX) == 0) point++;
                    if (point > endPoint) { point = endPoint + 1; continue; }
                    int firstTouched = point, lastTouched = point;
                    point++;
                    while (point <= endPoint)
                    {
                        if ((_glyphZone.Tags[point] & TagTouchX) != 0)
                        {
                            CarryPhase(x, pre, lastTouched + 1, point - 1, lastTouched, point);
                            lastTouched = point;
                        }
                        point++;
                    }
                    // wrap: the span after the last touched point runs back to the first
                    if (lastTouched != firstTouched)
                    {
                        CarryPhase(x, pre, lastTouched + 1, endPoint, lastTouched, firstTouched);
                        CarryPhase(x, pre, firstPoint, firstTouched - 1, lastTouched, firstTouched);
                    }
                    else
                    {
                        int d = x[firstTouched] - pre[firstTouched];
                        for (int i = firstPoint; i <= endPoint; i++)
                            if (i != firstTouched) x[i] += d;
                    }
                    point = endPoint + 1;
                }
            }
            finally { _glyphZone.CurX = saveX; }
            if (Environment.GetEnvironmentVariable("WPF_CT_CW_TRACE") == "1")
            {
                var tb = new System.Text.StringBuilder($"CW11 ctFrac={_ctFrac:0.0000} pts={pointCount}\n");
                for (int i = 0; i < pointCount && i < x.Length; i++)
                    tb.Append($"   pt{i,2} pre={pre[i],5} post={x[i],5} d={x[i] - pre[i],4}"
                        + $" {((_glyphZone.Tags[i] & TagTouchX) != 0 ? "T" : ".")}"
                        + $" par={_phaseP0[i],3}\n");
                Console.Error.Write(tb.ToString());
            }
            return true;
        }

        /// <summary>TEST OF THE TRUETYPE MODEL: GDI's ClearType x for a TrueType glyph looks far
        /// closer to the NATURAL (linearly scaled) outline stretched onto the device advance than
        /// to our bytecode-fitted x -- on Verdana 'w'@12 GDI's edges sit within ~0.37px of
        /// natural*deviceAdv/naturalAdv, while our fitted x is a third of a pixel further out at
        /// every edge. Replaces x with that stretch entirely.</summary>
        internal bool ApplyNaturalStretch(int[] x, int pointCount, int targetAdvance)
        {
            if (_realPoints != pointCount || _glyphZone.OrgX.Length <= pointCount + 1) return false;
            int o0 = _glyphZone.OrgX[pointCount], o1 = _glyphZone.OrgX[pointCount + 1];
            int natural = o1 - o0;
            if (natural <= 0 || targetAdvance <= 0) return false;
            float s = targetAdvance / (float) natural;
            for (int i = 0; i < pointCount && i < x.Length; i++)
                x[i] = o0 + (int) MathF.Round((_glyphZone.OrgX[i] - o0) * s);
            return true;
        }

        /// <summary>IUP's carry, but between the PRE-phase and POST-phase positions of the two
        /// reference points: an untouched point keeps its place in the span it sits in.</summary>
        private static void CarryPhase(int[] x, int[] pre, int lo, int hi, int a, int b)
        {
            if (lo > hi) return;
            int oa = pre[a], ob = pre[b], na = x[a], nb = x[b];
            if (oa > ob) { (oa, ob) = (ob, oa); (na, nb) = (nb, na); }
            int span = ob - oa;
            for (int i = lo; i <= hi; i++)
            {
                int o = pre[i];
                if (span == 0) { x[i] += na - oa; continue; }
                if (o <= oa) x[i] += na - oa;
                else if (o >= ob) x[i] += nb - ob;
                else x[i] = na + (int) (((long) (o - oa) * (nb - na)) / span);
            }
        }

        private void ApplyPhaseControl()
        {
            int adv = _realPoints + 1;              // the advance phantom
            switch (s_ctPhase)
            {
                case 2:                              // ctFactor = natural / hinted advance
                    _ctFrac = _glyphZone.CurX[adv] == 0 ? 0f
                        : (float) _glyphZone.OrgX[adv] / _glyphZone.CurX[adv] - 1f;
                    break;
                case 3:                              // ctFactor = hinted / natural advance
                    _ctFrac = _glyphZone.OrgX[adv] == 0 ? 0f
                        : (float) _glyphZone.CurX[adv] / _glyphZone.OrgX[adv] - 1f;
                    break;
                default:
                    _ctFrac = s_ctPhaseFactor / 10000f;
                    break;
            }
            if (Environment.GetEnvironmentVariable("WPF_CT_PHASE_DEBUG") == "1")
                Console.Error.WriteLine($"PHASE adv: org={_glyphZone.OrgX[adv]} cur={_glyphZone.CurX[adv]}"
                    + $" frac={_ctFrac:0.0000} realPts={_realPoints} zoneN={_glyphZone.PointCount}");
            if (_ctFrac == 0f && !s_ctPhaseRaw) return;
            // TEST OF THE OTHER ARCHITECTURE: GDI may SUPPRESS the bytecode's x-fitting in ClearType
            // and build x from the RAW scaled outline + phase. Start from OrgX (scaled, unfitted) so
            // the phase is not added on top of a base that already embodies it.
            if (s_ctPhaseRaw)
                for (int i = 0; i < _realPoints; i++) _glyphZone.CurX[i] = _glyphZone.OrgX[i];
            Array.Clear(_phaseDone, 0, _phaseDone.Length);
            for (int p = 0; p < _realPoints; p++)
            {
                int ph = PhaseOf(p);
                if (ph != 0) _glyphZone.CurX[p] += ph;
            }
        }

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

        /// <summary>Whether distances are currently measured along x rather than y.
        /// <para>"Predominantly x" is one reading of the ClearType direction and "has any x in it at
        /// all" is another, and the difference would be exactly the DIAGONALS -- 'A' 'X' 'M' 'W' 'N'
        /// 'K' 'V' 'Y' 'Z' '4' '7', the same set our fitting disagrees with GetGlyphOutline about,
        /// holding several of the dearest glyphs left. It makes NO DIFFERENCE, and not because the
        /// reading does not matter: sliding the threshold from "any x at all" to "ten times more x
        /// than y" moves the specimen by 41, which is its own noise. Segoe UI hints along x and y
        /// and nothing else at these sizes; its diagonals are carried by IUP. So the question is
        /// moot for this face, and whatever those glyphs disagree about, it is not this.</para>
        /// </summary>
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
        private int MeasureOriginal(int zoneA, int a, int zoneB, int b, bool black = false)
        {
            Zone za = ZoneOf(zoneA), zb = ZoneOf(zoneB);
            if (a >= za.PointCount || b >= zb.PointCount) return 0;
            // A stroke's weight is what it was before the compatible-width pre-scale (mode 9).
            if (black && s_blackOnInk && _preScaled && zoneA == 1 && zoneB == 1)
                return DualProject(za.InkX[a] - zb.InkX[b], za.OrgY[a] - zb.OrgY[b]);
            return DualProject(za.OrgX[a] - zb.OrgX[b], za.OrgY[a] - zb.OrgY[b]);
        }

        /// <summary>The same distance taken from the outline in FONT UNITS and scaled afterwards,
        /// which is a hair more exact -- the points were rounded to the grid on the way in, and this
        /// has not been. MD and MDRP ask for it, and only outside the twilight zone, whose points
        /// were never in the outline to begin with.</summary>
        private int MeasureOriginalExact(int zoneA, int a, int zoneB, int b)
        {
            Zone za = ZoneOf(zoneA), zb = ZoneOf(zoneB);
            if (a >= za.PointCount || b >= zb.PointCount) return 0;
            if (zoneA == 0 || zoneB == 0)
                return DualProject(za.OrgX[a] - zb.OrgX[b], za.OrgY[a] - zb.OrgY[b]);

            return ScaleUnits(DualProject(za.OrusX[a] - zb.OrusX[b], za.OrusY[a] - zb.OrusY[b]), _measureScale);
        }

        /// <summary>Move a point by <paramref name="distance"/> ALONG THE PROJECTION vector, which
        /// means shifting it along the freedom vector by however much that takes.</summary>
        private void MovePoint(Zone zone, int point, int distance, bool touch = true)
        {
            if (point < 0 || point >= zone.PointCount) return;

            // FreeType's post-IUP curfew was tried here and is NOT what GDI does -- see the note on
            // backward-compatibility mode above XSuppress. Measured alone it costs 681,433 ->
            // 684,721, and the mode it belongs to costs 1,550,791. No branch is kept for it: this is
            // the hottest function in the hinter.

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
        private int RoundDistance(int distance, bool position = false, bool mdap = false)
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
            // WPF_CT_POSGRID=physical put EVERY position on the whole-pixel grid, MDAP and
            // MIAP alike, and cost 5,834,105 against 2,180,771. MIAP places a point at a
            // control value outright, which is a different job from MDAP rounding a point
            // where it already is, and there is no reason the two want the same grid.
            // WPF_CT_POSGRID=mdap asks only the second of them.
            //
            // MEASURED, AND IT SETTLES THE QUESTION THE OTHER WAY. On 'H' at 12ppem it is
            // exactly right: the four coordinates go 1.000 2.094 7.000 8.094 and every one lands
            // inside the range GDI's pixels allow, where mode 6 alone misses the last two by a
            // third of a pixel. And it costs 5,979,285 against 3,271,235 across the repertoire.
            // Fixing 'H' breaks everything else, so GDI does not round MDAP positions to the
            // whole pixel.
            //
            // The inference that it did came from reading a RANGE as a value: H's third
            // coordinate must lie in [6.921, 7.078], which contains 7.0 but does not require it.
            // Anything in that interval will do, and 'the interval contains a whole pixel' is a
            // much weaker fact than it looks.
            //
            // Note also that mdap and physical measure IDENTICALLY under mode 5 (5,834,105 both),
            // so MIAP's position rounding never differs in practice -- the whole-pixel grid only
            // ever reached MDAP anyway.
            bool physicalPosition = position && (s_positionsOnPhysicalGrid
                                                 || (s_posGridMdapOnly && mdap));
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
            int thirds = XWholePixelGrid ? 1
                       : distanceGrid > 0 ? distanceGrid
                       : physicalPosition ? 1
                       : finer && TrueTypeFont.SubpixelFitting && IsHorizontalProjection ? 3
                       : position && s_positionGrid > 0 && InClearTypeDirection ? s_positionGrid
                       : InClearTypeDirection ? ClearTypeGrid
                       : 1;
            // A SNAP ZONE was tried here -- pull a value onto a whole pixel when it lands within a
            // few 64ths of one, leave it alone otherwise. It is the one mechanism that would explain
            // the shape of the per-glyph measurement, where GDI's lamps prefer the bi-level fit for
            // some glyphs and the ClearType fit for others at the SAME size with nothing geometric
            // separating the two lists: whichever a glyph resembled would be an accident of where
            // its stems happened to fall. Measured at 4, 6, 8, 12 and 16 sixty-fourths it is
            // monotonically worse -- 766,634 / 780,604 / 805,071 / 840,918 / 954,917 against
            // 759,520 -- so that is not the mechanism either.
            if (s_traceGrid)
            {
                s_gridHist[thirds >= 0 && thirds < 20 ? thirds : 19]++;
                if (++s_gridTotal == 300)
                {
                    var sb = new System.Text.StringBuilder($"      [grid] mode={TrueTypeFont.XHintMode}"
                        + $" subpixelFitting={TrueTypeFont.SubpixelFitting} ctInfo={ClearTypeInfo}"
                        + $" implausibleFitsDiscarded={TrueTypeFont.ImplausibleFits}"
                        + $" outlineSource={GlyphRunPainter.OutlineSourceCounts} :");
                    for (int k = 0; k < 20; k++)
                        if (s_gridHist[k] > 0) sb.Append($"  thirds={k} x{s_gridHist[k]}");
                    Console.Error.WriteLine(sb.ToString());
                }
            }
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
