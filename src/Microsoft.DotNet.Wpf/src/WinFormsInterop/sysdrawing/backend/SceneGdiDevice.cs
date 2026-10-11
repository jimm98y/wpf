// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Recorded pages replayed on a Windows printer DC, as VECTOR GDI.
//
// Windows' spooler takes GDI, not a document (see PlatformPrint's TakesRenderedDocument), so on
// Windows a WinForms page is not written as PDF: the same scene the PDF writer walks is put down on
// the printer's DC verb by verb, which is what WPF's GdiDevice does for a Visual. Nothing here is
// GDI+. What GDI+ used to do with the DC -- map its page units to device pixels, stroke with a pen,
// pick a font -- is done in managed code above, and GDI is handed device-pixel paths, geometric
// pens and glyph indices:
//
//   * coordinates are transformed in managed doubles and reach GDI as device pixels (GDI's own world
//     transform would quantise the page's hundredths of an inch);
//   * fills and clips are paths (BeginPath ... FillPath / SelectClipPath), Beziers as PolyBezierTo;
//   * strokes are GEOMETRIC pens with the pen's width, caps, join, miter limit and dash pattern, so a
//     line stays a line in the spooled job rather than becoming a filled outline;
//   * text is ExtTextOut(ETO_GLYPH_INDEX) of the face's own glyph ids, the face selected by name and
//     then VERIFIED -- its name and its glyph count -- because glyph indices into the wrong face are
//     garbage rather than wrong letters; a run GDI will not give the right face for is filled as
//     outlines instead. This is what makes "Microsoft Print to PDF" produce searchable text;
//   * gradients are GradientFill triangles inside a path clip, images StretchDIBits;
//   * a printer has no alpha (SHADEBLENDCAPS is 0), so translucency is flattened onto the white of
//     the paper, as WPF's alpha flattener does.
//
// The DC itself -- created from the printer settings' DEVMODE, StartDoc with print-to-file honoured
// -- is the print controller's (PrintingServicesWin32); this is only the drawing.
//

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Wpf.Interop.WebGpu.Composition;
using Microsoft.Wpf.Interop.WebGpu.Composition.Text;
using SceneBrush = Microsoft.Wpf.Interop.WebGpu.Composition.Brush;
using SceneLinear = Microsoft.Wpf.Interop.WebGpu.Composition.LinearGradientBrush;

namespace System.Drawing.WebGpuBackend
{
    [SupportedOSPlatform("windows")]
    internal sealed class SceneGdiDevice : IDisposable
    {
        private readonly IntPtr _dc;
        private readonly float _dpiX, _dpiY;
        private readonly Dictionary<(string, int, int, bool), IntPtr> _fonts = new();
        private readonly Dictionary<(string, int, bool), bool> _fontOk = new();

        internal SceneGdiDevice(IntPtr dc)
        {
            _dc = dc;
            _dpiX = Native.GetDeviceCaps(dc, Native.LOGPIXELSX);
            _dpiY = Native.GetDeviceCaps(dc, Native.LOGPIXELSY);
            if (_dpiX <= 0) _dpiX = 600;
            if (_dpiY <= 0) _dpiY = 600;
        }

        /// <summary>Puts one recorded page (hundredths of an inch, origin at the printable area's
        /// top left, which is the DC's origin) on the DC. The caller brackets it with StartPage and
        /// EndPage.</summary>
        internal void DrawPage(object scene)
        {
            if (scene is not SceneVisual root) return;
            int saved = Native.SaveDC(_dc);
            Native.SetBkMode(_dc, Native.TRANSPARENT);
            Native.SetGraphicsMode(_dc, Native.GM_ADVANCED);
            var sink = new GdiPageSink(this, Matrix3x2.CreateScale(_dpiX / 100f, _dpiY / 100f));
            ScenePageWalker.Walk(root, sink);
            sink.EndPage();
            Native.RestoreDC(_dc, saved);
        }

        public void Dispose()
        {
            foreach (IntPtr f in _fonts.Values) if (f != IntPtr.Zero) Native.DeleteObject(f);
            _fonts.Clear();
        }

        // ---- fonts -----------------------------------------------------------------------------

        private static readonly HashSet<string> s_installed = new(StringComparer.OrdinalIgnoreCase);

        // An HFONT for a family and style at an em in device pixels, or zero when GDI would give a
        // different face than the one the glyphs were laid out in.
        private IntPtr Font(PageText run, int emPixels)
        {
            string family = run.Family;
            if (string.IsNullOrEmpty(family) && run.Face != null
                && FontFiles.ReadNames(run.Face.FontData, run.Face.SfntOffset, out string fam, out _, out _))
                family = fam;
            if (string.IsNullOrEmpty(family)) return IntPtr.Zero;
            bool bold = (run.Style & 1) != 0, italic = (run.Style & 2) != 0;
            var key = (family, emPixels, run.Style, run.Face.SynthesizesBold);
            if (_fonts.TryGetValue(key, out IntPtr cached)) return cached;

            IntPtr font = Create(family, emPixels, bold, italic);
            if (font != IntPtr.Zero && !Verify(font, family, run.Face))
            {
                // This port reads fonts from their files, not from GDI's collection, so a face it
                // knows may be one GDI does not: make the file nameable for this process and ask again.
                Native.DeleteObject(font);
                font = IntPtr.Zero;
                string path = FontFiles.Find(family, bold, italic);
                bool added = false;
                if (path != null)
                    lock (s_installed)
                        if (s_installed.Add(path)) added = Native.AddFontResourceEx(path, Native.FR_PRIVATE, IntPtr.Zero) > 0;
                if (added)
                {
                    font = Create(family, emPixels, bold, italic);
                    if (font != IntPtr.Zero && !Verify(font, family, run.Face)) { Native.DeleteObject(font); font = IntPtr.Zero; }
                }
            }
            _fonts[key] = font;
            return font;
        }

        private static IntPtr Create(string family, int emPixels, bool bold, bool italic)
        {
            var lf = new Native.LOGFONT
            {
                // Negative asks for the EM, not the cell height.
                lfHeight = -Math.Max(1, emPixels),
                lfWeight = bold ? 700 : 400,
                lfItalic = (byte)(italic ? 1 : 0),
                lfCharSet = 1,                  // DEFAULT_CHARSET
                lfOutPrecision = 7,             // OUT_TT_ONLY_PRECIS: a bitmap face has no glyph indices
                lfQuality = 4,                  // ANTIALIASED_QUALITY
                lfFaceName = family.Length >= 32 ? family.Substring(0, 31) : family,
            };
            return Native.CreateFontIndirect(ref lf);
        }

        // The face GDI selected is the one asked for: by name, and by the glyph count of its live
        // 'maxp', which is what catches a different cut of the same family.
        private bool Verify(IntPtr font, string family, TrueTypeFont face)
        {
            IntPtr previous = Native.SelectObject(_dc, font);
            try
            {
                var name = new System.Text.StringBuilder(32);
                if (Native.GetTextFace(_dc, 32, name) == 0) return false;
                if (!string.Equals(name.ToString(), family.Length >= 32 ? family.Substring(0, 31) : family, StringComparison.OrdinalIgnoreCase))
                    return false;
                var maxp = new byte[2];
                if (Native.GetFontData(_dc, 0x7078616D /* 'maxp' */, 4, maxp, 2) != 2) return false;
                return ((maxp[0] << 8) | maxp[1]) == face.GlyphCount;
            }
            finally
            {
                Native.SelectObject(_dc, previous);
            }
        }

        // ---- the page --------------------------------------------------------------------------

        private sealed class GdiPageSink : IPageSink
        {
            private readonly SceneGdiDevice _dev;
            private IntPtr Dc => _dev._dc;
            private Matrix3x2 _m;                 // local -> device pixels
            private float _opacity = 1f;
            private readonly Stack<(Matrix3x2 M, float Opacity, int Dc, int[] Region, bool RegionOn, List<GdiShape> Paths)> _stack = new();

            // The page's clip region, and the path clips of the fills, as DriverPrint applies them:
            // not when they are set but around each drawing that needs them (DpDriver::SetupClipping
            // @1800dea20: nothing when the drawing's bounds are wholly inside the region, else SaveDC
            // and IntersectClipRect / ExtSelectClipRgn; then SetupPathClipping's AndClip).
            private int[] _region;
            private bool _regionOn;
            private List<GdiShape> _paths = new();

            internal GdiPageSink(SceneGdiDevice dev, Matrix3x2 deviceScale)
            {
                _dev = dev;
                _m = deviceScale;
                // The context's visible clip is never wider than the page (the DC's surface).
                _region = new[] { 0, 0, Native.GetDeviceCaps(dev._dc, Native.HORZRES), Native.GetDeviceCaps(dev._dc, Native.VERTRES) };
            }

            public void Save() => _stack.Push((_m, _opacity, Native.SaveDC(Dc), _region, _regionOn, _paths));

            public void Restore()
            {
                if (_stack.Count == 0) return;
                (Matrix3x2 m, float o, int dc, int[] region, bool on, List<GdiShape> paths) = _stack.Pop();
                Native.RestoreDC(Dc, dc);
                _m = m; _opacity = o; _region = region; _regionOn = on; _paths = paths;
            }

            /// <summary>Before a drawing whose device bounds are (x, y, w, h) -- or, unknown, always:
            /// the region if the drawing is not wholly inside it, then the pending path clips.</summary>
            private void ClipFor(int x, int y, int w, int h, bool known = true, bool skipRegion = false)
            {
                if (!skipRegion && _region != null && !_regionOn && (!known || !Inside(_region, x, y, w, h)))
                {
                    Log("SaveDC");
                    Native.SaveDC(Dc);
                    if (_region.Length == 4)
                    {
                        Log($"IntersectClipRect {_region[0]},{_region[1]},{_region[2]},{_region[3]}");
                        Native.IntersectClipRect(Dc, _region[0], _region[1], _region[2], _region[3]);
                    }
                    else
                    {
                        IntPtr rgn = Region(_region);
                        Log("ExtSelectClipRgn rgn 1");
                        Native.ExtSelectClipRgn(Dc, rgn, Native.RGN_AND);
                        Native.DeleteObject(rgn);
                    }
                    _regionOn = true;
                }
                if (_paths.Count > 0)
                {
                    foreach (GdiShape s in _paths)
                    {
                        Log("SaveDC");
                        Native.SaveDC(Dc);
                        AndClip(s);
                    }
                    _paths = new List<GdiShape>();
                }
            }

            // A drawing whose bounds are not known here: the region only when it is not the page's own.
            /// <summary>DpDriver::SetupClipping around ONE driver call (a solid fill, a GDI-pen stroke):
            /// the region when the call's bounds are not wholly inside it, taken off again after the call
            /// (RestoreClipping); pending path clips as ClipFor does. True when something is to be undone.</summary>
            private bool ClipDriver(int x, int y, int w, int h)
            {
                bool pushed = false;
                if (_region != null && !_regionOn && !Inside(_region, x, y, w, h))
                {
                    ClipFor(x, y, w, h);
                    _regionOn = false;
                    pushed = true;
                }
                else ClipFor(x, y, w, h, skipRegion: true);
                return pushed;
            }

            private void Unclip()
            {
                Log("RestoreDC -1");
                Native.RestoreDC(Dc, -1);
            }

            private void ClipForUnknown()
            {
                bool page = _region != null && _region.Length == 4 && _region[0] <= 0 && _region[1] <= 0
                    && _region[2] >= Native.GetDeviceCaps(Dc, Native.HORZRES) && _region[3] >= Native.GetDeviceCaps(Dc, Native.VERTRES);
                if ((_region == null || page) && _paths.Count == 0) return;
                ClipFor(0, 0, 0, 0, known: false, skipRegion: page);
            }

            private static IntPtr Region(int[] rects)
            {
                IntPtr rgn = Native.CreateRectRgn(0, 0, 0, 0);
                for (int i = 0; i + 3 < rects.Length; i += 4)
                {
                    IntPtr r = Native.CreateRectRgn(rects[i], rects[i + 1], rects[i + 2], rects[i + 3]);
                    Native.CombineRgn(rgn, rgn, r, 2 /* RGN_OR */);
                    Native.DeleteObject(r);
                }
                return rgn;
            }

            // DpRegion::GetRectVisibility == 3: the rectangle wholly inside the region.
            private static bool Inside(int[] rects, int x, int y, int w, int h)
            {
                if (w <= 0 || h <= 0) return true;
                IntPtr a = Native.CreateRectRgn(x, y, x + w, y + h), rgn = Region(rects);
                int k = Native.CombineRgn(a, a, rgn, 4 /* RGN_DIFF */);
                Native.DeleteObject(a); Native.DeleteObject(rgn);
                return k == 1 /* NULLREGION */;
            }

            public void Concat(Matrix3x2 m) => _m = m * _m;

            public void ClipRect(float x, float y, float w, float h)
            {
                var p = new PagePath();
                p.Rect(x, y, Math.Max(0, w), Math.Max(0, h));
                ClipPath(p);
            }

            public void ClipPath(PagePath path)
            {
                if (path.GdiClip is GdiShape gs)
                {
                    _paths = new List<GdiShape>(_paths) { gs };
                    return;
                }
                if (path.DeviceRects is int[] rects)
                {
                    _region = rects;
                    _regionOn = false;
                    return;
                }
                if (path.IsEmpty) { Native.IntersectClipRect(Dc, 0, 0, 0, 0); return; }
                Native.SetPolyFillMode(Dc, path.EvenOdd ? Native.ALTERNATE : Native.WINDING);
                if (BuildPath(path)) Native.SelectClipPath(Dc, Native.RGN_AND);
            }

            public void Opacity(float alpha) => _opacity *= Math.Clamp(alpha, 0f, 1f);

            // Translucency on paper: the colour over white, since a printer DC cannot blend.
            private int ColorRef(RgbaColor c, double extraAlpha = 1)
            {
                (double r, double g, double b, double a) = ScenePageWalker.Srgb(c);
                a *= _opacity * extraAlpha;
                r = r * a + (1 - a); g = g * a + (1 - a); b = b * a + (1 - a);
                int R = (int)Math.Round(Math.Clamp(r, 0, 1) * 255), G = (int)Math.Round(Math.Clamp(g, 0, 1) * 255), B = (int)Math.Round(Math.Clamp(b, 0, 1) * 255);
                return R | (G << 8) | (B << 16);
            }

            private static double AlphaOf(RgbaColor c) => c.A;

            public void Fill(PagePath path, SceneBrush brush)
            {
                if (path.IsEmpty) return;
                PrintGdiFill tagged = brush is SolidColorBrush sc ? PrintGdiFill.Find(sc) : null;
                if (tagged == null) GetHdc();
                PrintRaster raster0 = brush is ImageBrush ib ? PrintRaster.Find(ib.PixelsRgba) : null;
                // (A solid fill clips itself, after its brush: GdiFill. A masked band is put down with no
                // clip at all: its mask is the clip.)
                if (tagged?.Shape != null) { }
                else if (raster0 != null && raster0.DevW > 0) ClipFor(raster0.DevX, raster0.DevY, raster0.DevW, raster0.DevH, skipRegion: raster0.Kind == PrintRaster.KindMasked);
                else ClipForUnknown();
                switch (brush)
                {
                    case SolidColorBrush solid when PrintGdiFill.Find(solid) is PrintGdiFill gdi:
                        GdiFill(gdi);
                        break;
                    case SolidColorBrush solid:
                        if (AlphaOf(solid.Color) * _opacity <= 0) return;
                        FillSolid(path, ColorRef(solid.Color));
                        break;
                    case SceneLinear linear:
                        Shade(path, () => LinearTriangles(path, linear));
                        break;
                    case RadialGradientBrush radial:
                        Shade(path, () => RadialRings(radial));
                        break;
                    case ImageBrush image when PrintRaster.Find(image.PixelsRgba) is PrintRaster raster:
                        Raster(path, raster);
                        break;
                    case ImageBrush image when image.PixelsRgba != null && image.PixelWidth > 0 && image.PixelHeight > 0:
                        Image(path, image);
                        break;
                }
            }

            // ---- the rasters GDI+'s driver puts down its own way (see PrintRaster) ---------------

            private const int SRCINVERT = 0x00660046, SRCAND = 0x008800C6;

            private void Raster(PagePath path, PrintRaster r)
            {
                (float x0, float y0, float x1, float y1) = path.Bounds();
                Vector2 a = Vector2.Transform(new Vector2(x0, y0), _m), b = Vector2.Transform(new Vector2(x1, y1), _m);
                int left = (int)MathF.Round(a.X), top = (int)MathF.Round(a.Y);
                int width = (int)MathF.Round(b.X) - left, height = (int)MathF.Round(b.Y) - top;
                if (width <= 0 || height <= 0) return;
                if (r.Kind == PrintRaster.KindMasked)
                {
                    // XOR the colour, AND the mask, XOR the colour: the colour where the mask is
                    // set, the paper as it was elsewhere.
                    StretchBits(r.Color, r.Width, r.Height, 32, null, left, top, width, height, r.SrcX, 0, r.SrcW, r.Height, SRCINVERT);
                    if (r.MaskBlackFirst)
                    {
                        // ConvertBitmapDataAlphaChannelTo1BPP's DIB: black / white, the bits inverted.
                        var inv = new byte[r.Mask.Length];
                        for (int i = 0; i < inv.Length; i++) inv[i] = (byte)~r.Mask[i];
                        StretchBits(inv, r.MaskWidth, r.MaskHeight, 1, new[] { 0, 0xFFFFFF }, left, top, width, height, r.MaskSrcX, 0,
                                    r.MaskSrcW > 0 ? r.MaskSrcW : width, r.MaskHeight, SRCAND);
                    }
                    else
                        StretchBits(r.Mask, r.MaskWidth, r.MaskHeight, 1, new[] { unchecked((int)0xFFFFFFFF), 0 }, left, top, width, height, r.MaskSrcX, 0,
                                    r.MaskSrcW > 0 ? r.MaskSrcW : width, r.MaskHeight, SRCAND);
                    StretchBits(r.Color, r.Width, r.Height, 32, null, left, top, width, height, r.SrcX, 0, r.SrcW, r.Height, SRCINVERT);
                    return;
                }
                if (r.Kind == PrintRaster.KindAlphaXor)
                {
                    // ConvertBitmapToGdi::StretchBlt with an alpha level: the bitmap XOR, the rectangle
                    // ANDed with the alpha brush (DPa), the bitmap XOR again -- inside the shape's clip.
                    IntPtr mask = r.AlphaDib != null ? CreateDIBPatternBrushPt(r.AlphaDib) : Native.GetStockObject(Native.BLACK_BRUSH);
                    Native.SetStretchBltMode(Dc, Native.COLORONCOLOR);
                    StretchBits(r.Color, r.Width, r.Height, 32, null, left, top, width, height, 0, 0, r.Width, r.Height, SRCINVERT, palettize: true);
                    IntPtr h = SelectBrush(mask);
                    PatBlt(left, top, width, height, PATAND);
                    SelectBrush(h);
                    StretchBits(r.Color, r.Width, r.Height, 32, null, left, top, width, height, 0, 0, r.Width, r.Height, SRCINVERT, palettize: true);
                    if (r.AlphaDib != null) Native.DeleteObject(mask);
                    return;
                }
                if (r.Kind == PrintRaster.KindXorPath)
                {
                    // XOR the bitmap, black the shape in (R2_MASKPEN: what is there AND black), XOR
                    // the bitmap again: the bitmap inside the shape, the paper outside it.
                    StretchBits(r.Color, r.Width, r.Height, 32, null, left, top, width, height, 0, 0, r.Width, r.Height, SRCINVERT);
                    var shape = new PagePath { EvenOdd = !r.ClipNonZero };
                    int n = Math.Min(r.ClipTypes.Length, r.ClipXY.Length / 2);
                    for (int i = 0; i < n; i++)
                    {
                        var p = new Vector2(r.ClipXY[i * 2], r.ClipXY[i * 2 + 1]);
                        int pt = r.ClipTypes[i] & 7;
                        if (pt == 0) shape.MoveTo(p);
                        else if (pt == 3 && i + 2 < n)
                        {
                            shape.CubicTo(p, new Vector2(r.ClipXY[i * 2 + 2], r.ClipXY[i * 2 + 3]), new Vector2(r.ClipXY[i * 2 + 4], r.ClipXY[i * 2 + 5]));
                            i += 2;
                        }
                        else shape.LineTo(p);
                        if ((r.ClipTypes[i] & 0x80) != 0) shape.Close();
                    }
                    if (r.Shape != null)
                    {
                        // ConvertPathToGdi::Fill of the shape under R2_MASKPEN, with the alpha brush
                        // (a near-constant translucent gradient) or the stock black one.
                        IntPtr mask = r.AlphaDib != null ? CreateDIBPatternBrushPt(r.AlphaDib) : Native.GetStockObject(Native.BLACK_BRUSH);
                        int rop2 = SetROP2(Native.R2_MASKPEN);
                        ShapeFill(r.Shape, mask);
                        SetROP2(rop2);
                        if (r.AlphaDib != null) Native.DeleteObject(mask);
                    }
                    else
                    {
                        int rop2 = Native.SetROP2(Dc, Native.R2_MASKPEN);
                        // The recording's units are the identity under _m here (BeginDeviceDraw).
                        FillSolid(shape, 0);
                        Native.SetROP2(Dc, rop2);
                    }
                    StretchBits(r.Color, r.Width, r.Height, 32, null, left, top, width, height, 0, 0, r.Width, r.Height, SRCINVERT);
                    return;
                }
                // Runs: each row's pixels with alpha 5 or more, a StretchDIBits a run.
                int s = width / Math.Max(1, r.Width), t = height / Math.Max(1, r.Height);
                for (int j = 0; j < r.Height; j++)
                {
                    int i = 0;
                    while (i < r.Width)
                    {
                        if (r.Color[(j * r.Width + i) * 4 + 3] < 5) { i++; continue; }
                        int k = i;
                        while (k < r.Width && r.Color[(j * r.Width + k) * 4 + 3] >= 5) k++;
                        int n = k - i;
                        var run = new byte[n * 4];
                        Buffer.BlockCopy(r.Color, (j * r.Width + i) * 4, run, 0, run.Length);
                        for (int q = 3; q < run.Length; q += 4) run[q] = 255;
                        StretchBits(run, n, 1, 32, null, left + s * i, top + t * j, n * s, t, 0, 0, n, 1, Native.SRCCOPY);
                        i = k;
                    }
                }
            }

            // ---- GDI+'s solid fills (see PrintGdiFill) ---------------------------------------------

            private const int PATINVERT = 0x005A0049, PATAND = 0x00A000C9, PATCOPY = 0x00F00021;

            // ConvertBrushToGdi at DriverPrint+0x58: the one solid brush the driver keeps, made
            // afresh only when the colour changes.
            private IntPtr _solid;
            private int _solidColor = -1;

            /// <summary>DpContext::GetHdc's first use on the page (CleanTheHdc @180037c28): MM_TEXT and
            /// R2_COPYPEN -- when the first drawing asks for the DC, after it has made its brush.</summary>
            private bool _clean;

            private void GetHdc()
            {
                if (_clean) return;
                _clean = true;
                Native.SetMapMode(Dc, 1);
                SetROP2(13);
            }

            internal void EndPage()
            {
                if (_solid != IntPtr.Zero) Native.DeleteObject(_solid);
                _solid = IntPtr.Zero;
                _solidColor = -1;
            }

            private IntPtr SolidBrush(int color)
            {
                if (_solid != IntPtr.Zero && _solidColor == color) return _solid;
                if (_solid != IntPtr.Zero) Native.DeleteObject(_solid);
                _solid = Native.CreateSolidBrush(color);
                _solidColor = color;
                Log($"CreateSolidBrush {color:X6} -> {NewBrush(_solid)}");
                return _solid;
            }

            /// <summary>DriverPrint::FillPath @1800cded0 / FillRects @1800ce1e0 of a solid brush: the
            /// shape as it is (alpha 0xfe or more), or dithered between two PATINVERTs.</summary>
            private void GdiFill(PrintGdiFill f)
            {
                GdiShape s = f.Shape;
                if (s == null) return;
                IntPtr color = SolidBrush(f.Color);
                GetHdc();
                if (s.Region != null)
                {
                    // DriverPrint::FillRegion: the region already holds the clip.
                    IntPtr rgn = Region(s.Region);
                    if (f.AlphaDib == null)
                    {
                        Log($"FillRgn brush={Name(color)}");
                        Native.FillRgn(Dc, rgn, color);
                    }
                    else
                    {
                        IntPtr mask = CreateDIBPatternBrushPt(f.AlphaDib);
                        IntPtr h = SelectBrush(color);
                        PatBlt(s.X, s.Y, s.W, s.H, PATINVERT);
                        int rop2 = SetROP2(Native.R2_MASKPEN);
                        int old = SetTextColor(Native.GetBkColor(Dc));
                        Log($"FillRgn brush={Name(mask)}");
                        Native.FillRgn(Dc, rgn, mask);
                        SetTextColor(old);
                        SetROP2(rop2);
                        PatBlt(s.X, s.Y, s.W, s.H, PATINVERT);
                        SelectBrush(h);
                        Native.DeleteObject(mask);
                    }
                    Native.DeleteObject(rgn);
                    return;
                }
                // (GetBrush first, then SetupClipping: the order DriverPrint::FillPath calls them in.)
                bool pushed = ClipDriver(s.X, s.Y, s.W, s.H);
                GdiFillBody(f, s, color);
                if (pushed) Unclip();
            }

            private void GdiFillBody(PrintGdiFill f, GdiShape s, IntPtr color)
            {
                if (f.AlphaDib == null)
                {
                    if (s.IsRects) RectsFill(s, color, PATCOPY);
                    else ShapeFill(s, color);
                    return;
                }
                IntPtr mask = CreateDIBPatternBrushPt(f.AlphaDib);
                if (s.IsRects)
                {
                    // ConvertRectFToGdi::AlphaFill @1800d8b60.
                    IntPtr h = SelectBrush(color);
                    PatBlt(s.X, s.Y, s.W, s.H, PATINVERT);
                    int old = SetTextColor(Native.GetBkColor(Dc));
                    RectsFill(s, mask, PATAND);
                    SetTextColor(old);
                    PatBlt(s.X, s.Y, s.W, s.H, PATINVERT);
                    SelectBrush(h);
                }
                else
                {
                    // ConvertPathToGdi::AlphaFill @1800d8a50.
                    IntPtr h = SelectBrush(color);
                    PatBlt(s.X, s.Y, s.W, s.H, PATINVERT);
                    int rop2 = SetROP2(Native.R2_MASKPEN);
                    int old = SetTextColor(Native.GetBkColor(Dc));
                    ShapeFill(s, mask);
                    SetTextColor(old);
                    SetROP2(rop2);
                    PatBlt(s.X, s.Y, s.W, s.H, PATINVERT);
                    SelectBrush(h);
                }
                Native.DeleteObject(mask);
            }

            /// <summary>ConvertRectFToGdi::Fill @1800340b8: each rectangle PatBlt'd with the ROP, the
            /// null pen selected.</summary>
            private void RectsFill(GdiShape s, IntPtr brush, int rop)
            {
                IntPtr h = SelectBrush(brush);
                IntPtr p = SelectPen(Native.GetStockObject(Native.NULL_PEN));
                for (int i = 0; i + 3 < s.Rects.Length; i += 4)
                    PatBlt(s.Rects[i], s.Rects[i + 1], s.Rects[i + 2] - s.Rects[i], s.Rects[i + 3] - s.Rects[i + 1], rop);
                SelectPen(p);
                SelectBrush(h);
            }

            /// <summary>ConvertPathToGdi::Fill @1800d95c0 (no increased resolution): polygons with the
            /// null pen, or the path built and FillPath'd.</summary>
            private void ShapeFill(GdiShape s, IntPtr brush)
            {
                if (s.NPts < 1) return;
                IntPtr h = SelectBrush(brush);
                int fm = SetPolyFillMode(s.FillMode);
                if ((s.Flags & 1) != 0)
                {
                    IntPtr p = SelectPen(Native.GetStockObject(Native.NULL_PEN));
                    if (s.NSub == 1) Polygon(s.Pts, 0, s.NPts);
                    else PolyPolygonDraw(s.Pts, 0, s.Counts, 0, s.NSub);
                    SelectPen(p);
                }
                else
                {
                    BeginPath();
                    if ((s.Flags & 0x10) == 0) DrawMixedPath(s);
                    else PolyBezier(s.Pts, 0, s.NPts);
                    EndPath();
                    FillPathNow();
                }
                SetPolyFillMode(fm);
                SelectBrush(h);
            }

            /// <summary>DriverPrint::StrokePath @1800d21b0, the GDI pen branch: the brush (GetBrush, the
            /// solid one kept), ConvertPenToGdi (the miter limit, ExtCreatePen), the clip, Draw, the pen
            /// deleted and the miter limit put back.</summary>
            private void GdiStroke(GdiPen p)
            {
                GdiShape s = p.Shape;
                if (s == null) return;
                SolidBrush(p.Color);
                GetHdc();
                float oldMiter = 0;
                bool miterSet = p.SetMiter && Native.SetMiterLimit(Dc, p.Miter, out oldMiter);
                var lb = new Native.LOGBRUSH { lbStyle = 0, lbColor = p.Color };
                IntPtr pen = Native.ExtCreatePen(p.Style, (uint)p.Width, ref lb, 0, null);
                Log($"ExtCreatePen style={p.Style:X} width={p.Width} brush=0/{p.Color:X6} -> {NewPen(pen)}");
                bool pushed = ClipDriver(s.X, s.Y, s.W, s.H);
                Draw(s, pen);
                if (pushed) Unclip();
                Native.DeleteObject(pen);
                if (miterSet) Native.SetMiterLimit(Dc, oldMiter, out _);
            }

            /// <summary>ConvertPathToGdi::Draw @1800d9270 (no one-pixel extension).</summary>
            private void Draw(GdiShape s, IntPtr pen)
            {
                if (s.NPts < 1) return;
                IntPtr h = SelectPen(pen);
                IntPtr b = SelectBrush(Native.GetStockObject(5));
                if ((s.Flags & 1) == 0)
                {
                    if ((s.Flags & 0x10) != 0) PolyBezier(s.Pts, 0, s.NPts);
                    else
                    {
                        BeginPath();
                        DrawMixedPath(s);
                        EndPath();
                        Log($"StrokePath {State()}");
                        Native.StrokePath(Dc);
                    }
                }
                else if (s.NSub != 1)
                {
                    if ((s.Flags & 0x20) == 0) PolyPolyline(s.Pts, s.Counts, s.NSub);
                    else if ((s.Flags & 0x40) == 0) PolyPolygonDirect(s.Pts, s.Counts, s.NSub);
                    else
                    {
                        // GDI+ compares the whole buffer's first point with the subpath's last index.
                        int at = 0;
                        for (int k = 0; k < s.NSub; k++)
                        {
                            int cpt = s.Counts[k];
                            if (s.Pts[0] == s.Pts[(cpt - 1) * 2] && s.Pts[1] == s.Pts[(cpt - 1) * 2 + 1]) Polygon(s.Pts, at, cpt);
                            else Polyline(s.Pts, at, cpt);
                            at += cpt;
                        }
                    }
                }
                else if ((s.Flags & 0x20) != 0) Polygon(s.Pts, 0, s.NPts);
                else Polyline(s.Pts, 0, s.NPts);
                SelectPen(h);
                SelectBrush(b);
            }

            private void Polyline(int[] pts, int from, int n)
            {
                Log($"Polyline {Pts(pts, from, n)}");
                Native.Polyline(Dc, Slice(pts, from, n), n);
            }

            private void PolyPolyline(int[] pts, int[] counts, int npoly)
            {
                int sum = 0;
                for (int q = 0; q < npoly; q++) sum += counts[q];
                var cs = new uint[npoly];
                for (int q = 0; q < npoly; q++) cs[q] = (uint)counts[q];
                Log($"PolyPolyline counts=[{string.Join(",", cs)}] {Pts(pts, 0, sum)}");
                Native.PolyPolyline(Dc, Slice(pts, 0, sum), cs, (uint)npoly);
            }

            private void PolyPolygonDirect(int[] pts, int[] counts, int npoly)
            {
                int sum = 0;
                for (int q = 0; q < npoly; q++) sum += counts[q];
                var cs = new int[npoly];
                Array.Copy(counts, cs, npoly);
                Log($"PolyPolygon counts=[{string.Join(",", cs)}] {Pts(pts, 0, sum)} {State()}");
                Native.PolyPolygon(Dc, Slice(pts, 0, sum), cs, npoly);
            }

            private static int s_penNo;
            private static readonly Dictionary<IntPtr, string> s_penIds = new();

            private static string NewPen(IntPtr h)
            {
                if (string.IsNullOrEmpty(s_dump)) return null;
                string id = "p" + (s_penNo++);
                lock (s_penIds) s_penIds[h] = id;
                return id;
            }

            /// <summary>ConvertPathToGdi::AndClip @1802227b8 (DriverPrint::SetupPathClipping @1800d18d0,
            /// flags 0): the path built and selected into the clip.</summary>
            private void AndClip(GdiShape s)
            {
                if (s.NPts < 1) return;
                BeginPath();
                int fm = SetPolyFillMode(s.FillMode);
                if ((s.Flags & 1) == 0)
                {
                    if ((s.Flags & 0x10) == 0) DrawMixedPath(s);
                    else PolyBezier(s.Pts, 0, s.NPts);
                }
                else if (s.NSub == 1) Polygon(s.Pts, 0, s.NPts);
                else
                {
                    int sum = 0;
                    for (int q = 0; q < s.NSub; q++) sum += s.Counts[q];
                    var cs = new int[s.NSub];
                    Array.Copy(s.Counts, cs, s.NSub);
                    Log($"PolyPolygon counts=[{string.Join(",", cs)}] {Pts(s.Pts, 0, sum)} {State()}");
                    Native.PolyPolygon(Dc, Slice(s.Pts, 0, sum), cs, s.NSub);
                }
                EndPath();
                Log("SelectClipPath 1");
                Native.SelectClipPath(Dc, Native.RGN_AND);
                SetPolyFillMode(fm);
            }

            /// <summary>ConvertPathToGdi::DrawMixedPath @1802232c8.</summary>
            private void DrawMixedPath(GdiShape s)
            {
                int i = 0, last = s.NPts - 1;
                while (i <= last)
                {
                    int t = s.Types[i] & 7;
                    if (t == 0)
                    {
                        if (i > 0 && (s.Types[i - 1] & 0x80) != 0) CloseFigure();
                        MoveTo(s.Pts[i * 2], s.Pts[i * 2 + 1]);
                        i++;
                        continue;
                    }
                    int j = i;
                    do j++; while (j <= last && (s.Types[j] & 7) == t);
                    if (t == 3) PolyBezierTo(s.Pts, i, j - i);
                    else if (j - i == 1) LineTo(s.Pts[i * 2], s.Pts[i * 2 + 1]);
                    else PolylineTo(s.Pts, i, j - i);
                    i = j;
                }
                if ((s.Types[s.NPts - 1] & 0x80) != 0) CloseFigure();
            }

            /// <summary>CPolyPolygon::Draw @1800d91b0: more than 31 polygons split in eight runs when
            /// their boxes are disjoint.</summary>
            private void PolyPolygonDraw(int[] pts, int ptFrom, int[] counts, int cFrom, int npoly)
            {
                if (npoly > 0x1f)
                {
                    int size = npoly / 8;
                    var runs = new (int P, int C, int N, int L, int T, int R, int B)[8];
                    int p = ptFrom;
                    for (int k = 0; k < 8; k++)
                    {
                        int cnt = k == 7 ? npoly - 7 * size : size;
                        int c0 = cFrom + k * size;
                        int total = 0;
                        for (int q = 0; q < cnt; q++) total += counts[c0 + q];
                        int l = pts[p * 2], t = pts[p * 2 + 1], r = l, b = t;
                        for (int q = 1; q < total; q++)
                        {
                            int x = pts[(p + q) * 2], y = pts[(p + q) * 2 + 1];
                            if (x < l) l = x; if (x > r) r = x;
                            if (y < t) t = y; if (y > b) b = y;
                        }
                        runs[k] = (p, c0, cnt, l, t, r, b);
                        p += total;
                    }
                    bool disjoint = true;
                    for (int a = 0; a < 8 && disjoint; a++)
                        for (int c = a + 1; c < 8; c++)
                            if (runs[a].L < runs[c].R && runs[a].T < runs[c].B && runs[c].L < runs[a].R && runs[c].T < runs[a].B)
                            { disjoint = false; break; }
                    if (disjoint)
                    {
                        for (int k = 0; k < 8; k++) PolyPolygonDraw(pts, runs[k].P, counts, runs[k].C, runs[k].N);
                        return;
                    }
                }
                int sum = 0;
                for (int q = 0; q < npoly; q++) sum += counts[cFrom + q];
                var xy = new int[sum * 2];
                Array.Copy(pts, ptFrom * 2, xy, 0, sum * 2);
                var cs = new int[npoly];
                Array.Copy(counts, cFrom, cs, 0, npoly);
                Log($"PolyPolygon counts=[{string.Join(",", cs)}] {Pts(xy, 0, sum)} {State()}");
                Native.PolyPolygon(Dc, xy, cs, npoly);
            }

            // ---- the GDI calls, each logged as the stock hook logs gdiplus.dll's (WF_PRINT_DUMP) --

            private static int s_brushNo;
            private static readonly Dictionary<IntPtr, string> s_brushIds = new();
            private static Dictionary<IntPtr, int> s_stock;

            private static string NewBrush(IntPtr h)
            {
                if (string.IsNullOrEmpty(s_dump)) return null;
                string id = "b" + (s_brushNo++);
                lock (s_brushIds) s_brushIds[h] = id;
                return id;
            }

            private static string Name(IntPtr h)
            {
                if (h == IntPtr.Zero) return "null";
                if (s_stock == null)
                {
                    var st = new Dictionary<IntPtr, int>();
                    for (int i = 0; i <= 19; i++) { IntPtr o = Native.GetStockObject(i); if (o != IntPtr.Zero) st.TryAdd(o, i); }
                    s_stock = st;
                }
                if (s_stock.TryGetValue(h, out int k)) return "stock" + k;
                lock (s_brushIds) return s_brushIds.TryGetValue(h, out string s) ? s : "?";
            }

            private static void Log(string line)
            {
                if (string.IsNullOrEmpty(s_dump)) return;
                try
                {
                    System.IO.Directory.CreateDirectory(s_dump);
                    System.IO.File.AppendAllText(System.IO.Path.Combine(s_dump, "log.txt"), "     " + line + "\n");
                }
                catch (Exception) { }
            }

            private string State()
            {
                if (string.IsNullOrEmpty(s_dump)) return null;
                Native.GetClipBox(Dc, out Native.RECT clip);
                Native.GetBrushOrgEx(Dc, out Native.POINT org);
                return $"brush={Name(Native.GetCurrentObject(Dc, 2))} rop2={Native.GetROP2(Dc)} text={Native.GetTextColor(Dc):X6} bk={Native.GetBkColor(Dc):X6} org={org.x},{org.y} fill={Native.GetPolyFillMode(Dc)} clipbox=({clip.l},{clip.t},{clip.r},{clip.b})";
            }

            private static string Pts(int[] pts, int from, int n)
            {
                if (string.IsNullOrEmpty(s_dump)) return null;
                var sb = new System.Text.StringBuilder($"n={n} [");
                for (int i = 0; i < n; i++) sb.Append(pts[(from + i) * 2]).Append(',').Append(pts[(from + i) * 2 + 1]).Append(i + 1 < n ? " " : "");
                return sb.Append(']').ToString();
            }

            private static int[] Slice(int[] pts, int from, int n)
            {
                var r = new int[n * 2];
                Array.Copy(pts, from * 2, r, 0, n * 2);
                return r;
            }

            private IntPtr SelectBrush(IntPtr h)
            {
                Log($"SelectObject brush {Name(h)}");
                return Native.SelectObject(Dc, h);
            }

            private IntPtr SelectPen(IntPtr h)
            {
                string pn;
                lock (s_penIds) pn = s_penIds.TryGetValue(h, out string id) ? id : Name(h);
                Log($"SelectObject pen {pn}");
                return Native.SelectObject(Dc, h);
            }

            private void PatBlt(int x, int y, int w, int h, int rop)
            {
                Log($"PatBlt ({x},{y},{w},{h}) rop={rop:X} {State()}");
                Native.PatBlt(Dc, x, y, w, h, rop);
            }

            private int SetROP2(int rop2) { Log($"SetROP2 {rop2}"); return Native.SetROP2(Dc, rop2); }
            private int SetTextColor(int c) { Log($"SetTextColor {c:X6}"); return Native.SetTextColor(Dc, c); }
            private int SetPolyFillMode(int m) { Log($"SetPolyFillMode {m}"); return Native.SetPolyFillMode(Dc, m); }
            private void BeginPath() { Log("BeginPath"); Native.BeginPath(Dc); }
            private void EndPath() { Log("EndPath"); Native.EndPath(Dc); }
            private void CloseFigure() { Log("CloseFigure"); Native.CloseFigure(Dc); }
            private void FillPathNow() { Log($"FillPath {State()}"); Native.FillPath(Dc); }
            private void MoveTo(int x, int y) { Log($"MoveToEx {x},{y}"); Native.MoveToEx(Dc, x, y, IntPtr.Zero); }
            private void LineTo(int x, int y) { Log($"LineTo {x},{y}"); Native.LineTo(Dc, x, y); }

            private void Polygon(int[] pts, int from, int n)
            {
                Log($"Polygon {Pts(pts, from, n)} {State()}");
                Native.Polygon(Dc, Slice(pts, from, n), n);
            }

            private void PolyBezier(int[] pts, int from, int n)
            {
                Log($"PolyBezier {Pts(pts, from, n)}");
                Native.PolyBezier(Dc, Slice(pts, from, n), (uint)n);
            }

            private void PolyBezierTo(int[] pts, int from, int n)
            {
                Log($"PolyBezierTo {Pts(pts, from, n)}");
                Native.PolyBezierToI(Dc, Slice(pts, from, n), (uint)n);
            }

            private void PolylineTo(int[] pts, int from, int n)
            {
                Log($"PolylineTo {Pts(pts, from, n)}");
                Native.PolylineTo(Dc, Slice(pts, from, n), (uint)n);
            }

            private static IntPtr CreateDIBPatternBrushPt(byte[] packed)
            {
                IntPtr h = Native.CreateDIBPatternBrushPt(packed, 0);
                if (!string.IsNullOrEmpty(s_dump))
                {
                    int w = BitConverter.ToInt32(packed, 4), ht = BitConverter.ToInt32(packed, 8);
                    int bpp = BitConverter.ToInt16(packed, 14), comp = BitConverter.ToInt32(packed, 16), size = BitConverter.ToInt32(packed, 20);
                    int used = BitConverter.ToInt32(packed, 32);
                    int pal = bpp <= 8 ? (used != 0 ? used : 1 << bpp) : 0;
                    var pals = new string[pal];
                    for (int i = 0; i < pal; i++) pals[i] = BitConverter.ToUInt32(packed, 40 + i * 4).ToString("X6");
                    int stride = ((w * bpp + 31) / 32) * 4, at = 40 + pal * 4;
                    var rows = new string[Math.Abs(ht)];
                    for (int r = 0; r < rows.Length; r++) rows[r] = Convert.ToHexString(packed, at + r * stride, stride).ToLowerInvariant();
                    Log($"CreateDIBPatternBrushPt {w}x{ht}x{bpp} comp={comp} size={size} usage=0 pal=[{string.Join(",", pals)}] bits={string.Join("/", rows)} -> {NewBrush(h)}");
                }
                return h;
            }

            // StretchDIBits of a top-down DIB: 32bpp BGRA, or 1bpp with its two-colour palette.
            // The DIB as GDI+'s driver hands it over (DriverNonPS::OutputBufferDIB, ConvertBitmapToGdi):
            // BOTTOM-UP, the colour 24bpp, a mask 1bpp under a white (0xFFFFFFFF) / black palette.
            // (The source rectangles here are always whole rows, so the flip leaves them as they are.)
            private void StretchBits(byte[] bits, int w, int h, int bpp, int[] palette, int x, int y, int dw, int dh, int sx, int sy, int sw, int sh, int rop, bool palettize = false)
            {
                byte[] up;
                int outBpp = bpp;
                int[] pal = palette;
                if (bpp == 32 && palettize && Palettize(bits, w, h, out byte[] packed, out int pbpp, out int[] ppal))
                {
                    up = packed; outBpp = pbpp; pal = ppal;
                }
                else if (bpp == 32)
                {
                    outBpp = 24;
                    int stride = ((w * 24 + 31) / 32) * 4;
                    up = new byte[stride * h];
                    for (int j = 0; j < h; j++)
                    {
                        int d = (h - 1 - j) * stride, o = j * w * 4;
                        for (int i = 0; i < w; i++) { up[d + i * 3] = bits[o + i * 4]; up[d + i * 3 + 1] = bits[o + i * 4 + 1]; up[d + i * 3 + 2] = bits[o + i * 4 + 2]; }
                    }
                }
                else
                {
                    int stride = PrintRaster.MaskStride(w);
                    up = new byte[stride * h];
                    for (int j = 0; j < h; j++) Buffer.BlockCopy(bits, j * stride, up, (h - 1 - j) * stride, stride);
                }
                Dump(bits, w, h, bpp, x, y, dw, dh, sx, sy, sw, sh, rop, pal, outBpp, Crc(up, w, h, outBpp));
                int n = pal?.Length ?? 0;
                var bmi = new byte[40 + n * 4];
                BitConverter.TryWriteBytes(bmi.AsSpan(0), 40); BitConverter.TryWriteBytes(bmi.AsSpan(4), w); BitConverter.TryWriteBytes(bmi.AsSpan(8), h);
                BitConverter.TryWriteBytes(bmi.AsSpan(12), (short)1); BitConverter.TryWriteBytes(bmi.AsSpan(14), (short)outBpp);
                BitConverter.TryWriteBytes(bmi.AsSpan(32), n);
                for (int i = 0; i < n; i++) BitConverter.TryWriteBytes(bmi.AsSpan(40 + i * 4), pal[i]);
                Native.StretchDIBitsB(Dc, x, y, dw, dh, sx, sy, sw, sh, up, bmi, 0, rop);
            }

            /// <summary>ConvertBitmapToGdi::ConvertBitmapToGdi @1800d76d8 of a 32bpp bitmap: its colours
            /// gathered by a PaletteSorter (AddColor @180033f68) seeded with black and white, first seen
            /// first, top row down; up to 256 of them make a palettised DIB -- 1bpp for two, 4bpp up to
            /// sixteen, else 8bpp -- bottom-up, rows packed most significant bits first.</summary>
            private static bool Palettize(byte[] bgra, int w, int h, out byte[] packed, out int bpp, out int[] palette)
            {
                packed = null; bpp = 0; palette = null;
                var index = new Dictionary<int, int> { [0] = 0, [0xFFFFFF] = 1 };
                var pal = new List<int> { 0, 0xFFFFFF };
                var idx = new byte[w * h];
                for (int j = 0; j < h; j++)
                    for (int i = 0; i < w; i++)
                    {
                        int o = (j * w + i) * 4;
                        int c = bgra[o] | (bgra[o + 1] << 8) | (bgra[o + 2] << 16);   // RGBQUAD
                        if (!index.TryGetValue(c, out int k))
                        {
                            if (pal.Count == 256) return false;
                            k = pal.Count;
                            index[c] = k;
                            pal.Add(c);
                        }
                        idx[j * w + i] = (byte)k;
                    }
                bpp = pal.Count <= 16 ? (pal.Count < 3 ? 1 : 4) : 8;
                int stride = ((w * bpp + 31) / 32) * 4;
                packed = new byte[stride * h];
                for (int j = 0; j < h; j++)
                {
                    int d = (h - 1 - j) * stride;
                    for (int i = 0; i < w; i++)
                    {
                        int v = idx[j * w + i];
                        if (bpp == 8) packed[d + i] = (byte)v;
                        else if (bpp == 4) packed[d + (i >> 1)] |= (byte)(v << ((i & 1) == 0 ? 4 : 0));
                        else packed[d + (i >> 3)] |= (byte)(v << (7 - (i & 7)));
                    }
                }
                palette = pal.ToArray();
                return true;
            }

            private void FillSolid(PagePath path, int colorRef)
            {
                IntPtr brush = Native.CreateSolidBrush(colorRef);
                IntPtr old = Native.SelectObject(Dc, brush);
                IntPtr oldPen = Native.SelectObject(Dc, Native.GetStockObject(Native.NULL_PEN));
                Native.SetPolyFillMode(Dc, path.EvenOdd ? Native.ALTERNATE : Native.WINDING);
                if (BuildPath(path)) Native.FillPath(Dc);
                Native.SelectObject(Dc, oldPen);
                Native.SelectObject(Dc, old);
                Native.DeleteObject(brush);
            }

            // Inside the shape's clip, draw what paints it.
            private void Shade(PagePath path, Action paint)
            {
                int s = Native.SaveDC(Dc);
                ClipPath(path);
                paint();
                Native.RestoreDC(Dc, s);
            }

            private static List<GradientStop> Sorted(GradientStop[] stops)
            {
                var sorted = new List<GradientStop>(stops);
                sorted.Sort((a, b) => a.Offset.CompareTo(b.Offset));
                if (sorted.Count == 0) sorted.Add(new GradientStop(0, new RgbaColor(0, 0, 0, 1)));
                return sorted;
            }

            // A linear gradient as GradientFill triangles: a quad per pair of neighbouring stops,
            // across the whole of the shape's bounds, and solid pads beyond the end stops.
            private void LinearTriangles(PagePath path, SceneLinear g)
            {
                Vector2 d = g.End - g.Start;
                float len = d.Length();
                List<GradientStop> stops = Sorted(g.Stops);
                if (len < 1e-6f) { FillSolid(path, ColorRef(stops[stops.Count - 1].Color)); return; }
                Vector2 u = d / len, n = new Vector2(-u.Y, u.X);
                (float x0, float y0, float x1, float y1) = path.Bounds();
                float tMin = float.MaxValue, tMax = float.MinValue, sMin = float.MaxValue, sMax = float.MinValue;
                foreach (Vector2 c in new[] { new Vector2(x0, y0), new Vector2(x1, y0), new Vector2(x0, y1), new Vector2(x1, y1) })
                {
                    float t = Vector2.Dot(c - g.Start, u), s = Vector2.Dot(c - g.Start, n);
                    tMin = MathF.Min(tMin, t); tMax = MathF.Max(tMax, t); sMin = MathF.Min(sMin, s); sMax = MathF.Max(sMax, s);
                }
                var ts = new List<(float T, RgbaColor C)>();
                ts.Add((MathF.Min(tMin, stops[0].Offset * len), stops[0].Color));
                foreach (GradientStop st in stops) ts.Add((st.Offset * len, st.Color));
                ts.Add((MathF.Max(tMax, stops[stops.Count - 1].Offset * len), stops[stops.Count - 1].Color));
                var verts = new List<Native.TRIVERTEX>();
                var tris = new List<Native.GRADIENT_TRIANGLE>();
                for (int i = 0; i + 1 < ts.Count; i++)
                {
                    if (ts[i + 1].T <= ts[i].T) continue;
                    int b = verts.Count;
                    verts.Add(Vertex(g.Start + u * ts[i].T + n * sMin, ts[i].C));
                    verts.Add(Vertex(g.Start + u * ts[i].T + n * sMax, ts[i].C));
                    verts.Add(Vertex(g.Start + u * ts[i + 1].T + n * sMax, ts[i + 1].C));
                    verts.Add(Vertex(g.Start + u * ts[i + 1].T + n * sMin, ts[i + 1].C));
                    tris.Add(new Native.GRADIENT_TRIANGLE { V1 = (uint)b, V2 = (uint)(b + 1), V3 = (uint)(b + 2) });
                    tris.Add(new Native.GRADIENT_TRIANGLE { V1 = (uint)b, V2 = (uint)(b + 2), V3 = (uint)(b + 3) });
                }
                if (tris.Count > 0)
                    Native.GradientFill(Dc, verts.ToArray(), (uint)verts.Count, tris.ToArray(), (uint)tris.Count, Native.GRADIENT_FILL_TRIANGLE);
            }

            private Native.TRIVERTEX Vertex(Vector2 local, RgbaColor c)
            {
                Vector2 p = Vector2.Transform(local, _m);
                int cr = ColorRef(c);
                return new Native.TRIVERTEX
                {
                    x = (int)MathF.Round(p.X), y = (int)MathF.Round(p.Y),
                    Red = (ushort)((cr & 0xff) << 8), Green = (ushort)(((cr >> 8) & 0xff) << 8), Blue = (ushort)(((cr >> 16) & 0xff) << 8),
                };
            }

            // A radial gradient as concentric ellipses, outermost first, each in the colour of its
            // radius: GDI has no radial shading, and at printer resolution sixty-four rings read as one.
            private void RadialRings(RadialGradientBrush g)
            {
                List<GradientStop> stops = Sorted(g.Stops);
                const int Rings = 64;
                // Beyond the outermost stop: its colour, over everything the clip lets through.
                var pad = new PagePath();
                pad.Rect(g.Center.X - g.RadiusX * 4, g.Center.Y - g.RadiusY * 4, g.RadiusX * 8, g.RadiusY * 8);
                FillSolid(pad, ColorRef(stops[stops.Count - 1].Color));
                for (int i = Rings; i >= 1; i--)
                {
                    float t = i / (float)Rings;
                    var ring = new PagePath();
                    ring.Ellipse(g.Center.X, g.Center.Y, g.RadiusX * t, g.RadiusY * t);
                    FillSolid(ring, ColorRef(Sample(stops, t - 0.5f / Rings)));
                }
            }

            private static RgbaColor Sample(List<GradientStop> stops, float t)
            {
                if (t <= stops[0].Offset) return stops[0].Color;
                for (int i = 0; i + 1 < stops.Count; i++)
                {
                    GradientStop a = stops[i], b = stops[i + 1];
                    if (t <= b.Offset)
                    {
                        float k = b.Offset > a.Offset ? (t - a.Offset) / (b.Offset - a.Offset) : 1f;
                        return new RgbaColor(a.Color.R + (b.Color.R - a.Color.R) * k, a.Color.G + (b.Color.G - a.Color.G) * k,
                                             a.Color.B + (b.Color.B - a.Color.B) * k, a.Color.A + (b.Color.A - a.Color.A) * k);
                    }
                }
                return stops[stops.Count - 1].Color;
            }

            // The image being put down is a band of a scan DIB (not palettised).
            private bool _scanDib;

            private void Image(PagePath path, ImageBrush image)
            {
                _scanDib = PrintRaster.IsScanDib(image.PixelsRgba);
                int w = image.PixelWidth, h = image.PixelHeight;
                // Composited onto white: a printer DC has no alpha.
                byte[] src = image.PixelsRgba;
                double a0 = _opacity * image.Opacity;
                var bgra = new byte[w * h * 4];
                for (int i = 0; i < w * h; i++)
                {
                    double a = src[i * 4 + 3] / 255.0 * a0;
                    bgra[i * 4] = (byte)Math.Round(src[i * 4 + 2] * a + 255 * (1 - a));
                    bgra[i * 4 + 1] = (byte)Math.Round(src[i * 4 + 1] * a + 255 * (1 - a));
                    bgra[i * 4 + 2] = (byte)Math.Round(src[i * 4] * a + 255 * (1 - a));
                    bgra[i * 4 + 3] = 255;
                }
                (float x0, float y0, float x1, float y1) = path.Bounds();
                int s = Native.SaveDC(Dc);
                ClipPath(path);
                if (image.TileMode == TileMode.None || image.TileWidth <= 0 || image.TileHeight <= 0)
                    Blit(bgra, w, h, x0, y0, x1 - x0, y1 - y0);
                else
                {
                    // A tile (a hatch): placed from the local origin, repeated over the bounds.
                    float tw = image.TileWidth, th = image.TileHeight;
                    int count = (int)(Math.Ceiling((x1 - x0) / tw + 1) * Math.Ceiling((y1 - y0) / th + 1));
                    if (count <= 20000)
                        for (float ty = MathF.Floor(y0 / th) * th; ty < y1; ty += th)
                            for (float tx = MathF.Floor(x0 / tw) * tw; tx < x1; tx += tw)
                                Blit(bgra, w, h, tx, ty, tw, th);
                    else
                    {
                        // Too many to place one by one: the tile's average colour.
                        long r = 0, g = 0, b = 0;
                        for (int i = 0; i < w * h; i++) { b += bgra[i * 4]; g += bgra[i * 4 + 1]; r += bgra[i * 4 + 2]; }
                        int n = w * h;
                        FillSolid(path, (int)(r / n) | ((int)(g / n) << 8) | ((int)(b / n) << 16));
                    }
                }
                Native.RestoreDC(Dc, s);
            }

            private void Blit(byte[] bgra, int w, int h, float x, float y, float dw, float dh)
            {
                Vector2 a = Vector2.Transform(new Vector2(x, y), _m), b = Vector2.Transform(new Vector2(x + dw, y + dh), _m);
                // (A transform composed back to upright leaves float dust off the diagonal.)
                float dust = 1e-5f * (MathF.Abs(_m.M11) + MathF.Abs(_m.M22));
                if (MathF.Abs(_m.M12) > dust || MathF.Abs(_m.M21) > dust || b.X < a.X || b.Y < a.Y)
                {
                    // StretchDIBits draws upright, unflipped rectangles only: anything else is
                    // resampled into one first, as DriverPrint::DrawImage does -- a DIB of
                    // round(dpi / 100) device pixels a pixel over the device bounds, the
                    // parallelogram itself kept by the clip the caller set.
                    Turned(bgra, w, h, x, y, dw, dh);
                    return;
                }
                int left = (int)MathF.Round(a.X), top = (int)MathF.Round(a.Y);
                int width = (int)MathF.Round(b.X) - left, height = (int)MathF.Round(b.Y) - top;
                if (width <= 0 || height <= 0) return;
                Stretch(bgra, w, h, left, top, width, height);
            }

            private void Turned(byte[] bgra, int w, int h, float x, float y, float dw, float dh)
            {
                if (dw == 0 || dh == 0) return;
                // Local -> device for the image's unit square, and back.
                Matrix3x2 unit = Matrix3x2.CreateScale(dw, dh) * Matrix3x2.CreateTranslation(x, y) * _m;
                if (!Matrix3x2.Invert(unit, out Matrix3x2 inv)) return;
                float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
                foreach (Vector2 c in new[] { Vector2.Zero, Vector2.UnitX, Vector2.UnitY, Vector2.One })
                {
                    Vector2 d = Vector2.Transform(c, unit);
                    x0 = MathF.Min(x0, d.X); y0 = MathF.Min(y0, d.Y); x1 = MathF.Max(x1, d.X); y1 = MathF.Max(y1, d.Y);
                }
                int s = Math.Max(1, (int)(_dev._dpiX / 100f + 0.5f));
                int gx = (int)MathF.Floor(x0 / s), gy = (int)MathF.Floor(y0 / s);
                int gw = (int)MathF.Ceiling(x1 / s) - gx, gh = (int)MathF.Ceiling(y1 / s) - gy;
                if (gw <= 0 || gh <= 0 || (long)gw * gh > 64L << 20) return;
                var dib = new byte[gw * gh * 4];
                for (int j = 0; j < gh; j++)
                    for (int i = 0; i < gw; i++)
                    {
                        // The DIB pixel's centre in the image, bilinear with the edges clamped.
                        Vector2 u = Vector2.Transform(new Vector2((gx + i + 0.5f) * s, (gy + j + 0.5f) * s), inv);
                        float fx = Math.Clamp(u.X * w - 0.5f, 0, w - 1), fy = Math.Clamp(u.Y * h - 0.5f, 0, h - 1);
                        int ix = Math.Min((int)fx, w - 1), iy = Math.Min((int)fy, h - 1);
                        int jx = Math.Min(ix + 1, w - 1), jy = Math.Min(iy + 1, h - 1);
                        float ax = fx - ix, ay = fy - iy;
                        int o = (j * gw + i) * 4;
                        for (int k = 0; k < 4; k++)
                        {
                            float top = bgra[(iy * w + ix) * 4 + k] * (1 - ax) + bgra[(iy * w + jx) * 4 + k] * ax;
                            float bot = bgra[(jy * w + ix) * 4 + k] * (1 - ax) + bgra[(jy * w + jx) * 4 + k] * ax;
                            dib[o + k] = (byte)Math.Clamp((int)MathF.Round(top * (1 - ay) + bot * ay), 0, 255);
                        }
                    }
                Stretch(dib, gw, gh, gx * s, gy * s, gw * s, gh * s);
            }

            private void Stretch(byte[] bgra, int w, int h, int left, int top, int width, int height)
            {
                // What ConvertBitmapToGdi::StretchBlt sets before every blit (hooked: mode 3).
                Native.SetStretchBltMode(Dc, Native.COLORONCOLOR);
                Native.SetBrushOrgEx(Dc, 0, 0, IntPtr.Zero);
                StretchBits(bgra, w, h, 32, null, left, top, width, height, 0, 0, w, h, Native.SRCCOPY, palettize: !_scanDib);
            }

            // WF_PRINT_DUMP=dir: every bitmap put on the printer, as a PPM and a line of log --
            // what a hook on gdiplus.dll's StretchDIBits shows of stock, to compare pixel for pixel.
            private static readonly string s_dump = Environment.GetEnvironmentVariable("WF_PRINT_DUMP");
            private static int s_dumped;

            // FNV-1a of a DIB's pixel bits (row padding left out): to compare against the stock hook byte for byte.
            private static string Crc(byte[] bits, int w, int h, int bpp)
            {
                if (string.IsNullOrEmpty(s_dump)) return "";
                int stride = ((w * bpp + 31) / 32) * 4, full = w * bpp / 8, rem = w * bpp % 8;
                uint hsh = 2166136261;
                for (int r = 0; r < h; r++)
                {
                    int o = r * stride;
                    for (int i = 0; i < full; i++) { hsh ^= bits[o + i]; hsh *= 16777619; }
                    if (rem != 0) { hsh ^= (byte)(bits[o + full] & (0xFF << (8 - rem))); hsh *= 16777619; }
                }
                return " crc=" + hsh.ToString("X8");
            }

            private static void Dump(byte[] bits, int w, int h, int bpp, int x, int y, int dw, int dh, int sx, int sy, int sw, int sh, int rop, int[] palette = null, int logBpp = 0, string crc = "")
            {
                if (string.IsNullOrEmpty(s_dump)) return;
                try
                {
                    System.IO.Directory.CreateDirectory(s_dump);
                    int n = s_dumped++;
                    System.IO.File.AppendAllText(System.IO.Path.Combine(s_dump, "log.txt"),
                        $"{n:D4} StretchDIBits dst=({x},{y},{dw},{dh}) src=({sx},{sy},{sw},{sh}) bmi={w}x{h}x{(logBpp != 0 ? logBpp : bpp)} rop={rop:X}{(palette != null ? " pal=[" + string.Join(",", Array.ConvertAll(palette.Length > 16 ? palette[..16] : palette, c => ((uint)c).ToString("X6"))) + "]" : "")}{crc}\n");
                    using var f = System.IO.File.Create(System.IO.Path.Combine(s_dump, $"{n:D4}.ppm"));
                    byte[] head = System.Text.Encoding.ASCII.GetBytes($"P6\n{w} {h}\n255\n");
                    f.Write(head, 0, head.Length);
                    var row = new byte[w * 3];
                    int stride = PrintRaster.MaskStride(w);
                    for (int j = 0; j < h; j++)
                    {
                        for (int i = 0; i < w; i++)
                        {
                            if (bpp == 1)
                            {
                                bool set = (bits[j * stride + (i >> 3)] & (0x80 >> (i & 7))) != 0;
                                byte v = palette != null && palette.Length == 2 ? (byte)(palette[set ? 1 : 0] & 0xff) : set ? (byte)0 : (byte)255;
                                row[i * 3] = row[i * 3 + 1] = row[i * 3 + 2] = v;
                                continue;
                            }
                            int s = (j * w + i) * 4;
                            row[i * 3] = bits[s + 2]; row[i * 3 + 1] = bits[s + 1]; row[i * 3 + 2] = bits[s];
                        }
                        f.Write(row, 0, row.Length);
                    }
                }
                catch (Exception) { }
            }

            public void Stroke(PagePath path, SceneBrush brush, StrokeStyle style)
            {
                if (path.IsEmpty) return;
                if (GdiPen.Find(brush) is GdiPen gpen)
                {
                    GdiStroke(gpen);
                    return;
                }
                GetHdc();
                ClipForUnknown();
                RgbaColor color = brush switch
                {
                    SolidColorBrush s => s.Color,
                    SceneLinear l when l.Stops.Length > 0 => l.Stops[0].Color,
                    RadialGradientBrush r when r.Stops.Length > 0 => r.Stops[0].Color,
                    _ => new RgbaColor(0, 0, 0, 1),
                };
                if (color.A * _opacity <= 0) return;
                float scale = MathF.Sqrt(MathF.Abs(_m.M11 * _m.M22 - _m.M12 * _m.M21));
                int width = Math.Max(1, (int)MathF.Round((float)style.Thickness * scale));
                uint penStyle = Native.PS_GEOMETRIC
                    | (style.Cap == LineCap.Round ? Native.PS_ENDCAP_ROUND : style.Cap == LineCap.Square ? Native.PS_ENDCAP_SQUARE : Native.PS_ENDCAP_FLAT)
                    | (style.Join == LineJoin.Round ? Native.PS_JOIN_ROUND : style.Join == LineJoin.Bevel ? Native.PS_JOIN_BEVEL : Native.PS_JOIN_MITER);
                uint[] dashes = null;
                if (style.DashArray is double[] da && da.Length > 0)
                {
                    // In multiples of the thickness; GDI wants device units, and no zero lengths.
                    dashes = new uint[da.Length];
                    for (int i = 0; i < da.Length; i++) dashes[i] = (uint)Math.Max(1, Math.Round(da[i] * style.Thickness * scale));
                    penStyle |= Native.PS_USERSTYLE;
                }
                var lb = new Native.LOGBRUSH { lbStyle = 0, lbColor = ColorRef(color) };
                IntPtr pen = Native.ExtCreatePen(penStyle, (uint)width, ref lb, (uint)(dashes?.Length ?? 0), dashes);
                if (pen == IntPtr.Zero) return;
                IntPtr old = Native.SelectObject(Dc, pen);
                if (style.Join == LineJoin.Miter) Native.SetMiterLimit(Dc, (float)Math.Max(1, style.MiterLimit), IntPtr.Zero);
                if (BuildPath(path)) Native.StrokePath(Dc);
                Native.SelectObject(Dc, old);
                Native.DeleteObject(pen);
            }

            public bool Text(PageText run)
            {
                if (run.Face == null || run.Glyphs == null || run.Glyphs.Length == 0) return false;
                GetHdc();
                ClipForUnknown();
                float scale = MathF.Sqrt(MathF.Abs(_m.M11 * _m.M22 - _m.M12 * _m.M21));
                if (!(scale > 0)) return true;
                int em = (int)MathF.Round(run.Em * scale);
                if (em < 1) return true;
                IntPtr font = _dev.Font(run, em);
                if (font == IntPtr.Zero) return false;
                if (run.Color.A * _opacity <= 0) return true;

                // The em goes to the font in device pixels; what is left of the transform -- a
                // rotation, a flip -- to GDI's world transform. The two composed are exactly _m.
                var world = new Native.XFORM
                {
                    eM11 = _m.M11 / scale, eM12 = _m.M12 / scale, eM21 = _m.M21 / scale, eM22 = _m.M22 / scale,
                    eDx = _m.M31, eDy = _m.M32,
                };
                IntPtr oldFont = Native.SelectObject(Dc, font);
                uint oldAlign = Native.SetTextAlign(Dc, Native.TA_BASELINE);
                SetTextColor(ColorRef(run.Color));
                Native.SetWorldTransform(Dc, ref world);
                try
                {
                    // Runs of glyphs on one baseline go down together, each placed by an explicit
                    // advance taken between running totals so rounding does not accumulate.
                    int i = 0, n = run.Glyphs.Length;
                    while (i < n)
                    {
                        int j = i + 1;
                        while (j < n && run.Y[j] == run.Y[i]) j++;
                        int count = j - i;
                        var glyphs = new ushort[count];
                        var dx = new int[count];
                        for (int k = 0; k < count; k++)
                        {
                            glyphs[k] = run.Glyphs[i + k];
                            float next = k + 1 < count ? run.X[i + k + 1] : run.X[i + k] + run.Face.DesignAdvance(run.Glyphs[i + k]) * run.Em / run.Face.UnitsPerEmForHinting;
                            dx[k] = (int)MathF.Round(next * scale) - (int)MathF.Round(run.X[i + k] * scale);
                        }
                        Log($"ExtTextOutW {(int)MathF.Round(run.X[i] * scale)},{(int)MathF.Round(run.Y[i] * scale)} n={count}");
                        Native.ExtTextOut(Dc, (int)MathF.Round(run.X[i] * scale), (int)MathF.Round(run.Y[i] * scale),
                                          Native.ETO_GLYPH_INDEX, IntPtr.Zero, glyphs, (uint)count, dx);
                        i = j;
                    }
                }
                finally
                {
                    var identity = new Native.XFORM { eM11 = 1, eM22 = 1 };
                    Native.SetWorldTransform(Dc, ref identity);
                    Native.SetTextAlign(Dc, oldAlign);
                    Native.SelectObject(Dc, oldFont);
                }
                return true;
            }

            // The path in device pixels, as GDI's current path.
            private bool BuildPath(PagePath path)
            {
                Native.BeginPath(Dc);
                int k = 0;
                var bez = new Native.POINT[3];
                foreach (PagePath.Op op in path.Ops)
                {
                    switch (op)
                    {
                        case PagePath.Op.Move: { Native.POINT p = Dev(path.Points[k++]); Native.MoveToEx(Dc, p.x, p.y, IntPtr.Zero); break; }
                        case PagePath.Op.Line: { Native.POINT p = Dev(path.Points[k++]); Native.LineTo(Dc, p.x, p.y); break; }
                        case PagePath.Op.Cubic:
                            bez[0] = Dev(path.Points[k++]); bez[1] = Dev(path.Points[k++]); bez[2] = Dev(path.Points[k++]);
                            Native.PolyBezierTo(Dc, bez, 3);
                            break;
                        case PagePath.Op.Close: Native.CloseFigure(Dc); break;
                    }
                }
                return Native.EndPath(Dc);
            }

            private Native.POINT Dev(Vector2 local)
            {
                Vector2 p = Vector2.Transform(local, _m);
                return new Native.POINT { x = Clamp(p.X), y = Clamp(p.Y) };
            }

            // GDI coordinates are 27-bit on a printer DC; a "fill everything" rectangle must not wrap.
            private static int Clamp(float v) => (int)MathF.Round(Math.Clamp(v, -(1 << 26), 1 << 26));
        }

        // ---- gdi32 / msimg32 -------------------------------------------------------------------

        internal static class Native
        {
            internal const int LOGPIXELSX = 88, LOGPIXELSY = 90, HORZRES = 8, VERTRES = 10;
            internal const int PHYSICALWIDTH = 110, PHYSICALHEIGHT = 111, PHYSICALOFFSETX = 112, PHYSICALOFFSETY = 113;
            internal const int ALTERNATE = 1, WINDING = 2, RGN_AND = 1, TRANSPARENT = 1, GM_ADVANCED = 2;
            internal const int HALFTONE = 4, COLORONCOLOR = 3, SRCCOPY = 0x00CC0020, NULL_PEN = 8;
            internal const uint ETO_GLYPH_INDEX = 0x0010, TA_BASELINE = 24;
            internal const uint FR_PRIVATE = 0x10;
            internal const uint PS_GEOMETRIC = 0x00010000, PS_USERSTYLE = 7;
            internal const uint PS_ENDCAP_ROUND = 0, PS_ENDCAP_SQUARE = 0x100, PS_ENDCAP_FLAT = 0x200;
            internal const uint PS_JOIN_ROUND = 0, PS_JOIN_BEVEL = 0x1000, PS_JOIN_MITER = 0x2000;
            internal const uint GRADIENT_FILL_TRIANGLE = 2;

            [StructLayout(LayoutKind.Sequential)] internal struct POINT { public int x, y; }
            [StructLayout(LayoutKind.Sequential)] internal struct XFORM { public float eM11, eM12, eM21, eM22, eDx, eDy; }
            [StructLayout(LayoutKind.Sequential)] internal struct LOGBRUSH { public uint lbStyle; public int lbColor; public IntPtr lbHatch; }
            [StructLayout(LayoutKind.Sequential)] internal struct TRIVERTEX { public int x, y; public ushort Red, Green, Blue, Alpha; }
            [StructLayout(LayoutKind.Sequential)] internal struct GRADIENT_TRIANGLE { public uint V1, V2, V3; }
            [StructLayout(LayoutKind.Sequential)]
            internal struct BITMAPINFOHEADER
            {
                public int biSize, biWidth, biHeight; public short biPlanes, biBitCount;
                public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
            }
            [StructLayout(LayoutKind.Sequential)]
            internal struct BITMAPINFO1 { public BITMAPINFOHEADER Header; public int Color0, Color1; }
            [DllImport("gdi32.dll")] internal static extern int StretchDIBits(IntPtr dc, int x, int y, int w, int h, int sx, int sy, int sw, int sh, byte[] bits, ref BITMAPINFO1 bmi, uint usage, int rop);
            internal const int R2_MASKPEN = 9, BLACK_BRUSH = 4;
            [StructLayout(LayoutKind.Sequential)] internal struct RECT { public int l, t, r, b; }
            [DllImport("gdi32.dll")] internal static extern int SetMapMode(IntPtr dc, int mode);
            [DllImport("gdi32.dll")] internal static extern bool FillRgn(IntPtr dc, IntPtr rgn, IntPtr brush);
            [DllImport("gdi32.dll")] internal static extern bool Polyline(IntPtr dc, int[] pts, int n);
            [DllImport("gdi32.dll")] internal static extern bool PolyPolyline(IntPtr dc, int[] pts, uint[] counts, uint n);
            [DllImport("gdi32.dll", EntryPoint = "SetMiterLimit")] internal static extern bool SetMiterLimit(IntPtr dc, float limit, out float old);
            [DllImport("gdi32.dll", EntryPoint = "StretchDIBits")] internal static extern int StretchDIBitsB(IntPtr dc, int x, int y, int w, int h, int sx, int sy, int sw, int sh, byte[] bits, byte[] bmi, uint usage, int rop);
            [DllImport("gdi32.dll")] internal static extern IntPtr CreateRectRgn(int l, int t, int r, int b);
            [DllImport("gdi32.dll")] internal static extern int CombineRgn(IntPtr dst, IntPtr a, IntPtr b, int mode);
            [DllImport("gdi32.dll")] internal static extern int ExtSelectClipRgn(IntPtr dc, IntPtr rgn, int mode);
            [DllImport("gdi32.dll")] internal static extern bool PatBlt(IntPtr dc, int x, int y, int w, int h, int rop);
            [DllImport("gdi32.dll")] internal static extern IntPtr CreateDIBPatternBrushPt(byte[] packed, uint usage);
            [DllImport("gdi32.dll")] internal static extern bool Polygon(IntPtr dc, int[] pts, int n);
            [DllImport("gdi32.dll")] internal static extern bool PolyPolygon(IntPtr dc, int[] pts, int[] counts, int n);
            [DllImport("gdi32.dll")] internal static extern bool PolyBezier(IntPtr dc, int[] pts, uint n);
            [DllImport("gdi32.dll", EntryPoint = "PolyBezierTo")] internal static extern bool PolyBezierToI(IntPtr dc, int[] pts, uint n);
            [DllImport("gdi32.dll")] internal static extern bool PolylineTo(IntPtr dc, int[] pts, uint n);
            [DllImport("gdi32.dll")] internal static extern IntPtr GetCurrentObject(IntPtr dc, uint type);
            [DllImport("gdi32.dll")] internal static extern int GetROP2(IntPtr dc);
            [DllImport("gdi32.dll")] internal static extern int GetTextColor(IntPtr dc);
            [DllImport("gdi32.dll")] internal static extern int GetBkColor(IntPtr dc);
            [DllImport("gdi32.dll")] internal static extern int GetPolyFillMode(IntPtr dc);
            [DllImport("gdi32.dll")] internal static extern bool GetBrushOrgEx(IntPtr dc, out POINT p);
            [DllImport("gdi32.dll")] internal static extern int GetClipBox(IntPtr dc, out RECT r);
            [DllImport("gdi32.dll")] internal static extern int SetROP2(IntPtr dc, int rop2);
            [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
            internal struct LOGFONT
            {
                public int lfHeight, lfWidth, lfEscapement, lfOrientation, lfWeight;
                public byte lfItalic, lfUnderline, lfStrikeOut, lfCharSet, lfOutPrecision, lfClipPrecision, lfQuality, lfPitchAndFamily;
                [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string lfFaceName;
            }

            [DllImport("gdi32.dll")] internal static extern int GetDeviceCaps(IntPtr dc, int index);
            [DllImport("gdi32.dll")] internal static extern int SaveDC(IntPtr dc);
            [DllImport("gdi32.dll")] internal static extern bool RestoreDC(IntPtr dc, int state);
            [DllImport("gdi32.dll")] internal static extern int SetBkMode(IntPtr dc, int mode);
            [DllImport("gdi32.dll")] internal static extern int SetGraphicsMode(IntPtr dc, int mode);
            [DllImport("gdi32.dll")] internal static extern bool SetWorldTransform(IntPtr dc, ref XFORM xf);
            [DllImport("gdi32.dll")] internal static extern bool BeginPath(IntPtr dc);
            [DllImport("gdi32.dll")] internal static extern bool EndPath(IntPtr dc);
            [DllImport("gdi32.dll")] internal static extern bool FillPath(IntPtr dc);
            [DllImport("gdi32.dll")] internal static extern bool StrokePath(IntPtr dc);
            [DllImport("gdi32.dll")] internal static extern bool CloseFigure(IntPtr dc);
            [DllImport("gdi32.dll")] internal static extern bool SelectClipPath(IntPtr dc, int mode);
            [DllImport("gdi32.dll")] internal static extern int IntersectClipRect(IntPtr dc, int l, int t, int r, int b);
            [DllImport("gdi32.dll")] internal static extern bool MoveToEx(IntPtr dc, int x, int y, IntPtr prev);
            [DllImport("gdi32.dll")] internal static extern bool LineTo(IntPtr dc, int x, int y);
            [DllImport("gdi32.dll")] internal static extern bool PolyBezierTo(IntPtr dc, POINT[] pts, uint count);
            [DllImport("gdi32.dll")] internal static extern int SetPolyFillMode(IntPtr dc, int mode);
            [DllImport("gdi32.dll")] internal static extern IntPtr CreateSolidBrush(int color);
            [DllImport("gdi32.dll")] internal static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
            [DllImport("gdi32.dll")] internal static extern bool DeleteObject(IntPtr obj);
            [DllImport("gdi32.dll")] internal static extern IntPtr GetStockObject(int index);
            [DllImport("gdi32.dll")] internal static extern IntPtr ExtCreatePen(uint style, uint width, ref LOGBRUSH lb, uint count, uint[] styles);
            [DllImport("gdi32.dll")] internal static extern bool SetMiterLimit(IntPtr dc, float limit, IntPtr old);
            [DllImport("gdi32.dll")] internal static extern int SetStretchBltMode(IntPtr dc, int mode);
            [DllImport("gdi32.dll")] internal static extern bool SetBrushOrgEx(IntPtr dc, int x, int y, IntPtr prev);
            [DllImport("gdi32.dll")] internal static extern int StretchDIBits(IntPtr dc, int x, int y, int w, int h, int sx, int sy, int sw, int sh, byte[] bits, ref BITMAPINFOHEADER bmi, uint usage, int rop);
            [DllImport("gdi32.dll", CharSet = CharSet.Unicode, EntryPoint = "CreateFontIndirectW")] internal static extern IntPtr CreateFontIndirect(ref LOGFONT lf);
            [DllImport("gdi32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetTextFaceW")] internal static extern int GetTextFace(IntPtr dc, int count, System.Text.StringBuilder name);
            [DllImport("gdi32.dll")] internal static extern uint GetFontData(IntPtr dc, uint table, uint offset, byte[] buffer, uint size);
            [DllImport("gdi32.dll")] internal static extern uint SetTextAlign(IntPtr dc, uint align);
            [DllImport("gdi32.dll")] internal static extern int SetTextColor(IntPtr dc, int color);
            [DllImport("gdi32.dll", CharSet = CharSet.Unicode, EntryPoint = "ExtTextOutW")]
            internal static extern bool ExtTextOut(IntPtr dc, int x, int y, uint options, IntPtr rect, ushort[] glyphs, uint count, int[] dx);
            [DllImport("gdi32.dll", CharSet = CharSet.Unicode, EntryPoint = "AddFontResourceExW")] internal static extern int AddFontResourceEx(string file, uint flags, IntPtr reserved);
            [DllImport("msimg32.dll")] internal static extern bool GradientFill(IntPtr dc, TRIVERTEX[] verts, uint nverts, GRADIENT_TRIANGLE[] mesh, uint nmesh, uint mode);
        }
    }
}
