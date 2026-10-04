// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// An Image's pixels with no GDI+ behind them: what gdiplus.dll keeps for a GpBitmap, kept here.
//
// A frame is held in ITS OWN pixel format, at GDI+'s stride (rows padded to four bytes), with its
// palette when indexed -- so a 24bpp bitmap stores 24 bits a pixel, a 1555 bitmap rounds what is
// set into it exactly as GDI+ does, and a lock in the bitmap's own format hands back the bitmap's
// own bytes. Conversions between formats (GetPixel, SetPixel, LockBits in another format, drawing)
// go through straight 32bpp ARGB with GDI+'s arithmetic, which GdipPixels holds.
//
// A multi-frame image (an animated GIF, a multi-page TIFF) keeps every decoded frame, and
// SelectActiveFrame makes one of them current, as GDI+ re-decodes the frame it is asked for.
//

using System.Collections.Generic;
using System.Drawing.Imaging;

namespace System.Drawing
{
    /// <summary>One frame of pixels, as GDI+ stores it.</summary>
    internal sealed class GdipFrame
    {
        internal int Width, Height, Stride;
        internal PixelFormat Format;
        internal byte[] Bits;
        /// <summary>The palette of an indexed frame (null otherwise). Setting it makes it the frame's
        /// OWN palette (see <see cref="PaletteIsDefault"/>).</summary>
        internal Color[] Palette
        {
            get => _palette;
            set { _palette = value; PaletteIsDefault = false; }
        }
        private Color[] _palette;
        /// <summary>True while an indexed frame has only the palette its format starts with: GDI+
        /// keeps NO palette for such a bitmap (GpMemoryBitmap+0x68 is null) and a lock or write in
        /// another indexed format uses that format's default palette instead of this one.</summary>
        internal bool PaletteIsDefault;
        internal int PaletteFlags;
        internal float DpiX = 96f, DpiY = 96f;

        internal GdipFrame(int width, int height, PixelFormat format)
        {
            Width = width;
            Height = height;
            Format = format;
            Stride = StrideFor(width, format);
            Bits = new byte[checked((long)Stride * height)];
            if ((format & PixelFormat.Indexed) != 0)
            {
                _palette = GdipPixels.DefaultPalette(format, out PaletteFlags);
                PaletteIsDefault = true;
            }
        }

        private GdipFrame()
        {
        }

        /// <summary>GDI+'s row size: whole pixels' bits rounded up to four bytes.</summary>
        internal static int StrideFor(int width, PixelFormat format)
            => checked(((width * Image.GetPixelFormatSize(format) + 31) / 32) * 4);

        internal int BitsPerPixel => Image.GetPixelFormatSize(Format);

        internal bool IsIndexed => (Format & PixelFormat.Indexed) != 0;

        internal GdipFrame Clone()
        {
            var f = new GdipFrame
            {
                Width = Width, Height = Height, Stride = Stride, Format = Format,
                Bits = (byte[])Bits.Clone(),
                Palette = Palette == null ? null : (Color[])Palette.Clone(),
                PaletteFlags = PaletteFlags, DpiX = DpiX, DpiY = DpiY,
                PaletteIsDefault = PaletteIsDefault,
            };
            return f;
        }
    }

    /// <summary>Everything a managed Image holds: the current frame, how it was read, its
    /// properties, and the frames it can switch to.</summary>
    internal sealed class GdipImageData
    {
        /// <summary>The pixels the Bitmap is: the active frame, writable.</summary>
        internal GdipFrame Frame;
        internal Guid RawFormat = ImageFormat.MemoryBmp.Guid;
        internal int Flags;
        internal List<PropertyItem> Properties = new List<PropertyItem> ();
        /// <summary>FrameDimension.Page, Time or Resolution; with <see cref="Frames"/>.</summary>
        internal Guid FrameDimension = Imaging.FrameDimension.Page.Guid;
        /// <summary>Every decoded frame, as decoded (null for a single-frame image).</summary>
        internal GdipFrame[] Frames;
        /// <summary>Per frame, its own property items (a TIFF page's directory); null when every
        /// frame shares <see cref="Properties"/>.</summary>
        internal List<PropertyItem>[] FrameProperties;
        internal int ActiveFrame;

        internal GdipImageData(GdipFrame frame)
        {
            Frame = frame;
        }

        internal int FrameCount => Frames?.Length ?? 1;

        internal GdipImageData Clone()
        {
            var c = new GdipImageData(Frame.Clone())
            {
                RawFormat = RawFormat, Flags = Flags, FrameDimension = FrameDimension,
                Frames = Frames, FrameProperties = FrameProperties, ActiveFrame = ActiveFrame,
            };
            foreach (PropertyItem p in Properties)
                c.Properties.Add(GdipCodecs.Property (p.Id, p.Type, p.Len, p.Value == null ? null : (byte[])p.Value.Clone ()));
            return c;
        }

        /// <summary>The flags of a bitmap created in memory: alpha for an alpha or indexed format.</summary>
        internal static int NewFlags(PixelFormat format)
            => Image.IsAlphaPixelFormat(format) || (format & PixelFormat.Indexed) != 0 ? (int)ImageFlags.HasAlpha : 0;
    }
}
