// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// A minimal retained scene model that mirrors WPF's Visual / RenderData
// semantics: a tree of visuals each carrying an offset, a transform, an opacity
// and an optional clip, plus drawing content. This is the cross-platform analog
// of the slave composition tree that milcore rebuilds from the DUCE command
// stream. In the full Phase-1 integration these objects are produced by reading
// the live DUCE channel; here they are built directly so the renderer can be
// proven in isolation.
//

using System.Collections.Generic;
using System.Numerics;

namespace Microsoft.Wpf.Interop.WebGpu.Composition
{
    /// <summary>Straight (non-premultiplied) RGBA colour, channels in [0,1].</summary>
    internal readonly struct RgbaColor
    {
        public readonly float R, G, B, A;

        public RgbaColor(float r, float g, float b, float a)
        {
            R = r; G = g; B = b; A = a;
        }

        public static RgbaColor FromBytes(byte r, byte g, byte b, byte a)
            => new(r / 255f, g / 255f, b / 255f, a / 255f);
    }

    /// <summary>Axis-aligned rectangle in a visual's local coordinate space.</summary>
    internal readonly struct Rect
    {
        public readonly float X, Y, Width, Height;

        public Rect(float x, float y, float width, float height)
        {
            X = x; Y = y; Width = width; Height = height;
        }
    }

    /// <summary>
    /// Geometry to be filled. Mirrors the small set of WPF geometries the slice
    /// supports today: an axis-aligned rectangle and a convex polygon.
    /// </summary>
    internal abstract class Geometry
    {
    }

    internal sealed class RectangleGeometry : Geometry
    {
        public Rect Rect { get; }
        public RectangleGeometry(Rect rect) => Rect = rect;
    }

    /// <summary>Convex polygon, vertices in order. Triangulated as a fan.</summary>
    internal sealed class PolygonGeometry : Geometry
    {
        public Vector2[] Points { get; }
        public PolygonGeometry(Vector2[] points) => Points = points;
    }

    /// <summary>How overlapping/contained contours determine the filled region.</summary>
    internal enum FillRule
    {
        EvenOdd = 0,
        NonZero = 1,
    }

    internal abstract class PathSegment
    {
    }

    internal sealed class LineSegment : PathSegment
    {
        public Vector2 Point { get; }
        public LineSegment(Vector2 point) => Point = point;
    }

    internal sealed class QuadraticBezierSegment : PathSegment
    {
        public Vector2 Control { get; }
        public Vector2 Point { get; }
        public QuadraticBezierSegment(Vector2 control, Vector2 point) { Control = control; Point = point; }
    }

    internal sealed class CubicBezierSegment : PathSegment
    {
        public Vector2 Control1 { get; }
        public Vector2 Control2 { get; }
        public Vector2 Point { get; }
        public CubicBezierSegment(Vector2 c1, Vector2 c2, Vector2 point) { Control1 = c1; Control2 = c2; Point = point; }
    }

    /// <summary>A single subpath: a start point followed by connected segments.</summary>
    internal sealed class PathFigure
    {
        public Vector2 Start { get; set; }
        public List<PathSegment> Segments { get; } = new();
        public bool Closed { get; set; } = true;

        public PathFigure() { }
        public PathFigure(Vector2 start) => Start = start;
    }

    /// <summary>
    /// Arbitrary geometry: one or more figures (with line/Bézier segments) filled
    /// per <see cref="FillRule"/>. The analog of WPF's PathGeometry; supports
    /// concave, self-intersecting and multi-contour shapes.
    /// </summary>
    internal sealed class PathGeometry : Geometry
    {
        public FillRule FillRule { get; }
        public List<PathFigure> Figures { get; }

        public PathGeometry(FillRule fillRule, List<PathFigure> figures)
        {
            FillRule = fillRule;
            Figures = figures;
        }
    }

    /// <summary>Base of the brush hierarchy that paints a fill (mirrors WPF Brush).</summary>
    internal abstract class Brush
    {
    }

    internal sealed class SolidColorBrush : Brush
    {
        public RgbaColor Color { get; }
        public SolidColorBrush(RgbaColor color) => Color = color;
    }

    internal readonly struct GradientStop
    {
        public readonly float Offset;     // 0..1 along the gradient axis
        public readonly RgbaColor Color;
        public GradientStop(float offset, RgbaColor color) { Offset = offset; Color = color; }
    }

    /// <summary>
    /// Linear gradient between <see cref="Start"/> and <see cref="End"/> (in the
    /// fill's local coordinate space), realized as a sampled colour ramp.
    /// </summary>
    internal sealed class LinearGradientBrush : Brush
    {
        public Vector2 Start { get; }
        public Vector2 End { get; }
        public GradientStop[] Stops { get; }

        public LinearGradientBrush(Vector2 start, Vector2 end, GradientStop[] stops)
        {
            Start = start; End = end; Stops = stops;
        }
    }

    /// <summary>
    /// Image brush: straight (non-premultiplied) RGBA8 pixels, row-major, mapped
    /// across the fill geometry's local bounds.
    /// </summary>
    internal sealed class ImageBrush : Brush
    {
        public byte[] PixelsRgba { get; }
        public int PixelWidth { get; }
        public int PixelHeight { get; }

        public ImageBrush(byte[] pixelsRgba, int pixelWidth, int pixelHeight)
        {
            PixelsRgba = pixelsRgba; PixelWidth = pixelWidth; PixelHeight = pixelHeight;
        }
    }

    /// <summary>
    /// Base of the heterogeneous drawing-instruction list a visual records
    /// (the analog of WPF's RenderData stream: fills, glyph runs, ...).
    /// </summary>
    internal abstract class DrawingPrimitive
    {
    }

    /// <summary>A single fill instruction: geometry + brush.</summary>
    internal sealed class GeometryFill : DrawingPrimitive
    {
        public Geometry Geometry { get; }
        public Brush Brush { get; }

        public GeometryFill(Geometry geometry, Brush brush)
        {
            Geometry = geometry;
            Brush = brush;
        }

        /// <summary>Convenience overload for the common solid-colour fill.</summary>
        public GeometryFill(Geometry geometry, RgbaColor color)
            : this(geometry, new SolidColorBrush(color))
        {
        }
    }

    /// <summary>
    /// A run of text drawn from a baseline origin (the analog of WPF's
    /// DrawGlyphRun). The glyphs are rasterized by an <see cref="Text.IGlyphSource"/>
    /// and composited from a glyph atlas; <see cref="EmSize"/> scales the source.
    /// </summary>
    internal sealed class GlyphRunDraw : DrawingPrimitive
    {
        public string Text { get; }
        public Vector2 Origin { get; }   // baseline origin, local space
        public float EmSize { get; }
        public RgbaColor Color { get; }

        public GlyphRunDraw(string text, Vector2 origin, float emSize, RgbaColor color)
        {
            Text = text; Origin = origin; EmSize = emSize; Color = color;
        }
    }

    /// <summary>
    /// A node in the visual tree. Coordinate composition matches WPF: a child
    /// point is mapped to the parent by applying <see cref="Transform"/> and then
    /// translating by <see cref="Offset"/>. Opacity multiplies down the tree and
    /// <see cref="Clip"/> (if set) intersects the visible region.
    /// </summary>
    internal sealed class SceneVisual
    {
        /// <summary>Offset applied after <see cref="Transform"/> (VisualOffset).</summary>
        public Vector2 Offset { get; set; } = Vector2.Zero;

        /// <summary>Local transform applied before <see cref="Offset"/> (VisualTransform).</summary>
        public Matrix3x2 Transform { get; set; } = Matrix3x2.Identity;

        /// <summary>Opacity in [0,1], multiplied with ancestors.</summary>
        public double Opacity { get; set; } = 1.0;

        /// <summary>Optional axis-aligned clip in this visual's local space.</summary>
        public Rect? Clip { get; set; }

        /// <summary>Drawing content recorded by this visual, in local space.</summary>
        public List<DrawingPrimitive> Content { get; } = new();

        public List<SceneVisual> Children { get; } = new();

        /// <summary>Maps a local point to the parent's coordinate space.</summary>
        public Matrix3x2 LocalToParent => Transform * Matrix3x2.CreateTranslation(Offset);
    }
}
