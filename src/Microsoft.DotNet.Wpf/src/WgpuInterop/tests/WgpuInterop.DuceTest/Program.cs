// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Phase-1 DUCE round-trip test.
//
// Proves the command-stream seam: a scene is rendered directly, then the same
// scene is serialized to a DUCE-style batch (CompositionChannel), decoded by the
// CompositionEngine into a fresh visual tree and rendered again. The two images
// must be byte-for-byte identical -- i.e. nothing is lost going through the
// serialized protocol that, in the full port, WPF already emits. A few absolute
// pixels are also checked so the test fails loudly if rendering itself breaks.
//

using System;
using System.Numerics;
using Microsoft.Wpf.Interop.WebGpu.Composition;
using Microsoft.Wpf.Interop.WebGpu.Composition.Protocol;

internal static class Program
{
    private const int W = 64;
    private const int H = 64;

    private static int Main()
    {
        SceneVisual scene = BuildScene();
        var background = RgbaColor.FromBytes(255, 255, 255, 255);

        using var ctx = WgpuContext.Create();
        var renderer = new WgpuSceneRenderer(ctx);

        // 1) Direct render of the hand-built tree.
        byte[] direct = renderer.RenderToRgba(scene, W, H, background);

        // 2) Serialize -> transmit -> rebuild -> render.
        byte[] batch = CompositionChannel.EncodeScene(scene);
        var engine = new CompositionEngine();
        engine.ProcessBatch(batch);
        SceneVisual? rebuilt = engine.Root;
        if (rebuilt is null) return Fail("CompositionEngine produced no root");
        byte[] viaProtocol = renderer.RenderToRgba(rebuilt, W, H, background);

        Console.WriteLine($"command batch = {batch.Length} bytes");

        // The protocol must be lossless: identical pixels.
        if (direct.Length != viaProtocol.Length) return Fail("image size mismatch");
        int diffs = 0, firstDiff = -1;
        for (int i = 0; i < direct.Length; i++)
        {
            if (direct[i] != viaProtocol[i]) { diffs++; if (firstDiff < 0) firstDiff = i; }
        }
        if (diffs != 0)
            return Fail($"{diffs} byte(s) differ after round-trip (first at offset {firstDiff})");
        Console.WriteLine("round-trip is byte-identical to direct render");

        // Absolute sanity checks on the decoded image.
        bool ok = true;
        ok &= Check(viaProtocol, 16, 16, 255, 0, 0, 255, "opaque red");
        ok &= Check(viaProtocol, 48, 48, 128, 128, 128, 255, "50% black over white");
        ok &= Check(viaProtocol, 12, 50, 128, 255, 128, 255, "clipped green (kept)");
        ok &= Check(viaProtocol, 30, 50, 255, 255, 255, 255, "clipped green (removed)");
        ok &= Check(viaProtocol, 44, 8, 0, 0, 255, 255, "scaled blue");

        if (ok)
        {
            Console.WriteLine("DUCE ROUND-TRIP TEST PASSED: scene survives the command protocol and renders identically.");
            return 0;
        }
        return Fail("absolute pixel checks failed");
    }

    // Same scene shape as the render test, exercising offset/transform/opacity/clip.
    private static SceneVisual BuildScene()
    {
        var root = new SceneVisual();

        var childA = new SceneVisual();
        childA.Content.Add(new GeometryFill(new RectangleGeometry(new Rect(8, 8, 32, 32)),
            RgbaColor.FromBytes(255, 0, 0, 255)));
        root.Children.Add(childA);

        var childB = new SceneVisual { Offset = new Vector2(40, 40) };
        childB.Content.Add(new GeometryFill(new RectangleGeometry(new Rect(0, 0, 16, 16)),
            new RgbaColor(0f, 0f, 0f, 0.5f)));
        root.Children.Add(childB);

        var childC = new SceneVisual { Offset = new Vector2(8, 40), Opacity = 0.5, Clip = new Rect(0, 0, 16, 32) };
        childC.Content.Add(new GeometryFill(new RectangleGeometry(new Rect(0, 0, 32, 32)),
            RgbaColor.FromBytes(0, 255, 0, 255)));
        root.Children.Add(childC);

        var childE = new SceneVisual { Transform = Matrix3x2.CreateScale(2f, 2f) };
        childE.Content.Add(new GeometryFill(new RectangleGeometry(new Rect(20, 2, 4, 4)),
            RgbaColor.FromBytes(0, 0, 255, 255)));
        root.Children.Add(childE);

        return root;
    }

    private static bool Check(byte[] px, int x, int y, byte r, byte g, byte b, byte a, string what)
    {
        int i = (y * W + x) * 4;
        bool ok = Near(px[i], r) && Near(px[i + 1], g) && Near(px[i + 2], b) && Near(px[i + 3], a);
        Console.WriteLine($"  [{(ok ? "ok " : "BAD")}] ({x},{y}) {what}: got [{px[i]},{px[i + 1]},{px[i + 2]},{px[i + 3]}]");
        return ok;
    }

    private static bool Near(byte actual, byte expected) => Math.Abs(actual - expected) <= 1;

    private static int Fail(string message)
    {
        Console.Error.WriteLine($"DUCE ROUND-TRIP TEST FAILED: {message}");
        return 1;
    }
}
