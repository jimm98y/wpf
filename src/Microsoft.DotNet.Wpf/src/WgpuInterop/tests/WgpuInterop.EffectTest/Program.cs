// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Phase-1 effect test. Effects render a visual's subtree into an offscreen layer
// and post-process it:
//   * BlurEffect: a separable Gaussian blur softens a sharp square -- the edge
//     becomes a grey gradient and ink spreads beyond the original bounds, while
//     the interior stays solid and far-away pixels stay clear.
//   * DropShadowEffect: a blurred, tinted silhouette is drawn offset beneath the
//     sharp content -- a grey shadow appears down-right of the square, but not
//     up-left, and the square itself stays solid on top.
// Both effects round-trip through the DUCE protocol byte-for-byte.
//

using System;
using Microsoft.Wpf.Interop.WebGpu.Composition;
using Microsoft.Wpf.Interop.WebGpu.Composition.Protocol;

internal static class Program
{
    private const int W = 96;
    private const int H = 96;
    private static int _failures;

    private static int Main()
    {
        using var ctx = WgpuContext.Create();
        using var renderer = new WgpuSceneRenderer(ctx);
        var white = RgbaColor.FromBytes(255, 255, 255, 255);

        // ---- Blur ----
        SceneVisual blurScene = Wrap(new SceneVisual
        {
            Effect = new BlurEffect(5),
        }, new RectangleGeometry(new Rect(20, 20, 40, 40))); // (20,20)-(60,60)
        byte[] blur = renderer.RenderToRgba(blurScene, W, H, white);

        Console.WriteLine($"blur: center={Lum(blur, 40, 40)}, edge={Lum(blur, 60, 40)}, outside={Lum(blur, 66, 40)}, far={Lum(blur, 85, 40)}");
        Check(Lum(blur, 40, 40) < 40, "blur: interior stays solid");
        Check(Between(Lum(blur, 60, 40), 50, 205), "blur: original edge is a soft grey");
        Check(Lum(blur, 66, 40) < 240, "blur: ink spreads beyond the original bounds");
        Check(Lum(blur, 85, 40) > 245, "blur: far pixels stay clear");
        CheckRoundTrip(renderer, blurScene, blur, white, "blur");

        // ---- Drop shadow ----
        SceneVisual shadowScene = Wrap(new SceneVisual
        {
            Effect = new DropShadowEffect(new RgbaColor(0f, 0f, 0f, 0.7f), 2, 12, 12),
        }, new RectangleGeometry(new Rect(20, 20, 24, 24))); // (20,20)-(44,44)
        byte[] shadow = renderer.RenderToRgba(shadowScene, W, H, white);

        Console.WriteLine($"shadow: rect={Lum(shadow, 30, 30)}, shadow={Lum(shadow, 52, 52)}, upLeft={Lum(shadow, 12, 12)}");
        Check(Lum(shadow, 30, 30) < 40, "shadow: the content stays solid on top");
        Check(Between(Lum(shadow, 52, 52), 40, 175), "shadow: a grey shadow appears down-right");
        Check(Lum(shadow, 12, 12) > 245, "shadow: no shadow up-left (it is offset)");
        CheckRoundTrip(renderer, shadowScene, shadow, white, "drop shadow");

        if (_failures == 0)
        {
            Console.WriteLine("EFFECT TEST PASSED: Gaussian blur and drop shadow render via offscreen layers and survive the protocol.");
            return 0;
        }
        Console.Error.WriteLine($"EFFECT TEST FAILED: {_failures} check(s) wrong.");
        return 1;
    }

    private static SceneVisual Wrap(SceneVisual effected, Geometry geometry)
    {
        var root = new SceneVisual();
        effected.Content.Add(new GeometryFill(geometry, RgbaColor.FromBytes(0, 0, 0, 255)));
        root.Children.Add(effected);
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

    private static int Lum(byte[] px, int x, int y) => px[(y * W + x) * 4];
    private static bool Between(int v, int lo, int hi) => v >= lo && v <= hi;

    private static void Check(bool ok, string what)
    {
        Console.WriteLine($"  [{(ok ? "ok " : "BAD")}] {what}");
        if (!ok) _failures++;
    }
}
