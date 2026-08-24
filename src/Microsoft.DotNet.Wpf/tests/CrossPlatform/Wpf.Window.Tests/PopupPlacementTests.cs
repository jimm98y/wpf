// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// A popup lands where its placement target says, in SCREEN coordinates.
//
// Here rather than in Wpf.Platform.Tests because the arithmetic is WPF's, not the head's: Popup asks
// the window where it is (SafeNativeMethods.GetWindowRect) and converts the target's client point to
// the screen (PointUtil.ClientToScreen), and those two answers have to agree with each other.
//
// Off Windows they did not. GetWindowRect reported every window at the origin while ClientToScreen
// reported the truth, so the two disagreed by exactly the window's position -- invisible for a window
// at the top-left corner, and wrong by hundreds of pixels for any other. This pins the agreement
// down, on a window deliberately placed away from the corner.
//

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Xunit;

namespace Wpf.Window.Tests
{
    public class PopupPlacementTests
    {
        private static bool DisplayAvailable =>
            OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"))
            || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"));

        [Fact]
        public void APopupOpensAtItsPlacementTarget()
        {
            Assert.SkipUnless(DisplayAvailable, "requires a display server");

            System.Windows.Window? window = null;
            try
            {
                Border target = null!;
                Border content = null!;
                Popup popup = null!;

                window = UiThread.Invoke(() =>
                {
                    target = new Border
                    {
                        Width = 120,
                        Height = 40,
                        Background = Brushes.LightGray,
                        HorizontalAlignment = HorizontalAlignment.Left,
                        VerticalAlignment = VerticalAlignment.Top,
                        Margin = new Thickness(60, 50, 0, 0),
                    };
                    content = new Border { Width = 150, Height = 80, Background = Brushes.White };
                    popup = new Popup
                    {
                        PlacementTarget = target,
                        Placement = PlacementMode.Bottom,
                        Child = content,
                    };

                    var w = new System.Windows.Window
                    {
                        Title = "Wpf.Window.Tests popup placement",
                        Width = 480,
                        Height = 360,
                        // Away from the corner ON PURPOSE: at (0,0) a window-origin bug cancels out
                        // and the test would pass while the arithmetic was wrong.
                        WindowStartupLocation = WindowStartupLocation.Manual,
                        Left = 220,
                        Top = 160,
                        Content = new Grid { Children = { target, popup } },
                    };
                    w.Show();
                    return w;
                });

                UiThread.WaitUntil(() => false, 500);
                UiThread.Invoke(() => popup.IsOpen = true);
                Assert.True(UiThread.WaitUntil(() => content.IsVisible && content.ActualWidth > 0),
                    "the popup never became visible");
                UiThread.WaitUntil(() => false, 300);

                (Point expected, Point actual) = UiThread.Invoke(() =>
                    (target.PointToScreen(new Point(0, target.ActualHeight)),
                     content.PointToScreen(new Point(0, 0))));

                Assert.True(Math.Abs(expected.X - actual.X) <= 2 && Math.Abs(expected.Y - actual.Y) <= 2,
                    $"a Bottom-placed popup should start at its target's bottom-left {expected}, "
                    + $"but it is at {actual}. A disagreement the size of the window's own position "
                    + "means the window rect and the client-to-screen conversion are using different "
                    + "origins.");
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
