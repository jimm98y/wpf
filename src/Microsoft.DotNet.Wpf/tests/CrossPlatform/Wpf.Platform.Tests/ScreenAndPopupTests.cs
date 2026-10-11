// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Screen geometry and popup placement, per OS.
//
// Both are places where the heads are implemented separately and agree only by intent, and both feed
// WPF code that assumes Win32 semantics:
//
//   * GetPrimaryScreenPixels backs SystemParameters.PrimaryScreenWidth / WorkArea, which layout and
//     WindowStartupLocation use;
//   * popup placement is the one case Wayland reports back, since a compositor may slide or flip a
//     popup to keep it on screen (see WaylandWindow's header on the virtual coordinate model).
//
// Still no WPF: these drive the windowing heads directly, for the reasons in DpiChainTests.
//

using System;
using MS.Internal.Interop;
using MS.Internal.Interop.Wayland;
using Xunit;

namespace Wpf.Platform.Tests
{
    /// <summary>
    /// In the head collection, and that is load-bearing rather than tidiness: on Wayland the output
    /// list is populated by the registry roundtrip, which only happens once a connection exists.
    /// Querying screen bounds before any window has been created returns the 1920x1080 FALLBACK and
    /// reports false -- which looks exactly like "this head does not report screen bounds" and would
    /// have skipped forever on a machine that reports them perfectly well.
    /// </summary>
    [Collection(HeadCollection.Name)]
    public sealed class ScreenBoundsTests : HeadTestBase
    {
        public ScreenBoundsTests(HeadFixture fixture) : base(fixture) { }

        /// <summary>
        /// Screen bounds must be a non-empty rect in top-left device pixels, and the WORK AREA must
        /// sit inside the monitor. WPF derives SystemParameters from these, so an inverted or empty
        /// rect produces windows sized to nothing rather than an error anyone can trace.
        /// </summary>
        [Fact]
        public void PrimaryScreen_IsANonEmptyRect_WithTheWorkAreaInsideIt()
        {
            _ = Window;   // force the shared window, and with it the Wayland connection + outputs

            bool ok = PlatformWindow.GetPrimaryScreenPixels(
                out int monLeft, out int monTop, out int monRight, out int monBottom,
                out int workLeft, out int workTop, out int workRight, out int workBottom);

            Assert.True(ok,
                "the head returned its fallback screen bounds. On Wayland that means the output list " +
                "was empty -- either no connection had been established, or no wl_output reported a mode.");

            Assert.True(monRight > monLeft && monBottom > monTop,
                $"the monitor rect is empty or inverted: ({monLeft},{monTop})-({monRight},{monBottom})");

            // A plausible display, not a placeholder. The 1920x1080 fallback in
            // PlatformWindow.GetPrimaryScreenPixels returns false, so reaching here with those exact
            // numbers would mean a head fabricated them.
            Assert.InRange(monRight - monLeft, 320, 32768);
            Assert.InRange(monBottom - monTop, 240, 32768);

            Assert.True(workRight > workLeft && workBottom > workTop,
                $"the work area is empty or inverted: ({workLeft},{workTop})-({workRight},{workBottom})");
            Assert.True(workLeft >= monLeft && workTop >= monTop
                     && workRight <= monRight && workBottom <= monBottom,
                $"the work area ({workLeft},{workTop})-({workRight},{workBottom}) is not inside the " +
                $"monitor ({monLeft},{monTop})-({monRight},{monBottom})");
        }
    }

    /// <summary>
    /// A popup is a SEPARATE window kind with an owner, not just a borderless toplevel: on Wayland it
    /// is an xdg_popup positioned relative to its parent, and the compositor may move it to keep it
    /// on screen.
    /// </summary>
    public sealed class PopupPlacementTests
    {
        private const int OwnerW = 400, OwnerH = 300;

        [Fact]
        public void Popup_InheritsItsOwnersBackingScale()
        {
            IPlatformWindow? owner = null, popup = null;
            try
            {
                (owner, popup) = CreateOwnerAndPopup();

                // A popup defers to its owner while it is being positioned: it has not entered any
                // output yet, so its own scale would be a guess, and a popup whose DPI disagrees with
                // its owner renders at the wrong size AND lands in the wrong place.
                Assert.True(Math.Abs(popup!.GetBackingScale() - owner!.GetBackingScale()) < 1e-9,
                    $"popup scale {popup.GetBackingScale()} != owner scale {owner.GetBackingScale()}");
            }
            finally
            {
                try { popup?.Destroy(); } catch { }
                try { owner?.Destroy(); } catch { }
            }
        }

        /// <summary>
        /// A popup is borderless, so its outer size IS its client size -- unlike a toplevel, which
        /// carries a caption. A head that applied the toplevel non-client inset to popups would
        /// shrink every menu by the height of a titlebar.
        /// </summary>
        [Fact]
        public void Popup_IsBorderless_SoOuterEqualsClient()
        {
            IPlatformWindow? owner = null, popup = null;
            try
            {
                (owner, popup) = CreateOwnerAndPopup();

                popup!.GetPixelSize(out int cw, out int ch);
                popup.GetWindowPixelSize(out int ww, out int wh);

                Assert.True(ww == cw && wh == ch,
                    $"a borderless popup should have no non-client inset: client {cw}x{ch}, outer {ww}x{wh}");
            }
            finally
            {
                try { popup?.Destroy(); } catch { }
                try { owner?.Destroy(); } catch { }
            }
        }

        /// <summary>
        /// Creates an owner toplevel and a popup owned by it, or skips.
        ///
        /// Deliberately NOT using the shared HeadFixture window: a popup needs its owner to outlive
        /// it and be destroyed after it (destroying a surface that still has a role attached is a
        /// protocol error), so the pair is owned together here.
        /// </summary>
        private static (IPlatformWindow Owner, IPlatformWindow Popup) CreateOwnerAndPopup()
        {
            Assert.SkipUnless(HeadFixture.DisplayAvailableStatic,
                "requires a display server (no WAYLAND_DISPLAY or DISPLAY set)");

            if (OperatingSystem.IsLinux())
            {
                Assert.SkipWhen(string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")),
                    "the Linux head is Wayland-native; there is no X11 fallback");

                WaylandWindow.EnsureApplication();
                var owner = new WaylandWindow();
                owner.Create("Wpf.Platform.Tests owner", 0, 0, OwnerW, OwnerH, borderless: false);

                var popup = new WaylandWindow();
                popup.Create("Wpf.Platform.Tests popup", 20, 20, 120, 80, borderless: true, OwnerHandle(owner));
                return (owner, popup);
            }

            if (OperatingSystem.IsMacOS())
            {
                // AppKit only builds windows on the process's main thread, and xunit dispatches every
                // test onto a worker. Program.cs keeps the main thread pumping a Dispatcher precisely
                // so there is somewhere to marshal to; creating a window without one used to abort
                // the whole assembly (an Objective-C exception through managed frames), so nothing in
                // this project reported at all on macOS.
                Assert.SkipUnless(PlatformThread.CanReachMainThread,
                    "AppKit windows require the process main thread, which this test host does not provide");

                // The PAIR is created in one hop rather than two, so the popup is built while its
                // owner is the frontmost window -- the same order an application opens a menu in.
                return PlatformThread.InvokeOnMain(() =>
                {
                    var owner = new CocoaWindow();
                    owner.Create("Wpf.Platform.Tests owner", 0, 0, OwnerW, OwnerH);
                    var popup = new CocoaWindow();
                    popup.Create("Wpf.Platform.Tests popup", 20, 20, 120, 80, borderless: true);
                    return (MainThreadWindow.Wrap(owner), MainThreadWindow.Wrap(popup));
                });
            }

            Assert.Skip($"no windowing head is wired into this test for {Environment.OSVersion.Platform}");
            return default;
        }

        /// <summary>The owner's surface handle, which is what the popup's owner parameter takes.</summary>
        private static IntPtr OwnerHandle(WaylandWindow owner)
        {
            // WaylandWindow identifies itself by its wl_surface, which is what FromHandle keys on.
            foreach (System.Reflection.FieldInfo f in typeof(WaylandWindow)
                         .GetFields(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance))
            {
                if (f.Name == "_surface" && f.GetValue(owner) is IntPtr h) return h;
            }
            return IntPtr.Zero;
        }
    }
}
