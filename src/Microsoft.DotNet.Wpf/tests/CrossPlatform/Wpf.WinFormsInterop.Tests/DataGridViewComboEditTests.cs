// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// A DataGridViewComboBoxCell put into edit mode showed an EMPTY combo box: stock's editing control
// holds the cell's value, selected ("Red" on a highlighted field); ours had no selection at all.
//

using System.Drawing;
using System.Windows.Forms;
using Xunit;

namespace Wpf.WinFormsInterop.Tests
{
    public class DataGridViewComboEditTests
    {
        [Fact]
        public void EditingACombo_SelectsTheCellsValue()
        {
            using var form = new Form { ClientSize = new Size(400, 200) };
            var grid = new DataGridView { Dock = DockStyle.Fill, AllowUserToAddRows = false };
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Text" });
            var combo = new DataGridViewComboBoxColumn { HeaderText = "Combo" };
            combo.Items.AddRange("Red", "Green");
            grid.Columns.Add(combo);
            grid.Rows.Add("one", "Red");
            grid.Rows.Add("two", "Green");
            form.Controls.Add(grid);
            form.CreateControl();
            grid.CreateControl();

            grid.CurrentCell = grid.Rows[0].Cells[1];
            bool began = grid.BeginEdit(true);
            Assert.True(began);
            var editor = Assert.IsAssignableFrom<ComboBox>(grid.EditingControl);
            Assert.Equal(2, editor.Items.Count);
            Assert.Equal("Red", editor.SelectedItem);
            Assert.Equal("Red", editor.Text);

            grid.EndEdit();
            grid.CurrentCell = grid.Rows[1].Cells[1];
            Assert.True(grid.BeginEdit(true));
            editor = Assert.IsAssignableFrom<ComboBox>(grid.EditingControl);
            Assert.Equal("Green", editor.SelectedItem);
        }

        private static void Click(DataGridView grid, int col, int row, int x, int y)
        {
            var e = new DataGridViewCellMouseEventArgs(col, row, x, y, new MouseEventArgs(MouseButtons.Left, 1, 0, 0, 0));
            typeof(DataGridView).GetMethod("OnCellMouseClick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(grid, new object[] { e });
        }

        // .NET's DataGridViewComboBoxCell.OnMouseClick: the click that makes the cell current only
        // selects it; the next one begins the edit, and drops the list only on the drop-down button.
        [Fact]
        public void ASecondClickOnTheCurrentComboCell_BeginsTheEdit()
        {
            using var form = new Form { ClientSize = new Size(400, 200) };
            var grid = new DataGridView { Dock = DockStyle.Fill, AllowUserToAddRows = false };
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Text" });
            var combo = new DataGridViewComboBoxColumn { HeaderText = "Combo", Width = 70 };
            combo.Items.AddRange("Red", "Green");
            grid.Columns.Add(combo);
            grid.Rows.Add("one", "Red");
            form.Controls.Add(grid);
            form.CreateControl();
            grid.CreateControl();

            grid.CurrentCell = grid.Rows[0].Cells[1];
            // entered through the mouse, as a click on it enters it
            typeof(DataGridViewCell).GetMethod("OnEnterInternal", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(grid.CurrentCell, new object[] { 0, true });

            Click(grid, 1, 0, 10, 8);
            Assert.False(grid.IsCurrentCellInEditMode, "the click that made the cell current only selects it");

            Click(grid, 1, 0, 10, 8);
            Assert.True(grid.IsCurrentCellInEditMode, "the next click begins the edit");
            var editor = Assert.IsAssignableFrom<ComboBox>(grid.EditingControl);
            Assert.False(editor.DroppedDown, "off the drop-down button the list stays up");
        }
    }
}
