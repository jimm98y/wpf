// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The iOS text-input channel: UITextInput on the WPF view.
//
// On a phone there is no keyboard until something asks for one, and nothing can ask unless it is a
// first responder adopting a text-input protocol. UIKit offers two, and the difference between them
// is the whole reason this file is the size it is:
//
//   UIKeyInput   three methods. The keyboard appears and finished text arrives. A Japanese keyboard
//                still works, but it resolves the reading INSIDE the keyboard, so the user types
//                into a popup bar rather than into the document.
//   UITextInput  the reading is MARKED TEXT: it lives in the document, underlined, and converts in
//                place -- the same inline composition Windows, Linux, macOS and Android all give.
//
// UITextInput is the one implemented here. Its cost is that it is not a callback interface but a
// document protocol: UIKit navigates the text through opaque UITextPosition/UITextRange objects, so
// those abstract classes need concrete subclasses before any of it can work. Both are synthesised at
// run time (WindowsBase cannot reference UIKit) and both are just an integer offset in a box, which
// is what makes the rest of the protocol mechanical: every position is an index into the document,
// every range a pair of them.
//
// The document itself belongs to WPF. This file never holds text -- it forwards every question to
// the IUIKitTextDocument the ImmComposition bridge installs, and forwards every edit back the same
// way, so the WPF document stays the single source of truth exactly as on the other platforms.
//

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace MS.Internal.Interop
{
    /// <summary>
    /// The document UITextInput is asking about, implemented by the ImmComposition bridge over the
    /// focused WPF editor. All offsets are UTF-16 indices into the editor's plain text, which is the
    /// unit UIKit counts in.
    /// </summary>
    internal interface IUIKitTextDocument
    {
        /// <summary>Total length of the document, in UTF-16 units.</summary>
        int Length { get; }

        /// <summary>Text between two offsets; the caller has already clamped them.</summary>
        string TextInRange(int start, int end);

        /// <summary>The current selection (start == end for a caret).</summary>
        void GetSelection(out int start, out int end);

        /// <summary>Move the selection, e.g. when UIKit moves the caret for the keyboard.</summary>
        void SetSelection(int start, int end);

        /// <summary>The composition's extent, or false when there is none.</summary>
        bool TryGetMarkedRange(out int start, out int end);

        /// <summary>
        /// Show <paramref name="text"/> as the in-progress composition, with the input method's
        /// selection inside it. Null text ends the composition.
        /// </summary>
        void SetMarkedText(string text, int selectionStart, int selectionLength);

        /// <summary>Accept the composition as final text.</summary>
        void UnmarkText();

        /// <summary>Replace a range with final text (autocorrect, predictive insertion, plain typing).</summary>
        void ReplaceRange(int start, int end, string text);

        /// <summary>
        /// The caret rectangle at an offset, in device pixels relative to the client area, so the
        /// candidate window and the magnifier land on the right characters.
        /// </summary>
        bool TryGetCaretRect(int offset, out double x, out double y, out double width, out double height);
    }

    [SupportedOSPlatform("ios")]
    internal static unsafe class UIKitTextInput
    {
        /// <summary>The editor UIKit is currently talking to. Set by the ImmComposition bridge.</summary>
        internal static IUIKitTextDocument Document { get; set; }

        internal static bool IsAvailable => OperatingSystem.IsIOS() || OperatingSystem.IsMacCatalyst();

        /// <summary>True while a text field has focus and the keyboard has been asked for.</summary>
        internal static bool IsEnabled { get; private set; }

        /// <summary>True while the input method holds an unfinished composition.</summary>
        internal static bool IsComposing
            => Document != null && Document.TryGetMarkedRange(out _, out _);

        private static IntPtr _firstResponder;
        private static IntPtr _inputDelegate;
        private static IntPtr _tokenizer;

        /// <summary>Raises the keyboard on the view backing the focused window.</summary>
        public static void Enable(IntPtr view, bool multiline, bool password)
        {
            if (!IsAvailable || view == IntPtr.Zero) return;

            _ = multiline;
            _ = password;   // secureTextEntry needs UITextInputTraits; see the traits note below.

            if (_firstResponder != IntPtr.Zero && _firstResponder != view)
            {
                SendVoid(_firstResponder, Sel("resignFirstResponder"));
            }

            SendVoid(view, Sel("becomeFirstResponder"));
            _firstResponder = view;
            IsEnabled = true;
        }

        /// <summary>Dismisses the keyboard.</summary>
        public static void Disable()
        {
            if (_firstResponder != IntPtr.Zero)
            {
                SendVoid(_firstResponder, Sel("resignFirstResponder"));
                _firstResponder = IntPtr.Zero;
            }
            IsEnabled = false;
        }

        /// <summary>
        /// Tells UIKit the text or selection moved underneath it (a WPF edit, not one of its own).
        /// Without this the keyboard's autocorrect state drifts from the document.
        /// </summary>
        public static void NotifyDocumentChanged()
        {
            if (_inputDelegate == IntPtr.Zero) return;
            IntPtr responder = _firstResponder;
            if (responder == IntPtr.Zero) return;

            SendVoidPtr(_inputDelegate, Sel("selectionWillChange:"), responder);
            SendVoidPtr(_inputDelegate, Sel("selectionDidChange:"), responder);
        }

        // ---- class synthesis ------------------------------------------------------

        private static IntPtr s_positionClass;
        private static IntPtr s_rangeClass;
        private static nint s_positionIndexOffset;
        private static nint s_rangeStartOffset;
        private static nint s_rangeEndOffset;

        /// <summary>
        /// Adds UITextInput to a synthesised view class. Called from UIKitWindow while it builds its
        /// view classes, before objc_registerClassPair: methods cannot be added afterwards.
        /// </summary>
        internal static void AddTextInputMethods(IntPtr cls)
        {
            EnsurePositionClasses();

            // Conformance is checked separately from the methods: a view that answers NO to
            // -conformsToProtocol: is never asked for text, however much of the protocol it implements.
            foreach (string name in new[] { "UITextInput", "UIKeyInput", "UITextInputTraits" })
            {
                IntPtr protocol = objc_getProtocol(name);
                if (protocol != IntPtr.Zero) class_addProtocol(cls, protocol);
            }

            // --- UIResponder / UIKeyInput ---
            Add(cls, "canBecomeFirstResponder", (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, byte>)&CanBecomeFirstResponderImp, "B@:");
            Add(cls, "hasText", (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, byte>)&HasTextImp, "B@:");
            Add(cls, "insertText:", (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, void>)&InsertTextImp, "v@:@");
            Add(cls, "deleteBackward", (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void>)&DeleteBackwardImp, "v@:");

            // --- marked text: the reason this protocol is here at all ---
            Add(cls, "setMarkedText:selectedRange:", (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, NSRange, void>)&SetMarkedTextImp, "v@:@{_NSRange=QQ}");
            Add(cls, "unmarkText", (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void>)&UnmarkTextImp, "v@:");
            Add(cls, "markedTextRange", (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr>)&MarkedTextRangeImp, "@@:");
            Add(cls, "markedTextStyle", (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr>)&NilImp, "@@:");
            Add(cls, "setMarkedTextStyle:", (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, void>)&IgnorePtrImp, "v@:@");

            // --- selection and document extent ---
            Add(cls, "selectedTextRange", (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr>)&SelectedTextRangeImp, "@@:");
            Add(cls, "setSelectedTextRange:", (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, void>)&SetSelectedTextRangeImp, "v@:@");
            Add(cls, "beginningOfDocument", (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr>)&BeginningOfDocumentImp, "@@:");
            Add(cls, "endOfDocument", (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr>)&EndOfDocumentImp, "@@:");

            // --- text access and editing ---
            Add(cls, "textInRange:", (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr>)&TextInRangeImp, "@@:@");
            Add(cls, "replaceRange:withText:", (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, void>)&ReplaceRangeImp, "v@:@@");

            // --- position arithmetic: how UIKit walks the document ---
            Add(cls, "textRangeFromPosition:toPosition:", (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr>)&TextRangeFromPositionImp, "@@:@@");
            Add(cls, "positionFromPosition:offset:", (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, nint, IntPtr>)&PositionFromPositionOffsetImp, "@@:@q");
            Add(cls, "positionFromPosition:inDirection:offset:", (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, nint, nint, IntPtr>)&PositionFromPositionDirectionImp, "@@:@qq");
            Add(cls, "comparePosition:toPosition:", (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, nint>)&ComparePositionImp, "q@:@@");
            Add(cls, "offsetFromPosition:toPosition:", (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, nint>)&OffsetFromPositionImp, "q@:@@");
            Add(cls, "positionWithinRange:farthestInDirection:", (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, nint, IntPtr>)&PositionWithinRangeImp, "@@:@q");
            Add(cls, "characterRangeByExtendingPosition:inDirection:", (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, nint, IntPtr>)&CharacterRangeByExtendingImp, "@@:@q");

            // --- writing direction: left-to-right, and not configurable from the keyboard ---
            Add(cls, "baseWritingDirectionForPosition:inDirection:", (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, nint, nint>)&BaseWritingDirectionImp, "q@:@q");
            Add(cls, "setBaseWritingDirection:forRange:", (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, nint, IntPtr, void>)&SetBaseWritingDirectionImp, "v@:q@");

            // --- geometry: where the candidate window and the loupe go ---
            Add(cls, "firstRectForRange:", (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, CGRect>)&FirstRectForRangeImp, RectEnc + "@:@");
            Add(cls, "caretRectForPosition:", (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, CGRect>)&CaretRectForPositionImp, RectEnc + "@:@");
            Add(cls, "selectionRectsForRange:", (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr>)&EmptyArrayImp, "@@:@");

            // --- hit testing: WPF routes touches itself, so these answer conservatively ---
            Add(cls, "closestPositionToPoint:", (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, CGPoint, IntPtr>)&ClosestPositionToPointImp, "@@:{CGPoint=dd}");
            Add(cls, "closestPositionToPoint:withinRange:", (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, CGPoint, IntPtr, IntPtr>)&ClosestPositionWithinRangeImp, "@@:{CGPoint=dd}@");
            Add(cls, "characterRangeAtPoint:", (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, CGPoint, IntPtr>)&CharacterRangeAtPointImp, "@@:{CGPoint=dd}");

            // --- the delegate and tokenizer UIKit installs / expects ---
            Add(cls, "inputDelegate", (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr>)&InputDelegateImp, "@@:");
            Add(cls, "setInputDelegate:", (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, void>)&SetInputDelegateImp, "v@:@");
            Add(cls, "tokenizer", (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr>)&TokenizerImp, "@@:");
        }

        private const string RectEnc = "{CGRect={CGPoint=dd}{CGSize=dd}}";

        private static void Add(IntPtr cls, string selector, IntPtr imp, string types)
            => class_addMethod(cls, Sel(selector), imp, types);

        /// <summary>
        /// Builds the concrete UITextPosition/UITextRange subclasses UIKit hands back and forth.
        /// Each is one integer (or two) in an ivar; the protocol never inspects them, it only passes
        /// them to the methods above, so an offset in a box is the whole implementation.
        /// </summary>
        private static void EnsurePositionClasses()
        {
            if (s_positionClass != IntPtr.Zero) return;

            IntPtr posBase = objc_getClass("UITextPosition");
            IntPtr rangeBase = objc_getClass("UITextRange");
            if (posBase == IntPtr.Zero || rangeBase == IntPtr.Zero) return;   // not a UIKit process

            s_positionClass = objc_getClass("WpfTextPosition");
            if (s_positionClass == IntPtr.Zero)
            {
                s_positionClass = objc_allocateClassPair(posBase, "WpfTextPosition", UIntPtr.Zero);
                // 8 bytes, 8-byte aligned (alignment is passed as log2), encoded as a long.
                class_addIvar(s_positionClass, "_index", (UIntPtr)8, 3, "q");
                objc_registerClassPair(s_positionClass);
            }

            s_rangeClass = objc_getClass("WpfTextRange");
            if (s_rangeClass == IntPtr.Zero)
            {
                s_rangeClass = objc_allocateClassPair(rangeBase, "WpfTextRange", UIntPtr.Zero);
                class_addIvar(s_rangeClass, "_start", (UIntPtr)8, 3, "q");
                class_addIvar(s_rangeClass, "_end", (UIntPtr)8, 3, "q");

                // UITextRange is abstract: these three are what make it usable.
                class_addMethod(s_rangeClass, Sel("start"),
                    (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr>)&RangeStartImp, "@@:");
                class_addMethod(s_rangeClass, Sel("end"),
                    (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr>)&RangeEndImp, "@@:");
                class_addMethod(s_rangeClass, Sel("isEmpty"),
                    (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, byte>)&RangeIsEmptyImp, "B@:");
                objc_registerClassPair(s_rangeClass);
            }

            s_positionIndexOffset = ivar_getOffset(class_getInstanceVariable(s_positionClass, "_index"));
            s_rangeStartOffset = ivar_getOffset(class_getInstanceVariable(s_rangeClass, "_start"));
            s_rangeEndOffset = ivar_getOffset(class_getInstanceVariable(s_rangeClass, "_end"));
        }

        // ---- position/range helpers ------------------------------------------------

        private static IntPtr MakePosition(int index)
        {
            if (s_positionClass == IntPtr.Zero) return IntPtr.Zero;
            IntPtr obj = Send(Send(s_positionClass, Sel("alloc")), Sel("init"));
            if (obj == IntPtr.Zero) return IntPtr.Zero;
            *(long*)((byte*)obj + s_positionIndexOffset) = index;
            return Send(obj, Sel("autorelease"));
        }

        private static IntPtr MakeRange(int start, int end)
        {
            if (s_rangeClass == IntPtr.Zero) return IntPtr.Zero;
            if (end < start) (start, end) = (end, start);

            IntPtr obj = Send(Send(s_rangeClass, Sel("alloc")), Sel("init"));
            if (obj == IntPtr.Zero) return IntPtr.Zero;
            *(long*)((byte*)obj + s_rangeStartOffset) = start;
            *(long*)((byte*)obj + s_rangeEndOffset) = end;
            return Send(obj, Sel("autorelease"));
        }

        // -1 for a null or foreign object: every caller treats a negative index as "no answer",
        // which is what keeps a stray object from being read as offset 0 and moving the caret.
        private static int IndexOf(IntPtr position)
            => position == IntPtr.Zero || s_positionClass == IntPtr.Zero
                ? -1
                : (int)*(long*)((byte*)position + s_positionIndexOffset);

        private static bool RangeOf(IntPtr range, out int start, out int end)
        {
            start = end = 0;
            if (range == IntPtr.Zero || s_rangeClass == IntPtr.Zero) return false;
            start = (int)*(long*)((byte*)range + s_rangeStartOffset);
            end = (int)*(long*)((byte*)range + s_rangeEndOffset);
            return true;
        }

        private static int Clamp(int index)
        {
            IUIKitTextDocument doc = Document;
            int length = doc?.Length ?? 0;
            return index < 0 ? 0 : (index > length ? length : index);
        }

        // ---- UIKeyInput ------------------------------------------------------------

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static byte CanBecomeFirstResponderImp(IntPtr self, IntPtr sel) => 1;

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static byte HasTextImp(IntPtr self, IntPtr sel)
        {
            try { return (byte)((Document?.Length ?? 0) > 0 ? 1 : 0); }
            catch { return 0; }
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void InsertTextImp(IntPtr self, IntPtr sel, IntPtr text)
        {
            // Never let a managed exception unwind into UIKit.
            try
            {
                IUIKitTextDocument doc = Document;
                string s = ReadString(text);
                if (doc == null || string.IsNullOrEmpty(s)) return;

                doc.GetSelection(out int start, out int end);
                doc.ReplaceRange(start, end, s);
            }
            catch (Exception e) { Log("insertText", e); }
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void DeleteBackwardImp(IntPtr self, IntPtr sel)
        {
            try
            {
                IUIKitTextDocument doc = Document;
                if (doc == null) return;

                doc.GetSelection(out int start, out int end);
                if (start != end) doc.ReplaceRange(start, end, string.Empty);
                else if (start > 0) doc.ReplaceRange(start - 1, start, string.Empty);
            }
            catch (Exception e) { Log("deleteBackward", e); }
        }

        // ---- marked text -----------------------------------------------------------

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void SetMarkedTextImp(IntPtr self, IntPtr sel, IntPtr text, NSRange selected)
        {
            try
            {
                IUIKitTextDocument doc = Document;
                if (doc == null) return;

                // A nil or empty marked string is how UIKit cancels a composition.
                string s = ReadString(text);
                doc.SetMarkedText(string.IsNullOrEmpty(s) ? null : s,
                                  (int)selected.location, (int)selected.length);
            }
            catch (Exception e) { Log("setMarkedText", e); }
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void UnmarkTextImp(IntPtr self, IntPtr sel)
        {
            try { Document?.UnmarkText(); }
            catch (Exception e) { Log("unmarkText", e); }
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static IntPtr MarkedTextRangeImp(IntPtr self, IntPtr sel)
        {
            try
            {
                IUIKitTextDocument doc = Document;
                // nil means "no composition", and UIKit relies on it: a non-nil answer while nothing
                // is marked makes the keyboard believe it still owns text it has already committed.
                if (doc == null || !doc.TryGetMarkedRange(out int start, out int end)) return IntPtr.Zero;
                return MakeRange(start, end);
            }
            catch (Exception e) { Log("markedTextRange", e); return IntPtr.Zero; }
        }

        // ---- selection and extent --------------------------------------------------

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static IntPtr SelectedTextRangeImp(IntPtr self, IntPtr sel)
        {
            try
            {
                IUIKitTextDocument doc = Document;
                if (doc == null) return IntPtr.Zero;
                doc.GetSelection(out int start, out int end);
                return MakeRange(start, end);
            }
            catch (Exception e) { Log("selectedTextRange", e); return IntPtr.Zero; }
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void SetSelectedTextRangeImp(IntPtr self, IntPtr sel, IntPtr range)
        {
            try
            {
                if (Document != null && RangeOf(range, out int start, out int end))
                {
                    Document.SetSelection(Clamp(start), Clamp(end));
                }
            }
            catch (Exception e) { Log("setSelectedTextRange", e); }
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static IntPtr BeginningOfDocumentImp(IntPtr self, IntPtr sel) => MakePosition(0);

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static IntPtr EndOfDocumentImp(IntPtr self, IntPtr sel)
        {
            try { return MakePosition(Document?.Length ?? 0); }
            catch (Exception e) { Log("endOfDocument", e); return MakePosition(0); }
        }

        // ---- text access and editing ----------------------------------------------

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static IntPtr TextInRangeImp(IntPtr self, IntPtr sel, IntPtr range)
        {
            try
            {
                IUIKitTextDocument doc = Document;
                if (doc == null || !RangeOf(range, out int start, out int end)) return IntPtr.Zero;

                string s = doc.TextInRange(Clamp(start), Clamp(end));
                return string.IsNullOrEmpty(s) ? MakeNSString(string.Empty) : MakeNSString(s);
            }
            catch (Exception e) { Log("textInRange", e); return IntPtr.Zero; }
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void ReplaceRangeImp(IntPtr self, IntPtr sel, IntPtr range, IntPtr text)
        {
            try
            {
                IUIKitTextDocument doc = Document;
                if (doc == null || !RangeOf(range, out int start, out int end)) return;
                doc.ReplaceRange(Clamp(start), Clamp(end), ReadString(text) ?? string.Empty);
            }
            catch (Exception e) { Log("replaceRange", e); }
        }

        // ---- position arithmetic ---------------------------------------------------

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static IntPtr TextRangeFromPositionImp(IntPtr self, IntPtr sel, IntPtr from, IntPtr to)
        {
            int a = IndexOf(from), b = IndexOf(to);
            return a < 0 || b < 0 ? IntPtr.Zero : MakeRange(a, b);
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static IntPtr PositionFromPositionOffsetImp(IntPtr self, IntPtr sel, IntPtr position, nint offset)
            => PositionOffsetBy(position, offset);

        // Shared by both position-offset entry points; an UnmanagedCallersOnly method cannot be
        // called from managed code, so the logic cannot live in the IMP itself.
        private static IntPtr PositionOffsetBy(IntPtr position, long offset)
        {
            int index = IndexOf(position);
            if (index < 0) return IntPtr.Zero;

            long target = index + offset;
            // Out of bounds must be nil, not clamped: UIKit walks until it gets nil, and a clamped
            // answer makes that walk never terminate.
            if (target < 0 || target > (Document?.Length ?? 0)) return IntPtr.Zero;
            return MakePosition((int)target);
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static IntPtr PositionFromPositionDirectionImp(IntPtr self, IntPtr sel, IntPtr position, nint direction, nint offset)
        {
            // UITextLayoutDirection: right = 2, left = 3, up = 4, down = 5. Up/down would need line
            // layout, which the document protocol here does not model; left/right is what the
            // keyboard uses while composing.
            long signed = direction == 3 || direction == 4 ? -offset : offset;
            return PositionOffsetBy(position, signed);
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static nint ComparePositionImp(IntPtr self, IntPtr sel, IntPtr a, IntPtr b)
        {
            int x = IndexOf(a), y = IndexOf(b);
            return x < y ? -1 : (x > y ? 1 : 0);   // NSOrderedAscending / Descending / Same
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static nint OffsetFromPositionImp(IntPtr self, IntPtr sel, IntPtr from, IntPtr to)
        {
            int a = IndexOf(from), b = IndexOf(to);
            return a < 0 || b < 0 ? 0 : b - a;
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static IntPtr PositionWithinRangeImp(IntPtr self, IntPtr sel, IntPtr range, nint direction)
        {
            if (!RangeOf(range, out int start, out int end)) return IntPtr.Zero;
            return MakePosition(direction == 3 || direction == 4 ? start : end);
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static IntPtr CharacterRangeByExtendingImp(IntPtr self, IntPtr sel, IntPtr position, nint direction)
        {
            int index = IndexOf(position);
            if (index < 0) return IntPtr.Zero;

            return direction == 3 || direction == 4
                ? MakeRange(Clamp(index - 1), index)
                : MakeRange(index, Clamp(index + 1));
        }

        // ---- writing direction -----------------------------------------------------

        // NSWritingDirectionNatural = -1. WPF decides direction from the text itself.
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static nint BaseWritingDirectionImp(IntPtr self, IntPtr sel, IntPtr position, nint direction) => -1;

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void SetBaseWritingDirectionImp(IntPtr self, IntPtr sel, nint direction, IntPtr range) { }

        // ---- geometry --------------------------------------------------------------

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static CGRect FirstRectForRangeImp(IntPtr self, IntPtr sel, IntPtr range)
        {
            try
            {
                if (RangeOf(range, out int start, out _)) return CaretRect(self, start);
            }
            catch (Exception e) { Log("firstRectForRange", e); }
            return default;
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static CGRect CaretRectForPositionImp(IntPtr self, IntPtr sel, IntPtr position)
        {
            try
            {
                int index = IndexOf(position);
                if (index >= 0) return CaretRect(self, index);
            }
            catch (Exception e) { Log("caretRectForPosition", e); }
            return default;
        }

        /// <summary>
        /// The caret rectangle in the VIEW's coordinates: UIKit asks in points relative to the view,
        /// while WPF reports device pixels relative to the client area, so the scale divides out.
        /// </summary>
        private static CGRect CaretRect(IntPtr view, int offset)
        {
            IUIKitTextDocument doc = Document;
            if (doc == null || !doc.TryGetCaretRect(offset, out double x, out double y, out double w, out double h))
            {
                return default;
            }

            double scale = ScreenScale();
            if (scale <= 0) scale = 1;
            return new CGRect { x = x / scale, y = y / scale, width = Math.Max(w / scale, 1), height = h / scale };
        }

        private static double ScreenScale()
        {
            IntPtr screen = Send(objc_getClass("UIScreen"), Sel("mainScreen"));
            return screen == IntPtr.Zero ? 1 : SendDouble(screen, Sel("scale"));
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static IntPtr EmptyArrayImp(IntPtr self, IntPtr sel, IntPtr arg)
            => Send(objc_getClass("NSArray"), Sel("array"));

        // ---- hit testing -----------------------------------------------------------
        //
        // WPF does its own hit testing from touch events, so these answer with the caret rather than
        // resolving a point. That costs UIKit's own text selection gestures, which WPF replaces.

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static IntPtr ClosestPositionToPointImp(IntPtr self, IntPtr sel, CGPoint point)
        {
            try
            {
                IUIKitTextDocument doc = Document;
                if (doc == null) return MakePosition(0);
                doc.GetSelection(out int start, out _);
                return MakePosition(start);
            }
            catch { return MakePosition(0); }
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static IntPtr ClosestPositionWithinRangeImp(IntPtr self, IntPtr sel, CGPoint point, IntPtr range)
            => RangeOf(range, out int start, out _) ? MakePosition(start) : IntPtr.Zero;

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static IntPtr CharacterRangeAtPointImp(IntPtr self, IntPtr sel, CGPoint point) => IntPtr.Zero;

        // ---- delegate and tokenizer ------------------------------------------------

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static IntPtr InputDelegateImp(IntPtr self, IntPtr sel) => _inputDelegate;

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void SetInputDelegateImp(IntPtr self, IntPtr sel, IntPtr value)
        {
            // Assigned, not retained: UIKit owns the delegate and would leak if this retained it.
            _inputDelegate = value;
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static IntPtr TokenizerImp(IntPtr self, IntPtr sel)
        {
            try
            {
                // UIKit requires a non-nil tokenizer; the stock string tokenizer works because it is
                // written against this very protocol, so it answers from the text we hand it.
                if (_tokenizer == IntPtr.Zero)
                {
                    IntPtr cls = objc_getClass("UITextInputStringTokenizer");
                    if (cls == IntPtr.Zero) return IntPtr.Zero;
                    _tokenizer = SendPtrRet(Send(cls, Sel("alloc")), Sel("initWithTextInput:"), self);
                }
                return _tokenizer;
            }
            catch (Exception e) { Log("tokenizer", e); return IntPtr.Zero; }
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static IntPtr NilImp(IntPtr self, IntPtr sel) => IntPtr.Zero;

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void IgnorePtrImp(IntPtr self, IntPtr sel, IntPtr arg) { }

        // ---- WpfTextRange accessors ------------------------------------------------

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static IntPtr RangeStartImp(IntPtr self, IntPtr sel)
            => MakePosition((int)*(long*)((byte*)self + s_rangeStartOffset));

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static IntPtr RangeEndImp(IntPtr self, IntPtr sel)
            => MakePosition((int)*(long*)((byte*)self + s_rangeEndOffset));

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static byte RangeIsEmptyImp(IntPtr self, IntPtr sel)
            => (byte)(*(long*)((byte*)self + s_rangeStartOffset) == *(long*)((byte*)self + s_rangeEndOffset) ? 1 : 0);

        private static void Log(string what, Exception e)
            => Console.WriteLine($"WPF iOS {what} failed: {e}");

        // ---- Objective-C runtime ----------------------------------------------------

        private const string ObjC = "/usr/lib/libobjc.A.dylib";

        private static IntPtr Sel(string name) => sel_registerName(name);

        private static string ReadString(IntPtr nsString)
        {
            if (nsString == IntPtr.Zero) return null;
            IntPtr utf8 = Send(nsString, Sel("UTF8String"));
            return utf8 == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(utf8);
        }

        private static IntPtr MakeNSString(string value)
        {
            if (value == null) return IntPtr.Zero;
            IntPtr bytes = Marshal.StringToCoTaskMemUTF8(value);
            try { return SendPtrRet(objc_getClass("NSString"), Sel("stringWithUTF8String:"), bytes); }
            finally { Marshal.FreeCoTaskMem(bytes); }
        }

        [DllImport(ObjC)] private static extern IntPtr objc_getClass(string name);
        [DllImport(ObjC)] private static extern IntPtr objc_getProtocol(string name);
        [DllImport(ObjC)] private static extern IntPtr sel_registerName(string name);
        [DllImport(ObjC)] private static extern IntPtr objc_allocateClassPair(IntPtr superclass, string name, UIntPtr extraBytes);
        [DllImport(ObjC)] private static extern void objc_registerClassPair(IntPtr cls);
        [DllImport(ObjC)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool class_addMethod(IntPtr cls, IntPtr sel, IntPtr imp, string types);
        [DllImport(ObjC)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool class_addProtocol(IntPtr cls, IntPtr protocol);
        [DllImport(ObjC)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool class_addIvar(IntPtr cls, string name, UIntPtr size, byte alignment, string types);
        [DllImport(ObjC)] private static extern IntPtr class_getInstanceVariable(IntPtr cls, string name);
        [DllImport(ObjC)] private static extern nint ivar_getOffset(IntPtr ivar);

        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr receiver, IntPtr selector);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendVoid(IntPtr receiver, IntPtr selector);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendVoidPtr(IntPtr receiver, IntPtr selector, IntPtr arg);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendPtrRet(IntPtr receiver, IntPtr selector, IntPtr arg);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern double SendDouble(IntPtr receiver, IntPtr selector);

        [StructLayout(LayoutKind.Sequential)]
        private struct NSRange
        {
            public ulong location;
            public ulong length;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct CGPoint
        {
            public double x;
            public double y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct CGRect
        {
            public double x;
            public double y;
            public double width;
            public double height;
        }
    }
}
