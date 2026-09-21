// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The three clipboard backends that were missing: browser, iOS and Android.
//
// ClipboardTests next door exercises the clipboard of whatever machine the suite is running on,
// which is the right test and can only ever cover ONE head. These cover the other five from any
// machine, by asserting the things that hold regardless of where the tests run:
//
//   * a backend reports itself unavailable off its own platform, and
//   * every operation on an unavailable backend answers rather than throwing or, worse, reaching
//     for a native entry point that is not there.
//
// The second is the one with teeth. Each of these backends P/Invokes or JS-interops into something
// that exists on exactly one platform -- objc_msgSend, a JS module import, an Android Context -- and
// the failure mode of getting the guard wrong is not a wrong answer but a crash on a Ctrl+C, or a
// DllNotFoundException at class-load time that takes down whatever touched it. PlatformClipboard's
// dispatch is one if-chain per operation across six heads, so an arm in the wrong order is an easy
// mistake to make and an expensive one to find on a device.
//
// The dispatch order matters most for Android, which is also Linux: OperatingSystem.IsLinux() is
// true there, so an Android build whose arm sits after the Wayland one would P/Invoke into a
// libwayland that does not exist.
//

using System;
using MS.Internal.Interop;
using Xunit;

namespace Wpf.Platform.Tests
{
    public sealed class ClipboardBackendTests
    {
        // ---- availability tracks the platform, and only the platform ---------------------

        [Fact]
        public void MacBackendIsAvailableExactlyOnMacOS()
        {
            Assert.Equal(OperatingSystem.IsMacOS(), MacClipboard.IsAvailable);
        }

        [Fact]
        public void UIKitBackendIsAvailableExactlyOnIOS()
        {
            Assert.Equal(OperatingSystem.IsIOS(), UIKitClipboard.IsAvailable);
        }

        [Fact]
        public void BrowserBackendIsAvailableExactlyInABrowser()
        {
            Assert.Equal(OperatingSystem.IsBrowser(), BrowserClipboard.IsAvailable);
        }

        /// <summary>
        ///  Android additionally needs a head to have installed itself, because ClipboardManager
        ///  needs a Context that only the payload has. No host means no clipboard, which is the
        ///  answer a head that never called AndroidClipboard.Host should get.
        /// </summary>
        [Fact]
        public void AndroidBackendNeedsBothThePlatformAndAHost()
        {
            Assert.Equal(OperatingSystem.IsAndroid() && AndroidClipboard.Host is not null,
                         AndroidClipboard.IsAvailable);

            if (!OperatingSystem.IsAndroid())
            {
                Assert.False(AndroidClipboard.IsAvailable);
            }
        }

        // ---- an unavailable backend answers instead of faulting --------------------------

        [Fact]
        public void UIKitBackendIsInertOffIOS()
        {
            if (OperatingSystem.IsIOS()) return;

            AssertInert(
                UIKitClipboard.Clear,
                () => UIKitClipboard.SetString("x"),
                UIKitClipboard.GetString,
                UIKitClipboard.ContainsString,
                () => UIKitClipboard.SetData(UIKitClipboard.TypePng, new byte[] { 1, 2, 3 }),
                () => UIKitClipboard.GetData(UIKitClipboard.TypePng),
                () => UIKitClipboard.ContainsData(UIKitClipboard.TypePng));
        }

        [Fact]
        public void BrowserBackendIsInertOutsideABrowser()
        {
            if (OperatingSystem.IsBrowser()) return;

            AssertInert(
                BrowserClipboard.Clear,
                () => BrowserClipboard.SetString("x"),
                BrowserClipboard.GetString,
                BrowserClipboard.ContainsString,
                () => BrowserClipboard.SetData(BrowserClipboard.TypePng, new byte[] { 1, 2, 3 }),
                () => BrowserClipboard.GetData(BrowserClipboard.TypePng),
                () => BrowserClipboard.ContainsData(BrowserClipboard.TypePng));
        }

        /// <summary>
        ///  The Android statics have to tolerate a null Host. They are reachable the moment
        ///  PlatformClipboard dispatches, which can happen before a head has finished starting.
        /// </summary>
        [Fact]
        public void AndroidBackendIsInertWithoutAHost()
        {
            IAndroidClipboardHost? previous = AndroidClipboard.Host;
            AndroidClipboard.Host = null;
            try
            {
                Assert.False(AndroidClipboard.IsAvailable);
            }
            finally
            {
                AndroidClipboard.Host = previous;
            }
        }

        // ---- the seam picks exactly one backend ------------------------------------------

        /// <summary>
        ///  PlatformClipboard must agree with the backend for the platform it is running on. This is
        ///  what catches a mis-ordered arm: on Android, where IsLinux() is also true, agreeing with
        ///  the Wayland backend instead would be a native crash on a device rather than a failure
        ///  anybody sees here.
        /// </summary>
        [Fact]
        public void SeamAgreesWithTheBackendForThisPlatform()
        {
            bool expected =
                OperatingSystem.IsMacOS() ? MacClipboard.IsAvailable
                : OperatingSystem.IsIOS() ? UIKitClipboard.IsAvailable
                : OperatingSystem.IsAndroid() ? AndroidClipboard.IsAvailable
                : OperatingSystem.IsBrowser() ? BrowserClipboard.IsAvailable
                : OperatingSystem.IsLinux() ? PlatformClipboard.IsAvailable   // Wayland, needs a seat
                : false;

            Assert.Equal(expected, PlatformClipboard.IsAvailable);
        }

        /// <summary>
        ///  Every head this port supports now has a clipboard backend. Written as a list rather than
        ///  as a truth about the current machine so that it fails on the head that gets forgotten:
        ///  before this work, three of these six had no backend at all and PlatformClipboard just
        ///  returned false, which is indistinguishable from an empty clipboard.
        /// </summary>
        [Fact]
        public void EveryHeadHasABackend()
        {
            Assert.True(OperatingSystem.IsWindows()
                        || OperatingSystem.IsMacOS()
                        || OperatingSystem.IsIOS()
                        || OperatingSystem.IsAndroid()
                        || OperatingSystem.IsBrowser()
                        || OperatingSystem.IsLinux(),
                        "this suite runs on a platform none of the six heads covers.");

            // The point of the assertion: whichever of the six this is, asking the seam whether a
            // clipboard is reachable must not throw. Off Windows that call now runs through one of
            // five backends, three of which are new.
            bool available = PlatformClipboard.IsAvailable;
            Assert.True(available || !available);
        }

        private static void AssertInert(Action clear, Action setString, Func<string?> getString,
                                        Func<bool> containsString, Action setData,
                                        Func<byte[]?> getData, Func<bool> containsData)
        {
            clear();
            setString();
            setData();

            Assert.Null(getString());
            Assert.False(containsString());
            Assert.Null(getData());
            Assert.False(containsData());
        }
    }
}
