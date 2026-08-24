// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using MS.Win32; // *NativeMethods

namespace System.Windows.Input
{
    /// <summary>
    ///     The Win32MouseDevice class implements the platform specific
    ///     MouseDevice features for the Win32 platform
    /// </summary>
    internal sealed class Win32MouseDevice : MouseDevice
    {
        /// <summary>
        ///
        /// </summary>
        /// <param name="inputManager">
        /// </param>
        internal Win32MouseDevice(InputManager inputManager)
            : base(inputManager)
        {
        }

        /// <summary>
        ///     Gets the current state of the specified button from the device from the underlying system
        /// </summary>
        /// <param name="mouseButton">
        ///     The mouse button to get the state of
        /// </param>
        /// <returns>
        ///     The state of the specified mouse button
        /// </returns>
        // Off-Windows there is no GetKeyState to read the async physical button state, so we track it
        // from the mouse input reports as the provider feeds them in (see TrackMacButtons). Indexed by
        // MouseButton (Left=0, Middle=1, Right=2, XButton1=3, XButton2=4).
        private static readonly MouseButtonState[] s_macButtonStates = new MouseButtonState[5];

        internal static void TrackMacButtons(RawMouseActions actions)
        {
            if ((actions & RawMouseActions.Button1Press) != 0)   s_macButtonStates[(int)MouseButton.Left]     = MouseButtonState.Pressed;
            if ((actions & RawMouseActions.Button1Release) != 0) s_macButtonStates[(int)MouseButton.Left]     = MouseButtonState.Released;
            if ((actions & RawMouseActions.Button2Press) != 0)   s_macButtonStates[(int)MouseButton.Right]    = MouseButtonState.Pressed;
            if ((actions & RawMouseActions.Button2Release) != 0) s_macButtonStates[(int)MouseButton.Right]    = MouseButtonState.Released;
            if ((actions & RawMouseActions.Button3Press) != 0)   s_macButtonStates[(int)MouseButton.Middle]   = MouseButtonState.Pressed;
            if ((actions & RawMouseActions.Button3Release) != 0) s_macButtonStates[(int)MouseButton.Middle]   = MouseButtonState.Released;
            if ((actions & RawMouseActions.Button4Press) != 0)   s_macButtonStates[(int)MouseButton.XButton1] = MouseButtonState.Pressed;
            if ((actions & RawMouseActions.Button4Release) != 0) s_macButtonStates[(int)MouseButton.XButton1] = MouseButtonState.Released;
            if ((actions & RawMouseActions.Button5Press) != 0)   s_macButtonStates[(int)MouseButton.XButton2] = MouseButtonState.Pressed;
            if ((actions & RawMouseActions.Button5Release) != 0) s_macButtonStates[(int)MouseButton.XButton2] = MouseButtonState.Released;
        }

        internal override MouseButtonState GetButtonStateFromSystem(MouseButton mouseButton)
        {
            MouseButtonState mouseButtonState = MouseButtonState.Released;

            if (!System.OperatingSystem.IsWindows())
            {
                // Security Mitigation: do not give out input state if the device is not active.
                return IsActive ? s_macButtonStates[(int)mouseButton] : MouseButtonState.Released;
            }

            // Security Mitigation: do not give out input state if the device is not active.
            if(IsActive)
            {
                int virtualKeyCode = 0;

                switch( mouseButton )
                {
                    case MouseButton.Left:
                        virtualKeyCode = NativeMethods.VK_LBUTTON;
                        break;
                    case MouseButton.Right:
                        virtualKeyCode = NativeMethods.VK_RBUTTON;
                        break;
                    case MouseButton.Middle:
                        virtualKeyCode = NativeMethods.VK_MBUTTON;
                        break;
                    case MouseButton.XButton1:
                        virtualKeyCode = NativeMethods.VK_XBUTTON1;
                        break;
                    case MouseButton.XButton2:
                        virtualKeyCode = NativeMethods.VK_XBUTTON2;
                        break;
                }

                mouseButtonState = ( UnsafeNativeMethods.GetKeyState(virtualKeyCode) & 0x8000 ) != 0 ? MouseButtonState.Pressed : MouseButtonState.Released;
            }

            return mouseButtonState;
        }
    }
}
