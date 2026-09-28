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
            var palette = new Bitmap(1, 1, PixelFormat.Format8bppIndexed).Palette;
            palette.Entries[0] = Color.FromArgb(255, 200, 100, 50);
            attrs.GetAdjustedPalette(palette, ColorAdjustType.Bitmap);
            Color c = palette.Entries[0];
            Assert.Equal((0, 100, 50, 128), (c.R, c.G, c.B, c.A));
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
            var l = new[] { new Point(0, 0), new Point(10, 0), new Point(10, 5), new Point(5, 5), new Point(5, 10), new Point(0, 10) };
            Assert.Contains("PathGeometry", Record(g => g.FillPolygon(Brushes.Black, l)).kinds);
            var square = new[] { new Point(0, 0), new Point(10, 0), new Point(10, 10), new Point(0, 10) };
            Assert.Contains("PolygonGeometry", Record(g => g.FillPolygon(Brushes.Black, square)).kinds);
        }
    }
}
