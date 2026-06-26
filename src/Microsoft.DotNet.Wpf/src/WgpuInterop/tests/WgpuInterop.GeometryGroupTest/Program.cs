// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Phase-1 geometry-group test. A GeometryGroup combines several geometries (of
// possibly different kinds) into one filled region under a shared fill rule. A
// rounded rectangle and an ellipse are grouped with EvenOdd, so the ellipse cuts
// a hole out of the rounded rectangle -- a frame:
//   * the band (inside the rounded rect, outside the ellipse) is filled,
//   * the hole (inside the ellipse) shows the background,
//   * the rounded-rect corner is still cut away,
//   * outside everything is background,
//   * and it round-trips through the protocol (children serialized recursively).
//

using System;
using System.Collections.Generic;
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

        // Rounded rect (8,8)-(56,40) r=8, with an ellipse hole at (32,24) rx=12 ry=8.
        Solid(px, 32, 12, 0, 0, 0, "band above the hole is filled");
        Solid(px, 12, 24, 0, 0, 0, "band beside the hole is filled");
        Solid(px, 32, 24, 255, 255, 255, "ellipse cuts a hole (background)");
        Solid(px, 9, 9, 255, 255, 255, "rounded-rect corner is still cut away");
        Solid(px, 2, 2, 255, 255, 255, "outside everything is background");

        bool aa = false;
        for (int i = 0; i < px.Length; i += 4) { int v = px[i]; if (v > 30 && v < 225) { aa = true; break; } }
        Report(aa, "edges are anti-aliased");

        byte[] batch = CompositionChannel.EncodeScene(root);
        var engine = new CompositionEngine();
        engine.ProcessBatch(batch);
        SceneVisual rebuilt = engine.Root ?? throw new InvalidOperationException("no root");
        byte[] viaProtocol = renderer.RenderToRgba(rebuilt, W, H, white);
        int diffs = 0;
        for (int i = 0; i < px.Length; i++) if (px[i] != viaProtocol[i]) diffs++;
        Report(diffs == 0, "geometry group is byte-identical after protocol round-trip");

        if (_failures == 0)
        {
            Console.WriteLine("GEOMETRY GROUP TEST PASSED: mixed geometries combine (EvenOdd hole) and survive the protocol.");
            return 0;
        }
        Console.Error.WriteLine($"GEOMETRY GROUP TEST FAILED: {_failures} check(s) wrong.");
        return 1;
    }

    private static SceneVisual BuildScene()
    {
        var root = new SceneVisual();
        var fill = new SceneVisual();
        var group = new GeometryGroup(FillRule.EvenOdd, new List<Geometry>
        {
            new RoundedRectangleGeometry(new Rect(8, 8, 48, 32), 8, 8),
            new EllipseGeometry(new Vector2(32, 24), 12, 8),
        });
        fill.Content.Add(new GeometryFill(group, RgbaColor.FromBytes(0, 0, 0, 255)));
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
