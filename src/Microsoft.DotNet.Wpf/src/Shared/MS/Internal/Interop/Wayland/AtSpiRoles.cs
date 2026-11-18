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

using System;
using System.Collections.Generic;
using System.Globalization;

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
        private const uint ScrollPane = 49;
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
        /// The role for a node, given its patterns as well as its control type. A grid cell, a
        /// scrolling pane and a WinForms form usually arrive with a control type that says nothing
        /// (Custom, Group, Pane), and to Orca the role decides how a node is read: a cell with its
        /// row and column, a scroll pane as a container rather than a stop, a frame as a window. A
        /// type that already means something -- a list that scrolls, a text box in a cell -- keeps
        /// its own role. Two substitutions apply to any type: a password box is PASSWORD_TEXT, so
        /// Orca does not echo what is typed into it, and a modal window is a DIALOG, which is what
        /// makes Orca read its contents when it opens.
        /// </summary>
        internal static uint RoleFor(int automationControlType, AccessibleState state)
        {
            uint role = RoleFor(automationControlType);
            if (role == Text && (state & AccessibleState.Password) != 0) return PasswordText;

            if (role is Panel or Unknown)
            {
                if ((state & AccessibleState.Window) != 0) role = Frame;
                else if ((state & AccessibleState.GridItem) != 0) return TableCell;
                else if ((state & AccessibleState.Scrollable) != 0) return ScrollPane;
            }

            if (role == Frame && (state & AccessibleState.Modal) != 0) return Dialog;
            return role;
        }

        /// <summary>
        /// The human-readable role name. AT-SPI clients show these directly, and Orca speaks them,
        /// so they are the lowercase names from the specification rather than anything invented.
        /// </summary>
        internal static string RoleNameFor(int automationControlType) => NameOfRole(RoleFor(automationControlType));

        /// <summary>The role name for a node, by the same rules as <see cref="RoleFor(int, AccessibleState)"/>.</summary>
        internal static string RoleNameFor(int automationControlType, AccessibleState state)
            => NameOfRole(RoleFor(automationControlType, state));

        private static string NameOfRole(uint role) => role switch
        {
            PushButton => "button",           // ATSPI_ROLE_BUTTON (at-spi2 renamed PUSH_BUTTON; its name followed)
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
            ScrollPane => "scroll pane",
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
        private const int StateIconified = 15;
        private const int StateModal = 16;
        private const int StateMultiselectable = 18;
        private const int StateSelectable = 22;
        private const int StateSelected = 23;
        private const int StateSensitive = 24;
        private const int StateShowing = 25;
        private const int StateVisible = 30;
        private const int StateIndeterminate = 32;
        private const int StateSelectableText = 38;
        private const int StateCheckable = 41;
        private const int StateReadOnly = 43;

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
            // READ_ONLY is not the absence of EDITABLE: it is what tells Orca a text it can browse
            // is one the user cannot type into, rather than one it knows nothing about.
            if ((state & (AccessibleState.HasValue | AccessibleState.HasText)) != 0 &&
                (state & AccessibleState.ReadOnly) != 0)
            {
                bits |= 1UL << StateReadOnly;
            }
            // Text whose characters can be selected, which is what gates Orca's selection commands.
            // A password box's cannot be read, so they cannot be selected either, as far as anyone
            // listening is concerned.
            if ((state & AccessibleState.HasText) != 0 && (state & AccessibleState.Password) == 0)
            {
                bits |= 1UL << StateSelectableText;
            }

            if ((state & AccessibleState.MultiSelectable) != 0) bits |= 1UL << StateMultiselectable;
            if ((state & AccessibleState.Modal) != 0) bits |= 1UL << StateModal;

            return bits;
        }

        /// <summary>
        /// The state set for a window, whose minimized state is not a flag but the Window pattern's
        /// visual state (WindowVisualState: 2 is minimized). ICONIFIED is what Orca reads when the
        /// user cycles through windows and lands on one that is not on screen.
        /// </summary>
        internal static ulong StatesFor(AccessibleState state, int windowVisualState)
        {
            ulong bits = StatesFor(state);
            if ((state & AccessibleState.Window) != 0 && windowVisualState == 2)
            {
                bits |= 1UL << StateIconified;
                bits &= ~(1UL << StateShowing);
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

            // object:text-changed:insert or :delete. The direction is not in the change -- WPF says
            // only that the text is different -- so the emitter diffs against the text it last saw
            // and sends delete, insert or both (AtSpiText.Diff). "insert" is the entry here because
            // it is the half every edit that adds text produces.
            AutomationChangeKind.TextChanged => ("TextChanged", "insert"),

            // These have NO detail in AT-SPI: Orca subscribes to "object:text-caret-moved",
            // "object:selection-changed" and "object:visible-data-changed" as they are, and a
            // detail appended to them is a different event that nobody listens for. A text
            // selection change is a caret move first; the emitter adds object:text-selection-changed
            // when there is, or was, a selected range (AtSpiText.SelectionSignals).
            AutomationChangeKind.TextSelectionChanged => ("TextCaretMoved", string.Empty),
            AutomationChangeKind.SelectionContainerChanged => ("SelectionChanged", string.Empty),
            AutomationChangeKind.ScrollChanged => ("VisibleDataChanged", string.Empty),

            _ => (string.Empty, string.Empty),
        };
    }

    /// <summary>
    /// The Text interface's arithmetic, free of D-Bus so it can be tested where there is no bus.
    ///
    /// Two vocabularies meet here. The tree counts in UTF-16 code units, as .NET strings do; AT-SPI
    /// counts in CHARACTERS -- Unicode code points, as GLib's UTF-8 strings do. They agree until the
    /// text holds anything outside the Basic Multilingual Plane (an emoji, a CJK extension
    /// ideograph), and from there on every offset past it is off by one per pair. Orca does not
    /// error on a wrong offset; it reads the wrong word. So every offset crosses this boundary
    /// through <see cref="ToCodePoints"/> or <see cref="ToUtf16"/>, and nothing else.
    /// </summary>
    internal static class AtSpiText
    {
        // AtspiTextBoundaryType, GetTextAtOffset and its Before/After siblings. Internal rather than
        // private so the constants checker, which reads every "private const" here as a role or a
        // state, does not mistake them for either.
        internal const uint BoundaryChar = 0;
        internal const uint BoundaryWordStart = 1;
        internal const uint BoundaryWordEnd = 2;
        internal const uint BoundarySentenceStart = 3;
        internal const uint BoundarySentenceEnd = 4;
        internal const uint BoundaryLineStart = 5;
        internal const uint BoundaryLineEnd = 6;

        // AtspiTextGranularity, GetStringAtOffset's newer and simpler spelling of the same question.
        internal const uint GranularityChar = 0;
        internal const uint GranularityWord = 1;
        internal const uint GranularitySentence = 2;
        internal const uint GranularityLine = 3;
        internal const uint GranularityParagraph = 4;

        /// <summary>The units text can be cut into, both vocabularies' worth.</summary>
        internal enum Unit { Char, WordStart, WordEnd, SentenceStart, SentenceEnd, LineStart, LineEnd, Paragraph }

        /// <summary>
        /// The tree's answer to "which line is this offset on": the line's first offset and its
        /// length, line break included, in UTF-16. False when the tree has no line geometry.
        /// </summary>
        internal delegate bool LineSource(int utf16Offset, out int start, out int length);

        internal static bool TryUnitForBoundary(uint boundary, out Unit unit)
        {
            unit = boundary switch
            {
                BoundaryChar => Unit.Char,
                BoundaryWordStart => Unit.WordStart,
                BoundaryWordEnd => Unit.WordEnd,
                BoundarySentenceStart => Unit.SentenceStart,
                BoundarySentenceEnd => Unit.SentenceEnd,
                BoundaryLineStart => Unit.LineStart,
                BoundaryLineEnd => Unit.LineEnd,
                _ => Unit.Char,
            };
            return boundary <= BoundaryLineEnd;
        }

        /// <summary>
        /// A granularity as the boundary it means. ATK defines WORD and LINE as running from the
        /// start of the unit to the start of the next -- the START boundaries -- and GTK and Qt
        /// answer them that way, so a client switching from one call to the other hears the same
        /// words.
        /// </summary>
        internal static bool TryUnitForGranularity(uint granularity, out Unit unit)
        {
            unit = granularity switch
            {
                GranularityChar => Unit.Char,
                GranularityWord => Unit.WordStart,
                GranularitySentence => Unit.SentenceStart,
                GranularityLine => Unit.LineStart,
                GranularityParagraph => Unit.Paragraph,
                _ => Unit.Char,
            };
            return granularity <= GranularityParagraph;
        }

        // ---- UTF-16 and code points ------------------------------------------------

        private static bool IsPairAt(string text, int i)
            => i + 1 < text.Length && char.IsHighSurrogate(text[i]) && char.IsLowSurrogate(text[i + 1]);

        /// <summary>The UTF-16 length of the character starting at <paramref name="i"/>: 2 for a pair, else 1.</summary>
        private static int Step(string text, int i) => IsPairAt(text, i) ? 2 : 1;

        /// <summary>The text's length in characters. A lone surrogate counts as one, as GLib's
        /// UTF-8 conversion turns it into one replacement character.</summary>
        internal static int CharacterCount(string text) => ToCodePoints(text, text?.Length ?? 0);

        /// <summary>
        /// A UTF-16 offset as a character offset. An offset that falls between the halves of a pair
        /// is the pair's own offset: the caret cannot stand inside a character, and the tree should
        /// never say it does, but a range end computed by arithmetic can.
        /// </summary>
        internal static int ToCodePoints(string text, int utf16Offset)
        {
            if (string.IsNullOrEmpty(text) || utf16Offset <= 0) return 0;
            int limit = Math.Min(utf16Offset, text.Length);
            int count = 0;
            for (int i = 0; i < limit; )
            {
                int step = Step(text, i);
                if (i + step > limit) break;   // the offset is inside this pair
                i += step;
                count++;
            }
            return count;
        }

        /// <summary>A character offset as a UTF-16 offset, clamped to the text. -1 is the end, as
        /// AT-SPI's GetText spells it.</summary>
        internal static int ToUtf16(string text, int codePointOffset)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            if (codePointOffset < 0) return text.Length;
            int i = 0;
            for (int n = 0; n < codePointOffset && i < text.Length; n++) i += Step(text, i);
            return Math.Min(i, text.Length);
        }

        /// <summary>The characters from <paramref name="start"/> up to <paramref name="end"/>; -1 (or anything
        /// past the end) is the end. A backwards range is empty, as ATK's is, rather than swapped.</summary>
        internal static string Substring(string text, int start, int end)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            int s = ToUtf16(text, Math.Max(start, 0));
            int e = end < 0 ? text.Length : ToUtf16(text, end);
            return e > s ? text.Substring(s, e - s) : string.Empty;
        }

        /// <summary>The character at an offset as a code point, or 0 past the end -- the answer
        /// GetCharacterAtOffset gives, an int and not a string.</summary>
        internal static int CharacterAt(string text, int codePointOffset)
        {
            if (string.IsNullOrEmpty(text) || codePointOffset < 0) return 0;
            int i = ToUtf16(text, codePointOffset);
            if (i >= text.Length) return 0;
            return IsPairAt(text, i) ? char.ConvertToUtf32(text[i], text[i + 1]) : text[i];
        }

        /// <summary>The UTF-16 range of the one character at a character offset, for its extents.</summary>
        internal static (int Start, int Length) CharacterRange(string text, int codePointOffset)
        {
            int i = ToUtf16(text, Math.Max(codePointOffset, 0));
            return i >= (text?.Length ?? 0) ? (i, 0) : (i, Step(text, i));
        }

        // ---- text at, before and after an offset -----------------------------------

        /// <summary>
        /// GetTextAtOffset and its siblings: the unit around a character offset (<paramref name="direction"/>
        /// 0), the one before it (-1) or the one after it (1), and where it starts and ends, in characters.
        /// "Before" is the unit that ends where this one starts; "after", the one that starts where it
        /// ends -- which is how a client steps through a text a word or a line at a time.
        /// </summary>
        internal static (string Text, int Start, int End) TextAt(string text, int codePointOffset, Unit unit,
                                                                 int direction, LineSource lines)
        {
            text ??= string.Empty;
            int length = text.Length;
            int offset = ToUtf16(text, Math.Max(codePointOffset, 0));

            (int s, int e) = UnitAt(text, offset, unit, lines);
            if (direction < 0)
            {
                if (s <= 0) (s, e) = (0, 0);
                else (s, e) = UnitAt(text, s - 1, unit, lines);
            }
            else if (direction > 0)
            {
                if (e >= length) (s, e) = (length, length);
                else (s, e) = UnitAt(text, e, unit, lines);
            }

            s = Math.Clamp(s, 0, length);
            e = Math.Clamp(e, s, length);
            return (text.Substring(s, e - s), ToCodePoints(text, s), ToCodePoints(text, e));
        }

        /// <summary>The unit around a UTF-16 offset, as a UTF-16 range.</summary>
        internal static (int Start, int End) UnitAt(string text, int offset, Unit unit, LineSource lines)
        {
            int length = text.Length;
            offset = Math.Clamp(offset, 0, length);
            // never inside a pair: the character it is in starts one unit back
            if (offset > 0 && offset < length && char.IsLowSurrogate(text[offset]) && char.IsHighSurrogate(text[offset - 1])) offset--;

            switch (unit)
            {
                case Unit.Char:
                    return offset >= length ? (length, length) : (offset, offset + Step(text, offset));

                case Unit.LineStart:
                {
                    (int start, int end, _) = LineOf(text, offset, lines);
                    return (start, end);
                }
                case Unit.LineEnd:
                    return LineEndUnit(text, offset, lines);
                case Unit.Paragraph:
                {
                    (int start, int end, _) = LineOf(text, offset, null);
                    return (start, end);
                }

                default:
                {
                    // At the very end there is no character to be in: the unit is the last one, as
                    // a caret parked after the final word still reads that word.
                    if (offset >= length && length > 0) offset = length - (length > 1 && char.IsLowSurrogate(text[length - 1]) && char.IsHighSurrogate(text[length - 2]) ? 2 : 1);
                    List<int> boundaries = unit switch
                    {
                        Unit.WordStart => WordBoundaries(text, starts: true),
                        Unit.WordEnd => WordBoundaries(text, starts: false),
                        Unit.SentenceStart => SentenceBoundaries(text, starts: true),
                        _ => SentenceBoundaries(text, starts: false),
                    };
                    int s = 0, e = length;
                    foreach (int b in boundaries)
                    {
                        if (b <= offset) s = b;
                        else { e = b; break; }
                    }
                    return (s, e);
                }
            }
        }

        /// <summary>
        /// Whether the character at <paramref name="i"/> belongs to a word: letters, digits, the marks
        /// that combine with them, connectors such as the underscore, and an apostrophe between two
        /// letters ("don't" is one word to a reader). Everything else -- spaces, punctuation -- is
        /// what words are separated by.
        /// </summary>
        private static bool IsWordAt(string text, int i)
        {
            if (char.IsLetterOrDigit(text, i)) return true;
            UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(text, i);
            if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark
                or UnicodeCategory.EnclosingMark or UnicodeCategory.ConnectorPunctuation) return true;
            char c = text[i];
            return (c == '\'' || c == '’') && i > 0 && i + 1 < text.Length &&
                   char.IsLetter(text[i - 1]) && char.IsLetter(text, i + 1);
        }

        /// <summary>Where words start (or end), in UTF-16. A word START unit runs from one start to the
        /// next, trailing space included; a word END unit from one end to the next, leading space included.</summary>
        private static List<int> WordBoundaries(string text, bool starts)
        {
            var boundaries = new List<int>();
            bool previous = false;
            for (int i = 0; i < text.Length; i += Step(text, i))
            {
                bool word = IsWordAt(text, i);
                if (starts ? word && !previous : !word && previous) boundaries.Add(i);
                previous = word;
            }
            if (!starts && previous) boundaries.Add(text.Length);
            return boundaries;
        }

        private static bool IsSentenceTerminator(char c) => c is '.' or '!' or '?' or '…' or '。';

        /// <summary>
        /// Where sentences start (or end): after a run of terminators -- with any closing quotes or
        /// brackets -- that is followed by space or the end. Approximate, as every sentence breaker
        /// without a dictionary is ("e.g. this" is one sentence and reads as two), but it is the
        /// granularity a reader asks for least, and a rough answer beats a whole paragraph.
        /// </summary>
        private static List<int> SentenceBoundaries(string text, bool starts)
        {
            var boundaries = new List<int>();
            for (int i = 0; i < text.Length; i++)
            {
                if (!IsSentenceTerminator(text[i])) continue;
                int j = i;
                while (j < text.Length && (IsSentenceTerminator(text[j]) || text[j] is '"' or '\'' or ')' or ']' or '”' or '’')) j++;
                if (j < text.Length && !char.IsWhiteSpace(text[j])) { i = j - 1; continue; }

                if (!starts) boundaries.Add(j);
                int k = j;
                while (k < text.Length && char.IsWhiteSpace(text[k])) k++;
                if (starts && k < text.Length) boundaries.Add(k);
                i = Math.Max(j, k) - 1;
            }
            if (!starts && (boundaries.Count == 0 || boundaries[^1] != text.Length) && text.Length > 0) boundaries.Add(text.Length);
            return boundaries;
        }

        /// <summary>The length of the line break that ends just before <paramref name="i"/>: 2 for \r\n, 1 for \n or \r, else 0.</summary>
        private static int BreakBefore(string text, int i)
        {
            if (i <= 0 || i > text.Length) return 0;
            char c = text[i - 1];
            if (c == '\n') return i >= 2 && text[i - 2] == '\r' ? 2 : 1;
            return c == '\r' ? 1 : 0;
        }

        /// <summary>
        /// The line holding a UTF-16 offset: its start, its end with the break, and its end without.
        /// Lines are the tree's -- layout's, wrapped where the text box wraps them, which is what a
        /// reader reading "the current line" means -- and the text's own breaks when the tree has none,
        /// or gives an answer that does not hold the offset.
        /// </summary>
        private static (int Start, int End, int ContentEnd) LineOf(string text, int offset, LineSource lines)
        {
            int length = text.Length;
            int start = -1, end = -1;
            if (lines != null && lines(offset, out int s, out int l))
            {
                s = Math.Clamp(s, 0, length);
                int e = Math.Clamp(s + Math.Max(l, 0), s, length);
                if (s <= offset && (offset < e || e == length)) { start = s; end = e; }
            }
            if (start < 0) (start, end) = HardLine(text, offset);
            int contentEnd = end - BreakBefore(text, end);
            return (start, end, Math.Max(contentEnd, start));
        }

        private static (int Start, int End) HardLine(string text, int offset)
        {
            int start = 0;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c != '\n' && c != '\r') continue;
                int next = c == '\r' && i + 1 < text.Length && text[i + 1] == '\n' ? i + 2 : i + 1;
                if (offset < next) return (start, next);
                start = next;
                i = next - 1;
            }
            return (start, text.Length);
        }

        /// <summary>
        /// A LINE_END unit: from the end of the line before (its break included, so the unit starts
        /// with it) to the end of this one's characters. An offset sitting in a line's break belongs to
        /// the unit that runs on to the next line's end, because the break is that unit's first character.
        /// </summary>
        private static (int Start, int End) LineEndUnit(string text, int offset, LineSource lines)
        {
            (int start, int end, int contentEnd) = LineOf(text, offset, lines);
            if (offset < contentEnd || contentEnd == end)
                return (start == 0 ? 0 : start - BreakBefore(text, start), contentEnd);

            if (end >= text.Length) return (contentEnd, text.Length);
            (_, _, int nextContentEnd) = LineOf(text, end, lines);
            return (contentEnd, Math.Max(nextContentEnd, contentEnd));
        }

        // ---- what changed --------------------------------------------------------------

        /// <summary>
        /// The text-changed signals that turn <paramref name="before"/> into <paramref name="after"/>:
        /// at most a delete and then an insert, at the first character that differs, in characters.
        ///
        /// WPF reports only THAT the text changed, and Orca needs WHAT: typing is echoed from the
        /// insert's text and a backspace from the delete's. The common prefix and suffix are kept;
        /// whatever is between them in the old text was deleted and whatever is between them in the
        /// new one inserted. Neither end may split a surrogate pair, or the character reported would
        /// be half of one. Replacing a selection is both, delete first, which is the order a client
        /// applying them to its own copy of the text needs.
        /// </summary>
        internal static List<(string Detail, int Offset, int Length, string Text)> Diff(string before, string after)
        {
            before ??= string.Empty;
            after ??= string.Empty;
            var edits = new List<(string, int, int, string)>(2);
            if (string.Equals(before, after, StringComparison.Ordinal)) return edits;

            int max = Math.Min(before.Length, after.Length);
            int prefix = 0;
            while (prefix < max && before[prefix] == after[prefix]) prefix++;
            if (prefix > 0 && char.IsHighSurrogate(before[prefix - 1])) prefix--;

            int suffix = 0;
            while (suffix < max - prefix && before[before.Length - 1 - suffix] == after[after.Length - 1 - suffix]) suffix++;
            if (suffix > 0 && char.IsLowSurrogate(before[before.Length - suffix])) suffix--;

            int offset = ToCodePoints(before, prefix);
            string deleted = before.Substring(prefix, before.Length - suffix - prefix);
            string inserted = after.Substring(prefix, after.Length - suffix - prefix);
            if (deleted.Length > 0) edits.Add(("delete", offset, CharacterCount(deleted), deleted));
            if (inserted.Length > 0) edits.Add(("insert", offset, CharacterCount(inserted), inserted));
            return edits;
        }

        /// <summary>
        /// A text's selection as AT-SPI sees it: a range, and which end of it the caret is at. WPF's
        /// selection has no direction, but AT-SPI's caret is a point, and after shift+right it is at
        /// the END of the selection -- reading the start would announce the caret going nowhere.
        /// </summary>
        internal readonly struct Selection
        {
            internal Selection(int start, int length, bool caretAtEnd)
            {
                Start = start;
                Length = Math.Max(length, 0);
                CaretAtEnd = caretAtEnd && Length > 0;
            }

            internal int Start { get; }
            internal int Length { get; }
            internal bool CaretAtEnd { get; }
            internal int Caret => CaretAtEnd ? Start + Length : Start;
        }

        /// <summary>
        /// The new selection, given the one before it, and what to tell the client: whether the caret
        /// moved (object:text-caret-moved) and whether the selected range did
        /// (object:text-selection-changed). The caret's end is inferred from how the range moved: if
        /// the start stayed put and the length changed, the user is extending at the end.
        /// </summary>
        internal static (Selection Now, bool CaretMoved, bool SelectionChanged) SelectionSignals(
            Selection? before, int start, int length)
        {
            length = Math.Max(length, 0);
            bool atEnd = before is Selection b && length > 0 && b.Start == start && (b.Length != length || b.CaretAtEnd);
            var now = new Selection(start, length, atEnd);

            bool caretMoved = before is not Selection old || old.Caret != now.Caret;
            bool rangeChanged = before is Selection prior
                ? (prior.Length > 0 || length > 0) && (prior.Start != start || prior.Length != length)
                : length > 0;
            return (now, caretMoved, rangeChanged);
        }
    }

    /// <summary>
    /// The Selection and Table interfaces' mapping onto the tree, free of D-Bus so it can be tested.
    ///
    /// AT-SPI addresses both by INDEX -- the n-th child, the n-th selected child, the cell at index i
    /// -- and the tree by id, so these are mostly the arithmetic between the two, with the rules for
    /// what selecting means on a container that allows one item or many.
    /// </summary>
    internal static class AtSpiPatterns
    {
        private const int Invalid = IAutomationTreeSource.InvalidNode;

        private static bool IsSelected(IAutomationTreeSource tree, int item)
            => (tree.GetState(item) & AccessibleState.Selected) != 0;

        /// <summary>The container's child at an index, or <see cref="IAutomationTreeSource.InvalidNode"/>.</summary>
        internal static int ChildAt(IAutomationTreeSource tree, int node, int index)
        {
            int[] children = tree.GetChildIds(node);
            return index >= 0 && index < children.Length ? children[index] : Invalid;
        }

        /// <summary>The n-th SELECTED child (GetSelectedChild counts the selection, not the children).</summary>
        internal static int SelectedChildAt(IAutomationTreeSource tree, int node, int selectedIndex)
        {
            int[] selected = tree.GetSelectedIds(node);
            return selectedIndex >= 0 && selectedIndex < selected.Length ? selected[selectedIndex] : Invalid;
        }

        internal static bool IsChildSelected(IAutomationTreeSource tree, int node, int childIndex)
        {
            int item = ChildAt(tree, node, childIndex);
            return item >= 0 && IsSelected(tree, item);
        }

        /// <summary>
        /// SelectChild ADDS to the selection, in AT-SPI's terms. On a container that allows many
        /// that is a ToggleSelection of an unselected item (a ctrl+click); with nothing selected yet,
        /// or on a container that allows one, it is a plain Select, which replaces.
        /// </summary>
        internal static bool SelectChild(IAutomationTreeSource tree, int node, int childIndex)
        {
            int item = ChildAt(tree, node, childIndex);
            if (item < 0) return false;
            if (IsSelected(tree, item)) return true;

            bool multiple = (tree.GetState(node) & AccessibleState.MultiSelectable) != 0;
            if (multiple && tree.GetSelectedIds(node).Length > 0 && tree.SupportsAction(item, AccessibleAction.ToggleSelection))
                return tree.DoAction(item, AccessibleAction.ToggleSelection);
            return tree.DoAction(item, AccessibleAction.Select);
        }

        /// <summary>Takes one item out of the selection. An item that is not selected already is
        /// not; one that cannot be toggled off (the only item of a single selection) stays.</summary>
        internal static bool Deselect(IAutomationTreeSource tree, int item)
        {
            if (item < 0) return false;
            if (!IsSelected(tree, item)) return true;
            return tree.SupportsAction(item, AccessibleAction.ToggleSelection) &&
                   tree.DoAction(item, AccessibleAction.ToggleSelection);
        }

        internal static bool DeselectChild(IAutomationTreeSource tree, int node, int childIndex)
            => Deselect(tree, ChildAt(tree, node, childIndex));

        internal static bool DeselectSelectedChild(IAutomationTreeSource tree, int node, int selectedIndex)
            => Deselect(tree, SelectedChildAt(tree, node, selectedIndex));

        /// <summary>Selects every selectable child: only a container that allows many can.</summary>
        internal static bool SelectAll(IAutomationTreeSource tree, int node)
        {
            if ((tree.GetState(node) & AccessibleState.MultiSelectable) == 0) return false;
            bool ok = true;
            foreach (int item in tree.GetChildIds(node))
            {
                if ((tree.GetState(item) & AccessibleState.Selectable) == 0 || IsSelected(tree, item)) continue;
                bool any = tree.GetSelectedIds(node).Length > 0;
                AccessibleAction action = any && tree.SupportsAction(item, AccessibleAction.ToggleSelection)
                    ? AccessibleAction.ToggleSelection : AccessibleAction.Select;
                ok &= tree.DoAction(item, action);
            }
            return ok;
        }

        internal static bool ClearSelection(IAutomationTreeSource tree, int node)
        {
            bool ok = true;
            foreach (int item in tree.GetSelectedIds(node)) ok &= Deselect(tree, item);
            return ok;
        }

        // ---- tables --------------------------------------------------------------------

        /// <summary>
        /// A cell's index in the table: row-major, row * columns + column, as ATK's tables number
        /// them. Not the cell's index among the table's children -- a WPF data grid's children are its
        /// rows and header, not its cells -- but a number the client only ever hands back to
        /// GetRowAtIndex and GetColumnAtIndex, so it needs to be consistent rather than structural.
        /// </summary>
        internal static int CellIndex(int rows, int columns, int row, int column)
            => row >= 0 && column >= 0 && row < rows && column < columns ? row * columns + column : -1;

        /// <summary>The inverse of <see cref="CellIndex"/>: -1 for both for an index outside the table.</summary>
        internal static (int Row, int Column) RowAndColumnOf(int rows, int columns, int index)
            => columns > 0 && index >= 0 && index < rows * columns ? (index / columns, index % columns) : (-1, -1);

        /// <summary>
        /// The rows with something selected in them. The tree reports selected ITEMS, which in a data
        /// grid are rows (whose cells carry the row number) and in a cell-selecting grid are cells
        /// (which carry it themselves); either way the row is the grid item's.
        /// </summary>
        internal static int[] SelectedRows(IAutomationTreeSource tree, int table)
        {
            var rows = new SortedSet<int>();
            foreach (int item in tree.GetSelectedIds(table))
            {
                int row = RowOf(tree, item);
                if (row >= 0) rows.Add(row);
            }
            var result = new int[rows.Count];
            rows.CopyTo(result);
            return result;
        }

        private static int RowOf(IAutomationTreeSource tree, int item)
        {
            if (tree.TryGetGridItem(item, out int row, out _, out _, out _, out _)) return row;
            foreach (int cell in tree.GetChildIds(item))
            {
                if (tree.TryGetGridItem(cell, out row, out _, out _, out _, out _)) return row;
            }
            return -1;
        }

        internal static bool IsCellSelected(IAutomationTreeSource tree, int table, int row, int column)
        {
            int cell = tree.GetGridCell(table, row, column);
            if (cell >= 0 && IsSelected(tree, cell)) return true;
            return row >= 0 && Array.IndexOf(SelectedRows(tree, table), row) >= 0;
        }

        /// <summary>A table's header for a column (or a row), or <see cref="IAutomationTreeSource.InvalidNode"/>.</summary>
        internal static int ColumnHeader(IAutomationTreeSource tree, int table, int column)
        {
            int[] headers = tree.GetColumnHeaderIds(table);
            return column >= 0 && column < headers.Length ? headers[column] : Invalid;
        }

        internal static int RowHeader(IAutomationTreeSource tree, int table, int row)
        {
            int[] headers = tree.GetRowHeaderIds(table);
            return row >= 0 && row < headers.Length ? headers[row] : Invalid;
        }

        /// <summary>A cell's span in rows and columns, 1 by 1 for anything the tree does not place.</summary>
        internal static (int RowSpan, int ColumnSpan) SpanAt(IAutomationTreeSource tree, int table, int row, int column)
        {
            int cell = tree.GetGridCell(table, row, column);
            if (cell >= 0 && tree.TryGetGridItem(cell, out _, out _, out int rowSpan, out int columnSpan, out _))
                return (Math.Max(rowSpan, 1), Math.Max(columnSpan, 1));
            return (cell >= 0 ? 1 : 0, cell >= 0 ? 1 : 0);
        }

        /// <summary>The header cells over a table cell: the column's headers and the row's, from its grid.</summary>
        internal static int[] HeaderCellsOf(IAutomationTreeSource tree, int cell, bool columns)
        {
            if (!tree.TryGetGridItem(cell, out int row, out int column, out int rowSpan, out int columnSpan, out int grid) || grid < 0)
                return Array.Empty<int>();
            var headers = new List<int>();
            int first = columns ? column : row, span = Math.Max(columns ? columnSpan : rowSpan, 1);
            for (int i = first; i < first + span; i++)
            {
                int header = columns ? ColumnHeader(tree, grid, i) : RowHeader(tree, grid, i);
                if (header >= 0) headers.Add(header);
            }
            return headers.ToArray();
        }
    }
}
