// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The byte-level vocabulary the managed image codecs speak, shared by WPF (PresentationCore) and
// System.Drawing (the WinForms port's Mono.System.Drawing), which link-compile these files.
//
// Neither side's types appear here: no BitmapSource, PixelFormat or BitmapPalette (WPF), no
// Bitmap or Imaging.PixelFormat (System.Drawing). A picture is a layout named for its bytes, packed
// rows, and a palette as 0xAARRGGBB words. Each assembly keeps its own thin adapter that turns its
// image type into this and back (the *.Wpf.cs halves next to the WPF encoders; the codec bridge in
// System.Drawing's backend).
//

using System;

namespace System.Windows.Media.Imaging
{
    /// <summary>How a row of pixels is laid out, named after WPF's pixel formats (whose byte orders
    /// they are). Sub-byte layouts put the FIRST pixel in the HIGH bits.</summary>
    internal enum ManagedPixelLayout
    {
        Unknown = 0,
        BlackWhite,
        Gray2,
        Gray4,
        Gray8,
        Gray16,
        Indexed1,
        Indexed2,
        Indexed4,
        Indexed8,
        Bgr555,
        Bgr565,
        Bgr24,
        Rgb24,
        Bgr32,
        Bgra32,
        Pbgra32,
        Rgb48,
        Rgba64,
        Prgba64,
    }

    internal static class ManagedPixelLayouts
    {
        internal static int BitsPerPixel(ManagedPixelLayout layout) => layout switch
        {
            ManagedPixelLayout.BlackWhite or ManagedPixelLayout.Indexed1 => 1,
            ManagedPixelLayout.Gray2 or ManagedPixelLayout.Indexed2 => 2,
            ManagedPixelLayout.Gray4 or ManagedPixelLayout.Indexed4 => 4,
            ManagedPixelLayout.Gray8 or ManagedPixelLayout.Indexed8 => 8,
            ManagedPixelLayout.Bgr555 or ManagedPixelLayout.Bgr565 or ManagedPixelLayout.Gray16 => 16,
            ManagedPixelLayout.Bgr24 or ManagedPixelLayout.Rgb24 => 24,
            ManagedPixelLayout.Bgr32 or ManagedPixelLayout.Bgra32 or ManagedPixelLayout.Pbgra32 => 32,
            ManagedPixelLayout.Rgb48 => 48,
            ManagedPixelLayout.Rgba64 or ManagedPixelLayout.Prgba64 => 64,
            _ => 0,
        };

        internal static bool IsIndexed(ManagedPixelLayout layout) =>
            layout is ManagedPixelLayout.Indexed1 or ManagedPixelLayout.Indexed2
                   or ManagedPixelLayout.Indexed4 or ManagedPixelLayout.Indexed8;

        internal static bool IsGray(ManagedPixelLayout layout) =>
            layout is ManagedPixelLayout.BlackWhite or ManagedPixelLayout.Gray2 or ManagedPixelLayout.Gray4
                   or ManagedPixelLayout.Gray8 or ManagedPixelLayout.Gray16;

        /// <summary>The indexed layout of <paramref name="bits"/> per pixel.</summary>
        internal static ManagedPixelLayout Indexed(int bits) => bits switch
        {
            1 => ManagedPixelLayout.Indexed1,
            2 => ManagedPixelLayout.Indexed2,
            4 => ManagedPixelLayout.Indexed4,
            _ => ManagedPixelLayout.Indexed8,
        };

        /// <summary>The greyscale layout of <paramref name="bits"/> per pixel.</summary>
        internal static ManagedPixelLayout Gray(int bits) => bits switch
        {
            1 => ManagedPixelLayout.BlackWhite,
            2 => ManagedPixelLayout.Gray2,
            4 => ManagedPixelLayout.Gray4,
            16 => ManagedPixelLayout.Gray16,
            _ => ManagedPixelLayout.Gray8,
        };
    }

    /// <summary>A picture in one layout: packed rows of <see cref="Stride"/> bytes, a palette for
    /// the indexed layouts, and the resolution it was stored at.</summary>
    internal sealed class ManagedRaster
    {
        internal int Width;
        internal int Height;
        internal ManagedPixelLayout Layout;
        internal byte[] Pixels;
        internal int Stride;
        /// <summary>0xAARRGGBB per entry; indexed layouts only.</summary>
        internal uint[] Palette;
        internal double DpiX = 96;
        internal double DpiY = 96;

        internal ManagedRaster(int width, int height, ManagedPixelLayout layout, byte[] pixels, int stride, uint[] palette = null)
        {
            Width = width; Height = height; Layout = layout; Pixels = pixels; Stride = stride; Palette = palette;
        }
    }
}
