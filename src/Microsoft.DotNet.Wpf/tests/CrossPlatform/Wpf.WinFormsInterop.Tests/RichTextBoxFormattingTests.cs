// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Formatting applied to a RichTextBox's selection -- SelectionFont, SelectionColor -- has to survive
// the control joining a form and getting its handle. Designer code applies it before either.
//

using System.Drawing;
using System.Windows.Forms;
using Xunit;

namespace Wpf.WinFormsInterop.Tests
{
    public class RichTextBoxFormattingTests
    {
        private static RichTextBox Formatted()
        {
            var rich = new RichTextBox { Size = new Size(194, 80), Text = "Rich text: bold, red, big." };
            rich.Select(11, 4); rich.SelectionFont = new Font(rich.Font, FontStyle.Bold);
            rich.Select(17, 3); rich.SelectionColor = Color.Red;
            rich.Select(22, 3); rich.SelectionFont = new Font(rich.Font.FontFamily, 14f);
            rich.Select(0, 0);
            return rich;
        }

        private static void AssertFormatted(RichTextBox rich, string when)
        {
            rich.Select(11, 4);
            Assert.True(rich.SelectionFont != null && rich.SelectionFont.Bold, $"bold lost {when}");
            rich.Select(17, 3);
            Assert.True(rich.SelectionColor.ToArgb() == Color.Red.ToArgb(), $"red lost {when}: {rich.SelectionColor}");
            rich.Select(22, 3);
            Assert.True(rich.SelectionFont != null && rich.SelectionFont.Size == 14f, $"size lost {when}: {rich.SelectionFont?.Size}");
            rich.Select(0, 0);
        }

        [Fact]
        public void SelectionFormatting_IsKept_BeforeTheHandle()
        {
            using var rich = Formatted();
            AssertFormatted(rich, "before the handle");
        }

        [Fact]
        public void SelectionFormatting_SurvivesTheHandle()
        {
            using var rich = Formatted();
            _ = rich.Handle;
            AssertFormatted(rich, "after the handle");
        }

        [Fact]
        public void SelectionFormatting_SurvivesJoiningAForm()
        {
            using var form = new Form { Font = new Font("Segoe UI", 9f) };
            var rich = Formatted();
            form.Controls.Add(rich);
            AssertFormatted(rich, "after joining a form");
            _ = rich.Handle;
            AssertFormatted(rich, "after the handle");
        }
    }
}
