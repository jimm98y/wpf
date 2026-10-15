// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// A VisualBrush with an ABSOLUTE Viewbox and a bounding-box-relative Viewport, stretched to fill: what
// GraphWorX's editor draws as the ghost of an element being dragged (GwxElement.GetDrawing).
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
    }
}
