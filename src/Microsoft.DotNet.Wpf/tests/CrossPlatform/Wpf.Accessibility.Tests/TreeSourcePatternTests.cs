// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The patterns the platform backends read through IAutomationTreeSource beyond name, role and value:
// text (offsets, selection, lines), selection containers, grids and tables, scrolling, windows. These
// are what VoiceOver, Orca, TalkBack and a browser's screen reader ask a text field, a list, a data
// grid or a scroll view, and each is answered here from a live control's peer -- the AutomationBridge
// every backend shares -- so a backend only has to translate, never to interpret WPF.
//

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using MS.Internal.Interop;
using Xunit;

namespace Wpf.Accessibility.Tests
{
    public class TreeSourcePatternTests
    {
        private static readonly Type s_bridge = typeof(UIElement).Assembly.GetType("MS.Internal.Automation.AutomationBridge", throwOnError: true)!;

        /// <summary>The tree every backend reads, and the node id of a peer in it.</summary>
        private static IAutomationTreeSource Tree => (IAutomationTreeSource)s_bridge.GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

        /// <summary>The whole tree read once, as a screen reader reads it before it asks about a node.</summary>
        private static void Walk(IAutomationTreeSource tree, int id, int depth = 0)
        {
            if (depth > 32) return;
            foreach (int child in tree.GetChildIds(id)) Walk(tree, child, depth + 1);
        }

        private static int IdOf(AutomationPeer peer) => (int)s_bridge.GetMethod("IdOf", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, new object[] { peer })!;

        [Fact]
        public void ATextBox_ExposesItsText_Selection_AndLines()
        {
            var r = AccessibilityHarness.ProbeRooted(
                () => new TextBox { Text = "first line\r\nsecond", Width = 200, AcceptsReturn = true },
                peer =>
                {
                    int id = IdOf(peer);
                    var tree = Tree;
                    var state = tree.GetState(id);
                    tree.TryGetText(id, out string text);
                    bool selected = tree.SetTextSelection(id, 12, 3);        // "sec", past the line break
                    tree.TryGetTextSelection(id, out int start, out int length);
                    string selectedText = ((TextBox)((UIElementAutomationPeer)peer).Owner).SelectedText;
                    tree.TryGetTextLine(id, 14, out int lineStart, out int lineLength, out int lineNumber);
                    tree.TryGetTextLine(id, 2, out int firstStart, out _, out int firstNumber);
                    return (state, text, selected, start, length, selectedText, lineStart, lineLength, lineNumber, firstStart, firstNumber);
                });
            Assert.True(r.state.HasFlag(AccessibleState.HasText));
            Assert.Equal("first line\r\nsecond", r.text);
            Assert.True(r.selected);
            Assert.Equal("sec", r.selectedText);
            Assert.Equal((12, 3), (r.start, r.length));
            Assert.Equal(1, r.lineNumber);
            Assert.Equal(12, r.lineStart);
            Assert.Equal(6, r.lineLength);
            Assert.Equal((0, 0), (r.firstStart, r.firstNumber));
        }

        [Fact]
        public void AListBox_IsASelectionContainer_AndNamesItsSelectedItems()
        {
            var r = AccessibilityHarness.ProbeRooted(
                () =>
                {
                    var list = new ListBox { SelectionMode = SelectionMode.Extended };
                    foreach (string s in new[] { "one", "two", "three" }) list.Items.Add(new ListBoxItem { Content = s });
                    list.SelectedItems.Add(list.Items[0]);
                    list.SelectedItems.Add(list.Items[2]);
                    return list;
                },
                peer =>
                {
                    int id = IdOf(peer);
                    var tree = Tree;
                    tree.GetChildIds(id);       // the item peers exist once the list's children were asked for, as a reader asks
                    int[] selected = tree.GetSelectedIds(id);
                    return (tree.GetState(id), selected.Select(tree.GetName).ToArray());
                });
            Assert.True(r.Item1.HasFlag(AccessibleState.SelectionContainer));
            Assert.True(r.Item1.HasFlag(AccessibleState.MultiSelectable));
            Assert.Equal(new[] { "one", "three" }, r.Item2);
        }

        public sealed class Row { public string Name { get; set; } = ""; public int Count { get; set; } }

        [Fact]
        public void ADataGrid_IsATableOfCells_WithColumnHeaders()
        {
            var r = AccessibilityHarness.ProbeRooted(
                () => new DataGrid
                {
                    ItemsSource = new List<Row> { new() { Name = "apples", Count = 3 }, new() { Name = "pears", Count = 5 } },
                    CanUserAddRows = false,
                    Width = 300, Height = 200,
                },
                peer =>
                {
                    int id = IdOf(peer);
                    var tree = Tree;
                    Walk(tree, id);
                    tree.TryGetGrid(id, out int rows, out int columns);
                    int cell = tree.GetGridCell(id, 1, 0);
                    tree.TryGetGridItem(cell, out int row, out int column, out _, out _, out int grid);
                    // the headers are the visible header row's (DataGrid's peer lists it only when IsVisible,
                    // which an element outside a shown window never is): here, none, and an array still
                    string[] headers = tree.GetColumnHeaderIds(id).Select(tree.GetName).ToArray();
                    return (tree.GetState(id), rows, columns, row, column, grid == id, headers);
                });
            Assert.True(r.Item1.HasFlag(AccessibleState.Grid));
            Assert.True(r.Item1.HasFlag(AccessibleState.Table));
            Assert.Equal((2, 2), (r.rows, r.columns));
            Assert.Equal((1, 0), (r.row, r.column));
            Assert.True(r.Item6, "the cell names its grid");
            Assert.NotNull(r.headers);
        }

        [Fact]
        public void AScrollViewer_ReportsWhereItIs_AndScrolls()
        {
            var r = AccessibilityHarness.ProbeRooted(
                () => new ScrollViewer { Width = 100, Height = 100, Content = new Border { Width = 80, Height = 400 } },
                peer =>
                {
                    int id = IdOf(peer);
                    var tree = Tree;
                    tree.TryGetScroll(id, out double h, out double v, out double hv, out double vv, out bool hs, out bool vs);
                    bool scrolled = tree.Scroll(id, 2, 3);       // a page down
                    var viewer = (ScrollViewer)((UIElementAutomationPeer)peer).Owner;
                    viewer.UpdateLayout();
                    return (tree.GetState(id), v, vv, hs, vs, scrolled, viewer.VerticalOffset);
                });
            Assert.True(r.Item1.HasFlag(AccessibleState.Scrollable));
            Assert.Equal(0, r.v);
            Assert.Equal(25, r.vv, 1);
            Assert.False(r.hs);
            Assert.True(r.vs);
            Assert.True(r.scrolled);
            Assert.True(r.VerticalOffset > 0, "a page down moved the content");
        }

        [Fact]
        public void AWindow_ReportsItsVisualState()
        {
            var r = AccessibilityHarness.ProbeRooted(
                () => new Window { WindowState = WindowState.Maximized, ResizeMode = ResizeMode.CanMinimize },
                peer =>
                {
                    int id = IdOf(peer);
                    var tree = Tree;
                    bool ok = tree.TryGetWindow(id, out int state, out bool canMin, out bool canMax, out _);
                    return (tree.GetState(id), ok, state, canMin, canMax);
                });
            Assert.True(r.Item1.HasFlag(AccessibleState.Window));
            Assert.True(r.ok);
            Assert.Equal(1, r.state);       // maximized
            Assert.True(r.canMin);
            Assert.False(r.canMax);
        }
    }
}
