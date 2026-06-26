// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// A tiny deterministic 5x7 bitmap font. It exists so the text pipeline (glyph
// atlas + sampling + layout) can be exercised and pixel-tested with no external
// font dependency. Coverage is binary (0 or 255), so at integer EmSize the
// rendered text is exact. A real port swaps this for FreeType/DirectWrite behind
// IGlyphSource; the atlas and renderer are unchanged.
//

using System.Collections.Generic;

namespace Microsoft.Wpf.Interop.WebGpu.Composition.Text
{
    internal sealed class BuiltinBitmapFont : IGlyphSource
    {
        private const int CellWidth = 5;
        private const int CellHeight = 7;
        private const int AdvanceCells = 6; // 5px glyph + 1px gap

        public int Ascent => CellHeight;

        // 7 rows x 5 columns; '#' = opaque, anything else = transparent.
        private static readonly Dictionary<char, string[]> Glyphs = new()
        {
            ['A'] = new[] { ".###.", "#...#", "#...#", "#####", "#...#", "#...#", "#...#" },
            ['B'] = new[] { "####.", "#...#", "#...#", "####.", "#...#", "#...#", "####." },
            ['C'] = new[] { ".###.", "#...#", "#....", "#....", "#....", "#...#", ".###." },
            ['E'] = new[] { "#####", "#....", "#....", "####.", "#....", "#....", "#####" },
            ['F'] = new[] { "#####", "#....", "#....", "####.", "#....", "#....", "#...." },
            ['G'] = new[] { ".###.", "#...#", "#....", "#.###", "#...#", "#...#", ".###." },
            ['H'] = new[] { "#...#", "#...#", "#...#", "#####", "#...#", "#...#", "#...#" },
            ['I'] = new[] { "#####", "..#..", "..#..", "..#..", "..#..", "..#..", "#####" },
            ['L'] = new[] { "#....", "#....", "#....", "#....", "#....", "#....", "#####" },
            ['O'] = new[] { ".###.", "#...#", "#...#", "#...#", "#...#", "#...#", ".###." },
            ['P'] = new[] { "####.", "#...#", "#...#", "####.", "#....", "#....", "#...." },
            ['R'] = new[] { "####.", "#...#", "#...#", "####.", "#.#..", "#..#.", "#...#" },
            ['S'] = new[] { ".####", "#....", "#....", ".###.", "....#", "....#", "####." },
            ['T'] = new[] { "#####", "..#..", "..#..", "..#..", "..#..", "..#..", "..#.." },
            ['U'] = new[] { "#...#", "#...#", "#...#", "#...#", "#...#", "#...#", ".###." },
            ['W'] = new[] { "#...#", "#...#", "#...#", "#.#.#", "#.#.#", "#.#.#", ".#.#." },
        };

        public bool TryGetGlyph(char c, out GlyphBitmap glyph)
        {
            if (c == ' ')
            {
                glyph = new GlyphBitmap(System.Array.Empty<byte>(), 0, 0, AdvanceCells, 0, CellHeight);
                return true;
            }

            if (!Glyphs.TryGetValue(c, out string[]? rows))
            {
                glyph = default;
                return false;
            }

            var coverage = new byte[CellWidth * CellHeight];
            for (int y = 0; y < CellHeight; y++)
            {
                string row = rows[y];
                for (int x = 0; x < CellWidth; x++)
                    coverage[y * CellWidth + x] = (x < row.Length && row[x] == '#') ? (byte)255 : (byte)0;
            }

            // Baseline sits at the bottom of the cell: BearingY = CellHeight.
            glyph = new GlyphBitmap(coverage, CellWidth, CellHeight, AdvanceCells, 0, CellHeight);
            return true;
        }
    }
}
