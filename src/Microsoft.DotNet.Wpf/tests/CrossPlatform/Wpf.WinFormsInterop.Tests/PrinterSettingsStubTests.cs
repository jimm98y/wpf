// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// PrinterSettings members that were Mono stubs throwing NotImplementedException: CreateMeasurementGraphics (a
// printed page's Graphics for the page settings), IsDirectPrintingSupported and the collections' CopyTo.
//

using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Printing;
using Xunit;

namespace Wpf.WinFormsInterop.Tests
{
    public class PrinterSettingsStubTests
    {
        [Fact]
        public void CreateMeasurementGraphics_MeasuresAsAPrintedPage()
        {
            var settings = new PrinterSettings();
            using Graphics g = settings.CreateMeasurementGraphics();
            Assert.Equal(GraphicsUnit.Display, g.PageUnit);
            Assert.True(g.DpiX >= 72, $"dpi {g.DpiX}");
            SizeF size;
            using (var font = new Font("Arial", 12f))
                size = g.MeasureString("Hello", font);
            Assert.True(size.Width > 0 && size.Height > 0);
            // A 12-point line is about a sixth of an inch: some 17-20 hundredths in the page unit.
            Assert.InRange(size.Height, 12f, 30f);

            using Graphics moved = settings.CreateMeasurementGraphics(honorOriginAtMargins: true);
            PageSettings page = settings.DefaultPageSettings;
            Assert.Equal(page.Margins.Left - page.HardMarginX, moved.Transform.OffsetX, 2);
        }

        [Fact]
        public void IsDirectPrintingSupported_IsNeverTrueOutsideJpegAndPng()
        {
            var settings = new PrinterSettings();
            Assert.False(settings.IsDirectPrintingSupported(ImageFormat.Bmp));
            Assert.False(settings.IsDirectPrintingSupported(ImageFormat.Gif));
            using var bmp = new Bitmap(4, 4);
            Assert.False(settings.IsDirectPrintingSupported(bmp));   // a memory bitmap's RawFormat is MemoryBmp
        }

        [Fact]
        public void Graphics_GetContextInfo_IsTheClipAndTheTransform()
        {
            using var bmp = new Bitmap(50, 50);
            using Graphics g = Graphics.FromImage(bmp);
#pragma warning disable SYSLIB0016, CS0618
            var info = (object[])g.GetContextInfo();
            Assert.True(((Region)info[0]).IsInfinite(g));
            g.TranslateTransform(5, 7);
            g.SetClip(new Rectangle(1, 2, 10, 10));
            info = (object[])g.GetContextInfo();
#pragma warning restore SYSLIB0016, CS0618
            Assert.Equal(new RectangleF(1, 2, 10, 10), ((Region)info[0]).GetBounds(g));   // in world units, as Clip
            Assert.Equal(5f, ((System.Drawing.Drawing2D.Matrix)info[1]).OffsetX);
        }

        [Fact]
        public void TheSettingsCollections_CopyTo()
        {
            var sizes = new PrinterSettings.PaperSizeCollection(new[] { new PaperSize("A", 100, 200) });
            var array = new PaperSize[2];
            sizes.CopyTo(array, 1);
            Assert.Equal("A", array[1].PaperName);

            var sources = new PrinterSettings.PaperSourceCollection(new[] { new PaperSource { SourceName = "Tray" } });
            var srcArray = new PaperSource[1];
            sources.CopyTo(srcArray, 0);
            Assert.Equal("Tray", srcArray[0].SourceName);

            var res = new PrinterSettings.PrinterResolutionCollection(new[] { new PrinterResolution { X = 600, Y = 600 } });
            var resArray = new PrinterResolution[1];
            res.CopyTo(resArray, 0);
            Assert.Equal(600, resArray[0].X);

            var names = new PrinterSettings.StringCollection(new[] { "p1", "p2" });
            var nameArray = new string[2];
            names.CopyTo(nameArray, 0);
            Assert.Equal(new[] { "p1", "p2" }, nameArray);
        }
    }
}
