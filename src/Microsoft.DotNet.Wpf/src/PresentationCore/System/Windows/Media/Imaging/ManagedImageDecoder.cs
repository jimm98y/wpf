// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Managed image decoding for platforms without native WIC. Decodes PNG (all
// standard bit depths and color types, tRNS transparency, Adam7 interlace),
// JPEG (baseline and progressive, via ManagedJpegDecoder), GIF (every frame,
// composed -- ManagedGifDecoder), TIFF (baseline, LZW/PackBits/Deflate --
// ManagedTiffDecoder), ICO and uncompressed BMP into straight BGRA32, and
// materializes the result as a managed-backed BitmapSource.
//
// Every format this repo can ENCODE it can now also decode, which had not been
// true of GIF and TIFF: the stack wrote files it could not read back.
//

using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace System.Windows.Media.Imaging
{
    internal static class ManagedImageDecoder
    {
        /// <summary>
        /// Decodes an image from a stream or URI into a managed-backed BitmapSource (Bgra32).
        /// The URI is used when <paramref name="stream"/> is null: file URIs open directly;
        /// anything else (pack://, http) goes through WPF's request helper.
        /// </summary>
        internal static BitmapSource Decode(Uri uri, Stream stream) => DecodeAll(uri, stream)[0];

        /// <summary>
        /// Decodes every frame an image carries: the frames of an animated GIF or the pages of a
        /// multi-page TIFF, and a single-element list for every other format. Each frame is frozen.
        /// </summary>
        /// <remarks>
        /// GIF frames arrive already composed onto the logical screen, so any one of them can be
        /// shown on its own; see ManagedGifDecoder for why that matters.
        /// </remarks>
        internal static List<BitmapSource> DecodeAll(Uri uri, Stream stream)
        {
            byte[] data = ReadAllBytes(uri, stream);

            if (ManagedGifDecoder.IsGif(data))
            {
                List<ManagedGifFrame> gifFrames = ManagedGifDecoder.Decode(data, out int gifWidth, out int gifHeight);
                var decoded = new List<BitmapSource>(gifFrames.Count);
                foreach (ManagedGifFrame frame in gifFrames)
                {
                    decoded.Add(Materialize(frame.Bgra, gifWidth, gifHeight, 96, 96));
                }
                return decoded;
            }

            if (ManagedTiffDecoder.IsTiff(data))
            {
                List<ManagedTiffPage> pages = ManagedTiffDecoder.Decode(data);
                var decoded = new List<BitmapSource>(pages.Count);
                foreach (ManagedTiffPage page in pages)
                {
                    decoded.Add(Materialize(page.Bgra, page.Width, page.Height,
                                            page.DpiX > 0 ? page.DpiX : 96,
                                            page.DpiY > 0 ? page.DpiY : 96));
                }
                return decoded;
            }

            byte[] bgra;
            int width, height;
            double dpiX = 96, dpiY = 96;

            if (data.Length > 8 && data[0] == 0x89 && data[1] == 'P' && data[2] == 'N' && data[3] == 'G')
            {
                bgra = DecodePng(data, out width, out height, out dpiX, out dpiY,
                    out PixelFormat pngFormat, out BitmapPalette pngPalette, out int pngStride);
                if (pngStride != 0)
                {
                    return new List<BitmapSource>(1)
                    {
                        Materialize(bgra, width, height, dpiX, dpiY, pngFormat, pngPalette, pngStride),
                    };
                }
            }
            else if (data.Length > 2 && data[0] == 'B' && data[1] == 'M')
            {
                bgra = DecodeBmp(data, out width, out height,
                    out PixelFormat bmpFormat, out BitmapPalette bmpPalette, out int bmpStride);
                if (bmpStride != 0)
                {
                    return new List<BitmapSource>(1)
                    {
                        Materialize(bgra, width, height, dpiX, dpiY, bmpFormat, bmpPalette, bmpStride),
                    };
                }
            }
            else if (data.Length > 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
            {
                bgra = ManagedJpegDecoder.Decode(data, out width, out height);
            }
            else if (data.Length > 6 && data[0] == 0 && data[1] == 0 && data[2] == 1 && data[3] == 0)
            {
                bgra = DecodeIco(data, out width, out height);
            }
            else
            {
                // NotSupportedException, not PlatformNotSupportedException: this is what WPF has always
                // thrown for image data it cannot decode, and callers (and BitmapImage's own tests)
                // match on the exact type. It is also the more accurate of the two now that these
                // codecs run everywhere -- an unrecognised format is unsupported on every platform,
                // not unsupported on this one.
                throw new NotSupportedException(
                    "Only PNG, JPEG, GIF, TIFF, ICO and uncompressed BMP can be decoded: "
                    + "the data matched none of them.");
            }

            return new List<BitmapSource>(1) { Materialize(bgra, width, height, dpiX, dpiY) };
        }

        private static BitmapSource Materialize(byte[] bgra, int width, int height, double dpiX, double dpiY)
            => Materialize(bgra, width, height, dpiX, dpiY, PixelFormats.Bgra32, null, width * 4);

        private static BitmapSource Materialize(byte[] pixels, int width, int height, double dpiX, double dpiY,
            PixelFormat format, BitmapPalette palette, int stride)
        {
            var source = BitmapSource.Create(width, height, dpiX, dpiY, format, palette, pixels, stride);
            source.Freeze();
            return source;
        }

        private static byte[] ReadAllBytes(Uri uri, Stream stream)
        {
            if (stream != null)
            {
                using var ms = new MemoryStream();
                stream.CopyTo(ms);
                return ms.ToArray();
            }

            ArgumentNullException.ThrowIfNull(uri);

            if (uri.IsFile)
            {
                return File.ReadAllBytes(uri.LocalPath);
            }

            using Stream response = MS.Internal.WpfWebRequestHelper.CreateRequestAndGetResponseStream(uri);
            using var buffer = new MemoryStream();
            response.CopyTo(buffer);
            return buffer.ToArray();
        }

        // ---- PNG -----------------------------------------------------------------------

        private static byte[] DecodePng(byte[] data, out int width, out int height, out double dpiX, out double dpiY,
            out PixelFormat narrowFormat, out BitmapPalette narrowPalette, out int narrowStride,
            bool allowNarrow = true)
        {
            int pos = 8;
            width = 0; height = 0; dpiX = 96; dpiY = 96;
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
                        if (data[body + 8] == 1)   // pixels per metre
                        {
                            dpiX = ReadU32(data, body) * 0.0254;
                            dpiY = ReadU32(data, body + 4) * 0.0254;
                        }
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

            // Keep the file's own format where WPF has one for it, rather than expanding everything
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
            narrowFormat = default;
            narrowPalette = null;
            narrowStride = 0;
            // allowNarrow is false where the caller composites the result itself and needs BGRA --
            // an ICO entry, whose PNG is merged with the icon's own mask.
            bool narrow = allowNarrow && interlace == 0 && bitDepth <= 8 &&
                (colorType == 3 || (colorType == 0 && trns == null));

            if (narrow)
            {
                narrowFormat = colorType == 3
                    ? bitDepth switch
                    {
                        1 => PixelFormats.Indexed1,
                        2 => PixelFormats.Indexed2,
                        4 => PixelFormats.Indexed4,
                        _ => PixelFormats.Indexed8,
                    }
                    : bitDepth switch
                    {
                        1 => PixelFormats.BlackWhite,
                        2 => PixelFormats.Gray2,
                        4 => PixelFormats.Gray4,
                        _ => PixelFormats.Gray8,
                    };

                if (colorType == 3)
                {
                    if (palette == null || palette.Length < 3)
                    {
                        throw new InvalidDataException("PNG palette image has no PLTE chunk.");
                    }

                    int entries = palette.Length / 3;
                    var colors = new List<Color>(entries);
                    for (int i = 0; i < entries; i++)
                    {
                        // tRNS on a palette image is a per-entry alpha table, shorter than the
                        // palette when the trailing entries are opaque.
                        byte a = trns != null && i < trns.Length ? trns[i] : (byte)255;
                        colors.Add(Color.FromArgb(a, palette[i * 3], palette[i * 3 + 1], palette[i * 3 + 2]));
                    }
                    narrowPalette = new BitmapPalette(colors);
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
                    bitDepth, colorType, channels, palette, trns);
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
                        bitDepth, colorType, channels, palette, trns);
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
            byte[] packed = null, int packedStride = 0)
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
                    // pixels to the byte in exactly the layout WPF wants. So the narrow formats do
                    // not need packing so much as they need not to be expanded. (Only reached for
                    // non-interlaced images, where a pass row maps 1:1 onto an output row.)
                    Array.Copy(row, 0, packed, y * packedStride, Math.Min(rowBytes, packedStride));
                }
                else
                {
                    EmitRow(row, bgra, passWidth, (outY0 + y * outDy) * outWidth + outX0, outDx,
                        bitDepth, colorType, palette, trns);
                }
                (prev, row) = (row, prev);
            }
        }

        /// <summary>Converts one unfiltered scanline to BGRA at the given output pixel index/step.</summary>
        private static void EmitRow(byte[] row, byte[] bgra, int passWidth, int outIndex, int outStep,
            int bitDepth, int colorType, byte[] palette, byte[] trns)
        {
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
                        int v = Sample();
                        byte gg = (byte)(subByte ? v * 255 / grayMax : v);
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

        /// <summary>
        /// Decodes a Windows .ico: picks the largest/deepest entry and decodes it. Each entry is
        /// either an embedded PNG or a BMP DIB (BITMAPINFOHEADER, no file header) whose biHeight is
        /// doubled to cover the trailing 1-bpp AND mask. Supports 1/4/8-bit indexed, 24-bit and
        /// 32-bit color, applying the AND mask (and the 32-bit alpha when present).
        /// </summary>
        private static byte[] DecodeIco(byte[] data, out int width, out int height)
        {
            int count = data[4] | (data[5] << 8);
            if (count <= 0 || 6 + count * 16 > data.Length)
            {
                throw new InvalidDataException("Corrupt ICO directory.");
            }

            // Choose the best entry: largest area, then greatest bit depth.
            int best = -1;
            long bestScore = -1;
            for (int i = 0; i < count; i++)
            {
                int e = 6 + i * 16;
                int w = data[e] == 0 ? 256 : data[e];
                int h = data[e + 1] == 0 ? 256 : data[e + 1];
                int bits = data[e + 6] | (data[e + 7] << 8);
                long score = (long)w * h * 100 + bits;
                if (score > bestScore)
                {
                    bestScore = score;
                    best = e;
                }
            }

            int imgSize = ReadU32LE(data, best + 8);
            int imgOff = ReadU32LE(data, best + 12);
            if (imgOff < 0 || imgSize < 0 || imgOff + imgSize > data.Length || imgOff + 8 > data.Length)
            {
                throw new InvalidDataException("Corrupt ICO entry.");
            }

            // PNG-compressed entry (common for 256x256): decode directly.
            if (data[imgOff] == 0x89 && data[imgOff + 1] == 'P' && data[imgOff + 2] == 'N' && data[imgOff + 3] == 'G')
            {
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
            if (compression != 0)
            {
                throw new PlatformNotSupportedException("Only uncompressed ICO DIB entries are supported without native WIC.");
            }

            width = biWidth;
            height = biHeight / 2;   // biHeight covers colour rows + AND-mask rows
            if (width <= 0 || height <= 0)
            {
                throw new InvalidDataException("Invalid ICO DIB dimensions.");
            }

            int palOff = p + hdrSize;
            int palCount = bitCount <= 8 ? (clrUsed != 0 ? clrUsed : 1 << bitCount) : 0;
            int xorOff = palOff + palCount * 4;

            int colorStride = ((width * bitCount + 31) / 32) * 4;
            int maskStride = ((width + 31) / 32) * 4;
            int maskOff = xorOff + colorStride * height;

            var bgra = new byte[width * height * 4];
            bool anyAlpha = false;

            for (int y = 0; y < height; y++)
            {
                int srcY = height - 1 - y;   // DIB rows are bottom-up
                int colorRow = xorOff + srcY * colorStride;
                int maskRow = maskOff + srcY * maskStride;
                for (int x = 0; x < width; x++)
                {
                    byte r, g, b, a = 255;
                    if (bitCount == 32)
                    {
                        int s = colorRow + x * 4;
                        b = data[s]; g = data[s + 1]; r = data[s + 2]; a = data[s + 3];
                        if (a != 0) { anyAlpha = true; }
                    }
                    else if (bitCount == 24)
                    {
                        int s = colorRow + x * 3;
                        b = data[s]; g = data[s + 1]; r = data[s + 2];
                    }
                    else
                    {
                        int idx = ReadIndex(data, colorRow, x, bitCount);
                        int pe = palOff + idx * 4;
                        b = data[pe]; g = data[pe + 1]; r = data[pe + 2];
                    }

                    if (bitCount != 32)
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
                        int bit = (data[maskRow + (x >> 3)] >> (7 - (x & 7))) & 1;
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
                    throw new PlatformNotSupportedException($"Unsupported ICO DIB bit depth {bitCount}.");
            }
        }

        private static int ReadU32LE(byte[] data, int pos) =>
            data[pos] | (data[pos + 1] << 8) | (data[pos + 2] << 16) | (data[pos + 3] << 24);

        // ---- BMP -----------------------------------------------------------------------

        private static byte[] DecodeBmp(byte[] data, out int width, out int height,
            out PixelFormat narrowFormat, out BitmapPalette narrowPalette, out int narrowStride)
        {
            narrowFormat = default;
            narrowPalette = null;
            narrowStride = 0;

            int pixelOffset = BitConverter.ToInt32(data, 10);
            width = BitConverter.ToInt32(data, 18);
            int rawHeight = BitConverter.ToInt32(data, 22);
            int bpp = BitConverter.ToUInt16(data, 28);
            int compression = BitConverter.ToInt32(data, 30);

            bool topDown = rawHeight < 0;
            height = Math.Abs(rawHeight);
            bool palettized = bpp == 1 || bpp == 4 || bpp == 8;
            if (width <= 0 || height == 0 || (bpp != 24 && bpp != 32 && !palettized) ||
                (compression != 0 && compression != 3))
            {
                throw new PlatformNotSupportedException(
                    "Only uncompressed 1/4/8/24/32-bit BMP is supported without native WIC.");
            }

            int srcStride = ((width * bpp + 7) / 8 + 3) & ~3;

            // A palettised BMP is already the packed picture: its rows are Indexed1/4/8 in WPF's own
            // layout, just bottom-up and padded to a four-byte boundary. These used to be rejected
            // outright -- 1/4/8-bit BMPs are what icons, old assets and many screenshots are -- and
            // the only work needed is to unflip the rows and drop the padding.
            if (palettized)
            {
                int headerSize = BitConverter.ToInt32(data, 14);
                int tableOffset = 14 + headerSize;
                int used = data.Length > 50 ? BitConverter.ToInt32(data, 46) : 0;
                int entries = used > 0 ? used : 1 << bpp;
                if (tableOffset + entries * 4 > data.Length)
                {
                    throw new InvalidDataException("BMP colour table is truncated.");
                }

                var colors = new List<Color>(entries);
                for (int i = 0; i < entries; i++)
                {
                    int e = tableOffset + i * 4;   // B, G, R, reserved
                    colors.Add(Color.FromRgb(data[e + 2], data[e + 1], data[e]));
                }
                narrowPalette = new BitmapPalette(colors);
                narrowFormat = bpp switch
                {
                    1 => PixelFormats.Indexed1,
                    4 => PixelFormats.Indexed4,
                    _ => PixelFormats.Indexed8,
                };

                narrowStride = (width * bpp + 7) / 8;
                var packed = new byte[checked(narrowStride * height)];
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

            var bgra = new byte[width * height * 4];
            for (int y = 0; y < height; y++)
            {
                int srcRow = pixelOffset + (topDown ? y : height - 1 - y) * srcStride;
                for (int x = 0; x < width; x++)
                {
                    int s = srcRow + x * bpp / 8;
                    int o = (y * width + x) * 4;
                    bgra[o] = data[s];
                    bgra[o + 1] = data[s + 1];
                    bgra[o + 2] = data[s + 2];
                    bgra[o + 3] = bpp == 32 ? data[s + 3] : (byte)255;
                }
            }
            return bgra;
        }

        private static int ReadU32(byte[] data, int pos) =>
            (data[pos] << 24) | (data[pos + 1] << 16) | (data[pos + 2] << 8) | data[pos + 3];
    }
}
