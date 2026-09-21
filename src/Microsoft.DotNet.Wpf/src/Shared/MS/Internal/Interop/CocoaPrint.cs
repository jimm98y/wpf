// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Printing on macOS: the system print panel, through PDFKit.
//
// A rendered PDF is exactly what this platform wants. Quartz IS a PDF imaging model, PDFKit will
// build a print operation from a document, and NSPrintOperation then owns everything a user expects
// -- the panel, the preview, the page-range and copies controls, "Save as PDF", and the queue.
// Writing any of that by hand would be reimplementing a dialog the user already knows.
//
//     PDFDocument(fileURL) -> printOperationForPrintInfo:scalingMode:autoRotate: -> runOperation
//
// Printer enumeration goes to NSPrinter rather than to CUPS directly. macOS is a CUPS system
// underneath, but NSPrinter reports what the user has actually configured, in the order and with
// the names the print panel will show, and it costs one message send.
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace MS.Internal.Interop
{
    [SupportedOSPlatform("macos")]
    internal sealed class CocoaPrint : IPrintBackend
    {
        public PrinterInfo[] EnumeratePrinters()
        {
            if (!EnsureAppKit()) return Array.Empty<PrinterInfo>();

            IntPtr names = Send(Cls("NSPrinter"), Sel("printerNames"));
            if (names == IntPtr.Zero) return Array.Empty<PrinterInfo>();

            nint count = SendNInt(names, Sel("count"));
            if (count <= 0) return Array.Empty<PrinterInfo>();

            string defaultName = DefaultPrinterName();
            var printers = new List<PrinterInfo>((int)count);

            for (nint i = 0; i < count; i++)
            {
                string name = Utf8(SendPtrNInt(names, Sel("objectAtIndex:"), i));
                if (string.IsNullOrEmpty(name)) continue;

                printers.Add(new PrinterInfo
                {
                    Name = name,
                    DisplayName = DisplayNameOf(name) ?? name,
                    IsDefault = string.Equals(name, defaultName, StringComparison.Ordinal),
                });
            }

            return printers.ToArray();
        }

        /// <summary>
        /// The printer the shared NSPrintInfo starts on, which is the system default.
        /// </summary>
        private static string DefaultPrinterName()
        {
            IntPtr info = Send(Cls("NSPrintInfo"), Sel("sharedPrintInfo"));
            if (info == IntPtr.Zero) return null;

            IntPtr printer = Send(info, Sel("printer"));
            return printer != IntPtr.Zero ? Utf8(Send(printer, Sel("name"))) : null;
        }

        private static string DisplayNameOf(string name)
        {
            IntPtr printer = SendPtrRet(Cls("NSPrinter"), Sel("printerWithName:"), NSStr(name));
            if (printer == IntPtr.Zero) return null;

            // The "type" is the model, which is what the panel shows beside the queue name.
            string type = Utf8(Send(printer, Sel("type")));
            return string.IsNullOrEmpty(type) ? name : name + " (" + type + ")";
        }

        /// <summary>
        /// Nothing is shown here. On macOS the print panel belongs to the print OPERATION, so it
        /// appears at submission with the document already in it and a live preview -- which is
        /// better than a dialog beforehand, and is what every native application does.
        /// </summary>
        public bool ShowPrintUI(PrintJobSettings settings) => true;

        public bool Submit(string jobName, Stream document, PrintJobSettings settings)
        {
            // PDFKit reads from a URL rather than from bytes we hand it, so the document goes to a
            // file. It is deleted after the operation returns, which is after the user has dismissed
            // the panel and the job has been spooled.
            string path = Path.Combine(Path.GetTempPath(), "wpf-print-" + Guid.NewGuid().ToString("N") + ".pdf");

            try
            {
                using (var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    document.CopyTo(file);
                }

                return Print(path, jobName, settings);
            }
            catch (IOException)
            {
                return false;
            }
            finally
            {
                try { File.Delete(path); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        private static bool Print(string path, string jobName, PrintJobSettings settings)
        {
            IntPtr pool = Send(Send(Cls("NSAutoreleasePool"), Sel("alloc")), Sel("init"));

            try
            {
                // PDFKit is a separate framework and is not loaded into a plain application, so it
                // has to be asked for. dlopen rather than a DllImport for the same reason the
                // accessibility code uses one: a DllImport naming a framework is resolved EAGERLY by
                // the iOS linker, and this file compiles into WindowsBase for every head.
                if (!EnsureAppKit() || !EnsurePdfKit()) return false;

                IntPtr url = SendPtrRet(Cls("NSURL"), Sel("fileURLWithPath:"), NSStr(path));
                if (url == IntPtr.Zero) return false;

                IntPtr pdf = Send(Cls("PDFDocument"), Sel("alloc"));
                pdf = SendPtrRet(pdf, Sel("initWithURL:"), url);
                if (pdf == IntPtr.Zero) return false;

                IntPtr info = PrintInfo(jobName, settings);

                // Scaling mode 0 is kPDFPrintPageScaleNone: the page is already the right size, and
                // letting the operation scale it would silently shrink every document by the
                // printer's margins.
                IntPtr operation = SendPtrPtrNIntBool(pdf,
                    Sel("printOperationForPrintInfo:scalingMode:autoRotate:"), info, 0, false);

                if (operation == IntPtr.Zero) return false;

                SendVoidBool(operation, Sel("setShowsPrintPanel:"), true);
                SendVoidBool(operation, Sel("setShowsProgressPanel:"), true);

                if (!string.IsNullOrEmpty(jobName))
                {
                    SendVoidPtr(operation, Sel("setJobTitle:"), NSStr(jobName));
                }

                // Blocking, and correct here: the caller is PrintDialog.PrintVisual, which is
                // synchronous by contract, and macOS keeps a running dispatcher loop underneath.
                return SendBool(operation, Sel("runOperation"));
            }
            finally
            {
                if (pool != IntPtr.Zero) Send(pool, Sel("drain"));
            }
        }

        private static IntPtr PrintInfo(string jobName, PrintJobSettings settings)
        {
            IntPtr info = Send(Cls("NSPrintInfo"), Sel("sharedPrintInfo"));
            if (info == IntPtr.Zero || settings == null) return info;

            // A copy, so a job's choices do not become the application's defaults.
            info = SendPtrRet(Send(Cls("NSPrintInfo"), Sel("alloc")), Sel("initWithDictionary:"),
                              Send(info, Sel("dictionary")));

            if (!string.IsNullOrEmpty(settings.PrinterName))
            {
                IntPtr printer = SendPtrRet(Cls("NSPrinter"), Sel("printerWithName:"), NSStr(settings.PrinterName));
                if (printer != IntPtr.Zero) SendVoidPtr(info, Sel("setPrinter:"), printer);
            }

            // The page size is already baked into the PDF's MediaBox, so the margins are zeroed
            // rather than set: anything else would inset content that has already been laid out.
            SendVoidDouble(info, Sel("setLeftMargin:"), 0);
            SendVoidDouble(info, Sel("setRightMargin:"), 0);
            SendVoidDouble(info, Sel("setTopMargin:"), 0);
            SendVoidDouble(info, Sel("setBottomMargin:"), 0);

            SendVoidNInt(info, Sel("setOrientation:"), settings.Landscape ? 1 : 0);

            _ = jobName;
            return info;
        }

        /// <summary>
        /// Makes sure AppKit is loaded.
        ///
        /// Not a formality: a process that has not put a window on screen has never loaded AppKit,
        /// so objc_getClass("NSPrinter") returns nil and enumeration quietly answers "no printers".
        /// That is exactly what a machine with no printers looks like, which is what makes it worth
        /// a comment -- the failure is indistinguishable from the ordinary empty case.
        ///
        /// dlopen rather than a DllImport naming the framework, for the reason the accessibility
        /// code gives: this file compiles into WindowsBase for EVERY head, and the iOS linker
        /// resolves a framework-named DllImport eagerly, failing the link with "framework 'AppKit'
        /// not found".
        /// </summary>
        private static bool EnsureAppKit()
        {
            if (Cls("NSPrinter") != IntPtr.Zero) return true;

            if (s_appKit == IntPtr.Zero)
            {
                s_appKit = dlopen("/System/Library/Frameworks/AppKit.framework/AppKit", RTLD_LAZY);
            }

            return Cls("NSPrinter") != IntPtr.Zero;
        }

        private static IntPtr s_appKit;

        private static bool EnsurePdfKit()
        {
            if (s_pdfKit != IntPtr.Zero) return true;
            if (Cls("PDFDocument") != IntPtr.Zero) return true;

            s_pdfKit = dlopen("/System/Library/Frameworks/PDFKit.framework/PDFKit", RTLD_LAZY);
            return s_pdfKit != IntPtr.Zero && Cls("PDFDocument") != IntPtr.Zero;
        }

        private static IntPtr s_pdfKit;

        private const int RTLD_LAZY = 1;

        [DllImport("/usr/lib/libSystem.B.dylib")]
        private static extern IntPtr dlopen(string path, int mode);

        // ---- Objective-C runtime ----------------------------------------------------

        private const string ObjC = "/usr/lib/libobjc.A.dylib";

        private static IntPtr Cls(string name) => objc_getClass(name);

        private static IntPtr Sel(string name) => sel_registerName(name);

        private static IntPtr NSStr(string s) => SendPtrUtf8(Cls("NSString"), Sel("stringWithUTF8String:"), s ?? string.Empty);

        private static string Utf8(IntPtr nsString)
        {
            if (nsString == IntPtr.Zero) return null;

            IntPtr utf8 = Send(nsString, Sel("UTF8String"));
            return utf8 != IntPtr.Zero ? Marshal.PtrToStringUTF8(utf8) : null;
        }

        [DllImport(ObjC)] private static extern IntPtr objc_getClass(string name);
        [DllImport(ObjC)] private static extern IntPtr sel_registerName(string name);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr receiver, IntPtr selector);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern nint SendNInt(IntPtr receiver, IntPtr selector);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] [return: MarshalAs(UnmanagedType.I1)] private static extern bool SendBool(IntPtr receiver, IntPtr selector);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendPtrRet(IntPtr receiver, IntPtr selector, IntPtr arg);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendPtrNInt(IntPtr receiver, IntPtr selector, nint arg);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendPtrUtf8(IntPtr receiver, IntPtr selector, [MarshalAs(UnmanagedType.LPUTF8Str)] string arg);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendVoidPtr(IntPtr receiver, IntPtr selector, IntPtr arg);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendVoidNInt(IntPtr receiver, IntPtr selector, nint arg);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendVoidDouble(IntPtr receiver, IntPtr selector, double arg);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendVoidBool(IntPtr receiver, IntPtr selector, [MarshalAs(UnmanagedType.I1)] bool arg);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendPtrPtrNIntBool(IntPtr receiver, IntPtr selector, IntPtr a, nint b, [MarshalAs(UnmanagedType.I1)] bool c);
    }
}
