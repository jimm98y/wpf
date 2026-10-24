// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// SizeGripStyle.Auto: a sizable form shown with ShowDialog has a grip in its bottom-right corner --
// with visual styles Windows 11's six #BFBFBF dots -- and one shown modelessly has none. The grip
// was never shown, since ShowDialog did not re-evaluate it.
//

using System;
using System.Drawing;
using System.Threading;
using Xunit;
using SWF = System.Windows.Forms;

namespace Wpf.WinFormsInterop.Tests
{
    public class FormSizeGripTests
    {
        private static Color Corner(bool modal)
        {
            Color c = Color.Empty;
            Exception? failure = null;
            var t = new Thread(() =>
            {
                try
                {
                    SWF.Application.EnableVisualStyles();
                    using var f = new SWF.Form { StartPosition = SWF.FormStartPosition.Manual, Location = new Point(60, 60), ClientSize = new Size(200, 100), ShowInTaskbar = false };
                    void Grab()
                    {
                        SWF.Application.DoEvents();
                        using var b = new Bitmap(f.ClientSize.Width, f.ClientSize.Height);
                        f.DrawToBitmap(b, new Rectangle(0, 0, b.Width, b.Height));
                        c = b.GetPixel(b.Width - 4, b.Height - 4);
                    }
                    if (modal) { f.Shown += (s, e) => { Grab(); f.Close(); }; f.ShowDialog(); }
                    else { f.Show(); Grab(); f.Close(); }
                }
                catch (Exception ex) { failure = ex; }
            });
            t.TrySetApartmentState(ApartmentState.STA);
            t.Start();
            t.Join();
            if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
            return c;
        }

        [Fact]
        public void ModalSizableForm_HasTheGrip()
            => Assert.Equal(Color.FromArgb(191, 191, 191).ToArgb(), Corner(true).ToArgb());

        [Fact]
        public void ModelessForm_HasNone()
            => Assert.NotEqual(Color.FromArgb(191, 191, 191).ToArgb(), Corner(false).ToArgb());
    }
}
