// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// DataGridViewColumnCollection's filtered queries -- GetFirstColumn, GetNextColumn and the rest --
// were stubs answering 0 and null, so a caller asking for the first visible column was told
// there was none. They follow the columns' display order and match on include/exclude states.
//

using System.Windows.Forms;
using Xunit;

namespace Wpf.WinFormsInterop.Tests
{
    public class DataGridViewColumnQueryTests
    {
        private static DataGridView Grid(out DataGridViewColumn a, out DataGridViewColumn b, out DataGridViewColumn c)
        {
            var grid = new DataGridView();
            a = new DataGridViewTextBoxColumn { Name = "a", Width = 40 };
            b = new DataGridViewTextBoxColumn { Name = "b", Width = 50, Visible = false };
            c = new DataGridViewTextBoxColumn { Name = "c", Width = 60, ReadOnly = true };
            grid.Columns.AddRange(a, b, c);
            return grid;
        }

        [Fact]
        public void FirstLastNextPrevious_FollowTheFilters()
        {
            using var grid = Grid(out var a, out var b, out var c);
            var cols = grid.Columns;
            Assert.Same(a, cols.GetFirstColumn(DataGridViewElementStates.Visible));
            Assert.Same(c, cols.GetLastColumn(DataGridViewElementStates.Visible, DataGridViewElementStates.None));
            Assert.Same(c, cols.GetNextColumn(a, DataGridViewElementStates.Visible, DataGridViewElementStates.None));
            Assert.Same(a, cols.GetPreviousColumn(c, DataGridViewElementStates.Visible, DataGridViewElementStates.None));
            Assert.Same(a, cols.GetFirstColumn(DataGridViewElementStates.Visible, DataGridViewElementStates.ReadOnly));
            Assert.Null(cols.GetNextColumn(c, DataGridViewElementStates.Visible, DataGridViewElementStates.None));
        }

        [Fact]
        public void CountAndWidth_CountOnlyMatchingColumns()
        {
            using var grid = Grid(out _, out _, out _);
            Assert.Equal(2, grid.Columns.GetColumnCount(DataGridViewElementStates.Visible));
            Assert.Equal(100, grid.Columns.GetColumnsWidth(DataGridViewElementStates.Visible));
            Assert.Equal(3, grid.Columns.GetColumnCount(DataGridViewElementStates.None));
        }

        [Fact]
        public void DisplayOrder_NotCollectionOrder()
        {
            using var grid = Grid(out var a, out _, out var c);
            c.DisplayIndex = 0;
            Assert.Same(c, grid.Columns.GetFirstColumn(DataGridViewElementStates.Visible));
            Assert.Same(a, grid.Columns.GetNextColumn(c, DataGridViewElementStates.Visible, DataGridViewElementStates.None));
        }
    }
}
