// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Phase-1 brush-on-coverage test: gradient and image brushes now work on
// coverage geometry (filled paths AND strokes), not just solid colours, by
// baking the brush per coverage texel. Also confirms dash runs honour the cap
// style (round caps per dash).
//   * Gradient on a filled path: colour varies red->blue across the shape.
//   * Gradient on a stroke: the stroked band itself goes red->blue.
//   * Image on a filled path: a 2x2 checker maps across the geometry.
//   * Round-capped dashes: a dash's rounded end extends past its nominal length,
//     while a true gap (beyond both caps) stays blank.
//   * Byte-identical protocol round-trip.
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

        // Gradient on a filled path (square 4..44 x, 2..12 y; red@left -> blue@right).
        Reddish(px, 7, 7, "gradient fill-path: red end");
        Bluish(px, 41, 7, "gradient fill-path: blue end");

        // Gradient on a stroke (band y 24..34; red@left -> blue@right).
        Reddish(px, 8, 29, "gradient stroke: red end");
        Bluish(px, 40, 29, "gradient stroke: blue end");

        // Image on a filled path (2x2 checker over square 4..28 x, 40..64 y). Image brushes are
        // bilinear-sampled (matching WPF), so sample near the clamped texel corners, not mid-texel.
        Solid(px, 6, 42, 255, 0, 0, "image path: top-left red");
        Solid(px, 22, 58, 255, 255, 0, "image path: bottom-right yellow");

        // Round-capped dashes (line y=76, pattern 10 on / 16 off, half-width 4).
        Solid(px, 8, 76, 0, 0, 0, "dash body");
        Solid(px, 16, 76, 0, 0, 0, "round cap extends the dash past x=14");
        Solid(px, 22, 76, 255, 255, 255, "true gap (beyond both round caps) is blank");

        bool aa = false;
        for (int i = 0; i < px.Length; i += 4) { int v = px[i]; if (v > 30 && v < 225) { aa = true; break; } }
        Report(aa, "edges are anti-aliased");

        byte[] batch = CompositionChannel.EncodeScene(root);
        var engine = new CompositionEngine();
        engine.ProcessBatch(batch);
        SceneVisual rebuilt = engine.Root ?? throw new InvalidOperationException("no root");
        byte[] viaProtocol = renderer.RenderToRgba(rebuilt, W, H, background);

        Console.WriteLine($"command batch = {batch.Length} bytes");
        int diffs = 0;
        for (int i = 0; i < px.Length; i++) if (px[i] != viaProtocol[i]) diffs++;
        Report(diffs == 0, diffs == 0 ? "byte-identical after protocol round-trip" : $"{diffs} bytes differ");

        if (_failures == 0)
        {
            Console.WriteLine("BRUSH STROKE TEST PASSED: gradient/image brushes paint paths and strokes, and dash caps work.");
            return 0;
        }
        Console.Error.WriteLine($"BRUSH STROKE TEST FAILED: {_failures} check(s) wrong.");
        return 1;
    }

    private static SceneVisual BuildScene()
    {
        var root = new SceneVisual();
        var content = new SceneVisual();
        var black = RgbaColor.FromBytes(0, 0, 0, 255);

        GradientStop[] redBlue =
        {
            new(0f, RgbaColor.FromBytes(255, 0, 0, 255)),
            new(1f, RgbaColor.FromBytes(0, 0, 255, 255)),
        };

        // Gradient-filled path square.
        content.Content.Add(new GeometryFill(
            ClosedRect(4, 2, 40, 10),
            new LinearGradientBrush(new Vector2(4, 2), new Vector2(44, 2), redBlue)));

        // Gradient-stroked line.
        var strokeLine = new PathFigure(new Vector2(4, 29)) { Closed = false };
        strokeLine.Segments.Add(new LineSegment(new Vector2(44, 29)));
        content.Content.Add(new GeometryStroke(
            new PathGeometry(FillRule.NonZero, new List<PathFigure> { strokeLine }),
            new LinearGradientBrush(new Vector2(4, 29), new Vector2(44, 29), redBlue),
            new StrokeStyle(10, LineCap.Round)));

        // Image-filled path square (2x2 checker: TL red, TR green, BL blue, BR yellow).
        byte[] checker = { 255, 0, 0, 255, 0, 255, 0, 255, 0, 0, 255, 255, 255, 255, 0, 255 };
        content.Content.Add(new GeometryFill(
            ClosedRect(4, 40, 24, 24),
            new ImageBrush(checker, 2, 2)));

        // Round-capped dashed line.
        var dashLine = new PathFigure(new Vector2(4, 76)) { Closed = false };
        dashLine.Segments.Add(new LineSegment(new Vector2(80, 76)));
        content.Content.Add(new GeometryStroke(
            new PathGeometry(FillRule.NonZero, new List<PathFigure> { dashLine }),
            black, new StrokeStyle(8, LineCap.Round, LineJoin.Round, 10, new double[] { 10, 16 }, 0)));

        root.Children.Add(content);
        return root;
    }

    private static PathGeometry ClosedRect(float x, float y, float w, float h)
    {
        var f = new PathFigure(new Vector2(x, y)) { Closed = true };
        f.Segments.Add(new LineSegment(new Vector2(x + w, y)));
        f.Segments.Add(new LineSegment(new Vector2(x + w, y + h)));
        f.Segments.Add(new LineSegment(new Vector2(x, y + h)));
        return new PathGeometry(FillRule.NonZero, new List<PathFigure> { f });
    }

    private static void Reddish(byte[] px, int x, int y, string what)
    {
        int i = (y * W + x) * 4;
        // sRGB-space stop interpolation (WPF default) makes the ends fall off faster than a linear lerp.
        bool ok = px[i] > 180 && px[i + 2] < 80;
        Console.WriteLine($"  [{(ok ? "ok " : "BAD")}] ({x},{y}) {what}: got [{px[i]},{px[i + 1]},{px[i + 2]}]");
        if (!ok) _failures++;
    }

    private static void Bluish(byte[] px, int x, int y, string what)
    {
        int i = (y * W + x) * 4;
        // sRGB-space stop interpolation (WPF default) makes the ends fall off faster than a linear lerp.
        bool ok = px[i + 2] > 180 && px[i] < 80;
        Console.WriteLine($"  [{(ok ? "ok " : "BAD")}] ({x},{y}) {what}: got [{px[i]},{px[i + 1]},{px[i + 2]}]");
        if (!ok) _failures++;
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
