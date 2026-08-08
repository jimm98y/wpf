// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The block types a FlowDocument is built from, and whether their content reaches the page.
//
// The port swapped the native PTS engine for ManagedFlowLayout, which modelled Paragraph, Section
// and List and silently returned "no content" for everything else. Silently is the problem: a Table
// did not throw, it simply drew nothing, so a document lost entire chunks with no diagnostic
// anywhere. These tests exist so a block type that is not modelled fails a test instead.
//

using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using Xunit;

namespace Wpf.Document.Tests
{
    public class BlockLayoutTests
    {
        [Fact]
        public void ParagraphTextReachesThePage()
        {
            var doc = new FlowDocument();
            doc.Blocks.Add(DocumentHarness.Para("the quick brown fox"));

            PageContent page = DocumentHarness.Paginate(doc);

            Assert.True(page.Contains("quick brown fox"), $"got {page}");
        }

        [Fact]
        public void ParagraphsStackDownThePage()
        {
            var doc = new FlowDocument();
            doc.Blocks.Add(DocumentHarness.Para("first block"));
            doc.Blocks.Add(DocumentHarness.Para("second block"));

            PageContent page = DocumentHarness.Paginate(doc);

            Rect? first = page.Find("first");
            Rect? second = page.Find("second");
            Assert.True(first.HasValue && second.HasValue, $"got {page}");
            Assert.True(second.Value.Top > first.Value.Top,
                $"second block should be below the first ({second.Value.Top} vs {first.Value.Top})");
        }

        [Fact]
        public void SectionContentReachesThePage()
        {
            var section = new Section();
            section.Blocks.Add(DocumentHarness.Para("inside a section"));

            var doc = new FlowDocument();
            doc.Blocks.Add(section);

            PageContent page = DocumentHarness.Paginate(doc);

            Assert.True(page.Contains("inside a section"), $"got {page}");
        }

        [Fact]
        public void ListItemsReachThePageAndAreMarked()
        {
            var list = new List();
            list.ListItems.Add(new ListItem(DocumentHarness.Para("alpha item")));
            list.ListItems.Add(new ListItem(DocumentHarness.Para("beta item")));

            var doc = new FlowDocument();
            doc.Blocks.Add(list);

            PageContent page = DocumentHarness.Paginate(doc);

            Assert.True(page.Contains("alpha item") && page.Contains("beta item"), $"got {page}");

            // The marker is drawn as its own run; the item text is indented past it.
            Rect? alpha = page.Find("alpha item");
            Assert.True(alpha.HasValue && alpha.Value.Left > 0, "list item text should be indented");
        }

        /// <summary>
        /// The regression this suite was written for: a Table's cells produced no glyphs at all, so a
        /// document's tabular content simply vanished.
        /// </summary>
        [Fact]
        public void TableCellTextReachesThePage()
        {
            PageContent page = DocumentHarness.Paginate(TwoByTwo());

            Assert.True(page.Contains("top left"), $"cell 'top left' is missing: {page}");
            Assert.True(page.Contains("top right"), $"cell 'top right' is missing: {page}");
            Assert.True(page.Contains("bottom left"), $"cell 'bottom left' is missing: {page}");
            Assert.True(page.Contains("bottom right"), $"cell 'bottom right' is missing: {page}");
        }

        [Fact]
        public void TableCellsAreLaidOutInAGrid()
        {
            PageContent page = DocumentHarness.Paginate(TwoByTwo());

            Rect tl = Require(page, "top left"), tr = Require(page, "top right");
            Rect bl = Require(page, "bottom left"), br = Require(page, "bottom right");

            // Columns: the right column starts to the right of the left one, on both rows.
            Assert.True(tr.Left > tl.Left, $"top right ({tr.Left}) should be right of top left ({tl.Left})");
            Assert.True(br.Left > bl.Left, $"bottom right ({br.Left}) should be right of bottom left ({bl.Left})");

            // Rows: row two is below row one, and the cells within a row share a top.
            Assert.True(bl.Top > tl.Top, $"row 2 ({bl.Top}) should be below row 1 ({tl.Top})");
            Assert.True(System.Math.Abs(tl.Top - tr.Top) < 1.0, "cells in a row should share a top");
            Assert.True(System.Math.Abs(bl.Top - br.Top) < 1.0, "cells in a row should share a top");
        }

        [Fact]
        public void TableRespectsExplicitColumnWidths()
        {
            var table = new Table();
            table.Columns.Add(new TableColumn { Width = new GridLength(120) });
            table.Columns.Add(new TableColumn { Width = new GridLength(280) });

            var row = new TableRow();
            row.Cells.Add(new TableCell(DocumentHarness.Para("narrow")));
            row.Cells.Add(new TableCell(DocumentHarness.Para("wide")));
            table.RowGroups.Add(new TableRowGroup());
            table.RowGroups[0].Rows.Add(row);

            var doc = new FlowDocument();
            doc.Blocks.Add(table);

            PageContent page = DocumentHarness.Paginate(doc, width: 600);

            Rect narrow = Require(page, "narrow"), wide = Require(page, "wide");

            // The second column starts at the first column's declared width, not at half the page.
            Assert.True(wide.Left >= 115 && wide.Left <= 160,
                $"second column should start near x=120, was {wide.Left}");
            Assert.True(narrow.Left < 60, $"first column should start at the left edge, was {narrow.Left}");
        }

        [Fact]
        public void TableCellHonoursColumnSpan()
        {
            var table = new Table();
            table.Columns.Add(new TableColumn { Width = new GridLength(150) });
            table.Columns.Add(new TableColumn { Width = new GridLength(150) });

            var header = new TableRow();
            header.Cells.Add(new TableCell(DocumentHarness.Para("spanning header")) { ColumnSpan = 2 });
            var body = new TableRow();
            body.Cells.Add(new TableCell(DocumentHarness.Para("left")));
            body.Cells.Add(new TableCell(DocumentHarness.Para("right")));

            table.RowGroups.Add(new TableRowGroup());
            table.RowGroups[0].Rows.Add(header);
            table.RowGroups[0].Rows.Add(body);

            var doc = new FlowDocument();
            doc.Blocks.Add(table);

            PageContent page = DocumentHarness.Paginate(doc, width: 600);

            Rect head = Require(page, "spanning header");
            Rect right = Require(page, "right");

            // The spanning cell starts in column one; the second row's second cell still starts in
            // column two, so the span widened a cell rather than shifting the grid.
            Assert.True(head.Left < 60, $"spanning cell should start at column 1, was {head.Left}");
            Assert.True(right.Left >= 145 && right.Left <= 190,
                $"column 2 should still start near x=150, was {right.Left}");
        }

        [Fact]
        public void TableDrawsItsCellBordersAndBackgrounds()
        {
            var table = new Table { CellSpacing = 0 };
            table.Columns.Add(new TableColumn { Width = new GridLength(150) });

            var row = new TableRow();
            row.Cells.Add(new TableCell(DocumentHarness.Para("boxed"))
            {
                BorderBrush = Brushes.Black,
                BorderThickness = new Thickness(1),
                Background = Brushes.LightGray,
            });
            table.RowGroups.Add(new TableRowGroup());
            table.RowGroups[0].Rows.Add(row);

            var doc = new FlowDocument();
            doc.Blocks.Add(table);

            PageContent page = DocumentHarness.Paginate(doc);

            Assert.True(page.Contains("boxed"), $"cell text is missing: {page}");
            Assert.True(page.Rectangles.Count > 0,
                "a cell with a border and a background should draw geometry, not only text");
        }

        [Fact]
        public void TableFollowsSurroundingParagraphs()
        {
            var doc = new FlowDocument();
            doc.Blocks.Add(DocumentHarness.Para("before the table"));
            doc.Blocks.Add(TwoByTwoTable());
            doc.Blocks.Add(DocumentHarness.Para("after the table"));

            PageContent page = DocumentHarness.Paginate(doc);

            Rect before = Require(page, "before the table");
            Rect cell = Require(page, "top left");
            Rect after = Require(page, "after the table");

            Assert.True(cell.Top > before.Top, "the table should come after the paragraph above it");
            Assert.True(after.Top > cell.Top, "the paragraph below should come after the table");
        }

        /// <summary>
        /// A BlockUIContainer hosts a real UIElement. It has to become a visual child of the page,
        /// not a drawing: that is what makes it paint and what makes it take input. It also has to
        /// occupy its measured height, or everything below it sits in the wrong place.
        /// </summary>
        [Fact]
        public void BlockUIContainerHostsItsElement()
        {
            System.Windows.Controls.Button button = null;
            PageContent page = DocumentHarness.Sta(() =>
            {
                button = new System.Windows.Controls.Button { Content = "click me", Width = 120, Height = 40 };

                var doc = new FlowDocument();
                doc.Blocks.Add(DocumentHarness.Para("above the element"));
                doc.Blocks.Add(new BlockUIContainer(button));
                doc.Blocks.Add(DocumentHarness.Para("below the element"));
                return DocumentHarness.Paginate(doc);
            });

            Assert.True(page.HostsElement(button),
                "the hosted Button never became part of the page's visual tree");

            Rect above = Require(page, "above the element");
            Rect below = Require(page, "below the element");
            Assert.True(below.Top - above.Top >= 40,
                $"the element's height should push the text below it down, got {below.Top - above.Top}px");
        }

        [Fact]
        public void HostedElementIsPositionedWhereLayoutPutIt()
        {
            System.Windows.Controls.Button button = null;
            PageContent page = DocumentHarness.Sta(() =>
            {
                button = new System.Windows.Controls.Button { Content = "hosted", Width = 100, Height = 30 };

                var doc = new FlowDocument();
                doc.Blocks.Add(DocumentHarness.Para("first line of text"));
                doc.Blocks.Add(new BlockUIContainer(button));
                return DocumentHarness.Paginate(doc);
            });

            Rect? bounds = page.ElementBounds(button);
            Assert.True(bounds.HasValue, "the hosted element has no bounds on the page");

            Rect first = Require(page, "first line of text");
            Assert.True(bounds.Value.Top >= first.Top,
                $"the element should sit below the paragraph above it ({bounds.Value.Top} vs {first.Top})");
            Assert.True(bounds.Value.Height >= 29, $"the element lost its height: {bounds.Value.Height}");
        }

        [Fact]
        public void InlineUIContainerHostsItsElement()
        {
            System.Windows.Controls.CheckBox check = null;
            PageContent page = DocumentHarness.Sta(() =>
            {
                check = new System.Windows.Controls.CheckBox { Width = 60, Height = 20 };

                var para = new Paragraph();
                para.Inlines.Add(new Run("before "));
                para.Inlines.Add(new InlineUIContainer(check));
                para.Inlines.Add(new Run(" after"));

                var doc = new FlowDocument();
                doc.Blocks.Add(para);
                return DocumentHarness.Paginate(doc);
            });

            Assert.True(page.HostsElement(check),
                "the inline CheckBox never became part of the page's visual tree");
            Assert.True(page.Contains("before") && page.Contains("after"),
                $"the text either side of the element is missing: {page}");
        }

        [Fact]
        public void InlineElementDoesNotSitUnderTheTextAroundIt()
        {
            System.Windows.Controls.CheckBox check = null;
            PageContent page = DocumentHarness.Sta(() =>
            {
                check = new System.Windows.Controls.CheckBox { Width = 60, Height = 20 };

                var para = new Paragraph();
                para.Inlines.Add(new Run("before"));
                para.Inlines.Add(new InlineUIContainer(check));
                para.Inlines.Add(new Run("after"));

                var doc = new FlowDocument();
                doc.Blocks.Add(para);
                return DocumentHarness.Paginate(doc);
            });

            Rect element = page.ElementBounds(check) ?? Rect.Empty;
            Assert.False(element.IsEmpty, "the inline element has no bounds");

            Rect before = Require(page, "before");
            Rect after = Require(page, "after");

            // The reserved gap is what keeps the two words apart; without it they would be adjacent
            // and the element would be drawn on top of them.
            Assert.True(element.Left >= before.Right - 1.0,
                $"the element ({element.Left}) overlaps the text before it (ends {before.Right})");
            Assert.True(after.Left >= element.Right - 1.0,
                $"the text after ({after.Left}) overlaps the element (ends {element.Right})");
        }

        private static Rect Require(PageContent page, string needle)
        {
            Rect? r = page.Find(needle);
            Assert.True(r.HasValue, $"\"{needle}\" never reached the page: {page}");
            return r!.Value;
        }

        private static FlowDocument TwoByTwo()
        {
            var doc = new FlowDocument();
            doc.Blocks.Add(TwoByTwoTable());
            return doc;
        }

        private static Table TwoByTwoTable()
        {
            var table = new Table();
            table.Columns.Add(new TableColumn());
            table.Columns.Add(new TableColumn());

            var group = new TableRowGroup();
            var r1 = new TableRow();
            r1.Cells.Add(new TableCell(DocumentHarness.Para("top left")));
            r1.Cells.Add(new TableCell(DocumentHarness.Para("top right")));
            var r2 = new TableRow();
            r2.Cells.Add(new TableCell(DocumentHarness.Para("bottom left")));
            r2.Cells.Add(new TableCell(DocumentHarness.Para("bottom right")));
            group.Rows.Add(r1);
            group.Rows.Add(r2);
            table.RowGroups.Add(group);
            return table;
        }
    }
}
