// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Managed TIFF encoder for platforms without native WIC.
//
// Baseline uncompressed RGBA, little-endian ("II"), one strip. Uncompressed rather than LZW or
// Deflate because TIFF's value here is being lossless and universally readable; the compression
// schemes add a decoder-compatibility question and a lot of code for a format nobody picks to save
// space.
//
// The layout is fixed and computed up front: header, then the IFD, then the few values too large to
// sit inline in their entries, then the pixels. TIFF requires IFD entries to be sorted by tag, which
// the order below preserves.
//

using System.Collections.Generic;
using System.IO;

namespace System.Windows.Media.Imaging
{
    internal static class ManagedTiffEncoder
    {
        // TIFF field types
        private const ushort TypeShort = 3;
        private const ushort TypeLong = 4;
        private const ushort TypeRational = 5;

        private const int EntryCount = 13;
        private const int HeaderSize = 8;
        private const int IfdSize = 2 + EntryCount * 12 + 4;

        internal static void Save(BitmapSource source, Stream stream) => Save(new[] { source }, stream);

        /// <summary>
        ///  Writes every page as its own IFD, chained through each directory's next-IFD pointer.
        /// </summary>
        /// <remarks>
        ///  Multi-page is the reason to choose TIFF over PNG, and TiffBitmapEncoder.Frames is a
        ///  collection, so writing only the first frame silently threw the caller's document away.
        ///  Each page's layout is computed up front (the format needs forward offsets to pixels the
        ///  writer has not reached yet), which is why the sizes are summed before anything is
        ///  emitted rather than as the stream is written.
        /// </remarks>
        internal static void Save(IReadOnlyList<BitmapSource> sources, Stream stream)
        {
            if (sources == null || sources.Count == 0)
            {
                throw new InvalidOperationException("The bitmap has no pixels to encode.");
            }

            var pages = new List<(byte[] Bgra, int Width, int Height, int Stride, double DpiX, double DpiY)>(sources.Count);
            foreach (BitmapSource source in sources)
            {
                byte[] pixels = source.CopyPixelsForManagedComposition(out int w, out int h, out int s);
                if (pixels == null || w <= 0 || h <= 0)
                {
                    throw new InvalidOperationException("The bitmap has no pixels to encode.");
                }
                pages.Add((pixels, w, h, s, source.DpiX, source.DpiY));
            }

            // Header, then one self-contained block per page: IFD, the values too large to sit
            // inline, then the pixels.
            const int InlineOverflowSize = 8 + 8 + 8;           // BitsPerSample + XResolution + YResolution
            int pageStart = HeaderSize;

            var starts = new int[pages.Count];
            for (int i = 0; i < pages.Count; i++)
            {
                starts[i] = pageStart;
                pageStart += IfdSize + InlineOverflowSize + pages[i].Width * pages[i].Height * 4;
            }

            var header = new byte[HeaderSize];
            int h0 = 0;
            header[h0++] = (byte)'I';                           // little-endian
            header[h0++] = (byte)'I';
            WriteU16(header, ref h0, 42);                       // the TIFF magic number
            WriteU32(header, ref h0, (uint)starts[0]);          // offset of the first IFD
            stream.Write(header, 0, header.Length);

            for (int i = 0; i < pages.Count; i++)
            {
                (byte[] bgra, int width, int height, int stride, double dpiX, double dpiY) = pages[i];
                int nextIfd = i + 1 < pages.Count ? starts[i + 1] : 0;
                WritePage(stream, bgra, width, height, stride, dpiX, dpiY, starts[i], nextIfd);
            }
        }

        private static void WritePage(Stream stream, byte[] bgra, int width, int height, int stride,
                                      double dpiX, double dpiY, int pageStart, int nextIfd)
        {
            // Values that do not fit in an entry's four bytes live after the IFD, in this order.
            int bitsPerSampleOffset = pageStart + IfdSize;      // 4 SHORTs = 8 bytes
            int xResolutionOffset = bitsPerSampleOffset + 8;    // RATIONAL = 8 bytes
            int yResolutionOffset = xResolutionOffset + 8;
            int pixelOffset = yResolutionOffset + 8;
            int pixelBytes = width * height * 4;

            var buffer = new byte[pixelOffset - pageStart];
            int o = 0;

            WriteU16(buffer, ref o, EntryCount);
            WriteEntry(buffer, ref o, 256, TypeLong, 1, (uint)width);           // ImageWidth
            WriteEntry(buffer, ref o, 257, TypeLong, 1, (uint)height);          // ImageLength
            WriteEntry(buffer, ref o, 258, TypeShort, 4, (uint)bitsPerSampleOffset); // BitsPerSample
            WriteEntry(buffer, ref o, 259, TypeShort, 1, 1);                    // Compression: none
            WriteEntry(buffer, ref o, 262, TypeShort, 1, 2);                    // Photometric: RGB
            WriteEntry(buffer, ref o, 273, TypeLong, 1, (uint)pixelOffset);     // StripOffsets
            WriteEntry(buffer, ref o, 277, TypeShort, 1, 4);                    // SamplesPerPixel
            WriteEntry(buffer, ref o, 278, TypeLong, 1, (uint)height);          // RowsPerStrip: all of it
            WriteEntry(buffer, ref o, 279, TypeLong, 1, (uint)pixelBytes);      // StripByteCounts
            WriteEntry(buffer, ref o, 282, TypeRational, 1, (uint)xResolutionOffset);
            WriteEntry(buffer, ref o, 283, TypeRational, 1, (uint)yResolutionOffset);
            WriteEntry(buffer, ref o, 296, TypeShort, 1, 2);                    // ResolutionUnit: inch
            // ExtraSamples = 2, unassociated alpha. Without this a reader is entitled to treat the
            // fourth sample as premultiplied and darken everything transparent.
            WriteEntry(buffer, ref o, 338, TypeShort, 1, 2);
            WriteU32(buffer, ref o, (uint)nextIfd);                             // 0 on the last page

            WriteU16(buffer, ref o, 8);                                         // BitsPerSample: 8,8,8,8
            WriteU16(buffer, ref o, 8);
            WriteU16(buffer, ref o, 8);
            WriteU16(buffer, ref o, 8);
            WriteRational(buffer, ref o, dpiX);
            WriteRational(buffer, ref o, dpiY);

            stream.Write(buffer, 0, buffer.Length);

            // Photometric RGB means samples are ordered R, G, B, A -- the source is B, G, R, A.
            var row = new byte[width * 4];
            for (int y = 0; y < height; y++)
            {
                int i = y * stride;
                for (int x = 0; x < width; x++, i += 4)
                {
                    row[x * 4] = bgra[i + 2];
                    row[x * 4 + 1] = bgra[i + 1];
                    row[x * 4 + 2] = bgra[i];
                    row[x * 4 + 3] = bgra[i + 3];
                }
                stream.Write(row, 0, row.Length);
            }
        }

        /// <summary>
        /// One IFD entry. Values of four bytes or fewer sit in the entry itself; anything larger is an
        /// offset to where it really lives, which is what the caller passes for those tags.
        /// </summary>
        private static void WriteEntry(byte[] buffer, ref int offset, ushort tag, ushort type, uint count, uint value)
        {
            WriteU16(buffer, ref offset, tag);
            WriteU16(buffer, ref offset, type);
            WriteU32(buffer, ref offset, count);

            // A SHORT that fits inline occupies the FIRST two bytes of the value field, not the last:
            // the field is not an integer, it is four bytes read according to the type.
            if (type == TypeShort && count == 1)
            {
                WriteU16(buffer, ref offset, (ushort)value);
                WriteU16(buffer, ref offset, 0);
            }
            else
            {
                WriteU32(buffer, ref offset, value);
            }
        }

        /// <summary>A RATIONAL is a numerator/denominator pair; x100 keeps two decimals of the DPI.</summary>
        private static void WriteRational(byte[] buffer, ref int offset, double value)
        {
            uint numerator = (uint)Math.Round((value <= 0 ? 96.0 : value) * 100.0);
            WriteU32(buffer, ref offset, numerator);
            WriteU32(buffer, ref offset, 100);
        }

        private static void WriteU16(byte[] buffer, ref int offset, ushort value)
        {
            buffer[offset++] = (byte)value;
            buffer[offset++] = (byte)(value >> 8);
        }

        private static void WriteU32(byte[] buffer, ref int offset, uint value)
        {
            buffer[offset++] = (byte)value;
            buffer[offset++] = (byte)(value >> 8);
            buffer[offset++] = (byte)(value >> 16);
            buffer[offset++] = (byte)(value >> 24);
        }
    }
}
