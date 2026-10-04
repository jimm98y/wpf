// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Managed BMP encoder.
//
// A Bgra32 picture is written as a 32-bit bitmap with a BITMAPV5HEADER. The V5 header rather than
// the usual BITMAPINFOHEADER is what lets the alpha channel survive: a plain 32-bit BI_RGB bitmap has
// an undefined fourth byte, and most readers discard it. V5 states the channel masks and the colour
// space explicitly, so the alpha is not a matter of interpretation. (GDI+ writes the plain form, and
// System.Drawing asks for it: alphaHeader false.) Bgr24 is the classic 24-bit form, and the indexed
// layouts are written as BMP's own 1, 4 and 8-bit palettised bitmaps.
//
// Rows are written bottom-up (positive biHeight), which is the conventional orientation and the one
// every reader handles.
//
// The byte-level half, shared with System.Drawing; ManagedBmpEncoder.Wpf.cs (PresentationCore)
// turns a BitmapSource into the raster written here.
//

using System;
using System.Collections.Generic;
using System.IO;

namespace System.Windows.Media.Imaging
{
    internal static partial class ManagedBmpEncoder
    {
        private const int FileHeaderSize = 14;
        private const int V5HeaderSize = 124;

        /// <summary>Writes <paramref name="image"/> (Bgra32, Bgr24 or Indexed1/4/8).</summary>
        internal static void Write(Stream stream, ManagedRaster image, uint ppmX, uint ppmY, bool alphaHeader = true)
        {
            int width = image.Width, height = image.Height;
            if (image.Pixels == null || width <= 0 || height <= 0)
            {
                throw new InvalidOperationException("The bitmap has no pixels to encode.");
            }

            switch (image.Layout)
            {
                case ManagedPixelLayout.Indexed1:
                case ManagedPixelLayout.Indexed4:
                case ManagedPixelLayout.Indexed8:
                    WritePalettized(stream, image, ppmX, ppmY);
                    return;
                case ManagedPixelLayout.Bgr24:
                    WritePlain(stream, image, 24, ppmX, ppmY);
                    return;
                case ManagedPixelLayout.Bgra32 when !alphaHeader:
                    WritePlain(stream, image, 32, ppmX, ppmY);
                    return;
                case ManagedPixelLayout.Bgra32:
                    break;
                default:
                    throw new NotSupportedException($"A {image.Layout} picture cannot be written as BMP.");
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
            WriteU32(header, ref o, ppmX);
            WriteU32(header, ref o, ppmY);
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
                Buffer.BlockCopy(image.Pixels, y * image.Stride, row, 0, width * 4);
                stream.Write(row, 0, row.Length);
            }
        }

        /// <summary>A 24- or 32-bit BI_RGB bitmap with a BITMAPINFOHEADER, bottom-up.</summary>
        private static void WritePlain(Stream stream, ManagedRaster image, int bpp, uint ppmX, uint ppmY)
        {
            int width = image.Width, height = image.Height;
            int rowBytes = width * bpp / 8;
            int fileStride = (rowBytes + 3) & ~3;
            int imageSize = fileStride * height;
            int offBits = FileHeaderSize + 40;

            var header = new byte[offBits];
            int o = 0;
            header[o++] = (byte)'B';
            header[o++] = (byte)'M';
            WriteU32(header, ref o, (uint)(offBits + imageSize));
            WriteU32(header, ref o, 0);
            WriteU32(header, ref o, (uint)offBits);
            WriteU32(header, ref o, 40);
            WriteU32(header, ref o, (uint)width);
            WriteU32(header, ref o, (uint)height);
            WriteU16(header, ref o, 1);
            WriteU16(header, ref o, (ushort)bpp);
            WriteU32(header, ref o, 0);                              // BI_RGB
            WriteU32(header, ref o, (uint)imageSize);
            WriteU32(header, ref o, ppmX);
            WriteU32(header, ref o, ppmY);
            WriteU32(header, ref o, 0);
            WriteU32(header, ref o, 0);
            stream.Write(header, 0, header.Length);

            var row = new byte[fileStride];
            for (int y = height - 1; y >= 0; y--)
            {
                Buffer.BlockCopy(image.Pixels, y * image.Stride, row, 0, rowBytes);
                stream.Write(row, 0, row.Length);
            }
        }

        /// <summary>
        /// Writes an Indexed1/4/8 picture as a palettised BMP: the classic 40-byte
        /// BITMAPINFOHEADER, a colour table, and the packed rows BOTTOM-UP with each padded to a
        /// four-byte boundary.
        ///
        /// Indexed2 is not among them because BMP has no 2-bit form. Palette ALPHA is also lost here
        /// -- a BMP colour table's fourth byte is reserved and readers ignore it -- which is a
        /// property of the format rather than of this encoder.
        /// </summary>
        private static void WritePalettized(Stream stream, ManagedRaster image, uint ppmX, uint ppmY)
        {
            int width = image.Width, height = image.Height;
            int bpp = ManagedPixelLayouts.BitsPerPixel(image.Layout);
            uint[] colors = image.Palette ?? Array.Empty<uint>();

            int packedStride = (width * bpp + 7) / 8;
            int fileStride = (packedStride + 3) & ~3;
            int tableEntries = Math.Min(colors.Length, 1 << bpp);
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
            WriteU32(header, ref o, ppmX);
            WriteU32(header, ref o, ppmY);
            WriteU32(header, ref o, (uint)tableEntries);             // biClrUsed
            WriteU32(header, ref o, 0);                              // biClrImportant

            for (int i = 0; i < tableEntries; i++)
            {
                header[o++] = (byte)colors[i];
                header[o++] = (byte)(colors[i] >> 8);
                header[o++] = (byte)(colors[i] >> 16);
                header[o++] = 0;                                     // reserved
            }

            stream.Write(header, 0, header.Length);

            var row = new byte[fileStride];
            for (int y = height - 1; y >= 0; y--)                    // bottom-up
            {
                Array.Clear(row, 0, row.Length);
                Array.Copy(image.Pixels, y * image.Stride, row, 0, packedStride);
                stream.Write(row, 0, row.Length);
            }
        }

        /// <summary>Pixels per metre for a resolution in dots per inch, as WPF has always rounded it.</summary>
        internal static uint PixelsPerMetre(double dpi) =>
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
