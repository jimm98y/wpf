// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// A standard cursor DRAWN, as the cursor editor's list draws each one: the driver's
// DefineStdCursorBitmap was a generated stub returning nothing, so every cursor drew as an empty
// box. On Windows it is now the system's own cursor, read out of its bitmaps (GDI+'s FromHicon
// refuses a cursor handle).
//

using System;
using System.Drawing;
using System.Windows.Forms;
using Xunit;

namespace Wpf.WinFormsInterop.Tests
{
    public class CursorImageTests
    {
        private static int Inked(Cursor cursor)
        {
            using var bmp = new Bitmap(40, 40);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Magenta);
                cursor.DrawStretched(g, new Rectangle(0, 0, 32, 32));
            }
            int changed = 0;
            for (int y = 0; y < 40; y++)
                for (int x = 0; x < 40; x++)
                    if (bmp.GetPixel(x, y).ToArgb() != Color.Magenta.ToArgb())
                        changed++;
            return changed;
        }

        [Fact]
        public void AStandardCursor_DrawsTheSystemsPicture()
        {
            if (!OperatingSystem.IsWindows())
                return;   // no system cursor to read elsewhere
            foreach (Cursor c in new[] { Cursors.Arrow, Cursors.Hand, Cursors.IBeam, Cursors.WaitCursor, Cursors.SizeAll, Cursors.SizeNESW, Cursors.SizeNWSE })
                Assert.True(Inked(c) > 10, $"{c} drew nothing");
        }

        [Fact]
        public void TheCursorSize_IsTheSystemsNotZero()
        {
            Assert.Equal(new Size(32, 32), SystemInformation.CursorSize);
            Assert.Equal(new Size(32, 32), Cursors.Default.Size);
        }
    }
}
