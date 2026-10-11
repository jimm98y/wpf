// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// How GDI+ (gdiplus.dll 10.0.26100) plays a plain EMF or WMF onto a Graphics: not onto the target
// itself but through GDI into a 32bpp DIB of the destination's device size (GpGraphics::EnumEmf
// @180091520 with its DIB branch):
//
//   CreateDibSection32Bpp @180090b50   the destination's corners under world to device, each side
//                                      its length rounded (AdjustForMaximumSize past 1024)
//   Init32BppDibToTransparent          every pixel 0xAA0D0B0C
//   EnumerateEmfRecords(.., 0)         MfEnumState: GDI plays the records into the DIB, the frame
//                                      onto (0, 0, w, h); raster operations meet the DIB's pixels
//   Draw32BppDib @180090f58            a pixel still 0xAA0D0B0C (only its RGB once a raster
//                                      operation was used) is transparent, any other opaque; the
//                                      DIB drawn as PARGB onto the destination grown by a device
//                                      pixel each side from (-1, -1, w + 2, h + 2), nearest
//                                      neighbour taken as bilinear (EnumEmf)
//   MfEnumState::OutputDIB @1800b7eb0  a colour DIB of a BitBlt / StretchBlt / StretchDIBits record
//                                      is first stretched by GDI+ (SourceCopy, the playback's
//                                      interpolation, TileFlipXY, from the source less one pixel
//                                      each way) into a 24bpp bitmap of its own device size (LPtoDP
//                                      of the origin and the origin plus the absolute extents,
//                                      GetIntDistance); GDI then stretches that onto the destination
//                                      as BLTRECORD::bOrderStupid orders it (an inverted side moved
//                                      on by one), COLORONCOLOR: the source mirrored first, then
//                                      stretch::vInitStrDDA's DDA. A 1bpp one is stretched by GDI
//
// The raster operations here are GDI's: each ROP3 / ROP2 a truth table over the pattern, source and
// destination bits.
//

using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace System.Drawing.WebGpuBackend.Gdip
{
    internal sealed partial class GpGdiPlayer
    {
        const uint Transparent = 0xAA0D0B0C;

        Bitmap _canvas;                  // the DIB GDI plays into; null when playing on the target
        Graphics _target;                // the Graphics the playback is for
        PointF[] _canvasDest;            // the destination's device corners
        int _cw, _ch;
        bool _ropUsed;                   // MfEnumState +0xd4
        int _srcBpp;                     // the bit count of the DIB the blit in hand came from
        int _gpW, _gpH;                  // OutputDIB's own device size of a StretchDIBits destination
        InterpolationMode _interp;       // the playback's interpolation (MetafilePlayer +0x145c)

        bool Canvas => _canvas != null;

        // The DIB of the blit in hand as GDI gets it (header + colours, bits), for HALFTONE.
        byte[] _htInfo, _htBits;
        int _htBitsAt;

        /// <summary>CreateDibSection32Bpp + Init32BppDibToTransparent: the DIB for the destination
        /// whose device corners are <paramref name="p"/>; false when it has no area.</summary>
        bool StartCanvas(PointF[] p)
        {
            static float Dist(PointF a, PointF b) => MathF.Sqrt((b.Y - a.Y) * (b.Y - a.Y) + (b.X - a.X) * (b.X - a.X));
            int w = (int)(Dist(p[0], p[1]) + 0.5f), h = (int)(Dist(p[0], p[2]) + 0.5f);
            if (w == 0 || h == 0) return false;
            if (w > 0x400 || h > 0x400)
            {
                if (h <= w) AdjustForMaximumSize(ref w, ref h); else AdjustForMaximumSize(ref h, ref w);
            }
            _interp = _t.InterpolationMode;
            _canvas = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            BitmapData bd = _canvas.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                var row = new int[w];
                for (int i = 0; i < w; i++) row[i] = unchecked((int)Transparent);
                for (int y = 0; y < h; y++) System.Runtime.InteropServices.Marshal.Copy(row, 0, bd.Scan0 + y * bd.Stride, w);
            }
            finally { _canvas.UnlockBits(bd); }
            _cw = w; _ch = h;
            _canvasDest = p;
            _target = _t;
            _t = Graphics.FromImage(_canvas);
            return true;
        }

        bool _direct;                    // the DIB stands for the target's own HDC (device space)

        /// <summary>EnumEmf playing on the target's HDC: a DIB over the target's device space from
        /// its origin to past the destination's corners, which GDI draws into as it would into the
        /// target (the DC's clip is the playback's), copied one to one at the end.</summary>
        bool StartDirectCanvas(PointF[] p)
        {
            float mx = Math.Max(Math.Max(p[0].X, p[1].X), Math.Max(p[2].X, p[1].X + p[2].X - p[0].X));
            float my = Math.Max(Math.Max(p[0].Y, p[1].Y), Math.Max(p[2].Y, p[1].Y + p[2].Y - p[0].Y));
            if (!(mx > 0f) || !(my > 0f) || mx > 0x4000 || my > 0x4000) return false;
            int w = (int)MathF.Ceiling(mx) + 2, h = (int)MathF.Ceiling(my) + 2;
            _interp = _t.InterpolationMode;
            _canvas = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            BitmapData bd = _canvas.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                var row = new int[w];
                for (int i = 0; i < w; i++) row[i] = unchecked((int)Transparent);
                for (int y = 0; y < h; y++) System.Runtime.InteropServices.Marshal.Copy(row, 0, bd.Scan0 + y * bd.Stride, w);
            }
            finally { _canvas.UnlockBits(bd); }
            _cw = w; _ch = h;
            _canvasDest = p;
            _target = _t;
            _t = Graphics.FromImage(_canvas);
            _direct = true;
            return true;
        }

        /// <summary>Gives the DIB up before anything was drawn (nothing to play).</summary>
        void AbortCanvas()
        {
            if (_canvas == null) return;
            _t.Dispose();
            _t = _target;
            _canvas.Dispose();
            _canvas = null;
            _direct = false;
        }

        static void AdjustForMaximumSize(ref int big, ref int small)
        {
            int b = big;
            big = 0x200;
            if (small > 0x100)
            {
                small = (int)MathF.Floor((float)small * (512f / (float)b) + 0.5f);
                if (small < 0x100) small = 0x100;
            }
        }

        /// <summary>Draw32BppDib: the DIB's untouched pixels transparent, the rest opaque, drawn over
        /// the destination.</summary>
        public void Finish()
        {
            if (_canvas == null) return;
            _t.Dispose();
            _t = _target;
            Bitmap c = _canvas;
            _canvas = null;
            using (c)
            using (var pargb = new Bitmap(_cw, _ch, PixelFormat.Format32bppArgb))
            {
                BitmapData sd = c.LockBits(new Rectangle(0, 0, _cw, _ch), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                BitmapData dd = pargb.LockBits(new Rectangle(0, 0, _cw, _ch), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                try
                {
                    var row = new int[_cw];
                    for (int y = 0; y < _ch; y++)
                    {
                        System.Runtime.InteropServices.Marshal.Copy(sd.Scan0 + y * sd.Stride, row, 0, _cw);
                        for (int x = 0; x < _cw; x++)
                        {
                            uint v = (uint)row[x];
                            bool clear = _ropUsed ? (v & 0xffffff) == (Transparent & 0xffffff) : v == Transparent;
                            row[x] = clear ? 0 : unchecked((int)(v | 0xff000000));
                        }
                        System.Runtime.InteropServices.Marshal.Copy(row, 0, dd.Scan0 + y * dd.Stride, _cw);
                    }
                }
                finally { c.UnlockBits(sd); pargb.UnlockBits(dd); }
                if (_direct)
                {
                    // GDI drew on the target itself: what it touched, one to one, inside the clip
                    // SetupClippingForMetafilePlayback gave the DC.
                    _direct = false;
                    Graphics g = _t;
                    g.ResetTransform();
                    g.PageUnit = GraphicsUnit.Pixel;
                    g.PageScale = 1f;
                    if (_s.BaseClip == null) g.ResetClip(); else g.Clip = _s.BaseClip;
                    InterpolationMode oim = g.InterpolationMode;
                    PixelOffsetMode opm = g.PixelOffsetMode;
                    CompositingMode ocm = g.CompositingMode;
                    g.InterpolationMode = InterpolationMode.NearestNeighbor;
                    g.PixelOffsetMode = PixelOffsetMode.Half;
                    g.CompositingMode = CompositingMode.SourceOver;
                    g.DrawImage(pargb, new Rectangle(0, 0, _cw, _ch), 0, 0, _cw, _ch, GraphicsUnit.Pixel);
                    g.InterpolationMode = oim;
                    g.PixelOffsetMode = opm;
                    g.CompositingMode = ocm;
                    return;
                }
                // Draw32BppDib @180090f58: the destination rectangle (world units) grown by a device
                // pixel's world size each side (GpGraphics::GetWorldPixelSize @1800dabf0: the device
                // vector (1, 1) back through the inverse transform, each part's absolute value),
                // through the world transform.
                GpMat wd = _s.EmfWorldToDevice;
                RectangleF dr = _s.EmfDest;
                float det = wd.M11 * wd.M22 - wd.M12 * wd.M21;
                float pxw = 1f, pxh = 1f;
                if (det != 0f)
                {
                    pxw = MathF.Abs((wd.M22 - wd.M21) / det);
                    pxh = MathF.Abs((wd.M11 - wd.M12) / det);
                }
                if (dr.Width < 0f) pxw = -pxw;
                if (dr.Height < 0f) pxh = -pxh;
                float gx = dr.X - pxw, gy = dr.Y - pxh, gw = dr.Width + pxw * 2f, gh = dr.Height + pxh * 2f;
                Graphics t = _t;
                t.ResetTransform();
                t.PageUnit = GraphicsUnit.Pixel;
                t.PageScale = 1f;
                if (_s.BaseClip == null) t.ResetClip(); else t.Clip = _s.BaseClip;
                InterpolationMode im = _interp == InterpolationMode.NearestNeighbor ? InterpolationMode.Bilinear : _interp;
                InterpolationMode oldIm = t.InterpolationMode;
                t.InterpolationMode = im;
                // GpGraphics::DrawImage of the rectangle under the world transform
                using (Matrix wm = wd.ToMatrix()) t.Transform = wm;
                t.DrawImage(pargb, new RectangleF(gx, gy, gw, gh), new RectangleF(-1, -1, _cw + 2, _ch + 2), GraphicsUnit.Pixel);
                t.ResetTransform();
                t.InterpolationMode = oldIm;
            }

            static PointF Unit(PointF a, PointF b)
            {
                float dx = b.X - a.X, dy = b.Y - a.Y, l = MathF.Sqrt(dx * dx + dy * dy);
                return l > 0 ? new PointF(dx / l, dy / l) : new PointF(0, 0);
            }
        }

        // ---- the raster operations ------------------------------------------------------------------

        static uint Rop3(int index, uint p, uint s, uint d)
        {
            uint r = 0;
            for (int c = 0; c < 8; c++)
            {
                if (((index >> c) & 1) == 0) continue;
                r |= ((c & 4) != 0 ? p : ~p) & ((c & 2) != 0 ? s : ~s) & ((c & 1) != 0 ? d : ~d);
            }
            return r;
        }

        static uint Rop2(int code, uint p, uint d)
        {
            int t = code - 1;
            uint r = 0;
            for (int c = 0; c < 4; c++)
            {
                if (((t >> c) & 1) == 0) continue;
                r |= ((c & 2) != 0 ? p : ~p) & ((c & 1) != 0 ? d : ~d);
            }
            return r;
        }

        static bool UsesPattern(int rop) => (((rop >> 4) ^ rop) & 0x0f0000) != 0;
        static bool UsesSource(int rop) => (((rop >> 2) ^ rop) & 0x330000) != 0;

        /// <summary>The device clip (the DC's clip under its meta region) as a per-pixel test over
        /// the DIB; null when nothing clips.</summary>
        bool[] ClipMask()
        {
            if (Gdi)
            {
                GdiRgn c = GdiClip();
                if (c == null) return null;
                var m = new bool[_cw * _ch];
                foreach (var q in c.Rects())
                    for (int y = Math.Max(0, q.T); y < Math.Min(_ch, q.B); y++)
                        for (int x = Math.Max(0, q.L); x < Math.Min(_cw, q.R); x++) m[y * _cw + x] = true;
                return m;
            }
            Region vis = null;
            if (_dc.Clip != null) vis = _dc.Clip.Clone();
            if (_dc.MetaClip != null) { if (vis == null) vis = _dc.MetaClip.Clone(); else vis.Intersect(_dc.MetaClip); }
            if (vis == null) return null;
            var mask = new bool[_cw * _ch];
            using (vis)
            using (var id = new Matrix())
                foreach (RectangleF r in vis.GetRegionScans(id))
                {
                    int x0 = Math.Max(0, (int)MathF.Round(r.Left)), x1 = Math.Min(_cw, (int)MathF.Round(r.Right));
                    int y0 = Math.Max(0, (int)MathF.Round(r.Top)), y1 = Math.Min(_ch, (int)MathF.Round(r.Bottom));
                    for (int y = y0; y < y1; y++)
                        for (int x = x0; x < x1; x++) mask[y * _cw + x] = true;
                }
            return mask;
        }

        /// <summary>The current brush as GDI realizes it: a colour, or a pattern tiled from the brush
        /// origin in device pixels; null for a hollow brush.</summary>
        Func<int, int, uint> PatternOf(bool transparentBk = false)
        {
            GdiBrush b = _dc.Brush;
            if (b == null || b.Style == 1) return null;
            if (b.Style == 0) { uint c = Rgb(b.Color); return (x, y) => c; }
            if (b.Style == 2 && b.Hatch >= 0 && b.Hatch < 6)
            {
                // win32k's hatch bitmaps (PatBlt of each HS_ style), the hatch colour on its ones,
                // the background colour on its zeros -- or nothing there in TRANSPARENT mode.
                byte[] bits = s_gdiHatch[b.Hatch];
                uint fg = Rgb(b.Color), bg = transparentBk && _dc.BkMode == 1 ? NoPaint : Rgb(_dc.BkColor);
                int hx = (int)MathF.Round(_base.Dx) + _dc.BrushOrg.X + WmfPatternX, hy = (int)MathF.Round(_base.Dy) + _dc.BrushOrg.Y + WmfPatternY;
                return (x, y) => (bits[(((y - hy) % 8) + 8) % 8] & (0x80 >> ((((x - hx) % 8) + 8) % 8))) != 0 ? fg : bg;
            }
            Bitmap pat;
            bool own = false;
            if (b.Style == 2)
            {
                own = true;
                pat = new Bitmap(8, 8, PixelFormat.Format32bppArgb);
                using (Graphics g = Graphics.FromImage(pat))
                using (var hb = new HatchBrush(HatchOf(b.Hatch), b.Color, _dc.BkColor))
                    g.FillRectangle(hb, 0, 0, 8, 8);
            }
            else if (b.Pattern == null) { uint c = Rgb(b.Color); return (x, y) => c; }
            else if (b.Mono) { own = true; pat = Recolor(b.Pattern, _dc.TextColor, _dc.BkColor); }
            else pat = b.Pattern;
            int w = pat.Width, h = pat.Height;
            var px = new uint[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++) px[y * w + x] = Rgb(pat.GetPixel(x, y));
            if (own) pat.Dispose();
            int ox = (int)MathF.Round(_base.Dx) + _dc.BrushOrg.X + WmfPatternX, oy = (int)MathF.Round(_base.Dy) + _dc.BrushOrg.Y + WmfPatternY;
            return (x, y) => px[(((y - oy) % h + h) % h) * w + ((x - ox) % w + w) % w];
        }

        // GpMetafile::EnumerateForPlayback @180092348 hands GpGraphics::EnumEmf a WMF whose world
        // transform only translates and scales up-right to play on the destination's own HDC
        // (EnumEmf's non-DIB branch): GDI's patterns and SelectClipRgn's device units are then the
        // destination's pixels, the playback's lying at its viewport origin. Any other transform
        // plays into the 32bpp DIB, whose pixel 0 is the origin.
        bool WmfOnDestination
        {
            get
            {
                if (!_wmfCanvas) return false;
                GpMat w = _s.EmfWorldToDevice;
                return w.M12 == 0f && w.M21 == 0f && w.M11 >= 0f && w.M22 >= 0f;
            }
        }

        int WmfPatternX => WmfOnDestination ? -(int)MathF.Floor(_canvasDest[0].X + 0.5f) : 0;
        int WmfPatternY => WmfOnDestination ? -(int)MathF.Floor(_canvasDest[0].Y + 0.5f) : 0;

        /// <summary>A pattern pixel that is not painted (a hatch's background in TRANSPARENT mode).</summary>
        const uint NoPaint = 0xffffffff;

        static readonly byte[][] s_gdiHatch =
        {
            new byte[] { 0x00, 0x00, 0x00, 0xff, 0x00, 0x00, 0x00, 0x00 },   // HS_HORIZONTAL
            new byte[] { 0x08, 0x08, 0x08, 0x08, 0x08, 0x08, 0x08, 0x08 },   // HS_VERTICAL
            new byte[] { 0x80, 0x40, 0x20, 0x10, 0x08, 0x04, 0x02, 0x01 },   // HS_FDIAGONAL
            new byte[] { 0x01, 0x02, 0x04, 0x08, 0x10, 0x20, 0x40, 0x80 },   // HS_BDIAGONAL
            new byte[] { 0x08, 0x08, 0x08, 0xff, 0x08, 0x08, 0x08, 0x08 },   // HS_CROSS
            new byte[] { 0x81, 0x42, 0x24, 0x18, 0x18, 0x24, 0x42, 0x81 },   // HS_DIAGCROSS
        };

        static HatchStyle HatchOf(int hatch)
        {
            switch (hatch)
            {
                case 0: return HatchStyle.Horizontal;
                case 1: return HatchStyle.Vertical;
                case 2: return HatchStyle.ForwardDiagonal;
                case 3: return HatchStyle.BackwardDiagonal;
                case 4: return HatchStyle.Cross;
                default: return HatchStyle.DiagonalCross;
            }
        }

        static uint Rgb(Color c) => (uint)(c.R << 16 | c.G << 8 | c.B);

        static int Round(float v) => (int)MathF.Floor(v + 0.5f);

        /// <summary>BitBlt / StretchBlt / StretchDIBits / PatBlt into the DIB: the destination's device
        /// rectangle, each pixel the ROP3 of the brush, the source and the DIB.</summary>
        bool RasterBlit(Bitmap bm, PointF[] d, RectangleF src, int rop, bool dib)
        {
            if ((rop & 0xffff0000) == 0x00AA0000) return true;   // DSTCOPY
            if (d[0].Y != d[1].Y || d[0].X != d[2].X) return false;
            uint rr = (uint)rop & 0x00ff00ff;
            if (rr != 0xcc0020 && rr != 0x330008 && rr != 0xf00021 && rr != 0x000042 && rr != 0xff0062) _ropUsed = true;
            int index = (rop >> 16) & 0xff;
            int x0 = Round(d[0].X), x1 = Round(d[1].X), y0 = Round(d[0].Y), y1 = Round(d[2].Y);
            bool mirrorX = x1 < x0, mirrorY = y1 < y0;
            // BLTRECORD::bOrderStupid @1402e4798 (BLTRECORD::bStretch @14017cba0): an inverted
            // side is swapped and both its ends moved on by one, so the mirrored rectangle covers
            // (x1, x0] rather than [x1, x0).
            int left = mirrorX ? x1 + 1 : x0, top = mirrorY ? y1 + 1 : y0;
            int dw = Math.Abs(x1 - x0), dh = Math.Abs(y1 - y0);
            if (dw == 0 || dh == 0) return true;
            Func<int, int, uint> pat = null;
            if (UsesPattern(rop))
            {
                pat = PatternOf();
                if (pat == null) return true;
            }
            uint[] spx = null;
            int sw = 0, sh = 0;
            Func<int, int, uint> source = null;
            Bitmap pre = null;
            if (UsesSource(rop))
            {
                if (bm == null) return true;
                float fsw = Math.Abs(src.Width), fsh = Math.Abs(src.Height);
                int gw = _gpW, gh = _gpH;
                bool stretch = gw != (int)fsw || gh != (int)fsh;
                if (dib && stretch && (_srcBpp != 1 || rr == 0xcc0020) && _interp != InterpolationMode.NearestNeighbor
                    && fsw > 1 && fsh > 1 && gw * gh < 0x800000 && gw > 0 && gh > 0)
                {
                    // MfEnumState::OutputDIB: GDI+ stretches the colours first, into a 24bpp bitmap
                    // of its own device size (SourceCopy, the playback's interpolation, TileFlipXY,
                    // the source less one pixel each way); GDI then stretches that onto the
                    // destination (COLORONCOLOR), which is a copy where the two sizes agree.
                    pre = new Bitmap(gw, gh, PixelFormat.Format24bppRgb);
                    using (Graphics g = Graphics.FromImage(pre))
                    using (var ia = new ImageAttributes())
                    {
                        g.CompositingMode = CompositingMode.SourceCopy;
                        g.InterpolationMode = _interp;
                        ia.SetWrapMode(WrapMode.TileFlipXY);
                        float wsgn = src.Width < 0 ? -1 : 1, hsgn = src.Height < 0 ? -1 : 1;
                        g.DrawImage(bm, new[] { new PointF(0, 0), new PointF(gw, 0), new PointF(0, gh) },
                            new RectangleF(src.X, src.Y, src.Width - wsgn, src.Height - hsgn), GraphicsUnit.Pixel, ia);
                    }
                    spx = Pixels(pre, out sw, out sh);
                    // OutputDIB then leaves the stretch mode it set (HALFTONE for a SRCCOPY,
                    // COLORONCOLOR otherwise) and StretchDIBits the 24bpp result, top-down
                    // (biHeight -gh); HALFTONE runs win32k's AA engine even where the sizes agree.
                    if (_dc.StretchMode == 4 && GdiHalftone.Stretch(Dib24Info(sw, sh), 0, Dib24Bits(spx, sw, sh), 0, 0, 0, sw, sh,
                            mirrorX ? -dw : dw, mirrorY ? -dh : dh) is uint[] ht)
                    {
                        int hw = dw;
                        source = (i, j) => ht[j * hw + i];
                    }
                    else source = StretchSource(spx, sw, sh, 0, 0, sw, sh, dw, dh, mirrorX, mirrorY);
                }
                else
                {
                    spx = Pixels(bm, out sw, out sh);
                    int sx0 = (int)src.X, sy0 = (int)src.Y, sx1 = (int)(src.X + src.Width), sy1 = (int)(src.Y + src.Height);
                    bool mx = mirrorX, my = mirrorY;
                    if (sx1 < sx0) { (sx0, sx1) = (sx1 + 1, sx0 + 1); mx = !mx; }   // bOrderStupid on the source
                    if (sy1 < sy0) { (sy0, sy1) = (sy1 + 1, sy0 + 1); my = !my; }
                    if (sx1 == sx0 || sy1 == sy0) return true;
                    // BLTRECORD::bStretch @14017cba0: a stretch through the BLTRECORD (a bitmap's, or a
                    // DIB's under a ROP other than SRCCOPY) that is not HALFTONE, in GM_COMPATIBLE,
                    // not mirrored, by at most one pixel each way is BLTRECORD::bBitBlt @14017c910:
                    // copied, a missing last column / row dropped, an extra one the last again.
                    bool viaBltRecord = _wmfCanvas && _dc.StretchMode != 4 && (!dib || rr != 0xcc0020);
                    if (viaBltRecord && !mx && !my && Math.Abs(dw - (sx1 - sx0)) <= 1 && Math.Abs(dh - (sy1 - sy0)) <= 1)
                    {
                        int bx = sx0, by = sy0, bw = sx1 - sx0, bh = sy1 - sy0;
                        uint[] q = spx; int qw = sw, qh = sh;
                        source = (i, j) => q[Math.Clamp(by + Math.Min(j, bh - 1), 0, qh - 1) * qw + Math.Clamp(bx + Math.Min(i, bw - 1), 0, qw - 1)];
                    }
                    else if (_htInfo != null && _dc.StretchMode == 4
                        && GdiHalftone.Stretch(_htInfo, 0, _htBits, _htBitsAt, (int)src.X, sh - (int)src.Y - (int)src.Height, (int)src.Width, (int)src.Height,
                            mirrorX ? -dw : dw, mirrorY ? -dh : dh) is uint[] ht)
                    {
                        // HALFTONE (MfEnumState::OutputDIB's mode for a SRCCOPY GDI+ did not stretch
                        // first): win32k's AA halftone engine (GdiHalftone.cs), from the DIB's own bits.
                        int hw = dw;
                        source = (i, j) => ht[j * hw + i];
                    }
                    else source = StretchSource(spx, sw, sh, sx0, sy0, sx1 - sx0, sy1 - sy0, dw, dh, mx, my, _dc.StretchMode);
                }
            }
            bool[] clip = ClipMask();
            int cx0 = Math.Max(0, left), cy0 = Math.Max(0, top);
            int cx1 = Math.Min(_cw, left + dw), cy1 = Math.Min(_ch, top + dh);
            if (cx1 <= cx0 || cy1 <= cy0) { pre?.Dispose(); return true; }
            BitmapData bd = _canvas.LockBits(new Rectangle(0, 0, _cw, _ch), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
            try
            {
                var row = new int[_cw];
                for (int y = cy0; y < cy1; y++)
                {
                    System.Runtime.InteropServices.Marshal.Copy(bd.Scan0 + y * bd.Stride, row, 0, _cw);
                    int j = y - top;
                    for (int x = cx0; x < cx1; x++)
                    {
                        if (clip != null && !clip[y * _cw + x]) continue;
                        int i = x - left;
                        uint p = pat != null ? pat(x, y) : 0;
                        uint s = source != null ? source(i, j) : 0;
                        row[x] = unchecked((int)Rop3(index, p, s, (uint)row[x]));
                    }
                    System.Runtime.InteropServices.Marshal.Copy(row, 0, bd.Scan0 + y * bd.Stride, _cw);
                }
            }
            finally { _canvas.UnlockBits(bd); pre?.Dispose(); }
            return true;
        }

        /// <summary>EngStretchBltNew @140178858's COLORONCOLOR read of the source for each pixel of
        /// the (ordered) destination. A mirrored blit first copies the source mirrored (the
        /// SURFMEM copy, vStrMirror32 @1401c06d0 across, a negated stride down) and then stretches
        /// it unmirrored; the stretch is stretch::vInitStrDDA @1401bfda0's DDA, which gives source
        /// pixel k the destination pixels from floor((k D + (S - 1) / 2) / S), so destination pixel
        /// x reads k = floor(((x + 1) S - (S - 1) / 2 - 1) / D).</summary>
        static Func<int, int, uint> StretchSource(uint[] px, int w, int h, int sx, int sy, int sW, int sH, int dW, int dH, bool mirrorX, bool mirrorY, int mode = 3)
        {
            // BLACKONWHITE / WHITEONBLACK shrinking (stretch::pxrlStrRead01AND @1401be970,
            // pxrlStrRead24AND @1401bf6b0, ...OR): the source pixels the DDA skips are ANDed (ORed)
            // into the destination pixel the next one writes.
            if ((mode == 1 || mode == 2) && (dW < sW || dH < sH))
            {
                var c0 = new int[dW]; var c1 = new int[dW]; var r0 = new int[dH]; var r1 = new int[dH];
                int Kx(int i) => i < 0 ? -1 : (int)(((long)(i + 1) * sW - ((sW - 1) >> 1) - 1) / dW);
                int Ky(int j) => j < 0 ? -1 : (int)(((long)(j + 1) * sH - ((sH - 1) >> 1) - 1) / dH);
                for (int i = 0; i < dW; i++) { c1[i] = Kx(i); c0[i] = dW < sW ? Kx(i - 1) + 1 : c1[i]; if (c0[i] > c1[i]) c0[i] = c1[i]; }
                for (int j = 0; j < dH; j++) { r1[j] = Ky(j); r0[j] = dH < sH ? Ky(j - 1) + 1 : r1[j]; if (r0[j] > r1[j]) r0[j] = r1[j]; }
                bool and = mode == 1;
                return (i, j) =>
                {
                    uint v = and ? 0xffffffu : 0;
                    for (int ky = r0[j]; ky <= r1[j]; ky++)
                        for (int kx = c0[i]; kx <= c1[i]; kx++)
                        {
                            int ux = mirrorX ? sW - 1 - kx : kx, uy = mirrorY ? sH - 1 - ky : ky;
                            uint p = px[Math.Clamp(sy + uy, 0, h - 1) * w + Math.Clamp(sx + ux, 0, w - 1)];
                            v = and ? v & p : v | p;
                        }
                    return v;
                };
            }
            var us = new int[dW];
            var vs = new int[dH];
            for (int i = 0; i < dW; i++)
            {
                int k = (int)(((long)(i + 1) * sW - ((sW - 1) >> 1) - 1) / dW);
                if (mirrorX) k = sW - 1 - k;
                us[i] = Math.Clamp(sx + k, 0, w - 1);
            }
            for (int j = 0; j < dH; j++)
            {
                int k = (int)(((long)(j + 1) * sH - ((sH - 1) >> 1) - 1) / dH);
                if (mirrorY) k = sH - 1 - k;
                vs[j] = Math.Clamp(sy + k, 0, h - 1);
            }
            return (i, j) => px[vs[j] * w + us[i]];
        }

        /// <summary>The BITMAPINFOHEADER OutputDIB builds for its stretched bitmap: 24bpp, top-down.</summary>
        static byte[] Dib24Info(int w, int h)
        {
            var info = new byte[40];
            Le.W32(info, 0, 40);
            Le.W32(info, 4, w);
            Le.W32(info, 8, -h);
            info[12] = 1;
            info[14] = 24;
            return info;
        }

        /// <summary>0x00RRGGBB pixels as 24bpp DIB scans (B, G, R; rows padded to four bytes).</summary>
        static byte[] Dib24Bits(uint[] px, int w, int h)
        {
            int stride = (w * 3 + 3) & ~3;
            var bits = new byte[stride * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    uint p = px[y * w + x];
                    int q = y * stride + x * 3;
                    bits[q] = (byte)p; bits[q + 1] = (byte)(p >> 8); bits[q + 2] = (byte)(p >> 16);
                }
            return bits;
        }

        static uint[] Pixels(Bitmap b, out int w, out int h)
        {
            w = b.Width; h = b.Height;
            var px = new uint[w * h];
            BitmapData bd = b.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                var row = new int[w];
                for (int y = 0; y < h; y++)
                {
                    System.Runtime.InteropServices.Marshal.Copy(bd.Scan0 + y * bd.Stride, row, 0, w);
                    for (int x = 0; x < w; x++) px[y * w + x] = (uint)row[x] & 0xffffff;
                }
            }
            finally { b.UnlockBits(bd); }
            return px;
        }

        /// <summary>A fill or stroke under a ROP2 other than R2_COPYPEN: what it covers (drawn with the
        /// plain brush or pen into a scratch DIB) put through the ROP2 against the DIB.</summary>
        bool RopDraw(Action<Graphics> draw)
        {
            int code = _dc.Rop2;
            if (!Canvas || code == 13) return false;
            if (code == 11) return true;
            _ropUsed = true;
            using (var tmp = new Bitmap(_cw, _ch, PixelFormat.Format32bppArgb))
            {
                Graphics saved = _t;
                int savedRop = _dc.Rop2;
                using (Graphics g = Graphics.FromImage(tmp))
                {
                    _t = g;
                    _dc.Rop2 = 13;
                    try { Prepare(); draw(g); }
                    finally { _t = saved; _dc.Rop2 = savedRop; }
                }
                uint[] cover = Pixels(tmp, out _, out _);
                BitmapData ad = tmp.LockBits(new Rectangle(0, 0, _cw, _ch), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                var alpha = new int[_cw * _ch];
                try
                {
                    var row = new int[_cw];
                    for (int y = 0; y < _ch; y++)
                    {
                        System.Runtime.InteropServices.Marshal.Copy(ad.Scan0 + y * ad.Stride, row, 0, _cw);
                        for (int x = 0; x < _cw; x++) alpha[y * _cw + x] = (int)((uint)row[x] >> 24);
                    }
                }
                finally { tmp.UnlockBits(ad); }
                BitmapData bd = _canvas.LockBits(new Rectangle(0, 0, _cw, _ch), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
                try
                {
                    var row = new int[_cw];
                    for (int y = 0; y < _ch; y++)
                    {
                        System.Runtime.InteropServices.Marshal.Copy(bd.Scan0 + y * bd.Stride, row, 0, _cw);
                        for (int x = 0; x < _cw; x++)
                        {
                            int k = y * _cw + x;
                            if (alpha[k] < 128) continue;
                            row[x] = unchecked((int)Rop2(code, cover[k], (uint)row[x]));
                        }
                        System.Runtime.InteropServices.Marshal.Copy(row, 0, bd.Scan0 + y * bd.Stride, _cw);
                    }
                }
                finally { _canvas.UnlockBits(bd); }
            }
            return true;
        }
    }
}
