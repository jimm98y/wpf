// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Phase-1 real-font test. Loads an actual TrueType font and exercises
// TrueTypeFont (real outlines via the shared PathRasterizer). Rather than assert
// exact pixels of a specific font (fragile), it checks structural properties
// that only real outlines + real metrics produce:
//   * 'o' has a hollow counter (centre empty, ring inked) -> real contours +
//     non-zero fill of a reverse-wound inner loop;
//   * the glyph is anti-aliased (coverage values strictly between 0 and 255);
//   * advances are proportional ('W' wider than 'l') -> real horizontal metrics,
//     unlike the monospace built-in font;
//   * a string renders end-to-end through the atlas with real ink.
//

using System;
using System.IO;
using System.Numerics;
using Microsoft.Wpf.Interop.WebGpu.Composition;
using Microsoft.Wpf.Interop.WebGpu.Composition.Text;

internal static class Program
{
    private const int W = 160;
    private const int H = 64;
    private static int _failures;

    private static int Main()
    {
        string? fontPath = FindFont();
        if (fontPath is null)
            return Fail("no TrueType font found (set WGPU_TEST_FONT or install Arial/Segoe UI)");
        Console.WriteLine($"font = {fontPath}");

        var font = new TrueTypeFont(File.ReadAllBytes(fontPath));
        Console.WriteLine($"glyphs = {font.GlyphCount}, pixelsPerEm = {font.PixelsPerEm}");
        Check(font.GlyphCount > 0, "font reports glyphs");
        Check(font.PixelsPerEm == 48, "pixels-per-em");

        // 'o': real outline with a counter (hole).
        if (!font.TryGetGlyph('o', out GlyphBitmap o) || o.Width < 4 || o.Height < 4)
            return Fail("'o' glyph not rasterized");

        byte centre = o.Coverage[(o.Height / 2) * o.Width + (o.Width / 2)];
        byte ring = o.Coverage[(o.Height / 2) * o.Width + Math.Max(1, o.Width / 8)];
        Console.WriteLine($"'o' {o.Width}x{o.Height}: centre={centre}, ring={ring}");
        Check(centre < 40, "'o' counter is hollow (centre empty)");
        Check(ring > 200, "'o' ring is inked");

        bool hasAa = false;
        foreach (byte b in o.Coverage) if (b > 10 && b < 245) { hasAa = true; break; }
        Check(hasAa, "'o' edges are anti-aliased (partial coverage present)");

        // Proportional metrics.
        font.TryGetGlyph('W', out GlyphBitmap wG);
        font.TryGetGlyph('l', out GlyphBitmap lG);
        Console.WriteLine($"advances: W={wG.Advance}, l={lG.Advance}");
        Check(wG.Advance > lG.Advance, "proportional advances (W wider than l)");

        // End-to-end render through the atlas with the real font.
        using var ctx = WgpuContext.Create();
        using var renderer = new WgpuSceneRenderer(ctx, font);
        var root = new SceneVisual();
        var text = new SceneVisual();
        text.Content.Add(new GlyphRunDraw("Wol", new Vector2(6, 44), 40f, RgbaColor.FromBytes(0, 0, 0, 255)));
        root.Children.Add(text);
        byte[] px = renderer.RenderToRgba(root, W, H, RgbaColor.FromBytes(255, 255, 255, 255));

        int ink = 0, background = 0;
        for (int i = 0; i < px.Length; i += 4)
        {
            if (px[i] < 64) ink++;
            else if (px[i] > 250) background++;
        }
        Console.WriteLine($"rendered ink pixels = {ink}, background = {background}");
        Check(ink > 50, "real font renders ink end-to-end");
        Check(background > ink, "text is glyphs, not a solid block");

        if (_failures == 0)
        {
            Console.WriteLine("FONT TEST PASSED: real TrueType outlines render with counters, anti-aliasing and proportional metrics.");
            return 0;
        }
        Console.Error.WriteLine($"FONT TEST FAILED: {_failures} check(s) wrong.");
        return 1;
    }

    private static string? FindFont()
    {
        string? env = Environment.GetEnvironmentVariable("WGPU_TEST_FONT");
        if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;

        string fonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        foreach (string name in new[] { "arial.ttf", "segoeui.ttf", "tahoma.ttf", "verdana.ttf" })
        {
            string candidate = Path.Combine(fonts, name);
            if (File.Exists(candidate)) return candidate;
        }
        // Common Linux/macOS locations for CI portability.
        foreach (string candidate in new[]
        {
            "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf",
            "/Library/Fonts/Arial.ttf",
            "/System/Library/Fonts/Supplemental/Arial.ttf",
        })
            if (File.Exists(candidate)) return candidate;

        return null;
    }

    private static void Check(bool ok, string what)
    {
        Console.WriteLine($"  [{(ok ? "ok " : "BAD")}] {what}");
        if (!ok) _failures++;
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine($"FONT TEST FAILED: {message}");
        return 1;
    }
}
