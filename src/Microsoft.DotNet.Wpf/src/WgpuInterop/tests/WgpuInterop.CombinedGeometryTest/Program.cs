// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Phase-1 combined-geometry (boolean op) test. Two overlapping circles A (left)
// and B (right) are combined with each GeometryCombineMode and rendered black on
// white. Three probe points distinguish the modes:
//   A-only (18,24), overlap (33,24), B-only (48,24).
//     Union     -> fill , fill , fill
//     Intersect -> bg   , fill , bg
//     Xor       -> fill , bg   , fill
//     Exclude   -> fill , bg   , bg     (in A but not B)
// Combining happens at the coverage level, so the boolean result is anti-aliased
// and round-trips through the protocol (geometry serialized recursively).
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
        using var ctx = WgpuContext.Create();
        using var renderer = new WgpuSceneRenderer(ctx);
        var white = RgbaColor.FromBytes(255, 255, 255, 255);

        Probe(renderer, white, GeometryCombineMode.Union, fillAOnly: true, fillOverlap: true, fillBOnly: true);
        Probe(renderer, white, GeometryCombineMode.Intersect, fillAOnly: false, fillOverlap: true, fillBOnly: false);
        Probe(renderer, white, GeometryCombineMode.Xor, fillAOnly: true, fillOverlap: false, fillBOnly: true);
        Probe(renderer, white, GeometryCombineMode.Exclude, fillAOnly: true, fillOverlap: false, fillBOnly: false);

        // Anti-aliasing + protocol round-trip on one mode.
        SceneVisual scene = Scene(GeometryCombineMode.Union);
        byte[] px = renderer.RenderToRgba(scene, W, H, white);
        bool aa = false;
        for (int i = 0; i < px.Length; i += 4) { int v = px[i]; if (v > 30 && v < 225) { aa = true; break; } }
        Report(aa, "boolean result is anti-aliased");

        byte[] batch = CompositionChannel.EncodeScene(scene);
        var engine = new CompositionEngine();
        engine.ProcessBatch(batch);
        SceneVisual rebuilt = engine.Root ?? throw new InvalidOperationException("no root");
        byte[] viaProtocol = renderer.RenderToRgba(rebuilt, W, H, white);
        int diffs = 0;
        for (int i = 0; i < px.Length; i++) if (px[i] != viaProtocol[i]) diffs++;
        Report(diffs == 0, "combined geometry is byte-identical after protocol round-trip");

        if (_failures == 0)
        {
            Console.WriteLine("COMBINED GEOMETRY TEST PASSED: union/intersect/xor/exclude render at the coverage level and survive the protocol.");
            return 0;
        }
        Console.Error.WriteLine($"COMBINED GEOMETRY TEST FAILED: {_failures} check(s) wrong.");
        return 1;
    }

    private static void Probe(WgpuSceneRenderer renderer, RgbaColor white, GeometryCombineMode mode,
        bool fillAOnly, bool fillOverlap, bool fillBOnly)
    {
        byte[] px = renderer.RenderToRgba(Scene(mode), W, H, white);
        Console.WriteLine($"{mode}: aOnly={Lum(px, 18)}, overlap={Lum(px, 33)}, bOnly={Lum(px, 48)}");
        Region(px, 18, fillAOnly, $"{mode}: A-only");
        Region(px, 33, fillOverlap, $"{mode}: overlap");
        Region(px, 48, fillBOnly, $"{mode}: B-only");
    }

    private static SceneVisual Scene(GeometryCombineMode mode)
    {
        var root = new SceneVisual();
        var v = new SceneVisual();
        v.Content.Add(new GeometryFill(
            new CombinedGeometry(mode,
                new EllipseGeometry(new Vector2(26, 24), 14, 14),
                new EllipseGeometry(new Vector2(40, 24), 14, 14)),
            RgbaColor.FromBytes(0, 0, 0, 255)));
        root.Children.Add(v);
        return root;
    }

    private static int Lum(byte[] px, int x) => px[(24 * W + x) * 4];

    private static void Region(byte[] px, int x, bool filled, string what)
    {
        int v = Lum(px, x);
        bool ok = filled ? v < 40 : v > 215;
        Console.WriteLine($"  [{(ok ? "ok " : "BAD")}] {what} -> {(filled ? "filled" : "empty")} (got {v})");
        if (!ok) _failures++;
    }

    private static void Report(bool ok, string what)
    {
        Console.WriteLine($"  [{(ok ? "ok " : "BAD")}] {what}");
        if (!ok) _failures++;
    }
}
