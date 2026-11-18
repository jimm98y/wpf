// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The macOS accessibility backend: WPF's automation tree, exposed through NSAccessibility.
//
// A WPF window on macOS is one NSView with a CAMetalLayer on it. Everything the user sees is drawn
// by the compositor, so to VoiceOver the whole app is a single blank rectangle -- there are no
// child views to describe. What this file adds is a parallel tree of accessibility elements, one per
// AutomationPeer, that VoiceOver navigates instead.
//
// NSAccessibility is an informal protocol: any NSObject that answers the right selectors is an
// accessibility element. So rather than subclassing NSAccessibilityElement (whose stored properties
// would duplicate state WPF already owns), the element class here is a bare NSObject holding one
// integer -- a node id -- and every answer is fetched live from the tree behind it. That keeps the
// two from drifting, and means a collected peer degrades to "nothing" rather than to a stale label.
//
// Coordinates are the one genuine impedance mismatch. WPF reports bounds in top-left device pixels;
// AppKit wants bottom-left screen points. The conversion lives in CocoaWindow beside the mouse
// path's, so both directions of the same flip stay together.
//

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace MS.Internal.Interop
{
    [SupportedOSPlatform("macos")]
    public static class CocoaAccessibility
    {
        /// <summary>True on macOS once AppKit is reachable; the tree itself may still be dormant.</summary>
        internal static bool IsAvailable => OperatingSystem.IsMacOS();

        /// <summary>
        /// True once VoiceOver (or any other client) has actually asked us something. Until then no
        /// tree is built at all -- see the note on the first-message trigger below.
        /// </summary>
        internal static bool IsAttached { get; private set; }

        private static IntPtr s_elementClass;
        private static nint s_nodeIdOffset;

        // One Objective-C element per node, kept alive for as long as the node is exposed. VoiceOver
        // compares elements by pointer identity when tracking focus, so handing out a fresh object
        // for the same node each time would make focus appear to jump to a different element.
        private static readonly Dictionary<int, IntPtr> s_elements = new();

        /// <summary>
        /// Called from CocoaWindow while it builds its content-view class, before the class is
        /// registered. Adds the handful of selectors that make the view an accessibility container
        /// whose one child is the WPF tree's root.
        /// </summary>
        public static void AddViewAccessibility(IntPtr viewClass)
        {
            EnsureElementClass();

            s_viewIsElement = ViewIsAccessibilityElementImp;
            s_viewRole = ViewRoleImp;
            s_viewChildren = ViewChildrenImp;
            s_viewHitTest = ViewHitTestImp;

            AddMethod(viewClass, "isAccessibilityElement", s_viewIsElement, "B@:");
            AddMethod(viewClass, "accessibilityRole", s_viewRole, "@@:");
            AddMethod(viewClass, "accessibilityChildren", s_viewChildren, "@@:");
            AddMethod(viewClass, "accessibilityHitTest:", s_viewHitTest, "@@:{CGPoint=dd}");
        }

        /// <summary>
        /// Something asked an accessibility question, which on macOS is the only reliable signal that
        /// an assistive technology exists: AppKit does not send these selectors otherwise. It is the
        /// same trigger Windows gets from WM_GETOBJECT, and it is why the tree costs nothing in the
        /// overwhelmingly common case of no screen reader.
        /// </summary>
        private static void NoteAccessibilityClient()
        {
            if (IsAttached) return;
            IsAttached = true;

            // The tree lives in PresentationCore, which cannot be referenced from here; the request
            // travels up through the seam instead.
            AutomationTree.RequestActivation();
            AutomationTree.Changed += OnTreeChanged;
        }

        private static IAutomationTreeSource Tree => AutomationTree.Current;

        // ---- the element class -----------------------------------------------------

        private static void EnsureElementClass()
        {
            if (s_elementClass != IntPtr.Zero) return;

            // Re-registering an existing class pair aborts the process, so look it up first.
            IntPtr existing = objc_getClass("WpfAccessibilityElement");
            if (existing != IntPtr.Zero)
            {
                s_elementClass = existing;
                s_nodeIdOffset = ivar_getOffset(class_getInstanceVariable(existing, "_nodeId"));
                return;
            }

            IntPtr nsobject = objc_getClass("NSObject");
            if (nsobject == IntPtr.Zero) return;

            IntPtr cls = objc_allocateClassPair(nsobject, "WpfAccessibilityElement", UIntPtr.Zero);
            if (cls == IntPtr.Zero) return;

            class_addIvar(cls, "_nodeId", (UIntPtr)8, 3, "q");

            // Held in static fields: Objective-C keeps only the raw function pointer, so a collected
            // delegate would leave the class calling into freed memory.
            s_isElement = IsElementImp;
            s_role = RoleImp;
            s_roleDescription = RoleDescriptionImp;
            s_label = LabelImp;
            s_help = HelpImp;
            s_identifier = IdentifierImp;
            s_value = ValueImp;
            s_children = ChildrenImp;
            s_parent = ParentImp;
            s_frame = FrameImp;
            s_enabled = EnabledImp;
            s_focused = FocusedImp;
            s_setFocused = SetFocusedImp;
            s_press = PressImp;
            s_minValue = MinValueImp;
            s_maxValue = MaxValueImp;
            s_setValue = SetValueImp;
            s_hitTest = HitTestImp;

            AddMethod(cls, "isAccessibilityElement", s_isElement, "B@:");
            AddMethod(cls, "accessibilityRole", s_role, "@@:");
            AddMethod(cls, "accessibilityRoleDescription", s_roleDescription, "@@:");
            AddMethod(cls, "accessibilityLabel", s_label, "@@:");
            AddMethod(cls, "accessibilityHelp", s_help, "@@:");
            AddMethod(cls, "accessibilityIdentifier", s_identifier, "@@:");
            AddMethod(cls, "accessibilityValue", s_value, "@@:");
            AddMethod(cls, "accessibilityChildren", s_children, "@@:");
            AddMethod(cls, "accessibilityParent", s_parent, "@@:");
            AddMethod(cls, "accessibilityFrame", s_frame, "{CGRect={CGPoint=dd}{CGSize=dd}}@:");
            AddMethod(cls, "isAccessibilityEnabled", s_enabled, "B@:");
            AddMethod(cls, "isAccessibilityFocused", s_focused, "B@:");
            AddMethod(cls, "setAccessibilityFocused:", s_setFocused, "v@:B");
            AddMethod(cls, "accessibilityPerformPress", s_press, "B@:");
            AddMethod(cls, "accessibilityMinValue", s_minValue, "@@:");
            AddMethod(cls, "accessibilityMaxValue", s_maxValue, "@@:");
            AddMethod(cls, "setAccessibilityValue:", s_setValue, "v@:@");
            AddMethod(cls, "accessibilityHitTest:", s_hitTest, "@@:{CGPoint=dd}");

            AddPatternMethods(cls);

            objc_registerClassPair(cls);

            s_elementClass = cls;
            s_nodeIdOffset = ivar_getOffset(class_getInstanceVariable(cls, "_nodeId"));
        }

        private static void AddMethod(IntPtr cls, string selector, Delegate impl, string types)
            => class_addMethod(cls, Sel(selector), Marshal.GetFunctionPointerForDelegate(impl), types);

        /// <summary>The element for a node, created once and reused so pointer identity is stable.</summary>
        private static unsafe IntPtr ElementFor(int nodeId)
        {
            if (nodeId < 0 || s_elementClass == IntPtr.Zero) return IntPtr.Zero;

            if (s_elements.TryGetValue(nodeId, out IntPtr existing)) return existing;

            IntPtr element = Send(Send(s_elementClass, Sel("alloc")), Sel("init"));
            if (element == IntPtr.Zero) return IntPtr.Zero;

            *(long*)((byte*)element + s_nodeIdOffset) = nodeId;
            s_elements[nodeId] = element;
            return element;
        }

        private static unsafe int NodeOf(IntPtr element)
            => element == IntPtr.Zero || s_elementClass == IntPtr.Zero
                ? IAutomationTreeSource.InvalidNode
                : (int)*(long*)((byte*)element + s_nodeIdOffset);

        // ---- the view's own answers ------------------------------------------------

        private delegate byte BoolImpl(IntPtr self, IntPtr sel);
        private delegate IntPtr PtrImpl(IntPtr self, IntPtr sel);
        private delegate void VoidBoolImpl(IntPtr self, IntPtr sel, byte value);
        private delegate void VoidPtrImpl(IntPtr self, IntPtr sel, IntPtr value);
        private delegate CGRect RectImpl(IntPtr self, IntPtr sel);
        private delegate IntPtr HitImpl(IntPtr self, IntPtr sel, CGPoint point);

        private static BoolImpl s_viewIsElement, s_isElement, s_enabled, s_focused, s_press;
        private static PtrImpl s_viewRole, s_viewChildren, s_role, s_roleDescription, s_label,
                               s_help, s_identifier, s_value, s_children, s_parent,
                               s_minValue, s_maxValue;
        private static VoidBoolImpl s_setFocused;
        private static VoidPtrImpl s_setValue;
        private static RectImpl s_frame;
        private static HitImpl s_viewHitTest, s_hitTest;

        // The view is a container, not an element in its own right: VoiceOver should describe the
        // controls inside it, never the blank Metal surface.
        private static byte ViewIsAccessibilityElementImp(IntPtr self, IntPtr sel)
        {
            NoteAccessibilityClient();
            return 0;
        }

        private static IntPtr ViewRoleImp(IntPtr self, IntPtr sel)
        {
            NoteAccessibilityClient();
            return NSStr("AXGroup");
        }

        private static IntPtr ViewChildrenImp(IntPtr self, IntPtr sel)
        {
            NoteAccessibilityClient();
            try
            {
                IAutomationTreeSource tree = Tree;
                if (tree == null) return EmptyArray();

                int root = tree.GetRootId(CocoaWindow.HandleOf(self));   // the tree knows its window by HWND
                if (root < 0) return EmptyArray();

                IntPtr element = ElementFor(root);
                return element == IntPtr.Zero ? EmptyArray() : ArrayOf(element);
            }
            catch (Exception e) { Log("accessibilityChildren", e); return EmptyArray(); }
        }

        private static IntPtr ViewHitTestImp(IntPtr self, IntPtr sel, CGPoint point)
        {
            NoteAccessibilityClient();
            try
            {
                IAutomationTreeSource tree = Tree;
                if (tree == null) return IntPtr.Zero;

                CocoaWindow.ConvertCocoaPointsToScreenPixels(point.x, point.y, out double px, out double py);
                int hit = tree.HitTest(self, px, py);
                return hit < 0 ? IntPtr.Zero : ElementFor(hit);
            }
            catch (Exception e) { Log("accessibilityHitTest", e); return IntPtr.Zero; }
        }

        // ---- an element's answers --------------------------------------------------

        private static byte IsElementImp(IntPtr self, IntPtr sel) => 1;

        private static IntPtr RoleImp(IntPtr self, IntPtr sel)
            => Guarded(self, sel, static (tree, node) => NSStr(RoleOf(tree, node)), NSStr("AXUnknown"));

        private static string RoleOf(IAutomationTreeSource tree, int node)
            => CocoaRoles.RoleFor(tree.GetRole(node), tree.GetState(node));

        private static IntPtr RoleDescriptionImp(IntPtr self, IntPtr sel)
        {
            // Let AppKit supply the localised description for the role we reported; it knows the
            // user's language and we do not.
            IntPtr role = RoleImp(self, sel);
            return SendPtrPtr(objc_getClass("NSAccessibilityElement"), Sel("accessibilityRoleDescription:"), role) is IntPtr d
                   && d != IntPtr.Zero ? d : IntPtr.Zero;
        }

        private static IntPtr LabelImp(IntPtr self, IntPtr sel)
            => Guarded(self, sel, static (tree, node) => NSStr(tree.GetName(node)), IntPtr.Zero);

        private static IntPtr HelpImp(IntPtr self, IntPtr sel)
            => Guarded(self, sel, static (tree, node) => NSStr(tree.GetHelpText(node)), IntPtr.Zero);

        private static IntPtr IdentifierImp(IntPtr self, IntPtr sel)
            => Guarded(self, sel, static (tree, node) => NSStr(tree.GetAutomationId(node)), IntPtr.Zero);

        private static IntPtr ValueImp(IntPtr self, IntPtr sel)
            => Guarded(self, sel, static (tree, node) =>
            {
                // A checkbox's value is its checked state, as a number; everything else is text.
                AccessibleState state = tree.GetState(node);
                if ((state & (AccessibleState.Checked | AccessibleState.Indeterminate)) != 0 ||
                    CocoaRoles.IsToggleRole(tree.GetRole(node)))
                {
                    int v = (state & AccessibleState.Indeterminate) != 0 ? 2
                          : (state & AccessibleState.Checked) != 0 ? 1 : 0;
                    return NSNumber(v);
                }

                if (tree.TryGetRange(node, out double value, out _, out _)) return NSDouble(value);

                // A text control's AXValue is its whole text, which is what every range attribute
                // counts into. The Value pattern's string can differ from it (a password box masks
                // it, a rich text box has none), and VoiceOver reading one while indexing the other
                // would speak the wrong characters.
                if (IsText(state) && tree.TryGetText(node, out string text)) return NSStr(text);
                return NSStr(tree.GetValue(node));
            }, IntPtr.Zero);

        private static IntPtr MinValueImp(IntPtr self, IntPtr sel)
            => Guarded(self, sel, static (tree, node)
                => tree.TryGetRange(node, out _, out double min, out _) ? NSDouble(min) : IntPtr.Zero, IntPtr.Zero);

        private static IntPtr MaxValueImp(IntPtr self, IntPtr sel)
            => Guarded(self, sel, static (tree, node)
                => tree.TryGetRange(node, out _, out _, out double max) ? NSDouble(max) : IntPtr.Zero, IntPtr.Zero);

        private static IntPtr ChildrenImp(IntPtr self, IntPtr sel)
            => Guarded(self, sel, static (tree, node) =>
            {
                int[] children = tree.GetChildIds(node);
                if (children.Length == 0) return EmptyArray();

                IntPtr array = Send(objc_getClass("NSMutableArray"), Sel("array"));
                foreach (int child in children)
                {
                    IntPtr element = ElementFor(child);
                    if (element != IntPtr.Zero) SendVoidPtr(array, Sel("addObject:"), element);
                }
                return array;
            }, EmptyArray());

        private static IntPtr ParentImp(IntPtr self, IntPtr sel)
            => Guarded(self, sel, static (tree, node) =>
            {
                int parent = tree.GetParentId(node);
                return parent < 0 ? IntPtr.Zero : ElementFor(parent);
            }, IntPtr.Zero);

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

                return ToCocoaRect(x, y, w, h);
            }
            catch (Exception e) { Log("accessibilityFrame", e); return default; }
        }

        private static byte EnabledImp(IntPtr self, IntPtr sel)
            => GuardedBool(self, static (tree, node) => (tree.GetState(node) & AccessibleState.Enabled) != 0, true);

        private static byte FocusedImp(IntPtr self, IntPtr sel)
            => GuardedBool(self, static (tree, node) => (tree.GetState(node) & AccessibleState.Focused) != 0, false);

        private static void SetFocusedImp(IntPtr self, IntPtr sel, byte value)
        {
            if (value == 0) return;   // Clearing focus is WPF's business, not an AT's.
            try
            {
                IAutomationTreeSource tree = Tree;
                int node = NodeOf(self);
                if (tree != null && node >= 0) tree.SetFocus(node);
            }
            catch (Exception e) { Log("setAccessibilityFocused", e); }
        }

        private static byte PressImp(IntPtr self, IntPtr sel)
        {
            try
            {
                IAutomationTreeSource tree = Tree;
                int node = NodeOf(self);
                if (tree == null || node < 0) return 0;

                // AXPress is one selector but several WPF patterns; try them in the order a user
                // means them. A checkbox has both Invoke and Toggle in some themes, and toggling is
                // what pressing it does.
                foreach (AccessibleAction action in s_pressOrder)
                {
                    if (tree.SupportsAction(node, action)) return (byte)(tree.DoAction(node, action) ? 1 : 0);
                }
                return 0;
            }
            catch (Exception e) { Log("accessibilityPerformPress", e); return 0; }
        }

        private static readonly AccessibleAction[] s_pressOrder =
        {
            AccessibleAction.Toggle, AccessibleAction.Invoke, AccessibleAction.Select, AccessibleAction.Expand,
        };

        private static void SetValueImp(IntPtr self, IntPtr sel, IntPtr value)
        {
            try
            {
                IAutomationTreeSource tree = Tree;
                int node = NodeOf(self);
                if (tree == null || node < 0) return;

                tree.SetValue(node, ReadString(value));
            }
            catch (Exception e) { Log("setAccessibilityValue", e); }
        }

        private static IntPtr HitTestImp(IntPtr self, IntPtr sel, CGPoint point)
        {
            try
            {
                IAutomationTreeSource tree = Tree;
                if (tree == null) return self;

                CocoaWindow.ConvertCocoaPointsToScreenPixels(point.x, point.y, out double px, out double py);
                int hit = tree.HitTest(IntPtr.Zero, px, py);
                return hit < 0 ? self : ElementFor(hit);
            }
            catch (Exception e) { Log("hitTest", e); return self; }
        }

        // ---- patterns: text, selection, tables, scrolling, windows -----------------
        //
        // These are the NSAccessibility protocol's own accessors, the same ones NSTextView and
        // NSTableView implement. AppKit derives the attribute list VoiceOver sees from which of
        // them an object answers, and asks -isAccessibilitySelectorAllowed: per selector before
        // listing one. Every element here shares one class, so the class answers all of them and
        // that method narrows them per node to the patterns the node actually has -- otherwise a
        // button would claim AXNumberOfCharacters and a text box AXRowCount.
        //
        // The PARAMETERIZED list is the exception: for an object that is not an NSView AppKit
        // reports the same fixed set (every text one, AXCellForColumnAndRow) whatever it answers,
        // and does not filter it. So each parameterized accessor also checks its node and answers
        // nil -- or NSNotFound -- for one without the pattern, which is what a client gets from an
        // AppKit control asked a question that does not apply to it.

        private delegate long IntImpl(IntPtr self, IntPtr sel);
        private delegate long IntIntImpl(IntPtr self, IntPtr sel, long value);
        private delegate NSRange RangeImpl(IntPtr self, IntPtr sel);
        private delegate NSRange RangeIntImpl(IntPtr self, IntPtr sel, long value);
        private delegate NSRange RangePointImpl(IntPtr self, IntPtr sel, CGPoint point);
        private delegate void VoidRangeImpl(IntPtr self, IntPtr sel, NSRange range);
        private delegate IntPtr PtrRangeImpl(IntPtr self, IntPtr sel, NSRange range);
        private delegate CGRect RectRangeImpl(IntPtr self, IntPtr sel, NSRange range);
        private delegate IntPtr PtrIntIntImpl(IntPtr self, IntPtr sel, long a, long b);
        private delegate byte BoolSelImpl(IntPtr self, IntPtr sel, IntPtr selector);

        private const string RangeType = "{_NSRange=QQ}";
        private const string RectType = "{CGRect={CGPoint=dd}{CGSize=dd}}";

        // NSNotFound, the location AppKit uses for "no such range".
        private const ulong NotFound = long.MaxValue;

        // Held for the same reason as the delegates above: Objective-C keeps only the raw pointer.
        private static readonly List<Delegate> s_patternImps = new();

        // selector -> which nodes may answer it
        private static readonly Dictionary<IntPtr, Func<IAutomationTreeSource, int, AccessibleState, bool>> s_gates = new();

        private static void AddPatternMethods(IntPtr cls)
        {
            void Add(string selector, Delegate impl, string types, Func<IAutomationTreeSource, int, AccessibleState, bool> gate)
            {
                s_patternImps.Add(impl);
                AddMethod(cls, selector, impl, types);
                if (gate != null) s_gates[Sel(selector)] = gate;
            }

            static bool Text(IAutomationTreeSource t, int n, AccessibleState s) => IsText(s);
            static bool Selection(IAutomationTreeSource t, int n, AccessibleState s) => (s & AccessibleState.SelectionContainer) != 0;
            static bool Rows(IAutomationTreeSource t, int n, AccessibleState s) => IsTableRole(CocoaRoles.RoleFor(t.GetRole(n), s));
            static bool SelectedRows(IAutomationTreeSource t, int n, AccessibleState s) => Selection(t, n, s) && Rows(t, n, s);
            static bool Grid(IAutomationTreeSource t, int n, AccessibleState s) => (s & AccessibleState.Grid) != 0;
            static bool Table(IAutomationTreeSource t, int n, AccessibleState s) => (s & AccessibleState.Table) != 0;
            static bool GridItem(IAutomationTreeSource t, int n, AccessibleState s) => (s & AccessibleState.GridItem) != 0;
            static bool Modal(IAutomationTreeSource t, int n, AccessibleState s) => (s & (AccessibleState.Window | AccessibleState.Modal)) != 0;
            static bool Window(IAutomationTreeSource t, int n, AccessibleState s)
                => (s & AccessibleState.Window) != 0 && t.TryGetWindow(n, out _, out _, out _, out _);
            static bool Minimizable(IAutomationTreeSource t, int n, AccessibleState s)
                => (s & AccessibleState.Window) != 0 && t.TryGetWindow(n, out _, out bool canMinimize, out _, out _) && canMinimize;

            // text
            Add("accessibilityNumberOfCharacters", (IntImpl)NumberOfCharactersImp, "q@:", Text);
            Add("accessibilitySelectedText", (PtrImpl)SelectedTextImp, "@@:", Text);
            Add("accessibilitySelectedTextRange", (RangeImpl)SelectedTextRangeImp, RangeType + "@:", Text);
            Add("setAccessibilitySelectedTextRange:", (VoidRangeImpl)SetSelectedTextRangeImp, "v@:" + RangeType, Text);
            Add("accessibilityVisibleCharacterRange", (RangeImpl)VisibleCharacterRangeImp, RangeType + "@:", Text);
            Add("accessibilityInsertionPointLineNumber", (IntImpl)InsertionPointLineNumberImp, "q@:", Text);
            Add("accessibilityStringForRange:", (PtrRangeImpl)StringForRangeImp, "@@:" + RangeType, Text);
            Add("accessibilityAttributedStringForRange:", (PtrRangeImpl)AttributedStringForRangeImp, "@@:" + RangeType, Text);
            Add("accessibilityRangeForLine:", (RangeIntImpl)RangeForLineImp, RangeType + "@:q", Text);
            Add("accessibilityLineForIndex:", (IntIntImpl)LineForIndexImp, "q@:q", Text);
            Add("accessibilityFrameForRange:", (RectRangeImpl)FrameForRangeImp, RectType + "@:" + RangeType, Text);
            Add("accessibilityRangeForPosition:", (RangePointImpl)RangeForPositionImp, RangeType + "@:{CGPoint=dd}", Text);
            Add("accessibilityRangeForIndex:", (RangeIntImpl)RangeForIndexImp, RangeType + "@:q", Text);
            Add("accessibilityStyleRangeForIndex:", (RangeIntImpl)StyleRangeForIndexImp, RangeType + "@:q", Text);

            // selection
            Add("accessibilitySelectedChildren", (PtrImpl)SelectedChildrenImp, "@@:", Selection);
            Add("setAccessibilitySelectedChildren:", (VoidPtrImpl)SetSelectedChildrenImp, "v@:@", Selection);
            Add("accessibilitySelectedRows", (PtrImpl)SelectedChildrenImp, "@@:", SelectedRows);
            Add("setAccessibilitySelectedRows:", (VoidPtrImpl)SetSelectedChildrenImp, "v@:@", SelectedRows);

            // tables
            Add("accessibilityRows", (PtrImpl)RowsImp, "@@:", Rows);
            Add("accessibilityRowCount", (IntImpl)RowCountImp, "q@:", Grid);
            Add("accessibilityColumnCount", (IntImpl)ColumnCountImp, "q@:", Grid);
            Add("accessibilityCellForColumn:row:", (PtrIntIntImpl)CellForColumnAndRowImp, "@@:qq", Grid);
            Add("accessibilityColumnHeaderUIElements", (PtrImpl)ColumnHeadersImp, "@@:", Table);
            Add("accessibilityRowIndexRange", (RangeImpl)RowIndexRangeImp, RangeType + "@:", GridItem);
            Add("accessibilityColumnIndexRange", (RangeImpl)ColumnIndexRangeImp, RangeType + "@:", GridItem);

            // windows. The close and minimize BUTTONS are the native NSWindow's, which AppKit
            // already exposes; inventing elements for them here would give VoiceOver two of each.
            Add("isAccessibilityModal", (BoolImpl)ModalImp, "B@:", Modal);
            Add("isAccessibilityMinimized", (BoolImpl)MinimizedImp, "B@:", Window);
            Add("setAccessibilityMinimized:", (VoidBoolImpl)SetMinimizedImp, "v@:B", Minimizable);

            // Scrolling has no protocol accessors: AppKit's scroll areas take their page actions
            // and AXScrollToVisible through the original action API, so the element answers that
            // API too. AppKit asks each of those selectors separately (NSAccessibilityElement
            // itself implements none of them and relies on exactly that), but once the class
            // names its own actions it is asked for ALL of them there -- so the list carries
            // AXPress as well, and performing routes it back to the press accessor.
            Add("accessibilityActionNames", (PtrImpl)ActionNamesImp, "@@:", null);
            Add("accessibilityPerformAction:", (VoidPtrImpl)PerformActionImp, "v@:@", null);
            Add("accessibilityActionDescription:", (PtrPtrImpl)ActionDescriptionImp, "@@:@", null);

            // Press was offered by every element, text and images included, because the class has
            // the accessor; now that selectors are narrowed per node it can be offered honestly.
            s_gates[Sel("accessibilityPerformPress")] = static (t, n, s) => CanPress(t, n);

            // Likewise AXMinValue/AXMaxValue, which only a range control has.
            static bool Range(IAutomationTreeSource t, int n, AccessibleState s) => t.TryGetRange(n, out _, out _, out _);
            s_gates[Sel("accessibilityMinValue")] = Range;
            s_gates[Sel("accessibilityMaxValue")] = Range;

            Add("isAccessibilitySelectorAllowed:", (BoolSelImpl)SelectorAllowedImp, "B@::", null);
            Add("accessibilityIsAttributeSettable:", (BoolSelImpl)SettableImp, "B@:@", null);
        }

        private static byte SelectorAllowedImp(IntPtr self, IntPtr sel, IntPtr selector)
        {
            try
            {
                if (s_gates.TryGetValue(selector, out var gate))
                {
                    IAutomationTreeSource tree = Tree;
                    int node = NodeOf(self);
                    if (tree == null || node < 0) return 0;
                    return (byte)(gate(tree, node, tree.GetState(node)) ? 1 : 0);
                }

                // Not one of ours: allowed, which is what NSObject would have said had it an
                // opinion -- it implements no accessibility methods of its own to ask.
                return 1;
            }
            catch (Exception e) { Log("isAccessibilitySelectorAllowed", e); return 0; }
        }

        // The attributes a client may set, by the setter that sets them.
        private static readonly Dictionary<string, string> s_setters = new()
        {
            ["AXValue"] = "setAccessibilityValue:",
            ["AXFocused"] = "setAccessibilityFocused:",
            ["AXSelectedTextRange"] = "setAccessibilitySelectedTextRange:",
            ["AXSelectedChildren"] = "setAccessibilitySelectedChildren:",
            ["AXSelectedRows"] = "setAccessibilitySelectedRows:",
            ["AXMinimized"] = "setAccessibilityMinimized:",
        };

        /// <summary>
        /// The original API's settability question. AppKit answers it from the setter for an
        /// allowed selector, but for one -isAccessibilitySelectorAllowed: refuses it falls back to
        /// this -- and with nothing here it says YES, so a window that cannot be minimized would
        /// still offer AXMinimized. The answer is the same rule either way: the setter exists and
        /// the node is allowed it.
        /// </summary>
        private static byte SettableImp(IntPtr self, IntPtr sel, IntPtr attribute)
        {
            try
            {
                if (!s_setters.TryGetValue(ReadString(attribute), out string setter)) return 0;
                return SelectorAllowedImp(self, sel, Sel(setter));
            }
            catch (Exception e) { Log("accessibilityIsAttributeSettable", e); return 0; }
        }

        internal static bool IsText(AccessibleState state)
            => (state & AccessibleState.HasText) != 0 && (state & AccessibleState.Password) == 0;

        internal static bool IsTableRole(string role) => role is "AXTable" or "AXOutline";

        private static T GuardedValue<T>(IntPtr self, string what, Func<IAutomationTreeSource, int, T> get, T fallback)
        {
            try
            {
                IAutomationTreeSource tree = Tree;
                int node = NodeOf(self);
                if (tree == null || node < 0) return fallback;
                return get(tree, node);
            }
            catch (Exception e) { Log(what, e); return fallback; }
        }

        private static void GuardedAction(IntPtr self, string what, Action<IAutomationTreeSource, int> act)
        {
            try
            {
                IAutomationTreeSource tree = Tree;
                int node = NodeOf(self);
                if (tree != null && node >= 0) act(tree, node);
            }
            catch (Exception e) { Log(what, e); }
        }

        // ---- text ----

        private static string TextOf(IAutomationTreeSource tree, int node)
            => IsText(tree.GetState(node)) && tree.TryGetText(node, out string text) ? text ?? string.Empty : null;

        /// <summary>
        /// An AppKit range clipped to the text. Ranges arrive from the client unchecked --
        /// NSNotFound, or past the end after an edit it has not heard about yet -- and a substring
        /// that threw would only end up as a nil answer anyway.
        /// </summary>
        private static (int Start, int Length) Clip(NSRange range, int textLength)
        {
            ulong start = Math.Min(range.location, (ulong)textLength);
            ulong length = Math.Min(range.length, (ulong)textLength - start);
            return ((int)start, (int)length);
        }

        private static NSRange RangeOf(int start, int length)
            => new NSRange { location = (ulong)Math.Max(start, 0), length = (ulong)Math.Max(length, 0) };

        private static NSRange NoRange => new NSRange { location = NotFound, length = 0 };

        private static long NumberOfCharactersImp(IntPtr self, IntPtr sel)
            => GuardedValue(self, "accessibilityNumberOfCharacters", static (tree, node) => (long)(TextOf(tree, node)?.Length ?? 0), 0L);

        private static (int Start, int Length) SelectionOf(IAutomationTreeSource tree, int node, string text)
        {
            if (!tree.TryGetTextSelection(node, out int start, out int length)) return (0, 0);
            return Clip(RangeOf(start, length), text.Length);
        }

        private static IntPtr SelectedTextImp(IntPtr self, IntPtr sel)
            => Guarded(self, sel, static (tree, node) =>
            {
                string text = TextOf(tree, node);
                if (text == null) return IntPtr.Zero;
                (int start, int length) = SelectionOf(tree, node, text);
                return NSStr(text.Substring(start, length));
            }, IntPtr.Zero);

        private static NSRange SelectedTextRangeImp(IntPtr self, IntPtr sel)
            => GuardedValue(self, "accessibilitySelectedTextRange", static (tree, node) =>
            {
                string text = TextOf(tree, node);
                if (text == null) return NoRange;
                (int start, int length) = SelectionOf(tree, node, text);
                return RangeOf(start, length);
            }, NoRange);

        private static void SetSelectedTextRangeImp(IntPtr self, IntPtr sel, NSRange range)
            => GuardedAction(self, "setAccessibilitySelectedTextRange", (tree, node) =>
            {
                string text = TextOf(tree, node);
                if (text == null) return;
                (int start, int length) = Clip(range, text.Length);
                tree.SetTextSelection(node, start, length);
            });

        private static NSRange VisibleCharacterRangeImp(IntPtr self, IntPtr sel)
            => GuardedValue(self, "accessibilityVisibleCharacterRange", static (tree, node) =>
            {
                string text = TextOf(tree, node);
                if (text == null) return NoRange;

                // Without layout to ask, all of it: VoiceOver reads the visible range when the user
                // asks for "what is on screen", and saying nothing would be worse than saying it all.
                if (!tree.TryGetVisibleTextRange(node, out int start, out int length)) return RangeOf(0, text.Length);
                (int s, int l) = Clip(RangeOf(start, length), text.Length);
                return RangeOf(s, l);
            }, NoRange);

        private static long InsertionPointLineNumberImp(IntPtr self, IntPtr sel)
            => GuardedValue(self, "accessibilityInsertionPointLineNumber", static (tree, node) =>
            {
                string text = TextOf(tree, node);
                if (text == null) return 0L;
                (int start, _) = SelectionOf(tree, node, text);
                return (long)LineOf(tree, node, start);
            }, 0L);

        private static int LineOf(IAutomationTreeSource tree, int node, int offset)
            => tree.TryGetTextLine(node, offset, out _, out _, out int number) ? Math.Max(number, 0) : 0;

        private static IntPtr StringForRangeImp(IntPtr self, IntPtr sel, NSRange range)
            => GuardedValue(self, "accessibilityStringForRange", (tree, node) =>
            {
                string text = TextOf(tree, node);
                if (text == null) return IntPtr.Zero;
                (int start, int length) = Clip(range, text.Length);
                return NSStr(text.Substring(start, length));
            }, IntPtr.Zero);

        private static IntPtr AttributedStringForRangeImp(IntPtr self, IntPtr sel, NSRange range)
            => GuardedValue(self, "accessibilityAttributedStringForRange", (tree, node) =>
            {
                string text = TextOf(tree, node);
                if (text == null) return IntPtr.Zero;
                (int start, int length) = Clip(range, text.Length);

                // Plain: WPF's text pattern does carry fonts and colours, but VoiceOver reads only
                // the characters by default and an attribute it misreads is worse than none.
                IntPtr cls = objc_getClass("NSAttributedString");
                if (cls == IntPtr.Zero) return IntPtr.Zero;
                IntPtr attributed = SendPtrPtr(Send(cls, Sel("alloc")), Sel("initWithString:"), NSStr(text.Substring(start, length)));
                return attributed == IntPtr.Zero ? IntPtr.Zero : Send(attributed, Sel("autorelease"));
            }, IntPtr.Zero);

        private static NSRange RangeForLineImp(IntPtr self, IntPtr sel, long line)
            => GuardedValue(self, "accessibilityRangeForLine", (tree, node) =>
            {
                string text = TextOf(tree, node);
                if (text == null || line < 0) return NoRange;
                return RangeForLine(tree, node, text, line);
            }, NoRange);

        /// <summary>
        /// The tree answers "which line is this offset on", never "where is line N", so the lines
        /// are walked from the top. Each step starts at the end of the line before; a line that
        /// does not end where the tree says (an empty last line, a line reported without its
        /// break) is stepped over one character at a time so the walk always makes progress.
        /// </summary>
        private static NSRange RangeForLine(IAutomationTreeSource tree, int node, string text, long line)
        {
            if (!tree.TryGetTextLine(node, 0, out _, out _, out _))
            {
                // No line geometry at all: the text is one line.
                return line == 0 ? RangeOf(0, text.Length) : NoRange;
            }

            int offset = 0;
            while (offset <= text.Length)
            {
                if (!tree.TryGetTextLine(node, offset, out int start, out int length, out int number)) break;
                if (number == line)
                {
                    (int s, int l) = Clip(RangeOf(start, length), text.Length);
                    return RangeOf(s, l);
                }
                if (number > line) break;

                int next = start + length;
                offset = next > offset ? next : offset + 1;
            }
            return NoRange;
        }

        private static long LineForIndexImp(IntPtr self, IntPtr sel, long index)
            => GuardedValue(self, "accessibilityLineForIndex", (tree, node) =>
            {
                string text = TextOf(tree, node);
                if (text == null) return 0L;
                return (long)LineOf(tree, node, (int)Math.Clamp(index, 0, text.Length));
            }, 0L);

        private static CGRect FrameForRangeImp(IntPtr self, IntPtr sel, NSRange range)
            => GuardedValue(self, "accessibilityFrameForRange", (tree, node) =>
            {
                string text = TextOf(tree, node);
                if (text == null) return default;
                (int start, int length) = Clip(range, text.Length);

                if (!tree.TryGetTextRangeBounds(node, start, length, out double x, out double y, out double w, out double h))
                {
                    return default;
                }
                return ToCocoaRect(x, y, w, h);
            }, default(CGRect));

        private static NSRange RangeForPositionImp(IntPtr self, IntPtr sel, CGPoint point)
            => GuardedValue(self, "accessibilityRangeForPosition", (tree, node) =>
            {
                string text = TextOf(tree, node);
                if (text == null) return NoRange;

                CocoaWindow.ConvertCocoaPointsToScreenPixels(point.x, point.y, out double px, out double py);
                int offset = tree.GetTextOffsetAtPoint(node, px, py);
                return offset < 0 ? NoRange : ComposedRangeAt(text, offset);
            }, NoRange);

        private static NSRange RangeForIndexImp(IntPtr self, IntPtr sel, long index)
            => GuardedValue(self, "accessibilityRangeForIndex", (tree, node) =>
            {
                string text = TextOf(tree, node);
                if (text == null || index < 0 || index > text.Length) return NoRange;
                return ComposedRangeAt(text, (int)index);
            }, NoRange);

        /// <summary>
        /// The user-perceived character around an offset: AppKit's "composed character sequence",
        /// so moving by character never lands VoiceOver between the halves of a surrogate pair or
        /// between a letter and its combining accent.
        /// </summary>
        internal static NSRange ComposedRangeAt(string text, int offset)
        {
            if (offset >= text.Length) return RangeOf(text.Length, 0);

            int[] starts = System.Globalization.StringInfo.ParseCombiningCharacters(text);
            int index = Array.BinarySearch(starts, offset);
            if (index < 0) index = ~index - 1;

            int begin = starts[index];
            int end = index + 1 < starts.Length ? starts[index + 1] : text.Length;
            return RangeOf(begin, end - begin);
        }

        private static NSRange StyleRangeForIndexImp(IntPtr self, IntPtr sel, long index)
            => GuardedValue(self, "accessibilityStyleRangeForIndex", static (tree, node) =>
            {
                // One run: the attributed strings above carry no style either, so the whole text
                // is uniformly styled as far as anything we report can tell.
                string text = TextOf(tree, node);
                return text == null ? NoRange : RangeOf(0, text.Length);
            }, NoRange);

        // ---- selection ----

        private static IntPtr SelectedChildrenImp(IntPtr self, IntPtr sel)
            => Guarded(self, sel, static (tree, node) => ElementsOf(tree.GetSelectedIds(node)), EmptyArray());

        /// <summary>
        /// The client picks a new selection. Selecting the first replaces whatever was selected
        /// and the rest are added to it, which is how a click and then command-clicks would build
        /// the same selection -- a single-selection container simply ends up with the first.
        /// </summary>
        private static void SetSelectedChildrenImp(IntPtr self, IntPtr sel, IntPtr array)
            => GuardedAction(self, "setAccessibilitySelectedChildren", (tree, node) =>
            {
                if (array == IntPtr.Zero) return;
                bool multiple = (tree.GetState(node) & AccessibleState.MultiSelectable) != 0;

                long count = (long)Send(array, Sel("count"));
                for (long i = 0; i < count; i++)
                {
                    int item = NodeOfElement(SendPtrLong(array, Sel("objectAtIndex:"), i));
                    if (item < 0) continue;

                    if (i == 0) tree.DoAction(item, AccessibleAction.Select);
                    else if (!multiple) break;
                    else if ((tree.GetState(item) & AccessibleState.Selected) == 0) tree.DoAction(item, AccessibleAction.ToggleSelection);
                }
            });

        /// <summary>A node id for an object the client handed back, or InvalidNode for anything that is not one of ours.</summary>
        private static int NodeOfElement(IntPtr obj)
        {
            if (obj == IntPtr.Zero || s_elementClass == IntPtr.Zero) return IAutomationTreeSource.InvalidNode;
            return SendBoolPtr(obj, Sel("isKindOfClass:"), s_elementClass) != 0 ? NodeOf(obj) : IAutomationTreeSource.InvalidNode;
        }

        private static IntPtr ElementsOf(int[] ids)
        {
            if (ids == null || ids.Length == 0) return EmptyArray();

            IntPtr array = Send(objc_getClass("NSMutableArray"), Sel("array"));
            foreach (int id in ids)
            {
                IntPtr element = ElementFor(id);
                if (element != IntPtr.Zero) SendVoidPtr(array, Sel("addObject:"), element);
            }
            return array;
        }

        // ---- tables ----

        /// <summary>
        /// A table's rows are the children that are rows -- a DataGrid's header presenter is a
        /// child too, and is not one. An outline's rows are every row the user can reach, nested
        /// ones included, because NSOutlineView reports its rows flat; the descent stops at a
        /// collapsed row, whose children are not on screen.
        /// </summary>
        private static IntPtr RowsImp(IntPtr self, IntPtr sel)
            => Guarded(self, sel, static (tree, node) =>
            {
                var rows = new List<int>();
                CollectRows(tree, node, RoleOf(tree, node) == "AXOutline", rows, depth: 0);
                return ElementsOf(rows.ToArray());
            }, EmptyArray());

        private static void CollectRows(IAutomationTreeSource tree, int node, bool nested, List<int> rows, int depth)
        {
            if (depth > 64) return;   // a cycle in a broken tree must not hang VoiceOver

            foreach (int child in tree.GetChildIds(node))
            {
                if (RoleOf(tree, child) != "AXRow") continue;
                rows.Add(child);
                if (nested && (tree.GetState(child) & AccessibleState.Collapsed) == 0)
                {
                    CollectRows(tree, child, nested, rows, depth + 1);
                }
            }
        }

        private static long RowCountImp(IntPtr self, IntPtr sel)
            => GuardedValue(self, "accessibilityRowCount", static (tree, node)
                => tree.TryGetGrid(node, out int rows, out _) ? (long)Math.Max(rows, 0) : 0L, 0L);

        private static long ColumnCountImp(IntPtr self, IntPtr sel)
            => GuardedValue(self, "accessibilityColumnCount", static (tree, node)
                => tree.TryGetGrid(node, out _, out int columns) ? (long)Math.Max(columns, 0) : 0L, 0L);

        private static IntPtr CellForColumnAndRowImp(IntPtr self, IntPtr sel, long column, long row)
            => GuardedValue(self, "accessibilityCellForColumnAndRow", (tree, node) =>
            {
                if (column < 0 || row < 0 || column > int.MaxValue || row > int.MaxValue) return IntPtr.Zero;
                int cell = tree.GetGridCell(node, (int)row, (int)column);
                return cell < 0 ? IntPtr.Zero : ElementFor(cell);
            }, IntPtr.Zero);

        private static IntPtr ColumnHeadersImp(IntPtr self, IntPtr sel)
            => Guarded(self, sel, static (tree, node) => ElementsOf(tree.GetColumnHeaderIds(node)), EmptyArray());

        private static NSRange RowIndexRangeImp(IntPtr self, IntPtr sel)
            => GuardedValue(self, "accessibilityRowIndexRange", static (tree, node)
                => tree.TryGetGridItem(node, out int row, out _, out int rowSpan, out _, out _)
                    ? RangeOf(row, Math.Max(rowSpan, 1)) : NoRange, NoRange);

        private static NSRange ColumnIndexRangeImp(IntPtr self, IntPtr sel)
            => GuardedValue(self, "accessibilityColumnIndexRange", static (tree, node)
                => tree.TryGetGridItem(node, out _, out int column, out _, out int columnSpan, out _)
                    ? RangeOf(column, Math.Max(columnSpan, 1)) : NoRange, NoRange);

        // ---- windows ----

        private static byte ModalImp(IntPtr self, IntPtr sel)
            => GuardedBool(self, static (tree, node) => (tree.GetState(node) & AccessibleState.Modal) != 0, false);

        private const int WindowVisualStateNormal = 0, WindowVisualStateMinimized = 2;

        private static byte MinimizedImp(IntPtr self, IntPtr sel)
            => GuardedBool(self, static (tree, node)
                => tree.TryGetWindow(node, out int state, out _, out _, out _) && state == WindowVisualStateMinimized, false);

        private static void SetMinimizedImp(IntPtr self, IntPtr sel, byte value)
            => GuardedAction(self, "setAccessibilityMinimized", (tree, node)
                => tree.SetWindowVisualState(node, value != 0 ? WindowVisualStateMinimized : WindowVisualStateNormal));

        // ---- scrolling ----

        // System.Windows.Automation.ScrollAmount
        private const int LargeDecrement = 0, NoAmount = 2, LargeIncrement = 3;

        internal const string ScrollUpByPage = "AXScrollUpByPage", ScrollDownByPage = "AXScrollDownByPage",
                              ScrollLeftByPage = "AXScrollLeftByPage", ScrollRightByPage = "AXScrollRightByPage",
                              ScrollToVisible = "AXScrollToVisible";

        private delegate IntPtr PtrPtrImpl(IntPtr self, IntPtr sel, IntPtr arg);

        internal const string Press = "AXPress";

        private static IntPtr ActionNamesImp(IntPtr self, IntPtr sel)
            => Guarded(self, sel, static (tree, node) =>
            {
                List<string> names = ActionNames(tree, node);
                if (names.Count == 0) return EmptyArray();

                IntPtr array = Send(objc_getClass("NSMutableArray"), Sel("array"));
                foreach (string name in names) SendVoidPtr(array, Sel("addObject:"), NSStr(name));
                return array;
            }, EmptyArray());

        private static bool CanPress(IAutomationTreeSource tree, int node)
        {
            foreach (AccessibleAction action in s_pressOrder)
            {
                if (tree.SupportsAction(node, action)) return true;
            }
            return false;
        }

        /// <summary>Every action the element offers through the action API, AXPress included.</summary>
        internal static List<string> ActionNames(IAutomationTreeSource tree, int node)
        {
            List<string> names = ExtraActions(tree, node);
            if (CanPress(tree, node)) names.Insert(0, Press);
            return names;
        }

        /// <summary>The actions AppKit cannot derive from a protocol accessor, as NSScrollView names them.</summary>
        internal static List<string> ExtraActions(IAutomationTreeSource tree, int node)
        {
            var names = new List<string>();
            AccessibleState state = tree.GetState(node);

            if ((state & AccessibleState.Scrollable) != 0 &&
                tree.TryGetScroll(node, out _, out _, out _, out _, out bool horizontal, out bool vertical))
            {
                if (vertical) { names.Add(ScrollUpByPage); names.Add(ScrollDownByPage); }
                if (horizontal) { names.Add(ScrollLeftByPage); names.Add(ScrollRightByPage); }
            }

            if ((state & AccessibleState.ScrollItem) != 0) names.Add(ScrollToVisible);
            return names;
        }

        private static void PerformActionImp(IntPtr self, IntPtr sel, IntPtr action)
        {
            try
            {
                IAutomationTreeSource tree = Tree;
                int node = NodeOf(self);
                if (tree == null || node < 0) return;

                string name = ReadString(action);
                if (name == Press) PressImp(self, sel);
                else PerformExtraAction(tree, node, name);
            }
            catch (Exception e) { Log("accessibilityPerformAction", e); }
        }

        private static IntPtr ActionDescriptionImp(IntPtr self, IntPtr sel, IntPtr action)
        {
            // AppKit's own localised wording, as for the role description.
            try { return action == IntPtr.Zero ? IntPtr.Zero : NSAccessibilityActionDescription(action); }
            catch (Exception e) { Log("accessibilityActionDescription", e); return IntPtr.Zero; }
        }

        /// <summary>Runs one of <see cref="ExtraActions"/>; false for a name that is not one of them.</summary>
        internal static bool PerformExtraAction(IAutomationTreeSource tree, int node, string name)
        {
            switch (name)
            {
                // "Up" is towards the start, which in UIA's terms is a decrement.
                case ScrollUpByPage: tree.Scroll(node, NoAmount, LargeDecrement); return true;
                case ScrollDownByPage: tree.Scroll(node, NoAmount, LargeIncrement); return true;
                case ScrollLeftByPage: tree.Scroll(node, LargeDecrement, NoAmount); return true;
                case ScrollRightByPage: tree.Scroll(node, LargeIncrement, NoAmount); return true;
                case ScrollToVisible: tree.ScrollIntoView(node); return true;
                default: return false;
            }
        }

        private static CGRect ToCocoaRect(double x, double y, double w, double h)
        {
            CocoaWindow.ConvertScreenPixelsToCocoaPoints(x, y, w, h,
                out double sx, out double sy, out double sw, out double sh);
            return new CGRect { x = sx, y = sy, width = sw, height = sh };
        }

        /// <summary>
        /// The element for a node, for tests that drive the class through objc_msgSend as AppKit
        /// would. Loads AppKit first: the class's accessors build AppKit objects, and a test
        /// process has not loaded it.
        /// </summary>
        internal static IntPtr ElementForTest(int nodeId)
        {
            if (!OperatingSystem.IsMacOS()) return IntPtr.Zero;
            const int RTLD_NOW = 2;
            if (dlopen("/System/Library/Frameworks/AppKit.framework/AppKit", RTLD_NOW) == IntPtr.Zero) return IntPtr.Zero;
            EnsureElementClass();
            return ElementFor(nodeId);
        }

        // ---- notifications out -----------------------------------------------------

        private static void OnTreeChanged(AutomationChange change)
        {
            try
            {
                IntPtr element = ElementFor(change.NodeId);
                if (element == IntPtr.Zero) return;

                NSAccessibilityPostNotification(element, NSStr(NotificationFor(Tree, change)));
            }
            catch (Exception e) { Log("postNotification", e); }
        }

        /// <summary>The NSAccessibility notification a change is announced with.</summary>
        internal static string NotificationFor(IAutomationTreeSource tree, AutomationChange change) => change.Kind switch
        {
            AutomationChangeKind.FocusChanged => "AXFocusedUIElementChanged",
            AutomationChangeKind.ValueChanged => "AXValueChanged",
            AutomationChangeKind.SelectionChanged => "AXSelectedChildrenChanged",
            AutomationChangeKind.ChildrenChanged => "AXLayoutChanged",
            AutomationChangeKind.ChildAdded => "AXLayoutChanged",
            AutomationChangeKind.ChildRemoved => "AXLayoutChanged",
            // NSAccessibility has no notification of its own for expanded/checked; both are
            // read back through AXValue, so that is what VoiceOver has to be told changed.
            AutomationChangeKind.ExpandedChanged => "AXValueChanged",
            AutomationChangeKind.CheckedChanged => "AXValueChanged",
            // The text is the element's AXValue (see ValueImp), so an edit is a value change --
            // which is what NSTextView posts as the user types.
            AutomationChangeKind.TextChanged => "AXValueChanged",
            AutomationChangeKind.TextSelectionChanged => "AXSelectedTextChanged",
            // A table's selection is its selected ROWS to VoiceOver, and it listens for the rows
            // notification there; NSTableView posts that one, not the children one.
            AutomationChangeKind.SelectionContainerChanged =>
                tree != null && IsTableRole(RoleOf(tree, change.NodeId)) ? "AXSelectedRowsChanged" : "AXSelectedChildrenChanged",
            // Scrolling moves every child of the scroll area without changing any of them, which is
            // exactly what the layout notification means; VoiceOver re-reads positions on it.
            AutomationChangeKind.ScrollChanged => "AXLayoutChanged",
            _ => "AXTitleChanged",
        };

        // ---- helpers ---------------------------------------------------------------

        private static IntPtr Guarded(IntPtr self, IntPtr sel, Func<IAutomationTreeSource, int, IntPtr> get, IntPtr fallback)
        {
            try
            {
                IAutomationTreeSource tree = Tree;
                int node = NodeOf(self);
                if (tree == null || node < 0) return fallback;
                return get(tree, node);
            }
            catch (Exception e) { Log("accessibility property", e); return fallback; }
        }

        private static byte GuardedBool(IntPtr self, Func<IAutomationTreeSource, int, bool> get, bool fallback)
        {
            try
            {
                IAutomationTreeSource tree = Tree;
                int node = NodeOf(self);
                if (tree == null || node < 0) return (byte)(fallback ? 1 : 0);
                return (byte)(get(tree, node) ? 1 : 0);
            }
            catch { return (byte)(fallback ? 1 : 0); }
        }

        private static void Log(string what, Exception e)
            => Console.WriteLine($"WPF macOS accessibility {what} failed: {e}");

        // ---- Objective-C runtime ----------------------------------------------------

        private const string ObjC = "/usr/lib/libobjc.A.dylib";

        private static IntPtr Sel(string name) => sel_registerName(name);

        private static IntPtr EmptyArray() => Send(objc_getClass("NSArray"), Sel("array"));

        private static IntPtr ArrayOf(IntPtr one)
            => SendPtrPtr(objc_getClass("NSArray"), Sel("arrayWithObject:"), one);

        private static IntPtr NSNumber(int value)
            => SendPtrInt(objc_getClass("NSNumber"), Sel("numberWithInt:"), value);

        private static IntPtr NSDouble(double value)
            => SendPtrDouble(objc_getClass("NSNumber"), Sel("numberWithDouble:"), value);

        private static IntPtr NSStr(string value)
        {
            if (string.IsNullOrEmpty(value)) return SendPtrPtr(objc_getClass("NSString"), Sel("string"), IntPtr.Zero);

            IntPtr bytes = Marshal.StringToCoTaskMemUTF8(value);
            try { return SendPtrPtr(objc_getClass("NSString"), Sel("stringWithUTF8String:"), bytes); }
            finally { Marshal.FreeCoTaskMem(bytes); }
        }

        private static string ReadString(IntPtr nsString)
        {
            if (nsString == IntPtr.Zero) return string.Empty;
            IntPtr utf8 = Send(nsString, Sel("UTF8String"));
            return utf8 == IntPtr.Zero ? string.Empty : (Marshal.PtrToStringUTF8(utf8) ?? string.Empty);
        }

        [DllImport(ObjC)] private static extern IntPtr objc_getClass(string name);
        [DllImport(ObjC)] private static extern IntPtr sel_registerName(string name);
        [DllImport(ObjC)] private static extern IntPtr objc_allocateClassPair(IntPtr super, string name, UIntPtr extra);
        [DllImport(ObjC)] private static extern void objc_registerClassPair(IntPtr cls);
        [DllImport(ObjC)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool class_addMethod(IntPtr cls, IntPtr sel, IntPtr imp, string types);
        [DllImport(ObjC)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool class_addIvar(IntPtr cls, string name, UIntPtr size, byte align, string types);
        [DllImport(ObjC)] private static extern IntPtr class_getInstanceVariable(IntPtr cls, string name);
        [DllImport(ObjC)] private static extern nint ivar_getOffset(IntPtr ivar);

        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr r, IntPtr s);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendVoidPtr(IntPtr r, IntPtr s, IntPtr a);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendPtrPtr(IntPtr r, IntPtr s, IntPtr a);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendPtrInt(IntPtr r, IntPtr s, int a);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendPtrDouble(IntPtr r, IntPtr s, double a);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendPtrLong(IntPtr r, IntPtr s, long a);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern byte SendBoolPtr(IntPtr r, IntPtr s, IntPtr a);

        // Resolved through dlsym rather than [DllImport("...AppKit")]. A DllImport naming a
        // framework by path is resolved EAGERLY by the iOS AOT linker, which then fails the whole
        // iOS build with "ld: framework 'AppKit' not found" -- this file is compiled into
        // WindowsBase for every head, macOS-only attribute notwithstanding. dlopen is how the rest
        // of this folder reaches frameworks for exactly that reason.
        private static IntPtr s_postNotification = (IntPtr)(-1);

        private static void NSAccessibilityPostNotification(IntPtr element, IntPtr notification)
        {
            if (s_postNotification == (IntPtr)(-1))
            {
                const int RTLD_NOW = 2;
                IntPtr appKit = dlopen("/System/Library/Frameworks/AppKit.framework/AppKit", RTLD_NOW);
                s_postNotification = appKit == IntPtr.Zero
                    ? IntPtr.Zero
                    : dlsym(appKit, "NSAccessibilityPostNotification");
            }

            if (s_postNotification == IntPtr.Zero) return;

            Marshal.GetDelegateForFunctionPointer<PostNotificationDelegate>(s_postNotification)(element, notification);
        }

        private delegate void PostNotificationDelegate(IntPtr element, IntPtr notification);

        private static IntPtr s_actionDescription = (IntPtr)(-1);

        private static IntPtr NSAccessibilityActionDescription(IntPtr action)
        {
            if (s_actionDescription == (IntPtr)(-1))
            {
                const int RTLD_NOW = 2;
                IntPtr appKit = dlopen("/System/Library/Frameworks/AppKit.framework/AppKit", RTLD_NOW);
                s_actionDescription = appKit == IntPtr.Zero ? IntPtr.Zero : dlsym(appKit, "NSAccessibilityActionDescription");
            }

            if (s_actionDescription == IntPtr.Zero) return IntPtr.Zero;
            return Marshal.GetDelegateForFunctionPointer<ActionDescriptionDelegate>(s_actionDescription)(action);
        }

        private delegate IntPtr ActionDescriptionDelegate(IntPtr action);

        [DllImport("/usr/lib/libSystem.dylib")] private static extern IntPtr dlopen(string path, int mode);
        [DllImport("/usr/lib/libSystem.dylib")] private static extern IntPtr dlsym(IntPtr handle, string symbol);

        [StructLayout(LayoutKind.Sequential)]
        private struct CGPoint { public double x; public double y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct CGRect { public double x; public double y; public double width; public double height; }

        [StructLayout(LayoutKind.Sequential)]
        internal struct NSRange { public ulong location; public ulong length; }
    }

    /// <summary>
    /// AutomationControlType to the NSAccessibility role vocabulary.
    ///
    /// Its own type so the table is testable without AppKit, and so the "what did we not map"
    /// question has one place to look. The numbers are AutomationControlType's values; the enum
    /// itself lives in PresentationCore, which this assembly cannot reference.
    /// </summary>
    internal static class CocoaRoles
    {
        internal static string RoleFor(int automationControlType) => automationControlType switch
        {
            0 => "AXButton",            // Button
            1 => "AXGroup",             // Calendar
            2 => "AXCheckBox",          // CheckBox
            3 => "AXPopUpButton",       // ComboBox
            4 => "AXTextField",         // Edit
            5 => "AXLink",              // Hyperlink
            6 => "AXImage",             // Image
            7 => "AXRow",               // ListItem
            8 => "AXList",              // List
            9 => "AXMenu",              // Menu
            10 => "AXMenuBar",          // MenuBar
            11 => "AXMenuItem",         // MenuItem
            12 => "AXProgressIndicator",// ProgressBar
            13 => "AXRadioButton",      // RadioButton
            14 => "AXScrollBar",        // ScrollBar
            15 => "AXSlider",           // Slider
            16 => "AXIncrementor",      // Spinner
            17 => "AXGroup",            // StatusBar
            18 => "AXTabGroup",         // Tab
            19 => "AXRadioButton",      // TabItem
            20 => "AXStaticText",       // Text
            21 => "AXToolbar",          // ToolBar
            22 => "AXGroup",            // ToolTip
            23 => "AXOutline",          // Tree
            24 => "AXRow",              // TreeItem
            25 => "AXUnknown",          // Custom
            26 => "AXGroup",            // Group
            27 => "AXValueIndicator",   // Thumb
            28 => "AXTable",            // DataGrid
            29 => "AXRow",              // DataItem
            30 => "AXTextArea",         // Document
            31 => "AXMenuButton",       // SplitButton
            32 => "AXWindow",           // Window
            33 => "AXGroup",            // Pane
            34 => "AXGroup",            // Header
            // A column header is a button to AppKit (NSTableHeaderView's cells are, with the sort
            // subrole): AXColumn is the column itself, an element with rows and an index, which a
            // WPF header item is not -- VoiceOver would go looking for cells under it.
            35 => "AXButton",           // HeaderItem
            36 => "AXTable",            // Table
            37 => "AXGroup",            // TitleBar
            38 => "AXSplitter",         // Separator
            _ => "AXUnknown",
        };

        /// <summary>
        /// The role for a node, given its patterns as well as its control type. A grid cell and a
        /// scrolling pane usually come with a control type that says nothing (Custom, Group,
        /// Pane), and to VoiceOver the role is what decides how they are navigated: a cell is
        /// read with its row and column, a scroll area offers paging. A type that already means
        /// something -- a list that scrolls, a text box in a cell -- keeps its own role.
        /// </summary>
        internal static string RoleFor(int automationControlType, AccessibleState state)
        {
            string role = RoleFor(automationControlType);
            if (role is not ("AXUnknown" or "AXGroup")) return role;

            if ((state & AccessibleState.GridItem) != 0) return "AXCell";
            if ((state & AccessibleState.Scrollable) != 0) return "AXScrollArea";
            return role;
        }

        /// <summary>Roles whose AXValue is a checked-state number rather than text.</summary>
        internal static bool IsToggleRole(int automationControlType)
            => automationControlType is 2 or 13 or 19;   // CheckBox, RadioButton, TabItem
    }
}
