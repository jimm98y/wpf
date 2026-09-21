// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Ported from WgpuInterop.StabilityTest and DeterminismProbe.
//
// Resource lifecycle and reproducibility. A real app renders the same renderer thousands of times,
// so transient GPU objects must be released and long-lived ones cached -- and neither can be
// observed from a single frame, which is why nothing else in the suite covers it.
//

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Security.Cryptography;
using Microsoft.Wpf.Interop.WebGpu.Composition;
using WgpuInterop.Tests.Harness;
using Xunit;

namespace WgpuInterop.Tests.Lifecycle
{
    public sealed class StabilityTests : RendererTestBase
    {
        public StabilityTests(GpuFixture gpu) : base(gpu) { }

        private const int W = 96, H = 64;

        // Fewer iterations than the standalone app's 400. That number was chosen for a dedicated
        // process; in a suite that runs on every build, 120 still exercises many cycles of
        // allocate-and-release while keeping the whole run near a second. If a leak or a
        // use-after-free needs more than 120 frames to show, it will also show in the app.
        private const int Iterations = 120;

        /// <summary>Uses EVERY render path, so per-frame resource churn is actually exercised.</summary>
        private static SceneVisual Scene()
        {
            var root = new SceneVisual();

            var solid = new SceneVisual();
            solid.Content.Add(new GeometryFill(new RectangleGeometry(new Rect(2, 2, 12, 12)),
                RgbaColor.FromBytes(200, 30, 30, 255)));
            root.Children.Add(solid);

            var gradient = new SceneVisual();
            gradient.Content.Add(new GeometryFill(
                new RectangleGeometry(new Rect(18, 2, 30, 12)),
                new LinearGradientBrush(new Vector2(18, 2), new Vector2(48, 2), new[]
                {
                    new GradientStop(0f, RgbaColor.FromBytes(255, 0, 0, 255)),
                    new GradientStop(1f, RgbaColor.FromBytes(0, 0, 255, 255)),
                })));
            root.Children.Add(gradient);

            byte[] checker = { 255, 0, 0, 255, 0, 255, 0, 255, 0, 0, 255, 255, 255, 255, 0, 255 };
            var image = new SceneVisual();
            image.Content.Add(new GeometryFill(new RectangleGeometry(new Rect(52, 2, 12, 12)),
                new ImageBrush(checker, 2, 2)));
            root.Children.Add(image);

            var triFigure = new PathFigure(new Vector2(0, 16));
            triFigure.Segments.Add(new LineSegment(new Vector2(16, 16)));
            triFigure.Segments.Add(new LineSegment(new Vector2(16, 0)));
            var path = new SceneVisual { Offset = new Vector2(70, 20) };
            path.Content.Add(new GeometryFill(
                new PathGeometry(FillRule.NonZero, new List<PathFigure> { triFigure }),
                RgbaColor.FromBytes(20, 120, 200, 255)));
            root.Children.Add(path);

            var text = new SceneVisual();
            text.Content.Add(new GlyphRunDraw("WPF", new Vector2(4, 56), 21f, RgbaColor.FromBytes(0, 0, 0, 255)));
            root.Children.Add(text);

            return root;
        }

        /// <summary>
        /// Repeated rendering through ONE renderer must stay byte-identical. A per-frame resource
        /// released too early, or reused after release, shows up as a frame that diverges from the
        /// first -- usually intermittently, which is why every iteration is compared rather than just
        /// the last.
        /// </summary>
        [Fact]
        public void RepeatedRendering_StaysByteIdentical()
        {
            SceneVisual scene = Scene();
            using WgpuSceneRenderer renderer = NewRenderer();

            byte[] reference = renderer.RenderToRgba(scene, W, H, White);

            int diverged = 0, firstBad = -1;
            for (int i = 1; i < Iterations; i++)
            {
                byte[] frame = renderer.RenderToRgba(scene, W, H, White);
                if (!Same(frame, reference)) { diverged++; if (firstBad < 0) firstBad = i; }
            }

            Assert.True(diverged == 0,
                $"{diverged} of {Iterations} iterations diverged from frame 0 (first at iteration {firstBad}); " +
                "transient GPU resources are not being released safely");
        }

        /// <summary>
        /// The glyph atlas must be uploaded ONCE for the whole run. It is the long-lived cache, so an
        /// upload per frame is a silent performance regression that no pixel comparison can see --
        /// the output is identical either way.
        /// </summary>
        [Fact]
        public void GlyphAtlas_IsUploadedOnlyOnce()
        {
            SceneVisual scene = Scene();
            using WgpuSceneRenderer renderer = NewRenderer();

            for (int i = 0; i < Iterations; i++) renderer.RenderToRgba(scene, W, H, White);

            Assert.True(renderer.AtlasUploads == 1,
                $"the glyph atlas was uploaded {renderer.AtlasUploads} times across {Iterations} renders; " +
                "it should be cached and uploaded exactly once");
        }

        private static bool Same(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }
    }

    /// <summary>
    /// Ported from WgpuInterop.DeterminismProbe. Repeated renders through SEPARATE contexts must
    /// produce identical pixels.
    ///
    /// Distinct from StabilityTests: that one reuses a renderer and catches lifecycle bugs, this one
    /// tears the device down between runs and catches state that leaks in from outside the scene --
    /// uninitialised buffers, hash iteration order, anything that varies per device.
    /// </summary>
    public sealed class DeterminismTests : RendererTestBase
    {
        public DeterminismTests(GpuFixture gpu) : base(gpu) { }

        private const int W = 240, H = 180;

        private static SceneVisual Scene()
        {
            var root = new SceneVisual();
            root.Content.Add(new GeometryFill(new EllipseGeometry(new Vector2(60, 60), 40, 30),
                RgbaColor.FromBytes(200, 60, 60, 255)));
            root.Content.Add(new GeometryFill(new RoundedRectangleGeometry(new Rect(110, 20, 100, 70), 14, 14),
                RgbaColor.FromBytes(60, 120, 200, 200)));
            var stops = new[]
            {
                new GradientStop(0f, RgbaColor.FromBytes(255, 240, 0, 255)),
                new GradientStop(1f, RgbaColor.FromBytes(0, 160, 90, 255)),
            };
            root.Content.Add(new GeometryFill(new RectangleGeometry(new Rect(20, 110, 190, 50)),
                new LinearGradientBrush(new Vector2(20, 110), new Vector2(210, 160), stops, GradientSpreadMethod.Pad)));
            return root;
        }

        [Fact]
        public void RepeatedRenders_AreHashIdentical()
        {
            string? first = null;
            for (int i = 0; i < 4; i++)
            {
                byte[] px = NewRenderer().RenderToRgba(Scene(), W, H, White);
                string hash = Convert.ToHexString(SHA256.HashData(px))[..16];
                first ??= hash;
                Assert.True(hash == first,
                    $"render {i} hashed {hash} but render 0 hashed {first}: the renderer is not deterministic");
            }
        }
    }
}
