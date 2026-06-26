// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The glyph rasterization seam. The renderer composites text from a glyph atlas
// and does not care where the coverage bitmaps come from; an IGlyphSource
// provides them. Today the only implementation is a deterministic built-in
// bitmap font (no external dependency, so text is pixel-testable). On real
// platforms this is where FreeType/HarfBuzz (Linux/macOS/WASM) or DirectWrite
// (Windows) plug in -- they rasterize the same coverage the atlas packs.
//
// All metrics are in the source's base pixel units; the renderer scales by
// EmSize / Ascent when laying out a run.
//

namespace Microsoft.Wpf.Interop.WebGpu.Composition.Text
{
    /// <summary>A rasterized glyph: 8-bit coverage plus placement metrics.</summary>
    internal readonly struct GlyphBitmap
    {
        /// <summary>Row-major coverage, one byte (0..255) per pixel; empty for blank glyphs.</summary>
        public readonly byte[] Coverage;
        public readonly int Width;
        public readonly int Height;
        /// <summary>Horizontal pen advance after this glyph.</summary>
        public readonly int Advance;
        /// <summary>Left side bearing (offset from the pen to the bitmap's left edge).</summary>
        public readonly int BearingX;
        /// <summary>Top side bearing (distance from the baseline up to the bitmap's top).</summary>
        public readonly int BearingY;

        public GlyphBitmap(byte[] coverage, int width, int height, int advance, int bearingX, int bearingY)
        {
            Coverage = coverage; Width = width; Height = height;
            Advance = advance; BearingX = bearingX; BearingY = bearingY;
        }
    }

    internal interface IGlyphSource
    {
        /// <summary>Base pixels above the baseline; used to map EmSize to a scale.</summary>
        int Ascent { get; }

        /// <summary>Resolves a character to a glyph; false if unsupported.</summary>
        bool TryGetGlyph(char c, out GlyphBitmap glyph);
    }
}
