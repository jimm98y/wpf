// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// A LinkLabel whose text wraps shows every line of it. It draws its text clipped to each piece's region, which the
// controls' measuring context measured on one line: past the first line no piece had a region, and nothing of a
// wrapped link label after its first line was drawn -- a link at the end of a sentence did not show at all.
//

using System;
using System.Drawing;
using System.Threading;
using Xunit;
using SWF = System.Windows.Forms;

namespace Wpf.WinFormsInterop.Tests
{
    public class LinkLabelWrapTests
    {
        [Fact]
        public void AWrappedLinkLabel_DrawsItsLinkOnTheSecondLine()
        {
            Bitmap? shot = null;
            Exception? failure = null;
            var t = new Thread(() =>
            {
                try
                {
                    string text = "An older version is installed. Its settings are kept; see what changes.";
                    using var form = new SWF.Form
                    {
                        StartPosition = SWF.FormStartPosition.Manual, Location = new Point(8000, 80),
                        ClientSize = new Size(240, 60), ShowInTaskbar = false, BackColor = Color.White,
                    };
                    var label = new SWF.LinkLabel { AutoSize = false, Size = new Size(220, 44), BackColor = Color.White, LinkColor = Color.Blue };
                    label.Text = text;
                    label.Links.Clear();
                    label.Links.Add(text.IndexOf("what", StringComparison.Ordinal), "what changes".Length, "x");
                    form.Controls.Add(label);
                    form.Shown += (s, e) => form.BeginInvoke(new Action(() =>
                    {
                        shot = new Bitmap(label.Width, label.Height);
                        label.DrawToBitmap(shot, new Rectangle(Point.Empty, label.Size));
                        form.Close();
                    }));
                    SWF.Application.Run(form);
                }
                catch (Exception ex) { failure = ex; }
            });
            t.TrySetApartmentState(ApartmentState.STA);
            t.Start();
            Assert.True(t.Join(TimeSpan.FromSeconds(30)));
            if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();

            using (shot)
            {
                int lineHeight = SWF.Control.DefaultFont.Height;
                int blueBelow = 0, inkBelow = 0;
                for (int y = lineHeight; y < shot!.Height; y++)
                    for (int x = 0; x < shot.Width; x++)
                    {
                        Color c = shot.GetPixel(x, y);
                        if (c.B > 160 && c.R < 120 && c.G < 120) blueBelow++;
                        if (c.GetBrightness() < 0.5f) inkBelow++;
                    }
                // the link is on the second line, and so is the rest of the sentence's first half
                Assert.True(blueBelow > 20, $"no link drawn below the first line ({blueBelow} blue pixels)");
                Assert.True(inkBelow > 20, $"nothing drawn below the first line ({inkBelow} dark pixels)");
            }
        }
    }
}
