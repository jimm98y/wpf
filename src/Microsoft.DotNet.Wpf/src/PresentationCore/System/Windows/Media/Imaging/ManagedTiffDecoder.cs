// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Managed TIFF decoder for platforms without native WIC. The counterpart of ManagedTiffEncoder,
// which has shipped for a while -- so off Windows this stack could WRITE a TIFF it could not READ
// back.
//
// Reading TIFF is a much wider problem than writing it. The encoder picks one shape (little-endian,
// uncompressed, one strip, 8-bit RGBA) and writes it; a decoder meets whatever a scanner, a fax
// gateway or Photoshop chose thirty years ago. What is supported here is BASELINE TIFF plus the two
// compressions that are universal in practice:
//
//   byte order      II and MM
//   compression     1 none, 5 LZW, 32773 PackBits, 8/32946 Deflate
//   photometric     0 WhiteIsZero, 1 BlackIsZero, 2 RGB, 3 Palette
//   bit depth       1, 4, 8 and 16 bits per sample
//   samples         1 (grey/palette), 3 (RGB), 4 (RGB + alpha), extra samples beyond that ignored
//   predictor       1 none, 2 horizontal differencing
//   layout          strips and TILES, chunky
//   pages           every IFD in the chain
//
// Rejected with a clear message rather than a wrong picture: PlanarConfiguration 2 (separate
// planes). It is legal and it is rare; guessing at it would produce a plausible but scrambled
// bitmap, which is worse than a NotSupportedException naming the reason.
//
// Tiles are the same idea as strips in two dimensions, with one trap. A tile is always stored FULL
// SIZE: an image whose width is not a multiple of the tile width still ends each row of tiles with
// a complete tile, and the overhang is padding the decoder has to drop. So the row stride inside a
// tile comes from the TILE width and never from the image width -- read it from the image width and
// every row after the first tile column is shifted, which looks like a smeared or sheared picture
// rather than like a decoder bug.
//
// Two details are worth knowing before reading the code, because both are silent-corruption traps:
//
//   * TIFF's LZW is NOT GIF's. The codes are packed MSB-first (GIF is LSB-first) and the code width
//     grows one code EARLY -- at 511, 1023, 2047 rather than 512, 1024, 2048. Adobe's own early
//     encoder had the off-by-one and the format kept it. A GIF LZW loop pointed at TIFF data decodes
//     the first few dozen pixels correctly and then drifts, which is exactly the kind of bug that
//     survives a smoke test.
//
//   * A SHORT that fits inline sits in the FIRST two bytes of the entry's value field, not the last,
//     because the field is four bytes read according to the type rather than an integer.
//

#nullable enable

using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace System.Windows.Media.Imaging
{
    /// <summary>One decoded TIFF page: BGRA32 pixels plus the resolution the file recorded.</summary>
    internal readonly struct ManagedTiffPage
    {
        internal ManagedTiffPage(byte[] bgra, int width, int height, double dpiX, double dpiY)
        {
            Bgra = bgra;
            Width = width;
            Height = height;
            DpiX = dpiX;
            DpiY = dpiY;
        }

        internal byte[] Bgra { get; }
        internal int Width { get; }
        internal int Height { get; }
        internal double DpiX { get; }
        internal double DpiY { get; }
    }

    internal static class ManagedTiffDecoder
    {
        // Tags this decoder reads. Anything else in the IFD is skipped.
        private const ushort TagImageWidth = 256;
        private const ushort TagImageLength = 257;
        private const ushort TagBitsPerSample = 258;
        private const ushort TagCompression = 259;
        private const ushort TagPhotometric = 262;
        private const ushort TagStripOffsets = 273;
        private const ushort TagSamplesPerPixel = 277;
        private const ushort TagRowsPerStrip = 278;
        private const ushort TagStripByteCounts = 279;
        private const ushort TagXResolution = 282;
        private const ushort TagYResolution = 283;
        private const ushort TagPlanarConfiguration = 284;
        private const ushort TagResolutionUnit = 296;
        private const ushort TagPredictor = 317;
        private const ushort TagColorMap = 320;
        private const ushort TagTileWidth = 322;
        private const ushort TagTileLength = 323;
        private const ushort TagTileOffsets = 324;
        private const ushort TagTileByteCounts = 325;
        private const ushort TagExtraSamples = 338;
        private const ushort TagSampleFormat = 339;

        private const ushort CompressionNone = 1;
        private const ushort CompressionLzw = 5;
        private const ushort CompressionDeflateAdobe = 8;
        private const ushort CompressionPackBits = 32773;
        private const ushort CompressionDeflate = 32946;

        private const ushort PhotometricWhiteIsZero = 0;
        private const ushort PhotometricBlackIsZero = 1;
        private const ushort PhotometricRgb = 2;
        private const ushort PhotometricPalette = 3;

        /// <summary>True when the bytes open with either TIFF byte-order mark and the magic 42.</summary>
        internal static bool IsTiff(byte[] data)
        {
            if (data.Length < 8) return false;

            if (data[0] == 'I' && data[1] == 'I')
            {
                return data[2] == 42 && data[3] == 0;
            }

            if (data[0] == 'M' && data[1] == 'M')
            {
                return data[2] == 0 && data[3] == 42;
            }

            return false;
        }

        /// <summary>Decodes every page in the IFD chain.</summary>
        internal static List<ManagedTiffPage> Decode(byte[] data)
        {
            if (!IsTiff(data))
            {
                throw new InvalidDataException("not a TIFF: the byte-order mark or magic number did not match.");
            }

            bool bigEndian = data[0] == 'M';
            var pages = new List<ManagedTiffPage>();

            long ifdOffset = ReadU32(data, 4, bigEndian);

            // A malformed file can point one IFD at another in a cycle. Every offset visited is
            // remembered so the walk terminates instead of allocating pages until the process dies.
            var visited = new HashSet<long>();

            while (ifdOffset > 0 && ifdOffset + 2 <= data.Length && visited.Add(ifdOffset))
            {
                pages.Add(DecodePage(data, (int)ifdOffset, bigEndian, out long next));
                ifdOffset = next;
            }

            if (pages.Count == 0)
            {
                throw new InvalidDataException("the TIFF contained no image directories.");
            }

            return pages;
        }

        // ---- one page --------------------------------------------------------------------

        private static ManagedTiffPage DecodePage(byte[] data, int ifdOffset, bool bigEndian, out long nextIfd)
        {
            var entries = new Dictionary<ushort, IfdEntry>();

            int count = ReadU16(data, ifdOffset, bigEndian);
            int entryBase = ifdOffset + 2;

            for (int i = 0; i < count; i++)
            {
                int offset = entryBase + i * 12;
                if (offset + 12 > data.Length) break;

                var entry = new IfdEntry(
                    (ushort)ReadU16(data, offset, bigEndian),
                    (ushort)ReadU16(data, offset + 2, bigEndian),
                    ReadU32(data, offset + 4, bigEndian),
                    offset + 8);

                entries[entry.Tag] = entry;
            }

            int afterEntries = entryBase + count * 12;
            nextIfd = afterEntries + 4 <= data.Length ? ReadU32(data, afterEntries, bigEndian) : 0;

            int planar = (int)GetScalar(data, entries, TagPlanarConfiguration, bigEndian, 1);
            if (planar != 1)
            {
                throw new NotSupportedException("TIFF PlanarConfiguration 2 (separate planes) is not supported.");
            }

            int width = (int)GetScalar(data, entries, TagImageWidth, bigEndian, 0);
            int height = (int)GetScalar(data, entries, TagImageLength, bigEndian, 0);
            if (width <= 0 || height <= 0)
            {
                throw new InvalidDataException($"TIFF page is {width}x{height}.");
            }

            int samplesPerPixel = (int)GetScalar(data, entries, TagSamplesPerPixel, bigEndian, 1);
            samplesPerPixel = Math.Max(1, samplesPerPixel);

            uint[] bitsPerSample = GetArray(data, entries, TagBitsPerSample, bigEndian);
            int bits = bitsPerSample.Length > 0 ? (int)bitsPerSample[0] : 1;
            if (bits is not (1 or 4 or 8 or 16))
            {
                throw new NotSupportedException($"TIFF with {bits} bits per sample is not supported.");
            }

            for (int i = 1; i < bitsPerSample.Length && i < samplesPerPixel; i++)
            {
                if (bitsPerSample[i] != bits)
                {
                    throw new NotSupportedException("TIFF with mixed sample depths is not supported.");
                }
            }

            uint[] sampleFormat = GetArray(data, entries, TagSampleFormat, bigEndian);
            if (sampleFormat.Length > 0 && sampleFormat[0] == 3)
            {
                throw new NotSupportedException("floating-point TIFF samples are not supported.");
            }

            int compression = (int)GetScalar(data, entries, TagCompression, bigEndian, CompressionNone);
            int photometric = (int)GetScalar(data, entries, TagPhotometric, bigEndian,
                                             samplesPerPixel >= 3 ? PhotometricRgb : PhotometricBlackIsZero);
            int predictor = (int)GetScalar(data, entries, TagPredictor, bigEndian, 1);

            uint[] extraSamples = GetArray(data, entries, TagExtraSamples, bigEndian);
            // ExtraSamples 1 is ASSOCIATED alpha, i.e. already multiplied into the colour. WPF wants
            // straight alpha, so those samples have to be divided back out below.
            bool premultiplied = extraSamples.Length > 0 && extraSamples[0] == 1;

            uint[] palette = photometric == PhotometricPalette
                ? GetArray(data, entries, TagColorMap, bigEndian)
                : Array.Empty<uint>();

            double dpiX = ReadResolution(data, entries, TagXResolution, bigEndian);
            double dpiY = ReadResolution(data, entries, TagYResolution, bigEndian);
            int resolutionUnit = (int)GetScalar(data, entries, TagResolutionUnit, bigEndian, 2);
            if (resolutionUnit == 3)
            {
                // Centimetres.
                dpiX *= 2.54;
                dpiY *= 2.54;
            }
            else if (resolutionUnit != 2)
            {
                dpiX = 0;
                dpiY = 0;
            }

            var bgra = new byte[width * height * 4];

            // Strips and tiles differ only in the GEOMETRY of the block: how big it is, where it
            // lands, and how long a row inside it is. Everything after that -- the bounds check, the
            // decompression, the predictor, the pixel expansion -- is identical, so it is written
            // once here and the two layouts below only work out the numbers.
            bool DecodeBlock(int offset, int byteCount, int blockBytesPerRow, int blockRows,
                             int blockPixelWidth, int firstRow, int rowCount,
                             int firstColumn, int columnCount)
            {
                if (offset < 0 || byteCount < 0 || (long)offset + byteCount > data.Length)
                {
                    return false;   // a truncated file keeps the blocks that were whole
                }

                byte[] raw = Decompress(data, offset, byteCount, compression, blockBytesPerRow * blockRows);

                if (predictor == 2)
                {
                    ApplyHorizontalPredictor(raw, blockBytesPerRow, rowCount, blockPixelWidth, samplesPerPixel, bits);
                }

                EmitBlock(raw, bgra, width, blockBytesPerRow, firstRow, rowCount, firstColumn, columnCount,
                          samplesPerPixel, bits, photometric, palette, premultiplied);
                return true;
            }

            if (entries.ContainsKey(TagTileWidth))
            {
                int tileWidth = (int)GetScalar(data, entries, TagTileWidth, bigEndian, 0);
                int tileHeight = (int)GetScalar(data, entries, TagTileLength, bigEndian, 0);
                if (tileWidth <= 0 || tileHeight <= 0)
                {
                    throw new InvalidDataException($"the TIFF page declares a {tileWidth}x{tileHeight} tile.");
                }

                uint[] tileOffsets = GetArray(data, entries, TagTileOffsets, bigEndian);
                uint[] tileByteCounts = GetArray(data, entries, TagTileByteCounts, bigEndian);
                if (tileOffsets.Length == 0)
                {
                    throw new InvalidDataException("the TIFF page is tiled but has no tile offsets.");
                }

                // From the TILE width, not the image width -- see the header. A stored tile is always
                // whole, so the padding on a right-hand or bottom edge tile is part of the data and
                // has to be stepped over rather than assumed away.
                int tileBytesPerRow = (tileWidth * samplesPerPixel * bits + 7) / 8;
                int tilesAcross = (width + tileWidth - 1) / tileWidth;

                for (int tile = 0; tile < tileOffsets.Length; tile++)
                {
                    int firstColumn = tile % tilesAcross * tileWidth;
                    int firstRow = tile / tilesAcross * tileHeight;
                    if (firstRow >= height) break;

                    int byteCount = tile < tileByteCounts.Length
                        ? (int)tileByteCounts[tile]
                        : tileBytesPerRow * tileHeight;

                    if (!DecodeBlock((int)tileOffsets[tile], byteCount, tileBytesPerRow, tileHeight,
                                     tileWidth, firstRow, Math.Min(tileHeight, height - firstRow),
                                     firstColumn, Math.Min(tileWidth, width - firstColumn)))
                    {
                        break;
                    }
                }

                return new ManagedTiffPage(bgra, width, height, dpiX, dpiY);
            }

            uint[] stripOffsets = GetArray(data, entries, TagStripOffsets, bigEndian);
            uint[] stripByteCounts = GetArray(data, entries, TagStripByteCounts, bigEndian);
            if (stripOffsets.Length == 0)
            {
                throw new InvalidDataException("the TIFF page has no strip offsets.");
            }

            long rowsPerStripValue = GetScalar(data, entries, TagRowsPerStrip, bigEndian, uint.MaxValue);
            int rowsPerStrip = rowsPerStripValue >= height || rowsPerStripValue <= 0
                ? height
                : (int)rowsPerStripValue;

            // Rows are padded to a byte boundary; sub-byte depths make that padding visible.
            int bytesPerRow = (width * samplesPerPixel * bits + 7) / 8;

            for (int strip = 0; strip < stripOffsets.Length; strip++)
            {
                int firstRow = strip * rowsPerStrip;
                if (firstRow >= height) break;

                int rowsInStrip = Math.Min(rowsPerStrip, height - firstRow);

                int byteCount = strip < stripByteCounts.Length
                    ? (int)stripByteCounts[strip]
                    : bytesPerRow * rowsInStrip;

                if (!DecodeBlock((int)stripOffsets[strip], byteCount, bytesPerRow, rowsInStrip,
                                 width, firstRow, rowsInStrip, 0, width))
                {
                    break;
                }
            }

            return new ManagedTiffPage(bgra, width, height, dpiX, dpiY);
        }

        /// <summary>
        /// Expands one block -- a strip, or a tile -- into the BGRA canvas.
        /// <paramref name="bytesPerRow"/> is the stride WITHIN the block, which for a tile is wider
        /// than <paramref name="columnCount"/> whenever the tile hangs over an edge of the image.
        /// </summary>
        private static void EmitBlock(byte[] raw, byte[] bgra, int width, int bytesPerRow,
                                      int firstRow, int rowCount, int firstColumn, int columnCount,
                                      int samplesPerPixel, int bits,
                                      int photometric, uint[] palette, bool premultiplied)
        {
            int paletteEntries = palette.Length / 3;
            int maxValue = (1 << bits) - 1;

            for (int row = 0; row < rowCount; row++)
            {
                int rowStart = row * bytesPerRow;
                if (rowStart + bytesPerRow > raw.Length) break;

                int destinationRow = (firstRow + row) * width * 4;

                for (int x = 0; x < columnCount; x++)
                {
                    int destination = destinationRow + (firstColumn + x) * 4;
                    int sampleBase = x * samplesPerPixel;

                    byte r, g, b;
                    byte a = 255;

                    switch (photometric)
                    {
                        case PhotometricPalette:
                        {
                            int index = ReadSample(raw, rowStart, sampleBase, bits);
                            if (index < paletteEntries)
                            {
                                // ColorMap is 16-bit per channel, all reds then all greens then all
                                // blues -- not interleaved triples, which is the usual misreading.
                                r = (byte)(palette[index] >> 8);
                                g = (byte)(palette[paletteEntries + index] >> 8);
                                b = (byte)(palette[2 * paletteEntries + index] >> 8);
                            }
                            else
                            {
                                r = g = b = 0;
                            }
                            break;
                        }

                        case PhotometricRgb:
                        {
                            r = Scale(ReadSample(raw, rowStart, sampleBase, bits), maxValue);
                            g = Scale(ReadSample(raw, rowStart, sampleBase + 1, bits), maxValue);
                            b = Scale(ReadSample(raw, rowStart, sampleBase + 2, bits), maxValue);
                            if (samplesPerPixel >= 4)
                            {
                                a = Scale(ReadSample(raw, rowStart, sampleBase + 3, bits), maxValue);
                            }
                            break;
                        }

                        case PhotometricWhiteIsZero:
                        {
                            byte grey = Scale(maxValue - ReadSample(raw, rowStart, sampleBase, bits), maxValue);
                            r = g = b = grey;
                            if (samplesPerPixel >= 2)
                            {
                                a = Scale(ReadSample(raw, rowStart, sampleBase + 1, bits), maxValue);
                            }
                            break;
                        }

                        case PhotometricBlackIsZero:
                        default:
                        {
                            byte grey = Scale(ReadSample(raw, rowStart, sampleBase, bits), maxValue);
                            r = g = b = grey;
                            if (samplesPerPixel >= 2)
                            {
                                a = Scale(ReadSample(raw, rowStart, sampleBase + 1, bits), maxValue);
                            }
                            break;
                        }
                    }

                    if (premultiplied && a is > 0 and < 255)
                    {
                        r = Unpremultiply(r, a);
                        g = Unpremultiply(g, a);
                        b = Unpremultiply(b, a);
                    }

                    bgra[destination + 0] = b;
                    bgra[destination + 1] = g;
                    bgra[destination + 2] = r;
                    bgra[destination + 3] = a;
                }
            }
        }

        private static byte Unpremultiply(byte channel, byte alpha) =>
            (byte)Math.Min(255, channel * 255 / alpha);

        private static byte Scale(int value, int maxValue)
        {
            if (maxValue == 255) return (byte)Math.Clamp(value, 0, 255);
            if (maxValue <= 0) return 0;
            return (byte)Math.Clamp(value * 255 / maxValue, 0, 255);
        }

        /// <summary>Reads sample <paramref name="index"/> of a row at the given bit depth.</summary>
        private static int ReadSample(byte[] raw, int rowStart, int index, int bits)
        {
            switch (bits)
            {
                case 8:
                {
                    int offset = rowStart + index;
                    return offset < raw.Length ? raw[offset] : 0;
                }

                case 16:
                {
                    // Reported as 8-bit: the high byte is the value, which is what every viewer
                    // shows for a 16-bit image and what BGRA32 can hold.
                    int offset = rowStart + index * 2;
                    return offset + 1 < raw.Length ? raw[offset + 1] << 8 | raw[offset] : 0;
                }

                default:
                {
                    // 1 and 4 bits: samples are packed MSB-first within each byte.
                    int bitOffset = index * bits;
                    int offset = rowStart + bitOffset / 8;
                    if (offset >= raw.Length) return 0;

                    int shift = 8 - bits - (bitOffset % 8);
                    return (raw[offset] >> shift) & ((1 << bits) - 1);
                }
            }
        }

        /// <summary>
        ///  Undoes predictor 2, where each sample was stored as its difference from the sample one
        ///  pixel to its left. Encoders use it because a smooth gradient turns into a run of equal
        ///  small numbers, which LZW and Deflate then compress far better than the original values.
        /// </summary>
        private static void ApplyHorizontalPredictor(byte[] raw, int bytesPerRow, int rows,
                                                     int width, int samplesPerPixel, int bits)
        {
            if (bits != 8 && bits != 16)
            {
                // The predictor is only defined for whole-byte samples.
                return;
            }

            for (int row = 0; row < rows; row++)
            {
                int rowStart = row * bytesPerRow;

                if (bits == 8)
                {
                    for (int i = samplesPerPixel; i < bytesPerRow; i++)
                    {
                        int offset = rowStart + i;
                        if (offset >= raw.Length) break;
                        raw[offset] = (byte)(raw[offset] + raw[offset - samplesPerPixel]);
                    }
                }
                else
                {
                    int stride = samplesPerPixel * 2;
                    for (int i = stride; i + 1 < bytesPerRow; i += 2)
                    {
                        int offset = rowStart + i;
                        if (offset + 1 >= raw.Length) break;

                        int previous = raw[offset - stride] | (raw[offset - stride + 1] << 8);
                        int current = raw[offset] | (raw[offset + 1] << 8);
                        int sum = (current + previous) & 0xFFFF;

                        raw[offset] = (byte)sum;
                        raw[offset + 1] = (byte)(sum >> 8);
                    }
                }
            }
        }

        // ---- compression -----------------------------------------------------------------

        private static byte[] Decompress(byte[] data, int offset, int byteCount, int compression, int expected)
        {
            switch (compression)
            {
                case CompressionNone:
                {
                    var raw = new byte[Math.Max(expected, byteCount)];
                    Array.Copy(data, offset, raw, 0, byteCount);
                    return raw;
                }

                case CompressionLzw:
                    return TiffLzwDecode(data, offset, byteCount, expected);

                case CompressionPackBits:
                    return PackBitsDecode(data, offset, byteCount, expected);

                case CompressionDeflate:
                case CompressionDeflateAdobe:
                    return InflateDecode(data, offset, byteCount, expected);

                default:
                    throw new NotSupportedException($"TIFF compression {compression} is not supported.");
            }
        }

        /// <summary>
        ///  TIFF's LZW: MSB-first packing and an early code-width bump (see the file header).
        /// </summary>
        private static byte[] TiffLzwDecode(byte[] data, int offset, int byteCount, int expected)
        {
            const int ClearCode = 256;
            const int EndCode = 257;
            const int MaxCodes = 4096;

            var prefix = new short[MaxCodes];
            var suffix = new byte[MaxCodes];
            var stack = new byte[MaxCodes];

            for (int i = 0; i < 256; i++)
            {
                prefix[i] = -1;
                suffix[i] = (byte)i;
            }

            var output = new byte[expected];
            int written = 0;

            int codeSize = 9;
            int nextCode = EndCode + 1;
            int previous = -1;

            int bitPosition = 0;
            int totalBits = byteCount * 8;

            while (bitPosition + codeSize <= totalBits && written < expected)
            {
                // MSB-first: assemble the code from the top of each byte down.
                int code = 0;
                for (int i = 0; i < codeSize; i++)
                {
                    int bit = bitPosition + i;
                    int b = data[offset + (bit >> 3)];
                    code = (code << 1) | ((b >> (7 - (bit & 7))) & 1);
                }
                bitPosition += codeSize;

                if (code == ClearCode)
                {
                    codeSize = 9;
                    nextCode = EndCode + 1;
                    previous = -1;
                    continue;
                }

                if (code == EndCode)
                {
                    break;
                }

                int current;
                int stackTop = 0;

                if (code < nextCode && (code < 256 || code > EndCode))
                {
                    current = code;
                }
                else if (previous >= 0)
                {
                    stack[stackTop++] = FirstCharacter(prefix, suffix, previous);
                    current = previous;
                }
                else
                {
                    break;
                }

                while (current >= 256)
                {
                    if (stackTop >= stack.Length || current >= MaxCodes) goto done;
                    stack[stackTop++] = suffix[current];
                    current = prefix[current];
                    if (current < 0) goto done;
                }

                stack[stackTop++] = suffix[current];

                for (int i = stackTop - 1; i >= 0 && written < output.Length; i--)
                {
                    output[written++] = stack[i];
                }

                if (previous >= 0 && nextCode < MaxCodes)
                {
                    prefix[nextCode] = (short)previous;
                    suffix[nextCode] = suffix[current];
                    nextCode++;
                }

                previous = code;

                // The EARLY bump: one code before the width would actually overflow.
                if (nextCode + 1 >= (1 << codeSize) && codeSize < 12)
                {
                    codeSize++;
                }
            }

        done:
            return output;
        }

        private static byte FirstCharacter(short[] prefix, byte[] suffix, int code)
        {
            int guard = 0;
            while (prefix[code] >= 0 && guard++ < 4096)
            {
                code = prefix[code];
            }
            return suffix[code];
        }

        /// <summary>
        ///  PackBits: a run-length scheme where a signed length byte says what follows. 0..127 means
        ///  that many + 1 literal bytes; -1..-127 means the next byte repeated 1 - n times; -128 is
        ///  a no-op.
        /// </summary>
        private static byte[] PackBitsDecode(byte[] data, int offset, int byteCount, int expected)
        {
            var output = new byte[expected];
            int written = 0;
            int end = offset + byteCount;

            while (offset < end && written < expected)
            {
                sbyte length = (sbyte)data[offset++];

                if (length >= 0)
                {
                    int run = length + 1;
                    for (int i = 0; i < run && offset < end && written < expected; i++)
                    {
                        output[written++] = data[offset++];
                    }
                }
                else if (length != -128)
                {
                    if (offset >= end) break;
                    byte value = data[offset++];
                    int run = 1 - length;
                    for (int i = 0; i < run && written < expected; i++)
                    {
                        output[written++] = value;
                    }
                }
            }

            return output;
        }

        private static byte[] InflateDecode(byte[] data, int offset, int byteCount, int expected)
        {
            var output = new byte[expected];

            using var source = new MemoryStream(data, offset, byteCount, writable: false);
            using var inflate = new ZLibStream(source, CompressionMode.Decompress);

            int written = 0;
            while (written < expected)
            {
                int read = inflate.Read(output, written, expected - written);
                if (read <= 0) break;
                written += read;
            }

            return output;
        }

        // ---- IFD primitives --------------------------------------------------------------

        private readonly struct IfdEntry
        {
            internal IfdEntry(ushort tag, ushort type, long count, int valueOffset)
            {
                Tag = tag;
                Type = type;
                Count = count;
                ValueOffset = valueOffset;
            }

            internal ushort Tag { get; }
            internal ushort Type { get; }
            internal long Count { get; }

            /// <summary>Offset of the entry's four-byte value field, not of the value itself.</summary>
            internal int ValueOffset { get; }
        }

        private static int TypeSize(ushort type) => type switch
        {
            1 or 2 or 6 or 7 => 1,      // BYTE, ASCII, SBYTE, UNDEFINED
            3 or 8 => 2,                // SHORT, SSHORT
            4 or 9 or 11 => 4,          // LONG, SLONG, FLOAT
            5 or 10 or 12 => 8,         // RATIONAL, SRATIONAL, DOUBLE
            _ => 0,
        };

        private static long GetScalar(byte[] data, Dictionary<ushort, IfdEntry> entries, ushort tag,
                                      bool bigEndian, long fallback)
        {
            uint[] values = GetArray(data, entries, tag, bigEndian);
            return values.Length > 0 ? values[0] : fallback;
        }

        /// <summary>
        ///  Reads an entry's values as unsigned 32-bit numbers, following the offset when they do not
        ///  fit in the four-byte value field.
        /// </summary>
        private static uint[] GetArray(byte[] data, Dictionary<ushort, IfdEntry> entries, ushort tag, bool bigEndian)
        {
            if (!entries.TryGetValue(tag, out IfdEntry entry)) return Array.Empty<uint>();

            int size = TypeSize(entry.Type);
            if (size == 0 || entry.Count <= 0) return Array.Empty<uint>();

            long totalBytes = size * entry.Count;
            int start = totalBytes <= 4 ? entry.ValueOffset : (int)ReadU32(data, entry.ValueOffset, bigEndian);

            if (start < 0 || start + totalBytes > data.Length) return Array.Empty<uint>();

            // A RATIONAL's numerator is what callers of this helper want; the denominator is read
            // separately by ReadResolution, the only place a ratio actually matters.
            int count = (int)Math.Min(entry.Count, 1 << 20);
            var values = new uint[count];

            for (int i = 0; i < count; i++)
            {
                int offset = start + i * size;
                values[i] = size switch
                {
                    1 => data[offset],
                    2 => (uint)ReadU16(data, offset, bigEndian),
                    _ => (uint)ReadU32(data, offset, bigEndian),
                };
            }

            return values;
        }

        private static double ReadResolution(byte[] data, Dictionary<ushort, IfdEntry> entries,
                                             ushort tag, bool bigEndian)
        {
            if (!entries.TryGetValue(tag, out IfdEntry entry) || entry.Type != 5) return 0;

            int start = (int)ReadU32(data, entry.ValueOffset, bigEndian);
            if (start < 0 || start + 8 > data.Length) return 0;

            long numerator = ReadU32(data, start, bigEndian);
            long denominator = ReadU32(data, start + 4, bigEndian);

            return denominator == 0 ? 0 : (double)numerator / denominator;
        }

        private static int ReadU16(byte[] data, int offset, bool bigEndian)
        {
            if (offset + 2 > data.Length) return 0;
            return bigEndian
                ? (data[offset] << 8) | data[offset + 1]
                : data[offset] | (data[offset + 1] << 8);
        }

        private static long ReadU32(byte[] data, int offset, bool bigEndian)
        {
            if (offset + 4 > data.Length) return 0;
            return bigEndian
                ? ((long)data[offset] << 24) | ((long)data[offset + 1] << 16) | ((long)data[offset + 2] << 8) | data[offset + 3]
                : data[offset] | ((long)data[offset + 1] << 8) | ((long)data[offset + 2] << 16) | ((long)data[offset + 3] << 24);
        }
    }
}
