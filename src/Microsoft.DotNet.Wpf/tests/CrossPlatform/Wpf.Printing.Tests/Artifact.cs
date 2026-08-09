// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Writes a real PDF to disk so it can be opened by something that is not this repo's own reader.
//
// Off by default: it is a fixture for manual inspection, not an assertion. Set WPF_PDF_ARTIFACT to a
// path and run the suite to produce it.
//

using System;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Xps.Pdf;
using Xunit;

namespace Wpf.Printing.Tests
{
    public class Artifact
    {
        [Fact]
        public void WriteSamplePdf()
        {
            string path = Environment.GetEnvironmentVariable("WPF_PDF_ARTIFACT");
            Assert.SkipWhen(string.IsNullOrEmpty(path), "WPF_PDF_ARTIFACT is not set");

            var visual = new DrawingVisual();
            using (DrawingContext dc = visual.RenderOpen())
            {
                dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, 816, 1056));

                dc.DrawText(Text("WPF printing on macOS", "Arial", 32, Brushes.Black), new Point(64, 80));
                dc.DrawText(Text("こんにちは世界 — 日本語のテキスト", "Noto Sans CJK JP", 28, Brushes.Black),
                            new Point(64, 140));
                dc.DrawText(Text("The quick brown fox jumps over the lazy dog.", "Times New Roman", 18,
                                 Brushes.DimGray), new Point(64, 200));

                dc.DrawRectangle(Brushes.CornflowerBlue, new Pen(Brushes.Navy, 3), new Rect(64, 250, 300, 120));
                dc.DrawEllipse(Brushes.Orange, new Pen(Brushes.DarkRed, 2), new Point(560, 310), 90, 60);

                dc.PushClip(new EllipseGeometry(new Point(200, 500), 110, 110));
                dc.DrawRectangle(Brushes.SeaGreen, null, new Rect(64, 400, 400, 200));
                dc.Pop();

                var dashed = new Pen(Brushes.Purple, 4)
                {
                    DashStyle = new DashStyle(new DoubleCollection { 2, 1 }, 0),
                    StartLineCap = PenLineCap.Round,
                    EndLineCap = PenLineCap.Round,
                };
                dc.DrawLine(dashed, new Point(64, 660), new Point(700, 660));
            }

            using (var file = File.Create(path))
            using (var writer = new PdfDocumentWriter(file, leaveOpen: true))
            {
                writer.PageSize = new Size(816, 1056);
                writer.Write(visual);
            }

            Assert.True(new FileInfo(path).Length > 1000);
        }

        private static FormattedText Text(string text, string family, double size, Brush brush)
            => new FormattedText(text, CultureInfo.GetCultureInfo("en-US"), FlowDirection.LeftToRight,
                                 new Typeface(family), size, brush, 1.0);
    }
}
