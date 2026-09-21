// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Ported from WgpuInterop.BitmapCacheTest.
//
// CacheMode="BitmapCache" -- decoded, and DELIBERATELY NOT ACTED ON.
//
// MILCMD_VISUAL_SETCACHEMODE (0x1e) and MILCMD_BITMAPCACHE (0x8d) decode onto
// SceneVisual.BitmapCached, but the renderer does not force such a subtree through the layer path.
// That was implemented and then reverted on the strength of what this test measures: on repeat
// frames the UNCACHED path already issues zero bind groups, because the per-shape coverage-mask
// cache has already removed the work BitmapCache exists to avoid. Adding a layer only adds a
// composite, and round-tripping through an 8-bit premultiplied texture shifts anti-aliased pixels.
//
// So this asserts what is actually TRUE -- the flag decodes, and setting it changes nothing -- and
// the numbers behind the decision stay in the assertion messages. A test that instead asserted
// "BitmapCache makes things faster" would be asserting a design that was measured and rejected.
//
// If someone later implements the layer path deliberately, THESE TESTS SHOULD FAIL. That is the
// point: the failure is the prompt to re-measure rather than a bug.
//

using System;
using System.Numerics;
using Microsoft.Wpf.Interop.WebGpu.Composition;
using WgpuInterop.Tests.Harness;
using Xunit;

namespace WgpuInterop.Tests.Lifecycle
{
    public sealed class BitmapCacheTests : RendererTestBase
    {
        public BitmapCacheTests(GpuFixture gpu) : base(gpu) { }

        private const int W = 200, H = 200;
        private const int Drawables = 6;
        private const int Frames = 20;

        /// <summary>A subtree of several curved drawables -- the case BitmapCache exists for.</summary>
        private static SceneVisual Scene(bool cached)
        {
            var root = new SceneVisual();
            var card = new SceneVisual { BitmapCached = cached };
            for (int i = 0; i < Drawables; i++)
            {
                float x = 12 + (i % 12) * 15, y = 12 + (i / 12) * 15;
                card.Content.Add(new GeometryFill(new EllipseGeometry(new Vector2(x, y), 22, 16),
                    RgbaColor.FromBytes((byte)(40 + i * 30), 90, 180, 255)));
                card.Content.Add(new GeometryFill(
                    new RoundedRectangleGeometry(new Rect(x - 20, y + 22, 40, 14), 6, 6),
                    RgbaColor.FromBytes(200, (byte)(60 + i * 20), 40, 255)));
            }
            root.Children.Add(card);
            return root;
        }

        private readonly record struct Counters(int BindGroups, int LayerHits);

        /// <summary>
        /// Render the SAME scene instance repeatedly and report the steady-state per-frame cost.
        ///
        /// The same instance matters: a freshly built tree each frame would defeat any content hash
        /// and would measure re-creation rather than caching.
        /// </summary>
        private (byte[] Pixels, Counters Cost) RenderRepeated(bool cached)
        {
            WgpuSceneRenderer renderer = NewRenderer();
            SceneVisual scene = Scene(cached);
            byte[] px = renderer.RenderToRgba(scene, W, H, White);   // warm

            int bindGroups = 0, hits = 0;
            for (int f = 0; f < Frames; f++)
            {
                WgpuSceneRenderer.PerfReset();
                px = renderer.RenderToRgba(scene, W, H, White);
                bindGroups += WgpuSceneRenderer.PerfBindGroups;
                hits += WgpuSceneRenderer.PerfLayerHits;
            }
            return (px, new Counters(bindGroups / Frames, hits / Frames));
        }

        /// <summary>
        /// The finding that made BitmapCache unnecessary: repeat frames already cost NOTHING without
        /// it. If this ever becomes non-zero, the coverage-mask cache has regressed and the
        /// BitmapCache decision genuinely deserves revisiting.
        /// </summary>
        [Fact]
        public void RepeatFrames_AlreadyCostNoBindGroups_WithoutBitmapCache()
        {
            (_, Counters plain) = RenderRepeated(cached: false);
            Assert.True(plain.BindGroups == 0,
                $"an uncached repeat frame issued {plain.BindGroups} bind groups; it used to be 0, which is " +
                "the measurement BitmapCache was rejected on. The coverage-mask cache may have regressed.");
        }

        /// <summary>
        /// Setting the flag must change NOTHING visually, because the renderer does not act on it.
        /// Round-tripping a subtree through an 8-bit premultiplied layer texture shifts anti-aliased
        /// pixels, so any difference here means the layer path went live.
        /// </summary>
        [Fact]
        public void SettingCacheMode_ChangesNothingVisually()
        {
            (byte[] plainPx, _) = RenderRepeated(cached: false);
            (byte[] cachedPx, _) = RenderRepeated(cached: true);

            int differing = 0, maxDelta = 0;
            for (int i = 0; i < W * H; i++)
                for (int c = 0; c < 3; c++)
                {
                    int d = Math.Abs(plainPx[i * 4 + c] - cachedPx[i * 4 + c]);
                    maxDelta = Math.Max(maxDelta, d);
                    if (d > 2) { differing++; break; }
                }

            Assert.True(differing == 0,
                $"{differing} pixels changed (max delta {maxDelta}) when CacheMode was set. The flag is " +
                "supposed to be inert; if the layer path was implemented on purpose, re-measure the cost " +
                "table and update this test rather than deleting it.");
        }
    }
}
