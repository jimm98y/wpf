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
        // AtspiRole values used here.
        private const uint Invalid = 0;
        private const uint CheckBox = 8;
        private const uint ColumnHeader = 10;
        private const uint ComboBox = 11;
        private const uint Dialog = 16;
        private const uint DocumentFrame = 82;
        private const uint Filler = 21;
        private const uint Frame = 22;
        private const uint Icon = 25;
        private const uint Image = 26;
        private const uint Label = 29;
        private const uint Link = 88;
        private const uint List = 31;
        private const uint ListItem = 32;
        private const uint Menu = 33;
        private const uint MenuBar = 34;
        private const uint MenuItem = 35;
        private const uint PageTab = 36;
        private const uint PageTabList = 37;
        private const uint Panel = 39;
        private const uint PasswordText = 40;
        private const uint PushButton = 43;
        private const uint ProgressBar = 42;
        private const uint RadioButton = 44;
        private const uint ScrollBar = 47;
        private const uint Separator = 49;
        private const uint Slider = 50;
        private const uint SpinButton = 52;
        private const uint StatusBar = 53;
        private const uint Table = 54;
        private const uint TableCell = 55;
        private const uint TableRow = 57;
        private const uint Text = 60;
        private const uint ToolBar = 62;
        private const uint ToolTip = 63;
        private const uint Tree = 64;
        private const uint TreeItem = 65;
        private const uint Calendar = 5;
        private const uint Unknown = 66;

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
            StatusBar => "statusbar",
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

        // AtspiStateType values, as bit positions in the 64-bit state set.
        private const int StateEnabled = 8;
        private const int StateExpandable = 10;
        private const int StateExpanded = 11;
        private const int StateFocusable = 12;
        private const int StateFocused = 13;
        private const int StateSelectable = 18;
        private const int StateSelected = 19;
        private const int StateSensitive = 20;
        private const int StateShowing = 21;
        private const int StateVisible = 22;
        private const int StateChecked = 4;
        private const int StateIndeterminate = 15;
        private const int StateEditable = 7;
        private const int StateCheckable = 34;

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
    }
}
