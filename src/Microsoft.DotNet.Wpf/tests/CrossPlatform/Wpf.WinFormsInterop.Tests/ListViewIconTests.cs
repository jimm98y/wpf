// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// A list view item's small icon is drawn whole: the label's background used to be filled from a
// pixel before the icon's end (where the text is seated), so every icon in a Details list lost its
// right-hand column -- the Preferences list's rectangle icon looked cropped.
//

using System.Drawing;
using System.Drawing.Imaging;
using Xunit;
using SWF = System.Windows.Forms;

namespace Wpf.WinFormsInterop.Tests
{
    public class ListViewIconTests
    {
        [Theory]
        [InlineData(SWF.View.Details)]
        [InlineData(SWF.View.List)]
        [InlineData(SWF.View.SmallIcon)]
        public void SmallIcon_RightEdgeColumnIsDrawn(SWF.View view)
        {
            using var icon = new Bitmap(16, 16, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(icon)) g.Clear(Color.Red);
            using var images = new SWF.ImageList { ImageSize = new Size(16, 16), ColorDepth = SWF.ColorDepth.Depth32Bit };
            images.Images.Add(icon);
            using var form = new SWF.Form { ClientSize = new Size(300, 120), ShowInTaskbar = false, StartPosition = SWF.FormStartPosition.Manual, Location = new Point(40, 40) };
            var lv = new SWF.ListView { View = view, SmallImageList = images, Bounds = new Rectangle(0, 0, 300, 120), BorderStyle = SWF.BorderStyle.None };
            if (view == SWF.View.Details) lv.Columns.Add("Name", 200);
            lv.Items.Add(new SWF.ListViewItem("New Rectangle Default Settings", 0));
            form.Controls.Add(lv);
            form.Show();
            SWF.Application.DoEvents();
            Rectangle r = lv.Items[0].GetBounds(SWF.ItemBoundsPortion.Icon);
            using var shot = new Bitmap(lv.Width, lv.Height);
            // The item pane as the theme paints it (DrawToBitmap leaves the pane, a child window, out).
            object theme = typeof(SWF.Control).Assembly.GetType("System.Windows.Forms.ThemeEngine")!
                .GetProperty("Current", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)!.GetValue(null)!;
            using (var g = Graphics.FromImage(shot))
            {
                g.Clear(Color.White);
                theme.GetType().GetMethod("DrawListViewItems")!.Invoke(theme, new object[] { g, new Rectangle(0, 0, lv.Width, lv.Height), lv });
            }
            int y = r.Top + r.Height / 2;
            // The icon's 16 columns from where it is drawn: all red, the last one included.
            int left = -1;
            for (int x = r.Left; x < r.Right + 4 && left < 0; x++) if (shot.GetPixel(x, y).ToArgb() == Color.Red.ToArgb()) left = x;
            Assert.True(left >= 0, $"no icon at row {y} of {r}");
            Assert.Equal(Color.Red.ToArgb(), shot.GetPixel(left + 15, y).ToArgb());
        }
    }
}
