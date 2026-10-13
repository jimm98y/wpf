// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// A shrinking DrawImage at the default (bilinear) interpolation. An image whose source rectangle is
// 420 pixels or more on a side is first averaged down to its device size and only then sampled, so it
// shrinks with a box filter; a smaller one is point-bilinear sampled. A clip narrows the source rows
// first, which moves where the averaging starts. Every expected value here was read off stock GDI+.
//

using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Xunit;

namespace Wpf.WinFormsInterop.Tests
{
    public class DrawImageShrinkTests
    {
        static Bitmap Column (int w, int h, int col)
        {
            var s = new Bitmap (w, h, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage (s)) g.Clear (Color.Black);
            for (int y = 0; y < h; y++) s.SetPixel (col, y, Color.White);
            return s;
        }

        static int[] Row (int w, int h, int col, int dw, int dh)
        {
            using var s = Column (w, h, col);
            using var d = new Bitmap (dw, dh, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage (d)) { g.Clear (Color.Transparent); g.DrawImage (s, 0, 0, dw, dh); }
            var r = new int [dw];
            for (int x = 0; x < dw; x++) r [x] = d.GetPixel (x, dh / 2).R;
            return r;
        }

        [Fact]
        public void SmallImage_IsSampled()
        {
            int[] r = Row (418, 4, 210, 209, 2);
            Assert.Equal (255, r [105]);
            Assert.Equal (0, r [104] + r [106]);
            r = Row (418, 4, 211, 300, 2);
            Assert.Equal (100, r [151]);
            Assert.Equal (55, r [152]);
        }

        [Fact]
        public void LargeImage_IsAveraged()
        {
            int[] r = Row (420, 4, 210, 210, 2);
            Assert.Equal (128, r [105]);
            r = Row (420, 4, 211, 300, 2);
            Assert.Equal (73, r [150]);
            Assert.Equal (109, r [151]);
            r = Row (420, 4, 211, 140, 2);
            Assert.Equal (85, r [70]);
        }

        [Fact]
        public void LargeImage_IsAveragedInBothAxes()
        {
            // 420 high and 4 wide: the tall side alone makes it large.
            using var s = new Bitmap (4, 420, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage (s)) g.Clear (Color.Black);
            for (int x = 0; x < 4; x++) s.SetPixel (x, 211, Color.White);
            using var d = new Bitmap (2, 210, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage (d)) { g.Clear (Color.Transparent); g.DrawImage (s, 0, 0, 2, 210); }
            Assert.Equal (128, d.GetPixel (1, 105).R);
        }

        [Fact]
        public void ClippedDraw_StartsTheAveragingAtTheClip()
        {
            using var n = Noise (997, 613, 7);
            using var d = new Bitmap (500, 300, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage (d)) {
                g.Clear (Color.White);
                g.SetClip (new Rectangle (13, 101, 300, 77));
                g.DrawImage (n, 0, 0, 500, 300);
            }
            Assert.Equal (Color.FromArgb (255, 189, 183, 207).ToArgb (), d.GetPixel (17, 101).ToArgb ());
        }

        static Bitmap Noise (int w, int h, int seed)
        {
            // The order of Color.FromArgb(rnd a, r, g, b) per pixel, row by row.
            var rnd = new Random (seed);
            var b = new Bitmap (w, h, PixelFormat.Format32bppArgb);
            var data = b.LockBits (new Rectangle (0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            var row = new byte [w * 4];
            for (int y = 0; y < h; y++) {
                for (int x = 0; x < w; x++) {
                    int a = rnd.Next (256), r = rnd.Next (256), g = rnd.Next (256), bl = rnd.Next (256);
                    row [x * 4] = (byte) bl; row [x * 4 + 1] = (byte) g; row [x * 4 + 2] = (byte) r; row [x * 4 + 3] = (byte) a;
                }
                Marshal.Copy (row, 0, data.Scan0 + y * data.Stride, row.Length);
            }
            b.UnlockBits (data);
            return b;
        }
    }
}
