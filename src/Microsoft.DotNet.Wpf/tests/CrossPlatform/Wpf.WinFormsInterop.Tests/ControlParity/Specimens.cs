// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The controls under comparison, and the one way both sides are asked to draw them.
//
// This file is compiled TWICE, into two programs that cannot share a process: the test itself,
// which binds this fork's System.Windows.Forms, and StockRenderer, which binds the real
// Microsoft.WindowsDesktop.App one. Sharing the source is the whole point -- a difference in the
// pixels is then a difference in the implementation and can be nothing else. It therefore uses only
// API both stacks have, and must keep doing so.
//
// Control.DrawToBitmap is what makes this possible without a window, a screen or a GPU: both stacks
// implement it, both paint through their own theme, and the answer comes back as pixels. Every
// earlier comparison in this area went through a screen capture, which drags in the desktop
// composition, the window frame, whatever was behind the window and ClearType; none of that is the
// control's drawing and all of it had to be argued away afterwards.
//

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace WinFormsControlParity
{
    /// <summary>One control in one state, at a fixed size, with a name the report can use.</summary>
    internal sealed class Specimen
    {
        public Specimen(string name, int width, int height, Func<Control> create)
        {
            Name = name;
            Width = width;
            Height = height;
            Create = create;
        }

        public string Name { get; }
        public int Width { get; }
        public int Height { get; }
        public Func<Control> Create { get; }
    }

    internal static class Specimens
    {
        /// <summary>Every specimen, in a fixed order. Sizes are given rather than measured: a
        /// control that sizes itself differently on the two stacks would otherwise be compared at
        /// two different sizes, and the images would not even line up.</summary>
        internal static List<Specimen> All()
        {
            var all = new List<Specimen>();

            all.Add(new Specimen("button", 100, 32, () => new Button { Text = "Button" }));
            all.Add(new Specimen("button-disabled", 100, 32, () => new Button { Text = "Button", Enabled = false }));
            all.Add(new Specimen("button-flat", 100, 32, () => new Button { Text = "Button", FlatStyle = FlatStyle.Flat }));

            all.Add(new Specimen("checkbox", 120, 22, () => new CheckBox { Text = "CheckBox", Checked = true }));
            all.Add(new Specimen("checkbox-clear", 120, 22, () => new CheckBox { Text = "CheckBox" }));
            all.Add(new Specimen("checkbox-disabled", 120, 22, () => new CheckBox { Text = "CheckBox", Checked = true, Enabled = false }));

            all.Add(new Specimen("radio", 120, 22, () => new RadioButton { Text = "Radio", Checked = true }));
            all.Add(new Specimen("radio-clear", 120, 22, () => new RadioButton { Text = "Radio" }));

            all.Add(new Specimen("label", 120, 20, () => new Label { Text = "Label" }));
            all.Add(new Specimen("label-disabled", 120, 20, () => new Label { Text = "Label", Enabled = false }));
            all.Add(new Specimen("linklabel", 120, 20, () => new LinkLabel { Text = "LinkLabel" }));

            all.Add(new Specimen("textbox", 160, 24, () => new TextBox { Text = "Ada Lovelace" }));
            all.Add(new Specimen("textbox-readonly", 160, 24, () => new TextBox { Text = "read only", ReadOnly = true }));
            all.Add(new Specimen("textbox-password", 160, 24, () => new TextBox { Text = "password", UseSystemPasswordChar = true }));

            all.Add(new Specimen("groupbox", 180, 80, () => new GroupBox { Text = "GroupBox" }));
            all.Add(new Specimen("panel-border", 120, 60, () => new Panel { BorderStyle = BorderStyle.FixedSingle }));

            all.Add(new Specimen("progressbar", 200, 20, () => new ProgressBar { Value = 45 }));
            all.Add(new Specimen("progressbar-full", 200, 20, () => new ProgressBar { Value = 100 }));

            all.Add(new Specimen("listbox", 160, 90, () =>
            {
                var lb = new ListBox();
                lb.Items.AddRange(new object[] { "alpha", "bravo", "charlie", "delta" });
                lb.SelectedIndex = 1;
                return lb;
            }));

            all.Add(new Specimen("combobox-list", 160, 24, () =>
            {
                var c = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
                c.Items.AddRange(new object[] { "Vulkan", "Metal" });
                c.SelectedIndex = 0;
                return c;
            }));
            all.Add(new Specimen("combobox-editable", 160, 24, () =>
            {
                var c = new ComboBox();
                c.Items.AddRange(new object[] { "editable" });
                c.Text = "editable";
                return c;
            }));

            all.Add(new Specimen("checkedlistbox", 160, 70, () =>
            {
                var c = new CheckedListBox();
                c.Items.Add("checked", true);
                c.Items.Add("clear", false);
                return c;
            }));

            all.Add(new Specimen("treeview", 160, 90, () =>
            {
                var t = new TreeView();
                TreeNode root = t.Nodes.Add("Renderer");
                root.Nodes.Add("Device");
                t.ExpandAll();
                return t;
            }));

            all.Add(new Specimen("listview", 200, 90, () =>
            {
                var v = new ListView { View = View.Details, FullRowSelect = true };
                v.Columns.Add("Name", 90);
                v.Columns.Add("Value", 90);
                v.Items.Add(new ListViewItem(new[] { "Backend", "wgpu" }));
                v.Items.Add(new ListViewItem(new[] { "Surface", "HWND" }));
                return v;
            }));

            all.Add(new Specimen("tabcontrol", 200, 90, () =>
            {
                var t = new TabControl();
                t.TabPages.Add(new TabPage("Shapes"));
                t.TabPages.Add(new TabPage("Text"));
                return t;
            }));

            all.Add(new Specimen("numericupdown", 120, 24, () => new NumericUpDown { Value = 42 }));
            all.Add(new Specimen("trackbar", 180, 45, () => new TrackBar { Maximum = 10, Value = 4 }));
            all.Add(new Specimen("hscrollbar", 180, 17, () => new HScrollBar { Value = 30 }));
            all.Add(new Specimen("vscrollbar", 17, 90, () => new VScrollBar { Value = 30 }));
            all.Add(new Specimen("statusstrip", 200, 24, () =>
            {
                var s = new StatusStrip();
                s.Items.Add(new ToolStripStatusLabel("Ready"));
                return s;
            }));

            return all;
        }
    }
}
