// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Drawing.Drawing2D;

namespace System.Drawing.WebGpuBackend.Gdip
{
    internal static partial class GpPen
    {
        /// <summary>GpPath::Widen @18008ab68 (GraphicsPath.Widen): the widened path in the space
        /// <paramref name="matrix"/> maps to, winding; null when GDI+ fails it. <paramref name="solid"/>
        /// drops the dashes first, as GpPath::IsOutlineVisible @180089ce0 does.</summary>
        public static partial GpPath Widen (GpPath path, Pen pen, GpMatrix matrix, float flatness, bool solid)
        {
            DpPen dp = DpPen.From (pen);
            if (solid) { dp.DashStyle = 0; dp.DashArray = null; }
            GpPath r = GetWidenedPath (path, dp, matrix, flatness, 96f);
            if (r != null) r.FillMode = FillMode.Winding;
            return r;
        }

        /// <summary>GdipGetPathWorldBounds with a pen: GpPath::GetBounds @18001c020, the control
        /// box grown by the pen's reach (not the widened outline).</summary>
        public static partial RectangleF WidenedBounds (GpPath path, Pen pen, GpMatrix? matrix)
            => GpStroke.Bounds (path, matrix, DpPen.From (pen), 96f);

        /// <summary>GpPath::GetWidenedPath @180089680: the outline of the stroke, in the space
        /// <paramref name="m"/> maps to (device space for drawing). An Inset pen widens each closed
        /// figure at twice the width and keeps what lies inside the path.</summary>
        public static GpPath GetWidenedPath (GpPath path, DpPen pen, GpMatrix? m, float flatness, float dpi)
        {
            GpMatrix mm = m ?? GpMatrix.CreateIdentity ();
            if (pen.Alignment != 1) return GetWidenedPathInternal (path, pen, mm, flatness, false);
            return null;   // Inset: not yet
        }

        /// <summary>GpPath::GetWidenedPathInternal @18001c6e0.</summary>
        static GpPath GetWidenedPathInternal (GpPath path, DpPen pen, GpMatrix m, float flatness, bool inset)
        {
            GpMatrix inv = m;
            if (!inv.Invert ()) return null;
            GpPath p = path.Clone ();
            // Flattened (or transformed) in device space, then brought back.
            p.Flatten (m, flatness);
            if (p.Count > 0 && inv.Complexity != 0) {
                for (int i = 0; i < p.Count; i++) p.Points [i] = inv.Transform (p.Points [i]);
            }
            GpPath caps = null;
            if (pen.StartCap == 0xff || pen.EndCap == 0xff || (pen.StartCap & 0xf0) != 0 || (pen.EndCap & 0xf0) != 0) {
                return null;   // GpEndCapCreator: not yet
            }
            if (pen.DashStyle != 0 && p.Count > 0) {
                GpPath d = CreateDashedPath (p, pen, m, 0f, 0f, inset ? 0.5f : 1f, true);
                if (d == null) return null;
                p = d;
            }
            GpPath wide;
            if (p.Count < 1) wide = caps;
            else {
                var w = new GpPathWidener (p, pen, m, 0f, 0f, inset);
                wide = w.Widen ();
            }
            if (wide == null) return null;
            if (caps != null) wide.AddPath (caps, false);
            if (wide.Count > 0) wide.Transform (m);
            return wide;
        }

    }

    internal static partial class GpPathWarp
    {
        public static partial void Warp (GpPath path, GpMatrix? matrix, PointF[] dest, RectangleF src, WarpMode mode, float flatness)
            => throw new NotImplementedException ("GpPath::WarpAndFlattenSelf");
    }

    internal static partial class GpPathText
    {
        public static partial void AddString (GpPath path, string s, FontFamily family, int style, float emSize, RectangleF layout, StringFormat format)
            => throw new NotImplementedException ("GpPath::AddString");
    }
}
