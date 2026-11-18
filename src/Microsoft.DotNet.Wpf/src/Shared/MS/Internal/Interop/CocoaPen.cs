// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// A graphics tablet on macOS: the pen as a contact, beside the mouse it already drives.
//
// A Mac has no touchscreen and its trackpad is not a digitizer of the screen (PlatformGesture), but
// a tablet IS one: its surface is mapped onto the screen, and AppKit reports the pen as the MOUSE
// with the tablet's measurements riding on the same events. A mouse event whose subtype is
// NSEventSubtypeTabletPoint carries pressure, tilt, rotation and the pen's buttons; a proximity
// event -- NSEventTypeTabletProximity, or a mouse event of subtype NSEventSubtypeTabletProximity --
// says which end of the pen came into range, and what the device can measure.
//
// So the mouse is left exactly as it is, and a left-button press, drag and release that carry
// tablet data are ALSO reported through the touch seam, as every other head reports a pen: the
// contact first, then the mouse, so the ink path reads what the pen measured when the press
// arrives. A mouse or a trackpad sends no tablet subtype and never reaches the seam at all.
//
// Coalescing. AppKit merges queued drags into one, which for a pen discards most of what it
// sampled. While a pen stroke is down that merging is turned off ([NSEvent
// setMouseCoalescingEnabled:NO]) and done here instead: consecutive pen drags already waiting in
// the queue become one delivered drag that carries the earlier ones as its coalesced samples
// (CocoaWindow.GatherPenDrags). The mouse sees the same one event per batch it saw before; the ink
// sees every point.
//

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace MS.Internal.Interop
{
    internal static class CocoaPen
    {
        // NSEventType, NSEventSubtype, NSPointingDeviceType and NSEventButtonMask values.
        internal const ulong NSLeftMouseDown = 1, NSLeftMouseUp = 2, NSMouseMoved = 5, NSLeftMouseDragged = 6,
                             NSRightMouseDown = 3, NSRightMouseUp = 4, NSRightMouseDragged = 7,
                             NSOtherMouseDown = 25, NSOtherMouseUp = 26, NSOtherMouseDragged = 27,
                             NSEventTypeTabletPoint = 23, NSEventTypeTabletProximity = 24;
        internal const int NSEventSubtypeTabletPoint = 1, NSEventSubtypeTabletProximity = 2;
        internal const ulong NSPointingDeviceTypeEraser = 3;
        internal const ulong NSEventButtonMaskPenLowerSide = 2;

        // The proximity event's capability mask: IOKit's NX_TABLET_CAPABILITY_* bits, which say what
        // the device in range actually measures.
        internal const ulong CapabilityButtons = 0x0040, CapabilityTiltX = 0x0080, CapabilityTiltY = 0x0100,
                             CapabilityRotation = 0x2000;

        /// <summary>
        ///  The degrees a full-scale tilt (NSEvent.tilt of +/-1) stands for. AppKit scales tilt to
        ///  the device's range rather than reporting an angle; a Wacom pen's range is +/-60 degrees
        ///  of physical lean, and 60 is what the other toolkits on macOS take a full tilt to mean.
        /// </summary>
        internal const double FullTiltDegrees = 60.0;

        /// <summary>
        ///  The seam's id for the pen. One pen at a time touches a tablet, and the id sits far from the
        ///  small integers other heads hand fingers, as Wayland's tool ids do.
        /// </summary>
        internal const int ContactId = 0x7AB1E7;

        /// <summary>What a tablet event said, as AppKit reports it.</summary>
        internal struct TabletReading
        {
            public bool IsPoint;            // carries pressure, tilt, rotation and buttons
            public bool IsProximity;        // carries which device came into range, or left
            public double Pressure;         // 0..1
            public double TiltX, TiltY;     // -1..1, AppKit's y up
            public double Rotation;         // degrees, counterclockwise
            public ulong Buttons;           // NSEventButtonMask
            public bool EnteringProximity;
            public ulong PointingDeviceType;
            public ulong CapabilityMask;
        }

        // ---- what is in range ----

        private static bool s_eraser;
        private static ulong s_capabilities;
        private static bool s_capabilitiesKnown;

        /// <summary>The eraser end of the pen is the one in range.</summary>
        internal static bool EraserInProximity => s_eraser;

        // ---- the stroke ----

        private static bool s_down;
        private static IntPtr s_window;
        private static bool s_restoreCoalescing;
        private static readonly List<TouchSample> s_coalesced = new();

        /// <summary>A pen stroke is down: its left-button press carried tablet data.</summary>
        internal static bool IsStrokeDown => s_down;

        /// <summary>
        ///  Reads the tablet fields of an event, if it has any. Only touches the properties AppKit
        ///  defines for the event's type and subtype: asking a plain mouse event for its tilt raises
        ///  an Objective-C exception, which no managed caller survives.
        /// </summary>
        internal static bool Read(IntPtr evt, out TabletReading reading)
        {
            reading = default;
            if (evt == IntPtr.Zero || !AppKitLoaded()) return false;

            ulong type = SendNUInt(evt, Sel("type"));
            int subtype = 0;
            if (IsMouseType(type)) subtype = (short)(SendNUInt(evt, Sel("subtype")) & 0xFFFF);

            reading.IsPoint = type == NSEventTypeTabletPoint || (IsMouseType(type) && subtype == NSEventSubtypeTabletPoint);
            reading.IsProximity = type == NSEventTypeTabletProximity || (IsMouseType(type) && subtype == NSEventSubtypeTabletProximity);

            if (reading.IsPoint)
            {
                reading.Pressure = SendFloat(evt, Sel("pressure"));
                NSPoint tilt = SendPoint(evt, Sel("tilt"));
                reading.TiltX = tilt.x;
                reading.TiltY = tilt.y;
                reading.Rotation = SendFloat(evt, Sel("rotation"));
                reading.Buttons = SendNUInt(evt, Sel("buttonMask"));
            }
            else if (reading.IsProximity)
            {
                reading.EnteringProximity = SendBool(evt, Sel("isEnteringProximity"));
                reading.PointingDeviceType = SendNUInt(evt, Sel("pointingDeviceType"));
                reading.CapabilityMask = SendNUInt(evt, Sel("capabilityMask"));
            }
            return reading.IsPoint || reading.IsProximity;
        }

        /// <summary>
        ///  A tablet point as the seam's PenState: degrees, y towards the user, twist clockwise.
        /// </summary>
        /// <param name="capabilities">The device's capability mask from its proximity event, or null
        /// when no proximity has been seen -- the app started with the pen already in range -- and
        /// every axis AppKit carried is taken as measured.</param>
        internal static PenState ToPenState(in TabletReading reading, bool eraser, ulong? capabilities)
        {
            bool hasTilt = capabilities is not { } c1 || (c1 & (CapabilityTiltX | CapabilityTiltY)) != 0;
            bool hasButtons = capabilities is not { } c2 || (c2 & CapabilityButtons) != 0;

            // Rotation is the one axis a pen without the sensor reports as an exact zero rather than
            // leaving out, so without a capability mask a zero is taken as "no sensor".
            bool hasRotation = capabilities is { } c3 ? (c3 & CapabilityRotation) != 0 : reading.Rotation != 0;

            // AppKit's y axis points UP the screen, as all of AppKit's do; the seam's tilt Y is a lean
            // towards the user, down the screen. And AppKit's rotation counts counterclockwise where
            // Windows' twist counts clockwise.
            double tiltX = hasTilt ? Math.Clamp(reading.TiltX, -1, 1) * FullTiltDegrees : double.NaN;
            double tiltY = hasTilt ? -Math.Clamp(reading.TiltY, -1, 1) * FullTiltDegrees : double.NaN;
            double twist = hasRotation ? PenGeometry.NormalizeDegrees(360.0 - reading.Rotation) : double.NaN;
            bool? barrel = hasButtons ? (reading.Buttons & NSEventButtonMaskPenLowerSide) != 0 : null;

            return new PenState(Math.Clamp(reading.Pressure, 0, 1), tiltX, tiltY, eraser,
                                twist, double.NaN, double.NaN, barrel);
        }

        /// <summary>The PenState of an event, with what is known about the device in range.</summary>
        internal static PenState PenOf(in TabletReading reading)
            => ToPenState(reading, s_eraser, s_capabilitiesKnown ? s_capabilities : null);

        /// <summary>The pen came into range, or left it: which end, and what it measures.</summary>
        internal static void NoteProximity(in TabletReading reading)
        {
            if (!reading.IsProximity) return;
            if (reading.EnteringProximity)
            {
                s_eraser = reading.PointingDeviceType == NSPointingDeviceTypeEraser;
                s_capabilities = reading.CapabilityMask;
                s_capabilitiesKnown = reading.CapabilityMask != 0;
            }
            else
            {
                // Out of range: the next pen in may be the other end, or another pen entirely.
                s_eraser = false;
                s_capabilitiesKnown = false;
            }
        }

        /// <summary>
        ///  An event is about to be delivered to the mouse. Reports it to the touch seam first when it
        ///  is a pen tip's press, drag or release.
        /// </summary>
        /// <param name="windowHandle">The window the mouse event is going to.</param>
        /// <param name="screenX">The event's position in screen device pixels.</param>
        internal static void BeforeMouse(IntPtr evt, ulong type, IntPtr windowHandle, int screenX, int screenY)
        {
            try
            {
                if (type != NSLeftMouseDown && type != NSLeftMouseDragged && type != NSLeftMouseUp) return;
                IPlatformTouchSink sink = PlatformTouch.Sink;

                if (type == NSLeftMouseUp)
                {
                    if (!s_down) return;
                    EndStroke();
                    sink?.TouchUp(s_window, ContactId, screenX, screenY, Timestamp());
                    return;
                }

                if (!Read(evt, out TabletReading reading) || !reading.IsPoint)
                {
                    // A drag without tablet data inside a pen stroke is the pen's own -- a driver
                    // that reports some samples without the subtype -- and is dropped rather than
                    // ending the stroke; the mouse still has it.
                    return;
                }
                PenState pen = PenOf(reading);

                if (type == NSLeftMouseDown)
                {
                    if (s_down)
                    {
                        // A press with no release before it: AppKit lost the up (a window closed
                        // under the pen). The old contact is cancelled rather than left alive.
                        EndStroke();
                        sink?.TouchCancel(s_window, ContactId);
                    }
                    BeginStroke(windowHandle);
                    sink?.TouchDown(windowHandle, ContactId, screenX, screenY, pen, Timestamp());
                    return;
                }

                if (!s_down) return;
                TouchSample[] coalesced = s_coalesced.Count > 0 ? s_coalesced.ToArray() : null;
                s_coalesced.Clear();
                if (sink is null) return;
                if (coalesced is null) sink.TouchMove(s_window, ContactId, screenX, screenY, pen, Timestamp());
                else sink.TouchMoveCoalesced(s_window, ContactId, screenX, screenY, pen, Timestamp(), coalesced);
            }
            catch
            {
                // A stray event must never break the pump.
            }
        }

        /// <summary>
        ///  The mouse has had the event. A release that never reached <see cref="BeforeMouse"/> -- one
        ///  dropped or rescued on the way, delivered to a window holding capture -- still ends the
        ///  pen's contact, which would otherwise stay alive in WPF forever.
        /// </summary>
        internal static void AfterMouse(ulong type)
        {
            if (!s_down || type != NSLeftMouseUp) return;
            EndStroke();
            try { PlatformTouch.Sink?.TouchCancel(s_window, ContactId); } catch { }
        }

        /// <summary>
        ///  A pen drag folded into a later one: its position and measurement, kept for the drag that
        ///  is delivered.
        /// </summary>
        internal static void AddCoalesced(int screenX, int screenY, in PenState pen)
            => s_coalesced.Add(new TouchSample(screenX, screenY, pen, Timestamp()));

        /// <summary>A left drag that carries tablet data, inside a pen stroke.</summary>
        internal static bool IsPenDrag(IntPtr evt)
        {
            if (!s_down || evt == IntPtr.Zero) return false;
            if (SendNUInt(evt, Sel("type")) != NSLeftMouseDragged) return false;
            return (short)(SendNUInt(evt, Sel("subtype")) & 0xFFFF) == NSEventSubtypeTabletPoint;
        }

        private static void BeginStroke(IntPtr windowHandle)
        {
            s_down = true;
            s_window = windowHandle;
            s_coalesced.Clear();

            // AppKit's own merging off while the pen is down; restored at the lift, so a mouse or a
            // trackpad afterwards behaves exactly as it always has.
            IntPtr nsEvent = objc_getClass("NSEvent");
            if (nsEvent == IntPtr.Zero) return;
            s_restoreCoalescing = SendBool(nsEvent, Sel("isMouseCoalescingEnabled"));
            if (s_restoreCoalescing) SendVoidBool(nsEvent, Sel("setMouseCoalescingEnabled:"), false);
        }

        private static void EndStroke()
        {
            s_down = false;
            s_coalesced.Clear();
            if (!s_restoreCoalescing) return;
            s_restoreCoalescing = false;
            IntPtr nsEvent = objc_getClass("NSEvent");
            if (nsEvent != IntPtr.Zero) SendVoidBool(nsEvent, Sel("setMouseCoalescingEnabled:"), true);
        }

        private static uint Timestamp() => (uint)Environment.TickCount;

        private static bool IsMouseType(ulong t) =>
            t == NSLeftMouseDown || t == NSLeftMouseUp || t == NSRightMouseDown || t == NSRightMouseUp ||
            t == NSMouseMoved || t == NSLeftMouseDragged || t == NSRightMouseDragged ||
            t == NSOtherMouseDown || t == NSOtherMouseUp || t == NSOtherMouseDragged;

        private static bool AppKitLoaded() => objc_getClass("NSEvent") != IntPtr.Zero;

        // ---- interop ----

        private const string ObjC = "/usr/lib/libobjc.A.dylib";

        [StructLayout(LayoutKind.Sequential)]
        private struct NSPoint
        {
            public double x;
            public double y;
        }

        private static IntPtr Sel(string name) => sel_registerName(name);

        [DllImport(ObjC)] private static extern IntPtr objc_getClass(string name);
        [DllImport(ObjC)] private static extern IntPtr sel_registerName(string name);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern ulong SendNUInt(IntPtr receiver, IntPtr selector);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern float SendFloat(IntPtr receiver, IntPtr selector);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern NSPoint SendPoint(IntPtr receiver, IntPtr selector);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] [return: MarshalAs(UnmanagedType.I1)] private static extern bool SendBool(IntPtr receiver, IntPtr selector);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendVoidBool(IntPtr receiver, IntPtr selector, [MarshalAs(UnmanagedType.I1)] bool arg);
    }
}
