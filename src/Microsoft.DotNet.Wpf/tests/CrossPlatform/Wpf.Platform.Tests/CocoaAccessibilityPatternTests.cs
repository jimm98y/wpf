// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The macOS accessibility element's patterns -- text, selection, tables, scrolling, windows --
// driven from the AppKit side.
//
// As in CocoaTextInputTests, the test takes AppKit's place: the element is an Objective-C object,
// so sending it -accessibilityRangeForLine: is exactly what VoiceOver's request turns into, and
// the type encodings and struct ABIs the IMPs were registered with are exercised for real. The tree
// behind it is a fake with the shape of AutomationBridge's answers.
//
// Which attributes VoiceOver is told exist is AppKit's derivation, inside the accessibility
// bridge, from the protocol accessors an element answers narrowed by -isAccessibilitySelectorAllowed:.
// That bridge is not reachable without a running app and a trusted client, so the tests assert the
// half that is ours: the accessor answers, and the selector is allowed exactly where it should be.
//

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using MS.Internal.Interop;
using Xunit;

namespace Wpf.Platform.Tests
{
    [Collection("CocoaAccessibility")]   // AutomationTree.Current is process-wide
    public class CocoaAccessibilityPatternTests
    {
        private const ulong NotFound = long.MaxValue;

        // ---- text ----
        [Fact]
        public void TextNodeReportsItsTextAndSelection()
        {
            var tree = new PatternTree();
            tree.Text[1] = "Hello world";
            tree.Selection[1] = (6, 5);

            WithTree(tree, () =>
            {
                IntPtr e = Element(1);

                Assert.Equal(11, SendLong(e, Sel("accessibilityNumberOfCharacters")));
                Assert.Equal("Hello world", ReadString(Send(e, Sel("accessibilityValue"))));
                Assert.Equal("world", ReadString(Send(e, Sel("accessibilitySelectedText"))));

                NSRange selection = SendRange(e, Sel("accessibilitySelectedTextRange"));
                Assert.Equal(6ul, selection.location);
                Assert.Equal(5ul, selection.length);
            });
        }

        [Fact]
        public void SettingTheSelectedRangeSelectsInTheTree()
        {
            var tree = new PatternTree();
            tree.Text[1] = "Hello world";

            WithTree(tree, () =>
            {
                IntPtr e = Element(1);

                SendVoidRange(e, Sel("setAccessibilitySelectedTextRange:"), Range(2, 3));
                Assert.Equal((2, 3), tree.Selection[1]);

                // A range past the end is clipped rather than passed on: the tree would refuse it,
                // and VoiceOver sends one routinely right after the text shrinks.
                SendVoidRange(e, Sel("setAccessibilitySelectedTextRange:"), Range(9, 50));
                Assert.Equal((9, 2), tree.Selection[1]);
            });
        }

        [Fact]
        public void StringsForRangesAreSubstringsClippedToTheText()
        {
            var tree = new PatternTree();
            tree.Text[1] = "Hello world";

            WithTree(tree, () =>
            {
                IntPtr e = Element(1);

                Assert.Equal("lo w", ReadString(SendPtrRange(e, Sel("accessibilityStringForRange:"), Range(3, 4))));
                Assert.Equal("ld", ReadString(SendPtrRange(e, Sel("accessibilityStringForRange:"), Range(9, 100))));
                Assert.Equal("", ReadString(SendPtrRange(e, Sel("accessibilityStringForRange:"), Range(NotFound, 0))));

                IntPtr attributed = SendPtrRange(e, Sel("accessibilityAttributedStringForRange:"), Range(0, 5));
                Assert.NotEqual(IntPtr.Zero, attributed);
                Assert.Equal("Hello", ReadString(Send(attributed, Sel("string"))));

                NSRange style = SendRangeLong(e, Sel("accessibilityStyleRangeForIndex:"), 4);
                Assert.Equal(0ul, style.location);
                Assert.Equal(11ul, style.length);
            });
        }

        [Fact]
        public void TextNodeOffersEveryTextAttribute()
        {
            var tree = new PatternTree();
            tree.Text[1] = "Hello world";

            WithTree(tree, () =>
            {
                IntPtr e = Element(1);

                // AppKit lists an attribute for an element that answers its accessor AND is allowed
                // it; these are the accessors behind AXNumberOfCharacters ... AXStyleRangeForIndex.
                foreach (string selector in new[]
                {
                    "accessibilityNumberOfCharacters", "accessibilitySelectedText", "accessibilitySelectedTextRange",
                    "accessibilityVisibleCharacterRange", "accessibilityInsertionPointLineNumber",
                    "accessibilityStringForRange:", "accessibilityAttributedStringForRange:", "accessibilityRangeForLine:",
                    "accessibilityLineForIndex:", "accessibilityFrameForRange:", "accessibilityRangeForPosition:",
                    "accessibilityRangeForIndex:", "accessibilityStyleRangeForIndex:",
                    // The selection is the one text attribute a client may change.
                    "setAccessibilitySelectedTextRange:",
                })
                {
                    Assert.True(Allowed(e, selector), selector);
                }
            });
        }

        [Fact]
        public void NodesWithoutAPatternDoNotClaimItsAttributes()
        {
            var tree = new PatternTree();
            tree.Roles[2] = 0;                                   // a Button: no text, no grid
            tree.States[2] = AccessibleState.Enabled;

            WithTree(tree, () =>
            {
                IntPtr e = Element(2);

                Assert.False(SendBoolPtr(e, Sel("isAccessibilitySelectorAllowed:"), Sel("accessibilityNumberOfCharacters")));
                Assert.False(SendBoolPtr(e, Sel("isAccessibilitySelectorAllowed:"), Sel("accessibilityRowCount")));
                Assert.False(SendBoolPtr(e, Sel("isAccessibilitySelectorAllowed:"), Sel("accessibilitySelectedChildren")));
                // ...but anything not gated is left to AppKit.
                Assert.True(SendBoolPtr(e, Sel("isAccessibilitySelectorAllowed:"), Sel("accessibilityLabel")));
                Assert.False(Allowed(e, "accessibilityStringForRange:"));
                Assert.False(Allowed(e, "accessibilityCellForColumn:row:"));
                Assert.False(Allowed(e, "accessibilityRowIndexRange"));
                Assert.False(Allowed(e, "isAccessibilityModal"));
                Assert.False(Allowed(e, "isAccessibilityMinimized"));
                // Nothing to press on it either, so AXPress is not offered.
                Assert.False(Allowed(e, "accessibilityPerformPress"));
                Assert.Empty(Strings(Send(e, Sel("accessibilityActionNames"))));
            });
        }

        [Fact]
        public void PasswordTextIsNotExposed()
        {
            var tree = new PatternTree();
            tree.Text[1] = "secret";
            tree.States[1] = AccessibleState.HasText | AccessibleState.Password;

            WithTree(tree, () =>
            {
                IntPtr e = Element(1);
                Assert.Equal(0, SendLong(e, Sel("accessibilityNumberOfCharacters")));
                Assert.NotEqual("secret", ReadString(Send(e, Sel("accessibilityValue"))));
                Assert.False(SendBoolPtr(e, Sel("isAccessibilitySelectorAllowed:"), Sel("accessibilityStringForRange:")));
            });
        }

        [Fact]
        public void LinesMapToIndexesBothWays()
        {
            var tree = new PatternTree();
            tree.Text[1] = "ab\ncd\nef";                         // lines at 0, 3, 6
            tree.Selection[1] = (4, 0);                          // caret inside "cd"

            WithTree(tree, () =>
            {
                IntPtr e = Element(1);

                Assert.Equal(0, SendLongLong(e, Sel("accessibilityLineForIndex:"), 1));
                Assert.Equal(1, SendLongLong(e, Sel("accessibilityLineForIndex:"), 4));
                Assert.Equal(2, SendLongLong(e, Sel("accessibilityLineForIndex:"), 7));
                Assert.Equal(1, SendLong(e, Sel("accessibilityInsertionPointLineNumber")));

                NSRange line1 = SendRangeLong(e, Sel("accessibilityRangeForLine:"), 1);
                Assert.Equal(3ul, line1.location);
                Assert.Equal(3ul, line1.length);                 // with its break, as the tree reports it

                NSRange line2 = SendRangeLong(e, Sel("accessibilityRangeForLine:"), 2);
                Assert.Equal(6ul, line2.location);
                Assert.Equal(2ul, line2.length);

                Assert.Equal(NotFound, SendRangeLong(e, Sel("accessibilityRangeForLine:"), 3).location);
            });
        }

        [Fact]
        public void LineWalkSurvivesLinesReportedWithoutTheirBreak()
        {
            // A tree whose lines stop short of the break must not trap the walk on the break.
            var tree = new PatternTree { LinesIncludeBreak = false };
            tree.Text[1] = "ab\ncd\n";

            WithTree(tree, () =>
            {
                NSRange line1 = SendRangeLong(Element(1), Sel("accessibilityRangeForLine:"), 1);
                Assert.Equal(3ul, line1.location);
                Assert.Equal(2ul, line1.length);
                Assert.Equal(NotFound, SendRangeLong(Element(1), Sel("accessibilityRangeForLine:"), 9).location);
            });
        }

        [Fact]
        public void RangeForIndexKeepsSurrogatePairsTogether()
        {
            var tree = new PatternTree();
            tree.Text[1] = "a\U0001F600b";                       // a, an emoji (two UTF-16 units), b

            WithTree(tree, () =>
            {
                IntPtr e = Element(1);

                NSRange emoji = SendRangeLong(e, Sel("accessibilityRangeForIndex:"), 2);   // its second half
                Assert.Equal(1ul, emoji.location);
                Assert.Equal(2ul, emoji.length);

                NSRange b = SendRangeLong(e, Sel("accessibilityRangeForIndex:"), 3);
                Assert.Equal(3ul, b.location);
                Assert.Equal(1ul, b.length);
            });
        }

        [Fact]
        public void VisibleRangeFallsBackToTheWholeText()
        {
            var tree = new PatternTree();
            tree.Text[1] = "Hello world";

            WithTree(tree, () =>
            {
                IntPtr e = Element(1);
                NSRange all = SendRange(e, Sel("accessibilityVisibleCharacterRange"));
                Assert.Equal(0ul, all.location);
                Assert.Equal(11ul, all.length);

                tree.Visible[1] = (2, 4);
                NSRange some = SendRange(e, Sel("accessibilityVisibleCharacterRange"));
                Assert.Equal(2ul, some.location);
                Assert.Equal(4ul, some.length);
            });
        }

        [Fact]
        public void RangeBoundsAreFlippedLikeTheElementFrame()
        {
            var tree = new PatternTree();
            tree.Text[1] = "Hello world";
            tree.RangeBounds = (200, 100, 40, 20);

            WithTree(tree, () =>
            {
                NSRect rect = SendRectRange(Element(1), Sel("accessibilityFrameForRange:"), Range(0, 5));

                CocoaWindow.ConvertScreenPixelsToCocoaPoints(200, 100, 40, 20,
                    out double x, out double y, out double w, out double h);
                Assert.Equal(x, rect.x, 6);
                Assert.Equal(y, rect.y, 6);
                Assert.Equal(w, rect.width, 6);
                Assert.Equal(h, rect.height, 6);
                Assert.Equal((0, 5), tree.LastBoundsRange);
            });
        }

        [Fact]
        public void RangeForPositionHitTestsInScreenPixels()
        {
            var tree = new PatternTree();
            tree.Text[1] = "Hello world";
            tree.OffsetAtPoint = 4;

            WithTree(tree, () =>
            {
                NSRange r = SendRangePoint(Element(1), Sel("accessibilityRangeForPosition:"), new CGPoint { x = 120, y = 300 });
                Assert.Equal(4ul, r.location);
                Assert.Equal(1ul, r.length);

                CocoaWindow.ConvertCocoaPointsToScreenPixels(120, 300, out double px, out double py);
                Assert.Equal(px, tree.LastPoint.X, 6);
                Assert.Equal(py, tree.LastPoint.Y, 6);

                tree.OffsetAtPoint = -1;
                Assert.Equal(NotFound, SendRangePoint(Element(1), Sel("accessibilityRangeForPosition:"), default).location);
            });
        }

        // ---- selection ----

        [Fact]
        public void SelectedChildrenAreTheTreesSelectedItems()
        {
            var tree = new PatternTree();
            tree.Roles[10] = 8;                                   // List
            tree.States[10] = AccessibleState.SelectionContainer | AccessibleState.MultiSelectable;
            tree.SelectedIds[10] = new[] { 12, 14 };

            WithTree(tree, () =>
            {
                IntPtr e = Element(10);
                IntPtr selected = Send(e, Sel("accessibilitySelectedChildren"));
                Assert.Equal(2, (long)Send(selected, Sel("count")));
                Assert.Equal(Element(12), SendPtrLong(selected, Sel("objectAtIndex:"), 0));
                Assert.Equal(Element(14), SendPtrLong(selected, Sel("objectAtIndex:"), 1));

                // A list is not a table: no AXSelectedRows on it.
                Assert.False(SendBoolPtr(e, Sel("isAccessibilitySelectorAllowed:"), Sel("accessibilitySelectedRows")));
            });
        }

        [Fact]
        public void SettingSelectedChildrenSelectsTheFirstAndAddsTheRest()
        {
            var tree = new PatternTree();
            tree.Roles[10] = 8;
            tree.States[10] = AccessibleState.SelectionContainer | AccessibleState.MultiSelectable;

            WithTree(tree, () =>
            {
                IntPtr array = Send(objc_getClass("NSMutableArray"), Sel("array"));
                SendVoidPtr(array, Sel("addObject:"), Element(12));
                SendVoidPtr(array, Sel("addObject:"), NSString("not an element"));
                SendVoidPtr(array, Sel("addObject:"), Element(14));

                SendVoidPtr(Element(10), Sel("setAccessibilitySelectedChildren:"), array);

                Assert.Equal(new[] { (12, AccessibleAction.Select), (14, AccessibleAction.ToggleSelection) }, tree.ActionLog);
            });
        }

        // ---- tables ----

        [Fact]
        public void TableAnswersCountsRowsHeadersAndCells()
        {
            var tree = new PatternTree();
            tree.Roles[20] = 28;                                  // DataGrid
            tree.States[20] = AccessibleState.Grid | AccessibleState.Table | AccessibleState.SelectionContainer;
            tree.Grid[20] = (3, 2);
            tree.Children[20] = new[] { 21, 22, 23, 24 };
            tree.Roles[21] = 34;                                  // the header presenter: not a row
            tree.Roles[22] = tree.Roles[23] = tree.Roles[24] = 29; // DataItem
            tree.ColumnHeaders[20] = new[] { 30, 31 };
            tree.SelectedIds[20] = new[] { 23 };
            tree.Cells[(20, 1, 0)] = 40;

            WithTree(tree, () =>
            {
                IntPtr e = Element(20);

                Assert.Equal("AXTable", ReadString(Send(e, Sel("accessibilityRole"))));
                Assert.Equal(3, SendLong(e, Sel("accessibilityRowCount")));
                Assert.Equal(2, SendLong(e, Sel("accessibilityColumnCount")));

                IntPtr rows = Send(e, Sel("accessibilityRows"));
                Assert.Equal(3, (long)Send(rows, Sel("count")));
                Assert.Equal(Element(22), SendPtrLong(rows, Sel("objectAtIndex:"), 0));

                IntPtr headers = Send(e, Sel("accessibilityColumnHeaderUIElements"));
                Assert.Equal(2, (long)Send(headers, Sel("count")));
                Assert.Equal(Element(31), SendPtrLong(headers, Sel("objectAtIndex:"), 1));

                IntPtr selectedRows = Send(e, Sel("accessibilitySelectedRows"));
                Assert.Equal(Element(23), SendPtrLong(selectedRows, Sel("objectAtIndex:"), 0));

                // Column first, as AppKit names the selector; the tree takes row first.
                Assert.Equal(Element(40), SendPtrLongLong(e, Sel("accessibilityCellForColumn:row:"), 0, 1));
                Assert.Equal(IntPtr.Zero, SendPtrLongLong(e, Sel("accessibilityCellForColumn:row:"), 1, 0));

                foreach (string selector in new[] { "accessibilityRowCount", "accessibilityColumnCount", "accessibilityRows",
                                                    "accessibilityColumnHeaderUIElements", "accessibilitySelectedRows",
                                                    "setAccessibilitySelectedRows:", "accessibilityCellForColumn:row:",
                                                    "accessibilitySelectedChildren" })
                {
                    Assert.True(Allowed(e, selector), selector);
                }
                // The table is not itself a cell.
                Assert.False(Allowed(e, "accessibilityRowIndexRange"));
            });
        }

        [Fact]
        public void CellReportsItsRowAndColumnRanges()
        {
            var tree = new PatternTree();
            tree.Roles[40] = 25;                                  // Custom, as a DataGridCell is
            tree.States[40] = AccessibleState.GridItem;
            tree.GridItems[40] = (row: 4, column: 2, rowSpan: 1, columnSpan: 3);

            WithTree(tree, () =>
            {
                IntPtr e = Element(40);

                Assert.Equal("AXCell", ReadString(Send(e, Sel("accessibilityRole"))));

                NSRange rows = SendRange(e, Sel("accessibilityRowIndexRange"));
                Assert.Equal(4ul, rows.location);
                Assert.Equal(1ul, rows.length);

                NSRange columns = SendRange(e, Sel("accessibilityColumnIndexRange"));
                Assert.Equal(2ul, columns.location);
                Assert.Equal(3ul, columns.length);
            });
        }

        [Fact]
        public void OutlineRowsIncludeExpandedDescendants()
        {
            var tree = new PatternTree();
            tree.Roles[50] = 23;                                  // Tree
            tree.Children[50] = new[] { 51, 52 };
            tree.Roles[51] = tree.Roles[52] = tree.Roles[53] = tree.Roles[54] = 24;  // TreeItem
            tree.Children[51] = new[] { 53 };                     // expanded
            tree.Children[52] = new[] { 54 };
            tree.States[52] = AccessibleState.Collapsed;          // its child is not on screen

            WithTree(tree, () =>
            {
                IntPtr rows = Send(Element(50), Sel("accessibilityRows"));
                Assert.Equal(3, (long)Send(rows, Sel("count")));
                Assert.Equal(Element(51), SendPtrLong(rows, Sel("objectAtIndex:"), 0));
                Assert.Equal(Element(53), SendPtrLong(rows, Sel("objectAtIndex:"), 1));
                Assert.Equal(Element(52), SendPtrLong(rows, Sel("objectAtIndex:"), 2));
            });
        }

        [Fact]
        public void RolesFollowPatternsOnlyWhereTheTypeSaysNothing()
        {
            Assert.Equal("AXCell", CocoaRoles.RoleFor(25, AccessibleState.GridItem));         // Custom
            Assert.Equal("AXScrollArea", CocoaRoles.RoleFor(33, AccessibleState.Scrollable)); // Pane
            Assert.Equal("AXScrollArea", CocoaRoles.RoleFor(26, AccessibleState.Scrollable)); // Group
            Assert.Equal("AXList", CocoaRoles.RoleFor(8, AccessibleState.Scrollable));        // a List that scrolls
            Assert.Equal("AXTable", CocoaRoles.RoleFor(28, AccessibleState.Scrollable));      // a DataGrid that scrolls
            Assert.Equal("AXTextField", CocoaRoles.RoleFor(4, AccessibleState.GridItem));     // an Edit in a cell
            Assert.Equal("AXRow", CocoaRoles.RoleFor(29, AccessibleState.None));              // DataItem
            Assert.Equal("AXTable", CocoaRoles.RoleFor(36, AccessibleState.None));            // Table
        }

        // ---- scrolling ----

        [Fact]
        public void ScrollAreaOffersPageActionsThatScrollTheTree()
        {
            var tree = new PatternTree();
            tree.Roles[60] = 33;                                  // Pane
            tree.States[60] = AccessibleState.Scrollable;
            tree.Scrollable[60] = (horizontal: true, vertical: true);

            WithTree(tree, () =>
            {
                IntPtr e = Element(60);
                Assert.Equal("AXScrollArea", ReadString(Send(e, Sel("accessibilityRole"))));

                List<string> actions = Strings(Send(e, Sel("accessibilityActionNames")));
                Assert.Contains("AXScrollUpByPage", actions);
                Assert.Contains("AXScrollDownByPage", actions);
                Assert.Contains("AXScrollLeftByPage", actions);
                Assert.Contains("AXScrollRightByPage", actions);

                SendVoidPtr(e, Sel("accessibilityPerformAction:"), NSString("AXScrollUpByPage"));
                SendVoidPtr(e, Sel("accessibilityPerformAction:"), NSString("AXScrollDownByPage"));
                SendVoidPtr(e, Sel("accessibilityPerformAction:"), NSString("AXScrollLeftByPage"));
                SendVoidPtr(e, Sel("accessibilityPerformAction:"), NSString("AXScrollRightByPage"));

                // ScrollAmount: 0 large decrement, 2 none, 3 large increment -- (horizontal, vertical).
                Assert.Equal(new[] { (60, 2, 0), (60, 2, 3), (60, 0, 2), (60, 3, 2) }, tree.ScrollLog);
            });
        }

        [Fact]
        public void OnlyTheDirectionsThatScrollAreOffered()
        {
            var tree = new PatternTree();
            tree.Roles[60] = 33;
            tree.States[60] = AccessibleState.Scrollable;
            tree.Scrollable[60] = (horizontal: false, vertical: true);

            WithTree(tree, () =>
            {
                List<string> actions = Strings(Send(Element(60), Sel("accessibilityActionNames")));
                Assert.Contains("AXScrollDownByPage", actions);
                Assert.DoesNotContain("AXScrollLeftByPage", actions);
            });
        }

        [Fact]
        public void ScrollToVisibleScrollsTheItemIntoView()
        {
            var tree = new PatternTree();
            tree.Roles[61] = 7;                                   // ListItem
            tree.States[61] = AccessibleState.ScrollItem;
            tree.Actions[61] = new[] { AccessibleAction.Select };

            WithTree(tree, () =>
            {
                IntPtr e = Element(61);
                List<string> actions = Strings(Send(e, Sel("accessibilityActionNames")));
                Assert.Contains("AXScrollToVisible", actions);
                // Once the element names its actions it must name them all: AppKit no longer
                // derives AXPress from -accessibilityPerformPress for it.
                Assert.Contains("AXPress", actions);

                SendVoidPtr(e, Sel("accessibilityPerformAction:"), NSString("AXScrollToVisible"));
                Assert.Equal(new[] { 61 }, tree.ScrolledIntoView);

                // A standard action still reaches the protocol method through AppKit.
                SendVoidPtr(e, Sel("accessibilityPerformAction:"), NSString("AXPress"));
                Assert.Equal(new[] { (61, AccessibleAction.Select) }, tree.ActionLog);
            });
        }

        // ---- windows ----

        [Fact]
        public void WindowReportsModalAndMinimized()
        {
            var tree = new PatternTree();
            tree.Roles[70] = 32;                                  // Window
            tree.States[70] = AccessibleState.Window | AccessibleState.Modal;
            tree.Windows[70] = (visualState: 0, canMinimize: true);

            WithTree(tree, () =>
            {
                IntPtr e = Element(70);

                Assert.True(SendBool(e, Sel("isAccessibilityModal")));
                Assert.False(SendBool(e, Sel("isAccessibilityMinimized")));

                SendVoidBool(e, Sel("setAccessibilityMinimized:"), true);
                Assert.Equal(2, tree.Windows[70].visualState);
                Assert.True(SendBool(e, Sel("isAccessibilityMinimized")));

                SendVoidBool(e, Sel("setAccessibilityMinimized:"), false);
                Assert.Equal(0, tree.Windows[70].visualState);

                Assert.True(Allowed(e, "isAccessibilityModal"));
                Assert.True(Allowed(e, "isAccessibilityMinimized"));
                Assert.True(SendBoolPtr(e, Sel("isAccessibilitySelectorAllowed:"), Sel("setAccessibilityMinimized:")));

                // A window that cannot be minimized reports the state but does not offer to change it.
                tree.Windows[70] = (visualState: 0, canMinimize: false);
                Assert.False(SendBoolPtr(e, Sel("isAccessibilitySelectorAllowed:"), Sel("setAccessibilityMinimized:")));
            });
        }

        // ---- through AppKit's accessibility entry points ----
        //
        // NSAccessibilityEntryPoint* are the functions AppKit's accessibility server calls for every
        // request a client makes of an element: the derivation from protocol accessors, the
        // -isAccessibilitySelectorAllowed: filter and the original-API fallbacks all happen inside
        // them. They are exported but undocumented, so a macOS without them skips these tests
        // rather than failing; the direct-selector tests above still cover our half.

        [Fact]
        public void AppKitListsEachNodesAttributesFromItsPatterns()
        {
            var tree = new PatternTree();
            tree.Text[1] = "Hello world";
            tree.Roles[2] = 0;                                    // Button
            tree.States[2] = AccessibleState.Enabled;
            tree.Roles[20] = 28;                                  // DataGrid
            tree.States[20] = AccessibleState.Grid | AccessibleState.Table | AccessibleState.SelectionContainer;
            tree.Roles[40] = 25;
            tree.States[40] = AccessibleState.GridItem;
            tree.Roles[70] = 32;
            tree.States[70] = AccessibleState.Window | AccessibleState.Modal;
            tree.Windows[70] = (visualState: 0, canMinimize: true);

            WithTree(tree, () =>
            {
                var names = EntryPoint<Func1>("NSAccessibilityEntryPointAttributeNames");

                List<string> text = Strings(names(Element(1)));
                foreach (string a in new[] { "AXValue", "AXNumberOfCharacters", "AXSelectedText", "AXSelectedTextRange",
                                             "AXVisibleCharacterRange", "AXInsertionPointLineNumber" })
                {
                    Assert.Contains(a, text);
                }
                Assert.DoesNotContain("AXRowCount", text);

                List<string> button = Strings(names(Element(2)));
                foreach (string a in new[] { "AXNumberOfCharacters", "AXSelectedText", "AXRowCount", "AXSelectedChildren", "AXModal", "AXMinimized",
                                             "AXMinValue", "AXMaxValue" })
                {
                    Assert.DoesNotContain(a, button);
                }

                List<string> table = Strings(names(Element(20)));
                foreach (string a in new[] { "AXRowCount", "AXColumnCount", "AXRows", "AXColumnHeaderUIElements", "AXSelectedRows", "AXSelectedChildren" })
                {
                    Assert.Contains(a, table);
                }

                List<string> cell = Strings(names(Element(40)));
                Assert.Contains("AXRowIndexRange", cell);
                Assert.Contains("AXColumnIndexRange", cell);

                List<string> window = Strings(names(Element(70)));
                Assert.Contains("AXModal", window);
                Assert.Contains("AXMinimized", window);
            });
        }

        [Fact]
        public void AppKitAnswersParameterizedAttributesThroughTheAccessors()
        {
            var tree = new PatternTree();
            tree.Text[1] = "Hello world";
            tree.Roles[20] = 28;
            tree.States[20] = AccessibleState.Grid;
            tree.Grid[20] = (3, 2);
            tree.Cells[(20, 1, 0)] = 40;

            WithTree(tree, () =>
            {
                var parameterized = EntryPoint<Func1>("NSAccessibilityEntryPointParameterizedAttributeNames");
                var valueFor = EntryPoint<Func3>("NSAccessibilityEntryPointValueForAttributeWithParameter");

                List<string> names = Strings(parameterized(Element(1)));
                foreach (string a in new[] { "AXStringForRange", "AXAttributedStringForRange", "AXRangeForLine", "AXLineForIndex",
                                             "AXBoundsForRange", "AXRangeForPosition", "AXRangeForIndex", "AXStyleRangeForIndex" })
                {
                    Assert.Contains(a, names);
                }

                IntPtr range = SendPtrRange(objc_getClass("NSValue"), Sel("valueWithRange:"), Range(6, 5));
                Assert.Equal("world", ReadString(valueFor(Element(1), NSString("AXStringForRange"), range)));

                // [column, row], as the attribute's parameter is defined.
                IntPtr columnRow = Send(objc_getClass("NSMutableArray"), Sel("array"));
                SendVoidPtr(columnRow, Sel("addObject:"), SendPtrLong(objc_getClass("NSNumber"), Sel("numberWithLong:"), 0));
                SendVoidPtr(columnRow, Sel("addObject:"), SendPtrLong(objc_getClass("NSNumber"), Sel("numberWithLong:"), 1));
                Assert.Equal(Element(40), valueFor(Element(20), NSString("AXCellForColumnAndRow"), columnRow));
            });
        }

        [Fact]
        public void AppKitOffersSettingOnlyWhereTheNodeAllowsIt()
        {
            var tree = new PatternTree();
            tree.Text[1] = "Hello world";
            tree.Roles[2] = 0;
            tree.Roles[70] = 32;
            tree.States[70] = AccessibleState.Window;
            tree.Windows[70] = (visualState: 0, canMinimize: false);

            WithTree(tree, () =>
            {
                var settable = EntryPoint<BoolFunc2>("NSAccessibilityEntryPointIsAttributeSettable");

                Assert.NotEqual(0, settable(Element(1), NSString("AXSelectedTextRange")));
                Assert.Equal(0, settable(Element(2), NSString("AXSelectedTextRange")));

                Assert.Equal(0, settable(Element(70), NSString("AXMinimized")));
                tree.Windows[70] = (visualState: 0, canMinimize: true);
                Assert.NotEqual(0, settable(Element(70), NSString("AXMinimized")));
            });
        }

        [Fact]
        public void AppKitListsAndPerformsTheScrollActions()
        {
            var tree = new PatternTree();
            tree.Roles[60] = 33;
            tree.States[60] = AccessibleState.Scrollable;
            tree.Scrollable[60] = (horizontal: false, vertical: true);
            tree.Roles[61] = 7;
            tree.States[61] = AccessibleState.ScrollItem;
            tree.Actions[61] = new[] { AccessibleAction.Select };
            tree.Text[1] = "Hello world";

            WithTree(tree, () =>
            {
                var actions = EntryPoint<Func1>("NSAccessibilityEntryPointActionNames");
                var perform = EntryPoint<VoidFunc2>("NSAccessibilityEntryPointPerformAction");

                Assert.Equal(new[] { "AXScrollUpByPage", "AXScrollDownByPage" }, Strings(actions(Element(60))));
                Assert.Equal(new[] { "AXPress", "AXScrollToVisible" }, Strings(actions(Element(61))));
                Assert.Empty(Strings(actions(Element(1))));       // plain text: nothing to press

                perform(Element(60), NSString("AXScrollDownByPage"));
                Assert.Equal(new[] { (60, 2, 3) }, tree.ScrollLog);

                perform(Element(61), NSString("AXPress"));
                perform(Element(61), NSString("AXScrollToVisible"));
                Assert.Equal(new[] { (61, AccessibleAction.Select) }, tree.ActionLog);
                Assert.Equal(new[] { 61 }, tree.ScrolledIntoView);
            });
        }

        // ---- notifications and robustness ----

        [Fact]
        public void PatternChangesMapToAppKitsNotifications()
        {
            var tree = new PatternTree();
            tree.Roles[20] = 28;                                  // DataGrid
            tree.Roles[10] = 8;                                   // List

            static AutomationChange Change(AutomationChangeKind kind, int id) => new(kind, id, IntPtr.Zero);

            Assert.Equal("AXValueChanged", CocoaAccessibility.NotificationFor(tree, Change(AutomationChangeKind.TextChanged, 1)));
            Assert.Equal("AXSelectedTextChanged", CocoaAccessibility.NotificationFor(tree, Change(AutomationChangeKind.TextSelectionChanged, 1)));
            Assert.Equal("AXSelectedRowsChanged", CocoaAccessibility.NotificationFor(tree, Change(AutomationChangeKind.SelectionContainerChanged, 20)));
            Assert.Equal("AXSelectedChildrenChanged", CocoaAccessibility.NotificationFor(tree, Change(AutomationChangeKind.SelectionContainerChanged, 10)));
            Assert.Equal("AXLayoutChanged", CocoaAccessibility.NotificationFor(tree, Change(AutomationChangeKind.ScrollChanged, 60)));
            Assert.Equal("AXSelectedChildrenChanged", CocoaAccessibility.NotificationFor(null, Change(AutomationChangeKind.SelectionContainerChanged, 20)));
        }

        [Fact]
        public void PatternAccessorsAnswerWithNoTreeOrAStaleNode()
        {
            IntPtr stale = Element(999);
            Assert.Equal(0, SendLong(stale, Sel("accessibilityNumberOfCharacters")));

            AutomationTree.Current = null;
            Assert.Equal(0, SendLong(stale, Sel("accessibilityNumberOfCharacters")));
            Assert.Equal(NotFound, SendRangeLong(stale, Sel("accessibilityRangeForLine:"), 0).location);
            Assert.Equal(IntPtr.Zero, SendPtrLongLong(stale, Sel("accessibilityCellForColumn:row:"), 0, 0));
            SendVoidPtr(stale, Sel("accessibilityPerformAction:"), NSString("AXScrollUpByPage"));
            SendVoidRange(stale, Sel("setAccessibilitySelectedTextRange:"), Range(0, 0));
        }

        // ---- harness ---------------------------------------------------------------------------

        private static IntPtr Element(int id)
        {
            Assert.SkipUnless(OperatingSystem.IsMacOS(), "the NSAccessibility backend is macOS-only");
            IntPtr element = CocoaAccessibility.ElementForTest(id);
            Assert.SkipWhen(element == IntPtr.Zero, "AppKit is not loadable in this process");
            return element;
        }

        private static void WithTree(PatternTree tree, Action test)
        {
            Assert.SkipUnless(OperatingSystem.IsMacOS(), "the NSAccessibility backend is macOS-only");
            AutomationTree.Current = tree;
            try { test(); }
            finally { AutomationTree.Current = null; }
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr Func1(IntPtr element);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr Func3(IntPtr element, IntPtr attribute, IntPtr parameter);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate byte BoolFunc2(IntPtr element, IntPtr attribute);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void VoidFunc2(IntPtr element, IntPtr action);

        private static T EntryPoint<T>(string name) where T : Delegate
        {
            Assert.SkipUnless(OperatingSystem.IsMacOS(), "the NSAccessibility backend is macOS-only");
            IntPtr appKit = NativeLibrary.Load("/System/Library/Frameworks/AppKit.framework/AppKit");
            Assert.SkipUnless(NativeLibrary.TryGetExport(appKit, name, out IntPtr function), $"AppKit does not export {name} on this macOS");
            return Marshal.GetDelegateForFunctionPointer<T>(function);
        }

        private static bool Allowed(IntPtr element, string selector)
            => SendBoolPtr(element, Sel("isAccessibilitySelectorAllowed:"), Sel(selector));

        private static NSRange Range(ulong location, ulong length) => new NSRange { location = location, length = length };

        private static List<string> Strings(IntPtr array)
        {
            var list = new List<string>();
            if (array == IntPtr.Zero) return list;
            long count = (long)Send(array, Sel("count"));
            for (long i = 0; i < count; i++) list.Add(ReadString(SendPtrLong(array, Sel("objectAtIndex:"), i)));
            return list;
        }

        private static IntPtr NSString(string value)
        {
            IntPtr bytes = Marshal.StringToCoTaskMemUTF8(value);
            try { return SendPtrPtr(objc_getClass("NSString"), Sel("stringWithUTF8String:"), bytes); }
            finally { Marshal.FreeCoTaskMem(bytes); }
        }

        private static string ReadString(IntPtr nsString)
        {
            if (nsString == IntPtr.Zero) return null;
            IntPtr utf8 = Send(nsString, Sel("UTF8String"));
            return utf8 == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(utf8);
        }

        private const string ObjC = "/usr/lib/libobjc.A.dylib";

        private static IntPtr Sel(string name) => sel_registerName(name);

        [DllImport(ObjC)] private static extern IntPtr objc_getClass(string name);
        [DllImport(ObjC)] private static extern IntPtr sel_registerName(string name);

        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr r, IntPtr s);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern long SendLong(IntPtr r, IntPtr s);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern long SendLongLong(IntPtr r, IntPtr s, long a);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendPtrPtr(IntPtr r, IntPtr s, IntPtr a);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendPtrLong(IntPtr r, IntPtr s, long a);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendPtrLongLong(IntPtr r, IntPtr s, long a, long b);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendPtrRange(IntPtr r, IntPtr s, NSRange a);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendVoidPtr(IntPtr r, IntPtr s, IntPtr a);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendVoidRange(IntPtr r, IntPtr s, NSRange a);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendVoidBool(IntPtr r, IntPtr s, [MarshalAs(UnmanagedType.I1)] bool a);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] [return: MarshalAs(UnmanagedType.I1)] private static extern bool SendBool(IntPtr r, IntPtr s);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] [return: MarshalAs(UnmanagedType.I1)] private static extern bool SendBoolPtr(IntPtr r, IntPtr s, IntPtr a);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern NSRange SendRange(IntPtr r, IntPtr s);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern NSRange SendRangeLong(IntPtr r, IntPtr s, long a);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern NSRange SendRangePoint(IntPtr r, IntPtr s, CGPoint a);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern NSRect SendRectRange(IntPtr r, IntPtr s, NSRange a);

        [StructLayout(LayoutKind.Sequential)]
        private struct NSRange { public ulong location; public ulong length; }

        [StructLayout(LayoutKind.Sequential)]
        private struct CGPoint { public double x; public double y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct NSRect { public double x; public double y; public double width; public double height; }

        /// <summary>
        /// A tree with the pattern members answered from dictionaries, shaped like AutomationBridge's
        /// answers: lines by hard breaks, with the break included unless told otherwise.
        /// </summary>
        private sealed class PatternTree : IAutomationTreeSource
        {
            internal readonly Dictionary<int, int> Roles = new();
            internal readonly Dictionary<int, AccessibleState> States = new();
            internal readonly Dictionary<int, AccessibleAction[]> Actions = new();
            internal readonly Dictionary<int, int[]> Children = new();
            internal readonly Dictionary<int, string> Text = new();
            internal readonly Dictionary<int, (int Start, int Length)> Selection = new();
            internal readonly Dictionary<int, (int Start, int Length)> Visible = new();
            internal readonly Dictionary<int, int[]> SelectedIds = new();
            internal readonly Dictionary<int, (int Rows, int Columns)> Grid = new();
            internal readonly Dictionary<(int Grid, int Row, int Column), int> Cells = new();
            internal readonly Dictionary<int, (int row, int column, int rowSpan, int columnSpan)> GridItems = new();
            internal readonly Dictionary<int, int[]> ColumnHeaders = new();
            internal readonly Dictionary<int, (bool horizontal, bool vertical)> Scrollable = new();
            internal readonly Dictionary<int, (int visualState, bool canMinimize)> Windows = new();

            internal bool LinesIncludeBreak = true;
            internal (double X, double Y, double W, double H) RangeBounds;
            internal (int Start, int Length) LastBoundsRange;
            internal int OffsetAtPoint = -1;
            internal (double X, double Y) LastPoint;

            internal readonly List<(int, AccessibleAction)> ActionLog = new();
            internal readonly List<(int, int, int)> ScrollLog = new();
            internal readonly List<int> ScrolledIntoView = new();

            public int GetRootId(IntPtr windowHandle) => 0;
            public int[] GetChildIds(int id) => Children.TryGetValue(id, out int[] c) ? c : Array.Empty<int>();
            public int GetParentId(int id) => IAutomationTreeSource.InvalidNode;
            public int GetRole(int id) => Roles.TryGetValue(id, out int r) ? r : (Text.ContainsKey(id) ? 4 : -1);
            public string GetName(int id) => string.Empty;
            public string GetAutomationId(int id) => string.Empty;
            public string GetHelpText(int id) => string.Empty;
            public string GetValue(int id) => string.Empty;
            public bool TryGetRange(int id, out double value, out double minimum, out double maximum) { value = minimum = maximum = 0; return false; }

            public AccessibleState GetState(int id)
                => States.TryGetValue(id, out AccessibleState s) ? s : (Text.ContainsKey(id) ? AccessibleState.HasText : default);

            public bool TryGetBounds(int id, out double x, out double y, out double width, out double height) { x = y = width = height = 0; return false; }
            public int HitTest(IntPtr windowHandle, double screenX, double screenY) => IAutomationTreeSource.InvalidNode;
            public int GetFocusedId(IntPtr windowHandle) => IAutomationTreeSource.InvalidNode;
            public bool SupportsAction(int id, AccessibleAction action) => Actions.TryGetValue(id, out var a) && Array.IndexOf(a, action) >= 0;

            public bool DoAction(int id, AccessibleAction action)
            {
                ActionLog.Add((id, action));
                return true;
            }

            public bool SetValue(int id, string value) => false;
            public bool SetFocus(int id) => false;

            public bool TryGetText(int id, out string text)
            {
                if (Text.TryGetValue(id, out text)) return true;
                text = string.Empty;
                return false;
            }

            public bool TryGetTextSelection(int id, out int start, out int length)
            {
                (start, length) = Selection.TryGetValue(id, out var s) ? s : (0, 0);
                return Text.ContainsKey(id);
            }

            public bool SetTextSelection(int id, int start, int length)
            {
                Selection[id] = (start, length);
                return true;
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
                if (!Text.TryGetValue(id, out string text)) return false;

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

            public int[] GetSelectedIds(int id) => SelectedIds.TryGetValue(id, out int[] s) ? s : Array.Empty<int>();

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
                gridId = IAutomationTreeSource.InvalidNode;
                return known;
            }

            public int[] GetColumnHeaderIds(int id) => ColumnHeaders.TryGetValue(id, out int[] h) ? h : Array.Empty<int>();

            public bool TryGetScroll(int id, out double horizontalPercent, out double verticalPercent, out double horizontalViewSize,
                out double verticalViewSize, out bool horizontallyScrollable, out bool verticallyScrollable)
            {
                horizontalPercent = verticalPercent = 0; horizontalViewSize = verticalViewSize = 50;
                bool known = Scrollable.TryGetValue(id, out var s);
                (horizontallyScrollable, verticallyScrollable) = s;
                return known;
            }

            public bool Scroll(int id, int horizontalAmount, int verticalAmount)
            {
                ScrollLog.Add((id, horizontalAmount, verticalAmount));
                return true;
            }

            public bool ScrollIntoView(int id)
            {
                ScrolledIntoView.Add(id);
                return true;
            }

            public bool TryGetWindow(int id, out int visualState, out bool canMinimize, out bool canMaximize, out bool isTopmost)
            {
                bool known = Windows.TryGetValue(id, out var w);
                (visualState, canMinimize) = w;
                canMaximize = isTopmost = false;
                return known;
            }

            public bool SetWindowVisualState(int id, int visualState)
            {
                if (!Windows.TryGetValue(id, out var w)) return false;
                Windows[id] = (visualState, w.canMinimize);
                return true;
            }
        }
    }
}
