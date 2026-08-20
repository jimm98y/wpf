// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Does the framework find out what the head did?
//
// Setting a window state was only ever half of it. WPF learns a window's state from ONE place --
// the wParam of the WM_SIZE it gets when the state changes, which Window.WmSizeChanged switches on
// to update WindowState and raise StateChanged. Off Windows that message is synthesised by
// HwndWrapper from the head's resize notification, and the wParam was a hardcoded SIZE_RESTORED.
//
// So the head maximized the window, correctly, and the framework was never told. WindowState stayed
// Normal after the user hit the zoom button; StateChanged did not fire for any transition; and
// WindowChrome, which watches for SIZE_MAXIMIZED to pad a custom-chromed window away from the screen
// edge, never adjusted. Wpf.Platform.Tests could not have caught it: the head was doing its job.
//
// These tests therefore assert the seam and not either side of it. They drive a real Window through
// its public API and ask what the framework believes afterwards.
//

using System;
using System.Collections.Generic;
using System.Windows;
using Xunit;

namespace Wpf.Window.Tests
{
    public class WindowStateRoundTripTests
    {
        /// <summary>True where a real top-level window can be shown at all.</summary>
        private static bool DisplayAvailable =>
            OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"))
            || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"));

        /// <summary>
        /// Maximizing a window has to reach the framework: WindowState stays put and StateChanged
        /// fires. Before the head could report its state, the synthesised WM_SIZE always said
        /// SIZE_RESTORED and StateChanged fired for NOTHING -- not for maximize, not for the restore
        /// afterwards.
        /// </summary>
        [Fact]
        public void MaximizingAndRestoringRaiseStateChanged()
        {
            Assert.SkipUnless(DisplayAvailable, "requires a display server");

            var seen = new List<WindowState>();
            System.Windows.Window? window = null;

            try
            {
                window = UiThread.Invoke(() =>
                {
                    var w = new System.Windows.Window
                    {
                        Title = "Wpf.Window.Tests state",
                        Width = 420,
                        Height = 320,
                        WindowStartupLocation = WindowStartupLocation.CenterScreen,
                    };
                    w.StateChanged += (_, _) => seen.Add(w.WindowState);
                    w.Show();
                    return w;
                });

                // Settle: showing a window produces resize traffic of its own, and anything it raised
                // belongs to the Show rather than to the transitions under test.
                UiThread.WaitUntil(() => false, 300);
                seen.Clear();

                UiThread.Invoke(() => window!.WindowState = WindowState.Maximized);
                Assert.True(UiThread.WaitUntil(() => seen.Contains(WindowState.Maximized)),
                    "StateChanged never reported Maximized. The head is told to maximize by " +
                    "WindowState's property-changed callback, so the window itself is maximized -- " +
                    "what is missing is the report coming back, through the WM_SIZE wParam.");

                Assert.Equal(WindowState.Maximized, UiThread.Invoke(() => window!.WindowState));

                UiThread.Invoke(() => window!.WindowState = WindowState.Normal);
                Assert.True(UiThread.WaitUntil(() => seen.Contains(WindowState.Normal)),
                    "StateChanged never reported the restore back to Normal.");

                Assert.Equal(WindowState.Normal, UiThread.Invoke(() => window!.WindowState));
            }
            finally
            {
                if (window is not null)
                {
                    UiThread.Invoke(() => { try { window.Close(); } catch { } });
                }
            }
        }

        /// <summary>
        /// RestoreBounds is where the window goes back to, so maximizing must not change it. That is
        /// the entire point of the property -- an application persists it to reopen where the user
        /// left them -- and off Windows it was fabricated: GetWindowPlacement reported the CURRENT
        /// size at the origin, so a maximized window said its restore rect was the whole screen.
        /// </summary>
        [Fact]
        public void RestoreBoundsSurvivesMaximizing()
        {
            Assert.SkipUnless(DisplayAvailable, "requires a display server");

            System.Windows.Window? window = null;
            try
            {
                window = UiThread.Invoke(() =>
                {
                    var w = new System.Windows.Window
                    {
                        Title = "Wpf.Window.Tests restore bounds",
                        Width = 420,
                        Height = 320,
                    };
                    w.Show();
                    return w;
                });

                // The head samples its restore rect while the window is sitting still, so let it.
                UiThread.WaitUntil(() => false, 500);

                Rect normal = UiThread.Invoke(() => window!.RestoreBounds);

                // The SIZE has to be the window's own, not its client area: RestoreBounds used to come
                // back 410x282 for this window because it was built from the content size.
                Assert.Equal(420, normal.Width, 2);
                Assert.Equal(320, normal.Height, 2);

                UiThread.Invoke(() => window!.WindowState = WindowState.Maximized);
                Assert.True(UiThread.WaitUntil(() => window!.WindowState == WindowState.Maximized),
                    "the window never reported itself maximized");
                UiThread.WaitUntil(() => false, 400);

                Rect maximized = UiThread.Invoke(() => window!.RestoreBounds);

                Assert.True(maximized == normal,
                    $"RestoreBounds changed when the window was maximized: {normal} became {maximized}. " +
                    "It is meant to be the rect the window will return to, which is the one it had " +
                    "before -- reporting the maximized rect makes it useless for the one job it has.");
            }
            finally
            {
                if (window is not null)
                {
                    UiThread.Invoke(() => { try { window.Close(); } catch { } });
                }
            }
        }

        /// <summary>
        /// A window that OPENS maximized must report itself maximized once it is up.
        /// </summary>
        /// <remarks>
        /// Worth separating from the transition above because it takes a different route: the state
        /// is set before there is an hwnd at all, so it is applied during Show rather than by a
        /// property-changed callback on a live window. That is also the shape that used to leave a
        /// window built, surfaced, composited into -- and never ordered onto the screen, which is
        /// the regression WindowStateShowTests was written for.
        /// </remarks>
        [Fact]
        public void AWindowShownMaximizedReportsItself()
        {
            Assert.SkipUnless(DisplayAvailable, "requires a display server");

            System.Windows.Window? window = null;
            try
            {
                window = UiThread.Invoke(() =>
                {
                    var w = new System.Windows.Window
                    {
                        Title = "Wpf.Window.Tests shown maximized",
                        Width = 420,
                        Height = 320,
                        WindowState = WindowState.Maximized,
                    };
                    w.Show();
                    return w;
                });

                Assert.True(UiThread.WaitUntil(() => window!.WindowState == WindowState.Maximized),
                    $"a window shown with WindowState=Maximized reports " +
                    $"{UiThread.Invoke(() => window!.WindowState)}");

                // And it is actually big: a window that reports maximized while sitting at its
                // requested 420x320 would satisfy the property check and nothing else.
                double width = UiThread.Invoke(() => window!.ActualWidth);
                Assert.True(width > 420,
                    $"reports Maximized but is still {width:0} DIPs wide, its unmaximized width");
            }
            finally
            {
                if (window is not null)
                {
                    UiThread.Invoke(() => { try { window.Close(); } catch { } });
                }
            }
        }
    }
}
