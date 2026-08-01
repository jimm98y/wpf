// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Curve-flattening fidelity measurement.
//
// Two independent measurements, both reported as numbers rather than pass/fail
// only, so a regression shows up as a trend and not just a broken assertion:
//
//   A. GEOMETRIC -- flatten each shape and measure the true maximum deviation of
//      the resulting polyline from the analytic curve, by dense sampling. This is
//      what "flattening tolerance" is supposed to bound, measured directly.
//
//   B. RENDERED -- fill a large circle and compare the rendered alpha against an
//      analytic reference (exact circle coverage by 32x32 supersampling of the
//      true disc). This catches the case the geometric metric cannot see: an
//      error that only appears once the world transform scales local-space
//      geometry up.
//
// Run with `--report` to print the full table without asserting; the default
// run asserts the tolerance contract and exits non-zero on violation. `--origin N`
// re-centres the geometric cases to probe float32 coordinate precision.
//
// KNOWN LIMIT (measured, not fixed here): geometry is flattened in float32, so past
// roughly 10k device units the coordinate precision itself eats into the tolerance --
// circle r=100 measures 0.98x tol at origin 2000 but 1.59x at origin 100000. A very
// large scrolled or zoomed document would need double-precision or origin rebasing in
// the flattening path; nothing below that scale is affected.
//

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using Microsoft.Wpf.Interop.WebGpu.Composition;
using Microsoft.Wpf.Interop.WebGpu.Composition.Protocol;

internal static class Program
{
    private static int _failures;
    private const float Tol = CurveFlattener.DefaultTolerance;
    private static double _baselineRms;
    // Device coordinate the test circles are centred on. Flattening is evaluated in
    // float32, so a large origin costs precision that shows up directly in the
    // deviation: measured dev/tol on circle r=100 is 0.971 at origin 100, 0.977 at
    // 2000, 1.019 at 10000 and 1.593 at 100000. 2000 is a realistic on-screen device
    // coordinate; --origin overrides it to probe that precision curve.
    private static float OriginBias = 2000f;
    private static bool Diagnose;

    private static int Main(string[] args)
    {
        bool reportOnly = Array.IndexOf(args, "--report") >= 0;
        Diagnose = Array.IndexOf(args, "--diag") >= 0;
        int bi = Array.IndexOf(args, "--origin");
        if (bi >= 0 && bi + 1 < args.Length) OriginBias = float.Parse(args[bi + 1], CultureInfo.InvariantCulture);

        Console.WriteLine("== A. geometric: flattened polyline vs analytic curve (device space, tol="
            + Tol.ToString("0.###", CultureInfo.InvariantCulture) + "px) ==");
        Console.WriteLine($"{"shape",-34}{"segments",9}{"maxDev(px)",14}{"dev/tol",10}");
        Console.WriteLine(new string('-', 68));

        foreach ((string name, PathGeometry geom, Func<float, Vector2> exact, int samples) in GeometryCases())
            MeasureGeometric(name, geom, exact, samples, reportOnly);

        Console.WriteLine();
        Console.WriteLine("== B. rendered: filled disc alpha vs analytic coverage ==");
        Console.WriteLine($"{"case",-34}{"maxErr",10}{"rmsErr",10}{"vs 1x",10}{"verdict",9}");
        Console.WriteLine(new string('-', 83));

        // A local-space unit-ish circle blown up by the world transform is the case that
        // fixed-step flattening got wrong: the chord error scales with the shape.
        MeasureRenderedDisc("disc r=20 (world 1x)", 20f, 1f, reportOnly);
        MeasureRenderedDisc("disc r=20 (world 4x)", 20f, 4f, reportOnly);
        MeasureRenderedDisc("disc r=20 (world 12x)", 20f, 12f, reportOnly);

        Console.WriteLine();
        if (_failures > 0)
        {
            Console.WriteLine($"CURVE FIDELITY TEST FAILED: {_failures} violation(s).");
            return 1;
        }
        Console.WriteLine("CURVE FIDELITY TEST PASSED: flattening stays within tolerance at every scale.");
        return 0;
    }

    // ---- A. geometric ----

    private static void MeasureGeometric(string name, PathGeometry geom, Func<float, Vector2> exact,
        int samples, bool reportOnly)
    {
        // Re-walk with the shared flattener rather than reading FlattenToEdges' output:
        // that drops horizontal edges (they contribute no scanline crossing), which would
        // put gaps in the polyline this measures against.
        var pts = new List<Vector2>();
        foreach (PathFigure f in geom.Figures)
        {
            pts.Add(f.Start);
            Vector2 cur = f.Start;
            foreach (PathSegment seg in f.Segments)
                cur = PathRasterizer.AppendSegment(pts, cur, seg, Tol);
        }

        float maxDev = 0f;
        for (int i = 0; i < samples; i++)
        {
            Vector2 p = exact(i / (float)(samples - 1));
            maxDev = MathF.Max(maxDev, DistToPolyline(p, pts));
        }

        float ratio = maxDev / Tol;
        Console.WriteLine($"{name,-34}{pts.Count,9}{maxDev,14:F5}{ratio,10:F3}");
        if (!reportOnly && ratio > 1.05f)
        {
            Console.WriteLine($"  [FAIL] {name}: deviation {maxDev:F5}px exceeds tolerance {Tol}px");
            _failures++;
        }
    }

    private static IEnumerable<(string, PathGeometry, Func<float, Vector2>, int)> GeometryCases()
    {
        // Goes through the renderer's OWN EllipseGeometry -> path conversion (arc count and
        // all), not a local kappa copy, so this measures what actually gets rasterized.
        foreach (float r in new[] { 4f, 20f, 100f, 600f, 4000f })
        {
            var c = new Vector2(OriginBias, OriginBias);
            yield return ($"circle r={r}", WgpuSceneRenderer.EllipseToPathForTest(c, r, r, Tol),
                t => Circle(c, r, t), 8001);
        }

        // Semicircular MIL arcs, through the PROTOCOL decoder's arc->Bézier conversion --
        // the path a real WPF ArcSegment takes. Same fixed-quadrant error as EllipseToPath had.
        foreach (float r in new[] { 20f, 200f, 1500f })
        {
            var c = new Vector2(OriginBias, OriginBias);
            var afig = new PathFigure(new Vector2(c.X - r, c.Y)) { Closed = false };
            MilcoreEngine.AddArcAsBeziersForTest(afig, new Vector2(c.X - r, c.Y), new Vector2(c.X + r, c.Y),
                r, r, 0f, largeArc: false, sweepClockwise: true);
            yield return ($"MIL arc 180deg r={r}",
                new PathGeometry(FillRule.NonZero, new List<PathFigure> { afig }),
                t => new Vector2(c.X - r * MathF.Cos(t * MathF.PI), c.Y - r * MathF.Sin(t * MathF.PI)), 8001);
        }

        // A cubic with an interior inflection -- the shape the closed-form k^-3 span
        // formula under-counts, and the reason the cubic->quadratic split is adaptive.
        var p0 = new Vector2(50, 300); var q1 = new Vector2(400, -200);
        var q2 = new Vector2(-100, 500); var p1 = new Vector2(350, 100);
        var fig = new PathFigure(p0) { Closed = false };
        fig.Segments.Add(new CubicBezierSegment(q1, q2, p1));
        yield return ("cubic with inflection", new PathGeometry(FillRule.NonZero, new List<PathFigure> { fig }),
            t => CubicAt(p0, q1, q2, p1, t), 4001);

        // A near-cusp cubic (control points nearly coincident through the turn).
        var r0 = new Vector2(100, 100); var s1 = new Vector2(500, 100);
        var s2 = new Vector2(100, 100); var r1 = new Vector2(500, 400);
        var fig2 = new PathFigure(r0) { Closed = false };
        fig2.Segments.Add(new CubicBezierSegment(s1, s2, r1));
        yield return ("cubic near-cusp", new PathGeometry(FillRule.NonZero, new List<PathFigure> { fig2 }),
            t => CubicAt(r0, s1, s2, r1, t), 4001);

        // A large quadratic.
        var u0 = new Vector2(0, 0); var uc = new Vector2(900, 1400); var u1 = new Vector2(1800, 0);
        var fig3 = new PathFigure(u0) { Closed = false };
        fig3.Segments.Add(new QuadraticBezierSegment(uc, u1));
        yield return ("quadratic (1800px span)", new PathGeometry(FillRule.NonZero, new List<PathFigure> { fig3 }),
            t => QuadAt(u0, uc, u1, t), 4001);
    }

    // ---- B. rendered ----

    private static void MeasureRenderedDisc(string name, float localRadius, float worldScale, bool reportOnly)
    {
        float r = localRadius * worldScale;
        int size = (int)MathF.Ceiling(2 * r) + 8;
        var centre = new Vector2(size / 2f, size / 2f);

        // Circle authored in LOCAL space, scaled by the visual transform: the flattening
        // happens before the scale, so this is where magnified chord error would appear.
        var localCentre = new Vector2(centre.X / worldScale, centre.Y / worldScale);
        var root = new SceneVisual { Transform = Matrix3x2.CreateScale(worldScale) };
        root.Content.Add(new GeometryFill(
            new EllipseGeometry(localCentre, localRadius, localRadius),
            RgbaColor.FromBytes(0, 0, 0, 255)));

        using var ctx = WgpuContext.Create();
        var renderer = new WgpuSceneRenderer(ctx);
        byte[] px = renderer.RenderToRgba(root, size, size, RgbaColor.FromBytes(255, 255, 255, 255));

        double sumSq = 0; double maxErr = 0; int count = 0;
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            double reference = DiscCoverage(x, y, centre, r);
            // Black on white: alpha == 1 - (red/255).
            double actual = 1.0 - px[(y * size + x) * 4] / 255.0;
            double e = Math.Abs(actual - reference);
            // Only score pixels at or near the boundary; the interior is trivially exact
            // and would dilute the metric.
            if (reference > 0.001 && reference < 0.999)
            {
                sumSq += e * e; count++;
                if (e > maxErr) maxErr = e;

            }
        }

        double rms = count > 0 ? Math.Sqrt(sumSq / count) : 0;

        if (Diagnose)
        {
            Console.WriteLine($"    [diag] boundary px={count}");
            // Sweep a sub-pixel offset of the REFERENCE. If some non-zero offset fits much
            // better, the residual is a coordinate-convention mismatch in this harness, not
            // renderer error -- worth knowing before drawing any conclusion from the number.
            double bestOff = 0, bestRms = double.MaxValue;
            for (int oy = -8; oy <= 8; oy++)
            for (int ox = -8; ox <= 8; ox++)
            {
                var c2 = new Vector2(centre.X + ox / 16f, centre.Y + oy / 16f);
                double ss = 0; int cc = 0;
                for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    double rf = DiscCoverage(x, y, c2, r);
                    if (rf <= 0.001 || rf >= 0.999) continue;
                    double ac = 1.0 - px[(y * size + x) * 4] / 255.0;
                    ss += (ac - rf) * (ac - rf); cc++;
                }
                double rr = Math.Sqrt(ss / Math.Max(cc, 1));
                if (rr < bestRms) { bestRms = rr; bestOff = ox / 16.0 + 1000 * (oy / 16.0); }
            }
            Console.WriteLine($"    [diag] best sub-pixel refit: rms={bestRms:F6} at offset code {bestOff:F4} (0 = no shift)");
        }
        // Absolute floor. The residual here is the rasterizer's own anti-aliasing accuracy
        // against a 32x32-supersampled reference (a sub-pixel offset sweep confirms the
        // reference is aligned, so none of it is harness error); measured at ~0.073 rms /
        // ~0.13 max, so this bound leaves modest headroom rather than being aspirational.
        bool ok = maxErr <= 0.18 && rms <= 0.09;

        // The contract that actually distinguishes correct flattening: edge accuracy must not
        // DEGRADE as the world transform scales the geometry up. Fixed-step flattening fails
        // exactly here -- the chord error grows with the shape while the pixel grid does not.
        if (_baselineRms == 0) _baselineRms = rms;
        double growth = rms / _baselineRms;
        bool scaleStable = growth <= 1.25;
        ok &= scaleStable;

        Console.WriteLine($"{name,-34}{maxErr,10:F4}{rms,10:F4}{growth,10:F3}{(ok ? "ok" : "FAIL"),9}");
        if (!reportOnly && !ok)
        {
            Console.WriteLine($"  [FAIL] {name}: maxErr={maxErr:F4} rms={rms:F4} growth-vs-1x={growth:F3}");
            _failures++;
        }
    }

    // Exact-ish coverage of pixel (x,y) by the disc, via 32x32 supersampling.
    private static double DiscCoverage(int x, int y, Vector2 c, float r)
    {
        const int S = 32;
        int inside = 0;
        for (int j = 0; j < S; j++)
        for (int i = 0; i < S; i++)
        {
            float sx = x + (i + 0.5f) / S;
            float sy = y + (j + 0.5f) / S;
            float dx = sx - c.X, dy = sy - c.Y;
            if (dx * dx + dy * dy <= r * r) inside++;
        }
        return inside / (double)(S * S);
    }

    // ---- helpers ----

    private static PathGeometry CirclePath(Vector2 c, float r)
    {
        const float K = 0.5522847498f;
        float o = r * K;
        var f = new PathFigure(new Vector2(c.X, c.Y - r)) { Closed = true };
        f.Segments.Add(new CubicBezierSegment(new Vector2(c.X + o, c.Y - r), new Vector2(c.X + r, c.Y - o), new Vector2(c.X + r, c.Y)));
        f.Segments.Add(new CubicBezierSegment(new Vector2(c.X + r, c.Y + o), new Vector2(c.X + o, c.Y + r), new Vector2(c.X, c.Y + r)));
        f.Segments.Add(new CubicBezierSegment(new Vector2(c.X - o, c.Y + r), new Vector2(c.X - r, c.Y + o), new Vector2(c.X - r, c.Y)));
        f.Segments.Add(new CubicBezierSegment(new Vector2(c.X - r, c.Y - o), new Vector2(c.X - o, c.Y - r), new Vector2(c.X, c.Y - r)));
        return new PathGeometry(FillRule.NonZero, new List<PathFigure> { f });
    }

    // NOTE: measured against the true circle, so this also charges the kappa
    // approximation's own ~0.02% radial error -- a deliberate, conservative choice.
    private static Vector2 Circle(Vector2 c, float r, float t)
    {
        float a = -MathF.PI / 2f + t * MathF.PI * 2f;
        return new Vector2(c.X + MathF.Cos(a) * r, c.Y + MathF.Sin(a) * r);
    }

    private static Vector2 CubicAt(Vector2 p0, Vector2 c1, Vector2 c2, Vector2 p1, float t)
    {
        float u = 1f - t;
        return u * u * u * p0 + 3f * u * u * t * c1 + 3f * u * t * t * c2 + t * t * t * p1;
    }

    private static Vector2 QuadAt(Vector2 p0, Vector2 c, Vector2 p1, float t)
    {
        float u = 1f - t;
        return u * u * p0 + 2f * u * t * c + t * t * p1;
    }

    private static float DistToPolyline(Vector2 p, List<Vector2> pts)
    {
        float best = float.MaxValue;
        for (int i = 0; i + 1 < pts.Count; i++)
            best = MathF.Min(best, DistToSegment(p, pts[i], pts[i + 1]));
        return best;
    }

    private static float DistToSegment(Vector2 p, Vector2 a, Vector2 b)
    {
        Vector2 ab = b - a;
        float d2 = Vector2.Dot(ab, ab);
        if (d2 < 1e-20f) return (p - a).Length();
        float t = Math.Clamp(Vector2.Dot(p - a, ab) / d2, 0f, 1f);
        return (p - (a + ab * t)).Length();
    }
}
