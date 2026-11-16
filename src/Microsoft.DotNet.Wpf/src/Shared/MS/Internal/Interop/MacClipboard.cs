// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// macOS system clipboard (NSPasteboard) bridge for the WPF Clipboard, callable
// from any WPF assembly. Backs Clipboard text/image with the real general
// pasteboard so copy/paste interops with other macOS applications. Uses the
// same Objective-C runtime P/Invoke approach as CocoaWindow/CocoaDialogs --
// no child processes, no WinForms.
//

using System;
using System.Runtime.InteropServices;

namespace MS.Internal.Interop
{
    /// <summary>Access to the macOS general pasteboard, callable from any WPF assembly.</summary>
    public static class MacClipboard
    {
        // Uniform Type Identifiers for the standard pasteboard representations.
        public const string TypeString = "public.utf8-plain-text";
        public const string TypePng = "public.png";
        public const string TypeHtml = "public.html";
        public const string TypeRtf = "public.rtf";

        /// <summary>True when running on macOS, where the NSPasteboard bridge is usable.</summary>
        public static bool IsAvailable => OperatingSystem.IsMacOS();

        // NSPasteboard/NSString live in AppKit/Foundation. If AppKit has not been loaded into the
        // process (e.g. before any window exists), objc_getClass("NSPasteboard") returns nil and
        // every objc_msgSend silently no-ops. Load the framework once so the classes are registered.
        private static bool s_appKitLoaded;

        private static void EnsureAppKit()
        {
            if (s_appKitLoaded)
            {
                return;
            }

            dlopen("/System/Library/Frameworks/AppKit.framework/AppKit", RTLD_LAZY);
            s_appKitLoaded = true;
        }

        private static IntPtr GeneralPasteboard()
        {
            EnsureAppKit();
            return Send(Cls("NSPasteboard"), Sel("generalPasteboard"));
        }

        /// <summary>Empties the system pasteboard.</summary>
        public static void Clear()
        {
            if (!IsAvailable)
            {
                return;
            }

            Send(GeneralPasteboard(), Sel("clearContents"));
        }

        /// <summary>Replaces the pasteboard contents with a single plain-text string.</summary>
        public static void SetString(string value)
        {
            if (!IsAvailable || value is null)
            {
                return;
            }

            IntPtr pb = GeneralPasteboard();
            Send(pb, Sel("clearContents"));
            SendBool(pb, Sel("setString:forType:"), NSStr(value), NSStr(TypeString));
        }

        /// <summary>Returns the pasteboard's plain-text string, or <see langword="null"/> when none is present.</summary>
        public static string GetString()
        {
            if (!IsAvailable)
            {
                return null;
            }

            IntPtr ns = Send(GeneralPasteboard(), Sel("stringForType:"), NSStr(TypeString));
            if (ns == IntPtr.Zero)
            {
                return null;
            }

            return Marshal.PtrToStringUTF8(Send(ns, Sel("UTF8String")));
        }

        /// <summary>True when the pasteboard currently holds plain text.</summary>
        public static bool ContainsString() => IsAvailable && GetString() is not null;

        /// <summary>Replaces the pasteboard contents with raw bytes under a single UTI type.</summary>
        public static void SetData(string type, byte[] data)
        {
            if (!IsAvailable || data is null)
            {
                return;
            }

            IntPtr pb = GeneralPasteboard();
            Send(pb, Sel("clearContents"));
            SendBool(pb, Sel("setData:forType:"), NSData(data), NSStr(type));
        }

        /// <summary>Returns the pasteboard bytes for a UTI type, or <see langword="null"/> when absent.</summary>
        public static byte[] GetData(string type)
        {
            if (!IsAvailable)
            {
                return null;
            }

            IntPtr nsData = Send(GeneralPasteboard(), Sel("dataForType:"), NSStr(type));
            if (nsData == IntPtr.Zero)
            {
                return null;
            }

            nint len = SendNInt(nsData, Sel("length"));
            if (len <= 0)
            {
                return null;
            }

            IntPtr bytes = Send(nsData, Sel("bytes"));
            if (bytes == IntPtr.Zero)
            {
                return null;
            }

            byte[] managed = new byte[(int)len];
            Marshal.Copy(bytes, managed, 0, (int)len);
            return managed;
        }

        /// <summary>True when the pasteboard currently holds data of the given UTI type.</summary>
        public static bool ContainsData(string type) => IsAvailable && GetData(type) is not null;

        /// <summary>
        /// Replaces the pasteboard contents with several representations at once: the files (if any)
        /// as pasteboard items of their own -- the way Finder puts several files there -- and every other
        /// type on the first item. Written one at a time, each write cleared the one before, and a copy
        /// that offers text and RTF (a rich text box) left only whichever came last.
        /// </summary>
        public static void SetMany(System.Collections.Generic.IReadOnlyList<(string Type, byte[] Data)> items, string[] filePaths)
        {
            if (!IsAvailable)
            {
                return;
            }

            IntPtr pb = GeneralPasteboard();
            Send(pb, Sel("clearContents"));
            if (filePaths is { Length: > 0 })
            {
                IntPtr urls = Send(Cls("NSMutableArray"), Sel("array"));
                foreach (string path in filePaths)
                {
                    if (string.IsNullOrEmpty(path)) continue;
                    IntPtr url = Send(Cls("NSURL"), Sel("fileURLWithPath:"), NSStr(path));
                    if (url != IntPtr.Zero) Send(urls, Sel("addObject:"), url);
                }
                SendBool1(pb, Sel("writeObjects:"), urls);
            }
            foreach ((string type, byte[] data) in items)
            {
                if (data is null) continue;
                SendBool(pb, Sel("setData:forType:"), NSData(data), NSStr(type));
            }
        }

        /// <summary>The paths of the files on the pasteboard (file URLs only), or null when there are none.</summary>
        public static string[] GetFilePaths()
        {
            if (!IsAvailable)
            {
                return null;
            }

            IntPtr classes = Send(Cls("NSArray"), Sel("arrayWithObject:"), Cls("NSURL"));
            IntPtr yes = SendBoolArg(Cls("NSNumber"), Sel("numberWithBool:"), true);
            IntPtr options = Send(Cls("NSDictionary"), Sel("dictionaryWithObject:forKey:"), yes, NSStr("NSPasteboardURLReadingFileURLsOnlyKey"));
            IntPtr urls = Send(GeneralPasteboard(), Sel("readObjectsForClasses:options:"), classes, options);
            nint count = urls == IntPtr.Zero ? 0 : SendNInt(urls, Sel("count"));
            if (count <= 0)
            {
                return null;
            }

            var paths = new System.Collections.Generic.List<string>((int)count);
            for (nint i = 0; i < count; i++)
            {
                IntPtr url = SendIndex(urls, Sel("objectAtIndex:"), (nuint)i);
                IntPtr path = url == IntPtr.Zero ? IntPtr.Zero : Send(url, Sel("path"));
                string s = path == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(Send(path, Sel("UTF8String")));
                if (!string.IsNullOrEmpty(s)) paths.Add(s);
            }
            return paths.Count > 0 ? paths.ToArray() : null;
        }

        // ---- Objective-C runtime ----------------------------------------------------

        private const string ObjC = "/usr/lib/libobjc.A.dylib";

        private static IntPtr Cls(string name) => objc_getClass(name);
        private static IntPtr Sel(string name) => sel_registerName(name);

        private static IntPtr NSStr(string s) =>
            SendPtrUtf8(Cls("NSString"), Sel("stringWithUTF8String:"), s ?? string.Empty);

        private static IntPtr NSData(byte[] data)
        {
            // -[NSData dataWithBytes:length:] copies the buffer, so the native
            // allocation can be freed immediately after the call returns.
            IntPtr buffer = Marshal.AllocHGlobal(data.Length);
            try
            {
                Marshal.Copy(data, 0, buffer, data.Length);
                return SendDataWithBytes(Cls("NSData"), Sel("dataWithBytes:length:"), buffer, (nuint)data.Length);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        private const int RTLD_LAZY = 0x1;
        [DllImport("/usr/lib/libSystem.dylib")] private static extern IntPtr dlopen(string path, int mode);

        [DllImport(ObjC)] private static extern IntPtr objc_getClass(string name);
        [DllImport(ObjC)] private static extern IntPtr sel_registerName(string name);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr receiver, IntPtr selector);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr receiver, IntPtr selector, IntPtr arg);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern nint SendNInt(IntPtr receiver, IntPtr selector);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] [return: MarshalAs(UnmanagedType.I1)] private static extern bool SendBool(IntPtr receiver, IntPtr selector, IntPtr arg1, IntPtr arg2);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendPtrUtf8(IntPtr receiver, IntPtr selector, [MarshalAs(UnmanagedType.LPUTF8Str)] string arg);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendDataWithBytes(IntPtr receiver, IntPtr selector, IntPtr bytes, nuint length);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr receiver, IntPtr selector, IntPtr arg1, IntPtr arg2);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] [return: MarshalAs(UnmanagedType.I1)] private static extern bool SendBool1(IntPtr receiver, IntPtr selector, IntPtr arg);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendBoolArg(IntPtr receiver, IntPtr selector, [MarshalAs(UnmanagedType.I1)] bool arg);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendIndex(IntPtr receiver, IntPtr selector, nuint index);
    }
}
