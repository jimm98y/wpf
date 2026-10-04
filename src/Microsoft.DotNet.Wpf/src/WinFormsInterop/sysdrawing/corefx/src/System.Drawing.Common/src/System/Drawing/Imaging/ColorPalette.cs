// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Runtime.InteropServices;

namespace System.Drawing.Imaging
{
    /// <summary>
    /// Defines an array of colors that make up a color palette.
    /// </summary>
    public sealed class ColorPalette
    {
        // We don't provide a public constructor for ColorPalette because if we allow 
        // arbitrary creation of color palettes you could in theroy not only change the color entries, but the size 
        // of the palette and that is not valid for an image (meaning you cannot change the palette size for an image).  
        // ColorPalettes are only valid for "indexed" images like GIFs.

        private int _flags;
        private Color[] _entries;

        /// <summary>
        /// Specifies how to interpret the color information in the array of colors.
        /// </summary>
        public int Flags
        {
            get
            {
                return _flags;
            }
        }

        /// <summary>
        /// Specifies an array of <see cref='Color'/> objects.
        /// </summary>
        public Color[] Entries
        {
            get
            {
                return _entries;
            }
        }

        internal ColorPalette(int count)
        {
            _entries = new Color[count];
        }

        internal ColorPalette()
        {
            _entries = new Color[1];
        }

        /// <summary>A managed image's palette, as GDI+ would hand it out.</summary>
        internal ColorPalette(int flags, Color[] entries)
        {
            _flags = flags;
            _entries = entries;
        }

        /// <summary>
        /// Creates a custom color palette of <paramref name="customColors"/>.
        /// </summary>
        public ColorPalette(params Color[] customColors) : this(0, customColors)
        {
        }

        /// <summary>
        /// Creates a standard color palette (GdipInitializePalette with no bitmap).
        /// </summary>
        public ColorPalette(PaletteType fixedPaletteType)
        {
            ColorPalette palette = InitializePalette(fixedPaletteType, 0, useTransparentColor: false, bitmap: null);
            _flags = palette.Flags;
            _entries = palette.Entries;
        }

        /// <summary>
        /// Creates an optimal color palette of <paramref name="colors"/> entries for the colors in
        /// <paramref name="bitmap"/> (GDI+'s median cut), its last entry transparent black when
        /// <paramref name="useTransparentColor"/>.
        /// </summary>
        public static ColorPalette CreateOptimalPalette(int colors, bool useTransparentColor, Bitmap bitmap)
            => InitializePalette((PaletteType)1, colors, useTransparentColor, bitmap);

        // System.Drawing.Common hands GdipInitializePalette a 256-entry buffer; for Custom GDI+ leaves
        // it as it is (here: zeros).
        internal static ColorPalette InitializePalette(PaletteType fixedPaletteType, int colorCount, bool useTransparentColor, Bitmap bitmap)
        {
            uint[] p = GdipHalftone.InitializePalette((int)fixedPaletteType, colorCount, useTransparentColor, bitmap?.Data.Frame, out int flags)
                       ?? new uint[256];
            var entries = new Color[p.Length];
            for (int i = 0; i < p.Length; i++) entries[i] = Color.FromArgb(unchecked((int)p[i]));
            return new ColorPalette(flags, entries);
        }

        internal void ConvertFromMemory(IntPtr memory)
        {
            // Memory layout is:
            //    UINT Flags
            //    UINT Count
            //    ARGB Entries[size]

            _flags = Marshal.ReadInt32(memory);

            int size;

            size = Marshal.ReadInt32((IntPtr)((long)memory + 4));  // Marshal.SizeOf(size.GetType())

            _entries = new Color[size];

            for (int i = 0; i < size; i++)
            {
                // use Marshal.SizeOf()
                int argb = Marshal.ReadInt32((IntPtr)((long)memory + 8 + i * 4));
                _entries[i] = Color.FromArgb(argb);
            }
        }

        internal IntPtr ConvertToMemory()
        {
            // Memory layout is:
            //    UINT Flags
            //    UINT Count
            //    ARGB Entries[size]

            // use Marshal.SizeOf()
            int length = _entries.Length;
            IntPtr memory = Marshal.AllocHGlobal(checked(4 * (2 + length)));

            Marshal.WriteInt32(memory, 0, _flags);
            // use Marshal.SizeOf()
            Marshal.WriteInt32((IntPtr)checked((long)memory + 4), 0, length);

            for (int i = 0; i < length; i++)
            {
                // use Marshal.SizeOf()
                Marshal.WriteInt32((IntPtr)((long)memory + 4 * (i + 2)), 0, _entries[i].ToArgb());
            }

            return memory;
        }
    }
}
