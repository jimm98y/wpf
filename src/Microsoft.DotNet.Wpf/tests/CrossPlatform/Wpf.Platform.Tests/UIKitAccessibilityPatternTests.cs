// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The iOS accessibility element's patterns -- text, tables, scrolling, windows, notifications --
// as the decisions behind each IMP.
//
// Unlike the macOS backend, these cannot be driven through objc_msgSend in-process: UIKit is an
// iOS framework and does not load into a macOS process, so neither UIAccessibilityElement nor the
// element class derived from it exists here. Every IMP is therefore a thin shell around an internal
// function taking the tree and the node, and these tests call those functions directly against a
// fake tree with the shape of AutomationBridge's answers. What is left untested here is the shell:
// the selector registrations and their type encodings.
//

#nullable disable

using System;
using System.Collections.Generic;
using MS.Internal.Interop;
using Xunit;

namespace Wpf.Platform.Tests
{
    public class UIKitAccessibilityPatternTests
    {
        private static readonly nint NSNotFound = nint.MaxValue;

        // ---- traits ----

        [Fact]
        public void TraitsHaveTheValuesUIKitExports()
        {
            // UIKit's UIAccessibilityTrait* variables hold these; the old values here were each one bit
            // off, so a checked checkbox was announced as one that plays a sound.
            Assert.Equal(0x1ul, UIKitTraits.Button);
            Assert.Equal(0x2ul, UIKitTraits.Link);
            Assert.Equal(0x4ul, UIKitTraits.Image);
            Assert.Equal(0x8ul, UIKitTraits.Selected);
            Assert.Equal(0x40ul, UIKitTraits.StaticText);
            Assert.Equal(0x100ul, UIKitTraits.NotEnabled);
            Assert.Equal(0x200ul, UIKitTraits.UpdatesFrequently);
            Assert.Equal(0x1000ul, UIKitTraits.Adjustable);
            Assert.Equal(0x8000ul, UIKitTraits.TabBar);
            Assert.Equal(0x10000ul, UIKitTraits.Header);
        }

        [Fact]
        public void RolesAndStatesBecomeTraits()
        {
            const AccessibleState Enabled = AccessibleState.Enabled;

            Assert.Equal(UIKitTraits.Button | UIKitTraits.Selected, UIKitTraits.For(2, Enabled | AccessibleState.Checked));
            Assert.Equal(UIKitTraits.Button, UIKitTraits.For(2, Enabled));
            Assert.Equal(UIKitTraits.Button | UIKitTraits.NotEnabled, UIKitTraits.For(0, AccessibleState.None));
            Assert.Equal(UIKitTraits.Adjustable, UIKitTraits.For(15, Enabled | AccessibleState.HasRange));
            Assert.Equal(UIKitTraits.StaticText, UIKitTraits.For(20, Enabled));
            Assert.Equal(UIKitTraits.Header, UIKitTraits.For(35, Enabled));
            // a selected list item is heard as selected
            Assert.Equal(UIKitTraits.Selected, UIKitTraits.For(7, Enabled | AccessibleState.Selected));
        }

        [Fact]
        public void GridsAreDataTablesAndListsAreLists()
        {
            Assert.Equal((nint)1, UIKitAccessibility.ContainerTypeFor(28, AccessibleState.Grid | AccessibleState.Table));
            Assert.Equal((nint)1, UIKitAccessibility.ContainerTypeFor(8, AccessibleState.Grid));   // a GridView ListView
            Assert.Equal((nint)2, UIKitAccessibility.ContainerTypeFor(8, AccessibleState.None));
            // a table control without a Grid pattern cannot hand out cells, so it is not a DataTable
            Assert.Equal((nint)4, UIKitAccessibility.ContainerTypeFor(36, AccessibleState.None));
            Assert.Equal((nint)4, UIKitAccessibility.ContainerTypeFor(26, AccessibleState.None));
        }

        // ---- which selectors a node answers ----

        [Fact]
        public void GatedSelectorsAndProtocolsBelongToTheirPattern()
        {
            Assert.Equal(UIKitAccessibility.PatternGate.Text, UIKitAccessibility.GateOf("accessibilityContentForLineNumber:"));
            Assert.Equal(UIKitAccessibility.PatternGate.Text, UIKitAccessibility.GateOf("accessibilityPageContent"));
            Assert.Equal(UIKitAccessibility.PatternGate.Text, UIKitAccessibility.GateOf("UIAccessibilityReadingContent"));
            Assert.Equal(UIKitAccessibility.PatternGate.Table, UIKitAccessibility.GateOf("accessibilityDataTableCellElementForRow:column:"));
            Assert.Equal(UIKitAccessibility.PatternGate.Table, UIKitAccessibility.GateOf("accessibilityRowCount"));
            Assert.Equal(UIKitAccessibility.PatternGate.Table, UIKitAccessibility.GateOf("UIAccessibilityContainerDataTable"));
            Assert.Equal(UIKitAccessibility.PatternGate.Cell, UIKitAccessibility.GateOf("accessibilityRowRange"));
            Assert.Equal(UIKitAccessibility.PatternGate.Cell, UIKitAccessibility.GateOf("UIAccessibilityContainerDataTableCell"));
            // everything else is the superclass's to answer
            Assert.Equal(UIKitAccessibility.PatternGate.None, UIKitAccessibility.GateOf("accessibilityLabel"));
            Assert.Equal(UIKitAccessibility.PatternGate.None, UIKitAccessibility.GateOf("accessibilityScroll:"));
        }

        [Fact]
        public void OnlyTextNodesClaimToBeReadingContent()
        {
            var tree = new FakeTree();
            tree.Text[1] = "Hello";
            tree.States[2] = AccessibleState.Enabled;                                    // a button
            tree.Text[3] = "secret";
            tree.States[3] = AccessibleState.HasText | AccessibleState.Password;         // a password box

            Assert.True(UIKitAccessibility.Passes(UIKitAccessibility.PatternGate.Text, tree, 1, false));
            Assert.False(UIKitAccessibility.Passes(UIKitAccessibility.PatternGate.Text, tree, 2, false));
            Assert.False(UIKitAccessibility.Passes(UIKitAccessibility.PatternGate.Text, tree, 3, false));
            Assert.False(UIKitAccessibility.Passes(UIKitAccessibility.PatternGate.Text, tree, -1, false));
            Assert.False(UIKitAccessibility.Passes(UIKitAccessibility.PatternGate.Text, null, 1, false));
        }

        [Fact]
        public void OnlyANestedGridIsADataTableAndOnlyItsPartsAreCells()
        {
            FakeTree tree = TableTree();

            Assert.True(UIKitAccessibility.Passes(UIKitAccessibility.PatternGate.Table, tree, Grid, isNestedContainer: true));
            // a grid with nothing listed in it is an element, not a container, so it has no cells to give
            Assert.False(UIKitAccessibility.Passes(UIKitAccessibility.PatternGate.Table, tree, Grid, isNestedContainer: false));
            Assert.False(UIKitAccessibility.Passes(UIKitAccessibility.PatternGate.Table, tree, Row0, isNestedContainer: true));

            Assert.True(UIKitAccessibility.Passes(UIKitAccessibility.PatternGate.Cell, tree, Cell(1, 1), false));
            Assert.True(UIKitAccessibility.Passes(UIKitAccessibility.PatternGate.Cell, tree, Row0, false));
            Assert.True(UIKitAccessibility.Passes(UIKitAccessibility.PatternGate.Cell, tree, Header0, false));
            Assert.False(UIKitAccessibility.Passes(UIKitAccessibility.PatternGate.Cell, tree, Grid, false));
        }

        // ---- text ----

        [Fact]
        public void TextIsTheValueUnlessItIsTheName()
        {
            var tree = new FakeTree();
            tree.Text[1] = "Typed so far";
            tree.Text[2] = "Caption";
            tree.Names[2] = "Caption";
            tree.Text[3] = "hunter2";
            tree.States[3] = AccessibleState.HasText | AccessibleState.Password;

            Assert.Equal("Typed so far", UIKitAccessibility.TextValue(tree, 1));
            Assert.Null(UIKitAccessibility.TextValue(tree, 2));   // read once, as the label
            Assert.Null(UIKitAccessibility.TextValue(tree, 3));   // never the characters of a password
            Assert.Null(UIKitAccessibility.TextValue(tree, 4));   // no Text pattern
        }

        [Fact]
        public void LinesAreReadWithoutTheirBreaks()
        {
            var tree = new FakeTree();
            tree.Text[1] = "first\nsecond\n\nfourth";

            Assert.Equal("first", UIKitAccessibility.LineContent(tree, 1, 0));
            Assert.Equal("second", UIKitAccessibility.LineContent(tree, 1, 1));
            Assert.Equal("", UIKitAccessibility.LineContent(tree, 1, 2));          // an empty line is still a line
            Assert.Equal("fourth", UIKitAccessibility.LineContent(tree, 1, 3));
            Assert.Null(UIKitAccessibility.LineContent(tree, 1, 4));
            Assert.Null(UIKitAccessibility.LineContent(tree, 1, -1));
        }

        [Fact]
        public void LinesWalkEvenWhenTheTreeReportsThemWithoutBreaks()
        {
            var tree = new FakeTree { LinesIncludeBreak = false };
            tree.Text[1] = "ab\ncd\nef";

            Assert.True(UIKitAccessibility.TryGetLineRange(tree, 1, tree.Text[1], 2, out int start, out int length));
            Assert.Equal((6, 2), (start, length));
        }

        [Fact]
        public void TextWithoutLineGeometryIsOneLine()
        {
            var tree = new FakeTree { HasLines = false };
            tree.Text[1] = "all of it";

            Assert.Equal("all of it", UIKitAccessibility.LineContent(tree, 1, 0));
            Assert.Null(UIKitAccessibility.LineContent(tree, 1, 1));
            Assert.Null(UIKitAccessibility.LineContent(tree, 2, 0));   // not text at all
        }

        [Fact]
        public void ALineFrameIsItsCharactersBoundsInPoints()
        {
            var tree = new FakeTree { RangeBounds = (20, 40, 200, 30) };
            tree.Text[1] = "one\ntwo";

            Assert.True(UIKitAccessibility.TryGetLineFrame(tree, 1, 1, 2.0, out double x, out double y, out double w, out double h));
            Assert.Equal((4, 3), tree.LastBoundsRange);
            Assert.Equal((10.0, 20.0, 100.0, 15.0), (x, y, w, h));

            Assert.False(UIKitAccessibility.TryGetLineFrame(tree, 1, 5, 2.0, out _, out _, out _, out _));
        }

        [Fact]
        public void APointIsTheLineOfTheCharacterUnderIt()
        {
            var tree = new FakeTree { OffsetAtPoint = 5 };
            tree.Text[1] = "one\ntwo\nthree";

            Assert.Equal((nint)1, UIKitAccessibility.LineNumberAt(tree, 1, 10, 30, 3.0));
            Assert.Equal((30.0, 90.0), tree.LastPoint);   // points to the tree's device pixels

            tree.OffsetAtPoint = -1;
            Assert.Equal(NSNotFound, UIKitAccessibility.LineNumberAt(tree, 1, 10, 30, 3.0));
            Assert.Equal(NSNotFound, UIKitAccessibility.LineNumberAt(tree, 2, 10, 30, 3.0));
        }

        [Fact]
        public void APageIsTheVisibleText()
        {
            var tree = new FakeTree();
            tree.Text[1] = "above the fold, on screen, below";
            tree.Visible[1] = (16, 9);
            tree.Text[2] = "short";

            Assert.Equal("on screen", UIKitAccessibility.PageContent(tree, 1));
            Assert.Equal("short", UIKitAccessibility.PageContent(tree, 2));   // no visible range: all of it
            Assert.Null(UIKitAccessibility.PageContent(tree, 3));
        }

        [Fact]
        public void OnlyTheFocusedEditorForwardsToTheTextInput()
        {
            const AccessibleState Editor = AccessibleState.HasText | AccessibleState.Enabled | AccessibleState.Focused;

            Assert.True(UIKitAccessibility.ForwardsTextInput(Editor));
            Assert.False(UIKitAccessibility.ForwardsTextInput(Editor & ~AccessibleState.Focused));
            Assert.False(UIKitAccessibility.ForwardsTextInput(Editor | AccessibleState.ReadOnly));
            Assert.False(UIKitAccessibility.ForwardsTextInput(Editor | AccessibleState.Password));
            Assert.False(UIKitAccessibility.ForwardsTextInput(AccessibleState.Focused | AccessibleState.Enabled));
        }

        // ---- tables ----

        [Fact]
        public void CellsAreFoundByRowAndColumnWithinTheGrid()
        {
            FakeTree tree = TableTree();

            Assert.Equal(Cell(1, 2), UIKitAccessibility.CellAt(tree, Grid, 1, 2));
            Assert.Equal(-1, UIKitAccessibility.CellAt(tree, Grid, 2, 0));          // past the last row
            Assert.Equal(-1, UIKitAccessibility.CellAt(tree, Grid, 0, 3));
            Assert.Equal(-1, UIKitAccessibility.CellAt(tree, Grid, ulong.MaxValue, 0));
            Assert.Equal(-1, UIKitAccessibility.CellAt(tree, Row0, 0, 0));         // not a grid
        }

        [Fact]
        public void HeadersAreOnePerColumn()
        {
            int[] headers = { 50, 51, 52 };
            Assert.Equal(51, UIKitAccessibility.HeaderAt(headers, 1));
            Assert.Equal(-1, UIKitAccessibility.HeaderAt(headers, 3));
            Assert.Equal(-1, UIKitAccessibility.HeaderAt(Array.Empty<int>(), 0));
            Assert.Equal(-1, UIKitAccessibility.HeaderAt(null, 0));
        }

        [Fact]
        public void PartsOfATableReportTheRowsAndColumnsTheyCover()
        {
            FakeTree tree = TableTree();
            tree.GridItems[Cell(0, 1)] = (0, 1, 1, 2);   // a cell spanning two columns

            Assert.True(UIKitAccessibility.TryGetCellRanges(tree, Cell(1, 2), out int row, out int rowSpan, out int column, out int columnSpan));
            Assert.Equal((1, 1, 2, 1), (row, rowSpan, column, columnSpan));

            Assert.True(UIKitAccessibility.TryGetCellRanges(tree, Cell(0, 1), out row, out rowSpan, out column, out columnSpan));
            Assert.Equal((0, 1, 1, 2), (row, rowSpan, column, columnSpan));

            // a row is its cells' row across every column
            Assert.True(UIKitAccessibility.TryGetCellRanges(tree, Row1, out row, out rowSpan, out column, out columnSpan));
            Assert.Equal((1, 1, 0, 3), (row, rowSpan, column, columnSpan));

            // a column header is its column, on no row
            Assert.True(UIKitAccessibility.TryGetCellRanges(tree, Header0 + 2, out row, out rowSpan, out column, out columnSpan));
            Assert.Equal((-1, 0, 2, 1), (row, rowSpan, column, columnSpan));

            Assert.False(UIKitAccessibility.TryGetCellRanges(tree, Grid, out _, out _, out _, out _));
            Assert.False(UIKitAccessibility.TryGetCellRanges(tree, 999, out _, out _, out _, out _));
        }

        // ---- scrolling ----

        [Fact]
        public void SwipesScrollAPageTheWayTheContentFollowsTheFingers()
        {
            const int Dec = 0, None = 2, Inc = 3;

            Assert.True(UIKitAccessibility.ScrollAmountsFor(3, true, true, out int h, out int v));    // Up: a page down
            Assert.Equal((None, Inc), (h, v));
            Assert.True(UIKitAccessibility.ScrollAmountsFor(4, true, true, out h, out v));            // Down: a page up
            Assert.Equal((None, Dec), (h, v));
            Assert.True(UIKitAccessibility.ScrollAmountsFor(2, true, true, out h, out v));            // Left: a page right
            Assert.Equal((Inc, None), (h, v));
            Assert.True(UIKitAccessibility.ScrollAmountsFor(1, true, true, out h, out v));            // Right: a page left
            Assert.Equal((Dec, None), (h, v));

            // Next and Previous read down the page, or across one that only scrolls across
            Assert.True(UIKitAccessibility.ScrollAmountsFor(5, true, true, out h, out v));
            Assert.Equal((None, Inc), (h, v));
            Assert.True(UIKitAccessibility.ScrollAmountsFor(6, true, false, out h, out v));
            Assert.Equal((Dec, None), (h, v));

            // a direction the node does not scroll in, and directions UIKit does not have
            Assert.False(UIKitAccessibility.ScrollAmountsFor(3, true, false, out _, out _));
            Assert.False(UIKitAccessibility.ScrollAmountsFor(2, false, true, out _, out _));
            Assert.False(UIKitAccessibility.ScrollAmountsFor(5, false, false, out _, out _));
            Assert.False(UIKitAccessibility.ScrollAmountsFor(0, true, true, out _, out _));
            Assert.False(UIKitAccessibility.ScrollAmountsFor(7, true, true, out _, out _));
        }

        [Fact]
        public void APositionAtAnEndCannotScrollFurther()
        {
            Assert.True(UIKitAccessibility.CanScroll(50, 3));
            Assert.False(UIKitAccessibility.CanScroll(100, 3));
            Assert.True(UIKitAccessibility.CanScroll(100, 0));
            Assert.False(UIKitAccessibility.CanScroll(0, 0));
            Assert.False(UIKitAccessibility.CanScroll(-1, 3));    // does not scroll that way at all
            Assert.False(UIKitAccessibility.CanScroll(50, 2));    // no amount
        }

        [Fact]
        public void APageIsOneViewportOfTheScrollableRange()
        {
            // a viewport a quarter of the content: the range is three viewports, so a page is a third
            Assert.Equal(100.0 / 3, UIKitAccessibility.PredictedPercent(0, 25, 3), 6);
            Assert.Equal(100.0, UIKitAccessibility.PredictedPercent(90, 25, 3), 6);
            Assert.Equal(0.0, UIKitAccessibility.PredictedPercent(10, 25, 0), 6);
            Assert.Equal(100.0, UIKitAccessibility.PredictedPercent(0, 100, 3), 6);
        }

        [Fact]
        public void TheStatusIsThePageOfThePosition()
        {
            Assert.Equal("Page 1 of 4", UIKitAccessibility.PageStatus(0, 25));
            Assert.Equal("Page 2 of 4", UIKitAccessibility.PageStatus(100.0 / 3, 25));
            Assert.Equal("Page 4 of 4", UIKitAccessibility.PageStatus(100, 25));
            Assert.Equal("Page 2 of 3", UIKitAccessibility.PageStatus(50, 40));
            Assert.Equal("Page 1 of 1", UIKitAccessibility.PageStatus(0, 100));
            Assert.Equal("Page 1 of 1", UIKitAccessibility.PageStatus(-1, 0));
        }

        [Fact]
        public void TheSwipeScrollsTheNearestScrollingAncestor()
        {
            var tree = new FakeTree();
            tree.Parents[10] = 5;          // an item in a list in a scroll viewer
            tree.Parents[5] = 1;
            tree.States[1] = AccessibleState.Scrollable;
            tree.Scroll[1] = (-1, 0, 100, 25, false, true);

            Assert.True(UIKitAccessibility.TryScrollPage(tree, 10, 3, out string status));
            Assert.Equal(new List<(int, int, int)> { (1, 2, 3) }, tree.ScrollLog);
            // the viewer moves on its next layout, so the status is where the page lands
            Assert.Equal("Page 2 of 4", status);
        }

        [Fact]
        public void TheStatusUsesThePositionWhenTheTreeAlreadyMoved()
        {
            var tree = new FakeTree { ScrollMovesAtOnce = 50 };
            tree.States[1] = AccessibleState.Scrollable;
            tree.Scroll[1] = (-1, 0, 100, 40, false, true);

            Assert.True(UIKitAccessibility.TryScrollPage(tree, 1, 3, out string status));
            Assert.Equal("Page 2 of 3", status);
        }

        [Fact]
        public void AtTheEndTheSwipeGoesToTheNextScrollerOrFails()
        {
            var tree = new FakeTree();
            tree.Parents[2] = 1;
            tree.States[2] = AccessibleState.Scrollable;          // an inner list, at its bottom
            tree.Scroll[2] = (-1, 100, 100, 50, false, true);
            tree.States[1] = AccessibleState.Scrollable;          // the page around it, at its top
            tree.Scroll[1] = (-1, 0, 100, 50, false, true);

            Assert.True(UIKitAccessibility.TryScrollPage(tree, 2, 3, out _));
            Assert.Equal(new List<(int, int, int)> { (1, 2, 3) }, tree.ScrollLog);

            // the inner list can still go up, so it takes the swipe the other way
            tree.ScrollLog.Clear();
            Assert.True(UIKitAccessibility.TryScrollPage(tree, 2, 4, out _));
            Assert.Equal(new List<(int, int, int)> { (2, 2, 0) }, tree.ScrollLog);

            // both at their tops: nothing moves up, NO, and UIKit plays the border sound
            tree.Scroll[2] = (-1, 0, 100, 50, false, true);
            tree.ScrollLog.Clear();
            Assert.False(UIKitAccessibility.TryScrollPage(tree, 2, 4, out string status));
            Assert.Null(status);
            Assert.Empty(tree.ScrollLog);

            // a node with nothing scrolling above it
            Assert.False(UIKitAccessibility.TryScrollPage(tree, 99, 3, out _));
        }

        // ---- windows ----

        [Fact]
        public void EscapeDismissesTheModalWindowAroundTheElement()
        {
            var tree = new FakeTree();
            tree.Parents[3] = 2;
            tree.Parents[2] = 1;
            tree.States[1] = AccessibleState.Window | AccessibleState.Modal;
            tree.States[7] = AccessibleState.Window;                  // an ordinary window

            Assert.Equal(1, UIKitAccessibility.ModalWindowAround(tree, 3));
            Assert.Equal(-1, UIKitAccessibility.ModalWindowAround(tree, 7));
            Assert.True(UIKitAccessibility.IsModalWindow(AccessibleState.Window | AccessibleState.Modal));
            Assert.False(UIKitAccessibility.IsModalWindow(AccessibleState.Modal));
            Assert.False(UIKitAccessibility.IsModalWindow(AccessibleState.Window));
        }

        // ---- notifications ----

        [Fact]
        public void ChangesArePostedAsLayoutChangesAndANewWindowAsANewScreen()
        {
            const uint Screen = 1000, Layout = 1001;

            Assert.Equal(Screen, UIKitAccessibility.NotificationFor(AutomationChangeKind.ChildrenChanged, isWindowRoot: true));
            Assert.Equal(Layout, UIKitAccessibility.NotificationFor(AutomationChangeKind.ChildrenChanged, isWindowRoot: false));
            Assert.Equal(Layout, UIKitAccessibility.NotificationFor(AutomationChangeKind.ChildAdded, false));
            Assert.Equal(Layout, UIKitAccessibility.NotificationFor(AutomationChangeKind.ChildRemoved, false));
            Assert.Equal(Layout, UIKitAccessibility.NotificationFor(AutomationChangeKind.FocusChanged, false));
            Assert.Equal(Layout, UIKitAccessibility.NotificationFor(AutomationChangeKind.TextChanged, false));
            Assert.Equal(Layout, UIKitAccessibility.NotificationFor(AutomationChangeKind.ScrollChanged, false));
            Assert.Equal(Layout, UIKitAccessibility.NotificationFor(AutomationChangeKind.SelectionContainerChanged, false));
            Assert.Equal(Layout, UIKitAccessibility.NotificationFor(AutomationChangeKind.NameChanged, false));
            // the caret is the text input's to announce, through its input delegate
            Assert.Equal(0u, UIKitAccessibility.NotificationFor(AutomationChangeKind.TextSelectionChanged, false));
        }

        [Fact]
        public void ScrollingRebuildsTheListsAsStructureDoes()
        {
            Assert.True(UIKitAccessibility.InvalidatesLists(AutomationChangeKind.ChildAdded));
            Assert.True(UIKitAccessibility.InvalidatesLists(AutomationChangeKind.ChildRemoved));
            Assert.True(UIKitAccessibility.InvalidatesLists(AutomationChangeKind.ChildrenChanged));
            // what is off screen is left out of the lists, so a scroll changes what belongs in them
            Assert.True(UIKitAccessibility.InvalidatesLists(AutomationChangeKind.ScrollChanged));
            Assert.True(UIKitAccessibility.InvalidatesLists(AutomationChangeKind.OffscreenChanged));
            Assert.False(UIKitAccessibility.InvalidatesLists(AutomationChangeKind.TextChanged));
            Assert.False(UIKitAccessibility.InvalidatesLists(AutomationChangeKind.ValueChanged));
        }

        // ---- the fake tree ----

        // A 2 x 3 grid: a header row (Header0..+2), then two rows of cells, each row an item whose
        // children are the cells -- the shape a DataGrid's peers have.
        private const int Grid = 100, Row0 = 110, Row1 = 111, Header0 = 120;
        private static int Cell(int row, int column) => 200 + row * 10 + column;

        private static FakeTree TableTree()
        {
            var tree = new FakeTree();
            tree.States[Grid] = AccessibleState.Grid | AccessibleState.Table;
            tree.Grid[Grid] = (2, 3);
            tree.ColumnHeaders[Grid] = new[] { Header0, Header0 + 1, Header0 + 2 };
            tree.Children[Grid] = new[] { Row0, Row1 };
            for (int c = 0; c < 3; c++) tree.Parents[Header0 + c] = Grid;
            for (int r = 0; r < 2; r++)
            {
                int row = r == 0 ? Row0 : Row1;
                tree.Parents[row] = Grid;
                tree.Children[row] = new[] { Cell(r, 0), Cell(r, 1), Cell(r, 2) };
                for (int c = 0; c < 3; c++)
                {
                    tree.States[Cell(r, c)] = AccessibleState.GridItem;
                    tree.GridItems[Cell(r, c)] = (r, c, 1, 1);
                    tree.Cells[(Grid, r, c)] = Cell(r, c);
                    tree.Parents[Cell(r, c)] = row;
                }
            }
            return tree;
        }

        private sealed class FakeTree : IAutomationTreeSource
        {
            internal readonly Dictionary<int, AccessibleState> States = new();
            internal readonly Dictionary<int, string> Names = new();
            internal readonly Dictionary<int, int> Parents = new();
            internal readonly Dictionary<int, int[]> Children = new();
            internal readonly Dictionary<int, string> Text = new();
            internal readonly Dictionary<int, (int Start, int Length)> Visible = new();
            internal readonly Dictionary<int, (int Rows, int Columns)> Grid = new();
            internal readonly Dictionary<(int Grid, int Row, int Column), int> Cells = new();
            internal readonly Dictionary<int, (int Row, int Column, int RowSpan, int ColumnSpan)> GridItems = new();
            internal readonly Dictionary<int, int[]> ColumnHeaders = new();
            internal readonly Dictionary<int, (double H, double V, double HSize, double VSize, bool HScroll, bool VScroll)> Scroll = new();

            internal bool HasLines = true;
            internal bool LinesIncludeBreak = true;
            internal (double X, double Y, double W, double H) RangeBounds;
            internal (int Start, int Length) LastBoundsRange;
            internal int OffsetAtPoint = -1;
            internal (double X, double Y) LastPoint;
            internal double ScrollMovesAtOnce = -1;   // >= 0: Scroll lands there at once
            internal readonly List<(int, int, int)> ScrollLog = new();

            public int GetRootId(IntPtr windowHandle) => 0;
            public int[] GetChildIds(int id) => Children.TryGetValue(id, out int[] c) ? c : Array.Empty<int>();
            public int GetParentId(int id) => Parents.TryGetValue(id, out int p) ? p : IAutomationTreeSource.InvalidNode;
            public int GetRole(int id) => Text.ContainsKey(id) ? 4 : -1;
            public string GetName(int id) => Names.TryGetValue(id, out string n) ? n : string.Empty;
            public string GetAutomationId(int id) => string.Empty;
            public string GetHelpText(int id) => string.Empty;
            public string GetValue(int id) => string.Empty;
            public bool TryGetRange(int id, out double value, out double minimum, out double maximum) { value = minimum = maximum = 0; return false; }

            public AccessibleState GetState(int id)
                => States.TryGetValue(id, out AccessibleState s) ? s : (Text.ContainsKey(id) ? AccessibleState.HasText : default);

            public bool TryGetBounds(int id, out double x, out double y, out double width, out double height) { x = y = width = height = 0; return false; }
            public int HitTest(IntPtr windowHandle, double screenX, double screenY) => IAutomationTreeSource.InvalidNode;
            public int GetFocusedId(IntPtr windowHandle) => IAutomationTreeSource.InvalidNode;
            public bool SupportsAction(int id, AccessibleAction action) => false;
            public bool DoAction(int id, AccessibleAction action) => false;
            public bool SetValue(int id, string value) => false;
            public bool SetFocus(int id) => false;

            public bool TryGetText(int id, out string text)
            {
                if (Text.TryGetValue(id, out text)) return true;
                text = string.Empty;
                return false;
            }

            public bool TryGetTextRangeBounds(int id, int start, int length, out double x, out double y, out double width, out double height)
            {
                LastBoundsRange = (start, length);
                (x, y, width, height) = RangeBounds;
                return Text.ContainsKey(id);
            }

            public bool TryGetTextLine(int id, int offset, out int lineStart, out int lineLength, out int lineNumber)
            {
                lineStart = lineLength = lineNumber = 0;
                if (!HasLines || !Text.TryGetValue(id, out string text)) return false;

                int start = 0, number = 0;
                for (int i = 0; i < text.Length; i++)
                {
                    if (text[i] != '\n') continue;
                    if (offset <= i)
                    {
                        lineStart = start; lineLength = (LinesIncludeBreak ? i + 1 : i) - start; lineNumber = number;
                        return true;
                    }
                    start = i + 1;
                    number++;
                }
                lineStart = start; lineLength = text.Length - start; lineNumber = number;
                return true;
            }

            public int GetTextOffsetAtPoint(int id, double screenX, double screenY)
            {
                LastPoint = (screenX, screenY);
                return OffsetAtPoint;
            }

            public bool TryGetVisibleTextRange(int id, out int start, out int length)
            {
                bool known = Visible.TryGetValue(id, out var v);
                (start, length) = v;
                return known;
            }

            public bool TryGetGrid(int id, out int rows, out int columns)
            {
                bool known = Grid.TryGetValue(id, out var g);
                (rows, columns) = g;
                return known;
            }

            public int GetGridCell(int id, int row, int column)
                => Cells.TryGetValue((id, row, column), out int cell) ? cell : IAutomationTreeSource.InvalidNode;

            public bool TryGetGridItem(int id, out int row, out int column, out int rowSpan, out int columnSpan, out int gridId)
            {
                bool known = GridItems.TryGetValue(id, out var g);
                (row, column, rowSpan, columnSpan) = g;
                gridId = known ? Grid.Count > 0 ? FirstGrid() : IAutomationTreeSource.InvalidNode : IAutomationTreeSource.InvalidNode;
                return known;
            }

            private int FirstGrid()
            {
                foreach (int id in Grid.Keys) return id;
                return IAutomationTreeSource.InvalidNode;
            }

            public int[] GetColumnHeaderIds(int id) => ColumnHeaders.TryGetValue(id, out int[] h) ? h : Array.Empty<int>();

            public bool TryGetScroll(int id, out double horizontalPercent, out double verticalPercent, out double horizontalViewSize,
                out double verticalViewSize, out bool horizontallyScrollable, out bool verticallyScrollable)
            {
                bool known = Scroll.TryGetValue(id, out var s);
                if (!known) s = (-1, -1, 100, 100, false, false);
                (horizontalPercent, verticalPercent, horizontalViewSize, verticalViewSize, horizontallyScrollable, verticallyScrollable) = s;
                return known;
            }

            bool IAutomationTreeSource.Scroll(int id, int horizontalAmount, int verticalAmount)
            {
                if (!Scroll.TryGetValue(id, out var s)) return false;
                ScrollLog.Add((id, horizontalAmount, verticalAmount));
                if (ScrollMovesAtOnce >= 0) Scroll[id] = s with { V = ScrollMovesAtOnce };
                return true;
            }
        }
    }
}
