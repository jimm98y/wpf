// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Turning one positioned glyph into the fills that draw it.
//
// This is the single place that knows how a glyph becomes geometry, and it exists because there are
// two callers that both need it and had drifted apart:
//
//   * MilcoreEngine, decoding the WPF protocol, where a run arrives as glyph INDICES from WPF's own
//     text stack together with the font it was shaped against;
//   * WgpuSceneRenderer.EmitText, drawing a GlyphRunDraw, where a run arrives as a STRING that the
//     renderer has to shape itself against its default font (the WinForms-in-WPF and spike paths).
//
// Those two inputs are genuinely different and stay different. What must NOT differ is what happens
// once a glyph id is in hand -- and it did: colour (COLR/CPAL) glyph support was added to the
// protocol path only, so an emoji drawn through a string run came out as a black silhouette. One
// implementation, two callers, and a feature can no longer land in only half the product.
//
// The callers keep their own batching and transform policy, which is legitimately different:
// MilcoreEngine emits one fill per glyph carrying a baseline anchor (its coverage cache snaps a run
// to a single baseline), while the scene renderer merges a run's monochrome glyphs into one fill and
// applies the world transform at draw time.
//

using System.Collections.Generic;
using System.Numerics;

namespace Microsoft.Wpf.Interop.WebGpu.Composition.Text
{
    /// <summary>One fill that a glyph decomposes into: a shape, and how to paint it.</summary>
    internal readonly struct GlyphFill
    {
        /// <summary>
        /// The shape to fill, already scaled to em size and translated to the glyph position. For an
        /// outline glyph or a COLR layer this is the glyph's contours; for a colour BITMAP glyph it
        /// is simply the rectangle the image occupies.
        /// </summary>
        public readonly List<PathFigure> Figures;

        /// <summary>The layer's palette colour, or null to use the run's foreground brush.</summary>
        public readonly RgbaColor? Color;

        /// <summary>
        /// A brush to paint the shape with, overriding <see cref="Color"/>. Set only for a colour
        /// bitmap glyph, where it is an ImageBrush over the decoded PNG.
        /// </summary>
        public readonly Brush? Brush;

        /// <summary>
        /// True when this is colour artwork (a COLR layer or a bitmap) rather than a plain outline.
        /// Callers use it to skip the text gamma correction: that exists for thin monochrome stems
        /// against a background, and applying it to artwork shifts its colours.
        /// </summary>
        public readonly bool IsColorLayer;

        public GlyphFill(List<PathFigure> figures, RgbaColor? color, bool isColorLayer, Brush? brush = null)
        {
            Figures = figures;
            Color = color;
            IsColorLayer = isColorLayer;
            Brush = brush;
        }
    }

    internal static class GlyphRunPainter
    {
        /// <summary>
        /// Appends the fills for one positioned glyph to <paramref name="into"/>.
        ///
        /// A COLR/CPAL glyph yields one fill per layer, back to front, each with its palette colour
        /// (or null where the layer follows the text colour). Anything else yields a single
        /// monochrome fill, or nothing at all when the glyph has no outline (a space).
        ///
        /// <paramref name="into"/> is supplied by the caller and appended to, so a run can reuse one
        /// list across its glyphs rather than allocating per glyph on a hot path.
        /// </summary>
        /// <param name="font">Outline source for the glyph and for any COLR layer glyph.</param>
        /// <param name="colorFont">The same font as <paramref name="font"/> when it carries COLR/CPAL, else null.</param>
        /// <param name="glyphId">Glyph to paint.</param>
        /// <param name="scale">Em size divided by the font's design units per em.</param>
        /// <param name="gx">Glyph origin x (pen position plus any x offset), in the caller's local space.</param>
        /// <param name="gy">Glyph baseline y, in the caller's local space.</param>
        public static void Paint(IGlyphOutlineFont font, IColorGlyphFont? colorFont, int glyphId,
                                 float scale, float gx, float gy, List<GlyphFill> into)
            => Paint(font, colorFont, font as IBitmapGlyphFont, glyphId, scale, gx, gy, into);

        /// <inheritdoc cref="Paint(IGlyphOutlineFont, IColorGlyphFont?, int, float, float, float, List{GlyphFill})"/>
        /// <param name="bitmapFont">
        /// The same font again when it carries CBDT/CBLC colour bitmaps, else null. Checked BEFORE
        /// outlines: a bitmap emoji font has no outlines to fall back to, and where a font has both,
        /// the colour artwork is what the glyph is meant to look like.
        /// </param>
        public static void Paint(IGlyphOutlineFont font, IColorGlyphFont? colorFont, IBitmapGlyphFont? bitmapFont,
                                 int glyphId, float scale, float gx, float gy, List<GlyphFill> into)
        {
            if (font is null) return;

            if (bitmapFont != null && TryPaintBitmap(bitmapFont, font, glyphId, scale, gx, gy, into))
                return;

            if (colorFont != null &&
                colorFont.TryGetColorLayers(glyphId, out IReadOnlyList<ColorGlyphLayer> layers) &&
                layers.Count > 0)
            {
                foreach (ColorGlyphLayer layer in layers)
                {
                    if (!font.TryGetGlyphOutline(layer.GlyphId, out List<PathFigure> lf) || lf.Count == 0)
                        continue;
                    into.Add(new GlyphFill(ScaleFigures(lf, scale, gx, gy), layer.Color, isColorLayer: true));
                }
                return;
            }

            if (font.TryGetGlyphOutline(glyphId, out List<PathFigure> figures) && figures.Count > 0)
                into.Add(new GlyphFill(ScaleFigures(figures, scale, gx, gy), null, isColorLayer: false));
        }

        // A colour bitmap glyph: decode the PNG once and paint it as an image over the rectangle it
        // occupies. Deliberately expressed as an ordinary geometry + ImageBrush rather than as a new
        // kind of draw, so it inherits the transform, clip, opacity and sampling that every other
        // image in the scene already gets, on both text paths, with no renderer changes at all.
        private static bool TryPaintBitmap(IBitmapGlyphFont bitmapFont, IGlyphOutlineFont font, int glyphId,
                                           float scale, float gx, float gy, List<GlyphFill> into)
        {
            if (!bitmapFont.TryGetGlyphBitmap(glyphId, out BitmapGlyph bmp) || bmp.Png is null) return false;

            DecodedBitmap? decoded = Decode(bmp);
            if (decoded is null) return false;
            DecodedBitmap d = decoded.Value;

            // The strike is in its own pixels (Noto Color Emoji draws at 136ppem), so map it onto the
            // em size this run is being drawn at. `scale` converts font units to run pixels, and the
            // outline font reports how many font units make an em, which gives the run's em in pixels.
            float emPixels = font.PixelsPerEm * scale;
            float k = bmp.PpemY > 0 ? emPixels / bmp.PpemY : scale;

            float w = bmp.PixelWidth * k;
            float h = bmp.PixelHeight * k;
            // BearingY is measured UP from the baseline to the top of the bitmap, and y grows down.
            float x0 = gx + bmp.BearingX * k;
            float y0 = gy - bmp.BearingY * k;

            var rect = new List<PathFigure>(1) { Rectangle(x0, y0, w, h) };
            var brush = new ImageBrush(d.Rgba, d.Width, d.Height);
            into.Add(new GlyphFill(rect, null, isColorLayer: true, brush));
            return true;
        }

        private readonly struct DecodedBitmap
        {
            public readonly byte[] Rgba;
            public readonly int Width, Height;
            public DecodedBitmap(byte[] rgba, int width, int height) { Rgba = rgba; Width = width; Height = height; }
        }

        // Decoding a ~136x128 PNG per glyph per frame would be absurd, and emoji repeat, so the
        // decoded pixels are kept. Keyed by the PNG bytes themselves (reference equality): the font
        // hands back the same array for the same glyph, and two fonts never share one.
        private static readonly Dictionary<byte[], DecodedBitmap?> s_decoded = new(ReferenceEqualityComparer.Instance);

        private static DecodedBitmap? Decode(in BitmapGlyph bmp)
        {
            lock (s_decoded)
            {
                if (s_decoded.TryGetValue(bmp.Png, out DecodedBitmap? cached)) return cached;
            }

            DecodedBitmap? result = null;
            try
            {
                byte[] rgba = PngReader.Decode(bmp.Png, out int w, out int h);
                if (w > 0 && h > 0) result = new DecodedBitmap(rgba, w, h);
            }
            catch (System.IO.InvalidDataException)
            {
                // An image format this decoder does not handle. Cached as "no bitmap" so the failure
                // costs one attempt rather than one per frame; the glyph falls back to its outline.
            }

            lock (s_decoded)
            {
                s_decoded[bmp.Png] = result;
            }
            return result;
        }

        private static PathFigure Rectangle(float x, float y, float w, float h)
        {
            var f = new PathFigure(new Vector2(x, y)) { Closed = true };
            f.Segments.Add(new LineSegment(new Vector2(x + w, y)));
            f.Segments.Add(new LineSegment(new Vector2(x + w, y + h)));
            f.Segments.Add(new LineSegment(new Vector2(x, y + h)));
            return f;
        }

        /// <summary>
        /// Scale glyph-outline figures (font units, baseline at y=0) by <paramref name="s"/> and move
        /// them to (<paramref name="tx"/>, <paramref name="ty"/>).
        /// </summary>
        public static List<PathFigure> ScaleFigures(List<PathFigure> figures, float s, float tx, float ty)
        {
            Vector2 M(Vector2 p) => new(p.X * s + tx, p.Y * s + ty);

            var scaled = new List<PathFigure>(figures.Count);
            foreach (PathFigure f in figures)
            {
                var nf = new PathFigure(M(f.Start)) { Closed = f.Closed };
                foreach (PathSegment seg in f.Segments)
                {
                    nf.Segments.Add(seg switch
                    {
                        LineSegment l => new LineSegment(M(l.Point)),
                        QuadraticBezierSegment q => new QuadraticBezierSegment(M(q.Control), M(q.Point)),
                        CubicBezierSegment c => new CubicBezierSegment(M(c.Control1), M(c.Control2), M(c.Point)),
                        _ => seg,
                    });
                }
                scaled.Add(nf);
            }
            return scaled;
        }
    }
}
