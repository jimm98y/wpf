// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// What a control draws once the keyboard has brought the focus cues out (FocusCueTests covers the cue
// state itself). Tabbing through app's "Configure your Control" dialog, the tree and the check box
// showed where the focus was and two controls did not, where Windows shows it on both:
//   - a tab control draws a focus rectangle inside its selected tab;
//   - a drop-down list (ComboBoxStyle.DropDownList) shows its item in the highlight colours.
//

using System;
using System.Drawing;
using System.Reflection;
using System.Threading;
using Xunit;
using SWF = System.Windows.Forms;

namespace Wpf.WinFormsInterop.Tests
{
    [Collection(ProcessDpiStateCollection.Name)]
    public class FocusCuePaintTests
    {
        private static void OnSta(Action a)
        {
            Exception? failure = null;
            var t = new Thread(() => { try { a(); } catch (Exception ex) { failure = ex; } });
            t.TrySetApartmentState(ApartmentState.STA);
            t.Start();
            t.Join();
            if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }

        // WM_CHANGEUISTATE (UIS_CLEAR, UISF_HIDEFOCUS) at the form: what a Tab press there sends.
        private static void RevealFocusCues(SWF.Form f)
        {
            const int WM_CHANGEUISTATE = 0x0127, UIS_CLEAR = 2, UISF_HIDEFOCUS = 0x1;
            var m = SWF.Message.Create(f.Handle, WM_CHANGEUISTATE, (IntPtr)((UISF_HIDEFOCUS << 16) | UIS_CLEAR), IntPtr.Zero);
            typeof(SWF.Control).GetMethod("WndProc", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(f, new object[] { m });
        }

        private static Bitmap Render(SWF.Control c)
        {
            SWF.Application.DoEvents();
            var bmp = new Bitmap(c.Width, c.Height);
            c.DrawToBitmap(bmp, new Rectangle(0, 0, c.Width, c.Height));
            return bmp;
        }

        private static int Differing(Bitmap a, Bitmap b, Rectangle area)
        {
            int n = 0;
            for (int y = area.Top; y < area.Bottom; y++)
                for (int x = area.Left; x < area.Right; x++)
                    if (a.GetPixel(x, y).ToArgb() != b.GetPixel(x, y).ToArgb()) n++;
            return n;
        }

        private static SWF.Form NewForm()
            => new SWF.Form { StartPosition = SWF.FormStartPosition.Manual, Location = new Point(-3300, 1500), ClientSize = new Size(300, 200), ShowInTaskbar = false };

        [Fact]
        public void AFocusedTabControl_DrawsAFocusRectangleInItsSelectedTab()
        {
            OnSta(() =>
            {
                using SWF.Form form = NewForm();
                var tabs = new SWF.TabControl { Bounds = new Rectangle(10, 10, 200, 120) };
                tabs.TabPages.Add("Appearance");
                tabs.TabPages.Add("Advanced");
                var other = new SWF.Button { Bounds = new Rectangle(10, 150, 80, 24), Text = "OK" };
                form.Controls.Add(tabs);
                form.Controls.Add(other);
                form.Show();
                RevealFocusCues(form);

                other.Focus();
                using Bitmap unfocused = Render(tabs);
                tabs.Focus();
                Assert.True(tabs.Focused);
                using Bitmap focused = Render(tabs);

                Rectangle tab = tabs.GetTabRect(0);
                Assert.True(Differing(unfocused, focused, tab) > 10,
                            "the selected tab has to show the focus once the keyboard has revealed it");
            });
        }

        [Fact]
        public void AFocusedDropDownList_ShowsItsItemHighlighted()
        {
            OnSta(() =>
            {
                using SWF.Form form = NewForm();
                var combo = new SWF.ComboBox { Bounds = new Rectangle(10, 10, 150, 21), DropDownStyle = SWF.ComboBoxStyle.DropDownList };
                combo.Items.AddRange(new object[] { "Hidden", "Visible" });
                combo.SelectedIndex = 0;
                var other = new SWF.Button { Bounds = new Rectangle(10, 150, 80, 24), Text = "OK" };
                form.Controls.Add(combo);
                form.Controls.Add(other);
                form.Show();
                RevealFocusCues(form);

                combo.Focus();
                Assert.True(combo.Focused);
                using Bitmap focused = Render(combo);

                int highlight = 0;
                for (int y = 0; y < focused.Height; y++)
                    for (int x = 0; x < focused.Width; x++)
                        if (focused.GetPixel(x, y).ToArgb() == SystemColors.Highlight.ToArgb()) highlight++;
                Assert.True(highlight > 50, $"a focused drop-down list shows its item highlighted ({highlight} highlight pixels)");
            });
        }
    }
}
