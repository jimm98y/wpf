// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// In-process AppKit dialogs for macOS: NSAlert (MessageBox), NSOpenPanel and
// NSSavePanel (Microsoft.Win32 file dialogs). Uses the same Objective-C runtime
// P/Invoke approach as CocoaWindow -- no child processes, no WinForms.
//

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace MS.Internal.Interop
{
    /// <summary>Modal AppKit dialogs, callable from any WPF assembly on macOS.</summary>
    public static class CocoaDialogs
    {
        private static readonly bool s_debug =
            Environment.GetEnvironmentVariable("WPF_COCOA_DIALOG_LOG") == "1";

        /// <summary>
        /// Shows an app-modal NSAlert with the given buttons (first button is the default)
        /// and returns the zero-based index of the button clicked.
        /// alertStyle: 0 = warning, 1 = informational, 2 = critical (NSAlertStyle).
        /// </summary>
        public static int ShowAlert(string text, string caption, string[] buttons, int alertStyle)
        {
            CocoaWindow.EnsureApplication();
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
            CocoaWindow.EnsureApplication();
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
            CocoaWindow.EnsureApplication();
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
    }
}
