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

        /// <summary>
        /// And changing it on a LIVE window works too, which needed the READ to be fixed before the
        /// write could be turned on at all.
        /// </summary>
        /// <remarks>
        /// The change travels through HwndStyleManager.Flush to CriticalSetWindowLong, and WPF's
        /// style writes are read-modify-write: it reads the current style through GetWindowLong, ORs
        /// its change in, and flushes the whole word back. While GetWindowLong could only answer with
        /// the state bits, that flush stripped WS_THICKFRAME off every window it touched -- applying
        /// it broke six tests that had been passing, this suite's own maximize and move among them.
        /// Now that the read reports the resize bits, the flush writes back what was already there.
        /// </remarks>
        [Fact]
        public void ChangingResizeModeAfterShowingChangesTheChrome()
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
                        Title = "Wpf.Window.Tests resize mode change",
                        Width = 360,
                        Height = 260,
                        ResizeMode = ResizeMode.CanResize,
                    };
                    w.Show();
                    return w;
                });

                UiThread.WaitUntil(() => false, 400);
                Assert.True((MaskOf(window!) & Resizable) != 0, "the window did not start resizable");

                UiThread.Invoke(() => window!.ResizeMode = ResizeMode.NoResize);
                Assert.True(UiThread.WaitUntil(() => (MaskOf(window!) & Resizable) == 0),
                    $"ResizeMode=NoResize left the window resizable, mask 0x{MaskOf(window!):x}");

                UiThread.Invoke(() => window!.ResizeMode = ResizeMode.CanResize);
                Assert.True(UiThread.WaitUntil(() => (MaskOf(window!) & Resizable) != 0),
                    $"ResizeMode=CanResize did not make the window resizable again, mask 0x{MaskOf(window!):x}");
            }
            finally
            {
                if (window is not null)
                {
                    UiThread.Invoke(() => { try { window.Close(); } catch { } });
                }
            }
        }

        private static nuint MaskOf(System.Windows.Window window) => UiThread.Invoke(() =>
        {
            IntPtr handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            return PlatformWindow.FromHandle(handle) is CocoaWindow cocoa ? cocoa.GetStyleMask() : 0;
        });
    }
}
