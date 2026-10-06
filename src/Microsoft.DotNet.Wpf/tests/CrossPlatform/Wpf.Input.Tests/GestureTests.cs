// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The system gestures: the ones Windows recognises for us.
//
// Tap, double-tap, press-and-hold and flick are not computed by WPF. They come out of the
// InteractionContext in ninput.dll, which the pointer stack feeds raw packets and reads decoded
// gestures back from (PointerInteractionEngine). WPF then turns them into StylusSystemGesture and,
// for press-and-hold, into the right-click that opens a context menu.
//
// That matters here because it is the one part of the touch path with a native dependency other
// than user32, and because press-and-hold IS right-click on a touch screen: without it there is no
// way to open a context menu with a finger at all.
//

using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Xunit;

namespace Wpf.Input.Tests
{
    public class GestureTests
    {
        /// <summary>
        /// A finger touched down and lifted without moving is a tap.
        /// </summary>
        [Fact]
        public void AStationaryContactIsRecognisedAsATap()
        {
            using var injector = new TouchInjector();
            Assert.SkipUnless(injector.TryInitialize(), "this session does not accept injected touch");

            using var probe = InputWindow.Show();
            var gestures = new List<SystemGesture>();

            UiThread.Invoke(() =>
            {
                probe.Surface.StylusSystemGesture += (s, e) => gestures.Add(e.SystemGesture);
            });

            (int x, int y) = probe.Centre;
            injector.Down(new Contact(0, x, y));
            probe.Pump(TimeSpan.FromMilliseconds(120));
            injector.Up(0);
            probe.PumpUntil(() => gestures.Contains(SystemGesture.Tap), TimeSpan.FromSeconds(2));

            Assert.Contains(SystemGesture.Tap, gestures);
        }

        /// <summary>
        /// A finger held still opens a context menu.
        ///
        /// This is the touch right-click. It travels the longest road in the touch stack: the
        /// InteractionContext decides it is a hold, the pointer stack turns that into a RightTap
        /// system gesture, and WPF promotes it to a right mouse button so ContextMenu opens -- so a
        /// failure here can be in any of the three, but the symptom is always the same, that a touch
        /// user cannot reach a context menu.
        /// </summary>
        [Fact]
        public void PressAndHoldOpensAContextMenu()
        {
            using var injector = new TouchInjector();
            Assert.SkipUnless(injector.TryInitialize(), "this session does not accept injected touch");

            using var probe = InputWindow.Show();
            bool opened = false;

            UiThread.Invoke(() =>
            {
                var menu = new ContextMenu();
                menu.Items.Add(new MenuItem { Header = "an item" });
                menu.Opened += (s, e) => opened = true;
                probe.Surface.ContextMenu = menu;
            });
            probe.Pump(TimeSpan.FromMilliseconds(200));

            (int x, int y) = probe.Centre;
            injector.Down(new Contact(0, x, y));

            // Held, not slept through: an injected contact that stops being refreshed is aged out by
            // the system and never becomes a hold.
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (clock.Elapsed < TimeSpan.FromSeconds(2) && !opened)
            {
                injector.Hold();
                probe.Pump(TimeSpan.FromMilliseconds(50));
            }

            injector.Up(0);
            probe.PumpUntil(() => opened, TimeSpan.FromSeconds(1));

            // Leave nothing open behind us.
            UiThread.Invoke(() => { if (probe.Surface.ContextMenu is not null) probe.Surface.ContextMenu.IsOpen = false; });
            probe.Pump(TimeSpan.FromMilliseconds(100));

            Assert.True(opened,
                "holding a finger still on an element never opened its ContextMenu, so there is no way to right-click by touch");
        }

        /// <summary>
        /// A touch can be captured. Capture is how every dragging control -- a Slider thumb, a
        /// scrollbar, a ListBox being rubber-banded -- keeps receiving a contact after it leaves the
        /// element's bounds, and it is per-TouchDevice rather than global as the mouse's is.
        /// </summary>
        [Fact]
        public void ATouchDeviceCanBeCapturedAndKeepsReporting()
        {
            using var injector = new TouchInjector();
            Assert.SkipUnless(injector.TryInitialize(), "this session does not accept injected touch");

            using var probe = InputWindow.Show();
            Border? target = null;
            bool captured = false;
            int movesAfterLeaving = 0;

            UiThread.Invoke(() =>
            {
                // A small element in the corner, so the finger can be dragged clear off it.
                target = new Border
                {
                    Background = System.Windows.Media.Brushes.LightGray,
                    Width = 120,
                    Height = 90,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Top,
                };
                target.TouchDown += (s, e) => captured = e.TouchDevice.Capture(target);
                target.TouchMove += (s, e) =>
                {
                    Point p = e.GetTouchPoint(target).Position;
                    if (p.X > target.ActualWidth || p.Y > target.ActualHeight) movesAfterLeaving++;
                };
                probe.Surface.Child = target;
            });
            probe.Pump(TimeSpan.FromMilliseconds(300));

            (int sx, int sy) = UiThread.Invoke(() =>
            {
                Point p = target!.PointToScreen(new Point(60, 45));
                return ((int)Math.Round(p.X), (int)Math.Round(p.Y));
            });

            injector.Down(new Contact(0, sx, sy));
            probe.Pump(TimeSpan.FromMilliseconds(120));

            // Straight out of the element, well past its bottom-right corner.
            for (int step = 1; step <= 8; step++)
            {
                (int mx, int my) = probe.SurfacePointToScreen(120 + step * 20, 100 + step * 15);
                injector.Move(new Contact(0, mx, my));
                probe.Pump(TimeSpan.FromMilliseconds(40));
            }

            injector.Up(0);
            probe.Pump(TimeSpan.FromMilliseconds(200));

            Assert.True(captured, "TouchDevice.Capture returned false, so no control can drag with a finger");
            Assert.True(movesAfterLeaving > 0,
                "the captured element stopped receiving TouchMove once the finger left its bounds, so a drag would break the moment it strayed");
        }
    }
}
