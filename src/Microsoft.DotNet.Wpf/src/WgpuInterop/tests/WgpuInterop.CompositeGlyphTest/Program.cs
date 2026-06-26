// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Phase-1 composite-glyph test. Accented letters (e-acute, a-grave, n-tilde, ...)
// are usually TrueType *composite* glyphs: a base glyph plus a diacritic placed
// with a transform/offset. These used to render blank (advance only). This test
// finds a genuine composite accented glyph and verifies it now rasterizes as the
// base plus an accent above it:
//   * the glyph is actually composite (IsCompositeGlyph),
//   * it renders (non-empty),
//   * it is taller than its base letter and rises higher above the baseline,
//   * it has more ink than the base (the accent adds coverage),
//   * and it renders end-to-end through the atlas.
//

using System;
using System.IO;
using System.Numerics;
using Microsoft.Wpf.Interop.WebGpu.Composition;
using Microsoft.Wpf.Interop.WebGpu.Composition.Text;

internal static class Program
{
    private static int _failures;

    // Accented letter -> its base letter. Code points (not literals) keep this
    // source pure ASCII regardless of how the file is encoded/read.
    private static readonly (char Accented, char Base)[] Candidates =
    {
        ((char)0x00E9, 'e'), // e-acute
        ((char)0x00E8, 'e'), // e-grave
        ((char)0x00E0, 'a'), // a-grave
        ((char)0x00F1, 'n'), // n-tilde
        ((char)0x00FC, 'u'), // u-diaeresis
        ((char)0x00F4, 'o'), // o-circumflex
        ((char)0x00E7, 'c'), // c-cedilla
    };

    private static int Main()
    {
        string? fontPath = FindFont();
        if (fontPath is null)
            return Fail("no TrueType font found (set WGPU_TEST_FONT or install Arial/Segoe UI)");
        Console.WriteLine($"font = {fontPath}");

        var font = new TrueTypeFont(File.ReadAllBytes(fontPath));

        // Find an accented letter that is genuinely a composite glyph.
        char accented = '\0', baseChar = '\0';
        foreach ((char a, char b) in Candidates)
        {
            if (font.IsCompositeGlyph(a) && font.TryGetGlyph(b, out _))
            {
                accented = a; baseChar = b; break;
            }
        }
        if (accented == '\0')
            return Fail("no composite accented glyph found in this font");
        Console.WriteLine($"composite glyph = '{accented}' (base '{baseChar}')");

        if (!font.TryGetGlyph(accented, out GlyphBitmap acc) || acc.Width < 2 || acc.Height < 2)
            return Fail($"'{accented}' did not rasterize");
        font.TryGetGlyph(baseChar, out GlyphBitmap baseGlyph);

        Console.WriteLine($"'{accented}': {acc.Width}x{acc.Height}, bearingY={acc.BearingY}, ink={Ink(acc)}");
        Console.WriteLine($"'{baseChar}': {baseGlyph.Width}x{baseGlyph.Height}, bearingY={baseGlyph.BearingY}, ink={Ink(baseGlyph)}");

        Check(acc.Height > baseGlyph.Height, "accented glyph is taller than its base (accent adds height)");
        Check(acc.BearingY > baseGlyph.BearingY, "accented glyph rises higher above the baseline");
        Check(Ink(acc) > Ink(baseGlyph), "accented glyph has more ink than its base (the accent)");

        // End-to-end through the atlas.
        using var ctx = WgpuContext.Create();
        using var renderer = new WgpuSceneRenderer(ctx, font);
        var root = new SceneVisual();
        var text = new SceneVisual();
        text.Content.Add(new GlyphRunDraw(accented.ToString(), new Vector2(8, 44), 40f, RgbaColor.FromBytes(0, 0, 0, 255)));
        root.Children.Add(text);
        byte[] px = renderer.RenderToRgba(root, 64, 64, RgbaColor.FromBytes(255, 255, 255, 255));
        int ink = 0;
        for (int i = 0; i < px.Length; i += 4) if (px[i] < 64) ink++;
        Console.WriteLine($"rendered ink pixels = {ink}");
        Check(ink > 30, "composite glyph renders ink end-to-end");

        if (_failures == 0)
        {
            Console.WriteLine("COMPOSITE GLYPH TEST PASSED: accented (composite) glyphs render as base + diacritic.");
            return 0;
        }
        Console.Error.WriteLine($"COMPOSITE GLYPH TEST FAILED: {_failures} check(s) wrong.");
        return 1;
    }

    private static int Ink(GlyphBitmap g)
    {
        int n = 0;
        foreach (byte b in g.Coverage) if (b > 128) n++;
        return n;
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
        Console.Error.WriteLine($"COMPOSITE GLYPH TEST FAILED: {message}");
        return 1;
    }
}
