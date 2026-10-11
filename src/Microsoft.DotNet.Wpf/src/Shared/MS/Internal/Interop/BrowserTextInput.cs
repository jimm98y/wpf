// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The browser input-method channel: DOM composition events.
//
// A WPF window in the browser is a <canvas>, and a canvas cannot be composed into -- the DOM only
// raises compositionstart/update/end for an editable element. So the JS half keeps one invisible
// contenteditable parked at the caret and focused while a WPF text field is focused, purely to give
// the IME something to attach to; see browser-window.js. This file is the managed end of that: the
// queued composition events arrive on the pump like every other DOM event and come out as
// BrowserImeUpdate.
//
// What the DOM does NOT give, and the other platforms do, is the clause selection inside the
// composition -- IMM32 has ATTR_TARGET_CONVERTED and zwp_text_input_v3 has preedit cursor_begin/end,
// but compositionupdate carries only the string. The whole preedit therefore draws with one
// underline rather than highlighting the clause being converted. That is a fidelity limit of the
// web platform, not a shortcut here.
//
// Only the TRANSPORT lives here; ImmComposition turns these updates into a WPF composition exactly
// as it does for IMM32, Wayland and Cocoa.
//

using System;
using System.Runtime.Versioning;

namespace MS.Internal.Interop
{
    /// <summary>
    /// One change from the browser's input method. Like AppKit and unlike Wayland these arrive as
    /// separate messages rather than one atomic batch, so each event produces one of these.
    /// </summary>
    internal readonly struct BrowserImeUpdate
    {
        /// <summary>Final text to insert, or null while the composition is still in progress.</summary>
        public string CommitText { get; }

        /// <summary>The in-progress composition to display, or null to end/clear it.</summary>
        public string PreeditText { get; }

        public BrowserImeUpdate(string commit, string preedit)
        {
            CommitText = commit;
            PreeditText = preedit;
        }
    }

    [SupportedOSPlatform("browser")]
    internal static class BrowserTextInput
    {
        /// <summary>Raised on the pump thread when the browser's input method changes the composition.</summary>
        public static event Action<BrowserImeUpdate> ImeUpdate;

        /// <summary>The browser always has composition events; there is nothing to probe for.</summary>
        internal static bool IsAvailable => OperatingSystem.IsBrowser();

        /// <summary>True while a text field has focus and the editable element is engaged.</summary>
        internal static bool IsEnabled { get; private set; }

        /// <summary>
        /// True while the IME holds an unfinished composition. The JS half suppresses keystrokes for
        /// the duration (they belong to the input method), so this is only the managed mirror of that
        /// state, used to decide whether a stray key still needs swallowing.
        /// </summary>
        internal static bool IsComposing { get; private set; }

        // The event kinds browser-window.js queues; kept in step with the k values it pushes.
        private const int CompositionStart = 0;
        private const int CompositionUpdate = 1;
        private const int CompositionEnd = 2;

        /// <summary>Starts an editing session. Called when a TextBox takes focus.</summary>
        public static void Enable(bool multiline, bool password)
        {
            if (!IsAvailable) return;

            // A password field must not be routed through an input method: the candidate window puts
            // the characters on screen, and the browser's IME keeps its own history of them.
            if (password)
            {
                Disable();
                return;
            }

            _ = multiline;   // The DOM offers no content-type hint on a contenteditable.

            BrowserWindow.Js.EnableTextInput();
            IsEnabled = true;
        }

        /// <summary>Ends the session. Called when focus leaves the field.</summary>
        public static void Disable()
        {
            if (!IsAvailable || !IsEnabled) return;

            BrowserWindow.Js.DisableTextInput();
            IsEnabled = false;
            IsComposing = false;
        }

        /// <summary>
        /// Where the caret is, in device pixels relative to the client area. The browser anchors the
        /// candidate window to the focused element, so this moves that element rather than sending
        /// the rectangle anywhere: position IS the mechanism here.
        /// </summary>
        public static void SetCursorRectangle(int x, int y, int width, int height)
        {
            if (!IsAvailable || !IsEnabled) return;
            BrowserWindow.Js.SetImeCaretRect(x, y, Math.Max(1, width), Math.Max(1, height));
        }

        /// <summary>
        /// Turns one queued composition event into an update. Called from BrowserWindow's drain loop,
        /// which owns the queue; the data is already on the UI thread by then.
        /// </summary>
        internal static void DispatchQueuedEvent(int kind, string data)
        {
            switch (kind)
            {
                case CompositionStart:
                    IsComposing = true;
                    // Nothing to show yet: compositionstart carries no text, and raising an empty
                    // preedit would start (and immediately end) a composition in the editor.
                    break;

                case CompositionUpdate:
                    IsComposing = true;
                    Raise(new BrowserImeUpdate(null, string.IsNullOrEmpty(data) ? null : data));
                    break;

                case CompositionEnd:
                    IsComposing = false;
                    // An empty commit is how the browser reports a cancelled composition (Escape):
                    // both fields null, which ImmComposition reads as "end it with no text".
                    Raise(new BrowserImeUpdate(string.IsNullOrEmpty(data) ? null : data, null));
                    break;
            }
        }

        private static void Raise(BrowserImeUpdate update)
        {
            try { ImeUpdate?.Invoke(update); }
            catch (InvalidOperationException) { }   // the editor went away mid-composition
        }
    }
}
