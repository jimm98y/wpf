// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// GDI records -- an EMF's, a WMF's, an EMF+ file's after EmfPlusGetDC -- played through Graphics'
// public API. GDI+ hands these to GDI (EnumEnhMetaFile / PlayMetaFile into the target's HDC,
// GpGraphics::EnumEmf @180091520) or, converting, to CEmfPlusEnumState (@1800ab410), which turns each
// record into GpGraphics calls; this does what the converter does, in managed code, keeping the DC
// state GDI keeps:
//
//   the mapping: map mode (CEmfPlusEnumState::RecalculateTransform @1800b13c0 -- the fixed modes in
//     the reference device's millimetres, y up), window and viewport origin and extent
//     (MM_ISOTROPIC keeping the aspect), the world transform (SetWorldTransform /
//     ModifyWorldTransform), all under the EMF's frame mapped onto the destination (PlayEnhMetaFile:
//     rclFrame onto the inclusive rectangle GDI+ rounds the destination to) or a WMF's placeable box;
//   the object table (CreatePen / ExtCreatePen / CreateBrushIndirect / CreateDIBPatternBrushPt /
//     CreateMonoBrush / ExtCreateFontIndirectW / CreatePalette, SelectObject, DeleteObject, the
//     stock objects); text colour, background colour and mode, text alignment, polygon fill mode,
//     arc direction, miter limit, ROP2, the current position, SaveDC / RestoreDC;
//   paths (BeginPath .. EndPath, then FillPath / StrokePath / StrokeAndFillPath / SelectClipPath /
//     WidenPath / FlattenPath), clipping (IntersectClipRect, ExcludeClipRect, ExtSelectClipRgn,
//     OffsetClipRgn, SetMetaRgn) in device units;
//   drawing: every poly-, line-, rectangle, ellipse, arc, pie, chord and round-rectangle record,
//     regions (FillRgn / FrameRgn / PaintRgn), text (ExtTextOutW / A, PolyTextOut), bitmaps
//     (BitBlt / StretchBlt / StretchDIBits / SetDIBitsToDevice / AlphaBlend / TransparentBlt, the
//     DIBs decoded), GradientFill.
//
// GDI draws aliased: the target draws GDI records with SmoothingMode None.
//

using System.Collections.Generic;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text;

namespace System.Drawing.WebGpuBackend.Gdip
{
    internal sealed partial class GpGdiPlayer : IDisposable
    {
        readonly GpMetafilePlayer.Session _s;
        Graphics _t;

        abstract class GdiObj : IDisposable { public virtual void Dispose() { } }

        sealed class GdiPen : GdiObj
        {
            public int Style;            // PS_* (style | end cap | join | type)
            public int Width;
            public Color Color;
            public int BrushStyle;
            public Brush PatternBrush;
            public float[] Dashes;
            public bool Old;             // CreatePen's (LOGPEN): realized per draw (DC::vRealizeLineAttrs)
            public bool OldGeometric;    // a CreatePen pen realized geometric (Rectangle strokes it mitred)
            public bool Null => (Style & 0xf) == 5;
            public bool Geometric => (Style & 0x10000) != 0;
            public override void Dispose() => PatternBrush?.Dispose();
        }

        sealed class GdiBrush : GdiObj
        {
            public int Style;            // BS_SOLID 0, BS_NULL 1, BS_HATCHED 2, BS_PATTERN 3, BS_DIBPATTERN 5
            public Color Color;
            public int Hatch;
            public Bitmap Pattern;
            public bool Mono;
            public bool OneBit;          // a 1bpp pattern (a mask for the raster idioms)
            public override void Dispose() => Pattern?.Dispose();
        }

        sealed class GdiFont : GdiObj
        {
            public int Height, Width, Escapement, Orientation, Weight;
            public bool Italic, Underline, StrikeOut;
            public int CharSet, Quality;
            public string Face = "";
        }

        sealed class GdiPalette : GdiObj { public Color[] Entries = new Color[0]; }

        sealed class Dc
        {
            public int MapMode = 1;
            public Point WinOrg, VpOrg;
            public Size WinExt = new Size(1, 1), VpExt = new Size(1, 1);
            public GpMat World = GpMat.Identity;
            public GdiPen Pen;
            public GdiBrush Brush;
            public GdiFont Font;
            public Color TextColor = Color.Black, BkColor = Color.White;
            public int BkMode = 2;
            public int TextAlign;
            public int PolyFill = 1;
            public int Rop2 = 13;
            public int StretchMode = 1;
            public int ArcDirection = 1;
            public float MiterLimit = 10f;
            public PointF Pos;
            public Region Clip;
            public Region MetaClip;
            public Point BrushOrg;
            // GDI's own state of the DIB's DC (GpGdiPlayer.Gdi.cs): the target's world transform,
            // the virtual DC's, the clip and meta regions in device pixels.
            public GdiXform GWorld = GdiXform.Identity, VWorld = GdiXform.Identity;
            public bool GWorldIdentity = true, VWorldIdentity = true;
            public GdiRgn GClip, GMeta;
            public Dc Clone()
            {
                var d = (Dc)MemberwiseClone();
                d.Clip = Clip?.Clone();
                d.MetaClip = MetaClip?.Clone();
                return d;
            }
        }

        Dc _dc = new Dc();
        readonly Stack<Dc> _saved = new Stack<Dc>();
        GdiObj[] _objects = new GdiObj[16];
        GraphicsPath _path;
        bool _inPath;
        GpMat _base = GpMat.Identity;
        int _devCx = 1, _devCy = 1, _mmCx = 1, _mmCy = 1;

        public GpGdiPlayer(GpMetafilePlayer.Session s)
        {
            _s = s;
            _t = s.Target;
            _dc.Pen = StockPen(7);
            _dc.Brush = StockBrush(0);
            _dc.Font = new GdiFont { Height = -12, Weight = 400, Face = "System" };
        }

        public void Dispose()
        {
            if (_canvas != null) { _t.Dispose(); _t = _target; _canvas.Dispose(); _canvas = null; }
            foreach (var o in _objects) o?.Dispose();
            _dc.Clip?.Dispose();
            _path?.Dispose();
        }

        static GdiPen StockPen(int i)
        {
            switch (i)
            {
                case 6: return new GdiPen { Style = 0, Width = 0, Color = Color.White };
                case 8: return new GdiPen { Style = 5 };
                default: return new GdiPen { Style = 0, Width = 0, Color = Color.Black };
            }
        }

        static GdiBrush StockBrush(int i)
        {
            switch (i)
            {
                case 1: return new GdiBrush { Style = 0, Color = Color.FromArgb(0xc0, 0xc0, 0xc0) };
                case 2: return new GdiBrush { Style = 0, Color = Color.FromArgb(0x80, 0x80, 0x80) };
                case 3: return new GdiBrush { Style = 0, Color = Color.FromArgb(0x40, 0x40, 0x40) };
                case 4: return new GdiBrush { Style = 0, Color = Color.Black };
                case 5: return new GdiBrush { Style = 1 };
                default: return new GdiBrush { Style = 0, Color = Color.White };
            }
        }

        // ---- the mapping ---------------------------------------------------------------------------

        public void BeginEmf(GpMetafileData d)
        {
            byte[] h = d.Emf;
            int fl = Le.I32(h, 24), ft = Le.I32(h, 28), fr = Le.I32(h, 32), fb = Le.I32(h, 36);
            _devCx = Math.Max(1, Le.I32(h, 72)); _devCy = Math.Max(1, Le.I32(h, 76));
            _mmCx = Math.Max(1, Le.I32(h, 80)); _mmCy = Math.Max(1, Le.I32(h, 84));
            if (_s.IsEmfPlus)
            {
                // An EMF+ file's GDI records (after GetDC) are in the metafile's device pixels.
                _base = _s.MetafileToDevice;
                return;
            }
            // The frame onto the destination (PlayEnhMetaFile maps rclFrame onto the rectangle it is
            // given); the destination here is the unit square of the playback's world.
            RectangleF dst = _s.EmfDest;
            GpMat toDevice = _s.EmfWorldToDevice;
            // GpGraphics::EnumEmf: GDI plays the picture into a DIB of the destination's device size.
            if (StartCanvas(DestCorners(dst)))
            {
                // MetafilePlayer::EnumerateEmfRecords hands PlayEnhMetaFile (0, 0, w - 1, h - 1): the
                // frame spans the rectangle's extents.
                dst = new RectangleF(0, 0, _cw - 1, _ch - 1);
                toDevice = GpMat.Identity;
                GdiBeginEmf(h);
            }
            double kx = 100.0 * _mmCx / _devCx, ky = 100.0 * _mmCy / _devCy;
            double fw = fr - fl, fh = fb - ft;
            double sx = fw != 0 ? dst.Width / fw : 1, sy = fh != 0 ? dst.Height / fh : 1;
            if (Canvas)
            {
                // An empty frame: a reference pixel spans the whole DIB.
                if (fw == 0) sx = dst.Width / kx;
                if (fh == 0) sy = dst.Height / ky;
            }
            var m = new GpMat((float)(kx * sx), 0, 0, (float)(ky * sy), (float)(dst.X - fl * sx), (float)(dst.Y - ft * sy));
            _base = GpMat.Multiply(m, toDevice);
        }

        PointF[] DestCorners(RectangleF dst)
        {
            var p = new[] { new PointF(dst.X, dst.Y), new PointF(dst.Right, dst.Y), new PointF(dst.X, dst.Bottom) };
            _s.EmfWorldToDevice.Transform(p);
            return p;
        }

        public void BeginWmf(GpMetafileData d)
        {
            GpPlaceable p = d.Placeable;
            RectangleF dst = _s.EmfDest;
            _dc.MapMode = 8;
            _dc.WinOrg = new Point(Math.Min(p.Left, p.Right), Math.Min(p.Top, p.Bottom));
            _dc.WinExt = new Size(Math.Max(1, Math.Abs(p.Right - p.Left)), Math.Max(1, Math.Abs(p.Bottom - p.Top)));
            if (StartCanvas(DestCorners(dst)))
            {
                _dc.VpOrg = new Point(0, 0);
                _dc.VpExt = new Size(_cw, _ch);
                _base = GpMat.Identity;
                GdiBeginWmf();
            }
            else
            {
                _dc.VpOrg = new Point(GpMetafileFormat.Round(dst.X), GpMetafileFormat.Round(dst.Y));
                _dc.VpExt = new Size(Math.Max(1, GpMetafileFormat.Round(dst.Width)), Math.Max(1, GpMetafileFormat.Round(dst.Height)));
                _base = _s.EmfWorldToDevice;
            }
            _objects = new GdiObj[Math.Max(16, (int)Le.U16(d.Wmf, 10))];
        }

        static bool FixedScale(int mode, out double perMm)
        {
            switch (mode)
            {
                case 2: perMm = 10; return true;
                case 3: perMm = 100; return true;
                case 4: perMm = 100 / 25.4; return true;
                case 5: perMm = 1000 / 25.4; return true;
                case 6: perMm = 1440 / 25.4; return true;
            }
            perMm = 0;
            return false;
        }

        GpMat PageToDevice()
        {
            double sx, sy;
            if (_dc.MapMode == 1) { sx = 1; sy = 1; }
            else if (FixedScale(_dc.MapMode, out double perMm))
            {
                sx = (double)_devCx / _mmCx / perMm;
                sy = -(double)_devCy / _mmCy / perMm;
            }
            else
            {
                sx = (double)_dc.VpExt.Width / (_dc.WinExt.Width == 0 ? 1 : _dc.WinExt.Width);
                sy = (double)_dc.VpExt.Height / (_dc.WinExt.Height == 0 ? 1 : _dc.WinExt.Height);
            }
            double ox = _dc.VpOrg.X - _dc.WinOrg.X * sx, oy = _dc.VpOrg.Y - _dc.WinOrg.Y * sy;
            return new GpMat((float)sx, 0, 0, (float)sy, (float)ox, (float)oy);
        }

        GpMat LogicalToTarget => GpMat.Multiply(GpMat.Multiply(_dc.World, PageToDevice()), _base);

        void Isotropic()
        {
            if (_dc.MapMode != 7) return;
            int wx = _dc.WinExt.Width, wy = _dc.WinExt.Height, vx = _dc.VpExt.Width, vy = _dc.VpExt.Height;
            if (wx == 0 || wy == 0) return;
            double xd = (double)vx / wx, yd = (double)vy / wy;
            if (Math.Abs(xd) < Math.Abs(yd)) vy = (int)Math.Round(vy * Math.Abs(xd / yd));
            else vx = (int)Math.Round(vx * Math.Abs(yd / xd));
            _dc.VpExt = new Size(vx == 0 ? 1 : vx, vy == 0 ? 1 : vy);
        }

        PointF[] ToTarget(PointF[] pts)
        {
            var o = (PointF[])pts.Clone();
            LogicalToTarget.Transform(o);
            return o;
        }

        PointF ToTarget(PointF p)
        {
            var a = new[] { p };
            LogicalToTarget.Transform(a);
            return a[0];
        }

        float XScale { get { GpMat m = LogicalToTarget; return (float)Math.Sqrt(m.M11 * m.M11 + m.M12 * m.M12); } }
        float YScale { get { GpMat m = LogicalToTarget; return (float)Math.Sqrt(m.M21 * m.M21 + m.M22 * m.M22); } }

        // ---- the target --------------------------------------------------------------------------

        void Prepare()
        {
            _t.ResetTransform();
            _t.PageUnit = GraphicsUnit.Pixel;
            _t.PageScale = 1f;
            Region vis = Canvas ? GdiClipRegion() : _s.BaseClip?.Clone();
            if (!Gdi)
            {
                if (_dc.Clip != null) { if (vis == null) vis = _dc.Clip.Clone(); else vis.Intersect(_dc.Clip); }
                if (_dc.MetaClip != null) { if (vis == null) vis = _dc.MetaClip.Clone(); else vis.Intersect(_dc.MetaClip); }
            }
            if (vis == null) _t.ResetClip(); else { _t.Clip = vis; vis.Dispose(); }
            _t.SmoothingMode = SmoothingMode.None;
            // GDI samples a DIB's pixels where GDI+ does without a pixel offset.
            _t.PixelOffsetMode = Canvas ? PixelOffsetMode.None : PixelOffsetMode.Half;
            _t.CompositingMode = CompositingMode.SourceOver;
            _t.InterpolationMode = _dc.StretchMode == 4 ? InterpolationMode.HighQualityBilinear : InterpolationMode.NearestNeighbor;
        }

        bool Nop => _dc.Rop2 == 11;

        Color RopColor(Color c)
        {
            switch (_dc.Rop2)
            {
                case 1: return Color.Black;
                case 16: return Color.White;
                case 4: return Color.FromArgb(c.A, 255 - c.R, 255 - c.G, 255 - c.B);
            }
            return c;
        }

        // ---- raster operations on the target's pixels, as GDI plays them ----------------------------
        //
        // (A plain EMF or WMF plays into GDI+'s 32bpp DIB, where GpGdiPlayer.Raster.cs runs each raster
        // operation on the pixels; what follows is for the GDI records an EMF+ file plays after GetDC.)
        //
        // GDI+'s down-level translucency is three raster operations GDI applies to the target's
        // pixels: PATINVERT the colour, AND (DPa / R2_MASKPEN) a 1bpp dither pattern, PATINVERT again;
        // what is left is the colour where the pattern is black. A translucent bitmap is the same with
        // SRCINVERT; a masked one SRCPAINT's its 1bpp mask (which under HALFTONE stretching GDI copies)
        // then SRCAND's the colours. These are played as their outcome: the pattern is in the target's
        // pixels, aligned to its origin, as a GDI brush is.

        GdiBrush _xor;                       // the colour a PATINVERT is waiting to cancel
        Bitmap _xorImage;                    // a SRCINVERT bitmap waiting for its mask
        PointF[] _xorImageDest;
        RectangleF _xorImageSrc;
        Bitmap _xorImageMask;                // the dither pattern ANDed between the two SRCINVERTs
        Bitmap _mask;                        // a SRCPAINT 1bpp mask waiting for its colours
        PointF[] _maskDest;

        /// <summary>A texture of the pattern's black pixels in <paramref name="c"/>, the rest clear,
        /// tiled in target pixels from the origin.</summary>
        static TextureBrush PatternBrushOf(Bitmap pattern, Color c)
        {
            var t = new Bitmap(pattern.Width, pattern.Height, PixelFormat.Format32bppArgb);
            for (int y = 0; y < pattern.Height; y++)
                for (int x = 0; x < pattern.Width; x++)
                    t.SetPixel(x, y, pattern.GetPixel(x, y).GetBrightness() < 0.5f ? c : Color.Transparent);
            var tb = new TextureBrush(t, WrapMode.Tile);
            t.Dispose();
            return tb;
        }

        /// <summary>The brush a DPa / R2_MASKPEN fill paints: while a PATINVERT colour waits, the
        /// colour through the pattern; else the pattern's black (AND keeps what is under white).</summary>
        Brush MaskFillBrush()
        {
            GdiBrush b = _dc.Brush;
            if (b == null || b.Pattern == null) return null;
            if (_xorImage != null)
            {
                _xorImageMask?.Dispose();
                _xorImageMask = (Bitmap)b.Pattern.Clone();
                return null;
            }
            Color c = _xor != null ? (_xor.Pattern == null ? _xor.Color : Color.Black) : Color.Black;
            TextureBrush tb = PatternBrushOf(b.Pattern, c);
            // PlayEnhMetaFile moves the brush origin with the picture: the pattern starts where the
            // metafile's device origin lands.
            tb.TranslateTransform((float)Math.Round(_base.Dx), (float)Math.Round(_base.Dy));
            return tb;
        }

        bool MaskFill => _dc.Rop2 == 9 && _dc.Brush != null && _dc.Brush.Pattern != null && _dc.Brush.OneBit;

        Brush FillBrush()
        {
            GdiBrush b = _dc.Brush;
            if (b == null || b.Style == 1 || Nop) return null;
            if (MaskFill) return MaskFillBrush();
            switch (b.Style)
            {
                case 0: return new SolidBrush(RopColor(b.Color));
                case 2:
                    {
                        HatchStyle hs;
                        switch (b.Hatch)
                        {
                            case 0: hs = HatchStyle.Horizontal; break;
                            case 1: hs = HatchStyle.Vertical; break;
                            case 2: hs = HatchStyle.ForwardDiagonal; break;
                            case 3: hs = HatchStyle.BackwardDiagonal; break;
                            case 4: hs = HatchStyle.Cross; break;
                            default: hs = HatchStyle.DiagonalCross; break;
                        }
                        Color back = _dc.BkMode == 1 ? Color.Transparent : _dc.BkColor;
                        return new HatchBrush(hs, b.Color, back);
                    }
                case 3:
                case 5:
                    if (b.Pattern == null) return new SolidBrush(b.Color);
                    {
                        Bitmap pat = b.Mono ? Recolor(b.Pattern, _dc.TextColor, _dc.BkColor) : b.Pattern;
                        var tb = new TextureBrush(pat, WrapMode.Tile);
                        GpMat m = _base;
                        // A GDI pattern is device pixels, tiled from the brush origin: never scaled.
                        tb.Transform = new Matrix(1, 0, 0, 1, MathF.Round(m.Dx) + _dc.BrushOrg.X, MathF.Round(m.Dy) + _dc.BrushOrg.Y);
                        if (!ReferenceEquals(pat, b.Pattern)) pat.Dispose();
                        return tb;
                    }
            }
            return new SolidBrush(b.Color);
        }

        static Bitmap Recolor(Bitmap mono, Color fore, Color back)
        {
            var r = new Bitmap(mono.Width, mono.Height, PixelFormat.Format32bppArgb);
            for (int y = 0; y < mono.Height; y++)
                for (int x = 0; x < mono.Width; x++)
                    r.SetPixel(x, y, mono.GetPixel(x, y).GetBrightness() < 0.5f ? fore : back);
            return r;
        }

        Pen StrokePen()
        {
            GdiPen p = _dc.Pen;
            if (p == null || p.Null || Nop) return null;
            float w = p.Width <= 1 && !p.Geometric ? 1f : Math.Max(1f, p.Width * XScale);
            var pen = new Pen(RopColor(p.Color), w);
            if (p.PatternBrush != null) pen.Brush = p.PatternBrush;
            int style = p.Style & 0xf;
            if (p.Geometric || w > 1f)
            {
                switch (p.Style & 0xf00)
                {
                    case 0x000: pen.StartCap = pen.EndCap = LineCap.Round; break;
                    case 0x100: pen.StartCap = pen.EndCap = LineCap.Square; break;
                    case 0x200: pen.StartCap = pen.EndCap = LineCap.Flat; break;
                }
                switch (p.Style & 0xf000)
                {
                    case 0x0000: pen.LineJoin = LineJoin.Round; break;
                    case 0x1000: pen.LineJoin = LineJoin.Bevel; break;
                    case 0x2000: pen.LineJoin = LineJoin.Miter; pen.MiterLimit = Math.Max(1f, _dc.MiterLimit); break;
                }
            }
            switch (style)
            {
                case 1: pen.DashPattern = w > 1 ? new[] { 3f, 1f } : new[] { 18f, 6f }; break;
                case 2: pen.DashPattern = w > 1 ? new[] { 1f, 1f } : new[] { 3f, 3f }; break;
                case 3: pen.DashPattern = w > 1 ? new[] { 3f, 1f, 1f, 1f } : new[] { 9f, 6f, 3f, 6f }; break;
                case 4: pen.DashPattern = w > 1 ? new[] { 3f, 1f, 1f, 1f, 1f, 1f } : new[] { 9f, 3f, 3f, 3f, 3f, 3f }; break;
                case 7:
                    if (p.Dashes != null && p.Dashes.Length > 0)
                    {
                        var d = new float[p.Dashes.Length];
                        for (int i = 0; i < d.Length; i++) d[i] = Math.Max(0.1f, p.Dashes[i] * XScale / w);
                        pen.DashPattern = d;
                    }
                    break;
                case 8: pen.DashPattern = new[] { 1f, 1f }; break;
            }
            if (style == 6) pen.Alignment = PenAlignment.Inset;
            return pen;
        }

        FillMode PolyFill => _dc.PolyFill == 2 ? FillMode.Winding : FillMode.Alternate;

        void FillAndStroke(GraphicsPath devicePath, bool fill, bool stroke)
        {
            if (_inPath)
            {
                _path.AddPath(devicePath, false);
                return;
            }
            if (fill && RopDraw(g => { using (Brush b = FillBrush()) if (b != null) g.FillPath(b, devicePath); })) fill = false;
            if (stroke && RopDraw(g => { using (Pen p = StrokePen()) if (p != null) g.DrawPath(p, devicePath); })) stroke = false;
            if (!fill && !stroke) return;
            Prepare();
            if (fill)
                using (Brush b = FillBrush())
                    if (b != null) _t.FillPath(b, devicePath);
            if (stroke)
                using (Pen p = StrokePen())
                    if (p != null) _t.DrawPath(p, devicePath);
        }

        void Stroke(GraphicsPath devicePath)
        {
            if (!RopDraw(g => { using (Pen p = StrokePen()) if (p != null) g.DrawPath(p, devicePath); }))
            {
                Prepare();
                using (Pen p = StrokePen()) if (p != null) _t.DrawPath(p, devicePath);
            }
            devicePath.Dispose();
        }

        // ---- records ----------------------------------------------------------------------------

        public void PlayEmf(int type, byte[] b, int o, int n)
        {
            var r = new GpReader(b, o, n);
            switch (type)
            {
                case 2: PolyCore(r, false, 0); return;
                case 3: PolyCore(r, false, 1); return;
                case 4: PolyCore(r, false, 2); return;
                case 5: PolyCore(r, false, 3); return;
                case 6: PolyCore(r, false, 4); return;
                case 7: PolyPolyCore(r, false, false); return;
                case 8: PolyPolyCore(r, false, true); return;
                case 85: PolyCore(r, true, 0); return;
                case 86: PolyCore(r, true, 1); return;
                case 87: PolyCore(r, true, 2); return;
                case 88: PolyCore(r, true, 3); return;
                case 89: PolyCore(r, true, 4); return;
                case 90: PolyPolyCore(r, true, false); return;
                case 91: PolyPolyCore(r, true, true); return;
                case 56: case 92: PolyDraw(r, type == 92); return;
                case 9: { int cx = r.I32(), cy = r.I32(); bool iso = _dc.MapMode >= 7; SetWindowExt(cx, cy); if (Gdi && iso) GdiSetTransform(); return; }
                case 10: { int x = r.I32(), y = r.I32(); _dc.WinOrg = new Point(x, y); if (Gdi) GdiSetTransform(); return; }
                case 11: { int cx = r.I32(), cy = r.I32(); bool iso = _dc.MapMode >= 7; SetViewportExt(cx, cy); if (Gdi && iso) GdiSetTransform(); return; }
                case 12: { int x = r.I32(), y = r.I32(); _dc.VpOrg = new Point(x, y); if (Gdi) GdiSetTransform(); return; }
                case 13: { int x = r.I32(), y = r.I32(); _dc.BrushOrg = new Point(x, y); return; }
                case 17:
                    {
                        int old = _dc.MapMode, m = r.I32();
                        SetMapMode(m);
                        if (Gdi && (old != m || m == 7)) GdiSetTransform();
                        return;
                    }
                case 18: _dc.BkMode = r.I32(); return;
                case 19: _dc.PolyFill = r.I32(); return;
                case 20: _dc.Rop2 = r.I32(); return;
                case 21: _dc.StretchMode = r.I32(); return;
                case 22: _dc.TextAlign = r.I32(); return;
                case 24: _dc.TextColor = ColorRef(r.I32()); return;
                case 25: _dc.BkColor = ColorRef(r.I32()); return;
                case 26: { int x = r.I32(), y = r.I32(); OffsetClip(x, y); return; }
                case 27: { int x = r.I32(), y = r.I32(); MoveTo(x, y); return; }
                case 28: SetMetaRgn(); return;
                case 29: { int l = r.I32(), t = r.I32(), rr = r.I32(), bb = r.I32(); ClipRectLogical(l, t, rr, bb, true); return; }
                case 30: { int l = r.I32(), t = r.I32(), rr = r.I32(), bb = r.I32(); ClipRectLogical(l, t, rr, bb, false); return; }
                case 31: { int xn = r.I32(), xd = r.I32(), yn = r.I32(), yd = r.I32(); ScaleExt(false, xn, xd, yn, yd); if (Gdi) GdiSetTransform(); return; }
                case 32: { int xn = r.I32(), xd = r.I32(), yn = r.I32(), yd = r.I32(); ScaleExt(true, xn, xd, yn, yd); if (Gdi) GdiSetTransform(); return; }
                case 33: _saved.Push(_dc.Clone()); return;
                case 34: RestoreDc(r.I32()); return;
                case 35:
                    {
                        float[] e = r.Matrix6();
                        _dc.World = GpMat.FromElements(e);
                        if (Gdi)
                        {
                            ModifyWorld(ref _dc.VWorld, ref _dc.VWorldIdentity, e, 4);
                            GdiSetTransform();
                        }
                        return;
                    }
                case 36:
                    {
                        float[] e = r.Matrix6();
                        int mode = r.I32();
                        ModifyWorld(e, mode);
                        if (Gdi)
                        {
                            ModifyWorld(ref _dc.VWorld, ref _dc.VWorldIdentity, e, mode);
                            if (mode == 2) ModifyWorld(ref _dc.GWorld, ref _dc.GWorldIdentity, e, 2);
                            else GdiSetTransform();
                        }
                        return;
                    }
                case 37: SelectObject(r.U32()); return;
                case 38: CreatePen(r); return;
                case 39: CreateBrushIndirect(r); return;
                case 40: DeleteObject(r.U32()); return;
                case 41: AngleArc(r); return;
                case 42: ShapeEllipse(RectL(r)); return;
                case 43: ShapeRectangle(RectL(r)); return;
                case 44: { RectangleF rc = RectL(r); int cx = r.I32(), cy = r.I32(); ShapeRoundRect(rc, cx, cy); return; }
                case 45: ArcKind(r, 0); return;
                case 46: ArcKind(r, 1); return;
                case 47: ArcKind(r, 2); return;
                case 55: ArcKind(r, 3); return;
                case 49: CreatePalette(r); return;
                case 54: { int x = r.I32(), y = r.I32(); LineTo(x, y); return; }
                case 57: _dc.ArcDirection = r.I32(); return;
                case 58: _dc.MiterLimit = (float)r.U32(); return;   // an integer (MRSETMITERLIMIT::bPlay)
                case 59: BeginPath(); return;
                case 60: _inPath = false; return;
                case 61: _path?.CloseFigure(); _gPath?.CloseFigure(); return;
                case 62: PathOp(true, false); return;
                case 63: PathOp(true, true); return;
                case 64: PathOp(false, true); return;
                case 65: _path?.Flatten(); if (_gPath != null) _gPath = _gPath.Flattened(); return;
                case 66: WidenPath(); return;
                case 67: SelectClipPath(r.I32()); return;
                case 68: _path?.Dispose(); _path = null; _gPath = null; _inPath = false; return;
                case 71: FillRgn(r); return;
                case 72: FrameRgn(r); return;
                case 74: PaintRgn(r); return;
                case 75: ExtSelectClipRgn(r); return;
                case 76: BitBlt(r, b, o); return;
                case 77: StretchBlt(r, b, o); return;
                case 80: SetDIBitsToDevice(r, b, o); return;
                case 81: StretchDIBits(r, b, o); return;
                case 82: ExtCreateFont(r); return;
                case 83: ExtTextOut(r, b, o, false); return;
                case 84: ExtTextOut(r, b, o, true); return;
                case 96: PolyTextOut(r, b, o, false); return;
                case 97: PolyTextOut(r, b, o, true); return;
                case 93: CreateDibBrush(r, b, o, true); return;
                case 94: CreateDibBrush(r, b, o, false); return;
                case 95: ExtCreatePen(r, b, o); return;
                case 114: AlphaBlend(r, b, o); return;
                case 116: TransparentBlt(r, b, o); return;
                case 118: GradientFill(r); return;
            }
        }

        static Color ColorRef(int c) => Color.FromArgb(255, c & 0xff, (c >> 8) & 0xff, (c >> 16) & 0xff);

        static RectangleF RectL(GpReader r)
        {
            int l = r.I32(), t = r.I32(), rr = r.I32(), bb = r.I32();
            return RectangleF.FromLTRB(l, t, rr, bb);
        }

        void SetMapMode(int m)
        {
            if (m < 1 || m > 8) return;
            _dc.MapMode = m;
        }

        void SetWindowExt(int cx, int cy)
        {
            if ((_dc.MapMode != 7 && _dc.MapMode != 8) || cx == 0 || cy == 0) return;
            _dc.WinExt = new Size(cx, cy);
            Isotropic();
        }

        void SetViewportExt(int cx, int cy)
        {
            if ((_dc.MapMode != 7 && _dc.MapMode != 8) || cx == 0 || cy == 0) return;
            _dc.VpExt = new Size(cx, cy);
            Isotropic();
        }

        void ScaleExt(bool window, int xn, int xd, int yn, int yd)
        {
            if (xd == 0 || yd == 0 || (_dc.MapMode != 7 && _dc.MapMode != 8)) return;
            if (window) _dc.WinExt = new Size(_dc.WinExt.Width * xn / xd, _dc.WinExt.Height * yn / yd);
            else _dc.VpExt = new Size(_dc.VpExt.Width * xn / xd, _dc.VpExt.Height * yn / yd);
            Isotropic();
        }

        void ModifyWorld(float[] e, int mode)
        {
            var m = GpMat.FromElements(e);
            switch (mode)
            {
                case 1: _dc.World = GpMat.Identity; break;
                case 2: _dc.World = GpMat.Multiply(m, _dc.World); break;
                case 3: _dc.World = GpMat.Multiply(_dc.World, m); break;
                case 4: _dc.World = m; break;
            }
        }

        void RestoreDc(int rel)
        {
            int n = rel < 0 ? -rel : _saved.Count - rel + 1;
            if (n <= 0 || n > _saved.Count) return;
            Dc d = null;
            for (int i = 0; i < n; i++) { d?.Clip?.Dispose(); d = _saved.Pop(); }
            _dc.Clip?.Dispose();
            _dc = d;
        }

        // ---- objects ----------------------------------------------------------------------------

        void Put(uint index, GdiObj o)
        {
            if (index >= 0x10000) { o.Dispose(); return; }
            if (index >= _objects.Length) Array.Resize(ref _objects, (int)index + 16);
            GdiObj old = _objects[index];
            _objects[index] = o;
            if (old != null && !ReferenceEquals(_dc.Pen, old) && !ReferenceEquals(_dc.Brush, old) && !ReferenceEquals(_dc.Font, old)) old.Dispose();
        }

        void SelectObject(uint index)
        {
            if ((index & 0x80000000) != 0)
            {
                int s = (int)(index & 0x7fffffff);
                if (s <= 5) { _dc.Brush = StockBrush(s); return; }
                if (s == 18) { _dc.Brush = StockBrush(0); return; }
                if (s >= 6 && s <= 8) { _dc.Pen = StockPen(s); return; }
                if (s == 19) { _dc.Pen = StockPen(7); return; }
                if (s >= 10 && s <= 17)
                    _dc.Font = new GdiFont { Height = -13, Weight = s == 13 ? 700 : 400, Face = s == 10 || s == 11 || s == 16 ? "Courier New" : "Arial" };
                return;
            }
            if (index >= _objects.Length) return;
            switch (_objects[index])
            {
                case GdiPen p: _dc.Pen = p; break;
                case GdiBrush b: _dc.Brush = b; break;
                case GdiFont f: _dc.Font = f; break;
            }
        }

        void DeleteObject(uint index)
        {
            if (index >= _objects.Length || _objects[index] == null) return;
            GdiObj o = _objects[index];
            _objects[index] = null;
            if (ReferenceEquals(_dc.Pen, o) || ReferenceEquals(_dc.Brush, o) || ReferenceEquals(_dc.Font, o)) return;
            o.Dispose();
        }

        void CreatePen(GpReader r)
        {
            uint idx = r.U32();
            int style = r.I32(), wx = r.I32(); r.I32();
            int color = r.I32();
            if (Gdi && !_wmfCanvas && style != 5)
            {
                // EmfEnumState::CreatePen @1800b44d0: GDI+ makes the pen itself, cosmetic (one pixel)
                // when it is no wider than a device pixel, else geometric with round caps and joins.
                if (GdipCosmetic(wx)) { style = style is >= 0 and <= 4 or 8 ? style : 0; wx = 1; }
                else style = (style is >= 0 and <= 4 or 6 ? style : 0) | 0x10000;
            }
            Put(idx, new GdiPen { Style = style, Width = wx, Color = ColorRef(color), Old = !(Gdi && !_wmfCanvas) });
        }

        void ExtCreatePen(GpReader r, byte[] b, int o)
        {
            uint idx = r.U32();
            int offBmi = r.I32(), cbBmi = r.I32(), offBits = r.I32(), cbBits = r.I32();
            int style = r.I32(), width = r.I32(), brushStyle = r.I32(), color = r.I32(); r.I32();
            int numEntries = r.I32();
            float[] dashes = null;
            if (numEntries > 0 && numEntries < 1000)
            {
                dashes = new float[numEntries];
                for (int i = 0; i < numEntries; i++) dashes[i] = r.I32();
            }
            if (Gdi && !_wmfCanvas && brushStyle != 1 && GdipCosmetic(width))
            {
                // EmfEnumState::ExtCreatePen @1800b46a8: a pen no wider than a device pixel becomes a
                // cosmetic one of its line style (PS_INSIDEFRAME as PS_SOLID), one pixel wide.
                style &= 0xf;
                if (style == 6) style = 0;
                width = 1;
            }
            var pen = new GdiPen { Style = style, Width = width, Color = ColorRef(color), BrushStyle = brushStyle, Dashes = dashes };
            if (brushStyle == 1) pen.Style = (style & ~0xf) | 5;
            if (brushStyle == 2) pen.PatternBrush = new HatchBrush(HatchStyle.Cross, ColorRef(color), Color.Transparent);
            if ((brushStyle == 3 || brushStyle == 5) && cbBmi > 0)
            {
                Bitmap bm = Dib(b, o - 8, offBmi, cbBmi, offBits, cbBits);
                if (bm != null) pen.PatternBrush = new TextureBrush(bm);
            }
            Put(idx, pen);
        }

        void CreateBrushIndirect(GpReader r)
        {
            uint idx = r.U32();
            int style = r.I32(), color = r.I32(), hatch = r.I32();
            Put(idx, new GdiBrush { Style = style == 2 ? 2 : style == 1 ? 1 : 0, Color = ColorRef(color), Hatch = hatch });
        }

        void CreateDibBrush(GpReader r, byte[] b, int o, bool mono)
        {
            uint idx = r.U32();
            r.I32();
            int offBmi = r.I32(), cbBmi = r.I32(), offBits = r.I32(), cbBits = r.I32();
            Bitmap bm = Dib(b, o - 8, offBmi, cbBmi, offBits, cbBits);
            bool oneBit = cbBmi >= 16 && o - 8 + offBmi + 15 < b.Length && Le.U16(b, o - 8 + offBmi + 14) == 1;
            Put(idx, new GdiBrush { Style = 3, Pattern = bm, Mono = mono, OneBit = oneBit });
        }

        void CreatePalette(GpReader r)
        {
            uint idx = r.U32();
            r.I16();
            int n = r.I16();
            var pal = new GdiPalette { Entries = new Color[Math.Max(0, n)] };
            for (int i = 0; i < n; i++) { byte rr = r.U8(), g = r.U8(), bb = r.U8(); r.U8(); pal.Entries[i] = Color.FromArgb(rr, g, bb); }
            Put(idx, pal);
        }

        void ExtCreateFont(GpReader r)
        {
            uint idx = r.U32();
            var f = new GdiFont { Height = r.I32(), Width = r.I32(), Escapement = r.I32(), Orientation = r.I32(), Weight = r.I32() };
            f.Italic = r.U8() != 0; f.Underline = r.U8() != 0; f.StrikeOut = r.U8() != 0; f.CharSet = r.U8();
            r.U8(); r.U8(); f.Quality = r.U8(); r.U8();
            var sb = new StringBuilder();
            for (int i = 0; i < 32; i++) { char c = (char)r.I16(); if (c == 0) break; sb.Append(c); }
            f.Face = sb.ToString();
            Put(idx, f);
        }

        // ---- paths ------------------------------------------------------------------------------

        void BeginPath()
        {
            _path?.Dispose();
            _path = new GraphicsPath(PolyFill);
            _gPath = Gdi ? new GdiPath() : null;
            _inPath = true;
        }

        void PathOp(bool fill, bool stroke)
        {
            if (Gdi)
            {
                // NtGdiFillPath / NtGdiStrokeAndFillPath close every figure; StrokePath strokes the
                // path as it is.
                GdiPath gp = _gPath;
                _gPath = null;
                _path?.Dispose(); _path = null;
                _inPath = false;
                if (gp == null) return;
                if (fill) gp.CloseAll();
                if (fill) GdiFillPath(gp, _dc.PolyFill == 2);
                // A fill flattens the path in place before the pen widens it
                // (EPATHOBJ_bSimpleStrokeAndFill @140168450).
                if (fill && _dc.Brush != null && _dc.Brush.Style != 1) gp = gp.Flattened();
                if (stroke) GdiStroke(gp);
                return;
            }
            if (_path == null) return;
            _inPath = false;
            GraphicsPath p = _path;
            _path = null;
            p.FillMode = PolyFill;
            FillAndStroke(p, fill, stroke);
            p.Dispose();
        }

        void WidenPath()
        {
            if (Gdi)
            {
                if (_gPath == null || _dc.Pen == null || !GeometricLineAttrs(DrawPen(), out GdiLineAttrs la)) return;
                GdiPath w = GdiWiden.Widen(_gPath, TargetWtoD(), la);
                if (w != null) _gPath = w;
                return;
            }
            if (_path == null) return;
            using (Pen p = StrokePen())
                if (p != null) _path.Widen(p);
        }

        void SelectClipPath(int mode)
        {
            if (Gdi)
            {
                GdiPath gp = _gPath;
                _gPath = null;
                _path?.Dispose(); _path = null;
                _inPath = false;
                if (gp == null) return;
                gp.CloseAll();
                GdiSelectRgn(GdiRgn.FromSpans(GdiFill.Spans(gp, _dc.PolyFill == 2)), mode);
                return;
            }
            if (_path == null) return;
            _inPath = false;
            _path.FillMode = PolyFill;
            CombineClip(new Region(_path), mode);
            _path.Dispose();
            _path = null;
        }

        // ---- clipping (device units) ---------------------------------------------------------------

        // RGN_AND 1, RGN_OR 2, RGN_XOR 3, RGN_DIFF 4, RGN_COPY 5.
        void CombineClip(Region device, int mode)
        {
            if (mode == 5 || (_dc.Clip == null && mode != 4))
            {
                _dc.Clip?.Dispose();
                _dc.Clip = device;
                return;
            }
            Region cur = _dc.Clip ?? new Region();
            switch (mode)
            {
                case 2: cur.Union(device); break;
                case 3: cur.Xor(device); break;
                case 4: cur.Exclude(device); break;
                default: cur.Intersect(device); break;
            }
            device.Dispose();
            _dc.Clip = cur;
        }

        void ClipRectLogical(int l, int t, int r, int b, bool exclude)
        {
            if (Gdi) { GdiClipRect(l, t, r, b, exclude); return; }
            var p = new GraphicsPath();
            p.AddPolygon(ToTarget(new[] { new PointF(l, t), new PointF(r, t), new PointF(r, b), new PointF(l, b) }));
            CombineClip(new Region(p), exclude ? 4 : 1);
            p.Dispose();
        }

        void OffsetClip(int dx, int dy)
        {
            if (Gdi) { GdiOffsetClip(dx, dy); return; }
            if (_dc.Clip == null) return;
            PointF o = ToTarget(new PointF(0, 0)), d = ToTarget(new PointF(dx, dy));
            _dc.Clip.Translate(d.X - o.X, d.Y - o.Y);
        }

        void SetMetaRgn()
        {
            if (Gdi)
            {
                if (_dc.GClip == null) return;
                _dc.GMeta = _dc.GMeta == null ? _dc.GClip : GdiRgn.Combine(_dc.GMeta, _dc.GClip, 1);
                _dc.GClip = null;
                return;
            }
            if (_dc.Clip == null) return;
            if (_dc.MetaClip == null) _dc.MetaClip = _dc.Clip;
            else { _dc.MetaClip.Intersect(_dc.Clip); _dc.Clip.Dispose(); }
            _dc.Clip = null;
        }

        /// <summary>An RGNDATA: rectangles in the metafile's device units, as a region in the target's.</summary>
        Region RgnData(GpReader r, int size)
        {
            if (size < 32) return null;
            r.I32(); r.I32();
            int count = r.I32();
            r.I32();
            r.Rect();
            var rg = new Region();
            rg.MakeEmpty();
            for (int i = 0; i < count && r.Ok; i++)
            {
                int l = r.I32(), t = r.I32(), rr = r.I32(), b = r.I32();
                var pts = new[] { new PointF(l, t), new PointF(rr, t), new PointF(rr, b), new PointF(l, b) };
                _base.Transform(pts);
                using (var p = new GraphicsPath())
                {
                    p.AddPolygon(pts);
                    rg.Union(p);
                }
            }
            return rg;
        }

        void ExtSelectClipRgn(GpReader r)
        {
            int cb = r.I32(), mode = r.I32();
            if (Gdi) { GdiExtSelectClipRgn(r, cb, mode); return; }
            if (mode == 5 && cb < 32)
            {
                _dc.Clip?.Dispose();
                _dc.Clip = null;
                return;
            }
            Region rg = RgnData(r, cb);
            if (rg != null) CombineClip(rg, mode);
        }

        Brush BrushAt(uint ib)
        {
            GdiBrush saved = _dc.Brush;
            if (ib < _objects.Length && _objects[ib] is GdiBrush gb) _dc.Brush = gb;
            else if ((ib & 0x80000000) != 0) _dc.Brush = StockBrush((int)(ib & 0xff));
            Brush b = FillBrush();
            _dc.Brush = saved;
            return b;
        }

        void FillRgn(GpReader r)
        {
            r.Rect();
            int cb = r.I32();
            uint ib = r.U32();
            if (Gdi)
            {
                GdiBrush saved = _dc.Brush;
                if (ib < _objects.Length && _objects[ib] is GdiBrush gb) _dc.Brush = gb;
                else if ((ib & 0x80000000) != 0) _dc.Brush = StockBrush((int)(ib & 0xff));
                GdiFillRgn(r, cb);
                _dc.Brush = saved;
                return;
            }
            Region rg = RgnData(r, cb);
            if (rg == null) return;
            if (!RopDraw(g => { using (Brush b = BrushAt(ib)) if (b != null) g.FillRegion(b, rg); }))
            {
                Prepare();
                using (Brush b = BrushAt(ib)) if (b != null) _t.FillRegion(b, rg);
            }
            rg.Dispose();
        }

        void PaintRgn(GpReader r)
        {
            r.Rect();
            int cb = r.I32();
            if (Gdi) { GdiFillRgn(r, cb); return; }
            Region rg = RgnData(r, cb);
            if (rg == null) return;
            if (!RopDraw(g => { using (Brush b = FillBrush()) if (b != null) g.FillRegion(b, rg); }))
            {
                Prepare();
                using (Brush b = FillBrush()) if (b != null) _t.FillRegion(b, rg);
            }
            rg.Dispose();
        }

        void FrameRgn(GpReader r)
        {
            r.Rect();
            int cb = r.I32();
            uint ib = r.U32();
            int w = r.I32(); r.I32();
            Region rg = RgnData(r, cb);
            if (rg == null) return;
            Prepare();
            using (Brush b = BrushAt(ib))
            using (Matrix id = new Matrix())
                if (b != null)
                    foreach (RectangleF q in rg.GetRegionScans(id))
                        using (var p = new Pen(b, Math.Max(1, w * XScale))) _t.DrawRectangle(p, q.X, q.Y, q.Width, q.Height);
            rg.Dispose();
        }

        // ---- lines and shapes -----------------------------------------------------------------

        void MoveTo(float x, float y)
        {
            _dc.Pos = new PointF(x, y);
            if (Gdi) { GdiMoveTo(); return; }
            if (_inPath) _path.StartFigure();
        }

        void LineTo(float x, float y)
        {
            if (Gdi)
            {
                GdiPath gp = _inPath ? (_gPath ??= new GdiPath()) : new GdiPath();
                GdiAddPoly(gp, new[] { new PointF(x, y) }, false, true, false);
                _dc.Pos = new PointF(x, y);
                if (!_inPath) GdiStroke(gp);
                return;
            }
            var a = new[] { _dc.Pos, new PointF(x, y) };
            _dc.Pos = a[1];
            var p = new GraphicsPath();
            p.AddLines(ToTarget(a));
            if (_inPath) { _path.AddPath(p, true); p.Dispose(); return; }
            Stroke(p);
        }

        static PointF[] ReadPoints(GpReader r, bool sixteen, int count)
        {
            if (count < 0 || count > r.Left) { r.Ok = false; return new PointF[0]; }
            var pts = new PointF[count];
            for (int i = 0; i < count; i++)
            {
                if (sixteen) { short x = r.I16(), y = r.I16(); pts[i] = new PointF(x, y); }
                else { int x = r.I32(), y = r.I32(); pts[i] = new PointF(x, y); }
            }
            return pts;
        }

        // kind: 0 PolyBezier, 1 Polygon, 2 Polyline, 3 PolyBezierTo, 4 PolylineTo.
        void PolyCore(GpReader r, bool sixteen, int kind)
        {
            r.Rect();
            int count = r.I32();
            PointF[] pts = ReadPoints(r, sixteen, count);
            if (!r.Ok || count == 0) return;
            PolyPoints(pts, kind);
        }

        void PolyPoints(PointF[] pts, int kind)
        {
            int count = pts.Length;
            if (Gdi) { GdiPolyPoints(pts, kind); return; }
            var path = new GraphicsPath(PolyFill);
            switch (kind)
            {
                case 0:
                    if (count < 4 || (count - 1) % 3 != 0) return;
                    path.AddBeziers(ToTarget(pts));
                    if (_inPath) { _path.AddPath(path, false); return; }
                    Stroke(path);
                    return;
                case 1:
                    if (count < 2) return;
                    path.AddPolygon(ToTarget(pts));
                    FillAndStroke(path, true, true);
                    path.Dispose();
                    return;
                case 2:
                    if (count < 2) return;
                    path.AddLines(ToTarget(pts));
                    if (_inPath) { _path.AddPath(path, false); return; }
                    Stroke(path);
                    return;
                default:
                    {
                        var all = new PointF[count + 1];
                        all[0] = _dc.Pos;
                        Array.Copy(pts, 0, all, 1, count);
                        _dc.Pos = pts[count - 1];
                        if (kind == 3) { if (count % 3 != 0) return; path.AddBeziers(ToTarget(all)); }
                        else path.AddLines(ToTarget(all));
                        if (_inPath) { _path.AddPath(path, true); return; }
                        Stroke(path);
                        return;
                    }
            }
        }

        void PolyPolyCore(GpReader r, bool sixteen, bool polygon)
        {
            r.Rect();
            int nPolys = r.I32(), total = r.I32();
            if (nPolys < 0 || nPolys > r.Left / 4) return;
            var counts = new int[nPolys];
            for (int i = 0; i < nPolys; i++) counts[i] = r.I32();
            PointF[] pts = ReadPoints(r, sixteen, total);
            if (!r.Ok) return;
            PolyPolyPoints(pts, counts, polygon);
        }

        void PolyPolyPoints(PointF[] pts, int[] counts, bool polygon)
        {
            if (Gdi) { GdiPolyPolyPoints(pts, counts, polygon); return; }
            PointF[] dev = ToTarget(pts);
            var path = new GraphicsPath(PolyFill);
            int at = 0;
            foreach (int c in counts)
            {
                if (c < 0 || at + c > dev.Length) break;
                var part = new PointF[c];
                Array.Copy(dev, at, part, 0, c);
                at += c;
                path.StartFigure();
                if (c >= 2)
                {
                    if (polygon) path.AddPolygon(part);
                    else path.AddLines(part);
                }
            }
            if (polygon) { FillAndStroke(path, true, true); path.Dispose(); }
            else if (_inPath) _path.AddPath(path, false);
            else Stroke(path);
        }

        void PolyDraw(GpReader r, bool sixteen)
        {
            r.Rect();
            int count = r.I32();
            PointF[] pts = ReadPoints(r, sixteen, count);
            var types = new byte[Math.Max(0, count)];
            for (int i = 0; i < count; i++) types[i] = r.U8();
            if (!r.Ok) return;
            if (Gdi) { GdiPolyDraw(pts, types); return; }
            var path = new GraphicsPath(PolyFill);
            PointF cur = _dc.Pos;
            for (int i = 0; i < count; i++)
            {
                int t = types[i] & ~1;
                PointF p = pts[i];
                if (t == 6) { path.StartFigure(); cur = p; }
                else if (t == 2) { path.AddLines(ToTarget(new[] { cur, p })); cur = p; }
                else if (t == 4 && i + 2 < count)
                {
                    path.AddBezier(ToTarget(cur), ToTarget(pts[i]), ToTarget(pts[i + 1]), ToTarget(pts[i + 2]));
                    cur = pts[i + 2];
                    if ((types[i + 2] & 1) != 0) path.CloseFigure();
                    i += 2;
                    continue;
                }
                if ((types[i] & 1) != 0) path.CloseFigure();
            }
            _dc.Pos = cur;
            if (_inPath) { _path.AddPath(path, false); return; }
            Stroke(path);
        }

        // GDI's rectangle-shaped records exclude the right and bottom edge.
        void Box(RectangleF rc, out float l, out float t, out float r, out float b)
        {
            l = Math.Min(rc.Left, rc.Right); r = Math.Max(rc.Left, rc.Right) - 1;
            t = Math.Min(rc.Top, rc.Bottom); b = Math.Max(rc.Top, rc.Bottom) - 1;
        }

        void ShapeRectangle(RectangleF rc)
        {
            if (Gdi)
            {
                // EmfEnumState::Rectangle @1800b5360: GDI+ plays an EMF's rectangle, onto anything
                // but a metafile, as the Polygon through its four corners; a WMF's goes to GDI.
                if (_wmfCanvas) GdiShape(rc, 0, 0, 0);
                else GdiPolyPoints(new[] { new PointF(rc.Left, rc.Top), new PointF(rc.Right, rc.Top), new PointF(rc.Right, rc.Bottom), new PointF(rc.Left, rc.Bottom) }, 1);
                return;
            }
            Box(rc, out float l, out float t, out float r, out float b);
            using (var p = new GraphicsPath(PolyFill))
            {
                p.AddPolygon(ToTarget(new[] { new PointF(l, t), new PointF(r, t), new PointF(r, b), new PointF(l, b) }));
                FillAndStroke(p, true, true);
            }
        }

        void ShapeEllipse(RectangleF rc)
        {
            if (Gdi) { GdiShape(rc, 1, 0, 0); return; }
            Box(rc, out float l, out float t, out float r, out float b);
            using (var p = new GraphicsPath(PolyFill))
            {
                p.AddEllipse(l, t, r - l, b - t);
                using (Matrix m = LogicalToTarget.ToMatrix()) p.Transform(m);
                FillAndStroke(p, true, true);
            }
        }

        void ShapeRoundRect(RectangleF rc, int cx, int cy)
        {
            if (Gdi) { GdiShape(rc, 2, cx, cy); return; }
            Box(rc, out float l, out float t, out float r, out float b);
            float w = Math.Min(Math.Abs(cx), r - l), h = Math.Min(Math.Abs(cy), b - t);
            using (var p = new GraphicsPath(PolyFill))
            {
                if (w <= 0 || h <= 0) p.AddRectangle(RectangleF.FromLTRB(l, t, r, b));
                else
                {
                    p.AddArc(l, t, w, h, 180, 90);
                    p.AddArc(r - w, t, w, h, 270, 90);
                    p.AddArc(r - w, b - h, w, h, 0, 90);
                    p.AddArc(l, b - h, w, h, 90, 90);
                    p.CloseFigure();
                }
                using (Matrix m = LogicalToTarget.ToMatrix()) p.Transform(m);
                FillAndStroke(p, true, true);
            }
        }

        static float Angle(float cx, float cy, float x, float y) => (float)(Math.Atan2(y - cy, x - cx) * 180 / Math.PI);

        static PointF ArcPoint(float x, float y, float w, float h, float deg)
        {
            double a = deg * Math.PI / 180;
            return new PointF((float)(x + w / 2 + w / 2 * Math.Cos(a)), (float)(y + h / 2 + h / 2 * Math.Sin(a)));
        }

        // kind: 0 Arc, 1 Chord, 2 Pie, 3 ArcTo. From the radial through the start point to the one
        // through the end point, counter-clockwise (on the screen) unless AD_CLOCKWISE.
        void ArcKind(GpReader r, int kind)
        {
            RectangleF rc = RectL(r);
            float sx = r.I32(), sy = r.I32(), ex = r.I32(), ey = r.I32();
            if (!r.Ok) return;
            ArcShape(rc, sx, sy, ex, ey, kind);
        }

        void ArcShape(RectangleF rc, float sx, float sy, float ex, float ey, int kind)
        {
            Box(rc, out float l, out float t, out float rr, out float b);
            if (rr - l <= 0 || b - t <= 0) return;
            float cx = (l + rr) / 2, cy = (t + b) / 2;
            float a0 = Angle(cx, cy, sx, sy), a1 = Angle(cx, cy, ex, ey);
            float sweep = a1 - a0;
            if (_dc.ArcDirection == 2) { if (sweep <= 0) sweep += 360; }
            else { if (sweep >= 0) sweep -= 360; }
            var p = new GraphicsPath(PolyFill);
            if (kind == 2) p.AddPie(l, t, rr - l, b - t, a0, sweep);
            else
            {
                if (kind == 3) p.AddLine(_dc.Pos, ArcPoint(l, t, rr - l, b - t, a0));
                p.AddArc(l, t, rr - l, b - t, a0, sweep);
                if (kind == 1) p.CloseFigure();
            }
            if (kind == 3) _dc.Pos = ArcPoint(l, t, rr - l, b - t, a0 + sweep);
            using (Matrix m = LogicalToTarget.ToMatrix()) p.Transform(m);
            if (kind == 0 || kind == 3)
            {
                if (_inPath) { _path.AddPath(p, kind == 3); p.Dispose(); return; }
                Stroke(p);
                return;
            }
            FillAndStroke(p, true, true);
            p.Dispose();
        }

        void AngleArc(GpReader r)
        {
            int cx = r.I32(), cy = r.I32();
            int radius = r.I32();
            float start = r.F(), sweep = r.F();
            var p = new GraphicsPath();
            float d = radius * 2;
            p.AddLine(_dc.Pos, ArcPoint(cx - radius, cy - radius, d, d, -start));
            p.AddArc(cx - radius, cy - radius, d, d, -start, -sweep);
            _dc.Pos = ArcPoint(cx - radius, cy - radius, d, d, -start - sweep);
            using (Matrix m = LogicalToTarget.ToMatrix()) p.Transform(m);
            if (_inPath) { _path.AddPath(p, true); p.Dispose(); return; }
            Stroke(p);
        }

        // ---- text ---------------------------------------------------------------------------------

        Font MakeFont(GdiFont f, out float ascentPx, out float heightPx)
        {
            float h = Math.Abs(f.Height) * YScale;
            if (h <= 0) h = 12 * YScale;
            var style = FontStyle.Regular;
            if (f.Weight >= 600) style |= FontStyle.Bold;
            if (f.Italic) style |= FontStyle.Italic;
            if (f.Underline) style |= FontStyle.Underline;
            if (f.StrikeOut) style |= FontStyle.Strikeout;
            string face = string.IsNullOrEmpty(f.Face) || f.Face == "System" ? "Arial" : f.Face;
            FontFamily fam;
            try { fam = new FontFamily(face); }
            catch (ArgumentException) { fam = new FontFamily("Arial"); }
            float em = fam.GetEmHeight(style), asc = fam.GetCellAscent(style), desc = fam.GetCellDescent(style);
            // lfHeight < 0: the em height; > 0: the cell height (ascent + descent).
            float emPx = f.Height < 0 || asc + desc <= 0 ? h : h * em / (asc + desc);
            ascentPx = em > 0 ? emPx * asc / em : emPx * 0.9f;
            heightPx = em > 0 ? emPx * (asc + desc) / em : emPx * 1.15f;
            return new Font(fam, Math.Max(0.5f, emPx), style, GraphicsUnit.Pixel);
        }

        void DrawText(string s, PointF logical, int options, RectangleF? clipRect, int[] dx)
        {
            if (string.IsNullOrEmpty(s) || _dc.Font == null) return;
            Prepare();
            int align = _dc.TextAlign;
            bool updateCp = (align & 1) != 0;
            PointF origin = updateCp ? _dc.Pos : logical;
            PointF dev = ToTarget(origin);
            using (Font font = MakeFont(_dc.Font, out float ascent, out float height))
            using (var sf = new StringFormat(StringFormat.GenericTypographic))
            {
                sf.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces | StringFormatFlags.NoWrap | StringFormatFlags.NoClip;
                float width = 0;
                float[] adv = null;
                if (dx != null && dx.Length >= s.Length)
                {
                    adv = new float[s.Length];
                    for (int i = 0; i < s.Length; i++) { adv[i] = dx[i] * XScale; width += adv[i]; }
                }
                else width = _t.MeasureString(s, font, PointF.Empty, sf).Width;
                float x = dev.X, y = dev.Y;
                int h = align & 6;
                if (h == 6) x -= width / 2; else if (h == 2) x -= width;
                int v = align & 24;
                if (v == 24) y -= ascent; else if (v == 8) y -= height;
                GraphicsState st = _t.Save();
                try
                {
                    if (_dc.Font.Escapement != 0)
                    {
                        _t.TranslateTransform(dev.X, dev.Y);
                        _t.RotateTransform(-_dc.Font.Escapement / 10f);
                        _t.TranslateTransform(-dev.X, -dev.Y);
                    }
                    RectangleF? devClip = null;
                    if (clipRect.HasValue)
                    {
                        RectangleF c = clipRect.Value;
                        PointF[] cr = ToTarget(new[] { new PointF(c.Left, c.Top), new PointF(c.Right, c.Bottom) });
                        devClip = RectangleF.FromLTRB(Math.Min(cr[0].X, cr[1].X), Math.Min(cr[0].Y, cr[1].Y), Math.Max(cr[0].X, cr[1].X), Math.Max(cr[0].Y, cr[1].Y));
                    }
                    if (devClip.HasValue && (options & 4) != 0) _t.IntersectClip(devClip.Value);       // ETO_CLIPPED
                    if (devClip.HasValue && (options & 2) != 0)                                     // ETO_OPAQUE
                        using (var bb = new SolidBrush(_dc.BkColor)) _t.FillRectangle(bb, devClip.Value);
                    else if (_dc.BkMode == 2)
                        using (var bb = new SolidBrush(_dc.BkColor)) _t.FillRectangle(bb, x, y, width, height);
                    using (var tb = new SolidBrush(_dc.TextColor))
                    {
                        if (adv == null) _t.DrawString(s, font, tb, x, y, sf);
                        else
                        {
                            float cx = x;
                            for (int i = 0; i < s.Length; i++)
                            {
                                _t.DrawString(s[i].ToString(), font, tb, cx, y, sf);
                                cx += adv[i];
                            }
                        }
                    }
                }
                finally { _t.Restore(st); }
                if (updateCp)
                    _dc.Pos = new PointF(_dc.Pos.X + width / Math.Max(1e-6f, XScale), _dc.Pos.Y);
            }
        }

        // EMR_EXTTEXTOUTW / A: rclBounds, iGraphicsMode, exScale, eyScale, then the EMRTEXT.
        void ExtTextOut(GpReader r, byte[] b, int o, bool wide)
        {
            r.Rect(); r.I32(); r.F(); r.F();
            TextRecord(r, b, o, wide);
        }

        void TextRecord(GpReader r, byte[] b, int o, bool wide)
        {
            int x = r.I32(), y = r.I32();
            int nChars = r.I32(), offString = r.I32(), options = r.I32();
            int l = r.I32(), t = r.I32(), rr = r.I32(), bb = r.I32();
            int offDx = r.I32();
            if (!r.Ok || nChars < 0 || nChars > 0x10000) return;
            int so = o - 8 + offString;
            string s;
            if (wide)
            {
                if (so < 0 || so + nChars * 2 > b.Length) return;
                s = Encoding.Unicode.GetString(b, so, nChars * 2);
            }
            else
            {
                if (so < 0 || so + nChars > b.Length) return;
                s = Encoding.Latin1.GetString(b, so, nChars);
            }
            int[] dx = null;
            int dO = o - 8 + offDx;
            int step = (options & 0x2000) != 0 ? 8 : 4;    // ETO_PDY: dx and dy pairs
            if (offDx > 0 && dO >= 0 && dO + nChars * step <= b.Length)
            {
                dx = new int[nChars];
                for (int i = 0; i < nChars; i++) dx[i] = Le.I32(b, dO + i * step);
            }
            RectangleF? clip = (options & 6) != 0 ? RectangleF.FromLTRB(l, t, rr, bb) : (RectangleF?)null;
            DrawText(s, new PointF(x, y), options, clip, dx);
        }

        void PolyTextOut(GpReader r, byte[] b, int o, bool wide)
        {
            r.Rect(); r.I32(); r.F(); r.F();
            int n = r.I32();
            for (int i = 0; i < n && r.Ok; i++) TextRecord(r, b, o, wide);
        }

        // ---- bitmaps --------------------------------------------------------------------------------

        /// <summary>A DIB (BITMAPINFO at offBmi, bits at offBits, from the record's start) as a Bitmap.</summary>
        internal static Bitmap Dib(byte[] b, int rec, int offBmi, int cbBmi, int offBits, int cbBits)
        {
            if (cbBmi < 12 || offBmi <= 0 || rec + offBmi + cbBmi > b.Length) return null;
            if (cbBits < 0 || offBits < 0 || rec + offBits + cbBits > b.Length) return null;
            return DibFromInfo(b, rec + offBmi, cbBmi, b, rec + offBits, cbBits);
        }

        internal static Bitmap DibFromInfo(byte[] info, int io, int cbInfo, byte[] bits, int bo, int cbBits)
        {
            int size = 14 + cbInfo + cbBits;
            var f = new byte[size];
            f[0] = (byte)'B'; f[1] = (byte)'M';
            Le.W32(f, 2, size);
            Le.W32(f, 10, 14 + cbInfo);
            Buffer.BlockCopy(info, io, f, 14, cbInfo);
            if (cbBits > 0) Buffer.BlockCopy(bits, bo, f, 14 + cbInfo, cbBits);
            try
            {
                using (var im = Image.FromBytes(f))
                    return new Bitmap(im);
            }
            catch (Exception e) when (e is ArgumentException || e is OutOfMemoryException || e is NotSupportedException || e is IndexOutOfRangeException || e is InvalidOperationException)
            {
                return null;
            }
        }

        void Blit(Bitmap bm, float xDest, float yDest, float cxDest, float cyDest, RectangleF src, int rop)
        {
            PointF[] d = ToTarget(new[] { new PointF(xDest, yDest), new PointF(xDest + cxDest, yDest), new PointF(xDest, yDest + cyDest) });
            if (Canvas && RasterBlit(bm, d, src, rop, _dibBlit)) return;
            if (rop == 0x00AA0029) return;                                   // DSTCOPY: nothing
            if (bm == null && rop == 0x005A0049)                             // PATINVERT: cancelled by the next
            {
                _xor = _xor == null ? _dc.Brush : null;
                return;
            }
            if (bm != null && rop == 0x00660046)                             // SRCINVERT: likewise, a bitmap
            {
                if (_xorImage == null)
                {
                    _xorImage = (Bitmap)bm.Clone();
                    _xorImageDest = d;
                    _xorImageSrc = src;
                    return;
                }
                Bitmap img = _xorImage, mk = _xorImageMask;
                _xorImage = null; _xorImageMask = null;
                if (mk != null)
                {
                    DrawThroughPattern(img, _xorImageDest, _xorImageSrc, mk);
                    mk.Dispose();
                }
                img.Dispose();
                return;
            }
            if (bm != null && rop == 0x00EE0086 && IsTwoTone(bm))           // SRCPAINT: a mask, for the SRCAND next
            {
                _mask?.Dispose();
                _mask = (Bitmap)bm.Clone();
                _maskDest = d;
                return;
            }
            if (bm != null && rop == 0x008800C6 && _mask != null)            // SRCAND after SRCPAINT's mask
            {
                Bitmap mk = _mask;
                _mask = null;
                if (_dc.StretchMode == 4)
                {
                    // HALFTONE: the stretched mask is copied, the stretched colours ANDed onto it.
                    using (Bitmap sm = Stretched(mk, d, src, out int bx, out int by, InterpolationMode.NearestNeighbor))
                    using (Bitmap si = Stretched(bm, d, src, out _, out _, InterpolationMode.HighQualityBilinear))
                    {
                        if (sm == null || si == null) { mk.Dispose(); return; }
                        for (int y = 0; y < sm.Height; y++)
                            for (int x = 0; x < sm.Width; x++)
                            {
                                Color a = sm.GetPixel(x, y), c = si.GetPixel(x, y);
                                if (a.A == 0) continue;
                                sm.SetPixel(x, y, a.GetBrightness() >= 0.5f ? Color.FromArgb(255, c) : Color.Black);
                            }
                        Prepare();
                        _t.DrawImageUnscaled(sm, bx, by);
                    }
                    mk.Dispose();
                    return;
                }
                using (var merged = new Bitmap(bm.Width, bm.Height, PixelFormat.Format32bppArgb))
                {
                    for (int y = 0; y < bm.Height; y++)
                        for (int x = 0; x < bm.Width; x++)
                        {
                            bool on = x < mk.Width && y < mk.Height && mk.GetPixel(x, y).GetBrightness() >= 0.5f;
                            Color c = bm.GetPixel(x, y);
                            merged.SetPixel(x, y, on ? Color.FromArgb(255, c) : Color.Transparent);
                        }
                    Prepare();
                    using (var ia = new ImageAttributes())
                    {
                        ia.SetWrapMode(WrapMode.TileFlipXY);
                        _t.DrawImage(merged, d, src, GraphicsUnit.Pixel, ia);
                    }
                }
                mk.Dispose();
                return;
            }
            Prepare();
            if (bm == null)
            {
                Brush br = null;
                if (rop == 0x00A000C9) br = MaskFillBrush();                // DPa: the mask idiom
                else if (rop == 0x00F00021) br = FillBrush();                // PATCOPY
                else if (rop == 0x00000042) br = new SolidBrush(Color.Black); // BLACKNESS
                else if (rop == 0x00FF0062) br = new SolidBrush(Color.White); // WHITENESS
                if (br == null) return;
                using (br)
                using (var p = new GraphicsPath())
                {
                    p.AddPolygon(new[] { d[0], d[1], new PointF(d[1].X + d[2].X - d[0].X, d[1].Y + d[2].Y - d[0].Y), d[2] });
                    _t.FillPath(br, p);
                }
                return;
            }
            if (rop == 0x00330008)                                           // NOTSRCCOPY
                using (var ia = Inverted()) _t.DrawImage(bm, d, src, GraphicsUnit.Pixel, ia);
            else
                using (var ia = new ImageAttributes())
                {
                    ia.SetWrapMode(WrapMode.TileFlipXY);
                    _t.DrawImage(bm, d, src, GraphicsUnit.Pixel, ia);
                }
        }

        static bool IsTwoTone(Bitmap bm)
        {
            for (int y = 0; y < bm.Height; y++)
                for (int x = 0; x < bm.Width; x++)
                {
                    Color c = bm.GetPixel(x, y);
                    if (!(c.R == 0 && c.G == 0 && c.B == 0) && !(c.R == 255 && c.G == 255 && c.B == 255)) return false;
                }
            return true;
        }

        /// <summary>The source stretched onto the parallelogram, in a bitmap of its box in target pixels.</summary>
        Bitmap Stretched(Bitmap img, PointF[] d, RectangleF src, out int bx, out int by, InterpolationMode mode)
        {
            float minx = Math.Min(Math.Min(d[0].X, d[1].X), Math.Min(d[2].X, d[1].X + d[2].X - d[0].X));
            float maxx = Math.Max(Math.Max(d[0].X, d[1].X), Math.Max(d[2].X, d[1].X + d[2].X - d[0].X));
            float miny = Math.Min(Math.Min(d[0].Y, d[1].Y), Math.Min(d[2].Y, d[1].Y + d[2].Y - d[0].Y));
            float maxy = Math.Max(Math.Max(d[0].Y, d[1].Y), Math.Max(d[2].Y, d[1].Y + d[2].Y - d[0].Y));
            bx = (int)Math.Floor(minx); by = (int)Math.Floor(miny);
            int bw = (int)Math.Ceiling(maxx) - bx, bh = (int)Math.Ceiling(maxy) - by;
            if (bw <= 0 || bh <= 0 || bw > 8192 || bh > 8192) return null;
            var tmp = new Bitmap(bw, bh, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(tmp))
            {
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.InterpolationMode = mode;
                var dd = new[] { new PointF(d[0].X - bx, d[0].Y - by), new PointF(d[1].X - bx, d[1].Y - by), new PointF(d[2].X - bx, d[2].Y - by) };
                using (var ia = new ImageAttributes())
                {
                    ia.SetWrapMode(WrapMode.TileFlipXY);
                    g.DrawImage(img, dd, src, GraphicsUnit.Pixel, ia);
                }
            }
            return tmp;
        }

        /// <summary>The image where the pattern (tiled in target pixels) is black, the target elsewhere.</summary>
        void DrawThroughPattern(Bitmap img, PointF[] d, RectangleF src, Bitmap pattern)
        {
            float minx = Math.Min(Math.Min(d[0].X, d[1].X), Math.Min(d[2].X, d[1].X + d[2].X - d[0].X));
            float maxx = Math.Max(Math.Max(d[0].X, d[1].X), Math.Max(d[2].X, d[1].X + d[2].X - d[0].X));
            float miny = Math.Min(Math.Min(d[0].Y, d[1].Y), Math.Min(d[2].Y, d[1].Y + d[2].Y - d[0].Y));
            float maxy = Math.Max(Math.Max(d[0].Y, d[1].Y), Math.Max(d[2].Y, d[1].Y + d[2].Y - d[0].Y));
            int bx = (int)Math.Floor(minx), by = (int)Math.Floor(miny);
            int bw = (int)Math.Ceiling(maxx) - bx, bh = (int)Math.Ceiling(maxy) - by;
            if (bw <= 0 || bh <= 0 || bw > 8192 || bh > 8192) return;
            using (var tmp = new Bitmap(bw, bh, PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(tmp))
                {
                    g.PixelOffsetMode = PixelOffsetMode.Half;
                    g.InterpolationMode = _dc.StretchMode == 4 ? InterpolationMode.HighQualityBilinear : InterpolationMode.NearestNeighbor;
                    var dd = new[] { new PointF(d[0].X - bx, d[0].Y - by), new PointF(d[1].X - bx, d[1].Y - by), new PointF(d[2].X - bx, d[2].Y - by) };
                    using (var ia = new ImageAttributes())
                    {
                        ia.SetWrapMode(WrapMode.TileFlipXY);
                        g.DrawImage(img, dd, src, GraphicsUnit.Pixel, ia);
                    }
                }
                int pw = pattern.Width, ph = pattern.Height;
                for (int y = 0; y < bh; y++)
                    for (int x = 0; x < bw; x++)
                    {
                        int px = ((bx + x) % pw + pw) % pw, py = ((by + y) % ph + ph) % ph;
                        if (pattern.GetPixel(px, py).GetBrightness() >= 0.5f) tmp.SetPixel(x, y, Color.Transparent);
                    }
                Prepare();
                _t.DrawImageUnscaled(tmp, bx, by);
            }
        }

        static ImageAttributes Inverted()
        {
            var ia = new ImageAttributes();
            ia.SetColorMatrix(new ColorMatrix(new[]
            {
                new float[] { -1, 0, 0, 0, 0 }, new float[] { 0, -1, 0, 0, 0 }, new float[] { 0, 0, -1, 0, 0 },
                new float[] { 0, 0, 0, 1, 0 }, new float[] { 1, 1, 1, 0, 1 },
            }));
            return ia;
        }

        void BitBlt(GpReader r, byte[] b, int o)
        {
            r.Rect();
            int xd = r.I32(), yd = r.I32(), cx = r.I32(), cy = r.I32(), rop = r.I32();
            int xs = r.I32(), ys = r.I32();
            r.Matrix6(); r.I32(); r.I32();
            int offBmi = r.I32(), cbBmi = r.I32(), offBits = r.I32(), cbBits = r.I32();
            Bitmap bm = cbBmi > 0 ? Dib(b, o - 8, offBmi, cbBmi, offBits, cbBits) : null;
            _srcBpp = BitCount(b, o - 8, offBmi, cbBmi);
            Blit(bm, xd, yd, cx, cy, new RectangleF(xs, ys, cx, cy), rop);
            bm?.Dispose();
        }

        void StretchBlt(GpReader r, byte[] b, int o)
        {
            r.Rect();
            int xd = r.I32(), yd = r.I32(), cx = r.I32(), cy = r.I32(), rop = r.I32();
            int xs = r.I32(), ys = r.I32();
            r.Matrix6(); r.I32(); r.I32();
            int offBmi = r.I32(), cbBmi = r.I32(), offBits = r.I32(), cbBits = r.I32();
            int cxs = r.I32(), cys = r.I32();
            Bitmap bm = cbBmi > 0 ? Dib(b, o - 8, offBmi, cbBmi, offBits, cbBits) : null;
            _srcBpp = BitCount(b, o - 8, offBmi, cbBmi);
            Blit(bm, xd, yd, cx, cy, new RectangleF(xs, ys, cxs, cys), rop);
            bm?.Dispose();
        }

        bool _dibBlit;

        static int BitCount(byte[] b, int rec, int offBmi, int cbBmi)
            => cbBmi >= 16 && rec + offBmi + 16 <= b.Length ? Le.U16(b, rec + offBmi + 14) : 0;

        void StretchDIBits(GpReader r, byte[] b, int o)
        {
            r.Rect();
            int xd = r.I32(), yd = r.I32(), xs = r.I32(), ys = r.I32(), cxs = r.I32(), cys = r.I32();
            int offBmi = r.I32(), cbBmi = r.I32(), offBits = r.I32(), cbBits = r.I32();
            r.I32();
            int rop = r.I32(), cxd = r.I32(), cyd = r.I32();
            Bitmap bm = Dib(b, o - 8, offBmi, cbBmi, offBits, cbBits);
            _srcBpp = BitCount(b, o - 8, offBmi, cbBmi);
            _dibBlit = true;
            try
            {
                if (bm == null) { Blit(null, xd, yd, cxd, cyd, RectangleF.Empty, rop); return; }
                // The source counts rows from the DIB's bottom.
                Blit(bm, xd, yd, cxd, cyd, new RectangleF(xs, bm.Height - ys - cys, cxs, cys), rop);
            }
            finally { _dibBlit = false; }
            bm.Dispose();
        }

        void SetDIBitsToDevice(GpReader r, byte[] b, int o)
        {
            r.Rect();
            int xd = r.I32(), yd = r.I32(), xs = r.I32(), ys = r.I32(), cx = r.I32(), cy = r.I32();
            int offBmi = r.I32(), cbBmi = r.I32(), offBits = r.I32(), cbBits = r.I32();
            Bitmap bm = Dib(b, o - 8, offBmi, cbBmi, offBits, cbBits);
            if (bm == null) return;
            _srcBpp = BitCount(b, o - 8, offBmi, cbBmi);
            Blit(bm, xd, yd, cx, cy, new RectangleF(xs, bm.Height - ys - cy, cx, cy), 0x00CC0020);
            bm.Dispose();
        }

        void AlphaBlend(GpReader r, byte[] b, int o)
        {
            r.Rect();
            int xd = r.I32(), yd = r.I32(), cx = r.I32(), cy = r.I32();
            int blend = r.I32();
            int xs = r.I32(), ys = r.I32();
            r.Matrix6(); r.I32(); r.I32();
            int offBmi = r.I32(), cbBmi = r.I32(), offBits = r.I32(), cbBits = r.I32();
            int cxs = r.I32(), cys = r.I32();
            Bitmap bm = Dib(b, o - 8, offBmi, cbBmi, offBits, cbBits);
            if (bm == null) return;
            int sca = (blend >> 16) & 0xff;
            PointF[] d = ToTarget(new[] { new PointF(xd, yd), new PointF(xd + cx, yd), new PointF(xd, yd + cy) });
            Prepare();
            using (var ia = new ImageAttributes())
            {
                ia.SetColorMatrix(new ColorMatrix { Matrix33 = sca / 255f });
                _t.DrawImage(bm, d, new RectangleF(xs, ys, cxs, cys), GraphicsUnit.Pixel, ia);
            }
            bm.Dispose();
        }

        void TransparentBlt(GpReader r, byte[] b, int o)
        {
            r.Rect();
            int xd = r.I32(), yd = r.I32(), cx = r.I32(), cy = r.I32();
            int key = r.I32();
            int xs = r.I32(), ys = r.I32();
            r.Matrix6(); r.I32(); r.I32();
            int offBmi = r.I32(), cbBmi = r.I32(), offBits = r.I32(), cbBits = r.I32();
            int cxs = r.I32(), cys = r.I32();
            Bitmap bm = Dib(b, o - 8, offBmi, cbBmi, offBits, cbBits);
            if (bm == null) return;
            PointF[] d = ToTarget(new[] { new PointF(xd, yd), new PointF(xd + cx, yd), new PointF(xd, yd + cy) });
            Prepare();
            using (var ia = new ImageAttributes())
            {
                Color k = ColorRef(key);
                ia.SetColorKey(k, k);
                _t.DrawImage(bm, d, new RectangleF(xs, ys, cxs, cys), GraphicsUnit.Pixel, ia);
            }
            bm.Dispose();
        }

        // EMR_GRADIENTFILL: TRIVERTEX colours (16 bits a channel) over rectangles or triangles.
        void GradientFill(GpReader r)
        {
            r.Rect();
            int nVer = r.I32(), nTri = r.I32(), mode = r.I32();
            if (nVer < 0 || nVer > r.Left / 16) return;
            var pts = new PointF[nVer];
            var cols = new Color[nVer];
            for (int i = 0; i < nVer; i++)
            {
                int x = r.I32(), y = r.I32();
                int red = (ushort)r.I16() >> 8, green = (ushort)r.I16() >> 8, blue = (ushort)r.I16() >> 8;
                r.I16();
                pts[i] = new PointF(x, y);
                cols[i] = Color.FromArgb(255, red, green, blue);
            }
            PointF[] dev = ToTarget(pts);
            Prepare();
            if (mode == 2)
            {
                for (int i = 0; i < nTri && r.Ok; i++)
                {
                    int a = r.I32(), bb = r.I32(), c = r.I32();
                    if (a >= nVer || bb >= nVer || c >= nVer || a < 0 || bb < 0 || c < 0) continue;
                    var tri = new[] { dev[a], dev[bb], dev[c] };
                    try
                    {
                        using (var pg = new PathGradientBrush(tri))
                        {
                            pg.SurroundColors = new[] { cols[a], cols[bb], cols[c] };
                            pg.CenterColor = Color.FromArgb((cols[a].R + cols[bb].R + cols[c].R) / 3, (cols[a].G + cols[bb].G + cols[c].G) / 3, (cols[a].B + cols[bb].B + cols[c].B) / 3);
                            _t.FillPolygon(pg, tri);
                        }
                    }
                    catch (OutOfMemoryException) { }
                }
                return;
            }
            for (int i = 0; i < nTri && r.Ok; i++)
            {
                int ul = r.I32(), lr = r.I32();
                if (ul >= nVer || lr >= nVer || ul < 0 || lr < 0) continue;
                RectangleF rc = RectangleF.FromLTRB(Math.Min(dev[ul].X, dev[lr].X), Math.Min(dev[ul].Y, dev[lr].Y), Math.Max(dev[ul].X, dev[lr].X), Math.Max(dev[ul].Y, dev[lr].Y));
                if (rc.Width <= 0 || rc.Height <= 0) continue;
                using (var lg = new LinearGradientBrush(rc, cols[ul], cols[lr], mode == 1 ? LinearGradientMode.Vertical : LinearGradientMode.Horizontal))
                    _t.FillRectangle(lg, rc);
            }
        }
    }
}
