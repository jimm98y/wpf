// Cross-framework scene bridge: content produced OUTSIDE the WPF visual tree (a WinForms control hosted
// by WindowsFormsHost, whose XplatUIWebGpu driver records the SAME SceneVisual type) is registered here
// and composited DIRECTLY into the WPF compositor's scene by WpfCompositionSink — no bitmap, no readback.
// Both WPF (MilcoreEngine) and WinForms emit Microsoft.Wpf.Interop.WebGpu.Composition.SceneVisual into
// the same WgpuSceneRenderer, so a hosted control's scene is just another child of the WPF root.

using System;
using System.Collections.Generic;
using System.Numerics;

namespace Microsoft.Wpf.Interop.WebGpu.Composition
{
    /// <summary>One hosted scene positioned in the WPF target's DEVICE-pixel space. <see cref="Scale"/>
    /// converts the hosted content's own units (WinForms points, 96dpi) to device pixels.</summary>
    public sealed class EmbeddedItem
    {
        public object Scene { get; set; }              // boxed SceneVisual (the hosted control's scene)
        public float DeviceX { get; set; }             // top-left of the host, in the WPF target's device pixels
        public float DeviceY { get; set; }
        public float DeviceW { get; set; }             // clip size in device pixels
        public float DeviceH { get; set; }

        /// <summary>Top-left of the clip, in device pixels RELATIVE to <see cref="DeviceX"/>/
        /// <see cref="DeviceY"/>. Non-zero when the host cuts into the content: a hosted control
        /// that reaches past its host's edge must stop there, and the part that survives may start
        /// partway into the window.</summary>
        public float ClipX { get; set; }
        public float ClipY { get; set; }
        public float Scale { get; set; } = 1f;         // hosted-unit -> device-pixel scale (typically DPI)

        /// <summary>The window this content belongs to (its HwndSource handle). The registry is
        /// process-wide but a scene belongs to exactly one window: without this every WPF window
        /// composited every other window's hosted content, at those coordinates. Opening a second
        /// window over an application that hosts WinForms -- SharpDevelop's Options dialog over its
        /// workbench -- drew the workbench's panes across the dialog. Zero means "no window
        /// recorded", which composites everywhere, as before.</summary>
        public IntPtr Window { get; set; }
    }

    /// <summary>Registry of hosted (non-WPF) scenes the WPF compositor should overlay. Populated by the
    /// WinForms hosts each frame; read by WpfCompositionSink.Present.</summary>
    /// <remarks>
    /// <para>ONE SET PER PUBLISHER. Every host that composites foreign content passes an object that
    /// identifies it as the owner of what it publishes, and <c>Set(owner, items)</c> replaces only
    /// that owner's set. It used to be one set per publishing thread, replaced whole: two independent
    /// hosting mechanisms on one page -- the gallery's own WinForms card and a real WindowsFormsHost,
    /// each with its own render tick -- erased each other every frame, and only whichever had
    /// published last was on screen. Before that it was one set for the process, so a second WPF UI
    /// thread hosting WinForms erased the first thread's controls. The compositors (one per UI
    /// thread) read every owner's set and keep what belongs to their window.</para>
    /// <para>The overloads without an owner publish under the calling thread, which is what they did
    /// before owners existed.</para>
    /// <para>The caret is per publisher too, and carries the window it is in. It used to be drawn into
    /// EVERY window being composited, so a second window showed the first one's blinking caret.</para>
    /// <para>Sets compose in the order their owners first published, so a host that is already on
    /// screen keeps its place under one that appears later.</para>
    /// </remarks>
    public static class EmbeddedContent
    {
        private sealed class Publisher
        {
            public Publisher(object owner) => Owner = owner;
            public readonly object Owner;
            public readonly List<EmbeddedItem> Items = new();
            public float CaretX, CaretY, CaretW, CaretH;
            public bool CaretVisible;
            public IntPtr CaretWindow;
        }

        /// <summary>The owner the thread-scoped overloads publish under: one per thread, equal by id.</summary>
        private sealed record ThreadOwner(int ThreadId);

        // In first-publish order (the composition order). A handful of entries at most, so a list
        // searched by owner beats a dictionary that would not keep the order.
        private static readonly List<Publisher> s_publishers = new();
        private static readonly object s_lock = new();
        private static bool s_logged;

        private static object ThreadKey() => new ThreadOwner(Environment.CurrentManagedThreadId);

        private static int IndexOf(object owner)
        {
            for (int i = 0; i < s_publishers.Count; i++)
                if (Equals(s_publishers[i].Owner, owner)) return i;
            return -1;
        }

        private static Publisher Of(object owner)
        {
            int i = IndexOf(owner);
            if (i >= 0) return s_publishers[i];
            var p = new Publisher(owner);
            s_publishers.Add(p);
            return p;
        }

        /// <summary>Set the hosted text caret's device-pixel rect and blink visibility (called each host
        /// tick). The compositor draws it on top of the hosted scenes, in every window: prefer the
        /// overload that names the window.</summary>
        public static void SetCaret(float x, float y, float w, float h, bool visible)
            => SetCaret(ThreadKey(), IntPtr.Zero, x, y, w, h, visible);

        /// <summary>Set the hosted text caret of <paramref name="window"/> (its HwndSource handle) for
        /// the calling thread. Zero composites it into every window, as the old overload did.</summary>
        public static void SetCaret(IntPtr window, float x, float y, float w, float h, bool visible)
            => SetCaret(ThreadKey(), window, x, y, w, h, visible);

        /// <summary>Set <paramref name="owner"/>'s hosted text caret in <paramref name="window"/>
        /// (zero: every window), leaving every other owner's alone.</summary>
        public static void SetCaret(object owner, IntPtr window, float x, float y, float w, float h, bool visible)
        {
            ArgumentNullException.ThrowIfNull(owner);
            lock (s_lock)
            {
                if (!visible && IndexOf(owner) < 0) return;
                Publisher p = Of(owner);
                p.CaretX = x; p.CaretY = y; p.CaretW = w; p.CaretH = h; p.CaretVisible = visible;
                p.CaretWindow = window;
                Prune(p);
            }
        }

        /// <summary>Replace the calling thread's set of hosted scenes (called once per host-update
        /// tick). Null or empty withdraws it. Hosts should prefer the overload that names an owner,
        /// so that two of them on one thread do not erase each other.</summary>
        public static void Set(IReadOnlyList<EmbeddedItem> items) => Set(ThreadKey(), items);

        /// <summary>Replace <paramref name="owner"/>'s set of hosted scenes, leaving every other
        /// owner's alone. Null or empty withdraws it.</summary>
        public static void Set(object owner, IReadOnlyList<EmbeddedItem> items)
        {
            ArgumentNullException.ThrowIfNull(owner);
            lock (s_lock)
            {
                if ((items == null || items.Count == 0) && IndexOf(owner) < 0) return;
                Publisher p = Of(owner);
                p.Items.Clear();
                if (items != null) p.Items.AddRange(items);
                Prune(p);
            }
        }

        /// <summary>Withdraw everything <paramref name="owner"/> published: its scenes and its caret.</summary>
        public static void Withdraw(object owner)
        {
            if (owner == null) return;
            lock (s_lock)
            {
                int i = IndexOf(owner);
                if (i >= 0) s_publishers.RemoveAt(i);
            }
        }

        // An owner with nothing left to show leaves the registry, so a host (or a UI thread) that is
        // gone does not leave an entry -- and a reference to itself -- behind for the process's life.
        private static void Prune(Publisher p)
        {
            if (p.Items.Count == 0 && !p.CaretVisible)
                s_publishers.Remove(p);
        }

        public static bool Any
        {
            get
            {
                lock (s_lock)
                {
                    foreach (Publisher p in s_publishers)
                        if (p.Items.Count > 0) return true;
                    return false;
                }
            }
        }

        /// <summary>Return a render root that composites the hosted scenes ON TOP of the WPF root (each
        /// translated to its device position, scaled to device pixels, and clipped to the host rect).
        /// Returns <paramref name="wpfRoot"/> unchanged when nothing is hosted.</summary>
        internal static SceneVisual Compose(SceneVisual wpfRoot, IntPtr window)
        {
            lock (s_lock)
            {
                if (s_publishers.Count == 0) return wpfRoot;
                SceneVisual? composite = null;
                foreach (Publisher p in s_publishers)
                {
                    foreach (EmbeddedItem it in p.Items)
                    {
                        if (it?.Scene is not SceneVisual sv) continue;
                        if (it.Window != IntPtr.Zero && window != IntPtr.Zero && it.Window != window)
                            continue;                       // belongs to a different window
                        if (!s_logged && System.Environment.GetEnvironmentVariable("WF_DIAG_EMBED") == "1")
                        { s_logged = true; System.Console.Error.WriteLine($"COMPOSE item dev=({it.DeviceX},{it.DeviceY}) size=({it.DeviceW}x{it.DeviceH}) scale={it.Scale}"); }
                        // Clip is evaluated in this node's LOCAL space (CollectVisual transforms it by the
                        // node's world matrix, which already includes Transform=Scale) — so it must be given
                        // in the hosted content's own (pre-scale) units, else it ends up scale× too large and
                        // never clips, letting the hosted scene leak past the host's bounds.
                        float s = it.Scale > 0 ? it.Scale : 1f;
                        var placed = new SceneVisual
                        {
                            Offset = new Vector2(it.DeviceX, it.DeviceY),
                            Clip = new Rect(it.ClipX / s, it.ClipY / s, it.DeviceW / s, it.DeviceH / s),
                            Transform = Matrix3x2.CreateScale(s),
                        };
                        placed.Children.Add(sv);
                        Root(ref composite, wpfRoot).Children.Add(placed);
                    }
                }
                foreach (Publisher p in s_publishers)
                {
                    if (!p.CaretVisible || p.CaretW <= 0 || p.CaretH <= 0) continue;
                    if (p.CaretWindow != IntPtr.Zero && window != IntPtr.Zero && p.CaretWindow != window) continue;
                    var caret = new SceneVisual();
                    caret.Content.Add(new GeometryFill(
                        new RectangleGeometry(new Rect(p.CaretX, p.CaretY, p.CaretW, p.CaretH)),
                        RgbaColor.FromBytes(0, 0, 0, 255)));
                    Root(ref composite, wpfRoot).Children.Add(caret);   // last -> on top of the hosted controls
                }
                return composite ?? wpfRoot;
            }
        }

        private static SceneVisual Root(ref SceneVisual? composite, SceneVisual wpfRoot)
        {
            if (composite == null)
            {
                composite = new SceneVisual();
                composite.Children.Add(wpfRoot);
            }
            return composite;
        }
    }
}
