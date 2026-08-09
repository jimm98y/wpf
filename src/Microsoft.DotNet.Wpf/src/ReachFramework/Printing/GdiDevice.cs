// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The Windows backend for WPF's printing pipeline: an ILegacyDevice that draws into a printer's
// device context.
//
// This is the implementation ILegacyDevice was designed around and the one this port did not have.
// The interface is unchanged from the product's -- device contexts, DEVMODE blobs, GDI escapes are
// all there in its member list -- because it was extracted from a C++/CLI class that drove GDI. Only
// that class could not come with it: it is a mixed-mode assembly, and a port that compiles on macOS
// cannot build one. Everything above the interface (VisualTreeFlattener, DrawingContextFlattener,
// MetroDevice0, Flattener) is managed, portable and already worked; this closes the loop on Windows
// the same way PdfDevice closed it everywhere else.
//
// WHAT ARRIVES HERE is what the alpha flattener leaves: solid colours, no gradients, no opacity
// masks, effects and 3-D already rasterized to images. That is not a limitation being worked around
// -- it is the flattener's purpose, and it is why a device this simple can print a WPF page
// faithfully. GDI has no transparency on a printer DC (the drivers on this machine report
// SHADEBLENDCAPS of zero), and it does not need any.
//
// TWO THINGS ARE DONE DIFFERENTLY from a naive GDI port, both for accuracy:
//
//   * Coordinates are transformed in managed code, in doubles, and reach GDI already in device
//     pixels. Letting GDI's world transform do it would quantise every point to a 96th of an inch
//     first, which at 600 dpi is six pixels of error on every edge.
//
//   * Strokes are widened to outlines and filled, rather than drawn with a GDI pen. WPF's pens have
//     dash patterns, dash caps, miter limits and non-uniform scaling that GDI's do not; widening
//     produces the exact contour WPF would have rendered, and hands GDI a job it cannot get wrong.
//

using System;
using System.Collections.Generic;
using System.Printing;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace System.Windows.Xps.Printing
{
    /// <summary>Draws pages of WPF content into a Windows printer device context.</summary>
    [SupportedOSPlatform("windows")]
    internal sealed class GdiDevice : ILegacyDevice, IDisposable
    {
        private IntPtr _dc;
        private bool _ownsDc;
        private bool _documentStarted;
        private bool _pageStarted;

        /// <summary>WPF units to device pixels: the page's own scale, and the printer's margin.</summary>
        private Matrix _deviceTransform = Matrix.Identity;

        private Matrix _transform = Matrix.Identity;
        private readonly Stack<Matrix> _transforms = new Stack<Matrix>();

        /// <summary>
        /// One entry per PushClip, saying whether it took a SaveDC that PopClip must give back.
        /// A clip that could not be established takes none, and popping it must then not restore
        /// somebody else's state -- which is the bug that makes the rest of a page lose its clip.
        /// </summary>
        private readonly Stack<bool> _clips = new Stack<bool>();

        /// <summary>The printer's own resolution, filled in when the device context is created.</summary>
        internal int DpiX { get; private set; } = 96;

        internal int DpiY { get; private set; } = 96;

        /// <summary>The paper, in WPF units, as the driver describes it.</summary>
        internal Size PaperSize { get; private set; }

        /// <summary>Where this job's output goes instead of the printer's port, if anywhere.</summary>
        internal string OutputFile { get; set; }

        internal bool HasDeviceContext => _dc != IntPtr.Zero;

        // ---- document and page lifecycle -------------------------------------------

        public int StartDocument(string printerName, string jobName, string filename, byte[] deviceMode)
        {
            CreateDeviceContext(printerName, jobName, deviceMode);
            return StartDoc(jobName, filename ?? OutputFile);
        }

        public void StartDocumentWithoutCreatingDC(string printerName, string jobName, string filename)
        {
            StartDoc(jobName, filename ?? OutputFile);
        }

        public void CreateDeviceContext(string printerName, string jobName, byte[] deviceMode)
        {
            if (_dc != IntPtr.Zero) return;

            if (string.IsNullOrEmpty(printerName))
            {
                throw new PrintSystemException("A print job needs a printer, and no name was given.");
            }

            IntPtr devMode = IntPtr.Zero;

            try
            {
                if (deviceMode != null && deviceMode.Length != 0)
                {
                    devMode = Marshal.AllocHGlobal(deviceMode.Length);
                    Marshal.Copy(deviceMode, 0, devMode, deviceMode.Length);
                }

                _dc = GdiNative.CreateDC("WINSPOOL", printerName, null, devMode);
            }
            finally
            {
                if (devMode != IntPtr.Zero) Marshal.FreeHGlobal(devMode);
            }

            if (_dc == IntPtr.Zero)
            {
                throw new PrintSystemException(
                    $"The printer '{printerName}' would not open. It may have been removed, or its " +
                    "driver may not be installed on this machine.");
            }

            _ownsDc = true;
            Measure();
        }

        public void DeleteDeviceContext()
        {
            if (_dc == IntPtr.Zero) return;

            if (_ownsDc) GdiNative.DeleteDC(_dc);

            _dc = IntPtr.Zero;
            _ownsDc = false;
        }

        public void EndDocument()
        {
            if (_dc != IntPtr.Zero && _documentStarted)
            {
                GdiNative.EndDoc(_dc);
                _documentStarted = false;
            }

            DeleteDeviceContext();
        }

        /// <summary>Abandons the job. The spooler discards what has been written; no paper comes out.</summary>
        internal void AbortDocument()
        {
            if (_dc != IntPtr.Zero && _documentStarted)
            {
                GdiNative.AbortDoc(_dc);
                _documentStarted = false;
            }

            DeleteDeviceContext();
        }

        public void StartPage(byte[] deviceMode, int rasterizationDPI)
        {
            if (_dc == IntPtr.Zero || _pageStarted) return;

            // A per-page DEVMODE is how a document changes paper part way through -- a cover sheet
            // on letterhead, a landscape table in a portrait report. ResetDC is the only way to
            // apply one, and it is legal only between pages.
            if (deviceMode != null && deviceMode.Length != 0)
            {
                IntPtr devMode = Marshal.AllocHGlobal(deviceMode.Length);

                try
                {
                    Marshal.Copy(deviceMode, 0, devMode, deviceMode.Length);
                    GdiNative.ResetDC(_dc, devMode);
                    Measure();
                }
                finally
                {
                    Marshal.FreeHGlobal(devMode);
                }
            }

            if (GdiNative.StartPage(_dc) <= 0)
            {
                throw new PrintSystemException("The printer refused a new page.");
            }

            _pageStarted = true;

            // GM_ADVANCED for the whole page: an image with a rotation in it needs a world
            // transform, and the mode cannot be changed while one is set.
            GdiNative.SetGraphicsMode(_dc, GdiNative.GM_ADVANCED);
            GdiNative.SetStretchBltMode(_dc, GdiNative.HALFTONE);
            GdiNative.SetBrushOrgEx(_dc, 0, 0, IntPtr.Zero);
            GdiNative.SetBkMode(_dc, GdiNative.TRANSPARENT);

            _transforms.Clear();
            _clips.Clear();
            _transform = Matrix.Identity;

            PushTransform(_deviceTransform);
        }

        public void EndPage()
        {
            if (_dc == IntPtr.Zero || !_pageStarted) return;

            // Whatever the page left open, close: a clip that outlives its page would apply to the
            // next one, and an unbalanced SaveDC leaks a DC state slot per page.
            while (_clips.Count != 0) PopClip();
            while (_transforms.Count != 0) PopTransform();

            GdiNative.EndPage(_dc);
            _pageStarted = false;
        }

        // ---- transforms and clips ---------------------------------------------------

        public void PushTransform(Matrix transform)
        {
            _transforms.Push(_transform);
            _transform = transform * _transform;
        }

        public void PopTransform()
        {
            if (_transforms.Count == 0) return;

            _transform = _transforms.Pop();
        }

        public void PushClip(Geometry clipGeometry)
        {
            if (_dc == IntPtr.Zero)
            {
                _clips.Push(false);
                return;
            }

            if (clipGeometry == null)
            {
                // No geometry is no restriction. Recorded so the matching PopClip stays paired.
                _clips.Push(false);
                return;
            }

            if (GdiNative.SaveDC(_dc) == 0)
            {
                _clips.Push(false);
                return;
            }

            _clips.Push(true);

            PathGeometry path = Flatten(clipGeometry);

            if (path == null || path.Figures.Count == 0)
            {
                // An empty clip admits nothing. Saying so with an empty rectangle is the difference
                // between drawing none of the content and drawing all of it.
                GdiNative.IntersectClipRect(_dc, 0, 0, 0, 0);
                return;
            }

            GdiNative.SetPolyFillMode(_dc,
                path.FillRule == FillRule.EvenOdd ? GdiNative.ALTERNATE : GdiNative.WINDING);

            if (BuildPath(path))
            {
                GdiNative.SelectClipPath(_dc, GdiNative.RGN_AND);
            }
        }

        public void PopClip()
        {
            if (_clips.Count == 0) return;

            bool saved = _clips.Pop();

            if (saved && _dc != IntPtr.Zero) GdiNative.RestoreDC(_dc, -1);
        }

        // ---- drawing ----------------------------------------------------------------

        public void DrawGeometry(Brush brush, Pen pen, Brush strokeBrush, Geometry geometry)
        {
            if (_dc == IntPtr.Zero || geometry == null) return;

            if (TryColor(brush, out Color fillColor))
            {
                Fill(geometry, fillColor);
            }

            if (pen != null && TryColor(strokeBrush ?? pen.Brush, out Color strokeColor))
            {
                Geometry outline = Widen(geometry, pen);
                if (outline != null) Fill(outline, strokeColor);
            }
        }

        /// <summary>
        /// Draws a glyph run as TEXT, through GDI's own text engine.
        ///
        /// The obvious alternative is to fill the outlines -- GlyphRun.BuildGeometry, one Fill, four
        /// lines. It does not work here and cannot: GlyphTypeface.ComputeGlyphOutline is a
        /// wpfgfx_cor3 entry point over a DirectWrite font face, and this port ships wpfgfx on no
        /// platform, so asking for an outline throws DllNotFoundException. That is a hole in
        /// PresentationCore rather than in printing -- GlyphRun.BuildGeometry and
        /// FormattedText.BuildGeometry are public API and have the same problem -- and it is not
        /// the print path's to close.
        ///
        /// Going through GDI is the better answer anyway, and it is what the product's own GDI
        /// exporter did. Text stays text: the driver embeds the font and the resulting page has
        /// selectable, searchable, copyable words on it rather than several thousand filled paths.
        ///
        /// The danger of drawing by glyph INDEX is that indices only mean anything against the
        /// exact font the run was shaped with. If GDI substitutes -- and it substitutes silently --
        /// the page fills with the wrong glyphs, which is worse than an error because it looks
        /// deliberate. Everything in Font() exists to make sure that cannot happen unnoticed.
        /// </summary>
        public void DrawGlyphRun(Brush foreground, GlyphRun glyphRun)
        {
            if (_dc == IntPtr.Zero || glyphRun == null) return;

            IList<ushort> indices = glyphRun.GlyphIndices;
            if (indices == null || indices.Count == 0) return;
            if (!TryColor(foreground, out Color color)) return;

            // The transform is split so the font can be created at a size measured in DEVICE
            // pixels: rounding an em size to whole WPF units first would quantise 14.4pt type to
            // 14pt, which at 600 dpi is two and a half pixels of error on every line. What is left
            // over -- rotation, skew, any anisotropy -- goes to GDI as a world transform, and the
            // two composed are exactly the transform we were given.
            double scale = MatrixScale(_transform);
            if (scale <= 0) return;

            double emSize = glyphRun.FontRenderingEmSize * scale;
            if (emSize < 0.5) return;                       // smaller than a device pixel

            GdiFont font = Font(glyphRun.GlyphTypeface, emSize);
            if (font == null) return;

            var world = new GdiNative.XFORM
            {
                eM11 = (float)(_transform.M11 / scale),
                eM12 = (float)(_transform.M12 / scale),
                eM21 = (float)(_transform.M21 / scale),
                eM22 = (float)(_transform.M22 / scale),
                eDx = (float)_transform.OffsetX,
                eDy = (float)_transform.OffsetY,
            };

            IntPtr previousFont = GdiNative.SelectObject(_dc, font.Handle);
            uint previousAlign = GdiNative.SetTextAlign(_dc, GdiNative.TA_LEFT | GdiNative.TA_BASELINE);
            GdiNative.SetTextColor(_dc, ColorRef(color));
            GdiNative.SetWorldTransform(_dc, ref world);

            try
            {
                if (font.Matches) DrawByIndex(glyphRun, scale);
                else DrawByCharacter(glyphRun, scale);
            }
            finally
            {
                Identity();
                GdiNative.SetTextAlign(_dc, previousAlign);
                GdiNative.SelectObject(_dc, previousFont);
            }
        }

        /// <summary>
        /// The glyphs as WPF placed them.
        ///
        /// Positions are computed per glyph and emitted as an explicit advance array rather than
        /// letting GDI advance by the font's own widths. WPF has already done the shaping --
        /// kerning, ligatures, justification -- and the entire point of printing what was laid out
        /// is that the page matches the screen. Differences are taken between running totals so a
        /// half-pixel rounding on one glyph does not accumulate across a line.
        /// </summary>
        private void DrawByIndex(GlyphRun glyphRun, double scale)
        {
            IList<ushort> indices = glyphRun.GlyphIndices;
            IList<double> advances = glyphRun.AdvanceWidths;
            IList<Point> offsets = glyphRun.GlyphOffsets;

            Point origin = glyphRun.BaselineOrigin;

            // An odd bidi level is a right-to-left run, whose glyphs advance leftwards from the
            // origin. IsLeftToRight says the same thing and is internal to PresentationCore.
            bool leftToRight = (glyphRun.BidiLevel & 1) == 0;

            // A run with per-glyph offsets, or one that runs right to left, cannot be described by
            // a single origin and a list of advances. Those go down one glyph at a time.
            bool positioned = !leftToRight;

            if (offsets != null)
            {
                for (int i = 0; i < offsets.Count && !positioned; i++)
                {
                    if (offsets[i].X != 0 || offsets[i].Y != 0) positioned = true;
                }
            }

            var glyphs = new ushort[1];

            if (positioned)
            {
                double advance = 0;

                for (int i = 0; i < indices.Count; i++)
                {
                    Point offset = (offsets != null && i < offsets.Count) ? offsets[i] : default;

                    double x = leftToRight ? advance + offset.X : -advance - offset.X;
                    double y = -offset.Y;

                    glyphs[0] = indices[i];

                    GdiNative.ExtTextOut(_dc,
                                         Round((origin.X + x) * scale),
                                         Round((origin.Y + y) * scale),
                                         GdiNative.ETO_GLYPH_INDEX, IntPtr.Zero, glyphs, 1, null);

                    advance += (advances != null && i < advances.Count) ? advances[i] : 0;
                }

                return;
            }

            var run = new ushort[indices.Count];
            var dx = new int[indices.Count];

            double pen = 0;

            for (int i = 0; i < indices.Count; i++)
            {
                run[i] = indices[i];

                double next = pen + ((advances != null && i < advances.Count) ? advances[i] : 0);

                dx[i] = Round(next * scale) - Round(pen * scale);
                pen = next;
            }

            GdiNative.ExtTextOut(_dc, Round(origin.X * scale), Round(origin.Y * scale),
                                 GdiNative.ETO_GLYPH_INDEX, IntPtr.Zero, run, run.Length, dx);
        }

        /// <summary>
        /// The run's characters, when the font GDI gave us is not the one the run was shaped with.
        ///
        /// Indices would be meaningless against a substituted face, but characters are not: the
        /// words come out right, in a font of the right family, at the right place on the page.
        /// Kerning and ligatures are whatever the substitute does. It is a worse page than the
        /// screen and a far better one than a blank space where a paragraph should be.
        /// </summary>
        private void DrawByCharacter(GlyphRun glyphRun, double scale)
        {
            IList<char> characters = glyphRun.Characters;
            if (characters == null || characters.Count == 0) return;

            var text = new char[characters.Count];
            characters.CopyTo(text, 0);

            Point origin = glyphRun.BaselineOrigin;

            GdiNative.ExtTextOutString(_dc, Round(origin.X * scale), Round(origin.Y * scale),
                                       0, IntPtr.Zero, new string(text), text.Length, null);
        }

        public void DrawImage(BitmapSource source, byte[] buffer, Rect rect)
        {
            if (_dc == IntPtr.Zero || source == null) return;
            if (rect.IsEmpty || rect.Width <= 0 || rect.Height <= 0) return;

            // A raw Pbgra32 buffer means the flattener rasterized this itself; the contract is the
            // source's own dimensions with a width*4 stride.
            if (buffer != null)
            {
                source = BitmapSource.Create(source.PixelWidth, source.PixelHeight, 96, 96,
                                             PixelFormats.Pbgra32, null, buffer, source.PixelWidth * 4);
            }

            int width = source.PixelWidth;
            int height = source.PixelHeight;
            if (width <= 0 || height <= 0) return;

            byte[] bits = Opaque(source, width, height);
            if (bits == null) return;

            var header = new GdiNative.BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<GdiNative.BITMAPINFOHEADER>(),
                biWidth = width,

                // Negative for a top-down bitmap, which is the order WPF's pixels are already in.
                biHeight = -height,
                biPlanes = 1,
                biBitCount = 32,
                biCompression = GdiNative.BI_RGB,
                biSizeImage = (uint)(width * height * 4),
            };

            bool axisAligned = IsAxisAligned(_transform);

            if (!axisAligned)
            {
                // A rotation or a skew cannot be folded into a destination rectangle, so for this
                // one call GDI is given the matrix and the rectangle in its own space.
                var xform = new GdiNative.XFORM
                {
                    eM11 = (float)_transform.M11,
                    eM12 = (float)_transform.M12,
                    eM21 = (float)_transform.M21,
                    eM22 = (float)_transform.M22,
                    eDx = (float)_transform.OffsetX,
                    eDy = (float)_transform.OffsetY,
                };

                GdiNative.SetWorldTransform(_dc, ref xform);

                GdiNative.StretchDIBits(_dc,
                                        Round(rect.X), Round(rect.Y), Round(rect.Width), Round(rect.Height),
                                        0, 0, width, height,
                                        bits, ref header, GdiNative.DIB_RGB_COLORS, GdiNative.SRCCOPY);

                Identity();
                return;
            }

            Point topLeft = _transform.Transform(rect.TopLeft);
            Point bottomRight = _transform.Transform(rect.BottomRight);

            int x = Round(Math.Min(topLeft.X, bottomRight.X));
            int y = Round(Math.Min(topLeft.Y, bottomRight.Y));
            int w = Round(Math.Abs(bottomRight.X - topLeft.X));
            int h = Round(Math.Abs(bottomRight.Y - topLeft.Y));

            if (w <= 0 || h <= 0) return;

            // A mirroring transform survives as a negative extent, which StretchDIBits reads as a
            // flip. Recovering it from the sign of the matrix keeps mirrored content mirrored.
            if (bottomRight.X < topLeft.X) { x += w; w = -w; }
            if (bottomRight.Y < topLeft.Y) { y += h; h = -h; }

            GdiNative.StretchDIBits(_dc, x, y, w, h, 0, 0, width, height,
                                    bits, ref header, GdiNative.DIB_RGB_COLORS, GdiNative.SRCCOPY);
        }

        public void Comment(string message)
        {
        }

        /// <summary>
        /// Whether this device context is the Microsoft XPS Document Writer in passthrough mode.
        ///
        /// It is not, and cannot be: passthrough means handing MXDW a finished XPS package instead
        /// of drawing, and this port renders rather than serializing to XPS. False is the answer
        /// that sends the caller down the drawing path, which is the one that works.
        /// </summary>
        public bool ExtEscMXDWPassThru() => false;

        public string ExtEscGetName() => null;

        // ---- the work behind the drawing --------------------------------------------

        private int StartDoc(string jobName, string outputFile)
        {
            if (_dc == IntPtr.Zero || _documentStarted) return 0;

            var info = new GdiNative.DOCINFO
            {
                lpszDocName = string.IsNullOrEmpty(jobName) ? "WPF document" : jobName,
                lpszOutput = string.IsNullOrEmpty(outputFile) ? null : outputFile,
            };

            info.cbSize = Marshal.SizeOf(info);

            int job = GdiNative.StartDoc(_dc, ref info);

            if (job <= 0)
            {
                int error = Marshal.GetLastWin32Error();

                // 1223 is ERROR_CANCELLED: the user dismissed the driver's own dialog, which for a
                // printer on the PORTPROMPT port is the Save As box. A cancelled print is not a
                // failure to report, it is a print that did not happen.
                throw new PrintSystemException(error == 1223
                    ? "The print job was cancelled."
                    : $"The printer would not start a job (Win32 error {error}).");
            }

            _documentStarted = true;
            return job;
        }

        /// <summary>
        /// Reads the page geometry from the driver and builds the WPF-to-device matrix.
        ///
        /// The translation is the part that is easy to miss. WPF measures from the top left of the
        /// PAPER; GDI measures from the top left of the part the printer can mark, which on most
        /// printers is a few millimetres in. Without it every page prints shifted down and right by
        /// the size of the hardware margin.
        /// </summary>
        private void Measure()
        {
            DpiX = Math.Max(1, GdiNative.GetDeviceCaps(_dc, GdiNative.LOGPIXELSX));
            DpiY = Math.Max(1, GdiNative.GetDeviceCaps(_dc, GdiNative.LOGPIXELSY));

            int physicalWidth = GdiNative.GetDeviceCaps(_dc, GdiNative.PHYSICALWIDTH);
            int physicalHeight = GdiNative.GetDeviceCaps(_dc, GdiNative.PHYSICALHEIGHT);

            PaperSize = new Size(physicalWidth * 96.0 / DpiX, physicalHeight * 96.0 / DpiY);

            var transform = Matrix.Identity;
            transform.Scale(DpiX / 96.0, DpiY / 96.0);
            transform.Translate(-GdiNative.GetDeviceCaps(_dc, GdiNative.PHYSICALOFFSETX),
                                -GdiNative.GetDeviceCaps(_dc, GdiNative.PHYSICALOFFSETY));

            _deviceTransform = transform;
        }

        private void Fill(Geometry geometry, Color color)
        {
            PathGeometry path = Flatten(geometry);
            if (path == null || path.Figures.Count == 0) return;

            IntPtr brush = GdiNative.CreateSolidBrush(ColorRef(color));
            if (brush == IntPtr.Zero) return;

            IntPtr previous = GdiNative.SelectObject(_dc, brush);

            try
            {
                GdiNative.SetPolyFillMode(_dc,
                    path.FillRule == FillRule.EvenOdd ? GdiNative.ALTERNATE : GdiNative.WINDING);

                if (BuildPath(path)) GdiNative.FillPath(_dc);
            }
            finally
            {
                GdiNative.SelectObject(_dc, previous);
                GdiNative.DeleteObject(brush);
            }
        }

        /// <summary>
        /// Emits a flattened path into the device context between BeginPath and EndPath.
        ///
        /// Returns false when nothing usable was emitted, in which case the bracket has been
        /// abandoned rather than closed -- leaving an empty path open would attach itself to the
        /// next FillPath and paint something nobody asked for.
        /// </summary>
        private bool BuildPath(PathGeometry path)
        {
            Matrix local = path.Transform?.Value ?? Matrix.Identity;
            Matrix matrix = local * _transform;

            if (!GdiNative.BeginPath(_dc)) return false;

            bool any = false;
            var points = new List<GdiNative.POINT>(64);

            foreach (PathFigure figure in path.Figures)
            {
                points.Clear();

                foreach (PathSegment segment in figure.Segments)
                {
                    switch (segment)
                    {
                        case PolyLineSegment poly:
                            foreach (Point p in poly.Points) points.Add(Device(matrix, p));
                            break;

                        case LineSegment line:
                            points.Add(Device(matrix, line.Point));
                            break;
                    }
                }

                if (points.Count == 0) continue;

                GdiNative.POINT start = Device(matrix, figure.StartPoint);
                GdiNative.MoveToEx(_dc, start.x, start.y, IntPtr.Zero);
                GdiNative.PolylineTo(_dc, points.ToArray(), points.Count);

                if (figure.IsClosed) GdiNative.CloseFigure(_dc);

                any = true;
            }

            if (!any)
            {
                GdiNative.AbortPath(_dc);
                return false;
            }

            return GdiNative.EndPath(_dc);
        }

        /// <summary>
        /// A geometry reduced to line segments, at a tolerance chosen for THIS printer.
        ///
        /// WPF's default flattening tolerance is a quarter of a 96th of an inch, picked against a
        /// display where that is a quarter of a pixel. On a 600 dpi printer the same number is a
        /// pixel and a half, and large curves come out visibly faceted. Scaling the tolerance by
        /// the transform keeps the error a quarter of a DEVICE pixel wherever the page is drawn,
        /// and costs vertices only where the drawing is actually large.
        /// </summary>
        private PathGeometry Flatten(Geometry geometry)
        {
            double scale = Math.Max(MatrixScale(_transform), 0.01);

            try
            {
                return geometry.GetFlattenedPathGeometry(0.25 / scale, ToleranceType.Absolute);
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }

        /// <summary>
        /// The contour of a stroke, as a fillable outline.
        ///
        /// Widening rather than stroking is what lets dash patterns, dash caps, miter limits and
        /// anisotropic scaling come out right: GDI's geometric pen has none of those, and its width
        /// is a single number in device units, which a transform that scales x and y differently
        /// cannot be reduced to.
        /// </summary>
        private Geometry Widen(Geometry geometry, Pen pen)
        {
            double scale = Math.Max(MatrixScale(_transform), 0.01);

            try
            {
                return geometry.GetWidenedPathGeometry(pen, 0.25 / scale, ToleranceType.Absolute);
            }
            catch (InvalidOperationException)
            {
                return null;
            }
            catch (ArgumentException)
            {
                return null;
            }
        }

        // ---- fonts -------------------------------------------------------------------

        /// <summary>An HFONT and whether GDI actually gave us the face we asked for.</summary>
        private sealed class GdiFont : IDisposable
        {
            internal IntPtr Handle;

            /// <summary>
            /// Whether the selected face is the one the glyph run was shaped against. False means
            /// GDI substituted, and glyph indices must not be used.
            /// </summary>
            internal bool Matches;

            public void Dispose()
            {
                if (Handle != IntPtr.Zero) GdiNative.DeleteObject(Handle);
                Handle = IntPtr.Zero;
            }
        }

        private readonly Dictionary<(string, int, int, bool), GdiFont> _fonts =
            new Dictionary<(string, int, int, bool), GdiFont>();

        /// <summary>Font files handed to GDI privately, so it is done once per process, not per run.</summary>
        private static readonly HashSet<string> s_installed =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// An HFONT for a typeface at a size in device pixels.
        ///
        /// The font file is registered with GDI first. This port resolves fonts itself, from files
        /// on disk, and deliberately does not go through the system font collection -- so a face it
        /// is perfectly happy with may be one GDI has never heard of. AddFontResourceEx with
        /// FR_PRIVATE makes it nameable for this process only, which is what a printing application
        /// should do to the machine's font list: nothing permanent.
        /// </summary>
        private GdiFont Font(GlyphTypeface typeface, double emSize)
        {
            if (typeface == null) return null;

            string face = FaceName(typeface);
            if (string.IsNullOrEmpty(face)) return null;

            int height = Math.Max(1, (int)Math.Round(emSize));
            int weight = typeface.Weight.ToOpenTypeWeight();
            bool italic = typeface.Style != FontStyles.Normal;

            var key = (face, height, weight, italic);
            if (_fonts.TryGetValue(key, out GdiFont cached)) return cached;

            Install(typeface);

            var description = new GdiNative.LOGFONT
            {
                // Negative asks for the EM size rather than the cell height, which is what a WPF
                // rendering em size means. Positive would come out about 20 per cent too large.
                lfHeight = -height,
                lfWeight = weight,
                lfItalic = (byte)(italic ? 1 : 0),
                lfCharSet = (byte)GdiNative.DEFAULT_CHARSET,

                // TrueType only. A bitmap face cannot be addressed by glyph index at all, and
                // letting GDI pick one is how a run silently turns into the wrong thing.
                lfOutPrecision = (byte)GdiNative.OUT_TT_ONLY_PRECIS,
                lfClipPrecision = (byte)GdiNative.CLIP_DEFAULT_PRECIS,
                lfQuality = (byte)GdiNative.ANTIALIASED_QUALITY,
                lfPitchAndFamily = (byte)GdiNative.DEFAULT_PITCH,
                lfFaceName = face.Length >= GdiNative.LF_FACESIZE
                    ? face.Substring(0, GdiNative.LF_FACESIZE - 1)
                    : face,
            };

            IntPtr handle = GdiNative.CreateFontIndirect(ref description);

            var font = new GdiFont
            {
                Handle = handle,
                Matches = handle != IntPtr.Zero && Verify(handle, description.lfFaceName, typeface),
            };

            _fonts[key] = font;
            return handle == IntPtr.Zero ? null : font;
        }

        /// <summary>
        /// Whether the font GDI selected is the one that was asked for.
        ///
        /// Two checks, because either alone is fooled. The face name catches a substitution to an
        /// unrelated family. The glyph count -- read from the live face's own 'maxp' table -- catches
        /// the case that actually bites: a different CUT of the same family, where the name matches
        /// and the glyph order does not, so every index lands on a neighbouring letter.
        /// </summary>
        private bool Verify(IntPtr font, string requested, GlyphTypeface typeface)
        {
            IntPtr previous = GdiNative.SelectObject(_dc, font);

            try
            {
                var selected = new System.Text.StringBuilder(GdiNative.LF_FACESIZE);

                if (GdiNative.GetTextFace(_dc, GdiNative.LF_FACESIZE, selected) == 0) return false;

                if (!string.Equals(selected.ToString(), requested, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                // 'maxp' begins with a 32-bit version and then numGlyphs, big-endian.
                var maxp = new byte[2];

                if (GdiNative.GetFontData(_dc, GdiNative.TableMaxp, 4, maxp, 2) != 2) return false;

                int glyphs = (maxp[0] << 8) | maxp[1];
                return glyphs == typeface.GlyphCount;
            }
            catch (NotSupportedException)
            {
                return false;
            }
            finally
            {
                GdiNative.SelectObject(_dc, previous);
            }
        }

        /// <summary>Makes a typeface's file nameable to GDI in this process. Harmless if it already is.</summary>
        private static void Install(GlyphTypeface typeface)
        {
            Uri uri = typeface.FontUri;
            if (uri == null || !uri.IsAbsoluteUri || !uri.IsFile) return;

            string path;

            try
            {
                path = uri.LocalPath;
            }
            catch (InvalidOperationException)
            {
                return;
            }

            if (string.IsNullOrEmpty(path)) return;

            lock (s_installed)
            {
                if (!s_installed.Add(path)) return;
            }

            try
            {
                GdiNative.AddFontResourceEx(path, GdiNative.FR_PRIVATE, IntPtr.Zero);
            }
            catch (DllNotFoundException)
            {
            }
            catch (EntryPointNotFoundException)
            {
            }
        }

        /// <summary>
        /// The name GDI knows this typeface by.
        ///
        /// Win32FamilyNames first: it is the legacy family name, the one that splits weights into
        /// separate families ("Segoe UI Semibold" rather than "Segoe UI" plus a weight), and it is
        /// the name GDI's font matcher works in. FamilyNames is the typographic name and is what
        /// DirectWrite uses; asking GDI for that one is how a semibold run comes out regular.
        /// </summary>
        private static string FaceName(GlyphTypeface typeface)
        {
            string name = Pick(typeface.Win32FamilyNames) ?? Pick(typeface.FamilyNames);
            return name;
        }

        private static string Pick(IDictionary<System.Globalization.CultureInfo, string> names)
        {
            if (names == null || names.Count == 0) return null;

            System.Globalization.CultureInfo current = System.Globalization.CultureInfo.CurrentUICulture;

            if (names.TryGetValue(current, out string exact) && !string.IsNullOrEmpty(exact)) return exact;

            if (names.TryGetValue(System.Globalization.CultureInfo.InvariantCulture, out string invariant) &&
                !string.IsNullOrEmpty(invariant))
            {
                return invariant;
            }

            foreach (KeyValuePair<System.Globalization.CultureInfo, string> pair in names)
            {
                // English before anything else: it is the name the font's own 'name' table is most
                // likely to carry and the one GDI was given when the file was installed.
                if (pair.Key.TwoLetterISOLanguageName == "en" && !string.IsNullOrEmpty(pair.Value))
                {
                    return pair.Value;
                }
            }

            foreach (KeyValuePair<System.Globalization.CultureInfo, string> pair in names)
            {
                if (!string.IsNullOrEmpty(pair.Value)) return pair.Value;
            }

            return null;
        }

        /// <summary>
        /// The bitmap as opaque 32-bit BGRX, composited onto white.
        ///
        /// A printer device context has no alpha -- the drivers here report SHADEBLENDCAPS of zero
        /// -- and by this point in the pipeline it should not need any: the flattener has resolved
        /// transparency between primitives already. What is left is edge antialiasing and whatever
        /// the flattener could not resolve, and white is both the correct backdrop for paper and
        /// the same choice the product's own GDI exporter made.
        /// </summary>
        private static byte[] Opaque(BitmapSource source, int width, int height)
        {
            var converted = new FormatConvertedBitmap(source, PixelFormats.Pbgra32, null, 0);

            int stride = width * 4;
            var pixels = new byte[stride * height];

            try
            {
                converted.CopyPixels(pixels, stride, 0);
            }
            catch (ArgumentException)
            {
                return null;
            }
            catch (NotSupportedException)
            {
                return null;
            }

            for (int i = 0; i < pixels.Length; i += 4)
            {
                byte alpha = pixels[i + 3];

                if (alpha == 255)
                {
                    pixels[i + 3] = 0;
                    continue;
                }

                if (alpha == 0)
                {
                    pixels[i] = 255;
                    pixels[i + 1] = 255;
                    pixels[i + 2] = 255;
                    pixels[i + 3] = 0;
                    continue;
                }

                // Pbgra32 is premultiplied, so compositing onto white is the source plus the white
                // that shows through: c + 255 * (1 - a).
                int uncovered = 255 - alpha;
                pixels[i] = (byte)Math.Min(255, pixels[i] + uncovered);
                pixels[i + 1] = (byte)Math.Min(255, pixels[i + 1] + uncovered);
                pixels[i + 2] = (byte)Math.Min(255, pixels[i + 2] + uncovered);
                pixels[i + 3] = 0;
            }

            return pixels;
        }

        private void Identity()
        {
            var identity = new GdiNative.XFORM { eM11 = 1, eM22 = 1 };
            GdiNative.SetWorldTransform(_dc, ref identity);
        }

        private static GdiNative.POINT Device(Matrix matrix, Point point)
        {
            Point transformed = matrix.Transform(point);
            return new GdiNative.POINT(Round(transformed.X), Round(transformed.Y));
        }

        /// <summary>
        /// Device coordinates, clamped.
        ///
        /// GDI's path engine works in 27-bit fixed point and misbehaves quietly past it, and a
        /// transform with a very large scale in it -- which a degenerate layout can produce -- is
        /// exactly how you get there. Clamping puts the point off the page, which is where it
        /// belongs, instead of wrapping it round to the middle of it.
        /// </summary>
        private static int Round(double value)
        {
            if (double.IsNaN(value)) return 0;

            return (int)Math.Round(Math.Clamp(value, -16_000_000.0, 16_000_000.0),
                                   MidpointRounding.AwayFromZero);
        }

        private static bool IsAxisAligned(Matrix matrix)
            => Math.Abs(matrix.M12) < 1e-6 && Math.Abs(matrix.M21) < 1e-6;

        /// <summary>How much a matrix magnifies, taken as the larger of the two axes.</summary>
        private static double MatrixScale(Matrix matrix)
            => Math.Max(Math.Sqrt(matrix.M11 * matrix.M11 + matrix.M12 * matrix.M12),
                        Math.Sqrt(matrix.M21 * matrix.M21 + matrix.M22 * matrix.M22));

        /// <summary>A COLORREF, which is BGR and not RGB.</summary>
        private static int ColorRef(Color color) => color.R | (color.G << 8) | (color.B << 16);

        /// <summary>
        /// A brush reduced to one colour. Only solid colours reach a device: the flattener has
        /// decomposed gradients into clipped solid slices and rasterized everything else.
        /// </summary>
        private static bool TryColor(Brush brush, out Color color)
        {
            color = Colors.Black;

            if (brush is not SolidColorBrush solid) return false;

            color = solid.Color;

            // Alpha is gone by here, and a colour that is still transparent is one the flattener
            // decided was invisible. Painting it opaque would be worse than painting nothing.
            double alpha = solid.Opacity * (solid.Color.A / 255.0);
            return alpha > 0;
        }

        public void Dispose()
        {
            if (_documentStarted) AbortDocument();
            else DeleteDeviceContext();

            foreach (GdiFont font in _fonts.Values) font.Dispose();

            _fonts.Clear();
        }
    }
}
