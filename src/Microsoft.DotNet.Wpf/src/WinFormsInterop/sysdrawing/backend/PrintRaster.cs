// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The two rasters GDI+'s printer driver puts down that are not a plain bitmap (DriverNonPS::
// OutputBufferDIB @1800cb640, gdiplus.dll 10.0.26100 arm64), as Graphics.Print.cs records them.
//
// A recorded page holds a raster as an ImageBrush over a device rectangle: what a preview draws and
// what the vector devices are handed. These two need more than the brush's pixels to be put down as
// GDI+ puts them down, so the recorder files that here, against the brush's pixel array:
//
//   Masked  a translucent fill. GDI+ cannot blend on a printer, so it HALFTONES the alpha: a colour
//           DIB of the brush (one pixel per s x t device pixels), and a 1bpp mask at the device's
//           own resolution -- a pixel is set where the 16 x 16 ordered-dither threshold is under the
//           alpha the shape has there. The colour goes down SRCINVERT, the mask SRCAND, the colour
//           SRCINVERT again: paper where the mask is clear, the colour where it is set.
//   Runs    a texture whose bitmap is only opaque or clear: each row of the DIB put down as the runs
//           of its pixels with alpha 5 or more, one StretchDIBits a run, inside the shape's clip.
//   XorPath an opaque straight gradient in a shape that is not a rectangle: the bitmap SRCINVERT,
//           the shape (ConvertPathToGdi, flags 2) filled under R2_MASKPEN with the stock black
//           brush -- or, for a near-constant translucent one, the alpha brush (PrintGdiFill) --
//           the bitmap SRCINVERT again. Its clip is the preview's only: on paper the XOR clips.
//   AlphaXor a near-constant translucent bitmap gradient (ConvertBitmapToGdi::StretchBlt @1800d9e60
//           with an alpha level): the bitmap SRCINVERT over the device rectangle, the rectangle
//           PatBlt DPa with the alpha brush, the bitmap SRCINVERT again, inside the shape's clip.
//
// The DIBs go to GDI as GDI+ hands them: bottom-up, colour 24bpp, a mask 1bpp under a white
// (0xFFFFFFFF) / black palette (measured: Microsoft Print to PDF folds the XOR / AND / XOR back
// into one translucent image at cell resolution for DIBs in that form, and not for top-down 32bpp).
//
// The preview gets an ordinary image: the colour with each cell's share of mask, or the runs' cells
// opaque and the rest clear.
//

using System.Runtime.CompilerServices;

namespace System.Drawing.WebGpuBackend
{
    internal sealed class PrintRaster
    {
        internal const int KindMasked = 1, KindRuns = 2, KindXorPath = 3, KindAlphaXor = 4;

        public int Kind;
        /// <summary>The colour DIB as GDI+ hands it over: Width x Height cells, top-down, BGRA (alpha
        /// unused for Masked; for Runs, the straight alpha the runs are cut by).</summary>
        public byte[] Color;
        public int Width, Height;
        /// <summary>Masked: the part of the colour DIB that is put down (cells).</summary>
        public int SrcX, SrcW;
        /// <summary>Masked: the mask, MaskWidth x Height' device pixels, top-down, 1bpp rows of
        /// ((MaskWidth + 31) / 32) * 4 bytes, MSB first; set = draw.</summary>
        public byte[] Mask;
        public int MaskWidth, MaskHeight, MaskSrcX;
        /// <summary>Masked: the mask's source width, when it is not the device rectangle's (a
        /// straight gradient's alpha is halftoned at 300 dpi and stretched).</summary>
        public int MaskSrcW;
        /// <summary>XorPath: the shape, in the recording's units (x,y pairs, GDI+ point types).</summary>
        public float[] ClipXY;
        public byte[] ClipTypes;
        public bool ClipNonZero;
        /// <summary>XorPath: the shape as ConvertPathToGdi (flags 2) puts it to GDI, filled under
        /// R2_MASKPEN with <see cref="AlphaDib"/> (a near-constant translucent gradient's alpha brush,
        /// see PrintGdiFill) or, when that is null, the stock black brush.</summary>
        public GdiShape Shape;
        public byte[] AlphaDib;
        /// <summary>Device pixels per DIB pixel.</summary>
        public int S, T;
        /// <summary>The device rectangle the raster covers (for Runs, the band).</summary>
        public int DevX, DevY, DevW, DevH;

        private static readonly ConditionalWeakTable<byte[], PrintRaster> s_table = new();

        internal static void Attach(byte[] previewRgba, PrintRaster raster) => s_table.AddOrUpdate(previewRgba, raster);

        internal static PrintRaster Find(byte[] previewRgba)
            => previewRgba != null && s_table.TryGetValue(previewRgba, out PrintRaster r) ? r : null;

        internal static int MaskStride(int width) => ((width + 31) / 32) * 4;

        internal bool MaskBit(int x, int y) => (Mask[y * MaskStride(MaskWidth) + (x >> 3)] & (0x80 >> (x & 7))) != 0;
    }
}
