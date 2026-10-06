// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// WinForms printing without GDI+: a PrintDocument's pages are recorded through the managed Graphics
// and then either written as a vector PDF (every head but Windows) or replayed as GDI on the printer
// DC (Windows). These drive the PDF half directly -- it runs anywhere -- and read the file back, the
// way Wpf.Printing.Tests reads WPF's: what matters is that a reader can open it, that the text is
// text in an embedded font a reader can map back to characters, and that the geometry lands where
// the page's units put it.
//

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Printing;
using System.Globalization;
using System.IO;
using Wpf.Printing.Tests;
using Xunit;

namespace Wpf.WinFormsInterop.Tests
{
    public class PrintingTests
    {
        private static PdfDocument PrintToPdf(Action<PrintPageEventArgs> draw, int pages = 1, Action<PrintDocument> setup = null)
        {
            var doc = new PrintDocument { DocumentName = "test" };
            // Paper the test owns, so nothing depends on the machine's default printer.
            doc.DefaultPageSettings.PaperSize = new PaperSize("Letter", 850, 1100);
            setup?.Invoke(doc);
            int page = 0;
            doc.PrintPage += (s, e) =>
            {
                draw(e);
                e.HasMorePages = ++page < pages;
            };
            using var buffer = new MemoryStream();
            doc.PrintController = new PdfPrintController(buffer);
            doc.Print();
            return PdfDocument.Parse(buffer.ToArray());
        }

        private static string Content(PdfDocument pdf, int page = 0)
            => pdf.StreamText(pdf.Pages()[page]["Contents"]);

        // The content stream's lines that end in an operator, split into their operands and it.
        private static IEnumerable<string[]> Operators(string content, string op)
        {
            foreach (string line in content.Split('\n'))
                if (line.EndsWith(" " + op, StringComparison.Ordinal))
                    yield return line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        }

        private static double[] Numbers(string s)
        {
            var list = new List<double>();
            foreach (string t in s.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                list.Add(double.Parse(t, CultureInfo.InvariantCulture));
            return list.ToArray();
        }

        [Fact]
        public void APageIsAPdfPageOfThePaperSize()
        {
            PdfDocument pdf = PrintToPdf(e => e.Graphics.FillRectangle(Brushes.Red, 100, 100, 200, 50), pages: 2);

            Assert.StartsWith("%PDF-", pdf.Header);
            Assert.Equal(2, pdf.Pages().Count);
            var box = (List<object>)pdf.Resolve(pdf.Pages()[0]["MediaBox"]);
            // 8.5 x 11 inches, in points.
            Assert.Equal(612.0, Convert.ToDouble(box[2], CultureInfo.InvariantCulture), 3);
            Assert.Equal(792.0, Convert.ToDouble(box[3], CultureInfo.InvariantCulture), 3);
        }

        [Fact]
        public void GeometryLandsWhereThePageUnitsPutIt()
        {
            string content = Content(PrintToPdf(e =>
            {
                Graphics g = e.Graphics;
                // Display units: a hundredth of an inch.
                g.FillRectangle(Brushes.Red, 100, 200, 300, 50);
                // Millimetres: 10mm is 39.37 hundredths of an inch.
                g.PageUnit = GraphicsUnit.Millimeter;
                g.FillRectangle(Brushes.Blue, 10, 10, 20, 5);
            }));

            // The page starts by turning hundredths of an inch, y down, into points, y up.
            Assert.StartsWith("0.72 0 0 -0.72 0 792 cm", content, StringComparison.Ordinal);
            Assert.Contains("1 0 0 rg", content, StringComparison.Ordinal);
            Assert.Contains("100 200 m", content, StringComparison.Ordinal);
            Assert.Contains("400 250 l", content, StringComparison.Ordinal);

            // The millimetre rectangle went through a page transform of 100/25.4 per unit.
            double[] scale = null;
            foreach (string[] op in Operators(content, "cm"))
                if (op.Length == 7 && op[1] == "0" && op[2] == "0" && op[4] == "0" && op[5] == "0" && op[0] != "0.72")
                    scale = Numbers(string.Join(' ', op, 0, 6));
            Assert.True(scale != null, "no page-unit transform in:\n" + content);
            Assert.Equal(100 / 25.4, scale[0], 3);
            Assert.Contains("10 10 m", content, StringComparison.Ordinal);
        }

        [Fact]
        public void TextIsTextInAnEmbeddedFontThatMapsBackToItsCharacters()
        {
            PdfDocument pdf = PrintToPdf(e =>
            {
                using var font = new Font("Arial", 12);
                e.Graphics.DrawString("Hello PDF", font, Brushes.Black, 100, 100);
            });
            string content = Content(pdf);

            Assert.Contains("BT", content, StringComparison.Ordinal);
            Assert.Contains("Tj", content, StringComparison.Ordinal);

            // The first glyph's text matrix: x past GDI+'s em/6 leading margin, y on the baseline
            // the face's ascent puts under the top of the layout. 12pt is 16.67 hundredths of an inch.
            string[] tm = null;
            foreach (string[] op in Operators(content, "Tj"))
                if (op.Length == 9 && op[6] == "Tm") { tm = op; break; }
            Assert.True(tm != null, "no positioned glyph in:\n" + content);
            double x = double.Parse(tm[4], CultureInfo.InvariantCulture);
            double y = double.Parse(tm[5], CultureInfo.InvariantCulture);
            string firstGlyph = tm[7].Trim('<', '>');
            double em = 12 * 100 / 72.0;
            Assert.InRange(x, 100 + em / 6 - 0.5, 100 + em / 6 + 0.5);
            Assert.InRange(y, 100 + em * 1854 / 2048 - 0.5, 100 + em * 1854 / 2048 + 0.5);

            // The font: a Type0 over an embedded TrueType file, with a ToUnicode map.
            var resources = (PdfDictionary)pdf.Resolve(pdf.Pages()[0]["Resources"]);
            var fonts = (PdfDictionary)pdf.Resolve(resources["Font"]);
            var type0 = (PdfDictionary)pdf.Resolve(Assert.Single(fonts).Value);
            Assert.Equal("Type0", type0["Subtype"]);
            Assert.Equal("Identity-H", type0["Encoding"]);
            var cid = (PdfDictionary)pdf.Resolve(((List<object>)pdf.Resolve(type0["DescendantFonts"]))[0]);
            var descriptor = (PdfDictionary)pdf.Resolve(cid["FontDescriptor"]);
            byte[] file = pdf.StreamData(descriptor["FontFile2"]);
            Assert.True(file != null && file.Length > 1000, "the face is not embedded");
            // A subset of the glyphs drawn, named as one: Arial whole is a megabyte.
            Assert.True(file.Length < 200_000, $"the embedded face is {file.Length} bytes: not a subset");
            Assert.Matches(@"^[A-Z]{6}\+Arial", (string)type0["BaseFont"]);

            string cmap = pdf.StreamText(type0["ToUnicode"]);
            Assert.Contains("beginbfchar", cmap, StringComparison.Ordinal);
            // The first glyph is the 'H' of Hello.
            Assert.Contains("<" + firstGlyph + "> <0048>", cmap, StringComparison.Ordinal);
            foreach (char c in "elo PDF".Replace(" ", ""))
                Assert.Contains(((int)c).ToString("X4") + ">", cmap, StringComparison.Ordinal);
        }

        [Fact]
        public void StrokesCurvesGradientsImagesAndClipsStayVector()
        {
            using var bitmap = new Bitmap(4, 3);
            for (int i = 0; i < 4; i++) bitmap.SetPixel(i, 1, Color.Green);
            PdfDocument pdf = PrintToPdf(e =>
            {
                Graphics g = e.Graphics;
                using (var pen = new Pen(Color.Blue, 3) { LineJoin = LineJoin.Round })
                    g.DrawRectangle(pen, 50, 60, 100, 40);
                g.FillEllipse(Brushes.Green, 200, 60, 80, 40);
                using (var grad = new LinearGradientBrush(new Rectangle(50, 150, 200, 50), Color.Yellow, Color.Navy, LinearGradientMode.Horizontal))
                    g.FillRectangle(grad, 50, 150, 200, 50);
                g.DrawImage(bitmap, 300, 150, 40, 30);
                g.SetClip(new Rectangle(50, 250, 100, 30));
                g.FillEllipse(Brushes.Red, 30, 230, 140, 80);
                g.ResetClip();
            });
            string content = Content(pdf);

            // A stroke with its pen: width, round join, stroked.
            Assert.Contains("3 w", content, StringComparison.Ordinal);
            Assert.Contains("1 j", content, StringComparison.Ordinal);
            Assert.Matches(@"50 60 m\n150 60 l\n150 100 l\n50 100 l\nh\nS", content);
            // An ellipse is Beziers, not a polygon.
            Assert.Contains(" c\n", content, StringComparison.Ordinal);
            // A gradient is a shading, not bands.
            Assert.Contains("/Sh0 sh", content, StringComparison.Ordinal);
            var resources = (PdfDictionary)pdf.Resolve(pdf.Pages()[0]["Resources"]);
            var shading = (PdfDictionary)pdf.Resolve(((PdfDictionary)pdf.Resolve(resources["Shading"]))["Sh0"]);
            Assert.Equal(2, Convert.ToInt32(shading["ShadingType"], CultureInfo.InvariantCulture));
            // An image is an image XObject in its rectangle.
            Assert.Contains("/Im0 Do", content, StringComparison.Ordinal);
            var image = (PdfDictionary)((PdfStream)pdf.Resolve(((PdfDictionary)pdf.Resolve(resources["XObject"]))["Im0"])).Dictionary;
            Assert.Equal(4, Convert.ToInt32(image["Width"], CultureInfo.InvariantCulture));
            Assert.Matches(@"40 0 0 -30 300 180 cm /Im0 Do", content);
            // The clip, and everything drawn after it outside it.
            Assert.Contains("50 250 100 30 re W n", content, StringComparison.Ordinal);

            // Balanced graphics state, or readers reject the file.
            int q = 0, Q = 0;
            foreach (string line in content.Split('\n')) { if (line == "q") q++; else if (line == "Q") Q++; }
            Assert.Equal(q, Q);
        }

        // The page's image XObjects: name -> (width, height, RGB bytes).
        private static Dictionary<string, (int W, int H, byte[] Rgb)> Images(PdfDocument pdf)
        {
            var images = new Dictionary<string, (int, int, byte[])>();
            var resources = (PdfDictionary)pdf.Resolve(pdf.Pages()[0]["Resources"]);
            if (!resources.ContainsKey("XObject")) return images;
            foreach (KeyValuePair<string, object> x in (PdfDictionary)pdf.Resolve(resources["XObject"]))
            {
                var dict = ((PdfStream)pdf.Resolve(x.Value)).Dictionary;
                images[x.Key] = (Convert.ToInt32(dict["Width"], CultureInfo.InvariantCulture),
                                 Convert.ToInt32(dict["Height"], CultureInfo.InvariantCulture), pdf.StreamData(x.Value));
            }
            return images;
        }

        // What these measure was measured on Microsoft Print to PDF at 600 dpi; the PDF writer takes
        // the printer's resolution, so the exact numbers hold only there.
        private static PdfDocument PrintAt600(Action<Graphics> draw)
        {
            float dpi = 0;
            PdfDocument pdf = PrintToPdf(e => { dpi = e.Graphics.DpiX; draw(e.Graphics); });
            Assert.SkipUnless(dpi == 600, $"the printer's resolution is {dpi} dpi, not the 600 the reference was measured at");
            return pdf;
        }

        [Fact]
        public void CentredWrappedLinesStartWhereGdiPlusPutsThem()
        {
            // GDI+'s FullTextImager lays a wrapped string out: a centred line's trailing space hangs
            // past its end instead of being centred with it. The line starts are stock .NET's,
            // printed to Microsoft Print to PDF (47.52, 63.72 and 79.32 points); they used to sit
            // 5 points to the left.
            string content = Content(PrintAt600(g =>
            {
                using var arial = new Font("Arial", 18);
                using var fmt = new StringFormat { Alignment = StringAlignment.Center };
                g.DrawString("A paragraph long enough to wrap across several lines inside its layout rectangle, centred.",
                             arial, Brushes.Black, new RectangleF(50, 40, 400, 200), fmt);
            }));
            var starts = new List<(double X, double Y)>();
            foreach (string[] op in Operators(content, "Tj"))
            {
                if (op.Length != 9 || op[6] != "Tm") continue;
                double x = double.Parse(op[4], CultureInfo.InvariantCulture), y = double.Parse(op[5], CultureInfo.InvariantCulture);
                if (starts.Count == 0 || Math.Abs(starts[^1].Y - y) > 1) starts.Add((x, y));
            }
            Assert.Equal(3, starts.Count);
            Assert.InRange(starts[0].X, 66.0 - 0.2, 66.0 + 0.2);
            Assert.InRange(starts[1].X, 88.5 - 0.2, 88.5 + 0.2);
            Assert.InRange(starts[2].X, 110.17 - 0.2, 110.17 + 0.2);
        }

        [Fact]
        public void AHatchPrintsAsGdiPlusBandsItOnAHundredDpiGrid()
        {
            // DriverPrint rasterizes a hatch: the fill's bounds on a grid of dpi / 100 device pixels,
            // one pattern bit per cell, a DIB of whole bands, clipped to the shape -- bit-exact with
            // what gdiplus.dll hands StretchDIBits for this rectangle (152 x 122, from 294,294).
            PdfDocument pdf = PrintAt600(g =>
            {
                using var hb = new HatchBrush(HatchStyle.Cross, Color.DarkRed, Color.LightYellow);
                g.FillRectangle(hb, 50, 50, 150, 120);
            });
            var image = Assert.Single(Images(pdf)).Value;
            Assert.Equal(152, image.W);
            Assert.Equal(122, image.H);
            Assert.DoesNotContain("/Pattern", Content(pdf), StringComparison.Ordinal);
            // Cross: a line every eight cells, the pattern on the device origin. DIB column 0 is
            // device cell 49, so column 7 (cell 56) is a line and column 4 is not.
            (byte R, byte G, byte B) Px(int x, int y) => (image.Rgb[(y * image.W + x) * 3], image.Rgb[(y * image.W + x) * 3 + 1], image.Rgb[(y * image.W + x) * 3 + 2]);
            Assert.Equal((Color.DarkRed.R, Color.DarkRed.G, Color.DarkRed.B), Px(7, 3));
            Assert.Equal((Color.LightYellow.R, Color.LightYellow.G, Color.LightYellow.B), Px(4, 3));
        }

        [Fact]
        public void APathGradientPrintsAsGdiPlusBitmap()
        {
            // PrivateFillRect: the device bounds (1801 x 1201) into a bitmap of (w - 256) / 5 + 256
            // a side, the surround colour carried 5% past the rim, transparent black beyond it --
            // which a printer prints black. The centre is the centre colour.
            PdfDocument pdf = PrintAt600(g =>
            {
                using var path = new GraphicsPath();
                path.AddEllipse(30, 280, 340, 240);
                using var pgb = new PathGradientBrush(path) { CenterColor = Color.White, SurroundColors = new[] { Color.Navy } };
                g.FillRectangle(pgb, 0, 250, 400, 300);
            });
            var image = Assert.Single(Images(pdf)).Value;
            Assert.Equal(GdiPlusSide(2404), image.W);
            Assert.Equal(GdiPlusSide(1804), image.H);
            int c = (image.H / 2 * image.W + image.W / 2) * 3;
            Assert.True(image.Rgb[c] > 225 && image.Rgb[c + 1] > 225 && image.Rgb[c + 2] > 225,
                        $"the centre is {image.Rgb[c]},{image.Rgb[c + 1]},{image.Rgb[c + 2]}, not the centre colour ({image.Rgb.Length} bytes for {image.W}x{image.H})");
            Assert.Equal(new byte[] { 0, 0, 0 }, image.Rgb[0..3]);
        }

        private static int GdiPlusSide(int w) => Math.Min(Math.Min((w - 256) / 5 + 256, w), 1024);

        [Fact]
        public void ATurnedImagePrintsAsGdiPlusDrawsIt()
        {
            // A 30-degree turn: drawn into a DIB of 100 dpi (a small image's cap), the parallelogram's
            // bounds -- 159 x 148 cells, as gdiplus.dll's StretchDIBits had it -- clipped to the image.
            using var bitmap = new Bitmap(64, 48);
            for (int y = 0; y < 48; y++)
                for (int x = 0; x < 64; x++)
                    bitmap.SetPixel(x, y, (x / 16 + y / 16) % 2 == 0 ? Color.FromArgb(200, 40, 40) : Color.FromArgb(40, 90, 200));
            PdfDocument pdf = PrintAt600(g =>
            {
                g.TranslateTransform(350, 100);
                g.RotateTransform(30);
                g.DrawImage(bitmap, 0, 0, 128, 96);
            });
            var image = Assert.Single(Images(pdf)).Value;
            Assert.Equal(159, image.W);
            Assert.Equal(148, image.H);
            string content = Content(pdf);
            Assert.Contains("W n", content, StringComparison.Ordinal);
        }

        // ---- what DriverPrint rasterizes: textures, translucency, pens, rims ------------------------
        //
        // Each of these was compared bit for bit with what gdiplus.dll hands StretchDIBits printing the
        // same page to Microsoft Print to PDF at 600 dpi (a hook on its import table); these check the
        // PDF carries the same DIBs.

        private static Bitmap Tile(int w, int h, int alpha)
        {
            var b = new Bitmap(w, h);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    b.SetPixel(x, y, Color.FromArgb(alpha, 30 + x * 200 / w, 220 - y * 180 / h, (x + y) % 2 == 0 ? 60 : 160));
            return b;
        }

        // An image XObject's explicit stencil: width, height, rows of (w + 7) / 8 bytes, set = painted.
        private static (int W, int H, byte[] Bits) Stencil(PdfDocument pdf, string image)
        {
            var resources = (PdfDictionary)pdf.Resolve(pdf.Pages()[0]["Resources"]);
            var dict = ((PdfStream)pdf.Resolve(((PdfDictionary)pdf.Resolve(resources["XObject"]))[image])).Dictionary;
            Assert.True(dict.ContainsKey("Mask"), image + " has no /Mask");
            object maskRef = dict["Mask"];
            var mask = ((PdfStream)pdf.Resolve(maskRef)).Dictionary;
            Assert.Equal(true, mask["ImageMask"]);
            return (Convert.ToInt32(mask["Width"], CultureInfo.InvariantCulture), Convert.ToInt32(mask["Height"], CultureInfo.InvariantCulture),
                    pdf.StreamData(maskRef));
        }

        private static bool Bit((int W, int H, byte[] Bits) m, int x, int y) => (m.Bits[y * ((m.W + 7) / 8) + (x >> 3)] & (0x80 >> (x & 7))) != 0;

        [Fact]
        public void ATextureBrushPrintsOneDibPixelATexel()
        {
            // A texel is a hundredth of an inch, six device pixels: the bounds on a grid of six
            // (202 x 152 from cell 49), each DIB pixel the texel the cell is.
            using Bitmap tile = Tile(8, 6, 255);
            PdfDocument pdf = PrintAt600(g =>
            {
                using var tb = new TextureBrush(tile);
                g.FillRectangle(tb, 50, 50, 200, 150);
            });
            var image = Assert.Single(Images(pdf)).Value;
            Assert.Equal(202, image.W);
            Assert.Equal(152, image.H);
            foreach ((int i, int j) in new[] { (1, 1), (10, 7), (100, 50), (150, 120) })
            {
                Color c = tile.GetPixel((49 + i) % 8, (49 + j) % 6);
                int o = (j * image.W + i) * 3;
                Assert.InRange(image.Rgb[o], c.R - 1, c.R + 1);
                Assert.InRange(image.Rgb[o + 1], c.G - 1, c.G + 1);
                Assert.InRange(image.Rgb[o + 2], c.B - 1, c.B + 1);
            }
        }

        [Fact]
        public void ATranslucentHatchIsHalftonedAsGdiPlusDoesIt()
        {
            // A printer cannot blend: GDI+ dithers the alpha into a 1bpp mask at the device's
            // resolution, HT_16x16[(y + n) % 16][x % 16] < alpha, n the DIBs made so far in the
            // process. The colour DIB is the hatch unpremultiplied (the clear back black); the mask
            // is set only on the fore colour's cells, and there exactly where the dither says.
            PdfDocument pdf = PrintAt600(g =>
            {
                using var hb = new HatchBrush(HatchStyle.Cross, Color.FromArgb(128, Color.DarkRed), Color.FromArgb(0, Color.White));
                g.FillRectangle(hb, 50, 50, 150, 120);
            });
            KeyValuePair<string, (int W, int H, byte[] Rgb)> entry = Assert.Single(Images(pdf));
            var image = entry.Value;
            var mask = Stencil(pdf, entry.Key);
            Assert.Equal((150, 120), (image.W, image.H));
            Assert.Equal((900, 720), (mask.W, mask.H));
            byte[] ht = HalftoneTable();
            int best = -1;
            for (int n = 0; n < 16 && best < 0; n++)
            {
                bool all = true;
                for (int y = 0; y < mask.H && all; y++)
                    for (int x = 0; x < mask.W && all; x++)
                    {
                        int o = ((y / 6) * image.W + x / 6) * 3;
                        bool fore = image.Rgb[o] == Color.DarkRed.R && image.Rgb[o + 1] == Color.DarkRed.G && image.Rgb[o + 2] == Color.DarkRed.B;
                        bool want = fore && ht[((300 + y + n) & 15) * 16 + ((300 + x) & 15)] < 128;
                        all = Bit(mask, x, y) == want;
                    }
                if (all) best = n;
            }
            Assert.True(best >= 0, "the mask is not GDI+'s dither of the hatch at any row offset");
        }

        // HT_16x16 (gdiplus.dll): the 16 x 16 ordered dither, a recursive Bayer matrix (its last entry
        // 254 where the recursion gives 255).
        private static byte[] HalftoneTable()
        {
            var t = new byte[256];
            for (int y = 0; y < 16; y++)
                for (int x = 0; x < 16; x++)
                {
                    int v = 0;
                    for (int bit = 0; bit < 4; bit++)
                    {
                        int xb = (x >> bit) & 1, yb = (y >> bit) & 1;
                        v |= ((xb ^ yb) << (7 - 2 * bit)) | (yb << (6 - 2 * bit));
                    }
                    t[y * 16 + x] = (byte)Math.Min(v, 254);   // the table stops at 254
                }
            return t;
        }

        [Fact]
        public void ATranslucentPathGradientIsBandedAtAbout256Cells()
        {
            // s = ceil(w / 256): 1801 x 1201 device pixels on cells of 8 x 5, two bands, each a
            // colour DIB under its own halftone mask.
            PdfDocument pdf = PrintAt600(g =>
            {
                using var path = new GraphicsPath();
                path.AddEllipse(50, 50, 300, 200);
                using var pgb = new PathGradientBrush(path) { CenterColor = Color.FromArgb(200, Color.White), SurroundColors = new[] { Color.FromArgb(128, Color.Navy) } };
                g.FillPath(pgb, path);
            });
            var images = Images(pdf);
            Assert.Equal(2, images.Count);
            foreach (KeyValuePair<string, (int W, int H, byte[] Rgb)> im in images)
            {
                var mask = Stencil(pdf, im.Key);
                Assert.Equal(226, im.Value.W);
                Assert.Equal(8 * im.Value.W, mask.W);
                Assert.Equal(5 * im.Value.H, mask.H);
            }
        }

        [Fact]
        public void AHatchedPenFillsItsWidenedOutline()
        {
            // GDI+ widens the stroke on the device and fills it; the bounds it bands are the
            // outline's grown once more by the pen's reach (half its width times the miter limit),
            // which takes a 0.12 inch line's bands back to the page's corner: three of 412 x 63.
            PdfDocument pdf = PrintAt600(g =>
            {
                using var hb = new HatchBrush(HatchStyle.DiagonalCross, Color.DarkBlue, Color.LightYellow);
                using var pen = new Pen(hb, 12);
                g.DrawLine(pen, 50, 50, 350, 120);
            });
            var images = Images(pdf);
            Assert.Equal(3, images.Count);
            foreach (var im in images.Values) Assert.Equal((412, 63), (im.W, im.H));
            string content = Content(pdf);
            Assert.Contains("412 0 0 -63 0 63 cm", content, StringComparison.Ordinal);
            Assert.Contains("W n", content, StringComparison.Ordinal);
        }

        [Fact]
        public void ATextureOfOpaqueAndClearPixelsPrintsItsOpaqueRuns()
        {
            // A bitmap whose pixels are all opaque or clear is put down as the runs of each row's
            // pixels with alpha 5 or more; the PDF carries them as a stencil at the DIB's own size.
            using Bitmap tile = Tile(8, 6, 255);
            for (int y = 0; y < 6; y++) for (int x = 0; x < 8; x++) if ((x + y) % 3 == 0) tile.SetPixel(x, y, Color.Transparent);
            PdfDocument pdf = PrintAt600(g =>
            {
                using var tb = new TextureBrush(tile);
                g.FillEllipse(tb, 300, 480, 200, 150);
            });
            var images = Images(pdf);
            Assert.NotEmpty(images);
            int top = 0;
            foreach (KeyValuePair<string, (int W, int H, byte[] Rgb)> im in images)
            {
                var mask = Stencil(pdf, im.Key);
                Assert.Equal((im.Value.W, im.Value.H), (mask.W, mask.H));
                for (int j = 0; j < Math.Min(mask.H, 152 - top); j++)
                    for (int i = 0; i < mask.W; i++)
                        Assert.Equal(((300 + i) % 8 + (480 + top + j) % 6) % 3 != 0, Bit(mask, i, j));
                top += im.Value.H;
            }
        }

        [Fact]
        public void ATurnedImageBleedsItsEdgesIntoTheBand()
        {
            // NextBufferFunc24bppBleed: the span's edge pixels keep the image's colour and every row
            // runs on in its end pixel's, so the band's corners are the image's corner colours.
            using var bitmap = new Bitmap(64, 48);
            for (int y = 0; y < 48; y++)
                for (int x = 0; x < 64; x++)
                    bitmap.SetPixel(x, y, (x / 16 + y / 16) % 2 == 0 ? Color.FromArgb(200, 40, 40) : Color.FromArgb(40, 90, 200));
            PdfDocument pdf = PrintAt600(g =>
            {
                g.TranslateTransform(600, 100);
                g.RotateTransform(90);
                g.DrawImage(bitmap, 0, 0, 128, 96);
            });
            var image = Assert.Single(Images(pdf)).Value;
            Assert.Equal((96, 128), (image.W, image.H));
            Assert.Equal(new byte[] { 200, 40, 40 }, image.Rgb[0..3]);
            int last = (image.W * image.H - 1) * 3;
            Assert.Equal(new byte[] { 40, 90, 200 }, image.Rgb[last..(last + 3)]);
        }

        [Fact]
        public void APreviewIsThePagesAsScenesNotBitmaps()
        {
            var doc = new PrintDocument();
            doc.DefaultPageSettings.PaperSize = new PaperSize("Letter", 850, 1100);
            int page = 0;
            doc.PrintPage += (s, e) =>
            {
                e.Graphics.FillRectangle(Brushes.Red, 100, 100, 200, 100);
                e.HasMorePages = ++page < 3;
            };
            var controller = new PreviewPrintController();
            doc.PrintController = controller;
            try
            {
                doc.Print();
            }
            catch (InvalidPrinterException)
            {
                Assert.Skip("previewing needs a valid printer, as stock .NET's does");
            }
            PreviewPageInfo[] pages = controller.GetPreviewPageInfo();
            Assert.Equal(3, pages.Length);
            Assert.Equal(new Size(850, 1100), pages[0].PhysicalSize);
            Assert.IsNotType<Bitmap>(pages[0].Image);
            Assert.Equal(850, pages[0].Image.Width);

            // What the preview control does with it: draw it scaled into a box, on a recording Graphics.
            Graphics g = System.Drawing.WebGpuBackend.GpuRaster.NewRecording();
            g.DrawImage(pages[0].Image, new Rectangle(10, 10, 170, 220), 0, 0, 850, 1100, GraphicsUnit.Pixel);
            object scene = System.Drawing.WebGpuBackend.GpuRaster.EndScene(g);
            Assert.NotNull(scene);
        }

        [Fact]
        public void ARecordingGraphicsKeepsGdiPlusTransformState()
        {
            Graphics g = System.Drawing.WebGpuBackend.GpuRaster.NewRecording();
            g.PageUnit = GraphicsUnit.Point;
            Assert.Equal(GraphicsUnit.Point, g.PageUnit);
            g.TranslateTransform(10, 20);
            g.ScaleTransform(2, 3);
            float[] m = g.Transform.Elements;
            Assert.Equal(new float[] { 2, 0, 0, 3, 10, 20 }, m);

            GraphicsState state = g.Save();
            g.RotateTransform(90);
            g.Restore(state);
            Assert.Equal(new float[] { 2, 0, 0, 3, 10, 20 }, g.Transform.Elements);

            // World (1,1) -> page (12, 23) -> device: a point is 96/72 pixels on a 96 dpi recording.
            var pts = new[] { new PointF(1, 1) };
            g.TransformPoints(CoordinateSpace.Device, CoordinateSpace.World, pts);
            Assert.Equal(12 * 96 / 72f, pts[0].X, 3);
            Assert.Equal(23 * 96 / 72f, pts[0].Y, 3);
            g.Dispose();
        }

        [Fact]
        public void OnWindowsItPrintsToAFileThroughThePrinterDriver()
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "the GDI replay is Windows'");
            const string pdfPrinter = "Microsoft Print to PDF";
            bool installed = false;
            foreach (string p in PrinterSettings.InstalledPrinters) installed |= p == pdfPrinter;
            Assert.SkipUnless(installed, "needs Microsoft Print to PDF");

            string file = Path.Combine(Path.GetTempPath(), "wf-print-" + Guid.NewGuid().ToString("N") + ".pdf");
            try
            {
                var doc = new PrintDocument();
                doc.PrinterSettings.PrinterName = pdfPrinter;
                doc.PrinterSettings.PrintToFile = true;
                doc.PrinterSettings.PrintFileName = file;
                doc.PrintPage += (s, e) =>
                {
                    using var font = new Font("Arial", 14);
                    e.Graphics.DrawString("Printed by GDI", font, Brushes.Black, 100, 100);
                    e.Graphics.DrawEllipse(Pens.Blue, 100, 200, 300, 150);
                };
                doc.Print();

                // The driver writes the file when the job is spooled; give it a moment.
                for (int i = 0; i < 100 && (!File.Exists(file) || new FileInfo(file).Length == 0); i++)
                    System.Threading.Thread.Sleep(100);
                Assert.True(File.Exists(file), "the job wrote no file");
                byte[] bytes = File.ReadAllBytes(file);
                Assert.True(bytes.Length > 1000);
                Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(bytes, 0, 4));
            }
            finally
            {
                try { File.Delete(file); } catch (IOException) { }
            }
        }
    }
}
