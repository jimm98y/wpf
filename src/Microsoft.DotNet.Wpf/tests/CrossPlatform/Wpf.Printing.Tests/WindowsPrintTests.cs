// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Printing on Windows, against a real printer and a real spooler.
//
// Every other test in this suite stops at a stand-in backend and asserts on what it was handed.
// These do not: they open a device context on an installed printer, draw into it, and let the
// spooler finish the job. That is the half no fake can cover, and it is the half that was missing
// -- PlatformPrint had no Windows backend at all, so the most common WPF platform was the only one
// where PrintDialog.PrintVisual could not work.
//
// THE PRINTER IS "Microsoft Print to PDF", which ships with Windows and is why this can be checked
// on any machine rather than only one with hardware. It is a genuine printer: a real driver, a real
// queue, a real GDI device context at 600 dpi. Nothing about the path under test knows it is
// special. What makes it usable in a test is that its output is a file we can then read back, and
// that DOCINFO.lpszOutput names that file -- without which the driver, sitting on the PORTPROMPT
// port, would put up a modal Save As box and the test would hang rather than fail.
//
// The assertions go through the same PDF reader the rest of the suite uses. Note whose PDF it is:
// this one was written by the Microsoft driver from the GDI calls we made, NOT by this port's PDF
// writer. Page count, page size and ink on the page are therefore evidence about the print job and
// not about our own file format.
//

using System;
using System.Collections.Generic;
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
    public class WindowsPrintTests : IDisposable
    {
        private const string PdfPrinter = "Microsoft Print to PDF";

        private readonly List<string> _files = new List<string>();

        public WindowsPrintTests()
        {
            // The real one. Other tests in this collection install a stand-in, and the collection
            // is serialized, but a null here is cheap insurance against running after one of them.
            PlatformPrint.Current = null;
        }

        public void Dispose()
        {
            foreach (string file in _files)
            {
                try { File.Delete(file); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        [Fact]
        public void TheWindowsBackendIsTheOneThatDrawsRatherThanSubmits()
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "the Windows print backend is Windows-only");

            // The distinction the whole design turns on. Five print systems take a finished
            // document; this one hands out a device context, so XpsDocumentWriter must not render
            // a PDF and try to submit it.
            Assert.True(PlatformPrint.IsAvailable);
            Assert.False(PlatformPrint.TakesRenderedDocument);
        }

        [Fact]
        public void InstalledPrintersAreDiscovered()
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "the Windows print backend is Windows-only");

            var queues = new List<PrintQueue>(new LocalPrintServer().GetPrintQueues());

            Assert.NotEmpty(queues);
            Assert.Contains(queues, q => q.Name == PdfPrinter);

            // Exactly one default, and it is one of the printers we found. A machine can have no
            // default; it cannot have a default that is not installed.
            PrintQueue standard = new LocalPrintServer().DefaultPrintQueue;
            Assert.NotNull(standard);
            Assert.Contains(queues, q => q.Name == standard.Name);
        }

        [Fact]
        public void TheDriverReportsItsPaperAndItsMargins()
        {
            PrintQueue queue = SkipUnlessPdfPrinter();

            PrinterInfo printer = queue.Printer;

            // US Letter or A4 depending on the machine's locale, so assert the shape rather than
            // the number: a portrait sheet somewhere between 5 and 17 inches.
            Assert.InRange(printer.PageWidth, 480.0, 1632.0);
            Assert.InRange(printer.PageHeight, printer.PageWidth, 1632.0);

            // Print to PDF marks the whole sheet -- it has no hardware margin -- so its imageable
            // area is the paper. What matters is that the numbers are present and consistent.
            Assert.True(printer.ImageableWidth > 0);
            Assert.True(printer.ImageableOriginX + printer.ImageableWidth <= printer.PageWidth + 1);
            Assert.True(printer.ImageableOriginY + printer.ImageableHeight <= printer.PageHeight + 1);
        }

        [Fact]
        public void PrintVisualProducesAPrintedPage()
        {
            PrintQueue queue = SkipUnlessPdfPrinter();

            string output = Redirect(queue);

            var dialog = new PrintDialog { PrintQueue = queue };
            dialog.PrintVisual(Rectangle(Brushes.Red), "a red rectangle");

            PdfDocument pdf = Printed(output);
            Assert.Single(pdf.Pages());
        }

        [Fact]
        public void EveryPageOfAPaginatedDocumentIsPrinted()
        {
            PrintQueue queue = SkipUnlessPdfPrinter();

            string output = Redirect(queue);

            var dialog = new PrintDialog { PrintQueue = queue };
            dialog.PrintDocument(new ThreePages(), "three pages");

            Assert.Equal(3, Printed(output).Pages().Count);
        }

        [Fact]
        public void TheRequestedPaperSizeReachesTheDriver()
        {
            PrintQueue queue = SkipUnlessPdfPrinter();

            string output = Redirect(queue);

            // A5 -- 148 x 210 mm, which is 559.5 x 794 in WPF's 96ths of an inch. It is the default
            // paper of no printer on any machine this runs on, so a page that comes back A5 came
            // back because the DEVMODE said so and not by luck.
            const double A5Width = 559.5;
            const double A5Height = 794.0;

            var ticket = new PrintTicket
            {
                PageMediaSize = new PageMediaSize(A5Width, A5Height),
            };

            var dialog = new PrintDialog { PrintQueue = queue, PrintTicket = ticket };
            dialog.PrintVisual(Rectangle(Brushes.Blue), "A5");

            PdfDocument pdf = Printed(output);
            (double width, double height) = MediaBox(pdf);

            // A PDF is measured in points, which are 72ths of an inch to WPF's 96ths. The driver
            // rounds to whole tenths of a millimetre on the way through DEVMODE, so allow a couple
            // of points either way.
            double expectedWidth = A5Width * 0.75;
            double expectedHeight = A5Height * 0.75;

            Assert.True(Math.Abs(width - expectedWidth) < 3 && Math.Abs(height - expectedHeight) < 3,
                        $"asked the driver for A5 ({expectedWidth:F1} x {expectedHeight:F1} pt) " +
                        $"and it printed {width:F1} x {height:F1} pt");
        }

        [Fact]
        public void TextAndShapesBothReachThePage()
        {
            PrintQueue queue = SkipUnlessPdfPrinter();

            string output = Redirect(queue);

            var visual = new DrawingVisual();
            using (DrawingContext dc = visual.RenderOpen())
            {
                dc.DrawRectangle(Brushes.Orange, new Pen(Brushes.DarkBlue, 3), new Rect(40, 40, 300, 120));
                dc.DrawEllipse(Brushes.SeaGreen, null, new Point(200, 320), 120, 80);
                dc.DrawText(Text("Printed by the WPF port"), new Point(40, 460));
            }

            var dialog = new PrintDialog { PrintQueue = queue };
            dialog.PrintVisual(visual, "shapes and text");

            PdfDocument pdf = Printed(output);
            Assert.Single(pdf.Pages());

            // A page with a filled rectangle, a stroked outline, an ellipse and a line of text is
            // not a small file. An empty page from this driver is a few hundred bytes, so a job
            // that drew nothing -- the failure a page-count assertion cannot see -- shows up here.
            Assert.True(new FileInfo(output).Length > 2000,
                        $"the printed page is only {new FileInfo(output).Length} bytes, which is an empty sheet");

            // Text went down as TEXT.
            //
            // The device draws glyph runs with ExtTextOut rather than filling their outlines, and
            // the difference is invisible to a page count and to a byte count: filled outlines
            // would produce a perfectly good-looking page with no font in it. An embedded font
            // program is the evidence that the driver received characters, which is what makes the
            // printed page searchable and what a fallback to outlines would silently lose.
            string raw = File.ReadAllText(output, System.Text.Encoding.Latin1);

            Assert.Contains("/Type /Font", raw, StringComparison.Ordinal);
            Assert.True(raw.Contains("/FontFile2", StringComparison.Ordinal) ||
                        raw.Contains("/FontFile3", StringComparison.Ordinal) ||
                        raw.Contains("/FontFile", StringComparison.Ordinal),
                        "the printed page has no embedded font, so the text was drawn as paths");
        }

        [Fact]
        public void APrintJobSurvivesAPrinterThatIsNotThere()
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "the Windows print backend is Windows-only");

            // A queue can go away between being enumerated and being printed to -- someone deletes
            // it, or a network connection drops. That has to be an exception the application can
            // catch, not a crash inside a device context.
            var writer = PrintQueue.CreateXpsDocumentWriter(Missing());

            Assert.Throws<PrintSystemException>(() => writer.Write(Rectangle(Brushes.Red)));
        }

        // ---- fixtures --------------------------------------------------------------

        /// <summary>
        /// The Print to PDF queue, or a skipped test.
        ///
        /// Skipped rather than failed when it is absent: it is optional in Windows Features and
        /// turned off in some managed images, and a suite that fails on a machine without it is
        /// reporting the machine, not the code.
        /// </summary>
        private static PrintQueue SkipUnlessPdfPrinter()
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "the Windows print backend is Windows-only");

            PrintQueue queue = null;

            foreach (PrintQueue candidate in new LocalPrintServer().GetPrintQueues())
            {
                if (candidate.Name == PdfPrinter) queue = candidate;
            }

            Assert.SkipWhen(queue == null, $"'{PdfPrinter}' is not installed on this machine");
            return queue;
        }

        /// <summary>Points this queue's next job at a file, and remembers to delete it.</summary>
        private string Redirect(PrintQueue queue)
        {
            string path = Path.Combine(Path.GetTempPath(), "wpf-print-test-" + Guid.NewGuid().ToString("N") + ".pdf");

            _files.Add(path);
            queue.OutputFile = path;

            return path;
        }

        /// <summary>
        /// The file the driver produced, parsed.
        ///
        /// The wait is not superstition. EndDoc hands the job to the spooler and returns; the
        /// driver writes the file on the spooler's thread, so the bytes are not there yet and the
        /// last of them arrive after the file does.
        /// </summary>
        private static PdfDocument Printed(string path)
        {
            byte[] bytes = null;

            for (int attempt = 0; attempt < 300; attempt++)
            {
                try
                {
                    if (File.Exists(path))
                    {
                        bytes = File.ReadAllBytes(path);
                        if (bytes.Length > 0) break;
                    }
                }
                catch (IOException)
                {
                    // Still being written.
                }

                System.Threading.Thread.Sleep(100);
            }

            Assert.True(bytes != null && bytes.Length > 0,
                        "the printer produced no file, so the job never reached the driver");

            return PdfDocument.Parse(bytes);
        }

        private static (double Width, double Height) MediaBox(PdfDocument pdf)
        {
            var box = (List<object>)pdf.Resolve(pdf.Pages()[0]["MediaBox"]);

            double left = Convert.ToDouble(box[0], System.Globalization.CultureInfo.InvariantCulture);
            double bottom = Convert.ToDouble(box[1], System.Globalization.CultureInfo.InvariantCulture);
            double right = Convert.ToDouble(box[2], System.Globalization.CultureInfo.InvariantCulture);
            double top = Convert.ToDouble(box[3], System.Globalization.CultureInfo.InvariantCulture);

            return (right - left, top - bottom);
        }

        /// <summary>A queue naming a printer this machine does not have.</summary>
        private static PrintQueue Missing()
            => new PrintQueue(new PrinterInfo
            {
                Name = "a printer that does not exist " + Guid.NewGuid().ToString("N"),
            });

        private static FormattedText Text(string text)
            => new FormattedText(text, System.Globalization.CultureInfo.InvariantCulture,
                                 FlowDirection.LeftToRight,
                                 new Typeface("Segoe UI"), 32, Brushes.Black, 96);

        private static Visual Rectangle(Brush brush)
        {
            var visual = new DrawingVisual();
            using (DrawingContext dc = visual.RenderOpen())
            {
                dc.DrawRectangle(brush, null, new Rect(60, 60, 300, 200));
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
                    dc.DrawRectangle(Brushes.Black, null, new Rect(60, 60 + pageNumber * 40, 300, 20));
                }

                return new DocumentPage(visual, PageSize, new Rect(PageSize), new Rect(PageSize));
            }
        }
    }
}
