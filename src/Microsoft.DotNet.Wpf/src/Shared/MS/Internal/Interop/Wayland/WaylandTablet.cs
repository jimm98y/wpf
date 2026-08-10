// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// tablet-v2: the stylus. A pen is not a mouse and not a finger, and Wayland says so with a protocol
// of its own -- three objects deep before a single coordinate arrives:
//
//     zwp_tablet_manager_v2  --get_tablet_seat(wl_seat)-->  zwp_tablet_seat_v2
//     zwp_tablet_seat_v2     --tool_added------------------>  zwp_tablet_tool_v2   (one per tool)
//
// A "tool" is a physical implement, not a device: the tip and the eraser end of one Wacom pen are
// two zwp_tablet_tool_v2 objects, and each is announced once and then reused every time it comes
// back into proximity.
//
// FOUR THINGS ABOUT THIS PROTOCOL SHAPE THE CODE:
//
//   1. BINDING IT TURNS OFF THE COMPOSITOR'S POINTER EMULATION -- for this client only, and only
//      for the tablet. A pen already worked here as a plain mouse, because the compositor emulated
//      one for a client that had not bound the protocol. The moment we bind, that stops: mutter,
//      KWin and wlroots all check whether the focused surface accepts tablet-v2 before deciding.
//      So this file has to drive the mouse ITSELF as well as the touch seam, or "adding pressure"
//      would have taken the pen from working-without-pressure to not working at all.
//
//   2. THE AXES ARE SEPARATE EVENTS, BATCHED BY `frame`. One physical sample arrives as motion,
//      then pressure, then tilt, then frame. Reporting a position the moment motion arrives would
//      pair it with the PREVIOUS sample's pressure -- visible in ink as a stroke whose width lags
//      the pen by one sample. Everything is accumulated and flushed once, at the frame.
//
//   3. `down` AND `up` CARRY NO POSITION, and `up` carries no serial either. Contact starts and
//      ends wherever the last motion left the tool, which is the same rule wl_touch.up follows.
//
//   4. PRESSURE IS REPORTED WHILE HOVERING. It is not a proxy for contact: the compositor decides
//      what counts as logical contact (some devices need a pressure threshold), and says so with
//      down and up. Pressure is carried on hover moves too, which is correct -- WPF just has
//      nothing that reads it until the tip is down.
//
// The pad -- the buttons, rings and strips on the tablet body -- is deliberately not handled. Its
// wl_interface tables exist all the same; see the note in WlProtocols.
//

#nullable enable

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using static MS.Internal.Interop.Wayland.WlProtocols;

namespace MS.Internal.Interop.Wayland
{
    [SupportedOSPlatform("linux")]
    internal static unsafe class WaylandTablet
    {
        private static IntPtr s_manager;
        private static IntPtr s_seat;
        private static IntPtr s_tabletSeat;

        // Native memory the C side keeps BY POINTER, so it lives as long as the process.
        private static IntPtr* s_tabletSeatListener;
        private static IntPtr* s_toolListener;

        private static readonly Dictionary<IntPtr, ToolState> s_tools = new();

        /// <summary>
        /// Contact ids for pen tips. The seam only requires an id to be unique among the contacts
        /// alive at one time, but wl_touch hands out small integers from zero for fingers, and a pen
        /// resting on a tablet while a finger touches the screen is an ordinary thing to do -- so
        /// tools are numbered somewhere fingers will not reach.
        /// </summary>
        private const int FirstToolContactId = 1000;
        private static int s_nextContactId = FirstToolContactId;

        /// <summary>What one tool has told us since the last frame, and what it is doing now.</summary>
        private sealed class ToolState
        {
            public readonly int ContactId;
            public uint Type;

            /// <summary>The surface the tool is in proximity above, or Zero.</summary>
            public IntPtr Surface;

            /// <summary>True between `down` and `up` -- the tip is in logical contact.</summary>
            public bool IsDown;

            /// <summary>Last known position, surface-local, in wl_fixed as it arrived.</summary>
            public int FixedX, FixedY;
            public bool HaveEverMoved;

            public double X => WaylandInput.Fixed(FixedX);
            public double Y => WaylandInput.Fixed(FixedY);

            // Per-frame accumulator.
            public bool FrameHasMotion;
            public bool FrameHasDown;
            public bool FrameHasUp;
            public bool FrameHasProximityOut;
            public uint FrameTime;
            public uint DownSerial;
            public readonly List<(int Button, bool Pressed, uint Serial)> FrameButtons = new();

            /// <summary>Axes, carried between frames: an axis is only re-sent when it CHANGES.</summary>
            public double Pressure = -1;
            public double TiltX = double.NaN;
            public double TiltY = double.NaN;

            public ToolState(int contactId) => ContactId = contactId;

            /// <summary>
            /// The eraser is not a mode of the pen: it is a SEPARATE tool object, announced with
            /// its own type, so this is fixed for the lifetime of the object rather than something
            /// that changes as the user flips the pen over.
            /// </summary>
            public bool IsInverted => Type == ZWP_TABLET_TOOL_V2_TYPE_ERASER;

            public PenState Pen => new PenState(Pressure, TiltX, TiltY, IsInverted);

            public void ResetFrame()
            {
                FrameHasMotion = FrameHasDown = FrameHasUp = FrameHasProximityOut = false;
                DownSerial = 0;
                FrameButtons.Clear();
            }
        }

        // ---- Setup ------------------------------------------------------------------------------
        //
        // The manager global and the seat can arrive in either order, so both sides call in and the
        // tablet seat is created once both exist -- the same handshake WaylandTextInput uses.

        internal static void AttachManager(IntPtr manager)
        {
            s_manager = manager;
            TryCreateTabletSeat();
        }

        internal static void AttachSeat(IntPtr seat)
        {
            s_seat = seat;
            TryCreateTabletSeat();
        }

        /// <summary>
        /// Builds the two listener vtables, each checked against the wl_interface table it will be
        /// attached to.
        /// </summary>
        /// <remarks>
        /// Deliberately separate from the setup path and free of any call into libwayland, so that a
        /// test can run the check on a machine with no compositor -- which is the only place the
        /// suite runs. A vtable one handler short of its interface is not a compile error and not a
        /// protocol error; it is libwayland calling past the end of the array.
        /// </remarks>
        internal static void EnsureListeners()
        {
            if (s_toolListener is not null) return;

            s_tabletSeatListener = Wl.Vtable("zwp_tablet_seat_v2",
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, void>)&OnTabletAdded,
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, void>)&OnToolAdded,
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, void>)&OnPadAdded);

            // One vtable serves every tool: it is a table of function pointers, and the tool an
            // event belongs to arrives as the proxy argument.
            s_toolListener = Wl.Vtable("zwp_tablet_tool_v2",
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, uint, void>)&OnToolType,
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, uint, uint, void>)&OnToolHardwareSerial,
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, uint, uint, void>)&OnToolHardwareIdWacom,
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, uint, void>)&OnToolCapability,
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void>)&OnToolDone,
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void>)&OnToolRemoved,
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, uint, IntPtr, IntPtr, void>)&OnToolProximityIn,
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void>)&OnToolProximityOut,
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, uint, void>)&OnToolDown,
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void>)&OnToolUp,
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, int, int, void>)&OnToolMotion,
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, uint, void>)&OnToolPressure,
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, uint, void>)&OnToolDistance,
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, int, int, void>)&OnToolTilt,
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, int, void>)&OnToolRotation,
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, int, void>)&OnToolSlider,
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, int, int, void>)&OnToolWheel,
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, uint, uint, uint, void>)&OnToolButton,
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, uint, void>)&OnToolFrame);
        }

        private static void TryCreateTabletSeat()
        {
            if (s_tabletSeat != IntPtr.Zero || s_manager == IntPtr.Zero || s_seat == IntPtr.Zero) return;

            try
            {
                EnsureListeners();

                s_tabletSeat = Wl.Construct(s_manager, ZWP_TABLET_MANAGER_V2_GET_TABLET_SEAT,
                                            "zwp_tablet_seat_v2", Wl.wl_proxy_get_version(s_manager),
                                            WlArgument.NewId(), WlArgument.Ptr(s_seat));
                if (s_tabletSeat == IntPtr.Zero) return;

                Wl.wl_proxy_add_listener(s_tabletSeat, s_tabletSeatListener, IntPtr.Zero);

                WaylandDisplay.LogSink?.Invoke("tablet-v2 bound; the compositor will stop emulating a pointer for the pen.");
            }
            catch (Exception e)
            {
                WaylandDisplay.LogSink?.Invoke("could not create the tablet seat: " + e.Message);
            }
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void OnTabletAdded(IntPtr data, IntPtr tabletSeat, IntPtr tablet)
        {
            // The tablet object describes the hardware (name, vid/pid, sysfs path). Nothing WPF can
            // use, and no listener is attached -- but the proxy libwayland just created for it is
            // real, which is why zwp_tablet_v2 needs a table.
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void OnPadAdded(IntPtr data, IntPtr tabletSeat, IntPtr pad)
        {
            // Not handled; see the file header.
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void OnToolAdded(IntPtr data, IntPtr tabletSeat, IntPtr tool)
        {
            try
            {
                if (tool == IntPtr.Zero || s_tools.ContainsKey(tool)) return;

                s_tools[tool] = new ToolState(s_nextContactId++);

                EnsureListeners();
                Wl.wl_proxy_add_listener(tool, s_toolListener, IntPtr.Zero);
            }
            catch { }
        }

        // ---- Tool description -------------------------------------------------------------------

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void OnToolType(IntPtr data, IntPtr tool, uint toolType)
        {
            // Which physical implement this object is. The one that matters to WPF is the eraser,
            // which is a tool of its own rather than a state of the pen -- see ToolState.IsInverted.
            if (s_tools.TryGetValue(tool, out ToolState? state)) state.Type = toolType;
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void OnToolHardwareSerial(IntPtr data, IntPtr tool, uint hi, uint lo) { }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void OnToolHardwareIdWacom(IntPtr data, IntPtr tool, uint hi, uint lo) { }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void OnToolCapability(IntPtr data, IntPtr tool, uint capability)
        {
            // Which axes this tool has. Nothing to record: an axis the tool does not have simply
            // never sends its event, and the corresponding PenState field stays at its
            // "not reported" value -- which is what the seam wants, rather than a fabricated zero.
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void OnToolDone(IntPtr data, IntPtr tool) { }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void OnToolRemoved(IntPtr data, IntPtr tool)
        {
            try
            {
                if (!s_tools.TryGetValue(tool, out ToolState? state)) return;

                // A tool cannot be removed while in contact -- the compositor sends up and
                // proximity_out first -- but if the connection is being torn down under us, an
                // abandoned contact would stay alive in WPF forever.
                if (state.IsDown && state.Surface != IntPtr.Zero)
                    PlatformTouch.Sink?.TouchCancel(state.Surface, state.ContactId);

                s_tools.Remove(tool);

                // The protocol requires the client to destroy the object it was told about.
                Wl.Destroy(tool, ZWP_TABLET_TOOL_V2_DESTROY);
            }
            catch { }
        }

        // ---- Proximity ---------------------------------------------------------------------------

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void OnToolProximityIn(IntPtr data, IntPtr tool, uint serial, IntPtr tablet, IntPtr surface)
        {
            try
            {
                if (!s_tools.TryGetValue(tool, out ToolState? state)) return;
                WaylandInput.NoteInputSerial(serial);

                state.Surface = surface;

                // Proximity carries no position of its own; the first motion of the frame supplies
                // it. Until then the tool is somewhere over this surface and nothing more is known,
                // so a stale position from the last surface must not be reported.
                state.HaveEverMoved = false;
            }
            catch { }
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void OnToolProximityOut(IntPtr data, IntPtr tool)
        {
            try
            {
                if (!s_tools.TryGetValue(tool, out ToolState? state)) return;

                // Deferred to the frame: the protocol sends the release of every held button, and
                // the tip's own up, BEFORE proximity_out and within the same frame.
                state.FrameHasProximityOut = true;
            }
            catch { }
        }

        // ---- Contact and axes ---------------------------------------------------------------------

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void OnToolDown(IntPtr data, IntPtr tool, uint serial)
        {
            try
            {
                if (!s_tools.TryGetValue(tool, out ToolState? state)) return;
                state.FrameHasDown = true;
                state.DownSerial = serial;
            }
            catch { }
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void OnToolUp(IntPtr data, IntPtr tool)
        {
            try
            {
                if (s_tools.TryGetValue(tool, out ToolState? state)) state.FrameHasUp = true;
            }
            catch { }
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void OnToolMotion(IntPtr data, IntPtr tool, int fx, int fy)
        {
            try
            {
                if (!s_tools.TryGetValue(tool, out ToolState? state)) return;
                state.FixedX = fx;
                state.FixedY = fy;
                state.HaveEverMoved = true;
                state.FrameHasMotion = true;
            }
            catch { }
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void OnToolPressure(IntPtr data, IntPtr tool, uint pressure)
        {
            try
            {
                if (s_tools.TryGetValue(tool, out ToolState? state))
                    state.Pressure = pressure / ZWP_TABLET_TOOL_V2_AXIS_MAX;
            }
            catch { }
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void OnToolDistance(IntPtr data, IntPtr tool, uint distance)
        {
            // How far the tip is above the tablet while hovering. WPF has no concept of it.
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void OnToolTilt(IntPtr data, IntPtr tool, int fx, int fy)
        {
            try
            {
                if (!s_tools.TryGetValue(tool, out ToolState? state)) return;

                // Degrees from vertical, and the sign convention already matches: Wayland's angle is
                // positive when the TOP of the tool leans along the positive axis, and the surface's
                // positive y points down -- so positive x is a lean to the right and positive y a
                // lean towards the user, which is what PenState (and the browser, and Windows) mean.
                state.TiltX = WaylandInput.Fixed(fx);
                state.TiltY = WaylandInput.Fixed(fy);
            }
            catch { }
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void OnToolRotation(IntPtr data, IntPtr tool, int degrees)
        {
            // Twist, for an artist's brush tool. PenState has no field for it yet.
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void OnToolSlider(IntPtr data, IntPtr tool, int position) { }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void OnToolWheel(IntPtr data, IntPtr tool, int degrees, int clicks) { }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void OnToolButton(IntPtr data, IntPtr tool, uint serial, uint button, uint state)
        {
            try
            {
                if (!s_tools.TryGetValue(tool, out ToolState? tracked)) return;
                WaylandInput.NoteInputSerial(serial);

                // A barrel button. Queued rather than raised here so it keeps its place relative to
                // the motion and the tip in the same frame.
                tracked.FrameButtons.Add(((int)button, state != 0, serial));
            }
            catch { }
        }

        // ---- Frame ---------------------------------------------------------------------------------

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void OnToolFrame(IntPtr data, IntPtr tool, uint time)
        {
            try
            {
                if (!s_tools.TryGetValue(tool, out ToolState? state)) return;
                state.FrameTime = time;
                Flush(state);
            }
            catch
            {
                if (s_tools.TryGetValue(tool, out ToolState? state)) state.ResetFrame();
            }
        }

        /// <summary>
        /// Delivers one hardware sample: everything the tool said since the last frame, in the order
        /// WPF has to see it -- move first so the press lands where the pen is, then the tip, then
        /// the barrel buttons, then the lift.
        /// </summary>
        private static void Flush(ToolState state)
        {
            IntPtr surface = state.Surface;
            if (surface == IntPtr.Zero) { state.ResetFrame(); return; }

            IPlatformTouchSink? sink = PlatformTouch.Sink;
            uint time = state.FrameTime;

            // Nothing can be reported before the tool has said where it is: a proximity_in carries
            // no coordinates, and reporting the press at the position it had over the LAST surface
            // would put the click somewhere the pen never was. In practice libinput sends the axes
            // in the same frame as the proximity, so this holds a press back for at most one frame.
            if (!state.HaveEverMoved)
            {
                DeferUntilPositionKnown(state, surface, sink, time);
                return;
            }

            WaylandInput.NotePointerPosition(surface, state.X, state.Y);

            if (state.FrameHasMotion)
            {
                // The mouse follows the pen whether or not the tip is down -- hovering a pen over a
                // button should light it up, exactly as hovering a mouse does.
                RaiseMouse(surface, WaylandMouseKind.Move, 0, state, time);

                if (state.IsDown && sink is not null)
                {
                    ToScreen(surface, state, out int moveX, out int moveY);
                    sink.TouchMove(surface, state.ContactId, moveX, moveY, state.Pen, time);
                }
            }

            if (state.FrameHasDown && !state.IsDown)
            {
                state.IsDown = true;
                WaylandInput.NoteButtonSerial(state.DownSerial, pressed: true);

                if (sink is not null)
                {
                    ToScreen(surface, state, out int downX, out int downY);
                    sink.TouchDown(surface, state.ContactId, downX, downY, state.Pen, time);
                }

                RaiseMouse(surface, WaylandMouseKind.ButtonDown, WaylandInput.BTN_LEFT, state, time);
            }

            for (int i = 0; i < state.FrameButtons.Count; i++)
            {
                (int button, bool pressed, uint serial) = state.FrameButtons[i];
                WaylandInput.NoteButtonSerial(serial, pressed);
                RaiseMouse(surface, pressed ? WaylandMouseKind.ButtonDown : WaylandMouseKind.ButtonUp,
                           MapBarrelButton(button), state, time);
            }

            if (state.FrameHasUp && state.IsDown)
            {
                state.IsDown = false;

                // zwp_tablet_tool_v2.up has no arguments, so there is no serial to record -- only the
                // press count to balance.
                WaylandInput.NoteButtonSerial(0, pressed: false);

                // The up carries no position either, so the seam is told there is none and lifts the
                // contact where it was last seen.
                sink?.TouchUp(surface, state.ContactId, PlatformTouch.NoPosition, PlatformTouch.NoPosition, time);
                RaiseMouse(surface, WaylandMouseKind.ButtonUp, WaylandInput.BTN_LEFT, state, time);
            }

            if (state.FrameHasProximityOut) FlushProximityOut(state, surface, sink, time);

            state.ResetFrame();
        }

        /// <summary>
        /// A frame that arrived before the tool's position did. Only a proximity_out can be acted on;
        /// a press is carried over to the next frame, and a press cancelled by its own release within
        /// the same positionless frame is simply dropped.
        /// </summary>
        private static void DeferUntilPositionKnown(ToolState state, IntPtr surface, IPlatformTouchSink? sink, uint time)
        {
            bool deferredDown = state.FrameHasDown && !state.FrameHasUp;
            uint deferredSerial = state.DownSerial;

            if (state.FrameHasProximityOut) FlushProximityOut(state, surface, sink, time);

            state.ResetFrame();

            if (deferredDown && state.Surface != IntPtr.Zero)
            {
                state.FrameHasDown = true;
                state.DownSerial = deferredSerial;
            }
        }

        private static void FlushProximityOut(ToolState state, IntPtr surface, IPlatformTouchSink? sink, uint time)
        {
            // The pen has left the tablet's hover range. Anything still in contact at this point is
            // abandoned rather than completed -- the compositor sends the up first when there is one.
            if (state.IsDown)
            {
                state.IsDown = false;
                WaylandInput.NoteButtonSerial(0, pressed: false);
                sink?.TouchCancel(surface, state.ContactId);
                RaiseMouse(surface, WaylandMouseKind.ButtonUp, WaylandInput.BTN_LEFT, state, time);
            }

            // Reported as a leave, not merely forgotten, or whatever the pen was hovering stays lit.
            WaylandInput.RaiseSynthesizedMouse(
                new WaylandMouseMessage(surface, WaylandMouseKind.Leave, 0, 0, 0, 0, 0, (int)time));
            WaylandInput.NotePointerLeft(surface);

            state.Surface = IntPtr.Zero;
            state.HaveEverMoved = false;

            // The axes are sticky between frames -- only a CHANGE is re-sent -- so they have to be
            // forgotten here, or the next stroke would open with the pressure the last one ended on.
            state.Pressure = -1;
            state.TiltX = state.TiltY = double.NaN;
        }

        /// <summary>
        /// A stylus's barrel buttons, as the button WPF will see. The convention is the one every
        /// other stack uses and the one users expect: the lower button is a right-click.
        /// </summary>
        private static int MapBarrelButton(int button) => button switch
        {
            WaylandInput.BTN_STYLUS => WaylandInput.BTN_RIGHT,
            WaylandInput.BTN_STYLUS2 => WaylandInput.BTN_MIDDLE,
            _ => WaylandInput.BTN_RIGHT,
        };

        private static void RaiseMouse(IntPtr surface, WaylandMouseKind kind, int button, ToolState state, uint time)
        {
            WaylandInput.RaiseSynthesizedMouse(new WaylandMouseMessage(
                surface, kind, button,
                WaylandInput.Px(state.X, surface), WaylandInput.Py(state.Y, surface),
                0, 0, (int)time));
        }

        private static void ToScreen(IntPtr surface, ToolState state, out int screenX, out int screenY)
            => WaylandInput.ToScreen(surface, state.FixedX, state.FixedY, out screenX, out screenY);
    }
}
