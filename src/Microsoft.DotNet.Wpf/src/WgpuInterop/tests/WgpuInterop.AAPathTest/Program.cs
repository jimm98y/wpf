// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Phase-1 anti-aliased path-fill test. Exercises the arbitrary-path pipeline:
// figures are flattened, rasterized to an analytic-AA coverage mask on the CPU,
// uploaded as an R8 texture and composited on the GPU.
//
//   * Even-odd ring: two concentric squares -> filled ring with a hollow centre
//     (proves the fill rule and multi-contour holes).
//   * Triangle with a 45-degree edge: interior is filled, exterior is not, and
//     a pixel sitting exactly on the diagonal has intermediate coverage
//     (proves anti-aliasing -- a non-AA fill would be hard black/white).
//   * Round-trip: the path survives the DUCE protocol byte-for-byte.
//

using System;
using System.Collections.Generic;
using System.Numerics;
using Microsoft.Wpf.Interop.WebGpu.Composition;
using Microsoft.Wpf.Interop.WebGpu.Composition.Protocol;

internal static class Program
{
    private const int W = 96;
    private const int H = 64;
    private static int _failures;

    private static int Main()
    {
        SceneVisual root = BuildScene();
        var background = RgbaColor.FromBytes(255, 255, 255, 255);

        using var ctx = WgpuContext.Create();
        var renderer = new WgpuSceneRenderer(ctx);
        byte[] px = renderer.RenderToRgba(root, W, H, background);

        // Even-odd ring (outer square device 4..28, inner hole 12..20).
        Solid(px, 50, 2, 255, 255, 255, "background");
        Solid(px, 6, 16, 0, 0, 0, "ring band (even-odd filled)");
        Solid(px, 16, 16, 255, 255, 255, "ring centre (even-odd hole)");

        // Triangle (right angle at device (80,44); hypotenuse dx+dy=84).
        Solid(px, 74, 40, 0, 0, 0, "triangle interior");
        Solid(px, 44, 8, 255, 255, 255, "triangle exterior");

        // Anti-aliasing: a pixel whose centre sits on the 45-degree edge
        // (device x+y=83) must be partial coverage, not hard black/white.
        Intermediate(px, 60, 23, "triangle diagonal (anti-aliased edge)");

        // Protocol round-trip must be byte-identical.
        byte[] batch = CompositionChannel.EncodeScene(root);
        var engine = new CompositionEngine();
        engine.ProcessBatch(batch);
        SceneVisual rebuilt = engine.Root ?? throw new InvalidOperationException("no root");
        byte[] viaProtocol = renderer.RenderToRgba(rebuilt, W, H, background);

        Console.WriteLine($"command batch = {batch.Length} bytes");
        int diffs = 0;
        for (int i = 0; i < px.Length; i++) if (px[i] != viaProtocol[i]) diffs++;
        if (diffs == 0) Console.WriteLine("  [ok ] path is byte-identical after protocol round-trip");
        else { Console.WriteLine($"  [BAD] {diffs} bytes differ after round-trip"); _failures++; }

        if (_failures == 0)
        {
            Console.WriteLine("AA PATH TEST PASSED: arbitrary paths fill with the fill rule, anti-alias, and survive the protocol.");
            return 0;
        }
        Console.Error.WriteLine($"AA PATH TEST FAILED: {_failures} check(s) wrong.");
        return 1;
    }

    private static SceneVisual BuildScene()
    {
        var root = new SceneVisual();
        var black = RgbaColor.FromBytes(0, 0, 0, 255);

        // Even-odd ring at offset (4,4): outer 0..24, inner 8..16 (local).
        var ring = new SceneVisual { Offset = new Vector2(4, 4) };
        ring.Content.Add(new GeometryFill(
            new PathGeometry(FillRule.EvenOdd, new List<PathFigure>
            {
                Square(0, 0, 24),
                Square(8, 8, 8),
            }),
            black));
        root.Children.Add(ring);

        // Right triangle at offset (40,4): local (0,40),(40,40),(40,0).
        var tri = new SceneVisual { Offset = new Vector2(40, 4) };
        var figure = new PathFigure(new Vector2(0, 40));
        figure.Segments.Add(new LineSegment(new Vector2(40, 40)));
        figure.Segments.Add(new LineSegment(new Vector2(40, 0)));
        tri.Content.Add(new GeometryFill(
            new PathGeometry(FillRule.NonZero, new List<PathFigure> { figure }),
            black));
        root.Children.Add(tri);

        return root;
    }

    private static PathFigure Square(float x, float y, float size)
    {
        var f = new PathFigure(new Vector2(x, y));
        f.Segments.Add(new LineSegment(new Vector2(x + size, y)));
        f.Segments.Add(new LineSegment(new Vector2(x + size, y + size)));
        f.Segments.Add(new LineSegment(new Vector2(x, y + size)));
        return f;
    }

    private static void Solid(byte[] px, int x, int y, byte r, byte g, byte b, string what)
    {
        int i = (y * W + x) * 4;
        bool ok = Near(px[i], r) && Near(px[i + 1], g) && Near(px[i + 2], b);
        Console.WriteLine($"  [{(ok ? "ok " : "BAD")}] ({x},{y}) {what}: got [{px[i]},{px[i + 1]},{px[i + 2]}] expected ~[{r},{g},{b}]");
        if (!ok) _failures++;
    }

    private static void Intermediate(byte[] px, int x, int y, string what)
    {
        int i = (y * W + x) * 4;
        int v = px[i]; // grey on black/white, any channel works
        bool ok = v > 30 && v < 225;
        Console.WriteLine($"  [{(ok ? "ok " : "BAD")}] ({x},{y}) {what}: got {v} (expected strictly between 30 and 225)");
        if (!ok) _failures++;
    }

    private static bool Near(byte actual, byte expected) => Math.Abs(actual - expected) <= 4;
}
