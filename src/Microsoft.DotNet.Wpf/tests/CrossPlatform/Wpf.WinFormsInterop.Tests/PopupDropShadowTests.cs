// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Windows shades a popup when its window class asks for it (CS_DROPSHADOW) and drop shadows are on, not
// because it is a popup. The driver shaded every WS_POPUP window, so DotNetBar's menus -- whose class
// does not ask, and which put a layered shadow window of their own under the menu -- had a second
// shadow, standing off the menu past the two pixels DotNetBar leaves for its own.
//

using System;
using System.Collections;
using System.Drawing;
using Xunit;
using SWF = System.Windows.Forms;

namespace Wpf.WinFormsInterop.Tests
{
    public class PopupDropShadowTests
    {
        private sealed class Popup : SWF.Control
        {
            private readonly bool _dropShadow;
            public Popup(bool dropShadow) { _dropShadow = dropShadow; SetTopLevel(true); }
            protected override SWF.CreateParams CreateParams
            {
                get
                {
                    var cp = base.CreateParams;
                    cp.Style = unchecked((int)0x86000000);           // WS_POPUP | WS_CLIPSIBLINGS | WS_CLIPCHILDREN
                    cp.ExStyle = 0x80;                               // WS_EX_TOOLWINDOW
                    if (_dropShadow) cp.ClassStyle |= 0x00020000;    // CS_DROPSHADOW
                    return cp;
                }
            }
            protected override void OnPaint(SWF.PaintEventArgs e) => e.Graphics.FillRectangle(Brushes.White, ClientRectangle);
        }

        private static int Visuals(object scene)
        {
            int n = 1;
            foreach (object c in (IEnumerable)scene.GetType().GetProperty("Children")!.GetValue(scene)!) n += Visuals(c);
            return n;
        }

        private static int ShownVisuals(bool dropShadow)
        {
            using var p = new Popup(dropShadow) { Bounds = new Rectangle(8000, 80, 60, 40) };
            p.Show();
            p.Refresh();
            SWF.Application.DoEvents();
            object scene = SWF.XplatUIWebGpu.GetInstance().GetWindowScene(p.Handle);
            Assert.NotNull(scene);
            return Visuals(scene);
        }

        [Fact]
        public void OnlyAPopupWhoseClassAsks_IsShaded()
        {
            Assert.True(SWF.SystemInformation.IsDropShadowEnabled);
            Assert.True(ShownVisuals(dropShadow: true) > ShownVisuals(dropShadow: false));
        }
    }
}
