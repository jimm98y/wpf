// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace System.Drawing.Imaging
{
    /// <summary>
    /// Specifies the type of a color palette (GDI+ 1.1's PaletteType). GDI+'s PaletteTypeOptimal (1)
    /// is reached through <see cref="ColorPalette.CreateOptimalPalette"/>, as in System.Drawing.Common.
    /// </summary>
    public enum PaletteType
    {
        /// <summary>A palette that is not one of the fixed ones.</summary>
        Custom = 0,
        /// <summary>Black and white.</summary>
        FixedBlackAndWhite = 2,
        /// <summary>The eight primaries and the eight other VGA colours.</summary>
        FixedHalftone8 = 3,
        /// <summary>Three levels of each primary (27), and the VGA colours the cube lacks.</summary>
        FixedHalftone27 = 4,
        /// <summary>Four levels of each primary (64), and the VGA colours the cube lacks.</summary>
        FixedHalftone64 = 5,
        /// <summary>Five levels of each primary (125), and the VGA colours the cube lacks.</summary>
        FixedHalftone125 = 6,
        /// <summary>Six levels of each primary (216), and the VGA colours the cube lacks.</summary>
        FixedHalftone216 = 7,
        /// <summary>Six levels of red and blue and seven of green (252).</summary>
        FixedHalftone252 = 8,
        /// <summary>Eight levels of red and green and four of blue (256).</summary>
        FixedHalftone256 = 9,
    }
}
