// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// One WebGPU device per UI thread, shared by everything that draws on that thread and by the
// Direct3D shim devices made there (Documentation/d3d11-webgpu-shim.md, section 8):
//
// - the thread's WPF composition sink,
// - its WinForms windows and popups, which also composite an ElementHost's WPF scene,
// - D3D11CreateDevice / Direct3DCreate9Ex devices created on it.
//
// A D3DImage frame of a shim surface made on the thread is then a GPU copy into a texture that every
// renderer showing it can sample. That covers a standalone WPF window and a WPF scene inside a WinForms
// window alike. A thread that composites nothing (a decoder's worker) has no device of its own: its
// shim devices use the primary device, the first WPF sink's thread's, so that shared handles still meet
// the D3DImage on the UI thread. A shim surface shown on a thread other than its device's goes as
// pixels (the copy path).
//
// Every device is made as the shim wants it (D3DDevice.CreateGpu): the adapter's limits, the features
// D3D11 takes for granted, and validation errors logged rather than fatal. A device is let go of when
// its last user is.
//
// WPF_D3DIMAGE_SHARE=0: shim devices get a device of their own (none is shared with a compositor).
//

using System;
using System.Collections.Generic;
using Microsoft.Wpf.Interop.WebGpu.Direct3D;

namespace Microsoft.Wpf.Interop.WebGpu.Composition
{
    internal static class ThreadGpu
    {
        private sealed class Entry
        {
            public WgpuContext Ctx = null!;
            public int Thread;
            public int Refs;
            public int HostWindows;      // WinForms windows drawing with it
        }

        private static readonly object s_lock = new();
        private static readonly Dictionary<int, Entry> s_byThread = new();
        private static readonly Dictionary<WgpuContext, Entry> s_byCtx = new(ReferenceEqualityComparer.Instance);
        private static Entry? s_primary;
        private static Entry? s_unshared;      // WPF_D3DIMAGE_SHARE=0: the shim devices' own
        internal static readonly bool Unshared = Environment.GetEnvironmentVariable("WPF_D3DIMAGE_SHARE") == "0";

        private static int s_events;
        private static void Trace(string what) { if (Direct3D.D3DLog.Trace || s_trace) Direct3D.D3DLog.Once("threadgpu" + System.Threading.Interlocked.Increment(ref s_events), "thread device: " + what); }
        private static readonly bool s_trace = Environment.GetEnvironmentVariable("WPF_D3DIMAGE_TRACE") == "1";

        private static Entry Create(int thread)
        {
            Trace($"new device for thread {thread} (asked on thread {Environment.CurrentManagedThreadId})");
            var e = new Entry { Ctx = D3DDevice.CreateGpu(), Thread = thread };
            s_byCtx[e.Ctx] = e;
            return e;
        }

        private static Entry ForThread(int thread)
        {
            if (!s_byThread.TryGetValue(thread, out var e)) s_byThread[thread] = e = Create(thread);
            return e;
        }

        /// <summary>Makes the calling thread's device now, on the thread itself (no reference taken). A WPF
        /// sink asks for its device from its render thread; made there, the device's GL context was made
        /// on a thread its WinForms windows never present from, and on the GL backend (Parallels) the
        /// windows then stopped presenting now and then.</summary>
        public static void Prepare()
        {
            if (Unshared) return;
            lock (s_lock) ForThread(Environment.CurrentManagedThreadId);
        }

        /// <summary>The calling thread's device, for a compositor on it (a WPF sink: <paramref name="wpfSink"/>,
        /// which makes the thread the primary one if none is yet). Released with <see cref="Release"/>.</summary>
        public static WgpuContext Acquire(bool wpfSink = false) => Acquire(Environment.CurrentManagedThreadId, wpfSink);

        /// <summary>UI thread <paramref name="thread"/>'s device, asked from another thread (a WPF sink makes
        /// its device on its render thread, for the UI thread it composites).</summary>
        public static WgpuContext Acquire(int thread, bool wpfSink)
        {
            lock (s_lock)
            {
                var e = ForThread(thread);
                e.Refs++;
                if (wpfSink && s_primary == null) s_primary = e;
                return e.Ctx;
            }
        }

        /// <summary>The calling thread's device, for a WinForms window (which may composite hosted WPF
        /// scenes). Released with <see cref="Release"/>.</summary>
        public static WgpuContext AcquireForHostWindow()
        {
            lock (s_lock)
            {
                var e = ForThread(Environment.CurrentManagedThreadId);
                e.Refs++;
                e.HostWindows++;
                return e.Ctx;
            }
        }

        /// <summary>The device for a Direct3D shim device made on the calling thread: the thread's when it
        /// has one, else the primary one, else a new one for this thread. Released with
        /// <see cref="Release"/>.</summary>
        public static WgpuContext AcquireForShim()
        {
            lock (s_lock)
            {
                Entry e;
                if (Unshared) e = s_unshared ??= Create(0);
                else if (!s_byThread.TryGetValue(Environment.CurrentManagedThreadId, out e!))
                    e = s_primary is { Refs: > 0 } p ? p : ForThread(Environment.CurrentManagedThreadId);
                e.Refs++;
                Trace($"shim device on thread {Environment.CurrentManagedThreadId} uses thread {e.Thread}'s device");
                return e.Ctx;
            }
        }

        /// <summary>A device for an adapter's description (no reference taken): the calling thread's,
        /// the primary, or any.</summary>
        public static WgpuContext Peek()
        {
            lock (s_lock)
            {
                if (s_byThread.TryGetValue(Environment.CurrentManagedThreadId, out var e)) return e.Ctx;
                if (s_primary != null) return s_primary.Ctx;
                foreach (var any in s_byCtx.Values) return any.Ctx;
                // nothing yet: this thread's, kept
                e = ForThread(Environment.CurrentManagedThreadId);
                e.Refs++;
                return e.Ctx;
            }
        }

        /// <summary>One user of <paramref name="ctx"/> is done (<paramref name="hostWindow"/>: a WinForms
        /// window's). The last one disposes it. A device none of these made is left alone.</summary>
        public static void Release(WgpuContext ctx, bool hostWindow = false)
        {
            Entry? gone = null;
            lock (s_lock)
            {
                if (!s_byCtx.TryGetValue(ctx, out var e)) return;
                if (hostWindow) e.HostWindows--;
                if (--e.Refs > 0) return;
                s_byCtx.Remove(ctx);
                if (s_byThread.TryGetValue(e.Thread, out var t) && t == e) s_byThread.Remove(e.Thread);
                if (s_primary == e) s_primary = null;
                if (s_unshared == e) s_unshared = null;
                gone = e;
            }
            Trace($"thread {gone.Thread}'s device let go of");
            gone.Ctx.Dispose();
        }

        /// <summary>Whether a WinForms window draws with a device other than <paramref name="ctx"/>: a
        /// hosted WPF scene drawn there cannot show <paramref name="ctx"/>'s textures.</summary>
        public static bool HostWindowsOffDevice(WgpuContext ctx)
        {
            lock (s_lock)
            {
                foreach (var e in s_byCtx.Values) if (e.HostWindows > 0 && !ReferenceEquals(e.Ctx, ctx)) return true;
                return false;
            }
        }

        /// <summary>For logs: every device, its thread, users and windows.</summary>
        public static string Describe()
        {
            lock (s_lock)
            {
                var sb = new System.Text.StringBuilder();
                foreach (var e in s_byCtx.Values) sb.Append($"[thread {e.Thread} refs {e.Refs} windows {e.HostWindows}{(e == s_primary ? " primary" : "")}] ");
                return sb.ToString();
            }
        }

        /// <summary>Whether <paramref name="ctx"/> is one of these (managed here).</summary>
        public static bool Manages(WgpuContext ctx) { lock (s_lock) return s_byCtx.ContainsKey(ctx); }
    }
}
