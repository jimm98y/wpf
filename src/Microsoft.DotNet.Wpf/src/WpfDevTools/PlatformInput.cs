// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Where a dispatched event enters WPF.
//
// One method per head, because input entry is per head. The alternative -- raising
// routed events by hand -- cannot be made to work: MouseEventArgs.GetPosition and
// UIElement.CaptureMouse both read the MouseDevice rather than the event, so a drag
// handler written the ordinary way sees the real cursor wherever it happens to be,
// and nothing that depends on capture responds at all.
//
// macOS is implemented. The others each have an equivalent entry point of their own
// (BrowserWindow's event queue, the Wayland/UIKit/Android backends) and are one
// method each; until then a dispatched event is reported rather than half-delivered.
//

using System;
using MS.Internal.Interop;

namespace Microsoft.Wpf.DevTools
{
    internal static class PlatformInput
    {
        private static bool s_warned;

        /// <summary>True when this head can accept injected input.</summary>
        internal static bool Available => OperatingSystem.IsMacOS();

        /// <summary>
        /// Deliver a mouse event. Coordinates are client DEVICE pixels, top-left origin.
        /// nsType is a raw NSEventType; wheel is in WPF units, 120 to a notch.
        /// </summary>
        internal static void Mouse(IntPtr view, int nsType, int buttonNumber, int x, int y, int wheel)
        {
            if (!Available)
            {
                Unsupported();
                return;
            }

            try
            {
                CocoaWindow.InjectMouse(view, nsType, buttonNumber, x, y, wheel);
            }
            catch (Exception ex)
            {
                DevToolsServer.Log($"mouse injection failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>Deliver a key event. `characters` is what was typed, which is what a text box reads.</summary>
        internal static void Key(IntPtr view, bool down, int keyCode, string characters, int modifiers)
        {
            if (!Available)
            {
                Unsupported();
                return;
            }

            try
            {
                CocoaWindow.InjectKey(new CocoaWindow.CocoaKeyMessage(
                    view, down, keyCode, characters, isRepeat: false,
                    modifierFlags: (ulong)modifiers, timestampMs: Environment.TickCount));
            }
            catch (Exception ex)
            {
                DevToolsServer.Log($"key injection failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static void Unsupported()
        {
            if (s_warned)
                return;

            s_warned = true;
            DevToolsServer.Log("this head has no input entry point yet; dispatched input is ignored " +
                               "(see PlatformInput -- one method per head)");
        }
    }
}
