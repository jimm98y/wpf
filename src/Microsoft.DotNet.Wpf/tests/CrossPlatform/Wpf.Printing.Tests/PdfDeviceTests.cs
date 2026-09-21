// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The PDF device on its own, driven through ILegacyDevice by hand.
//
// PdfWriterTests goes through the whole pipeline, which is the right way to check what a user gets.
// But the pipeline is a filter as much as a conduit: the alpha flattener composites transparency
// against the page, resolves clips into the shapes they clip, and decomposes gradients before the
// device ever sees them. Several of the device's own paths are therefore unreachable from the top --
// soft masks, ExtGState alpha, clipping paths -- and would sit untested while looking covered.
//
// So these tests call the interface directly. That is not reaching past an abstraction: ILegacyDevice
// IS the contract, and the question here is whether the device honours it for calls the current
// upstream happens not to make.
//

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Xps.Pdf;
using Xunit;

namespace Wpf.Printing.Tests
{
    public class PdfDeviceTests
    {
        [Fact]
        public void PushClipEmitsAClippingPath()
        {
            PdfDocument pdf = Drive(device =>
            {
                device.PushClip(new EllipseGeometry(new Point(50, 50), 30, 30));
                device.DrawGeometry(Brushes.Red, null, null, new RectangleGeometry(new Rect(0, 0, 200, 200)));
                device.PopClip();
            });

            string content = PdfWriterTests.ContentOf(pdf);

            // Either operator is a clipping path; which one depends on the clip's fill rule, and
            // WPF's default for a PathGeometry is even-odd. For a convex shape the two agree.
            Assert.True(content.Contains("W n", StringComparison.Ordinal) ||
                        content.Contains("W* n", StringComparison.Ordinal),
                        "the clip did not reach the device as a clipping path");
        }

        [Fact]
        public void AnEvenOddClipUsesTheEvenOddOperator()
        {
            var geometry = new PathGeometry { FillRule = FillRule.EvenOdd };
            geometry.AddGeometry(new EllipseGeometry(new Point(50, 50), 40, 40));
            geometry.AddGeometry(new EllipseGeometry(new Point(50, 50), 20, 20));

            PdfDocument pdf = Drive(device =>
            {
                device.PushClip(geometry);
                device.DrawGeometry(Brushes.Red, null, null, new RectangleGeometry(new Rect(0, 0, 200, 200)));
                device.PopClip();
            });

            // A ring clip drawn with the nonzero rule would be a disc: the hole would fill in.
            Assert.Contains("W* n", PdfWriterTests.ContentOf(pdf), StringComparison.Ordinal);
        }

        [Fact]
        public void AnEmptyClipBlocksEverything()
        {
            // "Clip to nothing" has to mean nothing is drawn. Emitting no clip at all -- the easy
            // mistake -- means everything is drawn, which is the opposite.
            PdfDocument pdf = Drive(device =>
            {
                device.PushClip(new PathGeometry());
                device.DrawGeometry(Brushes.Red, null, null, new RectangleGeometry(new Rect(0, 0, 200, 200)));
                device.PopClip();
            });

            Assert.Contains("0 0 0 0 re W n", PdfWriterTests.ContentOf(pdf), StringComparison.Ordinal);
        }

        [Fact]
        public void ResidualAlphaBecomesAnExtGState()
        {
            // Reachable when the flattener leaves alpha behind rather than compositing it away.
            PdfDocument pdf = Drive(device =>
            {
                var translucent = new SolidColorBrush(Color.FromArgb(128, 255, 0, 0));
                device.DrawGeometry(translucent, null, null, new RectangleGeometry(new Rect(0, 0, 50, 50)));
            });

            var resources = (PdfDictionary)pdf.Resolve(pdf.Pages()[0]["Resources"]);
            Assert.True(resources.ContainsKey("ExtGState"), "a half-transparent brush produced no ExtGState");

            var states = (PdfDictionary)pdf.Resolve(resources["ExtGState"]);
            var state = (PdfDictionary)pdf.Resolve(states.OnlyValue());

            Assert.Equal(0.502, Convert.ToDouble(state["ca"], CultureInfo.InvariantCulture), 2);
            Assert.Equal(0.502, Convert.ToDouble(state["CA"], CultureInfo.InvariantCulture), 2);
        }

        [Fact]
        public void ATransparentImageCarriesASoftMask()
        {
            var pixels = new byte[2 * 2 * 4];
            for (int i = 0; i < pixels.Length; i += 4)
            {
                // Premultiplied: a half-transparent mid-grey is stored at half strength.
                pixels[i] = 64; pixels[i + 1] = 64; pixels[i + 2] = 64; pixels[i + 3] = 128;
            }

            BitmapSource bitmap = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Pbgra32, null, pixels, 8);

            PdfDocument pdf = Drive(device => device.DrawImage(bitmap, null, new Rect(0, 0, 10, 10)));

            var resources = (PdfDictionary)pdf.Resolve(pdf.Pages()[0]["Resources"]);
            var xobjects = (PdfDictionary)pdf.Resolve(resources["XObject"]);
            var image = (PdfStream)pdf.Resolve(xobjects.OnlyValue());

            Assert.True(image.Dictionary.ContainsKey("SMask"), "a half-transparent image carried no soft mask");

            var mask = (PdfStream)pdf.Resolve(image.Dictionary["SMask"]);
            Assert.Equal("DeviceGray", mask.Dictionary["ColorSpace"]);
            Assert.Equal(4, pdf.StreamData(mask).Length);
            Assert.Equal(128, pdf.StreamData(mask)[0]);

            // Un-premultiplied on the way out: 64 at an alpha of 128 is a mid-grey at full strength,
            // not a quarter grey. Skipping that division is what puts dark halos on soft edges.
            Assert.InRange(pdf.StreamData(image)[0], 120, 136);
        }

        [Fact]
        public void AnOpaqueImageCarriesNoMask()
        {
            var pixels = new byte[2 * 2 * 4];
            for (int i = 0; i < pixels.Length; i += 4)
            {
                pixels[i] = 0; pixels[i + 1] = 0; pixels[i + 2] = 255; pixels[i + 3] = 255;
            }

            BitmapSource bitmap = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Pbgra32, null, pixels, 8);
            PdfDocument pdf = Drive(device => device.DrawImage(bitmap, null, new Rect(0, 0, 10, 10)));

            var resources = (PdfDictionary)pdf.Resolve(pdf.Pages()[0]["Resources"]);
            var xobjects = (PdfDictionary)pdf.Resolve(resources["XObject"]);
            var image = (PdfStream)pdf.Resolve(xobjects.OnlyValue());

            // The common case, and worth half the bytes.
            Assert.False(image.Dictionary.ContainsKey("SMask"));
        }

        [Fact]
        public void TheRawBufferOverloadIsHonoured()
        {
            // The flattener passes a Pbgra32 buffer alongside the source when it rasterized the image
            // itself, and the contract is that the BUFFER wins.
            var source = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Pbgra32, null, new byte[16], 8);

            var buffer = new byte[2 * 2 * 4];
            for (int i = 0; i < buffer.Length; i += 4)
            {
                buffer[i] = 0; buffer[i + 1] = 255; buffer[i + 2] = 0; buffer[i + 3] = 255;   // green
            }

            PdfDocument pdf = Drive(device => device.DrawImage(source, buffer, new Rect(0, 0, 10, 10)));

            var resources = (PdfDictionary)pdf.Resolve(pdf.Pages()[0]["Resources"]);
            var xobjects = (PdfDictionary)pdf.Resolve(resources["XObject"]);
            byte[] rgb = pdf.StreamData((PdfStream)pdf.Resolve(xobjects.OnlyValue()));

            Assert.Equal(0, rgb[0]);      // R
            Assert.Equal(255, rgb[1]);    // G
            Assert.Equal(0, rgb[2]);      // B
        }

        [Fact]
        public void TransformsNestAndUnwind()
        {
            PdfDocument pdf = Drive(device =>
            {
                device.PushTransform(new Matrix(2, 0, 0, 2, 10, 20));
                device.PushTransform(new Matrix(1, 0, 0, 1, 5, 5));
                device.DrawGeometry(Brushes.Red, null, null, new RectangleGeometry(new Rect(0, 0, 10, 10)));
                device.PopTransform();
                device.PopTransform();
            });

            string content = PdfWriterTests.ContentOf(pdf);

            Assert.Contains("2 0 0 2 10 20 cm", content, StringComparison.Ordinal);
            Assert.Contains("1 0 0 1 5 5 cm", content, StringComparison.Ordinal);
            Assert.Equal(PdfWriterTests.CountOperator(content, "q"), PdfWriterTests.CountOperator(content, "Q"));
        }

        [Fact]
        public void AnUnclosedPageStillBalances()
        {
            // A push without its pop is a bug upstream, but it must not produce an unreadable file.
            PdfDocument pdf = Drive(device =>
            {
                device.PushClip(new RectangleGeometry(new Rect(0, 0, 10, 10)));
                device.PushTransform(new Matrix(1, 0, 0, 1, 5, 5));
                device.DrawGeometry(Brushes.Red, null, null, new RectangleGeometry(new Rect(0, 0, 10, 10)));
                // deliberately no pops
            });

            string content = PdfWriterTests.ContentOf(pdf);
            Assert.Equal(PdfWriterTests.CountOperator(content, "q"), PdfWriterTests.CountOperator(content, "Q"));
        }

        [Fact]
        public void ABrushTheFlattenerLeftUnresolvedDrawsNothing()
        {
            // A gradient should never arrive here; the flattener decomposes them. If one does, the
            // choice is between drawing it as some solid colour and drawing nothing. Nothing is
            // right: a black rectangle over the content is far worse than a gap.
            PdfDocument pdf = Drive(device => device.DrawGeometry(
                new LinearGradientBrush(Colors.Red, Colors.Blue, 0), null, null,
                new RectangleGeometry(new Rect(0, 0, 100, 100))));

            string content = PdfWriterTests.ContentOf(pdf);
            Assert.DoesNotContain(" f", content, StringComparison.Ordinal);
        }

        [Fact]
        public void TheDeviceContextMembersRefuse()
        {
            // These describe a Windows print job, not drawing. Throwing is what the framework's own
            // managed ILegacyDevice does, and it is better than pretending: a caller that needs a
            // device context needs a device, and there is not one here.
            using var buffer = new MemoryStream();
            using var writer = new PdfDocumentWriter(buffer, leaveOpen: true);

            PdfDevice device = writer.Device;

            Assert.Throws<InvalidOperationException>(() => device.CreateDeviceContext("p", "j", null));
            Assert.Throws<InvalidOperationException>(() => device.DeleteDeviceContext());
            Assert.Throws<InvalidOperationException>(() => device.ExtEscGetName());
            Assert.Throws<InvalidOperationException>(() => device.ExtEscMXDWPassThru());
        }

        // ---- helpers ---------------------------------------------------------------

        /// <summary>Drives one page through ILegacyDevice directly and parses the result.</summary>
        private static PdfDocument Drive(Action<PdfDevice> body)
        {
            using var buffer = new MemoryStream();

            using (var writer = new PdfDocumentWriter(buffer, leaveOpen: true))
            {
                PdfDevice device = writer.Device;

                device.StartDocument(null, "test", null, null);
                device.StartPage(null, 96);
                body(device);
                device.EndPage();
                device.EndDocument();
            }

            return PdfDocument.Parse(buffer.ToArray());
        }
    }
}
