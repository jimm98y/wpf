// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// A resize lays a container out once, as .NET does. The stack's ScrollableControl keeps its implicit
// scroll bars as child controls, and moved them -- hidden -- on every size change: that asked the
// container for a second layout, so every docked container of an application was laid out twice per
// step of a live resize (app: the main form, its config control, every DotNetBar dock site).
//

using System;
using System.Drawing;
using Xunit;
using SWF = System.Windows.Forms;

namespace Wpf.WinFormsInterop.Tests
{
    public sealed class LayoutPassTests
    {
        [Fact]
        public void AResize_LaysTheFormAndItsDockedPanelOutOnce()
        {
            using var form = new SWF.Form { ClientSize = new Size(300, 200), StartPosition = SWF.FormStartPosition.Manual,
                Location = new Point(8000, 80), ShowInTaskbar = false };
            var panel = new SWF.Panel { Dock = SWF.DockStyle.Fill };
            var inner = new SWF.Panel { Dock = SWF.DockStyle.Left, Width = 50 };
            panel.Controls.Add(inner);
            form.Controls.Add(panel);
            form.Show();
            SWF.Application.DoEvents();
            int formLayouts = 0, panelLayouts = 0;
            form.Layout += (s, e) => formLayouts++;
            panel.Layout += (s, e) => panelLayouts++;
            for (int i = 1; i <= 3; i++)
            {
                formLayouts = panelLayouts = 0;
                form.ClientSize = new Size(300 + i * 20, 200 + i * 10);
                SWF.Application.DoEvents();
                Assert.True(formLayouts == 1, $"resize {i}: the form was laid out {formLayouts} times");
                Assert.True(panelLayouts == 1, $"resize {i}: the docked panel was laid out {panelLayouts} times");
            }
            // ...and a scroll bar that is needed still comes, where it belongs.
            form.AutoScroll = true;
            form.AutoScrollMinSize = new Size(1000, 1000);
            form.ClientSize = new Size(260, 180);
            SWF.Application.DoEvents();
            Assert.True(form.VerticalScroll.Visible && form.HorizontalScroll.Visible, "no scroll bars for a 1000x1000 canvas");
            Assert.True(form.DisplayRectangle.Width >= 1000 && form.DisplayRectangle.Height >= 1000, $"display rectangle {form.DisplayRectangle}");
        }
    }
}
