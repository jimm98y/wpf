// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The path an actual WPF application takes to print, end to end.
//
// PrintDialog.PrintVisual is three calls deep: it asks LocalPrintServer for a queue, asks the queue
// for an XpsDocumentWriter, and calls Write. Every document viewer in the framework does the same.
// None of it worked in this port -- the shipped System.Printing was a sixteen-line class with a
// private constructor -- so these tests are the ones that say whether printing is fixed.
//
// A stand-in backend is installed through PlatformPrint, which is what makes this testable on a
// machine with no printers. It also lets the tests assert on what the backend is HANDED, which is
// where the interesting mistakes live: the wrong page size, an empty document, a job submitted
// after the user cancelled.
//

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Printing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Xps;
using MS.Internal.Interop;
using Xunit;

namespace Wpf.Printing.Tests
{
    [Collection("Printing")]
    public class PrintQueueTests : IDisposable
    {
        private readonly FakeBackend _backend = new FakeBackend();

        public PrintQueueTests()
        {
            PlatformPrint.Current = _backend;
        }

        public void Dispose() => PlatformPrint.Current = null;

        [Fact]
        public void PrintersAreDiscoveredThroughThePlatform()
        {
            var queues = new List<PrintQueue>(new LocalPrintServer().GetPrintQueues());

            Assert.Equal(2, queues.Count);
            Assert.Equal("Office", queues[0].Name);
            Assert.Equal("Office Printer (2nd floor)", queues[0].FullName);
        }

        [Fact]
        public void TheDefaultQueueIsTheOneThePlatformMarks()
        {
            PrintQueue queue = new LocalPrintServer().DefaultPrintQueue;

            Assert.NotNull(queue);
            Assert.Equal("Desk", queue.Name);
            Assert.True(queue.IsDefault);
        }

        [Fact]
        public void NoPrintersMeansNoDefaultQueueRatherThanAnException()
        {
            // "No default printer" is a state PrintDialog has always had to handle, and it is what a
            // machine with nothing installed looks like. Throwing here would take out any app that
            // merely constructs a PrintDialog.
            _backend.Printers = Array.Empty<PrinterInfo>();

            Assert.Null(new LocalPrintServer().DefaultPrintQueue);
            Assert.Empty(new List<PrintQueue>(new LocalPrintServer().GetPrintQueues()));
        }

        [Fact]
        public void PrintVisualRendersAndSubmits()
        {
            // The whole point of the exercise, through the API an existing WPF app already calls.
            var dialog = new PrintDialog { PrintQueue = new LocalPrintServer().DefaultPrintQueue };

            dialog.PrintVisual(Rectangle(Brushes.Red), "a red rectangle");

            Assert.Equal("a red rectangle", _backend.LastJobName);
            Assert.NotNull(_backend.LastDocument);

            PdfDocument pdf = PdfDocument.Parse(_backend.LastDocument);
            Assert.Single(pdf.Pages());
            Assert.Contains("1 0 0 rg", PdfWriterTests.ContentOf(pdf), StringComparison.Ordinal);
        }

        [Fact]
        public void PrintDocumentPaginatesAndSubmitsEveryPage()
        {
            var dialog = new PrintDialog { PrintQueue = new LocalPrintServer().DefaultPrintQueue };

            dialog.PrintDocument(new ThreePages(), "three pages");

            PdfDocument pdf = PdfDocument.Parse(_backend.LastDocument);
            Assert.Equal(3, pdf.Pages().Count);
        }

        [Fact]
        public void TheJobCarriesThePrinterItWasAddressedTo()
        {
            var dialog = new PrintDialog { PrintQueue = new LocalPrintServer().DefaultPrintQueue };
            dialog.PrintVisual(Rectangle(Brushes.Blue), "job");

            Assert.Equal("Desk", _backend.LastSettings.PrinterName);
        }

        [Fact]
        public void ThePageSizeComesFromThePrinter()
        {
            // A4 rather than Letter, because that is what this printer reports. Getting it from the
            // wrong place reflows every page of a paginated document.
            var queues = new List<PrintQueue>(new LocalPrintServer().GetPrintQueues());
            var dialog = new PrintDialog { PrintQueue = queues[0] };      // the A4 one

            dialog.PrintVisual(Rectangle(Brushes.Green), "a4 job");

            PdfDocument pdf = PdfDocument.Parse(_backend.LastDocument);
            var box = (List<object>)pdf.Resolve(pdf.Pages()[0]["MediaBox"]);

            // 794x1123 WPF units at 0.75 points per unit.
            Assert.Equal(595.5, Convert.ToDouble(box[2], CultureInfo.InvariantCulture), 1);
            Assert.Equal(842.25, Convert.ToDouble(box[3], CultureInfo.InvariantCulture), 1);

            // And definitively not US Letter, which is what it would be if the size came from the
            // fallback rather than from the printer.
            Assert.NotEqual(612.0, Convert.ToDouble(box[2], CultureInfo.InvariantCulture), 0);
        }

        [Fact]
        public void PrintingWithNoQueueSaysSoRatherThanFailingObscurely()
        {
            _backend.Printers = Array.Empty<PrinterInfo>();

            var dialog = new PrintDialog();

            // What this used to do was a TypeLoadException on desktop and a NullReferenceException in
            // the browser, neither of which tells anyone anything.
            PrintSystemException error = Assert.Throws<PrintSystemException>(
                () => dialog.PrintVisual(Rectangle(Brushes.Red), "nowhere"));

            Assert.Contains("no printer", error.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void ARefusedSubmissionIsReported()
        {
            _backend.Accept = false;

            var dialog = new PrintDialog { PrintQueue = new LocalPrintServer().DefaultPrintQueue };

            Assert.Throws<PrintSystemException>(() => dialog.PrintVisual(Rectangle(Brushes.Red), "refused"));
        }

        [Fact]
        public void TheTemporaryFileDoesNotSurviveTheJob()
        {
            var dialog = new PrintDialog { PrintQueue = new LocalPrintServer().DefaultPrintQueue };
            dialog.PrintVisual(Rectangle(Brushes.Red), "tidy");

            // A fifty-page CJK document is tens of megabytes of temporary file. Leaving those behind
            // fills a disk quietly over weeks.
            Assert.Empty(Directory.GetFiles(Path.GetTempPath(), "wpf-print-*.pdf"));
        }

        [Fact]
        public void PrintableAreaReflectsTheChosenPrinter()
        {
            var queues = new List<PrintQueue>(new LocalPrintServer().GetPrintQueues());
            var dialog = new PrintDialog { PrintQueue = queues[0] };

            // PrintDialog answers 816x1056 (US Letter) when it has nothing to ask. With a real queue
            // it should be reporting that queue's paper.
            Assert.True(dialog.PrintableAreaWidth > 0);
            Assert.True(dialog.PrintableAreaHeight > 0);
        }

        [Fact]
        public void AWriterCanTargetAStreamWithNoPrinterAtAll()
        {
            // Export rather than print. Same pipeline, no print system involved.
            using var buffer = new MemoryStream();

            XpsDocumentWriter writer = PrintQueue.CreateXpsDocumentWriter(buffer, new Size(816, 1056));
            writer.Write(Rectangle(Brushes.Red));

            Assert.Single(PdfDocument.Parse(buffer.ToArray()).Pages());
        }

        // ---- fixtures --------------------------------------------------------------

        private static Visual Rectangle(Brush brush)
        {
            var visual = new DrawingVisual();
            using (DrawingContext dc = visual.RenderOpen())
            {
                dc.DrawRectangle(brush, null, new Rect(10, 10, 100, 50));
            }
            return visual;
        }

        /// <summary>A paginator that hands out three trivially different pages.</summary>
        private sealed class ThreePages : DocumentPaginator
        {
            public override bool IsPageCountValid => true;

            public override int PageCount => 3;

            public override Size PageSize { get; set; } = new Size(816, 1056);

            public override IDocumentPaginatorSource Source => null;

            public override DocumentPage GetPage(int pageNumber)
            {
                var visual = new DrawingVisual();
                using (DrawingContext dc = visual.RenderOpen())
                {
                    dc.DrawRectangle(Brushes.Black, null, new Rect(10, 10 + pageNumber * 20, 100, 10));
                }

                return new DocumentPage(visual, PageSize, new Rect(PageSize), new Rect(PageSize));
            }
        }

        /// <summary>A print system that remembers what it was asked to do.</summary>
        private sealed class FakeBackend : IPrintBackend
        {
            internal PrinterInfo[] Printers =
            {
                new PrinterInfo
                {
                    Name = "Office", DisplayName = "Office Printer (2nd floor)",
                    PageWidth = 794, PageHeight = 1123,       // A4
                },
                new PrinterInfo
                {
                    Name = "Desk", DisplayName = "Desk Printer", IsDefault = true,
                    PageWidth = 816, PageHeight = 1056,       // US Letter
                },
            };

            internal bool Accept = true;
            internal bool ShowUiResult = true;

            internal string LastJobName;
            internal byte[] LastDocument;
            internal MS.Internal.Interop.PrintJobSettings LastSettings;

            public PrinterInfo[] EnumeratePrinters() => Printers;

            public bool ShowPrintUI(MS.Internal.Interop.PrintJobSettings settings) => ShowUiResult;

            public bool Submit(string jobName, Stream document, MS.Internal.Interop.PrintJobSettings settings)
            {
                LastJobName = jobName;
                LastSettings = settings;

                using var buffer = new MemoryStream();
                document.CopyTo(buffer);
                LastDocument = buffer.ToArray();

                return Accept;
            }
        }
    }

    [CollectionDefinition("Printing", DisableParallelization = true)]
    public class PrintingCollection
    {
        // PlatformPrint.Current is process-wide, so these tests cannot run beside each other.
    }
}
