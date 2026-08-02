// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// GuidelineSet pixel snapping.
//
// WPF controls emit a GuidelineSet so that a 1px border lands ON a device pixel boundary.
// This renderer used to drop MILCMD_VISUAL_SETGUIDELINECOLLECTION entirely -- 441 of them
// arrived in a 20-second WPF Gallery session -- so every thin line at a fractional
// position painted as two half-covered rows.
//
// The check is crispness, not just "something changed": a snapped 1px line must produce
// ONE fully-covered row, while the unsnapped one produces two partial rows. Measured as
// peak coverage and as the count of partially-covered rows.
//

using System;
using System.Collections.Generic;
using System.Numerics;
using Microsoft.Wpf.Interop.WebGpu.Composition;

internal static class Program
{
    private const int W = 40, H = 24;
    private static int _failures;

    private static int Main()
    {
        Console.WriteLine("A 1px bar at a fractional device offset. peak=255 with a single");
        Console.WriteLine("covered row is crisp; a split across two partial rows is soft.\n");
        Console.WriteLine($"{"offset",10}{"unsnapped",22}{"with guidelines",24}");
        Console.WriteLine(new string('-', 58));

        int softWithout = 0, total = 0;
        foreach (float frac in new[] { 0.0f, 0.1f, 0.25f, 0.3f, 0.5f, 0.6f, 0.75f, 0.9f })
        {
            (int peakOff, int partOff) = Render(frac, snap: false);
            (int peakOn, int partOn) = Render(frac, snap: true);
            bool crispOff = Crisp(peakOff, partOff), crispOn = Crisp(peakOn, partOn);
            total++;
            if (!crispOff) softWithout++;

            Console.WriteLine($"{"+" + frac.ToString("0.00"),10}"
                + $"{$"peak={peakOff} rows={partOff} {(crispOff ? "crisp" : "SOFT")}",22}"
                + $"{$"peak={peakOn} rows={partOn} {(crispOn ? "crisp" : "SOFT")}",24}");

            // The contract: WITH guidelines the line is crisp at every sub-pixel offset.
            // Without them it is a coin flip -- the renderer's existing half-pixel phase
            // snapping happens to save some fractions -- so that side is reported, not asserted.
            if (!crispOn)
            {
                Console.WriteLine($"  [FAIL] guidelines did not snap offset +{frac:0.00}: peak={peakOn} partialRows={partOn}");
                _failures++;
            }
        }

        Console.WriteLine();
        Console.WriteLine($"without guidelines: {softWithout}/{total} offsets render soft; with guidelines: 0/{total}.");
        // Self-check: if the unsnapped path were already crisp everywhere, the comparison
        // above would be vacuous. That is a real failure on the GPU coverage path -- but NOT
        // under WPF_WEBGPU_CPU_RASTER=1, where the CPU rasterizer bakes each mask at an integer
        // local origin and so is already pixel-aligned for axis-aligned rects. Guidelines are
        // a no-op there by construction.
        bool cpuRaster = Environment.GetEnvironmentVariable("WPF_WEBGPU_CPU_RASTER") == "1";
        if (softWithout == 0 && !cpuRaster)
        {
            Console.WriteLine("  [FAIL] the unsnapped path was crisp everywhere, so this test proves nothing.");
            _failures++;
        }
        else if (softWithout == 0)
        {
            Console.WriteLine("  (cpu raster: already pixel-aligned, so guidelines change nothing here)");
        }

        // The case guidelines exist for: fractional DPI scaling. At 1.25x/1.5x a 1px logical
        // border is 1.25/1.5 DEVICE pixels, so both its edges land mid-pixel and it straddles
        // no matter what the sub-pixel phase is. This is the everyday Windows configuration.
        Console.WriteLine();
        Console.WriteLine($"{"dpi scale",10}{"unsnapped",22}{"with guidelines",24}");
        Console.WriteLine(new string('-', 58));
        int softScaled = 0, totalScaled = 0;
        foreach (float scale in new[] { 1.25f, 1.5f, 1.75f })
        {
            (int pOff, int rOff) = RenderScaled(scale, snap: false);
            (int pOn, int rOn) = RenderScaled(scale, snap: true);
            bool cOff = Crisp(pOff, rOff), cOn = Crisp(pOn, rOn);
            totalScaled++; if (!cOff) softScaled++;
            Console.WriteLine($"{scale + "x",10}{$"peak={pOff} rows={rOff} {(cOff ? "crisp" : "SOFT")}",22}"
                + $"{$"peak={pOn} rows={rOn} {(cOn ? "crisp" : "SOFT")}",24}");
            if (!cOn)
            {
                Console.WriteLine($"  [FAIL] guidelines did not snap at {scale}x: peak={pOn} rows={rOn}");
                _failures++;
            }
        }
        Console.WriteLine($"fractional DPI: {softScaled}/{totalScaled} soft unsnapped, 0/{totalScaled} with guidelines.");

        Console.WriteLine();
        if (_failures > 0) { Console.WriteLine($"GUIDELINE TEST FAILED: {_failures} problem(s)."); return 1; }
        Console.WriteLine("GUIDELINE TEST PASSED: guidelines snap a 1px line onto one pixel row at every offset.");
        return 0;
    }

    // Same 1px logical bar, but under a DPI scale so its device height is fractional.
    private static (int, int) RenderScaled(float scale, bool snap)
    {
        // 8.4 dip, not 8: at 1.25x, 8*1.25 is exactly 10.0 and the bar is accidentally aligned,
        // which hid the effect completely. Real layout rarely lands on such values.
        const float barY = 8.4f;
        int w = (int)(W * scale) + 4, h = (int)(H * scale) + 4;
        var child = new SceneVisual { Transform = Matrix3x2.CreateScale(scale) };
        if (snap) child.GuidelinesY = new[] { barY, barY + 1f };
        child.Content.Add(new GeometryFill(new RectangleGeometry(new Rect(6, barY, 28, 1)),
            RgbaColor.FromBytes(0, 0, 0, 255)));
        var root = new SceneVisual();
        root.Children.Add(child);

        using var ctx = WgpuContext.Create();
        var renderer = new WgpuSceneRenderer(ctx);
        byte[] px = renderer.RenderToRgba(root, w, h, RgbaColor.FromBytes(255, 255, 255, 255));

        int peak = 0, partial = 0;
        int col = (int)(20 * scale);
        for (int y = 0; y < h; y++)
        {
            int cov = 255 - px[(y * w + col) * 4];
            peak = Math.Max(peak, cov);
            if (cov > 12 && cov < 243) partial++;
        }
        return (peak, partial);
    }

    // No partially covered rows at all: a single half-covered row IS the soft edge this
    // is about. Allowing one masked the fractional-DPI case entirely.
    private static bool Crisp(int peak, int partialRows) => peak >= 250 && partialRows == 0;

    // Returns (peak coverage 0-255 over the bar's column, number of partially covered rows).
    private static (int, int) Render(float fracY, bool snap)
    {
        const float barY = 8f;
        var child = new SceneVisual { Offset = new Vector2(0, fracY) };
        if (snap)
        {
            // What a Border emits: the two edges of the 1px line, in LOCAL space.
            child.GuidelinesY = new[] { barY, barY + 1f };
        }
        child.Content.Add(new GeometryFill(new RectangleGeometry(new Rect(6, barY, 28, 1)),
            RgbaColor.FromBytes(0, 0, 0, 255)));
        var root = new SceneVisual();
        root.Children.Add(child);

        using var ctx = WgpuContext.Create();
        var renderer = new WgpuSceneRenderer(ctx);
        byte[] px = renderer.RenderToRgba(root, W, H, RgbaColor.FromBytes(255, 255, 255, 255));

        int peak = 0, partial = 0;
        for (int y = 0; y < H; y++)
        {
            int cov = 255 - px[(y * W + 20) * 4];        // black on white, mid-bar column
            peak = Math.Max(peak, cov);
            if (cov > 12 && cov < 243) partial++;
        }
        return (peak, partial);
    }
}
