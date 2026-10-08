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
using System.Drawing;

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

        // ---- where a top-level control is on screen ------------------------------------------------
        //
        // A control that puts a REAL native thing over its rectangle -- a web engine's view, an
        // <iframe> -- has to know which native window its form is presented in, and where. With the
        // driver's own top-level hosts (Win32Host, CocoaHost) the form IS the host window, at its
        // origin, and HostWindow says everything. It does not when the form is presented somewhere
        // else: WindowsFormsHost presents its container form inside a WPF window at the host
        // element's position, which moves with layout and scrolling; the browser head presents the
        // form, frame and all, onto a canvas. Those presenters register a placement for the
        // top-level control they present, and the overlay controls ask TryPlace instead of assuming
        // HostWindow at the origin.

        /// <summary>Where a presented top-level control is: its native <c>Window</c>; the driver
        /// screen point (<c>OriginX</c>, <c>OriginY</c>) that is drawn at device pixel (<c>X</c>,
        /// <c>Y</c>) of that window; the driver-unit to device-pixel <c>Scale</c>; and whether it is
        /// being shown at all.</summary>
        internal delegate (IntPtr Window, float X, float Y, int OriginX, int OriginY, float Scale, bool Visible) Placement();

        private static readonly Dictionary<Control, Placement> s_placements = new();

        /// <summary>Raised when a placement is registered, withdrawn or has moved: overlay controls
        /// that have not found their window yet try again, and the rest re-place themselves.</summary>
        internal static event Action<Control> PlacementChanged;

        /// <summary>Register (or, with null, withdraw) how <paramref name="topLevel"/> is presented.</summary>
        internal static void SetPlacement(Control topLevel, Placement placement)
        {
            if (topLevel == null) return;
            lock (s_lock)
            {
                if (placement == null) s_placements.Remove(topLevel);
                else s_placements[topLevel] = placement;
            }
            RaisePlacementChanged(topLevel);
        }

        /// <summary><paramref name="topLevel"/>'s placement moved (or was shown or hidden).</summary>
        internal static void RaisePlacementChanged(Control topLevel) => PlacementChanged?.Invoke(topLevel);

        /// <summary>Whether <paramref name="control"/> is inside <paramref name="topLevel"/> (or is it).</summary>
        internal static bool IsWithin(Control control, Control topLevel)
        {
            Control top = control;
            while (top?.Parent != null) top = top.Parent;
            return top != null && ReferenceEquals(top, topLevel);
        }

        /// <summary>Where <paramref name="control"/>'s rectangle is, in device pixels of the native
        /// window it is presented in. False when it is not presented anywhere yet.</summary>
        internal static bool TryPlace(Control control, out IntPtr window, out Rectangle device, out float scale, out bool visible)
        {
            window = IntPtr.Zero; device = Rectangle.Empty; scale = 1f; visible = false;
            if (control == null) return false;

            Control top = control;
            while (top.Parent != null) top = top.Parent;

            Placement placement;
            lock (s_lock) s_placements.TryGetValue(top, out placement);

            if (placement != null)
            {
                var p = placement();
                if (p.Window == IntPtr.Zero) return false;
                Point screen = control.PointToScreen(Point.Empty);
                scale = p.Scale > 0 ? p.Scale : 1f;
                window = p.Window;
                visible = p.Visible;
                device = new Rectangle(
                    (int)Math.Round(p.X + (screen.X - p.OriginX) * scale),
                    (int)Math.Round(p.Y + (screen.Y - p.OriginY) * scale),
                    (int)Math.Round(control.Width * scale),
                    (int)Math.Round(control.Height * scale));
                return true;
            }

            if (HostWindow == IntPtr.Zero) return false;

            // The driver's own top-level host: the form fills the host window from its origin.
            Point origin = Point.Empty;
            for (Control c = control; c is not null && c is not Form; c = c.Parent)
                origin.Offset(c.Left, c.Top);
            scale = HostScale <= 0 ? 1f : HostScale;
            window = HostWindow;
            visible = true;
            device = new Rectangle(
                (int)Math.Round(origin.X * scale), (int)Math.Round(origin.Y * scale),
                (int)Math.Round(control.Width * scale), (int)Math.Round(control.Height * scale));
            return true;
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
