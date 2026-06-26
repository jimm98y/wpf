// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Phase-1 gradient spread-method test. A gradient's spread method controls how it
// extends past its [0,1] range. A black->white gradient with a short axis (0..16)
// fills a wide rectangle, so most of it is "beyond" the gradient:
//   * Repeat tiles the ramp: each 16px block restarts at black and ends at white.
//   * Reflect mirrors the ramp: alternate blocks run white->black.
// The discriminating pixels are at x=17 (just past the first ramp) and x=31 (just
// before the next boundary): Repeat is dark then light there, Reflect is the
// opposite. Both spreads round-trip through the protocol.
//

using System;
using System.Numerics;
using Microsoft.Wpf.Interop.WebGpu.Composition;
using Microsoft.Wpf.Interop.WebGpu.Composition.Protocol;

internal static class Program
{
    private const int W = 64;
    private const int H = 16;
    private static int _failures;

    private static int Main()
    {
        using var ctx = WgpuContext.Create();
        using var renderer = new WgpuSceneRenderer(ctx);
        var white = RgbaColor.FromBytes(255, 255, 255, 255);

        SceneVisual repeat = Scene(GradientSpreadMethod.Repeat);
        SceneVisual reflect = Scene(GradientSpreadMethod.Reflect);
        byte[] rep = renderer.RenderToRgba(repeat, W, H, white);
        byte[] refl = renderer.RenderToRgba(reflect, W, H, white);

        Console.WriteLine($"repeat:  x8={Lum(rep, 8)}, x17={Lum(rep, 17)}, x24={Lum(rep, 24)}, x31={Lum(rep, 31)}");
        Console.WriteLine($"reflect: x8={Lum(refl, 8)}, x17={Lum(refl, 17)}, x24={Lum(refl, 24)}, x31={Lum(refl, 31)}");

        // Mid of the first ramp is grey for both.
        Check(Between(Lum(rep, 8), 100, 170), "repeat: first ramp midpoint is grey");
        Check(Between(Lum(refl, 8), 100, 170), "reflect: first ramp midpoint is grey");

        // Repeat restarts the ramp (dark just past x=16, light before the next boundary).
        Check(Lum(rep, 17) < 70, "repeat: ramp restarts dark just past the axis");
        Check(Lum(rep, 31) > 190, "repeat: ramp reaches light before the next tile");

        // Reflect mirrors the ramp (light just past x=16, dark before the next boundary).
        Check(Lum(refl, 17) > 190, "reflect: ramp mirrors to light just past the axis");
        Check(Lum(refl, 31) < 70, "reflect: ramp mirrors to dark before the next tile");

        CheckRoundTrip(renderer, repeat, rep, white, "repeat");
        CheckRoundTrip(renderer, reflect, refl, white, "reflect");

        if (_failures == 0)
        {
            Console.WriteLine("SPREAD TEST PASSED: Repeat and Reflect gradient spreads tile correctly and survive the protocol.");
            return 0;
        }
        Console.Error.WriteLine($"SPREAD TEST FAILED: {_failures} check(s) wrong.");
        return 1;
    }

    private static SceneVisual Scene(GradientSpreadMethod spread)
    {
        var root = new SceneVisual();
        var fill = new SceneVisual();
        var stops = new[]
        {
            new GradientStop(0f, RgbaColor.FromBytes(0, 0, 0, 255)),
            new GradientStop(1f, RgbaColor.FromBytes(255, 255, 255, 255)),
        };
        fill.Content.Add(new GeometryFill(
            new RectangleGeometry(new Rect(0, 0, 64, 16)),
            new LinearGradientBrush(new Vector2(0, 8), new Vector2(16, 8), stops, spread)));
        root.Children.Add(fill);
        return root;
    }

    private static void CheckRoundTrip(WgpuSceneRenderer renderer, SceneVisual scene, byte[] direct, RgbaColor bg, string what)
    {
        byte[] batch = CompositionChannel.EncodeScene(scene);
        var engine = new CompositionEngine();
        engine.ProcessBatch(batch);
        SceneVisual rebuilt = engine.Root ?? throw new InvalidOperationException("no root");
        byte[] viaProtocol = renderer.RenderToRgba(rebuilt, W, H, bg);
        int diffs = 0;
        for (int i = 0; i < direct.Length; i++) if (direct[i] != viaProtocol[i]) diffs++;
        Check(diffs == 0, $"{what}: byte-identical after protocol round-trip");
    }

    private static int Lum(byte[] px, int x) => px[(8 * W + x) * 4]; // sample row y=8
    private static bool Between(int v, int lo, int hi) => v >= lo && v <= hi;

    private static void Check(bool ok, string what)
    {
        Console.WriteLine($"  [{(ok ? "ok " : "BAD")}] {what}");
        if (!ok) _failures++;
    }
}
