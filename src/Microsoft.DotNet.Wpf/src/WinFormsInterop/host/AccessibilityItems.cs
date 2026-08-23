// The parts of a control that are not themselves controls: a menu's entries, a list's rows, a
// grid's cells, a tree's nodes, a calendar's days.
//
// Left out, an automation tree stops at the control. A stock WinForms gallery publishes about four
// hundred elements; this one published ninety, and everything a screen reader would actually read
// out -- the items -- was in the missing three hundred. Nothing here changes what a control draws;
// it only describes what is already on the screen.
//
// Items are addressed by the object the control already holds -- a ToolStripItem, a ListViewItem, a
// TreeNode -- because those are stable across calls, which is what lets a client tell "the same
// element again" from "a new element". Where a control keeps no such object (a list box's rows are
// whatever the caller added, a calendar's days are dates), an ItemKey stands in for one and is
// interned so the same row always yields the same key.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace WinFormsWebGpu.Accessibility
{
    /// <summary>A stand-in identity for an item a control does not keep an object for. Interned by
    /// <see cref="A11yItems"/> so that the same row is always the same key.</summary>
    internal sealed class ItemKey
    {
        internal readonly Control Owner;
        internal readonly string Kind;
        internal readonly int Index;

        internal ItemKey(Control owner, string kind, int index)
        {
            Owner = owner;
            Kind = kind;
            Index = index;
        }
    }

    internal static class A11yItems
    {
        // One interned key per (owner, kind, index), so runtime ids stay put between calls.
        private static readonly Dictionary<Control, Dictionary<string, ItemKey>> s_keys =
            new Dictionary<Control, Dictionary<string, ItemKey>>();

        private static ItemKey Key(Control owner, string kind, int index)
        {
            lock (s_keys)
            {
                Dictionary<string, ItemKey> byName;
                if (!s_keys.TryGetValue(owner, out byName))
                    s_keys[owner] = byName = new Dictionary<string, ItemKey>();
                string id = kind + ":" + index;
                ItemKey key;
                if (!byName.TryGetValue(id, out key))
                    byName[id] = key = new ItemKey(owner, kind, index);
                return key;
            }
        }

        /// <summary>The control an item belongs to, for anything that is not a Control itself.</summary>
        internal static Control OwnerOf(object element)
        {
            var key = element as ItemKey;
            if (key != null)
                return key.Owner;
            var tsi = element as ToolStripItem;
            if (tsi != null)
                return tsi.Owner;
            var lvi = element as ListViewItem;
            if (lvi != null)
                return lvi.ListView;
            var col = element as ColumnHeader;
            if (col != null)
                return col.ListView;
            var node = element as TreeNode;
            if (node != null)
                return node.TreeView;
            var page = element as TabPage;
            if (page != null)
                return page.Parent;
            return null;
        }

        /// <summary>The element this one hangs under, which is not always the control that owns
        /// it: a grid's cell belongs to its row, and the row to the grid. Answering the control
        /// for everything left every row with exactly one visible child, because a client walks
        /// from one child to the next through the parent's list and a cell was not in it.</summary>
        internal static object ParentOf(object element)
        {
            var key = element as ItemKey;
            if (key != null)
            {
                var grid = key.Owner as DataGridView;
                if (grid != null)
                {
                    if (key.Kind == "corner" || key.Kind == "colheader")
                        return Key(grid, "headerrow", 0);
                    if (key.Kind == "rowheader")
                        return Key(grid, "gridrow", key.Index);
                    if (key.Kind.StartsWith("cell", StringComparison.Ordinal))
                    {
                        int row;
                        if (int.TryParse(key.Kind.Substring(4).TrimEnd('_'), out row))
                            return Key(grid, "gridrow", row);
                    }
                }

                var calendar = key.Owner as MonthCalendar;
                if (calendar != null)
                {
                    if (key.Kind == "caltitle" || key.Kind == "caltable")
                        return Key(calendar, "calpane", 0);
                    if (key.Kind == "calrow")
                        return Key(calendar, "caltable", 0);
                    if (key.Kind == "calname")
                        return Key(calendar, "calrow", 0);
                    if (key.Kind == "calday")
                        return Key(calendar, "calrow", key.Index / 7 + 1);
                }

                // A details-view cell belongs to its row.
                var view = key.Owner as ListView;
                if (view != null && key.Kind.StartsWith("cell", StringComparison.Ordinal))
                {
                    int row;
                    if (int.TryParse(key.Kind.Substring(4), out row) && row < view.Items.Count)
                        return view.Items[row];
                }
            }
            return OwnerOf(element);
        }

        /// <summary>The items of a control, in reading order. Empty for a control that has none.</summary>
        internal static IList<object> ChildrenOf(object element)
        {
            var list = new List<object>();
            var control = element as Control;
            if (control == null)
            {
                AddNestedItems(element, list);
                return list;
            }

            try
            {
                var strip = control as ToolStrip;
                if (strip != null)
                {
                    foreach (ToolStripItem item in strip.Items)
                        if (item != null && item.Available)
                            list.Add(item);
                    return list;
                }

                var view = control as ListView;
                if (view != null)
                {
                    if (view.View == View.Details)
                        foreach (ColumnHeader col in view.Columns)
                            list.Add(col);
                    foreach (ListViewItem item in view.Items)
                        list.Add(item);
                    return list;
                }

                var tree = control as TreeView;
                if (tree != null)
                {
                    foreach (TreeNode node in tree.Nodes)
                        list.Add(node);
                    return list;
                }

                var tabs = control as TabControl;
                if (tabs != null)
                {
                    // The page is a control and shows as a pane; the tab you click on is a
                    // separate element, which is what Windows publishes as well.
                    foreach (TabPage page in tabs.TabPages)
                        list.Add(page);
                    for (int i = 0; i < tabs.TabCount; i++)
                        list.Add(Key(tabs, "tabitem", i));
                    return list;
                }

                // A link label reads as its text with the links inside it.
                var link_label = control as LinkLabel;
                if (link_label != null)
                {
                    for (int i = 0; i < link_label.Links.Count; i++)
                        list.Add(Key(link_label, "link", i));
                    return list;
                }

                // A list box keeps no object per row -- the items are whatever was added -- so the
                // row index stands in for one.
                var list_box = control as ListBox;
                if (list_box != null)
                {
                    for (int i = 0; i < list_box.Items.Count; i++)
                        list.Add(Key(list_box, "row", i));
                    return list;
                }

                // A scroll bar reads as its parts: the two arrows, the thumb, and the empty track
                // either side of it. That is what lets an assistive technology say where the view
                // sits, and what a test harness clicks.
                var bar = control as ScrollBar;
                if (bar != null)
                {
                    list.Add(Key(bar, "arrow", 0));
                    if (PageArea(bar, true).Height > 0 || PageArea(bar, true).Width > 0)
                        list.Add(Key(bar, "page", 0));
                    list.Add(Key(bar, "thumb", 0));
                    if (PageArea(bar, false).Height > 0 || PageArea(bar, false).Width > 0)
                        list.Add(Key(bar, "page", 1));
                    list.Add(Key(bar, "arrow", 1));
                    return list;
                }

                // A calendar reads the way Windows describes one: the two arrows, then a pane for
                // the month holding its title and a table of week rows -- the first of which is
                // the day names.
                var calendar = control as MonthCalendar;
                if (calendar != null)
                {
                    list.Add(Key(calendar, "calprev", 0));
                    list.Add(Key(calendar, "calnext", 0));
                    list.Add(Key(calendar, "calpane", 0));
                    return list;
                }

                // A grid reads as a row of column headers followed by its rows; each row carries
                // its own header cell and then its data cells.
                var grid = control as DataGridView;
                if (grid != null)
                {
                    if (grid.ColumnHeadersVisible)
                        list.Add(Key(grid, "headerrow", 0));
                    for (int i = 0; i < grid.Rows.Count; i++)
                        if (grid.Rows[i].Visible)
                            list.Add(Key(grid, "gridrow", i));
                    return list;
                }
            }
            catch (Exception)
            {
                // A control disposed mid-walk must not take the caller down with it.
            }
            return list;
        }

        private static void AddNestedItems(object element, List<object> list)
        {
            try
            {
                var node = element as TreeNode;
                if (node != null)
                {
                    foreach (TreeNode child in node.Nodes)
                        list.Add(child);
                    return;
                }

                // A row in a details view reads as its cells.
                var item = element as ListViewItem;
                if (item != null && item.ListView != null && item.ListView.View == View.Details)
                {
                    for (int i = 0; i < item.SubItems.Count; i++)
                        list.Add(Key(item.ListView, "cell" + item.Index, i));
                    return;
                }

                var key = element as ItemKey;
                if (key != null)
                {
                    var calendar = key.Owner as MonthCalendar;
                    if (calendar != null)
                    {
                        if (key.Kind == "calpane")
                        {
                            list.Add(Key(calendar, "caltitle", 0));
                            list.Add(Key(calendar, "caltable", 0));
                        }
                        else if (key.Kind == "caltable")
                        {
                            for (int r = 0; r < CalendarRows; r++)
                                list.Add(Key(calendar, "calrow", r));
                        }
                        else if (key.Kind == "calrow")
                        {
                            for (int c = 0; c < 7; c++)
                                list.Add(key.Index == 0
                                    ? Key(calendar, "calname", c)
                                    : Key(calendar, "calday", (key.Index - 1) * 7 + c));
                        }
                        return;
                    }

                    var grid = key.Owner as DataGridView;
                    if (grid == null)
                        return;
                    if (key.Kind == "headerrow")
                    {
                        if (grid.RowHeadersVisible)
                            list.Add(Key(grid, "corner", 0));
                        for (int c = 0; c < grid.Columns.Count; c++)
                            list.Add(Key(grid, "colheader", c));
                    }
                    else if (key.Kind == "gridrow")
                    {
                        if (grid.RowHeadersVisible)
                            list.Add(Key(grid, "rowheader", key.Index));
                        for (int c = 0; c < grid.Columns.Count; c++)
                            list.Add(Key(grid, "cell" + key.Index + "_", c));
                    }
                }
            }
            catch (Exception)
            {
            }
        }

        internal static A11yRole RoleOf(object element)
        {
            var key = element as ItemKey;
            if (key != null)
            {
                switch (key.Kind)
                {
                    case "arrow": case "page": return A11yRole.Button;
                    case "thumb": return A11yRole.Thumb;
                    case "headerrow": case "gridrow": return A11yRole.Custom;
                    case "corner": case "colheader": case "rowheader": return A11yRole.Header;
                    case "calprev": case "calnext": case "caltitle": return A11yRole.Button;
                    case "calpane": case "calrow": return A11yRole.Pane;
                    case "caltable": return A11yRole.Table;
                    case "calname": return A11yRole.Header;
                    case "calday": return A11yRole.DataItem;
                    case "tabitem": return A11yRole.TabItem;
                    case "link": return A11yRole.Link;
                }
                if (key.Owner is CheckedListBox)
                    return A11yRole.CheckBox;
                if (key.Kind.StartsWith("cell", StringComparison.Ordinal))
                    // A grid publishes its cells as data; a list view publishes its columns as
                    // plain text, which is what Windows does with each.
                    return key.Owner is DataGridView ? A11yRole.DataItem : A11yRole.Text;
                return A11yRole.ListItem;
            }
            if (element is ToolStripSeparator)
                return A11yRole.Separator;
            var tsi = element as ToolStripItem;
            if (tsi != null)
                return tsi.Owner is MenuStrip ? A11yRole.MenuItem
                     : tsi.Owner is StatusStrip ? A11yRole.Text
                     : A11yRole.Button;
            if (element is ColumnHeader)
                return A11yRole.Header;
            if (element is ListViewItem)
                return A11yRole.ListItem;
            if (element is TreeNode)
                return A11yRole.TreeItem;
            if (element is TabPage)
                return A11yRole.TabItem;
            return A11yRole.Unknown;
        }

        /// <summary>The empty stretch of a scroll bar's track before or after the thumb -- what a client
        /// clicks to move by a page. Empty when the thumb is against that end.</summary>
        internal static Rectangle PageArea(ScrollBar bar, bool before)
        {
            try
            {
                Rectangle thumb = bar.ThumbPos;
                Rectangle first = bar.FirstArrowArea, second = bar.SecondArrowArea;
                if (bar.vert)
                {
                    int top = before ? first.Bottom : thumb.Bottom;
                    int bottom = before ? thumb.Y : second.Y;
                    return new Rectangle(0, top, bar.Width, Math.Max(0, bottom - top));
                }
                int left = before ? first.Right : thumb.Right;
                int right = before ? thumb.X : second.X;
                return new Rectangle(left, 0, Math.Max(0, right - left), bar.Height);
            }
            catch (Exception)
            {
                return Rectangle.Empty;
            }
        }

        /// <summary>What a scroll bar's parts are called, which differs by orientation exactly as
        /// Windows names them.</summary>
        private static string ScrollPartName(ScrollBar bar, string kind, int index)
        {
            bool vert = bar.vert;
            switch (kind)
            {
                case "arrow":
                    return index == 0 ? (vert ? "Line up" : "Column left")
                                      : (vert ? "Line down" : "Column right");
                case "page": return index == 0 ? (vert ? "Page up" : "Page left")
                                               : (vert ? "Page down" : "Page right");
                default: return "Position";
            }
        }

        /// <summary>What a grid's parts are called. Windows names a cell after its column and
        /// row together with the sort state, which is what a screen reader reads out.</summary>
        private static string GridPartName(DataGridView grid, ItemKey key)
        {
            if (key.Kind == "headerrow") return "Top Row";
            if (key.Kind == "gridrow" || key.Kind == "rowheader") return "Row " + (key.Index + 1);
            if (key.Kind == "corner") return "Top Left Header Cell";
            if (key.Kind == "colheader")
                return key.Index < grid.Columns.Count ? grid.Columns[key.Index].HeaderText : string.Empty;
            if (key.Kind.StartsWith("cell", StringComparison.Ordinal))
            {
                int row;
                string digits = key.Kind.Substring(4).TrimEnd('_');
                if (int.TryParse(digits, out row) && key.Index < grid.Columns.Count)
                    return grid.Columns[key.Index].HeaderText + " Row " + (row + 1) + ", Not sorted.";
            }
            return string.Empty;
        }

        // Seven columns, and six week rows under the row of day names.
        private const int CalendarRows = 7;

        /// <summary>The first day the grid shows, which is usually in the previous month.</summary>
        private static DateTime CalendarFirstDay(MonthCalendar cal)
        {
            DateTime month = cal.current_month;
            return cal.GetFirstDateInMonthGrid(new DateTime(month.Year, month.Month, 1));
        }

        private static string CalendarPartName(MonthCalendar cal, ItemKey key)
        {
            switch (key.Kind)
            {
                case "calprev": return "Previous";
                case "calnext": return "Next";
                case "calpane":
                case "caltitle":
                case "caltable":
                    // Whatever the heading says, which is the month only while the days are
                    // showing -- zoomed out it names the year, the decade or the century.
                    return cal.ZoomTitle;
                case "calname":
                {
                    // Sunday is the 1st of October 2006, which is what the theme counts from.
                    DateTime sunday = new DateTime(2006, 10, 1);
                    int first = (int) cal.GetDayOfWeek(cal.FirstDayOfWeek);
                    return sunday.AddDays(key.Index + first).ToString("ddd");
                }
                case "calday":
                    return CalendarFirstDay(cal).AddDays(key.Index).ToString("D");
            }
            return string.Empty;
        }

        internal static string NameOf(object element)
        {
            try
            {
                var key = element as ItemKey;
                if (key != null)
                {
                    var scroll = key.Owner as ScrollBar;
                    if (scroll != null)
                        return ScrollPartName(scroll, key.Kind, key.Index);

                    var cal = key.Owner as MonthCalendar;
                    if (cal != null)
                        return CalendarPartName(cal, key);

                    var tab_control = key.Owner as TabControl;
                    if (tab_control != null && key.Index < tab_control.TabCount)
                        return tab_control.TabPages[key.Index].Text ?? string.Empty;

                    var link = key.Owner as LinkLabel;
                    if (link != null)
                        return link.Text ?? string.Empty;

                    var data_grid = key.Owner as DataGridView;
                    if (data_grid != null)
                        return GridPartName(data_grid, key);

                    var list_box = key.Owner as ListBox;
                    if (list_box != null && key.Kind == "row" && key.Index < list_box.Items.Count)
                        return list_box.GetItemText(list_box.Items[key.Index]);
                    var view = key.Owner as ListView;
                    if (view != null && key.Kind.StartsWith("cell", StringComparison.Ordinal))
                    {
                        int row;
                        if (int.TryParse(key.Kind.Substring(4), out row) && row < view.Items.Count)
                        {
                            ListViewItem owner_item = view.Items[row];
                            if (key.Index < owner_item.SubItems.Count)
                                return owner_item.SubItems[key.Index].Text;
                        }
                    }
                    return string.Empty;
                }

                var tsi = element as ToolStripItem;
                if (tsi != null)
                    return tsi.Text ?? string.Empty;
                var col = element as ColumnHeader;
                if (col != null)
                    return col.Text ?? string.Empty;
                var item = element as ListViewItem;
                if (item != null)
                    return item.Text ?? string.Empty;
                var node = element as TreeNode;
                if (node != null)
                    return node.Text ?? string.Empty;
                var page = element as TabPage;
                if (page != null)
                    return page.Text ?? string.Empty;
            }
            catch (Exception)
            {
            }
            return string.Empty;
        }

        /// <summary>An item's rectangle in the driver's screen space, the same space
        /// <see cref="A11y.DriverBounds"/> answers in.</summary>
        internal static Rectangle BoundsOf(object element)
        {
            try
            {
                Control owner = OwnerOf(element);
                if (owner == null || owner.IsDisposed)
                    return Rectangle.Empty;
                Point origin = owner.PointToScreen(Point.Empty);

                var key = element as ItemKey;
                if (key != null)
                {
                    var cal = key.Owner as MonthCalendar;
                    if (cal != null)
                        return Offset(CalendarPartBounds(cal, key), origin);

                    var tab_control = key.Owner as TabControl;
                    if (tab_control != null && key.Index < tab_control.TabCount)
                        return Offset(tab_control.GetTabRect(key.Index), origin);

                    var link = key.Owner as LinkLabel;
                    if (link != null)
                        return Offset(link.ClientRectangle, origin);

                    var scroll = key.Owner as ScrollBar;
                    if (scroll != null)
                    {
                        Rectangle part = key.Kind == "thumb" ? scroll.ThumbPos
                            : key.Kind == "page" ? PageArea(scroll, key.Index == 0)
                            : key.Index == 0 ? scroll.FirstArrowArea : scroll.SecondArrowArea;
                        return Offset(part, origin);
                    }

                    var data_grid = key.Owner as DataGridView;
                    if (data_grid != null)
                        return Offset(GridPartBounds(data_grid, key), origin);

                    var list_box = key.Owner as ListBox;
                    if (list_box != null && key.Kind == "row")
                        return Offset(list_box.GetItemRectangle(key.Index), origin);
                    var view = key.Owner as ListView;
                    if (view != null && key.Kind.StartsWith("cell", StringComparison.Ordinal))
                    {
                        int row;
                        if (int.TryParse(key.Kind.Substring(4), out row) && row < view.Items.Count)
                            return Offset(view.Items[row].SubItems[key.Index].Bounds, origin);
                    }
                    return Rectangle.Empty;
                }

                var tsi = element as ToolStripItem;
                if (tsi != null)
                    return Offset(tsi.Bounds, origin);
                var col = element as ColumnHeader;
                if (col != null)
                {
                    var view = col.ListView;
                    int x = 0;
                    foreach (ColumnHeader c in view.Columns)
                    {
                        if (ReferenceEquals(c, col))
                            return Offset(new Rectangle(x, 0, c.Width, view.Font.Height + 10), origin);
                        x += c.Width;
                    }
                    return Rectangle.Empty;
                }
                var item = element as ListViewItem;
                if (item != null)
                    return Offset(item.Bounds, origin);
                var node = element as TreeNode;
                if (node != null)
                    return Offset(node.Bounds, origin);
                var page = element as TabPage;
                if (page != null)
                    return A11y.DriverBounds(page);
            }
            catch (Exception)
            {
            }
            return Rectangle.Empty;
        }

        /// <summary>Where a calendar's part sits, in the calendar's own client coordinates. The
        /// grid starts one margin in and one title down, and every cell is date_cell_size --
        /// the same numbers the theme lays the calendar out with.</summary>
        private static Rectangle CalendarPartBounds(MonthCalendar cal, ItemKey key)
        {
            try
            {
                Size cell = cal.date_cell_size;
                Size title = cal.title_size;
                int margin = ThemeEngine.Current.MonthCalendarMargin(cal);
                if (cell.Width <= 0 || cell.Height <= 0)
                    return Rectangle.Empty;
                int gridX = margin, gridY = margin + title.Height;
                int width = 7 * cell.Width;

                switch (key.Kind)
                {
                    case "calpane":
                        return new Rectangle(margin, margin, width, title.Height + CalendarRows * cell.Height);
                    case "caltitle":
                        return new Rectangle(margin, margin, width, title.Height);
                    case "caltable":
                        return new Rectangle(gridX, gridY, width, CalendarRows * cell.Height);
                    case "calrow":
                        return new Rectangle(gridX, gridY + key.Index * cell.Height, width, cell.Height);
                    case "calname":
                        return new Rectangle(gridX + key.Index * cell.Width, gridY, cell.Width, cell.Height);
                    case "calday":
                        return new Rectangle(gridX + (key.Index % 7) * cell.Width,
                            gridY + (key.Index / 7 + 1) * cell.Height, cell.Width, cell.Height);
                    case "calprev":
                    case "calnext":
                    {
                        Size button = new Size(cell.Width, title.Height - 6);
                        int y = margin + 3;
                        int x = key.Kind == "calprev" ? margin + 3 : margin + width - button.Width - 3;
                        return new Rectangle(x, y, button.Width, button.Height);
                    }
                }
            }
            catch (Exception)
            {
            }
            return Rectangle.Empty;
        }

        /// <summary>Where a grid's part sits, in the grid's own client coordinates.</summary>
        private static Rectangle GridPartBounds(DataGridView grid, ItemKey key)
        {
            try
            {
                if (key.Kind == "headerrow")
                    return new Rectangle(0, 0, grid.ClientSize.Width, grid.ColumnHeadersHeight);
                if (key.Kind == "gridrow")
                    return grid.GetRowDisplayRectangle(key.Index, false);
                if (key.Kind == "corner")
                    return new Rectangle(0, 0, grid.RowHeadersWidth, grid.ColumnHeadersHeight);
                if (key.Kind == "colheader")
                    return grid.GetCellDisplayRectangle(key.Index, -1, false);
                if (key.Kind == "rowheader")
                    return grid.GetCellDisplayRectangle(-1, key.Index, false);
                if (key.Kind.StartsWith("cell", StringComparison.Ordinal))
                {
                    int row;
                    string digits = key.Kind.Substring(4).TrimEnd('_');
                    if (int.TryParse(digits, out row))
                        return grid.GetCellDisplayRectangle(key.Index, row, false);
                }
            }
            catch (Exception)
            {
            }
            return Rectangle.Empty;
        }

        private static Rectangle Offset(Rectangle r, Point origin)
        {
            return new Rectangle(r.X + origin.X, r.Y + origin.Y, r.Width, r.Height);
        }

        /// <summary>Whether pressing the item does something -- a menu entry, a tool bar button.</summary>
        internal static bool CanInvoke(object element)
        {
            if (element is ToolStripItem)
                return !(element is ToolStripSeparator);
            // A calendar's arrows and its heading are buttons in the tree Windows publishes, so
            // they answer to being invoked -- the only way anything but a pointer can page a
            // month or zoom out to the year.
            var key = element as ItemKey;
            return key != null && key.Owner is MonthCalendar &&
                (key.Kind == "calprev" || key.Kind == "calnext" || key.Kind == "caltitle" ||
                 key.Kind == "calday");
        }

        internal static void Invoke(object element)
        {
            var tsi = element as ToolStripItem;
            if (tsi != null)
            {
                tsi.PerformClick();
                return;
            }
            var key = element as ItemKey;
            var calendar = key == null ? null : key.Owner as MonthCalendar;
            if (calendar == null)
                return;
            switch (key.Kind)
            {
                case "calprev": calendar.InvokeStep(-1); break;
                case "calnext": calendar.InvokeStep(1); break;
                case "caltitle": calendar.ZoomOut(); break;
                case "calday": calendar.InvokeCell(key.Index); break;
            }
        }

        internal static bool IsEnabled(object element)
        {
            try
            {
                var tsi = element as ToolStripItem;
                if (tsi != null)
                    return tsi.Enabled;
                Control owner = OwnerOf(element);
                return owner == null || owner.Enabled;
            }
            catch (Exception)
            {
                return true;
            }
        }
    }
}
