// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The other half of WindowStateShowTests: whether the head HONOURS the state, not just how WPF
// classifies the SW_* value.
//
// That file asserts the classification and says why it stops there -- "asserting it needs a real
// window on the process main thread, which a test runner does not have". A test runner does now
// (Program.cs), so the rest can be asserted, on a real window, against what the window server
// actually did.
//
// Worth asserting because the two heads reach the same behaviour by different routes and neither is
// obviously right. macOS has no "maximize": AppKit spells it -zoom, which TOGGLES, so the head has
// to ask -isZoomed first -- and WPF sends SW_SHOWMAXIMIZED on every Show of an already-maximized
// window, so a missing guard un-maximizes on the second Show rather than doing nothing. Wayland has
// no synchronous answer at all: set_maximized is a REQUEST and the size arrives later in a configure
// event. Both are asserted the same way here -- act, then wait for the window to settle -- because
// that is the only shape that is true of both.
//
// One window for the three tests rather than one each: creating and destroying toplevels repeatedly
// in a single process is what the HeadFixture comment warns about on Wayland. Each test restores the
// window first, so they do not depend on each other or on the order xunit runs them in.
//

using System;
using System.Diagnostics;
using System.Threading;
using MS.Internal.Interop;
using MS.Internal.Interop.Wayland;
using MS.Win32;
using Xunit;

namespace Wpf.Platform.Tests
{
    /// <summary>One resizable toplevel of its own, kept for the class and put back to normal at the end.</summary>
    public sealed class StateWindowFixture : IDisposable
    {
        public const int WidthDips = 400, HeightDips = 300;

        public IPlatformWindow? Window { get; }
        public string? Unavailable { get; }

        public StateWindowFixture()
        {
            if (!HeadFixture.DisplayAvailableStatic)
            {
                Unavailable = "requires a display server (no WAYLAND_DISPLAY or DISPLAY set)";
                return;
            }

            try
            {
                if (OperatingSystem.IsLinux())
                {
                    if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")))
                    { Unavailable = "the Linux head is Wayland-native; there is no X11 fallback"; return; }

                    WaylandWindow.EnsureApplication();
                    var w = new WaylandWindow();
                    w.Create("Wpf.Platform.Tests state", 0, 0, WidthDips, HeightDips, borderless: false);
                    Window = w;
                }
                else if (OperatingSystem.IsMacOS())
                {
                    if (!PlatformThread.CanReachMainThread)
                    {
                        Unavailable = "AppKit windows require the process main thread, which this test host does not provide";
                        return;
                    }

                    Window = MainThreadWindow.Wrap(PlatformThread.InvokeOnMain<IPlatformWindow>(() =>
                    {
                        var w = new CocoaWindow();
                        w.Create("Wpf.Platform.Tests state", 0, 0, WidthDips, HeightDips);
                        return w;
                    }));
                }
                else
                {
                    Unavailable = $"no windowing head is wired into this test for {Environment.OSVersion.Platform}";
                }
            }
            catch (Exception e)
            {
                Unavailable = $"the windowing head could not create a window: {e.GetType().Name}: {e.Message}";
            }
        }

        public void Dispose()
        {
            // Leave nothing maximized behind: the window is about to go, but a head that failed
            // half-way through a test should not hand the next one a zoomed window.
            try { Window?.SetWindowState(NativeMethods.SW_RESTORE); } catch { }
            try { Window?.Destroy(); } catch { }
        }
    }

    public sealed class WindowStateHeadTests : IClassFixture<StateWindowFixture>
    {
        private readonly StateWindowFixture _fixture;

        public WindowStateHeadTests(StateWindowFixture fixture) => _fixture = fixture;

        private IPlatformWindow Window
        {
            get
            {
                Assert.SkipWhen(_fixture.Window is null, _fixture.Unavailable ?? "no platform window");
                return _fixture.Window!;
            }
        }

        /// <summary>
        /// Maximizing must actually enlarge the window, and fill the WORK AREA rather than the whole
        /// monitor -- a maximized window does not cover the menu bar, the Dock or a panel.
        /// </summary>
        [Fact]
        public void Maximize_FillsTheWorkArea()
        {
            IPlatformWindow w = Window;
            (int normalW, int normalH) = Normalize(w);

            w.SetWindowState(NativeMethods.SW_SHOWMAXIMIZED);
            (int maxW, int maxH) = SettleUntil(w, (cw, ch) => cw > normalW && ch > normalH,
                $"the window never grew past its normal {normalW}x{normalH}px after SW_SHOWMAXIMIZED");

            try
            {
                // Half again in each direction: enough to say the head maximized rather than nudged
                // the frame, without assuming any particular screen size.
                Assert.True(maxW >= normalW * 3 / 2 && maxH >= normalH * 3 / 2,
                    $"maximized to {maxW}x{maxH}px, barely more than the normal {normalW}x{normalH}px");

                // The work-area comparison only means something when the window is ON the primary
                // screen, which is the only one GetPrimaryScreenPixels can describe. On a second
                // monitor the numbers belong to a different display and the growth check above is all
                // that can honestly be asserted.
                if (PlatformWindow.GetPrimaryScreenPixels(
                        out int monLeft, out int monTop, out int monRight, out int monBottom,
                        out int workLeft, out int workTop, out int workRight, out int workBottom))
                {
                    w.GetClientScreenOriginPixels(out int sx, out int sy);
                    bool onPrimary = sx >= monLeft && sx < monRight && sy >= monTop && sy < monBottom;
                    if (onPrimary)
                    {
                        // The CLIENT size, not the outer one, is what gets compared to the screen.
                        // GetWindowPixelSize reports the outer frame INCLUDING a non-client inset the
                        // macOS head fabricates -- 10pt horizontally, where macOS has no side frame at
                        // all -- so that a window's client width matches the one Win32 would give it.
                        // A maximized window therefore reports an outer width 10pt WIDER than the
                        // screen it exactly fills, which is correct and not worth asserting against.
                        w.GetPixelSize(out int clientW, out int clientH);
                        int workW = workRight - workLeft, workH = workBottom - workTop;
                        Assert.True(clientW >= workW * 4 / 5 && clientH >= workH * 4 / 5,
                            $"maximized to a {clientW}x{clientH}px client area, which does not fill " +
                            $"the {workW}x{workH}px work area");
                        Assert.True(clientW <= monRight - monLeft && clientH <= monBottom - monTop,
                            $"maximized to a {clientW}x{clientH}px client area, larger than the " +
                            $"{monRight - monLeft}x{monBottom - monTop}px monitor");
                    }
                }
            }
            finally
            {
                w.SetWindowState(NativeMethods.SW_RESTORE);
            }
        }

        /// <summary>Restoring must undo it, not leave the window somewhere in between.</summary>
        [Fact]
        public void RestoreAfterMaximize_ReturnsToTheNormalSize()
        {
            IPlatformWindow w = Window;
            (int normalW, int normalH) = Normalize(w);

            w.SetWindowState(NativeMethods.SW_SHOWMAXIMIZED);
            SettleUntil(w, (cw, ch) => cw > normalW && ch > normalH,
                $"the window never grew past its normal {normalW}x{normalH}px after SW_SHOWMAXIMIZED");

            w.SetWindowState(NativeMethods.SW_RESTORE);
            (int backW, int backH) = SettleUntil(w, (cw, ch) => Near(cw, normalW) && Near(ch, normalH),
                $"the window never came back to {normalW}x{normalH}px after SW_RESTORE");

            Assert.True(Near(backW, normalW) && Near(backH, normalH),
                $"restored to {backW}x{backH}px, not the {normalW}x{normalH}px it started at");
        }

        /// <summary>
        /// SW_SHOWMAXIMIZED twice must leave the window maximized. WPF sends it on every Show of a
        /// maximized window, and AppKit's -zoom TOGGLES: without the -isZoomed guard in the head the
        /// second one restores the window, so a maximized window "un-maximized itself" on being shown
        /// again. Nothing else in this suite would notice.
        /// </summary>
        [Fact]
        public void MaximizeTwice_StaysMaximized()
        {
            IPlatformWindow w = Window;
            (int normalW, int normalH) = Normalize(w);

            w.SetWindowState(NativeMethods.SW_SHOWMAXIMIZED);
            (int firstW, int firstH) = SettleUntil(w, (cw, ch) => cw > normalW && ch > normalH,
                $"the window never grew past its normal {normalW}x{normalH}px after SW_SHOWMAXIMIZED");

            try
            {
                w.SetWindowState(NativeMethods.SW_SHOWMAXIMIZED);

                // Give a head that DOES toggle time to shrink the window back -- asserting straight
                // away would pass on the animation rather than on the outcome.
                Thread.Sleep(SettleQuietMs);
                w.GetWindowPixelSize(out int againW, out int againH);

                Assert.True(Near(againW, firstW) && Near(againH, firstH),
                    $"a second SW_SHOWMAXIMIZED changed the window from {firstW}x{firstH}px to " +
                    $"{againW}x{againH}px; it should have been a no-op (macOS -zoom toggles, so the " +
                    $"head has to check -isZoomed first)");
            }
            finally
            {
                w.SetWindowState(NativeMethods.SW_RESTORE);
            }
        }

        // ---- helpers ----------------------------------------------------------------------------

        private const int SettleTimeoutMs = 5000;
        private const int SettleStepMs = 20;
        private const int SettleQuietMs = 400;

        /// <summary>Two sizes are the same window state if they agree to within a couple of pixels;
        /// a head that keeps fractional points can land a pixel out on the way back.</summary>
        private static bool Near(int a, int b) => Math.Abs(a - b) <= 2;

        /// <summary>
        /// Put the window back to normal and return the size it settled at. Called at the start of
        /// every test rather than relying on the previous one having cleaned up, so the three are
        /// independent of each other and of the order they run in.
        /// </summary>
        private static (int Width, int Height) Normalize(IPlatformWindow w)
        {
            w.SetWindowState(NativeMethods.SW_RESTORE);
            Thread.Sleep(SettleQuietMs);
            w.GetWindowPixelSize(out int width, out int height);
            Assert.True(width > 0 && height > 0, $"the window reports a {width}x{height}px frame");
            return (width, height);
        }

        /// <summary>
        /// Poll the window's outer size until <paramref name="reached"/> holds, then keep polling
        /// until it stops changing -- the state change is asynchronous on BOTH heads (an AppKit zoom
        /// animates; a Wayland configure arrives when the compositor sends it), so the first size
        /// that satisfies the predicate is usually mid-animation rather than the final one.
        /// </summary>
        private static (int Width, int Height) SettleUntil(IPlatformWindow w, Func<int, int, bool> reached, string failure)
        {
            var clock = Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < SettleTimeoutMs)
            {
                w.GetWindowPixelSize(out int width, out int height);
                if (reached(width, height))
                {
                    // Settled = unchanged across a quiet interval.
                    int lastW = width, lastH = height;
                    while (clock.ElapsedMilliseconds < SettleTimeoutMs)
                    {
                        Thread.Sleep(SettleQuietMs);
                        w.GetWindowPixelSize(out width, out height);
                        if (width == lastW && height == lastH) return (width, height);
                        (lastW, lastH) = (width, height);
                    }
                    return (width, height);
                }

                Thread.Sleep(SettleStepMs);
            }

            w.GetWindowPixelSize(out int finalW, out int finalH);
            Assert.Fail($"{failure} (waited {SettleTimeoutMs}ms; it is {finalW}x{finalH}px)");
            return default;
        }
    }
}
