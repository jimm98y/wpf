// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// A ToolStripPanel's strips: ToolStripPanel.Join puts one in a given row or at a given point, and a strip is
// moved by its grip -- along its row, to another row, to a row of its own -- raising BeginDrag and EndDrag.
// The Mono port added every joined strip to the end, and a grip did nothing.
//

using System;
using System.Drawing;
using System.Linq;
using Xunit;
using SWF = System.Windows.Forms;

namespace Wpf.WinFormsInterop.Tests
{
    public class ToolStripDragTests
    {
        private sealed class Strip : SWF.ToolStrip
        {
            public Strip(string name) { Name = name; Items.Add(new SWF.ToolStripButton(name)); }
            public void Down(Point p) { Capture = true; OnMouseDown(new SWF.MouseEventArgs(SWF.MouseButtons.Left, 1, p.X, p.Y, 0)); }
            public void Move(Point p) => OnMouseMove(new SWF.MouseEventArgs(SWF.MouseButtons.Left, 0, p.X, p.Y, 0));
            public void Up(Point p) { OnMouseUp(new SWF.MouseEventArgs(SWF.MouseButtons.Left, 1, p.X, p.Y, 0)); Capture = false; }
        }

        private static (SWF.Form, SWF.ToolStripPanel) Host()
        {
            var form = new SWF.Form { StartPosition = SWF.FormStartPosition.Manual, Location = new Point(8000, 80), ClientSize = new Size(500, 300), ShowInTaskbar = false };
            var container = new SWF.ToolStripContainer { Dock = SWF.DockStyle.Fill };
            form.Controls.Add(container);
            form.Show();
            SWF.Application.DoEvents();
            return (form, container.TopToolStripPanel);
        }

        private static int RowOf(SWF.ToolStripPanel panel, SWF.ToolStrip ts)
            => Array.FindIndex(panel.Rows, r => r.Controls.Contains(ts));

        [Fact]
        public void Join_PutsAStripInTheRowOrAtThePointAskedFor()
        {
            var (form, panel) = Host();
            using (form) {
                var a = new Strip("a");
                var b = new Strip("b");
                panel.Join(a, 0);
                panel.Join(b, 1);
                Assert.Equal(2, panel.Rows.Length);
                Assert.Equal(0, RowOf(panel, a));
                Assert.Equal(1, RowOf(panel, b));

                // into the first row, 200 pixels along it
                panel.Join(b, new Point(200, a.Top + 2));
                Assert.Single(panel.Rows);
                Assert.Equal(0, RowOf(panel, b));
                Assert.InRange(b.Left, 200, 204);
                Assert.Throws<ArgumentOutOfRangeException>(() => panel.Join(b, -1));
            }
        }

        [Fact]
        public void TheGrip_MovesTheStrip_AndRaisesBeginAndEndDrag()
        {
            var (form, panel) = Host();
            using (form) {
                var a = new Strip("a");
                var b = new Strip("b");
                panel.Join(a, 0);
                panel.Join(b, 1);
                int begun = 0, ended = 0;
                bool draggingMidway = false;
                b.BeginDrag += (s, e) => begun++;
                b.EndDrag += (s, e) => ended++;

                Rectangle grip = b.GripRectangle;
                Assert.False(grip.IsEmpty);
                Point hold = new Point(grip.X + grip.Width / 2, grip.Y + grip.Height / 2);
                b.Down(hold);
                draggingMidway = b.IsCurrentlyDragging;
                // up into the first row, well along it: the pointer in b's coordinates at the move's moment
                Point target = b.PointToClient(panel.PointToScreen(new Point(250 + hold.X, a.Top + a.Height / 2)));
                b.Move(target);
                b.Up(b.PointToClient(panel.PointToScreen(new Point(250 + hold.X, a.Top + a.Height / 2))));

                Assert.Equal(1, begun);
                Assert.Equal(1, ended);
                Assert.True(draggingMidway);
                Assert.False(b.IsCurrentlyDragging);
                Assert.Single(panel.Rows);
                Assert.Equal(0, RowOf(panel, b));
                Assert.InRange(b.Left, 245, 256);
            }
        }
    
        private sealed class Dropping : SWF.ToolStrip
        {
            public SWF.DragEventArgs Args(SWF.IDataObject data, Point client)
            {
                Point screen = PointToScreen(client);
                return new SWF.DragEventArgs(data, 0, screen.X, screen.Y, SWF.DragDropEffects.Move | SWF.DragDropEffects.Copy, SWF.DragDropEffects.None);
            }
            public void Enter(SWF.DragEventArgs e) => OnDragEnter(e);
            public void Over(SWF.DragEventArgs e) => OnDragOver(e);
            public void Drop(SWF.DragEventArgs e) => OnDragDrop(e);
        }

        private static (SWF.Form, Dropping) StripHost(params string[] names)
        {
            var form = new SWF.Form { StartPosition = SWF.FormStartPosition.Manual, Location = new Point(8000, 80), ClientSize = new Size(500, 120), ShowInTaskbar = false };
            var strip = new Dropping();
            foreach (string n in names) strip.Items.Add(new SWF.ToolStripButton(n) { Name = n });
            form.Controls.Add(strip);
            form.Show();
            SWF.Application.DoEvents();
            return (form, strip);
        }

        [Fact]
        public void AllowDropAndAllowItemReorder_CannotBothBeTrue()
        {
            using var strip = new SWF.ToolStrip { AllowItemReorder = true };
            Assert.Throws<ArgumentException>(() => strip.AllowDrop = true);
            strip.AllowItemReorder = false;
            strip.AllowDrop = true;
            Assert.Throws<ArgumentException>(() => strip.AllowItemReorder = true);
        }

        [Fact]
        public void AnItemDroppedOnAReorderingStrip_GoesWhereItWasDropped()
        {
            var (form, strip) = StripHost("a", "b", "c");
            using (form) {
                strip.AllowItemReorder = true;
                var data = new SWF.DataObject();
                data.SetData(typeof(SWF.ToolStripItem), strip.Items["a"]);
                SWF.ToolStripItem c = strip.Items["c"];
                var beyond = new Point(c.Bounds.Right + 2, c.Bounds.Top + c.Bounds.Height / 2);
                var e = strip.Args(data, beyond);
                strip.Enter(e);
                Assert.Equal(SWF.DragDropEffects.Move, e.Effect);
                strip.Drop(strip.Args(data, beyond));
                Assert.Equal(new[] { "b", "c", "a" }, strip.Items.Cast<SWF.ToolStripItem>().Select(i => i.Name).ToArray());
            }
        }

        [Fact]
        public void AnItemThatAllowsDrops_GetsTheDragsOverIt()
        {
            var (form, strip) = StripHost("a", "b");
            using (form) {
                SWF.ToolStripItem b = strip.Items["b"];
                b.AllowDrop = true;
                int entered = 0, dropped = 0, stripDropped = 0;
                b.DragEnter += (s, e) => { entered++; e.Effect = SWF.DragDropEffects.Copy; };
                b.DragDrop += (s, e) => dropped++;
                strip.DragDrop += (s, e) => stripDropped++;
                var data = new SWF.DataObject(SWF.DataFormats.UnicodeText, "x");
                var over = new Point(b.Bounds.X + b.Bounds.Width / 2, b.Bounds.Y + b.Bounds.Height / 2);
                var e = strip.Args(data, over);
                strip.Enter(e);
                Assert.Equal(1, entered);
                Assert.Equal(SWF.DragDropEffects.Copy, e.Effect);
                strip.Drop(strip.Args(data, over));
                Assert.Equal(1, dropped);
                Assert.Equal(0, stripDropped);
            }
        }
    
        private static (SWF.Form, SWF.ToolStripPanel, SWF.ToolStrip, SWF.ToolStrip) Layout()
        {
            var form = new SWF.Form { StartPosition = SWF.FormStartPosition.Manual, Location = new Point(8000, 80), ClientSize = new Size(500, 300), ShowInTaskbar = false };
            var container = new SWF.ToolStripContainer { Dock = SWF.DockStyle.Fill, Name = "container" };
            container.TopToolStripPanel.Name = "top";
            form.Controls.Add(container);
            var a = new SWF.ToolStrip { Name = "a" };
            a.Items.AddRange(new SWF.ToolStripItem[] { new SWF.ToolStripButton("1") { Name = "one" }, new SWF.ToolStripButton("2") { Name = "two" }, new SWF.ToolStripButton("3") { Name = "three" } });
            var b = new SWF.ToolStrip { Name = "b" };
            b.Items.Add(new SWF.ToolStripButton("4") { Name = "four" });
            container.TopToolStripPanel.Join(a, 0);
            container.TopToolStripPanel.Join(b, 0);
            form.Show();
            SWF.Application.DoEvents();
            return (form, container.TopToolStripPanel, a, b);
        }

        [Fact]
        public void SaveSettingsAndLoadSettings_BringBackTheStripsRowsAndItemOrder()
        {
            const string key = "Wpf.WinFormsInterop.Tests.ToolStripSettingsRoundTrip";
            var (form, panel, a, b) = Layout();
            using (form) {
                panel.Join(b, 1);                       // b on a row of its own
                a.Items.Insert(0, a.Items["three"]);   // three, one, two
                SWF.ToolStripManager.SaveSettings(form, key);
            }

            var (again, panel2, a2, b2) = Layout();
            using (again) {
                Assert.Single(panel2.Rows);
                SWF.ToolStripManager.LoadSettings(again, key);
                Assert.Equal(2, panel2.Rows.Length);
                Assert.Equal(1, RowOf(panel2, b2));
                Assert.Equal(new[] { "three", "one", "two" }, a2.Items.Cast<SWF.ToolStripItem>().Select(i => i.Name).ToArray());
            }
        }
    
        private sealed class Restoring : SWF.ToolStrip
        {
            public Restoring() { TabStop = true; Items.Add(new SWF.ToolStripButton("x")); }
            public void Restore() => RestoreFocus();
        }

        [Fact]
        public void RestoreFocus_GivesTheFocusBackToWhereItCameFrom()
        {
            var form = new SWF.Form { StartPosition = SWF.FormStartPosition.Manual, Location = new Point(8000, 80), ClientSize = new Size(300, 120), ShowInTaskbar = false };
            using (form) {
                var box = new SWF.TextBox { Location = new Point(10, 60) };
                var strip = new Restoring();
                form.Controls.Add(box);
                form.Controls.Add(strip);
                form.Show();
                SWF.Application.DoEvents();
                box.Focus();
                Assert.True(box.Focused);
                strip.Focus();
                Assert.True(strip.Focused);
                strip.Restore();
                Assert.True(box.Focused);
            }
        }
    }
}
