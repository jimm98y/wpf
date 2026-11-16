// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// A frame presented in the same Core Animation transaction as the window change it is drawn for.
//
// While AppKit resizes a window -- a live resize, a zoom's animation -- it changes the window's frame
// in a transaction of its own and asks the application for its content at the new size in the middle
// of it (the window-did-resize notification). A CAMetalLayer drawable presented the usual way
// (-[MTLCommandBuffer presentDrawable:], scheduled with the GPU's work) reaches the screen on a later
// display refresh than the frame change does: for a refresh the window has its new frame and its old
// picture, and anything laid out relative to the window's size -- a caption centred in it -- jumps back
// and forth as the window grows. Apple's answer is CAMetalLayer.presentsWithTransaction with the
// command buffer committed, -waitUntilScheduled, and -[CAMetalDrawable present] on the thread that
// holds the transaction. wgpu always presents the scheduled way (wgpu-hal's Metal surface sets
// presentsWithTransaction off at every configure, and its switch for the other way is not part of
// wgpu-native's API).
//
// So, for a present made inside such a step (Begin/End around it), the command buffer's
// presentDrawable: is taken over: the drawable is kept instead of scheduled, and once wgpu has
// committed the buffer it is presented the transactional way. Any other present goes through the
// method as it was. The method is replaced on the command buffer class the device hands out, once.
//

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Microsoft.Wpf.Interop.WebGpu.Composition.Platform
{
    internal static unsafe class MacTransactionPresent
    {
        [ThreadStatic] private static int t_depth;
        [ThreadStatic] private static List<(IntPtr Buffer, IntPtr Drawable)> t_kept;

        private static IntPtr s_original;     // the class's own presentDrawable:
        private static int s_installed;       // 0 not tried, 1 installed, -1 unavailable
        private static readonly object s_lock = new();

        private static readonly bool s_disabled = Environment.GetEnvironmentVariable("WPF_MAC_TRANSACTION_PRESENT") == "0";

        /// <summary>Presents made on this thread until <see cref="End"/> are shown with the current
        /// Core Animation transaction (on macOS; elsewhere nothing changes).</summary>
        public static void Begin()
        {
            if (!OperatingSystem.IsMacOS() || s_disabled || !Install()) return;
            t_depth++;
        }

        public static void End()
        {
            if (!OperatingSystem.IsMacOS() || s_disabled || s_installed != 1 || t_depth == 0) return;
            t_depth--;
        }

        /// <summary>Whether a present on this thread now is to be shown with the transaction.</summary>
        internal static bool Active => t_depth > 0;

        /// <summary>After wgpu's present (which committed the command buffer): each drawable it handed to
        /// presentDrawable: on this thread, presented with the transaction once its work is scheduled.</summary>
        internal static void PresentKept()
        {
            List<(IntPtr Buffer, IntPtr Drawable)> kept = t_kept;
            if (kept == null || kept.Count == 0) return;
            foreach ((IntPtr buffer, IntPtr drawable) in kept)
            {
                try
                {
                    Send(buffer, Sel("waitUntilScheduled"));
                    IntPtr layer = Send(drawable, Sel("layer"));
                    if (layer != IntPtr.Zero) SendVoidBool(layer, Sel("setPresentsWithTransaction:"), true);
                    Send(drawable, Sel("present"));
                    if (layer != IntPtr.Zero) SendVoidBool(layer, Sel("setPresentsWithTransaction:"), false);
                }
                finally
                {
                    objc_release(drawable);
                    objc_release(buffer);
                }
            }
            kept.Clear();
        }

        private static bool Install()
        {
            if (s_installed != 0) return s_installed == 1;
            lock (s_lock)
            {
                if (s_installed != 0) return s_installed == 1;
                s_installed = -1;
                try
                {
                    // the class of the command buffers the system device makes: wgpu's are its
                    IntPtr device = MTLCreateSystemDefaultDevice();
                    if (device == IntPtr.Zero) return false;
                    IntPtr queue = Send(device, Sel("newCommandQueue"));
                    IntPtr buffer = queue == IntPtr.Zero ? IntPtr.Zero : Send(queue, Sel("commandBuffer"));
                    IntPtr cls = buffer == IntPtr.Zero ? IntPtr.Zero : object_getClass(buffer);
                    if (queue != IntPtr.Zero) objc_release(queue);
                    objc_release(device);
                    if (cls == IntPtr.Zero) return false;
                    IntPtr method = class_getInstanceMethod(cls, Sel("presentDrawable:"));
                    if (method == IntPtr.Zero) return false;
                    delegate* unmanaged<IntPtr, IntPtr, IntPtr, void> hook = &PresentDrawable;
                    s_original = method_setImplementation(method, (IntPtr)hook);
                    if (s_original == IntPtr.Zero) return false;
                    s_installed = 1;
                }
                catch (Exception) { s_installed = -1; }
                return s_installed == 1;
            }
        }

        [UnmanagedCallersOnly]
        private static void PresentDrawable(IntPtr self, IntPtr cmd, IntPtr drawable)
        {
            if (t_depth > 0 && drawable != IntPtr.Zero)
            {
                (t_kept ??= new()).Add((objc_retain(self), objc_retain(drawable)));
                return;
            }
            ((delegate* unmanaged<IntPtr, IntPtr, IntPtr, void>)s_original)(self, cmd, drawable);
        }

        private const string ObjC = "/usr/lib/libobjc.A.dylib";
        private const string Metal = "/System/Library/Frameworks/Metal.framework/Metal";

        [DllImport(Metal)] private static extern IntPtr MTLCreateSystemDefaultDevice();
        [DllImport(ObjC)] private static extern IntPtr sel_registerName(string name);
        [DllImport(ObjC)] private static extern IntPtr object_getClass(IntPtr obj);
        [DllImport(ObjC)] private static extern IntPtr class_getInstanceMethod(IntPtr cls, IntPtr sel);
        [DllImport(ObjC)] private static extern IntPtr method_setImplementation(IntPtr method, IntPtr imp);
        [DllImport(ObjC)] private static extern IntPtr objc_retain(IntPtr obj);
        [DllImport(ObjC)] private static extern void objc_release(IntPtr obj);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr r, IntPtr s);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendVoidBool(IntPtr r, IntPtr s, [MarshalAs(UnmanagedType.I1)] bool b);

        private static IntPtr Sel(string name) => sel_registerName(name);
    }
}
