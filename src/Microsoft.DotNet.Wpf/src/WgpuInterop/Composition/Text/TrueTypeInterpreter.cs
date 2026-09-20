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
            // itrp_GetCVTEntrySlow forms it as (v + (v >> 63) + 0x8000) >> 16. The middle term
            // is -1 for a NEGATIVE product and 0 otherwise, so a value whose fraction is exactly
            // a half rounds AWAY from zero on both sides; without it a negative half rounds up
            // and the two sides of the origin are not treated alike. Scale runs on every control
            // value and every point coordinate, so the difference is one 64th almost anywhere.
            return (int)((v + 0x8000) >> 16);
        }

        /// <summary>A CONTROL VALUE is not scaled the way a point is. itrp_GetCVTEntrySlow
        /// forms it as (v + (v >> 63) + 0x8000) >> 16 -- the middle term is -1 for a negative
        /// product, so an exact half rounds AWAY from zero on both sides. The point scalers
        /// (scl_ScaleOldCharPoints and its phantom twin) use a different multiply-and-shift
        /// with no such term, so this belongs to the control values alone -- applying it to
        /// both measures 3,634,447 against 3,598,948.
        /// <para>AND IT MEASURES WORSE HERE, so it is OFF: 3,614,199 against 3,598,948 when it
        /// was written, and 1,569,637 against 139,753 re-measured on 2026-09-19 -- eleven times
        /// the residual.</para>
        /// <para>SETTLED 2026-09-19, and the whole chain is read now rather than guessed. The old
        /// note said "GDI does not take this path at all -- GetCVTEntryFast returns the stored
        /// value with no scaling, so the array is already scaled and the rounding that produced it
        /// happened somewhere we have not found". The conclusion was right and every step of the
        /// reasoning was wrong. In order:</para>
        /// <para>itrp_Execute@140037030 installs the FAST accessors only when gs[0x16b] == 1, the
        /// font program, or when gs[0x2e] is non-zero; prep and every glyph program otherwise get
        /// itrp_GetCVTEntrySlow. So GDI does take the slow path.</para>
        /// <para>But itrp_GetCVTScale@140037cd0 does not return a scale. It returns gs[0x160] for
        /// a pure x projection, gs[0x164] for a pure y one, and a cached sqrt of the two for a
        /// diagonal -- and scl_InitializeScaling sets those two to the ASPECT RATIO, not to a
        /// size: whichever of the x scale gs[0x184] and the y scale gs[0x188] is larger gets
        /// 0x10000 and the other gets their quotient. For an unrotated, unstretched glyph the two
        /// are equal, both come out 1.0, and GetCVTEntrySlow's
        /// (v * 0x10000 + sign + 0x8000) >> 16 hands back v unchanged. The slow path is an
        /// ASPECT CORRECTION, not the scaling. The array really is pre-scaled.</para>
        /// <para>And the pre-scaling is scl_Scale@140040f50, which switches on the rounder
        /// installed by scl_InitializeScaling: scl_FRound when the em is a POWER OF TWO (all six
        /// faces are 2048), scl_SRound when it is not, scl_FixRound when the numerator reaches
        /// 0x8000. The FRound arm is `(v * num + (den >> 1)) >> log2(den)` -- an arithmetic shift,
        /// so an exact half goes UP, which is what ScaleUnits does. Half AWAY from zero belongs to
        /// the SRound and FixRound arms, and our faces never reach them.</para>
        /// <para>So the control values are exact, and WPF_CT_CVTROUND=away is off for the right
        /// reason at last: it is the tie rule of a code path these fonts do not take.</para>
        /// </summary>
        internal static int ScaleControlValue(int fontUnits, int scale16)
        {
            long v = (long)fontUnits * scale16;
            if (s_cvtHalfAway) v += v >> 63;
            return (int)((v + 0x8000) >> 16);
        }

        private static readonly bool s_cvtHalfAway =
            Environment.GetEnvironmentVariable("WPF_CT_CVTROUND") == "away";

        /// <summary>Font units to 26.6 pixels at the size last prepared, rounded the way Windows'
        /// rasterizer rounds them (<see cref="ScaleUnits"/>).</summary>
        private int Scale(int fontUnits) => ScaleUnits(fontUnits, _scale);

        /// <summary>The y coordinate's scaling, which scl_ScaleOldCharPoints takes through a
        /// SEPARATE function pointer from x's -- globals+0xd0 against globals+0xc8 -- so the two
        /// need not round the same way. WPF_CT_YSCALE=away rounds an exact half AWAY FROM ZERO
        /// (scl_SRound's shape) instead of UP (scl_FRound's), which differs only below the
        /// baseline: a descender's -0.5 becomes -1 rather than 0.</summary>
        private int ScaleY(int fontUnits)
        {
            if (!s_yScaleAway) return ScaleUnits(fontUnits, _scale);
            long v = (long) fontUnits * _scale;
            return (int) ((v + (v >> 63) + 0x8000) >> 16);
        }

        private static readonly bool s_yScaleAway =
            Environment.GetEnvironmentVariable("WPF_CT_YSCALE") == "away";

        /// <summary>a*b/c, rounded to nearest, and sign-symmetric: the magnitudes are what is
        /// divided, so a negative c cannot turn the rounding round the other way.</summary>
        private static readonly int s_engineComp =
            int.TryParse(Environment.GetEnvironmentVariable("WPF_CT_ENGINE"), out int ec) ? ec : 0;

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

        /// <summary>WPF_CT_ROUND_PHASE=quarter: see RoundMode.ToGrid.</summary>
        private static readonly bool s_roundPhaseQuarter =
            Environment.GetEnvironmentVariable("WPF_CT_ROUND_PHASE") == "quarter";

        private static int Pix(int value) => (value + 32) & ~63;          // to the nearest whole pixel
        private static int Floor(int value) => value & ~63;
        private static int Ceil(int value) => (value + 63) & ~63;

        /// <summary>A 2.14 unit vector from an arbitrary one, which is what the vector-setting
        /// instructions need before they can store it.
        /// <para>By way of a 16.16 unit vector, then divided by four, because that is how the
        /// reference implementation does it and the last two bits of the answer decide which side of
        /// a pixel boundary a point on a long diagonal lands.</para></summary>
        /// <summary>WPF_CT_NORM=twostep rounds the unit vector at 2.16 and divides by four, as
        /// we used to. itrp_Normalize forms it in ONE rounded division and the second rounding
        /// could land an LSB low, tilting every diagonal: 3,605,604 -> 3,602,444 on the
        /// specimen, 12,621,181 -> 12,606,533 on the holdout, no ratchet moves.</summary>
        /// <summary>WPF_CT_GRID_AXIS=exact rounds on the lamp grid only where localGS+0xcc is
        /// set, i.e. never for a diagonal projection -- which is what itrp_MIRP does inline.</summary>
        private static readonly bool s_gridAxisExact =
            Environment.GetEnvironmentVariable("WPF_CT_GRID_AXIS") == "exact";

        private static readonly bool s_normDirect =
            Environment.GetEnvironmentVariable("WPF_CT_NORM") != "twostep";

        private static void Normalize(int x, int y, out int nx, out int ny)
        {
            if (x == 0 && y == 0) { nx = 0x4000; ny = 0; return; }

            double length = Math.Sqrt((double)x * x + (double)y * y);
            if (length <= 0.0) { nx = 0x4000; ny = 0; return; }

            // itrp_Normalize@14003c3a8 READ TO ITS END, which corrects what the note above says
            // about it. It does NOT form the vector in one rounded division. It divides to a 2.30
            // quotient with the halves away from zero,
            //     q = (x << 30 +/- len/2) / len          (sdiv, the sign-matched half added first)
            // and only then makes it 2.14,
            //     nx = (q + 0x8000) >> 16                (ASR, so half UP rather than to even)
            // -- two roundings, just not the pair `twostep` had (16.16 rounded, then truncated by
            // four). So the shape of the argument for `direct` was wrong.
            // <para>The conclusion survives anyway, and provably: over all 13,225 vectors with
            // x and y from -400 to 400 in steps of 7, GDI's two-step form and Math.Round(x *
            // 16384 / len) give the SAME 2.14 pair every time. The tie it would take to separate
            // them needs x * 2^30 / len within half an LSB of a half, which a real vector does not
            // land on. Implemented as WPF_CT_NORM=gdi and measured: Times 'v' and 'W' at 24ppem
            // and Arial Bold 'A' and 'X' at 20 -- four diagonal glyphs chosen because a projection
            // vector's last bit is what would tilt them -- moved by EXACTLY ZERO. Removed as dead
            // weight; the diagonals are not this.</para>
            if (s_normDirect)
            {
                // itrp_Normalize@180086738 forms the component as x * 0x40000000 / len, with
                // half the divisor added first and the sign matched -- one rounded division
                // straight to the unit vector. Rounding at 2.16 and then dividing by four
                // truncates a second time and can land an LSB low, which tilts every diagonal.
                nx = (int)Math.Round(x * 16384.0 / length);
                ny = (int)Math.Round(y * 16384.0 / length);
                return;
            }
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

        /// <summary>SCANCTRL and SCANTYPE as the pre-program left them. Dropout control is a
        /// property of the SCAN CONVERTER, decided by the face's prep, and GDI honours it under
        /// ClearType: measured with the slab probe, a 1/16px-tall feature renders as a FULL ROW
        /// once the probe's prep carries Times' `SCANCTRL 303 / SCANTYPE 1`, and as nothing
        /// without. Times New Roman's mode-6 branch deliberately leaves its serifs to IUP
        /// (0.22px tall at 16ppem) and relies on this to keep them.</summary>
        internal int PrepScanControl => _prepState.ScanControl;
        internal int PrepScanType => _prepState.ScanType;
        private bool _prepRun;
        private bool _inPreProgram;
        private bool _inComposite;
        private bool _iupDone;
        private bool _iupXDone, _iupYDone;
        private bool _prepClearType;
        private bool _prepBiLevel;

        /// <summary>Run this hint with the BI-LEVEL rules -- physical grid, full cut-in, full minimum
        /// distance, every delta applied -- whatever the ClearType defaults say.
        /// <para>Set around a second hinting pass, so a glyph can be fitted both ways and the two
        /// results compared. See TrueTypeFont's left-edge transfer.</para>
        /// <para>WHAT THE BI-LEVEL PASS PROVES ABOUT THE CLEARTYPE ONE (2026-09-20). GDI's own
        /// fitted points, through GetGlyphOutline, pin the bi-level pass exactly -- Tahoma 'q'@15
        /// 33 of 33 points, Tahoma 'p'@17 30 of 30, Verdana 'b'@13 33 of 33, every off-curve
        /// control included, 0 differing in x and 0 in y -- and those are three of the glyphs that
        /// carry the residual. Two things follow, and both were checked rather than assumed:
        /// <list type="bullet">
        /// <item>THE CLEARTYPE PASS'S Y IS EXACT. Dump FINAL in both modes for Tahoma 'p'@17 and
        /// the y arrays are identical, so the CT branch does the same y work the bi-level one
        /// does, and the bi-level one is GDI's. The residual is purely X. (The point solver says
        /// the same from the other side: with both axes free it still moves only x.)</item>
        /// <item>AND THE CONTROL VALUES ARE THE SAME IN BOTH PASSES. WPF_CVT_DUMP for Tahoma at
        /// 17ppem gives 388 entries per prep run and every one is bit-identical between the
        /// bi-level and ClearType passes, which is what s_ctInPrep already says (globals[0x16b] is
        /// zero while the pre-program runs, so prep never reaches the sixteenth grid). So a wrong
        /// CT-pass control value is not the explanation either.</item>
        /// </list>
        /// What is left is the CLEARTYPE BRANCH of the glyph program placing one x a sixty-fourth
        /// or two from where GDI puts it -- a different block of instructions from the one the
        /// bi-level oracle covers, and the only part of the chain with no oracle over it.</para></summary>
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
            _iupXDone = _iupYDone = false;
            // itrp_Execute@140037148 clears bits 0, 1, 3 and 4 of gs+0x1c2 as a glyph starts
            // (`and w8, w8, #0xffe4`) and leaves the FDEF-recognition bits 8..11 alone -- those
            // were decided once, when the font program ran.
            _mdBit3 = false;
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
                    LatchClearTypeAxis();
                    _projFnGeneral = false;

                    // The dump covers the GLYPH's own program only: the font program and prep run
                    // hundreds of instructions that are the same for every glyph and drown it.
                    _postIupSeen = 0;          // see PostIupExempt
                    bool dumping = TrueTypeInterpreter.s_dumpGlyph;
                    _dumpActive = dumping;
                    try { if (!Execute(glyph.Instructions, 0)) return false; }
                    finally { _dumpActive = false; }
                    // The trace prints each instruction's state BEFORE it runs, so the last one --
                    // which is nearly always an IUP, and nearly always the one in question -- never
                    // shows its own result. Print the finished outline.
                    if (dumping) DumpFinal();
                }

                // ExecutePhaseControl is called from itrp_Execute as well as itrp_IUP, so a
                // glyph whose program has no IUP -- or no instructions at all, which is most
                // composites -- is STILL phased. Hooking only IUP left 169 accented glyphs
                // with their advance never realized, and they were 169 of the 215 regressions.
                // WPF_CT_PHASE_TWICE=1: run the phase AGAIN here even though IUP[x] already
                // ran it. ExecutePhaseControl@140035970 is a byte-for-byte copy of itrp_IUP's
                // phase block -- same lastContourEnd+5 scan, same "any node flagged" boolean, same
                // PhaseShift over every point -- and it neither reads nor writes elem[0x60], the
                // latch that stops IUP's own copy running twice. itrp_Execute@140037314 calls it.
                // AND IT DOES NOT RUN TWICE, which the gate says and the measurement confirms.
                // ExecutePhaseControl@140035970 really does not read elem[0x60] -- it only writes
                // it, at 1400359fc -- so the latch that stops itrp_IUP's copy repeating does not
                // stop this one. What stops it is the CALL SITE: itrp_Execute reaches it only
                // through `globals[0x16b] == 2` (a glyph, not prep), `globals[0x1c0]` bits 0 and 1
                // (ClearType), and `elem[0xd0] == 0` -- a SECOND latch, written by
                // fsg_ExecuteGlyph. That one is plainly not zero once the glyph program has run
                // its IUP: clearing our own latch so the phase runs again measures 27,149,676
                // against 31,200. The at-execute call is still needed for the glyphs that never
                // reach an IUP -- turning it off costs 217,420.
                if (s_phaseTwice) _phaseApplied = false;
                if (s_phaseAtExecute) ApplyPhaseAtIup();

                // THE OUTLINE IS RE-ANCHORED ON THE FITTED LEFT PHANTOM. fs__Contour, after each
                // pass's fsg_ExecuteGlyph (140025398.. for pass one, the pfVar50 loop for pass
                // two), translates EVERY point 0..lastPt+8 by
                //     dx = round(origin.x - curX[pp1]),  dy = origin.y - curY[pp1]
                // where origin is the client's requested position (clientRec[0x180]/[0x18c]
                // >> 10, zero for us) and the rounding, applied only when gridfitting and
                // globals[0x1d4] == 0, is to the WHOLE PIXEL in a bi-level pass (+0x20 & ~0x3f)
                // and to the SIXTEENTH under ClearType (+2 & ~3; bVar49 = clientRec[0x41a] bit 0
                // in pass two). dy is never rounded. The program itself rarely moves pp1, but
                // the PHASE does: the pair rule makes pp1 the mate of the first stem edge placed
                // from it (Arial Bold 'A'@20: pp1 and p4, -10/64), so the whole glyph then slides
                // back +12/64. That was the uniform third-of-a-lamp offset every Arial diagonal
                // showed: 'A'@20B 6,767 -> ~1,100, 'A'@16B 2,291 -> 0, holdout 376,284 -> ~330k.
                // WPF_CT_PP1_ORIGIN=0 off; =exact skips the rounding; =x leaves y alone.
                if (s_pp1Origin != 0 && _realPoints + 1 < _glyphZone.CurX.Length)
                {
                    int pp1 = _realPoints;
                    int dx = -_glyphZone.CurX[pp1];
                    int dy = s_pp1Origin == 3 ? 0 : -_glyphZone.CurY[pp1];
                    if (s_pp1Origin != 2)
                        dx = BiLevelPass || !TrueTypeFont.SubpixelFitting ? (dx + 32) & ~63
                           : s_pp1Sample ? RoundToSample(dx)
                           : (dx + 2) & ~3;
                    if (dx != 0 || dy != 0)
                    {
                        int np = Math.Min(_realPoints + 4, _glyphZone.CurX.Length);
                        for (int i = 0; i < np; i++) { _glyphZone.CurX[i] += dx; _glyphZone.CurY[i] += dy; }
                        if (s_phaseDump) Console.Error.WriteLine($"PP1ORIGIN dx={dx} dy={dy} bilevel={BiLevelPass}");
                    }
                }

                // WPF_CT_XSHIFT=<n>: a DIAGNOSTIC, not a rule -- translate the fitted outline by
                // n sixty-fourths in x, to ask whether a glyph's residual is a translation or a
                // shape. Never set in a measurement that is reported as a result.
                if ((s_xShiftProbe != 0 || s_yShiftProbe != 0)
                    && !BiLevelPass && TrueTypeFont.SubpixelFitting)
                {
                    int np2 = Math.Min(_realPoints + 4, _glyphZone.CurX.Length);
                    for (int i = 0; i < np2; i++)
                    { _glyphZone.CurX[i] += s_xShiftProbe; _glyphZone.CurY[i] += s_yShiftProbe; }
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
                if (s_storeProbe && _realPoints == 27 && CompatibleAdvance64 == 576)
                    Console.Error.WriteLine($"STORE pts={_realPoints} compat64={CompatibleAdvance64}"
                        + $" cur0={_glyphZone.CurX[0]} cur1={_glyphZone.CurX[1]}"
                        + $" cur10={_glyphZone.CurX[10]} cur18={_glyphZone.CurX[18]}"
                        + $" pp2={_glyphZone.CurX[_realPoints + 1]}");
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
        /// <summary>Whether GDI is doing SUB-PIXEL x fitting at this size at all.
        /// <para>Measured, not guessed. GDI's own ClearType-fitted x for Verdana 'x' at 12ppem is
        /// IDENTICAL to its bi-level fit -- 32/105/192/222/227/255/342/424, every value -- so at that
        /// size GDI grid-fits x the ordinary way and does no sixteenth-of-a-pixel work. Verdana's gasp
        /// gives 0x05 up to 16ppem and 0x0f above; the sizes where our 'x' is wrong (10, 12, 16) are
        /// exactly the ones WITHOUT the SYMMETRIC_SMOOTHING bit, and the ones where it is right (8 and
        /// 24) are exactly the ones with it. Segoe UI splits the same way at its own thresholds.</para>
        /// <para>WPF_CT_SUBPIX_GASP=1 asks the face before using the sixteenth grid.</para></summary>
        private bool SubpixelGridHere => !s_subpixNeedsGasp || SymmetricRenderingAnswer;
        
        private static readonly bool s_subpixNeedsGasp =
            Environment.GetEnvironmentVariable("WPF_CT_SUBPIX_GASP") == "1";
        
        /// <summary>PREP IS EXCLUDED HERE AND GDI DOES NOT EXCLUDE IT -- a known, measured
        /// divergence, left in deliberately.
        /// <para>`!_inPreProgram` means every control value the pre-program rounds gets the BI-LEVEL
        /// grid. GDI has no such exclusion: `itrp_RoundToGridSP` and the rest of the SP set are
        /// installed on the TRANSFORM, so its prep rounds on the sixteenth grid like everything else.
        /// Verdana shows it plainly -- our cvt[26], the one value that places all four diagonal edges
        /// of 'x', comes out of prep as exactly 1.0000px at 10, 12 AND 16ppem against pure scales of
        /// 0.9180 / 1.1016 / 1.4688 (raw 188 units, upem 2048). Three inputs, one output.</para>
        /// <para>WPF_CT_PREP=1 removes the exclusion and the mechanism is confirmed: cvt[26] at 12ppem
        /// becomes 1.1875px and Verdana 'x'@12 goes 0.679 -> 0.732 of GDI's ink, the case it was
        /// diagnosed from. It is NOT SHIPPED because the corpus gets worse -- 1,628,346 -> 2,650,808,
        /// or 1,901,124 with WPF_CT_PREP=auto (only at sizes whose gasp omits SYMMETRIC_SMOOTHING).
        /// Exactly 18 of Verdana's 334 control values move, every one of them a stem width and every
        /// one by +3/16px, so it widens every control-value stem by 19% at a stroke; WPF_CT_STEMFAT=12
        /// does the same thing by another route and measures 1,827,907. The ink BALANCE improves
        /// (mean ratio 0.9936 -> 0.9956, light rows 35 -> 30) while the PLACEMENT worsens (heavy rows
        /// 4 -> 11). So the rounding is right and something downstream misplaces the ink it buys;
        /// fixing this needs that second half found first.</para></summary>
        /// <summary>The ClearType-axis latch localGS+0xcc as GDI keeps it: set by every
        /// projection-vector instruction from the 0x1c0 bits alone, with NO mode test -- so it is
        /// live in the PRE-PROGRAM too. itrp_MIRP's sixteenth cut-in and the halved minimum
        /// distance read this latch and nothing else; only the ROUNDING functions add the
        /// "mode != 0 or native" condition (itrp_RTG@14003d340 etc.), which is what keeps the
        /// pre-program on whole pixels. InClearTypeDirection below excludes the pre-program for
        /// everything; this is the latch for the two rules that do not. WPF_CT_PREP_LATCH=0
        /// restores the old exclusion for them.</summary>
        internal bool CtLatch =>
            s_prepLatch && _inPreProgram
                ? ClearTypeInfo && (s_ctAxisNotPureY ? NotPureYProjection : IsHorizontalProjection) && SubpixelGridHere
                : InClearTypeDirection;

        private static readonly bool s_prepLatch =
            Environment.GetEnvironmentVariable("WPF_CT_PREP_LATCH") != "0";

        internal bool InClearTypeDirection =>
            ClearTypeInfo && (s_ctAxisNotPureY ? (s_ctDirLatched ? _ctDirFlag : NotPureYProjection) : IsHorizontalProjection) && (CtRoundingInPrep || !_inPreProgram) && SubpixelGridHere;

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
        /// <summary>Whether PREP rounds control values on the ClearType grid. GDI has no
        /// pre-program exclusion at all -- the SP rounding functions are installed on the
        /// TRANSFORM -- but taking that literally costs 1,628,346 -> 2,650,808, and the cost is
        /// concentrated at 24ppem (+98k) while 12ppem is where it helps. 24 is where Verdana's gasp
        /// DOES ask for symmetric smoothing and 12 is where it does not, so WPF_CT_PREP=auto rounds
        /// prep the ClearType way only at the sizes without it.</summary>
        /// <summary>GDI's own test for "the vectors are on the ClearType axis", read out of
        /// itrp_SDPVTL@14003d918 rather than guessed:
        ///     if (!(globals[0x1c0] &amp; 1))            latch = 0        // ClearType off
        ///     else if (bit2 clear)  latch = (pv.y == 0x4000 &amp;&amp; pv.x == 0) ? 0 : 1
        /// i.e. **anything that is not a pure +Y projection counts as ON the axis**, diagonals
        /// included. Our `IsHorizontalProjection` asks |px| &gt; |py| instead, so a STEEP diagonal --
        /// more vertical than horizontal but not vertical -- takes the bi-level rules here and the
        /// ClearType ones in GDI. SVTCA[x] (itrp_SVTCA_1@14003f6f0) sets the same latch as
        /// (bit0 set &amp;&amp; bit2 clear), and SPVTL/SDPVTL RECOMPUTE it, which we never did.
        /// WPF_CT_AXIS_NOTPUREY=1.</summary>
        private bool NotPureYProjection => !(_gs.ProjX == 0 && _gs.ProjY == 0x4000);

        /// <summary>The grid the CURRENT round state was installed under: GDI binds it when the
        /// round-state instruction runs. Null until one has run.</summary>
        private bool? _roundGridSubpixel;
        
        private void LatchRoundGrid()
        {
            _roundGridSubpixel = InClearTypeDirection;
            // THE ROUNDING FUNCTION IS CHOSEN WHEN THE ROUND STATE IS SET, NOT WHEN A DISTANCE
            // IS ROUNDED. itrp_RTG@14003d340, RDTG@14003d040, RTHG@14003d390, RUTG@14003d3e0,
            // ROFF@1400949f0 and RTDG@14003d2f0 each install one of two functions at
            // globals+0x90: the subpixel variant (itrp_RoundToGridSP etc., `(v + comp/2 + 2) &
            // ~3`, a SIXTEENTH) when the ClearType-axis latch localGS+0xcc is set AND (native
            // mode globals[0x88] bit 2 OR the mode byte globals[0x16b] != 0, i.e. not the
            // pre-program); otherwise the plain whole-pixel one. The pointer then serves every
            // rounded MIRP/MDRP/MIAP/MDAP until the next round-state instruction, whatever the
            // projection is by then -- so `SVTCA[y] RTHG ... SVTCA[x] MDRP` rounds x to a WHOLE
            // pixel under ClearType, and a pre-program's RTG always installs the whole-pixel
            // function. Only itrp_MIRP has an inline fast path (localGS+0xa4 != 0: plain RTG with
            // axis vectors, never cleared since) that rounds by the CURRENT latch.
            _roundFnSp = InClearTypeDirection && !BiLevelPass
                         && (s_roundInstallMode == 2 || NativeClearTypeMode || !_inPreProgram);
        }

        /// <summary>Whether the round-state function GDI would have installed is the SIXTEENTH
        /// one. See LatchRoundGrid.</summary>
        private bool _roundFnSp;

        /// <summary>WPF_CT_ROUND_INSTALL=1: GdiRoundsToSixteenth decides the grid. OFF: its one
        /// surviving rule -- RDTG rounding down to the whole pixel under an off-axis projection,
        /// read from itrp_RoundDownToGridSP@140094a40 -- breaks Arial Bold 'A'/'K'/'X'@20 (0 ->
        /// 4,114 / 1,702 / 491) and costs the holdout 281,797 -> 1,299,293, so at the MDRP that
        /// follows `SDPVTL RDTG` GDI's gs+0x78 is evidently not itrp_Project (SDPVTL installs a
        /// dual-projection function) and the fallback never fires there. Kept as the record of
        /// three readings: the install-time model (80.9M), install + RTG inline (5.2M), RDTG
        /// alone (1.3M). The rounding-time model this file already had is the right one.</summary>
        /// <summary>WPF_CT_ROUND_INSTALL: 1 the install model as the binary writes it, 2 the same
        /// without the pre-program clause, so the two can be told apart.</summary>
        private static readonly int s_roundInstallMode =
            int.TryParse(Environment.GetEnvironmentVariable("WPF_CT_ROUND_INSTALL"), out int rim) ? rim : 0;

        private static readonly bool s_roundInstall = s_roundInstallMode != 0;

        /// <summary>WPF_CT_ROUND_INLINE=1: model itrp_MIRP's inline path (localGS+0xa4 != 0) as
        /// "RTG with axis-aligned vectors", rounding by the current latch. Default off: every
        /// vector or round-state instruction but SVTCA and RTG zeroes 0xa4 and nothing but a
        /// nonzero SVTCA restores it, so after any pre-program it stays zero.</summary>
        private static readonly bool s_roundInline =
            Environment.GetEnvironmentVariable("WPF_CT_ROUND_INLINE") == "1";

        private bool ProjectionIsAxis =>
            (_gs.ProjX == 0x4000 && _gs.ProjY == 0) || (_gs.ProjX == 0 && _gs.ProjY == 0x4000);
        private bool FreedomIsAxis =>
            (_gs.FreeX == 0x4000 && _gs.FreeY == 0) || (_gs.FreeX == 0 && _gs.FreeY == 0x4000);

        /// <summary>The grid GDI rounds this distance on: true = sixteenth, false = whole pixel.
        /// itrp_RoundDownToGridSP@140094a40 is the one subpixel function with a check of its own:
        /// off native mode, with the GENERAL projection function installed (an off-axis
        /// projection vector), it falls back to the whole-pixel RoundDownToGrid.</summary>
        private bool GdiRoundsToSixteenth()
        {
            // ...AND EVERY PROJECTION-VECTOR INSTRUCTION RE-INSTALLS IT: itrp_SVTCA_1@14003f6f0
            // (14003f734-748), SPVTCA, SPVTL, SDPVTL and WPV all reload globals+0x90 from the
            // table at 14009b8c0 indexed by round state + 8 when the NEW latch is set, so the
            // choice follows the projection wherever a program sets one before rounding, which
            // is everywhere -- the install-time reading measured 80.9M. What survives of it is
            // itrp_RoundDownToGridSP's own check: off native mode, with the general projection
            // function installed (an off-axis projection vector -- SDPVTL/SPVTL), RDTG rounds
            // DOWN TO THE WHOLE PIXEL; the other subpixel functions never look.
            // 2026-09-19: THE INSTALL MODEL AND THE PER-CALL MODEL ARE THE SAME MODEL, so this
            // is now the fallback itself and the knob is a no-op (97,806 either way).
            // <para>globals+0x90 is written in thirteen places -- the six round-state opcodes and
            // SVTCA, SPVTCA, SPVTL, SPVFS/WPV and SDPVTL, all of which now call LatchRoundGrid --
            // and EVERY projection change goes through one of them. So "which function is
            // installed" cannot disagree with "what the projection is now", and choosing the grid
            // per call is choosing the installed one. itrp_MDRP does load it from globals+0x90
            // (14003a694: `ldr x8,[x23, #0x90]`, x23 = gs), and the table at 14009b8c0 is
            // confirmed 0..7 plain / 8..15 subpixel with Super and Super45 sharing entries.</para>
            // <para>What this used to return was `!ProjectionIsAxis` in place of the RDTG rule --
            // the SUPERSEDED reading, keyed on the projection VECTOR where the binary keys on the
            // installed projection FUNCTION at gs+0x78. SDPVTL is where they part: it sets an
            // off-axis vector but installs itrp_OldProject, not itrp_Project, so RDTG must NOT
            // fall back there, and the old predicate made it. The 80.9M and the 1,125,947 this
            // knob is recorded as measuring are that old mistake re-measured, not evidence about
            // installing. The correct rule lives in rdtgWhole, keyed on _projFnGeneral, and is
            // applied earlier in the chain than this.</para>
            return !BiLevelPass && InClearTypeDirection;
        }
        
        private static readonly bool s_roundLatch =
            Environment.GetEnvironmentVariable("WPF_CT_ROUNDLATCH") == "1";
        
        private static readonly bool s_ctAxisNotPureY =
            Environment.GetEnvironmentVariable("WPF_CT_AXIS_NOTPUREY") != "0";
        
        private bool CtRoundingInPrep =>
            s_ctInPrepAuto ? !SymmetricRenderingAnswer : s_ctInPrep;
        
        private static readonly bool s_ctInPrepAuto =
            Environment.GetEnvironmentVariable("WPF_CT_PREP") == "auto";
        
        /// <summary>WPF_CT_PREP=1 puts the pre-program on the ClearType grid too; =auto ties it to
        /// the symmetric-rendering answer.
        /// <para>THE BINARY'S GATE SAYS WHICH, AND THE MEASUREMENT SAYS WHAT 0x16b HOLDS. Every
        /// round-state setter -- itrp_RTG@14003d340 and its five siblings -- picks the SUBPIXEL
        /// rounding function over the whole-pixel one when
        /// `localGS[0xcc] != 0 &amp;&amp; (globals[0x88] bit 2 || globals[0x16b] != 0)`. Read alone that
        /// allows the pre-program, since 0x16b is plainly not the projection. Measured on the
        /// holdout at 31,200: putting prep on the ClearType grid costs 5,815,200 and tying it to
        /// the symmetric answer 1,240,559. So `globals[0x16b]` is ZERO while the pre-program runs
        /// and 2 once a glyph is loaded -- which is also what itrp_IUP's `if (globals[0x16b] != 2)
        /// bail` says -- and excluding prep, which this does, IS the gate.</para>
        /// <para>The engine compensation went the same way: WPF_CT_ENGINE=64 measures
        /// 163,269,513, so GDI's is zero and the `comp / 2` the SP family applies to it is
        /// invisible here.</para></summary>
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

        /// <summary>The ClearType rounding grid, in parts of a pixel. 16 is the sixteenth.
        /// <para>GDI SELECTS IT BY SWAPPING THE ROUNDING FUNCTION, and under a condition we do not
        /// reproduce. `itrp_RTG` reads
        /// <code>
        ///     f = itrp_RoundToGridSP;                       // SP = the SUB-PIXEL variant
        ///     if (localGS+0xcc == 0 || (!(globals[0x88] &amp; 4) &amp;&amp; globals[0x16b] == 0))
        ///         f = itrp_RoundToGrid;                     // the ordinary WHOLE-PIXEL one
        ///     globals[0x90] = f;
        /// </code>
        /// and SVTCA_0/_1 reinstall from the same 16-entry table at 0x14009b8c0 -- eight ordinary
        /// functions followed by eight SP ones -- with `+8` under the identical condition. So the
        /// sixteenth needs the ClearType axis AND EITHER native ClearType mode (INSTCTRL selector 3,
        /// globals[0x88] bit 2) OR a nonzero mode byte globals[0x16b]. We apply it whenever
        /// InClearTypeDirection, with no mode-byte term.</para>
        /// <para>For the specimen this is a no-op -- 0x16b is 2, compatible widths, so the gate
        /// passes -- and the prediction that follows from it is REFUTED. Arial Italic at 8/9/10/11,
        /// baseline 43,752 for the four rows: grid 8 gives 186,564, grid 4 298,984, grid 2 468,106
        /// and grid 1 (the whole pixel) 729,694, monotonic in the wrong direction; with
        /// WPF_CT_PHASE=0 as well, which is the pairing the binary suggests since the phase is
        /// gated on the same mode byte, 161,157 / 368,877 / 495,197 / 711,054. Recorded because a
        /// face or size where 0x16b is 0 would get whole-pixel x, and that is the shape of every
        /// "GDI's ClearType x equals its bi-level x" observation on record.</para>
        /// <para>THE WRITER HAS NOW BEEN FOUND, AND THE BYTE IS NOT PER-FACE OR PER-SIZE. No
        /// instruction stores a byte at 0x16b; it is the HIGH byte of the little-endian halfword
        /// at globals+0x16a, and that halfword has five writers:
        /// <code>
        ///   fs__NewTransformation@140026064   mov w8,#0x101   -> mode 1   (per-size default)
        ///   fsg_RunPreProgram@1400309b4       strh w21        -> mode 0   (w21 also goes to elem+0x50, the
        ///                                                                  contour count; high byte 0)
        ///   fs__Contour@140024838             strh w26        -> mode 0   (same pattern)
        ///   itrp_ExecuteGlyphPgm@14003733c    mov w9,#0x200   -> mode 2   UNCONDITIONALLY, first thing
        ///   fsg_SimpleInnerGridFit@140031488  mov w8,#0x200   -> mode 2   (behind `cbz w21`)
        /// </code>
        /// So the byte is 0 while 'prep' runs and 2 while every glyph program runs. That is WHY
        /// prep rounds on the whole pixel and the glyph program on the sixteenth -- the model we
        /// already ship -- and it means no face and no size ever runs its glyph program with
        /// whole-pixel x. The Arial 'z'@20 reading above must therefore be a ROUND executed in
        /// prep or an fpgm function called from it, not a mode-0 glyph program.</para>
        /// <para>The measurement agrees. Forcing whole-pixel x on the Times Bold bowls, where GDI's
        /// ClearType outline sits nearer the bi-level one and this was the obvious suspect, is far
        /// worse -- o@16B 1,574 -> 4,270, 0@16B 2,034 -> 15,182, o@18B 0 -> 11,156 -- with or
        /// without the phase. The sixteenth is right; whatever narrows GDI's bowls, it is not the
        /// grid and not this byte.</para></summary>
        internal static readonly int ClearTypeGrid =
            int.TryParse(Environment.GetEnvironmentVariable("WPF_CT_GRID"), out int ctg) && ctg > 0 ? ctg : 16;

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

        /// <summary>The bi-level pass's phantom advance span in sixty-fourths, UNROUNDED -- the
        /// numerator fs__Contour actually uses for the phase scale. See the note at the scale.
        /// </summary>
        internal static int BiLevelSpan64;

        /// <summary>Composite recursion depth of the glyph being hinted: 0 for a glyph asked for
        /// directly, 1+ for a component of a composite. WPF_CT_PHASE_DEPTH selects which of them
        /// the phase runs for.</summary>
        internal static int HintDepth;

        /// <summary>Which grid the LEFT PHANTOM is put on before the program runs.
        /// <para>scl_AdjustOldCharSideBearing@1801da5f0 rounds it to a SIXTEENTH under ClearType and
        /// to a whole pixel otherwise -- `(v + 2) &amp; ~3` against `(v + 0x20) &amp; ~0x3f`, chosen by
        /// `globals[0x1c0]` bit 0 set and bit 2 clear -- and scl_RoundCurrentSideBearingPnt@1801da780
        /// then sets the advance phantom to `pp1 + rounded advance` on the same two grids. GDI
        /// rounds pp1 NOWHERE ELSE, so a ClearType pass never sees it on a whole pixel. We rounded
        /// it to a whole pixel in both passes and let the side-bearing shift move it again, which
        /// left pp1 off both grids -- Times New Roman Italic 'j' has org 3/64, and that came out
        /// at 1/64 where GDI has 4/64. Holdout 172,615 -&gt; 165,670.</para>
        /// <para>2 (the default) is GDI's: the sixteenth under ClearType, the whole pixel in the
        /// bi-level pass. 1 rounds to a whole pixel in both, as before. 0 does not round at all,
        /// which is worse still (194,744) because the advance phantom is then derived from an
        /// unrounded pp1.</para></summary>
        private static readonly int s_pp1Round =
            int.TryParse(Environment.GetEnvironmentVariable("WPF_CT_PP1_SIXTEENTH"), out int p1) ? p1
            // NOT `s_lsbRound ? ...`: that field is declared later in the file and static
            // initialisers run in declaration order, so it would still be false here.
            : Environment.GetEnvironmentVariable("WPF_CT_LSBROUND") != "0" ? 0 : 2;

        private static readonly int s_advancePhantom =
            int.TryParse(Environment.GetEnvironmentVariable("WPF_PP2_ROUND"), out int pp) ? pp
            // Mode 7 of the compatible-width correction moves features by how far the LINEAR
            // advance is from the one the glyph is laid out at, so its run starts with the
            // advance phantom on the linear advance, unrounded. The phase pass measures the
            // same distance the same way and wants the same start.
            : Environment.GetEnvironmentVariable("WPF_CT_PHASE") != "0" ? 6
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
            _roundFnSp = false;          // the pre-program starts on the whole-pixel functions
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
                    _suppressedFdefCount = 0;
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

        /// <summary>The control values as SCALED AND NOTHING ELSE -- a snapshot taken before the
        /// pre-program runs, so no WCVTP or DELTAC has touched them.
        /// <para>GDI KEEPS BOTH. Read out of its glyph space (two arrays, at +0x004ec and
        /// +0x02004) the prep-processed one matches ours entry for entry, and the second one is
        /// exactly this: cvt[125] measures 41/51/62/82/103/123 at ppem 8/10/12/16/20/24, which is
        /// round(164 * ppem / 2048 * 64) at every size, while the processed array holds 64 or 128
        /// -- whole pixels. GDI uses the processed values on y and these on the ClearType x axis,
        /// which is what "ClearType does not round stem widths in x" actually means.</para>
        /// <para>CONFIRMED at four sizes: GDI's fitted stem IS this value. ppem 12/16/20/24
        /// gives B[125] 62/82/103/123 and a fitted stem of 62/82/103/123, while the processed
        /// array holds 64/64/128/128 -- at 20ppem it says two pixels and the stem is 1.61, so it
        /// is unambiguously this array and not that one.</para>
        /// <para>AND YET IT MEASURES WORSE, so it is OFF: 5,131,048 against 3,598,948 on
        /// HowOurWeightTracksGdis, and 5,534,687 / 5,162,771 / 5,168,373 when combined with
        /// WPF_CT_MINDIST_DIV 1 / 4 / 6 (the minimum distance becomes binding once the control
        /// value drops below a pixel, which is why MINDIST_DIV alone had never done anything).
        /// The mechanism is not in doubt; what it means is that something downstream was tuned
        /// around its absence -- most likely the filter and contrast curve, which were fitted
        /// against stems this rule makes narrower. Do not re-enable this on its own.</para>
        /// <para>2026-09-13, AND THE SHAPE OF IT IS NOT TWO ARRAYS BUT TWO SCALES.
        /// `itrp_GetCVTScale@140037cd0` is four lines and it chooses by the PROJECTION VECTOR:
        /// <code>
        ///     if (localGS[0x1a] == 0) return globals[0x160];   // projection pure x
        ///     if (localGS[0x18] == 0) return globals[0x164];   // projection pure y
        ///     return cached ??= sqrt(x-scale^2 + y-scale^2);   // diagonal, kept at localGS+0xa0
        /// </code>
        /// so a control value is scaled AT READ TIME by whichever axis the program is working on,
        /// and a diagonal link gets a third scale again, combined through DWRITE_FracSqrt. Our
        /// RCVT returns `_scaledCvt[i]` -- one array, one scale, no axis -- so every control value
        /// read on one of the two axes is scaled by the other one's factor whenever the two
        /// differ, which in compatible-width mode is exactly when x is compressed.</para>
        /// <para>The writer is `GetInstRecord`, and it settles what the two fields are:
        /// `ldr q16,[x23,#0x20]; str q16,[x26,#0x160]` copies a sixteen-byte block out of the
        /// transform record, and the code straight after reciprocates each into instance+0x60 and
        /// +0x64 and multiplies each by the head table's unitsPerEm into globals+0x10 and +0x14.
        /// They are the X SCALE and the Y SCALE, 16.16, nothing more exotic.</para>
        /// <para>AND THAT MAKES THIS ARCHITECTURE RATHER THAN A BUG WE HAVE. For an unrotated,
        /// unstretched text transform at an integer ppem the two scales are EQUAL, and with
        /// px^2 + py^2 == 1 the diagonal branch's sqrt((px*xs)^2 + (py*ys)^2) collapses to the same
        /// number again -- so on this specimen `itrp_GetCVTScale` returns one value on every axis
        /// and our single `_scale` is equivalent to all three branches. It would only diverge under
        /// an ANISOTROPIC transform, which nothing here produces: the italic faces are sheared, and
        /// a shear lives in the off-diagonal terms, not in xs and ys. Do not go looking for Times'
        /// missing pixels down this path; it is recorded so the next reader does not have to
        /// re-derive it, and because it does say the two-array reading above had the wrong SHAPE --
        /// GDI substitutes a factor at read time, not a second array.</para>
        /// <para>WPF_CT_LINEAR_CVT=1.</para></summary>
        private int[] _linearCvt = Array.Empty<int>();

        /// <summary>0 off, 1 = every control-value read on the ClearType axis, 2 = MIRP only
        /// (a measured DISTANCE), leaving MIAP's absolute positioning on the processed array.</summary>
        internal static readonly int s_linearCvtX =
            int.TryParse(Environment.GetEnvironmentVariable("WPF_CT_LINEAR_CVT"), out int lc) ? lc : 0;

        /// <summary>The control value a distance should be measured against: the unprocessed one
        /// in the ClearType direction, the pre-program's one everywhere else.</summary>
        private int CvtFor(int i, bool distance = true)
        {
            if (s_linearCvtX != 0 && (s_linearCvtX == 1 || distance)
                && InClearTypeDirection && !BiLevelPass
                && (uint) i < (uint) _linearCvt.Length)
                return _linearCvt[i];
            return (uint) i < (uint) _scaledCvt.Length ? _scaledCvt[i] : 0;
        }

        private void ScaleControlValues()
        {
            if (_scaledCvt.Length != _controlValues.Length)
                _scaledCvt = new int[_controlValues.Length];
            if (_linearCvt.Length != _controlValues.Length)
                _linearCvt = new int[_controlValues.Length];
            for (int i = 0; i < _controlValues.Length; i++)
                _linearCvt[i] = _scaledCvt[i] = ScaleControlValue(_controlValues[i], _scale);
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
            _projFnGeneral = false;
        }

        /// <summary>Whether GDI's gs+0x78 -- the projection function -- is the GENERAL
        /// itrp_Project. SPVTL and WPV (SPVFS) install it, for an axis vector as much as a
        /// diagonal one; SVTCA and SPVTCA install itrp_XProject/itrp_YProject; SDPVTL installs
        /// itrp_OldProject; itrp_Execute starts a program on the x pair. It matters for exactly
        /// one thing: itrp_RoundDownToGridSP@140094a40 rounds to the WHOLE pixel, not the
        /// sixteenth, when it finds itrp_Project there off native mode. Times New Roman Bold's
        /// 'x' places its top-serif corner with `CALL(GPV .. SPVFS 0x4000,0) RDTG MDAP[r]` and
        /// GDI floors 4.95 to 4.0 where we kept 4.9375 -- a whole-pixel notch shift in the top
        /// row at every size. The earlier reading of this rule keyed on the VECTOR
        /// (ProjectionIsAxis) and broke Arial Bold A/K/X, whose RDTG follows SDPVTL; keyed on
        /// the installed function it is the same rule and does not. WPF_CT_RDTG_PROJFN=0.</summary>
        private bool _projFnGeneral;

        private static readonly bool s_rdtgProjFn =
            Environment.GetEnvironmentVariable("WPF_CT_RDTG_PROJFN") != "0";

        private void ResetProjection()
        {
            if (s_pfProjGdi)
            {
                // itrp_ComputeAndCheck_PF_Proj@1400367f8: each product rounded to 2.14 ON ITS OWN
                // (`(a*b + 0x2000) >> 14`, twice) and summed -- not one floor of the exact sum --
                // and a dot product within 0x3ff of zero (the vectors nearly perpendicular) is
                // replaced by a whole unit of its sign, so a move never divides by a sliver.
                // WPF_CT_PFPROJ=0 keeps the single floor.
                int d = ((_gs.ProjX * _gs.FreeX + 0x2000) >> 14) + ((_gs.ProjY * _gs.FreeY + 0x2000) >> 14);
                if ((uint) ((d + 0x3ff) & 0xffff) < 0x7ff) d = ((d >> 15) & 1) != 0 ? -0x4000 : 0x4000;
                _dotProduct = d;
                return;
            }
            _dotProduct = (int)(((long)_gs.ProjX * _gs.FreeX + (long)_gs.ProjY * _gs.FreeY) >> 14);
            if (_dotProduct == 0) _dotProduct = 0x4000;
        }

        private static readonly bool s_pfProjGdi =
            Environment.GetEnvironmentVariable("WPF_CT_PFPROJ") != "0";

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
                    z.OrgY[i] = z.CurY[i] = ScaleY(glyph.Y[i]);
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
            // ROUNDING THE CURRENT LEFT PHANTOM TO A WHOLE PIXEL IS NOT IN THE BINARY, and it
            // still measures best, so it stays. Recorded so it is not re-derived:
            // fsg_SimpleInnerGridFit@+0x30a40 rounds the phantom TWICE around +0x131220 and
            // +0x131260 -- once for the real points, once for the phantoms -- on the familiar
            // two-way gate ((v+0x20)&~0x3f off the ClearType axis, (v+2)&~3 on it, the second
            // reached via +0x131514). BOTH read the SAME array, `[x20+0x10]`, and a memcpy at
            // +0x131298 copies +0x10 over +0x00 afterwards, so those are the ORIGINAL coordinates
            // -- which is the rounding s_lsbRound below already does -- and the CURRENT left
            // phantom is never rounded on its own account at all.
            // Measured all three ways, ratchets green in each: whole pixel (this)
            // 975,462 / holdout 3,162,566; a sixteenth 985,522 / 3,206,554; not rounded at all
            // 981,440 / 3,189,494. WPF_CT_PP1_SIXTEENTH=2 or 3 to re-measure.
            if (s_pp1Round == 1 || !SubpixelFittingHere)
                z.CurX[glyph.PointCount] = Pix(z.CurX[glyph.PointCount]);
            else if (s_pp1Round == 2)
                z.CurX[glyph.PointCount] = (z.CurX[glyph.PointCount] + 2) & ~3;
            // s_pp1Round == 3: leave it, which is what the binary's SEQUENCE implies -- it rounds
            // the ORIGINAL phantom (twice, real points then phantoms) and only then memcpys the
            // original array over the current one, so the current left phantom is never rounded
            // on its own account.
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
                // 5: WHAT GDI ACTUALLY DOES. scl_RoundCurrentSideBearingPnt rounds the
                // ADVANCE -- the gap between the two x phantoms, not either phantom on its own
                // -- and rounds it two ways on the same gate the component offset uses:
                //     (v + 0x20) & ~0x3f   to a whole pixel, off the ClearType axis
                //     (v + 2)    & ~3      to a SIXTEENTH, on it
                // (The y phantoms below it round to a whole pixel unconditionally.)
                5 => z.CurX[glyph.PointCount]
                     + (((z.CurX[glyph.PointCount + 1] - z.CurX[glyph.PointCount]) + 2) & ~3),
                // 6: the same, but the ADVANCE IS TAKEN IN FONT UNITS AND SCALED ONCE.
                // scl_RoundCurrentSideBearingPnt forms it as (xs[pp2] - xs[pp1]) * xScale, not
                // as the difference of two already-scaled phantoms, and the two round apart --
                // the same distinction WPF_MDRP_EXACT draws for a black distance.
                6 => z.CurX[glyph.PointCount]
                     + ((Scale(glyph.X[glyph.PointCount + 1] - glyph.X[glyph.PointCount]) + 2) & ~3),
                _ => Pix(z.CurX[glyph.PointCount + 1]),              // round, as a bi-level rasterizer does
            };

            // scl_AdjustOldCharSideBearing@1801d97d0 / scl_AdjustOldPhantomSideBearing: the LSB
            // phantom's ORIGINAL x is rounded on the same two-way gate as everything else --
            // a whole pixel off the ClearType axis, a SIXTEENTH on it -- and the REAL points
            // (0..lastEnd, not the phantoms) are translated by the difference before a single
            // instruction runs. It is how the glyph is placed against its own origin, and it is
            // why our left edge kept measuring a sixty-fourth or three right of GDI's.
            // AND THE VERTICAL PAIR. The same scl_RoundCurrentSideBearingPnt@1801da780 that puts
            // the advance phantom on its grid finishes by putting BOTH y phantoms on whole pixels
            //     curY[pp3] = (curY[pp3] + 0x20) & ~0x3f;
            //     curY[pp4] = (curY[pp3] + advanceHeight + 0x20) & ~0x3f;
            // on either grid -- there is no ClearType branch in the y half, because nothing
            // oversamples y. It writes CUR only, leaving ORG where the scaling put it, exactly as
            // the x half does. WPF_CT_YPHANTOM=0 leaves them unrounded.
            if (s_yPhantomRound && glyph.PointCount + 3 < n)
            {
                int p3 = glyph.PointCount + 2, p4 = glyph.PointCount + 3;
                int advY = z.CurY[p4] - z.CurY[p3];
                z.CurY[p3] = Pix(z.CurY[p3]);
                z.CurY[p4] = Pix(z.CurY[p3] + advY);
            }

            if (LsbRoundHere && !glyph.Composite && glyph.PointCount < n)
            {
                int pp1 = glyph.PointCount;
                int org = z.OrgX[pp1];
                int snapped = SubpixelFittingHere ? (org + 2) & ~3 : (org + 32) & ~63;
                int delta = snapped - org;
                if (delta != 0)
                    for (int i = 0; i < glyph.PointCount; i++)
                    { z.OrgX[i] += delta; z.CurX[i] += delta; z.InkX[i] += delta; }
                // AND THE PHANTOMS. fsg_SimpleInnerGridFit inlines BOTH halves of this: the first loop
                // moves the real points (scl_AdjustOldCharSideBearing) and a second one moves EIGHT more
                // entries starting at pp1 (scl_AdjustOldPhantomSideBearing, scl_ShiftOldPoints(.., 8)),
                // by the same delta re-derived from the same unshifted ox[pp1]. So the whole glyph and
                // its advance box translate together. Moving only the real points slides the outline
                // INSIDE the box, which changes every distance the program later measures from a phantom
                // -- and the phase tree is rooted at exactly those phantoms.
                if (s_lsbPhantoms)
                    for (int i = glyph.PointCount; i < n && i < glyph.PointCount + 4; i++)
                    { z.OrgX[i] += delta; z.CurX[i] += delta; z.InkX[i] += delta; }
            }

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
                _phaseColour = new int[np];
                _phaseVal = new int[np]; _phaseDone = new bool[np];
            }
            if (_phasePartner.Length < np) _phasePartner = new int[np];
            for (int i = 0; i < np; i++) { _phaseP0[i] = -1; _phaseP1[i] = -1; }
            for (int i = 0; i < _phasePartner.Length; i++) _phasePartner[i] = -1;
            _phaseGlyphStamp++;
            _pvPtA = _pvPtB = -1;
            _phaseAnyCycle = false;
            _phaseApplied = false;
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

        /// <summary>localGS+0xce / +0xd0: the two points SPVTL or SDPVTL took the projection
        /// vector from, or -1 when it is on an axis. GDI keeps them beside the ClearType-x flag at
        /// +0xcc and MDRP/ALIGNRP consult them before recording anything.</summary>
        private int _pvPtA = -1, _pvPtB = -1;

        private void SetVectorLine(int a, int b)
        {
            if (s_phaseDump && (a != _pvPtA || b != _pvPtB))
                Console.Error.WriteLine($"  VLINE ({a},{b}) was ({_pvPtA},{_pvPtB}) zp1={_gs.Zp1} zp2={_gs.Zp2} prep={_inPreProgram}");
            _pvPtA = a; _pvPtB = b;
        }

        /// <summary>InterAlign@180294a80: does p sit between a and b in FONT UNITS?</summary>
        private bool InterAlign(int a, int p, int b)
        {
            int[] x = _glyphZone.OrusX;
            if ((uint) a >= (uint) x.Length || (uint) b >= (uint) x.Length) return false;
            if ((uint) p >= (uint) x.Length) return false;
            int lo = Math.Min(x[a], x[b]), hi = Math.Max(x[a], x[b]);
            return x[p] >= lo && x[p] <= hi;
        }

        /// <summary>THE CALL-SITE SET IS PROVEN COMPLETE (2026-09-20). Every inlined
        /// AddDistance and AddProportion body calls IndirectlyDependsOn@140035a10, so its
        /// callers enumerate the sites exactly, and there are seven:
        /// <code>
        ///   AddDistance@1400354c8   (out of line)  &lt;- itrp_ALIGNRP, itrp_MSIRP
        ///   AddProportion@140035630 (out of line)  &lt;- itrp_ALIGNRP, itrp_IP x3, itrp_ISECT,
        ///                                             itrp_MDRP
        ///   inlined bodies                         &lt;- itrp_IP x2, itrp_MDRP, itrp_MIRP,
        ///                                             itrp_SHP_Common
        /// </code>
        /// which is MIRP, MDRP, MSIRP, ALIGNRP, SHP, IP and ISECT -- the set this file already
        /// records, with MDRP and ALIGNRP taking either a proportion or a distance and the other
        /// four only a distance. Nothing records a link that we do not.</summary>
        /// <summary>AND SHPIX RECORDS NOTHING, which is worth stating because SHPIX shares
        /// itrp_SHP_Common with SHP and that body holds a link block. itrp_SHPIX calls it with
        /// `mov w2,#0xffffffff` at 14003e930 -- reference point -1 -- and the link block's first
        /// gate is `-1 &lt; (int) param_3`, so it never runs for a SHPIX. The move-suppression
        /// gate at the end of SHP_Common is likewise SHPIX-only: itrp_SHP passes param_4 = 0
        /// (14003e7bc) and SHPIX passes 1 (14003e92c), and param_4 == 0 applies the shift
        /// unconditionally.</summary>
        private void LinkX(int zoneP, int p, int zoneR, int r, int distanceType = -1,
            bool canProportion = false, bool doubleCheck = false, int phaseType = -1,
            [System.Runtime.CompilerServices.CallerMemberName] string site = "")
        {
            // itrp_MDRP enters its record block on `zp1 elem != twilight` alone (14003a630) and
            // the inlined AddDistance indexes the reference into THAT zone's node array, so a
            // link from a twilight reference is recorded as p -> (rp0 index in the glyph zone).
            // WPF_CT_PHASE_TWILIGHT_REF=1 does the same; the default refuses those links.
            if (_inPreProgram || zoneP != 1 || (zoneR != 1 && !s_phaseTwilightRef)) return;
            // PHASE tree: record p's parent for EVERY link -- any colour, horizontal or diagonal,
            // and even when the reference is a PHANTOM (the advance/lsb). The phase ORIGINATES at
            // the phantoms and flows to whatever was placed from them, so filtering by link colour
            // (as the stem path below does) starves the tree and leaves most of the glyph unmoved.
            // MDRP and ALIGNRP do NOT always record a distance. When the projection vector was
            // taken from a line (SPVTL/SDPVTL) and the point being placed lies BETWEEN that line's
            // two points, GDI records a PROPORTION instead -- two parents, so the point
            // interpolates between them. Every other opcode (MIRP, MSIRP, SHP) only ever records a
            // distance. This is where the density the phase walk needs comes from: a one-parent
            // chain that bottoms out in a root cannot move at all, but a two-parent node can.
            // COMPONENTS ONLY -- WPF_CT_PHASE_PROPCOMP=1, under test. itrp_MDRP's AddProportion
            // call sits behind four gates and the first is `elem != glyphElem`:
            //     if (elem == glyphElem || globals[0x16b] != 2 || localGS[0xcc] == 0
            //         || (globals[0x1c0] >> 1 & 1) == 0) skip;
            //     if (localGS[0xce] != -1 && localGS[0xd0] != -1
            //         && InterAlign(elem, localGS[0xce], p, localGS[0xd0]))
            //         AddProportion(1, elem, localGS[0xce], p, localGS[0xd0]);
            // so GDI records no proportion link at all for a SIMPLE glyph, and every glyph in the
            // weight specimen is simple.
            // <para>REFUTED, AND THE READING WITH IT -- `elem != glyphElem` DOES NOT MEAN "is a
            // component". The gate costs 931,315 against 611,135 on the holdout and 12,411 against
            // 10,327 on Arial Bold at 20ppem. The same condition guards itrp_MIRP's inlined
            // AddDistance, itrp_ALIGNRP's pair of calls, and itrp_IUP's phase block itself -- and
            // forcing THAT one to mean "components only" measures 46,983,682 (WPF_CT_PHASE_DEPTH=3).
            // A condition that would switch the entire phase off for every simple glyph, on a
            // specimen made entirely of simple glyphs, cannot mean what it looks like. It means
            // "not the TWILIGHT element": localGS+0x38 is zone 0, every link is recorded in the
            // glyph zone, so the test is true in the ordinary case and these gates all read "do not
            // build a phase tree out of the twilight zone".</para>
            // <para>So there is no contradiction and nothing upstream to find here: recording a
            // proportion link for a simple glyph is correct, and WPF_CT_PHASE_PROPCOMP only exists
            // to keep the measurement that says so.</para>
            bool tookProportion = false;
            if (canProportion && s_phaseInterAlign
                && (!s_phasePropComponent || _inComposite || HintDepth > 0)
                && _pvPtA >= 0 && _pvPtB >= 0
                && InterAlign(_pvPtA, p, _pvPtB))
            { PhaseProportion(_pvPtA, p, _pvPtB); tookProportion = true; }
            // EITHER A PROPORTION OR A DISTANCE, as itrp_MDRP and itrp_ALIGNRP do: the inlined
            // AddDistance at 14003a7e8 is reached only when the vector line is unset (14003a7dc)
            // or InterAlign fails (14003aa08), and a successful AddProportion at 14003aa1c jumps
            // straight to the move (14003aa20 -> 14003a638 -> 14003a884). RESOLVED 2026-09-17
            // after two failed attempts: recording both had measured 128k better because our
            // SFVTL was also setting the vector line (LineVector was shared with SPVTL), so an
            // ALIGNRP after SFVTL(3,5) asked InterAlign(3, 3, 5), which passes, and the
            // proportion was refused for a == p -- and only the extra distance rescued it. GDI's
            // line there is still the last PROJECTION line, its InterAlign fails, and it records
            // the distance. With SFVTL leaving the line alone, both and either/or measure
            // IDENTICALLY (380,873) and either/or ships. WPF_CT_PHASE_PROP_ONLY=0 records both.
            // WPF_CT_PHASE_PROP_ONLY=2: after a proportion, run only the dependency check the
            // extra AddDistance would have run -- raise the cycle flag, record nothing.
            if (s_propCycleOnly && tookProportion)
            { if (PhaseDependsOn(r, p, 100)) _phaseAnyCycle = true; }
            else if (!(s_propExcludesDistance && tookProportion))
            {
                _phaseSite = site;
                PhaseDistance(r, p, doubleCheck
                    ? PhaseLinkColour(r, p, phaseType >= 0 ? phaseType : distanceType) : 3);
            }
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
        private int[] _phaseColour = new int[128];                       // 1 black, 2 white, 0 neither
        /// <summary>WPF_CT_PHASE_PROP_ONLY=1: an MDRP/ALIGNRP that recorded a proportion records no
        /// distance, which is what itrp_MDRP does (see LinkX).</summary>
        private static readonly bool s_propCycleOnly =
            Environment.GetEnvironmentVariable("WPF_CT_PHASE_PROP_ONLY") == "2";
        private static readonly bool s_phaseTwilightRef =
            Environment.GetEnvironmentVariable("WPF_CT_PHASE_TWILIGHT_REF") == "1";

        private static readonly bool s_propExcludesDistance =
            Environment.GetEnvironmentVariable("WPF_CT_PHASE_PROP_ONLY") is null or "1";

        private string _phaseSite = "";
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
        /// <summary>AddDistance@1801db020, which every MDRP/MIRP/MSIRP/ALIGNRP/SHP calls: the
        /// placed point takes ONE parent -- not the reference itself but the topmost ancestor
        /// reachable from it through links whose ends share the same ORIGINAL x (PhaseAncestor).
        /// Faithful to the original: indices checked and distinct, a cycle marked rather than
        /// recorded, the FIRST parent a point is given wins, and the second slot cleared.</summary>
        /// <summary>WPF_CT_PHASE_ATIUP=1: run the phase where GDI runs it -- ONCE, from inside the
        /// glyph program at the first IUP, rather than as a pass over the finished outline.
        /// itrp_IUP calls ExecutePhaseControl guarded by elem[0x60] (the once-only flag), so the
        /// displacement lands BEFORE that IUP interpolates and the untouched points are carried
        /// along with it. A post-pass cannot reproduce that: it moves points the interpolation has
        /// already placed.</summary>
        private static readonly int s_phaseDepth =
            int.TryParse(Environment.GetEnvironmentVariable("WPF_CT_PHASE_DEPTH"), out int pd) ? pd
            // 1: the COMPONENTS. With mode 7 gone there is nothing else correcting them, and
            // phasing the assembly instead leaves each component where its own program put it
            // (185 accented ratchets fail that way against 145 this way, 159 doing both).
            : Environment.GetEnvironmentVariable("WPF_CT_PHASE") != "0" ? 1 : 0;

        private static readonly bool s_phaseTruncFactor =
            Environment.GetEnvironmentVariable("WPF_CT_PHASE_TRUNC") == "1";

        private static readonly bool s_phaseAtIup =
            Environment.GetEnvironmentVariable("WPF_CT_PHASE_ATIUP") is { } ai ? ai == "1"
            : Environment.GetEnvironmentVariable("WPF_CT_PHASE") != "0";

        private bool _phaseApplied;

        /// <summary>WPF_CT_PHASE_NUM=compat: build the phase scale from the LAID-OUT advance, as
        /// we used to, instead of fs__Contour's unrounded bi-level phantom span.</summary>
        /// <summary>WPF_CT_PHASE_DEN=span: divide by the difference of the two scaled phantom
        /// points, as we used to, instead of scaling the font-unit advance once. See the
        /// comment at the factor.</summary>
        private static readonly bool s_phaseDenRound =
            Environment.GetEnvironmentVariable("WPF_CT_PHASE_DEN") == "round";
        private static readonly bool s_phaseDenFontUnits =
            Environment.GetEnvironmentVariable("WPF_CT_PHASE_DEN") != "span";

        /// <summary>WPF_CT_PHASE_FADJ=&lt;n&gt;: nudge the 16.16 phase factor by n. Probe only.
        /// <para>AND IT CANNOT BE READ ON THE HOLDOUT, because the factor sets the ADVANCE as well
        /// as the shape. A single glyph improves under it -- Arial Bold 'A' at 20ppem goes 6,767
        /// to 3,232 around +1200 -- while the same nudge takes the holdout from 598,774 to
        /// 10,564,419 at +400 and 30,112,636 at +1200, because every glyph after the first in a
        /// 53-character specimen is then laid out in the wrong place (Arial Bold at 20ppem:
        /// centroid dx +0.025 becomes -0.341, sum|d| 10,327 becomes 62,470). Symmetrically at
        /// -400, 10,307,229.</para>
        /// <para>So the factor is NOT a free parameter that happens to be near-optimal; it is
        /// PINNED by the advance, which is already exact against GDI at 6-24ppem. A per-glyph
        /// probe that likes a different factor is telling you about the shape and lying about the
        /// spacing. Arial's diagonal pool is not the factor.</para></summary>
        private static readonly int s_phaseFactorAdj =
            int.TryParse(Environment.GetEnvironmentVariable("WPF_CT_PHASE_FADJ"), out int fa) ? fa : 0;

        private static readonly bool s_phaseNumCt =
            Environment.GetEnvironmentVariable("WPF_CT_PHASE_NUM") == "ct";
        private static readonly bool s_phaseNumSpan =
            Environment.GetEnvironmentVariable("WPF_CT_PHASE_NUM") != "compat";

        private static readonly bool s_phaseRecip =
            Environment.GetEnvironmentVariable("WPF_CT_PHASE_RECIP") == "1";

        /// <summary>Apply the phase to the live glyph zone, once per glyph. ctFactor is the
        /// compatible advance over the linear one, as fs_NewGlyph computes it.</summary>
        /// <remarks>GDI'S OWN PHASE OUTPUT, MEASURED. Arial Bold 'A' at 20ppem is the one glyph
        /// where both ends are pinned: its pre-phase outline is byte-exact against GDI
        /// (WPF_CT_PHASE_ATIUP=0 with GDI at quality 6 scores ZERO), and the anchor search now
        /// reaches GDI exactly once pairwise moves are allowed, so the anchors it returns ARE
        /// GDI's coordinates. Every point of the glyph is touched, so IUP places nothing and the
        /// phase is the only thing between the two columns.
        /// <code>
        ///   pt   pre-phase   ours   GDI     our shift   GDI's   node
        ///    0       904      824    835       -80       -69    p0=1,  col=1
        ///    1       705      625    635       -80       -70    p0=9,  partner=0
        ///    2       640      571    571       -69       -69    p0=9, p1=1
        ///    4       195      185    200       -10        +5    p0=11, col=1
        ///    5         0      -10     -5       -10        -5    p0=11, partner=4
        ///    9       442      398    411       -44       -31    p0=4,  col=2
        /// </code>
        /// Those six have no instruction after the phase. The other five (3, 6, 7, 8, 10) are
        /// followed by SHPIXes of -2, +4, -4, +4, -2, so their finals -- 238, 315, 505, 528, 297
        /// against ours 235, 303, 493, 516, 289 -- are not the phase alone.
        /// <para>Two facts fall straight out. p2 is IDENTICAL, and it is the only two-parent node
        /// in the list: CalcAvgXPhase fed OUR parent shifts returns -69, which is what GDI has,
        /// while fed GDI's own parent shifts (-31 and -70) it returns -58. And p4 and p5 move
        /// APART, +5 against -5, which no pair rule can produce -- a pair moves both ends by one
        /// shift. We pair them (ADDDIST r=5 p=4, colour 1, and colour 1 IS the pairing condition),
        /// and the colour there does not come from the contour flag: it is the input colour passed
        /// through because the two points are not adjacent, and WPF_CT_PHASE_WIND=1 leaves every
        /// colour in the glyph unchanged -- WHICH WAS THE NO-OP KNOB; see PhaseWinding. With the
        /// knob working, the flag DOES decide it: both links are adjacent on contour 0 with
        /// t1=t2=True and dy=0, so the colour is ((~flag &amp; 1) ^ turn) + 1 and our flag is 0.
        /// fs__Contour fills that array with a constant 1 at 140024740 (w26, set once at
        /// 140024464), which would make both colours 2 and pair neither -- exactly what GDI's
        /// numbers want -- but taking it costs 6,767 -> 7,041 here and 922 -> 5,242 on 'K'.</para>
        /// <para>ARIAL BOLD 'K' AT 20ppem IS SHARPER STILL. It also reaches GDI exactly, and
        /// ELEVEN OF ITS TWELVE ANCHORS ARE ALREADY RIGHT -- the whole 922 is point 6, ours at
        /// 555 and GDI's at 547. Our shift there is -6, inherited from its only parent (point 11;
        /// orgs 558 against 281, so the re-derivation does not fire because K raises no cycle
        /// flag) and GDI's is -14. Neither the single-point rule on cur (-17) nor on org (-17),
        /// nor a two-parent mix with point 8 (-18), nor pairing it with 11 (-13) gives -14. And
        /// K's PRE-phase outline scores 58 rather than 0, so part of its 922 may be upstream:
        /// 'A' is still the only glyph where both ends are pinned.</para></remarks>
        /// <remarks>WHAT THE ARIAL DIAGONAL POOL LOOKS LIKE FROM HERE, and the caveat that goes
        /// with it. Arial Bold 'X' at 20ppem has every point touched, so IUP moves nothing and the
        /// phase is the LAST thing that touches the glyph (WPF_HINT_MOVES=1 shows it moving all
        /// twelve points at the IUP[x]). Comparing our final x with GDI's solved outline, which
        /// reaches residual zero and so IS GDI's:
        /// <code>
        ///          pt0  pt1 pt2 pt3 pt4  pt5  pt6  pt7  pt8  pt9 pt10 pt11
        ///   ours    -3  -11  -4  -4 -13  -18  -18  -10  -19  -19   -8   -3   (our phase deltas)
        ///   GDI     +5   -7   0  -4  -9  -18  -16   -2  -19  -11   -4   +1   (GDI final - our pre-phase)
        /// </code>
        /// <para>Three nodes agree exactly and every difference is 0, +2, +4 or +8. THE CAVEAT:
        /// the second row is GDI's final minus OUR pre-phase, so it is only GDI's phase delta if
        /// GDI's pre-phase outline equals ours -- which is not observable, since GGO answers
        /// bi-level and the solver only ever sees the finished thing. The difference could be
        /// split between the fitting and the phase in any proportion. What IS established is that
        /// the compression factor is right (0.9742 = 832/854, and pt5 and pt8 land on it exactly)
        /// and that something after the control-value passes is wrong.</para>
        /// <para>None of the tree knobs moves this row (baseline 10,327): PAIRS and ROOT exactly
        /// neutral, INTERALIGN 12,411, ROOTCYCLE 17,404, PHANTOM 23,365, GDIPAIR 43,935.</para>
        /// <para>And one rule of PhaseShift confirmed while reading it: a node with NO parent
        /// computes its direct scale but does NOT apply it to itself -- the `*psVar17 == -1` test
        /// jumps past the `cur[i] += iVar8` -- it only returns the value for its children. The
        /// advance phantom sitting still with d=0 in WPF_CT_PHASE_DUMP is that rule, not a
        /// bug.</para></remarks>
        private static readonly bool s_phaseAnchorPp1 =
            Environment.GetEnvironmentVariable("WPF_CT_PHASE_ANCHOR") == "1";

        private static readonly bool s_phaseNoMove =
            Environment.GetEnvironmentVariable("WPF_CT_PHASE_NOMOVE") == "1";

        internal void ApplyPhaseAtIup()
        {
            // NOT ON A COMPOSITE. Our composites are assembled from components that were each
            // hinted (and so each already phased) by their own HintedProgram call, so phasing the
            // assembly again applies it twice -- 101 of the 224 EveryAccentedGlyph failures.
            // GDI reaches composite offsets through scl_CalcComponentOffset, a different path.
            // WPF_CT_PHASE_NOMOVE=1: the ONE switch that turns the phase's point movement off
            // and changes nothing else. WPF_CT_PHASE=0 is not that switch -- it is read by three
            // other defaults (the stem fat, the MDRP minimum distance and the SHPIX outline mode)
            // and flips them all at once, which is why "the phase off" has never been measurable
            // on its own. Every call site goes through here, so this is the whole of it.
            if (s_phaseNoMove) return;
            if (!s_phaseAtIup || _phaseApplied || BiLevelPass || !TrueTypeFont.SubpixelFitting) return;
            // s_phaseDepth: 0 = only a top-level glyph (components are phased by their own run
            // and the assembly would apply it a second time), 1 = only components, 2 = both.
            if (s_phaseDepth == 0 && HintDepth > 0) return;
            if (s_phaseDepth == 1 && HintDepth == 0 && _inComposite) return;
            // 3: STRICTLY components -- REFUTED, and the reading behind it with it. itrp_IUP's
            // phase block is guarded by `elem != localGS[0x38]`, which read as "not the GLYPH
            // element", and for a simple glyph those would be the same -- so GDI would never phase
            // one. It measures 46,983,682 against 611,135. localGS[0x38] is therefore the TWILIGHT
            // element, and `elem != twilight` is true for any real glyph, so the guard says only
            // "do not phase the twilight zone". Kept as a knob because the number is the proof.
            if (s_phaseDepth == 3 && HintDepth == 0) return;
            if (!ClearTypeInfo) return;
            _phaseApplied = true;
            int adv = _realPoints + 1;
            if (adv >= _glyphZone.CurX.Length) return;
            // THE DENOMINATOR IS THE ADVANCE IN FONT UNITS, SCALED ONCE -- not the difference
            // of two separately scaled phantom points. fs__Contour@140024964 builds it as
            //     span  = curX[lastEnd + 2] - curX[lastEnd + 1];       // 26.6, unrounded
            //     if (ctx[0x1ac] == 0 || span == 0) factor = 0x10000;  // the nonzero GUARD only
            //     denom = (*(ctx + 0xd8))(&ctx[0x130]);                // one call, one result
            //     factor = (span << 16 +/- denom/2) / denom;           // signs agree -> plus
            // where `denom` has to be the LINEARLY SCALED ADVANCE in 26.6, that being the only
            // thing a span in 26.6 can be divided by to give a ratio near one. `ctx[0x1ac]` is
            // the advance in FONT UNITS -- read as a halfword at 140024998 -- and it is only
            // tested against zero; it is NOT the divisor, whatever an earlier note here said.
            // So one value crosses from design space to 26.6, once. We took the difference of
            // OrgX[pp2] and OrgX[pp1], and those are two independently scaled and rounded
            // coordinates: round(a*s) - round(b*s) is not round((a-b)*s), and the two disagree by
            // a sixty-fourth whenever the two roundings fall opposite ways.
            // <para>AND IT ONLY BITES WHERE THE LEFT PHANTOM IS OFF ZERO, which is why the fix
            // moved exactly six rows and every one of them is Times New Roman ITALIC. pp1.x is
            // xMin - lsb, so it is ZERO for every glyph whose left side bearing matches its
            // bounding box -- all seventeen Times Regular letters sampled -- and there
            // `OrgX[pp2] - OrgX[pp1]` IS `Scale(advance)` and the two forms agree exactly. Times
            // Italic puts three of the same seventeen at pp1.x = 2, and one of those reads
            // orgPP1 = 2, orgPP2 = 342 against a font-unit advance that scales to 341: the old
            // divisor was 340, one sixty-fourth short, on a glyph where the phase then places
            // every anchor.</para>
            // <para>That is exactly the size of the error the anchor search measures. Over 340
            // glyph/size pairs across six faces, sixty of the ninety-four imperfect ones are
            // reproduced EXACTLY by moving anchors alone, fifty-three of those need just ONE
            // anchor moved, and fifty-two of the seventy-two moves are one or two sixty-fourths.
            // The phase is what places those anchors -- turning it off costs ten to thirty times
            // the error on every one of them -- so a last sixty-fourth in the factor is the shape
            // of what is left. WPF_CT_PHASE_DEN=span restores the phantom difference.</para>
            int linear = s_phaseDenFontUnits
                       ? Scale(_glyphZone.OrusX[adv] - _glyphZone.OrusX[_realPoints])
                       : _glyphZone.OrgX[adv] - _glyphZone.OrgX[_realPoints];
            // WPF_CT_PHASE_DEN=round: REFUTED (holdout 376,284 -> 37,012,697, 355 ratchets) though
            // 'A'@20B alone went 6,767 -> 4,095. The denominator is the linear advance ROUNDED to a whole
            // pixel, as the realized advance is. Arial Bold 'A'@20 compresses 832/924 = 0.9004
            // with the unrounded one and its raster shows GDI compressing visibly less; 832/896
            // would be 0.9286.
            if (s_phaseDenRound && linear > 0) linear = (linear + 32) & ~63;
            if (s_phaseEntryProbe)
                Console.Error.WriteLine($"PHASEENTRY pts={_realPoints} orgPP1={_glyphZone.OrgX[_realPoints]}"
                    + $" orgPP2={_glyphZone.OrgX[adv]} linear={linear} compat64={CompatibleAdvance64}"
                    + $" curPP1={_glyphZone.CurX[_realPoints]} curPP2={_glyphZone.CurX[adv]}"
                    + $" span64={BiLevelSpan64}");
            if (linear <= 0 || CompatibleAdvance64 <= 0) return;
            _ctFrac = CompatibleAdvance64 / (float) linear - 1f;
            // GS+0x1d0 is a 16.16 FIXED, not a float, and every phase below is derived from it by
            // integer arithmetic. Carrying it as a float rounded differently from GDI at the last
            // bit, which on a 26.6 coordinate is a third of a pixel.
            // GDI ROUNDS THIS, half away from zero, where we truncated. fs__Contour builds it as
            //     x9 = (int64) span << 16;  w8 = w0/2 with the sign fixup
            //     x9 = (signs agree) ? x9 + w8 : x9 - w8;   factor = x9 / w0
            // which is round-half-away-from-zero on (span << 16) / linear. Worth a fraction of a
            // sixty-fourth on any one point, but it is free and it is what the binary does.
            // WPF_CT_PHASE_TRUNC=1 goes back to truncating.
            // WPF_CT_PHASE_RECIP=1 divides the OTHER way round. fs__Contour@140025010 builds the
            // factor as `(w22 << 16 +/- w0/2) / w0`, where w22 is `phantom2.x - phantom1.x` read
            // out of a coordinate array and w0 comes from a call through elem+0xd8. An earlier
            // reading took w22 for the device advance and w0 for the linear one, which is what
            // ships; read the other way it is linear/compatible, the reciprocal. Arial Italic 'N'
            // at 9ppem is the case that asks: linear is exactly 6.500 and the compatible advance
            // 6, the largest compression of its neighbourhood, and it is the only size where that
            // glyph does not already solve EXACTLY (8 and 10ppem both do). GDI's span there is
            // 445/64 against our 396, the natural 408 and the bi-level 452 -- and 408 * 6.5/6 is
            // 442, which looked like the reciprocal's prediction almost exactly.
            // <para>REFUTED, and the coincidence was just that: Arial Italic goes 3,525/28,914/
            // 6,235 at 8/9/10ppem to 142,090/100,678/195,711. So the earlier reading is the right
            // one -- w22 is the DEVICE advance and w0 the linear -- and compat/linear ships. What
            // is wrong at 9ppem is not the ratio's direction but whether the phase runs at all:
            // fs__Contour@1400249a0 sets the factor to exactly 1.0 whenever `globals[0x1ac]` is
            // zero (or the advance span is), and 0x1ac is copied from `elem[0x46]` as it walks the
            // element list. That gate is the open question, not this.</para>
            // THE NUMERATOR IS THE BI-LEVEL PASS'S PHANTOM SPAN, NOT THE LAID-OUT ADVANCE.
            // fs__Contour runs the glyph TWICE -- once against one globals block (pfVar51) and
            // once against another (pfVar50, which is the one the phase reads) -- and between the
            // two it computes the scale from the FIRST pass's own phantom points:
            //     iVar19  = last contour end;
            //     uVar43  = curX[iVar19 + 2] - curX[iVar19 + 1];      // 26.6, unrounded
            //     denom   = (*globals[0x130])(globals[0x1ac]);        // the advance in font units
            //     globals[0x1d0] = (uVar43 << 16 +/- denom/2) / denom;
            // We used CompatibleAdvance64, which is that same measurement ROUNDED TO A WHOLE
            // PIXEL (or read out of 'hdmx', which caches the rounded numbers). Rounding is right
            // for laying a glyph out -- GDI advances by whole pixels -- and wrong here, because
            // the binary never rounds before dividing. WPF_CT_PHASE_NUM=compat restores it.
            // <para>AND UNCONDITIONALLY, which is worth saying because the obvious hedge measures
            // WORSE. 'hdmx' is a cache of pass one's phantom advance, so where our measurement
            // rounds to a different pixel from the table's it is tempting to distrust our run and
            // fall back -- and that costs 12,778 (869,355 against 856,577). It buys back the one
            // row it was written for and loses about twice as much elsewhere, so the span is the
            // better numerator even where our own bi-level advance is doubtful.</para>
            // <para>A NARROWER GUARD -- fall back only when the span pulls the OPPOSITE way from
            // the advance box, which is the case that can visibly damage a glyph -- is worse
            // still, at 873,726. Neither hedge is worth having.</para>
            // <para>Both were written for one row, Segoe UI at 12ppem, and that row turned out to
            // be a DIFFERENT BUG, now fixed: our bi-level pass claimed greyscale in GETINFO and
            // put Segoe UI and Consolas on a prep branch GDI never takes, which made 'x' measure a
            // 6-pixel advance against its own 'hdmx' entry of 5. See the GETINFO handler. With
            // that right the span needs no guard at all and the holdout is 843,447.</para>
            int numerator = s_phaseNumSpan && BiLevelSpan64 > 0 ? BiLevelSpan64 : CompatibleAdvance64;
            // WPF_CT_PHASE_NUM=ct: REFUTED (holdout 46,789,700; 'A'@20B 6,767 -> 13,653; the ClearType
            // pass leaves 'A's advance phantom at 908/64 against a linear 924, so this span is nearly
            // the linear one). The numerator is THIS pass's phantom span as the program left it
            // -- sixteenth-rounded, since the ClearType pass MIRPs the advance phantom on the
            // sixteenth -- rather than the bi-level pass's whole-pixel one. fs__Contour runs the
            // glyph twice against two globals blocks and takes the span from the first; whether
            // that first run is the bi-level one or the other ClearType one decides this.
            if (s_phaseNumCt)
            {
                int ctSpan = _glyphZone.CurX[adv] - _glyphZone.CurX[_realPoints];
                if (ctSpan > 0) numerator = ctSpan;
            }
            long ctNum = (long) (s_phaseRecip ? linear : numerator) << 16;
            int ctDen = s_phaseRecip ? numerator : linear;
            _ctFactor16 = s_phaseTruncFactor ? (int) (ctNum / ctDen)
                         : (int) ((ctNum + (ctNum < 0 ? -(ctDen / 2) : ctDen / 2)) / ctDen);
            if (s_phaseRecip) _ctFrac = linear / (float) CompatibleAdvance64 - 1f;
            // WPF_CT_PHASE_FADJ=<n>: add n to the 16.16 factor. A probe, not a rule. The anchor
            // search says three quarters of what is left is one anchor out by one sixty-fourth,
            // and one factor is shared by every anchor of a glyph -- so if the factor is the
            // source, ONE value of n should take a whole glyph to zero, and if it is not, no value
            // will. That is a question the oracle can answer in a sweep and the binary cannot,
            // because the factor's numerator is pass one's unrounded phantom span and GDI exposes
            // no fractional advance anywhere.
            // <para>ANSWERED, PARTLY. The factor cannot be the whole story and is not excluded
            // either, and the window has to be wide enough to see it: a node's shift is about
            // `cur * (factor - 1) / 65536`, so moving a shift by one sixty-fourth on a six-pixel
            // glyph needs a nudge around 160, and a first sweep of +/-100 saw three glyphs "not
            // move for any nudge" that simply had not been reached yet. Four anchor-solvable
            // glyphs, swept to +/-600:
            // <code>
            //   Tahoma 'q'@16   292 -> 0   window  -80..-10   span 576 / den 566 -> 66,694
            //   Tahoma 'b'@12   118 -> 0   window  100..220   span 448 / den 425 -> 69,083
            //   Segoe UI 'o'@12    no nudge reaches zero (best 236)
            //   Segoe UI 's'@13    no nudge reaches zero (best 118, the baseline)
            // </code>
            // For 'b' the factor IS a plausible cause: span+1 gives +154 and denominator-1 gives
            // +163, both inside its window. For 'q' it is not: span-1 is -116, denominator+1 is
            // -118, denominator-1 is +118, and its window is -80..-10, so the factor GDI would
            // need lies strictly between anything the formula can produce from integer inputs.
            // And for two of the four NO factor works at all. So a single wrong factor does not
            // explain the pool, though it may explain individual glyphs.</para>
            // <para>One thing the probe did settle: `BiLevelSpan64` equals `CompatibleAdvance64`
            // on all four -- 448, 448, 384, 576, every one a whole number of pixels -- because the
            // bi-level pass rounds its advance. So "the numerator is the UNROUNDED span, not the
            // rounded advance" is a distinction without a difference on these glyphs, and whatever
            // that change was worth came from glyphs where the two disagree.</para>
            _ctFactor16 += s_phaseFactorAdj;
            // THE PHASE ONLY EVER EXPANDS CORRECTLY. Where 'hdmx' forces an advance SMALLER
            // than the natural one the fraction goes negative, and the tree -- 24 of 32 points
            // roots that are never moved, 8 touched points carrying the shift, IUP spreading it
            // between them -- turns the contraction into an EXPANSION: Times New Roman Bold
            // 'I'@12 has ctFrac -0.1438 and comes out 17% WIDER (span 215 -> 252) instead of
            // 15% narrower. GDI's own fitted outline for that glyph is the UNPHASED one.
            // WPF_CT_PHASE_NEG=1 puts the old behaviour back.
            // PURE SCALE ABOUT THE ORIGIN. Measured against GDI's own fitted outline: our
            // UNPHASED Times New Roman Bold 'I'@12 spans 16..268, and 16 * 0.856 = 13.7 and
            // 268 * 0.856 = 229.4 -- GDI's outline is 14..229. So GDI scales every x by the
            // compatible/linear ratio about x=0, where our tree walk distributes the shift
            // over touched points and lets IUP spread it, giving -4..248. WPF_CT_PHASE_SCALE=1.
            if (s_phaseScale)
            {
                for (int i = 0; i < _realPoints + 2 && i < _glyphZone.CurX.Length; i++)
                    _glyphZone.CurX[i] = (int) (((long) _glyphZone.CurX[i] * _ctFactor16 + 0x8000) >> 16);
                _phaseApplied = true;
                return;
            }
            if (s_phaseSkipShrinking && _ctFactor16 < 0x10000) return;
            if (_ctFactor16 == 0x10000) return;
            BuildPhasePartners(_realPoints);
            int n = _realPoints + 4;
            if (_phaseFlags.Length < n) _phaseFlags = new byte[n + 8];
            Array.Clear(_phaseFlags, 0, _phaseFlags.Length);
            if (!s_phaseFaithful)
            {
                Array.Clear(_phaseDone, 0, _phaseDone.Length);
                for (int i = 0; i < _realPoints + 2 && i < _glyphZone.CurX.Length; i++)
                {
                    if (i < _realPoints && (_glyphZone.Tags[i] & TagTouchX) == 0) continue;
                    int ph = PhaseOf(i);
                    if (ph != 0) _glyphZone.CurX[i] += ph;
                }
                return;
            }
            // ExecutePhaseControl@18007fc60 walks EVERY node in index order, phantoms included,
            // and does not ask whether the point was touched.
            if (s_phaseTrace)
            {
                int roots = 0, one = 0, two = 0, pair = 0, touched = 0;
                for (int i = 0; i < _realPoints; i++)
                {
                    if ((_glyphZone.Tags[i] & TagTouchX) != 0) touched++;
                    if (_phaseP0[i] < 0) roots++; else if (_phaseP1[i] < 0) one++; else two++;
                    if (i < _phasePartner.Length && _phasePartner[i] >= 0) pair++;
                }
                Console.Error.WriteLine($"PHASETREE pts={_realPoints} touchedX={touched} "
                    + $"roots={roots} oneParent={one} twoParents={two} pairs={pair} "
                    + $"ctFrac={_ctFrac:0.0000} cycle={_phaseAnyCycle}");
            }
            if (s_phaseDump)
            {
                int[] before = (int[]) _glyphZone.CurX.Clone();
                for (int k = 0; k < n && k < _glyphZone.CurX.Length; k++) PhaseShiftNode(k);
                Console.Error.WriteLine($"PHASEDUMP pts={_realPoints} ctFrac={_ctFrac:0.0000} ctFactor={_ctFactor16 / 65536.0:0.0000} cycle={_phaseAnyCycle} rootDirect={PhaseRootDirect}"
                    + $" compat64={CompatibleAdvance64} linear64={_glyphZone.OrgX[_realPoints + 1] - _glyphZone.OrgX[_realPoints]}"
                    + $" bilevelSpan64={BiLevelSpan64}");
                for (int k = 0; k < n && k < _glyphZone.CurX.Length; k++)
                {
                    int pr = k < _phasePartner.Length ? _phasePartner[k] : -1;
                    int co = k < _phaseColour.Length ? _phaseColour[k] : 0;
                    Console.Error.WriteLine($"  p{k,3} p0={_phaseP0[k],4} p1={_phaseP1[k],4} col={co} partner={pr,4} org={_glyphZone.OrgX[k],6} x={before[k],6} -> {_glyphZone.CurX[k],6} d={_glyphZone.CurX[k] - before[k],5} tx={TouchMark(k)}");
                }
                return;
            }
            // WHEN THE TREE FINDS NOTHING, FALL BACK TO A SCALE. Measured on Arial Italic 'w' at
            // 16ppem, whose hdmx advance is 9 against a linear 11.555: the tree yields no shift at
            // all (phase on and off are bit-identical, 31,059 either way) because every one of its
            // points is placed from a control value, so nothing is left for the partners to carry.
            // GDI compresses that glyph anyway -- its rendered ink is NARROWER than ours by 3-5
            // lamps a row and reaches a row higher. A blanket scale (WPF_CT_PHASE_SCALE) is far
            // worse at 4,458,490 because it overrides the tree everywhere; this only speaks up
            // where the tree had nothing to say. WPF_CT_PHASE_FALLBACK=0 turns it off.
            int before0 = _glyphZone.CurX.Length > 0 ? _glyphZone.CurX[0] : 0;
            bool moved = false;
            for (int i = 0; i < n && i < _glyphZone.CurX.Length; i++)
            {
                int was = _glyphZone.CurX[i];
                PhaseShiftNode(i);
                if (_glyphZone.CurX[i] != was) moved = true;
            }
            // WPF_CT_PHASE_ANCHOR=1: re-anchor the outline on the PHASED left phantom. The pair
            // rule can move pp1 itself -- Arial Bold 'A'@20's pp1 goes -10/64 as the mate of p4 --
            // and the raster is positioned from the phantoms after hinting. The earlier
            // "REFUTED: 6,767 -> 71,350, holdout 24.7M, 129 ratchets" was of a broken version of
            // this that sat INSIDE the loop above and shifted the glyph once per point.
            if (s_phaseAnchorPp1 && _realPoints < _phaseVal.Length && _realPoints < _glyphZone.CurX.Length)
            {
                int dx = -_phaseVal[_realPoints];
                if (dx != 0) for (int q = 0; q < n && q < _glyphZone.CurX.Length; q++) _glyphZone.CurX[q] += dx;
            }
            if (!moved && s_phaseFallbackScale && _ctFactor16 != 0x10000)
                for (int i = 0; i < _realPoints + 2 && i < _glyphZone.CurX.Length; i++)
                    _glyphZone.CurX[i] =
                        (int) (((long) _glyphZone.CurX[i] * _ctFactor16 + 0x8000) >> 16);
        }

        /// <summary>Scale the glyph when the phase tree produced no shift at all. See the note at
        /// the call site. WPF_CT_PHASE_FALLBACK.</summary>
        private static readonly bool s_phaseFallbackScale =
            Environment.GetEnvironmentVariable("WPF_CT_PHASE_FALLBACK") == "1";

        /// <summary>PhaseShift@18007fd10, ported statement for statement. Unlike the earlier
        /// PhaseOf this is not a pure function: GDI's version MOVES points as it walks, and which
        /// points it moves is most of the rule.</summary>
        /// <summary>ExecutePhaseControl's param_3: GDI computes it as "did any node close a
        /// cycle", but that is decided by OUR tree, so it stays switchable.</summary>
        /// <summary>WPF_CT_PHASE_PHANTOM=0 denies the two x phantoms their direct phase, which
        /// makes them ordinary roots -- a bisection handle, not one of GDI's rules.</summary>
        /// <summary>WPF_CT_PP1_ORIGIN: 1 (default) fs__Contour's re-anchor on the fitted pp1 with
        /// its rounding; 2 = exact (no rounding); 3 = x only; 0 = off. See Hint.</summary>
        /// <summary>fs__Contour@140025398's re-anchor rounds dx in the CURRENT frame, and by the
        /// time it runs the outline has been through mth_IntelMul with the device matrix -- which
        /// carries the ClearType OVERSCALE, since the same function divides the finished x back
        /// down by `globals[0x49a]` at 140024dfc. So its two roundings are a whole pixel and a
        /// sixteenth OF AN OVERSCALED PIXEL, and which one it takes is
        ///     bVar49 = (flags[0x41a] bit 0 set) &amp;&amp; (bit 1 clear)   ->  sixteenth
        ///     otherwise                                          ->  whole pixel
        /// with bit 0 = ClearType and bit 1 = compatible widths (the span/factor block at
        /// 140025320 is gated on both, and the second globals block exists only when bit 1 is
        /// set). Our pass is ClearType WITH compatible widths, so bit 1 is set and the rounding
        /// is a whole OVERSCALED pixel -- one sample, a SIXTH of a real pixel -- not the
        /// sixteenth we had.
        /// <para>REFUTED BY MEASUREMENT, and kept as the record of the reading: rounding dx to a
        /// whole OVERSCALED pixel (a sixth of a real one) measures 209,012 against the shipped
        /// sixteenth-of-a-real-pixel's 172,715, and the other arm of the same branch -- a
        /// sixteenth of an overscaled pixel, which at our resolution is no rounding at all --
        /// measures 184,107 (WPF_CT_PP1_ORIGIN=exact). So whatever frame fs__Contour's re-anchor
        /// is in by the time it runs, it is not one where the outline has already been multiplied
        /// by six; either mth_IntelMul's matrix does not carry the oversample (the division back
        /// at 140024dfc would then be undoing something applied later) or uVar20/pfVar40 gate the
        /// rounding away for this pass. The sixteenth of a REAL pixel is also what every rounding
        /// inside the interpreter uses, so it keeps the fitted outline on one grid.</para>
        /// WPF_CT_PP1_SAMPLE=1 measures the overscaled reading again.</summary>
        private static readonly bool s_pp1Sample =
            Environment.GetEnvironmentVariable("WPF_CT_PP1_SAMPLE") == "1";

        /// <summary>Round a 26.6 x to a whole pixel of the 6x oversampled frame, which is what
        /// `(dx + 0x20) &amp; ~0x3f` does there. Kept in real 26.6, so the answer is the nearest
        /// sixty-fourth to it.</summary>
        private static int RoundToSample(int dx)
        {
            long over = (long) dx * TrueTypeFont.ClearTypeOversample;
            over = (over + 32) & ~63L;
            long half = TrueTypeFont.ClearTypeOversample / 2;
            return (int) ((over >= 0 ? over + half : over - half) / TrueTypeFont.ClearTypeOversample);
        }

        private static readonly int s_pp1Origin =
            Environment.GetEnvironmentVariable("WPF_CT_PP1_ORIGIN") switch
            { "0" => 0, "exact" => 2, "x" => 3, _ => 1 };

        /// <summary>WPF_CT_YSHIFT=&lt;n&gt;: the same diagnostic in y. A DIAGNOSTIC, never a rule.</summary>
        private static readonly int s_yShiftProbe =
            int.TryParse(Environment.GetEnvironmentVariable("WPF_CT_YSHIFT"), out int ys) ? ys : 0;

        private static readonly int s_xShiftProbe =
            int.TryParse(Environment.GetEnvironmentVariable("WPF_CT_XSHIFT"), out int xs) ? xs : 0;

        private static readonly bool s_phasePhantom =
            Environment.GetEnvironmentVariable("WPF_CT_PHASE_PHANTOM") != "0";

        private bool PhaseRootDirect =>
            s_phaseRootFromCycle ? _phaseAnyCycle : s_phaseRootDirect;

        /// <summary>RE-READ END TO END AGAINST PhaseShift@140035b80 (2026-09-19), because the
        /// anchor census says a handful of points want a shift one or two sixty-fourths from the
        /// one this produces. Everything below is what the binary does:
        /// <list type="bullet">
        /// <item>The node value lives at node+8 as a 32-bit int and the node record is 12 bytes.
        /// Its units are SIXTY-FOURTHS -- `2*cur*(f-0x10000)` is 26.6 x 16.16, and the `>> 17`
        /// leaves 26.6 -- so GDI rounds the shift AT EVERY NODE exactly as we do. "Carry the
        /// phase fractionally down the tree and round once at the point" is not what it does, and
        /// that was the best remaining guess at where a sixty-fourth could come from.</item>
        /// <item>One parent: `mov w3,w8; bl PhaseShift` and the child takes the parent's value
        /// unchanged. Two parents: it computes both and, if they are EQUAL, skips the averaging
        /// (`cmp w0,w24; b.eq`).</item>
        /// <item>The mate rule fires only when BOTH parents are present; with either missing it
        /// falls to 140035e08, the sum form `(cur[p] + cur[mate]) * (f-1)`.</item>
        /// <item>param_3's re-derive at 140035e5c is gated on `node[+2] == -1` and then on
        /// `orgX[node[+0]] == orgX[p]` -- the ORG array at elem+0x10, not orus and not cur.</item>
        /// <item>A root does not move: with no parent it stores its value and falls through
        /// without touching curX.</item>
        /// </list>
        /// So the shift machinery is not where the remaining sixty-fourths are; the TREE is the
        /// only part of the phase left that could put them there.
        /// <para>THE SMALLEST COMPLETE CASE, worked end to end 2026-09-20 so the next attempt does
        /// not have to. CONSOLAS '1' AT 18ppem is 256 of the holdout, twelve points, six of them
        /// x-touched, and the solver says GDI's outline is OURS WITH ONE POINT MOVED: hold the
        /// other five anchors and P3 alone reaches GDI at 290 against our 291; hold P3 instead and
        /// NO placement of the other five reaches it. Every number:
        /// <code>
        ///   program   51 SVTCA[x]  52 IP(p3 between rp1=pp2, rp2=pp1)  53 MDAP[r] p3
        ///   IP        orus 1126 -> 0, cur 632 -> 0, orus[p3] 512
        ///             round(-614 * -632 / -1126) = -345,  632 - 345 = 287
        ///   MDAP[r]   RoundToGridSP(287) = (287 + 0 + 2) &amp; ~3 = 288
        ///   phase     factor = 640/633 -&gt; 66261, f-1 = 725;  v(pp2) = round(632*725/65536) = 7
        ///             p3 = avg(pp2, pp1) = trunc((288-0)*7 / 633) = 3   -&gt; 291
        ///   GDI                                                            290
        /// </code>
        /// and the constraint that makes it hard: p1, p6, p7 and p10 all INHERIT p3's shift and
        /// are all correct at 3, and p9 is p3's mate and is correct at 3. So GDI does not give the
        /// subtree a different shift. Nor can the pre-phase value be 287: RoundToGridSP only ever
        /// returns a multiple of four, so MDAP's output is 284, 288 or 292 and the phased results
        /// are 287, 291, 295 -- 290 is not among them. Something gives p3, and p3 alone, one
        /// sixty-fourth less than the tree does, and neither the factor (swept, worse everywhere)
        /// nor the rounding grid (swept, worse everywhere) is it.</para>
        /// <para>AND THERE IS A SECOND SOLUTION, which is the useful part. Hold P3 AND P9 at our
        /// values and the search still reaches GDI, this time by P6 alone: 79 -> 77. P6's
        /// pre-phase x is 76 and it inherits v = 3 through p1 from p3. Seventy-seven is exactly
        /// what the RE-DERIVE produces -- `PhaseDiv(2 * 76 * 725)` = 1, so 76 + 1 = 77 -- and the
        /// re-derive is gated on ExecutePhaseControl's param_3, which is the "any node carries the
        /// cycle flag" boolean and is FALSE for this glyph. Forcing it on
        /// (WPF_CT_PHASE_ROOTCYCLE=0) takes Consolas Regular '1'@18 from 256 to 118 -- halved --
        /// while costing Bold 44 and Italic 883, so it is not a blanket rule but the mechanism is
        /// in that gate. What is missing is a reason for the flag to be set on THIS glyph and not
        /// on its bold and italic.</para>
        /// <para>AND THE DEEPER LESSON, which undercuts the framing above. There are at least
        /// THREE distinct single-point fixes for this glyph, all reaching GDI exactly: the free
        /// solver names P4 (-1), the anchor search names P3 (-1), and with P3 and P9 held it
        /// names P6 (-2), which also drags P5. Four differing lamps do not determine an outline.
        /// So "GDI's outline is ours with ONE point moved" is a property of the solver's
        /// minimality, not a fact about GDI -- the real difference may be anywhere in the glyph,
        /// and chasing the point the solver happens to name is chasing an artifact. What the
        /// evidence does say is only that SOME change on the scale of a sixty-fourth fixes it.</para>
        /// <para>What IS pinned, because every step was read: org is GDI's (GGO's unhinted
        /// outline gives g3 = 4.500px against our 288/64), the bi-level fit is GDI's (11 of 11
        /// points exact), the bi-level chain through the same IP and MDAP lands on 320 in both,
        /// and in the ClearType pass the chain is forced -- IP gives 287 from any plausible
        /// advance phantom (632, 633, 636, 628 all end at 288 after MDAP; only an unrounded 640
        /// reaches 292), MDAP's SP round returns a multiple of four, and the phase's avg is 3.
        /// The model produces 291 and cannot produce 290.</para>
        /// <para>REFUTED THE SAME DAY, and worth the line so nobody re-opens it: on the whole
        /// holdout that gate measures 6,151,781 against 28,183, so the re-derive really is
        /// conditional and the 77 above is a coincidence rather than a clue. Nor can the flag
        /// fire on this glyph by any reading of the binary. The pair records, in order, are
        /// SHP(3->7), MIRP(3->9) -- which takes partner[3] = 9 -- MDRP(3->10), MDRP(3->1) and
        /// MDRP(1->6); the only flag-setting branch in the pair tail needs
        /// `partner[nodes[anchor].p0] == anchor`, and partner[12] is -1 throughout while
        /// partner[3] is 9 and never 1.</para>
        /// <para>Everything the flag depends on has now been read end to end and matches:
        /// ExecutePhaseControl@140035970 scans nodes 0..lastEnd+4 for flag bit 0 and passes
        /// `found` as param_3 to every PhaseShift; AddDistance@1400354c8 sets that bit on the
        /// PLACED node in two places -- when IndirectlyDependsOn(anchor, placed) is true
        /// (14003561c, and it then still falls into the pair tail), and when the anchor's parent
        /// already partners the anchor (14003560c) -- and its ancestor walk climbs
        /// `nodes[cur].p0` while ORUS (elem+0x20) is equal, stopping otherwise. The pair tail
        /// fires only for colour 1, only when the anchor has no partner yet, and never when the
        /// placed point already partners the anchor.</para></summary>
        private int PhaseShiftNode(int p)
        {
            if (p < 0 || (uint) p >= (uint) _phaseFlags.Length) return 0;
            if ((uint) p >= (uint) _glyphZone.CurX.Length) return 0;
            byte fl = _phaseFlags[p];
            if ((fl & 4) != 0) return 0;                 // already on the stack: a cycle
            _phaseFlags[p] = (byte) (fl | 4);
            if ((fl & 2) == 0)
            {
                int a = _phaseP0[p], b = _phaseP1[p];
                int v;
                // The DIRECT branch is not "every phantom" -- it is exactly the two x phantoms
                // (lastEnd+1 and lastEnd+2). The y phantoms take the ordinary tree path.
                if (s_phasePhantom && (p == _realPoints || p == _realPoints + 1))
                    v = PhaseDiv(2L * _glyphZone.CurX[p] * (_ctFactor16 - 0x10000));
                else if (a < 0)
                    v = PhaseRootDirect ? PhaseDiv(2L * _glyphZone.CurX[p] * (_ctFactor16 - 0x10000)) : 0;
                else if (b < 0)
                    v = PhaseShiftNode(a);
                else
                {
                    int vb = PhaseShiftNode(b), va = PhaseShiftNode(a);
                    v = CalcAvgXPhase(a, p, b, va, vb);
                }
                // A recursive call can finish THIS node through its partner (below), in which case
                // GDI abandons everything it just computed and keeps the stored value.
                if ((_phaseFlags[p] & 2) == 0)
                {
                    int mate = p < _phasePartner.Length ? _phasePartner[p] : -1;
                    bool mateFree = mate >= 0 && mate < _glyphZone.CurX.Length
                                    && (_phaseFlags[mate] & 2) == 0;
                    int before = _glyphZone.CurX[p];
                    if (!mateFree)
                    {
                        // Only a node with FEWER THAN TWO parents may be re-derived directly, and
                        // only once its own x has come away from its parent's.
                        // A BLACK link is a STROKE, and GDI's compatible-width correction keeps stroke
                        // weight and takes the whole change out of the WHITE. Measured on Times New
                        // Roman Bold 'I'@12, where hdmx forces 4.0px against a natural 4.67: GDI's
                        // fitted stem stays 108/64 while its serifs shrink 72 -> 53.5 a side. Re-deriving
                        // a black-linked point from its own x breaks that, because the two ends of one
                        // stem then move by different amounts. WPF_CT_PHASE_KEEPBLACK=0 restores the old
                        // unconditional re-derivation.
                        if (PhaseRootDirect && b < 0 && (a < 0 || _glyphZone.OrgX[p] != _glyphZone.OrgX[a]))
                            v = PhaseDiv(2L * _glyphZone.CurX[p] * (_ctFactor16 - 0x10000));
                        // A ROOT IS NEVER MOVED. It still publishes its phase for its children to
                        // inherit, but its own x stays where the interpreter left it -- which is
                        // what keeps the advance phantom, a root, from drifting off the compatible
                        // width the whole phase pass exists to reach.
                        if (a >= 0) _glyphZone.CurX[p] += v;
                    }
                    else
                    {
                        // The PAIR rule, and it applies only to a node the tree did NOT already pin
                        // between two parents: both edges move together by the phase of their
                        // centre, and the partner is marked done so it is not phased twice.
                        if (a < 0 || b < 0)
                            v = PhaseDiv((long) (_glyphZone.CurX[p] + _glyphZone.CurX[mate])
                                         * (_ctFactor16 - 0x10000));
                        _glyphZone.CurX[p] += v;
                        _glyphZone.CurX[mate] += v;
                        _phaseVal[mate] = v;
                        _phaseFlags[mate] |= 2;
                    }
                    _phaseVal[p] = v;
                    _phaseFlags[p] |= 2;
                    // PHASENODE: the per-node view LINKCOL/GATE-DROP do not give -- which path set
                    // the node's shift, from which parents and mate, and what it did to the point.
                    // It is how Segoe UI '8' at 11ppem was worked back to its two anchors.
                    if (s_phaseDump)
                        Console.Error.WriteLine($"  PHASENODE p={p,3} a={a,3} b={b,3} mate={mate,3}"
                            + $" {(mateFree ? (a < 0 || b < 0 ? "mate-sum" : "avg+mate") : a < 0 ? "root" : b < 0 ? "parent" : "avg"),-8}"
                            + $" v={v,3}  x {before} -> {_glyphZone.CurX[p]}"
                            + (mateFree ? $"  (mate {mate} -> {_glyphZone.CurX[mate]})" : ""));
                }
            }
            _phaseFlags[p] &= 0xfb;
            return _phaseVal[p];
        }

        /// <summary>CompDiv(0x20000, v), and the inline sequence the phantom branch uses instead of
        /// calling it: v / 2^17 rounded half AWAY FROM ZERO. (The decompiler spells the truncation
        /// out as "+ 0x1ffff when negative", which is what C# integer division already does.)</summary>
        /// <summary>The phase shift for a value of 2 * cur * (factor - 1), in 16.16: half a
        /// 0x20000 added with the product's sign, then divided toward zero.
        /// <para>READ OUT OF PhaseShift@140035b80 (2026-09-19). Its three inline copies are
        /// `n = 2*cur*(f - 0x10000); n += (n &lt; 0 ? -0x10000 : 0x10000); (n + (n &lt; 0 ?
        /// 0x1ffff : 0)) &gt;&gt; 17` -- the last step being the compiler's idiom for a signed
        /// divide by 2^17 that truncates toward zero, so the whole is round-half-away, which this
        /// is. Its mate case is `CompDiv(0x20000, (cur &lt;&lt; 1) * (f - 0x10000))`, the same
        /// value. Its average is a plain C division, truncating, as CalcAvgXPhase's is. And it
        /// runs BEFORE the interpolation in itrp_IUP, as ours does by default. So the phase is
        /// exact in arithmetic and in order, and whatever still moves an interpolated point a
        /// sixty-fourth is in which instructions run under ClearType, not in what any computes.</para>
        /// </summary>
        private static int PhaseDiv(long v)
        {
            v += v < 0 ? -0x10000L : 0x10000L;
            return (int) (v / 0x20000L);
        }

        /// <summary>The partner map used by both phase entry points.</summary>
        private void BuildPhasePartners(int pointCount)
        {
            if (s_phaseGdiPairs) return;   // already filled, one-way, by PhasePair

            if (_phasePartner.Length < pointCount + 4) _phasePartner = new int[pointCount + 4];
            for (int i = 0; i < _phasePartner.Length; i++) _phasePartner[i] = -1;
            if (!s_phasePairs) return;
            for (int k = 0; k < _linkCount; k++)
            {
                int r = _linkA[k], q = _linkB[k];
                if ((uint) r >= (uint) pointCount || (uint) q >= (uint) pointCount) continue;
                if (s_phasePairAdjacent && !PhaseAdjacent(r, q, pointCount)) continue;
                if (_phasePartner[r] < 0 && _phasePartner[q] < 0)
                { _phasePartner[r] = q; _phasePartner[q] = r; }
            }
        }

        private void PhaseDistance(int r, int p, int colour)
        {
            // GDI records nothing unless the PROJECTION IS ON THE CLEARTYPE (x) AXIS. Every
            // AddDistance call site tests localGS+0xcc -- the flag itrp_SVTCA_1 sets when it puts
            // the vectors on x under ClearType -- and skips the call outright when it is clear:
            //     ldrh w8,[x19,#0xcc] ; cbz w8, skip ; ldrh w8,[x22,#0x1c0] ; tbz w8,#1, skip
            // We had been recording every link, diagonal ones included, which fed the tree
            // relationships GDI never puts in it.
            // THIS GATE IS WHERE ARIAL BOLD 'K' LOSES ITS ONE POINT, and the gate GDI uses is not
            // the one modelled here.
            // <para>K at 20ppem is a single wrong point, p6, and the shift it wants (-14) is
            // produced by exactly one rule in the pass: a two-parent proportion between a point at
            // org 281 and p5 at org 903. The program offers precisely that -- SDPVTL at
            // instruction 175 sets the line from (10, 5) and the MDRP at 180 is what moves p6 --
            // and this gate throws the link away, because after SDPVTL the projection is DIAGONAL
            // and our latch says "not on the ClearType axis". Turn the gate off
            // (WPF_CT_PHASE_XONLY=0) and K goes 922 -> ZERO, and 'A' 6,767 -> 6,567.</para>
            // <para>AND K MAY NOT BE A PHASE VALUE AT ALL -- the "p6 shifts -6 where GDI shifts
            // -14" reading assumes our PRE-PHASE x for p6 is GDI's, and nothing establishes that.
            // "62/62 bi-level exact" is about the BI-LEVEL fit; the ClearType fit is free to
            // differ, and GDI's 547 is equally consistent with a pre-phase 553 shifted -6. Three
            // things were checked and none of them is the cause: all twelve points are x-touched
            // (tx=T in the PHASEDUMP trace), so IUP[x] interpolates nothing and cannot be blamed;
            // p6's MDRP runs with the SDPVTL line = (6,7), so the proportion branch is rejected by
            // `a == placed` in GDI exactly as in ours, and the tempting arithmetic that made -14
            // fall out of a proportion between p10 and p5 was a coincidence -- several pairs
            // spanning the glyph give -14, and (10,5) is not this instruction's line. Do not spend
            // another round on K without an independent measurement of its pre-phase x.</para>
            // <para>Turning it off wholesale is not the answer -- holdout 598,774 -> 6,983,952
            // with 162 ratchets failing, because then every y-axis link is recorded too. The gate
            // is real; ours is simply computing the wrong predicate.</para>
            // <para>SETTLED, AND THIS GATE IS RIGHT -- the paragraph above is why K is NOT a gate
            // bug, kept because two earlier passes each reached a wrong answer from a partial read.
            // <list type="number">
            // <item>The apparent contradiction (SVTCA_1 stores 1 / SDPVTL "stores 0 for pure x")
            // was a ONE-BRANCH decode. itrp_SDPVTL's write is gated on globals[0x1c0] bit 2 -- the
            // bit that says WHICH axis ClearType oversamples -- and I had only followed the bit-2-set
            // arm. The whole thing at 14003d918..14003d96c is
            //     store 1  iff  component != 0x4000 || other != 0
            // with (component, other) = (pv.y, pv.x) when bit 2 is clear, i.e. STORE 0 ONLY FOR A
            // PURE-AXIS VECTOR. SVTCA[y] -> 0 and SVTCA[x] -> 1 fit that exactly. There is no
            // contradiction; 0xcc is "the projection is NOT purely off the ClearType axis", which
            // is NotPureYProjection, which is what InClearTypeDirection already computes.</item>
            // <item>And this gate already used it: s_phaseAxisExact defaults to FALSE, so the
            // shipped predicate has always been InClearTypeDirection, not OnClearTypeAxis. The
            // note's claim that "our latch says not on the ClearType axis" was never true of the
            // shipped path. Implementing GDI's predicate a second time (WPF_CT_PHASE_GATE) moved
            // Arial K/A/X and Times 'o' by exactly ZERO and was reverted.</item>
            // <item>K's link is NOT dropped here. The GATE-DROP trace below shows every drop in the
            // ClearType pass is pv=(0,0x4000) pure Y; the ctInfo=False rows are the BI-LEVEL pass,
            // whose tree is discarded. K's diagonal MIRP link (r=10 p=6, pv=(12042,-11110), the very
            // point that is wrong) is RECORDED. What WPF_CT_PHASE_XONLY=0 gives K is the pure-Y
            // links, and those are gated in the binary: itrp_ALIGNRP's AddDistance at 140036258 sits
            // inside the region every failing test branches past (mode!=2, 0xcc==0, 0x1c0 bit 1
            // clear all jump to 14003625c), and itrp_MDRP's AddProportion at 14003aa1c likewise.
            // itrp_ISECT remains the ONLY ungated call site, which we already model. So K@20's 922
            // is a phase VALUE, not a missing link, and XONLY=0 merely compensates -- at the cost of
            // 598,774 -> 6,983,952 and 162 ratchets.</item></list></para>
            if (s_phaseXAxisOnly && !(s_phaseAxisExact ? OnClearTypeAxis : InClearTypeDirection))
            { if (s_phaseDump) Console.Error.WriteLine($"  GATE-DROP r={r,3} p={p,3} via={_phaseSite} {GateWhy()}"); return; }
            int n = _realPoints + 4;
            if ((uint) p >= (uint) n || (uint) r >= (uint) n || p == r) return;
            if ((uint) p >= (uint) _phaseP0.Length || (uint) r >= (uint) _phaseP0.Length) return;
            if (s_phaseDump) Console.Error.WriteLine($"  ADDDIST r={r,3} p={p,3} col={colour} via={_phaseSite,-22} "
                + $"p0={_phaseP0[p],3} dep={PhaseDependsOn(r, p, 100)} line=({_pvPtA},{_pvPtB})");
            // SHP DOES RECORD A LINK, AND A CALLER LIST DOES NOT PROVE OTHERWISE. Ghidra shows
            // AddDistance@1400354c8 with two callers, itrp_ALIGNRP and itrp_MSIRP, and it is
            // tempting to conclude that SHP records nothing -- which would make Tahoma 'q'@15's
            // P17 and P20 roots, since that glyph has no ALIGNRP and no MSIRP. It is wrong.
            // itrp_SHP_Common@14003e978 carries the body INLINED: the same IndirectlyDependsOn
            // call, the same node array at elem+0x68 with its twelve-byte stride, the same
            // ancestor walk up node[+0] while `elem[+0x20]` (ORUS) is equal, and the same write of
            // the ancestor into node[+0] with 0xffff into node[+2]. MIRP inlines it too, which is
            // already written down at the MIRP site. A missing call site means the compiler
            // inlined the function, not that the work does not happen.
            // <para>The SHP block is gated on four things, and the fourth is one this file does
            // not model: `elem != twilight && globals[0x16b] == 2 && localGS[0xcc] != 0 &&
            // (globals[0x1c0] &gt;&gt; 1 &amp; 1) != 0`. The first three are the glyph-program
            // mode and the ClearType-direction latch we already gate on; 0x1c0 bit 1 is the same
            // bit itrp_IUP tests before choosing its reference array, and whether it is always set
            // on our path is not established.</para>
            // <para>So Tahoma 'q'@15's P17 and P20 are one-parent nodes in GDI as well, and the
            // sixty-fourth that separates them is still unexplained.</para>
            // ORDER MATTERS, and GDI's is the reverse of the obvious one: AddDistance asks
            // IndirectlyDependsOn(r, p) FIRST and only then looks at whether p already has a parent.
            // A re-link onto an already-placed point therefore still RAISES THE CYCLE FLAG when the
            // reference depends on it -- and that flag is ExecutePhaseControl's param_3, which decides
            // for the whole glyph whether a one-parent node re-derives its phase from its own x or
            // inherits its parent's. Testing 'already parented' first swallowed those flags.
            if (PhaseDependsOn(r, p, 100))
            {
                _phaseAnyCycle = true;                  // GDI: nodes[p].flags |= 1
                PhasePair(r, p, colour);
                return;
            }
            if (_phaseP0[p] >= 0) { PhasePair(r, p, colour); return; }   // parented; first wins
            int anc = PhaseAncestor(r, p);
            _phaseP0[p] = anc;
            _phaseP1[p] = -1;
            if (p < _phaseColour.Length) _phaseColour[p] = colour;
            PhasePair(anc, p, colour);
        }

        /// <summary>AddDistance's param_5 == 1 tail. Note the write is ONE-WAY -- the ANCESTOR
        /// gets the partner pointer and the placed point does not point back, so PhaseShift moves
        /// the pair only when it reaches the ancestor. BuildPhasePartners had been making it
        /// symmetric, which phases the same stem from both ends.</summary>
        private void PhasePair(int anc, int p, int colour)
        {
            if (s_phaseDump) Console.Error.WriteLine($"  PAIR? anc={anc,3} p={p,3} col={colour} partner[anc]={(anc < _phasePartner.Length ? _phasePartner[anc] : -9)} partner[p]={(p < _phasePartner.Length ? _phasePartner[p] : -9)} p0[anc]={_phaseP0[anc]}");
            if (!s_phaseGdiPairs || colour != 1) return;
            // WPF_CT_PHASE_PHANTOM_MATE=0: a PHANTOM is never a mate. Arial Bold 'A'@20: the link
            // (5,4) re-targets to pp1 because pp1 and p5 share orus 0, the pair then shifts pp1 by
            // avg(0,195)*(f-1) = -10 and p5 inherits it -- while GDI's own phase raster (quality
            // 5 against 6) leaves that left edge exactly where the unphased one has it.
            // GDI'S READING SHIPS AND THE EXEMPTION IS EXPENSIVE. AddDistance's pair tail has no
            // phantom test at all -- its bounds are `index < lastContourEnd + 5`, which INCLUDES
            // the four phantoms -- so a phantom may be either end of a pair. Re-measured
            // 2026-09-18: refusing them costs 172,715 -> 230,692. WPF_CT_PHASE_PHANTOM_MATE=0.
            if (!s_phasePhantomMate && (anc >= _realPoints || p >= _realPoints)) return;
            if ((uint) anc >= (uint) _phasePartner.Length) return;
            if ((uint) p >= (uint) _phasePartner.Length) return;
            if (_phasePartner[anc] >= 0 || _phasePartner[p] == anc) return;
            // THE ANCHOR'S PARENT, NOT THE PLACED POINT'S. AddDistance@1801db148 reads [x7], and x7
            // is &nodes[anchor] -- the same base whose partner slot was just tested at [x7,#4]:
            //     ldrsh w8,[x7]          ; nodes[anchor].p0
            //     smaddl x8,w8,w5,x6     ; &nodes[that]
            //     ldrsh w8,[x8, #0x4]    ; .partner
            //     cmp w8,w11 ; b.ne set  ; == anchor ? cycle : make the pair
            // Reading the PLACED point's parent instead swallowed the flag. Times New Roman Bold 'I'
            // is the case that shows it: the right serif is MIRP'd black off the stem's right edge,
            // whose own parent is the left edge, whose partner IS that right edge -- a closed loop.
            // GDI flags it, so ExecutePhaseControl's param_3 goes to 1 for the whole glyph, and every
            // one-parent node then re-derives its phase from its own x instead of inheriting the
            // stem's. That is the difference between serif tips at -4..248 and GDI's 11..224.
            int par = _phaseP0[anc];
            if (par >= 0 && (uint) par < (uint) _phasePartner.Length && _phasePartner[par] == anc)
                _phaseAnyCycle = true;                  // GDI sets the node's flag bit 0
            else
                _phasePartner[anc] = p;
        }

        /// <summary>IndirectlyDependsOn@1801db288: is <paramref name="target"/> an ancestor of
        /// <paramref name="node"/> in the phase tree? Depth-limited exactly as GDI's is (100,
        /// decremented by two per level), so a malformed program cannot spin.</summary>
        private bool PhaseDependsOn(int node, int target, int depth)
        {
            if (depth - 1 < 0) return true;
            if ((uint) node >= (uint) _phaseP0.Length) return false;
            int a = _phaseP0[node], b = _phaseP1[node];
            if (a < 0) return false;
            if (b < 0)
                return a == target || PhaseDependsOn(a, target, depth - 2);
            if (a == target || b == target) return true;
            return PhaseDependsOn(a, target, depth - 2) || PhaseDependsOn(b, target, depth - 2);
        }

        /// <summary>AddProportion@1802946c0, which IP calls for a point it places BETWEEN two
        /// references: that point takes BOTH of them as parents, and CalcAvgXPhaseShift later
        /// interpolates their phases across it. Faithful to the original: every index checked and
        /// distinct, a cycle marked rather than recorded, and the pair written ONLY when both
        /// parent slots are still empty -- the first proportion a point is given wins.</summary>
        private void PhaseProportion(int a, int placed, int b, bool axisGate = true,
            [System.Runtime.CompilerServices.CallerMemberName] string site = "")
        {
            // ISECT DOES NOT TAKE THE AXIS GATE. Every AddDistance call site tests the ClearType
            // axis latch first, but itrp_ISECT's AddProportion is guarded only by `mode == 2 &&
            // ClearType-flags bit 1` -- the compatible-widths bit -- with no axis condition at
            // all. It matters because ISECT runs with the projection on Y (Arial 'X' sets it with
            // SFVTCA[y] before building its crossing), so the axis gate threw the record away and
            // the intersection point was left out of the phase tree entirely: 'X'@24's crossing
            // kept its unphased x while everything around it was compressed onto the advance.
            if (axisGate && s_phaseXAxisOnly
                && !(s_phaseAxisExact ? OnClearTypeAxis : InClearTypeDirection)) return;
            int n = _realPoints + 4;
            if ((uint) placed >= (uint) n || (uint) a >= (uint) n || (uint) b >= (uint) n) return;
            if (a == placed || b == placed || a == b) return;
            // WPF_CT_PHASE_PROPPHANTOM=0: refuse a proportion whose REFERENCE is a phantom. A
            // probe. AddProportion's own bounds test is `index < lastContourEnd + 5`, which
            // INCLUDES the four phantoms, so the binary permits it -- but permitting is not the
            // same as GDI's rp1/rp2 ever being a phantom there, and a phantom parent is how the
            // advance's own shift gets mixed into a real point. Segoe UI 'o'@12 is the case:
            // `ADDPROP a=3 p=21 b=25` is an IP between a real point and the RIGHT PHANTOM, the
            // phantom carries -2, and P21 interpolates to -1.52 -> -1 where GDI's pixels want -2.
            // <para>REFUTED. Refusing them is worse wherever it acts and neutral elsewhere: Segoe
            // UI 'o'@12 137 -> 236, 's'@13 118 -> 647, Tahoma 'b'@12 118 -> 3,051, with Tahoma
            // 'q'@16, Arial 'e'@13 and Times 'a'@13 unmoved. GDI records proportions onto
            // phantoms, which is what its bounds test already said.</para>
            if (!s_phasePropPhantom && (a >= _realPoints || b >= _realPoints)) return;
            if ((uint) placed >= (uint) _phaseP0.Length) return;
            // WHICH OPCODE MADE THE LINK. Without it the six ADDPROP lines a glyph emits cannot
            // be matched to the instructions that caused them, and the references they carry do
            // not obviously come from where they should: Arial Bold 'X'@20 moves points 1, 4, 7
            // and 10 with IP (0x0F), whose site passes (rp1, rp2) = (2, 9) for all four, yet the
            // recorded parents are (2,9), (5,0), (6,11) and (6,11).
            if (s_phaseDump) Console.Error.WriteLine($"  ADDPROP a={a,3} p={placed,3} b={b,3}"
                + $" via={site,-22} depA={PhaseDependsOn(a, placed, 100)}"
                + $" depB={PhaseDependsOn(b, placed, 100)}");
            if (PhaseDependsOn(a, placed, 100) || PhaseDependsOn(b, placed, 100))
            { _phaseAnyCycle = true; return; }          // GDI sets node[P].flags |= 1 here
            if (_phaseP0[placed] < 0 && _phaseP1[placed] < 0)
            { _phaseP0[placed] = a; _phaseP1[placed] = b; }
        }

        private int PhaseAncestor(int r, int placed)
        {
            int cur = r;
            for (int guard = 0; guard < 50; guard++)
            {
                int par = _phaseP0[cur];
                if (par < 0 || par == placed) break;
                // +0x20 in AddDistance's walk is OrusX -- FONT UNITS. Two points that differ
                // in the design can round to one scaled OrgX, and then we walk past a join
                // GDI stops at.
                if (_glyphZone.OrusX[par] != _glyphZone.OrusX[cur]) break;
                cur = par;
            }
            return cur;
        }

        /// <summary>Which contour a point belongs to, or -1. ContNum@GDI.</summary>
        private int PhaseContour(int p)
        {
            int start = 0;
            for (int c = 0; c < _contourCount && c < _glyphZone.Contours.Length; c++)
            {
                int end = _glyphZone.Contours[c];
                if (p >= start && p <= end) return c;
                start = end + 1;
            }
            return -1;
        }

        /// <summary>DoubleCheckLinkColor@180123298, ported exactly. This is the function whose
        /// RETURN VALUE is AddDistance's param_5 at the four call sites that can make a pair, so it
        /// is the whole of GDI's pairing rule and not the adjacency guess we had.
        /// <para>It answers 1 or 2 -- BLACK or WHITE -- for two points that are neighbours on one
        /// contour, turn the same way, and are joined by a segment shallower than 2:1; anything
        /// else is 0, or the caller's own colour when the points are not neighbours at all. Only
        /// colour 1 makes a partner, and telling 1 from 2 needs the contour's WINDING, which is
        /// exactly what our old adjacency test was missing: it kept both.</para></summary>
        private int PhaseLinkColour(int p1, int p2, int colour)
        {
            int c1 = PhaseContour(p1);
            int c2 = PhaseContour(p2);
            if (s_phaseDump)
                Console.Error.WriteLine($"  LINKCOL p1={p1,3} p2={p2,3} in={colour}"
                    + $" contour {c1}/{c2} of {_contourCount}"
                    + $" ends=[{string.Join(",", System.Linq.Enumerable.Take(_glyphZone.Contours, Math.Max(0, _contourCount)))}]");
            if (c1 < 0) return 0;
            if (c2 < 0) return 0;
            if (c1 != c2) return colour;
            int end = _glyphZone.Contours[c1];
            int start = c1 == 0 ? 0 : _glyphZone.Contours[c1 - 1] + 1;
            if (end <= start) return 0;
            int next1 = p1 != end ? p1 + 1 : start;
            int prev1 = p1 == start ? end : p1 - 1;
            if (p2 != next1 && p2 != prev1) return colour;
            int next2 = p2 != end ? p2 + 1 : start;
            int prev2 = p2 == start ? end : p2 - 1;
            int[] x = _glyphZone.OrusX, y = _glyphZone.OrusY;
            if ((uint) prev1 >= (uint) x.Length || (uint) next1 >= (uint) x.Length) return 0;
            if ((uint) prev2 >= (uint) x.Length || (uint) next2 >= (uint) x.Length) return 0;
            bool t1 = (long) (x[p1] - x[prev1]) * (y[next1] - y[p1])
                    < (long) (y[p1] - y[prev1]) * (x[next1] - x[p1]);
            bool t2 = (long) (x[p2] - x[prev2]) * (y[next2] - y[p2])
                    < (long) (y[p2] - y[prev2]) * (x[next2] - x[p2]);
            int dx0 = Math.Abs(x[p2] - x[p1]), dy0 = Math.Abs(y[p2] - y[p1]);
            if (s_phaseDump)
                Console.Error.WriteLine($"    adjacent: t1={t1} t2={t2} dx={dx0} dy={dy0}"
                    + $" wind={PhaseWinding(c1)}"
                    + $" -> {(t1 != t2 ? 0 : dy0 > 2 * dx0 ? 0 : ((~PhaseWinding(c1) & 1) ^ (t1 ? 1 : 0)) + 1)}");
            if (t1 != t2) return 0;
            int dx = dx0, dy = dy0;
            if (dy > 2 * dx) return 0;
            return ((~PhaseWinding(c1) & 1) ^ (t1 ? 1 : 0)) + 1;
        }

        /// <summary>The per-contour byte GDI keeps at element+0x58. We have no such array, so it is
        /// recomputed from the signed area in font units.
        /// <para>WPF_CT_PHASE_WIND=1 IS THE DEFAULT, NOT THE FLIP -- the fallback below is
        /// `WPF_CT_PHASE != "0"`, which is true whenever the phase is on, so setting the knob to 1
        /// changes nothing at all. It is =0 that takes the other sense. The note here used to say
        /// the opposite and cost an afternoon: "WPF_CT_PHASE_WIND=1 moves A, X and K by exactly
        /// nothing" was recorded as evidence that the winding is not involved, and it was evidence
        /// that the knob was a no-op.</para>
        /// <para>AND THE SIGNED AREA IS NOT WHAT GDI'S BYTE HOLDS. The colour is
        /// ((~flag &amp; 1) ^ turn) + 1 and colour 1 is the whole pairing condition, so the two
        /// senses of this flag EXACTLY SWAP which links pair. GDI's own outline for Arial Bold 'A'
        /// at 20ppem -- recovered exactly, see the remark at ApplyPhase -- has p0/p1 shifted -69
        /// and -70 and p4/p5 +5 and -5, so neither of those is a pair, while we pair both; that
        /// wants flag=1 on its outer contour. Setting it measures 6,767 -> 7,041 on that glyph and
        /// 922 -> 5,242 on 'K', whose single contour is the outer contour of a capital in the same
        /// face at the same size and ought to answer the same way. So whatever element+0x58 is, it
        /// is not the contour's winding as a signed area computes it.</para></summary>
        private int PhaseWinding(int c)
        {
            if (_contourWind == null || _contourWind.Length < _contourCount)
                _contourWind = new sbyte[Math.Max(8, _contourCount)];
            if (_contourWindGlyph == _phaseGlyphStamp && _contourWind[c] >= 0) return _contourWind[c];
            if (_contourWindGlyph != _phaseGlyphStamp)
            {
                for (int i = 0; i < _contourWind.Length; i++) _contourWind[i] = -1;
                _contourWindGlyph = _phaseGlyphStamp;
            }
            int end = _glyphZone.Contours[c];
            int start = c == 0 ? 0 : _glyphZone.Contours[c - 1] + 1;
            long area = 0;
            for (int i = start; i <= end && i < _glyphZone.OrusX.Length; i++)
            {
                int j = i == end ? start : i + 1;
                area += (long) _glyphZone.OrusX[i] * _glyphZone.OrusY[j]
                      - (long) _glyphZone.OrusX[j] * _glyphZone.OrusY[i];
            }
            // The area's sense: negative area (TrueType's clockwise outer, y up) is 0. The old
            // WPF_CT_PHASE_WIND=0 flip only applies in the legacy "area" mode -- s_phaseWindFlip
            // reads the same variable, and a mode name must not flip the sense as a side effect.
            bool legacy = s_phaseWindMode == 0 && s_phaseWindConst < 0;
            int areaSign = (area < 0) == !(legacy ? s_phaseWindFlip : true) ? 1 : 0;
            int w = s_phaseWindMode == 1 ? PhaseNesting(c)
                  : s_phaseWindMode == 2 ? (areaSign ^ PhaseNesting(c))
                  : s_phaseWindConst >= 0 ? s_phaseWindConst : areaSign;
            if (s_phaseDump)
                Console.Error.WriteLine($"  WIND c={c} area={area} areaSign={areaSign} nest={PhaseNesting(c)} mode={s_phaseWindMode} const={s_phaseWindConst} -> {w}");
            _contourWind[c] = (sbyte) w;
            return w;
        }

        /// <summary>T when the point is x-touched, for the PHASEDUMP trace.</summary>
        private string TouchMark(int k) =>
            k < _realPoints && (_glyphZone.Tags[k] & TagTouchX) != 0 ? "T" : ".";

        /// <summary>WPF_CT_PHASE_WIND=const1 / const0: every contour's flag is that constant, as
        /// fs__Contour's fill at 140024740 would give if it writes w26 = 1 for all of them.</summary>
        private static readonly int s_phaseWindConst =
            Environment.GetEnvironmentVariable("WPF_CT_PHASE_WIND") switch { "const1" => 1, "const0" => 0, _ => -1 };

        /// <summary>WPF_CT_PHASE_WIND=nest: the flag is the contour's NESTING parity -- 1 for a hole,
        /// 0 for an outer contour -- which is what fsg_CheckOutlineOrientation@14002c690 computes.
        /// It zeroes every contour's byte (14002c6cc), skips contours of two points or fewer, finds
        /// the four extreme points, and calls 14002bcb0 -- which walks EVERY contour of the element
        /// from that extreme point -- for directions 0 and 2, using 1 or 3 only when those two
        /// disagree, and sets bit 0 when the answer is 1 (14002cefc..cf08). A containment test at
        /// an extreme point is orientation-independent, which is why the signed-area sign was wrong
        /// on faces whose outer contours wind the other way, and why "always 0" was nearly right:
        /// only holes carry a 1, and holes carry few phase links.</summary>
        /// <summary>WPF_CT_PHASE_WIND=nest: nesting parity alone. =xor: the contour's winding XOR its
        /// nesting parity -- 1 only when a contour winds the wrong way for its depth, which is what
        /// an outline ORIENTATION CHECK reports, and 0 on every contour of a well-formed face.
        /// Measured: nest equals the signed area on this corpus (380,873, both mark holes 1);
        /// "always 0" is better (376,284), so GDI's holes carry 0; xor gives 0 everywhere here.</summary>
        private static readonly int s_phaseWindMode =
            Environment.GetEnvironmentVariable("WPF_CT_PHASE_WIND") switch { "nest" => 1, "area" or "0" or "1" or "const0" or "const1" => 0, _ => 2 };

        private int PhaseNesting(int c)
        {
            int[] xs = _glyphZone.OrusX, ys = _glyphZone.OrusY;
            if (c < 0 || c >= _contourCount || c >= _glyphZone.Contours.Length) return 0;
            int end = _glyphZone.Contours[c], start = c == 0 ? 0 : _glyphZone.Contours[c - 1] + 1;
            if (end - start + 1 <= 2 || end >= xs.Length) return 0;
            int iMinX = start, iMaxX = start, iMinY = start, iMaxY = start;
            for (int i = start; i <= end; i++)
            {
                if (xs[i] < xs[iMinX]) iMinX = i; if (xs[i] > xs[iMaxX]) iMaxX = i;
                if (ys[i] < ys[iMinY]) iMinY = i; if (ys[i] > ys[iMaxY]) iMaxY = i;
            }
            int a = NestParity(c, iMinX), b = NestParity(c, iMaxX);
            if (a >= 0 && a == b) return a;
            int d = NestParity(c, iMinY); if (d >= 0) return d;
            int e = NestParity(c, iMaxY); if (e >= 0) return e;
            return a >= 0 ? a : b >= 0 ? b : 0;
        }

        /// <summary>Even-odd containment of point pi inside every contour other than c, by a ray to
        /// +x in design units; -1 when the ray meets a vertex or edge exactly (ambiguous).</summary>
        private int NestParity(int c, int pi)
        {
            int[] xs = _glyphZone.OrusX, ys = _glyphZone.OrusY;
            long px = xs[pi], py = ys[pi];
            int crossings = 0, s0 = 0;
            for (int o = 0; o < _contourCount && o < _glyphZone.Contours.Length; o++)
            {
                int oEnd = _glyphZone.Contours[o], oStart = s0; s0 = oEnd + 1;
                if (o == c || oEnd < oStart || oEnd >= xs.Length) continue;
                for (int i = oStart; i <= oEnd; i++)
                {
                    int j = i == oEnd ? oStart : i + 1;
                    long x1 = xs[i], y1 = ys[i], x2 = xs[j], y2 = ys[j];
                    if ((y1 > py) == (y2 > py)) continue;
                    long num = (py - y1) * (x2 - x1), den = y2 - y1;
                    long xint = x1 + num / den;
                    if (xint == px && num % den == 0) return -1;
                    if (xint > px) crossings++;
                }
            }
            return crossings & 1;
        }

        private static readonly bool s_phasePhantomMate =
            Environment.GetEnvironmentVariable("WPF_CT_PHASE_PHANTOM_MATE") != "0";

        private sbyte[] _contourWind;
        private int _contourWindGlyph = -1, _phaseGlyphStamp;

        /// <summary>WPF_CT_PHASE_INTERALIGN=0 drops MDRP/ALIGNRP's proportion branch.</summary>
        private static readonly bool s_phaseInterAlign =
            Environment.GetEnvironmentVariable("WPF_CT_PHASE_INTERALIGN") != "0";

        /// <summary>WPF_CT_PHASE_TWICE=1: let the Execute-end phase run even after IUP's.
        /// <para>MEASURED AND CATASTROPHIC -- Times 'A'@14 1,282 -> 9,448, Arial Bold 'K'@20
        /// 922 -> 4,785 -- and the useful part is WHY, because GDI really does make the second
        /// call. ExecutePhaseControl@140035970 is a byte-for-byte copy of itrp_IUP's phase block
        /// (the same lastContourEnd+5 scan for a node with flags bit 0, the same glyph-wide
        /// boolean, the same PhaseShift over every point) and it neither reads nor writes
        /// elem[0x60] -- that latch stops only IUP's own copy running twice. itrp_Execute calls it
        /// at 140037314, after IUP has run. So on a glyph with IUP[x] the phase IS applied twice.
        /// <para>Since applying OURS twice doubles every shift, GDI's PhaseShift must be
        /// IDEMPOTENT: it assigns the node's value onto a stored base rather than accumulating a
        /// delta onto whatever cur happens to hold. That is a property to check at PhaseShift's
        /// coordinate store, and it matters beyond tidiness -- an idempotent second pass would
        /// re-apply the phase to points IUP had just MOVED, which is the one mechanism that can
        /// give an interpolated point a shift its interpolation did not produce.</para></summary>
        private static readonly bool s_phaseTwice =
            Environment.GetEnvironmentVariable("WPF_CT_PHASE_TWICE") == "1";

        /// <summary>WPF_CT_PHASE_ATEXEC=0 keeps the phase on IUP alone.
        /// <para>KEEP IT ON: =0 measures 598,774 -> 784,675 on the 8..24 holdout. The binary has
        /// EXACTLY TWO appliers -- the only external callers of PhaseShift@140035b80 are
        /// itrp_IUP@14003925c and ExecutePhaseControl@1400359e8 -- and ExecutePhaseControl is in
        /// turn called from just itrp_Execute@140037314 and itrp_SHC@14003e138. So the three sites
        /// we have (this one, ApplyPhaseControl, and s_phaseAtShc) are the three GDI has, and a
        /// glyph that never runs IUP[x] is phased by the Execute call, not left unphased. The
        /// 186k is what that call is worth.</para></summary>
        private static readonly bool s_phaseAtExecute =
            Environment.GetEnvironmentVariable("WPF_CT_PHASE_ATEXEC") != "0";

        /// <summary>WPF_CT_LSBROUND=1: round the LSB phantom and translate, as GDI does.</summary>
        private static readonly bool s_yPhantomRound =
            Environment.GetEnvironmentVariable("WPF_CT_YPHANTOM") != "0";

        private static readonly bool s_lsbPhantoms =
            Environment.GetEnvironmentVariable("WPF_CT_LSB_PHANTOM") != "0";

        /// <summary>scl_AdjustOldCharSideBearing@1801da5f0, which is how GDI puts the left
        /// phantom on its grid: it rounds `orgX[pp1]` -- to a SIXTEENTH under ClearType, to a whole
        /// pixel otherwise -- and SHIFTS EVERY POINT of the glyph, phantoms included, by the same
        /// delta, so org and cur agree and the outline keeps its place inside the advance box.
        /// That is the only place GDI rounds pp1.
        /// <para>We used to round pp1's cur IN PLACE and leave the outline where it was, which
        /// slides the outline inside the box and leaves org and cur disagreeing by the delta.
        /// Together with the sixteenth grid this is worth 172,715 -> 160,147.
        /// WPF_CT_LSBROUND=0 goes back to the in-place rounding (with
        /// WPF_CT_PP1_SIXTEENTH=2, which is what it needs to be then -- 165,670; the two
        /// together, which rounds pp1 twice, is 189,708).</para></summary>
        /// <summary>WPF_CT_LSBROUND: 0 off everywhere, both in both passes, anything else (the
        /// default) only in the ClearType pass.
        /// <para>THE BI-LEVEL PASS DOES NOT DO IT, and GDI's own fitted points say so without
        /// room for argument. The shift is applied BEFORE the program runs, so it does not survive
        /// as a translation: a point the program ROUNDS lands on the same absolute grid whichever
        /// frame it started in, and only an UNTOUCHED point carries the shift. GGO's bi-level
        /// points for Times New Roman Italic 'j' agree with ours exactly on the twenty points its
        /// program pins and are 2/64 away on the twenty-nine it does not -- which is the signature
        /// of a shift we applied and GDI did not. 'c' and 'y', whose programs pin nothing in x,
        /// are out by 2/64 on every single point at 12-18ppem and 3/64 at 20-24, exactly the
        /// side-bearing delta (their hmtx lsb is four font units off their glyf xMin, and
        /// round(4 * ppem / 32) is 2 up to 18 and 3 from 20).</para>
        /// <para>Over the whole sweep -- six faces, three styles, 8..24ppem, 306 combinations --
        /// this is the ONLY interpreter disagreement of any size: 1,683 x points, all of it Times
        /// New Roman Italic's c, j and y. Gating it takes the face from 41 of 44 glyphs exact to
        /// 43, and 129 differing points to 2.</para>
        /// <para>It stays ON for the ClearType pass, where the pixels demand it: WPF_CT_LSBROUND=0
        /// measures 145,276 against 139,753. And gating it is EXACTLY NEUTRAL on those pixels --
        /// 139,753 either way -- so nothing is being fitted here; the holdout cannot see the
        /// bi-level frame at all, and the oracle can see nothing else.</para></summary>
        private static readonly string s_lsbRoundMode =
            Environment.GetEnvironmentVariable("WPF_CT_LSBROUND") ?? "";

        private static bool LsbRoundHere =>
            s_lsbRoundMode != "0" && (s_lsbRoundMode == "both" || !BiLevelPass);

        /// <summary>Is the ClearType x grid in force for THIS run?</summary>
        private static bool SubpixelFittingHere =>
            TrueTypeFont.SubpixelFitting && !BiLevelPass;

        /// <summary>Whether the phase also runs when the compatible advance is SMALLER than the
        /// linear one. WPF_CT_PHASE_NEG=1.</summary>
        /// <summary>Apply the compatible-width correction as a plain multiplicative scale about
        /// x=0 instead of the phase tree. WPF_CT_PHASE_SCALE=1.</summary>
        private static readonly bool s_phaseScale =
            Environment.GetEnvironmentVariable("WPF_CT_PHASE_SCALE") == "1";

        private static readonly bool s_phaseSkipShrinking =
            Environment.GetEnvironmentVariable("WPF_CT_PHASE_NOSHRINK") == "1";

        private static readonly bool s_storeProbe =
            Environment.GetEnvironmentVariable("WPF_STORE_PROBE") == "1";

        private static readonly bool s_phaseEntryProbe =
            Environment.GetEnvironmentVariable("WPF_PHASE_ENTRY") == "1";

        private static readonly bool s_phaseDump =
            Environment.GetEnvironmentVariable("WPF_CT_PHASE_DUMP") == "1";

        private static readonly bool s_phaseAtShc =
            Environment.GetEnvironmentVariable("WPF_CT_PHASE_SHC") != "0";

        private static readonly bool s_phaseTrace =
            Environment.GetEnvironmentVariable("WPF_CT_PHASE_TRACE") == "1";

        private static readonly bool s_phaseWindFlip =
            Environment.GetEnvironmentVariable("WPF_CT_PHASE_WIND") is { } pw ? pw == "1"
            // The sense that makes a stem's two sides one PAIR. Backwards they never pair and
            // each side takes its own phase: Segoe UI 'H'@12 came out 84 and 82 sixty-fourths
            // wide against GDI's 75 and 75.
            : Environment.GetEnvironmentVariable("WPF_CT_PHASE") != "0";

        /// <summary>WPF_CT_PHASE_GDIPAIR=0 goes back to guessing partners from our own link list
        /// in BuildPhasePartners instead of taking them from AddDistance's param_5.</summary>
        /// <summary>WPF_CT_PHASE_PROPCOMP=1: record a proportion link only inside a COMPONENT, as
        /// itrp_MDRP's first gate does. See the comment at the call.</summary>
        private static readonly bool s_phasePropComponent =
            Environment.GetEnvironmentVariable("WPF_CT_PHASE_PROPCOMP") == "1";

        private static readonly bool s_phaseGdiPairs =
            Environment.GetEnvironmentVariable("WPF_CT_PHASE_GDIPAIR") != "0";

        private int[] _phasePartner = new int[128];

        /// <summary>ExecutePhaseControl's param_3: whether ANY point in this glyph was flagged as
        /// closing a cycle. PhaseShift consults it for a point with no parent -- such a point takes
        /// the direct x*(ctFactor-1) when it is set and NOTHING when it is clear.</summary>
        private bool _phaseAnyCycle;
        private byte[] _phaseFlags = new byte[128];   // 1 = cycle, 2 = done, 4 = on the stack
        private int _ctFactor16;                      // GS+0x1d0: FixDiv(device, linear), 16.16

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
                // PhaseShift gives it 0 when param_3 is clear and the same direct x*(ctFactor-1)
                // the phantoms take when it is set (its CompDiv branch reduces to that). param_3 is
                // "did any point close a cycle", which ExecutePhaseControl computes for the glyph.
                phase = (s_phaseRootFromCycle ? _phaseAnyCycle : s_phaseRootDirect)
                    ? (int) MathF.Round(_glyphZone.CurX[p] * _ctFrac) : 0;
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
        /// <summary>The two-parent rule, inlined in PhaseShift rather than a function of its own.
        /// <para>IT TRUNCATES, and that is read off the instructions, not the decompiler: the
        /// sequence at 140035d88 is `sub / mul / sub / sub / madd` and then a bare
        /// `sdiv w0,w9,w8` at 140035da4 with NO rounding term added first. Worth pinning because
        /// it became load-bearing. Segoe UI's 'o' at 12ppem is one anchor out by one sixty-fourth
        /// -- GDI wants P9 at 408 where we give 409 -- and the whole of its chain is this rule:
        /// P25 is the right phantom and takes -2; P3 is a root paired with P15 and takes 0; P21
        /// interpolates between them by org position, `((351-36)*-2 + (450-351)*0) / (450-36)`
        /// = -630/414 = -1.52, which truncates to -1; and P9 inherits that through the pair. A
        /// rule that ROUNDED would give -2 for that node -- which is exactly the shape of a wrong
        /// answer one is tempted to ship, so it was measured as well as read.</para>
        /// <para>AND IT IS WRONG TWICE OVER. Rounding does not even fix the glyph that suggested
        /// it: Segoe UI 'o'@12 goes 137 -> 236, because the rule runs on every two-parent node in
        /// the glyph and the other nodes move the wrong way. Across the holdout it is
        /// catastrophic, 605,280 -> 1,220,769. WPF_CT_PHASE_AVGROUND=1 keeps the diagnostic. The
        /// lesson is the general one: a per-node inference about a rule that runs on EVERY node
        /// is not a prediction about the glyph until it has been rendered.</para>
        /// <para>So the divergence on that glyph is in an INPUT to the rule, not the rule: one of
        /// the two parent shifts, or one of the three org positions. Neither parent explains it on
        /// its own arithmetic -- P25 would have to be -3, which needs its cur at 675 against the
        /// 452 it has, and P3 would have to be -2, which needs its pair sum at 900 against 170 --
        /// and no value of the phase factor fixes this glyph either. Its program placement is
        /// exact: P9 is MIRP'd to 404 and then SHPIXed by 6 under a literal `MPPEM == 12` guard,
        /// a hand-tuned delta with no rounding in it. What is left is the TREE.</para></summary>
        private int CalcAvgXPhase(int a, int p, int b, int phA, int phB)
        {
            // +0x10 in CalcAvgXPhaseShift's element struct is OrgX. The phase MAGNITUDES come
            // from CurX, but the ratio that mixes them is taken on the unfitted outline.
            // <para>VERIFIED AT INSTRUCTION LEVEL @140035d60, which is the whole rule in twelve
            // instructions: load org[parentA], org[parentB] and org[p] from elem+0x10; if
            // org[parentA] < org[parentB] take A as the low end and B as the high, else the
            // mirrored branch; then
            //     w9 = (orgHi - org[p]) * shiftLo + (org[p] - orgLo) * shiftHi
            //     w0 = w9 / (orgHi - orgLo)                     -- bare sdiv, TRUNCATING
            // and nothing else. A zero denominator brk's there rather than averaging.</para>
            // <para>Checked numerically as well as structurally, on Arial Bold 'X' at 20ppem.
            // Its p10 has parents 6 and 11 with org 824 and 222 against its own 426, and our
            // phases -18 and -3: ((426-222)*-18 + (824-426)*-3) / 602 = -8.08 -> -8, which is
            // exactly what the phase dump shows. Feed the SAME formula the shifts GDI's own
            // outline implies for those two parents (-16 and +1) and it returns -4, which is
            // exactly what GDI's outline implies for p10. So the rule is right and the error it
            // carries is inherited: what differs is the parents' shifts, not the mixing.</para>
            int xa = _glyphZone.OrgX[a], xb = _glyphZone.OrgX[b], xp = _glyphZone.OrgX[p];
            int lo = Math.Min(xa, xb), hi = Math.Max(xa, xb);
            if (xa >= xb) (phA, phB) = (phB, phA);   // GDI orders by x, swapping the phases
            if (lo == hi) return (phA + phB) / 2;
            long num = (long) (xp - lo) * phB + (long) (hi - xp) * phA, den = hi - lo;
            // WPF_CT_PHASE_AVGROUND=1: round instead of truncating. A DIAGNOSTIC, not a candidate
            // -- the binary plainly truncates (bare sdiv, see the summary above) -- kept because it
            // measures whether "one truncation away from GDI" is systematic across the pool or a
            // property of the one glyph that suggested it.
            if (s_phaseAvgRound)
                return (int) ((num + (num < 0 ? -den / 2 : den / 2)) / den);
            return (int) (num / den);
        }

        /// <summary>Whether the PAIR rule is restricted to links GDI would call a stem.
        /// <para>DoubleCheckLinkColor@1400357e8 READ IN FULL, and it is two tests, not one. Given
        /// (elem, p1, p2) it walks the contour-end array for the contour holding each point and
        /// returns false if they differ; then it finds p1's neighbours on that contour (with the
        /// wrap handled off elem+0x38/0x40) and returns false unless p2 IS one of them. So the
        /// first test is ADJACENCY ON ONE CONTOUR. The second is a TURN: for each of the two
        /// points it forms the cross product of the incoming and outgoing segments out of the
        /// FONT-UNIT arrays (elem+0x20 and +0x28) as
        ///     (orusX[p] - orusX[prev]) * (orusY[next] - orusY[p])   vs
        ///     (orusY[p] - orusY[prev]) * (orusX[next] - orusX[p])
        /// keeps only the sign of the comparison, and returns 0 if the two signs differ -- the two
        /// ends of a stem must turn the same way. Then a 2:1 test: |dy| > 2*|dx| in font units
        /// also returns 0. (An earlier note here said the 2:1 test was NOT in the binary; it is,
        /// at 140035938, and that claim was made off a dump that stopped sixteen instructions
        /// short of it.)</para>
        /// <para>AND IT IS NOT A PREDICATE -- it returns the COLOUR. itrp_MIRP calls it as
        /// DoubleCheckLinkColor(elem, r, p, distanceType &amp; 3) and puts the result straight into
        /// the register the inlined AddDistance pairs on (`cmp w5,#1`). Three outcomes: the input
        /// colour unchanged when the two points are on different contours or are not adjacent;
        /// ZERO when they are adjacent but turn opposite ways, are steeper than 2:1, or sit on an
        /// invalid contour; otherwise a colour recomputed from the geometry as
        ///     ((~contourFlag[c] &amp; 1) ^ turnSign) + 1        -- so 1 (black) or 2 (white)
        /// where contourFlag is the per-contour byte at element+0x58. PhaseLinkColour below is
        /// that, case for case.</para> That IS
        /// GDI's predicate, but it is applied to OUR link set, which is not GDI's, and it measures
        /// worse for that reason: with the faithful tree, 6,654,259 filtered against 5,841,656
        /// unfiltered.
        /// <para>SHIPPED 2026-09-14, now that it is EXACTLY NEUTRAL: 605,280 either way on the
        /// 8..24 holdout, where it used to cost 800,000. The note said "off until the links
        /// themselves are GDI's", and enough has been fixed upstream since that the predicate no
        /// longer rejects anything this specimen produces. Neutral and GDI's beats neutral and
        /// invented, so it goes on -- but do NOT re-measure it looking for a win, and do not read
        /// the neutrality as confirmation: it means the case does not arise here.</para>
        /// <para>WPF_CT_PHASE_ADJ=0 turns it off.</para></summary>
        /// <summary>WPF_CT_PHASE_AVGROUND=1 -- diagnostic only; see CalcAvgXPhase.</summary>
        private static readonly bool s_phaseAvgRound =
            Environment.GetEnvironmentVariable("WPF_CT_PHASE_AVGROUND") == "1";

        /// <summary>WPF_CT_PHASE_PROPPHANTOM=0 -- probe; see PhaseProportion.</summary>
        private static readonly bool s_phasePropPhantom =
            Environment.GetEnvironmentVariable("WPF_CT_PHASE_PROPPHANTOM") != "0";

        private static readonly bool s_phasePairAdjacent =
            Environment.GetEnvironmentVariable("WPF_CT_PHASE_ADJ") != "0";

        private static readonly bool s_phasePairs =
            Environment.GetEnvironmentVariable("WPF_CT_PHASE_PAIRS") != "0";

        /// <summary>Whether a rootless point's phase follows ExecutePhaseControl's ACTUAL param_3
        /// ("did any point close a cycle") rather than the blanket s_phaseRootDirect.
        /// <para>This is GDI's rule, and it used to measure worse -- 5,936,734 against 5,841,656
        /// -- because the flag is computed from OUR tree and our links were not GDI's. The note
        /// here said to turn it on once the link set was right. It is: DoubleCheckLinkColor
        /// decides the pairs now and its winding is the right way round. Turning it on is worth
        /// 4,813,476 -> 4,368,478 on the specimen and 17,331,018 -> 15,662,723 on the holdout,
        /// with 112 ratchets improved against 19 worse.</para></summary>
        /// <summary>WPF_CT_PHASE_XONLY=0 records the phase tree for every link, as we used to.
        /// GDI records only on the ClearType x axis (localGS+0xcc at every call site).</summary>
        /// <summary>WPF_CT_PHASE_AXIS=exact records the phase tree only where localGS+0xcc is
        /// set -- an axis-EXACT projection, which is what AddDistance and AddProportion really
        /// test. GDI's rule, and it measures WORSE: 4,067,369 against 3,605,604 for recording
        /// on any horizontal-ish projection. Recording the DIAGONAL links GDI leaves out is
        /// worth 462k to us, so something upstream of the tree differs. See the census note on
        /// OnClearTypeAxis.</summary>
        /// <summary>Why the phase axis gate said no, for the GATE-DROP trace.</summary>
        private string GateWhy() =>
            $"ctInfo={ClearTypeInfo} notPureY={NotPureYProjection} pv=({_gs.ProjX},{_gs.ProjY}) "
            + $"prepOk={CtRoundingInPrep || !_inPreProgram} subpixel={SubpixelGridHere}";

        private static readonly bool s_phaseAxisExact =
            Environment.GetEnvironmentVariable("WPF_CT_PHASE_AXIS") == "exact";

        private static readonly bool s_phaseXAxisOnly =
            Environment.GetEnvironmentVariable("WPF_CT_PHASE_XONLY") != "0";

        /// <summary>WPF_CT_PHASE_FAITHFUL=0 goes back to the pure-function PhaseOf.</summary>
        private static readonly bool s_phaseFaithful =
            Environment.GetEnvironmentVariable("WPF_CT_PHASE_FAITHFUL") != "0";

        private static readonly bool s_phaseRootFromCycle =
            Environment.GetEnvironmentVariable("WPF_CT_PHASE_ROOTCYCLE") is { } rc ? rc == "1"
            : Environment.GetEnvironmentVariable("WPF_CT_PHASE") != "0";

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
        {
            get
            {
                bool loose = (_gs.ProjX < 0 ? -_gs.ProjX : _gs.ProjX)
                           > (_gs.ProjY < 0 ? -_gs.ProjY : _gs.ProjY);
                if (s_projCensus && loose && !_inPreProgram)
                {
                    var key = (_gs.ProjX, _gs.ProjY);
                    lock (s_projSeen)
                    {
                        if (!s_censusHooked)
                        {
                            s_censusHooked = true;
                            AppDomain.CurrentDomain.ProcessExit += (_, _) => DumpProjCensus();
                        }
                        s_projSeen.TryGetValue(key, out long n); s_projSeen[key] = n + 1;
                        if (++s_censusCount == 5000) DumpProjCensus();
                    }
                }
                return loose;
            }
        }

        /// <summary>localGS+0xcc exactly: itrp_SVTCA_1 writes 1 only when ClearType is on with
        /// its axis on x, and SPVTL/SDPVTL only when the vector is EXACTLY (0x4000, 0). The
        /// AddDistance and AddProportion call sites test this slot, not "mostly horizontal".
        /// <para>Census (WPF_PROJ_CENSUS=1): of 159,581 horizontal-ish projections in the weight
        /// run, 139,660 are axis-EXACT and the rest are real diagonals about 12 degrees off.
        /// </para></summary>
        internal bool OnClearTypeAxis =>
            ClearTypeInfo && (s_ctInPrep || !_inPreProgram)
            && (s_ctAxisLatched ? _ctAxisFlag : _gs.ProjX == 0x4000 && _gs.ProjY == 0) && SubpixelGridHere;

        /// <summary>localGS+0xcc is LATCHED, not recomputed. Only five handlers write it --
        /// itrp_SVTCA_0/_1, itrp_SPVTCA_0/_1, itrp_SPVTL and itrp_SDPVTL -- and SPVFS is NOT
        /// among them, so setting the projection vector from the stack leaves the flag saying
        /// whatever the last of those said. Recomputing it from the current vector, as we did,
        /// is therefore not the same predicate at all.</summary>
        private bool _ctAxisFlag;

        internal void LatchClearTypeAxis()
        {
            _ctAxisFlag = ClearTypeInfo && _gs.ProjX == 0x4000 && _gs.ProjY == 0;
            _ctDirFlag = NotPureYProjection;
        }

        /// <summary>The LATCHED "not a pure +Y projection" answer -- gs+0xcc, kept the way GDI
        /// keeps it rather than recomputed from the current vector.
        /// <para>THE NAME IS RIGHT, and here is the whole predicate, because two passes at the
        /// binary reached different answers. `itrp_SDPVTL` computes it from the ACTUAL VECTOR:
        /// with ClearType on (globals[0x1c0] bit 0) and x the oversampled axis (bit 2 clear) it
        /// writes 0 only when the projection is pure +Y, and 1 otherwise -- which is this field's
        /// name exactly. `itrp_SVTCA_1` (SVTCA[**x**]: it installs itrp_XMovePoint) and
        /// `itrp_SVTCA_0` (SVTCA[y]) compute the same answer from the FLAGS instead, because there
        /// the axis is known a priori: SVTCA[x] writes `bit0 &amp;&amp; !bit2`, SVTCA[y] writes
        /// `bit0 &amp;&amp; bit2`. So bit 2 of 0x1c0 says which axis ClearType oversamples, the field is
        /// zero on both axes when ClearType is off, and for every case that arises our recomputed
        /// predicate and GDI's latched one agree. gs+0xcc is also what selects the sixteenth
        /// rounding functions: see the rounding-function note below.</para>
        /// <para>`itrp_SDPVTL` writes gs+0xcc, and so do SVTCA_0/_1 and SPVTCA_0/_1 and SPVTL --
        /// five handlers, and that is ALL of them. **SPVFS and SFVFS are not among them.** A face
        /// that reads the projection vector with GPV, does arithmetic on it and sets the vectors
        /// back from the stack therefore leaves the latch saying whatever the last of those five
        /// said, while we recomputed it from the diagonal vector and switched into the ClearType
        /// rules GDI was not using.</para>
        /// <para>This is exactly the split between the italic faces that work and the one that does
        /// not. Times New Roman Italic sets its stem-perpendicular vector through GPV/SPVFS with the
        /// FREEDOM vector equal to it, and the glyphs that do so are precisely the ones that are
        /// wrong -- 'l' 51 such instructions, 'd' 51, 'n' 58, all needing a quarter to half a pixel
        /// of shift, against 'o', 'e' and 's' with NONE and already exact. Verdana, Arial and Segoe
        /// UI italic use SDPVTL/SPVTL, which do latch, and their 'l' at 12ppem is pixel-exact.</para>
        /// <para>WPF_CT_DIR_LATCH=0 goes back to recomputing.</para></summary>
        private bool _ctDirFlag;

        /// <para>MEASURED AND WORSE, and kept because the divergence is real. Latching costs
        /// 1,539,711 -> 2,248,675 on the specimen. All six handlers GDI writes gs+0xcc from are
        /// covered (SVTCA, SPVTCA, SPVTL, SDPVTL) and the predicate matches `itrp_SDPVTL`, so this
        /// is not an incomplete port: every other rule keyed off InClearTypeDirection was tuned
        /// against the RECOMPUTED predicate, and swapping it moves all of them at once. Times
        /// Italic's own 'l' does not even change (5,483 either way) because the last latching
        /// instruction before its GPV/SPVFS block is SVTCA[x], which sets the latch to the same 1
        /// we compute. So the latch is NOT the italic shear either.</para>
        private static readonly bool s_ctDirLatched =
            Environment.GetEnvironmentVariable("WPF_CT_DIR_LATCH") == "1";

        private static readonly bool s_ctAxisLatched =
            Environment.GetEnvironmentVariable("WPF_CT_AXIS_LATCH") != "0";

        private static readonly bool s_projCensus =
            Environment.GetEnvironmentVariable("WPF_PROJ_CENSUS") == "1";

        private static bool s_censusHooked;
        private static long s_censusCount;

        internal static readonly System.Collections.Generic.Dictionary<(int, int), long> s_projSeen = new();

        internal static void DumpProjCensus()
        {
            if (!s_projCensus) return;
            lock (s_projSeen)
            {
                long total = 0, exact = 0;
                foreach (var kv in s_projSeen)
                {
                    total += kv.Value;
                    if (kv.Key.Item1 == 0x4000 && kv.Key.Item2 == 0) exact += kv.Value;
                }
                Console.Error.WriteLine($"PROJCENSUS total={total} exact={exact} distinct={s_projSeen.Count}");
                var top = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<(int, int), long>>(s_projSeen);
                top.Sort((x, y) => y.Value.CompareTo(x.Value));
                for (int i = 0; i < top.Count && i < 12; i++)
                    Console.Error.WriteLine($"PROJ ({top[i].Key.Item1},{top[i].Key.Item2}) {top[i].Value}");
            }
        }

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
            ClearTypeInfo && !_inPreProgram
            // itrp_DeltaEngine@180080d28 does not ask whether freedom is MOSTLY horizontal. On
            // the ClearType x axis it runs a delta only when the freedom vector is EXACTLY
            //     (0, 0x4000)  -- pure, positive y
            // and skips everything else, so a diagonal freedom and a pure NEGATIVE y both go
            // where our |x| > |y| test kept them. WPF_CT_DELTA_FREE=loose restores it.
            && (s_deltaFreeExact ? !(_gs.FreeX == 0 && _gs.FreeY == 0x4000) : IsHorizontalFreedom);

        private static readonly bool s_deltaFreeExact =
            Environment.GetEnvironmentVariable("WPF_CT_DELTA_FREE") != "loose";

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
                    zone.CurX[point] += FreedomStep(distance, _gs.FreeX);
                    if (touch) zone.Tags[point] |= TagTouchX;
                }
                if (s_xTrace)
                    Console.Error.WriteLine("XMOVE pt=" + point + " d=" + distance
                        + " fx=" + _gs.FreeX + " fy=" + _gs.FreeY + " pf=" + _dotProduct
                        + " step=" + FreedomStep(distance, _gs.FreeX)
                        + (XSuppress ? " SUPPRESSED" : " x=" + zone.CurX[point])
                        + (touch ? " touch" : ""));
            }
            if (_gs.FreeY != 0)
            {
                // A DIAGONAL FREEDOM VECTOR PERTURBS Y, WHICH CLEARTYPE HAS ALREADY GRID-FIT.
                // Times New Roman Italic is the only specimen face that moves points ALONG a
                // diagonal (pv == fv, set through GPV/SPVFS), and it is the only italic face whose
                // 'l' is not pixel-exact -- 51 such instructions in 'l', 51 in 'd', 58 in 'n',
                // against NONE in 'o', 'e' and 's', which are already right. WPF_CT_DIAG_YMOVE=0
                // asks whether GDI refuses the y half of such a move in the ClearType pass.
                // 0 refuses the y half outright; 2 refuses it only for a point the program has
                // ALREADY placed in y, on the reading that a diagonal move should not un-fit a
                // grid-fitted y. 0 measures 2,053,855 and is MIXED per glyph (Times Italic 'l'
                // 5,483 -> 4,698 and 'o' better, but 'n' 3,925 -> 4,456), which is what a rule that
                // is right but wrongly scoped looks like.
                bool refuse = s_diagYMove != 1 && !BiLevelPass && ClearTypeInfo && _gs.FreeX != 0
                              && (s_diagYMove == 0 || (zone.Tags[point] & TagTouchY) != 0);
                if (!refuse)
                {
                    zone.CurY[point] += FreedomStepY(distance, _gs.FreeY);
                    if (touch) zone.Tags[point] |= TagTouchY;
                }
                if (s_yTrace)
                    Console.Error.WriteLine("YMOVE pt=" + point + " d=" + distance
                        + " fx=" + _gs.FreeX + " fy=" + _gs.FreeY
                        + " pf=" + _dotProduct + " step=" + FreedomStepY(distance, _gs.FreeY)
                        + " exact=" + (_dotProduct == 0 ? 0
                            : (double) ((long) _gs.FreeY * distance) / _dotProduct).ToString("0.000")
                        + (refuse ? " REFUSED" : " y=" + zone.CurY[point]));
            }
        }

        /// <summary>WPF_YTRACE=1: every y move the program makes, so the ClearType pass and the
        /// bi-level pass can be diffed instruction for instruction.</summary>
        /// <summary>WPF_XTRACE=1: every x move the program makes, the twin of WPF_YTRACE. Added
        /// for Times New Roman Italic's descenders, the last glyphs whose bi-level fit disagrees
        /// with GDI's own.</summary>
        private static readonly bool s_xTrace =
            Environment.GetEnvironmentVariable("WPF_XTRACE") == "1";

        private static readonly bool s_yTrace =
            Environment.GetEnvironmentVariable("WPF_YTRACE") == "1";

        private static readonly int s_diagYMove =
            int.TryParse(Environment.GetEnvironmentVariable("WPF_CT_DIAG_YMOVE"), out int dy) ? dy : 1;

        /// <summary>How far one component moves when the point travels <paramref name="distance"/>
        /// along the freedom vector. itrp_MovePoint@180086260 does NOT use one formula:
        /// <code>
        ///   dot == 0x4000 :  ((f * d) >> 13) + 1 >> 1     // shifts: half toward +infinity
        ///   otherwise     :  f == dot ? d : CompDiv(dot, f * d)   // half AWAY from zero
        /// </code>
        /// We used the second everywhere. The two agree except on an exact half with a negative
        /// product -- f*d = -8192 gives 0 by the shifts and -1 by ours -- which is a diagonal
        /// move, and diagonals are the worst glyphs left in the corpus.
        /// <para>WPF_CT_FREESTEP=muldiv restores the single formula.</para></summary>
        private int FreedomStep(int distance, int component)
        {
            if (!s_freeStepExact) return MulDiv(distance, component, _dotProduct);
            if (_dotProduct == 0x4000)
                return (int) (((((long) component * distance) >> 13) + 1) >> 1);
            if (component == _dotProduct) return distance;
            return MulDiv(distance, component, _dotProduct);
        }

        private static readonly bool s_freeStepExact =
            Environment.GetEnvironmentVariable("WPF_CT_FREESTEP") != "muldiv";

        /// <summary>The y half of itrp_MovePoint@14003bfa0 is NOT CompDiv. Read from the ARM64
        /// at 14003c028..14003c048: `w8 = pfProj/2` (truncating), then `num - w8` when pfProj is
        /// negative and `num + w8` otherwise -- half the divisor's MAGNITUDE added whatever the
        /// numerator's sign -- and one sdiv, which truncates toward zero. For a negative product
        /// with a positive dot product that lands one 64th ABOVE the symmetric rounding almost
        /// every time. The x half calls CompDiv, which is symmetric. WPF_CT_YMOVE_TRUNC=0 rounds
        /// y symmetrically as before.</summary>
        private int FreedomStepY(int distance, int component)
        {
            // IN BOTH PASSES, AND THAT WAS TESTED RATHER THAN ASSUMED. The twelve single y
            // points GDI's own bi-level fitted points still disagree on are ALL a diagonal
            // freedom vector on a diagonal glyph -- Arial 'X' at 13 and 16, Times Roman 'k' at 14
            // and 'x' at 19, Times Bold 'A' at 9 and 'k' at 21, Arial Italic 'R' at 18 -- and all
            // out by exactly 2/64. Arial 'X' at 13ppem is the worked case: point 6's last move is
            // distance -18 along a freedom vector of (-14049, 8429) with a projection dot of
            // 16383, so the exact step is -9.261; adding the divisor's magnitude unsigned and
            // truncating gives -8, rounding symmetrically gives -9, and GDI has -9.
            // <para>So gating this to the ClearType pass, the way the side-bearing snap had to be
            // gated, looks obvious and is WRONG. It makes Arial exact at 13 and 16 and breaks
            // Times: over the whole sweep of 306 combinations it takes the perfectly clean count
            // from 280 down to 219. WPF_CT_YMOVE_TRUNC=ct measures that again, =0 turns the form
            // off entirely.</para>
            // <para>AND THE ARITHMETIC IS NOT THE PROBLEM EITHER, because it is now read rather
            // than inferred. itrp_MovePoint@14003bfa0's y half is
            //     if (dot == freeY) y += distance;
            //     else { n = freeY * distance + (dot &lt; 0 ? -(dot/2) : dot/2); y += n / dot; }
            // -- the half carries the DOT's sign, not the numerator's, and the divide truncates
            // toward zero. That is exactly this function. For Arial 'X' point 6 it gives -8, which
            // is what we produce, so GDI reaches its 382 by a different ROUTE, not by rounding
            // this move differently. The projection dot is not it either:
            // itrp_ComputeAndCheck_PF_Proj@1400367f8 rounds the two products separately and adds,
            // `((fx*px + 0x2000) >> 14) + ((fy*py + 0x2000) >> 14)`, which is our ResetProjection
            // and which is where the 16383 comes from on both sides; its only adjustment snaps a
            // dot NEAR ZERO to +/-0x4000, and 16383 is nowhere near it.</para>
            // <para>So the twelve y points are a difference in WHICH INSTRUCTIONS RUN, not in what
            // any of them computes -- the touch-set frontier, now narrowed to seven glyphs.</para>
            // <para>RE-COUNTED 2026-09-20 over six faces x twelve sizes (9..18, 20, 24), the
            // repertoire a-z A K N R W X Y Z 0-9: SIXTY-EIGHT of the seventy-two face/size
            // combinations are bit-exact against GDI's own bi-level fitted points, and the four
            // that are not are ONE POINT EACH, all in y, all 2/64, all on a diagonal crossing --
            // Arial 'X' at 13 and 16, Times New Roman 'k' at 14 and 'x' at 24. Twelve down to
            // four. Nothing else in the corpus disagrees with GDI's own fit at all, which is what
            // makes the ClearType branch the only place the holdout can be coming from.</para>
            // <para>THE WHOLE DIAGONAL/ITALIC KNOB SET RE-SWEPT 2026-09-20 at a holdout of
            // 28,183, because italics are 46% of what is left (Times I 4,012 + Tahoma I 4,128 +
            // Consolas I 2,315 + Arial I 2,007 + Verdana I 604 = 13,066) and Arial's and
            // Consolas' italics are NINE TIMES their romans (2,007 against 229, 2,315 against
            // 256) although both are real italic files rather than synthesised obliques. Every
            // knob confirms what ships:
            // <code>
            //   WPF_CT_DIAG_YMOVE=0    3,127,669      WPF_CT_YMOVE_TRUNC=0      96,479
            //   WPF_CT_DIAG_YMOVE=2    2,367,744      WPF_CT_FREESTEP=muldiv    96,479
            //   WPF_OBLIQUE_ROUND=0       86,741      WPF_OBLIQUE_MATRIX=0      31,300
            //   WPF_CT_YMOVE_TRUNC=ct     28,183  -- INERT on the holdout now
            // </code>
            // The `=ct` row is the one worth reading twice: gating this to the ClearType pass
            // used to cost 61 clean rows (280 -> 219) and now changes the holdout by NOTHING, so
            // the bi-level pass's y rounding no longer reaches the shipped raster at all -- it
            // survives only in the advance, which is whole-pixel rounded either way. The clean-row
            // claim above is therefore about the BI-LEVEL ORACLE, not about pixels.</para>
            if (!s_freeStepExact || !s_yMoveTrunc
                || (BiLevelPass && s_yMoveTruncMode == "ct"))
                return FreedomStep(distance, component);
            if (_dotProduct == 0x4000)
                return (int) (((((long) component * distance) >> 13) + 1) >> 1);
            if (component == _dotProduct) return distance;
            long num = (long) component * distance;
            long half = Math.Abs(_dotProduct / 2);
            return (int) ((num + half) / _dotProduct);
        }

        private static readonly string s_yMoveTruncMode =
            Environment.GetEnvironmentVariable("WPF_CT_YMOVE_TRUNC") ?? "";

        private static readonly bool s_yMoveTrunc = s_yMoveTruncMode != "0";

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
        /// <summary>WPF_CT_SROUND_SCALE=0 restores the old behaviour: an SROUND period compared
        /// against a value already multiplied up for the finer ClearType grid.
        /// <para>AND THE BINARY'S OWN TABLE SAYS SCALING BOTH IS RIGHT (2026-09-20).
        /// itrp_SVTCA_1@14003f6f0 re-installs the round function on every axis change, from a
        /// sixteen-entry table at 0x14009b8c0 indexed by `roundState + (subpixel ? 8 : 0)` where
        /// subpixel is `globals[0x88] bit 2 || globals[0x16b] != 0`:
        /// <code>
        ///   0..7   ToDoubleGrid DownToGrid UpToGrid ToGrid ToHalfGrid Off SuperRound Super45Round
        ///   8..15  ...SP        ...SP      ...SP    ...SP  ...SP      ...SP SuperRound Super45Round
        /// </code>
        /// -- entries 14 and 15 are the SAME FUNCTIONS as 6 and 7. SuperRound and Super45Round
        /// have NO subpixel variant, so under ClearType they round exactly as they do in bi-level
        /// mode. Scaling the value and the period by the same factor and dividing back, which is
        /// what this does, is algebraically that: (4u)/(4p)*(4p)/4 = u/p*p. The table also
        /// confirms that the grid follows the CURRENT axis rather than being latched when RTG
        /// ran, which is how RoundDistance chooses it.</para></summary>
        private static readonly bool s_superRoundScaled =
            Environment.GetEnvironmentVariable("WPF_CT_SROUND_SCALE") != "0";

        /// <summary>ENGINE COMPENSATION. Every one of GDI's rounding functions takes it as its
        /// third argument and folds it in before the grid:
        /// <code>
        ///   itrp_RoundToGrid    v >= 0 ?  (v + c + 0x20) &amp; ~0x3f : -((c - v) + 0x20 &amp; ~0x3f)
        ///   itrp_RoundToGridSP  v >= 0 ?  (v + c/2 + 2)  &amp; ~3    : -((c/2 - v) + 2 &amp; ~3)
        /// </code>
        /// so the SUBPIXEL table halves it, exactly as it quarters the grid. The value comes
        /// from an array indexed by the DISTANCE TYPE -- grey, black, white -- which is the
        /// classic mechanism that makes a black distance round down and a white one round up.
        /// We had none at all -- and MEASURED, GDI's array must be zeros on this path, because
        /// any magnitude is monotonically worse: 0 gives 3,605,604, then 4 gives 6,957,809,
        /// 8 gives 7,912,782, 16 gives 10,633,377 and 32 gives 18,834,045. That matches what a
        /// modern antialiasing rasterizer does (FreeType zeroes its compensations too); the
        /// nonzero values belong to the bi-level engine. WPF_CT_ENGINE sets the magnitude in
        /// 64ths and defaults to 0, i.e. off.</summary>
        /// <summary>WPF_CT_ROUND_GRID=1: the bare ROUND[ab] OPCODE rounds on the whole pixel in
        /// the ClearType pass, while MIRP, MDRP, MDAP and MIAP stay on the sixteenth.
        /// <para>Measured because of what Arial's own program does with the answer. 'z' at 20ppem
        /// spends its whole 3,353 on the diagonal, and the diagonal's control value is computed by
        /// a function the glyph program SELECTS BY ARITHMETIC ON A ROUNDED NUMBER:
        /// <code>
        ///     ROUND[black](114);  -64;  MAX(_, 0);  /4096;  +44;  CALL
        /// </code>
        /// Rounded to the whole pixel 114 becomes 128 and the call is to function 45, which returns
        /// `MAX(|pv.x|,|pv.y|) * 64 / 8192 + 2` -- 128*cos + 2, a two-pixel diagonal weight, 93/64
        /// here. Rounded on the sixteenth it becomes 116 and the call is to function 44, which
        /// returns `MAX * 64 / 16384` -- 64*cos, ONE pixel, 47/64. A 12/64 difference in a rounding
        /// is amplified into a different formula for how heavy every diagonal in the face is.</para>
        /// <para>GDI draws the two-pixel one. Function 45 gives 1.45px perpendicular, which at this
        /// diagonal's cosine is 2.04px measured across a scan line, and GDI's own pixels measure
        /// 2.08px (6.24 lamps of coverage against our 8.70). Function 44 would be 1.0px, which is
        /// plainly not what GDI draws. So GDI took the whole-pixel branch inside its ClearType
        /// pass.</para>
        /// <para>The binary does NOT license a ROUND-only rule: `itrp_ROUND` calls the same
        /// `globals+0x90` pointer MIRP's general path calls, so in fontdrvhost the two cannot
        /// disagree. What it licenses is the GATE -- `itrp_RTG` and SVTCA and SDPVTL all install
        /// `itrp_RoundToGridSP` only when `localGS+0xcc != 0 && (globals[0x88] & 4 ||
        /// globals[0x16b] != 0)` -- and Arial issues no INSTCTRL, so GDI's behaviour here says
        /// `globals[0x16b]` was ZERO. This knob exists to measure whether that mechanism is real
        /// before anyone goes looking for 0x16b's writer, and it is deliberately narrow: putting
        /// EVERY rounding on the whole pixel is already known to be catastrophic (see
        /// ClearTypeGrid).</para></summary>
        private static readonly bool s_roundOpWholePixel =
            Environment.GetEnvironmentVariable("WPF_CT_ROUND_GRID") == "1";

        /// <summary>VERIFIED AGAINST ALL TEN ROUND FUNCTIONS (2026-09-19). The bi-level oracle
        /// proves the whole-pixel half of the table at +0x9b8c0, because our bi-level fit is
        /// bit-exact against GDI's own points; the SUBPIXEL half decides every ClearType anchor
        /// and had only ever been checked against pixels. Read end to end, it matches:
        /// <list type="bullet">
        /// <item>Each SP variant is its plain twin with the whole-pixel mask 0xffffffc0 replaced
        /// by the sixteenth's 0xfffffffc and the half 0x20 by 2 -- ToGrid `(v + 2) &amp; ~3`,
        /// DownToGrid `v &amp; ~3`, UpToGrid `(v + 3) &amp; ~3`, ToHalfGrid `(v &amp; ~3) + 2`,
        /// RoundOff `v`. Multiplying by ClearTypeGrid, rounding to the pixel and dividing back,
        /// which is what this does, is the same number in every mode.</item>
        /// <item>Only RoundDownToGridSP@140094a40 carries a gate, and it is the one already
        /// shipped: `!(globals[0x88] bit 2) &amp;&amp; localGS[0x78] == itrp_Project` falls back to
        /// the WHOLE-pixel form. That is rdtgWhole below.</item>
        /// <item>The SP forms halve the compensation (`param_3 / 2`), as the line below does.</item>
        /// <item>Every one of them refuses to let rounding change a value's SIGN: if the result's
        /// sign differs from the input's it returns 0 instead (or +/-0x20, and +/-2 in SP, for
        /// ToHalfGrid). Computing on the magnitude and clamping it at zero, which is what happens
        /// at the end of this function, is the same rule.</item>
        /// <item>itrp_MDRP passes the compensation as `globals[(opcode &amp; 3) + 9]`, a per-colour
        /// array, and applies it inline with the same sign rule when the round bit is clear. The
        /// same excerpt halves the minimum distance when localGS[0xcc] is set, which
        /// s_minDistMdrp already does.</item>
        /// <item>AND THE COMPENSATION IS PROVEN ZERO, read end to end rather than inferred from
        /// the residual's shape, which is how this note used to argue it. fs__Contour fills the
        /// array itself, at 1400247b0 and again in the second pass:
        /// <code>
        ///   c = (0x16c0a - clientRec[0x1a0]) &gt;&gt; 10;      // arithmetic
        ///   globals[9] = 0;      // grey
        ///   globals[10] = c;     // black
        ///   globals[11] = -c;    // white
        ///   globals[12] = 0;
        /// </code>
        /// and clientRec[0x1a0] is a copy of transform[0x98], which bSetXform writes at 14001c67c
        /// from the literal at 14001c7f8. That literal is <c>0a 6a 01 00</c> = 0x16a0a. So
        /// <c>0x16c0a - 0x16a0a = 0x200</c>, exactly half of the 0x400 the shift divides by, and
        /// the arithmetic shift takes it to ZERO on all three colours. The constant is chosen to
        /// make it zero. Swept on the holdout for completeness: every non-zero value is
        /// catastrophic (-6 is 41,625,889 against 28,183), which is what a proof predicts.</item>
        /// </list>
        /// So the ClearType grid is not where the remaining sixty-fourths come from.</summary>
        private int RoundDistance(int distance, bool position = false, bool mdap = false,
                                  int linkType = -1, bool bare = false)
        {
            int comp = s_engineComp == 0 || linkType < 0 ? 0
                     : linkType == 1 ? -s_engineComp        // black: shrink
                     : linkType == 2 ? s_engineComp         // white: expand
                     : 0;                                   // grey
            bool? installed = s_roundInstall ? GdiRoundsToSixteenth() : null;
            if (comp != 0 && (installed ?? (InClearTypeDirection && !BiLevelPass))) comp /= 2;
            distance += distance < 0 ? -comp : comp;
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
            bool rdtgWhole = s_rdtgProjFn && _gs.Round == RoundMode.DownToGrid && !BiLevelPass
                             && !NativeClearTypeMode && _projFnGeneral;
            int thirds = XWholePixelGrid ? 1
                       : rdtgWhole ? 1
                       : bare && s_roundOpWholePixel && !BiLevelPass ? 1
                       : distanceGrid > 0 ? distanceGrid
                       : physicalPosition ? 1
                       : finer && TrueTypeFont.SubpixelFitting && IsHorizontalProjection ? 3
                       : position && s_positionGrid > 0 && InClearTypeDirection ? s_positionGrid
                       : (installed is bool inst ? inst
                         : s_roundLatch && _roundGridSubpixel is bool rg ? rg
                         : s_gridAxisExact ? OnClearTypeAxis : InClearTypeDirection) ? ClearTypeGrid
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
                // WPF_CT_ROUND_PHASE=quarter: on the finer ClearType grid, add a QUARTER of the
                // grid step before truncating rather than a half. The note under RoundMode.Super
                // below records what the binary does -- GDI keeps the value and divides the GRID,
                // its ClearType rounding table being "the same code with the period divided by 16
                // and the phase HALVED" -- and it is ambiguous whether "halved" means the phase
                // ends up at half the new period (ordinary rounding, what we do) or at a quarter
                // of it. This is the second reading, and it is REFUTED: 8,397,970 against
                // 611,135 on the holdout. So "halved" means the phase ends at half the NEW period
                // -- ordinary round-to-nearest on the sixteenth, which is what ships. Worth
                // keeping because it disambiguates that note, which reads both ways.
                // <para>IT ALSO SETTLES ROUND-HALF-DOWN, which looks like a separate and much
                // narrower question and is not. On the finer grid `value` has already been
                // multiplied by sixteen and the original is in sixty-fourths, so value mod 64 is
                // always 0, 16, 32 or 48 -- and `+31` and `+16` send every one of those to the
                // same place. Rounding the tie down IS the quarter phase here, measures the same
                // 8,397,970, and needs no knob of its own.</para>
                case RoundMode.ToGrid:
                    value = s_roundPhaseQuarter && thirds > 1 ? (value + 16) & ~63 : Pix(value);
                    break;
                case RoundMode.ToHalfGrid: value = Floor(value) + 32; break;
                case RoundMode.ToDoubleGrid: value = (value + 16) & ~31; break;
                case RoundMode.DownToGrid: value = Floor(value); break;
                case RoundMode.UpToGrid: value = Ceil(value); break;
                case RoundMode.Off: return distance;
                case RoundMode.Super:
                case RoundMode.Super45:
                    {
                        // THE VALUE HAS ALREADY BEEN SCALED BY `thirds` for the finer ClearType
                        // grid, so the SROUND period, phase and threshold must be scaled with it --
                        // they are in the same 26.6 the value was. Without this a font that sets
                        // its own period rounds a 16x value against a 1x grid, which is not a finer
                        // grid but a period sixteen times too small.
                        // GDI does the mirror image: it keeps the value and divides the GRID. Its
                        // two rounding-function tables (normal at PTR_itrp_RoundToDoubleGrid, the
                        // ClearType set eight entries later) are the same code with the period
                        // divided by 16 and the phase HALVED.
                        // NOTE it is currently a no-op: SROUND is used by 'prep', and 'prep' is
                        // excluded from the ClearType grid (see s_ctInPrep, measured and worse), so
                        // every SROUND this specimen reaches has thirds == 1. Kept because it is
                        // right, and because enabling the ClearType grid anywhere SROUND runs would
                        // otherwise round a 16x value against a 1x period.
                        int scale = s_superRoundScaled ? thirds : 1;
                        int period = _gs.RoundPeriod * scale;
                        if (period <= 0) break;
                        int phase = _gs.RoundPhase * scale;
                        int threshold = _gs.RoundThreshold * scale;
                        value = value - phase + threshold;
                        value = value >= 0 ? value / period * period : -((-value + period - 1) / period * period);
                        value += phase;
                        break;
                    }
            }

            value /= thirds;

            if (value < 0) value = 0;
            return negative ? -value : value;
        }

        /// <summary>SROUND and S45ROUND both take one byte and mean the same thing by it: a period,
        /// a phase within it, and how far past a step counts as reaching the next one.</summary>
        /// <summary>itrp_SetRoundValues@14003f8a0, which decodes SROUND and S45ROUND's one byte.
        /// <para>The periods are LITERALS there, not derived: 0x20 / 0x40 / 0x80 for SROUND and
        /// 0x17 / 0x2d / 0x5b -- 23, 45, 91 -- for S45ROUND, with 999 for the reserved fourth
        /// code. We had been halving and doubling a 46, which gives 46 and 92 where GDI has 45 and
        /// 91. The phase is `(p + 2) &gt;&gt; 2`, `(p + 1) &gt;&gt; 1` and `(3p + 2) &gt;&gt; 2`, and the
        /// threshold `((n - 4) * p + 4) &gt;&gt; 3` with an ARITHMETIC shift -- all of which agree with
        /// the plain divisions we had for SROUND's powers of two and none of which do for
        /// S45ROUND's odd ones. WPF_CT_SROUND_GDI=0 restores the derived values.</para></summary>
        private void SetSuperRound(int selector, int gridPeriod)
        {
            bool s45 = gridPeriod != 64;
            if (s_superRoundGdi)
                _gs.RoundPeriod = (selector & 0xC0) switch
                {
                    0x00 => s45 ? 23 : 32,
                    0x40 => s45 ? 45 : 64,
                    0x80 => s45 ? 91 : 128,
                    _ => 999,
                };
            else
                _gs.RoundPeriod = (selector & 0xC0) switch
                {
                    0x00 => gridPeriod / 2,
                    0x40 => gridPeriod,
                    0x80 => gridPeriod * 2,
                    _ => gridPeriod,
                };

            int p = _gs.RoundPeriod;
            _gs.RoundPhase = (selector & 0x30) switch
            {
                0x00 => 0,
                0x10 => s_superRoundGdi ? (p + 2) >> 2 : p / 4,
                0x20 => s_superRoundGdi ? (p + 1) >> 1 : p / 2,
                _ => s_superRoundGdi ? (3 * p + 2) >> 2 : p * 3 / 4,
            };

            int threshold = selector & 0x0F;
            _gs.RoundThreshold = threshold == 0 ? p - 1
                               : s_superRoundGdi ? ((threshold - 4) * p + 4) >> 3
                               : (threshold - 4) * p / 8;
        }

        private static readonly bool s_superRoundGdi =
            Environment.GetEnvironmentVariable("WPF_CT_SROUND_GDI") != "0";

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
