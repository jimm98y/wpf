// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// A control inside a disabled container looks disabled -- exactly as it does disabled itself. In
// AlarmWorX's "Configure your Control" the Title group is disabled when its check box is clear; stock
// greys the Font combo and its label with it, ours drew both enabled.
//

using System.Drawing;
using Xunit;
using SWF = System.Windows.Forms;

namespace Wpf.WinFormsInterop.Tests
{
    public class DisabledParentChildLookTests
    {
        private sealed class QuietForm : SWF.Form
        {
            protected override bool ShowWithoutActivation => true;
        }

        /// <summary>The child drawn inside a group, with the GROUP disabled or the CHILD disabled.</summary>
        private static Bitmap Draw(SWF.Control child, bool disableGroup, bool beforeShow)
        {
            using var form = new QuietForm { ClientSize = new Size(300, 120), StartPosition = SWF.FormStartPosition.Manual,
                Location = new Point(8000, 80), ShowInTaskbar = false, FormBorderStyle = SWF.FormBorderStyle.None };
            var group = new SWF.GroupBox { Left = 5, Top = 5, Width = 280, Height = 100, Text = "Title" };
            child.Left = 10; child.Top = 30;
            group.Controls.Add(child);
            form.Controls.Add(group);
            if (beforeShow) { if (disableGroup) group.Enabled = false; else child.Enabled = false; }
            form.Show();
            if (!beforeShow) { if (disableGroup) group.Enabled = false; else child.Enabled = false; }
            SWF.Application.DoEvents();
            var bmp = new Bitmap(child.Width, child.Height);
            child.DrawToBitmap(bmp, new Rectangle(0, 0, child.Width, child.Height));
            return bmp;
        }

        private static int Differing(Bitmap a, Bitmap b)
        {
            int n = 0;
            for (int y = 0; y < a.Height; y++)
                for (int x = 0; x < a.Width; x++)
                    if (a.GetPixel(x, y).ToArgb() != b.GetPixel(x, y).ToArgb()) n++;
            return n;
        }

        public static TheoryData<string, bool> Cases() => new()
        {
            { "combo", true }, { "combo", false }, { "label", true }, { "label", false },
            { "bound", true }, { "bound", false },
        };

        private sealed class Item { public string ElementText { get; set; } = ""; public string ElementValue { get; set; } = ""; }

        private static SWF.Control Make(string kind)
        {
            if (kind == "bound")
            {
                // As AlarmWorX's configurator: bound to a list, its value chosen after the group was disabled.
                var bound = new SWF.ComboBox { DropDownStyle = SWF.ComboBoxStyle.DropDownList, Width = 200, FormattingEnabled = true };
                bound.DataSource = new[] { new Item { ElementText = "Arial", ElementValue = "a" }, new Item { ElementText = "Segoe UI", ElementValue = "s" } };
                bound.DisplayMember = "ElementText";
                bound.ValueMember = "ElementValue";
                bound.HandleCreated += (s, e) => bound.BeginInvoke(new System.Action(() => bound.SelectedValue = "s"));
                return bound;
            }
            if (kind == "label") return new SWF.Label { Text = "Font:", AutoSize = true };
            var combo = new SWF.ComboBox { DropDownStyle = SWF.ComboBoxStyle.DropDownList, Width = 200 };
            combo.Items.AddRange(new object[] { "Segoe UI", "Arial" });
            combo.SelectedIndex = 0;
            return combo;
        }

        [Theory]
        [MemberData(nameof(Cases))]
        public void AChildOfADisabledGroup_LooksLikeADisabledChild(string kind, bool beforeShow)
        {
            using Bitmap viaGroup = Draw(Make(kind), disableGroup: true, beforeShow);
            using Bitmap itself = Draw(Make(kind), disableGroup: false, beforeShow);
            Assert.Equal(0, Differing(viaGroup, itself));
        }

        /// <summary>Stock .NET 10 on Windows 11, DrawToBitmap of a disabled 200x23 drop-down list
        /// (itself or through its GroupBox): the face is #FAFAFA and the caption GrayText #6D6D6D.</summary>
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void ADisabledDropDownList_HasStocksFaceAndGreyCaption(bool viaGroup)
        {
            using Bitmap bmp = Draw(Make("combo"), disableGroup: viaGroup, beforeShow: false);
            Color face = bmp.GetPixel(bmp.Width / 2 + 40, bmp.Height / 2);
            Color darkest = Color.White;
            for (int y = 3; y < bmp.Height - 3; y++)
                for (int x = 4; x < 60; x++)
                {
                    Color c = bmp.GetPixel(x, y);
                    if (c.R + c.G + c.B < darkest.R + darkest.G + darkest.B) darkest = c;
                }
            Assert.Equal(Color.FromArgb(250, 250, 250).ToArgb(), face.ToArgb());
            Assert.Equal(Color.FromArgb(109, 109, 109).ToArgb(), darkest.ToArgb());
        }
    }
}
