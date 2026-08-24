// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// iOS/iPadOS system clipboard (UIPasteboard) bridge for the WPF Clipboard, the sibling of
// MacClipboard and written to the same shape: the Objective-C runtime through P/Invoke, no
// child processes, no bindings.
//
// Before this file the iOS head had no clipboard: PlatformClipboard fell through to "unavailable",
// so a copy in a WPF TextBox reached an in-process dictionary and nothing else. On iPadOS, where
// two apps are routinely on screen at once and the pasteboard is also how Handoff moves text
// between devices, that is a conspicuous hole.
//
// It is NOT a copy of MacClipboard even though it reads like one. AppKit and UIKit disagree on
// every method that matters here:
//
//   NSPasteboard                          UIPasteboard
//   +generalPasteboard                    +generalPasteboard
//   -clearContents                        (none: assign an empty -items array)
//   -setString:forType:                   -setValue:forPasteboardType:
//   -stringForType:                       -valueForPasteboardType: (an id, not an NSString)
//   -setData:forType:                     -setData:forPasteboardType:
//   -dataForType:                         -dataForPasteboardType:
//
// The UTIs are the same on both, which is the one thing that does carry over.
//

using System;
using System.Runtime.InteropServices;

namespace MS.Internal.Interop
{
    /// <summary>Access to the iOS general pasteboard, callable from any WPF assembly.</summary>
    public static class UIKitClipboard
    {
        // Uniform Type Identifiers, the same spellings AppKit uses.
        public const string TypeString = "public.utf8-plain-text";
        public const string TypePng = "public.png";

        /// <summary>True when running on iOS/iPadOS, where the UIPasteboard bridge is usable.</summary>
        public static bool IsAvailable => OperatingSystem.IsIOS();

        // UIKit is always loaded inside a real iOS app, but a unit-test host or a tool linked
        // against this assembly may not have it. objc_getClass then returns nil and every
        // objc_msgSend silently no-ops, so load the framework once and check the class before use.
        private static bool s_uiKitLoaded;

        private static IntPtr GeneralPasteboard()
        {
            if (!s_uiKitLoaded)
            {
                dlopen("/System/Library/Frameworks/UIKit.framework/UIKit", RTLD_LAZY);
                s_uiKitLoaded = true;
            }

            IntPtr cls = objc_getClass("UIPasteboard");
            return cls == IntPtr.Zero ? IntPtr.Zero : Send(cls, Sel("generalPasteboard"));
        }

        /// <summary>Empties the system pasteboard.</summary>
        public static void Clear()
        {
            if (!IsAvailable) return;

            IntPtr pb = GeneralPasteboard();
            if (pb == IntPtr.Zero) return;

            // UIPasteboard has no -clearContents. Assigning an empty items array is the documented
            // equivalent and the only thing that also drops the non-string representations.
            IntPtr empty = Send(objc_getClass("NSArray"), Sel("array"));
            SendVoidPtr(pb, Sel("setItems:"), empty);
        }

        /// <summary>Replaces the pasteboard contents with a single plain-text string.</summary>
        public static void SetString(string value)
        {
            if (!IsAvailable || value is null) return;

            IntPtr pb = GeneralPasteboard();
            if (pb == IntPtr.Zero) return;

            SendVoidPtrPtr(pb, Sel("setValue:forPasteboardType:"), NSStr(value), NSStr(TypeString));
        }

        /// <summary>Returns the pasteboard's plain-text string, or <see langword="null"/> when none is present.</summary>
        public static string GetString()
        {
            if (!IsAvailable) return null;

            IntPtr pb = GeneralPasteboard();
            if (pb == IntPtr.Zero) return null;

            IntPtr value = Send(pb, Sel("valueForPasteboardType:"), NSStr(TypeString));
            if (value == IntPtr.Zero) return null;

            // -valueForPasteboardType: returns an id: an NSString for a text UTI, but an NSData if
            // the writer put raw bytes under it. Asking an NSData for -UTF8String would call a
            // selector it does not implement and kill the process, so check first.
            if (!SendBoolPtr(value, Sel("isKindOfClass:"), objc_getClass("NSString")))
            {
                return null;
            }

            return Marshal.PtrToStringUTF8(Send(value, Sel("UTF8String")));
        }

        /// <summary>True when the pasteboard currently holds plain text.</summary>
        public static bool ContainsString() => IsAvailable && GetString() is not null;

        /// <summary>Replaces the pasteboard contents with raw bytes under a single UTI type.</summary>
        public static void SetData(string type, byte[] data)
        {
            if (!IsAvailable || data is null) return;

            IntPtr pb = GeneralPasteboard();
            if (pb == IntPtr.Zero) return;

            SendVoidPtrPtr(pb, Sel("setData:forPasteboardType:"), NSData(data), NSStr(type));
        }

        /// <summary>Returns the pasteboard bytes for a UTI type, or <see langword="null"/> when absent.</summary>
        public static byte[] GetData(string type)
        {
            if (!IsAvailable) return null;

            IntPtr pb = GeneralPasteboard();
            if (pb == IntPtr.Zero) return null;

            IntPtr nsData = Send(pb, Sel("dataForPasteboardType:"), NSStr(type));
            if (nsData == IntPtr.Zero) return null;

            nint len = SendNInt(nsData, Sel("length"));
            if (len <= 0) return null;

            IntPtr bytes = Send(nsData, Sel("bytes"));
            if (bytes == IntPtr.Zero) return null;

            byte[] managed = new byte[(int)len];
            Marshal.Copy(bytes, managed, 0, (int)len);
            return managed;
        }

        /// <summary>True when the pasteboard currently holds data of the given UTI type.</summary>
        public static bool ContainsData(string type) => IsAvailable && GetData(type) is not null;

        // ---- Objective-C runtime ----------------------------------------------------

        private const string ObjC = "/usr/lib/libobjc.A.dylib";

        private static IntPtr Sel(string name) => sel_registerName(name);

        private static IntPtr NSStr(string s) =>
            SendPtrUtf8(objc_getClass("NSString"), Sel("stringWithUTF8String:"), s ?? string.Empty);

        private static IntPtr NSData(byte[] data)
        {
            // -[NSData dataWithBytes:length:] copies the buffer, so the native allocation can be
            // freed immediately after the call returns.
            IntPtr buffer = Marshal.AllocHGlobal(data.Length);
            try
            {
                Marshal.Copy(data, 0, buffer, data.Length);
                return SendDataWithBytes(objc_getClass("NSData"), Sel("dataWithBytes:length:"), buffer, (nuint)data.Length);
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
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendVoidPtr(IntPtr receiver, IntPtr selector, IntPtr arg);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendVoidPtrPtr(IntPtr receiver, IntPtr selector, IntPtr arg1, IntPtr arg2);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] [return: MarshalAs(UnmanagedType.I1)] private static extern bool SendBoolPtr(IntPtr receiver, IntPtr selector, IntPtr arg);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendPtrUtf8(IntPtr receiver, IntPtr selector, [MarshalAs(UnmanagedType.LPUTF8Str)] string arg);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendDataWithBytes(IntPtr receiver, IntPtr selector, IntPtr bytes, nuint length);
    }
}
