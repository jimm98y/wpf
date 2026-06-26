// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Phase-1 group-opacity test. A visual with opacity < 1 that draws more than one
// thing is composited as a single offscreen layer: its content is drawn opaque
// into a texture, then that texture is blended once at the group opacity. The
// observable consequence is that overlapping children do NOT double-blend.
//
// Two black rectangles overlap under a 50%-opacity parent. With correct layer
// compositing every covered pixel -- single-covered or overlapping -- is the
// same 50% grey. The old per-primitive opacity would darken the overlap (each
// rectangle blended separately), so the overlap is the discriminating check.
//

using System;
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

        int single1 = Lum(px, 15, 15); // only rectangle A
        int single2 = Lum(px, 50, 50); // only rectangle B
        int overlap = Lum(px, 32, 32);  // both rectangles
        int bg = Lum(px, 60, 4);
        Console.WriteLine($"background={bg}, singleA={single1}, singleB={single2}, overlap={overlap}");

        Check(Math.Abs(bg - 255) <= 4, "background is white");
        Check(Math.Abs(single1 - 128) <= 6, "single-covered region is 50% grey (A)");
        Check(Math.Abs(single2 - 128) <= 6, "single-covered region is 50% grey (B)");
        Check(Math.Abs(overlap - 128) <= 6, "overlap is the SAME 50% grey (composited once, not double-blended)");
        Check(Math.Abs(overlap - single1) <= 6, "overlap matches single coverage (group composited as a unit)");

        // The scene (with the opacity command) round-trips through the protocol.
        byte[] batch = CompositionChannel.EncodeScene(root);
        var engine = new CompositionEngine();
        engine.ProcessBatch(batch);
        SceneVisual rebuilt = engine.Root ?? throw new InvalidOperationException("no root");
        byte[] viaProtocol = renderer.RenderToRgba(rebuilt, W, H, background);
        int diffs = 0;
        for (int i = 0; i < px.Length; i++) if (px[i] != viaProtocol[i]) diffs++;
        Check(diffs == 0, "byte-identical after protocol round-trip");

        if (_failures == 0)
        {
            Console.WriteLine("GROUP OPACITY TEST PASSED: a translucent group composites as one layer; overlaps don't double-blend.");
            return 0;
        }
        Console.Error.WriteLine($"GROUP OPACITY TEST FAILED: {_failures} check(s) wrong.");
        return 1;
    }

    private static SceneVisual BuildScene()
    {
        var root = new SceneVisual();

        // One visual at 50% opacity drawing two overlapping black rectangles.
        var group = new SceneVisual { Opacity = 0.5 };
        var black = RgbaColor.FromBytes(0, 0, 0, 255);
        group.Content.Add(new GeometryFill(new RectangleGeometry(new Rect(10, 10, 30, 30)), black)); // (10,10)-(40,40)
        group.Content.Add(new GeometryFill(new RectangleGeometry(new Rect(25, 25, 30, 30)), black)); // (25,25)-(55,55)
        root.Children.Add(group);

        return root;
    }

    private static int Lum(byte[] px, int x, int y) => px[(y * W + x) * 4]; // grey, so red channel suffices

    private static void Check(bool ok, string what)
    {
        Console.WriteLine($"  [{(ok ? "ok " : "BAD")}] {what}");
        if (!ok) _failures++;
    }
}
