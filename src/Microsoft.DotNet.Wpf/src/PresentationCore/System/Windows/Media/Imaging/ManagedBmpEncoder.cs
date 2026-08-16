// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Managed BMP encoder for platforms without native WIC.
//
// Writes a 32-bit BGRA bitmap with a BITMAPV5HEADER. The V5 header rather than the usual
// BITMAPINFOHEADER is what lets the alpha channel survive: a plain 32-bit BI_RGB bitmap has an
// undefined fourth byte, and most readers discard it. V5 states the channel masks and the colour
// space explicitly, so the alpha is not a matter of interpretation.
//
// Rows are written bottom-up (positive biHeight), which is the conventional orientation and the one
// every reader handles. At 32 bits per pixel a row is always a multiple of four bytes, so there is no
// padding to compute.
//

using System.Collections.Generic;
using System.IO;

namespace System.Windows.Media.Imaging
{
    internal static class ManagedBmpEncoder
    {
        private const int FileHeaderSize = 14;
        private const int V5HeaderSize = 124;

        internal static void Save(BitmapSource source, Stream stream)
        {
            // BMP has 1, 4 and 8-bit palettised forms of its own, so a bitmap that is already one of
            // them is written as itself rather than widened to 32bpp -- which the decoder now reads
            // back as the same format, closing the round trip.
            if (TrySavePalettized(source, stream))
            {
                return;
            }

            byte[] bgra = source.CopyPixelsForManagedComposition(out int width, out int height, out int stride);
            if (bgra == null || width <= 0 || height <= 0)
            {
                throw new InvalidOperationException("The bitmap has no pixels to encode.");
            }

            int imageSize = width * height * 4;
            int offBits = FileHeaderSize + V5HeaderSize;

            var header = new byte[offBits];
            int o = 0;

            // BITMAPFILEHEADER
            header[o++] = (byte)'B';
            header[o++] = (byte)'M';
            WriteU32(header, ref o, (uint)(offBits + imageSize));    // total file size
            WriteU32(header, ref o, 0);                              // reserved
            WriteU32(header, ref o, (uint)offBits);                  // pixel data offset

            // BITMAPV5HEADER
            WriteU32(header, ref o, V5HeaderSize);
            WriteU32(header, ref o, (uint)width);
            WriteU32(header, ref o, (uint)height);                   // positive: bottom-up
            WriteU16(header, ref o, 1);                              // planes
            WriteU16(header, ref o, 32);                             // bits per pixel
            WriteU32(header, ref o, 3);                              // BI_BITFIELDS
            WriteU32(header, ref o, (uint)imageSize);
            WriteU32(header, ref o, PixelsPerMetre(source.DpiX));
            WriteU32(header, ref o, PixelsPerMetre(source.DpiY));
            WriteU32(header, ref o, 0);                              // colours used
            WriteU32(header, ref o, 0);                              // colours important
            WriteU32(header, ref o, 0x00FF0000);                     // red mask
            WriteU32(header, ref o, 0x0000FF00);                     // green mask
            WriteU32(header, ref o, 0x000000FF);                     // blue mask
            WriteU32(header, ref o, 0xFF000000);                     // alpha mask
            WriteU32(header, ref o, 0x73524742);                     // 'sRGB' colour space
            o += 36;                                                 // CIEXYZTRIPLE endpoints: unused for sRGB
            WriteU32(header, ref o, 0);                              // gamma red
            WriteU32(header, ref o, 0);                              // gamma green
            WriteU32(header, ref o, 0);                              // gamma blue
            WriteU32(header, ref o, 4);                              // LCS_GM_IMAGES
            WriteU32(header, ref o, 0);                              // profile data
            WriteU32(header, ref o, 0);                              // profile size
            WriteU32(header, ref o, 0);                              // reserved

            stream.Write(header, 0, header.Length);

            // Bottom-up: the last source row is written first. The channel order already matches --
            // both this format and the source buffer are B, G, R, A.
            var row = new byte[width * 4];
            for (int y = height - 1; y >= 0; y--)
            {
                Buffer.BlockCopy(bgra, y * stride, row, 0, width * 4);
                stream.Write(row, 0, row.Length);
            }
        }

        /// <summary>
        /// Writes an Indexed1/4/8 bitmap as a palettised BMP: the classic 40-byte
        /// BITMAPINFOHEADER, a colour table, and the packed rows BOTTOM-UP with each padded to a
        /// four-byte boundary. Returns false for anything else.
        ///
        /// Indexed2 is not among them because BMP has no 2-bit form; it takes the 32bpp path. Palette
        /// ALPHA is also lost here -- a BMP colour table's fourth byte is reserved and readers ignore
        /// it -- which is a property of the format rather than of this encoder.
        /// </summary>
        private static bool TrySavePalettized(BitmapSource source, Stream stream)
        {
            PixelFormat format = source.Format;
            int bpp = format.BitsPerPixel;
            if (!format.Palettized || (bpp != 1 && bpp != 4 && bpp != 8))
            {
                return false;
            }

            IList<Color> colors = source.Palette?.Colors;
            if (colors == null || colors.Count == 0)
            {
                return false;
            }

            int width = source.PixelWidth, height = source.PixelHeight;
            if (width <= 0 || height <= 0)
            {
                return false;
            }

            int packedStride = (width * bpp + 7) / 8;
            var packed = new byte[checked(packedStride * height)];
            source.CopyPixels(packed, packedStride, 0);

            int fileStride = (packedStride + 3) & ~3;
            int tableEntries = Math.Min(colors.Count, 1 << bpp);
            int offBits = FileHeaderSize + 40 + tableEntries * 4;
            int imageSize = fileStride * height;

            var header = new byte[offBits];
            int o = 0;
            header[o++] = (byte)'B';
            header[o++] = (byte)'M';
            WriteU32(header, ref o, (uint)(offBits + imageSize));
            WriteU32(header, ref o, 0);
            WriteU32(header, ref o, (uint)offBits);

            WriteU32(header, ref o, 40);                             // BITMAPINFOHEADER
            WriteU32(header, ref o, (uint)width);
            WriteU32(header, ref o, (uint)height);                   // positive: bottom-up
            WriteU16(header, ref o, 1);                              // planes
            WriteU16(header, ref o, (ushort)bpp);
            WriteU32(header, ref o, 0);                              // BI_RGB
            WriteU32(header, ref o, (uint)imageSize);
            WriteU32(header, ref o, PixelsPerMetre(source.DpiX));
            WriteU32(header, ref o, PixelsPerMetre(source.DpiY));
            WriteU32(header, ref o, (uint)tableEntries);             // biClrUsed
            WriteU32(header, ref o, 0);                              // biClrImportant

            for (int i = 0; i < tableEntries; i++)
            {
                header[o++] = colors[i].B;
                header[o++] = colors[i].G;
                header[o++] = colors[i].R;
                header[o++] = 0;                                     // reserved
            }

            stream.Write(header, 0, header.Length);

            var row = new byte[fileStride];
            for (int y = height - 1; y >= 0; y--)                    // bottom-up
            {
                Array.Clear(row, 0, row.Length);
                Array.Copy(packed, y * packedStride, row, 0, packedStride);
                stream.Write(row, 0, row.Length);
            }
            return true;
        }

        private static uint PixelsPerMetre(double dpi) =>
            (uint)Math.Round((dpi <= 0 ? 96.0 : dpi) / 0.0254);

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
