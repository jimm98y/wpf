// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// WPF's half of the managed pixel converter: PixelFormat and BitmapPalette in, the shared
// byte-level converter (Shared/MS/Internal/Imaging/ManagedPixelConverter.cs) underneath. Also the
// mapping between WPF's pixel formats and the codecs' ManagedPixelLayout, which every WPF-facing
// codec adapter goes through.
//

using System.Windows.Media;
using MS.Internal;

namespace System.Windows.Media.Imaging
{
    internal static partial class ManagedPixelConverter
    {
        internal static bool CanConvert(PixelFormat format) => Bpp(Layout(format)) > 0;

        internal static byte[] ToBgra32(byte[] src, int srcStride, int width, int height,
            PixelFormat format, BitmapPalette palette)
            => ToBgra32(src, srcStride, width, height, Layout(format), Palette(palette));

        internal static byte[] FromBgra32(byte[] bgra, int width, int height, PixelFormat dest,
            BitmapPalette palette, out int stride)
            => FromBgra32(bgra, width, height, Layout(dest), Palette(palette), out stride);

        /// <summary>The codec layout a WPF pixel format stores, or Unknown.</summary>
        internal static ManagedPixelLayout Layout(PixelFormat format) => format.Format switch
        {
            PixelFormatEnum.BlackWhite => ManagedPixelLayout.BlackWhite,
            PixelFormatEnum.Gray2 => ManagedPixelLayout.Gray2,
            PixelFormatEnum.Gray4 => ManagedPixelLayout.Gray4,
            PixelFormatEnum.Gray8 => ManagedPixelLayout.Gray8,
            PixelFormatEnum.Gray16 => ManagedPixelLayout.Gray16,
            PixelFormatEnum.Indexed1 => ManagedPixelLayout.Indexed1,
            PixelFormatEnum.Indexed2 => ManagedPixelLayout.Indexed2,
            PixelFormatEnum.Indexed4 => ManagedPixelLayout.Indexed4,
            PixelFormatEnum.Indexed8 => ManagedPixelLayout.Indexed8,
            PixelFormatEnum.Bgr555 => ManagedPixelLayout.Bgr555,
            PixelFormatEnum.Bgr565 => ManagedPixelLayout.Bgr565,
            PixelFormatEnum.Bgr24 => ManagedPixelLayout.Bgr24,
            PixelFormatEnum.Rgb24 => ManagedPixelLayout.Rgb24,
            PixelFormatEnum.Bgr32 => ManagedPixelLayout.Bgr32,
            PixelFormatEnum.Bgra32 => ManagedPixelLayout.Bgra32,
            PixelFormatEnum.Pbgra32 => ManagedPixelLayout.Pbgra32,
            PixelFormatEnum.Rgb48 => ManagedPixelLayout.Rgb48,
            PixelFormatEnum.Rgba64 => ManagedPixelLayout.Rgba64,
            PixelFormatEnum.Prgba64 => ManagedPixelLayout.Prgba64,
            _ => ManagedPixelLayout.Unknown,
        };

        /// <summary>The WPF pixel format of a codec layout.</summary>
        internal static PixelFormat Format(ManagedPixelLayout layout) => layout switch
        {
            ManagedPixelLayout.BlackWhite => PixelFormats.BlackWhite,
            ManagedPixelLayout.Gray2 => PixelFormats.Gray2,
            ManagedPixelLayout.Gray4 => PixelFormats.Gray4,
            ManagedPixelLayout.Gray8 => PixelFormats.Gray8,
            ManagedPixelLayout.Gray16 => PixelFormats.Gray16,
            ManagedPixelLayout.Indexed1 => PixelFormats.Indexed1,
            ManagedPixelLayout.Indexed2 => PixelFormats.Indexed2,
            ManagedPixelLayout.Indexed4 => PixelFormats.Indexed4,
            ManagedPixelLayout.Indexed8 => PixelFormats.Indexed8,
            ManagedPixelLayout.Bgr555 => PixelFormats.Bgr555,
            ManagedPixelLayout.Bgr565 => PixelFormats.Bgr565,
            ManagedPixelLayout.Bgr24 => PixelFormats.Bgr24,
            ManagedPixelLayout.Rgb24 => PixelFormats.Rgb24,
            ManagedPixelLayout.Bgr32 => PixelFormats.Bgr32,
            ManagedPixelLayout.Pbgra32 => PixelFormats.Pbgra32,
            ManagedPixelLayout.Rgb48 => PixelFormats.Rgb48,
            ManagedPixelLayout.Rgba64 => PixelFormats.Rgba64,
            ManagedPixelLayout.Prgba64 => PixelFormats.Prgba64,
            _ => PixelFormats.Bgra32,
        };

        /// <summary>A WPF palette as the codecs' 0xAARRGGBB words (null for none).</summary>
        internal static uint[] Palette(BitmapPalette palette)
        {
            IList<Color> colors = palette?.Colors;
            if (colors == null) return null;
            var words = new uint[colors.Count];
            for (int i = 0; i < words.Length; i++)
            {
                Color c = colors[i];
                words[i] = ((uint)c.A << 24) | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B;
            }
            return words;
        }

        /// <summary>The codecs' palette words as a WPF palette (null for none).</summary>
        internal static BitmapPalette Palette(uint[] words)
        {
            if (words == null) return null;
            var colors = new List<Color>(words.Length);
            foreach (uint w in words)
            {
                colors.Add(Color.FromArgb((byte)(w >> 24), (byte)(w >> 16), (byte)(w >> 8), (byte)w));
            }
            return new BitmapPalette(colors);
        }
    }
}
