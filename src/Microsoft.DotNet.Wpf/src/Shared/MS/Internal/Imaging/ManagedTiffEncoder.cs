// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Managed TIFF encoder.
//
// Baseline uncompressed RGBA (or RGB), little-endian ("II"), one strip per page. Uncompressed rather
// than LZW or Deflate because TIFF's value here is being lossless and universally readable; the
// compression schemes add a decoder-compatibility question and a lot of code for a format nobody
// picks to save space.
//
// The layout is fixed and computed up front: header, then the IFD, then the few values too large to
// sit inline in their entries, then the pixels. TIFF requires IFD entries to be sorted by tag, which
// the order below preserves.
//
// The byte-level half, shared with System.Drawing; ManagedTiffEncoder.Wpf.cs (PresentationCore)
// turns BitmapSources into the pages written here.
//

using System;
using System.Collections.Generic;
using System.IO;

namespace System.Windows.Media.Imaging
{
    internal static partial class ManagedTiffEncoder
    {
        // TIFF field types
        private const ushort TypeShort = 3;
        private const ushort TypeLong = 4;
        private const ushort TypeRational = 5;

        private const int HeaderSize = 8;

        // Entries per page: thirteen with an alpha sample (ExtraSamples), twelve without.
        private static int IfdSize(bool alpha) => 2 + (alpha ? 13 : 12) * 12 + 4;

        // BitsPerSample + XResolution + YResolution, the values too large to sit inline.
        private const int InlineOverflowSize = 8 + 8 + 8;

        /// <summary>
        ///  Writes every page as its own IFD, chained through each directory's next-IFD pointer.
        ///  A Bgra32 page is RGBA (unassociated alpha); a Bgr24 page is RGB.
        /// </summary>
        /// <remarks>
        ///  Multi-page is the reason to choose TIFF over PNG, and a multi-frame encoder is a
        ///  collection, so writing only the first frame silently threw the caller's document away.
        ///  Each page's layout is computed up front (the format needs forward offsets to pixels the
        ///  writer has not reached yet), which is why the sizes are summed before anything is
        ///  emitted rather than as the stream is written.
        /// </remarks>
        internal static void Write(IReadOnlyList<ManagedRaster> pages, Stream stream)
        {
            if (pages == null || pages.Count == 0)
            {
                throw new InvalidOperationException("The bitmap has no pixels to encode.");
            }

            // Header, then one self-contained block per page: IFD, the values too large to sit
            // inline, then the pixels.
            int pageStart = HeaderSize;

            var starts = new int[pages.Count];
            for (int i = 0; i < pages.Count; i++)
            {
                ManagedRaster p = pages[i];
                if (p.Pixels == null || p.Width <= 0 || p.Height <= 0
                    || (p.Layout != ManagedPixelLayout.Bgra32 && p.Layout != ManagedPixelLayout.Bgr24
                        && (p.Layout != ManagedPixelLayout.Indexed8 || p.Palette == null)))
                {
                    throw new InvalidOperationException("The bitmap has no pixels to encode.");
                }
                starts[i] = pageStart;
                if (p.Layout == ManagedPixelLayout.Indexed8)
                {
                    pageStart += IndexedIfdSize + 8 + 8 + 3 * 256 * 2 + p.Width * p.Height;
                    continue;
                }
                bool alpha = p.Layout == ManagedPixelLayout.Bgra32;
                pageStart += IfdSize(alpha) + InlineOverflowSize + p.Width * p.Height * (alpha ? 4 : 3);
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
                int nextIfd = i + 1 < pages.Count ? starts[i + 1] : 0;
                if (pages[i].Layout == ManagedPixelLayout.Indexed8) WriteIndexedPage(stream, pages[i], starts[i], nextIfd);
                else WritePage(stream, pages[i], starts[i], nextIfd);
            }
        }

        // A palette page: twelve entries, one 8-bit sample, the ColorMap (all reds, then all greens,
        // then all blues, sixteen bits each) after the resolutions.
        private const int IndexedIfdSize = 2 + 12 * 12 + 4;

        private static void WriteIndexedPage(Stream stream, ManagedRaster page, int pageStart, int nextIfd)
        {
            int width = page.Width, height = page.Height;
            int xResolutionOffset = pageStart + IndexedIfdSize, yResolutionOffset = xResolutionOffset + 8;
            int colorMapOffset = yResolutionOffset + 8, pixelOffset = colorMapOffset + 3 * 256 * 2;
            var buffer = new byte[pixelOffset - pageStart];
            int o = 0;
            WriteU16(buffer, ref o, 12);
            WriteEntry(buffer, ref o, 256, TypeLong, 1, (uint)width);
            WriteEntry(buffer, ref o, 257, TypeLong, 1, (uint)height);
            WriteEntry(buffer, ref o, 258, TypeShort, 1, 8);                    // BitsPerSample
            WriteEntry(buffer, ref o, 259, TypeShort, 1, 1);                    // Compression: none
            WriteEntry(buffer, ref o, 262, TypeShort, 1, 3);                    // Photometric: palette
            WriteEntry(buffer, ref o, 273, TypeLong, 1, (uint)pixelOffset);
            WriteEntry(buffer, ref o, 277, TypeShort, 1, 1);
            WriteEntry(buffer, ref o, 278, TypeLong, 1, (uint)height);
            WriteEntry(buffer, ref o, 279, TypeLong, 1, (uint)(width * height));
            WriteEntry(buffer, ref o, 282, TypeRational, 1, (uint)xResolutionOffset);
            WriteEntry(buffer, ref o, 283, TypeRational, 1, (uint)yResolutionOffset);
            WriteEntry(buffer, ref o, 320, TypeShort, 3 * 256, (uint)colorMapOffset);   // ColorMap
            WriteU32(buffer, ref o, (uint)nextIfd);
            WriteRational(buffer, ref o, page.DpiX);
            WriteRational(buffer, ref o, page.DpiY);
            for (int channel = 2; channel >= 0; channel--)
            {
                for (int i = 0; i < 256; i++)
                {
                    int v = i < page.Palette.Length ? (int)(page.Palette[i] >> (channel * 8)) & 0xFF : 0;
                    WriteU16(buffer, ref o, (ushort)(v * 257));
                }
            }
            stream.Write(buffer, 0, buffer.Length);
            var row = new byte[width];
            for (int y = 0; y < height; y++)
            {
                Buffer.BlockCopy(page.Pixels, y * page.Stride, row, 0, width);
                stream.Write(row, 0, width);
            }
        }

        private static void WritePage(Stream stream, ManagedRaster page, int pageStart, int nextIfd)
        {
            int width = page.Width, height = page.Height, stride = page.Stride;
            bool alpha = page.Layout == ManagedPixelLayout.Bgra32;
            int samples = alpha ? 4 : 3;
            byte[] bgra = page.Pixels;

            // Values that do not fit in an entry's four bytes live after the IFD, in this order.
            int bitsPerSampleOffset = pageStart + IfdSize(alpha);  // 3 or 4 SHORTs, in 8 bytes
            int xResolutionOffset = bitsPerSampleOffset + 8;    // RATIONAL = 8 bytes
            int yResolutionOffset = xResolutionOffset + 8;
            int pixelOffset = yResolutionOffset + 8;
            int pixelBytes = width * height * samples;

            var buffer = new byte[pixelOffset - pageStart];
            int o = 0;

            WriteU16(buffer, ref o, (ushort)(alpha ? 13 : 12));
            WriteEntry(buffer, ref o, 256, TypeLong, 1, (uint)width);           // ImageWidth
            WriteEntry(buffer, ref o, 257, TypeLong, 1, (uint)height);          // ImageLength
            WriteEntry(buffer, ref o, 258, TypeShort, (uint)samples, (uint)bitsPerSampleOffset); // BitsPerSample
            WriteEntry(buffer, ref o, 259, TypeShort, 1, 1);                    // Compression: none
            WriteEntry(buffer, ref o, 262, TypeShort, 1, 2);                    // Photometric: RGB
            WriteEntry(buffer, ref o, 273, TypeLong, 1, (uint)pixelOffset);     // StripOffsets
            WriteEntry(buffer, ref o, 277, TypeShort, 1, (uint)samples);        // SamplesPerPixel
            WriteEntry(buffer, ref o, 278, TypeLong, 1, (uint)height);          // RowsPerStrip: all of it
            WriteEntry(buffer, ref o, 279, TypeLong, 1, (uint)pixelBytes);      // StripByteCounts
            WriteEntry(buffer, ref o, 282, TypeRational, 1, (uint)xResolutionOffset);
            WriteEntry(buffer, ref o, 283, TypeRational, 1, (uint)yResolutionOffset);
            WriteEntry(buffer, ref o, 296, TypeShort, 1, 2);                    // ResolutionUnit: inch
            if (alpha)
            {
                // ExtraSamples = 2, unassociated alpha. Without this a reader is entitled to treat the
                // fourth sample as premultiplied and darken everything transparent.
                WriteEntry(buffer, ref o, 338, TypeShort, 1, 2);
            }
            WriteU32(buffer, ref o, (uint)nextIfd);                             // 0 on the last page

            for (int s = 0; s < 4; s++)                                         // BitsPerSample: 8 each
            {
                WriteU16(buffer, ref o, (ushort)(s < samples ? 8 : 0));
            }
            WriteRational(buffer, ref o, page.DpiX);
            WriteRational(buffer, ref o, page.DpiY);

            stream.Write(buffer, 0, buffer.Length);

            // Photometric RGB means samples are ordered R, G, B and then A -- the source is B, G, R.
            var row = new byte[width * samples];
            for (int y = 0; y < height; y++)
            {
                int i = y * stride;
                for (int x = 0; x < width; x++, i += samples)
                {
                    row[x * samples] = bgra[i + 2];
                    row[x * samples + 1] = bgra[i + 1];
                    row[x * samples + 2] = bgra[i];
                    if (alpha) row[x * 4 + 3] = bgra[i + 3];
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
