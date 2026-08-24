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

using System;
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

        private static void OnTreeChanged(AutomationChange change)
        {
            try
            {
                Host?.InvalidateAccessibilityNode(
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
        /// {"cls","name","desc","x","y","w","h","en","fo","ck","chk","exp","sel","val","act"}.
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

            if (tree.TryGetRange(nodeId, out double value, out double min, out double max))
            {
                json.Append(",\"val\":").Append(value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture))
                    .Append(",\"vmin\":").Append(min.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture))
                    .Append(",\"vmax\":").Append(max.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture));
            }
            else
            {
                string text = tree.GetValue(nodeId);
                if (text.Length != 0) json.Append(",\"text\":\"").Append(Escape(text)).Append('"');
            }

            // The actions to advertise. TalkBack only offers what the node claims.
            int actions = 0;
            if (tree.SupportsAction(nodeId, AccessibleAction.Invoke) ||
                tree.SupportsAction(nodeId, AccessibleAction.Toggle) ||
                tree.SupportsAction(nodeId, AccessibleAction.Select)) actions |= 1;   // click
            if (tree.SupportsAction(nodeId, AccessibleAction.Expand)) actions |= 2;
            if (tree.SupportsAction(nodeId, AccessibleAction.Collapse)) actions |= 4;
            json.Append(",\"act\":").Append(actions);

            json.Append('}');
            return json.ToString();
        }

        /// <summary>
        /// Performs an Android accessibility action. The kinds are this file's own small vocabulary,
        /// not Android's constants, so the head does the mapping where the constants are visible.
        ///   0 click, 1 focus, 2 expand, 3 collapse, 4 set-value (uses <paramref name="argument"/>).
        /// </summary>
        public static bool PerformAction(int nodeId, int kind, string argument)
        {
            IAutomationTreeSource tree = Tree;
            if (tree == null) return false;

            switch (kind)
            {
                case 0:
                    foreach (AccessibleAction action in s_clickOrder)
                    {
                        if (tree.SupportsAction(nodeId, action)) return tree.DoAction(nodeId, action);
                    }
                    return false;
                case 1: return tree.SetFocus(nodeId);
                case 2: return tree.DoAction(nodeId, AccessibleAction.Expand);
                case 3: return tree.DoAction(nodeId, AccessibleAction.Collapse);
                case 4: return tree.SetValue(nodeId, argument ?? string.Empty);
                default: return false;
            }
        }

        private static readonly AccessibleAction[] s_clickOrder =
        {
            AccessibleAction.Toggle, AccessibleAction.Invoke, AccessibleAction.Select,
        };

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
                    sb.Append(c switch { '"' => "\\\"", '\\' => "\\\\", '\n' => "\\n", '\r' => "\\r", '\t' => "\\t", _ => "" });
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
