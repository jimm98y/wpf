// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// System.Drawing's CompositingMode through the GPU-raster seam.
//
// This is GDI+'s Graphics.CompositingMode, NOT WPF's internal MilCompositingMode (which no
// public WPF API sets). It is reachable here because this fork implements System.Drawing and
// routes its drawing verbs into this renderer.
//
// The scenario is the one from the Microsoft docs, "Use Compositing Mode to Control Alpha
// Blending": two semi-transparent overlapping ellipses drawn with SourceCopy must NOT blend
// with each other -- the second replaces the first in the overlap, alpha included -- while
// SourceOver blends them. Before this, a recording Graphics discarded the property entirely
// (nativeObject == 0 made the setter return early), so SourceCopy silently alpha-blended.
//

using System;
using System.Collections.Generic;
using System.Numerics;
using Microsoft.Wpf.Interop.WebGpu.Composition;

internal static class Program
{
    private const int W = 190, H = 110;
    private static int _failures;

    // alpha 160, as in the docs example.
    private static readonly RgbaColor Red = RgbaColor.FromBytes(255, 0, 0, 160);
    private static readonly RgbaColor Green = RgbaColor.FromBytes(0, 255, 0, 160);

    private static int Main()
    {
        (int r, int g, int b) over = OverlapPixel(sourceCopy: false);
        (int r, int g, int b) copy = OverlapPixel(sourceCopy: true);
        Console.WriteLine($"  overlap pixel  SourceOver=({over.r},{over.g},{over.b})  SourceCopy=({copy.r},{copy.g},{copy.b})");

        // SourceOver: green over red leaves red showing through -> a red component remains.
        Check(over.r > 40 && over.g > 40, "SourceOver blends the ellipses with each other");
        // SourceCopy: the green ellipse REPLACES the red one in the overlap -> no red left.
        Check(copy.r < 20 && copy.g > 40, "SourceCopy replaces instead of blending (no red remains)");

        // SourceCopy replaces the DESTINATION as well, not just earlier shapes: drawing
        // alpha-160 red over white writes the premultiplied source (160,0,0) instead of
        // blending to a pink. The docs example hides this by drawing onto a blank bitmap,
        // where "replace" and "blend over nothing" look the same -- it shows up here because
        // the target is white. This is the behaviour, not a bug, so it is pinned down.
        (int r, int g, int b) soloOver = RedOnlyPixel(sourceCopy: false);
        (int r, int g, int b) soloCopy = RedOnlyPixel(sourceCopy: true);
        Console.WriteLine($"  red-only pixel SourceOver=({soloOver.r},{soloOver.g},{soloOver.b})  SourceCopy=({soloCopy.r},{soloCopy.g},{soloCopy.b})");
        Check(soloOver.r > 240 && soloOver.g > 60,
            "SourceOver blends the lone ellipse with the white background");
        Check(soloCopy.r > 140 && soloCopy.r < 180 && soloCopy.g < 20 && soloCopy.b < 20,
            "SourceCopy writes the premultiplied source over the background (160,0,0)");

        Console.WriteLine();
        if (_failures > 0) { Console.WriteLine($"COMPOSITING MODE TEST FAILED: {_failures} problem(s)."); return 1; }
        Console.WriteLine("COMPOSITING MODE TEST PASSED: SourceCopy replaces, SourceOver blends.");
        return 0;
    }

    private static (int, int, int) OverlapPixel(bool sourceCopy) => Sample(sourceCopy, 90, 50);
    private static (int, int, int) RedOnlyPixel(bool sourceCopy) => Sample(sourceCopy, 20, 20);

    private static (int, int, int) Sample(bool sourceCopy, int x, int y)
    {
        // Red ellipse then a green one overlapping it, exactly as the docs example draws them.
        var root = new SceneVisual();
        root.Content.Add(new GeometryFill(new EllipseGeometry(new Vector2(75, 35), 75, 35), Red)
        { SourceCopy = sourceCopy });
        root.Content.Add(new GeometryFill(new EllipseGeometry(new Vector2(105, 65), 75, 35), Green)
        { SourceCopy = sourceCopy });

        using var ctx = WgpuContext.Create();
        var renderer = new WgpuSceneRenderer(ctx);
        byte[] px = renderer.RenderToRgba(root, W, H, RgbaColor.FromBytes(255, 255, 255, 255));
        int i = (y * W + x) * 4;
        return (px[i], px[i + 1], px[i + 2]);
    }

    private static void Check(bool ok, string what)
    {
        Console.WriteLine($"  [{(ok ? "ok " : "FAIL")}] {what}");
        if (!ok) _failures++;
    }
}
