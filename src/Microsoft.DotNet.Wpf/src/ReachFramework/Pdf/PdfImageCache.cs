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
// wrong is the classic cause of black fringing around anti-aliased edges in a printed page, because
// premultiplied pixels have to have their colour divided back out before it means anything alone.
//
// Fully opaque images skip the mask entirely, which is the common case and halves the bytes.
//
// Shared, link-compiled, by WPF's printing (PdfImageCache.Wpf.cs reads a BitmapSource) and WinForms'
// (which hands over straight RGBA); this file sees neither.
//

using System;
using System.Collections.Generic;
using System.Globalization;

namespace System.Windows.Xps.Pdf
{
    internal sealed class PdfImage
    {
        internal string ResourceName;
        internal int ObjectId;
    }

    internal sealed partial class PdfImageCache
    {
        private readonly PdfWriter _writer;
        private readonly Dictionary<object, PdfImage> _images = new Dictionary<object, PdfImage>();

        internal PdfImageCache(PdfWriter writer)
        {
            _writer = writer;
        }

        /// <summary>
        /// The image resource for <paramref name="key"/>, writing it on first sight from STRAIGHT
        /// (not premultiplied) RGBA8 pixels. Null when there is nothing to draw.
        /// </summary>
        internal PdfImage ForRgba(object key, int width, int height, Func<byte[]> rgba)
        {
            if (key == null || width <= 0 || height <= 0) return null;

            if (_images.TryGetValue(key, out PdfImage existing)) return existing;

            byte[] pixels = rgba();
            if (pixels == null || pixels.Length < width * height * 4) return null;

            var rgb = new byte[width * height * 3];
            var alpha = new byte[width * height];
            bool transparent = false;

            for (int i = 0, p = 0, a = 0; a < width * height; i += 4, p += 3, a++)
            {
                rgb[p] = pixels[i];
                rgb[p + 1] = pixels[i + 1];
                rgb[p + 2] = pixels[i + 2];
                alpha[a] = pixels[i + 3];
                if (pixels[i + 3] != 255) transparent = true;
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

            _images[key] = image;
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
