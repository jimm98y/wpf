// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Gestures: what an application actually consumes.
//
// Almost no app handles TouchDown. Apps handle manipulation -- pan, pinch, rotate, and the inertia
// that follows a flick -- and they scroll by dragging a ScrollViewer. That layer is pure managed
// code and platform-agnostic, so it is easy to assume it works; but it is fed by TouchDevice, which
// is fed by the stylus stack, and it also depends on something the port DID replace: the
// manipulation processor advances on CompositionTarget.Rendering, so a compositor that does not
// tick produces a manipulation that starts and then never moves.
//

using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Xunit;

namespace Wpf.Input.Tests
{
    public class ManipulationTests
    {
        /// <summary>
        /// One finger dragged across a manipulation-enabled element produces translation deltas.
        /// </summary>
        [Fact]
        public void ADragProducesManipulationTranslation()
        {
            using var injector = new TouchInjector();
            Assert.SkipUnless(injector.TryInitialize(), "this session does not accept injected touch");

            using var probe = InputWindow.Show();
            bool started = false;
            double translated = 0;

            UiThread.Invoke(() =>
            {
                probe.Surface.IsManipulationEnabled = true;
                probe.Surface.ManipulationStarted += (s, e) => started = true;
                probe.Surface.ManipulationDelta += (s, e) => translated += e.DeltaManipulation.Translation.X;
            });

            Drag(injector, probe, fromX: 80, toX: 300, y: 150, steps: 10, settleMs: 45);

            Assert.True(started, "dragging a finger across a manipulation-enabled element never started a manipulation");
            Assert.True(translated > 100,
                $"the manipulation started but barely moved: total X translation was {translated:0.#} for a 220px drag");
        }

        /// <summary>
        /// Two fingers moving apart is a pinch, and a pinch is a SCALE. This is the one gesture that
        /// cannot be faked from a single promoted mouse: it needs both contacts alive at once, which
        /// is why it is worth its own test even though the pan above already proves manipulation runs.
        /// </summary>
        [Fact]
        public void TwoFingersMovingApartProduceScale()
        {
            using var injector = new TouchInjector();
            Assert.SkipUnless(injector.TryInitialize(), "this session does not accept injected touch");

            using var probe = InputWindow.Show();
            double scale = 1;

            UiThread.Invoke(() =>
            {
                probe.Surface.IsManipulationEnabled = true;
                probe.Surface.ManipulationDelta += (s, e) => scale *= e.DeltaManipulation.Scale.X;
            });

            const double y = 150;
            const double startGap = 40, endGap = 180;
            const int steps = 10;

            (int lx, int ly) = probe.SurfacePointToScreen(200 - startGap, y);
            (int rx, int ry) = probe.SurfacePointToScreen(200 + startGap, y);
            injector.Down(new Contact(0, lx, ly), new Contact(1, rx, ry));
            probe.Pump(TimeSpan.FromMilliseconds(120));

            for (int step = 1; step <= steps; step++)
            {
                double gap = startGap + (endGap - startGap) * step / steps;
                (int a, int ay) = probe.SurfacePointToScreen(200 - gap, y);
                (int b, int by) = probe.SurfacePointToScreen(200 + gap, y);
                injector.Move(new Contact(0, a, ay), new Contact(1, b, by));
                probe.Pump(TimeSpan.FromMilliseconds(45));
            }

            injector.Up(0, 1);
            probe.Pump(TimeSpan.FromMilliseconds(200));

            // The gap grows 4.5x; anything meaningfully above 1 proves scale is being computed and
            // reported, without pinning the test to the processor's exact smoothing.
            Assert.True(scale > 1.5,
                $"spreading two fingers from a {startGap * 2}px gap to {endGap * 2}px reported a cumulative scale of {scale:0.###}");
        }

        /// <summary>
        /// A flick keeps going after the finger leaves. Inertia is what makes touch scrolling feel
        /// like touch rather than like a mouse, and it is a separate processor from the manipulation
        /// itself -- it can be missing while pan and pinch both work.
        /// </summary>
        [Fact]
        public void AFlickStartsInertia()
        {
            using var injector = new TouchInjector();
            Assert.SkipUnless(injector.TryInitialize(), "this session does not accept injected touch");

            using var probe = InputWindow.Show();
            bool inertiaStarting = false;
            bool deltaDuringInertia = false;

            UiThread.Invoke(() =>
            {
                probe.Surface.IsManipulationEnabled = true;
                probe.Surface.ManipulationInertiaStarting += (s, e) =>
                {
                    inertiaStarting = true;
                    e.TranslationBehavior.DesiredDeceleration = 0.001;
                };
                probe.Surface.ManipulationDelta += (s, e) =>
                {
                    if (e.IsInertial) deltaDuringInertia = true;
                };
            });

            // Fast and short-settled: velocity at lift is what decides whether inertia is worth
            // starting, so the steps are deliberately barely pumped.
            Drag(injector, probe, fromX: 60, toX: 380, y: 150, steps: 8, settleMs: 12);
            probe.PumpUntil(() => deltaDuringInertia, TimeSpan.FromSeconds(2));

            Assert.True(inertiaStarting, "flicking a finger across the element never raised ManipulationInertiaStarting");
            Assert.True(deltaDuringInertia, "inertia was announced but no inertial ManipulationDelta ever followed");
        }

        /// <summary>
        /// A ScrollViewer scrolls when dragged with a finger.
        ///
        /// This is the gesture users actually make, and the one with the most machinery behind it:
        /// PanningMode turns the touch into a manipulation, ScrollViewer consumes the manipulation
        /// and moves its offset, and the whole thing is opt-in, so it is also the easiest to have
        /// never worked without anyone noticing.
        /// </summary>
        [Fact]
        public void AScrollViewerPansWithAFinger()
        {
            using var injector = new TouchInjector();
            Assert.SkipUnless(injector.TryInitialize(), "this session does not accept injected touch");

            using var probe = InputWindow.Show();
            ScrollViewer? scroller = null;

            UiThread.Invoke(() =>
            {
                var tall = new StackPanel();
                for (int i = 0; i < 60; i++)
                {
                    tall.Children.Add(new TextBlock { Text = $"line {i}", FontSize = 20, Margin = new Thickness(4) });
                }

                scroller = new ScrollViewer
                {
                    Content = tall,
                    PanningMode = PanningMode.VerticalOnly,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                };
                probe.Surface.Child = scroller;
            });
            probe.Pump(TimeSpan.FromMilliseconds(400));

            // Drag UP to scroll DOWN, as a finger does.
            DragVertical(injector, probe, x: 200, fromY: 260, toY: 60, steps: 10, settleMs: 45);
            probe.Pump(TimeSpan.FromMilliseconds(300));

            double offset = UiThread.Invoke(() => scroller!.VerticalOffset);

            Assert.True(offset > 20,
                $"dragging a finger up a ScrollViewer with PanningMode.VerticalOnly moved it to offset {offset:0.#}; touch panning is not reaching it");
        }

        // ---- shared gestures ----------------------------------------------------------------

        private static void Drag(TouchInjector injector, InputWindow probe,
                                 double fromX, double toX, double y, int steps, int settleMs)
        {
            (int sx, int sy) = probe.SurfacePointToScreen(fromX, y);
            injector.Down(new Contact(0, sx, sy));
            probe.Pump(TimeSpan.FromMilliseconds(80));

            for (int step = 1; step <= steps; step++)
            {
                (int mx, int my) = probe.SurfacePointToScreen(fromX + (toX - fromX) * step / steps, y);
                injector.Move(new Contact(0, mx, my));
                probe.Pump(TimeSpan.FromMilliseconds(settleMs));
            }

            injector.Up(0);
            probe.Pump(TimeSpan.FromMilliseconds(150));
        }

        private static void DragVertical(TouchInjector injector, InputWindow probe,
                                         double x, double fromY, double toY, int steps, int settleMs)
        {
            (int sx, int sy) = probe.SurfacePointToScreen(x, fromY);
            injector.Down(new Contact(0, sx, sy));
            probe.Pump(TimeSpan.FromMilliseconds(80));

            for (int step = 1; step <= steps; step++)
            {
                (int mx, int my) = probe.SurfacePointToScreen(x, fromY + (toY - fromY) * step / steps);
                injector.Move(new Contact(0, mx, my));
                probe.Pump(TimeSpan.FromMilliseconds(settleMs));
            }

            injector.Up(0);
            probe.Pump(TimeSpan.FromMilliseconds(150));
        }
    }
}
