// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Phase-1 DrawGeometry test. A single GeometryDrawing both fills a geometry and
// strokes its outline (WPF's DrawGeometry(brush, pen, geometry)). An ellipse is
// drawn with a grey fill and a black stroke, so:
//   * the interior is the fill colour,
//   * the boundary is the stroke colour (the stroke is drawn over the fill),
//   * outside the stroke is the background,
//   * and the whole drawing round-trips through the protocol.
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

        // Ellipse centre (32,24), rx=20, ry=14; grey fill, black stroke (width 4).
        Solid(px, 32, 24, 200, 200, 200, "interior is the fill colour");
        Solid(px, 40, 24, 200, 200, 200, "interior (off-centre) is still the fill");
        Solid(px, 52, 24, 0, 0, 0, "boundary is the stroke colour (over the fill)");
        Solid(px, 60, 24, 255, 255, 255, "outside the stroke is the background");

        bool aa = false;
        for (int i = 0; i < px.Length; i += 4) { int v = px[i]; if (v > 30 && v < 190) { aa = true; break; } }
        Report(aa, "edges are anti-aliased");

        byte[] batch = CompositionChannel.EncodeScene(root);
        var engine = new CompositionEngine();
        engine.ProcessBatch(batch);
        SceneVisual rebuilt = engine.Root ?? throw new InvalidOperationException("no root");
        byte[] viaProtocol = renderer.RenderToRgba(rebuilt, W, H, white);
        int diffs = 0;
        for (int i = 0; i < px.Length; i++) if (px[i] != viaProtocol[i]) diffs++;
        Report(diffs == 0, "drawing is byte-identical after protocol round-trip");

        if (_failures == 0)
        {
            Console.WriteLine("DRAW GEOMETRY TEST PASSED: one primitive fills and strokes a geometry and survives the protocol.");
            return 0;
        }
        Console.Error.WriteLine($"DRAW GEOMETRY TEST FAILED: {_failures} check(s) wrong.");
        return 1;
    }

    private static SceneVisual BuildScene()
    {
        var root = new SceneVisual();
        var v = new SceneVisual();
        v.Content.Add(new GeometryDrawing(
            new EllipseGeometry(new Vector2(32, 24), 20, 14),
            new SolidColorBrush(RgbaColor.FromBytes(200, 200, 200, 255)), // fill
            new SolidColorBrush(RgbaColor.FromBytes(0, 0, 0, 255)),       // stroke
            new StrokeStyle(4)));
        root.Children.Add(v);
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

    private static bool Near(byte actual, byte expected) => Math.Abs(actual - expected) <= 8;
}
