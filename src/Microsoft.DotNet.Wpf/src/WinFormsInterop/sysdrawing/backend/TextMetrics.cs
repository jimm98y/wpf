// Managed text measurement for the GPU-raster path — no libgdiplus. It shapes the run with the SAME
// font/shaper the WebGPU renderer draws with (so measured width == rendered width, making centred
// button/label text exact) and sums glyph advances. This replaces the libgdiplus MeasureString the
// DrawString alignment used, which is also a prerequisite for the browser (no libgdiplus in-browser).

using System;
using System.Collections.Generic;
using Microsoft.Wpf.Interop.WebGpu.Composition.Text;

namespace System.Drawing.WebGpuBackend
{
    internal static class TextMetrics
    {
        private static readonly IFont Font = LoadFont();
        // KERNING, because Windows kerns a string run and a measurement that does not is a
        // measurement of a different string. Arial's "AVAVAV..." measures 140 pixels unkerned
        // against Windows' 121.
        // WPF_KERN_MEASURE=0 kerns when DRAWING but not when MEASURING, to tell a text difference
        // from a LAYOUT one: a measured width feeds AutoSize, and a control that measures a pixel
        // narrower moves everything laid out after it.
        //
        // MEASURED on the control window. Kerning is worth 40,000 overall (1,807,443 unkerned
        // against 1,767,527), and its effect is UNEVEN: TreeView -57,452, RichTextBox -55,146, the
        // labelled TextBox -55,146, ListView -31,900 and TabControl -21,416, against MonthCalendar
        // +181,142 and ProgressBar +109,550.
        //
        // The ProgressBar has no text at all, and that is the tell: measuring without kerning takes
        // its error from 166,420 to 73,628 and its POSITION component to exactly zero. It was being
        // moved, not drawn wrong -- something laid out after a label whose autosize width changed.
        // Kerning the measurement is still the better setting overall (1,767,527 against 1,776,863
        // for draw-only), so it stays, but the two are separable and the knob is here because the
        // next person will want to separate them again.
        private static readonly ITextShaper Shaper =
            Environment.GetEnvironmentVariable("WPF_KERN_MEASURE") == "0"
                ? new SimpleTextShaper() : (ITextShaper) new KerningTextShaper();
        private static readonly List<ShapedGlyph> Buf = new List<ShapedGlyph>();

        // Width/height of a run at the given pixel em size. Advances are in the font's base pixels
        // (PixelsPerEm), scaled to emPx. Height ~ emPx (a single line).
        public static void Measure(string text, float emPx, out float width, out float height)
            => Measure(text, emPx, 0, null, out width, out height);

        public static void Measure(string text, float emPx, int simulations, out float width, out float height)
            => Measure(text, emPx, simulations, null, out width, out height);

        /// <summary>Measure in a given style. Bold is wider than regular, so measuring everything
        /// with the regular face laid bold text out too tightly and let it overlap what came
        /// next.</summary>
        /// <summary>Measure in a given family and style. Measuring everything with one face made
        /// every layout that depends on text width wrong for every other font: a run measured in a
        /// proportional face and drawn in a fixed-width one does not fit where it was told to go.
        /// </summary>
        public static void Measure(string text, float emPx, int simulations, string family,
                                   out float width, out float height)
        {
            IFont font = FontFor(simulations, family);
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
                    // A whole pixel per glyph, as Windows lays a string out: it advances the pen by
                    // each glyph's width rounded to a pixel, so a run is the SUM of rounded widths and
                    // not the rounded sum. Adding the fractions up first and rounding once made every
                    // run come out a shade narrower than the same run in Windows -- a couple of pixels
                    // over a sentence, which is enough to fit text where Windows clips it.
                    //
                    // And ASK THE FACE first, exactly as the renderer does when it lays the same run
                    // out (WgpuSceneRenderer, TryGetDeviceAdvance). 'hdmx' is the designer's own table
                    // of device widths and it is what GDI uses where it has a row; rounding the scaled
                    // design advance is a third answer that agrees with neither, and measuring one way
                    // while drawing the other put the two out of step with each other as well as with
                    // Windows. Measured against a live stock window at 9pt Segoe UI: T came out 6 where
                    // GDI says 7, x 6 where GDI says 5, C 7 against 8 and m 10 against 11, so "Text"
                    // measured 29 against Windows' 28 and every caption built on it drifted.
                    float scale = emPx / font.PixelsPerEm;
                    var faced = font as IHintedGlyphFont;
                    lineWidth = 0f;
                    foreach (ShapedGlyph g in Buf)
                    {
                        lineWidth += faced is not null
                                     && faced.TryGetDeviceAdvance(g.GlyphId, emPx, out float device)
                                     ? device
                                     : (float)Math.Round(g.Advance * scale);
                        // Separate from the advance, and rounded on the same grid: see ShapedGlyph.
                        if (g.Kern != 0f) lineWidth += (float)Math.Round(g.Kern * scale);
                    }
                }
                if (lineWidth > width) width = lineWidth;
            }
        }

        /// <summary>The ascent and descent GDI reports for this family and style at this pixel
        /// size -- the line box every control that sizes itself to a line of text is measured
        /// against. False when the family cannot be resolved to a face.
        /// <para>The face knows the rule (TrueTypeFont.TryGetGdiLineMetrics); this only finds the
        /// face. Scaling the DESIGN ascent and descent, which is what this used to do through
        /// GDI+, is three pixels short for Arial and for Times New Roman at nine point, and put
        /// every line of them two pixels above Windows' own.</para></summary>
        internal static bool TryGetGdiLineMetrics(string family, bool bold, bool italic, float emPx,
                                                  out int ascent, out int descent)
        {
            ascent = descent = 0;
            int ppem = (int) Math.Round(emPx);
            if (ppem <= 0) return false;
            IFont face = FontFor((bold ? 1 : 0) | (italic ? 2 : 0), family);
            return face is TrueTypeFont ttf && ttf.TryGetGdiLineMetrics(ppem, out ascent, out descent);
        }

        // One instance per family and style. A family that ships a real bold or italic file gets
        // that file; one that does not has the style synthesized from its regular face, which is
        // what the flags on TrueTypeFont do.
        private static readonly Dictionary<string, IFont> Faces = new Dictionary<string, IFont>();

        internal static IFont FontFor(int simulations, string family)
        {
            int i = simulations & 3;
            bool bold = (i & 1) != 0, italic = (i & 2) != 0;
            if (string.IsNullOrEmpty(family))
                return i == 0 ? Font : StyledDefault(i);

            string key = family + "|" + i;
            lock (Faces)
            {
                if (Faces.TryGetValue(key, out IFont cached)) return cached;

                IFont made = null;
                string path = FontFiles.Find(family, bold, italic);
                if (path != null)
                {
                    bool styledFile = FontFiles.HasStyledFile(family, bold, italic);
                    try
                    {
                        made = new TrueTypeFont(System.IO.File.ReadAllBytes(path),
                                                bold && !styledFile, italic && !styledFile);
                    }
                    catch (Exception)
                    {
                        made = null;      // an unreadable or unsupported file is not fatal
                    }
                }
                Faces[key] = made ??= (i == 0 ? Font : StyledDefault(i));
                return made;
            }
        }

        private static readonly IFont[] Styled = new IFont[4];

        private static IFont StyledDefault(int i)
        {
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
