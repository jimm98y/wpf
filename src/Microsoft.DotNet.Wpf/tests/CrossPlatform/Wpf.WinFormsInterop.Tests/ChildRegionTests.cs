// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// A child control's Region is its shape, as SetWindowRgn makes it on Windows: outside it the control is not
// there for the mouse (the click is its parent's) and paints nothing (its paint is recorded clipped to it).
// A round button was a rectangle that took every click in its corners.
//

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using Xunit;
using SWF = System.Windows.Forms;

namespace Wpf.WinFormsInterop.Tests
{
    public class ChildRegionTests
    {
        [Fact]
        public void ARoundChild_TakesClicksOnlyInsideItsCircle()
        {
            SWF.XplatUIWebGpu driver = SWF.XplatUIWebGpu.GetInstance();
            using var form = new SWF.Form { StartPosition = SWF.FormStartPosition.Manual, Location = new Point(9500, 4900), ClientSize = new Size(300, 200), ShowInTaskbar = false };
            var round = new SWF.Panel { Bounds = new Rectangle(50, 50, 100, 100) };
            using (var path = new GraphicsPath())
            {
                path.AddEllipse(0, 0, 100, 100);
                round.Region = new Region(path);
            }
            int roundDown = 0, formDown = 0;
            round.MouseDown += (s, e) => roundDown++;
            form.MouseDown += (s, e) => formDown++;
            form.Controls.Add(round);
            form.Show();
            round.Refresh();
            SWF.Application.DoEvents();

            Point centre = round.PointToScreen(new Point(50, 50)), corner = round.PointToScreen(new Point(3, 3));
            driver.InjectMouseDown(corner.X, corner.Y); driver.InjectMouseUp(corner.X, corner.Y);
            Assert.Equal((0, 1), (roundDown, formDown));             // the corner is the form's
            driver.InjectMouseDown(centre.X, centre.Y); driver.InjectMouseUp(centre.X, centre.Y);
            Assert.Equal((1, 1), (roundDown, formDown));             // the circle is the child's

            round.Region = null;                                     // a rectangle again
            driver.InjectMouseDown(corner.X, corner.Y); driver.InjectMouseUp(corner.X, corner.Y);
            Assert.Equal((2, 1), (roundDown, formDown));
        }
    }
}
