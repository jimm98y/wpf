// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Phase-1 ellipse test. An ellipse is realized as four quarter-arcs and filled
// through the analytic-AA coverage path. A black ellipse centred at (32,24) with
// radii (24,16) -- wider than it is tall -- is checked for:
//   * a filled interior,
//   * the bounding-box corners cut away (background), since they're outside the
//     ellipse,
//   * the wide/tall asymmetry: a point far on the x-axis (within rx) is filled
//     while the same distance on the y-axis (beyond ry) is background,
//   * an anti-aliased edge,
//   * and a byte-identical protocol round-trip.
//

using System;
using System.Numerics;
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

        // Ellipse centre (32,24), rx=24, ry=16.
        Solid(px, 32, 24, 0, 0, 0, "interior is filled");
        Solid(px, 9, 9, 255, 255, 255, "bounding-box corner is outside the ellipse (background)");

        // Asymmetry: 18px from the centre is inside on x (rx=24) but outside on y (ry=16).
        Solid(px, 50, 24, 0, 0, 0, "wide axis: 18px out on x is still inside");
        Solid(px, 32, 42, 255, 255, 255, "tall axis: 18px out on y is outside");
        // ...and the matching opposites confirm it's an ellipse, not a circle.
        Solid(px, 32, 14, 0, 0, 0, "10px out on y is inside");
        Solid(px, 60, 24, 255, 255, 255, "28px out on x is outside");

        bool aa = false;
        for (int i = 0; i < px.Length; i += 4) { int v = px[i]; if (v > 30 && v < 225) { aa = true; break; } }
        Report(aa, "the ellipse edge is anti-aliased");

        byte[] batch = CompositionChannel.EncodeScene(root);
        var engine = new CompositionEngine();
        engine.ProcessBatch(batch);
        SceneVisual rebuilt = engine.Root ?? throw new InvalidOperationException("no root");
        byte[] viaProtocol = renderer.RenderToRgba(rebuilt, W, H, white);
        int diffs = 0;
        for (int i = 0; i < px.Length; i++) if (px[i] != viaProtocol[i]) diffs++;
        Report(diffs == 0, "ellipse is byte-identical after protocol round-trip");

        if (_failures == 0)
        {
            Console.WriteLine("ELLIPSE TEST PASSED: ellipse geometry renders with anti-aliasing and survives the protocol.");
            return 0;
        }
        Console.Error.WriteLine($"ELLIPSE TEST FAILED: {_failures} check(s) wrong.");
        return 1;
    }

    private static SceneVisual BuildScene()
    {
        var root = new SceneVisual();
        var fill = new SceneVisual();
        fill.Content.Add(new GeometryFill(
            new EllipseGeometry(new Vector2(32, 24), 24, 16),
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
