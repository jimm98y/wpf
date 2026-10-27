// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// One WebGPU device for the process, on every head, shared by everything that draws and by the
// Direct3D shim (Documentation/d3d11-webgpu-shim.md, section 8):
//
// - every WPF composition sink, whatever its UI thread,
// - every WinForms window and popup, which also composite an ElementHost's WPF scene,
// - D3D11CreateDevice / Direct3DCreate9Ex devices, whatever thread makes them.
//
// A D3DImage frame of a shim surface is then always a GPU copy into a texture every renderer can
// sample, and a WPF scene hosted in a WinForms window is always drawn on the device its textures live
// on. The browser has one device anyway, made from navigator.gpu at boot.
//
// The device and its queue are used from more than one thread (a splash screen's UI thread, a decoder's
// D3D11 device); wgpu's device and queue are thread-safe, and each window keeps its own renderer. On
// wgpu's GL backend a context belongs to the thread that made it: Prepare makes the device on the first
// UI thread rather than on a render thread, which GL in a virtual machine (Parallels) needs.
//
// Every device is made as the shim wants it (D3DDevice.CreateGpu): the adapter's limits, the features
// D3D11 takes for granted, and validation errors logged rather than fatal. It is let go of when its
// last user is, and the next user makes a new one.
//

using System;
using System.Collections.Generic;

namespace Microsoft.Wpf.Interop.WebGpu.Composition
{
    internal static class ProcessGpu
    {
        private static readonly object s_lock = new();
        private static WgpuContext? s_ctx;
        private static int s_refs, s_thread;

        private static readonly bool s_trace = Environment.GetEnvironmentVariable("WPF_D3DIMAGE_TRACE") == "1";
        private static int s_events;
        private static void Trace(string what)
        {
            if (Direct3D.D3DLog.Trace || s_trace) Direct3D.D3DLog.Once("processgpu" + System.Threading.Interlocked.Increment(ref s_events), "process device: " + what);
        }

        private static WgpuContext Device()
        {
            if (s_ctx != null) return s_ctx;
            s_thread = Environment.CurrentManagedThreadId;
            Trace($"made on thread {s_thread}");
            s_ctx = Direct3D.D3DDevice.CreateGpu();
            return s_ctx;
        }

        /// <summary>Makes the device now, on the calling thread, if there is none yet (no reference taken):
        /// a WPF sink calls it from its UI thread, which its render thread would otherwise make it on.</summary>
        public static void Prepare()
        {
            lock (s_lock) Device();
        }

        /// <summary>The process's device, for one more user. Released with <see cref="Release"/>.</summary>
        public static WgpuContext Acquire()
        {
            lock (s_lock)
            {
                s_refs++;
                return Device();
            }
        }

        /// <summary>The device, for an adapter's description (no reference taken; made and kept if none).</summary>
        public static WgpuContext Peek()
        {
            lock (s_lock)
            {
                if (s_ctx == null) s_refs++;   // made for this: kept
                return Device();
            }
        }

        /// <summary>One user of <paramref name="ctx"/> is done; the last one disposes it. A device this did
        /// not make is left alone.</summary>
        public static void Release(WgpuContext ctx)
        {
            WgpuContext? gone = null;
            lock (s_lock)
            {
                if (!ReferenceEquals(ctx, s_ctx)) return;
                if (--s_refs > 0) return;
                gone = s_ctx;
                s_ctx = null;
                s_refs = 0;
            }
            Trace("let go of");
            gone.Dispose();
        }

        /// <summary>Whether <paramref name="ctx"/> is the process's device.</summary>
        public static bool Manages(WgpuContext ctx) { lock (s_lock) return ReferenceEquals(ctx, s_ctx); }

        /// <summary>For logs.</summary>
        public static string Describe() { lock (s_lock) return s_ctx == null ? "[no device]" : $"[device made on thread {s_thread}, {s_refs} users]"; }
    }
}
