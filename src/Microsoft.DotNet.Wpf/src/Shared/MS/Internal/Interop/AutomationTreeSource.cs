// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The seam between WPF's accessibility tree and the platform backends that expose it.
//
// Every native assistive-technology API is pull-based and addresses nodes by ID: "what are the
// children of node 7", "what is node 7 called", "activate node 7". WPF's side of that conversation
// is the AutomationPeer tree, which lives in PresentationCore -- an assembly WindowsBase cannot
// reference. So the contract is declared down here, where the platform code lives, and implemented
// up there, where the tree lives. The same shape as IUIKitTextDocument in the iOS text stack, and
// for the same reason: the native side has to PULL, not be pushed to.
//
// Everything is primitive-typed on purpose. These calls are made from Objective-C IMPs, JNI-adjacent
// host code, D-Bus message handlers and JS interop, none of which want a marshalling framework.
//
// Every member must be TOTAL. An assistive technology asks about nodes on its own schedule, and by
// the time a question arrives the element behind it may already be gone -- a stale ID has to come
// back as "nothing", never as an exception, because there is no managed frame above these calls to
// catch one.
//

using System;

namespace MS.Internal.Interop
{
    /// <summary>What an accessible node can be asked to do. Each maps to a WPF automation pattern.</summary>
    public enum AccessibleAction
    {
        /// <summary>Press a button, activate a hyperlink (IInvokeProvider).</summary>
        Invoke,
        /// <summary>Flip a checkbox or toggle button through its next state (IToggleProvider).</summary>
        Toggle,
        /// <summary>Open a tree node, combo box or expander (IExpandCollapseProvider).</summary>
        Expand,
        /// <summary>Close one (IExpandCollapseProvider).</summary>
        Collapse,
        /// <summary>Select a list/tree item, replacing the current selection (ISelectionItemProvider).</summary>
        Select,
        /// <summary>Add to or remove from a multi-selection (ISelectionItemProvider).</summary>
        ToggleSelection,
    }

    /// <summary>
    /// State bits an assistive technology asks about. Deliberately a flags enum rather than a bag of
    /// bool accessors: every backend needs most of them at once, and one call per node per query is
    /// already the dominant cost of an accessibility tree walk.
    /// </summary>
    [Flags]
    public enum AccessibleState
    {
        None = 0,
        Enabled = 1 << 0,
        Focusable = 1 << 1,
        Focused = 1 << 2,
        Offscreen = 1 << 3,
        Password = 1 << 4,
        ReadOnly = 1 << 5,
        /// <summary>Checkbox/radio is checked. <see cref="Indeterminate"/> wins over this.</summary>
        Checked = 1 << 6,
        Indeterminate = 1 << 7,
        Expanded = 1 << 8,
        Collapsed = 1 << 9,
        Selected = 1 << 10,
        Selectable = 1 << 11,
        /// <summary>The node has a Value pattern, so <see cref="IAutomationTreeSource.GetValue"/> means something.</summary>
        HasValue = 1 << 12,
        /// <summary>The node has a RangeValue pattern, so TryGetRange will answer.</summary>
        HasRange = 1 << 13,
        /// <summary>The node has a Text pattern: its text, selection and character geometry (TryGetText and the rest).</summary>
        HasText = 1 << 14,
        /// <summary>The node holds selectable items (a Selection pattern): GetSelectedIds answers.</summary>
        SelectionContainer = 1 << 15,
        /// <summary>The node is a grid of cells (a Grid pattern): TryGetGrid and GetGridCell answer.</summary>
        Grid = 1 << 16,
        /// <summary>The grid has row and column headers (a Table pattern).</summary>
        Table = 1 << 17,
        /// <summary>The node is a cell of a grid (a GridItem pattern): TryGetGridItem answers.</summary>
        GridItem = 1 << 18,
        /// <summary>The node scrolls its content (a Scroll pattern): TryGetScroll and Scroll answer.</summary>
        Scrollable = 1 << 19,
        /// <summary>The node can be scrolled into view (a ScrollItem pattern).</summary>
        ScrollItem = 1 << 20,
        /// <summary>The node is a window (a Window pattern): TryGetWindow and its commands answer.</summary>
        Window = 1 << 21,
        /// <summary>A selection container that allows more than one item selected.</summary>
        MultiSelectable = 1 << 22,
        /// <summary>A window that blocks the others until it closes.</summary>
        Modal = 1 << 23,
    }

    /// <summary>
    /// The accessibility tree, as the platform backends see it. Implemented by PresentationCore's
    /// AutomationBridge over the AutomationPeer tree, and installed in <see cref="Current"/>.
    /// </summary>
    public interface IAutomationTreeSource
    {
        /// <summary>
        /// The root node of the window identified by <paramref name="windowHandle"/> (the same handle
        /// the platform window backends hand out), or <see cref="InvalidNode"/> if that window has no
        /// tree -- which is the normal answer before anything has asked for accessibility.
        /// </summary>
        int GetRootId(IntPtr windowHandle);

        /// <summary>Children in tree order. Never null; an empty array for a leaf or a stale id.</summary>
        int[] GetChildIds(int id);

        /// <summary>The parent, or <see cref="InvalidNode"/> for the root or a stale id.</summary>
        int GetParentId(int id);

        /// <summary>The AutomationControlType, as its integer value. -1 for a stale id.</summary>
        int GetRole(int id);

        /// <summary>The accessible name. Never null; empty when unnamed.</summary>
        string GetName(int id);

        /// <summary>The stable, non-localised id an automated test would use. Never null.</summary>
        string GetAutomationId(int id);

        /// <summary>Extra description beyond the name (tooltip / help text). Never null.</summary>
        string GetHelpText(int id);

        /// <summary>The current value as text, for anything with a Value pattern. Never null.</summary>
        string GetValue(int id);

        /// <summary>Numeric value and bounds for a slider/progress bar. False when there is no range.</summary>
        bool TryGetRange(int id, out double value, out double minimum, out double maximum);

        /// <summary>The state bits. <see cref="AccessibleState.None"/> for a stale id.</summary>
        AccessibleState GetState(int id);

        /// <summary>
        /// Screen bounds in device pixels, top-left origin. All-zero when the node has no
        /// rectangle (offscreen, or a stale id); backends must treat that as "do not draw a focus
        /// ring here" rather than as a rectangle at the origin.
        /// </summary>
        bool TryGetBounds(int id, out double x, out double y, out double width, out double height);

        /// <summary>The deepest node at a screen point, or <see cref="InvalidNode"/>.</summary>
        int HitTest(IntPtr windowHandle, double screenX, double screenY);

        /// <summary>The node with keyboard focus, or <see cref="InvalidNode"/>.</summary>
        int GetFocusedId(IntPtr windowHandle);

        /// <summary>Whether <paramref name="action"/> is available before offering it to the user.</summary>
        bool SupportsAction(int id, AccessibleAction action);

        /// <summary>Performs the action. False when the node is gone or the pattern is absent.</summary>
        bool DoAction(int id, AccessibleAction action);

        /// <summary>Sets the text value of an editable node (IValueProvider.SetValue).</summary>
        bool SetValue(int id, string value);

        /// <summary>Gives the node keyboard focus.</summary>
        bool SetFocus(int id);

        // ---- text (a Text pattern). Offsets are UTF-16 code units from the start of the text. ----------

        /// <summary>The whole text. False without a Text pattern.</summary>
        bool TryGetText(int id, out string text) { text = string.Empty; return false; }

        /// <summary>The selection, or the caret as an empty one at <paramref name="start"/>.</summary>
        bool TryGetTextSelection(int id, out int start, out int length) { start = length = 0; return false; }

        /// <summary>Selects a range (an empty one puts the caret there).</summary>
        bool SetTextSelection(int id, int start, int length) => false;

        /// <summary>The screen rectangle (device pixels) around a range's characters.</summary>
        bool TryGetTextRangeBounds(int id, int start, int length, out double x, out double y, out double width, out double height)
        { x = y = width = height = 0; return false; }

        /// <summary>The line holding the character at <paramref name="offset"/>: its first offset, its length, and its number.</summary>
        bool TryGetTextLine(int id, int offset, out int lineStart, out int lineLength, out int lineNumber)
        { lineStart = lineLength = lineNumber = 0; return false; }

        /// <summary>The offset of the character at a screen point, or -1.</summary>
        int GetTextOffsetAtPoint(int id, double screenX, double screenY) => -1;

        /// <summary>The part of the text on screen.</summary>
        bool TryGetVisibleTextRange(int id, out int start, out int length) { start = length = 0; return false; }

        // ---- selection (a Selection pattern) ----------------------------------------------------------

        /// <summary>The selected items of a selection container. Never null.</summary>
        int[] GetSelectedIds(int id) => Array.Empty<int>();

        // ---- grids and tables (Grid, GridItem, Table patterns) -----------------------------------------

        /// <summary>A grid's rows and columns.</summary>
        bool TryGetGrid(int id, out int rows, out int columns) { rows = columns = 0; return false; }

        /// <summary>The cell at a row and column, or <see cref="InvalidNode"/>.</summary>
        int GetGridCell(int id, int row, int column) => InvalidNode;

        /// <summary>A cell's place: row, column, spans, and the grid it is in.</summary>
        bool TryGetGridItem(int id, out int row, out int column, out int rowSpan, out int columnSpan, out int gridId)
        { row = column = rowSpan = columnSpan = 0; gridId = InvalidNode; return false; }

        /// <summary>A table's column headers. Never null.</summary>
        int[] GetColumnHeaderIds(int id) => Array.Empty<int>();

        /// <summary>A table's row headers. Never null.</summary>
        int[] GetRowHeaderIds(int id) => Array.Empty<int>();

        // ---- scrolling (Scroll, ScrollItem patterns) --------------------------------------------------

        /// <summary>
        /// Where a scrolling node is: each direction's position and view size as percentages (0-100;
        /// -1 for a direction that does not scroll), and whether it scrolls.
        /// </summary>
        bool TryGetScroll(int id, out double horizontalPercent, out double verticalPercent, out double horizontalViewSize,
            out double verticalViewSize, out bool horizontallyScrollable, out bool verticallyScrollable)
        {
            horizontalPercent = verticalPercent = -1; horizontalViewSize = verticalViewSize = 100;
            horizontallyScrollable = verticallyScrollable = false;
            return false;
        }

        /// <summary>Scrolls by amounts (System.Windows.Automation.ScrollAmount: 0 large decrement, 1 small
        /// decrement, 2 none, 3 large increment, 4 small increment).</summary>
        bool Scroll(int id, int horizontalAmount, int verticalAmount) => false;

        /// <summary>Scrolls to percentages (-1 leaves a direction where it is).</summary>
        bool SetScrollPercent(int id, double horizontalPercent, double verticalPercent) => false;

        /// <summary>Scrolls the node's container until the node is in view.</summary>
        bool ScrollIntoView(int id) => false;

        // ---- windows (a Window pattern) ---------------------------------------------------------------

        /// <summary>A window's visual state (System.Windows.Automation.WindowVisualState: 0 normal, 1
        /// maximized, 2 minimized), and whether it can be minimized, maximized, and is topmost.</summary>
        bool TryGetWindow(int id, out int visualState, out bool canMinimize, out bool canMaximize, out bool isTopmost)
        { visualState = 0; canMinimize = canMaximize = isTopmost = false; return false; }

        /// <summary>Minimizes, maximizes or restores the window.</summary>
        bool SetWindowVisualState(int id, int visualState) => false;

        /// <summary>Closes the window.</summary>
        bool CloseWindow(int id) => false;

        /// <summary>
        /// The reply to any question about a node that does not exist, or never did. Chosen as -1
        /// rather than 0 because 0 is a perfectly good node id and several of these APIs would
        /// happily walk to it.
        /// </summary>
        const int InvalidNode = -1;
    }

    /// <summary>
    /// Where the backends find the tree, and where they report what the user did to it.
    ///
    /// Static because there is one accessibility tree per process, exactly as there is one input
    /// method: assistive technologies address the application, not a particular window, and the
    /// window handle is a parameter rather than a separate channel.
    /// </summary>
    public static class AutomationTree
    {
        /// <summary>
        /// The installed tree, or null when PresentationCore has not started one -- which is the
        /// case in a WindowsBase-only process, and until an assistive technology is detected.
        /// </summary>
        public static IAutomationTreeSource Current
        {
            get { lock (s_sources) return s_sources.Count == 0 ? s_primary : s_router; }
            set => s_primary = value;
        }

        private static IAutomationTreeSource s_primary;
        private static readonly System.Collections.Generic.List<(IAutomationTreeSource Source, int IdBase)> s_sources = new();
        private static readonly Router s_router = new();

        /// <summary>
        /// A tree of another UI stack in the process (WinForms' forms) beside WPF's: it answers for its own windows (a
        /// root for their handles) and for node ids from <paramref name="idBase"/> up, above any WPF hands out.
        /// <see cref="Current"/> is then a router that asks each its own.
        /// </summary>
        public static void AddSource(IAutomationTreeSource source, int idBase)
        {
            lock (s_sources)
            {
                if (s_sources.Exists(x => ReferenceEquals(x.Source, source))) return;
                s_sources.Add((source, idBase));
                s_sources.Sort((a, b) => b.IdBase.CompareTo(a.IdBase));
            }
        }

        // a node to the source whose ids it is in; a window to the first source with a root for it
        private sealed class Router : IAutomationTreeSource
        {
            private static IAutomationTreeSource ForId(int id)
            {
                lock (s_sources)
                    foreach (var (source, idBase) in s_sources)
                        if (id >= idBase) return source;
                return s_primary;
            }

            private static IAutomationTreeSource ForWindow(IntPtr windowHandle)
            {
                (IAutomationTreeSource Source, int IdBase)[] sources;
                lock (s_sources) sources = s_sources.ToArray();
                foreach (var (source, _) in sources)
                    if (source.GetRootId(windowHandle) != IAutomationTreeSource.InvalidNode) return source;
                return s_primary;
            }

            public int GetRootId(IntPtr windowHandle) => ForWindow(windowHandle)?.GetRootId(windowHandle) ?? IAutomationTreeSource.InvalidNode;
            public int[] GetChildIds(int id) => ForId(id)?.GetChildIds(id) ?? Array.Empty<int>();
            public int GetParentId(int id) => ForId(id)?.GetParentId(id) ?? IAutomationTreeSource.InvalidNode;
            public int GetRole(int id) => ForId(id)?.GetRole(id) ?? -1;
            public string GetName(int id) => ForId(id)?.GetName(id);
            public string GetAutomationId(int id) => ForId(id)?.GetAutomationId(id);
            public string GetHelpText(int id) => ForId(id)?.GetHelpText(id);
            public string GetValue(int id) => ForId(id)?.GetValue(id);
            public bool TryGetRange(int id, out double value, out double minimum, out double maximum)
            {
                value = minimum = maximum = 0;
                return ForId(id)?.TryGetRange(id, out value, out minimum, out maximum) ?? false;
            }
            public AccessibleState GetState(int id) => ForId(id)?.GetState(id) ?? AccessibleState.None;
            public bool TryGetBounds(int id, out double x, out double y, out double width, out double height)
            {
                x = y = width = height = 0;
                return ForId(id)?.TryGetBounds(id, out x, out y, out width, out height) ?? false;
            }
            public int HitTest(IntPtr windowHandle, double screenX, double screenY) => ForWindow(windowHandle)?.HitTest(windowHandle, screenX, screenY) ?? IAutomationTreeSource.InvalidNode;
            public int GetFocusedId(IntPtr windowHandle) => ForWindow(windowHandle)?.GetFocusedId(windowHandle) ?? IAutomationTreeSource.InvalidNode;
            public bool SupportsAction(int id, AccessibleAction action) => ForId(id)?.SupportsAction(id, action) ?? false;
            public bool DoAction(int id, AccessibleAction action) => ForId(id)?.DoAction(id, action) ?? false;
            public bool SetValue(int id, string value) => ForId(id)?.SetValue(id, value) ?? false;
            public bool SetFocus(int id) => ForId(id)?.SetFocus(id) ?? false;

            public bool TryGetText(int id, out string text) { text = string.Empty; return ForId(id)?.TryGetText(id, out text) ?? false; }
            public bool TryGetTextSelection(int id, out int start, out int length) { start = length = 0; return ForId(id)?.TryGetTextSelection(id, out start, out length) ?? false; }
            public bool SetTextSelection(int id, int start, int length) => ForId(id)?.SetTextSelection(id, start, length) ?? false;
            public bool TryGetTextRangeBounds(int id, int start, int length, out double x, out double y, out double width, out double height)
            {
                x = y = width = height = 0;
                return ForId(id)?.TryGetTextRangeBounds(id, start, length, out x, out y, out width, out height) ?? false;
            }
            public bool TryGetTextLine(int id, int offset, out int lineStart, out int lineLength, out int lineNumber)
            {
                lineStart = lineLength = lineNumber = 0;
                return ForId(id)?.TryGetTextLine(id, offset, out lineStart, out lineLength, out lineNumber) ?? false;
            }
            public int GetTextOffsetAtPoint(int id, double screenX, double screenY) => ForId(id)?.GetTextOffsetAtPoint(id, screenX, screenY) ?? -1;
            public bool TryGetVisibleTextRange(int id, out int start, out int length) { start = length = 0; return ForId(id)?.TryGetVisibleTextRange(id, out start, out length) ?? false; }
            public int[] GetSelectedIds(int id) => ForId(id)?.GetSelectedIds(id) ?? Array.Empty<int>();
            public bool TryGetGrid(int id, out int rows, out int columns) { rows = columns = 0; return ForId(id)?.TryGetGrid(id, out rows, out columns) ?? false; }
            public int GetGridCell(int id, int row, int column) => ForId(id)?.GetGridCell(id, row, column) ?? IAutomationTreeSource.InvalidNode;
            public bool TryGetGridItem(int id, out int row, out int column, out int rowSpan, out int columnSpan, out int gridId)
            {
                row = column = rowSpan = columnSpan = 0; gridId = IAutomationTreeSource.InvalidNode;
                return ForId(id)?.TryGetGridItem(id, out row, out column, out rowSpan, out columnSpan, out gridId) ?? false;
            }
            public int[] GetColumnHeaderIds(int id) => ForId(id)?.GetColumnHeaderIds(id) ?? Array.Empty<int>();
            public int[] GetRowHeaderIds(int id) => ForId(id)?.GetRowHeaderIds(id) ?? Array.Empty<int>();
            public bool TryGetScroll(int id, out double horizontalPercent, out double verticalPercent, out double horizontalViewSize,
                out double verticalViewSize, out bool horizontallyScrollable, out bool verticallyScrollable)
            {
                horizontalPercent = verticalPercent = -1; horizontalViewSize = verticalViewSize = 100; horizontallyScrollable = verticallyScrollable = false;
                return ForId(id)?.TryGetScroll(id, out horizontalPercent, out verticalPercent, out horizontalViewSize, out verticalViewSize, out horizontallyScrollable, out verticallyScrollable) ?? false;
            }
            public bool Scroll(int id, int horizontalAmount, int verticalAmount) => ForId(id)?.Scroll(id, horizontalAmount, verticalAmount) ?? false;
            public bool SetScrollPercent(int id, double horizontalPercent, double verticalPercent) => ForId(id)?.SetScrollPercent(id, horizontalPercent, verticalPercent) ?? false;
            public bool ScrollIntoView(int id) => ForId(id)?.ScrollIntoView(id) ?? false;
            public bool TryGetWindow(int id, out int visualState, out bool canMinimize, out bool canMaximize, out bool isTopmost)
            {
                visualState = 0; canMinimize = canMaximize = isTopmost = false;
                return ForId(id)?.TryGetWindow(id, out visualState, out canMinimize, out canMaximize, out isTopmost) ?? false;
            }
            public bool SetWindowVisualState(int id, int visualState) => ForId(id)?.SetWindowVisualState(id, visualState) ?? false;
            public bool CloseWindow(int id) => ForId(id)?.CloseWindow(id) ?? false;
        }

        /// <summary>True when there is a tree to ask. Backends check this before every batch.</summary>
        public static bool IsAvailable => Current != null;

        /// <summary>
        /// Raised by PresentationCore when something changed that an assistive technology needs to
        /// hear about. Backends translate it into their platform's notification; nothing else in
        /// WindowsBase listens.
        /// </summary>
        public static event Action<AutomationChange> Changed;

        /// <summary>
        /// Raised when a backend has detected a real assistive technology and wants the tree built.
        ///
        /// The direction matters: the backends live down here in WindowsBase, but the tree they are
        /// asking for is PresentationCore's, and WindowsBase cannot reference PresentationCore. So a
        /// backend cannot call Activate directly -- it raises this, and PresentationCore, which
        /// subscribed on the way up, answers. The mirror image of Changed below.
        /// </summary>
        public static event Action ActivationRequested;

        /// <summary>
        /// Called by a backend the first time it sees an assistive technology. Safe to call
        /// repeatedly; PresentationCore's handler is idempotent.
        /// </summary>
        public static void RequestActivation()
        {
            try { ActivationRequested?.Invoke(); }
            catch (InvalidOperationException) { }   // nothing listening yet
        }

        /// <summary>Called by PresentationCore. Kept here so the event stays private to the pair.</summary>
        public static void RaiseChanged(AutomationChange change)
        {
            try { Changed?.Invoke(change); }
            catch (InvalidOperationException) { }   // a backend torn down mid-notification
        }
    }

    /// <summary>One thing that changed, as flat data a backend can forward without touching the tree.</summary>
    public readonly struct AutomationChange
    {
        public AutomationChangeKind Kind { get; }
        public int NodeId { get; }
        public IntPtr WindowHandle { get; }

        /// <summary>
        /// The child that was added or removed, for <see cref="AutomationChangeKind.ChildAdded"/> and
        /// <see cref="AutomationChangeKind.ChildRemoved"/>; <see cref="IAutomationTreeSource.InvalidNode"/>
        /// otherwise. NodeId is the PARENT for those two kinds, because that is the node whose child
        /// list changed and the one every platform's notification is addressed to.
        /// </summary>
        public int ChildId { get; }

        /// <summary>The child's index in the parent, or -1 when it is not known (a removal).</summary>
        public int Index { get; }

        public AutomationChange(AutomationChangeKind kind, int nodeId, IntPtr windowHandle)
            : this(kind, nodeId, windowHandle, IAutomationTreeSource.InvalidNode, -1)
        {
        }

        public AutomationChange(AutomationChangeKind kind, int nodeId, IntPtr windowHandle,
                                int childId, int index)
        {
            Kind = kind;
            NodeId = nodeId;
            WindowHandle = windowHandle;
            ChildId = childId;
            Index = index;
        }
    }

    /// <summary>
    /// What changed, at the granularity the native APIs distinguish.
    ///
    /// The specific kinds exist because every assistive technology says something DIFFERENT for each
    /// of them -- "expanded", "checked", "dimmed" -- and a backend handed a generic "something about
    /// this node changed" has no way to recover which. Collapsing them was measurably wrong on Linux:
    /// opening a combo box was announced to Orca as a NAME change.
    ///
    /// Backends switch on this with a default arm, so new kinds degrade to the generic notification
    /// rather than breaking a head that has not been taught about them.
    /// </summary>
    public enum AutomationChangeKind
    {
        /// <summary>Keyboard focus moved to NodeId.</summary>
        FocusChanged,
        /// <summary>NodeId's children changed in bulk; the child list must be re-read.</summary>
        ChildrenChanged,
        /// <summary>A property of NodeId changed, and it is not one of the specific kinds below.</summary>
        PropertyChanged,
        /// <summary>NodeId's value changed specifically -- worth its own kind because most platforms
        /// have a dedicated, less chatty notification for it than for any property.</summary>
        ValueChanged,
        /// <summary>NodeId became or stopped being selected.</summary>
        SelectionChanged,
        /// <summary>NodeId's accessible name changed.</summary>
        NameChanged,
        /// <summary>NodeId's help text / description changed.</summary>
        DescriptionChanged,
        /// <summary>NodeId expanded or collapsed (a combo box, tree node, expander).</summary>
        ExpandedChanged,
        /// <summary>NodeId's checkbox/toggle state changed.</summary>
        CheckedChanged,
        /// <summary>NodeId became enabled or disabled.</summary>
        EnabledChanged,
        /// <summary>NodeId scrolled on or off screen. It still exists either way.</summary>
        OffscreenChanged,
        /// <summary>ChildId was added to NodeId at Index.</summary>
        ChildAdded,
        /// <summary>ChildId was removed from NodeId.</summary>
        ChildRemoved,
        /// <summary>NodeId's text (a Text pattern's) changed.</summary>
        TextChanged,
        /// <summary>NodeId's text selection or caret moved.</summary>
        TextSelectionChanged,
        /// <summary>NodeId scrolled.</summary>
        ScrollChanged,
        /// <summary>The set of selected items in the container NodeId changed.</summary>
        SelectionContainerChanged,
    }
}
