// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Managed PNG encoder. Writes a ManagedRaster as 8-bit RGBA (color type 6), 8-bit RGB (type 2),
// palette (type 3, with tRNS when an entry is not opaque) or greyscale (type 0), filter 0, with a
// pHYs chunk carrying the resolution. Compression is raw deflate via System.IO.Compression wrapped
// in a zlib container (header + Adler-32), as the PNG IDAT chunk requires.
//
// The byte-level half, shared with System.Drawing; ManagedPngEncoder.Wpf.cs (PresentationCore)
// turns a BitmapSource into the raster written here.
//

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace System.Windows.Media.Imaging
{
    internal static partial class ManagedPngEncoder
    {
        /// <summary>
        /// Writes <paramref name="image"/>: Bgra32 as RGBA, Bgr24 as RGB, an indexed layout as a
        /// palette PNG and a greyscale one as greyscale, the packed rows going down verbatim.
        /// <paramref name="srgb"/> adds the sRGB and gAMA chunks GDI+ writes.
        /// </summary>
        internal static void Write(Stream stream, ManagedRaster image, uint ppmX, uint ppmY, bool srgb = false)
        {
            int width = image.Width, height = image.Height;
            if (image.Pixels == null || width <= 0 || height <= 0)
            {
                throw new InvalidOperationException("The bitmap has no pixels to encode.");
            }

            ManagedPixelLayout layout = image.Layout;
            bool indexed = ManagedPixelLayouts.IsIndexed(layout);
            bool gray = ManagedPixelLayouts.IsGray(layout);
            int bitDepth, colorType;
            if (indexed || gray)
            {
                bitDepth = ManagedPixelLayouts.BitsPerPixel(layout);
                colorType = indexed ? 3 : 0;
            }
            else if (layout == ManagedPixelLayout.Bgr24)
            {
                bitDepth = 8; colorType = 2;
            }
            else if (layout == ManagedPixelLayout.Bgra32)
            {
                bitDepth = 8; colorType = 6;
            }
            else
            {
                throw new NotSupportedException($"A {layout} picture cannot be written as PNG.");
            }

            // PNG signature (raw bytes -- a u8 string literal would UTF-8-encode 0x89 as C2 89).
            ReadOnlySpan<byte> signature = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];
            stream.Write(signature);

            Span<byte> ihdr = stackalloc byte[13];
            WriteU32(ihdr, (uint)width);
            WriteU32(ihdr.Slice(4), (uint)height);
            ihdr[8] = (byte)bitDepth;
            ihdr[9] = (byte)colorType;
            ihdr[10] = 0;   // compression
            ihdr[11] = 0;   // filter
            ihdr[12] = 0;   // interlace
            WriteChunk(stream, "IHDR"u8, ihdr);

            if (srgb)
            {
                Span<byte> intent = stackalloc byte[1];
                intent[0] = 0;   // perceptual
                WriteChunk(stream, "sRGB"u8, intent);
                Span<byte> gama = stackalloc byte[4];
                WriteU32(gama, 45455);
                WriteChunk(stream, "gAMA"u8, gama);
            }

            // pHYs: pixels per metre.
            Span<byte> phys = stackalloc byte[9];
            WriteU32(phys, ppmX);
            WriteU32(phys.Slice(4), ppmY);
            phys[8] = 1;    // unit: metre
            WriteChunk(stream, "pHYs"u8, phys);

            if (indexed)
            {
                uint[] colors = image.Palette ?? Array.Empty<uint>();
                var plte = new byte[colors.Length * 3];
                for (int i = 0; i < colors.Length; i++)
                {
                    plte[i * 3] = (byte)(colors[i] >> 16);
                    plte[i * 3 + 1] = (byte)(colors[i] >> 8);
                    plte[i * 3 + 2] = (byte)colors[i];
                }
                WriteChunk(stream, "PLTE"u8, plte);

                // tRNS is a per-entry alpha table, and may stop early: entries past its end are
                // opaque by definition, so only the run up to the LAST non-opaque one is written.
                int last = -1;
                for (int i = 0; i < colors.Length; i++)
                {
                    if ((colors[i] >> 24) != 255) last = i;
                }
                if (last >= 0)
                {
                    var trns = new byte[last + 1];
                    for (int i = 0; i <= last; i++) trns[i] = (byte)(colors[i] >> 24);
                    WriteChunk(stream, "tRNS"u8, trns);
                }
            }

            int rowBytes = (width * bitDepth * (colorType == 6 ? 4 : colorType == 2 ? 3 : 1) + 7) / 8;
            byte[] raw = new byte[height * (1 + rowBytes)];
            int o = 0;
            byte[] src = image.Pixels;
            for (int y = 0; y < height; y++)
            {
                raw[o++] = 0;   // filter: None
                int i = y * image.Stride;
                if (colorType == 6)
                {
                    for (int x = 0; x < width; x++, i += 4)
                    {
                        raw[o++] = src[i + 2];   // R
                        raw[o++] = src[i + 1];   // G
                        raw[o++] = src[i];       // B
                        raw[o++] = src[i + 3];   // A
                    }
                }
                else if (colorType == 2)
                {
                    for (int x = 0; x < width; x++, i += 3)
                    {
                        raw[o++] = src[i + 2];
                        raw[o++] = src[i + 1];
                        raw[o++] = src[i];
                    }
                }
                else
                {
                    // The packed row goes down verbatim.
                    Array.Copy(src, i, raw, o, rowBytes);
                    o += rowBytes;
                }
            }

            WriteIdatAndEnd(stream, raw);
        }

        /// <summary>Pixels per metre for a resolution in dots per inch, as WPF has always rounded it.</summary>
        internal static uint PixelsPerMetre(double dpi) => (uint)Math.Round(dpi / 0.0254);

        /// <summary>Deflates the filtered scanlines into IDAT and closes the file with IEND.</summary>
        private static void WriteIdatAndEnd(Stream stream, byte[] raw)
        {
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
