// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Managed PNG encoder for platforms without native WIC. Encodes a BitmapSource
// as an 8-bit RGBA PNG (color type 6, filter 0) with a pHYs chunk carrying the
// source DPI. Compression is raw deflate via System.IO.Compression wrapped in a
// zlib container (header + Adler-32), as the PNG IDAT chunk requires.
//

using System.IO;
using System.IO.Compression;

namespace System.Windows.Media.Imaging
{
    internal static class ManagedPngEncoder
    {
        internal static void Save(BitmapSource source, Stream stream)
        {
            // Straight (non-premultiplied) BGRA32, normalized by the same helper the managed
            // composition path uses (handles Bgra32/Pbgra32 without native WIC).
            byte[] bgra = source.CopyPixelsForManagedComposition(out int width, out int height, out int stride);
            if (bgra == null || width <= 0 || height <= 0)
            {
                throw new InvalidOperationException("The bitmap has no pixels to encode.");
            }

            // PNG signature (raw bytes -- a u8 string literal would UTF-8-encode 0x89 as C2 89).
            ReadOnlySpan<byte> signature = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];
            stream.Write(signature);

            // IHDR: 8-bit RGBA.
            Span<byte> ihdr = stackalloc byte[13];
            WriteU32(ihdr, (uint)width);
            WriteU32(ihdr.Slice(4), (uint)height);
            ihdr[8] = 8;    // bit depth
            ihdr[9] = 6;    // color type: truecolor + alpha
            ihdr[10] = 0;   // compression
            ihdr[11] = 0;   // filter
            ihdr[12] = 0;   // interlace
            WriteChunk(stream, "IHDR"u8, ihdr);

            // pHYs: DPI -> pixels per metre (1 inch = 0.0254 m).
            Span<byte> phys = stackalloc byte[9];
            WriteU32(phys, (uint)Math.Round(source.DpiX / 0.0254));
            WriteU32(phys.Slice(4), (uint)Math.Round(source.DpiY / 0.0254));
            phys[8] = 1;    // unit: metre
            WriteChunk(stream, "pHYs"u8, phys);

            // IDAT: zlib(header + deflate(filter byte + RGBA row, per row) + adler32).
            byte[] raw = new byte[height * (1 + width * 4)];
            int o = 0;
            for (int y = 0; y < height; y++)
            {
                raw[o++] = 0;   // filter: None
                int i = y * stride;
                for (int x = 0; x < width; x++, i += 4)
                {
                    raw[o++] = bgra[i + 2];   // R
                    raw[o++] = bgra[i + 1];   // G
                    raw[o++] = bgra[i];       // B
                    raw[o++] = bgra[i + 3];   // A
                }
            }

            using var idat = new MemoryStream();
            idat.WriteByte(0x78);   // zlib: 32K window, deflate
            idat.WriteByte(0x9C);   // default compression, header check
            using (var deflate = new DeflateStream(idat, CompressionLevel.Optimal, leaveOpen: true))
            {
                deflate.Write(raw);
            }
            Span<byte> adler = stackalloc byte[4];
            WriteU32(adler, Adler32(raw));
            idat.Write(adler);
            WriteChunk(stream, "IDAT"u8, idat.GetBuffer().AsSpan(0, (int)idat.Length));

            WriteChunk(stream, "IEND"u8, ReadOnlySpan<byte>.Empty);
        }

        private static void WriteChunk(Stream stream, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
        {
            Span<byte> len = stackalloc byte[4];
            WriteU32(len, (uint)data.Length);
            stream.Write(len);
            stream.Write(type);
            stream.Write(data);

            // The chunk CRC covers the type bytes then the data bytes.
            uint crc = 0xFFFFFFFF;
            crc = Crc32Update(crc, type);
            crc = Crc32Update(crc, data);
            Span<byte> crcBytes = stackalloc byte[4];
            WriteU32(crcBytes, crc ^ 0xFFFFFFFF);
            stream.Write(crcBytes);
        }

        private static void WriteU32(Span<byte> dst, uint v)
        {
            dst[0] = (byte)(v >> 24);
            dst[1] = (byte)(v >> 16);
            dst[2] = (byte)(v >> 8);
            dst[3] = (byte)v;
        }

        private static uint Adler32(ReadOnlySpan<byte> data)
        {
            uint a = 1, b = 0;
            foreach (byte t in data)
            {
                a = (a + t) % 65521;
                b = (b + a) % 65521;
            }
            return (b << 16) | a;
        }

        private static readonly uint[] s_crcTable = BuildCrcTable();

        private static uint[] BuildCrcTable()
        {
            var table = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++)
                {
                    c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
                }
                table[n] = c;
            }
            return table;
        }

        private static uint Crc32Update(uint crc, ReadOnlySpan<byte> data)
        {
            foreach (byte t in data)
            {
                crc = s_crcTable[(crc ^ t) & 0xFF] ^ (crc >> 8);
            }
            return crc;
        }
    }
}
