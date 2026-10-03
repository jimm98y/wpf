// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// iOS/iPadOS message boxes: UIAlertController, the platform's own alert, reached through the
// Objective-C runtime as UIKitDialogs reaches the document picker.
//
// Like the picker there is no modal form of it -- the controller is PRESENTED and the answer arrives
// later on an action's handler block -- so this returns a Task and MessageBox.ShowAsync awaits it.
// Each button is a UIAlertAction whose handler is a global block (ObjCBlock) carrying the alert's
// token and the button's index in its context; the blocks are freed once the alert has answered.
//

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace MS.Internal.Interop
{
    internal static unsafe class UIKitAlerts
    {
        public static bool IsAvailable => OperatingSystem.IsIOS();

        private sealed class Pending
        {
            public TaskCompletionSource<int> Completion;
            public List<IntPtr> Blocks = new List<IntPtr>();
        }

        private static readonly Dictionary<int, Pending> s_pending = new Dictionary<int, Pending>();
        private static int s_nextToken;

        /// <summary>Presents an alert with the given buttons and completes with the index of the
        /// one tapped. <paramref name="cancelIndex"/> is drawn as the cancel action (or -1);
        /// <paramref name="preferredIndex"/> is the bold, default one (or -1).</summary>
        public static Task<int> ShowAlertAsync(string title, string message, string[] labels, int cancelIndex, int preferredIndex)
        {
            if (!IsAvailable || labels == null || labels.Length == 0) return Task.FromResult(-1);

            IntPtr presenter = RootViewController();
            IntPtr alertClass = objc_getClass("UIAlertController");
            IntPtr actionClass = objc_getClass("UIAlertAction");
            if (presenter == IntPtr.Zero || alertClass == IntPtr.Zero || actionClass == IntPtr.Zero)
                return Task.FromResult(-1);

            var pending = new Pending { Completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously) };
            int token;
            lock (s_pending)
            {
                token = ++s_nextToken & 0xffffff;
                s_pending[token] = pending;
            }

            try
            {
                // UIAlertControllerStyleAlert = 1.
                IntPtr alert = SendPtrPtrNInt(alertClass, Sel("alertControllerWithTitle:message:preferredStyle:"),
                                              NSStr(title ?? string.Empty), NSStr(message ?? string.Empty), 1);
                for (int i = 0; i < labels.Length; i++)
                {
                    IntPtr block = ObjCBlock.Create(
                        (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void>)&ActionInvoked,
                        (IntPtr)((token << 8) | i));
                    pending.Blocks.Add(block);
                    // UIAlertActionStyleDefault = 0, Cancel = 1.
                    IntPtr action = SendPtrNIntPtr(actionClass, Sel("actionWithTitle:style:handler:"),
                                                   NSStr(labels[i]), i == cancelIndex ? 1 : 0, block);
                    SendVoidPtr(alert, Sel("addAction:"), action);
                    if (i == preferredIndex)
                        SendVoidPtr(alert, Sel("setPreferredAction:"), action);
                }
                SendPresent(presenter, Sel("presentViewController:animated:completion:"), alert, 1, IntPtr.Zero);
            }
            catch (Exception)
            {
                Finish(token, -1);
            }
            return pending.Completion.Task;
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
        private static void ActionInvoked(IntPtr block, IntPtr action)
        {
            long context = (long)ObjCBlock.ContextOf(block);
            Finish((int)(context >> 8), (int)(context & 0xff));
        }

        private static void Finish(int token, int index)
        {
            Pending pending;
            lock (s_pending)
            {
                if (!s_pending.TryGetValue(token, out pending)) return;
                s_pending.Remove(token);
            }
            // UIKit has dismissed the alert by the time a handler runs, and a global block is never
            // retained by the runtime, so the blocks can go now.
            foreach (IntPtr b in pending.Blocks) ObjCBlock.Release(b);
            pending.Completion.TrySetResult(index);
        }

        /// <summary>The top-most presented controller of the key window, which is what can present.</summary>
        private static IntPtr RootViewController()
        {
            IntPtr app = Send(objc_getClass("UIApplication"), Sel("sharedApplication"));
            if (app == IntPtr.Zero) return IntPtr.Zero;
            IntPtr window = Send(app, Sel("keyWindow"));
            if (window == IntPtr.Zero)
            {
                IntPtr windows = Send(app, Sel("windows"));
                if (windows != IntPtr.Zero && SendNInt(windows, Sel("count")) > 0)
                    window = SendPtrNInt(windows, Sel("objectAtIndex:"), 0);
            }
            if (window == IntPtr.Zero) return IntPtr.Zero;
            IntPtr controller = Send(window, Sel("rootViewController"));
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
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr receiver, IntPtr selector);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendVoidPtr(IntPtr receiver, IntPtr selector, IntPtr arg);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern nint SendNInt(IntPtr receiver, IntPtr selector);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendPtrNInt(IntPtr receiver, IntPtr selector, nint arg);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendPtrPtrNInt(IntPtr receiver, IntPtr selector, IntPtr a, IntPtr b, nint c);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendPtrNIntPtr(IntPtr receiver, IntPtr selector, IntPtr a, nint b, IntPtr c);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendPresent(IntPtr receiver, IntPtr selector, IntPtr controller, byte animated, IntPtr completion);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendPtrUtf8(IntPtr receiver, IntPtr selector, [MarshalAs(UnmanagedType.LPUTF8Str)] string arg);
    }
}
