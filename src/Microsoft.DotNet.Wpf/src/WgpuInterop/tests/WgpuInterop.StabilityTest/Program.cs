// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Phase-1 stability / resource-lifecycle test. A real app renders the same
// renderer thousands of times, so transient GPU objects must be released and
// long-lived ones (the glyph atlas) cached. This renders a scene that uses every
// path (solid, gradient, image, text, AA path) many times and checks:
//   * output stays byte-for-byte identical across iterations (disposal of
//     per-frame resources doesn't corrupt state), and
//   * the glyph atlas is uploaded only once for the whole run (it's cached, not
//     rebuilt every frame).
//

using System;
using System.Numerics;
using Microsoft.Wpf.Interop.WebGpu.Composition;

internal static class Program
{
    private const int W = 96;
    private const int H = 64;
    private const int Iterations = 400;

    private static int Main()
    {
        SceneVisual scene = BuildScene();
        var background = RgbaColor.FromBytes(255, 255, 255, 255);

        using var ctx = WgpuContext.Create();
        using var renderer = new WgpuSceneRenderer(ctx);

        byte[] reference = renderer.RenderToRgba(scene, W, H, background);

        int mismatchedIterations = 0;
        for (int i = 1; i < Iterations; i++)
        {
            byte[] frame = renderer.RenderToRgba(scene, W, H, background);
            if (!Same(frame, reference)) mismatchedIterations++;
        }

        Console.WriteLine($"iterations           = {Iterations}");
        Console.WriteLine($"identical to frame 0 = {Iterations - mismatchedIterations}/{Iterations}");
        Console.WriteLine($"glyph atlas uploads  = {renderer.AtlasUploads}");

        bool stable = mismatchedIterations == 0;
        bool cached = renderer.AtlasUploads == 1;

        if (!stable) Console.Error.WriteLine($"  [BAD] {mismatchedIterations} iterations diverged from frame 0");
        else Console.WriteLine("  [ok ] every iteration is byte-identical (transient resources released safely)");

        if (!cached) Console.Error.WriteLine($"  [BAD] atlas uploaded {renderer.AtlasUploads} times (expected 1 — should be cached)");
        else Console.WriteLine("  [ok ] glyph atlas uploaded exactly once across the whole run (cached)");

        if (stable && cached)
        {
            Console.WriteLine($"STABILITY TEST PASSED: {Iterations} renders, transient resources released and the atlas cached.");
            return 0;
        }
        Console.Error.WriteLine("STABILITY TEST FAILED.");
        return 1;
    }

    // Uses every render path so the per-frame resource churn is exercised.
    private static SceneVisual BuildScene()
    {
        var root = new SceneVisual();

        var solid = new SceneVisual();
        solid.Content.Add(new GeometryFill(new RectangleGeometry(new Rect(2, 2, 12, 12)), RgbaColor.FromBytes(200, 30, 30, 255)));
        root.Children.Add(solid);

        var gradient = new SceneVisual();
        gradient.Content.Add(new GeometryFill(
            new RectangleGeometry(new Rect(18, 2, 30, 12)),
            new LinearGradientBrush(new Vector2(18, 2), new Vector2(48, 2), new[]
            {
                new GradientStop(0f, RgbaColor.FromBytes(255, 0, 0, 255)),
                new GradientStop(1f, RgbaColor.FromBytes(0, 0, 255, 255)),
            })));
        root.Children.Add(gradient);

        byte[] checker = { 255, 0, 0, 255, 0, 255, 0, 255, 0, 0, 255, 255, 255, 255, 0, 255 };
        var image = new SceneVisual();
        image.Content.Add(new GeometryFill(new RectangleGeometry(new Rect(52, 2, 12, 12)), new ImageBrush(checker, 2, 2)));
        root.Children.Add(image);

        var triFigure = new PathFigure(new Vector2(0, 16));
        triFigure.Segments.Add(new LineSegment(new Vector2(16, 16)));
        triFigure.Segments.Add(new LineSegment(new Vector2(16, 0)));
        var path = new SceneVisual { Offset = new Vector2(70, 20) };
        path.Content.Add(new GeometryFill(
            new PathGeometry(FillRule.NonZero, new System.Collections.Generic.List<PathFigure> { triFigure }),
            RgbaColor.FromBytes(20, 120, 200, 255)));
        root.Children.Add(path);

        var text = new SceneVisual();
        text.Content.Add(new GlyphRunDraw("WPF", new Vector2(4, 56), 21f, RgbaColor.FromBytes(0, 0, 0, 255)));
        root.Children.Add(text);

        return root;
    }

    private static bool Same(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }
}
