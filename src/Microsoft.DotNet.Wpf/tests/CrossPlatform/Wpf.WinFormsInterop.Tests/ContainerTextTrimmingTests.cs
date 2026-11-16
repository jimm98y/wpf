// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// A string trimmed to its rectangle is trimmed inside a Graphics container too. On a window's Graphics
// the GDI+ layout that trims ("Explorer" in a narrow tab: "Ex...") was taken only outside containers;
// inside one the string went to an older path that drew it whole, so a tab strip painting its captions
// in a container showed them full width, over each other.
//

using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.WebGpuBackend;
using Xunit;

namespace Wpf.WinFormsInterop.Tests
{
    [Collection(ProcessDpiStateCollection.Name)]
    public class ContainerTextTrimmingTests
    {
        private static StringFormat TabFormat()
        {
            var sf = (StringFormat)StringFormat.GenericTypographic.Clone();
            sf.Alignment = StringAlignment.Center;
            sf.LineAlignment = StringAlignment.Center;
            sf.Trimming = StringTrimming.EllipsisCharacter;
            sf.FormatFlags |= StringFormatFlags.NoWrap;
            return sf;
        }

        [Theory]
        [InlineData(96f)]
        [InlineData(192f)]
        public void InAContainerAStringIsTrimmedAsOutsideOne(float dpi)
        {
            float was = DisplayDpi.X;
            DisplayDpi.Set(dpi);
            try
            {
                using var font = new Font("Segoe UI", 9f);
                using StringFormat sf = TabFormat();
                float k = dpi / 96f;
                var rect = new RectangleF(10, 2, 22 * k, 16 * k);

                string outside = Runs(g => g.DrawString("Explorer", font, Brushes.Black, new RectangleF(rect.X + 20, rect.Y + 5, rect.Width, rect.Height), sf));
                string inside = Runs(g =>
                {
                    g.TranslateTransform(20, 5);
                    var c = g.BeginContainer();
                    g.DrawString("Explorer", font, Brushes.Black, rect, sf);
                    g.EndContainer(c);
                });
                Assert.Equal(outside, inside);
                // laid out by GDI+ and trimmed: the visible part and the ellipsis, inside the tab
                Assert.DoesNotContain("GlyphRunDraw", inside);
                Assert.Equal(2, inside.Split('|').Length);
            }
            finally { DisplayDpi.Set(was); }
        }

        /// <summary>Each recorded text run, with where its ink lies.</summary>
        private static string Runs(Action<Graphics> draw)
        {
            Graphics g = GpuRaster.NewRecording();
            draw(g);
            object scene = GpuRaster.EndScene(g);
            var rows = new List<string>();
            Collect(scene, rows);
            return string.Join(" | ", rows);
        }

        private static void Collect(object visual, List<string> rows)
        {
            foreach (object p in (IEnumerable)visual.GetType().GetProperty("Content")!.GetValue(visual)!)
            {
                string name = p.GetType().Name;
                if (name == "GdiPlusTextDraw")
                {
                    object run = p.GetType().GetProperty("Run")!.GetValue(p)!;
                    object? pre = run.GetType().GetField("Pre")!.GetValue(run);
                    rows.Add(pre == null ? name : $"{name} {pre.GetType().GetField("Left")!.GetValue(pre)}+{pre.GetType().GetField("Width")!.GetValue(pre)}");
                }
                else if (name.Contains("Glyph") || name.Contains("Text"))
                    rows.Add($"{name} '{p.GetType().GetProperty("Text")?.GetValue(p)}'");
            }
            foreach (object child in (IEnumerable)visual.GetType().GetProperty("Children")!.GetValue(visual)!)
                Collect(child, rows);
        }
    }
}
