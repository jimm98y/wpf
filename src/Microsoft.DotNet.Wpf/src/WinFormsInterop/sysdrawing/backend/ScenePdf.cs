// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Recorded pages as a VECTOR PDF: what a WinForms PrintDocument becomes on every head that is not
// Windows (macOS, Linux, iOS, Android, the browser), all of whose print systems take a finished
// document. The file format, the font embedding and the image encoding are WPF's own -- PdfWriter,
// PdfFontCache, SfntReader, PdfImageCache, link-compiled from ReachFramework/Pdf -- so this is only the
// sink that turns a page's scene into a content stream.
//
// Pages are recorded in hundredths of an inch, y down from the top left of the paper. PDF's unit is
// a point and its y runs up, so every page starts with one matrix that reconciles the two and nothing
// below it thinks about either again. Text stays text: each run is glyph ids of an embedded face,
// placed one by one where the layout put them, with the characters behind them in the font's
// ToUnicode map so the page can be searched and copied. Gradients are PDF shadings rather than
// bands, curves are curves, strokes carry their pen.
//

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Text;
using System.Windows.Xps.Pdf;
using Microsoft.Wpf.Interop.WebGpu.Composition;
using Microsoft.Wpf.Interop.WebGpu.Composition.Text;
using SceneBrush = Microsoft.Wpf.Interop.WebGpu.Composition.Brush;
using SceneLinear = Microsoft.Wpf.Interop.WebGpu.Composition.LinearGradientBrush;

namespace System.Drawing.WebGpuBackend
{
    internal sealed class ScenePdfDocument : IDisposable
    {
        private readonly PdfWriter _writer;
        private readonly PdfFontCache _fonts;
        private readonly PdfImageCache _images;
        private int _rasters;   // the rasters written (PrintRaster), each its own image

        internal ScenePdfDocument(Stream destination, bool leaveOpen = false)
        {
            _writer = new PdfWriter(destination, leaveOpen);
            _fonts = new PdfFontCache(_writer);
            _images = new PdfImageCache(_writer);
        }

        internal int PageCount => _writer.PageCount;

        /// <summary>Writes one page: <paramref name="scene"/> in hundredths of an inch on paper of
        /// the given size (also hundredths of an inch).</summary>
        internal void AddPage(object scene, float paperWidth, float paperHeight)
        {
            var sink = new PdfPageSink(this, paperWidth, paperHeight);
            if (scene is SceneVisual root) ScenePageWalker.Walk(root, sink);
            sink.Finish();
        }

        internal void Close()
        {
            _fonts.WriteAll();
            _writer.Close();
        }

        public void Dispose()
        {
            Close();
            _writer.Dispose();
        }

        /// <summary>The whole document written to bytes, for a caller that hands a stream on.</summary>
        internal static byte[] Write(IReadOnlyList<(object Scene, float Width, float Height)> pages)
        {
            using var ms = new MemoryStream();
            using (var doc = new ScenePdfDocument(ms, leaveOpen: true))
                foreach ((object scene, float w, float h) in pages) doc.AddPage(scene, w, h);
            return ms.ToArray();
        }

        // ---- fonts -----------------------------------------------------------------------------

        private PdfFont FontFor(TrueTypeFont face)
            => _fonts.For(face, () =>
            {
                FaceMetrics m = FaceMetrics.Of(face);
                float upem = m.UnitsPerEm;
                return new PdfFontSource
                {
                    ReadFile = () => (face.FontData, FaceIndex(face.FontData, face.SfntOffset)),
                    FamilyName = FontFiles.ReadNames(face.FontData, face.SfntOffset, out string family, out _, out _) ? family : "Embedded",
                    Ascent = m.CellAscent / upem,
                    Descent = -m.CellDescent / upem,
                    CapHeight = m.CapHeight / upem,
                    Advance = g => face.DesignAdvance(g) / (double)upem,
                };
            });

        // Which face of a collection begins at this offset (0 for a plain font file).
        private static int FaceIndex(byte[] d, int sfntOffset)
        {
            if (sfntOffset == 0 || d.Length < 12 || d[0] != (byte)'t' || d[1] != (byte)'t' || d[2] != (byte)'c' || d[3] != (byte)'f') return 0;
            int count = (d[8] << 24) | (d[9] << 16) | (d[10] << 8) | d[11];
            for (int i = 0; i < count && 12 + i * 4 + 4 <= d.Length; i++)
            {
                int o = 12 + i * 4;
                if (((d[o] << 24) | (d[o + 1] << 16) | (d[o + 2] << 8) | d[o + 3]) == sfntOffset) return i;
            }
            return 0;
        }

        // ---- one page --------------------------------------------------------------------------

        private sealed class PdfPageSink : IPageSink
        {
            private readonly ScenePdfDocument _doc;
            private readonly float _width, _height;
            private readonly StringBuilder _c = new StringBuilder(8192);
            private readonly List<PdfFont> _fonts = new();
            private readonly List<PdfImage> _images = new();
            private readonly Dictionary<double, string> _alpha = new();
            private readonly List<(string Name, int Id)> _shadings = new();
            private readonly List<(string Name, int Id)> _patterns = new();

            // The current transform from local units to the page's default space (points, y up), and
            // the opacity groups multiply down: what a pattern (which ignores the CTM) needs to know,
            // and what a translucent container's content is drawn at.
            private Matrix3x2 _ctm;
            private float _opacity = 1f;
            private readonly Stack<(Matrix3x2 Ctm, float Opacity)> _stack = new();

            internal PdfPageSink(ScenePdfDocument doc, float width, float height)
            {
                _doc = doc; _width = width; _height = height;
                // Hundredths of an inch, y down -> points, y up.
                _ctm = new Matrix3x2(0.72f, 0, 0, -0.72f, 0, height * 0.72f);
                _c.Append("0.72 0 0 -0.72 0 ").Append(N(height * 0.72)).Append(" cm\n");
            }

            public void Save()
            {
                _stack.Push((_ctm, _opacity));
                _c.Append("q\n");
            }

            public void Restore()
            {
                if (_stack.Count == 0) return;
                (_ctm, _opacity) = _stack.Pop();
                _c.Append("Q\n");
            }

            public void Concat(Matrix3x2 m)
            {
                _ctm = m * _ctm;
                _c.Append(N(m.M11)).Append(' ').Append(N(m.M12)).Append(' ').Append(N(m.M21)).Append(' ')
                  .Append(N(m.M22)).Append(' ').Append(N(m.M31)).Append(' ').Append(N(m.M32)).Append(" cm\n");
            }

            public void ClipRect(float x, float y, float w, float h)
            {
                _c.Append(N(x)).Append(' ').Append(N(y)).Append(' ').Append(N(Math.Max(0, w))).Append(' ')
                  .Append(N(Math.Max(0, h))).Append(" re W n\n");
            }

            public void ClipPath(PagePath path)
            {
                if (path.IsEmpty) { _c.Append("0 0 0 0 re W n\n"); return; }
                AppendPath(path);
                _c.Append(path.EvenOdd ? "W* n\n" : "W n\n");
            }

            public void Opacity(float alpha) => _opacity *= Math.Clamp(alpha, 0f, 1f);

            public void Fill(PagePath path, SceneBrush brush)
            {
                if (path.IsEmpty) return;
                switch (brush)
                {
                    case SolidColorBrush solid:
                    {
                        (double r, double g, double b, double a) = ScenePageWalker.Srgb(solid.Color);
                        a *= _opacity;
                        if (a <= 0) return;
                        _c.Append("q\n");
                        Alpha(a);
                        _c.Append(N(r)).Append(' ').Append(N(g)).Append(' ').Append(N(b)).Append(" rg\n");
                        AppendPath(path);
                        _c.Append(path.EvenOdd ? "f*\n" : "f\n");
                        _c.Append("Q\n");
                        break;
                    }
                    case SceneLinear linear:
                        Shaded(path, Axial(linear));
                        break;
                    case RadialGradientBrush radial:
                    {
                        // A circle of radius 1 at the origin, scaled into the brush's ellipse.
                        int id = Shading(3, "0 0 0 0 0 1", radial.Stops);
                        string name = "Sh" + _shadings.Count.ToString(CultureInfo.InvariantCulture);
                        _shadings.Add((name, id));
                        _c.Append("q\n");
                        Alpha(_opacity * StopAlpha(radial.Stops));
                        AppendPath(path);
                        _c.Append(path.EvenOdd ? "W* n\n" : "W n\n");
                        _c.Append(N(radial.RadiusX)).Append(" 0 0 ").Append(N(radial.RadiusY)).Append(' ')
                          .Append(N(radial.Center.X)).Append(' ').Append(N(radial.Center.Y)).Append(" cm\n");
                        _c.Append('/').Append(name).Append(" sh\nQ\n");
                        break;
                    }
                    case ImageBrush image when PrintRaster.Find(image.PixelsRgba) is PrintRaster raster:
                        Raster(path, raster);
                        break;
                    case ImageBrush image when image.PixelsRgba != null && image.PixelWidth > 0 && image.PixelHeight > 0:
                        Imaged(path, image);
                        break;
                }
            }

            // A raster GDI+'s driver puts down its own way (see PrintRaster), as what it leaves on
            // the paper: the colour DIB through a stencil -- the halftone mask at the device's
            // resolution, or the runs' cells -- an explicit /Mask, set samples painted.
            private void Raster(PagePath path, PrintRaster r)
            {
                int cw, ch, mw, mh;
                byte[] rgb, bits;
                if (r.Kind == PrintRaster.KindXorPath)
                {
                    // What the XOR leaves: the bitmap inside the shape.
                    rgb = new byte[r.Width * r.Height * 3];
                    for (int i = 0; i < r.Width * r.Height; i++)
                    {
                        rgb[i * 3] = r.Color[i * 4 + 2]; rgb[i * 3 + 1] = r.Color[i * 4 + 1]; rgb[i * 3 + 2] = r.Color[i * 4];
                    }
                    var shape = new PagePath { EvenOdd = !r.ClipNonZero };
                    int n = Math.Min(r.ClipTypes.Length, r.ClipXY.Length / 2);
                    for (int i = 0; i < n; i++)
                    {
                        var p = new Vector2(r.ClipXY[i * 2], r.ClipXY[i * 2 + 1]);
                        int t = r.ClipTypes[i] & 7;
                        if (t == 0) shape.MoveTo(p);
                        else if (t == 3 && i + 2 < n)
                        {
                            shape.CubicTo(p, new Vector2(r.ClipXY[i * 2 + 2], r.ClipXY[i * 2 + 3]), new Vector2(r.ClipXY[i * 2 + 4], r.ClipXY[i * 2 + 5]));
                            i += 2;
                        }
                        else shape.LineTo(p);
                        if ((r.ClipTypes[i] & 0x80) != 0) shape.Close();
                    }
                    var plain = new PdfImage
                    {
                        ResourceName = "Ir" + (_doc._rasters++).ToString(CultureInfo.InvariantCulture),
                        ObjectId = _doc._writer.AllocateObject(),
                    };
                    _doc._writer.WriteStreamObject(plain.ObjectId, rgb, string.Concat(
                        "/Type /XObject /Subtype /Image /Width ", r.Width.ToString(CultureInfo.InvariantCulture),
                        " /Height ", r.Height.ToString(CultureInfo.InvariantCulture), " /ColorSpace /DeviceRGB /BitsPerComponent 8"));
                    _images.Add(plain);
                    (float px0, float py0, float px1, float py1) = path.Bounds();
                    _c.Append("q\n");
                    ClipPath(shape);
                    _c.Append(N(px1 - px0)).Append(" 0 0 ").Append(N(-(py1 - py0))).Append(' ')
                      .Append(N(px0)).Append(' ').Append(N(py1)).Append(" cm /").Append(plain.ResourceName).Append(" Do\nQ\n");
                    return;
                }
                if (r.Kind == PrintRaster.KindMasked)
                {
                    cw = r.SrcW; ch = r.Height; mw = r.MaskSrcW > 0 ? r.MaskSrcW : r.DevW; mh = r.MaskHeight;
                    rgb = new byte[cw * ch * 3];
                    for (int j = 0; j < ch; j++)
                        for (int i = 0; i < cw; i++)
                        {
                            int o = (j * r.Width + r.SrcX + i) * 4, d = (j * cw + i) * 3;
                            rgb[d] = r.Color[o + 2]; rgb[d + 1] = r.Color[o + 1]; rgb[d + 2] = r.Color[o];
                        }
                    int stride = (mw + 7) / 8;
                    bits = new byte[stride * mh];
                    for (int j = 0; j < mh; j++)
                        for (int i = 0; i < mw; i++)
                            if (r.MaskBit(r.MaskSrcX + i, j)) bits[j * stride + (i >> 3)] |= (byte)(0x80 >> (i & 7));
                }
                else
                {
                    cw = mw = r.Width; ch = mh = r.Height;
                    rgb = new byte[cw * ch * 3];
                    int stride = (mw + 7) / 8;
                    bits = new byte[stride * mh];
                    for (int j = 0; j < ch; j++)
                        for (int i = 0; i < cw; i++)
                        {
                            int o = (j * r.Width + i) * 4, d = (j * cw + i) * 3;
                            rgb[d] = r.Color[o + 2]; rgb[d + 1] = r.Color[o + 1]; rgb[d + 2] = r.Color[o];
                            if (r.Color[o + 3] >= 5) bits[j * stride + (i >> 3)] |= (byte)(0x80 >> (i & 7));
                        }
                }
                if (cw <= 0 || ch <= 0 || mw <= 0 || mh <= 0) return;
                PdfWriter w = _doc._writer;
                int maskId = w.AllocateObject();
                w.WriteStreamObject(maskId, bits, string.Concat(
                    "/Type /XObject /Subtype /Image /Width ", mw.ToString(CultureInfo.InvariantCulture),
                    " /Height ", mh.ToString(CultureInfo.InvariantCulture), " /ImageMask true /BitsPerComponent 1 /Decode [ 1 0 ]"));
                var image = new PdfImage
                {
                    ResourceName = "Ir" + (_doc._rasters++).ToString(CultureInfo.InvariantCulture),
                    ObjectId = w.AllocateObject(),
                };
                w.WriteStreamObject(image.ObjectId, rgb, string.Concat(
                    "/Type /XObject /Subtype /Image /Width ", cw.ToString(CultureInfo.InvariantCulture),
                    " /Height ", ch.ToString(CultureInfo.InvariantCulture),
                    " /ColorSpace /DeviceRGB /BitsPerComponent 8 /Mask ", maskId.ToString(CultureInfo.InvariantCulture), " 0 R"));
                _images.Add(image);
                (float x0, float y0, float x1, float y1) = path.Bounds();
                _c.Append("q\n");
                _c.Append(N(x1 - x0)).Append(" 0 0 ").Append(N(-(y1 - y0))).Append(' ')
                  .Append(N(x0)).Append(' ').Append(N(y1)).Append(" cm /").Append(image.ResourceName).Append(" Do\nQ\n");
            }

            private (int Id, string Name) Axial(SceneLinear linear)
            {
                int id = Shading(2, string.Concat(N(linear.Start.X), " ", N(linear.Start.Y), " ", N(linear.End.X), " ", N(linear.End.Y)), linear.Stops);
                string name = "Sh" + _shadings.Count.ToString(CultureInfo.InvariantCulture);
                _shadings.Add((name, id));
                return (id, name);
            }

            private void Shaded(PagePath path, (int Id, string Name) shading)
            {
                _c.Append("q\n");
                AppendPath(path);
                _c.Append(path.EvenOdd ? "W* n\n" : "W n\n");
                _c.Append('/').Append(shading.Name).Append(" sh\nQ\n");
            }

            private static double StopAlpha(GradientStop[] stops)
            {
                double a = 0;
                foreach (GradientStop s in stops) a = Math.Max(a, s.Color.A);
                return stops.Length == 0 ? 1 : a;
            }

            // A shading over the stops: type 2 (axial) or 3 (radial), extended at both ends as
            // GDI+'s Pad does, its colour a stitching of linear interpolations between neighbours.
            private int Shading(int type, string coords, GradientStop[] stops)
            {
                PdfWriter w = _doc._writer;
                var sorted = new List<GradientStop>(stops);
                sorted.Sort((a, b) => a.Offset.CompareTo(b.Offset));
                if (sorted.Count == 0) sorted.Add(new GradientStop(0, new RgbaColor(0, 0, 0, 1)));
                if (sorted.Count == 1) sorted.Add(new GradientStop(1, sorted[0].Color));
                if (sorted[0].Offset > 0) sorted.Insert(0, new GradientStop(0, sorted[0].Color));
                if (sorted[sorted.Count - 1].Offset < 1) sorted.Add(new GradientStop(1, sorted[sorted.Count - 1].Color));

                string Rgb(RgbaColor c)
                {
                    (double r, double g, double b, _) = ScenePageWalker.Srgb(c);
                    return string.Concat(N(r), " ", N(g), " ", N(b));
                }

                var functions = new StringBuilder("[ ");
                var bounds = new StringBuilder("[ ");
                var encode = new StringBuilder("[ ");
                for (int i = 0; i + 1 < sorted.Count; i++)
                {
                    functions.Append("<< /FunctionType 2 /Domain [ 0 1 ] /C0 [ ").Append(Rgb(sorted[i].Color))
                             .Append(" ] /C1 [ ").Append(Rgb(sorted[i + 1].Color)).Append(" ] /N 1 >> ");
                    if (i > 0) bounds.Append(N(sorted[i].Offset)).Append(' ');
                    encode.Append("0 1 ");
                }
                functions.Append(']'); bounds.Append(']'); encode.Append(']');

                int id = w.AllocateObject();
                w.WriteObject(id, string.Concat(
                    "<< /ShadingType ", type.ToString(CultureInfo.InvariantCulture), " /ColorSpace /DeviceRGB",
                    " /Coords [ ", coords, " ] /Extend [ true true ]",
                    " /Function << /FunctionType 3 /Domain [ 0 1 ] /Functions ", functions.ToString(),
                    " /Bounds ", bounds.ToString(), " /Encode ", encode.ToString(), " >> >>"));
                return id;
            }

            private void Imaged(PagePath path, ImageBrush image)
            {
                PdfImage pdfImage = _doc._images.ForRgba(image.PixelsRgba, image.PixelWidth, image.PixelHeight, () => image.PixelsRgba);
                if (pdfImage == null) return;
                if (!_images.Contains(pdfImage)) _images.Add(pdfImage);
                (float x0, float y0, float x1, float y1) = path.Bounds();

                if (image.TileMode == TileMode.None || image.TileWidth <= 0 || image.TileHeight <= 0)
                {
                    // Mapped once across the shape's bounds; the image's unit square runs up, a
                    // rectangle here runs down, hence the negative height.
                    _c.Append("q\n");
                    Alpha(_opacity * image.Opacity);
                    AppendPath(path);
                    _c.Append(path.EvenOdd ? "W* n\n" : "W n\n");
                    _c.Append(N(x1 - x0)).Append(" 0 0 ").Append(N(-(y1 - y0))).Append(' ')
                      .Append(N(x0)).Append(' ').Append(N(y1)).Append(" cm /").Append(pdfImage.ResourceName).Append(" Do\nQ\n");
                    return;
                }

                // A tile: a tiling pattern whose cell is the image. A pattern is placed in the page's
                // DEFAULT space, not the current one, so its matrix is the current transform.
                PdfWriter w = _doc._writer;
                int id = w.AllocateObject();
                float tw = image.TileWidth, th = image.TileHeight;
                string cell = string.Concat("q ", N(tw), " 0 0 ", N(-th), " 0 ", N(th), " cm /", pdfImage.ResourceName, " Do Q\n");
                byte[] content = Encoding.ASCII.GetBytes(cell);
                w.WriteStreamObject(id, content, string.Concat(
                    "/Type /Pattern /PatternType 1 /PaintType 1 /TilingType 1",
                    " /BBox [ 0 0 ", N(tw), " ", N(th), " ] /XStep ", N(tw), " /YStep ", N(th),
                    " /Matrix [ ", N(_ctm.M11), " ", N(_ctm.M12), " ", N(_ctm.M21), " ", N(_ctm.M22), " ", N(_ctm.M31), " ", N(_ctm.M32), " ]",
                    " /Resources << /XObject << /", pdfImage.ResourceName, " ", pdfImage.ObjectId.ToString(CultureInfo.InvariantCulture), " 0 R >> >>"));
                string name = "P" + _patterns.Count.ToString(CultureInfo.InvariantCulture);
                _patterns.Add((name, id));
                _c.Append("q\n");
                Alpha(_opacity * image.Opacity);
                _c.Append("/Pattern cs /").Append(name).Append(" scn\n");
                AppendPath(path);
                _c.Append(path.EvenOdd ? "f*\n" : "f\n");
                _c.Append("Q\n");
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
                (double cr, double cg, double cb, double ca) = ScenePageWalker.Srgb(color);
                ca *= _opacity;
                if (ca <= 0) return;
                _c.Append("q\n");
                Alpha(ca);
                _c.Append(N(cr)).Append(' ').Append(N(cg)).Append(' ').Append(N(cb)).Append(" RG\n");
                _c.Append(N(style.Thickness)).Append(" w\n");
                _c.Append(style.Cap == LineCap.Round ? "1 J\n" : style.Cap == LineCap.Square ? "2 J\n" : "0 J\n");
                _c.Append(style.Join == LineJoin.Round ? "1 j\n" : style.Join == LineJoin.Bevel ? "2 j\n" : "0 j\n");
                if (style.Join == LineJoin.Miter && style.MiterLimit >= 1) _c.Append(N(style.MiterLimit)).Append(" M\n");
                if (style.DashArray is double[] dashes && dashes.Length > 0)
                {
                    // In multiples of the thickness, as GDI+ states them; PDF wants lengths.
                    _c.Append("[ ");
                    foreach (double d in dashes) _c.Append(N(d * style.Thickness)).Append(' ');
                    _c.Append("] ").Append(N(style.DashOffset * style.Thickness)).Append(" d\n");
                }
                AppendPath(path);
                _c.Append("S\nQ\n");
            }

            public bool Text(PageText run)
            {
                if (run.Face == null || run.Glyphs == null || run.Glyphs.Length == 0) return false;
                PdfFont font = _doc.FontFor(run.Face);
                if (font == null) return false;
                if (!_fonts.Contains(font)) _fonts.Add(font);
                (double r, double g, double b, double a) = ScenePageWalker.Srgb(run.Color);
                a *= _opacity;
                if (a <= 0) return true;

                _c.Append("q\n");
                Alpha(a);
                _c.Append(N(r)).Append(' ').Append(N(g)).Append(' ').Append(N(b)).Append(" rg\n");
                // A simulated bold is the outline filled AND stroked a little wider; a simulated italic
                // is a shear in the text matrix. Both are what the face would be drawn as on screen.
                bool bold = run.Face.SynthesizesBold;
                float shear = run.Face.SynthesizesOblique ? 0.2126f : 0f;
                if (bold)
                {
                    _c.Append(N(r)).Append(' ').Append(N(g)).Append(' ').Append(N(b)).Append(" RG\n")
                      .Append(N(run.Em * 0.02)).Append(" w 2 Tr\n");
                }
                _c.Append("BT\n/").Append(font.ResourceName).Append(' ').Append(N(run.Em)).Append(" Tf\n");
                for (int i = 0; i < run.Glyphs.Length; i++)
                {
                    ushort glyph = run.Glyphs[i];
                    font.MapGlyph(glyph, run.Text?[i]);
                    // The page is upside down (y runs down); the text matrix turns the glyph back.
                    _c.Append("1 0 ").Append(N(shear)).Append(" -1 ").Append(N(run.X[i])).Append(' ').Append(N(run.Y[i]))
                      .Append(" Tm <").Append(glyph.ToString("X4", CultureInfo.InvariantCulture)).Append("> Tj\n");
                }
                _c.Append("ET\nQ\n");
                return true;
            }

            private void Alpha(double alpha)
            {
                if (alpha >= 1.0) return;
                alpha = Math.Max(0, Math.Round(alpha, 3));
                if (!_alpha.TryGetValue(alpha, out string name))
                {
                    name = "GS" + _alpha.Count.ToString(CultureInfo.InvariantCulture);
                    _alpha[alpha] = name;
                }
                _c.Append('/').Append(name).Append(" gs\n");
            }

            private void AppendPath(PagePath path)
            {
                int k = 0;
                foreach (PagePath.Op op in path.Ops)
                {
                    switch (op)
                    {
                        case PagePath.Op.Move: P(path.Points[k++]); _c.Append("m\n"); break;
                        case PagePath.Op.Line: P(path.Points[k++]); _c.Append("l\n"); break;
                        case PagePath.Op.Cubic: P(path.Points[k++]); P(path.Points[k++]); P(path.Points[k++]); _c.Append("c\n"); break;
                        case PagePath.Op.Close: _c.Append("h\n"); break;
                    }
                }
            }

            private void P(Vector2 p) => _c.Append(N(p.X)).Append(' ').Append(N(p.Y)).Append(' ');

            internal void Finish()
            {
                while (_stack.Count > 0) Restore();
                PdfWriter w = _doc._writer;
                int contentId = w.AllocateObject();
                int pageId = w.AllocateObject();
                string content = _c.ToString();
                var bytes = new byte[content.Length];
                for (int i = 0; i < content.Length; i++) bytes[i] = (byte)content[i];
                w.WriteStreamObject(contentId, bytes);

                var res = new StringBuilder("<< ");
                if (_fonts.Count != 0)
                {
                    res.Append("/Font << ");
                    foreach (PdfFont f in _fonts) res.Append('/').Append(f.ResourceName).Append(' ').Append(f.ObjectId.ToString(CultureInfo.InvariantCulture)).Append(" 0 R ");
                    res.Append(">> ");
                }
                if (_images.Count != 0)
                {
                    res.Append("/XObject << ");
                    foreach (PdfImage im in _images) res.Append('/').Append(im.ResourceName).Append(' ').Append(im.ObjectId.ToString(CultureInfo.InvariantCulture)).Append(" 0 R ");
                    res.Append(">> ");
                }
                if (_shadings.Count != 0)
                {
                    res.Append("/Shading << ");
                    foreach ((string name, int id) in _shadings) res.Append('/').Append(name).Append(' ').Append(id.ToString(CultureInfo.InvariantCulture)).Append(" 0 R ");
                    res.Append(">> ");
                }
                if (_patterns.Count != 0)
                {
                    res.Append("/Pattern << ");
                    foreach ((string name, int id) in _patterns) res.Append('/').Append(name).Append(' ').Append(id.ToString(CultureInfo.InvariantCulture)).Append(" 0 R ");
                    res.Append(">> ");
                }
                if (_alpha.Count != 0)
                {
                    res.Append("/ExtGState << ");
                    foreach (KeyValuePair<double, string> s in _alpha)
                        res.Append('/').Append(s.Value).Append(" << /ca ").Append(N(s.Key)).Append(" /CA ").Append(N(s.Key)).Append(" >> ");
                    res.Append(">> ");
                }
                res.Append(">>");

                w.WriteObject(pageId, string.Concat(
                    "<< /Type /Page /Parent ", w.PagesObjectId.ToString(CultureInfo.InvariantCulture), " 0 R",
                    " /MediaBox [ 0 0 ", N(_width * 0.72), " ", N(_height * 0.72), " ]",
                    " /Resources ", res.ToString(),
                    " /Contents ", contentId.ToString(CultureInfo.InvariantCulture), " 0 R >>"));
                w.AddPage(pageId);
            }

            private static string N(double v) => PdfWriter.Number(v);
        }
    }
}
