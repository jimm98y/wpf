// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Managed image decoding. Decodes PNG (all standard bit depths and color types, tRNS
// transparency, Adam7 interlace), JPEG (baseline and progressive, via ManagedJpegDecoder), GIF
// (every frame, composed -- ManagedGifDecoder), TIFF (baseline, LZW/PackBits/Deflate --
// ManagedTiffDecoder), ICO and BMP (uncompressed, bitfields, RLE4/RLE8) into straight BGRA32 or,
// where the file is already one, a narrow indexed or greyscale layout.
//
// This is the byte-level half, shared by WPF and by System.Drawing (the WinForms port links it);
// neither side's image types appear here. PresentationCore's ManagedImageDecoder.Wpf.cs turns the
// result into a BitmapSource; System.Drawing's codec bridge turns it into a GDI+-shaped Bitmap.
//
// Every format this repo can ENCODE it can now also decode, which had not been
// true of GIF and TIFF: the stack wrote files it could not read back.
//

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace System.Windows.Media.Imaging
{
    internal static partial class ManagedImageDecoder
    {
        // ---- sniffing ------------------------------------------------------------------

        internal static bool IsPng(byte[] data) =>
            data.Length > 8 && data[0] == 0x89 && data[1] == 'P' && data[2] == 'N' && data[3] == 'G';

        internal static bool IsBmp(byte[] data) => data.Length > 2 && data[0] == 'B' && data[1] == 'M';

        internal static bool IsJpeg(byte[] data) => data.Length > 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF;

        internal static bool IsIco(byte[] data) =>
            data.Length > 6 && data[0] == 0 && data[1] == 0 && data[2] == 1 && data[3] == 0;

        // ---- PNG -----------------------------------------------------------------------

        /// <summary>How a PNG is stored, beyond its pixels.</summary>
        internal struct PngInfo
        {
            internal int BitDepth;
            internal int ColorType;
            internal bool Interlaced;
            /// <summary>A tRNS chunk was present.</summary>
            internal bool HasTransparency;
            internal bool HasPhys;
            internal int PhysUnit;
            internal uint PpmX, PpmY;
            /// <summary>gAMA, in units of 1/100000; -1 when absent.</summary>
            internal long Gamma;
            /// <summary>sRGB rendering intent; -1 when absent.</summary>
            internal int SrgbIntent;
        }

        internal static byte[] DecodePng(byte[] data, out int width, out int height, out double dpiX, out double dpiY,
            out ManagedPixelLayout narrowFormat, out uint[] narrowPalette, out int narrowStride,
            bool allowNarrow = true)
            => DecodePng(data, out width, out height, out dpiX, out dpiY, out narrowFormat, out narrowPalette,
                         out narrowStride, out _, allowNarrow);

        internal static byte[] DecodePng(byte[] data, out int width, out int height, out double dpiX, out double dpiY,
            out ManagedPixelLayout narrowFormat, out uint[] narrowPalette, out int narrowStride, out PngInfo info,
            bool allowNarrow = true, bool roundGray16 = false)
        {
            int pos = 8;
            width = 0; height = 0; dpiX = 96; dpiY = 96;
            info = new PngInfo { Gamma = -1, SrgbIntent = -1 };
            int bitDepth = 0, colorType = 0, interlace = 0;
            byte[] palette = null;   // rgb triples
            byte[] trns = null;      // per-color-type transparency chunk
            using var idat = new MemoryStream();

            while (pos + 8 <= data.Length)
            {
                int len = ReadU32(data, pos);
                uint type = (uint)ReadU32(data, pos + 4);
                int body = pos + 8;
                if (len < 0 || body + len + 4 > data.Length)
                {
                    throw new InvalidDataException("Corrupt PNG chunk.");
                }

                switch (type)
                {
                    case 0x49484452:   // IHDR
                        width = ReadU32(data, body);
                        height = ReadU32(data, body + 4);
                        bitDepth = data[body + 8];
                        colorType = data[body + 9];
                        if (data[body + 10] != 0 || data[body + 11] != 0)
                        {
                            throw new InvalidDataException("Unsupported PNG compression/filter method.");
                        }
                        interlace = data[body + 12];
                        break;
                    case 0x504C5445:   // PLTE
                        palette = new byte[len];
                        Array.Copy(data, body, palette, 0, len);
                        break;
                    case 0x74524E53:   // tRNS
                        trns = new byte[len];
                        Array.Copy(data, body, trns, 0, len);
                        break;
                    case 0x70485973:   // pHYs
                        info.HasPhys = true;
                        info.PpmX = (uint)ReadU32(data, body);
                        info.PpmY = (uint)ReadU32(data, body + 4);
                        info.PhysUnit = data[body + 8];
                        if (data[body + 8] == 1)   // pixels per metre
                        {
                            dpiX = ReadU32(data, body) * 0.0254;
                            dpiY = ReadU32(data, body + 4) * 0.0254;
                        }
                        break;
                    case 0x67414D41:   // gAMA
                        if (len >= 4) info.Gamma = (uint)ReadU32(data, body);
                        break;
                    case 0x73524742:   // sRGB
                        if (len >= 1) info.SrgbIntent = data[body];
                        break;
                    case 0x49444154:   // IDAT
                        idat.Write(data, body, len);
                        break;
                    case 0x49454E44:   // IEND
                        pos = data.Length;
                        continue;
                }
                pos = body + len + 4;
            }

            if (width <= 0 || height <= 0 || idat.Length < 2)
            {
                throw new InvalidDataException("PNG has no image data.");
            }

            info.BitDepth = bitDepth;
            info.ColorType = colorType;
            info.Interlaced = interlace != 0;
            info.HasTransparency = trns != null;

            int channels = colorType switch
            {
                0 => 1,   // grayscale
                2 => 3,   // rgb
                3 => 1,   // palette index
                4 => 2,   // gray + alpha
                6 => 4,   // rgba
                _ => throw new InvalidDataException($"Unsupported PNG color type {colorType}."),
            };

            // Inflate the zlib stream (skip the 2-byte header; DeflateStream stops before adler).
            idat.Position = 2;
            using var raw = new MemoryStream();
            using (var inflate = new DeflateStream(idat, CompressionMode.Decompress, leaveOpen: true))
            {
                inflate.CopyTo(raw);
            }
            byte[] rawBytes = raw.GetBuffer();

            // Keep the file's own format where there is one for it, rather than expanding everything
            // to 32bpp. A palette PNG is Indexed1/2/4/8 and a greyscale one is BlackWhite/Gray2/4/8;
            // reporting Bgra32 for them told applications the wrong Format, handed back a null
            // Palette, and cost 32x the memory for a 1bpp image.
            //
            // Interlaced images are excluded: Adam7 scatters each pass's pixels across the output,
            // so their scanlines are not rows of the finished picture and there is nothing to copy
            // straight through. They keep the expanded path.
            //
            // A greyscale PNG carrying a tRNS transparent-colour key is excluded too: the
            // transparency it describes cannot be expressed in a Gray format, and dropping it would
            // silently make transparent pixels opaque.
            narrowFormat = ManagedPixelLayout.Unknown;
            narrowPalette = null;
            narrowStride = 0;
            // allowNarrow is false where the caller composites the result itself and needs BGRA --
            // an ICO entry, whose PNG is merged with the icon's own mask.
            bool narrow = allowNarrow && interlace == 0 && bitDepth <= 8 &&
                (colorType == 3 || (colorType == 0 && trns == null));

            if (narrow)
            {
                narrowFormat = colorType == 3 ? ManagedPixelLayouts.Indexed(bitDepth) : ManagedPixelLayouts.Gray(bitDepth);

                if (colorType == 3)
                {
                    if (palette == null || palette.Length < 3)
                    {
                        throw new InvalidDataException("PNG palette image has no PLTE chunk.");
                    }

                    int entries = palette.Length / 3;
                    var colors = new uint[entries];
                    for (int i = 0; i < entries; i++)
                    {
                        // tRNS on a palette image is a per-entry alpha table, shorter than the
                        // palette when the trailing entries are opaque.
                        uint a = trns != null && i < trns.Length ? trns[i] : 255u;
                        colors[i] = (a << 24) | ((uint)palette[i * 3] << 16) | ((uint)palette[i * 3 + 1] << 8) | palette[i * 3 + 2];
                    }
                    narrowPalette = colors;
                }

                narrowStride = (width * bitDepth + 7) / 8;
                var packedPixels = new byte[checked(narrowStride * height)];
                int packedPos = 0;
                DecodePass(rawBytes, ref packedPos, null, width, height, width, 0, 0, 1, 1,
                    bitDepth, colorType, channels, palette, trns, packedPixels, narrowStride);
                return packedPixels;
            }

            var bgra = new byte[width * height * 4];
            int rawPos = 0;
            if (interlace == 0)
            {
                DecodePass(rawBytes, ref rawPos, bgra, width, height, width, 0, 0, 1, 1,
                    bitDepth, colorType, channels, palette, trns, roundGray16: roundGray16);
            }
            else
            {
                // Adam7: seven sub-images, each filtered independently.
                ReadOnlySpan<int> x0 = [0, 4, 0, 2, 0, 1, 0];
                ReadOnlySpan<int> y0 = [0, 0, 4, 0, 2, 0, 1];
                ReadOnlySpan<int> dx = [8, 8, 4, 4, 2, 2, 1];
                ReadOnlySpan<int> dy = [8, 8, 8, 4, 4, 2, 2];
                for (int p = 0; p < 7; p++)
                {
                    int pw = (width - x0[p] + dx[p] - 1) / dx[p];
                    int ph = (height - y0[p] + dy[p] - 1) / dy[p];
                    if (pw <= 0 || ph <= 0)
                    {
                        continue;
                    }
                    DecodePass(rawBytes, ref rawPos, bgra, pw, ph, width, x0[p], y0[p], dx[p], dy[p],
                        bitDepth, colorType, channels, palette, trns, roundGray16: roundGray16);
                }
            }
            return bgra;
        }

        /// <summary>
        /// Unfilters one (sub-)image whose scanlines start at <paramref name="rawPos"/> and
        /// scatters its pixels into the BGRA output at (outX0 + x*outDx, outY0 + y*outDy).
        /// </summary>
        private static void DecodePass(byte[] raw, ref int rawPos, byte[] bgra,
            int passWidth, int passHeight, int outWidth, int outX0, int outY0, int outDx, int outDy,
            int bitDepth, int colorType, int channels, byte[] palette, byte[] trns,
            byte[] packed = null, int packedStride = 0, bool roundGray16 = false)
        {
            int bitsPerPixel = channels * bitDepth;
            int rowBytes = (passWidth * bitsPerPixel + 7) / 8;
            int bpp = Math.Max(1, bitsPerPixel / 8);   // filter delta distance

            var prev = new byte[rowBytes];
            var row = new byte[rowBytes];

            for (int y = 0; y < passHeight; y++)
            {
                if (rawPos + 1 + rowBytes > raw.Length)
                {
                    throw new InvalidDataException("PNG image data is truncated.");
                }
                byte filter = raw[rawPos++];
                Array.Copy(raw, rawPos, row, 0, rowBytes);
                rawPos += rowBytes;

                switch (filter)
                {
                    case 0:
                        break;
                    case 1:   // Sub
                        for (int i = bpp; i < rowBytes; i++)
                        {
                            row[i] = (byte)(row[i] + row[i - bpp]);
                        }
                        break;
                    case 2:   // Up
                        for (int i = 0; i < rowBytes; i++)
                        {
                            row[i] = (byte)(row[i] + prev[i]);
                        }
                        break;
                    case 3:   // Average
                        for (int i = 0; i < rowBytes; i++)
                        {
                            int left = i >= bpp ? row[i - bpp] : 0;
                            row[i] = (byte)(row[i] + ((left + prev[i]) >> 1));
                        }
                        break;
                    case 4:   // Paeth
                        for (int i = 0; i < rowBytes; i++)
                        {
                            int a = i >= bpp ? row[i - bpp] : 0;
                            int b = prev[i];
                            int c = i >= bpp ? prev[i - bpp] : 0;
                            int pa = Math.Abs(b - c), pb = Math.Abs(a - c), pc = Math.Abs(a + b - 2 * c);
                            row[i] = (byte)(row[i] + (pa <= pb && pa <= pc ? a : pb <= pc ? b : c));
                        }
                        break;
                    default:
                        throw new InvalidDataException($"Unknown PNG filter {filter}.");
                }

                if (packed != null)
                {
                    // For a palette or greyscale PNG the UNFILTERED SCANLINE IS ALREADY the packed
                    // pixel row -- PNG filtering is per byte, and an Indexed4 row really is two
                    // pixels to the byte in exactly the layout wanted. So the narrow formats do
                    // not need packing so much as they need not to be expanded. (Only reached for
                    // non-interlaced images, where a pass row maps 1:1 onto an output row.)
                    Array.Copy(row, 0, packed, y * packedStride, Math.Min(rowBytes, packedStride));
                }
                else
                {
                    EmitRow(row, bgra, passWidth, (outY0 + y * outDy) * outWidth + outX0, outDx,
                        bitDepth, colorType, palette, trns, roundGray16);
                }
                (prev, row) = (row, prev);
            }
        }

        /// <summary>Converts one unfiltered scanline to BGRA at the given output pixel index/step.</summary>
        private static void EmitRow(byte[] row, byte[] bgra, int passWidth, int outIndex, int outStep,
            int bitDepth, int colorType, byte[] palette, byte[] trns, bool roundGray16 = false)
        {
            // roundGray16: a 16-bit grey sample ROUNDED to eight bits (as GDI+ reads one) rather
            // than cut to its high byte.
            // Per-sample reader across 1/2/4/8/16-bit packing; 16-bit keeps the high byte.
            int bitPos = 0;
            int Sample()
            {
                switch (bitDepth)
                {
                    case 8:
                        return row[bitPos++];
                    case 16:
                        int hi = row[bitPos]; bitPos += 2;
                        return hi;
                    default:
                        int shift = 8 - bitDepth - (bitPos & 7);
                        int v = (row[bitPos >> 3] >> shift) & ((1 << bitDepth) - 1);
                        bitPos += bitDepth;
                        return v;
                }
            }
            // For sub-byte depths, bitPos counts bits; for 8/16 it counts bytes (see Sample).
            bool subByte = bitDepth < 8;
            int grayMax = (1 << bitDepth) - 1;

            for (int x = 0; x < passWidth; x++)
            {
                byte r, g, b, a = 255;
                switch (colorType)
                {
                    case 0:   // grayscale (+ optional tRNS gray key)
                    {
                        int full = bitDepth == 16 ? (row[bitPos] << 8) | row[bitPos + 1] : 0;
                        int v = Sample();
                        byte gg = (byte)(subByte ? v * 255 / grayMax : v);
                        if (bitDepth == 16 && roundGray16) gg = (byte)((full * 255 + 32767) / 65535);
                        r = g = b = gg;
                        if (trns != null && trns.Length >= 2 && v == ((trns[0] << 8) | trns[1]) % (grayMax + 1))
                        {
                            a = 0;
                        }
                        break;
                    }
                    case 2:   // rgb (+ optional tRNS rgb key)
                    {
                        r = (byte)Sample(); g = (byte)Sample(); b = (byte)Sample();
                        if (trns != null && trns.Length >= 6 &&
                            r == trns[1] && g == trns[3] && b == trns[5])
                        {
                            a = 0;
                        }
                        break;
                    }
                    case 3:   // palette (+ optional tRNS alpha table)
                    {
                        int idx = Sample();
                        int pi = idx * 3;
                        if (palette == null || pi + 2 >= palette.Length)
                        {
                            throw new InvalidDataException("PNG palette index out of range.");
                        }
                        r = palette[pi]; g = palette[pi + 1]; b = palette[pi + 2];
                        if (trns != null && idx < trns.Length)
                        {
                            a = trns[idx];
                        }
                        break;
                    }
                    case 4:   // gray + alpha
                    {
                        byte gg = (byte)Sample();
                        r = g = b = gg;
                        a = (byte)Sample();
                        break;
                    }
                    default:  // 6: rgba
                    {
                        r = (byte)Sample(); g = (byte)Sample(); b = (byte)Sample(); a = (byte)Sample();
                        break;
                    }
                }

                int o = (outIndex + x * outStep) * 4;
                bgra[o] = b; bgra[o + 1] = g; bgra[o + 2] = r; bgra[o + 3] = a;
            }
        }

        // ---- ICO -----------------------------------------------------------------------

        /// <summary>One image of an .ico directory.</summary>
        internal readonly struct IcoEntry
        {
            internal IcoEntry(int index, int width, int height, int colorCount, int planes, int bitCount, int size, int offset, bool isPng)
            {
                Index = index; Width = width; Height = height; ColorCount = colorCount; Planes = planes;
                BitCount = bitCount; Size = size; Offset = offset; IsPng = isPng;
            }

            internal int Index { get; }
            /// <summary>The directory's width and height, 256 for a stored 0.</summary>
            internal int Width { get; }
            internal int Height { get; }
            internal int ColorCount { get; }
            internal int Planes { get; }
            internal int BitCount { get; }
            internal int Size { get; }
            internal int Offset { get; }
            internal bool IsPng { get; }
        }

        /// <summary>The directory of a Windows .ico (or .cur).</summary>
        internal static IcoEntry[] ReadIcoDirectory(byte[] data)
        {
            int count = data[4] | (data[5] << 8);
            if (count <= 0 || 6 + count * 16 > data.Length)
            {
                throw new InvalidDataException("Corrupt ICO directory.");
            }

            var entries = new IcoEntry[count];
            for (int i = 0; i < count; i++)
            {
                int e = 6 + i * 16;
                int off = ReadU32LE(data, e + 12);
                int size = ReadU32LE(data, e + 8);
                bool png = off >= 0 && off + 4 <= data.Length
                        && data[off] == 0x89 && data[off + 1] == 'P' && data[off + 2] == 'N' && data[off + 3] == 'G';
                entries[i] = new IcoEntry(i,
                    data[e] == 0 ? 256 : data[e],
                    data[e + 1] == 0 ? 256 : data[e + 1],
                    data[e + 2],
                    data[e + 4] | (data[e + 5] << 8),
                    data[e + 6] | (data[e + 7] << 8),
                    size, off, png);
            }
            return entries;
        }

        /// <summary>
        /// Decodes a Windows .ico: picks the largest/deepest entry and decodes it.
        /// </summary>
        internal static byte[] DecodeIco(byte[] data, out int width, out int height)
        {
            IcoEntry[] entries = ReadIcoDirectory(data);

            // Choose the best entry: largest area, then greatest bit depth.
            int best = 0;
            long bestScore = -1;
            for (int i = 0; i < entries.Length; i++)
            {
                long score = (long)entries[i].Width * entries[i].Height * 100 + entries[i].BitCount;
                if (score > bestScore)
                {
                    bestScore = score;
                    best = i;
                }
            }

            return DecodeIcoEntry(data, entries[best], out width, out height, out _);
        }

        /// <summary>
        /// Decodes one entry of an .ico to straight BGRA. Each entry is either an embedded PNG or a
        /// BMP DIB (BITMAPINFOHEADER, no file header) whose biHeight is doubled to cover the trailing
        /// 1-bpp AND mask. Supports 1/4/8-bit indexed, 16-, 24- and 32-bit colour, applying the AND
        /// mask (and the 32-bit alpha when present). <paramref name="dibBitCount"/> is what the
        /// entry's own header says it is (32 for a PNG entry).
        /// </summary>
        internal static byte[] DecodeIcoEntry(byte[] data, IcoEntry entry, out int width, out int height, out int dibBitCount)
        {
            int imgSize = entry.Size;
            int imgOff = entry.Offset;
            if (imgOff < 0 || imgSize < 0 || imgOff + 8 > data.Length)
            {
                throw new InvalidDataException("Corrupt ICO entry.");
            }
            // Some writers overstate the last entry's size; what is there is what there is.
            imgSize = Math.Min(imgSize, data.Length - imgOff);

            // PNG-compressed entry (common for 256x256): decode directly.
            if (entry.IsPng)
            {
                dibBitCount = 32;
                byte[] png = new byte[imgSize];
                Array.Copy(data, imgOff, png, 0, imgSize);
                return DecodePng(png, out width, out height, out _, out _, out _, out _, out _, allowNarrow: false);
            }

            // BMP DIB entry.
            int p = imgOff;
            int hdrSize = ReadU32LE(data, p);
            int biWidth = ReadU32LE(data, p + 4);
            int biHeight = ReadU32LE(data, p + 8);
            int bitCount = data[p + 14] | (data[p + 15] << 8);
            int compression = ReadU32LE(data, p + 16);
            int clrUsed = ReadU32LE(data, p + 32);
            dibBitCount = bitCount;
            if (compression != BiRgb && compression != BiBitFields && compression != BiAlphaBitFields)
            {
                throw new NotSupportedException(
                    "Only uncompressed or bitfield ICO DIB entries can be decoded.");
            }

            DibChannels channels = DibChannels.Read(data, p, hdrSize, compression, bitCount);

            width = biWidth;
            height = biHeight / 2;   // biHeight covers colour rows + AND-mask rows
            if (width <= 0 || height <= 0)
            {
                throw new InvalidDataException("Invalid ICO DIB dimensions.");
            }

            // A BMP says where its pixels start; an ICO does not -- they simply follow the header and
            // the colour table. So the bitfield masks, which sit BETWEEN the two when the header is a
            // plain BITMAPINFOHEADER, have to be stepped over here. (A V4-or-later header carries them
            // inside itself, and hdrSize already covers them.) Miss this and the decoder reads the
            // masks as the first pixels: the colours still look plausible and the AND mask, one row
            // further out than it should be, turns the whole icon transparent.
            int maskBytes = hdrSize >= 108 ? 0
                          : compression == BiAlphaBitFields ? 16
                          : compression == BiBitFields ? 12
                          : 0;

            int palOff = p + hdrSize + maskBytes;
            int palCount = bitCount <= 8 ? (clrUsed != 0 ? clrUsed : 1 << bitCount) : 0;
            int xorOff = palOff + palCount * 4;

            int colorStride = ((width * bitCount + 31) / 32) * 4;
            int maskStride = ((width + 31) / 32) * 4;
            int maskOff = xorOff + colorStride * height;
            // A 32-bit image carries its transparency in alpha, and Windows reads one whose AND mask
            // was left off -- .NET's own DataGridView and PropertyGrid icons are written so.
            bool haveMask = maskOff + maskStride * height <= data.Length;

            var bgra = new byte[width * height * 4];
            bool anyAlpha = false;

            for (int y = 0; y < height; y++)
            {
                int srcY = height - 1 - y;   // DIB rows are bottom-up
                int colorRow = xorOff + srcY * colorStride;
                int maskRow = maskOff + srcY * maskStride;
                if (colorRow + colorStride > data.Length)
                {
                    throw new InvalidDataException("ICO pixel data is truncated.");
                }
                for (int x = 0; x < width; x++)
                {
                    byte r, g, b, a = 255;
                    if (bitCount == 32)
                    {
                        channels.Extract(ReadDibPixel(data, colorRow + x * 4, 32), out b, out g, out r, out a);
                        if (a != 0) { anyAlpha = true; }
                    }
                    else if (bitCount == 24)
                    {
                        int s = colorRow + x * 3;
                        b = data[s]; g = data[s + 1]; r = data[s + 2];
                    }
                    else if (bitCount == 16)
                    {
                        channels.Extract(ReadDibPixel(data, colorRow + x * 2, 16), out b, out g, out r, out _);
                    }
                    else
                    {
                        int idx = ReadIndex(data, colorRow, x, bitCount);
                        int pe = palOff + idx * 4;
                        b = data[pe]; g = data[pe + 1]; r = data[pe + 2];
                    }

                    if (bitCount != 32 && haveMask)
                    {
                        // AND mask: 1 = transparent, 0 = opaque.
                        int bit = (data[maskRow + (x >> 3)] >> (7 - (x & 7))) & 1;
                        a = bit == 1 ? (byte)0 : (byte)255;
                    }

                    int o = (y * width + x) * 4;
                    bgra[o] = b; bgra[o + 1] = g; bgra[o + 2] = r; bgra[o + 3] = a;
                }
            }

            // Some 32-bit icons ship an all-zero alpha channel; fall back to the AND mask.
            if (bitCount == 32 && !anyAlpha)
            {
                for (int y = 0; y < height; y++)
                {
                    int srcY = height - 1 - y;
                    int maskRow = maskOff + srcY * maskStride;
                    for (int x = 0; x < width; x++)
                    {
                        int bit = haveMask ? (data[maskRow + (x >> 3)] >> (7 - (x & 7))) & 1 : 0;
                        bgra[(y * width + x) * 4 + 3] = bit == 1 ? (byte)0 : (byte)255;
                    }
                }
            }

            return bgra;
        }

        /// <summary>Reads a 1/2/4/8-bit big-endian-packed palette index at pixel x in a DIB color row.</summary>
        private static int ReadIndex(byte[] data, int rowOff, int x, int bitCount)
        {
            switch (bitCount)
            {
                case 8:
                    return data[rowOff + x];
                case 4:
                    return (data[rowOff + (x >> 1)] >> ((x & 1) == 0 ? 4 : 0)) & 0x0F;
                case 2:
                    return (data[rowOff + (x >> 2)] >> (6 - (x & 3) * 2)) & 0x03;
                case 1:
                    return (data[rowOff + (x >> 3)] >> (7 - (x & 7))) & 0x01;
                default:
                    throw new NotSupportedException($"Unsupported ICO DIB bit depth {bitCount}.");
            }
        }

        private static int ReadU32LE(byte[] data, int pos) =>
            data[pos] | (data[pos + 1] << 8) | (data[pos + 2] << 16) | (data[pos + 3] << 24);

        // ---- DIB channel layout --------------------------------------------------------
        //
        // Shared by BMP and by an ICO's DIB entries, which are the same structure minus the file
        // header -- and which had drifted apart: BMP took BI_BITFIELDS and then read BGRA as though
        // the masks said BGRA, while ICO refused BI_BITFIELDS outright. One of those is a wrong
        // picture and the other is no picture, from the same bytes.

        private const int BiRgb = 0;
        private const int BiRle8 = 1;
        private const int BiRle4 = 2;
        private const int BiBitFields = 3;
        private const int BiAlphaBitFields = 6;

        /// <summary>
        /// Where each channel lives inside a 16- or 32-bit DIB pixel.
        /// </summary>
        /// <remarks>
        /// BI_BITFIELDS exists because a DIB pixel is a WORD or a DWORD whose bits mean whatever the
        /// masks say -- RGB565 and RGB555 are both 16-bit, and a 32-bit DIB may be BGRA or RGBA. The
        /// masks are not decoration: reading them as a fixed layout gives a picture with its red and
        /// blue swapped, which looks like a colour-management problem rather than a decoder that
        /// skipped four DWORDs of the header.
        /// </remarks>
        internal readonly struct DibChannels
        {
            private readonly uint _r, _g, _b, _a;
            private readonly int _rShift, _gShift, _bShift, _aShift;
            private readonly int _rMax, _gMax, _bMax, _aMax;

            private DibChannels(uint r, uint g, uint b, uint a)
            {
                _r = r; _g = g; _b = b; _a = a;
                (_rShift, _rMax) = Describe(r);
                (_gShift, _gMax) = Describe(g);
                (_bShift, _bMax) = Describe(b);
                (_aShift, _aMax) = Describe(a);
            }

            internal uint RedMask => _r;
            internal uint GreenMask => _g;
            internal uint BlueMask => _b;
            internal uint AlphaMask => _a;

            /// <summary>What BI_RGB means at each depth: 555 at 16 bits, BGRA at 32.</summary>
            internal static DibChannels Default(int bpp) => bpp == 16
                ? new DibChannels(0x7C00, 0x03E0, 0x001F, 0)
                : new DibChannels(0x00FF0000, 0x0000FF00, 0x000000FF, 0xFF000000);

            /// <summary>
            /// The masks a DIB header carries, or the defaults when it carries none.
            /// </summary>
            /// <remarks>
            /// They sit at the same three offsets either way. BI_BITFIELDS with a plain 40-byte
            /// BITMAPINFOHEADER stores them immediately AFTER the header; BITMAPV4HEADER and later
            /// store them INSIDE it -- and V4 opens with a verbatim BITMAPINFOHEADER, so "inside at
            /// offset 40" and "just after the 40 bytes" are the same address. A fourth, alpha, mask
            /// follows only for BI_ALPHABITFIELDS or a V4-or-later header.
            /// </remarks>
            internal static DibChannels Read(byte[] data, int headerStart, int headerSize, int compression, int bpp)
            {
                if (compression != BiBitFields && compression != BiAlphaBitFields)
                {
                    return Default(bpp);
                }

                int masks = headerStart + 40;
                if (masks + 12 > data.Length)
                {
                    return Default(bpp);   // truncated header; the defaults are the best guess left
                }

                uint r = (uint)ReadU32LE(data, masks);
                uint g = (uint)ReadU32LE(data, masks + 4);
                uint b = (uint)ReadU32LE(data, masks + 8);

                // All-zero masks are meaningless and appear in files that set BI_BITFIELDS without
                // filling them in; the default layout is what such a file is actually holding.
                if ((r | g | b) == 0)
                {
                    return Default(bpp);
                }

                uint a = 0;
                bool hasAlpha = compression == BiAlphaBitFields || headerSize >= 108;
                if (hasAlpha && masks + 16 <= data.Length)
                {
                    a = (uint)ReadU32LE(data, masks + 12);
                }

                return new DibChannels(r, g, b, a);
            }

            /// <summary>A mask's low bit position and the largest value its field can hold.</summary>
            private static (int Shift, int Max) Describe(uint mask)
            {
                if (mask == 0) return (0, 0);

                int shift = 0;
                while ((mask & 1) == 0) { mask >>= 1; shift++; }
                return (shift, (int)mask);
            }

            internal void Extract(uint pixel, out byte b, out byte g, out byte r, out byte a)
            {
                r = Channel(pixel, _r, _rShift, _rMax);
                g = Channel(pixel, _g, _gShift, _gMax);
                b = Channel(pixel, _b, _bShift, _bMax);
                // No alpha mask means no alpha channel, which is opaque -- not transparent.
                a = _a == 0 ? (byte)255 : Channel(pixel, _a, _aShift, _aMax);
            }

            /// <summary>
            /// One field, widened to eight bits. The rounding matters at 5 bits: a plain shift left
            /// leaves the maximum at 248 rather than 255, so a white RGB555 image comes out grey.
            /// </summary>
            private static byte Channel(uint pixel, uint mask, int shift, int max)
            {
                if (max <= 0) return 0;
                int value = (int)((pixel & mask) >> shift);
                return max == 255 ? (byte)value : (byte)((value * 255 + max / 2) / max);
            }
        }

        /// <summary>Reads one 16- or 32-bit DIB pixel.</summary>
        internal static uint ReadDibPixel(byte[] data, int offset, int bpp) =>
            bpp == 16
                ? (uint)(data[offset] | (data[offset + 1] << 8))
                : (uint)ReadU32LE(data, offset);

        // ---- BMP -----------------------------------------------------------------------

        /// <summary>How a BMP is stored, beyond its pixels.</summary>
        internal struct BmpInfo
        {
            internal int BitCount;
            internal int Compression;
            internal int HeaderSize;
            /// <summary>biXPelsPerMeter / biYPelsPerMeter (0 when the file left them unset).</summary>
            internal int PpmX, PpmY;
            internal bool TopDown;
            internal DibChannels Channels;
            /// <summary>For a 16/24/32-bit file: where its pixel rows start, and their stride.</summary>
            internal int PixelOffset, FileStride;
        }

        internal static byte[] DecodeBmp(byte[] data, out int width, out int height,
            out ManagedPixelLayout narrowFormat, out uint[] narrowPalette, out int narrowStride)
            => DecodeBmp(data, out width, out height, out narrowFormat, out narrowPalette, out narrowStride, out _);

        internal static byte[] DecodeBmp(byte[] data, out int width, out int height,
            out ManagedPixelLayout narrowFormat, out uint[] narrowPalette, out int narrowStride, out BmpInfo info)
        {
            narrowFormat = ManagedPixelLayout.Unknown;
            narrowPalette = null;
            narrowStride = 0;
            info = default;

            if (data.Length < 54)
            {
                throw new InvalidDataException("BMP header is truncated.");
            }

            int pixelOffset = BitConverter.ToInt32(data, 10);
            int headerSize = BitConverter.ToInt32(data, 14);
            width = BitConverter.ToInt32(data, 18);
            int rawHeight = BitConverter.ToInt32(data, 22);
            int bpp = BitConverter.ToUInt16(data, 28);
            int compression = BitConverter.ToInt32(data, 30);

            bool topDown = rawHeight < 0;
            height = Math.Abs(rawHeight);
            bool rle = (compression == BiRle8 && bpp == 8) || (compression == BiRle4 && bpp == 4);
            bool palettized = bpp == 1 || bpp == 4 || bpp == 8;
            if (width <= 0 || height == 0 || (bpp != 16 && bpp != 24 && bpp != 32 && !palettized) ||
                (compression != BiRgb && compression != BiBitFields && compression != BiAlphaBitFields && !rle))
            {
                throw new NotSupportedException(
                    "Only uncompressed, bitfield or RLE 1/4/8/16/24/32-bit BMP can be decoded.");
            }

            info.BitCount = bpp;
            info.Compression = compression;
            info.HeaderSize = headerSize;
            info.TopDown = topDown;
            if (headerSize >= 40)
            {
                info.PpmX = BitConverter.ToInt32(data, 38);
                info.PpmY = BitConverter.ToInt32(data, 42);
            }

            int srcStride = ((width * bpp + 7) / 8 + 3) & ~3;

            // A palettised BMP is already the packed picture: its rows are Indexed1/4/8 in the
            // codecs' own layout, just bottom-up and padded to a four-byte boundary. These used to be
            // rejected outright -- 1/4/8-bit BMPs are what icons, old assets and many screenshots are
            // -- and the only work needed is to unflip the rows and drop the padding.
            if (palettized)
            {
                int tableOffset = 14 + headerSize;
                if (headerSize == 40 && (compression == BiBitFields || compression == BiAlphaBitFields))
                {
                    tableOffset += compression == BiAlphaBitFields ? 16 : 12;
                }
                int used = data.Length > 50 ? BitConverter.ToInt32(data, 46) : 0;
                int entries = used > 0 && used <= (1 << bpp) ? used : 1 << bpp;
                if (tableOffset + entries * 4 > data.Length)
                {
                    throw new InvalidDataException("BMP colour table is truncated.");
                }

                var colors = new uint[entries];
                for (int i = 0; i < entries; i++)
                {
                    int e = tableOffset + i * 4;   // B, G, R, reserved
                    colors[i] = 0xFF000000u | ((uint)data[e + 2] << 16) | ((uint)data[e + 1] << 8) | data[e];
                }
                narrowPalette = colors;
                narrowFormat = ManagedPixelLayouts.Indexed(bpp);

                narrowStride = (width * bpp + 7) / 8;
                var packed = new byte[checked(narrowStride * height)];
                if (rle)
                {
                    DecodeRle(data, pixelOffset, compression == BiRle4, width, height, topDown, packed, narrowStride);
                    return packed;
                }
                for (int y = 0; y < height; y++)
                {
                    int srcRow = pixelOffset + (topDown ? y : height - 1 - y) * srcStride;
                    if (srcRow + narrowStride > data.Length)
                    {
                        throw new InvalidDataException("BMP pixel data is truncated.");
                    }
                    Array.Copy(data, srcRow, packed, y * narrowStride, narrowStride);
                }
                return packed;
            }

            // 24-bit is plain BGR triples and has no bitfield form to interpret; 16- and 32-bit are
            // whatever the masks say (see DibChannels).
            DibChannels channels = DibChannels.Read(data, 14, headerSize, compression, bpp);
            info.Channels = channels;
            info.PixelOffset = pixelOffset;
            info.FileStride = srcStride;

            var bgra = new byte[width * height * 4];
            for (int y = 0; y < height; y++)
            {
                int srcRow = pixelOffset + (topDown ? y : height - 1 - y) * srcStride;
                if (srcRow + (width * bpp + 7) / 8 > data.Length)
                {
                    throw new InvalidDataException("BMP pixel data is truncated.");
                }
                for (int x = 0; x < width; x++)
                {
                    int s = srcRow + x * bpp / 8;
                    int o = (y * width + x) * 4;

                    if (bpp == 24)
                    {
                        bgra[o] = data[s];
                        bgra[o + 1] = data[s + 1];
                        bgra[o + 2] = data[s + 2];
                        bgra[o + 3] = 255;
                        continue;
                    }

                    channels.Extract(ReadDibPixel(data, s, bpp), out byte b, out byte g, out byte r, out byte a);
                    bgra[o] = b; bgra[o + 1] = g; bgra[o + 2] = r; bgra[o + 3] = a;
                }
            }
            return bgra;
        }

        /// <summary>
        /// BI_RLE8 / BI_RLE4: pairs of (count, value) runs, and escapes after a zero count -- 0 end
        /// of line, 1 end of bitmap, 2 a delta (dx, dy), anything else a literal run padded to a
        /// word. Pixels the stream skips stay index 0, as GDI leaves them. Written into
        /// <paramref name="packed"/> top-down.
        /// </summary>
        private static void DecodeRle(byte[] data, int pos, bool four, int width, int height, bool topDown,
            byte[] packed, int stride)
        {
            int x = 0, row = 0;   // row counts from the first stored row (the bottom, unless top-down)
            void Put(int value)
            {
                if (x < width && row < height)
                {
                    int y = topDown ? row : height - 1 - row;
                    if (four)
                    {
                        int i = y * stride + (x >> 1);
                        packed[i] |= (byte)((value & 15) << ((x & 1) == 0 ? 4 : 0));
                    }
                    else
                    {
                        packed[y * stride + x] = (byte)value;
                    }
                }
                x++;
            }

            while (pos + 1 < data.Length && row < height)
            {
                int count = data[pos], value = data[pos + 1];
                pos += 2;
                if (count > 0)
                {
                    for (int i = 0; i < count; i++)
                    {
                        Put(four ? ((i & 1) == 0 ? value >> 4 : value & 15) : value);
                    }
                    continue;
                }
                switch (value)
                {
                    case 0:
                        x = 0; row++;
                        break;
                    case 1:
                        return;
                    case 2:
                        if (pos + 1 >= data.Length) return;
                        x += data[pos]; row += data[pos + 1];
                        pos += 2;
                        break;
                    default:
                    {
                        int n = value;
                        int bytes = four ? (n + 1) / 2 : n;
                        for (int i = 0; i < n && pos + (four ? i / 2 : i) < data.Length; i++)
                        {
                            int b = data[pos + (four ? i / 2 : i)];
                            Put(four ? ((i & 1) == 0 ? b >> 4 : b & 15) : b);
                        }
                        pos += (bytes + 1) & ~1;
                        break;
                    }
                }
            }
        }

        private static int ReadU32(byte[] data, int pos) =>
            (data[pos] << 24) | (data[pos + 1] << 16) | (data[pos + 2] << 8) | data[pos + 3];
    }
}
