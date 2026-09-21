// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// WPF drawing to PDF, checked by reading the file back.
//
// Every test here parses the produced bytes with PdfReader, which starts from startxref and walks
// the structure the way an actual reader does. That is the assertion that matters: not "the writer
// emitted the operators we expected" but "the file resolves".
//

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Xps.Pdf;
using Xunit;

namespace Wpf.Printing.Tests
{
    public class PdfWriterTests
    {
        [Fact]
        public void ProducesAFileAReaderCanOpen()
        {
            PdfDocument pdf = Render(dc => dc.DrawRectangle(Brushes.Red, null, new Rect(10, 10, 100, 50)));

            Assert.StartsWith("%PDF-1.", pdf.Header, StringComparison.Ordinal);
            Assert.NotNull(pdf.Trailer);
            Assert.NotNull(pdf.Catalog);
            Assert.Equal("Catalog", pdf.Catalog["Type"]);
        }

        [Fact]
        public void EveryObjectInTheCrossReferenceTableResolves()
        {
            // The failure this catches is the one that matters most in a binary format: an offset
            // that is stale, or an object written but never listed. Both produce a file that opens
            // in one reader and not another.
            PdfDocument pdf = Render(dc =>
            {
                dc.DrawRectangle(Brushes.Blue, new Pen(Brushes.Black, 2), new Rect(0, 0, 50, 50));
                dc.DrawEllipse(Brushes.Green, null, new Point(100, 100), 40, 40);
            });

            foreach (int number in pdf.ObjectNumbers)
            {
                object resolved = pdf.Resolve(new PdfReference { Number = number });
                Assert.NotNull(resolved);
            }
        }

        [Fact]
        public void OnePagePerVisual()
        {
            using var buffer = new MemoryStream();
            using (var writer = new PdfDocumentWriter(buffer, leaveOpen: true))
            {
                writer.Write(Draw(dc => dc.DrawRectangle(Brushes.Red, null, new Rect(0, 0, 10, 10))));
                writer.Write(Draw(dc => dc.DrawRectangle(Brushes.Blue, null, new Rect(0, 0, 10, 10))));
                writer.Write(Draw(dc => dc.DrawRectangle(Brushes.Green, null, new Rect(0, 0, 10, 10))));
            }

            Assert.Equal(3, PdfDocument.Parse(buffer.ToArray()).Pages().Count);
        }

        [Fact]
        public void PageSizeIsInPointsNotPixels()
        {
            // WPF measures in 96ths of an inch and PDF in 72nds. Getting this wrong is not subtle in
            // effect -- a Letter page comes out 11.1 by 14.7 inches -- but it is invisible on screen,
            // where everything is scaled to fit anyway.
            using var buffer = new MemoryStream();
            using (var writer = new PdfDocumentWriter(buffer, leaveOpen: true))
            {
                writer.PageSize = new Size(816, 1056);   // US Letter in WPF units
                writer.Write(Draw(dc => dc.DrawRectangle(Brushes.Red, null, new Rect(0, 0, 10, 10))));
            }

            PdfDocument pdf = PdfDocument.Parse(buffer.ToArray());
            var box = (List<object>)pdf.Resolve(pdf.Pages()[0]["MediaBox"]);

            Assert.Equal(0.0, Convert.ToDouble(box[0], CultureInfo.InvariantCulture), 3);
            Assert.Equal(612.0, Convert.ToDouble(box[2], CultureInfo.InvariantCulture), 1);   // 8.5 inch
            Assert.Equal(792.0, Convert.ToDouble(box[3], CultureInfo.InvariantCulture), 1);   // 11 inch
        }

        [Fact]
        public void AFilledRectangleBecomesAFilledPath()
        {
            string content = ContentOf(Render(dc =>
                dc.DrawRectangle(Brushes.Red, null, new Rect(10, 20, 100, 50))));

            Assert.Contains(" m", content, StringComparison.Ordinal);        // path started
            Assert.Contains(" l", content, StringComparison.Ordinal);        // lines
            Assert.Contains("f", content, StringComparison.Ordinal);         // filled
            Assert.Contains("1 0 0 rg", content, StringComparison.Ordinal);  // red
        }

        [Fact]
        public void AStrokedShapeCarriesItsPenState()
        {
            string content = ContentOf(Render(dc => dc.DrawRectangle(
                null,
                new Pen(Brushes.Black, 4) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round },
                new Rect(10, 10, 80, 40))));

            Assert.Contains("4 w", content, StringComparison.Ordinal);       // thickness
            Assert.Contains("1 J", content, StringComparison.Ordinal);       // round cap
            Assert.Contains("1 j", content, StringComparison.Ordinal);       // round join
            Assert.Contains("S", content, StringComparison.Ordinal);         // stroked
        }

        [Fact]
        public void EveryContentStreamBalancesItsGraphicsState()
        {
            // An unbalanced q/Q is not a rendering glitch, it is a file readers reject. It is also
            // easy to produce: any early return between a push and its pop leaves one behind.
            PdfDocument pdf = Render(dc =>
            {
                dc.PushClip(new EllipseGeometry(new Point(50, 50), 40, 40));
                dc.PushTransform(new RotateTransform(15));
                dc.DrawRectangle(Brushes.Red, new Pen(Brushes.Blue, 2), new Rect(0, 0, 100, 100));
                dc.Pop();
                dc.Pop();
            });

            string content = ContentOf(pdf);

            int pushes = CountOperator(content, "q");
            int pops = CountOperator(content, "Q");
            Assert.Equal(pushes, pops);
        }

        [Fact]
        public void ARectangularClipIsResolvedIntoTheShape()
        {
            // No clipping path in the output, and that is right: the flattener intersects a
            // rectangular clip with a rectangle analytically and emits the result. Asserting on a
            // "W n" here would be testing for work that should not be done.
            string content = ContentOf(Render(dc =>
            {
                dc.PushClip(new RectangleGeometry(new Rect(20, 20, 40, 40)));
                dc.DrawRectangle(Brushes.Red, null, new Rect(0, 0, 200, 200));
                dc.Pop();
            }));

            Assert.DoesNotContain("W n", content, StringComparison.Ordinal);

            // The 200x200 rectangle has become the 40x40 the clip left of it.
            Assert.Contains("20 20 m", content, StringComparison.Ordinal);
            Assert.Contains("60 20 l", content, StringComparison.Ordinal);
            Assert.DoesNotContain("200 200 l", content, StringComparison.Ordinal);
        }

        [Fact]
        public void ACurvedClipCutsTheShapeItClips()
        {
            // The clip does not survive as a clipping path here either: with real geometry booleans
            // underneath it, the flattener can intersect an elliptical clip with a rectangle and emit
            // the result. That is the better outcome, and it only became possible once Geometry.Combine
            // stopped approximating every shape by its bounding rectangle.
            string content = ContentOf(Render(dc =>
            {
                dc.PushClip(new EllipseGeometry(new Point(50, 50), 30, 30));
                dc.DrawRectangle(Brushes.Red, null, new Rect(0, 0, 200, 200));
                dc.Pop();
            }));

            Assert.DoesNotContain("200 200 l", content, StringComparison.Ordinal);

            // Curved, so many short segments rather than a rectangle's four.
            Assert.True(CountOperator(content, "l") > 20,
                $"the clipped shape has {CountOperator(content, "l")} line segments, so it is not the circle");
        }

        [Fact]
        public void OpacityIsFlattenedAgainstThePage()
        {
            // Not a bug, and worth stating because it looks like one: the pipeline is called the
            // ALPHA FLATTENER, and this is the flattening. A half-transparent red over nothing is
            // composited against the page and emitted as opaque pink, because that is what it will
            // look like on paper and because the GDI device this pipeline was written for could not
            // express transparency at all.
            //
            // PDF could have carried the alpha. The cost of going through this seam is that by the
            // time drawing arrives, the decision has been made.
            string content = ContentOf(Render(dc =>
            {
                dc.PushOpacity(0.5);
                dc.DrawRectangle(Brushes.Red, null, new Rect(0, 0, 100, 100));
                dc.Pop();
            }));

            Assert.Contains("1 0.502 0.502 rg", content, StringComparison.Ordinal);
        }

        [Fact]
        public void AnImageBecomesAnXObject()
        {
            var pixels = new byte[4 * 4 * 4];
            for (int i = 0; i < pixels.Length; i += 4)
            {
                pixels[i] = 0; pixels[i + 1] = 0; pixels[i + 2] = 255; pixels[i + 3] = 255;   // opaque red
            }

            BitmapSource bitmap = BitmapSource.Create(
                4, 4, 96, 96, PixelFormats.Pbgra32, null, pixels, 16);

            using var buffer = new MemoryStream();
            using (var writer = new PdfDocumentWriter(buffer, leaveOpen: true))
            {
                writer.Write(Draw(dc => dc.DrawImage(bitmap, new Rect(10, 10, 40, 40))));
            }

            PdfDocument pdf = PdfDocument.Parse(buffer.ToArray());
            var resources = (PdfDictionary)pdf.Resolve(pdf.Pages()[0]["Resources"]);

            Assert.True(resources.ContainsKey("XObject"), "a drawn image produced no XObject");

            var xobjects = (PdfDictionary)pdf.Resolve(resources["XObject"]);
            var image = (PdfStream)pdf.Resolve(xobjects.OnlyValue());

            Assert.Equal("Image", image.Dictionary["Subtype"]);
            Assert.Equal(4.0, Convert.ToDouble(image.Dictionary["Width"], CultureInfo.InvariantCulture));
            Assert.Equal("DeviceRGB", image.Dictionary["ColorSpace"]);

            // Three bytes per pixel: PDF has no premultiplied RGBA, so alpha would be a separate mask.
            Assert.Equal(4 * 4 * 3, pdf.StreamData(image).Length);
        }

        [Fact]
        public void NumbersAreWrittenInvariantly()
        {
            // A locale that writes 0,75 for three quarters produces a file every reader rejects, and
            // it is the classic bug that only appears on someone else's machine.
            System.Globalization.CultureInfo original = System.Globalization.CultureInfo.CurrentCulture;
            try
            {
                System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");

                string content = ContentOf(Render(dc =>
                    dc.DrawRectangle(Brushes.Red, null, new Rect(10.5, 20.25, 100, 50))));

                Assert.DoesNotContain(",", content, StringComparison.Ordinal);
                Assert.Contains("10.5", content, StringComparison.Ordinal);
            }
            finally
            {
                System.Globalization.CultureInfo.CurrentCulture = original;
            }
        }

        [Fact]
        public void CurvesAreFlattenedForPrintNotForAScreen()
        {
            // WPF's default tolerance of 0.25 units was chosen against a display, where it is a
            // quarter of a pixel. A printer rasterizes at 600 dpi or more, where the same number is
            // nearly two pixels and shows as faceting on any large curve.
            string content = ContentOf(Render(dc =>
                dc.DrawEllipse(Brushes.Orange, null, new Point(300, 300), 250, 250)));

            int segments = CountOperator(content, "l");

            // At 0.25 a 250-unit circle comes out around 80 segments; at 0.05 it is roughly 170.
            Assert.True(segments > 120,
                $"a 250-unit circle flattened to {segments} segments, which is a screen tolerance, not a print one");
        }

        [Fact]
        public void AnEmptyDocumentIsStillAValidFile()
        {
            using var buffer = new MemoryStream();
            using (var writer = new PdfDocumentWriter(buffer, leaveOpen: true)) { }

            PdfDocument pdf = PdfDocument.Parse(buffer.ToArray());
            Assert.NotNull(pdf.Catalog);
            Assert.Empty(pdf.Pages());
        }

        [Fact]
        public void AnEmptyVisualStillProducesAPage()
        {
            // Printing a blank page is a thing people do, and a writer that skipped it would produce
            // a document whose page numbering silently disagreed with the document's.
            PdfDocument pdf = Render(dc => { });
            Assert.Single(pdf.Pages());
        }


        // ---- helpers ---------------------------------------------------------------

        internal static DrawingVisual Draw(Action<DrawingContext> body)
        {
            var visual = new DrawingVisual();
            using (DrawingContext dc = visual.RenderOpen()) body(dc);
            return visual;
        }

        internal static PdfDocument Render(Action<DrawingContext> body)
        {
            using var buffer = new MemoryStream();
            using (var writer = new PdfDocumentWriter(buffer, leaveOpen: true))
            {
                writer.Write(Draw(body));
            }
            return PdfDocument.Parse(buffer.ToArray());
        }

        internal static string ContentOf(PdfDocument pdf, int page = 0)
            => pdf.StreamText(pdf.Pages()[page]["Contents"]);

        /// <summary>Counts a single-letter operator, ignoring the letter inside other tokens.</summary>
        internal static int CountOperator(string content, string op)
        {
            int count = 0;
            foreach (string token in content.Split(new[] { ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (token == op) count++;
            }
            return count;
        }
    }

    internal static class PdfDictionaryExtensions
    {
        /// <summary>The single value of a one-entry dictionary, which resource dictionaries here are.</summary>
        internal static object OnlyValue(this PdfDictionary dictionary)
        {
            foreach (object value in dictionary.Values) return value;
            throw new Xunit.Sdk.XunitException("the resource dictionary is empty");
        }
    }
}
