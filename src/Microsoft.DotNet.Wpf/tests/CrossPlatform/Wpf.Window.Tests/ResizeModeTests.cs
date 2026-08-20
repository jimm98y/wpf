// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// ResizeMode reaches the window, and keeps reaching it.
//
// It is carried as WS_THICKFRAME and WS_MINIMIZEBOX (Window.CreateResizibility) in the same style
// word the head reads "borderless" and "chromeless" out of -- and those two bits were never read.
// Every window came out fully resizable whatever the application asked for: a NoResize dialog could
// be dragged bigger by its edge and zoomed by its green button.
//
// Asserted through the NSWindow's style mask, because that IS the answer: on macOS "may this window
// be resized" is not a property the window exposes, it is which bits are in the mask.
//

using System;
using System.Windows;
using MS.Internal.Interop;
using Xunit;

namespace Wpf.Window.Tests
{
    public class ResizeModeTests
    {
        // NSWindowStyleMask: Titled 1, Closable 2, Miniaturizable 4, Resizable 8.
        private const nuint Resizable = 8;
        private const nuint Miniaturizable = 4;

        [Theory]
        [InlineData(ResizeMode.CanResize, true, true)]
        [InlineData(ResizeMode.CanResizeWithGrip, true, true)]
        [InlineData(ResizeMode.NoResize, false, false)]
        [InlineData(ResizeMode.CanMinimize, false, true)]
        public void AWindowIsBuiltWithTheChromeItsResizeModeAsksFor(
            ResizeMode mode, bool resizable, bool miniaturizable)
        {
            Assert.SkipUnless(OperatingSystem.IsMacOS(),
                "the style mask is the macOS head's expression of ResizeMode");

            System.Windows.Window? window = null;
            try
            {
                window = UiThread.Invoke(() =>
                {
                    var w = new System.Windows.Window
                    {
                        Title = $"Wpf.Window.Tests {mode}",
                        Width = 360,
                        Height = 260,
                        ResizeMode = mode,
                    };
                    w.Show();
                    return w;
                });

                UiThread.WaitUntil(() => false, 400);
                nuint mask = MaskOf(window!);

                Assert.True(((mask & Resizable) != 0) == resizable,
                    $"{mode} should{(resizable ? "" : " not")} be resizable, mask is 0x{mask:x}");
                Assert.True(((mask & Miniaturizable) != 0) == miniaturizable,
                    $"{mode} should{(miniaturizable ? "" : " not")} be miniaturizable, mask is 0x{mask:x}");
            }
            finally
            {
                if (window is not null)
                {
                    UiThread.Invoke(() => { try { window.Close(); } catch { } });
                }
            }
        }

        // Changing ResizeMode on a LIVE window is NOT covered, because it does not work.
        //
        // That change travels through HwndStyleManager.Flush -> CriticalSetWindowLong, which is a
        // no-op off Windows, and wiring it up is not enough on its own: WPF's style writes are
        // read-modify-write, and GetWindowLong off Windows can only answer with the two state bits
        // it knows. The word flushed back has lost WS_THICKFRAME, WS_CAPTION and the rest, so acting
        // on it strips the chrome from windows that never asked -- measured, as six tests that had
        // been passing. Answering GetWindowLong with a faithful style word first is the way in, and
        // is a larger piece of Win32 emulation than this one property justifies.

        private static nuint MaskOf(System.Windows.Window window) => UiThread.Invoke(() =>
        {
            IntPtr handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            return PlatformWindow.FromHandle(handle) is CocoaWindow cocoa ? cocoa.GetStyleMask() : 0;
        });
    }
}
