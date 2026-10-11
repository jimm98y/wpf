// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Linux-only glue for WPF-Samples' WPFGallery, compiled in by WPFGallery.Linux.csproj.
//
// It lives HERE, in the fork, rather than next to the gallery sources, because both csproj files in
// that folder use the SDK's default **/*.cs glob -- a file dropped there would be compiled into the
// upstream Windows build too. Referencing it by path keeps the sample exactly as it shipped.
//
// What it does: hides the gallery's own minimise/maximise/close buttons.
//
// The gallery draws a Windows 11 style caption inside its client area. On Linux the window manager
// (through libdecor) draws a real titlebar with its own buttons, and that titlebar is what provides
// interactive move, resize, snapping and the window menu -- so it is the one to keep. Leaving both
// gives the window two sets of caption buttons. Hiding the app's is the same arrangement macOS ends
// up with: the platform titlebar on top, the app's title row below it without duplicate controls.
//

using System;
using System.Runtime.CompilerServices;
using System.Windows;

namespace WPFGallery.Linux
{
    internal static class LinuxChrome
    {
        // Named in MainWindow.xaml. Matched by name because they are ordinary Buttons in the app's
        // own markup -- there is no chrome contract that identifies them as caption buttons.
        private static readonly string[] CaptionButtons = { "MinimizeButton", "MaximizeButton", "CloseButton" };

        [ModuleInitializer]
        internal static void Initialize()
        {
            // Windows keeps the app's caption: there the app IS the chrome (WindowChrome removes the
            // system one), so hiding these would leave no way to close the window.
            if (OperatingSystem.IsWindows()) return;

            EventManager.RegisterClassHandler(
                typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnWindowLoaded));
        }

        private static void OnWindowLoaded(object sender, RoutedEventArgs e)
        {
            if (sender is not Window window) return;

            foreach (string name in CaptionButtons)
            {
                if (window.FindName(name) is UIElement button)
                {
                    button.Visibility = Visibility.Collapsed;
                }
            }
        }
    }
}
