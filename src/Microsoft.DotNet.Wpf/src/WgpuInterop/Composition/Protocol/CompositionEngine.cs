// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Server side of the protocol: the analog of milcore's composition engine (the
// UCE slave resource tree). It consumes batches produced by CompositionChannel,
// maintains a handle -> resource table, applies visual property/content/structure
// commands and exposes the rebuilt SceneVisual tree for the WebGPU renderer.
// This is the component that, in the full port, replaces wpfgfx's command
// processing -- it reads the same kind of stream WPF already emits and turns it
// into GPU work via WgpuSceneRenderer instead of Direct3D.
//
// State is retained across batches (like a milcore partition), so later batches
// can mutate previously created resources.
//

using System.Collections.Generic;
using System.Numerics;

namespace Microsoft.Wpf.Interop.WebGpu.Composition.Protocol
{
    internal sealed class CompositionEngine
    {
        private readonly Dictionary<uint, SceneVisual> _resources = new();
        private uint _rootHandle;

        /// <summary>The current composition root, or null if none has been set.</summary>
        public SceneVisual? Root => _resources.TryGetValue(_rootHandle, out SceneVisual? v) ? v : null;

        /// <summary>Applies a serialized batch to the retained composition tree.</summary>
        public void ProcessBatch(byte[] batch)
        {
            var r = new CommandReader(batch);
            while (!r.AtEnd)
            {
                uint payloadSize = r.U32();
                var command = (MilCommand)r.U32();
                int payloadStart = r.Position;

                switch (command)
                {
                    case MilCommand.CreateResource:
                    {
                        uint handle = r.U32();
                        _ = (MilResourceType)r.U8();
                        _resources[handle] = new SceneVisual();
                        break;
                    }
                    case MilCommand.VisualSetOffset:
                    {
                        SceneVisual v = Get(r.U32());
                        v.Offset = new Vector2((float)r.F64(), (float)r.F64());
                        break;
                    }
                    case MilCommand.VisualSetTransform:
                    {
                        SceneVisual v = Get(r.U32());
                        v.Transform = new Matrix3x2(r.F32(), r.F32(), r.F32(), r.F32(), r.F32(), r.F32());
                        break;
                    }
                    case MilCommand.VisualSetOpacity:
                    {
                        SceneVisual v = Get(r.U32());
                        v.Opacity = r.F64();
                        break;
                    }
                    case MilCommand.VisualSetClip:
                    {
                        SceneVisual v = Get(r.U32());
                        v.Clip = r.U8() != 0 ? new Rect(r.F32(), r.F32(), r.F32(), r.F32()) : null;
                        break;
                    }
                    case MilCommand.VisualSetClipGeometry:
                    {
                        SceneVisual v = Get(r.U32());
                        v.ClipGeometry = ReadPath(r);
                        break;
                    }
                    case MilCommand.VisualSetOpacityMask:
                    {
                        SceneVisual v = Get(r.U32());
                        v.OpacityMask = ReadBrush(r);
                        break;
                    }
                    case MilCommand.VisualSetEffect:
                    {
                        SceneVisual v = Get(r.U32());
                        v.Effect = (EffectKind)r.U8() switch
                        {
                            EffectKind.Blur => new BlurEffect(r.F64()),
                            EffectKind.DropShadow => new DropShadowEffect(ReadColor(r), r.F64(), r.F64(), r.F64()),
                            _ => null,
                        };
                        break;
                    }
                    case MilCommand.VisualSetContent:
                    {
                        SceneVisual v = Get(r.U32());
                        int cb = (int)r.U32();
                        byte[] renderData = r.Bytes(cb);
                        v.Content.Clear();
                        ParseRenderData(renderData, v.Content);
                        break;
                    }
                    case MilCommand.VisualAddChild:
                    {
                        SceneVisual parent = Get(r.U32());
                        SceneVisual child = Get(r.U32());
                        parent.Children.Add(child);
                        break;
                    }
                    case MilCommand.TargetSetRoot:
                        _rootHandle = r.U32();
                        break;
                }

                // Resync to the next record regardless of how much this command read.
                r.Position = payloadStart + (int)payloadSize;
            }
        }

        private SceneVisual Get(uint handle) => _resources[handle];

        private static void ParseRenderData(byte[] data, List<DrawingPrimitive> output)
        {
            var r = new CommandReader(data);
            while (!r.AtEnd)
            {
                var op = (RenderDataOp)r.U8();
                switch (op)
                {
                    case RenderDataOp.FillRectangle:
                    {
                        var rect = new Rect(r.F32(), r.F32(), r.F32(), r.F32());
                        Brush brush = ReadBrush(r);
                        output.Add(new GeometryFill(new RectangleGeometry(rect), brush));
                        break;
                    }
                    case RenderDataOp.FillPolygon:
                    {
                        int count = r.U16();
                        var points = new Vector2[count];
                        for (int i = 0; i < count; i++)
                            points[i] = new Vector2(r.F32(), r.F32());
                        Brush brush = ReadBrush(r);
                        output.Add(new GeometryFill(new PolygonGeometry(points), brush));
                        break;
                    }
                    case RenderDataOp.FillRoundedRectangle:
                    {
                        var rect = new Rect(r.F32(), r.F32(), r.F32(), r.F32());
                        float rx = r.F32(), ry = r.F32();
                        Brush brush = ReadBrush(r);
                        output.Add(new GeometryFill(new RoundedRectangleGeometry(rect, rx, ry), brush));
                        break;
                    }
                    case RenderDataOp.FillEllipse:
                    {
                        var center = new Vector2(r.F32(), r.F32());
                        float rx = r.F32(), ry = r.F32();
                        Brush brush = ReadBrush(r);
                        output.Add(new GeometryFill(new EllipseGeometry(center, rx, ry), brush));
                        break;
                    }
                    case RenderDataOp.FillGeometryGroup:
                    {
                        GeometryGroup group = ReadGeometryGroup(r);
                        Brush brush = ReadBrush(r);
                        output.Add(new GeometryFill(group, brush));
                        break;
                    }
                    case RenderDataOp.FillCombinedGeometry:
                    {
                        Geometry geometry = ReadGeometry(r);
                        Brush brush = ReadBrush(r);
                        output.Add(new GeometryFill(geometry, brush));
                        break;
                    }
                    case RenderDataOp.FillPath:
                    {
                        PathGeometry path = ReadPath(r);
                        Brush brush = ReadBrush(r);
                        output.Add(new GeometryFill(path, brush));
                        break;
                    }
                    case RenderDataOp.StrokePath:
                    {
                        PathGeometry path = ReadPath(r);
                        StrokeStyle style = ReadStrokeStyle(r);
                        Brush brush = ReadBrush(r);
                        output.Add(new GeometryStroke(path, brush, style));
                        break;
                    }
                    case RenderDataOp.DrawGeometry:
                    {
                        Geometry geometry = ReadGeometry(r);
                        Brush? fill = r.U8() != 0 ? ReadBrush(r) : null;
                        Brush? stroke = null;
                        StrokeStyle style = default;
                        if (r.U8() != 0) { stroke = ReadBrush(r); style = ReadStrokeStyle(r); }
                        output.Add(new GeometryDrawing(geometry, fill, stroke, style));
                        break;
                    }
                    case RenderDataOp.DrawGlyphRun:
                    {
                        var origin = new Vector2(r.F32(), r.F32());
                        float emSize = r.F32();
                        RgbaColor color = ReadColor(r);
                        int len = r.U16();
                        var chars = new char[len];
                        for (int i = 0; i < len; i++) chars[i] = (char)r.U16();
                        output.Add(new GlyphRunDraw(new string(chars), origin, emSize, color));
                        break;
                    }
                    default:
                        return; // unknown op: stop parsing this stream
                }
            }
        }

        private static Geometry ReadGeometry(CommandReader r)
        {
            var kind = (GeometryKind)r.U8();
            switch (kind)
            {
                case GeometryKind.Rectangle:
                    return new RectangleGeometry(new Rect(r.F32(), r.F32(), r.F32(), r.F32()));
                case GeometryKind.Polygon:
                {
                    int count = r.U16();
                    var points = new Vector2[count];
                    for (int i = 0; i < count; i++) points[i] = new Vector2(r.F32(), r.F32());
                    return new PolygonGeometry(points);
                }
                case GeometryKind.RoundedRectangle:
                {
                    var rect = new Rect(r.F32(), r.F32(), r.F32(), r.F32());
                    return new RoundedRectangleGeometry(rect, r.F32(), r.F32());
                }
                case GeometryKind.Ellipse:
                    return new EllipseGeometry(new Vector2(r.F32(), r.F32()), r.F32(), r.F32());
                case GeometryKind.Group:
                    return ReadGeometryGroup(r);
                case GeometryKind.Combined:
                {
                    var mode = (GeometryCombineMode)r.U8();
                    Geometry g1 = ReadGeometry(r);
                    Geometry g2 = ReadGeometry(r);
                    return new CombinedGeometry(mode, g1, g2);
                }
                default:
                    return ReadPath(r);
            }
        }

        private static GeometryGroup ReadGeometryGroup(CommandReader r)
        {
            var fillRule = (FillRule)r.U8();
            int count = r.U16();
            var children = new List<Geometry>(count);
            for (int i = 0; i < count; i++) children.Add(ReadGeometry(r));
            return new GeometryGroup(fillRule, children);
        }

        private static PathGeometry ReadPath(CommandReader r)
        {
            var fillRule = (FillRule)r.U8();
            int figureCount = r.U16();
            var figures = new List<PathFigure>(figureCount);
            for (int f = 0; f < figureCount; f++)
            {
                var figure = new PathFigure(new Vector2(r.F32(), r.F32())) { Closed = r.U8() != 0 };
                int segCount = r.U16();
                for (int s = 0; s < segCount; s++)
                {
                    var kind = (PathSegmentKind)r.U8();
                    switch (kind)
                    {
                        case PathSegmentKind.Line:
                            figure.Segments.Add(new LineSegment(new Vector2(r.F32(), r.F32())));
                            break;
                        case PathSegmentKind.Quadratic:
                            figure.Segments.Add(new QuadraticBezierSegment(
                                new Vector2(r.F32(), r.F32()), new Vector2(r.F32(), r.F32())));
                            break;
                        case PathSegmentKind.Cubic:
                            figure.Segments.Add(new CubicBezierSegment(
                                new Vector2(r.F32(), r.F32()), new Vector2(r.F32(), r.F32()), new Vector2(r.F32(), r.F32())));
                            break;
                    }
                }
                figures.Add(figure);
            }
            return new PathGeometry(fillRule, figures);
        }

        private static StrokeStyle ReadStrokeStyle(CommandReader r)
        {
            double thickness = r.F64();
            var cap = (LineCap)r.U8();
            var join = (LineJoin)r.U8();
            double miterLimit = r.F64();
            double dashOffset = r.F64();
            int dashCount = r.U16();
            double[]? dashes = dashCount > 0 ? new double[dashCount] : null;
            for (int i = 0; i < dashCount; i++) dashes![i] = r.F64();
            return new StrokeStyle(thickness, cap, join, miterLimit, dashes, dashOffset);
        }

        private static Brush ReadBrush(CommandReader r)
        {
            var kind = (BrushKind)r.U8();
            switch (kind)
            {
                case BrushKind.Solid:
                    return new SolidColorBrush(ReadColor(r));
                case BrushKind.LinearGradient:
                {
                    var start = new Vector2(r.F32(), r.F32());
                    var end = new Vector2(r.F32(), r.F32());
                    var spread = (GradientSpreadMethod)r.U8();
                    int stopCount = r.U16();
                    var stops = new GradientStop[stopCount];
                    for (int i = 0; i < stopCount; i++)
                        stops[i] = new GradientStop(r.F32(), ReadColor(r));
                    return new LinearGradientBrush(start, end, stops, spread);
                }
                case BrushKind.RadialGradient:
                {
                    var center = new Vector2(r.F32(), r.F32());
                    float radiusX = r.F32(), radiusY = r.F32();
                    var spread = (GradientSpreadMethod)r.U8();
                    int stopCount = r.U16();
                    var stops = new GradientStop[stopCount];
                    for (int i = 0; i < stopCount; i++)
                        stops[i] = new GradientStop(r.F32(), ReadColor(r));
                    return new RadialGradientBrush(center, radiusX, radiusY, stops, spread);
                }
                case BrushKind.Image:
                {
                    int w = (int)r.U32();
                    int h = (int)r.U32();
                    var tileMode = (TileMode)r.U8();
                    float tileWidth = r.F32();
                    float tileHeight = r.F32();
                    byte[] pixels = r.Bytes(w * h * 4);
                    return new ImageBrush(pixels, w, h, tileMode, tileWidth, tileHeight);
                }
                default:
                    return new SolidColorBrush(new RgbaColor(0, 0, 0, 0));
            }
        }

        private static RgbaColor ReadColor(CommandReader r) => new(r.F32(), r.F32(), r.F32(), r.F32());
    }
}
