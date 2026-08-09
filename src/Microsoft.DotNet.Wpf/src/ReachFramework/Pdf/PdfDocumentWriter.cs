// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Writing a Visual or a paginated document to PDF.
//
// This is the whole pipeline assembled: WPF's visual-tree walker, its drawing normalizer, its
// primitive and alpha flatteners, and a PDF device at the bottom. Every stage above the device
// already existed and already ran on every platform -- it was written for XPS and for GDI printing
// and has been sitting in ReachFramework compiled and unreachable, because the only thing that ever
// implemented its output interface was a C++/CLI class this port does not build.
//
//     Visual / DocumentPaginator
//       -> VisualTreeFlattener.Walk        (walk the tree, resolve properties)
//          -> DrawingContextFlattener      (animations, VisualBrush, rasterize what cannot be drawn)
//             -> MetroDevice0              (build a retained primitive tree)
//                -> Flattener.Convert      (flatten transparency, decompose gradients)
//                   -> PdfDevice           (write the page)
//
// The one piece that had to be written here is the adapter in the middle: MetroDevice0 is not
// itself an IMetroDrawingContext, and the class that adapts it upstream (MetroToGdiConverter) is
// bound to a PrintQueue for DEVMODE and printer capabilities. PdfDrawingContext is that adapter
// without the printer.
//

using System;
using System.IO;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using Microsoft.Internal.AlphaFlattener;
using System.Windows.Xps.Serialization;
using System.Printing;

namespace System.Windows.Xps.Pdf
{
    /// <summary>
    /// Writes WPF content to a PDF stream. One instance writes one document; dispose it (or call
    /// <see cref="Close"/>) to finish the file, which is when the cross-reference table is written.
    /// </summary>
    internal sealed class PdfDocumentWriter : IDisposable
    {
        private readonly PdfDevice _device;
        private readonly ILegacyDevice _sink;
        private bool _documentStarted;
        private bool _closed;

        /// <summary>
        /// The page size in WPF units (96ths of an inch) used for a Visual that does not carry one.
        /// Defaults to US Letter.
        /// </summary>
        internal Size PageSize
        {
            get => _device.PageSize;
            set => _device.PageSize = value;
        }

        internal PdfDocumentWriter(Stream destination, bool leaveOpen = false)
        {
            ArgumentNullException.ThrowIfNull(destination);

            _device = new PdfDevice(destination, leaveOpen);
            _sink = _device;
        }

        /// <summary>
        /// The device underneath, for tests that need to exercise drawing paths the flattener
        /// upstream does not currently produce: clipping paths, soft masks, residual alpha. Typed as
        /// PdfDevice rather than ILegacyDevice because the interface belongs to System.Printing and
        /// is not visible outside ReachFramework.
        /// </summary>
        internal PdfDevice Device => _device;

        /// <summary>Writes one Visual as one page.</summary>
        internal void Write(Visual visual)
        {
            ArgumentNullException.ThrowIfNull(visual);

            EnsureDocument();
            WritePage(visual, PageSize);
        }

        /// <summary>
        /// Writes a paginated document, one page per <see cref="DocumentPage"/>.
        ///
        /// The paginator is asked for its page count first. A DocumentPaginator is allowed to answer
        /// "not yet known" and compute pagination in the background, which for a FlowDocument it does;
        /// printing has to wait it out rather than stop at the pages that happen to be ready.
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

        /// <summary>Finishes the document. Further writes are ignored.</summary>
        internal void Close()
        {
            if (_closed) return;
            _closed = true;

            if (_documentStarted) _sink.EndDocument();
            else _device.Dispose();
        }

        public void Dispose() => Close();

        // ---- the pipeline ----------------------------------------------------------

        private void EnsureDocument()
        {
            if (_closed) throw new ObjectDisposedException(nameof(PdfDocumentWriter));
            if (_documentStarted) return;

            _documentStarted = true;
            _sink.StartDocument(null, "WPF", null, null);
        }

        private void WritePage(Visual visual, Size size)
        {
            if (visual == null) return;

            _device.PageSize = size;

            var primitives = new MetroDevice0();
            primitives.StartDocument();
            primitives.StartPage();

            _sink.StartPage(null, 96);

            try
            {
                VisualTreeFlattener.Walk(visual, new PdfDrawingContext(primitives), size,
                                         new TreeWalkProgress());

                // Quality left unset on purpose. Asking for Photographic sends Flattener down a path
                // that queries the device's DPI through PrintQueue, which does not exist here -- and
                // the page is being written at full resolution regardless, since it is vector.
                primitives.FlushPage(_sink, size.Width, size.Height, null);
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
    /// A straight forwarder, and it exists only because MetroDevice0 does not implement the interface
    /// itself and the class that adapts it upstream drags in a PrintQueue for DEVMODE conversion and
    /// printer capabilities.
    /// </summary>
    internal sealed class PdfDrawingContext : IMetroDrawingContext
    {
        private readonly MetroDevice0 _primitives;

        internal PdfDrawingContext(MetroDevice0 primitives)
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
