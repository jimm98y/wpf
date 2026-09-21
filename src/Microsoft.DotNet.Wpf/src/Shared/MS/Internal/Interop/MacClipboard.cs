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
    }
}
