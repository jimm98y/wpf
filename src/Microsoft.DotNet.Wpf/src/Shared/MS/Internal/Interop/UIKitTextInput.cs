// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The iOS text-input channel: UIKeyInput on the WPF view.
//
// On a phone there is no keyboard until something asks for one, and nothing can ask unless it is a
// first responder that adopts a text-input protocol. Before this file the iOS head had no text input
// at all -- not "no Japanese", but no typing of any kind -- because the WPF view was a plain UIView:
// it could not become first responder, so the software keyboard never appeared.
//
// UIKeyInput is the smaller of the two protocols UIKit offers, and it is deliberately the one used
// here. What it gives is exactly what a WPF TextBox needs to be typed into: the keyboard appears,
// and finished text arrives through -insertText:. That INCLUDES Japanese, Chinese and Korean --
// on iOS the candidate list lives inside the keyboard itself rather than in the app's window, so the
// input method resolves the composition on its own and hands over the committed string.
//
// What UIKeyInput does NOT give is marked text: the in-progress reading is shown inside the keyboard
// instead of inline in the document, so there is no preedit for WPF to underline. Inline composition
// needs the full UITextInput protocol, which is a much larger surface (it is defined in terms of
// UITextPosition/UITextRange objects, each of which would have to be synthesised as its own
// Objective-C class). That is a separate piece of work; this one makes the platform typeable.
//

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace MS.Internal.Interop
{
    /// <summary>One text change from the iOS keyboard.</summary>
    internal readonly struct UIKitImeUpdate
    {
        /// <summary>Text the keyboard committed, or null for a backspace.</summary>
        public string CommitText { get; }

        /// <summary>True when the user pressed delete rather than inserting anything.</summary>
        public bool DeleteBackward { get; }

        public UIKitImeUpdate(string commit, bool deleteBackward)
        {
            CommitText = commit;
            DeleteBackward = deleteBackward;
        }
    }

    [SupportedOSPlatform("ios")]
    internal static unsafe class UIKitTextInput
    {
        /// <summary>Raised on the UI thread when the keyboard inserts or deletes text.</summary>
        public static event Action<UIKitImeUpdate> ImeUpdate;

        internal static bool IsAvailable => OperatingSystem.IsIOS() || OperatingSystem.IsMacCatalyst();

        /// <summary>True while a text field has focus and the keyboard has been asked for.</summary>
        internal static bool IsEnabled { get; private set; }

        /// <summary>
        /// Always false: UIKeyInput has no marked text, so there is never an unfinished composition
        /// on this platform's side of the boundary. Present so the platform dispatcher can ask every
        /// backend the same question.
        /// </summary>
        internal static bool IsComposing => false;

        // The view currently holding the keyboard, so Disable can resign the right one even if the
        // WPF window it belongs to is torn down first.
        private static IntPtr _firstResponder;

        /// <summary>
        /// Asks for the keyboard on the view backing the focused window. Called when a TextBox takes
        /// focus; -becomeFirstResponder is what actually raises it.
        /// </summary>
        public static void Enable(IntPtr view, bool multiline, bool password)
        {
            if (!IsAvailable || view == IntPtr.Zero) return;

            _ = multiline;   // UIKeyInput carries no traits; a UITextInputTraits pass would set these.
            _ = password;    // Likewise secureTextEntry, which needs the traits protocol.

            if (_firstResponder != IntPtr.Zero && _firstResponder != view)
            {
                SendVoid(_firstResponder, Sel("resignFirstResponder"));
            }

            SendVoid(view, Sel("becomeFirstResponder"));
            _firstResponder = view;
            IsEnabled = true;
        }

        /// <summary>Dismisses the keyboard. Called when focus leaves the field.</summary>
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
        /// Adds UIKeyInput to a synthesised view class. Called from UIKitWindow while it is building
        /// its view classes, before objc_registerClassPair -- methods cannot be added afterwards.
        /// </summary>
        internal static void AddKeyInputMethods(IntPtr cls)
        {
            // Conformance is a separate step from implementing the methods, and UIKit checks it:
            // a view that does not answer -conformsToProtocol: is never asked for text at all.
            IntPtr protocol = objc_getProtocol("UIKeyInput");
            if (protocol != IntPtr.Zero) class_addProtocol(cls, protocol);

            // BOOL is _Bool on every iOS ABI, hence "B".
            class_addMethod(cls, Sel("canBecomeFirstResponder"),
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, byte>)&CanBecomeFirstResponderImp, "B@:");
            class_addMethod(cls, Sel("hasText"),
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, byte>)&HasTextImp, "B@:");
            class_addMethod(cls, Sel("insertText:"),
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, void>)&InsertTextImp, "v@:@");
            class_addMethod(cls, Sel("deleteBackward"),
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void>)&DeleteBackwardImp, "v@:");
        }

        // A plain UIView answers NO, and a view that answers NO can never raise the keyboard.
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static byte CanBecomeFirstResponderImp(IntPtr self, IntPtr sel) => 1;

        // UIKit asks this to decide whether the delete key does anything. Answering YES
        // unconditionally keeps backspace live: the document, not the view, knows what is there, and
        // a delete at an empty caret is harmless.
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static byte HasTextImp(IntPtr self, IntPtr sel) => 1;

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void InsertTextImp(IntPtr self, IntPtr sel, IntPtr text)
        {
            // Never let a managed exception unwind into UIKit.
            try
            {
                string s = ReadString(text);
                if (!string.IsNullOrEmpty(s)) Raise(new UIKitImeUpdate(s, false));
            }
            catch (Exception e)
            {
                Console.WriteLine($"WPF iOS insertText failed: {e}");
            }
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void DeleteBackwardImp(IntPtr self, IntPtr sel)
        {
            try
            {
                Raise(new UIKitImeUpdate(null, true));
            }
            catch (Exception e)
            {
                Console.WriteLine($"WPF iOS deleteBackward failed: {e}");
            }
        }

        private static void Raise(UIKitImeUpdate update)
        {
            try { ImeUpdate?.Invoke(update); }
            catch (InvalidOperationException) { }   // the editor went away mid-edit
        }

        // ---- Objective-C runtime ----------------------------------------------------

        private const string ObjC = "/usr/lib/libobjc.A.dylib";

        private static IntPtr Sel(string name) => sel_registerName(name);

        private static string ReadString(IntPtr nsString)
        {
            if (nsString == IntPtr.Zero) return null;
            IntPtr utf8 = Send(nsString, Sel("UTF8String"));
            return utf8 == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(utf8);
        }

        [DllImport(ObjC)] private static extern IntPtr objc_getProtocol(string name);
        [DllImport(ObjC)] private static extern IntPtr sel_registerName(string name);
        [DllImport(ObjC)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool class_addMethod(IntPtr cls, IntPtr sel, IntPtr imp, string types);
        [DllImport(ObjC)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool class_addProtocol(IntPtr cls, IntPtr protocol);

        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr receiver, IntPtr selector);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendVoid(IntPtr receiver, IntPtr selector);
    }
}
