// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// WPF's accessibility tree, presented to the platform backends that expose it to a screen reader.
//
// WPF already models accessibility completely and platform-neutrally: about a hundred AutomationPeer
// classes describe every control's role, name, value, bounds and state, and AutomationPeer.GetPattern
// hands back the same IInvokeProvider/IToggleProvider/IValueProvider interfaces that drive UI
// Automation on Windows. None of that is Windows-specific -- it is all managed code compiled into
// every head. What was missing off Windows is only the last mile: something to answer a native
// assistive technology's questions out of that tree.
//
// This is that. It is deliberately NOT an abstraction over the peers -- it is an ADAPTER, turning a
// lazily-built object graph into the flat, id-addressed, total-function view every native
// accessibility API expects (see IAutomationTreeSource for why).
//
// Node identity is the one thing WPF does not provide. Peers are created on demand, discarded freely
// and have no stable handle, while every AT addresses nodes by number and will happily ask about a
// node long after the element behind it is gone. So ids are minted here, held weakly, and answered
// as "nothing" once the peer has been collected.
//

using MS.Internal.Interop;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Interop;
using System.Windows.Media;

namespace MS.Internal.Automation
{
    /// <summary>
    /// The single process-wide adapter from the AutomationPeer tree to IAutomationTreeSource.
    /// Dormant until <see cref="Activate"/> is called by a platform backend that has detected a real
    /// assistive technology.
    /// </summary>
    internal sealed class AutomationBridge : IAutomationTreeSource
    {
        private static readonly AutomationBridge s_instance = new AutomationBridge();

        /// <summary>
        /// True once an assistive technology has been detected and the tree is being maintained.
        ///
        /// This is deliberately not "true off Windows": building and updating an accessibility tree
        /// costs real work on every layout and property change, and the overwhelming majority of runs
        /// have no AT attached. Backends probe their platform (VoiceOver running, the a11y bus
        /// present, AccessibilityManager enabled, ...) and switch this on only then.
        /// </summary>
        internal static bool IsActive { get; private set; }

        /// <summary>
        /// Starts serving the tree. Idempotent, and safe to call from any thread a backend happens to
        /// detect its AT on.
        ///
        /// Two things have to happen for peers to exist at all off Windows, and both are done by
        /// reusing plumbing that already exists rather than adding a parallel path:
        ///
        ///   * AutomationPeer.RaiseAutomationEvent drops every event unless EventMap has a listener
        ///     registered for it, so the events we forward are registered here;
        ///   * root peers are only built when a PresentationSource is (re)assigned its RootVisual
        ///     while EventMap.HasListeners -- which is exactly what EventMap.AddEvent triggers
        ///     through NotifySources. Registering the first event therefore also builds the tree.
        /// </summary>
        internal static void Activate()
        {
            if (IsActive) return;
            IsActive = true;

            AutomationTree.Current = s_instance;

            // Registering these opens the event gate AND, via EventMap.NotifySources, walks the live
            // PresentationSources so their root peers get created. The ids are the ones EventMap
            // recognises; anything else it ignores, so this list is the whole vocabulary we forward.
            EventMap.AddEvent(AutomationElementIdentifiers.AutomationFocusChangedEvent.Id);
            EventMap.AddEvent(AutomationElementIdentifiers.StructureChangedEvent.Id);
            EventMap.AddEvent(AutomationElementIdentifiers.AutomationPropertyChangedEvent.Id);
            EventMap.AddEvent(SelectionItemPatternIdentifiers.ElementSelectedEvent.Id);
        }

        // ---- node identity ---------------------------------------------------------
        //
        // Peers are held WEAKLY. An assistive technology keeps ids far longer than WPF keeps the
        // elements behind them -- a virtualised list discards peers as it scrolls -- and a strong
        // table here would turn the accessibility tree into a leak of the entire visual history.

        private static readonly ConditionalWeakTable<AutomationPeer, NodeId> s_idByPeer = new();
        private static readonly Dictionary<int, WeakReference<AutomationPeer>> s_peerById = new();
        private static readonly object s_lock = new object();
        private static int s_nextId;

        private sealed class NodeId
        {
            internal NodeId(int value) { Value = value; }
            internal int Value { get; }
        }

        /// <summary>The id for a peer, minting one on first sight.</summary>
        internal static int IdOf(AutomationPeer peer)
        {
            if (peer == null) return IAutomationTreeSource.InvalidNode;

            lock (s_lock)
            {
                if (s_idByPeer.TryGetValue(peer, out NodeId existing))
                {
                    return existing.Value;
                }

                // Ids are never reused. Reuse would let an assistive technology's stale reference
                // silently address a different element, which is worse than answering "gone".
                int id = ++s_nextId;
                s_idByPeer.Add(peer, new NodeId(id));
                s_peerById[id] = new WeakReference<AutomationPeer>(peer);

                // The dead-entry sweep is amortised here rather than run on a timer: the table only
                // grows when new nodes appear, so that is the only time it needs pruning.
                if ((id & 0xFF) == 0) PruneCollected();
                return id;
            }
        }

        /// <summary>The peer for an id, or null once it has been collected or never existed.</summary>
        private static AutomationPeer PeerOf(int id)
        {
            if (id <= 0) return null;

            lock (s_lock)
            {
                if (s_peerById.TryGetValue(id, out WeakReference<AutomationPeer> weak) &&
                    weak.TryGetTarget(out AutomationPeer peer))
                {
                    return peer;
                }
                return null;
            }
        }

        private static void PruneCollected()
        {
            List<int> dead = null;
            foreach (KeyValuePair<int, WeakReference<AutomationPeer>> entry in s_peerById)
            {
                if (!entry.Value.TryGetTarget(out _))
                {
                    (dead ??= new List<int>()).Add(entry.Key);
                }
            }

            if (dead == null) return;
            foreach (int id in dead) s_peerById.Remove(id);
        }

        // ---- tree ------------------------------------------------------------------

        public int GetRootId(IntPtr windowHandle)
        {
            AutomationPeer root = RootPeer(windowHandle);
            return root == null ? IAutomationTreeSource.InvalidNode : IdOf(root);
        }

        private static AutomationPeer RootPeer(IntPtr windowHandle)
        {
            foreach (PresentationSource source in PresentationSource.CriticalCurrentSources)
            {
                if (source.IsDisposed) continue;
                if (source is not HwndSource hwnd || hwnd.Handle != windowHandle) continue;
                if (hwnd.RootVisual is not Visual root) continue;

                return Safe(() => HwndTarget.EnsureAutomationPeer(root, windowHandle), null);
            }
            return null;
        }

        public int[] GetChildIds(int id)
        {
            AutomationPeer peer = PeerOf(id);
            if (peer == null) return Array.Empty<int>();

            List<AutomationPeer> children = Safe(peer.GetChildren, null);
            if (children == null || children.Count == 0) return Array.Empty<int>();

            var ids = new int[children.Count];
            for (int i = 0; i < children.Count; i++) ids[i] = IdOf(children[i]);
            return ids;
        }

        public int GetParentId(int id)
        {
            AutomationPeer peer = PeerOf(id);
            if (peer == null) return IAutomationTreeSource.InvalidNode;

            AutomationPeer parent = Safe(peer.GetParent, null);
            return parent == null ? IAutomationTreeSource.InvalidNode : IdOf(parent);
        }

        // ---- properties ------------------------------------------------------------

        public int GetRole(int id)
        {
            AutomationPeer peer = PeerOf(id);
            return peer == null ? -1 : (int)Safe(peer.GetAutomationControlType, AutomationControlType.Custom);
        }

        public string GetName(int id) => Text(PeerOf(id), static p => p.GetName());

        public string GetAutomationId(int id) => Text(PeerOf(id), static p => p.GetAutomationId());

        public string GetHelpText(int id) => Text(PeerOf(id), static p => p.GetHelpText());

        public string GetValue(int id)
        {
            AutomationPeer peer = PeerOf(id);
            if (peer == null) return string.Empty;

            if (Pattern<IValueProvider>(peer, PatternInterface.Value) is IValueProvider value)
            {
                return Safe(() => value.Value, null) ?? string.Empty;
            }

            if (Pattern<IRangeValueProvider>(peer, PatternInterface.RangeValue) is IRangeValueProvider range)
            {
                return Safe(() => range.Value.ToString(System.Globalization.CultureInfo.CurrentCulture), null)
                       ?? string.Empty;
            }

            return string.Empty;
        }

        public bool TryGetRange(int id, out double value, out double minimum, out double maximum)
        {
            value = minimum = maximum = 0;

            AutomationPeer peer = PeerOf(id);
            if (peer == null) return false;
            if (Pattern<IRangeValueProvider>(peer, PatternInterface.RangeValue) is not IRangeValueProvider range)
            {
                return false;
            }

            double v = 0, lo = 0, hi = 0;
            if (!Safe(() => { v = range.Value; lo = range.Minimum; hi = range.Maximum; return true; }, false))
            {
                return false;
            }

            value = v; minimum = lo; maximum = hi;
            return true;
        }

        public AccessibleState GetState(int id)
        {
            AutomationPeer peer = PeerOf(id);
            if (peer == null) return AccessibleState.None;

            AccessibleState state = AccessibleState.None;

            if (Safe(peer.IsEnabled, true)) state |= AccessibleState.Enabled;
            if (Safe(peer.IsKeyboardFocusable, false)) state |= AccessibleState.Focusable;
            if (Safe(peer.HasKeyboardFocus, false)) state |= AccessibleState.Focused;
            if (Safe(peer.IsOffscreen, false)) state |= AccessibleState.Offscreen;
            if (Safe(peer.IsPassword, false)) state |= AccessibleState.Password;

            if (Pattern<IToggleProvider>(peer, PatternInterface.Toggle) is IToggleProvider toggle)
            {
                switch (Safe(() => toggle.ToggleState, ToggleState.Off))
                {
                    case ToggleState.On: state |= AccessibleState.Checked; break;
                    case ToggleState.Indeterminate: state |= AccessibleState.Indeterminate; break;
                }
            }

            if (Pattern<IExpandCollapseProvider>(peer, PatternInterface.ExpandCollapse) is IExpandCollapseProvider expand)
            {
                switch (Safe(() => expand.ExpandCollapseState, ExpandCollapseState.LeafNode))
                {
                    case ExpandCollapseState.Expanded:
                    case ExpandCollapseState.PartiallyExpanded:
                        state |= AccessibleState.Expanded;
                        break;
                    case ExpandCollapseState.Collapsed:
                        state |= AccessibleState.Collapsed;
                        break;
                }
            }

            if (Pattern<ISelectionItemProvider>(peer, PatternInterface.SelectionItem) is ISelectionItemProvider item)
            {
                state |= AccessibleState.Selectable;
                if (Safe(() => item.IsSelected, false)) state |= AccessibleState.Selected;
            }

            if (Pattern<IValueProvider>(peer, PatternInterface.Value) is IValueProvider value)
            {
                state |= AccessibleState.HasValue;
                if (Safe(() => value.IsReadOnly, false)) state |= AccessibleState.ReadOnly;
            }

            if (Pattern<IRangeValueProvider>(peer, PatternInterface.RangeValue) is not null)
            {
                state |= AccessibleState.HasRange;
            }

            return state;
        }

        public bool TryGetBounds(int id, out double x, out double y, out double width, out double height)
        {
            x = y = width = height = 0;

            AutomationPeer peer = PeerOf(id);
            if (peer == null) return false;

            Rect rect = Safe(peer.GetBoundingRectangle, Rect.Empty);
            if (rect.IsEmpty || rect.Width <= 0 || rect.Height <= 0) return false;

            x = rect.X; y = rect.Y; width = rect.Width; height = rect.Height;
            return true;
        }

        public int HitTest(IntPtr windowHandle, double screenX, double screenY)
        {
            AutomationPeer root = RootPeer(windowHandle);
            if (root == null) return IAutomationTreeSource.InvalidNode;

            // Depth-first, deepest hit wins: an AT wants the innermost element under the point, and
            // the peer tree is small enough at any one level that this stays cheap.
            AutomationPeer hit = Deepest(root, screenX, screenY, depth: 0);
            return hit == null ? IAutomationTreeSource.InvalidNode : IdOf(hit);
        }

        private static AutomationPeer Deepest(AutomationPeer peer, double x, double y, int depth)
        {
            // Guard against a peer tree that cycles through a badly-behaved custom peer; an AT hit
            // test must not be able to hang the UI thread.
            if (depth > 64) return null;

            Rect rect = Safe(peer.GetBoundingRectangle, Rect.Empty);
            if (rect.IsEmpty || !rect.Contains(x, y)) return null;

            List<AutomationPeer> children = Safe(peer.GetChildren, null);
            if (children != null)
            {
                for (int i = children.Count - 1; i >= 0; i--)
                {
                    AutomationPeer child = Deepest(children[i], x, y, depth + 1);
                    if (child != null) return child;
                }
            }

            return peer;
        }

        public int GetFocusedId(IntPtr windowHandle)
        {
            AutomationPeer root = RootPeer(windowHandle);
            if (root == null) return IAutomationTreeSource.InvalidNode;

            AutomationPeer focused = FindFocused(root, depth: 0);
            return focused == null ? IAutomationTreeSource.InvalidNode : IdOf(focused);
        }

        private static AutomationPeer FindFocused(AutomationPeer peer, int depth)
        {
            if (depth > 64) return null;
            if (Safe(peer.HasKeyboardFocus, false)) return peer;

            List<AutomationPeer> children = Safe(peer.GetChildren, null);
            if (children == null) return null;

            foreach (AutomationPeer child in children)
            {
                AutomationPeer found = FindFocused(child, depth + 1);
                if (found != null) return found;
            }
            return null;
        }

        // ---- actions ---------------------------------------------------------------

        public bool SupportsAction(int id, AccessibleAction action)
        {
            AutomationPeer peer = PeerOf(id);
            if (peer == null) return false;

            return action switch
            {
                AccessibleAction.Invoke => Pattern<IInvokeProvider>(peer, PatternInterface.Invoke) is not null,
                AccessibleAction.Toggle => Pattern<IToggleProvider>(peer, PatternInterface.Toggle) is not null,
                AccessibleAction.Expand or AccessibleAction.Collapse
                    => Pattern<IExpandCollapseProvider>(peer, PatternInterface.ExpandCollapse) is not null,
                AccessibleAction.Select or AccessibleAction.ToggleSelection
                    => Pattern<ISelectionItemProvider>(peer, PatternInterface.SelectionItem) is not null,
                _ => false,
            };
        }

        public bool DoAction(int id, AccessibleAction action)
        {
            AutomationPeer peer = PeerOf(id);
            if (peer == null) return false;

            switch (action)
            {
                case AccessibleAction.Invoke:
                    return Pattern<IInvokeProvider>(peer, PatternInterface.Invoke) is IInvokeProvider invoke
                           && Safe(() => { invoke.Invoke(); return true; }, false);

                case AccessibleAction.Toggle:
                    return Pattern<IToggleProvider>(peer, PatternInterface.Toggle) is IToggleProvider toggle
                           && Safe(() => { toggle.Toggle(); return true; }, false);

                case AccessibleAction.Expand:
                    return Pattern<IExpandCollapseProvider>(peer, PatternInterface.ExpandCollapse) is IExpandCollapseProvider e
                           && Safe(() => { e.Expand(); return true; }, false);

                case AccessibleAction.Collapse:
                    return Pattern<IExpandCollapseProvider>(peer, PatternInterface.ExpandCollapse) is IExpandCollapseProvider c
                           && Safe(() => { c.Collapse(); return true; }, false);

                case AccessibleAction.Select:
                    return Pattern<ISelectionItemProvider>(peer, PatternInterface.SelectionItem) is ISelectionItemProvider s
                           && Safe(() => { s.Select(); return true; }, false);

                case AccessibleAction.ToggleSelection:
                    return Pattern<ISelectionItemProvider>(peer, PatternInterface.SelectionItem) is ISelectionItemProvider t
                           && Safe(() =>
                           {
                               if (t.IsSelected) t.RemoveFromSelection(); else t.AddToSelection();
                               return true;
                           }, false);

                default:
                    return false;
            }
        }

        public bool SetValue(int id, string value)
        {
            AutomationPeer peer = PeerOf(id);
            if (peer == null) return false;

            return Pattern<IValueProvider>(peer, PatternInterface.Value) is IValueProvider provider
                   && Safe(() => { provider.SetValue(value ?? string.Empty); return true; }, false);
        }

        public bool SetFocus(int id)
        {
            AutomationPeer peer = PeerOf(id);
            return peer != null && Safe(() => { peer.SetFocus(); return true; }, false);
        }

        // ---- notifications out -----------------------------------------------------

        /// <summary>
        /// Called from AutomationPeer's event paths. Kept as one entry point so the peers do not need
        /// to know which (if any) platform is listening.
        /// </summary>
        internal static void Notify(AutomationPeer peer, AutomationChangeKind kind)
        {
            if (!IsActive || peer == null) return;

            IntPtr window = Safe(() => peer.Hwnd, IntPtr.Zero);
            AutomationTree.RaiseChanged(new AutomationChange(kind, IdOf(peer), window));
        }

        /// <summary>
        /// Maps one of WPF's automation events onto the small vocabulary the backends understand.
        /// Events outside that vocabulary are dropped here rather than at each backend: a screen
        /// reader that is told about everything announces over itself.
        /// </summary>
        internal static void NotifyEvent(AutomationPeer peer, AutomationEvents eventId)
        {
            if (!IsActive) return;

            switch (eventId)
            {
                case AutomationEvents.AutomationFocusChanged:
                    Notify(peer, AutomationChangeKind.FocusChanged);
                    break;
                case AutomationEvents.SelectionItemPatternOnElementSelected:
                case AutomationEvents.SelectionItemPatternOnElementAddedToSelection:
                case AutomationEvents.SelectionItemPatternOnElementRemovedFromSelection:
                    Notify(peer, AutomationChangeKind.SelectionChanged);
                    break;
                case AutomationEvents.StructureChanged:
                    Notify(peer, AutomationChangeKind.ChildrenChanged);
                    break;
                case AutomationEvents.PropertyChanged:
                    Notify(peer, AutomationChangeKind.PropertyChanged);
                    break;
            }
        }

        /// <summary>
        /// A property changed. Value gets its own change kind because every platform has a cheaper,
        /// less chatty notification for it than for "something about this node changed", and a
        /// slider or progress bar can raise this many times a second.
        /// </summary>
        internal static void NotifyPropertyChanged(AutomationPeer peer, AutomationProperty property)
        {
            if (!IsActive) return;

            bool isValue = property == ValuePatternIdentifiers.ValueProperty
                        || property == RangeValuePatternIdentifiers.ValueProperty;

            Notify(peer, isValue ? AutomationChangeKind.ValueChanged : AutomationChangeKind.PropertyChanged);
        }

        // ---- helpers ---------------------------------------------------------------

        /// <summary>
        /// A peer's pattern provider, or null. Custom peers are app code running inside what is
        /// effectively a callback from the OS, so a throw here has to become "no pattern".
        /// </summary>
        private static T Pattern<T>(AutomationPeer peer, PatternInterface which) where T : class
            => Safe(() => peer.GetPattern(which) as T, null);

        private static string Text(AutomationPeer peer, Func<AutomationPeer, string> get)
            => peer == null ? string.Empty : (Safe(() => get(peer), null) ?? string.Empty);

        /// <summary>
        /// Runs a peer call and swallows failure, because every caller of this class is an
        /// assistive technology on the other side of a native callback. A custom AutomationPeer that
        /// throws is an app bug; letting it unwind into AppKit, the D-Bus dispatcher or a JNI frame
        /// turns that bug into a process abort with no managed stack to explain it.
        /// </summary>
        private static T Safe<T>(Func<T> action, T fallback)
        {
            try { return action(); }
            catch (Exception e) when (!IsFatal(e)) { return fallback; }
        }

        private static bool IsFatal(Exception e)
            => e is OutOfMemoryException || e is StackOverflowException || e is System.Threading.ThreadAbortException;
    }
}
