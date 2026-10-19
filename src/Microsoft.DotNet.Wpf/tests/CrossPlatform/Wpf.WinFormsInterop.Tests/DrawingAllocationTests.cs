// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// What drawing costs the garbage collector. A live resize of app allocated about 27 MB a step,
// two to three collections and 15 ms of pauses a step, and much of it was copies nothing needed: an
// array per row whenever GDI drew through a DC over a bitmap, a second window-sized array for every
// bitmap handed to the renderer, two copies of the pen's brush and a closure for every line drawn.
//

using System;
using System.Drawing;
using System.Drawing.Imaging;
using Xunit;
using SWF = System.Windows.Forms;

namespace Wpf.WinFormsInterop.Tests
{
    public sealed class DrawingAllocationTests
    {
        private const int Rows = 4000;

        private static long Allocated(Action a)
        {
            a();                                                  // warm: first-call costs are not the point
            long before = GC.GetAllocatedBytesForCurrentThread();
            a();
            return GC.GetAllocatedBytesForCurrentThread() - before;
        }

        /// <summary>A bitmap's DC (Graphics.GetHdc) is copied back on ReleaseHdc a row at a time; each
        /// row was a new array. 4000 rows of a 64-pixel bitmap: 1.1 MB of rows.</summary>
        [Fact]
        public void ReleasingABitmapsDc_DoesNotAllocateARowAtATime()
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "an HDC exists on Windows only");
            using var bmp = new Bitmap(64, Rows, PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(bmp);
            long bytes = Allocated(() => { IntPtr h = g.GetHdc(); g.ReleaseHdc(h); });
            Assert.True(bytes < 200_000, $"{bytes} bytes for a GetHdc / ReleaseHdc of a 64x{Rows} bitmap");
        }

        /// <summary>A Graphics over a DC copies the DC's pixels into its frame when it first draws; each
        /// row was a new array on top of the frame itself.</summary>
        [Fact]
        public void AGraphicsOverADc_CapturesWithoutAnArrayPerRow()
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "an HDC exists on Windows only");
            using var bmp = new Bitmap(64, Rows, PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(bmp);
            IntPtr h = g.GetHdc();
            try
            {
                long bytes = Allocated(() =>
                {
                    using var over = Graphics.FromHdc(h);
                    over.FillRectangle(Brushes.Red, 0, 0, 1, 1);
                });
                long frame = 64L * Rows * 4;
                Assert.True(bytes < frame + 200_000, $"{bytes} bytes to draw through a DC over a {frame}-byte frame");
            }
            finally { g.ReleaseHdc(h); }
        }

        private sealed class LinePainter : SWF.Control
        {
            public long PerLine = -1;
            protected override void OnPaint(SWF.PaintEventArgs e)
            {
                using var pen = new Pen(Color.Navy);
                const int Lines = 2000;
                for (int i = 0; i < 50; i++) e.Graphics.DrawLine(pen, 10, i, 190, i);     // warm
                long before = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < Lines; i++) e.Graphics.DrawLine(pen, 10, i % 190, 190, i % 190);
                PerLine = (GC.GetAllocatedBytesForCurrentThread() - before) / Lines;
            }
        }

        /// <summary>A line recorded for the GPU costs its own primitive and nothing more: no copies of
        /// its pen's brush (Pen.Brush hands out a copy; two were taken a line) and no closure over its
        /// arguments (the trace and print paths' lambdas, allocated on every call, traced or not).</summary>
        [Fact]
        public void ARecordedLine_CostsNoBrushCopiesAndNoClosure()
        {
            using var form = new SWF.Form { ClientSize = new Size(200, 200), StartPosition = SWF.FormStartPosition.Manual,
                Location = new Point(8000, 80), ShowInTaskbar = false, FormBorderStyle = SWF.FormBorderStyle.None };
            var painter = new LinePainter { Bounds = new Rectangle(0, 0, 200, 200) };
            form.Controls.Add(painter);
            form.Show();
            SWF.Application.DoEvents();
            painter.Refresh();
            SWF.Application.DoEvents();
            Console.WriteLine($"recorded DrawLine: {painter.PerLine} bytes per line");
            Assert.True(painter.PerLine >= 0, "the control did not paint");
            // 288 bytes a line now; the copies and the closure made it 568.
            Assert.True(painter.PerLine < 400, $"{painter.PerLine} bytes per recorded line");
        }

        private sealed class ImagePainter : SWF.Control
        {
            public long Bytes = -1;
            public Bitmap Image;
            protected override void OnPaint(SWF.PaintEventArgs e)
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                e.Graphics.DrawImageUnscaled(Image, 0, 0);
                Bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            }
        }

        /// <summary>A bitmap handed to the renderer is converted to RGBA once, into one array: it was
        /// read into a whole ARGB array first and that converted into a second.</summary>
        [Fact]
        public void ABitmapDrawnForTheGpu_IsConvertedIntoOneArray()
        {
            using var form = new SWF.Form { ClientSize = new Size(600, 600), StartPosition = SWF.FormStartPosition.Manual,
                Location = new Point(8000, 80), ShowInTaskbar = false, FormBorderStyle = SWF.FormBorderStyle.None };
            using var image = new Bitmap(512, 512, PixelFormat.Format32bppArgb);
            var painter = new ImagePainter { Bounds = new Rectangle(0, 0, 600, 600), Image = image };
            form.Controls.Add(painter);
            form.Show();
            SWF.Application.DoEvents();
            long rgba = 512L * 512 * 4;
            for (int i = 0; i < 2; i++)
            {
                image.SetPixel(i, 0, Color.FromArgb(255, i * 50, 0, 0));      // new pixels: no cached conversion
                painter.Refresh();
                SWF.Application.DoEvents();
            }
            Assert.True(painter.Bytes >= 0, "the control did not paint");
            Assert.True(painter.Bytes < rgba * 3 / 2, $"{painter.Bytes} bytes to draw a {rgba}-byte image");
        }

        private sealed class FramePainter : SWF.Control
        {
            protected override void OnPaint(SWF.PaintEventArgs e)
            {
                // a 2-pixel outline of the control, filled aliased (GDI+'s default), as DotNetBar's
                // RibbonForm fills its border: an outer and an inner figure under the alternate rule
                using var path = new System.Drawing.Drawing2D.GraphicsPath();
                path.AddRectangle(new Rectangle(0, 0, Width, Height));
                path.AddRectangle(new Rectangle(2, 2, Width - 4, Height - 4));
                using var brush = new SolidBrush(Color.SteelBlue);
                e.Graphics.FillPath(brush, path);
            }
        }

        /// <summary>An aliased path fill records a rectangle per run of identical rows, not one per row:
        /// a form's 600-row outline was 1200 one-row fills on every repaint, each of them damage on every
        /// step of a resize.</summary>
        [Fact]
        public void AnAliasedOutline_IsRecordedAsAFewRectangles_NotOnePerRow()
        {
            using var form = new SWF.Form { ClientSize = new Size(300, 600), StartPosition = SWF.FormStartPosition.Manual,
                Location = new Point(8000, 80), ShowInTaskbar = false, FormBorderStyle = SWF.FormBorderStyle.None };
            var painter = new FramePainter { Bounds = new Rectangle(0, 0, 300, 600) };
            form.Controls.Add(painter);
            form.Show();
            SWF.Application.DoEvents();
            painter.Refresh();
            SWF.Application.DoEvents();
            object scene = SWF.XplatUIWebGpu.GetInstance().GetWindowScene(painter.Handle);
            Assert.NotNull(scene);
            int fills = 0;
            void Walk(object v)
            {
                foreach (object p in (System.Collections.IEnumerable)v.GetType().GetProperty("Content")!.GetValue(v)!)
                    if (p.GetType().Name == "GeometryFill") fills++;
                foreach (object c in (System.Collections.IEnumerable)v.GetType().GetProperty("Children")!.GetValue(v)!) Walk(c);
            }
            Walk(scene);
            // top, bottom, and the two sides: four rectangles (it was 2 + 2 x 596 = 1196)
            Assert.True(fills <= 8, $"{fills} fills for a rectangular outline");
        }
    }
}
