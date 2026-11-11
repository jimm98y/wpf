// WinForms' accessibility tree for the platforms WPF's backends speak to -- NSAccessibility (CocoaAccessibility),
// AT-SPI (AtSpiBridge), the browser's DOM mirror, UIKit's and Android's -- through the same seam WPF's tree goes
// through (WindowsBase's IAutomationTreeSource, AutomationTree.AddSource): the facts are A11y's and A11yItems'
// (the portable half, which UiaBridge translates for UI Automation on Windows), asked of the live control tree.
//
// A host registers its window (IA11yHostSite: the native handle the platform backend knows it by, the form, and
// the mapping of the driver's space to the screen); the form is that window's root. Every element -- a control, or
// an item a control does not keep an object for (A11yItems' keys) -- is given an id from IdBase up, the same id
// for as long as the element lives, as a backend keeps elements by id.

extern alias WindowsBase;

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.CompilerServices;
using System.Windows.Forms;
using AccessibleAction = WindowsBase::MS.Internal.Interop.AccessibleAction;
using AccessibleState = WindowsBase::MS.Internal.Interop.AccessibleState;
using AutomationTree = WindowsBase::MS.Internal.Interop.AutomationTree;
using IAutomationTreeSource = WindowsBase::MS.Internal.Interop.IAutomationTreeSource;

namespace WinFormsWebGpu.Accessibility
{
    internal sealed class AutomationSource : IAutomationTreeSource
    {
        /// <summary>The first id WinForms' elements have: above any WPF's tree hands out in a process.</summary>
        internal const int IdBase = 1 << 30;

        private static readonly AutomationSource s_instance = new();
        private static readonly object s_gate = new();
        private static readonly Dictionary<IntPtr, IA11yHostSite> s_sites = new();
        // element -> id (weakly: a control gone takes its id with it), id -> element
        private static readonly ConditionalWeakTable<object, Box> s_ids = new();
        private static readonly Dictionary<int, WeakReference<object>> s_elements = new();
        private static int s_next = IdBase;

        private sealed class Box { public int Id; }

        /// <summary>A host's window in the tree: its form the root of what the platform's backend asks of the
        /// handle. Called as the window is made; <see cref="Unregister"/> when it goes.</summary>
        internal static void Register(IA11yHostSite site)
        {
            if (site == null || site.Handle == IntPtr.Zero) return;
            lock (s_gate) s_sites[site.Handle] = site;
            AutomationTree.AddSource(s_instance, IdBase);
        }

        internal static void Unregister(IntPtr handle)
        {
            lock (s_gate) s_sites.Remove(handle);
        }

        // ---- ids ----------------------------------------------------------------------------------------------

        private static int IdOf(object element)
        {
            if (element == null) return IAutomationTreeSource.InvalidNode;
            lock (s_gate)
            {
                Box box = s_ids.GetValue(element, _ => new Box { Id = s_next++ });
                if (!s_elements.ContainsKey(box.Id)) s_elements[box.Id] = new WeakReference<object>(element);
                return box.Id;
            }
        }

        private static object ElementOf(int id)
        {
            lock (s_gate)
            {
                if (!s_elements.TryGetValue(id, out var weak)) return null;
                if (!weak.TryGetTarget(out object element) || (element is Control c && c.IsDisposed))
                {
                    s_elements.Remove(id);
                    return null;
                }
                return element;
            }
        }

        private static IA11yHostSite SiteOf(object element)
        {
            Control owner = A11yItems.OwnerOf(element) ?? element as Control;
            Form form = owner?.FindForm() ?? owner as Form;
            lock (s_gate)
                foreach (var site in s_sites.Values)
                    if (site.Form != null && (ReferenceEquals(site.Form, form) || ReferenceEquals(site.Form, owner))) return site;
            return null;
        }

        private static IA11yHostSite SiteOfWindow(IntPtr handle)
        {
            lock (s_gate) return s_sites.TryGetValue(handle, out var site) && site.Form != null && !site.Form.IsDisposed ? site : null;
        }

        // ---- the tree -----------------------------------------------------------------------------------------

        public int GetRootId(IntPtr windowHandle) => SiteOfWindow(windowHandle) is { } site ? IdOf(site.Form) : IAutomationTreeSource.InvalidNode;

        public int[] GetChildIds(int id)
        {
            object element = ElementOf(id);
            if (element == null) return Array.Empty<int>();
            try
            {
                var ids = new List<int>();
                if (element is Control c)
                    foreach (Control child in A11y.Children(c)) ids.Add(IdOf(child));
                foreach (object item in A11yItems.ChildrenOf(element))
                    if (item is not Control) ids.Add(IdOf(item));
                return ids.ToArray();
            }
            catch (Exception) { return Array.Empty<int>(); }
        }

        public int GetParentId(int id)
        {
            object element = ElementOf(id);
            if (element == null) return IAutomationTreeSource.InvalidNode;
            try
            {
                if (element is Form) return IAutomationTreeSource.InvalidNode;
                object parent = element is Control c ? A11y.ParentOf(c) : A11yItems.ParentOf(element);
                return parent == null ? IAutomationTreeSource.InvalidNode : IdOf(parent);
            }
            catch (Exception) { return IAutomationTreeSource.InvalidNode; }
        }

        // ---- facts --------------------------------------------------------------------------------------------

        public int GetRole(int id)
        {
            object element = ElementOf(id);
            if (element == null) return -1;
            try { return ControlType(element is Control c ? A11y.RoleOf(c) : A11yItems.RoleOf(element)); }
            catch (Exception) { return ControlType(A11yRole.Custom); }
        }

        // A11yRole as WPF's AutomationControlType numbers it, which every backend maps to its platform's roles
        private static int ControlType(A11yRole role) => role switch
        {
            A11yRole.Button => 0, A11yRole.Calendar => 1, A11yRole.CheckBox => 2, A11yRole.ComboBox => 3, A11yRole.Edit => 4,
            A11yRole.Link => 5, A11yRole.Image => 6, A11yRole.ListItem => 7, A11yRole.List => 8, A11yRole.Menu => 9,
            A11yRole.MenuBar => 10, A11yRole.MenuItem => 11, A11yRole.ProgressBar => 12, A11yRole.RadioButton => 13,
            A11yRole.ScrollBar => 14, A11yRole.Slider => 15, A11yRole.Spinner => 16, A11yRole.StatusBar => 17, A11yRole.Tab => 18,
            A11yRole.TabItem => 19, A11yRole.Text => 20, A11yRole.ToolBar => 21, A11yRole.Tree => 23, A11yRole.TreeItem => 24,
            A11yRole.Group => 26, A11yRole.Thumb => 27, A11yRole.DataGrid => 28, A11yRole.DataItem => 29, A11yRole.Document => 30,
            A11yRole.Window => 32, A11yRole.Pane => 33, A11yRole.Header => 34, A11yRole.Table => 36, A11yRole.Separator => 38,
            _ => 25,   // Custom (Unknown too)
        };

        public string GetName(int id) => Ask(id, e => e is Control c ? A11y.NameOf(c) : A11yItems.NameOf(e));

        public string GetAutomationId(int id) => Ask(id, e => e is Control c ? A11y.AutomationIdOf(c) : null);

        public string GetHelpText(int id) => Ask(id, e => e is Control c ? A11y.DescriptionOf(c) : null);

        public string GetValue(int id) => Ask(id, e => e is Control c ? (A11y.HasValue(c) ? A11y.ValueOf(c) : null) : (A11yItems.HasValue(e) ? A11yItems.ValueOf(e) : null));

        private static string Ask(int id, Func<object, string> question)
        {
            object element = ElementOf(id);
            if (element == null) return null;
            try { return question(element); }
            catch (Exception) { return null; }
        }

        public bool TryGetRange(int id, out double value, out double minimum, out double maximum)
        {
            value = minimum = maximum = 0;
            switch (ElementOf(id))
            {
                case ProgressBar p: value = p.Value; minimum = p.Minimum; maximum = p.Maximum; return true;
                case TrackBar t: value = t.Value; minimum = t.Minimum; maximum = t.Maximum; return true;
                case NumericUpDown n: value = (double)n.Value; minimum = (double)n.Minimum; maximum = (double)n.Maximum; return true;
                case ScrollBar s: value = s.Value; minimum = s.Minimum; maximum = s.Maximum; return true;
                default: return false;
            }
        }

        public AccessibleState GetState(int id)
        {
            object element = ElementOf(id);
            if (element == null) return AccessibleState.None;
            var state = AccessibleState.None;
            try
            {
                if (element is Control c)
                {
                    if (A11y.IsEnabled(c)) state |= AccessibleState.Enabled;
                    if (A11y.IsFocusable(c)) state |= AccessibleState.Focusable;
                    if (A11y.IsFocused(c)) state |= AccessibleState.Focused;
                    if (!A11y.IsVisible(c)) state |= AccessibleState.Offscreen;
                    if (c is TextBox tb && (tb.UseSystemPasswordChar || tb.PasswordChar != '\0')) state |= AccessibleState.Password;
                    if (A11y.HasValue(c))
                    {
                        state |= AccessibleState.HasValue;
                        if (A11y.IsReadOnly(c)) state |= AccessibleState.ReadOnly;
                    }
                    if (A11y.CanToggle(c))
                        state |= A11y.ToggleStateOf(c) switch { A11yToggle.On => AccessibleState.Checked, A11yToggle.Mixed => AccessibleState.Indeterminate, _ => AccessibleState.None };
                    if (A11y.CanExpand(c)) state |= A11y.IsExpanded(c) ? AccessibleState.Expanded : AccessibleState.Collapsed;
                    if (A11y.CanSelect(c))
                    {
                        state |= AccessibleState.Selectable;
                        if (A11y.IsSelected(c)) state |= AccessibleState.Selected;
                    }
                }
                else
                {
                    if (A11yItems.IsEnabled(element)) state |= AccessibleState.Enabled;
                    if (A11yItems.HasValue(element))
                    {
                        state |= AccessibleState.HasValue;
                        if (A11yItems.IsReadOnly(element)) state |= AccessibleState.ReadOnly;
                    }
                    if (A11yItems.CanExpand(element)) state |= A11yItems.IsExpanded(element) ? AccessibleState.Expanded : AccessibleState.Collapsed;
                    if (A11yItems.CanSelect(element))
                    {
                        state |= AccessibleState.Selectable;
                        if (A11yItems.IsSelected(element)) state |= AccessibleState.Selected;
                    }
                    if (A11yItems.BoundsOf(element).IsEmpty) state |= AccessibleState.Offscreen;
                }
                if (TryGetRange(id, out _, out _, out _)) state |= AccessibleState.HasRange;
            }
            catch (Exception) { }
            return state;
        }

        // the form is its whole window (frame and caption: what a top-level element is given); a control its driver
        // bounds, an item its own (UiaBridge's rules), mapped to the screen by the host
        public bool TryGetBounds(int id, out double x, out double y, out double width, out double height)
        {
            x = y = width = height = 0;
            object element = ElementOf(id);
            IA11yHostSite site = element == null ? null : SiteOf(element);
            if (site == null) return false;
            try
            {
                if (ReferenceEquals(element, site.Form)) return site.TryWindowRect(out x, out y, out width, out height);
                Rectangle bounds = element is Control c ? (A11y.IsVisible(c) ? A11y.DriverBounds(c) : Rectangle.Empty) : A11yItems.BoundsOf(element);
                return !bounds.IsEmpty && site.TryMapToScreen(bounds, out x, out y, out width, out height);
            }
            catch (Exception) { return false; }
        }

        public int HitTest(IntPtr windowHandle, double screenX, double screenY)
        {
            IA11yHostSite site = SiteOfWindow(windowHandle);
            if (site == null || !site.TryMapFromScreen(screenX, screenY, out Point driverPoint)) return IAutomationTreeSource.InvalidNode;
            try
            {
                Control hit = A11y.HitTest(site.Form, driverPoint) ?? site.Form;
                // the item under the point, where the control has items
                foreach (object item in A11yItems.ChildrenOf(hit))
                    if (item is not Control && A11yItems.BoundsOf(item).Contains(driverPoint)) return IdOf(item);
                return IdOf(hit);
            }
            catch (Exception) { return IAutomationTreeSource.InvalidNode; }
        }

        public int GetFocusedId(IntPtr windowHandle)
        {
            IA11yHostSite site = SiteOfWindow(windowHandle);
            if (site == null) return IAutomationTreeSource.InvalidNode;
            try { return IdOf(A11y.FindFocused(site.Form) ?? site.Form); }
            catch (Exception) { return IAutomationTreeSource.InvalidNode; }
        }

        // ---- what can be done ---------------------------------------------------------------------------------

        public bool SupportsAction(int id, AccessibleAction action)
        {
            object element = ElementOf(id);
            if (element == null) return false;
            try
            {
                return (action, element) switch
                {
                    (AccessibleAction.Invoke, Control c) => A11y.CanInvoke(c),
                    (AccessibleAction.Invoke, _) => A11yItems.CanInvoke(element),
                    (AccessibleAction.Toggle, Control c) => A11y.CanToggle(c),
                    (AccessibleAction.Expand or AccessibleAction.Collapse, Control c) => A11y.CanExpand(c),
                    (AccessibleAction.Expand or AccessibleAction.Collapse, _) => A11yItems.CanExpand(element),
                    (AccessibleAction.Select or AccessibleAction.ToggleSelection, Control c) => A11y.CanSelect(c),
                    (AccessibleAction.Select or AccessibleAction.ToggleSelection, _) => A11yItems.CanSelect(element),
                    _ => false,
                };
            }
            catch (Exception) { return false; }
        }

        public bool DoAction(int id, AccessibleAction action)
        {
            object element = ElementOf(id);
            if (element == null || !SupportsAction(id, action)) return false;
            return OnUiThread(element, () =>
            {
                switch (action, element)
                {
                    case (AccessibleAction.Invoke, Control c): A11y.Invoke(c); break;
                    case (AccessibleAction.Invoke, _): A11yItems.Invoke(element); break;
                    case (AccessibleAction.Toggle, Control c): A11y.Toggle(c); break;
                    case (AccessibleAction.Expand, Control c): A11y.SetExpanded(c, true); break;
                    case (AccessibleAction.Collapse, Control c): A11y.SetExpanded(c, false); break;
                    case (AccessibleAction.Expand, _): A11yItems.SetExpanded(element, true); break;
                    case (AccessibleAction.Collapse, _): A11yItems.SetExpanded(element, false); break;
                    case (AccessibleAction.Select, Control c): A11y.Select(c); break;
                    case (AccessibleAction.Select, _): A11yItems.SetSelected(element, true); break;
                    case (AccessibleAction.ToggleSelection, Control c): A11y.Select(c); break;
                    case (AccessibleAction.ToggleSelection, _): A11yItems.SetSelected(element, !A11yItems.IsSelected(element)); break;
                }
            });
        }

        public bool SetValue(int id, string value)
        {
            object element = ElementOf(id);
            if (element == null) return false;
            return OnUiThread(element, () =>
            {
                if (element is Control c) A11y.SetValue(c, value);
                else A11yItems.SetValue(element, value);
            });
        }

        public bool SetFocus(int id)
        {
            object element = ElementOf(id);
            Control c = element as Control ?? A11yItems.OwnerOf(element);
            return c != null && OnUiThread(c, () => A11y.Focus(c));
        }

        // a backend asks on whatever thread its platform calls in on; a control is acted on on its own
        private static bool OnUiThread(object element, Action action)
        {
            try
            {
                Control c = element as Control ?? A11yItems.OwnerOf(element);
                if (c != null && c.IsHandleCreated && c.InvokeRequired) c.Invoke(action);
                else action();
                return true;
            }
            catch (Exception) { return false; }
        }
    }
}
