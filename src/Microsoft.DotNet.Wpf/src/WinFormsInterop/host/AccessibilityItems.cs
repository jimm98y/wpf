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
using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;
using System.Windows.Forms.PropertyGridInternal;

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

        /// <summary>The form an open menu dropped out of: the control it was raised over, or the
        /// item it hangs under, whichever it has.</summary>
        private static Form FormBehind(ToolStripDropDown drop)
        {
            try
            {
                var context = drop as ContextMenuStrip;
                if (context != null && context.SourceControl != null)
                    return context.SourceControl.FindForm();
                for (ToolStripItem item = drop.OwnerItem; item != null; item = item.OwnerItem)
                    if (item.Owner != null)
                        return item.Owner.FindForm();
            }
            catch (Exception)
            {
            }
            return null;
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
            var grid_item = element as GridEntry;
            if (grid_item != null)
                return grid_item.GridView;
            return null;
        }

        /// <summary>The element this one hangs under, which is not always the control that owns
        /// it: a grid's cell belongs to its row, and the row to the grid. Answering the control
        /// for everything left every row with exactly one visible child, because a client walks
        /// from one child to the next through the parent's list and a cell was not in it.</summary>
        internal static object ParentOf(object element)
        {
            // A node belongs to the node it is under, and only the top ones to the tree itself;
            // a property to the category it is under. Answering the control for all of them left
            // every item after the first child of a branch unreachable: a client finds a sibling
            // by looking for itself in its parent's children, and a nested item is not among the
            // control's own.
            var tree_node = element as TreeNode;
            if (tree_node != null && tree_node.Parent != null)
                return tree_node.Parent;

            var grid_item = element as GridItem;
            if (grid_item != null && grid_item.Parent != null
                && grid_item.Parent.GridItemType != GridItemType.Root)
                return grid_item.Parent;

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
                // An open menu is a window of its own rather than a child of anything, so a walk
                // of a form's controls never reaches it -- and a menu nothing can find is a menu
                // nothing can read out or drive. Windows publishes it as a window a client can
                // still get to; hanging it under the form it dropped out of is the nearest thing
                // to that here, where a menu has no window of its own to be found by.
                var form = control as Form;
                if (form != null)
                {
                    foreach (ToolStripDropDown open in ToolStripManager.OpenDropDowns())
                        if (ReferenceEquals(FormBehind(open), form))
                            list.Add(open);
                }

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
                    // Windows publishes the header as one anonymous pane above the rows, not as a
                    // run of header elements: the columns are reachable through the cells, which
                    // are named after them.
                    if (view.View == View.Details && view.header_control != null
                        && view.header_control.Visible)
                        list.Add(Key(view, "lvheader", 0));
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
                    // separate element, which is what Windows publishes as well. Only the page on
                    // top is published: the others are not on the screen, and listing them gave
                    // every tab control a pane per page that nothing could ever be read from.
                    if (tabs.SelectedTab != null)
                        list.Add(tabs.SelectedTab);
                    for (int i = 0; i < tabs.TabCount; i++)
                        list.Add(Key(tabs, "tabitem", i));
                    return list;
                }

                // A property grid's rows: the categories, each with its properties under it.
                var properties = control as PropertyGridView;
                if (properties != null)
                {
                    GridItem root = properties.RootItem;
                    if (root != null)
                        foreach (GridItem item in root.GridItems)
                            list.Add(item);
                    return list;
                }

                // A combo box reads as the field the value shows in and the button that opens
                // the list, which is what Windows publishes for one whether or not it is editable.
                var combo = control as ComboBox;
                if (combo != null)
                {
                    list.Add(Key(combo, "cbfield", 0));
                    if (combo.DropDownStyle != ComboBoxStyle.Simple)
                        list.Add(Key(combo, "cbopen", 0));
                    return list;
                }

                // The two halves of a spin button.
                var spinner = control as UpDownBase.UpDownSpinner;
                if (spinner != null)
                {
                    list.Add(Key(spinner, "spin", 0));
                    list.Add(Key(spinner, "spin", 1));
                    return list;
                }

                // A slider reads as the thumb and the track either side of it.
                var track = control as TrackBar;
                if (track != null)
                {
                    list.Add(Key(track, "trackpage", 0));
                    list.Add(Key(track, "trackthumb", 0));
                    list.Add(Key(track, "trackpage", 1));
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
                    // A page area only exists while there is track on that side of the thumb. Asking for either
                        // dimension rather than both let a bar with its thumb hard against one end publish a page
                        // button of no height at all, which Windows does not.
                    list.Add(Key(bar, "arrow", 0));
                    if (!PageArea(bar, true).IsEmpty)
                        list.Add(Key(bar, "page", 0));
                    list.Add(Key(bar, "thumb", 0));
                    if (!PageArea(bar, false).IsEmpty)
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
                    if (calendar.ShowToday)
                        list.Add(Key(calendar, "caltoday", 0));
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

                // A property row that is open reads as the rows under it. A closed one reads as
                // nothing, the same as a closed tree node: what is not on the screen is not there.
                var grid_item = element as GridItem;
                if (grid_item != null)
                {
                    if (grid_item.Expandable && !grid_item.Expanded)
                        return;
                    foreach (GridItem child in grid_item.GridItems)
                        list.Add(child);
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
                    case "calprev": case "calnext": case "caltitle": case "caltoday":
                        return A11yRole.Button;
                    case "lvheader": return A11yRole.Pane;
                    case "cbfield": return A11yRole.Text;
                    case "cbopen": case "spin": case "trackpage": return A11yRole.Button;
                    case "trackthumb": return A11yRole.Thumb;
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
            {
                // What it was told it is, if it was told: two buttons that are one choice between
                // them are a pair of radio buttons however they are drawn, and only the control
                // that made them knows that.
                A11yRole given = FromAccessibleRole(tsi.AccessibleRole);
                if (given != A11yRole.Unknown)
                    return given;
                // An entry in a menu bar or in any menu that drops out of one is a menu item; a
                // label in the status bar is text; anything else on a strip is a button.
                return tsi.Owner is MenuStrip || tsi.Owner is ToolStripDropDown ? A11yRole.MenuItem
                     : tsi.Owner is StatusStrip ? A11yRole.Text
                     : A11yRole.Button;
            }
            if (element is ColumnHeader)
                return A11yRole.Header;
            if (element is ListViewItem)
                return A11yRole.ListItem;
            if (element is TreeNode || element is GridItem)
                return A11yRole.TreeItem;
            if (element is TabPage)
                return A11yRole.TabItem;
            return A11yRole.Unknown;
        }

        /// <summary>What a control was explicitly told to call itself, in this layer's terms.
        /// Unknown when it was told nothing, which is the usual case.</summary>
        private static A11yRole FromAccessibleRole(AccessibleRole role)
        {
            switch (role)
            {
                case AccessibleRole.PushButton: return A11yRole.Button;
                case AccessibleRole.RadioButton: return A11yRole.RadioButton;
                case AccessibleRole.CheckButton: return A11yRole.CheckBox;
                case AccessibleRole.MenuItem: return A11yRole.MenuItem;
                case AccessibleRole.Separator: return A11yRole.Separator;
                case AccessibleRole.StaticText: return A11yRole.Text;
                default: return A11yRole.Unknown;
            }
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
                    // Nothing at all rather than a strip of no height: the caller asks whether there is a page
                    // area on this side, and a rectangle with a width still reads as one.
                    return bottom <= top ? Rectangle.Empty
                        : new Rectangle(0, top, bar.Width, bottom - top);
                }
                int left = before ? first.Right : thumb.Right;
                int right = before ? thumb.X : second.X;
                return right <= left ? Rectangle.Empty
                    : new Rectangle(left, 0, right - left, bar.Height);
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
                case "caltoday":
                    return "Today: " + cal.TodayDate.ToShortDateString();
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

                    // The field carries the combo box's own name -- it is where the value shows --
                    // and the rest are called what they do.
                    if (key.Kind == "cbfield")
                        return A11y.NameOf(key.Owner);
                    if (key.Kind == "cbopen")
                        return "Open";
                    if (key.Kind == "spin")
                        return key.Index == 0 ? "Up" : "Down";
                    if (key.Kind == "trackpage")
                        return key.Index == 0 ? "Large decrease" : "Large increase";
                    if (key.Kind == "trackthumb")
                        return "Position";

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
                {
                    // What it was given to be called, then its caption, then the tip that appears
                    // when the pointer rests on it. A toolbar button usually carries only an icon,
                    // and the tip is the only thing that says what the icon means -- which is what
                    // Windows reads out for one.
                    if (!string.IsNullOrEmpty(tsi.AccessibleName))
                        return tsi.AccessibleName;
                    if (!string.IsNullOrEmpty(tsi.Text))
                        return WithoutMnemonic(tsi.Text);
                    return tsi.ToolTipText ?? string.Empty;
                }
                var col = element as ColumnHeader;
                if (col != null)
                    return col.Text ?? string.Empty;
                var item = element as ListViewItem;
                if (item != null)
                    return item.Text ?? string.Empty;
                var node = element as TreeNode;
                if (node != null)
                    return node.Text ?? string.Empty;
                var grid_item = element as GridItem;
                if (grid_item != null)
                    return grid_item.Label ?? string.Empty;
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
                // From the client, not the window: a bordered control's items begin a border in.
                Point origin = A11y.ClientOrigin(owner);

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
                        return Offset(LinkBounds(link, key.Index), origin);

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

                    if (key.Kind == "cbfield")
                        return Offset(new Rectangle(Point.Empty, key.Owner.ClientSize), origin);
                    if (key.Kind == "cbopen")
                        return Offset(ComboOpenBounds((ComboBox) key.Owner), origin);
                    if (key.Kind == "spin")
                    {
                        Rectangle half = new Rectangle(Point.Empty, key.Owner.ClientSize);
                        half.Height /= 2;
                        if (key.Index == 1)
                            half.Y += half.Height;
                        return Offset(half, origin);
                    }
                    if (key.Kind.StartsWith("track", StringComparison.Ordinal))
                        return Offset(TrackPartBounds((TrackBar) key.Owner, key), origin);

                    var list_box = key.Owner as ListBox;
                    if (list_box != null && key.Kind == "row")
                        return Offset(ListRowBounds(list_box, key.Index), origin);
                    if (key.Kind == "lvheader")
                    {
                        var header_view = (ListView) key.Owner;
                        int strip = header_view.ClientRectangle.Width - header_view.BorderInset * 2;
                        if (header_view.v_scroll != null && header_view.v_scroll.Visible)
                            strip -= header_view.v_scroll.Width;
                        return Offset(new Rectangle(0, 0, Math.Max(0, strip),
                            header_view.header_control.Height), origin);
                    }
                    var view = key.Owner as ListView;
                    if (view != null && key.Kind.StartsWith("cell", StringComparison.Ordinal))
                    {
                        int row;
                        if (int.TryParse(key.Kind.Substring(4), out row) && row < view.Items.Count)
                            return Offset(CellBounds(view, view.Items[row], key.Index), origin);
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
                var grid_row = element as GridEntry;
                if (grid_row != null && grid_row.GridView != null)
                {
                    // The row's own strip, inside the frame the grid draws around itself.
                    PropertyGridView view = grid_row.GridView;
                    return Offset(new Rectangle(0, grid_row.Top, view.RowWidth, view.RowHeight),
                        origin);
                }
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
                    case "caltoday":
                    {
                        // The strip under the grid, with the date written across the middle of it.
                        int top = margin + title.Height + CalendarRows * cell.Height;
                        return new Rectangle(gridX, top, width, cell.Height);
                    }
                    case "calprev":
                    case "calnext":
                    {
                        // Windows reports the arrow itself -- a fixed 16x16 glyph centred in the
                        // title, flush with the ends of the grid -- not the whole hit area around
                        // it, which is what our theme paints and what this used to report.
                        const int Glyph = 16;
                        int y = margin + (title.Height - Glyph) / 2;
                        int x = key.Kind == "calprev" ? margin : margin + width - Glyph;
                        return new Rectangle(x, y, Glyph, Glyph);
                    }
                }
            }
            catch (Exception)
            {
            }
            return Rectangle.Empty;
        }

        /// <summary>A caption without the ampersand that marks the key which reaches it. The
        /// name a client reads is what the menu says on screen: "File", not "&amp;File". A doubled
        /// ampersand is one real one.</summary>
        private static string WithoutMnemonic(string text)
        {
            if (string.IsNullOrEmpty(text) || text.IndexOf('&') < 0)
                return text ?? string.Empty;

            var built = new System.Text.StringBuilder(text.Length);
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] != '&')
                    built.Append(text[i]);
                else if (i + 1 < text.Length && text[i + 1] == '&')
                    built.Append(text[i++]);
            }
            return built.ToString();
        }

        /// <summary>Where a list box's row sits, in the row area's own coordinates -- which is
        /// what GetItemRectangle already answers in, and where the caller's origin starts.
        /// <para>Windows publishes only the rows a list can actually show: one scrolled past the
        /// end, or hanging out of the bottom, reports nothing at all rather than a sliver.</para>
        /// </summary>
        private static Rectangle ListRowBounds(ListBox list, int index)
        {
            Rectangle row = list.GetItemRectangle(index);
            Rectangle first = list.GetItemRectangle(list.TopIndex);
            row.X -= first.X;
            row.Y -= first.Y;

            row.Intersect(new Rectangle(Point.Empty, list.items_area.Size));
            return row.Width <= 0 || row.Height <= 0 ? Rectangle.Empty : row;
        }

        /// <summary>The button that drops a combo box's list down, in the box's own coordinates.</summary>
        private static Rectangle ComboOpenBounds(ComboBox combo)
        {
            Rectangle button = combo.ButtonArea;
            if (!button.IsEmpty)
                return button;
            int width = SystemInformation.VerticalScrollBarWidth;
            return new Rectangle(combo.ClientSize.Width - width, 0, width, combo.ClientSize.Height);
        }

        /// <summary>Where a slider's thumb, and the track either side of it, sit.</summary>
        private static Rectangle TrackPartBounds(TrackBar track, ItemKey key)
        {
            Rectangle thumb = track.ThumbPos, area = track.ThumbArea;
            if (key.Kind == "trackthumb")
                return thumb;

            // The channel is the four pixels the theme paints, not the whole strip the thumb
            // travels in: a client aiming at "large decrease" aims at the groove.
            const int Channel = 4;
            if (track.Orientation == Orientation.Horizontal)
            {
                int left = key.Index == 0 ? area.X : thumb.Right;
                int right = key.Index == 0 ? thumb.X : area.Right;
                return right <= left ? Rectangle.Empty
                    : new Rectangle(left, area.Y, right - left, Channel);
            }
            int top = key.Index == 0 ? area.Y : thumb.Bottom;
            int bottom = key.Index == 0 ? thumb.Y : area.Bottom;
            return bottom <= top ? Rectangle.Empty
                : new Rectangle(area.X, top, Channel, bottom - top);
        }

        /// <summary>Where a link's own text sits, in the label's client coordinates.
        /// <para>A LinkLabel is ordinary text with a link somewhere inside it, and Windows reports
        /// the link's run of characters rather than the whole label -- a reader should land on the
        /// words that can actually be clicked, not on the sentence around them. The runs are
        /// already measured for painting; this is their union.</para></summary>
        private static Rectangle LinkBounds(LinkLabel label, int index)
        {
            if (index < 0 || index >= label.Links.Count)
                return label.ClientRectangle;

            Rectangle bounds = Rectangle.Empty;
            using (Graphics g = label.CreateGraphics())
            {
                foreach (LinkLabel.Piece piece in label.Links[index].pieces)
                {
                    if (piece.region == null)
                        continue;
                    Rectangle run = Rectangle.Ceiling(piece.region.GetBounds(g));
                    bounds = bounds.IsEmpty ? run : Rectangle.Union(bounds, run);
                }
            }
            return bounds.IsEmpty ? label.ClientRectangle : bounds;
        }

        /// <summary>Where a details-view cell sits, the way Windows reports one.
        /// <para>The first column's cell is the whole row: the row IS the item, and a reader
        /// announces the item's label for it. The last column's runs on to the row's right edge
        /// instead of stopping at its header's width, so that no part of the row belongs to
        /// nothing. Only the columns in between are exactly as wide as their header. Reporting
        /// every cell at its own column width -- which is what the sub-item's own Bounds gives --
        /// left the row's right-hand remainder unreachable and named the first column something
        /// narrower than the row a reader had just moved to.</para></summary>
        private static Rectangle CellBounds(ListView view, ListViewItem item, int column)
        {
            Rectangle row = item.Bounds;
            if (view.item_control != null)
                row.Width = Math.Max(row.Width, view.item_control.Width - row.X);
            if (column <= 0 || view.Columns.Count == 0)
                return row;

            int x = row.X;
            for (int i = 0; i < column && i < view.Columns.Count; i++)
                x += view.Columns[i].Width;
            int right = column >= view.Columns.Count - 1
                ? row.Right
                : Math.Min(row.Right, x + view.Columns[column].Width);
            return right <= x ? Rectangle.Empty : new Rectangle(x, row.Y, right - x, row.Height);
        }

        /// <summary>The part of a grid a client can actually see: inside the frame, and short of
        /// whichever scroll bars are up. A row past the bottom of it, or a column past the right,
        /// is not on the screen at all, and Windows answers nothing for one rather than pointing
        /// at where it would be if the grid were bigger.</summary>
        private static Rectangle GridViewport(DataGridView grid)
        {
            Rectangle view = grid.ClientRectangle;
            int frame = grid.BorderStyle == BorderStyle.None ? 0
                      : grid.BorderStyle == BorderStyle.FixedSingle ? 1 : 2;
            view.Inflate(-frame, -frame);
            if (grid.verticalScrollBar != null && grid.verticalScrollBar.Visible)
                view.Width -= grid.verticalScrollBar.Width;
            if (grid.horizontalScrollBar != null && grid.horizontalScrollBar.Visible)
                view.Height -= grid.horizontalScrollBar.Height;
            return view;
        }

        /// <summary>Where a grid's part sits, in the grid's own client coordinates.</summary>
        private static Rectangle GridPartBounds(DataGridView grid, ItemKey key)
        {
            try
            {
                Rectangle view = GridViewport(grid);
                Rectangle part = Rectangle.Empty;
                if (key.Kind == "headerrow")
                    part = new Rectangle(view.X, view.Y, view.Width, grid.ColumnHeadersHeight);
                else if (key.Kind == "gridrow")
                {
                    part = grid.GetRowDisplayRectangle(key.Index, false);
                    // A row is as wide as the grid can show, whatever the columns add up to.
                    part.X = view.X;
                    part.Width = view.Width;
                }
                else if (key.Kind == "corner")
                    part = new Rectangle(view.X, view.Y, grid.RowHeadersWidth, grid.ColumnHeadersHeight);
                else if (key.Kind == "colheader")
                    part = grid.GetCellDisplayRectangle(key.Index, -1, false);
                else if (key.Kind == "rowheader")
                    part = grid.GetCellDisplayRectangle(-1, key.Index, false);
                else if (key.Kind.StartsWith("cell", StringComparison.Ordinal))
                {
                    int row;
                    string digits = key.Kind.Substring(4).TrimEnd('_');
                    if (int.TryParse(digits, out row))
                        part = grid.GetCellDisplayRectangle(key.Index, row, false);
                }

                part.Intersect(view);
                return part.Width <= 0 || part.Height <= 0 ? Rectangle.Empty : part;
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
            if (element is GridItem)
                return true;
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
            // A property grid's row: invoking it is how Windows moves the grid's selection onto it,
            // and the only way anything but a pointer can pick a property.
            var grid_item = element as GridItem;
            if (grid_item != null)
            {
                try { grid_item.Select(); } catch (Exception) { }
                return;
            }
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

        /// <summary>Whether an item opens something -- a menu entry with a submenu under it, a
        /// split button's arrow. Windows publishes those as expandable, and it is the only way
        /// anything but a pointer can open one: a menu with a submenu does not respond to being
        /// invoked, it responds to being opened.</summary>
        internal static bool CanExpand(object element)
        {
            var grid_item = element as GridItem;
            if (grid_item != null)
            {
                try { return grid_item.Expandable; } catch (Exception) { return false; }
            }
            var item = element as ToolStripDropDownItem;
            return item != null && item.HasDropDownItems;
        }

        internal static bool IsExpanded(object element)
        {
            var grid_item = element as GridItem;
            if (grid_item != null)
            {
                try { return grid_item.Expanded; } catch (Exception) { return false; }
            }
            var item = element as ToolStripDropDownItem;
            return item != null && item.DropDown != null && item.DropDown.Visible;
        }

        internal static void SetExpanded(object element, bool expanded)
        {
            var grid_item = element as GridItem;
            if (grid_item != null)
            {
                try { if (grid_item.Expandable) grid_item.Expanded = expanded; } catch (Exception) { }
                return;
            }
            var item = element as ToolStripDropDownItem;
            if (item == null || !item.HasDropDownItems)
                return;
            if (expanded)
                item.ShowDropDown();
            else
                item.HideDropDown();
        }

        /// <summary>The stand-in for one of a list box's rows, so a caller outside can name the very
        /// object the tree publishes rather than an equal one.</summary>
        internal static object RowKey(ListBox list, int index)
        {
            return Key(list, "row", index);
        }

        /// <summary>Whether an item is one of a set its control picks from -- a row in a list, a row
        /// in a details view, a node in a tree. Windows publishes those as selectable, and it is the
        /// only way anything but a pointer can put the selection on a particular row.</summary>
        internal static bool CanSelect(object element)
        {
            if (element is ListViewItem || element is TreeNode)
                return true;
            var key = element as ItemKey;
            return key != null && key.Kind == "row" && key.Owner is ListBox;
        }

        internal static bool IsSelected(object element)
        {
            try
            {
                var item = element as ListViewItem;
                if (item != null)
                    return item.Selected;
                var node = element as TreeNode;
                if (node != null)
                    return node.TreeView != null && ReferenceEquals(node.TreeView.SelectedNode, node);
                var key = element as ItemKey;
                var list = key == null ? null : key.Owner as ListBox;
                return list != null && key.Kind == "row" && list.SelectedIndices.Contains(key.Index);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>Put the selection on this item, or take it off.
        /// <para>Selecting ADDS to whatever is already picked rather than replacing it, because that
        /// is what Windows does: its list view answers a Select by adding the row to the selected
        /// indices, its list box by assigning the selected index, its tree by assigning the selected
        /// node -- so on a control that picks one thing a select replaces, and on one that picks
        /// several it adds. Clearing first read as the tidier reading of Select, and made the same
        /// script leave the two applications in different states.</para></summary>
        internal static void SetSelected(object element, bool selected)
        {
            try
            {
                var item = element as ListViewItem;
                if (item != null && item.ListView != null)
                {
                    item.Selected = selected;
                    if (selected)
                        item.Focused = true;
                    return;
                }
                var node = element as TreeNode;
                if (node != null && node.TreeView != null)
                {
                    node.TreeView.SelectedNode = selected ? node : null;
                    return;
                }
                var key = element as ItemKey;
                var list = key == null ? null : key.Owner as ListBox;
                if (list == null || key.Kind != "row")
                    return;
                if (list.SelectionMode == SelectionMode.One)
                    list.SelectedIndex = selected ? key.Index : -1;
                else
                    list.SetSelected(key.Index, selected);
            }
            catch (Exception)
            {
            }
        }

        /// <summary>A property grid's row carries a value, and Windows publishes it: the row answers
        /// with what the property is set to and can be asked to set it. Nothing else among these items
        /// has a value of its own -- a menu entry or a list row is its own text.</summary>
        internal static bool HasValue(object element)
        {
            var grid_item = element as GridItem;
            return grid_item != null && grid_item.PropertyDescriptor != null;
        }

        internal static string ValueOf(object element)
        {
            var grid_item = element as GridItem;
            if (grid_item == null)
                return null;
            try
            {
                object value = grid_item.Value;
                PropertyDescriptor property = grid_item.PropertyDescriptor;
                if (property != null && property.Converter != null
                    && property.Converter.CanConvertTo(typeof(string)))
                    return property.Converter.ConvertToString(value);
                return value == null ? string.Empty : value.ToString();
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        internal static bool IsReadOnly(object element)
        {
            var grid_item = element as GridItem;
            if (grid_item == null || grid_item.PropertyDescriptor == null)
                return true;
            try { return grid_item.PropertyDescriptor.IsReadOnly; } catch (Exception) { return true; }
        }

        internal static void SetValue(object element, string value)
        {
            var grid_item = element as GridItem;
            if (grid_item == null || grid_item.PropertyDescriptor == null)
                return;
            try
            {
                PropertyDescriptor property = grid_item.PropertyDescriptor;
                if (property.IsReadOnly || property.Converter == null
                    || !property.Converter.CanConvertFrom(typeof(string)))
                    return;
                grid_item.Select();
                object parsed = property.Converter.ConvertFromString(value);
                Control owner = OwnerOf(element);
                var grid = owner as PropertyGrid;
                object target = grid != null ? grid.SelectedObject : null;
                if (target != null)
                    property.SetValue(target, parsed);
            }
            catch (Exception)
            {
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
