// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Managed image decoding for platforms without native WIC. Decodes PNG (all
// standard bit depths and color types, tRNS transparency, Adam7 interlace) and
// uncompressed BMP into straight BGRA32, and materializes the result as a
// managed-backed BitmapSource. JPEG and other containers are not supported on
// this path.
//

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
        internal static BitmapSource Decode(Uri uri, Stream stream)
        {
            byte[] data;
            if (stream != null)
            {
                using var ms = new MemoryStream();
                stream.CopyTo(ms);
                data = ms.ToArray();
            }
            else
            {
                ArgumentNullException.ThrowIfNull(uri);
                if (uri.IsFile)
                {
                    data = File.ReadAllBytes(uri.LocalPath);
                }
                else
                {
                    using Stream response = MS.Internal.WpfWebRequestHelper.CreateRequestAndGetResponseStream(uri);
                    using var ms = new MemoryStream();
                    response.CopyTo(ms);
                    data = ms.ToArray();
                }
            }

            byte[] bgra;
            int width, height;
            double dpiX = 96, dpiY = 96;

            if (data.Length > 8 && data[0] == 0x89 && data[1] == 'P' && data[2] == 'N' && data[3] == 'G')
            {
                bgra = DecodePng(data, out width, out height, out dpiX, out dpiY);
            }
            else if (data.Length > 2 && data[0] == 'B' && data[1] == 'M')
            {
                bgra = DecodeBmp(data, out width, out height);
            }
            else if (data.Length > 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
            {
                bgra = ManagedJpegDecoder.Decode(data, out width, out height);
            }
            else
            {
                throw new PlatformNotSupportedException(
                    "Only PNG, JPEG and uncompressed BMP can be decoded without native WIC on this platform.");
            }

            var source = BitmapSource.Create(width, height, dpiX, dpiY, PixelFormats.Bgra32, null, bgra, width * 4);
            source.Freeze();
            return source;
        }

        // ---- PNG -----------------------------------------------------------------------

        private static byte[] DecodePng(byte[] data, out int width, out int height, out double dpiX, out double dpiY)
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
            int bitDepth, int colorType, int channels, byte[] palette, byte[] trns)
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

                EmitRow(row, bgra, passWidth, (outY0 + y * outDy) * outWidth + outX0, outDx,
                    bitDepth, colorType, palette, trns);
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

        // ---- BMP -----------------------------------------------------------------------

        private static byte[] DecodeBmp(byte[] data, out int width, out int height)
        {
            int pixelOffset = BitConverter.ToInt32(data, 10);
            width = BitConverter.ToInt32(data, 18);
            int rawHeight = BitConverter.ToInt32(data, 22);
            int bpp = BitConverter.ToUInt16(data, 28);
            int compression = BitConverter.ToInt32(data, 30);

            bool topDown = rawHeight < 0;
            height = Math.Abs(rawHeight);
            if (width <= 0 || height == 0 || (bpp != 24 && bpp != 32) || (compression != 0 && compression != 3))
            {
                throw new PlatformNotSupportedException("Only uncompressed 24/32-bit BMP is supported without native WIC.");
            }

            int srcStride = ((width * bpp / 8) + 3) & ~3;
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
