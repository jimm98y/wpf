// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The scene catalogue for the whole-image baseline test, lifted VERBATIM from
// WgpuInterop.RenderBaselineTest so the committed baseline PNGs stay valid.
//
// These builders are the contract: change one and every baseline it feeds has to be regenerated and
// re-reviewed. They were moved rather than rewritten precisely because a transcription slip would
// silently alter what the baselines guard, and the PNGs would be "updated" to match the mistake.
//

using System;
using System.Collections.Generic;
using System.Numerics;
using Microsoft.Wpf.Interop.WebGpu.Composition;
using Microsoft.Wpf.Interop.WebGpu.Composition.Protocol;

namespace WgpuInterop.Tests.Baseline
{
    internal static class BaselineScenes
    {
    // ---- scene catalogue ----

    public static IEnumerable<(string, int, int, Func<SceneVisual>)> Scenes()
    {
        yield return ("curves-fill", 260, 180, CurvesFill);
        yield return ("curves-zoom8x", 300, 300, () => Zoomed(8f));
        yield return ("strokes-joins", 320, 200, StrokeJoins);
        yield return ("strokes-dashed", 300, 140, StrokesDashed);
        yield return ("gradients", 320, 200, Gradients);
        yield return ("clip-opacity", 240, 180, ClipOpacity);
        yield return ("combined-geometry", 300, 120, Combined);
        yield return ("transform-rotate", 240, 240, RotatedContent);
        yield return ("mil-arc-large", 460, 260, MilArcLarge);
        yield return ("text-run", 300, 120, TextRun);
        yield return ("effects", 380, 160, Effects);
    }

    // Goes through the MIL protocol's arc-to-Bézier conversion, which nothing else in the
    // catalogue touches -- a mutation test proved that reverting that conversion to its old
    // fixed 90-degree split changed no baseline at all. The radius is large on purpose: the
    // fixed split's error is proportional to it (~2.7e-4*r) and vanishes on small corners.
    private static SceneVisual MilArcLarge()
    {
        var root = new SceneVisual();
        const float r = 400f;
        var start = new Vector2(30, 240);
        var f = new PathFigure(start) { Closed = false };
        MilcoreEngine.AddArcAsBeziersForTest(f, start, new Vector2(430, 240), r, r, 0f,
            largeArc: false, sweepClockwise: true);
        root.Content.Add(new GeometryStroke(
            new PathGeometry(FillRule.NonZero, new List<PathFigure> { f }),
            RgbaColor.FromBytes(160, 20, 40, 255),
            new StrokeStyle(6, LineCap.Butt, LineJoin.Round)));

        // A rotated elliptical arc too: exercises the xRotation and radii-correction branches.
        var s2 = new Vector2(60, 60);
        var f2 = new PathFigure(s2) { Closed = false };
        MilcoreEngine.AddArcAsBeziersForTest(f2, s2, new Vector2(400, 120), 260f, 120f, 35f,
            largeArc: false, sweepClockwise: false);
        root.Content.Add(new GeometryStroke(
            new PathGeometry(FillRule.NonZero, new List<PathFigure> { f2 }),
            RgbaColor.FromBytes(20, 80, 160, 255),
            new StrokeStyle(5, LineCap.Round, LineJoin.Round)));
        return root;
    }

    // Text is the single biggest consumer of the coverage path (every glyph is a filled
    // path) and had no whole-image coverage at all.
    private static SceneVisual TextRun()
    {
        var root = new SceneVisual();
        root.Content.Add(new GlyphRunDraw("Wgpu 123", new Vector2(14, 44), 30f,
            RgbaColor.FromBytes(15, 15, 15, 255)));
        root.Content.Add(new GlyphRunDraw("small caps", new Vector2(14, 84), 13f,
            RgbaColor.FromBytes(70, 70, 110, 255)));
        return root;
    }

    // Effects had NO whole-image coverage, which is why adding BlurEffect.KernelType
    // changed no baseline at all. Both kernels are here so a change to either is caught.
    private static SceneVisual Effects()
    {
        var root = new SceneVisual();
        SceneVisual Card(float x, Effect e, RgbaColor c)
        {
            var v = new SceneVisual { Effect = e };
            v.Content.Add(new GeometryFill(new RoundedRectangleGeometry(new Rect(x, 40, 80, 60), 10, 10), c));
            return v;
        }
        root.Children.Add(Card(20, new BlurEffect(9, BlurKernelType.Gaussian), RgbaColor.FromBytes(200, 50, 50, 255)));
        root.Children.Add(Card(120, new BlurEffect(9, BlurKernelType.Box), RgbaColor.FromBytes(50, 120, 200, 255)));
        root.Children.Add(Card(220, new DropShadowEffect(RgbaColor.FromBytes(0, 0, 0, 180), 8, 5, 5),
            RgbaColor.FromBytes(40, 160, 90, 255)));
        return root;
    }

    private static SceneVisual CurvesFill()
    {
        var root = new SceneVisual();
        foreach ((float cx, float r) in new[] { (40f, 12f), (100f, 28f), (190f, 50f) })
            root.Content.Add(new GeometryFill(new EllipseGeometry(new Vector2(cx, 60), r, r),
                RgbaColor.FromBytes(210, 70, 60, 255)));
        root.Content.Add(new GeometryFill(new RoundedRectangleGeometry(new Rect(20, 120, 100, 44), 16, 16),
            RgbaColor.FromBytes(50, 110, 200, 255)));
        root.Content.Add(new GeometryFill(new RoundedRectangleGeometry(new Rect(140, 120, 100, 44), 2, 2),
            RgbaColor.FromBytes(40, 150, 90, 255)));
        return root;
    }

    // The case fixed subdivision got wrong: small local geometry magnified by the world
    // transform. Faceting here is exactly what the baseline is meant to pin down.
    private static SceneVisual Zoomed(float scale)
    {
        var root = new SceneVisual { Transform = Matrix3x2.CreateScale(scale) };
        root.Content.Add(new GeometryFill(new EllipseGeometry(new Vector2(18, 18), 16, 16),
            RgbaColor.FromBytes(30, 30, 30, 255)));
        root.Content.Add(new GeometryFill(new RoundedRectangleGeometry(new Rect(2, 26, 33, 8), 3.5f, 3.5f),
            RgbaColor.FromBytes(200, 120, 0, 255)));
        return root;
    }

    private static SceneVisual StrokeJoins()
    {
        var root = new SceneVisual();
        var joins = new[] { LineJoin.Miter, LineJoin.Bevel, LineJoin.Round };
        var caps = new[] { LineCap.Butt, LineCap.Square, LineCap.Round };
        for (int i = 0; i < 3; i++)
        {
            float x = 30 + i * 100;
            var f = new PathFigure(new Vector2(x, 150)) { Closed = false };
            f.Segments.Add(new LineSegment(new Vector2(x + 35, 40)));
            f.Segments.Add(new LineSegment(new Vector2(x + 70, 150)));
            root.Content.Add(new GeometryStroke(
                new PathGeometry(FillRule.NonZero, new List<PathFigure> { f }),
                RgbaColor.FromBytes(20, 60, 140, 255),
                new StrokeStyle(14, caps[i], joins[i])));
        }
        return root;
    }

    private static SceneVisual StrokesDashed()
    {
        var root = new SceneVisual();
        var f = new PathFigure(new Vector2(20, 70)) { Closed = false };
        f.Segments.Add(new CubicBezierSegment(new Vector2(100, 0), new Vector2(200, 140), new Vector2(280, 70)));
        root.Content.Add(new GeometryStroke(
            new PathGeometry(FillRule.NonZero, new List<PathFigure> { f }),
            RgbaColor.FromBytes(150, 30, 120, 255),
            new StrokeStyle(9, LineCap.Round, LineJoin.Round, 10.0, new double[] { 3, 2 })));
        return root;
    }

    private static SceneVisual Gradients()
    {
        var root = new SceneVisual();
        var stops = new[]
        {
            new GradientStop(0f, RgbaColor.FromBytes(255, 220, 0, 255)),
            new GradientStop(0.5f, RgbaColor.FromBytes(230, 60, 40, 255)),
            new GradientStop(1f, RgbaColor.FromBytes(20, 60, 160, 255)),
        };
        root.Content.Add(new GeometryFill(new RectangleGeometry(new Rect(15, 15, 130, 80)),
            new LinearGradientBrush(new Vector2(15, 15), new Vector2(145, 95), stops, GradientSpreadMethod.Pad)));
        root.Content.Add(new GeometryFill(new RectangleGeometry(new Rect(170, 15, 130, 80)),
            new LinearGradientBrush(new Vector2(170, 15), new Vector2(212, 55), stops, GradientSpreadMethod.Reflect)));
        root.Content.Add(new GeometryFill(new EllipseGeometry(new Vector2(80, 150), 60, 38),
            new RadialGradientBrush(new Vector2(80, 150), 60f, 38f, stops, GradientSpreadMethod.Pad)));
        root.Content.Add(new GeometryFill(new RoundedRectangleGeometry(new Rect(170, 112, 130, 76), 18, 18),
            new LinearGradientBrush(new Vector2(170, 112), new Vector2(300, 188), stops, GradientSpreadMethod.Pad)));
        return root;
    }

    private static SceneVisual ClipOpacity()
    {
        var root = new SceneVisual();
        var clip = new PathFigure(new Vector2(120, 20)) { Closed = true };
        clip.Segments.Add(new LineSegment(new Vector2(220, 150)));
        clip.Segments.Add(new LineSegment(new Vector2(20, 150)));
        var group = new SceneVisual
        {
            Opacity = 0.55,
            ClipGeometry = new PathGeometry(FillRule.NonZero, new List<PathFigure> { clip }),
        };
        group.Content.Add(new GeometryFill(new RectangleGeometry(new Rect(10, 10, 220, 160)),
            RgbaColor.FromBytes(200, 40, 40, 255)));
        group.Content.Add(new GeometryFill(new EllipseGeometry(new Vector2(120, 110), 70, 45),
            RgbaColor.FromBytes(20, 90, 200, 255)));
        root.Children.Add(group);
        return root;
    }

    private static SceneVisual Combined()
    {
        var root = new SceneVisual();
        var modes = new[] { GeometryCombineMode.Union, GeometryCombineMode.Intersect,
                            GeometryCombineMode.Xor, GeometryCombineMode.Exclude };
        for (int i = 0; i < modes.Length; i++)
        {
            float x = 20 + i * 72;
            root.Content.Add(new GeometryFill(new CombinedGeometry(modes[i],
                    new EllipseGeometry(new Vector2(x, 60), 26, 26),
                    new EllipseGeometry(new Vector2(x + 26, 60), 26, 26)),
                RgbaColor.FromBytes(70, 70, 160, 255)));
        }
        return root;
    }

    private static SceneVisual RotatedContent()
    {
        var root = new SceneVisual
        {
            Transform = Matrix3x2.CreateRotation(0.4f, new Vector2(120, 120)) * Matrix3x2.CreateScale(1.0f, 1.35f),
        };
        root.Content.Add(new GeometryFill(new RectangleGeometry(new Rect(40, 40, 160, 70)),
            RgbaColor.FromBytes(180, 90, 30, 255)));
        root.Content.Add(new GeometryFill(new EllipseGeometry(new Vector2(120, 140), 62, 34),
            RgbaColor.FromBytes(30, 140, 120, 255)));
        return root;
    }
    }
}
