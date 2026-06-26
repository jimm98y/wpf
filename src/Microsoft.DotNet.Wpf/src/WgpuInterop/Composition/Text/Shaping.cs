// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The text-shaping seam. Shaping turns a string into a sequence of *positioned
// glyphs* -- the HarfBuzz model (glyph infos + glyph positions). Separating it
// from rendering lets the renderer consume glyph ids + advances + offsets
// without assuming one character maps to one glyph at a fixed advance, which is
// what real shaping (kerning, ligatures, mark positioning, contextual forms,
// bidi) requires.
//
// Implemented here: the seam, a pass-through shaper, and a kerning shaper. More
// complex shaping (OpenType GSUB/GPOS, HarfBuzz) plugs in behind ITextShaper.
//

using System.Collections.Generic;

namespace Microsoft.Wpf.Interop.WebGpu.Composition.Text
{
    /// <summary>A shaped glyph: which glyph, how far to advance, and its offset.</summary>
    internal readonly struct ShapedGlyph
    {
        public readonly int GlyphId;
        public readonly float Advance;   // x advance, base pixels
        public readonly float XOffset;   // placement offset, base pixels
        public readonly float YOffset;

        public ShapedGlyph(int glyphId, float advance, float xOffset = 0f, float yOffset = 0f)
        {
            GlyphId = glyphId; Advance = advance; XOffset = xOffset; YOffset = yOffset;
        }
    }

    /// <summary>Font data a shaper needs: char-to-glyph, advances and kerning.</summary>
    internal interface IShapingFont
    {
        /// <summary>Maps a character to a glyph index (0 = .notdef / unmapped).</summary>
        int GlyphIndex(char c);

        /// <summary>The glyph's default horizontal advance, in base pixels.</summary>
        float Advance(int glyphId);

        /// <summary>Kerning adjustment (base pixels, usually negative) between a pair.</summary>
        bool TryGetKerning(int leftGlyph, int rightGlyph, out float kerning);
    }

    /// <summary>A font that can both shape text and rasterize glyphs.</summary>
    internal interface IFont : IGlyphSource, IShapingFont
    {
    }

    internal interface ITextShaper
    {
        void Shape(IShapingFont font, string text, List<ShapedGlyph> output);
    }

    /// <summary>Pass-through shaper: one glyph per character at its default advance.</summary>
    internal sealed class SimpleTextShaper : ITextShaper
    {
        public void Shape(IShapingFont font, string text, List<ShapedGlyph> output)
        {
            output.Clear();
            foreach (char c in text)
            {
                int gid = font.GlyphIndex(c);
                output.Add(new ShapedGlyph(gid, font.Advance(gid)));
            }
        }
    }

    /// <summary>Adds pairwise kerning on top of the simple mapping.</summary>
    internal sealed class KerningTextShaper : ITextShaper
    {
        public void Shape(IShapingFont font, string text, List<ShapedGlyph> output)
        {
            output.Clear();
            foreach (char c in text)
            {
                int gid = font.GlyphIndex(c);
                output.Add(new ShapedGlyph(gid, font.Advance(gid)));
            }

            for (int i = 0; i < output.Count - 1; i++)
            {
                if (font.TryGetKerning(output[i].GlyphId, output[i + 1].GlyphId, out float k))
                {
                    ShapedGlyph g = output[i];
                    output[i] = new ShapedGlyph(g.GlyphId, g.Advance + k, g.XOffset, g.YOffset);
                }
            }
        }
    }
}
