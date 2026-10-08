// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Graphics.DrawString is GDI+'s, and GDI+ lays a string out itself (FastTextImager) -- natural
// hinted advances, the 1.03 tracking, em/6 margins, an unrounded baseline -- rather than the way
// GDI does. These hold the recorded layout against what gdiplus.dll itself handed to
// GpGraphics::DrawPlacedGlyphs for the same calls (logged by hooking it, .NET 10 on Windows 11,
// gdiplus 10.0.26100.9444): glyph ids and float origins, exactly.
//

using System;
using System.Collections;
using System.Drawing;
using System.Drawing.WebGpuBackend;
using System.Globalization;
using System.Linq;
using Xunit;

namespace Wpf.WinFormsInterop.Tests
{
    public class GdiPlusTextLayoutTests
    {
        /// <summary>The first GdiPlusTextDraw a recording made, as (glyphs, device x, baseline y);
        /// null when the string was drawn as anything else.</summary>
        private static (int[] Glyphs, float[] X, float Y)? Recorded(Action<Graphics> draw, out string kinds)
        {
            Graphics g = GpuRaster.NewRecording();
            draw(g);
            object scene = GpuRaster.EndScene(g);
            var names = new System.Text.StringBuilder();
            object? found = Find(scene, names);
            kinds = names.ToString();
            if (found is null) return null;
            object run = found.GetType().GetProperty("Run")!.GetValue(found)!;
            T F<T>(string name) => (T)run.GetType().GetField(name)!.GetValue(run)!;
            ushort[] glyphs = F<ushort[]>("Glyphs");
            float[] adv = F<float[]>("Advances");
            float ox = F<float>("OriginX"), lead = F<float>("LeadOffset");
            var xs = new float[glyphs.Length];
            if (xs.Length > 0)
            {
                // GdiPlusText.GlyphXs, with an identity device transform.
                xs[0] = F<bool>("RoundOrigin") ? MathF.Floor(ox + 0.5f) + lead : ox;
                for (int i = 1; i < xs.Length; i++) xs[i] = xs[i - 1] + adv[i - 1];
            }
            return (glyphs.Select(v => (int)v).ToArray(), xs, F<float>("OriginY"));
        }

        private static object? Find(object visual, System.Text.StringBuilder names)
        {
            foreach (object p in (IEnumerable)visual.GetType().GetProperty("Content")!.GetValue(visual)!)
            {
                names.Append(p.GetType().Name).Append(' ');
                if (p.GetType().Name == "GdiPlusTextDraw") return p;
            }
            foreach (object child in (IEnumerable)visual.GetType().GetProperty("Children")!.GetValue(visual)!)
                if (Find(child, names) is { } f) return f;
            return null;
        }

        private static bool FacesInstalled(params string[] files)
            => OperatingSystem.IsWindows()
               && files.All(f => System.IO.File.Exists(System.IO.Path.Combine(
                      Environment.GetFolderPath(Environment.SpecialFolder.Fonts), f)));

        // face, size, style, StringAlignment, LineAlignment, rect, typographic, text, then gdiplus.dll's
        // own glyph ids and origins ("x,y" each).
        [Theory]
        [InlineData("Times New Roman", 12f, 0, 2, 0, 4.86f, 28.81f, 0f, 80f, false, "The quick brown fox",
            "55 75 72 3 84 88 76 70 78 3 69 85 82 90 81 3 73 82 91",
            "-131,43.809998 -122,43.809998 -114,43.809998 -107,43.809998 -103,43.809998 -95,43.809998 -87,43.809998 -83,43.809998 -76,43.809998 -68,43.809998 -64,43.809998 -56,43.809998 -51,43.809998 -43,43.809998 -31,43.809998 -23,43.809998 -19,43.809998 -13,43.809998 -5,43.809998")]
        // Leading spaces: not drawn, an origin offset of (nom * scale16 * lead) >> 12 sixteenths.
        [InlineData("Arial", 12f, 3, 1, 0, 20.51f, 14.87f, 0f, 80f, false, "  padded  ",
            "83 68 71 71 72 71",
            "-3.875,29.869999 6.125,29.869999 15.125,29.869999 25.125,29.869999 35.125,29.869999 44.125,29.869999")]
        // A fixed-pitch face takes the nominal layout: unrounded origin, x += nom * scale16 / 65536.
        [InlineData("Consolas", 10f, 0, 1, 0, 16.45f, 29.69f, 120f, 50f, true, "File Edit View",
            "9 139 142 135 3 8 134 139 150 3 25 139 135 153",
            "25.134895,41.690002 32.47135,41.690002 39.807808,41.690002 47.144264,41.690002 54.48072,41.690002 61.817177,41.690002 69.15363,41.690002 76.49009,41.690002 83.826546,41.690002 91.163,41.690002 98.49946,41.690002 105.835915,41.690002 113.17237,41.690002 120.50883,41.690002")]
        [InlineData("Tahoma", 24f, 1, 1, 2, 12.8f, 15.62f, 600f, 31.5f, false, "WAVE To",
            "58 36 57 40 3 55 82",
            "240,36.495 273,36.495 295,36.495 317,36.495 337,36.495 346,36.495 366,36.495")]
        [InlineData("Verdana", 24f, 0, 1, 1, 4.34f, 24.01f, 600f, 50f, false, "File Edit View",
            "41 76 79 72 3 40 71 76 87 3 57 76 72 90",
            "197,59.56469 215,59.56469 224,59.56469 233,59.56469 252,59.56469 263,59.56469 283,59.56469 303,59.56469 312,59.56469 325,59.56469 336,59.56469 358,59.56469 367,59.56469 386,59.56469")]
        [InlineData("Microsoft Sans Serif", 8.25f, 0, 2, 1, 20.36f, 19.81f, 600f, 20f, false, "Ag",
            "36 74", "606,33.8974 613,33.8974")]
        public void DrawString_PlacesGlyphsWhereGdiPlusDoes(string face, float size, int style, int align, int lineAlign,
            float x, float y, float w, float h, bool typographic, string text, string glyphs, string origins)
        {
            Assert.SkipUnless(FacesInstalled("times.ttf", "arialbi.ttf", "consola.ttf", "tahomabd.ttf", "verdana.ttf", "micross.ttf"),
                              "needs the Windows faces the oracle was taken with");
            using var font = new Font(face, size, (FontStyle)style);
            using var format = typographic ? new StringFormat(StringFormat.GenericTypographic) : new StringFormat();
            format.Alignment = (StringAlignment)align;
            format.LineAlignment = (StringAlignment)lineAlign;
            var got = Recorded(g => g.DrawString(text, font, Brushes.Black, new RectangleF(x, y, w, h), format), out string kinds);
            Assert.True(got.HasValue, $"not drawn as GDI+ text: {kinds}");
            int[] wantGlyphs = glyphs.Split(' ').Select(int.Parse).ToArray();
            (float X, float Y)[] want = origins.Split(' ').Select(o =>
            {
                string[] q = o.Split(',');
                return (float.Parse(q[0], CultureInfo.InvariantCulture), float.Parse(q[1], CultureInfo.InvariantCulture));
            }).ToArray();
            Assert.Equal(wantGlyphs, got!.Value.Glyphs);
            Assert.Equal(want.Select(o => o.X).ToArray(), got.Value.X);
            Assert.Equal(want[0].Y, got.Value.Y);
        }

        [Fact]
        public void DrawString_NoFormat_UsesTheMarginAndNaturalAdvances()
        {
            Assert.SkipUnless(FacesInstalled("segoeui.ttf"), "needs Segoe UI");
            using var font = new Font("Segoe UI", 9f);
            // The specimen's first row: margin em/6 = 2, origin floor(6 + 2 + 0.5) = 8, baseline
            // 6 + VDMX ascent 13 = 19 (not rounded, and a row below GDI's tmAscent of 12); the advances
            // are DirectWrite's GDI-natural ones (the python model of gdiplus agrees: 8, 14, 21).
            var got = Recorded(g => g.DrawString("abc", font, Brushes.Black, 6f, 6f), out string kinds);
            Assert.True(got.HasValue, kinds);
            Assert.Equal(new[] { 8f, 14f, 21f }, got!.Value.X);
            Assert.Equal(19f, got.Value.Y);
        }

        /// <summary>A window's DrawString under an axis scale is the fast imager's under that scale,
        /// as a bitmap's is (GpGraphics::DrawString hands FastTextImager the world-to-device
        /// transform: the device em's realization, the stretched fits where the axes' ppems differ,
        /// the advances on the device): the recorded string, rasterized, is the engine's to the
        /// pixel -- every hint, the bi-level ones included.</summary>
        [Theory]
        [InlineData(1f, 1.5f, 5, "Verdana", 8f)]
        [InlineData(1f, 1.5f, 3, "Verdana", 8f)]
        [InlineData(1f, 1.5f, 1, "Segoe UI", 9f)]
        [InlineData(1f, 1.5f, 4, "Arial", 9f)]
        [InlineData(1f, 1.5f, 2, "Calibri", 9f)]
        [InlineData(2f, 2f, 5, "Tahoma", 8f)]
        [InlineData(2f, 2f, 3, "Times New Roman", 9f)]
        [InlineData(1.5f, 1f, 5, "Segoe UI", 9f)]
        [InlineData(1.25f, 1.25f, 1, "Arial", 10f)]
        public void DrawString_UnderAnAxisScale_IsTheEnginesOnScreen(float sx, float sy, int hint, string face, float px)
        {
            Assert.SkipUnless(FacesInstalled("verdana.ttf", "segoeui.ttf", "arial.ttf", "calibri.ttf", "tahoma.ttf", "times.ttf"),
                              "needs the Windows faces");
            using var font = new Font(face, px, GraphicsUnit.Pixel);
            const string text = "Stretched Hamburgefonstiv 0123";
            void Draw(Graphics g)
            {
                g.ScaleTransform(sx, sy);
                g.TextRenderingHint = (System.Drawing.Text.TextRenderingHint)hint;
                g.DrawString(text, font, Brushes.Black, 3f, 4f);
            }
            using var engine = new Bitmap(400, 80, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(engine)) { g.Clear(Color.White); Draw(g); }

            Graphics r = GpuRaster.NewRecording();
            Draw(r);
            object scene = GpuRaster.EndScene(r);
            Assert.True(Find(scene, new System.Text.StringBuilder()) is not null, "not recorded as GDI+ text");
            using var screen = new Bitmap(400, 80, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(screen)) g.Clear(Color.White);
            screen.GetPixel(0, 0);   // the clear on the pixels
            SceneRaster.Render(screen.managed.Frame, new[] { scene });

            int differ = 0;
            for (int yy = 0; yy < 80; yy++)
                for (int xx = 0; xx < 400; xx++)
                    if (engine.GetPixel(xx, yy) != screen.GetPixel(xx, yy)) differ++;
            Assert.Equal(0, differ);
        }

        /// <summary>What the fast imager refuses under a transform -- a turned world, a wrapping or
        /// multi-line string, tabs, the style lines, a layout rectangle that clips -- a window has the
        /// GDI+ engine lay out and realize, and blends the levels itself: the recorded string,
        /// rasterized, is the engine's bitmap.</summary>
        [Theory]
        [InlineData("rotate30", 5)]
        [InlineData("rotate90", 4)]
        [InlineData("rotate-45", 1)]
        [InlineData("wrap", 5)]
        [InlineData("wrap", 3)]
        [InlineData("tabs", 5)]
        [InlineData("underline", 5)]
        [InlineData("clip", 5)]
        [InlineData("lines", 4)]
        public void DrawString_ThroughTheEngine_IsTheEnginesOnScreen(string kind, int hint)
        {
            Assert.SkipUnless(FacesInstalled("segoeui.ttf", "verdana.ttf", "arial.ttf"), "needs the Windows faces");
            using var font = new Font(kind == "underline" ? "Arial" : "Segoe UI", 9f, kind == "underline" ? FontStyle.Underline : FontStyle.Regular, GraphicsUnit.Pixel);
            void Draw(Graphics g)
            {
                switch (kind)
                {
                    case "rotate30": g.TranslateTransform(60f, 10f); g.RotateTransform(30f); break;
                    case "rotate90": g.TranslateTransform(120f, 4f); g.RotateTransform(90f); break;
                    case "rotate-45": g.TranslateTransform(20f, 70f); g.RotateTransform(-45f); break;
                    default: g.ScaleTransform(kind == "tabs" ? 1.5f : 1f, kind == "clip" ? 2f : 1.5f); break;
                }
                g.TextRenderingHint = (System.Drawing.Text.TextRenderingHint)hint;
                switch (kind)
                {
                    case "wrap": g.DrawString("Wrapped Hamburgefonstiv text in a narrow box", font, Brushes.Black, new RectangleF(3f, 2f, 90f, 60f)); break;
                    case "lines": g.DrawString("two\nlines", font, Brushes.Black, 3f, 2f); break;
                    case "tabs": g.DrawString("a\tb\tc", font, Brushes.Black, 3f, 4f); break;
                    case "clip": g.DrawString("Clipped by its layout rectangle", font, Brushes.Black, new RectangleF(3f, 2f, 70f, 12f)); break;
                    default: g.DrawString("Turned Hamburgefonstiv 0123", font, Brushes.Black, 3f, 4f); break;
                }
            }
            using var engine = new Bitmap(400, 160, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(engine)) { g.Clear(Color.White); Draw(g); }

            Graphics r = GpuRaster.NewRecording();
            Draw(r);
            object scene = GpuRaster.EndScene(r);
            using var screen = new Bitmap(400, 160, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(screen)) g.Clear(Color.White);
            screen.GetPixel(0, 0);   // the clear on the pixels
            SceneRaster.Render(screen.managed.Frame, new[] { scene });

            int differ = 0, ink = 0;
            for (int yy = 0; yy < 160; yy++)
                for (int xx = 0; xx < 400; xx++)
                {
                    Color a = engine.GetPixel(xx, yy), b = screen.GetPixel(xx, yy);
                    if (a.ToArgb() != Color.White.ToArgb()) ink++;
                    if (a != b) differ++;
                }
            Assert.True(ink > 20, "the engine drew nothing");
            Assert.Equal(0, differ);
        }

        [Theory]
        [InlineData("tab\there")]       // control characters: the full imager (Line Services)
        [InlineData("two\nlines")]
        [InlineData("שלום")]   // a complex script
        public void DrawString_WhatTheFastImagerRefuses_KeepsTheOldPath(string text)
        {
            Assert.SkipUnless(FacesInstalled("segoeui.ttf"), "needs Segoe UI");
            using var font = new Font("Segoe UI", 9f);
            var got = Recorded(g => g.DrawString(text, font, Brushes.Black, 6f, 6f), out string kinds);
            Assert.Null(got);
            Assert.Contains("GlyphRunDraw", kinds);
        }
    }
}
