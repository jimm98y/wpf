// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Guards the Fluent-icon substitution table in
// DirectWriteForwarder/Stub/Managed/OpenTypeFontData.TryRemapFluentIcon.
//
// Off-Windows there is no "Segoe Fluent Icons", so the fork substitutes the vendored
// MDL2-based Symbols.ttf and remaps the Fluent-only codepoints onto MDL2 equivalents.
// The table itself is a handful of reviewed constants; the FRAGILE half is the font --
// if a future font drop loses one of the substitute glyphs, the remap silently points at
// .notdef and the tofu comes back somewhere else. Nothing else would catch that.
//
// So this asserts, against the vendored font actually shipped in the SDK package:
//   * every Fluent codepoint in the table is genuinely ABSENT (otherwise the remap is
//     dead code, and worse, is overriding a real glyph);
//   * every substitute it maps to is PRESENT.
//

using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Wpf.Interop.WebGpu.Composition.Text;

internal static class Program
{
    // Must mirror TryRemapFluentIcon. Duplicated deliberately: that method is private to
    // another assembly, and a copy that drifts is exactly what this test would flag.
    private static readonly (uint Fluent, uint Mdl2, string What)[] Table =
    {
        (0xE9AE, 0xE738, "CheckBox indeterminate dash -> Remove"),
        (0xEB3C, 0xE790, "Colors -> Color (palette)"),
        (0xED58, 0xE76E, "Icons -> Emoji2"),
        (0xEF58, 0xE9D9, "User Dashboard -> Analytics"),
        (0xF246, 0xF0E2, "Layout -> GridView"),
    };

    private static int _failures;

    private static int Main()
    {
        string? path = FindVendoredFont();
        if (path is null)
        {
            Console.WriteLine("FAIL: vendored Symbols.ttf not found (sdk/WpfWebGpu.Sdk/web/fonts).");
            return 1;
        }
        Console.WriteLine($"font: {path}");

        var font = new TrueTypeFont(File.ReadAllBytes(path));
        Console.WriteLine($"{"mapping",-40}{"fluent",10}{"substitute",13}");
        Console.WriteLine(new string('-', 63));

        foreach ((uint fluent, uint mdl2, string what) in Table)
        {
            int gFluent = font.GlyphIndex((char)fluent);
            int gMdl2 = font.GlyphIndex((char)mdl2);
            bool absent = gFluent == 0;
            bool present = gMdl2 != 0;
            Console.WriteLine($"{what,-40}{(absent ? "absent" : "PRESENT!"),10}{(present ? "present" : "MISSING!"),13}");

            if (!absent)
            {
                Console.WriteLine($"  [FAIL] U+{fluent:X4} exists in the font, so remapping it hides the real glyph.");
                _failures++;
            }
            if (!present)
            {
                Console.WriteLine($"  [FAIL] substitute U+{mdl2:X4} is not in the font -- the remap yields .notdef.");
                _failures++;
            }
        }

        Console.WriteLine();
        if (_failures > 0) { Console.WriteLine($"ICON FALLBACK TEST FAILED: {_failures} problem(s)."); return 1; }
        Console.WriteLine("ICON FALLBACK TEST PASSED: every Fluent remap targets a glyph the vendored font has.");
        return 0;
    }

    private static string? FindVendoredFont()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 12 && d is not null; i++, d = d.Parent)
        {
            string p = Path.Combine(d.FullName, "sdk", "WpfWebGpu.Sdk", "web", "fonts", "Symbols.ttf");
            if (File.Exists(p)) return p;
        }
        return null;
    }
}
