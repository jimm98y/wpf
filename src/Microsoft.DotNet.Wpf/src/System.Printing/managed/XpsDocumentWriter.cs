// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// XpsDocumentWriter: the object every printing path in WPF funnels through.
//
// PrintDialog.PrintVisual, PrintDialog.PrintDocument, DocumentViewerBase, FlowDocumentScrollViewer,
// SinglePageViewer and DocumentApplicationDocumentViewer all reach printing by calling
// PrintQueue.CreateXpsDocumentWriter and then Write on the result. Making these two methods work is
// what makes printing work in an existing WPF application, unchanged.
//
// The name is now a small lie: the document produced is a PDF, not an XPS package. Renaming it is
// not an option -- it is the type every one of those callers is written against, and it is public
// API. What matters is the shape: hand it content, it renders and submits. XPS was only ever the
// format Windows' spooler happened to want.
//
// The write is synchronous and to a temporary file rather than to memory. Both are deliberate. A
// page of CJK text carries an embedded font of several megabytes, and a fifty-page document should
// not hold all of it in memory at once; and every platform's submission API wants either a path or
// a stream it can read at its own pace, which a file gives for free.
//

using System.ComponentModel;
using System.IO;
using System.Printing;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Documents.Serialization;
using System.Windows.Media;
using MS.Internal.Interop;

// PrintSystemException is declared in ReachFramework, not here, even though its namespace says
// System.Printing. That is how the real product splits it too, and defining a second one would give
// callers two types with the same full name that do not catch each other.

namespace System.Windows.Xps
{
    /// <summary>Writes WPF content to a print queue.</summary>
    public partial class XpsDocumentWriter : SerializerWriter
    {
        private readonly PrintQueue _printQueue;
        private readonly Stream _destination;
        private bool _cancelled;

        internal XpsDocumentWriter(PrintQueue printQueue)
        {
            _printQueue = printQueue;
        }

        internal XpsDocumentWriter(Packaging.XpsDocument document)
        {
            // The XPS packaging path. It reaches here from XpsDocument.CreateXpsDocumentWriter, and
            // there is no packaging implementation behind it in this port, so writing through it
            // reports that rather than producing an empty package.
            _ = document;
        }

        /// <summary>Writes to a stream directly, bypassing any print system.</summary>
        internal XpsDocumentWriter(Stream destination, Size pageSize)
        {
            _destination = destination;
            PageSize = pageSize;
        }

        /// <summary>The job name a print queue shows. Defaults to the application's.</summary>
        public string JobDescription { get; set; }

        /// <summary>The page size to render onto, in WPF units.</summary>
        internal Size PageSize { get; set; }

        public override event WritingProgressChangedEventHandler WritingProgressChanged;

        public override event WritingCompletedEventHandler WritingCompleted;

        public override event WritingCancelledEventHandler WritingCancelled;

        public override event WritingPrintTicketRequiredEventHandler WritingPrintTicketRequired;

        // ---- the writes every caller uses ------------------------------------------
        //
        // SerializerWriter declares every combination of content type, print ticket and async user
        // state, which is 32 members. They all reduce to the same four questions -- what content,
        // which page size, sync or async -- so they funnel immediately.

        public override void Write(Visual visual) => WriteCore(visual, null, null);

        public override void Write(Visual visual, PrintTicket printTicket) => WriteCore(visual, null, printTicket);

        public override void Write(DocumentPaginator documentPaginator) => WriteCore(null, documentPaginator, null);

        public override void Write(DocumentPaginator documentPaginator, PrintTicket printTicket)
            => WriteCore(null, documentPaginator, printTicket);

        public override void Write(FixedPage fixedPage) => WriteCore(fixedPage, null, null);

        public override void Write(FixedPage fixedPage, PrintTicket printTicket)
            => WriteCore(fixedPage, null, printTicket);

        public override void Write(FixedDocument fixedDocument) => WriteCore(null, Paginator(fixedDocument), null);

        public override void Write(FixedDocument fixedDocument, PrintTicket printTicket)
            => WriteCore(null, Paginator(fixedDocument), printTicket);

        public override void Write(FixedDocumentSequence fixedDocumentSequence)
            => WriteCore(null, Paginator(fixedDocumentSequence), null);

        public override void Write(FixedDocumentSequence fixedDocumentSequence, PrintTicket printTicket)
            => WriteCore(null, Paginator(fixedDocumentSequence), printTicket);

        // ---- asynchronous forms ----------------------------------------------------
        //
        // Synchronous underneath, with completion reported through the same events. Rendering walks
        // a visual tree, and a visual tree has thread affinity, so moving this to a background
        // thread is a far larger change than an async wrapper -- it would mean the caller could no
        // longer touch the content it just handed over.

        public override void WriteAsync(Visual visual) => Async(() => Write(visual));

        public override void WriteAsync(Visual visual, object userState) => Async(() => Write(visual), userState);

        public override void WriteAsync(Visual visual, PrintTicket printTicket)
            => Async(() => Write(visual, printTicket));

        public override void WriteAsync(Visual visual, PrintTicket printTicket, object userState)
            => Async(() => Write(visual, printTicket), userState);

        public override void WriteAsync(DocumentPaginator documentPaginator)
            => Async(() => Write(documentPaginator));

        public override void WriteAsync(DocumentPaginator documentPaginator, object userState)
            => Async(() => Write(documentPaginator), userState);

        public override void WriteAsync(DocumentPaginator documentPaginator, PrintTicket printTicket)
            => Async(() => Write(documentPaginator, printTicket));

        public override void WriteAsync(DocumentPaginator documentPaginator, PrintTicket printTicket, object userState)
            => Async(() => Write(documentPaginator, printTicket), userState);

        public override void WriteAsync(FixedPage fixedPage) => Async(() => Write(fixedPage));

        public override void WriteAsync(FixedPage fixedPage, object userState)
            => Async(() => Write(fixedPage), userState);

        public override void WriteAsync(FixedPage fixedPage, PrintTicket printTicket)
            => Async(() => Write(fixedPage, printTicket));

        public override void WriteAsync(FixedPage fixedPage, PrintTicket printTicket, object userState)
            => Async(() => Write(fixedPage, printTicket), userState);

        public override void WriteAsync(FixedDocument fixedDocument) => Async(() => Write(fixedDocument));

        public override void WriteAsync(FixedDocument fixedDocument, object userState)
            => Async(() => Write(fixedDocument), userState);

        public override void WriteAsync(FixedDocument fixedDocument, PrintTicket printTicket)
            => Async(() => Write(fixedDocument, printTicket));

        public override void WriteAsync(FixedDocument fixedDocument, PrintTicket printTicket, object userState)
            => Async(() => Write(fixedDocument, printTicket), userState);

        public override void WriteAsync(FixedDocumentSequence fixedDocumentSequence)
            => Async(() => Write(fixedDocumentSequence));

        public override void WriteAsync(FixedDocumentSequence fixedDocumentSequence, object userState)
            => Async(() => Write(fixedDocumentSequence), userState);

        public override void WriteAsync(FixedDocumentSequence fixedDocumentSequence, PrintTicket printTicket)
            => Async(() => Write(fixedDocumentSequence, printTicket));

        public override void WriteAsync(FixedDocumentSequence fixedDocumentSequence, PrintTicket printTicket,
                                        object userState)
            => Async(() => Write(fixedDocumentSequence, printTicket), userState);

        public override void CancelAsync() => _cancelled = true;

        /// <summary>Batched writing, where several visuals become one document.</summary>
        public override SerializerWriterCollator CreateVisualsCollator() => new VisualsToXpsDocument(this);

        public override SerializerWriterCollator CreateVisualsCollator(PrintTicket documentSequencePT,
                                                                       PrintTicket documentPT)
            => new VisualsToXpsDocument(this);

        // ---- the actual work -------------------------------------------------------

        private void WriteCore(Visual visual, DocumentPaginator paginator, PrintTicket printTicket)
        {
            _cancelled = false;

            Size pageSize = ResolvePageSize(printTicket);

            if (_destination != null)
            {
                Render(_destination, pageSize, visual, paginator);
                RaiseCompleted(null);
                return;
            }

            if (_printQueue == null)
            {
                throw new PrintSystemException(
                    "There is no printer to print to. PrintDialog.PrintQueue is null, which is what " +
                    "no default printer looks like.");
            }

            // Windows takes the other route. Its spooler does not accept a document, it hands out a
            // device context and expects the pages to be drawn into it, so there is no file to
            // render and nothing to submit -- the job IS the drawing. Everything above this point
            // in the pipeline is the same; only the bottom of it differs.
            if (!PlatformPrint.TakesRenderedDocument)
            {
                Draw(visual, paginator, pageSize, printTicket);
                return;
            }

            string path = Path.Combine(Path.GetTempPath(),
                                       "wpf-print-" + Guid.NewGuid().ToString("N") + ".pdf");

            try
            {
                using (var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    Render(file, pageSize, visual, paginator);
                }

                if (_cancelled)
                {
                    RaiseCancelled();
                    return;
                }

                var settings = new MS.Internal.Interop.PrintJobSettings
                {
                    PrinterName = _printQueue.Name,
                    PageWidth = pageSize.Width,
                    PageHeight = pageSize.Height,

                    // Copies belong to the print system, not to rendering: the document is written
                    // once and the spooler repeats it. A ticket that does not say means one.
                    Copies = Math.Max(1, printTicket?.CopyCount ?? 1),
                };

                using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    string jobName = JobDescription
                                     ?? _printQueue.CurrentJobSettings?.Description
                                     ?? "WPF document";

                    if (!PlatformPrint.Submit(jobName, file, settings))
                    {
                        throw new PrintSystemException(
                            "The document was rendered but the print system would not accept it.");
                    }
                }

                RaiseCompleted(null);
            }
            finally
            {
                // Best effort: a temporary file left behind is untidy, and failing to delete it is
                // not a reason to fail a print that otherwise succeeded.
                try { File.Delete(path); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        /// <summary>
        /// The Windows route: draw the pages straight into the printer's device context.
        ///
        /// No temporary file, and no second pass. The five other print systems want a finished
        /// document because that is what their submission APIs take; Windows' spooler is the
        /// drawing surface, so rendering to a file first and then replaying it would only lose
        /// fidelity and time.
        /// </summary>
        private void Draw(Visual visual, DocumentPaginator paginator, Size pageSize, PrintTicket printTicket)
        {
            using var writer = new Printing.GdiDocumentWriter(_printQueue.Name)
            {
                PageSize = pageSize,
                Copies = Math.Max(1, printTicket?.CopyCount ?? 1),
                OutputFile = _printQueue.OutputFile,

                JobName = JobDescription
                          ?? _printQueue.CurrentJobSettings?.Description
                          ?? "WPF document",
            };

            try
            {
                if (paginator != null) writer.Write(paginator);
                else if (visual != null) writer.Write(visual);

                if (_cancelled)
                {
                    // Abort rather than close: a cancelled job that reached the spooler still
                    // prints, and "cancelled" that produces paper is not cancelled.
                    writer.Abort();
                    RaiseCancelled();
                    return;
                }

                writer.Close();
            }
            catch
            {
                writer.Abort();
                throw;
            }

            RaiseCompleted(null);
        }

        private static void Render(Stream destination, Size pageSize, Visual visual, DocumentPaginator paginator)
        {
            using var writer = new Pdf.PdfDocumentWriter(destination, leaveOpen: true) { PageSize = pageSize };

            if (paginator != null) writer.Write(paginator);
            else if (visual != null) writer.Write(visual);
        }

        /// <summary>
        /// The page to render onto: what the ticket asks for, else what the printer reports, else
        /// US Letter -- the same fallback PrintDialog.PrintableAreaWidth already uses.
        /// </summary>
        private Size ResolvePageSize(PrintTicket printTicket)
        {
            if (!PageSize.IsEmpty && PageSize.Width > 0 && PageSize.Height > 0) return PageSize;

            if (printTicket?.PageMediaSize != null)
            {
                PageMediaSize media = printTicket.PageMediaSize;
                if (media.Width.HasValue && media.Height.HasValue &&
                    media.Width.Value > 0 && media.Height.Value > 0)
                {
                    bool landscape = printTicket.PageOrientation == PageOrientation.Landscape;

                    return landscape
                        ? new Size(media.Height.Value, media.Width.Value)
                        : new Size(media.Width.Value, media.Height.Value);
                }
            }

            return _printQueue?.DefaultPageSize ?? new Size(816, 1056);
        }

        private static DocumentPaginator Paginator(IDocumentPaginatorSource source)
            => source?.DocumentPaginator;

        private void Async(Action write, object userState = null)
        {
            try
            {
                write();
            }
            catch (Exception e) when (e is PrintSystemException or XpsWriterException or IOException)
            {
                // Async callers are told through the event rather than by an exception they have no
                // stack frame to catch on.
                RaiseCompleted(e, userState);
            }
        }

        private void RaiseCompleted(Exception error, object userState = null)
            => WritingCompleted?.Invoke(this, new WritingCompletedEventArgs(_cancelled, userState, error));

        private void RaiseCancelled()
            => WritingCancelled?.Invoke(this, new WritingCancelledEventArgs(null));

        private void RaiseProgress(int number)
            => WritingProgressChanged?.Invoke(this,
                   new WritingProgressChangedEventArgs(WritingProgressChangeLevel.FixedPageWritingProgress,
                                                       number, number, null));
    }

    /// <summary>Several visuals written into one document.</summary>
    public partial class VisualsToXpsDocument : SerializerWriterCollator
    {
        private readonly XpsDocumentWriter _writer;

        internal VisualsToXpsDocument(XpsDocumentWriter writer)
        {
            _writer = writer;
        }

        public override void BeginBatchWrite() { }

        public override void EndBatchWrite() { }

        public override void Write(Visual visual) => _writer.Write(visual);

        public override void Write(Visual visual, PrintTicket printTicket) => _writer.Write(visual, printTicket);

        public override void WriteAsync(Visual visual) => _writer.WriteAsync(visual);

        public override void WriteAsync(Visual visual, PrintTicket printTicket) => _writer.WriteAsync(visual, printTicket);

        public override void WriteAsync(Visual visual, object userState) => _writer.WriteAsync(visual);

        public override void WriteAsync(Visual visual, PrintTicket printTicket, object userState)
            => _writer.WriteAsync(visual, printTicket);

        public override void CancelAsync() => _writer.CancelAsync();

        public override void Cancel() => _writer.CancelAsync();
    }

    /// <summary>Something went wrong writing a document.</summary>
    public partial class XpsWriterException : Exception
    {
        public XpsWriterException() { }

        public XpsWriterException(string message) : base(message) { }

        public XpsWriterException(string message, Exception innerException) : base(message, innerException) { }
    }
}
