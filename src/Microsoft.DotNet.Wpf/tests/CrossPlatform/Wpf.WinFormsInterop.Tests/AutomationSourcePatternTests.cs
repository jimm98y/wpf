// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// WinForms' accessibility tree source beyond names and actions: text (TextBoxBase), selection containers, grids and
// tables (DataGridView, a details ListView), scrolling (a list, a grid, an AutoScroll panel), windows (Form), and the
// changes raised for them -- asked through the IAutomationTreeSource the platform backends read, routed by
// AutomationTree as theirs is.
//
// The form is registered with a site of the test's own, whose screen is the driver's (no mapping), so the answers are
// in the coordinates the controls report themselves in.
//

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using MS.Internal.Interop;
using WinFormsWebGpu.Accessibility;
using Xunit;
using SWF = System.Windows.Forms;

namespace Wpf.WinFormsInterop.Tests
{
    [Collection(ProcessDpiStateCollection.Name)]
    public class AutomationSourcePatternTests
    {
        private static int s_handles = 0x5A110000;

        private sealed class Site : IA11yHostSite
        {
            public Site(SWF.Form form) { Form = form; Handle = new IntPtr(Interlocked.Increment(ref s_handles)); }
            public IntPtr Handle { get; }
            public SWF.Form Form { get; }
            public bool TryMapToScreen(Rectangle r, out double x, out double y, out double width, out double height)
            { x = r.X; y = r.Y; width = r.Width; height = r.Height; return true; }
            public bool TryMapFromScreen(double x, double y, out Point driverPoint) { driverPoint = new Point((int)x, (int)y); return true; }
            public bool TryWindowRect(out double x, out double y, out double width, out double height)
            { x = Form.Left; y = Form.Top; width = Form.Width; height = Form.Height; return true; }
        }

        private static void OnSta(Action a)
        {
            Exception? failure = null;
            var t = new Thread(() => { try { a(); } catch (Exception ex) { failure = ex; } });
            t.TrySetApartmentState(ApartmentState.STA);
            t.Start();
            t.Join();
            if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }

        /// <summary>Shows <paramref name="form"/> off screen, registers it, and runs <paramref name="test"/> with the
        /// tree and the root's id.</summary>
        private static void With(SWF.Form form, Action<IAutomationTreeSource, int, Site> test)
        {
            form.StartPosition = SWF.FormStartPosition.Manual;
            form.Location = new Point(-3300, 1500);
            form.ShowInTaskbar = false;
            var site = new Site(form);
            using (form)
            {
                form.Show();
                SWF.Application.DoEvents();
                AutomationSource.Register(site);
                try
                {
                    IAutomationTreeSource tree = AutomationTree.Current!;
                    int root = tree.GetRootId(site.Handle);
                    Assert.NotEqual(IAutomationTreeSource.InvalidNode, root);
                    test(tree, root, site);
                }
                finally { AutomationSource.Unregister(site.Handle); }
            }
        }

        /// <summary>The node of the control named <paramref name="name"/>, found by walking the tree.</summary>
        private static int Find(IAutomationTreeSource tree, int from, string name)
        {
            if (tree.GetAutomationId(from) == name) return from;
            foreach (int child in tree.GetChildIds(from))
            {
                int found = Find(tree, child, name);
                if (found != IAutomationTreeSource.InvalidNode) return found;
            }
            return IAutomationTreeSource.InvalidNode;
        }

        private static RectangleF Bounds(IAutomationTreeSource tree, int id)
        {
            Assert.True(tree.TryGetBounds(id, out double x, out double y, out double w, out double h));
            return new RectangleF((float)x, (float)y, (float)w, (float)h);
        }

        private static RectangleF RangeBounds(IAutomationTreeSource tree, int id, int start, int length)
        {
            Assert.True(tree.TryGetTextRangeBounds(id, start, length, out double x, out double y, out double w, out double h));
            return new RectangleF((float)x, (float)y, (float)w, (float)h);
        }

        // ---- text ---------------------------------------------------------------------------------------------

        [Fact]
        public void TextBoxAnswersItsTextSelectionLinesAndGeometry()
        {
            OnSta(() =>
            {
                var form = new SWF.Form { ClientSize = new Size(300, 200) };
                var box = new SWF.TextBox { Name = "box", Multiline = true, Bounds = new Rectangle(10, 10, 200, 120), Text = "alpha\r\nbeta\r\ngamma" };
                form.Controls.Add(box);
                With(form, (tree, root, site) =>
                {
                    int id = Find(tree, root, "box");
                    AccessibleState state = tree.GetState(id);
                    Assert.True(state.HasFlag(AccessibleState.HasText));
                    Assert.False(state.HasFlag(AccessibleState.ReadOnly));

                    Assert.True(tree.TryGetText(id, out string text));
                    Assert.Equal("alpha\r\nbeta\r\ngamma", text);

                    Assert.True(tree.SetTextSelection(id, 7, 4));
                    Assert.Equal((7, 4), (box.SelectionStart, box.SelectionLength));
                    Assert.True(tree.TryGetTextSelection(id, out int s, out int n));
                    Assert.Equal((7, 4), (s, n));

                    // lines are the box's, each with its break; the last without one
                    Assert.True(tree.TryGetTextLine(id, 8, out int ls, out int ll, out int number));
                    Assert.Equal((7, 6, 1), (ls, ll, number));
                    Assert.True(tree.TryGetTextLine(id, text.Length, out ls, out ll, out number));
                    Assert.Equal((13, 5, 2), (ls, ll, number));
                    Assert.Equal(box.GetFirstCharIndexFromLine(1), 7);

                    // "beta" is drawn inside the box, under "alpha", and starts where "alpha" does
                    RectangleF boxBounds = Bounds(tree, id);
                    RectangleF alpha = RangeBounds(tree, id, 0, 5), beta = RangeBounds(tree, id, 7, 4);
                    Assert.True(boxBounds.Contains(beta), $"{beta} in {boxBounds}");
                    Assert.True(beta.Top >= alpha.Bottom - 1, $"{beta} under {alpha}");
                    Assert.InRange(beta.Left - alpha.Left, -1, 1);
                    Assert.True(alpha.Width > beta.Width / 2);

                    // and a point in a character's cell is that character
                    RectangleF e = RangeBounds(tree, id, 8, 1);
                    Assert.Equal(8, tree.GetTextOffsetAtPoint(id, e.X + e.Width / 2, e.Y + e.Height / 2));
                    Assert.Equal(-1, tree.GetTextOffsetAtPoint(id, boxBounds.Right + 50, boxBounds.Top));

                    Assert.True(tree.TryGetVisibleTextRange(id, out int vs, out int vl));
                    Assert.Equal((0, text.Length), (vs, vl));

                    box.ReadOnly = true;
                    Assert.True(tree.GetState(id).HasFlag(AccessibleState.ReadOnly));
                });
            });
        }

        [Fact]
        public void WrappedTextCountsTheLinesOnTheScreen()
        {
            OnSta(() =>
            {
                var form = new SWF.Form { ClientSize = new Size(300, 200) };
                string words = "one two three four five six seven eight nine ten eleven twelve";
                var box = new SWF.TextBox { Name = "box", Multiline = true, WordWrap = true, Bounds = new Rectangle(10, 10, 80, 150), Text = words };
                form.Controls.Add(box);
                With(form, (tree, root, site) =>
                {
                    int id = Find(tree, root, "box");
                    Assert.True(tree.TryGetTextLine(id, words.Length - 1, out int start, out _, out int number));
                    Assert.True(number > 0, "the paragraph wraps");
                    Assert.True(start > 0);
                    // numbered from 0, as the box's own GetLineFromCharIndex and GetFirstCharIndexFromLine number them
                    Assert.Equal(box.GetLineFromCharIndex(words.Length - 1), number);
                    Assert.Equal(box.GetFirstCharIndexFromLine(number), start);
                    // the first line holds the first word and stops before the text does
                    Assert.True(tree.TryGetTextLine(id, 0, out int s0, out int l0, out int n0));
                    Assert.Equal(0, s0);
                    Assert.Equal(0, n0);
                    Assert.InRange(l0, 3, words.Length - 1);
                });
            });
        }

        [Fact]
        public void PasswordBoxHasNoText()
        {
            OnSta(() =>
            {
                var form = new SWF.Form { ClientSize = new Size(300, 100) };
                form.Controls.Add(new SWF.TextBox { Name = "pw", Bounds = new Rectangle(10, 10, 100, 20), Text = "secret", UseSystemPasswordChar = true });
                form.Controls.Add(new SWF.MaskedTextBox { Name = "pin", Bounds = new Rectangle(10, 40, 100, 20), Text = "1234", PasswordChar = '*' });
                With(form, (tree, root, site) =>
                {
                    foreach (string name in new[] { "pw", "pin" })
                    {
                        int id = Find(tree, root, name);
                        AccessibleState state = tree.GetState(id);
                        Assert.True(state.HasFlag(AccessibleState.Password), name);
                        Assert.False(state.HasFlag(AccessibleState.HasText), name);
                        Assert.False(tree.TryGetText(id, out string text), name);
                        Assert.Equal(string.Empty, text);
                        Assert.False(tree.TryGetTextRangeBounds(id, 0, 1, out _, out _, out _, out _), name);
                    }
                });
            });
        }

        // ---- selection ----------------------------------------------------------------------------------------

        [Fact]
        public void ListBoxSelectionIsItsRowNodes()
        {
            OnSta(() =>
            {
                var form = new SWF.Form { ClientSize = new Size(300, 200) };
                var list = new SWF.ListBox { Name = "list", Bounds = new Rectangle(10, 10, 120, 150), SelectionMode = SWF.SelectionMode.MultiExtended };
                list.Items.AddRange(new object[] { "a", "b", "c", "d" });
                var single = new SWF.ListBox { Name = "single", Bounds = new Rectangle(150, 10, 120, 150) };
                single.Items.AddRange(new object[] { "x", "y" });
                form.Controls.Add(list);
                form.Controls.Add(single);
                With(form, (tree, root, site) =>
                {
                    int id = Find(tree, root, "list");
                    AccessibleState state = tree.GetState(id);
                    Assert.True(state.HasFlag(AccessibleState.SelectionContainer));
                    Assert.True(state.HasFlag(AccessibleState.MultiSelectable));
                    list.SetSelected(1, true);
                    list.SetSelected(3, true);
                    int[] rows = tree.GetChildIds(id);
                    Assert.Equal(new[] { rows[1], rows[3] }, tree.GetSelectedIds(id));

                    int other = Find(tree, root, "single");
                    Assert.True(tree.GetState(other).HasFlag(AccessibleState.SelectionContainer));
                    Assert.False(tree.GetState(other).HasFlag(AccessibleState.MultiSelectable));
                    single.SelectedIndex = 1;
                    Assert.Equal(new[] { tree.GetChildIds(other)[1] }, tree.GetSelectedIds(other));
                });
            });
        }

        [Fact]
        public void TreeAndTabSelectionsAreTheirNodes()
        {
            OnSta(() =>
            {
                var form = new SWF.Form { ClientSize = new Size(400, 250) };
                var treeView = new SWF.TreeView { Name = "tree", Bounds = new Rectangle(10, 10, 150, 200) };
                treeView.Nodes.Add("one");
                treeView.Nodes.Add("two");
                var tabs = new SWF.TabControl { Name = "tabs", Bounds = new Rectangle(170, 10, 200, 200) };
                tabs.TabPages.Add(new SWF.TabPage("first") { Name = "first" });
                tabs.TabPages.Add(new SWF.TabPage("second") { Name = "second" });
                form.Controls.Add(treeView);
                form.Controls.Add(tabs);
                With(form, (tree, root, site) =>
                {
                    int t = Find(tree, root, "tree");
                    treeView.SelectedNode = treeView.Nodes[1];
                    Assert.Equal(new[] { tree.GetChildIds(t)[1] }, tree.GetSelectedIds(t));
                    Assert.Equal("two", tree.GetName(tree.GetSelectedIds(t)[0]));

                    int tc = Find(tree, root, "tabs");
                    Assert.True(tree.GetState(tc).HasFlag(AccessibleState.SelectionContainer));
                    tabs.SelectedIndex = 1;
                    Assert.Equal(new[] { Find(tree, root, "second") }, tree.GetSelectedIds(tc));
                });
            });
        }

        [Fact]
        public void ComboBoxSelectionIsAnEntryOfItsList()
        {
            OnSta(() =>
            {
                var form = new SWF.Form { ClientSize = new Size(300, 250) };
                var combo = new SWF.ComboBox { Name = "combo", DropDownStyle = SWF.ComboBoxStyle.DropDownList, Bounds = new Rectangle(10, 10, 150, 24) };
                combo.Items.AddRange(new object[] { "red", "green", "blue" });
                form.Controls.Add(combo);
                With(form, (tree, root, site) =>
                {
                    int c = Find(tree, root, "combo");
                    Assert.True(tree.GetState(c).HasFlag(AccessibleState.SelectionContainer));
                    Assert.False(tree.GetState(c).HasFlag(AccessibleState.MultiSelectable));
                    Assert.Empty(tree.GetSelectedIds(c));

                    // shut, it is the field and the button that opens the list
                    Assert.Equal(2, tree.GetChildIds(c).Length);

                    combo.SelectedIndex = 2;
                    int[] picked = tree.GetSelectedIds(c);
                    Assert.Single(picked);
                    Assert.Equal("blue", tree.GetName(picked[0]));
                    Assert.Equal(7, tree.GetRole(picked[0]));   // ListItem
                    AccessibleState pickedState = tree.GetState(picked[0]);
                    Assert.True(pickedState.HasFlag(AccessibleState.Selectable));
                    Assert.True(pickedState.HasFlag(AccessibleState.Selected));

                    // open, the list is published with its entries, and the selection is one of them
                    combo.DroppedDown = true;
                    SWF.Application.DoEvents();
                    try
                    {
                        int list = Array.Find(tree.GetChildIds(c), id => tree.GetRole(id) == 8);   // List
                        Assert.NotEqual(0, list);
                        Assert.Equal(c, tree.GetParentId(list));
                        int[] entries = tree.GetChildIds(list);
                        Assert.Equal(new[] { "red", "green", "blue" }, Array.ConvertAll(entries, tree.GetName));
                        Assert.Equal(entries[2], picked[0]);
                        Assert.Equal(list, tree.GetParentId(entries[0]));
                        Assert.True(tree.GetState(entries[0]).HasFlag(AccessibleState.Selectable));
                        Assert.False(tree.GetState(entries[0]).HasFlag(AccessibleState.Selected));
                        // an entry on the screen is where the list draws it: in the list, which drops out of the
                        // box -- below it, or above it where the screen has no room below
                        RectangleF first = Bounds(tree, entries[0]), box = Bounds(tree, c), shown = Bounds(tree, list);
                        Assert.True(shown.Contains(first), $"{first} in {shown}");
                        Assert.True(first.Top >= box.Bottom - 1 || first.Bottom <= box.Top + 1, $"{first} outside {box}");

                        // selecting an entry puts the box on it
                        Assert.True(tree.DoAction(entries[1], AccessibleAction.Select));
                        Assert.Equal(1, combo.SelectedIndex);
                        Assert.Equal(new[] { entries[1] }, tree.GetSelectedIds(c));
                    }
                    finally { combo.DroppedDown = false; SWF.Application.DoEvents(); }

                    Assert.Equal(2, tree.GetChildIds(c).Length);
                });
            });
        }

        [Fact]
        public void SimpleComboBoxAlwaysPublishesItsList()
        {
            OnSta(() =>
            {
                var form = new SWF.Form { ClientSize = new Size(300, 250) };
                var combo = new SWF.ComboBox { Name = "combo", DropDownStyle = SWF.ComboBoxStyle.Simple, Bounds = new Rectangle(10, 10, 150, 150) };
                combo.Items.AddRange(new object[] { "one", "two" });
                form.Controls.Add(combo);
                With(form, (tree, root, site) =>
                {
                    int c = Find(tree, root, "combo");
                    int list = Array.Find(tree.GetChildIds(c), id => tree.GetRole(id) == 8);
                    Assert.NotEqual(0, list);
                    int[] entries = tree.GetChildIds(list);
                    Assert.Equal(new[] { "one", "two" }, Array.ConvertAll(entries, tree.GetName));
                    combo.SelectedIndex = 0;
                    Assert.Equal(new[] { entries[0] }, tree.GetSelectedIds(c));
                });
            });
        }

        // ---- grids --------------------------------------------------------------------------------------------

        private static SWF.DataGridView Grid(int rows)
        {
            var grid = new SWF.DataGridView { Name = "grid", Bounds = new Rectangle(10, 10, 360, 200), AllowUserToAddRows = false };
            grid.Columns.Add("a", "A");
            grid.Columns.Add("b", "B");
            grid.Columns.Add("c", "C");
            for (int r = 0; r < rows; r++) grid.Rows.Add($"a{r}", $"b{r}", $"c{r}");
            return grid;
        }

        [Fact]
        public void DataGridViewIsATableOfItsCells()
        {
            OnSta(() =>
            {
                var form = new SWF.Form { ClientSize = new Size(400, 250) };
                var grid = Grid(5);
                form.Controls.Add(grid);
                With(form, (tree, root, site) =>
                {
                    int g = Find(tree, root, "grid");
                    AccessibleState state = tree.GetState(g);
                    Assert.True(state.HasFlag(AccessibleState.Grid));
                    Assert.True(state.HasFlag(AccessibleState.Table));

                    Assert.True(tree.TryGetGrid(g, out int rows, out int columns));
                    Assert.Equal((5, 3), (rows, columns));

                    int cell = tree.GetGridCell(g, 2, 1);
                    Assert.NotEqual(IAutomationTreeSource.InvalidNode, cell);
                    Assert.True(tree.GetState(cell).HasFlag(AccessibleState.GridItem));
                    Assert.True(tree.TryGetGridItem(cell, out int r, out int c, out int rs, out int cs, out int gridId));
                    Assert.Equal((2, 1, 1, 1, g), (r, c, rs, cs, gridId));
                    Assert.Equal("B Row 3, Not sorted.", tree.GetName(cell));
                    // the cell asked for is the one the tree walk reaches: a child of its row, which is the grid's
                    int row = tree.GetParentId(cell);
                    Assert.Contains(cell, tree.GetChildIds(row));
                    Assert.Contains(row, tree.GetChildIds(g));
                    Assert.Equal(IAutomationTreeSource.InvalidNode, tree.GetGridCell(g, 5, 0));
                    Assert.Equal(IAutomationTreeSource.InvalidNode, tree.GetGridCell(g, 0, 3));

                    int[] headers = tree.GetColumnHeaderIds(g);
                    Assert.Equal(3, headers.Length);
                    Assert.Equal("B", tree.GetName(headers[1]));
                    Assert.Equal(5, tree.GetRowHeaderIds(g).Length);

                    grid.RowHeadersVisible = false;
                    Assert.Empty(tree.GetRowHeaderIds(g));
                });
            });
        }

        [Fact]
        public void DetailsListViewIsATableOfItemsAndSubitems()
        {
            OnSta(() =>
            {
                var form = new SWF.Form { ClientSize = new Size(400, 250) };
                var view = new SWF.ListView { Name = "view", View = SWF.View.Details, Bounds = new Rectangle(10, 10, 360, 200) };
                view.Columns.Add("Name", 100);
                view.Columns.Add("Size", 100);
                view.Items.Add(new SWF.ListViewItem(new[] { "one", "1" }));
                view.Items.Add(new SWF.ListViewItem(new[] { "two", "2" }));
                form.Controls.Add(view);
                With(form, (tree, root, site) =>
                {
                    int v = Find(tree, root, "view");
                    Assert.True(tree.GetState(v).HasFlag(AccessibleState.Grid));
                    Assert.True(tree.GetState(v).HasFlag(AccessibleState.Table));
                    Assert.True(tree.TryGetGrid(v, out int rows, out int columns));
                    Assert.Equal((2, 2), (rows, columns));
                    int cell = tree.GetGridCell(v, 1, 1);
                    Assert.Equal("2", tree.GetName(cell));
                    Assert.True(tree.TryGetGridItem(cell, out int r, out int c, out _, out _, out int gridId));
                    Assert.Equal((1, 1, v), (r, c, gridId));
                    int[] headers = tree.GetColumnHeaderIds(v);
                    Assert.Equal(new[] { "Name", "Size" }, Array.ConvertAll(headers, tree.GetName));

                    view.View = SWF.View.List;
                    Assert.False(tree.GetState(v).HasFlag(AccessibleState.Grid));
                    Assert.False(tree.TryGetGrid(v, out _, out _));
                });
            });
        }

        // ---- scrolling ----------------------------------------------------------------------------------------

        [Fact]
        public void ListBoxScrollsByItsBar()
        {
            OnSta(() =>
            {
                var form = new SWF.Form { ClientSize = new Size(300, 200) };
                var list = new SWF.ListBox { Name = "list", Bounds = new Rectangle(10, 10, 120, 100) };
                for (int i = 0; i < 50; i++) list.Items.Add("item " + i);
                form.Controls.Add(list);
                With(form, (tree, root, site) =>
                {
                    int id = Find(tree, root, "list");
                    Assert.True(tree.GetState(id).HasFlag(AccessibleState.Scrollable));
                    Assert.True(tree.TryGetScroll(id, out double hp, out double vp, out _, out double vv, out bool hs, out bool vs));
                    Assert.True(vs);
                    Assert.False(hs);
                    Assert.Equal(-1, hp);
                    Assert.Equal(0, vp);
                    Assert.InRange(vv, 1, 99);

                    Assert.True(tree.Scroll(id, 2, 4));
                    Assert.True(list.TopIndex > 0);
                    Assert.True(tree.TryGetScroll(id, out _, out vp, out _, out _, out _, out _));
                    Assert.True(vp > 0);

                    Assert.True(tree.SetScrollPercent(id, -1, 100));
                    Assert.True(tree.TryGetScroll(id, out _, out vp, out _, out _, out _, out _));
                    Assert.Equal(100, vp);

                    int first = tree.GetChildIds(id)[0];
                    Assert.True(tree.GetState(first).HasFlag(AccessibleState.ScrollItem));
                    Assert.True(tree.ScrollIntoView(first));
                    Assert.Equal(0, list.TopIndex);
                });
            });
        }

        [Fact]
        public void DataGridViewScrollsItsRows()
        {
            OnSta(() =>
            {
                var form = new SWF.Form { ClientSize = new Size(400, 250) };
                var grid = Grid(100);
                form.Controls.Add(grid);
                With(form, (tree, root, site) =>
                {
                    int g = Find(tree, root, "grid");
                    Assert.True(tree.GetState(g).HasFlag(AccessibleState.Scrollable));
                    Assert.True(tree.TryGetScroll(g, out _, out double vp, out _, out _, out _, out bool vs));
                    Assert.True(vs);
                    Assert.Equal(0, vp);

                    Assert.True(tree.SetScrollPercent(g, -1, 100));
                    Assert.True(tree.TryGetScroll(g, out _, out vp, out _, out _, out _, out _));
                    Assert.True(vp > 95, $"at {vp}");
                    Assert.True(grid.VerticalScrollingOffset > 0);

                    Assert.True(tree.ScrollIntoView(tree.GetGridCell(g, 0, 0)));
                    Assert.Equal(0, grid.VerticalScrollingOffset);

                    Assert.True(tree.Scroll(g, 2, 3));
                    Assert.True(grid.VerticalScrollingOffset > 0);
                });
            });
        }

        [Fact]
        public void AutoScrollPanelScrollsItsChildren()
        {
            OnSta(() =>
            {
                var form = new SWF.Form { ClientSize = new Size(300, 300) };
                var panel = new SWF.Panel { Name = "panel", AutoScroll = true, Bounds = new Rectangle(10, 10, 200, 200) };
                var top = new SWF.Button { Name = "top", Text = "top", Bounds = new Rectangle(10, 10, 80, 25) };
                var bottom = new SWF.Button { Name = "bottom", Text = "bottom", Bounds = new Rectangle(10, 900, 80, 25) };
                panel.Controls.Add(top);
                panel.Controls.Add(bottom);
                form.Controls.Add(panel);
                With(form, (tree, root, site) =>
                {
                    int p = Find(tree, root, "panel");
                    Assert.True(tree.GetState(p).HasFlag(AccessibleState.Scrollable));
                    Assert.True(tree.TryGetScroll(p, out _, out double vp, out _, out double vv, out _, out bool vs));
                    Assert.True(vs);
                    Assert.Equal(0, vp);
                    Assert.InRange(vv, 1, 50);

                    Assert.True(tree.SetScrollPercent(p, -1, 100));
                    Assert.True(tree.TryGetScroll(p, out _, out vp, out _, out _, out _, out _));
                    Assert.Equal(100, vp);

                    Assert.True(tree.Scroll(p, 2, 1));
                    Assert.True(tree.TryGetScroll(p, out _, out vp, out _, out _, out _, out _));
                    Assert.InRange(vp, 1, 99.9);

                    // brought just into view, at the top of the panel
                    Assert.True(tree.ScrollIntoView(Find(tree, root, "top")));
                    Assert.Equal(0, top.Top);

                    // a panel whose children fit does not scroll
                    bottom.Top = 100;
                    SWF.Application.DoEvents();
                    Assert.False(tree.GetState(p).HasFlag(AccessibleState.Scrollable));
                });
            });
        }

        // ---- windows ------------------------------------------------------------------------------------------

        [Fact]
        public void FormIsAWindow()
        {
            OnSta(() =>
            {
                var form = new SWF.Form { ClientSize = new Size(300, 200), MaximizeBox = false, TopMost = true };
                bool closed = false;
                form.FormClosed += (_, _) => closed = true;
                With(form, (tree, root, site) =>
                {
                    AccessibleState state = tree.GetState(root);
                    Assert.True(state.HasFlag(AccessibleState.Window));
                    Assert.False(state.HasFlag(AccessibleState.Modal));
                    Assert.False(state.HasFlag(AccessibleState.ScrollItem));

                    Assert.True(tree.TryGetWindow(root, out int visual, out bool canMin, out bool canMax, out bool topmost));
                    Assert.Equal((0, true, false, true), (visual, canMin, canMax, topmost));

                    Assert.False(tree.SetWindowVisualState(root, 1));
                    Assert.True(tree.SetWindowVisualState(root, 2));
                    Assert.Equal(SWF.FormWindowState.Minimized, form.WindowState);
                    Assert.True(tree.TryGetWindow(root, out visual, out _, out _, out _));
                    Assert.Equal(2, visual);
                    Assert.True(tree.SetWindowVisualState(root, 0));
                    Assert.Equal(SWF.FormWindowState.Normal, form.WindowState);

                    form.MaximizeBox = true;
                    Assert.True(tree.SetWindowVisualState(root, 1));
                    Assert.Equal(SWF.FormWindowState.Maximized, form.WindowState);
                    form.WindowState = SWF.FormWindowState.Normal;

                    Assert.True(tree.CloseWindow(root));
                    for (int i = 0; i < 20 && !closed; i++) SWF.Application.DoEvents();
                    Assert.True(closed);
                });
            });
        }

        [Fact]
        public void DialogIsModal()
        {
            OnSta(() =>
            {
                var dialog = new SWF.Form { ClientSize = new Size(200, 100), StartPosition = SWF.FormStartPosition.Manual, Location = new Point(-3300, 1500), ShowInTaskbar = false };
                AccessibleState? seen = null;
                dialog.Shown += (_, _) =>
                {
                    var site = new Site(dialog);
                    AutomationSource.Register(site);
                    try
                    {
                        IAutomationTreeSource tree = AutomationTree.Current!;
                        seen = tree.GetState(tree.GetRootId(site.Handle));
                    }
                    finally { AutomationSource.Unregister(site.Handle); }
                    dialog.BeginInvoke(new Action(dialog.Close));
                };
                using (dialog) dialog.ShowDialog();
                Assert.NotNull(seen);
                Assert.True(seen!.Value.HasFlag(AccessibleState.Window));
                Assert.True(seen.Value.HasFlag(AccessibleState.Modal));
            });
        }

        // ---- changes ------------------------------------------------------------------------------------------

        [Fact]
        public void ChangesAreRaisedForTextSelectionAndScrolling()
        {
            OnSta(() =>
            {
                var form = new SWF.Form { ClientSize = new Size(400, 300) };
                var box = new SWF.TextBox { Name = "box", Bounds = new Rectangle(10, 10, 150, 20), Text = "hello" };
                var list = new SWF.ListBox { Name = "list", Bounds = new Rectangle(10, 40, 120, 100) };
                for (int i = 0; i < 30; i++) list.Items.Add("item " + i);
                var panel = new SWF.Panel { Name = "panel", AutoScroll = true, Bounds = new Rectangle(200, 10, 150, 150) };
                panel.Controls.Add(new SWF.Button { Bounds = new Rectangle(0, 600, 50, 20) });
                form.Controls.Add(box);
                form.Controls.Add(list);
                form.Controls.Add(panel);
                With(form, (tree, root, site) =>
                {
                    int b = Find(tree, root, "box"), l = Find(tree, root, "list"), p = Find(tree, root, "panel");
                    var seen = new List<(AutomationChangeKind, int)>();
                    Action<AutomationChange> listener = c => { if (c.WindowHandle == site.Handle) lock (seen) seen.Add((c.Kind, c.NodeId)); };
                    AutomationTree.Changed += listener;
                    try
                    {
                        box.Text = "hello world";
                        Assert.Contains((AutomationChangeKind.TextChanged, b), seen);
                        box.Select(1, 3);
                        Assert.Contains((AutomationChangeKind.TextSelectionChanged, b), seen);

                        list.SelectedIndex = 2;
                        Assert.Contains((AutomationChangeKind.SelectionContainerChanged, l), seen);

                        Assert.True(tree.Scroll(l, 2, 3));
                        Assert.Contains((AutomationChangeKind.ScrollChanged, l), seen);

                        Assert.True(tree.SetScrollPercent(p, -1, 50));
                        SWF.Application.DoEvents();
                        Assert.Contains((AutomationChangeKind.ScrollChanged, p), seen);
                    }
                    finally { AutomationTree.Changed -= listener; }
                });
            });
        }
    }
}
