// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The managed font dialog's lists, as Windows' own ChooseFont shows them on a stock Windows 11
// install: a family's weights grouped into its styles (Segoe UI's Light...Black Italic), legacy
// names parsed into family and style ("Arial Rounded MT Bold" is family "Arial Rounded MT"),
// comdlg32's style order (Arial: Narrow before Italic before Regular), simulated faces and the
// system code page's script first. Read off the native dialog; the model is ChooseFontModel.
//

using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using Xunit;

namespace Wpf.WinFormsInterop.Tests
{
    public class FontDialogModelTests
    {
        private static (string name, string[] faces, string[] scripts)[] Families()
        {
            Type family = typeof(FontDialog).Assembly.GetType("System.Windows.Forms.FontDialogFamily", true)!;
            var list = (IList)family.GetMethod("Enumerate", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, new object[] { false })!;
            return list.Cast<object>().Select(f => (
                (string)family.GetField("Name")!.GetValue(f)!,
                ((IList)family.GetField("Faces")!.GetValue(f)!).Cast<object>().Select(x => (string)x.GetType().GetField("Name")!.GetValue(x)!).ToArray(),
                ((IList)family.GetField("Scripts")!.GetValue(f)!).Cast<object>().Select(x => (string)x.GetType().GetField("Name")!.GetValue(x)!).ToArray()))
                .ToArray();
        }

        [Fact]
        public void TheListsAreWindowsOwn()
        {
            if (!OperatingSystem.IsWindows())
                return;   // the expectations are a stock Windows install's fonts
            var all = Families();
            var by = all.ToDictionary(f => f.name);

            Assert.Equal(new[] { "Light", "Light Italic", "Semilight", "Semilight Italic", "Regular", "Italic",
                "Semibold", "Semibold Italic", "Bold", "Bold Italic", "Black", "Black Italic" }, by["Segoe UI"].faces);
            Assert.Equal(new[] { "Narrow", "Narrow Italic", "Italic", "Regular", "Narrow Bold", "Narrow Bold Italic",
                "Bold", "Bold Italic", "Black", "Black Oblique" }, by["Arial"].faces);
            // Simulations fill the missing slots: Impact has one face, Tahoma two.
            Assert.Equal(new[] { "Regular", "Oblique", "Bold", "Bold Oblique" }, by["Impact"].faces);
            Assert.Equal(new[] { "Regular", "Bold", "Oblique", "Bold Oblique" }, by["Tahoma"].faces);
            Assert.Equal(new[] { "Medium", "Medium Oblique", "Bold", "Bold Oblique" }, by["Marlett"].faces);
            Assert.Equal(new[] { "Symbol" }, by["Marlett"].scripts);
            // Weights folded into the typographic family: no "Segoe UI Semibold" entry of its own.
            Assert.DoesNotContain(all, f => f.name == "Segoe UI Semibold" || f.name == "Arial Black");
            // Sorted as the combo box sorts: case-insensitively, by words.
            var names = all.Select(f => f.name).ToList();
            Assert.True(names.IndexOf("Segoe UI") < names.IndexOf("Segoe UI Emoji"));
            Assert.Contains("Western", by["Arial"].scripts);
            Assert.Contains("Cyrillic", by["Arial"].scripts);
        }

        [Fact]
        public void PickingAFaceReturnsIt()
        {
            if (!OperatingSystem.IsWindows())
                return;
            using var dialog = new FontDialog { Font = new System.Drawing.Font("Segoe UI", 9f) };
            const BindingFlags any = BindingFlags.Instance | BindingFlags.NonPublic;
            typeof(FontDialog).GetMethod("RunDialog", any)!.Invoke(dialog, new object[] { IntPtr.Zero });
            var fonts = (ComboBox)typeof(FontDialog).GetField("fontCombo", any)!.GetValue(dialog)!;
            var styles = (ComboBox)typeof(FontDialog).GetField("styleCombo", any)!.GetValue(dialog)!;
            var sizes = (ComboBox)typeof(FontDialog).GetField("sizeCombo", any)!.GetValue(dialog)!;
            Assert.Equal("Segoe UI", fonts.SelectedItem);
            Assert.Equal("Regular", styles.SelectedItem);
            Assert.Equal("9", sizes.SelectedItem);

            styles.SelectedIndex = styles.FindStringExact("Semibold");
            sizes.SelectedIndex = sizes.FindStringExact("12");
            typeof(FontDialog).GetMethod("BuildResult", any)!.Invoke(dialog, null);
            Assert.Equal("Segoe UI Semibold", dialog.Font.Name);
            Assert.Equal(12f, dialog.Font.SizeInPoints);
            Assert.False(dialog.Font.Bold);

            fonts.SelectedIndex = fonts.FindStringExact("Arial");
            styles.SelectedIndex = styles.FindStringExact("Bold Italic");
            typeof(FontDialog).GetMethod("BuildResult", any)!.Invoke(dialog, null);
            Assert.Equal("Arial", dialog.Font.Name);
            Assert.True(dialog.Font.Bold && dialog.Font.Italic);
        }
    }
}
