// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// A node not drawn highlighted wears its own BackColor and ForeColor -- the selected node too. Stock's
// custom draw hands comctl32 the node's colours whenever the item is not shown as the focused
// selection. AlarmWorX's TreeViewEx marks its selection itself (BackColor = Highlight, ForeColor =
// HighlightText); we filled the selected node with the TreeView's white, under white text, and the
// "Alarm Viewer" node of its configurator vanished.
//

using System.Drawing;
using Xunit;
using SWF = System.Windows.Forms;

namespace Wpf.WinFormsInterop.Tests
{
    public class TreeViewSelectedNodeColorTests
    {
        private sealed class QuietForm : SWF.Form
        {
            protected override bool ShowWithoutActivation => true;
        }

        private static (Bitmap, Rectangle) Render(bool appColoured, bool hideSelection)
        {
            // Shown (a TreeView lays its nodes out only when visible), off every monitor, not activated.
            using var form = new QuietForm { ClientSize = new Size(200, 120), StartPosition = SWF.FormStartPosition.Manual,
                Location = new Point(8000, 80), ShowInTaskbar = false, FormBorderStyle = SWF.FormBorderStyle.None };
            var tree = new SWF.TreeView { Width = 200, Height = 120, HideSelection = hideSelection };
            var root = tree.Nodes.Add("Alarm Viewer");
            root.Nodes.Add("Tab");
            var elsewhere = new SWF.TextBox { Top = 130 };   // the focus is here, not on the tree
            form.Controls.Add(tree);
            form.Controls.Add(elsewhere);
            form.Show();
            tree.SelectedNode = root;
            elsewhere.Focus();
            Assert.False(tree.Focused);
            if (appColoured)
            {
                root.BackColor = SystemColors.Highlight;
                root.ForeColor = SystemColors.HighlightText;
            }
            var bmp = new Bitmap(tree.Width, tree.Height);
            tree.DrawToBitmap(bmp, new Rectangle(0, 0, tree.Width, tree.Height));
            // The root's row: the first item-high band of the client, inside the border.
            return (bmp, new Rectangle(2, 2, bmp.Width - 4, tree.ItemHeight));
        }

        private static int Count(Bitmap bmp, Rectangle r, Color c)
        {
            int n = 0;
            for (int y = r.Top; y < r.Bottom; y++)
                for (int x = r.Left; x < r.Right; x++)
                    if (bmp.GetPixel(x, y).ToArgb() == c.ToArgb()) n++;
            return n;
        }

        [Fact]
        public void ASelectedNodeWithItsOwnColours_ShowsThemUnfocused()
        {
            var (bmp, bounds) = Render(appColoured: true, hideSelection: true);
            using (bmp)
            {
                int area = bounds.Width * bounds.Height;
                int back = Count(bmp, bounds, SystemColors.Highlight);
                Assert.True(back > area / 5, $"{back} of {area} pixels of the root's row are the Highlight it set");
                // ...and its text shows on it: white pixels inside the highlighted run.
                int text = Count(bmp, bounds, SystemColors.HighlightText);
                Assert.True(text > 10, $"no text on the node ({text} white pixels)");
            }
        }

        [Fact]
        public void APlainSelectedNode_WithHideSelection_IsDrawnLikeTheOthers()
        {
            var (bmp, bounds) = Render(appColoured: false, hideSelection: true);
            using (bmp)
                Assert.Equal(0, Count(bmp, bounds, SystemColors.Highlight));
        }
    }
}
