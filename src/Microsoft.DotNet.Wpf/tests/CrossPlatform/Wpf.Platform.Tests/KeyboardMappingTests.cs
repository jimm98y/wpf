// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The libxkbcommon binding the Linux head types through.
//
// Input DELIVERY cannot be tested unattended -- wl_keyboard events come from the compositor, and a
// client cannot synthesise them (the same wall the clipboard write tests hit: no input, no serial).
// The TRANSLATION half can: given a keymap and an evdev keycode, libxkbcommon produces a keysym and
// text, and that is a pure function of inputs this test supplies itself.
//
// Which makes this worth having, because a P/Invoke binding is precisely the kind of code that
// compiles perfectly and fails at run time. The repo already warns about this shape for the Wayland
// protocol tables ("nothing in the C# compiler checks opcode order, signature strings or types[]
// arrays"); the same is true of every DllImport in WlXkb -- a wrong parameter type or a missing
// unsafe pointer marshals garbage into libxkbcommon and returns a plausible-looking wrong keysym.
//
// EvdevOffset is the specific trap. XKB keycodes are evdev keycodes plus 8 (an X11 legacy: X keycodes
// start at 8). Forgetting it does not crash -- it silently shifts the whole keyboard, so 'a' types
// something else and every test that only asks "did a key arrive" still passes.
//

using System;
using System.Runtime.InteropServices;
using System.Text;
using MS.Internal.Interop.Wayland;
using Xunit;

namespace Wpf.Platform.Tests
{
    public sealed unsafe class KeyboardMappingTests : IDisposable
    {
        // evdev keycodes from linux/input-event-codes.h, i.e. what wl_keyboard.key delivers.
        private const uint EvdevA = 30;        // KEY_A
        private const uint EvdevLeftShift = 42;
        private const uint EvdevEsc = 1;

        // X11 keysyms.
        private const uint XkA = 0x0061;       // 'a'
        private const uint XkShiftA = 0x0041;  // 'A'

        private readonly IntPtr _context;
        private readonly IntPtr _keymap;
        private readonly IntPtr _state;
        private readonly string? _unavailable;

        public KeyboardMappingTests()
        {
            if (!OperatingSystem.IsLinux()) { _unavailable = "libxkbcommon is the Linux head's keyboard layer"; return; }

            try
            {
                _context = WlXkb.xkb_context_new(WlXkb.XKB_CONTEXT_NO_FLAGS);
                if (_context == IntPtr.Zero) { _unavailable = "xkb_context_new returned null"; return; }

                byte[] utf8 = Encoding.UTF8.GetBytes(UsKeymap + "\0");
                fixed (byte* p = utf8)
                {
                    _keymap = WlXkb.xkb_keymap_new_from_string(
                        _context, p, WlXkb.XKB_KEYMAP_FORMAT_TEXT_V1, WlXkb.XKB_KEYMAP_COMPILE_NO_FLAGS);
                }
                if (_keymap == IntPtr.Zero) { _unavailable = "the test keymap did not compile"; return; }

                _state = WlXkb.xkb_state_new(_keymap);
                if (_state == IntPtr.Zero) _unavailable = "xkb_state_new returned null";
            }
            catch (DllNotFoundException)
            {
                _unavailable = "libxkbcommon.so.0 is not installed";
            }
        }

        public void Dispose()
        {
            if (_state != IntPtr.Zero) WlXkb.xkb_state_unref(_state);
            if (_keymap != IntPtr.Zero) WlXkb.xkb_keymap_unref(_keymap);
            if (_context != IntPtr.Zero) WlXkb.xkb_context_unref(_context);
        }

        private void Require() => Assert.SkipWhen(_unavailable is not null, _unavailable ?? "");

        /// <summary>
        /// The offset that silently shifts the entire keyboard when forgotten. KEY_A + 8 must produce
        /// 'a'; the raw evdev code must NOT.
        /// </summary>
        [Fact]
        public void EvdevOffset_IsAppliedBeforeLookup()
        {
            Require();

            uint withOffset = WlXkb.xkb_state_key_get_one_sym(_state, EvdevA + WlXkb.EvdevOffset);
            Assert.True(withOffset == XkA,
                $"KEY_A + EvdevOffset should be keysym 'a' (0x{XkA:X4}), got 0x{withOffset:X4}");

            uint without = WlXkb.xkb_state_key_get_one_sym(_state, EvdevA);
            Assert.True(without != XkA,
                "the RAW evdev code also produced 'a', so this keymap cannot detect a missing EvdevOffset");
        }

        /// <summary>
        /// The UTF-8 fetch is a buffer-out P/Invoke -- the shape most likely to be mis-marshalled.
        /// It must return the byte count and fill the buffer, not just one or the other.
        /// </summary>
        [Fact]
        public void KeyGetUtf8_FillsTheBufferAndReturnsTheLength()
        {
            Require();

            byte[] buf = new byte[8];
            int n;
            fixed (byte* p = buf)
                n = WlXkb.xkb_state_key_get_utf8(_state, EvdevA + WlXkb.EvdevOffset, p, (nuint)buf.Length);

            Assert.True(n == 1, $"'a' should be one UTF-8 byte, xkb reported {n}");
            Assert.Equal("a", Encoding.UTF8.GetString(buf, 0, n));
        }

        /// <summary>
        /// State updates must change the result. Depressing Shift has to turn 'a' into 'A' AND make
        /// the modifier query agree -- the two are separate calls, and typing depends on both.
        /// </summary>
        [Fact]
        public void ShiftModifier_ChangesTheKeysymAndIsReportedActive()
        {
            Require();

            uint shiftMask = ShiftMask();
            Assert.True(shiftMask != 0, "could not find the Shift modifier index in the test keymap");

            WlXkb.xkb_state_update_mask(_state, shiftMask, 0, 0, 0, 0, 0);
            try
            {
                uint sym = WlXkb.xkb_state_key_get_one_sym(_state, EvdevA + WlXkb.EvdevOffset);
                Assert.True(sym == XkShiftA,
                    $"with Shift depressed, KEY_A should be 'A' (0x{XkShiftA:X4}), got 0x{sym:X4}");

                Assert.True(WlXkb.xkb_state_mod_name_is_active(_state, WlXkb.ModShift, XkbStateComponent.ModsEffective),
                    $"'{WlXkb.ModShift}' is not reported active while its mask is depressed; the mod NAME may be wrong");
            }
            finally
            {
                WlXkb.xkb_state_update_mask(_state, 0, 0, 0, 0, 0, 0);
            }
        }

        /// <summary>
        /// Key repeat is a per-key property of the keymap, and the head asks xkb rather than
        /// repeating everything. A letter repeats; a modifier must not -- holding Shift that
        /// auto-repeated would flood the input queue.
        /// </summary>
        [Fact]
        public void KeyRepeats_DistinguishesLettersFromModifiers()
        {
            Require();

            Assert.True(WlXkb.xkb_keymap_key_repeats(_keymap, EvdevA + WlXkb.EvdevOffset),
                "a letter key should be repeatable");
            Assert.False(WlXkb.xkb_keymap_key_repeats(_keymap, EvdevLeftShift + WlXkb.EvdevOffset),
                "a modifier key must not auto-repeat");
        }

        /// <summary>
        /// Escape yields the ESC CONTROL CHARACTER (U+001B), not empty text.
        ///
        /// Worth pinning down because it is a trap for the layer above: xkb_state_key_get_utf8 returns
        /// text for control keys too, so a head that forwards whatever it gets straight to WPF as
        /// input types a control character into a TextBox on every Escape, Backspace and Tab. The
        /// filtering has to be deliberate, and it can only be deliberate if this behaviour is known.
        ///
        /// (This test originally asserted zero bytes, on the assumption that Escape is "not text".
        /// libxkbcommon disagreed, and it is right.)
        /// </summary>
        [Fact]
        public void ControlKey_YieldsItsControlCharacter_NotEmptyText()
        {
            Require();

            byte[] buf = new byte[8];
            int n;
            fixed (byte* p = buf)
                n = WlXkb.xkb_state_key_get_utf8(_state, EvdevEsc + WlXkb.EvdevOffset, p, (nuint)buf.Length);

            Assert.True(n == 1, $"Escape should produce one byte (the ESC control character), xkb reported {n}");
            Assert.True(buf[0] == 0x1B, $"Escape should be U+001B, got 0x{buf[0]:X2}");
        }

        /// <summary>
        /// Finds Shift's modifier mask by probing masks until xkb agrees Shift is active. Done by
        /// probe rather than by assuming index 0 so the test does not depend on the keymap's
        /// declaration order.
        /// </summary>
        private uint ShiftMask()
        {
            for (int i = 0; i < 8; i++)
            {
                uint mask = 1u << i;
                WlXkb.xkb_state_update_mask(_state, mask, 0, 0, 0, 0, 0);
                bool active = WlXkb.xkb_state_mod_name_is_active(_state, WlXkb.ModShift, XkbStateComponent.ModsEffective);
                WlXkb.xkb_state_update_mask(_state, 0, 0, 0, 0, 0, 0);
                if (active) return mask;
            }
            return 0;
        }

        /// <summary>
        /// A minimal US keymap, inline so the test does not depend on the box's configured layout.
        /// Only the keys probed above are defined; everything else falls through to NoSymbol, which
        /// is what makes the "raw evdev code is not 'a'" check meaningful.
        /// </summary>
        private const string UsKeymap = @"
xkb_keymap {
  xkb_keycodes ""test"" {
    minimum = 8;
    maximum = 255;
    <ESC> = 9;
    <AC01> = 38;
    <LFSH> = 50;
  };
  xkb_types ""test"" {
    virtual_modifiers NumLock;
    type ""ONE_LEVEL""  { modifiers = none; map[none] = Level1; level_name[Level1] = ""Any""; };
    type ""ALPHABETIC"" { modifiers = Shift+Lock;
                         map[none] = Level1; map[Shift] = Level2; map[Lock] = Level2;
                         level_name[Level1] = ""Base""; level_name[Level2] = ""Caps""; };
  };
  xkb_compatibility ""test"" {
    interpret Shift_L { action = SetMods(modifiers = Shift); };
  };
  xkb_symbols ""test"" {
    name[group1] = ""US"";
    key <ESC>  { type = ""ONE_LEVEL"",  [ Escape ] };
    key <AC01> { type = ""ALPHABETIC"", [ a, A ] };
    key <LFSH> { type = ""ONE_LEVEL"",  [ Shift_L ] };
    modifier_map Shift { <LFSH> };
  };
};
";
    }
}
