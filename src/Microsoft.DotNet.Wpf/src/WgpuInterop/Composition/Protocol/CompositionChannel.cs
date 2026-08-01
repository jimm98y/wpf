// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Client side of the protocol: the analog of DUCE.Channel. It serializes
// resource creation and visual property/content/structure commands into a
// batch of bytes. In WPF this is driven by Visual marshalling
// (Visual.RenderContent -> RenderData -> channel commands); here EncodeScene
// walks a SceneVisual tree and emits the equivalent stream, assigning a handle
// to each visual. Commit() returns the batch the CompositionEngine consumes.
//

using System;
using System.Collections.Generic;
using System.Numerics;

namespace Microsoft.Wpf.Interop.WebGpu.Composition.Protocol
{
    internal sealed class CompositionChannel
    {
        private readonly CommandWriter _writer = new();

        public void CreateResource(uint handle, MilResourceType type) =>
            _writer.Record(MilCommand.CreateResource, w => { w.U32(handle); w.U8((byte)type); });

        public void VisualSetOffset(uint handle, Vector2 offset) =>
            _writer.Record(MilCommand.VisualSetOffset, w => { w.U32(handle); w.F64(offset.X); w.F64(offset.Y); });

        public void VisualSetTransform(uint handle, Matrix3x2 m) =>
            _writer.Record(MilCommand.VisualSetTransform, w =>
            {
                w.U32(handle);
                w.F32(m.M11); w.F32(m.M12); w.F32(m.M21); w.F32(m.M22); w.F32(m.M31); w.F32(m.M32);
            });

        public void VisualSetOpacity(uint handle, double opacity) =>
            _writer.Record(MilCommand.VisualSetOpacity, w => { w.U32(handle); w.F64(opacity); });

        public void VisualSetClip(uint handle, Rect? clip) =>
            _writer.Record(MilCommand.VisualSetClip, w =>
            {
                w.U32(handle);
                if (clip is { } c)
                {
                    w.U8(1);
                    w.F32(c.X); w.F32(c.Y); w.F32(c.Width); w.F32(c.Height);
                }
                else
                {
                    w.U8(0);
                }
            });

        public void VisualSetClipGeometry(uint handle, PathGeometry clip) =>
            _writer.Record(MilCommand.VisualSetClipGeometry, w => { w.U32(handle); WritePath(w, clip); });

        public void VisualSetOpacityMask(uint handle, Brush mask) =>
            _writer.Record(MilCommand.VisualSetOpacityMask, w => { w.U32(handle); WriteBrush(w, mask); });

        public void VisualSetEffect(uint handle, Effect effect) =>
            _writer.Record(MilCommand.VisualSetEffect, w =>
            {
                w.U32(handle);
                switch (effect)
                {
                    case BlurEffect blur:
                        w.U8((byte)EffectKind.Blur);
                        w.F64(blur.Radius);
                        w.U8((byte)blur.Kernel);
                        break;
                    case DropShadowEffect ds:
                        w.U8((byte)EffectKind.DropShadow);
                        WriteColor(w, ds.Color);
                        w.F64(ds.BlurRadius);
                        w.F64(ds.OffsetX);
                        w.F64(ds.OffsetY);
                        break;
                    default:
                        w.U8((byte)EffectKind.None);
                        break;
                }
            });

        public void VisualSetContent(uint handle, IReadOnlyList<DrawingPrimitive> content) =>
            _writer.Record(MilCommand.VisualSetContent, w =>
            {
                w.U32(handle);
                byte[] renderData = BuildRenderData(content);
                w.U32((uint)renderData.Length);
                w.Bytes(renderData);
            });

        public void VisualAddChild(uint parent, uint child) =>
            _writer.Record(MilCommand.VisualAddChild, w => { w.U32(parent); w.U32(child); });

        public void TargetSetRoot(uint handle) =>
            _writer.Record(MilCommand.TargetSetRoot, w => w.U32(handle));

        public byte[] Commit() => _writer.ToArray();

        /// <summary>Serializes a whole visual tree into a single batch.</summary>
        public static byte[] EncodeScene(SceneVisual root)
        {
            var channel = new CompositionChannel();
            uint next = 1;
            uint rootHandle = channel.Emit(root, ref next);
            channel.TargetSetRoot(rootHandle);
            return channel.Commit();
        }

        private uint Emit(SceneVisual visual, ref uint next)
        {
            uint handle = next++;
            CreateResource(handle, MilResourceType.Visual);
            VisualSetOffset(handle, visual.Offset);
            VisualSetTransform(handle, visual.Transform);
            VisualSetOpacity(handle, visual.Opacity);
            VisualSetClip(handle, visual.Clip);
            if (visual.ClipGeometry is { } clipGeometry)
                VisualSetClipGeometry(handle, clipGeometry);
            if (visual.OpacityMask is { } opacityMask)
                VisualSetOpacityMask(handle, opacityMask);
            if (visual.Effect is { } effect)
                VisualSetEffect(handle, effect);
            if (visual.Content.Count > 0)
                VisualSetContent(handle, visual.Content);

            foreach (SceneVisual child in visual.Children)
            {
                uint childHandle = Emit(child, ref next);
                VisualAddChild(handle, childHandle);
            }
            return handle;
        }

        private static byte[] BuildRenderData(IReadOnlyList<DrawingPrimitive> content)
        {
            var w = new CommandWriter();
            foreach (DrawingPrimitive primitive in content)
            {
                switch (primitive)
                {
                    case GeometryFill { Geometry: RectangleGeometry rg } fill:
                        w.U8((byte)RenderDataOp.FillRectangle);
                        w.F32(rg.Rect.X); w.F32(rg.Rect.Y); w.F32(rg.Rect.Width); w.F32(rg.Rect.Height);
                        WriteBrush(w, fill.Brush);
                        break;
                    case GeometryFill { Geometry: PolygonGeometry pg } fill:
                        w.U8((byte)RenderDataOp.FillPolygon);
                        w.U16((ushort)pg.Points.Length);
                        foreach (Vector2 p in pg.Points) { w.F32(p.X); w.F32(p.Y); }
                        WriteBrush(w, fill.Brush);
                        break;
                    case GeometryFill { Geometry: RoundedRectangleGeometry rr } fill:
                        w.U8((byte)RenderDataOp.FillRoundedRectangle);
                        w.F32(rr.Rect.X); w.F32(rr.Rect.Y); w.F32(rr.Rect.Width); w.F32(rr.Rect.Height);
                        w.F32(rr.RadiusX); w.F32(rr.RadiusY);
                        WriteBrush(w, fill.Brush);
                        break;
                    case GeometryFill { Geometry: EllipseGeometry el } fill:
                        w.U8((byte)RenderDataOp.FillEllipse);
                        w.F32(el.Center.X); w.F32(el.Center.Y); w.F32(el.RadiusX); w.F32(el.RadiusY);
                        WriteBrush(w, fill.Brush);
                        break;
                    case GeometryFill { Geometry: GeometryGroup grp } fill:
                        w.U8((byte)RenderDataOp.FillGeometryGroup);
                        WriteGeometryGroup(w, grp);
                        WriteBrush(w, fill.Brush);
                        break;
                    case GeometryFill { Geometry: CombinedGeometry cg } fill:
                        w.U8((byte)RenderDataOp.FillCombinedGeometry);
                        WriteGeometry(w, cg);
                        WriteBrush(w, fill.Brush);
                        break;
                    case GeometryFill { Geometry: PathGeometry path } fill:
                        w.U8((byte)RenderDataOp.FillPath);
                        WritePath(w, path);
                        WriteBrush(w, fill.Brush);
                        break;
                    case GeometryStroke stroke:
                        w.U8((byte)RenderDataOp.StrokePath);
                        WritePath(w, stroke.Geometry);
                        WriteStrokeStyle(w, stroke.Style);
                        WriteBrush(w, stroke.Brush);
                        break;
                    case GeometryDrawing drawing:
                        w.U8((byte)RenderDataOp.DrawGeometry);
                        WriteGeometry(w, drawing.Geometry);
                        if (drawing.Fill is { } fill2) { w.U8(1); WriteBrush(w, fill2); } else w.U8(0);
                        if (drawing.Stroke is { } stroke2) { w.U8(1); WriteBrush(w, stroke2); WriteStrokeStyle(w, drawing.StrokeStyle); } else w.U8(0);
                        break;
                    case GlyphRunDraw run:
                        w.U8((byte)RenderDataOp.DrawGlyphRun);
                        w.F32(run.Origin.X); w.F32(run.Origin.Y);
                        w.F32(run.EmSize);
                        WriteColor(w, run.Color);
                        w.U16((ushort)run.Text.Length);
                        foreach (char ch in run.Text) w.U16(ch);
                        break;
                }
            }
            return w.ToArray();
        }

        // Recursive geometry serializer (used by geometry groups, which may
        // contain any geometry kind, including nested groups).
        private static void WriteGeometry(CommandWriter w, Geometry geometry)
        {
            switch (geometry)
            {
                case RectangleGeometry r:
                    w.U8((byte)GeometryKind.Rectangle);
                    w.F32(r.Rect.X); w.F32(r.Rect.Y); w.F32(r.Rect.Width); w.F32(r.Rect.Height);
                    break;
                case PolygonGeometry p:
                    w.U8((byte)GeometryKind.Polygon);
                    w.U16((ushort)p.Points.Length);
                    foreach (Vector2 pt in p.Points) { w.F32(pt.X); w.F32(pt.Y); }
                    break;
                case RoundedRectangleGeometry rr:
                    w.U8((byte)GeometryKind.RoundedRectangle);
                    w.F32(rr.Rect.X); w.F32(rr.Rect.Y); w.F32(rr.Rect.Width); w.F32(rr.Rect.Height);
                    w.F32(rr.RadiusX); w.F32(rr.RadiusY);
                    break;
                case EllipseGeometry e:
                    w.U8((byte)GeometryKind.Ellipse);
                    w.F32(e.Center.X); w.F32(e.Center.Y); w.F32(e.RadiusX); w.F32(e.RadiusY);
                    break;
                case GeometryGroup grp:
                    w.U8((byte)GeometryKind.Group);
                    WriteGeometryGroup(w, grp);
                    break;
                case CombinedGeometry cg:
                    w.U8((byte)GeometryKind.Combined);
                    w.U8((byte)cg.Mode);
                    WriteGeometry(w, cg.Geometry1);
                    WriteGeometry(w, cg.Geometry2);
                    break;
                default:
                    w.U8((byte)GeometryKind.Path);
                    WritePath(w, (PathGeometry)geometry);
                    break;
            }
        }

        private static void WriteGeometryGroup(CommandWriter w, GeometryGroup grp)
        {
            w.U8((byte)grp.FillRule);
            w.U16((ushort)grp.Children.Count);
            foreach (Geometry child in grp.Children)
                WriteGeometry(w, child);
        }

        private static void WritePath(CommandWriter w, PathGeometry path)
        {
            w.U8((byte)path.FillRule);
            w.U16((ushort)path.Figures.Count);
            foreach (PathFigure figure in path.Figures)
            {
                w.F32(figure.Start.X); w.F32(figure.Start.Y);
                w.U8(figure.Closed ? (byte)1 : (byte)0);
                w.U16((ushort)figure.Segments.Count);
                foreach (PathSegment seg in figure.Segments)
                {
                    switch (seg)
                    {
                        case LineSegment l:
                            w.U8((byte)PathSegmentKind.Line);
                            w.F32(l.Point.X); w.F32(l.Point.Y);
                            break;
                        case QuadraticBezierSegment q:
                            w.U8((byte)PathSegmentKind.Quadratic);
                            w.F32(q.Control.X); w.F32(q.Control.Y);
                            w.F32(q.Point.X); w.F32(q.Point.Y);
                            break;
                        case CubicBezierSegment c:
                            w.U8((byte)PathSegmentKind.Cubic);
                            w.F32(c.Control1.X); w.F32(c.Control1.Y);
                            w.F32(c.Control2.X); w.F32(c.Control2.Y);
                            w.F32(c.Point.X); w.F32(c.Point.Y);
                            break;
                    }
                }
            }
        }

        private static void WriteStrokeStyle(CommandWriter w, StrokeStyle style)
        {
            w.F64(style.Thickness);
            w.U8((byte)style.Cap);
            w.U8((byte)style.Join);
            w.F64(style.MiterLimit);
            w.F64(style.DashOffset);
            double[] dashes = style.DashArray ?? System.Array.Empty<double>();
            w.U16((ushort)dashes.Length);
            foreach (double d in dashes) w.F64(d);
        }

        private static void WriteBrush(CommandWriter w, Brush brush)
        {
            switch (brush)
            {
                case SolidColorBrush s:
                    w.U8((byte)BrushKind.Solid);
                    WriteColor(w, s.Color);
                    break;
                case LinearGradientBrush g:
                    w.U8((byte)BrushKind.LinearGradient);
                    w.F32(g.Start.X); w.F32(g.Start.Y); w.F32(g.End.X); w.F32(g.End.Y);
                    w.U8((byte)g.SpreadMethod);
                    w.U16((ushort)g.Stops.Length);
                    foreach (GradientStop stop in g.Stops) { w.F32(stop.Offset); WriteColor(w, stop.Color); }
                    break;
                case RadialGradientBrush rad:
                    w.U8((byte)BrushKind.RadialGradient);
                    w.F32(rad.Center.X); w.F32(rad.Center.Y); w.F32(rad.RadiusX); w.F32(rad.RadiusY);
                    w.U8((byte)rad.SpreadMethod);
                    w.U16((ushort)rad.Stops.Length);
                    foreach (GradientStop stop in rad.Stops) { w.F32(stop.Offset); WriteColor(w, stop.Color); }
                    break;
                case ImageBrush img:
                    w.U8((byte)BrushKind.Image);
                    w.U32((uint)img.PixelWidth);
                    w.U32((uint)img.PixelHeight);
                    w.U8((byte)img.TileMode);
                    w.F32(img.TileWidth);
                    w.F32(img.TileHeight);
                    w.Bytes(img.PixelsRgba);
                    break;
                default:
                    throw new NotSupportedException($"Unsupported brush: {brush.GetType().Name}");
            }
        }

        private static void WriteColor(CommandWriter w, RgbaColor c)
        {
            w.F32(c.R); w.F32(c.G); w.F32(c.B); w.F32(c.A);
        }
    }
}
