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

    /// <summary>
    /// A rectangle with rounded corners (the analog of WPF's RectangleGeometry
    /// with RadiusX/RadiusY). Realized as a path with quarter-ellipse corners.
    /// </summary>
    internal sealed class RoundedRectangleGeometry : Geometry
    {
        public Rect Rect { get; }
        public float RadiusX { get; }
        public float RadiusY { get; }

        public RoundedRectangleGeometry(Rect rect, float radiusX, float radiusY)
        {
            Rect = rect; RadiusX = radiusX; RadiusY = radiusY;
        }
    }

    /// <summary>
    /// An axis-aligned ellipse (a circle when the radii are equal) — the analog of
    /// WPF's EllipseGeometry. Realized as a path with four quarter-ellipse arcs.
    /// </summary>
    internal sealed class EllipseGeometry : Geometry
    {
        public Vector2 Center { get; }
        public float RadiusX { get; }
        public float RadiusY { get; }

        public EllipseGeometry(Vector2 center, float radiusX, float radiusY)
        {
            Center = center; RadiusX = radiusX; RadiusY = radiusY;
        }
    }

    /// <summary>How overlapping/contained contours determine the filled region.</summary>
    internal enum FillRule
    {
        EvenOdd = 0,
        NonZero = 1,
    }

    /// <summary>Boolean combination of two geometries (WPF's GeometryCombineMode).</summary>
    internal enum GeometryCombineMode
    {
        Union = 0,     // either
        Intersect = 1, // both
        Xor = 2,       // exactly one
        Exclude = 3,   // in the first but not the second
    }

    /// <summary>
    /// A boolean combination of two geometries (the analog of WPF's
    /// CombinedGeometry). Combined at the coverage level, so the result is
    /// anti-aliased.
    /// </summary>
    internal sealed class CombinedGeometry : Geometry
    {
        public GeometryCombineMode Mode { get; }
        public Geometry Geometry1 { get; }
        public Geometry Geometry2 { get; }

        public CombinedGeometry(GeometryCombineMode mode, Geometry geometry1, Geometry geometry2)
        {
            Mode = mode; Geometry1 = geometry1; Geometry2 = geometry2;
        }
    }

    /// <summary>
    /// A composite of several geometries (possibly different kinds) filled as one
    /// region under a shared fill rule — the analog of WPF's GeometryGroup. With
    /// EvenOdd, a contained child becomes a hole (e.g. a frame with a cut-out).
    /// </summary>
    internal sealed class GeometryGroup : Geometry
    {
        public FillRule FillRule { get; }
        public List<Geometry> Children { get; }

        public GeometryGroup(FillRule fillRule, List<Geometry> children)
        {
            FillRule = fillRule; Children = children;
        }
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

    /// <summary>How a gradient extends beyond its [0,1] range (WPF's GradientSpreadMethod).</summary>
    internal enum GradientSpreadMethod
    {
        Pad = 0,      // hold the end colours
        Reflect = 1,  // mirror back and forth
        Repeat = 2,   // tile from the start
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
        public GradientSpreadMethod SpreadMethod { get; }

        public LinearGradientBrush(Vector2 start, Vector2 end, GradientStop[] stops, GradientSpreadMethod spread = GradientSpreadMethod.Pad)
        {
            Start = start; End = end; Stops = stops; SpreadMethod = spread;
        }
    }

    /// <summary>
    /// Radial gradient from <see cref="Center"/> out to an ellipse of radii
    /// (<see cref="RadiusX"/>, <see cref="RadiusY"/>), in the fill's local space
    /// (the analog of WPF's RadialGradientBrush). Evaluated per pixel.
    /// </summary>
    internal sealed class RadialGradientBrush : Brush
    {
        public Vector2 Center { get; }
        public float RadiusX { get; }
        public float RadiusY { get; }
        public GradientStop[] Stops { get; }
        public GradientSpreadMethod SpreadMethod { get; }

        public RadialGradientBrush(Vector2 center, float radiusX, float radiusY, GradientStop[] stops, GradientSpreadMethod spread = GradientSpreadMethod.Pad)
        {
            Center = center; RadiusX = radiusX; RadiusY = radiusY; Stops = stops; SpreadMethod = spread;
        }
    }

    /// <summary>How an image brush repeats across the fill (WPF's TileMode).</summary>
    // Values MATCH System.Windows.Media.TileMode exactly (None=0, FlipX=1, FlipY=2, FlipXY=3, Tile=4)
    // so the MILCMD parse can cast the raw WPF value directly. (Note: Tile is 4, not 1.)
    internal enum TileMode
    {
        None = 0,   // map once across the geometry bounds
        FlipX = 1,  // repeat, mirroring alternate columns
        FlipY = 2,  // repeat, mirroring alternate rows
        FlipXY = 3, // repeat, mirroring both
        Tile = 4,   // repeat
    }

    /// <summary>
    /// Image brush: straight (non-premultiplied) RGBA8 pixels, row-major. With
    /// <see cref="TileMode.None"/> the image maps once across the geometry's local
    /// bounds; otherwise it tiles every (<see cref="TileWidth"/>,
    /// <see cref="TileHeight"/>) local units from the local origin.
    /// </summary>
    internal sealed class ImageBrush : Brush
    {
        public byte[] PixelsRgba { get; }
        public int PixelWidth { get; }
        public int PixelHeight { get; }
        public TileMode TileMode { get; }
        public float TileWidth { get; }
        public float TileHeight { get; }

        public ImageBrush(byte[] pixelsRgba, int pixelWidth, int pixelHeight,
            TileMode tileMode = TileMode.None, float tileWidth = 0f, float tileHeight = 0f)
        {
            PixelsRgba = pixelsRgba; PixelWidth = pixelWidth; PixelHeight = pixelHeight;
            TileMode = tileMode; TileWidth = tileWidth; TileHeight = tileHeight;
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

    /// <summary>How the ends of an open stroked figure are shaped.</summary>
    internal enum LineCap
    {
        Butt = 0,
        Round = 1,
        Square = 2,
    }

    /// <summary>How the corner between two stroked segments is filled.</summary>
    internal enum LineJoin
    {
        Miter = 0,
        Bevel = 1,
        Round = 2,
    }

    /// <summary>Pen parameters for stroking (the analog of WPF's Pen).</summary>
    internal readonly struct StrokeStyle
    {
        public readonly double Thickness;
        public readonly LineCap Cap;
        public readonly LineJoin Join;
        public readonly double MiterLimit;
        /// <summary>Alternating on/off dash lengths (pixels); null = solid.</summary>
        public readonly double[]? DashArray;
        public readonly double DashOffset;

        public StrokeStyle(double thickness, LineCap cap = LineCap.Round, LineJoin join = LineJoin.Round,
            double miterLimit = 10.0, double[]? dashArray = null, double dashOffset = 0.0)
        {
            Thickness = thickness;
            Cap = cap;
            Join = join;
            MiterLimit = miterLimit;
            DashArray = dashArray;
            DashOffset = dashOffset;
        }
    }

    /// <summary>
    /// Strokes a path's outline with a brush (the analog of WPF's
    /// DrawGeometry with a Pen). Round joins/caps today; the outline is
    /// rasterized like a filled path so it anti-aliases identically.
    /// </summary>
    internal sealed class GeometryStroke : DrawingPrimitive
    {
        public PathGeometry Geometry { get; }
        public Brush Brush { get; }
        public StrokeStyle Style { get; }

        public GeometryStroke(PathGeometry geometry, Brush brush, StrokeStyle style)
        {
            Geometry = geometry;
            Brush = brush;
            Style = style;
        }

        /// <summary>Convenience overload for the common solid-colour stroke.</summary>
        public GeometryStroke(PathGeometry geometry, RgbaColor color, StrokeStyle style)
            : this(geometry, new SolidColorBrush(color), style)
        {
        }
    }

    /// <summary>
    /// Fills a geometry and/or strokes its outline in one instruction (the analog
    /// of WPF's DrawGeometry(brush, pen, geometry)). The fill is drawn first, then
    /// the stroke on top. Either may be null.
    /// </summary>
    internal sealed class GeometryDrawing : DrawingPrimitive
    {
        public Geometry Geometry { get; }
        public Brush? Fill { get; }
        public Brush? Stroke { get; }
        public StrokeStyle StrokeStyle { get; }

        public GeometryDrawing(Geometry geometry, Brush? fill, Brush? stroke = null, StrokeStyle strokeStyle = default)
        {
            Geometry = geometry; Fill = fill; Stroke = stroke; StrokeStyle = strokeStyle;
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

    /// <summary>A post-processing effect applied to a visual's rendered subtree.</summary>
    internal abstract class Effect
    {
    }

    /// <summary>Gaussian blur (the analog of WPF's BlurEffect).</summary>
    internal sealed class BlurEffect : Effect
    {
        /// <summary>Blur radius in pixels (≈ the Gaussian standard deviation).</summary>
        public double Radius { get; }
        public BlurEffect(double radius) => Radius = radius;
    }

    /// <summary>
    /// Drop shadow: a blurred, tinted, offset silhouette of the subtree drawn
    /// beneath it (the analog of WPF's DropShadowEffect).
    /// </summary>
    internal sealed class DropShadowEffect : Effect
    {
        public RgbaColor Color { get; }
        public double BlurRadius { get; }
        public double OffsetX { get; }
        public double OffsetY { get; }

        public DropShadowEffect(RgbaColor color, double blurRadius, double offsetX, double offsetY)
        {
            Color = color; BlurRadius = blurRadius; OffsetX = offsetX; OffsetY = offsetY;
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

        /// <summary>Optional axis-aligned clip in this visual's local space (fast scissor path).</summary>
        public Rect? Clip { get; set; }

        /// <summary>
        /// Optional arbitrary clip geometry in this visual's local space. Unlike
        /// <see cref="Clip"/>, this masks the subtree to any shape (the analog of
        /// WPF's Visual.Clip with a non-rectangular geometry).
        /// </summary>
        public PathGeometry? ClipGeometry { get; set; }

        /// <summary>Optional post-processing effect applied to the rendered subtree.</summary>
        public Effect? Effect { get; set; }

        /// <summary>
        /// Optional brush whose alpha modulates the subtree's opacity per pixel
        /// (the analog of WPF's Visual.OpacityMask). Evaluated in local space.
        /// </summary>
        public Brush? OpacityMask { get; set; }

        /// <summary>Drawing content recorded by this visual, in local space.</summary>
        public List<DrawingPrimitive> Content { get; } = new();

        public List<SceneVisual> Children { get; } = new();

        /// <summary>Maps a local point to the parent's coordinate space.</summary>
        public Matrix3x2 LocalToParent => Transform * Matrix3x2.CreateTranslation(Offset);
    }
}
