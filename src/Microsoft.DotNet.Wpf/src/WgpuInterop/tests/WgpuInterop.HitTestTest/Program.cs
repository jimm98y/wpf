// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// GPU hit-test test. Two overlapping filled rectangles (each its own visual, drawn
// back-to-front) plus a text run are built, round-tripped through the composition
// protocol (which assigns each visual its id/handle), then queried with the GPU
// visual-id readback (WgpuSceneRenderer.HitTest):
//   * a point only in the back rect returns the back visual,
//   * a point in the overlap returns the FRONT visual (topmost-wins),
//   * a point outside both shapes' coverage returns 0 (no hit) even inside the
//     bounding boxes (the rects don't overlap there),
//   * a point in the text run's bounds returns the text visual,
//   * an out-of-bounds query returns 0.
//

using System;
using System.Numerics;
using Microsoft.Wpf.Interop.WebGpu.Composition;
using Microsoft.Wpf.Interop.WebGpu.Composition.Protocol;

internal static class Program
{
    private const int W = 200;
    private const int H = 160;
    private static int _failures;

    private static int Main()
    {
        SceneVisual root = BuildScene();

        // Round-trip so every visual gets its protocol handle == hit-test id.
        byte[] batch = CompositionChannel.EncodeScene(root);
        var engine = new CompositionEngine();
        engine.ProcessBatch(batch);
        SceneVisual rebuilt = engine.Root ?? throw new InvalidOperationException("no root");

        // Recover the ids the engine assigned (back rect, front rect, text), in child order.
        uint backId = rebuilt.Children[0].Id;
        uint frontId = rebuilt.Children[1].Id;
        uint textId = rebuilt.Children[2].Id;
        Console.WriteLine($"ids: back={backId} front={frontId} text={textId}");

        using var ctx = WgpuContext.Create();
        using var renderer = new WgpuSceneRenderer(ctx);

        // Back rect: (20,20)-(110,90). Front rect: (80,60)-(170,130). Overlap: (80,60)-(110,90).
        Expect(renderer.HitTest(rebuilt, 40, 40, W, H), backId, "back-only point hits back rect");
        Expect(renderer.HitTest(rebuilt, 150, 110, W, H), frontId, "front-only point hits front rect");
        Expect(renderer.HitTest(rebuilt, 95, 75, W, H), frontId, "overlap point hits FRONT (topmost)");
        Expect(renderer.HitTest(rebuilt, 5, 5, W, H), 0u, "empty corner hits nothing");
        Expect(renderer.HitTest(rebuilt, 150, 40, W, H), 0u, "gap between rects hits nothing");

        // Text run drawn at origin (30,145), em 12 -> bounds roughly (30,133)-(30+5*12,151).
        Expect(renderer.HitTest(rebuilt, 45, 143, W, H), textId, "text-bounds point hits the text visual");

        Expect(renderer.HitTest(rebuilt, -1, 10, W, H), 0u, "out-of-bounds query returns 0");

        // Per-frame retained id buffer: the first HitTest(root,...) rendered it; subsequent
        // point-only queries reuse it (pure readback, no re-walk) and must agree.
        Expect(renderer.HitTest(40, 40), backId, "cached readback: back-only point");
        Expect(renderer.HitTest(95, 75), frontId, "cached readback: overlap -> front");
        Expect(renderer.HitTest(5, 5), 0u, "cached readback: empty corner");

        Console.WriteLine(_failures == 0 ? "ALL OK" : $"{_failures} FAILURE(S)");
        return _failures == 0 ? 0 : 1;
    }

    private static SceneVisual BuildScene()
    {
        var root = new SceneVisual();

        var back = new SceneVisual();
        back.Content.Add(new GeometryFill(
            new RectangleGeometry(new Rect(20, 20, 90, 70)),
            RgbaColor.FromBytes(200, 40, 40, 255)));
        root.Children.Add(back);

        var front = new SceneVisual();
        front.Content.Add(new GeometryFill(
            new RectangleGeometry(new Rect(80, 60, 90, 70)),
            RgbaColor.FromBytes(40, 80, 200, 255)));
        root.Children.Add(front);

        var text = new SceneVisual();
        text.Content.Add(new GlyphRunDraw("Hello", new Vector2(30, 145), 12f,
            RgbaColor.FromBytes(0, 0, 0, 255)));
        root.Children.Add(text);

        return root;
    }

    private static void Expect(uint actual, uint expected, string what)
    {
        bool ok = actual == expected;
        Console.WriteLine($"  [{(ok ? "ok " : "BAD")}] {what}: got id={actual} expected {expected}");
        if (!ok) _failures++;
    }
}
