// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The browser backend's patterns -- text, selection, grids, scrolling, windows -- without a browser.
//
// The browser half (browser-window.js a11ySync) turns each node of the mirror's JSON into ARIA:
// a text field into an <input>/<textarea> with its value and selection, a grid's numbers into
// aria-rowcount and aria-rowindex, a modal window into a dialog. There is no JavaScript engine in
// this test run, so what is tested is the JSON the managed side builds for it -- the fields and
// the arithmetic (1-based, the header row counted) -- and the managed end of what comes back: a
// selection made in the mirror, Escape in a dialog. Whether a screen reader then reads it is for a
// browser.
//

using System;
using System.Collections.Generic;
using MS.Internal.Interop;
using Xunit;
using FakeTree = Wpf.Platform.Tests.AndroidAccessibilityPatternTests.FakeTree;

namespace Wpf.Platform.Tests
{
    public class BrowserAccessibilityPatternTests
    {
        private static readonly IntPtr Main = (IntPtr)1, Dialog = (IntPtr)2;

        private static string Mirror(FakeTree tree, params IntPtr[] windows)
            => BrowserAccessibility.BuildMirrorJson(tree, windows.Length == 0 ? new[] { Main } : windows);

        private static FakeTree WindowTree()
        {
            var tree = new FakeTree { AllBounded = true };
            tree.Roots[Main] = 0;
            tree.Roles[0] = 32;
            tree.Names[0] = "App";
            tree.States[0] = AccessibleState.Window | AccessibleState.Enabled;
            return tree;
        }

        // The object for one node, cut out of the mirror's array.
        private static string NodeOf(string mirror, int id)
        {
            string key = "{\"id\":" + id + ",";
            int start = mirror.IndexOf(key, StringComparison.Ordinal);
            Assert.True(start >= 0, $"node {id} not in {mirror}");
            int end = mirror.IndexOf("},{\"id\":", start, StringComparison.Ordinal);
            return end < 0 ? mirror.Substring(start, mirror.Length - start - 1) : mirror.Substring(start, end - start + 1);
        }

        // ---- keys and descriptions ----

        [Fact]
        public void KeysAreMirroredAsAriaKeyShortcuts_AndHelpAsADescription()
        {
            FakeTree tree = WindowTree();
            tree.SetChildren(0, 1, 2);
            tree.Roles[1] = 0; tree.Names[1] = "Open"; tree.States[1] = AccessibleState.Enabled;
            tree.Keys[1] = ("O", "", "");                    // WPF's button: the letter, pressed with Alt
            tree.Roles[2] = 11; tree.Names[2] = "Save"; tree.States[2] = AccessibleState.Enabled;
            tree.Keys[2] = ("S", "Ctrl+Shift+S", "");        // a menu item's letter is pressed alone
            string mirror = Mirror(tree);
            Assert.Contains("\"ks\":\"Alt+O\"", NodeOf(mirror, 1));
            Assert.Contains("\"ks\":\"S Control+Shift+S\"", NodeOf(mirror, 2));
        }

        // ---- text ----

        [Fact]
        public void ATextBoxIsMirroredWithItsTextAndSelection()
        {
            FakeTree tree = WindowTree();
            tree.SetChildren(0, 1);
            tree.AddText(1, "Hello world", AccessibleState.HasValue | AccessibleState.Enabled | AccessibleState.Focusable);
            tree.Selections[1] = (6, 5);

            string node = NodeOf(Mirror(tree), 1);
            Assert.Contains("\"r\":\"textbox\"", node);
            Assert.Contains("\"tx\":\"Hello world\"", node);
            Assert.Contains("\"ed\":1", node);
            Assert.Contains("\"ss\":6,\"se\":11", node);
            Assert.DoesNotContain("\"ro\":", node);
            Assert.DoesNotContain("\"ml\":", node);
            // The text is the text control's value; the old value-as-text field would say it twice.
            Assert.DoesNotContain("\"vt\":", node);
        }

        [Fact]
        public void AReadOnlyOrMultiLineTextSaysSo()
        {
            FakeTree tree = WindowTree();
            tree.SetChildren(0, 1, 2);
            tree.AddText(1, "line one\nline two", AccessibleState.HasValue | AccessibleState.ReadOnly);
            tree.AddText(2, "rich", default);
            tree.Roles[2] = 30;   // Document: no Value pattern, so nothing can be written back

            string mirror = Mirror(tree);
            string readOnly = NodeOf(mirror, 1);
            Assert.Contains("\"ml\":1", readOnly);
            Assert.Contains("\"ro\":1", readOnly);
            Assert.DoesNotContain("\"ed\":", readOnly);
            Assert.Contains("\"tx\":\"line one\\nline two\"", readOnly);

            string document = NodeOf(mirror, 2);
            Assert.Contains("\"r\":\"document\"", document);
            Assert.Contains("\"ml\":1", document);
            Assert.DoesNotContain("\"ed\":", document);
        }

        [Fact]
        public void APasswordAndStaticTextAreNotTextControls()
        {
            FakeTree tree = WindowTree();
            tree.SetChildren(0, 1, 2);
            tree.AddText(1, "secret", AccessibleState.HasValue | AccessibleState.Password);
            tree.AddText(2, "A label", default);
            tree.Roles[2] = 20;   // Text
            tree.Names[2] = "A label";

            string mirror = Mirror(tree);
            Assert.DoesNotContain("secret", mirror);
            Assert.DoesNotContain("\"tx\":", mirror);
            Assert.Contains("\"r\":\"\",\"n\":\"A label\"", NodeOf(mirror, 2));

            Assert.False(BrowserAccessibility.IsTextControl(4, AccessibleState.HasText | AccessibleState.Password));
            Assert.False(BrowserAccessibility.IsTextControl(20, AccessibleState.HasText));
            Assert.False(BrowserAccessibility.IsTextControl(4, AccessibleState.HasValue));
            Assert.True(BrowserAccessibility.IsTextControl(30, AccessibleState.HasText));
        }

        [Fact]
        public void ATextsControlCharactersAreKeptAndALabelsDropped()
        {
            FakeTree tree = WindowTree();
            tree.SetChildren(0, 1);
            tree.AddText(1, "a\u0001b", default);
            tree.Names[1] = "x\u0001y";
            string node = NodeOf(Mirror(tree), 1);
            // The selection's offsets count every code unit, so the text keeps them all.
            Assert.Contains("\"tx\":\"a\\u0001b\"", node);
            Assert.Contains("\"n\":\"xy\"", node);
        }

        [Fact]
        public void AVeryLongTextIsCut()
        {
            FakeTree tree = WindowTree();
            tree.SetChildren(0, 1);
            tree.AddText(1, new string('a', BrowserAccessibility.MaxText + 50), default);
            tree.Selections[1] = (BrowserAccessibility.MaxText + 10, 5);
            string node = NodeOf(Mirror(tree), 1);
            Assert.Contains($"\"ss\":{BrowserAccessibility.MaxText},\"se\":{BrowserAccessibility.MaxText}", node);
            Assert.DoesNotContain(new string('a', BrowserAccessibility.MaxText + 1), node);
        }

        [Fact]
        public void ASelectionFromTheMirrorIsApplied()
        {
            FakeTree tree = WindowTree();
            tree.AddText(1, "Hello world", AccessibleState.HasValue);

            Assert.True(BrowserAccessibility.ApplyTextSelection(tree, 1, 6, 11));
            Assert.Equal((6, 5), tree.Selections[1]);
            Assert.True(BrowserAccessibility.ApplyTextSelection(tree, 1, 9, 2));   // a backward selection
            Assert.Equal((2, 7), tree.Selections[1]);
            Assert.True(BrowserAccessibility.ApplyTextSelection(tree, 1, 4, 400));
            Assert.Equal((4, 7), tree.Selections[1]);
            Assert.False(BrowserAccessibility.ApplyTextSelection(tree, 404, 0, 1));

            Assert.True(BrowserAccessibility.TryParsePair("12,40.5", out double a, out double b));
            Assert.Equal((12.0, 40.5), (a, b));
            Assert.True(BrowserAccessibility.TryParsePair("-1,25", out a, out b));
            Assert.Equal((-1.0, 25.0), (a, b));
            Assert.False(BrowserAccessibility.TryParsePair("", out _, out _));
            Assert.False(BrowserAccessibility.TryParsePair("7", out _, out _));
        }

        // ---- selection ----

        [Fact]
        public void AMultiSelectListAndItsItemsSaySo()
        {
            FakeTree tree = WindowTree();
            tree.SetChildren(0, 10);
            tree.Roles[10] = 8;
            tree.States[10] = AccessibleState.SelectionContainer | AccessibleState.MultiSelectable;
            tree.SetChildren(10, 11, 12);
            tree.Roles[11] = tree.Roles[12] = 7;
            tree.Names[11] = "Apple";
            tree.Names[12] = "Banana";
            tree.States[11] = AccessibleState.Selectable;
            tree.States[12] = AccessibleState.Selectable | AccessibleState.Selected;

            string mirror = Mirror(tree);
            Assert.Contains("\"r\":\"listbox\"", NodeOf(mirror, 10));
            Assert.Contains("\"ms\":1", NodeOf(mirror, 10));
            Assert.Contains("\"p\":10", NodeOf(mirror, 11));   // owned by the list
            Assert.Contains("\"sbl\":1", NodeOf(mirror, 11));
            Assert.DoesNotContain("\"sel\":", NodeOf(mirror, 11));
            Assert.Contains("\"sel\":1,\"sbl\":1", NodeOf(mirror, 12));

            // A single-select list is not multiselectable.
            tree.States[10] = AccessibleState.SelectionContainer;
            Assert.DoesNotContain("\"ms\":", NodeOf(Mirror(tree), 10));
        }

        // ---- grids ----

        private const int HeaderRow = 130;

        private static FakeTree GridInWindow(bool table)
        {
            FakeTree tree = AndroidAccessibilityPatternTests.TableTree();
            tree.AllBounded = true;
            tree.Roots[Main] = 0;
            tree.Roles[0] = 32;
            tree.States[0] = AccessibleState.Window;
            tree.SetChildren(0, AndroidAccessibilityPatternTests.Grid);
            if (!table)
            {
                tree.States[AndroidAccessibilityPatternTests.Grid] &= ~AccessibleState.Table;
                return tree;
            }
            // The header row, under the grid beside the data rows, as a DataGrid's peers have it.
            int header0 = AndroidAccessibilityPatternTests.Header0;
            tree.SetChildren(AndroidAccessibilityPatternTests.Grid, HeaderRow, AndroidAccessibilityPatternTests.Row0, AndroidAccessibilityPatternTests.Row1);
            tree.Roles[HeaderRow] = 34;
            tree.SetChildren(HeaderRow, header0, header0 + 1, header0 + 2);
            return tree;
        }

        [Fact]
        public void AGridCountsItsHeaderRowAndItsCellsAreOneBased()
        {
            FakeTree tree = GridInWindow(table: true);
            int grid = AndroidAccessibilityPatternTests.Grid;

            string mirror = Mirror(tree);
            string gridNode = NodeOf(mirror, grid);
            Assert.Contains("\"r\":\"grid\"", gridNode);
            Assert.Contains("\"rc\":3,\"cc\":3", gridNode);        // two data rows and the header row
            Assert.Contains("\"ms\":1", gridNode);

            string header = NodeOf(mirror, AndroidAccessibilityPatternTests.Header0 + 2);
            Assert.Contains("\"r\":\"columnheader\"", header);
            Assert.Contains("\"ri\":1,\"ci\":3", header);

            string cell = NodeOf(mirror, AndroidAccessibilityPatternTests.Cell(1, 2));
            Assert.Contains("\"r\":\"gridcell\"", cell);              // a DataItem peer, but a cell of a grid
            Assert.Contains("\"ri\":3,\"ci\":3", cell);
            Assert.DoesNotContain("\"rs\":", cell);

            string row = NodeOf(mirror, AndroidAccessibilityPatternTests.Row0);
            Assert.Contains("\"r\":\"row\"", row);
            Assert.Contains("\"ri\":2", row);
        }

        [Fact]
        public void AGridWithoutHeadersStartsAtRowOneAndSpansAreSent()
        {
            FakeTree tree = GridInWindow(table: false);
            int grid = AndroidAccessibilityPatternTests.Grid;
            int cell = AndroidAccessibilityPatternTests.Cell(0, 0);
            tree.GridItems[cell] = (0, 0, 2, 3, grid);

            string mirror = Mirror(tree);
            Assert.Contains("\"rc\":2,\"cc\":3", NodeOf(mirror, grid));
            Assert.Contains("\"ri\":1,\"ci\":1,\"rs\":2,\"cs\":3", NodeOf(mirror, cell));
        }

        [Fact]
        public void ATablesCellsAreCellsAndRowHeadersAreRowHeaders()
        {
            FakeTree tree = GridInWindow(table: true);
            int grid = AndroidAccessibilityPatternTests.Grid;
            tree.Roles[grid] = 36;   // Table
            int first = AndroidAccessibilityPatternTests.Cell(1, 0);
            tree.RowHeaders[grid] = new[] { AndroidAccessibilityPatternTests.Cell(0, 0), first };

            string mirror = Mirror(tree);
            Assert.Contains("\"r\":\"table\"", NodeOf(mirror, grid));
            Assert.Contains("\"r\":\"cell\"", NodeOf(mirror, AndroidAccessibilityPatternTests.Cell(1, 1)));
            string rowHeader = NodeOf(mirror, first);
            Assert.Contains("\"r\":\"rowheader\"", rowHeader);
            Assert.Contains("\"ri\":3,\"ci\":1", rowHeader);
        }

        // ---- scrolling ----

        [Fact]
        public void AScrollingPaneCarriesItsPositionAndViewSize()
        {
            FakeTree tree = WindowTree();
            tree.SetChildren(0, 30);
            tree.Roles[30] = 33;
            tree.States[30] = AccessibleState.Scrollable;
            tree.Scroll[30] = (-1, 40, 100, 25, false, true);

            Assert.Contains("\"scr\":1,\"sh\":-1,\"sv\":40,\"szh\":100,\"szv\":25", NodeOf(Mirror(tree), 30));

            tree.Scroll[30] = (12.5, 140, 50, 25, true, true);   // out of range is clamped
            Assert.Contains("\"sh\":12.5,\"sv\":100,\"szh\":50,\"szv\":25", NodeOf(Mirror(tree), 30));

            tree.Scroll[30] = (-1, -1, 100, 100, false, false);
            Assert.DoesNotContain("\"scr\":", NodeOf(Mirror(tree), 30));
        }

        // ---- windows ----

        [Fact]
        public void EveryWindowIsMirroredAndAModalOneIsADialog()
        {
            FakeTree tree = WindowTree();
            tree.Roots[Dialog] = 50;
            tree.Roles[50] = 32;
            tree.Names[50] = "Save changes?";
            tree.States[50] = AccessibleState.Window | AccessibleState.Modal;
            tree.SetChildren(50, 51);
            tree.Roles[51] = 0;
            tree.Names[51] = "OK";

            string mirror = Mirror(tree, Main, Dialog);
            string app = NodeOf(mirror, 0);
            Assert.Contains("\"p\":-1", app);
            Assert.Contains("\"r\":\"\"", app);                   // the page's own window: no role, as before
            Assert.Contains("\"wh\":1", app);
            Assert.DoesNotContain("\"modal\":", app);

            string dialog = NodeOf(mirror, 50);
            Assert.Contains("\"r\":\"dialog\",\"n\":\"Save changes?\"", dialog);
            Assert.Contains("\"wh\":2", dialog);
            Assert.Contains("\"modal\":1", dialog);
            Assert.Contains("\"p\":50", NodeOf(mirror, 51));

            // Only a window's root says which window it is.
            Assert.DoesNotContain("\"wh\":", NodeOf(mirror, 51));
        }

        [Fact]
        public void EscapeClosesOnlyAModalWindow()
        {
            FakeTree tree = WindowTree();
            tree.States[50] = AccessibleState.Window | AccessibleState.Modal;
            Assert.True(BrowserAccessibility.CloseModal(tree, 50));
            Assert.Equal(50, tree.Closed);

            tree.Closed = -1;
            Assert.False(BrowserAccessibility.CloseModal(tree, 0));   // the page's own window
            Assert.False(BrowserAccessibility.CloseModal(tree, 404));
            Assert.Equal(-1, tree.Closed);
        }

        [Fact]
        public void TheRoleOfAPatternOverridesTheControlType()
        {
            FakeTree tree = GridInWindow(table: true);
            Assert.Equal("dialog", BrowserAccessibility.RoleOf(tree, 9, 32, AccessibleState.Window | AccessibleState.Modal, -1, out _, out _));
            Assert.Equal("", BrowserAccessibility.RoleOf(tree, 9, 32, AccessibleState.Window, -1, out _, out _));
            Assert.Equal("button", BrowserAccessibility.RoleOf(tree, 9, 0, AccessibleState.Enabled, -1, out _, out _));
            Assert.Equal("columnheader", BrowserAccessibility.RoleOf(tree, AndroidAccessibilityPatternTests.Header0, 35, default, HeaderRow, out int r, out int c));
            Assert.Equal((1, 1), (r, c));
        }

        [Fact]
        public void TheOldFieldsAreUnchanged()
        {
            FakeTree tree = WindowTree();
            tree.SetChildren(0, 3);
            tree.Roles[3] = 0;
            tree.Names[3] = "Save";
            tree.States[3] = AccessibleState.Enabled | AccessibleState.Focusable;
            tree.Actions[3] = new[] { AccessibleAction.Invoke };
            Assert.Equal("{\"id\":3,\"p\":0,\"r\":\"button\",\"n\":\"Save\",\"x\":0,\"y\":0,\"w\":10,\"h\":10,\"foc\":1,\"act\":1}",
                         NodeOf(Mirror(tree), 3));
        }
    }
}
