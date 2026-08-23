// The portable half of accessibility for the WinForms-on-WebGPU host.
//
// The controls in this stack have no native handles -- the driver keeps them as managed Hwnd objects
// and the whole form is drawn into a single window -- so the tree that every platform's assistive
// technology builds out of native handles stopped at the host window. Everything below it was
// invisible to a screen reader, to an inspector, and to any test harness.
//
// Every platform wants the same facts (what is this element, what is it called, where is it, what
// can be done to it) behind a different interface: UI Automation on Windows, NSAccessibility on
// macOS, AT-SPI on Linux. Those facts live here, once, as queries over the live Control tree -- not
// as a shadow tree, which would have to be invalidated on every add, remove, show and hide. The
// per-platform files are then only a translation of names.
//
// Where a control already answers through Mono's own AccessibleObject, that answer wins: it is the
// control's considered description of itself, and several controls override it.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace WinFormsWebGpu.Accessibility
{
    /// <summary>What an element is, in terms every platform can name. Deliberately close to the
    /// common subset of UI Automation's control types and NSAccessibility's roles.</summary>
    internal enum A11yRole
    {
        Unknown, Window, Pane, Group, Button, CheckBox, RadioButton, Text, Link, ComboBox, Edit,
        Document, List, ListItem, Table, Calendar, Spinner, ProgressBar, Slider, ScrollBar, Tree,
        TreeItem, Tab, TabItem, MenuBar, MenuItem, StatusBar, ToolBar, DataGrid, Image, Separator,
        Header, DataItem,
    }

    /// <summary>Three states, because a check box has three.</summary>
    internal enum A11yToggle { Off, On, Mixed }

    /// <summary>The facts about one control, asked of the live tree. Static rather than an object
    /// graph so there is nothing to keep in sync.</summary>
    internal static class A11y
    {
        // ---- the tree ---------------------------------------------------------------------------

        /// <summary>A control's visible children, in the order the collection holds them, which
        /// is the order they were added and the order they read on screen. Reversing it to get
        /// z-order instead handed every client the form bottom-up.</summary>
        internal static IList<Control> Children(Control parent)
        {
            var list = new List<Control>();
            if (parent == null)
                return list;
            try
            {
                Control.ControlCollection children = parent.Controls;
                for (int i = 0; i < children.Count; i++)
                {
                    Control c = children[i];
                    if (c != null && c.Visible && !c.IsDisposed)
                        list.Add(c);
                }
            }
            catch (Exception)
            {
                // A control disposed mid-walk must not take the caller down with it.
            }
            return list;
        }

        /// <summary>A control's bounds in the driver's screen space. That is not the real screen:
        /// the stack renders to a virtual 96-DPI screen which the host magnifies, so a host has to
        /// map this before handing it to the platform.</summary>
        internal static Rectangle DriverBounds(Control c)
        {
            try
            {
                if (c == null || c.IsDisposed)
                    return Rectangle.Empty;
                if (c.Parent == null)
                    return c.Bounds;
                return new Rectangle(c.Parent.PointToScreen(c.Location), c.Size);
            }
            catch (Exception)
            {
                return Rectangle.Empty;
            }
        }

        /// <summary>The deepest visible control containing a point given in driver space.</summary>
        internal static Control HitTest(Control root, Point driverPoint)
        {
            Control hit = root;
            for (bool descended = true; descended;)
            {
                descended = false;
                foreach (Control c in Children(hit))
                {
                    if (DriverBounds(c).Contains(driverPoint))
                    {
                        hit = c;
                        descended = true;
                        break;
                    }
                }
            }
            return hit;
        }

        internal static Control FindFocused(Control parent)
        {
            try
            {
                if (parent == null || parent.IsDisposed)
                    return null;
                if (parent.Focused)
                    return parent;
                Control.ControlCollection children = parent.Controls;
                for (int i = 0; i < children.Count; i++)
                {
                    Control found = FindFocused(children[i]);
                    if (found != null)
                        return found;
                }
            }
            catch (Exception)
            {
            }
            return null;
        }

        // ---- what it is -------------------------------------------------------------------------

        internal static A11yRole RoleOf(Control c)
        {
            if (c == null)
                return A11yRole.Unknown;
            // By type name rather than by type, so this file need not reference every control in the
            // library, and so a subclass lands on its base's role.
            for (Type t = c.GetType(); t != null; t = t.BaseType)
            {
                switch (t.Name)
                {
                    case "Form": return A11yRole.Window;
                    case "Button": return A11yRole.Button;
                    case "CheckBox": return A11yRole.CheckBox;
                    case "RadioButton": return A11yRole.RadioButton;
                    case "LinkLabel":
                    case "Label": return A11yRole.Text;
                    case "DateTimePicker":
                    case "ComboBox": return A11yRole.ComboBox;
                    case "MaskedTextBox":
                    case "TextBox": return A11yRole.Edit;
                    case "RichTextBox": return A11yRole.Document;
                    case "CheckedListBox":
                    case "ListBox": return A11yRole.List;
                    case "ListView": return A11yRole.Table;
                    case "MonthCalendar": return A11yRole.Calendar;
                    case "NumericUpDown":
                    case "DomainUpDown":
                    case "UpDownBase": return A11yRole.Spinner;
                    case "ProgressBar": return A11yRole.ProgressBar;
                    case "TrackBar": return A11yRole.Slider;
                    case "HScrollBar":
                    case "VScrollBar":
                    case "ScrollBar": return A11yRole.ScrollBar;
                    case "TreeView": return A11yRole.Tree;
                    case "GroupBox": return A11yRole.Group;
                    case "TabControl": return A11yRole.Tab;
                    case "TabPage": return A11yRole.Pane;
                    case "DataGridView": return A11yRole.DataGrid;
                    case "MenuStrip": return A11yRole.MenuBar;
                    case "StatusStrip": return A11yRole.StatusBar;
                    case "ToolStrip": return A11yRole.ToolBar;
                    case "PictureBox": return A11yRole.Pane;
                }
            }
            return A11yRole.Pane;
        }

        /// <summary>What the element is called. A control that draws its own text names itself with
        /// it; one that holds the user's text does not, because that is its value.</summary>
        internal static string NameOf(Control c)
        {
            if (c == null)
                return string.Empty;
            try
            {
                AccessibleObject acc = c.AccessibilityObject;
                if (acc != null && !string.IsNullOrEmpty(acc.Name))
                    return acc.Name;
            }
            catch (Exception)
            {
            }
            try
            {
                if (c is TextBoxBase)
                    return c.Name ?? string.Empty;
                return c.Text ?? string.Empty;
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        internal static string DescriptionOf(Control c)
        {
            try
            {
                AccessibleObject acc = c == null ? null : c.AccessibilityObject;
                return acc != null ? acc.Description : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        internal static string AutomationIdOf(Control c)
        {
            try { return c == null ? string.Empty : (c.Name ?? string.Empty); }
            catch (Exception) { return string.Empty; }
        }

        internal static bool IsEnabled(Control c)
        {
            try { return c != null && c.Enabled; } catch (Exception) { return false; }
        }

        internal static bool IsFocused(Control c)
        {
            try { return c != null && c.Focused; } catch (Exception) { return false; }
        }

        internal static bool IsFocusable(Control c)
        {
            try { return c != null && c.CanFocus; } catch (Exception) { return false; }
        }

        internal static bool IsVisible(Control c)
        {
            try { return c != null && !c.IsDisposed && c.Visible; } catch (Exception) { return false; }
        }

        internal static void Focus(Control c)
        {
            try { if (c != null) c.Focus(); } catch (Exception) { }
        }

        // ---- what can be done to it ---------------------------------------------------------------

        /// <summary>Whether pressing it is meaningful. A check box and a radio button are pressed
        /// by toggling and selecting, not by invoking, so they are not invokable.</summary>
        internal static bool CanInvoke(Control c)
        {
            return c is Button;
        }

        internal static void Invoke(Control c)
        {
            var button = c as Button;
            if (button != null)
                button.PerformClick();
        }

        internal static bool CanToggle(Control c)
        {
            return c is CheckBox;
        }

        internal static A11yToggle ToggleStateOf(Control c)
        {
            var check = c as CheckBox;
            if (check == null)
                return A11yToggle.Mixed;
            switch (check.CheckState)
            {
                case CheckState.Checked: return A11yToggle.On;
                case CheckState.Unchecked: return A11yToggle.Off;
                default: return A11yToggle.Mixed;
            }
        }

        internal static void Toggle(Control c)
        {
            var check = c as CheckBox;
            if (check != null)
                check.Checked = !check.Checked;
        }

        /// <summary>A drop-down that can be opened and shut. An assistive technology has no
        /// other way to reach a combo box's list.</summary>
        internal static bool CanExpand(Control c)
        {
            return c is ComboBox;
        }

        internal static bool IsExpanded(Control c)
        {
            var combo = c as ComboBox;
            return combo != null && combo.DroppedDown;
        }

        internal static void SetExpanded(Control c, bool expanded)
        {
            var combo = c as ComboBox;
            if (combo != null)
                combo.DroppedDown = expanded;
        }

        internal static bool HasValue(Control c)
        {
            return c is TextBoxBase;
        }

        internal static bool IsReadOnly(Control c)
        {
            var edit = c as TextBoxBase;
            return edit == null || edit.ReadOnly;
        }

        internal static string ValueOf(Control c)
        {
            try
            {
                var edit = c as TextBoxBase;
                return edit != null ? edit.Text : (c == null ? string.Empty : c.Text);
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        internal static void SetValue(Control c, string value)
        {
            var edit = c as TextBoxBase;
            if (edit != null && !edit.ReadOnly)
                edit.Text = value;
        }
    }

    /// <summary>What a platform host has to supply: which window owns the tree, which form is in it,
    /// and how a driver-space rectangle lands on the real screen.</summary>
    internal interface IA11yHostSite
    {
        IntPtr Handle { get; }
        Form Form { get; }
        /// <summary>Maps a rectangle in the driver's virtual screen space to real screen pixels.
        /// Returns false when the window is not on screen.</summary>
        bool TryMapToScreen(Rectangle driverRect, out double x, out double y, out double width, out double height);
        /// <summary>The inverse, for hit testing.</summary>
        bool TryMapFromScreen(double x, double y, out Point driverPoint);
    }
}
