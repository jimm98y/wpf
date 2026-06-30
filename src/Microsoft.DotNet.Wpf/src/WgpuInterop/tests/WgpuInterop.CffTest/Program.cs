// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Exercises CffFont (OpenType/PostScript outlines) the same way FontTest exercises
// TrueTypeFont: it loads a real .otf and checks structural properties only real
// Type 2 charstring outlines + real metrics produce -- a hollow 'o' counter,
// anti-aliasing, proportional advances -- plus the synthetic bold/oblique
// simulations (bolder = more ink, oblique = sheared wider).
//

using System;
using System.IO;
using Microsoft.Wpf.Interop.WebGpu.Composition.Text;

internal static class Program
{
    private static int _failures;

    private static int Main()
    {
        string? path = FindCff();
        if (path is null)
            return Fail("no CFF/OpenType font found (set WGPU_TEST_CFF to an .otf)");
        Console.WriteLine($"font = {path}");

        byte[] bytes = File.ReadAllBytes(path);
        Check(CffFont.IsCff(bytes), "detected as CFF/OpenType");

        var font = new CffFont(bytes);
        Console.WriteLine($"glyphs = {font.GlyphCount}, pixelsPerEm = {font.PixelsPerEm}, cidKeyed = {font.IsCidKeyed}");
        Check(font.GlyphCount > 0, "font reports glyphs");
        Check(font.PixelsPerEm == 48, "pixels-per-em");

        Check(font.GlyphIndex('A') != 0, "cmap maps 'A'");

        // 'o': real CFF outline with a counter (hole).
        int oGid = font.GlyphIndex('o');
        if (oGid == 0 || !font.TryGetGlyph(oGid, out GlyphBitmap o) || o.Width < 4 || o.Height < 4)
            return Fail("'o' glyph not rasterized from CFF outline");

        byte centre = o.Coverage[(o.Height / 2) * o.Width + (o.Width / 2)];
        byte ring = o.Coverage[(o.Height / 2) * o.Width + Math.Max(1, o.Width / 8)];
        Console.WriteLine($"'o' {o.Width}x{o.Height}: centre={centre}, ring={ring}, ink={Ink(o)}");
        Check(centre < 60, "'o' counter is hollow (centre empty)");
        Check(ring > 180, "'o' ring is inked");

        bool hasAa = false;
        foreach (byte b in o.Coverage) if (b > 10 && b < 245) { hasAa = true; break; }
        Check(hasAa, "'o' edges are anti-aliased");

        // Proportional metrics.
        float wAdv = font.Advance(font.GlyphIndex('W'));
        float lAdv = font.Advance(font.GlyphIndex('l'));
        Console.WriteLine($"advances: W={wAdv:0.0}, l={lAdv:0.0}");
        Check(wAdv > lAdv, "proportional advances (W wider than l)");

        // Synthetic bold: same outline emboldened -> strictly more ink.
        var bold = new CffFont(bytes, synthesizeBold: true);
        bold.TryGetGlyph(bold.GlyphIndex('o'), out GlyphBitmap ob);
        Console.WriteLine($"bold 'o' ink={Ink(ob)} vs regular {Ink(o)}");
        Check(Ink(ob) > Ink(o) * 1.05f, "synthetic bold adds ink");
        byte boldCentre = ob.Coverage[(ob.Height / 2) * ob.Width + (ob.Width / 2)];
        Check(boldCentre < 110, "bold 'o' counter still (mostly) hollow");

        // Synthetic oblique: shearing a tall glyph widens its bounding box.
        var oblique = new CffFont(bytes, synthesizeOblique: true);
        font.TryGetGlyph(font.GlyphIndex('I'), out GlyphBitmap iReg);
        oblique.TryGetGlyph(oblique.GlyphIndex('I'), out GlyphBitmap iObl);
        Console.WriteLine($"oblique 'I' {iObl.Width}x{iObl.Height} vs regular {iReg.Width}x{iReg.Height}");
        Check(iObl.Width > iReg.Width, "synthetic oblique shears 'I' wider");

        if (_failures == 0)
        {
            Console.WriteLine("CFF TEST PASSED: PostScript (CFF) outlines render with counters, AA, metrics, bold + oblique.");
            return 0;
        }
        Console.Error.WriteLine($"CFF TEST FAILED: {_failures} check(s) wrong.");
        return 1;
    }

    private static int Ink(GlyphBitmap g)
    {
        int sum = 0;
        foreach (byte b in g.Coverage) sum += b;
        return sum;
    }

    private static string? FindCff()
    {
        string? env = Environment.GetEnvironmentVariable("WGPU_TEST_CFF");
        if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;

        // Probe well-known CFF/OpenType fonts that ship with common apps, then scan
        // the system Fonts folder for any 'OTTO' (CFF) sfnt.
        foreach (string c in new[]
        {
            @"C:\Program Files\Adobe\Acrobat DC\Acrobat\CrashReporterResources\AdobeClean-Regular.otf",
            @"C:\Program Files\Adobe\Acrobat DC\Acrobat\WebResources\Resource2\app1\fonts\AdobeCleanUX-Light.otf",
        })
            if (File.Exists(c)) return c;

        string fonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        try
        {
            foreach (string f in Directory.EnumerateFiles(fonts, "*.otf"))
                return f;
            foreach (string f in Directory.EnumerateFiles(fonts, "*.ttf"))
            {
                byte[] head = new byte[4];
                using FileStream fs = File.OpenRead(f);
                if (fs.Read(head, 0, 4) == 4 && head[0] == (byte)'O' && head[1] == (byte)'T' && head[2] == (byte)'T' && head[3] == (byte)'O')
                    return f;
            }
        }
        catch { }
        return null;
    }

    private static void Check(bool ok, string what)
    {
        Console.WriteLine($"  [{(ok ? "ok " : "BAD")}] {what}");
        if (!ok) _failures++;
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine($"CFF TEST FAILED: {message}");
        return 1;
    }
}
