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

using System;
using System.Collections.Generic;
using System.Runtime.Versioning;
using System.Text;

namespace MS.Internal.Interop
{
    [SupportedOSPlatform("browser")]
    internal static class BrowserAccessibility
    {
        internal static bool IsAvailable => OperatingSystem.IsBrowser();

        /// <summary>True once the mirror is being maintained.</summary>
        internal static bool IsAttached { get; private set; }

        // Set when something changed and the mirror has not caught up. Drained on the pump tick, so
        // a burst of property changes during one layout costs one rebuild, not one per change.
        private static bool s_dirty;
        private static IntPtr s_window;

        private static IAutomationTreeSource Tree => AutomationTree.Current;

        /// <summary>
        /// Starts mirroring for a window. Called when the first browser window is created; the host
        /// page can decline with &lt;meta name="wpf-a11y" content="off"&gt;.
        /// </summary>
        internal static void Attach(IntPtr window)
        {
            if (!IsAvailable || IsAttached) return;
            if (!BrowserWindow.Js.A11yIsEnabled()) return;

            IsAttached = true;
            s_window = window;

            AutomationTree.RequestActivation();
            AutomationTree.Changed += OnTreeChanged;
            s_dirty = true;
        }

        private static void OnTreeChanged(AutomationChange change)
        {
            // Focus is the one change worth pushing immediately: a screen reader that hears about it
            // late reads the previous control. Everything else rides the next frame.
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
                int root = tree.GetRootId(s_window);
                if (root < 0) return;

                var json = new StringBuilder(1024);
                json.Append('[');
                int count = 0;
                Emit(tree, root, IAutomationTreeSource.InvalidNode, json, ref count, depth: 0);
                json.Append(']');

                BrowserWindow.Js.A11ySync(json.ToString());
            }
            catch (Exception e)
            {
                Console.WriteLine($"WPF browser accessibility sync failed: {e}");
            }
        }

        // A cap, because a mirror is real DOM. A tree this large is already past the point where a
        // screen reader is usable, and an unbounded walk would stall the frame.
        private const int MaxNodes = 2000;

        private static void Emit(IAutomationTreeSource tree, int node, int parent,
                                 StringBuilder json, ref int count, int depth)
        {
            if (count >= MaxNodes || depth > 64) return;

            AccessibleState state = tree.GetState(node);

            // Offscreen nodes are not drawn, so mirroring them would put a screen reader on controls
            // the user cannot see and hand the keyboard a tab stop that goes nowhere.
            if ((state & AccessibleState.Offscreen) != 0) return;

            string role = BrowserRoles.RoleFor(tree.GetRole(node));
            string name = tree.GetName(node);

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

                if ((state & AccessibleState.Enabled) == 0) json.Append(",\"dis\":1");
                if ((state & AccessibleState.Focusable) != 0) json.Append(",\"foc\":1");
                if ((state & AccessibleState.Checked) != 0) json.Append(",\"chk\":1");
                if ((state & AccessibleState.Indeterminate) != 0) json.Append(",\"chk\":2");
                if ((state & AccessibleState.Expanded) != 0) json.Append(",\"exp\":1");
                if ((state & AccessibleState.Collapsed) != 0) json.Append(",\"exp\":0");
                if ((state & AccessibleState.Selected) != 0) json.Append(",\"sel\":1");

                if (tree.TryGetRange(node, out double value, out double min, out double max))
                {
                    json.Append(",\"v\":").Append(Round(value))
                        .Append(",\"vmin\":").Append(Round(min))
                        .Append(",\"vmax\":").Append(Round(max));
                }
                else
                {
                    string text = tree.GetValue(node);
                    if (text.Length != 0) json.Append(",\"vt\":\"").Append(Escape(text)).Append('"');
                }

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
                Emit(tree, child, parent, json, ref count, depth + 1);
            }
        }

        /// <summary>
        /// The user activated a mirror element (click, Enter, Space). Called from the drained event
        /// queue, so it is already on the dispatcher thread.
        /// </summary>
        internal static void DispatchAction(int nodeId, int actionKind)
        {
            IAutomationTreeSource tree = Tree;
            if (tree == null || nodeId < 0) return;

            try
            {
                switch (actionKind)
                {
                    case 0:   // activate
                        foreach (AccessibleAction action in s_activateOrder)
                        {
                            if (tree.SupportsAction(nodeId, action)) { tree.DoAction(nodeId, action); return; }
                        }
                        break;
                    case 1:   // focus
                        tree.SetFocus(nodeId);
                        break;
                }
            }
            catch (Exception e)
            {
                Console.WriteLine($"WPF browser accessibility action failed: {e}");
            }
        }

        private static readonly AccessibleAction[] s_activateOrder =
        {
            AccessibleAction.Toggle, AccessibleAction.Invoke, AccessibleAction.Select, AccessibleAction.Expand,
        };

        private static long Round(double value) => (long)Math.Round(value);

        private static string Escape(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;

            StringBuilder sb = null;
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (c is '"' or '\\' or < ' ')
                {
                    sb ??= new StringBuilder(value.Length + 8).Append(value, 0, i);
                    sb.Append(c switch
                    {
                        '"' => "\\\"",
                        '\\' => "\\\\",
                        '\n' => "\\n",
                        '\r' => "\\r",
                        '\t' => "\\t",
                        _ => "",     // other control characters carry no meaning in a label
                    });
                }
                else
                {
                    sb?.Append(c);
                }
            }
            return sb?.ToString() ?? value;
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
