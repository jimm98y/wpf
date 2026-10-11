// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// WPF's half of the managed BMP encoder: a BitmapSource as the raster the shared encoder
// (Shared/MS/Internal/Imaging/ManagedBmpEncoder.cs) writes.
//

using System.IO;

namespace System.Windows.Media.Imaging
{
    internal static partial class ManagedBmpEncoder
    {
        internal static void Save(BitmapSource source, Stream stream)
        {
            uint ppmX = PixelsPerMetre(source.DpiX), ppmY = PixelsPerMetre(source.DpiY);

            // BMP has 1, 4 and 8-bit palettised forms of its own, so a bitmap that is already one of
            // them is written as itself rather than widened to 32bpp -- which the decoder now reads
            // back as the same format, closing the round trip.
            if (PalettizedRaster(source) is ManagedRaster indexed)
            {
                Write(stream, indexed, ppmX, ppmY);
                return;
            }

            byte[] bgra = source.CopyPixelsForManagedComposition(out int width, out int height, out int stride);
            if (bgra == null || width <= 0 || height <= 0)
            {
                throw new InvalidOperationException("The bitmap has no pixels to encode.");
            }

            Write(stream, new ManagedRaster(width, height, ManagedPixelLayout.Bgra32, bgra, stride), ppmX, ppmY);
        }

        private static ManagedRaster PalettizedRaster(BitmapSource source)
        {
            PixelFormat format = source.Format;
            int bpp = format.BitsPerPixel;
            if (!format.Palettized || (bpp != 1 && bpp != 4 && bpp != 8))
            {
                return null;
            }

            IList<Color> colors = source.Palette?.Colors;
            if (colors == null || colors.Count == 0)
            {
                return null;
            }

            int width = source.PixelWidth, height = source.PixelHeight;
            if (width <= 0 || height <= 0)
            {
                return null;
            }

            int packedStride = (width * bpp + 7) / 8;
            var packed = new byte[checked(packedStride * height)];
            source.CopyPixels(packed, packedStride, 0);
            return new ManagedRaster(width, height, ManagedPixelConverter.Layout(format), packed, packedStride,
                                     ManagedPixelConverter.Palette(source.Palette));
        }
    }
}
