// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Phase-1 render test for the WebGPU scene renderer.
//
// Builds a small WPF-style visual tree that exercises the features the slice
// claims to support -- opaque fills, premultiplied-alpha opacity blending, a
// child offset, a non-trivial transform (scale), and a rectangular clip -- then
// renders it via real WebGPU draw calls and asserts specific pixels. This is the
// headless stand-in for the eventual "pixel-diff against milcore" gate.
//

using System;
using System.Numerics;
using Microsoft.Wpf.Interop.WebGpu.Composition;

internal static class Program
{
    private const int W = 64;
    private const int H = 64;

    private static int _failures;

    private static int Main()
    {
        var root = new SceneVisual();

        // childA: opaque red rectangle, device (8,8)-(40,40).
        var childA = new SceneVisual();
        childA.Content.Add(new GeometryFill(
            new RectangleGeometry(new Rect(8, 8, 32, 32)),
            RgbaColor.FromBytes(255, 0, 0, 255)));
        root.Children.Add(childA);

        // childB: 50% black at offset (40,40), 16x16 -> device (40,40)-(56,56).
        // Over the white background this must resolve to mid-grey.
        var childB = new SceneVisual { Offset = new Vector2(40, 40) };
        childB.Content.Add(new GeometryFill(
            new RectangleGeometry(new Rect(0, 0, 16, 16)),
            new RgbaColor(0f, 0f, 0f, 0.5f)));
        root.Children.Add(childB);

        // childC: 50%-opacity green, offset (8,40), clipped to its left half.
        // Green geometry is 32 wide but the clip Rect(0,0,16,32) keeps only x<24.
        var childC = new SceneVisual
        {
            Offset = new Vector2(8, 40),
            Opacity = 0.5,
            Clip = new Rect(0, 0, 16, 32),
        };
        childC.Content.Add(new GeometryFill(
            new RectangleGeometry(new Rect(0, 0, 32, 32)),
            RgbaColor.FromBytes(0, 255, 0, 255)));
        root.Children.Add(childC);

        // childE: scale x2 transform, blue rect Rect(20,2,4,4) -> device (40,4)-(48,12).
        // The covered pixels only land there if the scale is actually applied.
        var childE = new SceneVisual { Transform = Matrix3x2.CreateScale(2f, 2f) };
        childE.Content.Add(new GeometryFill(
            new RectangleGeometry(new Rect(20, 2, 4, 4)),
            RgbaColor.FromBytes(0, 0, 255, 255)));
        root.Children.Add(childE);

        using var ctx = WgpuContext.Create();
        var renderer = new WgpuSceneRenderer(ctx);
        byte[] px = renderer.RenderToRgba(root, W, H, RgbaColor.FromBytes(255, 255, 255, 255));

        Console.WriteLine($"rendered {W}x{H} via WebGPU draw calls");

        // background
        Expect(px, 60, 2, 255, 255, 255, 255, "background");
        // childA opaque red
        Expect(px, 16, 16, 255, 0, 0, 255, "opaque red fill");
        // childB 50% black over white -> grey
        Expect(px, 48, 48, 128, 128, 128, 255, "opacity blend (50% black/white)");
        // childC inside clip: 50% green over white
        Expect(px, 12, 50, 128, 255, 128, 255, "clip kept (green left half)");
        // childC outside clip -> background shows through
        Expect(px, 30, 50, 255, 255, 255, 255, "clip removed (green right half)");
        // childE scaled blue
        Expect(px, 44, 8, 0, 0, 255, 255, "scale transform (blue)");

        if (_failures == 0)
        {
            Console.WriteLine("PHASE-1 RENDER TEST PASSED: transforms, opacity blending and clipping render correctly via WebGPU.");
            return 0;
        }

        Console.Error.WriteLine($"PHASE-1 RENDER TEST FAILED: {_failures} pixel check(s) wrong.");
        return 1;
    }

    private static void Expect(byte[] px, int x, int y, byte r, byte g, byte b, byte a, string what)
    {
        int i = (y * W + x) * 4;
        byte ar = px[i], ag = px[i + 1], ab = px[i + 2], aa = px[i + 3];
        bool ok = Near(ar, r) && Near(ag, g) && Near(ab, b) && Near(aa, a);
        string status = ok ? "ok " : "BAD";
        Console.WriteLine($"  [{status}] ({x},{y}) {what}: got [{ar},{ag},{ab},{aa}] expected [{r},{g},{b},{a}]");
        if (!ok) _failures++;
    }

    private static bool Near(byte actual, byte expected) => Math.Abs(actual - expected) <= 1;
}
