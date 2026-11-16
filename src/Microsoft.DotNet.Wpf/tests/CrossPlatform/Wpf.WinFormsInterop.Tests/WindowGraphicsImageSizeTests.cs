// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// DrawImage at a point draws an image at its physical size in the Graphics' resolution (GDI+):
// its pixels times DpiX over the image's own resolution. A window's Graphics -- a recording, with
// no image behind it -- reports the display's DPI, but sized images as if it were 96: at 192 dpi a
// control that double-buffers its paint into a new Bitmap (which takes the display's resolution)
// and draws it back at (0, 0) showed at half its size, its text and all.
//

using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.WebGpuBackend;
using Xunit;

namespace Wpf.WinFormsInterop.Tests
{
    [Collection(ProcessDpiStateCollection.Name)]
    public class WindowGraphicsImageSizeTests
    {
        [Fact]
        public void AtTwiceTheDpiADisplayResolutionBitmapGoesDownPixelForPixel()
        {
            float was = DisplayDpi.X;
            DisplayDpi.Set(192);
            try
            {
                using var bmp = new Bitmap(40, 30);
                Assert.Equal(192f, bmp.HorizontalResolution);
                Assert.Equal("0,0,40,30", ImageRect(g => g.DrawImage(bmp, 0, 0)));
                Assert.Equal("5,6,40,30", ImageRect(g => g.DrawImage(bmp, 5f, 6f)));

                // a 96-dpi image is twice its pixels there, as GDI+ draws it
                using var icon = new Bitmap(16, 16);
                icon.SetResolution(96, 96);
                Assert.Equal("0,0,32,32", ImageRect(g => g.DrawImage(icon, 0, 0)));
            }
            finally
            {
                DisplayDpi.Set(was);
            }
        }

        [Fact]
        public void AtTwiceTheDpiAPointSizedStringIsLaidOutAsOnABitmap()
        {
            float was = DisplayDpi.X;
            DisplayDpi.Set(192);
            try
            {
                using var font = new Font("Microsoft Sans Serif", 8.25f, FontStyle.Regular, GraphicsUnit.Point);
                using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                var box = new SizeF(63, 19);
                using var bmp = new Bitmap(10, 10);
                using var onBitmap = Graphics.FromImage(bmp);
                onBitmap.MeasureString("&Cancel", font, box, format, out int bChars, out int bLines);
                Graphics rec = GpuRaster.NewRecording();
                rec.MeasureString("&Cancel", font, box, format, out int rChars, out int rLines);
                GpuRaster.EndScene(rec);
                Assert.Equal((bChars, bLines), (rChars, rLines));
                Assert.Equal(1, rLines);
            }
            finally
            {
                DisplayDpi.Set(was);
            }
        }

        [Fact]
        public void APngThatStatesNoResolutionHasTheDesktopsAsInGdiPlus()
        {
            // a PNG written with a resolution, and the same pixels with its pHYs chunk taken out
            byte[] withPhys;
            using (var src = new Bitmap(16, 16))
            {
                src.SetResolution(72, 72);
                using var ms = new System.IO.MemoryStream();
                src.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                withPhys = ms.ToArray();
            }
            byte[] noPhys = StripChunk(withPhys, "pHYs");
            Assert.True(noPhys.Length < withPhys.Length, "the chunk was there to take out");

            float was = DisplayDpi.X;
            DisplayDpi.Set(192);
            try
            {
                using (var stated = new Bitmap(new System.IO.MemoryStream(withPhys)))
                    Assert.Equal(72f, stated.HorizontalResolution, 0);
                using (var unstated = new Bitmap(new System.IO.MemoryStream(noPhys)))
                {
                    Assert.Equal(192f, unstated.HorizontalResolution);
                    // ...so drawn at its own size on a 192-dpi window it goes down pixel for pixel
                    Assert.Equal("0,0,16,16", ImageRect(g => g.DrawImageUnscaled(unstated, 0, 0)));
                }
            }
            finally
            {
                DisplayDpi.Set(was);
            }
        }

        private static byte[] StripChunk(byte[] png, string type)
        {
            var o = new System.IO.MemoryStream();
            o.Write(png, 0, 8);
            int i = 8;
            while (i + 8 <= png.Length)
            {
                int len = (png[i] << 24) | (png[i + 1] << 16) | (png[i + 2] << 8) | png[i + 3];
                string t = System.Text.Encoding.ASCII.GetString(png, i + 4, 4);
                if (t != type) o.Write(png, i, 12 + len);
                i += 12 + len;
            }
            return o.ToArray();
        }

        [Fact]
        public void IntoABitmapTheBitmapsResolutionDecides()
        {
            using var target = new Bitmap(100, 100);
            target.SetResolution(96, 96);
            using var src = new Bitmap(40, 30);
            src.SetResolution(192, 192);
            using var g = Graphics.FromImage(target);
            Assert.Equal(96f, g.DpiX);
        }

        private static string ImageRect(Action<Graphics> draw)
        {
            Graphics g = GpuRaster.NewRecording();
            draw(g);
            object scene = GpuRaster.EndScene(g);
            var rows = new List<string>();
            Collect(scene, rows);
            Assert.Single(rows);
            return rows[0];
        }

        private static void Collect(object visual, List<string> rows)
        {
            foreach (object p in (IEnumerable)visual.GetType().GetProperty("Content").GetValue(visual))
            {
                object geo = p.GetType().GetProperty("Geometry")?.GetValue(p);
                object rect = geo?.GetType().GetProperty("Rect")?.GetValue(geo) ?? geo?.GetType().GetField("Rect")?.GetValue(geo);
                if (rect == null) continue;
                float F(string n) => Convert.ToSingle(rect.GetType().GetProperty(n)?.GetValue(rect) ?? rect.GetType().GetField(n).GetValue(rect));
                rows.Add($"{F("X")},{F("Y")},{F("Width")},{F("Height")}");
            }
            foreach (object child in (IEnumerable)visual.GetType().GetProperty("Children").GetValue(visual))
                Collect(child, rows);
        }
    }
}
