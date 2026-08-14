// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Every head can be asked what its display refreshes at.
//
// WPF paces itself against the display: MediaContext schedules the next commit from the refresh
// period, and falls back to a hardcoded "about a vblank" of 17ms when nothing has told it the rate.
// Off Windows that fallback was the ONLY path, because the notification carrying the rate
// (MilMessage.Presented) is posted by milcore and had no managed equivalent -- so every head ran at
// 58.8fps whatever the panel could do.
//
// The seam answers 0 for "cannot say", which keeps that fallback. That is a legitimate answer and
// the reason these tests assert a RANGE rather than a number: the value depends on the machine, and
// a test demanding 60 would fail on the 120Hz laptop it is most wanted on.
//
// The range check is the one with teeth. Wayland reports milli-Hz and iOS reports a frame count, so
// the two likeliest mistakes are a rate a thousand times too large and one a thousand times too
// small. Either would sail past a null check and wreck the pacing rather than the pixels.
//

using System;
using MS.Internal.Interop;
using Xunit;

namespace Wpf.Platform.Tests
{
    public sealed class RefreshRateTests
    {
        /// <summary>Anything outside this is a unit mistake, not a display.</summary>
        private const double Slowest = 20.0;
        private const double Fastest = 1000.0;

        [Fact]
        public void ThisMachineReportsAPlausibleRefreshRate()
        {
            IPlatformWindow window = CurrentHeadWindow();
            Assert.SkipWhen(window is null, "no windowing head on this platform");

            double hz = window.GetRefreshRateHz();

            // Zero is allowed: a head that cannot ask says so, and WPF keeps its old fallback.
            if (hz == 0) return;

            Assert.InRange(hz, Slowest, Fastest);
        }

        /// <summary>
        /// Asking a window that was never created must not throw, and must not invent a number.
        /// </summary>
        /// <remarks>
        /// It is allowed to answer with the main display's rate -- a window not yet placed on a
        /// screen still has to say something, and the head it belongs to is the right one to guess
        /// with. What it must not do is fail: this is called from the present path, once a frame, and
        /// an exception there would take down compositing over a diagnostic.
        ///
        /// This deliberately does NOT assert zero. It did at first, and the assertion was wrong
        /// rather than the code -- it passed on a machine whose main screen answered nothing and
        /// failed the moment a display was reachable, which is a test measuring the room it runs in.
        /// </remarks>
        [Fact]
        public void AnUncreatedWindowAnswersWithoutThrowing()
        {
            IPlatformWindow window = CurrentHeadWindow();
            Assert.SkipWhen(window is null, "no windowing head on this platform");

            double hz = window.GetRefreshRateHz();

            Assert.True(hz == 0 || (hz >= Slowest && hz <= Fastest),
                $"an uncreated window reported {hz}Hz, which is neither 'unknown' nor a display");
        }

        private static IPlatformWindow CurrentHeadWindow()
        {
            if (OperatingSystem.IsMacOS()) return new CocoaWindow();
            if (OperatingSystem.IsAndroid()) return new AndroidWindow();
            if (OperatingSystem.IsIOS()) return new UIKitWindow();
            if (OperatingSystem.IsBrowser()) return new BrowserWindow();
            if (OperatingSystem.IsLinux()) return new MS.Internal.Interop.Wayland.WaylandWindow();
            return null;
        }
    }
}
