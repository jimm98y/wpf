// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Expanding any bitmap format into straight BGRA32, in managed code.
//
// On Windows this is WIC's format converter. Off it there is none, and everything that wants pixels
// -- FormatConvertedBitmap, and CopyPixelsForManagedComposition on the way to the screen -- wants
// them as straight BGRA32. What stood in for this handled exactly one case, a source that was
// already 32bpp, stepping the source four bytes per pixel unconditionally. Anything narrower read
// the wrong bytes, and the SUB-BYTE formats read past the end of the buffer entirely.
//
// The sub-byte formats are the reason this exists as its own file rather than a loop somewhere. A
// byte holds eight BlackWhite or Indexed1 pixels, four Gray2 or Indexed2, two Gray4 or Indexed4,
// and in each case the FIRST pixel is in the HIGH bits -- so unpacking is a shift whose amount
// depends on the pixel's position within the byte, the row's last byte is usually only partly used,
// and the stride says nothing about where the pixels stop. Every one of those is somewhere a
// plausible-looking but wrong picture comes from.
//
// Sample scaling is the other place to be careful. An n-bit channel spans 0..(2^n - 1) and has to
// land on 0..255 with both ends exact: white must reach 255, not 240. That is v * 255 / max, not
// v << (8 - n).
//

using System;
using System.Windows.Media;
using MS.Internal;

namespace System.Windows.Media.Imaging
{
    internal static class ManagedPixelConverter
    {
        /// <summary>
        /// True if <see cref="ToBgra32"/> understands this format. Callers fall back to their own
        /// best effort for anything else rather than producing confidently wrong pixels.
        /// </summary>
        internal static bool CanConvert(PixelFormat format) => Bpp(format) > 0;

        private static int Bpp(PixelFormat format)
        {
            PixelFormatEnum f = format.Format;
            return f switch
            {
                PixelFormatEnum.BlackWhite or PixelFormatEnum.Indexed1 => 1,
                PixelFormatEnum.Gray2 or PixelFormatEnum.Indexed2 => 2,
                PixelFormatEnum.Gray4 or PixelFormatEnum.Indexed4 => 4,
                PixelFormatEnum.Gray8 or PixelFormatEnum.Indexed8 => 8,
                PixelFormatEnum.Bgr555 or PixelFormatEnum.Bgr565 or PixelFormatEnum.Gray16 => 16,
                PixelFormatEnum.Bgr24 or PixelFormatEnum.Rgb24 => 24,
                PixelFormatEnum.Bgr32 or PixelFormatEnum.Bgra32 or PixelFormatEnum.Pbgra32 => 32,
                PixelFormatEnum.Rgb48 => 48,
                PixelFormatEnum.Rgba64 or PixelFormatEnum.Prgba64 => 64,
                _ => 0,
            };
        }

        /// <summary>
        /// Expands <paramref name="src"/> into a straight (non-premultiplied) BGRA32 buffer of
        /// <paramref name="width"/> * 4 bytes per row. Returns null for a format this does not
        /// understand, or when an indexed format arrives without the palette it needs.
        /// </summary>
        internal static byte[] ToBgra32(byte[] src, int srcStride, int width, int height,
            PixelFormat format, BitmapPalette palette)
        {
            int bpp = Bpp(format);
            if (bpp == 0 || src == null || width <= 0 || height <= 0) return null;

            // An indexed format is nothing but offsets into its palette; without one there is no
            // picture to produce, and inventing a greyscale ramp would be worse than declining.
            bool indexed = format.Palettized;
            System.Collections.Generic.IList<Color> colors = palette?.Colors;
            if (indexed && (colors == null || colors.Count == 0)) return null;

            var dst = new byte[checked(width * 4 * height)];

            for (int y = 0; y < height; y++)
            {
                int rowStart = y * srcStride;
                int di = y * width * 4;

                for (int x = 0; x < width; x++, di += 4)
                {
                    byte b, g, r, a = 255;

                    if (bpp < 8)
                    {
                        // Sub-byte: pixels per byte, first pixel in the HIGH bits.
                        int perByte = 8 / bpp;
                        int si = rowStart + x / perByte;
                        if (si >= src.Length) return null;
                        int shift = 8 - bpp - (x % perByte) * bpp;
                        int max = (1 << bpp) - 1;
                        int v = (src[si] >> shift) & max;

                        if (indexed)
                        {
                            Color c = colors[v < colors.Count ? v : colors.Count - 1];
                            b = c.B; g = c.G; r = c.R; a = c.A;
                        }
                        else
                        {
                            // BlackWhite / Gray2 / Gray4: spread the level over the full range so
                            // the top level is exactly white.
                            b = g = r = (byte)(v * 255 / max);
                        }
                    }
                    else
                    {
                        int si = rowStart + x * (bpp / 8);
                        if (si + bpp / 8 > src.Length) return null;

                        switch (format.Format)
                        {
                            case PixelFormatEnum.Indexed8:
                            {
                                int v = src[si];
                                Color c = colors[v < colors.Count ? v : colors.Count - 1];
                                b = c.B; g = c.G; r = c.R; a = c.A;
                                break;
                            }
                            case PixelFormatEnum.Gray8:
                                b = g = r = src[si];
                                break;
                            case PixelFormatEnum.Gray16:
                                // Little-endian 16-bit sample; the high byte IS the 8-bit value.
                                b = g = r = src[si + 1];
                                break;
                            case PixelFormatEnum.Bgr555:
                            {
                                int v = src[si] | (src[si + 1] << 8);
                                r = Scale5((v >> 10) & 0x1F);
                                g = Scale5((v >> 5) & 0x1F);
                                b = Scale5(v & 0x1F);
                                break;
                            }
                            case PixelFormatEnum.Bgr565:
                            {
                                int v = src[si] | (src[si + 1] << 8);
                                r = Scale5((v >> 11) & 0x1F);
                                g = (byte)(((v >> 5) & 0x3F) * 255 / 63);
                                b = Scale5(v & 0x1F);
                                break;
                            }
                            case PixelFormatEnum.Bgr24:
                                b = src[si]; g = src[si + 1]; r = src[si + 2];
                                break;
                            case PixelFormatEnum.Rgb24:
                                r = src[si]; g = src[si + 1]; b = src[si + 2];
                                break;
                            case PixelFormatEnum.Bgr32:
                                // The fourth byte is undefined padding, NOT alpha.
                                b = src[si]; g = src[si + 1]; r = src[si + 2];
                                break;
                            case PixelFormatEnum.Bgra32:
                                b = src[si]; g = src[si + 1]; r = src[si + 2]; a = src[si + 3];
                                break;
                            case PixelFormatEnum.Pbgra32:
                                a = src[si + 3];
                                b = Unpremultiply(src[si], a);
                                g = Unpremultiply(src[si + 1], a);
                                r = Unpremultiply(src[si + 2], a);
                                break;
                            case PixelFormatEnum.Rgb48:
                                r = src[si + 1]; g = src[si + 3]; b = src[si + 5];
                                break;
                            case PixelFormatEnum.Rgba64:
                                r = src[si + 1]; g = src[si + 3]; b = src[si + 5]; a = src[si + 7];
                                break;
                            case PixelFormatEnum.Prgba64:
                                a = src[si + 7];
                                r = Unpremultiply(src[si + 1], a);
                                g = Unpremultiply(src[si + 3], a);
                                b = Unpremultiply(src[si + 5], a);
                                break;
                            default:
                                return null;
                        }
                    }

                    dst[di] = b; dst[di + 1] = g; dst[di + 2] = r; dst[di + 3] = a;
                }
            }

            return dst;
        }

        /// <summary>
        /// The other direction: pack a straight BGRA32 buffer INTO <paramref name="dest"/>, which may
        /// be narrower than a byte per pixel. <paramref name="stride"/> receives the packed row size.
        /// Returns null for a destination this does not know, or for an indexed destination with no
        /// palette to match against.
        ///
        /// This is what makes a request like FormatConvertedBitmap(src, Indexed4, palette) mean
        /// something: without it the result was 32bpp wearing another format's name, so an
        /// application that asked to quantise an image and then looked at Format -- or saved it --
        /// was told something untrue.
        /// </summary>
        internal static byte[] FromBgra32(byte[] bgra, int width, int height, PixelFormat dest,
            BitmapPalette palette, out int stride)
        {
            stride = 0;
            int bpp = Bpp(dest);
            if (bpp == 0 || bgra == null || width <= 0 || height <= 0) return null;

            bool indexed = dest.Palettized;
            System.Collections.Generic.IList<Color> colors = palette?.Colors;
            if (indexed && (colors == null || colors.Count == 0)) return null;

            stride = checked((width * bpp + 7) / 8);
            var dst = new byte[checked(stride * height)];

            // Nearest-colour matching is a scan of up to 256 entries per pixel, and the images that
            // reach it are overwhelmingly made of few distinct colours (that is why they are being
            // palettised), so remembering each answer turns almost all of them into a lookup.
            var nearest = indexed ? new System.Collections.Generic.Dictionary<int, int>() : null;
            int maxIndex = indexed ? Math.Min(colors.Count, 1 << bpp) - 1 : 0;

            for (int y = 0; y < height; y++)
            {
                int rowStart = y * stride;
                int si = y * width * 4;

                for (int x = 0; x < width; x++, si += 4)
                {
                    byte b = bgra[si], g = bgra[si + 1], r = bgra[si + 2], a = bgra[si + 3];
                    int value;

                    if (indexed)
                    {
                        int key = (r << 16) | (g << 8) | b;
                        if (!nearest.TryGetValue(key, out value))
                        {
                            value = NearestPaletteIndex(colors, maxIndex, r, g, b);
                            nearest[key] = value;
                        }
                    }
                    else if (bpp < 8 || dest.Format == PixelFormatEnum.Gray8 || dest.Format == PixelFormatEnum.Gray16)
                    {
                        // Greyscale destinations quantise the Rec.601 luma to the levels available.
                        int luma = (r * 77 + g * 150 + b * 29) >> 8;
                        int levels = (1 << bpp) - 1;
                        value = dest.Format switch
                        {
                            PixelFormatEnum.Gray16 => luma,          // high byte only; see below
                            PixelFormatEnum.Gray8 => luma,
                            // ROUND to the nearest level rather than truncating. Truncating makes
                            // the top level unreachable except from an exact 255 -- for BlackWhite,
                            // where there is only one level above zero, that turns every grey but
                            // pure white into black and the picture comes out nearly blank.
                            _ => (luma * levels + 127) / 255,
                        };
                    }
                    else
                    {
                        value = 0;   // handled by the wide-format switch below
                    }

                    if (bpp < 8)
                    {
                        int perByte = 8 / bpp;
                        int shift = 8 - bpp - (x % perByte) * bpp;
                        dst[rowStart + x / perByte] |= (byte)(value << shift);
                        continue;
                    }

                    int di = rowStart + x * (bpp / 8);
                    switch (dest.Format)
                    {
                        case PixelFormatEnum.Indexed8:
                            dst[di] = (byte)value;
                            break;
                        case PixelFormatEnum.Gray8:
                            dst[di] = (byte)value;
                            break;
                        case PixelFormatEnum.Gray16:
                            dst[di] = (byte)value; dst[di + 1] = (byte)value;
                            break;
                        case PixelFormatEnum.Bgr555:
                        {
                            int v = ((r * 31 / 255) << 10) | ((g * 31 / 255) << 5) | (b * 31 / 255);
                            dst[di] = (byte)(v & 0xFF); dst[di + 1] = (byte)(v >> 8);
                            break;
                        }
                        case PixelFormatEnum.Bgr565:
                        {
                            int v = ((r * 31 / 255) << 11) | ((g * 63 / 255) << 5) | (b * 31 / 255);
                            dst[di] = (byte)(v & 0xFF); dst[di + 1] = (byte)(v >> 8);
                            break;
                        }
                        case PixelFormatEnum.Bgr24:
                            dst[di] = b; dst[di + 1] = g; dst[di + 2] = r;
                            break;
                        case PixelFormatEnum.Rgb24:
                            dst[di] = r; dst[di + 1] = g; dst[di + 2] = b;
                            break;
                        case PixelFormatEnum.Bgr32:
                            dst[di] = b; dst[di + 1] = g; dst[di + 2] = r; dst[di + 3] = 255;
                            break;
                        case PixelFormatEnum.Bgra32:
                            dst[di] = b; dst[di + 1] = g; dst[di + 2] = r; dst[di + 3] = a;
                            break;
                        case PixelFormatEnum.Pbgra32:
                            dst[di] = (byte)(b * a / 255); dst[di + 1] = (byte)(g * a / 255);
                            dst[di + 2] = (byte)(r * a / 255); dst[di + 3] = a;
                            break;
                        default:
                            stride = 0;
                            return null;
                    }
                }
            }

            return dst;
        }

        private static int NearestPaletteIndex(System.Collections.Generic.IList<Color> colors,
            int maxIndex, byte r, byte g, byte b)
        {
            int best = 0, bestDistance = int.MaxValue;
            for (int i = 0; i <= maxIndex; i++)
            {
                Color c = colors[i];
                int dr = c.R - r, dg = c.G - g, db = c.B - b;
                int distance = dr * dr + dg * dg + db * db;
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = i;
                    if (distance == 0) break;
                }
            }
            return best;
        }

        private static byte Scale5(int v) => (byte)(v * 255 / 31);

        private static byte Unpremultiply(byte channel, byte alpha)
            => alpha == 0 ? (byte)0 : alpha == 255 ? channel : (byte)Math.Min(255, channel * 255 / alpha);
    }
}
