// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// TextureBrush fills (gdiplus.dll arm64 10.0.26100):
//
//   GpTexture::CreateOutputSpan @18006dd30   device matrix = brush transform x world-to-device; an
//        integer translate with Tile or Clamp copies (DpOutputBilinearSpan_Identity @18006c858),
//        anything else is bilinear whatever the interpolation mode (DpOutputBilinearSpan @1800bfda8,
//        the texture form: the brush's wrap mode, a transparent clamp colour, the whole tile locked
//        as PARGB, and +0.5 prepended under PixelOffsetMode Half/HighQuality)
//   GpTexture::InitializeBrushBitmap @18006ee70   the tile: the image cut to floor(rect + 0.5) as
//        PARGB, or recoloured (GpBitmap::Recolor -> ColorAdjust as Bitmap) when the ImageAttributes
//        has any recolouring; the ImageAttributes constructor takes its wrap mode
//

namespace System.Drawing.WebGpuBackend.Gdip
{
    internal sealed partial class GpGraphics
    {
        GpSpan CreateTextureSpan (TextureBrush tb, GpScan scan)
        {
            GdipFrame tile = tb.TileFrame;
            if (tile == null || tile.Width <= 0 || tile.Height <= 0) return null;
            GpMatrix m = GpMatrix.Multiply (tb.Gp, WorldToDevice);
            DpBitmapSrc src = Lock (tile);
            int wrap = (int) tb.WrapMode;
            if (IsIntegerTranslate (m) && (wrap & ~4) == 0)
                return new GpIdentitySpan (scan, src, m, new DpImageAttr { Wrap = wrap, Clamp = 0 });
            GpMatrix t = m;
            if (_ctx.PixelOffset == Drawing2D.PixelOffsetMode.Half || _ctx.PixelOffset == Drawing2D.PixelOffsetMode.HighQuality)
                t.Translate (0.5f, 0.5f, false);
            GpMatrix inv = GpMatrix.CreateIdentity ();
            if (t.IsInvertible) { inv = t; inv.Invert (); }
            return new GpBilinearSpan (scan, src, inv, wrap);
        }

        /// <summary>The whole frame as premultiplied ARGB pixels (LockBits PARGB).</summary>
        static DpBitmapSrc Lock (GdipFrame f)
        {
            GdipFrame c = f.Format == Imaging.PixelFormat.Format32bppPArgb ? f
                : GdipPixels.Convert (f, new Rectangle (0, 0, f.Width, f.Height), Imaging.PixelFormat.Format32bppPArgb, false);
            var px = new uint [f.Width * f.Height];
            for (int y = 0; y < f.Height; y++) {
                int o = y * c.Stride;
                for (int x = 0; x < f.Width; x++, o += 4)
                    px [y * f.Width + x] = (uint) (c.Bits [o] | c.Bits [o + 1] << 8 | c.Bits [o + 2] << 16 | c.Bits [o + 3] << 24);
            }
            return new DpBitmapSrc (f.Width, f.Height, px);
        }
    }
}
