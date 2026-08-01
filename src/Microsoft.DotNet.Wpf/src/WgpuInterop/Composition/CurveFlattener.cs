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

        // Upper bounds. A degenerate control point (NaN, or a coordinate near float.Max) must
        // not be able to turn one curve into an unbounded vertex stream; the caps also keep
        // the GPU per-fragment segment loops finite.
        private const int MaxCurveSteps = 256;
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
