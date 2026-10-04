// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// WPF's half of the image cache: a BitmapSource read as the straight RGBA PdfImageCache.cs takes.
//

using System;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace System.Windows.Xps.Pdf
{
    internal sealed partial class PdfImageCache
    {
        /// <summary>
        /// The image resource for a bitmap, writing it on first sight. Null when the bitmap cannot be
        /// read, which drawing treats as nothing to draw rather than as an error.
        /// </summary>
        internal PdfImage For(BitmapSource source)
        {
            if (source == null) return null;
            return ForRgba(source, source.PixelWidth, source.PixelHeight, () => Straight(source));
        }

        private static byte[] Straight(BitmapSource source)
        {
            int width = source.PixelWidth, height = source.PixelHeight;
            byte[] bgra;
            try
            {
                var converted = new FormatConvertedBitmap(source, PixelFormats.Pbgra32, null, 0);
                bgra = new byte[width * height * 4];
                converted.CopyPixels(bgra, width * 4, 0);
            }
            catch (NotSupportedException) { return null; }
            catch (InvalidOperationException) { return null; }
            catch (ArgumentException) { return null; }

            var rgba = new byte[bgra.Length];
            for (int i = 0; i < bgra.Length; i += 4)
            {
                byte b = bgra[i], g = bgra[i + 1], r = bgra[i + 2], al = bgra[i + 3];

                // Un-premultiply. Skipping this is what produces dark halos on anti-aliased edges:
                // a half-transparent white pixel is stored as (128,128,128,128), and writing 128 as
                // the colour makes it grey rather than white.
                if (al != 0 && al != 255)
                {
                    r = (byte)Math.Min(255, r * 255 / al);
                    g = (byte)Math.Min(255, g * 255 / al);
                    b = (byte)Math.Min(255, b * 255 / al);
                }

                rgba[i] = r; rgba[i + 1] = g; rgba[i + 2] = b; rgba[i + 3] = al;
            }
            return rgba;
        }
    }
}
