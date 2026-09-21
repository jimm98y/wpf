// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// A CPU-side glyph atlas: glyph coverage bitmaps are shelf-packed into one R8
// image, and each glyph's atlas rectangle + metrics are cached. The renderer
// uploads this image as a single texture and draws each glyph as a quad sampling
// its sub-rectangle -- the same approach milcore's glyph cache uses, and the
// reason text is one texture + many quads rather than one draw per glyph.
//
// Coverage is single-channel (R8); the text shader reads it as the alpha mask
// and multiplies by the run's brush colour.
//

using System;
using System.Collections.Generic;

namespace Microsoft.Wpf.Interop.WebGpu.Composition.Text
{
    /// <summary>A glyph's location in the atlas plus its placement metrics.</summary>
    internal readonly struct GlyphEntry
    {
        public readonly float U0, V0, U1, V1; // atlas texture coordinates
        public readonly int Width, Height;     // base pixels
        public readonly int Advance, BearingX, BearingY;

        public GlyphEntry(float u0, float v0, float u1, float v1, int width, int height, int advance, int bearingX, int bearingY)
        {
            U0 = u0; V0 = v0; U1 = u1; V1 = v1;
            Width = width; Height = height; Advance = advance; BearingX = bearingX; BearingY = bearingY;
        }
    }

    internal sealed class GlyphAtlas
    {
        private const int Padding = 1;

        private readonly Dictionary<int, GlyphEntry> _entries = new();
        private int _penX = Padding;
        private int _penY = Padding;
        private int _rowHeight;

        public int Width { get; }
        public int Height { get; }
        public byte[] Pixels { get; }

        /// <summary>True when glyphs were added since the last <see cref="ClearDirty"/>.</summary>
        public bool Dirty { get; private set; }

        public GlyphAtlas(int width = 256, int height = 256)
        {
            Width = width;
            Height = height;
            Pixels = new byte[width * height];
        }

        public void ClearDirty() => Dirty = false;

        /// <summary>Looks up an already-packed glyph (no rasterization).</summary>
        public bool TryGet(int glyphId, out GlyphEntry entry) => _entries.TryGetValue(glyphId, out entry);

        /// <summary>
        /// GPU path: reserves an atlas rectangle for a glyph of the given size and records its
        /// entry + metrics, WITHOUT filling any pixels (the renderer rasterizes the outline into
        /// the returned rectangle on the GPU). For a zero-size (blank) glyph, stores a metrics-only
        /// entry and returns rw=rh=0. Returns false if the atlas is full.
        /// </summary>
        public bool AddPacked(int glyphId, int width, int height, int advance, int bearingX, int bearingY,
            out GlyphEntry entry, out int rx, out int ry, out int rw, out int rh)
        {
            rx = ry = rw = rh = 0;
            if (width <= 0 || height <= 0)
            {
                entry = new GlyphEntry(0, 0, 0, 0, 0, 0, advance, bearingX, bearingY);
                _entries[glyphId] = entry;
                return true;
            }

            // Shelf packing: wrap to a new row when the current one is full.
            if (_penX + width + Padding > Width)
            {
                _penX = Padding;
                _penY += _rowHeight + Padding;
                _rowHeight = 0;
            }
            if (_penY + height + Padding > Height)
            {
                entry = default;
                return false;   // atlas full (caller can skip the glyph / grow later)
            }

            rx = _penX; ry = _penY; rw = width; rh = height;
            entry = new GlyphEntry(
                _penX / (float)Width, _penY / (float)Height,
                (_penX + width) / (float)Width, (_penY + height) / (float)Height,
                width, height, advance, bearingX, bearingY);
            _entries[glyphId] = entry;

            _penX += width + Padding;
            _rowHeight = Math.Max(_rowHeight, height);
            Dirty = true;
            return true;
        }

        /// <summary>Returns the glyph's atlas entry, rasterizing+packing it on first use.</summary>
        public bool TryGetOrAdd(IGlyphSource font, int glyphId, out GlyphEntry entry)
        {
            if (_entries.TryGetValue(glyphId, out entry))
                return true;

            if (!font.TryGetGlyph(glyphId, out GlyphBitmap g))
                return false;

            if (g.Width == 0 || g.Height == 0)
            {
                // Blank glyph (e.g. space): metrics only, no atlas rectangle.
                entry = new GlyphEntry(0, 0, 0, 0, 0, 0, g.Advance, g.BearingX, g.BearingY);
                _entries[glyphId] = entry;
                return true;
            }

            // Shelf packing: wrap to a new row when the current one is full.
            if (_penX + g.Width + Padding > Width)
            {
                _penX = Padding;
                _penY += _rowHeight + Padding;
                _rowHeight = 0;
            }
            if (_penY + g.Height + Padding > Height)
                throw new InvalidOperationException("Glyph atlas is full.");

            for (int y = 0; y < g.Height; y++)
                for (int x = 0; x < g.Width; x++)
                    Pixels[(_penY + y) * Width + (_penX + x)] = g.Coverage[y * g.Width + x];

            entry = new GlyphEntry(
                _penX / (float)Width, _penY / (float)Height,
                (_penX + g.Width) / (float)Width, (_penY + g.Height) / (float)Height,
                g.Width, g.Height, g.Advance, g.BearingX, g.BearingY);
            _entries[glyphId] = entry;

            _penX += g.Width + Padding;
            _rowHeight = Math.Max(_rowHeight, g.Height);
            Dirty = true;
            return true;
        }
    }
}
