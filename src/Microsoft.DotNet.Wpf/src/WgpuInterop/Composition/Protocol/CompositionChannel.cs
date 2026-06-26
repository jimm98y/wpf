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
                    case GeometryFill { Geometry: PathGeometry path } fill:
                        w.U8((byte)RenderDataOp.FillPath);
                        WritePath(w, path);
                        WriteBrush(w, fill.Brush);
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
                    w.U16((ushort)g.Stops.Length);
                    foreach (GradientStop stop in g.Stops) { w.F32(stop.Offset); WriteColor(w, stop.Color); }
                    break;
                case ImageBrush img:
                    w.U8((byte)BrushKind.Image);
                    w.U32((uint)img.PixelWidth);
                    w.U32((uint)img.PixelHeight);
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
