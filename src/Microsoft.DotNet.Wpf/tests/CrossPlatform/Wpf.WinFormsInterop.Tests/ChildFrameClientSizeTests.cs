// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// A child window's border is non-client, as in Win32: a 100x100 Panel with FixedSingle has a 98x98
// client starting one pixel in, a sunken ListBox a 96x96 one starting two in. This stack used to give
// every child its whole window as client and draw the border over its edge, so ClientSize,
// DisplayRectangle and everything laid out from them were the frame too large, and each control made
// up for it on its own.
//
// Every expected value below was measured off stock .NET 10 WinForms with visual styles (a probe
// printing Size, ClientSize, the client's offset from the window's corner and DisplayRectangle).
//

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using Xunit;

namespace Wpf.WinFormsInterop.Tests
{
    public class ChildFrameClientSizeTests
    {
        public static IEnumerable<object[]> Cases()
        {
            // name, factory, client size, client origin in the window, display rectangle
            yield return Case("Panel None", () => new Panel { BorderStyle = BorderStyle.None, Size = new Size(100, 100) }, 100, 100, 0, new Rectangle(0, 0, 100, 100));
            yield return Case("Panel FixedSingle", () => new Panel { BorderStyle = BorderStyle.FixedSingle, Size = new Size(100, 100) }, 98, 98, 1, new Rectangle(0, 0, 98, 98));
            yield return Case("Panel Fixed3D", () => new Panel { BorderStyle = BorderStyle.Fixed3D, Size = new Size(100, 100) }, 96, 96, 2, new Rectangle(0, 0, 96, 96));
            // Scroll bars are non-client too (WS_HSCROLL / WS_VSCROLL), inside the border.
            yield return Case("Panel AutoScroll both", () => new Panel { BorderStyle = BorderStyle.FixedSingle, AutoScroll = true, AutoScrollMinSize = new Size(300, 300), Size = new Size(100, 100) }, 81, 81, 1, new Rectangle(0, 0, 300, 300));
            yield return Case("Panel AutoScroll horizontal", () => new Panel { BorderStyle = BorderStyle.FixedSingle, AutoScroll = true, AutoScrollMinSize = new Size(300, 50), Size = new Size(100, 100) }, 98, 81, 1, new Rectangle(0, 0, 300, 81));
            yield return Case("Panel AutoScroll vertical", () => new Panel { BorderStyle = BorderStyle.Fixed3D, AutoScroll = true, AutoScrollMinSize = new Size(50, 300), Size = new Size(100, 100) }, 79, 96, 2, new Rectangle(0, 0, 79, 300));
            yield return Case("UserControl FixedSingle", () => new UserControl { BorderStyle = BorderStyle.FixedSingle, Size = new Size(100, 100) }, 98, 98, 1, new Rectangle(0, 0, 98, 98));
            yield return Case("UserControl Fixed3D", () => new UserControl { BorderStyle = BorderStyle.Fixed3D, Size = new Size(100, 100) }, 96, 96, 2, new Rectangle(0, 0, 96, 96));
            yield return Case("ListBox Fixed3D", () => new ListBox { IntegralHeight = false, Size = new Size(100, 100) }, 96, 96, 2, new Rectangle(0, 0, 96, 96));
            yield return Case("ListBox FixedSingle", () => new ListBox { IntegralHeight = false, BorderStyle = BorderStyle.FixedSingle, Size = new Size(100, 100) }, 98, 98, 1, new Rectangle(0, 0, 98, 98));
            yield return Case("TextBox Fixed3D", () => new TextBox { Size = new Size(100, 23) }, 96, 19, 2, new Rectangle(0, 0, 96, 19));
            // The edit control takes WS_BORDER off its window and draws the line in its client.
            yield return Case("TextBox FixedSingle", () => new TextBox { BorderStyle = BorderStyle.FixedSingle, Size = new Size(100, 23) }, 100, 23, 0, new Rectangle(0, 0, 100, 23));
            yield return Case("TextBox Multiline", () => new TextBox { Multiline = true, Size = new Size(100, 100) }, 96, 96, 2, new Rectangle(0, 0, 96, 96));
            yield return Case("RichTextBox Fixed3D", () => new RichTextBox { Size = new Size(100, 100) }, 96, 96, 2, new Rectangle(0, 0, 96, 96));
            yield return Case("TreeView Fixed3D", () => new TreeView { Size = new Size(100, 100) }, 96, 96, 2, new Rectangle(0, 0, 96, 96));
            yield return Case("TreeView FixedSingle", () => new TreeView { BorderStyle = BorderStyle.FixedSingle, Size = new Size(100, 100) }, 98, 98, 1, new Rectangle(0, 0, 98, 98));
            yield return Case("ListView", () => new ListView { Size = new Size(100, 100) }, 96, 96, 2, new Rectangle(0, 0, 96, 96));
            yield return Case("ListView Details", () =>
            {
                var lv = new ListView { View = View.Details, Size = new Size(100, 100) };
                lv.Columns.Add("A", 60);
                return lv;
            }, 96, 96, 2, new Rectangle(0, 0, 96, 96));
            // A grid draws its own border; its window has none.
            yield return Case("DataGridView FixedSingle", () => new DataGridView { Size = new Size(100, 100) }, 100, 100, 0, new Rectangle(0, 0, 100, 100));
            yield return Case("DataGridView Fixed3D", () => new DataGridView { BorderStyle = BorderStyle.Fixed3D, Size = new Size(100, 100) }, 100, 100, 0, new Rectangle(0, 0, 100, 100));
            yield return Case("SplitContainer Fixed3D", () => new SplitContainer { BorderStyle = BorderStyle.Fixed3D, Size = new Size(100, 100) }, 100, 100, 0, new Rectangle(0, 0, 100, 100));
            yield return Case("GroupBox", () => new GroupBox { Size = new Size(100, 100) }, 100, 100, 0, new Rectangle(3, 19, 94, 78));
            // A Static's border is one pixel either way (Fixed3D is WS_EX_STATICEDGE).
            yield return Case("Label FixedSingle", () => new Label { BorderStyle = BorderStyle.FixedSingle, Size = new Size(100, 30) }, 98, 28, 1, new Rectangle(0, 0, 98, 28));
            yield return Case("Label Fixed3D", () => new Label { BorderStyle = BorderStyle.Fixed3D, Size = new Size(100, 30) }, 98, 28, 1, new Rectangle(0, 0, 98, 28));
            yield return Case("PictureBox FixedSingle", () => new PictureBox { BorderStyle = BorderStyle.FixedSingle, Size = new Size(100, 100) }, 98, 98, 1, new Rectangle(0, 0, 98, 98));
            yield return Case("PictureBox Fixed3D", () => new PictureBox { BorderStyle = BorderStyle.Fixed3D, Size = new Size(100, 100) }, 96, 96, 2, new Rectangle(0, 0, 96, 96));
            yield return Case("ComboBox", () => new ComboBox { Size = new Size(100, 23) }, 100, 23, 0, new Rectangle(0, 0, 100, 23));
        }

        private static object[] Case(string name, Func<Control> make, int w, int h, int origin, Rectangle display)
            => new object[] { name, make, new Size(w, h), origin, display };

        [Theory]
        [MemberData(nameof(Cases))]
        public void AChildsBorder_IsOutsideItsClient(string name, Func<Control> make, Size client, int origin, Rectangle display)
        {
            // Measured from a frameless parent: a form that is never shown has no host frame to be
            // measured against.
            using var f = new Form { ClientSize = new Size(300, 300) };
            var host = new Panel { Bounds = new Rectangle(0, 0, 300, 300) };
            Control c = make();
            c.Location = new Point(40, 30);
            host.Controls.Add(c);
            f.Controls.Add(host);
            Create(f, host, c);

            Assert.True(client == c.ClientSize, $"{name}: ClientSize {c.ClientSize}, stock {client}");
            Point at = c.PointToScreen(Point.Empty), parent = host.PointToScreen(Point.Empty);
            Assert.True(new Point(40 + origin, 30 + origin) == new Point(at.X - parent.X, at.Y - parent.Y),
                $"{name}: client at {at.X - parent.X},{at.Y - parent.Y} in the form, stock {40 + origin},{30 + origin}");
            Assert.True(display == c.DisplayRectangle, $"{name}: DisplayRectangle {c.DisplayRectangle}, stock {display}");
        }

        [Fact]
        public void ASpinBox_DrawsItsOwnFrame_WithVisualStyles()
        {
            // .NET's UpDownBase keeps no window frame under visual styles and paints a themed one in
            // its client; without them the border style is the window's WS_EX_CLIENTEDGE.
            using var f = new Form { ClientSize = new Size(300, 300) };
            var n = new NumericUpDown { Size = new Size(100, 23) };
            f.Controls.Add(n);
            Create(f, n);
            Size expected = Application.RenderWithVisualStyles ? new Size(100, 23) : new Size(96, 19);
            Assert.Equal(expected, n.ClientSize);
        }

        [Fact]
        public void AChildInABorderedPanel_StartsInsideTheBorder()
        {
            // A child positioned by Location sits that far from the panel's CLIENT, which starts
            // inside the frame -- not on it, and not a second frame further in.
            using var f = new Form { ClientSize = new Size(300, 300) };
            var host = new Panel { Bounds = new Rectangle(0, 0, 300, 300) };
            var p = new Panel { BorderStyle = BorderStyle.Fixed3D, Bounds = new Rectangle(10, 10, 200, 200) };
            var b = new Button { Bounds = new Rectangle(5, 30, 50, 20) };
            var d = new Button { Dock = DockStyle.Top, Height = 20 };
            p.Controls.Add(b);
            p.Controls.Add(d);
            host.Controls.Add(p);
            f.Controls.Add(host);
            Create(f, host, p, b, d);
            Point parent = host.PointToScreen(Point.Empty);
            Point at = b.PointToScreen(Point.Empty);
            Assert.Equal(new Point(10 + 2 + 5, 10 + 2 + 30), new Point(at.X - parent.X, at.Y - parent.Y));
            Assert.Equal(new Rectangle(0, 0, 196, 20), d.Bounds);
        }

        [Fact]
        public void ChangingTheBorderStyle_ChangesTheFrame()
        {
            using var f = new Form { ClientSize = new Size(300, 300) };
            var p = new Panel { Size = new Size(100, 100) };
            f.Controls.Add(p);
            Create(f, p);
            Assert.Equal(new Size(100, 100), p.ClientSize);
            p.BorderStyle = BorderStyle.Fixed3D;
            Assert.Equal(new Size(96, 96), p.ClientSize);
            Assert.Equal(new Size(100, 100), p.Size);
            p.BorderStyle = BorderStyle.FixedSingle;
            Assert.Equal(new Size(98, 98), p.ClientSize);
            p.BorderStyle = BorderStyle.None;
            Assert.Equal(new Size(100, 100), p.ClientSize);
        }

        /// <summary>Handles parent first, as showing the form would make them: CreateControl does
        /// nothing for a form that is never shown, and a child whose handle comes first has none
        /// to be a child of.</summary>
        private static void Create(params Control[] top_down)
        {
            foreach (Control c in top_down)
                _ = c.Handle;
        }
    }
}
