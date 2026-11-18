// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The Android accessibility backend's WindowsBase half.
//
// Android's contract for "one View that draws many things" is AccessibilityNodeProvider: the view
// hands TalkBack a tree of VIRTUAL nodes addressed by integer id. That maps almost exactly onto the
// automation tree, so this file is mostly a translation of ids and an action vocabulary.
//
// The split is the same one AndroidWindow documents and for the same reason: an
// AccessibilityNodeProvider is a Java class, and WindowsBase cannot reference Mono.Android. So the
// provider itself lives in AndroidHost.cs -- the payload each app head compiles in -- and everything
// here is PUBLIC, because that payload sees only WindowsBase's public surface. (Everything else in
// this accessibility stack is internal; this is the one place it cannot be.)
//
// The head asks in primitives and gets primitives back. Node data comes over as one compact JSON
// string per node rather than a dozen property calls, because each of those is a JNI crossing.
//
// What a node carries is what Android's own widgets put in their AccessibilityNodeInfo, because
// TalkBack's behaviour is keyed off exactly those fields: an EditText's text, selection and
// movement granularities are what its reading-by-character/word/line menu works from; a ListView's
// CollectionInfo and its items' CollectionItemInfo are what "item 3 of 12" comes from; a
// ScrollView's scroll actions are what the two-finger swipe performs. And the events are the ones
// those widgets send -- TYPE_VIEW_TEXT_CHANGED with the before-text, TYPE_VIEW_TEXT_SELECTION_CHANGED
// with the range -- because TalkBack speaks the change from the event, not from re-reading the node.
//

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace MS.Internal.Interop
{
    /// <summary>
    /// What the app head must provide for accessibility. A SIBLING of IAndroidHost rather than more
    /// members on it, so that a head which does not care (the WgpuInterop spike) is not forced to
    /// implement them.
    /// </summary>
    public interface IAndroidAccessibilityHost
    {
        /// <summary>
        /// Tell the framework a virtual node changed, so TalkBack re-reads it. Pass -1 for the whole
        /// tree. Maps to View.invalidateVirtualView / sendAccessibilityEvent on the head side.
        /// </summary>
        void InvalidateAccessibilityNode(IntPtr handle, int nodeId);
    }

    /// <summary>
    /// The events TalkBack speaks from rather than re-reading the node: text edits, caret moves,
    /// scrolling, selection, and a window's pane state. A sibling of
    /// <see cref="IAndroidAccessibilityHost"/> rather than a member on it, for the reason that one
    /// is a sibling of IAndroidHost: a head written before these existed keeps compiling, and gets
    /// the content-changed invalidation it always got.
    /// </summary>
    public interface IAndroidAccessibilityEventHost
    {
        /// <summary>
        /// Send one accessibility event whose source is virtual node <paramref name="nodeId"/>. The
        /// event is one flat JSON object, in the vocabulary <see cref="AndroidAccessibility"/>
        /// documents beside its EventJson builders.
        /// </summary>
        void SendAccessibilityEvent(IntPtr handle, int nodeId, string eventJson);
    }

    /// <summary>
    /// The bridge the head's AccessibilityNodeProvider calls into. Public for the head payload; the
    /// tree behind it is the same internal one every other platform uses.
    /// </summary>
    // No [SupportedOSPlatform] here, matching AndroidWindow/IAndroidHost next door: the public
    // WindowsBase surface does not carry platform attributes, and adding one fails ApiCompat against
    // the hand-written reference assembly.
    public static class AndroidAccessibility
    {
        /// <summary>Set by the head alongside AndroidWindow.Host.</summary>
        public static IAndroidAccessibilityHost Host { get; set; }

        /// <summary>The node id meaning "the view itself", which is Android's HOST_VIEW_ID.</summary>
        public const int HostViewId = -1;

        /// <summary>No such node. Matches the value the other backends use.</summary>
        public const int InvalidNode = -1;

        // The "act" bits of the node JSON: the actions a node advertises. The head maps each to
        // Android's AccessibilityAction where the constants are visible.
        internal const int ActClick = 1, ActExpand = 2, ActCollapse = 4,
            ActScrollForward = 8, ActScrollBackward = 16,
            ActScrollUp = 32, ActScrollDown = 64, ActScrollLeft = 128, ActScrollRight = 256,
            ActShowOnScreen = 512, ActSetSelection = 1024, ActTraverse = 2048, ActSetText = 4096,
            ActDismiss = 8192;

        // The PerformAction kinds. 0-4 predate the rest and keep their numbers.
        internal const int KindClick = 0, KindFocus = 1, KindExpand = 2, KindCollapse = 3, KindSetValue = 4,
            KindSetSelection = 5, KindScrollForward = 6, KindScrollBackward = 7,
            KindScrollUp = 8, KindScrollDown = 9, KindScrollLeft = 10, KindScrollRight = 11,
            KindShowOnScreen = 12, KindDismiss = 13;

        /// <summary>
        /// The movement granularities, as bits. They are Android's own MOVEMENT_GRANULARITY_* values
        /// on purpose, so the head can hand the "gran" field to setMovementGranularities and the
        /// argument of a traversal action back here without a table on either side.
        /// </summary>
        public const int GranularityCharacter = 1, GranularityWord = 2, GranularityLine = 4,
            GranularityParagraph = 8, GranularityPage = 16;

        // The "ev" values of an event JSON (SendAccessibilityEvent).
        internal const int EvTextChanged = 1, EvTextSelectionChanged = 2, EvScrolled = 3, EvSelected = 4, EvWindow = 5;

        /// <summary>
        /// Called by the head the first time Android asks for a node provider -- which it only does
        /// when an accessibility service is actually bound, so this is the "a screen reader exists"
        /// signal, exactly like the first NSAccessibility message on macOS.
        /// </summary>
        public static void Attach()
        {
            if (s_attached) return;
            s_attached = true;

            AutomationTree.RequestActivation();
            AutomationTree.Changed += OnTreeChanged;
        }

        private static bool s_attached;

        private static IAutomationTreeSource Tree => AutomationTree.Current;

        // What the events are diffed against: the text of a node when it was last read or changed,
        // the scroll position it last reported, and each window's pane state. Bounded, because ids
        // come and go with the elements behind them and nothing tells this file which are gone.
        private static readonly Dictionary<int, string> s_texts = new();
        private static readonly Dictionary<int, (long X, long Y)> s_scrolls = new();
        private static readonly Dictionary<IntPtr, (bool Modal, string Pane)> s_windows = new();

        // The accessibility cursor of each text node TalkBack has traversed: where the selection was
        // put (anchor and moving end), and what the tree reported back for it. Android keeps one of
        // these per TextView beside the real selection, because a read-only text has no caret to
        // move but must still be read word by word from where the reading stopped.
        private static readonly Dictionary<int, (int Anchor, int Active, int TreeStart, int TreeLength)> s_cursors = new();

        private const int MaxRemembered = 512;

        private static void Remember<TKey, TValue>(Dictionary<TKey, TValue> map, TKey key, TValue value)
        {
            if (map.Count >= MaxRemembered && !map.ContainsKey(key)) map.Clear();
            map[key] = value;
        }

        private static void OnTreeChanged(AutomationChange change)
        {
            try
            {
                IAutomationTreeSource tree = Tree;
                IAndroidAccessibilityHost host = Host;
                if (host == null) return;

                if (tree != null && host is IAndroidAccessibilityEventHost events)
                {
                    (int source, string json) = EventFor(tree, change);
                    if (json.Length != 0) events.SendAccessibilityEvent(change.WindowHandle, source, json);

                    // A window's pane title and modality are not changes the tree reports on their
                    // own; any change in the window is the occasion to look.
                    string window = WindowEventJson(tree, change.WindowHandle, out int root);
                    if (window.Length != 0) events.SendAccessibilityEvent(change.WindowHandle, root, window);

                    // The specific event already made TalkBack refresh the node it is about.
                    if (json.Length != 0 && change.Kind != AutomationChangeKind.SelectionContainerChanged) return;
                }

                host.InvalidateAccessibilityNode(
                    change.WindowHandle,
                    change.Kind == AutomationChangeKind.ChildrenChanged ? InvalidNode : change.NodeId);
            }
            catch (InvalidOperationException) { }   // the activity went away mid-notification
        }

        /// <summary>The root virtual node for a window, or <see cref="InvalidNode"/>.</summary>
        public static int GetRootId(IntPtr handle) => Tree?.GetRootId(handle) ?? InvalidNode;

        /// <summary>Children of a node, in tree order. Never null.</summary>
        public static int[] GetChildIds(int nodeId) => Tree?.GetChildIds(nodeId) ?? Array.Empty<int>();

        /// <summary>The parent node, or <see cref="InvalidNode"/> at the root.</summary>
        public static int GetParentId(int nodeId) => Tree?.GetParentId(nodeId) ?? InvalidNode;

        /// <summary>The node under a point, in screen device pixels.</summary>
        public static int HitTest(IntPtr handle, int screenX, int screenY)
            => Tree?.HitTest(handle, screenX, screenY) ?? InvalidNode;

        /// <summary>The node with keyboard focus.</summary>
        public static int GetFocusedId(IntPtr handle) => Tree?.GetFocusedId(handle) ?? InvalidNode;

        /// <summary>
        /// Everything the head needs to populate an AccessibilityNodeInfo, as one JSON object:
        ///   cls name desc x y w h en fo sel ck chk   class, labels, bounds and the basic states;
        ///   val vmin vmax                             a range (RangeInfo);
        ///   text                                      the value, or a Text pattern's whole text;
        ///   ed ml pw ss se gran                       editable, multi-line, password, the text
        ///                                             selection, the movement granularities;
        ///   coll cr cc chi csm                        CollectionInfo: rows, columns, hierarchical,
        ///                                             selection mode (0 none, 1 single, 2 multiple);
        ///   item ir ic irs ics ih                     CollectionItemInfo: row, column, spans, heading;
        ///   scr                                       scrollable;
        ///   pane modal                                a window's pane title, and whether it is modal;
        ///   act                                       the Act* bits.
        /// Empty string when the node is gone -- the head then returns a null NodeInfo, which is how
        /// Android is told a virtual node no longer exists.
        ///
        /// One string rather than a dozen getters because every one of those would be a JNI
        /// transition, and TalkBack asks for whole subtrees at a time.
        /// </summary>
        public static string GetNodeJson(int nodeId)
        {
            IAutomationTreeSource tree = Tree;
            if (tree == null) return string.Empty;

            int role = tree.GetRole(nodeId);
            if (role < 0) return string.Empty;

            AccessibleState state = tree.GetState(nodeId);
            var json = new StringBuilder(256);

            json.Append("{\"cls\":\"").Append(AndroidClassNames.For(role)).Append('"')
                .Append(",\"name\":\"").Append(Escape(tree.GetName(nodeId))).Append('"')
                .Append(",\"desc\":\"").Append(Escape(tree.GetHelpText(nodeId))).Append('"');

            if (tree.TryGetBounds(nodeId, out double x, out double y, out double w, out double h))
            {
                json.Append(",\"x\":").Append((long)x).Append(",\"y\":").Append((long)y)
                    .Append(",\"w\":").Append((long)w).Append(",\"h\":").Append((long)h);
            }

            json.Append(",\"en\":").Append((state & AccessibleState.Enabled) != 0 ? 1 : 0)
                .Append(",\"fo\":").Append((state & AccessibleState.Focusable) != 0 ? 1 : 0)
                .Append(",\"sel\":").Append((state & AccessibleState.Selected) != 0 ? 1 : 0);

            // Checkable is a separate bit from checked on Android, and TalkBack announces the two
            // differently ("tick box, ticked" vs "tick box").
            bool checkable = (state & (AccessibleState.Checked | AccessibleState.Indeterminate)) != 0
                             || tree.SupportsAction(nodeId, AccessibleAction.Toggle);
            json.Append(",\"ck\":").Append(checkable ? 1 : 0)
                .Append(",\"chk\":").Append((state & AccessibleState.Checked) != 0 ? 1 : 0);

            int actions = 0;
            if (tree.TryGetRange(nodeId, out double value, out double min, out double max))
            {
                json.Append(",\"val\":").Append(value.ToString("0.##", CultureInfo.InvariantCulture))
                    .Append(",\"vmin\":").Append(min.ToString("0.##", CultureInfo.InvariantCulture))
                    .Append(",\"vmax\":").Append(max.ToString("0.##", CultureInfo.InvariantCulture));
            }
            else
            {
                actions |= AppendText(tree, nodeId, role, state, json);
            }

            AppendCollection(tree, nodeId, role, state, json);
            AppendCollectionItem(tree, nodeId, role, state, json);
            actions |= AppendScroll(tree, nodeId, state, json);

            if ((state & AccessibleState.Window) != 0)
            {
                json.Append(",\"pane\":\"").Append(Escape(tree.GetName(nodeId))).Append('"');
                if ((state & AccessibleState.Modal) != 0)
                {
                    // An AlertDialog is dismissed by TalkBack's back gesture through ACTION_DISMISS;
                    // a modal WPF window is closed the same way, as Escape or the close box would.
                    json.Append(",\"modal\":1");
                    actions |= ActDismiss;
                }
            }

            // The actions to advertise. TalkBack only offers what the node claims.
            if (tree.SupportsAction(nodeId, AccessibleAction.Invoke) ||
                tree.SupportsAction(nodeId, AccessibleAction.Toggle) ||
                tree.SupportsAction(nodeId, AccessibleAction.Select)) actions |= ActClick;
            if (tree.SupportsAction(nodeId, AccessibleAction.Expand)) actions |= ActExpand;
            if (tree.SupportsAction(nodeId, AccessibleAction.Collapse)) actions |= ActCollapse;
            if ((state & AccessibleState.ScrollItem) != 0) actions |= ActShowOnScreen;
            json.Append(",\"act\":").Append(actions);

            json.Append('}');
            return json.ToString();
        }

        /// <summary>
        /// The text half of a node: a Text pattern's whole text where there is one (a TextBox's
        /// value is the same text, but a RichTextBox has no value at all), the value otherwise; and
        /// for text, what an EditText carries beside it. Returns the actions that come with it.
        /// </summary>
        private static int AppendText(IAutomationTreeSource tree, int nodeId, int role, AccessibleState state, StringBuilder json)
        {
            bool password = (state & AccessibleState.Password) != 0;
            string text = null;
            bool hasText = (state & AccessibleState.HasText) != 0 && tree.TryGetText(nodeId, out text);
            text = (hasText ? text : tree.GetValue(nodeId)) ?? string.Empty;

            // A password's characters are never handed out: TextView gives accessibility the
            // transformed text, one bullet per character, and so does this. Its length still tells
            // TalkBack how many characters have been typed.
            if (text.Length != 0) json.Append(",\"text\":\"").Append(Escape(password ? Mask(text) : text)).Append('"');

            bool editable = IsEditable(role, state);
            int actions = 0;
            if (editable) { json.Append(",\"ed\":1"); actions |= ActSetText; }
            if (password) json.Append(",\"pw\":1");
            if (!hasText) return actions;

            if (role == 30 || text.IndexOfAny(s_lineBreaks) >= 0) json.Append(",\"ml\":1");

            // The baseline the first text-changed event is diffed against, as AT-SPI's is: the text
            // TalkBack was last shown.
            if (!s_texts.ContainsKey(nodeId)) Remember(s_texts, nodeId, text);

            if (tree.TryGetTextSelection(nodeId, out int start, out int length))
            {
                start = Math.Clamp(start, 0, text.Length);
                length = Math.Clamp(length, 0, text.Length - start);
                json.Append(",\"ss\":").Append(start).Append(",\"se\":").Append(start + length);
            }
            actions |= ActSetSelection;

            if (text.Length != 0 && !password)
            {
                int granularities = GranularitiesFor(tree, nodeId);
                json.Append(",\"gran\":").Append(granularities);
                actions |= ActTraverse;
            }
            return actions;
        }

        private static readonly char[] s_lineBreaks = { '\n', '\r', '\u2028', '\u2029' };

        /// <summary>
        /// Editable as an EditText is: something the user can type into. Built on the Value pattern,
        /// as ACTION_SET_TEXT is, so a read-only box or a text without one (a TextBlock) is not.
        /// </summary>
        internal static bool IsEditable(int role, AccessibleState state)
            => (state & AccessibleState.HasValue) != 0 && (state & AccessibleState.ReadOnly) == 0
               && ((state & (AccessibleState.HasText | AccessibleState.Password)) != 0 || role == 4);

        /// <summary>
        /// Character, word and paragraph always, as TextView offers them; line only where the tree
        /// knows its lines (TryGetTextLine), because a line split out of the text's line breaks is
        /// not the line on screen once it wraps. Page needs a layout's pages, which the tree has not.
        /// </summary>
        internal static int GranularitiesFor(IAutomationTreeSource tree, int nodeId)
        {
            int granularities = GranularityCharacter | GranularityWord | GranularityParagraph;
            if (tree.TryGetTextLine(nodeId, 0, out _, out _, out _)) granularities |= GranularityLine;
            return granularities;
        }

        private static string Mask(string text) => new string('\u2022', text.Length);

        /// <summary>
        /// CollectionInfo, as a ListView or GridView sets it: a grid's own rows and columns; a list's
        /// items as rows of one column; a tree's top level the same, but hierarchical.
        /// </summary>
        private static void AppendCollection(IAutomationTreeSource tree, int nodeId, int role, AccessibleState state, StringBuilder json)
        {
            bool container = (state & AccessibleState.SelectionContainer) != 0;
            int rows, columns;
            if ((state & AccessibleState.Grid) != 0 && tree.TryGetGrid(nodeId, out rows, out columns)) { }
            else if (container) { rows = tree.GetChildIds(nodeId).Length; columns = 1; }
            else return;

            int mode = !container ? 0 : (state & AccessibleState.MultiSelectable) != 0 ? 2 : 1;
            json.Append(",\"coll\":1,\"cr\":").Append(Math.Max(rows, 0))
                .Append(",\"cc\":").Append(Math.Max(columns, 0))
                .Append(",\"chi\":").Append(role == 23 ? 1 : 0)
                .Append(",\"csm\":").Append(mode);
        }

        /// <summary>
        /// CollectionItemInfo: a cell's own place in its grid; a row of a grid (a DataGrid's row,
        /// whose children are the cells) the row of its first cell across every column; a header
        /// its column, as a heading; an item of a list its index in it.
        /// </summary>
        private static void AppendCollectionItem(IAutomationTreeSource tree, int nodeId, int role, AccessibleState state, StringBuilder json)
        {
            if (!TryGetItemPlace(tree, nodeId, role, state, out int row, out int column, out int rowSpan, out int columnSpan, out bool heading))
                return;
            json.Append(",\"item\":1,\"ir\":").Append(row).Append(",\"ic\":").Append(column)
                .Append(",\"irs\":").Append(rowSpan).Append(",\"ics\":").Append(columnSpan)
                .Append(",\"ih\":").Append(heading ? 1 : 0);
        }

        internal static bool TryGetItemPlace(IAutomationTreeSource tree, int nodeId, int role, AccessibleState state,
            out int row, out int column, out int rowSpan, out int columnSpan, out bool heading)
        {
            row = column = 0; rowSpan = columnSpan = 1; heading = false;

            if ((state & AccessibleState.GridItem) != 0
                && tree.TryGetGridItem(nodeId, out row, out column, out rowSpan, out columnSpan, out int grid))
            {
                rowSpan = Math.Max(rowSpan, 1); columnSpan = Math.Max(columnSpan, 1);
                heading = role == 35 || (grid != InvalidNode
                    && (Array.IndexOf(tree.GetColumnHeaderIds(grid), nodeId) >= 0 || Array.IndexOf(tree.GetRowHeaderIds(grid), nodeId) >= 0));
                return true;
            }

            int parent = tree.GetParentId(nodeId);
            if (parent == InvalidNode) return false;
            AccessibleState parentState = tree.GetState(parent);

            // A column header: in a table's header list, which is the grid's or its header row's.
            for (int g = parent, hops = 0; g != InvalidNode && hops < 2; g = tree.GetParentId(g), hops++)
            {
                if ((tree.GetState(g) & AccessibleState.Table) == 0) continue;
                int index = Array.IndexOf(tree.GetColumnHeaderIds(g), nodeId);
                if (index >= 0) { column = index; heading = true; return true; }
                index = Array.IndexOf(tree.GetRowHeaderIds(g), nodeId);
                if (index >= 0) { row = index; heading = true; return true; }
            }

            // A row of a grid: the cells are its children, and they know which row it is.
            if ((parentState & AccessibleState.Grid) != 0)
            {
                foreach (int child in tree.GetChildIds(nodeId))
                {
                    if ((tree.GetState(child) & AccessibleState.GridItem) == 0) continue;
                    if (!tree.TryGetGridItem(child, out row, out _, out _, out _, out _)) continue;
                    column = 0;
                    columnSpan = tree.TryGetGrid(parent, out _, out int columns) ? Math.Max(columns, 1) : 1;
                    return true;
                }
            }

            if ((parentState & AccessibleState.SelectionContainer) != 0)
            {
                int index = Array.IndexOf(tree.GetChildIds(parent), nodeId);
                if (index < 0) return false;
                row = index;
                return true;
            }
            return false;
        }

        /// <summary>
        /// A scrolling node is scrollable, and offers the scroll actions for the directions it can
        /// still move in -- a ScrollView at its top does not offer ACTION_SCROLL_BACKWARD, and
        /// TalkBack's "end of list" earcon comes from that.
        /// </summary>
        private static int AppendScroll(IAutomationTreeSource tree, int nodeId, AccessibleState state, StringBuilder json)
        {
            if ((state & AccessibleState.Scrollable) == 0) return 0;
            if (!tree.TryGetScroll(nodeId, out double hp, out double vp, out _, out _, out bool hs, out bool vs)) return 0;
            if (!hs && !vs) return 0;

            json.Append(",\"scr\":1");
            return ScrollActionsFor(hs, hp, vs, vp);
        }

        internal static int ScrollActionsFor(bool horizontallyScrollable, double horizontalPercent, bool verticallyScrollable, double verticalPercent)
        {
            bool up = verticallyScrollable && verticalPercent > 0.01;
            bool down = verticallyScrollable && verticalPercent < 99.99;
            bool left = horizontallyScrollable && horizontalPercent > 0.01;
            bool right = horizontallyScrollable && horizontalPercent < 99.99;

            int actions = 0;
            if (down || right) actions |= ActScrollForward;
            if (up || left) actions |= ActScrollBackward;
            if (up) actions |= ActScrollUp;
            if (down) actions |= ActScrollDown;
            if (left) actions |= ActScrollLeft;
            if (right) actions |= ActScrollRight;
            return actions;
        }

        /// <summary>
        /// The ScrollAmount pair (horizontal, vertical) for a scroll action. Forward and backward are
        /// a page, as ScrollView's pageScroll is, along the axis that can still move -- vertical
        /// first, as a list is read. (-1, -1) when the node cannot move that way.
        /// </summary>
        internal static (int Horizontal, int Vertical) ScrollAmountsFor(int kind,
            bool horizontallyScrollable, double horizontalPercent, bool verticallyScrollable, double verticalPercent)
        {
            const int LargeDecrement = 0, NoAmount = 2, LargeIncrement = 3;
            switch (kind)
            {
                case KindScrollForward:
                    if (verticallyScrollable && verticalPercent < 99.99) return (NoAmount, LargeIncrement);
                    if (horizontallyScrollable && horizontalPercent < 99.99) return (LargeIncrement, NoAmount);
                    break;
                case KindScrollBackward:
                    if (verticallyScrollable && verticalPercent > 0.01) return (NoAmount, LargeDecrement);
                    if (horizontallyScrollable && horizontalPercent > 0.01) return (LargeDecrement, NoAmount);
                    break;
                case KindScrollUp: if (verticallyScrollable) return (NoAmount, LargeDecrement); break;
                case KindScrollDown: if (verticallyScrollable) return (NoAmount, LargeIncrement); break;
                case KindScrollLeft: if (horizontallyScrollable) return (LargeDecrement, NoAmount); break;
                case KindScrollRight: if (horizontallyScrollable) return (LargeIncrement, NoAmount); break;
            }
            return (-1, -1);
        }

        /// <summary>
        /// Performs an Android accessibility action. The kinds are this file's own small vocabulary,
        /// not Android's constants, so the head does the mapping where the constants are visible.
        ///   0 click, 1 focus, 2 expand, 3 collapse, 4 set-value (ACTION_SET_TEXT; the argument is
        ///   the text), 5 set-selection (the argument is "start,end", either -1 when absent),
        ///   6 scroll forward, 7 scroll backward, 8 up, 9 down, 10 left, 11 right,
        ///   12 show on screen, 13 dismiss (close a modal window).
        /// Traversal by granularity returns a range as well, so it is <see cref="MoveAtGranularity"/>.
        /// </summary>
        public static bool PerformAction(int nodeId, int kind, string argument)
        {
            IAutomationTreeSource tree = Tree;
            if (tree == null) return false;

            switch (kind)
            {
                case KindClick:
                    foreach (AccessibleAction action in s_clickOrder)
                    {
                        if (tree.SupportsAction(nodeId, action)) return tree.DoAction(nodeId, action);
                    }
                    return false;
                case KindFocus: return tree.SetFocus(nodeId);
                case KindExpand: return tree.DoAction(nodeId, AccessibleAction.Expand);
                case KindCollapse: return tree.DoAction(nodeId, AccessibleAction.Collapse);
                case KindSetValue: return tree.SetValue(nodeId, argument ?? string.Empty);
                case KindSetSelection: return SetSelection(tree, nodeId, argument);
                case KindScrollForward:
                case KindScrollBackward:
                case KindScrollUp:
                case KindScrollDown:
                case KindScrollLeft:
                case KindScrollRight:
                {
                    if (!tree.TryGetScroll(nodeId, out double hp, out double vp, out _, out _, out bool hs, out bool vs)) return false;
                    (int horizontal, int vertical) = ScrollAmountsFor(kind, hs, hp, vs, vp);
                    return horizontal >= 0 && tree.Scroll(nodeId, horizontal, vertical);
                }
                case KindShowOnScreen: return tree.ScrollIntoView(nodeId);
                case KindDismiss:
                    return (tree.GetState(nodeId) & (AccessibleState.Window | AccessibleState.Modal)) == (AccessibleState.Window | AccessibleState.Modal)
                        && tree.CloseWindow(nodeId);
                default: return false;
            }
        }

        private static readonly AccessibleAction[] s_clickOrder =
        {
            AccessibleAction.Toggle, AccessibleAction.Invoke, AccessibleAction.Select,
        };

        /// <summary>
        /// ACTION_SET_SELECTION. Android's two ints may come in either order (the end is where the
        /// caret goes); both absent clears the selection to the caret at its start, as TextView does.
        /// </summary>
        private static bool SetSelection(IAutomationTreeSource tree, int nodeId, string argument)
        {
            if (!tree.TryGetText(nodeId, out string text)) return false;
            text ??= string.Empty;
            (int anchor, int active) = ParseSelectionArgument(argument);
            if (anchor < 0 && active < 0)
            {
                if (!tree.TryGetTextSelection(nodeId, out int start, out _)) return false;
                anchor = active = start;
            }
            else if (anchor < 0) anchor = active;
            else if (active < 0) active = anchor;

            anchor = Math.Clamp(anchor, 0, text.Length);
            active = Math.Clamp(active, 0, text.Length);
            return Select(tree, nodeId, anchor, active);
        }

        internal static (int Anchor, int Active) ParseSelectionArgument(string argument)
        {
            if (string.IsNullOrEmpty(argument)) return (-1, -1);
            int comma = argument.IndexOf(',');
            if (comma < 0) return (ParseInt(argument), -1);
            return (ParseInt(argument.Substring(0, comma)), ParseInt(argument.Substring(comma + 1)));

            static int ParseInt(string s) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : -1;
        }

        // Puts the selection at (anchor, active) and remembers it as the accessibility cursor.
        private static bool Select(IAutomationTreeSource tree, int nodeId, int anchor, int active)
        {
            int start = Math.Min(anchor, active), length = Math.Abs(active - anchor);
            bool set = tree.SetTextSelection(nodeId, start, length);
            int treeStart = -1, treeLength = -1;
            if (tree.TryGetTextSelection(nodeId, out int s, out int l)) { treeStart = s; treeLength = l; }
            Remember(s_cursors, nodeId, (anchor, active, treeStart, treeLength));
            return set || (tree.GetState(nodeId) & AccessibleState.HasText) != 0;
        }

        /// <summary>
        /// ACTION_NEXT_AT_MOVEMENT_GRANULARITY and ACTION_PREVIOUS_AT_MOVEMENT_GRANULARITY, as
        /// View.traverseAtGranularity performs them: the next (or previous) character, word, line or
        /// paragraph from the accessibility cursor, which then moves to its far end -- or, extending,
        /// stretches the selection there. The segment comes back for the head's
        /// TYPE_VIEW_TEXT_TRAVERSED_AT_MOVEMENT_GRANULARITY, which is what TalkBack speaks.
        /// </summary>
        public static bool MoveAtGranularity(int nodeId, int granularity, bool forward, bool extendSelection,
                                             out int segmentStart, out int segmentEnd)
        {
            segmentStart = segmentEnd = -1;
            IAutomationTreeSource tree = Tree;
            if (tree == null || !tree.TryGetText(nodeId, out string text) || string.IsNullOrEmpty(text)) return false;
            if ((tree.GetState(nodeId) & AccessibleState.Password) != 0) return false;

            (int anchor, int active) = CursorOf(tree, nodeId, text.Length);
            int current = active >= 0 ? active : forward ? 0 : text.Length;

            TextLines lines = (int offset, out int start, out int length) => tree.TryGetTextLine(nodeId, offset, out start, out length, out _);
            if (!FindSegment(text, granularity, forward, current, lines, out segmentStart, out segmentEnd)) return false;

            int newActive = forward ? segmentEnd : segmentStart;
            int newAnchor = extendSelection ? (anchor >= 0 ? anchor : forward ? segmentStart : segmentEnd) : newActive;
            Select(tree, nodeId, newAnchor, newActive);
            return true;
        }

        // The cursor TalkBack is reading from: the one this file last put, unless the selection has
        // moved since (the user typed, or tapped elsewhere), in which case the selection's own.
        private static (int Anchor, int Active) CursorOf(IAutomationTreeSource tree, int nodeId, int textLength)
        {
            bool known = s_cursors.TryGetValue(nodeId, out var cursor);
            if (tree.TryGetTextSelection(nodeId, out int start, out int length)
                && !(known && cursor.TreeStart == start && cursor.TreeLength == length))
            {
                start = Math.Clamp(start, 0, textLength);
                return (start, Math.Clamp(start + length, 0, textLength));
            }
            if (!known) return (-1, -1);
            return (Math.Clamp(cursor.Anchor, 0, textLength), Math.Clamp(cursor.Active, 0, textLength));
        }

        /// <summary>A text's lines, from the tree: the line holding an offset (its start and length).</summary>
        internal delegate bool TextLines(int offset, out int start, out int length);

        /// <summary>
        /// The segment after (or before) <paramref name="current"/>, by Android's text segment
        /// iterators' rules: a character is one character (a surrogate pair is one); a word is a
        /// run of letters and digits, read from the cursor when it is inside one; a line is the next
        /// line that starts at or after the cursor; a paragraph is a run between line breaks.
        /// </summary>
        internal static bool FindSegment(string text, int granularity, bool forward, int current, TextLines lines,
                                         out int start, out int end)
        {
            start = end = -1;
            if (string.IsNullOrEmpty(text)) return false;
            current = Math.Clamp(current, 0, text.Length);

            switch (granularity)
            {
                case GranularityCharacter:
                    if (forward)
                    {
                        if (current >= text.Length) return false;
                        if (current > 0 && char.IsLowSurrogate(text[current]) && char.IsHighSurrogate(text[current - 1])) current--;
                        start = current;
                        end = current + (char.IsHighSurrogate(text[current]) && current + 1 < text.Length && char.IsLowSurrogate(text[current + 1]) ? 2 : 1);
                    }
                    else
                    {
                        if (current > 0 && current < text.Length && char.IsLowSurrogate(text[current]) && char.IsHighSurrogate(text[current - 1])) current--;
                        if (current <= 0) return false;
                        end = current;
                        start = current - (current >= 2 && char.IsLowSurrogate(text[current - 1]) && char.IsHighSurrogate(text[current - 2]) ? 2 : 1);
                    }
                    return true;

                case GranularityWord:
                    if (forward)
                    {
                        int s = current;
                        while (s < text.Length && !IsWordChar(text, s)) s++;
                        if (s >= text.Length) return false;
                        int e = s;
                        while (e < text.Length && IsWordChar(text, e)) e++;
                        (start, end) = (s, e);
                    }
                    else
                    {
                        int e = current;
                        while (e > 0 && !IsWordChar(text, e - 1)) e--;
                        if (e <= 0) return false;
                        int s = e;
                        while (s > 0 && IsWordChar(text, s - 1)) s--;
                        (start, end) = (s, e);
                    }
                    return true;

                case GranularityParagraph:
                    if (forward)
                    {
                        int s = current;
                        while (s < text.Length && IsBreak(text[s])) s++;
                        if (s >= text.Length) return false;
                        int e = s + 1;
                        while (e < text.Length && !IsBreak(text[e])) e++;
                        (start, end) = (s, e);
                    }
                    else
                    {
                        int e = current;
                        while (e > 0 && IsBreak(text[e - 1])) e--;
                        if (e <= 0) return false;
                        int s = e - 1;
                        while (s > 0 && !IsBreak(text[s - 1])) s--;
                        (start, end) = (s, e);
                    }
                    return true;

                case GranularityLine:
                    return lines != null && FindLine(text, forward, current, lines, out start, out end);

                default:
                    return false;
            }
        }

        // LineTextSegmentIterator: forward, the line starting at the cursor or else the one after
        // it; backward, the line ending at the cursor or else the one before it. A line's end here
        // runs over its line break, whether or not the tree counts the break as part of the line.
        private static bool FindLine(string text, bool forward, int current, TextLines lines, out int start, out int end)
        {
            start = end = -1;
            if (forward)
            {
                if (current >= text.Length) return false;
                if (!LineAt(text, current, lines, out int ls, out int le)) return false;
                if (ls != current)
                {
                    if (le >= text.Length) return false;
                    if (!LineAt(text, le, lines, out ls, out le)) return false;
                }
                (start, end) = (ls, le);
                return true;
            }
            else
            {
                if (current <= 0) return false;
                if (!LineAt(text, current - 1, lines, out int ls, out int le)) return false;
                if (le != current)
                {
                    if (ls <= 0) return false;
                    if (!LineAt(text, ls - 1, lines, out ls, out le)) return false;
                }
                (start, end) = (ls, le);
                return true;
            }
        }

        private static bool LineAt(string text, int offset, TextLines lines, out int start, out int end)
        {
            end = -1;
            if (!lines(offset, out start, out int length)) return false;
            start = Math.Clamp(start, 0, text.Length);
            end = Math.Clamp(start + Math.Max(length, 0), start, text.Length);

            // The tree may stop a line before its break; the break belongs with it here, as a
            // TextView's layout puts it at the end of the line it ends.
            if (end > start && IsBreak(text[end - 1]))
            {
                if (text[end - 1] == '\r' && end < text.Length && text[end] == '\n') end++;
            }
            else if (end < text.Length && text[end] == '\r')
            {
                end++;
                if (end < text.Length && text[end] == '\n') end++;
            }
            else if (end < text.Length && IsBreak(text[end])) end++;
            if (end == start && start < text.Length) end = start + 1;   // never a line of nothing
            return true;
        }

        private static bool IsBreak(char c) => c is '\n' or '\r' or '\u2028' or '\u2029';

        private static bool IsWordChar(string text, int i)
        {
            char c = text[i];
            if (char.IsSurrogate(c))
            {
                if (char.IsHighSurrogate(c) && i + 1 < text.Length) return char.IsLetterOrDigit(text, i);
                if (char.IsLowSurrogate(c) && i > 0 && char.IsHighSurrogate(text[i - 1])) return char.IsLetterOrDigit(text, i - 1);
                return false;
            }
            return char.IsLetterOrDigit(c) || c == '\'' && i > 0 && i + 1 < text.Length && char.IsLetter(text[i - 1]) && char.IsLetter(text[i + 1]);
        }

        // ---- events ----
        //
        // An event JSON is {"ev":kind,"cls":class,...} with the kind's own fields:
        //   1 TYPE_VIEW_TEXT_CHANGED            from add rem before text pw
        //   2 TYPE_VIEW_TEXT_SELECTION_CHANGED  from to count text pw
        //   3 TYPE_VIEW_SCROLLED                sx sy mx my dx dy (pixels of content)
        //   4 TYPE_VIEW_SELECTED                text (the item's name)
        //   5 the window's pane                 pane modal (the head sets its view's pane title, and
        //                                       hides the windows under a modal one)

        /// <summary>The event for a change, and the node it is about; an empty JSON when the change
        /// has no event of its own and is only a content change.</summary>
        internal static (int Source, string Json) EventFor(IAutomationTreeSource tree, AutomationChange change)
        {
            int node = change.NodeId;
            switch (change.Kind)
            {
                case AutomationChangeKind.TextChanged:
                    return (node, TextChangedJson(tree, node));
                case AutomationChangeKind.TextSelectionChanged:
                    return (node, TextSelectionJson(tree, node));
                case AutomationChangeKind.ScrollChanged:
                    return (node, ScrolledJson(tree, node));
                case AutomationChangeKind.SelectionContainerChanged:
                {
                    // ListView sends TYPE_VIEW_SELECTED from the item that became selected, and
                    // TalkBack speaks that item; the container itself only changed content.
                    int[] selected = tree.GetSelectedIds(node);
                    if (selected.Length == 0) return (node, string.Empty);
                    int item = selected[0];
                    int role = tree.GetRole(item);
                    if (role < 0) return (node, string.Empty);
                    return (item, new StringBuilder(64).Append("{\"ev\":").Append(EvSelected)
                        .Append(",\"cls\":\"").Append(AndroidClassNames.For(role)).Append('"')
                        .Append(",\"text\":\"").Append(Escape(tree.GetName(item))).Append("\"}").ToString());
                }
                default:
                    return (node, string.Empty);
            }
        }

        /// <summary>
        /// TYPE_VIEW_TEXT_CHANGED, as TextView sends it: where the edit is, how much went and came,
        /// and the text before it. The before-text is the one last seen; a node whose text was
        /// never seen has no baseline and gets no event, because an insertion of its whole text
        /// would be read out as though the user had typed it.
        /// </summary>
        internal static string TextChangedJson(IAutomationTreeSource tree, int node)
        {
            if ((tree.GetState(node) & AccessibleState.HasText) == 0 || !tree.TryGetText(node, out string now))
            {
                s_texts.Remove(node);
                return string.Empty;
            }
            now ??= string.Empty;
            bool known = s_texts.TryGetValue(node, out string before);
            Remember(s_texts, node, now);
            if (!known || before == now) return string.Empty;

            (int from, int removed, int added) = TextDiff(before, now);
            bool password = (tree.GetState(node) & AccessibleState.Password) != 0;
            var json = new StringBuilder(64 + before.Length + now.Length);
            json.Append("{\"ev\":").Append(EvTextChanged)
                .Append(",\"cls\":\"").Append(AndroidClassNames.For(tree.GetRole(node))).Append('"')
                .Append(",\"from\":").Append(from).Append(",\"add\":").Append(added).Append(",\"rem\":").Append(removed)
                .Append(",\"before\":\"").Append(Escape(password ? Mask(before) : before)).Append('"')
                .Append(",\"text\":\"").Append(Escape(password ? Mask(now) : now)).Append('"');
            if (password) json.Append(",\"pw\":1");
            return json.Append('}').ToString();
        }

        /// <summary>
        /// The one replacement that turns <paramref name="before"/> into <paramref name="after"/>:
        /// the common start and end are kept, and what is between them was removed and added.
        /// Never splits a surrogate pair, so the counts are whole characters.
        /// </summary>
        internal static (int From, int Removed, int Added) TextDiff(string before, string after)
        {
            before ??= string.Empty;
            after ??= string.Empty;
            int prefix = 0, limit = Math.Min(before.Length, after.Length);
            while (prefix < limit && before[prefix] == after[prefix]) prefix++;
            // Stopped between the halves of a pair: the pair is what changed.
            if (prefix > 0 && char.IsHighSurrogate(before[prefix - 1])) prefix--;

            int suffix = 0;
            while (suffix < before.Length - prefix && suffix < after.Length - prefix
                   && before[before.Length - 1 - suffix] == after[after.Length - 1 - suffix]) suffix++;
            if (suffix > 0 && char.IsLowSurrogate(before[before.Length - suffix])) suffix--;

            return (prefix, before.Length - prefix - suffix, after.Length - prefix - suffix);
        }

        /// <summary>TYPE_VIEW_TEXT_SELECTION_CHANGED: the selection's start and end, and the text's
        /// length as the item count, which is what EditText puts in them.</summary>
        internal static string TextSelectionJson(IAutomationTreeSource tree, int node)
        {
            AccessibleState state = tree.GetState(node);
            if ((state & AccessibleState.HasText) == 0 || !tree.TryGetText(node, out string text)
                || !tree.TryGetTextSelection(node, out int start, out int length)) return string.Empty;
            text ??= string.Empty;
            start = Math.Clamp(start, 0, text.Length);
            length = Math.Clamp(length, 0, text.Length - start);
            bool password = (state & AccessibleState.Password) != 0;

            var json = new StringBuilder(64 + text.Length);
            json.Append("{\"ev\":").Append(EvTextSelectionChanged)
                .Append(",\"cls\":\"").Append(AndroidClassNames.For(tree.GetRole(node))).Append('"')
                .Append(",\"from\":").Append(start).Append(",\"to\":").Append(start + length)
                .Append(",\"count\":").Append(text.Length)
                .Append(",\"text\":\"").Append(Escape(password ? Mask(text) : text)).Append('"');
            if (password) json.Append(",\"pw\":1");
            return json.Append('}').ToString();
        }

        /// <summary>
        /// TYPE_VIEW_SCROLLED with ScrollView's numbers: the scroll offset and its maximum, in pixels
        /// of content, and the change since the last one. The tree speaks in percentages of the
        /// content and view sizes, so the content's extent is worked back from the viewport's.
        /// </summary>
        internal static string ScrolledJson(IAutomationTreeSource tree, int node)
        {
            if (!tree.TryGetScroll(node, out double hp, out double vp, out double hSize, out double vSize, out bool hs, out bool vs))
                return string.Empty;
            tree.TryGetBounds(node, out _, out _, out double w, out double h);

            (long sx, long mx) = ScrollPixels(hs, hp, hSize, w);
            (long sy, long my) = ScrollPixels(vs, vp, vSize, h);
            long dx = 0, dy = 0;
            if (s_scrolls.TryGetValue(node, out var last)) { dx = sx - last.X; dy = sy - last.Y; }
            Remember(s_scrolls, node, (sx, sy));

            return new StringBuilder(96).Append("{\"ev\":").Append(EvScrolled)
                .Append(",\"cls\":\"").Append(AndroidClassNames.For(tree.GetRole(node))).Append('"')
                .Append(",\"sx\":").Append(sx).Append(",\"sy\":").Append(sy)
                .Append(",\"mx\":").Append(mx).Append(",\"my\":").Append(my)
                .Append(",\"dx\":").Append(dx).Append(",\"dy\":").Append(dy).Append('}').ToString();
        }

        /// <summary>A direction's offset and maximum offset in pixels, from its percentage, its view
        /// size (the viewport as a percentage of the content) and the viewport's pixels.</summary>
        internal static (long Offset, long Maximum) ScrollPixels(bool scrollable, double percent, double viewSize, double viewport)
        {
            if (!scrollable || percent < 0 || viewSize <= 0 || viewport <= 0) return (0, 0);
            double content = viewport * 100.0 / Math.Min(viewSize, 100.0);
            double maximum = Math.Max(content - viewport, 0);
            return ((long)Math.Round(maximum * Math.Clamp(percent, 0, 100) / 100.0), (long)Math.Round(maximum));
        }

        /// <summary>
        /// The pane event for a window whose title or modality has changed since it was last told
        /// (or never was), with its root as the source; empty when nothing changed.
        /// </summary>
        internal static string WindowEventJson(IAutomationTreeSource tree, IntPtr handle, out int root)
        {
            root = tree.GetRootId(handle);
            if (root == InvalidNode) return string.Empty;
            AccessibleState state = tree.GetState(root);
            if ((state & AccessibleState.Window) == 0) return string.Empty;

            bool modal = (state & AccessibleState.Modal) != 0;
            string pane = tree.GetName(root) ?? string.Empty;
            if (s_windows.TryGetValue(handle, out var known) && known.Modal == modal && known.Pane == pane) return string.Empty;
            Remember(s_windows, handle, (modal, pane));

            return new StringBuilder(64).Append("{\"ev\":").Append(EvWindow)
                .Append(",\"pane\":\"").Append(Escape(pane)).Append('"')
                .Append(",\"modal\":").Append(modal ? 1 : 0).Append('}').ToString();
        }

        /// <summary>Forgets what the events are diffed against. For tests, which share one process.</summary>
        internal static void ResetEventState()
        {
            s_texts.Clear();
            s_scrolls.Clear();
            s_windows.Clear();
            s_cursors.Clear();
        }

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
                    switch (c)
                    {
                        case '"': sb.Append("\\\""); break;
                        case '\\': sb.Append("\\\\"); break;
                        case '\n': sb.Append("\\n"); break;
                        case '\r': sb.Append("\\r"); break;
                        case '\t': sb.Append("\\t"); break;
                        // Kept rather than dropped: a text's offsets (the selection, an edit's
                        // position) count every character, so losing one moves all after it.
                        default: sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture)); break;
                    }
                }
                else sb?.Append(c);
            }
            return sb?.ToString() ?? value;
        }
    }

    /// <summary>
    /// AutomationControlType to the android.widget class names TalkBack keys its announcements off.
    /// Android has no role enum: the CLASS NAME is the role.
    /// </summary>
    internal static class AndroidClassNames
    {
        internal static string For(int automationControlType) => automationControlType switch
        {
            0 => "android.widget.Button",
            2 => "android.widget.CheckBox",
            3 => "android.widget.Spinner",
            4 => "android.widget.EditText",
            5 => "android.widget.TextView",      // Hyperlink
            6 => "android.widget.ImageView",
            7 => "android.widget.TextView",      // ListItem
            8 => "android.widget.ListView",
            9 or 10 => "android.widget.ListView",// Menu, MenuBar
            11 => "android.widget.TextView",     // MenuItem
            12 => "android.widget.ProgressBar",
            13 => "android.widget.RadioButton",
            14 => "android.widget.ScrollBar",
            15 => "android.widget.SeekBar",
            16 => "android.widget.NumberPicker",
            18 => "android.widget.TabWidget",
            19 => "android.widget.TextView",     // TabItem
            20 => "android.widget.TextView",
            23 => "android.widget.ListView",     // Tree
            24 => "android.widget.TextView",     // TreeItem
            28 or 36 => "android.widget.GridView",
            29 => "android.view.View",           // DataItem
            31 => "android.widget.Button",       // SplitButton
            _ => "android.view.View",
        };
    }
}
