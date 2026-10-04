// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// DriverMeta's bitmaps (gdiplus.dll 10.0.26100): BrushFillUsingBitmap @1800d3728 and DrawImage
// @1800d46b0 with ConvertBitmapToGdi @1800d76d8 / StretchBlt @1800d9e60.
//

namespace System.Drawing.WebGpuBackend.Gdip
{
    internal sealed partial class GpMetafileRecorder
    {
        /// <summary>DriverMeta::BrushFillUsingBitmap: true when the brush was rendered as a bitmap.</summary>
        bool BrushFillUsingBitmap(Rectangle rect, Brush brush, PathToGdi clip)
        {
            return false;
        }
    }
}
