// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// A Form's Size is its WINDOW, frame included, and ClientSize is what is inside it: on Windows a
// sizable form set to ClientSize 400x300 is 416x339. The driver modelled no frame, so the two were
// equal -- code that sizes a form and lays out its client came out 16 pixels too wide, and anything
// that reads the parent's Size (the tool strip renderers shade a strip as a slice of its parent's
// width) disagreed with Windows. The frame is Windows 11's, from AdjustWindowRectExForDpi at 96 dpi.
//

using System;
using System.Drawing;
using System.Windows.Forms;
using Xunit;

namespace Wpf.WinFormsInterop.Tests
{
    public class FormFrameTests
    {
        [Theory]
        [InlineData(FormBorderStyle.Sizable, 16, 39)]
        [InlineData(FormBorderStyle.FixedSingle, 16, 39)]
        [InlineData(FormBorderStyle.Fixed3D, 20, 43)]
        [InlineData(FormBorderStyle.FixedDialog, 16, 39)]
        [InlineData(FormBorderStyle.SizableToolWindow, 16, 39)]
        [InlineData(FormBorderStyle.None, 0, 0)]
        public void SizeIsClientPlusTheWindowsFrame(FormBorderStyle style, int dw, int dh)
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "the frame is the host's; Windows' is Windows 11's");
            using var form = new Form { FormBorderStyle = style, ClientSize = new Size(400, 300) };
            Assert.Equal(new Size(400, 300), form.ClientSize);
            Assert.Equal(new Size(400 + dw, 300 + dh), form.Size);
        }

        [Fact]
        public void SettingSizeLeavesTheClientInside()
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "the frame is the host's; Windows' is Windows 11's");
            using var form = new Form { Size = new Size(300, 200) };
            Assert.Equal(new Size(284, 161), form.ClientSize);
        }
    }
}
