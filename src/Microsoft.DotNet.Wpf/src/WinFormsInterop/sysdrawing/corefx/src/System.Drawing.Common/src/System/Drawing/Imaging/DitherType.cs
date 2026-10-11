// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace System.Drawing.Imaging
{
    /// <summary>
    /// Specifies the dithering algorithm <see cref="Bitmap.ConvertFormat(PixelFormat, DitherType, PaletteType, ColorPalette, float)"/>
    /// uses (GDI+ 1.1's DitherType).
    /// </summary>
    public enum DitherType
    {
        /// <summary>No dithering: each pixel takes the nearest palette entry.</summary>
        None = 0,
        /// <summary>The nearest palette entry, as None.</summary>
        Solid = 1,
        /// <summary>A 4x4 ordered (Bayer) dither over a fixed halftone palette.</summary>
        Ordered4x4 = 2,
        /// <summary>An 8x8 ordered dither over a fixed halftone palette.</summary>
        Ordered8x8 = 3,
        /// <summary>A 16x16 ordered dither over a fixed halftone palette.</summary>
        Ordered16x16 = 4,
        /// <summary>A 4x4 spiral dither over a fixed halftone palette.</summary>
        Spiral4x4 = 5,
        /// <summary>An 8x8 spiral dither over a fixed halftone palette.</summary>
        Spiral8x8 = 6,
        /// <summary>A 4x4 dual spiral dither over a fixed halftone palette.</summary>
        DualSpiral4x4 = 7,
        /// <summary>An 8x8 dual spiral dither over a fixed halftone palette.</summary>
        DualSpiral8x8 = 8,
        /// <summary>Floyd-Steinberg error diffusion into any palette.</summary>
        ErrorDiffusion = 9,
    }
}
