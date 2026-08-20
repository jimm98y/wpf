// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The displays, enumerated.
//
// There used to be exactly one, everywhere: MonitorFromWindow and MonitorFromRect handed back a
// single fixed handle, and GetMonitorInfo filled its struct from the primary screen whatever handle
// it was given and flagged it primary. A window on a second display was therefore told it had the
// primary's bounds, work area and DPI -- which reaches WindowStartupLocation.CenterScreen, the
// RestoreBounds conversion, popup clamping and maximize sizing.
//
// Written to hold whatever displays are attached, in whatever arrangement, because that is not
// something a test can choose: the assertions are invariants (one primary, work area inside its
// monitor, a point maps back to the display it came from), not coordinates.
//

using System;
using MS.Internal.Interop;
using Xunit;

namespace Wpf.Platform.Tests
{
    public sealed class MonitorTests
    {
        [Fact]
        public void ThereIsAtLeastOneDisplayAndExactlyOnePrimary()
        {
            Assert.SkipUnless(HeadFixture.DisplayAvailableStatic, "requires a display server");

            int count = PlatformWindow.GetMonitorCount();
            Assert.True(count >= 1, $"the head reports {count} displays");

            int primaries = 0;
            for (int i = 0; i < count; i++)
            {
                Assert.True(PlatformWindow.GetMonitorPixels(i,
                        out int ml, out int mt, out int mr, out int mb,
                        out int wl, out int wt, out int wr, out int wb, out bool isPrimary),
                    $"display {i} of {count} could not be described");

                Assert.True(mr > ml && mb > mt, $"display {i} is empty or inverted: ({ml},{mt})-({mr},{mb})");
                Assert.True(wr > wl && wb > wt, $"display {i}'s work area is empty or inverted");
                Assert.True(wl >= ml && wt >= mt && wr <= mr && wb <= mb,
                    $"display {i}'s work area ({wl},{wt})-({wr},{wb}) is not inside its monitor ({ml},{mt})-({mr},{mb})");

                if (isPrimary) primaries++;
            }

            Assert.True(primaries == 1, $"{primaries} of {count} displays claim to be the primary; exactly one may");
        }

        /// <summary>
        /// Every display's own centre maps back to that display. The regression test for the fixed
        /// handle: with one fabricated monitor every point answered 0, so this passes only when the
        /// displays are told apart.
        /// </summary>
        [Fact]
        public void APointOnADisplayMapsBackToThatDisplay()
        {
            Assert.SkipUnless(HeadFixture.DisplayAvailableStatic, "requires a display server");

            int count = PlatformWindow.GetMonitorCount();
            for (int i = 0; i < count; i++)
            {
                Assert.True(PlatformWindow.GetMonitorPixels(i,
                    out int ml, out int mt, out int mr, out int mb,
                    out _, out _, out _, out _, out _));

                int cx = ml + (mr - ml) / 2, cy = mt + (mb - mt) / 2;
                int found = PlatformWindow.MonitorIndexFromPointPixels(cx, cy);

                Assert.True(found == i,
                    $"the centre of display {i}, ({cx},{cy}) in ({ml},{mt})-({mr},{mb}), was attributed "
                    + $"to display {found}");
            }
        }

        /// <summary>
        /// Displays do not overlap, which is what makes the point lookup above unambiguous. macOS
        /// arranges them edge to edge; a head that reported them all at the origin would satisfy
        /// every other assertion here and fail this one.
        /// </summary>
        [Fact]
        public void DisplaysDoNotOverlap()
        {
            Assert.SkipUnless(HeadFixture.DisplayAvailableStatic, "requires a display server");

            int count = PlatformWindow.GetMonitorCount();
            Assert.SkipWhen(count < 2, "only one display is attached");

            for (int i = 0; i < count; i++)
            {
                PlatformWindow.GetMonitorPixels(i, out int al, out int at, out int ar, out int ab,
                                                out _, out _, out _, out _, out _);
                for (int j = i + 1; j < count; j++)
                {
                    PlatformWindow.GetMonitorPixels(j, out int bl, out int bt, out int br, out int bb,
                                                    out _, out _, out _, out _, out _);
                    bool overlaps = al < br && bl < ar && at < bb && bt < ab;
                    Assert.False(overlaps,
                        $"displays {i} ({al},{at})-({ar},{ab}) and {j} ({bl},{bt})-({br},{bb}) overlap");
                }
            }
        }
    }
}
