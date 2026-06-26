// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Phase-1 arbitrary-clip test. A visual can be clipped to any geometry (not just
// an axis-aligned rectangle): the subtree is rendered into a layer and masked by
// the clip geometry's coverage. A black rectangle that would cover most of the
// canvas is clipped to a triangle, so pixels inside the triangle are painted and
// pixels outside it (still inside the rectangle) are masked away -- with an
// anti-aliased clip edge. The clip geometry round-trips through the protocol.
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
        var white = RgbaColor.FromBytes(255, 255, 255, 255);

        using var ctx = WgpuContext.Create();
        using var renderer = new WgpuSceneRenderer(ctx);
        byte[] px = renderer.RenderToRgba(root, W, H, white);

        // Triangle clip: apex (32,8), base (8,56)-(56,56).
        Solid(px, 32, 40, 0, 0, 0, "inside the clip triangle (painted)");
        Solid(px, 32, 12, 0, 0, 0, "near the apex, inside (painted)");
        Solid(px, 12, 12, 255, 255, 255, "outside the clip (masked, though the rect covers it)");
        Solid(px, 52, 15, 255, 255, 255, "outside the clip on the other side (masked)");

        bool aa = false;
        for (int i = 0; i < px.Length; i += 4) { int v = px[i]; if (v > 30 && v < 225) { aa = true; break; } }
        Report(aa, "the clip edge is anti-aliased");

        byte[] batch = CompositionChannel.EncodeScene(root);
        var engine = new CompositionEngine();
        engine.ProcessBatch(batch);
        SceneVisual rebuilt = engine.Root ?? throw new InvalidOperationException("no root");
        byte[] viaProtocol = renderer.RenderToRgba(rebuilt, W, H, white);
        int diffs = 0;
        for (int i = 0; i < px.Length; i++) if (px[i] != viaProtocol[i]) diffs++;
        Report(diffs == 0, "clip geometry is byte-identical after protocol round-trip");

        if (_failures == 0)
        {
            Console.WriteLine("CLIP TEST PASSED: a visual is masked to arbitrary clip geometry with an anti-aliased edge.");
            return 0;
        }
        Console.Error.WriteLine($"CLIP TEST FAILED: {_failures} check(s) wrong.");
        return 1;
    }

    private static SceneVisual BuildScene()
    {
        var root = new SceneVisual();

        var triangle = new PathFigure(new Vector2(32, 8)) { Closed = true };
        triangle.Segments.Add(new LineSegment(new Vector2(56, 56)));
        triangle.Segments.Add(new LineSegment(new Vector2(8, 56)));

        var clipped = new SceneVisual
        {
            ClipGeometry = new PathGeometry(FillRule.NonZero, new List<PathFigure> { triangle }),
        };
        // A rectangle that covers nearly the whole canvas; only the triangle shows.
        clipped.Content.Add(new GeometryFill(new RectangleGeometry(new Rect(8, 8, 48, 48)), RgbaColor.FromBytes(0, 0, 0, 255)));
        root.Children.Add(clipped);

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
