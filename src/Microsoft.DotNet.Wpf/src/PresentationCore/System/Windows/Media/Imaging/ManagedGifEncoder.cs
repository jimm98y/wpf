// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Managed GIF encoder for platforms without native WIC. Single frame, GIF89a, with a global colour
// table and LZW-compressed image data.
//
// GIF is a palette format, so the interesting part is choosing 256 colours. Two paths:
//
//   * If the image already has few enough distinct colours -- which is the common case for the
//     screenshots, icons and diagrams people actually save as GIF -- they are used verbatim and the
//     encode is LOSSLESS.
//   * Otherwise median cut: repeatedly split the colour box with the widest channel range at its
//     median, then average each box. It is the classic algorithm, it is deterministic, and it needs no
//     tuning parameters, which matters more here than squeezing out the last bit of quality.
//
// Alpha collapses to GIF's single transparent palette index: a pixel is either fully transparent or
// fully opaque, because that is all the format can express.
//

using System.Collections.Generic;
using System.IO;

namespace System.Windows.Media.Imaging
{
    internal static class ManagedGifEncoder
    {
        private const int MaxColors = 256;
        private const byte AlphaThreshold = 128;

        internal static void Save(BitmapSource source, Stream stream)
        {
            byte[] bgra = source.CopyPixelsForManagedComposition(out int width, out int height, out int stride);
            if (bgra == null || width <= 0 || height <= 0)
            {
                throw new InvalidOperationException("The bitmap has no pixels to encode.");
            }

            bool hasTransparency = HasTransparency(bgra, width, height, stride);
            int colorBudget = hasTransparency ? MaxColors - 1 : MaxColors;

            byte[] palette = BuildPalette(bgra, width, height, stride, colorBudget, out int paletteCount);
            int transparentIndex = hasTransparency ? paletteCount++ : -1;

            byte[] indices = MapToPalette(bgra, width, height, stride, palette, paletteCount, transparentIndex);

            // The colour table must be a power of two, at least 2 entries.
            int tableBits = 1;
            while ((1 << tableBits) < paletteCount) tableBits++;
            if (tableBits < 1) tableBits = 1;
            int tableSize = 1 << tableBits;

            WriteHeader(stream, width, height, tableBits);
            WriteColorTable(stream, palette, paletteCount, tableSize);
            if (transparentIndex >= 0) WriteGraphicControl(stream, transparentIndex);
            WriteImageDescriptor(stream, width, height);
            WriteLzw(stream, indices, tableBits);

            stream.WriteByte(0x3B);     // trailer
        }

        private static bool HasTransparency(byte[] bgra, int width, int height, int stride)
        {
            for (int y = 0; y < height; y++)
            {
                int i = y * stride + 3;
                for (int x = 0; x < width; x++, i += 4)
                {
                    if (bgra[i] < AlphaThreshold) return true;
                }
            }
            return false;
        }

        // -------------------------------------------------------------- palette ----

        /// <summary>
        /// Choose up to <paramref name="budget"/> colours, returned as RGB triples. Fully transparent
        /// pixels are excluded: they will take the transparent index and would otherwise drag palette
        /// entries towards whatever colour happens to sit under them.
        /// </summary>
        private static byte[] BuildPalette(byte[] bgra, int width, int height, int stride, int budget, out int count)
        {
            var histogram = new Dictionary<int, int>();
            for (int y = 0; y < height; y++)
            {
                int i = y * stride;
                for (int x = 0; x < width; x++, i += 4)
                {
                    if (bgra[i + 3] < AlphaThreshold) continue;
                    int key = (bgra[i + 2] << 16) | (bgra[i + 1] << 8) | bgra[i];
                    histogram.TryGetValue(key, out int n);
                    histogram[key] = n + 1;
                }
            }

            var palette = new byte[MaxColors * 3];

            if (histogram.Count == 0)
            {
                count = 1;
                return palette;     // fully transparent image: one arbitrary entry
            }

            // Few enough colours to keep exactly, so the encode is lossless.
            if (histogram.Count <= budget)
            {
                count = 0;
                foreach (int key in histogram.Keys)
                {
                    palette[count * 3] = (byte)(key >> 16);
                    palette[count * 3 + 1] = (byte)(key >> 8);
                    palette[count * 3 + 2] = (byte)key;
                    count++;
                }
                return palette;
            }

            return MedianCut(histogram, budget, palette, out count);
        }

        private static byte[] MedianCut(Dictionary<int, int> histogram, int budget, byte[] palette, out int count)
        {
            var colors = new List<int>(histogram.Keys);
            var boxes = new List<(int Start, int Length)> { (0, colors.Count) };

            while (boxes.Count < budget)
            {
                // Split the box with the widest single-channel spread; when none can be split, stop.
                int target = -1, targetChannel = 0, widest = 0;
                for (int b = 0; b < boxes.Count; b++)
                {
                    if (boxes[b].Length < 2) continue;
                    Spread(colors, boxes[b], out int channel, out int range);
                    if (range > widest)
                    {
                        widest = range;
                        target = b;
                        targetChannel = channel;
                    }
                }
                if (target < 0) break;

                (int start, int length) = boxes[target];
                colors.Sort(start, length, Comparer<int>.Create((a, b) => Channel(a, targetChannel) - Channel(b, targetChannel)));

                int half = length / 2;
                boxes[target] = (start, half);
                boxes.Add((start + half, length - half));
            }

            count = boxes.Count;
            for (int b = 0; b < boxes.Count; b++)
            {
                (int start, int length) = boxes[b];
                long r = 0, g = 0, bl = 0, weight = 0;
                for (int i = start; i < start + length; i++)
                {
                    int color = colors[i];
                    int n = histogram[color];
                    r += (long)((color >> 16) & 0xFF) * n;
                    g += (long)((color >> 8) & 0xFF) * n;
                    bl += (long)(color & 0xFF) * n;
                    weight += n;
                }
                if (weight == 0) weight = 1;
                palette[b * 3] = (byte)(r / weight);
                palette[b * 3 + 1] = (byte)(g / weight);
                palette[b * 3 + 2] = (byte)(bl / weight);
            }
            return palette;
        }

        private static void Spread(List<int> colors, (int Start, int Length) box, out int channel, out int range)
        {
            int minR = 255, maxR = 0, minG = 255, maxG = 0, minB = 255, maxB = 0;
            for (int i = box.Start; i < box.Start + box.Length; i++)
            {
                int color = colors[i];
                int r = (color >> 16) & 0xFF, g = (color >> 8) & 0xFF, b = color & 0xFF;
                if (r < minR) minR = r; if (r > maxR) maxR = r;
                if (g < minG) minG = g; if (g > maxG) maxG = g;
                if (b < minB) minB = b; if (b > maxB) maxB = b;
            }

            int rangeR = maxR - minR, rangeG = maxG - minG, rangeB = maxB - minB;
            if (rangeG >= rangeR && rangeG >= rangeB) { channel = 1; range = rangeG; }
            else if (rangeR >= rangeB) { channel = 0; range = rangeR; }
            else { channel = 2; range = rangeB; }
        }

        private static int Channel(int color, int channel) => channel switch
        {
            0 => (color >> 16) & 0xFF,
            1 => (color >> 8) & 0xFF,
            _ => color & 0xFF,
        };

        /// <summary>
        /// Map every pixel to its nearest palette entry. The cache matters: real images repeat colours
        /// heavily, and without it this is a linear scan of the palette per pixel.
        /// </summary>
        private static byte[] MapToPalette(byte[] bgra, int width, int height, int stride,
                                           byte[] palette, int paletteCount, int transparentIndex)
        {
            var indices = new byte[width * height];
            var cache = new Dictionary<int, byte>();
            int searchable = transparentIndex >= 0 ? paletteCount - 1 : paletteCount;
            if (searchable < 1) searchable = 1;

            for (int y = 0; y < height; y++)
            {
                int i = y * stride;
                int o = y * width;
                for (int x = 0; x < width; x++, i += 4, o++)
                {
                    if (transparentIndex >= 0 && bgra[i + 3] < AlphaThreshold)
                    {
                        indices[o] = (byte)transparentIndex;
                        continue;
                    }

                    int key = (bgra[i + 2] << 16) | (bgra[i + 1] << 8) | bgra[i];
                    if (cache.TryGetValue(key, out byte cached))
                    {
                        indices[o] = cached;
                        continue;
                    }

                    int r = bgra[i + 2], g = bgra[i + 1], b = bgra[i];
                    int best = 0, bestDistance = int.MaxValue;
                    for (int p = 0; p < searchable; p++)
                    {
                        int dr = r - palette[p * 3];
                        int dg = g - palette[p * 3 + 1];
                        int db = b - palette[p * 3 + 2];
                        int distance = dr * dr + dg * dg + db * db;
                        if (distance >= bestDistance) continue;
                        bestDistance = distance;
                        best = p;
                        if (distance == 0) break;
                    }

                    cache[key] = (byte)best;
                    indices[o] = (byte)best;
                }
            }
            return indices;
        }

        // --------------------------------------------------------------- blocks ----

        private static void WriteHeader(Stream stream, int width, int height, int tableBits)
        {
            stream.Write(new[] { (byte)'G', (byte)'I', (byte)'F', (byte)'8', (byte)'9', (byte)'a' }, 0, 6);
            WriteU16(stream, (ushort)width);
            WriteU16(stream, (ushort)height);
            // Global colour table present, 8-bit colour resolution, table size 2^(n+1).
            stream.WriteByte((byte)(0x80 | (7 << 4) | (tableBits - 1)));
            stream.WriteByte(0);        // background colour index
            stream.WriteByte(0);        // pixel aspect ratio: none given
        }

        private static void WriteColorTable(Stream stream, byte[] palette, int count, int tableSize)
        {
            stream.Write(palette, 0, count * 3);
            // The table has to be filled to its declared power-of-two size.
            for (int i = count; i < tableSize; i++)
            {
                stream.WriteByte(0); stream.WriteByte(0); stream.WriteByte(0);
            }
        }

        private static void WriteGraphicControl(Stream stream, int transparentIndex)
        {
            stream.WriteByte(0x21);     // extension introducer
            stream.WriteByte(0xF9);     // graphic control label
            stream.WriteByte(4);        // block size
            stream.WriteByte(0x01);     // transparent colour flag
            WriteU16(stream, 0);        // delay time
            stream.WriteByte((byte)transparentIndex);
            stream.WriteByte(0);        // block terminator
        }

        private static void WriteImageDescriptor(Stream stream, int width, int height)
        {
            stream.WriteByte(0x2C);
            WriteU16(stream, 0);        // left
            WriteU16(stream, 0);        // top
            WriteU16(stream, (ushort)width);
            WriteU16(stream, (ushort)height);
            stream.WriteByte(0);        // no local table, not interlaced
        }

        private static void WriteU16(Stream stream, ushort value)
        {
            stream.WriteByte((byte)value);
            stream.WriteByte((byte)(value >> 8));
        }

        // ------------------------------------------------------------------ LZW ----

        /// <summary>
        /// GIF's variable-width LZW. Codes are packed LSB-first and the width grows as the dictionary
        /// fills; at 4096 entries the encoder emits a clear code and starts over, which is what keeps
        /// the width bounded at 12 bits.
        /// </summary>
        private static void WriteLzw(Stream stream, byte[] indices, int tableBits)
        {
            int minCodeSize = Math.Max(2, tableBits);
            stream.WriteByte((byte)minCodeSize);

            int clearCode = 1 << minCodeSize;
            int endCode = clearCode + 1;
            int nextCode = endCode + 1;
            int codeSize = minCodeSize + 1;

            var writer = new BlockWriter(stream);
            var dictionary = new Dictionary<long, int>();

            writer.Write(clearCode, codeSize);

            if (indices.Length > 0)
            {
                int prefix = indices[0];
                for (int i = 1; i < indices.Length; i++)
                {
                    int k = indices[i];
                    long key = ((long)prefix << 8) | (uint)k;
                    if (dictionary.TryGetValue(key, out int code))
                    {
                        prefix = code;
                        continue;
                    }

                    writer.Write(prefix, codeSize);
                    dictionary[key] = nextCode++;

                    if (nextCode > (1 << codeSize) && codeSize < 12)
                    {
                        codeSize++;
                    }
                    else if (nextCode >= 4096)
                    {
                        writer.Write(clearCode, codeSize);
                        dictionary.Clear();
                        nextCode = endCode + 1;
                        codeSize = minCodeSize + 1;
                    }

                    prefix = k;
                }
                writer.Write(prefix, codeSize);
            }

            writer.Write(endCode, codeSize);
            writer.Flush();
            stream.WriteByte(0);        // block terminator
        }

        /// <summary>
        /// Packs codes LSB-first and emits them as GIF data sub-blocks, each at most 255 bytes behind a
        /// length byte.
        /// </summary>
        private sealed class BlockWriter
        {
            private readonly Stream _stream;
            private readonly byte[] _block = new byte[255];
            private int _blockLength;
            private uint _bits;
            private int _bitCount;

            internal BlockWriter(Stream stream) => _stream = stream;

            internal void Write(int code, int length)
            {
                _bits |= (uint)code << _bitCount;
                _bitCount += length;
                while (_bitCount >= 8)
                {
                    Emit((byte)_bits);
                    _bits >>= 8;
                    _bitCount -= 8;
                }
            }

            internal void Flush()
            {
                if (_bitCount > 0)
                {
                    Emit((byte)_bits);
                    _bits = 0;
                    _bitCount = 0;
                }
                FlushBlock();
            }

            private void Emit(byte value)
            {
                _block[_blockLength++] = value;
                if (_blockLength == 255) FlushBlock();
            }

            private void FlushBlock()
            {
                if (_blockLength == 0) return;
                _stream.WriteByte((byte)_blockLength);
                _stream.Write(_block, 0, _blockLength);
                _blockLength = 0;
            }
        }
    }
}
