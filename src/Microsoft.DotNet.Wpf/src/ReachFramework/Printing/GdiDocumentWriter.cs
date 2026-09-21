// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Printing a Visual or a paginated document on Windows.
//
// The same assembly of parts as PdfDocumentWriter, pointed at a printer instead of a stream:
//
//     Visual / DocumentPaginator
//       -> VisualTreeFlattener.Walk        (walk the tree, resolve properties)
//          -> DrawingContextFlattener      (animations, VisualBrush, rasterize what cannot be drawn)
//             -> MetroDevice0              (build a retained primitive tree)
//                -> Flattener.Convert      (flatten transparency, decompose gradients)
//                   -> GdiDevice           (draw the page into the printer's device context)
//
// Only the last stage differs, which is the whole point of ILegacyDevice existing.
//
// The one real difference in how the pipeline is DRIVEN is quality. Writing PDF, the flattener is
// left at its defaults because the output is vector at any size; printing, whatever it has to
// rasterize lands on paper at the printer's resolution, so it is asked for High -- 300 dpi rather
// than the 96 a screen would get. That number is the difference between a soft-edged rectangle
// where a drop shadow was and one that looks like it was printed.
//

using System;
using System.Printing;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using Microsoft.Internal.AlphaFlattener;
using System.Windows.Xps.Serialization;

namespace System.Windows.Xps.Printing
{
    /// <summary>Sends WPF content to a Windows printer as a print job.</summary>
    [SupportedOSPlatform("windows")]
    internal sealed class GdiDocumentWriter : IDisposable
    {
        private readonly GdiDevice _device = new GdiDevice();
        private readonly string _printerName;
        private readonly ILegacyDevice _sink;

        private bool _documentStarted;
        private bool _closed;

        internal GdiDocumentWriter(string printerName)
        {
            ArgumentException.ThrowIfNullOrEmpty(printerName);

            _printerName = printerName;
            _sink = _device;
        }

        /// <summary>The page size in WPF units. Zero leaves the printer's own paper alone.</summary>
        internal Size PageSize { get; set; }

        /// <summary>What the queue shows this job as.</summary>
        internal string JobName { get; set; }

        /// <summary>A file to write to instead of the printer's port. See PrintJobSettings.OutputFile.</summary>
        internal string OutputFile { get; set; }

        /// <summary>How many times the spooler should print the document.</summary>
        internal int Copies { get; set; } = 1;

        /// <summary>The paper the driver settled on, once the job has started.</summary>
        internal Size PaperSize => _device.PaperSize;

        /// <summary>Writes one Visual as one page.</summary>
        internal void Write(Visual visual)
        {
            ArgumentNullException.ThrowIfNull(visual);

            EnsureDocument();
            WritePage(visual, PageSize);
        }

        /// <summary>
        /// Writes a paginated document, one page per DocumentPage.
        ///
        /// The page count is settled first. A DocumentPaginator may answer "not yet known" and
        /// compute pagination in the background, which a FlowDocument does; a print has to wait it
        /// out rather than stop at the pages that happen to be ready.
        /// </summary>
        internal void Write(DocumentPaginator paginator)
        {
            ArgumentNullException.ThrowIfNull(paginator);

            EnsureDocument();

            if (!paginator.IsPageCountValid) paginator.ComputePageCount();

            for (int i = 0; i < paginator.PageCount; i++)
            {
                DocumentPage page = paginator.GetPage(i);
                if (page == null || page == DocumentPage.Missing) continue;

                try
                {
                    Size size = page.Size;
                    if (size.IsEmpty || size.Width <= 0 || size.Height <= 0) size = PageSize;

                    WritePage(page.Visual, size);
                }
                finally
                {
                    // A DocumentPage owns a live visual tree; a long document that never released
                    // them would hold the whole thing in memory at once.
                    (page as IDisposable)?.Dispose();
                }
            }
        }

        /// <summary>Finishes the job and hands it to the spooler.</summary>
        internal void Close()
        {
            if (_closed) return;
            _closed = true;

            if (_documentStarted) _sink.EndDocument();

            // Always, even after EndDocument: the device caches an HFONT per face and size for the
            // life of a job, and those are GDI objects with a per-process limit.
            _device.Dispose();
        }

        /// <summary>Abandons the job. Nothing is printed and nothing is queued.</summary>
        internal void Abort()
        {
            if (_closed) return;
            _closed = true;

            _device.AbortDocument();
        }

        public void Dispose() => Close();

        private void EnsureDocument()
        {
            ObjectDisposedException.ThrowIf(_closed, this);

            if (_documentStarted) return;

            _documentStarted = true;
            _device.OutputFile = OutputFile;

            byte[] devMode = GdiNative.BuildDevMode(_printerName, PageSize.Width, PageSize.Height, Copies);

            _sink.StartDocument(_printerName, JobName ?? "WPF document", OutputFile, devMode);

            // What the driver actually gave us, which is not always what was asked for: a printer
            // with no custom paper support answers a request for 5x7 with its nearest standard
            // size. Rendering onto the size that was asked for would then run off the sheet.
            if (PageSize.IsEmpty || PageSize.Width <= 0 || PageSize.Height <= 0)
            {
                PageSize = _device.PaperSize;
            }
        }

        private void WritePage(Visual visual, Size size)
        {
            if (visual == null) return;

            if (size.IsEmpty || size.Width <= 0 || size.Height <= 0) size = _device.PaperSize;

            var primitives = new MetroDevice0();
            primitives.StartDocument();
            primitives.StartPage();

            _sink.StartPage(null, _device.DpiX);

            try
            {
                VisualTreeFlattener.Walk(visual, new GdiDrawingContext(primitives), size,
                                         new TreeWalkProgress());

                // High, not the default. Anything the flattener has to rasterize -- an opacity
                // mask, an effect, a 3-D scene -- is rasterized at this resolution and then printed
                // at the device's, so leaving it at a screen's 96 dpi puts visibly soft patches on
                // an otherwise sharp page. High is 300, which is a reasonable floor for paper
                // without making a full-page rasterization enormous.
                primitives.FlushPage(_sink, size.Width, size.Height, OutputQuality.High);
            }
            finally
            {
                _sink.EndPage();
                primitives.EndDocument();
            }
        }
    }

    /// <summary>
    /// Adapts the visual-tree walker's output onto the primitive builder.
    ///
    /// A straight forwarder, and identical to PdfDrawingContext. Both exist because MetroDevice0
    /// does not implement the interface itself, and the class that adapts it upstream
    /// (MetroToGdiConverter) reaches through a PrintQueue for DEVMODE and printer capabilities that
    /// this path has already resolved.
    /// </summary>
    internal sealed class GdiDrawingContext : IMetroDrawingContext
    {
        private readonly MetroDevice0 _primitives;

        internal GdiDrawingContext(MetroDevice0 primitives)
        {
            _primitives = primitives;
        }

        void IMetroDrawingContext.DrawGeometry(Brush brush, Pen pen, Geometry geometry)
            => _primitives.DrawGeometry(brush, pen, geometry);

        void IMetroDrawingContext.DrawImage(ImageSource image, Rect rectangle)
            => _primitives.DrawImage(image, rectangle);

        void IMetroDrawingContext.DrawGlyphRun(Brush foreground, GlyphRun glyphRun)
            => _primitives.DrawGlyphRun(foreground, glyphRun);

        void IMetroDrawingContext.Push(Matrix transform, Geometry clip, double opacity, Brush opacityMask,
                                       Rect maskBounds, bool onePrimitive, string nameAttr, Visual node,
                                       Uri navigateUri, EdgeMode edgeMode)
            => _primitives.Push(transform, clip, opacity, opacityMask, maskBounds, onePrimitive);

        void IMetroDrawingContext.Pop() => _primitives.Pop();

        void IMetroDrawingContext.Comment(string message)
        {
        }
    }
}
