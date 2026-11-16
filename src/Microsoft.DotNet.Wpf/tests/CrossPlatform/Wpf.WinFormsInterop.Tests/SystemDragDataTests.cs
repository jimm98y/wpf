// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// What a WinForms form hands other applications when it starts a drag, and what it makes of
// a drag dropped on it from another application (SystemDragData.cs, for CocoaHost and WaylandFormHost). The drag session itself needs
// a real mouse drag; the data each way is checked here: the types other applications read (text, file
// URIs, HTML, RTF, PNG) and the formats Windows applications read back (FileDrop paths, CF_HTML, a
// Bitmap).
//

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using Xunit;

namespace Wpf.WinFormsInterop.Tests
{
    public class SystemDragDataTests
    {
        [Fact]
        public void ADraggedDataObjectOffersWhatOtherApplicationsRead()
        {
            using var image = new Bitmap(2, 2);
            var data = new DataObject();
            data.SetData(DataFormats.UnicodeText, "dragged text");
            data.SetData(DataFormats.FileDrop, new[] { "/tmp/one.txt", "/tmp/two words.txt" });
            data.SetData(DataFormats.Html, XplatUIWebGpu.ToCfHtml("<b>x</b>"));
            data.SetData(DataFormats.Rtf, @"{\rtf1 x}");
            data.SetData(DataFormats.Bitmap, image);

            Dictionary<string, byte[]> payload = SystemDragData.Payload(data);

            Assert.True(payload.ContainsKey("application/x-wpf-dragdrop"));
            Assert.Equal("dragged text", System.Text.Encoding.UTF8.GetString(payload["text/plain;charset=utf-8"]));
            Assert.Equal("file:///tmp/one.txt\r\nfile:///tmp/two%20words.txt\r\n", System.Text.Encoding.UTF8.GetString(payload["text/uri-list"]));
            Assert.Equal("<b>x</b>", System.Text.Encoding.UTF8.GetString(payload["text/html"]));
            Assert.Equal(@"{\rtf1 x}", System.Text.Encoding.UTF8.GetString(payload["text/rtf"]));
            Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, payload["image/png"][..4]);
        }

        [Fact]
        public void AFinderDragArrivesAsAFileDropList()
        {
            var types = new[] { "text/uri-list", "text/plain;charset=utf-8" };
            byte[] Read(string mime) => mime switch
            {
                "text/uri-list" => System.Text.Encoding.UTF8.GetBytes("file:///Users/someone/Report%20Q3.pdf\r\n"),
                "text/plain;charset=utf-8" => System.Text.Encoding.UTF8.GetBytes("Report Q3.pdf"),
                _ => null,
            };

            IDataObject got = SystemDragData.ToDataObject(types, Read);

            Assert.True(got.GetDataPresent(DataFormats.FileDrop));
            Assert.Equal(new[] { "/Users/someone/Report Q3.pdf" }, (string[])got.GetData(DataFormats.FileDrop));
            Assert.Equal("Report Q3.pdf", got.GetData(DataFormats.UnicodeText));
        }

        [Fact]
        public void HtmlAndImagesArriveAsWindowsHandsThemOver()
        {
            byte[] png;
            using (var image = new Bitmap(3, 3))
            using (var ms = new System.IO.MemoryStream())
            {
                image.SetPixel(1, 1, Color.Lime);
                image.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                png = ms.ToArray();
            }
            var types = new[] { "text/html", "image/png" };
            byte[] Read(string mime) => mime == "text/html" ? System.Text.Encoding.UTF8.GetBytes("<p>hi</p>") : mime == "image/png" ? png : null;

            IDataObject got = SystemDragData.ToDataObject(types, Read);

            Assert.Equal("<p>hi</p>", XplatUIWebGpu.HtmlFragment((string)got.GetData(DataFormats.Html)));
            var bmp = Assert.IsType<Bitmap>(got.GetData(DataFormats.Bitmap));
            Assert.Equal(Color.Lime.ToArgb(), bmp.GetPixel(1, 1).ToArgb());
        }
    }
}
