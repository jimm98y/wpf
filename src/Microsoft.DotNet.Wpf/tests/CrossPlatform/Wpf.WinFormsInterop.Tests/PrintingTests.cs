// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// WinForms printing without GDI+: a PrintDocument's pages are recorded through the managed Graphics
// and then either written as a vector PDF (every head but Windows) or replayed as GDI on the printer
// DC (Windows). These drive the PDF half directly -- it runs anywhere -- and read the file back, the
// way Wpf.Printing.Tests reads WPF's: what matters is that a reader can open it, that the text is
// text in an embedded font a reader can map back to characters, and that the geometry lands where
// the page's units put it.
//

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Printing;
using System.Globalization;
using System.IO;
using Wpf.Printing.Tests;
using Xunit;

namespace Wpf.WinFormsInterop.Tests
{
    public class PrintingTests
    {
        private static PdfDocument PrintToPdf(Action<PrintPageEventArgs> draw, int pages = 1, Action<PrintDocument> setup = null)
        {
            var doc = new PrintDocument { DocumentName = "test" };
            // Paper the test owns, so nothing depends on the machine's default printer.
            doc.DefaultPageSettings.PaperSize = new PaperSize("Letter", 850, 1100);
            setup?.Invoke(doc);
            int page = 0;
            doc.PrintPage += (s, e) =>
            {
                draw(e);
                e.HasMorePages = ++page < pages;
            };
            using var buffer = new MemoryStream();
            doc.PrintController = new PdfPrintController(buffer);
            doc.Print();
            return PdfDocument.Parse(buffer.ToArray());
        }

        private static string Content(PdfDocument pdf, int page = 0)
            => pdf.StreamText(pdf.Pages()[page]["Contents"]);

        // The content stream's lines that end in an operator, split into their operands and it.
        private static IEnumerable<string[]> Operators(string content, string op)
        {
            foreach (string line in content.Split('\n'))
                if (line.EndsWith(" " + op, StringComparison.Ordinal))
                    yield return line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        }

        private static double[] Numbers(string s)
        {
            var list = new List<double>();
            foreach (string t in s.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                list.Add(double.Parse(t, CultureInfo.InvariantCulture));
            return list.ToArray();
        }

        [Fact]
        public void APageIsAPdfPageOfThePaperSize()
        {
            PdfDocument pdf = PrintToPdf(e => e.Graphics.FillRectangle(Brushes.Red, 100, 100, 200, 50), pages: 2);

            Assert.StartsWith("%PDF-", pdf.Header);
            Assert.Equal(2, pdf.Pages().Count);
            var box = (List<object>)pdf.Resolve(pdf.Pages()[0]["MediaBox"]);
            // 8.5 x 11 inches, in points.
            Assert.Equal(612.0, Convert.ToDouble(box[2], CultureInfo.InvariantCulture), 3);
            Assert.Equal(792.0, Convert.ToDouble(box[3], CultureInfo.InvariantCulture), 3);
        }

        [Fact]
        public void GeometryLandsWhereThePageUnitsPutIt()
        {
            string content = Content(PrintToPdf(e =>
            {
                Graphics g = e.Graphics;
                // Display units: a hundredth of an inch.
                g.FillRectangle(Brushes.Red, 100, 200, 300, 50);
                // Millimetres: 10mm is 39.37 hundredths of an inch.
                g.PageUnit = GraphicsUnit.Millimeter;
                g.FillRectangle(Brushes.Blue, 10, 10, 20, 5);
            }));

            // The page starts by turning hundredths of an inch, y down, into points, y up.
            Assert.StartsWith("0.72 0 0 -0.72 0 792 cm", content, StringComparison.Ordinal);
            Assert.Contains("1 0 0 rg", content, StringComparison.Ordinal);
            Assert.Contains("100 200 m", content, StringComparison.Ordinal);
            Assert.Contains("400 250 l", content, StringComparison.Ordinal);

            // The millimetre rectangle went through a page transform of 100/25.4 per unit.
            double[] scale = null;
            foreach (string[] op in Operators(content, "cm"))
                if (op.Length == 7 && op[1] == "0" && op[2] == "0" && op[4] == "0" && op[5] == "0" && op[0] != "0.72")
                    scale = Numbers(string.Join(' ', op, 0, 6));
            Assert.True(scale != null, "no page-unit transform in:\n" + content);
            Assert.Equal(100 / 25.4, scale[0], 3);
            Assert.Contains("10 10 m", content, StringComparison.Ordinal);
        }

        [Fact]
        public void TextIsTextInAnEmbeddedFontThatMapsBackToItsCharacters()
        {
            PdfDocument pdf = PrintToPdf(e =>
            {
                using var font = new Font("Arial", 12);
                e.Graphics.DrawString("Hello PDF", font, Brushes.Black, 100, 100);
            });
            string content = Content(pdf);

            Assert.Contains("BT", content, StringComparison.Ordinal);
            Assert.Contains("Tj", content, StringComparison.Ordinal);

            // The first glyph's text matrix: x past GDI+'s em/6 leading margin, y on the baseline
            // the face's ascent puts under the top of the layout. 12pt is 16.67 hundredths of an inch.
            string[] tm = null;
            foreach (string[] op in Operators(content, "Tj"))
                if (op.Length == 9 && op[6] == "Tm") { tm = op; break; }
            Assert.True(tm != null, "no positioned glyph in:\n" + content);
            double x = double.Parse(tm[4], CultureInfo.InvariantCulture);
            double y = double.Parse(tm[5], CultureInfo.InvariantCulture);
            string firstGlyph = tm[7].Trim('<', '>');
            double em = 12 * 100 / 72.0;
            Assert.InRange(x, 100 + em / 6 - 0.5, 100 + em / 6 + 0.5);
            Assert.InRange(y, 100 + em * 1854 / 2048 - 0.5, 100 + em * 1854 / 2048 + 0.5);

            // The font: a Type0 over an embedded TrueType file, with a ToUnicode map.
            var resources = (PdfDictionary)pdf.Resolve(pdf.Pages()[0]["Resources"]);
            var fonts = (PdfDictionary)pdf.Resolve(resources["Font"]);
            var type0 = (PdfDictionary)pdf.Resolve(Assert.Single(fonts).Value);
            Assert.Equal("Type0", type0["Subtype"]);
            Assert.Equal("Identity-H", type0["Encoding"]);
            var cid = (PdfDictionary)pdf.Resolve(((List<object>)pdf.Resolve(type0["DescendantFonts"]))[0]);
            var descriptor = (PdfDictionary)pdf.Resolve(cid["FontDescriptor"]);
            byte[] file = pdf.StreamData(descriptor["FontFile2"]);
            Assert.True(file != null && file.Length > 10000, "the face is not embedded");

            string cmap = pdf.StreamText(type0["ToUnicode"]);
            Assert.Contains("beginbfchar", cmap, StringComparison.Ordinal);
            // The first glyph is the 'H' of Hello.
            Assert.Contains("<" + firstGlyph + "> <0048>", cmap, StringComparison.Ordinal);
            foreach (char c in "elo PDF".Replace(" ", ""))
                Assert.Contains(((int)c).ToString("X4") + ">", cmap, StringComparison.Ordinal);
        }

        [Fact]
        public void StrokesCurvesGradientsImagesAndClipsStayVector()
        {
            using var bitmap = new Bitmap(4, 3);
            for (int i = 0; i < 4; i++) bitmap.SetPixel(i, 1, Color.Green);
            PdfDocument pdf = PrintToPdf(e =>
            {
                Graphics g = e.Graphics;
                using (var pen = new Pen(Color.Blue, 3) { LineJoin = LineJoin.Round })
                    g.DrawRectangle(pen, 50, 60, 100, 40);
                g.FillEllipse(Brushes.Green, 200, 60, 80, 40);
                using (var grad = new LinearGradientBrush(new Rectangle(50, 150, 200, 50), Color.Yellow, Color.Navy, LinearGradientMode.Horizontal))
                    g.FillRectangle(grad, 50, 150, 200, 50);
                g.DrawImage(bitmap, 300, 150, 40, 30);
                g.SetClip(new Rectangle(50, 250, 100, 30));
                g.FillEllipse(Brushes.Red, 30, 230, 140, 80);
                g.ResetClip();
            });
            string content = Content(pdf);

            // A stroke with its pen: width, round join, stroked.
            Assert.Contains("3 w", content, StringComparison.Ordinal);
            Assert.Contains("1 j", content, StringComparison.Ordinal);
            Assert.Matches(@"50 60 m\n150 60 l\n150 100 l\n50 100 l\nh\nS", content);
            // An ellipse is Beziers, not a polygon.
            Assert.Contains(" c\n", content, StringComparison.Ordinal);
            // A gradient is a shading, not bands.
            Assert.Contains("/Sh0 sh", content, StringComparison.Ordinal);
            var resources = (PdfDictionary)pdf.Resolve(pdf.Pages()[0]["Resources"]);
            var shading = (PdfDictionary)pdf.Resolve(((PdfDictionary)pdf.Resolve(resources["Shading"]))["Sh0"]);
            Assert.Equal(2, Convert.ToInt32(shading["ShadingType"], CultureInfo.InvariantCulture));
            // An image is an image XObject in its rectangle.
            Assert.Contains("/Im0 Do", content, StringComparison.Ordinal);
            var image = (PdfDictionary)((PdfStream)pdf.Resolve(((PdfDictionary)pdf.Resolve(resources["XObject"]))["Im0"])).Dictionary;
            Assert.Equal(4, Convert.ToInt32(image["Width"], CultureInfo.InvariantCulture));
            Assert.Matches(@"40 0 0 -30 300 180 cm /Im0 Do", content);
            // The clip, and everything drawn after it outside it.
            Assert.Contains("50 250 100 30 re W n", content, StringComparison.Ordinal);

            // Balanced graphics state, or readers reject the file.
            int q = 0, Q = 0;
            foreach (string line in content.Split('\n')) { if (line == "q") q++; else if (line == "Q") Q++; }
            Assert.Equal(q, Q);
        }

        [Fact]
        public void APreviewIsThePagesAsScenesNotBitmaps()
        {
            var doc = new PrintDocument();
            doc.DefaultPageSettings.PaperSize = new PaperSize("Letter", 850, 1100);
            int page = 0;
            doc.PrintPage += (s, e) =>
            {
                e.Graphics.FillRectangle(Brushes.Red, 100, 100, 200, 100);
                e.HasMorePages = ++page < 3;
            };
            var controller = new PreviewPrintController();
            doc.PrintController = controller;
            try
            {
                doc.Print();
            }
            catch (InvalidPrinterException)
            {
                Assert.Skip("previewing needs a valid printer, as stock .NET's does");
            }
            PreviewPageInfo[] pages = controller.GetPreviewPageInfo();
            Assert.Equal(3, pages.Length);
            Assert.Equal(new Size(850, 1100), pages[0].PhysicalSize);
            Assert.IsNotType<Bitmap>(pages[0].Image);
            Assert.Equal(850, pages[0].Image.Width);

            // What the preview control does with it: draw it scaled into a box, on a recording Graphics.
            Graphics g = System.Drawing.WebGpuBackend.GpuRaster.NewRecording();
            g.DrawImage(pages[0].Image, new Rectangle(10, 10, 170, 220), 0, 0, 850, 1100, GraphicsUnit.Pixel);
            object scene = System.Drawing.WebGpuBackend.GpuRaster.EndScene(g);
            Assert.NotNull(scene);
        }

        [Fact]
        public void ARecordingGraphicsKeepsGdiPlusTransformState()
        {
            Graphics g = System.Drawing.WebGpuBackend.GpuRaster.NewRecording();
            g.PageUnit = GraphicsUnit.Point;
            Assert.Equal(GraphicsUnit.Point, g.PageUnit);
            g.TranslateTransform(10, 20);
            g.ScaleTransform(2, 3);
            float[] m = g.Transform.Elements;
            Assert.Equal(new float[] { 2, 0, 0, 3, 10, 20 }, m);

            GraphicsState state = g.Save();
            g.RotateTransform(90);
            g.Restore(state);
            Assert.Equal(new float[] { 2, 0, 0, 3, 10, 20 }, g.Transform.Elements);

            // World (1,1) -> page (12, 23) -> device: a point is 96/72 pixels on a 96 dpi recording.
            var pts = new[] { new PointF(1, 1) };
            g.TransformPoints(CoordinateSpace.Device, CoordinateSpace.World, pts);
            Assert.Equal(12 * 96 / 72f, pts[0].X, 3);
            Assert.Equal(23 * 96 / 72f, pts[0].Y, 3);
            g.Dispose();
        }

        [Fact]
        public void OnWindowsItPrintsToAFileThroughThePrinterDriver()
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "the GDI replay is Windows'");
            const string pdfPrinter = "Microsoft Print to PDF";
            bool installed = false;
            foreach (string p in PrinterSettings.InstalledPrinters) installed |= p == pdfPrinter;
            Assert.SkipUnless(installed, "needs Microsoft Print to PDF");

            string file = Path.Combine(Path.GetTempPath(), "wf-print-" + Guid.NewGuid().ToString("N") + ".pdf");
            try
            {
                var doc = new PrintDocument();
                doc.PrinterSettings.PrinterName = pdfPrinter;
                doc.PrinterSettings.PrintToFile = true;
                doc.PrinterSettings.PrintFileName = file;
                doc.PrintPage += (s, e) =>
                {
                    using var font = new Font("Arial", 14);
                    e.Graphics.DrawString("Printed by GDI", font, Brushes.Black, 100, 100);
                    e.Graphics.DrawEllipse(Pens.Blue, 100, 200, 300, 150);
                };
                doc.Print();

                // The driver writes the file when the job is spooled; give it a moment.
                for (int i = 0; i < 100 && (!File.Exists(file) || new FileInfo(file).Length == 0); i++)
                    System.Threading.Thread.Sleep(100);
                Assert.True(File.Exists(file), "the job wrote no file");
                byte[] bytes = File.ReadAllBytes(file);
                Assert.True(bytes.Length > 1000);
                Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(bytes, 0, 4));
            }
            finally
            {
                try { File.Delete(file); } catch (IOException) { }
            }
        }
    }
}
