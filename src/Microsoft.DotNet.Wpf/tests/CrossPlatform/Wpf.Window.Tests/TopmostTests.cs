// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Window.Topmost reaches the window.
//
// Asserted through the head's window level rather than by looking at the screen, because stacking is
// not observable from inside the process any other way -- and a screenshot could not tell "kept on
// top" from "happens to be in front right now".
//
// Topmost travels as a Z-ORDER argument (SetWindowPos's hWndInsertAfter) and nothing else, so it was
// the one window property that could be dropped without anything else looking wrong.
//

using System;
using System.Windows;
using MS.Internal.Interop;
using Xunit;

namespace Wpf.Window.Tests
{
    public class TopmostTests
    {
        private const nint NSNormalWindowLevel = 0;
        private const nint NSFloatingWindowLevel = 3;

        [Fact]
        public void TopmostRaisesAndLowersTheWindowLevel()
        {
            Assert.SkipUnless(OperatingSystem.IsMacOS(),
                "the window level is the macOS head's expression of Topmost; other heads have their own or none");

            System.Windows.Window? window = null;
            try
            {
                window = UiThread.Invoke(() =>
                {
                    var w = new System.Windows.Window
                    {
                        Title = "Wpf.Window.Tests topmost",
                        Width = 360,
                        Height = 260,
                    };
                    w.Show();
                    return w;
                });

                UiThread.WaitUntil(() => false, 400);

                Assert.Equal(NSNormalWindowLevel, LevelOf(window!));

                UiThread.Invoke(() => window!.Topmost = true);
                Assert.True(UiThread.WaitUntil(() => LevelOf(window!) == NSFloatingWindowLevel),
                    $"Topmost=true left the window at level {LevelOf(window!)}. It travels as "
                    + "SetWindowPos's hWndInsertAfter and nothing else, so dropping that argument "
                    + "makes the property silently inert.");

                UiThread.Invoke(() => window!.Topmost = false);
                Assert.True(UiThread.WaitUntil(() => LevelOf(window!) == NSNormalWindowLevel),
                    $"Topmost=false left the window at level {LevelOf(window!)}");
            }
            finally
            {
                if (window is not null)
                {
                    UiThread.Invoke(() => { try { window.Close(); } catch { } });
                }
            }
        }

        private static nint LevelOf(System.Windows.Window window) => UiThread.Invoke(() =>
        {
            IntPtr handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            return PlatformWindow.FromHandle(handle) is CocoaWindow cocoa ? cocoa.GetWindowLevel() : 0;
        });
    }
}
