// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The Wayland half of the Linux web view (LinuxWebViewBackend drives WPE WebKit; read LinuxWebViewRegistration for
// why the two meet through AppContext): the overlay a page is shown in, which is a wl_subsurface of the window's own
// surface -- a native child view, as the WKWebView is on macOS -- and the input that falls on it.
//
//   * The engine renders into shared memory (WPEBackend-fdo's SHM export); each frame's pixels are copied into a
//     wl_shm buffer of ours and committed on the subsurface. A few buffers go round, each reused only once the
//     compositor has released it, so a frame never overwrites one that is on screen.
//   * The subsurface is desynchronised: a page's frames show as they come, not with the window's next present.
//   * Pointer events on the subsurface go to the overlay's handler instead of the window under it, and a press on
//     it gives the overlay the keyboard (as a click gives a child HWND the focus) until a press lands elsewhere.
//   * WebKit runs on GLib's main loop, which nothing else here iterates: hooks registered here run on each pass of
//     the Wayland loop, which then wakes at least every 10 ms while any is registered.
//

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using static MS.Internal.Interop.Wayland.WlProtocols;

namespace MS.Internal.Interop.Wayland
{
    [SupportedOSPlatform("linux")]
    internal static unsafe class WaylandWebViewSupport
    {
        private const string InvokeKey = "MS.Internal.Interop.WebView.Linux.Invoke";
        private const uint WL_SUBCOMPOSITOR_GET_SUBSURFACE = 1;
        private const uint WL_SUBSURFACE_DESTROY = 0, WL_SUBSURFACE_SET_POSITION = 1, WL_SUBSURFACE_PLACE_ABOVE = 2, WL_SUBSURFACE_SET_DESYNC = 5;
        private const uint WL_SURFACE_DESTROY_ = 0, WL_SURFACE_ATTACH_ = 1, WL_SURFACE_COMMIT_ = 6, WL_SURFACE_DAMAGE_BUFFER = 9;
        private const uint WL_BUFFER_DESTROY = 0;

        private sealed class ShmBuffer
        {
            public IntPtr Buffer, Map;
            public int Width, Height, Size;
            public bool Busy;
        }

        private sealed class Overlay
        {
            public int Id;
            public IntPtr Parent, Surface, Subsurface;
            public int X, Y, Width, Height;
            public bool Visible = true;
            public readonly List<ShmBuffer> Buffers = new();
            public ShmBuffer? Shown;
            public Action<int, int, int, int, int, int>? Mouse;   // kind, button, x, y, wheel, wheelH
            public Action<uint, uint, bool, uint, string?>? Key;  // keysym, scancode, down, modifiers, text
        }

        private static readonly Dictionary<int, Overlay> s_overlays = new();
        private static readonly Dictionary<IntPtr, ShmBuffer> s_bufferOf = new();
        private static readonly List<Action> s_pumps = new();
        private static int s_nextId;
        private static IntPtr* s_bufferListener;
        private static Overlay? s_keyTarget;

        /// <summary>Whether a hook asks for the loop to wake regularly (WebKit's GLib main loop).</summary>
        internal static bool HasPumps => s_pumps.Count > 0;

        /// <summary>Made available as the Wayland layer comes up.</summary>
        internal static void Register()
        {
            AppContext.SetData(InvokeKey, new Func<string, object[], object?>(Invoke));
            AppContext.SetData("WpfWebGpu.Wayland.SurfaceOf", new Func<IntPtr, IntPtr>(WaylandWindow.SurfaceOf));
        }

        private static object? Invoke(string command, object[] a)
        {
            switch (command)
            {
                case "createSubsurface": return Create((IntPtr)a[0], (int)a[1], (int)a[2], (int)a[3], (int)a[4]);
                case "moveSubsurface": Move((int)a[0], (int)a[1], (int)a[2], (int)a[3], (int)a[4]); return null;
                case "setSubsurfaceVisible": SetVisible((int)a[0], (bool)a[1]); return null;
                case "attachPixels": return AttachPixels((int)a[0], (IntPtr)a[1], (int)a[2], (int)a[3], (int)a[4]);
                case "destroySubsurface": Destroy((int)a[0]); return null;
                case "getDisplay": return WaylandDisplay.Display;
                case "setInput": SetInput((int)a[0], (Action<int, int, int, int, int, int>?)a[1], (Action<uint, uint, bool, uint, string?>?)a[2]); return null;
                case "addPump": s_pumps.Add((Action)a[0]); return null;
                case "removePump": s_pumps.Remove((Action)a[0]); return null;
                case "focus": if (s_overlays.TryGetValue((int)a[0], out Overlay? f)) s_keyTarget = f; return null;
                case "captureFrame": return null;
            }
            throw new NotSupportedException("web view command " + command);
        }

        private static int Create(IntPtr parent, int x, int y, int width, int height)
        {
            if (WaylandDisplay.Subcompositor == IntPtr.Zero || WaylandDisplay.Shm == IntPtr.Zero || parent == IntPtr.Zero) return 0;
            IntPtr surface = Wl.Construct(WaylandDisplay.Compositor, WL_COMPOSITOR_CREATE_SURFACE, "wl_surface",
                Wl.wl_proxy_get_version(WaylandDisplay.Compositor), WlArgument.NewId());
            if (surface == IntPtr.Zero) return 0;
            IntPtr sub = Wl.Construct(WaylandDisplay.Subcompositor, WL_SUBCOMPOSITOR_GET_SUBSURFACE, "wl_subsurface", 1,
                WlArgument.NewId(), WlArgument.Ptr(surface), WlArgument.Ptr(parent));
            if (sub == IntPtr.Zero) { Wl.Destroy(surface, WL_SURFACE_DESTROY_); return 0; }
            Wl.Request(sub, WL_SUBSURFACE_SET_DESYNC);
            Wl.Request(sub, WL_SUBSURFACE_PLACE_ABOVE, WlArgument.Ptr(parent));
            Wl.Request(sub, WL_SUBSURFACE_SET_POSITION, WlArgument.Int(x), WlArgument.Int(y));
            var o = new Overlay { Id = ++s_nextId, Parent = parent, Surface = surface, Subsurface = sub, X = x, Y = y, Width = width, Height = height };
            s_overlays[o.Id] = o;
            // the position takes effect with the parent's next commit (its next frame)
            Wl.Request(parent, WL_SURFACE_COMMIT_);
            WaylandDisplay.Flush();
            WaylandDisplay.LogSink?.Invoke($"web view overlay {o.Id}: subsurface of 0x{parent:x} at ({x},{y}) {width}x{height}");
            return o.Id;
        }

        private static void Move(int id, int x, int y, int width, int height)
        {
            if (!s_overlays.TryGetValue(id, out Overlay? o)) return;
            bool moved = x != o.X || y != o.Y;
            o.X = x; o.Y = y; o.Width = width; o.Height = height;
            if (moved)
            {
                Wl.Request(o.Subsurface, WL_SUBSURFACE_SET_POSITION, WlArgument.Int(x), WlArgument.Int(y));
                Wl.Request(o.Parent, WL_SURFACE_COMMIT_);
                WaylandDisplay.Flush();
            }
        }

        private static void SetVisible(int id, bool visible)
        {
            if (!s_overlays.TryGetValue(id, out Overlay? o) || o.Visible == visible) return;
            o.Visible = visible;
            // hidden: no buffer (a subsurface without one is not shown); shown again with the frame it had
            Wl.Request(o.Surface, WL_SURFACE_ATTACH_, WlArgument.Ptr(visible && o.Shown != null ? o.Shown.Buffer : IntPtr.Zero), WlArgument.Int(0), WlArgument.Int(0));
            if (visible && o.Shown != null)
                Wl.Request(o.Surface, WL_SURFACE_DAMAGE_BUFFER, WlArgument.Int(0), WlArgument.Int(0), WlArgument.Int(int.MaxValue), WlArgument.Int(int.MaxValue));
            Wl.Request(o.Surface, WL_SURFACE_COMMIT_);
            WaylandDisplay.Flush();
        }

        /// <summary>A frame: <paramref name="height"/> rows of premultiplied BGRA (ARGB8888 in memory) at
        /// <paramref name="stride"/> bytes, copied into a free buffer of the overlay's and committed. False when every
        /// buffer is still the compositor's (the frame is dropped; the next one comes).</summary>
        private static bool AttachPixels(int id, IntPtr pixels, int width, int height, int stride)
        {
            if (!s_overlays.TryGetValue(id, out Overlay? o) || width <= 0 || height <= 0) return false;
            ShmBuffer? target = null;
            foreach (ShmBuffer b in o.Buffers)
                if (!b.Busy && b.Width == width && b.Height == height) { target = b; break; }
            if (target == null)
            {
                // buffers of another size are let go as they come back; a new one is made, up to three
                o.Buffers.RemoveAll(b => { if (b.Busy || (b.Width == width && b.Height == height)) return false; Free(b); return true; });
                if (o.Buffers.Count >= 3) return false;
                target = NewBuffer(width, height);
                if (target == null) return false;
                o.Buffers.Add(target);
            }
            int row = width * 4;
            for (int y = 0; y < height; y++)
                Buffer.MemoryCopy((byte*)pixels + (long)y * stride, (byte*)target.Map + (long)y * row, row, row);
            target.Busy = true;
            o.Shown = target;
            if (!o.Visible) { target.Busy = false; return true; }
            Wl.Request(o.Surface, WL_SURFACE_ATTACH_, WlArgument.Ptr(target.Buffer), WlArgument.Int(0), WlArgument.Int(0));
            Wl.Request(o.Surface, WL_SURFACE_DAMAGE_BUFFER, WlArgument.Int(0), WlArgument.Int(0), WlArgument.Int(width), WlArgument.Int(height));
            Wl.Request(o.Surface, WL_SURFACE_COMMIT_);
            WaylandDisplay.Flush();
            return true;
        }

        [DllImport("libc", SetLastError = true)] private static extern int memfd_create([MarshalAs(UnmanagedType.LPUTF8Str)] string name, uint flags);
        [DllImport("libc", SetLastError = true)] private static extern int ftruncate(int fd, long length);
        [DllImport("libc", SetLastError = true)] private static extern IntPtr mmap(IntPtr address, nuint length, int protection, int flags, int fd, long offset);
        [DllImport("libc")] private static extern int munmap(IntPtr address, nuint length);
        [DllImport("libc")] private static extern int close(int fd);

        private static ShmBuffer? NewBuffer(int width, int height)
        {
            int size = width * height * 4;
            int fd = memfd_create("wpf-webview", 1 /* MFD_CLOEXEC */);
            if (fd < 0) return null;
            try
            {
                if (ftruncate(fd, size) != 0) return null;
                IntPtr map = mmap(IntPtr.Zero, (nuint)size, 3 /* PROT_READ | PROT_WRITE */, 1 /* MAP_SHARED */, fd, 0);
                if (map == new IntPtr(-1)) return null;
                IntPtr pool = Wl.Construct(WaylandDisplay.Shm, WL_SHM_CREATE_POOL, "wl_shm_pool", 1, WlArgument.NewId(), WlArgument.Int(fd), WlArgument.Int(size));
                if (pool == IntPtr.Zero) { munmap(map, (nuint)size); return null; }
                IntPtr buffer = Wl.Construct(pool, WL_SHM_POOL_CREATE_BUFFER, "wl_buffer", 1, WlArgument.NewId(),
                    WlArgument.Int(0), WlArgument.Int(width), WlArgument.Int(height), WlArgument.Int(width * 4), WlArgument.UInt(WL_SHM_FORMAT_ARGB8888));
                Wl.Request(pool, WL_SHM_POOL_DESTROY);
                if (buffer == IntPtr.Zero) { munmap(map, (nuint)size); return null; }
                if (s_bufferListener == null)
                    s_bufferListener = Wl.Vtable((IntPtr)(delegate* unmanaged<IntPtr, IntPtr, void>)&OnBufferRelease);
                Wl.wl_proxy_add_listener(buffer, s_bufferListener, IntPtr.Zero);
                var b = new ShmBuffer { Buffer = buffer, Map = map, Width = width, Height = height, Size = size };
                s_bufferOf[buffer] = b;
                return b;
            }
            finally { close(fd); }
        }

        [UnmanagedCallersOnly]
        private static void OnBufferRelease(IntPtr data, IntPtr buffer)
        {
            if (s_bufferOf.TryGetValue(buffer, out ShmBuffer? b)) b.Busy = false;
        }

        private static void Free(ShmBuffer b)
        {
            s_bufferOf.Remove(b.Buffer);
            Wl.Destroy(b.Buffer, WL_BUFFER_DESTROY);
            munmap(b.Map, (nuint)b.Size);
        }

        private static void Destroy(int id)
        {
            if (!s_overlays.Remove(id, out Overlay? o)) return;
            if (s_keyTarget == o) s_keyTarget = null;
            Wl.Destroy(o.Subsurface, WL_SUBSURFACE_DESTROY);
            Wl.Destroy(o.Surface, WL_SURFACE_DESTROY_);
            foreach (ShmBuffer b in o.Buffers) Free(b);
            o.Buffers.Clear();
            WaylandDisplay.Flush();
        }

        private static void SetInput(int id, Action<int, int, int, int, int, int>? mouse, Action<uint, uint, bool, uint, string?>? key)
        {
            if (!s_overlays.TryGetValue(id, out Overlay? o)) return;
            o.Mouse = mouse;
            o.Key = key;
        }

        // ---- input -------------------------------------------------------------------------------

        /// <summary>A pointer event on an overlay: its handler's, not the window's. A press on one gives it the keyboard; a
        /// press anywhere else takes the keyboard back.</summary>
        internal static bool TryMouse(in WaylandMouseMessage m)
        {
            if (s_overlays.Count == 0) return false;
            Overlay? hit = null;
            foreach (Overlay o in s_overlays.Values)
                if (o.Surface == m.Surface) { hit = o; break; }
            if (m.Kind == WaylandMouseKind.ButtonDown) s_keyTarget = hit;
            if (hit == null || hit.Mouse == null) return false;
            try { hit.Mouse((int)m.Kind, m.Button, m.X, m.Y, m.Wheel, m.WheelHorizontal); } catch (Exception) { }
            return true;
        }

        /// <summary>A key while an overlay has the keyboard: its handler's.</summary>
        internal static bool TryKey(in WaylandKeyMessage k)
        {
            Overlay? o = s_keyTarget;
            if (o?.Key == null || !s_overlays.ContainsKey(o.Id)) return false;
            try { o.Key(k.Keysym, k.Scancode, k.IsDown, (uint)k.Modifiers, k.Characters); } catch (Exception) { }
            return true;
        }

        /// <summary>The hooks, on each pass of the Wayland loop (the UI thread).</summary>
        internal static void Pump()
        {
            if (s_pumps.Count == 0) return;
            foreach (Action pump in s_pumps.ToArray())
                try { pump(); } catch (Exception) { }
        }
    }
}
