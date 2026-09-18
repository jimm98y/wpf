// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Step counts for flattening curves to a geometric error bound, replacing the
// fixed 24-step subdivision that used to be hardcoded at every flattening site.
//
// A fixed step count is wrong in both directions: it facets a large curve (a
// 400px circle got 24 segments per quadrant -- ~2px of visible chord error) and
// wastes work on a small one (a 6px glyph curve also got 24). Every function
// here returns the smallest step count whose worst-case deviation from the true
// curve is within `tolerance`, expressed in the SAME units as the control
// points.
//
// Callers that flatten geometry already mapped to device space pass
// DefaultTolerance directly. Callers that flatten in a geometry's LOCAL space,
// whose output is then scaled up by the world transform, must divide by that
// scale (ToleranceForScale) -- otherwise the error is magnified along with the
// shape, which is exactly the faceting the fixed count produced.
//

using System;
using System.Numerics;

namespace Microsoft.Wpf.Interop.WebGpu.Composition
{
    internal static class CurveFlattener
    {
        /// <summary>
        /// Max allowed deviation from the true curve, in device pixels.
        ///
        /// Tighter than the 0.1-0.25px a general 2D rasterizer would use, and deliberately so:
        /// this rasterizer's own anti-aliasing floor is about 0.015 rms alpha error against a
        /// supersampled reference, so a looser tolerance would make FLATTENING the dominant
        /// error rather than a negligible one. Measured rendered rms error on a filled disc,
        /// sweeping this constant (rasterizer floor ~0.0151):
        ///
        ///     tol      0.1      0.05     0.025    0.0125
        ///     rms      0.0488   0.0278   0.0176   0.0151
        ///
        /// 0.025 sits just above the floor; halving it again doubles the segment count to buy
        /// 0.0025 of alpha. It also reproduces the accuracy the old fixed 24-step subdivision
        /// happened to give at 1x scale, so nothing regresses in the common case.
        /// </summary>
        public const float DefaultTolerance = 0.025f;

        /// <summary>The tolerance GLYPHS are flattened at, which is finer than the default.
        /// <para>A flattening chord sags INWARD on a convex curve, so it can only ever
        /// under-cover, and a lamp is a third of a pixel -- a 0.025px sag is up to seven per
        /// cent of a lamp, one-sided, and text is nothing but small convex curves. Measured on
        /// the text specimen it is worth about eight hundredths of a per cent of the whole
        /// difference at EVERY size:</para>
        /// <code>
        ///   ppem        10        12        14        16        20      TOTAL
        ///   0.025  1,623,912 2,343,248 1,982,506 2,882,767 2,331,725  11,164,158
        ///   0.004  1,612,629 2,338,570 1,965,325 2,860,019 2,299,096  11,075,639
        ///   0.001  1,611,687 2,338,583 1,965,321 2,857,435 2,296,048  11,069,074
        /// </code>
        /// <para>0.004 WAS WHERE THAT SETTLED AND IT IS NOW 0.0001, because GDI DOES NOT FLATTEN
        /// CURVES AT ALL. fontdrvhost's scan converter solves the spline against each scanline --
        /// fsc_CalcSpline takes the span's endpoints in 26.6 and walks scanline indices
        /// (`(hi - 0x21 >> 6) + 1` and the mirror for the descending case), evaluating the curve
        /// there rather than walking a polyline. So a flattening chord is not an approximation of
        /// what GDI draws, it is pure one-sided error against it, and the only question is how
        /// much of it we are prepared to pay to remove.</para>
        /// <para>Re-measured on the holdout (6 faces x 4 styles x 8..24ppem) it is worth a great
        /// deal more than the note below claimed. That note was written under a coverage model
        /// several fixes ago and is left standing only as a record of the method:</para>
        /// <code>
        ///   tolerance   0.004    0.001   0.0005   0.0002   0.0001  0.00005  0.000005
        ///   holdout   843,447  657,099  627,945  614,202  611,135  609,651   608,571
        ///   seconds        21       34       34       40       53      103       261
        /// </code>
        /// <para>0.0001 is 99.6 per cent of everything available and a fifth of the cost of
        /// chasing the last 0.4. The mechanism is why the number is so large for so small a sag:
        /// 0.004px against a lamp of a third of a pixel is 1.2 per cent of a lamp, it is ONE-SIDED
        /// (a chord on a convex curve can only ever under-cover), and it applies to every curved
        /// edge in every glyph. At 0.0001 the same sag is 0.03 per cent of a lamp.</para>
        /// <para>The superseded 2026 measurement, kept for the method:
        /// 0.001 buys a further 0.06 per cent for two and a half times the segments, so
        /// 0.004 is where it settles. The ink ratio against Windows moves 0.9855 to 0.9863 at
        /// 16ppem, in the direction the mechanism predicts, which is what makes this a fix
        /// rather than a fitted constant.</para>
        /// <para>AND EVERY OTHER KNOB WAS RE-SWEPT AFTER IT, because this file's own rule is
        /// that a geometry-level change makes the rest stale. None moved: gamma is still a
        /// clean minimum at 1.20 (1.17 and 1.23 give 2,962,941 and 2,967,828 against
        /// 2,860,019 at 16ppem), stem fat still 6 (3 and 9 give 2,347,178 and 2,359,248
        /// against 2,338,608 at 12ppem), the control-value cut-in still divided by 16 (8 and
        /// 32 give 2,572,083 and 2,429,427), XHintMode still 5, and the contrast curve still
        /// applied AFTER the filter rather than before it -- that last one was explicitly
        /// flagged as settled under an older coverage model and re-testable, and it measures
        /// 2,772,488 and 3,444,957 the other way at 12 and 16ppem. So this correction is
        /// orthogonal to all of them.</para>
        /// <para>RE-SWEPT AGAIN in 2026-09 after the move to 0.0001, on 10/12/14/16/20ppem against
        /// a baseline of 176,245. All still hold and none was absorbing the sag: gamma 1.20
        /// (1.17 and 1.23 give 859,943 and 658,567), cut-in divisor 16 (8 and 32 give 2,819,125
        /// and 1,452,103), x-hint mode 5 (1, 4, 6, 7 give 26.1M, 4.4M, 9.0M, 4.2M; mode 2 measures
        /// identically to 5), contrast filter off (on 16,433,937, auto 8,804,877). The gamma
        /// minimum is far SHARPER than it was, which is what removing a one-signed ink deficit
        /// should do. One correction to the paragraph above: "stem fat still 6" is stale -- the
        /// shipped default is 0, and 6 now costs 282,442 against 176,245.</para>
        /// <para>Kept separate from the default so that ordinary geometry -- a large rounded
        /// rectangle, a circle -- does not pay two and a half times the segments for an
        /// accuracy only text at a few pixels an em can use. WPF_CURVE_TOL overrides
        /// both.</para></summary>
        public static readonly float GlyphTolerance =
            float.TryParse(Environment.GetEnvironmentVariable("WPF_CURVE_TOL"),
                           System.Globalization.NumberStyles.Float,
                           System.Globalization.CultureInfo.InvariantCulture, out float gt)
                && gt > 0 ? gt : 0.0001f;

        // AND IT IS CONVERGED. A sweep on the 90-row specimen, with the step cap raised so that
        // the cap is not what a curve actually gets: a ten-thousandth of a pixel measures 45,362,
        // two millionths 45,128, and a ten-millionth 45,228 -- DOWN AND THEN BACK UP, and
        // identical at 256 and 4096 steps. A converging quantity does not do that. What moves is
        // the flattening vertices landing on different sides of sample centres, so the 234 (and
        // the holdout's 2,775) is tie noise, not chord error being removed. Do not lower the
        // tolerance chasing it: it costs five times the run time and buys a coin toss.

        /// <summary>The tolerance a caller gets when it does not name one. WPF_CURVE_TOL.
        /// <para>Worth a knob because 0.025px is not obviously below the floor for TEXT. A
        /// flattening chord sags INWARD on a convex curve, so it can only ever under-cover,
        /// and a lamp is a third of a pixel -- so a 0.025px sag is up to seven per cent of a
        /// lamp, one-sided. The specimen is measurably light (ink against Windows 0.9855 at
        /// 16ppem), which is the symptom that mechanism would produce.</para></summary>
        public static readonly float Tolerance =
            float.TryParse(Environment.GetEnvironmentVariable("WPF_CURVE_TOL"),
                           System.Globalization.NumberStyles.Float,
                           System.Globalization.CultureInfo.InvariantCulture, out float t)
                && t > 0 ? t : DefaultTolerance;

        // Upper bounds. A degenerate control point (NaN, or a coordinate near float.Max) must
        // not be able to turn one curve into an unbounded vertex stream; the caps also keep
        // the GPU per-fragment segment loops finite.
        /// <summary>WPF_CURVE_MAXSTEPS raises the cap. Below about a ten-thousandth of a pixel
        /// the cap, not the tolerance, is what a glyph curve actually gets, so a convergence
        /// sweep has to move both.</summary>
        private static readonly int MaxCurveSteps =
            int.TryParse(Environment.GetEnvironmentVariable("WPF_CURVE_MAXSTEPS"), out int ms)
                && ms > 0 ? ms : 256;
        private const int MaxRingSteps = 512;

        /// <summary>
        /// Tolerance in LOCAL units for geometry that a later transform scales by
        /// <paramref name="scale"/> device pixels per local unit.
        /// </summary>
        public static float ToleranceForScale(float scale)
        {
            if (!(scale > 1e-6f) || float.IsNaN(scale)) return DefaultTolerance;
            return DefaultTolerance / scale;
        }

        /// <summary>Uniform scale magnitude of a transform's linear part (max of the two axis scales).</summary>
        public static float ScaleOf(Matrix3x2 m)
        {
            float sx = MathF.Sqrt(m.M11 * m.M11 + m.M12 * m.M12);
            float sy = MathF.Sqrt(m.M21 * m.M21 + m.M22 * m.M22);
            return MathF.Max(sx, sy);
        }

        /// <summary>
        /// Line segments needed to flatten a quadratic Bézier within <paramref name="tolerance"/>.
        /// For a quadratic the chord error is EXACT, not just bounded: B(t) - chord(t) =
        /// -t(1-t)·d with d = p0 - 2c + p1, so the max deviation over k uniform spans is
        /// |d| / (4k²). Inverting gives the step count below.
        /// </summary>
        public static int QuadraticSteps(Vector2 p0, Vector2 c, Vector2 p1, float tolerance)
        {
            // A caller that names no tolerance passes zero and means the default;
            // resolving it here keeps every entry point honouring WPF_CURVE_TOL.
            if (!(tolerance > 0f)) tolerance = Tolerance;
            Vector2 d = p0 - 2f * c + p1;
            return StepsFromSquareLaw(d.Length() * 0.25f, tolerance);
        }

        /// <summary>
        /// Line segments needed to flatten a cubic Bézier within <paramref name="tolerance"/>.
        /// Uses the standard degree-n bound: over k uniform spans the deviation is at most
        /// (n(n-1)/8)·k⁻²·max‖Δ²P‖, i.e. 0.75·k⁻²·max(|p0-2c1+c2|, |c1-2c2+p1|) for n = 3.
        /// </summary>
        public static int CubicSteps(Vector2 p0, Vector2 c1, Vector2 c2, Vector2 p1, float tolerance)
        {
            // A caller that names no tolerance passes zero and means the default;
            // resolving it here keeps every entry point honouring WPF_CURVE_TOL.
            if (!(tolerance > 0f)) tolerance = Tolerance;
            float m = MathF.Max((p0 - 2f * c1 + c2).Length(), (c1 - 2f * c2 + p1).Length());
            return StepsFromSquareLaw(0.75f * m, tolerance);
        }


        /// <summary>
        /// Segments in a regular polygon approximating a full circle of <paramref name="radius"/>
        /// within <paramref name="tolerance"/>. The sagitta of a chord subtending 2π/k is
        /// r·(1 - cos(π/k)), inverted exactly here rather than through the small-angle
        /// approximation so a very coarse tolerance doesn't under-count.
        /// </summary>
        public static int CircleSteps(float radius, float tolerance)
        {
            // A caller that names no tolerance passes zero and means the default;
            // resolving it here keeps every entry point honouring WPF_CURVE_TOL.
            if (!(tolerance > 0f)) tolerance = Tolerance;
            if (!(radius > 0f) || float.IsNaN(radius)) return 3;
            if (!(tolerance > 0f)) return MaxRingSteps;
            if (tolerance >= radius) return 3;                     // coarser than the shape itself
            double theta = Math.Acos(1.0 - tolerance / radius);    // half the subtended angle
            if (!(theta > 0.0)) return MaxRingSteps;
            int k = (int)Math.Ceiling(Math.PI / theta);
            return Math.Max(3, Clamp(k, MaxRingSteps));
        }

        /// <summary>
        /// Cubic Bézier arcs needed to approximate an elliptical arc of <paramref name="radius"/>
        /// sweeping <paramref name="sweepRadians"/>, within <paramref name="tolerance"/>.
        ///
        /// A quarter-arc (the usual "split at 90 degrees" rule) carries radial error of about
        /// 2.7e-4·r, which is invisible on a 20px corner and 0.16px on a 600px one -- well past
        /// the flattening tolerance, and the dominant geometric error on a large curve even
        /// after subdivision itself became adaptive. The error scales as the sixth power of the
        /// per-piece angle, so the count grows extremely slowly: one extra split buys 64x.
        /// </summary>
        public static int ArcCount(float radius, float sweepRadians, float tolerance)
        {
            // A caller that names no tolerance passes zero and means the default;
            // resolving it here keeps every entry point honouring WPF_CURVE_TOL.
            if (!(tolerance > 0f)) tolerance = Tolerance;
            float sweep = MathF.Abs(sweepRadians);
            if (!(sweep > 0f) || float.IsNaN(sweep)) return 1;
            int quadrants = Math.Max(1, (int)Math.Ceiling(sweep / (MathF.PI / 2f)));
            if (!(radius > 0f) || !(tolerance > 0f)) return quadrants;

            // The arc approximation and the line flattening that follows it are independent
            // errors that ADD, so the arc stage only gets part of the budget; without the split
            // a large circle measures just over tolerance with each stage individually inside it.
            double excess = 2.7e-4 * radius / (tolerance / 3.0);
            if (excess <= 1.0) return quadrants;
            int refine = (int)Math.Ceiling(Math.Pow(excess, 1.0 / 6.0));
            return quadrants * Math.Clamp(refine, 1, 16);
        }

        // Shared inverse of an error that decays as k^-2: err(k) = coefficient / k^2.
        private static int StepsFromSquareLaw(float coefficient, float tolerance)
        {
            if (!(tolerance > 0f) || float.IsNaN(coefficient)) return 1;
            if (coefficient <= tolerance) return 1;
            double k = Math.Sqrt(coefficient / tolerance);
            return Clamp((int)Math.Ceiling(k), MaxCurveSteps);
        }

        private static int Clamp(int k, int max) => k < 1 ? 1 : (k > max ? max : k);
    }
}
