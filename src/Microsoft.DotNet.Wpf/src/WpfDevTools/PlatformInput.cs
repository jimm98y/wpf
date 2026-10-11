// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Where a dispatched event enters WPF.
//
// One translation per head, because input entry is per head: each backend reports in
// its own vocabulary (NSEventTypes on macOS, DOM kinds in the browser, evdev codes on
// Wayland, touch kinds on the phones) and each has a static event the framework's
// input providers already subscribe to. Injecting there means an event travels the
// path a real one travels, which is the whole point -- MouseEventArgs.GetPosition and
// UIElement.CaptureMouse read the MouseDevice rather than the event, so routed events
// raised by hand cannot drive anything that captures, drags, hovers or selects.
//
// The domains speak the neutral vocabulary below and never a platform's own, so adding
// a head is a case in two switches and nothing else moves.
//

using System;
using MS.Internal.Interop;
using MS.Internal.Interop.Wayland;

namespace Microsoft.Wpf.DevTools
{
    internal enum InjectedMouse
    {
        Move,
        Down,
        Up,
        Wheel,
    }

    internal enum InjectedButton
    {
        Left,
        Right,
        Middle,
    }

    internal static class PlatformInput
    {
        /// <summary>CDP's buttons bitmask, for telling a drag from a hover.</summary>
        private const int LeftHeld = 1, RightHeld = 2, MiddleHeld = 4;

        // Raw NSEventTypes, the vocabulary CocoaWindow reports in.
        private const int NSLeftDown = 1, NSLeftUp = 2, NSRightDown = 3, NSRightUp = 4;
        private const int NSMouseMoved = 5, NSLeftDragged = 6, NSRightDragged = 7;
        private const int NSScrollWheel = 22, NSOtherDown = 25, NSOtherUp = 26, NSOtherDragged = 27;

        // evdev button codes, the vocabulary Wayland reports in.
        private const int BtnLeft = 0x110, BtnRight = 0x111, BtnMiddle = 0x112;

        private static bool s_warned;

        /// <summary>True when this head can accept injected input.</summary>
        internal static bool Available =>
            OperatingSystem.IsMacOS() || OperatingSystem.IsBrowser() || OperatingSystem.IsIOS() ||
            OperatingSystem.IsAndroid() || (OperatingSystem.IsLinux() && !OperatingSystem.IsAndroid());

        /// <summary>
        /// Deliver a mouse event. Coordinates are client DEVICE pixels, top-left origin;
        /// wheel is in WPF units, 120 to a notch. heldButtons is CDP's bitmask, which only
        /// matters where a backend distinguishes a drag from a move.
        /// </summary>
        internal static void Mouse(IntPtr window, InjectedMouse kind, InjectedButton button,
                                   int heldButtons, int x, int y, int wheel)
        {
            try
            {
                if (OperatingSystem.IsMacOS())
                {
                    CocoaWindow.InjectMouse(window, CocoaType(kind, button, heldButtons),
                                            CocoaButtonNumber(button), x, y, wheel);
                    return;
                }

                if (OperatingSystem.IsBrowser())
                {
                    // DOM button indices: 0 left, 1 middle, 2 right -- not the order the
                    // neutral enum happens to be in.
                    int dom = button switch
                    {
                        InjectedButton.Right => 2,
                        InjectedButton.Middle => 1,
                        _ => 0,
                    };

                    BrowserWindow.InjectMouse(window, (int)kind, dom, x, y, wheel);
                    return;
                }

                // The phones report touches, which the provider already drives the mouse
                // from; a touch has no button, so the neutral button is dropped rather than
                // mistranslated.
                if (OperatingSystem.IsIOS())
                {
                    UIKitWindow.InjectMouse(window, (int)kind, x, y, wheel);
                    return;
                }

                if (OperatingSystem.IsAndroid())
                {
                    AndroidWindow.InjectMouse(window, (int)kind, x, y, wheel);
                    return;
                }

                if (OperatingSystem.IsLinux())
                {
                    WaylandInput.InjectMouse(window, WaylandKind(kind), WaylandButton(button), x, y, wheel);
                    return;
                }

                Unsupported();
            }
            catch (Exception ex)
            {
                DevToolsServer.Log($"mouse injection failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// Deliver a key event. `characters` is what was typed, which is the half a text box
        /// reads; `key` is the frontend's name for the key itself.
        /// </summary>
        internal static void Key(IntPtr window, bool down, string key, string characters, int modifiers)
        {
            try
            {
                if (OperatingSystem.IsMacOS())
                {
                    CocoaWindow.InjectKey(new CocoaWindow.CocoaKeyMessage(
                        window, down, keyCode: 0, characters, isRepeat: false,
                        modifierFlags: (ulong)modifiers, timestampMs: Environment.TickCount));
                    return;
                }

                if (OperatingSystem.IsBrowser())
                {
                    // CDP modifiers: 1 alt, 2 ctrl, 4 meta, 8 shift.
                    BrowserWindow.InjectKey(window, down, code: key, key: characters.Length > 0 ? characters : key,
                                            ctrl: (modifiers & 2) != 0, shift: (modifiers & 8) != 0,
                                            alt: (modifiers & 1) != 0, meta: (modifiers & 4) != 0);
                    return;
                }

                if (OperatingSystem.IsLinux() && !OperatingSystem.IsAndroid())
                {
                    // Keysym 0: the characters carry the text, which is what reaches a text
                    // box. A key that is only a keysym -- Tab, the arrows -- would need the
                    // XKB mapping, which is not reconstructed here.
                    WaylandInput.InjectKey(window, down, keysym: 0, characters);
                    return;
                }

                // iOS and Android have no key path at all: HwndKeyboardInputProvider does not
                // subscribe to one there, because text arrives through the soft keyboard's own
                // route. Saying so beats inventing an event nothing is listening for.
                Unsupported();
            }
            catch (Exception ex)
            {
                DevToolsServer.Log($"key injection failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // ------------------------------------------------------------------
        // Per-head vocabularies
        // ------------------------------------------------------------------

        /// <summary>
        /// A move with a button held is a DRAG, and AppKit says so with its own event type.
        /// Reported as a plain move, the drag is lost on anything that tells them apart.
        /// </summary>
        private static int CocoaType(InjectedMouse kind, InjectedButton button, int heldButtons) => kind switch
        {
            InjectedMouse.Wheel => NSScrollWheel,
            InjectedMouse.Down => button switch
            {
                InjectedButton.Right => NSRightDown,
                InjectedButton.Middle => NSOtherDown,
                _ => NSLeftDown,
            },
            InjectedMouse.Up => button switch
            {
                InjectedButton.Right => NSRightUp,
                InjectedButton.Middle => NSOtherUp,
                _ => NSLeftUp,
            },
            _ => (heldButtons & LeftHeld) != 0 ? NSLeftDragged
               : (heldButtons & RightHeld) != 0 ? NSRightDragged
               : (heldButtons & MiddleHeld) != 0 ? NSOtherDragged
               : NSMouseMoved,
        };

        /// <summary>macOS button numbers: 0 left, 1 right, 2 middle.</summary>
        private static int CocoaButtonNumber(InjectedButton button) => button switch
        {
            InjectedButton.Right => 1,
            InjectedButton.Middle => 2,
            _ => 0,
        };

        private static WaylandMouseKind WaylandKind(InjectedMouse kind) => kind switch
        {
            InjectedMouse.Down => WaylandMouseKind.ButtonDown,
            InjectedMouse.Up => WaylandMouseKind.ButtonUp,
            InjectedMouse.Wheel => WaylandMouseKind.Wheel,
            _ => WaylandMouseKind.Move,
        };

        private static int WaylandButton(InjectedButton button) => button switch
        {
            InjectedButton.Right => BtnRight,
            InjectedButton.Middle => BtnMiddle,
            _ => BtnLeft,
        };

        private static void Unsupported()
        {
            if (s_warned)
                return;

            s_warned = true;
            DevToolsServer.Log("this head has no input entry point for that event; it was ignored " +
                               "(see PlatformInput -- one translation per head)");
        }
    }
}
