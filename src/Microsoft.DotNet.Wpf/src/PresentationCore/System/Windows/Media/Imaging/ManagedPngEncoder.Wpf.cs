// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// WPF's half of the managed PNG encoder: a BitmapSource as the raster the shared encoder
// (Shared/MS/Internal/Imaging/ManagedPngEncoder.cs) writes.
//

using System.IO;

namespace System.Windows.Media.Imaging
{
    internal static partial class ManagedPngEncoder
    {
        internal static void Save(BitmapSource source, Stream stream)
        {
            uint ppmX = PixelsPerMetre(source.DpiX), ppmY = PixelsPerMetre(source.DpiY);

            // A source that is ALREADY one of PNG's own narrow formats is written as that format
            // rather than widened to RGBA8. Otherwise loading a 1-bit PNG and saving it produced a
            // 32bpp file thirty-two times the size, and "lossless" round trips lost the format even
            // where they kept every pixel.
            if (NarrowRaster(source) is ManagedRaster narrow)
            {
                Write(stream, narrow, ppmX, ppmY);
                return;
            }

            // Straight (non-premultiplied) BGRA32, normalized by the same helper the managed
            // composition path uses (handles Bgra32/Pbgra32 without native WIC).
            byte[] bgra = source.CopyPixelsForManagedComposition(out int width, out int height, out int stride);
            if (bgra == null || width <= 0 || height <= 0)
            {
                throw new InvalidOperationException("The bitmap has no pixels to encode.");
            }

            Write(stream, new ManagedRaster(width, height, ManagedPixelLayout.Bgra32, bgra, stride), ppmX, ppmY);
        }

        /// <summary>
        /// <paramref name="source"/> in its own format when PNG has one that matches: a palettised
        /// bitmap (written as colour type 3 with a PLTE, and a tRNS when any entry is not opaque), a
        /// greyscale one (colour type 0). Null for anything else, which takes the RGBA path.
        /// </summary>
        private static ManagedRaster NarrowRaster(BitmapSource source)
        {
            PixelFormat format = source.Format;
            int bitDepth = format.BitsPerPixel;
            bool indexed = format.Palettized && (bitDepth == 1 || bitDepth == 2 || bitDepth == 4 || bitDepth == 8);
            bool gray = format == PixelFormats.BlackWhite || format == PixelFormats.Gray2
                     || format == PixelFormats.Gray4 || format == PixelFormats.Gray8
                     || format == PixelFormats.Gray16;

            if (!indexed && !gray)
            {
                return null;
            }

            IList<Color> colors = source.Palette?.Colors;
            if (indexed && (colors == null || colors.Count == 0))
            {
                return null;   // an indexed bitmap with no palette has no colours to write
            }

            int width = source.PixelWidth, height = source.PixelHeight;
            if (width <= 0 || height <= 0)
            {
                return null;
            }

            int stride = (width * bitDepth + 7) / 8;
            var packed = new byte[checked(stride * height)];
            source.CopyPixels(packed, stride, 0);

            return new ManagedRaster(width, height, ManagedPixelConverter.Layout(format), packed, stride,
                                     indexed ? ManagedPixelConverter.Palette(source.Palette) : null);
        }
    }
}
