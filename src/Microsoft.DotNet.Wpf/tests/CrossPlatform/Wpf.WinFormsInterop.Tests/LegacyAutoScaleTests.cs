// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The legacy (.NET 1.x) form scaling: a form whose resources carry $this.AutoScaleBaseSize is scaled on load by
// the ratio of its font's measured size (Form.GetAutoScaleSize) to that base, plus .NET's 0.08 fudge. The WebGPU
// driver answered GetAutoScaleSize with (0, 0), so the ratio was 0 + 0.08 and every control of such a form came
// out at a twelfth of its size -- GraphWorX's Find dialog (base 5x13, Segoe UI 9) showed as a few slivers.
//
// The probe table is that dialog as stock .NET 10 lays it out at 96 DPI (an in-process dump of GwxFindReplaceForm
// under stock and under this stack, same harness): client 664x447 -> 854x586, a label 80x20 -> 102x26, the right
// panel 88x439 -> 113x576.
//

using System;
using System.Drawing;
using Xunit;
using SWF = System.Windows.Forms;

namespace Wpf.WinFormsInterop.Tests
{
    public class LegacyAutoScaleTests
    {
        private sealed class LegacyForm : SWF.Form
        {
#pragma warning disable CS0618 // the obsolete model is the point
            public void Scale() => ApplyAutoScaling();
#pragma warning restore CS0618
        }

        [Fact]
        public void GetAutoScaleSize_MeasuresTheFont_AsDotNetDoes()
        {
            using var font = new Font("Segoe UI", 9f);
            SizeF s = SWF.Form.GetAutoScaleSize(font);
            if (System.Drawing.WebGpuBackend.DisplayDpi.X == 96)
            {
                // stock .NET 10, Segoe UI 9 at 96 DPI: (6, 16) once rounded, which is what the scaling uses
                Assert.Equal(6, (int)Math.Round(s.Width));
                Assert.Equal(16, (int)Math.Round(s.Height));
            }
            Assert.InRange(s.Width, 3f, 20f);
            Assert.Equal(font.Height, s.Height);
            // a wider face measures wider
            using var wide = new Font("Segoe UI", 18f);
            Assert.True(SWF.Form.GetAutoScaleSize(wide).Width > s.Width);
        }

        [Theory]
        // what, from, stock after
        [InlineData("client", 664, 447, 854, 586)]
        [InlineData("label", 80, 20, 102, 26)]
        [InlineData("panel", 88, 439, 113, 576)]
        public void AFormWithAnAutoScaleBaseSize_ScalesLikeStock(string what, int w0, int h0, int w1, int h1)
        {
            if (System.Drawing.WebGpuBackend.DisplayDpi.X != 96) return;   // the table is 96 DPI's
            using var form = new LegacyForm { Font = new Font("Segoe UI", 9f) };
#pragma warning disable CS0618
            form.AutoScaleBaseSize = new Size(5, 13);   // GwxFindReplaceForm's $this.AutoScaleBaseSize
#pragma warning restore CS0618
            form.ClientSize = new Size(664, 447);
            form.MinimumSize = new Size(680, 400);   // its $this.MinimumSize: scaled too, it is what makes stock 854 wide
            var label = new SWF.Label { AutoSize = false, Location = new Point(0, 0), Size = new Size(80, 20) };
            var panel = new SWF.Panel { Location = new Point(576, 8), Size = new Size(88, 439) };   // panelRight, as its resources place it
            form.Controls.Add(label);
            form.Controls.Add(panel);
            Size Of() => what switch { "client" => form.ClientSize, "label" => label.Size, _ => panel.Size };
            Assert.Equal(new Size(w0, h0), Of());
            form.Scale();
            Assert.Equal(new Size(w1, h1), Of());
        }

        [Fact]
        public void TheClassicFont_IsTheClassicBase_SoGraphWorXsDialogIsNotScaled()
        {
            // GraphWorX sets Application.SetDefaultFont(Microsoft Sans Serif 8.25): in the app the dialog's font
            // measures exactly its 5x13 base, and stock leaves it at its designed 680x486.
            using var font = new Font("Microsoft Sans Serif", 8.25f);
            if (System.Drawing.WebGpuBackend.DisplayDpi.X != 96 || font.Name != "Microsoft Sans Serif") return;
            SizeF s = SWF.Form.GetAutoScaleSize(font);
            Assert.Equal(new Size(5, 13), new Size((int)Math.Round(s.Width), (int)Math.Round(s.Height)));
            using var form = new LegacyForm { Font = font };
#pragma warning disable CS0618
            form.AutoScaleBaseSize = new Size(5, 13);
#pragma warning restore CS0618
            form.ClientSize = new Size(664, 447);
            form.Scale();
            Assert.Equal(new Size(664, 447), form.ClientSize);
        }

        [Fact]
        public void TheSameFontMeasuresTheSame_NoScaling()
        {
            using var form = new LegacyForm { Font = new Font("Segoe UI", 9f) };
            SizeF cur = SWF.Form.GetAutoScaleSize(form.Font);
#pragma warning disable CS0618
            form.AutoScaleBaseSize = new Size((int)Math.Round(cur.Width), (int)Math.Round(cur.Height));
#pragma warning restore CS0618
            form.ClientSize = new Size(300, 200);
            form.Scale();
            Assert.Equal(new Size(300, 200), form.ClientSize);
        }
    }
}
