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
using System.Windows;
using System.Windows.Controls;
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

        internal static bool DisplayAvailable =>
            OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"))
            || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"));

        internal static Window? Window { get; private set; }

        internal static Button? Button { get; private set; }

        /// <summary>Counts real Click events, so the input tests can prove one happened.</summary>
        internal static int ClickCount;

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

                    var window = new Window
                    {
                        Title = WindowTitle,
                        Width = 400,
                        Height = 300,
                        Content = new Grid { Children = { button } },
                    };

                    button.Click += (_, _) => ClickCount++;

                    window.Show();

                    Window = window;
                    Button = button;

                    // Start on the UI thread: Start captures Dispatcher.CurrentDispatcher,
                    // which is what every command marshals back onto.
                    DevToolsServer.Start(0);
                });

                // Nothing has a box until layout has run, and half these assertions are
                // about boxes.
                UiThread.WaitUntil(() => Button!.ActualWidth > 0);

                s_started = true;
                return DevToolsServer.ListeningPort;
            }
        }
    }
}
