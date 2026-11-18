// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// A grid's first displayed row and column, and its horizontal offset, as Windows' DataGridView has them: read,
// they are where the grid is scrolled to; set, they scroll it -- the bars move, the rows and columns drawn are the
// ones asked for, Scroll is raised -- and near the end the grid goes as far as it can. A row or column that cannot
// be first (out of range, hidden) is refused. Ours only stored the value, so setting it did nothing and reading it
// answered what had been stored rather than what was shown.
//

using System;
using System.Drawing;
using Xunit;
using SWF = System.Windows.Forms;

namespace Wpf.WinFormsInterop.Tests
{
    public class DataGridViewFirstDisplayedTests
    {
        private static (SWF.Form form, SWF.DataGridView grid) Make(int rows, int columns)
        {
            var form = new SWF.Form
            {
                ClientSize = new Size(400, 250), StartPosition = SWF.FormStartPosition.Manual,
                Location = new Point(-3300, 1500), ShowInTaskbar = false,
            };
            var grid = new SWF.DataGridView { Bounds = new Rectangle(10, 10, 360, 200), AllowUserToAddRows = false };
            for (int c = 0; c < columns; c++) grid.Columns.Add("c" + c, "C" + c);
            foreach (SWF.DataGridViewColumn column in grid.Columns) column.Width = 100;
            for (int r = 0; r < rows; r++)
            {
                var values = new object[columns];
                for (int c = 0; c < columns; c++) values[c] = $"r{r}c{c}";
                grid.Rows.Add(values);
            }
            form.Controls.Add(grid);
            form.Show();
            SWF.Application.DoEvents();
            return (form, grid);
        }

        [Fact]
        public void SettingTheFirstDisplayedRow_ScrollsTheGrid()
        {
            var (form, grid) = Make(100, 3);
            using var _ = form;
            Assert.Equal(0, grid.FirstDisplayedScrollingRowIndex);
            int top = grid.GetRowDisplayRectangle(0, false).Y;
            int scrolls = 0;
            grid.Scroll += (_, e) => { if (e.ScrollOrientation == SWF.ScrollOrientation.VerticalScroll) scrolls++; };

            grid.FirstDisplayedScrollingRowIndex = 10;

            Assert.Equal(10, grid.FirstDisplayedScrollingRowIndex);
            Assert.True(scrolls > 0, "Scroll is raised");
            // the rows above it are scrolled off: the offset and the bar are their height
            int above = 0;
            for (int i = 0; i < 10; i++) above += grid.Rows[i].Height;
            Assert.Equal(above, grid.VerticalScrollingOffset);
            Assert.Equal(above, grid.verticalScrollBar.Value);
            // and it is drawn where the first row was
            Assert.Equal(top, grid.GetRowDisplayRectangle(10, false).Y);

            grid.FirstDisplayedScrollingRowIndex = 0;
            Assert.Equal(0, grid.FirstDisplayedScrollingRowIndex);
            Assert.Equal(0, grid.VerticalScrollingOffset);
            Assert.Equal(0, grid.verticalScrollBar.Value);
        }

        [Fact]
        public void TheLastRow_ScrollsAsFarAsTheGridGoes()
        {
            var (form, grid) = Make(100, 3);
            using var _ = form;
            grid.FirstDisplayedScrollingRowIndex = 99;
            int first = grid.FirstDisplayedScrollingRowIndex;
            Assert.InRange(first, 80, 99);
            // the last row is on the screen, and the bar is at its end
            Assert.Equal(100, first + grid.DisplayedRowCount(true));
            Assert.True(grid.VerticalScrollingOffset > 0);
        }

        [Fact]
        public void ARowThatCannotBeFirst_IsRefused()
        {
            var (form, grid) = Make(20, 3);
            using var _ = form;
            Assert.Throws<ArgumentOutOfRangeException>(() => grid.FirstDisplayedScrollingRowIndex = -1);
            Assert.Throws<ArgumentOutOfRangeException>(() => grid.FirstDisplayedScrollingRowIndex = 20);
            grid.Rows[5].Visible = false;
            Assert.Throws<InvalidOperationException>(() => grid.FirstDisplayedScrollingRowIndex = 5);
            Assert.Equal(0, grid.FirstDisplayedScrollingRowIndex);
        }

        [Fact]
        public void AGridWithNoRows_HasNoFirstDisplayedRow()
        {
            var (form, grid) = Make(0, 3);
            using var _ = form;
            Assert.Equal(-1, grid.FirstDisplayedScrollingRowIndex);
        }

        [Fact]
        public void SettingTheFirstDisplayedColumn_ScrollsTheGrid()
        {
            var (form, grid) = Make(5, 20);
            using var _ = form;
            Assert.Equal(0, grid.FirstDisplayedScrollingColumnIndex);
            int scrolls = 0;
            grid.Scroll += (_, e) => { if (e.ScrollOrientation == SWF.ScrollOrientation.HorizontalScroll) scrolls++; };

            grid.FirstDisplayedScrollingColumnIndex = 5;

            Assert.Equal(5, grid.FirstDisplayedScrollingColumnIndex);
            Assert.True(scrolls > 0, "Scroll is raised");
            Assert.Equal(500, grid.HorizontalScrollingOffset);
            Assert.Equal(500, grid.horizontalScrollBar.Value);
            Assert.Equal(0, grid.FirstDisplayedScrollingColumnHiddenWidth);

            Assert.Throws<ArgumentOutOfRangeException>(() => grid.FirstDisplayedScrollingColumnIndex = 20);
            grid.Columns[3].Visible = false;
            Assert.Throws<InvalidOperationException>(() => grid.FirstDisplayedScrollingColumnIndex = 3);
        }

        [Fact]
        public void SettingTheHorizontalOffset_ScrollsTheGrid()
        {
            var (form, grid) = Make(5, 20);
            using var _ = form;
            grid.HorizontalScrollingOffset = 550;
            Assert.Equal(550, grid.HorizontalScrollingOffset);
            Assert.Equal(550, grid.horizontalScrollBar.Value);
            // part way into the sixth column: it is the first shown, half of it scrolled off
            Assert.Equal(5, grid.FirstDisplayedScrollingColumnIndex);
            Assert.Equal(50, grid.FirstDisplayedScrollingColumnHiddenWidth);

            // past the end it goes as far as the grid scrolls
            grid.HorizontalScrollingOffset = 100000;
            Assert.InRange(grid.HorizontalScrollingOffset, 1, 2000 - 1);
            Assert.Equal(grid.HorizontalScrollingOffset, grid.horizontalScrollBar.Value);

            grid.HorizontalScrollingOffset = 0;
            Assert.Equal(0, grid.FirstDisplayedScrollingColumnIndex);
            Assert.Throws<ArgumentOutOfRangeException>(() => grid.HorizontalScrollingOffset = -1);
        }
    }
}
