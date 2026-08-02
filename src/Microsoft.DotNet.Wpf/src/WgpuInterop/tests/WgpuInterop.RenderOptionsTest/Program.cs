// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// RenderOptions: MILCMD_VISUAL_SETRENDEROPTIONS (0x21) was decoded by nobody, so
// RenderOptions.SetEdgeMode / SetBitmapScalingMode were silently ignored. Both are
// deliberate opt-outs of smoothing -- pixel art, QR codes, crisp diagrams -- so ignoring
// them produces exactly the blurring the app asked to avoid.
//
// Asserted on PIXELS through the real command, both directions, because a decode that
// stores a flag nothing reads would pass any structural check.
//

using System;
using System.Collections.Generic;
using Microsoft.Wpf.Interop.WebGpu.Composition;
using Microsoft.Wpf.Interop.WebGpu.Composition.Protocol;
using Microsoft.Wpf.Interop.WebGpu.Composition.Protocol;

internal static class Program
{
    private const int W = 64, H = 64;
    private static int _failures;

    private static int Main()
    {
        // --- EdgeMode.Aliased: a rotated rect's edge must go hard ---
        int aaPartials = EdgePartials(aliased: false);
        int alPartials = EdgePartials(aliased: true);
        Console.WriteLine($"  rotated edge, partial-coverage pixels: default={aaPartials} aliased={alPartials}");
        Check(aaPartials > 20, "default edges are anti-aliased (many partial pixels)");
        Check(alPartials == 0, "EdgeMode.Aliased removes every partial pixel");

        // --- BitmapScalingMode.NearestNeighbor: a magnified 2x2 image must not blend ---
        int linBlend = ImageBlendPixels(nearest: false);
        int nnBlend = ImageBlendPixels(nearest: true);
        Console.WriteLine($"  magnified 2x2 image, blended pixels:   default={linBlend} nearest={nnBlend}");
        Check(linBlend > 20, "default image scaling filters (blended pixels present)");
        Check(nnBlend == 0, "NearestNeighbor produces only the source colours");

        // --- Field ordering through the REAL command ---
        // MilRenderOptions is seven u32s and the two we honour sit either side of fields we
        // skip. Decoding it with every field non-zero catches an off-by-one in the struct walk,
        // which would otherwise read CompositingMode as EdgeMode and silently alias the two.
        MilcorePath();

        Console.WriteLine();
        if (_failures > 0) { Console.WriteLine($"RENDER OPTIONS TEST FAILED: {_failures} problem(s)."); return 1; }
        Console.WriteLine("RENDER OPTIONS TEST PASSED: EdgeMode and BitmapScalingMode are honoured.");
        return 0;
    }

    // Counts pixels that are neither background nor solid fill along a rotated edge.
    private static int EdgePartials(bool aliased)
    {
        var root = new SceneVisual();
        var child = new SceneVisual
        {
            Transform = System.Numerics.Matrix3x2.CreateRotation(0.30f, new System.Numerics.Vector2(32, 32)),
            AliasedEdges = aliased,
        };
        child.Content.Add(new GeometryFill(new RectangleGeometry(new Rect(12, 12, 40, 40)), RgbaColor.FromBytes(0, 0, 0, 255)));
        root.Children.Add(child);

        byte[] px = Render(root);
        int partial = 0;
        for (int i = 0; i < W * H; i++)
        {
            int v = px[i * 4];
            if (v > 20 && v < 235) partial++;
        }
        return partial;
    }

    // Counts pixels that are not one of the four source colours (i.e. produced by filtering).
    private static int ImageBlendPixels(bool nearest)
    {
        var rgba = new byte[2 * 2 * 4];
        (byte, byte, byte)[] cols = { (255, 0, 0), (0, 255, 0), (0, 0, 255), (255, 255, 0) };
        for (int i = 0; i < 4; i++)
        { rgba[i*4] = cols[i].Item1; rgba[i*4+1] = cols[i].Item2; rgba[i*4+2] = cols[i].Item3; rgba[i*4+3] = 255; }

        var root = new SceneVisual();
        var child = new SceneVisual { NearestBitmapScaling = nearest };
        child.Content.Add(new GeometryFill(new RectangleGeometry(new Rect(4, 4, 56, 56)), new ImageBrush(rgba, 2, 2)));
        root.Children.Add(child);

        byte[] px = Render(root);
        int blended = 0;
        for (int y = 8; y < H - 8; y++)
        for (int x = 8; x < W - 8; x++)
        {
            int i = (y * W + x) * 4;
            bool exact = false;
            foreach ((byte r, byte g, byte b) in cols)
                if (Math.Abs(px[i] - r) < 12 && Math.Abs(px[i+1] - g) < 12 && Math.Abs(px[i+2] - b) < 12) exact = true;
            if (!exact) blended++;
        }
        return blended;
    }

    private static void MilcorePath()
    {
        // Every field non-zero and distinct, so a misread lands on the wrong value rather than
        // coincidentally on the right one.
        var e = new MilcoreEngine();
        e.CreateOrAddRef(2, MilResourceTypeId.Visual);
        e.SubmitCommand(SetRenderOptions(2,
            flags: 0x1 | 0x2 | 0x8 | 0x10 | 0x20,   // BitmapScalingMode|EdgeMode|ClearType|TextRendering|TextHinting
            edgeMode: 1,                            // Aliased
            compositingMode: 5,                     // SourceUnder -- read past, must not shift the rest
            bitmapScalingMode: 3,                   // NearestNeighbor
            clearTypeHint: 1, textRenderingMode: 2, textHintingMode: 1));
        e.Realize();

        SceneVisual? v = e.VisualByHandle(2);
        if (v is null) { Check(false, "milcore path: no visual"); return; }
        Check(v.AliasedEdges, "EdgeMode read correctly with every other field populated");
        Check(v.NearestBitmapScaling, "BitmapScalingMode read from the right offset past CompositingMode");
    }

    // MILCMD_VISUAL_SETRENDEROPTIONS: Handle@4, then MilRenderOptions@8 (7 x u32).
    private static byte[] SetRenderOptions(uint handle, uint flags, uint edgeMode, uint compositingMode,
        uint bitmapScalingMode, uint clearTypeHint, uint textRenderingMode, uint textHintingMode)
    {
        var b = new List<byte>();
        void U32(uint v) => b.AddRange(BitConverter.GetBytes(v));
        U32(0x21); U32(handle);
        U32(flags); U32(edgeMode); U32(compositingMode); U32(bitmapScalingMode);
        U32(clearTypeHint); U32(textRenderingMode); U32(textHintingMode);
        return b.ToArray();
    }

    private static byte[] Render(SceneVisual root)
    {
        using var ctx = WgpuContext.Create();
        var renderer = new WgpuSceneRenderer(ctx);
        return renderer.RenderToRgba(root, W, H, RgbaColor.FromBytes(255, 255, 255, 255));
    }

    private static void Check(bool ok, string what)
    {
        Console.WriteLine($"  [{(ok ? "ok " : "FAIL")}] {what}");
        if (!ok) _failures++;
    }
}
