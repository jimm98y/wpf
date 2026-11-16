// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// GDI+ lays a string out in a box shorter than a line: a word too wide for the box breaks at a
// character, and the line after it is laid out only while a quarter of a line is left (the default
// character trimming); so a button too small for "Cancel" shows one line, as Windows draws it. On a
// window's Graphics (a recording) both lines were drawn, centred about the box: the top of the first
// cut off by the button's edge and the second showing under it.
//

using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.WebGpuBackend;
using Xunit;

namespace Wpf.WinFormsInterop.Tests
{
    public class GdiPlusShortBoxTests
    {
        private static (Font font, StringFormat format, RectangleF box) Setup(Graphics g)
        {
            var font = new Font("Microsoft Sans Serif", 20f, FontStyle.Regular, GraphicsUnit.Pixel);
            var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            SizeF whole = g.MeasureString("Cancel", font);
            SizeF canc = g.MeasureString("Canc", font);
            return (font, format, new RectangleF(0, 0, (canc.Width + whole.Width) / 2, whole.Height * 0.8f));
        }

        [Fact]
        public void AWordTooWideForAShortBoxIsOneLine()
        {
            using var bmp = new Bitmap(10, 10);
            using var g = Graphics.FromImage(bmp);
            var (font, format, box) = Setup(g);
            g.MeasureString("Cancel", font, box.Size, format, out int chars, out int lines);
            Assert.Equal(1, lines);
            Assert.True(chars < 6, $"{chars} characters fitted");
        }

        [Fact]
        public void OnAWindowsGraphicsItIsOneLineToo()
        {
            Graphics g = GpuRaster.NewRecording();
            var (font, format, box) = Setup(g);
            g.DrawString("Cancel", font, Brushes.Black, box, format);
            object scene = GpuRaster.EndScene(g);
            var runs = new List<string>();
            Collect(scene, runs);
            Assert.True(runs.Count == 1, "runs: " + string.Join(" | ", runs));
        }

        [Fact]
        public void TextRendererOnAWindowsGraphicsDrawsOneLine()
        {
            Graphics g = GpuRaster.NewRecording();
            var (font, _, box) = Setup(g);
            var flags = System.Windows.Forms.TextFormatFlags.TextBoxControl | System.Windows.Forms.TextFormatFlags.WordBreak
                      | System.Windows.Forms.TextFormatFlags.HorizontalCenter | System.Windows.Forms.TextFormatFlags.VerticalCenter;
            System.Windows.Forms.TextRenderer.DrawText(g, "Cancel", font, Rectangle.Round(box), Color.Black, flags);
            object scene = GpuRaster.EndScene(g);
            var runs = new List<string>();
            Collect(scene, runs);
            Assert.True(runs.Count == 1, "runs: " + string.Join(" | ", runs));
        }

        [Fact]
        public void AButtonsCompatibleTextIsLaidOutByGdiPlusOnItsMemorySurface()
        {
            // the button's paint Graphics stands for a memory DC (ButtonBase.GdiTextOnMemorySurface)
            using var button = new System.Windows.Forms.Button { Text = "&Cancel", UseCompatibleTextRendering = true, Size = new Size(75, 23) };
            button.Font = new Font("Microsoft Sans Serif", 20f, FontStyle.Regular, GraphicsUnit.Pixel);
            Graphics g = GpuRaster.NewRecording();
            typeof(Graphics).GetField("memory_surface_text", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(g, true);
            using (var pe = new System.Windows.Forms.PaintEventArgs(g, new Rectangle(0, 0, 75, 23)))
                typeof(System.Windows.Forms.Control).GetMethod("OnPaint", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(button, new object[] { pe });
            object scene = GpuRaster.EndScene(g);
            var runs = new List<string>();
            Collect(scene, runs);
            Assert.True(runs.Count == 1 && runs[0].StartsWith("GdiPlusTextDraw"), "runs: " + string.Join(" | ", runs));
            Assert.True((bool)typeof(Graphics).GetField("memory_surface_text", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(g), "the flag is the button's again after its text");
        }

        private static void Collect(object visual, List<string> runs)
        {
            foreach (object p in (IEnumerable)visual.GetType().GetProperty("Content").GetValue(visual))
            {
                string name = p.GetType().Name;
                if (!name.Contains("Text") && !name.Contains("Glyph")) continue;
                string text = p.GetType().GetProperty("Text")?.GetValue(p) as string
                              ?? p.GetType().GetField("Text")?.GetValue(p) as string ?? "?";
                runs.Add($"{name} '{text}'");
            }
            foreach (object child in (IEnumerable)visual.GetType().GetProperty("Children").GetValue(visual))
                Collect(child, runs);
        }
    }
}
