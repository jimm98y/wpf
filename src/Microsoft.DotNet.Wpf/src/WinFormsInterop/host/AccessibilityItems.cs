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
                    foreach (TabPage page in tabs.TabPages)
                        list.Add(page);
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
                return key.Owner is CheckedListBox ? A11yRole.CheckBox
                     : key.Kind.StartsWith("cell", StringComparison.Ordinal) ? A11yRole.DataItem
                     : A11yRole.ListItem;
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

        internal static string NameOf(object element)
        {
            try
            {
                var key = element as ItemKey;
                if (key != null)
                {
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

        private static Rectangle Offset(Rectangle r, Point origin)
        {
            return new Rectangle(r.X + origin.X, r.Y + origin.Y, r.Width, r.Height);
        }

        /// <summary>Whether pressing the item does something -- a menu entry, a tool bar button.</summary>
        internal static bool CanInvoke(object element)
        {
            return element is ToolStripItem && !(element is ToolStripSeparator);
        }

        internal static void Invoke(object element)
        {
            var tsi = element as ToolStripItem;
            if (tsi != null)
                tsi.PerformClick();
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
