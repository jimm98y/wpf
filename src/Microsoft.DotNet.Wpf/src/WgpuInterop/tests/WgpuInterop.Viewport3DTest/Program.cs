// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Phase-1 3D (Viewport3D) test. 3D content is rasterized with a perspective
// camera, directional + ambient diffuse lighting and a depth buffer into an
// offscreen texture, then composited into the 2D scene. Two things are checked:
//   * Lighting: the same camera-facing quad is much brighter when the light
//     points at it than when the light points away (diffuse term responds to the
//     light direction); corners stay background.
//   * Depth: a near quad drawn first is NOT overwritten by a far quad drawn after
//     it -- the centre shows the near colour (depth test, not paint order).
//

using System;
using System.Collections.Generic;
using System.Numerics;
using Microsoft.Wpf.Interop.WebGpu.Composition;

internal static class Program
{
    private const int W = 64;
    private const int H = 48;
    private static int _failures;

    private static int Main()
    {
        using var ctx = WgpuContext.Create();
        using var renderer = new WgpuSceneRenderer(ctx);
        var white = RgbaColor.FromBytes(255, 255, 255, 255);

        var camera = new Camera3D(new Vector3(0, 0, 3), new Vector3(0, 0, -1), new Vector3(0, 1, 0), 45f);
        var ambient = new RgbaColor(0.15f, 0.15f, 0.15f, 1f);
        var gray = new RgbaColor(0.6f, 0.6f, 0.6f, 1f);
        MeshGeometry3D quad = Quad();

        // ---- Lighting ----
        byte[] lit = renderer.RenderToRgba(LightingScene(camera, ambient, gray, quad, towardQuad: true), W, H, white);
        byte[] dark = renderer.RenderToRgba(LightingScene(camera, ambient, gray, quad, towardQuad: false), W, H, white);
        int bright = Lum(lit, 32, 24), dim = Lum(dark, 32, 24);
        Console.WriteLine($"lighting: light-toward centre={bright}, light-away centre={dim}");
        Check(bright > 150, "quad is bright when the light points at it");
        Check(dim < 70, "quad is dim (ambient only) when the light points away");
        Check(bright > dim + 80, "diffuse lighting responds to the light direction");
        Check(Lum(lit, 2, 2) > 245, "corners are background (the quad doesn't fill the viewport)");

        // ---- Depth ----
        byte[] depth = renderer.RenderToRgba(DepthScene(camera, ambient, quad), W, H, white);
        int cr = depth[(24 * W + 32) * 4], cb = depth[(24 * W + 32) * 4 + 2];
        Console.WriteLine($"depth: centre RGB=[{depth[(24 * W + 32) * 4]},{depth[(24 * W + 32) * 4 + 1]},{cb}]");
        Check(cr > 180 && cb < 90, "the near quad (drawn first) is not overwritten by the far quad (depth test)");
        Check(Lum(depth, 2, 2) > 245, "corners are background");

        if (_failures == 0)
        {
            Console.WriteLine("VIEWPORT3D TEST PASSED: 3D meshes render with a perspective camera, diffuse lighting and depth testing.");
            return 0;
        }
        Console.Error.WriteLine($"VIEWPORT3D TEST FAILED: {_failures} check(s) wrong.");
        return 1;
    }

    private static SceneVisual LightingScene(Camera3D cam, RgbaColor ambient, RgbaColor color, MeshGeometry3D quad, bool towardQuad)
    {
        var light = new DirectionalLight3D(new Vector3(0, 0, towardQuad ? -1 : 1), RgbaColor.FromBytes(255, 255, 255, 255));
        return Viewport(cam, light, ambient, new List<Model3D> { new(quad, color) });
    }

    private static SceneVisual DepthScene(Camera3D cam, RgbaColor ambient, MeshGeometry3D quad)
    {
        var light = new DirectionalLight3D(new Vector3(0, 0, -1), RgbaColor.FromBytes(255, 255, 255, 255));
        // Near red drawn first; far blue drawn second. Depth must keep red at the centre.
        var models = new List<Model3D>
        {
            new(quad, new RgbaColor(1f, 0f, 0f, 1f), Matrix4x4.CreateTranslation(0, 0, 0.5f)),
            new(quad, new RgbaColor(0f, 0f, 1f, 1f), Matrix4x4.CreateTranslation(0, 0, -0.5f)),
        };
        return Viewport(cam, light, ambient, models);
    }

    private static SceneVisual Viewport(Camera3D cam, DirectionalLight3D light, RgbaColor ambient, List<Model3D> models)
    {
        var root = new SceneVisual();
        var v = new SceneVisual();
        v.Content.Add(new Viewport3DDraw(cam, light, ambient, models));
        root.Children.Add(v);
        return root;
    }

    // A unit quad in the z=0 plane facing +Z (toward the camera).
    private static MeshGeometry3D Quad()
    {
        var positions = new[]
        {
            new Vector3(-1, -1, 0), new Vector3(1, -1, 0), new Vector3(1, 1, 0), new Vector3(-1, 1, 0),
        };
        var normals = new[] { Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ };
        var indices = new[] { 0, 1, 2, 0, 2, 3 };
        return new MeshGeometry3D(positions, normals, indices);
    }

    private static int Lum(byte[] px, int x, int y) => px[(y * W + x) * 4];

    private static void Check(bool ok, string what)
    {
        Console.WriteLine($"  [{(ok ? "ok " : "BAD")}] {what}");
        if (!ok) _failures++;
    }
}
