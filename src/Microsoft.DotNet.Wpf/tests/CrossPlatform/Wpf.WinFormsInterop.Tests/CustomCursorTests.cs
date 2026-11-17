// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// An application's own cursor, read from a .cur stream, is shown with its picture and its hotspot, as on
// Windows: it fell back to the arrow (DefineCursor made nothing, and the cursor's shape had no id). The
// picture is the XOR bitmap made transparent by the AND mask, or by its own alpha in a 32-bit cursor.
//

using System;
using System.Drawing;
using System.IO;
using Xunit;
using SWF = System.Windows.Forms;

namespace Wpf.WinFormsInterop.Tests
{
    public class CustomCursorTests
    {
        /// <summary>A 32 x 32 .cur: <paramref name="bits"/> per pixel (32 with alpha, or 1), the hotspot (5, 7).
        /// <paramref name="pixel"/> says, top-down, each pixel's colour (and for 1 bpp its AND bit).</summary>
        private static MemoryStream Cur(int bits, Func<int, int, (uint Argb, bool And)> pixel)
        {
            const int w = 32, h = 32;
            int xorStride = bits == 32 ? w * 4 : ((w + 31) / 32) * 4, andStride = ((w + 31) / 32) * 4;
            int colours = bits == 1 ? 2 : 0;
            int imageSize = 40 + colours * 4 + xorStride * h + andStride * h;
            var ms = new MemoryStream();
            var b = new BinaryWriter(ms);
            b.Write((short)0); b.Write((short)2); b.Write((short)1);                         // ICONDIR: a cursor, one image
            b.Write((byte)w); b.Write((byte)h); b.Write((byte)colours); b.Write((byte)0);
            b.Write((short)5); b.Write((short)7);                                            // the hotspot
            b.Write(imageSize); b.Write(22);
            b.Write(40); b.Write(w); b.Write(h * 2); b.Write((short)1); b.Write((short)bits);
            b.Write(0); b.Write(0); b.Write(0); b.Write(0); b.Write(colours); b.Write(0);
            if (bits == 1) { b.Write(0x00000000); b.Write(0x00FFFFFF); }                    // black, white
            for (int y = h - 1; y >= 0; y--)                                                  // XOR, bottom-up
            {
                if (bits == 32) for (int x = 0; x < w; x++) b.Write(pixel(x, y).Argb);
                else
                {
                    var row = new byte[xorStride];
                    for (int x = 0; x < w; x++) if ((pixel(x, y).Argb & 0xFFFFFF) != 0) row[x / 8] |= (byte)(0x80 >> (x & 7));
                    b.Write(row);
                }
            }
            for (int y = h - 1; y >= 0; y--)                                                  // AND, bottom-up
            {
                var row = new byte[andStride];
                for (int x = 0; x < w; x++) if (pixel(x, y).And) row[x / 8] |= (byte)(0x80 >> (x & 7));
                b.Write(row);
            }
            ms.Position = 0;
            return ms;
        }

        private static (int Id, Bitmap Image, Point Hot) Shown(SWF.Cursor cursor, int y)
        {
            SWF.XplatUIWebGpu driver = SWF.XplatUIWebGpu.GetInstance();
            using var form = new SWF.Form { StartPosition = SWF.FormStartPosition.Manual, Location = new Point(9500, y), ClientSize = new Size(200, 100), ShowInTaskbar = false };
            var panel = new SWF.Panel { Bounds = new Rectangle(10, 10, 150, 70), Cursor = cursor };
            form.Controls.Add(panel);
            form.Show();
            SWF.Application.DoEvents();
            Point at = panel.PointToScreen(new Point(20, 20));
            driver.InjectMouseMove(at.X, at.Y, false);
            int id = driver.GetActiveCursor();
            Assert.True(SWF.XplatUIWebGpu.TryGetCustomCursor(id, out Bitmap image, out Point hot), $"no picture for cursor {id}");
            return (id, image, hot);
        }

        [Fact]
        public void A32BitCursor_IsShownWithItsAlphaAndHotspot()
        {
            using var stream = Cur(32, (x, y) => (x < 4 && y < 4 ? 0xFFFF0000u : 0x00000000u, false));
            var cursor = new SWF.Cursor(stream);
            Assert.Equal(new Point(5, 7), cursor.HotSpot);

            var (id, image, hot) = Shown(cursor, 3700);
            Assert.True(id >= SWF.XplatUIWebGpu.FirstCustomCursor);
            Assert.Equal(new Point(5, 7), hot);
            Assert.Equal(Color.FromArgb(255, 255, 0, 0).ToArgb(), image.GetPixel(1, 1).ToArgb());
            Assert.Equal(0, image.GetPixel(20, 20).A);
        }

        [Fact]
        public void AMonochromeCursor_IsTransparentWhereItsMaskSays()
        {
            // a white square on black, with the rest of it the screen (AND 1, XOR 0)
            using var stream = Cur(1, (x, y) => x < 8 && y < 8 ? (0xFFFFFFu, false) : x < 16 && y < 16 ? (0u, false) : (0u, true));
            var (_, image, _) = Shown(new SWF.Cursor(stream), 4100);
            Assert.Equal(Color.White.ToArgb(), image.GetPixel(2, 2).ToArgb());
            Assert.Equal(Color.Black.ToArgb(), image.GetPixel(12, 12).ToArgb());
            Assert.Equal(0, image.GetPixel(25, 25).A);
        }
    }
}
