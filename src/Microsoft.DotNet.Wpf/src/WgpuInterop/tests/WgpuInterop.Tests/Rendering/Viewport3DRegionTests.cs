// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// A Viewport3D that occupies part of the window renders into a texture that size, not the window's.
//
// Every other 3D test uses the empty-viewport "fill the whole target" form, so the sub-rect path --
// which is the only form a real application produces, a 3D panel sitting inside a layout -- had no
// coverage at all. It also had a cost nobody could see in a picture: the resolve target, the
// multisampled colour buffer and the depth buffer were allocated at FULL TARGET size however small
// the panel was, cleared and rasterized every frame; and a Viewport3D forced every clip/mask/effect
// layer above it to bake window-sized textures too, re-baked on each frame the 3D animated. On a
// maximized window that is several 3840x1077 surfaces per frame to draw a small spinning cube.
//
// What has to stay true once the pass is region-sized is placement: the projection now maps NDC into
// a rect inside a small texture rather than inside the window, and that texture is composited back
// at an offset. Getting either wrong moves or scales the 3D content, so these assert WHERE the cube
// lands and that nothing escapes its rect -- not merely that something rendered.
//

using System.Collections.Generic;
using System.Numerics;
using Microsoft.Wpf.Interop.WebGpu.Composition;
using WgpuInterop.Tests.Harness;
using Xunit;

namespace WgpuInterop.Tests.Rendering
{
    public sealed class Viewport3DRegionTests : RendererTestBase
    {
        public Viewport3DRegionTests(GpuFixture gpu) : base(gpu) { }

        private const int W = 120, H = 120;

        private static readonly RgbaColor Ambient = new(0.15f, 0.15f, 0.15f, 1f);
        private static readonly RgbaColor Gray = new(0.6f, 0.6f, 0.6f, 1f);

        /// <summary>A camera-facing unit quad, lit head-on so it renders as a solid bright block.</summary>
        private static MeshGeometry3D Quad()
        {
            var positions = new[]
            {
                new Vector3(-1, -1, 0), new Vector3(1, -1, 0), new Vector3(1, 1, 0), new Vector3(-1, 1, 0),
            };
            var normals = new[] { Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ };
            return new MeshGeometry3D(positions, normals, new[] { 0, 1, 2, 0, 2, 3 });
        }

        private static SceneVisual SceneWithViewportAt(Rect viewport)
        {
            var cam = new Camera3D(new Vector3(0, 0, 3), new Vector3(0, 0, -1), new Vector3(0, 1, 0), 45f);
            var light = new DirectionalLight3D(new Vector3(0, 0, -1), RgbaColor.FromBytes(255, 255, 255, 255));

            var root = new SceneVisual();
            var host = new SceneVisual();
            host.Content.Add(new Viewport3DDraw(cam, light, Ambient,
                new List<Model3D> { new(Quad(), Gray) }, viewport));
            root.Children.Add(host);
            return root;
        }

        private Image RenderAt(Rect viewport) => new(Render(SceneWithViewportAt(viewport), W, H), W, H);

        /// <summary>Is this pixel background (the white the harness clears to) rather than 3D content?</summary>
        private static bool IsBackground(Image img, int x, int y) => img[x, y].R > 240 && img[x, y].B > 240;

        [Fact]
        public void TheCubeRendersInsideItsRectAndNowhereElse()
        {
            // A viewport in the lower-right quadrant: every edge is an interior edge of the window, so
            // content escaping in ANY direction lands on background this can see.
            var img = RenderAt(new Rect(60, 60, 56, 56));

            Assert.False(IsBackground(img, 88, 88), "the 3D content should cover the middle of its viewport rect");

            // Everything outside the rect stays background. The upper-left quadrant is the interesting
            // one: under the old full-target pass the quad was projected into a window-sized texture,
            // so a mistake in the region mapping shows up here as content at the wrong place.
            Assert.True(IsBackground(img, 30, 30), "nothing should render in the opposite quadrant");
            Assert.True(IsBackground(img, 88, 30), "nothing should render above the viewport rect");
            Assert.True(IsBackground(img, 30, 88), "nothing should render left of the viewport rect");
            Assert.True(IsBackground(img, 4, 4), "the window corner should stay background");
        }

        /// <summary>
        /// The same viewport in two places must render the same picture, just translated. This is the
        /// assertion that actually pins the region mapping: a scale error, or an origin that failed to
        /// cancel against the region's, would change the content rather than merely move it.
        /// </summary>
        [Fact]
        public void MovingTheViewportTranslatesTheContentUnchanged()
        {
            const int Size = 48;
            var a = RenderAt(new Rect(8, 8, Size, Size));
            var b = RenderAt(new Rect(64, 64, Size, Size));

            int compared = 0, differing = 0;
            for (int y = 0; y < Size; y++)
            {
                for (int x = 0; x < Size; x++)
                {
                    Rgb pa = a[8 + x, 8 + y], pb = b[64 + x, 64 + y];
                    compared++;
                    // A pixel of slack: the two rects sit at different subpixel phases of the same
                    // projection, so edge texels legitimately differ slightly.
                    if (System.Math.Abs(pa.R - pb.R) > 8 || System.Math.Abs(pa.G - pb.G) > 8 ||
                        System.Math.Abs(pa.B - pb.B) > 8)
                    {
                        differing++;
                    }
                }
            }

            Assert.True(compared > 0, "nothing was compared");
            Assert.True(differing * 50 <= compared,
                $"{differing} of {compared} pixels differ between the same viewport drawn at two " +
                "positions; moving a Viewport3D must translate its content, not change it");
        }

        /// <summary>
        /// A viewport larger than the window still works: the region is capped at the target size, and
        /// the projection has to stay consistent with that cap rather than with the requested rect.
        /// </summary>
        [Fact]
        public void AViewportBiggerThanTheWindowStillRenders()
        {
            var img = RenderAt(new Rect(-40, -40, W + 80, H + 80));

            Assert.False(IsBackground(img, W / 2, H / 2),
                "an oversized viewport should still project its content across the window");
        }
    }
}
