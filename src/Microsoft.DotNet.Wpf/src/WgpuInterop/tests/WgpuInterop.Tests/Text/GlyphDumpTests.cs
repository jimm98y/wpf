// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// A way to LOOK at what the text stack draws, without a window.
//
// Grid fitting is judged by eye against what Windows draws, and the only way to see ours was to
// build the SDK, rebuild the gallery, and put it on the screen -- eight minutes to answer a question
// that changes a constant. Worse, it meant every attempt landed in front of whoever was using the
// application. This renders a run through the real renderer and writes it to a file, so an attempt
// can be compared against a capture of Windows' own in seconds.
//
// Set WPF_GLYPH_DUMP to a directory to turn it on; it is skipped otherwise, so it costs the suite
// nothing.
//

using System;
using System.IO;
using System.Numerics;
using Microsoft.Wpf.Interop.WebGpu.Composition;
using Microsoft.Wpf.Interop.WebGpu.Composition.Text;
using WgpuInterop.Tests.Harness;
using Xunit;

namespace WgpuInterop.Tests.Text
{
    public sealed class GlyphDumpTests : RendererTestBase
    {
        public GlyphDumpTests(GpuFixture gpu) : base(gpu) { }

        [Fact]
        public void DumpTextAtScreenSizes()
        {
            string? dir = Environment.GetEnvironmentVariable("WPF_GLYPH_DUMP");
            Assert.SkipWhen(string.IsNullOrEmpty(dir), "set WPF_GLYPH_DUMP to a directory to dump text");
            Directory.CreateDirectory(dir!);

            // WPF_GLYPH_DUMP_FAMILY picks a face by name and style ("Segoe UI:bold"), so the bold and
            // italic files can be looked at too -- they are separate faces with their own stems and
            // their own blue zones, and fitting them is a separate question from fitting the regular.
            string? want = Environment.GetEnvironmentVariable("WPF_GLYPH_DUMP_FAMILY");
            TrueTypeFont font = TestFonts.Load();
            if (!string.IsNullOrEmpty(want))
            {
                string[] parts = want!.Split(':');
                bool bold = Array.IndexOf(parts, "bold") > 0;
                bool italic = Array.IndexOf(parts, "italic") > 0;
                string? file = FontFiles.Find(parts[0], bold, italic);
                Assert.SkipWhen(file is null, $"no file for {want}");
                font = new TrueTypeFont(File.ReadAllBytes(file!));
            }
            string text = Environment.GetEnvironmentVariable("WPF_GLYPH_DUMP_TEXT") ?? "Button CheckBox loose";
            const int W = 420, H = 26;

            var root = new SceneVisual();
            root.Content.Add(new GlyphRunDraw(text, new Vector2(6f, 18f), 12f,
                                              RgbaColor.FromBytes(0, 0, 0, 255)));

            byte[] px = NewRenderer(font).RenderToRgba(root, W, H, RgbaColor.FromBytes(240, 240, 240, 255));
            string path = Path.Combine(dir!, "ours.png");
            PngWriter.Write(path, px, W, H);
            Assert.True(File.Exists(path));

            // What the fitting decided, in numbers: the lines it anchored to and the width it thinks
            // this face's stems are. Reading those off a picture of the result is guesswork.
            var report = new System.Text.StringBuilder();
            HintMetrics m = GlyphHinter.Measure(font.UnitsPerEmForHinting, c =>
            {
                int gid = font.GlyphIndex(c);
                return gid <= 0 ? null : font.ContoursForHinting(gid);
            });
            report.AppendLine($"em={m.UnitsPerEm} stemX={m.StandardStemX:0.0} ({m.StandardStemX * 12f / m.UnitsPerEm:0.00}px) "
                              + $"stemY={m.StandardStemY:0.0} ({m.StandardStemY * 12f / m.UnitsPerEm:0.00}px)");
            foreach (HintZone z in m.Zones)
                report.AppendLine($"zone top={z.Top} flat={z.Flat:0.0} ({z.Flat * 12f / m.UnitsPerEm:0.00}px -> "
                                  + $"{MathF.Round(z.Flat * 12f / m.UnitsPerEm)}) round={z.Round:0.0}");
            foreach (char probe in Environment.GetEnvironmentVariable("WPF_GLYPH_DUMP_EXPLAIN") ?? "")
            {
                var contours = font.ContoursForHinting(font.GlyphIndex(probe));
                if (contours == null) continue;
                report.AppendLine($"--- '{probe}' at 12ppem");
                report.Append(GlyphHinter.Explain(contours, m, 12f, horizontal: true));
                report.Append(GlyphHinter.Explain(contours, m, 12f, horizontal: false));
            }
            File.WriteAllText(Path.Combine(dir!, "metrics.txt"), report.ToString());
        }
    }
}
