// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Phase-1 rounded-rectangle test. A rounded rectangle is realized as a path with
// quarter-ellipse corners and filled through the analytic-AA coverage path. A
// black rounded rect (radius 12) fills a canvas, and we check that:
//   * the interior and the straight edges are solid,
//   * the bounding-box corners are cut away (rounded), so they show the
//     background, while points inside the corner arc are filled,
//   * the curved corner is anti-aliased,
//   * and it round-trips through the protocol.
//

using System;
using Microsoft.Wpf.Interop.WebGpu.Composition;
using Microsoft.Wpf.Interop.WebGpu.Composition.Protocol;

internal static class Program
{
    private const int W = 64;
    private const int H = 48;
    private static int _failures;

    private static int Main()
    {
        SceneVisual root = BuildScene();
        var white = RgbaColor.FromBytes(255, 255, 255, 255);

        using var ctx = WgpuContext.Create();
        using var renderer = new WgpuSceneRenderer(ctx);
        byte[] px = renderer.RenderToRgba(root, W, H, white);

        // Rect (8,8)-(56,40), corner radius 12 (corner centre TL = (20,20)).
        Solid(px, 32, 24, 0, 0, 0, "interior is filled");
        Solid(px, 32, 9, 0, 0, 0, "straight top edge is filled");
        Solid(px, 9, 24, 0, 0, 0, "straight left edge is filled");
        Solid(px, 16, 16, 0, 0, 0, "inside the corner arc is filled");
        Solid(px, 9, 9, 255, 255, 255, "bounding-box corner is rounded away (background)");
        Solid(px, 54, 38, 255, 255, 255, "opposite bounding-box corner is rounded away");

        bool aa = false;
        for (int i = 0; i < px.Length; i += 4) { int v = px[i]; if (v > 30 && v < 225) { aa = true; break; } }
        Report(aa, "the rounded corner is anti-aliased");

        byte[] batch = CompositionChannel.EncodeScene(root);
        var engine = new CompositionEngine();
        engine.ProcessBatch(batch);
        SceneVisual rebuilt = engine.Root ?? throw new InvalidOperationException("no root");
        byte[] viaProtocol = renderer.RenderToRgba(rebuilt, W, H, white);
        int diffs = 0;
        for (int i = 0; i < px.Length; i++) if (px[i] != viaProtocol[i]) diffs++;
        Report(diffs == 0, "rounded rectangle is byte-identical after protocol round-trip");

        if (_failures == 0)
        {
            Console.WriteLine("ROUNDED RECT TEST PASSED: rounded corners render with anti-aliasing and survive the protocol.");
            return 0;
        }
        Console.Error.WriteLine($"ROUNDED RECT TEST FAILED: {_failures} check(s) wrong.");
        return 1;
    }

    private static SceneVisual BuildScene()
    {
        var root = new SceneVisual();
        var fill = new SceneVisual();
        fill.Content.Add(new GeometryFill(
            new RoundedRectangleGeometry(new Rect(8, 8, 48, 32), 12, 12),
            RgbaColor.FromBytes(0, 0, 0, 255)));
        root.Children.Add(fill);
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
