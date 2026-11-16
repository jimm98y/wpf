// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// A drag event starts from the effect the last one chose, as .NET's DropTarget does on Windows: a
// target that says Copy in DragEnter and leaves Effect alone in DragDrop (the usual handler) has
// copied, and DoDragDrop returns Copy. Started from None, the drop reported nothing done -- a drag
// out of one WinForms form onto another's list came back "None" though the list had taken it.
//

using System.Drawing;
using System.Windows.Forms;
using Xunit;

namespace Wpf.WinFormsInterop.Tests
{
    public class DragEffectCarryTests
    {
        [Fact]
        public void TheDropStartsFromWhatDragEnterChose()
        {
            XplatUIWebGpu driver = XplatUIWebGpu.GetInstance();
            Assert.NotNull(driver);
            using var form = new Form { ClientSize = new Size(200, 120), StartPosition = FormStartPosition.Manual, Location = new Point(40, 40), ShowInTaskbar = false };
            var list = new ListBox { Dock = DockStyle.Fill, AllowDrop = true };
            DragDropEffects seenInOver = DragDropEffects.None, seenInDrop = DragDropEffects.None;
            list.DragEnter += (s, e) => e.Effect = DragDropEffects.Copy;
            list.DragOver += (s, e) => seenInOver = e.Effect;
            list.DragDrop += (s, e) => seenInDrop = e.Effect;
            form.Controls.Add(list);
            form.Show();      // hit-testable: the driver finds only visible windows under a point

            Point p = list.PointToScreen(new Point(50, 30));
            var data = new DataObject(DataFormats.UnicodeText, "x");
            var allowed = DragDropEffects.Copy | DragDropEffects.Move;

            Assert.Equal(DragDropEffects.Copy, driver.InjectDragEnterIn(form.Handle, p.X, p.Y, data, allowed, 1));
            driver.InjectDragOverIn(form.Handle, p.X, p.Y, data, allowed, 1);
            DragDropEffects dropped = driver.InjectDragDropIn(form.Handle, p.X, p.Y, data, allowed, 1);

            Assert.Equal(DragDropEffects.Copy, seenInOver);
            Assert.Equal(DragDropEffects.Copy, seenInDrop);
            Assert.Equal(DragDropEffects.Copy, dropped);
        }
    }
}
