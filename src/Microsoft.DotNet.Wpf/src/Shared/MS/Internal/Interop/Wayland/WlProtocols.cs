// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The Wayland protocols libwayland-client does NOT export descriptors for, transcribed from their
// XML into the declarative form Wl.BuildAllLocked turns into native wl_interface tables.
//
// A C client gets these from wayland-scanner at build time. We author them because the target
// machine need not have wayland-protocols installed (this one does not), and because generating C
// at build time would mean a native toolchain in a managed-only build.
//
// TRANSCRIPTION RULES -- every one of these has a failure mode that is silent here and fatal later,
// because nothing in the compiler checks any of it:
//
//   * REQUEST AND EVENT ORDER IS THE WIRE OPCODE. Never reorder, never omit a member in the middle;
//     retire one by leaving it in place. Opcode 3 is "the fourth request", nothing else.
//   * The signature's leading digits are the "since" version, not an argument ("3ou" = since 3,
//     two arguments). '?' marks the FOLLOWING argument nullable and is likewise not an argument.
//   * types[] has ONE ENTRY PER ARGUMENT, null except for object ('o') and new_id ('n') slots.
//     Wl.BuildMessages throws at startup if the count disagrees with the signature.
//   * A constructor request ('n') still needs an argument slot at the call site, value ignored.
//
// Versions here are the maximum this code understands; the version actually bound is min(this,
// what the compositor advertises) -- see WaylandWindow's registry handler.
//
// Sources: xdg-shell.xml, viewporter.xml, fractional-scale-v1.xml, cursor-shape-v1.xml,
// tablet-v2.xml (wayland-protocols, MIT).
//

using System;

namespace MS.Internal.Interop.Wayland
{
    internal static class WlProtocols
    {
        // ---- xdg_positioner constants ----------------------------------------------------
        internal const uint XDG_POSITIONER_ANCHOR_NONE = 0;
        internal const uint XDG_POSITIONER_ANCHOR_TOP = 1;
        internal const uint XDG_POSITIONER_ANCHOR_BOTTOM = 2;
        internal const uint XDG_POSITIONER_ANCHOR_LEFT = 3;
        internal const uint XDG_POSITIONER_ANCHOR_RIGHT = 4;
        internal const uint XDG_POSITIONER_ANCHOR_TOP_LEFT = 5;
        internal const uint XDG_POSITIONER_ANCHOR_BOTTOM_LEFT = 6;
        internal const uint XDG_POSITIONER_ANCHOR_TOP_RIGHT = 7;
        internal const uint XDG_POSITIONER_ANCHOR_BOTTOM_RIGHT = 8;

        internal const uint XDG_POSITIONER_GRAVITY_NONE = 0;
        internal const uint XDG_POSITIONER_GRAVITY_TOP = 1;
        internal const uint XDG_POSITIONER_GRAVITY_BOTTOM = 2;
        internal const uint XDG_POSITIONER_GRAVITY_LEFT = 3;
        internal const uint XDG_POSITIONER_GRAVITY_RIGHT = 4;
        internal const uint XDG_POSITIONER_GRAVITY_TOP_LEFT = 5;
        internal const uint XDG_POSITIONER_GRAVITY_BOTTOM_LEFT = 6;
        internal const uint XDG_POSITIONER_GRAVITY_TOP_RIGHT = 7;
        internal const uint XDG_POSITIONER_GRAVITY_BOTTOM_RIGHT = 8;

        internal const uint XDG_POSITIONER_CONSTRAINT_ADJUSTMENT_NONE = 0;
        internal const uint XDG_POSITIONER_CONSTRAINT_ADJUSTMENT_SLIDE_X = 1;
        internal const uint XDG_POSITIONER_CONSTRAINT_ADJUSTMENT_SLIDE_Y = 2;
        internal const uint XDG_POSITIONER_CONSTRAINT_ADJUSTMENT_FLIP_X = 4;
        internal const uint XDG_POSITIONER_CONSTRAINT_ADJUSTMENT_FLIP_Y = 8;
        internal const uint XDG_POSITIONER_CONSTRAINT_ADJUSTMENT_RESIZE_X = 16;
        internal const uint XDG_POSITIONER_CONSTRAINT_ADJUSTMENT_RESIZE_Y = 32;

        // ---- wp_cursor_shape_device_v1 shapes (v1) ----------------------------------------
        internal const uint CURSOR_SHAPE_DEFAULT = 1;
        internal const uint CURSOR_SHAPE_CONTEXT_MENU = 2;
        internal const uint CURSOR_SHAPE_HELP = 3;
        internal const uint CURSOR_SHAPE_POINTER = 4;
        internal const uint CURSOR_SHAPE_PROGRESS = 5;
        internal const uint CURSOR_SHAPE_WAIT = 6;
        internal const uint CURSOR_SHAPE_CELL = 7;
        internal const uint CURSOR_SHAPE_CROSSHAIR = 8;
        internal const uint CURSOR_SHAPE_TEXT = 9;
        internal const uint CURSOR_SHAPE_VERTICAL_TEXT = 10;
        internal const uint CURSOR_SHAPE_ALIAS = 11;
        internal const uint CURSOR_SHAPE_COPY = 12;
        internal const uint CURSOR_SHAPE_MOVE = 13;
        internal const uint CURSOR_SHAPE_NO_DROP = 14;
        internal const uint CURSOR_SHAPE_NOT_ALLOWED = 15;
        internal const uint CURSOR_SHAPE_GRAB = 16;
        internal const uint CURSOR_SHAPE_GRABBING = 17;
        internal const uint CURSOR_SHAPE_E_RESIZE = 18;
        internal const uint CURSOR_SHAPE_N_RESIZE = 19;
        internal const uint CURSOR_SHAPE_NE_RESIZE = 20;
        internal const uint CURSOR_SHAPE_NW_RESIZE = 21;
        internal const uint CURSOR_SHAPE_S_RESIZE = 22;
        internal const uint CURSOR_SHAPE_SE_RESIZE = 23;
        internal const uint CURSOR_SHAPE_SW_RESIZE = 24;
        internal const uint CURSOR_SHAPE_W_RESIZE = 25;
        internal const uint CURSOR_SHAPE_EW_RESIZE = 26;
        internal const uint CURSOR_SHAPE_NS_RESIZE = 27;
        internal const uint CURSOR_SHAPE_NESW_RESIZE = 28;
        internal const uint CURSOR_SHAPE_NWSE_RESIZE = 29;
        internal const uint CURSOR_SHAPE_COL_RESIZE = 30;
        internal const uint CURSOR_SHAPE_ROW_RESIZE = 31;
        internal const uint CURSOR_SHAPE_ALL_SCROLL = 32;
        internal const uint CURSOR_SHAPE_ZOOM_IN = 33;
        internal const uint CURSOR_SHAPE_ZOOM_OUT = 34;

        // ---- Request opcodes --------------------------------------------------------------
        // Named so call sites read as protocol rather than magic numbers; they are just the index
        // of the request in the corresponding table below.

        internal const uint XDG_WM_BASE_DESTROY = 0;
        internal const uint XDG_WM_BASE_CREATE_POSITIONER = 1;
        internal const uint XDG_WM_BASE_GET_XDG_SURFACE = 2;
        internal const uint XDG_WM_BASE_PONG = 3;

        internal const uint XDG_POSITIONER_DESTROY = 0;
        internal const uint XDG_POSITIONER_SET_SIZE = 1;
        internal const uint XDG_POSITIONER_SET_ANCHOR_RECT = 2;
        internal const uint XDG_POSITIONER_SET_ANCHOR = 3;
        internal const uint XDG_POSITIONER_SET_GRAVITY = 4;
        internal const uint XDG_POSITIONER_SET_CONSTRAINT_ADJUSTMENT = 5;
        internal const uint XDG_POSITIONER_SET_OFFSET = 6;
        internal const uint XDG_POSITIONER_SET_REACTIVE = 7;
        internal const uint XDG_POSITIONER_SET_PARENT_SIZE = 8;
        internal const uint XDG_POSITIONER_SET_PARENT_CONFIGURE = 9;

        internal const uint XDG_SURFACE_DESTROY = 0;
        internal const uint XDG_SURFACE_GET_TOPLEVEL = 1;
        internal const uint XDG_SURFACE_GET_POPUP = 2;
        internal const uint XDG_SURFACE_SET_WINDOW_GEOMETRY = 3;
        internal const uint XDG_SURFACE_ACK_CONFIGURE = 4;

        internal const uint XDG_TOPLEVEL_SET_TITLE = 2;
        internal const uint XDG_TOPLEVEL_SET_APP_ID = 3;
        internal const uint XDG_TOPLEVEL_SET_MAX_SIZE = 7;
        internal const uint XDG_TOPLEVEL_SET_MIN_SIZE = 8;
        internal const uint XDG_TOPLEVEL_SET_MAXIMIZED = 9;
        internal const uint XDG_TOPLEVEL_UNSET_MAXIMIZED = 10;
        internal const uint XDG_TOPLEVEL_SET_FULLSCREEN = 11;
        internal const uint XDG_TOPLEVEL_UNSET_FULLSCREEN = 12;
        internal const uint XDG_TOPLEVEL_SET_MINIMIZED = 13;

        internal const uint XDG_POPUP_DESTROY = 0;
        internal const uint XDG_POPUP_GRAB = 1;
        internal const uint XDG_POPUP_REPOSITION = 2;

        internal const uint WP_VIEWPORTER_DESTROY = 0;
        internal const uint WP_VIEWPORTER_GET_VIEWPORT = 1;

        internal const uint WP_VIEWPORT_DESTROY = 0;
        internal const uint WP_VIEWPORT_SET_SOURCE = 1;
        internal const uint WP_VIEWPORT_SET_DESTINATION = 2;

        internal const uint WP_FRACTIONAL_SCALE_MANAGER_DESTROY = 0;
        internal const uint WP_FRACTIONAL_SCALE_MANAGER_GET_FRACTIONAL_SCALE = 1;

        internal const uint WP_FRACTIONAL_SCALE_DESTROY = 0;

        internal const uint WP_CURSOR_SHAPE_MANAGER_DESTROY = 0;
        internal const uint WP_CURSOR_SHAPE_MANAGER_GET_POINTER = 1;

        internal const uint WP_CURSOR_SHAPE_DEVICE_DESTROY = 0;
        internal const uint WP_CURSOR_SHAPE_DEVICE_SET_SHAPE = 1;

        internal const uint ZWP_TEXT_INPUT_MANAGER_V3_DESTROY = 0;
        internal const uint ZWP_TEXT_INPUT_MANAGER_V3_GET_TEXT_INPUT = 1;

        internal const uint ZWP_TEXT_INPUT_V3_DESTROY = 0;
        internal const uint ZWP_TEXT_INPUT_V3_ENABLE = 1;
        internal const uint ZWP_TEXT_INPUT_V3_DISABLE = 2;
        internal const uint ZWP_TEXT_INPUT_V3_SET_SURROUNDING_TEXT = 3;
        internal const uint ZWP_TEXT_INPUT_V3_SET_TEXT_CHANGE_CAUSE = 4;
        internal const uint ZWP_TEXT_INPUT_V3_SET_CONTENT_TYPE = 5;
        internal const uint ZWP_TEXT_INPUT_V3_SET_CURSOR_RECTANGLE = 6;
        internal const uint ZWP_TEXT_INPUT_V3_COMMIT = 7;

        // zwp_text_input_v3.change_cause
        internal const uint ZWP_TEXT_INPUT_V3_CHANGE_CAUSE_INPUT_METHOD = 0;
        internal const uint ZWP_TEXT_INPUT_V3_CHANGE_CAUSE_OTHER = 1;

        // zwp_text_input_v3.content_hint (bitfield)
        internal const uint ZWP_TEXT_INPUT_V3_CONTENT_HINT_NONE = 0;
        internal const uint ZWP_TEXT_INPUT_V3_CONTENT_HINT_COMPLETION = 1;
        internal const uint ZWP_TEXT_INPUT_V3_CONTENT_HINT_SPELLCHECK = 2;
        internal const uint ZWP_TEXT_INPUT_V3_CONTENT_HINT_AUTO_CAPITALIZATION = 4;
        internal const uint ZWP_TEXT_INPUT_V3_CONTENT_HINT_LOWERCASE = 8;
        internal const uint ZWP_TEXT_INPUT_V3_CONTENT_HINT_UPPERCASE = 16;
        internal const uint ZWP_TEXT_INPUT_V3_CONTENT_HINT_TITLECASE = 32;
        internal const uint ZWP_TEXT_INPUT_V3_CONTENT_HINT_HIDDEN_TEXT = 64;
        internal const uint ZWP_TEXT_INPUT_V3_CONTENT_HINT_SENSITIVE_DATA = 128;
        internal const uint ZWP_TEXT_INPUT_V3_CONTENT_HINT_LATIN = 256;
        internal const uint ZWP_TEXT_INPUT_V3_CONTENT_HINT_MULTILINE = 512;

        // zwp_text_input_v3.content_purpose
        internal const uint ZWP_TEXT_INPUT_V3_CONTENT_PURPOSE_NORMAL = 0;
        internal const uint ZWP_TEXT_INPUT_V3_CONTENT_PURPOSE_ALPHA = 1;
        internal const uint ZWP_TEXT_INPUT_V3_CONTENT_PURPOSE_DIGITS = 2;
        internal const uint ZWP_TEXT_INPUT_V3_CONTENT_PURPOSE_NUMBER = 3;
        internal const uint ZWP_TEXT_INPUT_V3_CONTENT_PURPOSE_PHONE = 4;
        internal const uint ZWP_TEXT_INPUT_V3_CONTENT_PURPOSE_URL = 5;
        internal const uint ZWP_TEXT_INPUT_V3_CONTENT_PURPOSE_EMAIL = 6;
        internal const uint ZWP_TEXT_INPUT_V3_CONTENT_PURPOSE_NAME = 7;
        internal const uint ZWP_TEXT_INPUT_V3_CONTENT_PURPOSE_PASSWORD = 8;
        internal const uint ZWP_TEXT_INPUT_V3_CONTENT_PURPOSE_PIN = 9;
        internal const uint ZWP_TEXT_INPUT_V3_CONTENT_PURPOSE_DATE = 10;
        internal const uint ZWP_TEXT_INPUT_V3_CONTENT_PURPOSE_TIME = 11;
        internal const uint ZWP_TEXT_INPUT_V3_CONTENT_PURPOSE_DATETIME = 12;
        internal const uint ZWP_TEXT_INPUT_V3_CONTENT_PURPOSE_TERMINAL = 13;

        /// <summary>The largest surrounding text zwp_text_input_v3.set_surrounding_text accepts.</summary>
        internal const int ZWP_TEXT_INPUT_V3_MAX_SURROUNDING_BYTES = 4000;

        // tablet-v2. Note the manager's opcodes: get_tablet_seat is 0 and destroy is 1, the reverse
        // of every other protocol here. Transcribed, not assumed.
        internal const uint ZWP_TABLET_MANAGER_V2_GET_TABLET_SEAT = 0;
        internal const uint ZWP_TABLET_MANAGER_V2_DESTROY = 1;

        internal const uint ZWP_TABLET_SEAT_V2_DESTROY = 0;

        internal const uint ZWP_TABLET_TOOL_V2_SET_CURSOR = 0;
        internal const uint ZWP_TABLET_TOOL_V2_DESTROY = 1;

        internal const uint ZWP_TABLET_V2_DESTROY = 0;
        internal const uint ZWP_TABLET_PAD_V2_DESTROY = 1;

        // zwp_tablet_tool_v2.type. These are the evdev BTN_TOOL_* codes, which is why they start at
        // 0x140 rather than 0.
        internal const uint ZWP_TABLET_TOOL_V2_TYPE_PEN = 0x140;
        internal const uint ZWP_TABLET_TOOL_V2_TYPE_ERASER = 0x141;
        internal const uint ZWP_TABLET_TOOL_V2_TYPE_BRUSH = 0x142;
        internal const uint ZWP_TABLET_TOOL_V2_TYPE_PENCIL = 0x143;
        internal const uint ZWP_TABLET_TOOL_V2_TYPE_AIRBRUSH = 0x144;
        internal const uint ZWP_TABLET_TOOL_V2_TYPE_FINGER = 0x145;
        internal const uint ZWP_TABLET_TOOL_V2_TYPE_MOUSE = 0x146;
        internal const uint ZWP_TABLET_TOOL_V2_TYPE_LENS = 0x147;

        /// <summary>Pressure and distance are normalised to this, not to 1.</summary>
        internal const double ZWP_TABLET_TOOL_V2_AXIS_MAX = 65535.0;

        // Core-protocol opcodes we use (these interfaces come from libwayland, but the opcodes are
        // still ours to get right). Verified against the installed libwayland's own tables.
        internal const uint WL_REGISTRY_BIND = 0;
        internal const uint WL_COMPOSITOR_CREATE_SURFACE = 0;
        internal const uint WL_COMPOSITOR_CREATE_REGION = 1;
        internal const uint WL_SURFACE_DESTROY = 0;
        internal const uint WL_SURFACE_ATTACH = 1;
        internal const uint WL_SURFACE_DAMAGE = 2;
        internal const uint WL_SURFACE_FRAME = 3;
        internal const uint WL_SURFACE_SET_OPAQUE_REGION = 4;
        internal const uint WL_SURFACE_SET_INPUT_REGION = 5;
        internal const uint WL_SURFACE_COMMIT = 6;
        internal const uint WL_SURFACE_SET_BUFFER_SCALE = 8;
        internal const uint WL_REGION_DESTROY = 0;
        internal const uint WL_REGION_ADD = 1;
        internal const uint WL_SEAT_GET_POINTER = 0;
        internal const uint WL_SEAT_GET_KEYBOARD = 1;
        internal const uint WL_SEAT_GET_TOUCH = 2;
        internal const uint WL_POINTER_SET_CURSOR = 0;
        internal const uint WL_DATA_DEVICE_MANAGER_CREATE_DATA_SOURCE = 0;
        internal const uint WL_DATA_DEVICE_MANAGER_GET_DATA_DEVICE = 1;
        internal const uint WL_DATA_DEVICE_START_DRAG = 0;
        internal const uint WL_DATA_DEVICE_SET_SELECTION = 1;
        internal const uint WL_DATA_SOURCE_OFFER = 0;
        internal const uint WL_DATA_SOURCE_DESTROY = 1;
        internal const uint WL_DATA_DEVICE_RELEASE = 2;
        internal const uint WL_DATA_SOURCE_SET_ACTIONS = 2;
        internal const uint WL_DATA_OFFER_ACCEPT = 0;
        internal const uint WL_DATA_OFFER_RECEIVE = 1;
        internal const uint WL_DATA_OFFER_DESTROY = 2;
        internal const uint WL_DATA_OFFER_FINISH = 3;
        internal const uint WL_DATA_OFFER_SET_ACTIONS = 4;

        // wl_data_device_manager.dnd_action bitfield
        internal const uint WL_DND_ACTION_NONE = 0;
        internal const uint WL_DND_ACTION_COPY = 1;
        internal const uint WL_DND_ACTION_MOVE = 2;
        internal const uint WL_DND_ACTION_ASK = 4;

        // wl_seat capability bits
        internal const uint WL_SEAT_CAPABILITY_POINTER = 1;
        internal const uint WL_SEAT_CAPABILITY_KEYBOARD = 2;
        internal const uint WL_SEAT_CAPABILITY_TOUCH = 4;

        // ---- The tables --------------------------------------------------------------------

        internal static readonly WlInterfaceDef[] All = new[]
        {
            new WlInterfaceDef("xdg_wm_base", 6,
                requests: new[]
                {
                    new WlMsgDef("destroy", ""),
                    new WlMsgDef("create_positioner", "n", "xdg_positioner"),
                    new WlMsgDef("get_xdg_surface", "no", "xdg_surface", "wl_surface"),
                    new WlMsgDef("pong", "u", (string?)null),
                },
                events: new[]
                {
                    new WlMsgDef("ping", "u", (string?)null),
                }),

            new WlInterfaceDef("xdg_positioner", 6,
                requests: new[]
                {
                    new WlMsgDef("destroy", ""),
                    new WlMsgDef("set_size", "ii", null, null),
                    new WlMsgDef("set_anchor_rect", "iiii", null, null, null, null),
                    new WlMsgDef("set_anchor", "u", (string?)null),
                    new WlMsgDef("set_gravity", "u", (string?)null),
                    new WlMsgDef("set_constraint_adjustment", "u", (string?)null),
                    new WlMsgDef("set_offset", "ii", null, null),
                    new WlMsgDef("set_reactive", "3"),
                    new WlMsgDef("set_parent_size", "3ii", null, null),
                    new WlMsgDef("set_parent_configure", "3u", (string?)null),
                },
                events: Array.Empty<WlMsgDef>()),

            new WlInterfaceDef("xdg_surface", 6,
                requests: new[]
                {
                    new WlMsgDef("destroy", ""),
                    // We never CONSTRUCT a toplevel -- libdecor owns those and makes its own
                    // xdg_surface/xdg_toplevel pair. The table is still exact, because requests are
                    // sent on libdecor's xdg_toplevel proxy (libdecor_frame_get_xdg_toplevel) for
                    // the window states libdecor does not wrap.
                    new WlMsgDef("get_toplevel", "n", "xdg_toplevel"),
                    new WlMsgDef("get_popup", "n?oo", "xdg_popup", "xdg_surface", "xdg_positioner"),
                    new WlMsgDef("set_window_geometry", "iiii", null, null, null, null),
                    new WlMsgDef("ack_configure", "u", (string?)null),
                },
                events: new[]
                {
                    new WlMsgDef("configure", "u", (string?)null),
                }),

            // Generated from xdg-shell.xml and checked by the table validator. Only the state
            // requests (maximize/minimize/fullscreen) are actually sent, on libdecor's proxy.
            new WlInterfaceDef("xdg_toplevel", 7,
                requests: new[]
                {
                    new WlMsgDef("destroy", ""),
                    new WlMsgDef("set_parent", "?o", "xdg_toplevel"),
                    new WlMsgDef("set_title", "s", (string?)null),
                    new WlMsgDef("set_app_id", "s", (string?)null),
                    new WlMsgDef("show_window_menu", "ouii", "wl_seat", null, null, null),
                    new WlMsgDef("move", "ou", "wl_seat", null),
                    new WlMsgDef("resize", "ouu", "wl_seat", null, null),
                    new WlMsgDef("set_max_size", "ii", null, null),
                    new WlMsgDef("set_min_size", "ii", null, null),
                    new WlMsgDef("set_maximized", ""),
                    new WlMsgDef("unset_maximized", ""),
                    new WlMsgDef("set_fullscreen", "?o", "wl_output"),
                    new WlMsgDef("unset_fullscreen", ""),
                    new WlMsgDef("set_minimized", ""),
                },
                events: new[]
                {
                    new WlMsgDef("configure", "iia", null, null, null),
                    new WlMsgDef("close", ""),
                    new WlMsgDef("configure_bounds", "4ii", null, null),
                    new WlMsgDef("wm_capabilities", "5a", (string?)null),
                }),

            new WlInterfaceDef("xdg_popup", 6,
                requests: new[]
                {
                    new WlMsgDef("destroy", ""),
                    new WlMsgDef("grab", "ou", "wl_seat", null),
                    new WlMsgDef("reposition", "3ou", "xdg_positioner", null),
                },
                events: new[]
                {
                    new WlMsgDef("configure", "iiii", null, null, null, null),
                    new WlMsgDef("popup_done", ""),
                    new WlMsgDef("repositioned", "3u", (string?)null),
                }),

            new WlInterfaceDef("wp_viewporter", 1,
                requests: new[]
                {
                    new WlMsgDef("destroy", ""),
                    new WlMsgDef("get_viewport", "no", "wp_viewport", "wl_surface"),
                },
                events: Array.Empty<WlMsgDef>()),

            new WlInterfaceDef("wp_viewport", 1,
                requests: new[]
                {
                    new WlMsgDef("destroy", ""),
                    new WlMsgDef("set_source", "ffff", null, null, null, null),
                    new WlMsgDef("set_destination", "ii", null, null),
                },
                events: Array.Empty<WlMsgDef>()),

            new WlInterfaceDef("wp_fractional_scale_manager_v1", 1,
                requests: new[]
                {
                    new WlMsgDef("destroy", ""),
                    new WlMsgDef("get_fractional_scale", "no", "wp_fractional_scale_v1", "wl_surface"),
                },
                events: Array.Empty<WlMsgDef>()),

            new WlInterfaceDef("wp_fractional_scale_v1", 1,
                requests: new[]
                {
                    new WlMsgDef("destroy", ""),
                },
                events: new[]
                {
                    // The scale as a multiple of 1/120, so 1.25x arrives as 150.
                    new WlMsgDef("preferred_scale", "u", (string?)null),
                }),

            new WlInterfaceDef("wp_cursor_shape_manager_v1", 1,
                requests: new[]
                {
                    new WlMsgDef("destroy", ""),
                    new WlMsgDef("get_pointer", "no", "wp_cursor_shape_device_v1", "wl_pointer"),
                    // Never called -- WaylandCursor sets the cursor through wl_pointer -- but the
                    // interface it names now has a table of its own, so this resolves for real.
                    new WlMsgDef("get_tablet_tool_v2", "no", "wp_cursor_shape_device_v1", "zwp_tablet_tool_v2"),
                },
                events: Array.Empty<WlMsgDef>()),

            new WlInterfaceDef("wp_cursor_shape_device_v1", 1,
                requests: new[]
                {
                    new WlMsgDef("destroy", ""),
                    new WlMsgDef("set_shape", "uu", null, null),
                },
                events: Array.Empty<WlMsgDef>()),

            // text-input-unstable-v3: how a Wayland client talks to an input method (fcitx5, ibus,
            // ...). Without it a CJK IME has no channel to this process at all -- the compositor
            // routes the keystrokes to the input method and the client simply never hears the result.
            new WlInterfaceDef("zwp_text_input_manager_v3", 1,
                requests: new[]
                {
                    new WlMsgDef("destroy", ""),
                    new WlMsgDef("get_text_input", "no", "zwp_text_input_v3", "wl_seat"),
                },
                events: Array.Empty<WlMsgDef>()),

            new WlInterfaceDef("zwp_text_input_v3", 1,
                requests: new[]
                {
                    new WlMsgDef("destroy", ""),
                    new WlMsgDef("enable", ""),
                    new WlMsgDef("disable", ""),
                    new WlMsgDef("set_surrounding_text", "sii", null, null, null),
                    new WlMsgDef("set_text_change_cause", "u", (string?)null),
                    new WlMsgDef("set_content_type", "uu", null, null),
                    new WlMsgDef("set_cursor_rectangle", "iiii", null, null, null, null),
                    new WlMsgDef("commit", ""),
                },
                events: new[]
                {
                    new WlMsgDef("enter", "o", "wl_surface"),
                    new WlMsgDef("leave", "o", "wl_surface"),
                    // The string is nullable and the two cursor offsets are BYTE offsets into it.
                    new WlMsgDef("preedit_string", "?sii", null, null, null),
                    new WlMsgDef("commit_string", "?s", (string?)null),
                    new WlMsgDef("delete_surrounding_text", "uu", null, null),
                    new WlMsgDef("done", "u", (string?)null),
                }),

            // ---- tablet-v2 -----------------------------------------------------------------
            //
            // A stylus. Without this the compositor emulates a pointer from the pen for clients
            // that did not bind the protocol -- position only, so pressure and tilt never arrive.
            // Binding it turns that emulation OFF for this client, which is why WaylandTablet has
            // to drive the mouse itself as well as the touch seam.
            //
            // All EIGHT interfaces are declared, not just the three that carry pen data. The pad
            // (the buttons and rings on the tablet body) is announced by an event whose argument is
            // a new_id, and libwayland creates the proxy for it during demarshalling whether or not
            // anything is listening -- with a NULL wl_interface it cannot, and a client that owns a
            // Wacom with a pad would die on the first pad_added. The same reasoning cascades to
            // pad_group, and from there to ring, strip and dial.

            new WlInterfaceDef("zwp_tablet_manager_v2", 2,
                requests: new[]
                {
                    new WlMsgDef("get_tablet_seat", "no", "zwp_tablet_seat_v2", "wl_seat"),
                    new WlMsgDef("destroy", ""),
                },
                events: Array.Empty<WlMsgDef>()),

            new WlInterfaceDef("zwp_tablet_seat_v2", 2,
                requests: new[]
                {
                    new WlMsgDef("destroy", ""),
                },
                events: new[]
                {
                    new WlMsgDef("tablet_added", "n", "zwp_tablet_v2"),
                    new WlMsgDef("tool_added", "n", "zwp_tablet_tool_v2"),
                    new WlMsgDef("pad_added", "n", "zwp_tablet_pad_v2"),
                }),

            new WlInterfaceDef("zwp_tablet_tool_v2", 2,
                requests: new[]
                {
                    new WlMsgDef("set_cursor", "u?oii", null, "wl_surface", null, null),
                    new WlMsgDef("destroy", ""),
                },
                events: new[]
                {
                    new WlMsgDef("type", "u", (string?)null),
                    new WlMsgDef("hardware_serial", "uu", null, null),
                    new WlMsgDef("hardware_id_wacom", "uu", null, null),
                    new WlMsgDef("capability", "u", (string?)null),
                    new WlMsgDef("done", ""),
                    new WlMsgDef("removed", ""),
                    new WlMsgDef("proximity_in", "uoo", null, "zwp_tablet_v2", "wl_surface"),
                    new WlMsgDef("proximity_out", ""),
                    new WlMsgDef("down", "u", (string?)null),
                    new WlMsgDef("up", ""),
                    new WlMsgDef("motion", "ff", null, null),
                    new WlMsgDef("pressure", "u", (string?)null),
                    new WlMsgDef("distance", "u", (string?)null),
                    new WlMsgDef("tilt", "ff", null, null),
                    new WlMsgDef("rotation", "f", (string?)null),
                    new WlMsgDef("slider", "i", (string?)null),
                    new WlMsgDef("wheel", "fi", null, null),
                    new WlMsgDef("button", "uuu", null, null, null),
                    new WlMsgDef("frame", "u", (string?)null),
                }),

            new WlInterfaceDef("zwp_tablet_v2", 2,
                requests: new[]
                {
                    new WlMsgDef("destroy", ""),
                },
                events: new[]
                {
                    new WlMsgDef("name", "s", (string?)null),
                    new WlMsgDef("id", "uu", null, null),
                    new WlMsgDef("path", "s", (string?)null),
                    new WlMsgDef("done", ""),
                    new WlMsgDef("removed", ""),
                    new WlMsgDef("bustype", "2u", (string?)null),
                }),

            new WlInterfaceDef("zwp_tablet_pad_v2", 2,
                requests: new[]
                {
                    new WlMsgDef("set_feedback", "usu", null, null, null),
                    new WlMsgDef("destroy", ""),
                },
                events: new[]
                {
                    new WlMsgDef("group", "n", "zwp_tablet_pad_group_v2"),
                    new WlMsgDef("path", "s", (string?)null),
                    new WlMsgDef("buttons", "u", (string?)null),
                    new WlMsgDef("done", ""),
                    new WlMsgDef("button", "uuu", null, null, null),
                    new WlMsgDef("enter", "uoo", null, "zwp_tablet_v2", "wl_surface"),
                    new WlMsgDef("leave", "uo", null, "wl_surface"),
                    new WlMsgDef("removed", ""),
                }),

            new WlInterfaceDef("zwp_tablet_pad_group_v2", 2,
                requests: new[]
                {
                    new WlMsgDef("destroy", ""),
                },
                events: new[]
                {
                    new WlMsgDef("buttons", "a", (string?)null),
                    new WlMsgDef("ring", "n", "zwp_tablet_pad_ring_v2"),
                    new WlMsgDef("strip", "n", "zwp_tablet_pad_strip_v2"),
                    new WlMsgDef("modes", "u", (string?)null),
                    new WlMsgDef("done", ""),
                    new WlMsgDef("mode_switch", "uuu", null, null, null),
                    new WlMsgDef("dial", "2n", "zwp_tablet_pad_dial_v2"),
                }),

            new WlInterfaceDef("zwp_tablet_pad_ring_v2", 2,
                requests: new[]
                {
                    new WlMsgDef("set_feedback", "su", null, null),
                    new WlMsgDef("destroy", ""),
                },
                events: new[]
                {
                    new WlMsgDef("source", "u", (string?)null),
                    new WlMsgDef("angle", "f", (string?)null),
                    new WlMsgDef("stop", ""),
                    new WlMsgDef("frame", "u", (string?)null),
                }),

            new WlInterfaceDef("zwp_tablet_pad_strip_v2", 2,
                requests: new[]
                {
                    new WlMsgDef("set_feedback", "su", null, null),
                    new WlMsgDef("destroy", ""),
                },
                events: new[]
                {
                    new WlMsgDef("source", "u", (string?)null),
                    new WlMsgDef("position", "u", (string?)null),
                    new WlMsgDef("stop", ""),
                    new WlMsgDef("frame", "u", (string?)null),
                }),

            new WlInterfaceDef("zwp_tablet_pad_dial_v2", 2,
                requests: new[]
                {
                    new WlMsgDef("set_feedback", "su", null, null),
                    new WlMsgDef("destroy", ""),
                },
                events: new[]
                {
                    new WlMsgDef("delta", "i", (string?)null),
                    new WlMsgDef("frame", "u", (string?)null),
                }),
        };
    }
}
