// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// AutomationControlType and state, translated into AT-SPI's vocabulary.
//
// Its own file, and free of any D-Bus type, so the mapping can be unit-tested on a machine with no
// accessibility bus -- which is where most of this repo's development happens. Role numbers are
// AtspiRole from atspi-constants.h; state numbers are AtspiStateType. Both are ABI, so they are
// spelled out rather than derived.
//

namespace MS.Internal.Interop.Wayland
{
    internal static class AtSpiRoles
    {
        // AtspiRole values used here. These are ABI: a wrong number is not a compile error and not a
        // protocol error either -- the client simply believes a different control is there, which is
        // why they are checked against the installed at-spi2 by eng/check-atspi-constants.py rather
        // than trusted to review. TABLE_ROW and TREE_ITEM are NOT adjacent to their neighbours; they
        // were appended to the end of the enum long after the block they belong to.
        private const uint Invalid = 0;
        private const uint CheckBox = 7;
        private const uint ColumnHeader = 10;
        private const uint ComboBox = 11;
        private const uint Dialog = 16;
        private const uint DocumentFrame = 82;
        private const uint Filler = 20;
        private const uint Frame = 23;
        private const uint Icon = 26;
        private const uint Image = 27;
        private const uint Label = 29;
        private const uint Link = 88;
        private const uint List = 31;
        private const uint ListItem = 32;
        private const uint Menu = 33;
        private const uint MenuBar = 34;
        private const uint MenuItem = 35;
        private const uint PageTab = 37;
        private const uint PageTabList = 38;
        private const uint Panel = 39;
        private const uint PasswordText = 40;
        private const uint PushButton = 43;
        private const uint ProgressBar = 42;
        private const uint RadioButton = 44;
        private const uint ScrollBar = 48;
        private const uint Separator = 50;
        private const uint Slider = 51;
        private const uint SpinButton = 52;
        private const uint StatusBar = 54;
        private const uint Table = 55;
        private const uint TableCell = 56;
        private const uint TableRow = 90;
        private const uint Text = 61;
        private const uint ToolBar = 63;
        private const uint ToolTip = 64;
        private const uint Tree = 65;
        private const uint TreeItem = 91;
        private const uint Calendar = 5;
        private const uint Unknown = 67;

        internal static uint RoleFor(int automationControlType) => automationControlType switch
        {
            0 => PushButton,
            1 => Calendar,
            2 => CheckBox,
            3 => ComboBox,
            4 => Text,              // Edit -- PasswordText is substituted by StatesFor's caller
            5 => Link,
            6 => Image,
            7 => ListItem,
            8 => List,
            9 => Menu,
            10 => MenuBar,
            11 => MenuItem,
            12 => ProgressBar,
            13 => RadioButton,
            14 => ScrollBar,
            15 => Slider,
            16 => SpinButton,
            17 => StatusBar,
            18 => PageTabList,
            19 => PageTab,
            20 => Label,
            21 => ToolBar,
            22 => ToolTip,
            23 => Tree,
            24 => TreeItem,
            25 => Unknown,          // Custom
            26 => Panel,            // Group
            27 => Slider,           // Thumb
            28 => Table,            // DataGrid
            29 => TableRow,         // DataItem
            30 => DocumentFrame,
            31 => PushButton,       // SplitButton
            32 => Frame,            // Window
            33 => Panel,            // Pane
            34 => Panel,            // Header
            35 => ColumnHeader,
            36 => Table,
            37 => Panel,            // TitleBar
            38 => Separator,
            _ => Unknown,
        };

        /// <summary>
        /// The human-readable role name. AT-SPI clients show these directly, and Orca speaks them,
        /// so they are the lowercase names from the specification rather than anything invented.
        /// </summary>
        internal static string RoleNameFor(int automationControlType) => RoleFor(automationControlType) switch
        {
            PushButton => "push button",
            CheckBox => "check box",
            RadioButton => "radio button",
            ComboBox => "combo box",
            Text => "text",
            PasswordText => "password text",
            Link => "link",
            Image => "image",
            Icon => "icon",
            List => "list",
            ListItem => "list item",
            Menu => "menu",
            MenuBar => "menu bar",
            MenuItem => "menu item",
            ProgressBar => "progress bar",
            ScrollBar => "scroll bar",
            Slider => "slider",
            SpinButton => "spin button",
            StatusBar => "status bar",
            PageTabList => "page tab list",
            PageTab => "page tab",
            Label => "label",
            ToolBar => "tool bar",
            ToolTip => "tool tip",
            Tree => "tree",
            TreeItem => "tree item",
            Panel => "panel",
            Table => "table",
            TableRow => "table row",
            TableCell => "table cell",
            ColumnHeader => "column header",
            DocumentFrame => "document frame",
            Frame => "frame",
            Dialog => "dialog",
            Separator => "separator",
            Calendar => "calendar",
            Filler => "filler",
            _ => "unknown",
        };

        // AtspiStateType values, as bit positions in the 64-bit state set. ABI, like the roles above,
        // and with the same guard -- and here a wrong number is worse than a wrong role, because
        // VISIBLE and SHOWING decide whether Orca considers the object to be on screen at all.
        private const int StateChecked = 4;
        private const int StateEditable = 7;
        private const int StateEnabled = 8;
        private const int StateExpandable = 9;
        private const int StateExpanded = 10;
        private const int StateFocusable = 11;
        private const int StateFocused = 12;
        private const int StateSelectable = 22;
        private const int StateSelected = 23;
        private const int StateSensitive = 24;
        private const int StateShowing = 25;
        private const int StateVisible = 30;
        private const int StateIndeterminate = 32;
        private const int StateCheckable = 41;

        /// <summary>
        /// The AT-SPI state set as a 64-bit mask. Sent as two uint32s, low word first.
        ///
        /// Note SHOWING and VISIBLE are distinct in AT-SPI: visible means "would be drawn if
        /// scrolled to", showing means "actually on screen". WPF's IsOffscreen answers the second,
        /// so an offscreen node keeps VISIBLE and loses SHOWING -- which is what lets Orca announce
        /// a list item that exists but is scrolled out of view.
        /// </summary>
        internal static ulong StatesFor(AccessibleState state)
        {
            ulong bits = 0;

            if ((state & AccessibleState.Enabled) != 0)
            {
                bits |= 1UL << StateEnabled;
                bits |= 1UL << StateSensitive;
            }
            if ((state & AccessibleState.Focusable) != 0) bits |= 1UL << StateFocusable;
            if ((state & AccessibleState.Focused) != 0) bits |= 1UL << StateFocused;
            if ((state & AccessibleState.Selectable) != 0) bits |= 1UL << StateSelectable;
            if ((state & AccessibleState.Selected) != 0) bits |= 1UL << StateSelected;

            bits |= 1UL << StateVisible;
            if ((state & AccessibleState.Offscreen) == 0) bits |= 1UL << StateShowing;

            if ((state & AccessibleState.Checked) != 0)
            {
                bits |= 1UL << StateChecked;
                bits |= 1UL << StateCheckable;
            }
            if ((state & AccessibleState.Indeterminate) != 0)
            {
                bits |= 1UL << StateIndeterminate;
                bits |= 1UL << StateCheckable;
            }

            if ((state & AccessibleState.Expanded) != 0)
            {
                bits |= 1UL << StateExpanded;
                bits |= 1UL << StateExpandable;
            }
            if ((state & AccessibleState.Collapsed) != 0) bits |= 1UL << StateExpandable;

            if ((state & AccessibleState.HasValue) != 0 && (state & AccessibleState.ReadOnly) == 0)
            {
                bits |= 1UL << StateEditable;
            }

            return bits;
        }

        /// <summary>
        /// The AT-SPI signal one change becomes: the member on org.a11y.atspi.Event.Object, and the
        /// event's detail string.
        ///
        /// A table rather than a switch inside the emitter so it can be tested without a bus, which
        /// is the only place these spellings can be checked at all: Orca matches on the concatenation
        /// ("object:state-changed:expanded"), so a typo does not fail, error or log -- the event is
        /// simply delivered to nobody. The VALUE each carries is not here, because for the state
        /// kinds it has to be read back from the tree.
        /// </summary>
        internal static (string Member, string Detail) SignalFor(AutomationChangeKind kind) => kind switch
        {
            AutomationChangeKind.FocusChanged => ("StateChanged", "focused"),
            AutomationChangeKind.SelectionChanged => ("StateChanged", "selected"),
            AutomationChangeKind.ExpandedChanged => ("StateChanged", "expanded"),
            AutomationChangeKind.CheckedChanged => ("StateChanged", "checked"),
            AutomationChangeKind.EnabledChanged => ("StateChanged", "enabled"),
            AutomationChangeKind.OffscreenChanged => ("StateChanged", "showing"),

            AutomationChangeKind.ValueChanged => ("PropertyChange", "accessible-value"),
            AutomationChangeKind.NameChanged => ("PropertyChange", "accessible-name"),
            AutomationChangeKind.DescriptionChanged => ("PropertyChange", "accessible-description"),
            // Unclassified. Clients treat a name change as "re-read this node", which is the most
            // useful thing "something about it changed" can be turned into.
            AutomationChangeKind.PropertyChanged => ("PropertyChange", "accessible-name"),

            AutomationChangeKind.ChildAdded => ("ChildrenChanged", "add"),
            AutomationChangeKind.ChildRemoved => ("ChildrenChanged", "remove"),
            // WPF's bulk notification, where neither direction nor index is known. AT-SPI has no
            // "invalidated" detail; an add at index -1 is how a client is told to re-read.
            AutomationChangeKind.ChildrenChanged => ("ChildrenChanged", "add"),

            _ => (string.Empty, string.Empty),
        };
    }
}
