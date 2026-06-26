// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Phase-1 styled-stroke test: pen styling beyond the round-everything default.
//   * Square cap: the cap is a rectangle, so a corner pixel past the endpoint is
//     inked (a round cap would clip it away).
//   * Miter vs bevel join at the same right-angle corner: the miter extends a
//     sharp apex (apex pixel inked); the bevel cuts it (apex pixel blank).
//   * Dashes: an 8-on/8-off pattern alternates ink and gap along a line.
//   * The whole thing round-trips through the DUCE protocol byte-for-byte.
//

using System;
using System.Collections.Generic;
using System.Numerics;
using Microsoft.Wpf.Interop.WebGpu.Composition;
using Microsoft.Wpf.Interop.WebGpu.Composition.Protocol;

internal static class Program
{
    private const int W = 96;
    private const int H = 96;
    private static int _failures;

    private static int Main()
    {
        SceneVisual root = BuildScene();
        var background = RgbaColor.FromBytes(255, 255, 255, 255);

        using var ctx = WgpuContext.Create();
        using var renderer = new WgpuSceneRenderer(ctx);
        byte[] px = renderer.RenderToRgba(root, W, H, background);

        // Square cap (line y=16, ends at x=10 and x=40, half-width 4).
        Solid(px, 25, 16, 0, 0, 0, "square-cap line body");
        Solid(px, 25, 4, 255, 255, 255, "clear above the line");
        Solid(px, 43, 13, 0, 0, 0, "square cap corner past endpoint (round would clip)");

        // Miter join at corner (20,40): sharp apex toward (16,36).
        Solid(px, 20, 50, 0, 0, 0, "miter L arm");
        Solid(px, 17, 37, 0, 0, 0, "miter apex is filled");

        // Bevel join at corner (60,40): apex cut off.
        Solid(px, 60, 50, 0, 0, 0, "bevel L arm");
        Solid(px, 59, 39, 0, 0, 0, "bevel fills near the corner");
        Solid(px, 57, 37, 255, 255, 255, "bevel cuts the apex (blank)");

        // Dashes (line y=80, pattern 8 on / 8 off from x=10).
        Solid(px, 13, 80, 0, 0, 0, "dash 1 (on)");
        Solid(px, 22, 80, 255, 255, 255, "gap 1 (off)");
        Solid(px, 30, 80, 0, 0, 0, "dash 2 (on)");
        Solid(px, 38, 80, 255, 255, 255, "gap 2 (off)");

        bool aa = false;
        for (int i = 0; i < px.Length; i += 4) { int v = px[i]; if (v > 30 && v < 225) { aa = true; break; } }
        Report(aa, "stroke edges are anti-aliased");

        byte[] batch = CompositionChannel.EncodeScene(root);
        var engine = new CompositionEngine();
        engine.ProcessBatch(batch);
        SceneVisual rebuilt = engine.Root ?? throw new InvalidOperationException("no root");
        byte[] viaProtocol = renderer.RenderToRgba(rebuilt, W, H, background);

        Console.WriteLine($"command batch = {batch.Length} bytes");
        int diffs = 0;
        for (int i = 0; i < px.Length; i++) if (px[i] != viaProtocol[i]) diffs++;
        Report(diffs == 0, diffs == 0 ? "styled stroke is byte-identical after round-trip" : $"{diffs} bytes differ");

        if (_failures == 0)
        {
            Console.WriteLine("STYLED STROKE TEST PASSED: square caps, miter/bevel joins and dashes render correctly and survive the protocol.");
            return 0;
        }
        Console.Error.WriteLine($"STYLED STROKE TEST FAILED: {_failures} check(s) wrong.");
        return 1;
    }

    private static SceneVisual BuildScene()
    {
        var root = new SceneVisual();
        var content = new SceneVisual();
        var black = RgbaColor.FromBytes(0, 0, 0, 255);

        content.Content.Add(new GeometryStroke(
            Path((10, 16), (40, 16)), black, new StrokeStyle(8, LineCap.Square)));

        content.Content.Add(new GeometryStroke(
            Path((20, 60), (20, 40), (40, 40)), black, new StrokeStyle(8, LineCap.Butt, LineJoin.Miter)));

        content.Content.Add(new GeometryStroke(
            Path((60, 60), (60, 40), (80, 40)), black, new StrokeStyle(8, LineCap.Butt, LineJoin.Bevel)));

        content.Content.Add(new GeometryStroke(
            Path((10, 80), (86, 80)), black,
            new StrokeStyle(6, LineCap.Butt, LineJoin.Miter, 10, new double[] { 8, 8 }, 0)));

        root.Children.Add(content);
        return root;
    }

    private static PathGeometry Path(params (float x, float y)[] points)
    {
        var figure = new PathFigure(new Vector2(points[0].x, points[0].y)) { Closed = false };
        for (int i = 1; i < points.Length; i++)
            figure.Segments.Add(new LineSegment(new Vector2(points[i].x, points[i].y)));
        return new PathGeometry(FillRule.NonZero, new List<PathFigure> { figure });
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
