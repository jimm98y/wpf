// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Does WPF_WEBGPU_LOCAL_COVERAGE_CACHE still earn its keep?
//
// The local-space cache is now gated to rotated/skewed transforms (IsAxisAligned in
// EmitCoverageMask): for axis-aligned content the device-space path already caches by
// integer offset + half-pixel phase, and the local-space one only added bilinear blur.
// That gate is worth nothing if the remaining case does not actually benefit, so this
// counts CPU/GPU coverage rasterizations (PerfCoverage) across an animation:
//
//   spin   -- a path rotating a degree per frame. The device key holds the exact linear
//             part, so it must miss EVERY frame; the local-space key is angle-invariant.
//   scroll -- the same path translating. Both paths should be nearly free.
//   still  -- the same path at a FIXED off-axis angle. Rotated, but not animating, so the
//             device path caches it once and the local cache must keep its hands off: its
//             resample would cost fidelity to save nothing.
//
// The cache is ON by default (WPF_WEBGPU_LOCAL_COVERAGE_CACHE=0 disables it). Run it both
// ways: with the cache on the spin must be cached, and with it off the spin must cost one
// rasterization per angle -- otherwise the "saving" is measured against a scene that never
// rasterized in the first place, and the number means nothing.
//

using System;
using System.Collections.Generic;
using System.Numerics;
using Microsoft.Wpf.Interop.WebGpu.Composition;

internal static class Program
{
    private const int W = 220, H = 220, Frames = 60;

    private static int Main()
    {
        bool lcc = Environment.GetEnvironmentVariable("WPF_WEBGPU_LOCAL_COVERAGE_CACHE") != "0";
        Console.WriteLine($"local coverage cache: {(lcc ? "ON (default)" : "off (WPF_WEBGPU_LOCAL_COVERAGE_CACHE=0)")}");

        (int spin, int spinLocal) = Count(Motion.Spin);
        (int scroll, int scrollLocal) = Count(Motion.Scroll);
        (int still, int stillLocal) = Count(Motion.StillRotated);
        Console.WriteLine($"  over {Frames} frames   rasterizations / routed-to-local-cache");
        Console.WriteLine($"    spinning       {spin,3} / {spinLocal,3}");
        Console.WriteLine($"    scrolling      {scroll,3} / {scrollLocal,3}");
        Console.WriteLine($"    still, rotated {still,3} / {stillLocal,3}");

        // The reason the cache is gated on ANIMATION, and the thing most likely to silently
        // regress: a visual sitting at a fixed angle must stay on the exact device path. The
        // local one would resample its mask through the rotation for a saving of zero -- 59
        // levels on the transform-rotate baseline, forever, on a stationary object.
        if (stillLocal != 0)
        {
            Console.WriteLine($"  [FAIL] a STATIC rotated visual was routed to the resampling cache {stillLocal}x.");
            return 1;
        }
        Console.WriteLine("  [ok ] a static rotated visual stays on the exact device-space path");

        // The scroll case must be cheap either way -- that is the device path's phase cache,
        // and the gate must not have disturbed it.
        if (scroll > Frames / 2)
        {
            Console.WriteLine($"  [FAIL] translation re-rasterized {scroll}x in {Frames} frames; the phase cache is broken.");
            return 1;
        }
        Console.WriteLine("  [ok ] translation is cached (the device-space phase cache is intact)");

        if (lcc)
        {
            // The whole point of the surviving case.
            if (spin > Frames / 2 || spinLocal == 0)
            {
                Console.WriteLine($"  [FAIL] rotation re-rasterized {spin}x in {Frames} frames "
                    + $"({spinLocal} local-cache draws); the cache is not engaging.");
                return 1;
            }
            Console.WriteLine($"  [ok ] an ANIMATING rotation is cached ({spin} rasterizations for {Frames} distinct angles)");
        }
        else
        {
            // Establishes that the ON number above is a real saving, not a scene that never
            // rasterized in the first place.
            if (spin < Frames)
            {
                Console.WriteLine($"  [FAIL] rotation only cost {spin} rasterizations with the cache OFF; nothing to save.");
                return 1;
            }
            Console.WriteLine($"  [ok ] rotation costs {spin} rasterizations without the cache (one per angle)");
        }

        Console.WriteLine(lcc ? "LOCAL CACHE PROBE PASSED (on)" : "LOCAL CACHE PROBE PASSED (off)");
        return 0;
    }

    // A stroked star: miter joins keep it off the fs_stroke SDF fast path, so it goes
    // through EmitCoverageMask -- the code the gate lives in.
    private static PathGeometry Star()
    {
        var pts = new Vector2[10];
        for (int i = 0; i < 10; i++)
        {
            double a = -Math.PI / 2 + i * Math.PI / 5;
            float r = (i % 2 == 0) ? 70f : 30f;
            pts[i] = new Vector2((float)(Math.Cos(a) * r), (float)(Math.Sin(a) * r));
        }
        var fig = new PathFigure(pts[0]) { Closed = true };
        for (int i = 1; i < 10; i++) fig.Segments.Add(new LineSegment(pts[i]));
        return new PathGeometry(FillRule.NonZero, new List<PathFigure> { fig });
    }

    private enum Motion { Spin, Scroll, StillRotated }

    private static (int Raster, int Local) Count(Motion motion)
    {
        using var ctx = WgpuContext.Create();
        var renderer = new WgpuSceneRenderer(ctx);
        PathGeometry star = Star();
        var style = new StrokeStyle(3.0, LineCap.Butt, LineJoin.Miter);

        // Warm past the animation detector's start-up frames (ChurnFramesToCache) so the count
        // reflects steady state. A spin pays full price for its first few frames by design --
        // that is what keeps a STATIC rotated visual on the exact device path forever.
        for (int warm = 0; warm < 8; warm++) Frame(renderer, star, style, motion, -warm);

        WgpuSceneRenderer.PerfReset();
        for (int f = 0; f < Frames; f++) Frame(renderer, star, style, motion, f + 1);
        return (WgpuSceneRenderer.PerfCoverage, WgpuSceneRenderer.PerfLocalCoverage);
    }

    private static void Frame(WgpuSceneRenderer renderer, PathGeometry star, StrokeStyle style, Motion motion, int f)
    {
        Matrix3x2 xf = motion switch
        {
            Motion.Spin => Matrix3x2.CreateRotation(f * (float)(Math.PI / 180.0)) * Matrix3x2.CreateTranslation(110, 110),
            // A fixed off-axis angle: rotated, but nothing changes frame to frame.
            Motion.StillRotated => Matrix3x2.CreateRotation(0.4f) * Matrix3x2.CreateTranslation(110, 110),
            // Fractional translation: this is what the half-pixel phase cache exists for.
            _ => Matrix3x2.CreateTranslation(110 + f * 0.37f % 3f, 110),
        };
        // The live compositor calls BeginFrame per composed frame (WpfCompositionSink); RenderToRgba
        // alone does not advance the frame counter, and the animation detector reads it. Without
        // this the probe presents 60 frames of motion as one frame and nothing looks animated.
        renderer.BeginFrame();
        var vis = new SceneVisual { Transform = xf };
        vis.Content.Add(new GeometryStroke(star, RgbaColor.FromBytes(20, 20, 20, 255), style));
        var root = new SceneVisual();
        root.Children.Add(vis);
        renderer.RenderToRgba(root, W, H, RgbaColor.FromBytes(255, 255, 255, 255));
    }
}
