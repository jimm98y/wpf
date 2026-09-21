// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Does a finger on the screen become a WPF touch event?
//
// This is the question nothing in this repo asked before. The port replaced rendering, windowing
// and the HWND plumbing; the touch stack sits on top of all three, and its failure mode is that
// an app simply does not respond to being touched -- no exception, no visual difference, nothing a
// pixel or layout test would notice.
//
// The tests climb the stack in order, so a failure says which link broke:
//   raw contact -> TouchDown -> position -> multi-touch -> capture -> promotion to mouse ->
//   the TabletDevice the app can enumerate.
//

using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Xunit;

namespace Wpf.Input.Tests
{
    public class TouchTests
    {
        /// <summary>
        /// The whole chain, at its shortest: one contact goes into the system's pointer queue and
        /// WPF raises TouchDown on the element under it.
        /// </summary>
        [Fact]
        public void ATouchContactRaisesTouchDown()
        {
            using var injector = new TouchInjector();
            Assert.SkipUnless(injector.TryInitialize(), "this session does not accept injected touch");

            using var probe = InputWindow.Show();
            var log = new List<string>();

            UiThread.Invoke(() =>
            {
                probe.Surface.TouchDown += (s, e) => log.Add("down");
                probe.Surface.TouchMove += (s, e) => log.Add("move");
                probe.Surface.TouchUp += (s, e) => log.Add("up");
            });

            (int x, int y) = probe.Centre;

            Assert.True(injector.Down(new Contact(0, x, y)), "injecting the contact failed");
            probe.PumpUntil(() => log.Contains("down"), TimeSpan.FromSeconds(2));

            injector.Up(0);
            probe.PumpUntil(() => log.Contains("up"), TimeSpan.FromSeconds(2));

            Assert.True(log.Contains("down"),
                $"a touch contact over the window raised no TouchDown; WPF saw [{string.Join(", ", log)}]");
            Assert.True(log.Contains("up"),
                $"the contact was lifted but no TouchUp arrived; WPF saw [{string.Join(", ", log)}]");
        }

        /// <summary>
        /// The touch lands where the finger is. A stack that delivers events but mis-maps the
        /// coordinates is arguably worse than one that delivers nothing: every control responds, but
        /// to the wrong place, and only on a scaled display.
        /// </summary>
        [Fact]
        public void ATouchReportsThePositionItWasInjectedAt()
        {
            using var injector = new TouchInjector();
            Assert.SkipUnless(injector.TryInitialize(), "this session does not accept injected touch");

            using var probe = InputWindow.Show();
            Point? reported = null;

            UiThread.Invoke(() =>
            {
                probe.Surface.TouchDown += (s, e) => reported ??= e.GetTouchPoint(probe.Surface).Position;
            });

            // A point deliberately off-centre and asymmetric, so a transposed or halved coordinate
            // cannot accidentally match.
            const double expectedX = 120, expectedY = 65;
            (int x, int y) = probe.SurfacePointToScreen(expectedX, expectedY);

            injector.Down(new Contact(0, x, y));
            probe.PumpUntil(() => reported is not null, TimeSpan.FromSeconds(2));
            injector.Up(0);
            probe.Pump(TimeSpan.FromMilliseconds(200));

            Assert.True(reported is not null, "no TouchDown arrived, so there was no position to check");

            // Two device pixels of slack: the injected point is rounded to integer screen pixels on
            // the way in and scaled back to device-independent units on the way out.
            Assert.True(Math.Abs(reported!.Value.X - expectedX) <= 2 && Math.Abs(reported.Value.Y - expectedY) <= 2,
                $"the touch was injected over ({expectedX}, {expectedY}) in the element but WPF reported {reported}");
        }

        /// <summary>
        /// Two fingers are two touches. WPF models each contact as its own TouchDevice, and
        /// everything above -- manipulation, per-finger capture, ink from more than one stylus -- is
        /// built on that. A stack that collapses multi-touch to the primary contact still passes
        /// every single-finger test here.
        /// </summary>
        [Fact]
        public void TwoContactsRaiseTwoDistinctTouchDevices()
        {
            using var injector = new TouchInjector();
            Assert.SkipUnless(injector.TryInitialize(), "this session does not accept injected touch");

            using var probe = InputWindow.Show();
            var deviceIds = new HashSet<int>();

            UiThread.Invoke(() =>
            {
                probe.Surface.TouchDown += (s, e) => deviceIds.Add(e.TouchDevice.Id);
            });

            (int x1, int y1) = probe.SurfacePointToScreen(100, 100);
            (int x2, int y2) = probe.SurfacePointToScreen(260, 180);

            // Down together, in one frame: this is what a real two-finger gesture looks like to the
            // system, and injecting them in separate frames would not exercise the same path.
            injector.Down(new Contact(0, x1, y1), new Contact(1, x2, y2));
            probe.PumpUntil(() => deviceIds.Count >= 2, TimeSpan.FromSeconds(2));
            injector.Up(0, 1);
            probe.Pump(TimeSpan.FromMilliseconds(200));

            Assert.True(deviceIds.Count >= 2,
                $"two simultaneous contacts produced {deviceIds.Count} TouchDevice(s); multi-touch is collapsing to one");
        }

        /// <summary>
        /// A finger that moves keeps reporting. TouchMove is what every drag, pan and ink stroke is
        /// made of, and it is the event most likely to be lost to a message the provider does not
        /// handle (WM_POINTERUPDATE) rather than to a broken device.
        /// </summary>
        [Fact]
        public void ADraggedContactRaisesTouchMove()
        {
            using var injector = new TouchInjector();
            Assert.SkipUnless(injector.TryInitialize(), "this session does not accept injected touch");

            using var probe = InputWindow.Show();
            var positions = new List<Point>();

            UiThread.Invoke(() =>
            {
                probe.Surface.TouchMove += (s, e) => positions.Add(e.GetTouchPoint(probe.Surface).Position);
            });

            // The path is described in ELEMENT coordinates and converted per step. Injecting a fixed
            // number of screen pixels instead would make the distance WPF reports depend on the
            // display's scale factor, and the test would quietly assert something different on every
            // machine.
            const double startX = 80, endX = 240, y0 = 80;
            const int steps = 8;

            (int sx, int sy) = probe.SurfacePointToScreen(startX, y0);
            injector.Down(new Contact(0, sx, sy));
            probe.Pump(TimeSpan.FromMilliseconds(120));

            for (int step = 1; step <= steps; step++)
            {
                (int mx, int my) = probe.SurfacePointToScreen(startX + (endX - startX) * step / steps, y0);
                injector.Move(new Contact(0, mx, my));
                probe.Pump(TimeSpan.FromMilliseconds(40));
            }

            injector.Up(0);
            probe.Pump(TimeSpan.FromMilliseconds(200));

            Assert.True(positions.Count >= 4,
                $"a finger dragged across the element raised only {positions.Count} TouchMove events");
            Assert.True(Math.Abs(positions[^1].X - endX) <= 3,
                $"TouchMove positions did not track the finger to {endX}: first {positions[0]}, last {positions[^1]}");
        }

        /// <summary>
        /// Tapping a Button clicks it.
        ///
        /// This is the single most important behaviour on the whole list, and it does NOT go through
        /// the touch events above: WPF's controls are driven by the mouse, and a touch reaches them
        /// only because the primary contact is promoted to mouse input. In the WM_POINTER stack that
        /// promotion is Windows' own, done by DefWindowProc for messages the app leaves unhandled --
        /// so an over-eager provider that marks pointer messages handled breaks every control in the
        /// product while leaving TouchDown working perfectly.
        /// </summary>
        [Fact]
        public void TappingAButtonClicksIt()
        {
            using var injector = new TouchInjector();
            Assert.SkipUnless(injector.TryInitialize(), "this session does not accept injected touch");

            using var probe = InputWindow.Show();
            int clicks = 0;
            Button? button = null;

            UiThread.Invoke(() =>
            {
                button = new Button
                {
                    Content = "tap me",
                    Width = 200,
                    Height = 80,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                button.Click += (s, e) => clicks++;
                probe.Surface.Child = button;
            });
            probe.Pump(TimeSpan.FromMilliseconds(300));

            (int x, int y) = UiThread.Invoke(() =>
            {
                Point centre = button!.PointToScreen(new Point(button.ActualWidth / 2, button.ActualHeight / 2));
                return ((int)Math.Round(centre.X), (int)Math.Round(centre.Y));
            });

            injector.Down(new Contact(0, x, y));
            probe.Pump(TimeSpan.FromMilliseconds(150));
            injector.Up(0);
            probe.PumpUntil(() => clicks > 0, TimeSpan.FromSeconds(2));

            Assert.True(clicks > 0,
                "tapping a Button raised no Click: touch is not being promoted to mouse input, so no WPF control responds to touch");
        }

        /// <summary>
        /// The digitizer is visible to the application. Apps branch on this -- showing touch-sized
        /// chrome, enabling panning, choosing an on-screen keyboard -- and Tablet.TabletDevices is
        /// how they ask. It is also the one part of the stack that runs before any input arrives, so
        /// it fails on a machine that is never touched.
        /// </summary>
        [Fact]
        public void TheTouchDigitizerIsEnumerable()
        {
            using var injector = new TouchInjector();
            Assert.SkipUnless(injector.TryInitialize(), "this session does not accept injected touch");

            using var probe = InputWindow.Show();

            // Injection creates the device lazily, so touch once before asking.
            (int x, int y) = probe.Centre;
            injector.Tap(x, y);
            probe.Pump(TimeSpan.FromMilliseconds(400));

            var kinds = UiThread.Invoke(() =>
            {
                var found = new List<string>();
                foreach (TabletDevice device in Tablet.TabletDevices) found.Add(device.Type.ToString());
                return found;
            });

            Assert.True(kinds.Contains(nameof(TabletDeviceType.Touch)),
                $"no touch digitizer is enumerable through Tablet.TabletDevices; it reported [{string.Join(", ", kinds)}]");
        }
    }
}
