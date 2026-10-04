// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Drawing.Drawing2D;

namespace System.Drawing.WebGpuBackend.Gdip
{
    internal static partial class GpPen
    {
        public static partial GpPath Widen (GpPath path, Pen pen, GpMatrix matrix, float flatness, bool solid)
            => throw new NotImplementedException ("GpPathWidener");
        public static partial RectangleF WidenedBounds (GpPath path, Pen pen, GpMatrix? matrix)
            => throw new NotImplementedException ("GpPath::GetBounds with a pen");
    }

    internal static partial class GpPathWarp
    {
        public static partial void Warp (GpPath path, GpMatrix? matrix, PointF[] dest, RectangleF src, WarpMode mode, float flatness)
            => throw new NotImplementedException ("GpPath::WarpAndFlattenSelf");
    }
}
