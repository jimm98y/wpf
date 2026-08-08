// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The per-OS windowing heads, driven DIRECTLY.
//
// NEW coverage, not a port. Nothing asserted any of this before, and it is what a long investigation
// into "text renders at scale 3 when every API reports 1" needed and did not have
// (Documentation/linux-head.md).
//
// These tests deliberately do NOT go through WPF. The windowing head is a layer BELOW WPF -- it is
// an IPlatformWindow implementation with its own public Create/GetPixelSize/GetBackingScale surface,
// and WPF is merely its first consumer. Testing it through a WPF Window would have meant dragging in
// PresentationCore, PresentationFramework, a Dispatcher and an STA thread (which does not even exist
// off-Windows: SetApartmentState throws PlatformNotSupportedException on Linux), and it would have
// tested the INTEGRATION rather than the head. A failure would not have told you which layer was
// wrong, which is the whole problem this suite exists to solve.
//
// So: WindowsBase only, one window, direct calls. The invariants asserted are the ones WPF then
// relies on, stated at the layer that has to guarantee them:
//
//     GetBackingScale()  --  the scale HwndTarget turns into its world transform
//     GetPixelSize()     --  the size the render surface is configured to
//     GetContentSize()   --  the DIP size layout works in
//
// If pixel size and content size disagree with the backing scale, WPF renders a DIP scene at one
// scale into a target sized for another. That is invisible in a screenshot of the correct SIZE and
// shows up only as soft or clipped output, which is why it survived so long.
//
// Every test needs a real display, so all of them SKIP with a reason on a headless box.
//

using System;
using MS.Internal.Interop;
using MS.Internal.Interop.Wayland;
using Xunit;

namespace Wpf.Platform.Tests
{
    /// <summary>
    /// ONE real platform window for the whole run, created through whichever head this OS uses, with
    /// no WPF involved.
    ///
    /// Shared rather than per-test on purpose. Creating and destroying a Wayland toplevel repeatedly
    /// in one process tears down libdecor's GTK plugin state and the run dies mid-way with a wall of
    /// `Gtk-CRITICAL ... assertion 'G_IS_OBJECT (object)' failed` and no test results at all -- the
    /// head is built for an app that makes its windows and keeps them, which is also how WPF uses
    /// it. Sharing one window matches that and keeps the failure surface honest: these tests read
    /// geometry, so they do not need isolation from each other.
    /// </summary>
    public sealed class HeadFixture : IDisposable
    {
        public IPlatformWindow? Window { get; }
        public string? Unavailable { get; }

        /// <summary>The display probe, reachable without constructing the fixture.</summary>
        public static bool DisplayAvailableStatic =>
            OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"))
            || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"));

        public const int WidthDips = 400, HeightDips = 300;

        public HeadFixture()
        {
            bool display = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
                || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"))
                || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"));
            if (!display) { Unavailable = "requires a display server (no WAYLAND_DISPLAY or DISPLAY set)"; return; }

            try
            {
                if (OperatingSystem.IsLinux())
                {
                    if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")))
                    { Unavailable = "the Linux head is Wayland-native; there is no X11 fallback"; return; }
                    WaylandWindow.EnsureApplication();
                    var w = new WaylandWindow();
                    w.Create("Wpf.Platform.Tests", 0, 0, WidthDips, HeightDips, borderless: false);
                    Window = w;
                }
                else if (OperatingSystem.IsMacOS())
                {
                    // See PlatformThread: AppKit windows belong to the main thread, which a test
                    // runner does not have to give. CocoaWindow.Create says so with an exception the
                    // catch below turns into a skip reason, but saying it here names the cause.
                    if (!PlatformThread.IsMainThread)
                    {
                        Unavailable = "AppKit windows require the process main thread, which the test runner does not provide";
                        return;
                    }

                    var w = new CocoaWindow();
                    w.Create("Wpf.Platform.Tests", 0, 0, WidthDips, HeightDips);
                    Window = w;
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

        public void Dispose() { try { Window?.Destroy(); } catch { } }
    }

    [CollectionDefinition(Name)]
    public sealed class HeadCollection : ICollectionFixture<HeadFixture>
    {
        public const string Name = "platform-head";
    }

    /// <summary>Base class: hands out the shared window, or skips with the reason there is none.</summary>
    [Collection(HeadCollection.Name)]
    public abstract class HeadTestBase
    {
        private readonly HeadFixture _fixture;
        protected HeadTestBase(HeadFixture fixture) => _fixture = fixture;

        protected IPlatformWindow Window
        {
            get
            {
                Assert.SkipWhen(_fixture.Window is null, _fixture.Unavailable ?? "no platform window");
                return _fixture.Window!;
            }
        }
    }

    public sealed class BackingScaleTests : HeadTestBase
    {
        public BackingScaleTests(HeadFixture f) : base(f) { }

        /// <summary>
        /// The backing scale must be POSITIVE, FINITE and plausible. It multiplies into HwndTarget's
        /// world transform, the client rects and the render surface size, so a zero or NaN here
        /// produces a zero-sized drawable or a scene at an absurd scale rather than an exception
        /// anyone can trace back.
        /// </summary>
        [Fact]
        public void BackingScale_IsPositiveFiniteAndPlausible()
        {
            {
                IPlatformWindow w = Window;
                double scale = w.GetBackingScale();
                Assert.True(scale > 0 && double.IsFinite(scale),
                    $"backing scale must be positive and finite, was {scale}");
                Assert.InRange(scale, 0.5, 8.0);
            }
        }

        /// <summary>
        /// On Linux the scale must be a multiple of 1/120, because that is the only shape Wayland can
        /// report one in: an integer wl_output.scale, or wp_fractional_scale_v1 in 120ths. A value
        /// outside that set was manufactured somewhere above the windowing layer instead of read from
        /// the compositor, which is precisely the failure this suite was written after.
        /// </summary>
        [Fact]
        public void OnWayland_ScaleComesFromTheCompositor()
        {
            Assert.SkipUnless(OperatingSystem.IsLinux(), "Wayland head: Linux only");
            {
                IPlatformWindow w = Window;
                double scale = w.GetBackingScale();
                double in120ths = scale * 120.0;
                Assert.True(Math.Abs(in120ths - Math.Round(in120ths)) < 1e-6,
                    $"backing scale {scale} is not a multiple of 1/120, so it did not come from the compositor");
                Assert.True(scale >= 1.0, $"Wayland never reports a scale below 1, got {scale}");
            }
        }
    }

    public sealed class WindowGeometryTests : HeadTestBase
    {
        public WindowGeometryTests(HeadFixture f) : base(f) { }

        /// <summary>
        /// THE invariant the DPI chain rests on: client pixels == content DIPs * backing scale.
        /// HwndTarget derives its world transform from the scale and the render surface from the
        /// pixel size, so these two disagreeing is the render-scale mismatch in its purest form.
        /// </summary>
        [Fact]
        public void PixelSize_EqualsContentSizeTimesBackingScale()
        {
            {
                IPlatformWindow w = Window;
                double scale = w.GetBackingScale();
                w.GetContentSize(out int cw, out int ch);
                w.GetPixelSize(out int pw, out int ph);

                Assert.True(Math.Abs(pw - cw * scale) <= 1,
                    $"pixel width {pw} != content {cw} DIPs * scale {scale} (= {cw * scale})");
                Assert.True(Math.Abs(ph - ch * scale) <= 1,
                    $"pixel height {ph} != content {ch} DIPs * scale {scale} (= {ch * scale})");
            }
        }

        /// <summary>
        /// The OUTER window is at least the client area. GetWindowRect reports the outer frame and
        /// GetClientRect the content; WPF relies on the difference being the non-client inset, so a
        /// head returning the client size for both silently changes what Window.Width means relative
        /// to Win32.
        /// </summary>
        [Fact]
        public void WindowPixelSize_IsAtLeastTheClientPixelSize()
        {
            {
                IPlatformWindow w = Window;
                w.GetPixelSize(out int cw, out int ch);
                w.GetWindowPixelSize(out int ww, out int wh);
                Assert.True(ww >= cw, $"outer width {ww} is smaller than client width {cw}");
                Assert.True(wh >= ch, $"outer height {wh} is smaller than client height {ch}");
            }
        }

        /// <summary>
        /// SetContentSizePixels sets the OUTER window size, despite its name.
        ///
        /// This is a genuine trap and the reason the test spells it out. Both heads document the
        /// incoming size as "the WPF Window size = the OUTER window size (Win32/WPF semantics)"
        /// because that is what WPF's SetWindowPos hands them: CocoaWindow sets the outer frame
        /// directly, WaylandWindow subtracts the non-client inset to derive the content size. So
        /// set(N) then GetPixelSize() returns N MINUS the chrome -- 320 in, 310 out on a GNOME
        /// titlebar -- and only GetWindowPixelSize round-trips.
        ///
        /// Asserting the round-trip on the wrong one of the two getters looks like a head bug and is
        /// not. Pinning the real contract here is what stops the next person "fixing" it.
        /// </summary>
        [Fact]
        public void SetContentSizePixels_SetsTheOuterWindowSize()
        {
                IPlatformWindow w = Window;
                double scale = w.GetBackingScale();
                int wantW = (int)Math.Round(320 * scale), wantH = (int)Math.Round(240 * scale);
                w.SetContentSizePixels(wantW, wantH);

                w.GetWindowPixelSize(out int outerW, out int outerH);
                w.GetPixelSize(out int clientW, out int clientH);

                // One DIP of slack per axis: a head that keeps its size in integral DIPs cannot
                // represent an odd pixel count at fractional scale.
                int slack = Math.Max(1, (int)Math.Ceiling(scale));
                Assert.True(Math.Abs(outerW - wantW) <= slack,
                    $"asked for an outer width of {wantW}px at scale {scale}, got {outerW}px");
                Assert.True(Math.Abs(outerH - wantH) <= slack,
                    $"asked for an outer height of {wantH}px at scale {scale}, got {outerH}px");

                // ...and the client area is the outer size less the chrome, never larger than it.
                Assert.True(clientW <= outerW && clientH <= outerH,
                    $"client {clientW}x{clientH} exceeds outer {outerW}x{outerH}");
        }

        /// <summary>The client origin must be a sane screen coordinate, not garbage or an infinity.</summary>
        [Fact]
        public void ClientScreenOrigin_IsFinite()
        {
            {
                IPlatformWindow w = Window;
                w.GetClientScreenOriginPixels(out int sx, out int sy);
                Assert.InRange(sx, -32768, 32767);
                Assert.InRange(sy, -32768, 32767);
            }
        }
    }

    /// <summary>
    /// Guards against a debugging override leaking into a real run. These exist to reproduce HiDPI
    /// locally; if either is set in CI the entire suite renders at a scale the display does not have
    /// and every pixel assertion measures the wrong thing while still passing.
    /// </summary>
    public sealed class ScaleOverrideTests
    {
        [Theory]
        [InlineData("WPF_LINUX_FORCE_SCALE")]
        [InlineData("WPF_MAC_FORCE_SCALE")]
        public void ForceScaleOverride_IsNotSet(string variable)
        {
            string? forced = Environment.GetEnvironmentVariable(variable);
            Assert.True(string.IsNullOrEmpty(forced),
                $"{variable} is set to '{forced}', which overrides the real display scale for the whole run");
        }
    }
}
