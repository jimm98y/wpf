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
using System.Windows.Forms.PropertyGridInternal;

namespace WinFormsWebGpu.Accessibility
{
    /// <summary>What an element is, in terms every platform can name. Deliberately close to the
    /// common subset of UI Automation's control types and NSAccessibility's roles.</summary>
    internal enum A11yRole
    {
        Unknown, Window, Pane, Group, Button, CheckBox, RadioButton, Text, Link, ComboBox, Edit,
        Document, List, ListItem, Table, Calendar, Spinner, ProgressBar, Slider, ScrollBar, Tree,
        TreeItem, Tab, TabItem, MenuBar, MenuItem, StatusBar, ToolBar, DataGrid, Image, Separator,
        Header, DataItem, Thumb, Custom,
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

                // A split container holds its panels in the order they were constructed, which is
                // not the order they read in: Panel2 came first, so the tree named the left-hand
                // panel after the right-hand one's contents.
                var split = parent as SplitContainer;
                if (split != null)
                    list.Sort((a, b) => PanelOrder(split, a).CompareTo(PanelOrder(split, b)));

                // A property grid holds its parts in the order they were constructed, which is
                // bottom-up; they read top-down. The splitter between the grid and the help strip
                // is not published at all -- Windows does not, and there is nothing to say about
                // it that its neighbours do not already say.
                if (parent is PropertyGrid)
                {
                    list.RemoveAll(c => c is Splitter);
                    // The list of properties is wrapped in a control that exists only to draw a
                    // frame round it. Windows publishes the list, framed; publishing the frame as
                    // well put a pane between the grid and its rows that says nothing.
                    for (int i = 0; i < list.Count; i++)
                        if (list[i] is PropertyGrid.BorderHelperControl && list[i].Controls.Count == 1)
                            list[i] = list[i].Controls[0];
                    list.Sort((a, b) => a.Top.CompareTo(b.Top));
                }
            }
            catch (Exception)
            {
                // A control disposed mid-walk must not take the caller down with it.
            }
            return list;
        }

        private static int PanelOrder(SplitContainer split, Control panel)
        {
            return ReferenceEquals(panel, split.Panel1) ? 0
                 : ReferenceEquals(panel, split.Panel2) ? 1 : 2;
        }

        /// <summary>The control this one hangs under in the tree that is published, which is not
        /// always the one it is parented to: a frame drawn around a control is not itself an
        /// element, so what it holds hangs under whatever holds the frame. This has to agree with
        /// <see cref="Children"/>, because a client walks from one sibling to the next by asking
        /// its parent for the list and finding itself in it -- disagree, and everything after the
        /// first child of that parent is unreachable.</summary>
        internal static Control ParentOf(Control c)
        {
            Control parent = c == null ? null : c.Parent;
            return parent is PropertyGrid.BorderHelperControl ? parent.Parent : parent;
        }

        /// <summary>A control's bounds in the driver's screen space. That is not the real screen:
        /// the stack renders to a virtual 96-DPI screen which the host magnifies, so a host has to
        /// map this before handing it to the platform.</summary>
        internal static Rectangle DriverBounds(Control c)
        {
            try
            {
                // A property grid's list of rows is published framed -- the frame is drawn by a
                // control wrapped round it that the tree does not show -- so the list is as big as
                // the frame, which is what Windows reports for it.
                if (c != null && c.Parent is PropertyGrid.BorderHelperControl)
                    c = c.Parent;
                if (c == null || c.IsDisposed)
                    return Rectangle.Empty;
                if (c.Parent == null)
                    return c.Bounds;
                // A CONTROL reports where it is even when a scrolling parent has carried it off the top or
                // the bottom -- Windows does, and a client uses that to scroll it back into view. Only the
                // ITEMS inside a control disappear when they scroll out of it (see the bridge).
                return new Rectangle(c.Parent.PointToScreen(c.Location), c.Size);
            }
            catch (Exception)
            {
                return Rectangle.Empty;
            }
        }

        /// <summary>What is left of a rectangle once every container between it and the top has had
        /// its say -- nothing, when the thing is scrolled out of sight.
        /// <para>A client is told where something is so it can point at it, read it out, or scroll to
        /// it. Something clipped away by a scrolling panel is nowhere on screen at all, and Windows
        /// answers with an empty rectangle rather than with where it would be if it could be
        /// seen.</para></summary>
        internal static Rectangle ClipToAncestors(Control parent, Rectangle driverRect)
        {
            for (Control a = parent; a != null; a = a.Parent)
            {
                if (driverRect.IsEmpty)
                    return Rectangle.Empty;
                // The top-level window clips nothing: it IS the surface everything is drawn on.
                if (a.Parent == null)
                    break;
                Rectangle client = new Rectangle(a.PointToScreen(Point.Empty), a.ClientSize);
                driverRect = Rectangle.Intersect(driverRect, client);
            }
            return driverRect.Width <= 0 || driverRect.Height <= 0 ? Rectangle.Empty : driverRect;
        }

        /// <summary>Where a control's client area begins, in driver space.
        /// <para>This driver models no non-client area -- a window and its client are the same
        /// rectangle -- so a control's border is painted inside its own bounds and the client really
        /// starts a border in from the corner. Everything positioned against the client, which is
        /// every item a control holds, is otherwise a border out.</para></summary>
        internal static Point ClientOrigin(Control c)
        {
            Point p = c.PointToScreen(Point.Empty);
            int inset = BorderInset(c);
            return inset == 0 ? p : new Point(p.X + inset, p.Y + inset);
        }

        internal static int BorderInset(Control c)
        {
            try
            {
                switch (c.InternalBorderStyle)
                {
                    case BorderStyle.FixedSingle: return 1;
                    case BorderStyle.Fixed3D: return 2;
                    default: return 0;
                }
            }
            catch (Exception)
            {
                return 0;
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
                    case "PropertyGridView": return A11yRole.Table;
                    case "MonthCalendar": return A11yRole.Calendar;
                    case "NumericUpDown":
                    case "DomainUpDown":
                    case "UpDownSpinner":
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
                // The control's own text, but only where the control is one whose text names it. A list's
                // text is the item showing in it and a spin box's is its value: neither is a name, and
                // answering with one leaves the control nameless to anything that goes looking for it.
                // A date picker is left unnamed, as Windows leaves it: what it reads out is the
                // date, and a name as well would say the date twice.
                if (c is DateTimePicker)
                    return string.Empty;

                if (c.GetStyle(ControlStyles.UseTextForAccessibility) && !string.IsNullOrEmpty(c.Text))
                    return c.Text;

                // The parts of a property grid. Windows names the button row and the grid itself
                // after the grid -- they are the grid, as far as anything reading the screen is
                // concerned -- and leaves the strip of help along the bottom anonymous.
                Control part_parent = c.Parent is PropertyGrid.BorderHelperControl ? c.Parent.Parent : c.Parent;
                var owning_grid = part_parent as PropertyGrid;
                if (owning_grid != null)
                {
                    if (c is PropertyGridView)
                        return NameOf(owning_grid) + " Properties Window";
                    return c is PropertyGrid.PropertyToolBar ? NameOf(owning_grid) : string.Empty;
                }

                // The parts a compound control is built from: the spin button between a spinner's
                // arrows, and the field beside it. Neither has a label of its own -- the label
                // belongs to the control they are part of -- and the tab-order walk below cannot
                // reach it, because the control itself is a tab stop and stops the search.
                if (c.Parent is UpDownBase)
                    return c is UpDownBase.UpDownSpinner ? "UpDown" : NameOf(c.Parent);

                // A scroll bar that no label introduces is named for the way it runs, which is how
                // Windows names the pair a scrolling control puts up.
                var bar = c as ScrollBar;
                if (bar != null && PrecedingLabel(c) == null)
                    return bar.vert ? "Vertical" : "Horizontal";

                // Otherwise the label in front of it, which is how a field on a form gets its name -- the
                // text beside it. Windows does this and so must we, or every control on a form laid out
                // that way is anonymous.
                Label label = PrecedingLabel(c);
                if (label != null && !string.IsNullOrEmpty(label.Text))
                    return label.Text;

                return string.Empty;
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        /// <summary>The label immediately in front of a control in tab order, whose text names it.
        /// <para>Walks backwards through the peers: a Label answers, and any visible tab stop stops the
        /// search -- a control that can be tabbed to is named by its own label, not by one further
        /// back. Windows walks tab order here where the old MSAA layer walked z-order.</para></summary>
        private static Label PrecedingLabel(Control c)
        {
            Control parent = c.Parent;
            if (parent == null)
                return null;
            ContainerControl container = parent.GetContainerControl() as ContainerControl;
            if (container == null)
                return null;

            for (Control previous = container.GetNextControl(c, false);
                 previous != null;
                 previous = container.GetNextControl(previous, false))
            {
                Label label = previous as Label;
                if (label != null)
                    return label;
                if (previous.Visible && previous.TabStop)
                    break;
            }
            return null;
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

        // ---- scrolling ----------------------------------------------------------------------
        //
        // A page taller than its window is reachable only by scrolling it, and a client with no
        // pointer -- an assistive technology, or a test driving the application through the
        // accessibility tree -- has no other way to bring a control into view. Windows answers that
        // with two patterns: the container says how far along it is and can be moved, and any
        // element can ask to be scrolled into view.

        /// <summary>The scrollable container itself: a panel with AutoScroll whose contents do not
        /// fit. A container that fits its contents is not scrollable and says so.</summary>
        internal static bool CanScroll(Control c)
        {
            bool h, v;
            return ScrollExtent(c, out h, out v) && (h || v);
        }

        private static bool ScrollExtent(Control c, out bool horizontal, out bool vertical)
        {
            horizontal = vertical = false;
            var panel = c as ScrollableControl;
            if (panel == null || !panel.AutoScroll)
                return false;
            try
            {
                Rectangle display = panel.DisplayRectangle;
                Size client = panel.ClientSize;
                horizontal = display.Width > client.Width;
                vertical = display.Height > client.Height;
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        internal static bool ScrollInfo(Control c, out double horizontalPercent, out double verticalPercent,
            out double horizontalViewSize, out double verticalViewSize,
            out bool horizontallyScrollable, out bool verticallyScrollable)
        {
            horizontalPercent = verticalPercent = NoScroll;
            horizontalViewSize = verticalViewSize = 100;
            if (!ScrollExtent(c, out horizontallyScrollable, out verticallyScrollable))
                return false;
            var panel = (ScrollableControl) c;
            Rectangle display = panel.DisplayRectangle;
            Size client = panel.ClientSize;
            // AutoScrollPosition reads back negated -- that is how the property is defined -- so the
            // distance actually scrolled is its magnitude.
            Point at = panel.AutoScrollPosition;
            if (horizontallyScrollable)
            {
                horizontalPercent = Percent(-at.X, display.Width - client.Width);
                horizontalViewSize = Percent(client.Width, display.Width);
            }
            if (verticallyScrollable)
            {
                verticalPercent = Percent(-at.Y, display.Height - client.Height);
                verticalViewSize = Percent(client.Height, display.Height);
            }
            return true;
        }

        /// <summary>What an axis reports when it does not scroll at all.</summary>
        internal const double NoScroll = -1.0;

        private static double Percent(int part, int whole)
        {
            if (whole <= 0)
                return 0;
            return Clamp(part * 100.0 / whole);
        }

        private static double Clamp(double percent)
        {
            return percent < 0 ? 0 : percent > 100 ? 100 : percent;
        }

        /// <summary>Move the container to a position given as a percentage of its range, leaving an
        /// axis where it is when that axis is given NoScroll.</summary>
        internal static void SetScrollPercent(Control c, double horizontalPercent, double verticalPercent)
        {
            bool h, v;
            if (!ScrollExtent(c, out h, out v))
                return;
            var panel = (ScrollableControl) c;
            Rectangle display = panel.DisplayRectangle;
            Size client = panel.ClientSize;
            Point at = panel.AutoScrollPosition;
            int x = -at.X, y = -at.Y;
            if (h && horizontalPercent >= 0)
                x = (int) Math.Round((display.Width - client.Width) * Clamp(horizontalPercent) / 100.0);
            if (v && verticalPercent >= 0)
                y = (int) Math.Round((display.Height - client.Height) * Clamp(verticalPercent) / 100.0);
            panel.AutoScrollPosition = new Point(x, y);
        }

        /// <summary>Step the container the way a scroll bar's arrows and its track do. The amounts
        /// are the ScrollAmount enumeration: 0 large decrement, 1 small decrement, 2 nothing,
        /// 3 large increment, 4 small increment.</summary>
        internal static void ScrollBy(Control c, int horizontalAmount, int verticalAmount)
        {
            bool h, v;
            if (!ScrollExtent(c, out h, out v))
                return;
            var panel = (ScrollableControl) c;
            Point at = panel.AutoScrollPosition;
            Size client = panel.ClientSize;
            int x = -at.X + Step(horizontalAmount, client.Width);
            int y = -at.Y + Step(verticalAmount, client.Height);
            panel.AutoScrollPosition = new Point(x, y);
        }

        private static int Step(int amount, int page)
        {
            int small = Math.Max(1, page / 10);
            switch (amount)
            {
                case 0: return -page;
                case 1: return -small;
                case 3: return page;
                case 4: return small;
                default: return 0;
            }
        }

        /// <summary>Bring an element into view by scrolling whatever contains it. Walks outwards, so
        /// a control nested several panels deep still surfaces.</summary>
        internal static void ScrollIntoView(object element)
        {
            Control c = element as Control;
            if (c == null)
            {
                // An item has no control of its own; bringing its owner into view is as close as
                // this gets, and is what a client asking for it actually wants to see.
                c = A11yItems.OwnerOf(element);
                if (c == null)
                    return;
            }
            try
            {
                for (Control parent = c.Parent; parent != null; parent = parent.Parent)
                {
                    bool h, v;
                    if (!ScrollExtent(parent, out h, out v) || (!h && !v))
                        continue;
                    var panel = (ScrollableControl) parent;
                    Rectangle target = parent.RectangleToClient(c.RectangleToScreen(c.ClientRectangle));
                    Point at = panel.AutoScrollPosition;
                    Size client = panel.ClientSize;
                    int x = -at.X, y = -at.Y;
                    if (v)
                    {
                        if (target.Top < 0)
                            y += target.Top;
                        else if (target.Bottom > client.Height)
                            y += Math.Min(target.Top, target.Bottom - client.Height);
                    }
                    if (h)
                    {
                        if (target.Left < 0)
                            x += target.Left;
                        else if (target.Right > client.Width)
                            x += Math.Min(target.Left, target.Right - client.Width);
                    }
                    panel.AutoScrollPosition = new Point(x, y);
                }
            }
            catch (Exception)
            {
            }
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
        /// <summary>The whole window on screen, frame and caption included, which is what a client
        /// expects of a top-level element -- and what every child's position is read against.</summary>
        bool TryWindowRect(out double x, out double y, out double width, out double height);
    }
}
