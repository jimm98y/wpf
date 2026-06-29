// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Phase-1 image-tiling test. An image brush can repeat across a fill (WPF's
// TileMode). A 2x2 red/green/blue/yellow checker is tiled with an 8x8 tile over
// a wide rectangle, so the row y=2 shows red|green within each tile:
//   * Tile: every tile is identical, so column 0 of each tile is red.
//   * FlipX: alternate tiles are mirrored, so the second tile's column 0 is green.
// The discriminating pixels are at x=10 (column 0 of the second tile) and x=14
// (column 1 of the second tile). Both tile modes round-trip through the protocol.
//

using System;
using Microsoft.Wpf.Interop.WebGpu.Composition;
using Microsoft.Wpf.Interop.WebGpu.Composition.Protocol;

internal static class Program
{
    private const int W = 32;
    private const int H = 16;
    private static int _failures;

    // 2x2 checker: TL red, TR green, BL blue, BR yellow.
    private static readonly byte[] Checker =
    {
        255, 0, 0, 255,   0, 255, 0, 255,
        0, 0, 255, 255,   255, 255, 0, 255,
    };

    private static int Main()
    {
        using var ctx = WgpuContext.Create();
        using var renderer = new WgpuSceneRenderer(ctx);
        var white = RgbaColor.FromBytes(255, 255, 255, 255);

        SceneVisual tile = Scene(TileMode.Tile);
        SceneVisual flip = Scene(TileMode.FlipX);
        byte[] tilePx = renderer.RenderToRgba(tile, W, H, white);
        byte[] flipPx = renderer.RenderToRgba(flip, W, H, white);

        // Sampling note: image brushes are bilinear-sampled (matching WPF), so a 2x2 checker scaled to
        // an 8x8 tile blends across texel boundaries. Sample at clamped corners of each texel region
        // (x in {1,6} per 8px tile, y=1 = top row) where bilinear == the pure texel colour.
        // Tile 0 is identical in both modes: red (col 0) | green (col 1) across the top row.
        Pixel(tilePx, 1, 1, 255, 0, 0, "tile: first tile column 0 (red)");
        Pixel(tilePx, 6, 1, 0, 255, 0, "tile: first tile column 1 (green)");
        // Tile 1 repeats exactly.
        Pixel(tilePx, 9, 1, 255, 0, 0, "tile: second tile column 0 repeats (red)");
        Pixel(tilePx, 14, 1, 0, 255, 0, "tile: second tile column 1 repeats (green)");

        // FlipX mirrors the second tile, so its columns swap.
        Pixel(flipPx, 1, 1, 255, 0, 0, "flipX: first tile column 0 (red)");
        Pixel(flipPx, 9, 1, 0, 255, 0, "flipX: second tile mirrored, column 0 is green");
        Pixel(flipPx, 14, 1, 255, 0, 0, "flipX: second tile mirrored, column 1 is red");

        CheckRoundTrip(renderer, tile, tilePx, white, "tile");
        CheckRoundTrip(renderer, flip, flipPx, white, "flipX");

        if (_failures == 0)
        {
            Console.WriteLine("TILE TEST PASSED: image brushes tile (and flip alternate tiles) and survive the protocol.");
            return 0;
        }
        Console.Error.WriteLine($"TILE TEST FAILED: {_failures} check(s) wrong.");
        return 1;
    }

    private static SceneVisual Scene(TileMode mode)
    {
        var root = new SceneVisual();
        var fill = new SceneVisual();
        fill.Content.Add(new GeometryFill(
            new RectangleGeometry(new Rect(0, 0, 32, 16)),
            new ImageBrush(Checker, 2, 2, mode, 8, 8)));
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

    private static void Pixel(byte[] px, int x, int y, byte r, byte g, byte b, string what)
    {
        int i = (y * W + x) * 4;
        bool ok = Near(px[i], r) && Near(px[i + 1], g) && Near(px[i + 2], b);
        Console.WriteLine($"  [{(ok ? "ok " : "BAD")}] ({x},{y}) {what}: got [{px[i]},{px[i + 1]},{px[i + 2]}]");
        if (!ok) _failures++;
    }

    private static void Check(bool ok, string what)
    {
        Console.WriteLine($"  [{(ok ? "ok " : "BAD")}] {what}");
        if (!ok) _failures++;
    }

    private static bool Near(byte actual, byte expected) => Math.Abs(actual - expected) <= 8;
}
