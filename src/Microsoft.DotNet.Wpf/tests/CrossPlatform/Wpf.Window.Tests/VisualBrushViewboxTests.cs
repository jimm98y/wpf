// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// A VisualBrush with an ABSOLUTE Viewbox and a bounding-box-relative Viewport, stretched to fill: what
// app's editor draws as the ghost of an element being dragged (GwxElement.GetDrawing).
//

using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Xunit;

namespace Wpf.Window.Tests
{
    public class VisualBrushViewboxTests
    {
        /// <summary>The source's subtree reaches far past the Viewbox (app's ghost of a grid
        /// control: its scroll extents and popups): only the Viewbox is shown, filling the target.
        /// The whole subtree was, so the element came out in a corner at a fraction of its size.</summary>
        [Fact]
        public void AnAbsoluteViewbox_SelectsPartOfALargerSource()
        {
            uint[] px = UiThread.Invoke(() =>
            {
                var canvas = new System.Windows.Controls.Canvas { Width = 100, Height = 50 };
                var red = new Rectangle { Fill = Brushes.Red, Width = 50, Height = 50 };
                var blue = new Rectangle { Fill = Brushes.Blue, Width = 50, Height = 50 };
                var far = new Rectangle { Fill = Brushes.Lime, Width = 300, Height = 300 };
                System.Windows.Controls.Canvas.SetLeft(blue, 50);
                System.Windows.Controls.Canvas.SetLeft(far, 150); System.Windows.Controls.Canvas.SetTop(far, 100);
                canvas.Children.Add(red); canvas.Children.Add(blue); canvas.Children.Add(far);
                canvas.Measure(new Size(100, 50)); canvas.Arrange(new Rect(0, 0, 100, 50));
                canvas.UpdateLayout();
                var brush = new VisualBrush(canvas)
                {
                    Viewbox = new Rect(0, 0, 100, 50), ViewboxUnits = BrushMappingMode.Absolute,
                    Viewport = new Rect(0, 0, 1, 1), ViewportUnits = BrushMappingMode.RelativeToBoundingBox,
                    TileMode = TileMode.None, Stretch = Stretch.Fill,
                };
                var dv = new DrawingVisual();
                using (DrawingContext dc = dv.RenderOpen())
                    dc.DrawRectangle(brush, null, new Rect(0, 0, 200, 100));
                var rtb = new RenderTargetBitmap(200, 100, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(dv);
                var buf = new uint[200 * 100];
                rtb.CopyPixels(buf, 200 * 4, 0);
                return buf;
            });
            Assert.Equal(0xFFFF0000u, px[10 * 200 + 10]);
            Assert.Equal(0xFFFF0000u, px[90 * 200 + 90]);
            Assert.Equal(0xFF0000FFu, px[10 * 200 + 110]);
            Assert.Equal(0xFF0000FFu, px[90 * 200 + 190]);
        }

        [Fact]
        public void AnAbsoluteViewbox_FillsTheTarget()
        {
            uint[] px = UiThread.Invoke(() =>
            {
                // The source: 100x50, left half red, right half blue.
                var grid = new System.Windows.Controls.Grid { Width = 100, Height = 50 };
                grid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition());
                grid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition());
                var red = new Rectangle { Fill = Brushes.Red };
                var blue = new Rectangle { Fill = Brushes.Blue };
                System.Windows.Controls.Grid.SetColumn(blue, 1);
                grid.Children.Add(red); grid.Children.Add(blue);
                grid.Measure(new Size(100, 50)); grid.Arrange(new Rect(0, 0, 100, 50));
                grid.UpdateLayout();

                var brush = new VisualBrush(grid)
                {
                    Viewbox = new Rect(0, 0, 100, 50), ViewboxUnits = BrushMappingMode.Absolute,
                    Viewport = new Rect(0, 0, 1, 1), ViewportUnits = BrushMappingMode.RelativeToBoundingBox,
                    TileMode = TileMode.None, Stretch = Stretch.Fill,
                };
                var dv = new DrawingVisual();
                using (DrawingContext dc = dv.RenderOpen())
                    dc.DrawRectangle(brush, null, new Rect(0, 0, 200, 100));
                var rtb = new RenderTargetBitmap(200, 100, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(dv);
                var buf = new uint[200 * 100];
                rtb.CopyPixels(buf, 200 * 4, 0);
                return buf;
            });
            // The brush fills the whole 200x100: red on the left half, blue on the right, to the corners.
            Assert.Equal(0xFFFF0000u, px[10 * 200 + 10]);
            Assert.Equal(0xFFFF0000u, px[90 * 200 + 90]);
            Assert.Equal(0xFF0000FFu, px[10 * 200 + 110]);
            Assert.Equal(0xFF0000FFu, px[90 * 200 + 190]);
        }
        // ---- The Viewbox is in the space the source is drawn in WITH its offset and transform: its
        // parent's. Recorded from stock WPF (RenderTargetBitmap of a Border at (100,50) on a Canvas):
        // a Viewbox of its bounds in the parent shows it, one of its own bounds (0,0,w,h) shows
        // nothing, and a RenderTransform moves it inside a fixed Viewbox. app's drag ghost and
        // resize outline (GwxElement.GetDrawing) take the element's parent-space bounds; taking them
        // in its own space cropped the ghost by the grab offset and put the outline at the origin.

        private static bool IsRed(uint p) => ((p >> 16) & 0xFF) > 200 && ((p >> 8) & 0xFF) < 60 && (p & 0xFF) < 60;

        /// <summary>A 600x400 white canvas with a red 120x40 Border at (100,50), plus cells filled with
        /// VisualBrushes of that Border through the given Viewboxes (each 120x40, at y = 200 + 50*i),
        /// with the Border moved by a RenderTransform of <paramref name="move"/>.</summary>
        private static uint[] RenderCells(Vector? move, params Rect[] viewboxes)
        {
            return UiThread.Invoke(() =>
            {
                var canvas = new System.Windows.Controls.Canvas { Width = 600, Height = 400, Background = Brushes.White };
                var src = new System.Windows.Controls.Border { Width = 120, Height = 40, Background = Brushes.Red };
                System.Windows.Controls.Canvas.SetLeft(src, 100); System.Windows.Controls.Canvas.SetTop(src, 50);
                if (move is { } m) src.RenderTransform = new TranslateTransform(m.X, m.Y);
                canvas.Children.Add(src);
                for (int i = 0; i < viewboxes.Length; i++)
                {
                    var cell = new Rectangle
                    {
                        Width = 120, Height = 40,
                        Fill = new VisualBrush(src)
                        {
                            Viewbox = viewboxes[i], ViewboxUnits = BrushMappingMode.Absolute,
                            Viewport = new Rect(0, 0, 1, 1), ViewportUnits = BrushMappingMode.RelativeToBoundingBox,
                            TileMode = TileMode.None, Stretch = Stretch.Fill,
                        },
                    };
                    System.Windows.Controls.Canvas.SetLeft(cell, 300); System.Windows.Controls.Canvas.SetTop(cell, 200 + 50 * i);
                    canvas.Children.Add(cell);
                }
                canvas.Measure(new Size(600, 400)); canvas.Arrange(new Rect(0, 0, 600, 400)); canvas.UpdateLayout();
                var rtb = new RenderTargetBitmap(600, 400, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(canvas);
                var buf = new uint[600 * 400];
                rtb.CopyPixels(buf, 600 * 4, 0);
                return buf;
            });
        }

        [Fact]
        public void AnAbsoluteViewbox_IsInTheSourcesParentSpace()
        {
            uint[] px = RenderCells(null, new Rect(100, 50, 120, 40), new Rect(0, 0, 120, 40));
            Assert.True(IsRed(px[220 * 600 + 310]) && IsRed(px[235 * 600 + 410]), "the parent-space Viewbox did not show the source");
            Assert.False(IsRed(px[270 * 600 + 310]) || IsRed(px[285 * 600 + 410]), "a Viewbox of the source's own bounds showed it (stock shows nothing there)");
        }

        [Fact]
        public void TwoViewboxesOfOneSource_EachShowTheirOwnPart()
        {
            // The second Viewbox starts 60 px left of the Border: its left half is empty, its right half red.
            uint[] px = RenderCells(null, new Rect(100, 50, 120, 40), new Rect(40, 50, 120, 40));
            Assert.True(IsRed(px[220 * 600 + 305]), "first brush");
            Assert.False(IsRed(px[270 * 600 + 310]), "the second brush shows the first brush's picture");
            Assert.True(IsRed(px[270 * 600 + 410]), "the second brush lost the Border");
        }

        [Fact]
        public void ARenderTransformOnTheSource_MovesItInsideAFixedViewbox()
        {
            uint[] px = RenderCells(new Vector(30, 10), new Rect(100, 50, 120, 40));
            Assert.False(IsRed(px[220 * 600 + 310]), "the moved source still fills the Viewbox's left edge");
            Assert.True(IsRed(px[235 * 600 + 340]), "the moved source is missing where it moved to");
        }

        /// <summary>app's resize outline: a DrawingGroup scaled about the element's origin holding
        /// GetDrawing's group (the parent-space VisualBrush under the inverse of the element's offset),
        /// placed at the element. Stock draws it over the element, 1.5x its size.</summary>
        [Fact]
        public void AScaledDrawingOfTheElement_DrawsOverTheElement()
        {
            uint[] px = UiThread.Invoke(() =>
            {
                var canvas = new System.Windows.Controls.Canvas { Width = 600, Height = 400, Background = Brushes.White };
                var src = new System.Windows.Controls.Border { Width = 120, Height = 40, Background = Brushes.Red };
                System.Windows.Controls.Canvas.SetLeft(src, 100); System.Windows.Controls.Canvas.SetTop(src, 50);
                canvas.Children.Add(src);
                var host = new DrawingHost();
                canvas.Children.Add(host);
                var val = new Rect(100, 50, 120, 40);
                var inner = new DrawingGroup { Transform = new TranslateTransform(-100, -50) };
                inner.Children.Add(new GeometryDrawing(new VisualBrush(src)
                {
                    Viewbox = val, ViewboxUnits = BrushMappingMode.Absolute,
                    Viewport = new Rect(0, 0, 1, 1), ViewportUnits = BrushMappingMode.RelativeToBoundingBox,
                    TileMode = TileMode.None, Stretch = Stretch.Fill,
                }, null, new RectangleGeometry(val)));
                var scaled = new DrawingGroup { Transform = new ScaleTransform(1.5, 1.5) };
                scaled.Children.Add(inner);
                var placed = new DrawingGroup { Transform = new TranslateTransform(100, 50) };
                placed.Children.Add(scaled);
                host.Drawing = placed;
                canvas.Measure(new Size(600, 400)); canvas.Arrange(new Rect(0, 0, 600, 400)); canvas.UpdateLayout();
                var rtb = new RenderTargetBitmap(600, 400, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(canvas);
                var buf = new uint[600 * 400];
                rtb.CopyPixels(buf, 600 * 4, 0);
                return buf;
            });
            // (100,50) + 1.5 * (120,40) = (280,110): the outline reaches past the element's own corner.
            Assert.True(IsRed(px[100 * 600 + 270]), "the outline is not over the element at 1.5x");
            Assert.False(IsRed(px[10 * 600 + 10]), "the outline is drawn at the origin");
        }

        private sealed class DrawingHost : FrameworkElement
        {
            public Drawing? Drawing;
            protected override void OnRender(DrawingContext dc) { if (Drawing != null) dc.DrawDrawing(Drawing); }
        }
    }
}
