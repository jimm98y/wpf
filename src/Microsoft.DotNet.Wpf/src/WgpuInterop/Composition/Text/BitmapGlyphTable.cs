// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Colour BITMAP glyphs: CBDT/CBLC, the format Noto Color Emoji uses on Linux and Android.
//
// The other colour-glyph format, COLR/CPAL (see ColorTable.cs), describes a glyph as a stack of
// outlines with palette colours. This one does not describe shapes at all: each glyph is a whole
// PNG, stored at one or more fixed pixel sizes ("strikes"). A font in this format usually has no
// 'glyf' or 'loca' table whatsoever, which is why it could not previously be loaded at all.
//
// Layout, briefly, because the indirection is hard to follow otherwise:
//
//   CBLC  ->  bitmapSizeTable per strike (ppem, glyph range)
//         ->  indexSubTableArray          (which glyphs, and where their index subtable is)
//         ->  indexSubTable               (per-glyph offsets INTO CBDT)
//   CBDT  ->  per-glyph metrics + the PNG bytes
//
// Supported: index formats 1, 2, 3 and 5, and image formats 17, 18 and 19 (the PNG ones). Anything
// else is reported as "no bitmap for this glyph" rather than guessed at: the bitmap formats that are
// not PNG are uncompressed monochrome/greyscale strikes from the pre-colour EBDT era, which no
// colour emoji font uses, and drawing one wrong would be worse than not drawing it.
//

using System.Collections.Generic;

namespace Microsoft.Wpf.Interop.WebGpu.Composition.Text
{
    /// <summary>One glyph's colour bitmap: the encoded image plus where it sits on the baseline.</summary>
    internal readonly struct BitmapGlyph
    {
        /// <summary>The encoded image (PNG).</summary>
        public readonly byte[] Png;

        /// <summary>Bitmap size in pixels.</summary>
        public readonly int PixelWidth, PixelHeight;

        /// <summary>Offset from the pen position to the bitmap's top-left, in strike pixels.</summary>
        public readonly int BearingX, BearingY;

        /// <summary>The strike this came from, in pixels per em; the scale reference for the above.</summary>
        public readonly int PpemX, PpemY;

        public BitmapGlyph(byte[] png, int pixelWidth, int pixelHeight, int bearingX, int bearingY, int ppemX, int ppemY)
        {
            Png = png;
            PixelWidth = pixelWidth; PixelHeight = pixelHeight;
            BearingX = bearingX; BearingY = bearingY;
            PpemX = ppemX; PpemY = ppemY;
        }
    }

    /// <summary>A font whose colour glyphs are bitmaps rather than outlines.</summary>
    internal interface IBitmapGlyphFont
    {
        /// <summary>The glyph's colour bitmap, or false when it has none.</summary>
        bool TryGetGlyphBitmap(int glyphId, out BitmapGlyph glyph);
    }

    internal sealed class BitmapGlyphTable
    {
        private readonly byte[] _data;
        private readonly int _cbdt;
        private readonly List<Strike> _strikes = new();
        private readonly Dictionary<int, BitmapGlyph?> _cache = new();

        /// <summary>One CBLC strike: a pixel size, and the index subtables that map glyphs to CBDT.</summary>
        private readonly struct Strike
        {
            public readonly int PpemX, PpemY;
            public readonly int FirstGlyph, LastGlyph;
            public readonly int IndexSubTableArray;    // absolute
            public readonly int NumIndexSubTables;
            public Strike(int ppemX, int ppemY, int first, int last, int array, int count)
            {
                PpemX = ppemX; PpemY = ppemY; FirstGlyph = first; LastGlyph = last;
                IndexSubTableArray = array; NumIndexSubTables = count;
            }
        }

        // ---- sbix (Apple Color Emoji) ----
        //
        // A flatter design than CBDT/CBLC: each strike holds one offset per glyph straight into its
        // own data, and the image sits behind a tiny header giving its origin and graphic type. No
        // index subtables and no metrics records -- the pixel size comes from the PNG itself.
        private readonly List<int> _sbixStrikes = new();   // absolute offsets of each strike
        private readonly int _numGlyphs;

        public BitmapGlyphTable(byte[] data, int sbixOffset, int numGlyphs, bool sbix)
        {
            _data = data;
            _cbdt = -1;
            _numGlyphs = numGlyphs;

            int numStrikes = (int)U32(sbixOffset + 4);
            for (int i = 0; i < numStrikes; i++)
            {
                int at = sbixOffset + 8 + i * 4;
                if (at + 4 > _data.Length) break;
                int strike = sbixOffset + (int)U32(at);
                // Each strike needs its ppem/ppi pair plus one offset per glyph and a terminator.
                if (strike > 0 && strike + 4 + (numGlyphs + 1) * 4 <= _data.Length) _sbixStrikes.Add(strike);
            }
        }

        public BitmapGlyphTable(byte[] data, int cblcOffset, int cbdtOffset)
        {
            _data = data;
            _cbdt = cbdtOffset;

            int numSizes = (int)U32(cblcOffset + 4);
            // A malformed count must not walk off the table; each entry is 48 bytes.
            for (int i = 0; i < numSizes; i++)
            {
                int st = cblcOffset + 8 + i * 48;
                if (st + 48 > _data.Length) break;

                int arrayOffset = (int)U32(st) + cblcOffset;
                int numSubTables = (int)U32(st + 8);
                int startGlyph = U16(st + 40);
                int endGlyph = U16(st + 42);
                int ppemX = _data[st + 44];
                int ppemY = _data[st + 45];

                if (arrayOffset > 0 && arrayOffset < _data.Length && numSubTables > 0)
                    _strikes.Add(new Strike(ppemX, ppemY, startGlyph, endGlyph, arrayOffset, numSubTables));
            }
        }

        /// <summary>True when the font actually carries usable strikes.</summary>
        public bool HasStrikes => _strikes.Count > 0 || _sbixStrikes.Count > 0;

        public bool TryGetGlyphBitmap(int glyphId, out BitmapGlyph glyph)
        {
            if (_cache.TryGetValue(glyphId, out BitmapGlyph? cached))
            {
                glyph = cached ?? default;
                return cached.HasValue;
            }

            BitmapGlyph? found = Build(glyphId);
            _cache[glyphId] = found;
            glyph = found ?? default;
            return found.HasValue;
        }

        // The largest strike that has this glyph. Emoji fonts usually ship exactly one (Noto Color
        // Emoji is 136ppem); where there are several, the biggest scales down most cleanly.
        private BitmapGlyph? Build(int glyphId)
        {
            BitmapGlyph? best = null;
            foreach (Strike s in _strikes)
            {
                if (glyphId < s.FirstGlyph || glyphId > s.LastGlyph) continue;
                BitmapGlyph? g = FromStrike(s, glyphId);
                if (g is null) continue;
                if (best is null || g.Value.PpemY > best.Value.PpemY) best = g;
            }

            foreach (int strike in _sbixStrikes)
            {
                BitmapGlyph? g = FromSbixStrike(strike, glyphId);
                if (g is null) continue;
                if (best is null || g.Value.PpemY > best.Value.PpemY) best = g;
            }

            return best;
        }

        private BitmapGlyph? FromSbixStrike(int strike, int glyphId)
        {
            if (glyphId < 0 || glyphId >= _numGlyphs) return null;

            int ppem = U16(strike);
            int offsets = strike + 4;
            int o0 = (int)U32(offsets + glyphId * 4);
            int o1 = (int)U32(offsets + (glyphId + 1) * 4);
            // Equal offsets mean "this strike has no image for this glyph", which is normal.
            if (o1 <= o0) return null;

            int at = strike + o0;
            int length = o1 - o0;
            // 4 bytes of origin + a 4-byte graphic type before the image itself.
            if (length <= 8 || at < 0 || at + length > _data.Length) return null;

            int originX = (short)U16(at);
            int originY = (short)U16(at + 2);
            string graphicType = System.Text.Encoding.ASCII.GetString(_data, at + 4, 4);
            if (graphicType != "png ") return null;   // 'jpg ' and 'tiff' are permitted but not used by emoji

            var png = new byte[length - 8];
            System.Array.Copy(_data, at + 8, png, 0, png.Length);

            // sbix carries no pixel dimensions of its own, so they come from the PNG's IHDR; and the
            // origin is the offset of the image's BOTTOM-left from the pen, so the top edge -- which
            // is what BearingY means here -- is that plus the height.
            if (!TryReadPngSize(png, out int w, out int h)) return null;
            if (ppem <= 0) ppem = h;

            return new BitmapGlyph(png, w, h, originX, originY + h, ppem, ppem);
        }

        // The width and height out of a PNG's IHDR, which is always the first chunk.
        private static bool TryReadPngSize(byte[] png, out int width, out int height)
        {
            width = height = 0;
            if (png.Length < 24 || png[0] != 0x89 || png[1] != 'P' || png[2] != 'N' || png[3] != 'G') return false;
            if (png[12] != 'I' || png[13] != 'H' || png[14] != 'D' || png[15] != 'R') return false;
            width = (png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19];
            height = (png[20] << 24) | (png[21] << 16) | (png[22] << 8) | png[23];
            return width > 0 && height > 0;
        }

        private BitmapGlyph? FromStrike(in Strike strike, int glyphId)
        {
            for (int i = 0; i < strike.NumIndexSubTables; i++)
            {
                int rec = strike.IndexSubTableArray + i * 8;
                if (rec + 8 > _data.Length) return null;

                int first = U16(rec), last = U16(rec + 2);
                if (glyphId < first || glyphId > last) continue;

                int sub = strike.IndexSubTableArray + (int)U32(rec + 4);
                if (sub + 8 > _data.Length) return null;

                int indexFormat = U16(sub);
                int imageFormat = U16(sub + 2);
                int imageDataOffset = (int)U32(sub + 4) + _cbdt;

                int k = glyphId - first;
                int at, length;

                switch (indexFormat)
                {
                    case 1:   // u32 offsets, one per glyph plus a terminator
                    {
                        int table = sub + 8;
                        if (table + (k + 2) * 4 > _data.Length) return null;
                        int o0 = (int)U32(table + k * 4), o1 = (int)U32(table + (k + 1) * 4);
                        at = imageDataOffset + o0;
                        length = o1 - o0;
                        break;
                    }
                    case 3:   // u16 offsets, otherwise identical to format 1
                    {
                        int table = sub + 8;
                        if (table + (k + 2) * 2 > _data.Length) return null;
                        int o0 = U16(table + k * 2), o1 = U16(table + (k + 1) * 2);
                        at = imageDataOffset + o0;
                        length = o1 - o0;
                        break;
                    }
                    case 2:   // constant-size images, metrics shared by the whole subtable
                    {
                        int imageSize = (int)U32(sub + 8);
                        at = imageDataOffset + k * imageSize;
                        length = imageSize;
                        break;
                    }
                    case 5:   // constant-size images, sparse glyph list
                    {
                        int imageSize = (int)U32(sub + 8);
                        int numGlyphs = (int)U32(sub + 20);
                        int idArray = sub + 24;
                        int index = -1;
                        for (int j = 0; j < numGlyphs; j++)
                        {
                            if (idArray + j * 2 + 2 > _data.Length) break;
                            if (U16(idArray + j * 2) == glyphId) { index = j; break; }
                        }
                        if (index < 0) return null;
                        at = imageDataOffset + index * imageSize;
                        length = imageSize;
                        break;
                    }
                    default:
                        return null;   // formats 4 and anything unknown: not seen in colour emoji fonts
                }

                if (length <= 0 || at < 0 || at + length > _data.Length) return null;
                return ReadImage(imageFormat, at, length, strike);
            }
            return null;
        }

        // The image record: metrics (whose shape depends on the format) followed by the PNG.
        private BitmapGlyph? ReadImage(int imageFormat, int at, int length, in Strike strike)
        {
            int width, height, bearingX, bearingY, headerSize;

            switch (imageFormat)
            {
                case 17:   // smallGlyphMetrics + u32 length + PNG
                    if (length < 5 + 4) return null;
                    height = _data[at];
                    width = _data[at + 1];
                    bearingX = (sbyte)_data[at + 2];
                    bearingY = (sbyte)_data[at + 3];
                    headerSize = 5 + 4;
                    break;

                case 18:   // bigGlyphMetrics + u32 length + PNG
                    if (length < 8 + 4) return null;
                    height = _data[at];
                    width = _data[at + 1];
                    bearingX = (sbyte)_data[at + 2];
                    bearingY = (sbyte)_data[at + 3];
                    headerSize = 8 + 4;
                    break;

                case 19:   // u32 length + PNG; metrics come from the strike
                    if (length < 4) return null;
                    width = strike.PpemX;
                    height = strike.PpemY;
                    bearingX = 0;
                    bearingY = strike.PpemY;
                    headerSize = 4;
                    break;

                default:
                    return null;   // an uncompressed (non-PNG) bitmap format: see this file's header
            }

            int declared = (int)U32(at + headerSize - 4);
            int available = length - headerSize;
            int pngLength = declared > 0 && declared <= available ? declared : available;
            if (pngLength <= 0) return null;

            var png = new byte[pngLength];
            System.Array.Copy(_data, at + headerSize, png, 0, pngLength);

            // Formats 17/18 carry the real pixel size in their metrics; a zero there means the
            // record is degenerate, so fall back to the strike rather than emitting an empty glyph.
            if (width <= 0 || height <= 0) { width = strike.PpemX; height = strike.PpemY; }

            return new BitmapGlyph(png, width, height, bearingX, bearingY, strike.PpemX, strike.PpemY);
        }

        private int U16(int o) => (_data[o] << 8) | _data[o + 1];
        private uint U32(int o) => ((uint)_data[o] << 24) | ((uint)_data[o + 1] << 16) | ((uint)_data[o + 2] << 8) | _data[o + 3];
    }
}
