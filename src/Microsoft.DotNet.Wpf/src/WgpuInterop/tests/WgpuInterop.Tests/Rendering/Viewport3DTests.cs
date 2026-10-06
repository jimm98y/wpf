// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Ported from WgpuInterop.Viewport3DTest.
//
// 3D content is rasterized with a perspective camera, directional + ambient diffuse lighting and a
// DEPTH BUFFER into an offscreen texture, then composited into the 2D scene. That last part is why
// these live in the renderer suite rather than being a self-contained 3D unit test: the composite
// is where 3D meets everything else.
//

using System;
using System.Collections.Generic;
using System.Numerics;
using Microsoft.Wpf.Interop.WebGpu.Composition;
using WgpuInterop.Tests.Harness;
using Xunit;

namespace WgpuInterop.Tests.Rendering
{
    public sealed class Viewport3DTests : RendererTestBase
    {
        public Viewport3DTests(GpuFixture gpu) : base(gpu) { }

        private const int W = 64, H = 48;

        private static readonly RgbaColor Ambient = new(0.15f, 0.15f, 0.15f, 1f);
        private static readonly RgbaColor Gray = new(0.6f, 0.6f, 0.6f, 1f);

        private static Camera3D LookAtCamera()
            => new(new Vector3(0, 0, 3), new Vector3(0, 0, -1), new Vector3(0, 1, 0), 45f);

        /// <summary>A unit quad in the z=0 plane facing +Z, i.e. toward the camera.</summary>
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

        private static SceneVisual Viewport(Camera3D cam, DirectionalLight3D light, List<Model3D> models)
        {
            var root = new SceneVisual();
            var v = new SceneVisual();
            v.Content.Add(new Viewport3DDraw(cam, light, Ambient, models));
            root.Children.Add(v);
            return root;
        }

        private static SceneVisual LightingScene(Camera3D cam, bool towardQuad)
        {
            var light = new DirectionalLight3D(new Vector3(0, 0, towardQuad ? -1 : 1),
                RgbaColor.FromBytes(255, 255, 255, 255));
            return Viewport(cam, light, new List<Model3D> { new(Quad(), Gray) });
        }

        /// <summary>
        /// The SAME camera-facing quad under a light pointing at it versus away. Comparing the two is
        /// what tests the diffuse term: either alone only proves something rendered.
        /// </summary>
        [Fact]
        public void DiffuseLighting_RespondsToTheLightDirection()
        {
            var lit = new Image(Render(LightingScene(LookAtCamera(), towardQuad: true), W, H), W, H);
            var dark = new Image(Render(LightingScene(LookAtCamera(), towardQuad: false), W, H), W, H);

            int bright = lit[32, 24].R, dim = dark[32, 24].R;

            Assert.True(bright > 150, $"the quad should be bright when lit, was {bright}");
            Assert.True(dim < 70, $"the quad should fall to ambient when unlit, was {dim}");
            Assert.True(bright > dim + 80,
                $"the diffuse term must respond to direction: lit={bright}, unlit={dim}");
            Assert.True(lit[2, 2].R > 245, "the corners should stay background; the quad does not fill the viewport");
        }

        /// <summary>
        /// A NEAR quad drawn FIRST must not be overwritten by a FAR quad drawn after it. Draw order
        /// and depth order are deliberately opposed, so a renderer that painted in order would fail
        /// while still producing a plausible image.
        /// </summary>
        [Fact]
        public void DepthBuffer_BeatsPaintOrder()
        {
            var light = new DirectionalLight3D(new Vector3(0, 0, -1), RgbaColor.FromBytes(255, 255, 255, 255));
            var models = new List<Model3D>
            {
                new(Quad(), new RgbaColor(1f, 0f, 0f, 1f), Matrix4x4.CreateTranslation(0, 0, 0.5f)),   // near, first
                new(Quad(), new RgbaColor(0f, 0f, 1f, 1f), Matrix4x4.CreateTranslation(0, 0, -0.5f)),  // far, second
            };

            var img = new Image(Render(Viewport(LookAtCamera(), light, models), W, H), W, H);
            Rgb centre = img[32, 24];

            Assert.True(centre.R > 180 && centre.B < 90,
                $"the near quad should survive the far one drawn after it, centre was {centre}");
            Assert.True(img[2, 2].R > 245, "the corners should stay background");
        }

        /// <summary>
        /// An explicit MatrixCamera must produce the same image as the equivalent look-at camera.
        /// The projection is built from the same aspect-corrected FOV WPF derives, so any difference
        /// is a convention mismatch (handedness, FOV axis, near plane) rather than a rounding one.
        /// </summary>
        [Fact]
        public void MatrixCamera_MatchesTheEquivalentLookAtCamera()
        {
            float aspect = W / (float)H;
            float fovY = 2f * MathF.Atan(MathF.Tan(45f * MathF.PI / 360f) / aspect);
            var matrixCam = new Camera3D(
                Matrix4x4.CreateLookAt(new Vector3(0, 0, 3), new Vector3(0, 0, 2), new Vector3(0, 1, 0)),
                Matrix4x4.CreatePerspectiveFieldOfView(fovY, aspect, 0.125f, 1000f));

            int lookAt = new Image(Render(LightingScene(LookAtCamera(), towardQuad: true), W, H), W, H)[32, 24].R;
            int matrix = new Image(Render(LightingScene(matrixCam, towardQuad: true), W, H), W, H)[32, 24].R;

            Assert.True(Math.Abs(matrix - lookAt) <= 8,
                $"MatrixCamera gave {matrix} where the look-at camera gave {lookAt}");
        }

        /// <summary>
        /// Sidedness. The quad is rotated to face AWAY, so a front-only model must be culled --
        /// and with a BackMaterial the back face must paint instead. Both halves are needed: culling
        /// alone could be "nothing rendered", and BackMaterial alone could be "culling never happens".
        /// </summary>
        [Fact]
        public void FrontOnlyModel_IsCulledFromBehind_AndBackMaterialPaintsIt()
        {
            Matrix4x4 aboutFace = Matrix4x4.CreateRotationY(MathF.PI);
            var frontMat = Material3D.Diffuse3D(new RgbaColor(1f, 0f, 0f, 1f));
            var backMat = Material3D.Diffuse3D(new RgbaColor(0f, 0f, 1f, 1f));
            var light = new DirectionalLight3D(new Vector3(0, 0, -1), RgbaColor.FromBytes(255, 255, 255, 255));

            var culled = new Image(Render(Viewport(LookAtCamera(), light,
                new List<Model3D> { new(Quad(), frontMat, true, default, false, aboutFace) }), W, H), W, H);
            Assert.True(culled[32, 24].R > 245,
                $"a front-only model viewed from behind should be culled, centre was {culled[32, 24]}");

            var backed = new Image(Render(Viewport(LookAtCamera(), light,
                new List<Model3D> { new(Quad(), frontMat, true, backMat, true, aboutFace) }), W, H), W, H);
            Rgb c = backed[32, 24];
            Assert.True(c.B > 100 && c.R < 90,
                $"the BackMaterial should paint the back face blue, not red or background; was {c}");
        }
    }
}
