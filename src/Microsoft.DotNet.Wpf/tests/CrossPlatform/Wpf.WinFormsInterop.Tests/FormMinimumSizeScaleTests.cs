// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// A designer form's MinimumSize bounds its WINDOW, not its client area. Mono clamped ClientSize to it,
// so a dialog designed with MinimumSize = its window size came out a frame larger than stock each way:
// app's "Configure your Control" (ClientSize 608x600, MinimumSize 670x638, AutoScaleMode.Font
// from 6x13) was 686x677 where stock is 670x639. Expected sizes recorded from stock .NET 10 at 96 dpi
// on Windows 11 (a sizable frame: 16 wide, 39 tall).
//

using System.Drawing;
using Xunit;
using SWF = System.Windows.Forms;

namespace Wpf.WinFormsInterop.Tests
{
    public class FormMinimumSizeScaleTests
    {
        private sealed class Designed : SWF.Form
        {
            public Designed(Font? font, bool minimumSize)
            {
                SuspendLayout();
                if (font != null) Font = font;
                var bottom = new SWF.Panel { Dock = SWF.DockStyle.Bottom, Size = new Size(608, 43) };
                var main = new SWF.Panel { Dock = SWF.DockStyle.Fill, Size = new Size(608, 557) };
                Tree = new SWF.TreeView { Location = new Point(9, 12), Size = new Size(179, 510),
                    Anchor = SWF.AnchorStyles.Top | SWF.AnchorStyles.Bottom | SWF.AnchorStyles.Left };
                main.Controls.Add(Tree);
                AutoScaleDimensions = new SizeF(6F, 13F);
                ClientSize = new Size(608, 600);
                if (minimumSize) MinimumSize = new Size(670, 638);
                AutoScaleMode = SWF.AutoScaleMode.Font;
                Controls.Add(main);
                Controls.Add(bottom);
                MaximizeBox = false;
                MinimizeBox = false;
                ShowIcon = false;
                ShowInTaskbar = false;
                ResumeLayout(false);
            }

            public SWF.TreeView Tree;
        }

        [Fact]
        public void ADesignedDialog_InMicrosoftSansSerif_HasStocksSize()
        {
            Assert.SkipUnless(System.OperatingSystem.IsWindows(), "stock's numbers are Windows 11's frame");
            using var f = new Designed(new Font("Microsoft Sans Serif", 8.25f), minimumSize: true);
            Assert.Equal(new SizeF(6, 13), f.CurrentAutoScaleDimensions);
            Assert.Equal(new Size(670, 639), f.Size);
            Assert.Equal(new Size(654, 600), f.ClientSize);
            Assert.Equal(new Size(670, 638), f.MinimumSize);
            Assert.Equal(new Rectangle(9, 12, 179, 510), f.Tree.Bounds);
        }

        [Fact]
        public void ADesignedDialog_InTheDefaultFont_ScalesThenMeetsItsMinimum()
        {
            Assert.SkipUnless(System.OperatingSystem.IsWindows(), "stock's numbers are Windows 11's frame");
            using var f = new Designed(null, minimumSize: true);
            Assert.Equal(new SizeF(7, 15), f.CurrentAutoScaleDimensions);
            Assert.Equal(new Size(779, 731), f.Size);
            Assert.Equal(new Size(763, 692), f.ClientSize);
            Assert.Equal(new Size(779, 730), f.MinimumSize);
        }

        [Fact]
        public void ADesignedDialog_WithoutAMinimum_KeepsItsScaledClientSize()
        {
            Assert.SkipUnless(System.OperatingSystem.IsWindows(), "stock's numbers are Windows 11's frame");
            using var f = new Designed(null, minimumSize: false);
            Assert.Equal(new Size(725, 731), f.Size);
            Assert.Equal(new Size(709, 692), f.ClientSize);
        }
    }
}
