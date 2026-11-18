// What WinForms' controls answer for the backends' selection, grid, table, scroll and window questions, and the
// changes to those they hear about.
//
// The elements are the ones the tree already publishes (A11y, A11yItems): a grid's cells, a list's rows and a tree's
// nodes are the same objects GetChildIds hands out, found by walking the tree rather than made up here, so that a cell
// a backend reaches through a grid question is the very node it reached by walking -- the same id, the same name.

extern alias WindowsBase;

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using AutomationChange = WindowsBase::MS.Internal.Interop.AutomationChange;
using AutomationChangeKind = WindowsBase::MS.Internal.Interop.AutomationChangeKind;
using AutomationTree = WindowsBase::MS.Internal.Interop.AutomationTree;
using IAutomationTreeSource = WindowsBase::MS.Internal.Interop.IAutomationTreeSource;

namespace WinFormsWebGpu.Accessibility
{
    internal sealed partial class AutomationSource
    {
        // ---- selection ----------------------------------------------------------------------------------------

        // A list, a details view, a tree and a tab strip (A11y.HasSelection). A combo box is not one here: its list's
        // entries are not elements of this tree (it publishes the field and the button that opens the list), so there
        // would be nothing for its selection to point at.
        public int[] GetSelectedIds(int id)
        {
            if (ElementOf(id) is not Control c || !A11y.HasSelection(c)) return Array.Empty<int>();
            try
            {
                var ids = new List<int>();
                foreach (object picked in A11y.SelectionOf(c)) ids.Add(IdOf(picked));
                return ids.ToArray();
            }
            catch (Exception) { return Array.Empty<int>(); }
        }

        // ---- grids and tables -------------------------------------------------------------------------------

        private static bool IsDetails(ListView view) => view != null && view.View == View.Details;

        /// <summary>The grid rows a DataGridView publishes: its visible rows, by index. A grid question counts rows
        /// the way the tree lists them, so the nth row asked for is the nth row a walk finds.</summary>
        private static List<int> GridRows(DataGridView grid)
        {
            var rows = new List<int>();
            for (int i = 0; i < grid.Rows.Count; i++)
                if (grid.Rows[i].Visible) rows.Add(i);
            return rows;
        }

        // a key the grid publishes among its children (or a row among its own), found by what it is
        private static ItemKey ChildKey(object parent, string kind, int index)
        {
            foreach (object child in A11yItems.ChildrenOf(parent))
                if (child is ItemKey k && k.Kind == kind && k.Index == index) return k;
            return null;
        }

        private static ItemKey CellKey(DataGridView grid, int rowIndex, int column)
        {
            ItemKey row = ChildKey(grid, "gridrow", rowIndex);
            return row == null ? null : ChildKey(row, "cell" + rowIndex + "_", column);
        }

        // the row a cell key names: DataGridView's "cell<row>_", a details view's "cell<row>"
        private static bool CellRow(ItemKey key, out int row)
        {
            row = -1;
            if (key == null || !key.Kind.StartsWith("cell", StringComparison.Ordinal)) return false;
            if (key.Owner is DataGridView)
                return key.Kind.EndsWith("_", StringComparison.Ordinal) && int.TryParse(key.Kind.Substring(4, key.Kind.Length - 5), out row);
            return key.Owner is ListView && int.TryParse(key.Kind.Substring(4), out row);
        }

        private static bool IsGridCell(object element)
            => element is ItemKey key && CellRow(key, out _) && (key.Owner is DataGridView || IsDetails(key.Owner as ListView));

        public bool TryGetGrid(int id, out int rows, out int columns)
        {
            rows = columns = 0;
            try
            {
                switch (ElementOf(id))
                {
                    case DataGridView grid:
                        rows = GridRows(grid).Count;
                        columns = grid.Columns.Count;
                        return true;
                    case ListView view when IsDetails(view):
                        rows = view.Items.Count;
                        columns = view.Columns.Count;
                        return true;
                }
            }
            catch (Exception) { }
            return false;
        }

        public int GetGridCell(int id, int row, int column)
        {
            if (row < 0 || column < 0) return IAutomationTreeSource.InvalidNode;
            try
            {
                switch (ElementOf(id))
                {
                    case DataGridView grid:
                    {
                        List<int> rows = GridRows(grid);
                        if (row >= rows.Count || column >= grid.Columns.Count) break;
                        ItemKey cell = CellKey(grid, rows[row], column);
                        return cell == null ? IAutomationTreeSource.InvalidNode : IdOf(cell);
                    }
                    case ListView view when IsDetails(view):
                    {
                        // a row publishes a cell per subitem it has; a column it has none for has no cell
                        if (row >= view.Items.Count || column >= view.Columns.Count) break;
                        IList<object> cells = A11yItems.ChildrenOf(view.Items[row]);
                        return column < cells.Count ? IdOf(cells[column]) : IAutomationTreeSource.InvalidNode;
                    }
                }
            }
            catch (Exception) { }
            return IAutomationTreeSource.InvalidNode;
        }

        public bool TryGetGridItem(int id, out int row, out int column, out int rowSpan, out int columnSpan, out int gridId)
        {
            row = column = rowSpan = columnSpan = 0;
            gridId = IAutomationTreeSource.InvalidNode;
            object element = ElementOf(id);
            if (!IsGridCell(element)) return false;
            try
            {
                var key = (ItemKey)element;
                CellRow(key, out int index);
                row = key.Owner is DataGridView grid ? GridRows(grid).IndexOf(index) : index;
                if (row < 0) return false;
                column = key.Index;
                rowSpan = columnSpan = 1;
                gridId = IdOf(key.Owner);
                return true;
            }
            catch (Exception) { return false; }
        }

        private static bool HasHeaders(object element) => element switch
        {
            DataGridView grid => grid.ColumnHeadersVisible || grid.RowHeadersVisible,
            ListView view => IsDetails(view) && view.HeaderStyle != ColumnHeaderStyle.None,
            _ => false,
        };

        public int[] GetColumnHeaderIds(int id)
        {
            var ids = new List<int>();
            try
            {
                switch (ElementOf(id))
                {
                    case DataGridView grid when grid.ColumnHeadersVisible:
                        if (ChildKey(grid, "headerrow", 0) is { } headers)
                            foreach (object part in A11yItems.ChildrenOf(headers))
                                if (part is ItemKey k && k.Kind == "colheader") ids.Add(IdOf(k));
                        break;
                    // a details view's headers are its ColumnHeaders, which name themselves and know where they are
                    // drawn; the tree publishes the strip they sit in as one pane, as Windows does
                    case ListView view when HasHeaders(view):
                        foreach (ColumnHeader header in view.Columns) ids.Add(IdOf(header));
                        break;
                }
            }
            catch (Exception) { }
            return ids.ToArray();
        }

        public int[] GetRowHeaderIds(int id)
        {
            var ids = new List<int>();
            try
            {
                if (ElementOf(id) is DataGridView grid && grid.RowHeadersVisible)
                    foreach (int index in GridRows(grid))
                        if (ChildKey(grid, "gridrow", index) is { } row && ChildKey(row, "rowheader", index) is { } header)
                            ids.Add(IdOf(header));
            }
            catch (Exception) { }
            return ids.ToArray();
        }

        // ---- scrolling ----------------------------------------------------------------------------------------
        //
        // A panel that scrolls its children (AutoScroll) is moved through its AutoScrollPosition (A11y's scrolling).
        // A control that scrolls its own content -- a text box, a list, a tree, a details view, a grid -- does it with
        // scroll bars of its own, and is moved the way a user moves it: by its bars, so that every control reacts as it
        // does to a click on one (a grid follows its bars' Scroll, the others their value).

        private static (ScrollBar Horizontal, ScrollBar Vertical) BarsOf(Control c) => c switch
        {
            TextBoxBase edit => (edit.hscroll, edit.vscroll),
            ListBox list => (list.UIAHScrollBar, list.UIAVScrollBar),
            TreeView tree => (tree.UIAHScrollBar, tree.UIAVScrollBar),
            ListView view => (view.h_scroll, view.v_scroll),
            DataGridView grid => (grid.horizontalScrollBar, grid.verticalScrollBar),
            _ => (null, null),
        };

        private static bool HasBars(Control c) => c is TextBoxBase or ListBox or TreeView or ListView or DataGridView;

        // the furthest a bar's value goes: its maximum less what a page of it shows (ScrollBar.MaximumAllowed)
        private static int Furthest(ScrollBar bar) => bar.use_manual_thumb_size ? bar.Maximum - bar.manual_thumb_size + 1 : bar.Maximum - bar.LargeChange + 1;

        private static bool Scrolls(ScrollBar bar) => bar != null && !bar.IsDisposed && bar.VisibleInternal && bar.Enabled && Furthest(bar) > bar.Minimum;

        private static void BarInfo(ScrollBar bar, out double percent, out double viewSize, out bool scrollable)
        {
            percent = A11y.NoScroll;
            viewSize = 100;
            scrollable = Scrolls(bar);
            if (!scrollable) return;
            double range = Furthest(bar) - bar.Minimum;
            percent = Math.Clamp((bar.Value - bar.Minimum) * 100.0 / range, 0, 100);
            double page = bar.use_manual_thumb_size ? bar.manual_thumb_size : bar.LargeChange;
            viewSize = Math.Clamp(page * 100.0 / (bar.Maximum - bar.Minimum + 1), 0, 100);
        }

        private static bool ScrollInfoOf(Control c, out double hp, out double vp, out double hv, out double vv, out bool hs, out bool vs)
        {
            hp = vp = A11y.NoScroll;
            hv = vv = 100;
            hs = vs = false;
            if (HasBars(c))
            {
                var (h, v) = BarsOf(c);
                BarInfo(h, out hp, out hv, out hs);
                BarInfo(v, out vp, out vv, out vs);
                return hs || vs;
            }
            return A11y.ScrollInfo(c, out hp, out vp, out hv, out vv, out hs, out vs) && (hs || vs);
        }

        public bool TryGetScroll(int id, out double horizontalPercent, out double verticalPercent, out double horizontalViewSize,
            out double verticalViewSize, out bool horizontallyScrollable, out bool verticallyScrollable)
        {
            horizontalPercent = verticalPercent = A11y.NoScroll;
            horizontalViewSize = verticalViewSize = 100;
            horizontallyScrollable = verticallyScrollable = false;
            if (ElementOf(id) is not Control c) return false;
            try { return ScrollInfoOf(c, out horizontalPercent, out verticalPercent, out horizontalViewSize, out verticalViewSize, out horizontallyScrollable, out verticallyScrollable); }
            catch (Exception) { return false; }
        }

        // one step of a bar, as its arrows and its track give it (ScrollAmount: 0 large decrement, 1 small decrement,
        // 2 none, 3 large increment, 4 small increment)
        private static void Step(ScrollBar bar, int amount)
        {
            switch (amount)
            {
                case 0: bar.UIALargeDecrement(); break;
                case 1: bar.UIASmallDecrement(); break;
                case 3: bar.UIALargeIncrement(); break;
                case 4: bar.UIASmallIncrement(); break;
            }
        }

        /// <summary>Puts a control's bar at <paramref name="value"/> as dragging its thumb there would: a grid is told
        /// through its bar's Scroll (it lays its rows out from that, and settles on a row boundary), the rest follow the
        /// bar's value.</summary>
        private static void MoveBar(Control owner, ScrollBar bar, int value)
        {
            value = Math.Clamp(value, bar.Minimum, Math.Max(bar.Minimum, Furthest(bar)));
            if (owner is DataGridView grid)
            {
                bool vertical = ReferenceEquals(bar, grid.verticalScrollBar);
                var e = new ScrollEventArgs(ScrollEventType.ThumbPosition, value, vertical ? ScrollOrientation.VerticalScroll : ScrollOrientation.HorizontalScroll);
                bar.SafeValueSet(value);
                if (vertical) grid.OnVScrollBarScroll(bar, e);
                else grid.OnHScrollBarScroll(bar, e);
                return;
            }
            bar.Value = value;
        }

        public bool Scroll(int id, int horizontalAmount, int verticalAmount)
        {
            if (ElementOf(id) is not Control c || horizontalAmount is < 0 or > 4 || verticalAmount is < 0 or > 4) return false;
            if (!ScrollInfoOf(c, out _, out _, out _, out _, out bool hs, out bool vs)) return false;
            // a direction that does not scroll is left alone, as UI Automation's providers leave it
            int h = hs ? horizontalAmount : 2, v = vs ? verticalAmount : 2;
            if (h == 2 && v == 2) return false;
            return OnUiThread(c, () =>
            {
                if (HasBars(c))
                {
                    var (hb, vb) = BarsOf(c);
                    if (h != 2) Step(hb, h);
                    if (v != 2) Step(vb, v);
                }
                else A11y.ScrollBy(c, h, v);
            });
        }

        public bool SetScrollPercent(int id, double horizontalPercent, double verticalPercent)
        {
            if (ElementOf(id) is not Control c) return false;
            if (!ScrollInfoOf(c, out _, out _, out _, out _, out bool hs, out bool vs)) return false;
            double h = hs && horizontalPercent >= 0 ? Math.Min(horizontalPercent, 100) : A11y.NoScroll;
            double v = vs && verticalPercent >= 0 ? Math.Min(verticalPercent, 100) : A11y.NoScroll;
            return OnUiThread(c, () =>
            {
                if (!HasBars(c)) { A11y.SetScrollPercent(c, h, v); return; }
                var (hb, vb) = BarsOf(c);
                if (h >= 0) MoveBar(c, hb, hb.Minimum + (int)Math.Round((Furthest(hb) - hb.Minimum) * h / 100.0));
                if (v >= 0) MoveBar(c, vb, vb.Minimum + (int)Math.Round((Furthest(vb) - vb.Minimum) * v / 100.0));
            });
        }

        // Inside its control an item is brought into view the way the control brings one into view itself (a node's
        // and a details row's EnsureVisible, a list's top row, a grid's bars), then the control into view in whatever
        // panels it is in (A11y.ScrollIntoView).
        public bool ScrollIntoView(int id)
        {
            object element = ElementOf(id);
            if (element == null || element is Form) return false;
            return OnUiThread(element, () =>
            {
                switch (element)
                {
                    case TreeNode node: node.EnsureVisible(); break;
                    case ListViewItem item: item.EnsureVisible(); break;
                    case ItemKey key when key.Owner is ListBox list && key.Kind == "row": ListRowIntoView(list, key.Index); break;
                    case ItemKey key when key.Owner is ListView view && CellRow(key, out int row) && row < view.Items.Count: view.Items[row].EnsureVisible(); break;
                    case ItemKey key when key.Owner is DataGridView grid: GridPartIntoView(grid, key); break;
                }
                A11y.ScrollIntoView(element);
            });
        }

        private static void ListRowIntoView(ListBox list, int index)
        {
            if (index < 0 || index >= list.Items.Count) return;
            int height = Math.Max(1, list.ItemHeight);
            int rows = Math.Max(1, (list.items_area.Height > 0 ? list.items_area.Height : list.ClientSize.Height) / height);
            if (index < list.TopIndex) list.TopIndex = index;
            else if (index >= list.TopIndex + rows) list.TopIndex = index - rows + 1;
        }

        // A grid's row (and a cell's column) into view. The grid's own FirstDisplayedScrollingRowIndex does not move it
        // in this WinForms, so it is moved by its bars, as it moves itself to show the current cell (MoveCurrentCell).
        private static void GridPartIntoView(DataGridView grid, ItemKey key)
        {
            int row = -1, column = -1;
            if (key.Kind == "gridrow" || key.Kind == "rowheader") row = key.Index;
            else if (key.Kind == "colheader") column = key.Index;
            else if (CellRow(key, out int r)) { row = r; column = key.Index; }

            if (row >= 0 && row < grid.Rows.Count && grid.Rows[row].Visible && Scrolls(grid.verticalScrollBar))
            {
                int top = 0;
                for (int i = 0; i < row; i++)
                    if (grid.Rows[i].Visible) top += grid.Rows[i].Height;
                int offset = grid.VerticalScrollingOffset;
                int view = ViewHeight(grid);
                if (top < offset) MoveBar(grid, grid.verticalScrollBar, top);
                else if (top + grid.Rows[row].Height > offset + view) MoveBar(grid, grid.verticalScrollBar, top + grid.Rows[row].Height - view);
            }
            if (column >= 0 && column < grid.Columns.Count && grid.Columns[column].Visible && !grid.Columns[column].Frozen && Scrolls(grid.horizontalScrollBar))
            {
                int left = 0;
                foreach (DataGridViewColumn col in grid.Columns)
                {
                    if (col.DisplayIndex >= grid.Columns[column].DisplayIndex) continue;
                    if (col.Visible && !col.Frozen) left += col.Width;
                }
                int offset = grid.HorizontalScrollingOffset;
                int view = grid.ClientSize.Width - (grid.RowHeadersVisible ? grid.RowHeadersWidth : 0) - (grid.verticalScrollBar.VisibleInternal ? grid.verticalScrollBar.Width : 0);
                int width = grid.Columns[column].Width;
                if (left < offset) MoveBar(grid, grid.horizontalScrollBar, left);
                else if (left + width > offset + view) MoveBar(grid, grid.horizontalScrollBar, left + width - view);
            }
        }

        // the height a grid shows its scrolling rows in: below the headers, above the horizontal bar
        private static int ViewHeight(DataGridView grid)
            => grid.ClientSize.Height - (grid.ColumnHeadersVisible ? grid.ColumnHeadersHeight : 0)
               - (grid.horizontalScrollBar.VisibleInternal ? grid.horizontalScrollBar.Height : 0);

        // ---- windows ------------------------------------------------------------------------------------------

        // What UI Automation's window pattern reads off a form's window styles: WS_MINIMIZEBOX and WS_MAXIMIZEBOX,
        // which a form sets only where it has a control box to put the buttons in.
        private static bool CanMinimize(Form f) => f.ControlBox && f.MinimizeBox;
        private static bool CanMaximize(Form f) => f.ControlBox && f.MaximizeBox;

        public bool TryGetWindow(int id, out int visualState, out bool canMinimize, out bool canMaximize, out bool isTopmost)
        {
            visualState = 0;
            canMinimize = canMaximize = isTopmost = false;
            if (ElementOf(id) is not Form f) return false;
            try
            {
                visualState = f.WindowState switch { FormWindowState.Maximized => 1, FormWindowState.Minimized => 2, _ => 0 };
                canMinimize = CanMinimize(f);
                canMaximize = CanMaximize(f);
                isTopmost = f.TopMost;
                return true;
            }
            catch (Exception) { return false; }
        }

        public bool SetWindowVisualState(int id, int visualState)
        {
            if (ElementOf(id) is not Form f || visualState is < 0 or > 2) return false;
            // a window without the button is not minimized or maximized through the pattern either (UI Automation
            // refuses it), though restoring one always is
            if (visualState == 1 && !CanMaximize(f) || visualState == 2 && !CanMinimize(f)) return false;
            return OnUiThread(f, () => f.WindowState = visualState switch { 1 => FormWindowState.Maximized, 2 => FormWindowState.Minimized, _ => FormWindowState.Normal });
        }

        // Closed the way the close button closes it -- FormClosing may still keep it open -- and later, on its own
        // thread's queue: UI Automation's Close posts WM_CLOSE rather than sending it, so a form that asks "save
        // changes?" as it closes does not hold the assistive technology's call until someone answers.
        public bool CloseWindow(int id)
        {
            if (ElementOf(id) is not Form f) return false;
            try
            {
                if (f.IsHandleCreated) f.BeginInvoke(new Action(f.Close));
                else f.Close();
                return true;
            }
            catch (Exception) { return false; }
        }

        // ---- changes ------------------------------------------------------------------------------------------
        //
        // A control is listened to from the moment it is first given an id: until a backend has seen an element it
        // has nothing to update, and from then on it hears what WinForms' own events say -- the text and the caret of
        // an edit, the selection of a list, a tree or a tab strip, the scrolling of anything that scrolls. The handlers
        // hang off the control itself, so they go when it does.

        private sealed class TextMark { public int Start = -1, Length = -1; }

        private static void Listen(Control c)
        {
            try
            {
                switch (c)
                {
                    case TextBoxBase edit:
                        edit.TextChanged += (_, _) => { if (!IsPassword(edit)) Raise(AutomationChangeKind.TextChanged, edit); };
                        if (edit.document != null)
                        {
                            // the caret moves for every selection the box makes (keys, mouse, Select): one change per
                            // selection, not per move that leaves it where it was
                            var mark = new TextMark();
                            EventHandler moved = (_, _) =>
                            {
                                if (IsPassword(edit)) return;
                                int start = edit.SelectionStart, length = edit.SelectionLength;
                                if (start == mark.Start && length == mark.Length) return;
                                mark.Start = start;
                                mark.Length = length;
                                Raise(AutomationChangeKind.TextSelectionChanged, edit);
                            };
                            edit.document.CaretMoved += moved;
                            edit.document.SelectionSet += moved;
                        }
                        break;
                    case ListBox list: list.SelectedIndexChanged += (_, _) => Raise(AutomationChangeKind.SelectionContainerChanged, list); break;
                    case ListView view: view.SelectedIndexChanged += (_, _) => Raise(AutomationChangeKind.SelectionContainerChanged, view); break;
                    case TreeView tree: tree.AfterSelect += (_, _) => Raise(AutomationChangeKind.SelectionContainerChanged, tree); break;
                    case TabControl tabs: tabs.SelectedIndexChanged += (_, _) => Raise(AutomationChangeKind.SelectionContainerChanged, tabs); break;
                }

                if (c is DataGridView grid) grid.Scroll += (_, _) => Raise(AutomationChangeKind.ScrollChanged, grid);
                else if (HasBars(c))
                {
                    var (h, v) = BarsOf(c);
                    if (h != null) h.ValueChanged += (_, _) => Raise(AutomationChangeKind.ScrollChanged, c);
                    if (v != null) v.ValueChanged += (_, _) => Raise(AutomationChangeKind.ScrollChanged, c);
                }
                // a panel moves with its bars however it is moved (AutoScrollPosition, the wheel, a focused child
                // brought into view), and raises Scroll only for some of those
                else if (c is ScrollableControl panel)
                {
                    if (panel.hscrollbar != null) panel.hscrollbar.ValueChanged += (_, _) => Raise(AutomationChangeKind.ScrollChanged, panel);
                    if (panel.vscrollbar != null) panel.vscrollbar.ValueChanged += (_, _) => Raise(AutomationChangeKind.ScrollChanged, panel);
                }
            }
            catch (Exception) { }
        }

        private static void Raise(AutomationChangeKind kind, object element)
        {
            try
            {
                IA11yHostSite site = SiteOf(element);
                if (site == null) return;
                AutomationTree.RaiseChanged(new AutomationChange(kind, IdOf(element), site.Handle));
            }
            catch (Exception) { }
        }
    }
}
