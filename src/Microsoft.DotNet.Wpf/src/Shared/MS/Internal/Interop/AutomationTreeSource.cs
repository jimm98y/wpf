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
    }
}
