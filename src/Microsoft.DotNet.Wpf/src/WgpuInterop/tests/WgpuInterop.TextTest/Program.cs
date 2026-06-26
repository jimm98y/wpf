// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Phase-1 text test: renders the string "WPF" through the glyph-atlas path
// (rasterize -> pack -> upload one texture -> draw a quad per glyph sampling its
// coverage) and asserts individual glyph pixels -- strokes are the text colour,
// holes show the background. Because the built-in font is binary coverage at an
// integer EmSize, the result is exact. It also verifies the glyph run survives
// the DUCE command protocol byte-for-byte.
//
// Layout: EmSize 28 over a 7px-ascent font => scale 4 (each font pixel = 4x4
// device pixels). "WPF" starts at x=8 with baseline y=44, so each glyph's top is
// y=16 and glyphs advance by 24px (W@8, P@32, F@56).
//

using System;
using System.Numerics;
using Microsoft.Wpf.Interop.WebGpu.Composition;
using Microsoft.Wpf.Interop.WebGpu.Composition.Protocol;

internal static class Program
{
    private const int W = 96;
    private const int H = 64;
    private static int _failures;

    private static int Main()
    {
        var root = new SceneVisual();
        var text = new SceneVisual();
        text.Content.Add(new GlyphRunDraw("WPF", new Vector2(8, 44), 28f, RgbaColor.FromBytes(0, 0, 0, 255)));
        root.Children.Add(text);

        var background = RgbaColor.FromBytes(255, 255, 255, 255);
        using var ctx = WgpuContext.Create();
        var renderer = new WgpuSceneRenderer(ctx);

        byte[] direct = renderer.RenderToRgba(root, W, H, background);

        // Each check targets the centre of a 4x4 device block for a known font texel.
        Expect(direct, 90, 2, 255, 255, 255, "background");
        Expect(direct, 10, 18, 0, 0, 0, "W left stem (stroke)");
        Expect(direct, 14, 18, 255, 255, 255, "W top gap (hole)");
        Expect(direct, 18, 30, 0, 0, 0, "W centre prong (stroke)");
        Expect(direct, 34, 38, 0, 0, 0, "P left stem (stroke)");
        Expect(direct, 42, 34, 255, 255, 255, "P lower-right (hole)");
        Expect(direct, 70, 18, 0, 0, 0, "F top bar (stroke)");
        Expect(direct, 74, 30, 255, 255, 255, "F right of mid bar (hole)");

        // Glyph run must round-trip through the command protocol unchanged.
        byte[] batch = CompositionChannel.EncodeScene(root);
        var engine = new CompositionEngine();
        engine.ProcessBatch(batch);
        SceneVisual rebuilt = engine.Root ?? throw new InvalidOperationException("no root");
        byte[] viaProtocol = renderer.RenderToRgba(rebuilt, W, H, background);

        Console.WriteLine($"command batch = {batch.Length} bytes");
        int diffs = 0;
        for (int i = 0; i < direct.Length; i++) if (direct[i] != viaProtocol[i]) diffs++;
        if (diffs == 0) Console.WriteLine("  [ok ] glyph run is byte-identical after protocol round-trip");
        else { Console.WriteLine($"  [BAD] {diffs} bytes differ after round-trip"); _failures++; }

        if (_failures == 0)
        {
            Console.WriteLine("TEXT TEST PASSED: \"WPF\" renders from the glyph atlas via WebGPU and survives the protocol.");
            return 0;
        }
        Console.Error.WriteLine($"TEXT TEST FAILED: {_failures} check(s) wrong.");
        return 1;
    }

    private static void Expect(byte[] px, int x, int y, byte r, byte g, byte b, string what)
    {
        int i = (y * W + x) * 4;
        bool ok = Near(px[i], r) && Near(px[i + 1], g) && Near(px[i + 2], b);
        Console.WriteLine($"  [{(ok ? "ok " : "BAD")}] ({x},{y}) {what}: got [{px[i]},{px[i + 1]},{px[i + 2]}] expected ~[{r},{g},{b}]");
        if (!ok) _failures++;
    }

    private static bool Near(byte actual, byte expected) => Math.Abs(actual - expected) <= 4;
}
