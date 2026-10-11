// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Printing on iOS: UIPrintInteractionController.
//
// The shape here is set by two facts about the platform, and both are why this file is short.
//
// First, iOS accepts a PDF directly as a printing item, so there is nothing to convert: the rendered
// document goes straight to the controller and AirPrint does the rest.
//
// Second, and more consequential: this head CANNOT show a modal dialog. Dispatcher.PushFrameImpl
// throws NotSupportedException on iOS, because there is no nested run loop to push. So there is no
// "choose a printer, then print" sequence to implement -- printer selection lives inside the sheet
// UIKit presents, and it is presented at submission. ShowPrintUI therefore returns true without
// showing anything, which is the seam's documented answer for a platform of this shape.
//
// The consequence for callers, which PrintDialog documents: ShowDialog() returns true immediately
// on this head, and the user's real choice happens later. A print that the user cancels in the sheet
// is reported through the completion handler rather than as a return value, which is why submission
// here answers "accepted for presentation" rather than "printed".
//
// Every callback is [UnmanagedCallersOnly], as everything in the iOS half of this port must be: it
// is AOT-only, so there is no runtime to build a native-to-managed thunk for a delegate.
//

using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace MS.Internal.Interop
{
    [SupportedOSPlatform("ios")]
    internal sealed unsafe class UIKitPrint : IPrintBackend
    {
        /// <summary>
        /// AirPrint discovers printers when the sheet is opened, not before, and there is no API to
        /// enumerate them beforehand. An empty list is the honest answer, and the layer above turns
        /// it into "no printer" -- which is why PrintDialog on this head goes straight to submission.
        /// </summary>
        public PrinterInfo[] EnumeratePrinters()
        {
            // One entry, standing for "the sheet will ask". Without it there is no PrintQueue, and
            // without a PrintQueue nothing can call Write at all.
            return new[]
            {
                new PrinterInfo
                {
                    Name = "AirPrint",
                    DisplayName = "AirPrint",
                    IsDefault = true,

                    // US Letter. iOS pages the document to the paper the user picks in the sheet,
                    // so this only decides what the content is laid out for.
                    PageWidth = 816,
                    PageHeight = 1056,
                },
            };
        }

        /// <summary>Nothing to show: the sheet is the dialog, and it appears at submission.</summary>
        public bool ShowPrintUI(PrintJobSettings settings) => true;

        public bool Submit(string jobName, Stream document, PrintJobSettings settings)
        {
            byte[] bytes;
            using (var buffer = new MemoryStream())
            {
                document.CopyTo(buffer);
                bytes = buffer.ToArray();
            }

            if (bytes.Length == 0) return false;

            IntPtr controller = Send(Cls("UIPrintInteractionController"), Sel("sharedPrintController"));
            if (controller == IntPtr.Zero) return false;

            // canPrintData is a real check, not a formality: it is how a malformed PDF is caught
            // before the sheet opens on an empty preview.
            IntPtr data = NSData(bytes);
            if (data == IntPtr.Zero) return false;

            if (!SendBoolPtr(Cls("UIPrintInteractionController"), Sel("canPrintData:"), data))
            {
                return false;
            }

            IntPtr info = Send(Cls("UIPrintInfo"), Sel("printInfo"));
            if (info != IntPtr.Zero)
            {
                // General is the output type for a mixed page. Photo would make the printer treat
                // the whole page as an image and pick glossy settings for a text document.
                SendVoidNInt(info, Sel("setOutputType:"), 0);

                if (!string.IsNullOrEmpty(jobName)) SendVoidPtr(info, Sel("setJobName:"), NSStr(jobName));
                if (settings != null) SendVoidNInt(info, Sel("setOrientation:"), settings.Landscape ? 1 : 0);

                SendVoidPtr(controller, Sel("setPrintInfo:"), info);
            }

            SendVoidPtr(controller, Sel("setPrintingItem:"), data);

            // Presented, not awaited. The sheet is modal to the USER but not to this thread, and
            // this head has no nested run loop to wait on one with -- so "true" here means the sheet
            // was put up, and whether paper comes out is between the user and AirPrint.
            SendVoidPtrBoolPtr(controller, Sel("presentAnimated:completionHandler:"), IntPtr.Zero, true,
                               IntPtr.Zero);

            return true;
        }

        // ---- Objective-C runtime ----------------------------------------------------

        private const string ObjC = "/usr/lib/libobjc.A.dylib";

        private static IntPtr Cls(string name) => objc_getClass(name);

        private static IntPtr Sel(string name) => sel_registerName(name);

        private static IntPtr NSStr(string s)
            => SendPtrUtf8(Cls("NSString"), Sel("stringWithUTF8String:"), s ?? string.Empty);

        private static IntPtr NSData(byte[] bytes)
        {
            fixed (byte* p = bytes)
            {
                return SendPtrPtrNInt(Cls("NSData"), Sel("dataWithBytes:length:"), (IntPtr)p, bytes.Length);
            }
        }

        [DllImport(ObjC)] private static extern IntPtr objc_getClass(string name);
        [DllImport(ObjC)] private static extern IntPtr sel_registerName(string name);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr receiver, IntPtr selector);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendPtrUtf8(IntPtr receiver, IntPtr selector, [MarshalAs(UnmanagedType.LPUTF8Str)] string arg);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendPtrPtrNInt(IntPtr receiver, IntPtr selector, IntPtr a, nint b);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] [return: MarshalAs(UnmanagedType.I1)] private static extern bool SendBoolPtr(IntPtr receiver, IntPtr selector, IntPtr arg);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendVoidPtr(IntPtr receiver, IntPtr selector, IntPtr arg);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendVoidNInt(IntPtr receiver, IntPtr selector, nint arg);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendVoidPtrBoolPtr(IntPtr receiver, IntPtr selector, IntPtr a, [MarshalAs(UnmanagedType.I1)] bool b, IntPtr c);
    }
}
