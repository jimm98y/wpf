// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// WM_CONTEXTMENU that a child window does not take goes on to its parent, as DefWindowProc sends it: a
// right click on a label inside a panel shows the panel's menu. And the menu key asks for the menu from
// the keyboard, with no point. Neither reached any menu.
//

using System;
using System.Drawing;
using Xunit;
using SWF = System.Windows.Forms;

namespace Wpf.WinFormsInterop.Tests
{
    public class ContextMenuForwardTests
    {
        [Fact]
        public void AChildsUnhandledContextMenu_IsItsParents()
        {
            SWF.XplatUIWebGpu driver = SWF.XplatUIWebGpu.GetInstance();
            using var form = new SWF.Form { StartPosition = SWF.FormStartPosition.Manual, Location = new Point(9500, 2100), ClientSize = new Size(300, 200), ShowInTaskbar = false };
            var menu = new SWF.ContextMenuStrip();
            menu.Items.Add("Item");
            int opened = 0;
            menu.Opening += (s, e) => { opened++; e.Cancel = true; };
            var panel = new SWF.Panel { Bounds = new Rectangle(10, 10, 200, 150), ContextMenuStrip = menu };
            var label = new SWF.Label { Bounds = new Rectangle(20, 20, 100, 30), Text = "label" };
            panel.Controls.Add(label);
            form.Controls.Add(panel);
            form.Show();
            SWF.Application.DoEvents();

            Point at = label.PointToScreen(new Point(10, 10));
            driver.InjectRightDown(at.X, at.Y);
            driver.InjectRightUp(at.X, at.Y);
            SWF.Application.DoEvents();
            Assert.Equal(1, opened);
        }

        [Fact]
        public void TheMenuKey_AsksForTheMenuWithNoPoint()
        {
            using var form = new SWF.Form { ClientSize = new Size(300, 200), ShowInTaskbar = false };
            var menu = new SWF.ContextMenuStrip();
            menu.Items.Add("Item");
            int opened = 0;
            menu.Opening += (s, e) => { opened++; e.Cancel = true; };
            var box = new SWF.Panel { Bounds = new Rectangle(10, 10, 200, 150), ContextMenuStrip = menu };
            form.Controls.Add(box);
            Assert.NotEqual(IntPtr.Zero, box.Handle);

            var m = SWF.Message.Create(box.Handle, 0x0101 /* WM_KEYUP */, (IntPtr)0x5D /* VK_APPS */, IntPtr.Zero);
            box.GetType().GetMethod("DefWndProc", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .Invoke(box, new object[] { m });
            SWF.Application.DoEvents();
            Assert.Equal(1, opened);
        }
    }
}
