// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Input: driving the app from the panel, through the platform's own entry point.
//
// Events go in where a REAL one goes in -- on macOS, CocoaWindow.InjectMouse, which is
// what AppKit's own reporting calls -- and from there through
// HwndMouseInputProvider and the InputManager exactly as a physical mouse would.
// So capture, hover states, Mouse.DirectlyOver, drag, text selection, scrollbar
// thumbs and hit testing all simply work, because nothing about them is being
// approximated.
//
// This replaced a pile of simulation: routed events raised by hand, automation
// peers invoked to make a Button click, thumb drags reconstructed through
// Track.ValueFromDistance, text selection driven by GetCharacterIndexFromPoint.
// All of it was working around one fact -- MouseEventArgs.GetPosition and
// CaptureMouse read the MouseDevice, not the event -- so a handler written the
// ordinary way (capture on down, GetPosition on move) saw the real cursor,
// wherever that happened to be. None of that arithmetic has to exist if the
// device is told where the mouse is, which is what injection does.
//
// The seam is per head, because input entry is. macOS is implemented; the other
// heads each have their own equivalent (BrowserWindow's queue, the Wayland and
// UIKit and Android backends) and are one method each. Where there is none, a
// dispatched event is reported as unsupported rather than half-delivered.
//

using System;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Wpf.DevTools.Json;

namespace Microsoft.Wpf.DevTools.Domains
{
    internal sealed class InputDomain : ICdpDomain
    {
        // Raw NSEventType values, the vocabulary CocoaWindow reports in.
        private const int NSLeftMouseDown = 1, NSLeftMouseUp = 2;
        private const int NSRightMouseDown = 3, NSRightMouseUp = 4;
        private const int NSMouseMoved = 5, NSLeftMouseDragged = 6, NSRightMouseDragged = 7;
        private const int NSScrollWheel = 22;
        private const int NSOtherMouseDown = 25, NSOtherMouseUp = 26, NSOtherMouseDragged = 27;

        /// <summary>CDP's buttons bitmask.</summary>
        private const int LeftButtonHeld = 1, RightButtonHeld = 2, MiddleButtonHeld = 4;

        /// <summary>
        /// Pixels the frontend reports for one wheel notch. CDP speaks pixels; a Cocoa wheel
        /// message carries WPF units, 120 to a notch.
        /// </summary>
        private const double WheelPixelsPerNotch = 100.0;

        private readonly CdpSession _session;

        /// <summary>Where the injected pointer was last put, so a move can precede anything else.</summary>
        private int _lastX = int.MinValue, _lastY = int.MinValue;

        internal InputDomain(CdpSession session)
        {
            _session = session;
        }

        public bool TryHandle(string method, JsonElement p, Utf8JsonWriter w)
        {
            switch (method)
            {
                case "Input.dispatchMouseEvent":
                    DispatchMouse(p);
                    return true;

                case "Input.dispatchKeyEvent":
                    DispatchKey(p);
                    return true;

                case "Input.insertText":
                    InsertText(CdpJson.GetString(p, "text"));
                    return true;

                case "Input.setIgnoreInputEvents":
                case "Input.emulateTouchFromMouseEvent":
                    return true;

                default:
                    return false;
            }
        }

        // ------------------------------------------------------------------
        // Mouse
        // ------------------------------------------------------------------

        private void DispatchMouse(JsonElement p)
        {
            string type = CdpJson.GetString(p, "type") ?? string.Empty;
            var point = new Point(CdpJson.GetDouble(p, "x"), CdpJson.GetDouble(p, "y"));

            // The picker takes precedence: "Select element" driven over the screencast
            // arrives as mouse events, and a hover there has to highlight rather than reach
            // the app at all.
            if (_session.Overlay.InspectModeActive)
            {
                if (type == "mouseMoved")
                    _session.Overlay.HighlightAt(point);
                else if (type == "mousePressed")
                    _session.Overlay.PickAt(point);
                return;
            }

            if (!TryResolveTarget(point, out IntPtr view, out int x, out int y))
                return;

            int buttons = CdpJson.GetInt(p, "buttons");
            string button = CdpJson.GetString(p, "button") ?? "none";

            // Put the pointer there first, if it is not there already.
            //
            // A real mouse is somewhere before it clicks or scrolls, and WPF relies on that:
            // Mouse.DirectlyOver is established by movement, and a wheel arriving at a position
            // the device has never been reported at scrolls nothing. The gallery's own input
            // self-test opens with a move for the same reason.
            if (type != "mouseMoved" && (x != _lastX || y != _lastY))
                MoveTo(view, buttons, x, y);

            switch (type)
            {
                case "mousePressed":
                    PlatformInput.Mouse(view, DownType(button), ButtonNumber(button), x, y, 0);
                    break;

                case "mouseReleased":
                    PlatformInput.Mouse(view, UpType(button), ButtonNumber(button), x, y, 0);
                    break;

                case "mouseMoved":
                    MoveTo(view, buttons, x, y);
                    break;

                case "mouseWheel":
                    int wheel = (int)Math.Round(-CdpJson.GetDouble(p, "deltaY") / WheelPixelsPerNotch
                                                * Mouse.MouseWheelDeltaForOneLine);
                    if (wheel != 0)
                        PlatformInput.Mouse(view, NSScrollWheel, 0, x, y, wheel);
                    break;
            }
        }

        /// <summary>
        /// Move the pointer. A move with a button held is a DRAG, and AppKit says so with a
        /// distinct event type; reporting it as a plain move loses the drag on anything that
        /// tells them apart.
        /// </summary>
        private void MoveTo(IntPtr view, int buttons, int x, int y)
        {
            PlatformInput.Mouse(view, MovedType(buttons), 0, x, y, 0);
            _lastX = x;
            _lastY = y;
        }

        private static int DownType(string button) => button switch
        {
            "right" => NSRightMouseDown,
            "middle" => NSOtherMouseDown,
            _ => NSLeftMouseDown,
        };

        private static int UpType(string button) => button switch
        {
            "right" => NSRightMouseUp,
            "middle" => NSOtherMouseUp,
            _ => NSLeftMouseUp,
        };

        private static int MovedType(int buttons)
        {
            if ((buttons & LeftButtonHeld) != 0) return NSLeftMouseDragged;
            if ((buttons & RightButtonHeld) != 0) return NSRightMouseDragged;
            if ((buttons & MiddleButtonHeld) != 0) return NSOtherMouseDragged;
            return NSMouseMoved;
        }

        private static int ButtonNumber(string button) => button switch
        {
            "right" => 1,
            "middle" => 2,
            _ => 0,
        };

        /// <summary>
        /// The window a page-space point belongs to, and that point in the client DEVICE
        /// pixels the platform reports in. The frontend speaks device-independent pixels; a
        /// Cocoa message does not, and skipping the conversion puts every event at a fraction
        /// of where it should be on a scaled display.
        /// </summary>
        private static bool TryResolveTarget(Point page, out IntPtr view, out int x, out int y)
        {
            view = IntPtr.Zero;
            x = y = 0;

            foreach (Visual root in VisualTreeModel.VisualRoots())
            {
                try
                {
                    if (PresentationSource.FromVisual(root) is not HwndSource source ||
                        source.Handle == IntPtr.Zero)
                    {
                        continue;
                    }

                    Point device = source.CompositionTarget?.TransformToDevice.Transform(page) ?? page;

                    view = source.Handle;
                    x = (int)Math.Round(device.X);
                    y = (int)Math.Round(device.Y);
                    return true;
                }
                catch
                {
                    // Mid-teardown; try the next root.
                }
            }

            return false;
        }

        // ------------------------------------------------------------------
        // Keyboard
        // ------------------------------------------------------------------

        private void DispatchKey(JsonElement p)
        {
            string type = CdpJson.GetString(p, "type") ?? string.Empty;

            // "char" carries the text, which is the half that reaches a text box.
            if (type == "char")
            {
                InsertText(CdpJson.GetString(p, "text"));
                return;
            }

            bool down = type is "keyDown" or "rawKeyDown";
            if (!down && type != "keyUp")
                return;

            if (!TryResolveWindow(out IntPtr view))
                return;

            string? text = CdpJson.GetString(p, "text");
            if (string.IsNullOrEmpty(text))
                text = CdpJson.GetString(p, "key");

            PlatformInput.Key(view, down, CdpJson.GetInt(p, "nativeVirtualKeyCode"),
                              text ?? string.Empty, CdpJson.GetInt(p, "modifiers"));
        }

        private void InsertText(string? text)
        {
            if (string.IsNullOrEmpty(text) || !TryResolveWindow(out IntPtr view))
                return;

            // A key down/up pair carrying the characters, which is how a real typed character
            // arrives -- the text input provider reads the characters, not the key code.
            foreach (char c in text)
            {
                PlatformInput.Key(view, down: true, keyCode: 0, c.ToString(), modifiers: 0);
                PlatformInput.Key(view, down: false, keyCode: 0, c.ToString(), modifiers: 0);
            }
        }

        private static bool TryResolveWindow(out IntPtr view)
            => TryResolveTarget(new Point(0, 0), out view, out _, out _);
    }
}
