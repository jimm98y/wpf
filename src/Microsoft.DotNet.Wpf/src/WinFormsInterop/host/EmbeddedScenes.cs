// The seam that lets content which is NOT a WinForms control contribute to the frame — today a WPF
// element tree hosted by ElementHost (System.Windows.Forms.Integration). The per-OS hosts ask this
// for extra scenes on every present and append them AFTER the driver's own window scenes, so the
// hosted content draws over the control it occupies and the whole window is still ONE WebGPU render
// pass over ONE surface.
//
// Nothing here is WPF-specific, and in a plain WinForms app the registry is empty and every call is
// a no-op.

using System;
using System.Collections.Generic;

namespace System.Windows.Forms
{
    /// <summary>One contributor of non-WinForms content, registered for the lifetime of the control
    /// hosting it.</summary>
    internal interface IEmbeddedScene
    {
        /// <summary>Run the embedded framework's own work for this frame (layout, animation, render).
        /// Called once per host tick, before the present.</summary>
        void Pump();

        /// <summary>Bumped whenever the embedded content changes. The hosts present-on-change, so
        /// without this an animation driven by the embedded framework alone would never repaint.</summary>
        int Version { get; }

        /// <summary>Add this frame's scenes, in the host's FORM-point space — the same space the
        /// driver's window scenes are handed to WgpuPresenter in. (ox, oy) is the form's origin in
        /// the driver's screen space, so a contributor can map its own rect the way the driver does.</summary>
        void Collect(int ox, int oy, List<(object Scene, int X, int Y)> into);
    }

    internal static class EmbeddedScenes
    {
        private static readonly List<IEmbeddedScene> s_items = new();
        private static readonly object s_lock = new();

        /// <summary>The host's native window and backing scale, published once the window exists. A
        /// contributor that needs a real OS window of its own (ElementHost parents WPF's HwndSource
        /// there) has no other way to reach them — the driver's Hwnds are managed handles, not OS
        /// windows. <see cref="HostWindowReady"/> fires when they become valid.</summary>
        internal static IntPtr HostWindow;
        internal static float HostScale = 1f;
        internal static event Action HostWindowReady;

        internal static void PublishHostWindow(IntPtr window, float scale)
        {
            HostWindow = window;
            HostScale = scale;
            HostWindowReady?.Invoke();
        }

        internal static void Register(IEmbeddedScene item)
        {
            if (item == null) return;
            lock (s_lock) { if (!s_items.Contains(item)) s_items.Add(item); }
        }

        internal static void Unregister(IEmbeddedScene item)
        {
            if (item == null) return;
            lock (s_lock) s_items.Remove(item);
        }

        /// <summary>Let every contributor run its own frame work before the present.</summary>
        internal static void Pump()
        {
            IEmbeddedScene[] items = Snapshot();
            if (items == null) return;
            foreach (IEmbeddedScene c in items) c.Pump();
        }

        /// <summary>Summed contributor versions, folded into the hosts' present-on-change check.</summary>
        internal static int CurrentVersion()
        {
            IEmbeddedScene[] items = Snapshot();
            if (items == null) return 0;
            int v = 0;
            foreach (IEmbeddedScene c in items) v += c.Version;
            return v;
        }

        /// <summary>The extra scenes for this frame, or null when nothing is embedded.</summary>
        internal static List<(object Scene, int X, int Y)> Get(int ox, int oy)
        {
            IEmbeddedScene[] items = Snapshot();
            if (items == null) return null;
            var list = new List<(object, int, int)>(items.Length);
            foreach (IEmbeddedScene c in items) c.Collect(ox, oy, list);
            return list.Count > 0 ? list : null;
        }

        // Copy under the lock: Pump/Collect run arbitrary embedded-framework code, which can register
        // or dispose a host (WPF layout creating or tearing one down) and mutate the list underneath.
        private static IEmbeddedScene[] Snapshot()
        {
            lock (s_lock) return s_items.Count == 0 ? null : s_items.ToArray();
        }
    }
}
