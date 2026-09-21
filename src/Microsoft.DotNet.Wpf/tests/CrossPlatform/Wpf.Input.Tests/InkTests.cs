// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Ink: the reason a pen exists.
//
// InkCanvas is the deepest consumer of the stylus stack in the product. A stroke is not built from
// StylusDown/Move/Up the way an ordinary handler would build it -- it comes off the StylusPlugIn
// pipeline, through DynamicRenderer and the EditingCoordinator, and lands in a StrokeCollection
// carrying the raw StylusPointCollection including pressure. That is a lot of machinery no other
// test here touches, and in stock WPF most of it hung off PenImc, the one native DLL this port
// deleted.
//

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using Xunit;

namespace Wpf.Input.Tests
{
    public class InkTests
    {
        /// <summary>
        /// A pen dragged across an InkCanvas leaves a stroke.
        /// </summary>
        [Fact]
        public void APenStrokeLandsInTheInkCanvas()
        {
            using var pen = new PenInjector();
            Assert.SkipUnless(pen.TryInitialize(), "this session has no synthetic pen device");

            using var probe = InputWindow.Show();
            InkCanvas? canvas = null;

            UiThread.Invoke(() =>
            {
                canvas = new InkCanvas { Background = System.Windows.Media.Brushes.White };
                probe.Surface.Child = canvas;
            });
            probe.Pump(TimeSpan.FromMilliseconds(400));

            DrawWithPen(pen, probe, canvas!, fromX: 60, toX: 300, y: 140, steps: 12);

            int strokes = UiThread.Invoke(() => canvas!.Strokes.Count);
            Assert.True(strokes > 0,
                "a pen dragged across an InkCanvas produced no stroke; the stylus plug-in pipeline is not delivering ink");
        }

        /// <summary>
        /// The stroke carries the pressure the pen applied. This is what makes ink look like ink --
        /// a stroke whose points all report the same pressure renders as a uniform line, which is
        /// exactly what a stack that delivers the geometry but drops the packet data produces.
        /// </summary>
        [Fact]
        public void AnInkStrokeCarriesVaryingPressure()
        {
            using var pen = new PenInjector();
            Assert.SkipUnless(pen.TryInitialize(), "this session has no synthetic pen device");

            using var probe = InputWindow.Show();
            InkCanvas? canvas = null;

            UiThread.Invoke(() =>
            {
                canvas = new InkCanvas { Background = System.Windows.Media.Brushes.White };
                probe.Surface.Child = canvas;
            });
            probe.Pump(TimeSpan.FromMilliseconds(400));

            DrawWithPen(pen, probe, canvas!, fromX: 60, toX: 300, y: 140, steps: 12, varyPressure: true);

            (int points, float min, float max) = UiThread.Invoke(() =>
            {
                if (canvas!.Strokes.Count == 0) return (0, 0f, 0f);

                Stroke stroke = canvas.Strokes[0];
                float lo = float.MaxValue, hi = float.MinValue;
                foreach (StylusPoint point in stroke.StylusPoints)
                {
                    lo = Math.Min(lo, point.PressureFactor);
                    hi = Math.Max(hi, point.PressureFactor);
                }
                return (stroke.StylusPoints.Count, lo, hi);
            });

            Assert.True(points > 2, $"the stroke has only {points} stylus point(s), so pressure could not be compared");
            Assert.True(max - min > 0.2f,
                $"the ink stroke's pressure never varied (between {min:0.###} and {max:0.###}) although the pen went from light to hard");
        }

        /// <summary>
        /// The eraser end erases. This is the behaviour the Inverted flag exists for, tested where it
        /// actually matters rather than on the flag itself: InkCanvas switches its editing mode from
        /// EditingMode to EditingModeInverted the moment a pen reports as turned over.
        /// </summary>
        [Fact]
        public void TheEraserEndErasesInsteadOfDrawing()
        {
            using var pen = new PenInjector();
            Assert.SkipUnless(pen.TryInitialize(), "this session has no synthetic pen device");

            using var probe = InputWindow.Show();
            InkCanvas? canvas = null;

            UiThread.Invoke(() =>
            {
                canvas = new InkCanvas { Background = System.Windows.Media.Brushes.White };
                probe.Surface.Child = canvas;
            });
            probe.Pump(TimeSpan.FromMilliseconds(400));

            // Draw a stroke with the nib...
            DrawWithPen(pen, probe, canvas!, fromX: 60, toX: 300, y: 140, steps: 12);
            int afterDrawing = UiThread.Invoke(() => canvas!.Strokes.Count);
            Assert.True(afterDrawing > 0, "nothing was drawn, so there was nothing to erase");

            // ...then go over it with the pen turned over.
            DrawWithPen(pen, probe, canvas!, fromX: 60, toX: 300, y: 140, steps: 12, eraser: true);
            int afterErasing = UiThread.Invoke(() => canvas!.Strokes.Count);

            Assert.True(afterErasing < afterDrawing,
                $"dragging the eraser end over the ink left {afterErasing} stroke(s) where there were {afterDrawing}; the eraser is drawing instead of erasing");
        }

        /// <summary>
        /// A finger draws too. InkCanvas inks from touch as well as from a pen, and it is a separate
        /// route into the same pipeline -- the touch device has to be handed to the stylus plug-ins
        /// rather than promoted to a mouse, or the drag pans and selects instead of inking.
        /// </summary>
        [Fact]
        public void AFingerAlsoDrawsOnTheInkCanvas()
        {
            using var injector = new TouchInjector();
            Assert.SkipUnless(injector.TryInitialize(), "this session does not accept injected touch");

            using var probe = InputWindow.Show();
            InkCanvas? canvas = null;

            UiThread.Invoke(() =>
            {
                canvas = new InkCanvas { Background = System.Windows.Media.Brushes.White };
                probe.Surface.Child = canvas;
            });
            probe.Pump(TimeSpan.FromMilliseconds(400));

            const double y = 140;
            (int sx, int sy) = ElementPointToScreen(probe, canvas!, 60, y);
            injector.Down(new Contact(0, sx, sy));
            probe.Pump(TimeSpan.FromMilliseconds(100));

            for (int step = 1; step <= 12; step++)
            {
                (int mx, int my) = ElementPointToScreen(probe, canvas!, 60 + 20.0 * step, y);
                injector.Move(new Contact(0, mx, my));
                probe.Pump(TimeSpan.FromMilliseconds(40));
            }

            injector.Up(0);
            probe.Pump(TimeSpan.FromMilliseconds(300));

            int strokes = UiThread.Invoke(() => canvas!.Strokes.Count);
            Assert.True(strokes > 0, "a finger dragged across an InkCanvas produced no stroke");
        }

        // ---- shared ---------------------------------------------------------------------------

        private static void DrawWithPen(PenInjector pen, InputWindow probe, InkCanvas canvas,
                                        double fromX, double toX, double y, int steps,
                                        bool varyPressure = false, bool eraser = false)
        {
            (int sx, int sy) = ElementPointToScreen(probe, canvas, fromX, y);

            pen.Hover(sx, sy, eraser);
            probe.Pump(TimeSpan.FromMilliseconds(100));
            pen.Down(sx, sy, pressure: varyPressure ? 120u : 500u, eraser: eraser);
            probe.Pump(TimeSpan.FromMilliseconds(100));

            for (int step = 1; step <= steps; step++)
            {
                (int mx, int my) = ElementPointToScreen(probe, canvas, fromX + (toX - fromX) * step / steps, y);
                uint pressure = varyPressure ? (uint)(120 + 880.0 * step / steps) : 500u;
                pen.Move(mx, my, pressure, eraser: eraser);
                probe.Pump(TimeSpan.FromMilliseconds(40));
            }

            (int ex, int ey) = ElementPointToScreen(probe, canvas, toX, y);
            pen.Up(ex, ey, eraser);
            probe.Pump(TimeSpan.FromMilliseconds(80));
            pen.Leave(ex, ey);
            probe.Pump(TimeSpan.FromMilliseconds(250));
        }

        private static (int X, int Y) ElementPointToScreen(InputWindow probe, UIElement element, double x, double y)
            => UiThread.Invoke(() =>
            {
                Point screen = element.PointToScreen(new Point(x, y));
                return ((int)Math.Round(screen.X), (int)Math.Round(screen.Y));
            });
    }
}
