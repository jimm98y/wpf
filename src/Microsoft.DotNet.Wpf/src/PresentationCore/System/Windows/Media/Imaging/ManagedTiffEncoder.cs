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

        internal static void Save(BitmapSource source, Stream stream)
        {
            byte[] bgra = source.CopyPixelsForManagedComposition(out int width, out int height, out int stride);
            if (bgra == null || width <= 0 || height <= 0)
            {
                throw new InvalidOperationException("The bitmap has no pixels to encode.");
            }

            // Values that do not fit in an entry's four bytes live after the IFD, in this order.
            int bitsPerSampleOffset = HeaderSize + IfdSize;     // 4 SHORTs = 8 bytes
            int xResolutionOffset = bitsPerSampleOffset + 8;    // RATIONAL = 8 bytes
            int yResolutionOffset = xResolutionOffset + 8;
            int pixelOffset = yResolutionOffset + 8;
            int pixelBytes = width * height * 4;

            var buffer = new byte[pixelOffset];
            int o = 0;

            buffer[o++] = (byte)'I';                            // little-endian
            buffer[o++] = (byte)'I';
            WriteU16(buffer, ref o, 42);                        // the TIFF magic number
            WriteU32(buffer, ref o, HeaderSize);               // offset of the first IFD

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
            WriteU32(buffer, ref o, 0);                                         // no further IFD

            WriteU16(buffer, ref o, 8);                                         // BitsPerSample: 8,8,8,8
            WriteU16(buffer, ref o, 8);
            WriteU16(buffer, ref o, 8);
            WriteU16(buffer, ref o, 8);
            WriteRational(buffer, ref o, source.DpiX);
            WriteRational(buffer, ref o, source.DpiY);

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
