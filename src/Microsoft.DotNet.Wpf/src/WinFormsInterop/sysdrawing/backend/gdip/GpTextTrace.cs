// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace System.Drawing.WebGpuBackend.Gdip
{
    /// <summary>What the text imagers hand GpGraphics::DrawPlacedGlyphs and DrawLines, for a test
    /// to compare against gdiplus.dll's own (the oracle hooks those two functions). Unset, nothing
    /// is reported.</summary>
    internal static class GpTextTrace
    {
        /// <summary>(render mode, em, glyphs, device origins x0,y0,x1,y1..).</summary>
        internal static Action<int, float, ushort[], float[]> Placed;
        /// <summary>(device pen width, pen unit, the line's world points x0,y0,x1,y1).</summary>
        internal static Action<float, int, float[]> Line;

        internal static void ReportPlaced (int mode, float em, ushort[] glyphs, float[] xs, float[] ys)
        {
            Action<int, float, ushort[], float[]> p = Placed;
            if (p == null) return;
            var xy = new float [glyphs.Length * 2];
            for (int i = 0; i < glyphs.Length; i++) { xy [2 * i] = xs [i]; xy [2 * i + 1] = ys [i]; }
            p (mode, em, glyphs, xy);
        }

        internal static void ReportPlaced (int mode, float em, ushort[] glyphs, PointF[] o)
        {
            Action<int, float, ushort[], float[]> p = Placed;
            if (p == null) return;
            var xy = new float [glyphs.Length * 2];
            for (int i = 0; i < glyphs.Length; i++) { xy [2 * i] = o [i].X; xy [2 * i + 1] = o [i].Y; }
            p (mode, em, glyphs, xy);
        }

        internal static void ReportLine (float width, PointF a, PointF b)
            => Line?.Invoke (width, 2, new[] { a.X, a.Y, b.X, b.Y });
    }
}
