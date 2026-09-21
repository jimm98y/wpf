// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Bitmaps, as PDF image XObjects.
//
// Everything arrives here already rasterized -- the alpha flattener turns effects, 3-D, video and
// brushes it cannot decompose into bitmaps -- so this is where a good deal of a printed page's bytes
// end up, and where the two decisions that matter are made.
//
// Colour and alpha are written as SEPARATE streams: PDF has no premultiplied RGBA image, so the
// colour goes in the image and the alpha goes in an /SMask that the image points at. Getting this
// wrong is the classic cause of black fringing around anti-aliased edges in a printed WPF page,
// because Pbgra32 is premultiplied and the colour channels have to be divided back out before they
// mean anything on their own.
//
// Fully opaque images skip the mask entirely, which is the common case and halves the bytes.
//

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace System.Windows.Xps.Pdf
{
    internal sealed class PdfImage
    {
        internal string ResourceName;
        internal int ObjectId;
    }

    internal sealed class PdfImageCache
    {
        private readonly PdfWriter _writer;
        private readonly Dictionary<BitmapSource, PdfImage> _images = new Dictionary<BitmapSource, PdfImage>();

        internal PdfImageCache(PdfWriter writer)
        {
            _writer = writer;
        }

        /// <summary>
        /// The image resource for a bitmap, writing it on first sight. Null when the bitmap cannot be
        /// read, which drawing treats as nothing to draw rather than as an error.
        /// </summary>
        internal PdfImage For(BitmapSource source)
        {
            if (source == null) return null;

            if (_images.TryGetValue(source, out PdfImage existing)) return existing;

            int width = source.PixelWidth, height = source.PixelHeight;
            if (width <= 0 || height <= 0) return null;

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

            var rgb = new byte[width * height * 3];
            var alpha = new byte[width * height];
            bool transparent = false;

            for (int i = 0, p = 0, a = 0; i < bgra.Length; i += 4, p += 3, a++)
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

                rgb[p] = r;
                rgb[p + 1] = g;
                rgb[p + 2] = b;
                alpha[a] = al;
                if (al != 255) transparent = true;
            }

            var image = new PdfImage
            {
                ResourceName = "Im" + _images.Count.ToString(CultureInfo.InvariantCulture),
                ObjectId = _writer.AllocateObject(),
            };

            string maskEntry = string.Empty;
            if (transparent)
            {
                int maskId = _writer.AllocateObject();
                _writer.WriteStreamObject(maskId, alpha, Dictionary(width, height, "/DeviceGray", 8, null));
                maskEntry = " /SMask " + maskId.ToString(CultureInfo.InvariantCulture) + " 0 R";
            }

            _writer.WriteStreamObject(image.ObjectId, rgb, Dictionary(width, height, "/DeviceRGB", 8, maskEntry));

            _images[source] = image;
            return image;
        }

        private static string Dictionary(int width, int height, string colorSpace, int bits, string extra)
        {
            return string.Concat(
                "/Type /XObject /Subtype /Image",
                " /Width ", width.ToString(CultureInfo.InvariantCulture),
                " /Height ", height.ToString(CultureInfo.InvariantCulture),
                " /ColorSpace ", colorSpace,
                " /BitsPerComponent ", bits.ToString(CultureInfo.InvariantCulture),
                extra ?? string.Empty);
        }
    }
}
