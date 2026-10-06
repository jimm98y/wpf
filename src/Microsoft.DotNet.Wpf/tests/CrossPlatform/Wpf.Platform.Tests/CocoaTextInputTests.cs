// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The macOS input-method channel (NSTextInputClient), driven from the AppKit side.
//
// A real composition needs a real input method, which no test can stand up. What a test CAN do is
// take AppKit's place: the client is an Objective-C object, so sending it -setMarkedText:... and
// -insertText:... is exactly what Kotoeri does, and everything downstream of that -- the UTF-8
// round-trip, the preedit selection arithmetic, the update the editor finally sees -- is the same
// code either way.
//
// The messages are sent through objc_msgSend rather than a managed helper on purpose. That is what
// exercises the parts most likely to be wrong and least likely to fail loudly: the type encodings
// the class was registered with, and the ABI of a struct returned FROM managed code (an NSRange in
// registers, an NSRect as four doubles). A mismatch there is not a compile error; it is a candidate
// window in the wrong place, or a crash inside AppKit.
//

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using MS.Internal.Interop;
using Xunit;

namespace Wpf.Platform.Tests
{
    [Collection("CocoaTextInput")]   // one process-wide input client; the tests share it
    public class CocoaTextInputTests
    {
        [Fact]
        public void MarkedTextBecomesAPreeditUpdate()
        {
            IntPtr client = RequireClient();

            List<CocoaImeUpdate> updates = Capture(() =>
                SetMarkedText(client, "にほんご", selectionStart: 4, selectionLength: 0));

            CocoaImeUpdate update = Assert.Single(updates);
            Assert.Equal("にほんご", update.PreeditText);
            Assert.Null(update.CommitText);
            Assert.True(CocoaTextInput.IsComposing);

            // Clean up: a composition left marked would leak into the next test.
            Capture(() => InsertText(client, string.Empty));
        }

        [Fact]
        public void ConvertedClauseIsReportedAsTheSelection()
        {
            IntPtr client = RequireClient();

            // What conversion looks like: the first two characters are the clause being converted
            // (drawn with the solid underline), the rest is still raw kana.
            List<CocoaImeUpdate> updates = Capture(() =>
                SetMarkedText(client, "日本ご", selectionStart: 0, selectionLength: 2));

            CocoaImeUpdate update = Assert.Single(updates);
            Assert.Equal(0, update.PreeditCursorBegin);
            Assert.Equal(2, update.PreeditCursorEnd);

            Capture(() => InsertText(client, string.Empty));
        }

        [Fact]
        public void CommittingEndsTheCompositionAndYieldsTheText()
        {
            IntPtr client = RequireClient();

            Capture(() => SetMarkedText(client, "にほんご", 4, 0));
            Assert.True(CocoaTextInput.IsComposing);

            List<CocoaImeUpdate> updates = Capture(() => InsertText(client, "日本語"));

            CocoaImeUpdate update = Assert.Single(updates);
            Assert.Equal("日本語", update.CommitText);
            Assert.Null(update.PreeditText);
            Assert.False(CocoaTextInput.IsComposing);
        }

        [Fact]
        public void AbandoningACompositionEndsItWithNoText()
        {
            IntPtr client = RequireClient();

            Capture(() => SetMarkedText(client, "にほん", 3, 0));

            // Escape: the input method drops the composition without committing anything.
            List<CocoaImeUpdate> updates = Capture(() => SendVoid(client, Sel("unmarkText")));

            CocoaImeUpdate update = Assert.Single(updates);
            Assert.Null(update.CommitText);
            Assert.Null(update.PreeditText);
            Assert.False(CocoaTextInput.IsComposing);
        }

        [Fact]
        public void HasMarkedTextTracksTheComposition()
        {
            IntPtr client = RequireClient();

            Assert.False(SendBool(client, Sel("hasMarkedText")));

            Capture(() => SetMarkedText(client, "あ", 1, 0));
            Assert.True(SendBool(client, Sel("hasMarkedText")));

            Capture(() => InsertText(client, "亜"));
            Assert.False(SendBool(client, Sel("hasMarkedText")));
        }

        [Fact]
        public void SelectedRangeReportsWhatWasLastToldToTheInputMethod()
        {
            IntPtr client = RequireClient();

            CocoaTextInput.SetSurroundingText("日本語のテキスト", cursor: 5, anchor: 3);

            // A struct RETURNED from a managed IMP: two NSUIntegers, which arrive in registers.
            NSRange range = SendRange(client, Sel("selectedRange"));
            Assert.Equal(3ul, range.location);
            Assert.Equal(2ul, range.length);
        }

        [Fact]
        public void SurroundingTextComesBackAsAnAttributedSubstring()
        {
            IntPtr client = RequireClient();

            CocoaTextInput.SetSurroundingText("日本語のテキスト", cursor: 0, anchor: 0);

            // The context an IME reconverts from. Requested in UTF-16 units, as AppKit counts.
            IntPtr attributed = SendAttributedSubstring(
                client, Sel("attributedSubstringForProposedRange:actualRange:"),
                new NSRange { location = 0, length = 3 }, IntPtr.Zero);

            Assert.NotEqual(IntPtr.Zero, attributed);
            Assert.Equal("日本語", ReadString(Send(attributed, Sel("string"))));
        }

        [Fact]
        public void CaretRectangleSurvivesTheReturnTrip()
        {
            IntPtr client = RequireClient();

            // Four doubles returned from managed code: the ABI most likely to be silently wrong, and
            // the answer that decides where the candidate window opens. There is no window here, so
            // the conversion declines and the previously-set rectangle stands -- which is precisely
            // what has to come back unmangled.
            NSRect rect = SendRect(client, Sel("firstRectForCharacterRange:actualRange:"),
                                   new NSRange { location = 0, length = 0 }, IntPtr.Zero);

            Assert.True(rect.width > 0, $"width came back as {rect.width}");
            Assert.True(rect.height > 0, $"height came back as {rect.height}");
            Assert.False(double.IsNaN(rect.x) || double.IsNaN(rect.y), "origin came back as NaN");
        }

        [Fact]
        public void ClientConformsToTheProtocolAppKitAsksFor()
        {
            IntPtr client = RequireClient();

            // NSTextInputContext refuses a client that answers NO here, however many of the methods
            // it implements -- adding the protocol is a separate step from adding the IMPs.
            IntPtr protocol = objc_getProtocol("NSTextInputClient");
            Assert.NotEqual(IntPtr.Zero, protocol);
            Assert.True(SendBoolPtr(client, Sel("conformsToProtocol:"), protocol));
        }

        // ---- harness ---------------------------------------------------------------------------

        private static IntPtr RequireClient()
        {
            Assert.SkipUnless(OperatingSystem.IsMacOS(), "the Cocoa input-method channel is macOS-only");

            IntPtr client = CocoaTextInput.ClientForTest;
            Assert.SkipWhen(client == IntPtr.Zero, "AppKit is not loadable in this process");
            return client;
        }

        /// <summary>Runs an action and returns the ImeUpdates it produced.</summary>
        private static List<CocoaImeUpdate> Capture(Action action)
        {
            var updates = new List<CocoaImeUpdate>();
            void Handler(CocoaImeUpdate update) => updates.Add(update);

            CocoaTextInput.ImeUpdate += Handler;
            try { action(); }
            finally { CocoaTextInput.ImeUpdate -= Handler; }

            return updates;
        }

        private static void SetMarkedText(IntPtr client, string text, int selectionStart, int selectionLength)
        {
            IntPtr str = MakeNSString(text);
            SendSetMarkedText(client, Sel("setMarkedText:selectedRange:replacementRange:"), str,
                              new NSRange { location = (ulong)selectionStart, length = (ulong)selectionLength },
                              NotFoundRange);
        }

        private static void InsertText(IntPtr client, string text)
            => SendInsertText(client, Sel("insertText:replacementRange:"), MakeNSString(text), NotFoundRange);

        // NSNotFound: "replace the marked text or the selection", the range AppKit sends unless an
        // input method is deliberately rewriting text it does not own.
        private static NSRange NotFoundRange => new NSRange { location = long.MaxValue, length = 0 };

        private static IntPtr MakeNSString(string value)
        {
            IntPtr bytes = Marshal.StringToCoTaskMemUTF8(value);
            try { return SendPtrRet(objc_getClass("NSString"), Sel("stringWithUTF8String:"), bytes); }
            finally { Marshal.FreeCoTaskMem(bytes); }
        }

        private static string ReadString(IntPtr nsString)
        {
            if (nsString == IntPtr.Zero) return null;
            IntPtr utf8 = Send(nsString, Sel("UTF8String"));
            return utf8 == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(utf8);
        }

        private const string ObjC = "/usr/lib/libobjc.A.dylib";

        private static IntPtr Sel(string name) => sel_registerName(name);

        [DllImport(ObjC)] private static extern IntPtr objc_getClass(string name);
        [DllImport(ObjC)] private static extern IntPtr objc_getProtocol(string name);
        [DllImport(ObjC)] private static extern IntPtr sel_registerName(string name);

        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr receiver, IntPtr sel);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendVoid(IntPtr receiver, IntPtr sel);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendPtrRet(IntPtr receiver, IntPtr sel, IntPtr arg);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] [return: MarshalAs(UnmanagedType.I1)] private static extern bool SendBool(IntPtr receiver, IntPtr sel);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] [return: MarshalAs(UnmanagedType.I1)] private static extern bool SendBoolPtr(IntPtr receiver, IntPtr sel, IntPtr arg);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern NSRange SendRange(IntPtr receiver, IntPtr sel);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern NSRect SendRect(IntPtr receiver, IntPtr sel, NSRange range, IntPtr actualRange);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendAttributedSubstring(IntPtr receiver, IntPtr sel, NSRange range, IntPtr actualRange);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendInsertText(IntPtr receiver, IntPtr sel, IntPtr str, NSRange replacement);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendSetMarkedText(IntPtr receiver, IntPtr sel, IntPtr str, NSRange selected, NSRange replacement);

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
    }
}
