// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// BlurEffect.KernelType (Gaussian vs Box).
//
// MILCMD_BLUREFFECT carries KernelType at offset 20, but the decoder used to stop after
// Radius@8, so a Box blur silently rendered as a Gaussian one. Two things are checked
// here, because either alone is weak: that Box DIFFERS from Gaussian (otherwise the
// plumbing is dead and nothing would notice), and that each matches an independent
// CPU reference convolution of the same unblurred image (otherwise both could be wrong
// in the same way).
//
// Also covers the protocol round-trip: the kernel has to survive encode/decode, which
// is where a new field is easiest to forget.
//

using System;
using System.Collections.Generic;
using System.Numerics;
using Microsoft.Wpf.Interop.WebGpu.Composition;
using Microsoft.Wpf.Interop.WebGpu.Composition.Protocol;

internal static class Program
{
    private const int W = 96, H = 96;
    private const double Radius = 12;
    private static int _failures;

    private static int Main()
    {
        byte[] gauss = Render(new BlurEffect(Radius, BlurKernelType.Gaussian));
        byte[] box = Render(new BlurEffect(Radius, BlurKernelType.Box));
        byte[] sharp = Render(null);

        // 1. The two kernels must produce visibly different images.
        (int diffPx, int maxDelta) = Compare(gauss, box);
        Report(diffPx > 200 && maxDelta > 20,
            $"Box differs from Gaussian ({diffPx} px differ, max delta {maxDelta})");

        // 2. Each must match a CPU reference convolution of the unblurred image. The
        //    renderer blurs premultiplied RGBA separably in two passes; the reference does
        //    the same thing on the CPU from `sharp`, so agreement is a real cross-check
        //    rather than a restatement of the shader.
        CheckAgainstReference("gaussian", gauss, sharp, gaussian: true);
        CheckAgainstReference("box", box, sharp, gaussian: false);

        // 3. Protocol round-trip must preserve the kernel.
        foreach (BlurKernelType k in new[] { BlurKernelType.Gaussian, BlurKernelType.Box })
        {
            SceneVisual root = Scene(new BlurEffect(Radius, k));
            var engine = new CompositionEngine();
            engine.ProcessBatch(CompositionChannel.EncodeScene(root));
            var rebuilt = engine.Root?.Children[0].Effect as BlurEffect;
            Report(rebuilt != null && rebuilt.Kernel == k && rebuilt.Radius == Radius,
                $"protocol round-trip preserves KernelType.{k}");
        }

        Console.WriteLine();
        if (_failures > 0) { Console.WriteLine($"BLUR KERNEL TEST FAILED: {_failures} problem(s)."); return 1; }
        Console.WriteLine("BLUR KERNEL TEST PASSED: Gaussian and Box are distinct and both match a CPU reference.");
        return 0;
    }

    private static void CheckAgainstReference(string name, byte[] actual, byte[] sharp, bool gaussian)
    {
        int taps = Math.Clamp((int)Math.Ceiling(Radius), 1, 48);
        double sigma = Math.Max(0.5, Radius / 3.0);
        var weights = new double[2 * taps + 1];
        double wsum = 0;
        for (int i = -taps; i <= taps; i++)
        {
            double w = gaussian ? Math.Exp(-(i * (double)i) / (2 * sigma * sigma)) : 1.0;
            weights[i + taps] = w; wsum += w;
        }
        for (int i = 0; i < weights.Length; i++) weights[i] /= wsum;

        byte[] tmp = Convolve(sharp, weights, taps, horizontal: true);
        byte[] reference = Convolve(tmp, weights, taps, horizontal: false);

        // Compare only the interior: the renderer blurs a region-sized layer whose edges
        // clamp differently from this reference, and that difference is not the thing
        // under test.
        int worst = 0; long sum = 0; int n = 0;
        for (int y = taps + 2; y < H - taps - 2; y++)
        for (int x = taps + 2; x < W - taps - 2; x++)
        for (int c = 0; c < 3; c++)
        {
            int d = Math.Abs(actual[(y * W + x) * 4 + c] - reference[(y * W + x) * 4 + c]);
            worst = Math.Max(worst, d); sum += d; n++;
        }
        double mean = n > 0 ? sum / (double)n : 0;
        Report(worst <= 12 && mean <= 3.0,
            $"{name} matches CPU reference convolution (max {worst}, mean {mean:F2})");
    }

    private static byte[] Convolve(byte[] src, double[] weights, int taps, bool horizontal)
    {
        var dst = new byte[src.Length];
        for (int y = 0; y < H; y++)
        for (int x = 0; x < W; x++)
        {
            double r = 0, g = 0, b = 0, a = 0;
            for (int i = -taps; i <= taps; i++)
            {
                int sx = horizontal ? Math.Clamp(x + i, 0, W - 1) : x;
                int sy = horizontal ? y : Math.Clamp(y + i, 0, H - 1);
                double w = weights[i + taps];
                int o = (sy * W + sx) * 4;
                r += src[o] * w; g += src[o + 1] * w; b += src[o + 2] * w; a += src[o + 3] * w;
            }
            int d = (y * W + x) * 4;
            dst[d] = (byte)Math.Clamp(r, 0, 255); dst[d + 1] = (byte)Math.Clamp(g, 0, 255);
            dst[d + 2] = (byte)Math.Clamp(b, 0, 255); dst[d + 3] = (byte)Math.Clamp(a, 0, 255);
        }
        return dst;
    }

    private static SceneVisual Scene(Effect effect)
    {
        var root = new SceneVisual();
        var child = new SceneVisual { Effect = effect };
        // A hard-edged square: a step edge is where two kernels of equal radius differ most.
        child.Content.Add(new GeometryFill(new RectangleGeometry(new Rect(28, 28, 40, 40)),
            RgbaColor.FromBytes(0, 0, 0, 255)));
        root.Children.Add(child);
        return root;
    }

    private static byte[] Render(Effect effect)
    {
        using var ctx = WgpuContext.Create();
        var renderer = new WgpuSceneRenderer(ctx);
        return renderer.RenderToRgba(Scene(effect), W, H, RgbaColor.FromBytes(255, 255, 255, 255));
    }

    private static (int, int) Compare(byte[] a, byte[] b)
    {
        int diff = 0, max = 0;
        for (int i = 0; i < W * H; i++)
        {
            int d = 0;
            for (int c = 0; c < 3; c++) d = Math.Max(d, Math.Abs(a[i * 4 + c] - b[i * 4 + c]));
            if (d > 4) diff++;
            max = Math.Max(max, d);
        }
        return (diff, max);
    }

    private static void Report(bool ok, string what)
    {
        Console.WriteLine($"  [{(ok ? "ok " : "FAIL")}] {what}");
        if (!ok) _failures++;
    }
}
