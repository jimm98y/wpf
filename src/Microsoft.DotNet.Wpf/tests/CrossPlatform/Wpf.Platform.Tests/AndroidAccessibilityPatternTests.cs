// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The Android backend's patterns -- text, selection, grids, scrolling, windows -- without a device.
//
// The AccessibilityNodeProvider that hands these to TalkBack is Java-side (AndroidHost.cs, compiled
// only into the Android heads), so what can be tested here is everything it is handed: the node
// JSON it copies into an AccessibilityNodeInfo, the action vocabulary it maps Android's actions
// onto, the segments a reading-granularity swipe moves over, and the events' JSON -- the edit a
// TYPE_VIEW_TEXT_CHANGED describes, the range of a selection change, a scroll's pixels. Those are
// pure functions over a fake tree. Whether TalkBack then speaks them is for a device.
//

using System;
using System.Collections.Generic;
using MS.Internal.Interop;
using Xunit;

namespace Wpf.Platform.Tests
{
    public class AndroidAccessibilityPatternTests : IDisposable
    {
        private const string Emoji = "\U0001F600";   // one character, two UTF-16 code units

        public AndroidAccessibilityPatternTests() => AndroidAccessibility.ResetEventState();

        public void Dispose()
        {
            AutomationTree.Current = null;
            AndroidAccessibility.ResetEventState();
        }

        private static FakeTree Install(FakeTree tree)
        {
            AutomationTree.Current = tree;
            return tree;
        }

        // ---- text nodes ----

        [Fact]
        public void ATextBoxCarriesItsTextSelectionAndGranularities()
        {
            FakeTree tree = Install(new FakeTree());
            tree.AddText(1, "Hello world", AccessibleState.HasValue | AccessibleState.Enabled | AccessibleState.Focusable);
            tree.Selections[1] = (6, 5);

            string json = AndroidAccessibility.GetNodeJson(1);

            Assert.Contains("\"cls\":\"android.widget.EditText\"", json);
            Assert.Contains("\"text\":\"Hello world\"", json);
            Assert.Contains("\"ed\":1", json);
            Assert.Contains("\"ss\":6,\"se\":11", json);
            Assert.Contains("\"gran\":15", json);   // character, word, line, paragraph
            Assert.DoesNotContain("\"ml\":", json);
            Assert.DoesNotContain("\"pw\":", json);

            int act = ActOf(json);
            Assert.NotEqual(0, act & AndroidAccessibility.ActSetSelection);
            Assert.NotEqual(0, act & AndroidAccessibility.ActTraverse);
            Assert.NotEqual(0, act & AndroidAccessibility.ActSetText);
        }

        [Fact]
        public void ReadOnlyTextIsReadableButNotEditable()
        {
            FakeTree tree = Install(new FakeTree { HasLines = false });
            tree.AddText(2, "Read me", AccessibleState.HasValue | AccessibleState.ReadOnly);

            string json = AndroidAccessibility.GetNodeJson(2);

            Assert.DoesNotContain("\"ed\":", json);
            Assert.Equal(0, ActOf(json) & AndroidAccessibility.ActSetText);
            Assert.NotEqual(0, ActOf(json) & AndroidAccessibility.ActTraverse);
            // No line granularity where the tree does not know its lines.
            Assert.Contains("\"gran\":11", json);
        }

        [Fact]
        public void APasswordIsMaskedAndCannotBeReadByWord()
        {
            FakeTree tree = Install(new FakeTree());
            tree.Roles[3] = 4;
            tree.States[3] = AccessibleState.Password | AccessibleState.HasValue;
            tree.Values[3] = "abc";

            string json = AndroidAccessibility.GetNodeJson(3);

            Assert.Contains("\"text\":\"\u2022\u2022\u2022\"", json);
            Assert.DoesNotContain("abc", json);
            Assert.Contains("\"pw\":1", json);
            Assert.Contains("\"ed\":1", json);
            Assert.DoesNotContain("\"gran\":", json);
            Assert.False(AndroidAccessibility.MoveAtGranularity(3, AndroidAccessibility.GranularityCharacter, true, false, out _, out _));
        }

        [Fact]
        public void ADocumentOrATextWithLineBreaksIsMultiLine()
        {
            FakeTree tree = Install(new FakeTree());
            tree.AddText(4, "one\ntwo", AccessibleState.HasValue);
            tree.AddText(5, "single", default);
            tree.Roles[5] = 30;   // Document

            Assert.Contains("\"ml\":1", AndroidAccessibility.GetNodeJson(4));
            Assert.Contains("\"ml\":1", AndroidAccessibility.GetNodeJson(5));
        }

        [Fact]
        public void ControlCharactersAreKeptSoOffsetsStillCount()
        {
            FakeTree tree = Install(new FakeTree());
            tree.AddText(6, "a\u0001b", default);
            Assert.Contains("\"text\":\"a\\u0001b\"", AndroidAccessibility.GetNodeJson(6));
        }

        // ---- reading by granularity ----

        [Fact]
        public void CharactersAreWholeSurrogatePairs()
        {
            string text = "a" + Emoji + "b";
            Assert.Equal((1, 3), Segment(text, AndroidAccessibility.GranularityCharacter, true, 1));
            Assert.Equal((1, 3), Segment(text, AndroidAccessibility.GranularityCharacter, true, 2));   // between the halves
            Assert.Equal((1, 3), Segment(text, AndroidAccessibility.GranularityCharacter, false, 3));
            Assert.Equal((0, 1), Segment(text, AndroidAccessibility.GranularityCharacter, false, 2));
            Assert.Null(SegmentOrNull(text, AndroidAccessibility.GranularityCharacter, true, 4));
            Assert.Null(SegmentOrNull(text, AndroidAccessibility.GranularityCharacter, false, 0));
        }

        [Fact]
        public void WordsAreRunsOfLettersAndDigits()
        {
            const string text = "Hello, brave  world";
            Assert.Equal((0, 5), Segment(text, AndroidAccessibility.GranularityWord, true, 0));
            Assert.Equal((7, 12), Segment(text, AndroidAccessibility.GranularityWord, true, 5));
            // From inside a word, the rest of it -- as Android's word iterator reads.
            Assert.Equal((9, 12), Segment(text, AndroidAccessibility.GranularityWord, true, 9));
            Assert.Equal((7, 12), Segment(text, AndroidAccessibility.GranularityWord, false, 14));
            Assert.Equal((0, 5), Segment(text, AndroidAccessibility.GranularityWord, false, 7));
            Assert.Null(SegmentOrNull(text, AndroidAccessibility.GranularityWord, true, 19));
            Assert.Null(SegmentOrNull("  ", AndroidAccessibility.GranularityWord, true, 0));
            Assert.Equal((0, 5), Segment("don't stop", AndroidAccessibility.GranularityWord, true, 0));
        }

        [Fact]
        public void ParagraphsRunBetweenLineBreaks()
        {
            const string text = "first\n\nsecond\nthird";
            Assert.Equal((0, 5), Segment(text, AndroidAccessibility.GranularityParagraph, true, 0));
            Assert.Equal((7, 13), Segment(text, AndroidAccessibility.GranularityParagraph, true, 5));
            Assert.Equal((14, 19), Segment(text, AndroidAccessibility.GranularityParagraph, true, 13));
            Assert.Equal((7, 13), Segment(text, AndroidAccessibility.GranularityParagraph, false, 14));
            Assert.Equal((0, 5), Segment(text, AndroidAccessibility.GranularityParagraph, false, 7));
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void LinesComeFromTheTreeWithTheirBreaks(bool linesIncludeBreak)
        {
            var tree = new FakeTree { LinesIncludeBreak = linesIncludeBreak };
            const string text = "ab\ncd\n\nef";
            tree.AddText(1, text, default);
            AndroidAccessibility.TextLines lines = (int o, out int s, out int l) => tree.TryGetTextLine(1, o, out s, out l, out _);

            Assert.Equal((0, 3), Segment(text, AndroidAccessibility.GranularityLine, true, 0, lines));
            // Mid-line, forward is the NEXT line; backward the one that ends at the cursor or before.
            Assert.Equal((3, 6), Segment(text, AndroidAccessibility.GranularityLine, true, 1, lines));
            Assert.Equal((6, 7), Segment(text, AndroidAccessibility.GranularityLine, true, 6, lines));   // the empty line
            Assert.Equal((7, 9), Segment(text, AndroidAccessibility.GranularityLine, true, 7, lines));
            Assert.Equal((7, 9), Segment(text, AndroidAccessibility.GranularityLine, false, 9, lines));
            Assert.Equal((0, 3), Segment(text, AndroidAccessibility.GranularityLine, false, 3, lines));
            Assert.Equal((0, 3), Segment(text, AndroidAccessibility.GranularityLine, false, 4, lines));
            Assert.Null(SegmentOrNull(text, AndroidAccessibility.GranularityLine, true, 9, lines));
            Assert.Null(SegmentOrNull(text, AndroidAccessibility.GranularityLine, false, 0, lines));
        }

        [Fact]
        public void PagesAreNotOffered()
        {
            Assert.Null(SegmentOrNull("text", AndroidAccessibility.GranularityPage, true, 0));
        }

        [Fact]
        public void ReadOnlyTextIsReadWordByWordFromAnAccessibilityCursor()
        {
            // A TextBlock's selection cannot move, so where reading stopped has to be remembered
            // here -- or every swipe would read the first word again.
            FakeTree tree = Install(new FakeTree { SelectionIsFixed = true });
            tree.AddText(1, "one two three", default);
            tree.Selections[1] = (0, 0);

            Assert.True(AndroidAccessibility.MoveAtGranularity(1, AndroidAccessibility.GranularityWord, true, false, out int s, out int e));
            Assert.Equal((0, 3), (s, e));
            Assert.True(AndroidAccessibility.MoveAtGranularity(1, AndroidAccessibility.GranularityWord, true, false, out s, out e));
            Assert.Equal((4, 7), (s, e));
            Assert.True(AndroidAccessibility.MoveAtGranularity(1, AndroidAccessibility.GranularityWord, false, false, out s, out e));
            Assert.Equal((4, 7), (s, e));
            Assert.True(AndroidAccessibility.MoveAtGranularity(1, AndroidAccessibility.GranularityWord, false, false, out s, out e));
            Assert.Equal((0, 3), (s, e));
            Assert.False(AndroidAccessibility.MoveAtGranularity(1, AndroidAccessibility.GranularityWord, false, false, out _, out _));
        }

        [Fact]
        public void EditableTextMovesItsCaretAndCanExtendTheSelection()
        {
            FakeTree tree = Install(new FakeTree());
            tree.AddText(1, "one two three", AccessibleState.HasValue);
            tree.Selections[1] = (4, 0);

            Assert.True(AndroidAccessibility.MoveAtGranularity(1, AndroidAccessibility.GranularityWord, true, false, out int s, out int e));
            Assert.Equal((4, 7), (s, e));
            Assert.Equal((7, 0), tree.Selections[1]);   // the caret after the word

            Assert.True(AndroidAccessibility.MoveAtGranularity(1, AndroidAccessibility.GranularityWord, true, true, out s, out e));
            Assert.Equal((8, 13), (s, e));
            Assert.Equal((7, 6), tree.Selections[1]);   // extended from where the caret was

            // The user moves the caret themselves: reading carries on from there, not from the cache.
            tree.Selections[1] = (0, 0);
            Assert.True(AndroidAccessibility.MoveAtGranularity(1, AndroidAccessibility.GranularityCharacter, true, false, out s, out e));
            Assert.Equal((0, 1), (s, e));
        }

        [Fact]
        public void BackwardsFromNowhereStartsAtTheEnd()
        {
            FakeTree tree = Install(new FakeTree { HasSelection = false });
            tree.AddText(1, "ab cd", default);
            Assert.True(AndroidAccessibility.MoveAtGranularity(1, AndroidAccessibility.GranularityWord, false, false, out int s, out int e));
            Assert.Equal((3, 5), (s, e));
        }

        // ---- actions ----

        [Fact]
        public void SetSelectionTakesEitherOrderAndBothAbsentCollapses()
        {
            FakeTree tree = Install(new FakeTree());
            tree.AddText(1, "Hello world", AccessibleState.HasValue);

            Assert.True(AndroidAccessibility.PerformAction(1, AndroidAccessibility.KindSetSelection, "2,5"));
            Assert.Equal((2, 3), tree.Selections[1]);
            Assert.True(AndroidAccessibility.PerformAction(1, AndroidAccessibility.KindSetSelection, "9,6"));
            Assert.Equal((6, 3), tree.Selections[1]);
            Assert.True(AndroidAccessibility.PerformAction(1, AndroidAccessibility.KindSetSelection, "4,99"));
            Assert.Equal((4, 7), tree.Selections[1]);                // clamped to the text
            Assert.True(AndroidAccessibility.PerformAction(1, AndroidAccessibility.KindSetSelection, "-1,-1"));
            Assert.Equal((4, 0), tree.Selections[1]);
            Assert.True(AndroidAccessibility.PerformAction(1, AndroidAccessibility.KindSetSelection, null));
            Assert.Equal((4, 0), tree.Selections[1]);

            Assert.Equal((3, -1), AndroidAccessibility.ParseSelectionArgument("3"));
            Assert.Equal((-1, -1), AndroidAccessibility.ParseSelectionArgument("x,y"));

            Assert.False(AndroidAccessibility.PerformAction(404, AndroidAccessibility.KindSetSelection, "0,1"));
        }

        [Fact]
        public void SetTextIsSetValue()
        {
            FakeTree tree = Install(new FakeTree());
            tree.AddText(1, "old", AccessibleState.HasValue);
            Assert.True(AndroidAccessibility.PerformAction(1, AndroidAccessibility.KindSetValue, "new"));
            Assert.Equal("new", tree.Values[1]);
        }

        // ---- collections ----

        [Fact]
        public void AListIsACollectionOfRowsAndItsItemsKnowTheirIndex()
        {
            FakeTree tree = Install(new FakeTree());
            tree.Roles[10] = 8;   // List
            tree.States[10] = AccessibleState.SelectionContainer;
            tree.SetChildren(10, 11, 12, 13);
            foreach (int item in new[] { 11, 12, 13 }) { tree.Roles[item] = 7; tree.States[item] = AccessibleState.Selectable; }
            tree.States[12] |= AccessibleState.Selected;

            string list = AndroidAccessibility.GetNodeJson(10);
            Assert.Contains("\"coll\":1,\"cr\":3,\"cc\":1,\"chi\":0,\"csm\":1", list);

            string item2 = AndroidAccessibility.GetNodeJson(12);
            Assert.Contains("\"item\":1,\"ir\":1,\"ic\":0,\"irs\":1,\"ics\":1,\"ih\":0", item2);
            Assert.Contains("\"sel\":1", item2);

            tree.States[10] |= AccessibleState.MultiSelectable;
            Assert.Contains("\"csm\":2", AndroidAccessibility.GetNodeJson(10));

            tree.Roles[10] = 23;   // Tree
            Assert.Contains("\"chi\":1", AndroidAccessibility.GetNodeJson(10));

            // Not in a container: no item info, and nothing that is not a container gets one either.
            tree.Roles[20] = 0;
            Assert.DoesNotContain("\"item\":", AndroidAccessibility.GetNodeJson(20));
            Assert.DoesNotContain("\"coll\":", AndroidAccessibility.GetNodeJson(20));
        }

        [Fact]
        public void AGridCarriesItsShapeAndItsCellsTheirPlace()
        {
            FakeTree tree = Install(TableTree());

            string grid = AndroidAccessibility.GetNodeJson(Grid);
            Assert.Contains("\"coll\":1,\"cr\":2,\"cc\":3,\"chi\":0,\"csm\":2", grid);

            string cell = AndroidAccessibility.GetNodeJson(Cell(1, 2));
            Assert.Contains("\"item\":1,\"ir\":1,\"ic\":2,\"irs\":1,\"ics\":1,\"ih\":0", cell);

            // A header is a heading, in its column.
            string header = AndroidAccessibility.GetNodeJson(Header0 + 1);
            Assert.Contains("\"ic\":1", header);
            Assert.Contains("\"ih\":1", header);

            // A row of the grid spans every column, on its cells' row.
            string row = AndroidAccessibility.GetNodeJson(Row1);
            Assert.Contains("\"item\":1,\"ir\":1,\"ic\":0,\"irs\":1,\"ics\":3", row);
        }

        // ---- scrolling ----

        [Fact]
        public void AScrollViewOffersOnlyTheDirectionsItCanMove()
        {
            Assert.Equal(AndroidAccessibility.ActScrollForward | AndroidAccessibility.ActScrollDown,
                         AndroidAccessibility.ScrollActionsFor(false, -1, true, 0));
            Assert.Equal(AndroidAccessibility.ActScrollBackward | AndroidAccessibility.ActScrollUp,
                         AndroidAccessibility.ScrollActionsFor(false, -1, true, 100));
            Assert.Equal(AndroidAccessibility.ActScrollForward | AndroidAccessibility.ActScrollBackward
                         | AndroidAccessibility.ActScrollUp | AndroidAccessibility.ActScrollDown | AndroidAccessibility.ActScrollRight,
                         AndroidAccessibility.ScrollActionsFor(true, 0, true, 50));

            FakeTree tree = Install(new FakeTree());
            tree.Roles[30] = 33;
            tree.States[30] = AccessibleState.Scrollable;
            tree.Scroll[30] = (-1, 40, 100, 25, false, true);
            string json = AndroidAccessibility.GetNodeJson(30);
            Assert.Contains("\"scr\":1", json);
            Assert.NotEqual(0, ActOf(json) & AndroidAccessibility.ActScrollForward);

            // A scroll view that has nothing to scroll is not scrollable.
            tree.Scroll[30] = (-1, -1, 100, 100, false, false);
            Assert.DoesNotContain("\"scr\":", AndroidAccessibility.GetNodeJson(30));
        }

        [Fact]
        public void ScrollActionsArePagesAlongTheAxisThatCanMove()
        {
            Assert.Equal((2, 3), AndroidAccessibility.ScrollAmountsFor(AndroidAccessibility.KindScrollForward, true, 0, true, 50));
            Assert.Equal((3, 2), AndroidAccessibility.ScrollAmountsFor(AndroidAccessibility.KindScrollForward, true, 0, true, 100));
            Assert.Equal((2, 0), AndroidAccessibility.ScrollAmountsFor(AndroidAccessibility.KindScrollBackward, true, 50, true, 50));
            Assert.Equal((0, 2), AndroidAccessibility.ScrollAmountsFor(AndroidAccessibility.KindScrollBackward, true, 50, true, 0));
            Assert.Equal((-1, -1), AndroidAccessibility.ScrollAmountsFor(AndroidAccessibility.KindScrollForward, false, -1, true, 100));
            Assert.Equal((2, 0), AndroidAccessibility.ScrollAmountsFor(AndroidAccessibility.KindScrollUp, false, -1, true, 0));
            Assert.Equal((2, 3), AndroidAccessibility.ScrollAmountsFor(AndroidAccessibility.KindScrollDown, false, -1, true, 0));
            Assert.Equal((0, 2), AndroidAccessibility.ScrollAmountsFor(AndroidAccessibility.KindScrollLeft, true, 10, false, -1));
            Assert.Equal((3, 2), AndroidAccessibility.ScrollAmountsFor(AndroidAccessibility.KindScrollRight, true, 10, false, -1));
            Assert.Equal((-1, -1), AndroidAccessibility.ScrollAmountsFor(AndroidAccessibility.KindScrollRight, false, -1, true, 0));

            FakeTree tree = Install(new FakeTree());
            tree.States[30] = AccessibleState.Scrollable;
            tree.Scroll[30] = (-1, 40, 100, 25, false, true);
            Assert.True(AndroidAccessibility.PerformAction(30, AndroidAccessibility.KindScrollForward, null));
            Assert.Equal((30, 2, 3), tree.ScrollLog[^1]);
            Assert.False(AndroidAccessibility.PerformAction(30, AndroidAccessibility.KindScrollLeft, null));
        }

        [Fact]
        public void ShowOnScreenIsScrollIntoView()
        {
            FakeTree tree = Install(new FakeTree());
            tree.Roles[31] = 7;
            tree.States[31] = AccessibleState.ScrollItem;
            Assert.NotEqual(0, ActOf(AndroidAccessibility.GetNodeJson(31)) & AndroidAccessibility.ActShowOnScreen);
            Assert.True(AndroidAccessibility.PerformAction(31, AndroidAccessibility.KindShowOnScreen, null));
            Assert.Equal(31, tree.ScrolledIntoView);
        }

        // ---- windows ----

        [Fact]
        public void AModalWindowHasAPaneTitleAndCanBeDismissed()
        {
            FakeTree tree = Install(new FakeTree());
            tree.Roles[0] = 32;
            tree.Names[0] = "Save changes?";
            tree.States[0] = AccessibleState.Window | AccessibleState.Modal;

            string json = AndroidAccessibility.GetNodeJson(0);
            Assert.Contains("\"pane\":\"Save changes?\"", json);
            Assert.Contains("\"modal\":1", json);
            Assert.NotEqual(0, ActOf(json) & AndroidAccessibility.ActDismiss);

            Assert.True(AndroidAccessibility.PerformAction(0, AndroidAccessibility.KindDismiss, null));
            Assert.Equal(0, tree.Closed);

            // A window that is not modal is not dismissed by a back gesture.
            tree.States[0] = AccessibleState.Window;
            tree.Closed = -1;
            Assert.Equal(0, ActOf(AndroidAccessibility.GetNodeJson(0)) & AndroidAccessibility.ActDismiss);
            Assert.False(AndroidAccessibility.PerformAction(0, AndroidAccessibility.KindDismiss, null));
            Assert.Equal(-1, tree.Closed);
        }

        [Fact]
        public void AWindowsPaneIsToldOnceAndAgainWhenItChanges()
        {
            FakeTree tree = new FakeTree();
            tree.Roles[0] = 32;
            tree.Names[0] = "Options";
            tree.States[0] = AccessibleState.Window | AccessibleState.Modal;

            string first = AndroidAccessibility.WindowEventJson(tree, IntPtr.Zero, out int root);
            Assert.Equal(0, root);
            Assert.Equal("{\"ev\":5,\"pane\":\"Options\",\"modal\":1}", first);
            Assert.Equal(string.Empty, AndroidAccessibility.WindowEventJson(tree, IntPtr.Zero, out _));

            tree.States[0] = AccessibleState.Window;
            Assert.Contains("\"modal\":0", AndroidAccessibility.WindowEventJson(tree, IntPtr.Zero, out _));

            // Not a window at all (a root before its peer is built): nothing.
            tree.States[0] = default;
            Assert.Equal(string.Empty, AndroidAccessibility.WindowEventJson(tree, IntPtr.Zero, out _));
        }

        // ---- events ----

        [Fact]
        public void TheDiffIsOneReplacement()
        {
            Assert.Equal((5, 0, 1), AndroidAccessibility.TextDiff("Hello", "Hello!"));
            Assert.Equal((0, 1, 0), AndroidAccessibility.TextDiff("xab", "ab"));
            Assert.Equal((2, 1, 2), AndroidAccessibility.TextDiff("abcde", "abXYde"));
            Assert.Equal((0, 0, 3), AndroidAccessibility.TextDiff("", "abc"));
            Assert.Equal((0, 3, 0), AndroidAccessibility.TextDiff("abc", null));
            // A repeated letter: the common end is not counted twice.
            Assert.Equal((3, 0, 1), AndroidAccessibility.TextDiff("aaa", "aaaa"));
            // A pair whose low half changed is replaced whole, never split.
            Assert.Equal((1, 2, 2), AndroidAccessibility.TextDiff("a\uD83D\uDE00b", "a\uD83D\uDE01b"));
        }

        [Fact]
        public void TextChangedCarriesTheEditAndTheTextBefore()
        {
            FakeTree tree = Install(new FakeTree());
            tree.AddText(1, "Hello", AccessibleState.HasValue);

            // Never shown: no baseline, no event -- the head just invalidates.
            Assert.Equal(string.Empty, AndroidAccessibility.TextChangedJson(tree, 1));

            tree.Text[1] = "Hello world";
            Assert.Contains("\"from\":5,\"add\":6,\"rem\":0,\"before\":\"Hello\",\"text\":\"Hello world\"",
                            AndroidAccessibility.TextChangedJson(tree, 1));

            // Reading the node does not move the baseline; the change does.
            AndroidAccessibility.GetNodeJson(1);
            tree.Text[1] = "Help world";
            Assert.Contains("\"from\":3,\"add\":1,\"rem\":2,\"before\":\"Hello world\"", AndroidAccessibility.TextChangedJson(tree, 1));

            // No change, no event.
            Assert.Equal(string.Empty, AndroidAccessibility.TextChangedJson(tree, 1));
        }

        [Fact]
        public void APasswordsEditIsMasked()
        {
            FakeTree tree = Install(new FakeTree());
            tree.AddText(1, "se", AccessibleState.HasValue | AccessibleState.Password);
            AndroidAccessibility.GetNodeJson(1);
            tree.Text[1] = "sec";
            string json = AndroidAccessibility.TextChangedJson(tree, 1);
            Assert.Contains("\"before\":\"\u2022\u2022\",\"text\":\"\u2022\u2022\u2022\"", json);
            Assert.Contains("\"pw\":1", json);
        }

        [Fact]
        public void TextSelectionChangedIsTheRangeAndTheLength()
        {
            FakeTree tree = Install(new FakeTree());
            tree.AddText(1, "Hello world", AccessibleState.HasValue);
            tree.Selections[1] = (6, 5);
            Assert.Equal("{\"ev\":2,\"cls\":\"android.widget.EditText\",\"from\":6,\"to\":11,\"count\":11,\"text\":\"Hello world\"}",
                         AndroidAccessibility.TextSelectionJson(tree, 1));
            Assert.Equal(string.Empty, AndroidAccessibility.TextSelectionJson(tree, 404));
        }

        [Fact]
        public void ScrolledIsInPixelsOfContentWithTheChange()
        {
            // A viewport of 200 pixels showing a quarter of its content: 800 of content, 600 to scroll.
            Assert.Equal((300L, 600L), AndroidAccessibility.ScrollPixels(true, 50, 25, 200));
            Assert.Equal((0L, 0L), AndroidAccessibility.ScrollPixels(false, 50, 25, 200));
            Assert.Equal((0L, 0L), AndroidAccessibility.ScrollPixels(true, -1, 25, 200));

            FakeTree tree = Install(new FakeTree());
            tree.Roles[30] = 33;
            tree.States[30] = AccessibleState.Scrollable;
            tree.Bounds[30] = (0, 0, 100, 200);
            tree.Scroll[30] = (-1, 50, 100, 25, false, true);
            Assert.Equal("{\"ev\":3,\"cls\":\"android.view.View\",\"sx\":0,\"sy\":300,\"mx\":0,\"my\":600,\"dx\":0,\"dy\":0}",
                         AndroidAccessibility.ScrolledJson(tree, 30));

            tree.Scroll[30] = (-1, 75, 100, 25, false, true);
            Assert.Contains("\"sy\":450,\"mx\":0,\"my\":600,\"dx\":0,\"dy\":150", AndroidAccessibility.ScrolledJson(tree, 30));
        }

        [Fact]
        public void ASelectionChangeIsSentFromTheSelectedItem()
        {
            FakeTree tree = new FakeTree();
            tree.Roles[10] = 8;
            tree.States[10] = AccessibleState.SelectionContainer;
            tree.Roles[12] = 7;
            tree.Names[12] = "Banana";
            tree.SelectedIds[10] = new[] { 12 };

            (int source, string json) = AndroidAccessibility.EventFor(tree,
                new AutomationChange(AutomationChangeKind.SelectionContainerChanged, 10, IntPtr.Zero));
            Assert.Equal(12, source);
            Assert.Equal("{\"ev\":4,\"cls\":\"android.widget.TextView\",\"text\":\"Banana\"}", json);

            // Nothing selected: only a content change on the list.
            tree.SelectedIds[10] = Array.Empty<int>();
            (source, json) = AndroidAccessibility.EventFor(tree,
                new AutomationChange(AutomationChangeKind.SelectionContainerChanged, 10, IntPtr.Zero));
            Assert.Equal(10, source);
            Assert.Equal(string.Empty, json);

            // The kinds without an event of their own are content changes.
            Assert.Equal(string.Empty, AndroidAccessibility.EventFor(tree,
                new AutomationChange(AutomationChangeKind.NameChanged, 10, IntPtr.Zero)).Json);
        }

        [Fact]
        public void TheExistingNodeFieldsAreUnchanged()
        {
            // AccessibilityTests.Android_* pin the older fields; a button gains nothing new.
            FakeTree tree = Install(new FakeTree());
            tree.Roles[3] = 0;
            tree.Names[3] = "Save";
            tree.Actions[3] = new[] { AccessibleAction.Invoke };
            string json = AndroidAccessibility.GetNodeJson(3);
            Assert.Contains("\"act\":1}", json);
            Assert.DoesNotContain("\"coll\":", json);
            Assert.DoesNotContain("\"gran\":", json);
        }

        // ---- helpers ----

        private static int ActOf(string json)
        {
            int i = json.IndexOf("\"act\":", StringComparison.Ordinal);
            Assert.True(i >= 0, json);
            i += 6;
            int end = i;
            while (end < json.Length && char.IsDigit(json[end])) end++;
            return int.Parse(json.AsSpan(i, end - i));
        }

        private static (int, int) Segment(string text, int granularity, bool forward, int current, AndroidAccessibility.TextLines lines = null)
            => SegmentOrNull(text, granularity, forward, current, lines) ?? throw new Xunit.Sdk.XunitException("no segment");

        private static (int, int)? SegmentOrNull(string text, int granularity, bool forward, int current, AndroidAccessibility.TextLines lines = null)
            => AndroidAccessibility.FindSegment(text, granularity, forward, current, lines, out int s, out int e) ? (s, e) : null;

        // A 2 x 3 table: a header row (Header0..+2) under the grid, then two rows of cells, each row
        // an item whose children are the cells -- the shape a DataGrid's peers have.
        internal const int Grid = 100, Row0 = 110, Row1 = 111, Header0 = 120;
        internal static int Cell(int row, int column) => 200 + row * 10 + column;

        internal static FakeTree TableTree()
        {
            var tree = new FakeTree();
            tree.Roles[Grid] = 28;
            tree.States[Grid] = AccessibleState.Grid | AccessibleState.Table | AccessibleState.SelectionContainer | AccessibleState.MultiSelectable;
            tree.Grid[Grid] = (2, 3);
            tree.ColumnHeaders[Grid] = new[] { Header0, Header0 + 1, Header0 + 2 };
            tree.SetChildren(Grid, Row0, Row1);
            for (int c = 0; c < 3; c++) { tree.Parents[Header0 + c] = Grid; tree.Roles[Header0 + c] = 35; }
            for (int r = 0; r < 2; r++)
            {
                int row = r == 0 ? Row0 : Row1;
                tree.Roles[row] = 29;
                tree.SetChildren(row, Cell(r, 0), Cell(r, 1), Cell(r, 2));
                for (int c = 0; c < 3; c++)
                {
                    tree.Roles[Cell(r, c)] = 29;
                    tree.States[Cell(r, c)] = AccessibleState.GridItem;
                    tree.GridItems[Cell(r, c)] = (r, c, 1, 1, Grid);
                }
            }
            return tree;
        }

        internal sealed class FakeTree : IAutomationTreeSource
        {
            internal readonly Dictionary<int, int> Roles = new();
            internal readonly Dictionary<int, AccessibleState> States = new();
            internal readonly Dictionary<int, string> Names = new();
            internal readonly Dictionary<int, string> Values = new();
            internal readonly Dictionary<int, int> Parents = new();
            internal readonly Dictionary<int, int[]> Children = new();
            internal readonly Dictionary<int, AccessibleAction[]> Actions = new();
            internal readonly Dictionary<int, (double X, double Y, double W, double H)> Bounds = new();
            internal readonly Dictionary<int, string> Text = new();
            internal readonly Dictionary<int, (int Start, int Length)> Selections = new();
            internal readonly Dictionary<int, int[]> SelectedIds = new();
            internal readonly Dictionary<int, (int Rows, int Columns)> Grid = new();
            internal readonly Dictionary<int, (int Row, int Column, int RowSpan, int ColumnSpan, int Grid)> GridItems = new();
            internal readonly Dictionary<int, int[]> ColumnHeaders = new();
            internal readonly Dictionary<int, (double H, double V, double HSize, double VSize, bool HScroll, bool VScroll)> Scroll = new();
            internal readonly List<(int, int, int)> ScrollLog = new();

            internal readonly Dictionary<IntPtr, int> Roots = new();
            internal readonly Dictionary<int, int[]> RowHeaders = new();

            internal bool HasLines = true;
            internal bool AllBounded;   // every node without bounds of its own gets 10 x 10 at the origin
            internal bool LinesIncludeBreak = true;
            internal bool HasSelection = true;
            internal bool SelectionIsFixed;
            internal int ScrolledIntoView = -1;
            internal int Closed = -1;

            internal void AddText(int id, string text, AccessibleState extra)
            {
                Roles[id] = 4;
                States[id] = AccessibleState.HasText | extra;
                Text[id] = text;
                if (HasSelection) Selections[id] = (0, 0);
            }

            internal void SetChildren(int parent, params int[] children)
            {
                Children[parent] = children;
                foreach (int child in children) Parents[child] = parent;
            }

            public int GetRootId(IntPtr windowHandle) => Roots.TryGetValue(windowHandle, out int root) ? root : 0;
            public int[] GetChildIds(int id) => Children.TryGetValue(id, out int[] c) ? c : Array.Empty<int>();
            public int GetParentId(int id) => Parents.TryGetValue(id, out int p) ? p : IAutomationTreeSource.InvalidNode;
            public int GetRole(int id) => Roles.TryGetValue(id, out int r) ? r : (States.ContainsKey(id) ? 33 : -1);
            public string GetName(int id) => Names.TryGetValue(id, out string n) ? n : string.Empty;
            public string GetAutomationId(int id) => string.Empty;
            public string GetHelpText(int id) => string.Empty;
            public string GetValue(int id) => Values.TryGetValue(id, out string v) ? v : Text.TryGetValue(id, out string t) ? t : string.Empty;
            public bool TryGetRange(int id, out double value, out double minimum, out double maximum) { value = minimum = maximum = 0; return false; }
            public AccessibleState GetState(int id) => States.TryGetValue(id, out AccessibleState s) ? s : default;

            public bool TryGetBounds(int id, out double x, out double y, out double width, out double height)
            {
                bool known = Bounds.TryGetValue(id, out var b);
                if (!known && AllBounded && GetRole(id) >= 0) { b = (0, 0, 10, 10); known = true; }
                (x, y, width, height) = b;
                return known;
            }

            public int HitTest(IntPtr windowHandle, double screenX, double screenY) => IAutomationTreeSource.InvalidNode;
            public int GetFocusedId(IntPtr windowHandle) => IAutomationTreeSource.InvalidNode;
            public bool SupportsAction(int id, AccessibleAction action) => Actions.TryGetValue(id, out var a) && Array.IndexOf(a, action) >= 0;
            public bool DoAction(int id, AccessibleAction action) => SupportsAction(id, action);

            public bool SetValue(int id, string value)
            {
                if (!States.ContainsKey(id)) return false;
                Values[id] = value;
                return true;
            }

            public bool SetFocus(int id) => false;

            public bool TryGetText(int id, out string text)
            {
                if (Text.TryGetValue(id, out text)) return true;
                text = string.Empty;
                return false;
            }

            public bool TryGetTextSelection(int id, out int start, out int length)
            {
                bool known = Selections.TryGetValue(id, out var s);
                (start, length) = s;
                return known;
            }

            public bool SetTextSelection(int id, int start, int length)
            {
                if (SelectionIsFixed || !Text.ContainsKey(id)) return false;
                Selections[id] = (start, length);
                return true;
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

            public int[] GetSelectedIds(int id) => SelectedIds.TryGetValue(id, out int[] s) ? s : Array.Empty<int>();

            public bool TryGetGrid(int id, out int rows, out int columns)
            {
                bool known = Grid.TryGetValue(id, out var g);
                (rows, columns) = g;
                return known;
            }

            public bool TryGetGridItem(int id, out int row, out int column, out int rowSpan, out int columnSpan, out int gridId)
            {
                bool known = GridItems.TryGetValue(id, out var g);
                if (!known) g = (0, 0, 0, 0, IAutomationTreeSource.InvalidNode);
                (row, column, rowSpan, columnSpan, gridId) = g;
                return known;
            }

            public int[] GetColumnHeaderIds(int id) => ColumnHeaders.TryGetValue(id, out int[] h) ? h : Array.Empty<int>();
            public int[] GetRowHeaderIds(int id) => RowHeaders.TryGetValue(id, out int[] h) ? h : Array.Empty<int>();

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
                if (!Scroll.ContainsKey(id)) return false;
                ScrollLog.Add((id, horizontalAmount, verticalAmount));
                return true;
            }

            public bool ScrollIntoView(int id)
            {
                ScrolledIntoView = id;
                return true;
            }

            public bool CloseWindow(int id)
            {
                Closed = id;
                return true;
            }
        }
    }
}
