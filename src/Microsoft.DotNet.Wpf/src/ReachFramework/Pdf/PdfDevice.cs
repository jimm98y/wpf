// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The PDF backend for WPF's printing pipeline: an ILegacyDevice that writes a page description
// instead of driving a GDI device context.
//
// ILegacyDevice is the seam WPF has always had between "walk the visual tree and reduce it to
// primitives" and "put those primitives on a device". Everything above it -- VisualTreeFlattener,
// DrawingContextFlattener, MetroDevice0, Flattener -- is already managed and already portable, and
// none of it knows what a printer is. Only the implementation of these sixteen methods was ever
// Windows-specific, and only because the sole one ever written was C++/CLI over GDI.
//
// Of the sixteen, eight carry drawing and are implemented here. The other eight describe a Windows
// print job -- device contexts, DEVMODE blobs, GDI escapes -- and throw, exactly as the framework's
// own managed ILegacyDevice (Flattener.OutputContext) does.
//
// What arrives here has already been flattened: gradients have been decomposed into clipped solid
// slices, effects and 3-D rasterized to images, brushes resolved. That is the alpha flattener doing
// what it was written to do, which is reduce WPF drawing to what a plain device can express. PDF can
// express more than that -- native shadings, soft masks -- so the output is larger than it strictly
// needs to be. Text is the exception and the important one: glyph runs arrive intact and stay vector.
//

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Printing;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace System.Windows.Xps.Pdf
{
    /// <summary>Writes pages of WPF drawing into a PDF stream.</summary>
    internal sealed class PdfDevice : ILegacyDevice, IDisposable
    {
        private readonly PdfWriter _writer;
        private readonly PdfFontCache _fonts;
        private readonly PdfImageCache _images;

        private StringBuilder _content;
        private Size _pageSize;

        // How deep q/Q nesting is, so a page that ends mid-clip still closes its brackets. An
        // unbalanced content stream is not a rendering glitch, it is a file readers reject outright.
        private int _depth;

        private readonly List<PdfFont> _pageFonts = new List<PdfFont>();
        private readonly List<PdfImage> _pageImages = new List<PdfImage>();
        private readonly Dictionary<double, string> _pageAlphaStates = new Dictionary<double, string>();

        /// <summary>
        /// How finely curves are reduced to line segments, in WPF units.
        ///
        /// Tighter than WPF's 0.25 screen default, and deliberately: that default was chosen against
        /// a display, where a quarter of a 96th of an inch is a quarter pixel. A printer rasterizes
        /// this page at 600 dpi or more, where the same error is nearly two pixels and shows up as
        /// visible faceting on any large curve. At 0.05 the error is under half a pixel at 600 dpi,
        /// and the extra vertices cost bytes in a file that is already dominated by embedded fonts.
        /// </summary>
        private const double PrintTolerance = 0.05;

        /// <summary>
        /// The page size in WPF units. Set before StartPage; defaults to US Letter, which is what
        /// PrintDialog reports when there is no print system to ask.
        /// </summary>
        internal Size PageSize
        {
            get => _pageSize;
            set => _pageSize = value;
        }

        internal PdfDevice(Stream destination, bool leaveOpen = false)
        {
            _writer = new PdfWriter(destination, leaveOpen);
            _fonts = new PdfFontCache(_writer);
            _images = new PdfImageCache(_writer);
            _pageSize = new Size(816, 1056);
        }

        // ---- document and page lifecycle -------------------------------------------

        public int StartDocument(string printerName, string jobName, string filename, byte[] deviceMode)
        {
            // There is no device context to create: the "device" is a stream that already exists.
            // A job id of 1 is what the GDI implementation returns for a successful start.
            return 1;
        }

        public void StartDocumentWithoutCreatingDC(string printerName, string jobName, string filename)
        {
        }

        public void EndDocument()
        {
            _fonts.WriteAll();
            _writer.Close();
        }

        public void StartPage(byte[] deviceMode, int rasterizationDPI)
        {
            _content = new StringBuilder(4096);
            _depth = 0;
            _pageFonts.Clear();
            _pageImages.Clear();
            _pageAlphaStates.Clear();

            // Everything above this call thinks in WPF's space: 96ths of an inch, y down from the
            // top left. PDF's is 72nds, y up from the bottom left. One matrix reconciles them, and
            // then nothing else in this file has to think about it again.
            _content.Append(PdfWriter.PageMatrix(_pageSize.Height));
        }

        public void EndPage()
        {
            if (_content == null) return;

            while (_depth > 0) { _content.Append("Q\n"); _depth--; }

            int contentId = _writer.AllocateObject();
            int pageId = _writer.AllocateObject();

            _writer.WriteStreamObject(contentId, Latin1(_content.ToString()));

            var resources = new StringBuilder("<< ");

            if (_pageFonts.Count != 0)
            {
                resources.Append("/Font << ");
                foreach (PdfFont font in _pageFonts)
                {
                    resources.Append(PdfWriter.Name(font.ResourceName)).Append(' ')
                             .Append(font.ObjectId.ToString(CultureInfo.InvariantCulture)).Append(" 0 R ");
                }
                resources.Append(">> ");
            }

            if (_pageImages.Count != 0)
            {
                resources.Append("/XObject << ");
                foreach (PdfImage image in _pageImages)
                {
                    resources.Append(PdfWriter.Name(image.ResourceName)).Append(' ')
                             .Append(image.ObjectId.ToString(CultureInfo.InvariantCulture)).Append(" 0 R ");
                }
                resources.Append(">> ");
            }

            if (_pageAlphaStates.Count != 0)
            {
                resources.Append("/ExtGState << ");
                foreach (KeyValuePair<double, string> state in _pageAlphaStates)
                {
                    resources.Append(PdfWriter.Name(state.Value))
                             .Append(" << /ca ").Append(PdfWriter.Number(state.Key))
                             .Append(" /CA ").Append(PdfWriter.Number(state.Key)).Append(" >> ");
                }
                resources.Append(">> ");
            }

            resources.Append(">>");

            _writer.WriteObject(pageId, string.Concat(
                "<< /Type /Page /Parent ", _writer.PagesObjectId.ToString(CultureInfo.InvariantCulture), " 0 R",
                " /MediaBox [ 0 0 ", PdfWriter.Number(_pageSize.Width * PdfWriter.WpfToPdf), ' ',
                PdfWriter.Number(_pageSize.Height * PdfWriter.WpfToPdf), " ]",
                " /Resources ", resources.ToString(),
                " /Contents ", contentId.ToString(CultureInfo.InvariantCulture), " 0 R >>"));

            _writer.AddPage(pageId);
            _content = null;
        }

        // ---- the eight that carry drawing ------------------------------------------

        public void PushTransform(Matrix transform)
        {
            _content.Append("q ")
                    .Append(PdfWriter.Number(transform.M11)).Append(' ')
                    .Append(PdfWriter.Number(transform.M12)).Append(' ')
                    .Append(PdfWriter.Number(transform.M21)).Append(' ')
                    .Append(PdfWriter.Number(transform.M22)).Append(' ')
                    .Append(PdfWriter.Number(transform.OffsetX)).Append(' ')
                    .Append(PdfWriter.Number(transform.OffsetY)).Append(" cm\n");
            _depth++;
        }

        public void PopTransform() => Pop();

        public void PushClip(Geometry clipGeometry)
        {
            _content.Append("q\n");
            _depth++;

            if (clipGeometry == null) return;

            PathGeometry path = clipGeometry.GetFlattenedPathGeometry(PrintTolerance, ToleranceType.Absolute);
            if (path == null || path.Figures.Count == 0)
            {
                // An empty clip means nothing may be drawn. Saying so with a degenerate path is
                // clearer than omitting the clip, which would let everything through.
                _content.Append("0 0 0 0 re W n\n");
                return;
            }

            AppendPath(path);
            _content.Append(path.FillRule == FillRule.EvenOdd ? "W* n\n" : "W n\n");
        }

        public void PopClip() => Pop();

        public void DrawGeometry(Brush brush, Pen pen, Brush strokeBrush, Geometry geometry)
        {
            if (geometry == null) return;

            PathGeometry path = geometry.GetFlattenedPathGeometry(PrintTolerance, ToleranceType.Absolute);
            if (path == null || path.Figures.Count == 0) return;

            bool fill = TryColor(brush, out Color fillColor, out double fillAlpha);
            bool stroke = pen != null && TryColor(strokeBrush ?? pen.Brush, out _, out _);

            if (!fill && !stroke) return;

            _content.Append("q\n");
            _depth++;

            double alpha = fill ? fillAlpha : 1.0;

            if (stroke)
            {
                TryColor(strokeBrush ?? pen.Brush, out Color strokeColor, out double strokeAlpha);
                alpha = Math.Min(alpha, strokeAlpha);

                _content.Append(PdfWriter.Number(strokeColor.R / 255.0)).Append(' ')
                        .Append(PdfWriter.Number(strokeColor.G / 255.0)).Append(' ')
                        .Append(PdfWriter.Number(strokeColor.B / 255.0)).Append(" RG\n");

                AppendPenState(pen);
            }

            if (fill)
            {
                _content.Append(PdfWriter.Number(fillColor.R / 255.0)).Append(' ')
                        .Append(PdfWriter.Number(fillColor.G / 255.0)).Append(' ')
                        .Append(PdfWriter.Number(fillColor.B / 255.0)).Append(" rg\n");
            }

            AppendAlpha(alpha);
            AppendPath(path);

            bool evenOdd = path.FillRule == FillRule.EvenOdd;
            if (fill && stroke) _content.Append(evenOdd ? "B*\n" : "B\n");
            else if (fill) _content.Append(evenOdd ? "f*\n" : "f\n");
            else _content.Append("S\n");

            Pop();
        }

        public void DrawImage(BitmapSource source, byte[] buffer, Rect rect)
        {
            if (rect.IsEmpty || rect.Width <= 0 || rect.Height <= 0) return;

            // The flattener hands over a raw Pbgra32 buffer when it rasterized the image itself; the
            // contract is the source's own dimensions with a width*4 stride.
            if (buffer != null && source != null)
            {
                source = BitmapSource.Create(source.PixelWidth, source.PixelHeight, 96, 96,
                                             PixelFormats.Pbgra32, null, buffer, source.PixelWidth * 4);
            }

            PdfImage image = _images.For(source);
            if (image == null) return;

            if (!_pageImages.Contains(image)) _pageImages.Add(image);

            // An image XObject draws into the unit square, so the matrix both places and sizes it.
            // The y flip is because the unit square runs up and a WPF rect runs down.
            _content.Append("q ")
                    .Append(PdfWriter.Number(rect.Width)).Append(" 0 0 ")
                    .Append(PdfWriter.Number(-rect.Height)).Append(' ')
                    .Append(PdfWriter.Number(rect.X)).Append(' ')
                    .Append(PdfWriter.Number(rect.Y + rect.Height)).Append(" cm ")
                    .Append(PdfWriter.Name(image.ResourceName)).Append(" Do Q\n");
        }

        public void DrawGlyphRun(Brush foreground, GlyphRun glyphRun)
        {
            if (glyphRun == null || glyphRun.GlyphIndices == null || glyphRun.GlyphIndices.Count == 0) return;
            if (!TryColor(foreground, out Color color, out double alpha)) return;

            PdfFont font = _fonts.For(glyphRun.GlyphTypeface);
            if (font == null) return;

            if (!_pageFonts.Contains(font)) _pageFonts.Add(font);

            _content.Append("q\n");
            _depth++;

            AppendAlpha(alpha);
            _content.Append(PdfWriter.Number(color.R / 255.0)).Append(' ')
                    .Append(PdfWriter.Number(color.G / 255.0)).Append(' ')
                    .Append(PdfWriter.Number(color.B / 255.0)).Append(" rg\n");

            _content.Append("BT\n")
                    .Append(PdfWriter.Name(font.ResourceName)).Append(' ')
                    .Append(PdfWriter.Number(glyphRun.FontRenderingEmSize)).Append(" Tf\n");

            // The text matrix carries the flip back: the page matrix already turned the whole page
            // upside down, and text drawn through it would come out mirrored. Undoing it here rather
            // than at the page level keeps paths and images working in WPF's natural direction.
            Point origin = glyphRun.BaselineOrigin;
            _content.Append("1 0 0 -1 ")
                    .Append(PdfWriter.Number(origin.X)).Append(' ')
                    .Append(PdfWriter.Number(origin.Y)).Append(" Tm\n");

            AppendGlyphs(glyphRun, font);

            _content.Append("ET\n");
            Pop();
        }

        public void Comment(string message)
        {
            if (string.IsNullOrEmpty(message) || _content == null) return;

            _content.Append('%');
            foreach (char c in message)
            {
                _content.Append(c == '\r' || c == '\n' ? ' ' : c);
            }
            _content.Append('\n');
        }

        // ---- the eight that describe a Windows print job ---------------------------

        public void CreateDeviceContext(string printerName, string jobName, byte[] deviceMode)
            => throw new InvalidOperationException();

        public void DeleteDeviceContext() => throw new InvalidOperationException();

        public string ExtEscGetName() => throw new InvalidOperationException();

        public bool ExtEscMXDWPassThru() => throw new InvalidOperationException();

        // ---- content stream helpers ------------------------------------------------

        private void Pop()
        {
            if (_depth <= 0) return;

            _content.Append("Q\n");
            _depth--;
        }

        /// <summary>Emits a flattened path as PDF path-construction operators.</summary>
        private void AppendPath(PathGeometry path)
        {
            Matrix m = path.Transform?.Value ?? Matrix.Identity;

            foreach (PathFigure figure in path.Figures)
            {
                Point start = m.Transform(figure.StartPoint);
                _content.Append(PdfWriter.Number(start.X)).Append(' ')
                        .Append(PdfWriter.Number(start.Y)).Append(" m\n");

                foreach (PathSegment segment in figure.Segments)
                {
                    switch (segment)
                    {
                        case PolyLineSegment poly:
                            foreach (Point p in poly.Points)
                            {
                                Point t = m.Transform(p);
                                _content.Append(PdfWriter.Number(t.X)).Append(' ')
                                        .Append(PdfWriter.Number(t.Y)).Append(" l\n");
                            }
                            break;

                        case LineSegment line:
                        {
                            Point t = m.Transform(line.Point);
                            _content.Append(PdfWriter.Number(t.X)).Append(' ')
                                    .Append(PdfWriter.Number(t.Y)).Append(" l\n");
                            break;
                        }
                    }
                }

                if (figure.IsClosed) _content.Append("h\n");
            }
        }

        private void AppendPenState(Pen pen)
        {
            _content.Append(PdfWriter.Number(pen.Thickness)).Append(" w\n");

            _content.Append(pen.StartLineCap switch
            {
                PenLineCap.Round => "1 J\n",
                PenLineCap.Square => "2 J\n",
                _ => "0 J\n",   // Flat, and Triangle, which PDF has no cap for
            });

            _content.Append(pen.LineJoin switch
            {
                PenLineJoin.Round => "1 j\n",
                PenLineJoin.Bevel => "2 j\n",
                _ => "0 j\n",
            });

            if (pen.LineJoin == PenLineJoin.Miter && pen.MiterLimit > 0)
            {
                _content.Append(PdfWriter.Number(pen.MiterLimit)).Append(" M\n");
            }

            DashStyle dashes = pen.DashStyle;
            if (dashes?.Dashes != null && dashes.Dashes.Count != 0)
            {
                _content.Append("[ ");
                foreach (double dash in dashes.Dashes)
                {
                    // WPF states dashes in multiples of the pen thickness; PDF wants user units.
                    _content.Append(PdfWriter.Number(dash * pen.Thickness)).Append(' ');
                }
                _content.Append("] ").Append(PdfWriter.Number(dashes.Offset * pen.Thickness)).Append(" d\n");
            }
        }

        /// <summary>
        /// Constant alpha, through an ExtGState. PDF has no operator for it; it is a graphics-state
        /// parameter, which is why it needs a named resource rather than a number in the stream.
        /// </summary>
        private void AppendAlpha(double alpha)
        {
            if (alpha >= 1.0) return;

            alpha = Math.Max(0, Math.Round(alpha, 3));

            if (!_pageAlphaStates.TryGetValue(alpha, out string name))
            {
                name = "GS" + _pageAlphaStates.Count.ToString(CultureInfo.InvariantCulture);
                _pageAlphaStates[alpha] = name;
            }

            _content.Append(PdfWriter.Name(name)).Append(" gs\n");
        }

        /// <summary>
        /// The glyphs, as Identity-H two-byte codes with explicit positioning.
        ///
        /// Each glyph is placed from the run's own advances rather than left to the font's widths.
        /// WPF has already done the shaping -- kerning, ligatures, CJK spacing -- and the whole point
        /// of printing what was laid out is that the page matches the screen.
        /// </summary>
        private void AppendGlyphs(GlyphRun glyphRun, PdfFont font)
        {
            IList<ushort> indices = glyphRun.GlyphIndices;
            IList<double> advances = glyphRun.AdvanceWidths;
            IList<Point> offsets = glyphRun.GlyphOffsets;

            double emSize = glyphRun.FontRenderingEmSize;
            double pen = 0;

            for (int i = 0; i < indices.Count; i++)
            {
                ushort glyph = indices[i];
                font.UsedGlyphs.Add(glyph);

                double dx = 0, dy = 0;
                if (offsets != null && i < offsets.Count)
                {
                    dx = offsets[i].X;
                    dy = offsets[i].Y;
                }

                // Placed one at a time. A TJ array would be more compact, but it can only express
                // horizontal adjustment, and a glyph offset is two-dimensional -- it is how combining
                // marks and vertical text are positioned.
                _content.Append("1 0 0 -1 ")
                        .Append(PdfWriter.Number(glyphRun.BaselineOrigin.X + pen + dx)).Append(' ')
                        .Append(PdfWriter.Number(glyphRun.BaselineOrigin.Y - dy)).Append(" Tm ")
                        .Append('<').Append(glyph.ToString("X4", CultureInfo.InvariantCulture)).Append("> Tj\n");

                pen += (advances != null && i < advances.Count) ? advances[i] : 0;
            }

            _ = emSize;
        }

        /// <summary>
        /// A brush reduced to one colour. Only solid colours reach here: the alpha flattener has
        /// already decomposed gradients into clipped solid slices and rasterized everything else.
        /// </summary>
        private static bool TryColor(Brush brush, out Color color, out double alpha)
        {
            color = Colors.Black;
            alpha = 1.0;

            switch (brush)
            {
                case null:
                    return false;

                case SolidColorBrush solid:
                    color = solid.Color;
                    alpha = solid.Opacity * (solid.Color.A / 255.0);
                    return alpha > 0;

                default:
                    // Anything else would be a brush the flattener did not resolve. Drawing it black
                    // would be worse than not drawing it: a rectangle of solid black over content.
                    return false;
            }
        }

        private static byte[] Latin1(string text)
        {
            var bytes = new byte[text.Length];
            for (int i = 0; i < text.Length; i++) bytes[i] = (byte)text[i];
            return bytes;
        }

        public void Dispose()
        {
            _writer.Dispose();
        }
    }
}
