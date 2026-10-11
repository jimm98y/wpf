// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// A PropertyGrid holding a property of every built-in editor type, set against a stock one:
//  - a Categorized grid listed each category's properties alphabetically, where .NET keeps the order
//    the type declares them in (it sorts by name only when PropertySort says Alphabetical) -- our
//    GridItemCollection was a SortedList, so every grid was alphabetical whatever it was asked;
//  - a Cursor read "[Cursor:Hand]" (Cursor.ToString) where .NET's converter writes "Hand";
//  - a null Image showed an expander, with nothing to expand into.
//

using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;
using Xunit;

namespace Wpf.WinFormsInterop.Tests
{
    public class PropertyGridEntryTests
    {
        private sealed class Declared
        {
            [Category("A")] public int Zulu { get; set; } = 1;
            [Category("A")] public int Alpha { get; set; } = 2;
            [Category("A")] public Cursor Pointer { get; set; } = Cursors.Hand;
            [Category("A")] public Image Picture { get; set; }
        }

        private static GridItem Category(PropertyGrid grid)
        {
            GridItem root = grid.SelectedGridItem;
            while (root.Parent != null)
                root = root.Parent;
            return root.GridItems[0];
        }

        [Fact]
        public void ACategorizedGrid_KeepsTheDeclaredOrder_AndAnAlphabeticalOneSorts()
        {
            using var form = new Form();
            var grid = new PropertyGrid { PropertySort = PropertySort.Categorized, SelectedObject = new Declared() };
            form.Controls.Add(grid);
            form.CreateControl();

            GridItem a = Category(grid);
            Assert.Equal(new[] { "Zulu", "Alpha", "Pointer", "Picture" },
                         new[] { a.GridItems[0].Label, a.GridItems[1].Label, a.GridItems[2].Label, a.GridItems[3].Label });

            grid.PropertySort = PropertySort.CategorizedAlphabetical;
            grid.SelectedObject = new Declared();
            a = Category(grid);
            Assert.Equal(new[] { "Alpha", "Picture", "Pointer", "Zulu" },
                         new[] { a.GridItems[0].Label, a.GridItems[1].Label, a.GridItems[2].Label, a.GridItems[3].Label });
        }

        [Fact]
        public void AStandardCursor_IsWrittenAndReadByItsName()
        {
            var converter = TypeDescriptor.GetConverter(typeof(Cursor));
            Assert.Equal("Hand", converter.ConvertToString(Cursors.Hand));
            Assert.Same(Cursors.IBeam, converter.ConvertFromString("IBeam"));
        }

        [Fact]
        public void ANullValue_HasNothingToExpand()
        {
            using var form = new Form();
            var grid = new PropertyGrid { PropertySort = PropertySort.Categorized, SelectedObject = new Declared() };
            form.Controls.Add(grid);
            form.CreateControl();
            GridItem picture = Category(grid).GridItems["Picture"];
            Assert.False(picture.Expandable);
        }
    }
}
