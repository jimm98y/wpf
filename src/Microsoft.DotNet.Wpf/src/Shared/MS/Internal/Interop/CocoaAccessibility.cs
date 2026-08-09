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
    internal static class CocoaAccessibility
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
        internal static void AddViewAccessibility(IntPtr viewClass)
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

                int root = tree.GetRootId(self);
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
            => Guarded(self, sel, static (tree, node) => NSStr(CocoaRoles.RoleFor(tree.GetRole(node))), NSStr("AXUnknown"));

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

                CocoaWindow.ConvertScreenPixelsToCocoaPoints(x, y, w, h,
                    out double sx, out double sy, out double sw, out double sh);
                return new CGRect { x = sx, y = sy, width = sw, height = sh };
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

        // ---- notifications out -----------------------------------------------------

        private static void OnTreeChanged(AutomationChange change)
        {
            try
            {
                IntPtr element = ElementFor(change.NodeId);
                if (element == IntPtr.Zero) return;

                string notification = change.Kind switch
                {
                    AutomationChangeKind.FocusChanged => "AXFocusedUIElementChanged",
                    AutomationChangeKind.ValueChanged => "AXValueChanged",
                    AutomationChangeKind.SelectionChanged => "AXSelectedChildrenChanged",
                    AutomationChangeKind.ChildrenChanged => "AXLayoutChanged",
                    _ => "AXTitleChanged",
                };

                NSAccessibilityPostNotification(element, NSStr(notification));
            }
            catch (Exception e) { Log("postNotification", e); }
        }

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

        [DllImport("/usr/lib/libSystem.dylib")] private static extern IntPtr dlopen(string path, int mode);
        [DllImport("/usr/lib/libSystem.dylib")] private static extern IntPtr dlsym(IntPtr handle, string symbol);

        [StructLayout(LayoutKind.Sequential)]
        private struct CGPoint { public double x; public double y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct CGRect { public double x; public double y; public double width; public double height; }
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
            35 => "AXColumn",           // HeaderItem
            36 => "AXTable",            // Table
            37 => "AXGroup",            // TitleBar
            38 => "AXSplitter",         // Separator
            _ => "AXUnknown",
        };

        /// <summary>Roles whose AXValue is a checked-state number rather than text.</summary>
        internal static bool IsToggleRole(int automationControlType)
            => automationControlType is 2 or 13 or 19;   // CheckBox, RadioButton, TabItem
    }
}
