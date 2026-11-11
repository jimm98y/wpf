// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The mouse cursor.
//
// Wayland has no "set the cursor" call in the sense the other platforms mean it. A client owns the
// cursor image only while the pointer is over its surface, and it sets one by attaching a buffer to
// a dedicated cursor surface via wl_pointer.set_cursor -- which additionally requires the serial
// from the most recent wl_pointer.enter. That is a lot of machinery for "show an I-beam", and it is
// also how a client ends up with a cursor that does not match the user's theme.
//
// cursor-shape-v1 replaces all of it: the client names a shape from a standard enumeration and the
// COMPOSITOR draws it, correctly themed and correctly scaled on every output. It is the preferred
// path here -- but it only reached mutter in GNOME 47, so on GNOME 46 (Ubuntu 24.04) it is simply
// not advertised and every cursor would stay an arrow.
//
// So there is a second path: load the user's XCursor theme with libwayland-cursor and do the
// attach-and-set_cursor dance ourselves. The theme name and size come from
// LinuxDesktopSettings -- letting libwayland-cursor default them resolves to /usr/share/icons/
// default, which on Ubuntu inherits DMZ-White rather than the theme the user actually chose.
//
// A cursor of the app's own (a .cur or .ani, which Windows loads into a cursor of its own) is a third: its
// picture in a shared-memory buffer (wl_shm, one per cursor, made once), attached to the same cursor surface and
// given with its hot spot -- on every enter, as the others are.
//

using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using static MS.Internal.Interop.Wayland.WlProtocols;

namespace MS.Internal.Interop.Wayland
{
    /// <summary>struct wl_cursor_image: the pixels and hotspot of one cursor frame.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct WlCursorImage
    {
        public uint width, height;        // in BUFFER pixels
        public uint hotspot_x, hotspot_y; // likewise
        public uint delay;                // ms, for animated cursors
    }

    /// <summary>struct wl_cursor: the frames of one named cursor.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct WlCursor
    {
        public uint image_count;
        public WlCursorImage** images;
        public IntPtr name;
    }

    [SupportedOSPlatform("linux")]
    internal static unsafe class WaylandCursor
    {
        private const string LibCursor = "libwayland-cursor.so.0";

        [DllImport(LibCursor)]
        private static extern IntPtr wl_cursor_theme_load([MarshalAs(UnmanagedType.LPUTF8Str)] string? name, int size, IntPtr shm);

        [DllImport(LibCursor)] private static extern void wl_cursor_theme_destroy(IntPtr theme);

        [DllImport(LibCursor)]
        private static extern WlCursor* wl_cursor_theme_get_cursor(IntPtr theme, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);

        [DllImport(LibCursor)] private static extern IntPtr wl_cursor_image_get_buffer(WlCursorImage* image);

        private static IntPtr s_pointer;
        private static IntPtr s_shapeDevice;
        private static uint s_currentShape;

        // Theme path state (only used when cursor-shape-v1 is unavailable).
        private static IntPtr s_theme;
        private static int s_themeScale;
        private static bool s_themeUnavailable;
        private static IntPtr s_cursorSurface;
        private static string s_currentName = string.Empty;

        // the app's own cursor shown now, if one is; its buffers by key
        private static (IntPtr Buffer, int HotspotX, int HotspotY)? s_custom;
        private static readonly System.Collections.Generic.Dictionary<int, (IntPtr Buffer, int HotspotX, int HotspotY)> s_customBuffers = new();
        private const uint CustomShape = uint.MaxValue;   // s_currentShape while one is shown: any standard one differs

        internal static void AttachPointer(IntPtr pointer)
        {
            s_pointer = pointer;
            if (pointer == IntPtr.Zero) return;

            IntPtr manager = WaylandDisplay.CursorShapeManager;
            if (manager != IntPtr.Zero)
            {
                s_shapeDevice = Wl.Construct(manager, WP_CURSOR_SHAPE_MANAGER_GET_POINTER,
                    "wp_cursor_shape_device_v1", 1, WlArgument.NewId(), WlArgument.Ptr(pointer));
            }
        }

        /// <summary>
        /// Re-apply the current shape when the pointer enters a surface. Required, not cosmetic: the
        /// cursor is undefined on enter, so a client that does not set one shows whatever the
        /// previously-focused client happened to leave behind.
        /// </summary>
        internal static void OnPointerEnter(uint serial)
        {
            // Re-apply on every enter. The cursor is UNDEFINED when the pointer enters a surface, so
            // a client that does not set one shows whatever the previously-focused client left.
            if (s_custom is { } custom) { ApplyCustom(custom, serial); return; }
            Apply(s_currentShape == 0 ? CURSOR_SHAPE_DEFAULT : s_currentShape, serial);
        }

        /// <summary>The app's own cursor: <paramref name="bgra"/> (straight alpha), its hot spot; made into a
        /// buffer the first time <paramref name="key"/> is shown.</summary>
        internal static void SetCustomCursor(int key, byte[] bgra, int width, int height, int hotspotX, int hotspotY)
        {
            if (!s_customBuffers.TryGetValue(key, out var custom))
            {
                IntPtr buffer = MakeBuffer(bgra, width, height);
                if (buffer == IntPtr.Zero) return;
                s_customBuffers[key] = custom = (buffer, hotspotX, hotspotY);
            }
            // asked again on every move: given to the compositor once, and again on each enter (OnPointerEnter)
            if (s_custom is { } shown && shown.Buffer == custom.Buffer) return;
            s_custom = custom;
            s_currentShape = CustomShape;
            s_currentName = string.Empty;
            ApplyCustom(custom, WaylandInput.EnterSerial);
        }

        private static void ApplyCustom((IntPtr Buffer, int HotspotX, int HotspotY) custom, uint serial)
        {
            if (serial == 0 || s_pointer == IntPtr.Zero || !EnsureCursorSurface()) return;
            Wl.Request(s_cursorSurface, WL_SURFACE_SET_BUFFER_SCALE, WlArgument.Int(1));
            Wl.Request(s_cursorSurface, WL_SURFACE_ATTACH, WlArgument.Ptr(custom.Buffer), WlArgument.Int(0), WlArgument.Int(0));
            Wl.Request(s_cursorSurface, WL_SURFACE_DAMAGE, WlArgument.Int(0), WlArgument.Int(0), WlArgument.Int(int.MaxValue), WlArgument.Int(int.MaxValue));
            Wl.Request(s_cursorSurface, WL_SURFACE_COMMIT);
            Wl.Request(s_pointer, WL_POINTER_SET_CURSOR, WlArgument.UInt(serial), WlArgument.Ptr(s_cursorSurface),
                WlArgument.Int(custom.HotspotX), WlArgument.Int(custom.HotspotY));
            WaylandDisplay.Flush();
        }

        [DllImport("libc", SetLastError = true)] private static extern int memfd_create([MarshalAs(UnmanagedType.LPUTF8Str)] string name, uint flags);
        [DllImport("libc", SetLastError = true)] private static extern int ftruncate(int fd, long length);
        [DllImport("libc", SetLastError = true)] private static extern IntPtr mmap(IntPtr address, nuint length, int protection, int flags, int fd, long offset);
        [DllImport("libc")] private static extern int munmap(IntPtr address, nuint length);
        [DllImport("libc")] private static extern int close(int fd);

        // wl_shm's ARGB8888 (little-endian: B, G, R, A, premultiplied) from straight BGRA, in a pool of its own
        private static IntPtr MakeBuffer(byte[] bgra, int width, int height)
        {
            if (WaylandDisplay.Shm == IntPtr.Zero || width <= 0 || height <= 0 || bgra.Length < width * height * 4) return IntPtr.Zero;
            int size = width * height * 4;
            int fd = memfd_create("wpf-cursor", 1 /* MFD_CLOEXEC */);
            if (fd < 0) return IntPtr.Zero;
            try
            {
                if (ftruncate(fd, size) != 0) return IntPtr.Zero;
                IntPtr map = mmap(IntPtr.Zero, (nuint)size, 3 /* PROT_READ | PROT_WRITE */, 1 /* MAP_SHARED */, fd, 0);
                if (map == new IntPtr(-1)) return IntPtr.Zero;
                var pixels = new Span<byte>((void*)map, size);
                for (int i = 0; i < size; i += 4)
                {
                    int a = bgra[i + 3];
                    pixels[i] = (byte)(bgra[i] * a / 255);
                    pixels[i + 1] = (byte)(bgra[i + 1] * a / 255);
                    pixels[i + 2] = (byte)(bgra[i + 2] * a / 255);
                    pixels[i + 3] = (byte)a;
                }
                munmap(map, (nuint)size);
                IntPtr pool = Wl.Construct(WaylandDisplay.Shm, WL_SHM_CREATE_POOL, "wl_shm_pool", 1, WlArgument.NewId(), WlArgument.Int(fd), WlArgument.Int(size));
                if (pool == IntPtr.Zero) return IntPtr.Zero;
                IntPtr buffer = Wl.Construct(pool, WL_SHM_POOL_CREATE_BUFFER, "wl_buffer", 1, WlArgument.NewId(),
                    WlArgument.Int(0), WlArgument.Int(width), WlArgument.Int(height), WlArgument.Int(width * 4), WlArgument.UInt(WL_SHM_FORMAT_ARGB8888));
                // the buffer keeps the memory; the pool is not needed once it is made
                Wl.Request(pool, WL_SHM_POOL_DESTROY);
                return buffer;
            }
            finally { close(fd); }
        }

        /// <summary>Set the cursor from a WPF cursor name (see HwndMouseInputProvider).</summary>
        internal static void SetCursor(string name)
        {
            s_currentName = name ?? "Arrow";
            s_custom = null;
            uint shape = MapName(s_currentName);
            WaylandDisplay.LogSink?.Invoke($"cursor {s_currentName} (shape {shape}, was {s_currentShape}) serial={WaylandInput.EnterSerial} shapeDevice={s_shapeDevice != IntPtr.Zero} pointer={s_pointer != IntPtr.Zero}");
            if (shape != s_currentShape || s_shapeDevice == IntPtr.Zero)
            {
                s_currentShape = shape;
                Apply(shape, WaylandInput.EnterSerial);
            }
        }

        private static void Apply(uint shape, uint serial)
        {
            // A stale serial is silently ignored by the compositor, so there is nothing to do until
            // the pointer has actually entered one of our surfaces.
            if (serial == 0) return;

            if (s_shapeDevice != IntPtr.Zero)
            {
                try
                {
                    Wl.Request(s_shapeDevice, WP_CURSOR_SHAPE_DEVICE_SET_SHAPE,
                        WlArgument.UInt(serial), WlArgument.UInt(shape));
                    return;
                }
                catch { }
            }

            ApplyFromTheme(s_currentName, serial);
        }

        // ---- XCursor theme path (no cursor-shape-v1) ------------------------------------------

        /// <summary>
        /// Draw the cursor ourselves: pick the frame out of the user's XCursor theme, attach it to a
        /// dedicated cursor surface, and hand that surface to wl_pointer.set_cursor.
        /// </summary>
        private static void ApplyFromTheme(string name, uint serial)
        {
            if (s_themeUnavailable || s_pointer == IntPtr.Zero) return;

            // "None" hides the cursor: a NULL surface, which is the protocol's way of saying so.
            if (string.Equals(name, "None", StringComparison.Ordinal))
            {
                Wl.Request(s_pointer, WL_POINTER_SET_CURSOR,
                    WlArgument.UInt(serial), WlArgument.Ptr(IntPtr.Zero), WlArgument.Int(0), WlArgument.Int(0));
                WaylandDisplay.Flush();
                return;
            }

            // The theme is rasterised at a fixed pixel size, so it is loaded per backing scale and
            // reloaded if the pointer moves to a display with a different one.
            int scale = Math.Max(1, (int)Math.Round(WaylandDisplay.ScaleForSurface(WaylandInput.FocusSurface)));
            if (!EnsureTheme(scale)) return;

            WlCursor* cursor = null;
            foreach (string candidate in XCursorNames(name))
            {
                cursor = wl_cursor_theme_get_cursor(s_theme, candidate);
                if (cursor != null && cursor->image_count > 0) break;
                cursor = null;
            }
            if (cursor == null) { WaylandDisplay.LogSink?.Invoke($"cursor {name}: not in the theme '{LinuxDesktopSettings.CursorTheme}'"); return; }

            // Frame 0. Animated cursors (the "wait" spinner) would need a timer driven off
            // image->delay; a static first frame is the right trade for now.
            WlCursorImage* image = cursor->images[0];
            IntPtr buffer = wl_cursor_image_get_buffer(image);
            if (buffer == IntPtr.Zero) return;

            if (!EnsureCursorSurface()) return;

            Wl.Request(s_cursorSurface, WL_SURFACE_SET_BUFFER_SCALE, WlArgument.Int(scale));
            Wl.Request(s_cursorSurface, WL_SURFACE_ATTACH,
                WlArgument.Ptr(buffer), WlArgument.Int(0), WlArgument.Int(0));
            Wl.Request(s_cursorSurface, WL_SURFACE_DAMAGE,
                WlArgument.Int(0), WlArgument.Int(0), WlArgument.Int(int.MaxValue), WlArgument.Int(int.MaxValue));
            Wl.Request(s_cursorSurface, WL_SURFACE_COMMIT);

            // Hotspots are in buffer pixels; set_cursor wants surface-local coordinates.
            Wl.Request(s_pointer, WL_POINTER_SET_CURSOR,
                WlArgument.UInt(serial), WlArgument.Ptr(s_cursorSurface),
                WlArgument.Int((int)(image->hotspot_x / (uint)scale)),
                WlArgument.Int((int)(image->hotspot_y / (uint)scale)));
            WaylandDisplay.Flush();
        }

        private static bool EnsureTheme(int scale)
        {
            if (s_theme != IntPtr.Zero && s_themeScale == scale) return true;
            if (WaylandDisplay.Shm == IntPtr.Zero) { s_themeUnavailable = true; return false; }

            try
            {
                if (s_theme != IntPtr.Zero) wl_cursor_theme_destroy(s_theme);

                // The user's actual theme and size. Passing null here would fall back to the
                // "default" theme, which on Ubuntu inherits DMZ-White rather than what the user
                // selected -- the app's cursors would then differ from every other window's.
                s_theme = wl_cursor_theme_load(LinuxDesktopSettings.CursorTheme,
                                               LinuxDesktopSettings.CursorSize * scale,
                                               WaylandDisplay.Shm);
                s_themeScale = scale;
            }
            catch (DllNotFoundException)
            {
                WaylandDisplay.LogSink?.Invoke("libwayland-cursor not found; the cursor will not change shape.");
                s_themeUnavailable = true;
                s_theme = IntPtr.Zero;
            }
            return s_theme != IntPtr.Zero;
        }

        private static bool EnsureCursorSurface()
        {
            if (s_cursorSurface != IntPtr.Zero) return true;
            if (WaylandDisplay.Compositor == IntPtr.Zero) return false;
            s_cursorSurface = Wl.Construct(WaylandDisplay.Compositor, WL_COMPOSITOR_CREATE_SURFACE,
                "wl_surface", Wl.wl_proxy_get_version(WaylandDisplay.Compositor), WlArgument.NewId());
            return s_cursorSurface != IntPtr.Zero;
        }

        /// <summary>
        /// XCursor names to try for a WPF cursor, best first. Two spellings are needed because the
        /// freedesktop cursor-naming spec ("text", "pointer") postdates the legacy X11 names
        /// ("xterm", "hand2") and themes in the wild ship one, the other, or both.
        /// </summary>
        private static string[] XCursorNames(string name) => name switch
        {
            "IBeam" => new[] { "text", "xterm" },
            "Hand" => new[] { "pointer", "hand2", "hand1" },
            "Cross" => new[] { "crosshair", "cross" },
            "Wait" => new[] { "wait", "watch" },
            "AppStarting" => new[] { "progress", "left_ptr_watch", "half-busy" },
            "Help" => new[] { "help", "question_arrow", "whats_this" },
            "No" => new[] { "not-allowed", "crossed_circle", "forbidden" },
            "SizeAll" or "ScrollAll" => new[] { "all-scroll", "fleur", "move" },
            "SizeNS" or "ScrollNS" or "ScrollN" or "ScrollS" => new[] { "ns-resize", "sb_v_double_arrow", "v_double_arrow" },
            "SizeWE" or "ScrollWE" or "ScrollW" or "ScrollE" => new[] { "ew-resize", "sb_h_double_arrow", "h_double_arrow" },
            "SizeNWSE" => new[] { "nwse-resize", "size_fdiag", "bottom_right_corner" },
            "SizeNESW" => new[] { "nesw-resize", "size_bdiag", "bottom_left_corner" },
            "UpArrow" => new[] { "up-arrow", "sb_up_arrow", "default" },
            _ => new[] { "default", "left_ptr" },
        };

        /// <summary>
        /// WPF cursor names to cursor-shape-v1 shapes. The names are the ones
        /// HwndMouseInputProvider derives from System.Windows.Input.Cursors.
        /// </summary>
        private static uint MapName(string name) => name switch
        {
            "Arrow" or "AppStarting" => CURSOR_SHAPE_DEFAULT,
            "IBeam" => CURSOR_SHAPE_TEXT,
            "Hand" => CURSOR_SHAPE_POINTER,
            "Cross" => CURSOR_SHAPE_CROSSHAIR,
            "Wait" => CURSOR_SHAPE_WAIT,
            "Help" => CURSOR_SHAPE_HELP,
            "No" => CURSOR_SHAPE_NOT_ALLOWED,
            "SizeAll" => CURSOR_SHAPE_ALL_SCROLL,
            "SizeNS" => CURSOR_SHAPE_NS_RESIZE,
            "SizeWE" => CURSOR_SHAPE_EW_RESIZE,
            "SizeNWSE" => CURSOR_SHAPE_NWSE_RESIZE,
            "SizeNESW" => CURSOR_SHAPE_NESW_RESIZE,
            "ScrollAll" => CURSOR_SHAPE_ALL_SCROLL,
            "ScrollNS" => CURSOR_SHAPE_NS_RESIZE,
            "ScrollWE" => CURSOR_SHAPE_EW_RESIZE,
            "UpArrow" => CURSOR_SHAPE_DEFAULT,
            "None" => 0,
            _ => CURSOR_SHAPE_DEFAULT,
        };
    }
}
