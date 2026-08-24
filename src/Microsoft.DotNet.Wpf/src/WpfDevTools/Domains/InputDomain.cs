// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Input: driving the app from the panel, over the screencast.
//
// BE CLEAR ABOUT WHAT THIS IS. It is not operating-system input. WPF's real
// input path starts at an InputReport raised by the platform head, and both the
// report types and the heads' injection points are internal to PresentationCore
// -- which this assembly deliberately cannot reach, because reaching them is
// what would turn the inspector into a circular reference. There is no public
// API that injects a mouse event into a running WPF app on macOS.
//
// So a dispatched event does two things instead:
//
//   1. raises the corresponding ROUTED event on the element under the point, so
//      an app's own MouseDown/MouseUp/KeyDown handlers and command bindings run;
//   2. on a click, invokes the element's AUTOMATION PEER, which is the supported
//      programmatic way to activate a control and the mechanism the a11y bridge
//      on every head already uses. This is what makes a Button actually click:
//      ButtonBase raises Click from its own capture handling, which a synthetic
//      routed MouseUp does not reproduce.
//
// What you therefore do NOT get: mouse capture, hover visual states,
// Mouse.DirectlyOver, drag. Those need the real device. Anything relying on
// them will not respond, and that is a limit of the seam rather than a bug to
// be fixed here.
//

using System;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Wpf.DevTools.Json;

namespace Microsoft.Wpf.DevTools.Domains
{
    internal sealed class InputDomain : ICdpDomain
    {
        private readonly CdpSession _session;

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
            double x = CdpJson.GetDouble(p, "x");
            double y = CdpJson.GetDouble(p, "y");

            // The picker takes precedence over clicking. "Select element" driven over the
            // SCREENCAST arrives here rather than at the real window's mouse events, so a
            // hover has to highlight and a press has to select -- not press the button that
            // happens to be under the cursor.
            if (_session.Overlay.InspectModeActive)
            {
                if (type == "mouseMoved")
                    _session.Overlay.HighlightAt(new Point(x, y));
                else if (type == "mousePressed")
                    _session.Overlay.PickAt(new Point(x, y));
                return;
            }

            // Only the release is acted on. A press/release pair would otherwise
            // activate the control twice, and CDP always sends both.
            if (type != "mousePressed" && type != "mouseReleased")
                return;

            if (!TryHitTest(new Point(x, y), out UIElement? target) || target == null)
                return;

            MouseButton button = (CdpJson.GetString(p, "button") ?? "left") switch
            {
                "right" => MouseButton.Right,
                "middle" => MouseButton.Middle,
                _ => MouseButton.Left,
            };

            RaiseMouseEvent(target, type, button);

            if (type == "mouseReleased" && button == MouseButton.Left)
                InvokeAutomationPeer(target);
        }

        private static void RaiseMouseEvent(UIElement target, string type, MouseButton button)
        {
            try
            {
                bool pressed = type == "mousePressed";
                RoutedEvent preview = pressed ? Mouse.PreviewMouseDownEvent : Mouse.PreviewMouseUpEvent;
                RoutedEvent bubbling = pressed ? Mouse.MouseDownEvent : Mouse.MouseUpEvent;

                target.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, button)
                {
                    RoutedEvent = preview,
                    Source = target,
                });

                target.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, button)
                {
                    RoutedEvent = bubbling,
                    Source = target,
                });
            }
            catch (Exception ex)
            {
                DevToolsServer.Log($"synthetic mouse event failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// Activate the control the supported way. Walks up from the hit visual,
        /// because a click lands on whatever primitive the template put there
        /// (a Border, a ContentPresenter) and the peer that can be invoked belongs
        /// to the control that owns them.
        /// </summary>
        private static void InvokeAutomationPeer(UIElement target)
        {
            DependencyObject? current = target;

            while (current != null)
            {
                if (current is UIElement element)
                {
                    AutomationPeer? peer = UIElementAutomationPeer.CreatePeerForElement(element);
                    if (peer != null)
                    {
                        try
                        {
                            if (peer.GetPattern(PatternInterface.Invoke) is IInvokeProvider invoke)
                            {
                                invoke.Invoke();
                                return;
                            }

                            if (peer.GetPattern(PatternInterface.Toggle) is IToggleProvider toggle)
                            {
                                toggle.Toggle();
                                return;
                            }

                            if (peer.GetPattern(PatternInterface.SelectionItem) is ISelectionItemProvider select)
                            {
                                select.Select();
                                return;
                            }

                            if (peer.GetPattern(PatternInterface.ExpandCollapse) is IExpandCollapseProvider expand)
                            {
                                expand.Expand();
                                return;
                            }
                        }
                        catch (Exception ex)
                        {
                            DevToolsServer.Log($"automation invoke failed: {ex.GetType().Name}: {ex.Message}");
                            return;
                        }
                    }
                }

                current = VisualTreeHelper.GetParent(current);
            }
        }

        private static bool TryHitTest(Point point, out UIElement? target)
        {
            target = null;

            foreach (Visual root in VisualTreeModel.VisualRoots())
            {
                try
                {
                    HitTestResult result = VisualTreeHelper.HitTest(root, point);
                    DependencyObject? hit = result?.VisualHit;

                    while (hit != null && (VisualTreeModel.IsInspectorOwned(hit) || hit is not UIElement))
                        hit = VisualTreeHelper.GetParent(hit);

                    if (hit is UIElement element)
                    {
                        target = element;
                        return true;
                    }
                }
                catch
                {
                    // A root mid-teardown; try the next one.
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

            // "char" carries the text; keyDown/keyUp carry the physical key. Text is
            // the half that actually reaches a TextBox, so it is handled separately.
            if (type == "char")
            {
                InsertText(CdpJson.GetString(p, "text"));
                return;
            }

            if (type != "keyDown" && type != "rawKeyDown" && type != "keyUp")
                return;

            IInputElement? focused = Keyboard.FocusedElement;
            if (focused is not UIElement element)
                return;

            string? key = CdpJson.GetString(p, "key");
            if (!TryMapKey(key, out Key mapped))
                return;

            PresentationSource? source = PresentationSource.FromDependencyObject(element);
            if (source == null)
                return;

            try
            {
                bool down = type != "keyUp";
                element.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, mapped)
                {
                    RoutedEvent = down ? Keyboard.PreviewKeyDownEvent : Keyboard.PreviewKeyUpEvent,
                });
                element.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, mapped)
                {
                    RoutedEvent = down ? Keyboard.KeyDownEvent : Keyboard.KeyUpEvent,
                });
            }
            catch (Exception ex)
            {
                DevToolsServer.Log($"synthetic key event failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// Text goes in through the Value pattern rather than as characters, for the
        /// same reason a click goes through Invoke: it is the supported way in, and
        /// synthetic TextInput does not reach a TextBox's editor.
        /// </summary>
        private static void InsertText(string? text)
        {
            if (string.IsNullOrEmpty(text))
                return;

            if (Keyboard.FocusedElement is not UIElement element)
                return;

            AutomationPeer? peer = UIElementAutomationPeer.CreatePeerForElement(element);
            if (peer?.GetPattern(PatternInterface.Value) is not IValueProvider value || value.IsReadOnly)
                return;

            try
            {
                value.SetValue((value.Value ?? string.Empty) + text);
            }
            catch (Exception ex)
            {
                DevToolsServer.Log($"insertText failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// CDP key names to WPF's Key enum. Single characters and digits map by
        /// name; everything else is a named key that Enum.TryParse already knows,
        /// which covers Enter/Tab/Escape/arrows without a table to keep in step.
        /// </summary>
        private static bool TryMapKey(string? key, out Key mapped)
        {
            mapped = Key.None;

            if (string.IsNullOrEmpty(key))
                return false;

            if (key.Length == 1)
            {
                char c = char.ToUpperInvariant(key[0]);
                if (c is >= 'A' and <= 'Z')
                    return Enum.TryParse(c.ToString(), out mapped);
                if (c is >= '0' and <= '9')
                    return Enum.TryParse("D" + c, out mapped);
            }

            return Enum.TryParse(key, ignoreCase: true, out mapped) && mapped != Key.None;
        }
    }
}
