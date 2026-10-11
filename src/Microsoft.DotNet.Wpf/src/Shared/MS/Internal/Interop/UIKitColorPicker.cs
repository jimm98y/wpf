// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// iOS/iPadOS colour chooser: UIColorPickerViewController (iOS 14), the platform's own, reached
// through the Objective-C runtime as UIKitDialogs reaches the document picker -- for WinForms'
// ColorDialog hosted in WPF there, WPF having no colour dialog of its own.
//
// Like the document picker there is no modal form of it: the controller is PRESENTED and the
// answer arrives later on its delegate (colorPickerViewControllerDidFinish:, sent when the user
// dismisses it), so this returns a Task and ColorDialog.ShowDialogAsync awaits it. The picker has
// no Cancel -- an iOS user picks and dismisses -- so it answers the colour it shows when dismissed,
// and the caller reads an unchanged colour as a cancellation.
//

using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace MS.Internal.Interop
{
    internal static unsafe class UIKitColorPicker
    {
        /// <summary>True on iOS/iPadOS 14 or later, where UIColorPickerViewController exists.</summary>
        public static bool IsAvailable => OperatingSystem.IsIOSVersionAtLeast(14) || OperatingSystem.IsMacCatalystVersionAtLeast(14);

        // One picker at a time: it is a sheet, and a second call while one is up is a caller bug.
        private static TaskCompletionSource<int> s_pending;
        private static IntPtr s_delegateClass;
        private static IntPtr s_delegateInstance;

        /// <summary>Presents the picker on <paramref name="rgb"/> (0xRRGGBB) and completes with the
        /// colour it shows when dismissed, or -1 when it could not be presented.</summary>
        public static Task<int> PickColorAsync(int rgb, string title)
        {
            if (!IsAvailable) return Task.FromResult(-1);

            IntPtr pickerClass = objc_getClass("UIColorPickerViewController");
            IntPtr presenter = TopViewController();
            if (pickerClass == IntPtr.Zero || presenter == IntPtr.Zero) return Task.FromResult(-1);
            if (s_pending is not null && !s_pending.Task.IsCompleted) return Task.FromResult(-1);

            var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            s_pending = completion;
            try
            {
                IntPtr picker = Send(Send(pickerClass, Sel("alloc")), Sel("init"));
                SendVoidBool(picker, Sel("setSupportsAlpha:"), false);
                if (!string.IsNullOrEmpty(title)) SendVoidPtr(picker, Sel("setTitle:"), NSStr(title));
                IntPtr color = SendColor(objc_getClass("UIColor"), Sel("colorWithRed:green:blue:alpha:"),
                                         ((rgb >> 16) & 0xFF) / 255.0, ((rgb >> 8) & 0xFF) / 255.0, (rgb & 0xFF) / 255.0, 1.0);
                SendVoidPtr(picker, Sel("setSelectedColor:"), color);
                SendVoidPtr(picker, Sel("setDelegate:"), DelegateInstance());
                SendPresent(presenter, Sel("presentViewController:animated:completion:"), picker, 1, IntPtr.Zero);
            }
            catch (Exception)
            {
                s_pending = null;
                return Task.FromResult(-1);
            }
            return completion.Task;
        }

        private static IntPtr DelegateInstance()
        {
            if (s_delegateInstance != IntPtr.Zero) return s_delegateInstance;

            if (s_delegateClass == IntPtr.Zero)
            {
                s_delegateClass = objc_getClass("WpfColorPickerDelegate");
                if (s_delegateClass == IntPtr.Zero)
                {
                    s_delegateClass = objc_allocateClassPair(objc_getClass("NSObject"), "WpfColorPickerDelegate", UIntPtr.Zero);
                    class_addMethod(s_delegateClass, Sel("colorPickerViewControllerDidFinish:"),
                        (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, void>)&DidFinishImp, "v@:@");
                    objc_registerClassPair(s_delegateClass);
                }
            }

            s_delegateInstance = Send(Send(s_delegateClass, Sel("alloc")), Sel("init"));
            return s_delegateInstance;
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
        private static void DidFinishImp(IntPtr self, IntPtr sel, IntPtr picker)
        {
            TaskCompletionSource<int> pending = s_pending;
            s_pending = null;
            if (pending is null) return;
            try
            {
                IntPtr color = Send(picker, Sel("selectedColor"));
                double r = 0, g = 0, b = 0, a = 0;
                if (color == IntPtr.Zero || !SendGetRgba(color, Sel("getRed:green:blue:alpha:"), &r, &g, &b, &a))
                {
                    pending.TrySetResult(-1);
                    return;
                }
                pending.TrySetResult((ToByte(r) << 16) | (ToByte(g) << 8) | ToByte(b));
            }
            catch (Exception)
            {
                pending.TrySetResult(-1);
            }
        }

        // UIColor's extended sRGB components can stray outside 0..1; WinForms colours cannot.
        private static int ToByte(double component) => Math.Clamp((int)Math.Round(component * 255.0), 0, 255);

        /// <summary>The top-most presented controller of the key window, which is what can present.</summary>
        private static IntPtr TopViewController()
        {
            IntPtr application = Send(objc_getClass("UIApplication"), Sel("sharedApplication"));
            if (application == IntPtr.Zero) return IntPtr.Zero;

            IntPtr root = IntPtr.Zero;
            IntPtr scenes = Send(application, Sel("connectedScenes"));
            IntPtr enumerator = Send(scenes, Sel("objectEnumerator"));
            for (IntPtr scene = Send(enumerator, Sel("nextObject")); scene != IntPtr.Zero && root == IntPtr.Zero;
                 scene = Send(enumerator, Sel("nextObject")))
            {
                if (!SendBoolPtr(scene, Sel("isKindOfClass:"), objc_getClass("UIWindowScene"))) continue;
                IntPtr windows = Send(scene, Sel("windows"));
                nint count = SendNInt(windows, Sel("count"));
                for (nint i = 0; i < count; i++)
                {
                    IntPtr window = SendPtrNInt(windows, Sel("objectAtIndex:"), i);
                    if (!SendBool(window, Sel("isKeyWindow"))) continue;
                    root = Send(window, Sel("rootViewController"));
                    break;
                }
            }

            IntPtr controller = root;
            while (controller != IntPtr.Zero)
            {
                IntPtr presented = Send(controller, Sel("presentedViewController"));
                if (presented == IntPtr.Zero) break;
                controller = presented;
            }
            return controller;
        }

        private const string ObjC = "/usr/lib/libobjc.A.dylib";

        private static IntPtr Sel(string name) => sel_registerName(name);

        private static IntPtr NSStr(string s) =>
            SendPtrUtf8(objc_getClass("NSString"), Sel("stringWithUTF8String:"), s ?? string.Empty);

        [DllImport(ObjC)] private static extern IntPtr objc_getClass(string name);
        [DllImport(ObjC)] private static extern IntPtr sel_registerName(string name);
        [DllImport(ObjC)] private static extern IntPtr objc_allocateClassPair(IntPtr superclass, string name, UIntPtr extraBytes);
        [DllImport(ObjC)] private static extern void objc_registerClassPair(IntPtr cls);
        [DllImport(ObjC)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool class_addMethod(IntPtr cls, IntPtr sel, IntPtr imp, string types);

        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr receiver, IntPtr selector);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendVoidPtr(IntPtr receiver, IntPtr selector, IntPtr arg);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendVoidBool(IntPtr receiver, IntPtr selector, [MarshalAs(UnmanagedType.I1)] bool arg);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendPresent(IntPtr receiver, IntPtr selector, IntPtr controller, byte animated, IntPtr completion);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern nint SendNInt(IntPtr receiver, IntPtr selector);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendPtrNInt(IntPtr receiver, IntPtr selector, nint arg);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] [return: MarshalAs(UnmanagedType.I1)] private static extern bool SendBool(IntPtr receiver, IntPtr selector);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] [return: MarshalAs(UnmanagedType.I1)] private static extern bool SendBoolPtr(IntPtr receiver, IntPtr selector, IntPtr arg);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendPtrUtf8(IntPtr receiver, IntPtr selector, [MarshalAs(UnmanagedType.LPUTF8Str)] string arg);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendColor(IntPtr receiver, IntPtr selector, double r, double g, double b, double a);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] [return: MarshalAs(UnmanagedType.I1)] private static extern bool SendGetRgba(IntPtr receiver, IntPtr selector, double* r, double* g, double* b, double* a);
    }
}
