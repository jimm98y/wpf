// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Color glyphs (OpenType COLR/CPAL) -- the layered color format used by emoji and
// color icon fonts (e.g. Segoe UI Emoji). A base glyph maps to a back-to-front
// stack of layers; each layer is an ordinary outline glyph filled with a palette
// colour (or the run's foreground colour). We parse COLR version 0 (the flat
// layered format) + CPAL; the outlines themselves come from the font's normal
// glyf/CFF source, so a color glyph renders as several solid fills.
//
// COLR v1 (gradients/transforms/compositing) is not handled -- a glyph that only
// exists in the v1 table falls back to a monochrome outline.
//

using System.Collections.Generic;
using Microsoft.Wpf.Interop.WebGpu.Composition;

namespace Microsoft.Wpf.Interop.WebGpu.Composition.Text
{
    /// <summary>One layer of a color glyph: an outline glyph id + its colour
    /// (<see cref="Color"/> is null when the layer uses the run's foreground brush).</summary>
    internal readonly struct ColorGlyphLayer
    {
        public readonly int GlyphId;
        public readonly RgbaColor? Color;
        public ColorGlyphLayer(int glyphId, RgbaColor? color) { GlyphId = glyphId; Color = color; }
    }

    /// <summary>A font that can decompose a glyph into colored layers (COLR/CPAL).</summary>
    internal interface IColorGlyphFont
    {
        /// <summary>Returns the back-to-front color layers of <paramref name="glyphId"/>,
        /// or false if it is an ordinary (non-color) glyph.</summary>
        bool TryGetColorLayers(int glyphId, out IReadOnlyList<ColorGlyphLayer> layers);
    }

    internal sealed class ColorTable
    {
        private const int NoPaletteIndex = 0xFFFF;   // layer uses the foreground colour

        private readonly byte[] _data;
        private readonly int _colr;
        private readonly int _cpal;

        // COLR v0 base-glyph + layer records.
        private readonly int _numBaseGlyphs;
        private readonly int _baseGlyphRecords;   // absolute offset, 6 bytes each, sorted by glyph id
        private readonly int _layerRecords;       // absolute offset, 4 bytes each

        // CPAL palette 0.
        private readonly int _palette0First;      // first color-record index of palette 0
        private readonly int _numColorRecords;
        private readonly int _colorRecords;       // absolute offset, 4 bytes (BGRA) each

        private readonly Dictionary<int, ColorGlyphLayer[]> _cache = new();

        public ColorTable(byte[] data, int colrOffset, int cpalOffset)
        {
            _data = data;
            _colr = colrOffset;
            _cpal = cpalOffset;

            // COLR header (the v0 portion exists for every version).
            _numBaseGlyphs = U16(_colr + 2);
            _baseGlyphRecords = _colr + (int)U32(_colr + 4);
            _layerRecords = _colr + (int)U32(_colr + 8);

            // CPAL header (v0 fields; v1 adds trailing data we don't need).
            _numColorRecords = U16(_cpal + 6);
            _colorRecords = _cpal + (int)U32(_cpal + 8);
            int numPalettes = U16(_cpal + 4);
            _palette0First = numPalettes > 0 ? U16(_cpal + 12) : 0;
        }

        public bool TryGetColorLayers(int baseGid, out IReadOnlyList<ColorGlyphLayer> layers)
        {
            if (_cache.TryGetValue(baseGid, out ColorGlyphLayer[]? cached))
            {
                layers = cached;
                return cached.Length > 0;
            }

            ColorGlyphLayer[] result = Build(baseGid);
            _cache[baseGid] = result;
            layers = result;
            return result.Length > 0;
        }

        // Binary-searches the (glyph-id-sorted) base-glyph records, then reads each
        // referenced layer record and resolves its palette colour.
        private ColorGlyphLayer[] Build(int baseGid)
        {
            int lo = 0, hi = _numBaseGlyphs - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                int rec = _baseGlyphRecords + mid * 6;
                int gid = U16(rec);
                if (baseGid < gid) hi = mid - 1;
                else if (baseGid > gid) lo = mid + 1;
                else
                {
                    int firstLayer = U16(rec + 2);
                    int numLayers = U16(rec + 4);
                    var layers = new ColorGlyphLayer[numLayers];
                    for (int k = 0; k < numLayers; k++)
                    {
                        int lr = _layerRecords + (firstLayer + k) * 4;
                        int layerGid = U16(lr);
                        int paletteIndex = U16(lr + 2);
                        RgbaColor? color = paletteIndex == NoPaletteIndex ? null : GetColor(paletteIndex);
                        layers[k] = new ColorGlyphLayer(layerGid, color);
                    }
                    return layers;
                }
            }
            return System.Array.Empty<ColorGlyphLayer>();
        }

        // CPAL colour records are sRGB BGRA; brush colours in this engine are linear
        // scRGB, so sRGB-decode the channels (alpha stays linear).
        private RgbaColor GetColor(int paletteEntry)
        {
            int recIndex = _palette0First + paletteEntry;
            if (recIndex < 0 || recIndex >= _numColorRecords) return new RgbaColor(0, 0, 0, 1);
            int o = _colorRecords + recIndex * 4;
            byte b = _data[o], g = _data[o + 1], r = _data[o + 2], a = _data[o + 3];
            return new RgbaColor(SrgbToLinear(r), SrgbToLinear(g), SrgbToLinear(b), a / 255f);
        }

        private static float SrgbToLinear(byte v)
        {
            float c = v / 255f;
            return c <= 0.04045f ? c / 12.92f : (float)System.Math.Pow((c + 0.055f) / 1.055f, 2.4);
        }

        private int U16(int o) => (_data[o] << 8) | _data[o + 1];
        private uint U32(int o)
            => ((uint)_data[o] << 24) | ((uint)_data[o + 1] << 16) | ((uint)_data[o + 2] << 8) | _data[o + 3];
    }
}
