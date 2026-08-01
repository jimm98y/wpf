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
    private static bool _fillControl;

    private static int Main(string[] argv)
    {
        if (Array.IndexOf(argv, "--cost") >= 0) { Cost(); return 0; }
        if (Array.IndexOf(argv, "--segscale") >= 0) { SegmentScaling(); return 0; }
        if (Array.IndexOf(argv, "--strokegeom") >= 0) { StrokeGeom(); return 0; }
        if (Array.IndexOf(argv, "--strokediag") >= 0) { _fillControl = Array.IndexOf(argv, "--fill") >= 0; StrokeDiag(); return 0; }

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

    // GPU cost of the coverage pass, measured as a SLOPE.
    //
    // Absolute frame time cannot answer this. RenderToRgba ends in a full GPU->CPU readback
    // costing ~2.3ms at 900x600 -- more than the entire scene's render work -- so a single
    // measurement is dominated by a constant that has nothing to do with the shader. Two
    // earlier attempts here reported pure noise for exactly that reason.
    //
    // Instead render the same complex path 1, 2, 4, 8, 16 times in one frame and fit a line.
    // Readback, buffer setup and submit are all in the intercept; the SLOPE is milliseconds
    // per additional rasterized path, which is what a change to fs_coverage actually moves.
    // The mask cache is defeated by giving each copy its own geometry instance and radius.
    private static void GpuThroughput()
    {
        const int W = 1000, H = 1000;
        int[] counts = { 1, 2, 4, 8, 16 };
        var xs = new List<double>();
        var ys = new List<double>();

        using var ctx = WgpuContext.Create();
        var renderer = new WgpuSceneRenderer(ctx);
        var bg = RgbaColor.FromBytes(255, 255, 255, 255);

        Console.WriteLine();
        Console.WriteLine($"{"paths",8}{"ms/frame",12}{"gpu segs",11}");
        Console.WriteLine(new string('-', 31));
        foreach (int n in counts)
        {
            int frames = 24;
            for (int i = 0; i < 6; i++) renderer.RenderToRgba(LobedScene(n, i, W, H), W, H, bg);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (int f = 0; f < frames; f++) renderer.RenderToRgba(LobedScene(n, 100 + f, W, H), W, H, bg);
            sw.Stop();
            double ms = sw.Elapsed.TotalMilliseconds / frames;

            var probe = new List<float>();
            int segs = PathRasterizer.SegmentsToCubics(Lobed(new Vector2(500, 500), 320f, 0), probe,
                out _, out _, out _, out _);
            Console.WriteLine($"{n,8}{ms,12:F2}{segs * n,11}");
            xs.Add(n); ys.Add(ms);
        }

        // Least squares fit.
        double mx = 0, my = 0;
        for (int i = 0; i < xs.Count; i++) { mx += xs[i]; my += ys[i]; }
        mx /= xs.Count; my /= ys.Count;
        double num = 0, den = 0;
        for (int i = 0; i < xs.Count; i++) { num += (xs[i] - mx) * (ys[i] - my); den += (xs[i] - mx) * (xs[i] - mx); }
        double slope = num / den, intercept = my - slope * mx;
        Console.WriteLine($"  fixed overhead (intercept): {intercept,6:F2} ms   <- readback + submit");
        Console.WriteLine($"  COVERAGE COST (slope)     : {slope,6:F3} ms per rasterized path");
    }

    // Does SEGMENT COUNT drive the coverage cost, and how steeply?
    //
    // This is the question a segment-count reduction (native cubics, a path atlas, anything
    // that changes how much geometry the shader loops over) actually turns on, and it is
    // answerable without an A/B against deleted code. Hold the covered AREA fixed and vary
    // only the number of segments, by changing how many lobes the test curve has. If cost is
    // linear in segments, a 7x segment reduction is worth ~7x of the coverage term.
    private static void SegmentScaling()
    {
        const int W = 1000, H = 1000;
        Console.WriteLine($"{"lobes",7}{"segments",10}{"ms/path",11}{"us/segment",13}");
        Console.WriteLine(new string('-', 41));
        using var ctx = WgpuContext.Create();
        var renderer = new WgpuSceneRenderer(ctx);
        var bg = RgbaColor.FromBytes(255, 255, 255, 255);

        foreach (int lobes in new[] { 6, 12, 24, 48 })
        {
            var probe = new List<float>();
            int segs = PathRasterizer.SegmentsToCubics(Lobed(new Vector2(500, 500), 320f, 0, lobes), probe,
                out _, out _, out _, out _);
            // Two path counts; the difference cancels the fixed overhead exactly.
            double t1 = TimeLobed(renderer, bg, 2, lobes, W, H);
            double t2 = TimeLobed(renderer, bg, 10, lobes, W, H);
            double msPerPath = (t2 - t1) / 8.0;
            Console.WriteLine($"{lobes,7}{segs,10}{msPerPath,11:F3}{msPerPath * 1000.0 / segs,13:F1}");
        }
    }

    private static double TimeLobed(WgpuSceneRenderer r, RgbaColor bg, int n, int lobes, int w, int h)
    {
        for (int i = 0; i < 5; i++) r.RenderToRgba(LobedScene(n, i, w, h, lobes), w, h, bg);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        const int F = 20;
        for (int f = 0; f < F; f++) r.RenderToRgba(LobedScene(n, 100 + f, w, h, lobes), w, h, bg);
        sw.Stop();
        return sw.Elapsed.TotalMilliseconds / F;
    }

    // A many-lobed closed curve: lots of cubic segments over a large area, which is what
    // makes fs_coverage's per-fragment segment loop the dominant term.
    private static PathGeometry Lobed(Vector2 c, float r, int phase, int lobes = 12)
    {
        int Lobes = lobes;
        var f = new PathFigure(new Vector2(c.X + r, c.Y)) { Closed = true };
        for (int i = 0; i < Lobes; i++)
        {
            float a0 = i * MathF.PI * 2f / Lobes, a1 = (i + 1) * MathF.PI * 2f / Lobes;
            float rm = r * (i % 2 == 0 ? 0.62f : 1.0f) * (1f + 0.02f * phase);
            var mid = new Vector2(c.X + MathF.Cos((a0 + a1) * 0.5f) * rm, c.Y + MathF.Sin((a0 + a1) * 0.5f) * rm);
            var end = new Vector2(c.X + MathF.Cos(a1) * r, c.Y + MathF.Sin(a1) * r);
            f.Segments.Add(new CubicBezierSegment(mid, mid, end));
        }
        return new PathGeometry(FillRule.NonZero, new List<PathFigure> { f });
    }

    private static SceneVisual LobedScene(int n, int phase, int w, int h, int lobes = 12)
    {
        var root = new SceneVisual();
        for (int i = 0; i < n; i++)
        {
            // Each copy gets its own radius so the mask cache misses every time.
            float r = 300f + i * 0.5f + (phase % 7);
            root.Content.Add(new GeometryFill(Lobed(new Vector2(w / 2f, h / 2f), r, phase + i, lobes),
                RgbaColor.FromBytes((byte)(20 + i * 9), 90, 160, 40)));
        }
        return root;
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

    // WHERE does the stroke error live? A tolerance sweep already showed it is not
    // flattening, and CPU and GPU rasterizers report identical numbers, which points at the
    // stroke-to-fill GEOMETRY rather than either rasterizer. This bins the error two ways:
    // by angle around the circle (4 spikes => the kappa arc junctions; uniform => systematic
    // offset error; scattered => contour seams) and by signed radial position (inner vs outer
    // wall => which boundary is misplaced).
    // Direct measurement of the stroke-to-fill OUTPUT GEOMETRY, with no rasterizer involved:
    // every vertex PathStroker emits should sit on one of the two ideal annulus walls. This
    // separates "the outline is in the wrong place" from any rendering effect.
    private static void StrokeGeom()
    {
        const float localR = 20f, halfLocal = 6f;
        var c = new Vector2(0, 0);
        Console.WriteLine($"{"scale",7}{"tol(local)",13}{"vertices",10}{"maxDev",10}{"meanDev",10}{"dev(device)",13}");
        Console.WriteLine(new string('-', 63));
        foreach (float ws in new[] { 1f, 4f, 12f, 24f })
        {
            float tol = CurveFlattener.ToleranceForScale(ws);
            PathGeometry outline = PathStroker.Stroke(CirclePath(c, localR),
                new StrokeStyle(halfLocal * 2, LineCap.Butt, LineJoin.Miter), tol);

            double maxDev = 0, sum = 0; int n = 0;
            foreach (PathFigure f in outline.Figures)
            {
                var pts = new List<Vector2> { f.Start };
                foreach (PathSegment sg in f.Segments) if (sg is LineSegment l) pts.Add(l.Point);
                foreach (Vector2 v in pts)
                {
                    double d = v.Length();
                    // Distance to whichever ideal wall is nearer.
                    double dev = Math.Min(Math.Abs(d - (localR + halfLocal)), Math.Abs(d - (localR - halfLocal)));
                    maxDev = Math.Max(maxDev, dev); sum += dev; n++;
                }
            }
            double mean = n > 0 ? sum / n : 0;
            Console.WriteLine($"{ws,7}{tol,13:F5}{n,10}{maxDev,10:F5}{mean,10:F5}{maxDev * ws,13:F4}");
        }
        Console.WriteLine();
        Console.WriteLine("  dev is in LOCAL units; dev(device) = maxDev * scale, i.e. what a pixel sees.");
    }

    private static void StrokeDiag()
    {
        const float localR = 20f, halfLocal = 6f;
        foreach (float ws in new[] { 1f, 24f })
        {
            float r = localR * ws, half = halfLocal * ws;
            int size = (int)MathF.Ceiling(2 * (r + half) + 12);
            var centre = new Vector2(size / 2f, size / 2f);
            var lc = new Vector2(centre.X / ws, centre.Y / ws);
            var root = new SceneVisual { Transform = Matrix3x2.CreateScale(ws) };
            if (_fillControl)
            {
                // CONTROL: the same annulus as a plain even-odd FILL -- no stroker involved.
                // If this shows the same error, the stroke-to-fill geometry is exonerated and
                // the residual belongs to the rasterizer's treatment of curved edges.
                var grp = new GeometryGroup(FillRule.EvenOdd, new List<Geometry>
                {
                    new EllipseGeometry(lc, localR + halfLocal, localR + halfLocal),
                    new EllipseGeometry(lc, localR - halfLocal, localR - halfLocal),
                });
                root.Content.Add(new GeometryFill(grp, RgbaColor.FromBytes(0, 0, 0, 255)));
            }
            else
            {
                root.Content.Add(StrokeCircle(lc, localR, LineJoin.Miter, LineCap.Butt));
            }

            using var ctx = WgpuContext.Create();
            var rend = new WgpuSceneRenderer(ctx);
            byte[] px = rend.RenderToRgba(root, size, size, RgbaColor.FromBytes(255, 255, 255, 255));

            var byAngle = new double[12]; var nAngle = new int[12];
            var byBand = new double[6]; var nBand = new int[6];
            double worst = 0; double worstAng = 0, worstRad = 0;
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                double refc = Coverage(x, y, centre, r - half, r + half);
                if (refc <= 0.001 || refc >= 0.999) continue;
                double act = 1.0 - px[(y * size + x) * 4] / 255.0;
                double e = Math.Abs(act - refc);
                double dx = x + 0.5 - centre.X, dy = y + 0.5 - centre.Y;
                double ang = Math.Atan2(dy, dx); if (ang < 0) ang += 2 * Math.PI;
                double rad = Math.Sqrt(dx * dx + dy * dy) - r;         // signed: <0 inner wall
                int ai = Math.Min(11, (int)(ang / (2 * Math.PI) * 12));
                byAngle[ai] += e; nAngle[ai]++;
                int bi = Math.Clamp((int)((rad / half + 1.0) * 3.0), 0, 5);
                byBand[bi] += e; nBand[bi]++;
                if (e > worst) { worst = e; worstAng = ang * 180 / Math.PI; worstRad = rad; }
            }

            Console.WriteLine($"--- {(_fillControl ? "FILLED annulus (control)" : "stroke-miter")} world {ws}x  (r={r}, half={half}) ---");
            Console.Write("  mean err by angle (30deg bins): ");
            for (int i = 0; i < 12; i++) Console.Write($"{(nAngle[i] > 0 ? byAngle[i] / nAngle[i] : 0):F3} ");
            Console.WriteLine();
            Console.Write("  mean err by radial band (inner->outer): ");
            for (int i = 0; i < 6; i++) Console.Write($"{(nBand[i] > 0 ? byBand[i] / nBand[i] : 0):F3} ");
            Console.WriteLine();
            Console.WriteLine($"  worst {worst:F3} at angle {worstAng:F0}deg, radial offset {worstRad:+0.0;-0.0}px");
        }
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
