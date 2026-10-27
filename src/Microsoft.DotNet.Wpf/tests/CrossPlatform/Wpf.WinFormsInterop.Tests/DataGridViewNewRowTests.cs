// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The new row of a grid that lets the user add rows. Making it current -- the cell a grid makes current
// when its window is created, or the user clicking it -- adds nothing; the user's first change in it
// does, as .NET has it: the row begun becomes a real row, a new row for the next record goes below it, and
// UserAddedRow is raised with that new row, the one begun being right above it. Ours raised UserAddedRow
// on entering the new row with nothing added, and a handler reading the row above (app's criteria
// list, Rows[e.Row.Index - 1]) failed with ArgumentOutOfRangeException out of the grid's WM_CREATE.
//

using System.Drawing;
using System.Windows.Forms;
using Xunit;

namespace Wpf.WinFormsInterop.Tests
{
    public class DataGridViewNewRowTests
    {
        private static (Form form, TabControl tabs, TabPage page, DataGridView grid) Make()
        {
            var form = new Form { ClientSize = new Size(400, 300) };
            var tabs = new TabControl { Dock = DockStyle.Fill };
            var first = new TabPage("First");
            var page = new TabPage("Grouping Range");
            var grid = new DataGridView { Dock = DockStyle.Fill, AllowUserToAddRows = true };
            var condition = new DataGridViewComboBoxColumn { HeaderText = "Alarm Condition" };
            condition.Items.AddRange("LO", "HI");
            grid.Columns.Add(condition);
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Caption" });
            page.Controls.Add(grid);
            tabs.TabPages.Add(first);
            tabs.TabPages.Add(page);
            form.Controls.Add(tabs);
            return (form, tabs, page, grid);
        }

        [Fact]
        public void EnteringTheNewRow_AddsNothing()
        {
            var (form, tabs, page, grid) = Make();
            using var _ = form;
            int added = 0;
            grid.UserAddedRow += (_, e) => { added++; _ = grid.Rows[e.Row.Index - 1]; };
            form.Show();
            tabs.SelectedTab = page;              // the grid's window is created here
            Application.DoEvents();

            Assert.Equal(0, added);
            Assert.Equal(1, grid.Rows.Count);
            Assert.True(grid.Rows[0].IsNewRow);
        }

        [Fact]
        public void TheFirstChangeInTheNewRow_AddsTheRowAboveANewOne()
        {
            var (form, tabs, page, grid) = Make();
            using var _ = form;
            DataGridViewRow? begun = null, next = null;
            grid.UserAddedRow += (_, e) => { next = e.Row; begun = grid.Rows[e.Row.Index - 1]; };
            form.Show();
            tabs.SelectedTab = page;
            Application.DoEvents();

            grid.CurrentCell = grid.Rows[0].Cells[1];
            Assert.True(grid.BeginEdit(true));
            Assert.Null(next);                    // beginning the edit is not a change
            grid.EditingControl!.Text = "High alarm";

            Assert.NotNull(next);
            Assert.True(next!.IsNewRow);
            Assert.Equal(2, grid.Rows.Count);
            Assert.Same(grid.Rows[0], begun);
            Assert.False(begun!.IsNewRow);
            Assert.True(grid.IsCurrentCellDirty);

            Assert.True(grid.EndEdit());
            Assert.Equal("High alarm", grid.Rows[0].Cells[1].Value);
            Assert.Equal(2, grid.Rows.Count);
        }

        [Fact]
        public void CancellingTheRowBegun_TakesItOutAgain()
        {
            var (form, tabs, page, grid) = Make();
            using var _ = form;
            form.Show();
            tabs.SelectedTab = page;
            Application.DoEvents();

            grid.CurrentCell = grid.Rows[0].Cells[1];
            grid.BeginEdit(true);
            grid.EditingControl!.Text = "x";
            Assert.Equal(2, grid.Rows.Count);

            grid.CancelEdit();
            Assert.Equal(1, grid.Rows.Count);
            Assert.True(grid.Rows[0].IsNewRow);
            Assert.False(grid.IsCurrentCellDirty);
        }
    }
}
