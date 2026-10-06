// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The input-method channel: zwp_text_input_v3.
//
// On Wayland a client does NOT see the keystrokes an input method consumes. When an IME (fcitx5,
// ibus, kime, ...) is engaged, the compositor hands the key events to it instead of to us, and the
// input method answers with a *preedit string* (the in-progress composition, which the client draws
// inline) and eventually a *commit string* (the final text). A client that does not bind this
// protocol therefore cannot type Japanese, Chinese or Korean at all -- the keys vanish. That was the
// state of this head before: wl_keyboard delivered only what the IME did not swallow.
//
// The protocol is double-buffered in both directions and both directions carry a serial:
//
//   * Client -> compositor: enable/disable, set_surrounding_text, set_content_type and
//     set_cursor_rectangle only stage state; `commit` applies it and bumps OUR serial.
//   * Compositor -> client: preedit_string / commit_string / delete_surrounding_text stage state;
//     `done(serial)` applies all of it at once, in a FIXED order (delete, then commit, then
//     preedit). The serial echoes the number of commits WE had sent when the input method acted, so
//     a `done` whose serial is stale describes a state we have already moved past and is dropped --
//     without that check a fast typist sees the IME's answer to a previous keystroke applied twice.
//
// This file owns the protocol; what to DO with the text is WPF's business, so the applied batch is
// raised as one managed event (see ImmComposition, which draws the preedit inline with the same
// composition adorner the Windows IMM32 path uses).
//

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using static MS.Internal.Interop.Wayland.WlProtocols;

namespace MS.Internal.Interop.Wayland
{
    /// <summary>
    /// One atomically applied batch of input-method changes, delivered on zwp_text_input_v3.done.
    /// The three parts are applied in this order: delete, then commit, then preedit.
    /// </summary>
    internal readonly struct WaylandImeUpdate
    {
        /// <summary>Characters to remove before/after the cursor first, in UTF-8 BYTES (the
        /// protocol's unit), relative to the surrounding text last reported.</summary>
        public uint DeleteBeforeBytes { get; }
        public uint DeleteAfterBytes { get; }

        /// <summary>Final text to insert, or null if the input method committed nothing.</summary>
        public string? CommitText { get; }

        /// <summary>The in-progress composition to display, or null to end/clear it.</summary>
        public string? PreeditText { get; }

        /// <summary>Selection within the preedit, in UTF-16 offsets (already converted from the
        /// protocol's byte offsets), or -1 when the input method asks for a hidden cursor.</summary>
        public int PreeditCursorBegin { get; }
        public int PreeditCursorEnd { get; }

        public WaylandImeUpdate(uint deleteBefore, uint deleteAfter, string? commit,
                                string? preedit, int cursorBegin, int cursorEnd)
        {
            DeleteBeforeBytes = deleteBefore;
            DeleteAfterBytes = deleteAfter;
            CommitText = commit;
            PreeditText = preedit;
            PreeditCursorBegin = cursorBegin;
            PreeditCursorEnd = cursorEnd;
        }

        public bool IsEmpty => DeleteBeforeBytes == 0 && DeleteAfterBytes == 0
                               && CommitText == null && PreeditText == null;
    }

    [SupportedOSPlatform("linux")]
    internal static unsafe class WaylandTextInput
    {
        /// <summary>Raised on the Wayland thread when the input method applies a batch (done).</summary>
        public static event Action<WaylandImeUpdate>? ImeUpdate;

        /// <summary>Raised when the input method enters or leaves a surface, i.e. when typing through
        /// an IME becomes possible at all for that window.</summary>
        public static event Action<IntPtr, bool>? FocusChanged;

        private static IntPtr s_manager;
        private static IntPtr s_textInput;
        private static IntPtr* s_listener;

        /// <summary>Our commit count: the serial the compositor echoes back in `done`.</summary>
        private static uint s_serial;

        /// <summary>The surface the input method is currently attached to (0 = none).</summary>
        internal static IntPtr FocusSurface { get; private set; }

        /// <summary>True once the compositor has advertised an input-method channel.</summary>
        internal static bool IsAvailable => s_textInput != IntPtr.Zero;

        /// <summary>True while a text field has asked to receive input-method events.</summary>
        internal static bool IsEnabled { get; private set; }

        /// <summary>
        /// True while the input method is showing a preedit. Some input methods forward the keys
        /// they are composing with to the client as well, so the keyboard path suppresses its own
        /// text while this is set -- otherwise each keystroke of a Japanese word is inserted twice,
        /// once as the raw kana and once as the input method's committed conversion.
        /// </summary>
        internal static bool IsComposing { get; private set; }

        // Pending state from the compositor, accumulated until `done` applies it.
        private static uint s_pendingDeleteBefore, s_pendingDeleteAfter;
        private static string? s_pendingCommit;
        private static string? s_pendingPreedit;
        private static int s_pendingCursorBegin = -1, s_pendingCursorEnd = -1;

        // The last state we sent, so redundant requests (which each cost a round trip and can make
        // an input method re-show its candidate window) are dropped.
        private static string? s_lastSurrounding;
        private static int s_lastCursor = -1, s_lastAnchor = -1;
        private static int s_lastRectX, s_lastRectY, s_lastRectW, s_lastRectH;
        private static uint s_lastHint = uint.MaxValue, s_lastPurpose = uint.MaxValue;

        // ---- Setup ---------------------------------------------------------------------------

        /// <summary>Records the zwp_text_input_manager_v3 global; called from the registry handler.</summary>
        public static void AttachManager(IntPtr manager)
        {
            s_manager = manager;
            TryCreate();
        }

        /// <summary>Called once the seat exists. Either half may arrive first, so both call this.</summary>
        public static void AttachSeat(IntPtr seat)
        {
            TryCreate();
        }

        private static void TryCreate()
        {
            if (s_textInput != IntPtr.Zero) return;
            if (s_manager == IntPtr.Zero || WaylandDisplay.Seat == IntPtr.Zero) return;

            try
            {
                s_textInput = Wl.Construct(s_manager, ZWP_TEXT_INPUT_MANAGER_V3_GET_TEXT_INPUT,
                                           "zwp_text_input_v3", 1,
                                           WlArgument.NewId(), WlArgument.Ptr(WaylandDisplay.Seat));
                if (s_textInput == IntPtr.Zero) return;

                s_listener = Wl.Vtable(
                    (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, void>)&OnEnter,
                    (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, void>)&OnLeave,
                    (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, int, int, void>)&OnPreeditString,
                    (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, void>)&OnCommitString,
                    (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, uint, uint, void>)&OnDeleteSurroundingText,
                    (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, uint, void>)&OnDone);
                Wl.wl_proxy_add_listener(s_textInput, s_listener, IntPtr.Zero);

                WaylandDisplay.LogSink?.Invoke("zwp_text_input_v3 bound; IME input available.");
            }
            catch (Exception e)
            {
                WaylandDisplay.LogSink?.Invoke("could not create zwp_text_input_v3: " + e.Message);
                s_textInput = IntPtr.Zero;
            }
        }

        // ---- Client -> compositor --------------------------------------------------------------

        /// <summary>
        /// Tells the input method this window is now editing text, so it may start composing.
        /// `enable` RESETS all staged state on the compositor side, so everything the input method
        /// needs (content type, cursor rectangle, surrounding text) has to be sent again after it --
        /// callers do that by clearing our "last sent" cache here.
        /// </summary>
        public static void Enable(bool multiline, bool password)
        {
            if (s_textInput == IntPtr.Zero || FocusSurface == IntPtr.Zero) return;

            try
            {
                Wl.Request(s_textInput, ZWP_TEXT_INPUT_V3_ENABLE);
                IsEnabled = true;

                // Everything staged is gone; forget what we think the compositor knows.
                s_lastSurrounding = null;
                s_lastCursor = s_lastAnchor = -1;
                s_lastRectX = s_lastRectY = s_lastRectW = s_lastRectH = 0;
                s_lastHint = s_lastPurpose = uint.MaxValue;

                uint hint = ZWP_TEXT_INPUT_V3_CONTENT_HINT_NONE;
                if (multiline) hint |= ZWP_TEXT_INPUT_V3_CONTENT_HINT_MULTILINE;
                if (password) hint |= ZWP_TEXT_INPUT_V3_CONTENT_HINT_SENSITIVE_DATA
                                      | ZWP_TEXT_INPUT_V3_CONTENT_HINT_HIDDEN_TEXT;

                uint purpose = password
                    ? ZWP_TEXT_INPUT_V3_CONTENT_PURPOSE_PASSWORD
                    : ZWP_TEXT_INPUT_V3_CONTENT_PURPOSE_NORMAL;

                Wl.Request(s_textInput, ZWP_TEXT_INPUT_V3_SET_CONTENT_TYPE,
                           WlArgument.UInt(hint), WlArgument.UInt(purpose));
                s_lastHint = hint;
                s_lastPurpose = purpose;

                Commit();
                WaylandDisplay.LogSink?.Invoke($"text-input enabled (multiline={multiline} password={password})");
            }
            catch { }
        }

        /// <summary>Tells the input method no text field is active; ends any composition.</summary>
        public static void Disable()
        {
            if (s_textInput == IntPtr.Zero || !IsEnabled) return;

            try
            {
                Wl.Request(s_textInput, ZWP_TEXT_INPUT_V3_DISABLE);
                IsEnabled = false;
                IsComposing = false;
                Commit();
                WaylandDisplay.LogSink?.Invoke("text-input disabled");
            }
            catch { }
        }

        /// <summary>
        /// Where the caret is on screen, in SURFACE-LOCAL logical coordinates -- the anchor the input
        /// method positions its candidate window against. Without it the candidate list lands in the
        /// window's top-left corner instead of under the text being typed.
        /// </summary>
        public static void SetCursorRectangle(int x, int y, int width, int height)
        {
            if (s_textInput == IntPtr.Zero || !IsEnabled) return;
            if (x == s_lastRectX && y == s_lastRectY && width == s_lastRectW && height == s_lastRectH) return;

            try
            {
                Wl.Request(s_textInput, ZWP_TEXT_INPUT_V3_SET_CURSOR_RECTANGLE,
                           WlArgument.Int(x), WlArgument.Int(y),
                           WlArgument.Int(Math.Max(0, width)), WlArgument.Int(Math.Max(0, height)));
                s_lastRectX = x; s_lastRectY = y; s_lastRectW = width; s_lastRectH = height;
                Commit();
            }
            catch { }
        }

        /// <summary>
        /// The caret rectangle for a WPF window, given in DEVICE pixels relative to that window's
        /// client area -- the units the framework has -- and converted here to the surface-local
        /// logical units the protocol wants.
        ///
        /// Two conversions are needed. A popup (a combo box drop-down, a context menu) is composited
        /// INTO its owner's surface on this head rather than presenting its own, so the input method
        /// only ever entered the owner's surface: coordinates from a popup have to be shifted by the
        /// popup's origin within that owner. Then device pixels are divided by the surface's backing
        /// scale, because Wayland speaks logical units throughout.
        /// </summary>
        public static void SetCursorRectangleForWindow(IntPtr window, int x, int y, int width, int height)
        {
            if (s_textInput == IntPtr.Zero || !IsEnabled) return;

            IntPtr surface = FocusSurface;
            if (surface == IntPtr.Zero) return;

            if (window != surface && WaylandDisplay.WindowOriginQuery != null)
            {
                WaylandDisplay.WindowOriginQuery(window, out int originX, out int originY);
                x += originX;
                y += originY;
            }

            double scale = WaylandDisplay.ScaleForSurface(surface);
            if (scale <= 0) scale = 1.0;

            SetCursorRectangle(
                (int)Math.Round(x / scale), (int)Math.Round(y / scale),
                (int)Math.Round(width / scale), (int)Math.Round(height / scale));
        }

        /// <summary>
        /// The text around the caret, which input methods use for context: predictive conversion,
        /// reconversion, and deciding what delete_surrounding_text should remove. The protocol caps
        /// the string at 4000 bytes, so a long document is trimmed to a window around the cursor --
        /// on a UTF-16 boundary, and then on a UTF-8 one, since the offsets travel as byte counts.
        /// </summary>
        public static void SetSurroundingText(string text, int cursor, int anchor)
        {
            if (s_textInput == IntPtr.Zero || !IsEnabled) return;

            text ??= string.Empty;
            cursor = Math.Clamp(cursor, 0, text.Length);
            anchor = Math.Clamp(anchor, 0, text.Length);

            (string window, int windowCursor, int windowAnchor) = Trim(text, cursor, anchor);

            if (windowCursor == s_lastCursor && windowAnchor == s_lastAnchor &&
                string.Equals(window, s_lastSurrounding, StringComparison.Ordinal))
            {
                return;
            }

            try
            {
                // The protocol's offsets are byte offsets into the UTF-8 form, not character indices.
                int cursorBytes = Encoding.UTF8.GetByteCount(window.AsSpan(0, windowCursor));
                int anchorBytes = Encoding.UTF8.GetByteCount(window.AsSpan(0, windowAnchor));

                IntPtr utf8 = Wl.Utf8(window);
                try
                {
                    Wl.Request(s_textInput, ZWP_TEXT_INPUT_V3_SET_SURROUNDING_TEXT,
                               WlArgument.Ptr(utf8), WlArgument.Int(cursorBytes), WlArgument.Int(anchorBytes));
                }
                finally
                {
                    NativeMemory.Free((void*)utf8);
                }

                Wl.Request(s_textInput, ZWP_TEXT_INPUT_V3_SET_TEXT_CHANGE_CAUSE,
                           WlArgument.UInt(ZWP_TEXT_INPUT_V3_CHANGE_CAUSE_OTHER));

                s_lastSurrounding = window;
                s_lastCursor = windowCursor;
                s_lastAnchor = windowAnchor;
                Commit();
            }
            catch { }
        }

        /// <summary>
        /// Cuts <paramref name="text"/> down to at most the protocol's byte budget, keeping the
        /// cursor centred in what remains. Returns the window and the cursor/anchor within it.
        /// </summary>
        private static (string text, int cursor, int anchor) Trim(string text, int cursor, int anchor)
        {
            if (Encoding.UTF8.GetByteCount(text) <= ZWP_TEXT_INPUT_V3_MAX_SURROUNDING_BYTES)
                return (text, cursor, anchor);

            // A conservative character budget: even all-4-byte codepoints fit in the byte cap, so
            // the result never has to be re-trimmed after the UTF-8 count.
            const int Half = ZWP_TEXT_INPUT_V3_MAX_SURROUNDING_BYTES / 8;

            int start = Math.Max(0, Math.Min(cursor, anchor) - Half);
            int end = Math.Min(text.Length, Math.Max(cursor, anchor) + Half);

            // Never split a surrogate pair: the halves are not valid UTF-16 on their own.
            if (start > 0 && char.IsLowSurrogate(text[start])) start--;
            if (end < text.Length && char.IsLowSurrogate(text[end])) end++;

            return (text.Substring(start, end - start), cursor - start, anchor - start);
        }

        /// <summary>Applies everything staged since the last commit, and bumps our serial.</summary>
        private static void Commit()
        {
            Wl.Request(s_textInput, ZWP_TEXT_INPUT_V3_COMMIT);
            s_serial++;
        }

        // ---- Compositor -> client --------------------------------------------------------------
        //
        // Every handler swallows: an exception unwinding into libwayland is undefined behaviour.

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void OnEnter(IntPtr data, IntPtr textInput, IntPtr surface)
        {
            try
            {
                FocusSurface = surface;
                WaylandDisplay.LogSink?.Invoke("text-input entered surface");
                FocusChanged?.Invoke(surface, true);
            }
            catch { }
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void OnLeave(IntPtr data, IntPtr textInput, IntPtr surface)
        {
            try
            {
                // Leaving implicitly disables the object on the compositor side.
                IsEnabled = false;
                IsComposing = false;
                if (FocusSurface == surface) FocusSurface = IntPtr.Zero;
                ResetPending();
                FocusChanged?.Invoke(surface, false);
            }
            catch { }
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void OnPreeditString(IntPtr data, IntPtr textInput, IntPtr textUtf8, int cursorBegin, int cursorEnd)
        {
            try
            {
                // A null string means "no preedit", which is how a composition ends; it is NOT the
                // same as the empty string, so the distinction is preserved.
                s_pendingPreedit = textUtf8 == IntPtr.Zero ? null : Wl.FromUtf8(textUtf8);

                // The offsets are UTF-8 byte offsets into the preedit; -1 asks for a hidden cursor.
                s_pendingCursorBegin = ByteOffsetToCharIndex(s_pendingPreedit, cursorBegin);
                s_pendingCursorEnd = ByteOffsetToCharIndex(s_pendingPreedit, cursorEnd);
            }
            catch { }
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void OnCommitString(IntPtr data, IntPtr textInput, IntPtr textUtf8)
        {
            try { s_pendingCommit = textUtf8 == IntPtr.Zero ? null : Wl.FromUtf8(textUtf8); }
            catch { }
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void OnDeleteSurroundingText(IntPtr data, IntPtr textInput, uint beforeLength, uint afterLength)
        {
            try
            {
                s_pendingDeleteBefore = beforeLength;
                s_pendingDeleteAfter = afterLength;
            }
            catch { }
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void OnDone(IntPtr data, IntPtr textInput, uint serial)
        {
            try
            {
                // The serial is the number of commits we had sent when the input method decided on
                // this batch. If we have committed since (a caret move, a resize), the batch answers
                // a state that no longer exists: per the protocol the client discards the preedit
                // and delete, but STILL applies the commit string -- text the user typed must not be
                // dropped just because the field moved underneath it.
                bool stale = serial != s_serial;

                var update = stale
                    ? new WaylandImeUpdate(0, 0, s_pendingCommit, null, -1, -1)
                    : new WaylandImeUpdate(s_pendingDeleteBefore, s_pendingDeleteAfter,
                                           s_pendingCommit, s_pendingPreedit,
                                           s_pendingCursorBegin, s_pendingCursorEnd);

                IsComposing = !string.IsNullOrEmpty(update.PreeditText);

                ResetPending();

                if (!update.IsEmpty || !stale)
                    ImeUpdate?.Invoke(update);
            }
            catch { }
        }

        private static void ResetPending()
        {
            s_pendingDeleteBefore = s_pendingDeleteAfter = 0;
            s_pendingCommit = null;
            s_pendingPreedit = null;
            s_pendingCursorBegin = s_pendingCursorEnd = -1;
        }

        /// <summary>
        /// Converts a UTF-8 byte offset into <paramref name="text"/> to a UTF-16 index. Returns -1
        /// for the protocol's "hidden cursor" sentinel and for an offset that does not land on a
        /// character boundary (a malformed input method should not throw inside a Wayland callback).
        /// </summary>
        /// <summary>Test seam for <see cref="ByteOffsetToCharIndex"/>, which is otherwise only
        /// reachable through a live compositor sending a preedit.</summary>
        internal static int ByteOffsetToCharIndexForTest(string? text, int byteOffset)
            => ByteOffsetToCharIndex(text, byteOffset);

        private static int ByteOffsetToCharIndex(string? text, int byteOffset)
        {
            if (text == null || byteOffset < 0) return -1;
            if (byteOffset == 0) return 0;

            int bytes = 0;
            for (int i = 0; i < text.Length; )
            {
                if (bytes == byteOffset) return i;
                if (bytes > byteOffset) return -1;

                int charLen = char.IsHighSurrogate(text[i]) && i + 1 < text.Length ? 2 : 1;
                bytes += Encoding.UTF8.GetByteCount(text.AsSpan(i, charLen));
                i += charLen;
            }
            return bytes == byteOffset ? text.Length : -1;
        }
    }
}
