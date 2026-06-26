// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Phase-1 opacity-mask test. A visual's OpacityMask is a brush whose alpha
// modulates the subtree per pixel. A solid black rectangle covers the canvas
// with a horizontal gradient opacity mask that fades alpha 1 -> 0, so the result
// over a white background fades left (opaque black) to right (transparent):
//   * left is solid black, the middle is ~50% grey, the right is ~background,
//   * and the mask brush round-trips through the protocol.
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

        int left = Lum(px, 4, 24);
        int mid = Lum(px, 32, 24);
        int right = Lum(px, 60, 24);
        Console.WriteLine($"left={left}, mid={mid}, right={right}");

        Check(left < 40, "left is opaque (mask alpha ~1 -> black)");
        Check(Math.Abs(mid - 128) <= 24, "middle is ~50% (mask alpha ~0.5 -> grey)");
        Check(right > 215, "right is faded out (mask alpha ~0 -> background)");
        Check(left < mid && mid < right, "the mask produces a monotonic fade");

        byte[] batch = CompositionChannel.EncodeScene(root);
        var engine = new CompositionEngine();
        engine.ProcessBatch(batch);
        SceneVisual rebuilt = engine.Root ?? throw new InvalidOperationException("no root");
        byte[] viaProtocol = renderer.RenderToRgba(rebuilt, W, H, white);
        int diffs = 0;
        for (int i = 0; i < px.Length; i++) if (px[i] != viaProtocol[i]) diffs++;
        Check(diffs == 0, "opacity mask is byte-identical after protocol round-trip");

        if (_failures == 0)
        {
            Console.WriteLine("OPACITY MASK TEST PASSED: a brush-alpha mask fades the subtree per pixel and survives the protocol.");
            return 0;
        }
        Console.Error.WriteLine($"OPACITY MASK TEST FAILED: {_failures} check(s) wrong.");
        return 1;
    }

    private static SceneVisual BuildScene()
    {
        var root = new SceneVisual();
        var masked = new SceneVisual
        {
            // Alpha fades 1 (left) -> 0 (right); the stop colours' alpha is the mask.
            OpacityMask = new LinearGradientBrush(new Vector2(0, 0), new Vector2(64, 0), new[]
            {
                new GradientStop(0f, new RgbaColor(1f, 1f, 1f, 1f)),
                new GradientStop(1f, new RgbaColor(1f, 1f, 1f, 0f)),
            }),
        };
        masked.Content.Add(new GeometryFill(new RectangleGeometry(new Rect(0, 0, 64, 48)), RgbaColor.FromBytes(0, 0, 0, 255)));
        root.Children.Add(masked);
        return root;
    }

    private static int Lum(byte[] px, int x, int y) => px[(y * W + x) * 4];

    private static void Check(bool ok, string what)
    {
        Console.WriteLine($"  [{(ok ? "ok " : "BAD")}] {what}");
        if (!ok) _failures++;
    }
}
