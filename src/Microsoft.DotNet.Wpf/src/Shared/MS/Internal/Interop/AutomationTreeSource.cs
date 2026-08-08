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
    internal enum AccessibleAction
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
    internal enum AccessibleState
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
    internal interface IAutomationTreeSource
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
    internal static class AutomationTree
    {
        /// <summary>
        /// The installed tree, or null when PresentationCore has not started one -- which is the
        /// case in a WindowsBase-only process, and until an assistive technology is detected.
        /// </summary>
        internal static IAutomationTreeSource Current { get; set; }

        /// <summary>True when there is a tree to ask. Backends check this before every batch.</summary>
        internal static bool IsAvailable => Current != null;

        /// <summary>
        /// Raised by PresentationCore when something changed that an assistive technology needs to
        /// hear about. Backends translate it into their platform's notification; nothing else in
        /// WindowsBase listens.
        /// </summary>
        internal static event Action<AutomationChange> Changed;

        /// <summary>
        /// Raised when a backend has detected a real assistive technology and wants the tree built.
        ///
        /// The direction matters: the backends live down here in WindowsBase, but the tree they are
        /// asking for is PresentationCore's, and WindowsBase cannot reference PresentationCore. So a
        /// backend cannot call Activate directly -- it raises this, and PresentationCore, which
        /// subscribed on the way up, answers. The mirror image of Changed below.
        /// </summary>
        internal static event Action ActivationRequested;

        /// <summary>
        /// Called by a backend the first time it sees an assistive technology. Safe to call
        /// repeatedly; PresentationCore's handler is idempotent.
        /// </summary>
        internal static void RequestActivation()
        {
            try { ActivationRequested?.Invoke(); }
            catch (InvalidOperationException) { }   // nothing listening yet
        }

        /// <summary>Called by PresentationCore. Kept here so the event stays private to the pair.</summary>
        internal static void RaiseChanged(AutomationChange change)
        {
            try { Changed?.Invoke(change); }
            catch (InvalidOperationException) { }   // a backend torn down mid-notification
        }
    }

    /// <summary>One thing that changed, as flat data a backend can forward without touching the tree.</summary>
    internal readonly struct AutomationChange
    {
        public AutomationChangeKind Kind { get; }
        public int NodeId { get; }
        public IntPtr WindowHandle { get; }

        public AutomationChange(AutomationChangeKind kind, int nodeId, IntPtr windowHandle)
        {
            Kind = kind;
            NodeId = nodeId;
            WindowHandle = windowHandle;
        }
    }

    internal enum AutomationChangeKind
    {
        /// <summary>Keyboard focus moved to NodeId.</summary>
        FocusChanged,
        /// <summary>NodeId's children were added or removed.</summary>
        ChildrenChanged,
        /// <summary>A property of NodeId (name, value, state) changed.</summary>
        PropertyChanged,
        /// <summary>NodeId's value changed specifically -- worth its own kind because most platforms
        /// have a dedicated, less chatty notification for it than for any property.</summary>
        ValueChanged,
        /// <summary>NodeId became or stopped being selected.</summary>
        SelectionChanged,
    }
}
