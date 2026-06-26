// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Phase-1 kerning / shaping-seam test. Text is shaped (string -> positioned
// glyphs) by a pluggable ITextShaper before layout, so kerning, ligatures and
// mark positioning can adjust glyph advances/offsets. This renders "AW" with the
// pass-through shaper and with the kerning shaper; the built-in font defines a
// synthetic A/W kern of -2 cells, so the kerning shaper must pull the second
// glyph left by exactly 2 * scale device pixels -- everything else identical.
//

using System;
using System.Numerics;
using Microsoft.Wpf.Interop.WebGpu.Composition;
using Microsoft.Wpf.Interop.WebGpu.Composition.Text;

internal static class Program
{
    private const int W = 64;
    private const int H = 64;
    private const float EmSize = 28f;       // scale = EmSize / 7 = 4
    private const int Scale = 4;
    private const int KernCells = 2;        // built-in font's A/W kern
    private static int _failures;

    private static int Main()
    {
        using var ctx = WgpuContext.Create();
        using var simple = new WgpuSceneRenderer(ctx, font: null, shaper: new SimpleTextShaper());
        using var kerned = new WgpuSceneRenderer(ctx, font: null, shaper: new KerningTextShaper());

        var white = RgbaColor.FromBytes(255, 255, 255, 255);
        byte[] simplePx = simple.RenderToRgba(Scene(), W, H, white);
        byte[] kernedPx = kerned.RenderToRgba(Scene(), W, H, white);

        int simpleRight = RightmostInk(simplePx);
        int kernedRight = RightmostInk(kernedPx);
        int shift = simpleRight - kernedRight;
        Console.WriteLine($"rightmost ink: simple={simpleRight}, kerned={kernedRight}, shift={shift} (expected {KernCells * Scale})");

        Check(simpleRight > 0 && kernedRight > 0, "both strings rendered");
        Check(kernedRight < simpleRight, "kerning pulled the second glyph left");
        Check(Math.Abs(shift - KernCells * Scale) <= 1, "shift equals kern * scale exactly");

        // The first glyph's left edge is unchanged by the kern of the pair.
        Check(LeftmostInk(simplePx) == LeftmostInk(kernedPx), "first glyph position is unchanged");

        if (_failures == 0)
        {
            Console.WriteLine("KERNING TEST PASSED: the shaping seam applies kerning to glyph advances.");
            return 0;
        }
        Console.Error.WriteLine($"KERNING TEST FAILED: {_failures} check(s) wrong.");
        return 1;
    }

    private static SceneVisual Scene()
    {
        var root = new SceneVisual();
        var text = new SceneVisual();
        text.Content.Add(new GlyphRunDraw("AW", new Vector2(4, 44), EmSize, RgbaColor.FromBytes(0, 0, 0, 255)));
        root.Children.Add(text);
        return root;
    }

    private static int RightmostInk(byte[] px)
    {
        for (int x = W - 1; x >= 0; x--)
            for (int y = 0; y < H; y++)
                if (px[(y * W + x) * 4] < 128) return x;
        return -1;
    }

    private static int LeftmostInk(byte[] px)
    {
        for (int x = 0; x < W; x++)
            for (int y = 0; y < H; y++)
                if (px[(y * W + x) * 4] < 128) return x;
        return -1;
    }

    private static void Check(bool ok, string what)
    {
        Console.WriteLine($"  [{(ok ? "ok " : "BAD")}] {what}");
        if (!ok) _failures++;
    }
}
