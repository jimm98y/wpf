// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Drawing.Imaging;

namespace System.Drawing.WebGpuBackend.Gdip
{
    /// <summary>GpRecolor: the ImageAttributes colour adjustments per ColorAdjustType.</summary>
    internal sealed class GpRecolor
    {
        public bool HasRecoloring (ColorAdjustType type) => false;

        public GdipFrame Apply (GdipFrame f, ColorAdjustType type) => f;
    }
}
