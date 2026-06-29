// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Phase-1 brush test: exercises the textured-brush path (linear gradient + image)
// through real WebGPU bind groups, samplers and texture uploads, and also proves
// these brushes survive the DUCE command protocol unchanged.
//
//   * Gradient: a red->blue horizontal ramp; checks endpoints and the midpoint.
//   * Image:    a 2x2 red/green/blue/yellow checker sampled (nearest) across a
//               rectangle; checks each quadrant lands on the right texel.
//   * Round-trip: encode -> decode -> render must be byte-identical to direct.
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
        SceneVisual scene = BuildScene();
        var background = RgbaColor.FromBytes(255, 255, 255, 255);

        using var ctx = WgpuContext.Create();
        var renderer = new WgpuSceneRenderer(ctx);

        byte[] direct = renderer.RenderToRgba(scene, W, H, background);

        // Gradient ramp (Rect(4,4,56,16), axis (4,4)->(60,4), red@0 -> blue@1).
        // The gradient is continuous (~4.5/channel per pixel over 56px), so a
        // sample even a couple of pixels in is already slightly shaded; the
        // tolerances reflect that physical rate rather than demanding pure ends.
        // Stops are interpolated in sRGB space (WPF's default SRgbLinearInterpolation), so the
        // red->blue midpoint on this LINEAR target reads ~54 (= sRGB 127 once gamma-encoded for
        // display), not the 128 a naive linear interpolation would give.
        Expect(direct, 5, 12, 250, 0, 5, "gradient near start (red)", 14);
        Expect(direct, 58, 12, 5, 0, 250, "gradient near end (blue)", 14);
        Expect(direct, 32, 12, 54, 0, 54, "gradient midpoint (purple)", 12);

        // Image checker over Rect(8,36,16,16): TL red, TR green, BL blue, BR yellow.
        Expect(direct, 11, 39, 255, 0, 0, "image TL (red)", 2);
        Expect(direct, 21, 39, 0, 255, 0, "image TR (green)", 2);
        Expect(direct, 11, 49, 0, 0, 255, "image BL (blue)", 2);
        Expect(direct, 21, 49, 255, 255, 0, "image BR (yellow)", 2);

        // Protocol round-trip must be byte-identical.
        byte[] batch = CompositionChannel.EncodeScene(scene);
        var engine = new CompositionEngine();
        engine.ProcessBatch(batch);
        SceneVisual rebuilt = engine.Root ?? throw new InvalidOperationException("no root");
        byte[] viaProtocol = renderer.RenderToRgba(rebuilt, W, H, background);

        Console.WriteLine($"command batch = {batch.Length} bytes (includes gradient stops + image pixels)");
        int diffs = 0;
        for (int i = 0; i < direct.Length; i++) if (direct[i] != viaProtocol[i]) diffs++;
        if (diffs == 0) Console.WriteLine("  [ok ] gradient + image brushes are byte-identical after protocol round-trip");
        else { Console.WriteLine($"  [BAD] {diffs} bytes differ after round-trip"); _failures++; }

        if (_failures == 0)
        {
            Console.WriteLine("BRUSH TEST PASSED: gradient and image brushes render via WebGPU textures and survive the protocol.");
            return 0;
        }
        Console.Error.WriteLine($"BRUSH TEST FAILED: {_failures} check(s) wrong.");
        return 1;
    }

    private static SceneVisual BuildScene()
    {
        var root = new SceneVisual();

        var gradientVisual = new SceneVisual();
        gradientVisual.Content.Add(new GeometryFill(
            new RectangleGeometry(new Rect(4, 4, 56, 16)),
            new LinearGradientBrush(
                new Vector2(4, 4), new Vector2(60, 4),
                new[]
                {
                    new GradientStop(0f, RgbaColor.FromBytes(255, 0, 0, 255)),
                    new GradientStop(1f, RgbaColor.FromBytes(0, 0, 255, 255)),
                })));
        root.Children.Add(gradientVisual);

        // 2x2 checker, row-major: TL red, TR green, BL blue, BR yellow.
        byte[] checker =
        {
            255, 0, 0, 255,   0, 255, 0, 255,
            0, 0, 255, 255,   255, 255, 0, 255,
        };
        var imageVisual = new SceneVisual();
        imageVisual.Content.Add(new GeometryFill(
            new RectangleGeometry(new Rect(8, 36, 16, 16)),
            new ImageBrush(checker, 2, 2)));
        root.Children.Add(imageVisual);

        return root;
    }

    private static void Expect(byte[] px, int x, int y, byte r, byte g, byte b, string what, int tol)
    {
        int i = (y * W + x) * 4;
        bool ok = Near(px[i], r, tol) && Near(px[i + 1], g, tol) && Near(px[i + 2], b, tol);
        Console.WriteLine($"  [{(ok ? "ok " : "BAD")}] ({x},{y}) {what}: got [{px[i]},{px[i + 1]},{px[i + 2]}] expected ~[{r},{g},{b}]");
        if (!ok) _failures++;
    }

    private static bool Near(byte actual, byte expected, int tol) => Math.Abs(actual - expected) <= tol;
}
