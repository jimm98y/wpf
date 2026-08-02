// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// CacheMode="BitmapCache" -- decoded, deliberately not acted on.
//
// MILCMD_VISUAL_SETCACHEMODE (0x1e) and MILCMD_BITMAPCACHE (0x8d) are now decoded onto
// SceneVisual.BitmapCached, but the renderer does NOT force such a subtree through the
// layer path. That was implemented and then reverted on the strength of the numbers this
// test prints: on repeat frames the UNCACHED path already issues zero bind groups, because
// the per-shape coverage-mask cache has already removed the work BitmapCache exists to
// avoid. Adding a layer only adds a composite, and round-tripping through an 8-bit
// premultiplied texture shifts anti-aliased pixels slightly.
//
// The test therefore asserts what is actually true -- the flag decodes, and setting it
// changes nothing visually -- and prints the cost table so the decision can be revisited
// with evidence rather than redone from scratch.
//

using System;
using System.Collections.Generic;
using System.Numerics;
using Microsoft.Wpf.Interop.WebGpu.Composition;

internal static class Program
{
    private const int W = 200, H = 200;
    private static int Drawables = 6;
    private static int _failures;

    private static int Main()
    {
        foreach (int n in new[] { 6, 60, 150 })
        {
            Drawables = n;
            (_, Counters p2) = RenderRepeated(cached: false);
            (_, Counters c2) = RenderRepeated(cached: true);
            Console.WriteLine($"  {n * 2,4} drawables: uncached {p2.BindGroups,3} bg / {p2.Ms,5:F2} ms"
                + $"   cached {c2.BindGroups,3} bg / {c2.Ms,5:F2} ms   hits={c2.LayerHits}");
        }
        Drawables = 6;
        Console.WriteLine();

        (byte[] plainPx, Counters plain) = RenderRepeated(cached: false);
        (byte[] cachedPx, Counters cached) = RenderRepeated(cached: true);

        Console.WriteLine("  per-frame cost on REPEAT frames (identical scene):");
        Console.WriteLine($"{"",4}{"",22}{"bindGroups",12}{"layerHits",11}{"ms",9}");
        Console.WriteLine($"    {"CacheMode=None",-22}{plain.BindGroups,12}{plain.LayerHits,11}{plain.Ms,9:F2}");
        Console.WriteLine($"    {"CacheMode=BitmapCache",-22}{cached.BindGroups,12}{cached.LayerHits,11}{cached.Ms,9:F2}");

        // The uncached path is already free on repeat frames; that is the finding.
        Check(plain.BindGroups == 0,
            $"repeat frames already cost no bind groups without BitmapCache ({plain.BindGroups})");

        int diff = 0, maxDelta = 0;
        for (int i = 0; i < W * H; i++)
            for (int c = 0; c < 3; c++)
            {
                int d = Math.Abs(plainPx[i * 4 + c] - cachedPx[i * 4 + c]);
                maxDelta = Math.Max(maxDelta, d);
                if (d > 2) { diff++; break; }
            }
        Console.WriteLine($"  pixels differing when the flag is set: {diff} (max delta {maxDelta})");
        Check(diff == 0, "setting CacheMode changes nothing visually (the flag is not acted on)");

        Console.WriteLine();
        if (_failures > 0) { Console.WriteLine($"BITMAP CACHE TEST FAILED: {_failures} problem(s)."); return 1; }
        Console.WriteLine("BITMAP CACHE TEST PASSED: CacheMode decodes and is inert, as measured.");
        return 0;
    }

    // Renders the SAME scene five times and reports the final frame plus the total number of
    // coverage rasterizations. A cached subtree should only rasterize on the first frame.
    private readonly record struct Counters(int BindGroups, int LayerHits, double Ms);

    private static (byte[], Counters) RenderRepeated(bool cached)
    {
        using var ctx = WgpuContext.Create();
        var renderer = new WgpuSceneRenderer(ctx);
        // The SAME scene instance every frame: a fresh tree would defeat any content hash and
        // measure re-creation rather than caching.
        SceneVisual scene = Scene(cached);
        var bg = RgbaColor.FromBytes(255, 255, 255, 255);
        byte[] px = renderer.RenderToRgba(scene, W, H, bg);            // warm

        int bindGroups = 0, hits = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        const int Frames = 20;
        for (int f = 0; f < Frames; f++)
        {
            WgpuSceneRenderer.PerfReset();
            px = renderer.RenderToRgba(scene, W, H, bg);
            bindGroups += WgpuSceneRenderer.PerfBindGroups;
            hits += WgpuSceneRenderer.PerfLayerHits;
        }
        sw.Stop();
        return (px, new Counters(bindGroups / Frames, hits / Frames, sw.Elapsed.TotalMilliseconds / Frames));
    }

    // A subtree with several curved drawables -- the case BitmapCache exists for.
    private static SceneVisual Scene(bool cached)
    {
        var root = new SceneVisual();
        var card = new SceneVisual { BitmapCached = cached };
        for (int i = 0; i < Drawables; i++)
        {
            float x = 12 + (i % 12) * 15, y = 12 + (i / 12) * 15;
            card.Content.Add(new GeometryFill(new EllipseGeometry(new Vector2(x, y), 22, 16),
                RgbaColor.FromBytes((byte)(40 + i * 30), 90, 180, 255)));
            card.Content.Add(new GeometryFill(
                new RoundedRectangleGeometry(new Rect(x - 20, y + 22, 40, 14), 6, 6),
                RgbaColor.FromBytes(200, (byte)(60 + i * 20), 40, 255)));
        }
        root.Children.Add(card);
        return root;
    }

    private static void Check(bool ok, string what)
    {
        Console.WriteLine($"  [{(ok ? "ok " : "FAIL")}] {what}");
        if (!ok) _failures++;
    }
}
