// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Phase-1 stroked-path test. Strokes (not fills) two shapes and checks that the
// stroke-to-fill outline behaves like a pen:
//   * Diagonal open line (thickness 8, round cap): inked on the line, blank far
//     from it, and a round cap extends past the endpoint (a butt cap would not).
//   * Closed square (thickness 4): the border is inked but the centre is hollow
//     (background) -- the defining difference between a stroke and a fill.
//   * The 45-degree stroke edges are anti-aliased.
//   * The whole thing round-trips through the DUCE protocol byte-for-byte.
//

using System;
using System.Collections.Generic;
using System.Numerics;
using Microsoft.Wpf.Interop.WebGpu.Composition;
using Microsoft.Wpf.Interop.WebGpu.Composition.Protocol;

internal static class Program
{
    private const int W = 64;
    private const int H = 64;
    private static int _failures;

    private static int Main()
    {
        SceneVisual root = BuildScene();
        var background = RgbaColor.FromBytes(255, 255, 255, 255);

        using var ctx = WgpuContext.Create();
        using var renderer = new WgpuSceneRenderer(ctx);
        byte[] px = renderer.RenderToRgba(root, W, H, background);

        // Diagonal line from (12,12) to (52,52), thickness 8 (half-width 4), round cap.
        Solid(px, 32, 32, 0, 0, 0, "on the stroked line");
        Solid(px, 34, 30, 0, 0, 0, "within the stroke thickness");
        Solid(px, 40, 20, 255, 255, 255, "clear of the line");
        Solid(px, 10, 10, 0, 0, 0, "round cap extends past the endpoint");

        // Closed square stroked (thickness 4): border inked, centre hollow.
        Solid(px, 8, 48, 0, 0, 0, "square border is stroked");
        Solid(px, 16, 48, 255, 255, 255, "square centre is hollow (stroke, not fill)");

        // Anti-aliasing somewhere on the 45-degree stroke edges.
        bool aa = false;
        for (int i = 0; i < px.Length; i += 4) { int v = px[i]; if (v > 30 && v < 225) { aa = true; break; } }
        Report(aa, "stroke edges are anti-aliased");

        // Protocol round-trip.
        byte[] batch = CompositionChannel.EncodeScene(root);
        var engine = new CompositionEngine();
        engine.ProcessBatch(batch);
        SceneVisual rebuilt = engine.Root ?? throw new InvalidOperationException("no root");
        byte[] viaProtocol = renderer.RenderToRgba(rebuilt, W, H, background);

        Console.WriteLine($"command batch = {batch.Length} bytes");
        int diffs = 0;
        for (int i = 0; i < px.Length; i++) if (px[i] != viaProtocol[i]) diffs++;
        Report(diffs == 0, diffs == 0 ? "stroke is byte-identical after protocol round-trip" : $"{diffs} bytes differ after round-trip");

        if (_failures == 0)
        {
            Console.WriteLine("STROKE TEST PASSED: strokes render with thickness, round caps, hollow interiors, AA and survive the protocol.");
            return 0;
        }
        Console.Error.WriteLine($"STROKE TEST FAILED: {_failures} check(s) wrong.");
        return 1;
    }

    private static SceneVisual BuildScene()
    {
        var root = new SceneVisual();
        var content = new SceneVisual();
        var black = RgbaColor.FromBytes(0, 0, 0, 255);

        var line = new PathFigure(new Vector2(12, 12)) { Closed = false };
        line.Segments.Add(new LineSegment(new Vector2(52, 52)));
        content.Content.Add(new GeometryStroke(
            new PathGeometry(FillRule.NonZero, new List<PathFigure> { line }),
            black, new StrokeStyle(8, LineCap.Round)));

        var square = new PathFigure(new Vector2(8, 40)) { Closed = true };
        square.Segments.Add(new LineSegment(new Vector2(24, 40)));
        square.Segments.Add(new LineSegment(new Vector2(24, 56)));
        square.Segments.Add(new LineSegment(new Vector2(8, 56)));
        content.Content.Add(new GeometryStroke(
            new PathGeometry(FillRule.NonZero, new List<PathFigure> { square }),
            black, new StrokeStyle(4)));

        root.Children.Add(content);
        return root;
    }

    private static void Solid(byte[] px, int x, int y, byte r, byte g, byte b, string what)
    {
        int i = (y * W + x) * 4;
        bool ok = Near(px[i], r) && Near(px[i + 1], g) && Near(px[i + 2], b);
        Console.WriteLine($"  [{(ok ? "ok " : "BAD")}] ({x},{y}) {what}: got [{px[i]},{px[i + 1]},{px[i + 2]}] expected ~[{r},{g},{b}]");
        if (!ok) _failures++;
    }

    private static void Report(bool ok, string what)
    {
        Console.WriteLine($"  [{(ok ? "ok " : "BAD")}] {what}");
        if (!ok) _failures++;
    }

    private static bool Near(byte actual, byte expected) => Math.Abs(actual - expected) <= 6;
}
