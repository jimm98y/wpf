// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The clipboard seam, per OS.
//
// PlatformClipboard is internal to WindowsBase, so this project is a declared friend
// (WindowsBase/LibraryAssemblyInfo.cs). That is a product change made purely for testability; the
// alternative was reaching it through WPF's System.Windows.Clipboard, which would have tested WPF's
// wrapper rather than the head and dragged PresentationCore into a project that deliberately
// references WindowsBase alone.
//
// These tests TOUCH THE USER'S REAL CLIPBOARD. There is no per-process clipboard on any of these
// platforms, so running the suite overwrites whatever was copied. The alternative -- not testing the
// clipboard at all -- is worse, but the cost is real and is why the tests save and restore the
// previous text where they can.
//

using System;
using System.Text;
using MS.Internal.Interop;
using Xunit;

namespace Wpf.Platform.Tests
{
    /// <summary>
    /// In the head collection: on Wayland the clipboard rides the wl_data_device of an existing seat,
    /// so it needs the connection a window brings. Querying it cold reports "unavailable" -- the same
    /// trap the screen-bounds test fell into.
    /// </summary>
    [Collection(HeadCollection.Name)]
    public sealed class ClipboardTests : HeadTestBase
    {
        public ClipboardTests(HeadFixture fixture) : base(fixture) { }

        /// <summary>Ensure a connected head, then skip unless a clipboard is actually reachable.</summary>
        private void RequireClipboard()
        {
            _ = Window;   // forces the connection (and, on Wayland, the seat)
            Assert.SkipUnless(PlatformClipboard.IsAvailable,
                "no system clipboard is reachable from this head/session");
        }

        /// <summary>
        /// Additionally require that this head can actually OWN the selection.
        ///
        /// On Wayland it usually cannot, unattended. wl_data_device.set_selection needs a serial from
        /// REAL USER INPUT, and a test run has produced none -- so WaylandClipboard defers the publish
        /// (see its Offer/FlushDeferred path) and a set is genuinely not readable back yet. That is
        /// the protocol, not a defect: a client may not silently seize the clipboard.
        ///
        /// So these skip rather than fail on an idle Wayland session, and run for real on macOS and
        /// Windows, where the clipboard is synchronous. Asserting Windows semantics everywhere would
        /// have reported a correct head as broken -- which is exactly what the first version of this
        /// file did.
        /// </summary>
        private void RequireWritableClipboard()
        {
            RequireClipboard();
            if (!OperatingSystem.IsLinux()) return;

            Assert.SkipWhen(MS.Internal.Interop.Wayland.WaylandInput.LastInputSerial == 0,
                "Wayland requires a serial from real user input before a client may own the selection, " +
                "and an unattended run has produced none; the set is deferred, so it cannot be read back");
        }

        /// <summary>
        /// A string must survive set -> get unchanged. Non-ASCII is included deliberately: every
        /// backend marshals text through a byte encoding (UTF-8 on Wayland, NSString on macOS), and a
        /// length-versus-byte-count slip only shows on multi-byte characters.
        /// </summary>
        [Theory]
        [InlineData("hello clipboard")]
        [InlineData("multi\nline\ttext")]
        [InlineData("café über naïve")]     // multi-byte UTF-8
        [InlineData("")]                                    // empty is a real case, not a no-op
        public void String_RoundTrips(string value)
        {
            RequireWritableClipboard();

            string? previous = SafeGetString();
            try
            {
                PlatformClipboard.SetString(value);
                Assert.Equal(value, PlatformClipboard.GetString());
            }
            finally
            {
                if (!string.IsNullOrEmpty(previous)) SafeSetString(previous!);
            }
        }

        /// <summary>
        /// ContainsString must agree with GetString. They are separate calls into the backend, and a
        /// probe that says "yes" while the fetch returns null is how a paste menu ends up enabled
        /// over an empty clipboard.
        /// </summary>
        [Fact]
        public void ContainsString_AgreesWithGetString()
        {
            RequireWritableClipboard();

            string? previous = SafeGetString();
            try
            {
                PlatformClipboard.SetString("agreement probe");
                Assert.True(PlatformClipboard.ContainsString(),
                    "ContainsString said no immediately after SetString");
                Assert.False(string.IsNullOrEmpty(PlatformClipboard.GetString()),
                    "ContainsString said yes but GetString returned nothing");
            }
            finally
            {
                if (!string.IsNullOrEmpty(previous)) SafeSetString(previous!);
            }
        }

        /// <summary>
        /// Binary data round-trips BYTE-EXACTLY under its own format. The payload is a real PNG
        /// header plus bytes that are invalid UTF-8 (0xFF 0xFE 0x00), so any backend that routed this
        /// through a text encoding corrupts it rather than passing it along.
        /// </summary>
        [Fact]
        public void BinaryData_RoundTripsByteExactly()
        {
            RequireWritableClipboard();

            byte[] payload =
            {
                0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,   // PNG magic
                0xFF, 0xFE, 0x00, 0x01, 0x7F, 0x80, 0xC0, 0xC1,   // deliberately not valid UTF-8
            };

            PlatformClipboard.SetData(ClipboardFormat.Png, payload);

            Assert.True(PlatformClipboard.ContainsData(ClipboardFormat.Png),
                "ContainsData said no immediately after SetData");

            byte[]? got = PlatformClipboard.GetData(ClipboardFormat.Png);
            Assert.True(got is not null, "GetData returned null for a format just written");
            Assert.Equal(payload, got!);
        }

        /// <summary>
        /// Clear must actually clear. A no-op Clear is invisible in every other test here -- they all
        /// write before they read -- and leaves stale content readable after an app thinks it wiped it.
        /// </summary>
        [Fact]
        public void Clear_EmptiesTheClipboard()
        {
            RequireWritableClipboard();

            PlatformClipboard.SetString("about to be cleared");
            Assert.True(PlatformClipboard.ContainsString(), "the precondition write did not take");

            PlatformClipboard.Clear();

            Assert.True(string.IsNullOrEmpty(PlatformClipboard.GetString()),
                $"after Clear, GetString still returned '{PlatformClipboard.GetString()}'");
        }

        /// <summary>
        /// Asking for a format that was never written must report absent and return null -- not throw,
        /// and not hand back the text as bytes. A paste path that trusts a wrong answer here decodes
        /// whatever it gets.
        /// </summary>
        [Fact]
        public void AbsentFormat_ReportsMissingRatherThanGuessing()
        {
            RequireWritableClipboard();

            PlatformClipboard.Clear();
            PlatformClipboard.SetString("text only, no image");

            if (PlatformClipboard.ContainsData(ClipboardFormat.Png))
            {
                // Some compositors and pasteboards offer a rendered image for text. That is the
                // system's choice, not a bug -- but it must then actually produce the bytes.
                Assert.True(PlatformClipboard.GetData(ClipboardFormat.Png) is not null,
                    "ContainsData(Png) said yes for a text-only clipboard but GetData returned null");
                return;
            }

            Assert.True(PlatformClipboard.GetData(ClipboardFormat.Png) is null,
                "GetData returned bytes for a format ContainsData says is absent");
        }

        /// <summary>
        /// The READ path, which needs no selection ownership and therefore runs on every platform --
        /// including an idle Wayland session where every test above skips.
        ///
        /// It asserts self-consistency rather than a value, because what is on the user's clipboard
        /// is unknown and unknowable here: ContainsString and GetString are separate calls into the
        /// backend, and they must agree. A probe that says "yes" while the fetch returns nothing is
        /// how a paste menu ends up enabled over an empty clipboard; a read that throws on an empty
        /// or foreign-format clipboard takes the app down on Ctrl+V.
        /// </summary>
        [Fact]
        public void ReadPath_IsSelfConsistent_AndNeverThrows()
        {
            RequireClipboard();

            bool contains = PlatformClipboard.ContainsString();
            string? text = PlatformClipboard.GetString();

            if (contains)
            {
                Assert.True(text is not null,
                    "ContainsString reported text but GetString returned null");
            }
            else
            {
                Assert.True(string.IsNullOrEmpty(text),
                    $"ContainsString reported no text but GetString returned '{text}'");
            }

            // An unwritten binary format must answer, not throw, whatever the clipboard holds.
            bool hasPng = PlatformClipboard.ContainsData(ClipboardFormat.Png);
            byte[]? png = PlatformClipboard.GetData(ClipboardFormat.Png);
            if (!hasPng)
                Assert.True(png is null, "GetData returned bytes for a format ContainsData says is absent");
        }

        private static string? SafeGetString()
        { try { return PlatformClipboard.GetString(); } catch { return null; } }

        private static void SafeSetString(string s)
        { try { PlatformClipboard.SetString(s); } catch { } }
    }
}
