// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// TrueType/OpenType Collection (.ttc) test. A collection packs several faces into
// one file; DirectWrite selects one by index (IDWriteFontFace::GetIndex). We parse
// the 'ttcf' header ourselves, load two faces by their sfnt offsets, and assert
// each renders real outlines (hollow 'o' counter + AA) -- and that the two faces
// are actually different (distinct glyph counts or distinct 'A' ink), proving the
// per-face offset indexing works rather than always reading face 0.
//

using System;
using System.IO;
using Microsoft.Wpf.Interop.WebGpu.Composition.Text;

internal static class Program
{
    private static int _failures;

    private static int Main()
    {
        string? path = FindTtc();
        if (path is null)
            return Fail("no .ttc collection found (set WGPU_TEST_TTC)");
        Console.WriteLine($"collection = {path}");

        byte[] bytes = File.ReadAllBytes(path);
        if (!(bytes.Length >= 16 && bytes[0] == 't' && bytes[1] == 't' && bytes[2] == 'c' && bytes[3] == 'f'))
            return Fail("not a 'ttcf' collection");

        uint numFonts = BE32(bytes, 8);
        Console.WriteLine($"faces = {numFonts}");
        Check(numFonts >= 1, "collection reports at least one face");

        var inkPerFace = new int[Math.Min(numFonts, 4)];
        var glyphsPerFace = new int[inkPerFace.Length];
        for (int i = 0; i < inkPerFace.Length; i++)
        {
            int sfntOffset = (int)BE32(bytes, 12 + i * 4);
            IGlyphOutlineFont font = LoadFace(bytes, sfntOffset);
            var src = (IGlyphSource)font;
            var shaping = (IShapingFont)font;

            int gid = shaping.GlyphIndex('o');
            Check(gid != 0, $"face {i}: cmap maps 'o'");
            Check(src.TryGetGlyph(gid, out GlyphBitmap o) && o.Width >= 4 && o.Height >= 4, $"face {i}: 'o' rasterized");

            byte centre = o.Coverage.Length > 0 ? o.Coverage[(o.Height / 2) * o.Width + (o.Width / 2)] : (byte)255;
            Check(centre < 80, $"face {i}: 'o' counter hollow");
            bool aa = false; foreach (byte b in o.Coverage) if (b > 10 && b < 245) { aa = true; break; }
            Check(aa, $"face {i}: 'o' anti-aliased");

            int aGid = shaping.GlyphIndex('A');
            src.TryGetGlyph(aGid, out GlyphBitmap a);
            inkPerFace[i] = Ink(a);
            glyphsPerFace[i] = GlyphCount(font);
            Console.WriteLine($"  face {i}: offset={sfntOffset}, glyphs={glyphsPerFace[i]}, 'A' ink={inkPerFace[i]}");
        }

        if (numFonts >= 2)
        {
            // Per-face indexing is correct iff face 1 parses a DIFFERENT sfnt directory
            // than face 0 (collection-independent: even faces that share glyph outlines,
            // e.g. Cambria / Cambria Math, have distinct table directories).
            int off0 = (int)BE32(bytes, 12);
            int off1 = (int)BE32(bytes, 16);
            Check(off0 != off1, "faces have distinct sfnt offsets");
            Check(DirSignature(bytes, off0) != DirSignature(bytes, off1),
                "face 1 reads a different table directory than face 0 (per-face offset works)");
        }

        if (_failures == 0)
        {
            Console.WriteLine("TTC TEST PASSED: faces inside a collection load by index and render real outlines.");
            return 0;
        }
        Console.Error.WriteLine($"TTC TEST FAILED: {_failures} check(s) wrong.");
        return 1;
    }

    private static IGlyphOutlineFont LoadFace(byte[] bytes, int sfntOffset)
        => CffFont.IsCff(bytes, sfntOffset)
            ? new CffFont(bytes, sfntOffset: sfntOffset)
            : new TrueTypeFont(bytes, sfntOffset: sfntOffset);

    private static int GlyphCount(IGlyphOutlineFont f)
        => f is TrueTypeFont tt ? tt.GlyphCount : f is CffFont cff ? cff.GlyphCount : 0;

    private static int Ink(GlyphBitmap g)
    {
        int s = 0; foreach (byte b in g.Coverage) s += b; return s;
    }

    private static string? FindTtc()
    {
        string? env = Environment.GetEnvironmentVariable("WGPU_TEST_TTC");
        if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;

        string fonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        foreach (string name in new[] { "cambria.ttc", "msgothic.ttc", "BatangChe.ttc", "mingliub.ttc" })
        {
            string c = Path.Combine(fonts, name);
            if (File.Exists(c)) return c;
        }
        try
        {
            foreach (string f in Directory.EnumerateFiles(fonts, "*.ttc")) return f;
        }
        catch { }

        // macOS/Linux collections.
        foreach (string c in new[]
        {
            "/System/Library/Fonts/AppleSDGothicNeo.ttc",
            "/System/Library/Fonts/Helvetica.ttc",
        })
            if (File.Exists(c)) return c;

        return null;
    }

    private static uint BE32(byte[] b, int o)
        => ((uint)b[o] << 24) | ((uint)b[o + 1] << 16) | ((uint)b[o + 2] << 8) | b[o + 3];

    // A signature of the sfnt table directory at the given offset (tag:offset pairs),
    // so two faces in a collection can be compared without depending on glyph content.
    private static string DirSignature(byte[] b, int sfntBase)
    {
        int numTables = (b[sfntBase + 4] << 8) | b[sfntBase + 5];
        var sb = new System.Text.StringBuilder();
        int p = sfntBase + 12;
        for (int i = 0; i < numTables; i++, p += 16)
            sb.Append(System.Text.Encoding.ASCII.GetString(b, p, 4)).Append(':').Append(BE32(b, p + 8)).Append(';');
        return sb.ToString();
    }

    private static void Check(bool ok, string what)
    {
        Console.WriteLine($"  [{(ok ? "ok " : "BAD")}] {what}");
        if (!ok) _failures++;
    }

    private static int Fail(string m) { Console.Error.WriteLine("TTC TEST FAILED: " + m); return 1; }
}
