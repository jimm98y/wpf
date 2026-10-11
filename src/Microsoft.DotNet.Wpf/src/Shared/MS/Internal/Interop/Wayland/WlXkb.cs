// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// libxkbcommon: the keymap side of Wayland keyboard input.
//
// Wayland deliberately sends almost nothing interpreted. wl_keyboard.key carries a raw evdev
// scancode and nothing else -- no virtual key, no character, no notion of which layout or modifiers
// are in effect. The compositor instead hands the client an XKB keymap (as a shared-memory fd) and
// expects it to do the translation itself. libxkbcommon is that translation, and it is the same
// library GTK, Qt, SDL and the compositor all use, so the results agree with the rest of the desktop.
//
// Two outputs matter here, and they are NOT the same question:
//   * the KEYSYM (xkb_state_key_get_one_sym) drives WPF's Key/virtual-key world -- Tab, F5, arrows;
//   * the UTF-8 (xkb_state_key_get_utf8) drives text input, and correctly yields nothing for a
//     dead key and then the composed character on the following press.
//
// Compose (dead keys, Ctrl+Shift+U sequences) is a separate object again: xkb_compose_state feeds
// on keysyms and reports COMPOSING / COMPOSED / CANCELLED, which is what keeps "´" + "e" from
// emitting two characters instead of "é".
//

using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace MS.Internal.Interop.Wayland
{
    internal enum XkbKeyDirection { Up = 0, Down = 1 }

    /// <summary>enum xkb_state_component (only the parts we consume).</summary>
    [Flags]
    internal enum XkbStateComponent
    {
        ModsDepressed = 1 << 0,
        ModsLatched = 1 << 1,
        ModsLocked = 1 << 2,
        ModsEffective = 1 << 3,
        LayoutEffective = 1 << 7,
    }

    internal enum XkbComposeStatus
    {
        Nothing = 0,
        Composing = 1,
        Composed = 2,
        Cancelled = 3,
    }

    internal enum XkbComposeFeedResult
    {
        Ignored = 0,
        Accepted = 1,
    }

    [SupportedOSPlatform("linux")]
    internal static unsafe class WlXkb
    {
        private const string Lib = "libxkbcommon.so.0";

        internal const int XKB_CONTEXT_NO_FLAGS = 0;
        internal const int XKB_KEYMAP_COMPILE_NO_FLAGS = 0;
        internal const int XKB_KEYMAP_FORMAT_TEXT_V1 = 1;
        internal const int XKB_COMPOSE_COMPILE_NO_FLAGS = 0;
        internal const int XKB_COMPOSE_STATE_NO_FLAGS = 0;

        [DllImport(Lib)] internal static extern IntPtr xkb_context_new(int flags);
        [DllImport(Lib)] internal static extern void xkb_context_unref(IntPtr context);

        [DllImport(Lib)]
        internal static extern IntPtr xkb_keymap_new_from_string(IntPtr context, byte* text, int format, int flags);

        [DllImport(Lib)] internal static extern void xkb_keymap_unref(IntPtr keymap);
        [DllImport(Lib)] internal static extern IntPtr xkb_state_new(IntPtr keymap);
        [DllImport(Lib)] internal static extern void xkb_state_unref(IntPtr state);

        [DllImport(Lib)]
        internal static extern int xkb_state_update_mask(IntPtr state, uint depressedMods, uint latchedMods,
                                                         uint lockedMods, uint depressedLayout, uint latchedLayout, uint lockedLayout);

        /// <summary>The keysym for a keycode in the current state. Note the +8: Wayland reports raw
        /// evdev codes while XKB keycodes are evdev + 8, an offset inherited from X11.</summary>
        [DllImport(Lib)] internal static extern uint xkb_state_key_get_one_sym(IntPtr state, uint xkbKeycode);

        [DllImport(Lib)] internal static extern int xkb_state_key_get_utf8(IntPtr state, uint xkbKeycode, byte* buffer, nuint size);

        [DllImport(Lib)]
        [return: MarshalAs(UnmanagedType.I1)]
        internal static extern bool xkb_state_mod_name_is_active(IntPtr state, [MarshalAs(UnmanagedType.LPUTF8Str)] string modName, XkbStateComponent type);

        [DllImport(Lib)]
        [return: MarshalAs(UnmanagedType.I1)]
        internal static extern bool xkb_keymap_key_repeats(IntPtr keymap, uint xkbKeycode);

        // ---- Compose ------------------------------------------------------------------------

        [DllImport(Lib)]
        internal static extern IntPtr xkb_compose_table_new_from_locale(IntPtr context,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string locale, int flags);

        [DllImport(Lib)] internal static extern void xkb_compose_table_unref(IntPtr table);
        [DllImport(Lib)] internal static extern IntPtr xkb_compose_state_new(IntPtr table, int flags);
        [DllImport(Lib)] internal static extern void xkb_compose_state_unref(IntPtr state);
        [DllImport(Lib)] internal static extern XkbComposeFeedResult xkb_compose_state_feed(IntPtr state, uint keysym);
        [DllImport(Lib)] internal static extern XkbComposeStatus xkb_compose_state_get_status(IntPtr state);
        [DllImport(Lib)] internal static extern uint xkb_compose_state_get_one_sym(IntPtr state);
        [DllImport(Lib)] internal static extern int xkb_compose_state_get_utf8(IntPtr state, byte* buffer, nuint size);
        [DllImport(Lib)] internal static extern void xkb_compose_state_reset(IntPtr state);

        // XKB keycodes are evdev keycodes plus this offset (an X11 legacy: X keycodes start at 8).
        internal const uint EvdevOffset = 8;

        /// <summary>The mod names XKB uses, for xkb_state_mod_name_is_active.</summary>
        internal const string ModShift = "Shift";
        internal const string ModControl = "Control";
        internal const string ModAlt = "Mod1";
        internal const string ModSuper = "Mod4";
        internal const string ModCapsLock = "Lock";
        internal const string ModNumLock = "Mod2";
    }
}
