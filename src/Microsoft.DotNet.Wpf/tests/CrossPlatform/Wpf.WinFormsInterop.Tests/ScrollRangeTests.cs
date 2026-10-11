// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The units a scrolling control counts in, which is what sizes and places its thumb.
//
// Measured against stock controls with many items: our DataGridView counted its row headers into the
// horizontal range and guessed its pages, so both thumbs came out short; our ListView counted pixels
// where comctl32 counts COLUMNS in a list view and ROWS in a report view, so a List view's thumb came
// out a tenth long and a report view's sat a pixel off once scrolled; and a wheel notch moved a
// grid a row and a pixel, leaving its bar off the row boundary .NET keeps it on.
//

using System;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using Xunit;

namespace Wpf.WinFormsInterop.Tests
{
    public class ScrollRangeTests
    {
        private static T Field<T>(object o, string name)
            => (T)o.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(o)!;

        /// <summary>The bar's own visibility: Visible walks up to a form that is never shown here.</summary>
        private static bool Shown(Control c)
            => (bool)typeof(Control).GetField("is_visible", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(c)!;

        private static ScrollBar Prop(DataGridView g, string name)
            => (ScrollBar)typeof(DataGridView).GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(g)!;

        private static DataGridView Grid(Form f)
        {
            var grid = new DataGridView { Location = new Point(0, 0), Size = new Size(326, 130), AllowUserToAddRows = false, RowHeadersWidth = 24 };
            foreach (int w in new[] { 70, 50, 70, 56, 50 })
                grid.Columns.Add(new DataGridViewTextBoxColumn { Width = w });
            for (int i = 0; i < 12; i++)
                grid.Rows.Add("r" + i);
            f.Controls.Add(grid);
            f.CreateControl();
            grid.CreateControl();
            using var bmp = new Bitmap(grid.Width, grid.Height);
            grid.DrawToBitmap(bmp, new Rectangle(0, 0, grid.Width, grid.Height));   // the bars are laid out in paint
            return grid;
        }

        [Fact]
        public void AGridsRanges_AreItsScrollingBands_AndItsPagesTheDataArea()
        {
            using var f = new Form { ClientSize = new Size(400, 200) };
            DataGridView grid = Grid(f);
            ScrollBar h = Prop(grid, "HorizontalScrollBar"), v = Prop(grid, "VerticalScrollBar");
            int border = 1, rowHeight = grid.Rows[0].Height;

            Assert.True(Shown(h) && Shown(v));
            Assert.Equal(70 + 50 + 70 + 56 + 50, h.Maximum);                       // no row headers
            Assert.Equal(grid.ClientSize.Width - 2 * border - 24 - v.Width, h.LargeChange);
            Assert.Equal(12 * rowHeight, v.Maximum);                                // no column headers
            Assert.Equal(grid.ClientSize.Height - 2 * border - grid.ColumnHeadersHeight - h.Height, v.LargeChange);
        }

        [Fact]
        public void AGridScrolledByTheWheel_StaysOnARowBoundary()
        {
            using var f = new Form { ClientSize = new Size(400, 200) };
            DataGridView grid = Grid(f);
            ScrollBar v = Prop(grid, "VerticalScrollBar");
            typeof(DataGridView).GetMethod("OnMouseWheel", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(grid, new object[] { new MouseEventArgs(MouseButtons.None, 0, 10, 60, -120) });

            int row = grid.Rows[0].Height;
            Assert.True(v.Value > 0, "a notch scrolls");
            Assert.Equal(0, v.Value % row);                                        // on a row boundary
            Assert.Equal(SystemInformation.MouseWheelScrollLines * row, v.Value);  // the wheel's lines, in rows
        }

        [Fact]
        public void AListView_CountsColumns_AndAReportView_Rows()
        {
            using var f = new Form { ClientSize = new Size(400, 300) };
            var list = new ListView { Location = new Point(0, 0), Size = new Size(326, 60), View = View.List };
            for (int i = 1; i <= 20; i++) list.Items.Add("List " + i);
            var report = new ListView { Location = new Point(0, 80), Size = new Size(326, 120), View = View.Details };
            report.Columns.Add("Name", 120);
            for (int i = 1; i <= 15; i++) report.Items.Add("file" + i);
            f.Controls.Add(list); f.Controls.Add(report);
            f.CreateControl(); list.CreateControl(); report.CreateControl();

            ScrollBar h = Field<ScrollBar>(list, "h_scroll");
            Assert.True(Shown(h));
            int rows = Math.Max(1, (list.ClientSize.Height - h.Height) / list.Items[0].Bounds.Height);
            int columns = (20 + rows - 1) / rows;
            Assert.Equal(columns - 1, h.Maximum);
            Assert.Equal(h.Width / list.Items[rows].Bounds.X, h.LargeChange);         // columns wholly in view

            ScrollBar v = Field<ScrollBar>(report, "v_scroll");
            Assert.True(Shown(v));
            Assert.Equal(15 - 1, v.Maximum);
            Assert.Equal(1, v.SmallChange);
        }
    }
}
