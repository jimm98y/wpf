// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// One WebGPU device for the whole run, plus the scene-building helpers every renderer test needs.
//
// The standalone apps each created their own WgpuContext because each was its own process. In one
// test host that would mean ~57 device creations, which dominates the run and, on some drivers,
// exhausts adapters. The device is shared through an xunit collection fixture instead, and the
// suite runs single-threaded (see AssemblyInfo.cs) because the renderer and the device are not
// safe to drive concurrently.
//
// A fresh WgpuSceneRenderer IS created per scene: it owns caches (mask, atlas, bind groups) whose
// whole purpose is to persist across frames, so sharing one between unrelated tests would let a
// cache hit from one test satisfy another and hide a regression.
//

using System;
using Microsoft.Wpf.Interop.WebGpu.Composition;
using Microsoft.Wpf.Interop.WebGpu.Composition.Protocol;
using Microsoft.Wpf.Interop.WebGpu.Composition.Text;
using Xunit;

namespace WgpuInterop.Tests.Harness
{
    /// <summary>The shared device. Null when the machine has no usable adapter.</summary>
    public sealed class GpuFixture : IDisposable
    {
        internal WgpuContext? Context { get; }

        public GpuFixture()
        {
            if (Requirements.GpuUnavailable is null)
            {
                try { Context = WgpuContext.Create(); } catch { Context = null; }
            }
        }

        public void Dispose() => Context?.Dispose();
    }

    [CollectionDefinition(Name)]
    public sealed class GpuCollection : ICollectionFixture<GpuFixture>
    {
        public const string Name = "gpu";
    }

    /// <summary>
    /// Base class for renderer tests: gates on GPU availability and provides the
    /// build-a-scene-and-render-it helpers the ported apps all needed.
    /// </summary>
    [Collection(GpuCollection.Name)]
    public abstract class RendererTestBase
    {
        private readonly GpuFixture _gpu;

        protected RendererTestBase(GpuFixture gpu) => _gpu = gpu;

        internal WgpuContext Gpu
        {
            get
            {
                Requirements.RequireGpu();
                Assert.SkipWhen(_gpu.Context is null, "shared WebGPU device could not be created");
                return _gpu.Context!;
            }
        }

        /// <summary>A renderer over the shared device, with its own caches.</summary>
        internal WgpuSceneRenderer NewRenderer() => new(Gpu);

        internal WgpuSceneRenderer NewRenderer(IFont font) => new(Gpu, font);

        /// <summary>
        /// Realize a render-data blob as the content of visual <paramref name="hVisual"/> and return
        /// the resulting scene root. This is the shape almost every ported test used.
        /// </summary>
        internal static SceneVisual Realize(MilcoreEngine engine, byte[] content,
            uint hVisual = 1, uint hContent = 2)
        {
            engine.CreateOrAddRef(hContent, MilResourceTypeId.RenderData);
            engine.BeginCommand(MilCmd.RenderDataHeader(hContent, content.Length));
            engine.AppendCommandData(content);
            engine.EndCommand();
            engine.SubmitCommand(MilCmd.VisualSetContent(hVisual, hContent));
            engine.Realize();
            SceneVisual? root = engine.VisualByHandle(hVisual);
            Assert.NotNull(root);
            return root!;
        }

        /// <summary>Realize a render-data blob and render it, returning RGBA8 pixels.</summary>
        internal byte[] RenderContent(MilcoreEngine engine, byte[] content, int w, int h,
            RgbaColor? background = null, bool srgbOutput = false,
            uint hVisual = 1, uint hContent = 2)
        {
            SceneVisual root = Realize(engine, content, hVisual, hContent);
            return NewRenderer().RenderToRgba(root, w, h,
                background ?? RgbaColor.FromBytes(255, 255, 255, 255), srgbOutput);
        }

        internal static RgbaColor White => RgbaColor.FromBytes(255, 255, 255, 255);
        internal static RgbaColor Black => RgbaColor.FromBytes(0, 0, 0, 255);

        /// <summary>Render a scene directly (no MIL protocol) to RGBA8.</summary>
        internal byte[] Render(SceneVisual root, int w, int h, RgbaColor? background = null)
            => NewRenderer().RenderToRgba(root, w, h, background ?? White);

        /// <summary>
        /// Assert that SOME pixel carries partial coverage, i.e. the edge is anti-aliased rather
        /// than a hard binary mask. Deliberately a whole-image existence check: which pixel lands on
        /// an edge depends on the geometry, and pinning one would make the test about the shape
        /// rather than about anti-aliasing being on at all.
        /// </summary>
        internal static void AssertAntiAliased(byte[] px, string what, int lo = 30, int hi = 225)
        {
            for (int i = 0; i < px.Length; i += 4)
            {
                int v = px[i];
                if (v > lo && v < hi) return;
            }
            Assert.Fail($"{what}: no pixel has partial coverage (nothing strictly between {lo} and {hi}), so the edge is aliased");
        }

        /// <summary>
        /// Render the scene, then encode it through the DUCE command protocol, decode it and render
        /// again: the two must be BYTE-identical.
        ///
        /// Every ported test carried this check, and it is worth keeping on all of them rather than
        /// factoring down to one: the protocol is per-primitive, so "an ellipse survives" says
        /// nothing about whether a rounded rectangle's corner radii or a pen's dash array do. The
        /// failures it catches are marshalling slips, which move or drop geometry rather than
        /// discolouring it -- invisible to a tolerance-based pixel check.
        /// </summary>
        internal void AssertProtocolRoundTripIsIdentical(SceneVisual root, int w, int h, RgbaColor? background = null)
        {
            RgbaColor bg = background ?? White;
            WgpuSceneRenderer renderer = NewRenderer();
            byte[] direct = renderer.RenderToRgba(root, w, h, bg);

            byte[] batch = CompositionChannel.EncodeScene(root);
            var engine = new CompositionEngine();
            engine.ProcessBatch(batch);
            SceneVisual? rebuilt = engine.Root;
            Assert.True(rebuilt is not null, "protocol round-trip produced no root visual");

            byte[] viaProtocol = renderer.RenderToRgba(rebuilt!, w, h, bg);
            int diffs = 0;
            for (int i = 0; i < direct.Length; i++) if (direct[i] != viaProtocol[i]) diffs++;
            Assert.True(diffs == 0,
                $"{diffs} of {direct.Length} bytes differ after a {batch.Length}-byte protocol round-trip");
        }
    }
}
