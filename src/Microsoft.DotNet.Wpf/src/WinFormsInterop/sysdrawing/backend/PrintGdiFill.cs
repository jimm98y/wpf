// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// GDI+'s SOLID fills on a printer DC, as Graphics.Print.cs records them (gdiplus.dll 10.0.26100 arm64,
// public PDB):
//
//   DriverPrint::FillPath @1800cded0   ConvertPathToGdi @1800d8248 (flags 0x13) of the path through
//                                      the world-to-device matrix, its bounds the fill's draw rectangle
//   DriverPrint::FillRects @1800ce1e0  ConvertRectFToGdi @180033c80: each rectangle's corners by the
//                                      rasterizer's ceiling, the bounds their union
//   DriverPrint::SetupBrush @1800d11f0 the brush's alpha (+0xb0); under 2 nothing is drawn at all
//   ConvertBrushToGdi::SetBrush @1800d9d78 / InitializeBrush @1800d9b80   CreateSolidBrush of the
//                                      colour without its alpha, kept until the colour changes
//
// An alpha of 0xfe or more is filled as it is: ConvertPathToGdi::Fill @1800d95c0 (a polygon with the
// null pen, or BeginPath / PolyBezier or the mixed path / EndPath / FillPath) or ConvertRectFToGdi::
// Fill @1800340b8 (PatBlt PATCOPY a rectangle). Anything more translucent cannot be blended on a
// printer, so it is DITHERED with GDI's own raster operations (ConvertPathToGdi::AlphaFill @1800d8a50,
// ConvertRectFToGdi::AlphaFill @1800d8b60): the bounds PATINVERTed with the colour, the shape filled
// with an alpha brush under R2_MASKPEN (or PatBlt'd DPa) with the text colour made the background's,
// and the bounds PATINVERTed again -- the colour where the alpha brush is black, the paper as it was
// where it is white. The alpha brush is ConvertAlphaToGdi::SetAlpha @1800d9c70 (alpha exact, the
// 16 x 16 dither, the process-wide phase DAT_1802e8398 stepped first) -> CreateAlphaBrush @1800d9000:
// a 16 x 16 1bpp bottom-up DIB put down with CreateDIBPatternBrushPt.
//
// The same alpha brush and shape fill serve a near-constant translucent straight gradient
// (PrivateFillRect @1800cf510): the gradient bitmap SRCINVERT, the shape (ConvertPathToGdi flags 2)
// under R2_MASKPEN, SRCINVERT again -- see PrintRaster.KindXorPath.
//
// A recorded page carries the fill as an ordinary translucent SolidColorBrush (what a preview draws);
// the vector devices find this beside it and put down what GDI+ does: SceneGdiDevice the same calls,
// ScenePdf the shape in the colour through a tiling pattern of the alpha brush's bits.
//

using System.Runtime.CompilerServices;
using System.Drawing.WebGpuBackend.Gdip;

namespace System.Drawing.WebGpuBackend
{
    internal sealed class PrintGdiFill
    {
        /// <summary>The brush colour as a COLORREF (0x00bbggrr), its alpha dropped.</summary>
        public int Color;
        /// <summary>The alpha brush CreateAlphaBrush makes (a packed DIB: header, two palette
        /// entries, the bits), or null for an opaque fill.</summary>
        public byte[] AlphaDib;
        public GdiShape Shape;
        public float DpiX, DpiY;

        private static readonly ConditionalWeakTable<object, PrintGdiFill> s_table = new();

        internal static void Attach(object sceneBrush, PrintGdiFill fill) => s_table.AddOrUpdate(sceneBrush, fill);

        internal static PrintGdiFill Find(object sceneBrush)
            => sceneBrush != null && s_table.TryGetValue(sceneBrush, out PrintGdiFill f) ? f : null;

        /// <summary>CreateAlphaBrush's DIB: 16 x 16, rows bottom-up, 4 bytes a row.</summary>
        internal const int AlphaDibSize = 16, AlphaDibBits = 48, AlphaDibStride = 4;

        /// <summary>Whether the alpha brush's pixel at DEVICE (x, y) is black -- where the colour lands
        /// (the brush origin is the device's).</summary>
        internal static bool AlphaKeeps(byte[] dib, int x, int y)
        {
            int col = x & 15, row = 15 - (y & 15);
            return (dib[AlphaDibBits + row * AlphaDibStride + (col >> 3)] & (0x80 >> (col & 7))) == 0;
        }

        /// <summary>SetAlpha(alpha, 1, 1) -> CreateAlphaBrush(alpha, 16 x 16): the phase stepped, the DIB.</summary>
        internal static byte[] NextAlphaDib(int alpha) => GpMetafileRecorder.NextAlphaBrushDib((uint)alpha, true);
    }

    /// <summary>What ConvertPathToGdi / ConvertRectFToGdi make of a shape: device POINTs and how GDI
    /// is asked to fill them, and the device bounds the PATINVERTs cover.</summary>
    internal sealed class GdiShape
    {
        /// <summary>Rectangles (l, t, r, b quadruples), filled by PatBlt.</summary>
        public int[] Rects;
        /// <summary>ConvertPathToGdi's flags (+0x1b4): 1 polygons, 0x10 one figure of Beziers, else
        /// a mixed path; and GDI's fill mode (1 ALTERNATE, 2 WINDING).</summary>
        public int Flags, FillMode;
        public int[] Pts;
        public int NPts, NSub;
        public int[] Counts;
        public byte[] Types;
        public int X, Y, W, H;

        public bool IsRects => Rects != null;

        internal static GdiShape Of(GpMetafileRecorder.PathToGdi p) => new GdiShape
        {
            Flags = p.Flags, FillMode = p.FillMode, Pts = p.Pts, NPts = p.NPts, NSub = p.NSub, Counts = p.Counts,
            Types = p.Types, X = p.X, Y = p.Y, W = p.W, H = p.H,
        };

        internal static GdiShape Of(GpMetafileRecorder.RectFToGdi r)
        {
            var rects = new int[r.Rects.Count * 4];
            for (int i = 0; i < r.Rects.Count; i++) Array.Copy(r.Rects[i], 0, rects, i * 4, 4);
            return new GdiShape { Rects = rects, FillMode = 1, X = r.X, Y = r.Y, W = r.W, H = r.H };
        }
    }
}
