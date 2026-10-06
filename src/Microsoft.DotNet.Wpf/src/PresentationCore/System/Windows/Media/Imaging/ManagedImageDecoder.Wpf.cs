// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// WPF's half of managed image decoding: the shared byte-level decoders
// (Shared/MS/Internal/Imaging/ManagedImageDecoder.cs and friends) materialised as managed-backed,
// frozen BitmapSources, and the URI/stream reading WPF needs (pack://, http via its request helper).
//

using System.IO;

namespace System.Windows.Media.Imaging
{
    internal static partial class ManagedImageDecoder
    {
        /// <summary>
        /// Decodes an image from a stream or URI into a managed-backed BitmapSource (Bgra32).
        /// The URI is used when <paramref name="stream"/> is null: file URIs open directly;
        /// anything else (pack://, http) goes through WPF's request helper.
        /// </summary>
        internal static BitmapSource Decode(Uri uri, Stream stream) => DecodeAll(uri, stream)[0];

        /// <summary>
        /// Decodes every frame an image carries: the frames of an animated GIF or the pages of a
        /// multi-page TIFF, and a single-element list for every other format. Each frame is frozen.
        /// </summary>
        /// <remarks>
        /// GIF frames arrive already composed onto the logical screen, so any one of them can be
        /// shown on its own; see ManagedGifDecoder for why that matters.
        /// </remarks>
        internal static List<BitmapSource> DecodeAll(Uri uri, Stream stream)
        {
            byte[] data = ReadAllBytes(uri, stream);

            if (ManagedGifDecoder.IsGif(data))
            {
                List<ManagedGifFrame> gifFrames = ManagedGifDecoder.Decode(data, out int gifWidth, out int gifHeight,
                    out byte[] gifIndexed, out uint[] gifPalette);
                if (gifIndexed != null && gifPalette != null)
                {
                    // A static GIF is an Indexed8 picture and keeps its own format.
                    return new List<BitmapSource>(1)
                    {
                        Materialize(gifIndexed, gifWidth, gifHeight, 96, 96,
                            PixelFormats.Indexed8, ManagedPixelConverter.Palette(gifPalette), gifWidth),
                    };
                }

                var decoded = new List<BitmapSource>(gifFrames.Count);
                foreach (ManagedGifFrame frame in gifFrames)
                {
                    decoded.Add(Materialize(frame.Bgra, gifWidth, gifHeight, 96, 96));
                }
                return decoded;
            }

            if (ManagedTiffDecoder.IsTiff(data))
            {
                List<ManagedTiffPage> pages = ManagedTiffDecoder.Decode(data);
                var decoded = new List<BitmapSource>(pages.Count);
                foreach (ManagedTiffPage page in pages)
                {
                    decoded.Add(Materialize(page.Bgra, page.Width, page.Height,
                                            page.DpiX > 0 ? page.DpiX : 96,
                                            page.DpiY > 0 ? page.DpiY : 96));
                }
                return decoded;
            }

            byte[] bgra;
            int width, height;
            double dpiX = 96, dpiY = 96;

            if (IsPng(data))
            {
                bgra = DecodePng(data, out width, out height, out dpiX, out dpiY,
                    out ManagedPixelLayout pngFormat, out uint[] pngPalette, out int pngStride);
                if (pngStride != 0)
                {
                    return new List<BitmapSource>(1)
                    {
                        Materialize(bgra, width, height, dpiX, dpiY, ManagedPixelConverter.Format(pngFormat),
                            ManagedPixelConverter.Palette(pngPalette), pngStride),
                    };
                }
            }
            else if (IsBmp(data))
            {
                bgra = DecodeBmp(data, out width, out height,
                    out ManagedPixelLayout bmpFormat, out uint[] bmpPalette, out int bmpStride);
                if (bmpStride != 0)
                {
                    return new List<BitmapSource>(1)
                    {
                        Materialize(bgra, width, height, dpiX, dpiY, ManagedPixelConverter.Format(bmpFormat),
                            ManagedPixelConverter.Palette(bmpPalette), bmpStride),
                    };
                }
            }
            else if (IsJpeg(data))
            {
                bgra = ManagedJpegDecoder.Decode(data, out width, out height);
            }
            else if (IsIco(data))
            {
                bgra = DecodeIco(data, out width, out height);
            }
            else
            {
                // NotSupportedException, not PlatformNotSupportedException: this is what WPF has always
                // thrown for image data it cannot decode, and callers (and BitmapImage's own tests)
                // match on the exact type. It is also the more accurate of the two now that these
                // codecs run everywhere -- an unrecognised format is unsupported on every platform,
                // not unsupported on this one.
                throw new NotSupportedException(
                    "Only PNG, JPEG, GIF, TIFF, ICO and uncompressed BMP can be decoded: "
                    + "the data matched none of them.");
            }

            return new List<BitmapSource>(1) { Materialize(bgra, width, height, dpiX, dpiY) };
        }

        private static BitmapSource Materialize(byte[] bgra, int width, int height, double dpiX, double dpiY)
            => Materialize(bgra, width, height, dpiX, dpiY, PixelFormats.Bgra32, null, width * 4);

        private static BitmapSource Materialize(byte[] pixels, int width, int height, double dpiX, double dpiY,
            PixelFormat format, BitmapPalette palette, int stride)
        {
            var source = BitmapSource.Create(width, height, dpiX, dpiY, format, palette, pixels, stride);
            source.Freeze();
            return source;
        }

        private static byte[] ReadAllBytes(Uri uri, Stream stream)
        {
            if (stream != null)
            {
                using var ms = new MemoryStream();
                stream.CopyTo(ms);
                return ms.ToArray();
            }

            ArgumentNullException.ThrowIfNull(uri);

            if (uri.IsFile)
            {
                return File.ReadAllBytes(uri.LocalPath);
            }

            using Stream response = MS.Internal.WpfWebRequestHelper.CreateRequestAndGetResponseStream(uri);
            using var buffer = new MemoryStream();
            response.CopyTo(buffer);
            return buffer.ToArray();
        }
    }
}
