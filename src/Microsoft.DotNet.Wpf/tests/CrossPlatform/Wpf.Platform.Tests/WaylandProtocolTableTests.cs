// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The hand-authored wl_interface tables in WlProtocols, and the listener vtables indexed by them.
//
// These run EVERYWHERE, including on a machine with no libwayland at all. That is not an accident:
// Wl.Interface falls back to the authored tables when libwayland exports no symbol for a name, and
// building those tables is pure managed allocation. So the half of the Wayland backend that is
// pure transcription can be checked on the machine the suite actually runs on, while the half that
// needs a compositor cannot be checked anywhere.
//
// Worth checking, because none of it is a compile error and none of it is even a crash near the
// mistake:
//
//   * An event's position in the table IS its wire opcode, and it is also its index into the
//     listener vtable. Insert one event in the wrong place and every later event is delivered to
//     the handler for its neighbour -- a pressure value arriving as a tilt.
//   * A listener vtable shorter than the interface's event count makes libwayland read past the end
//     of the array and call whatever is there.
//   * An EVENT argument of type new_id has libwayland construct the proxy while demarshalling,
//     using types[] -- and it cannot construct one for a null interface. zwp_tablet_seat_v2.pad_added
//     is such an event, which is why the pad interfaces have tables even though nothing listens to
//     them: a client that owns a Wacom with a pad would otherwise die on the first one.
//
// The counts and positions below are transcribed from the protocol XML independently of the tables
// under test (protocols/tablet-v2.xml). protocols/validate-tables.py checks the whole file against
// the XML mechanically and in more detail; this covers what a test run can catch on its own.
//

using System;
using MS.Internal.Interop.Wayland;
using Xunit;

namespace Wpf.Platform.Tests
{
    public sealed unsafe class WaylandProtocolTableTests
    {
        /// <summary>Every interface reachable through a new_id EVENT argument, and so needed.</summary>
        public static TheoryData<string, int, int> TabletInterfaces => new()
        {
            // name, requests, events
            { "zwp_tablet_manager_v2", 2, 0 },
            { "zwp_tablet_seat_v2", 1, 3 },
            { "zwp_tablet_tool_v2", 2, 19 },
            { "zwp_tablet_v2", 1, 6 },
            { "zwp_tablet_pad_v2", 2, 8 },
            { "zwp_tablet_pad_group_v2", 1, 7 },
            { "zwp_tablet_pad_ring_v2", 2, 4 },
            { "zwp_tablet_pad_strip_v2", 2, 4 },
            { "zwp_tablet_pad_dial_v2", 2, 2 },
        };

        [Theory]
        [MemberData(nameof(TabletInterfaces))]
        public void TabletTableHasTheRightShape(string name, int requests, int events)
        {
            var iface = (WlInterface*)Wl.Interface(name);

            Assert.True(iface is not null, $"{name} has no wl_interface table");
            Assert.Equal(name, Wl.FromUtf8(iface->name));
            Assert.Equal(requests, iface->method_count);
            Assert.Equal(events, iface->event_count);
        }

        /// <summary>
        /// The events the tool listener actually decodes, at the opcodes the protocol gives them.
        /// A shift here is the failure that produces plausible nonsense rather than an error.
        /// </summary>
        [Theory]
        [InlineData(6, "proximity_in", "uoo")]
        [InlineData(7, "proximity_out", "")]
        [InlineData(8, "down", "u")]
        [InlineData(9, "up", "")]
        [InlineData(10, "motion", "ff")]
        [InlineData(11, "pressure", "u")]
        [InlineData(13, "tilt", "ff")]
        [InlineData(17, "button", "uuu")]
        [InlineData(18, "frame", "u")]
        public void TabletToolEventIsAtItsProtocolOpcode(int opcode, string name, string signature)
        {
            var iface = (WlInterface*)Wl.Interface("zwp_tablet_tool_v2");
            var events = (WlMessage*)iface->events;

            Assert.Equal(name, Wl.FromUtf8(events[opcode].name));
            Assert.Equal(signature, Wl.FromUtf8(events[opcode].signature));
        }

        /// <summary>
        /// The three events that announce a new object resolve the interface libwayland will build
        /// the proxy from. A null there is not a protocol error -- it is a client that dies the
        /// moment a tablet with a pad is plugged in.
        /// </summary>
        [Theory]
        [InlineData(0, "tablet_added")]
        [InlineData(1, "tool_added")]
        [InlineData(2, "pad_added")]
        public void TabletSeatConstructsTheObjectItAnnounces(int opcode, string name)
        {
            var iface = (WlInterface*)Wl.Interface("zwp_tablet_seat_v2");
            var events = (WlMessage*)iface->events;

            Assert.Equal(name, Wl.FromUtf8(events[opcode].name));
            Assert.Equal("n", Wl.FromUtf8(events[opcode].signature));

            var types = (IntPtr*)events[opcode].types;
            Assert.True(types[0] != IntPtr.Zero, $"{name} names an interface with no table");
        }

        /// <summary>
        /// The pad's own cascade. Nothing listens to any of it, but libwayland still builds every
        /// one of these proxies as the events arrive.
        /// </summary>
        [Fact]
        public void PadCascadeResolvesEndToEnd()
        {
            AssertEventConstructs("zwp_tablet_pad_v2", 0, "group");
            AssertEventConstructs("zwp_tablet_pad_group_v2", 1, "ring");
            AssertEventConstructs("zwp_tablet_pad_group_v2", 2, "strip");
            AssertEventConstructs("zwp_tablet_pad_group_v2", 6, "dial");

            static void AssertEventConstructs(string owner, int opcode, string name)
            {
                var iface = (WlInterface*)Wl.Interface(owner);
                var events = (WlMessage*)iface->events;
                var types = (IntPtr*)events[opcode].types;

                Assert.Equal(name, Wl.FromUtf8(events[opcode].name));
                Assert.True(types[0] != IntPtr.Zero, $"{owner}.{name} names an interface with no table");
            }
        }

        /// <summary>
        /// Builds the tablet's listener vtables, which checks each against its interface's event
        /// count. On Linux this runs when a tablet manager is advertised, which is to say almost
        /// never on a developer's machine and never in this suite.
        /// </summary>
        [Fact]
        public void TabletListenersMatchTheirInterfaces()
        {
            WaylandTablet.EnsureListeners();
        }

        /// <summary>
        /// A vtable that does NOT match is rejected. Without this, the check above passes just as
        /// happily against a Wl.Vtable overload that forgot to check anything.
        /// </summary>
        [Fact]
        public void AShortListenerVtableIsRejected()
        {
            var handlers = new IntPtr[3];   // zwp_tablet_tool_v2 has 19 events

            InvalidOperationException e = Assert.Throws<InvalidOperationException>(
                () => Wl.Vtable("zwp_tablet_tool_v2", handlers));

            Assert.Contains("19", e.Message);
        }
    }
}
