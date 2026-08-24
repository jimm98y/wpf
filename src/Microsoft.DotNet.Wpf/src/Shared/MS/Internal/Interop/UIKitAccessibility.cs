// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The iOS accessibility backend: WPF's automation tree, exposed through UIAccessibility.
//
// Three things make this different from the macOS backend next door, and all three are UIKit's
// doing rather than ours:
//
//   * UIKit has no accessibility TREE. A UIAccessibilityContainer exposes a FLAT, indexed list, and
//     VoiceOver swipes along it. So the peer tree is flattened here, and only nodes that are worth
//     stopping on are included -- a StackPanel that exists purely to lay out three buttons is not
//     something a user wants to swipe past.
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

        // The flattened view of the tree, and the container it was built for. VoiceOver asks for the
        // count and then walks indices, so the list must stay stable across that walk; it is rebuilt
        // when the tree says the structure changed, not per query.
        private static readonly List<int> s_flattened = new();
        private static IntPtr s_flattenedFor;
        private static bool s_flattenedStale = true;

        // One element object per node, so VoiceOver's pointer-identity focus tracking works.
        private static readonly Dictionary<int, IntPtr> s_elements = new();

        private static IAutomationTreeSource Tree => AutomationTree.Current;

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

            IntPtr existing = objc_getClass("WpfAccessibilityElement");
            if (existing != IntPtr.Zero)
            {
                s_elementClass = existing;
                s_nodeIdOffset = ivar_getOffset(class_getInstanceVariable(existing, "_nodeId"));
                return;
            }

            // UIAccessibilityElement already implements the container back-pointer and the plumbing
            // VoiceOver needs; subclassing it means only the answers have to be supplied.
            IntPtr baseClass = objc_getClass("UIAccessibilityElement");
            if (baseClass == IntPtr.Zero) return;   // not a UIKit process

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

            objc_registerClassPair(cls);

            s_elementClass = cls;
            s_nodeIdOffset = ivar_getOffset(class_getInstanceVariable(cls, "_nodeId"));
        }

        private static IntPtr ElementFor(int nodeId, IntPtr container)
        {
            if (nodeId < 0) return IntPtr.Zero;

            EnsureElementClass();
            if (s_elementClass == IntPtr.Zero) return IntPtr.Zero;
            if (s_elements.TryGetValue(nodeId, out IntPtr existing)) return existing;

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

        // ---- flattening ------------------------------------------------------------

        /// <summary>
        /// Rebuilds the flat list UIKit navigates. Only nodes worth stopping on are included: one
        /// that has no name, no value and nothing to do is layout scaffolding, and putting it in the
        /// list means the user swipes through silence.
        /// </summary>
        private static void EnsureFlattened(IntPtr container)
        {
            if (!s_flattenedStale && s_flattenedFor == container) return;

            s_flattened.Clear();
            s_flattenedFor = container;
            s_flattenedStale = false;

            IAutomationTreeSource tree = Tree;
            if (tree == null) return;

            int root = tree.GetRootId(container);
            if (root < 0) return;

            Flatten(tree, root, 0);
        }

        private static void Flatten(IAutomationTreeSource tree, int node, int depth)
        {
            if (depth > 64) return;   // a malformed custom peer must not hang VoiceOver

            if (IsWorthStopping(tree, node))
            {
                s_flattened.Add(node);

                // An element that is itself accessible HIDES its children -- UIKit's own rule for
                // views, and the right one here. A WPF Button contains a TextBlock carrying the same
                // string, so descending into it would list every control twice: once as a button and
                // again as static text. Containers are the exception: a ListBox is worth stopping on
                // AND its items have to be reachable.
                if (!IsContainerRole(tree.GetRole(node))) return;
            }

            foreach (int child in tree.GetChildIds(node)) Flatten(tree, child, depth + 1);
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

        private static bool IsWorthStopping(IAutomationTreeSource tree, int node)
        {
            AccessibleState state = tree.GetState(node);
            if ((state & AccessibleState.Offscreen) != 0) return false;

            if (!string.IsNullOrEmpty(tree.GetName(node))) return true;
            if ((state & (AccessibleState.HasValue | AccessibleState.HasRange)) != 0) return true;

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
                EnsureFlattened(self);
                return s_flattened.Count;
            }
            catch (Exception e) { Log("accessibilityElementCount", e); return 0; }
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static IntPtr ElementAtIndexImp(IntPtr self, IntPtr sel, nint index)
        {
            NoteAccessibilityClient();
            try
            {
                EnsureFlattened(self);
                if (index < 0 || index >= s_flattened.Count) return IntPtr.Zero;
                return ElementFor(s_flattened[(int)index], self);
            }
            catch (Exception e) { Log("accessibilityElementAtIndex", e); return IntPtr.Zero; }
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static nint IndexOfElementImp(IntPtr self, IntPtr sel, IntPtr element)
        {
            try
            {
                EnsureFlattened(self);
                int node = NodeOf(element);
                int index = s_flattened.IndexOf(node);
                // NSNotFound, which is what UIKit expects for an element that is no longer listed.
                return index < 0 ? nint.MaxValue : index;
            }
            catch (Exception e) { Log("indexOfAccessibilityElement", e); return nint.MaxValue; }
        }

        // ---- an element's answers --------------------------------------------------

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static byte IsElementImp(IntPtr self, IntPtr sel) => 1;

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

                string text = tree.GetValue(node);
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

        // ---- notifications out -----------------------------------------------------

        private static void OnTreeChanged(AutomationChange change)
        {
            try
            {
                bool structural = change.Kind == AutomationChangeKind.ChildrenChanged
                               || change.Kind == AutomationChangeKind.ChildAdded
                               || change.Kind == AutomationChangeKind.ChildRemoved;

                if (structural)
                {
                    // The flat list is now wrong; rebuild it before telling VoiceOver to re-read.
                    s_flattenedStale = true;
                }

                // UIAccessibilityLayoutChangedNotification = 1001, ScreenChanged = 1000.
                uint notification = structural ? 1000u : 1001u;

                IntPtr element = change.Kind == AutomationChangeKind.FocusChanged && s_flattenedFor != IntPtr.Zero
                    ? ElementFor(change.NodeId, s_flattenedFor)
                    : IntPtr.Zero;

                UIAccessibilityPostNotification(notification, element);
            }
            catch (Exception e) { Log("postNotification", e); }
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

        [DllImport(ObjC)] private static extern IntPtr objc_getClass(string name);
        [DllImport(ObjC)] private static extern IntPtr sel_registerName(string name);
        [DllImport(ObjC)] private static extern IntPtr objc_allocateClassPair(IntPtr super, string name, UIntPtr extra);
        [DllImport(ObjC)] private static extern void objc_registerClassPair(IntPtr cls);
        [DllImport(ObjC)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool class_addMethod(IntPtr cls, IntPtr sel, IntPtr imp, string types);
        [DllImport(ObjC)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool class_addIvar(IntPtr cls, string name, UIntPtr size, byte align, string types);
        [DllImport(ObjC)] private static extern IntPtr class_getInstanceVariable(IntPtr cls, string name);
        [DllImport(ObjC)] private static extern nint ivar_getOffset(IntPtr ivar);

        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr r, IntPtr s);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendPtrPtr(IntPtr r, IntPtr s, IntPtr a);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern double SendDouble(IntPtr r, IntPtr s);

        // dlsym for the same reason as the macOS backend: a framework named in a DllImport is
        // linked eagerly, and UIKit does not exist on the desktop heads this file also compiles
        // into. A raw function pointer rather than a delegate because iOS is AOT-only.
        private static IntPtr s_postNotification = (IntPtr)(-1);

        private static void UIAccessibilityPostNotification(uint notification, IntPtr argument)
        {
            if (s_postNotification == (IntPtr)(-1))
            {
                const int RTLD_NOW = 2;
                IntPtr uiKit = dlopen("/System/Library/Frameworks/UIKit.framework/UIKit", RTLD_NOW);
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
    }

    /// <summary>
    /// AutomationControlType and state to UIAccessibilityTraits.
    ///
    /// UIKit describes what an element DOES rather than what it is, so this is a bitmask union
    /// rather than a lookup: a checkbox is a button that can be selected, a slider is adjustable,
    /// and a heading is a header regardless of the control behind it.
    /// </summary>
    internal static class UIKitTraits
    {
        private const ulong None = 0;
        private const ulong Button = 1UL << 0;
        private const ulong Link = 1UL << 1;
        private const ulong Image = 1UL << 3;
        private const ulong Selected = 1UL << 4;
        private const ulong StaticText = 1UL << 7;
        private const ulong NotEnabled = 1UL << 9;
        private const ulong UpdatesFrequently = 1UL << 10;
        private const ulong Adjustable = 1UL << 12;
        private const ulong Header = 1UL << 15;
        private const ulong TabBar = 1UL << 21;

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
