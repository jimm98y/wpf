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
