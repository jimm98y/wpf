// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The mouse cursor.
//
// Wayland has no "set the cursor" call in the sense the other platforms mean it. A client owns the
// cursor image only while the pointer is over its surface, and it sets one by attaching a buffer to
// a dedicated cursor surface via wl_pointer.set_cursor -- which additionally requires the serial
// from the most recent wl_pointer.enter. That is a lot of machinery for "show an I-beam", and it is
// also how a client ends up with a cursor that does not match the user's theme.
//
// cursor-shape-v1 replaces all of it: the client names a shape from a standard enumeration and the
// COMPOSITOR draws it, correctly themed and correctly scaled on every output. mutter implements it,
// so it is the path taken here. Where it is missing, the cursor is simply left alone rather than
// hand-rolling theme loading through libwayland-cursor -- a wrong-looking cursor is a much smaller
// problem than the alternative this replaces, which on Linux was a call into CocoaWindow.SetCursor
// and therefore a libobjc DllNotFoundException.
//

using System;
using System.Runtime.Versioning;
using static MS.Internal.Interop.Wayland.WlProtocols;

namespace MS.Internal.Interop.Wayland
{
    [SupportedOSPlatform("linux")]
    internal static unsafe class WaylandCursor
    {
        private static IntPtr s_pointer;
        private static IntPtr s_shapeDevice;
        private static uint s_currentShape;

        internal static void AttachPointer(IntPtr pointer)
        {
            s_pointer = pointer;
            IntPtr manager = WaylandDisplay.CursorShapeManager;
            if (manager == IntPtr.Zero || pointer == IntPtr.Zero) return;

            s_shapeDevice = Wl.Construct(manager, WP_CURSOR_SHAPE_MANAGER_GET_POINTER,
                "wp_cursor_shape_device_v1", 1, WlArgument.NewId(), WlArgument.Ptr(pointer));
        }

        /// <summary>
        /// Re-apply the current shape when the pointer enters a surface. Required, not cosmetic: the
        /// cursor is undefined on enter, so a client that does not set one shows whatever the
        /// previously-focused client happened to leave behind.
        /// </summary>
        internal static void OnPointerEnter(uint serial)
        {
            if (s_currentShape != 0) Apply(s_currentShape, serial);
        }

        /// <summary>Set the cursor from a WPF cursor name (see HwndMouseInputProvider).</summary>
        internal static void SetCursor(string name)
        {
            uint shape = MapName(name);
            if (shape == 0 || shape == s_currentShape) return;
            s_currentShape = shape;
            Apply(shape, WaylandInput.EnterSerial);
        }

        private static void Apply(uint shape, uint serial)
        {
            // A stale serial is silently ignored by the compositor, so there is nothing to do until
            // the pointer has actually entered one of our surfaces.
            if (s_shapeDevice == IntPtr.Zero || serial == 0) return;
            try
            {
                Wl.Request(s_shapeDevice, WP_CURSOR_SHAPE_DEVICE_SET_SHAPE,
                    WlArgument.UInt(serial), WlArgument.UInt(shape));
            }
            catch { }
        }

        /// <summary>
        /// WPF cursor names to cursor-shape-v1 shapes. The names are the ones
        /// HwndMouseInputProvider derives from System.Windows.Input.Cursors.
        /// </summary>
        private static uint MapName(string name) => name switch
        {
            "Arrow" or "AppStarting" => CURSOR_SHAPE_DEFAULT,
            "IBeam" => CURSOR_SHAPE_TEXT,
            "Hand" => CURSOR_SHAPE_POINTER,
            "Cross" => CURSOR_SHAPE_CROSSHAIR,
            "Wait" => CURSOR_SHAPE_WAIT,
            "Help" => CURSOR_SHAPE_HELP,
            "No" => CURSOR_SHAPE_NOT_ALLOWED,
            "SizeAll" => CURSOR_SHAPE_ALL_SCROLL,
            "SizeNS" => CURSOR_SHAPE_NS_RESIZE,
            "SizeWE" => CURSOR_SHAPE_EW_RESIZE,
            "SizeNWSE" => CURSOR_SHAPE_NWSE_RESIZE,
            "SizeNESW" => CURSOR_SHAPE_NESW_RESIZE,
            "ScrollAll" => CURSOR_SHAPE_ALL_SCROLL,
            "ScrollNS" => CURSOR_SHAPE_NS_RESIZE,
            "ScrollWE" => CURSOR_SHAPE_EW_RESIZE,
            "UpArrow" => CURSOR_SHAPE_DEFAULT,
            "None" => 0,
            _ => CURSOR_SHAPE_DEFAULT,
        };
    }
}
