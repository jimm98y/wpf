// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The WebAssembly accessibility backend: WPF's automation tree, mirrored into the DOM as ARIA.
//
// The browser is unlike the other five heads in two ways that decide the whole design.
//
// First, the browser IS the assistive-technology surface. There is no NSAccessibility to answer or
// AccessibilityNodeProvider to implement -- a screen reader reads the DOM. So instead of answering
// questions, this backend MIRRORS: one absolutely-positioned, transparent element per accessible
// node, carrying role/aria-* attributes and sitting exactly over the pixels the compositor drew.
// Screen readers, keyboard tab order, browser zoom and automated auditing tools all then work on
// the mirror without knowing a canvas is involved.
//
// Second, the web has NO way to detect an assistive technology, deliberately -- exposing that would
// be a fingerprinting vector. Every other backend waits to be asked; this one cannot, so the mirror
// is built unconditionally. That is affordable (a few hundred transparent divs) and it has a real
// side benefit: the accessibility tree is always inspectable in devtools and auditable by axe. An
// app that does not want it can opt out with <meta name="wpf-a11y" content="off">.
//
// The mirror is pushed rather than pulled, so it is kept deliberately coarse: one JSON payload per
// sync, coalesced to at most one per frame, rather than a JS interop crossing per property.
//
// What each node becomes is what ARIA and HTML prescribe for it, because that is all a screen
// reader understands: a text box is a real <input>/<textarea> holding the text and the selection,
// so the screen reader's own reading commands work on it; a list, grid or tree owns its items
// (aria-owns, since the mirror is flat), with aria-selected, aria-rowindex and the rest; a
// scrolling pane is a real overflow container scrolled to where WPF's is; a modal window is a
// dialog with aria-modal, and the windows under it are inert. What the user or their screen reader
// does to those -- select text, scroll the pane, press Escape in the dialog -- comes back as a
// queued action, like activation always has.
//

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using System.Text;

namespace MS.Internal.Interop
{
    [SupportedOSPlatform("browser")]
    internal static partial class BrowserAccessibility
    {
        internal static bool IsAvailable => OperatingSystem.IsBrowser();

        /// <summary>True once the mirror is being maintained.</summary>
        internal static bool IsAttached { get; private set; }

        // Set when something changed and the mirror has not caught up. Drained on the pump tick, so
        // a burst of property changes during one layout costs one rebuild, not one per change.
        private static bool s_dirty;

        // The windows mirrored, in the order they were made: the page's own first, then its dialogs.
        // A modal dialog is a window of its own, and mirroring only the first would leave it -- the
        // one thing the user can act on -- unreadable.
        private static readonly List<IntPtr> s_windows = new();

        private static IAutomationTreeSource Tree => AutomationTree.Current;

        // The DispatchAction kinds browser-window.js queues; kept in step with the k values it pushes.
        internal const int ActionActivate = 0, ActionFocus = 1, ActionTextSelection = 2, ActionScroll = 3,
            ActionCloseModal = 4, ActionSetValue = 5;

        /// <summary>
        /// Starts mirroring a window. Called as each framed browser window is created; the host page
        /// can decline with &lt;meta name="wpf-a11y" content="off"&gt;.
        /// </summary>
        internal static void Attach(IntPtr window)
        {
            if (!IsAvailable) return;
            if (!IsAttached)
            {
                if (!BrowserWindow.Js.A11yIsEnabled()) return;

                IsAttached = true;
                AutomationTree.RequestActivation();
                AutomationTree.Changed += OnTreeChanged;
            }

            if (!s_windows.Contains(window)) s_windows.Add(window);
            s_dirty = true;
        }

        private static void OnTreeChanged(AutomationChange change)
        {
            // Focus is the one change worth pushing immediately: a screen reader that hears about it
            // late reads the previous control. Everything else rides the next frame -- the text,
            // caret, scroll and selection changes included, which the rebuild carries.
            if (change.Kind == AutomationChangeKind.FocusChanged)
            {
                BrowserWindow.Js.A11ySetFocus(change.NodeId);
            }

            s_dirty = true;
        }

        /// <summary>
        /// Rebuilds the mirror if anything changed. Called once per dispatcher tick from
        /// BrowserWindow.PumpEvents, beside the DOM event drain.
        /// </summary>
        internal static void Pump()
        {
            if (!IsAttached || !s_dirty) return;
            s_dirty = false;

            IAutomationTreeSource tree = Tree;
            if (tree == null) return;

            try
            {
                s_windows.RemoveAll(w => BrowserWindow.FromHandle(w) == null);
                BrowserWindow.Js.A11ySync(BuildMirrorJson(tree, s_windows));
            }
            catch (Exception e)
            {
                Console.WriteLine($"WPF browser accessibility sync failed: {e}");
            }
        }

        // A cap, because a mirror is real DOM. A tree this large is already past the point where a
        // screen reader is usable, and an unbounded walk would stall the frame.
        private const int MaxNodes = 2000;

        // And one on a text's length: the whole text crosses into the DOM on every sync, and a
        // document this long is read a screenful at a time anyway.
        internal const int MaxText = 100_000;

        /// <summary>
        /// The mirror of every window's tree, as the array a11ySync takes: one object per node,
        /// {"id","p","r","n","x","y","w","h"} plus the optional fields Emit documents.
        /// </summary>
        internal static string BuildMirrorJson(IAutomationTreeSource tree, IReadOnlyList<IntPtr> windows)
        {
            var json = new StringBuilder(1024);
            json.Append('[');
            int count = 0;
            var sync = new SyncState();
            foreach (IntPtr window in windows)
            {
                int root = tree.GetRootId(window);
                if (root < 0) continue;
                Emit(tree, root, IAutomationTreeSource.InvalidNode, window, json, ref count, depth: 0, sync);
            }
            json.Append(']');
            return json.ToString();
        }

        /// <summary>What one sync has already asked the tree: each table's headers, asked once per
        /// table rather than once per cell.</summary>
        private sealed class SyncState
        {
            internal readonly Dictionary<int, (int[] Columns, int[] Rows)> Headers = new();

            internal (int[] Columns, int[] Rows) HeadersOf(IAutomationTreeSource tree, int grid)
            {
                if (!Headers.TryGetValue(grid, out var headers))
                    Headers[grid] = headers = (tree.GetColumnHeaderIds(grid), tree.GetRowHeaderIds(grid));
                return headers;
            }
        }

        // The optional fields of a node:
        //   wh                 a window's root: the window, so a hidden window's subtree is hidden
        //   dis foc chk exp sel act v vmin vmax vt     states, a range, a value (as before)
        //   modal              a modal window (r is "dialog")
        //   tx ml ro ed ss se  a text field: its text, multi-line, read-only, editable (a value can be
        //                      written back), and the selection
        //   ms sbl             a container that selects many; an item that can be selected
        //   rc cc              a grid's rows and columns, its header row counted as ARIA counts it
        //   ri ci rs cs        a cell's (or row's, or header's) 1-based place and spans
        //   scr sh sv szh szv  a scrolling pane: its positions and view sizes, in percent
        private static void Emit(IAutomationTreeSource tree, int node, int parent, IntPtr window,
                                 StringBuilder json, ref int count, int depth, SyncState sync)
        {
            if (count >= MaxNodes || depth > 64) return;

            AccessibleState state = tree.GetState(node);

            // Offscreen nodes are not drawn, so mirroring them would put a screen reader on controls
            // the user cannot see and hand the keyboard a tab stop that goes nowhere.
            if ((state & AccessibleState.Offscreen) != 0) return;

            int controlType = tree.GetRole(node);
            string name = tree.GetName(node);
            string role = RoleOf(tree, node, controlType, state, parent, sync, out int rowIndex, out int columnIndex);

            // A node with neither a role nor a name contributes nothing to the tree; its children
            // still might, so it is skipped rather than pruned.
            bool worth = role.Length != 0 || name.Length != 0;

            if (worth && tree.TryGetBounds(node, out double x, out double y, out double w, out double h))
            {
                if (count++ > 0) json.Append(',');

                json.Append("{\"id\":").Append(node)
                    .Append(",\"p\":").Append(parent)
                    .Append(",\"r\":\"").Append(Escape(role)).Append('"')
                    .Append(",\"n\":\"").Append(Escape(name)).Append('"')
                    .Append(",\"x\":").Append(Round(x)).Append(",\"y\":").Append(Round(y))
                    .Append(",\"w\":").Append(Round(w)).Append(",\"h\":").Append(Round(h));

                if (parent == IAutomationTreeSource.InvalidNode) json.Append(",\"wh\":").Append((long)window);

                if ((state & AccessibleState.Enabled) == 0) json.Append(",\"dis\":1");
                if ((state & AccessibleState.Focusable) != 0) json.Append(",\"foc\":1");
                if ((state & AccessibleState.Checked) != 0) json.Append(",\"chk\":1");
                if ((state & AccessibleState.Indeterminate) != 0) json.Append(",\"chk\":2");
                if ((state & AccessibleState.Expanded) != 0) json.Append(",\"exp\":1");
                if ((state & AccessibleState.Collapsed) != 0) json.Append(",\"exp\":0");
                if ((state & AccessibleState.Selected) != 0) json.Append(",\"sel\":1");
                if ((state & AccessibleState.Selectable) != 0) json.Append(",\"sbl\":1");
                if ((state & (AccessibleState.SelectionContainer | AccessibleState.MultiSelectable))
                    == (AccessibleState.SelectionContainer | AccessibleState.MultiSelectable)) json.Append(",\"ms\":1");
                if (role == "dialog" && (state & AccessibleState.Modal) != 0) json.Append(",\"modal\":1");

                bool text = AppendText(tree, node, controlType, state, json);

                if (!text && tree.TryGetRange(node, out double value, out double min, out double max))
                {
                    json.Append(",\"v\":").Append(Round(value))
                        .Append(",\"vmin\":").Append(Round(min))
                        .Append(",\"vmax\":").Append(Round(max));
                }
                else if (!text && (state & AccessibleState.Password) == 0)   // a password's characters never reach the page
                {
                    string valueText = tree.GetValue(node);
                    if (valueText.Length != 0) json.Append(",\"vt\":\"").Append(Escape(valueText)).Append('"');
                }

                AppendGrid(tree, node, state, json, sync, rowIndex, columnIndex);
                AppendScroll(tree, node, state, json);

                if (tree.SupportsAction(node, AccessibleAction.Invoke) ||
                    tree.SupportsAction(node, AccessibleAction.Toggle) ||
                    tree.SupportsAction(node, AccessibleAction.Select))
                {
                    json.Append(",\"act\":1");
                }

                json.Append('}');
                parent = node;   // children hang off this node only if it was emitted
            }

            foreach (int child in tree.GetChildIds(node))
            {
                Emit(tree, child, parent, window, json, ref count, depth + 1, sync);
            }
        }

        /// <summary>
        /// The ARIA role. The control type's, except where a pattern says more than the control type
        /// does: a modal window is a dialog; a table's header is a columnheader or rowheader wherever
        /// it sits; a grid's cell is a gridcell (or a table's cell) whatever peer draws it. The
        /// header's place comes back with it, since finding out was the same question.
        /// </summary>
        internal static string RoleOf(IAutomationTreeSource tree, int node, int controlType, AccessibleState state,
                                      int parent, out int rowIndex, out int columnIndex)
            => RoleOf(tree, node, controlType, state, parent, new SyncState(), out rowIndex, out columnIndex);

        private static string RoleOf(IAutomationTreeSource tree, int node, int controlType, AccessibleState state,
                                     int parent, SyncState sync, out int rowIndex, out int columnIndex)
        {
            rowIndex = columnIndex = 0;
            string role = BrowserRoles.RoleFor(controlType);

            if ((state & (AccessibleState.Window | AccessibleState.Modal)) == (AccessibleState.Window | AccessibleState.Modal))
                return "dialog";

            // A header of a table: in its header list, which the table or its header row holds.
            int treeParent = tree.GetParentId(node);
            for (int g = treeParent, hops = 0; g != IAutomationTreeSource.InvalidNode && hops < 2; g = tree.GetParentId(g), hops++)
            {
                if ((tree.GetState(g) & AccessibleState.Table) == 0) continue;
                (int[] columns, int[] rows) = sync.HeadersOf(tree, g);
                int index = Array.IndexOf(columns, node);
                if (index >= 0) { rowIndex = 1; columnIndex = index + 1; return "columnheader"; }
                index = Array.IndexOf(rows, node);
                if (index >= 0) { rowIndex = index + 1 + (columns.Length > 0 ? 1 : 0); columnIndex = 1; return "rowheader"; }
            }

            if ((state & AccessibleState.GridItem) != 0 && role is not ("columnheader" or "rowheader"))
            {
                int grid = tree.TryGetGridItem(node, out _, out _, out _, out _, out int g) ? g : IAutomationTreeSource.InvalidNode;
                return grid != IAutomationTreeSource.InvalidNode && tree.GetRole(grid) == 36 ? "cell" : "gridcell";
            }
            return role;
        }

        /// <summary>
        /// A text field's text and selection, for a node the mirror makes a real text control: an
        /// edit box or a document with a Text pattern. Static text with one stays text, since a
        /// label turned into a read-only edit box would be announced as one. A password never puts
        /// its characters into the page. False when the node is not mirrored as a text control.
        /// </summary>
        internal static bool AppendText(IAutomationTreeSource tree, int node, int controlType, AccessibleState state, StringBuilder json)
        {
            if (!IsTextControl(controlType, state) || !tree.TryGetText(node, out string text)) return false;
            text ??= string.Empty;
            if (text.Length > MaxText) text = text.Substring(0, MaxText);

            json.Append(",\"tx\":\"").Append(Escape(text, keepControls: true)).Append('"');
            if (controlType == 30 || text.Contains('\n') || text.Contains('\r')) json.Append(",\"ml\":1");
            if ((state & AccessibleState.ReadOnly) != 0) json.Append(",\"ro\":1");
            if ((state & (AccessibleState.HasValue | AccessibleState.ReadOnly)) == AccessibleState.HasValue) json.Append(",\"ed\":1");

            if (tree.TryGetTextSelection(node, out int start, out int length))
            {
                start = Math.Clamp(start, 0, text.Length);
                length = Math.Clamp(length, 0, text.Length - start);
                json.Append(",\"ss\":").Append(start).Append(",\"se\":").Append(start + length);
            }
            return true;
        }

        internal static bool IsTextControl(int controlType, AccessibleState state)
            => (controlType == 4 || controlType == 30)
               && (state & AccessibleState.HasText) != 0 && (state & AccessibleState.Password) == 0;

        /// <summary>
        /// ARIA's grid numbers. Counts and indices are 1-based and count every row the user can
        /// reach, the header row among them -- so a table with column headers has one row more than
        /// its Grid pattern says, and its first data row is row 2.
        /// </summary>
        private static void AppendGrid(IAutomationTreeSource tree, int node, AccessibleState state, StringBuilder json,
                                       SyncState sync, int headerRow, int headerColumn)
        {
            if (headerRow > 0)
            {
                json.Append(",\"ri\":").Append(headerRow).Append(",\"ci\":").Append(headerColumn);
                return;
            }

            if ((state & AccessibleState.Grid) != 0 && tree.TryGetGrid(node, out int rows, out int columns))
            {
                int headerRows = (state & AccessibleState.Table) != 0 && sync.HeadersOf(tree, node).Columns.Length > 0 ? 1 : 0;
                json.Append(",\"rc\":").Append(Math.Max(rows, 0) + headerRows).Append(",\"cc\":").Append(Math.Max(columns, 0));
            }

            if ((state & AccessibleState.GridItem) != 0
                && tree.TryGetGridItem(node, out int row, out int column, out int rowSpan, out int columnSpan, out int grid))
            {
                int offset = HeaderRowsOf(tree, grid, sync);
                json.Append(",\"ri\":").Append(row + 1 + offset).Append(",\"ci\":").Append(column + 1);
                if (rowSpan > 1) json.Append(",\"rs\":").Append(rowSpan);
                if (columnSpan > 1) json.Append(",\"cs\":").Append(columnSpan);
                return;
            }

            // A row of a grid (a DataGrid's row, whose children are its cells): its cells' row.
            int parent = tree.GetParentId(node);
            if (parent == IAutomationTreeSource.InvalidNode || (tree.GetState(parent) & AccessibleState.Grid) == 0) return;
            foreach (int child in tree.GetChildIds(node))
            {
                if ((tree.GetState(child) & AccessibleState.GridItem) == 0) continue;
                if (!tree.TryGetGridItem(child, out row, out _, out _, out _, out grid)) continue;
                json.Append(",\"ri\":").Append(row + 1 + HeaderRowsOf(tree, grid != IAutomationTreeSource.InvalidNode ? grid : parent, sync));
                return;
            }
        }

        private static int HeaderRowsOf(IAutomationTreeSource tree, int grid, SyncState sync)
            => grid != IAutomationTreeSource.InvalidNode && (tree.GetState(grid) & AccessibleState.Table) != 0
               && sync.HeadersOf(tree, grid).Columns.Length > 0 ? 1 : 0;

        private static void AppendScroll(IAutomationTreeSource tree, int node, AccessibleState state, StringBuilder json)
        {
            if ((state & AccessibleState.Scrollable) == 0) return;
            if (!tree.TryGetScroll(node, out double hp, out double vp, out double hSize, out double vSize, out bool hs, out bool vs)) return;
            if (!hs && !vs) return;

            json.Append(",\"scr\":1")
                .Append(",\"sh\":").Append(hs ? Percent(hp) : "-1")
                .Append(",\"sv\":").Append(vs ? Percent(vp) : "-1")
                .Append(",\"szh\":").Append(Percent(hs ? hSize : 100))
                .Append(",\"szv\":").Append(Percent(vs ? vSize : 100));
        }

        private static string Percent(double value)
            => Math.Clamp(value, 0, 100).ToString("0.###", CultureInfo.InvariantCulture);

        /// <summary>
        /// The user did something to a mirror element: activated it (click, Enter, Space), focused
        /// it, or -- through the elements that are real controls -- selected text in it, scrolled
        /// it, typed into it, or pressed Escape in a modal dialog. Called from the drained event
        /// queue, so it is already on the dispatcher thread. The kinds that carry data leave it with
        /// the page, to be taken here (Js.A11yTake*), so the event itself stays two numbers.
        /// </summary>
        internal static void DispatchAction(int nodeId, int actionKind)
        {
            IAutomationTreeSource tree = Tree;
            if (tree == null || nodeId < 0) return;

            try
            {
                switch (actionKind)
                {
                    case ActionActivate:
                        foreach (AccessibleAction action in s_activateOrder)
                        {
                            if (tree.SupportsAction(nodeId, action)) { tree.DoAction(nodeId, action); return; }
                        }
                        break;
                    case ActionFocus:
                        // Reached by a screen reader's or the keyboard's navigation, which on the web
                        // moves focus and nothing else: an item half out of its pane is scrolled to,
                        // as UIA's ScrollItem would be asked.
                        if ((tree.GetState(nodeId) & AccessibleState.ScrollItem) != 0) tree.ScrollIntoView(nodeId);
                        tree.SetFocus(nodeId);
                        break;
                    case ActionTextSelection:
                        if (TryParsePair(Js.A11yTakeTextSelection(nodeId), out double start, out double end))
                            ApplyTextSelection(tree, nodeId, (int)start, (int)end);
                        break;
                    case ActionScroll:
                        if (TryParsePair(Js.A11yTakeScroll(nodeId), out double horizontal, out double vertical))
                            tree.SetScrollPercent(nodeId, horizontal, vertical);
                        break;
                    case ActionCloseModal:
                        CloseModal(tree, nodeId);
                        break;
                    case ActionSetValue:
                    {
                        string value = Js.A11yTakeValue(nodeId);
                        if (value != null && (tree.GetState(nodeId) & (AccessibleState.HasValue | AccessibleState.ReadOnly)) == AccessibleState.HasValue)
                            tree.SetValue(nodeId, value);
                        s_dirty = true;   // put back whatever WPF made of it
                        break;
                    }
                }
            }
            catch (Exception e)
            {
                Console.WriteLine($"WPF browser accessibility action failed: {e}");
            }
        }

        /// <summary>A selection made in a mirror text control, as the tree's start and length.
        /// The DOM's offsets are UTF-16 code units, as the tree's are.</summary>
        internal static bool ApplyTextSelection(IAutomationTreeSource tree, int nodeId, int start, int end)
        {
            if (!tree.TryGetText(nodeId, out string text)) return false;
            int length = (text ?? string.Empty).Length;
            start = Math.Clamp(start, 0, length);
            end = Math.Clamp(end, 0, length);
            if (end < start) (start, end) = (end, start);
            if (tree.TryGetTextSelection(nodeId, out int s, out int l) && s == start && l == end - start) return true;
            return tree.SetTextSelection(nodeId, start, end - start);
        }

        /// <summary>
        /// Escape in a modal dialog's mirror: close the dialog, as ARIA's dialog pattern has Escape
        /// do. The node is the dialog's root; anything else is ignored.
        /// </summary>
        internal static bool CloseModal(IAutomationTreeSource tree, int nodeId)
            => (tree.GetState(nodeId) & (AccessibleState.Window | AccessibleState.Modal)) == (AccessibleState.Window | AccessibleState.Modal)
               && tree.CloseWindow(nodeId);

        internal static bool TryParsePair(string pair, out double first, out double second)
        {
            first = second = 0;
            if (string.IsNullOrEmpty(pair)) return false;
            int comma = pair.IndexOf(',');
            return comma > 0
                && double.TryParse(pair.AsSpan(0, comma), NumberStyles.Float, CultureInfo.InvariantCulture, out first)
                && double.TryParse(pair.AsSpan(comma + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out second);
        }

        private static readonly AccessibleAction[] s_activateOrder =
        {
            AccessibleAction.Toggle, AccessibleAction.Invoke, AccessibleAction.Select, AccessibleAction.Expand,
        };

        private static long Round(double value) => (long)Math.Round(value);

        private static string Escape(string value) => Escape(value, keepControls: false);

        private static string Escape(string value, bool keepControls)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;

            StringBuilder sb = null;
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                // U+2028 and U+2029 are valid in JSON but end a line in older JavaScript parsers; they
                // are escaped with the control characters for that reason.
                if (c is '"' or '\\' or < ' ' or '\u2028' or '\u2029')
                {
                    sb ??= new StringBuilder(value.Length + 8).Append(value, 0, i);
                    switch (c)
                    {
                        case '"': sb.Append("\\\""); break;
                        case '\\': sb.Append("\\\\"); break;
                        case '\n': sb.Append("\\n"); break;
                        case '\r': sb.Append("\\r"); break;
                        case '\t': sb.Append("\\t"); break;
                        default:
                            // Other control characters carry no meaning in a label; in a text field's
                            // text they are kept, because the selection's offsets count them.
                            if (keepControls || c >= ' ') sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                            break;
                    }
                }
                else
                {
                    sb?.Append(c);
                }
            }
            return sb?.ToString() ?? value;
        }

        /// <summary>The browser-window.js exports for the data an action leaves with the page.</summary>
        private static partial class Js
        {
            private const string Module = "wpfBrowserWindow";

            /// <summary>The selection a mirror text control was given, "start,end"; empty when none is pending.</summary>
            [JSImport("a11yTakeTextSelection", Module)]
            internal static partial string A11yTakeTextSelection(int nodeId);

            /// <summary>Where a mirror pane was scrolled, "horizontal,vertical" percent (-1 for an axis that does not scroll).</summary>
            [JSImport("a11yTakeScroll", Module)]
            internal static partial string A11yTakeScroll(int nodeId);

            /// <summary>The text a mirror text control was edited to; null when none is pending.</summary>
            [JSImport("a11yTakeValue", Module)]
            internal static partial string A11yTakeValue(int nodeId);
        }
    }

    /// <summary>
    /// AutomationControlType to ARIA roles. An empty string means "no role" -- for a plain text run
    /// that is correct: ARIA has no role for it, and inventing one makes a screen reader announce
    /// scaffolding.
    /// </summary>
    internal static class BrowserRoles
    {
        internal static string RoleFor(int automationControlType) => automationControlType switch
        {
            0 => "button",
            1 => "application",     // Calendar
            2 => "checkbox",
            3 => "combobox",
            4 => "textbox",         // Edit
            5 => "link",
            6 => "img",
            7 => "option",          // ListItem
            8 => "listbox",
            9 => "menu",
            10 => "menubar",
            11 => "menuitem",
            12 => "progressbar",
            13 => "radio",
            14 => "scrollbar",
            15 => "slider",
            16 => "spinbutton",
            17 => "status",         // StatusBar
            18 => "tablist",
            19 => "tab",
            20 => "",               // Text
            21 => "toolbar",
            22 => "tooltip",
            23 => "tree",
            24 => "treeitem",
            25 => "",               // Custom
            26 => "group",
            27 => "",               // Thumb
            28 => "grid",
            29 => "row",            // DataItem
            30 => "document",
            31 => "button",         // SplitButton
            32 => "",               // Window -- the page itself
            33 => "region",         // Pane
            34 => "rowgroup",       // Header
            35 => "columnheader",
            36 => "table",
            37 => "banner",         // TitleBar
            38 => "separator",
            _ => "",
        };
    }
}
