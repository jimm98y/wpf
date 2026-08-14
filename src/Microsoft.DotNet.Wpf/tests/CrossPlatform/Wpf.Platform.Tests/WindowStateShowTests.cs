// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// A window asked to open maximized has to actually open.
//
// WPF shows a Window whose WindowState is Maximized by calling ShowWindow(SW_SHOWMAXIMIZED) and
// nothing else. The off-Windows shim only called SetVisible for the SW_* values it recognised, and
// that was not among them -- its comment said the minimize/maximize ones were "state changes on a
// window that stays on screen, already handled above", which was true only of Wayland, the one head
// that had a state implementation at all.
//
// So on macOS, iOS, Android and the browser such a window was created, given a surface and
// composited into -- and never ordered onto the screen. The window server reported it with correct
// bounds and onscreen=0, and the renderer sat in "no drawable, reconfiguring" for as long as the
// process ran. Nothing logged an error: from WPF's side every call had succeeded.
//
// What is asserted here is the CLASSIFICATION -- which SW_* values show, and which of those take
// focus -- because that is what was wrong, and because it is the one part of the shim that can be
// checked without a window server. Whether the platforms then honour the state is the heads' own
// business, and asserting it needs a real window on the process main thread, which a test runner
// does not have (see ScreenAndPopupTests, which skips on macOS for exactly that reason).
//

using MS.Win32;
using Xunit;

namespace Wpf.Platform.Tests
{
    public sealed class WindowStateShowTests
    {
        /// <summary>
        /// The regression itself. SW_SHOWMAXIMIZED is what WPF sends for WindowState=Maximized, and
        /// it has to show the window -- it is not a state change on something already on screen.
        /// </summary>
        [Fact]
        public void ShowMaximizedShowsTheWindow()
        {
            Assert.True(UnsafeNativeMethods.ShowsWindow(NativeMethods.SW_SHOWMAXIMIZED));
            Assert.True(UnsafeNativeMethods.ActivatesWindow(NativeMethods.SW_SHOWMAXIMIZED));
        }

        [Theory]
        [InlineData(NativeMethods.SW_NORMAL)]
        [InlineData(NativeMethods.SW_SHOW)]
        [InlineData(NativeMethods.SW_SHOWMAXIMIZED)]
        [InlineData(NativeMethods.SW_SHOWMINIMIZED)]
        [InlineData(NativeMethods.SW_MINIMIZE)]
        [InlineData(NativeMethods.SW_RESTORE)]
        [InlineData(NativeMethods.SW_SHOWNA)]
        [InlineData(NativeMethods.SW_SHOWNOACTIVATE)]
        [InlineData(NativeMethods.SW_SHOWMINNOACTIVE)]
        public void EverythingExceptHideShows(int nCmdShow)
        {
            Assert.True(UnsafeNativeMethods.ShowsWindow(nCmdShow),
                $"SW_* value {nCmdShow} was classified as not showing the window; only SW_HIDE hides");
        }

        [Fact]
        public void OnlyHideHides()
        {
            Assert.False(UnsafeNativeMethods.ShowsWindow(NativeMethods.SW_HIDE));
        }

        /// <summary>
        /// The three "show but do not take focus" values -- how WPF shows every Popup and any Window
        /// with ShowActivated=false. Getting this wrong in the other direction is what made a
        /// transparent top-level window unable to activate, so it is pinned from both sides.
        /// </summary>
        [Theory]
        [InlineData(NativeMethods.SW_SHOWNA, false)]
        [InlineData(NativeMethods.SW_SHOWNOACTIVATE, false)]
        [InlineData(NativeMethods.SW_SHOWMINNOACTIVE, false)]
        [InlineData(NativeMethods.SW_SHOW, true)]
        [InlineData(NativeMethods.SW_NORMAL, true)]
        [InlineData(NativeMethods.SW_SHOWMAXIMIZED, true)]
        [InlineData(NativeMethods.SW_RESTORE, true)]
        public void OnlyTheActivatingValuesTakeFocus(int nCmdShow, bool expected)
        {
            Assert.Equal(expected, UnsafeNativeMethods.ActivatesWindow(nCmdShow));
        }
    }
}
