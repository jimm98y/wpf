// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using MS.Win32; // VK translation.

namespace System.Windows.Input
{
    /// <summary>
    ///     The Win32KeyboardDevice class implements the platform specific
    ///     KeyboardDevice features for the Win32 platform
    /// </summary>
    internal sealed class Win32KeyboardDevice : KeyboardDevice
    {
        /// <summary>
        /// 
        /// </summary>
        /// <param name="inputManager">
        /// </param>
        internal Win32KeyboardDevice(InputManager inputManager)
            : base(inputManager)
        {
        }

        /// <summary>
        ///     Gets the current state of the specified key from the device from the underlying system
        /// </summary>
        /// <param name="key">
        ///     Key to get the state of
        /// </param>
        /// <returns>                           
        ///     The state of the specified key
        /// </returns>
        // Off-Windows there is no GetKeyState; key state is tracked from the input reports as the
        // keyboard provider feeds them in (see TrackMacKey). Indexed by Win32 virtual-key code.
        private static readonly KeyStates[] s_macKeyStates = new KeyStates[256];

        internal static void TrackMacKey(int virtualKey, bool down)
        {
            if ((uint)virtualKey > 255) return;

            if (down)
            {
                s_macKeyStates[virtualKey] |= KeyStates.Down;
                // Lock keys flip their toggled state on each press.
                if (virtualKey == 0x14 /*VK_CAPITAL*/ || virtualKey == 0x90 /*VK_NUMLOCK*/)
                {
                    s_macKeyStates[virtualKey] ^= KeyStates.Toggled;
                }
            }
            else
            {
                s_macKeyStates[virtualKey] &= ~KeyStates.Down;
            }
        }

        /// <summary>
        /// macOS: a modifier key's state as the system has it now. Tracked from this window's key reports
        /// alone, a modifier pressed while another window had the keyboard -- the WinForms form an
        /// ElementHost's WPF content sits in, until the click that gives the content the keyboard -- read
        /// as up: Alt+drag never rotated a 3D view. On Windows GetKeyState is the thread's keyboard state.
        /// Null for any other key.
        /// </summary>
        private static KeyStates? MacModifierState(int vk)
        {
            const ulong Caps = 0x10000, Shift = 0x20000, Control = 0x40000, Option = 0x80000, Command = 0x100000;
            const ulong LCtl = 0x01, LShift = 0x02, RShift = 0x04, LCmd = 0x08, RCmd = 0x10, LAlt = 0x20, RAlt = 0x40, RCtl = 0x2000;
            ulong want, side;
            switch (vk)
            {
                case 0x10: want = Shift; side = 0; break;            // VK_SHIFT
                case 0xA0: want = Shift; side = LShift; break;       // VK_LSHIFT
                case 0xA1: want = Shift; side = RShift; break;       // VK_RSHIFT
                case 0x11: want = Control; side = 0; break;          // VK_CONTROL
                case 0xA2: want = Control; side = LCtl; break;       // VK_LCONTROL
                case 0xA3: want = Control; side = RCtl; break;       // VK_RCONTROL
                case 0x12: want = Option; side = 0; break;           // VK_MENU (Alt: Option)
                case 0xA4: want = Option; side = LAlt; break;        // VK_LMENU
                case 0xA5: want = Option; side = RAlt; break;        // VK_RMENU
                case 0x5B: want = Command; side = LCmd; break;       // VK_LWIN (Command)
                case 0x5C: want = Command; side = RCmd; break;       // VK_RWIN
                case 0x14:                                           // VK_CAPITAL: the lock's state
                {
                    ulong f = MS.Internal.Interop.CocoaWindow.CurrentModifierFlags();
                    KeyStates s = s_macKeyStates[0x14] & KeyStates.Down;
                    return (f & Caps) != 0 ? s | KeyStates.Toggled : s;
                }
                default: return null;
            }
            ulong flags = MS.Internal.Interop.CocoaWindow.CurrentModifierFlags();
            bool down = (flags & want) != 0 && (side == 0 || (flags & side) != 0);
            return down ? KeyStates.Down : KeyStates.None;
        }

        protected override KeyStates GetKeyStatesFromSystem(Key key)
        {
            KeyStates keyStates = KeyStates.None;

            int virtualKeyCode = KeyInterop.VirtualKeyFromKey(key);

            if (!System.OperatingSystem.IsWindows())
            {
                if (System.OperatingSystem.IsMacOS() && MacModifierState(virtualKeyCode) is KeyStates modifier) return modifier;
                return ((uint)virtualKeyCode <= 255) ? s_macKeyStates[virtualKeyCode] : KeyStates.None;
            }

            int nativeKeyState = UnsafeNativeMethods.GetKeyState(virtualKeyCode);

            if ((nativeKeyState & 0x00008000) == 0x00008000)
                keyStates |= KeyStates.Down;

            if ((nativeKeyState & 0x00000001) == 0x00000001)
                keyStates |= KeyStates.Toggled;

            return keyStates;
        }
    }
}

