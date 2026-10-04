// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The non-solid brushes' spans (GpHatch, GpTexture, GpLineGradient, GpPathGradient ::CreateOutputSpan).
//

using System.Drawing.Drawing2D;

namespace System.Drawing.WebGpuBackend.Gdip
{
    internal sealed partial class GpGraphics
    {
        bool CanFillBrush (Brush brush) => brush is HatchBrush || brush is LinearGradientBrush;

        GpSpan CreateBrushSpan (Brush brush, GpScan scan, Rectangle draw)
        {
            switch (brush) {
            case HatchBrush hb:
                return new HatchSpan (scan, hb, _ctx.RenderingOrigin);
            case LinearGradientBrush lg:
                return new LineGradientSpan (scan, lg, WorldToDevice);
            }
            return null;
        }
    }
}
