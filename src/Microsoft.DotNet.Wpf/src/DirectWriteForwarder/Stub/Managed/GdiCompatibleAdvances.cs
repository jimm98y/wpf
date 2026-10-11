// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;

namespace MS.Internal.Text.TextInterface
{
    /// <summary>
    /// GDI's own advance for a glyph at a whole-pixel size -- what DirectWrite's GDI-compatible glyph
    /// metrics (IDWriteFontFace::GetGdiCompatibleGlyphMetrics, and the placements of the GDI_CLASSIC
    /// measuring mode) report, and so what WPF lays DISPLAY-mode text out with.
    /// <para>The answer needs the face's hinting (its hdmx, or the hinted advance phantom when it has
    /// none), which lives with the managed rasterizer, not here. The compositor installs a provider
    /// when it starts (Common/Graphics/exports.cs); with none, the design advance stands.</para>
    /// </summary>
    public static class GdiCompatibleAdvances
    {
        /// <summary>(font file, face index, simulations, ppem, glyph) -> whole-pixel advance, or -1.</summary>
        public static Func<string, int, int, int, int, int> Provider;

        internal static int PixelAdvance(Managed.FaceRecord face, int simulations, double pixels, int glyph)
        {
            Func<string, int, int, int, int, int> p = Provider;
            if (p is null || face is null || string.IsNullOrEmpty(face.FilePath)) return -1;
            int ppem = (int)Math.Round(pixels, MidpointRounding.AwayFromZero);
            if (ppem <= 0) return -1;
            try { return p(face.FilePath, face.FaceIndex, simulations, ppem, glyph); }
            catch { return -1; }
        }
    }
}
