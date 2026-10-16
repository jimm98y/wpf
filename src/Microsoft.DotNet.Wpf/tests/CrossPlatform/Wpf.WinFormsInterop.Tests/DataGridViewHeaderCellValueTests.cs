// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// A DataGridView header cell holds any object, as in .NET: its ValueType is object, its formatted
// value is the value's text. Mono's row header cell cast the value to string, so an app numbering its
// rows (app's column configurator: HeaderCell.Value = index) threw InvalidCastException out of
// a TreeView mouse-up. Expected values were recorded from stock .NET 10 WinForms.
//

using System;
using Xunit;
using SWF = System.Windows.Forms;

namespace Wpf.WinFormsInterop.Tests
{
    public class DataGridViewHeaderCellValueTests
    {
        private static SWF.DataGridView Grid()
        {
            var g = new SWF.DataGridView();
            g.Columns.Add("a", "A");
            g.Rows.Add("x");
            g.Rows.Add("y");
            return g;
        }

        [Fact]
        public void ARowHeaderCell_HoldsAnyObject()
        {
            using var g = Grid();
            var cell = g.Rows[0].HeaderCell;
            Assert.Equal(typeof(object), cell.ValueType);
            Assert.Equal(typeof(string), cell.FormattedValueType);
            Assert.Null(cell.Value);
            Assert.Equal("", cell.FormattedValue);
            cell.Value = 5;
            Assert.Equal(5, cell.Value);
            Assert.Equal("5", cell.FormattedValue);
            cell.Value = "s";
            Assert.Equal("s", cell.Value);
            Assert.Equal("s", cell.FormattedValue);
        }

        [Fact]
        public void AColumnHeaderCell_HoldsAnyObject()
        {
            using var g = Grid();
            var cell = g.Columns[0].HeaderCell;
            Assert.Equal(typeof(object), cell.ValueType);
            Assert.Equal(typeof(string), cell.FormattedValueType);
            Assert.Equal("A", cell.Value);
            Assert.Equal("A", cell.FormattedValue);
            cell.Value = 5;
            Assert.Equal(5, cell.Value);
            Assert.Equal("5", cell.FormattedValue);
        }

        [Fact]
        public void TheTopLeftAndAFreeRowHeaderCell_HoldAnyObject()
        {
            using var g = Grid();
            var tl = g.TopLeftHeaderCell;
            Assert.Equal(typeof(object), tl.ValueType);
            Assert.Equal(typeof(string), tl.FormattedValueType);
            tl.Value = 5;
            Assert.Equal(5, tl.Value);
            Assert.Equal("5", tl.FormattedValue);

            var free = new SWF.DataGridViewRowHeaderCell();
            Assert.Equal(typeof(object), free.ValueType);
            free.Value = 5;
            Assert.Equal(5, free.Value);
        }
    }
}
