// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The iOS accessibility backend: WPF's automation tree, exposed through UIAccessibility.
//
// Three things make this different from the macOS backend next door, and all three are UIKit's
// doing rather than ours:
//
//   * UIKit's tree is shallow by design. A UIAccessibilityContainer exposes an indexed list that
//     VoiceOver swipes along, and an element of that list may itself be a container -- a list, a
//     toolbar, a named group -- which VoiceOver announces on the way in ("list start", "toolbar")
//     and lets the user step over or into with the containers rotor. So the peer tree is
//     condensed rather than mirrored: only nodes worth stopping on are listed, a StackPanel that
//     exists purely to lay out three buttons disappears into its parent, and the containers a user
//     would recognise (UIA's lists, menus, toolbars, tabs, tables, and groups and panes with a
//     name) keep their own level.
//   * Roles are a BITMASK, not a name. UIAccessibilityTraits describes what an element does
//     (button, adjustable, selected, header) rather than what it is, so the mapping is many-to-many
//     and a checkbox is "button + selected" rather than a checkbox.
//   * IMPs must be [UnmanagedCallersOnly] static function pointers. iOS is AOT-only and cannot
//     build a native-to-managed thunk at run time, which is why the macOS file's managed-delegate
//     approach cannot be copied here. That in turn means no closures, no instance state, and shared
//     logic in ordinary methods the IMPs call -- an UnmanagedCallersOnly method cannot be called
//     from managed code at all.
//
// Coordinates are the easy part for once: UIKit measures in points from the top left, exactly like
// WPF, so the conversion is a divide by the screen scale with no flip.
//
// The richer patterns map onto the informal protocols UIKit's own controls answer:
//
//   * Text is UIAccessibilityReadingContent (VoiceOver's line-by-line reading) and the text as the
//     value. Editing is not something an accessibility element can do in UIKit -- the caret and the
//     selection belong to the UITextInput first responder -- so a focused editor hands VoiceOver the
//     WPF view, which already IS that responder (UIKitTextInput), through
//     accessibilityTextInputResponder. There is no public trait for "text field"; UITextField's comes
//     from UIKit's private accessibility bundle, so an editor here is announced by its value alone.
//   * A Grid is a UIAccessibilityContainerDataTable and its items UIAccessibilityContainerDataTableCell.
//     The rows stay the elements VoiceOver swipes through (they are what WPF selects and activates);
//     the cells are reached through the table protocol, which VoiceOver uses to move by row and column.
//   * Scrolling is accessibilityScroll:, answered by the nearest scrolling ancestor in the TREE: the
//     scroll viewer itself is layout scaffolding and is never listed, so it is never the element
//     VoiceOver sends the gesture to.
//   * A modal window's view is accessibilityViewIsModal, and Escape (the two-finger Z) closes it.
//
// Every element answers every one of those selectors, so -respondsToSelector: and
// -conformsToProtocol: are narrowed per node: VoiceOver decides that an element is a table, or has
// lines to read, by asking exactly those questions, and a button must not claim to be a page of text.
//

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace MS.Internal.Interop
{
    [SupportedOSPlatform("ios")]
    internal static unsafe class UIKitAccessibility
    {
        internal static bool IsAvailable => OperatingSystem.IsIOS() || OperatingSystem.IsMacCatalyst();

        /// <summary>True once VoiceOver (or Switch Control) has asked us anything.</summary>
        internal static bool IsAttached { get; private set; }

        private static IntPtr s_elementClass;
        private static nint s_nodeIdOffset;

        // The condensed tree: each window view's own list, and each nested container's. VoiceOver
        // asks for a count and then walks indices, so the lists must stay stable across that walk;
        // they are rebuilt when the tree says the structure changed, not per query. A node's parent
        // is the nested container it is listed in, or ViewParent for its view's own list (s_viewOf).
        // One window fills the screen and the others float over it, each a view of its own.
        private const int ViewParent = -1;
        private static readonly Dictionary<IntPtr, List<int>> s_views = new();
        private static readonly HashSet<IntPtr> s_knownViews = new();
        private static readonly Dictionary<int, List<int>> s_containers = new();
        private static readonly Dictionary<int, int> s_parents = new();
        private static readonly Dictionary<int, IntPtr> s_viewOf = new();
        private static bool s_stale = true;

        // One element object per node, so VoiceOver's pointer-identity focus tracking works.
        private static readonly Dictionary<int, IntPtr> s_elements = new();

        private static IAutomationTreeSource Tree => AutomationTree.Current;

        // WPF_A11Y_LOG=1: what VoiceOver asked and what the condensed tree held, as the Linux backend traces
        private static readonly bool s_log = Environment.GetEnvironmentVariable("WPF_A11Y_LOG") == "1";

        private static void Trace(string message)
        {
            if (s_log) Console.WriteLine($"[a11y] {message}");
        }

        /// <summary>
        /// Adds the container selectors to a synthesised view class. Called from UIKitWindow while it
        /// builds its view classes, before objc_registerClassPair -- methods cannot be added later.
        /// </summary>
        internal static void AddViewAccessibility(IntPtr viewClass)
        {
            // Deliberately NOT creating the element class here. This runs BETWEEN
            // objc_allocateClassPair and objc_registerClassPair for the view class, and building a
            // second class pair inside that window segfaults the runtime during app startup. The
            // element class is created on first use instead, which is also when it is first needed
            // -- no assistive technology, no class.
            class_addMethod(viewClass, Sel("isAccessibilityElement"),
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, byte>)&ViewIsElementImp, "B@:");
            class_addMethod(viewClass, Sel("accessibilityElementCount"),
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, nint>)&ElementCountImp, "q@:");
            class_addMethod(viewClass, Sel("accessibilityElementAtIndex:"),
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, nint, IntPtr>)&ElementAtIndexImp, "@@:q");
            class_addMethod(viewClass, Sel("indexOfAccessibilityElement:"),
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, nint>)&IndexOfElementImp, "q@:@");

            // A WPF window is a view, so a modal dialog's modality is the view's: VoiceOver then stays
            // inside it and ignores the sibling views -- the windows the dialog is blocking.
            class_addMethod(viewClass, Sel("accessibilityViewIsModal"),
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, byte>)&ViewIsModalImp, "B@:");
            class_addMethod(viewClass, Sel("accessibilityPerformEscape"),
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, byte>)&ViewEscapeImp, "B@:");
        }

        /// <summary>
        /// The first accessibility question is the signal that an assistive technology exists -- UIKit
        /// does not ask otherwise. Same trigger as WM_GETOBJECT on Windows and the first
        /// NSAccessibility message on macOS, and the reason an app with no screen reader pays nothing.
        /// </summary>
        private static void NoteAccessibilityClient()
        {
            if (IsAttached) return;
            IsAttached = true;

            AutomationTree.RequestActivation();
            AutomationTree.Changed += OnTreeChanged;
        }

        // ---- the element class -----------------------------------------------------

        private static void EnsureElementClass()
        {
            if (s_elementClass != IntPtr.Zero) return;

            // UIAccessibilityElement already implements the container back-pointer and the plumbing
            // VoiceOver needs; subclassing it means only the answers have to be supplied.
            IntPtr baseClass = objc_getClass("UIAccessibilityElement");
            if (baseClass == IntPtr.Zero) return;   // not a UIKit process

            // The narrowed -respondsToSelector:/-conformsToProtocol: defer to these for everything
            // they do not gate. Taken from the base class, so they are never our own overrides.
            s_superRespondsToSelector = class_getMethodImplementation(baseClass, Sel("respondsToSelector:"));
            s_superConformsToProtocol = class_getMethodImplementation(baseClass, Sel("conformsToProtocol:"));
            BuildGates();

            IntPtr existing = objc_getClass("WpfAccessibilityElement");
            if (existing != IntPtr.Zero)
            {
                s_elementClass = existing;
                s_nodeIdOffset = ivar_getOffset(class_getInstanceVariable(existing, "_nodeId"));
                return;
            }

            IntPtr cls = objc_allocateClassPair(baseClass, "WpfAccessibilityElement", UIntPtr.Zero);
            if (cls == IntPtr.Zero) return;

            class_addIvar(cls, "_nodeId", (UIntPtr)8, 3, "q");

            class_addMethod(cls, Sel("isAccessibilityElement"),
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, byte>)&IsElementImp, "B@:");
            class_addMethod(cls, Sel("accessibilityLabel"),
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr>)&LabelImp, "@@:");
            class_addMethod(cls, Sel("accessibilityValue"),
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr>)&ValueImp, "@@:");
            class_addMethod(cls, Sel("accessibilityHint"),
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr>)&HintImp, "@@:");
            class_addMethod(cls, Sel("accessibilityIdentifier"),
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr>)&IdentifierImp, "@@:");
            class_addMethod(cls, Sel("accessibilityTraits"),
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, ulong>)&TraitsImp, "Q@:");
            class_addMethod(cls, Sel("accessibilityFrame"),
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, CGRect>)&FrameImp,
                "{CGRect={CGPoint=dd}{CGSize=dd}}@:");
            class_addMethod(cls, Sel("accessibilityActivate"),
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, byte>)&ActivateImp, "B@:");
            class_addMethod(cls, Sel("accessibilityIncrement"),
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void>)&IncrementImp, "v@:");
            class_addMethod(cls, Sel("accessibilityDecrement"),
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void>)&DecrementImp, "v@:");

            // A nested container answers as one: not an element itself, its list in place of a label.
            class_addMethod(cls, Sel("accessibilityElementCount"),
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, nint>)&NestedCountImp, "q@:");
            class_addMethod(cls, Sel("accessibilityElementAtIndex:"),
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, nint, IntPtr>)&NestedAtIndexImp, "@@:q");
            class_addMethod(cls, Sel("indexOfAccessibilityElement:"),
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, nint>)&NestedIndexOfImp, "q@:@");
            class_addMethod(cls, Sel("accessibilityContainerType"),
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, nint>)&ContainerTypeImp, "q@:");

            // Which of the selectors below a node really answers (see the file header).
            class_addMethod(cls, Sel("respondsToSelector:"),
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, byte>)&RespondsToSelectorImp, "B@::");
            class_addMethod(cls, Sel("conformsToProtocol:"),
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, byte>)&ConformsToProtocolImp, "B@:@");

            // UIAccessibilityReadingContent: VoiceOver's reading by line, for a node with a Text pattern.
            class_addMethod(cls, Sel("accessibilityLineNumberForPoint:"),
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, CGPoint, nint>)&LineNumberForPointImp, "q@:{CGPoint=dd}");
            class_addMethod(cls, Sel("accessibilityContentForLineNumber:"),
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, nint, IntPtr>)&ContentForLineImp, "@@:q");
            class_addMethod(cls, Sel("accessibilityFrameForLineNumber:"),
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, nint, CGRect>)&FrameForLineImp,
                "{CGRect={CGPoint=dd}{CGSize=dd}}@:q");
            class_addMethod(cls, Sel("accessibilityPageContent"),
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr>)&PageContentImp, "@@:");
            class_addMethod(cls, Sel("accessibilityTextualContext"),
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr>)&TextualContextImp, "@@:");
            class_addMethod(cls, Sel("accessibilityTextInputResponder"),
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr>)&TextInputResponderImp, "@@:");

            // UIAccessibilityContainerDataTable on a grid, UIAccessibilityContainerDataTableCell on its parts.
            class_addMethod(cls, Sel("accessibilityRowCount"),
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, nuint>)&RowCountImp, "Q@:");
            class_addMethod(cls, Sel("accessibilityColumnCount"),
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, nuint>)&ColumnCountImp, "Q@:");
            class_addMethod(cls, Sel("accessibilityDataTableCellElementForRow:column:"),
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, nuint, nuint, IntPtr>)&CellElementImp, "@@:QQ");
            class_addMethod(cls, Sel("accessibilityHeaderElementsForColumn:"),
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, nuint, IntPtr>)&ColumnHeaderElementsImp, "@@:Q");
            class_addMethod(cls, Sel("accessibilityHeaderElementsForRow:"),
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, nuint, IntPtr>)&RowHeaderElementsImp, "@@:Q");
            class_addMethod(cls, Sel("accessibilityRowRange"),
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, NSRange>)&RowRangeImp, "{_NSRange=QQ}@:");
            class_addMethod(cls, Sel("accessibilityColumnRange"),
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, NSRange>)&ColumnRangeImp, "{_NSRange=QQ}@:");

            // Scrolling, scroll-into-view on focus, and modal windows.
            class_addMethod(cls, Sel("accessibilityScroll:"),
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, nint, byte>)&ScrollImp, "B@:q");
            class_addMethod(cls, Sel("accessibilityElementDidBecomeFocused"),
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void>)&DidBecomeFocusedImp, "v@:");
            class_addMethod(cls, Sel("accessibilityViewIsModal"),
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, byte>)&ElementIsModalImp, "B@:");
            class_addMethod(cls, Sel("accessibilityPerformEscape"),
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, byte>)&EscapeImp, "B@:");

            objc_registerClassPair(cls);

            s_elementClass = cls;
            s_nodeIdOffset = ivar_getOffset(class_getInstanceVariable(cls, "_nodeId"));
        }

        /// <summary>
        /// The element of a listed node, its accessibilityContainer the nested container it is listed
        /// in (or <paramref name="view"/>): VoiceOver walks that back-pointer to announce the
        /// containers the user is in.
        /// </summary>
        private static IntPtr ElementFor(int nodeId, IntPtr view)
        {
            if (nodeId < 0) return IntPtr.Zero;

            EnsureElementClass();
            if (s_elementClass == IntPtr.Zero) return IntPtr.Zero;

            IntPtr container = s_parents.TryGetValue(nodeId, out int parent) && parent != ViewParent
                ? ElementFor(parent, view)
                : s_viewOf.TryGetValue(nodeId, out IntPtr own) ? own : view;
            if (container == IntPtr.Zero) return IntPtr.Zero;

            if (s_elements.TryGetValue(nodeId, out IntPtr existing))
            {
                // the node may have moved since its element was made; the back-pointer follows it
                if (Send(existing, Sel("accessibilityContainer")) != container)
                    SendPtrPtr(existing, Sel("setAccessibilityContainer:"), container);
                return existing;
            }

            IntPtr element = SendPtrPtr(Send(s_elementClass, Sel("alloc")),
                                        Sel("initWithAccessibilityContainer:"), container);
            if (element == IntPtr.Zero) return IntPtr.Zero;

            *(long*)((byte*)element + s_nodeIdOffset) = nodeId;
            s_elements[nodeId] = element;
            return element;
        }

        private static int NodeOf(IntPtr element)
            => element == IntPtr.Zero || s_elementClass == IntPtr.Zero
                ? IAutomationTreeSource.InvalidNode
                : (int)*(long*)((byte*)element + s_nodeIdOffset);

        /// <summary>The view whose list a listed node is under, or zero for a node not listed.</summary>
        private static IntPtr ViewOfListed(int node)
        {
            if (!s_parents.ContainsKey(node)) return IntPtr.Zero;
            int top = node;
            for (int depth = 0; depth < 64 && s_parents.TryGetValue(top, out int parent) && parent != ViewParent; depth++)
                top = parent;
            return s_viewOf.TryGetValue(top, out IntPtr view) ? view : IntPtr.Zero;
        }

        /// <summary>
        /// The element of a table's cell or header. A listed one is the element VoiceOver already
        /// knows; an unlisted one (a cell inside a row, which hides it) is made with the table as its
        /// container, which is where VoiceOver goes back to when it leaves the cell.
        /// </summary>
        private static IntPtr TablePartElement(int node, IntPtr table)
        {
            if (node < 0) return IntPtr.Zero;
            IntPtr view = ViewOfListed(node);
            if (view != IntPtr.Zero) return ElementFor(node, view);
            if (table == IntPtr.Zero) return IntPtr.Zero;

            if (s_elements.TryGetValue(node, out IntPtr existing))
            {
                if (Send(existing, Sel("accessibilityContainer")) != table)
                    SendPtrPtr(existing, Sel("setAccessibilityContainer:"), table);
                return existing;
            }

            IntPtr element = SendPtrPtr(Send(s_elementClass, Sel("alloc")), Sel("initWithAccessibilityContainer:"), table);
            if (element == IntPtr.Zero) return IntPtr.Zero;
            *(long*)((byte*)element + s_nodeIdOffset) = node;
            s_elements[node] = element;
            return element;
        }

        /// <summary>
        /// Where an element sits in a list. One not listed itself (a table cell reached through the
        /// table protocol) is placed at its nearest listed ancestor, so a swipe from it carries on from
        /// its row rather than from the top.
        /// </summary>
        private static int IndexIn(List<int> list, int node)
        {
            IAutomationTreeSource tree = Tree;
            for (int depth = 0; node >= 0 && depth < 64; depth++)
            {
                int index = list.IndexOf(node);
                if (index >= 0 || tree == null) return index;
                node = tree.GetParentId(node);
            }
            return -1;
        }

        // ---- flattening ------------------------------------------------------------

        /// <summary>
        /// Rebuilds the lists UIKit navigates. Only nodes worth stopping on are included: one that
        /// has no name, no value and nothing to do is layout scaffolding, and listing it means the
        /// user swipes through silence.
        /// </summary>
        private static List<int> EnsureFlattened(IntPtr view)
        {
            if (s_stale)
            {
                s_views.Clear();
                s_containers.Clear();
                s_parents.Clear();
                s_viewOf.Clear();
                s_stale = false;
            }
            s_knownViews.Add(view);
            if (s_views.TryGetValue(view, out List<int> list)) return list;

            // The first question is what switches the automation tree on, and the window's root may
            // not exist yet when it is asked: nothing is kept until there is one to read, or the empty
            // answer would stand until the next structural change.
            IAutomationTreeSource tree = Tree;
            if (tree == null) { Trace("no automation tree yet"); return null; }

            // the tree knows its windows by handle, UIKit by view
            int root = tree.GetRootId(UIKitWindow.HandleOf(view));
            if (root < 0) { Trace($"view 0x{view.ToInt64():x}: no tree root"); return null; }

            list = new List<int>();
            s_views[view] = list;
            foreach (int child in tree.GetChildIds(root)) Collect(tree, child, list, ViewParent, view, 0);
            Trace($"view 0x{view.ToInt64():x}: root {root}, {list.Count} listed, {s_containers.Count} nested containers in all");
            return list;
        }

        private static void Collect(IAutomationTreeSource tree, int node, List<int> into, int parent, IntPtr view, int depth)
        {
            if (depth > 64) return;   // a malformed custom peer must not hang VoiceOver

            bool stop = IsWorthStopping(tree, node);
            int role = tree.GetRole(node);

            // a GroupBox's header is its own text peer, carrying the name the group already announces
            if (role == 20 && parent != ViewParent && tree.GetName(node) == tree.GetName(parent)) return;

            if (IsNestedContainer(tree, node, role, tree.GetState(node)))
            {
                var items = new List<int>();
                foreach (int child in tree.GetChildIds(node)) Collect(tree, child, items, node, view, depth + 1);
                if (items.Count > 0)
                {
                    s_containers[node] = items;
                    Add(node, into, parent, view);
                    return;
                }
                // an empty list still says it is there, as an element
                if (stop) Add(node, into, parent, view);
                return;
            }

            if (stop)
            {
                Add(node, into, parent, view);

                // An element that is itself accessible HIDES its children -- UIKit's own rule for
                // views, and the right one here. A WPF Button contains a TextBlock carrying the same
                // string, so descending into it would list every control twice: once as a button and
                // again as static text. A container's children are content in their own right, so a
                // window or pane worth stopping on still lists them, beside itself.
                if (!IsContainerRole(role)) return;
            }

            foreach (int child in tree.GetChildIds(node)) Collect(tree, child, into, parent, view, depth + 1);
        }

        private static void Add(int node, List<int> into, int parent, IntPtr view)
        {
            into.Add(node);
            s_parents[node] = parent;
            if (parent == ViewParent) s_viewOf[node] = view;
        }

        /// <summary>
        /// Roles whose children are content in their own right, rather than the parts a control is
        /// drawn from.
        /// </summary>
        private static bool IsContainerRole(int role) => role switch
        {
            8 or 23 or 28 or 36 => true,        // List, Tree, DataGrid, Table
            9 or 10 or 21 or 18 => true,        // Menu, MenuBar, ToolBar, Tab
            26 or 33 or 32 or 30 => true,       // Group, Pane, Window, Document
            17 or 37 or 34 => true,             // StatusBar, TitleBar, Header
            _ => false,
        };

        /// <summary>
        /// The containers that keep a level of their own: the ones a user recognises and would step
        /// over or into. A group, pane or window only when it has a name to announce -- WPF wraps
        /// nearly everything in nameless panes and groups, and a level per wrapper would bury every
        /// control under announcements of nothing.
        /// </summary>
        // A grid of any role keeps its level: it is where the table protocol is answered.
        private static bool IsNestedContainer(IAutomationTreeSource tree, int node, int role, AccessibleState state)
            => (state & AccessibleState.Grid) != 0 || role switch
            {
                8 or 23 or 28 or 36 or 9 or 10 or 21 or 18 or 17 => true,
                26 or 33 or 32 or 30 => !string.IsNullOrEmpty(tree.GetName(node)),
                _ => false,
            };

        /// <summary>UIAccessibilityContainerType: DataTable (1) for a node with a Grid pattern, whose
        /// row/column protocol this backend answers; List (2) for the lists VoiceOver counts through;
        /// SemanticGroup (4) for the rest -- including a table control without a Grid pattern, since
        /// DataTable promises cells that such a control cannot hand out.</summary>
        internal static nint ContainerTypeFor(int role, AccessibleState state)
        {
            if ((state & AccessibleState.Grid) != 0) return 1;
            return role switch
            {
                8 or 23 or 9 or 10 => 2,
                _ => 4,
            };
        }

        private static bool IsWorthStopping(IAutomationTreeSource tree, int node)
        {
            AccessibleState state = tree.GetState(node);
            if ((state & AccessibleState.Offscreen) != 0) return false;

            if (!string.IsNullOrEmpty(tree.GetName(node))) return true;
            // a nameless document still has its text to read
            if ((state & (AccessibleState.HasValue | AccessibleState.HasRange | AccessibleState.HasText)) != 0) return true;

            return tree.SupportsAction(node, AccessibleAction.Invoke)
                || tree.SupportsAction(node, AccessibleAction.Toggle)
                || tree.SupportsAction(node, AccessibleAction.Select);
        }

        // ---- the container's answers -----------------------------------------------

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static byte ViewIsElementImp(IntPtr self, IntPtr sel)
        {
            // The view is a container, never an element: what VoiceOver should describe is the
            // controls WPF drew into it, not the Metal surface they were drawn on.
            NoteAccessibilityClient();
            return 0;
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static nint ElementCountImp(IntPtr self, IntPtr sel)
        {
            NoteAccessibilityClient();
            try
            {
                return EnsureFlattened(self)?.Count ?? 0;
            }
            catch (Exception e) { Log("accessibilityElementCount", e); return 0; }
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static IntPtr ElementAtIndexImp(IntPtr self, IntPtr sel, nint index)
        {
            NoteAccessibilityClient();
            try
            {
                List<int> list = EnsureFlattened(self);
                if (list == null || index < 0 || index >= list.Count) return IntPtr.Zero;
                return ElementFor(list[(int)index], self);
            }
            catch (Exception e) { Log("accessibilityElementAtIndex", e); return IntPtr.Zero; }
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static nint IndexOfElementImp(IntPtr self, IntPtr sel, IntPtr element)
        {
            try
            {
                List<int> list = EnsureFlattened(self);
                int index = list == null ? -1 : IndexIn(list, NodeOf(element));
                // NSNotFound, which is what UIKit expects for an element that is no longer listed.
                return index < 0 ? nint.MaxValue : index;
            }
            catch (Exception e) { Log("indexOfAccessibilityElement", e); return nint.MaxValue; }
        }

        // ---- a nested container's answers ------------------------------------------

        private static List<int> NestedList(IntPtr element)
        {
            // after a structural change the lists are rebuilt from every view that has been asked about
            if (s_stale) foreach (IntPtr view in s_knownViews) EnsureFlattened(view);
            return s_containers.TryGetValue(NodeOf(element), out List<int> items) ? items : null;
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static nint NestedCountImp(IntPtr self, IntPtr sel)
        {
            try { return NestedList(self)?.Count ?? 0; }
            catch (Exception e) { Log("accessibilityElementCount (nested)", e); return 0; }
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static IntPtr NestedAtIndexImp(IntPtr self, IntPtr sel, nint index)
        {
            try
            {
                List<int> items = NestedList(self);
                if (items == null || index < 0 || index >= items.Count) return IntPtr.Zero;
                return ElementFor(items[(int)index], IntPtr.Zero);
            }
            catch (Exception e) { Log("accessibilityElementAtIndex (nested)", e); return IntPtr.Zero; }
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static nint NestedIndexOfImp(IntPtr self, IntPtr sel, IntPtr element)
        {
            try
            {
                List<int> items = NestedList(self);
                int index = items == null ? -1 : IndexIn(items, NodeOf(element));
                return index < 0 ? nint.MaxValue : index;
            }
            catch (Exception e) { Log("indexOfAccessibilityElement (nested)", e); return nint.MaxValue; }
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static nint ContainerTypeImp(IntPtr self, IntPtr sel)
        {
            try
            {
                IAutomationTreeSource tree = Tree;
                int node = NodeOf(self);
                if (tree == null || node < 0 || NestedList(self) == null) return 0;
                return ContainerTypeFor(tree.GetRole(node), tree.GetState(node));
            }
            catch (Exception e) { Log("accessibilityContainerType", e); return 0; }
        }

        // ---- an element's answers --------------------------------------------------

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static byte IsElementImp(IntPtr self, IntPtr sel)
        {
            // a nested container is navigated into, not stopped on
            try { return (byte)(NestedList(self) == null ? 1 : 0); }
            catch (Exception e) { Log("isAccessibilityElement", e); return 1; }
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static IntPtr LabelImp(IntPtr self, IntPtr sel)
        {
            try
            {
                IAutomationTreeSource tree = Tree;
                int node = NodeOf(self);
                return tree == null || node < 0 ? IntPtr.Zero : NSStr(tree.GetName(node));
            }
            catch (Exception e) { Log("accessibilityLabel", e); return IntPtr.Zero; }
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static IntPtr ValueImp(IntPtr self, IntPtr sel)
        {
            try
            {
                IAutomationTreeSource tree = Tree;
                int node = NodeOf(self);
                if (tree == null || node < 0) return IntPtr.Zero;

                // A checked state rides in the traits (Selected), not the value, so only real values
                // are reported here -- otherwise VoiceOver says "selected, 1".
                if (tree.TryGetRange(node, out double value, out _, out _))
                {
                    return NSStr(value.ToString("0.##", System.Globalization.CultureInfo.CurrentCulture));
                }

                // Text, as UITextView reports it: the value is what VoiceOver reads after the label.
                string text = TextValue(tree, node) ?? tree.GetValue(node);
                return string.IsNullOrEmpty(text) ? IntPtr.Zero : NSStr(text);
            }
            catch (Exception e) { Log("accessibilityValue", e); return IntPtr.Zero; }
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static IntPtr HintImp(IntPtr self, IntPtr sel)
        {
            try
            {
                IAutomationTreeSource tree = Tree;
                int node = NodeOf(self);
                if (tree == null || node < 0) return IntPtr.Zero;

                string help = tree.GetHelpText(node);
                return string.IsNullOrEmpty(help) ? IntPtr.Zero : NSStr(help);
            }
            catch (Exception e) { Log("accessibilityHint", e); return IntPtr.Zero; }
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static IntPtr IdentifierImp(IntPtr self, IntPtr sel)
        {
            try
            {
                IAutomationTreeSource tree = Tree;
                int node = NodeOf(self);
                return tree == null || node < 0 ? IntPtr.Zero : NSStr(tree.GetAutomationId(node));
            }
            catch (Exception e) { Log("accessibilityIdentifier", e); return IntPtr.Zero; }
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static ulong TraitsImp(IntPtr self, IntPtr sel)
        {
            try
            {
                IAutomationTreeSource tree = Tree;
                int node = NodeOf(self);
                if (tree == null || node < 0) return 0;

                return UIKitTraits.For(tree.GetRole(node), tree.GetState(node));
            }
            catch (Exception e) { Log("accessibilityTraits", e); return 0; }
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static CGRect FrameImp(IntPtr self, IntPtr sel)
        {
            try
            {
                IAutomationTreeSource tree = Tree;
                int node = NodeOf(self);
                if (tree == null || node < 0) return default;

                if (!tree.TryGetBounds(node, out double x, out double y, out double w, out double h))
                {
                    return default;
                }

                // UIKit measures in points from the top left, the same origin WPF reports in, so
                // only the scale differs -- no flip, unlike AppKit.
                double scale = ScreenScale();
                return new CGRect { x = x / scale, y = y / scale, width = w / scale, height = h / scale };
            }
            catch (Exception e) { Log("accessibilityFrame", e); return default; }
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static byte ActivateImp(IntPtr self, IntPtr sel)
        {
            try
            {
                IAutomationTreeSource tree = Tree;
                int node = NodeOf(self);
                if (tree == null || node < 0) return 0;

                return (byte)(PerformDefault(tree, node) ? 1 : 0);
            }
            catch (Exception e) { Log("accessibilityActivate", e); return 0; }
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void IncrementImp(IntPtr self, IntPtr sel) => Adjust(self, up: true);

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void DecrementImp(IntPtr self, IntPtr sel) => Adjust(self, up: false);

        // Ordinary managed methods: an UnmanagedCallersOnly method cannot be called from managed
        // code, so anything shared between IMPs has to live outside them.

        private static bool PerformDefault(IAutomationTreeSource tree, int node)
        {
            foreach (AccessibleAction action in s_activateOrder)
            {
                if (tree.SupportsAction(node, action)) return tree.DoAction(node, action);
            }
            return false;
        }

        private static readonly AccessibleAction[] s_activateOrder =
        {
            AccessibleAction.Toggle, AccessibleAction.Invoke, AccessibleAction.Select, AccessibleAction.Expand,
        };

        private static void Adjust(IntPtr element, bool up)
        {
            try
            {
                IAutomationTreeSource tree = Tree;
                int node = NodeOf(element);
                if (tree == null || node < 0) return;

                if (!tree.TryGetRange(node, out double value, out double min, out double max)) return;

                // VoiceOver's adjustable gesture has no step of its own, so a twentieth of the range
                // is used -- small enough to be precise, large enough that a swipe does something.
                double step = (max - min) / 20.0;
                if (step <= 0) step = 1;

                double target = Math.Clamp(up ? value + step : value - step, min, max);
                tree.SetValue(node, target.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            catch (Exception e) { Log("accessibilityIncrement/Decrement", e); }
        }

        // ---- which selectors a node answers ----------------------------------------

        /// <summary>The pattern a gated selector or protocol belongs to.</summary>
        internal enum PatternGate { None, Text, Table, Cell }

        internal static PatternGate GateOf(string selectorOrProtocol) => selectorOrProtocol switch
        {
            "UIAccessibilityReadingContent" or "accessibilityLineNumberForPoint:" or "accessibilityContentForLineNumber:"
                or "accessibilityFrameForLineNumber:" or "accessibilityPageContent" => PatternGate.Text,
            "UIAccessibilityContainerDataTable" or "accessibilityDataTableCellElementForRow:column:" or "accessibilityRowCount"
                or "accessibilityColumnCount" or "accessibilityHeaderElementsForRow:" or "accessibilityHeaderElementsForColumn:" => PatternGate.Table,
            "UIAccessibilityContainerDataTableCell" or "accessibilityRowRange" or "accessibilityColumnRange" => PatternGate.Cell,
            _ => PatternGate.None,
        };

        private static readonly string[] s_gatedNames =
        {
            "accessibilityLineNumberForPoint:", "accessibilityContentForLineNumber:", "accessibilityFrameForLineNumber:",
            "accessibilityPageContent", "accessibilityDataTableCellElementForRow:column:", "accessibilityRowCount",
            "accessibilityColumnCount", "accessibilityHeaderElementsForRow:", "accessibilityHeaderElementsForColumn:",
            "accessibilityRowRange", "accessibilityColumnRange",
        };

        private static readonly string[] s_gatedProtocols =
        {
            "UIAccessibilityReadingContent", "UIAccessibilityContainerDataTable", "UIAccessibilityContainerDataTableCell",
        };

        // SEL and Protocol pointers are unique per name, so the gates are looked up by pointer: these
        // two are asked about every selector UIKit ever probes an element for, and must stay cheap.
        private static readonly Dictionary<IntPtr, PatternGate> s_selectorGates = new();
        private static readonly Dictionary<IntPtr, PatternGate> s_protocolGates = new();
        private static IntPtr s_superRespondsToSelector;
        private static IntPtr s_superConformsToProtocol;

        private static void BuildGates()
        {
            if (s_selectorGates.Count > 0) return;
            foreach (string name in s_gatedNames) s_selectorGates[Sel(name)] = GateOf(name);
            foreach (string name in s_gatedProtocols)
            {
                IntPtr protocol = objc_getProtocol(name);
                if (protocol != IntPtr.Zero) s_protocolGates[protocol] = GateOf(name);
            }
        }

        /// <summary>Whether a node has the pattern behind a gate. A table is answered only by a nested
        /// container, since its cells and rows have to be the elements VoiceOver finds inside it.</summary>
        internal static bool Passes(PatternGate gate, IAutomationTreeSource tree, int node, bool isNestedContainer)
        {
            if (tree == null || node < 0) return false;
            return gate switch
            {
                PatternGate.Text => IsText(tree.GetState(node)),
                PatternGate.Table => isNestedContainer && (tree.GetState(node) & AccessibleState.Grid) != 0,
                PatternGate.Cell => TryGetCellRanges(tree, node, out _, out _, out _, out _),
                _ => true,
            };
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static byte RespondsToSelectorImp(IntPtr self, IntPtr sel, IntPtr selector)
        {
            try
            {
                if (s_selectorGates.TryGetValue(selector, out PatternGate gate))
                    return (byte)(Passes(gate, Tree, NodeOf(self), NestedList(self) != null) ? 1 : 0);
            }
            catch (Exception e) { Log("respondsToSelector", e); return 0; }

            return s_superRespondsToSelector == IntPtr.Zero ? (byte)0
                : ((delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, byte>)s_superRespondsToSelector)(self, sel, selector);
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static byte ConformsToProtocolImp(IntPtr self, IntPtr sel, IntPtr protocol)
        {
            try
            {
                if (s_protocolGates.TryGetValue(protocol, out PatternGate gate))
                    return (byte)(Passes(gate, Tree, NodeOf(self), NestedList(self) != null) ? 1 : 0);
            }
            catch (Exception e) { Log("conformsToProtocol", e); return 0; }

            return s_superConformsToProtocol == IntPtr.Zero ? (byte)0
                : ((delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, byte>)s_superConformsToProtocol)(self, sel, protocol);
        }

        // ---- text: UIAccessibilityReadingContent -----------------------------------

        private static readonly nint NSNotFound = nint.MaxValue;

        /// <summary>A Text pattern whose characters may be read out: a password box's may not.</summary>
        internal static bool IsText(AccessibleState state)
            => (state & AccessibleState.HasText) != 0 && (state & AccessibleState.Password) == 0;

        private static string TextOf(IAutomationTreeSource tree, int node)
            => IsText(tree.GetState(node)) && tree.TryGetText(node, out string text) ? text ?? string.Empty : null;

        /// <summary>
        /// The text as the element's value, or null to leave the value to the Value pattern. A node
        /// whose text IS its name (a TextBlock) would otherwise be read twice, label then value.
        /// </summary>
        internal static string TextValue(IAutomationTreeSource tree, int node)
        {
            string text = TextOf(tree, node);
            if (string.IsNullOrEmpty(text) || text == tree.GetName(node)) return null;
            return text;
        }

        /// <summary>
        /// The range of line <paramref name="line"/> (zero-based, as the tree numbers them). The tree
        /// answers "which line is this offset on", never "where is line N", so the lines are walked
        /// from the top; a line that does not end where the tree says is stepped over a character at
        /// a time so the walk always makes progress.
        /// </summary>
        internal static bool TryGetLineRange(IAutomationTreeSource tree, int node, string text, long line, out int start, out int length)
        {
            start = length = 0;
            if (text == null || line < 0) return false;

            if (!tree.TryGetTextLine(node, 0, out _, out _, out _))
            {
                // no line geometry at all: the text is one line
                if (line != 0) return false;
                length = text.Length;
                return true;
            }

            int offset = 0;
            while (offset <= text.Length)
            {
                if (!tree.TryGetTextLine(node, offset, out int s, out int l, out int number)) break;
                if (number == line)
                {
                    start = Math.Clamp(s, 0, text.Length);
                    length = Math.Clamp(l, 0, text.Length - start);
                    return true;
                }
                if (number > line) break;

                int next = s + l;
                offset = next > offset ? next : offset + 1;
            }
            return false;
        }

        /// <summary>A line's characters without its break: VoiceOver speaks a trailing newline as "new line".</summary>
        internal static string LineContent(IAutomationTreeSource tree, int node, long line)
        {
            string text = TextOf(tree, node);
            if (!TryGetLineRange(tree, node, text, line, out int start, out int length)) return null;
            return text.Substring(start, length).TrimEnd('\r', '\n');
        }

        /// <summary>A line's screen rectangle, in points.</summary>
        internal static bool TryGetLineFrame(IAutomationTreeSource tree, int node, long line, double scale,
            out double x, out double y, out double width, out double height)
        {
            x = y = width = height = 0;
            string text = TextOf(tree, node);
            if (!TryGetLineRange(tree, node, text, line, out int start, out int length)) return false;
            if (!tree.TryGetTextRangeBounds(node, start, length, out double px, out double py, out double pw, out double ph)) return false;
            if (scale <= 0) scale = 1;
            x = px / scale; y = py / scale; width = pw / scale; height = ph / scale;
            return true;
        }

        /// <summary>
        /// The line under a point (screen points, the space every other UIKit accessibility geometry
        /// is in), or NSNotFound.
        /// </summary>
        internal static nint LineNumberAt(IAutomationTreeSource tree, int node, double x, double y, double scale)
        {
            if (TextOf(tree, node) == null) return NSNotFound;
            if (scale <= 0) scale = 1;
            int offset = tree.GetTextOffsetAtPoint(node, x * scale, y * scale);
            if (offset < 0) return NSNotFound;
            if (!tree.TryGetTextLine(node, offset, out _, out _, out int number)) return 0;   // one line
            return Math.Max(number, 0);
        }

        /// <summary>The text on screen, which is what a "page" is to VoiceOver; all of it when the tree
        /// cannot say which part is visible.</summary>
        internal static string PageContent(IAutomationTreeSource tree, int node)
        {
            string text = TextOf(tree, node);
            if (text == null) return null;
            if (tree.TryGetVisibleTextRange(node, out int start, out int length) && length > 0 && start >= 0 && start < text.Length)
                return text.Substring(start, Math.Min(length, text.Length - start));
            return text;
        }

        /// <summary>
        /// Whether VoiceOver's text commands (move by character, word, line; select) go to the WPF
        /// view's UITextInput. Only for the editor that has the keyboard: the view's UITextInput
        /// speaks for the focused editor and no other, so handing it out for any other node would put
        /// VoiceOver's caret in the wrong text.
        /// </summary>
        internal static bool ForwardsTextInput(AccessibleState state)
            => IsText(state) && (state & AccessibleState.ReadOnly) == 0 && (state & AccessibleState.Focused) != 0;

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static nint LineNumberForPointImp(IntPtr self, IntPtr sel, CGPoint point)
            => Guarded(self, "accessibilityLineNumberForPoint", NSNotFound,
                       (tree, node) => LineNumberAt(tree, node, point.x, point.y, ScreenScale()));

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static IntPtr ContentForLineImp(IntPtr self, IntPtr sel, nint line)
            => Guarded(self, "accessibilityContentForLineNumber", IntPtr.Zero, (tree, node) =>
            {
                string content = LineContent(tree, node, line);
                // an empty line is still a line: an empty string, not nil, which would mean "no such line"
                return content == null ? IntPtr.Zero : content.Length == 0 ? EmptyNSString() : NSStr(content);
            });

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static CGRect FrameForLineImp(IntPtr self, IntPtr sel, nint line)
            => Guarded(self, "accessibilityFrameForLineNumber", default(CGRect), (tree, node) =>
                TryGetLineFrame(tree, node, line, ScreenScale(), out double x, out double y, out double w, out double h)
                    ? new CGRect { x = x, y = y, width = w, height = h }
                    : default);

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static IntPtr PageContentImp(IntPtr self, IntPtr sel)
            => Guarded(self, "accessibilityPageContent", IntPtr.Zero, static (tree, node) => NSStr(PageContent(tree, node)));

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static IntPtr TextualContextImp(IntPtr self, IntPtr sel)
            => Guarded(self, "accessibilityTextualContext", IntPtr.Zero, static (tree, node) =>
            {
                // A document is prose, which VoiceOver then reads with punctuation as a word processor's;
                // every other kind of text is left to its default.
                return tree.GetRole(node) == 30 && IsText(tree.GetState(node))
                    ? UIKitString("UIAccessibilityTextualContextWordProcessing")
                    : IntPtr.Zero;
            });

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static IntPtr TextInputResponderImp(IntPtr self, IntPtr sel)
            => Guarded(self, "accessibilityTextInputResponder", IntPtr.Zero, static (tree, node) =>
            {
                if (!ForwardsTextInput(tree.GetState(node))) return IntPtr.Zero;
                if (!UIKitTextInput.IsEnabled || UIKitTextInput.Document == null) return IntPtr.Zero;

                // the view this editor is drawn in, and only while it holds the keyboard
                IntPtr view = ViewOfListed(node);
                if (view == IntPtr.Zero || SendByte(view, Sel("isFirstResponder")) == 0) return IntPtr.Zero;
                return view;
            });

        // ---- tables: UIAccessibilityContainerDataTable(Cell) -----------------------

        /// <summary>
        /// The rows and columns a part of a table covers, -1 for an axis it does not sit on. A cell
        /// has its own; a row (an item whose children are cells, as a DataGrid's or a GridView's rows
        /// are) covers its cells' row and every column; a column header covers its column and no row,
        /// a row header its row and no column.
        /// </summary>
        internal static bool TryGetCellRanges(IAutomationTreeSource tree, int node,
            out int row, out int rowSpan, out int column, out int columnSpan)
        {
            row = column = -1; rowSpan = columnSpan = 0;
            if (tree == null || node < 0) return false;

            AccessibleState state = tree.GetState(node);
            if ((state & AccessibleState.GridItem) != 0 && tree.TryGetGridItem(node, out int r, out int c, out int rs, out int cs, out _))
            {
                row = r; column = c; rowSpan = Math.Max(rs, 1); columnSpan = Math.Max(cs, 1);
                return true;
            }

            int[] children = tree.GetChildIds(node);
            if (children.Length > 0 && (tree.GetState(children[0]) & AccessibleState.GridItem) != 0
                && tree.TryGetGridItem(children[0], out r, out _, out _, out _, out int grid))
            {
                row = r; rowSpan = 1; column = 0;
                columnSpan = tree.TryGetGrid(grid, out _, out int columns) && columns > 0 ? columns : children.Length;
                return true;
            }

            // a header: its place among the headers of the grid it is in
            int ancestor = tree.GetParentId(node);
            for (int depth = 0; ancestor >= 0 && depth < 64; depth++, ancestor = tree.GetParentId(ancestor))
            {
                if ((tree.GetState(ancestor) & AccessibleState.Grid) == 0) continue;
                int index = Array.IndexOf(tree.GetColumnHeaderIds(ancestor), node);
                if (index >= 0) { column = index; columnSpan = 1; return true; }
                index = Array.IndexOf(tree.GetRowHeaderIds(ancestor), node);
                if (index >= 0) { row = index; rowSpan = 1; return true; }
                return false;
            }
            return false;
        }

        /// <summary>The node of the cell at a row and column, or InvalidNode. NSUInteger indices beyond
        /// an int are simply not cells, rather than wrapping to negative ones.</summary>
        internal static int CellAt(IAutomationTreeSource tree, int grid, ulong row, ulong column)
        {
            if (row > int.MaxValue || column > int.MaxValue) return IAutomationTreeSource.InvalidNode;
            if (!tree.TryGetGrid(grid, out int rows, out int columns) || (int)row >= rows || (int)column >= columns)
                return IAutomationTreeSource.InvalidNode;
            return tree.GetGridCell(grid, (int)row, (int)column);
        }

        /// <summary>The header of one column or row: a table has one per column (or row), in order.</summary>
        internal static int HeaderAt(int[] headers, ulong index)
            => headers != null && index < (ulong)headers.Length ? headers[(int)index] : IAutomationTreeSource.InvalidNode;

        private static NSRange RangeOf(int location, int length)
            => location < 0 ? new NSRange { location = (ulong)long.MaxValue, length = 0 }
                            : new NSRange { location = (ulong)location, length = (ulong)Math.Max(length, 0) };

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static nuint RowCountImp(IntPtr self, IntPtr sel)
            => Guarded(self, "accessibilityRowCount", (nuint)0,
                       static (tree, node) => tree.TryGetGrid(node, out int rows, out _) ? (nuint)Math.Max(rows, 0) : 0);

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static nuint ColumnCountImp(IntPtr self, IntPtr sel)
            => Guarded(self, "accessibilityColumnCount", (nuint)0,
                       static (tree, node) => tree.TryGetGrid(node, out _, out int columns) ? (nuint)Math.Max(columns, 0) : 0);

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static IntPtr CellElementImp(IntPtr self, IntPtr sel, nuint row, nuint column)
            => Guarded(self, "accessibilityDataTableCellElementForRow:column:", IntPtr.Zero,
                       (tree, node) => TablePartElement(CellAt(tree, node, row, column), self));

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static IntPtr ColumnHeaderElementsImp(IntPtr self, IntPtr sel, nuint column)
            => Guarded(self, "accessibilityHeaderElementsForColumn", IntPtr.Zero,
                       (tree, node) => ArrayOf(TablePartElement(HeaderAt(tree.GetColumnHeaderIds(node), column), self)));

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static IntPtr RowHeaderElementsImp(IntPtr self, IntPtr sel, nuint row)
            => Guarded(self, "accessibilityHeaderElementsForRow", IntPtr.Zero,
                       (tree, node) => ArrayOf(TablePartElement(HeaderAt(tree.GetRowHeaderIds(node), row), self)));

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static NSRange RowRangeImp(IntPtr self, IntPtr sel)
            => Guarded(self, "accessibilityRowRange", RangeOf(-1, 0), static (tree, node) =>
                TryGetCellRanges(tree, node, out int row, out int span, out _, out _) ? RangeOf(row, span) : RangeOf(-1, 0));

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static NSRange ColumnRangeImp(IntPtr self, IntPtr sel)
            => Guarded(self, "accessibilityColumnRange", RangeOf(-1, 0), static (tree, node) =>
                TryGetCellRanges(tree, node, out _, out _, out int column, out int span) ? RangeOf(column, span) : RangeOf(-1, 0));

        // ---- scrolling ---------------------------------------------------------------

        // System.Windows.Automation.ScrollAmount, as IAutomationTreeSource.Scroll takes it.
        internal const int LargeDecrement = 0, NoAmount = 2, LargeIncrement = 3;

        /// <summary>
        /// UIAccessibilityScrollDirection to a page of ScrollAmount. The direction is the three-finger
        /// swipe's, and the content follows the fingers as it does under one: swiping up brings up
        /// what is below (a page down), swiping left what is to the right. Next and Previous are the
        /// reading order, down the page if it scrolls that way, else across.
        /// </summary>
        internal static bool ScrollAmountsFor(long direction, bool horizontallyScrollable, bool verticallyScrollable,
            out int horizontal, out int vertical)
        {
            horizontal = vertical = NoAmount;
            switch (direction)
            {
                case 1: horizontal = LargeDecrement; break;   // Right
                case 2: horizontal = LargeIncrement; break;   // Left
                case 3: vertical = LargeIncrement; break;     // Up
                case 4: vertical = LargeDecrement; break;     // Down
                case 5:                                       // Next
                case 6:                                       // Previous
                    int amount = direction == 5 ? LargeIncrement : LargeDecrement;
                    if (verticallyScrollable) vertical = amount;
                    else horizontal = amount;
                    break;
                default: return false;
            }
            return horizontal == NoAmount ? verticallyScrollable : horizontallyScrollable;
        }

        /// <summary>Whether a position (a percentage, -1 when it does not scroll) has room to move by an amount.</summary>
        internal static bool CanScroll(double percent, int amount)
        {
            if (percent < 0 || double.IsNaN(percent)) return false;
            return amount switch
            {
                LargeIncrement or 4 => percent < 100 - 0.01,
                LargeDecrement or 1 => percent > 0.01,
                _ => false,
            };
        }

        /// <summary>
        /// Where a page's scroll lands. A scroll viewer moves on its next layout pass, not inside the
        /// automation call, so straight after Scroll the tree still reports the old position; a page
        /// is one viewport, which as a percentage of the scrollable range is viewSize / (100 - viewSize).
        /// </summary>
        internal static double PredictedPercent(double percent, double viewSize, int amount)
        {
            double step = viewSize >= 100 || viewSize <= 0 ? 100 : viewSize / (100 - viewSize) * 100;
            double sign = amount is LargeIncrement or 4 ? 1 : amount is LargeDecrement or 1 ? -1 : 0;
            return Math.Clamp(percent + sign * step, 0, 100);
        }

        /// <summary>
        /// The "page n of m" VoiceOver says after a scroll, as UIScrollView's own. The content is
        /// 100 / viewSize viewports long; the first page is at 0% and the last at 100%.
        /// </summary>
        internal static string PageStatus(double percent, double viewSize)
        {
            int pages = viewSize <= 0 || viewSize >= 100 || double.IsNaN(viewSize) ? 1 : (int)Math.Ceiling(100 / viewSize - 1e-9);
            int page = pages == 1 ? 1 : (int)Math.Round(Math.Clamp(percent, 0, 100) / 100 * (pages - 1)) + 1;
            return string.Format(System.Globalization.CultureInfo.CurrentCulture, "Page {0} of {1}", page, pages);
        }

        /// <summary>
        /// Scrolls a page for VoiceOver's three-finger swipe: the nearest scrolling node from
        /// <paramref name="node"/> up that still has room in that direction -- the swipe goes to the
        /// focused element, and the scroll viewer around it is not listed, so it is found through the
        /// tree. False when nothing moved, which is UIKit's cue to try the next view up and, at the
        /// end, to play the boundary sound.
        /// </summary>
        internal static bool TryScrollPage(IAutomationTreeSource tree, int node, long direction, out string status)
        {
            status = null;
            for (int depth = 0; node >= 0 && depth < 64; depth++, node = tree.GetParentId(node))
            {
                if ((tree.GetState(node) & AccessibleState.Scrollable) == 0) continue;
                if (!tree.TryGetScroll(node, out double hp, out double vp, out double hv, out double vv, out bool hs, out bool vs)) continue;
                if (!ScrollAmountsFor(direction, hs, vs, out int h, out int v)) continue;

                bool vertical = v != NoAmount;
                double before = vertical ? vp : hp;
                double viewSize = vertical ? vv : hv;
                int amount = vertical ? v : h;
                if (!CanScroll(before, amount)) continue;
                if (!tree.Scroll(node, h, v)) continue;

                double after = before;
                if (tree.TryGetScroll(node, out double hp2, out double vp2, out _, out _, out _, out _))
                    after = vertical ? vp2 : hp2;
                if (Math.Abs(after - before) < 0.01) after = PredictedPercent(before, viewSize, amount);

                status = PageStatus(after, viewSize);
                return true;
            }
            return false;
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static byte ScrollImp(IntPtr self, IntPtr sel, nint direction)
            => Guarded(self, "accessibilityScroll", (byte)0, (tree, node) =>
            {
                if (!TryScrollPage(tree, node, direction, out string status)) return (byte)0;
                // UIAccessibilityPageScrolledNotification; repeating a status is how VoiceOver tells a
                // border was reached, so the string is the position, never a fixed "scrolled".
                UIAccessibilityPostNotification(1009, NSStr(status));
                return (byte)1;
            });

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void DidBecomeFocusedImp(IntPtr self, IntPtr sel)
            => Guarded(self, "accessibilityElementDidBecomeFocused", 0, static (tree, node) =>
            {
                // A list item VoiceOver swiped onto at the edge of the view comes fully into it, as
                // UITableView does with its cells -- otherwise the user hears an item they cannot see.
                if ((tree.GetState(node) & AccessibleState.ScrollItem) != 0) tree.ScrollIntoView(node);
                return 0;
            });

        // ---- windows -----------------------------------------------------------------

        internal static bool IsModalWindow(AccessibleState state)
            => (state & (AccessibleState.Window | AccessibleState.Modal)) == (AccessibleState.Window | AccessibleState.Modal);

        /// <summary>
        /// The modal window an Escape gesture on <paramref name="node"/> dismisses: the nearest one
        /// around it, or InvalidNode. Only a modal one: closing an ordinary window is not what Escape
        /// means, while dismissing a dialog is exactly what UIKit's popovers and alerts do with it.
        /// </summary>
        internal static int ModalWindowAround(IAutomationTreeSource tree, int node)
        {
            for (int depth = 0; node >= 0 && depth < 64; depth++, node = tree.GetParentId(node))
                if (IsModalWindow(tree.GetState(node))) return node;
            return IAutomationTreeSource.InvalidNode;
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static byte ElementIsModalImp(IntPtr self, IntPtr sel)
            => Guarded(self, "accessibilityViewIsModal", (byte)0, static (tree, node) => (byte)(IsModalWindow(tree.GetState(node)) ? 1 : 0));

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static byte EscapeImp(IntPtr self, IntPtr sel)
            => Guarded(self, "accessibilityPerformEscape", (byte)0, static (tree, node) =>
            {
                int window = ModalWindowAround(tree, node);
                return (byte)(window >= 0 && tree.CloseWindow(window) ? 1 : 0);
            });

        // The view's own answers ask about the window it draws: the root of its tree.
        private static int RootOfView(IAutomationTreeSource tree, IntPtr view)
            => tree == null ? IAutomationTreeSource.InvalidNode : tree.GetRootId(UIKitWindow.HandleOf(view));

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static byte ViewIsModalImp(IntPtr self, IntPtr sel)
        {
            try
            {
                IAutomationTreeSource tree = Tree;
                int root = RootOfView(tree, self);
                return (byte)(root >= 0 && IsModalWindow(tree.GetState(root)) ? 1 : 0);
            }
            catch (Exception e) { Log("accessibilityViewIsModal (view)", e); return 0; }
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static byte ViewEscapeImp(IntPtr self, IntPtr sel)
        {
            try
            {
                IAutomationTreeSource tree = Tree;
                int root = RootOfView(tree, self);
                return (byte)(root >= 0 && IsModalWindow(tree.GetState(root)) && tree.CloseWindow(root) ? 1 : 0);
            }
            catch (Exception e) { Log("accessibilityPerformEscape (view)", e); return 0; }
        }

        /// <summary>The common shape of an element's answer: the tree and node, or the fallback, and
        /// never an exception into the Objective-C runtime.</summary>
        private static T Guarded<T>(IntPtr self, string what, T fallback, Func<IAutomationTreeSource, int, T> answer)
        {
            try
            {
                IAutomationTreeSource tree = Tree;
                int node = NodeOf(self);
                if (tree == null || node < 0) return fallback;
                return answer(tree, node);
            }
            catch (Exception e) { Log(what, e); return fallback; }
        }

        // ---- notifications out -----------------------------------------------------

        private const uint ScreenChanged = 1000, LayoutChanged = 1001;

        /// <summary>
        /// Whether the condensed lists are wrong after a change. Structure, obviously; and scrolling,
        /// because the lists leave out what is off screen, so a scroll changes which nodes belong in
        /// them without adding or removing any.
        /// </summary>
        internal static bool InvalidatesLists(AutomationChangeKind kind) => kind switch
        {
            AutomationChangeKind.ChildrenChanged or AutomationChangeKind.ChildAdded or AutomationChangeKind.ChildRemoved => true,
            AutomationChangeKind.ScrollChanged or AutomationChangeKind.OffscreenChanged => true,
            _ => false,
        };

        /// <summary>
        /// The UIAccessibility notification a change is posted as, or 0 for none. UIKit has only two
        /// that apply: ScreenChanged, which moves VoiceOver to the start of a new screen with a sound,
        /// and LayoutChanged, which has it re-read the elements and stay where it is. There is no
        /// public value-changed or selection-changed notification -- VoiceOver re-reads the focused
        /// element's value on LayoutChanged.
        /// </summary>
        internal static uint NotificationFor(AutomationChangeKind kind, bool isWindowRoot) => kind switch
        {
            // A window's whole content replaced (a dialog's tree arriving, a page navigated) is a new
            // screen. Anything smaller -- an item added to a list as the user types -- is not, and a
            // ScreenChanged for it would throw VoiceOver back to the top of the window every time.
            AutomationChangeKind.ChildrenChanged => isWindowRoot ? ScreenChanged : LayoutChanged,
            AutomationChangeKind.ChildAdded or AutomationChangeKind.ChildRemoved => LayoutChanged,
            // Typing changes the value VoiceOver reads; it echoes the keys itself, so only the
            // element has to be re-read, not announced.
            AutomationChangeKind.TextChanged => LayoutChanged,
            // The caret and selection are the text input's: UIKitTextInput tells UIKit through the
            // input delegate's selectionDidChange:, which is what VoiceOver's caret echo listens to.
            // An accessibility element has no caret of its own to announce.
            AutomationChangeKind.TextSelectionChanged => 0,
            // Scrolling moves every element without changing any; a selection container's selected
            // items carry the Selected trait, which is re-read with the layout.
            AutomationChangeKind.ScrollChanged => LayoutChanged,
            AutomationChangeKind.SelectionContainerChanged => LayoutChanged,
            _ => LayoutChanged,
        };

        private static void OnTreeChanged(AutomationChange change)
        {
            try
            {
                // The lists are now wrong; rebuild them before telling VoiceOver to re-read.
                if (InvalidatesLists(change.Kind)) s_stale = true;

                bool isWindowRoot = false;
                if (change.Kind == AutomationChangeKind.ChildrenChanged)
                {
                    IAutomationTreeSource tree = Tree;
                    isWindowRoot = tree != null && change.NodeId >= 0 && tree.GetRootId(change.WindowHandle) == change.NodeId;
                }

                uint notification = NotificationFor(change.Kind, isWindowRoot);
                if (notification == 0) return;

                IntPtr element = change.Kind == AutomationChangeKind.FocusChanged
                    ? FocusElement(change.NodeId)
                    : IntPtr.Zero;

                UIAccessibilityPostNotification(notification, element);
            }
            catch (Exception e) { Log("postNotification", e); }
        }

        // The element VoiceOver moves to with the keyboard focus: the focused node's, or the nearest
        // listed ancestor's (a TextBlock in a list item is not listed itself, the item is).
        private static IntPtr FocusElement(int node)
        {
            if (s_stale) foreach (IntPtr view in s_knownViews) EnsureFlattened(view);
            IAutomationTreeSource tree = Tree;
            for (int depth = 0; node >= 0 && tree != null && depth < 64; depth++, node = tree.GetParentId(node))
            {
                if (!s_parents.ContainsKey(node)) continue;
                for (int top = node; ; top = s_parents[top])
                    if (s_parents[top] == ViewParent) return ElementFor(node, s_viewOf[top]);
            }
            return IntPtr.Zero;
        }

        // ---- helpers ---------------------------------------------------------------

        private static double ScreenScale()
        {
            IntPtr screen = Send(objc_getClass("UIScreen"), Sel("mainScreen"));
            double scale = screen == IntPtr.Zero ? 1 : SendDouble(screen, Sel("scale"));
            return scale <= 0 ? 1 : scale;
        }

        private static void Log(string what, Exception e)
            => Console.WriteLine($"WPF iOS accessibility {what} failed: {e}");

        private const string ObjC = "/usr/lib/libobjc.A.dylib";

        private static IntPtr Sel(string name) => sel_registerName(name);

        private static IntPtr NSStr(string value)
        {
            if (string.IsNullOrEmpty(value)) return IntPtr.Zero;

            IntPtr bytes = Marshal.StringToCoTaskMemUTF8(value);
            try { return SendPtrPtr(objc_getClass("NSString"), Sel("stringWithUTF8String:"), bytes); }
            finally { Marshal.FreeCoTaskMem(bytes); }
        }

        private static IntPtr EmptyNSString() => Send(objc_getClass("NSString"), Sel("string"));

        private static IntPtr ArrayOf(IntPtr element)
            => element == IntPtr.Zero ? IntPtr.Zero : SendPtrPtr(objc_getClass("NSArray"), Sel("arrayWithObject:"), element);

        /// <summary>One of UIKit's exported NSString constants, by name; zero where UIKit lacks it.</summary>
        private static IntPtr UIKitString(string name)
        {
            IntPtr uiKit = UIKitHandle();
            IntPtr symbol = uiKit == IntPtr.Zero ? IntPtr.Zero : dlsym(uiKit, name);
            return symbol == IntPtr.Zero ? IntPtr.Zero : *(IntPtr*)symbol;
        }

        [DllImport(ObjC)] private static extern IntPtr objc_getClass(string name);
        [DllImport(ObjC)] private static extern IntPtr sel_registerName(string name);
        [DllImport(ObjC)] private static extern IntPtr objc_allocateClassPair(IntPtr super, string name, UIntPtr extra);
        [DllImport(ObjC)] private static extern void objc_registerClassPair(IntPtr cls);
        [DllImport(ObjC)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool class_addMethod(IntPtr cls, IntPtr sel, IntPtr imp, string types);
        [DllImport(ObjC)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool class_addIvar(IntPtr cls, string name, UIntPtr size, byte align, string types);
        [DllImport(ObjC)] private static extern IntPtr class_getInstanceVariable(IntPtr cls, string name);
        [DllImport(ObjC)] private static extern nint ivar_getOffset(IntPtr ivar);
        [DllImport(ObjC)] private static extern IntPtr objc_getProtocol(string name);
        [DllImport(ObjC)] private static extern IntPtr class_getMethodImplementation(IntPtr cls, IntPtr sel);

        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr r, IntPtr s);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendPtrPtr(IntPtr r, IntPtr s, IntPtr a);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern double SendDouble(IntPtr r, IntPtr s);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern byte SendByte(IntPtr r, IntPtr s);

        // dlsym for the same reason as the macOS backend: a framework named in a DllImport is
        // linked eagerly, and UIKit does not exist on the desktop heads this file also compiles
        // into. A raw function pointer rather than a delegate because iOS is AOT-only.
        private static IntPtr s_postNotification = (IntPtr)(-1);
        private static IntPtr s_uiKit = (IntPtr)(-1);

        private static IntPtr UIKitHandle()
        {
            if (s_uiKit == (IntPtr)(-1))
            {
                const int RTLD_NOW = 2;
                s_uiKit = dlopen("/System/Library/Frameworks/UIKit.framework/UIKit", RTLD_NOW);
            }
            return s_uiKit;
        }

        private static void UIAccessibilityPostNotification(uint notification, IntPtr argument)
        {
            if (s_postNotification == (IntPtr)(-1))
            {
                IntPtr uiKit = UIKitHandle();
                s_postNotification = uiKit == IntPtr.Zero
                    ? IntPtr.Zero
                    : dlsym(uiKit, "UIAccessibilityPostNotification");
            }

            if (s_postNotification == IntPtr.Zero) return;

            ((delegate* unmanaged[Cdecl]<uint, IntPtr, void>)s_postNotification)(notification, argument);
        }

        [DllImport("/usr/lib/libSystem.dylib")] private static extern IntPtr dlopen(string path, int mode);
        [DllImport("/usr/lib/libSystem.dylib")] private static extern IntPtr dlsym(IntPtr handle, string symbol);

        [StructLayout(LayoutKind.Sequential)]
        private struct CGRect { public double x; public double y; public double width; public double height; }

        [StructLayout(LayoutKind.Sequential)]
        private struct CGPoint { public double x; public double y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct NSRange { public ulong location; public ulong length; }
    }

    /// <summary>
    /// AutomationControlType and state to UIAccessibilityTraits.
    ///
    /// UIKit describes what an element DOES rather than what it is, so this is a bitmask union
    /// rather than a lookup: a checkbox is a button that can be selected, a slider is adjustable,
    /// and a heading is a header regardless of the control behind it.
    ///
    /// UIKit declares the traits as exported variables rather than an enum, so their values are only
    /// in the binary. These are the values its UIAccessibilityTrait* symbols hold; they are ABI and
    /// have not changed since the traits were introduced.
    /// </summary>
    internal static class UIKitTraits
    {
        internal const ulong None = 0;
        internal const ulong Button = 0x1;
        internal const ulong Link = 0x2;
        internal const ulong Image = 0x4;
        internal const ulong Selected = 0x8;
        internal const ulong StaticText = 0x40;
        internal const ulong NotEnabled = 0x100;
        internal const ulong UpdatesFrequently = 0x200;
        internal const ulong Adjustable = 0x1000;
        internal const ulong TabBar = 0x8000;
        internal const ulong Header = 0x10000;

        internal static ulong For(int automationControlType, AccessibleState state)
        {
            ulong traits = automationControlType switch
            {
                0 => Button,            // Button
                2 => Button,            // CheckBox      -- checked state rides in Selected
                3 => Button,            // ComboBox
                5 => Link,              // Hyperlink
                6 => Image,             // Image
                11 => Button,           // MenuItem
                12 => UpdatesFrequently,// ProgressBar
                13 => Button,           // RadioButton
                14 => Adjustable,       // ScrollBar
                15 => Adjustable,       // Slider
                16 => Adjustable,       // Spinner
                18 => TabBar,           // Tab
                19 => Button,           // TabItem
                20 => StaticText,       // Text
                27 => Adjustable,       // Thumb
                31 => Button,           // SplitButton
                34 => Header,           // Header
                35 => Header,           // HeaderItem
                37 => Header,           // TitleBar
                _ => None,
            };

            if ((state & AccessibleState.Enabled) == 0) traits |= NotEnabled;
            if ((state & (AccessibleState.Checked | AccessibleState.Selected)) != 0) traits |= Selected;
            if ((state & AccessibleState.HasRange) != 0) traits |= Adjustable;

            return traits;
        }
    }
}
