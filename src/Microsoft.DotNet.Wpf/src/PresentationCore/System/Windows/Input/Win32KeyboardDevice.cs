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

        protected override KeyStates GetKeyStatesFromSystem(Key key)
        {
            KeyStates keyStates = KeyStates.None;

            int virtualKeyCode = KeyInterop.VirtualKeyFromKey(key);

            if (!System.OperatingSystem.IsWindows())
            {
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

