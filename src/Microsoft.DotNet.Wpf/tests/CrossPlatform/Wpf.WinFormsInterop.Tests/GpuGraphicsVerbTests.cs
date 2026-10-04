// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Every System.Drawing draw verb has to reach the GPU scene. The ones that were never routed fell
// through to a GDI+ call on a null handle and drew NOTHING, silently: FillRectangles (a tool strip's
// grip), DrawImage with a source rectangle or ImageAttributes, DrawLine with PointF, the curve and
// Bezier family, DrawPie, FillClosedCurve, FillRegion. These record through GpuRaster and count what
// arrives in the scene.
//

using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.WebGpuBackend;
using Xunit;

namespace Wpf.WinFormsInterop.Tests
{
    public class GpuGraphicsVerbTests
    {
        /// <summary>Primitives in the recorded scene, and the names of their types.</summary>
        private static (int count, string kinds) Record(Action<Graphics> draw)
        {
            Graphics g = GpuRaster.NewRecording();
            draw(g);
            object scene = GpuRaster.EndScene(g);
            int n = 0;
            var kinds = new System.Text.StringBuilder();
            Walk(scene, ref n, kinds);
            return (n, kinds.ToString());
        }

        private static void Walk(object visual, ref int n, System.Text.StringBuilder kinds)
        {
            var content = (IEnumerable)visual.GetType().GetProperty("Content").GetValue(visual);
            foreach (object p in content)
            {
                n++;
                kinds.Append(p.GetType().Name);
                object geo = p.GetType().GetProperty("Geometry")?.GetValue(p);
                if (geo != null) kinds.Append('(').Append(geo.GetType().Name).Append(')');
                kinds.Append(' ');
            }
            foreach (object child in (IEnumerable)visual.GetType().GetProperty("Children").GetValue(visual))
                Walk(child, ref n, kinds);
        }

        [Fact]
        public void FillRectangles_FillsEachRectangle()
        {
            var (n, _) = Record(g => g.FillRectangles(Brushes.Black, new[] { new Rectangle(0, 0, 2, 2), new Rectangle(4, 0, 2, 2) }));
            Assert.Equal(2, n);
        }

        [Fact]
        public void DrawRectangles_And_DrawLinePointF_Draw()
        {
            Assert.True(Record(g => g.DrawRectangles(Pens.Black, new[] { new Rectangle(0, 0, 5, 5) })).count > 0);
            Assert.True(Record(g => g.DrawLine(Pens.Black, new PointF(0, 0), new PointF(5, 5))).count > 0);
        }

        [Fact]
        public void CurvesAndBeziers_Draw()
        {
            var pts = new[] { new Point(0, 0), new Point(10, 5), new Point(20, 0), new Point(30, 10) };
            Assert.True(Record(g => g.DrawBezier(Pens.Black, pts[0], pts[1], pts[2], pts[3])).count > 0);
            Assert.True(Record(g => g.DrawBeziers(Pens.Black, pts)).count > 0);
            Assert.True(Record(g => g.DrawCurve(Pens.Black, pts)).count > 0);
            Assert.True(Record(g => g.DrawClosedCurve(Pens.Black, pts)).count > 0);
            Assert.True(Record(g => g.FillClosedCurve(Brushes.Black, pts)).count > 0);
            Assert.True(Record(g => g.DrawPie(Pens.Black, 0f, 0f, 20f, 20f, 0f, 90f)).count > 0);
        }

        [Fact]
        public void FillRegion_FillsItsScans()
        {
            using var region = new Region(new Rectangle(0, 0, 10, 10));
            region.Union(new Rectangle(20, 0, 10, 10));
            Assert.Equal(2, Record(g => g.FillRegion(Brushes.Black, region)).count);
        }

        [Fact]
        public void DrawImage_WithSourceRectAndAttributes_Draws()
        {
            using var bmp = new Bitmap(8, 8);
            using var attrs = new ImageAttributes();
            attrs.SetColorMatrix(new ColorMatrix { Matrix33 = 0.5f });
            Assert.Equal(1, Record(g => g.DrawImage(bmp, new Rectangle(0, 0, 4, 4), new Rectangle(2, 2, 4, 4), GraphicsUnit.Pixel)).count);
            Assert.Equal(1, Record(g => g.DrawImage(bmp, new Rectangle(0, 0, 8, 8), 0, 0, 8, 8, GraphicsUnit.Pixel, attrs)).count);
        }

        [Fact]
        public void ImageAttributes_ColorMatrix_IsApplied()
        {
            using var attrs = new ImageAttributes();
            attrs.SetColorMatrix(new ColorMatrix(new[]
            {
                new float[] { 0, 0, 0, 0, 0 },
                new float[] { 0, 1, 0, 0, 0 },
                new float[] { 0, 0, 1, 0, 0 },
                new float[] { 0, 0, 0, 0.5f, 0 },
                new float[] { 0, 0, 0, 0, 1 },
            }));
            // The matrix applies when the image is drawn (GpRecolorObject::ColorAdjust on the bitmap).
            using var src = new Bitmap(1, 1, PixelFormat.Format32bppArgb);
            src.SetPixel(0, 0, Color.FromArgb(255, 200, 100, 50));
            using var dst = new Bitmap(1, 1, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(dst))
            {
                g.CompositingMode = CompositingMode.SourceCopy;
                g.DrawImage(src, new Rectangle(0, 0, 1, 1), 0, 0, 1, 1, GraphicsUnit.Pixel, attrs);
            }
            Color c = dst.GetPixel(0, 0);
            Assert.Equal(0, c.R);
            Assert.InRange(c.G, 99, 101);
            Assert.InRange(c.B, 49, 51);
            Assert.Equal(128, c.A);

            // GetAdjustedPalette adjusts GDI+'s native copy only: System.Drawing never reads it back,
            // so the caller's palette is unchanged (as on native GDI+).
            var palette = new Bitmap(1, 1, PixelFormat.Format8bppIndexed).Palette;
            palette.Entries[0] = Color.FromArgb(255, 200, 100, 50);
            attrs.GetAdjustedPalette(palette, ColorAdjustType.Bitmap);
            Color p0 = palette.Entries[0];
            Assert.Equal((200, 100, 50, 255), (p0.R, p0.G, p0.B, p0.A));
        }

        [Fact]
        public void FillPath_KeepsAHoleUnderAlternate()
        {
            // An outer square and an inner one: one region with a hole, not two filled squares.
            using var path = new GraphicsPath(FillMode.Alternate);
            path.AddRectangle(new Rectangle(0, 0, 30, 30));
            path.AddRectangle(new Rectangle(10, 10, 10, 10));
            var (n, kinds) = Record(g => g.FillPath(Brushes.Black, path));
            Assert.Equal(1, n);
            Assert.Contains("PathGeometry", kinds);
        }

        [Fact]
        public void FillPolygon_Concave_IsNotAFan()
        {
            // An L: a fan from its first corner would cover the notch.
            // Antialiased: GDI+'s default is aliased, which is the next test.
            var l = new[] { new Point(0, 0), new Point(10, 0), new Point(10, 5), new Point(5, 5), new Point(5, 10), new Point(0, 10) };
            Assert.Contains("PathGeometry", Record(g => { g.SmoothingMode = SmoothingMode.AntiAlias; g.FillPolygon(Brushes.Black, l); }).kinds);
            var square = new[] { new Point(0, 0), new Point(10, 0), new Point(10, 10), new Point(0, 10) };
            Assert.Contains("PolygonGeometry", Record(g => { g.SmoothingMode = SmoothingMode.AntiAlias; g.FillPolygon(Brushes.Black, square); }).kinds);
        }

        [Fact]
        public void FillPolygon_Aliased_IsGdiPlusPixelCentres()
        {
            // A ToolStrip combo box's drop-down arrow, as .NET fills it at SmoothingMode.None, and
            // the rows Windows lights for it (read off the stock tool strip): a pixel's centre is on
            // the integer coordinate, left and top edges are in, right and bottom edges out.
            var arrow = new[] { new Point(112, 10), new Point(117, 10), new Point(114, 13) };
            Graphics g = GpuRaster.NewRecording();
            g.FillPolygon(Brushes.Black, arrow);
            var rows = new List<string>();
            CollectRects(GpuRaster.EndScene(g), rows);
            Assert.Equal(new[] { "112,10,5,1", "113,11,3,1", "114,12,1,1" }, rows);
        }

        [Fact]
        public void LinearGradient_IsGdiPlusTable()
        {
            // The professional renderer's second tool strip gradient: brush over rows 12..24, filled
            // on 13..24. GDI+'s rows, as it computes them (a table of rounded colours blended with an
            // eight-bit fraction) and as Windows draws them; a float lerp at pixel centres gives
            // 247, 243 and 242 on rows 13, 20 and 22.
            var span = new GdipLinearGradient.Span(new RectangleF(0, 12, 10, 13), Color.FromArgb(248, 248, 248),
                                                   Color.FromArgb(241, 241, 241), LinearGradientMode.Vertical);
            var rows = new List<int>();
            for (int y = 13; y <= 24; y++) rows.Add((int)(span.Pixel(0, y) & 0xff));
            Assert.Equal(new[] { 248, 247, 246, 246, 245, 245, 244, 244, 243, 243, 242, 241 }, rows);
        }

        [Fact]
        public void AntialiasedEllipse_IsGdiPlusCoverage()
        {
            // FillEllipse(3.11, 3.812, 3.285, 5.582) with SmoothingMode.AntiAlias, as gdiplus.dll
            // draws it onto a transparent bitmap: the alpha of columns 3..6, rows 4..9.
            Assert.True(GdipAntialias.Fill(GdipAntialias.Ellipse(3.11f, 3.812f, 3.285f, 5.582f), 1, 0, 0, 1, 0, 0,
                                           out byte[] alpha, out Rectangle r));
            int At(int x, int y) => x < r.X || y < r.Y || x >= r.Right || y >= r.Bottom ? 0 : alpha[(y - r.Y) * r.Width + x - r.X];
            var rows = new List<string>();
            for (int y = 4; y <= 9; y++) rows.Add($"{At(3, y)} {At(4, y)} {At(5, y)} {At(6, y)}");
            Assert.Equal(new[] { "0 40 120 8", "0 223 255 135", "32 255 255 207", "56 255 255 239", "8 247 255 175", "0 143 247 64" }, rows);
        }

        private static void CollectRects(object visual, List<string> rows)
        {
            foreach (object p in (IEnumerable)visual.GetType().GetProperty("Content").GetValue(visual))
            {
                object geo = p.GetType().GetProperty("Geometry")?.GetValue(p);
                object rect = geo?.GetType().GetProperty("Rect")?.GetValue(geo) ?? geo?.GetType().GetField("Rect")?.GetValue(geo);
                if (rect == null) continue;
                float F(string n) => Convert.ToSingle(rect.GetType().GetProperty(n)?.GetValue(rect) ?? rect.GetType().GetField(n).GetValue(rect));
                rows.Add($"{F("X")},{F("Y")},{F("Width")},{F("Height")}");
            }
            foreach (object child in (IEnumerable)visual.GetType().GetProperty("Children").GetValue(visual))
                CollectRects(child, rows);
        }
    }
}
