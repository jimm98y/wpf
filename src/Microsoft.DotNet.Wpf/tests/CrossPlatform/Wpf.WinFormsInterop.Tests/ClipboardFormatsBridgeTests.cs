// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// What crosses to other applications besides text: a copy's HTML, RTF, image and file list.
//
// WinForms stores a data object one format at a time. Each format written to the system clipboard on
// its own replaced the one before (NSPasteboard's and the Wayland selection's writes each start over),
// and only text went out at all: a rich text box's copy reached another application as plain text,
// an image or a file list not at all. Now everything a copy offers goes out at the clipboard's close,
// as one write, and comes back from another application's copy the way Windows hands it over --
// "HTML Format" as CF_HTML with its header, images as a Bitmap, a file list as paths.
//
// As in ClipboardBridgeTests, the desktop's clipboard is not touched: a fake stands in for it.
//

using System;
using System.Collections.Specialized;
using System.Drawing;
using System.Windows.Forms;
using Xunit;

namespace Wpf.WinFormsInterop.Tests
{
    public class ClipboardFormatsBridgeTests : IDisposable
    {
        private sealed class FakeClipboard : IClipboardBridge
        {
            internal string Text, Html, Rtf;
            internal byte[] Png;
            internal string[] Files;
            internal int Writes;

            public bool TryGetText(out string text) { text = Text; return Text != null; }
            public void SetText(string text) { Text = text; Writes++; }
            public bool ContainsText() => Text != null;
            public void Clear() { Text = Html = Rtf = null; Png = null; Files = null; }

            public void SetFormats(string text, byte[] png, string html, string rtf, string[] files)
            {
                Text = text; Png = png; Html = html; Rtf = rtf; Files = files;
                Writes++;
            }

            public bool Contains(ClipKind kind) => kind switch
            {
                ClipKind.Text => Text != null,
                ClipKind.Png => Png != null,
                ClipKind.Html => Html != null,
                ClipKind.Rtf => Rtf != null,
                ClipKind.Files => Files != null,
                _ => false,
            };

            public byte[] GetPng() => Png;
            public string GetHtml() => Html;
            public string GetRtf() => Rtf;
            public string[] GetFiles() => Files;
        }

        private readonly IClipboardBridge _saved;
        private readonly FakeClipboard _fake = new();

        public ClipboardFormatsBridgeTests()
        {
            Assert.NotNull(XplatUIWebGpu.GetInstance());
            _saved = XplatUIWebGpu.ClipboardBridge;
            XplatUIWebGpu.ClipboardBridge = _fake;
        }

        public void Dispose() => XplatUIWebGpu.ClipboardBridge = _saved;

        [Fact]
        public void EverythingACopyOffersGoesOutInOneWrite()
        {
            using var image = new Bitmap(3, 2);
            image.SetPixel(1, 1, Color.Red);
            var data = new DataObject();
            data.SetData(DataFormats.UnicodeText, "plain");
            data.SetData(DataFormats.Rtf, @"{\rtf1 rich}");
            data.SetData(DataFormats.Html, XplatUIWebGpu.ToCfHtml("<b>bold</b>"));
            data.SetData(DataFormats.Bitmap, image);
            var files = new StringCollection { "/tmp/a.txt", "/tmp/b c.txt" };
            data.SetData(DataFormats.FileDrop, files);

            Clipboard.SetDataObject(data);

            Assert.Equal(1, _fake.Writes);
            Assert.Equal("plain", _fake.Text);
            Assert.Equal(@"{\rtf1 rich}", _fake.Rtf);
            Assert.Equal("<b>bold</b>", _fake.Html);          // the fragment, not Windows' CF_HTML
            Assert.Equal(new[] { "/tmp/a.txt", "/tmp/b c.txt" }, _fake.Files);
            Assert.NotNull(_fake.Png);
            using var back = new Bitmap(new System.IO.MemoryStream(_fake.Png));
            Assert.Equal(new Size(3, 2), back.Size);
            Assert.Equal(Color.Red.ToArgb(), back.GetPixel(1, 1).ToArgb());
        }

        [Fact]
        public void AnotherApplicationsCopyComesBackAsWindowsHandsItOver()
        {
            using (var image = new Bitmap(4, 4))
            using (var ms = new System.IO.MemoryStream())
            {
                image.SetPixel(2, 2, Color.Blue);
                image.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                _fake.Png = ms.ToArray();
            }
            _fake.Text = "from elsewhere";
            _fake.Html = "<i>it</i>";
            _fake.Rtf = @"{\rtf1 it}";
            _fake.Files = new[] { "/Users/someone/Documents/report.pdf" };

            IDataObject got = Clipboard.GetDataObject();

            Assert.Equal("from elsewhere", got.GetData(DataFormats.UnicodeText));
            Assert.Equal(@"{\rtf1 it}", got.GetData(DataFormats.Rtf));
            string html = (string)got.GetData(DataFormats.Html);
            Assert.StartsWith("Version:0.9", html);
            Assert.Equal("<i>it</i>", XplatUIWebGpu.HtmlFragment(html));
            Assert.Equal(new[] { "/Users/someone/Documents/report.pdf" }, (string[])got.GetData(DataFormats.FileDrop));
            Assert.True(Clipboard.ContainsImage());
            using Image img = Clipboard.GetImage();
            Assert.Equal(new Size(4, 4), img.Size);
            Assert.Equal(Color.Blue.ToArgb(), ((Bitmap)img).GetPixel(2, 2).ToArgb());
        }

        [Fact]
        public void CfHtmlOffsetsPointAtTheFragment()
        {
            string fragment = "<p>café – résumé</p>";   // multi-byte: the offsets are UTF-8 bytes
            string cf = XplatUIWebGpu.ToCfHtml(fragment);
            Assert.Equal(fragment, XplatUIWebGpu.HtmlFragment(cf));
            Assert.Equal("<p>no header</p>", XplatUIWebGpu.HtmlFragment("<p>no header</p>"));
        }

        [Fact]
        public void FileListsAreUriListsBothWays()
        {
            string[] paths = { "/tmp/plain.txt", "/tmp/with space & sign#.txt" };
            byte[] list = MSInternalInteropAccess.Encode(paths);
            Assert.Contains("file:///tmp/with%20space", System.Text.Encoding.UTF8.GetString(list));
            Assert.Equal(paths, MSInternalInteropAccess.Decode(list));
        }
    }
}
