// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// ImageList.Draw(g, x, y, index) puts the image down pixel for pixel at the list's ImageSize, as
// ImageList_Draw does, whatever resolution its bitmaps carry. A list built at 192 dpi (bitmaps that
// say 192, or a surface at 192) drew every image at the wrong size through DrawImage(image, x, y),
// which honours resolution:
// app's Preferences list showed 16-pixel icons where stock shows 32.
//

using System.Drawing;
using System.Windows.Forms;
using Xunit;

namespace Wpf.WinFormsInterop.Tests
{
    public class ImageListDrawTests
    {
        [Theory]
        [InlineData(96)]
        [InlineData(192)]
        public void Draw_IsPixelForPixel_AtTheImageSize(int targetDpi)
        {
            using var src = new Bitmap(32, 32);
            using (var g = Graphics.FromImage(src)) g.Clear(Color.Red);
            src.SetResolution(192, 192);
            using var list = new ImageList { ImageSize = new Size(32, 32), ColorDepth = ColorDepth.Depth32Bit };
            list.Images.Add(src);
            using var target = new Bitmap(64, 64);
            target.SetResolution(targetDpi, targetDpi);
            using (var g = Graphics.FromImage(target))
            {
                g.Clear(Color.White);
                list.Draw(g, 0, 0, 0);
            }
            Assert.Equal(Color.Red.ToArgb(), target.GetPixel(30, 30).ToArgb());
            Assert.Equal(Color.White.ToArgb(), target.GetPixel(33, 33).ToArgb());
        }
    }
}
