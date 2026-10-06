// The mirror of EmbeddedContent. There, WPF owns the frame and a hosted WinForms control's scene is
// overlaid into it (WindowsFormsHost). Here the OUTER app is not WPF -- a WinForms app on the
// XplatUIWebGpu driver -- and a WPF element tree is hosted inside one of its controls (ElementHost).
//
// The WPF side is unchanged: the element tree lives under a normal HwndSource, so layout, hit-testing,
// input, capture, popups and animation all work exactly as in a standalone WPF app, and its window's
// MilTarget is decoded into a SceneVisual by MilcoreEngine as usual. The only difference is the last
// step: instead of WpfCompositionSink acquiring a swap-chain texture for that window and presenting to
// it, the host CLAIMS the window here, and the sink PUBLISHES the target's root scene instead. The
// embedder then composites it into its own frame at the hosting control's rect -- so the WPF content
// and the WinForms controls around it are one WebGPU render pass over one surface. No bitmap, no
// readback, no second swap chain, and no z-order fight between two native windows.

using System;
using System.Collections.Generic;

namespace Microsoft.Wpf.Interop.WebGpu.Composition
{
    /// <summary>Registry of WPF windows whose scene is composited by a non-WPF host rather than
    /// presented to a surface of their own. Written by the sink (publish) and by the embedder
    /// (claim/read); every member is thread-safe.</summary>
    public static class HostedWpfContent
    {
        private sealed class Hosted
        {
            public SceneVisual? Root;
            public int Width, Height;
            public int Version;          // bumped on every publish -> the host knows to re-present
        }

        private static readonly Dictionary<ulong, Hosted> s_byHwnd = new();
        private static readonly object s_lock = new();

        /// <summary>Take over presentation of the WPF window <paramref name="hwnd"/>: the sink stops
        /// presenting it and publishes its scene here instead. Call once, right after the HwndSource
        /// is created and before WPF renders its first frame -- otherwise that frame is presented to a
        /// surface of the window's own, which on a hidden window is wasted work and on a visible one
        /// is a flash of the content in the wrong place.</summary>
        public static void Claim(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return;
            lock (s_lock) s_byHwnd[(ulong)hwnd.ToInt64()] = new Hosted();
        }

        /// <summary>Stop hosting <paramref name="hwnd"/> (the host control was disposed). The window
        /// goes back to presenting itself, which is what a torn-down ElementHost wants: nothing else
        /// keeps a dead scene alive.</summary>
        public static void Release(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return;
            lock (s_lock) s_byHwnd.Remove((ulong)hwnd.ToInt64());
        }

        /// <summary>True when any window is hosted (lets a sink skip the per-target lookup entirely
        /// in the overwhelmingly common case of a plain WPF app).</summary>
        public static bool Any { get { lock (s_lock) return s_byHwnd.Count > 0; } }

        /// <summary>The scene most recently decoded for <paramref name="hwnd"/>, boxed as object so
        /// embedders (a WinForms control) need no compile-time dependency on the scene graph, plus its
        /// size in device pixels. Returns null until WPF has rendered its first frame.</summary>
        public static object? GetScene(IntPtr hwnd, out int width, out int height, out int version)
        {
            lock (s_lock)
            {
                if (s_byHwnd.TryGetValue((ulong)hwnd.ToInt64(), out Hosted? h) && h.Root != null)
                {
                    width = h.Width; height = h.Height; version = h.Version;
                    return h.Root;
                }
            }
            width = height = version = 0;
            return null;
        }

        internal static bool IsClaimed(ulong hwnd)
        {
            lock (s_lock) return s_byHwnd.ContainsKey(hwnd);
        }

        /// <summary>Hand the freshly decoded root scene for a claimed window to its host. Called from
        /// the sink's render pass in place of presenting the target.</summary>
        internal static void Publish(ulong hwnd, SceneVisual root, int width, int height)
        {
            lock (s_lock)
            {
                if (!s_byHwnd.TryGetValue(hwnd, out Hosted? h)) return;
                h.Root = root;
                h.Width = width;
                h.Height = height;
                h.Version++;
            }
        }
    }
}
