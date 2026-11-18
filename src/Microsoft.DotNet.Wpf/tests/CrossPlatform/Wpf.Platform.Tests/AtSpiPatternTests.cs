// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The Linux backend's patterns -- text, selection, tables, windows -- without a bus.
//
// AT-SPI's half of each is a D-Bus method that reads its arguments, asks the tree and writes a
// reply; the reading and writing need libdbus and a client, and this machine has neither. What can
// go wrong without either is the arithmetic in between: offsets in characters rather than UTF-16,
// the word and line a client is told it is on, which item "the second selected child" is, which
// cell index 6 is, what an edit was. Those are pure functions over a fake tree, so they are tested
// here; the D-Bus shapes are what eng/check-atspi-constants.py and a live Orca session are for.
//

using System;
using System.Collections.Generic;
using MS.Internal.Interop;
using MS.Internal.Interop.Wayland;
using Xunit;

namespace Wpf.Platform.Tests
{
    public class AtSpiPatternTests
    {
        private const string Emoji = "\U0001F600";   // one character, two UTF-16 code units

        // ---- key bindings ----

        [Fact]
        public void KeyNamesAreWrittenAsGtkAccelerators()
        {
            Assert.Equal("<Alt>f", AtSpiBridge.KeyName("Alt+F"));
            Assert.Equal("<Control><Shift>o", AtSpiBridge.KeyName("Ctrl+Shift+O"));
            Assert.Equal("<Control>F5", AtSpiBridge.KeyName("Ctrl+F5"));
            Assert.Equal("s", AtSpiBridge.KeyName("S"));          // a drop-down's mnemonic: the letter alone
            Assert.Equal("<Control>plus", AtSpiBridge.KeyName("Ctrl++"));
            Assert.Equal(string.Empty, AtSpiBridge.KeyName(string.Empty));
        }

        [Fact]
        public void TheKeyBindingIsMnemonicSequenceAndShortcut()
        {
            var tree = new PatternTree();
            tree.Keys[1] = ("S", "Ctrl+S");     // a drop-down menu item
            tree.Roles[1] = 11;
            tree.Keys[2] = ("Alt+O", "");
            tree.Keys[4] = ("O", "");           // WPF's button: the letter alone, pressed with Alt
            Assert.Equal("s;;<Control>s", AtSpiBridge.KeyBindingOf(tree, 1));       // a menu item's letter stays bare
            Assert.Equal("<Alt>o", AtSpiBridge.KeyBindingOf(tree, 2));
            Assert.Equal(string.Empty, AtSpiBridge.KeyBindingOf(tree, 3));
            Assert.Equal("<Alt>o", AtSpiBridge.KeyBindingOf(tree, 4));
        }

        [Fact]
        public void AGridItemIsACell_EvenWhenItsTypeIsARows()
        {
            // a WinForms DataGridView cell: DataItem with the grid-item pattern; a WPF DataGrid row: DataItem alone
            Assert.Equal(AtSpiRoles.RoleNameFor(29, AccessibleState.GridItem), "table cell");
            Assert.Equal(AtSpiRoles.RoleNameFor(29, AccessibleState.None), "table row");
        }

        // ---- UTF-16 and characters ----

        [Fact]
        public void OffsetsAreCharactersNotUtf16()
        {
            string text = "a" + Emoji + "b";   // UTF-16: a, high, low, b

            Assert.Equal(3, AtSpiText.CharacterCount(text));
            Assert.Equal(2, AtSpiText.ToCodePoints(text, 3));   // before the b
            Assert.Equal(3, AtSpiText.ToCodePoints(text, 4));
            Assert.Equal(3, AtSpiText.ToUtf16(text, 2));
            Assert.Equal(4, AtSpiText.ToUtf16(text, 3));

            // Between the halves of a pair is the pair's own offset, never the character after it.
            Assert.Equal(1, AtSpiText.ToCodePoints(text, 2));

            // Clamped, and -1 is the end.
            Assert.Equal(4, AtSpiText.ToUtf16(text, 99));
            Assert.Equal(4, AtSpiText.ToUtf16(text, -1));
            Assert.Equal(3, AtSpiText.ToCodePoints(text, 99));
            Assert.Equal(0, AtSpiText.ToCodePoints(text, -5));
        }

        [Fact]
        public void LoneSurrogatesCountAsOneCharacter()
        {
            // GLib turns each into one replacement character, so each is one offset.
            string text = "a\uD83Db\uDE00";
            Assert.Equal(4, AtSpiText.CharacterCount(text));
            Assert.Equal(2, AtSpiText.ToCodePoints(text, 2));
        }

        [Fact]
        public void GetTextTakesCharacterRangesAndMinusOneForTheEnd()
        {
            string text = "a" + Emoji + "bc";
            Assert.Equal(Emoji, AtSpiText.Substring(text, 1, 2));
            Assert.Equal(Emoji + "bc", AtSpiText.Substring(text, 1, -1));
            Assert.Equal(text, AtSpiText.Substring(text, 0, 50));
            Assert.Equal(string.Empty, AtSpiText.Substring(text, 3, 1));   // backwards is empty, as ATK's is
            Assert.Equal(string.Empty, AtSpiText.Substring(null, 0, -1));
        }

        [Fact]
        public void TheCharacterAtAnOffsetIsACodePoint()
        {
            string text = "a" + Emoji + "b";
            Assert.Equal('a', AtSpiText.CharacterAt(text, 0));
            Assert.Equal(0x1F600, AtSpiText.CharacterAt(text, 1));
            Assert.Equal('b', AtSpiText.CharacterAt(text, 2));
            Assert.Equal(0, AtSpiText.CharacterAt(text, 3));
            Assert.Equal(0, AtSpiText.CharacterAt(text, -1));

            // The extents of one character cover both halves of a pair.
            Assert.Equal((1, 2), AtSpiText.CharacterRange(text, 1));
            Assert.Equal((4, 0), AtSpiText.CharacterRange(text, 3));
        }

        // ---- text at, before and after ----

        [Fact]
        public void CharactersAtBeforeAndAfter()
        {
            string text = "a" + Emoji + "b";
            Assert.Equal((Emoji, 1, 2), AtSpiText.TextAt(text, 1, AtSpiText.Unit.Char, 0, null));
            Assert.Equal(("a", 0, 1), AtSpiText.TextAt(text, 1, AtSpiText.Unit.Char, -1, null));
            Assert.Equal(("b", 2, 3), AtSpiText.TextAt(text, 1, AtSpiText.Unit.Char, 1, null));

            // At the end there is no character; the one before it is the last.
            Assert.Equal((string.Empty, 3, 3), AtSpiText.TextAt(text, 3, AtSpiText.Unit.Char, 0, null));
            Assert.Equal(("b", 2, 3), AtSpiText.TextAt(text, 3, AtSpiText.Unit.Char, -1, null));
            Assert.Equal((string.Empty, 0, 0), AtSpiText.TextAt(text, 0, AtSpiText.Unit.Char, -1, null));
        }

        [Fact]
        public void WordStartUnitsRunToTheNextWord()
        {
            const string text = "Hello brave  world";
            Assert.Equal(("brave  ", 6, 13), AtSpiText.TextAt(text, 7, AtSpiText.Unit.WordStart, 0, null));
            Assert.Equal(("Hello ", 0, 6), AtSpiText.TextAt(text, 7, AtSpiText.Unit.WordStart, -1, null));
            Assert.Equal(("world", 13, 18), AtSpiText.TextAt(text, 7, AtSpiText.Unit.WordStart, 1, null));

            // In the space after a word, the word is still the one it trails.
            Assert.Equal(("brave  ", 6, 13), AtSpiText.TextAt(text, 12, AtSpiText.Unit.WordStart, 0, null));

            // A caret after the last word reads that word.
            Assert.Equal(("world", 13, 18), AtSpiText.TextAt(text, 18, AtSpiText.Unit.WordStart, 0, null));
            Assert.Equal((string.Empty, 18, 18), AtSpiText.TextAt(text, 15, AtSpiText.Unit.WordStart, 1, null));
        }

        [Fact]
        public void WordEndUnitsRunFromThePreviousWord()
        {
            const string text = "Hello brave  world";
            Assert.Equal((" brave", 5, 11), AtSpiText.TextAt(text, 7, AtSpiText.Unit.WordEnd, 0, null));
            Assert.Equal(("Hello", 0, 5), AtSpiText.TextAt(text, 7, AtSpiText.Unit.WordEnd, -1, null));
            Assert.Equal(("  world", 11, 18), AtSpiText.TextAt(text, 7, AtSpiText.Unit.WordEnd, 1, null));
        }

        [Fact]
        public void WordsKeepApostrophesAndSkipPunctuation()
        {
            const string text = "don't, stop";
            Assert.Equal(("don't, ", 0, 7), AtSpiText.TextAt(text, 2, AtSpiText.Unit.WordStart, 0, null));
            Assert.Equal(("stop", 7, 11), AtSpiText.TextAt(text, 2, AtSpiText.Unit.WordStart, 1, null));
        }

        [Fact]
        public void WordOffsetsAfterAPairAreInCharacters()
        {
            string text = Emoji + " hi there";
            // UTF-16 puts "hi" at 3; in characters it is at 2.
            Assert.Equal(("hi ", 2, 5), AtSpiText.TextAt(text, 3, AtSpiText.Unit.WordStart, 0, null));
            Assert.Equal(("there", 5, 10), AtSpiText.TextAt(text, 2, AtSpiText.Unit.WordStart, 1, null));
        }

        [Fact]
        public void GranularitiesAreTheStartBoundaries()
        {
            Assert.True(AtSpiText.TryUnitForGranularity(AtSpiText.GranularityChar, out AtSpiText.Unit unit));
            Assert.Equal(AtSpiText.Unit.Char, unit);
            Assert.True(AtSpiText.TryUnitForGranularity(AtSpiText.GranularityWord, out unit));
            Assert.Equal(AtSpiText.Unit.WordStart, unit);
            Assert.True(AtSpiText.TryUnitForGranularity(AtSpiText.GranularityLine, out unit));
            Assert.Equal(AtSpiText.Unit.LineStart, unit);
            Assert.True(AtSpiText.TryUnitForGranularity(AtSpiText.GranularityParagraph, out unit));
            Assert.Equal(AtSpiText.Unit.Paragraph, unit);
            Assert.False(AtSpiText.TryUnitForGranularity(9, out _));

            Assert.True(AtSpiText.TryUnitForBoundary(AtSpiText.BoundaryLineEnd, out unit));
            Assert.Equal(AtSpiText.Unit.LineEnd, unit);
            Assert.True(AtSpiText.TryUnitForBoundary(AtSpiText.BoundaryWordEnd, out unit));
            Assert.Equal(AtSpiText.Unit.WordEnd, unit);
            Assert.False(AtSpiText.TryUnitForBoundary(7, out _));
        }

        [Fact]
        public void SentencesEndAtTerminatorsFollowedBySpace()
        {
            const string text = "Hi there. Version 1.5 is out! Fine";
            Assert.Equal(("Version 1.5 is out! ", 10, 30), AtSpiText.TextAt(text, 12, AtSpiText.Unit.SentenceStart, 0, null));
            Assert.Equal(("Hi there. ", 0, 10), AtSpiText.TextAt(text, 12, AtSpiText.Unit.SentenceStart, -1, null));
            Assert.Equal(("Fine", 30, 34), AtSpiText.TextAt(text, 12, AtSpiText.Unit.SentenceStart, 1, null));
            Assert.Equal((" Version 1.5 is out!", 9, 29), AtSpiText.TextAt(text, 12, AtSpiText.Unit.SentenceEnd, 0, null));
        }

        // ---- lines ----

        [Fact]
        public void LinesComeFromTheTree()
        {
            // Wrapped lines: no break in the text, so only the tree knows where the second begins.
            const string text = "hello world";
            AtSpiText.LineSource wrapped = (int offset, out int start, out int length) =>
            {
                (start, length) = offset < 6 ? (0, 6) : (6, 5);
                return true;
            };

            Assert.Equal(("world", 6, 11), AtSpiText.TextAt(text, 7, AtSpiText.Unit.LineStart, 0, wrapped));
            Assert.Equal(("hello ", 0, 6), AtSpiText.TextAt(text, 7, AtSpiText.Unit.LineStart, -1, wrapped));
            Assert.Equal((string.Empty, 11, 11), AtSpiText.TextAt(text, 7, AtSpiText.Unit.LineStart, 1, wrapped));

            // Paragraphs are the text's own, whatever the layout.
            Assert.Equal((text, 0, 11), AtSpiText.TextAt(text, 7, AtSpiText.Unit.Paragraph, 0, wrapped));
        }

        [Fact]
        public void WithoutLineGeometryTheBreaksAreTheLines()
        {
            const string text = "one\ntwo\r\nthree";
            AtSpiText.LineSource none = (int offset, out int start, out int length) => { start = length = 0; return false; };

            Assert.Equal(("two\r\n", 4, 9), AtSpiText.TextAt(text, 5, AtSpiText.Unit.LineStart, 0, none));
            Assert.Equal(("one\n", 0, 4), AtSpiText.TextAt(text, 5, AtSpiText.Unit.LineStart, -1, none));
            Assert.Equal(("three", 9, 14), AtSpiText.TextAt(text, 5, AtSpiText.Unit.LineStart, 1, none));
            Assert.Equal(("three", 9, 14), AtSpiText.TextAt(text, 5, AtSpiText.Unit.LineStart, 1, null));
        }

        [Fact]
        public void AnAnswerThatDoesNotHoldTheOffsetFallsBackToTheBreaks()
        {
            const string text = "one\ntwo";
            AtSpiText.LineSource wrong = (int offset, out int start, out int length) => { (start, length) = (0, 2); return true; };
            Assert.Equal(("two", 4, 7), AtSpiText.TextAt(text, 5, AtSpiText.Unit.LineStart, 0, wrong));
        }

        [Fact]
        public void LineEndUnitsStartWithThePreviousBreak()
        {
            const string text = "one\ntwo\nthree";
            Assert.Equal(("one", 0, 3), AtSpiText.TextAt(text, 1, AtSpiText.Unit.LineEnd, 0, null));
            Assert.Equal(("\ntwo", 3, 7), AtSpiText.TextAt(text, 5, AtSpiText.Unit.LineEnd, 0, null));
            Assert.Equal(("one", 0, 3), AtSpiText.TextAt(text, 5, AtSpiText.Unit.LineEnd, -1, null));
            Assert.Equal(("\nthree", 7, 13), AtSpiText.TextAt(text, 5, AtSpiText.Unit.LineEnd, 1, null));

            // the break itself belongs to the unit it starts
            Assert.Equal(("\ntwo", 3, 7), AtSpiText.TextAt(text, 3, AtSpiText.Unit.LineEnd, 0, null));
        }

        [Fact]
        public void AnEmptyLastLineIsALine()
        {
            const string text = "one\n";
            Assert.Equal((string.Empty, 4, 4), AtSpiText.TextAt(text, 4, AtSpiText.Unit.LineStart, 0, null));
            Assert.Equal(("one\n", 0, 4), AtSpiText.TextAt(text, 4, AtSpiText.Unit.LineStart, -1, null));
        }

        [Fact]
        public void LineOffsetsAreInCharacters()
        {
            string text = Emoji + "a\nbc";   // UTF-16 lengths: 2 + 1 + 1, then 2
            AtSpiText.LineSource tree = (int offset, out int start, out int length) =>
            {
                (start, length) = offset < 4 ? (0, 4) : (4, 2);
                return true;
            };
            Assert.Equal((Emoji + "a\n", 0, 3), AtSpiText.TextAt(text, 1, AtSpiText.Unit.LineStart, 0, tree));
            Assert.Equal(("bc", 3, 5), AtSpiText.TextAt(text, 1, AtSpiText.Unit.LineStart, 1, tree));
        }

        // ---- what changed ----

        [Fact]
        public void TypingIsAnInsertAndBackspaceADelete()
        {
            Assert.Equal(new[] { ("insert", 2, 1, "X") }, AtSpiText.Diff("ab", "abX"));
            Assert.Equal(new[] { ("insert", 1, 1, "X") }, AtSpiText.Diff("ab", "aXb"));
            Assert.Equal(new[] { ("delete", 3, 1, "l") }, AtSpiText.Diff("hello", "helo"));
            Assert.Equal(new[] { ("delete", 0, 2, "ab") }, AtSpiText.Diff("ab", string.Empty));
            Assert.Equal(new[] { ("insert", 0, 2, "ab") }, AtSpiText.Diff(null, "ab"));
            Assert.Empty(AtSpiText.Diff("same", "same"));
        }

        [Fact]
        public void ReplacingASelectionIsADeleteThenAnInsert()
        {
            Assert.Equal(new[] { ("delete", 1, 3, "bcd"), ("insert", 1, 1, "Z") }, AtSpiText.Diff("abcde", "aZe"));
        }

        [Fact]
        public void EditsNeverSplitAPair()
        {
            // The two emoji share their high surrogate; the change is the whole character.
            const string grin = "\U0001F601";
            Assert.Equal(new[] { ("delete", 1, 1, Emoji), ("insert", 1, 1, grin) }, AtSpiText.Diff("a" + Emoji, "a" + grin));

            // Offsets and lengths after a pair are characters.
            Assert.Equal(new[] { ("insert", 2, 1, Emoji) }, AtSpiText.Diff(Emoji + "x", Emoji + "x" + Emoji));
        }

        [Fact]
        public void TheCaretFollowsTheEndTheUserExtends()
        {
            // First sight: the caret is news, a caret is not a selection.
            var (now, moved, changed) = AtSpiText.SelectionSignals(null, 5, 0);
            Assert.True(moved);
            Assert.False(changed);
            Assert.Equal(5, now.Caret);

            // shift+right twice from 5: the start stays, so the caret is at the end.
            (now, moved, changed) = AtSpiText.SelectionSignals(now, 5, 1);
            Assert.Equal(6, now.Caret);
            Assert.True(moved && changed);
            (now, moved, changed) = AtSpiText.SelectionSignals(now, 5, 2);
            Assert.Equal(7, now.Caret);

            // collapsing back to a caret is a selection change too ("unselected")
            (now, moved, changed) = AtSpiText.SelectionSignals(now, 5, 0);
            Assert.Equal(5, now.Caret);
            Assert.True(moved && changed);

            // shift+left from 5: the start moves, so the caret is at the start
            (now, _, _) = AtSpiText.SelectionSignals(now, 3, 2);
            Assert.Equal(3, now.Caret);

            // nothing different, nothing to say
            (_, moved, changed) = AtSpiText.SelectionSignals(now, 3, 2);
            Assert.False(moved);
            Assert.False(changed);
        }

        // ---- selection ----

        [Fact]
        public void SelectedChildrenAreCountedInTheSelection()
        {
            var tree = ListTree(multiple: true);
            tree.Selected.UnionWith(new[] { 11, 12 });

            Assert.Equal(11, AtSpiPatterns.SelectedChildAt(tree, 1, 0));
            Assert.Equal(12, AtSpiPatterns.SelectedChildAt(tree, 1, 1));
            Assert.Equal(IAutomationTreeSource.InvalidNode, AtSpiPatterns.SelectedChildAt(tree, 1, 2));
            Assert.Equal(IAutomationTreeSource.InvalidNode, AtSpiPatterns.SelectedChildAt(tree, 1, -1));

            // IsChildSelected counts the CHILDREN
            Assert.False(AtSpiPatterns.IsChildSelected(tree, 1, 0));
            Assert.True(AtSpiPatterns.IsChildSelected(tree, 1, 1));
            Assert.False(AtSpiPatterns.IsChildSelected(tree, 1, 9));
        }

        [Fact]
        public void SelectChildAddsOnAMultipleSelection()
        {
            var tree = ListTree(multiple: true);

            Assert.True(AtSpiPatterns.SelectChild(tree, 1, 0));
            Assert.True(AtSpiPatterns.SelectChild(tree, 1, 2));
            Assert.Equal(new[] { (10, AccessibleAction.Select), (12, AccessibleAction.ToggleSelection) }, tree.Log);
            Assert.Equal(new[] { 10, 12 }, tree.GetSelectedIds(1));

            Assert.True(AtSpiPatterns.DeselectSelectedChild(tree, 1, 1));   // the second selected: 12
            Assert.Equal(new[] { 10 }, tree.GetSelectedIds(1));

            Assert.True(AtSpiPatterns.SelectAll(tree, 1));
            Assert.Equal(new[] { 10, 11, 12 }, tree.GetSelectedIds(1));

            Assert.True(AtSpiPatterns.DeselectChild(tree, 1, 0));
            Assert.Equal(new[] { 11, 12 }, tree.GetSelectedIds(1));

            Assert.True(AtSpiPatterns.ClearSelection(tree, 1));
            Assert.Empty(tree.GetSelectedIds(1));

            Assert.False(AtSpiPatterns.SelectChild(tree, 1, 3));
        }

        [Fact]
        public void SelectChildReplacesOnASingleSelection()
        {
            var tree = ListTree(multiple: false);

            Assert.True(AtSpiPatterns.SelectChild(tree, 1, 1));
            Assert.True(AtSpiPatterns.SelectChild(tree, 1, 2));
            Assert.Equal(new[] { 12 }, tree.GetSelectedIds(1));

            Assert.False(AtSpiPatterns.SelectAll(tree, 1));
            // an item that cannot be toggled off stays selected, and says so
            Assert.False(AtSpiPatterns.DeselectChild(tree, 1, 2));
            Assert.Equal(new[] { 12 }, tree.GetSelectedIds(1));
            // and one not selected is already deselected
            Assert.True(AtSpiPatterns.DeselectChild(tree, 1, 0));
        }

        // ---- tables ----

        [Fact]
        public void CellIndicesAreRowMajor()
        {
            Assert.Equal(6, AtSpiPatterns.CellIndex(3, 4, 1, 2));
            Assert.Equal(0, AtSpiPatterns.CellIndex(3, 4, 0, 0));
            Assert.Equal(11, AtSpiPatterns.CellIndex(3, 4, 2, 3));
            Assert.Equal(-1, AtSpiPatterns.CellIndex(3, 4, 3, 0));
            Assert.Equal(-1, AtSpiPatterns.CellIndex(3, 4, 0, 4));
            Assert.Equal(-1, AtSpiPatterns.CellIndex(3, 4, -1, 0));

            Assert.Equal((1, 2), AtSpiPatterns.RowAndColumnOf(3, 4, 6));
            Assert.Equal((2, 3), AtSpiPatterns.RowAndColumnOf(3, 4, 11));
            Assert.Equal((-1, -1), AtSpiPatterns.RowAndColumnOf(3, 4, 12));
            Assert.Equal((-1, -1), AtSpiPatterns.RowAndColumnOf(3, 0, 0));
            Assert.Equal((-1, -1), AtSpiPatterns.RowAndColumnOf(3, 4, -1));

            for (int i = 0; i < 12; i++)
            {
                (int row, int column) = AtSpiPatterns.RowAndColumnOf(3, 4, i);
                Assert.Equal(i, AtSpiPatterns.CellIndex(3, 4, row, column));
            }
        }

        [Fact]
        public void SelectedRowsComeFromRowsOrCells()
        {
            var tree = new PatternTree();
            tree.States[1] = AccessibleState.Grid | AccessibleState.SelectionContainer;
            tree.GridItems[21] = (2, 0, 1, 1, 1);   // a cell of row item 20
            tree.Children[20] = new[] { 21 };
            tree.GridItems[30] = (0, 3, 1, 1, 1);   // a selected cell
            tree.SelectedList[1] = new[] { 20, 30, 99 };

            Assert.Equal(new[] { 0, 2 }, AtSpiPatterns.SelectedRows(tree, 1));
            Assert.True(AtSpiPatterns.IsCellSelected(tree, 1, 2, 1));
            Assert.False(AtSpiPatterns.IsCellSelected(tree, 1, 1, 1));
        }

        [Fact]
        public void HeadersAndSpansComeFromTheGrid()
        {
            var tree = new PatternTree();
            tree.ColumnHeaders[1] = new[] { 100, 101, 102 };
            tree.RowHeaders[1] = new[] { 200, 201 };
            tree.GridItems[5] = (1, 1, 1, 2, 1);   // row 1, columns 1-2
            tree.Cells[(1, 1, 1)] = 5;

            Assert.Equal(101, AtSpiPatterns.ColumnHeader(tree, 1, 1));
            Assert.Equal(IAutomationTreeSource.InvalidNode, AtSpiPatterns.ColumnHeader(tree, 1, 3));
            Assert.Equal(201, AtSpiPatterns.RowHeader(tree, 1, 1));
            Assert.Equal(IAutomationTreeSource.InvalidNode, AtSpiPatterns.RowHeader(tree, 1, -1));

            Assert.Equal(new[] { 101, 102 }, AtSpiPatterns.HeaderCellsOf(tree, 5, columns: true));
            Assert.Equal(new[] { 201 }, AtSpiPatterns.HeaderCellsOf(tree, 5, columns: false));
            Assert.Empty(AtSpiPatterns.HeaderCellsOf(tree, 6, columns: true));

            Assert.Equal((1, 2), AtSpiPatterns.SpanAt(tree, 1, 1, 1));
            Assert.Equal((0, 0), AtSpiPatterns.SpanAt(tree, 1, 0, 0));   // no cell there
        }

        // ---- signals ----

        [Fact]
        public void PatternSignalsAreTheMembersOrcaSubscribesTo()
        {
            Assert.Equal(("TextChanged", "insert"), AtSpiRoles.SignalFor(AutomationChangeKind.TextChanged));
            Assert.Equal(("TextCaretMoved", string.Empty), AtSpiRoles.SignalFor(AutomationChangeKind.TextSelectionChanged));
            Assert.Equal(("SelectionChanged", string.Empty), AtSpiRoles.SignalFor(AutomationChangeKind.SelectionContainerChanged));
            Assert.Equal(("VisibleDataChanged", string.Empty), AtSpiRoles.SignalFor(AutomationChangeKind.ScrollChanged));
        }

        // ---- interfaces, roles and states ----

        [Fact]
        public void InterfacesFollowThePatterns()
        {
            const string Text = "org.a11y.atspi.Text", Editable = "org.a11y.atspi.EditableText";

            string[] plain = AtSpiBridge.InterfacesFor(AccessibleState.Enabled, hasActions: false);
            Assert.Equal(new[] { "org.a11y.atspi.Accessible", "org.a11y.atspi.Component" }, plain);

            Assert.Contains("org.a11y.atspi.Action", AtSpiBridge.InterfacesFor(AccessibleState.None, hasActions: true));
            Assert.Contains("org.a11y.atspi.Value", AtSpiBridge.InterfacesFor(AccessibleState.HasRange, false));

            string[] edit = AtSpiBridge.InterfacesFor(AccessibleState.HasText | AccessibleState.HasValue, false);
            Assert.Contains(Text, edit);
            Assert.Contains(Editable, edit);

            string[] readOnly = AtSpiBridge.InterfacesFor(AccessibleState.HasText | AccessibleState.HasValue | AccessibleState.ReadOnly, false);
            Assert.Contains(Text, readOnly);
            Assert.DoesNotContain(Editable, readOnly);

            // a password's characters are not offered at all
            string[] password = AtSpiBridge.InterfacesFor(AccessibleState.HasText | AccessibleState.HasValue | AccessibleState.Password, false);
            Assert.DoesNotContain(Text, password);
            Assert.DoesNotContain(Editable, password);

            Assert.Contains("org.a11y.atspi.Selection", AtSpiBridge.InterfacesFor(AccessibleState.SelectionContainer, false));
            Assert.Contains("org.a11y.atspi.Table", AtSpiBridge.InterfacesFor(AccessibleState.Grid, false));
            Assert.Contains("org.a11y.atspi.TableCell", AtSpiBridge.InterfacesFor(AccessibleState.GridItem, false));
            Assert.DoesNotContain("org.a11y.atspi.Table", AtSpiBridge.InterfacesFor(AccessibleState.GridItem, false));
        }

        [Fact]
        public void IntrospectionDeclaresWhatGetInterfacesReports()
        {
            string[] interfaces = AtSpiBridge.InterfacesFor(AccessibleState.HasText | AccessibleState.HasValue | AccessibleState.GridItem, false);
            string xml = AtSpiBridge.IntrospectXmlFor(interfaces);

            foreach (string iface in interfaces) Assert.Contains($"<interface name=\"{iface}\">", xml);
            Assert.DoesNotContain("org.a11y.atspi.Selection", xml);
            Assert.Contains("<method name=\"GetInterfaces\"><arg type=\"as\" direction=\"out\"/></method>", xml);
            Assert.Contains("<method name=\"GetTextAtOffset\"><arg type=\"i\" direction=\"in\"/><arg type=\"u\" direction=\"in\"/>" +
                            "<arg type=\"s\" direction=\"out\"/><arg type=\"i\" direction=\"out\"/><arg type=\"i\" direction=\"out\"/></method>", xml);
            Assert.Contains("<property name=\"Position\" type=\"(ii)\" access=\"read\"/>", xml);

            // well-formed, or busctl and d-feet show nothing
            System.Xml.Linq.XDocument.Parse(xml, System.Xml.Linq.LoadOptions.None);
        }

        [Fact]
        public void PatternsRefineRolesThatSayNothing()
        {
            const uint Dialog = 16, Frame = 23, List = 31, PasswordText = 40, Panel = 39, ScrollPane = 49, TableCell = 56, Text = 61;

            Assert.Equal(ScrollPane, AtSpiRoles.RoleFor(33, AccessibleState.Scrollable));              // Pane
            Assert.Equal("scroll pane", AtSpiRoles.RoleNameFor(33, AccessibleState.Scrollable));
            Assert.Equal(List, AtSpiRoles.RoleFor(8, AccessibleState.Scrollable));                     // a list that scrolls is a list
            Assert.Equal(Panel, AtSpiRoles.RoleFor(33, AccessibleState.None));

            Assert.Equal(TableCell, AtSpiRoles.RoleFor(25, AccessibleState.GridItem));                 // Custom
            Assert.Equal(Text, AtSpiRoles.RoleFor(4, AccessibleState.GridItem));                       // a text box in a cell
            Assert.Equal(PasswordText, AtSpiRoles.RoleFor(4, AccessibleState.Password));

            Assert.Equal(Frame, AtSpiRoles.RoleFor(32, AccessibleState.Window));
            Assert.Equal(Dialog, AtSpiRoles.RoleFor(32, AccessibleState.Window | AccessibleState.Modal));
            Assert.Equal("dialog", AtSpiRoles.RoleNameFor(32, AccessibleState.Window | AccessibleState.Modal));
            Assert.Equal(Frame, AtSpiRoles.RoleFor(33, AccessibleState.Window));                       // a form that is a Pane
        }

        [Fact]
        public void WindowAndPatternStates()
        {
            const int Showing = 25, Iconified = 15, Modal = 16, Multiselectable = 18, SelectableText = 38, ReadOnly = 43, Editable = 7;
            static bool Has(ulong bits, int state) => (bits & (1UL << state)) != 0;

            ulong dialog = AtSpiRoles.StatesFor(AccessibleState.Window | AccessibleState.Modal, 0);
            Assert.True(Has(dialog, Modal));
            Assert.True(Has(dialog, Showing));
            Assert.False(Has(dialog, Iconified));

            ulong minimized = AtSpiRoles.StatesFor(AccessibleState.Window, 2);
            Assert.True(Has(minimized, Iconified));
            Assert.False(Has(minimized, Showing));
            Assert.False(Has(AtSpiRoles.StatesFor(AccessibleState.None, 2), Iconified));   // only windows minimize

            Assert.True(Has(AtSpiRoles.StatesFor(AccessibleState.SelectionContainer | AccessibleState.MultiSelectable), Multiselectable));
            Assert.False(Has(AtSpiRoles.StatesFor(AccessibleState.SelectionContainer), Multiselectable));

            ulong text = AtSpiRoles.StatesFor(AccessibleState.HasText | AccessibleState.HasValue);
            Assert.True(Has(text, SelectableText));
            Assert.True(Has(text, Editable));
            Assert.False(Has(text, ReadOnly));

            ulong readOnly = AtSpiRoles.StatesFor(AccessibleState.HasText | AccessibleState.ReadOnly);
            Assert.True(Has(readOnly, ReadOnly));
            Assert.False(Has(readOnly, Editable));
            Assert.False(Has(AtSpiRoles.StatesFor(AccessibleState.HasText | AccessibleState.Password), SelectableText));
        }

        // ---- the fake ----

        /// <summary>A list (node 1) of three selectable items, 10-12, that answer Select and ToggleSelection the way
        /// a ListBox's items do: Select replaces, ToggleSelection adds or removes -- if the list allows many.</summary>
        private static PatternTree ListTree(bool multiple)
        {
            var tree = new PatternTree { Multiple = multiple };
            tree.States[1] = AccessibleState.SelectionContainer | (multiple ? AccessibleState.MultiSelectable : 0);
            tree.Children[1] = new[] { 10, 11, 12 };
            foreach (int item in tree.Children[1])
            {
                tree.States[item] = AccessibleState.Selectable;
                tree.Actions[item] = multiple
                    ? new[] { AccessibleAction.Select, AccessibleAction.ToggleSelection }
                    : new[] { AccessibleAction.Select };
            }
            return tree;
        }

        private sealed class PatternTree : IAutomationTreeSource
        {
            internal readonly Dictionary<int, AccessibleState> States = new();
            internal readonly Dictionary<int, AccessibleAction[]> Actions = new();
            internal readonly Dictionary<int, int[]> Children = new();
            internal readonly SortedSet<int> Selected = new();
            internal readonly Dictionary<int, int[]> SelectedList = new();
            internal readonly Dictionary<(int Grid, int Row, int Column), int> Cells = new();
            internal readonly Dictionary<int, (int Row, int Column, int RowSpan, int ColumnSpan, int Grid)> GridItems = new();
            internal readonly Dictionary<int, int[]> ColumnHeaders = new();
            internal readonly Dictionary<int, int[]> RowHeaders = new();
            internal readonly List<(int, AccessibleAction)> Log = new();
            internal bool Multiple;
            internal readonly Dictionary<int, (string Access, string Accelerator)> Keys = new();

            public string GetAccessKey(int id) => Keys.TryGetValue(id, out var k) ? k.Access : string.Empty;
            public string GetAcceleratorKey(int id) => Keys.TryGetValue(id, out var k) ? k.Accelerator : string.Empty;

            public int GetRootId(IntPtr windowHandle) => 0;
            public int[] GetChildIds(int id) => Children.TryGetValue(id, out var c) ? c : Array.Empty<int>();
            public int GetParentId(int id) => IAutomationTreeSource.InvalidNode;
            internal readonly Dictionary<int, int> Roles = new();
            public int GetRole(int id) => Roles.TryGetValue(id, out int r) ? r : -1;
            public string GetName(int id) => string.Empty;
            public string GetAutomationId(int id) => string.Empty;
            public string GetHelpText(int id) => string.Empty;
            public string GetValue(int id) => string.Empty;
            public bool TryGetRange(int id, out double value, out double minimum, out double maximum) { value = minimum = maximum = 0; return false; }

            public AccessibleState GetState(int id)
            {
                AccessibleState state = States.TryGetValue(id, out AccessibleState s) ? s : default;
                return Selected.Contains(id) ? state | AccessibleState.Selected : state;
            }

            public bool TryGetBounds(int id, out double x, out double y, out double width, out double height) { x = y = width = height = 0; return false; }
            public int HitTest(IntPtr windowHandle, double screenX, double screenY) => IAutomationTreeSource.InvalidNode;
            public int GetFocusedId(IntPtr windowHandle) => IAutomationTreeSource.InvalidNode;
            public bool SupportsAction(int id, AccessibleAction action) => Actions.TryGetValue(id, out var a) && Array.IndexOf(a, action) >= 0;

            public bool DoAction(int id, AccessibleAction action)
            {
                if (!SupportsAction(id, action)) return false;
                Log.Add((id, action));
                if (action == AccessibleAction.Select) { Selected.Clear(); Selected.Add(id); }
                else if (action == AccessibleAction.ToggleSelection && !Selected.Remove(id)) Selected.Add(id);
                return true;
            }

            public bool SetValue(int id, string value) => false;
            public bool SetFocus(int id) => false;

            public int[] GetSelectedIds(int id)
            {
                if (SelectedList.TryGetValue(id, out var list)) return list;
                var selected = new List<int>();
                foreach (int child in GetChildIds(id)) if (Selected.Contains(child)) selected.Add(child);
                return selected.ToArray();
            }

            public int GetGridCell(int id, int row, int column)
                => Cells.TryGetValue((id, row, column), out int cell) ? cell : IAutomationTreeSource.InvalidNode;

            public bool TryGetGridItem(int id, out int row, out int column, out int rowSpan, out int columnSpan, out int gridId)
            {
                if (GridItems.TryGetValue(id, out var item))
                {
                    (row, column, rowSpan, columnSpan, gridId) = item;
                    return true;
                }
                row = column = rowSpan = columnSpan = 0;
                gridId = IAutomationTreeSource.InvalidNode;
                return false;
            }

            public int[] GetColumnHeaderIds(int id) => ColumnHeaders.TryGetValue(id, out var h) ? h : Array.Empty<int>();
            public int[] GetRowHeaderIds(int id) => RowHeaders.TryGetValue(id, out var h) ? h : Array.Empty<int>();
        }
    }
}
