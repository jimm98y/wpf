// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// In-process AppKit dialogs for macOS: NSAlert (MessageBox), NSOpenPanel and
// NSSavePanel (Microsoft.Win32 file dialogs). Uses the same Objective-C runtime
// P/Invoke approach as CocoaWindow -- no child processes, no WinForms.
//
// Link-compiled into WindowsBase (public, for every WPF assembly) and into WinForms (internal, for
// a standalone WinForms app on macOS). Each supplies its accessibility and EnsureApplication in a
// part of its own: CocoaDialogs.Wpf.cs, and the WinForms file dialog bridge.
//

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace MS.Internal.Interop
{
    // No accessibility here on purpose: each assembly's own part states it (public in WindowsBase,
    // internal in WinForms).
#pragma warning disable IDE0040
    static partial class CocoaDialogs
#pragma warning restore IDE0040
    {
        /// <summary>Makes sure NSApplication is up before a panel runs.</summary>
        private static partial void EnsureApplication();

        private static readonly bool s_debug =
            Environment.GetEnvironmentVariable("WPF_COCOA_DIALOG_LOG") == "1";

        /// <summary>
        /// Shows an app-modal NSAlert with the given buttons (first button is the default)
        /// and returns the zero-based index of the button clicked.
        /// alertStyle: 0 = warning, 1 = informational, 2 = critical (NSAlertStyle).
        /// </summary>
        public static int ShowAlert(string text, string caption, string[] buttons, int alertStyle)
        {
            EnsureApplication();
            IntPtr alert = Send(Send(Cls("NSAlert"), Sel("alloc")), Sel("init"));
            SendVoidPtr(alert, Sel("setMessageText:"), NSStr(caption ?? string.Empty));
            SendVoidPtr(alert, Sel("setInformativeText:"), NSStr(text ?? string.Empty));
            SendVoidNInt(alert, Sel("setAlertStyle:"), alertStyle);
            foreach (string label in buttons)
            {
                Send(alert, Sel("addButtonWithTitle:"), NSStr(label));
            }

            nint response = (nint)Send(alert, Sel("runModal"));   // NSAlertFirstButtonReturn = 1000
            if (s_debug) Console.WriteLine($"COCOA-ALERT runModal response={response}");
            int index = (int)(response - 1000);
            return Math.Clamp(index, 0, buttons.Length - 1);
        }

        /// <summary>
        /// Shows an NSOpenPanel. Returns the selected paths, or null when cancelled.
        /// </summary>
        public static string[] ShowOpenPanel(string title, string initialDirectory, bool multiselect, bool chooseDirectories)
        {
            EnsureApplication();
            IntPtr panel = Send(Cls("NSOpenPanel"), Sel("openPanel"));
            ConfigurePanel(panel, title, initialDirectory);
            SendVoidBool(panel, Sel("setCanChooseFiles:"), !chooseDirectories);
            SendVoidBool(panel, Sel("setCanChooseDirectories:"), chooseDirectories);
            SendVoidBool(panel, Sel("setAllowsMultipleSelection:"), multiselect);

            if ((nint)Send(panel, Sel("runModal")) != 1)   // NSModalResponseOK
            {
                return null;
            }

            IntPtr urls = Send(panel, Sel("URLs"));
            nint count = (nint)Send(urls, Sel("count"));
            var paths = new List<string>((int)count);
            for (nint i = 0; i < count; i++)
            {
                IntPtr url = SendPtrNInt(urls, Sel("objectAtIndex:"), i);
                string path = UrlPath(url);
                if (path != null)
                {
                    paths.Add(path);
                }
            }
            return paths.Count > 0 ? paths.ToArray() : null;
        }

        /// <summary>
        /// Shows an NSSavePanel. Returns the chosen path, or null when cancelled.
        /// </summary>
        public static string ShowSavePanel(string title, string initialDirectory, string defaultFileName)
        {
            EnsureApplication();
            IntPtr panel = Send(Cls("NSSavePanel"), Sel("savePanel"));
            ConfigurePanel(panel, title, initialDirectory);
            if (!string.IsNullOrEmpty(defaultFileName))
            {
                SendVoidPtr(panel, Sel("setNameFieldStringValue:"), NSStr(defaultFileName));
            }

            if ((nint)Send(panel, Sel("runModal")) != 1)   // NSModalResponseOK
            {
                return null;
            }

            return UrlPath(Send(panel, Sel("URL")));
        }

        /// <summary>
        /// Runs NSPrintPanel on its own, over a copy of the shared NSPrintInfo -- macOS's print dialog
        /// for a caller that chooses settings first and prints later, as WinForms' PrintDialog does.
        /// The values passed in start the panel and come back as the user left them; false is a
        /// cancellation. Page numbers are 1-based; <paramref name="allPages"/> true means no range.
        /// </summary>
        public static bool ShowPrintPanel(ref string printer, ref int copies, ref bool allPages, ref int firstPage,
                                          ref int lastPage, ref bool landscape, bool showPageRange)
        {
            EnsureApplication();
            IntPtr info = Send(Send(Cls("NSPrintInfo"), Sel("sharedPrintInfo")), Sel("copy"));
            if (info == IntPtr.Zero) return false;

            if (!string.IsNullOrEmpty(printer))
            {
                IntPtr p = SendPtrRet(Cls("NSPrinter"), Sel("printerWithName:"), NSStr(printer));
                if (p != IntPtr.Zero) SendVoidPtr(info, Sel("setPrinter:"), p);
            }
            SendVoidNInt(info, Sel("setOrientation:"), landscape ? 1 : 0);   // NSPaperOrientationLandscape = 1
            IntPtr dict = Send(info, Sel("dictionary"));
            SendVoidPtrPtr(dict, Sel("setObject:forKey:"), SendPtrInt(Cls("NSNumber"), Sel("numberWithInt:"), Math.Max(1, copies)), NSStr("NSCopies"));
            if (showPageRange && !allPages && firstPage > 0)
            {
                SendVoidPtrPtr(dict, Sel("setObject:forKey:"), SendPtrInt(Cls("NSNumber"), Sel("numberWithBool:"), 0), NSStr("NSAllPages"));
                SendVoidPtrPtr(dict, Sel("setObject:forKey:"), SendPtrInt(Cls("NSNumber"), Sel("numberWithInt:"), firstPage), NSStr("NSFirstPage"));
                SendVoidPtrPtr(dict, Sel("setObject:forKey:"), SendPtrInt(Cls("NSNumber"), Sel("numberWithInt:"), Math.Max(firstPage, lastPage)), NSStr("NSLastPage"));
            }

            IntPtr panel = Send(Cls("NSPrintPanel"), Sel("printPanel"));
            // NSPrintPanelShowsCopies 1, ShowsPageRange 2, ShowsPaperSize 4, ShowsOrientation 8.
            SendVoidNInt(panel, Sel("setOptions:"), 1 | (showPageRange ? 2 : 0) | 4 | 8);
            if (SendNIntPtr(panel, Sel("runModalWithPrintInfo:"), info) != 1)   // NSModalResponseOK
            {
                return false;
            }

            IntPtr chosen = Send(info, Sel("printer"));
            if (chosen != IntPtr.Zero)
            {
                IntPtr name = Send(chosen, Sel("name"));
                if (name != IntPtr.Zero) printer = Marshal.PtrToStringUTF8(Send(name, Sel("UTF8String")));
            }
            landscape = (nint)Send(info, Sel("orientation")) == 1;
            IntPtr n = SendPtrRet(dict, Sel("objectForKey:"), NSStr("NSCopies"));
            if (n != IntPtr.Zero) copies = SendInt(n, Sel("intValue"));
            IntPtr all = SendPtrRet(dict, Sel("objectForKey:"), NSStr("NSAllPages"));
            allPages = all == IntPtr.Zero || SendBool(all, Sel("boolValue"));
            if (!allPages)
            {
                IntPtr f = SendPtrRet(dict, Sel("objectForKey:"), NSStr("NSFirstPage"));
                IntPtr l = SendPtrRet(dict, Sel("objectForKey:"), NSStr("NSLastPage"));
                if (f != IntPtr.Zero) firstPage = SendInt(f, Sel("intValue"));
                if (l != IntPtr.Zero) lastPage = SendInt(l, Sel("intValue"));
            }
            return true;
        }

        private static void ConfigurePanel(IntPtr panel, string title, string initialDirectory)
        {
            if (!string.IsNullOrEmpty(title))
            {
                // Panels no longer show a title bar string on modern macOS; message is the
                // visible equivalent above the file browser.
                SendVoidPtr(panel, Sel("setMessage:"), NSStr(title));
            }
            if (!string.IsNullOrEmpty(initialDirectory) && System.IO.Directory.Exists(initialDirectory))
            {
                IntPtr dirUrl = SendPtrRet(Cls("NSURL"), Sel("fileURLWithPath:"), NSStr(initialDirectory));
                SendVoidPtr(panel, Sel("setDirectoryURL:"), dirUrl);
            }
        }

        private static string UrlPath(IntPtr nsUrl)
        {
            if (nsUrl == IntPtr.Zero)
            {
                return null;
            }
            IntPtr path = Send(nsUrl, Sel("path"));
            if (path == IntPtr.Zero)
            {
                return null;
            }
            return Marshal.PtrToStringUTF8(Send(path, Sel("UTF8String")));
        }

        private static IntPtr NSStr(string s) =>
            SendPtrUtf8(Cls("NSString"), Sel("stringWithUTF8String:"), s ?? string.Empty);

        // ---- Objective-C runtime ----------------------------------------------------

        private const string ObjC = "/usr/lib/libobjc.A.dylib";

        private static IntPtr Cls(string name) => objc_getClass(name);
        private static IntPtr Sel(string name) => sel_registerName(name);

        [DllImport(ObjC)] private static extern IntPtr objc_getClass(string name);
        [DllImport(ObjC)] private static extern IntPtr sel_registerName(string name);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr receiver, IntPtr selector);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr receiver, IntPtr selector, IntPtr arg);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendVoidPtr(IntPtr receiver, IntPtr selector, IntPtr arg);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendVoidNInt(IntPtr receiver, IntPtr selector, nint arg);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendVoidBool(IntPtr receiver, IntPtr selector, [MarshalAs(UnmanagedType.I1)] bool arg);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendPtrRet(IntPtr receiver, IntPtr selector, IntPtr arg);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendPtrNInt(IntPtr receiver, IntPtr selector, nint arg);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendPtrUtf8(IntPtr receiver, IntPtr selector, [MarshalAs(UnmanagedType.LPUTF8Str)] string arg);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendVoidPtrPtr(IntPtr receiver, IntPtr selector, IntPtr a, IntPtr b);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendPtrInt(IntPtr receiver, IntPtr selector, int arg);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern int SendInt(IntPtr receiver, IntPtr selector);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] [return: MarshalAs(UnmanagedType.I1)] private static extern bool SendBool(IntPtr receiver, IntPtr selector);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern nint SendNIntPtr(IntPtr receiver, IntPtr selector, IntPtr arg);
    }
}
