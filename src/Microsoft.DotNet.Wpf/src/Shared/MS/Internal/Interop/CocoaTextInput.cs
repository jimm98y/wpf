// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The macOS input-method channel: NSTextInputContext + a client that conforms to NSTextInputClient.
//
// Typing Japanese, Chinese or Korean does not produce characters. It produces a CONVERSATION: the
// input method takes the keystrokes, shows an in-progress composition ("marked text") that the app
// draws inline, offers candidates, and only at the end hands over the final text. AppKit conducts
// that conversation through NSTextInputClient, and until this file existed the Cocoa key path went
// straight from UCKeyTranslate to a character, which can express dead keys (Option+e, e -> "e-acute")
// and nothing more -- so Kotoeri produced no text at all.
//
// The client here is a plain NSObject rather than the window's content view. NSTextInputContext only
// requires an object conforming to the protocol, and it can be driven directly (-activate, -handleEvent:)
// instead of through the first responder, which keeps the whole feature out of the way of the content
// view -- that view is the app's HWND stand-in AND the CAMetalLayer host, and it is not worth
// re-parenting the render surface to receive key events we already intercept in the pump.
//
// Only the TRANSPORT lives here. Everything that turns a composition into WPF text -- the adorner,
// the TextInputStart/Update events, the document edit -- is ImmComposition's, exactly as on Windows
// and Linux; this file's output is a CocoaImeUpdate and nothing else.
//

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace MS.Internal.Interop
{
    /// <summary>
    /// One batch of input-method changes. AppKit delivers the parts as separate messages
    /// (-setMarkedText:.../-insertText:...) rather than as one atomic update the way Wayland does,
    /// so each message produces one of these.
    /// </summary>
    internal readonly struct CocoaImeUpdate
    {
        /// <summary>Final text to insert, or null if the input method committed nothing.</summary>
        public string CommitText { get; }

        /// <summary>The in-progress composition to display, or null to end/clear it.</summary>
        public string PreeditText { get; }

        /// <summary>The input method's selection within the preedit, in UTF-16 offsets, or -1 for
        /// none. This is the clause being converted, which draws with the solid underline.</summary>
        public int PreeditCursorBegin { get; }
        public int PreeditCursorEnd { get; }

        /// <summary>Characters to remove around the caret before applying the rest, in UTF-16 units
        /// -- AppKit's own unit, unlike Wayland's UTF-8 bytes, so no conversion is needed here.
        /// Non-zero only when the input method asked to replace text it did not itself mark.</summary>
        public int ReplaceBeforeChars { get; }
        public int ReplaceAfterChars { get; }

        public CocoaImeUpdate(string commit, string preedit, int cursorBegin, int cursorEnd,
                              int replaceBefore, int replaceAfter)
        {
            CommitText = commit;
            PreeditText = preedit;
            PreeditCursorBegin = cursorBegin;
            PreeditCursorEnd = cursorEnd;
            ReplaceBeforeChars = replaceBefore;
            ReplaceAfterChars = replaceAfter;
        }
    }

    [SupportedOSPlatform("macos")]
    internal static class CocoaTextInput
    {
        /// <summary>Raised on the pump (UI) thread when the input method changes the composition.</summary>
        public static event Action<CocoaImeUpdate> ImeUpdate;

        /// <summary>True once AppKit is loaded and the input-method classes are reachable.</summary>
        internal static bool IsAvailable
            => OperatingSystem.IsMacOS() && LoadAppKit() && objc_getClass("NSTextInputContext") != IntPtr.Zero;

        /// <summary>
        /// NSTextInputContext lives in AppKit, which a WPF app has loaded long before any TextBox
        /// takes focus -- but this type is also reachable before the first window exists, and asking
        /// the runtime for a class from an unloaded framework simply answers nil. dlopen is
        /// idempotent and cached by dyld, so the repeat cost is a pointer comparison.
        /// </summary>
        private static bool LoadAppKit()
        {
            if (s_appKitLoaded) return true;

            const int RTLD_NOW = 2;
            dlopen("/System/Library/Frameworks/Foundation.framework/Foundation", RTLD_NOW);
            s_appKitLoaded = dlopen("/System/Library/Frameworks/AppKit.framework/AppKit", RTLD_NOW) != IntPtr.Zero;
            return s_appKitLoaded;
        }

        private static bool s_appKitLoaded;

        /// <summary>
        /// The NSTextInputClient instance, created on demand, for tests that drive the protocol
        /// directly rather than through a real input method. Live input arrives via -handleEvent:,
        /// which needs a running NSApplication and an engaged IME; the client's own answers need
        /// neither, and they are where the bridge can go wrong -- a mis-declared type encoding or a
        /// struct returned through the wrong ABI shows up here and nowhere else.
        /// </summary>
        internal static IntPtr ClientForTest => EnsureContext() ? _client : IntPtr.Zero;

        /// <summary>True while a text field has focus and the input context is activated.</summary>
        internal static bool IsEnabled { get; private set; }

        /// <summary>
        /// True while the input method holds an unfinished composition. The keyboard belongs to it
        /// for the duration: its keystrokes are its own, and passing them to WPF as well would move
        /// the caret out from under a composition the user is still editing.
        /// </summary>
        internal static bool IsComposing => _markedText != null;

        // ---- state the client answers questions about ------------------------------------------

        // The composition as the input method last set it, and its selection.
        private static string _markedText;
        private static ulong _markedSelectionStart;
        private static ulong _markedSelectionLength;

        // The text around the caret, as ImmComposition last reported it. Input methods ask for this
        // to convert in context (Japanese predictive conversion leans on it heavily) and to decide
        // what a replacement range refers to.
        private static string _surroundingText = string.Empty;
        private static ulong _selectionStart;
        private static ulong _selectionLength;

        // The caret's rectangle in screen points, bottom-left origin -- the coordinate space
        // -firstRectForCharacterRange: is defined in, so the candidate window lands under the text
        // being typed rather than at the window's origin.
        private static NSRect _caretRectScreen = new NSRect { x = 0, y = 0, width = 1, height = 16 };

        private static IntPtr _client;      // WpfTextInputClient instance (retained for the process)
        private static IntPtr _context;     // NSTextInputContext bound to it

        /// <summary>
        /// Starts an editing session: the input method may now compose into this app. Called when a
        /// TextBox takes focus.
        /// </summary>
        public static void Enable(bool multiline, bool password)
        {
            if (!IsAvailable) return;
            if (!EnsureContext()) return;

            // A password field must not go through an input method: the candidate window would put
            // the characters on screen, and predictive conversion would learn them. AppKit's own
            // secure fields disable the context outright, so do the same rather than merely hinting.
            if (password)
            {
                Disable();
                return;
            }

            _ = multiline;  // AppKit takes no content-type hint; the protocol on Linux does.

            if (!IsEnabled)
            {
                SendVoid(_context, Sel("activate"));
                IsEnabled = true;
            }
        }

        /// <summary>Ends the editing session. Called when focus leaves the field.</summary>
        public static void Disable()
        {
            if (_context == IntPtr.Zero || !IsEnabled) return;

            // Drop any half-finished composition first: deactivating with marked text outstanding
            // leaves the input method believing it still owns text that is about to lose its editor.
            if (IsComposing)
            {
                SendVoid(_context, Sel("discardMarkedText"));
                _markedText = null;
            }

            SendVoid(_context, Sel("deactivate"));
            IsEnabled = false;
        }

        /// <summary>
        /// Offers a key event to the input method. Returns true when it consumed the event, in which
        /// case the keystroke belongs to the composition and must not also reach WPF as a key.
        /// </summary>
        internal static bool HandleKeyEvent(IntPtr evt)
        {
            if (!IsEnabled || _context == IntPtr.Zero || evt == IntPtr.Zero) return false;

            // -handleEvent: calls back into the client (insertText:/setMarkedText:) synchronously,
            // so by the time this returns the update for this keystroke has already been raised.
            int before = _updateCount;
            bool consumed = SendBoolPtr(_context, Sel("handleEvent:"), evt);

            // "Consumed" on its own is not enough to swallow the keystroke. AppKit answers YES to
            // plenty of events it merely routed -- an editing command it turned into
            // -doCommandBySelector:, which this client deliberately ignores -- and it would answer
            // YES to everything if the context were ever activated without a working input session.
            // Either way the user pressed a key and nothing came back, so the keystroke is handed on
            // to the layout translation instead and typing degrades to exactly what it was before
            // this file existed, rather than to nothing at all.
            return consumed && (_updateCount != before || IsComposing);
        }

        // Bumped on every update raised; only ever compared against itself, so wrapping is harmless.
        private static int _updateCount;

        /// <summary>
        /// Where the caret is, in device pixels relative to the client area of the window that owns
        /// <paramref name="view"/>. Converted here to the screen points AppKit asks for, so the
        /// caller can keep working in the same units the IMM32 path uses.
        /// </summary>
        public static void SetCursorRectangleForWindow(IntPtr view, int x, int y, int width, int height)
        {
            if (!IsAvailable) return;

            if (!CocoaWindow.TryConvertClientPixelsToScreenPoints(
                    view, x, y, width, Math.Max(height, 1),
                    out double sx, out double sy, out double sw, out double sh))
            {
                return;
            }

            _caretRectScreen = new NSRect { x = sx, y = sy, width = sw, height = sh };

            // The candidate window is positioned when it opens; tell the input method to re-ask if
            // it is already up, otherwise it stays where the caret used to be.
            if (IsEnabled && _context != IntPtr.Zero)
            {
                SendVoid(_context, Sel("invalidateCharacterCoordinates"));
            }
        }

        /// <summary>
        /// The text on either side of the caret, with the selection's offsets within it. Reported on
        /// every composition change so conversion has context to work with.
        /// </summary>
        public static void SetSurroundingText(string text, int cursor, int anchor)
        {
            _surroundingText = text ?? string.Empty;

            int start = Math.Clamp(Math.Min(cursor, anchor), 0, _surroundingText.Length);
            int end = Math.Clamp(Math.Max(cursor, anchor), 0, _surroundingText.Length);

            _selectionStart = (ulong)start;
            _selectionLength = (ulong)(end - start);
        }

        // ---- the NSTextInputClient implementation ----------------------------------------------

        // The IMPs are managed delegates; the fields keep them (and the thunks behind them) alive
        // for the lifetime of the process, since Objective-C holds only the raw function pointers.
        private delegate void InsertTextDelegate(IntPtr self, IntPtr cmd, IntPtr str, NSRange replacement);
        private delegate void SetMarkedTextDelegate(IntPtr self, IntPtr cmd, IntPtr str, NSRange selected, NSRange replacement);
        private delegate void VoidDelegate(IntPtr self, IntPtr cmd);
        private delegate void SelectorDelegate(IntPtr self, IntPtr cmd, IntPtr selector);
        private delegate NSRange RangeDelegate(IntPtr self, IntPtr cmd);
        private delegate byte BoolDelegate(IntPtr self, IntPtr cmd);
        private delegate IntPtr AttributedSubstringDelegate(IntPtr self, IntPtr cmd, NSRange range, IntPtr actualRange);
        private delegate IntPtr ArrayDelegate(IntPtr self, IntPtr cmd);
        private delegate NSRect FirstRectDelegate(IntPtr self, IntPtr cmd, NSRange range, IntPtr actualRange);
        private delegate ulong CharacterIndexDelegate(IntPtr self, IntPtr cmd, NSPoint point);

        private static InsertTextDelegate s_insertText;
        private static SetMarkedTextDelegate s_setMarkedText;
        private static VoidDelegate s_unmarkText;
        private static SelectorDelegate s_doCommandBySelector;
        private static RangeDelegate s_selectedRange;
        private static RangeDelegate s_markedRange;
        private static BoolDelegate s_hasMarkedText;
        private static AttributedSubstringDelegate s_attributedSubstring;
        private static ArrayDelegate s_validAttributes;
        private static FirstRectDelegate s_firstRect;
        private static CharacterIndexDelegate s_characterIndex;

        private static bool EnsureContext()
        {
            if (_context != IntPtr.Zero) return true;
            if (!OperatingSystem.IsMacOS() || !LoadAppKit()) return false;

            IntPtr nsobject = objc_getClass("NSObject");
            IntPtr contextClass = objc_getClass("NSTextInputContext");
            if (nsobject == IntPtr.Zero || contextClass == IntPtr.Zero) return false;

            IntPtr cls = objc_allocateClassPair(nsobject, "WpfTextInputClient", UIntPtr.Zero);
            if (cls == IntPtr.Zero)
            {
                // Already registered (a second Dispatcher in the same process): reuse it.
                cls = objc_getClass("WpfTextInputClient");
                if (cls == IntPtr.Zero) return false;
            }
            else
            {
                // Conformance is not implied by implementing the methods -- NSTextInputContext asks
                // -conformsToProtocol: and refuses a client that answers NO.
                IntPtr protocol = objc_getProtocol("NSTextInputClient");
                if (protocol != IntPtr.Zero) class_addProtocol(cls, protocol);

                s_insertText = InsertText;
                s_setMarkedText = SetMarkedText;
                s_unmarkText = UnmarkText;
                s_doCommandBySelector = DoCommandBySelector;
                s_selectedRange = SelectedRange;
                s_markedRange = MarkedRange;
                s_hasMarkedText = HasMarkedText;
                s_attributedSubstring = AttributedSubstringForProposedRange;
                s_validAttributes = ValidAttributesForMarkedText;
                s_firstRect = FirstRectForCharacterRange;
                s_characterIndex = CharacterIndexForPoint;

                // Objective-C type encodings: NSRange is {_NSRange=QQ}, NSRect is a CGRect of two
                // CGPoint/CGSize pairs of doubles, and NSRangePointer is a pointer to the former.
                // BOOL is _Bool ("B") on Apple silicon; the IMP returns a single byte either way.
                const string Range = "{_NSRange=QQ}";
                const string RangePtr = "^{_NSRange=QQ}";
                const string Rect = "{CGRect={CGPoint=dd}{CGSize=dd}}";

                AddMethod(cls, "insertText:replacementRange:", s_insertText, "v@:@" + Range);
                AddMethod(cls, "setMarkedText:selectedRange:replacementRange:", s_setMarkedText, "v@:@" + Range + Range);
                AddMethod(cls, "unmarkText", s_unmarkText, "v@:");
                AddMethod(cls, "doCommandBySelector:", s_doCommandBySelector, "v@::");
                AddMethod(cls, "selectedRange", s_selectedRange, Range + "@:");
                AddMethod(cls, "markedRange", s_markedRange, Range + "@:");
                AddMethod(cls, "hasMarkedText", s_hasMarkedText, "B@:");
                AddMethod(cls, "attributedSubstringForProposedRange:actualRange:", s_attributedSubstring, "@@:" + Range + RangePtr);
                AddMethod(cls, "validAttributesForMarkedText", s_validAttributes, "@@:");
                AddMethod(cls, "firstRectForCharacterRange:actualRange:", s_firstRect, Rect + "@:" + Range + RangePtr);
                AddMethod(cls, "characterIndexForPoint:", s_characterIndex, "Q@:{CGPoint=dd}");

                objc_registerClassPair(cls);
            }

            _client = Send(Send(cls, Sel("alloc")), Sel("init"));
            if (_client == IntPtr.Zero) return false;

            _context = SendPtrRet(Send(contextClass, Sel("alloc")), Sel("initWithClient:"), _client);
            return _context != IntPtr.Zero;
        }

        private static void AddMethod(IntPtr cls, string selector, Delegate impl, string types)
            => class_addMethod(cls, Sel(selector), Marshal.GetFunctionPointerForDelegate(impl), types);

        // The input method committed text: the composition (if any) is over and this is the result.
        private static void InsertText(IntPtr self, IntPtr cmd, IntPtr str, NSRange replacement)
        {
            string text = ReadString(str);
            bool wasComposing = _markedText != null;
            _markedText = null;

            if (string.IsNullOrEmpty(text))
            {
                // Committing nothing still ends the composition (Escape, or the last kana deleted).
                if (wasComposing) Raise(new CocoaImeUpdate(null, null, -1, -1, 0, 0));
                return;
            }

            ResolveReplacement(replacement, out int before, out int after);
            Raise(new CocoaImeUpdate(text, null, -1, -1, before, after));
        }

        // The composition changed: draw this text inline, underlined, with the given clause selected.
        private static void SetMarkedText(IntPtr self, IntPtr cmd, IntPtr str, NSRange selected, NSRange replacement)
        {
            string text = ReadString(str);

            if (string.IsNullOrEmpty(text))
            {
                // An empty marked string is how AppKit cancels a composition.
                bool wasComposing = _markedText != null;
                _markedText = null;
                if (wasComposing) Raise(new CocoaImeUpdate(null, null, -1, -1, 0, 0));
                return;
            }

            _markedText = text;
            _markedSelectionStart = selected.location;
            _markedSelectionLength = selected.length;

            int begin = selected.location > (ulong)text.Length ? -1 : (int)selected.location;
            int end = begin < 0 ? -1 : Math.Min(begin + (int)selected.length, text.Length);

            ResolveReplacement(replacement, out int before, out int after);
            Raise(new CocoaImeUpdate(null, text, begin, end, before, after));
        }

        // The input method abandoned the composition without committing.
        private static void UnmarkText(IntPtr self, IntPtr cmd)
        {
            if (_markedText == null) return;
            _markedText = null;
            Raise(new CocoaImeUpdate(null, null, -1, -1, 0, 0));
        }

        // Editing commands (Return, arrows, delete) that the input method chose not to consume.
        // WPF has already seen the key event itself, so acting on these too would apply them twice.
        private static void DoCommandBySelector(IntPtr self, IntPtr cmd, IntPtr selector) { }

        private static NSRange SelectedRange(IntPtr self, IntPtr cmd)
            => new NSRange { location = _selectionStart, length = _selectionLength };

        // Where the composition sits in the document. NSNotFound when there is none -- an input
        // method that gets a real range back for a composition it did not start will try to edit it.
        private static NSRange MarkedRange(IntPtr self, IntPtr cmd)
            => _markedText == null
                ? new NSRange { location = NSNotFound, length = 0 }
                : new NSRange { location = _selectionStart, length = (ulong)_markedText.Length };

        private static byte HasMarkedText(IntPtr self, IntPtr cmd) => (byte)(_markedText != null ? 1 : 0);

        // Context for conversion: the requested slice of the text around the caret. Returning nil is
        // legal and costs Japanese reconversion, so answer it from what was last reported.
        private static IntPtr AttributedSubstringForProposedRange(IntPtr self, IntPtr cmd, NSRange range, IntPtr actualRange)
        {
            string text = _surroundingText;
            if (string.IsNullOrEmpty(text) || range.location == NSNotFound) return IntPtr.Zero;

            int start = (int)Math.Min(range.location, (ulong)text.Length);
            int length = (int)Math.Min(range.length, (ulong)(text.Length - start));
            if (length <= 0) return IntPtr.Zero;

            if (actualRange != IntPtr.Zero)
            {
                // Say what was actually returned; a clamped range the caller does not know about
                // makes the input method count from the wrong place.
                Marshal.WriteInt64(actualRange, 0, start);
                Marshal.WriteInt64(actualRange, 8, length);
            }

            IntPtr nsString = MakeNSString(text.Substring(start, length));
            if (nsString == IntPtr.Zero) return IntPtr.Zero;

            IntPtr attributed = Send(objc_getClass("NSAttributedString"), Sel("alloc"));
            attributed = SendPtrRet(attributed, Sel("initWithString:"), nsString);

            // Returned to Objective-C, which expects an autoreleased object from a method whose name
            // does not begin with alloc/new/copy/mutableCopy.
            return attributed == IntPtr.Zero ? IntPtr.Zero : Send(attributed, Sel("autorelease"));
        }

        // No styled-text attributes are honoured on the composition: the underline comes from WPF's
        // own CompositionAdorner, driven by the per-character attributes ImmComposition builds.
        private static IntPtr ValidAttributesForMarkedText(IntPtr self, IntPtr cmd)
            => Send(objc_getClass("NSArray"), Sel("array"));

        private static NSRect FirstRectForCharacterRange(IntPtr self, IntPtr cmd, NSRange range, IntPtr actualRange)
        {
            if (actualRange != IntPtr.Zero)
            {
                Marshal.WriteInt64(actualRange, 0, (long)range.location);
                Marshal.WriteInt64(actualRange, 8, 0);
            }
            return _caretRectScreen;
        }

        // Used for mouse interaction with a composition (clicking into candidate text). WPF routes
        // mouse input itself, so there is no index to give.
        private static ulong CharacterIndexForPoint(IntPtr self, IntPtr cmd, NSPoint point) => NSNotFound;

        /// <summary>
        /// Turns AppKit's replacement range into "characters before/after the caret to remove".
        /// NSNotFound means "replace the marked text, or the selection" -- both of which the editor
        /// already replaces on its own, so that case removes nothing extra.
        /// </summary>
        private static void ResolveReplacement(NSRange replacement, out int before, out int after)
        {
            before = 0;
            after = 0;

            if (replacement.location == NSNotFound || _surroundingText == null) return;

            long start = (long)replacement.location;
            long end = start + (long)replacement.length;
            long caret = (long)_selectionStart;
            long caretEnd = caret + (long)_selectionLength;

            if (start < caret) before = (int)Math.Min(caret - start, caret);
            if (end > caretEnd) after = (int)Math.Min(end - caretEnd, _surroundingText.Length - caretEnd);
        }

        private static void Raise(CocoaImeUpdate update)
        {
            _updateCount++;
            try { ImeUpdate?.Invoke(update); }
            catch (InvalidOperationException) { }   // the editor went away mid-composition
        }

        // ---- Objective-C runtime ----------------------------------------------------

        private const string ObjC = "/usr/lib/libobjc.A.dylib";
        private const ulong NSNotFound = long.MaxValue;   // NSNotFound is NSIntegerMax

        private static IntPtr Sel(string name) => sel_registerName(name);

        private static string ReadString(IntPtr obj)
        {
            if (obj == IntPtr.Zero) return null;

            // Either an NSString or an NSAttributedString, depending on the input method; the latter
            // answers -string with the plain text.
            IntPtr nsString = SendBoolPtr(obj, Sel("respondsToSelector:"), Sel("string"))
                ? Send(obj, Sel("string"))
                : obj;
            if (nsString == IntPtr.Zero) return null;

            IntPtr utf8 = Send(nsString, Sel("UTF8String"));
            return utf8 == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(utf8);
        }

        private static IntPtr MakeNSString(string value)
        {
            if (value == null) return IntPtr.Zero;

            // UTF-8, not ANSI: the text being round-tripped here is the reason this file exists, and
            // an ANSI marshal turns every kanji into a question mark.
            IntPtr bytes = Marshal.StringToCoTaskMemUTF8(value);
            try
            {
                // stringWithUTF8String: copies, so the buffer can go as soon as it returns.
                return SendPtrRet(objc_getClass("NSString"), Sel("stringWithUTF8String:"), bytes);
            }
            finally
            {
                Marshal.FreeCoTaskMem(bytes);
            }
        }

        [DllImport("/usr/lib/libSystem.dylib")] private static extern IntPtr dlopen(string path, int mode);

        [DllImport(ObjC)] private static extern IntPtr objc_getClass(string name);
        [DllImport(ObjC)] private static extern IntPtr objc_getProtocol(string name);
        [DllImport(ObjC)] private static extern IntPtr sel_registerName(string name);
        [DllImport(ObjC)] private static extern IntPtr objc_allocateClassPair(IntPtr superclass, string name, UIntPtr extraBytes);
        [DllImport(ObjC)] private static extern void objc_registerClassPair(IntPtr cls);
        [DllImport(ObjC)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool class_addMethod(IntPtr cls, IntPtr sel, IntPtr imp, string types);
        [DllImport(ObjC)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool class_addProtocol(IntPtr cls, IntPtr protocol);

        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr receiver, IntPtr selector);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendVoid(IntPtr receiver, IntPtr selector);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendPtrRet(IntPtr receiver, IntPtr selector, IntPtr arg);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] [return: MarshalAs(UnmanagedType.I1)] private static extern bool SendBoolPtr(IntPtr receiver, IntPtr selector, IntPtr arg);

        [StructLayout(LayoutKind.Sequential)]
        private struct NSRange
        {
            public ulong location;
            public ulong length;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NSRect
        {
            public double x;
            public double y;
            public double width;
            public double height;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NSPoint
        {
            public double x;
            public double y;
        }
    }
}
