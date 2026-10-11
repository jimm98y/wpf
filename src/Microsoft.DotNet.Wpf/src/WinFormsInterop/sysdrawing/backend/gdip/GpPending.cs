// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Entry points whose GDI+ ports land in their own files (widener, warp, path text).

using System.Drawing.Drawing2D;

namespace System.Drawing.WebGpuBackend.Gdip
{
    internal static partial class GpPen
    {
        public static partial GpPath Widen (GpPath path, Pen pen, GpMatrix matrix, float flatness, bool solid);
        public static partial RectangleF WidenedBounds (GpPath path, Pen pen, GpMatrix? matrix);
    }

    internal static partial class GpPathWarp
    {
        public static partial void Warp (GpPath path, GpMatrix? matrix, PointF[] dest, RectangleF src, WarpMode mode, float flatness);
    }

    internal static partial class GpPathText
    {
        public static partial void AddString (GpPath path, string s, FontFamily family, int style, float emSize, RectangleF layout, StringFormat format);
    }
}
