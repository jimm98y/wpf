// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// wl_seat: pointer and keyboard, translated into the same message shapes the Cocoa backend raises,
// so HwndMouseInputProvider/HwndKeyboardInputProvider can reuse their existing report helpers
// (ReportMacInput / ReportMacKey / ReportMacText) unchanged and only gain a new mapper.
//
// Three things about Wayland input differ enough from the other backends to be worth stating:
//
//   1. COORDINATES ARE SURFACE-LOCAL AND FIXED-POINT. wl_fixed_t is signed 24.8, so the value is
//      raw/256.0, and the origin is already top-left -- no flip, unlike Cocoa. That drops straight
//      into ReportMacInput's "client device pixels, top-left" contract.
//
//   2. EVENTS ARE EXPLICITLY BATCHED. A physical mouse move can arrive as motion + axis + button
//      followed by one `frame`. Reporting each sub-event separately produces duplicate WPF input
//      reports and, for scrolling, double-counts. State is accumulated and flushed once per frame,
//      which also coalesces motion -- worth real time on a 2-core box.
//
//   3. THE COMPOSITOR DOES NOT REPEAT KEYS. It sends repeat_info (rate, delay) once and expects the
//      client to synthesise repeats itself. Without that, holding a key types exactly one character.
//      The repeat deadline is published through NextDeadlineMs so the run loop's poll timeout can
//      account for it, otherwise a held key stalls whenever the app is otherwise idle.
//

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using static MS.Internal.Interop.Wayland.WlProtocols;

namespace MS.Internal.Interop.Wayland
{
    /// <summary>
    /// A translated Wayland pointer event. <see cref="X"/>/<see cref="Y"/> are client coordinates in
    /// DEVICE PIXELS with a top-left origin; <see cref="Wheel"/> is in WPF units (120 per notch).
    /// Deliberately the same shape as CocoaWindow.CocoaMouseMessage.
    /// </summary>
    internal readonly struct WaylandMouseMessage
    {
        /// <summary>The wl_surface the event belongs to -- the WPF window handle.</summary>
        public IntPtr Surface { get; }
        public WaylandMouseKind Kind { get; }
        /// <summary>Linux evdev button code (BTN_LEFT = 0x110), or 0 for move/wheel.</summary>
        public int Button { get; }
        public int X { get; }
        public int Y { get; }
        public int Wheel { get; }
        public int WheelHorizontal { get; }
        public int TimestampMs { get; }

        public WaylandMouseMessage(IntPtr surface, WaylandMouseKind kind, int button, int x, int y, int wheel, int wheelH, int timestampMs)
        {
            Surface = surface; Kind = kind; Button = button;
            X = x; Y = y; Wheel = wheel; WheelHorizontal = wheelH; TimestampMs = timestampMs;
        }
    }

    internal enum WaylandMouseKind { Move, ButtonDown, ButtonUp, Wheel, Leave }

    /// <summary>A translated Wayland key event; the same shape as CocoaWindow.CocoaKeyMessage.</summary>
    internal readonly struct WaylandKeyMessage
    {
        public IntPtr Surface { get; }
        public bool IsDown { get; }
        /// <summary>The XKB keysym (XK_Tab, XK_a, ...), which the provider maps to a virtual key.</summary>
        public uint Keysym { get; }
        /// <summary>Raw evdev scancode, for the cases where physical position matters.</summary>
        public uint Scancode { get; }
        /// <summary>Composed text for a key-down, or null when the key produces none.</summary>
        public string? Characters { get; }
        public bool IsRepeat { get; }
        public WaylandModifiers Modifiers { get; }
        public int TimestampMs { get; }

        public WaylandKeyMessage(IntPtr surface, bool isDown, uint keysym, uint scancode, string? characters,
                                 bool isRepeat, WaylandModifiers modifiers, int timestampMs)
        {
            Surface = surface; IsDown = isDown; Keysym = keysym; Scancode = scancode;
            Characters = characters; IsRepeat = isRepeat; Modifiers = modifiers; TimestampMs = timestampMs;
        }
    }

    [Flags]
    internal enum WaylandModifiers
    {
        None = 0, Shift = 1, Control = 2, Alt = 4, Super = 8, CapsLock = 16, NumLock = 32,
    }

    [SupportedOSPlatform("linux")]
    internal static unsafe class WaylandInput
    {
        // Linux evdev button codes (linux/input-event-codes.h).
        internal const int BTN_LEFT = 0x110;
        internal const int BTN_RIGHT = 0x111;
        internal const int BTN_MIDDLE = 0x112;
        internal const int BTN_SIDE = 0x113;
        internal const int BTN_EXTRA = 0x114;

        // A stylus's barrel buttons arrive as these, on the tablet tool rather than the pointer.
        internal const int BTN_STYLUS = 0x14b;
        internal const int BTN_STYLUS2 = 0x14c;
        internal const int BTN_STYLUS3 = 0x149;

        private const uint WL_POINTER_AXIS_VERTICAL_SCROLL = 0;
        private const uint WL_POINTER_AXIS_HORIZONTAL_SCROLL = 1;

        public static event Action<WaylandMouseMessage>? MouseInput;
        public static event Action<WaylandKeyMessage>? KeyInput;

        private static IntPtr s_seat, s_pointer, s_keyboard, s_touch;
        private static IntPtr* s_pointerListener;
        private static IntPtr* s_touchListener;
        private static IntPtr* s_keyboardListener;
        private static IntPtr* s_seatListener;

        // Serials. Wayland requires a RECENT one on requests that act on the user's behalf --
        // wl_pointer.set_cursor, xdg_popup.grab, wl_data_device.set_selection, libdecor's
        // interactive move/resize. A stale or zero serial is silently ignored (or a protocol error).
        internal static uint EnterSerial { get; private set; }
        internal static uint LastInputSerial { get; private set; }

        /// <summary>
        /// Serial of the pointer-button press that is currently held, or 0 if no button is down.
        /// This is the only serial <c>wl_data_device.start_drag</c> accepts.
        /// </summary>
        internal static uint LastPointerButtonSerial { get; private set; }

        private static int s_buttonsDown;
        internal static IntPtr FocusSurface { get; private set; }

        /// <summary>The pointer's last position, SURFACE-LOCAL and in logical units, together with
        /// the surface it was over. Wayland never reports a global cursor position, so this plus the
        /// window's virtual origin is the only way to answer GetCursorPos.</summary>
        internal static double PointerSurfaceX { get; private set; }
        internal static double PointerSurfaceY { get; private set; }
        internal static IntPtr KeyboardFocusSurface { get; private set; }

        // ---- Per-frame accumulator (see note 2 in the file header) --------------------------
        private static IntPtr s_frameSurface;
        private static bool s_haveMotion;
        private static double s_motionX, s_motionY;
        private static int s_frameWheel, s_frameWheelH;
        private static bool s_haveWheel;
        private static uint s_frameTime;

        // Scroll accumulation: axis_value120 (v8) is already in 1/120 units; axis_discrete (v5) is
        // in notches; the plain axis value is a length in logical pixels and needs a conversion.
        private static int s_value120V, s_value120H;
        private static bool s_haveValue120;
        private static double s_axisV, s_axisH;
        private static bool s_haveAxis;

        // ---- xkb ----------------------------------------------------------------------------
        private static IntPtr s_xkbContext, s_xkbKeymap, s_xkbState;
        private static IntPtr s_composeTable, s_composeState;
        private static WaylandModifiers s_modifiers;

        // ---- key repeat ---------------------------------------------------------------------
        private static int s_repeatRate = 25;      // keys per second
        private static int s_repeatDelayMs = 600;
        private static uint s_repeatScancode;
        private static uint s_repeatKeysym;
        private static string? s_repeatText;
        private static long s_repeatNextTicks;     // Environment.TickCount64 of the next synthetic repeat

        public static void AttachSeat(IntPtr seat, uint version)
        {
            if (seat == IntPtr.Zero) return;
            s_seat = seat;

            s_seatListener = Wl.Vtable(
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, uint, void>)&OnSeatCapabilities,
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, void>)&OnSeatName);
            Wl.wl_proxy_add_listener(seat, s_seatListener, IntPtr.Zero);

            if (s_xkbContext == IntPtr.Zero)
            {
                try
                {
                    s_xkbContext = WlXkb.xkb_context_new(WlXkb.XKB_CONTEXT_NO_FLAGS);
                    string locale = Environment.GetEnvironmentVariable("LC_ALL")
                                    ?? Environment.GetEnvironmentVariable("LC_CTYPE")
                                    ?? Environment.GetEnvironmentVariable("LANG")
                                    ?? "C.UTF-8";
                    s_composeTable = WlXkb.xkb_compose_table_new_from_locale(s_xkbContext, locale, WlXkb.XKB_COMPOSE_COMPILE_NO_FLAGS);
                    if (s_composeTable != IntPtr.Zero)
                        s_composeState = WlXkb.xkb_compose_state_new(s_composeTable, WlXkb.XKB_COMPOSE_STATE_NO_FLAGS);
                }
                catch (DllNotFoundException)
                {
                    WaylandDisplay.LogSink?.Invoke("libxkbcommon not found: keyboard input will be unavailable.");
                }
            }
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void OnSeatCapabilities(IntPtr data, IntPtr seat, uint capabilities)
        {
            try
            {
                if ((capabilities & WL_SEAT_CAPABILITY_POINTER) != 0 && s_pointer == IntPtr.Zero)
                {
                    s_pointer = Wl.Construct(seat, WL_SEAT_GET_POINTER, "wl_pointer", Wl.wl_proxy_get_version(seat), WlArgument.NewId());
                    s_pointerListener = Wl.Vtable(
                        (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, uint, IntPtr, int, int, void>)&OnPointerEnter,
                        (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, uint, IntPtr, void>)&OnPointerLeave,
                        (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, uint, int, int, void>)&OnPointerMotion,
                        (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, uint, uint, uint, uint, void>)&OnPointerButton,
                        (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, uint, uint, int, void>)&OnPointerAxis,
                        (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void>)&OnPointerFrame,
                        (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, uint, void>)&OnPointerAxisSource,
                        (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, uint, uint, void>)&OnPointerAxisStop,
                        (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, uint, int, void>)&OnPointerAxisDiscrete,
                        (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, uint, int, void>)&OnPointerAxisValue120,
                        (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, uint, uint, void>)&OnPointerAxisRelativeDirection);
                    Wl.wl_proxy_add_listener(s_pointer, s_pointerListener, IntPtr.Zero);
                    WaylandCursor.AttachPointer(s_pointer);
                }

                if ((capabilities & WL_SEAT_CAPABILITY_TOUCH) != 0 && s_touch == IntPtr.Zero)
                {
                    s_touch = Wl.Construct(seat, WL_SEAT_GET_TOUCH, "wl_touch", Wl.wl_proxy_get_version(seat), WlArgument.NewId());
                    s_touchListener = Wl.Vtable(
                        (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, uint, uint, IntPtr, int, int, int, void>)&OnTouchDown,
                        (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, uint, uint, int, void>)&OnTouchUp,
                        (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, uint, int, int, int, void>)&OnTouchMotion,
                        (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void>)&OnTouchFrame,
                        (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void>)&OnTouchCancel,
                        (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, int, int, int, void>)&OnTouchShape,
                        (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, int, int, void>)&OnTouchOrientation);
                    Wl.wl_proxy_add_listener(s_touch, s_touchListener, IntPtr.Zero);
                }

                if ((capabilities & WL_SEAT_CAPABILITY_KEYBOARD) != 0 && s_keyboard == IntPtr.Zero)
                {
                    s_keyboard = Wl.Construct(seat, WL_SEAT_GET_KEYBOARD, "wl_keyboard", Wl.wl_proxy_get_version(seat), WlArgument.NewId());
                    s_keyboardListener = Wl.Vtable(
                        (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, uint, int, uint, void>)&OnKeymap,
                        (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, uint, IntPtr, IntPtr, void>)&OnKeyboardEnter,
                        (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, uint, IntPtr, void>)&OnKeyboardLeave,
                        (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, uint, uint, uint, uint, void>)&OnKey,
                        (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, uint, uint, uint, uint, uint, void>)&OnModifiers,
                        (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, int, int, void>)&OnRepeatInfo);
                    Wl.wl_proxy_add_listener(s_keyboard, s_keyboardListener, IntPtr.Zero);
                }
            }
            catch { }
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void OnSeatName(IntPtr data, IntPtr seat, IntPtr name) { }

        // ---- Touch ---------------------------------------------------------------------------
        //
        // wl_touch reports CONTACTS, which the pointer protocol cannot express: each carries its own
        // id and lives from a down to an up, several at once. They go straight to the PlatformTouch
        // seam rather than through WaylandMouseMessage, because collapsing them into the mouse is
        // exactly what loses the second finger.
        //
        // The surface pointer IS the window handle here (WaylandWindow.FromHandle takes it), so no
        // mapping is needed -- only the surface-local logical position turned into the screen device
        // pixels the seam wants.
        //
        // A contact's id is unique among those currently down and the compositor reuses it freely
        // afterwards, which is the seam's contract already.

        private static IntPtr s_touchSurface;

        /// <summary>
        /// Surface-local logical coordinates, in wl_fixed, to the screen device pixels the
        /// PlatformTouch seam takes. Shared with the tablet, whose axes use the same units.
        /// </summary>
        internal static void ToScreen(IntPtr surface, int fx, int fy, out int screenX, out int screenY)
        {
            double scale = WaylandDisplay.ScaleForSurface(surface);
            double localX = Fixed(fx) * scale;
            double localY = Fixed(fy) * scale;

            // Through the display's seam rather than WaylandWindow directly: this layer is also
            // driven by a host with no WPF windows at all (WaylandSpike), where the query is null
            // and a surface-local position is already a screen one.
            int originX = 0, originY = 0;
            WaylandDisplay.SurfaceScreenOriginQuery?.Invoke(surface, out originX, out originY);

            screenX = (int)Math.Round(localX) + originX;
            screenY = (int)Math.Round(localY) + originY;
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void OnTouchDown(IntPtr data, IntPtr touch, uint serial, uint time, IntPtr surface, int id, int fx, int fy)
        {
            try
            {
                LastInputSerial = serial;
                s_touchSurface = surface;

                IPlatformTouchSink? sink = PlatformTouch.Sink;
                if (sink is null) return;

                ToScreen(surface, fx, fy, out int screenX, out int screenY);
                sink.TouchDown(surface, id, screenX, screenY, PenState.None, time);
            }
            catch { }
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void OnTouchMotion(IntPtr data, IntPtr touch, uint time, int id, int fx, int fy)
        {
            try
            {
                // Motion names no surface: it belongs to whichever the contact went down on.
                if (s_touchSurface == IntPtr.Zero) return;

                IPlatformTouchSink? sink = PlatformTouch.Sink;
                if (sink is null) return;

                ToScreen(s_touchSurface, fx, fy, out int screenX, out int screenY);
                sink.TouchMove(s_touchSurface, id, screenX, screenY, PenState.None, time);
            }
            catch { }
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void OnTouchUp(IntPtr data, IntPtr touch, uint serial, uint time, int id)
        {
            try
            {
                LastInputSerial = serial;
                if (s_touchSurface == IntPtr.Zero) return;

                // An up names only the contact, so the seam is told there is no position and lifts
                // it where it was last seen.
                PlatformTouch.Sink?.TouchUp(s_touchSurface, id, PlatformTouch.NoPosition, PlatformTouch.NoPosition, time);
            }
            catch { }
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void OnTouchFrame(IntPtr data, IntPtr touch)
        {
            // Nothing to do: contacts are delivered as they arrive rather than batched per frame.
            // WPF raises Touch.FrameReported from TouchDevice itself, so batching here would only
            // delay delivery.
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void OnTouchCancel(IntPtr data, IntPtr touch)
        {
            try
            {
                // The compositor took the whole sequence -- it started a gesture of its own, or the
                // surface lost the touch. Every live contact is abandoned, not completed.
                PlatformTouch.CancelAll(s_touchSurface);
                s_touchSurface = IntPtr.Zero;
            }
            catch { }
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void OnTouchShape(IntPtr data, IntPtr touch, int id, int major, int minor)
        {
            // The contact ellipse. WPF's TouchPoint carries a rect, but nothing off Windows fills it
            // in yet; see PlatformTouchDevice.GetTouchPoint.
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void OnTouchOrientation(IntPtr data, IntPtr touch, int id, int orientation) { }

        // ---- Pointer -------------------------------------------------------------------------

        /// <summary>wl_fixed_t is signed 24.8, so the value is the raw integer over 256.</summary>
        internal static double Fixed(int value) => value / 256.0;

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void OnPointerEnter(IntPtr data, IntPtr pointer, uint serial, IntPtr surface, int sx, int sy)
        {
            try
            {
                EnterSerial = serial;
                LastInputSerial = serial;
                FocusSurface = surface;
                s_frameSurface = surface;
                s_haveMotion = true;
                s_motionX = Fixed(sx);
                s_motionY = Fixed(sy);
                PointerSurfaceX = s_motionX;
                PointerSurfaceY = s_motionY;
                // The cursor is undefined on enter until the client sets one; a window that never
                // calls set_cursor shows whatever the previous client left behind.
                WaylandCursor.OnPointerEnter(serial);
            }
            catch { }
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void OnPointerLeave(IntPtr data, IntPtr pointer, uint serial, IntPtr surface)
        {
            try
            {
                LastInputSerial = serial;
                Raise(new WaylandMouseMessage(surface, WaylandMouseKind.Leave, 0, 0, 0, 0, 0, (int)s_frameTime));
                if (FocusSurface == surface) FocusSurface = IntPtr.Zero;
                s_frameSurface = IntPtr.Zero;
                s_haveMotion = false;
            }
            catch { }
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void OnPointerMotion(IntPtr data, IntPtr pointer, uint time, int sx, int sy)
        {
            try
            {
                s_frameTime = time;
                s_frameSurface = FocusSurface;
                s_haveMotion = true;
                s_motionX = Fixed(sx);
                s_motionY = Fixed(sy);
                PointerSurfaceX = s_motionX;
                PointerSurfaceY = s_motionY;
            }
            catch { }
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void OnPointerButton(IntPtr data, IntPtr pointer, uint serial, uint time, uint button, uint state)
        {
            try
            {
                NoteButtonSerial(serial, state != 0);

                s_frameTime = time;
                IntPtr surface = FocusSurface;
                if (surface == IntPtr.Zero) return;

                // Buttons are reported immediately rather than deferred to `frame`: press and
                // release must keep their order relative to each other, and a frame carries at most
                // one of each anyway. Motion accumulated so far is flushed first so the button lands
                // at the right position.
                FlushMotion(surface);

                var kind = state != 0 ? WaylandMouseKind.ButtonDown : WaylandMouseKind.ButtonUp;
                Raise(new WaylandMouseMessage(surface, kind, (int)button, Px(s_motionX, surface), Py(s_motionY, surface), 0, 0, (int)time));
            }
            catch { }
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void OnPointerAxis(IntPtr data, IntPtr pointer, uint time, uint axis, int value)
        {
            try
            {
                s_frameTime = time;
                s_haveAxis = true;
                double v = Fixed(value);
                if (axis == WL_POINTER_AXIS_VERTICAL_SCROLL) s_axisV += v; else s_axisH += v;
            }
            catch { }
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void OnPointerAxisDiscrete(IntPtr data, IntPtr pointer, uint axis, int discrete)
        {
            try
            {
                // Whole notches (wl_pointer v5..v7). Superseded by axis_value120 at v8; if both
                // arrive, value120 wins because it also describes partial detents.
                if (s_haveValue120) return;
                s_haveValue120 = true;
                if (axis == WL_POINTER_AXIS_VERTICAL_SCROLL) s_value120V += discrete * 120;
                else s_value120H += discrete * 120;
            }
            catch { }
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void OnPointerAxisValue120(IntPtr data, IntPtr pointer, uint axis, int value120)
        {
            try
            {
                s_haveValue120 = true;
                if (axis == WL_POINTER_AXIS_VERTICAL_SCROLL) s_value120V += value120;
                else s_value120H += value120;
            }
            catch { }
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void OnPointerAxisSource(IntPtr data, IntPtr pointer, uint axisSource) { }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void OnPointerAxisStop(IntPtr data, IntPtr pointer, uint time, uint axis) { }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void OnPointerAxisRelativeDirection(IntPtr data, IntPtr pointer, uint axis, uint direction) { }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void OnPointerFrame(IntPtr data, IntPtr pointer)
        {
            try
            {
                IntPtr surface = s_frameSurface != IntPtr.Zero ? s_frameSurface : FocusSurface;
                if (surface == IntPtr.Zero) { ResetFrame(); return; }

                FlushMotion(surface);

                int wheel = 0, wheelH = 0;
                if (s_haveValue120)
                {
                    wheel = s_value120V;
                    wheelH = s_value120H;
                }
                else if (s_haveAxis)
                {
                    // A length in logical pixels, not detents. libinput's convention is ~10px per
                    // notch, which is what GTK and Qt assume too.
                    wheel = (int)Math.Round(s_axisV / 10.0 * 120.0);
                    wheelH = (int)Math.Round(s_axisH / 10.0 * 120.0);
                }

                if (wheel != 0 || wheelH != 0)
                {
                    // Wayland's positive vertical axis points DOWN; WPF's positive wheel delta means
                    // scrolling UP (away from the user), so the sign has to be inverted.
                    Raise(new WaylandMouseMessage(surface, WaylandMouseKind.Wheel, 0,
                        Px(s_motionX, surface), Py(s_motionY, surface), -wheel, wheelH, (int)s_frameTime));
                }

                ResetFrame();
            }
            catch { ResetFrame(); }
        }

        private static void FlushMotion(IntPtr surface)
        {
            if (!s_haveMotion) return;
            s_haveMotion = false;
            Raise(new WaylandMouseMessage(surface, WaylandMouseKind.Move, 0,
                Px(s_motionX, surface), Py(s_motionY, surface), 0, 0, (int)s_frameTime));
        }

        private static void ResetFrame()
        {
            s_haveWheel = false;
            s_haveValue120 = false;
            s_haveAxis = false;
            s_value120V = s_value120H = 0;
            s_axisV = s_axisH = 0;
            s_frameWheel = s_frameWheelH = 0;
        }

        /// <summary>Surface-local logical coordinate to client DEVICE pixels for that window.</summary>
        internal static int Px(double x, IntPtr surface) => (int)Math.Round(x * WaylandDisplay.ScaleForSurface(surface));
        internal static int Py(double y, IntPtr surface) => (int)Math.Round(y * WaylandDisplay.ScaleForSurface(surface));

        private static void Raise(in WaylandMouseMessage m) => MouseInput?.Invoke(m);

        // ---- Shared with the other input sources on this seat --------------------------------
        //
        // The tablet is a second pointing device on the same seat (WaylandTablet). It has to reach
        // the same three pieces of per-seat state the pointer owns -- the mouse channel, the last
        // known cursor position, and the serials -- because from WPF's point of view there is one
        // mouse and the pen is driving it.

        /// <summary>Raises a mouse message on behalf of another device on this seat.</summary>
        internal static void RaiseSynthesizedMouse(in WaylandMouseMessage m) => Raise(m);

        /// <summary>
        /// Records where the cursor now is. Wayland never reports a global cursor position, so this
        /// plus the window's origin is the only answer GetCursorPos has -- and a context menu opened
        /// with the pen would otherwise appear at wherever the mouse was last left.
        /// </summary>
        internal static void NotePointerPosition(IntPtr surface, double localX, double localY)
        {
            FocusSurface = surface;
            PointerSurfaceX = localX;
            PointerSurfaceY = localY;
        }

        /// <summary>Clears the focus surface if it is still the one named.</summary>
        internal static void NotePointerLeft(IntPtr surface)
        {
            if (FocusSurface == surface) FocusSurface = IntPtr.Zero;
        }

        /// <summary>
        /// Records the serial of a press or release that gives this client an implicit grab.
        /// </summary>
        /// <remarks>
        /// A drag needs the serial of a BUTTON PRESS specifically -- the compositor validates that
        /// the client holds an implicit grab, and rejects (silently, on mutter) a serial that came
        /// from anything else. Tracked separately from LastInputSerial, which any event moves along,
        /// and cleared on release so a drag cannot start after the button is already up. Pass 0 for
        /// <paramref name="serial"/> where the event carried none: zwp_tablet_tool_v2.up has no
        /// arguments at all, but the release still has to balance the press.
        /// </remarks>
        internal static void NoteButtonSerial(uint serial, bool pressed)
        {
            if (serial != 0) LastInputSerial = serial;

            if (pressed)
            {
                if (serial != 0) LastPointerButtonSerial = serial;
                s_buttonsDown++;
            }
            else if (s_buttonsDown > 0 && --s_buttonsDown == 0)
            {
                LastPointerButtonSerial = 0;
            }
        }

        /// <summary>Records a serial from an event that is not a button press.</summary>
        internal static void NoteInputSerial(uint serial)
        {
            if (serial != 0) LastInputSerial = serial;
        }

        // ---- Keyboard ------------------------------------------------------------------------

        [DllImport("libc", SetLastError = true)] private static extern void* mmap(void* addr, nuint length, int prot, int flags, int fd, long offset);
        [DllImport("libc", SetLastError = true)] private static extern int munmap(void* addr, nuint length);
        [DllImport("libc", SetLastError = true)] private static extern int close(int fd);

        private const int PROT_READ = 1;
        private const int MAP_PRIVATE = 2;

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void OnKeymap(IntPtr data, IntPtr keyboard, uint format, int fd, uint size)
        {
            try
            {
                if (s_xkbContext == IntPtr.Zero) { close(fd); return; }

                // MAP_PRIVATE, not MAP_SHARED: since Wayland 1.7 the compositor may hand the same
                // keymap fd to several clients, and mapping it shared is a protocol violation.
                void* map = mmap(null, size, PROT_READ, MAP_PRIVATE, fd, 0);
                if (map == (void*)-1 || map == null) { close(fd); return; }

                try
                {
                    if (format == 1 /* XKB_V1 */)
                    {
                        IntPtr keymap = WlXkb.xkb_keymap_new_from_string(s_xkbContext, (byte*)map,
                            WlXkb.XKB_KEYMAP_FORMAT_TEXT_V1, WlXkb.XKB_KEYMAP_COMPILE_NO_FLAGS);
                        if (keymap != IntPtr.Zero)
                        {
                            if (s_xkbState != IntPtr.Zero) WlXkb.xkb_state_unref(s_xkbState);
                            if (s_xkbKeymap != IntPtr.Zero) WlXkb.xkb_keymap_unref(s_xkbKeymap);
                            s_xkbKeymap = keymap;
                            s_xkbState = WlXkb.xkb_state_new(keymap);
                        }
                    }
                }
                finally
                {
                    munmap(map, size);
                    close(fd);
                }
            }
            catch { try { close(fd); } catch { } }
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void OnKeyboardEnter(IntPtr data, IntPtr keyboard, uint serial, IntPtr surface, IntPtr keys)
        {
            try { LastInputSerial = serial; KeyboardFocusSurface = surface; }
            catch { }
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void OnKeyboardLeave(IntPtr data, IntPtr keyboard, uint serial, IntPtr surface)
        {
            try
            {
                LastInputSerial = serial;
                if (KeyboardFocusSurface == surface) KeyboardFocusSurface = IntPtr.Zero;
                StopRepeat();   // otherwise a key held while focus moves repeats forever
            }
            catch { }
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void OnKey(IntPtr data, IntPtr keyboard, uint serial, uint time, uint scancode, uint state)
        {
            try
            {
                LastInputSerial = serial;
                bool down = state != 0;
                uint xkbCode = scancode + WlXkb.EvdevOffset;
                uint keysym = s_xkbState != IntPtr.Zero ? WlXkb.xkb_state_key_get_one_sym(s_xkbState, xkbCode) : 0;
                string? text = down ? ComposeText(xkbCode, ref keysym) : null;

                KeyInput?.Invoke(new WaylandKeyMessage(KeyboardFocusSurface, down, keysym, scancode, text, false, s_modifiers, (int)time));

                if (down && s_xkbKeymap != IntPtr.Zero && WlXkb.xkb_keymap_key_repeats(s_xkbKeymap, xkbCode) && s_repeatRate > 0)
                {
                    s_repeatScancode = scancode;
                    s_repeatKeysym = keysym;
                    s_repeatText = text;
                    s_repeatNextTicks = Environment.TickCount64 + s_repeatDelayMs;
                }
                else if (!down && scancode == s_repeatScancode)
                {
                    StopRepeat();
                }
            }
            catch { }
        }

        /// <summary>
        /// The text a key press produces, run through the compose state so dead keys behave. Returns
        /// null while a sequence is still composing (the "´" of "´e" must type nothing at all).
        /// </summary>
        private static string? ComposeText(uint xkbCode, ref uint keysym)
        {
            if (s_xkbState == IntPtr.Zero) return null;

            if (s_composeState != IntPtr.Zero && keysym != 0)
            {
                if (WlXkb.xkb_compose_state_feed(s_composeState, keysym) == XkbComposeFeedResult.Accepted)
                {
                    switch (WlXkb.xkb_compose_state_get_status(s_composeState))
                    {
                        case XkbComposeStatus.Composing:
                            return null;
                        case XkbComposeStatus.Cancelled:
                            WlXkb.xkb_compose_state_reset(s_composeState);
                            return null;
                        case XkbComposeStatus.Composed:
                            {
                                byte* buf = stackalloc byte[64];
                                int n = WlXkb.xkb_compose_state_get_utf8(s_composeState, buf, 64);
                                keysym = WlXkb.xkb_compose_state_get_one_sym(s_composeState);
                                WlXkb.xkb_compose_state_reset(s_composeState);
                                return n > 0 ? Encoding.UTF8.GetString(buf, Math.Min(n, 63)) : null;
                            }
                    }
                }
            }

            byte* plain = stackalloc byte[64];
            int len = WlXkb.xkb_state_key_get_utf8(s_xkbState, xkbCode, plain, 64);
            if (len <= 0) return null;
            string s = Encoding.UTF8.GetString(plain, Math.Min(len, 63));
            // Control characters are not text: Ctrl+C yields U+0003 here, and WPF expects a key
            // event with no accompanying text input for it.
            if (s.Length == 1 && s[0] < ' ' && s[0] != '\t' && s[0] != '\r' && s[0] != '\n') return null;
            return s;
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void OnModifiers(IntPtr data, IntPtr keyboard, uint serial, uint depressed, uint latched, uint locked, uint group)
        {
            try
            {
                LastInputSerial = serial;
                if (s_xkbState == IntPtr.Zero) return;
                WlXkb.xkb_state_update_mask(s_xkbState, depressed, latched, locked, 0, 0, group);

                WaylandModifiers m = WaylandModifiers.None;
                const XkbStateComponent eff = XkbStateComponent.ModsEffective;
                if (WlXkb.xkb_state_mod_name_is_active(s_xkbState, WlXkb.ModShift, eff)) m |= WaylandModifiers.Shift;
                if (WlXkb.xkb_state_mod_name_is_active(s_xkbState, WlXkb.ModControl, eff)) m |= WaylandModifiers.Control;
                if (WlXkb.xkb_state_mod_name_is_active(s_xkbState, WlXkb.ModAlt, eff)) m |= WaylandModifiers.Alt;
                if (WlXkb.xkb_state_mod_name_is_active(s_xkbState, WlXkb.ModSuper, eff)) m |= WaylandModifiers.Super;
                if (WlXkb.xkb_state_mod_name_is_active(s_xkbState, WlXkb.ModCapsLock, eff)) m |= WaylandModifiers.CapsLock;
                if (WlXkb.xkb_state_mod_name_is_active(s_xkbState, WlXkb.ModNumLock, eff)) m |= WaylandModifiers.NumLock;
                s_modifiers = m;
            }
            catch { }
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void OnRepeatInfo(IntPtr data, IntPtr keyboard, int rate, int delay)
        {
            try
            {
                s_repeatRate = rate;          // rate == 0 means "no repeat at all"
                s_repeatDelayMs = delay;
                if (rate == 0) StopRepeat();
            }
            catch { }
        }

        private static void StopRepeat()
        {
            s_repeatScancode = 0;
            s_repeatKeysym = 0;
            s_repeatText = null;
            s_repeatNextTicks = 0;
        }

        /// <summary>
        /// Emit any synthetic key repeats that have come due. Called from the pump, because Wayland
        /// makes repeat the client's job (see note 3 in the file header).
        /// </summary>
        internal static void PumpKeyRepeat()
        {
            if (s_repeatNextTicks == 0 || s_repeatRate <= 0) return;
            long now = Environment.TickCount64;
            if (now < s_repeatNextTicks) return;

            int intervalMs = Math.Max(1, 1000 / s_repeatRate);
            // Catch up without flooding: if the loop was blocked for a second, emit one repeat and
            // realign, rather than a burst of forty.
            s_repeatNextTicks = now + intervalMs;

            KeyInput?.Invoke(new WaylandKeyMessage(KeyboardFocusSurface, true, s_repeatKeysym, s_repeatScancode,
                                                   s_repeatText, true, s_modifiers, (int)now));
        }

        /// <summary>
        /// Milliseconds until the next synthetic key repeat, or -1 when none is pending. The run
        /// loop folds this into its poll timeout: without it a held key stops repeating as soon as
        /// nothing else is waking the loop.
        /// </summary>
        internal static int NextDeadlineMs()
        {
            if (s_repeatNextTicks == 0 || s_repeatRate <= 0) return -1;
            long remaining = s_repeatNextTicks - Environment.TickCount64;
            return remaining <= 0 ? 0 : (int)Math.Min(remaining, int.MaxValue);
        }
    }
}
