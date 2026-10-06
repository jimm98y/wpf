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
            private readonly Stack<(Matrix3x2 M, float Opacity, int Dc)> _stack = new();

            internal GdiPageSink(SceneGdiDevice dev, Matrix3x2 deviceScale)
            {
                _dev = dev;
                _m = deviceScale;
            }

            public void Save() => _stack.Push((_m, _opacity, Native.SaveDC(Dc)));

            public void Restore()
            {
                if (_stack.Count == 0) return;
                (Matrix3x2 m, float o, int dc) = _stack.Pop();
                Native.RestoreDC(Dc, dc);
                _m = m; _opacity = o;
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
                switch (brush)
                {
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
                    StretchBits(r.Mask, r.MaskWidth, r.MaskHeight, 1, new[] { 0x00FFFFFF, 0 }, left, top, width, height, r.MaskSrcX, 0,
                                r.MaskSrcW > 0 ? r.MaskSrcW : width, r.MaskHeight, SRCAND);
                    StretchBits(r.Color, r.Width, r.Height, 32, null, left, top, width, height, r.SrcX, 0, r.SrcW, r.Height, SRCINVERT);
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
                    int rop2 = Native.SetROP2(Dc, Native.R2_MASKPEN);
                    // The recording's units are the identity under _m here (BeginDeviceDraw).
                    FillSolid(shape, 0);
                    Native.SetROP2(Dc, rop2);
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

            // StretchDIBits of a top-down DIB: 32bpp BGRA, or 1bpp with its two-colour palette.
            private void StretchBits(byte[] bits, int w, int h, int bpp, int[] palette, int x, int y, int dw, int dh, int sx, int sy, int sw, int sh, int rop)
            {
                var bmi = new Native.BITMAPINFO1
                {
                    Header = new Native.BITMAPINFOHEADER
                    {
                        biSize = Marshal.SizeOf<Native.BITMAPINFOHEADER>(), biWidth = w, biHeight = -h,
                        biPlanes = 1, biBitCount = (short)bpp, biCompression = 0, biClrUsed = palette?.Length ?? 0,
                    },
                    Color0 = palette != null ? palette[0] : 0, Color1 = palette != null && palette.Length > 1 ? palette[1] : 0,
                };
                Dump(bits, w, h, bpp, x, y, dw, dh, sx, sy, sw, sh, rop);
                Native.StretchDIBits(Dc, x, y, dw, dh, sx, sy, sw, sh, bits, ref bmi, 0, rop);
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

            private void Image(PagePath path, ImageBrush image)
            {
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
                var bmi = new Native.BITMAPINFOHEADER
                {
                    biSize = Marshal.SizeOf<Native.BITMAPINFOHEADER>(), biWidth = w, biHeight = -h,
                    biPlanes = 1, biBitCount = 32, biCompression = 0,
                };
                // What ConvertBitmapToGdi::StretchBlt sets before every blit (hooked: mode 3).
                Native.SetStretchBltMode(Dc, Native.COLORONCOLOR);
                Native.SetBrushOrgEx(Dc, 0, 0, IntPtr.Zero);
                Dump(bgra, w, h, 32, left, top, width, height, 0, 0, w, h, Native.SRCCOPY);
                Native.StretchDIBits(Dc, left, top, width, height, 0, 0, w, h, bgra, ref bmi, 0, Native.SRCCOPY);
            }

            // WF_PRINT_DUMP=dir: every bitmap put on the printer, as a PPM and a line of log --
            // what a hook on gdiplus.dll's StretchDIBits shows of stock, to compare pixel for pixel.
            private static readonly string s_dump = Environment.GetEnvironmentVariable("WF_PRINT_DUMP");
            private static int s_dumped;

            private static void Dump(byte[] bits, int w, int h, int bpp, int x, int y, int dw, int dh, int sx, int sy, int sw, int sh, int rop)
            {
                if (string.IsNullOrEmpty(s_dump)) return;
                try
                {
                    System.IO.Directory.CreateDirectory(s_dump);
                    int n = s_dumped++;
                    System.IO.File.AppendAllText(System.IO.Path.Combine(s_dump, "log.txt"),
                        $"{n:D4} StretchDIBits dst=({x},{y},{dw},{dh}) src=({sx},{sy},{sw},{sh}) bmi={w}x{h}x{bpp} rop={rop:X}\n");
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
                                byte v = (bits[j * stride + (i >> 3)] & (0x80 >> (i & 7))) != 0 ? (byte)0 : (byte)255;
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
                Native.SetTextColor(Dc, ColorRef(run.Color));
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
            internal const int R2_MASKPEN = 9;
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
