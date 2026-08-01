// Scale-stability regression test for the code paths that perform LINE flattening:
//   fill-cpu  : PathRasterizer.Flatten          (WPF_WEBGPU_CPU_RASTER=1)
//   stroke-rnd: PathRasterizer.FlattenCenterlineSegments  (GPU round-join SDF stroke)
//   stroke-mtr: PathStroker.FlattenFigure + AddDisc       (stroke-to-fill, miter join)
// Ground truth is analytic: a disc, or an annulus for the strokes.
//
// The property under test is that rendered edge accuracy does not fall apart as the world
// transform scales geometry up. With the old fixed 24-step subdivision it did, badly --
// measured rms alpha error from 1x to 24x zoom, CPU raster path:
//
//     fill          0.0151 -> 0.1217   (8.1x)
//     stroke round  0.0135 -> 0.2329  (17.2x), max error 0.998 (edge essentially wrong)
//     stroke miter  0.0108 -> 0.1412  (13.0x)
//
// The bounds below are absolute rather than ratios: a ratio passes trivially if the 1x
// case is also bad. Run with --cost for segment counts and rasterizer throughput.
using System;
using System.Collections.Generic;
using System.Numerics;
using Microsoft.Wpf.Interop.WebGpu.Composition;

internal static class Program
{
    // Measured worst case across all scales after the change is maxErr 0.167 / rms 0.064,
    // so these leave modest headroom; the baseline exceeds both by 4-6x.
    //
    // The strokes still drift ~4x from 1x to 24x. That residual is NOT flattening: sweeping
    // the tolerance from 0.1 down to 0.0125 leaves stroke 24x rms flat at 0.053-0.067 (it
    // even rises slightly), so the error is in the stroke-to-fill contour construction --
    // the union of per-segment rectangles with join discs -- not in how finely curves are
    // subdivided. Worth a separate look; tightening the flattener cannot fix it.
    private const double MaxErrBound = 0.25;
    private const double RmsBound = 0.09;
    private static int _failures;

    private static int Main(string[] argv)
    {
        if (Array.IndexOf(argv, "--cost") >= 0) { Cost(); return 0; }

        Console.WriteLine($"{"case",-34}{"maxErr",10}{"rmsErr",10}{"vs 1x",9}{"",6}");
        Console.WriteLine(new string('-', 63));
        Run("fill", (c, r) => Fill(c, r), null);
        Run("stroke-round", (c, r) => StrokeCircle(c, r, LineJoin.Round, LineCap.Round), 6f);
        Run("stroke-miter", (c, r) => StrokeCircle(c, r, LineJoin.Miter, LineCap.Butt), 6f);
        if (_failures > 0)
        {
            Console.WriteLine($"SCALE STABILITY FAILED: {_failures} case(s) outside bounds.");
            return 1;
        }
        Console.WriteLine("SCALE STABILITY PASSED: edge accuracy holds from 1x to 24x.");
        return 0;
    }

    // Cost side: how many line segments the flattener emits and how long the CPU
    // rasterizer takes, for shapes at the sizes real UI actually draws. Calls only
    // API whose signature is identical in both trees (the tolerance parameter is
    // optional), so the same source measures baseline and adaptive.
    private static void Cost()
    {
        Console.WriteLine($"{"shape",-30}{"lines",8}{"gpu segs",11}{"raster ms/1k",14}");
        Console.WriteLine(new string('-', 54));
        long totalSegs = 0, totalQuads = 0; double totalMs = 0;
        foreach ((string name, PathGeometry g) in CostShapes())
        {
            var edges = new List<float>();
            int n = PathRasterizer.FlattenToEdges(g, edges, out _, out _, out _, out _);
            // What the GPU coverage path actually uploads: quadratic segments, solved
            // analytically per scanline. This is the number native cubic support would reduce.
            var gpuSegs = new List<float>();
            int nq = PathRasterizer.SegmentsToCubics(g, gpuSegs, out _, out _, out _, out _);
            for (int i = 0; i < 20; i++) PathRasterizer.Rasterize(g);      // warm up + JIT
            var sw = System.Diagnostics.Stopwatch.StartNew();
            const int Iters = 1000;
            for (int i = 0; i < Iters; i++) PathRasterizer.Rasterize(g);
            sw.Stop();
            Console.WriteLine($"{name,-30}{n,8}{nq,11}{sw.Elapsed.TotalMilliseconds,14:F1}");
            totalSegs += n; totalQuads += nq; totalMs += sw.Elapsed.TotalMilliseconds;
        }
        Console.WriteLine(new string('-', 54));
        Console.WriteLine($"{"TOTAL",-30}{totalSegs,8}{totalQuads,11}{totalMs,14:F1}");
        GpuThroughput();
    }

    // GPU cost of the coverage pass, reported ABOVE A MEASURED FLOOR.
    //
    // Two things make the naive measurement useless here, both found by measuring rather
    // than assuming: (1) WgpuSceneRenderer caches a rasterized mask per (shape, transform),
    // so a static scene runs fs_coverage once and the rest of the frames just composite --
    // the geometry is animated to force a miss per shape per frame; (2) RenderToRgba ends in
    // a full GPU->CPU readback that costs ~2.3ms at 900x600, which is MORE than the entire
    // scene's render work, so the floor has to be subtracted or the signal is invisible.
    private static void GpuThroughput()
    {
        const int W = 900, H = 600, Frames = 100;
        using var ctx = WgpuContext.Create();
        var renderer = new WgpuSceneRenderer(ctx);
        var bg = RgbaColor.FromBytes(255, 255, 255, 255);

        var empty = new SceneVisual();
        empty.Content.Add(new GeometryFill(new RectangleGeometry(new Rect(0, 0, 8, 8)),
            RgbaColor.FromBytes(0, 0, 0, 255)));
        double floor = Time(renderer, bg, W, H, Frames, _ => empty);
        double small = Time(renderer, bg, W, H, Frames, f => BuildScene(f, W, H));
        double large = Time(renderer, bg, W, H, Frames, f => BuildLargeScene(f, W, H));

        Console.WriteLine();
        Console.WriteLine($"gpu readback floor           : {floor,6:F2} ms/frame");
        Console.WriteLine($"120 small shapes, animated   : {small,6:F2} ms/frame  ({small - floor,5:F2} above floor)");
        Console.WriteLine($"6 large discs r~250, animated: {large,6:F2} ms/frame  ({large - floor,5:F2} above floor)");
    }

    private static double Time(WgpuSceneRenderer r, RgbaColor bg, int w, int h, int frames,
        Func<int, SceneVisual> build)
    {
        for (int i = 0; i < 10; i++) r.RenderToRgba(build(i), w, h, bg);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int f = 0; f < frames; f++) r.RenderToRgba(build(100 + f), w, h, bg);
        sw.Stop();
        return sw.Elapsed.TotalMilliseconds / frames;
    }

    private static SceneVisual BuildLargeScene(int frame, int w, int h)
    {
        var root = new SceneVisual();
        float wobble = 1f + 0.11f * MathF.Sin(frame * 0.41f);
        for (int i = 0; i < 6; i++)
        {
            float cx = 150 + (i % 3) * 300, cy = 160 + (i / 3) * 280;
            var col = RgbaColor.FromBytes((byte)(40 + i * 30), (byte)(90 + i * 20), 200, 128);
            root.Content.Add(new GeometryFill(
                new EllipseGeometry(new Vector2(cx, cy), 250f * wobble, 250f * wobble), col));
        }
        return root;
    }

    private static SceneVisual BuildScene(int frame, int w, int h)
    {
        var root = new SceneVisual();
        int seed = 12345;
        int Next(int lo, int hi) { seed = (int)((1103515245L * seed + 12345) & 0x7fffffff); return lo + seed % (hi - lo); }
        float wobble = 1f + 0.13f * MathF.Sin(frame * 0.37f);
        for (int i = 0; i < 120; i++)
        {
            float cx = Next(40, w - 40), cy = Next(40, h - 40);
            float r = Next(6, 60) * wobble;
            var col = RgbaColor.FromBytes((byte)Next(0, 255), (byte)Next(0, 255), (byte)Next(0, 255), 255);
            if ((i & 1) == 0)
                root.Content.Add(new GeometryFill(new EllipseGeometry(new Vector2(cx, cy), r, r), col));
            else
                root.Content.Add(new GeometryFill(new RoundedRectangleGeometry(
                    new Rect(cx - r, cy - r * 0.6f, r * 2, r * 1.2f), r * 0.35f, r * 0.35f), col));
        }
        return root;
    }

    private static IEnumerable<(string, PathGeometry)> CostShapes()
    {
        yield return ("icon circle r=8", CirclePath(new Vector2(16, 16), 8));
        yield return ("button rrect 120x32 r=6", RRect(120, 32, 6));
        yield return ("card rrect 320x200 r=12", RRect(320, 200, 12));
        yield return ("avatar circle r=24", CirclePath(new Vector2(32, 32), 24));
        yield return ("hero circle r=200", CirclePath(new Vector2(220, 220), 200));
    }

    private static PathGeometry RRect(float w, float h, float r)
    {
        const float K = 0.5522847498f; float k = r * K; float x = 4, y = 4;
        var f = new PathFigure(new Vector2(x + r, y)) { Closed = true };
        f.Segments.Add(new LineSegment(new Vector2(x + w - r, y)));
        f.Segments.Add(new CubicBezierSegment(new Vector2(x + w - r + k, y), new Vector2(x + w, y + r - k), new Vector2(x + w, y + r)));
        f.Segments.Add(new LineSegment(new Vector2(x + w, y + h - r)));
        f.Segments.Add(new CubicBezierSegment(new Vector2(x + w, y + h - r + k), new Vector2(x + w - r + k, y + h), new Vector2(x + w - r, y + h)));
        f.Segments.Add(new LineSegment(new Vector2(x + r, y + h)));
        f.Segments.Add(new CubicBezierSegment(new Vector2(x + r - k, y + h), new Vector2(x, y + h - r + k), new Vector2(x, y + h - r)));
        f.Segments.Add(new LineSegment(new Vector2(x, y + r)));
        f.Segments.Add(new CubicBezierSegment(new Vector2(x, y + r - k), new Vector2(x + r - k, y), new Vector2(x + r, y)));
        return new PathGeometry(FillRule.NonZero, new List<PathFigure> { f });
    }

    private static void Run(string label, Func<Vector2, float, DrawingPrimitive> make, float? strokeHalf)
    {
        double base_ = 0;
        foreach (float ws in new[] { 1f, 4f, 12f, 24f })
        {
            (double mx, double rms) = Measure(20f, ws, make, strokeHalf);
            if (base_ == 0) base_ = rms;
            bool ok = mx <= MaxErrBound && rms <= RmsBound;
            if (!ok) _failures++;
            Console.WriteLine($"{label + " world " + ws + "x",-34}{mx,10:F4}{rms,10:F4}{rms / base_,9:F3}{(ok ? "  ok" : "  FAIL")}");
        }
        Console.WriteLine();
    }

    private static (double, double) Measure(float localRadius, float ws,
        Func<Vector2, float, DrawingPrimitive> make, float? strokeHalf)
    {
        float r = localRadius * ws;
        float pad = strokeHalf.HasValue ? strokeHalf.Value * ws + 6 : 8;
        int size = (int)MathF.Ceiling(2 * r + 2 * pad);
        var centre = new Vector2(size / 2f, size / 2f);

        var root = new SceneVisual { Transform = Matrix3x2.CreateScale(ws) };
        root.Content.Add(make(new Vector2(centre.X / ws, centre.Y / ws), localRadius));

        using var ctx = WgpuContext.Create();
        var renderer = new WgpuSceneRenderer(ctx);
        byte[] px = renderer.RenderToRgba(root, size, size, RgbaColor.FromBytes(255, 255, 255, 255));

        float inner = strokeHalf.HasValue ? r - strokeHalf.Value * ws : 0f;
        float outer = strokeHalf.HasValue ? r + strokeHalf.Value * ws : r;

        double sumSq = 0, maxErr = 0; int n = 0;
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            double reference = Coverage(x, y, centre, inner, outer);
            if (reference <= 0.001 || reference >= 0.999) continue;
            double actual = 1.0 - px[(y * size + x) * 4] / 255.0;
            double e = Math.Abs(actual - reference);
            sumSq += e * e; n++;
            if (e > maxErr) maxErr = e;
        }
        return (maxErr, Math.Sqrt(sumSq / Math.Max(n, 1)));
    }

    private static DrawingPrimitive Fill(Vector2 c, float r)
        => new GeometryFill(new EllipseGeometry(c, r, r), RgbaColor.FromBytes(0, 0, 0, 255));

    private static DrawingPrimitive StrokeCircle(Vector2 c, float r, LineJoin join, LineCap cap)
        => new GeometryStroke(CirclePath(c, r), RgbaColor.FromBytes(0, 0, 0, 255),
                              new StrokeStyle(12.0, cap, join));

    // Circle as 4 kappa cubics, authored explicitly so both trees build the SAME input
    // geometry (the ellipse->path arc count is one of the things under test).
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

    private static double Coverage(int x, int y, Vector2 c, float inner, float outer)
    {
        const int S = 24; int hit = 0;
        for (int j = 0; j < S; j++)
        for (int i = 0; i < S; i++)
        {
            float dx = x + (i + 0.5f) / S - c.X, dy = y + (j + 0.5f) / S - c.Y;
            float d2 = dx * dx + dy * dy;
            if (d2 <= outer * outer && d2 >= inner * inner) hit++;
        }
        return hit / (double)(S * S);
    }
}
