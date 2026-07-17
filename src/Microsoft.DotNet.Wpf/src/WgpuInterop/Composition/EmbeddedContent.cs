// Cross-framework scene bridge: content produced OUTSIDE the WPF visual tree (a WinForms control hosted
// by WindowsFormsHost, whose XplatUIWebGpu driver records the SAME SceneVisual type) is registered here
// and composited DIRECTLY into the WPF compositor's scene by WpfCompositionSink — no bitmap, no readback.
// Both WPF (MilcoreEngine) and WinForms emit Microsoft.Wpf.Interop.WebGpu.Composition.SceneVisual into
// the same WgpuSceneRenderer, so a hosted control's scene is just another child of the WPF root.

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
        public float DeviceW { get; set; }             // host size in device pixels (clip bounds)
        public float DeviceH { get; set; }
        public float Scale { get; set; } = 1f;         // hosted-unit -> device-pixel scale (typically DPI)
    }

    /// <summary>Registry of hosted (non-WPF) scenes the WPF compositor should overlay. Populated by the
    /// WinForms host each frame; read by WpfCompositionSink.Present.</summary>
    public static class EmbeddedContent
    {
        private static readonly List<EmbeddedItem> s_items = new();
        private static readonly object s_lock = new();
        private static bool s_logged;

        // Text caret for hosted content (the backing scenes don't contain it — WinForms drives it via
        // CreateCaret/SetCaretPos/CaretVisible and the host blinks it). Device-pixel rect + blink state.
        private static float s_caretX, s_caretY, s_caretW, s_caretH;
        private static bool s_caretVisible;

        /// <summary>Set the hosted text caret's device-pixel rect and blink visibility (called each host
        /// tick). The compositor draws it on top of the hosted scenes.</summary>
        public static void SetCaret(float x, float y, float w, float h, bool visible)
        {
            lock (s_lock) { s_caretX = x; s_caretY = y; s_caretW = w; s_caretH = h; s_caretVisible = visible; }
        }

        /// <summary>Replace the current set of hosted scenes (called once per host-update tick).</summary>
        public static void Set(IReadOnlyList<EmbeddedItem> items)
        {
            lock (s_lock) { s_items.Clear(); if (items != null) s_items.AddRange(items); }
        }

        public static bool Any { get { lock (s_lock) return s_items.Count > 0; } }

        /// <summary>Return a render root that composites the hosted scenes ON TOP of the WPF root (each
        /// translated to its device position, scaled to device pixels, and clipped to the host rect).
        /// Returns <paramref name="wpfRoot"/> unchanged when nothing is hosted.</summary>
        internal static SceneVisual Compose(SceneVisual wpfRoot)
        {
            lock (s_lock)
            {
                if (s_items.Count == 0 && !s_caretVisible) return wpfRoot;
                var composite = new SceneVisual();
                composite.Children.Add(wpfRoot);
                foreach (EmbeddedItem it in s_items)
                {
                    if (it?.Scene is not SceneVisual sv) continue;
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
                        Clip = new Rect(0, 0, it.DeviceW / s, it.DeviceH / s),
                        Transform = Matrix3x2.CreateScale(s),
                    };
                    placed.Children.Add(sv);
                    composite.Children.Add(placed);
                }
                if (s_caretVisible && s_caretW > 0 && s_caretH > 0)
                {
                    var caret = new SceneVisual();
                    caret.Content.Add(new GeometryFill(
                        new RectangleGeometry(new Rect(s_caretX, s_caretY, s_caretW, s_caretH)),
                        RgbaColor.FromBytes(0, 0, 0, 255)));
                    composite.Children.Add(caret);   // last -> on top of the hosted controls
                }
                return composite;
            }
        }
    }
}
