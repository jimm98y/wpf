// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// DriverMeta's bitmaps (gdiplus.dll 10.0.26100):
//
//   BrushFillUsingBitmap @1800d3728  a brush GDI has no brush for is rendered by GDI+ itself into a
//       32bpp bitmap of the fill's device rectangle (GpBitmap::CreateBitmapAndFillWithBrush
//       @18007ebb0: SourceCopy, the context's interpolation, half-pixel offset, the brush through the
//       world-to-device matrix and moved to the rectangle), the fill's path selected as the clip
//       (ConvertPathToGdi::AndClip), and blitted
//   DrawImage @1800d46b0  an image drawn axis-aligned: its source rectangle (rounded) blitted to the
//       device parallelogram's corners
//   ConvertBitmapToGdi @1800d76d8  the pixels to a DIB: alpha that does not vary by more than 3 is
//       one level (none at all from 0xfc up; nothing drawn below 4), anything else a 1bpp mask dithered
//       against HT_16x16; the colours palettized in the order they appear (black and white first,
//       PaletteSorter) into 1, 4 or 8 bits, or past 256 dithered to 16bpp RGB555
//   ConvertBitmapToGdi::StretchBlt @1800d9e60  HALFTONE twice; a mask as SRCPAINT then the colours
//       SRCAND, a single level as SRCINVERT / mask PatBlt (DPa) / SRCINVERT, either between GDI+'s
//       "TNPP" 0x106 / 0x107 comments (SetSrcCopyOnly @1802242d8); an opaque bitmap SRCCOPY
//

using System.Collections.Generic;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace System.Drawing.WebGpuBackend.Gdip
{
    internal sealed partial class GpMetafileRecorder
    {
        sealed class BitmapToGdi
        {
            public bool Valid;
            public bool Nothing;                 // +0x24: wholly transparent, nothing to draw
            public int Alpha;                    // +0x28: one alpha for every pixel, 0 = opaque
            public int RectX, RectY, RectW, RectH;  // +0x50: the source rectangle asked for
            public int SrcX, SrcY, SrcW, SrcH;   // +0x60: the DIB's part to draw
            public int Bpp;
            public byte[] Bits;
            public byte[] Mask;                  // 1bpp, or null
            public uint[] Palette = new uint[0]; // RGBQUADs

            /// <summary>ConvertBitmapToGdi(hdc, bitmap, rect, 0x108): the ARGB pixels of a
            /// <paramref name="bw"/> x <paramref name="bh"/> bitmap, top row first.</summary>
            public BitmapToGdi(uint[] argb, int bw, int bh, Rectangle rect)
            {
                RectX = rect.X; RectY = rect.Y; RectW = rect.Width; RectH = rect.Height;
                int x0 = Math.Max(rect.X, 0), y0 = Math.Max(rect.Y, 0);
                int x1 = Math.Min(rect.X + rect.Width, bw), y1 = Math.Min(rect.Y + rect.Height, bh);
                if (x1 <= x0 || y1 <= y0) return;
                int w = x1 - x0, h = y1 - y0;
                SrcW = w; SrcH = h;
                // Alpha: one level when it never strays by more than 3.
                uint lo = argb[y0 * bw + x0] >> 24, hi = lo;
                bool uniform = true;
                for (int y = y0; y < y1 && uniform; y++)
                    for (int x = x0; x < x1; x++)
                    {
                        uint a = argb[y * bw + x] >> 24;
                        if (a < lo) { lo = a; if (hi - lo > 3) { uniform = false; break; } }
                        else if (a > hi) { hi = a; if (hi - lo > 3) { uniform = false; break; } }
                    }
                if (uniform)
                {
                    if (hi < 4) { Nothing = true; Valid = true; return; }
                    if (lo < 0xfc) Alpha = (int)((hi + lo + 1) / 2);
                }
                // PaletteSorter: black and white first, then each colour as it appears.
                var index = new Dictionary<uint, int> { [0] = 0, [0xffffff] = 1 };
                var pal = new List<uint> { 0, 0xffffff };
                bool fits = true;
                var rgb = new uint[w * h];      // bottom-up, COLORREF order
                var idx = new byte[w * h];
                int maskStride = ((w + 31) >> 5) << 2;
                byte[] mask = uniform ? null : new byte[maskStride * h];
                for (int r = 0; r < h; r++)
                {
                    int sy = y0 + r, dr = h - 1 - r;
                    int bits = 0;
                    for (int c = 0; c < w; c++)
                    {
                        uint p = argb[sy * bw + x0 + c];
                        uint cref = ((p & 0xff) << 16) | (p & 0xff00) | ((p >> 16) & 0xff);
                        bool on = true;
                        if (!uniform)
                        {
                            on = GpMetafileRecorder.HT16[((x0 + c) & 15) + ((y0 + r) & 15) * 16] < (p >> 24);
                            bits = (bits << 1) | (on ? 1 : 0);
                            if (((c + 1) & 7) == 0) { mask[dr * maskStride + (c >> 3)] = (byte)bits; bits = 0; }
                        }
                        if (!on) { rgb[dr * w + c] = 0xffffff; idx[dr * w + c] = 1; continue; }
                        rgb[dr * w + c] = cref;
                        if (fits)
                        {
                            if (!index.TryGetValue(cref, out int i))
                            {
                                if (pal.Count == 256) { fits = false; continue; }
                                i = pal.Count;
                                index[cref] = i;
                                pal.Add(cref);
                            }
                            idx[dr * w + c] = (byte)i;
                        }
                    }
                    if (!uniform && (w & 7) != 0) mask[dr * maskStride + (w >> 3)] = (byte)(bits << (8 - (w & 7)));
                }
                Mask = mask;
                if (!fits)
                {
                    // Past 256 colours, with flag 8 (every DriverMeta caller passes 0x108): the 24-bit
                    // DIB made a GpBitmap and ConvertTo16BppAndFlip @18007e9f0 draws it upside down
                    // into a 16bpp RGB555 bitmap (SourceCopy, nearest neighbour, half-pixel offset),
                    // which GDI+ dithers (Dither_sRGB_555); its top-down rows are the bottom-up DIB.
                    Bpp = 16;
                    Bits = To16BppAndFlip(rgb, w, h);
                    Palette = new uint[0];
                }
                else
                {
                    int n = pal.Count;
                    // The 8bpp indices, bottom-up in rows of (w + 3) & ~3, packed in place to 1 or
                    // 4 bits when few colours: what a packed row leaves of its 4-byte alignment is
                    // the indices that were there.
                    int stride8 = (w + 3) & ~3;
                    var buf = new byte[stride8 * h];
                    for (int r = 0; r < h; r++) Buffer.BlockCopy(idx, r * w, buf, r * stride8, w);
                    Bpp = 8;
                    if (n < 17)
                    {
                        Bpp = n < 3 ? 1 : 4;
                        int ppb = 8 / Bpp, wr = 0;
                        for (int r = 0; r < h; r++)
                        {
                            int acc = 0, rd = r * stride8, c = 0;
                            while (c < w)
                            {
                                acc = ((acc << Bpp) & 0xff) | buf[rd + c];
                                c++;
                                if (c % ppb == 0) buf[wr++] = (byte)acc;
                            }
                            if (c % ppb != 0)
                            {
                                do { c++; acc = (acc << Bpp) & 0xff; } while (c % ppb != 0);
                                buf[wr] = (byte)acc;
                                do wr++; while ((wr & 3) != 0);
                            }
                            else while ((wr & 3) != 0) wr++;
                        }
                    }
                    int stride = ((w * Bpp + 31) >> 5) << 2;
                    Bits = new byte[stride * h];
                    Buffer.BlockCopy(buf, 0, Bits, 0, Bits.Length);
                    Palette = pal.ToArray();
                }
                Valid = true;
            }

            /// <summary>ConvertTo16BppAndFlip @18007e9f0: <paramref name="rgb"/> is the bottom-up DIB's
            /// COLORREFs; the result is the 16bpp RGB555 rows, bottom row first.</summary>
            static byte[] To16BppAndFlip(uint[] rgb, int w, int h)
            {
                using var src = new Bitmap(w, h, PixelFormat.Format24bppRgb);
                BitmapData sd = src.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
                try
                {
                    var row = new byte[sd.Stride];
                    for (int y = 0; y < h; y++)
                    {
                        int r = h - 1 - y;      // the DIB is bottom-up: image row y is DIB row h-1-y
                        for (int c = 0; c < w; c++)
                        {
                            uint v = rgb[r * w + c];
                            row[c * 3] = (byte)(v >> 16); row[c * 3 + 1] = (byte)(v >> 8); row[c * 3 + 2] = (byte)v;
                        }
                        System.Runtime.InteropServices.Marshal.Copy(row, 0, sd.Scan0 + y * sd.Stride, sd.Stride);
                    }
                }
                finally { src.UnlockBits(sd); }
                using var dst = new Bitmap(w, h, PixelFormat.Format16bppRgb555);
                using (Graphics g = Graphics.FromImage(dst))
                {
                    g.CompositingMode = CompositingMode.SourceCopy;
                    g.InterpolationMode = InterpolationMode.NearestNeighbor;
                    g.PixelOffsetMode = PixelOffsetMode.Half;
                    g.DrawImage(src, new RectangleF(0, h, w, -h), new RectangleF(0, 0, w, h), GraphicsUnit.Pixel);
                }
                int stride = (w * 2 + 3) & ~3;
                var bits = new byte[stride * h];
                BitmapData dd = dst.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format16bppRgb555);
                try
                {
                    for (int y = 0; y < h; y++)
                        System.Runtime.InteropServices.Marshal.Copy(dd.Scan0 + y * dd.Stride, bits, y * stride, w * 2);
                }
                finally { dst.UnlockBits(dd); }
                return bits;
            }

            public byte[] Bmi(bool mask)
            {
                int colors = mask ? 2 : Palette.Length;
                var b = new byte[40 + colors * 4];
                Le.W32(b, 0, 40); Le.W32(b, 4, SrcW); Le.W32(b, 8, SrcH);
                Le.W16(b, 12, 1); Le.W16(b, 14, mask ? 1 : Bpp);
                if (!mask) Le.W32(b, 32, Palette.Length);
                if (mask)
                {
                    uint c0 = Palette.Length > 0 ? Palette[0] : 0;
                    b[40] = (byte)(c0 >> 16); b[41] = (byte)(c0 >> 8); b[42] = (byte)c0;
                    b[44] = 0xff; b[45] = 0xff; b[46] = 0xff;
                }
                else
                    for (int i = 0; i < colors; i++)
                    {
                        uint c = Palette[i];
                        b[40 + i * 4] = (byte)(c >> 16); b[41 + i * 4] = (byte)(c >> 8); b[42 + i * 4] = (byte)c;
                    }
                return b;
            }

            static readonly byte[] TnppStart = { 0x54, 0x4e, 0x50, 0x50, 0x06, 0x01, 0, 0 };
            static readonly byte[] TnppEnd = { 0x54, 0x4e, 0x50, 0x50, 0x07, 0x01, 0, 0 };

            /// <summary>ConvertBitmapToGdi::StretchBlt to the parallelogram's corners (flags 0x108).</summary>
            public void StretchBlt(GpEmfDc dc, int[] p)
            {
                if (SrcH <= 0 || SrcW <= 0 || Nothing) return;
                int x = p[0], y = p[1];
                float fh = (float)(p[5] - y), fw = (float)(p[2] - x);
                int dw = (int)(((float)SrcW / (float)RectW) * fw);
                int dh = (int)(((float)SrcH / (float)RectH) * fh);
                if (RectX < 0) x -= (int)((fw / (float)RectW) * (float)RectX);
                if (RectY < 0) y -= (int)((float)RectY * (fh / (float)RectH));
                dc.SetStretchBltMode(4);
                if (dh == 0 || dw == 0) return;
                dc.SetStretchBltMode(4);
                byte[] bmi = Bmi(false);
                if (Mask != null || Alpha > 0)
                {
                    dc.GdiComment(TnppStart);
                    int rop = 0xcc0020;
                    if (Mask == null)
                    {
                        GpEmfDc.GdiObject saved = GpMetaDriverState.AlphaBrush;
                        uint savedLevel = GpMetaDriverState.AlphaLevel;
                        GpMetaDriverState.AlphaBrush = GpEmfDc.Stock(4);
                        GpMetaDriverState.AlphaLevel = 0xff;
                        GpEmfDc.GdiObject ab = SetAlpha(dc, (uint)Alpha, true, false);
                        dc.StretchDIBits(x, y, dw, dh, SrcX, SrcY, SrcW, SrcH, bmi, Bits, 0, 0x660046);
                        GpEmfDc.GdiObject h = dc.SelectObject(ab);
                        dc.PatBlt(x, y, dw, dh, 0xa000c9);
                        dc.SelectObject(h);
                        rop = 0x660046;
                        dc.StretchDIBits(x, y, dw, dh, SrcX, SrcY, SrcW, SrcH, bmi, Bits, 0, rop);
                        dc.DeleteObject(ab);
                        GpMetaDriverState.AlphaBrush = saved;
                        GpMetaDriverState.AlphaLevel = savedLevel;
                    }
                    else
                    {
                        dc.StretchDIBits(x, y, dw, dh, SrcX, SrcY, SrcW, SrcH, Bmi(true), Mask, 0, 0xee0086);
                        dc.StretchDIBits(x, y, dw, dh, SrcX, SrcY, SrcW, SrcH, bmi, Bits, 0, 0x8800c6);
                    }
                    dc.GdiComment(TnppEnd);
                    return;
                }
                dc.StretchDIBits(x, y, dw, dh, SrcX, SrcY, SrcW, SrcH, bmi, Bits, 0, 0xcc0020);
            }
        }

        static uint[] Argb(Bitmap b)
        {
            var px = new uint[b.Width * b.Height];
            BitmapData d = b.LockBits(new Rectangle(0, 0, b.Width, b.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                var row = new int[b.Width];
                for (int y = 0; y < b.Height; y++)
                {
                    System.Runtime.InteropServices.Marshal.Copy(d.Scan0 + y * d.Stride, row, 0, b.Width);
                    for (int x = 0; x < b.Width; x++) px[y * b.Width + x] = (uint)row[x];
                }
            }
            finally { b.UnlockBits(d); }
            return px;
        }

        /// <summary>DriverMeta::BrushFillUsingBitmap: true when the brush was rendered as a bitmap.</summary>
        bool BrushFillUsingBitmap(Rectangle rect, Brush brush, PathToGdi clip)
        {
            if (rect.Width <= 0 || rect.Height <= 0) return false;
            if (brush is LinearGradientBrush lg)
            {
                int special = SpecialGradientType(lg);
                if (special == 1 || special == 2) return GradientBands(rect, lg, special == 2, clip);
            }
            int w = rect.Width, h = rect.Height;
            if (w > 0x200 || h > 0x200)
            {
                // AdjustForMaximumSize: the longer side to 1024, the other in proportion (at least 512).
                if (w < h) AdjustForMaximumSize(ref h, ref w); else AdjustForMaximumSize(ref w, ref h);
            }
            GpMatrix m = DeviceMatrix;
            bool half = _state.PixelOffset == PixelOffsetMode.HighQuality || _state.PixelOffset == PixelOffsetMode.Half;
            if (half) { m.Dx += 0.5f; m.Dy += 0.5f; m.Complexity |= 1; }
            if (w != rect.Width || h != rect.Height)
            {
                m.Dx -= rect.X; m.Dy -= rect.Y;
                m.Scale((float)w / rect.Width, (float)h / rect.Height, true);
                m.Dx += rect.X; m.Dy += rect.Y;
            }
            uint[] px;
            using (var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.CompositingMode = CompositingMode.SourceCopy;
                    g.InterpolationMode = (InterpolationMode)_state.Interp;
                    g.PixelOffsetMode = PixelOffsetMode.Half;
                    // The brush through the world-to-device matrix, moved to the rectangle: the world
                    // transform carries it, the fill covers the bitmap.
                    var t = new Matrix(m.M11, m.M12, m.M21, m.M22, m.Dx - rect.X, m.Dy - rect.Y);
                    if (!t.IsInvertible) return false;
                    g.Transform = t;
                    var inv = t.Clone();
                    inv.Invert();
                    var corners = new[] { new PointF(0, 0), new PointF(w, 0), new PointF(w, h), new PointF(0, h) };
                    inv.TransformPoints(corners);
                    g.FillPolygon(brush, corners);
                }
                px = Argb(bmp);
            }
            GpEmfDc dc = ContextHdc();
            bool saved = SetupClipping(dc, rect);
            if (clip != null)
            {
                if (!saved) { dc.SaveDC(); saved = true; }
                clip.AndClip(dc, null);
            }
            var cb = new BitmapToGdi(px, w, h, new Rectangle(0, 0, w, h));
            if (cb.Valid)
                cb.StretchBlt(dc, new[] { rect.X, rect.Y, rect.X + rect.Width, rect.Y, rect.X, rect.Y + rect.Height });
            RestoreClipping(dc, saved);
            return cb.Valid;
        }

        /// <summary>GpLineGradient::GetSpecialGradientType @18006eca0: the brush's transform through
        /// world to device; 2 when it neither rotates nor shears (the colour changes along x only),
        /// 1 when it is a quarter turn (along y only), 3 otherwise.</summary>
        int SpecialGradientType(LinearGradientBrush lg)
        {
            GpMatrix m = GpMatrix.Multiply(lg.Xform, DeviceMatrix);
            if ((m.Complexity & ~3) == 0) return 2;
            const float eps = 0.000596046447753906f;
            if (eps <= Math.Abs(m.M11) || Math.Abs(m.M12) < eps || Math.Abs(m.M21) < eps) return 3;
            return eps <= Math.Abs(m.M22) ? 3 : 1;
        }

        /// <summary>DriverMeta::BrushFillUsingBitmap's gradient bands: the brush rendered into one row
        /// (or column) of the rectangle (CreateBitmapAndFillWithBrush under world to device, the
        /// context's interpolation and pixel offset), cut into runs whose channels stay within one
        /// (two past 128 pixels) of the run's first pixel, each run a rectangle filled with a solid
        /// brush (alpha through the mask brush) under the null pen; the last run takes the last
        /// pixel's colour.</summary>
        bool GradientBands(Rectangle rect, LinearGradientBrush brush, bool horizontal, PathToGdi clip)
        {
            int w = horizontal ? rect.Width : 1, h = horizontal ? 1 : rect.Height;
            GpMatrix m = DeviceMatrix;
            uint[] px;
            using (var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.CompositingMode = CompositingMode.SourceCopy;
                    g.InterpolationMode = (InterpolationMode)_state.Interp;
                    g.PixelOffsetMode = _state.PixelOffset;
                    var t = new Matrix(m.M11, m.M12, m.M21, m.M22, m.Dx - rect.X, m.Dy - rect.Y);
                    if (!t.IsInvertible) return false;
                    g.Transform = t;
                    var inv = t.Clone();
                    inv.Invert();
                    var corners = new[] { new PointF(0, 0), new PointF(w, 0), new PointF(w, h), new PointF(0, h) };
                    inv.TransformPoints(corners);
                    g.FillPolygon(brush, corners);
                }
                px = Argb(bmp);
            }
            GpEmfDc dc = ContextHdc();
            bool saved = SetupClipping(dc, rect);
            if (clip != null)
            {
                if (!saved) { dc.SaveDC(); saved = true; }
                clip.AndClip(dc, null);
            }
            GpEmfDc.GdiObject oldPen = dc.SelectObject(GpEmfDc.Stock(8));
            int n = horizontal ? w : h;
            int thr = (horizontal ? rect.Width : rect.Height) < 0x81 ? 1 : 2;
            uint refc = px[0];
            int start = 0;
            uint last = 0;
            for (int i = 1; i <= n; i++)
            {
                uint c;
                if (i == n) c = last;
                else
                {
                    uint p = px[i];
                    last = p;
                    bool brk = Diff(p >> 16, refc >> 16) >= thr || Diff(p >> 8, refc >> 8) >= thr
                        || Diff(p, refc) >= thr || Diff(p >> 24, refc >> 24) >= thr;
                    if (!brk) continue;
                    c = refc;
                    refc = p;
                }
                uint a = c >> 24;
                if (a > 1)
                {
                    RectangleF band = horizontal
                        ? new RectangleF(rect.X + start, rect.Y, i - start, rect.Height)
                        : new RectangleF(rect.X, rect.Y + start, rect.Width, i - start);
                    GpEmfDc.GdiObject hb = GpEmfDc.CreateSolidBrush(((c & 0xff) << 16) | (c & 0xff00) | ((c >> 16) & 0xff));
                    var cr = new RectFToGdi(new[] { band }, GpMatrix.CreateIdentity(), null);
                    if (a < 0xfe) cr.AlphaFill(dc, hb, SetAlpha(dc, a, false, false));
                    else cr.Fill(dc, hb, 0xf00021, true);
                    dc.DeleteObject(hb);
                }
                start = i;
            }
            dc.SelectObject(oldPen);
            RestoreClipping(dc, saved);
            return true;

            static int Diff(uint x, uint y) => Math.Abs((int)(x & 0xff) - (int)(y & 0xff));
        }

        /// <summary>AdjustForMaximumSize @1800d36b0.</summary>
        static void AdjustForMaximumSize(ref int big, ref int small)
        {
            int b = big;
            big = 0x200;
            if (small > 0x100)
            {
                small = Floor((float)small * (512f / (float)b) + 0.5f);
                if (small < 0x100) small = 0x100;
            }
        }

        /// <summary>GpGraphics::DrawImage down-level: an image drawn without rotation is DriverMeta's
        /// blit of its source rectangle; anything else GDI+ renders as a texture-filled parallelogram.</summary>
        void GdiDrawImage(Image image, RectangleF src, GpMat m, ImageAttributes ia)
        {
            if (!Gdi || !(image is Bitmap bitmap)) return;
            var pts = new[] { new PointF(src.X, src.Y), new PointF(src.Width + src.X, src.Y), new PointF(src.X, src.Height + src.Y) };
            m.Transform(pts);
            GpMat full = GpMat.Multiply(m, WorldToDevice);
            float l = src.X, t = src.Y, r = src.X + src.Width, b = src.Y + src.Height;
            full.TransformBounds(ref l, ref t, ref r, ref b);
            Rectangle? draw = DrawRect(Finish(l, t, r, b));
            if (!draw.HasValue || TotallyClipped(draw.Value)) return;
            lock (GpMetaDriverState.Lock)
            {
                if ((full.Complexity & ~3) != 0) { DrawImageRotated(draw.Value, bitmap, pts, src, full); return; }
                DriverDrawImage(draw.Value, bitmap, pts, src, DeviceMatrix);
            }
        }

        /// <summary>GpGraphics::DrvDrawImage @180011828 on the metafile driver when the source to device
        /// matrix rotates or shears: each of the image's axes scaled so that neither of its components
        /// is more than a device pixel per image pixel, the image drawn under that into a 32bpp ARGB
        /// bitmap of the result's bounds (+1, rounded; the context's pixel offset, a fresh ImageAttributes
        /// wrapping TileFlipXY), the clip intersected with the destination parallelogram (recorded as
        /// EmfPlusSetClipPath, and put back as EmfPlusSetClipRegion), and the bitmap drawn axis-aligned
        /// over the parallelogram's device bounds under an identity world to device.</summary>
        void DrawImageRotated(Rectangle draw, Bitmap image, PointF[] pts, RectangleF src, GpMat full)
        {
            float a = Math.Max(Math.Abs(full.M11), Math.Abs(full.M12));
            float f1 = a > 1f ? 1f / a : 1f;
            a = Math.Max(Math.Abs(full.M21), Math.Abs(full.M22));
            float f2 = a > 1f ? 1f / a : 1f;
            var m2 = new GpMat(f1 * full.M11, f1 * full.M12, f2 * full.M21, f2 * full.M22, full.Dx, full.Dy);
            float l = src.X, t = src.Y, r = src.X + src.Width, b = src.Y + src.Height;
            m2.TransformBounds(ref l, ref t, ref r, ref b);
            int w = (int)((r - l) + 1f + 0.5f), h = (int)((b - t) + 1f + 0.5f);
            float dl = src.X, dt = src.Y, dr = src.X + src.Width, db = src.Y + src.Height;
            full.TransformBounds(ref dl, ref dt, ref dr, ref db);
            if (w <= 0 || h <= 0) return;
            uint[] px;
            using (var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.Transparent);
                    g.TranslateTransform(-l, -t);
                    g.MultiplyTransform(new Matrix(m2.M11, m2.M12, m2.M21, m2.M22, m2.Dx, m2.Dy));
                    g.PixelOffsetMode = _state.PixelOffset;
                    using var ia = new ImageAttributes();
                    ia.SetWrapMode(WrapMode.TileFlipXY);
                    g.DrawImage(image, new[] { new PointF(src.X, src.Y), new PointF(src.Right, src.Y), new PointF(src.X, src.Bottom) }, src, GraphicsUnit.Pixel, ia);
                }
                px = Argb(bmp);
            }
            // CombineClip(parallelogram, Intersect): a device half pixel back when the pixel offset is half.
            float ox = 0f, oy = 0f;
            if (_state.PixelOffset == PixelOffsetMode.HighQuality || _state.PixelOffset == PixelOffsetMode.Half)
            {
                ox = -0.5f; oy = -0.5f;
                GpMatrix inv = DeviceMatrix;
                if (inv.Complexity != 0 && inv.Invert())
                {
                    ox = inv.M11 * -0.5f + inv.M21 * -0.5f;
                    oy = inv.M12 * -0.5f + inv.M22 * -0.5f;
                }
            }
            var quad = new[]
            {
                new PointF(pts[0].X + ox, pts[0].Y + oy), new PointF(pts[1].X + ox, pts[1].Y + oy),
                new PointF(pts[1].X - pts[0].X + pts[2].X + ox, pts[2].Y - pts[0].Y + pts[1].Y + oy),
                new PointF(pts[2].X + ox, pts[2].Y + oy),
            };
            GpRegion savedClip = _state.Clip?.Clone();
            using (var clipPath = new GraphicsPath(FillMode.Alternate))
            {
                clipPath.AddPolygon(quad);
                RecordClipPath(clipPath, CombineMode.Intersect);
                CombineClip(GpRegion.FromPath(clipPath.gp.PointArray(), clipPath.gp.TypeArray(), clipPath.gp.FillMode), CombineMode.Intersect);
            }
            var dpts = new[] { new PointF(dl, dt), new PointF(dr, dt), new PointF(dl, db) };
            DriverDrawImage(draw, px, w, h, dpts, new RectangleF(0, 0, w, h), GpMatrix.CreateIdentity());
            // SetClip(the saved clip, Replace): the clip GetClip gave back, in world space.
            using (Region back = savedClip == null ? new Region() : WorldClip(savedClip))
                RecordClipRegion(back, CombineMode.Replace);
            _state.Clip = savedClip;
        }

        /// <summary>DriverMeta::DrawImage @1800d46b0.</summary>
        void DriverDrawImage(Rectangle draw, Bitmap image, PointF[] pts, RectangleF src, GpMatrix w2d)
            => DriverDrawImage(draw, null, image.Width, image.Height, pts, src, w2d, image);

        void DriverDrawImage(Rectangle draw, uint[] argb, int bw, int bh, PointF[] pts, RectangleF src, GpMatrix w2d, Bitmap image = null)
        {
            if ((w2d.Complexity & ~3) != 0 || pts[0].X != pts[2].X || pts[0].Y != pts[1].Y)
            {
                if (image != null) DrawImageAsTexture(draw, image, pts, src);
                return;
            }
            var p = new int[6];
            for (int i = 0; i < 3; i++) { w2d.TransformFix(pts[i], out int x, out int y); p[i * 2] = x; p[i * 2 + 1] = y; }
            GpEmfDc dc = ContextHdc();
            float sx = src.X, sw = src.Width, sy = src.Y, sh = src.Height;
            if (sw < 0f) { sx += sw; sw = -sw; (p[0], p[2]) = (p[2], p[0]); }
            if (sh < 0f) { sy += sh; sh = -sh; (p[1], p[5]) = (p[5], p[1]); }
            int ih = Floor(sh + 0.5f), iw = Floor(sw + 0.5f), iy = Floor(sy + 0.5f), ix = Floor(sx + 0.5f);
            if (iw < 1 || ih < 1) return;
            var cb = new BitmapToGdi(argb ?? Argb(image), bw, bh, new Rectangle(ix, iy, iw, ih));
            if (!cb.Valid || cb.Nothing) return;
            int minx = Math.Min(p[0], p[2]), maxx = Math.Max(p[0], p[2]);
            int miny = Math.Min(p[1], p[5]), maxy = Math.Max(p[1], p[5]);
            bool saved = SetupClipping(dc, new Rectangle(minx, miny, maxx - minx + 1, maxy - miny + 1));
            cb.StretchBlt(dc, p);
            RestoreClipping(dc, saved);
        }

        /// <summary>A rotated or skewed image: GDI+ fills the parallelogram with the image as a texture.</summary>
        void DrawImageAsTexture(Rectangle draw, Bitmap image, PointF[] pts, RectangleF src)
        {
            if (!GpMat.InferAffine(pts, src, out GpMat im)) return;
            using (var tb = new TextureBrush(image, WrapMode.Clamp, Rectangle.Round(src)))
            {
                tb.Transform = new Matrix(im.M11, im.M12, im.M21, im.M22, im.M11 * src.X + im.M21 * src.Y + im.Dx, im.M12 * src.X + im.M22 * src.Y + im.Dy);
                var path = new GpPath(System.Drawing.Drawing2D.FillMode.Alternate);
                path.AddPolygon(new[] { pts[0], pts[1], new PointF(pts[1].X + pts[2].X - pts[0].X, pts[1].Y + pts[2].Y - pts[0].Y), pts[2] }, 4);
                var cp = new PathToGdi(path, DeviceMatrix, 0x10, null);
                if (!cp.Valid || cp.IsEmpty) return;
                BrushFillUsingBitmap(new Rectangle(cp.X, cp.Y, cp.W, cp.H), tb, cp);
            }
        }
    }
}
