// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The window under inspection, and the one endpoint every test shares.
//
// Shared because DevToolsServer.Start is once per process by design -- it is
// called from HwndSource, which runs for every window an app opens -- so the
// suite starts it once, on port 0 so the OS picks a free one rather than the
// tests racing whatever else is on 9222.
//

using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Microsoft.Wpf.DevTools;

namespace Wpf.DevTools.Tests
{
    internal static class TestApp
    {
        private static readonly object s_gate = new object();
        private static bool s_started;

        /// <summary>Known geometry, so the box-model assertions have something exact to check.</summary>
        internal const double ButtonWidth = 120;
        internal const double ButtonHeight = 40;
        internal static readonly Thickness ButtonMargin = new Thickness(10, 20, 30, 40);
        internal const string ButtonName = "InspectorTestButton";
        internal const string ButtonText = "PickMe";
        internal const string WindowTitle = "DevTools Test Window";

        /// <summary>True once the scroll viewer has content taller than its viewport.</summary>
        /// <summary>
        /// The centre of the vertical scrollbar's thumb, in window coordinates, or (0,0) when
        /// the scrollbar has not been realised.
        /// </summary>
        internal static Point VerticalThumbCentre()
        {
            foreach (Thumb thumb in Descendants<Thumb>(Scroller!))
            {
                if (thumb.ActualHeight <= 0 || thumb.ActualWidth <= 0)
                    continue;

                Point origin = thumb.TransformToAncestor(Window!).Transform(new Point(0, 0));
                return new Point(origin.X + thumb.ActualWidth / 2, origin.Y + thumb.ActualHeight / 2);
            }

            return new Point(0, 0);
        }

        private static IEnumerable<T> Descendants<T>(DependencyObject node) where T : DependencyObject
        {
            int count = VisualTreeHelper.GetChildrenCount(node);
            for (int i = 0; i < count; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(node, i);
                if (child is T match)
                    yield return match;

                foreach (T deeper in Descendants<T>(child))
                    yield return deeper;
            }
        }

        /// <summary>A point inside the text box, `fraction` of the way across it.</summary>
        internal static Point TextPointAt(double fraction)
        {
            Point origin = Text!.TransformToAncestor(Window!).Transform(new Point(0, 0));
            return new Point(origin.X + Text.ActualWidth * fraction, origin.Y + Text.ActualHeight / 2);
        }

        internal static bool EnsureScrollable()
            => UiThread.WaitUntil(() => Scroller is { ScrollableHeight: > 0 });

        internal static bool DisplayAvailable =>
            OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"))
            || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"));

        internal static Window? Window { get; private set; }

        internal static Button? Button { get; private set; }

        /// <summary>Counts real Click events, so the input tests can prove one happened.</summary>
        internal static int ClickCount;

        /// <summary>A scroll viewer with more content than fits, for the wheel test.</summary>
        internal static ScrollViewer? Scroller { get; private set; }

        /// <summary>A text box with enough text to drag a selection across.</summary>
        internal static TextBox? Text { get; private set; }

        internal static Point ScrollerCentre { get; private set; }

        /// <summary>Show the window, start the endpoint, and return its port.</summary>
        internal static int Start()
        {
            lock (s_gate)
            {
                if (s_started)
                    return DevToolsServer.ListeningPort;

                UiThread.Invoke(() =>
                {
                    var button = new Button
                    {
                        Name = ButtonName,
                        Width = ButtonWidth,
                        Height = ButtonHeight,
                        Margin = ButtonMargin,
                        Content = ButtonText,
                        HorizontalAlignment = HorizontalAlignment.Left,
                        VerticalAlignment = VerticalAlignment.Top,
                        Background = Brushes.Salmon,
                    };

                    // Deliberately taller than its viewport, so there is somewhere to scroll to.
                    var tall = new StackPanel();
                    for (int i = 0; i < 40; i++)
                        tall.Children.Add(new TextBlock { Text = "row " + i, Height = 20 });

                    var scroller = new ScrollViewer
                    {
                        Content = tall,
                        Width = 200,
                        Height = 100,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        VerticalAlignment = VerticalAlignment.Bottom,
                        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    };

                    var text = new TextBox
                    {
                        Text = "the quick brown fox jumps over the lazy dog",
                        Width = 260,
                        Height = 24,
                        HorizontalAlignment = HorizontalAlignment.Left,
                        VerticalAlignment = VerticalAlignment.Bottom,
                    };

                    var window = new Window
                    {
                        Title = WindowTitle,
                        Width = 400,
                        Height = 300,
                        Content = new Grid { Children = { button, scroller, text } },
                    };

                    button.Click += (_, _) => ClickCount++;

                    window.Show();

                    Window = window;
                    Button = button;
                    Scroller = scroller;
                    Text = text;

                    // Start on the UI thread: Start captures Dispatcher.CurrentDispatcher,
                    // which is what every command marshals back onto.
                    DevToolsServer.Start(0);
                });

                // Nothing has a box until layout has run, and half these assertions are
                // about boxes.
                UiThread.WaitUntil(() => Button!.ActualWidth > 0);

                UiThread.Invoke(() =>
                {
                    Point origin = Scroller!.TransformToAncestor(Window!).Transform(new Point(0, 0));
                    ScrollerCentre = new Point(origin.X + Scroller.ActualWidth / 2,
                                               origin.Y + Scroller.ActualHeight / 2);
                });

                s_started = true;
                return DevToolsServer.ListeningPort;
            }
        }
    }
}
