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

    /// <summary>ConvertPenToGdi @1800d8620 as DriverPrint::StrokePath asks for it: the GDI pen a
    /// GDI+ pen becomes (ExtCreatePen's style and width, the miter limit set beside it), the colour
    /// the brush's, and the shape ConvertPathToGdi::Draw @1800d9270 strokes with it.</summary>
    internal sealed class GdiPen
    {
        public uint Style;
        public int Width;
        public bool SetMiter;
        public float Miter;
        public int Color;
        public GdiShape Shape;

        private static readonly ConditionalWeakTable<object, GdiPen> s_table = new();

        internal static void Attach(object sceneBrush, GdiPen pen) => s_table.AddOrUpdate(sceneBrush, pen);

        internal static GdiPen Find(object sceneBrush)
            => sceneBrush != null && s_table.TryGetValue(sceneBrush, out GdiPen p) ? p : null;

        /// <summary>The pen, or null where ConvertPenToGdi fails (the caller then widens); flags
        /// gains 0x20 for a one-pixel cosmetic pen.</summary>
        internal static GdiPen From(DpPen pen, GpMatrix m, float dpi, ref int flags)
        {
            int join = pen.Join;
            uint type = 0x10000;
            if ((pen.CompoundCount > 0 || pen.Alignment != 0) && (flags & 1) == 0) return null;
            float w = pen.Width;
            int width;
            bool thin;
            if (pen.Unit == 0)
            {
                if (m.Complexity != 0)
                {
                    PointF v = m.VectorTransform(new PointF(w, 0f));
                    w = MathF.Sqrt((v.Y - 0f) * (v.Y - 0f) + (v.X - 0f) * (v.X - 0f));
                }
                width = (int)(w + 0.5f);
                thin = width < 2 && pen.StartCap != 0xff && pen.EndCap != 0xff;
                if (width < 2) width = 1;
            }
            else
            {
                w = DpPen.DeviceWidth(w, pen.Unit, dpi);
                width = (int)(w + 0.5f);
                thin = width < 2;
            }
            if (thin)
            {
                width = 1;
                if ((flags & 8) == 0) { flags |= 0x20; type = 0; }
                else join = 2;
            }
            uint style;
            switch (pen.DashStyle)
            {
                case 0: style = 0; break;
                case 1: style = 1; break;
                case 2: style = 2; break;
                case 3: style = 3; break;
                case 4: style = 4; break;
                default:
                    if ((flags & 1) == 0) return null;
                    style = 0;
                    break;
            }
            var gp = new GdiPen();
            if (type == 0x10000)
            {
                if ((flags & 2) == 0 && style != 0)
                {
                    if ((flags & 1) == 0) return null;
                    style = 0;
                }
                else if (style != 0 && (flags & 2) != 0) return null;
                int cap = pen.StartCap;
                int other = style != 0 ? pen.DashCap : cap;
                if ((cap != pen.EndCap || pen.EndCap != other || other != cap) && (flags & 1) == 0) return null;
                if (cap == 0) style |= 0x200;
                else if (cap == 1) style |= 0x100;
                else if (cap != 2)
                {
                    if ((flags & 1) == 0) return null;
                    style |= 0x200;
                }
                bool miter = false;
                if (join == 1) style |= 0x1000;
                else if (join != 2)
                {
                    if (join != 0 && join != 3 && (flags & 1) == 0) return null;
                    miter = true;
                }
                if (miter)
                {
                    style |= 0x2000;
                    gp.SetMiter = true;
                    gp.Miter = pen.MiterLimit;
                }
            }
            gp.Style = style | type;
            gp.Width = width;
            return gp;
        }
    }

    /// <summary>What ConvertPathToGdi / ConvertRectFToGdi make of a shape: device POINTs and how GDI
    /// is asked to fill them, and the device bounds the PATINVERTs cover.</summary>
    internal sealed class GdiShape
    {
        /// <summary>Rectangles (l, t, r, b quadruples), filled by PatBlt.</summary>
        public int[] Rects;
        /// <summary>A region (l, t, r, b rectangles), filled by FillRgn.</summary>
        public int[] Region;
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
