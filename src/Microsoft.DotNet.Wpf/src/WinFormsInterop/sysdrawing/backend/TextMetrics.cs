// Managed text measurement for the GPU-raster path — no libgdiplus. It shapes the run with the SAME
// font/shaper the WebGPU renderer draws with (so measured width == rendered width, making centred
// button/label text exact) and sums glyph advances. This replaces the libgdiplus MeasureString the
// DrawString alignment used, which is also a prerequisite for the browser (no libgdiplus in-browser).

using System.Collections.Generic;
using Microsoft.Wpf.Interop.WebGpu.Composition.Text;

namespace System.Drawing.WebGpuBackend
{
    internal static class TextMetrics
    {
        private static readonly IFont Font = LoadFont();
        private static readonly ITextShaper Shaper = new SimpleTextShaper();
        private static readonly List<ShapedGlyph> Buf = new List<ShapedGlyph>();

        // Width/height of a run at the given pixel em size. Advances are in the font's base pixels
        // (PixelsPerEm), scaled to emPx. Height ~ emPx (a single line).
        public static void Measure(string text, float emPx, out float width, out float height)
            => Measure(text, emPx, 0, out width, out height);

        /// <summary>Measure in a given style. Bold is wider than regular, so measuring everything
        /// with the regular face laid bold text out too tightly and let it overlap what came
        /// next.</summary>
        public static void Measure(string text, float emPx, int simulations, out float width, out float height)
        {
            IFont font = FontFor(simulations);
            height = emPx;
            width = 0f;
            if (string.IsNullOrEmpty(text)) return;

            // Multi-line text arrives here as one string. Shaping it whole turned each newline into
            // a glyph and made the width the sum of EVERY line: a 24-line exception message measured
            // about 15000px wide, past the GPU's maximum texture dimension, and the window it was in
            // presented nothing at all. Measure line by line -- widest line, one line height each.
            string[] lines = text.Split('\n');
            height = emPx * lines.Length;
            foreach (string raw in lines)
            {
                string line = raw.TrimEnd('\r');
                if (line.Length == 0) continue;
                float lineWidth;
                lock (Buf)
                {
                    Shaper.Shape((IShapingFont)font, line, Buf);
                    float baseWidth = 0f;
                    foreach (ShapedGlyph g in Buf) baseWidth += g.Advance;
                    lineWidth = baseWidth * emPx / font.PixelsPerEm;
                }
                if (lineWidth > width) width = lineWidth;
            }
        }

        // One instance per style, built from the same file: the font stack synthesizes bold and
        // oblique rather than needing a separate file for each.
        private static readonly IFont[] Styled = new IFont[4];

        private static IFont FontFor(int simulations)
        {
            int i = simulations & 3;
            if (i == 0) return Font;
            lock (Styled)
            {
                return Styled[i] ??= LoadFont((i & 1) != 0, (i & 2) != 0) ?? Font;
            }
        }

        private static IFont LoadFont(bool bold, bool oblique)
        {
            foreach (string p in new[] { "/System/Library/Fonts/Supplemental/Arial.ttf", "/Library/Fonts/Arial.ttf",
                                         "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf",
                                         "C:\\Windows\\Fonts\\arial.ttf",
                                         "/fonts/Arial.ttf", "/fonts/LiberationSans-Regular.ttf" })
                if (System.IO.File.Exists(p))
                    return new TrueTypeFont(System.IO.File.ReadAllBytes(p), bold, oblique);
            return null;
        }

        private static IFont LoadFont()
        {
            foreach (string p in new[] { "/System/Library/Fonts/Supplemental/Arial.ttf", "/Library/Fonts/Arial.ttf",
                                         "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf",
                                         "C:\\Windows\\Fonts\\arial.ttf",
                                         "/fonts/Arial.ttf", "/fonts/LiberationSans-Regular.ttf" })   // browser VFS
                if (System.IO.File.Exists(p)) return new TrueTypeFont(System.IO.File.ReadAllBytes(p));
            return new BuiltinBitmapFont();
        }
    }
}
