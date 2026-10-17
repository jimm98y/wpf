// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// A window that paints exactly what it painted before keeps its previous scene, and one that changes a
// little keeps every primitive that did not change: the damage tracker compares by reference, so a
// repaint with the same pixels was damage regardless. app's main window repainted its whole
// ribbon form on every WM_MOVE and re-rendered 117,760 px a step for nothing.
//

using System;
using System.Collections;
using System.Drawing;
using System.Linq;
using Xunit;
using SWF = System.Windows.Forms;

namespace Wpf.WinFormsInterop.Tests
{
    public class SceneReuseTests
    {
        private sealed class QuietForm : SWF.Form
        {
            protected override bool ShowWithoutActivation => true;
        }

        private sealed class Painter : SWF.Control
        {
            public Color Second = Color.Green;
            protected override void OnPaint(SWF.PaintEventArgs e)
            {
                using var a = new SolidBrush(Color.Red);
                using var b = new SolidBrush(Second);
                e.Graphics.FillRectangle(a, 2, 2, 30, 20);
                e.Graphics.FillRectangle(b, 40, 2, 30, 20);
                e.Graphics.DrawString("text", Font, Brushes.Black, 2, 30);
            }
        }

        private static object Scene(IntPtr h) => SWF.XplatUIWebGpu.GetInstance().GetWindowScene(h);

        /// <summary>Every primitive of a scene, depth first.</summary>
        private static object[] Primitives(object scene)
        {
            var all = new System.Collections.Generic.List<object>();
            void Walk(object v)
            {
                foreach (object p in (IEnumerable)v.GetType().GetProperty("Content")!.GetValue(v)!) all.Add(p);
                foreach (object c in (IEnumerable)v.GetType().GetProperty("Children")!.GetValue(v)!) Walk(c);
            }
            Walk(scene);
            return all.ToArray();
        }

        [Fact]
        public void ARepaintThatDrawsTheSame_KeepsTheScene_AndAChangeKeepsTheRest()
        {
            using var form = new QuietForm { ClientSize = new Size(200, 100), StartPosition = SWF.FormStartPosition.Manual,
                Location = new Point(8000, 80), ShowInTaskbar = false, FormBorderStyle = SWF.FormBorderStyle.None };
            var painter = new Painter { Bounds = new Rectangle(5, 5, 150, 60) };
            form.Controls.Add(painter);
            form.Show();
            SWF.Application.DoEvents();
            IntPtr h = painter.Handle;

            painter.Refresh();
            SWF.Application.DoEvents();
            object first = Scene(h);
            Assert.NotNull(first);
            object[] before = Primitives(first);

            painter.Refresh();
            SWF.Application.DoEvents();
            Assert.Same(first, Scene(h));

            painter.Second = Color.Blue;
            painter.Refresh();
            SWF.Application.DoEvents();
            object changed = Scene(h);
            Assert.NotSame(first, changed);
            object[] after = Primitives(changed);
            Assert.Equal(before.Length, after.Length);
            int kept = before.Zip(after, ReferenceEquals).Count(x => x);
            Assert.Equal(before.Length - 1, kept);   // all but the recoloured rectangle
        }
    }
}
