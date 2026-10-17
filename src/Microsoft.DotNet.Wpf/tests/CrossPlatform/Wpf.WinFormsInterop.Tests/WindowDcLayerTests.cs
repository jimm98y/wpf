// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// A picture drawn on a window outside its paint (CreateGraphics -- what WpfMac's window DC does with
// GetDC/GetWindowDC drawing) goes over what the window shows, as on a screen, and replaces the pixels
// it covers. Each one was appended to the window's scene until the window next painted: DotNetBar
// redraws a dock panel's frame through GetWindowDC on every resize step, so app grew by 1.7 MB
// a step, 600 MB in three move/resize cycles. Pictures drawn over a picture are now one picture.
//

using System;
using System.Collections;
using System.Drawing;
using Xunit;
using SWF = System.Windows.Forms;

namespace Wpf.WinFormsInterop.Tests
{
    public class WindowDcLayerTests
    {
        private sealed class QuietForm : SWF.Form
        {
            protected override bool ShowWithoutActivation => true;
        }

        private static IList Children(object v) => (IList)v.GetType().GetProperty("Children")!.GetValue(v)!;

        private static void DrawOutsidePaint(IntPtr handle, Color c, int x, int y, int w, int h)
        {
            using var bmp = new Bitmap(w, h);
            using (var bg = Graphics.FromImage(bmp)) bg.Clear(c);
            using Graphics g = SWF.XplatUIWebGpu.GetInstance().GetHwndGraphics(handle);
            g.DrawImageUnscaled(bmp, x, y);
        }

        [Fact]
        public void PicturesDrawnOverAPicture_AreOnePicture()
        {
            using var form = new QuietForm { ClientSize = new Size(200, 120), StartPosition = SWF.FormStartPosition.Manual,
                Location = new Point(8000, 80), ShowInTaskbar = false, FormBorderStyle = SWF.FormBorderStyle.None };
            var panel = new SWF.Panel { Bounds = new Rectangle(5, 5, 150, 90), BackColor = Color.White };
            form.Controls.Add(panel);
            form.Show();
            SWF.Application.DoEvents();
            panel.Refresh();
            SWF.Application.DoEvents();
            IntPtr h = panel.Handle;
            var driver = SWF.XplatUIWebGpu.GetInstance();
            int before = Children(driver.GetWindowScene(h)).Count;

            for (int i = 0; i < 20; i++)
                DrawOutsidePaint(h, i % 2 == 0 ? Color.Red : Color.Blue, 10 + i, 10, 40, 30);

            object scene = driver.GetWindowScene(h);
            Assert.Equal(before + 1, Children(scene).Count);

            // The one picture is the union of all twenty, the last one drawn on top.
            object layer = Children(scene)[Children(scene).Count - 1]!;
            object fill = ((IList)layer.GetType().GetProperty("Content")!.GetValue(layer)!)[0]!;
            object brush = fill.GetType().GetProperty("Brush")!.GetValue(fill)!;
            int pw = (int)brush.GetType().GetProperty("PixelWidth")!.GetValue(brush)!;
            byte[] px = (byte[])brush.GetType().GetProperty("PixelsRgba")!.GetValue(brush)!;
            Assert.Equal(40 + 19, pw);
            // Column 0 is only the first (red) picture's; the last column only the last (blue) one's.
            Assert.Equal(255, px[0]);                       // R
            Assert.Equal(255, px[(pw - 1) * 4 + 2]);        // B
            Assert.Equal(0, px[(pw - 1) * 4]);              // no red there
        }
    }
}
