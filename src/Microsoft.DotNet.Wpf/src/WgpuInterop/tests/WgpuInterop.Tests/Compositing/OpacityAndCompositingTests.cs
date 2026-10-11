// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Ported from WgpuInterop.GroupOpacityTest, OpacityMaskTest and CompositingModeTest.
//

using System;
using System.Collections.Generic;
using System.Numerics;
using Microsoft.Wpf.Interop.WebGpu.Composition;
using WgpuInterop.Tests.Harness;
using Xunit;

namespace WgpuInterop.Tests.Compositing
{
    public sealed class OpacityAndCompositingTests : RendererTestBase
    {
        public OpacityAndCompositingTests(GpuFixture gpu) : base(gpu) { }

        // ---- group opacity -----------------------------------------------------------------

        private const int GW = 64, GH = 64;

        /// <summary>One visual at 50% opacity drawing two OVERLAPPING black rectangles.</summary>
        private static SceneVisual GroupOpacityScene()
        {
            var root = new SceneVisual();
            var group = new SceneVisual { Opacity = 0.5 };
            var black = RgbaColor.FromBytes(0, 0, 0, 255);
            group.Content.Add(new GeometryFill(new RectangleGeometry(new Rect(10, 10, 30, 30)), black)); // (10,10)-(40,40)
            group.Content.Add(new GeometryFill(new RectangleGeometry(new Rect(25, 25, 30, 30)), black)); // (25,25)-(55,55)
            root.Children.Add(group);
            return root;
        }

        /// <summary>
        /// A visual with opacity &lt; 1 that draws more than one thing must composite as a SINGLE
        /// offscreen layer: content drawn opaque into a texture, then that texture blended once.
        ///
        /// The overlap is the whole test. Per-primitive opacity (the wrong implementation) darkens
        /// it, because each rectangle blends separately; correct layer compositing leaves every
        /// covered pixel at the same 50% grey whether one rectangle covers it or two.
        /// </summary>
        [Fact]
        public void TranslucentGroup_CompositesOnce_SoOverlapsDoNotDoubleBlend()
        {
            byte[] px = Render(GroupOpacityScene(), GW, GH);
            var img = new Image(px, GW, GH);

            int bg = img[60, 4].R;
            int singleA = img[15, 15].R;
            int singleB = img[50, 50].R;
            int overlap = img[32, 32].R;

            Assert.True(Math.Abs(bg - 255) <= 4, $"background should be white, was {bg}");
            Assert.True(Math.Abs(singleA - 128) <= 6, $"single-covered region A should be 50% grey, was {singleA}");
            Assert.True(Math.Abs(singleB - 128) <= 6, $"single-covered region B should be 50% grey, was {singleB}");
            Assert.True(Math.Abs(overlap - 128) <= 6,
                $"overlap should be the SAME 50% grey (composited once), was {overlap}");
            Assert.True(Math.Abs(overlap - singleA) <= 6,
                $"overlap ({overlap}) must match single coverage ({singleA}) or the group is not composited as a unit");
        }

        [Fact]
        public void TranslucentGroup_SurvivesProtocolRoundTrip()
            => AssertProtocolRoundTripIsIdentical(GroupOpacityScene(), GW, GH);

        // ---- opacity mask ------------------------------------------------------------------

        private const int MW = 64, MH = 48;

        /// <summary>A solid black rectangle under a horizontal gradient mask fading alpha 1 to 0.</summary>
        private static SceneVisual OpacityMaskScene()
        {
            var root = new SceneVisual();
            var masked = new SceneVisual
            {
                OpacityMask = new LinearGradientBrush(new Vector2(0, 0), new Vector2(64, 0), new[]
                {
                    new GradientStop(0f, new RgbaColor(1f, 1f, 1f, 1f)),
                    new GradientStop(1f, new RgbaColor(1f, 1f, 1f, 0f)),
                }),
            };
            masked.Content.Add(new GeometryFill(new RectangleGeometry(new Rect(0, 0, 64, 48)),
                RgbaColor.FromBytes(0, 0, 0, 255)));
            root.Children.Add(masked);
            return root;
        }

        [Fact]
        public void OpacityMask_FadesTheSubtreePerPixel()
        {
            byte[] px = Render(OpacityMaskScene(), MW, MH);
            var img = new Image(px, MW, MH);

            int left = img[4, 24].R, mid = img[32, 24].R, right = img[60, 24].R;

            Assert.True(left < 40, $"left should be opaque black (mask alpha ~1), was {left}");
            Assert.True(Math.Abs(mid - 128) <= 24, $"middle should be ~50% grey (mask alpha ~0.5), was {mid}");
            Assert.True(right > 215, $"right should fade to background (mask alpha ~0), was {right}");
            Assert.True(left < mid && mid < right,
                $"the mask must produce a monotonic fade, got left={left} mid={mid} right={right}");
        }

        [Fact]
        public void OpacityMask_SurvivesProtocolRoundTrip()
            => AssertProtocolRoundTripIsIdentical(OpacityMaskScene(), MW, MH);

        // ---- System.Drawing CompositingMode ------------------------------------------------
        //
        // This is GDI+'s Graphics.CompositingMode, NOT WPF's internal MilCompositingMode (which no
        // public WPF API sets). It is reachable because this fork implements System.Drawing and
        // routes its verbs into this renderer. The scenario is the one from the Microsoft docs,
        // "Use Compositing Mode to Control Alpha Blending": two semi-transparent overlapping
        // ellipses drawn with SourceCopy must NOT blend with each other. Before this was fixed, a
        // recording Graphics discarded the property entirely (nativeObject == 0 made the setter
        // return early) and SourceCopy silently alpha-blended.

        private const int CW = 190, CH = 110;
        private static readonly RgbaColor Red = RgbaColor.FromBytes(255, 0, 0, 160);     // alpha 160, as in the docs
        private static readonly RgbaColor Green = RgbaColor.FromBytes(0, 255, 0, 160);

        private Rgb Sample(bool sourceCopy, int x, int y)
        {
            var root = new SceneVisual();
            root.Content.Add(new GeometryFill(new EllipseGeometry(new Vector2(75, 35), 75, 35), Red)
            { SourceCopy = sourceCopy });
            root.Content.Add(new GeometryFill(new EllipseGeometry(new Vector2(105, 65), 75, 35), Green)
            { SourceCopy = sourceCopy });
            return new Image(Render(root, CW, CH), CW, CH)[x, y];
        }

        [Fact]
        public void SourceOver_BlendsOverlappingShapes()
        {
            Rgb over = Sample(sourceCopy: false, 90, 50);
            Assert.True(over.R > 40 && over.G > 40,
                $"SourceOver should leave red showing through the green, overlap was {over}");
        }

        [Fact]
        public void SourceCopy_ReplacesInsteadOfBlending()
        {
            Rgb copy = Sample(sourceCopy: true, 90, 50);
            Assert.True(copy.R < 20 && copy.G > 40,
                $"SourceCopy should replace the red entirely in the overlap, was {copy}");
        }

        /// <summary>
        /// SourceCopy replaces the DESTINATION too, not just earlier shapes: alpha-160 red over white
        /// writes the premultiplied source (160,0,0) rather than blending to pink. The docs example
        /// hides this by drawing onto a blank bitmap, where "replace" and "blend over nothing" look
        /// identical. It shows up here because the target is white. This is the behaviour, not a bug,
        /// so it is pinned down rather than tolerated.
        /// </summary>
        [Fact]
        public void SourceCopy_ReplacesTheDestination_NotJustEarlierShapes()
        {
            Rgb soloOver = Sample(sourceCopy: false, 20, 20);
            Rgb soloCopy = Sample(sourceCopy: true, 20, 20);

            Assert.True(soloOver.R > 240 && soloOver.G > 60,
                $"SourceOver should blend the lone ellipse with white, was {soloOver}");
            Assert.True(soloCopy.R > 140 && soloCopy.R < 180 && soloCopy.G < 20 && soloCopy.B < 20,
                $"SourceCopy should write the premultiplied source (160,0,0), was {soloCopy}");
        }
    }

    /// <summary>Ported from WgpuInterop.ShaderValidationTest.</summary>
    public sealed class ShaderCompilationTests : RendererTestBase
    {
        public ShaderCompilationTests(GpuFixture gpu) : base(gpu) { }

        /// <summary>
        /// Compile every shipped WGSL shader through wgpu's own frontend (naga), so a syntax or type
        /// error fails here rather than surfacing as a blank draw the first time some rarely-taken
        /// path runs. Several shaders sit behind off-by-default switches (fs_shapebrush) or only fire
        /// on specific content, so nothing else in the suite compiles them at all.
        /// </summary>
        [Fact]
        public void EveryEmbeddedShader_Compiles()
        {
            WgpuSceneRenderer renderer = NewRenderer();
            var names = new List<string>(ShaderSource.AllNames());
            names.Sort(StringComparer.Ordinal);

            Assert.True(names.Count > 0, "no embedded WGSL resources found -- a packaging regression");

            var failed = new List<string>();
            foreach (string name in names)
                if (renderer.CompileShaderForTest(ShaderSource.Get(name)) == IntPtr.Zero)
                    failed.Add(name);

            Assert.True(failed.Count == 0,
                $"{failed.Count} of {names.Count} shaders did not compile: {string.Join(", ", failed)}");
        }
    }
}
