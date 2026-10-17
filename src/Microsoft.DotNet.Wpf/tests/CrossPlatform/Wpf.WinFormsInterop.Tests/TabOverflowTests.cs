// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// A single-line TabControl whose tabs do not fit scrolls them the way comctl32 does: the tabs keep
// their own widths and places (those scrolled off sit to the left of the strip), GetTabRect is the
// tab's own rectangle selected or not, and selecting a tab past the up-down control scrolls by as
// few tabs as it takes. GraphWorX's configurator drew its selected tab over the first one shown.
// Expected rectangles recorded from stock .NET 10 (visual styles, Segoe UI 9, 96 dpi).
//

using System.Drawing;
using System.Text;
using Xunit;
using SWF = System.Windows.Forms;

namespace Wpf.WinFormsInterop.Tests
{
    public class TabOverflowTests
    {
        private sealed class QuietForm : SWF.Form
        {
            protected override bool ShowWithoutActivation => true;
        }

        [Theory]
        [InlineData(0, "[2,2,62,20] [64,2,75,20] [139,2,88,20] [227,2,98,20] [325,2,118,20] [443,2,65,20]")]
        [InlineData(3, "[-135,2,62,20] [-73,2,75,20] [2,2,88,20] [90,2,98,20] [188,2,118,20] [306,2,65,20]")]
        [InlineData(5, "[-321,2,62,20] [-259,2,75,20] [-184,2,88,20] [-96,2,98,20] [2,2,118,20] [120,2,65,20]")]
        public void AnOverflowingStrip_HasStocksTabRects(int select, string expected)
        {
            using var form = new QuietForm { StartPosition = SWF.FormStartPosition.Manual, Location = new Point(8000, 80),
                ClientSize = new Size(400, 200), ShowInTaskbar = false };
            var tc = new SWF.TabControl { Left = 5, Top = 5, Width = 300, Height = 150 };
            foreach (var t in new[] { "Status list", "Appearance", "Text Translator", "Grouping Range", "Advanced Grouping", "Condition" })
                tc.TabPages.Add(t);
            form.Controls.Add(tc);
            form.Show();
            SWF.Application.DoEvents();
            // As stock was driven: 0, 1, 3, 5 in turn.
            foreach (int s in new[] { 0, 1, 3, 5 })
            {
                tc.SelectedIndex = s;
                SWF.Application.DoEvents();
                if (s == select) break;
            }
            var sb = new StringBuilder();
            for (int i = 0; i < tc.TabCount; i++)
            {
                Rectangle r = tc.GetTabRect(i);
                sb.Append(i == 0 ? "" : " ").Append($"[{r.X},{r.Y},{r.Width},{r.Height}]");
            }
            Assert.Equal(expected, sb.ToString());
            form.Close();
        }
    }
}
