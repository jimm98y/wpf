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
using System.Windows.Automation.Text;
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
            EventMap.AddEvent(SelectionPatternIdentifiers.InvalidatedEvent.Id);
            EventMap.AddEvent(TextPatternIdentifiers.TextSelectionChangedEvent.Id);
            EventMap.AddEvent(TextPatternIdentifiers.TextChangedEvent.Id);
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

        public string GetAccessKey(int id) => Text(PeerOf(id), static p => p.GetAccessKey());

        public string GetAcceleratorKey(int id) => Text(PeerOf(id), static p => p.GetAcceleratorKey());

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

            if (Pattern<ITextProvider>(peer, PatternInterface.Text) is not null) state |= AccessibleState.HasText;
            if (Pattern<ISelectionProvider>(peer, PatternInterface.Selection) is ISelectionProvider selection)
            {
                state |= AccessibleState.SelectionContainer;
                if (Safe(() => selection.CanSelectMultiple, false)) state |= AccessibleState.MultiSelectable;
            }
            if (Pattern<IGridProvider>(peer, PatternInterface.Grid) is not null) state |= AccessibleState.Grid;
            if (Pattern<ITableProvider>(peer, PatternInterface.Table) is not null) state |= AccessibleState.Table;
            if (Pattern<IGridItemProvider>(peer, PatternInterface.GridItem) is not null) state |= AccessibleState.GridItem;
            if (Pattern<IScrollProvider>(peer, PatternInterface.Scroll) is not null) state |= AccessibleState.Scrollable;
            if (Pattern<IScrollItemProvider>(peer, PatternInterface.ScrollItem) is not null) state |= AccessibleState.ScrollItem;
            if (Pattern<IWindowProvider>(peer, PatternInterface.Window) is IWindowProvider window)
            {
                state |= AccessibleState.Window;
                if (Safe(() => window.IsModal, false)) state |= AccessibleState.Modal;
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

        // ---- text -----------------------------------------------------------------
        //
        // A Text pattern speaks in ranges; the platforms speak in offsets into the text. An offset is
        // the length of the text from the document's start to a range's endpoint. The other way,
        // a range is moved by characters -- UIA's characters are insertion positions, and a line
        // break ("\r\n") is one of them but two code units, so the move is corrected against the
        // text it covers until the offset is reached.

        private static ITextProvider TextOf(int id) => PeerOf(id) is AutomationPeer p ? Pattern<ITextProvider>(p, PatternInterface.Text) : null;

        private static int OffsetOf(ITextProvider text, ITextRangeProvider range, TextPatternRangeEndpoint endpoint)
        {
            ITextRangeProvider head = text.DocumentRange.Clone();
            head.MoveEndpointByRange(TextPatternRangeEndpoint.End, range, endpoint);
            return head.GetText(-1).Length;
        }

        /// <summary>A degenerate range at <paramref name="offset"/>.</summary>
        private static ITextRangeProvider RangeAt(ITextProvider text, int offset)
        {
            ITextRangeProvider r = text.DocumentRange.Clone();
            r.MoveEndpointByRange(TextPatternRangeEndpoint.End, r, TextPatternRangeEndpoint.Start);
            // a character move covers one or two code units: half the distance left never overshoots
            int at = 0;
            for (int guard = 0; at < offset && guard < 128; guard++)
            {
                int moved = r.MoveEndpointByUnit(TextPatternRangeEndpoint.End, TextUnit.Character, Math.Max(1, (offset - at) / 2));
                if (moved == 0) break;
                at = r.GetText(-1).Length;
            }
            r.MoveEndpointByRange(TextPatternRangeEndpoint.Start, r, TextPatternRangeEndpoint.End);
            return r;
        }

        private static ITextRangeProvider RangeOf(ITextProvider text, int start, int length)
        {
            ITextRangeProvider r = RangeAt(text, start);
            if (length > 0) r.MoveEndpointByRange(TextPatternRangeEndpoint.End, RangeAt(text, start + length), TextPatternRangeEndpoint.Start);
            return r;
        }

        public bool TryGetText(int id, out string text)
        {
            text = string.Empty;
            if (TextOf(id) is not ITextProvider t) return false;
            text = Safe(() => t.DocumentRange.GetText(-1), null) ?? string.Empty;
            return true;
        }

        public bool TryGetTextSelection(int id, out int start, out int length)
        {
            start = length = 0;
            if (TextOf(id) is not ITextProvider t) return false;
            int s0 = 0, n = 0;
            bool ok = Safe(() =>
            {
                ITextRangeProvider[] sel = t.GetSelection();
                if (sel == null || sel.Length == 0) return false;
                s0 = OffsetOf(t, sel[0], TextPatternRangeEndpoint.Start);
                n = sel[0].GetText(-1).Length;
                return true;
            }, false);
            start = s0; length = n;
            return ok;
        }

        public bool SetTextSelection(int id, int start, int length)
        {
            if (TextOf(id) is not ITextProvider t || start < 0 || length < 0) return false;
            return Safe(() => { RangeOf(t, start, length).Select(); return true; }, false);
        }

        public bool TryGetTextRangeBounds(int id, int start, int length, out double x, out double y, out double width, out double height)
        {
            x = y = width = height = 0;
            if (TextOf(id) is not ITextProvider t) return false;
            Rect union = Rect.Empty;
            Safe(() =>
            {
                ITextRangeProvider r = RangeOf(t, start, Math.Max(length, 0));
                // a caret (or a range of nothing) has no rectangles: the character after it, as narrow as a caret
                bool caret = length <= 0;
                if (caret) r.MoveEndpointByUnit(TextPatternRangeEndpoint.End, TextUnit.Character, 1);
                double[] rects = r.GetBoundingRectangles();
                for (int i = 0; rects != null && i + 3 < rects.Length; i += 4)
                    union.Union(new Rect(rects[i], rects[i + 1], caret ? 1 : rects[i + 2], rects[i + 3]));
                return true;
            }, false);
            if (union.IsEmpty) return false;
            x = union.X; y = union.Y; width = union.Width; height = union.Height;
            return true;
        }

        public bool TryGetTextLine(int id, int offset, out int lineStart, out int lineLength, out int lineNumber)
        {
            lineStart = lineLength = lineNumber = 0;
            if (TextOf(id) is not ITextProvider t) return false;
            int s0 = 0, n = 0, number = 0;
            bool ok = Safe(() =>
            {
                ITextRangeProvider line = RangeAt(t, Math.Max(offset, 0));
                line.ExpandToEnclosingUnit(TextUnit.Line);
                s0 = OffsetOf(t, line, TextPatternRangeEndpoint.Start);
                n = line.GetText(-1).Length;
                // the lines before it: the range from the line back to the document's start, by lines
                ITextRangeProvider back = line.Clone();
                back.MoveEndpointByRange(TextPatternRangeEndpoint.End, back, TextPatternRangeEndpoint.Start);
                number = -back.MoveEndpointByUnit(TextPatternRangeEndpoint.Start, TextUnit.Line, -1_000_000);
                return true;
            }, false);
            // lines are layout's, and only for text in view: without it (unchanged ranges, no lines
            // counted), the text's own line breaks
            string all = ok ? Safe(() => t.DocumentRange.GetText(-1), null) ?? string.Empty : string.Empty;
            if (ok && offset <= all.Length && (n <= 1 && HardLine(all, offset).Length > n || number == 0 && s0 > 0))
            {
                (s0, n, number) = HardLine(all, offset);
            }
            lineStart = s0; lineLength = n; lineNumber = number;
            return ok;
        }

        /// <summary>The line of <paramref name="text"/> the offset is in, by its breaks (\r\n, \n, \r): its start,
        /// its length with the break, and its number.</summary>
        private static (int Start, int Length, int Number) HardLine(string text, int offset)
        {
            int start = 0, number = 0;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c != '\n' && c != '\r') continue;
                int next = c == '\r' && i + 1 < text.Length && text[i + 1] == '\n' ? i + 2 : i + 1;
                if (offset < next) return (start, next - start, number);
                start = next;
                number++;
                i = next - 1;
            }
            return (start, text.Length - start, number);
        }

        public int GetTextOffsetAtPoint(int id, double screenX, double screenY)
        {
            if (TextOf(id) is not ITextProvider t) return -1;
            return Safe(() => t.RangeFromPoint(new Point(screenX, screenY)) is ITextRangeProvider r ? OffsetOf(t, r, TextPatternRangeEndpoint.Start) : -1, -1);
        }

        public bool TryGetVisibleTextRange(int id, out int start, out int length)
        {
            start = length = 0;
            if (TextOf(id) is not ITextProvider t) return false;
            int s0 = 0, e0 = 0;
            bool ok = Safe(() =>
            {
                ITextRangeProvider[] visible = t.GetVisibleRanges();
                if (visible == null || visible.Length == 0) return false;
                s0 = OffsetOf(t, visible[0], TextPatternRangeEndpoint.Start);
                e0 = OffsetOf(t, visible[^1], TextPatternRangeEndpoint.End);
                return true;
            }, false);
            start = s0; length = Math.Max(0, e0 - s0);
            return ok;
        }

        // ---- selection, grids, tables ------------------------------------------------

        /// <summary>The node of a provider a pattern hands back (an element of WPF's: its peer's wrapper).</summary>
        private static int IdOfProvider(IRawElementProviderSimple provider)
            => provider is ElementProxy proxy && proxy.Peer is AutomationPeer peer ? IdOf(peer) : IAutomationTreeSource.InvalidNode;

        private static int[] IdsOf(IRawElementProviderSimple[] providers)
        {
            if (providers == null || providers.Length == 0) return Array.Empty<int>();
            var ids = new List<int>(providers.Length);
            foreach (IRawElementProviderSimple p in providers)
            {
                int id = IdOfProvider(p);
                if (id != IAutomationTreeSource.InvalidNode) ids.Add(id);
            }
            return ids.ToArray();
        }

        public int[] GetSelectedIds(int id)
        {
            AutomationPeer peer = PeerOf(id);
            if (peer == null || Pattern<ISelectionProvider>(peer, PatternInterface.Selection) is not ISelectionProvider s) return Array.Empty<int>();
            return Safe(() => IdsOf(s.GetSelection()), Array.Empty<int>());
        }

        public bool TryGetGrid(int id, out int rows, out int columns)
        {
            rows = columns = 0;
            AutomationPeer peer = PeerOf(id);
            if (peer == null || Pattern<IGridProvider>(peer, PatternInterface.Grid) is not IGridProvider g) return false;
            int r = 0, c = 0;
            bool ok = Safe(() => { r = g.RowCount; c = g.ColumnCount; return true; }, false);
            rows = r; columns = c;
            return ok;
        }

        public int GetGridCell(int id, int row, int column)
        {
            AutomationPeer peer = PeerOf(id);
            if (peer == null || Pattern<IGridProvider>(peer, PatternInterface.Grid) is not IGridProvider g) return IAutomationTreeSource.InvalidNode;
            return Safe(() => IdOfProvider(g.GetItem(row, column)), IAutomationTreeSource.InvalidNode);
        }

        public bool TryGetGridItem(int id, out int row, out int column, out int rowSpan, out int columnSpan, out int gridId)
        {
            row = column = rowSpan = columnSpan = 0;
            gridId = IAutomationTreeSource.InvalidNode;
            AutomationPeer peer = PeerOf(id);
            if (peer == null || Pattern<IGridItemProvider>(peer, PatternInterface.GridItem) is not IGridItemProvider g) return false;
            int r = 0, c = 0, rs = 0, cs = 0, grid = IAutomationTreeSource.InvalidNode;
            bool ok = Safe(() => { r = g.Row; c = g.Column; rs = g.RowSpan; cs = g.ColumnSpan; grid = IdOfProvider(g.ContainingGrid); return true; }, false);
            row = r; column = c; rowSpan = rs; columnSpan = cs; gridId = grid;
            return ok;
        }

        public int[] GetColumnHeaderIds(int id)
        {
            AutomationPeer peer = PeerOf(id);
            if (peer == null || Pattern<ITableProvider>(peer, PatternInterface.Table) is not ITableProvider t) return Array.Empty<int>();
            return Safe(() => IdsOf(t.GetColumnHeaders()), Array.Empty<int>());
        }

        public int[] GetRowHeaderIds(int id)
        {
            AutomationPeer peer = PeerOf(id);
            if (peer == null || Pattern<ITableProvider>(peer, PatternInterface.Table) is not ITableProvider t) return Array.Empty<int>();
            return Safe(() => IdsOf(t.GetRowHeaders()), Array.Empty<int>());
        }

        // ---- scrolling ---------------------------------------------------------------

        public bool TryGetScroll(int id, out double horizontalPercent, out double verticalPercent, out double horizontalViewSize,
            out double verticalViewSize, out bool horizontallyScrollable, out bool verticallyScrollable)
        {
            horizontalPercent = verticalPercent = -1; horizontalViewSize = verticalViewSize = 100;
            horizontallyScrollable = verticallyScrollable = false;
            AutomationPeer peer = PeerOf(id);
            if (peer == null || Pattern<IScrollProvider>(peer, PatternInterface.Scroll) is not IScrollProvider s) return false;
            double hp = -1, vp = -1, hv = 100, vv = 100;
            bool hs = false, vs = false;
            bool ok = Safe(() =>
            {
                hp = s.HorizontalScrollPercent; vp = s.VerticalScrollPercent;
                hv = s.HorizontalViewSize; vv = s.VerticalViewSize;
                hs = s.HorizontallyScrollable; vs = s.VerticallyScrollable;
                return true;
            }, false);
            horizontalPercent = hp; verticalPercent = vp; horizontalViewSize = hv; verticalViewSize = vv;
            horizontallyScrollable = hs; verticallyScrollable = vs;
            return ok;
        }

        public bool Scroll(int id, int horizontalAmount, int verticalAmount)
        {
            AutomationPeer peer = PeerOf(id);
            if (peer == null || Pattern<IScrollProvider>(peer, PatternInterface.Scroll) is not IScrollProvider s) return false;
            if (horizontalAmount is < 0 or > 4 || verticalAmount is < 0 or > 4) return false;
            return Safe(() =>
            {
                // a direction that cannot scroll is left alone (the provider throws when asked to move it)
                var h = s.HorizontallyScrollable ? (ScrollAmount)horizontalAmount : ScrollAmount.NoAmount;
                var v = s.VerticallyScrollable ? (ScrollAmount)verticalAmount : ScrollAmount.NoAmount;
                if (h == ScrollAmount.NoAmount && v == ScrollAmount.NoAmount) return false;
                s.Scroll(h, v);
                return true;
            }, false);
        }

        public bool SetScrollPercent(int id, double horizontalPercent, double verticalPercent)
        {
            AutomationPeer peer = PeerOf(id);
            if (peer == null || Pattern<IScrollProvider>(peer, PatternInterface.Scroll) is not IScrollProvider s) return false;
            return Safe(() =>
            {
                double h = s.HorizontallyScrollable && horizontalPercent >= 0 ? Math.Min(horizontalPercent, 100) : ScrollPatternIdentifiers.NoScroll;
                double v = s.VerticallyScrollable && verticalPercent >= 0 ? Math.Min(verticalPercent, 100) : ScrollPatternIdentifiers.NoScroll;
                s.SetScrollPercent(h, v);
                return true;
            }, false);
        }

        public bool ScrollIntoView(int id)
        {
            AutomationPeer peer = PeerOf(id);
            if (peer == null || Pattern<IScrollItemProvider>(peer, PatternInterface.ScrollItem) is not IScrollItemProvider s) return false;
            return Safe(() => { s.ScrollIntoView(); return true; }, false);
        }

        // ---- windows -----------------------------------------------------------------

        public bool TryGetWindow(int id, out int visualState, out bool canMinimize, out bool canMaximize, out bool isTopmost)
        {
            visualState = 0; canMinimize = canMaximize = isTopmost = false;
            AutomationPeer peer = PeerOf(id);
            if (peer == null || Pattern<IWindowProvider>(peer, PatternInterface.Window) is not IWindowProvider w) return false;
            int vs = 0;
            bool min = false, max = false, top = false;
            bool ok = Safe(() => { vs = (int)w.VisualState; min = w.Minimizable; max = w.Maximizable; top = w.IsTopmost; return true; }, false);
            visualState = vs; canMinimize = min; canMaximize = max; isTopmost = top;
            return ok;
        }

        public bool SetWindowVisualState(int id, int visualState)
        {
            AutomationPeer peer = PeerOf(id);
            if (peer == null || visualState is < 0 or > 2 || Pattern<IWindowProvider>(peer, PatternInterface.Window) is not IWindowProvider w) return false;
            return Safe(() => { w.SetVisualState((WindowVisualState)visualState); return true; }, false);
        }

        public bool CloseWindow(int id)
        {
            AutomationPeer peer = PeerOf(id);
            if (peer == null || Pattern<IWindowProvider>(peer, PatternInterface.Window) is not IWindowProvider w) return false;
            return Safe(() => { w.Close(); return true; }, false);
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
                case AutomationEvents.TextPatternOnTextSelectionChanged:
                    Notify(peer, AutomationChangeKind.TextSelectionChanged);
                    break;
                case AutomationEvents.TextPatternOnTextChanged:
                    Notify(peer, AutomationChangeKind.TextChanged);
                    break;
                case AutomationEvents.SelectionPatternOnInvalidated:
                    Notify(peer, AutomationChangeKind.SelectionContainerChanged);
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

            Notify(peer, KindOf(property));
        }

        /// <summary>
        /// Which change kind a UIA property maps to.
        ///
        /// Every one of these is a DIFFERENT announcement to the user -- "expanded", "checked",
        /// "dimmed", a new name -- so folding them into one generic "property changed" throws away
        /// the only thing the backend needed. Anything not listed is genuinely generic: the backends
        /// re-read the node rather than announce a specific transition.
        /// </summary>
        private static AutomationChangeKind KindOf(AutomationProperty property)
        {
            if (property == ValuePatternIdentifiers.ValueProperty
             || property == RangeValuePatternIdentifiers.ValueProperty)
            {
                return AutomationChangeKind.ValueChanged;
            }
            if (property == AutomationElementIdentifiers.NameProperty)
            {
                return AutomationChangeKind.NameChanged;
            }
            if (property == AutomationElementIdentifiers.HelpTextProperty)
            {
                return AutomationChangeKind.DescriptionChanged;
            }
            if (property == ExpandCollapsePatternIdentifiers.ExpandCollapseStateProperty)
            {
                return AutomationChangeKind.ExpandedChanged;
            }
            if (property == TogglePatternIdentifiers.ToggleStateProperty)
            {
                return AutomationChangeKind.CheckedChanged;
            }
            if (property == AutomationElementIdentifiers.IsEnabledProperty)
            {
                return AutomationChangeKind.EnabledChanged;
            }
            if (property == AutomationElementIdentifiers.IsOffscreenProperty)
            {
                return AutomationChangeKind.OffscreenChanged;
            }
            if (property == SelectionItemPatternIdentifiers.IsSelectedProperty)
            {
                return AutomationChangeKind.SelectionChanged;
            }
            if (property == SelectionPatternIdentifiers.SelectionProperty)
            {
                return AutomationChangeKind.SelectionContainerChanged;
            }
            if (property == ScrollPatternIdentifiers.HorizontalScrollPercentProperty
             || property == ScrollPatternIdentifiers.VerticalScrollPercentProperty
             || property == ScrollPatternIdentifiers.HorizontalViewSizeProperty
             || property == ScrollPatternIdentifiers.VerticalViewSizeProperty)
            {
                return AutomationChangeKind.ScrollChanged;
            }

            return AutomationChangeKind.PropertyChanged;
        }

        /// <summary>
        /// One child appeared or disappeared. Reported separately from
        /// <see cref="AutomationChangeKind.ChildrenChanged"/>, which is WPF's BULK notification: a
        /// list gaining one row is the common case and the platform notifications for it carry the
        /// child and its index, which "something under here changed" cannot express.
        ///
        /// <paramref name="parent"/> is the addressed node in both directions, including removal --
        /// by then the child's peer is already detached, so it is the only node an assistive
        /// technology can still navigate from.
        /// </summary>
        internal static void NotifyChildChanged(AutomationPeer parent, AutomationPeer child,
                                                bool added, int index)
        {
            if (!IsActive || parent == null || child == null) return;

            IntPtr window = Safe(() => parent.Hwnd, IntPtr.Zero);
            AutomationTree.RaiseChanged(new AutomationChange(
                added ? AutomationChangeKind.ChildAdded : AutomationChangeKind.ChildRemoved,
                IdOf(parent), window, IdOf(child), index));
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
