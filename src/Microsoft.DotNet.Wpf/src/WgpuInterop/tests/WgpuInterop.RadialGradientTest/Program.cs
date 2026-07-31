// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Phase-1 radial-gradient test. A radial gradient fades from a centre colour out
// to an ellipse of radii, evaluated per pixel (unlike a linear gradient it can't
// be expressed by per-vertex interpolation). A rectangle is filled with a
// white-to-black radial gradient centred in it:
//   * the centre is the first stop (white),
//   * a point one radius out is the last stop (black),
//   * a point at half the radius is the mid colour (grey),
//   * points equidistant from the centre share a colour (radial symmetry),
//   * beyond the radius the last stop is held (clamped),
//   * and it round-trips through the protocol.
//

using System;
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

        int centre = Lum(px, 32, 32);
        int up = Lum(px, 32, 20);   // half radius up
        int left = Lum(px, 20, 32); // half radius left (symmetric)
        int edge = Lum(px, 32, 8);  // one radius up (last stop)
        int corner = Lum(px, 8, 8);  // beyond radius (clamped)
        Console.WriteLine($"centre={centre}, halfUp={up}, halfLeft={left}, edge={edge}, corner={corner}");

        // Stops interpolate in sRGB space (WPF default), so the half-radius sample is sRGB mid-grey;
        // the compositing mode decides the space it is stored in. Gamma mode (the default) keeps the
        // gamma value verbatim (~127); linear mode (WPF_WEBGPU_GAMMA=0) decodes it to linear (~60).
        int midGrey = WgpuSceneRenderer.s_gammaComposite ? 127 : 60;
        Check(centre > 230, "centre is the first stop (white)");
        Check(Math.Abs(up - midGrey) <= 28, "half radius is the mid colour (grey)");
        Check(Math.Abs(up - left) <= 8, "equidistant points match (radial symmetry)");
        Check(edge < 40, "one radius out is the last stop (black)");
        Check(corner < 40, "beyond the radius the last stop is held (clamped)");

        byte[] batch = CompositionChannel.EncodeScene(root);
        var engine = new CompositionEngine();
        engine.ProcessBatch(batch);
        SceneVisual rebuilt = engine.Root ?? throw new InvalidOperationException("no root");
        byte[] viaProtocol = renderer.RenderToRgba(rebuilt, W, H, white);
        int diffs = 0;
        for (int i = 0; i < px.Length; i++) if (px[i] != viaProtocol[i]) diffs++;
        Check(diffs == 0, "radial gradient is byte-identical after protocol round-trip");

        if (_failures == 0)
        {
            Console.WriteLine("RADIAL GRADIENT TEST PASSED: per-pixel radial gradient renders and survives the protocol.");
            return 0;
        }
        Console.Error.WriteLine($"RADIAL GRADIENT TEST FAILED: {_failures} check(s) wrong.");
        return 1;
    }

    private static SceneVisual BuildScene()
    {
        var root = new SceneVisual();
        var fill = new SceneVisual();
        var stops = new[]
        {
            new GradientStop(0f, RgbaColor.FromBytes(255, 255, 255, 255)),
            new GradientStop(1f, RgbaColor.FromBytes(0, 0, 0, 255)),
        };
        fill.Content.Add(new GeometryFill(
            new RectangleGeometry(new Rect(8, 8, 48, 48)),
            new RadialGradientBrush(new System.Numerics.Vector2(32, 32), 24, 24, stops)));
        root.Children.Add(fill);
        return root;
    }

    private static int Lum(byte[] px, int x, int y) => px[(y * W + x) * 4];

    private static void Check(bool ok, string what)
    {
        Console.WriteLine($"  [{(ok ? "ok " : "BAD")}] {what}");
        if (!ok) _failures++;
    }
}
